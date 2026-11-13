// W4.2 Phase A: host-owned, region-independent persistent world-object registry. Unity-free (unit-testable); the Unity side
// of the first vertical slice (ordinary doors) is src/Plugin/WorldDoorSync.cs.
//
// Identity: WorldObjectId = (namespace = region id, 32-bit FNV hash of scene name + census hierarchy path + kind) from the
// W1 census (src/Shared/WorldIdentity.cs): sibling indices only where names repeat, an occurrence suffix for several
// components on one transform, and ids that collide or repeat are AMBIGUOUS and never resolve -> never shared.
//
// Authority and validation boundaries (the host does NOT load the regions it does not play in):
//   - Simulation owner of a region = every player that has it loaded; the original interaction (door swing, lockpicking
//     minigame, ...) runs on that player's own PC.
//   - The host owns the authoritative state of each object and decides every transition: the requester must be in that
//     region (registry "region"), the kind/namespace must be supported, the state must be a valid state of that kind,
//     the base version must match the current version (first committed change wins; a stale, duplicated or reordered
//     request can never reverse a newer state), capacity and a per-player rate limit apply. When the host has a census
//     catalog of that region (it loaded the region in this process) the id must be a known, unambiguous object of that
//     kind; otherwise the id is accepted on the simulating player's word (logged as unverified) - the host cannot check
//     physics, line of sight or NPC perception of a scene it does not run. That is the stated trust boundary.
//   - Lifetime per kind: RegionLife = the original scene lifetime (the object resets when its scene is reloaded, e.g.
//     Door has no save key - verified Door.Start IL: no SPrefs access, OpenOnStart / randomlyOpen decided per load):
//     the host keeps the state while at least one player has the region loaded and clears it when the region empties,
//     exactly like a vanilla scene reload. Campaign = durable across sessions under the host campaign (for later kinds that
//     the original game persists, e.g. broken windows in W4.2B); serialised by SerializeCampaign / LoadCampaign.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum ObjectLifetime : byte { RegionLife = 1, Campaign = 2 }

    public static class ObjectStateRules
    {
        public const int MaxPerRegion = 100;            // registry value 1024 B: 4 + 8 x 100 = 804 B
        public const int RatePerSecond = 8;             // per player; the original door needs ~0.6 s per swing
        public const byte DoorOpen = 1, DoorSlow = 2;   // door state bits: open, last change was the original slow (hold-E) movement

        // W4.2 vertical slice: ordinary doors in Madison Street. Further kinds/regions in W4.2B-D.
        public static bool SupportedKind(ObjectKind k) { return k == ObjectKind.Door; }
        public static bool SupportedNamespace(ObjectKind k, int ns) { return k == ObjectKind.Door && ns == 99; }
        public static ObjectLifetime Lifetime(ObjectKind k) { return k == ObjectKind.Door ? ObjectLifetime.RegionLife : ObjectLifetime.Campaign; }
        public static bool ValidState(ObjectKind k, byte s) { return k == ObjectKind.Door && (s & ~(DoorOpen | DoorSlow)) == 0; }
        // Two states are the same physical state when they differ only in presentation bits (door: slow mark).
        public static bool SamePhysical(ObjectKind k, byte a, byte b) { return k == ObjectKind.Door ? (a & DoorOpen) == (b & DoorOpen) : a == b; }
        public static string RegistryKey(ObjectKind k) { return "wobj" + (byte)k; }
        public static bool TryKindOfKey(string key, out ObjectKind k)
        {
            k = 0; byte b;
            if (key == null || !key.StartsWith("wobj", StringComparison.Ordinal) || !byte.TryParse(key.Substring(4), out b)) return false;
            k = (ObjectKind)b; return SupportedKind(k);
        }
    }

    public struct ObjectState
    {
        public const int WireBytes = 8;
        public uint Hash; public byte State; public ushort Ver; public byte By;   // By = slot that committed the current state
    }

    // Committed states of one kind in one region (Region scope, arg = namespace, key ObjectStateRules.RegistryKey(kind)).
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
            if (n > ObjectStateRules.MaxPerRegion || b.Length != 4 + n * ObjectState.WireBytes) return false;
            var x = new ObjectStateList();
            for (int i = 0; i < n; i++) x.Items.Add(new ObjectState { Hash = r.GetUInt(), State = r.GetByte(), Ver = r.GetUShort(), By = r.GetByte() });
            l = x; return true;
        }
    }

    // Client proposal "objTxn" (Session scope, arg 0) or the host's own change: one requested transition. 16 B.
    public struct ObjectTxnRequest
    {
        public const int Bytes = 16;
        public uint Txn; public byte Kind; public int Ns; public uint Hash; public byte State; public ushort BaseVer;
        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put(Txn); w.Put(Kind); w.Put(Ns); w.Put(Hash); w.Put(State); w.Put(BaseVer);
            return w.CopyData();
        }
        public static bool TryParse(byte[] b, out ObjectTxnRequest t)
        {
            t = default(ObjectTxnRequest);
            if (b == null || b.Length != Bytes) return false;
            var r = new NetDataReader(b);
            t.Txn = r.GetUInt(); t.Kind = r.GetByte(); t.Ns = r.GetInt(); t.Hash = r.GetUInt(); t.State = r.GetByte(); t.BaseVer = r.GetUShort();
            return true;
        }
        public override string ToString() { return (ObjectKind)Kind + " " + IdentityNamespace.Name(Ns) + ":" + Hash.ToString("X8") + " -> " + State + " (base v" + BaseVer + ")"; }
    }

    // Host-side transition decision + registry commit for every supported kind (one book per host session).
    public sealed class RegionStateBook
    {
        readonly Dictionary<long, ObjectStateList> _lists = new Dictionary<long, ObjectStateList>();
        readonly Dictionary<byte, double> _windowStart = new Dictionary<byte, double>();
        readonly Dictionary<byte, int> _windowCount = new Dictionary<byte, int>();
        public long Accepted, Unchanged, Stale, Refused, Unverified, Cleared;

        static long K(ObjectKind k, int ns) { return ((long)(byte)k << 32) | (uint)ns; }

        public ObjectStateList List(ObjectKind k, int ns) { ObjectStateList l; return _lists.TryGetValue(K(k, ns), out l) ? l : null; }
        public bool TryGet(ObjectKind k, int ns, uint hash, out ObjectState s) { var l = List(k, ns); s = default(ObjectState); return l != null && l.TryGet(hash, out s); }

        // catalog: null = the host has no census of this namespace (unverified id), else true when the hash is a known,
        // unambiguous object of this kind.
        public ProposalCode Change(byte slot, int slotRegion, ObjectTxnRequest t, Func<ObjectKind, int, uint, bool?> catalog, double now, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            why = null;
            var kind = (ObjectKind)t.Kind;
            if (!ObjectStateRules.SupportedKind(kind) || !ObjectStateRules.SupportedNamespace(kind, t.Ns)) { why = "kind/region not shared in this milestone"; Refused++; return ProposalCode.NotAuthorized; }
            if (slotRegion != t.Ns) { why = "requester is in " + Regions.Name(slotRegion) + ", not in " + IdentityNamespace.Name(t.Ns); Refused++; return ProposalCode.NotAuthorized; }
            if (!ObjectStateRules.ValidState(kind, t.State)) { why = "invalid state " + t.State; Refused++; return ProposalCode.Invalid; }
            bool? known = catalog != null ? catalog(kind, t.Ns, t.Hash) : null;
            if (known == false) { why = "id is not an unambiguous " + kind + " of the host's census of " + IdentityNamespace.Name(t.Ns); Refused++; return ProposalCode.Invalid; }
            if (!RateOk(slot, now)) { why = "rate limit"; Refused++; return ProposalCode.Busy; }
            var list = List(kind, t.Ns);
            ObjectState cur = default(ObjectState); bool exists = list != null && list.TryGet(t.Hash, out cur);
            if (!exists) cur = default(ObjectState);
            if (cur.Ver != t.BaseVer)
            {
                if (exists && ObjectStateRules.SamePhysical(kind, cur.State, t.State)) { why = "already in that state (v" + cur.Ver + ")"; Unchanged++; return ProposalCode.Unchanged; }
                why = "base v" + t.BaseVer + " is not the current v" + cur.Ver + " (another change was committed first)"; Stale++; return ProposalCode.Stale;
            }
            if (exists && ObjectStateRules.SamePhysical(kind, cur.State, t.State)) { why = "unchanged"; Unchanged++; return ProposalCode.Unchanged; }
            if (list == null) { list = new ObjectStateList(); _lists[K(kind, t.Ns)] = list; }
            if (!exists && list.Items.Count >= ObjectStateRules.MaxPerRegion) { why = "region capacity " + ObjectStateRules.MaxPerRegion; Refused++; return ProposalCode.Busy; }
            var next = new ObjectState { Hash = t.Hash, State = t.State, Ver = (ushort)(cur.Ver == ushort.MaxValue ? 1 : cur.Ver + 1), By = slot };
            int i = list.IndexOf(t.Hash);
            if (i >= 0) list.Items[i] = next; else list.Items.Add(next);
            Commit(kind, t.Ns, list, reg, outbox);
            if (known == null) { Unverified++; why = "unverified id (host has no census of " + IdentityNamespace.Name(t.Ns) + ")"; }
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

        void Commit(ObjectKind k, int ns, ObjectStateList list, StateRegistry reg, List<StateEntry> outbox)
        {
            var e = reg.Commit(StateScope.Region, ns, ObjectStateRules.RegistryKey(k), list.ToBytes());
            if (e != null) outbox.Add(e);
        }

        // The region emptied: RegionLife objects return to the original scene state (vanilla reload), Campaign objects stay.
        public void ClearRegion(int ns, StateRegistry reg, List<StateEntry> outbox)
        {
            foreach (var kv in new List<KeyValuePair<long, ObjectStateList>>(_lists))
            {
                var kind = (ObjectKind)(byte)(kv.Key >> 32);
                if ((int)(uint)kv.Key != ns || ObjectStateRules.Lifetime(kind) != ObjectLifetime.RegionLife || kv.Value.Items.Count == 0) continue;
                kv.Value.Items.Clear();
                Commit(kind, ns, kv.Value, reg, outbox);
                Cleared++;
            }
        }

        // ---- Campaign lifetime (durable under the host campaign; no Campaign kind is shared yet - used by W4.2B) ----
        public byte[] SerializeCampaign(int historyId)
        {
            var w = new NetDataWriter();
            w.Put((byte)1); w.Put(historyId);
            var keep = new List<KeyValuePair<long, ObjectStateList>>();
            foreach (var kv in _lists) if (ObjectStateRules.Lifetime((ObjectKind)(byte)(kv.Key >> 32)) == ObjectLifetime.Campaign && kv.Value.Items.Count > 0) keep.Add(kv);
            w.Put(keep.Count);
            foreach (var kv in keep) { w.Put(kv.Key); w.PutBytesWithLength(kv.Value.ToBytes()); }
            return w.CopyData();
        }

        public bool LoadCampaign(byte[] b, int historyId, StateRegistry reg, List<StateEntry> outbox, out string err)
        {
            err = null;
            try
            {
                var r = new NetDataReader(b);
                if (r.GetByte() != 1) { err = "version"; return false; }
                if (r.GetInt() != historyId) { err = "other save history"; return false; }
                int n = r.GetInt();
                if (n < 0 || n > 4096) { err = "count"; return false; }
                var loaded = new List<KeyValuePair<long, ObjectStateList>>();
                for (int i = 0; i < n; i++)
                {
                    long key = r.GetLong(); ObjectStateList l;
                    if (!ObjectStateList.TryParse(r.GetBytesWithLength(), out l)) { err = "list " + i; return false; }
                    if (ObjectStateRules.Lifetime((ObjectKind)(byte)(key >> 32)) != ObjectLifetime.Campaign) continue;
                    loaded.Add(new KeyValuePair<long, ObjectStateList>(key, l));
                }
                foreach (var kv in loaded) { _lists[kv.Key] = kv.Value; Commit((ObjectKind)(byte)(kv.Key >> 32), (int)(uint)kv.Key, kv.Value, reg, outbox); }
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name; return false; }
        }

        public string Stats { get { return "accepted=" + Accepted + " unchanged=" + Unchanged + " stale=" + Stale + " refused=" + Refused + " unverified=" + Unverified + " cleared=" + Cleared; } }
    }

    // Client/observer side, pure: what to do with one local object given the committed state (WorldDoorSync).
    public enum DoorApply : byte { None, Swing, SkipBusy, SkipLocked, SkipPending }
    public static class WorldDoorRules
    {
        // desired: committed state; localOpen: Door.isOpen; moving: Door.opening (the original swing in progress);
        // localUse: the local player holds this door (PlayerScript.assignDoor); usable: tag "Door" (unlocked here);
        // pending: this instance has an unanswered proposal for the door.
        public static DoorApply Decide(byte desired, bool localOpen, bool moving, bool localUse, bool usable, bool pending)
        {
            bool open = (desired & ObjectStateRules.DoorOpen) != 0;
            if (open == localOpen) return DoorApply.None;
            if (pending) return DoorApply.SkipPending;
            if (moving || localUse) return DoorApply.SkipBusy;
            if (!usable) return DoorApply.SkipLocked;
            return DoorApply.Swing;
        }
    }
}
