// W6A offline tests: shared story progression foundation. Drives the SAME Unity-free pieces as the plugin: StoryAdvanceRequest
// / StoryState (wire), StoryTriggers / StoryRules, WorldHost "storyAdvance" dispatch + PublishStory through the real proposal
// path (ProposalGate) and the registry snapshot / delta wire format, plus the W2B parcel path in the same session. Each PC is a
// simulated ORIGINAL StoryManager that follows the verified IL (AdvanceStory, InitiateStory incl. the in-place home / jail
// skips and the once-keys, BeginStory, SaveStory, SaveGameNewSystem "only if higher", ReadNote.BeginRead story branch,
// ReadNote.Start parcel hide); the host side implements IStoryHost exactly like src/Plugin/StorySync.cs, the client side runs
// its AdvanceStory prefix, trigger context and follow rules. Stage keys / flags are the real 1.27c data (stages 0..40,
// docs/game-atlas/data/mission_catalog.json). In-game behaviour (HUD, calls, StoryObject gates) is manual two-PC QA
// (docs/TESTING.md §49); the Unity glue is source-scanned (test 11).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W6ATests
{
    static int _passed, _failed;
    const int Madison = 99, Hideout = 0, Jail = 93;
    const int S14 = 18, S15 = 19;   // story14 / story15 indices

    static int Main()
    {
        Run("01 wire + rules: storyAdvance request / story state codecs, fuzz, size bound, trigger table, decisions, keys, protocol 37 / mod 0.26.0", Wire);
        Run("02 Player 2 reads note_103 at story14: host original AdvanceStory once, both PCs on story15, original stage start on both (call), no private copy", Player2Note);
        Run("03 Player 1 reads note_103: original host step, Player 2 follows; Player 2's later read finds the original condition false", Player1Note);
        Run("04 exactly once: retransmission (cache), replay with a new id, second read before / after the follow - one advance", ExactlyOnce);
        Run("05 concurrent: both players read note_103 in the same moment, either order - one advance, one stage start, one call", Concurrent);
        Run("06 prerequisites + start effects: wrong stage, unlisted trigger, free mode, host loading (busy + retry), host-only effects (chase, teleport, debt, location skips)", Prerequisites);
        Run("07 personal vs shared: note XP stays the reader's, parcel code shared once (W2B), no money / items / inventory from a story step", PersonalVsShared);
        Run("08 save / reload / reconnect / late join: host save only-if-higher, host reload, quit without save, client scene load, disconnect during a read", SaveReloadReconnect);
        Run("09 note visibility: parcel taken through the shared world before the step - note stays readable while story14 is current, never otherwise", NoteVisibility);
        Run("10 unlisted Player-2 story triggers and polls do nothing (no private step, no proposal); the host's own triggers keep working", Unlisted);
        Run("11 Unity glue: hooks, client never runs AdvanceStory, follow = BeginStory path with stripped flags restored, ParcelSync fallback, wiring, no scans", Glue);
        Console.WriteLine(_failed == 0 ? "W6A TESTS PASSED (" + _passed + ")" : "W6A TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- real 1.27c stage data (StoryManager.story_scripts 0..40)
    static readonly string[] Keys = { "story1_escape", "story2_escapeB", "story3_escape", "story4_breakin", "story5", "story5b", "story5c", "story5d", "story5e", "story6a", "story6", "story7", "story8", "story9", "story10", "story11", "story12", "story13", "story14", "story15", "story16", "story17", "story18", "story19", "story20", "story21", "story22", "story23", "story24", "story26", "story27", "story28", "story29", "story30", "story30b", "story30c", "story31", "story32", "story33", "story34", "story35" };
    sealed class Script { public string Key; public bool Home, Jail, Chase, First, Debt, Call, CallOnce; public int DebtAmount; }
    static List<Script> Scripts()
    {
        var l = Keys.Select(k => new Script { Key = k, Call = true, CallOnce = true }).ToList();
        l[0].First = true; l[0].CallOnce = false; l[1].Call = l[2].Call = false; l[3].First = true; l[3].CallOnce = false;
        l[9].Home = true; l[12].Chase = true; l[26].Jail = true; l[28].Home = true; l[40].Debt = true; l[40].DebtAmount = 30000;
        return l;
    }

    // ---------------------------------------------------------------- simulated original StoryManager of one PC
    sealed class SM
    {
        public List<Script> S = Scripts(); public int Story; public Script Current; public bool Free;
        public readonly Dictionary<string, int> Prefs = new Dictionary<string, int>();   // SPrefs (host: save; P2: sandbox)
        public int Location = Madison, Calls, Chases, Teleports, Hud, OriginalAdvances; public long Debt = 500000;
        public int Pref(string k) { int v; return Prefs.TryGetValue(k, out v) ? v : 0; }
        // StoryManager.AdvanceStory IL_0000..IL_001d
        public void AdvanceStory() { if (Free) return; OriginalAdvances++; Story++; Initiate(); }
        // StoryManager.InitiateStory (effects relevant here, in IL order)
        public void Initiate()
        {
            Current = S[Story]; Hud++;
            if (Current.Home && Location == Hideout) { Story++; Current = S[Story]; }
            if (Current.Jail && Location == Jail) { Story++; Current = S[Story]; }
            if (Current.Chase) Chases++;
            if (Current.First) Teleports++;
            if (Current.Debt && Pref(Current.Key + "_deduct") == 0) { Debt -= Current.DebtAmount; Prefs[Current.Key + "_deduct"] = 1; }
            if (Current.Call && (!Current.CallOnce || Pref(Current.Key + "_Call") == 0)) { Calls++; if (Current.CallOnce) Prefs[Current.Key + "_Call"] = 1; }
        }
        public void BeginStory() { Story = Math.Min(Pref("Story"), S.Count - 1); Initiate(); }   // scene load (clamped)
        public void SaveStory() { Prefs["Story"] = Story; }
        public void SaveGame() { if (Story > Pref("Story")) Prefs["Story"] = Story; }   // SaveGameNewSystem: only if higher
        public string Key { get { return Current != null ? Current.Key : null; } }
    }

    sealed class Note { public string Path, StoryDesc, Pref; public bool AddCode, Advance; public int Xp; }
    static readonly Note Note103 = new Note { Path = "DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3", StoryDesc = "story14", Pref = "parcel1_madison", AddCode = true, Advance = true, Xp = 0 };
    static readonly Note Note108 = new Note { Path = "DOMY/Dom 108 Old/ABANDONEDHOUSE/pPlane3", StoryDesc = "story14", Pref = "", AddCode = false, Advance = true };   // hypothetical advancing copy: not listed
    static readonly Note SkillBook = new Note { Path = "DOMY/Dom 106/Interior_106/book", StoryDesc = "", Pref = "", AddCode = false, Advance = false, Xp = 50 };

    // Host plugin side: exactly src/Plugin/StorySync.cs (TryCurrent / Advance) + the per-frame publish.
    sealed class HostGame : IStoryHost, IParcelHost
    {
        public readonly SM M = new SM(); public bool Loaded = true; public int Xp; public long Money = 1000;
        public readonly HashSet<string> Codes = new HashSet<string>(); public int CodeAnim;
        public HostGame(int index) { M.Story = index; M.Initiate(); M.Calls = 0; M.SaveGame(); }
        public bool TryCurrent(out int index, out string key, out bool free)
        {
            index = M.Story; key = M.Key; free = M.Free;
            return Loaded && M.Current != null && index >= 0 && index < M.S.Count && ReferenceEquals(M.S[index], M.Current) && !string.IsNullOrEmpty(key);
        }
        public bool Advance(int expected) { if (M.Story != expected) return false; M.AdvanceStory(); return M.Story != expected; }
        // ReadNote.BeginRead on the host's own PC (original, story part IL_015a..IL_0195 + parcel / XP parts)
        public void ReadNote(Note n)
        {
            if (n.AddCode && !Codes.Contains(n.Pref) && M.Pref(n.Pref) == 0) { Codes.Add(n.Pref); CodeAnim++; }
            Xp += n.Xp;
            if (n.Advance && M.Key == n.StoryDesc) M.AdvanceStory();
        }
        // IParcelHost (W2B, as ParcelSync)
        public bool TryDiscovered(out List<string> prefs) { prefs = Codes.ToList(); return Loaded; }
        public bool Known(string pref) { return pref == "parcel1_madison" || pref == "parcel201"; }
        public ParcelApply Apply(byte slot, ParcelSource src, string pref) { if (Codes.Contains(pref) || M.Pref(pref) != 0) return ParcelApply.AlreadyKnown; Codes.Add(pref); CodeAnim++; return ParcelApply.Applied; }
    }
    static void Frame(WorldHost wh) { wh.PublishStory(0); }   // StorySync.Update on the host (publish on change)

    // Client PC: AdvanceStory prefix + ReadNote trigger context + follow (StorySync), parcel proposal (ParcelSync).
    sealed class Client
    {
        public readonly SM M = new SM(); readonly WorldHost _wh; public StateRegistry Reg; int _sent;
        public bool Connected = true; public int Suppressed, Proposed, Followed, Xp; public readonly HashSet<string> Codes = new HashSet<string>();
        public readonly List<ProposalCode> Results = new List<ProposalCode>(); public uint LastPid; public byte[] LastBytes;
        string _ctx;
        public Client(WorldHost wh, int sandboxIndex) { _wh = wh; M.Prefs["Story"] = sandboxIndex; M.BeginStory(); M.Calls = 0; Reconnect(); }
        public void Reconnect()
        {
            var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, _wh.Reg);
            var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep, hd; List<StateEntry> entries;
            Expect(StateMessages.TryReadSnapshot(r, out ep, out hd, out entries), "snapshot");
            Reg = StateRegistry.CreateClient(); Reg.LoadSnapshot(ep, hd, entries); _sent = _wh.Outbox.Count; Connected = true;
            Follow();
        }
        // StorySync.AdvancePre on Player 2: the original never runs; a listed trigger is proposed.
        void AdvanceHook()
        {
            Suppressed++;
            var t = _ctx != null ? StoryTriggers.Find(M.Key, StoryTriggerKind.ReadNote, _ctx) : null;
            if (t == null || M.Free) return;
            Propose(new StoryAdvanceRequest { FromIndex = M.Story, StageKey = M.Key, Kind = t.Kind, Path = StoryTriggers.Canonical(t) });
        }
        // ReadNote.BeginRead on Player 2's PC (prefix sets the context, original body, postfix clears)
        public void ReadNote(Note n)
        {
            _ctx = n.Advance ? n.Path : null;
            if (n.AddCode && !Codes.Contains(n.Pref) && M.Pref(n.Pref) == 0) { Codes.Add(n.Pref); ProposeParcel(n.Pref); }
            Xp += n.Xp;
            if (n.Advance && M.Key == n.StoryDesc) AdvanceHook();   // original condition, then the AdvanceStory call (prefix)
            _ctx = null;
        }
        // any other original AdvanceStory caller on Player 2's PC (poll, camera, pickup ...)
        public void OtherAdvanceCall() { _ctx = null; AdvanceHook(); }
        public ProposalCode Propose(StoryAdvanceRequest r)
        {
            if (!Connected) return ProposalCode.Busy;   // StorySync.Send: not proposed while disconnected
            LastPid = _pid++; LastBytes = r.ToBytes(); Proposed++;
            var code = Send(LastPid, LastBytes, WorldKeys.StoryAdvance); Results.Add(code); return code;
        }
        void ProposeParcel(string pref) { if (Connected) Send(_pid++, new ParcelCodeRequest { Source = ParcelSource.Note, Pref = pref }.ToBytes(), WorldKeys.ParcelCode); }
        public ProposalCode Send(uint pid, byte[] v, string key) { uint rev; int sr; float sh; return _wh.HandleProposal("c2", 2, pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = key, Value = v }, 1, out rev, out sr, out sh); }
        public void Sync()
        {
            if (!Connected) return;
            for (; _sent < _wh.Outbox.Count; _sent++)
            {
                var w = new NetDataWriter(); StateMessages.WriteDelta(w, _wh.Reg.Epoch, _wh.Outbox[_sent]);
                var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep; StateEntry e;
                Expect(StateMessages.TryReadDelta(r, out ep, out e), "delta");
                Reg.ApplyDelta(ep, e);
            }
            Follow();
        }
        public StoryState Shared() { var e = Reg.Get(StateScope.Session, 0, WorldKeys.Story); StoryState s; return e != null && StoryState.TryParse(e.Value, out s) ? s : null; }
        // StorySync.Follow: BeginStory path (story = host index, InitiateStory, SaveStory) with ClientStrippedFlags cleared once.
        public void Follow()
        {
            var s = Shared();
            if (!StoryRules.ClientShouldFollow(M.Story, M.Current != null, s)) return;
            if (s.Index < 0 || s.Index >= M.S.Count || M.S[s.Index].Key != s.Key) return;
            var sc = M.S[s.Index]; var saved = new[] { sc.Home, sc.Jail, sc.Chase, sc.First, sc.Debt };
            Expect(StoryRules.ClientStrippedFlags.SequenceEqual(new[] { "advance_if_home", "advance_if_jail", "police_chase_on_start", "first_mission", "deduct_debt" }), "stripped flag list");
            sc.Home = sc.Jail = sc.Chase = sc.First = sc.Debt = false;
            try { M.Story = s.Index; M.Initiate(); M.SaveStory(); }
            finally { sc.Home = saved[0]; sc.Jail = saved[1]; sc.Chase = saved[2]; sc.First = saved[3]; sc.Debt = saved[4]; }
            Followed++;
        }
        public void SceneLoad() { M.BeginStory(); Follow(); }   // travel: new StoryManager reads the sandbox "Story"
    }

    static uint _pid = 700;
    static WorldHost NewHost(out HostGame hg, int index = S14)
    {
        var wh = new WorldHost(73); wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, Madison, 0);
        hg = new HostGame(index); wh.StoryHost = hg; wh.ParcelHost = hg; wh.PublishStory(0); wh.PublishParcels();
        return wh;
    }
    static StoryState Shared(WorldHost wh) { var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Story); StoryState s; return e != null && StoryState.TryParse(e.Value, out s) ? s : null; }
    static StoryAdvanceRequest Req(int from, string key, string path) { return new StoryAdvanceRequest { FromIndex = from, StageKey = key, Kind = StoryTriggerKind.ReadNote, Path = path }; }
    const string P103 = "Interior_103/story object 103 notka/pPlane3";

    // ---------------------------------------------------------------- 01
    static void Wire()
    {
        var r = Req(S14, "story14", P103); StoryAdvanceRequest b;
        Expect(StoryAdvanceRequest.TryParse(r.ToBytes(), out b) && b.FromIndex == S14 && b.StageKey == "story14" && b.Kind == StoryTriggerKind.ReadNote && b.Path == P103, "request round trip");
        Expect(r.ToBytes().Length == 6 + 7 + P103.Length && r.ToBytes().Length < 140, "request size " + r.ToBytes().Length + " B");
        var big = Req(65535, new string('k', 32), new string('p', 96));
        Expect(StoryAdvanceRequest.TryParse(big.ToBytes(), out b) && b.FromIndex == 65535, "max index / key / path round trip");
        Expect(!StoryAdvanceRequest.TryParse(null, out b) && !StoryAdvanceRequest.TryParse(new byte[] { 2, 18, 0, 1, 1, 65, 1, 66 }, out b) && !StoryAdvanceRequest.TryParse(new byte[] { 1, 18, 0, 9, 1, 65, 1, 66 }, out b)
            && !StoryAdvanceRequest.TryParse(new byte[] { 1, 18, 0, 1, 1, 65, 2, 66 }, out b) && !StoryAdvanceRequest.TryParse(new byte[] { 1, 18, 0, 1, 1, 32, 1, 66 }, out b)
            && !StoryAdvanceRequest.TryParse(new byte[] { 1, 18, 0, 1, 0, 1, 66 }, out b) && !StoryAdvanceRequest.TryParse(Req(1, "k", " p").ToBytes(), out b), "bad version / kind / length / space in key / empty key / leading space refused");
        Expect(StoryAdvanceRequest.TryParse(Req(1, "k", "a b").ToBytes(), out b) && b.Path == "a b", "inner space in a path accepted (scene names)");
        var s = new StoryState { Index = S15, Key = "story15", By = 2 }; StoryState back;
        Expect(StoryState.TryParse(s.ToBytes(), out back) && back.SameAs(s) && back.By == 2 && s.ToBytes().Length < 40, "state round trip, " + s.ToBytes().Length + " B");
        Expect(!StoryState.TryParse(new byte[] { 1, 0, 0, 3, 1, 65 }, out back) && !StoryState.TryParse(new byte[] { 2, 0, 0, 0, 1, 65 }, out back) && !StoryState.TryParse(new byte[] { 1, 0, 0, 0, 2, 65 }, out back), "bad by / version / truncated refused");
        var rnd = new Random(11); for (int i = 0; i < 4000; i++) { var x = new byte[rnd.Next(0, 60)]; rnd.NextBytes(x); StoryState.TryParse(x, out back); StoryAdvanceRequest.TryParse(x, out b); }
        Expect(StoryTriggers.All.Length == 1 && StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, Note103.Path) != null && StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, P103) != null, "note_103 listed (full path and canonical suffix)");
        Expect(StoryTriggers.Find("story15", StoryTriggerKind.ReadNote, Note103.Path) == null && StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, "X" + P103) == null
            && StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, Note108.Path) == null && StoryTriggers.Find("story14", StoryTriggerKind.ReadNote, null) == null, "other stage / partial name / other note refused");
        string why;
        Expect(StoryRules.Decide(true, false, S14, "story14", r, out why) == ProposalCode.Accepted, "decide: accepted from the live index + key");
        Expect(StoryRules.Decide(false, false, S14, "story14", r, out why) == ProposalCode.Busy && StoryRules.Decide(true, true, 80, "story73_freemode", r, out why) == ProposalCode.Unchanged
            && StoryRules.Decide(true, false, S15, "story15", r, out why) == ProposalCode.Stale && StoryRules.Decide(true, false, S14, "story13", r, out why) == ProposalCode.Stale
            && StoryRules.Decide(true, false, S14, "story14", Req(S14, "story14", "pPlane3"), out why) == ProposalCode.Invalid, "decide: busy / free / stale index / stale key / unlisted");
        Expect(WorldKeys.StoryAdvance == "storyAdvance" && WorldKeys.Story == "story", "keys");
        Expect(ProtocolInfo.ProtocolVersion == 37 && ProtocolInfo.ModVersion == "0.26.1", "protocol 37 / mod 0.26.0 (new proposal + state: both PCs need 0.26.0)");
    }

    // ---------------------------------------------------------------- 02
    static void Player2Note()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh, S14);
        Expect(p2.M.Story == S14 && p2.M.Key == "story14" && Shared(wh).Index == S14, "both on story14 at the start");
        p2.ReadNote(Note103);
        Expect(p2.Results.Last() == ProposalCode.Accepted, "P2 read: storyAdvance accepted");
        Expect(hg.M.Story == S15 && hg.M.Key == "story15" && hg.M.OriginalAdvances == 1, "host: original AdvanceStory once -> story15");
        Expect(hg.M.Calls == 1 && hg.M.Pref("story15_Call") == 1, "host: original stage start (story15 phone call) once");
        Expect(Shared(wh).Index == S15 && Shared(wh).Key == "story15" && Shared(wh).By == 2 && wh.StoryAdvances == 1, "shared state story15, last advance by P2");
        Expect(p2.M.Story == S14 && p2.M.OriginalAdvances == 0, "P2's own AdvanceStory never ran (suppressed)");
        p2.Sync();
        Expect(p2.M.Story == S15 && p2.M.Key == "story15" && p2.Followed == 1 && p2.M.Calls == 1 && p2.M.Pref("Story") == S15, "P2 follows: HUD story15, original call presentation once, sandbox Story saved");
        Expect(p2.M.OriginalAdvances == 0 && p2.Suppressed == 1, "P2 never advanced a private copy");
        Frame(wh); p2.Sync();
        Expect(p2.Followed == 1 && hg.M.Story == S15, "no further changes on the next frames");
    }

    // ---------------------------------------------------------------- 03
    static void Player1Note()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh, S14);
        hg.ReadNote(Note103); Frame(wh);
        Expect(hg.M.Story == S15 && hg.M.OriginalAdvances == 1 && Shared(wh).Index == S15 && Shared(wh).By == 0, "P1: original host step, published");
        p2.Sync();
        Expect(p2.M.Story == S15 && p2.M.Key == "story15", "P2 follows to story15");
        p2.ReadNote(Note103);
        Expect(p2.Proposed == 0 && p2.Suppressed == 0 && hg.M.Story == S15, "P2's later read: original condition (story14 current) false - nothing happens");
    }

    // ---------------------------------------------------------------- 04
    static void ExactlyOnce()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh, S14);
        p2.ReadNote(Note103);
        Expect(p2.Send(p2.LastPid, p2.LastBytes, WorldKeys.StoryAdvance) == ProposalCode.Accepted && hg.M.Story == S15 && hg.M.OriginalAdvances == 1, "retransmission (same id): cached answer, no second advance");
        Expect(p2.Send(_pid++, p2.LastBytes, WorldKeys.StoryAdvance) == ProposalCode.Stale && hg.M.Story == S15, "replay with a new id: Stale (host index moved)");
        p2.ReadNote(Note103);   // delta not yet received: P2 still shows story14
        Expect(p2.Results.Last() == ProposalCode.Stale && hg.M.Story == S15 && hg.M.OriginalAdvances == 1, "second read before the follow arrives: Stale");
        p2.Sync(); p2.ReadNote(Note103);
        Expect(p2.Results.Count == 2 && hg.M.OriginalAdvances == 1 && hg.M.Calls == 1 && p2.M.Calls == 1, "after the follow: nothing proposed; one call each");
        Expect(wh.StoryAdvances == 1 && wh.StoryRefusals == 0, "host: one applied, stale is not a refusal");
    }

    // ---------------------------------------------------------------- 05
    static void Concurrent()
    {
        // order A: P1's read lands first on the host, P2's proposal arrives after
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh, S14);
        hg.ReadNote(Note103); p2.ReadNote(Note103); Frame(wh); p2.Sync();
        Expect(p2.Results.Last() == ProposalCode.Stale && hg.M.OriginalAdvances == 1 && hg.M.Story == S15 && p2.M.Story == S15 && hg.M.Calls == 1 && p2.M.Calls == 1, "P1 first: P2 Stale, one advance, one call each");
        // order B: P2's proposal first, P1 reads in the same frame before seeing anything
        var wh2 = NewHost(out hg); p2 = new Client(wh2, S14);
        p2.ReadNote(Note103); hg.ReadNote(Note103); Frame(wh2); p2.Sync();
        Expect(p2.Results.Last() == ProposalCode.Accepted && hg.M.OriginalAdvances == 1 && hg.M.Story == S15 && p2.M.Story == S15, "P2 first: host's own read finds story15 (original condition false)");
        Expect(Shared(wh2).Index == S15 && hg.M.Calls == 1, "one stage start");
    }

    // ---------------------------------------------------------------- 06
    static void Prerequisites()
    {
        HostGame hg; var wh = NewHost(out hg, S14 - 1); var p2 = new Client(wh, S14 - 1);
        Expect(p2.M.Key == "story13", "both on story13");
        p2.ReadNote(Note103);
        Expect(p2.Proposed == 0 && hg.M.Story == S14 - 1, "story13: original condition false, note never advances (StoryObject would not even show it)");
        Expect(p2.Propose(Req(S14 - 1, "story14", P103)) == ProposalCode.Stale && hg.M.Story == S14 - 1, "forged key for another stage: Stale");
        hg.ReadNote(Note103); Expect(hg.M.Story == S14 - 1, "host: same original condition");
        // unlisted trigger with a matching story key
        hg.M.Story = S14; hg.M.Initiate(); Frame(wh); p2.Sync();
        Expect(p2.M.Key == "story14", "both on story14");
        Expect(p2.Propose(Req(S14, "story14", "ABANDONEDHOUSE/pPlane3")) == ProposalCode.Invalid && hg.M.Story == S14 && wh.StoryRefusals == 1, "unlisted note path: Invalid");
        // free mode: AdvanceStory does nothing (original)
        hg.M.Free = true; Expect(p2.Propose(Req(S14, "story14", P103)) == ProposalCode.Unchanged && hg.M.Story == S14, "free mode: Unchanged"); hg.M.Free = false;
        // host loading: busy, the client retries
        hg.Loaded = false; Expect(p2.Propose(Req(S14, "story14", P103)) == ProposalCode.Busy && hg.M.Story == S14, "host scene loading: Busy");
        hg.Loaded = true; Expect(p2.Propose(Req(S14, "story14", P103)) == ProposalCode.Accepted && hg.M.Story == S15, "retry after the load: Accepted");
        // host-only stage start effects: chase (12), first_mission teleport (3), debt (40), home skip (9 / 28), jail skip (26)
        foreach (int target in new[] { 3, 9, 12, 26, 28, 40 })
        {
            var w = NewHost(out hg, target - 1); var c = new Client(w, target - 1); c.M.Location = target == 26 ? Jail : Hideout; hg.M.Location = Madison;
            long hostDebt = hg.M.Debt, cDebt = c.M.Debt; int hc = hg.M.Chases, ht = hg.M.Teleports;
            hg.M.AdvanceStory(); Frame(w); c.Sync();
            Expect(c.M.Story == target && hg.M.Story == target, "stage " + target + ": client follows exactly the host index (no location skip ahead)");
            Expect(c.M.Chases == 0 && c.M.Teleports == 0 && c.M.Debt == cDebt, "stage " + target + ": no chase / teleport / debt on Player 2");
            Expect(hg.M.Chases - hc == (target == 12 ? 1 : 0) && hg.M.Teleports - ht == (target == 3 ? 1 : 0) && hostDebt - hg.M.Debt == (target == 40 ? 30000 : 0), "stage " + target + ": host keeps the original start effects once");
            var sc = c.M.S[target]; Expect(sc.Home == (target == 9 || target == 28) && sc.Jail == (target == 26) && sc.Chase == (target == 12) && sc.First == (target == 3) && sc.Debt == (target == 40), "stage " + target + ": stripped flags restored");
        }
        // host at home: the original skip decides on the host (stage 9 at the hideout -> 10), Player 2 follows to 10
        var w9 = NewHost(out hg, 8); var c9 = new Client(w9, 8); hg.M.Location = Hideout;
        hg.M.AdvanceStory(); Frame(w9); c9.Sync();
        Expect(hg.M.Story == 10 && c9.M.Story == 10 && c9.M.Key == "story6", "host home skip: both on stage 10");
    }

    // ---------------------------------------------------------------- 07
    static void PersonalVsShared()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh, S14);
        long money = hg.Money; var noteXp = new Note { Path = Note103.Path, StoryDesc = "story14", Pref = "parcel1_madison", AddCode = true, Advance = true, Xp = 25 };
        p2.ReadNote(noteXp); p2.Sync();
        Expect(p2.Xp == 25 && hg.Xp == 0, "note XP (addPoint) stays the reader's");
        Expect(hg.Codes.Contains("parcel1_madison") && hg.CodeAnim == 1 && p2.Codes.Contains("parcel1_madison"), "parcel code: W2B shared once");
        Expect(hg.M.Story == S15 && p2.M.Story == S15 && hg.Money == money, "story step shared; no money");
        hg.ReadNote(noteXp);
        Expect(hg.Xp == 25 && hg.CodeAnim == 1 && hg.M.OriginalAdvances == 1, "host's later read: its own XP, no second code / step");
        p2.ReadNote(SkillBook); Expect(p2.Xp == 75 && p2.Proposed == 1, "skill book: personal XP, no story proposal");
    }

    // ---------------------------------------------------------------- 08
    static void SaveReloadReconnect()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh, S14);
        p2.ReadNote(Note103); p2.Sync();
        hg.M.SaveGame(); Expect(hg.M.Pref("Story") == S15, "host checkpoint: original SaveGameNewSystem writes the higher index");
        hg.M.Story = 5; hg.M.SaveGame(); Expect(hg.M.Pref("Story") == S15, "only if higher (original)"); hg.M.Story = S15;
        // host reload (travel): BeginStory reads the saved index, nothing to publish
        hg.M.BeginStory(); Frame(wh); p2.Sync();
        Expect(hg.M.Story == S15 && Shared(wh).Index == S15 && p2.M.Story == S15, "host reload: same index on both");
        // client scene load: sandbox "Story" was saved by the follow -> no second follow
        int f = p2.Followed; p2.SceneLoad();
        Expect(p2.M.Story == S15 && p2.Followed == f, "client travel: BeginStory already on story15 (SaveStory), no re-follow");
        // client sandbox stale (older value) -> follow after the load
        p2.M.Prefs["Story"] = S14; p2.SceneLoad();
        Expect(p2.M.Story == S15 && p2.Followed == f + 1, "stale sandbox index corrected after the load");
        // reconnect / late join: snapshot carries the story state
        var late = new Client(wh, 3);
        Expect(late.M.Story == S15 && late.M.Key == "story15", "late join / reconnect from an old sandbox: follows the host index");
        // host quits without saving after another advance: original loss, Player 2 follows down after the host reload
        var w2 = NewHost(out hg); var c2 = new Client(w2, S14); hg.M.SaveGame();
        c2.ReadNote(Note103); c2.Sync(); Expect(hg.M.Story == S15 && c2.M.Story == S15, "advanced, not saved");
        hg.M.BeginStory(); Frame(w2); c2.Sync();
        Expect(hg.M.Story == S14 && c2.M.Story == S14 && c2.M.Key == "story14", "host reload without save: original loss on both (stage repeats)");
        c2.ReadNote(Note103); c2.Sync();
        Expect(hg.M.Story == S15 && c2.M.Story == S15 && hg.M.Calls == 1, "step repeatable; story15 call once (original _Call key)");
        // disconnect while reading: nothing proposed, the note still works after the reconnect
        var w3 = NewHost(out hg); var c3 = new Client(w3, S14);
        c3.Connected = false; c3.ReadNote(Note103);
        Expect(hg.M.Story == S14 && c3.M.Story == S14 && c3.Proposed == 0, "disconnected read: no progress, no private step");
        c3.Reconnect(); c3.ReadNote(Note103); c3.Sync();
        Expect(hg.M.Story == S15 && c3.M.Story == S15, "after reconnect: the same original note advances once");
    }

    // ---------------------------------------------------------------- 09
    // ReadNote.Start IL_0062..IL_007f hides a code note with SPrefs(parcel_pref) >= 2; StorySync.NoteStartPost keeps it while
    // its advanceStory step is current.
    static bool Visible(SM m, Note n, bool w6a)
    {
        bool hidden = n.AddCode && m.Pref(n.Pref) >= 2;
        if (hidden && w6a && n.Advance && !m.Free && m.Key == n.StoryDesc) hidden = false;
        return !hidden;
    }
    static void NoteVisibility()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh, S14);
        hg.M.Prefs["parcel1_madison"] = 2; p2.M.Prefs["parcel1_madison"] = 2;   // parcel taken through the shared loot before the step
        Expect(!Visible(hg.M, Note103, false) && !Visible(p2.M, Note103, false), "without W6A: original Start hides the note -> story14 blocked by MP-only order");
        Expect(Visible(hg.M, Note103, true) && Visible(p2.M, Note103, true), "W6A: note stays readable on both while story14 is current");
        p2.ReadNote(Note103); p2.Sync();
        Expect(hg.M.Story == S15 && p2.M.Story == S15 && !hg.Codes.Contains("parcel1_madison") && hg.CodeAnim == 0, "P2 advances; taken parcel's code not re-discovered (SPrefs 2, original)");
        Expect(!Visible(hg.M, Note103, true) && !Visible(p2.M, Note103, true), "after the step: original hide applies again");
        Expect(Visible(hg.M, new Note { Path = "x", Pref = "parcel4", AddCode = true }, true), "untaken code notes unaffected");
    }

    // ---------------------------------------------------------------- 10
    static void Unlisted()
    {
        HostGame hg; var wh = NewHost(out hg, 14); var p2 = new Client(wh, 14);
        p2.OtherAdvanceCall(); p2.OtherAdvanceCall();   // e.g. StoryManager.Update advance_if_has_item poll on Player 2's copy
        Expect(p2.M.Story == 14 && p2.Proposed == 0 && p2.Suppressed == 2 && hg.M.Story == 14, "poll on Player 2: no private step, nothing proposed, host unchanged");
        hg.M.AdvanceStory(); Frame(wh); p2.Sync();   // the host's own poll (Player 1 bought the lockpick)
        Expect(hg.M.Story == 15 && p2.M.Story == 15 && p2.M.OriginalAdvances == 0, "host's own original trigger: shared, P2 follows");
        hg.M.Story = S14; hg.M.Initiate(); Frame(wh); p2.Sync();
        p2.ReadNote(Note108);
        Expect(p2.Proposed == 0 && hg.M.Story == S14 && p2.M.Story == S14, "unlisted advancing note: nothing (no Invalid traffic)");
    }

    // ---------------------------------------------------------------- 11
    static void Glue()
    {
        var code = Strip(Src("Plugin", "StorySync.cs"));
        Expect(code.Contains("AccessTools.Method(sm, \"AdvanceStory\", Type.EmptyTypes)") && code.Contains("AccessTools.Method(note, \"BeginRead\", Type.EmptyTypes)") && code.Contains("AccessTools.Method(note, \"Start\", Type.EmptyTypes)"), "hooks on the original AdvanceStory / ReadNote.BeginRead / ReadNote.Start");
        Expect(code.Contains("var i = Instance; if (i == null || i._host || _applying) return true;") && code.Contains("return false;\n        }\n        static void NoteReadPre"), "client AdvanceStory prefix: never runs the original; host / follow pass through");
        Expect(code.Contains("_ctxPath = _noteAdvance(__instance) ? PathOf(((Component)__instance).transform) : null;") && code.Contains("static void NoteReadPost() { _ctxPath = null; }"), "trigger context only around the note's own call");
        int f = code.IndexOf("void Follow(object sm, int local)", StringComparison.Ordinal); string fb = code.Substring(f, code.IndexOf("bool Local(out int index", f, StringComparison.Ordinal) - f);
        foreach (var x in new[] { "_applying = true;", "_flagSet[k](script, false);", "_setStory(sm, target); _initiate(sm); _saveStory(sm);", "finally", "if (saved[k]) _flagSet[k](script, true);", "_applying = false;", "if (_smCurrent(sm) == null) return;", "_scriptKey(script), _committed.Key" })
            Expect(fb.Contains(x), "follow = BeginStory path with stripped flags restored: " + x);
        Expect(fb.IndexOf("_advance(", StringComparison.Ordinal) < 0, "follow never calls AdvanceStory");
        Expect(code.Contains("_nextApply = now + 1.0;") && code.Contains("if (local == _lastLocal || _world.Host == null) return;"), "follow rate-limited, host publish only on change");
        Expect(code.Contains("if (sm == null || _smStory(sm) != expectedIndex) return false;") && code.Contains("_advance(sm);"), "host apply = original AdvanceStory from the expected index");
        Expect(code.Contains("if (index < 0 || index >= scripts.Count || !ReferenceEquals(scripts[index], cur)) return false;"), "host publishes only a consistent index");
        Expect(code.Contains("if (go.activeSelf || !_noteAdvance(__instance) || !_noteAddCode(__instance) || _prefInt(_notePref(__instance)) < 2) return;") && code.Contains("string.Equals(key, _noteDesc(__instance), StringComparison.Ordinal)"), "note kept only when hidden by the parcel rule and its step is current");
        foreach (var bad in new[] { "FindObjectsOfType", "FindObjectOfType", "GameObject.Find", "GetComponentsInChildren", "OnGUI", "GUI.", "SpawnWarning", "SetStory\"", "moneyAm", "debtAm", "SPrefs.SetInt", "AddXP", "StartPoliceChase" })
            Expect(code.IndexOf(bad, StringComparison.Ordinal) < 0, "StorySync has no " + bad);
        var ps = Strip(Src("Plugin", "ParcelSync.cs"));
        Expect(ps.Contains("if (!i._host && !StorySync.ClientHandlesStory && _noteAdvance(__instance)) { _setNoteAdvance(__instance, false);"), "ParcelSync: W2B story suppression only as fallback");
        var ws = Strip(Src("Plugin", "WorldSync.cs")); var wt = Strip(Src("Shared", "WorldTime.cs")); var pl = Strip(Src("Plugin", "Plugin.cs"));
        Expect(ws.Contains("ProposalResultHook12(id, code)") && wt.Contains("p.Key == WorldKeys.StoryAdvance") && pl.Contains("_story = new StorySync(node, log, _world)") && pl.Contains("Release(ref _story, \"STORY\")") && pl.Contains("_story.Update();"), "wiring: dispatch, result hook, lifecycle, update");
        var mirror = Strip(Src("Shared", "WorldMirror.cs"));
        Expect(mirror.Contains("new KeyFamily(0, \"Story\", true, KeyOwner.HostWorld, false"), "SPrefs Story stays unmirrored (index travels as the 'story' state, not as a key)");
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
