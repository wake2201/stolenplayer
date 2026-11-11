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
        Run("M2: v1 client (protocol 1) rejected by current host", () => RejectCase(h => h.ProtocolVersion = 1, RejectCode.ProtocolMismatch));
        Run("M3: v2 (M2) client rejected by current host", () => RejectCase(h => h.ProtocolVersion = 2, RejectCode.ProtocolMismatch));
        Run("M3.1: v3 (M3) client rejected by v4 host", () => RejectCase(h => h.ProtocolVersion = 3, RejectCode.ProtocolMismatch));
        Run("M4.1: v5 (M3.1.2) client rejected by v6 host", () => RejectCase(h => h.ProtocolVersion = 5, RejectCode.ProtocolMismatch));
        Run("M3.1.2: v4 (M3.1/M3.1.1) client rejected by v6 host", () => RejectCase(h => h.ProtocolVersion = 4, RejectCode.ProtocolMismatch));
        Run("M3.1: world state over UDP: join snapshot, region clock init, client sleep (deduplicated), isolation, rejection, reconnect", WorldStateExchange);
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

    // M3/M3.1: the real host logic (WorldHost) over the real transport; client side mirrors WorldSync's routing.
    static void WorldStateExchange()
    {
        int port = _port++;
        double t = 0;
        var wh = new WorldHost(0x1234);
        wh.FallbackHours = 10f;
        wh.SetPlayerRegion(1, 0, t);   // host in the hideout
        wh.Outbox.Clear();
        var cli = StateRegistry.CreateClient();
        var acks = new List<ProposalCode>();
        int snapshots = 0, gaps = 0;
        using (var h = Host("world", port))
        {
            var w = new LiteNetLib.Utils.NetDataWriter();
            Action flush = () => { foreach (var e in wh.Outbox) { w.Reset(); StateMessages.WriteDelta(w, wh.Reg.Epoch, e); h.Send(w, LiteNetLib.DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable); } wh.Outbox.Clear(); };
            h.RemoteConnected += id => { wh.Clocks.RefreshRunning(t, wh.Outbox); wh.Outbox.Clear(); w.Reset(); StateMessages.WriteSnapshot(w, wh.Reg); h.Send(w, LiteNetLib.DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable); };
            h.MessageReceived += (peer, type, r) =>
            {
                if (type != (byte)MsgType.StateProposal) return;
                uint id; StateEntry p; uint rev; int sr; float sh;
                Expect(StateMessages.TryReadProposal(r, out id, out p), "proposal parse");
                var code = wh.HandleProposal(h.RemoteInstanceId, 2, id, p, t, out rev, out sr, out sh);
                flush();
                var rw = new LiteNetLib.Utils.NetDataWriter(); StateMessages.WriteResult(rw, id, code, rev);
                h.Send(rw, LiteNetLib.DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable);
            };
            Action<SessionNode> wireClient = c =>
            {
                c.RemoteConnected += id => cli.Reset();
                c.MessageReceived += (peer, type, r) =>
                {
                    if (type == (byte)MsgType.StateSnapshot)
                    {
                        uint e, hd; List<StateEntry> l;
                        Expect(StateMessages.TryReadSnapshot(r, out e, out hd, out l), "snapshot parse");
                        cli.LoadSnapshot(e, hd, l); snapshots++;
                    }
                    else if (type == (byte)MsgType.StateDelta)
                    {
                        uint e; StateEntry d;
                        Expect(StateMessages.TryReadDelta(r, out e, out d), "delta parse");
                        if (cli.ApplyDelta(e, d) != DeltaResult.Applied) gaps++;
                    }
                    else if (type == (byte)MsgType.ProposalResult)
                    {
                        uint id, rev; ProposalCode code;
                        Expect(StateMessages.TryReadResult(r, out id, out code, out rev), "result parse");
                        acks.Add(code);
                    }
                };
            };
            Action<SessionNode, uint, StateEntry> propose = (c, id, e) =>
            {
                var pw = new LiteNetLib.Utils.NetDataWriter();
                StateMessages.WriteProposal(pw, id, e);
                c.Send(pw, LiteNetLib.DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable);
            };
            Func<int, StateEntry> regionProp = region => new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Region, Value = WorldKeys.Int(region) };
            Func<int, SleepRequest, StateEntry> sleepProp = (region, sr) => new StateEntry { Scope = StateScope.Region, ScopeArg = region, Key = WorldKeys.Sleep, Value = sr.ToBytes() };
            h.Start();
            using (var c = Client("world", port, "cli01"))
            {
                wireClient(c); c.Start();
                Pump(() => cli.HasSnapshot, 5000, "initial snapshot", h, c);
                Expect(cli.HeadRev == wh.Reg.HeadRev && RegionDirectory.RegionOf(cli, 1) == 0, "late-join snapshot content");
                propose(c, 1, regionProp(97));   // client enters Hillville (new region)
                propose(c, 1, regionProp(97));   // retransmitted duplicate
                Pump(() => acks.Count == 2, 5000, "2 proposal results", h, c);
                Expect(acks[0] == ProposalCode.Accepted && acks[1] == ProposalCode.Accepted, "acks");
                Pump(() => cli.HeadRev == wh.Reg.HeadRev, 2000, "deltas applied", h, c);
                var ce = cli.Get(StateScope.Region, 97, WorldKeys.Clock);
                RegionClock rc;
                Expect(ce != null && RegionClock.TryParse(ce.Value, out rc) && Math.Abs(rc.Hours - 10f) < 0.01f, "Hillville clock initialized from the session base clock");
                propose(c, 2, new StateEntry { Scope = StateScope.Region, ScopeArg = 97, Key = WorldKeys.SleepStart, Value = new SleepStart { From = 10f, TargetHour = 18 }.ToBytes() });
                propose(c, 3, sleepProp(97, new SleepRequest { From = 10f, Advance = 8f }));
                propose(c, 3, sleepProp(97, new SleepRequest { From = 10f, Advance = 8f }));   // duplicate sleep must not advance twice
                Pump(() => acks.Count == 5, 5000, "sleep results", h, c);
                Pump(() => cli.HeadRev == wh.Reg.HeadRev, 2000, "sleep delta", h, c);
                Expect(acks[2] == ProposalCode.Accepted && acks[3] == ProposalCode.Accepted && acks[4] == ProposalCode.Accepted, "sleep start + sleep acks");
                Expect(RegionClock.TryParse(cli.Get(StateScope.Region, 97, WorldKeys.Clock).Value, out rc) && Math.Abs(rc.Hours - 18f) < 0.01f, "Hillville advanced once to 18:00, got " + rc.Hours);
                Expect(Math.Abs(wh.Clocks.Model(0, t) - 10f) < 0.01f, "host's hideout unaffected by the client's sleep");
                propose(c, 4, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.Calendar, Value = new Calendar { Day = 3 }.ToBytes() });
                Pump(() => acks.Count == 6, 5000, "rejection", h, c);
                Expect(acks[5] == ProposalCode.NotAuthorized, "client must not overwrite the shared calendar");
            }
            Pump(() => !h.IsConnected, 5000, "host sees client gone", h);
            wh.SetPlayerRegion(2, Regions.None, t);
            wh.SetPlayerRegion(1, 99, t);   // changes while the client is away
            wh.Outbox.Clear();
            using (var c2 = Client("world", port, "cli02"))
            {
                wireClient(c2); c2.Start();
                Pump(() => snapshots == 2 && cli.HasSnapshot, 8000, "resync snapshot after reconnect", h, c2);
                RegionClock rc;
                Expect(cli.HeadRev == wh.Reg.HeadRev && RegionDirectory.RegionOf(cli, 1) == 99 && RegionDirectory.RegionOf(cli, 2) == Regions.None, "reconnect restored current state");
                Expect(RegionClock.TryParse(cli.Get(StateScope.Region, 97, WorldKeys.Clock).Value, out rc) && !rc.Running && Math.Abs(rc.Hours - 18f) < 0.01f, "empty Hillville kept 18:00 (frozen)");
                Expect(gaps == 0, "no gaps/duplicates expected over reliable ordered channel");
            }
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
