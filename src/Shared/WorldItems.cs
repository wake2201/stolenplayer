// M5 W3B shared dropped items (pure logic, offline-tested).
// Verified original model (Cecil IL, Assembly-CSharp 1.27c, read-only):
//  - Inventory.DropItemNewSystem (only reached from the inventory UI): Instantiate(ItemID.drop_object, Inventory.dropPos.position,
//    drop_object.rotation), Pickupable.item_ID = ItemID.item_ID, wasDropped = true, x10XP = true when ItemID.x10xp_amount_of > 0
//    else GameObject name "dropped", destroy_if_was_picked = false, SetActive(true), Rigidbody dynamic (gravity, continuous
//    collision; added when missing), then ItemID.Drop_Item (item_amount - 1, weight, drone/stolen-car side effects).
//  - Pickup: PlayerScript.Update raycast branch (weight / max_amount checks, IL_7bcd-IL_7c6c) -> Pickupable.PickUp ->
//    <PickingItem>d__72 (colliders off, sound, ItemID.Item_Picked(name, xp, wasDropped, special): +1 backpack, no XP for a
//    dropped item) -> Destroy(gameObject).
//  - Dropped items are NOT saved: a MainScene reload destroys them. The only conversion is GameManager.TryToMoveDroppedItemsToStorage
//    (every "Pickupable"-tagged object with wasDropped && !is_heavy_item -> ItemID.MoveToStorageOnDrop: hideout locker + 1
//    unless max_amount > 0; the object itself stays), called by SaveGame.SaveGameNewSystem IL_042a-IL_0448 only when
//    GameManager.current_location == 1, which GameManager.Start sets for Hillville (currentLoc 97, IL_0494). The hideout sets -1.
// Multiplayer model: the host owns one registry of dropped items per region (registry key "witems", Region scope, arg =
// region): host-assigned id, item type, origin player + origin transaction, rest pose, simulating player. Every instance
// presents the registry items of its own region with the original drop_object prefab; claiming is a host transaction with
// exactly one winner. A region that becomes empty loses its items (= the original scene reload).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum ItemOp : byte { Drop = 1, Pickup = 2, Settle = 3, Convert = 4 }

    public struct Quat16
    {
        public short X, Y, Z, W;
        public static Quat16 From(float x, float y, float z, float w)
        {
            float n = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (n < 1e-6f || float.IsNaN(n)) return new Quat16 { W = 32767 };
            return new Quat16 { X = Q(x / n), Y = Q(y / n), Z = Q(z / n), W = Q(w / n) };
        }
        static short Q(float v) { return (short)Math.Round(Math.Max(-1f, Math.Min(1f, v)) * 32767f); }
        public float Fx { get { return X / 32767f; } }
        public float Fy { get { return Y / 32767f; } }
        public float Fz { get { return Z / 32767f; } }
        public float Fw { get { return W / 32767f; } }
        public void Write(NetDataWriter w) { w.Put(X); w.Put(Y); w.Put(Z); w.Put(W); }
        public static Quat16 Read(NetDataReader r) { return new Quat16 { X = r.GetShort(), Y = r.GetShort(), Z = r.GetShort(), W = r.GetShort() }; }
    }

    // One dropped item in the shared world.
    public struct WorldItem
    {
        public uint Id;          // host-assigned, unique per host registry epoch
        public int ItemId;       // ItemID.item_ID (type)
        public byte Origin;      // slot that dropped it
        public uint OriginTxn;   // Player-2 transaction id (origin 2) / host sequence (origin 1)
        public Vec3 Pos;
        public Quat16 Rot;
        public byte Sim;         // slot whose instance simulates the original rigidbody (0 = nobody: presented at rest)
        // W3.2 lifecycle: false = Falling (Pos is the original drop point, Inventory.dropPos, in the air: the dropper's physics
        // has not run yet - the inventory is the original pause menu, PlayerScript.EnableInventory IL_00b0 timeScale 0);
        // true = Settled (Pos is the rest pose the simulating instance reported). Packed into bit 7 of the Sim byte
        // (protocol 19), so the record stays 34 B and 30 items still fit one registry value.
        public bool Settled;
        public const int WireSize = 34;
        const byte SettledBit = 0x80;

        public void Write(NetDataWriter w) { w.Put(Id); w.Put(ItemId); w.Put(Origin); w.Put(OriginTxn); w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); Rot.Write(w); w.Put((byte)((Sim & 0x7F) | (Settled ? SettledBit : 0))); }
        public static WorldItem Read(NetDataReader r)
        {
            var i = new WorldItem { Id = r.GetUInt(), ItemId = r.GetInt(), Origin = r.GetByte(), OriginTxn = r.GetUInt() };
            i.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat()); i.Rot = Quat16.Read(r);
            byte b = r.GetByte(); i.Sim = (byte)(b & 0x7F); i.Settled = (b & SettledBit) != 0;
            return i;
        }
        public override string ToString() { return "#" + Id + " item " + ItemId + " P" + Origin + (Settled ? " settled" : " falling") + " at " + Pos; }
    }

    // Registry value of one region: version, count, items. 2 + 34 x 30 = 1022 B (registry value limit 1024 B).
    public static class WorldItemList
    {
        public const int MaxPerRegion = 30;
        public static byte[] Encode(List<WorldItem> items)
        {
            var w = new NetDataWriter(); w.Put((byte)1); w.Put((byte)items.Count);
            foreach (var i in items) i.Write(w);
            return w.CopyData();
        }
        public static bool TryParse(byte[] v, out List<WorldItem> items)
        {
            items = null;
            if (v == null || v.Length < 2 || v[0] != 1) return false;
            int n = v[1];
            if (n > MaxPerRegion || v.Length != 2 + n * WorldItem.WireSize) return false;
            try
            {
                var r = new NetDataReader(v, 2, v.Length);
                var l = new List<WorldItem>(n); var seen = new HashSet<uint>();
                for (int k = 0; k < n; k++)
                {
                    var i = WorldItem.Read(r);
                    if (i.Id == 0 || !seen.Add(i.Id) || !Finite(i.Pos) || i.Origin == 0 || i.Origin > 2 || i.Sim > 2) return false;
                    l.Add(i);
                }
                items = l; return true;
            }
            catch { return false; }
        }
        internal static bool Finite(Vec3 p)
        {
            return !float.IsNaN(p.X) && !float.IsNaN(p.Y) && !float.IsNaN(p.Z) && Math.Abs(p.X) <= SnapshotValidation.MaxCoordinate
                && Math.Abs(p.Y) <= SnapshotValidation.MaxCoordinate && Math.Abs(p.Z) <= SnapshotValidation.MaxCoordinate;
        }
    }

    // Proposal "itemTxn" (Session scope, arg 0) and the host's own in-place call. 37 bytes.
    //  Drop:    Txn = Player-2 transaction id (client) / host sequence (host), ItemId, Region, Pos/Rot = drop pose.
    //  Pickup:  Txn = Player-2 transaction id (client) / 0 (host), WorldId, Region.
    //  Settle:  WorldId, Region, Pos/Rot = rest pose reported by the simulating instance.
    //  Convert: Region = the region whose dropped items the original save converts into the hideout locker.
    public struct ItemTxnRequest
    {
        public ItemOp Op; public uint Txn; public uint WorldId; public int ItemId; public int Region; public Vec3 Pos; public Quat16 Rot;
        public const int WireSize = 37;
        public byte[] Encode()
        {
            var w = new NetDataWriter(); w.Put((byte)Op); w.Put(Txn); w.Put(WorldId); w.Put(ItemId); w.Put(Region);
            w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); Rot.Write(w);
            return w.CopyData();
        }
        public static bool TryParse(byte[] v, out ItemTxnRequest t)
        {
            t = default(ItemTxnRequest);
            if (v == null || v.Length != WireSize || v[0] < 1 || v[0] > 4) return false;
            try
            {
                var r = new NetDataReader(v);
                t.Op = (ItemOp)r.GetByte(); t.Txn = r.GetUInt(); t.WorldId = r.GetUInt(); t.ItemId = r.GetInt(); t.Region = r.GetInt();
                t.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat()); t.Rot = Quat16.Read(r);
                return WorldItemList.Finite(t.Pos);
            }
            catch { return false; }
        }
    }

    // W3.1: what happens when the local player presses the original Drop (decided BEFORE the original changes the inventory).
    public enum DropDecision { Original, Refuse, HostOriginal, ClientRequest }

    public static class DropGate
    {
        // live = connected + registry synced; shareable = ordinary backpack item; regionCount = items already in the region;
        // busy = another Player-2 transaction in flight (client); restored = Player-2 inventory synchronized (client).
        public static DropDecision Decide(bool live, bool host, bool shareable, int region, int regionCount, bool busy, bool restored, out string why)
        {
            why = null;
            if (!live) { why = "not connected: original single-player drop"; return DropDecision.Original; }
            if (!shareable) { why = "special original item: original local drop"; return DropDecision.Original; }
            if (region == Regions.None) { why = "region not confirmed (loading)"; return DropDecision.Refuse; }
            if (regionCount >= WorldItemList.MaxPerRegion) { why = "region item limit " + WorldItemList.MaxPerRegion + " reached"; return DropDecision.Refuse; }
            if (host) return DropDecision.HostOriginal;
            if (busy) { why = "another Player-2 transaction is in flight"; return DropDecision.Refuse; }
            if (!restored) { why = "Player-2 inventory not synchronized"; return DropDecision.Refuse; }
            return DropDecision.ClientRequest;
        }
    }

    // W3.1: binds the object created by the ORIGINAL drop. Armed in the drop prefix, fed by the hook on the original
    // Instantiate call (the first one only), taken in the postfix after the original initialized the object. W3 looked the
    // object up by the "Pickupable" tag, which Pickupable.<Start> assigns only after WaitForSecondsRealtime(0.05) -> never found.
    public sealed class DropCapture
    {
        object _item, _seen;
        public bool Armed { get { return _item != null; } }
        public void Arm(object item) { _item = item; _seen = null; }
        public void Seen(object created) { if (_item != null && _seen == null) _seen = created; }
        // returns the created object (null = the original created none while armed); always disarms
        public object Take(out object item) { item = _item; var s = _seen; _item = null; _seen = null; return s; }
    }

    // W3.2: when the simulating (dropper's) instance reports the rest pose of its ORIGINAL object, and how an observer shows
    // an item that has not settled yet. Pure, offline-tested; the Unity side feeds it (WorldItemSync.SettleFrame / Present).
    // Verified original facts it relies on (Cecil IL, Assembly-CSharp 1.27c):
    //  - every backpack drop happens inside the inventory = pause menu (Inventory.DropItemNewSystem is reached only from the
    //    UI; PlayerScript.EnableInventory IL_00b0 Time.timeScale = 0, DisableInventory sets 1): the original object is created
    //    at once at Inventory.dropPos with a dynamic Rigidbody, but it cannot fall before the menu closes;
    //  - Pickupable.<Start>d__68 (after WaitForSecondsRealtime 0.05) sets thisRigid + deleteRigidInTime for non-heavy items;
    //    Pickupable.Update IL_0181-IL_01be then DESTROYS the Rigidbody as soon as it IsSleeping() or isKinematic: the
    //    original's own "at rest" decision; afterwards the item is a static collider object.
    public static class SettleRules
    {
        public const float MoveEpsSqr = 0.0009f;   // 3 cm: a re-report after the first one needs a real movement
        public const float TurnEpsDeg = 5f;
        public const float SlowSpeedSqr = 0.0004f; // 2 cm/s
        public const double MinUnpaused = 0.2;     // at least ~10 physics steps since the menu closed (velocity is 0 while paused)

        // Owner: report now? rbGone = the original destroyed the Rigidbody (rest); unpausedFor = seconds of timeScale > 0
        // since the drop (0 while paused).
        public static bool OwnerShouldReport(bool reported, bool paused, double unpausedFor, bool rbGone, bool rbSleeping, bool rbKinematic,
                                             float speedSqr, float movedSqr, float turnedDeg, out string why)
        {
            why = null;
            if (paused) { why = "physics paused (original inventory / pause menu)"; return false; }
            bool resting = rbGone || rbKinematic || (unpausedFor >= MinUnpaused && (rbSleeping || speedSqr < SlowSpeedSqr));
            if (!resting) { why = "falling"; return false; }
            why = rbGone ? "original removed the rigidbody (asleep)" : rbSleeping ? "rigidbody asleep" : rbKinematic ? "kinematic" : "slow";
            if (!reported) return true;                                // first rest: always reported (marks the item Settled)
            return movedSqr >= MoveEpsSqr || turnedDeg >= TurnEpsDeg;  // later: only a real movement
        }

        // Observer: a Falling item is shown on the surface below its drop point (prompt, never a solid object in the air);
        // a Settled item exactly at the reported rest pose (tables, shelves, slopes keep their real pose).
        public static bool ProjectToSurface(bool settled) { return !settled; }

        // Vertical correction from the item's lowest collider point to the surface hit straight below its centre.
        // Returns false (keep the reported pose) when nothing valid was hit. The lift is capped so an item never jumps through
        // solid geometry onto something above it; the drop is capped by the cast length.
        public const float MaxLift = 0.25f, MaxDrop = 6f;
        public static bool SurfaceOffset(float itemBottomY, bool hit, float surfaceY, out float dy)
        {
            dy = 0f;
            if (!hit || float.IsNaN(surfaceY) || float.IsInfinity(surfaceY)) return false;
            float d = surfaceY - itemBottomY;
            if (d > MaxLift || d < -MaxDrop) return false;
            dy = d; return true;
        }
    }

    // W3.2 diagnostics: detailed lines for the first few dropped items of a session only (by world id), never per frame.
    public sealed class DiagBudget
    {
        readonly HashSet<uint> _ids = new HashSet<uint>(); readonly int _max;
        public DiagBudget(int max) { _max = max; }
        public bool Want(uint id) { if (id == 0) return false; if (_ids.Contains(id)) return true; if (_ids.Count >= _max) return false; _ids.Add(id); return true; }
        public int Count { get { return _ids.Count; } }
    }

    // Host-only registry of dropped items. Pure: validity, single-winner claims, region lifetime, registry commits.
    public sealed class WorldItemBook
    {
        readonly Dictionary<int, List<WorldItem>> _byRegion = new Dictionary<int, List<WorldItem>>();
        uint _next;
        public long Drops, Pickups, Settles, Converted, Cleared, Refused;
        public uint LastApplied { get; private set; }   // id of the item changed by the last Apply (new id for a drop)

        public int CountIn(int region) { List<WorldItem> l; return _byRegion.TryGetValue(region, out l) ? l.Count : 0; }
        public List<WorldItem> InRegion(int region) { List<WorldItem> l; return _byRegion.TryGetValue(region, out l) ? new List<WorldItem>(l) : new List<WorldItem>(); }
        public int Total { get { int n = 0; foreach (var l in _byRegion.Values) n += l.Count; return n; } }

        public bool TryGet(uint id, out WorldItem item, out int region)
        {
            foreach (var kv in _byRegion)
                foreach (var i in kv.Value) if (i.Id == id) { item = i; region = kv.Key; return true; }
            item = default(WorldItem); region = Regions.None; return false;
        }

        // Validity only (no change). region = the requester's committed region.
        public ProposalCode Check(byte slot, int region, ItemTxnRequest t, out string why)
        {
            why = null;
            if (slot < 1 || slot > 2) { why = "slot"; return ProposalCode.Invalid; }
            if (region == Regions.None || t.Region != region) { Refused++; why = "not in that region"; return ProposalCode.NotAuthorized; }
            WorldItem it; int r;
            switch (t.Op)
            {
                case ItemOp.Drop:
                    if (CountIn(region) >= WorldItemList.MaxPerRegion) { Refused++; why = "region item limit"; return ProposalCode.Busy; }
                    return ProposalCode.Accepted;
                case ItemOp.Pickup:
                    if (!TryGet(t.WorldId, out it, out r) || r != region) { Refused++; why = "already claimed or gone"; return ProposalCode.Stale; }
                    return ProposalCode.Accepted;
                case ItemOp.Settle:
                    if (!TryGet(t.WorldId, out it, out r) || r != region) { why = "gone"; return ProposalCode.Stale; }
                    if (it.Sim != slot) { why = "not the simulating player"; return ProposalCode.NotAuthorized; }
                    return ProposalCode.Accepted;
                case ItemOp.Convert:
                    return ProposalCode.Accepted;
                default: why = "op"; return ProposalCode.Invalid;
            }
        }

        // Applies an accepted transaction (after the owner side was committed) and commits the region list.
        public uint Apply(byte slot, int region, ItemTxnRequest t, StateRegistry reg, List<StateEntry> outbox)
        {
            List<WorldItem> l;
            if (!_byRegion.TryGetValue(region, out l)) { l = new List<WorldItem>(); _byRegion[region] = l; }
            uint id = 0;
            switch (t.Op)
            {
                case ItemOp.Drop:
                    id = ++_next; if (id == 0) id = ++_next;
                    l.Add(new WorldItem { Id = id, ItemId = t.ItemId, Origin = slot, OriginTxn = t.Txn, Pos = t.Pos, Rot = t.Rot, Sim = slot });
                    Drops++; break;
                case ItemOp.Pickup:
                    id = t.WorldId; l.RemoveAll(i => i.Id == t.WorldId); Pickups++; break;
                case ItemOp.Settle:
                    for (int k = 0; k < l.Count; k++) if (l[k].Id == t.WorldId) { var i = l[k]; i.Pos = t.Pos; i.Rot = t.Rot; i.Settled = true; l[k] = i; id = i.Id; }
                    Settles++; break;
                case ItemOp.Convert:
                    return 0;   // the host plugin removes the converted items one by one (Remove)
            }
            LastApplied = id;
            Commit(region, l, reg, outbox);
            return id;
        }

        // Converted into the host locker by the original save conversion (or removed for another host reason).
        public bool Remove(uint id, StateRegistry reg, List<StateEntry> outbox)
        {
            WorldItem it; int r;
            if (!TryGet(id, out it, out r)) return false;
            var l = _byRegion[r]; l.RemoveAll(i => i.Id == id); Converted++;
            Commit(r, l, reg, outbox);
            return true;
        }

        // The simulating player left: the item stays where it settled, presented at rest by everybody.
        public void ReleaseSim(byte slot, StateRegistry reg, List<StateEntry> outbox)
        {
            foreach (var kv in _byRegion)
            {
                bool changed = false; var l = kv.Value;
                for (int k = 0; k < l.Count; k++) if (l[k].Sim == slot) { var i = l[k]; i.Sim = 0; l[k] = i; changed = true; }
                if (changed) Commit(kv.Key, l, reg, outbox);
            }
        }

        // Nobody is in the region any more: the original scene reload destroys every dropped item there.
        public int ClearRegion(int region, StateRegistry reg, List<StateEntry> outbox)
        {
            List<WorldItem> l;
            if (!_byRegion.TryGetValue(region, out l) || l.Count == 0) return 0;
            int n = l.Count; l.Clear(); Cleared += n;
            Commit(region, l, reg, outbox);
            return n;
        }

        static void Commit(int region, List<WorldItem> l, StateRegistry reg, List<StateEntry> outbox)
        {
            var e = reg.Commit(StateScope.Region, region, WorldKeys.WorldItems, WorldItemList.Encode(l));
            if (e != null) outbox.Add(e);
        }
    }
}
