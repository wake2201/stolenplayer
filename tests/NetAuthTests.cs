// N1 offline tests: connection-setting validation, join codes, abuse limits and the join-code handshake over real
// LiteNetLib UDP on this PC (127.0.0.1 / 0.0.0.0). No game involved, nothing leaves the machine. Exit code 0 = all passed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class NetAuthTests
{
    static string _logDir;
    static int _failed, _passed, _port = 37400;
    const string Game = "1.27c";

    static int Main()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ts2mp-n1-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDir);
        Run("01 config: ports, bind and host addresses validated", ConfigValidation);
        Run("02 join code: 115 random bits, format, normalisation, typo detection, key derivation", JoinCodes);
        Run("03 limiter: request window, per-address block after failures, expiry, global lock, bounded table", Limiter);
        Run("04 binding: network bind without join code refused; 0.0.0.0 with code reachable via loopback", Binding);
        Run("05 loopback connect with join code: mutual proof, gameplay messages flow", AuthOk);
        Run("06 wrong join code: fatal AuthFailed, no retry, host keeps listening and accepts the right code", WrongCode);
        Run("07 client without code at an authenticating host: AUTH_REQUIRED, no retry", NoCodeClient);
        Run("08 client with code at a host without code: host not authenticated, no retry", UnauthenticatedHost);
        Run("09 replay: captured proof fails against a new challenge; reused client nonce rejected", Replay);
        Run("10 bounded attempts: address blocked (RateLimited, retryable) after 5 wrong codes", RateLimit);
        Run("11 size limit: oversized connection request rejected before parsing", Oversized);
        Run("12 version mismatch is reported (protocol 20 client, other mod, other game) with auth on", VersionMismatch);
        Run("13 full session: one remote only, pending peer holds the slot until timeout, no messages before auth", FullSession);
        Run("14 port conflict on 0.0.0.0: host start fails cleanly", PortConflict);
        Run("15 reconnect: client restart re-authenticates; host restart with a new code refuses the old code", Reconnect);
        Run("16 DNS name target (localhost) resolved asynchronously and connected", DnsTarget);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed. Logs: " + _logDir);
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }

    static void Expect(bool cond, string msg) { if (!cond) throw new Exception(msg); }

    static string ReadLog(string name)
    {
        using (var fs = new FileStream(Path.Combine(_logDir, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var r = new StreamReader(fs)) return r.ReadToEnd();
    }

    static InstanceLog Log(string name, string role, string inst) { return new InstanceLog(Path.Combine(_logDir, name + ".log"), role, inst, "s1"); }

    static SessionNode Host(string tag, int port, string code, string bind = "127.0.0.1")
    {
        var n = new SessionNode(Role.Host, "hs" + tag, "host01", Game, port, Log(tag + "-host", "HOST", "host01"));
        n.BindAddress = bind;
        n.JoinKey = code != null ? JoinAuth.DeriveKey(JoinAuth.Normalize(code)) : null;
        n.AuthTimeoutMs = 1500;
        return n;
    }

    static SessionNode Client(string tag, int port, string inst, string code, string target = "127.0.0.1")
    {
        // A remote joiner does not know the host's random session id: it sends "join".
        var n = new SessionNode(Role.Client, "join", inst, Game, port, Log(tag + "-" + inst, "CLIENT", inst));
        n.ConnectAddress = target;
        n.JoinKey = code != null ? JoinAuth.DeriveKey(JoinAuth.Normalize(code)) : null;
        n.ReconnectDelayMs = 200;
        n.DisconnectTimeoutMs = 2000;
        return n;
    }

    static void Pump(Func<bool> until, int timeoutMs, string what, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            foreach (var n in nodes) n.Tick();
            if (until()) return;
            Thread.Sleep(5);
        }
        throw new Exception("timeout waiting for: " + what + " | states: " + string.Join(", ", Array.ConvertAll(nodes, n => n.Role + "=" + n.State + (n.LastReject.HasValue ? "/" + n.LastReject : ""))));
    }

    static void PumpFor(int ms, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); Thread.Sleep(5); }
    }

    // ---- 01 ----
    static void ConfigValidation()
    {
        int p;
        Expect(NetConfig.TryParsePort("27960", out p) && p == 27960, "default port");
        Expect(!NetConfig.TryParsePort("80", out p) && !NetConfig.TryParsePort("70000", out p) && !NetConfig.TryParsePort("-1", out p)
            && !NetConfig.TryParsePort("27960x", out p) && !NetConfig.TryParsePort("", out p) && !NetConfig.TryParsePort(null, out p) && !NetConfig.TryParsePort("+2796", out p), "bad ports refused");
        Expect(NetConfig.ValidBindAddress("0.0.0.0") && NetConfig.ValidBindAddress("127.0.0.1") && NetConfig.ValidBindAddress("192.168.178.20"), "bind literals");
        Expect(!NetConfig.ValidBindAddress("localhost") && !NetConfig.ValidBindAddress("::") && !NetConfig.ValidBindAddress("1.2.3") && !NetConfig.ValidBindAddress("224.0.0.1")
            && !NetConfig.ValidBindAddress("255.255.255.255") && !NetConfig.ValidBindAddress("010.1.1.1") && !NetConfig.ValidBindAddress(""), "bad binds refused");
        Expect(NetConfig.ValidConnectAddress("203.0.113.7") && NetConfig.ValidConnectAddress("myhost.dyndns.org") && NetConfig.ValidConnectAddress("localhost")
            && NetConfig.ValidConnectAddress("my-pc") && NetConfig.ValidConnectAddress("example.com."), "targets");
        Expect(!NetConfig.ValidConnectAddress("0.0.0.0") && !NetConfig.ValidConnectAddress("999.1.1.1") && !NetConfig.ValidConnectAddress("1.2.3")
            && !NetConfig.ValidConnectAddress("host:27960") && !NetConfig.ValidConnectAddress("-bad.com") && !NetConfig.ValidConnectAddress("a..b")
            && !NetConfig.ValidConnectAddress("has space") && !NetConfig.ValidConnectAddress("") && !NetConfig.ValidConnectAddress(new string('a', 254))
            && !NetConfig.ValidConnectAddress("239.1.1.1") && !NetConfig.ValidConnectAddress("ex_ample.com"), "bad targets refused");
        Expect(NetConfig.IsLoopback("127.0.0.1") && NetConfig.IsLoopback("127.5.5.5") && !NetConfig.IsLoopback("0.0.0.0") && !NetConfig.IsLoopback("localhost"), "loopback detection");

        // a SessionNode with an invalid address refuses to start
        using (var h = Host("cfg", _port++, null, "not-an-ip")) Expect(!h.Start() && h.State == ConnState.Failed, "host with invalid bind refuses");
        using (var c = Client("cfg", _port++, "c1", null, "999.1.1.1")) Expect(!c.Start() && c.State == ConnState.Failed, "client with invalid target refuses");
    }

    // ---- 02 ----
    static void JoinCodes()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 2000; i++)
        {
            string c = JoinAuth.NewCode();
            Expect(c.Length == 25 && JoinAuth.Normalize(c) == c, "fresh code valid: " + c);
            Expect(seen.Add(c), "codes unique");
        }
        string code = JoinAuth.NewCode();
        string shown = JoinAuth.Format(code);
        Expect(shown.Length == 29 && shown[5] == '-' && shown[23] == '-', "display format XXXXX-...");
        Expect(JoinAuth.Normalize(shown) == code && JoinAuth.Normalize(" " + shown.ToLowerInvariant().Replace("-", " ") + " ") == code, "dashes, spaces, lower case accepted");
        string withO = code.Replace('0', 'O').Replace('1', 'I');
        Expect(JoinAuth.Normalize(withO) == code, "O->0, I->1 accepted");
        // every single-character typo is detected by the 2 check characters with probability 1 - 1/1024; for the
        // first 23 positions test all substitutions and require that almost all are caught
        int caught = 0, total = 0;
        const string A = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (int i = 0; i < 25; i++)
            foreach (char ch in A)
            {
                if (ch == code[i]) continue;
                total++;
                var t = code.Substring(0, i) + ch + code.Substring(i + 1);
                if (JoinAuth.Normalize(t) == null) caught++;
            }
        Expect(caught >= total - 3, "typos detected: " + caught + "/" + total);
        Expect(JoinAuth.Normalize(code.Substring(1)) == null && JoinAuth.Normalize(code + "0") == null && JoinAuth.Normalize("") == null
            && JoinAuth.Normalize(null) == null && JoinAuth.Normalize(code.Substring(0, 24) + "U") == null, "wrong length / alphabet refused");
        var k1 = JoinAuth.DeriveKey(code);
        Expect(k1.Length == 32 && JoinAuth.FixedTimeEquals(k1, JoinAuth.DeriveKey(JoinAuth.Normalize(shown))), "key deterministic");
        Expect(!JoinAuth.FixedTimeEquals(k1, JoinAuth.DeriveKey(JoinAuth.NewCode())), "different code -> different key");
        var n1 = JoinAuth.NewNonce(); var n2 = JoinAuth.NewNonce();
        var pc = JoinAuth.Proof(k1, false, 21, Game, "", "c", n1, n2);
        var ph = JoinAuth.Proof(k1, true, 21, Game, "", "c", n1, n2);
        Expect(pc.Length == 32 && !JoinAuth.FixedTimeEquals(pc, ph), "client and host proofs domain-separated");
        Expect(!JoinAuth.FixedTimeEquals(pc, JoinAuth.Proof(k1, false, 21, Game, "", "c", n2, n1)), "nonce order bound");
        Expect(!JoinAuth.FixedTimeEquals(pc, JoinAuth.Proof(k1, false, 21, Game, "", "d", n1, n2)), "client instance bound");
        Expect(!JoinAuth.FixedTimeEquals(pc, JoinAuth.Proof(k1, false, 20, Game, "", "c", n1, n2)), "protocol bound");
    }

    // ---- 03 ----
    static void Limiter()
    {
        long now = 0;
        var l = new AuthLimiter(() => now);
        string why;
        for (int i = 0; i < 20; i++) Expect(l.AllowRequest("1.1.1.1", out why), "request " + i);
        Expect(!l.AllowRequest("1.1.1.1", out why), "21st request in window refused");
        Expect(l.AllowRequest("2.2.2.2", out why), "other address unaffected");
        now += 10001;
        Expect(l.AllowRequest("1.1.1.1", out why), "new window");
        for (int i = 0; i < 4; i++) l.Failure("3.3.3.3");
        Expect(l.AllowRequest("3.3.3.3", out why), "4 failures: still allowed");
        l.Failure("3.3.3.3");
        Expect(!l.AllowRequest("3.3.3.3", out why) && why.Contains("blocked"), "5th failure blocks");
        now += 119000; Expect(!l.AllowRequest("3.3.3.3", out why), "still blocked");
        now += 2000; Expect(l.AllowRequest("3.3.3.3", out why), "block expires");
        // failures spread beyond the window do not block
        for (int i = 0; i < 6; i++) { l.Failure("4.4.4.4"); now += 20000; }
        Expect(l.AllowRequest("4.4.4.4", out why), "slow failures outside the 60 s window do not block");
        // success clears the failure history
        for (int i = 0; i < 4; i++) l.Failure("5.5.5.5");
        l.Success("5.5.5.5"); l.Failure("5.5.5.5");
        Expect(l.AllowRequest("5.5.5.5", out why), "success resets failures");
        // global lock against distributed guessing
        var g = new AuthLimiter(() => now);
        for (int i = 0; i < 30; i++) g.Failure("10.0." + (i / 200) + "." + (i % 200));
        Expect(g.GlobalLocked && !g.AllowRequest("9.9.9.9", out why), "global lock after 30 failures");
        now += 60001; Expect(!g.GlobalLocked && g.AllowRequest("9.9.9.9", out why), "global lock expires");
        // bounded table
        var b = new AuthLimiter(() => now) { MaxTrackedAddresses = 100 };
        for (int i = 0; i < 1000; i++) { b.AllowRequest("10.1." + (i / 250) + "." + (i % 250), out why); now += 1; }
        Expect(b.TrackedAddresses <= 100, "tracked addresses bounded: " + b.TrackedAddresses);
        // nonce cache
        var nc = new NonceCache(3);
        var a = new byte[] { 1 }; Expect(nc.Add(a) && !nc.Add(a), "nonce reuse detected");
        nc.Add(new byte[] { 2 }); nc.Add(new byte[] { 3 }); nc.Add(new byte[] { 4 });
        Expect(nc.Add(a), "cache bounded (oldest evicted)");
    }

    // ---- 04 ----
    static void Binding()
    {
        int port = _port++;
        using (var h = Host("bindnokey", port, null, "0.0.0.0"))
            Expect(!h.Start() && h.State == ConnState.Failed, "0.0.0.0 without join code refused");
        string code = JoinAuth.NewCode();
        using (var h = Host("bindany", port, code, "0.0.0.0")) using (var c = Client("bindany", port, "c1", code))
        {
            Expect(h.Start() && h.State == ConnState.Listening && h.LocalPort == port, "0.0.0.0 with join code listens on the configured port");
            Expect(c.Start(), "client start");
            Pump(() => h.IsConnected && c.IsConnected, 5000, "connected through the any-address bind", h, c);
        }
        using (var h = Host("bindloop", port, null, "127.0.0.1")) Expect(h.Start(), "loopback host without code allowed (local test mode)");
    }

    // ---- 05 ----
    static void AuthOk()
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        using (var h = Host("ok", port, code)) using (var c = Client("ok", port, "cli01", code))
        {
            var atHost = new List<byte>(); var atClient = new List<byte>();
            h.MessageReceived += (p, t, r) => atHost.Add(t);
            c.MessageReceived += (p, t, r) => atClient.Add(t);
            Expect(h.Start() && c.Start(), "start");
            Pump(() => h.IsConnected && c.IsConnected, 5000, "both connected", h, c);
            Expect(h.AuthSuccesses == 1 && h.AuthFailures == 0, "host counted one success");
            Expect(c.RemoteInstanceId == "host01" && h.RemoteInstanceId == "cli01" && c.RemoteSessionId == "hsok", "ids exchanged, client adopted host session");
            var w = new NetDataWriter(); w.Put((byte)MsgType.SceneInfo); w.Put("x");
            c.Send(w, DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable);
            h.Send(w, DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable);
            Pump(() => atHost.Count == 1 && atClient.Count == 1, 3000, "messages both ways", h, c);
        }
        string log = ReadLog("ok-host.log") + ReadLog("ok-cli01.log");
        Expect(log.Contains("AUTH_OK") && log.Contains("host proved the join code"), "auth logged");
        Expect(!log.Contains(code) && !log.Contains(JoinAuth.Format(code)), "join code never written to the logs");
    }

    // ---- 06 ----
    static void WrongCode()
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        using (var h = Host("wrong", port, code)) using (var bad = Client("wrong", port, "bad01", JoinAuth.NewCode()))
        {
            Expect(h.Start() && bad.Start(), "start");
            Pump(() => bad.State == ConnState.Failed, 5000, "wrong code fails", h, bad);
            Expect(bad.LastReject == RejectCode.AuthFailed, "reject AuthFailed, got " + bad.LastReject);
            int attempts = bad.ConnectAttempts;
            PumpFor(800, h, bad);
            Expect(bad.State == ConnState.Failed && bad.ConnectAttempts == attempts, "no retry after a wrong code");
            Expect(h.State == ConnState.Listening && h.AuthFailures == 1 && !h.IsConnected, "host listening, one failure");
            using (var good = Client("wrong", port, "good01", code))
            {
                good.Start();
                Pump(() => h.IsConnected && good.IsConnected, 5000, "right code connects afterwards", h, good);
            }
        }
    }

    // ---- 07 ----
    static void NoCodeClient()
    {
        int port = _port++;
        using (var h = Host("nocode", port, JoinAuth.NewCode())) using (var c = Client("nocode", port, "c1", null))
        {
            h.Start(); c.Start();
            Pump(() => c.State == ConnState.Failed, 5000, "client without code fails", h, c);
            Expect(c.FatalReason != null && c.FatalReason.StartsWith("AUTH_REQUIRED"), "fatal reason AUTH_REQUIRED: " + c.FatalReason);
            int a = c.ConnectAttempts; PumpFor(600, h, c);
            Expect(c.ConnectAttempts == a && !h.IsConnected, "no retry, not connected");
            Pump(() => h.State == ConnState.Listening, 3000, "host back to listening", h, c);
        }
    }

    // ---- 08 ----
    static void UnauthenticatedHost()
    {
        int port = _port++;
        using (var h = new SessionNode(Role.Host, "join", "host01", Game, port, Log("unauth-host", "HOST", "host01")))
        using (var c = Client("unauth", port, "c1", JoinAuth.NewCode()))
        {
            // a loopback host without a code; the session id matches so only the missing host proof can fail
            h.Start(); c.Start();
            Pump(() => c.State == ConnState.Failed, 5000, "client refuses unauthenticated host", h, c);
            Expect(c.FatalReason != null && c.FatalReason.StartsWith("HOST_NOT_AUTHENTICATED"), "fatal reason: " + c.FatalReason);
            Expect(!c.IsConnected, "never connected");
        }
    }

    // ---- 09 ----
    static void Replay()
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        byte[] captured = null;
        var fixedNonce = JoinAuth.NewNonce();
        using (var h = Host("replay", port, code))
        {
            h.Start();
            using (var c1 = Client("replay", port, "cli01", code))
            {
                c1.HelloOverride = new Hello { GameVersion = Game, SessionId = "join", InstanceId = "cli01", ClientNonce = fixedNonce };
                c1.AuthResponseHook = p => { captured = (byte[])p.Clone(); return p; };
                c1.Start();
                Pump(() => h.IsConnected && c1.IsConnected, 5000, "first session", h, c1);
            }
            Pump(() => h.State == ConnState.Listening, 5000, "host free again", h);
            Expect(captured != null, "proof captured");
            // replay the captured proof (same instance id, fresh nonce from the attacker, new server challenge)
            using (var c2 = Client("replay", port, "cli01", JoinAuth.NewCode()))
            {
                c2.AuthResponseHook = p => captured;
                c2.Start();
                Pump(() => c2.State == ConnState.Failed, 5000, "replayed proof rejected", h, c2);
                Expect(c2.LastReject == RejectCode.AuthFailed && !h.IsConnected, "AuthFailed for replayed proof");
            }
            // replay the complete first hello (same client nonce): refused before any challenge
            using (var c3 = Client("replay", port, "cli01", code))
            {
                c3.HelloOverride = new Hello { GameVersion = Game, SessionId = "join", InstanceId = "cli01", ClientNonce = fixedNonce };
                c3.Start();
                Pump(() => c3.State == ConnState.Failed, 5000, "reused nonce rejected", h, c3);
                Expect(c3.LastReject == RejectCode.AuthFailed, "reused nonce -> AuthFailed, got " + c3.LastReject);
            }
            Expect(h.AuthSuccesses == 1, "only the genuine session authenticated");
        }
        Expect(ReadLog("replay-host.log").Contains("REPLAY"), "replay logged");
    }

    // ---- 10 ----
    static void RateLimit()
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        using (var h = Host("rate", port, code))
        {
            h.Start();
            for (int i = 0; i < 5; i++)
                using (var bad = Client("rate", port, "bad" + i, JoinAuth.NewCode()))
                {
                    bad.Start();
                    Pump(() => bad.State == ConnState.Failed, 5000, "bad attempt " + i, h, bad);
                    Expect(bad.LastReject == RejectCode.AuthFailed, "attempt " + i + " AuthFailed");
                }
            using (var good = Client("rate", port, "good", code))
            {
                good.RateLimitedDelayMs = 60000;
                good.Start();
                Pump(() => good.LastReject == RejectCode.RateLimited, 5000, "blocked address refused even with the right code", h, good);
                Expect(good.State == ConnState.WaitingRetry && good.FatalReason == null, "RateLimited is retryable (later)");
                Expect(!h.IsConnected && h.AuthFailures == 5, "no session, 5 failures");
            }
        }
    }

    // ---- 11 ----
    static void Oversized()
    {
        int port = _port++;
        using (var h = Host("big", port, JoinAuth.NewCode())) using (var c = Client("big", port, "c1", null))
        {
            c.HelloOverride = new Hello { GameVersion = Game, SessionId = "join", InstanceId = new string('x', 400) };
            h.Start(); c.Start();
            Pump(() => c.State == ConnState.Failed, 5000, "oversized rejected", h, c);
            Expect(c.LastReject == RejectCode.Oversized, "Oversized, got " + c.LastReject);
        }
    }

    // ---- 12 ----
    static void VersionMismatch()
    {
        Mismatch(h => h.ProtocolVersion = 20, RejectCode.ProtocolMismatch);
        Mismatch(h => h.ModVersion = "0.11.0", RejectCode.ModVersionMismatch);
        Mismatch(h => h.GameVersion = "1.26", RejectCode.GameVersionMismatch);
    }

    static void Mismatch(Action<Hello> tamper, RejectCode expected)
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        using (var h = Host("ver" + expected, port, code)) using (var c = Client("ver" + expected, port, "c1", code))
        {
            var hello = new Hello { GameVersion = Game, SessionId = "join", InstanceId = "c1" };
            tamper(hello);
            c.HelloOverride = hello;
            h.Start(); c.Start();
            Pump(() => c.State == ConnState.Failed, 5000, "mismatch " + expected, h, c);
            Expect(c.LastReject == expected, expected + " expected, got " + c.LastReject);
        }
    }

    // ---- 13 ----
    static void FullSession()
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        using (var h = Host("full", port, code))
        {
            int delivered = 0;
            h.MessageReceived += (p, t, r) => delivered++;
            h.Start();

            // a raw peer with a valid hello that never answers the challenge, then sends a gameplay message
            var raw = new RawPeer(port);
            Pump(() => raw.Connected, 5000, "raw peer transport connected", h);
            using (var c = Client("full", port, "cli01", code))
            {
                c.Start();
                Pump(() => c.LastReject == RejectCode.SessionFull, 5000, "real client gets SessionFull while the slot is pending", h, c);
                Expect(c.FatalReason == null, "SessionFull is retryable");
                raw.SendGameplay();
                Pump(() => !raw.Connected, 5000, "pre-auth gameplay message ends the pending peer", h, c);
                Expect(delivered == 0, "no gameplay message delivered before authentication");
                Pump(() => h.IsConnected && c.IsConnected, 8000, "real client connects on retry", h, c);

                using (var third = Client("full", port, "cli02", code))
                {
                    third.Start();
                    Pump(() => third.LastReject == RejectCode.SessionFull, 5000, "third peer refused", h, c, third);
                    Expect(h.IsConnected && h.RemoteInstanceId == "cli01", "first client keeps the slot");
                }

                // silent pending peer: times out (AuthTimeoutMs) and counts as a failure
                c.Dispose();
                Pump(() => h.State == ConnState.Listening, 5000, "host free", h);
                var silent = new RawPeer(port);
                Pump(() => silent.Connected, 5000, "silent peer connected", h);
                int fails = h.AuthFailures;
                Pump(() => !silent.Connected && h.AuthFailures == fails + 1, 6000, "silent peer timed out", h);
                silent.Stop();
            }
            raw.Stop();
        }
    }

    // Minimal LiteNetLib client that sends a valid Hello but does not authenticate.
    sealed class RawPeer
    {
        readonly NetManager _net; readonly EventBasedNetListener _l = new EventBasedNetListener(); NetPeer _peer; volatile bool _connected;
        readonly Thread _pump; volatile bool _stop;
        public bool Connected { get { return _connected; } }
        public RawPeer(int port)
        {
            _l.PeerConnectedEvent += p => _connected = true;
            _l.PeerDisconnectedEvent += (p, i) => _connected = false;
            _l.NetworkReceiveEvent += (p, r, ch, m) => { };
            _net = new NetManager(_l) { ChannelsCount = 3, IPv6Enabled = false };
            _net.Start(IPAddress.Loopback, IPAddress.IPv6None, 0);
            var w = new NetDataWriter();
            new Hello { GameVersion = Game, SessionId = "join", InstanceId = "raw" + port, ClientNonce = JoinAuth.NewNonce() }.Write(w);
            _peer = _net.Connect(new IPEndPoint(IPAddress.Loopback, port), w);
            _pump = new Thread(() => { while (!_stop) { _net.PollEvents(); Thread.Sleep(5); } }) { IsBackground = true };
            _pump.Start();
        }
        public void SendGameplay()
        {
            var w = new NetDataWriter(); w.Put((byte)MsgType.SceneInfo); w.Put("evil");
            _peer.Send(w, 0, DeliveryMethod.ReliableOrdered);
        }
        public void Stop() { _stop = true; _pump.Join(1000); _net.Stop(); }
    }

    // ---- 14 ----
    static void PortConflict()
    {
        int port = _port++;
        using (var blocker = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, port)))
        using (var h = Host("conflict", port, JoinAuth.NewCode(), "0.0.0.0"))
        {
            Expect(!h.Start() && h.State == ConnState.Failed, "bind conflict reported as Failed");
            h.Tick();
        }
        using (var h = Host("conflict2", port, JoinAuth.NewCode(), "0.0.0.0"))
            Expect(h.Start() && h.State == ConnState.Listening, "port usable once free");
    }

    // ---- 15 ----
    static void Reconnect()
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        var h = Host("recon-a", port, code);
        h.Start();
        using (var c = Client("recon", port, "cli01", code))
        {
            c.Start();
            Pump(() => h.IsConnected && c.IsConnected, 5000, "first connect", h, c);
        }
        Pump(() => h.State == ConnState.Listening, 5000, "host listening after client left", h);
        using (var c = Client("recon", port, "cli01", code))
        {
            c.Start();
            Pump(() => h.IsConnected && c.IsConnected, 5000, "client restart re-authenticates", h, c);
            Expect(h.AuthSuccesses == 2, "two successful authentications");
            // host goes away: client keeps retrying (transport loss is not fatal)
            h.Dispose();
            Pump(() => c.State == ConnState.WaitingRetry || c.State == ConnState.Connecting, 8000, "client notices host loss", c);
            Expect(c.FatalReason == null, "host loss is retryable");
            // host restarts with a NEW code (new launch): the old code is refused, no endless retries
            using (var h2 = Host("recon-b", port, JoinAuth.NewCode()))
            {
                h2.Start();
                Pump(() => c.State == ConnState.Failed, 10000, "old code refused by the new host session", h2, c);
                Expect(c.LastReject == RejectCode.AuthFailed, "AuthFailed, got " + c.LastReject);
            }
        }
    }

    // ---- 16 ----
    static void DnsTarget()
    {
        int port = _port++;
        string code = JoinAuth.NewCode();
        using (var h = Host("dns", port, code, "0.0.0.0")) using (var c = Client("dns", port, "cli01", code, "localhost"))
        {
            Expect(h.Start() && c.Start(), "start");
            Pump(() => h.IsConnected && c.IsConnected, 8000, "connected via host name", h, c);
        }
        using (var c = Client("dnsbad", _port++, "cli01", JoinAuth.NewCode(), "nonexistent-host.invalid"))
        {
            Expect(c.Start(), "start with unresolvable name (resolution is asynchronous)");
            Pump(() => ReadLog("dnsbad-cli01.log").Contains("DNS_FAILED"), 15000, "DNS failure logged and retried", c);
            Expect(c.FatalReason == null, "unresolvable name is retried, not fatal");
        }
    }
}
