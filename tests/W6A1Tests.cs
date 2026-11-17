// W6A.1 offline tests: public F8 multiplayer menu vs developer F10 menu. Drives the Unity-free rules the plugin uses
// (MenuRouting / MenuRules in src/Shared/MultiplayerLifecycle.cs, SaveIsolation.ResetDestination, StoryCatalog) and
// source-scans the Unity glue (src/Plugin/MultiplayerMenu*.cs, Plugin.cs) for the routing / gating / isolation order.
// In-game look, focus and click behaviour are manual QA (docs/TESTING.md §50).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TS2MP.Shared;

static class W6A1Tests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("01 hotkeys: F8 / F10 never both open, same key closes, F10 ignored unless enabled, other key ignored while typing, nothing opens outside the main menu", Hotkeys);
        Run("02 session routing: F8 joins only normal hosts, F10 only test campaigns, DEV_AUTOJOIN any; F8 host / join refused in a test process", Routing);
        Run("03 developer menu gating: config default off, never in launcher mode, every developer action behind the gate, F8 code has no developer feature", Gating);
        Run("04 save isolation: Dev Host prepares + verifies the isolated copy before the story step and the session; reset moves the test folder aside only while unused", Isolation);
        Run("05 story catalog: 81 original steps 0..80 (keys = mission_catalog.json), support classes, unsupported reasons + alternatives, selector refusals", Catalog);
        Run("06 story step application: copy armed + verified, original keys only (Story / StoryBackup), no rewards / debt / teleport / XP writes, failure releases the copy", StoryApply);
        Run("07 public menu content + interaction safety: styled IMGUI only in menu files, cached strings, input block restored, loading screen hand-over, no new wire", PublicMenu);
        Run("08 regressions: W6A story14 -> story15 path and W2B parcel fallback unchanged; protocol 37 / mod 0.26.1", Regressions);
        Console.WriteLine(_failed == 0 ? "W6A1 TESTS PASSED (" + _passed + ")" : "W6A1 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- 01
    static void Hotkeys()
    {
        var N = MenuPanel.None; var P = MenuPanel.Public; var D = MenuPanel.Developer;
        Expect(MenuRouting.Toggle(N, P, false, false, true) == P && MenuRouting.Toggle(P, P, false, false, true) == N, "F8 opens / closes");
        Expect(MenuRouting.Toggle(N, D, false, false, true) == N && MenuRouting.Toggle(P, D, false, false, true) == P, "F10 ignored while the developer menu is disabled");
        Expect(MenuRouting.Toggle(N, D, true, false, true) == D && MenuRouting.Toggle(D, D, true, false, true) == N, "F10 opens / closes when enabled");
        Expect(MenuRouting.Toggle(P, D, true, false, true) == D && MenuRouting.Toggle(D, P, true, false, true) == P, "switching replaces the panel (never two)");
        Expect(MenuRouting.Toggle(P, D, true, true, true) == P && MenuRouting.Toggle(D, P, true, true, true) == D, "the other menu's key is ignored while typing");
        Expect(MenuRouting.Toggle(P, P, true, true, true) == N, "own key still closes while typing");
        Expect(MenuRouting.Toggle(N, P, true, false, false) == N && MenuRouting.Toggle(N, D, true, false, false) == N && MenuRouting.Toggle(D, P, true, false, false) == N, "outside the main menu nothing opens");
        Expect(MenuRouting.Toggle(P, MenuPanel.None, true, false, true) == P, "no key: unchanged");
        foreach (MenuPanel cur in Enum.GetValues(typeof(MenuPanel))) foreach (MenuPanel k in Enum.GetValues(typeof(MenuPanel))) foreach (bool dev in new[] { false, true }) foreach (bool typing in new[] { false, true }) foreach (bool main in new[] { false, true })
        {
            var next = MenuRouting.Toggle(cur, k, dev, typing, main);
            Expect(dev || next != D || cur == D, "developer panel only when enabled");
            Expect(main || next == N || next == cur, "no new panel outside the main menu");
        }
    }

    // ---------------------------------------------------------------- 02
    static void Routing()
    {
        Expect(MenuRouting.JoinMismatch(JoinKind.Public, false) == null && MenuRouting.JoinMismatch(JoinKind.Public, true) == MenuRouting.PublicJoinTestHostText, "F8 join: normal host only");
        Expect(MenuRouting.JoinMismatch(JoinKind.Developer, true) == null && MenuRouting.JoinMismatch(JoinKind.Developer, false) == MenuRouting.DevJoinNormalHostText, "F10 join: test campaign only");
        Expect(MenuRouting.JoinMismatch(JoinKind.Any, true) == null && MenuRouting.JoinMismatch(JoinKind.Any, false) == null, "DEV_AUTOJOIN hook accepts both (launcher tests)");
        var mm = GameContext.MainMenu;
        Expect(MenuRouting.PublicHost(mm, MpState.Offline, false, false, false).Enabled && !MenuRouting.PublicHost(mm, MpState.Offline, false, false, true).Enabled, "F8 host refused in a test-campaign process");
        Expect(MenuRouting.PublicHost(mm, MpState.Offline, false, false, true).Reason == MenuRouting.DevActiveText, "with the reason");
        Expect(MenuRouting.PublicJoin(mm, MpState.Offline, false, true, false).Enabled && !MenuRouting.PublicJoin(mm, MpState.Offline, false, true, true).Enabled, "F8 join refused in a test-campaign process");
        Expect(!MenuRouting.PublicHost(mm, MpState.HostReady, false, false, false).Enabled && !MenuRouting.PublicHost(GameContext.InWorld, MpState.Offline, false, false, false).Enabled, "no second session, main menu only (existing rules kept)");
        Expect(!MenuRouting.PublicJoin(mm, MpState.ClientConnecting, false, true, false).Enabled && !MenuRouting.PublicJoin(mm, MpState.Offline, false, false, false).Enabled, "busy / no isolation refused");
        var code = Menu();
        int pr = code.IndexOf("void Prepare(SessionNode node, GameContext ctx, double now)", StringComparison.Ordinal);
        string prep = code.Substring(pr, code.IndexOf("bool ContinueOriginal()", pr, StringComparison.Ordinal) - pr);
        int mis = prep.IndexOf("MenuRouting.JoinMismatch(_joinKind, test)", StringComparison.Ordinal), sel = prep.IndexOf("SaveGuard.SelectClientSandbox(", StringComparison.Ordinal), arm = prep.IndexOf("SaveGuard.Arm(out why)", StringComparison.Ordinal);
        Expect(mis > 0 && sel > mis && arm > sel && prep.IndexOf("node.AbortPreparation(\"campaign kind differs from the menu\")", StringComparison.Ordinal) > mis, "kind checked after Welcome, before any sandbox is selected or armed");
        Expect(code.Contains("string kindErr = MenuRouting.JoinMismatch(_joinKind, node.HostTestCampaign);"), "re-join in an armed process checks the kind too");
        int sj = code.IndexOf("void StartJoin(JoinKind kind)", StringComparison.Ordinal);
        string join = code.Substring(sj, code.IndexOf("bool TestActive", sj, StringComparison.Ordinal) - sj);
        Expect(join.IndexOf("_joinKind = kind;", StringComparison.Ordinal) < join.IndexOf("new SessionNode(Role.Client", StringComparison.Ordinal) && join.Contains("if (kind == JoinKind.Public && TestActive)") && join.Contains("if (kind == JoinKind.Developer && !_devAllowed)"), "join kind fixed before the node starts; gates");
        Expect(code.Contains("StartJoin(JoinKind.Any);"), "DEV_AUTOJOIN uses JoinKind.Any");
    }

    // ---------------------------------------------------------------- 03
    static void Gating()
    {
        Expect(!MenuRouting.DevMenuAllowed(false, false) && !MenuRouting.DevMenuAllowed(true, true) && MenuRouting.DevMenuAllowed(true, false), "allowed only when configured, never in launcher mode");
        var plugin = Strip(Src("Plugin", "Plugin.cs"));
        Expect(plugin.Contains("Config.Bind(\"Developer\", \"EnableDeveloperMenu\", false,"), "config default off");
        Expect(plugin.Contains("\"DeveloperMenuKey\", \"F10\"") && plugin.Contains("settings.DevMenuKey = devKey == settings.MenuKey ? KeyCode.F10 : devKey;"), "F10 default, never the F8 key");
        Expect(plugin.IndexOf("\"F12\"", StringComparison.Ordinal) < 0, "no F12 binding (Steam screenshots)");
        var pkg = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Root()), "tools", "package.ps1"));
        Expect(pkg.IndexOf(".cfg", StringComparison.OrdinalIgnoreCase) < 0 || pkg.IndexOf("EnableDeveloperMenu", StringComparison.Ordinal) < 0, "release package ships no config that enables the developer menu");
        Expect(pkg.IndexOf("EnableDeveloperMenu = true", StringComparison.Ordinal) < 0, "never enabled by packaging");
        var code = Menu(); var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs")); var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        Expect(code.Contains("_devAllowed = MenuRouting.DevMenuAllowed(settings.DevMenuEnabled, legacy);") && code.Contains("else if (_devAllowed && Input.GetKeyDown(_settings.DevMenuKey)) Hotkey(MenuPanel.Developer);"), "F10 read only when allowed");
        Expect(code.Contains("if (test && !_devAllowed) { _hint = \"Entwicklermenü ist deaktiviert.\"; return; }"), "test host refused without the developer menu");
        Expect(ui.Contains("if (_panel == MenuPanel.Developer) { DrawDeveloper(); DrawPointer(); return; }")   /* W6A.2: + own pointer */, "developer window drawn only for the developer panel");
        foreach (var bad in new[] { "StartHost(true)", "JoinKind.Developer", "StoryCatalog", "_devStory", "PrepareTestCampaign", "ResetScenario", "Testkampagne hosten", "DevInfo" })
            Expect(ui.IndexOf(bad, StringComparison.Ordinal) < 0, "F8 menu has no developer feature: " + bad);
        Expect(ui.Contains("StartHost(false);") && ui.Contains("StartJoin(JoinKind.Public);"), "F8 = normal host / join");
        Expect(dev.Contains("StartHost(true);") && dev.Contains("StartJoin(JoinKind.Developer);") && dev.Contains("ResetScenario();"), "F10 = test host / join / reset");
        Expect(code.IndexOf("DrawTestSection", StringComparison.Ordinal) < 0 && code.IndexOf("\"Testkampagne hosten\"", StringComparison.Ordinal) < 0, "old F8 test section removed (capability moved, not deleted)");
        Expect(dev.Contains("bool PrepareTestCampaign(DevPlan plan)") && dev.Contains("SaveGuard.PrepareTestHostSave(dir, plan.SeedFromReal, out why)") && dev.Contains("_runner.EnableTestCampaign(dir,"), "test campaign preparation preserved in the developer menu");
    }

    // ---------------------------------------------------------------- 04
    static void Isolation()
    {
        string why; string test = Path.Combine(Path.GetTempPath(), "ts2mp-w6a1", "TS2MP", "testcampaign");
        string real = Path.Combine(Path.GetTempPath(), "ts2mp-w6a1", "LocalLow", "CookieDev", "Thief Simulator 2"), game = Path.Combine(Path.GetTempPath(), "ts2mp-w6a1", "Game");
        var dst = SaveIsolation.ResetDestination(test, real, game, "20261117-120000", out why);
        Expect(dst != null && dst == SaveIsolation.Norm(test) + ".reset-20261117-120000" && Path.GetDirectoryName(dst) == Path.GetDirectoryName(SaveIsolation.Norm(test)), "moved to a sibling folder");
        Expect(SaveIsolation.ResetDestination(Path.Combine(test, ".."), real, game, "1", out why) == null, "only the testcampaign folder");
        Expect(SaveIsolation.ResetDestination(Path.Combine(real, "testcampaign"), real, game, "1", out why) == null && SaveIsolation.ResetDestination(Path.Combine(game, "testcampaign"), real, game, "1", out why) == null, "never inside the real save / game folder");
        Expect(SaveIsolation.ResetDestination(test, real, game, "..\\x", out why) == null && SaveIsolation.ResetDestination(test, real, game, "", out why) == null && SaveIsolation.ResetDestination(null, real, game, "1", out why) == null, "bad stamp / empty refused");
        var mm = GameContext.MainMenu;
        Expect(MenuRouting.DevReset(mm, MpState.Offline, false, false, false).Enabled, "reset in a fresh main menu");
        Expect(!MenuRouting.DevReset(mm, MpState.Offline, false, true, false).Enabled && !MenuRouting.DevReset(mm, MpState.HostReady, false, false, false).Enabled
            && !MenuRouting.DevReset(mm, MpState.Offline, false, false, true).Enabled && !MenuRouting.DevReset(GameContext.InWorld, MpState.Offline, false, false, false).Enabled
            && !MenuRouting.DevReset(mm, MpState.Offline, true, false, false).Enabled, "never while armed / in a session / after a world load / outside the menu / launcher");
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        int rs = dev.IndexOf("void ResetScenario()", StringComparison.Ordinal);
        string reset = dev.Substring(rs, dev.IndexOf("void DrawDeveloper()", rs, StringComparison.Ordinal) - rs);
        Expect(reset.IndexOf("MenuRouting.DevReset(", StringComparison.Ordinal) < reset.IndexOf("SaveIsolation.ResetDestination(", StringComparison.Ordinal) && reset.IndexOf("SaveIsolation.ResetDestination(", StringComparison.Ordinal) < reset.IndexOf("Directory.Move(dir, dst);", StringComparison.Ordinal), "gate -> path check -> move");
        foreach (var bad in new[] { "Directory.Delete", "File.Delete" }) Expect(reset.IndexOf(bad, StringComparison.Ordinal) < 0, "reset never deletes: " + bad);
        var code = Menu();
        int sh = code.IndexOf("void StartHost(bool test)", StringComparison.Ordinal);
        string host = code.Substring(sh, code.IndexOf("void StartJoin(JoinKind kind)", sh, StringComparison.Ordinal) - sh);
        int prep = host.IndexOf("if (test && !DevPrepare(out plan)) return;", StringComparison.Ordinal),
            req = host.IndexOf("_lc.RequestHost(ctx, _port, now)", StringComparison.Ordinal), start = host.IndexOf("node.Start()", StringComparison.Ordinal), load = host.IndexOf("if (test) DevAutoLoad(plan);", StringComparison.Ordinal);
        Expect(prep > 0 && req > prep && start > req && load > start, "W6A.3: plan + isolated sandbox -> host session -> automatic original load");
        Expect(host.Contains("node.TestCampaign = _lc.TestPrepared && SaveGuard.DevHost && _runner.TestCampaignActive;"), "test flag in Welcome only for a prepared, armed copy");
    }

    // ---------------------------------------------------------------- 05
    static readonly int[] Unsupported = { 0, 1, 2, 20, 27, 28, 35, 39, 42, 47, 52, 59, 73, 74, 75, 80 };
    static void Catalog()
    {
        Expect(StoryCatalog.Count == 81 && StoryCatalog.Steps.Length == 81, "81 steps");
        for (int i = 0; i < 81; i++) Expect(StoryCatalog.Steps[i].Index == i && !string.IsNullOrEmpty(StoryCatalog.Steps[i].Key) && !string.IsNullOrEmpty(StoryCatalog.Steps[i].Text), "step " + i + " present");
        var json = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Root()), "docs", "game-atlas", "data", "mission_catalog.json"));
        var ms = Regex.Matches(json, "\"id\": \"story:(\\d+)\",\\s*\"index\": (\\d+),\\s*\"key\": \"([^\"]+)\",\\s*\"text_en\": \"([^\"]*)\"");
        Expect(ms.Count == 81, "mission_catalog.json story entries: " + ms.Count);
        foreach (Match m in ms)
        {
            int i = int.Parse(m.Groups[2].Value);
            Expect(StoryCatalog.Steps[i].Key == m.Groups[3].Value && StoryCatalog.Steps[i].Text == m.Groups[4].Value.Replace("\\\"", "\""), "step " + i + " key / text = mission_catalog.json");
        }
        Expect(StoryCatalog.Steps[18].Key == "story14" && StoryCatalog.Steps[19].Key == "story15" && StoryCatalog.Steps[80].Key == "story73_freemode", "W6A anchors");
        var uns = StoryCatalog.Steps.Where(s => s.Support == StepSupport.Unsupported).Select(s => s.Index).ToArray();
        Expect(uns.SequenceEqual(Unsupported), "unsupported set: " + string.Join(",", uns.Select(x => x.ToString()).ToArray()));
        foreach (var s in StoryCatalog.Steps)
        {
            if (s.Support != StepSupport.Supported) Expect(s.Reason.Length > 20, "step " + s.Index + " explains its class");
            Expect((StoryCatalog.Refusal(s.Index) == null) == (s.Support != StepSupport.Unsupported), "selector refuses exactly the unsupported step " + s.Index);
        }
        foreach (int i in new[] { 20, 27, 28, 35, 39, 42, 47, 52, 59, 73, 74, 75 }) Expect(StoryCatalog.Steps[i].Reason.Contains("Select step"), "step " + i + " names the step to select instead");
        Expect(StoryCatalog.Steps[18].Support == StepSupport.Supported && StoryCatalog.Steps[19].Support == StepSupport.Supported, "W6A slice steps supported");
        Expect(StoryCatalog.Steps[3].Support == StepSupport.Limited && StoryCatalog.Steps[12].Support == StepSupport.Limited && StoryCatalog.Steps[40].Reason.Contains("debt"), "special starts named (teleport, chase, debt)");
        Expect(StoryCatalog.Refusal(-1) != null && StoryCatalog.Refusal(81) != null && StoryCatalog.Get(81) == null, "out of range refused");
        Expect(MenuRouting.DevCopyRefusal(true) == null && MenuRouting.DevCopyRefusal(false) != null, "copy mode needs a save (W6A.3: story steps never use the copy)");
    }

    // ---------------------------------------------------------------- 06
    static void StoryApply()
    {
        // W6A.3: the step is written right after the ORIGINAL StartNewGame ran inside the verified sandbox (tests/W6A3Tests.cs).
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        int a = dev.IndexOf("void DevAutoLoad(DevPlan plan)", StringComparison.Ordinal);
        string body = dev.Substring(a, dev.IndexOf("void DevHostTick(GameContext ctx)", a, StringComparison.Ordinal) - a);
        int guard = body.IndexOf("if (!SaveGuard.Armed || !SaveGuard.DevHost || !SaveGuard.RuntimeCheck())", StringComparison.Ordinal), start = body.IndexOf("_startNewGame.Invoke(mm, null);", StringComparison.Ordinal),
            write = body.IndexOf("for (int k = 0; k < keys.Count; k++) _spSet(keys[k], vals[k]);", StringComparison.Ordinal);
        Expect(guard > 0 && start > guard && write > start, "sandbox verified -> original new game -> index written");
        int sets = 0, at = 0; while ((at = body.IndexOf("_spSet(", at, StringComparison.Ordinal)) >= 0) { sets++; at++; }
        Expect(sets == 1 && body.Contains("DevScenario.SeedKeys(plan.Story, keys, vals);"), "W6A.4: only DevScenario.SeedKeys (index + verified prerequisites) are written (" + sets + ")");
        foreach (var bad in new[] { "AdvanceStory(", "InitiateStory(", "_advance(", "_initiate(", "debtAm", "moneyAm", "AddXP", "StartPoliceChase", "_deduct", "_Call", "mission_" })
            Expect(body.IndexOf(bad, StringComparison.Ordinal) < 0, "story preparation never touches " + bad);
        var cb = Strip(Src("Shared", "Campaign.cs"));
        Expect(cb.Contains("TryInt(\"Story\""), "campaign snapshot carries the host's Story");
        var ss = Strip(Src("Plugin", "StorySync.cs"));
        Expect(ss.Contains("_setStory(sm, target); _initiate(sm); _saveStory(sm);"), "Player 2 follows the host index (W6A)");
    }

    // ---------------------------------------------------------------- 07
    static void PublicMenu()
    {
        var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs"));
        Expect(ui.Contains("Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f)") && ui.Contains("GUI.matrix = Matrix4x4.Scale(") && ui.Contains("GUI.matrix = old;"), "scaled to the screen, matrix restored");
        Expect(ui.Contains("if (_styles == null) _styles = BuildStyles();") && ui.Contains("if (_footer == null)"), "styles / footer built once");
        Expect(ui.Contains("Fast.Getter<object>(_menuType, \"currentProfileTxt\")") && ui.Contains("Fast.Getter<object>(_menuType, \"audioClick\")"), "game font + click sound reused");
        Expect(ui.Contains("_blockTried = true;") && ui.Contains("if (_blockedEs != null && Alive(_blockedEs)) _blockedEs.enabled = true;"), "one EventSystem lookup per opening, restored on close");
        var code = Menu();
        Expect(code.Contains("if (show && !_loading.Shown) ReleaseInputBlock();"), "menu input block handed to the join loading screen before it records the EventSystem");
        Expect(code.Contains("SyncInputBlock();") && code.Contains("DisposeUi();"), "per-frame sync + texture cleanup");
        Expect(ui.Contains("CloseMenu(\"button\")") && ui.Contains("\"Spiel hosten\"") && ui.Contains("\"Spiel beitreten\"") && ui.Contains("\"BEITRITTSCODE\"") && ui.Contains("ProtocolInfo.ModVersion") && ui.Contains("_lc.Error"), "host / join / code / version / errors / close");
        foreach (var bad in new[] { "FindObjectsOfType", "GameObject.Find(", "Resources.FindObjectsOfTypeAll" }) Expect(ui.IndexOf(bad, StringComparison.Ordinal) < 0, "no scene-wide search in the menu UI: " + bad);
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        Expect(dev.Contains("if (_devInfo != null && now < _devInfoAt) return _devInfo;") && dev.Contains("if (_stepLabels == null)"), "developer status / list strings cached");
        Expect(ProtocolInfo.ProtocolVersion == 39, "no wire change (protocol 37)");
    }

    // ---------------------------------------------------------------- 08
    static void Regressions()
    {
        Expect(StoryTriggers.All.Length == 1 && StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, "DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3") != null, "W6A trigger table unchanged");
        string why; var r = new StoryAdvanceRequest { FromIndex = 18, StageKey = "story14", Kind = StoryTriggerKind.ReadNote, Path = "Interior_103/story object 103 notka/pPlane3" };
        Expect(StoryRules.Decide(true, false, 18, "story14", r, out why) == ProposalCode.Accepted && StoryRules.Decide(true, false, 19, "story15", r, out why) == ProposalCode.Stale, "W6A decision unchanged");
        var ps = Strip(Src("Plugin", "ParcelSync.cs"));
        Expect(ps.Contains("if (!i._host && !StorySync.ClientHandlesStory && _noteAdvance(__instance)) { _setNoteAdvance(__instance, false);"), "W2B fallback unchanged");
        Expect(ProtocolInfo.ProtocolVersion == 39 && ProtocolInfo.ModVersion == "0.28.0", "protocol 37 / mod 0.26.1");
    }

    // ---------------------------------------------------------------- helpers
    static string Menu() { return Strip(Src("Plugin", "MultiplayerMenu.cs")); }
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
