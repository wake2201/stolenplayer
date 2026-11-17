// W6A.3 offline tests: reliable isolated story-test startup. Drives DevScenario (plan, aside target, seed + world checks),
// a model of the ORIGINAL MainMenu.StartNewGame skip-intro branch (IL 1.27c) and StoryManager.Start / BeginStory on prefs
// dictionaries (genuine save, isolated copy, story seed), real LiteNetLib handshakes for the developer host readiness gate,
// and source scans of the Unity glue (src/Plugin/MultiplayerMenu*.cs, SaveGuard.cs). In-game load + note_103: docs/TESTING.md §52.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TS2MP.Shared;

static class W6A3Tests
{
    static int _passed, _failed, _port = 37720;
    static string _logDir;
    const string Game = "1.27c";
    static readonly string Data = Path.Combine(Path.GetTempPath(), "ts2mp-w6a3", "TS2MP");
    static readonly string Real = Path.Combine(Path.GetTempPath(), "ts2mp-w6a3", "LocalLow", "CookieDev", "Thief Simulator 2");
    static readonly string GameDir = Path.Combine(Path.GetTempPath(), "ts2mp-w6a3", "Game");

    static int Main()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ts2mp-w6a3-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDir);
        Run("01 genuine save in free mode (FreeMode = 1): story seed never reads it, seed is story mode on index 18, genuine prefs unchanged", GenuineFreeMode);
        Run("02 existing isolated copy / old story seed in free mode: copy mode untouched, story start moves the old seed aside and starts a fresh new game", ExistingCopyFreeMode);
        Run("03 missing / invalid seed: copy without save, unsupported index, wrong prefs after the seed, wrong world after the load -> visible refusal", InvalidSeed);
        Run("04 story14 setup: index 18 label (index + key + objective), seed -> BeginStory index 18, note_103 trigger listed, W6A decision story14 -> story15", Story14);
        Run("05 setup failure, cancel, retry: every failure is a persistent F10 error + released sandbox; retry gets a fresh seed", FailureRetry);
        Run("06 developer host readiness: client waits (HostNotReady, retried) until the host world is ready, then connects without a code", Readiness);
        Run("07 no writes to genuine folders: seed paths, aside target, SaveGuard never copies for the seed, StartNewGame only inside the verified sandbox", NoGenuineWrites);
        Run("08 normal F8 unaffected: public host / join paths, keys and routing unchanged; protocol 37 / mod 0.26.3", F8Unaffected);
        Console.WriteLine(_failed == 0 ? "W6A3 TESTS PASSED (" + _passed + ")" : "W6A3 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- original IL models
    // MainMenu.StartNewGame IL_0000..IL_0135 (skipIntro = true): DeleteAll, CurrentLocation 99, Story 3, defaults, Save.
    static void StartNewGame(Dictionary<string, int> prefs, bool skipIntro)
    {
        prefs.Clear();
        if (!skipIntro) prefs["CurrentLocation"] = -1;
        else { prefs["CurrentLocation"] = 99; prefs["Story"] = 3; }
        prefs["Week"] = 1; prefs["Money"] = 0; prefs["Debt"] = 500000; prefs["RentTimer"] = 164; prefs["BatonLife"] = 100; prefs["CrowbarLife"] = 100; prefs["NextLevelRep"] = 750; prefs["FadeLoad"] = 1;
    }
    // MainMenu.StartFreeMode IL_0000..IL_0040 (the genuine save of the user)
    static Dictionary<string, int> FreeModeSave() { return new Dictionary<string, int> { { "CurrentLocation", 0 }, { "FreeMode", 1 }, { "Story", 80 }, { "StoryBackup", 80 }, { "Money", 200 } }; }
    static int Get(Dictionary<string, int> p, string k) { int v; return p.TryGetValue(k, out v) ? v : 0; }
    // StoryManager.Start (FreeMode -> isFreeMode) + BeginStory (Story clamped) + InitiateStory (free -> 80)
    static void Load(Dictionary<string, int> p, out int index, out string key, out bool free)
    {
        free = Get(p, "FreeMode") == 1;
        index = Math.Min(Get(p, "Story"), StoryCatalog.Count - 1);
        if (free) index = 80;
        key = StoryCatalog.Get(index).Key;
    }
    // what DevAutoLoad does after the original new game: Story / StoryBackup = index
    static Dictionary<string, int> Seed(int index)
    {
        var p = new Dictionary<string, int> { { "garbage", 7 }, { "FreeMode", 1 } };   // whatever an old sandbox held
        StartNewGame(p, true);
        p["Story"] = index; p["StoryBackup"] = index;
        return p;
    }

    // ---------------------------------------------------------------- 01
    static void GenuineFreeMode()
    {
        var genuine = FreeModeSave(); var before = new Dictionary<string, int>(genuine);
        DevPlan plan;
        Expect(DevScenario.Plan(18, Data, false, Real, GameDir, "20261117-100000", out plan) == null, "plan ok");
        Expect(plan.Mode == DevMode.StorySeed && !plan.SeedFromReal && plan.Story == 18 && plan.DataDir == Path.Combine(Data, "testcampaign", "story"), "story seed in <data>/testcampaign/story, not seeded from the genuine save");
        var seed = Seed(18);
        Expect(DevScenario.VerifySeedPrefs(18, Get(seed, "Story"), Get(seed, "StoryBackup"), Get(seed, "FreeMode"), Get(seed, "CurrentLocation")) == null, "seed: FreeMode 0, Story / StoryBackup 18, Madison");
        int idx; string key; bool free; Load(seed, out idx, out key, out free);
        Expect(idx == 18 && key == "story14" && !free && DevScenario.VerifyLoaded(18, idx, key, free) == null, "loaded world on story14, not free mode");
        Expect(genuine.Count == before.Count && genuine.All(kv => before[kv.Key] == kv.Value), "genuine prefs unchanged");
        Load(genuine, out idx, out key, out free);
        Expect(free && idx == 80 && DevScenario.VerifyLoaded(18, idx, key, free) != null, "W6A.2 root cause reproduced: a copy of this save can only be free mode (index 80)");
    }

    // ---------------------------------------------------------------- 02
    static void ExistingCopyFreeMode()
    {
        DevPlan copy, story;
        Expect(DevScenario.Plan(-1, Data, true, Real, GameDir, "1", out copy) == null && copy.Mode == DevMode.SaveCopy && copy.SeedFromReal && copy.DataDir == Path.Combine(Data, "testcampaign") && copy.MoveAsideTo == null, "copy mode: the W4.2B.2 copy, nothing moved");
        Expect(DevScenario.Plan(18, Data, true, Real, GameDir, "20261117-101500", out story) == null && story.MoveAsideTo == SaveIsolation.Norm(Path.Combine(Data, "testcampaign", "story")) + ".old-20261117-101500", "old story seed moved aside (never deleted)");
        Expect(story.DataDir != copy.DataDir && !story.DataDir.Equals(Path.Combine(Data, "testcampaign", "host-save"), StringComparison.OrdinalIgnoreCase), "the free-mode copy (testcampaign/host-save) is never the story seed");
        var seed = Seed(18);
        Expect(!seed.ContainsKey("garbage") && Get(seed, "FreeMode") == 0, "original DeleteAll clears whatever an old sandbox held (FreeMode 1 included)");
    }

    // ---------------------------------------------------------------- 03
    static void InvalidSeed()
    {
        Expect(MenuRouting.DevCopyRefusal(false) != null && MenuRouting.DevCopyRefusal(true) == null, "copy mode without a save refused");
        DevPlan p;
        foreach (int u in new[] { 0, 1, 2, 20, 27, 35, 80 }) Expect(DevScenario.Plan(u, Data, false, Real, GameDir, "1", out p) != null && p == null, "unsupported index " + u + " refused before anything changes");
        Expect(DevScenario.Plan(81, Data, false, Real, GameDir, "1", out p) != null && DevScenario.Plan(18, null, false, Real, GameDir, "1", out p) != null, "out of range / no data folder refused");
        Expect(DevScenario.VerifySeedPrefs(18, 18, 18, 1, 99) != null && DevScenario.VerifySeedPrefs(18, 3, 18, 0, 99) != null && DevScenario.VerifySeedPrefs(18, 18, 3, 0, 99) != null && DevScenario.VerifySeedPrefs(18, 18, 18, 0, -1) != null, "seed prefs: free mode / wrong Story / StoryBackup / intro location refused");
        var noSkip = new Dictionary<string, int>(); StartNewGame(noSkip, false); noSkip["Story"] = 18; noSkip["StoryBackup"] = 18;
        Expect(DevScenario.VerifySeedPrefs(18, 18, 18, 0, Get(noSkip, "CurrentLocation")) != null, "a new game WITH intro (CurrentLocation -1) is detected");
        Expect(DevScenario.VerifyLoaded(18, 80, "story73_freemode", true) != null && DevScenario.VerifyLoaded(18, 19, "story15", false) != null && DevScenario.VerifyLoaded(18, 18, "story15", false) != null, "world check: free mode / wrong index / wrong key");
    }

    // ---------------------------------------------------------------- 04
    static void Story14()
    {
        Expect(DevScenario.Label(18) == "Index 18 · story14 · Disable the 104 entrance camera with your drone", "index 18 shown with its key and objective: " + DevScenario.Label(18));
        Expect(DevScenario.Label(14).StartsWith("Index 14 · story10 · Buy a basic lockpicking tool", StringComparison.Ordinal), "index 14 is story10 (the lockpick step), not story14");
        Expect(StoryCatalog.Get(18).Support == StepSupport.Supported, "index 18 supported");
        var seed = Seed(18); int idx; string key; bool free; Load(seed, out idx, out key, out free);
        Expect(idx == 18 && key == "story14", "seed loads story14");
        Expect(StoryTriggers.Find(key, StoryTriggerKind.ReadNote, "DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3") != null, "note_103 is a listed shared trigger for this stage (StoryObject shows it while story14 is current)");
        string why; var r = new StoryAdvanceRequest { FromIndex = idx, StageKey = key, Kind = StoryTriggerKind.ReadNote, Path = "Interior_103/story object 103 notka/pPlane3" };
        Expect(StoryRules.Decide(true, free, idx, key, r, out why) == ProposalCode.Accepted, "Player 2's note_103 read is accepted on the seeded host");
        Expect(StoryRules.Decide(true, true, 80, "story73_freemode", r, out why) != ProposalCode.Accepted, "and would NOT be on the free-mode copy (W6A.2 failure)");
        Expect(StoryCatalog.Get(idx + 1).Key == "story15", "next step story15");
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        Expect(dev.Contains("GameObject.Find(\"story object 103 notka\")") && dev.Contains("if (StoryCatalog.Get(expect).Key == \"story14\")"), "host world check reports note_103 presence for story14");
        Expect(dev.Contains("int _devStory = 18"), "F10 preselects index 18");
    }

    // ---------------------------------------------------------------- 05
    static void FailureRetry()
    {
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs")); var code = Strip(Src("Plugin", "MultiplayerMenu.cs"));
        int a = dev.IndexOf("bool DevPrepare(out DevPlan plan)", StringComparison.Ordinal);
        string prep = dev.Substring(a, dev.IndexOf("bool PrepareTestCampaign(DevPlan plan)", a, StringComparison.Ordinal) - a);
        Expect(prep.IndexOf("_devError = null;", StringComparison.Ordinal) > 0 && prep.IndexOf("_devError = null;", StringComparison.Ordinal) < prep.IndexOf("DevScenario.Plan(", StringComparison.Ordinal), "error cleared only by a new attempt");
        int errs = 0, at = 0; while ((at = prep.IndexOf("DevError(", at, StringComparison.Ordinal)) >= 0) { errs++; at++; }
        int rel = 0; at = 0; while ((at = prep.IndexOf("ReleaseTestIfDue(", at, StringComparison.Ordinal)) >= 0) { rel++; at++; }
        Expect(errs >= 8 && rel >= 3, "every preparation failure is a visible error; failures after arming release the sandbox (" + errs + " / " + rel + ")");
        Expect(dev.Contains("_devError = \"TESTKAMPAGNE NICHT GESTARTET – \" + why") && dev.Contains("if (_devError != null) { GUILayout.Label(\"!! \" + _devError);"), "persistent, unambiguous F10 error");
        Expect(code.Contains("if (test) DevError(deny);") && code.Contains("if (test) DevError(\"UDP-Port \"") && code.Contains("if (test) DevError(\"Mehrspieler-Systeme"), "host start failures after the preparation are visible too");
        int al = dev.IndexOf("void DevAutoLoad(DevPlan plan)", StringComparison.Ordinal);
        string load = dev.Substring(al, dev.IndexOf("void DevHostTick(GameContext ctx)", al, StringComparison.Ordinal) - al);
        Expect(load.Contains("if (!_worldLoadedOnce && !_worldSceneLoaded) { CancelHost(\"automatic test load failed\"); DevError("), "automatic-load failure before the world: host cancelled, sandbox released, visible error");
        DevPlan p1, p2;
        Expect(DevScenario.Plan(18, Data, true, Real, GameDir, "20261117-110000", out p1) == null && DevScenario.Plan(18, Data, true, Real, GameDir, "20261117-110100", out p2) == null && p1.MoveAsideTo != p2.MoveAsideTo, "retry: a fresh seed every start");
        var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs"));
        Expect(ui.Contains("if (!_open) { if (_devStatus) DrawDevStatus(); return; }") && dev.Contains("if (Context() != GameContext.InWorld) { _devStatus = false; return; }"), "in-world developer status (read-only, F10)");
    }

    // ---------------------------------------------------------------- 06
    static InstanceLog Log(string name, string role, string inst) { return new InstanceLog(Path.Combine(_logDir, name + ".log"), role, inst, "s1"); }
    static void Pump(Func<bool> until, int ms, string what, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); if (until()) return; Thread.Sleep(5); }
        throw new Exception("timeout: " + what + " | " + string.Join(", ", Array.ConvertAll(nodes, n => n.Role + "=" + n.State + (n.LastReject.HasValue ? "/" + n.LastReject : ""))));
    }
    static void Readiness()
    {
        int port = _port++; var key = MenuRouting.SessionKey(true, null); string gate = "Welt lädt";
        using (var h = new SessionNode(Role.Host, "hsdev", "host01", Game, port, Log("ready-host", "HOST", "host01")))
        using (var c = new SessionNode(Role.Client, "join", "c1", Game, port, Log("ready-c1", "CLIENT", "c1")))
        {
            h.BindAddress = "127.0.0.1"; h.JoinKey = key; h.TestCampaign = true; h.HostGate = () => gate; h.AuthTimeoutMs = 1500;
            c.ConnectAddress = "127.0.0.1"; c.JoinKey = key; c.ReconnectDelayMs = 200; c.DisconnectTimeoutMs = 2000;
            Expect(h.Start() && c.Start(), "start");
            Pump(() => c.LastReject == RejectCode.HostNotReady, 6000, "client told: host not ready", h, c);
            Expect(c.State != ConnState.Failed && !h.IsConnected, "not failed: retried while the host world loads");
            gate = null;   // HOST_READINESS ready
            Pump(() => h.IsConnected && c.IsConnected, 8000, "connects once the host is ready", h, c);
            Expect(c.HostTestCampaign && MenuRouting.JoinMismatch(JoinKind.Developer, true) == null, "developer join, test campaign, no code");
        }
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs")); var code = Strip(Src("Plugin", "MultiplayerMenu.cs"));
        Expect(dev.Contains("TEST_CAMPAIGN HOST READY") && dev.Contains(">>> HOST BEREIT") && dev.Contains("HOST LÄDT"), "unmistakable ready status (log + F10)");
        Expect(code.Contains("else if (_joinKind == JoinKind.Developer) _hint = MenuRouting.DevHostUnreachableText;"), "unreachable host explained on the client");
    }

    // ---------------------------------------------------------------- 07
    static void NoGenuineWrites()
    {
        string why;
        Expect(DevScenario.AsideTarget(Path.Combine(Data, "testcampaign", "story"), Real, GameDir, "1", out why) != null, "story folder may be moved aside");
        Expect(DevScenario.AsideTarget(Path.Combine(Data, "testcampaign"), Real, GameDir, "1", out why) == null && DevScenario.AsideTarget(Path.Combine(Data, "story"), Real, GameDir, "1", out why) == null
            && DevScenario.AsideTarget(Path.Combine(Real, "testcampaign", "story"), Real, GameDir, "1", out why) == null && DevScenario.AsideTarget(Path.Combine(GameDir, "testcampaign", "story"), Real, GameDir, "1", out why) == null
            && DevScenario.AsideTarget(Path.Combine(Data, "testcampaign", "story"), Real, GameDir, "..\\x", out why) == null, "nothing else, never inside genuine / game folders, safe stamp");
        var guard = Strip(Src("Plugin", "SaveGuard.cs"));
        int sd = guard.IndexOf("static void SeedDevHostSave()", StringComparison.Ordinal);
        string seed = guard.Substring(sd, guard.IndexOf("static bool IsSaveData", sd, StringComparison.Ordinal) - sd);
        Expect(seed.IndexOf("if (!_seedFromReal) {", StringComparison.Ordinal) >= 0 && seed.IndexOf("if (!_seedFromReal) {", StringComparison.Ordinal) < seed.IndexOf("Directory.GetFiles(RealDir", StringComparison.Ordinal), "story seed: the genuine folder is not even read");
        Expect(guard.Contains("SandboxDir = sandbox; _seedFromReal = seedFromReal;") && guard.Contains("SandboxDir = _defaultSandbox; _seedFromReal = true;"), "flag set with the sandbox, reset on release");
        var dev = Strip(Src("Plugin", "MultiplayerMenu.Dev.cs"));
        int al = dev.IndexOf("void DevAutoLoad(DevPlan plan)", StringComparison.Ordinal);
        string load = dev.Substring(al, dev.IndexOf("void DevHostTick(GameContext ctx)", al, StringComparison.Ordinal) - al);
        Expect(load.IndexOf("SaveGuard.RuntimeCheck()", StringComparison.Ordinal) < load.IndexOf("_startNewGame.Invoke(mm, null);", StringComparison.Ordinal), "original new game only after the sandbox target is verified");
        Expect(load.Contains("try { _startNewGame.Invoke(mm, null); } finally { _setSkipIntro(mm, prev); }"), "skipIntro restored");
        Expect(dev.Contains("SaveGuard.PrepareTestHostSave(dir, plan.SeedFromReal, out why)"), "seed flag passed to the guard");
        foreach (var bad in new[] { "Directory.Delete", "File.Delete", "File.Copy" }) Expect(dev.IndexOf(bad, StringComparison.Ordinal) < 0, "developer menu never " + bad);
    }

    // ---------------------------------------------------------------- 08
    static void F8Unaffected()
    {
        var code = Strip(Src("Plugin", "MultiplayerMenu.cs")); var ui = Strip(Src("Plugin", "MultiplayerMenu.Ui.cs"));
        Expect(code.Contains("if (test && !DevPrepare(out plan)) return;") && code.Contains("if (test) DevAutoLoad(plan);"), "developer preparation / automatic load only for the test host");
        Expect(code.Contains("else node.JoinKey = JoinAuth.DeriveKey(code);") && code.Contains(": _lc.RequestJoin(Context(), _isolationAvailable, _joinHost, _joinPort, _joinCode, out req, now);"), "F8 host / join keys and code validation unchanged");
        Expect(ui.Contains("StartHost(false);") && ui.Contains("StartJoin(JoinKind.Public);") && ui.IndexOf("DevPrepare", StringComparison.Ordinal) < 0 && ui.IndexOf("DevAutoLoad", StringComparison.Ordinal) < 0, "F8 never reaches the developer flow");
        Expect(MenuRouting.JoinMismatch(JoinKind.Public, true) != null && MenuRouting.JoinMismatch(JoinKind.Public, false) == null, "routing unchanged");
        Expect(ProtocolInfo.ProtocolVersion == 37 && ProtocolInfo.ModVersion == "0.26.3", "protocol 37 / mod 0.26.3");
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
