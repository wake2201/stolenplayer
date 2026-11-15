// H1 heavy world objects: host-authoritative ownership lease (pure logic, offline-tested in tests/H1Tests.cs).
// Verified original model (ilspycmd decompile of Assembly-CSharp 1.27c, read-only; details docs/PROGRESS.md H1 §B):
//  - Pickup: PlayerScript.Update interact branch (tag "Pickupable", !isCarrying, !is_in_vehicle) plays SwitchWepSound, then
//    Pickupable.PickUpHeavy(hit distance) - its only caller; codenameAI.PickHeavy instead while the associate's heavy mode is on.
//    PickUpHeavy alone sets every carry field: parent null, tag "HeavyCarried", layer 11, trigger colliders, Rigidbody
//    kinematic without gravity, PS.canInteract false, PS.CarryItem, PS.isCarrying, CarryItemInfo / heavy_drop_prompt, isCarried.
//    No key, no reward, no inventory change.
//  - Carry: PlayerScript.Update lerps CarryItem to carryPos[CarryItem_PositionArray] every frame; interact / throw ->
//    StopCarryingItem(100 / 300) unless heavyObstructed or dropBigItemBlock.
//  - Drop: Pickupable.DropHeavy (callers StopCarryingItem, GetTazed): tag "Pickupable", layer 15, SetTriggerCollider(false),
//    Rigidbody useGravity + !isKinematic + Interpolate, PS.isCarrying = false, isCarried = false. It does NOT move the object:
//    the release pose is the carry pose. Only AFTER it returns does the caller add the throw force (StopCarryingItem:
//    AddForce(forward * power) + AddForce(up * power / 2), then CarryItem = null; GetTazed: AddForce(forward * 100)).
//    The dropped object keeps its Rigidbody (deleteRigidInTime is false for heavy items) and is NOT saved: a scene reload
//    restores the scene object at its original pose.
//  - Deposits: Place_Heavy_Trigger / Shop_Heavy_Trigger.OnTriggerEnter -> PlayerScript.HeavyItemPlacedInCar
//    -> Pickupable.PlacedInTrunk (+ CarController.HeavyItemPlaced / shop pose); VanHeavyItems (Ranger cargo, sensor at save);
//    HeavyItemsShed (sensor at save); CodenameAI.PickHeavy (associate carries it to the car).
//    H2: the Ranger cargo bed (VanHeavyItems) is shared (src/Shared/HeavyCargo.cs): Load moves a held record into the car's
//    host cargo list; a cargo object taken out again is a record with a synthetic id (HeavyCargo.Synthetic) - same lease rules.
// Multiplayer model: one record per touched census heavy object of a region (registry "wheavy", Region scope, arg = region
// namespace). The host grants exactly one lease per object (first committed wins: the host's own pickup is decided inside its
// PickUpHeavy call, the client's proposal when it arrives). The lease holder runs the ORIGINAL carry and, after DropHeavy, the
// ORIGINAL physics alone.
// H1.1 drop lifecycle: the holder commits the release pose right after DropHeavy (Drop: Placed + Moving, simulated by the
// dropper) - observers reveal the object there at once; while it falls / slides the holder streams bounded HeavyMotion
// snapshots (unreliable, lease-checked, observers interpolate the kinematic object); at rest it commits the exact pose
// (Settle: Moving cleared) and stops sending. A later real movement starts a new Drop -> motion -> Settle cycle.
// Every other instance presents the scene object from the record: hidden while carried (the remote avatar shows it), kinematic
// at the committed / streamed pose while placed. No record = the original scene pose. A region that empties loses its records
// (= the original scene reload restores every heavy object). A simulator that leaves while its object is still Moving hands the
// simulation to the other player when that player is in the region (its instance continues the original physics from the pose
// it shows), otherwise the record is removed (= the original reload: scene pose).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum HeavyState : byte { Carried = 1, Placed = 2, Removed = 3 }
    // Drop = the original release happened at Pos (H1.1: committed at once, the object is Moving); Settle = exact rest pose.
    // Load (H2) = the holder's object came to rest in a supported car's cargo bed (Digest = vehicle id, Pos / Rot = car-local pose).
    public enum HeavyOp : byte { Lease = 1, Drop = 2, Release = 3, Remove = 4, Settle = 5, Load = 6 }

    // One touched heavy object. Actor: Carried -> carrier slot; Placed -> simulating slot (0 = nobody, presented at rest);
    // Removed -> 0. HasPose: the object has a committed pose (Carried keeps the previous one: restored if the carry ends
    // without a drop). Moving (H1.1, Placed only): the simulating slot's original physics still moves it - observers follow its
    // HeavyMotion snapshots; cleared by the Settle (exact rest pose). Lease: host-assigned id of the current holder's lease
    // (stale-request / stale-snapshot guard), never 0.
    public struct HeavyRecord
    {
        public uint Hash;
        public HeavyState State;
        public byte Actor;
        public ushort Lease;
        public bool HasPose;
        public bool Moving;
        public Vec3 Pos;
        public Quat16 Rot;
        public bool Cargo;      // H2: a cargo object taken out of its car (Hash = HeavyCargo.Synthetic id, not a census hash)
        public const int WireSize = 29;
        const byte PoseBit = 1, MovingBit = 2, CargoBit = 4;

        public void Write(NetDataWriter w) { w.Put(Hash); w.Put((byte)State); w.Put(Actor); w.Put(Lease); w.Put((byte)((HasPose ? PoseBit : 0) | (Moving ? MovingBit : 0) | (Cargo ? CargoBit : 0))); w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); Rot.Write(w); }
        public static bool TryRead(NetDataReader r, out HeavyRecord h)
        {
            h = new HeavyRecord { Hash = r.GetUInt(), State = (HeavyState)r.GetByte(), Actor = r.GetByte(), Lease = r.GetUShort() };
            byte f = r.GetByte();
            h.HasPose = (f & PoseBit) != 0; h.Moving = (f & MovingBit) != 0; h.Cargo = (f & CargoBit) != 0;
            h.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat()); h.Rot = Quat16.Read(r);
            return (f & ~(PoseBit | MovingBit | CargoBit)) == 0 && (!h.Moving || (h.State == HeavyState.Placed && h.HasPose && h.Actor != 0))
                && (!h.Cargo || (HeavyCargo.IsSynthetic(h.Hash) && h.State != HeavyState.Removed));
        }
        public bool CarriedBy(byte slot) { return State == HeavyState.Carried && Actor == slot; }
        // the slot's original physics still moves it (observers follow its snapshots)
        public bool MovingBy(byte slot) { return State == HeavyState.Placed && Moving && Actor == slot; }
        public override string ToString() { return Hash.ToString("X8") + (Cargo ? " cargo" : "") + " " + State + (State == HeavyState.Removed ? "" : " P" + Actor) + " lease " + Lease + (Moving ? " moving" : "") + (HasPose ? " at " + Pos : " (scene pose)"); }
    }

    // Registry value of one region: version 3 (H1.1: Moving bit, H2: Cargo bit), count, records. 2 + 29 x 32 = 930 B (limit 1024 B).
    public static class HeavyList
    {
        public const int MaxPerRegion = 32;
        const byte Version = 3;
        public static byte[] Encode(List<HeavyRecord> l)
        {
            var w = new NetDataWriter(); w.Put(Version); w.Put((byte)l.Count);
            foreach (var h in l) h.Write(w);
            return w.CopyData();
        }
        public static bool TryParse(byte[] v, out List<HeavyRecord> l)
        {
            l = null;
            if (v == null || v.Length < 2 || v[0] != Version) return false;
            int n = v[1];
            if (n > MaxPerRegion || v.Length != 2 + n * HeavyRecord.WireSize) return false;
            try
            {
                var r = new NetDataReader(v); r.GetByte(); r.GetByte();
                l = new List<HeavyRecord>(n);
                for (int i = 0; i < n; i++)
                {
                    HeavyRecord h;
                    if (!HeavyRecord.TryRead(r, out h)) { l = null; return false; }
                    if (h.State < HeavyState.Carried || h.State > HeavyState.Removed || h.Actor > 2 || !WorldItemList.Finite(h.Pos)) { l = null; return false; }
                    l.Add(h);
                }
                return true;
            }
            catch { l = null; return false; }
        }
    }

    // Client proposal "heavyTxn" (Session scope, arg 0). Digest = the requester's census digest of that region (Lease of a census
    // object; 0 for a synthetic cargo id) or the vehicle id (Load: Pos / Rot are then car-local).
    // Cargo (H2, bit 0x80 of the op byte): Hash is a synthetic cargo id (HeavyCargo.Synthetic), never a census hash.
    public struct HeavyTxnRequest
    {
        public HeavyOp Op; public int Ns; public uint Hash; public uint Digest; public ushort Lease; public Vec3 Pos; public Quat16 Rot; public bool Cargo;
        public const int WireSize = 35;
        const byte CargoFlag = 0x80;
        public byte[] Encode()
        {
            var w = new NetDataWriter(); w.Put((byte)((byte)Op | (Cargo ? CargoFlag : 0))); w.Put(Ns); w.Put(Hash); w.Put(Digest); w.Put(Lease);
            w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); Rot.Write(w);
            return w.CopyData();
        }
        public static bool TryParse(byte[] v, out HeavyTxnRequest t)
        {
            t = default(HeavyTxnRequest);
            if (v == null || v.Length != WireSize) return false;
            int op = v[0] & ~CargoFlag;
            if (op < 1 || op > 6) return false;
            try
            {
                var r = new NetDataReader(v);
                r.GetByte(); t.Op = (HeavyOp)op; t.Cargo = (v[0] & CargoFlag) != 0; t.Ns = r.GetInt(); t.Hash = r.GetUInt(); t.Digest = r.GetUInt(); t.Lease = r.GetUShort();
                t.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat()); t.Rot = Quat16.Read(r);
                return WorldItemList.Finite(t.Pos) && (!t.Cargo || HeavyCargo.IsSynthetic(t.Hash));
            }
            catch { return false; }
        }
        public override string ToString() { return Op + " " + IdentityNamespace.Name(Ns) + ":" + (Cargo ? "cargo " : "") + Hash.ToString("X8") + (Op == HeavyOp.Lease ? "" : " lease " + Lease) + (Op == HeavyOp.Drop || Op == HeavyOp.Settle ? " at " + Pos : "") + (Op == HeavyOp.Load ? " into vehicle " + Digest + " at local " + Pos : ""); }
    }

    // H1.1 movement snapshot of one heavy object from its simulating holder (MsgType.HeavyMotion, unreliable: the per-object
    // sequence and the record's lease reject old / reordered / foreign packets). 40 B incl. the message type. Final = the
    // last snapshot, sent together with the Settle (same exact pose).
    public struct HeavyMotion
    {
        public int Ns; public uint Hash; public ushort Lease; public uint Seq; public uint TimeMs; public Vec3 Pos; public Quat16 Rot; public bool Final;
        public const int WireSize = 40;

        public void Write(NetDataWriter w)
        {
            w.Put((byte)MsgType.HeavyMotion); w.Put(Ns); w.Put(Hash); w.Put(Lease); w.Put(Seq); w.Put(TimeMs);
            w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); Rot.Write(w); w.Put((byte)(Final ? 1 : 0));
        }

        // Reader positioned after the message type byte.
        public static bool TryRead(NetDataReader r, out HeavyMotion m)
        {
            m = default(HeavyMotion);
            if (r.AvailableBytes != WireSize - 1) return false;
            m.Ns = r.GetInt(); m.Hash = r.GetUInt(); m.Lease = r.GetUShort(); m.Seq = r.GetUInt(); m.TimeMs = r.GetUInt();
            m.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat()); m.Rot = Quat16.Read(r);
            byte f = r.GetByte(); m.Final = f == 1;
            float n = m.Rot.Fx * m.Rot.Fx + m.Rot.Fy * m.Rot.Fy + m.Rot.Fz * m.Rot.Fz + m.Rot.Fw * m.Rot.Fw;
            return f <= 1 && m.Lease != 0 && n > 0.9f && n < 1.1f && WorldItemList.Finite(m.Pos)
                && Math.Abs(m.Pos.X) <= SnapshotValidation.MaxCoordinate && Math.Abs(m.Pos.Y) <= SnapshotValidation.MaxCoordinate && Math.Abs(m.Pos.Z) <= SnapshotValidation.MaxCoordinate;
        }
        public Quat RotQ { get { return new Quat(Rot.Fx, Rot.Fy, Rot.Fz, Rot.Fw).Normalized(); } }
    }

    public enum HeavyMotionVerdict { Buffer, Follow, Stale, NotHolder, WrongRegion, NoRecord, NotMoving }

    // Observer acceptance of one HeavyMotion: the sender is the record's current simulating holder with this very lease
    // (an older lease / another player's lease / a later pickup, storage or reset reject it), the object is in the observer's
    // region, the sequence is newer per object and lease. Buffer = the record still says Carried by the sender (its reliable
    // Drop commit is in flight; the unreliable snapshot overtook it) - buffered, not shown until the Drop arrives. Pure.
    public sealed class HeavyMotionFilter
    {
        struct Last { public ushort Lease; public uint Seq; }
        readonly Dictionary<uint, Last> _last = new Dictionary<uint, Last>();
        public const int MaxTracked = 64;
        public long Stale, Rejected;

        public HeavyMotionVerdict Check(HeavyMotion m, bool hasRecord, HeavyRecord r, byte sender, int localRegion)
        {
            if (localRegion == Regions.None || m.Ns != localRegion) { Rejected++; return HeavyMotionVerdict.WrongRegion; }
            if (!hasRecord || r.Hash != m.Hash) { Rejected++; return HeavyMotionVerdict.NoRecord; }
            if (r.Actor != sender || r.Lease != m.Lease || r.State == HeavyState.Removed) { Rejected++; return HeavyMotionVerdict.NotHolder; }
            bool carried = r.State == HeavyState.Carried;
            if (!carried && !r.Moving) { Rejected++; return HeavyMotionVerdict.NotMoving; }   // settled: a late snapshot never moves it again
            Last l;
            if (_last.TryGetValue(m.Hash, out l) && l.Lease == m.Lease && !SnapshotValidation.IsNewer(m.Seq, l.Seq)) { Stale++; return HeavyMotionVerdict.Stale; }
            if (!_last.ContainsKey(m.Hash) && _last.Count >= MaxTracked) _last.Clear();
            _last[m.Hash] = new Last { Lease = m.Lease, Seq = m.Seq };
            return carried ? HeavyMotionVerdict.Buffer : HeavyMotionVerdict.Follow;
        }

        public void Clear() { _last.Clear(); }
        public int Tracked { get { return _last.Count; } }
    }

    // Supported objects: census heavy objects of a region scene (never MainScene: it reloads at every region change of
    // either player, so one shared record could not follow the original reset; MainScene heavy objects keep the v0.16.1 rule).
    public static class HeavyRules
    {
        public const LootFlags Unusable = LootFlags.Ambiguous;   // a census id that does not resolve uniquely is never leased
        public static bool Supported(int ns, LootFlags flags)
        {
            return ns != Regions.None && ns != IdentityNamespace.MainScene && (flags & LootFlags.Heavy) != 0 && (flags & Unusable) == 0;
        }

        // Client, at the host's grant: the original pickup still makes sense now (the player may have moved, entered a car,
        // started another carry or opened a menu during the round trip). Same preconditions as the original interact branch
        // (PlayerScript.Update: !isCarrying, !is_in_vehicle, PMB enabled, timeScale > 0) plus the reach of its raycast with a
        // margin for the round trip.
        public const float MaxGrantDistance = 3.5f;
        public static string GrantBlock(bool objectAlive, bool carrying, bool inVehicle, bool controls, bool paused, float distance)
        {
            if (!objectAlive) return "object gone";
            if (carrying) return "already carrying";
            if (inVehicle) return "in a vehicle";
            if (!controls) return "player controls disabled";
            if (paused) return "game paused";
            if (distance > MaxGrantDistance) return "moved away (" + distance.ToString("0.0") + " m)";
            return null;
        }

        // Observer presentation of one record on an instance that does not simulate it.
        public enum Show : byte { ScenePose = 0, Hidden = 1, AtPose = 2, Own = 3 }
        public static Show Present(bool hasRecord, HeavyRecord r, byte me, bool simulatingHere)
        {
            if (!hasRecord) return Show.ScenePose;
            switch (r.State)
            {
                case HeavyState.Carried: return r.Actor == me ? Show.Own : Show.Hidden;
                case HeavyState.Removed: return Show.Hidden;
                default: return r.Actor == me && simulatingHere ? Show.Own : (r.HasPose ? Show.AtPose : Show.ScenePose);
            }
        }
    }

    // Host-only lease book. Pure: single winner, stale guard, region lifetime, registry commits.
    public sealed class HeavyBook
    {
        readonly Dictionary<int, List<HeavyRecord>> _byRegion = new Dictionary<int, List<HeavyRecord>>();
        ushort _nextLease;
        public long Leases, Drops, Settles, Releases, Removes, Refused, Cleared, SlotReleases, Handovers, Stowed;
        // H2: a record whose carry ended without any pose (grant unusable, disconnect, region change) was deleted - the host
        // puts a synthetic cargo object back into its car (WorldHost); args: region, record.
        public Action<int, HeavyRecord> CarryAbandoned;

        public int CountIn(int ns) { List<HeavyRecord> l; return _byRegion.TryGetValue(ns, out l) ? l.Count : 0; }
        public int Total { get { int n = 0; foreach (var l in _byRegion.Values) n += l.Count; return n; } }
        public bool TryGet(int ns, uint hash, out HeavyRecord r)
        {
            List<HeavyRecord> l;
            if (_byRegion.TryGetValue(ns, out l)) foreach (var x in l) if (x.Hash == hash) { r = x; return true; }
            r = default(HeavyRecord); return false;
        }
        public List<HeavyRecord> InRegion(int ns) { List<HeavyRecord> l; return _byRegion.TryGetValue(ns, out l) ? new List<HeavyRecord>(l) : new List<HeavyRecord>(); }

        ushort NextLease() { _nextLease++; if (_nextLease == 0) _nextLease = 1; return _nextLease; }

        // slotRegion = the requester's committed region. known = the object is a supported census heavy object (host check).
        public ProposalCode Lease(byte slot, int slotRegion, int ns, uint hash, bool known, StateRegistry reg, List<StateEntry> outbox, out ushort lease, out string why)
        {
            lease = 0; why = null;
            if (slot < 1 || slot > 2) { why = "slot"; return ProposalCode.Invalid; }
            if (!known) { Refused++; why = "not a supported census heavy object"; return ProposalCode.NotAuthorized; }
            if (ns == Regions.None || slotRegion != ns) { Refused++; why = "requester not in " + IdentityNamespace.Name(ns); return ProposalCode.NotAuthorized; }
            var l = List(ns); int i = Find(l, hash);
            if (i >= 0)
            {
                var r = l[i];
                if (r.CarriedBy(slot)) { lease = r.Lease; why = "already carried by the requester"; return ProposalCode.Unchanged; }
                if (r.State == HeavyState.Carried) { Refused++; why = "carried by P" + r.Actor; return ProposalCode.Stale; }
                // Removed = the host's original moved it into host-local storage (trunk / shop / cargo); only the host's own
                // original can take it out again (deposit sync is a later milestone)
                if (r.State == HeavyState.Removed && slot != 1) { Refused++; why = "in host-local storage"; return ProposalCode.Stale; }
                r.State = HeavyState.Carried; r.Actor = slot; r.Lease = NextLease(); r.Moving = false;   // Removed already has no pose
                l[i] = r; lease = r.Lease;
            }
            else
            {
                if (l.Count >= HeavyList.MaxPerRegion) { Refused++; why = "region heavy-object limit " + HeavyList.MaxPerRegion + " reached"; return ProposalCode.Busy; }
                var r = new HeavyRecord { Hash = hash, State = HeavyState.Carried, Actor = slot, Lease = NextLease(), Rot = new Quat16 { W = 32767 } };
                l.Add(r); lease = r.Lease;
            }
            Leases++;
            Commit(ns, l, reg, outbox);
            return ProposalCode.Accepted;
        }

        // H1.1 Drop: the holder's original release happened at pos (committed right after Pickupable.DropHeavy, before the
        // physics moved it) or the object it simulates started moving again -> Placed + Moving, simulated by the holder.
        // Settle: the exact rest pose of the object it simulates -> Placed, Moving cleared. Same holder + lease only.
        public ProposalCode Drop(byte slot, int slotRegion, int ns, uint hash, ushort lease, Vec3 pos, Quat16 rot, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            return Place(slot, slotRegion, ns, hash, lease, pos, rot, true, reg, outbox, out why);
        }
        public ProposalCode Settle(byte slot, int slotRegion, int ns, uint hash, ushort lease, Vec3 pos, Quat16 rot, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            return Place(slot, slotRegion, ns, hash, lease, pos, rot, false, reg, outbox, out why);
        }
        ProposalCode Place(byte slot, int slotRegion, int ns, uint hash, ushort lease, Vec3 pos, Quat16 rot, bool moving, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            why = null;
            if (!WorldItemList.Finite(pos)) { why = "pose"; return ProposalCode.Invalid; }
            if (slotRegion != ns) { Refused++; why = "requester not in " + IdentityNamespace.Name(ns); return ProposalCode.NotAuthorized; }
            var l = List(ns); int i = Find(l, hash);
            if (i < 0) { Refused++; why = "no lease (region reset or released)"; return ProposalCode.Stale; }
            var r = l[i];
            bool mine = r.Actor == slot && r.Lease == lease && (r.State == HeavyState.Carried || r.State == HeavyState.Placed);
            if (!mine) { Refused++; why = "lease " + lease + " not held (" + r + ")"; return ProposalCode.Stale; }
            if (r.State == HeavyState.Placed && r.HasPose && r.Moving == moving && SamePose(r, pos, rot)) return ProposalCode.Unchanged;
            r.State = HeavyState.Placed; r.HasPose = true; r.Moving = moving; r.Pos = pos; r.Rot = rot;
            l[i] = r;
            if (moving) Drops++; else Settles++;
            Commit(ns, l, reg, outbox);
            return ProposalCode.Accepted;
        }

        // The holder gives the object up without a new pose (carry ended without a drop: object gone with its scene, grant no
        // longer usable). Carried -> the previous rest pose (simulated by nobody) or, without one, the original scene pose
        // (record removed). Placed by the holder -> simulated by nobody. Still Moving -> handed to the other player when it is
        // in that region (otherRegion), else the original scene pose (Revert).
        public ProposalCode Release(byte slot, int ns, uint hash, ushort lease, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            return Release(slot, ns, hash, lease, Regions.None, reg, outbox, out why);
        }
        public ProposalCode Release(byte slot, int ns, uint hash, ushort lease, int otherRegion, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            why = null;
            var l = List(ns); int i = Find(l, hash);
            if (i < 0) { why = "no record"; return ProposalCode.Unchanged; }
            var r = l[i];
            if (r.Actor != slot || r.Lease != lease || r.State == HeavyState.Removed) { why = "lease " + lease + " not held (" + r + ")"; return ProposalCode.Stale; }
            _revertNs = ns; Revert(l, i, Other(slot), otherRegion == ns);
            Releases++;
            Commit(ns, l, reg, outbox);
            return ProposalCode.Accepted;
        }

        // Host only: its original moved the carried / simulated object into host-local storage (trunk, shop, vehicle cargo).
        public ProposalCode Remove(byte slot, int ns, uint hash, ushort lease, StateRegistry reg, List<StateEntry> outbox, out string why)
        {
            why = null;
            if (slot != 1) { why = "only the host's own original deposits"; return ProposalCode.NotAuthorized; }
            var l = List(ns); int i = Find(l, hash);
            if (i < 0) { why = "no record"; return ProposalCode.Stale; }
            var r = l[i];
            if (r.Actor != slot || r.Lease != lease || r.State == HeavyState.Removed) { why = "lease " + lease + " not held (" + r + ")"; return ProposalCode.Stale; }
            r.State = HeavyState.Removed; r.Actor = 0; r.HasPose = false; r.Moving = false; l[i] = r; Removes++;
            Commit(ns, l, reg, outbox);
            return ProposalCode.Accepted;
        }

        // The slot left its region (region change, menu, disconnect): its scene objects are gone on its instance. Carried
        // objects return to their previous rest pose / scene pose, resting simulated ones stay at rest, simulated by nobody.
        // H1.1: an object it still simulated in motion is handed to the other player if that player is in the object's region
        // (otherRegion = the other slot's committed region): its instance continues the ORIGINAL physics from the pose it shows
        // and commits the rest pose. Nobody else there -> the original scene pose (the region empties / the original reload).
        public int ReleaseSlot(byte slot, StateRegistry reg, List<StateEntry> outbox) { return ReleaseSlot(slot, Regions.None, reg, outbox); }
        public int ReleaseSlot(byte slot, int otherRegion, StateRegistry reg, List<StateEntry> outbox)
        {
            int n = 0;
            foreach (var kv in _byRegion)
            {
                var l = kv.Value; bool changed = false;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    if (l[i].Actor != slot || l[i].State == HeavyState.Removed) continue;
                    _revertNs = kv.Key; Revert(l, i, Other(slot), otherRegion == kv.Key && otherRegion != Regions.None); changed = true; n++;
                }
                if (changed) Commit(kv.Key, l, reg, outbox);
            }
            SlotReleases += n;
            return n;
        }

        // Nobody is in the region any more: the original scene reload restores every heavy object.
        public int ClearRegion(int ns, StateRegistry reg, List<StateEntry> outbox)
        {
            List<HeavyRecord> l;
            if (!_byRegion.TryGetValue(ns, out l) || l.Count == 0) return 0;
            int n = l.Count; l.Clear(); Cleared += n;
            Commit(ns, l, reg, outbox);
            return n;
        }

        static byte Other(byte slot) { return (byte)(slot == 1 ? 2 : 1); }
        int _revertNs;

        // ---- H2 vehicle cargo (WorldHost.HeavyTransaction validates vehicle / capacity first, then calls these) ----
        // The slot holds this record (carrying it, or simulating it after its own drop) with this lease.
        public bool Holds(byte slot, int ns, uint hash, ushort lease, bool cargo, out string why)
        {
            why = null; HeavyRecord r;
            if (!TryGet(ns, hash, out r) || r.Cargo != cargo) { why = "no lease (region reset or released)"; return false; }
            if (r.Actor != slot || r.Lease != lease || r.State == HeavyState.Removed) { why = "lease " + lease + " not held (" + r + ")"; return false; }
            return true;
        }
        // Load committed: the object leaves the shared world exactly once. A census object becomes Removed (hidden everywhere;
        // the original region reload restores it like in single player); a synthetic cargo object's record is deleted (its
        // object is the car's cargo again). Caller checked Holds.
        public void Stow(int ns, uint hash, StateRegistry reg, List<StateEntry> outbox)
        {
            var l = List(ns); int i = Find(l, hash);
            if (i < 0) return;
            if (l[i].Cargo) l.RemoveAt(i);
            else { var r = l[i]; r.State = HeavyState.Removed; r.Actor = 0; r.HasPose = false; r.Moving = false; l[i] = r; }
            Stowed++;
            Commit(ns, l, reg, outbox);
        }
        // Unload: a cargo object taken out of the car = a new record carried by the slot (no pose yet: an abandoned carry puts
        // it back into the car through CarryAbandoned). False = region list full.
        public bool AddCarried(byte slot, int ns, uint hash, StateRegistry reg, List<StateEntry> outbox, out ushort lease)
        {
            lease = 0;
            var l = List(ns);
            if (Find(l, hash) >= 0 || l.Count >= HeavyList.MaxPerRegion) return false;
            var r = new HeavyRecord { Hash = hash, State = HeavyState.Carried, Actor = slot, Lease = NextLease(), Rot = new Quat16 { W = 32767 }, Cargo = true };
            l.Add(r); lease = r.Lease; Leases++;
            Commit(ns, l, reg, outbox);
            return true;
        }
        // every synthetic id still present in a region list (host id allocation must not reuse them)
        public void SyntheticIds(HashSet<ushort> into)
        {
            foreach (var l in _byRegion.Values) foreach (var r in l) if (r.Cargo) into.Add(HeavyCargo.IdOf(r.Hash));
        }

        void Revert(List<HeavyRecord> l, int i, byte other, bool otherHere)
        {
            var r = l[i];
            if (r.State == HeavyState.Carried && !r.HasPose) { l.RemoveAt(i); if (CarryAbandoned != null) CarryAbandoned(_revertNs, r); return; }
            if (r.State == HeavyState.Placed && r.Moving)
            {
                // the committed pose of a moving object is its release pose (possibly in the air): never frozen there
                if (otherHere) { r.Actor = other; r.Lease = NextLease(); l[i] = r; Handovers++; return; }
                l.RemoveAt(i); return;
            }
            r.State = HeavyState.Placed; r.Actor = 0; r.Moving = false; l[i] = r;
        }

        static bool SamePose(HeavyRecord r, Vec3 p, Quat16 q)
        {
            return Vec3.Distance(r.Pos, p) < 0.001f && r.Rot.X == q.X && r.Rot.Y == q.Y && r.Rot.Z == q.Z && r.Rot.W == q.W;
        }
        List<HeavyRecord> List(int ns) { List<HeavyRecord> l; if (!_byRegion.TryGetValue(ns, out l)) { l = new List<HeavyRecord>(); _byRegion[ns] = l; } return l; }
        static int Find(List<HeavyRecord> l, uint hash) { for (int i = 0; i < l.Count; i++) if (l[i].Hash == hash) return i; return -1; }
        static void Commit(int ns, List<HeavyRecord> l, StateRegistry reg, List<StateEntry> outbox)
        {
            var e = reg.Commit(StateScope.Region, ns, WorldKeys.Heavy, HeavyList.Encode(l));
            if (e != null) outbox.Add(e);
        }
    }
}
