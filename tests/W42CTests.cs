// W4.2C Phase B offline tests: shared furniture (drawers, cupboards, fridges, file / glass cabinets, containers = original
// Door leaves) on the host-owned region-object registry, region coverage (Madison Street, Hillville, hideout), the
// incremental committed-page cache and loot safety. Drives the SAME Unity-free pieces as the plugin: DoorTypes (which leaves
// are shared), RegionStateBook through WorldHost ("objScene" / "objInit" / "objTxn"), the registry snapshot + delta wire
// format to a client registry, ObjectPageCache (the plugin's committed-state view) and WorldDoorRules (the plugin's apply
// decision) on simulated replica leaves. Swings, sounds and hooks in Unity are manual QA (docs/TESTING.md §34).
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W42CTests
{
    static int _passed, _failed;
    const int Hideout = 0, Hillville = 97, Madison = 99, Bank = 92;
    const uint Digest = 0x51C0FFEE;
    const byte D = (byte)ObjectKind.Door, L = (byte)ObjectKind.Lockpick;

    static int Main()
    {
        Run("01 door types: furniture leaves shared like doors, only car doors stay local (flag order = original Door fields)", Types);
        Run("02 drawer / cupboard / fridge: quick open + close, hold-E slow open + fast finish, replicas follow through real deltas", FurnitureTransitions);
        Run("03 both players on the same drawer: first commit wins, the stale one is restored; reordered requests never reverse", SameDrawer);
        Run("04 locked cabinet: the lock gates the leaf (opening waits for the lock state, closing never does)", LockedCabinet);
        Run("05 regions: Hillville + hideout shared, independent epochs; P2 alone in Hillville while the host stays in the hideout", Regions3);
        Run("06 region reload semantics: furniture resets with the region (last member leaves / alone reload), never persisted", Reload);
        Run("07 capacity + packets: Madison-sized region (1142 leaves) fits, 1201st Busy; page <= 804 B; rate limit 8/s", Capacity);
        Run("08 committed-page cache: only changed pages parsed, exact diffs, prune moves across pages, clear, bad pages", PageCache);
        Run("09 loot safety: opening furniture writes only the door page (no loot / item / XP keys); random-cabinet loot via the seeded roll (W4.2D)", LootSafety);
        Run("10 plugin wiring: bridge + sync use the shared rule, inactive leaves snap, no new side effects, protocol unchanged", Wiring);
        Console.WriteLine(_failed == 0 ? "W42C TESTS PASSED (" + _passed + ")" : "W42C TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- harness
    static uint _pid = 1;
    static ProposalCode Propose(WorldHost wh, byte slot, StateEntry e, double now)
    { uint rev; int sr; float sh; return wh.HandleProposal("c" + slot, slot, _pid++, e, now, out rev, out sr, out sh); }

    static ProposalCode Scene(WorldHost wh, byte slot, int ns, int serial, double now, uint digest = Digest)
    {
        var r = new ObjectSceneReport { Ns = ns, Serial = serial, Digest = digest, Count = 700 };
        if (slot == 1) { string why; return wh.ObjectScene(1, r, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Player, ScopeArg = slot, Key = WorldKeys.ObjectScene, Value = r.ToBytes() }, now);
    }

    static ProposalCode Init(WorldHost wh, byte slot, int ns, List<ObjectInitEntry> all, double now)
    {
        ProposalCode last = ProposalCode.Invalid;
        foreach (var c in ObjectInitChunk.Split(ns, wh.Objects.EpochOf(ns), all))
        {
            if (slot == 1) { string why; last = wh.ObjectInit(1, c, now, out why); }
            else last = Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectInit, Value = c.ToBytes() }, now);
            if (last != ProposalCode.Accepted) return last;
        }
        return last;
    }

    static ProposalCode Txn(WorldHost wh, byte slot, byte kind, uint hash, byte state, double now, int ns = Madison, int baseVer = -1)
    {
        ObjectState cur; wh.Objects.TryGet((ObjectKind)kind, ns, hash, out cur);
        var t = new ObjectTxnRequest { Txn = _pid, Op = ObjectOp.Set, Kind = kind, Ns = ns, Hash = hash, State = state, BaseVer = baseVer >= 0 ? (ushort)baseVer : cur.Ver, Epoch = wh.Objects.EpochOf(ns) };
        if (slot == 1) { string why; return wh.ObjectTransaction(1, t, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() }, now);
    }

    static ProposalCode Reserve(WorldHost wh, byte slot, uint hash, double now, int ns = Madison)
    {
        var t = new ObjectTxnRequest { Txn = _pid, Op = ObjectOp.Reserve, Kind = L, Ns = ns, Hash = hash, Epoch = wh.Objects.EpochOf(ns) };
        if (slot == 1) { string why; return wh.ObjectTransaction(1, t, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() }, now);
    }

    static WorldHost Host(int hostRegion, int p2Region)
    {
        var wh = new WorldHost(91);
        wh.SetPlayerRegion(1, hostRegion, 0); wh.SetPlayerRegion(2, p2Region, 0);
        return wh;
    }

    // Client side of one instance: a registry mirrored over the real snapshot + delta wire format, the plugin's
    // committed-page cache and simulated replica leaves of one region (open / broken bits as Door.isOpen / brokeHinge).
    sealed class Client
    {
        public readonly StateRegistry Reg; readonly WorldHost _wh; int _sent; public readonly int Ns;
        public readonly ObjectPageCache Doors = new ObjectPageCache();
        public readonly Dictionary<uint, byte> Leaves = new Dictionary<uint, byte>();
        public readonly HashSet<uint> Locked = new HashSet<uint>();   // tag "Lock1" here (lock-gated leaf)
        public int Swings;
        readonly string[] _keys = new string[ObjectStateRules.MaxPages];
        public Client(WorldHost wh, int ns)
        {
            _wh = wh; Ns = ns;
            for (int p = 0; p < _keys.Length; p++) _keys[p] = ObjectStateRules.RegistryKey(ObjectKind.Door, p);
            var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
            var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep, hd; List<StateEntry> entries;
            Expect(StateMessages.TryReadSnapshot(r, out ep, out hd, out entries), "snapshot");
            Reg = StateRegistry.CreateClient(); Reg.LoadSnapshot(ep, hd, entries);
            _sent = wh.Outbox.Count;
        }
        // forwards the host's committed deltas (wire round trip), then reconciles like WorldObjectSync.Reconcile(changed)
        public int Sync()
        {
            for (; _sent < _wh.Outbox.Count; _sent++)
            {
                var w = new NetDataWriter(); StateMessages.WriteDelta(w, _wh.Reg.Epoch, _wh.Outbox[_sent]);
                var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep; StateEntry e;
                Expect(StateMessages.TryReadDelta(r, out ep, out e), "delta");
                Reg.ApplyDelta(ep, e);
            }
            var changed = Doors.Refresh(p => Reg.Get(StateScope.Region, Ns, _keys[p]));
            int n = 0;
            if (changed != null) foreach (var h in changed) n += Apply(h) ? 1 : 0;
            return n;
        }
        public bool Apply(uint h)
        {
            ObjectState s; byte want = Doors.TryGet(h, out s) ? s.State : (byte)0;
            byte local; Leaves.TryGetValue(h, out local);
            var a = WorldDoorRules.Decide(want, (local & 1) != 0, (local & 4) != 0, false, false, !Locked.Contains(h), false);
            if (a == DoorApply.Swing || a == DoorApply.Break) { Leaves[h] = (byte)(want & 5); Swings++; return true; }
            return false;
        }
        public byte Leaf(uint h) { byte b; Leaves.TryGetValue(h, out b); return b; }
    }

    static WorldHost ReadyMadison(out Client host, out Client p2)
    {
        var wh = Host(Madison, Madison);
        Expect(Scene(wh, 1, Madison, 1, 0) == ProposalCode.Accepted && Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0.1) == ProposalCode.Accepted, "host initializes Madison");
        Expect(Scene(wh, 2, Madison, 2, 0.2) == ProposalCode.Accepted, "P2 joins the epoch");
        host = new Client(wh, Madison); p2 = new Client(wh, Madison);
        return wh;
    }

    // ---------------------------------------------------------------- 01
    static void Types()
    {
        Expect(DoorTypes.FlagFields.Length == 7 && DoorTypes.FlagFields[0] == "isCarDoor", "flag table starts with isCarDoor");
        string[] expect = { "isCarDoor", "isDrawer", "isCupboard", "isFridge", "isFileCabinet", "isGlassCabinet", "isContainer" };
        DoorType[] bits = { DoorType.CarDoor, DoorType.Drawer, DoorType.Cupboard, DoorType.Fridge, DoorType.FileCabinet, DoorType.GlassCabinet, DoorType.Container };
        for (int i = 0; i < 7; i++)
        {
            Expect(DoorTypes.FlagFields[i] == expect[i] && (byte)bits[i] == (byte)(1 << i), "flag " + i + " = " + expect[i] + " = bit " + i);
            var f = new bool[7]; f[i] = true;
            Expect(DoorTypes.FromFlags(f) == bits[i], "FromFlags " + expect[i]);
        }
        Expect(DoorTypes.Shared(DoorType.None) && !DoorTypes.IsFurniture(DoorType.None), "ordinary door / gate / window leaf: shared, not furniture");
        foreach (var t in new[] { DoorType.Drawer, DoorType.Cupboard, DoorType.Fridge, DoorType.FileCabinet, DoorType.GlassCabinet, DoorType.Container })
            Expect(DoorTypes.Shared(t) && DoorTypes.IsFurniture(t), t + " shared as furniture");
        Expect(!DoorTypes.Shared(DoorType.CarDoor) && !DoorTypes.Shared(DoorType.CarDoor | DoorType.Drawer) && !DoorTypes.IsFurniture(DoorType.CarDoor | DoorType.Cupboard), "a car door is never shared here (vdoor), even with a furniture flag");
        Expect(DoorTypes.FromFlags(null) == DoorType.None && DoorTypes.FromFlags(new bool[2] { false, true }) == DoorType.Drawer, "short / missing flag arrays");
        Expect(ObjectStateRules.SupportedKind(ObjectKind.Door) && !ObjectStateRules.SupportedKind(ObjectKind.Safe), "furniture uses the existing Door kind (no new kind, no new message)");
    }

    // ---------------------------------------------------------------- 02
    static void FurnitureTransitions()
    {
        Client host, p2; var wh = ReadyMadison(out host, out p2);
        const uint Drawer = 0xD0001, Cupboard = 0xC0002, Fridge = 0xF0003;
        // P2 quick-opens a drawer (Door.Use: isOpen true)
        p2.Leaves[Drawer] = 1;
        Expect(Txn(wh, 2, D, Drawer, ObjectStateRules.DoorOpen, 1.0) == ProposalCode.Accepted, "P2's drawer open committed");
        Expect(host.Sync() == 1 && host.Leaf(Drawer) == 1 && host.Swings == 1, "host replica swings the drawer open once");
        Expect(p2.Sync() == 0 && p2.Leaf(Drawer) == 1, "the actor does not re-apply its own change");
        // host closes it again
        host.Leaves[Drawer] = 0;
        Expect(Txn(wh, 1, D, Drawer, 0, 2.0) == ProposalCode.Accepted && p2.Sync() == 1 && p2.Leaf(Drawer) == 0, "host closes, P2 replica closes");
        Expect(host.Sync() == 0, "nothing re-applied on the actor");
        // hold-E slow open of a cupboard, then the actor releases E (Door.SlowFailed: slow mark dropped, new version)
        p2.Leaves[Cupboard] = 1;
        Expect(Txn(wh, 2, D, Cupboard, ObjectStateRules.DoorOpen | ObjectStateRules.DoorSlow, 3.0) == ProposalCode.Accepted, "slow open committed");
        ObjectState s; wh.Objects.TryGet(ObjectKind.Door, Madison, Cupboard, out s); ushort v1 = s.Ver;
        Expect(host.Sync() == 1 && host.Leaf(Cupboard) == 1, "replica opens (slow)");
        Expect(host.Doors.TryGet(Cupboard, out s) && (s.State & ObjectStateRules.DoorSlow) != 0, "slow mark visible to the replica");
        Expect(Txn(wh, 2, D, Cupboard, ObjectStateRules.DoorOpen, 3.4) == ProposalCode.Accepted, "fast finish = new version");
        wh.Objects.TryGet(ObjectKind.Door, Madison, Cupboard, out s);
        Expect(s.Ver == v1 + 1 && host.Sync() == 0 && WorldDoorRules.FastFinish(s.State, true, false, true), "replica keeps the leaf open and finishes its slow swing fast (no second swing)");
        // fridge: open then a repeated (presentation-only) report is Unchanged, no new version
        p2.Leaves[Fridge] = 1;
        Txn(wh, 2, D, Fridge, ObjectStateRules.DoorOpen, 4.0);
        wh.Objects.TryGet(ObjectKind.Door, Madison, Fridge, out s); ushort fv = s.Ver;
        Expect(Txn(wh, 2, D, Fridge, ObjectStateRules.DoorOpen, 4.5) == ProposalCode.Unchanged, "repeated open = Unchanged");
        wh.Objects.TryGet(ObjectKind.Door, Madison, Fridge, out s);
        Expect(s.Ver == fv && host.Sync() == 1 && host.Leaf(Fridge) == 1, "one version, one replica swing");
        Expect(Txn(wh, 2, D, Fridge, 8, 5.0) == ProposalCode.Invalid && Txn(wh, 2, D, Fridge, ObjectStateRules.DoorBroken, 5.1) == ProposalCode.Invalid, "invalid door bits refused (unknown bit / broken without open)");
    }

    // ---------------------------------------------------------------- 03
    static void SameDrawer()
    {
        Client host, p2; var wh = ReadyMadison(out host, out p2);
        const uint X = 0xD0100;
        // both open the closed drawer in the same moment from base v0: host first
        host.Leaves[X] = 1; p2.Leaves[X] = 1;
        Expect(Txn(wh, 1, D, X, 1, 1.0, Madison, 0) == ProposalCode.Accepted, "host commits first");
        Expect(Txn(wh, 2, D, X, 1, 1.0, Madison, 0) == ProposalCode.Unchanged, "P2's identical physical result = Unchanged (no flip-flop)");
        // P2 closes while the host closes too: stale base refused
        ObjectState s; wh.Objects.TryGet(ObjectKind.Door, Madison, X, out s);
        Expect(Txn(wh, 1, D, X, 0, 2.0, Madison, s.Ver) == ProposalCode.Accepted, "host closes from v" + s.Ver);
        p2.Leaves[X] = 1;   // P2 opened it locally again meanwhile (its proposal still carries the old base)
        Expect(Txn(wh, 2, D, X, 1, 2.0, Madison, s.Ver) == ProposalCode.Stale, "P2's open on the old base is Stale");
        Expect(p2.Sync() == 1 && p2.Leaf(X) == 0, "P2's refused change: the committed (closed) state is restored");
        // a delayed / reordered old request never reverses the newer state
        wh.Objects.TryGet(ObjectKind.Door, Madison, X, out s); ushort cur = s.Ver;
        Expect(Txn(wh, 2, D, X, 1, 3.0, Madison, cur - 1) == ProposalCode.Stale, "reordered request refused");
        wh.Objects.TryGet(ObjectKind.Door, Madison, X, out s);
        Expect(s.Ver == cur && s.State == 0, "state unchanged by the reordered request");
    }

    // ---------------------------------------------------------------- 04
    static void LockedCabinet()
    {
        Client host, p2; var wh = ReadyMadison(out host, out p2);
        const uint Leaf = 0xCA0001, Lock = 0x5CA001;
        host.Locked.Add(Leaf); p2.Locked.Add(Leaf);
        // P2 picks the cabinet lock (reservation -> minigame -> result), then opens the leaf
        Expect(Reserve(wh, 2, Lock, 1.0) == ProposalCode.Accepted && Txn(wh, 2, L, Lock, ObjectStateRules.LockUnlocked, 2.0) == ProposalCode.Accepted, "cabinet lock picked");
        p2.Locked.Remove(Leaf); p2.Leaves[Leaf] = 1;
        Expect(Txn(wh, 2, D, Leaf, 1, 2.2) == ProposalCode.Accepted, "leaf open committed");
        // the host's replica has not applied the lock yet: opening waits (SkipLocked), never forces a locked leaf open
        Expect(WorldDoorRules.Decide(1, false, false, false, false, false, false) == DoorApply.SkipLocked, "open waits for the lock state");
        Expect(host.Sync() == 0 && host.Leaf(Leaf) == 0, "host leaf stays closed while locked here");
        host.Locked.Remove(Leaf);   // the lock replica (Lockpick unlock bridge) applied
        Expect(host.Apply(Leaf) && host.Leaf(Leaf) == 1, "after the lock the leaf opens");
        // closing never waits for the lock (NPC relock / ResetLock closes first)
        Expect(WorldDoorRules.Decide(0, true, false, false, false, false, false) == DoorApply.Swing, "close ignores the lock tag");
    }

    // ---------------------------------------------------------------- 05
    static void Regions3()
    {
        Expect(ObjectStateRules.SupportedNamespace(Madison) && ObjectStateRules.SupportedNamespace(Hillville) && ObjectStateRules.SupportedNamespace(Hideout), "Madison, Hillville, hideout shared");
        foreach (int ns in new[] { Bank, 91, 94, 96, 1, 2, 3, -1, IdentityNamespace.MainScene, Regions.None }) Expect(!ObjectStateRules.SupportedNamespace(ns), Regions.Name(ns) + " not shared");
        Expect(Regions.Name(Hideout) == "Hideout" && Regions.None != Hideout && IdentityNamespace.ForScene("PlayerHideout") == Hideout && IdentityNamespace.ForScene("Hillville") == Hillville, "hideout namespace 0 is a real region (None = int.MinValue)");
        // host in the hideout, P2 alone in Hillville: two independent epochs; the host never loads Hillville
        var wh = Host(Hideout, Hillville);
        wh.ObjectCatalog = (k, ns, h) => ns == Hideout ? (bool?)(h < 0x100) : null;   // host censused only its hideout
        Expect(Scene(wh, 1, Hideout, 10, 0) == ProposalCode.Accepted && Init(wh, 1, Hideout, new List<ObjectInitEntry>(), 0.1) == ProposalCode.Accepted, "host initializes the hideout");
        Expect(Scene(wh, 2, Hillville, 20, 0.2, 0xAAAA0001) == ProposalCode.Accepted, "P2 starts the Hillville epoch");
        Expect(Init(wh, 2, Hillville, new List<ObjectInitEntry> { new ObjectInitEntry { Kind = D, Hash = 0x7001, State = 1 } }, 0.3) == ProposalCode.Accepted, "P2 initializes Hillville (its randomly open door)");
        Expect(Txn(wh, 2, D, 0x7002, 1, 1.0, Hillville) == ProposalCode.Accepted && wh.Objects.Unverified > 0, "P2 opens a Hillville drawer: accepted unverified (no host census)");
        Expect(Txn(wh, 1, D, 0x10, 1, 1.0, Hideout) == ProposalCode.Accepted, "host opens a hideout cupboard (strict catalog)");
        Expect(Txn(wh, 1, D, 0x900, 1, 1.1, Hideout) == ProposalCode.Invalid, "unknown hideout id refused by the host census");
        Expect(Txn(wh, 1, D, 0x7002, 0, 1.2, Hillville) == ProposalCode.NotAuthorized && Txn(wh, 2, D, 0x10, 0, 1.2, Hideout) == ProposalCode.NotAuthorized, "nobody changes a region it is not in");
        ObjectState s;
        Expect(wh.Objects.TryGet(ObjectKind.Door, Hillville, 0x7002, out s) && s.State == 1 && !wh.Objects.TryGet(ObjectKind.Door, Hideout, 0x7002, out s), "namespaces never mix");
        // the host travels to Hillville later and joins the running epoch with P2's committed furniture
        wh.SetPlayerRegion(1, Hillville, 5);
        Expect(Scene(wh, 1, Hillville, 30, 5.2, 0xAAAA0002) == ProposalCode.Invalid, "a different Hillville scene digest is refused (region stays local on that instance)");
        Expect(Scene(wh, 1, Hillville, 31, 5.5, 0xAAAA0001) == ProposalCode.Accepted, "host joins Hillville");
        var host = new Client(wh, Hillville);
        host.Leaves[0x7001] = 0; host.Leaves[0x7002] = 0;   // the host's own fresh Hillville scene: rolled closed
        Expect(host.Sync() == 2 && host.Leaf(0x7001) == 1 && host.Leaf(0x7002) == 1, "host reconciles to P2's committed state (canonical random door + drawer)");
    }

    // ---------------------------------------------------------------- 06
    static void Reload()
    {
        var wh = Host(Hideout, Hideout);
        Scene(wh, 1, Hideout, 1, 0); Init(wh, 1, Hideout, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Hideout, 2, 0);
        Expect(Txn(wh, 2, D, 0x20, 1, 1, Hideout) == ProposalCode.Accepted, "P2 opens a hideout drawer");
        ushort epoch = wh.Objects.EpochOf(Hideout);
        // both leave for Madison: after the grace period the hideout resets like the original reload
        wh.SetPlayerRegion(1, Madison, 2); wh.SetPlayerRegion(2, Madison, 2);
        wh.Tick(2 + ObjectStateRules.MemberGraceSeconds - 0.5);
        ObjectState s; Expect(wh.Objects.TryGet(ObjectKind.Door, Hideout, 0x20, out s) && s.State == 1, "kept during the grace period");
        wh.Tick(2 + ObjectStateRules.MemberGraceSeconds + 0.1);
        Expect(!wh.Objects.TryGet(ObjectKind.Door, Hideout, 0x20, out s) && wh.Objects.EpochOf(Hideout) == 0, "last member gone: the drawer is closed again (vanilla reload)");
        // coming back: new epoch, fresh initial state
        wh.SetPlayerRegion(2, Hideout, 20);
        Expect(Scene(wh, 2, Hideout, 3, 20) == ProposalCode.Accepted && wh.Objects.EpochOf(Hideout) == epoch + 1, "new epoch on return");
        // alone reload of a running region also resets
        Init(wh, 2, Hideout, new List<ObjectInitEntry>(), 20.1); Txn(wh, 2, D, 0x21, 1, 21, Hideout);
        Expect(Scene(wh, 2, Hideout, 4, 22) == ProposalCode.Accepted && wh.Objects.EpochOf(Hideout) == epoch + 2 && !wh.Objects.TryGet(ObjectKind.Door, Hideout, 0x21, out s), "alone reload = new epoch, furniture closed");
        Expect(ObjectStateRules.Lifetime(ObjectKind.Door) == ObjectLifetime.RegionLife, "furniture = region lifetime (Door has no save key)");
        // disconnect of the last member resets as well
        wh.PlayerDisconnected(2, 23);
        Expect(wh.Objects.EpochOf(Hideout) == 0, "Player 2 disconnects alone in the region: epoch ends");
    }

    // ---------------------------------------------------------------- 07
    static void Capacity()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, 0);
        // every one of Madison's 1142 Door components (doors + furniture; sashes are window states) opened once, 8 per second
        double t = 1;
        for (uint i = 0; i < 1142; i++) { if (i % 8 == 0) t += 1.01; Expect(Txn(wh, 2, D, 0x100000 + i, 1, t) == ProposalCode.Accepted, "leaf " + i); }
        Expect(wh.Objects.CountOf(ObjectKind.Door, Madison) == 1142, "1142 leaves tracked");
        int maxPage = 0;
        for (int p = 0; p < ObjectStateRules.MaxPages; p++)
        {
            var e = wh.Reg.Get(StateScope.Region, Madison, ObjectStateRules.RegistryKey(ObjectKind.Door, p));
            if (e != null) { maxPage = Math.Max(maxPage, e.Value.Length); Expect(e.Value.Length <= StateEntry.MaxValueBytes, "page fits a state entry"); }
        }
        Expect(maxPage == 804, "full page = 804 B (100 x 8 + 4)");
        for (uint i = 1142; i < 1200; i++) { if (i % 8 == 0) t += 1.01; Txn(wh, 2, D, 0x100000 + i, 1, t); }
        t += 1.01;
        Expect(Txn(wh, 2, D, 0x200000, 1, t) == ProposalCode.Busy && wh.Objects.CountOf(ObjectKind.Door, Madison) == 1200, "the 1201st object is refused (Busy), nothing evicted");
        // an existing object still changes at capacity
        Expect(Txn(wh, 2, D, 0x100005, 0, t + 0.01) == ProposalCode.Accepted, "known objects keep working at capacity");
        // rate limit: 8 single transitions per player per second (drawer spam), the 9th Busy, the next second fine
        var w2 = Host(Madison, Madison);
        Scene(w2, 1, Madison, 1, 0); Init(w2, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(w2, 2, Madison, 2, 0);
        for (uint i = 0; i < 8; i++) Expect(Txn(w2, 2, D, 0x300 + i, 1, 5.0 + i * 0.01) == ProposalCode.Accepted, "drawer " + i);
        Expect(Txn(w2, 2, D, 0x309, 1, 5.5) == ProposalCode.Busy, "9th within one second: Busy (the actor restores the committed state)");
        Expect(Txn(w2, 1, D, 0x309, 1, 5.5) == ProposalCode.Accepted, "the limit is per player");
        Expect(Txn(w2, 2, D, 0x30A, 1, 6.1) == ProposalCode.Accepted, "next second accepted");
        // one change = one 19 B request up, one page delta down (<= 804 B value)
        Expect(new ObjectTxnRequest().ToBytes().Length == ObjectTxnRequest.Bytes && ObjectTxnRequest.Bytes == 19, "19 B per proposal");
    }

    // ---------------------------------------------------------------- 08
    static void PageCache()
    {
        var reg = StateRegistry.CreateHost(5); var cache = new ObjectPageCache();
        Func<int, StateEntry> page = p => reg.Get(StateScope.Region, Madison, ObjectStateRules.RegistryKey(ObjectKind.Door, p));
        Action<int, ObjectState[]> write = (p, items) => { var l = new ObjectStateList(); l.Items.AddRange(items); reg.Commit(StateScope.Region, Madison, ObjectStateRules.RegistryKey(ObjectKind.Door, p), l.ToBytes()); };
        Expect(cache.Refresh(page) == null || cache.Count == 0, "empty registry");
        Expect(cache.Refresh(page) == null, "no change -> null (no work)");
        var p0 = new List<ObjectState>(); for (uint i = 0; i < 100; i++) p0.Add(new ObjectState { Hash = i, State = 1, Ver = 1, By = 2 });
        var p1 = new List<ObjectState>(); for (uint i = 100; i < 150; i++) p1.Add(new ObjectState { Hash = i, State = 1, Ver = 1, By = 2 });
        write(0, p0.ToArray()); write(1, p1.ToArray());
        long parsed = cache.PagesParsed;
        var ch = cache.Refresh(page);
        Expect(ch != null && ch.Count == 150 && cache.Count == 150 && cache.PagesParsed == parsed + 2, "two pages parsed, 150 new");
        // one leaf on page 1 changes: only page 1 parsed, exactly that hash reported
        p1[7] = new ObjectState { Hash = 107, State = 0, Ver = 2, By = 1 };
        write(1, p1.ToArray()); parsed = cache.PagesParsed;
        ch = cache.Refresh(page);
        Expect(ch != null && ch.Count == 1 && ch[0] == 107 && cache.PagesParsed == parsed + 1, "only page 1 parsed, diff = [107]");
        ObjectState s; Expect(cache.TryGet(107, out s) && s.State == 0 && s.Ver == 2 && cache.TryGet(3, out s) && s.State == 1, "values");
        // a version-only change (fast finish) is reported too
        p1[8] = new ObjectState { Hash = 108, State = 1, Ver = 2, By = 2 }; write(1, p1.ToArray());
        ch = cache.Refresh(page); Expect(ch.Count == 1 && ch[0] == 108, "version change reported");
        // prune moves hash 140 from page 1 to page 0 in two separate deltas (page 0 first): never reported as removed
        var np0 = new List<ObjectState>(p0); np0[99] = p1[40];   // 140 replaces 99 on page 0 (99 pruned)
        write(0, np0.ToArray());
        ch = cache.Refresh(page);
        Expect(ch.Contains(99) && !cache.TryGet(99, out s) && cache.TryGet(140, out s), "page 0 rewritten: 99 removed, 140 now owned by page 0");
        p1.RemoveAt(40); write(1, p1.ToArray());
        ch = cache.Refresh(page);
        Expect(!ch.Contains(140) && cache.TryGet(140, out s) && s.State == 1, "page 1 rewritten without 140: no false removal (owner check)");
        Expect(cache.Count == 149, "count after prune");
        // a page emptied (epoch end) removes its objects; an unparsable page counts as empty
        write(1, new ObjectState[0]);
        ch = cache.Refresh(page); Expect(ch.Count == 49 && cache.Count == 100, "page 1 cleared -> 49 removed");
        reg.Commit(StateScope.Region, Madison, ObjectStateRules.RegistryKey(ObjectKind.Door, 0), new byte[] { 9, 9, 9 });
        ch = cache.Refresh(page); Expect(ch.Count == 100 && cache.Count == 0, "bad page = no states (fail closed: defaults)");
        cache.Clear(); Expect(cache.Count == 0 && cache.Refresh(page) != null, "Clear forgets revisions (next refresh re-reads)");
        // randomized equivalence with a full rebuild (the previous algorithm)
        var rnd = new Random(42); var reg2 = StateRegistry.CreateHost(6); var c2 = new ObjectPageCache();
        var pages = new List<ObjectState>[ObjectStateRules.MaxPages]; for (int p = 0; p < pages.Length; p++) pages[p] = new List<ObjectState>();
        Func<int, StateEntry> pg2 = p => reg2.Get(StateScope.Region, Hillville, ObjectStateRules.RegistryKey(ObjectKind.Door, p));
        var model = new Dictionary<uint, ObjectState>();
        for (int round = 0; round < 400; round++)
        {
            int p = rnd.Next(pages.Length);
            if (pages[p].Count < 100 && rnd.Next(3) > 0) { uint h = (uint)(p * 1000 + rnd.Next(1000)); if (!pages[p].Exists(x => x.Hash == h)) pages[p].Add(new ObjectState { Hash = h, State = (byte)rnd.Next(2), Ver = 1 }); }
            else if (pages[p].Count > 0) { int i = rnd.Next(pages[p].Count); var o = pages[p][i]; pages[p][i] = new ObjectState { Hash = o.Hash, State = (byte)(o.State ^ 1), Ver = (ushort)(o.Ver + 1) }; }
            var l = new ObjectStateList(); l.Items.AddRange(pages[p]); reg2.Commit(StateScope.Region, Hillville, ObjectStateRules.RegistryKey(ObjectKind.Door, p), l.ToBytes());
            var fresh = new Dictionary<uint, ObjectState>(); foreach (var pp in pages) foreach (var x in pp) fresh[x.Hash] = x;
            var expectDiff = new HashSet<uint>();
            foreach (var kv in fresh) { ObjectState o; if (!model.TryGetValue(kv.Key, out o) || o.State != kv.Value.State || o.Ver != kv.Value.Ver) expectDiff.Add(kv.Key); }
            foreach (var kv in model) if (!fresh.ContainsKey(kv.Key)) expectDiff.Add(kv.Key);
            model = fresh;
            var got = c2.Refresh(pg2);
            var gotSet = new HashSet<uint>(got ?? new List<uint>());
            Expect(gotSet.SetEquals(expectDiff), "round " + round + ": diff equals the full rebuild");
            Expect(c2.Count == model.Count, "round " + round + ": count");
        }
    }

    // ---------------------------------------------------------------- 09
    static void LootSafety()
    {
        Client host, p2; var wh = ReadyMadison(out host, out p2);
        var before = new HashSet<string>(); foreach (var e in wh.Reg.Entries) before.Add(e.Id);
        int outbox = wh.Outbox.Count;
        Txn(wh, 2, D, 0xD00D, 1, 1.0);   // open a drawer that contains loot
        var added = new List<string>(); foreach (var e in wh.Reg.Entries) if (!before.Contains(e.Id)) added.Add(e.Key);
        Expect(added.Count == 1 && added[0] == ObjectStateRules.RegistryKey(ObjectKind.Door, 0), "only the door page appeared: " + string.Join(",", added.ToArray()));
        for (int i = outbox; i < wh.Outbox.Count; i++)
        {
            var k = wh.Outbox[i].Key;
            Expect(k.StartsWith("wobj" + D, StringComparison.Ordinal), "committed delta is a door page, not loot / items / player keys: " + k);
            Expect(!k.StartsWith("wloot", StringComparison.Ordinal) && !k.StartsWith("Item", StringComparison.Ordinal) && k.IndexOf("XP", StringComparison.Ordinal) < 0, "no loot / item / XP key");
        }
        // opening = presentation of the leaf; taking the contents stays the separate loot path (W4.1 lootTxn, Madison subset)
        Expect(LootRules.SupportedNamespace(Madison) && LootRules.SupportedNamespace(Hillville) && LootRules.SupportedNamespace(Hideout), "W4.2D: loot shared in every region (host scene-file catalogs)");
        Expect((LootRules.Blocking & LootFlags.RandomCabinet) == 0 && LootRules.Exclusion(LootFlags.RandomCabinet) != null, "W4.2D: cabinet loot shared through the seeded roll + one outcome per slot (CabinetBook)");
        Expect(LootRules.Exclusion(LootFlags.RandomCabinet) != null, "RandomFileCabinet contents (rolled per instance) stay outside shared loot: no shared identity -> no double grant");
        Expect(LootRules.Exclusion(LootFlags.RandomCabinet | LootFlags.NoXp) != null && LootRules.Exclusion(LootFlags.None) == null, "flag rule");
        // the census marks every Pickupable below a RandomFileCabinet (its own transform + every referenced variant)
        var census = File.ReadAllText(Path.Combine(PluginDir(), "WorldCensus.cs"));
        Expect(census.Contains("f |= LootFlags.RandomCabinet") && census.Contains("foreach (var o in list) { var g = o as GameObject; if (g != null) _cabinet.Add(g.transform); }"), "variant lists (firstCabinetRandom ...) marked");
    }

    // ---------------------------------------------------------------- 10
    static void Wiring()
    {
        var bridge = StripComments(File.ReadAllText(Path.Combine(PluginDir(), "WorldObjectBridge.cs")));
        var sync = StripComments(File.ReadAllText(Path.Combine(PluginDir(), "WorldObjectSync.cs")));
        Expect(bridge.Contains("var names = DoorTypes.FlagFields;") && bridge.Contains("public bool SharedDoor(Component d) { return !_special[0](d); }"), "bridge reads the original flags in DoorTypes order; shared = not isCarDoor");
        Expect(!sync.Contains("Ordinary(") && !bridge.Contains("Ordinary(") && !bridge.Contains("SpecialNames"), "the blanket furniture exclusion is gone");
        Expect(sync.Contains("if (!DoorTypes.Shared(type))") && sync.Contains("!_b.SharedDoor(c)) return 0;") && sync.Contains("!_b.SharedDoor(c)) return false;"), "capture, initial state and apply use the same rule");
        Expect(sync.Contains("if (!silent && !door.gameObject.activeInHierarchy) { silent = true; _inactiveSnaps++; }"), "inactive leaves snap (no stuck Door.opening)");
        Expect(sync.Contains("List<uint> RefreshKind(int ki) { return _com[ki].Refresh(_pageOf[ki]); }") && !sync.Contains("new Dictionary<uint, ObjectState>()"), "committed view = incremental page cache, no full rebuild");
        // replicas still never call gameplay originals (furniture adds none: Door.Use has no item / XP / mission call)
        foreach (var bad in new[] { "\"Use\"", "SlowFailed", "MakeNoise", "AlertNPC", "Mission_Complete", "AdvanceStory", "currentXPPoints", "item_amount", "SPrefs", "FBPP" })
            Expect(bridge.IndexOf(bad, StringComparison.Ordinal) < 0, "bridge never touches " + bad);
        foreach (var bad in new[] { "MakeNoise", "AlertNPC", "Mission_Complete", "AdvanceStory", "currentXPPoints", "item_amount", "SetActive(", "Destroy(", "SPrefs", "FBPP", "FindObjectsOfType", "GetComponentsInChildren" })
            Expect(sync.IndexOf(bad, StringComparison.Ordinal) < 0, "sync never calls " + bad);
        Expect(File.ReadAllText(Path.Combine(PluginDir(), "WorldObjectSync.cs")).Contains("if (region == Regions.None || !ObjectStateRules.SupportedNamespace(region)) return;   // no work elsewhere"), "no per-frame work outside shared regions");
        Expect(ProtocolInfo.ProtocolVersion == 33 && ProtocolInfo.ModVersion == "0.22.0", "protocol 28 (furniture wire unchanged; 27 = W4.1B shared money) / mod 0.16.1 (both instances must match)");
        Expect(Validation.Check(new Hello { ModVersion = "0.15.0", ClientNonce = new byte[JoinAuth.NonceBytes], InstanceId = "x" }, null, null, "h", false) == RejectCode.ModVersionMismatch, "a 0.15.0 peer (personal Player-2 money) is rejected at the handshake");
        Expect(Validation.Check(new Hello { ModVersion = "0.14.4", ClientNonce = new byte[JoinAuth.NonceBytes], InstanceId = "x" }, null, null, "h", false) == RejectCode.ModVersionMismatch, "a 0.14.4 peer (furniture local) is rejected at the handshake");
    }

    static string PluginDir()
    {
        var d = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 8 && d != null; i++) { var p = Path.Combine(d, "src", "Plugin"); if (Directory.Exists(p)) return p; d = Path.GetDirectoryName(d.TrimEnd('\\', '/')); }
        throw new Exception("src/Plugin not found");
    }
    static string StripComments(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in s.Split('\n')) { int i = line.IndexOf("//", StringComparison.Ordinal); sb.Append(i >= 0 ? line.Substring(0, i) : line).Append('\n'); }
        return sb.ToString();
    }
}
