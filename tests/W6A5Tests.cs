// W6A.5 offline tests: story-test join deadlock (real two-PC log 2026-11-17: index 18 seeded and published, HOST_READINESS
// "ready: Player 2 may join", scenario check pending "Story-Objekt 'story object 103 notka' nicht aktiv", FAILED after 20 s,
// client HostNotReady (13) forever). Uses the real DevScenario / ScenarioActions / JoinGate / ScenarioPrereqs rules, real
// SessionNode UDP loopback joins, and source scans of the Unity glue. In-game: docs/TESTING.md §54.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TS2MP.Shared;

static class W6A5Tests
{
    static int _passed, _failed, _port = 37760;
    static string _logDir;
    const string Game = "1.27c";

    static int Main()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ts2mp-w6a5-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDir);
        Run("01 real-log reproduction: a valid index-18 world without the note_103 holder passed the W6A.4 object rule never; W6A.5 critical check passes", RealLog);
        Run("02 note_103 / story14 actions: note reported unavailable in the original asset (never as testable), drone camera 104 = primary objective, diagnostics only", Actions);
        Run("03 one readiness state: world / waiting / checking / valid / invalid transitions; never 'may join' while refusing; invalid is permanent", Transitions);
        Run("04 client joins BEFORE the host world: HostNotReady retried through loading + scenario check, joins automatically once valid (UDP)", JoinBeforeReady);
        Run("05 invalid scenario: ScenarioInvalid (14), client stops (Failed, no endless retry), precise explanation (UDP)", InvalidScenario);
        Run("06 client joins AFTER the host is ready and normal F8 host (no scenario): immediate join, no fatal gate (UDP)", JoinAfterReady);
        Run("07 drones: P1/P2 test prerequisites, Player-2 grant once per fresh isolated record, no duplicate on reconnect, never in normal campaigns", Drones);
        Run("08 Unity glue: network gate / HOST_READINESS / lifecycle / F8 chip / F10 all read _gateNow; scenario objects not in the gate", Glue);
        Run("09 regressions: W6A story trigger, W2B note keep-visible hook, save isolation untouched; protocol 37 / mod 0.26.5", Regressions);
        Console.WriteLine(_failed == 0 ? "W6A5 TESTS PASSED (" + _passed + ")" : "W6A5 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // The host facts of the real log: Story 18 'story14', Madison Street, Item_89 = 1, Drone = 89 (equipped), flyby_upgrade3 = 1,
    // Player-2 record with item 89, spawn beside the host. The note_103 holder is NOT active (original asset state).
    static ScenarioFacts RealLogFacts()
    {
        var f = new ScenarioFacts { Expected = 18, LiveIndex = 18, LiveKey = "story14", FreeMode = false, Region = 99, EquippedDrone = 89, P2RecordKnown = true, SpawnOk = true, SpawnNote = "beside the host" };
        f.Prefs["Item_89"] = 1; f.Prefs["Drone"] = 89; f.Prefs["flyby_upgrade3"] = 1; f.P2Items[89] = 1;
        return f;
    }

    // ---------------------------------------------------------------- 01
    static void RealLog()
    {
        var keys = new List<string>(); var vals = new List<int>();
        DevScenario.SeedKeys(18, keys, vals);
        var seed = new Dictionary<string, int> { { "CurrentLocation", 99 }, { "FreeMode", 0 } };
        for (int i = 0; i < keys.Count; i++) seed[keys[i]] = vals[i];
        Expect(DevScenario.VerifySeed(18, k => { int v; return seed.TryGetValue(k, out v) ? v : 0; }) == null, "index-18 scenario seed as in the log (Story 18, Item_89 1, Drone 89, flyby_upgrade3 1)");
        Expect(DevScenario.Check(RealLogFacts()) == null, "the logged world is a valid session: the critical check passes without any story object");
        var src = Strip(Src("Shared", "DevScenario.cs"));
        int c = src.IndexOf("public static string Check(ScenarioFacts f)", StringComparison.Ordinal);
        string check = src.Substring(c, src.IndexOf("public static string Label(int index)", c, StringComparison.Ordinal) - c);
        Expect(check.IndexOf("Story-Objekt", StringComparison.Ordinal) < 0 && check.IndexOf("ScenarioActions", StringComparison.Ordinal) < 0, "no story-object / action line in the critical check (W6A.4 failure cause removed)");
        Expect(src.IndexOf("RequiredObjects", StringComparison.Ordinal) < 0, "W6A.4 RequiredObjects (note holder lookup) removed");
        // Every other line of the logged world still decides the check.
        var f = RealLogFacts(); f.LiveIndex = 0; f.LiveKey = "story1_escape"; Expect(DevScenario.Check(f) != null, "index 0 before BeginStory");
        f = RealLogFacts(); f.EquippedDrone = 0; Expect(DevScenario.Check(f) != null, "drone not yet equipped -> pending");
        f = RealLogFacts(); f.P2Items.Clear(); Expect(DevScenario.Check(f) != null, "Player 2 without drone");
        f = RealLogFacts(); f.Region = 0; Expect(DevScenario.Check(f) != null, "wrong region");
    }

    // ---------------------------------------------------------------- 02
    static void Actions()
    {
        var a = ScenarioActions.For(18);
        Expect(a.Length == 2, "story14: two original completion actions");
        var cam = a[0]; var note = a[1];
        Expect(cam.Id == "camera_104_drone" && cam.Primary && cam.StaticUnavailable == null && cam.FindPath == "Junkyard/SECURITY CAM (1)/Security Camera", "primary = SecurityCamera onDisableAdvanceStory story14 (scene root, not distance-culled)");
        Expect(note.Id == "note_103" && !note.Primary && note.StaticUnavailable != null && note.FindPath == null, "note_103: unavailable in the original asset, never looked up");
        Expect(ScenarioActions.Describe(note, true).Contains("nicht verfügbar"), "note never described as testable, even with a bogus 'active' input");
        Expect(ScenarioActions.Describe(cam, true).Contains("verfügbar") && !ScenarioActions.Describe(cam, true).Contains("nicht") && ScenarioActions.Describe(cam, false).Contains("nicht aktiv"), "camera action status both ways");
        Expect(ScenarioActions.For(5).Length == 0 && ScenarioActions.For(-1).Length == 0, "no actions listed for other steps");
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        int ok = dev.IndexOf("_scenarioState = ScenarioState.Valid;", StringComparison.Ordinal), act = dev.IndexOf("_actionsLine = ActionsLine(_checkIndex);", StringComparison.Ordinal);
        Expect(ok > 0 && act > ok, "actions evaluated only after the critical check passed (diagnostic, once)");
        Expect(dev.Contains("TEST_STORY story actions (diagnostic, not a join condition)"), "logged as diagnostic");
    }

    // ---------------------------------------------------------------- 03
    static void Transitions()
    {
        Expect(JoinGate.Reason("Welt lädt", ScenarioState.None, null) == "Welt lädt", "F8 loading: world reason");
        Expect(JoinGate.Reason(null, ScenarioState.None, null) == null, "F8 ready");
        Expect(JoinGate.Reason("Welt lädt", ScenarioState.Waiting, null) == "Welt lädt", "story test: world first");
        Expect(JoinGate.Reason(null, ScenarioState.Waiting, null) != null, "world ready but scenario not yet checked: closed (the real-log case logged 'may join' here)");
        Expect(JoinGate.Reason(null, ScenarioState.Checking, "Drohne 89 nicht ausgerüstet").Contains("Drohne"), "checking: pending reason shown");
        Expect(JoinGate.Reason(null, ScenarioState.Valid, null) == null, "valid: open");
        string inv = JoinGate.Reason(null, ScenarioState.Invalid, "Index 0");
        Expect(inv != null && inv.Contains("Index 0") && JoinGate.Fatal(ScenarioState.Invalid), "invalid: closed permanently with the cause");
        Expect(JoinGate.Reason("Welt lädt", ScenarioState.Invalid, "x").StartsWith("Story-Test ungültig"), "invalid stays invalid while the world reloads");
        foreach (ScenarioState s in Enum.GetValues(typeof(ScenarioState)))
            foreach (var w in new[] { null, "Welt lädt" })
            {
                string r = JoinGate.Reason(w, s, "d"); string line = JoinGate.LogLine(r, JoinGate.Fatal(s));
                Expect((r == null) == line.Contains("may join"), "HOST_READINESS says 'may join' exactly when joins are accepted (" + s + ", " + (w ?? "ready") + ")");
                var rej = JoinGate.Reject(r, JoinGate.Fatal(s));
                Expect(r == null ? rej == null : rej == (s == ScenarioState.Invalid ? RejectCode.ScenarioInvalid : RejectCode.HostNotReady), "network decision matches the state");
            }
        Expect(!JoinGate.Fatal(ScenarioState.Checking) && !JoinGate.Fatal(ScenarioState.Waiting) && !JoinGate.Fatal(ScenarioState.None), "only an invalid scenario is permanent");
        Expect(Validation.IsFatal(RejectCode.ScenarioInvalid) && !Validation.IsFatal(RejectCode.HostNotReady), "client: ScenarioInvalid stops, HostNotReady retries");
        Expect((byte)RejectCode.ScenarioInvalid == 14 && (byte)RejectCode.HostNotReady == 13, "reject values stable");
    }

    // ---------------------------------------------------------------- 04..06 (UDP loopback)
    static InstanceLog Log(string name, string role, string inst) { return new InstanceLog(Path.Combine(_logDir, name + ".log"), role, inst, "s1"); }
    static void Pump(Func<bool> until, int ms, string what, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); if (until()) return; Thread.Sleep(5); }
        throw new Exception("timeout: " + what + " | " + string.Join(", ", Array.ConvertAll(nodes, n => n.Role + "=" + n.State + (n.LastReject.HasValue ? "/" + n.LastReject : ""))));
    }
    static void Hold(int ms, params SessionNode[] nodes) { var end = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); Thread.Sleep(5); } }

    sealed class HostState { public string World = "Host startet"; public ScenarioState Scenario = ScenarioState.Waiting; public string Detail; }
    static SessionNode DevHost(int port, string tag, HostState st)
    {
        var h = new SessionNode(Role.Host, "hsdev", "host01", Game, port, Log(tag + "-host", "HOST", "host01"));
        h.BindAddress = "127.0.0.1"; h.JoinKey = MenuRouting.SessionKey(true, null); h.TestCampaign = true; h.AuthTimeoutMs = 1500;
        h.HostGate = () => JoinGate.Reason(st.World, st.Scenario, st.Detail);           // same composition as MultiplayerMenu.UpdateGate
        h.HostGateFatal = () => JoinGate.Fatal(st.Scenario);
        return h;
    }
    static SessionNode DevClient(int port, string tag)
    {
        var c = new SessionNode(Role.Client, "join", "c1", Game, port, Log(tag + "-c1", "CLIENT", "c1"));
        c.ConnectAddress = "127.0.0.1"; c.JoinKey = MenuRouting.SessionKey(true, null); c.ReconnectDelayMs = 200; c.HostNotReadyDelayMs = 250; c.DisconnectTimeoutMs = 2000;
        return c;
    }

    static void JoinBeforeReady()
    {
        int port = _port++; var st = new HostState();
        using (var h = DevHost(port, "before", st))
        using (var c = DevClient(port, "before"))
        {
            Expect(h.Start() && c.Start(), "start");
            Pump(() => c.LastReject == RejectCode.HostNotReady, 6000, "host loading: HostNotReady", h, c);
            st.World = null;                                         // HOST_READINESS world ready, scenario not yet checked
            st.Scenario = ScenarioState.Checking; st.Detail = "Drohne 89 nicht ausgerüstet";
            int before = c.ConnectAttempts; Hold(900, h, c);
            Expect(!h.IsConnected && c.State != ConnState.Failed && c.LastReject == RejectCode.HostNotReady && c.ConnectAttempts > before, "scenario pending: still retryable HostNotReady, client keeps retrying");
            st.Scenario = ScenarioState.Valid; st.Detail = null;      // critical check OK
            Pump(() => h.IsConnected && c.IsConnected, 8000, "joins automatically once the host is genuinely ready", h, c);
            Expect(c.HostTestCampaign && c.FatalReason == null, "developer test campaign join, no fatal state");
        }
    }

    static void InvalidScenario()
    {
        int port = _port++; var st = new HostState { World = null, Scenario = ScenarioState.Checking, Detail = "x" };
        using (var h = DevHost(port, "invalid", st))
        using (var c = DevClient(port, "invalid"))
        {
            Expect(h.Start() && c.Start(), "start");
            Pump(() => c.LastReject == RejectCode.HostNotReady, 6000, "pending first", h, c);
            st.Scenario = ScenarioState.Invalid; st.Detail = "Die geladene Welt steht auf Index 0";
            Pump(() => c.State == ConnState.Failed, 8000, "permanent failure reaches the client", h, c);
            Expect(c.LastReject == RejectCode.ScenarioInvalid && c.FatalReason != null && !h.IsConnected, "ScenarioInvalid, fatal");
            int attempts = c.ConnectAttempts; Hold(1200, h, c);
            Expect(c.ConnectAttempts == attempts && c.State == ConnState.Failed, "no further retry (no endless HostNotReady loop)");
            string why = Lifecycle.Explain(c.FatalReason, c.LastReject);
            Expect(why.Contains("Story-Test") && why.Contains("F10") && why.Contains("kein weiterer Versuch"), "client message says why and that it stopped: " + why);
        }
    }

    static void JoinAfterReady()
    {
        int port = _port++; var st = new HostState { World = null, Scenario = ScenarioState.Valid };
        using (var h = DevHost(port, "after", st))
        using (var c = DevClient(port, "after"))
        {
            Expect(h.Start() && c.Start(), "start");
            Pump(() => h.IsConnected && c.IsConnected, 8000, "immediate join when ready", h, c);
            Expect(!c.LastReject.HasValue || c.LastReject == RejectCode.HostNotReady, "no rejection other than transient readiness");
        }
        // Normal F8 host: HostGateFatal never set -> a world gate can only defer (HostNotReady), never refuse permanently.
        port = _port++; string world = "Welt lädt";
        using (var h = new SessionNode(Role.Host, "hsf8", "host02", Game, port, Log("f8-host", "HOST", "host02")))
        using (var c = new SessionNode(Role.Client, "join", "c2", Game, port, Log("f8-c2", "CLIENT", "c2")))
        {
            string code = JoinAuth.NewCode();
            h.BindAddress = "127.0.0.1"; h.JoinKey = JoinAuth.DeriveKey(code); h.HostGate = () => world; h.AuthTimeoutMs = 1500;
            c.ConnectAddress = "127.0.0.1"; c.JoinKey = JoinAuth.DeriveKey(code); c.ReconnectDelayMs = 200; c.HostNotReadyDelayMs = 250; c.DisconnectTimeoutMs = 2000;
            Expect(h.Start() && c.Start(), "start F8");
            Pump(() => c.LastReject == RejectCode.HostNotReady, 6000, "F8 loading: HostNotReady", h, c);
            world = null;
            Pump(() => h.IsConnected && c.IsConnected, 8000, "F8 join after ready", h, c);
            Expect(!h.TestCampaign && !c.HostTestCampaign, "normal campaign");
        }
    }

    // ---------------------------------------------------------------- 07
    static void Drones()
    {
        Expect(ScenarioPrereqs.For(18).Length == 2 && ScenarioPrereqs.For(18)[0].EquippedDroneId == 89 && ScenarioPrereqs.For(18)[0].P2ItemId == 89, "index 18: host equips drone 89, Player 2 gets its own item 89");
        var rec = P2Inventory.Initial(false);
        var g = ScenarioPrereqs.P2Grants(18, true, rec.CountOf);
        Expect(g.Count == 1 && g[0].P2ItemId == 89 && g[0].P2ItemCount == 1, "fresh isolated record: one drone");
        foreach (var p in g) rec.Set(p.P2ItemId, p.P2ItemCount);
        Expect(rec.CountOf(89) == 1, "granted once");
        Expect(ScenarioPrereqs.P2Grants(18, true, rec.CountOf).Count == 0, "re-evaluation / reconnect with the same record: nothing more");
        foreach (var p in ScenarioPrereqs.For(18)) if (p.P2ItemId != 0) rec.Set(p.P2ItemId, p.P2ItemCount);
        Expect(rec.CountOf(89) == 1, "Set is absolute: a repeated application never duplicates");
        Expect(ScenarioPrereqs.P2Grants(-1, true, id => 0).Count == 0, "normal campaign (no scenario): no developer grant");
        Expect(ScenarioPrereqs.P2Grants(18, false, id => 0).Count == 0, "not an isolated developer host: no grant");
        Expect(ScenarioPrereqs.P2Grants(15, true, id => 0).Count == 0, "before the drone step (index 15): no grant");
        Expect(CampaignPolicy.Classify("Item_89") == CampaignKeyClass.PlayerOwnP1 && CampaignPolicy.Classify("flyby_upgrade3") == CampaignKeyClass.World, "personal drone item, campaign-wide upgrade key (not bought twice)");
        var p2 = Strip(Src("Plugin", "Player2Sync.cs"));
        int nb = p2.IndexOf("b = new P2Book(P2Inventory.Initial(free), mark);", StringComparison.Ordinal), gr = p2.IndexOf("ScenarioPrereqs.P2Grants(", StringComparison.Ordinal), end = p2.IndexOf("if (_get(HistoryKey) != b.HistoryId)", StringComparison.Ordinal);
        Expect(nb > 0 && gr > nb && gr < end, "grant only in the fresh-record branch (late join / reconnect load the existing record)");
        int n = 0, at = 0; while ((at = p2.IndexOf("ScenarioPrereqs", at, StringComparison.Ordinal)) >= 0) { n++; at++; }
        Expect(n == 1, "no other developer grant site in Player2Sync");
    }

    // ---------------------------------------------------------------- 08
    static void Glue()
    {
        var code = Strip(Src("Plugin", "MultiplayerMenu.cs")); var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs")); var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs"));
        Expect(code.Contains("node.HostGate = () => _gateNow;") && code.Contains("node.HostGateFatal = () => JoinGate.Fatal(_scenarioState);"), "network gate = the one state");
        Expect(code.Contains("n.HostGate = () => _gateNow;"), "launcher host too");
        Expect(code.Contains("string g = JoinGate.Reason(_hostGateReason, _scenarioState, _scenarioDetail);") && code.Contains("_log.Info(\"HOST_READINESS \" + JoinGate.LogLine(g, JoinGate.Fatal(_scenarioState)));"), "HOST_READINESS logs the combined state");
        int rh = code.IndexOf("void RefreshHostGate(GameContext ctx)", StringComparison.Ordinal);
        Expect(code.IndexOf("HOST_READINESS", rh, code.IndexOf("string _gateNow", rh, StringComparison.Ordinal) - rh, StringComparison.Ordinal) < 0, "world-only gate no longer logs readiness by itself");
        int hf = code.IndexOf("void HostFrame(GameContext ctx, double now)", StringComparison.Ordinal);
        string frame = code.Substring(hf, code.IndexOf("void ClientFrame(", hf, StringComparison.Ordinal) - hf);
        int a = frame.IndexOf("RefreshHostGate(ctx);", StringComparison.Ordinal), b = frame.IndexOf("DevHostTick(ctx);", StringComparison.Ordinal), u = frame.IndexOf("UpdateGate();", StringComparison.Ordinal), t = frame.IndexOf("_lc.HostTick(ctx, _gateNow,", StringComparison.Ordinal);
        Expect(a > 0 && b > a && u > b && t > u, "world gate -> scenario tick -> combined gate -> lifecycle status, in this order");
        Expect(ui.Contains("string gate = _gateNow;") && ui.IndexOf("_hostGateReason", StringComparison.Ordinal) < 0, "F8 chip reads the combined state");
        Expect(dev.Contains("string HostLine(bool menu)") && dev.Contains("string g = _gateNow;") && dev.Contains("GUILayout.Label(HostLine(true));") && dev.Contains(": HostLine(false);"), "F10 menu + in-world status read the combined state");
        Expect(dev.Contains("BEITRITT GESPERRT") && dev.Contains("wartet automatisch"), "F10 distinguishes permanent failure from waiting");
        Expect(dev.Contains("ScenarioInvalid(bad);") && dev.Contains("ScenarioInvalid(\"Spielstand: \" + bad);"), "final check failure and invalid seed are permanent");
        Expect(dev.IndexOf("_scenarioGate", StringComparison.Ordinal) < 0 && code.IndexOf("_scenarioGate", StringComparison.Ordinal) < 0, "the separate W6A.4 scenario gate string is gone");
        var node = Strip(Src("Shared", "SessionNode.cs"));
        Expect(node.Contains("rej = JoinGate.Reject(notReady, GateFatal());") && node.Contains("peer.Disconnect(new[] { (byte)JoinGate.Reject(notReady, GateFatal()).Value });"), "request and welcome re-check use the same decision");
    }

    // ---------------------------------------------------------------- 09
    static void Regressions()
    {
        Expect(StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, "DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3") != null, "W6A note trigger kept (harmless if the original ever shows the note)");
        var story = Strip(Src("Plugin", "StorySync.cs"));
        Expect(story.Contains("static void NoteStartPost(object __instance)") && story.Contains("if (go.activeSelf || !_noteAdvance(__instance) || !_noteAddCode(__instance) || _prefInt(_notePref(__instance)) < 2) return;"), "W2B / W6A note keep-visible hook unchanged (only re-shows a note hidden by its own ReadNote.Start)");
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        foreach (var bad in new[] { "Directory.Delete", "File.Delete", "File.Copy" }) Expect(dev.IndexOf(bad, StringComparison.Ordinal) < 0, "developer menu never " + bad);
        Expect(dev.IndexOf(".SetActive(true)", StringComparison.Ordinal) < 0, "no story object is force-enabled");
        Expect(ProtocolInfo.ProtocolVersion == 41 && ProtocolInfo.ModVersion == "0.31.0", "protocol 37 / mod 0.26.5");
    }

    // ---------------------------------------------------------------- helpers
    static string Root()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && root != null; i++) { if (Directory.Exists(Path.Combine(root, "src", "Plugin"))) return Path.Combine(root, "src"); root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)); }
        throw new Exception("src not found");
    }
    static string Src(string dir, string f) { return File.ReadAllText(Path.Combine(Root(), dir, f)).Replace("\r\n", "\n"); }
    static string Strip(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var line in s.Split('\n')) { int i = line.IndexOf("//", StringComparison.Ordinal); string l = i >= 0 && line.IndexOf("\"", 0, i, StringComparison.Ordinal) < 0 ? line.Substring(0, i) : line; sb.Append(l.Trim()).Append('\n'); }
        return sb.ToString();
    }
}
