// N1.2 offline tests: F8 menu rules, host readiness, the client join lifecycle (Lifecycle state machine), the host join
// gate + ClientReady step over real LiteNetLib UDP on 127.0.0.1, and the save-isolation path rules. Exit 0 = all passed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class LifecycleTests
{
    static string _logDir;
    static int _failed, _passed, _port = 37900;
    const string Game = "1.27c";

    static int Main()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ts2mp-n12-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDir);
        Run("01 host from the main menu is denied", HostFromMenu);
        Run("02 host from a fully loaded world is accepted; ready only when every readiness fact holds", HostFromWorld);
        Run("03 join from the main menu is accepted (valid address/port/code)", JoinFromMenu);
        Run("04 join from an active solo game is denied with the German explanation", JoinFromWorld);
        Run("05 join before the host is gameplay-ready: HostNotReady, retried, no preparation until ready (UDP)", JoinBeforeReady);
        Run("06 host region change while connected: client stays, new joins deferred (UDP)", HostTransition);
        Run("07 host returning to the main menu ends the session", HostToMenu);
        Run("08 client disconnect/reconnect: one ClientReady per connection, automatic after the first preparation (UDP)", Reconnect);
        Run("09 authentication failure: no preparation, no isolation request, error (UDP)", AuthFailure);
        Run("10 connection timeout: client stays in the main menu with an error, nothing armed", Timeout);
        Run("11 host crash: connected client keeps retrying safely; fatal outcomes end the session (UDP)", HostCrash);
        Run("12 solo play: no session, F8 mode applies no patch before Join (rules + source scan)", SoloUnaffected);
        Run("13 ClientReady protocol: no gameplay before ready, ready timeout, abort = fatal (UDP)", ReadyProtocol);
        Run("14 full client lifecycle: arm -> systems -> ClientReady -> world snapshot -> Continue once -> Connected", FullClientFlow);
        Run("15 isolation failure: error, never synchronizing, Continue never requested", IsolationFailure);
        Run("16 save-isolation path rules (layout, protected writes, effective path)", IsolationRules);
        Run("17 menu availability matrix + legacy/sticky rules + error texts", MenuMatrix);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed. Logs: " + _logDir);
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }

    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    static InstanceLog Log(string n) { return new InstanceLog(Path.Combine(_logDir, n + ".log"), "T", n, "s"); }

    static HostFacts Ready() { return new HostFacts { Context = GameContext.InWorld, LoadedMap = true, PlayerPresent = true, ProfileValid = true, RegionKnown = true, PersistenceReady = true, SystemsReady = true, SecondsSinceSceneLoad = 10 }; }

    static SessionNode Host(string tag, int port, string code, Func<string> gate)
    {
        var n = new SessionNode(Role.Host, "hs", "host01", Game, port, Log(tag + "-host"));
        n.BindAddress = "127.0.0.1"; n.JoinKey = JoinAuth.DeriveKey(code); n.HostGate = gate; n.AuthTimeoutMs = 1500;
        return n;
    }

    static SessionNode Client(string tag, int port, string inst, string code, bool autoReady)
    {
        var n = new SessionNode(Role.Client, "join", inst, Game, port, Log(tag + "-" + inst));
        n.ConnectAddress = "127.0.0.1"; n.JoinKey = JoinAuth.DeriveKey(code); n.AutoReady = autoReady;
        n.ReconnectDelayMs = 200; n.HostNotReadyDelayMs = 300; n.DisconnectTimeoutMs = 2000;
        return n;
    }

    static void Pump(Func<bool> until, int ms, string what, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); if (until()) return; Thread.Sleep(5); }
        throw new Exception("timeout: " + what + " | " + string.Join(", ", Array.ConvertAll(nodes, n => n.Role + "=" + n.State + (n.LastReject.HasValue ? "/" + n.LastReject : ""))));
    }

    static void PumpFor(int ms, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); Thread.Sleep(5); }
    }

    // ---- 01 ----
    static void HostFromMenu()
    {
        var lc = new Lifecycle();
        string r = lc.RequestHost(GameContext.MainMenu, 27960, 0);
        Expect(r == MenuRules.HostFromMenuText && lc.State == MpState.Offline, "denied from the menu: " + r);
        Expect(lc.RequestHost(GameContext.Loading, 27960, 0) != null && lc.State == MpState.Offline, "denied while loading");
        Expect(HostReadiness.Check(new HostFacts { Context = GameContext.MainMenu }) != null, "readiness false in the menu");
    }

    // ---- 02 ----
    static void HostFromWorld()
    {
        var lc = new Lifecycle();
        Expect(lc.RequestHost(GameContext.InWorld, 80, 0) != null, "invalid port refused");
        Expect(lc.RequestHost(GameContext.InWorld, 27960, 0) == null && lc.State == MpState.HostStarting, "accepted in the world");
        lc.HostStarted(true, null, 1);
        Expect(lc.State == MpState.HostWaiting, "waiting until checked");
        var f = Ready();
        Expect(HostReadiness.Check(f) == null, "all facts -> ready");
        foreach (var bad in new Func<HostFacts, HostFacts>[] {
            x => { x.LoadedMap = false; return x; }, x => { x.PlayerPresent = false; return x; }, x => { x.ProfileValid = false; return x; },
            x => { x.RegionKnown = false; return x; }, x => { x.PersistenceReady = false; return x; }, x => { x.SystemsReady = false; return x; },
            x => { x.SecondsSinceSceneLoad = 1; return x; }, x => { x.Context = GameContext.Loading; return x; } })
            Expect(HostReadiness.Check(bad(f)) != null, "a missing fact blocks readiness");
        Expect(!lc.HostTick(GameContext.InWorld, "Ladevorgang läuft", false, 2) && lc.State == MpState.HostWaiting && lc.Status.Contains("pausiert"), "joins paused while not ready");
        Expect(!lc.HostTick(GameContext.InWorld, null, false, 3) && lc.State == MpState.HostReady, "ready");
        Expect(!lc.HostTick(GameContext.InWorld, null, true, 4) && lc.State == MpState.Connected, "client connected");
        var lc2 = new Lifecycle(); lc2.RequestHost(GameContext.InWorld, 27960, 0); lc2.HostStarted(false, "Port belegt", 1);
        Expect(lc2.State == MpState.Error && lc2.Error == "Port belegt", "start failure -> error");
    }

    // ---- 03 ----
    static void JoinFromMenu()
    {
        var lc = new Lifecycle(); JoinRequest req;
        string code = JoinAuth.Format(JoinAuth.NewCode());
        Expect(lc.RequestJoin(GameContext.MainMenu, true, "203.0.113.7", "27960", code, out req, 0) == null && lc.State == MpState.ClientConnecting, "accepted");
        Expect(req.Host == "203.0.113.7" && req.Port == 27960 && req.Code == code.Replace("-", ""), "normalised request");
        var bad = new Lifecycle();
        Expect(bad.RequestJoin(GameContext.MainMenu, true, "999.1.1.1", "27960", code, out req, 0) != null, "bad address");
        Expect(bad.RequestJoin(GameContext.MainMenu, true, "host.example", "80", code, out req, 0) != null, "bad port");
        Expect(bad.RequestJoin(GameContext.MainMenu, true, "host.example", "27960", "ABC", out req, 0) != null && bad.State == MpState.Offline, "bad code, still offline");
    }

    // ---- 04 ----
    static void JoinFromWorld()
    {
        var lc = new Lifecycle(); JoinRequest req;
        string r = lc.RequestJoin(GameContext.InWorld, true, "203.0.113.7", "27960", JoinAuth.NewCode(), out req, 0);
        Expect(r == "Um einem Multiplayer-Spiel beizutreten, musst du zuerst zum Hauptmenü zurückkehren." && lc.State == MpState.Offline, "exact German text: " + r);
        var a = MenuRules.Join(GameContext.InWorld, MpState.Offline, false, true);
        Expect(!a.Enabled && a.Reason == MenuRules.JoinFromWorldText, "menu shows the join option disabled");
    }

    // ---- 05 ----
    static void JoinBeforeReady()
    {
        int port = _port++; string code = JoinAuth.NewCode();
        string gate = "Ladevorgang läuft";
        using (var h = Host("notready", port, code, () => gate)) using (var c = Client("notready", port, "cli01", code, false))
        {
            int welcomed = 0; c.Welcomed += () => welcomed++;
            var lc = new Lifecycle(); JoinRequest req;
            lc.RequestJoin(GameContext.MainMenu, true, "127.0.0.1", port.ToString(), code, out req, 0);
            h.Start(); c.Start();
            Pump(() => c.LastReject == RejectCode.HostNotReady, 5000, "deferred", h, c);
            var act = lc.ClientTick(GameContext.MainMenu, c.State, c.LastReject, c.FatalReason, c.HostWaitReason, false, 1);
            Expect(act == ClientAction.None && lc.State == MpState.ClientConnecting && lc.Status.Contains("nicht bereit"), "waiting, no preparation: " + lc.Status);
            Expect(c.FatalReason == null && welcomed == 0 && !h.IsConnected, "retryable, never welcomed");
            PumpFor(800, h, c);
            Expect(c.State != ConnState.Failed && welcomed == 0, "still retrying while not ready");
            gate = null;
            Pump(() => c.State == ConnState.Preparing, 5000, "welcomed once the host is ready", h, c);
            Expect(welcomed == 1 && !h.IsConnected, "host waits for ClientReady");
            Expect(lc.ClientTick(GameContext.MainMenu, c.State, c.LastReject, c.FatalReason, c.HostWaitReason, false, 2) == ClientAction.ArmIsolation, "now preparation starts");
        }
    }

    // ---- 06 ----
    static void HostTransition()
    {
        int port = _port++; string code = JoinAuth.NewCode();
        string gate = null;
        using (var h = Host("transition", port, code, () => gate)) using (var c = Client("transition", port, "cli01", code, true))
        {
            h.Start(); c.Start();
            Pump(() => h.IsConnected && c.IsConnected, 5000, "connected", h, c);
            gate = "Ladevorgang läuft";    // host travels to another region
            var lc = new Lifecycle(); lc.RequestHost(GameContext.InWorld, port, 0); lc.HostStarted(true, null, 0);
            Expect(!lc.HostTick(GameContext.Loading, gate, true, 1) && lc.State == MpState.Connected, "session kept during the transition");
            using (var c2 = Client("transition", port, "cli02", code, true))
            {
                c2.Start();
                Pump(() => c2.LastReject == RejectCode.SessionFull || c2.LastReject == RejectCode.HostNotReady, 5000, "second join refused", h, c, c2);
            }
            PumpFor(500, h, c);
            Expect(h.IsConnected && c.IsConnected, "connected client unaffected by the transition");
        }
    }

    // ---- 07 ----
    static void HostToMenu()
    {
        var lc = new Lifecycle(); lc.RequestHost(GameContext.InWorld, 27960, 0); lc.HostStarted(true, null, 0);
        lc.HostTick(GameContext.InWorld, null, true, 1);
        Expect(lc.HostTick(GameContext.MainMenu, "Hauptmenü", true, 2) && lc.State == MpState.Disconnecting, "menu ends the session");
        lc.Stopped(3);
        Expect(lc.State == MpState.Offline && !lc.ClientModeSticky, "offline again, host may host again later");
        Expect(MenuRules.Host(GameContext.InWorld, lc.State, false, lc.ClientModeSticky).Enabled, "hosting available again in a world");
    }

    // ---- 08 ----
    static void Reconnect()
    {
        int port = _port++; string code = JoinAuth.NewCode();
        using (var h = Host("recon", port, code, null))
        {
            int hostConnects = 0; h.RemoteConnected += id => hostConnects++;
            h.Start();
            var c = Client("recon", port, "cli01", code, false);
            int clientConnects = 0; c.RemoteConnected += id => clientConnects++;
            c.Start();
            Pump(() => c.State == ConnState.Preparing, 5000, "preparing", h, c);
            Expect(hostConnects == 0 && clientConnects == 0, "no gameplay connection before ClientReady");
            Expect(c.ConfirmReady() && c.AutoReady, "ready confirmed; later reconnects automatic");
            Pump(() => h.IsConnected && c.IsConnected, 5000, "connected", h, c);
            Expect(hostConnects == 1 && clientConnects == 1, "exactly one connect each");
            // network loss: the host drops the peer, the client reconnects on its own and sends ClientReady automatically
            h.Dispose();
            Pump(() => c.State == ConnState.WaitingRetry || c.State == ConnState.Connecting, 8000, "client notices", c);
            using (var h2 = Host("recon2", port, code, null))
            {
                int h2Connects = 0; h2.RemoteConnected += id => h2Connects++;
                h2.Start();
                Pump(() => h2.IsConnected && c.IsConnected, 10000, "reconnected without a new preparation", h2, c);
                Expect(h2Connects == 1 && clientConnects == 2, "one connect per connection, never two at once");
            }
            c.Dispose();
        }
    }

    // ---- 09 ----
    static void AuthFailure()
    {
        int port = _port++;
        using (var h = Host("authfail", port, JoinAuth.NewCode(), null)) using (var c = Client("authfail", port, "cli01", JoinAuth.NewCode(), false))
        {
            int welcomed = 0; c.Welcomed += () => welcomed++;
            var lc = new Lifecycle(); JoinRequest req;
            lc.RequestJoin(GameContext.MainMenu, true, "127.0.0.1", port.ToString(), JoinAuth.NewCode(), out req, 0);
            h.Start(); c.Start();
            int arms = 0; ClientAction last = ClientAction.None;
            Pump(() => { var a = lc.ClientTick(GameContext.MainMenu, c.State, c.LastReject, c.FatalReason, c.HostWaitReason, false, 1); if (a == ClientAction.ArmIsolation) arms++; if (a != ClientAction.None) last = a; return c.State == ConnState.Failed && lc.State == MpState.Error; }, 5000, "failed", h, c);
            Expect(arms == 0 && welcomed == 0 && last == ClientAction.GiveUp, "never prepared, gave up");
            Expect(lc.Error.Contains("Beitrittscode") && !lc.ClientModeSticky, "German error, nothing armed: " + lc.Error);
        }
    }

    // ---- 10 ----
    static void Timeout()
    {
        var lc = new Lifecycle(); JoinRequest req;
        lc.RequestJoin(GameContext.MainMenu, true, "203.0.113.7", "27960", JoinAuth.NewCode(), out req, 0);
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connecting, null, null, null, false, 10) == ClientAction.None, "still trying");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.WaitingRetry, null, null, null, false, 31) == ClientAction.GiveUp && lc.State == MpState.Error, "timeout after 30 s");
        Expect(lc.Error.Contains("Zeitüberschreitung") && !lc.ClientModeSticky, "German timeout text, nothing armed");
        var lc2 = new Lifecycle();
        lc2.RequestJoin(GameContext.MainMenu, true, "203.0.113.7", "27960", JoinAuth.NewCode(), out req, 0);
        lc2.ClientTick(GameContext.MainMenu, ConnState.WaitingRetry, RejectCode.HostNotReady, null, "host not gameplay-ready", false, 5);
        Expect(lc2.ClientTick(GameContext.MainMenu, ConnState.WaitingRetry, RejectCode.HostNotReady, null, "host not gameplay-ready", false, 100) == ClientAction.None, "a loading host gets a longer wait");
        Expect(lc2.ClientTick(GameContext.MainMenu, ConnState.WaitingRetry, RejectCode.HostNotReady, null, null, false, 181) == ClientAction.GiveUp, "but not forever");
        var lc3 = new Lifecycle();
        lc3.RequestJoin(GameContext.MainMenu, true, "203.0.113.7", "27960", JoinAuth.NewCode(), out req, 0);
        Expect(lc3.ClientTick(GameContext.InWorld, ConnState.Connecting, null, null, null, false, 1) == ClientAction.GiveUp, "leaving the menu while connecting cancels");
    }

    // ---- 11 ----
    static void HostCrash()
    {
        var lc = new Lifecycle(); JoinRequest req;
        lc.RequestJoin(GameContext.MainMenu, true, "203.0.113.7", "27960", JoinAuth.NewCode(), out req, 0);
        lc.ClientTick(GameContext.MainMenu, ConnState.Preparing, null, null, null, false, 1);
        lc.IsolationArmed(1); lc.SystemsConfirmed(1);
        lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, true, 2);
        lc.ClientTick(GameContext.Loading, ConnState.Connected, null, null, null, true, 3);
        Expect(lc.State == MpState.Connected, "connected in the world");
        Expect(lc.ClientTick(GameContext.InWorld, ConnState.WaitingRetry, null, null, null, true, 4) == ClientAction.None && lc.Status.Contains("unterbrochen"), "host loss: retrying, shown");
        Expect(lc.ClientTick(GameContext.InWorld, ConnState.Failed, RejectCode.AuthFailed, "host rejected", null, true, 5) == ClientAction.GiveUp && lc.State == MpState.Error, "restarted host with a new code: session ends");
        Expect(lc.ClientModeSticky, "isolation stays armed after the session");
    }

    // ---- 12 ----
    static void SoloUnaffected()
    {
        var lc = new Lifecycle();
        Expect(lc.State == MpState.Offline && !lc.Busy(), "no session by default");
        Expect(MenuRules.Host(GameContext.MainMenu, MpState.Offline, false, false).Enabled == false, "nothing can start in the menu by accident");
        string root = Repo();
        string sg = File.ReadAllText(Path.Combine(root, "src", "Plugin", "SaveGuard.cs"));
        int a = sg.IndexOf("public static bool Precheck", StringComparison.Ordinal), b = sg.IndexOf("public static bool Arm", StringComparison.Ordinal);
        Expect(a > 0 && b > a, "Precheck and Arm found");
        string pre = sg.Substring(a, b - a);
        Expect(pre.IndexOf(".Patch(", StringComparison.Ordinal) < 0 && pre.IndexOf("PatchRedirects", StringComparison.Ordinal) < 0 && pre.IndexOf("InstallGuards", StringComparison.Ordinal) < 0, "Precheck applies no patch");
        string pl = File.ReadAllText(Path.Combine(root, "src", "Plugin", "Plugin.cs"));
        int f8 = pl.IndexOf("if (!legacy)", StringComparison.Ordinal), ret = pl.IndexOf("return;", f8, StringComparison.Ordinal);
        string f8Path = pl.Substring(f8, ret - f8);
        Expect(f8Path.IndexOf("SaveGuard.Install", StringComparison.Ordinal) < 0 && f8Path.IndexOf(".Patch", StringComparison.Ordinal) < 0 && f8Path.IndexOf("SessionNode", StringComparison.Ordinal) < 0, "F8 startup path: no patch, no network");
        string menu = File.ReadAllText(Path.Combine(root, "src", "Plugin", "MultiplayerMenu.cs"));
        Expect(menu.IndexOf("SaveGuard.Arm(", StringComparison.Ordinal) > 0 && menu.IndexOf("void Prepare(", StringComparison.Ordinal) < menu.IndexOf("SaveGuard.Arm(", StringComparison.Ordinal), "arming only inside the prepared join step");
        foreach (var f in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
            foreach (var line in File.ReadAllLines(f))
                if (line.Contains("_log.") || line.Contains("log.Info") || line.Contains("log.Warn") || line.Contains("log.Error"))
                    Expect(!(line.Contains("_hostCode") || line.Contains("req.Code") || line.Contains("_joinCode") || line.Contains("codeText")), Path.GetFileName(f) + " may log a join code: " + line.Trim());
    }

    static string Repo()
    {
        string d = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && d != null; i++) { if (Directory.Exists(Path.Combine(d, "src", "Plugin"))) return d; d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar)); }
        throw new Exception("repo not found");
    }

    // ---- 13 ----
    static void ReadyProtocol()
    {
        int port = _port++; string code = JoinAuth.NewCode();
        using (var h = Host("ready", port, code, null))
        {
            h.ReadyTimeoutMs = 800;
            int delivered = 0; h.MessageReceived += (p, t, r) => delivered++;
            h.Start();
            var c = Client("ready", port, "cli01", code, false);
            c.Start();
            Pump(() => c.State == ConnState.Preparing, 5000, "preparing", h, c);
            Pump(() => c.State != ConnState.Preparing, 5000, "host times out the silent client", h, c);
            Expect(!h.IsConnected && delivered == 0, "ready timeout, nothing delivered");
            c.Dispose();
            Pump(() => h.State == ConnState.Listening, 3000, "host free", h);
            var c2 = Client("ready", port, "cli02", code, false);
            c2.Start();
            Pump(() => c2.State == ConnState.Preparing, 5000, "preparing 2", h, c2);
            c2.AbortPreparation("isolation not verified");
            Pump(() => c2.State == ConnState.Failed, 5000, "abort -> failed", h, c2);
            int att = c2.ConnectAttempts; PumpFor(600, h, c2);
            Expect(c2.ConnectAttempts == att && c2.FatalReason.StartsWith("PREPARATION_FAILED") && !h.IsConnected, "fatal, no retry");
            c2.Dispose();
        }
    }

    // ---- 14 ----
    static void FullClientFlow()
    {
        var lc = new Lifecycle(); JoinRequest req;
        lc.RequestJoin(GameContext.MainMenu, true, "127.0.0.1", "27960", JoinAuth.NewCode(), out req, 0);
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Handshaking, null, null, null, false, 1) == ClientAction.None, "authenticating");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Preparing, null, null, null, false, 2) == ClientAction.ArmIsolation && lc.State == MpState.ClientPreparing, "arm request");
        lc.IsolationArmed(2);
        Expect(lc.ClientModeSticky, "sticky after arming");
        lc.SystemsConfirmed(2);
        Expect(lc.State == MpState.ClientSynchronizing, "synchronizing after ClientReady");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, false, 3) == ClientAction.None, "no load before the world snapshot");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, true, 4) == ClientAction.ContinueGame, "continue once synchronized");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, true, 5) == ClientAction.None, "continue requested only once");
        Expect(lc.ClientTick(GameContext.Loading, ConnState.Connected, null, null, null, true, 6) == ClientAction.None && lc.State == MpState.Connected, "connected once loading");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, true, 100) == ClientAction.None, "no sync timeout after synchronization");
        lc.BeginStop(101); lc.Stopped(102);
        Expect(lc.State == MpState.Offline && lc.Status.Contains("Neustart"), "offline, restart hint");
        Expect(!MenuRules.Host(GameContext.InWorld, lc.State, false, lc.ClientModeSticky).Enabled, "no hosting in a former client process");
        Expect(MenuRules.Join(GameContext.MainMenu, lc.State, false, true).Enabled, "joining again is allowed");
        // re-join with isolation already armed: Connected straight after the handshake (AutoReady)
        Expect(lc.RequestJoin(GameContext.MainMenu, true, "127.0.0.1", "27960", JoinAuth.NewCode(), out req, 200) == null, "rejoin");
        lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, false, 201);
        Expect(lc.State == MpState.ClientSynchronizing, "already armed: straight to synchronizing");
    }

    // ---- 15 ----
    static void IsolationFailure()
    {
        var lc = new Lifecycle(); JoinRequest req;
        lc.RequestJoin(GameContext.MainMenu, true, "127.0.0.1", "27960", JoinAuth.NewCode(), out req, 0);
        lc.ClientTick(GameContext.MainMenu, ConnState.Preparing, null, null, null, false, 1);
        lc.IsolationFailed("FBPP outside sandbox", 1);
        Expect(lc.State == MpState.Error && lc.Error.Contains("Speicherschutz"), "error");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, true, 2) == ClientAction.None, "never continues");
        Expect(!MenuRules.Join(GameContext.MainMenu, MpState.Offline, false, false).Enabled, "join disabled without isolation");
    }

    // ---- 16 ----
    static void IsolationRules()
    {
        string real = @"C:\Users\x\AppData\LocalLow\CookieDev\Thief Simulator 2", game = @"D:\Games\Thief Simulator 2", sb = @"C:\Users\x\AppData\Local\TS2MP\client-sandbox";
        Expect(SaveIsolation.LayoutError(sb, real, game) == null, "normal layout ok");
        Expect(SaveIsolation.LayoutError(real + @"\sandbox", real, game) != null, "sandbox inside the real save folder refused");
        Expect(SaveIsolation.LayoutError(game + @"\sandbox", real, game) != null, "sandbox inside the game folder refused");
        Expect(SaveIsolation.LayoutError(@"C:\Users\x\AppData", real, game) != null, "sandbox containing the real folder refused");
        Expect(SaveIsolation.IsProtectedWrite(real + @"\saveData.thief", real, game), "real save protected");
        Expect(SaveIsolation.IsProtectedWrite(game + @"\Thief Simulator 2_Data\x.ini", real, game), "game folder protected");
        Expect(!SaveIsolation.IsProtectedWrite(game + @"\BepInEx\config\x.cfg", real, game), "BepInEx folder allowed");
        Expect(!SaveIsolation.IsProtectedWrite(sb + @"\saveData.thief", real, game), "sandbox allowed");
        Expect(!SaveIsolation.IsProtectedWrite(real + "2\\x", real, game), "prefix lookalike folder not matched");
        Expect(SaveIsolation.Effective(false, sb, () => real) == real && SaveIsolation.Effective(true, sb, () => real) == sb, "effective path switches only when armed");
    }

    // ---- 17 ----
    static void MenuMatrix()
    {
        Expect(!MenuRules.Host(GameContext.InWorld, MpState.Offline, true, false).Enabled && !MenuRules.Join(GameContext.MainMenu, MpState.Offline, true, true).Enabled, "launcher mode: status only");
        Expect(!MenuRules.Host(GameContext.InWorld, MpState.Offline, false, true).Enabled, "former client: no hosting");
        Expect(!MenuRules.Host(GameContext.InWorld, MpState.HostReady, false, false).Enabled, "no second host");
        Expect(!MenuRules.Join(GameContext.MainMenu, MpState.ClientConnecting, false, true).Enabled, "no second join");
        Expect(MenuRules.Join(GameContext.MainMenu, MpState.Error, false, true).Enabled, "join again after an error");
        Expect(!MenuRules.Join(GameContext.Loading, MpState.Offline, false, true).Enabled, "no join while loading");
        Expect(Lifecycle.Explain(null, RejectCode.SessionFull).Contains("bereits") && Lifecycle.Explain(null, RejectCode.ModVersionMismatch).Contains("Mod-Version")
            && Lifecycle.Explain("HOST_NOT_AUTHENTICATED: x", null).Contains("Host") && Lifecycle.Explain(null, RejectCode.RateLimited).Contains("warten"), "error texts");
    }
}
