// W2B offline tests: shared parcel-locker codes, story clues and parcel-locker doors. Drives the SAME Unity-free pieces as the
// plugin: ParcelCodeRequest / ParcelState (wire), ParcelRules, WorldHost "parcelCode" dispatch + PublishParcels through the
// real proposal path and registry snapshot / delta wire format, RegionStateBook for the ParcelLocker region kind, LootBook /
// LootRules for the parcel loot. Each PC is a simulated ORIGINAL campaign that follows the verified IL (ReadNote.BeginRead,
// ItemID.HackedElectronic, SaveGameNewSystem, GameManager.LoadParcels, ParcelMinigame.PressNumberButton, parcel pickup);
// the host side implements IParcelHost exactly like src/Plugin/ParcelSync.cs, the client side runs its hook + apply rules.
// The 14 codes and the code notes are the real 1.27c data (docs/research/PARCEL_SYSTEM.md). In-game behaviour (phone UI,
// minigame, locker animation) is manual two-PC QA (docs/TESTING.md §48); the Unity glue is source-scanned (test 12).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W2BTests
{
    static int _passed, _failed;
    const int Madison = 99, Hillville = 97, Hideout = 0;
    const uint Digest = 0x0B2B2B2B;

    static int Main()
    {
        Run("01 wire + rules: parcelCode request / parcels state codecs, fuzz, size bound, original conditions, keys, protocol 37 / mod 0.26.0", Wire);
        Run("02 first story code note (note_103, parcel1_madison): Player 2 reads it, the host campaign learns the code, Player 1 opens Madison locker1", FirstStoryNote);
        Run("03 independent code (note_201, parcel201 Hillville): Player 1 reads it, Player 2 opens the Hillville locker with 69999", IndependentCode);
        Run("04 across maps, saves, travel, reconnect, reload: shared codes stay available; reload without save = original loss on both", MapsAndReloads);
        Run("05 duplicates: second read, already shared code, retransmitted / replayed proposal - one host mutation, one phone animation", Duplicates);
        Run("06 simultaneous discovery of the same code by both players: one host list entry, Unchanged for the late one", Simultaneous);
        Run("07 hacked electronic (hacked_parcel_pref): host queues like the original, usable on both after the host's save + load", Hack);
        Run("08 invalid / stale requests: unknown pref, malformed bytes, wrong arg, host not ready - nothing changes", Invalid);
        Run("09 story clue (parcel path): Player 2 never advances a private story copy; no loot, no money from a note; host keeps the original step (shared step: W6ATests)", Story);
        Run("10 parcel locker doors: region kind ParcelLocker, open by either player once, concurrent open, late join silent, region reload resets", Lockers);
        Run("11 parcel loot: one take per parcel (P1 claim vs P2 take race), taker's inventory only, host parcel list mutation once, sale money once", ParcelLoot);
        Run("12 Unity glue: hooks, story suppression restore, host apply mirrors the IL, set-true-only apply, no per-frame scans, regressions", Glue);
        Console.WriteLine(_failed == 0 ? "W2B TESTS PASSED (" + _passed + ")" : "W2B TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- real 1.27c data (MainScene MANAGERS/PARCEL STORAGE)
    static readonly string[][] RealCodes =
    {
        new[] { "parcel1_madison", "16161", "Madison St.", "locker1" }, new[] { "parcel_myst2", "99517", "Madison St.", "locker_myst" },
        new[] { "parcel2_madison", "25252", "Madison St.", "locker2" }, new[] { "parcel3_madison", "24252", "Madison St.", "locker3" },
        new[] { "parcel4", "59444", "Madison St.", "locker4" }, new[] { "parcel_jail", "42318", "Madison St.", "locker8" },
        new[] { "parcel106", "77611", "Madison St.", "locker5" }, new[] { "parcel115", "69695", "Madison St.", "locker6" },
        new[] { "parcel114", "45144", "Madison St.", "locker7" }, new[] { "parcel201", "69999", "Hillville", "locker1" },
        new[] { "parcel205", "54145", "Hillville", "locker2" }, new[] { "parcel202", "49637", "Hillville", "locker3" },
        new[] { "parcel_mysterious", "53453", "Hillville", "lockerMysterious" }, new[] { "parcel_hillville", "68611", "Hillville", "locker4" },
    };
    // code notes (ReadNote.addParcelCode = true), name -> (pref, advanceStory storyDesc or null)
    sealed class Note { public string Name, Pref, StoryDesc; public bool AddCode; }
    static readonly Note Note103 = new Note { Name = "note_103", Pref = "parcel1_madison", AddCode = true, StoryDesc = "story14" };
    static readonly Note Note201 = new Note { Name = "note_201", Pref = "parcel201", AddCode = true };
    static readonly Note Note106 = new Note { Name = "note_106", Pref = "parcel106", AddCode = true };
    static readonly Note Note108 = new Note { Name = "note_108", Pref = "parcel1_madison", AddCode = false };   // mentions the code, adds none
    static readonly Note JailNote = new Note { Name = "jail_note", Pref = "parcel_jail", AddCode = true };

    // ---------------------------------------------------------------- simulated original campaign of one PC
    sealed class Code { public string Pref, Digits, Location, Locker; public bool Discovered, Opened; }
    sealed class Game
    {
        public readonly Dictionary<string, int> Prefs = new Dictionary<string, int>();   // SPrefs (host: save; P2: sandbox)
        public List<Code> Storage = new List<Code>();
        public List<string> ParcelList = new List<string>(), MissionList = new List<string>();
        public string Story = "story14"; public int StoryAdvanced, Anim, Xp, Inventory; public int Money = 0;   // a note / code never changes it
        public bool Loaded = true;
        public Game() { NewStorage(); }
        void NewStorage() { Storage = RealCodes.Select(c => new Code { Pref = c[0], Digits = c[1], Location = c[2], Locker = c[3] }).ToList(); }
        public int Pref(string k) { int v; return Prefs.TryGetValue(k, out v) ? v : 0; }
        public Code Find(string pref) { return Storage.FirstOrDefault(c => c.Pref == pref); }

        // ReadNote.BeginRead (advance = the field value at call time)
        public void ReadNote(Note n, bool advance)
        {
            if (n.AddCode)
                foreach (var c in Storage)
                    if (c.Pref == n.Pref && !c.Discovered && Pref(c.Pref) == 0) { c.Discovered = true; Anim++; ParcelList.Add(n.Pref); break; }
            if (advance && n.StoryDesc != null && Story == n.StoryDesc) StoryAdvanced++;
        }
        // ItemID.HackedElectronic
        public void Hack(string pref) { Inventory++; if (pref != "" && Pref(pref) < 1) ParcelList.Add(pref); }
        // SaveGameNewSystem parcel / mission lists
        public void Save() { foreach (var p in ParcelList) Prefs[p] = 1; foreach (var p in MissionList) Prefs[p] = 2; }
        // scene (re)load: fresh MainScene ParcelStorage + GameManager lists, GameManager.LoadParcels (SPrefs == 1)
        public void Load() { NewStorage(); ParcelList = new List<string>(); MissionList = new List<string>(); foreach (var c in Storage) if (Pref(c.Pref) == 1) c.Discovered = true; }
        // ParcelMinigame.PressNumberButton with 5 digits (location = the station the player stands at)
        public bool EnterCode(string digits, string location)
        {
            foreach (var c in Storage)
                if (c.Digits == digits && c.Location == location && !c.Opened && c.Discovered) { c.Opened = true; return true; }
            return false;
        }
        // parcel loot pickup (Pickupable lock_parcel_after_pickup)
        public void PickParcel(string pref) { MissionList.Add(pref); ParcelList.Remove(pref); Inventory++; }
    }

    // Host plugin side: exactly src/Plugin/ParcelSync.cs (TryDiscovered / Known / Apply).
    sealed class HostGame : IParcelHost
    {
        public readonly Game G = new Game();
        public bool TryDiscovered(out List<string> prefs) { prefs = null; if (!G.Loaded) return false; prefs = G.Storage.Where(c => c.Discovered).Select(c => c.Pref).ToList(); return true; }
        public bool Known(string pref) { return G.Find(pref) != null; }
        public ParcelApply Apply(byte slot, ParcelSource src, string pref)
        {
            int saved = G.Pref(pref);
            if (src == ParcelSource.Hack) { if (!ParcelRules.HackQueues(saved) || G.ParcelList.Contains(pref)) return ParcelApply.AlreadyKnown; G.ParcelList.Add(pref); return ParcelApply.Applied; }
            foreach (var c in G.Storage)
            {
                if (c.Pref != pref || !ParcelRules.NoteDiscovers(c.Discovered, saved)) continue;
                c.Discovered = true; G.Anim++; G.ParcelList.Add(pref); return ParcelApply.Applied;
            }
            return ParcelApply.AlreadyKnown;
        }
    }

    // Client PC: hooks (ParcelSync.NotePre/NotePost/HackPre/HackPost) + ReadCommitted.
    sealed class Client
    {
        public readonly Game G = new Game(); readonly WorldHost _wh; public StateRegistry Reg; int _sent; bool _live;
        public readonly List<uint> Proposals = new List<uint>(); public readonly List<ProposalCode> Results = new List<ProposalCode>();
        public int StorySuppressed, Applied;
        public Client(WorldHost wh) { _wh = wh; Reconnect(); }
        public void Reconnect()
        {
            var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, _wh.Reg);
            var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep, hd; List<StateEntry> entries;
            Expect(StateMessages.TryReadSnapshot(r, out ep, out hd, out entries), "snapshot");
            Reg = StateRegistry.CreateClient(); Reg.LoadSnapshot(ep, hd, entries); _sent = _wh.Outbox.Count; _live = false;
            Apply();
        }
        public ProposalCode ReadNote(Note n)
        {
            bool before = n.AddCode && G.Storage.Any(c => c.Pref == n.Pref && c.Discovered);
            bool advance = n.StoryDesc != null; if (advance) StorySuppressed++;   // prefix clears advanceStory on Player 2
            G.ReadNote(n, false); G.Xp++;
            bool after = n.AddCode && G.Storage.Any(c => c.Pref == n.Pref && c.Discovered);
            if (n.AddCode && !before && after) return Propose(new ParcelCodeRequest { Source = ParcelSource.Note, Pref = n.Pref });
            return ProposalCode.Invalid;   // nothing proposed
        }
        public ProposalCode Hack(string pref)
        {
            bool before = G.ParcelList.Contains(pref); G.Hack(pref);
            if (!before && G.ParcelList.Contains(pref)) return Propose(new ParcelCodeRequest { Source = ParcelSource.Hack, Pref = pref });
            return ProposalCode.Invalid;
        }
        public uint LastPid; public byte[] LastBytes;
        public ProposalCode Propose(ParcelCodeRequest r)
        {
            LastPid = _pid++; LastBytes = r.ToBytes();
            var code = Send(LastPid, LastBytes);
            Proposals.Add(LastPid); Results.Add(code); Sync(); return code;
        }
        public ProposalCode Send(uint pid, byte[] v) { uint rev; int sr; float sh; return _wh.HandleProposal("c2", 2, pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ParcelCode, Value = v }, 1, out rev, out sr, out sh); }
        public void Sync()
        {
            for (; _sent < _wh.Outbox.Count; _sent++)
            {
                var w = new NetDataWriter(); StateMessages.WriteDelta(w, _wh.Reg.Epoch, _wh.Outbox[_sent]);
                var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep; StateEntry e;
                Expect(StateMessages.TryReadDelta(r, out ep, out e), "delta");
                Reg.ApplyDelta(ep, e);
            }
            Apply();
        }
        public void Load() { G.Load(); _live = false; Apply(); }   // travel / reload: new ParcelStorage, silent re-apply
        void Apply()
        {
            var e = Reg.Get(StateScope.Session, 0, WorldKeys.Parcels); ParcelState s;
            if (e != null && ParcelState.TryParse(e.Value, out s))
            {
                int n = 0;
                foreach (var c in G.Storage) if (!c.Discovered && s.Has(c.Pref)) { c.Discovered = true; n++; }
                Applied += n;
                if (n > 0 && _live) G.Anim++;
            }
            _live = true;
        }
    }

    static uint _pid = 500;
    static WorldHost NewHost(out HostGame hg, int p1 = Madison, int p2 = Madison)
    {
        var wh = new WorldHost(61); wh.SetPlayerRegion(1, p1, 0); wh.SetPlayerRegion(2, p2, 0);
        hg = new HostGame(); wh.ParcelHost = hg; wh.PublishParcels();
        return wh;
    }
    // host's own original discovery -> ParcelSync scan publishes
    static void HostRead(WorldHost wh, HostGame hg, Note n) { hg.G.ReadNote(n, true); wh.PublishParcels(); }
    static ParcelState Shared(WorldHost wh) { var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Parcels); ParcelState s; return e != null && ParcelState.TryParse(e.Value, out s) ? s : null; }

    // ---------------------------------------------------------------- 01
    static void Wire()
    {
        var r = new ParcelCodeRequest { Source = ParcelSource.Note, Pref = "parcel1_madison" }; ParcelCodeRequest b;
        Expect(ParcelCodeRequest.TryParse(r.ToBytes(), out b) && b.Source == ParcelSource.Note && b.Pref == "parcel1_madison" && r.ToBytes().Length == 17, "request round trip (2 + pref bytes)");
        Expect(!ParcelCodeRequest.TryParse(null, out b) && !ParcelCodeRequest.TryParse(new byte[] { 3, 1, 65 }, out b) && !ParcelCodeRequest.TryParse(new byte[] { 1, 2, 65 }, out b)
            && !ParcelCodeRequest.TryParse(new byte[] { 1, 1, 32 }, out b) && !ParcelCodeRequest.TryParse(new byte[] { 1, 0 }, out b), "bad source / length / char / empty refused");
        var s = ParcelState.Of(new[] { "parcel4", "parcel106", "parcel4", "parcel1_madison" }); ParcelState back;
        Expect(s.Discovered.Count == 3 && s.Discovered[0] == "parcel106" && ParcelState.TryParse(s.ToBytes(), out back) && back.SameAs(s), "state sorted, unique, round trip");
        var unsorted = new byte[] { 1, 2, 1, (byte)'b', 1, (byte)'a' };
        Expect(!ParcelState.TryParse(unsorted, out back) && !ParcelState.TryParse(new byte[] { 2, 0 }, out back) && !ParcelState.TryParse(new byte[] { 1, 1, 5, 65 }, out back), "unsorted / version / truncated refused");
        var rnd = new Random(7); for (int i = 0; i < 3000; i++) { var x = new byte[rnd.Next(0, 40)]; rnd.NextBytes(x); ParcelState.TryParse(x, out back); ParcelCodeRequest.TryParse(x, out b); }
        var many = ParcelState.Of(Enumerable.Range(0, 40).Select(i => new string('p', 40) + i.ToString("D2")));
        Expect(many.Discovered.Count == ParcelState.MaxCodes && many.ToBytes().Length <= StateEntry.MaxValueBytes, "capped at 20 codes, <= 1024 B");
        var real = ParcelState.Of(RealCodes.Select(c => c[0]));
        Expect(real.Discovered.Count == 14 && real.ToBytes().Length < 250, "all 14 real codes: " + real.ToBytes().Length + " B");
        Expect(ParcelRules.NoteDiscovers(false, 0) && !ParcelRules.NoteDiscovers(true, 0) && !ParcelRules.NoteDiscovers(false, 1) && !ParcelRules.NoteDiscovers(false, 2), "ReadNote condition IL_0081..IL_00a3");
        Expect(ParcelRules.HackQueues(0) && !ParcelRules.HackQueues(1) && !ParcelRules.HackQueues(2), "HackedElectronic condition IL_0039..IL_003f");
        Expect(WorldKeys.ParcelCode == "parcelCode" && WorldKeys.Parcels == "parcels", "keys");
        Expect(ProtocolInfo.ProtocolVersion == 41 && ProtocolInfo.ModVersion == "0.31.0", "protocol 37 / mod 0.26.0 (W6A on top of W2B: both PCs need 0.26.0)");
    }

    // ---------------------------------------------------------------- 02
    static void FirstStoryNote()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh);
        Expect(Shared(wh) != null && Shared(wh).Discovered.Count == 0, "no codes at the start");
        Expect(!hg.G.EnterCode("16161", "Madison St."), "host: unknown code -> original wrong_code");
        Expect(p2.ReadNote(Note103) == ProposalCode.Accepted, "P2 reads note_103: proposal accepted");
        Expect(p2.G.Find("parcel1_madison").Discovered && p2.G.Anim == 1, "P2: original discovery + phone animation once");
        var hc = hg.G.Find("parcel1_madison");
        Expect(hc.Discovered && hg.G.ParcelList.Count(x => x == "parcel1_madison") == 1 && hg.G.Anim == 1, "host campaign: discoveredOnMap + parcel_strings_to_set once + original animation");
        Expect(Shared(wh).Has("parcel1_madison") && Shared(wh).Discovered.Count == 1, "shared state lists the code");
        Expect(hg.G.EnterCode("16161", "Madison St.") && hc.Opened, "P1 opens Madison locker1 with the code P2 found");
        Expect(p2.G.Anim == 1, "P2 no second animation (own discovery)");
        Expect(hg.G.StoryAdvanced == 0 && p2.G.StoryAdvanced == 0 && p2.StorySuppressed == 1, "Player 2's private copy never advances (this harness models the parcel path only; the shared step is W6A, tests/W6ATests.cs)");
        hg.G.Save();
        Expect(hg.G.Pref("parcel1_madison") == 1, "durable with the host's next checkpoint (original SPrefs 1)");
    }

    // ---------------------------------------------------------------- 03
    static void IndependentCode()
    {
        HostGame hg; var wh = NewHost(out hg, Madison, Hillville); var p2 = new Client(wh);
        HostRead(wh, hg, Note201);
        Expect(hg.G.Find("parcel201").Discovered && hg.G.Anim == 1, "P1 original discovery (in Madison? no: the note is in Hillville; region does not matter for the campaign state)");
        p2.Sync();
        Expect(p2.G.Find("parcel201").Discovered && p2.G.Anim == 1 && p2.Applied == 1, "P2 learns parcel201 with the original phone animation");
        Expect(p2.G.EnterCode("69999", "Hillville"), "P2 opens Hillville locker1 with 69999");
        Expect(!p2.G.EnterCode("16161", "Madison St."), "other codes stay unknown");
        Expect(Shared(wh).Discovered.Count == 1 && p2.Proposals.Count == 0, "nothing proposed by P2 (code came from the host)");
    }

    // ---------------------------------------------------------------- 04
    static void MapsAndReloads()
    {
        HostGame hg; var wh = NewHost(out hg, Madison, Hillville); var p2 = new Client(wh);
        p2.ReadNote(JailNote); HostRead(wh, hg, Note106); p2.Sync();
        Expect(p2.G.Find("parcel_jail").Discovered && p2.G.Find("parcel106").Discovered && hg.G.Find("parcel_jail").Discovered, "two codes from two regions known on both");
        // P2 travels (new MainScene): its sandbox SPrefs do not have them yet -> silent re-apply from the shared state
        int anim = p2.G.Anim; p2.Load();
        Expect(p2.G.Find("parcel_jail").Discovered && p2.G.Find("parcel106").Discovered && p2.G.Anim == anim, "after P2 travel: still known, no animation");
        // P2 reconnects (fresh registry snapshot)
        p2.Reconnect(); p2.Load();
        Expect(p2.G.Find("parcel106").Discovered, "after reconnect: known");
        // host saves, then reloads (travel / load save): original LoadParcels restores, shared state unchanged
        hg.G.Save(); hg.G.Load(); wh.PublishParcels();
        Expect(hg.G.Find("parcel_jail").Discovered && hg.G.Find("parcel106").Discovered && Shared(wh).Discovered.Count == 2, "host save + reload: kept");
        // host reads one more and reloads WITHOUT saving: lost on the host (original); P2 keeps it until its next load, then consistent
        HostRead(wh, hg, Note201); p2.Sync();
        Expect(p2.G.Find("parcel201").Discovered, "P2 got parcel201");
        hg.G.Load(); wh.PublishParcels();
        Expect(!hg.G.Find("parcel201").Discovered && !Shared(wh).Has("parcel201"), "host reload without save: original loss");
        p2.Sync(); Expect(p2.G.Find("parcel201").Discovered, "set-true-only apply: P2 keeps it in this scene");
        p2.Load(); Expect(!p2.G.Find("parcel201").Discovered && p2.G.Find("parcel106").Discovered, "next P2 load: consistent with the host campaign");
        // host not loaded (scene transition): no publish, state kept
        hg.G.Loaded = false; Expect(!wh.PublishParcels() && Shared(wh).Discovered.Count == 2, "no publish while the host scene loads");
    }

    // ---------------------------------------------------------------- 05
    static void Duplicates()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh);
        Expect(p2.ReadNote(Note106) == ProposalCode.Accepted, "first read");
        uint pid = p2.LastPid; var bytes = p2.LastBytes;
        Expect(p2.Send(pid, bytes) == ProposalCode.Accepted && hg.G.ParcelList.Count(x => x == "parcel106") == 1 && hg.G.Anim == 1, "retransmission (same id): cached answer, no second mutation");
        Expect(p2.Send(_pid++, bytes) == ProposalCode.Unchanged && hg.G.ParcelList.Count(x => x == "parcel106") == 1, "replayed request (new id): Unchanged");
        Expect(p2.ReadNote(Note106) == ProposalCode.Invalid && p2.Proposals.Count == 1, "second read: original does nothing, nothing proposed");
        Expect(p2.ReadNote(Note108) == ProposalCode.Invalid && p2.Proposals.Count == 1, "note without addParcelCode: no discovery");
        HostRead(wh, hg, Note106);
        Expect(hg.G.Anim == 1 && hg.G.ParcelList.Count(x => x == "parcel106") == 1, "host reads it later: original condition false, nothing twice");
        p2.Sync();
        Expect(p2.G.Anim == 1, "P2 animation once");
        Expect(wh.ParcelDiscoveries == 1, "one host discovery counted");
    }

    // ---------------------------------------------------------------- 06
    static void Simultaneous()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh);
        // both read note_110-like code at the same moment: host's original first, P2's proposal arrives after
        var n110 = new Note { Name = "note_110", Pref = "parcel3_madison", AddCode = true };
        hg.G.ReadNote(n110, true);   // host original (scan not yet run)
        Expect(p2.ReadNote(n110) == ProposalCode.Unchanged, "P2's late proposal: Unchanged (host original condition false)");
        Expect(hg.G.ParcelList.Count(x => x == "parcel3_madison") == 1 && hg.G.Anim == 1, "one host list entry, one host animation");
        wh.PublishParcels(); p2.Sync();
        Expect(Shared(wh).Has("parcel3_madison") && p2.G.Find("parcel3_madison").Discovered && p2.G.Anim == 1, "shared, P2 one animation (its own)");
        // other order: P2 first, host reads after
        var n114 = new Note { Name = "note_114", Pref = "parcel114", AddCode = true };
        Expect(p2.ReadNote(n114) == ProposalCode.Accepted, "P2 first");
        hg.G.ReadNote(n114, true);
        Expect(hg.G.ParcelList.Count(x => x == "parcel114") == 1, "host original afterwards adds nothing");
    }

    // ---------------------------------------------------------------- 07
    static void Hack()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh);
        Expect(p2.Hack("parcel2_madison") == ProposalCode.Accepted, "P2 hacks an item with hacked_parcel_pref");
        Expect(hg.G.ParcelList.Contains("parcel2_madison") && !hg.G.Find("parcel2_madison").Discovered && !Shared(wh).Has("parcel2_madison"), "host: queued like the original, not yet usable (original needs save + load)");
        Expect(!p2.G.EnterCode("25252", "Madison St.") && !hg.G.EnterCode("25252", "Madison St."), "nobody can use it before the reload (original)");
        Expect(p2.Hack("parcel2_madison") == ProposalCode.Invalid, "second hack: already queued here, nothing proposed");
        Expect(p2.Send(_pid++, new ParcelCodeRequest { Source = ParcelSource.Hack, Pref = "parcel2_madison" }.ToBytes()) == ProposalCode.Unchanged && hg.G.ParcelList.Count(x => x == "parcel2_madison") == 1, "replayed hack: Unchanged");
        hg.G.Save(); hg.G.Load(); wh.PublishParcels(); p2.Sync();
        Expect(hg.G.Find("parcel2_madison").Discovered && p2.G.Find("parcel2_madison").Discovered && p2.G.EnterCode("25252", "Madison St."), "after the host's save + load: usable on both");
        Expect(p2.G.Inventory == 2 && hg.G.Inventory == 0, "the hacked item itself stays Player 2's (personal inventory)");
    }

    // ---------------------------------------------------------------- 08
    static void Invalid()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh);
        Expect(p2.Propose(new ParcelCodeRequest { Source = ParcelSource.Note, Pref = "parcel_999" }) == ProposalCode.Invalid, "unknown pref: Invalid");
        Expect(p2.Send(_pid++, new byte[] { 9, 1, 65 }) == ProposalCode.Invalid, "malformed: Invalid");
        uint rev; int sr; float sh;
        Expect(wh.HandleProposal("c2", 2, _pid++, new StateEntry { Scope = StateScope.Session, ScopeArg = 3, Key = WorldKeys.ParcelCode, Value = new ParcelCodeRequest { Source = ParcelSource.Note, Pref = "parcel4" }.ToBytes() }, 1, out rev, out sr, out sh) == ProposalCode.Invalid, "wrong arg: Invalid");
        hg.G.Loaded = false;
        Expect(p2.Propose(new ParcelCodeRequest { Source = ParcelSource.Note, Pref = "parcel4" }) == ProposalCode.Busy && !hg.G.Find("parcel4").Discovered, "host scene loading: Busy (client retries), nothing applied");
        hg.G.Loaded = true;
        Expect(p2.Send(_pid++, new ParcelCodeRequest { Source = ParcelSource.Note, Pref = "parcel4" }.ToBytes()) == ProposalCode.Accepted, "retry after the host is ready: accepted");
        hg.G.Prefs["parcel115"] = 2;   // taken parcel (mission string saved)
        Expect(p2.Send(_pid++, new ParcelCodeRequest { Source = ParcelSource.Note, Pref = "parcel115" }.ToBytes()) == ProposalCode.Unchanged && !hg.G.Find("parcel115").Discovered, "collected parcel: original condition false, nothing changes");
        var noHost = new WorldHost(3); noHost.SetPlayerRegion(2, Madison, 0); string why;
        Expect(noHost.DiscoverParcel(2, new ParcelCodeRequest { Source = ParcelSource.Note, Pref = "parcel4" }, out why) == ProposalCode.Busy, "no parcel host: Busy");
    }

    // ---------------------------------------------------------------- 09
    static void Story()
    {
        HostGame hg; var wh = NewHost(out hg); var p2 = new Client(wh);
        hg.G.Story = "story14"; p2.G.Story = "story14";
        p2.ReadNote(Note103);
        Expect(p2.G.StoryAdvanced == 0 && hg.G.StoryAdvanced == 0, "Player 2: no private story step; parcel path alone leaves the host story (W6A applies it, tests/W6ATests.cs 02)");
        Expect(hg.G.MissionList.Count == 0 && p2.G.MissionList.Count == 0 && hg.G.Inventory == 0 && p2.G.Inventory == 0 && hg.G.Money == 0 && p2.G.Money == 0, "a note grants no loot, no money, no mission string");
        Expect(p2.G.Xp == 1 && hg.G.Xp == 0, "note XP stays the reader's (original addPoint path untouched)");
        HostRead(wh, hg, Note103);
        Expect(hg.G.StoryAdvanced == 1, "the host's own read keeps the original story step (code already known: no second discovery)");
        Expect(hg.G.Anim == 1, "host animation only once (from Player 2's discovery)");
        hg.G.Story = "story15"; HostRead(wh, hg, Note103);
        Expect(hg.G.StoryAdvanced == 1, "original story condition (current step == storyDesc) never bypassed");
    }

    // ---------------------------------------------------------------- 10
    static ProposalCode Txn(WorldHost wh, byte slot, uint hash, byte state, double now, int ns = Madison, int baseVer = -1)
    {
        ObjectState cur; wh.Objects.TryGet(ObjectKind.ParcelLocker, ns, hash, out cur);
        var t = new ObjectTxnRequest { Txn = _pid, Op = ObjectOp.Set, Kind = (byte)ObjectKind.ParcelLocker, Ns = ns, Hash = hash, State = state, BaseVer = baseVer >= 0 ? (ushort)baseVer : cur.Ver, Epoch = wh.Objects.EpochOf(ns) };
        if (slot == 1) { string why; return wh.ObjectTransaction(1, t, now, out why); }
        uint rev; int sr; float sh;
        return wh.HandleProposal("c2", 2, _pid++, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() }, now, out rev, out sr, out sh);
    }
    static ProposalCode Scene(WorldHost wh, byte slot, int ns, int serial, double now)
    {
        var r = new ObjectSceneReport { Ns = ns, Serial = serial, Digest = Digest, Count = 900 };
        if (slot == 1) { string why; return wh.ObjectScene(1, r, now, out why); }
        uint rev; int sr; float sh;
        return wh.HandleProposal("c2", 2, _pid++, new StateEntry { Scope = StateScope.Player, ScopeArg = slot, Key = WorldKeys.ObjectScene, Value = r.ToBytes() }, now, out rev, out sr, out sh);
    }
    static byte Locker(WorldHost wh, uint h, int ns = Madison) { ObjectState s; return wh.Objects.TryGet(ObjectKind.ParcelLocker, ns, h, out s) ? s.State : (byte)0; }

    static void Lockers()
    {
        const uint L1 = 0x10C0E001, L2 = 0x10C0E002;
        Expect(ObjectStateRules.SupportedKind(ObjectKind.ParcelLocker) && ObjectStateRules.Lifetime(ObjectKind.ParcelLocker) == ObjectLifetime.RegionLife, "region kind, region lifetime (ParcelLocker.wasOpened has no save key)");
        Expect(ObjectStateRules.ValidState(ObjectKind.ParcelLocker, 1) && !ObjectStateRules.ValidState(ObjectKind.ParcelLocker, 2) && ObjectStateRules.CheckTransition(ObjectKind.ParcelLocker, 1, 0, ObjectOp.Set) != null, "one bit, open is terminal");
        Expect(ObjectStateRules.RegistryKey(ObjectKind.ParcelLocker) == "wobj22" && ObjectKinds.Get(ObjectKind.ParcelLocker).TypeName == "ParcelLocker", "wobj22 = census ParcelLocker");
        var wh = new WorldHost(62); wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, Madison, 0);
        Scene(wh, 1, Madison, 1, 0); string why; wh.ObjectInit(1, ObjectInitChunk.Split(Madison, wh.Objects.EpochOf(Madison), new List<ObjectInitEntry>())[0], 0.1, out why); Scene(wh, 2, Madison, 2, 0.2);
        Expect(Txn(wh, 2, L1, 1, 1) == ProposalCode.Accepted && Locker(wh, L1) == 1, "P2 opens locker1 (after its original code check): committed");
        Expect(Txn(wh, 1, L1, 1, 1.01, Madison, 0) == ProposalCode.Unchanged, "P1 opening the same locker at the same time: Unchanged");
        Expect(Txn(wh, 1, L1, 0, 1.2) == ProposalCode.Invalid && Locker(wh, L1) == 1, "never closed again");
        Expect(Txn(wh, 1, L2, 1, 1.3) == ProposalCode.Accepted, "P1 opens another");
        // late joiner: registry snapshot lists both open (WorldObjectSync full reconcile, silent)
        int n = 0; for (int p = 0; p < ObjectStateRules.MaxPages; p++) { var e = wh.Reg.Get(StateScope.Region, Madison, ObjectStateRules.RegistryKey(ObjectKind.ParcelLocker, p)); ObjectStateList l; if (e != null && ObjectStateList.TryParse(e.Value, out l)) n += l.Items.Count; }
        Expect(n == 2, "two open lockers in the region pages for late joiners");
        // region reload: both leave -> closed again (original: wasOpened is runtime state of the scene object)
        wh.SetPlayerRegion(1, Hideout, 5); wh.SetPlayerRegion(2, Hideout, 5); wh.Tick(5 + ObjectStateRules.MemberGraceSeconds + 0.1);
        Expect(Locker(wh, L1) == 0 && Locker(wh, L2) == 0 && wh.Objects.EpochOf(Madison) == 0, "region reload: lockers closed again");
    }

    // ---------------------------------------------------------------- 11
    static void ParcelLoot()
    {
        string why;
        Expect(LootRules.Classify(new LootEntry { Name = "sanny_parcel3", Flags = LootFlags.Parcel }, new ItemFacts { Known = true, Value = 150, AddXp = 5 }, out why) == LootClass.Valuable, "parcel item = shared world loot (one take)");
        var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var outbox = new List<StateEntry>();
        const uint Sanny = 0x5A11E003;
        // P1's original pickup claims first; P2's take of the same parcel arrives during it
        Expect(book.Claim(Madison, Sanny, 77, out why), "P1 claim");
        Expect(book.StateOf(Madison, Sanny) == LootState.Pending, "pending: Player 2's take is refused meanwhile (HostDecide)");
        Expect(book.Commit(Madison, Sanny, reg, outbox) == ProposalCode.Accepted, "P1's original reaches its irreversible step: taken");
        Expect(book.Commit(Madison, Sanny, reg, outbox) == ProposalCode.Unchanged && !book.Claim(Madison, Sanny, 78, out why), "taken once: no second take by anybody");
        // the host parcel list mutation happens only for an accepted take (source scan in W4.2D C2) - model it once
        var hostGame = new Game(); hostGame.ParcelList.Add("parcel3_madison"); hostGame.PickParcel("parcel3_madison");
        Expect(hostGame.MissionList.Count == 1 && hostGame.ParcelList.Count == 0 && hostGame.Inventory == 1, "original parcel mutation once, item in the taker's inventory");
        hostGame.Save(); Expect(hostGame.Pref("parcel3_madison") == 2, "saved as collected (SPrefs 2): its note hides at the next load (ReadNote.Start), its code can no longer be discovered");
        Expect(!ParcelRules.NoteDiscovers(false, hostGame.Pref("parcel3_madison")), "collected code not rediscoverable");
        var src = Src("Plugin", "WorldLootSync.cs");
        Expect(src.Contains("mission.Add(e.Pref); parcels.Remove(e.Pref);"), "WorldLootSync host parcel mutation unchanged (W4.2D)");
        var wallet = Src("Shared", "Wallet.cs");
        Expect(wallet.Length > 0 && !Src("Plugin", "ParcelSync.cs").Contains("moneyAm"), "codes never touch money; sales use the existing shared wallet");
    }

    // ---------------------------------------------------------------- 12
    static void Glue()
    {
        var ps = Src("Plugin", "ParcelSync.cs"); var code = Strip(ps);
        Expect(code.Contains("AccessTools.Method(note, \"BeginRead\", Type.EmptyTypes)") && code.Contains("AccessTools.Method(item, \"HackedElectronic\", Type.EmptyTypes)"), "hooks on the original ReadNote.BeginRead / ItemID.HackedElectronic");
        Expect(code.Contains("if (!i._host && !StorySync.ClientHandlesStory && _noteAdvance(__instance)) { _setNoteAdvance(__instance, false);") && code.Contains("if ((__state & 2) != 0) _setNoteAdvance(__instance, true);"), "fallback (StorySync not running): Player 2 story step suppressed for the one call and restored");
        int restore = code.IndexOf("if ((__state & 2) != 0) _setNoteAdvance(__instance, true);", StringComparison.Ordinal), inst = code.IndexOf("var i = Instance; if (i == null) return;", restore, StringComparison.Ordinal);
        Expect(restore > 0 && inst > restore, "restore happens before the session check (never left false)");
        Expect(code.Contains("ParcelRules.NoteDiscovers(_codeDiscovered(c), saved)") && code.Contains("_setDiscovered(c, true);") && code.Contains("list.Add(pref);") && code.Contains("_addNewCode(inv)"), "host apply = ReadNote IL_00b6..IL_00d8");
        Expect(code.Contains("if (!ParcelRules.HackQueues(saved) || list.Contains(pref)) return ParcelApply.AlreadyKnown;"), "host hack apply = HackedElectronic IL_0033..IL_005c (no discoveredOnMap)");
        Expect(code.Contains("if (c != null && !_codeDiscovered(c) && s.Has(_codePref(c))) { _setDiscovered(c, true); n++; }"), "client apply sets true only");
        Expect(code.IndexOf("_setDiscovered(c, false)", StringComparison.Ordinal) < 0, "never clears a code");
        Expect(code.Contains("if (_applyLive) { var inv = Inventory(); if (inv != null) _addNewCode(inv); }") && code.Contains("_boundStorage = storage; _applyLive = false;"), "animation only for live changes, silent after scene loads");
        Expect(code.Contains("_nextScan = now + 2.0;") && code.Contains("if (mask == _publishedMask"), "host scan every 2 s, publish only on change");
        foreach (var bad in new[] { "FindObjectsOfType", "FindObjectOfType", "GameObject.Find", "GetComponentsInChildren", "OnGUI", "GUI.", "SpawnWarning", "AdvanceStory(", "Mission_Complete", "moneyAm", "SPrefs.SetInt", "_prefSet" })
            Expect(code.IndexOf(bad, StringComparison.Ordinal) < 0, "ParcelSync has no " + bad);
        var wos = Strip(Src("Plugin", "WorldObjectSync.cs"));
        Expect(wos.Contains("AccessTools.Method(AccessTools.TypeByName(\"ParcelCode\"), \"OpenLocker\")") && wos.Contains("_h.Patch(openLocker, postfix: hm(\"LockerOpenedPost\"))"), "locker capture = ParcelCode.OpenLocker postfix");
        Expect(wos.Contains("if (i == null || WorldObjectBridge.Applying) return; try { i.OnLockerOpened(__instance); }"), "no echo from the replica");
        var br = Strip(Src("Plugin", "WorldObjectBridge.cs"));
        int ol = br.IndexOf("public void OpenLocker(", StringComparison.Ordinal);
        string body = br.Substring(ol, br.IndexOf("void OpenSash(", ol, StringComparison.Ordinal) - ol);
        foreach (var x in new[] { "if (_plOpened(l)) return;", "_pcSetOpened(c, true);", "_plSetOpened(l, true);", "if (!silent)", "snd.Play();" }) Expect(body.Contains(x), "replica mirrors OpenLocker + open sound: " + x);
        foreach (var x in new[] { "parcelsOpened", "QuitMinigame", "Invoke(" }) Expect(body.IndexOf(x, StringComparison.Ordinal) < 0, "replica never: " + x);
        var ws = Strip(Src("Plugin", "WorldSync.cs")); var wt = Strip(Src("Shared", "WorldTime.cs")); var pl = Strip(Src("Plugin", "Plugin.cs"));
        Expect(ws.Contains("ProposalResultHook11(id, code)") && wt.Contains("p.Key == WorldKeys.ParcelCode") && pl.Contains("_parcels = new ParcelSync(node, log, _world)") && pl.Contains("Release(ref _parcels, \"PARCEL\")"), "wiring: dispatch, result hook, lifecycle");
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
