// W4.2B offline tests: shared doors, locks (lockpicking) and windows on the host-owned region-object registry.
// Drives the SAME Unity-free pieces as the plugin (src/Plugin/WorldObjectSync.cs + WorldObjectBridge.cs + WorldCensus.cs):
// RegionStateBook (epochs, members + grace, initializer chunks, reservations, transitions, catalog / restriction, prune),
// the codecs, WorldHost dispatch ("objScene" / "objInit" / "objTxn"), the client-side apply rules, and a small simulated
// scene per instance (doors / locks / windows as plain values) that runs the plugin's reconcile rule (committed state,
// absent = default) through real registry snapshots and deltas. Unity behaviour (hooks, swings, minigames) is manual QA
// (docs/TESTING.md §29); the plugin's side-effect rules are source-scanned.
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W42BTests
{
    static int _passed, _failed;
    const int Hideout = 0, Hillville = 97, Madison = 99;
    const uint Digest = 0x1234ABCD;

    static int Main()
    {
        Run("01 host presses F8 inside an already-loaded Madison: late census (objects only, no digest pin), host initializes", HostAlreadyInRegion);
        Run("02 canonical random-door initialization: the initializer's roll wins, the other scene reconciles (absent = closed)", CanonicalDoors);
        Run("03 initial snapshot of 450 doors + 60 locks: chunked, paged, beyond the old 100-object limit, via registry snapshot", BigInitialState);
        Run("04 doors: open / close / slow / broken hinge (terminal), stale and reordered requests never reverse a newer state", Doors);
        Run("05 lockpicking simple / medium / advanced: reserve -> original minigame -> one commit; key, padlock, crowbar", LockModes);
        Run("06 exactly one authoritative commit; repeated auto-lockpick callbacks are Unchanged (no new version)", SingleCommit);
        Run("07 rejection, failure, cancellation: busy reservation, release, expiry, refused result leaves no state", Rejection);
        Run("08 both players on the same lock: one reservation, instant actions override, first commit wins", SameLock);
        Run("09 windows: LockedWindow -> sash Door under ONE logical id; inside / cut / broken; sash bits only when opened", Windows);
        Run("10 replicas never invoke gameplay side effects (bridge + sync source scan) and Player 2 story/mission blocks", SideEffects);
        Run("11 Player 2 alone in Madison while the host is at the hideout: P2 initializes and changes objects", P2Alone);
        Run("12 host enters Player 2's region afterwards: joins the epoch, sees every committed state", HostEntersLater);
        Run("13 simulation ownership: lowest present member; transfer on leave; NPC relock only from the owner", Ownership);
        Run("14 region lifetime: brief gap keeps state, last member leaving resets (grace), alone reload = new epoch", Lifetime);
        Run("15 reconnect during an active interaction: reservation dropped, member removed, clean rejoin", Reconnect);
        Run("16 validation: epoch, membership, catalog (strict / unverified + prune), digest, restricted, initializer rules", Validation);
        Run("17 codecs + fuzz; protocol pins; save protection (no SPrefs/FBPP in the new code), no scene scans per frame", CodecsAndScans);
        Run("18 W4.2B.1 relock -> unlock: the door closes on the replica although the relock is applied first; never stuck open", RelockUnlock);
        Run("19 W4.2B.1 crowbar-broken door + gates: lock and broken leaf both shared, replica breaks (and enables) the original door", BrokenDoorAndGates);
        Run("20 W4.2B.1 hold-E doors: slow mark replayed, fast finish committed as a new version, original speed profile on the replica", SlowDoors);
        Run("21 W4.2B.1 driving camera: Player 2 without own TPP gets the original third-person default, never Player 1's value", DrivingCamera);
        Run("22 W4.2B.1 launcher host readiness gate, deferred campaign snapshot, developer test mode isolation", ReadinessAndDevTest);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed.");
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- helpers
    const byte D = (byte)ObjectKind.Door, L = (byte)ObjectKind.Lockpick, W = (byte)ObjectKind.LockedWindow;

    static ProposalCode Scene(WorldHost wh, byte slot, int ns, int serial, uint digest, double now)
    {
        var r = new ObjectSceneReport { Ns = ns, Serial = serial, Digest = digest, Count = 1402 };
        if (slot == 1) { string why; return wh.ObjectScene(1, r, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Player, ScopeArg = slot, Key = WorldKeys.ObjectScene, Value = r.ToBytes() }, now);
    }

    static uint _pid = 1;
    static ProposalCode Propose(WorldHost wh, byte slot, StateEntry e, double now)
    { uint rev; int sr; float sh; return wh.HandleProposal("c" + slot, slot, _pid++, e, now, out rev, out sr, out sh); }

    static ProposalCode Init(WorldHost wh, byte slot, int ns, List<ObjectInitEntry> all, double now)
    {
        var chunks = ObjectInitChunk.Split(ns, wh.Objects.EpochOf(ns), all);
        ProposalCode last = ProposalCode.Invalid;
        foreach (var c in chunks)
        {
            if (slot == 1) { string why; last = wh.ObjectInit(1, c, now, out why); }
            else last = Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectInit, Value = c.ToBytes() }, now);
            if (last != ProposalCode.Accepted) return last;
        }
        return last;
    }

    static ProposalCode Txn(WorldHost wh, byte slot, ObjectOp op, byte kind, uint hash, byte state, double now, int ns = Madison, int baseVer = -1, int epoch = -1)
    {
        ObjectState cur; wh.Objects.TryGet((ObjectKind)kind, ns, hash, out cur);
        var t = new ObjectTxnRequest { Txn = _pid, Op = op, Kind = kind, Ns = ns, Hash = hash, State = state, BaseVer = baseVer >= 0 ? (ushort)baseVer : cur.Ver, Epoch = epoch >= 0 ? (ushort)epoch : wh.Objects.EpochOf(ns) };
        if (slot == 1) { string why; return wh.ObjectTransaction(1, t, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() }, now);
    }

    static ObjectState St(StateRegistry reg, int ns, byte kind, uint hash)
    {
        for (int p = 0; p < ObjectStateRules.MaxPages; p++)
        {
            var e = reg.Get(StateScope.Region, ns, ObjectStateRules.RegistryKey((ObjectKind)kind, p));
            ObjectStateList l; ObjectState s;
            if (e != null && ObjectStateList.TryParse(e.Value, out l) && l.TryGet(hash, out s)) return s;
        }
        return default(ObjectState);
    }

    static RegionObjectRecord Rec(StateRegistry reg, int ns)
    {
        var e = reg.Get(StateScope.Region, ns, ObjectStateRules.RegionKey); RegionObjectRecord r;
        Expect(e != null && RegionObjectRecord.TryParse(e.Value, out r), "region record present");
        RegionObjectRecord.TryParse(e.Value, out r); return r;
    }

    static StateRegistry ClientCopy(WorldHost wh)
    {
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
        var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep, hd; List<StateEntry> entries;
        Expect(StateMessages.TryReadSnapshot(r, out ep, out hd, out entries), "snapshot");
        var c = StateRegistry.CreateClient(); c.LoadSnapshot(ep, hd, entries); return c;
    }

    // A simulated scene instance: what WorldObjectSync reconciles (doors open/broken, lock + window state as tracked).
    sealed class SimScene
    {
        public readonly Dictionary<uint, byte> Doors = new Dictionary<uint, byte>(), Locks = new Dictionary<uint, byte>(), Windows = new Dictionary<uint, byte>();
        public int SideEffects = 0;   // a replica must never produce any (alarm, noise, story, stats, tools)
        public List<ObjectInitEntry> NonDefault()
        {
            var l = new List<ObjectInitEntry>();
            foreach (var kv in Doors) if (kv.Value != 0) l.Add(new ObjectInitEntry { Kind = D, Hash = kv.Key, State = kv.Value });
            foreach (var kv in Locks) if (kv.Value != 0) l.Add(new ObjectInitEntry { Kind = L, Hash = kv.Key, State = kv.Value });
            foreach (var kv in Windows) if (kv.Value != 0) l.Add(new ObjectInitEntry { Kind = W, Hash = kv.Key, State = kv.Value });
            return l;
        }
        // full reconcile: every object -> committed state, absent = default (same rule as WorldObjectSync.Reconcile(full))
        public int Reconcile(StateRegistry reg, int ns)
        {
            int changed = 0;
            changed += Apply(reg, ns, D, Doors); changed += Apply(reg, ns, L, Locks); changed += Apply(reg, ns, W, Windows);
            return changed;
        }
        static int Apply(StateRegistry reg, int ns, byte kind, Dictionary<uint, byte> objs)
        {
            int n = 0;
            foreach (var h in new List<uint>(objs.Keys))
            {
                byte want = St(reg, ns, kind, h).State;
                if (kind == D)
                {
                    var a = WorldDoorRules.Decide(want, (objs[h] & 1) != 0, (objs[h] & 4) != 0, false, false, true, false);
                    if (a == DoorApply.Swing || a == DoorApply.Break) { objs[h] = (byte)(want & 5); n++; }
                }
                else if (kind == L) { var a = WorldLockRules.Decide(want, objs[h], false, false); if (a == LockApply.Unlock || a == LockApply.Relock) { objs[h] = want; n++; } }
                else { var a = WorldWindowRules.Decide(want, (byte)(objs[h] & 3), (objs[h] & 3) == 0, false, false); if (a == WindowApply.Glass || (want & 3) != 0) { if (objs[h] != want) n++; objs[h] = want; } }
            }
            return n;
        }
    }

    static SimScene NewScene(int doors, int locks, int windows, int seed, int openPct)
    {
        var s = new SimScene(); var rnd = new Random(seed);
        for (uint i = 0; i < doors; i++) s.Doors[0x1000 + i] = (byte)(rnd.Next(100) < openPct ? 1 : 0);   // per-instance randomlyOpen roll
        for (uint i = 0; i < locks; i++) s.Locks[0x5000 + i] = 0;
        for (uint i = 0; i < windows; i++) s.Windows[0x8000 + i] = 0;
        return s;
    }

    static WorldHost Host(params int[] regions)
    {
        var wh = new WorldHost(77);
        wh.SetPlayerRegion(1, regions[0], 0);
        if (regions.Length > 1) wh.SetPlayerRegion(2, regions[1], 0);
        return wh;
    }

    // ---------------------------------------------------------------- 01
    static void HostAlreadyInRegion()
    {
        var wh = Host(Madison, Hideout);
        // late census: the host's scene is not pristine -> digest 0 (no scene metadata pinned)
        Expect(Scene(wh, 1, Madison, 501, 0, 1) == ProposalCode.Accepted, "host scene accepted");
        var r = Rec(wh.Reg, Madison);
        Expect(r.Active && !r.Initialized && r.InitBy == 1 && r.IsPresent(1) && r.Owner == 1, "host is the initializer and owner: " + r);
        var host = NewScene(30, 4, 2, 1, 25); host.Doors[0x1000] = 1; host.Locks[0x5000] = ObjectStateRules.LockUnlocked;   // opened / picked BEFORE pressing F8
        Expect(Init(wh, 1, Madison, host.NonDefault(), 2) == ProposalCode.Accepted && Rec(wh.Reg, Madison).Initialized, "host initial state committed");
        Expect(St(wh.Reg, Madison, L, 0x5000).State == ObjectStateRules.LockUnlocked, "the lock the host picked before hosting is part of the canonical state");
        // Player 2 arrives with a fresh (pristine) census: its digest is accepted (nothing was pinned by the late census)
        wh.SetPlayerRegion(2, Madison, 3);
        Expect(Scene(wh, 2, Madison, 77, Digest, 3) == ProposalCode.Accepted, "client's fresh digest accepted after a late host census");
        var p2 = NewScene(30, 4, 2, 2, 25);
        p2.Reconcile(ClientCopy(wh), Madison);
        foreach (var kv in host.Doors) Expect(p2.Doors[kv.Key] == kv.Value, "P2 door " + kv.Key.ToString("X") + " follows the host's scene");
        Expect(p2.Locks[0x5000] == ObjectStateRules.LockUnlocked, "P2 sees the lock unlocked");
        // source: the plugin censuses the already-loaded scene for object kinds only, without a loot catalog, and reports digest 0
        var census = Src("WorldCensus.cs"); var sync = Src("WorldObjectSync.cs");
        Expect(sync.Contains("census.CensusLoadedScenes();"), "WorldObjectSync censuses scenes that were loaded before hosting");
        Expect(census.Contains("if (late && !Shared(info.Kind)) continue;") && census.Contains("_pItem != null && !late ? new LootCatalog(ns) : null"), "late census: object kinds only, no loot catalog");
        Expect(sync.Contains("if (census.IsLate(_region)) digest = 0;"), "late census reports no scene metadata");
    }

    // ---------------------------------------------------------------- 02
    static void CanonicalDoors()
    {
        var wh = Host(Hideout, Madison);
        var a = NewScene(200, 0, 0, 11, 20); var b = NewScene(200, 0, 0, 22, 20);   // two different randomlyOpen rolls
        int differ = 0; foreach (var k in a.Doors.Keys) if (a.Doors[k] != b.Doors[k]) differ++;
        Expect(differ > 20, "the two instances rolled different doors (" + differ + ")");
        Expect(Scene(wh, 2, Madison, 1, Digest, 1) == ProposalCode.Accepted && Rec(wh.Reg, Madison).InitBy == 2, "P2 first: initializer");
        Expect(Init(wh, 2, Madison, a.NonDefault(), 1.1) == ProposalCode.Accepted, "canonical state committed");
        wh.SetPlayerRegion(1, Madison, 2);
        Expect(Scene(wh, 1, Madison, 9, Digest, 2) == ProposalCode.Accepted && Rec(wh.Reg, Madison).InitBy == 2, "host joins (does not re-initialize)");
        int changed = b.Reconcile(wh.Reg, Madison);
        foreach (var k in a.Doors.Keys) Expect(a.Doors[k] == b.Doors[k], "door " + k.ToString("X") + " canonical");
        Expect(changed == differ, "exactly the differing doors were reconciled (" + changed + "/" + differ + ")");
        // the host can never establish a second, conflicting initial state
        Expect(Init(wh, 1, Madison, b.NonDefault(), 3) != ProposalCode.Accepted, "second initializer refused");
    }

    // ---------------------------------------------------------------- 03
    static void BigInitialState()
    {
        var wh = Host(Madison, Hideout);
        Scene(wh, 1, Madison, 1, Digest, 0);
        var s = NewScene(1142, 76, 184, 5, 40);
        foreach (var k in new List<uint>(s.Locks.Keys)) if (k % 4 == 0) s.Locks[k] = ObjectStateRules.LockUnlocked;
        var all = s.NonDefault();
        Expect(all.Count > 400, "large initial state " + all.Count);
        var chunks = ObjectInitChunk.Split(Madison, wh.Objects.EpochOf(Madison), all);
        Expect(chunks.Count >= 3 && chunks.TrueForAll(c => c.ToBytes().Length <= StateEntry.MaxValueBytes), chunks.Count + " chunks within 1024 B each");
        Expect(Init(wh, 1, Madison, all, 1) == ProposalCode.Accepted && Rec(wh.Reg, Madison).Initialized, "initialized");
        Expect(wh.Objects.CountOf(ObjectKind.Door, Madison) > ObjectStateRules.PageSize, "more than 100 doors committed: " + wh.Objects.CountOf(ObjectKind.Door, Madison));
        for (int p = 0; p < ObjectStateRules.MaxPages; p++) { var e = wh.Reg.Get(StateScope.Region, Madison, ObjectStateRules.RegistryKey(ObjectKind.Door, p)); if (e != null) Expect(e.Value.Length <= StateEntry.MaxValueBytes, "page " + p + " within 1024 B"); }
        var client = ClientCopy(wh);
        var other = NewScene(1142, 76, 184, 6, 40);
        other.Reconcile(client, Madison);
        foreach (var k in s.Doors.Keys) Expect(other.Doors[k] == s.Doors[k], "door after snapshot");
        foreach (var k in s.Locks.Keys) Expect(other.Locks[k] == s.Locks[k], "lock after snapshot");
        Console.WriteLine("      " + all.Count + " non-default objects -> " + chunks.Count + " chunks, " + wh.Objects.CountOf(ObjectKind.Door, Madison) + " door states in " + ((wh.Objects.CountOf(ObjectKind.Door, Madison) + 99) / 100) + " pages");
        // even the full census fits: 1200 per kind
        var wh2 = Host(Madison); Scene(wh2, 1, Madison, 1, Digest, 0);
        var full = new List<ObjectInitEntry>(); for (uint i = 0; i < 1200; i++) full.Add(new ObjectInitEntry { Kind = D, Hash = i + 1, State = 1 });
        Expect(Init(wh2, 1, Madison, full, 1) == ProposalCode.Accepted && wh2.Objects.CountOf(ObjectKind.Door, Madison) == 1200, "1200 doors (every Madison door) initialize");
    }

    // ---------------------------------------------------------------- 04
    static void Doors()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, Digest, 0);
        const uint X = 0xD00D;
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, 1, 1.0) == ProposalCode.Accepted, "P2 opens (quick)");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, 0, 1.2) == ProposalCode.Accepted, "P2 closes");
        Expect(Txn(wh, 1, ObjectOp.Set, D, X, ObjectStateRules.DoorOpen | ObjectStateRules.DoorSlow, 1.4) == ProposalCode.Accepted && St(wh.Reg, Madison, D, X).Ver == 3, "host opens slowly (slow mark kept for the replay)");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, 0, 1.6, Madison, 1) == ProposalCode.Stale && St(wh.Reg, Madison, D, X).State == 3, "old base: stale, never reverses");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, 1, 1.8, Madison, 2) == ProposalCode.Unchanged, "already open (old base) -> Unchanged");
        Expect(Txn(wh, 1, ObjectOp.Set, D, X, ObjectStateRules.DoorOpen | ObjectStateRules.DoorBroken, 2.0) == ProposalCode.Accepted, "crowbar break-in: broken hinge");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, 0, 2.2) == ProposalCode.Invalid && (St(wh.Reg, Madison, D, X).State & 4) != 0, "a broken hinge cannot be closed");
        Expect(!ObjectStateRules.ValidState(ObjectKind.Door, ObjectStateRules.DoorBroken), "broken implies open");
        // replica decisions
        Expect(WorldDoorRules.Decide(5, false, false, false, false, true, false) == DoorApply.Break, "broken -> Break");
        Expect(WorldDoorRules.Decide(5, true, true, false, false, true, false) == DoorApply.None, "already broken");
        Expect(WorldDoorRules.Decide(0, true, true, false, false, true, false) == DoorApply.Diverged, "cannot repair here");
        Expect(WorldDoorRules.Decide(1, false, false, false, false, false, false) == DoorApply.SkipLocked, "locked here -> waits for the lock");
        Expect(WorldDoorRules.Decide(1, false, false, true, false, true, false) == DoorApply.SkipBusy && WorldDoorRules.Decide(1, false, false, false, true, true, false) == DoorApply.SkipBusy, "moving / held -> later");
        Expect(WorldDoorRules.Decide(0, true, false, false, false, true, true) == DoorApply.SkipPending, "own proposal pending");
        Expect(WorldDoorRules.Decide(1, false, false, false, true, false) == DoorApply.Swing, "Phase A signature kept");
    }

    // ---------------------------------------------------------------- 05
    static WorldHost Ready2(out SimScene p1, out SimScene p2)
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, Digest, 0);
        p1 = NewScene(10, 10, 10, 1, 0); p2 = NewScene(10, 10, 10, 2, 0);
        return wh;
    }

    static void LockModes()
    {
        SimScene p1, p2; var wh = Ready2(out p1, out p2);
        uint[] simple = { 0x5000 }, medium = { 0x5001 }, advanced = { 0x5002 };
        foreach (var lk in new[] { simple[0], medium[0], advanced[0] })
        {
            // the minigame difficulty only changes what runs locally; the transaction is the same for all three
            Expect(Txn(wh, 2, ObjectOp.Reserve, L, lk, 0, 1) == ProposalCode.Accepted && wh.Objects.ReservedBy(ObjectKind.Lockpick, Madison, lk) == 2, "reserve " + lk.ToString("X"));
            Expect(Txn(wh, 2, ObjectOp.Set, L, lk, ObjectStateRules.LockUnlocked, 2) == ProposalCode.Accepted, "commit after the original success");
            Expect(wh.Objects.ReservedBy(ObjectKind.Lockpick, Madison, lk) == 0, "reservation consumed by the commit");
        }
        p1.Reconcile(wh.Reg, Madison);
        Expect(p1.Locks[0x5000] == 1 && p1.Locks[0x5001] == 1 && p1.Locks[0x5002] == 1 && p1.SideEffects == 0, "host replica unlocked all three without side effects");
        // key (instant, no minigame) and padlock (crowbar hit, instant)
        Expect(Txn(wh, 1, ObjectOp.Set, L, 0x5003, ObjectStateRules.LockKey, 3) == ProposalCode.Accepted, "key unlock");
        Expect(Txn(wh, 2, ObjectOp.Set, L, 0x5004, ObjectStateRules.LockPadlock, 3.2) == ProposalCode.Accepted, "padlock broken");
        Expect(Txn(wh, 1, ObjectOp.Set, L, 0x5004, 0, 3.4) == ProposalCode.Invalid, "a broken padlock stays broken");
        // crowbar break-in: reservation, unlock + broken door
        Expect(Txn(wh, 1, ObjectOp.Reserve, L, 0x5005, 0, 4) == ProposalCode.Accepted, "crowbar reserve");
        Expect(Txn(wh, 1, ObjectOp.Set, L, 0x5005, 1, 4.5) == ProposalCode.Accepted && Txn(wh, 1, ObjectOp.Set, D, 0x1005, 5, 4.5) == ProposalCode.Accepted, "crowbar: lock unlocked + door forced open");
        // player relock (relock_door button -> Door.ResetLock -> Lockpick.RelockDoor)
        Expect(Txn(wh, 2, ObjectOp.Set, L, 0x5003, 0, 5) == ProposalCode.Accepted && St(wh.Reg, Madison, L, 0x5003).State == 0, "player relock");
        Expect(WorldLockRules.Decide(0, 2, false, false) == LockApply.Relock && WorldLockRules.Decide(1, 0, false, false) == LockApply.Unlock && WorldLockRules.Decide(1, 2, false, false) == LockApply.None, "lock replica decisions");
        Expect(WorldLockRules.Decide(1, 0, false, true) == LockApply.SkipBusy, "remote result waits while the local player is in that minigame");
    }

    // ---------------------------------------------------------------- 06
    static void SingleCommit()
    {
        SimScene p1, p2; var wh = Ready2(out p1, out p2);
        const uint Lk = 0x5010;
        Txn(wh, 2, ObjectOp.Reserve, L, Lk, 0, 1);
        long before = wh.Objects.Accepted;
        Expect(Txn(wh, 2, ObjectOp.Set, L, Lk, 1, 2, Madison, 0) == ProposalCode.Accepted, "first callback commits");
        // AutoLockpick_Manager.Win_Timed calls DoorUnlock once per door of unlockDoor[] (verified IL): duplicates are Unchanged
        for (int i = 0; i < 3; i++) Expect(Txn(wh, 2, ObjectOp.Set, L, Lk, 1, 2.01, Madison, 0) == ProposalCode.Unchanged, "duplicate callback " + i);
        Expect(wh.Objects.Accepted - before == 1 && St(wh.Reg, Madison, L, Lk).Ver == 1, "exactly one commit, version 1");
        var sync = Src("WorldObjectSync.cs");
        Expect(sync.Contains("// repeated callback (auto lockpick per door)"), "plugin drops repeated callbacks before proposing");
    }

    // ---------------------------------------------------------------- 07
    static void Rejection()
    {
        SimScene p1, p2; var wh = Ready2(out p1, out p2);
        const uint Lk = 0x5020;
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, Lk, 0, 1) == ProposalCode.Accepted, "P2 reserves");
        Expect(Txn(wh, 1, ObjectOp.Reserve, L, Lk, 0, 1.1) == ProposalCode.Busy, "host's lockpicking press does nothing while P2 picks");
        Expect(Txn(wh, 1, ObjectOp.Set, L, Lk, 1, 1.2) == ProposalCode.Busy && St(wh.Reg, Madison, L, Lk).Ver == 0, "a minigame result of the other player is refused (no state)");
        // failure / cancellation: the minigame ends without DoorUnlock -> release
        Expect(Txn(wh, 2, ObjectOp.Release, L, Lk, 0, 2) == ProposalCode.Accepted && wh.Objects.ReservedBy(ObjectKind.Lockpick, Madison, Lk) == 0, "released after cancel");
        Expect(Txn(wh, 2, ObjectOp.Release, L, Lk, 0, 2.1) == ProposalCode.Unchanged, "double release harmless");
        Expect(Txn(wh, 1, ObjectOp.Reserve, L, Lk, 0, 2.2) == ProposalCode.Accepted, "host may pick now");
        // expiry
        wh.Objects.Tick(2.2 + ObjectStateRules.ReserveSeconds + 1, wh.Reg, wh.Outbox);
        Expect(wh.Objects.ReservedBy(ObjectKind.Lockpick, Madison, Lk) == 0 && wh.Objects.Expired == 1, "reservation expires");
        // a reservation of an already unlocked lock is refused (Stale): the client reconciles instead of starting a minigame
        Txn(wh, 1, ObjectOp.Set, L, 0x5021, 1, 200);
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, 0x5021, 0, 200.1) == ProposalCode.Stale, "reserve of an unlocked lock -> Stale");
        // doors / windows: only locks and windows are reservable
        Expect(Txn(wh, 2, ObjectOp.Reserve, D, 0x1000, 0, 201) == ProposalCode.Invalid, "doors are not reservable");
        // refused local change -> plugin re-applies the committed state (marked dirty)
        Expect(Src("WorldObjectSync.cs").Contains("_dirty.Add(key); _retryAt = Now + 0.1;"), "refused proposal restores the committed state");
    }

    // ---------------------------------------------------------------- 08
    static void SameLock()
    {
        SimScene p1, p2; var wh = Ready2(out p1, out p2);
        const uint Lk = 0x5030;
        var a = Txn(wh, 1, ObjectOp.Reserve, L, Lk, 0, 1); var b = Txn(wh, 2, ObjectOp.Reserve, L, Lk, 0, 1);
        Expect(a == ProposalCode.Accepted && b == ProposalCode.Busy, "exactly one reservation (" + a + "/" + b + ")");
        // the other player uses the key meanwhile: instant original action, cancels the reservation
        Expect(Txn(wh, 2, ObjectOp.Set, L, Lk, ObjectStateRules.LockKey, 2) == ProposalCode.Accepted && wh.Objects.ReservedBy(ObjectKind.Lockpick, Madison, Lk) == 0, "key overrides the reservation");
        Expect(Txn(wh, 1, ObjectOp.Set, L, Lk, ObjectStateRules.LockUnlocked, 3, Madison, 0) == ProposalCode.Unchanged, "the picker's later success = same physical state -> Unchanged");
        // simultaneous sets on a free lock: first committed wins
        Expect(Txn(wh, 1, ObjectOp.Set, L, 0x5031, 1, 4, Madison, 0) == ProposalCode.Accepted && Txn(wh, 2, ObjectOp.Set, L, 0x5031, ObjectStateRules.LockPadlock, 4, Madison, 0) == ProposalCode.Unchanged, "first commit wins");
        Expect(St(wh.Reg, Madison, L, 0x5031).By == 1, "host's state stays");
    }

    // ---------------------------------------------------------------- 09
    static void Windows()
    {
        SimScene p1, p2; var wh = Ready2(out p1, out p2);
        const uint Wi = 0x8000;
        Expect(!ObjectStateRules.ValidState(ObjectKind.LockedWindow, ObjectStateRules.SashOpen), "a locked window has no movable sash");
        Expect(Txn(wh, 2, ObjectOp.Reserve, W, Wi, 0, 1) == ProposalCode.Accepted && Txn(wh, 2, ObjectOp.Set, W, Wi, ObjectStateRules.GlassCut, 2) == ProposalCode.Accepted, "glass cutting: reserve + cut");
        // the sash is the window's Door: open under the SAME id (bits 2-4)
        byte open = WorldDoorRules.WithSash(ObjectStateRules.GlassCut, ObjectStateRules.DoorOpen);
        Expect(Txn(wh, 2, ObjectOp.Set, W, Wi, open, 3) == ProposalCode.Accepted && St(wh.Reg, Madison, W, Wi).State == open, "sash open under the window id");
        Expect(WorldDoorRules.SashAsDoor(open) == ObjectStateRules.DoorOpen, "sash bits as door bits");
        Expect(wh.Objects.CountOf(ObjectKind.Door, Madison) == 0, "no separate door object for the window");
        Expect(Txn(wh, 1, ObjectOp.Set, W, Wi, ObjectStateRules.GlassIntact, 4) == ProposalCode.Invalid, "an opened window cannot be locked again");
        Expect(Txn(wh, 1, ObjectOp.Set, W, Wi, (byte)(ObjectStateRules.GlassBroken | ObjectStateRules.SashOpen), 4.1) == ProposalCode.Unchanged, "cut vs broken glass: same physical state");
        // unlock from inside (instant) and broken glass (brick / crowbar, instant)
        Expect(Txn(wh, 1, ObjectOp.Set, W, 0x8001, ObjectStateRules.GlassInside, 5) == ProposalCode.Accepted, "unlock from inside");
        Expect(Txn(wh, 2, ObjectOp.Reserve, W, 0x8002, 0, 6) == ProposalCode.Accepted, "P2 starts cutting");
        Expect(Txn(wh, 1, ObjectOp.Set, W, 0x8002, ObjectStateRules.GlassBroken, 6.5) == ProposalCode.Accepted, "a thrown brick still breaks it (instant)");
        Expect(Txn(wh, 2, ObjectOp.Set, W, 0x8002, ObjectStateRules.GlassCut, 7, Madison, 0) == ProposalCode.Unchanged, "the cutter's later result -> Unchanged");
        // crowbar on a window: cut + broken sash
        Expect(Txn(wh, 1, ObjectOp.Set, W, 0x8003, ObjectStateRules.GlassCut, 8) == ProposalCode.Accepted && Txn(wh, 1, ObjectOp.Set, W, 0x8003, (byte)(ObjectStateRules.GlassCut | ObjectStateRules.SashOpen | ObjectStateRules.SashBroken), 8.1) == ProposalCode.Accepted, "crowbar window");
        // replica decisions
        Expect(WorldWindowRules.Decide(2, 0, true, false, false) == WindowApply.Glass, "glass applied while the component lives");
        Expect(WorldWindowRules.Decide(2, 0, false, false, false) == WindowApply.Diverged, "component gone without a local state -> diverged (logged)");
        Expect(WorldWindowRules.Decide(3, 2, false, false, false) == WindowApply.None, "already open here");
        // identity: captured before Destroy, sash mapped to the window id
        var sync = Src("WorldObjectSync.cs"); var census = Src("WorldCensus.cs");
        Expect(sync.Contains("static bool GlassPre(object __instance, ref long __state)") && sync.Contains("// id read before the original destroys the LockedWindow component"), "window id captured in the prefix");
        Expect(census.Contains("_windowDoor[r.Comp.GetInstanceID()] = wid;") && sync.Contains("if (census.TryWindowOfDoor(door, out id) && id.Namespace == _region)"), "sash Door -> window id");
        p2.Reconcile(wh.Reg, Madison);
        Expect(p2.Windows[Wi] == open && p2.Windows[0x8001] == ObjectStateRules.GlassInside, "replica windows");
    }

    // ---------------------------------------------------------------- 10
    static void SideEffects()
    {
        var bridge = Src("WorldObjectBridge.cs");
        var code = StripComments(bridge);
        foreach (var bad in new[] { "DoorUnlock", "DoorUnlockKey", "\"BreakPadlock\"", "\"UnlockWindow", "\"BrokeWindow\"", "CheckAlarm", "StartPoliceChase", "AlertNPC", "MakeNoise", "Mission_Complete", "AdvanceStory",
                                   "\"Use\"", "SlowFailed", "\"ResetLock\"", "item_amount", "crowbarLife", "bltHealth", "openedLocks", "SpawnWarning", "isSeenByNPC", "Police" })
            Expect(code.IndexOf(bad, StringComparison.Ordinal) < 0, "bridge never touches " + bad);
        Expect(code.Contains("_lpSetAlarm(lp, false);") && code.IndexOf("_lpSetAlarm(lp, false);", StringComparison.Ordinal) < code.IndexOf("lp.transform.tag = \"Untagged\";", StringComparison.Ordinal), "hasAlarm cleared before the lock is retagged (no police chase on the replica)");
        Expect(code.Contains("_lwSetAlarm(lw, false);"), "window alarm cleared on the replica");
        foreach (var ok in new[] { "\"OpenOnStartt\"", "\"Resett\"", "\"OpenDestroyed\"", "\"RelockDoor\"" }) Expect(code.Contains(ok), "bridge uses the side-effect-free original " + ok);
        var sync = StripComments(Src("WorldObjectSync.cs"));
        // the sync applies replicas only through the bridge and never calls gameplay originals itself
        foreach (var bad in new[] { "StartPoliceChase", "MakeNoise", "AdvanceStory", "Mission_Complete", "AlertNPC", "item_amount", "SetActive(", "Destroy(" })
            Expect(sync.IndexOf(bad, StringComparison.Ordinal) < 0, "sync never calls " + bad);
        Expect(sync.Contains("if (i == null || WorldObjectBridge.Applying) return;"), "hooks ignore the bridge's own calls (no echo)");
        // Player 2 story/mission block in every prefix that can start progression
        foreach (var pre in new[] { "StartPickPre", "BreakinPre", "UnlockPre", "KeyPre", "PadlockPre", "BeginCutPre", "InsidePre", "GlassPre", "BrokePre" })
        {
            int i = sync.IndexOf("static bool " + pre, StringComparison.Ordinal); Expect(i >= 0, pre + " exists");
            int j = sync.IndexOf("static ", i + 10, StringComparison.Ordinal);
            Expect(sync.Substring(i, (j > 0 ? j : sync.Length) - i).Contains("StoryBlocked("), pre + " blocks story/mission objects for Player 2");
        }
        Expect(sync.Contains("if (_host || !_ok) return false;"), "Player 1 keeps the original story behaviour");
        // Harmony runs postfixes even when a prefix skipped the original: a blocked / suppressed call never publishes a change
        foreach (var post in new[] { "UnlockPost", "KeyPost", "PadlockPost" }) { int i = sync.IndexOf("static void " + post, StringComparison.Ordinal); Expect(i >= 0 && sync.Substring(i, 160).Contains("!__state"), post + " only after the original really ran"); }
        Expect(sync.Contains("if (i == null || __state == 2 || WorldObjectBridge.Applying) return;"), "suppressed NPC relock is not published");
        var bridgeStory = bridge.Substring(bridge.IndexOf("public bool StoryCritical", StringComparison.Ordinal), 600);
        Expect(bridgeStory.Contains("_lpStory(c) || !string.IsNullOrEmpty(_lpStoryName(c)) || _lpTut(c)") && bridgeStory.Contains("if (c == null) return true;"), "story classification: flag, story name or tutorial; fail closed");
        Expect(Src("WorldCensus.cs").Contains("// Fail closed: when the metadata cannot be read the object counts as story-critical."), "census metadata fails closed");
    }

    // ---------------------------------------------------------------- 11
    static void P2Alone()
    {
        var wh = Host(Hideout, Madison);
        Expect(Scene(wh, 2, Madison, 3, Digest, 1) == ProposalCode.Accepted, "P2's scene starts the epoch");
        var r = Rec(wh.Reg, Madison);
        Expect(r.InitBy == 2 && r.Owner == 2 && !r.IsMember(1), "P2 = initializer + simulation source; host not a member: " + r);
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, 0x5000, 0, 1.2) == ProposalCode.Busy, "nothing before the initial state");
        Expect(Init(wh, 2, Madison, new List<ObjectInitEntry> { new ObjectInitEntry { Kind = D, Hash = 0x1001, State = 1 } }, 1.5) == ProposalCode.Accepted, "P2 initializes");
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, 0x5000, 0, 2) == ProposalCode.Accepted && Txn(wh, 2, ObjectOp.Set, L, 0x5000, 1, 3) == ProposalCode.Accepted, "P2 picks a lock with the host at the hideout");
        Expect(wh.Objects.Unverified > 0, "accepted on P2's scene (host never loaded Madison): unverified, counted");
        Expect(Txn(wh, 1, ObjectOp.Set, D, 0x1001, 0, 3.5) == ProposalCode.NotAuthorized, "the host cannot change Madison from the hideout");
    }

    // ---------------------------------------------------------------- 12
    static void HostEntersLater()
    {
        var wh = Host(Hideout, Madison);
        Scene(wh, 2, Madison, 3, Digest, 1);
        var p2 = NewScene(50, 5, 5, 3, 30);
        Init(wh, 2, Madison, p2.NonDefault(), 1.5);
        Txn(wh, 2, ObjectOp.Reserve, L, 0x5001, 0, 2); Txn(wh, 2, ObjectOp.Set, L, 0x5001, 1, 3); p2.Locks[0x5001] = 1;
        Txn(wh, 2, ObjectOp.Set, W, 0x8001, ObjectStateRules.GlassBroken, 3.2); p2.Windows[0x8001] = ObjectStateRules.GlassBroken;
        // host travels to Madison: fresh scene of its own, then reports
        wh.SetPlayerRegion(1, Madison, 10);
        Expect(Scene(wh, 1, Madison, 44, Digest, 10.6) == ProposalCode.Accepted, "host joins the running epoch");
        var r = Rec(wh.Reg, Madison);
        Expect(r.IsPresent(1) && r.IsPresent(2) && r.InitBy == 2 && r.Owner == 1, "host now owner, P2 stays initializer of the epoch: " + r);
        var host = NewScene(50, 5, 5, 99, 30);
        host.Reconcile(wh.Reg, Madison);
        foreach (var k in p2.Doors.Keys) Expect(host.Doors[k] == p2.Doors[k], "door state");
        Expect(host.Locks[0x5001] == 1 && host.Windows[0x8001] == ObjectStateRules.GlassBroken, "the host sees P2's unlocked lock and broken window");
    }

    // ---------------------------------------------------------------- 13
    static void Ownership()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 2, Madison, 2, Digest, 0); Init(wh, 2, Madison, new List<ObjectInitEntry> { new ObjectInitEntry { Kind = L, Hash = 0x5000, State = 1 } }, 0); Scene(wh, 1, Madison, 1, Digest, 0);
        Expect(Rec(wh.Reg, Madison).Owner == 1, "both loaded: the host (lowest slot) owns NPC transitions");
        Expect(Txn(wh, 2, ObjectOp.NpcSet, L, 0x5000, 0, 1) == ProposalCode.NotAuthorized, "P2's NPC relock refused (non-owner)");
        Expect(Txn(wh, 1, ObjectOp.NpcSet, L, 0x5000, 0, 1.1) == ProposalCode.Accepted, "owner's NPC relock committed");
        Expect(Txn(wh, 1, ObjectOp.NpcSet, L, 0x5000, 1, 1.2) == ProposalCode.Invalid, "NPCs never unlock");
        // host leaves: after its region change it is away -> P2 owns immediately (lowest PRESENT member)
        wh.SetPlayerRegion(1, Hideout, 2);
        Expect(Rec(wh.Reg, Madison).Owner == 2, "transfer to P2 on the host's departure");
        Txn(wh, 2, ObjectOp.Set, L, 0x5000, 1, 3);
        Expect(Txn(wh, 2, ObjectOp.NpcSet, L, 0x5000, 0, 3.1) == ProposalCode.Accepted, "P2 now publishes NPC relocks");
        var sync = Src("WorldObjectSync.cs");
        Expect(sync.Contains("if (_rec.Owner == _world.LocalSlot) return true;") && sync.Contains("_npcSuppressed++;"), "non-owner instance suppresses its own NPC relock (owner's is replicated)");
        Expect(sync.Contains("NpcPre") && sync.Contains("AI.OnTriggerEnter"), "NPC context from the only NPC caller of Door.ResetLock (AI.OnTriggerEnter)");
    }

    // ---------------------------------------------------------------- 14
    static void Lifetime()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, Digest, 0);
        Txn(wh, 2, ObjectOp.Set, D, 0x1000, 1, 1);
        ushort epoch = wh.Objects.EpochOf(Madison);
        // a brief probe gap / out-of-order region notification: P2 None for a moment, back in the same scene instance
        wh.SetPlayerRegion(2, Regions.None, 2); wh.SetPlayerRegion(1, Hideout, 2);   // both "gone" for an instant
        wh.Tick(3);
        wh.SetPlayerRegion(2, Madison, 3.5);
        Expect(Scene(wh, 2, Madison, 2, Digest, 3.6) == ProposalCode.Accepted, "back in the same scene instance within the grace period");
        Expect(wh.Objects.EpochOf(Madison) == epoch && St(wh.Reg, Madison, D, 0x1000).State == 1, "state kept (no accidental reset)");
        wh.Tick(10);
        Expect(wh.Objects.EpochOf(Madison) == epoch && Rec(wh.Reg, Madison).Members == (1 << 2), "the host's grace expired, P2 keeps the region alive alone");
        // P2 reloads Madison (new scene instance) while alone: the original reload rebuilds everything -> new epoch, reset
        Expect(Scene(wh, 2, Madison, 9, Digest, 11) == ProposalCode.Accepted && wh.Objects.EpochOf(Madison) == epoch + 1 && St(wh.Reg, Madison, D, 0x1000).Ver == 0, "alone reload = vanilla reset (new epoch)");
        Init(wh, 2, Madison, new List<ObjectInitEntry>(), 11);
        Txn(wh, 2, ObjectOp.Set, D, 0x1000, 1, 12);
        // the last member leaves: after the grace period every state is cleared
        wh.SetPlayerRegion(2, Hideout, 13);
        wh.Tick(13 + ObjectStateRules.MemberGraceSeconds - 1);
        Expect(St(wh.Reg, Madison, D, 0x1000).State == 1, "still inside the grace period");
        wh.Tick(13 + ObjectStateRules.MemberGraceSeconds + 0.1);
        Expect(St(wh.Reg, Madison, D, 0x1000).Ver == 0 && !Rec(wh.Reg, Madison).Active && wh.Objects.Cleared > 0, "last member gone -> vanilla reset");
        // there is no campaign persistence for these transient states
        Expect(ObjectStateRules.Lifetime(ObjectKind.Door) == ObjectLifetime.RegionLife && ObjectStateRules.Lifetime(ObjectKind.Lockpick) == ObjectLifetime.RegionLife && ObjectStateRules.Lifetime(ObjectKind.LockedWindow) == ObjectLifetime.RegionLife, "region lifetime only");
        Expect(typeof(RegionStateBook).GetMethod("SerializeCampaign") == null, "no campaign serialiser for region objects");
    }

    // ---------------------------------------------------------------- 15
    static void Reconnect()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, Digest, 0);
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, 0x5000, 0, 1) == ProposalCode.Accepted, "P2 in a minigame");
        wh.PlayerDisconnected(2, 2);
        Expect(wh.Objects.ReservedBy(ObjectKind.Lockpick, Madison, 0x5000) == 0 && !Rec(wh.Reg, Madison).IsMember(2), "disconnect: reservation and membership gone at once");
        Expect(Txn(wh, 1, ObjectOp.Reserve, L, 0x5000, 0, 2.5) == ProposalCode.Accepted, "host may pick the lock");
        Txn(wh, 1, ObjectOp.Set, L, 0x5000, 1, 3);
        // P2 reconnects (its old minigame result, if any, arrives with an old epoch or as non-member -> refused)
        Expect(Txn(wh, 2, ObjectOp.Set, L, 0x5000, 1, 3.5) == ProposalCode.NotAuthorized, "stale result from before the reconnect refused (not a region member)");
        wh.SetPlayerRegion(2, Madison, 4);
        Expect(Scene(wh, 2, Madison, 3, Digest, 4.5) == ProposalCode.Accepted && Rec(wh.Reg, Madison).IsPresent(2), "rejoin reports the scene");
        var p2 = NewScene(3, 3, 3, 4, 0); p2.Reconcile(ClientCopy(wh), Madison);
        Expect(p2.Locks[0x5000] == 1, "rejoined P2 sees the committed lock");
        // initializer disconnects before finishing: the next present member takes over, partial states cleared
        var w2 = Host(Madison, Madison);
        Scene(w2, 2, Madison, 1, Digest, 0); Scene(w2, 1, Madison, 1, Digest, 0);
        var big = new List<ObjectInitEntry>(); for (uint i = 0; i < 300; i++) big.Add(new ObjectInitEntry { Kind = D, Hash = i + 1, State = 1 });
        var ch = ObjectInitChunk.Split(Madison, w2.Objects.EpochOf(Madison), big);
        Propose(w2, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectInit, Value = ch[0].ToBytes() }, 0.5);
        w2.PlayerDisconnected(2, 1);
        var r = Rec(w2.Reg, Madison);
        Expect(r.InitBy == 1 && !r.Initialized && w2.Objects.CountOf(ObjectKind.Door, Madison) == 0, "initialization handed to the host, partial chunk discarded: " + r);
        Expect(Init(w2, 1, Madison, new List<ObjectInitEntry>(), 1.5) == ProposalCode.Accepted && Rec(w2.Reg, Madison).Initialized, "host completes the initialization");
    }

    // ---------------------------------------------------------------- 16
    static void Validation()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0);
        Expect(Scene(wh, 2, Madison, 2, Digest ^ 1, 0) == ProposalCode.Invalid, "different scene metadata (digest) refused");
        Expect(Scene(wh, 2, Hillville, 2, Digest, 0) == ProposalCode.NotAuthorized, "unsupported region / not the requester's region");
        Expect(Scene(wh, 2, Madison, 2, Digest, 0) == ProposalCode.Accepted, "matching digest accepted");
        Expect(Init(wh, 2, Madison, new List<ObjectInitEntry>(), 0.2) == ProposalCode.NotAuthorized, "only the initializer sends the initial state");
        Expect(Txn(wh, 2, ObjectOp.Set, D, 1, 1, 0.3) == ProposalCode.Busy, "transitions wait for the initial state");
        Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0.4);
        Expect(Txn(wh, 2, ObjectOp.Set, D, 1, 1, 1, Madison, -1, 99) == ProposalCode.Stale, "wrong epoch -> Stale");
        Expect(Txn(wh, 2, ObjectOp.Set, D, 1, 9, 1.1) == ProposalCode.Invalid, "invalid door state");
        Expect(Txn(wh, 2, ObjectOp.Set, (byte)ObjectKind.Safe, 1, 1, 1.2) == ProposalCode.NotAuthorized, "kind not shared");
        // catalog: strict once the host has a census, restricted objects refused for Player 2 only
        wh.ObjectCatalog = (k, ns, h) => h < 0x100;
        wh.Objects.Restricted = (k, ns, h) => k == ObjectKind.Lockpick && h == 0x42;
        Expect(Txn(wh, 2, ObjectOp.Set, D, 0x200, 1, 2) == ProposalCode.Invalid, "unknown id refused with a host catalog");
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, 0x42, 0, 2.1) == ProposalCode.NotAuthorized, "story lock refused for Player 2 (host census metadata)");
        Expect(Txn(wh, 1, ObjectOp.Reserve, L, 0x42, 0, 2.2) == ProposalCode.Accepted, "Player 1 keeps the original story lock");
        // unverified ids are pruned once the host censuses the region
        var w2 = Host(Hideout, Madison);
        Scene(w2, 2, Madison, 1, Digest, 0); Init(w2, 2, Madison, new List<ObjectInitEntry> { new ObjectInitEntry { Kind = D, Hash = 0x10, State = 1 }, new ObjectInitEntry { Kind = D, Hash = 0x999, State = 1 } }, 0);
        Expect(w2.Objects.CountOf(ObjectKind.Door, Madison) == 2 && w2.Objects.Unverified == 2, "accepted unverified");
        w2.ObjectCatalog = (k, ns, h) => h == 0x10;
        Expect(w2.Objects.PruneUnknown(Madison, Digest, w2.Reg, w2.Outbox) == 1 && w2.Objects.CountOf(ObjectKind.Door, Madison) == 1, "unknown id pruned after the host census");
        // rate limit
        var w3 = Host(Madison); Scene(w3, 1, Madison, 1, Digest, 0); Init(w3, 1, Madison, new List<ObjectInitEntry>(), 0);
        int busy = 0; for (uint i = 0; i < 12; i++) if (Txn(w3, 1, ObjectOp.Set, D, 0x50 + i, 1, 5.0) == ProposalCode.Busy) busy++;
        Expect(busy == 12 - ObjectStateRules.RatePerSecond, "rate limit " + busy);
        // non-member / unsupported namespace
        var w4 = Host(Madison, Madison); Scene(w4, 1, Madison, 1, Digest, 0); Init(w4, 1, Madison, new List<ObjectInitEntry>(), 0);
        Expect(Txn(w4, 2, ObjectOp.Set, D, 1, 1, 1) == ProposalCode.NotAuthorized, "in the region but no scene reported -> not a member");
    }

    // ---------------------------------------------------------------- 17
    static void CodecsAndScans()
    {
        Expect(ProtocolInfo.ProtocolVersion == 25 && ProtocolInfo.ModVersion == "0.14.1", "protocol 25 / mod 0.14.1");
        var t = new ObjectTxnRequest { Txn = 5, Op = ObjectOp.Reserve, Kind = L, Ns = Madison, Hash = 7, State = 1, BaseVer = 3, Epoch = 9 }; ObjectTxnRequest t2;
        Expect(t.ToBytes().Length == ObjectTxnRequest.Bytes && ObjectTxnRequest.TryParse(t.ToBytes(), out t2) && t2.Op == ObjectOp.Reserve && t2.Epoch == 9 && t2.BaseVer == 3, "txn 19 B");
        var bad = t.ToBytes(); bad[4] = 9; Expect(!ObjectTxnRequest.TryParse(bad, out t2), "unknown op refused");
        var sr = new ObjectSceneReport { Ns = Madison, Serial = -5, Digest = Digest, Count = 1402 }; ObjectSceneReport sr2;
        Expect(sr.ToBytes().Length == ObjectSceneReport.Bytes && ObjectSceneReport.TryParse(sr.ToBytes(), out sr2) && sr2.Serial == -5 && sr2.Digest == Digest, "scene report 14 B");
        var rec = new RegionObjectRecord { Epoch = 3, Active = true, Initialized = true, InitBy = 2, Owner = 1, Members = 6, Away = 4 }; RegionObjectRecord rec2;
        Expect(RegionObjectRecord.TryParse(rec.ToBytes(), out rec2) && rec2.Epoch == 3 && rec2.IsPresent(1) && !rec2.IsPresent(2) && rec2.IsMember(2), "region record 8 B");
        var page = new ObjectStateList(); for (uint i = 0; i < 100; i++) page.Items.Add(new ObjectState { Hash = i, State = 1, Ver = 2, By = 2 });
        ObjectStateList p2; Expect(page.ToBytes().Length == 804 && ObjectStateList.TryParse(page.ToBytes(), out p2), "page 100 = 804 B");
        page.Items.Add(new ObjectState()); Expect(!ObjectStateList.TryParse(page.ToBytes(), out p2), "page over 100 refused");
        ObjectKind k; int pg;
        Expect(ObjectStateRules.TryKindOfKey("wobj2", out k, out pg) && k == ObjectKind.Door && pg == 0 && ObjectStateRules.TryKindOfKey("wobj3.5", out k, out pg) && k == ObjectKind.Lockpick && pg == 5 && !ObjectStateRules.TryKindOfKey("wobj2.12", out k, out pg) && !ObjectStateRules.TryKindOfKey("wloot0", out k), "registry keys");
        var rnd = new Random(3); ObjectInitChunk c; RegionObjectRecord rr; ObjectSceneReport ss;
        for (int i = 0; i < 4000; i++) { var b = new byte[rnd.Next(0, 60)]; rnd.NextBytes(b); ObjectTxnRequest.TryParse(b, out t2); ObjectInitChunk.TryParse(b, out c); RegionObjectRecord.TryParse(b, out rr); ObjectSceneReport.TryParse(b, out ss); ObjectStateList.TryParse(b, out p2); }
        Expect(ObjectInitChunk.Split(Madison, 1, new List<ObjectInitEntry>(new ObjectInitEntry[ObjectInitChunk.MaxEntries * ObjectInitChunk.MaxChunks + 1])) == null, "initial state above 3200 entries refused (region stays local)");
        // save protection: the new code never writes save data; no per-frame scene scans; no mod text in the game UI
        foreach (var f in new[] { "WorldObjectSync.cs", "WorldObjectBridge.cs" })
        {
            var src = StripComments(Src(f));
            foreach (var x in new[] { "SPrefs", "FBPP", "PlayerPrefs", "File.", "FindObjectsOfType", "FindObjectOfType", "GameObject.Find", "GetComponentsInChildren", "FindGameObjectsWithTag", "GetRootGameObjects", "OnGUI", "GUI.", "SpawnWarning", "UI.Text" })
                Expect(src.IndexOf(x, StringComparison.Ordinal) < 0, f + " has no " + x);
        }
        var sync = Src("WorldObjectSync.cs");
        Expect(sync.Contains("if (region == Regions.None || !ObjectStateRules.SupportedNamespace(region)) return;   // no work elsewhere") && sync.Contains("_watch.Count < 4"), "bounded per-frame work");
        Expect(!File.Exists(Path.Combine(PluginDir(), "WorldDoorSync.cs")), "Phase A WorldDoorSync replaced");
    }

    // ---------------------------------------------------------------- 18-22 (W4.2B.1)
    // A replica door with its lock tag, applied in the plugin's order (Kinds: Lockpick, LockedWindow, Door).
    sealed class RepDoor { public bool Open, Broken, Locked, Enabled = true; public byte Lock; }
    static void ApplyReplica(StateRegistry reg, RepDoor d, uint door, uint lk)
    {
        byte wantLock = St(reg, Madison, L, lk).State;
        var la = WorldLockRules.Decide(wantLock, d.Lock, false, false);
        if (la == LockApply.Unlock) { d.Lock = wantLock; d.Locked = false; d.Enabled = true; }          // Lockpick.DoorUnlock part: enabled + tag Door
        else if (la == LockApply.Relock) { d.Lock = 0; d.Locked = true; d.Enabled = true; }              // Lockpick.RelockDoor: tag Lock1 (door not closed)
        byte want = St(reg, Madison, D, door).State;
        var a = WorldDoorRules.Decide(want, d.Open, d.Broken, false, false, !d.Locked, false);
        if (a == DoorApply.Swing) d.Open = (want & 1) != 0;
        else if (a == DoorApply.Break) { d.Enabled = true; d.Open = true; d.Broken = true; }
    }

    static void RelockUnlock()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, Digest, 0);
        const uint X = 0xD1, LK = 0x51;
        var rep = new RepDoor { Locked = true };
        // P2 picks the lock and opens the door; P1's replica follows
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, LK, 0, 1.0) == ProposalCode.Accepted && Txn(wh, 2, ObjectOp.Set, L, LK, 1, 1.5) == ProposalCode.Accepted, "P2 unlocks");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, 1, 2.0) == ProposalCode.Accepted, "P2 opens");
        ApplyReplica(wh.Reg, rep, X, LK); Expect(rep.Open && !rep.Locked, "replica: unlocked + open");
        // NPC / Door.ResetLock relock of an OPEN door: lock -> 0 and door -> closed, published together; the replica applies the lock first
        Expect(Txn(wh, 1, ObjectOp.Set, L, LK, 0, 3.0) == ProposalCode.Accepted && Txn(wh, 1, ObjectOp.Set, D, X, 0, 3.0) == ProposalCode.Accepted, "relock + close committed");
        ApplyReplica(wh.Reg, rep, X, LK);
        Expect(rep.Locked && !rep.Open, "replica: relocked AND closed (before W4.2B.1 the close waited for the lock tag -> door stuck open)");
        Expect(WorldDoorRules.Decide(0, true, false, false, false, false, false) == DoorApply.Swing, "closing never waits for the lock tag");
        Expect(WorldDoorRules.Decide(1, false, false, false, false, false, false) == DoorApply.SkipLocked, "opening still waits for this instance's lock state");
        // the other player unlocks it again and opens it: both instances end open + unlocked, no divergence
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, LK, 0, 4.0) == ProposalCode.Accepted && Txn(wh, 2, ObjectOp.Set, L, LK, 1, 4.5) == ProposalCode.Accepted, "unlocked again");
        ApplyReplica(wh.Reg, rep, X, LK); Expect(!rep.Locked && !rep.Open, "replica: unlocked, still closed");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, 1, 5.0) == ProposalCode.Accepted, "opened again");
        ApplyReplica(wh.Reg, rep, X, LK); Expect(rep.Open && !rep.Locked, "replica: open again");
        Expect(Txn(wh, 1, ObjectOp.Set, D, X, 0, 5.5) == ProposalCode.Accepted, "P1 closes");
        ApplyReplica(wh.Reg, rep, X, LK); Expect(!rep.Open, "replica closes");
    }

    static void BrokenDoorAndGates()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, Digest, 0);
        const uint X = 0xD2, LK = 0x52;
        var rep = new RepDoor { Locked = true, Enabled = false };   // a lock-gated leaf whose Door behaviour starts disabled
        // crowbar: Lockpick.DoorUnlock (lock state) + Door.OpenDestroyed on the leaf (door state), same frame
        Expect(Txn(wh, 2, ObjectOp.Reserve, L, LK, 0, 1.0) == ProposalCode.Accepted && Txn(wh, 2, ObjectOp.Set, L, LK, 1, 1.8) == ProposalCode.Accepted, "crowbar unlock committed");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, ObjectStateRules.DoorOpen | ObjectStateRules.DoorBroken, 1.8) == ProposalCode.Accepted, "broken leaf committed");
        ApplyReplica(wh.Reg, rep, X, LK);
        Expect(rep.Open && rep.Broken && rep.Enabled && !rep.Locked, "replica: broken, open, Door enabled (Door.Update runs the swing)");
        Expect(WorldDoorRules.Decide(5, false, false, false, false, false, false) == DoorApply.Break, "break never waits for the lock tag");
        Expect(Txn(wh, 1, ObjectOp.Set, D, X, 0, 2.0) == ProposalCode.Invalid, "a broken hinge cannot be closed by the other player");
        var bridge = StripComments(Src("WorldObjectBridge.cs"));
        int b = bridge.IndexOf("public void Break(", StringComparison.Ordinal);
        Expect(b > 0 && bridge.Substring(b, 260).Contains("b.enabled = true"), "Break enables the original Door behaviour first (as Lockpick.DoorUnlock does before OpenDestroyed)");
        int s = bridge.IndexOf("SpecialNames =", StringComparison.Ordinal);
        string names = bridge.Substring(s, bridge.IndexOf(';', s) - s);
        Expect(!names.Contains("isGate") && names.Contains("isCarDoor") && names.Contains("isDrawer") && names.Contains("isContainer"), "gates shared; car doors + furniture stay local");
        var sync = Src("WorldObjectSync.cs");
        Expect(sync.Contains("stays local: furniture / car doors are not shared yet") && sync.Contains("_localOnly++"), "a non-shared door type is logged (no silent return)");
        Expect(sync.Contains("LogStatsIfChanged()") && sync.Contains("_log.Info(Stats);"), "WOBJ_STATS reach the log");
    }

    static void SlowDoors()
    {
        var wh = Host(Madison, Madison);
        Scene(wh, 1, Madison, 1, Digest, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0); Scene(wh, 2, Madison, 2, Digest, 0);
        const uint X = 0xD3, Wn = 0x83;
        const byte Slow = ObjectStateRules.DoorOpen | ObjectStateRules.DoorSlow;
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, Slow, 1.0) == ProposalCode.Accepted && St(wh.Reg, Madison, D, X).State == Slow, "hold-E open: slow mark committed");
        var c = ClientCopy(wh); Expect(St(c, Madison, D, X).State == Slow, "replica sees open + slow (initial state / direction)");
        Expect(WorldDoorRules.Decide(Slow, false, false, false, false, true, false) == DoorApply.Swing, "replica starts the slow swing");
        ushort v = St(wh.Reg, Madison, D, X).Ver;
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, ObjectStateRules.DoorOpen, 1.3) == ProposalCode.Accepted && St(wh.Reg, Madison, D, X).Ver == v + 1 && St(wh.Reg, Madison, D, X).State == 1, "E released while slow: fast finish committed (new version, same physical state)");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, ObjectStateRules.DoorOpen, 1.4) == ProposalCode.Unchanged, "repeat: Unchanged");
        Expect(Txn(wh, 2, ObjectOp.Set, D, X, Slow, 1.5) == ProposalCode.Unchanged, "adding a slow mark to a resting door: Unchanged (presentation only)");
        Expect(WorldDoorRules.FastFinish(1, true, false, true) && !WorldDoorRules.FastFinish(1, true, false, false) && !WorldDoorRules.FastFinish(Slow, true, false, true)
            && !WorldDoorRules.FastFinish(0, true, false, true) && !WorldDoorRules.FastFinish(5, true, true, true), "replica: speed up only its own slow swing, same direction, not broken");
        Expect(ObjectStateRules.SlowFinish(ObjectKind.Door, Slow, 1) && ObjectStateRules.SlowFinish(ObjectKind.Door, ObjectStateRules.DoorSlow, 0) && !ObjectStateRules.SlowFinish(ObjectKind.Door, 1, Slow)
            && !ObjectStateRules.SlowFinish(ObjectKind.Door, Slow, 0) && !ObjectStateRules.SlowFinish(ObjectKind.Lockpick, 1, 1), "slow-finish rule");
        // window sash: inside-unlocked window (glass 1), sash opened slowly then finished fast
        byte sashSlow = (byte)(ObjectStateRules.GlassInside | ObjectStateRules.SashOpen | ObjectStateRules.SashSlow);
        Expect(Txn(wh, 2, ObjectOp.Set, W, Wn, ObjectStateRules.GlassInside, 2.0) == ProposalCode.Accepted && Txn(wh, 2, ObjectOp.Set, W, Wn, sashSlow, 2.2) == ProposalCode.Accepted, "sash slow open");
        Expect(Txn(wh, 2, ObjectOp.Set, W, Wn, (byte)(ObjectStateRules.GlassInside | ObjectStateRules.SashOpen), 2.4) == ProposalCode.Accepted, "sash fast finish committed");
        Expect(WorldDoorRules.FastFinish(WorldDoorRules.SashAsDoor((byte)(ObjectStateRules.GlassInside | ObjectStateRules.SashOpen)), true, false, true), "sash replica speeds up too");
        // the replica mirrors the ORIGINAL Door.Update slow profile without the local player's HUD / input side effects
        var bridge = StripComments(Src("WorldObjectBridge.cs"));
        foreach (var k in new[] { "45f * slowMul", "0.8f / (0.85f * slowMul)", "17f * mul", "70f * mul", "150f * _speedMul(d)", "\"slowOpenSpeedMulti\"", "src.clip = sq; src.Play();" })
            Expect(bridge.Contains(k), "slow profile / finish mirrors the original: " + k);
        foreach (var bad in new[] { "\"openingSlowly\", ", "Setter<bool>(d, \"openingSlowly\")", "DoorOpenButton", "PCB", "PMB", "mouseSensitivity", "canMove" })
            Expect(bridge.IndexOf(bad, StringComparison.Ordinal) < 0, "replica never touches " + bad);
        var sync = StripComments(Src("WorldObjectSync.cs"));
        Expect(sync.Contains("if (_b.SlowCount > 0) _b.SlowTick();") && bridge.Contains("if (_slow.Count >= 8) _slow.RemoveAt(0);"), "bounded per-frame slow work (<= 8 replicas)");
        Expect(sync.Contains("else if (w.Slow && !_b.DoorSlow(w.Door))"), "actor detects the original fast finish (openingSlowly falls while moving)");
    }

    static void DrivingCamera()
    {
        string note; int[] r;
        var plan = SpawnRules.Decide(null, null, 555, Madison, new Vec3(1f, 2f, 3f), out note);
        var host = new CampaignData(); host.Ints["Story"] = 3; host.Ints["CurrentLocation"] = Madison; host.Bools["TPP"] = false;   // Player 1 drives in first person
        var p2 = P2Inventory.Initial(false); p2.Id = 555;
        var snap = CampaignSnapshot.Build(host, p2, plan, 0, 555, 1, out r);
        Expect(!snap.Data.Bools.ContainsKey("TPP"), "Player 1's TPP is never sent");
        snap.ApplyLocalPreferences(new CampaignData());
        Expect(snap.Data.Bools.ContainsKey("TPP") && snap.Data.Bools["TPP"], "launcher client (no captured preference): original default TPP = true (third person); before: key absent = first person");
        var s2 = CampaignSnapshot.Build(host, p2, plan, 0, 555, 2, out r); s2.ApplyLocalPreferences(null);
        Expect(s2.Data.Bools["TPP"], "null preferences: default");
        var own = new CampaignData(); own.Bools["TPP"] = false;
        var s3 = CampaignSnapshot.Build(host, p2, plan, 0, 555, 3, out r); s3.ApplyLocalPreferences(own);
        Expect(!s3.Data.Bools["TPP"], "Player 2's own first-person choice is kept");
        Expect(CampaignPolicy.LocalBoolDefaults.Length == 1 && CampaignPolicy.LocalBoolDefaults[0].Key == "TPP" && CampaignPolicy.LocalBoolDefaults[0].Value, "default = MainMenu.StartNewGame / StartFreeMode SetBool(\"TPP\", true)");
        var cb = Src("CampaignBootstrap.cs");
        Expect(cb.IndexOf("SaveGuard.RefreshLocalPreferencesFromSandbox();", StringComparison.Ordinal) >= 0 && cb.IndexOf("SaveGuard.RefreshLocalPreferencesFromSandbox();", StringComparison.Ordinal) < cb.IndexOf("snap.ApplyLocalPreferences(SaveGuard.LocalPreferences);", StringComparison.Ordinal), "the in-session camera choice is read from the sandbox before a new snapshot is written");
        Expect(!Src("VehicleSync.cs").Contains("SetBool(\"TPP\""), "no replacement camera controller / preference override in the vehicle code");
    }

    static void ReadinessAndDevTest()
    {
        // launcher host: the same readiness rules as the F8 host
        Expect(HostReadiness.Check(new HostFacts { Context = GameContext.MainMenu }) != null, "main menu -> joins deferred");
        Expect(HostReadiness.Check(new HostFacts { Context = GameContext.InWorld, LoadedMap = true, PlayerPresent = true, ProfileValid = true, SecondsSinceSceneLoad = 10, SystemsReady = true, RegionKnown = true, PersistenceReady = false }) != null, "Player-2 record not bound -> deferred");
        Expect(HostReadiness.Check(new HostFacts { Context = GameContext.InWorld, LoadedMap = true, PlayerPresent = true, ProfileValid = true, SecondsSinceSceneLoad = 10, SystemsReady = true, RegionKnown = true, PersistenceReady = true }) == null, "ready");
        var menu = Src("MultiplayerMenu.cs");
        Expect(menu.Contains("if (legacy && n != null && n.Role == Role.Host) { n.HostGate = () => _hostGateReason;") && menu.Contains("if (_legacy) { LegacyHostGate(); return; }"), "launcher host installs the readiness gate");
        var cb = Src("CampaignBootstrap.cs");
        Expect(!cb.Contains("host readiness should have prevented this join") && cb.Contains("_snapshotDeferred = true") && cb.Contains("deferred until the host's save was loaded"), "an early connection gets the snapshot once the record is bound (no silent dead end)");
        // developer test mode: isolated folders
        string data = @"C:\Users\x\AppData\Local\TS2MP";
        string dev = SaveIsolation.DevTestDataDir(data);
        Expect(dev == Path.Combine(data, "devtest") && SaveIsolation.DevTestDataDir(dev) == dev && SaveIsolation.DevTestDataDir(dev + "\\") == dev && SaveIsolation.DevTestDataDir(null) == null, "devtest data folder (idempotent)");
        string real = @"C:\Users\x\AppData\LocalLow\CookieDev\Thief Simulator 2", game = @"D:\Games\Thief Simulator 2";
        Expect(SaveIsolation.LayoutError(Path.Combine(dev, "host-save"), real, game) == null && SaveIsolation.LayoutError(Path.Combine(dev, "client-sandbox"), real, game) == null, "host copy + client sandbox are separate from the real save and game folders");
        Expect(!SaveIsolation.IsUnder(Path.Combine(dev, "p2inv-0.dat"), data) || SaveIsolation.IsUnder(Path.Combine(dev, "p2inv-0.dat"), dev), "Player-2 record of a developer test is inside devtest");
        var plugin = Src("Plugin.cs");
        int f8 = plugin.IndexOf("runner.InitMenu(", StringComparison.Ordinal), devAt = plugin.IndexOf("if (args.DevTest)", StringComparison.Ordinal);
        Expect(f8 > 0 && devAt > f8, "developer test mode is only reachable in launcher mode (after the F8 branch returned)");
        Expect(plugin.Contains("if (!DevTestMode.LoopbackOnly(args)) { args.DevTest = false;") && plugin.Contains("args.DataDir = DevTestMode.IsolatedDataDir(args.DataDir);")
            && plugin.Contains("else if (args.DevTest) saveSafe = SaveGuard.InstallDevHost("), "loopback only, isolated data, host save redirected");
        var guard = Src("SaveGuard.cs");
        int seed = guard.IndexOf("static void SeedDevHostSave()", StringComparison.Ordinal);
        string seedBody = guard.Substring(seed, guard.IndexOf("// W4.2: the client never plays", seed, StringComparison.Ordinal) - seed);
        Expect(seedBody.Contains("File.Copy(src, dst, false)") && !seedBody.Contains("File.Delete") && !seedBody.Contains("File.Move") && seedBody.Contains("if (hasSave)"), "host copy: read-only copy of the real folder, once, never overwriting");
        Expect(guard.Contains("if (!Precheck(harmony, devDataDir, log, \"host-save\")) return false;"), "host copy lives in <devtest>\\host-save");
        var devm = StripComments(Src("DevTestMode.cs"));
        Expect(devm.Contains("static bool Isolated() { return SaveGuard.Armed && SaveGuard.RuntimeCheck(); }") && devm.Contains("if (!Isolated()) { _log.Warn(") && devm.Contains("if (!Isolated()) return;"), "conveniences only while the save target is verified isolated");
        foreach (var bad in new[] { "AdvanceStory", "Mission_Complete", "story_manager", "\"Story\"", "XPPoints", "Lvl", "item_amount", "SpawnWarning", "OnGUI" })
            Expect(devm.IndexOf(bad, StringComparison.Ordinal) < 0, "developer mode never touches " + bad);
        Expect(devm.Contains("\"Learn_Skill\"") && devm.Contains("\"maxUpgrades\""), "skills through the original Learn_Skill + max upgrade");
        string root = Path.GetDirectoryName(Path.GetDirectoryName(PluginDir()));
        var pkg = File.ReadAllText(Path.Combine(root, "tools", "package.ps1"));
        Expect(pkg.Contains("No launcher"), "the player release contains no launcher (devtest unreachable for players)");
        var launcher = File.ReadAllText(Path.Combine(root, "src", "Launcher", "Launcher.cs"));
        Expect(launcher.Contains("(_devTest ? \" -ts2mp-devtest 1\" : \"\")") && launcher.Contains("_dataDir = on ? Path.Combine(_baseDataDir, \"devtest\") : _baseDataDir;"), "launcher devtest: argument + isolated data folder");
    }

    // ---------------------------------------------------------------- source helpers
    static string PluginDir()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && root != null; i++)
        {
            var c = Path.Combine(root, "src", "Plugin");
            if (Directory.Exists(c)) return c;
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new Exception("src/Plugin not found");
    }
    static string Src(string f) { return File.ReadAllText(Path.Combine(PluginDir(), f)); }
    static string StripComments(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in s.Split('\n')) { int i = line.IndexOf("//", StringComparison.Ordinal); sb.Append(i >= 0 ? line.Substring(0, i) : line).Append('\n'); }
        return sb.ToString();
    }
}
