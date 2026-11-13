// W4.2 Phase A + W4.2B: host-owned, region-independent world-object registry for doors, locks (Lockpick) and windows
// (LockedWindow) in a supported region. Unity-free (unit-testable); the Unity side is src/Plugin/WorldObjectSync.cs (+ the
// replica bridge src/Plugin/WorldObjectBridge.cs). Investigation: docs/research/{DOOR,LOCKPICKING,WINDOW}_SYSTEM.md.
//
// Identity: WorldObjectId = (namespace = region id, 32-bit FNV hash of scene + census hierarchy path + kind) from the W1
// census (src/Shared/WorldIdentity.cs); ambiguous ids never resolve -> never shared. A window is ONE network object: the
// id of its LockedWindow component (captured by the census before the original UnlockWindow/BrokeWindow destroys that
// component); its sash Door (same GameObject) is mapped to that id, the sash bits live in the window state.
//
// Region lifetime (vanilla: none of the three classes writes a save key -> every scene load rebuilds them):
//   - An EPOCH of a region starts when the first player reports its loaded scene instance ("objScene": region, scene
//     serial = Unity scene handle, census object digest) and ends when no member is left. Members that change region stay
//     members for MemberGraceSeconds (a brief probe gap / out-of-order region notification does not reset the region);
//     a member that reports a new scene serial reloaded its scene: alone -> new epoch (vanilla reload), else it reconciles.
//     Disconnect removes the member at once. Epoch end clears every committed state (= the original reload).
//   - The first reporter of an epoch is the INITIALIZER: it sends the canonical initial state of its scene (every object
//     not in the default state: open doors incl. the per-load randomlyOpen roll, broken hinges, unlocked locks, ...) in
//     "objInit" chunks (160 entries each, up to 20 chunks). Until the last chunk is committed the region is not initialized
//     and other transitions are Busy. Everybody else reconciles: committed state, absent = default (closed / locked /
//     intact). If the initializer leaves before finishing, the next present member takes over (partial states cleared).
//   - Simulation owner (NPC-caused transitions, see NpcSet) = the lowest present member slot (the host when present).
//
// Authority and validation (the host does NOT load the regions it does not play in):
//   - The host decides every transition: requester = present member of that region in the current epoch, kind/region
//     supported, valid state and valid transition of that kind, base version = current version (first committed wins;
//     stale/duplicate/reordered requests never reverse a newer state), reservation, rate limit, capacity (1200 per kind).
//   - Catalog: when the host has a census of the region its catalog is strict (unknown id -> Invalid). Without one the
//     scene report must carry the same census object digest as the first accepted report of that region (scene metadata
//     pinned for the host session) and ids are accepted as UNVERIFIED (counted, logged) and pruned as soon as the host
//     censuses the region (PruneUnknown). Restricted objects (story locks / mission windows per the host census) are
//     refused for Player 2. The host cannot verify that a minigame was really won - stated trust boundary.
//   - Reservations: Reserve (lock / window) before the original minigame starts; another player's Reserve or minigame
//     result is Busy while it is held (ReserveSeconds); instant original actions (key, padlock, break glass, unlock from
//     inside) are never blocked by another player's reservation and cancel it (their physical result already happened).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum ObjectLifetime : byte { RegionLife = 1, Campaign = 2 }
    public enum ObjectOp : byte { Set = 0, Reserve = 1, Release = 2, NpcSet = 3 }

    public static class ObjectStateRules
    {
        public const int PageSize = 100, MaxPages = 12;     // registry value: 4 + 8 x 100 = 804 B per page
        public const int MaxPerRegion = PageSize * MaxPages; // 1200 per kind per region (Madison: 1142 doors, 76 locks, 184 windows)
        public const int RatePerSecond = 8;                 // single transitions per player per second (init chunks excluded)
        public const double ReserveSeconds = 120, MemberGraceSeconds = 4;
        public const string RegionKey = "wrgn";

        // Door state bits
        public const byte DoorOpen = 1, DoorSlow = 2, DoorBroken = 4;
        // Lockpick states
        public const byte LockLocked = 0, LockUnlocked = 1, LockKey = 2, LockPadlock = 3;
        // LockedWindow state: bits 0-1 glass, bits 2-4 sash (open / slow / broken hinge)
        public const byte GlassMask = 3, GlassIntact = 0, GlassInside = 1, GlassCut = 2, GlassBroken = 3, SashOpen = 4, SashSlow = 8, SashBroken = 16;

        public static bool SupportedKind(ObjectKind k) { return k == ObjectKind.Door || k == ObjectKind.Lockpick || k == ObjectKind.LockedWindow; }
        public static bool SupportedNamespace(int ns) { return ns == 99; }   // W4.2B scope: Madison Street
        public static bool SupportedNamespace(ObjectKind k, int ns) { return SupportedKind(k) && SupportedNamespace(ns); }
        public static ObjectLifetime Lifetime(ObjectKind k) { return SupportedKind(k) ? ObjectLifetime.RegionLife : ObjectLifetime.Campaign; }

        public static bool ValidState(ObjectKind k, byte s)
        {
            switch (k)
            {
                case ObjectKind.Door: return (s & ~(DoorOpen | DoorSlow | DoorBroken)) == 0 && ((s & DoorBroken) == 0 || (s & DoorOpen) != 0);
                case ObjectKind.Lockpick: return s <= LockPadlock;
                case ObjectKind.LockedWindow:
                    if ((s & ~31) != 0) return false;
                    if ((s & GlassMask) == GlassIntact) return (s & ~GlassMask) == 0;            // a locked window has no movable sash
                    return (s & SashBroken) == 0 || (s & SashOpen) != 0;
            }
            return false;
        }

        // Two states are the same physical state when they differ only in presentation (slow mark, which unlock path,
        // cut vs broken glass once the window is open to the world).
        public static bool SamePhysical(ObjectKind k, byte a, byte b)
        {
            switch (k)
            {
                case ObjectKind.Door: return (a & (DoorOpen | DoorBroken)) == (b & (DoorOpen | DoorBroken));
                case ObjectKind.Lockpick: return (a != LockLocked) == (b != LockLocked);
                case ObjectKind.LockedWindow:
                    return ((a & GlassMask) == GlassIntact) == ((b & GlassMask) == GlassIntact) && (a & (SashOpen | SashBroken)) == (b & (SashOpen | SashBroken));
            }
            return a == b;
        }

        // W4.2B.1: a slow (hold-E) swing that the actor finishes fast (Door.SlowFailed while still slow: E released / ray
        // left the door) - same physical state, only the slow mark is dropped. Committed (new version) so the replica can
        // speed up its own swing; every other presentation-only difference stays Unchanged.
        public static bool SlowFinish(ObjectKind k, byte cur, byte next)
        {
            if (!SamePhysical(k, cur, next)) return false;
            if (k == ObjectKind.Door) return (cur & DoorSlow) != 0 && (next & DoorSlow) == 0 && (next & DoorBroken) == 0;
            if (k == ObjectKind.LockedWindow) return (cur & GlassMask) == (next & GlassMask) && (cur & SashSlow) != 0 && (next & SashSlow) == 0 && (next & SashBroken) == 0;
            return false;
        }

        // Transitions the original game can make (null = allowed).
        public static string CheckTransition(ObjectKind k, byte cur, byte next, ObjectOp op)
        {
            switch (k)
            {
                case ObjectKind.Door:
                    if ((cur & DoorBroken) != 0 && (next & DoorBroken) == 0) return "a broken hinge stays broken for the visit";
                    return null;
                case ObjectKind.Lockpick:
                    if (cur == LockPadlock) return "a broken padlock stays broken";
                    if (cur != LockLocked && next != LockLocked) return "already unlocked";
                    return null;
                case ObjectKind.LockedWindow:
                    if ((cur & GlassMask) != GlassIntact && (next & GlassMask) == GlassIntact) return "an unlocked window cannot be locked again";
                    if ((cur & GlassMask) != GlassIntact && (next & GlassMask) != (cur & GlassMask)) return "the glass of an opened window cannot change";
                    if ((cur & SashBroken) != 0 && (next & SashBroken) == 0) return "a broken sash stays broken";
                    return null;
            }
            return "unsupported kind";
        }

        // Instant original actions (no minigame): never blocked by another player's reservation.
        public static bool Instant(ObjectKind k, byte cur, byte next)
        {
            if (k == ObjectKind.Lockpick) return next == LockKey || next == LockPadlock;
            if (k == ObjectKind.LockedWindow) return (cur & GlassMask) == GlassIntact && ((next & GlassMask) == GlassBroken || (next & GlassMask) == GlassInside);
            return false;
        }

        public static bool Reservable(ObjectKind k) { return k == ObjectKind.Lockpick || k == ObjectKind.LockedWindow; }
        public static bool IsDefault(ObjectKind k, byte s) { return s == 0; }

        public static string RegistryKey(ObjectKind k) { return RegistryKey(k, 0); }
        public static string RegistryKey(ObjectKind k, int page) { return page == 0 ? "wobj" + (byte)k : "wobj" + (byte)k + "." + page; }
        public static bool TryKindOfKey(string key, out ObjectKind k) { int p; return TryKindOfKey(key, out k, out p); }
        public static bool TryKindOfKey(string key, out ObjectKind k, out int page)
        {
            k = 0; page = 0; byte b;
            if (key == null || !key.StartsWith("wobj", StringComparison.Ordinal)) return false;
            string rest = key.Substring(4); int dot = rest.IndexOf('.');
            if (dot >= 0) { if (!int.TryParse(rest.Substring(dot + 1), out page) || page < 1 || page >= MaxPages) return false; rest = rest.Substring(0, dot); }
            if (!byte.TryParse(rest, out b)) return false;
            k = (ObjectKind)b; return SupportedKind(k);
        }
    }

    public struct ObjectState
    {
        public const int WireBytes = 8;
        public uint Hash; public byte State; public ushort Ver; public byte By;   // By = slot that committed the current state
    }

    // One registry page of committed states of one kind in one region (Region scope, arg = namespace,
    // key ObjectStateRules.RegistryKey(kind, page)); ≤ PageSize entries.
    public sealed class ObjectStateList
    {
        public const byte Version = 1;
        public readonly List<ObjectState> Items = new List<ObjectState>();

        public int IndexOf(uint hash) { for (int i = 0; i < Items.Count; i++) if (Items[i].Hash == hash) return i; return -1; }
        public bool TryGet(uint hash, out ObjectState s) { int i = IndexOf(hash); s = i >= 0 ? Items[i] : default(ObjectState); return i >= 0; }

        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put(Version); w.Put((ushort)Items.Count); w.Put((byte)0);
            foreach (var s in Items) { w.Put(s.Hash); w.Put(s.State); w.Put(s.Ver); w.Put(s.By); }
            return w.CopyData();
        }

        public static bool TryParse(byte[] b, out ObjectStateList l)
        {
            l = null;
            if (b == null || b.Length < 4) return false;
            var r = new NetDataReader(b);
            if (r.GetByte() != Version) return false;
            int n = r.GetUShort(); r.GetByte();
            if (n > ObjectStateRules.PageSize || b.Length != 4 + n * ObjectState.WireBytes) return false;
            var x = new ObjectStateList();
            for (int i = 0; i < n; i++) x.Items.Add(new ObjectState { Hash = r.GetUInt(), State = r.GetByte(), Ver = r.GetUShort(), By = r.GetByte() });
            l = x; return true;
        }
    }

    // "objTxn" (Session scope, arg 0): one requested operation on one object. 19 B (protocol 24).
    public struct ObjectTxnRequest
    {
        public const int Bytes = 19;
        public uint Txn; public ObjectOp Op; public byte Kind; public int Ns; public uint Hash; public byte State; public ushort BaseVer; public ushort Epoch;
        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put(Txn); w.Put((byte)Op); w.Put(Kind); w.Put(Ns); w.Put(Hash); w.Put(State); w.Put(BaseVer); w.Put(Epoch);
            return w.CopyData();
        }
        public static bool TryParse(byte[] b, out ObjectTxnRequest t)
        {
            t = default(ObjectTxnRequest);
            if (b == null || b.Length != Bytes) return false;
            var r = new NetDataReader(b);
            t.Txn = r.GetUInt(); byte op = r.GetByte(); if (op > (byte)ObjectOp.NpcSet) return false; t.Op = (ObjectOp)op;
            t.Kind = r.GetByte(); t.Ns = r.GetInt(); t.Hash = r.GetUInt(); t.State = r.GetByte(); t.BaseVer = r.GetUShort(); t.Epoch = r.GetUShort();
            return true;
        }
        public override string ToString() { return Op + " " + (ObjectKind)Kind + " " + IdentityNamespace.Name(Ns) + ":" + Hash.ToString("X8") + " -> " + State + " (base v" + BaseVer + ", epoch " + Epoch + ")"; }
    }

    // "objScene" (Player scope, arg = sender slot): "I have this scene instance of the region loaded". 14 B.
    public struct ObjectSceneReport
    {
        public const int Bytes = 14;
        public int Ns; public int Serial; public uint Digest; public ushort Count;
        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(Ns); w.Put(Serial); w.Put(Digest); w.Put(Count); return w.CopyData(); }
        public static bool TryParse(byte[] b, out ObjectSceneReport s)
        {
            s = default(ObjectSceneReport);
            if (b == null || b.Length != Bytes) return false;
            var r = new NetDataReader(b); s.Ns = r.GetInt(); s.Serial = r.GetInt(); s.Digest = r.GetUInt(); s.Count = r.GetUShort();
            return true;
        }
    }

    public struct ObjectInitEntry { public byte Kind; public uint Hash; public byte State; }

    // "objInit" (Session scope, arg 0): one chunk of the initializer's canonical initial state. 10 + 6 x n B (n ≤ 160).
    public sealed class ObjectInitChunk
    {
        public const int MaxEntries = 160, MaxChunks = 20, EntryBytes = 6;
        public int Ns; public ushort Epoch; public byte Index, Count;
        public readonly List<ObjectInitEntry> Entries = new List<ObjectInitEntry>();
        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put(Ns); w.Put(Epoch); w.Put(Index); w.Put(Count); w.Put((ushort)Entries.Count);
            foreach (var e in Entries) { w.Put(e.Kind); w.Put(e.Hash); w.Put(e.State); }
            return w.CopyData();
        }
        public static bool TryParse(byte[] b, out ObjectInitChunk c)
        {
            c = null;
            if (b == null || b.Length < 10) return false;
            var r = new NetDataReader(b);
            var x = new ObjectInitChunk { Ns = r.GetInt(), Epoch = r.GetUShort(), Index = r.GetByte(), Count = r.GetByte() };
            int n = r.GetUShort();
            if (x.Count == 0 || x.Count > MaxChunks || x.Index >= x.Count || n > MaxEntries || b.Length != 10 + n * EntryBytes) return false;
            for (int i = 0; i < n; i++) x.Entries.Add(new ObjectInitEntry { Kind = r.GetByte(), Hash = r.GetUInt(), State = r.GetByte() });
            c = x; return true;
        }
        // Splits a canonical initial state into chunks (always at least one, possibly empty).
        public static List<ObjectInitChunk> Split(int ns, ushort epoch, List<ObjectInitEntry> all)
        {
            var list = new List<ObjectInitChunk>();
            int count = Math.Max(1, (all.Count + MaxEntries - 1) / MaxEntries);
            if (count > MaxChunks) return null;
            for (int i = 0; i < count; i++)
            {
                var c = new ObjectInitChunk { Ns = ns, Epoch = epoch, Index = (byte)i, Count = (byte)count };
                for (int j = i * MaxEntries; j < Math.Min(all.Count, (i + 1) * MaxEntries); j++) c.Entries.Add(all[j]);
                list.Add(c);
            }
            return list;
        }
    }

    // Committed region record "wrgn" (Region scope, arg = namespace). 8 B.
    public struct RegionObjectRecord
    {
        public const int Bytes = 8;
        public ushort Epoch; public bool Active, Initialized; public byte InitBy, Owner, Members, Away;
        public bool IsMember(byte slot) { return slot >= 1 && slot <= 7 && (Members & (1 << slot)) != 0; }
        public bool IsPresent(byte slot) { return IsMember(slot) && (Away & (1 << slot)) == 0; }
        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put((byte)1); w.Put(Epoch); w.Put((byte)((Active ? 1 : 0) | (Initialized ? 2 : 0))); w.Put(InitBy); w.Put(Owner); w.Put(Members); w.Put(Away);
            return w.CopyData();
        }
        public static bool TryParse(byte[] b, out RegionObjectRecord r)
        {
            r = default(RegionObjectRecord);
            if (b == null || b.Length != Bytes || b[0] != 1) return false;
            var x = new NetDataReader(b); x.GetByte();
            r.Epoch = x.GetUShort(); byte f = x.GetByte(); r.Active = (f & 1) != 0; r.Initialized = (f & 2) != 0;
            r.InitBy = x.GetByte(); r.Owner = x.GetByte(); r.Members = x.GetByte(); r.Away = x.GetByte();
            return true;
        }
        public override string ToString() { return "epoch " + Epoch + (Active ? "" : " inactive") + (Initialized ? " initialized" : " initializing") + " initBy=P" + InitBy + " owner=P" + Owner + " members=" + Convert.ToString(Members, 2) + " away=" + Convert.ToString(Away, 2); }
    }

    // Host-side book: region epochs, members, initialization, reservations and every committed transition.
    public sealed class RegionStateBook
    {
        sealed class Member { public int Serial; public double AwaySince = -1; }
        sealed class Life
        {
            public ushort Epoch; public bool Active, Initialized; public byte InitBy;
            public readonly SortedDictionary<byte, Member> Members = new SortedDictionary<byte, Member>();
            public uint ChunksSeen; public int ChunkCount;
        }
        sealed class Reservation { public byte Slot; public double Until; }
        sealed class KindList
        {
            public readonly List<ObjectState> Items = new List<ObjectState>();
            public readonly Dictionary<uint, int> Index = new Dictionary<uint, int>();
            public void Clear() { Items.Clear(); Index.Clear(); }
        }

        readonly Dictionary<int, Life> _life = new Dictionary<int, Life>();
        readonly Dictionary<long, KindList> _lists = new Dictionary<long, KindList>();
        readonly Dictionary<long, int> _pagesCommitted = new Dictionary<long, int>();   // pages last written per (kind, ns)
        readonly Dictionary<long, Reservation> _res = new Dictionary<long, Reservation>();
        readonly Dictionary<int, uint> _digest = new Dictionary<int, uint>();   // pinned scene metadata per namespace (host session)
        readonly Dictionary<byte, double> _windowStart = new Dictionary<byte, double>();
        readonly Dictionary<byte, int> _windowCount = new Dictionary<byte, int>();
        public long Accepted, Unchanged, Stale, Refused, Unverified, Cleared, Epochs, Pruned, InitEntries, Reserved, Released, Expired;
        public Func<ObjectKind, int, uint, bool?> Catalog;          // host census: null = never censused this namespace
        public Func<ObjectKind, int, uint, bool> Restricted;        // host census: story lock / mission window (refused for Player 2)
        public byte HostSlot = 1;

        static long K(ObjectKind k, int ns) { return ((long)(byte)k << 32) | (uint)ns; }
        static long R(ObjectKind k, int ns, uint hash) { return ((long)(byte)k << 56) ^ ((long)(ns & 0xFFFFFF) << 32) ^ hash; }

        Life LifeOf(int ns) { Life l; if (!_life.TryGetValue(ns, out l)) _life[ns] = l = new Life(); return l; }
        KindList ListOf(ObjectKind k, int ns, bool create) { KindList l; if (!_lists.TryGetValue(K(k, ns), out l) && create) _lists[K(k, ns)] = l = new KindList(); return l; }

        public bool TryGet(ObjectKind k, int ns, uint hash, out ObjectState s)
        {
            s = default(ObjectState); var l = ListOf(k, ns, false); int i;
            if (l == null || !l.Index.TryGetValue(hash, out i)) return false;
            s = l.Items[i]; return true;
        }
        public int CountOf(ObjectKind k, int ns) { var l = ListOf(k, ns, false); return l != null ? l.Items.Count : 0; }
        public bool TryRecord(int ns, out RegionObjectRecord r) { Life l; r = default(RegionObjectRecord); if (!_life.TryGetValue(ns, out l)) return false; r = Record(ns, l); return true; }
        public ushort EpochOf(int ns) { Life l; return _life.TryGetValue(ns, out l) && l.Active ? l.Epoch : (ushort)0; }
        public bool Initialized(int ns) { Life l; return _life.TryGetValue(ns, out l) && l.Active && l.Initialized; }
        public byte OwnerOf(int ns) { Life l; return _life.TryGetValue(ns, out l) ? Owner(l) : (byte)0; }
        public byte ReservedBy(ObjectKind k, int ns, uint hash) { Reservation r; return _res.TryGetValue(R(k, ns, hash), out r) ? r.Slot : (byte)0; }

        static byte Owner(Life l) { foreach (var kv in l.Members) if (kv.Value.AwaySince < 0) return kv.Key; return 0; }

        RegionObjectRecord Record(int ns, Life l)
        {
            var r = new RegionObjectRecord { Epoch = l.Epoch, Active = l.Active, Initialized = l.Initialized, InitBy = l.InitBy, Owner = Owner(l) };
            foreach (var kv in l.Members) { if (kv.Key >= 1 && kv.Key <= 7) { r.Members |= (byte)(1 << kv.Key); if (kv.Value.AwaySince >= 0) r.Away |= (byte)(1 << kv.Key); } }
            return r;
        }

        void CommitRecord(int ns, Life l, StateRegistry reg, List<StateEntry> outbox)
        {
            var e = reg.Commit(StateScope.Region, ns, ObjectStateRules.RegionKey, Record(ns, l).ToBytes());
            if (e != null) outbox.Add(e);
        }

        // Writes the pages of one kind list that changed (pageFrom..) and empties pages that are no longer needed.
        void CommitPages(ObjectKind k, int ns, KindList l, int pageFrom, StateRegistry reg, List<StateEntry> outbox)
        {
            int pages = (l.Items.Count + ObjectStateRules.PageSize - 1) / ObjectStateRules.PageSize;
            int before; _pagesCommitted.TryGetValue(K(k, ns), out before);
            int upto = Math.Max(pages, before);
            for (int p = Math.Max(0, pageFrom); p < upto; p++)
            {
                var page = new ObjectStateList();
                for (int i = p * ObjectStateRules.PageSize; i < Math.Min(l.Items.Count, (p + 1) * ObjectStateRules.PageSize); i++) page.Items.Add(l.Items[i]);
                var e = reg.Commit(StateScope.Region, ns, ObjectStateRules.RegistryKey(k, p), page.ToBytes());
                if (e != null) outbox.Add(e);
            }
            _pagesCommitted[K(k, ns)] = pages;
        }

        void ClearStates(int ns, StateRegistry reg, List<StateEntry> outbox)
        {
            foreach (var kv in new List<KeyValuePair<long, KindList>>(_lists))
            {
                if ((int)(uint)kv.Key != ns || kv.Value.Items.Count == 0) continue;
                kv.Value.Clear();
                CommitPages((ObjectKind)(byte)(kv.Key >> 32), ns, kv.Value, 0, reg, outbox);
                Cleared++;
            }
            foreach (var key in new List<long>(_res.Keys)) if (((key >> 32) & 0xFFFFFF) == (ns & 0xFFFFFF)) _res.Remove(key);
        }

        void StartEpoch(int ns, Life l, byte slot, StateRegistry reg, List<StateEntry> outbox)
        {
            ClearStates(ns, reg, outbox);
            l.Epoch = (ushort)(l.Epoch == ushort.MaxValue ? 1 : l.Epoch + 1);
            l.Active = true; l.Initialized = false; l.InitBy = slot; l.ChunksSeen = 0; l.ChunkCount = 0;
            Epochs++;
        }

        void EndEpoch(int ns, Life l, StateRegistry reg, List<StateEntry> outbox)
        {
            l.Active = false; l.Initialized = false; l.InitBy = 0; l.Members.Clear(); l.ChunksSeen = 0; l.ChunkCount = 0;
            ClearStates(ns, reg, outbox);
            CommitRecord(ns, l, reg, outbox);
        }

        // A member left for good: hand the initialization to the next present member (its own scene becomes canonical).
        void AfterMemberRemoved(int ns, Life l, byte removed, StateRegistry reg, List<StateEntry> outbox)
        {
            if (l.Members.Count == 0) { EndEpoch(ns, l, reg, outbox); return; }
            if (!l.Initialized && l.InitBy == removed)
            {
                ClearStates(ns, reg, outbox);
                byte next = Owner(l); if (next == 0) foreach (var kv in l.Members) { next = kv.Key; break; }
                l.InitBy = next; l.ChunksSeen = 0; l.ChunkCount = 0;
            }
            foreach (var key in new List<long>(_res.Keys)) if (_res[key].Slot == removed) _res.Remove(key);
            CommitRecord(ns, l, reg, outbox);
        }

        // ---- membership ----
        public ProposalCode OnScene(byte slot, int slotRegion, ObjectSceneReport s, double now, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            why = null;
            if (!ObjectStateRules.SupportedNamespace(s.Ns)) { why = "region not shared"; return ProposalCode.NotAuthorized; }
            if (slotRegion != s.Ns) { why = "requester is in " + Regions.Name(slotRegion) + ", not in " + IdentityNamespace.Name(s.Ns); return ProposalCode.NotAuthorized; }
            uint pinned;   // Digest 0 = no pristine census of that scene instance (host's late census): no metadata to pin or compare
            if (s.Digest != 0 && _digest.TryGetValue(s.Ns, out pinned) && pinned != s.Digest) { why = "scene metadata differs (census digest " + s.Digest.ToString("X8") + " vs " + pinned.ToString("X8") + ")"; Refused++; return ProposalCode.Invalid; }
            if (s.Digest != 0 && !_digest.ContainsKey(s.Ns)) _digest[s.Ns] = s.Digest;
            var l = LifeOf(s.Ns);
            Member m;
            if (!l.Active) { StartEpoch(s.Ns, l, slot, reg, outbox); l.Members[slot] = new Member { Serial = s.Serial }; why = "epoch " + l.Epoch + " started (initializer P" + slot + ")"; }
            else if (l.Members.TryGetValue(slot, out m))
            {
                if (m.Serial != s.Serial)
                {
                    bool others = false; foreach (var kv in l.Members) if (kv.Key != slot) others = true;
                    if (!others) { StartEpoch(s.Ns, l, slot, reg, outbox); l.Members[slot] = new Member { Serial = s.Serial }; why = "scene reloaded alone: epoch " + l.Epoch + " (vanilla reset)"; }
                    else { m.Serial = s.Serial; m.AwaySince = -1; why = "scene reloaded: reconciles to epoch " + l.Epoch; }
                }
                else { if (m.AwaySince < 0) { why = "unchanged"; CommitRecord(s.Ns, l, reg, outbox); return ProposalCode.Unchanged; } m.AwaySince = -1; why = "back in the same scene instance"; }
            }
            else { l.Members[slot] = new Member { Serial = s.Serial }; why = "joined epoch " + l.Epoch; }
            CommitRecord(s.Ns, l, reg, outbox);
            return ProposalCode.Accepted;
        }

        // Registry region of a player changed (host bookkeeping): members of other regions start their grace period.
        public void OnPlayerRegion(byte slot, int region, double now, StateRegistry reg, List<StateEntry> outbox)
        {
            foreach (var kv in _life)
            {
                Member m;
                if (!kv.Value.Active || kv.Key == region || !kv.Value.Members.TryGetValue(slot, out m) || m.AwaySince >= 0) continue;
                m.AwaySince = now;
                foreach (var key in new List<long>(_res.Keys)) if (_res[key].Slot == slot && ((key >> 32) & 0xFFFFFF) == (kv.Key & 0xFFFFFF)) { _res.Remove(key); Released++; }
                CommitRecord(kv.Key, kv.Value, reg, outbox);
            }
        }

        public void OnDisconnect(byte slot, StateRegistry reg, List<StateEntry> outbox)
        {
            foreach (var kv in new List<KeyValuePair<int, Life>>(_life))
                if (kv.Value.Active && kv.Value.Members.Remove(slot)) AfterMemberRemoved(kv.Key, kv.Value, slot, reg, outbox);
            foreach (var key in new List<long>(_res.Keys)) if (_res[key].Slot == slot) _res.Remove(key);
        }

        public void Tick(double now, StateRegistry reg, List<StateEntry> outbox)
        {
            foreach (var kv in new List<KeyValuePair<int, Life>>(_life))
            {
                if (!kv.Value.Active) continue;
                List<byte> gone = null;
                foreach (var m in kv.Value.Members) if (m.Value.AwaySince >= 0 && now - m.Value.AwaySince >= ObjectStateRules.MemberGraceSeconds) (gone = gone ?? new List<byte>()).Add(m.Key);
                if (gone == null) continue;
                foreach (var s in gone) { kv.Value.Members.Remove(s); AfterMemberRemoved(kv.Key, kv.Value, s, reg, outbox); }
            }
            if (_res.Count > 0)
                foreach (var key in new List<long>(_res.Keys)) if (_res[key].Until <= now) { _res.Remove(key); Expired++; }
        }

        // ---- initialization ----
        public ProposalCode Init(byte slot, ObjectInitChunk c, double now, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            why = null;
            Life l; Member m;
            if (!ObjectStateRules.SupportedNamespace(c.Ns) || !_life.TryGetValue(c.Ns, out l) || !l.Active) { why = "region epoch not active"; Refused++; return ProposalCode.NotAuthorized; }
            if (c.Epoch != l.Epoch) { why = "epoch " + c.Epoch + " is not the current " + l.Epoch; Stale++; return ProposalCode.Stale; }
            if (!l.Members.TryGetValue(slot, out m) || m.AwaySince >= 0) { why = "not a present member"; Refused++; return ProposalCode.NotAuthorized; }
            if (l.Initialized) { why = "already initialized"; Unchanged++; return ProposalCode.Unchanged; }
            if (l.InitBy != slot) { why = "P" + l.InitBy + " initializes this epoch"; Refused++; return ProposalCode.NotAuthorized; }
            if (l.ChunkCount != 0 && l.ChunkCount != c.Count) { why = "chunk count changed"; Refused++; return ProposalCode.Invalid; }
            if ((l.ChunksSeen & (1u << c.Index)) != 0) { why = "duplicate chunk"; Unchanged++; return ProposalCode.Unchanged; }
            l.ChunkCount = c.Count;
            var touched = new Dictionary<long, int>();
            int skipped = 0, unverified = 0;
            foreach (var e in c.Entries)
            {
                var k = (ObjectKind)e.Kind;
                if (!ObjectStateRules.SupportedKind(k) || !ObjectStateRules.ValidState(k, e.State) || ObjectStateRules.IsDefault(k, e.State)) { skipped++; continue; }
                bool? known = Catalog != null ? Catalog(k, c.Ns, e.Hash) : null;
                if (known == false) { skipped++; continue; }
                if (known == null) unverified++;
                var list = ListOf(k, c.Ns, true); int i;
                var st = new ObjectState { Hash = e.Hash, State = e.State, Ver = 1, By = slot };
                if (list.Index.TryGetValue(e.Hash, out i)) list.Items[i] = st;
                else { if (list.Items.Count >= ObjectStateRules.MaxPerRegion) { skipped++; continue; } i = list.Items.Count; list.Index[e.Hash] = i; list.Items.Add(st); }
                int p; long key = K(k, c.Ns); int page = i / ObjectStateRules.PageSize;
                if (!touched.TryGetValue(key, out p) || page < p) touched[key] = page;
                InitEntries++;
            }
            foreach (var kv in touched) CommitPages((ObjectKind)(byte)(kv.Key >> 32), c.Ns, _lists[kv.Key], kv.Value, reg, outbox);
            l.ChunksSeen |= 1u << c.Index;
            Unverified += unverified;
            if (l.ChunksSeen == (c.Count >= 32 ? uint.MaxValue : (1u << c.Count) - 1)) { l.Initialized = true; CommitRecord(c.Ns, l, reg, outbox); }
            why = "chunk " + (c.Index + 1) + "/" + c.Count + ": " + (c.Entries.Count - skipped) + " state(s)" + (skipped > 0 ? ", " + skipped + " skipped" : "") + (unverified > 0 ? ", " + unverified + " unverified" : "") + (l.Initialized ? " -> initialized" : "");
            return ProposalCode.Accepted;
        }

        // ---- transitions ----
        public ProposalCode Change(byte slot, int slotRegion, ObjectTxnRequest t, double now, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            why = null;
            var kind = (ObjectKind)t.Kind;
            if (!ObjectStateRules.SupportedNamespace(kind, t.Ns)) { why = "kind/region not shared in this milestone"; Refused++; return ProposalCode.NotAuthorized; }
            if (slotRegion != t.Ns) { why = "requester is in " + Regions.Name(slotRegion) + ", not in " + IdentityNamespace.Name(t.Ns); Refused++; return ProposalCode.NotAuthorized; }
            Life l; Member m;
            if (!_life.TryGetValue(t.Ns, out l) || !l.Active) { why = "no active epoch of " + IdentityNamespace.Name(t.Ns); Refused++; return ProposalCode.NotAuthorized; }
            if (t.Epoch != l.Epoch) { why = "epoch " + t.Epoch + " is not the current " + l.Epoch; Stale++; return ProposalCode.Stale; }
            if (!l.Members.TryGetValue(slot, out m) || m.AwaySince >= 0) { why = "not a present member of the region epoch"; Refused++; return ProposalCode.NotAuthorized; }
            if (!l.Initialized) { why = "region initializing"; Refused++; return ProposalCode.Busy; }
            if (t.Op != ObjectOp.Release && !ObjectStateRules.ValidState(kind, t.State)) { why = "invalid state " + t.State; Refused++; return ProposalCode.Invalid; }
            bool? known = Catalog != null ? Catalog(kind, t.Ns, t.Hash) : null;
            if (known == false) { why = "id is not an unambiguous " + kind + " of the host's census of " + IdentityNamespace.Name(t.Ns); Refused++; return ProposalCode.Invalid; }
            if (slot != HostSlot && Restricted != null && Restricted(kind, t.Ns, t.Hash)) { why = "story/mission object: Player 2 may not change it until shared missions exist"; Refused++; return ProposalCode.NotAuthorized; }
            if (t.Op == ObjectOp.NpcSet)
            {
                if (Owner(l) != slot) { why = "NPC transitions only from the region's simulation owner P" + Owner(l); Refused++; return ProposalCode.NotAuthorized; }
                bool relock = kind == ObjectKind.Lockpick && t.State == ObjectStateRules.LockLocked;
                bool close = kind == ObjectKind.Door && (t.State & ObjectStateRules.DoorOpen) == 0;
                if (!relock && !close) { why = "NPCs only relock locks / close doors"; Refused++; return ProposalCode.Invalid; }
            }
            if (!RateOk(slot, now)) { why = "rate limit"; Refused++; return ProposalCode.Busy; }

            ObjectState cur; bool exists = TryGet(kind, t.Ns, t.Hash, out cur);
            if (!exists) cur = default(ObjectState);
            long rk = R(kind, t.Ns, t.Hash); Reservation res; bool held = _res.TryGetValue(rk, out res) && res.Until > now;
            if (_res.ContainsKey(rk) && !held) { _res.Remove(rk); Expired++; }

            if (t.Op == ObjectOp.Reserve)
            {
                if (!ObjectStateRules.Reservable(kind)) { why = "not reservable"; Refused++; return ProposalCode.Invalid; }
                if (held && res.Slot != slot) { why = "reserved by P" + res.Slot; Refused++; return ProposalCode.Busy; }
                if (!ObjectStateRules.IsDefault(kind, cur.State)) { why = "already " + cur.State + " (v" + cur.Ver + ")"; Stale++; return ProposalCode.Stale; }
                _res[rk] = new Reservation { Slot = slot, Until = now + ObjectStateRules.ReserveSeconds };
                Reserved++; why = "reserved for P" + slot;
                return ProposalCode.Accepted;
            }
            if (t.Op == ObjectOp.Release)
            {
                if (held && res.Slot == slot) { _res.Remove(rk); Released++; why = "released"; return ProposalCode.Accepted; }
                why = "no reservation of P" + slot; Unchanged++; return ProposalCode.Unchanged;
            }

            bool instant = ObjectStateRules.Instant(kind, cur.State, t.State);
            if (held && res.Slot != slot && !instant) { why = "reserved by P" + res.Slot; Refused++; return ProposalCode.Busy; }
            if (cur.Ver != t.BaseVer)
            {
                if (ObjectStateRules.SamePhysical(kind, cur.State, t.State)) { why = "already in that state (v" + cur.Ver + ")"; Unchanged++; if (held && res.Slot == slot) _res.Remove(rk); return ProposalCode.Unchanged; }
                why = "base v" + t.BaseVer + " is not the current v" + cur.Ver + " (another change was committed first)"; Stale++; return ProposalCode.Stale;
            }
            if (ObjectStateRules.SamePhysical(kind, cur.State, t.State) && !ObjectStateRules.SlowFinish(kind, cur.State, t.State)) { why = "unchanged"; Unchanged++; if (held && res.Slot == slot) _res.Remove(rk); return ProposalCode.Unchanged; }
            string bad = ObjectStateRules.CheckTransition(kind, cur.State, t.State, t.Op);
            if (bad != null) { why = bad; Refused++; return ProposalCode.Invalid; }

            var list = ListOf(kind, t.Ns, true); int idx;
            var next = new ObjectState { Hash = t.Hash, State = t.State, Ver = (ushort)(cur.Ver == ushort.MaxValue ? 1 : cur.Ver + 1), By = slot };
            if (list.Index.TryGetValue(t.Hash, out idx)) list.Items[idx] = next;
            else
            {
                if (list.Items.Count >= ObjectStateRules.MaxPerRegion) { why = "region capacity " + ObjectStateRules.MaxPerRegion; Refused++; return ProposalCode.Busy; }
                idx = list.Items.Count; list.Index[t.Hash] = idx; list.Items.Add(next);
            }
            CommitPages(kind, t.Ns, list, idx / ObjectStateRules.PageSize, reg, outbox);
            if (held) { _res.Remove(rk); if (res.Slot != slot) why = "instant original action cancels P" + res.Slot + "'s reservation"; }
            if (known == null) { Unverified++; why = (why != null ? why + "; " : "") + "unverified id (host has no census of " + IdentityNamespace.Name(t.Ns) + ")"; }
            Accepted++;
            return ProposalCode.Accepted;
        }

        bool RateOk(byte slot, double now)
        {
            double start; int n;
            if (!_windowStart.TryGetValue(slot, out start) || now - start >= 1.0) { _windowStart[slot] = now; _windowCount[slot] = 1; return true; }
            _windowCount.TryGetValue(slot, out n);
            if (n >= ObjectStateRules.RatePerSecond) return false;
            _windowCount[slot] = n + 1;
            return true;
        }

        // The host censused the region (it loaded it): unverified ids that are not in its catalog are removed (they never
        // resolve on any instance) and the census digest replaces the pinned scene metadata.
        public int PruneUnknown(int ns, uint digest, StateRegistry reg, List<StateEntry> outbox)
        {
            if (digest != 0) _digest[ns] = digest;
            if (Catalog == null) return 0;
            int removed = 0;
            foreach (var kv in new List<KeyValuePair<long, KindList>>(_lists))
            {
                if ((int)(uint)kv.Key != ns) continue;
                var k = (ObjectKind)(byte)(kv.Key >> 32); var l = kv.Value; int before = l.Items.Count;
                var keep = new List<ObjectState>();
                foreach (var s in l.Items) if (Catalog(k, ns, s.Hash) != false) keep.Add(s);
                if (keep.Count == before) continue;
                l.Clear(); foreach (var s in keep) { l.Index[s.Hash] = l.Items.Count; l.Items.Add(s); }
                CommitPages(k, ns, l, 0, reg, outbox);
                removed += before - keep.Count;
            }
            Pruned += removed;
            return removed;
        }

        public string Stats
        {
            get
            {
                return "accepted=" + Accepted + " unchanged=" + Unchanged + " stale=" + Stale + " refused=" + Refused + " unverified=" + Unverified + " epochs=" + Epochs
                    + " initEntries=" + InitEntries + " cleared=" + Cleared + " pruned=" + Pruned + " reserved=" + Reserved + " released=" + Released + " expired=" + Expired;
            }
        }
    }

    // ---- client/observer side, pure: what to do with one local object given the committed state ----
    public enum DoorApply : byte { None, Swing, Break, SkipBusy, SkipLocked, SkipPending, Diverged }
    public static class WorldDoorRules
    {
        // desired: committed door bits (open / slow / broken); localOpen: Door.isOpen; localBroken: Door.brokeHinge;
        // moving: Door.opening; localUse: PlayerScript.assignDoor is this door; usable: tag "Door" (unlocked here);
        // pending: this instance has an unanswered proposal for the object.
        public static DoorApply Decide(byte desired, bool localOpen, bool localBroken, bool moving, bool localUse, bool usable, bool pending)
        {
            bool open = (desired & ObjectStateRules.DoorOpen) != 0, broken = (desired & ObjectStateRules.DoorBroken) != 0;
            if (open == localOpen && broken == localBroken) return DoorApply.None;
            if (localBroken && !broken) return DoorApply.Diverged;   // a broken hinge cannot be repaired in this scene instance
            if (pending) return DoorApply.SkipPending;
            if (moving || localUse) return DoorApply.SkipBusy;
            if (broken) return DoorApply.Break;
            // W4.2B.1: only OPENING waits for this instance's lock state. Closing never depends on the tag: the original
            // Door.ResetLock closes an open door and relocks it (the lock replica can be applied first in the same
            // reconcile), and NPCs open/close "Lock1" doors. Waiting here left the door open forever after a relock.
            if (!usable && open) return DoorApply.SkipLocked;
            return DoorApply.Swing;
        }
        // W4.2B.1: the actor finished a slow swing fast (slow mark dropped, same physical state) while this replica still
        // emulates the slow swing -> speed it up like the original Door.SlowFailed (presentation only).
        public static bool FastFinish(byte desired, bool localOpen, bool localBroken, bool replicaSlow)
        {
            bool open = (desired & ObjectStateRules.DoorOpen) != 0, broken = (desired & ObjectStateRules.DoorBroken) != 0;
            return replicaSlow && open == localOpen && broken == localBroken && !broken && (desired & ObjectStateRules.DoorSlow) == 0;
        }
        // Phase A signature (no broken bit).
        public static DoorApply Decide(byte desired, bool localOpen, bool moving, bool localUse, bool usable, bool pending)
        {
            return Decide((byte)(desired & ~ObjectStateRules.DoorBroken), localOpen, false, moving, localUse, usable, pending);
        }
        // Window sash bits (2-4) as door bits (0-2).
        public static byte SashAsDoor(byte windowState) { return (byte)((windowState >> 2) & 7); }
        public static byte WithSash(byte windowState, byte doorBits) { return (byte)((windowState & ObjectStateRules.GlassMask) | ((doorBits & 7) << 2)); }
    }

    public enum LockApply : byte { None, Unlock, Relock, SkipBusy, SkipPending, Diverged }
    public static class WorldLockRules
    {
        // local: the lock state this instance tracks (its own hooks + applied replicas); busy: the local player is in this
        // lock's minigame (a remote result waits until it ended).
        public static LockApply Decide(byte desired, byte local, bool pending, bool busy)
        {
            if (ObjectStateRules.SamePhysical(ObjectKind.Lockpick, desired, local)) return LockApply.None;
            if (local == ObjectStateRules.LockPadlock) return LockApply.Diverged;   // the padlock already fell here
            if (pending) return LockApply.SkipPending;
            if (busy) return LockApply.SkipBusy;
            return desired == ObjectStateRules.LockLocked ? LockApply.Relock : LockApply.Unlock;
        }
    }

    public enum WindowApply : byte { None, Glass, SkipBusy, SkipPending, Diverged }
    public static class WorldWindowRules
    {
        // Glass part only (the sash is a Door, WorldDoorRules); alive: the LockedWindow component still exists here.
        public static WindowApply Decide(byte desired, byte localGlass, bool alive, bool pending, bool busy)
        {
            byte want = (byte)(desired & ObjectStateRules.GlassMask);
            if ((want == 0) == (localGlass == 0)) return WindowApply.None;
            if (want == 0 || !alive) return WindowApply.Diverged;   // opened here / component gone: the original cannot re-lock it
            if (pending) return WindowApply.SkipPending;
            if (busy) return WindowApply.SkipBusy;
            return WindowApply.Glass;
        }
    }
}
