// W6C.1 offline tests: multiplayer-safe death / arrest without world rollback.
// Real Shared code: DeathRules / DeathEpisode / StoryLoadRule (src/Shared/Death.cs) + WorldHost (story publish, P2 story
// proposals, world items). The original save model is reproduced from the decompiled 1.27c IL (see Death.cs header):
// LoadCheckpoint = reload the DISK save without saving; SelectHideout / SelectJail = SaveGameNewSystem of the live model first,
// then reload. Unity glue (DeathSync, TravelHooks, StorySync, Plugin) is source-scanned. In-game: TESTING.md section 59.
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class W6C1Tests
{
    static int _passed, _failed; static uint _pid = 100;
    const int Madison = 99, Hideout = 0, Jail = 93, S14 = 18, S15 = 19, S16 = 20;

    static int Main()
    {
        Run("01 rules: caught-screen Load -> Respawn only in a session, pause / slot loads refused in a session, single-player original", Rules);
        Run("02 reasons + screen: original caughtTxt keys, DEATH vs CAUGHT, Go to Jail offered exactly as the original (reason 0, not BankScene)", Reasons);
        Run("03 episode idempotency: repeated GotCaught, double click, Respawn after Jail, failure re-opens, timeout, new scene ends the episode", Episode);
        Run("04 P1 and P2 die independently: two episodes, one choice each, no shared state", Independent);
        Run("05 0.30.0 rollback reproduced: host LoadCheckpoint drops P2's box take + story 19 -> box back at 104, story 18, contents twice", RollbackReproduced);
        Run("06 multiplayer Respawn: original SelectHideout saves the live model first -> box stays taken, story 19 kept, contents exactly once", RespawnNoRollback);
        Run("07 Go to Jail: original saves first -> shared progress kept, jail consequences applied (CurrentLocation 93, timesCaught +1)", JailNoRollback);
        Run("08 live network while P1 is on the caught screen: P2 item claim + listed story trigger committed by the host", LiveWhileCaught);
        Run("09 host story after its own scene load: lower index held 5 s, re-advance never published, still lower -> published, no max() merge", StoryHold);
        Run("10 repeated death / respawn cycles + both players near-simultaneously: story / items / revision consistent", Cycles);
        Run("11 Unity glue: DeathSync hooks, LoadCheckpoint never runs on the caught screen in a session, SelectHideout transition, label key, fail closed", Glue);
        Run("12 single-player unchanged + regressions + versions: hooks only in a session, TravelHooks no longer patches GotCaught / LoadCheckpoint, protocol 41 / mod 0.31.0", Regressions);
        Console.WriteLine(_failed == 0 ? "W6C1 TESTS PASSED (" + _passed + ")" : "W6C1 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- original save model (IL, Death.cs header)
    sealed class Game : IStoryHost
    {
        public readonly Dictionary<string, int> Live = new Dictionary<string, int>(), Disk = new Dictionary<string, int>();
        public int Story; public bool Loaded = true; public int Saves, Loads;
        public Game(int story) { Story = story; Live["Story"] = story; Save(); }
        public int Pref(string k) { int v; return Live.TryGetValue(k, out v) ? v : 0; }
        public bool TryCurrent(out int index, out string key, out bool free) { index = Story; key = "story" + (Story - 4); free = false; return Loaded; }
        public bool Advance(int expected) { if (Story != expected) return false; Story++; Live["Story"] = Story; return true; }
        // SaveGameNewSystem: FBPP disk write of the in-memory model
        public void Save() { Live["Story"] = Story; Disk.Clear(); foreach (var kv in Live) Disk[kv.Key] = kv.Value; Saves++; }
        // FBPP.Start on the MainScene load: the model is read from the disk file again; BeginStory reads "Story"
        void Reload() { Live.Clear(); foreach (var kv in Disk) Live[kv.Key] = kv.Value; Story = Pref("Story"); Loads++; }
        // GameManager.LoadCheckpoint -> LoadCheckpointIE -> Application.LoadLevel("MainScene"): NO save first
        public void LoadCheckpoint() { Reload(); }
        // GameManager.SelectHideout: Reset*Position, Medkit 0, CurrentLocation 0, SaveGameNewSystem, LoadWait -> LoadMain
        public void SelectHideout() { Live["CurrentLocation"] = Hideout; Live["Medkit"] = 0; Save(); Reload(); }
        // GameManager.SelectJail: CurrentLocation 93, timesCaught++, RemoveAllAlerts, SaveGameNewSystem, LoadWait -> LoadMain
        public void SelectJail() { Live["CurrentLocation"] = Jail; Live["TimesCaught"] = Pref("TimesCaught") + 1; Live["104_alert"] = 0; Save(); Reload(); }
    }

    // The 0.30.0 sequence: P2 takes the story box at house 104 (host mirror commit = ItemDay key in the host's LIVE model),
    // the host story advanced to story15 (19) after the last save, P2 unpacks the box in the hideout (one contents item).
    static WorldHost NewSession(out Game g)
    {
        var wh = new WorldHost(91); wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, Madison, 0);
        g = new Game(S14); wh.StoryHost = g; wh.PublishStory(0); wh.Outbox.Clear();
        wh.ItemTxnHook = (slot, t) => ProposalCode.Accepted;                 // owner inventory side (plugin) accepts
        g.Advance(S14); wh.PublishStory(0);                                    // story15, not saved yet
        g.Live["ItemDay_CardboardBox_FlybyLiteDrone"] = 3;                     // P2's box take committed by the host (WLOOT / mirror)
        return wh;
    }
    static StoryState StoryOf(WorldHost wh) { var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Story); StoryState s; return e != null && StoryState.TryParse(e.Value, out s) ? s : null; }
    static bool BoxAtHouse(Game g) { return g.Pref("ItemDay_CardboardBox_FlybyLiteDrone") == 0; }   // Pickupable.Start shows it again
    static uint RegisterContents(WorldHost wh)
    {
        wh.SetPlayerRegion(1, Hideout, 0);   // the unpack table is in the hideout (host there when the box is opened)
        string why; var drop = new ItemTxnRequest { Op = ItemOp.Drop, Txn = 0, ItemId = 91, Region = Hideout, Pos = new Vec3(1, 2, 3), Rot = Quat16.From(0, 0, 0, 1) };
        Expect(wh.ItemTransaction(1, drop, 1, out why) == ProposalCode.Accepted, "contents registered: " + why);
        return wh.Items.LastApplied;
    }
    static ProposalCode Send(WorldHost wh, byte slot, string key, byte[] value, uint id = 0)
    { uint rev; int sr; float sh; return wh.HandleProposal("cli", slot, id != 0 ? id : ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = key, Value = value }, 1, out rev, out sr, out sh); }

    // Host caught-screen click as DeathSync routes it.
    static string Click(Game g, LoadSource src, bool session, DeathEpisode ep, double now)
    {
        var d = DeathRules.Decide(session, src);
        if (d == LoadDecision.Original) { g.LoadCheckpoint(); return "original"; }
        if (d == LoadDecision.Refuse) return "refused";
        if (!ep.TryChoose(DeathChoice.Respawn, now)) return "duplicate";
        g.SelectHideout(); return "respawn";
    }

    // ---------------------------------------------------------------- 01
    static void Rules()
    {
        Expect(DeathRules.Decide(false, LoadSource.CaughtScreen) == LoadDecision.Original && DeathRules.Decide(false, LoadSource.PauseMenu) == LoadDecision.Original
            && DeathRules.Decide(false, LoadSource.SaveSlot) == LoadDecision.Original, "no session: every load is the original");
        Expect(DeathRules.Decide(true, LoadSource.CaughtScreen) == LoadDecision.Respawn, "session: caught screen -> Respawn");
        Expect(DeathRules.Decide(true, LoadSource.PauseMenu) == LoadDecision.Refuse && DeathRules.Decide(true, LoadSource.SaveSlot) == LoadDecision.Refuse, "session: pause menu / slot load refused");
        Expect(DeathRules.RespawnLabelKey == "Continue" && DeathRules.OriginalLabelKey == "load_checkpoint", "the game's own localization keys");
        Expect(DeathRules.SourceName(LoadSource.SaveSlot).Contains("LoadProfile") && DeathRules.SourceName(LoadSource.PauseMenu).Contains("pause"), "source names");
    }

    // ---------------------------------------------------------------- 02
    static void Reasons()
    {
        string[] keys = { "got_caught", "ran_over", "collapsed_from_gas", "killed_person", "only_guards", "you_got_shot", "got_caught", "car_found_police" };
        for (int r = 0; r < keys.Length; r++) Expect(DeathRules.ReasonKey(r) == keys[r], "reason " + r + " key");
        Expect(DeathRules.Kind(5) == "DEATH" && DeathRules.Kind(2) == "DEATH" && DeathRules.Kind(0) == "CAUGHT" && DeathRules.Kind(7) == "CAUGHT", "health / gas = DEATH");
        Expect(DeathRules.JailOffered(0, "MainScene") && !DeathRules.JailOffered(0, "BankScene") && !DeathRules.JailOffered(5, "MainScene") && !DeathRules.JailOffered(7, "MainScene"), "jail set only for reason 0 outside the bank");
    }

    // ---------------------------------------------------------------- 03
    static void Episode()
    {
        var ep = new DeathEpisode();
        Expect(ep.Begin(0, false) && ep.Id == 1 && ep.Open, "first GotCaught opens #1");
        Expect(!ep.Begin(0, true) && !ep.Begin(5, false) && ep.Duplicates == 2 && ep.Id == 1, "repeated GotCaught (original guard / same episode) = duplicate");
        Expect(ep.TryChoose(DeathChoice.Respawn, 10) && !ep.TryChoose(DeathChoice.Respawn, 10.1) && !ep.TryChoose(DeathChoice.Jail, 10.2), "one choice: double click / Jail after Respawn ignored");
        Expect(!ep.TimedOut(50, 60) && ep.TimedOut(71, 60), "no scene load 60 s after the choice -> failure");
        ep.Failed(); Expect(ep.Open && !ep.Chosen && ep.TryChoose(DeathChoice.Jail, 80), "failure re-opens the choice");
        ep.Complete(); Expect(!ep.Open && !ep.TryChoose(DeathChoice.Respawn, 90), "new scene ends the episode; late clicks ignored");
        Expect(ep.Begin(5, false) && ep.Id == 2 && ep.Reason == 5, "next death = episode #2");
    }

    // ---------------------------------------------------------------- 04
    static void Independent()
    {
        var p1 = new DeathEpisode(); var p2 = new DeathEpisode();
        Game g1; var wh = NewSession(out g1); var g2 = new Game(S15);   // P2's own sandbox game
        Expect(p1.Begin(0, false), "P1 caught");
        Expect(!p2.Open, "P2 unaffected");
        Expect(p2.Begin(5, false) && p2.TryChoose(DeathChoice.Respawn, 1) && p1.TryChoose(DeathChoice.Jail, 1), "both choose independently");
        g2.SelectHideout(); g1.SelectJail();
        Expect(g1.Pref("CurrentLocation") == Jail && g2.Pref("CurrentLocation") == Hideout, "each instance moves only itself");
        Expect(g1.Story == S15 && !BoxAtHouse(g1) && StoryOf(wh).Index == S15, "host shared progress kept through P1's jail");
    }

    // ---------------------------------------------------------------- 05
    static void RollbackReproduced()
    {
        Game g; var wh = NewSession(out g);
        uint c1 = RegisterContents(wh);   // P2's unpack of the taken box (first contents)
        var ep = new DeathEpisode(); ep.Begin(0, false);
        Expect(Click(g, LoadSource.CaughtScreen, false, ep, 1) == "original", "vanilla routing (0.30.0 had no session rule)");
        wh.PublishStory(0);
        Expect(g.Story == S14 && StoryOf(wh).Index == S14, "story 19 -> 18 (log: STORY host published story 18 'story14')");
        Expect(BoxAtHouse(g), "the taken box is back at house 104 on the host");
        uint c2 = RegisterContents(wh);   // host takes / unpacks the reappeared box: second contents
        Expect(c1 != c2 && wh.Items.CountIn(Hideout) == 2, "contents created twice (log: WITEM pickup #1 and #2 of item 91)");
    }

    // ---------------------------------------------------------------- 06
    static void RespawnNoRollback()
    {
        Game g; var wh = NewSession(out g);
        var before = StoryOf(wh); uint head = wh.Reg.HeadRev;
        var ep = new DeathEpisode(); ep.Begin(0, false);
        Expect(Click(g, LoadSource.CaughtScreen, true, ep, 1) == "respawn" && g.Saves == 2 && g.Loads == 1, "Respawn = SelectHideout: one save, one load, no LoadCheckpoint");
        Expect(Click(g, LoadSource.CaughtScreen, true, ep, 1.1) == "duplicate" && g.Saves == 2, "double click: no second transition");
        Expect(!wh.PublishStory(0) && StoryOf(wh).SameAs(before) && wh.Reg.HeadRev == head, "story 19 unchanged, no new world revision");
        Expect(!BoxAtHouse(g) && g.Pref("CurrentLocation") == Hideout, "box stays taken; the player spawns in the hideout");
        RegisterContents(wh);
        Expect(wh.Items.CountIn(Hideout) == 1, "exactly one contents item");
        Expect(Click(g, LoadSource.PauseMenu, true, ep, 2) == "refused" && Click(g, LoadSource.SaveSlot, true, ep, 2) == "refused" && g.Loads == 1, "later pause / slot loads refused, nothing reloaded");
    }

    // ---------------------------------------------------------------- 07
    static void JailNoRollback()
    {
        Game g; var wh = NewSession(out g); var before = StoryOf(wh);
        g.Live["104_alert"] = 1;
        var ep = new DeathEpisode(); ep.Begin(0, false);
        Expect(DeathRules.JailOffered(0, "MainScene") && ep.TryChoose(DeathChoice.Jail, 1), "Go to Jail chosen");
        g.SelectJail();
        Expect(g.Story == S15 && !BoxAtHouse(g) && !wh.PublishStory(0) && StoryOf(wh).SameAs(before), "story / box take kept (saved before the load)");
        Expect(g.Pref("CurrentLocation") == Jail && g.Pref("TimesCaught") == 1 && g.Pref("104_alert") == 0, "original jail consequences");
        Expect(!ep.TryChoose(DeathChoice.Respawn, 2), "no second choice on the same screen");
        g.SelectHideout();   // bail: PlayerScript.Update IL_54a8
        Expect(g.Story == S15 && !BoxAtHouse(g) && g.Pref("CurrentLocation") == Hideout, "jail exit (bail -> SelectHideout) keeps everything");
    }

    // ---------------------------------------------------------------- 08
    static void LiveWhileCaught()
    {
        Game g; var wh = NewSession(out g); wh.SetPlayerRegion(2, Hideout, 0);
        var ep = new DeathEpisode(); ep.Begin(0, false);   // host on the caught screen (Time.timeScale 0 on the host only)
        uint wid = RegisterContents(wh);
        int picks = 0; wh.ItemTxnHook = (slot, t) => { if (slot == 2 && t.Op == ItemOp.Pickup) picks++; return ProposalCode.Accepted; };
        var pick = new ItemTxnRequest { Op = ItemOp.Pickup, Txn = 1, WorldId = wid, ItemId = 91, Region = Hideout };
        Expect(Send(wh, 2, WorldKeys.ItemTxn, pick.Encode()) == ProposalCode.Accepted && picks == 1 && wh.Items.CountIn(Hideout) == 0, "P2's claim committed while the host is caught");
        Expect(Send(wh, 2, WorldKeys.StoryAdvance, new StoryAdvanceRequest { FromIndex = S16, StageKey = "story16", Kind = StoryTriggerKind.BlackbaySale, Path = "OTHERS/Viewport/Content/PACKAGE" }.ToBytes()) != ProposalCode.Accepted
            && g.Story == S15, "a stale-stage trigger is still validated (not accepted at story15)");
        g.Advance(S15); wh.PublishStory(0);   // stage story16 reached
        Expect(Send(wh, 2, WorldKeys.StoryAdvance, new StoryAdvanceRequest { FromIndex = S16, StageKey = "story16", Kind = StoryTriggerKind.BlackbaySale, Path = "OTHERS/Viewport/Content/PACKAGE" }.ToBytes()) == ProposalCode.Accepted
            && g.Story == S16 + 1, "P2's listed story16 trigger applied by the host original while the host is caught");
        ep.TryChoose(DeathChoice.Respawn, 1); g.SelectHideout();
        Expect(g.Story == S16 + 1 && !wh.PublishStory(0), "the progress P2 made during the caught screen survives the respawn");
    }

    // ---------------------------------------------------------------- 09
    static void StoryHold()
    {
        // travel race (0.30.0 log 21:37:05-09): save at 19, advance to 20 during LoadWait, fresh scene starts at 19, original re-advances
        Expect(StoryLoadRule.Decide(S15, true, S16, 100, 100.1) == StoryLoadDecision.Hold, "lower right after the load: held");
        Expect(StoryLoadRule.Decide(S15, true, S16, 100, 104.9) == StoryLoadDecision.Hold, "still within 5 s: held");
        Expect(StoryLoadRule.Decide(S16, true, S16, 100, 101) == StoryLoadDecision.Publish, "back at the committed index: publish path (unchanged record)");
        Expect(StoryLoadRule.Decide(S15, true, S16, 100, 105.1) == StoryLoadDecision.Publish, "still lower after 5 s: published (host state stays authoritative)");
        Expect(StoryLoadRule.Decide(S15, false, -1, 100, 100) == StoryLoadDecision.Publish && StoryLoadRule.Decide(S16 + 1, true, S16, 100, 100) == StoryLoadDecision.Publish, "no committed record / higher: publish");
        // published sequence of the race with the rule (host StorySync loop)
        var published = new List<int>(); int committed = S16; double bound = 0;
        int[] frames = { S15, S15, S15, S16 }; double[] at = { 0.1, 0.5, 0.9, 1.2 };
        for (int i = 0; i < frames.Length; i++)
            if (StoryLoadRule.Decide(frames[i], true, committed, bound, at[i]) == StoryLoadDecision.Publish && frames[i] != committed) { published.Add(frames[i]); committed = frames[i]; }
        Expect(published.Count == 0, "20 -> 19 -> 20 flicker never reaches Player 2");
        // never a max() merge: a P2 proposal from a higher (own-save) index is refused, the host index decides
        Game g; var wh = NewSession(out g);
        Expect(Send(wh, 2, WorldKeys.StoryAdvance, new StoryAdvanceRequest { FromIndex = 30, StageKey = "story26", Kind = StoryTriggerKind.ReadNote, Path = "x" }.ToBytes()) != ProposalCode.Accepted && g.Story == S15, "P2's higher index never adopted");
    }

    // ---------------------------------------------------------------- 10
    static void Cycles()
    {
        Game g; var wh = NewSession(out g); var before = StoryOf(wh); uint head = wh.Reg.HeadRev;
        var p1 = new DeathEpisode(); var p2 = new DeathEpisode(); var g2 = new Game(S15);
        for (int i = 0; i < 5; i++)
        {
            Expect(p1.Begin(i % 2 == 0 ? 0 : 5, false) && p2.Begin(0, false), "cycle " + i + ": both caught near-simultaneously");
            Expect(Click(g, LoadSource.CaughtScreen, true, p1, i) == "respawn", "P1 respawn " + i);
            if (i % 2 == 0) { Expect(p2.TryChoose(DeathChoice.Respawn, i), "P2 respawn"); g2.SelectHideout(); } else { Expect(p2.TryChoose(DeathChoice.Jail, i), "P2 jail"); g2.SelectJail(); }
            p1.Complete(); p2.Complete();
        }
        Expect(p1.Id == 5 && p2.Id == 5 && g.Story == S15 && !BoxAtHouse(g) && !wh.PublishStory(0) && StoryOf(wh).SameAs(before) && wh.Reg.HeadRev == head, "5 cycles: same story, box taken, no new revision");
        Expect(g2.Pref("TimesCaught") == 2 && g2.Story == S15, "P2's personal jail count only on P2");
    }

    // ---------------------------------------------------------------- 11
    static void Glue()
    {
        var d = Src("Plugin", "DeathSync.cs");
        Expect(d.Contains("AccessTools.Method(gm, \"GotCaught\", new[] { typeof(int) })") && d.Contains("AccessTools.Method(gm, \"LoadCheckpoint\", Type.EmptyTypes)")
            && d.Contains("AccessTools.Method(gm, \"LoadProfile\", new[] { typeof(int) })") && d.Contains("AccessTools.Method(gm, \"SelectJail\", Type.EmptyTypes)")
            && d.Contains("AccessTools.Method(gm, \"SelectHideout\", Type.EmptyTypes)") && d.Contains("\"LoadMenu\""), "hooks on the verified original methods");
        string lc = Between(d, "bool OnLoadCheckpoint(object gm)", "// ---- jail / quit ----");
        Expect(!lc.Contains("return true"), "the LoadCheckpoint handler never lets the original run during a session");
        Expect(lc.Contains("_selectHideout.Invoke(gm, null);") && lc.Contains("started = _gmDisableSound(gm);") && lc.Contains("co.SetActive(false)") && lc.Contains("_ep.Failed();") && lc.Contains("co.SetActive(true)"),
            "Respawn = original SelectHideout, started check (LoadWait disablesound), failure restores the screen");
        Expect(lc.Contains("_ep.TryChoose(DeathChoice.Respawn, Now)") && d.Contains("_ep.TryChoose(DeathChoice.Jail, Now)"), "one choice per caught screen");
        Expect(Between(d, "static bool LoadCheckpointPrefix(object __instance)", "bool OnLoadCheckpoint").Contains("return false; }") && Between(d, "static bool LoadProfilePrefix(int __0)", "static bool LoadCheckpointPrefix").Contains("return false;"),
            "handler exceptions fail closed (no reload)");
        Expect(d.Contains("DeathRules.RespawnLabelKey") && d.Contains("\"LOAD CHECKPOINT\"") && d.Contains("\"UnityEngine.UI.Text\"") && d.Contains("DeathRules.OriginalLabelKey"), "label from the game's own key, restored at session end");
        Expect(!d.Contains("GUI.") && !d.Contains("OnGUI") && !d.Contains("timeScale = 1"), "no mod UI text, no global timeScale override");
        Expect(d.Contains("\"RESPAWN begin \"") && d.Contains("\"JAIL begin \"") && d.Contains("\" complete \"") && d.Contains("\"CHECKPOINT_LOAD \"") && d.Contains("\"DEATH_SCREEN \"") && d.Contains("DeathRules.Kind(reason)"),
            "diagnostic events");
        Expect(Between(d, "public void Update()", "public string Stats").Contains("if (!_ep.Open) return;"), "per frame: early return when no caught screen (event-driven)");
        var ss = Src("Plugin", "StorySync.cs");
        Expect(ss.Contains("StoryLoadRule.Decide(local, _bindCommitted >= 0, _bindCommitted, _boundAt, Now) == StoryLoadDecision.Hold") && ss.Contains("_boundAt = Now; _bindCommitted = CommittedIndex();"), "host story hold after its own scene load");
        Expect(!Between(ss, "if (_host)", "double now = Now;").Contains("Math.Max"), "no max() story rule");
        var hs = Src("Plugin", "HeavySync.cs"); var hc = Src("Plugin", "HeavyCargoSync.cs");
        Expect(hs.Contains("internal string IdentityText") && hc.Contains("internal string IdentityText"), "heavy / cargo identity diagnostics");
    }

    // ---------------------------------------------------------------- 12
    static void Regressions()
    {
        var pl = Src("Plugin", "Plugin.cs"); var th = Src("Plugin", "TravelHooks.cs"); var d = Src("Plugin", "DeathSync.cs");
        Expect(pl.Contains("_death = new DeathSync(node, log, _world)") && pl.Contains("Release(ref _death, \"DEATH\")") && pl.Contains("_death.Update()"), "built / updated / released with the session systems");
        Expect(Between(pl, "void BuildSystems(SessionNode node)", "internal void DetachSession").Contains("new DeathSync("), "constructed only in BuildSystems (session)");
        Expect(d.Contains("_h.UnpatchSelf()") && d.Contains("if (ReferenceEquals(_inst, this)) _inst = null;"), "single-player after the session: patches removed");
        Expect(!th.Contains("\"GotCaught\"") && !th.Contains("\"LoadCheckpoint\"") && th.Contains("\"LoadMenu\""), "TravelHooks no longer patches GotCaught / LoadCheckpoint");
        Expect(!th.Contains("\"SelectJail\""), "SelectJail stays out of the travel menu hooks");
        Expect(ProtocolInfo.ProtocolVersion == 41 && ProtocolInfo.ModVersion == "0.31.0", "protocol 41 (no wire change) / mod 0.31.0 (both PCs: version check)");
    }

    // ---------------------------------------------------------------- helpers
    static string Between(string s, string a, string b)
    {
        int i = s.IndexOf(a, StringComparison.Ordinal); Expect(i >= 0, "marker not found: " + a);
        int j = s.IndexOf(b, i + a.Length, StringComparison.Ordinal); Expect(j > i, "end marker not found: " + b);
        return s.Substring(i, j - i);
    }
    static string Root()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++) { if (Directory.Exists(Path.Combine(dir, "src")) && Directory.Exists(Path.Combine(dir, "tests"))) return dir; dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/')); }
        throw new Exception("repo root not found");
    }
    static string Src(string dir, string f) { return File.ReadAllText(Path.Combine(Root(), "src", dir, f)).Replace("\r\n", "\n"); }
}
