// v0.16.1 offline tests (two-PC QA fixes): (A) world loot can never produce a reward twice - Player 2 pickups that no host
// transaction covers are refused, shared-name families are shared by census id, the host lists save-removed objects as taken;
// (B) Player 2's join shows the original main-menu loading state and locks the menu only while joining; (C) Player 2 spawns
// from the host campaign checkpoint with the original load rule, a live position survives only while the host world runs.
// The tests drive the SAME Unity-free pieces as the plugin (LootRules / LootBook / LootDecision, MenuRules / Lifecycle,
// HostCheckpoint / SpawnRules / CampaignSnapshot) plus source scans of the Unity glue (WorldLootSync, JoinLoadingScreen,
// MultiplayerMenu, CampaignBootstrap). In-game behaviour is manual QA (docs/TESTING.md §36).
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class V161Tests
{
    static int _passed, _failed;
    const int Ns = 99, Madison = 99, Hillville = 97, Hideout = 0, Verwood = 1, AutoShop = 3, Bank = 92;

    static int Main()
    {
        Run("A1 P1 pickup -> committed taken -> P2's copy removed (registry chunk), P2 cannot take it", P1RemovesForP2);
        Run("A2 P2 pickup -> host accepts once -> P1's claim refused, registry lists it taken", P2RemovesForP1);
        Run("A3 money / ordinary valuable / unique valuable classes, shared-name family members included", Classes);
        Run("A4 shared-name collision: Pot + Pot are two takes, family key once, live-object persistence", SharedNameFamily);
        Run("A5 simultaneous attempts: pending P1 claim -> P2 Busy; P2 first -> P1 refused; one owner", Simultaneous);
        Run("A6 duplicate / delayed / out-of-order proposals never grant twice", DuplicatesAndDelays);
        Run("A7 host save, reconnect, region reload: save-removed objects seeded as taken by the original reload rule; respawn day", ReloadAndRespawn);
        Run("A8 unsupported objects can never duplicate: every excluded class / region / MainScene object refused for Player 2", UnsupportedRefused);
        Run("A9 Unity glue: client never falls back to a local pickup, heavy pickups guarded, host never refuses P1 for excluded", LootSourceScan);
        Run("B1 loading-screen rule: host prepared menu usable; P2 join states locked; never in world / after Continue / offline", LoadingRule);
        Run("B2 P2 join flow: connect -> isolate -> synchronize -> Continue hand-off -> world; input restored", LoadingFlow);
        Run("B3 failure paths: connect timeout, host-not-ready timeout, sync timeout, rejected auth, cancel -> menu restored", LoadingFailures);
        Run("B4 Unity glue: original Deactivate/ActivateLoad objects, no own UI, hand-off before Continue, SaveGuard gate kept", LoadingSourceScan);
        Run("C1 original fixed-spawn regions: hideout / Verwood / Auto Shop / Madison story <= 3", FixedSpawnRegions);
        Run("C2 original saved-position regions: Madison / Hillville Saved_Pos 1 (manual save / autosave) and Saved_Pos 0 (travel)", SavedPositionRegions);
        Run("C3 uncommitted position + full restart / host menu return: never resurrected (new host session = checkpoint)", RestartUsesCheckpoint);
        Run("C4 Player 2 reconnect while the host world runs: live position (memory only), vehicle / hiding / story contexts", LiveRejoin);
        Run("C5 invalid / outdated records, mission contexts, different regions; plans stay inside the protocol-27 wire domain", SpawnEdgeCases);
        Run("C6 Unity glue: no p2spawn file read/write/rename, checkpoint from the host model, yaw only for live positions", SpawnSourceScan);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ------------------------------------------------------------------------------------------------ helpers
    static ItemFacts Facts() { return new ItemFacts { Known = true, Value = 150, AddXp = 5, MaxAmount = 0 }; }
    static LootCatalog Cat(params object[] objs)   // (hash, name, flags[, money])
    {
        var c = new LootCatalog(Ns);
        for (int i = 0; i < objs.Length; i += 4) c.Add((uint)objs[i], (string)objs[i + 1], 9, (LootFlags)objs[i + 2], null, 0, (int)objs[i + 3]);
        c.Seal(null); c.Digest = 1; return c;
    }
    static HashSet<uint> Taken(StateRegistry reg)
    {
        var s = new HashSet<uint>();
        for (int c = 0; c < LootBook.Chunks; c++) { var e = reg.Get(StateScope.Region, Ns, LootBook.Keys[c]); if (e != null) Expect(LootBook.TryParse(e.Value, c, s), "chunk parses"); }
        return s;
    }
    static ProposalCode P2(LootCatalog c, uint txn, uint applied, uint hash, LootBook book, bool persist = false)
    {
        LootEntry e; c.TryGet(hash, out e); LootClass cls; string why;
        var facts = e != null && (e.Flags & LootFlags.Money) != 0 ? new ItemFacts() : Facts();
        return LootDecision.DecideP2(txn, applied, true, c, 1, hash, 9, facts, book.StateOf(Ns, hash), book.HasRoom(Ns, hash), 0, persist, out cls, out why);
    }
    static string Root()
    {
        var d = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && d != null; i++) { if (Directory.Exists(Path.Combine(d, "src", "Plugin"))) return d; d = Path.GetDirectoryName(d.TrimEnd('\\', '/')); }
        throw new Exception("repository root not found");
    }
    static string Src(string f) { return File.ReadAllText(Path.Combine(Root(), "src", "Plugin", f)); }
    static string Shared(string f) { return File.ReadAllText(Path.Combine(Root(), "src", "Shared", f)); }

    // ------------------------------------------------------------------------------------------------ A loot
    static void P1RemovesForP2()
    {
        var c = Cat(10u, "Lamp", LootFlags.None, 0); var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>(); string why;
        Expect(book.Claim(Ns, 10, 555, out why) && book.StateOf(Ns, 10) == LootState.Pending, "P1 claim pending while the original coroutine runs");
        Expect(!Taken(reg).Contains(10), "not listed before the irreversible step");
        Expect(book.Commit(Ns, 10, reg, o) == ProposalCode.Accepted && o.Count == 1, "committed at SetDayRespawn, one registry delta");
        Expect(Taken(reg).Contains(10), "P2's observer reads it taken -> removes its copy");
        Expect(P2(c, 1, 0, 10, book) == ProposalCode.Stale, "P2 cannot take it afterwards");
    }

    static void P2RemovesForP1()
    {
        var c = Cat(11u, "Vase", LootFlags.None, 0); var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>(); string why;
        Expect(P2(c, 1, 0, 11, book) == ProposalCode.Accepted, "host accepts Player 2's pickup");
        Expect(book.Commit(Ns, 11, reg, o) == ProposalCode.Accepted && Taken(reg).Contains(11), "host commit: listed taken (host removes its own copy at once)");
        Expect(!book.Claim(Ns, 11, 777, out why) && why == "already taken", "Player 1's original pickup of it is refused (HostPick removes the stale copy)");
    }

    static void Classes()
    {
        string why;
        Expect(LootRules.Classify(new LootEntry { Name = "15$_a", Flags = LootFlags.Money, Money = 15 }, new ItemFacts(), out why) == LootClass.Money, "world money");
        Expect(LootRules.Classify(new LootEntry { Name = "15$_a", Flags = LootFlags.Money | LootFlags.SharedName, Money = 15 }, new ItemFacts(), out why) == LootClass.Money, "world money of a shared-name family");
        Expect(LootRules.Classify(new LootEntry { Name = "Lamp", Flags = LootFlags.None }, Facts(), out why) == LootClass.Valuable, "ordinary valuable");
        Expect(LootRules.Classify(new LootEntry { Name = "Pot", Flags = LootFlags.SharedName }, Facts(), out why) == LootClass.Valuable, "shared-name valuable (Madison 'Pot' / 'Wine1')");
        Expect(LootRules.Classify(new LootEntry { Name = "Ring", Flags = LootFlags.DestroyIfPicked }, Facts(), out why) == LootClass.UniqueValuable, "unique valuable");
        Expect(LootRules.Classify(new LootEntry { Name = "Ring", Flags = LootFlags.DestroyIfPicked | LootFlags.SharedName }, Facts(), out why) == LootClass.UniqueValuable, "unique of a shared-name family");
        var key = Facts(); key.Key = true;
        Expect(LootRules.Classify(new LootEntry { Name = "Key_102", Flags = LootFlags.DestroyIfPicked }, key, out why) == LootClass.UniqueValuable, "W4.2D: unique key shared as a unique valuable");
        var c = Cat(1u, "Pot", LootFlags.None, 0, 2u, "Pot", LootFlags.None, 0, 3u, "15$_x", LootFlags.Money, 15, 4u, "15$_x", LootFlags.Money, 15, 5u, "Ring", LootFlags.DestroyIfPicked, 0, 6u, "Ring", LootFlags.DestroyIfPicked, 0);
        Expect(c.Eligible == 2 && c.EligibleMoney == 2 && c.EligibleUnique == 2 && c.SharedNameObjects == 6, "flag-level eligibility counts family members (" + c.Eligible + "/" + c.EligibleMoney + "/" + c.EligibleUnique + ")");
    }

    static void SharedNameFamily()
    {
        var c = Cat(1u, "Wine1", LootFlags.None, 0, 2u, "Wine1", LootFlags.None, 0);
        var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>(); string why;
        // P1 takes member 1 (original), P2 then takes member 2 (host transaction): two separate takes
        Expect(book.Claim(Ns, 1, 100, out why) && book.Commit(Ns, 1, reg, o) == ProposalCode.Accepted, "P1 takes Wine1 #1");
        bool persist2 = LootRules.PersistentlyTaken(LootClass.Valuable, true, true, true, 20, 1, 15);   // family key now 20 > today 15, member 2 alive on the host
        Expect(!persist2 && P2(c, 1, 0, 2, book, persist2) == ProposalCode.Accepted, "member 2 still pickable while the host scene lives (vanilla until reload)");
        Expect(book.Commit(Ns, 2, reg, o) == ProposalCode.Accepted && Taken(reg).Count == 2, "two takes, never merged");
        // retransmitted P2 request for member 2: answered Unchanged, never a second grant
        Expect(P2(c, 1, 1, 2, book) == ProposalCode.Unchanged, "retransmission");
        // the same object can never be taken by both players
        Expect(P2(c, 2, 1, 1, book) == ProposalCode.Stale, "P2 cannot take member 1 that P1 took");
        // after a reload (no live host scene) the family key removes every member until its respawn day
        Expect(LootRules.PersistentlyTaken(LootClass.Valuable, true, false, false, 20, 1, 15) && LootRules.PersistentlyTaken(LootClass.Valuable, false, false, false, 20, 1, 15), "reload: key destroys every member (original <Start>)");
        Expect(LootRules.PersistentlyTaken(LootClass.Valuable, true, true, false, 0, 0, 15), "host object destroyed while live -> taken");
    }

    static void Simultaneous()
    {
        var c = Cat(20u, "Clock", LootFlags.None, 0); var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>(); string why;
        Expect(book.Claim(Ns, 20, 1, out why), "P1 claim");
        Expect(P2(c, 1, 0, 20, book) == ProposalCode.Busy, "P2 at the same moment -> Busy (nothing granted, retry)");
        Expect(book.Commit(Ns, 20, reg, o) == ProposalCode.Accepted && P2(c, 1, 0, 20, book) == ProposalCode.Stale, "P1 finished -> P2 Stale");
        var c2 = Cat(21u, "Clock2", LootFlags.None, 0); var b2 = new LootBook();
        Expect(P2(c2, 1, 0, 21, b2) == ProposalCode.Accepted && b2.Commit(Ns, 21, reg, o) == ProposalCode.Accepted, "P2 first");
        Expect(!b2.Claim(Ns, 21, 2, out why), "P1 afterwards refused");
        Expect(!book.Claim(Ns, 20, 3, out why) && why == "already taken", "no second claim on a taken object");
        Expect(b2.Claim(Ns, 22, 4, out why) && !b2.Claim(Ns, 22, 5, out why) && why == "pickup already in progress", "two P1 claims on one object: the second is refused");
    }

    static void DuplicatesAndDelays()
    {
        var c = Cat(30u, "Phone", LootFlags.None, 0); var book = new LootBook();
        Expect(P2(c, 3, 2, 30, book) == ProposalCode.Accepted, "next txn accepted");
        Expect(P2(c, 3, 3, 30, book) == ProposalCode.Unchanged && P2(c, 1, 3, 30, book) == ProposalCode.Unchanged, "duplicate / delayed old txn -> Unchanged (already decided)");
        Expect(P2(c, 5, 3, 30, book) == ProposalCode.Stale, "gap (out of order) -> Stale, nothing granted");
        LootTxnRequest t;
        Expect(!LootTxnRequest.TryParse(new LootTxnRequest { Txn = 0, Ns = Ns, Hash = 30 }.Encode(), out t) && !LootTxnRequest.TryParse(new byte[19], out t), "txn 0 / short request refused");
    }

    static void ReloadAndRespawn()
    {
        var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>(); var book = new LootBook();
        book.Commit(Ns, 40, reg, o); book.Commit(Ns, 41, reg, o);
        Expect(book.ClearRegion(Ns, reg, o) == 2 && Taken(reg).Count == 0, "region emptied: live list cleared (the original reload re-creates loot)");
        // reload: the host seeds by the original rule on its own keys (ItemDay_ in the future / unique picked) = what its <Start> destroyed
        int today = 12;
        Expect(LootRules.PersistentlyTaken(LootClass.Valuable, 15, 1, today), "ItemDay_ 15 > day 12 -> seeded taken");
        Expect(LootRules.PersistentlyTaken(LootClass.Money, 13, 1, today), "money respawn day ahead -> seeded taken");
        Expect(LootRules.PersistentlyTaken(LootClass.UniqueValuable, 0, 1, today), "unique picked for good -> seeded taken");
        Expect(!LootRules.PersistentlyTaken(LootClass.Valuable, 12, 1, today), "respawn day reached -> original respawn, available");
        Expect(!LootRules.PersistentlyTaken(LootClass.Excluded, 99, 1, today), "excluded objects are never seeded (P2 is refused anyway)");
        var src = Src("WorldLootSync.cs");
        Expect(src.Contains("void SeedRegion(") && src.Contains("LootRules.PersistentlyTaken(cls, get(LootKeys.Day(e.Name)), get(LootKeys.Picked(e.Name)), today)"), "seeding uses the original reload rule for every shared class");
        Expect(src.Contains("FbppApi.ReadModel(out keys, out err)"), "seeding reads the host model once (O(1) lookups), not one linear FBPP scan per object");
    }

    static void UnsupportedRefused()
    {
        string why;
        foreach (var f in new[] { LootFlags.Heavy, LootFlags.StoryPick, LootFlags.StoryName, LootFlags.StoryCar, LootFlags.NumericName, LootFlags.Ambiguous, LootFlags.LongName })   // W4.2D remaining exclusions
        {
            var cls = LootRules.Classify(new LootEntry { Name = "x", Flags = f }, Facts(), out why);
            Expect(cls == LootClass.Excluded && LootRules.ClientPolicy(true, true, cls, false) == P2PickPolicy.Refuse, f + " -> refused for Player 2 (no local pickup)");
        }
        foreach (var fx in new[] { new ItemFacts { Known = true, Drone = true, Value = 5, AddXp = 5 }, new ItemFacts { Known = true, Story = true, Value = 5, AddXp = 5 }, new ItemFacts { Known = true, Mission = true, Value = 5, AddXp = 5 },
                                   new ItemFacts { Known = true, Special = true, Value = 5, AddXp = 5 }, new ItemFacts { Known = true, Heavy = true, Value = 5, AddXp = 5 }, new ItemFacts { Known = false } })
        {
            var cls = LootRules.Classify(new LootEntry { Name = "y" }, fx, out why);
            Expect(LootRules.ClientPolicy(true, true, cls, false) == P2PickPolicy.Refuse, "item facts excluded -> refused: " + why);
        }
        Expect(LootRules.ClientPolicy(false, false, LootClass.Excluded, false) == P2PickPolicy.Refuse, "unsupported region (Hillville / hideout / missions) -> refused");
        Expect(LootRules.ClientPolicy(true, false, LootClass.Excluded, false) == P2PickPolicy.Refuse, "MainScene / runtime object (not in the region catalog) -> refused");
        Expect(LootRules.ClientPolicy(false, false, LootClass.Excluded, true) == P2PickPolicy.LocalOwnDrop && LootRules.ClientPolicy(true, false, LootClass.Excluded, true) == P2PickPolicy.LocalOwnDrop, "Player 2's own unshared drop -> picked back locally (exists only in its world)");
        Expect(LootRules.ClientPolicy(true, true, LootClass.Excluded, true) == P2PickPolicy.Refuse, "a census scene object is never treated as an own drop");
        foreach (var cls in new[] { LootClass.Valuable, LootClass.UniqueValuable, LootClass.Money }) Expect(LootRules.ClientPolicy(true, true, cls, false) == P2PickPolicy.Shared, cls + " -> host transaction");
        Expect(LootRules.SupportedNamespace(Madison) && LootRules.SupportedNamespace(Hillville) && LootRules.SupportedNamespace(Hideout) && LootRules.SupportedNamespace(IdentityNamespace.MainScene), "W4.2D: every region + MainScene supported");
    }

    static void LootSourceScan()
    {
        var s = Src("WorldLootSync.cs");
        int client = s.IndexOf("switch (LootRules.ClientPolicy(supported, inCat, cls, dropped))", StringComparison.Ordinal);
        Expect(client > 0 && s.IndexOf("case P2PickPolicy.Refuse:", client, StringComparison.Ordinal) > client, "client decision goes through ClientPolicy");
        Expect(!s.Contains("original local pickup\");") || s.Contains("Player 2 cannot pick it"), "the former client fallback 'not shared - original local pickup' is gone");
        int host = s.IndexOf("if (_host)", s.IndexOf("public bool AllowPick(", StringComparison.Ordinal), StringComparison.Ordinal);
        Expect(host > 0 && s.Substring(host, 900).Contains("ShadowClaim(ns, e, pick, excl); return true;"), "host: excluded objects keep the original pickup (never refused) + shadow claim");
        Expect(s.Contains("static bool HeavyPrefix(") && s.Contains("AccessTools.Method(pk, \"PickUpHeavy\", new[] { typeof(float) })") && s.Contains("return i._host || i.RefuseP2Heavy(c);"), "Pickupable.PickUpHeavy(float) guarded: H1 lease (HeavySync) or the v0.16.1 client refusal");
        Expect(s.Contains("persist = LootRules.TakenByKeys(c0, (e.Flags & LootFlags.SharedName) != 0"), "host decision uses the family-aware persistence (W4.2D occupancy seeding)");
        Expect(s.Contains("ACCEPTED by the host") && s.Contains("refused: \" + why + \" - no shared transaction possible"), "diagnostics: acceptance + refusal reasons");
        Expect(!s.Contains("GUI.") && !s.Contains("OnGUI"), "no mod text in the game UI");
    }

    // ------------------------------------------------------------------------------------------------ B loading
    static void LoadingRule()
    {
        foreach (var s in new[] { MpState.HostStarting, MpState.HostArmed, MpState.HostReady, MpState.HostWaiting, MpState.Offline, MpState.Error, MpState.Disconnecting, MpState.Connected })
            Expect(!MenuRules.JoinLoadingScreen(s, GameContext.MainMenu, false), s + ": main menu stays usable");
        foreach (var s in new[] { MpState.ClientConnecting, MpState.ClientPreparing, MpState.ClientSynchronizing })
        {
            Expect(MenuRules.JoinLoadingScreen(s, GameContext.MainMenu, false), s + ": original loading state, menu locked");
            Expect(!MenuRules.JoinLoadingScreen(s, GameContext.MainMenu, true), s + " after Continue: the original owns the screen");
            Expect(!MenuRules.JoinLoadingScreen(s, GameContext.Loading, false) && !MenuRules.JoinLoadingScreen(s, GameContext.InWorld, false), s + ": never outside the main menu");
        }
    }

    static void LoadingFlow()
    {
        var lc = new Lifecycle(); double t = 0; JoinRequest req;
        Expect(!MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, lc.ContinueIssued), "single player / offline: untouched");
        Expect(lc.RequestJoin(GameContext.MainMenu, true, "203.0.113.5", "27960", JoinAuth.Format(JoinAuth.NewCode()), out req, t) == null, "join requested");
        Expect(MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, lc.ContinueIssued), "connecting: locked");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connecting, null, null, "Host lädt", false, t += 1) == ClientAction.None && MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, lc.ContinueIssued), "host not ready (retrying): still locked");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Preparing, null, null, null, false, t += 1) == ClientAction.ArmIsolation && MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, lc.ContinueIssued), "authenticated: isolation armed while locked");
        lc.IsolationArmed(t); lc.SystemsConfirmed(t);
        Expect(lc.State == MpState.ClientSynchronizing && MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, lc.ContinueIssued), "waiting for the host campaign verification: locked");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, false, t += 1) == ClientAction.None, "no Continue before the campaign is written + verified");
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, true, t += 1) == ClientAction.ContinueGame && lc.ContinueIssued, "verified -> original Continue");
        Expect(!MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, lc.ContinueIssued), "hand-off: the original Continue shows its own loading screen");
        Expect(lc.ClientTick(GameContext.Loading, ConnState.Connected, null, null, null, true, t += 1) == ClientAction.None && lc.State == MpState.Connected, "world loading -> connected");
        JoinRequest r2; var lc2 = new Lifecycle();
        lc2.RequestJoin(GameContext.MainMenu, true, "203.0.113.5", "27960", JoinAuth.Format(JoinAuth.NewCode()), out r2, 0);
        Expect(!lc2.ContinueIssued, "a new join starts without the previous Continue flag");
    }

    static Lifecycle Joining(out double t)
    {
        var lc = new Lifecycle(); JoinRequest req; t = 0;
        lc.RequestJoin(GameContext.MainMenu, true, "203.0.113.5", "27960", JoinAuth.Format(JoinAuth.NewCode()), out req, t);
        return lc;
    }

    static void LoadingFailures()
    {
        double t; var lc = Joining(out t);
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connecting, null, null, null, false, t + lc.ConnectTimeoutSeconds + 1) == ClientAction.GiveUp && !MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, lc.ContinueIssued), "connection timeout -> menu restored");
        lc = Joining(out t);
        lc.ClientTick(GameContext.MainMenu, ConnState.Connecting, null, null, "wait", false, t + 1);
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connecting, null, null, "wait", false, t + lc.HostWaitTimeoutSeconds + 2) == ClientAction.GiveUp && lc.State == MpState.Error, "host never ready -> menu restored");
        lc = Joining(out t);
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Failed, RejectCode.AuthFailed, null, null, false, t + 1) == ClientAction.GiveUp && !MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, false), "wrong join code -> menu restored");
        lc = Joining(out t);
        lc.ClientTick(GameContext.MainMenu, ConnState.Preparing, null, null, null, false, t += 1); lc.IsolationArmed(t); lc.SystemsConfirmed(t);
        Expect(lc.ClientTick(GameContext.MainMenu, ConnState.Connected, null, null, null, false, t + lc.SyncTimeoutSeconds + 1) == ClientAction.Abort && !MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, false), "campaign never verified (SaveGuard / verification failure) -> abort, menu restored, no Continue");
        lc = Joining(out t);
        lc.ClientTick(GameContext.MainMenu, ConnState.Preparing, null, null, null, false, t += 1);
        lc.IsolationFailed("redirect not verified", t);
        Expect(lc.State == MpState.Error && !MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, false), "save isolation refused -> menu restored");
        lc = Joining(out t);
        lc.BeginStop(t + 1); Expect(!MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, false), "cancel (Abbrechen) -> disconnecting -> menu restored");
        lc.Stopped(t + 1); Expect(lc.State == MpState.Offline && !MenuRules.JoinLoadingScreen(lc.State, GameContext.MainMenu, false), "offline again");
    }

    static void LoadingSourceScan()
    {
        var j = Src("JoinLoadingScreen.cs"); var m = Src("MultiplayerMenu.cs"); var cb = Src("CampaignBootstrap.cs");
        foreach (var f in new[] { "\"Deactivate\"", "\"ActivateLoad\"", "\"showFunFacts\"", "\"funFactTimer\"", "UnityEngine.EventSystems.EventSystem" }) Expect(j.Contains(f), "original object/field used: " + f);
        Expect(!j.Contains("OnGUI") && !j.Contains("GUI.") && !j.Contains("new GameObject") && !j.Contains("AddComponent") && !j.Contains("Instantiate"), "no own UI, no new objects (original loading screen only)");
        Expect(!j.Contains("volumeDown") || j.Contains("left to the original Continue"), "music fade left to the original");
        Expect(!j.Contains("loadText") || j.Contains("loadText keeps its scene text"), "no invented progress text");
        int hand = m.IndexOf("_loading.HandOff();", StringComparison.Ordinal), inv = m.IndexOf("cont.Invoke(mm, null)", StringComparison.Ordinal);
        Expect(hand > 0 && inv > hand, "hand-off right before the original MainMenu.Continue");
        Expect(m.Contains("if (!ContinueOriginal()) { _lc.Fail(") && m.Contains("EndClient(\"continue failed\")"), "a Continue that cannot start ends the join (menu restored), never a hidden manual Continue");
        Expect(m.Contains("bool joining = _hostCode == null && (s == MpState.ClientConnecting") && m.Contains("MenuRules.JoinLoadingScreen(s, Context(), _lc.ContinueIssued)"), "host sessions never lock the menu; no context probe unless joining");
        Expect(m.Contains("if (!legacy) _loading = new JoinLoadingScreen(log);"), "launcher (legacy) mode unchanged");
        Expect(cb.Contains("if (!SaveGuard.Armed || _allowContinue) return true;"), "Continue stays gated until the host campaign was written and verified");
    }

    // ------------------------------------------------------------------------------------------------ C spawn
    static HostCheckpoint Cp(int region, bool saved, float x, float y, float z, int story = 42) { return new HostCheckpoint { Region = region, SavedPos = saved, HasPos = true, Pos = new Vec3(x, y, z), Story = story }; }

    static void FixedSpawnRegions()
    {
        string note;
        foreach (int r in new[] { Hideout, Verwood, AutoShop })
        {
            var p = SpawnRules.Decide(null, 5, Cp(r, true, 10, 2, 10), out note);
            Expect(p.Mode == SpawnMode.OriginalSpawn && p.Region == r && !p.HasTarget && p.Reason == SpawnReason.FirstJoin, Regions.Name(r) + ": the original fixed spawn (saved XYZ ignored like GameManager.Start): " + p);
        }
        var early = SpawnRules.Decide(null, 5, Cp(Madison, true, 10, 2, 10, 3), out note);
        Expect(early.Mode == SpawnMode.OriginalSpawn && early.Region == Madison, "Madison with Story <= 3: GameManager.Start places nobody (scene default)");
        Expect(SpawnRegions.LoadsSavedPosition(Hillville, 0) && SpawnRegions.LoadsSavedPosition(Madison, 4) && !SpawnRegions.LoadsSavedPosition(Madison, 3) && !SpawnRegions.LoadsSavedPosition(98, 50) && !SpawnRegions.LoadsSavedPosition(Hideout, 50), "only Madison (Story > 3) / Hillville load XPlayer..ZPlayer (98 is saved but never loaded)");
    }

    static void SavedPositionRegions()
    {
        string note;
        // manual save / quicksave / autosave in Madison or Hillville = SaveGame1 -> SaveGameNewSystem(true): Saved_Pos 1 + XYZ
        var model = new CampaignData(); model.Ints["CurrentLocation"] = Hillville; model.Ints["Saved_Pos"] = 1; model.Ints["Story"] = 50;
        model.Floats["XPlayer"] = 120f; model.Floats["YPlayer"] = 4f; model.Floats["ZPlayer"] = -33f;
        var p = SpawnRules.Decide(null, 5, HostCheckpoint.FromModel(model), out note);
        Expect(p.Mode == SpawnMode.RestoreNear && p.Region == Hillville && p.Target.X == 120f && p.Target.Z == -33f, "saved checkpoint -> beside the saved position (Player 1 loads there too): " + p);
        int[] rm; var snap = CampaignSnapshot.Build(model, P2Inventory.Initial(false), p, 0, 5, 1, out rm);
        Expect(snap.Data.Ints["CurrentLocation"] == Hillville && snap.Data.Ints["Saved_Pos"] == 1 && snap.Data.Floats["XPlayer"] == 120f, "sandbox keys for the original LoadPlayerPosition");
        // travel (Select<Map>): Saved_Pos 0 -> original IsStartingCar at the leave point; Player 2 beside the host's leave point
        var travel = Cp(Madison, false, 0, 0, 0); travel.HasLeavePoint = true; travel.LeavePoint = new Vec3(300f, 10f, 40f);
        p = SpawnRules.Decide(null, 5, travel, out note);
        Expect(p.Mode == SpawnMode.SafePoint && p.Region == Madison && p.Target.X == 300f && p.Reason == SpawnReason.FirstJoin, "travel checkpoint -> beside the original leave point (never the driver seat): " + p);
        // autosave disabled: the model keeps the older checkpoint (position keys change only at a real save)
        var older = Cp(Madison, true, 50, 2, 50);
        p = SpawnRules.Decide(null, 5, older, out note);
        Expect(p.Target.X == 50f, "no autosave -> the last real save decides");
    }

    static void RestartUsesCheckpoint()
    {
        string note;
        var live = new P2SpawnRecord { HistoryId = 5, Region = Madison, Pos = new Vec3(900, 5, 900), Yaw = 10, Seq = 40 };
        var inSession = SpawnRules.Decide(live, 5, Cp(Madison, true, 1, 2, 3), out note);
        Expect(inSession.Mode == SpawnMode.Restore && inSession.Target.X == 900f, "same host session: live position");
        // full restart / host menu return = a new host session: the live record is gone (memory only) -> checkpoint
        var after = SpawnRules.Decide(null, 5, Cp(Madison, true, 1, 2, 3), out note);
        Expect(after.Mode == SpawnMode.RestoreNear && after.Target.X == 1f && after.Reason == SpawnReason.FirstJoin, "never resurrects the unsaved position: " + after);
    }

    static void LiveRejoin()
    {
        string note;
        var p = SpawnRules.Decide(new P2SpawnRecord { HistoryId = 5, Region = Hillville, Pos = new Vec3(7, 8, 9), Yaw = 90, Context = SpawnContext.InVehicle, Seq = 2 }, 5, Cp(Madison, true, 1, 2, 3), out note);
        Expect(p.Mode == SpawnMode.RestoreNear && p.Region == Hillville && p.Reason == SpawnReason.VehicleOrHiding, "in a vehicle -> beside it, never the seat (vehicle sync owns seats)");
        p = SpawnRules.Decide(new P2SpawnRecord { HistoryId = 5, Region = Madison, Pos = new Vec3(7, 8, 9), Context = SpawnContext.PoliceChase, Seq = 2 }, 5, Cp(Hideout, false, 0, 0, 0), out note);
        Expect(p.Mode == SpawnMode.SafePoint && p.Reason == SpawnReason.StoryContext, "unsafe context -> nearest original safe point");
        p = SpawnRules.Decide(new P2SpawnRecord { HistoryId = 5, Region = Madison, Pos = new Vec3(7, 8, 9), Yaw = 33, Seq = 2 }, 5, Cp(Hideout, false, 0, 0, 0), out note);
        Expect(p.Mode == SpawnMode.Restore && p.Yaw == 33f && p.Region == Madison, "plain live rejoin keeps Player 2's own region (host elsewhere)");
    }

    static void SpawnEdgeCases()
    {
        string note;
        var other = SpawnRules.Decide(new P2SpawnRecord { HistoryId = 4, Region = Madison, Pos = new Vec3(7, 8, 9), Seq = 2 }, 5, Cp(Hillville, true, 1, 2, 3), out note);
        Expect(other.Region == Hillville && other.Reason == SpawnReason.FirstJoin, "outdated record (other save history) -> checkpoint");
        var mission = SpawnRules.Decide(new P2SpawnRecord { HistoryId = 5, Region = Bank, Pos = new Vec3(7, 8, 9), Seq = 2 }, 5, Cp(Hillville, true, 1, 2, 3), out note);
        Expect(mission.Region == Hillville, "live position in a mission scene -> checkpoint");
        foreach (int heist in new[] { 90, 91, 92, 93, 94, 95, 96, 98, -1 })
        {
            var h = SpawnRules.Decide(null, 5, Cp(heist, true, 1, 2, 3), out note);
            Expect(h.Region == Hideout && h.Mode == SpawnMode.OriginalSpawn && h.Reason == SpawnReason.HostInMissionScene, Regions.Name(heist) + " checkpoint -> hideout (multiplayer adaptation)");
        }
        // every plan the host can produce stays inside the protocol-27 wire domain (modes 1..5, reasons 1..8)
        var plans = new List<SpawnPlan>();
        foreach (int r in new[] { Hideout, Verwood, AutoShop, Madison, Hillville, Bank, Regions.None })
            foreach (bool saved in new[] { true, false })
            {
                var cp = Cp(r, saved, 1, 2, 3); cp.HasLeavePoint = true; cp.LeavePoint = new Vec3(4, 5, 6);
                plans.Add(SpawnRules.Decide(null, 5, cp, out note));
                foreach (var ctx in new[] { SpawnContext.None, SpawnContext.InVehicle, SpawnContext.Hiding, SpawnContext.BlockSaving })
                    plans.Add(SpawnRules.Decide(new P2SpawnRecord { HistoryId = 5, Region = r == Regions.None ? Madison : r, Pos = new Vec3(1, 1, 1), Context = ctx }, 5, cp, out note));
            }
        foreach (var p in plans)
        {
            Expect(p.Mode != SpawnMode.NearHost, "never NearHost (no teleport to Player 1)");
            var w = new NetDataWriter(); p.Write(w); SpawnPlan back;
            Expect(w.Length == SpawnPlan.Bytes && SpawnPlan.TryRead(new NetDataReader(w.CopyData()), out back) && back.Mode == p.Mode && back.Region == p.Region, "wire round trip of " + p);
            Expect(!SpawnRegions.LoadsSavedPosition(p.Region, 42) || p.HasTarget, "a position region always has a target (never XPlayer..ZPlayer = 0): " + p);
        }
    }

    static void SpawnSourceScan()
    {
        var cb = Src("CampaignBootstrap.cs");
        foreach (var bad in new[] { "File.ReadAllBytes", "Quarantine(", "new FleetWriter", "new FleetStore", "File.Delete", "File.Move", "WriteAllBytes" })
            Expect(cb.IndexOf(bad, StringComparison.Ordinal) < 0, "the host never reads / writes / renames spawn files: " + bad);
        Expect(cb.Contains("File.Exists(legacy)") && cb.Contains("left untouched, not used"), "retired p2spawn files only noticed");
        Expect(cb.Contains("var cp = Checkpoint(model, hostRegion);") && cb.Contains("SpawnRules.Decide(_rec, p2.HostHistoryId, cp, out note)"), "plan from the host checkpoint + memory-only live position");
        Expect(cb.Contains("(_plan.Reason == SpawnReason.Rejoin || _plan.Reason == SpawnReason.VehicleOrHiding) && _pcbSetYaw != null"), "yaw only for a live position");
        Expect(cb.Contains("if (region == _plan.Region) PlaceSpawn(gm);"), "placement only where the original loaded the planned region (travel / vehicle arrivals untouched)");
        var c = Shared("Campaign.cs");
        Expect(c.Contains("PlayerScript.Quicksave") && c.Contains("SaveGameNewSystem(savePos, time)") && c.Contains("Inventory.autoSaveToggle"), "verified save rules documented next to the code");
        Expect(Shared("Protocol.cs").Contains("public const int ProtocolVersion = 39;"), "v0.16.1 spawn changes added no wire values (protocol 28 = W4.2D loot namespaces, 29 = H1 heavy leases, 30 = H2 heavy cargo, 31 = V1.2 vehicle purchase, 32 = W1 hideout purchases, 33 = V1.4.1 medkit, 34 = H3 heavy sale, 35 = W2A breakables, 36 = W2B parcel codes, 37 = W6A shared story, 38 = W6B drone / cameras / story call, 39 = W6B.2 unpack)");
    }
}
