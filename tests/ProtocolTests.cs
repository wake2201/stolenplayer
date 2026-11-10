// Offline protocol tests: real LiteNetLib UDP over 127.0.0.1, no game involved. Exit code 0 = all passed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TS2MP.Shared;

static class ProtocolTests
{
    static string _logDir;
    static int _failed, _passed, _port = 37100;

    static int Main()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ts2mp-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDir);
        Run("handshake succeeds", HandshakeOk);
        Run("wrong session rejected (fatal, no retry)", () => RejectCase(h => h.SessionId = "other", RejectCode.SessionMismatch));
        Run("protocol version mismatch rejected", () => RejectCase(h => h.ProtocolVersion = 999, RejectCode.ProtocolMismatch));
        Run("mod version mismatch rejected", () => RejectCase(h => h.ModVersion = "9.9.9", RejectCode.ModVersionMismatch));
        Run("game version mismatch rejected", () => RejectCase(h => h.GameVersion = "0.0", RejectCode.GameVersionMismatch));
        Run("bad magic rejected", () => RejectCase(h => h.Magic = "XXXX", RejectCode.BadMagic));
        Run("duplicate instance id rejected", () => RejectCase(h => h.InstanceId = "host01", RejectCode.DuplicateInstance));
        Run("third peer rejected as SessionFull while slot taken", SessionFull);
        Run("client retries until host appears", ClientBeforeHost);
        Run("host shutdown -> client detects and reconnects to restarted host", HostRestart);
        Run("client shutdown -> host returns to Listening and accepts new client", ClientRestart);
        Run("port already in use -> host start fails cleanly", PortInUse);
        Run("M2: PlayerState (sequenced) + SceneInfo (reliable) exchanged both ways after handshake", StateExchange);
        Run("M2: v1 client (protocol 1) rejected by v2 host", () => RejectCase(h => h.ProtocolVersion = 1, RejectCode.ProtocolMismatch));
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed. Logs: " + _logDir);
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }

    static InstanceLog Log(string name, string role, string inst) { return new InstanceLog(Path.Combine(_logDir, name + ".log"), role, inst, "s1"); }

    static SessionNode Host(string tag, int port)
    {
        var n = new SessionNode(Role.Host, "s1", "host01", "1.27c", port, Log(tag + "-host", "HOST", "host01"));
        n.ReconnectDelayMs = 200;
        return n;
    }

    static SessionNode Client(string tag, int port, string inst)
    {
        var n = new SessionNode(Role.Client, "s1", inst, "1.27c", port, Log(tag + "-" + inst, "CLIENT", inst));
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
        throw new Exception("timeout waiting for: " + what + " | states: " + string.Join(", ", Array.ConvertAll(nodes, n => n.Role + "=" + n.State)));
    }

    static void Expect(bool cond, string msg) { if (!cond) throw new Exception(msg); }

    static void HandshakeOk()
    {
        int port = _port++;
        using (var h = Host("ok", port)) using (var c = Client("ok", port, "cli01"))
        {
            Expect(h.Start(), "host start");
            Expect(h.State == ConnState.Listening, "host listening");
            Expect(c.Start(), "client start");
            Pump(() => h.State == ConnState.Connected && c.State == ConnState.Connected, 5000, "both connected", h, c);
            Expect(h.RemoteInstanceId == "cli01", "host sees client id");
            Expect(c.RemoteInstanceId == "host01", "client sees host id");
        }
    }

    static void RejectCase(Action<Hello> tamper, RejectCode expected)
    {
        int port = _port++;
        using (var h = Host("rej" + expected, port)) using (var c = Client("rej" + expected, port, "cli01"))
        {
            var hello = new Hello { GameVersion = "1.27c", SessionId = "s1", InstanceId = "cli01" };
            tamper(hello);
            c.HelloOverride = hello;
            h.Start(); c.Start();
            Pump(() => c.State == ConnState.Failed, 5000, "client failed", h, c);
            Expect(c.LastReject == expected, "reject code " + c.LastReject + " != " + expected);
            int attempts = c.ConnectAttempts;
            PumpFor(800, h, c); // must stay Failed without retrying
            Expect(c.State == ConnState.Failed && c.ConnectAttempts == attempts, "client retried after fatal reject");
        }
    }

    static void PumpFor(int ms, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); Thread.Sleep(5); }
    }

    static void SessionFull()
    {
        int port = _port++;
        using (var h = Host("full", port)) using (var c1 = Client("full", port, "cli01")) using (var c2 = Client("full", port, "cli02"))
        {
            h.Start(); c1.Start();
            Pump(() => c1.State == ConnState.Connected, 5000, "first client connected", h, c1);
            c2.Start();
            Pump(() => c2.LastReject == RejectCode.SessionFull, 5000, "second client rejected SessionFull", h, c1, c2);
            Expect(c1.State == ConnState.Connected, "first client unaffected");
            Expect(c2.State != ConnState.Connected, "second client not connected");
        }
    }

    static void ClientBeforeHost()
    {
        int port = _port++;
        using (var c = Client("early", port, "cli01"))
        {
            c.Start();
            Pump(() => c.ConnectAttempts >= 2, 15000, "client retried", c);
            using (var h = Host("early", port))
            {
                h.Start();
                Pump(() => c.State == ConnState.Connected, 15000, "client connected after host start", h, c);
            }
        }
    }

    static void HostRestart()
    {
        int port = _port++;
        using (var c = Client("hrestart", port, "cli01"))
        {
            var h = Host("hrestart-a", port);
            h.Start(); c.Start();
            Pump(() => c.State == ConnState.Connected, 5000, "connected", h, c);
            h.Dispose();
            Pump(() => c.State == ConnState.WaitingRetry || c.State == ConnState.Connecting, 5000, "client noticed host shutdown", c);
            Expect(c.LastDisconnect == LiteNetLib.DisconnectReason.RemoteConnectionClose, "reason " + c.LastDisconnect);
            using (var h2 = Host("hrestart-b", port))
            {
                h2.Start();
                Pump(() => c.State == ConnState.Connected && h2.State == ConnState.Connected, 15000, "reconnected", h2, c);
                Expect(c.HandshakesCompleted == 2, "handshakes " + c.HandshakesCompleted);
            }
        }
    }

    static void ClientRestart()
    {
        int port = _port++;
        using (var h = Host("crestart", port))
        {
            h.Start();
            var c = Client("crestart", port, "cli01");
            c.Start();
            Pump(() => h.State == ConnState.Connected, 5000, "connected", h, c);
            c.Dispose();
            Pump(() => h.State == ConnState.Listening, 5000, "host back to listening", h);
            using (var c2 = Client("crestart", port, "cli02"))
            {
                c2.Start();
                Pump(() => h.State == ConnState.Connected && c2.State == ConnState.Connected, 5000, "new client connected", h, c2);
                Expect(h.RemoteInstanceId == "cli02", "host sees new client");
            }
        }
    }

    static void StateExchange()
    {
        int port = _port++;
        using (var h = Host("state", port)) using (var c = Client("state", port, "cli01"))
        {
            var gotHost = new List<PlayerSnapshot>(); var gotClient = new List<PlayerSnapshot>(); string sceneAtHost = null;
            h.MessageReceived += (peer, type, r) =>
            {
                PlayerSnapshot s; SceneInfo si;
                if (type == (byte)MsgType.PlayerState && PlayerStateCodec.TryRead(r, out s)) gotHost.Add(s);
                else if (type == (byte)MsgType.SceneInfo && SceneInfo.TryRead(r, out si)) sceneAtHost = si.SceneKey;
            };
            c.MessageReceived += (peer, type, r) => { PlayerSnapshot s; if (type == (byte)MsgType.PlayerState && PlayerStateCodec.TryRead(r, out s)) gotClient.Add(s); };
            h.Start(); c.Start();
            Pump(() => h.IsConnected && c.IsConnected, 5000, "connected", h, c);
            var w = new LiteNetLib.Utils.NetDataWriter();
            new SceneInfo { Slot = 2, SceneKey = "MainScene", SceneHash = SceneKeys.Hash("MainScene"), Present = true }.Write(w);
            c.Send(w, LiteNetLib.DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable);
            for (uint i = 1; i <= 20; i++)
            {
                w.Reset(); PlayerStateCodec.Write(w, new PlayerSnapshot { Slot = 2, Seq = i, TimeMs = i * 50, Pos = new Vec3(i, 0, 0), Flags = MoveFlags.Present });
                c.Send(w, LiteNetLib.DeliveryMethod.Sequenced, SessionNode.ChannelState);
                w.Reset(); PlayerStateCodec.Write(w, new PlayerSnapshot { Slot = 1, Seq = i, TimeMs = i * 50, Pos = new Vec3(-i, 0, 0), Flags = MoveFlags.Present });
                h.Send(w, LiteNetLib.DeliveryMethod.Sequenced, SessionNode.ChannelState);
                PumpFor(10, h, c);
            }
            Pump(() => sceneAtHost != null && gotHost.Count > 0 && gotClient.Count > 0 && gotHost[gotHost.Count - 1].Seq == 20 && gotClient[gotClient.Count - 1].Seq == 20, 3000, "snapshots received", h, c);
            Expect(sceneAtHost == "MainScene", "scene info");
            for (int i = 1; i < gotHost.Count; i++) Expect(gotHost[i].Seq > gotHost[i - 1].Seq, "sequenced delivery out of order");
            Expect(gotHost[gotHost.Count - 1].Pos.X == 20f && gotClient[gotClient.Count - 1].Pos.X == -20f, "payload");
        }
    }

    static void PortInUse()
    {
        int port = _port++;
        using (var blocker = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port)))
        using (var h = Host("portbusy", port))
        {
            Expect(!h.Start(), "start should fail");
            Expect(h.State == ConnState.Failed, "state Failed");
        }
    }
}
