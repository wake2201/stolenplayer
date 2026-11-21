// W6B.3 offline tests: shared mission-box contents + cooperative story tasks (two-PC report 0.28.0: P2 opened the box but saw no
// contents, P1 still saw the box; P2's plant at 110 / kitchen photo at 105 completed only locally, payout reverted; story 22 -> 21
// after the host's GameManager.LoadCheckpoint). Real Shared code: WorldHost.CompleteMission / AdvanceStory / ItemTransaction,
// MissionRules, MissionState, WorldItemBook. The host plugin (IStoryHost + IMissionHost) is modelled from the IL of
// Mission_Item.Mission_Complete / MissionComplete1 and StoryManager.AdvanceStory; the Unity glue (HeavyUnpackSync, WorldItemSync,
// StoryMissions) is source-scanned. In-game: docs/TESTING.md section 57.
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class W6B3Tests
{
    static int _passed, _failed;
    const int Hideout = 0, Madison = 99, Hillville = 97;
    const int S16 = 20, S17 = 21, S18 = 22, S19 = 23;
    static readonly string[] Keys = { "story16", "story17", "story18", "story19" };
    static uint _pid = 9000;

    static int Main()
    {
        Run("01 wire + tables: missionAct / missions codecs, fuzz, listed tasks 110 plant (story17) / 105 photo (story18), story16 Blackbay trigger, keys, protocol 40 / mod 0.29.0", Wire);
        Run("02 P2 plants at 110: host commits once (key 2, payout 250 once, story17 -> story18), replay / second request Stale, cached retransmission", P2Plant);
        Run("03 P2 photographs the 105 kitchen: one commit, story18 -> story19, payout once; P1 then gets nothing", P2Photo);
        Run("04 P1 completes first with the original: P2's request afterwards Stale (no second payout / story step); missions state follows", P1First);
        Run("05 invalid actions refused without any effect: unlisted task, wrong kind, wrong stage, not accepted, wrong region, too far, slot 1, free mode, host loading", Invalid);
        Run("06 host checkpoint rollback (story 22 -> 21, key 2 -> 1, money restored): followed, not forced forward; the task completes once again", Checkpoint);
        Run("07 story16 Blackbay sale by P2 (holder of the shared package): one advance; wrong stage Stale; ReadNote kind on the Blackbay path refused", Blackbay);
        Run("08 shared contents registry: one host item (origin P1, txn 0), P2 or P1 claims it once, simultaneous claims one winner, region rules", Contents);
        Run("09 Unity glue: contents postfixes + Keep + ItemCardboard reset, mission hooks / undo / resume, wallet unchanged, no UI text", Glue);
        Run("10 regressions: story14 note trigger, W6B.2 Unpack op, W6B call keys unchanged", Regressions);
        Console.WriteLine(_failed == 0 ? "W6B3 TESTS PASSED (" + _passed + ")" : "W6B3 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- host model (StorySync IStoryHost + StoryMissions IMissionHost)
    sealed class HostGame : IStoryHost, IMissionHost
    {
        public int Story; public bool Loaded = true, Free; public long Money = 1000, MoneyMade; public int Advances, Payouts;
        public readonly Dictionary<string, int> Prefs = new Dictionary<string, int>();
        public bool Near = true; public string NearWhy = "too far"; public WorldHost Wh;
        public HostGame(int story) { Story = story; Initiate(); }
        string Key { get { return Story - S16 >= 0 && Story - S16 < Keys.Length ? Keys[Story - S16] : "story" + Story; } }
        // StoryManager.InitiateStory accept_job_on_start: <house>_mission_<job> = 1 when <= 0
        void Initiate()
        {
            if (Key == "story17" && Pref("110_mission_0") <= 0) Prefs["110_mission_0"] = 1;
            if (Key == "story18" && Pref("105_mission_0") <= 0) Prefs["105_mission_0"] = 1;
        }
        public int Pref(string k) { int v; return Prefs.TryGetValue(k, out v) ? v : 0; }
        public bool TryCurrent(out int index, out string key, out bool free) { index = Story; key = Key; free = Free; return Loaded; }
        public bool Advance(int expected) { if (Story != expected || Free) return false; Story++; Advances++; Initiate(); return true; }
        public bool TryMissionPref(MissionTarget t, out int pref) { pref = Pref(t.PrefKey); return Loaded; }
        public bool PlayerNear(byte slot, MissionTarget t, out string why) { why = Near ? null : NearWhy; return Near; }
        // MissionComplete1 campaign part: key 2, payout, AdvanceStory when the stage matches
        public bool Complete(MissionTarget t, byte slot, out string why)
        {
            why = null; Prefs[t.PrefKey] = 2; Money += 250; MoneyMade += 250; Payouts++;
            if (Key == t.StageKey) Advance(Story);
            return true;
        }
        public void Publish() { var s = new MissionState(); foreach (var t in MissionTargets.All) s.Set(t.House, t.Mission, Pref(t.PrefKey)); Wh.PublishMissions(s); }
        // Player 1's own original Mission_Complete (vanilla on the host, then the MissionPost publish)
        public bool OriginalComplete(MissionTarget t)
        {
            if (Pref(t.PrefKey) != 1) return false;
            Complete(t, 1, out _why); Publish(); Wh.PublishStory(0);
            return true;
        }
        static string _why;
        // GameManager.LoadCheckpoint: the save before the task (story, key, money of that save)
        public void LoadCheckpoint(int story, Dictionary<string, int> prefs, long money) { Story = story; Prefs.Clear(); foreach (var kv in prefs) Prefs[kv.Key] = kv.Value; Money = money; Publish(); Wh.PublishStory(0); }
    }

    static WorldHost NewHost(out HostGame hg, int story, int p2Region = Madison)
    {
        var wh = new WorldHost(77); wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, p2Region, 0);
        hg = new HostGame(story) { Wh = wh }; wh.StoryHost = hg; wh.MissionHost = hg; wh.PublishStory(0); hg.Publish(); wh.Outbox.Clear();
        return wh;
    }
    static MissionActionRequest Act(int from, int house, int mission, MissionActionKind kind, string key)
    { return new MissionActionRequest { FromIndex = from, House = house, Mission = mission, Kind = kind, StageKey = key }; }
    static ProposalCode Send(WorldHost wh, byte slot, string key, byte[] value, uint id = 0)
    { uint rev; int sr; float sh; return wh.HandleProposal("cli", slot, id != 0 ? id : ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = key, Value = value }, 1, out rev, out sr, out sh); }
    static ProposalCode P2(WorldHost wh, MissionActionRequest r, uint id = 0) { return Send(wh, 2, WorldKeys.MissionAction, r.ToBytes(), id); }
    static StoryState Story(WorldHost wh) { var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Story); StoryState s; return e != null && StoryState.TryParse(e.Value, out s) ? s : null; }
    static int Shared(WorldHost wh, int house, int mission)
    {
        var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Missions); MissionState s; int p;
        Expect(e != null && MissionState.TryParse(e.Value, out s) && s.TryGet(house, mission, out p), "missions record has " + house + "[" + mission + "]");
        MissionState.TryParse(e.Value, out s); s.TryGet(house, mission, out p); return p;
    }

    // ---------------------------------------------------------------- 01
    static void Wire()
    {
        var r = Act(S17, 110, 0, MissionActionKind.Plant, "story17"); MissionActionRequest b;
        Expect(MissionActionRequest.TryParse(r.ToBytes(), out b) && b.House == 110 && b.Mission == 0 && b.Kind == MissionActionKind.Plant && b.FromIndex == S17 && b.StageKey == "story17", "request round trip");
        Expect(r.ToBytes().Length == 15, "request 15 B, got " + r.ToBytes().Length);
        Expect(!MissionActionRequest.TryParse(null, out b) && !MissionActionRequest.TryParse(new byte[] { 2, 21, 0, 110, 0, 0, 1, 1, 65 }, out b)
            && !MissionActionRequest.TryParse(new byte[] { 1, 21, 0, 110, 0, 0, 3, 1, 65 }, out b) && !MissionActionRequest.TryParse(new byte[] { 1, 21, 0, 110, 0, 0, 1, 2, 65 }, out b)
            && !MissionActionRequest.TryParse(new byte[] { 1, 21, 0, 110, 0, 0, 1, 1, 32 }, out b) && !MissionActionRequest.TryParse(new byte[] { 1, 21, 0, 110, 0, 0, 1, 0 }, out b), "bad version / kind / length / key refused");
        var s = new MissionState(); s.Set(110, 0, 2); s.Set(105, 0, -1); MissionState back; int p;
        Expect(MissionState.TryParse(s.ToBytes(), out back) && back.SameAs(s) && back.TryGet(105, 0, out p) && p == -1 && s.ToBytes().Length == 10, "state round trip (negative key kept)");
        Expect(!MissionState.TryParse(new byte[] { 1, 1, 0 }, out back) && !MissionState.TryParse(new byte[] { 2, 0 }, out back) && !MissionState.TryParse(new byte[] { 1, 33 }, out back), "bad state refused");
        var rnd = new Random(5); for (int i = 0; i < 5000; i++) { var x = new byte[rnd.Next(0, 40)]; rnd.NextBytes(x); MissionActionRequest.TryParse(x, out b); MissionState.TryParse(x, out back); }
        var t110 = MissionTargets.Find(110, 0); var t105 = MissionTargets.Find(105, 0);
        Expect(MissionTargets.All.Length == 2 && t110 != null && t110.Kind == MissionActionKind.Plant && t110.StageKey == "story17" && t110.PrefKey == "110_mission_0" && t110.Region == Madison
            && t105 != null && t105.Kind == MissionActionKind.Photo && t105.StageKey == "story18" && t105.PrefKey == "105_mission_0" && MissionTargets.Find(110, 1) == null && MissionTargets.Find(105, 1) == null, "listed tasks (only the two verified story tasks)");
        Expect(MissionTargets.PathMatches(t110, "DOMY/DOM 110/MISSION PLANT/Fake") && !MissionTargets.PathMatches(t110, "DOMY/XDOM 110/MISSION PLANT/Fake") && !MissionTargets.PathMatches(t110, "DOMY/DOM 101/MISSION PLANT/Fake")
            && MissionTargets.PathMatches(t105, "DOMY/Soda Store/STORY PHOTOGRAPH/GameObject/PHOTOGRAPH MISSION") && !MissionTargets.PathMatches(t105, "DOMY/DOM 110/GameObject/PHOTOGRAPH MISSION"), "path suffix rules (110 kitchen photo is not the story photo)");
        Expect(StoryTriggers.Find("story16", StoryTriggerKind.BlackbaySale, "IMPORTANT OBJECTS/Canvases/HIDE CANVAS OBJECT/Computer Canvas/COMPUTER/ContentCanvas/ContentWrapper/MAIN WINDOW/blackbay.xyz/blackbay/BG/WINDOW RIGHT/OTHERS/Viewport/Content/PACKAGE") != null
            && StoryTriggers.Find("story16", StoryTriggerKind.BlackbaySale, ".../OTHERS/Viewport/Content/BRIEFCASE") == null && StoryTriggers.Find("story26", StoryTriggerKind.BlackbaySale, "OTHERS/Viewport/Content/BRIEFCASE") == null, "story16 PACKAGE listed, other listings not");
        var sr = new StoryAdvanceRequest { FromIndex = S16, StageKey = "story16", Kind = StoryTriggerKind.BlackbaySale, Path = "OTHERS/Viewport/Content/PACKAGE" }; StoryAdvanceRequest sb;
        Expect(StoryAdvanceRequest.TryParse(sr.ToBytes(), out sb) && sb.Kind == StoryTriggerKind.BlackbaySale, "storyAdvance kind 2 round trip");
        Expect(WorldKeys.MissionAction == "missionAct" && WorldKeys.Missions == "missions" && ProtocolInfo.ProtocolVersion == 41 && ProtocolInfo.ModVersion == "0.30.0", "keys / protocol 40 / mod 0.29.0");
    }

    // ---------------------------------------------------------------- 02
    static void P2Plant()
    {
        HostGame hg; var wh = NewHost(out hg, S17);
        Expect(Shared(wh, 110, 0) == 1, "task accepted at story17 (accept_job_on_start)");
        uint id = ++_pid; var r = Act(S17, 110, 0, MissionActionKind.Plant, "story17");
        Expect(P2(wh, r, id) == ProposalCode.Accepted, "P2 plant accepted");
        Expect(hg.Pref("110_mission_0") == 2 && hg.Payouts == 1 && hg.Money == 1250 && hg.Story == S18 && Story(wh).Index == S18 && Story(wh).By == 2 && Shared(wh, 110, 0) == 2, "key 2, payout once, story18 published by P2");
        Expect(P2(wh, r, id) == ProposalCode.Accepted && hg.Payouts == 1 && hg.Story == S18, "retransmission answered from the cache (no second payout)");
        Expect(P2(wh, r) == ProposalCode.Stale && hg.Payouts == 1 && hg.Story == S18, "replay with a new id: Stale");
        Expect(wh.MissionCompletions == 1, "one host completion");
    }

    // ---------------------------------------------------------------- 03
    static void P2Photo()
    {
        HostGame hg; var wh = NewHost(out hg, S18);
        Expect(P2(wh, Act(S18, 105, 0, MissionActionKind.Photo, "story18")) == ProposalCode.Accepted && hg.Story == S19 && hg.Payouts == 1 && Shared(wh, 105, 0) == 2, "photo committed once, story19");
        Expect(!hg.OriginalComplete(MissionTargets.Find(105, 0)) && hg.Payouts == 1, "P1's original afterwards returns at once (key 2, original Mission_Complete guard)");
    }

    // ---------------------------------------------------------------- 04
    static void P1First()
    {
        HostGame hg; var wh = NewHost(out hg, S17);
        Expect(hg.OriginalComplete(MissionTargets.Find(110, 0)) && hg.Story == S18 && Shared(wh, 110, 0) == 2, "P1 original completion: key 2 published");
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Stale && hg.Payouts == 1 && hg.Story == S18, "P2's later request Stale: no second payout / step");
        Expect(MissionRules.ClientPresentDone(1, 2) && !MissionRules.ClientPresentDone(2, 2) && !MissionRules.ClientPresentDone(2, 1), "P2 presents P1's completion once, never a rollback as done");
        // the other order in the same host step: P2 first, then P1's own original -> nothing
        HostGame hg2; var wh2 = NewHost(out hg2, S17);
        Expect(P2(wh2, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Accepted && !hg2.OriginalComplete(MissionTargets.Find(110, 0)) && hg2.Payouts == 1 && hg2.Advances == 1, "P2 then P1: one payout, one step");
    }

    // ---------------------------------------------------------------- 05
    static void Invalid()
    {
        HostGame hg; var wh = NewHost(out hg, S17);
        Expect(P2(wh, Act(S17, 110, 1, MissionActionKind.Photo, "story17")) == ProposalCode.Invalid, "unlisted task 110[1]");
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Photo, "story17")) == ProposalCode.Invalid, "wrong kind (photo for the plant task)");
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story18")) == ProposalCode.Invalid, "stage key not the task's");
        Expect(P2(wh, Act(S17, 105, 0, MissionActionKind.Photo, "story18")) == ProposalCode.Stale, "105 photo before story18: key 0 (not accepted) -> Stale");
        Expect(P2(wh, Act(S16, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Stale, "old index -> Stale");
        hg.Near = false; hg.NearWhy = "Player 2 is in Hillville";
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.NotAuthorized, "wrong region / too far -> NotAuthorized");
        hg.Near = true;
        Expect(Send(wh, 1, WorldKeys.MissionAction, Act(S17, 110, 0, MissionActionKind.Plant, "story17").ToBytes()) == ProposalCode.Invalid, "slot 1 never proposes (host runs the original)");
        Expect(Send(wh, 2, WorldKeys.MissionAction, new byte[] { 1, 2, 3 }) == ProposalCode.Invalid, "malformed");
        hg.Loaded = false; Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Busy, "host loading -> Busy (retry)"); hg.Loaded = true;
        hg.Free = true; Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Unchanged, "free mode -> Unchanged"); hg.Free = false;
        Expect(hg.Payouts == 0 && hg.Story == S17 && hg.Pref("110_mission_0") == 1 && hg.Money == 1000 && wh.MissionCompletions == 0, "nothing changed by any refusal");
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Accepted && hg.Payouts == 1, "the valid action still works afterwards");
    }

    // ---------------------------------------------------------------- 06
    static void Checkpoint()
    {
        HostGame hg; var wh = NewHost(out hg, S17);
        var save = new Dictionary<string, int>(hg.Prefs); long savedMoney = hg.Money;
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Accepted && hg.Story == S18 && Shared(wh, 110, 0) == 2, "committed: story18");
        hg.LoadCheckpoint(S17, save, savedMoney);   // real log: host published story 22, LoadCheckpoint, host published story 21
        Expect(Story(wh).Index == S17 && Shared(wh, 110, 0) == 1 && hg.Money == savedMoney, "rollback followed: story 21, key 1, money of the checkpoint");
        Expect(!MissionRules.ClientPresentDone(2, 1), "P2 never forced forward by a rollback (sandbox key goes back to 1)");
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Accepted && hg.Story == S18 && hg.Money == savedMoney + 250, "the restored task completes once again (one payout in the restored history)");
        Expect(P2(wh, Act(S17, 110, 0, MissionActionKind.Plant, "story17")) == ProposalCode.Stale && hg.Money == savedMoney + 250, "and only once");
    }

    // ---------------------------------------------------------------- 07
    static void Blackbay()
    {
        HostGame hg; var wh = NewHost(out hg, S16);
        var sale = new StoryAdvanceRequest { FromIndex = S16, StageKey = "story16", Kind = StoryTriggerKind.BlackbaySale, Path = "OTHERS/Viewport/Content/PACKAGE" };
        Expect(Send(wh, 2, WorldKeys.StoryAdvance, sale.ToBytes()) == ProposalCode.Accepted && hg.Story == S17 && Story(wh).Key == "story17", "P2 story16 sale: host AdvanceStory once");
        Expect(Send(wh, 2, WorldKeys.StoryAdvance, sale.ToBytes()) == ProposalCode.Stale && hg.Story == S17, "second sale request Stale");
        HostGame hg2; var wh2 = NewHost(out hg2, S17);
        Expect(Send(wh2, 2, WorldKeys.StoryAdvance, new StoryAdvanceRequest { FromIndex = S17, StageKey = "story17", Kind = StoryTriggerKind.BlackbaySale, Path = "OTHERS/Viewport/Content/PACKAGE" }.ToBytes()) == ProposalCode.Invalid && hg2.Story == S17, "not listed for another stage");
        Expect(Send(wh2, 2, WorldKeys.StoryAdvance, new StoryAdvanceRequest { FromIndex = S16, StageKey = "story16", Kind = StoryTriggerKind.ReadNote, Path = "OTHERS/Viewport/Content/PACKAGE" }.ToBytes()) == ProposalCode.Invalid, "wrong kind for the path");
    }

    // ---------------------------------------------------------------- 08
    static void Contents()
    {
        var wh = new WorldHost(78); wh.SetPlayerRegion(1, Hideout, 0); wh.SetPlayerRegion(2, Hideout, 0);
        int p2Picks = 0; wh.ItemTxnHook = (slot, t) => { if (slot == 2 && t.Op == ItemOp.Pickup) p2Picks++; return ProposalCode.Accepted; };
        string why;
        // HeavyUnpackSync.RegisterContents -> WorldItemSync.HostRegisterContents: Drop by slot 1, txn 0, at the original pose
        var drop = new ItemTxnRequest { Op = ItemOp.Drop, Txn = 0, ItemId = 91, Region = Hideout, Pos = new Vec3(1, 2, 3), Rot = Quat16.From(0, 0, 0, 1) };
        Expect(wh.ItemTransaction(1, drop, 1, out why) == ProposalCode.Accepted, "host registers the contents");
        uint wid = wh.Items.LastApplied; WorldItem it; int r;
        Expect(wh.Items.TryGet(wid, out it, out r) && r == Hideout && it.ItemId == 91 && it.Origin == 1 && it.OriginTxn == 0 && wh.Items.CountIn(Hideout) == 1, "one logical item (origin P1, txn 0: no P1 inventory journal)");
        var e = wh.Reg.Get(StateScope.Region, Hideout, WorldKeys.WorldItems); List<WorldItem> l;
        Expect(e != null && WorldItemList.TryParse(e.Value, out l) && l.Count == 1 && l[0].Id == wid, "committed for Player 2's presentation");
        uint id = ++_pid; var pick = new ItemTxnRequest { Op = ItemOp.Pickup, Txn = 1, WorldId = wid, ItemId = 91, Region = Hideout };
        Expect(Send(wh, 2, WorldKeys.ItemTxn, pick.Encode(), id) == ProposalCode.Accepted && p2Picks == 1 && wh.Items.CountIn(Hideout) == 0, "P2 claims it");
        string w2; Expect(wh.ItemTransaction(1, new ItemTxnRequest { Op = ItemOp.Pickup, WorldId = wid, ItemId = 91, Region = Hideout }, 1, out w2) == ProposalCode.Stale, "P1 afterwards: Stale (gone for the other player)");
        Expect(Send(wh, 2, WorldKeys.ItemTxn, pick.Encode(), id) == ProposalCode.Accepted && p2Picks == 1, "retransmitted claim: cached, not a second unit");
        // P1 first, then P2
        Expect(wh.ItemTransaction(1, drop, 1, out why) == ProposalCode.Accepted, "second box contents"); uint w3 = wh.Items.LastApplied;
        Expect(wh.ItemTransaction(1, new ItemTxnRequest { Op = ItemOp.Pickup, WorldId = w3, ItemId = 91, Region = Hideout }, 1, out w2) == ProposalCode.Accepted, "P1 claims");
        Expect(Send(wh, 2, WorldKeys.ItemTxn, new ItemTxnRequest { Op = ItemOp.Pickup, Txn = 2, WorldId = w3, ItemId = 91, Region = Hideout }.Encode()) == ProposalCode.Stale && p2Picks == 1, "P2 afterwards Stale");
        // P2 elsewhere cannot claim it
        Expect(wh.ItemTransaction(1, drop, 1, out why) == ProposalCode.Accepted, "third"); uint w4 = wh.Items.LastApplied;
        wh.SetPlayerRegion(2, Madison, 0);
        Expect(Send(wh, 2, WorldKeys.ItemTxn, new ItemTxnRequest { Op = ItemOp.Pickup, Txn = 3, WorldId = w4, ItemId = 91, Region = Hideout }.Encode()) == ProposalCode.NotAuthorized && wh.Items.CountIn(Hideout) == 1, "P2 not in the hideout: refused");
    }

    // ---------------------------------------------------------------- 09
    static void Glue()
    {
        var un = Src("Plugin", "HeavyUnpackSync.cs"); var wi = Src("Plugin", "WorldItemSync.cs"); var sm = Src("Plugin", "StoryMissions.cs"); var ss = Src("Plugin", "StorySync.cs"); var pl = Src("Plugin", "Plugin.cs");
        Expect(un.Contains("\"SpawnPostfix\"") && un.Contains("\"SpawnStartPostfix\"") && un.Contains("HostRegisterContents(go, _contentsItem") && un.Contains("_prefSetInt(\"ItemCardboard\", -1)")
            && un.Contains("items.HostItemTaken = OnContentsTaken") && un.Contains("_world.LocalRegion != TravelRules.Hideout") && un.Contains("ContentsFrame(now);"), "host: original SpawnItem / SpawnItemOnStart result registered, re-check, taken -> ItemCardboard -1");
        Expect(un.Contains("if (UnpackRules.ClientMaySpawn(true, hs._host, _resumeUnpack)) return true;") && Strip(un).Contains("_mgSpawn(mg);"), "P2 never spawns contents; host RunUnpack still the original SpawnItem");
        Expect(wi.Contains("if (b.Keep && b.Go != null) continue;") && wi.Contains("!kv.Value.Keep) drop.Add") && wi.Contains("if (_bound.TryGetValue(it.Id, out kb) && kb.Keep) continue;")
            && wi.Contains("(b.Keep && b.Region != region)") && wi.Contains("kb.Keep = false; Unbind(t.WorldId, true);") && wi.Contains("if (HostItemTaken != null) HostItemTaken(wid, 1);"), "Keep: never destroyed by region change / clear / conversion; destroyed only when P2 took it");
        Expect(sm.Contains("AccessTools.Method(_miT, \"Mission_Complete\"") && sm.Contains("\"PlantedPre\"") && sm.Contains("\"SellPre\"") && sm.Contains("Undo(t, plant, photo);") && sm.Contains("_missionResume = true;")
            && sm.Contains("_world.Host.MissionHost = this") && sm.Contains("_prefSet(t.PrefKey, 2);") && sm.Contains("_advance(sm);") && sm.Contains("MissionBlockedPre"), "mission hooks: gate, undo, resume of the original, host campaign part, fail closed");
        Expect(ss.Contains("PatchMissions();") && ss.Contains("OnMissionResult(id, code)") && ss.Contains("MissionFrame(Now)") && ss.Contains("if (_missionResume)") && pl.Contains("_story.Players = _sync;"), "StorySync wiring");
        Expect(Src("Shared", "Wallet.cs").Contains("new MoneyWriter(\"Mission_Item\", \"MissionComplete1\", MoneyPolicy.Exclude"), "P2's local MissionComplete1 money stays excluded (the host pays the shared balance once)");
        foreach (var f in new[] { sm, un }) Expect(!f.Contains("OnGUI") && !f.Contains("GUI.") && !f.Contains("Side_Info_Spawner"), "no mod text in the gameplay UI");
        var sms = Strip(sm); int a = sms.IndexOf("void MissionFrame(double now)", StringComparison.Ordinal), z = sms.IndexOf("void FollowMissions()", StringComparison.Ordinal);
        Expect(a > 0 && z > a && !sms.Substring(a, z - a).Contains("FindObjectsOfType") && !sms.Substring(a, z - a).Contains("GameObject.Find"), "no scene search in the per-frame path (only at an event / registry change)");
    }

    // ---------------------------------------------------------------- 10
    static void Regressions()
    {
        Expect(StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, "DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3") != null && StoryTriggers.All.Length == 2, "story14 note still listed");
        Expect((byte)HeavyOp.Unpack == 9 && WorldKeys.StoryCall == "storyCall" && WorldKeys.StoryCallAnswer == "storyCallAnswer" && WorldKeys.StoryAdvance == "storyAdvance", "W6B / W6B.2 wire unchanged");
    }

    // ---------------------------------------------------------------- source helpers
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
