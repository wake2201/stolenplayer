// W6A.4 offline tests: story-test prerequisites (drone + camera module for index 18, both players), Player-2 first spawn in
// story tests, and the scenario check that gates joins / HOST READY. Models the ORIGINAL load path on prefs dictionaries
// (MainMenu.StartNewGame skip-intro, ItemID.Start Item_<id>, DroneManager.<FindDrone> + AssignDrone + ApplyUpgrades), uses the
// real CampaignPolicy key classes for Player 2's sandbox, the real SpawnRules / DevScenario / ScenarioPrereqs, and source-scans
// the Unity glue. In-game: docs/TESTING.md §53.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TS2MP.Shared;

static class W6A4Tests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("01 prerequisite table: index 18 needs the Flyby drone (item 89, Drone key) + camera module (flyby_upgrade3); seed keys verified", Table);
        Run("02 original load model: host seed equips drone 89 with the camera module; Player 2 (World keys + own record item) equips its own", LoadModel);
        Run("03 Player-2 grant: once, only in a fresh isolated record; rejoin keeps it, a reset scenario grants once again, never without a scenario", Grant);
        Run("04 Player-2 spawn: fresh seed checkpoint -> hideout (reproduced); story test -> beside the host in Madison; rejoin keeps its own position", Spawn);
        Run("05 scenario check: index 0 before BeginStory never OK; every requirement (index, region, drone, upgrade, P2 item, note_103, spawn) can fail it", Check);
        Run("06 ordering + gate: check only after HOST_READINESS, retried, HOST READY only after OK, joins gated, final failure keeps joins closed", Gate);
        Run("07 roles, rejoin, reset: client drone check, scenario cleared with the session, fresh seed per start, F8 unchanged; protocol 37 / mod 0.26.4", Roles);
        Console.WriteLine(_failed == 0 ? "W6A4 TESTS PASSED (" + _passed + ")" : "W6A4 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }
    static int Get(Dictionary<string, int> p, string k) { int v; return p.TryGetValue(k, out v) ? v : 0; }

    // ---------------------------------------------------------------- original models
    static Dictionary<string, int> HostSeed(int index)
    {
        var p = new Dictionary<string, int>();   // MainMenu.StartNewGame skip intro (DeleteAll first)
        p["CurrentLocation"] = 99; p["Story"] = 3; p["Week"] = 1; p["Debt"] = 500000;
        var keys = new List<string>(); var vals = new List<int>();
        DevScenario.SeedKeys(index, keys, vals);
        for (int i = 0; i < keys.Count; i++) p[keys[i]] = vals[i];
        return p;
    }
    // ItemID.Start (Item_<id> -> item_amount), DroneManager.<FindDrone> (Drone -> item with amount > 0 -> AssignDrone),
    // AssignDrone -> ApplyUpgrades (flyby_upgradeN == 1). Returns the equipped drone id (0 = none) and the camera flag.
    static int Equip(Dictionary<string, int> p, out bool camera)
    {
        camera = false;
        int drone = Get(p, "Drone");
        if (drone <= 0 || Get(p, "Item_" + drone) <= 0) return 0;
        camera = drone == 89 && Get(p, "flyby_upgrade3") == 1;   // flyby_upgrade3 = camera_module (level1 DroneShopScript)
        return drone;
    }
    // Player 2's sandbox: the host model's World keys (CampaignPolicy) + Item_<id> from the host-owned record.
    static Dictionary<string, int> P2Sandbox(Dictionary<string, int> host, P2Inventory rec)
    {
        var s = new Dictionary<string, int>();
        foreach (var kv in host) if (CampaignPolicy.Classify(kv.Key) == CampaignKeyClass.World) s[kv.Key] = kv.Value;
        foreach (var kv in rec.Items) s["Item_" + kv.Key] = kv.Value;
        return s;
    }
    // Player2Sync.LoadBook: a NEW record = P2Inventory.Initial + the scenario grant (only while a scenario is active, DevHost).
    static P2Inventory NewRecord(int scenario, bool devHost)
    {
        var r = P2Inventory.Initial(false);
        if (scenario >= 0 && devHost)
            foreach (var pr in ScenarioPrereqs.For(scenario)) if (pr.P2ItemId != 0 && r.CountOf(pr.P2ItemId) < pr.P2ItemCount) r.Set(pr.P2ItemId, pr.P2ItemCount);
        return r;
    }

    // ---------------------------------------------------------------- 01
    static void Table()
    {
        Expect(ScenarioPrereqs.For(15).Length == 0 && ScenarioPrereqs.For(-1).Length == 0, "before the drone step: nothing");
        Expect(ScenarioPrereqs.For(16).Select(p => p.Id).SequenceEqual(new[] { "flyby_drone" }), "index 16: drone (stolen at index 15)");
        Expect(ScenarioPrereqs.For(18).Select(p => p.Id).SequenceEqual(new[] { "flyby_drone", "drone_camera_module" }), "index 18: drone + camera module (bought at index 16)");
        var keys = new List<string>(); var vals = new List<int>(); DevScenario.SeedKeys(18, keys, vals);
        var d = keys.Select((k, i) => new { k, v = vals[i] }).ToDictionary(x => x.k, x => x.v);
        Expect(d["Story"] == 18 && d["StoryBackup"] == 18 && d["Item_89"] == 1 && d["Drone"] == 89 && d["flyby_upgrade3"] == 1 && d.Count == 5, "seed keys for index 18: " + string.Join(", ", keys.ToArray()));
        DevScenario.SeedKeys(5, keys, vals); Expect(keys.Count == 2, "index 5: only the story index");
        var seed = HostSeed(18);
        Expect(DevScenario.VerifySeed(18, k => Get(seed, k)) == null, "seed verified");
        seed.Remove("flyby_upgrade3"); Expect(DevScenario.VerifySeed(18, k => Get(seed, k)) != null, "missing camera module detected");
        seed = HostSeed(18); seed["Item_89"] = 0; Expect(DevScenario.VerifySeed(18, k => Get(seed, k)) != null, "missing drone item detected");
        Expect(ScenarioPrereqs.FlybyDrone == 89 && ScenarioPrereqs.All.All(p => p.Evidence.Length > 20), "item 89, evidence per entry");
        Expect(ScenarioPrereqs.All.All(p => StoryCatalog.Get(p.FromIndex).Support != StepSupport.Unsupported), "prerequisites start at supported steps");
    }

    // ---------------------------------------------------------------- 02
    static void LoadModel()
    {
        bool cam; var host = HostSeed(18);
        Expect(Equip(host, out cam) == 89 && cam, "host: original load equips drone 89 with the camera module");
        Expect(CampaignPolicy.Classify("Drone") == CampaignKeyClass.World && CampaignPolicy.Classify("flyby_upgrade3") == CampaignKeyClass.World && CampaignPolicy.Classify("Item_89") == CampaignKeyClass.PlayerOwnP1, "key classes: Drone / upgrade shared with the campaign, Item_89 personal");
        var p2 = P2Sandbox(host, NewRecord(18, true));
        Expect(Get(p2, "Item_89") == 1 && Equip(p2, out cam) == 89 && cam, "Player 2: own drone item (record) + campaign keys -> own drone equipped with the camera module");
        var p2NoGrant = P2Sandbox(host, NewRecord(-1, true));
        Expect(Equip(p2NoGrant, out cam) == 0, "without the grant Player 2 would have no drone (the reported failure for a fresh game)");
        var fresh = HostSeed(5); Expect(Equip(fresh, out cam) == 0, "index 5: no drone (original state of that step)");
    }

    // ---------------------------------------------------------------- 03
    static void Grant()
    {
        var r = NewRecord(18, true);
        Expect(r.CountOf(89) == 1 && r.Items.Count == 1, "fresh record: exactly one drone");
        var again = r.Clone(); Expect(again.CountOf(89) == 1, "rejoin: same record, no second grant");
        Expect(NewRecord(18, false).CountOf(89) == 0 && NewRecord(-1, true).CountOf(89) == 0 && NewRecord(15, true).CountOf(89) == 0, "never outside a developer host scenario / before the drone step");
        Expect(NewRecord(18, true).CountOf(89) == 1, "reset scenario (fresh folder -> new record): granted once again");
        var src = Strip(Src("Plugin", "Player2Sync.cs"));
        int nb = src.IndexOf("b = new P2Book(P2Inventory.Initial(free), mark);", StringComparison.Ordinal), g = src.IndexOf("foreach (var pr in ScenarioPrereqs.P2Grants(MultiplayerMenu.ActiveScenario, SaveGuard.DevHost, b.Rec.CountOf))", StringComparison.Ordinal);
        int end = src.IndexOf("if (_get(HistoryKey) != b.HistoryId)", StringComparison.Ordinal);
        Expect(nb > 0 && g > nb && g < end, "grant only inside the new-record branch of LoadBook");
        Expect(ScenarioPrereqs.P2Grants(18, true, id => 1).Count == 0 && ScenarioPrereqs.P2Grants(18, true, id => 0).Count == 1, "idempotent grant (W6A.5: ScenarioPrereqs.P2Grants)");
    }

    // ---------------------------------------------------------------- 04
    static void Spawn()
    {
        var cp = new HostCheckpoint { Region = 99, SavedPos = false, HasPos = false, Story = 18, HasLeavePoint = false };
        string note; var normal = SpawnRules.Decide(null, 1, cp, out note);
        Expect(normal.Mode == SpawnMode.OriginalSpawn && normal.Region == SpawnRegions.Fallback && note.Contains("has no usable leave point"), "reproduced: fresh seed -> hideout original spawn (" + note + ")");
        var host = new Vec3(120f, 4f, -35f);
        var t = SpawnRules.TestScenario(normal, 99, true, host, out note);
        Expect(t.Mode == SpawnMode.RestoreNear && t.Region == 99 && t.HasTarget && t.Target.X == 120f && t.Reason == SpawnReason.FirstJoin, "story test: beside the host in Madison (checked ring, never the exact spot)");
        var live = new P2SpawnRecord { HistoryId = 1, Region = 99, Pos = new Vec3(10, 2, 10), Yaw = 0, Context = SpawnContext.None, Seq = 3 };
        var rejoin = SpawnRules.Decide(live, 1, cp, out note);
        Expect(SpawnRules.TestScenario(rejoin, 99, true, host, out note).Mode == SpawnMode.Restore, "rejoin keeps its own last position");
        Expect(SpawnRules.TestScenario(normal, 99, false, host, out note).Mode == SpawnMode.OriginalSpawn && SpawnRules.TestScenario(normal, 93, true, host, out note).Mode == SpawnMode.OriginalSpawn, "no host position / host in a scripted region (jail 93): normal plan");
        var bad = new Vec3(float.NaN, 0, 0); Expect(SpawnRules.TestScenario(normal, 99, true, bad, out note).Mode == SpawnMode.OriginalSpawn, "invalid position refused");
        var cb = Strip(Src("Plugin", "CampaignBootstrap.cs"));
        Expect(cb.Contains("if (MultiplayerMenu.ActiveScenario >= 0 && _node.TestCampaign)") && cb.Contains("plan = SpawnRules.TestScenario(plan, hostRegion, ok, hp, out tn);"), "applied only to a developer story-test session");
    }

    // ---------------------------------------------------------------- 05
    static ScenarioFacts Good()
    {
        var f = new ScenarioFacts { Expected = 18, LiveIndex = 18, LiveKey = "story14", FreeMode = false, Region = 99, EquippedDrone = 89, P2RecordKnown = true, SpawnOk = true, SpawnNote = "ok" };
        f.Prefs["Item_89"] = 1; f.Prefs["Drone"] = 0; f.Prefs["flyby_upgrade3"] = 1; f.P2Items[89] = 1;
        return f;
    }
    static void Check()
    {
        Expect(DevScenario.Check(Good()) == null, "all requirements met");
        var f = Good(); f.LiveIndex = 0; f.LiveKey = "story1_escape";
        Expect(DevScenario.Check(f) != null && DevScenario.Check(f).Contains("Index 0"), "the reported 'Index 0' state (before BeginStory) never passes");
        f = Good(); f.FreeMode = true; Expect(DevScenario.Check(f) != null, "free mode");
        f = Good(); f.Region = 0; Expect(DevScenario.Check(f) != null, "wrong region (hideout)");
        f = Good(); f.EquippedDrone = 0; Expect(DevScenario.Check(f).Contains("Drohne"), "drone not equipped yet (FindDrone runs 0.5 s after load)");
        f = Good(); f.Prefs["flyby_upgrade3"] = 0; Expect(DevScenario.Check(f) != null, "camera module missing");
        f = Good(); f.P2Items.Clear(); Expect(DevScenario.Check(f) != null, "Player 2 has no drone");
        f = Good(); f.P2RecordKnown = false; Expect(DevScenario.Check(f) != null, "Player-2 record not bound");
        f = Good(); f.SpawnOk = false; Expect(DevScenario.Check(f) != null, "invalid Player-2 spawn plan");
        var g5 = new ScenarioFacts { Expected = 5, LiveIndex = 5, LiveKey = "story5b", Region = 99, SpawnOk = true };
        Expect(DevScenario.Check(g5) == null, "a step without prerequisites / objects passes on index + region + spawn");
    }

    // ---------------------------------------------------------------- 06
    static void Gate()
    {
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs")); var code = Strip(Src("Plugin", "MultiplayerMenu.cs"));
        int t = dev.IndexOf("void DevHostTick(GameContext ctx)", StringComparison.Ordinal);
        string tick = dev.Substring(t, dev.IndexOf("ScenarioFacts GatherFacts(int index)", t, StringComparison.Ordinal) - t);
        int wait = tick.IndexOf("if (_hostGateReason != null) { _scenarioState = ScenarioState.Waiting; return; }", StringComparison.Ordinal), check = tick.IndexOf("DevScenario.Check(f)", StringComparison.Ordinal);
        Expect(wait > 0 && check > wait, "no check before HOST_READINESS (StoryManager / scene initialised)");
        Expect(tick.Contains("_nextCheck = now + 0.5;") && tick.Contains("else if (now - _checkSince > 20)"), "retried every 0.5 s, final after 20 s");
        int ok = tick.IndexOf("if (bad == null)", StringComparison.Ordinal), ready = tick.IndexOf("TEST_CAMPAIGN HOST READY", ok, StringComparison.Ordinal), fail = tick.IndexOf("else if (now - _checkSince > 20)", StringComparison.Ordinal);
        Expect(ok > 0 && ready > ok && ready < fail, "HOST READY only in the OK branch");
        int n = 0, at = 0; while ((at = dev.IndexOf("HOST READY:", at, StringComparison.Ordinal)) >= 0) { n++; at++; }
        Expect(n == 2 && tick.Contains("if (_checkIndex < 0)"), "the other HOST READY is only for sessions without a scenario (copy mode)");
        Expect(tick.Contains("ScenarioInvalid(bad);"), "final failure closes joins permanently with the reason (W6A.5)");
        Expect(code.Contains("node.HostGate = () => _gateNow;") && code.Contains("node.HostGateFatal = () => JoinGate.Fatal(_scenarioState);"), "joins read the one readiness state (W6A.5)");
        Expect(dev.Contains("if (bad != null) { _devError = \"Story-Testspielstand ungültig: \" + bad; ScenarioInvalid("), "an invalid seed closes the gate too");
        Expect(dev.Contains("GatherFacts(_checkIndex)") && dev.Contains("f.EquippedDrone = EquippedDrone();") && dev.Contains("f.P2RecordKnown = rec != null;") && dev.Contains("f.SpawnOk = plan.Mode == SpawnMode.RestoreNear"), "facts: live story, drone, Player-2 record, spawn plan");
    }

    // ---------------------------------------------------------------- 07
    static void Roles()
    {
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs")); var code = Strip(Src("Plugin", "MultiplayerMenu.cs")); var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs"));
        Expect(code.Contains("if (_joinKind == JoinKind.Developer && _lc.State == MpState.Connected) DevClientTick(ctx);") && dev.Contains("TEST_SCENARIO Player 2 check OK: own drone"), "Player 2 checks its own drone");
        Expect(code.Contains("_hostCode = null; _hint = null; _devHosting = false; ClearScenario(); _p2CheckState = 0;"), "scenario cleared when the session stops (no grant / spawn override leaks into a later session)");
        Expect(dev.Contains("_devError = null; _readyLogged = false; ClearScenario();"), "every start begins without a scenario");
        DevPlan a, b;
        Expect(DevScenario.Plan(18, "C:\\x\\TS2MP", true, "C:\\y", "C:\\z", "1", out a) == null && a.MoveAsideTo != null && DevScenario.Plan(18, "C:\\x\\TS2MP", false, "C:\\y", "C:\\z", "2", out b) == null && b.MoveAsideTo == null, "reset: old seed moved aside, fresh seed + fresh records");
        Expect(ui.Contains("StartHost(false);") && ui.Contains("StartJoin(JoinKind.Public);") && code.Contains("else node.JoinKey = JoinAuth.DeriveKey(code);"), "F8 host / join unchanged");
        Expect(ui.Contains("var m = Event.current.mousePosition;") && ui.Contains("DrawPointer();"), "cursor implementation unchanged");
        Expect(ProtocolInfo.ProtocolVersion == 38 && ProtocolInfo.ModVersion == "0.27.1", "protocol 37 / mod 0.26.4");
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
        var sb = new System.Text.StringBuilder();
        foreach (var line in s.Split('\n')) { int i = line.IndexOf("//", StringComparison.Ordinal); sb.Append(i >= 0 ? line.Substring(0, i) : line).Append('\n'); }
        return sb.ToString();
    }
}
