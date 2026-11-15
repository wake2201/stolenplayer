// H2 shared heavy vehicle cargo (pure logic, offline-tested in tests/H2Tests.cs).
// Verified original model (ilspycmd decompile of Assembly-CSharp 1.27c + MainScene scene data, read-only; PROGRESS H2 §A):
//  - The Ranger (MainScene "PLAYER CAR", VehiclesManager.firstCar, carNr 0) is the only car with a VanHeavyItems component
//    (MainScene scan: one instance, on the car root; the Shadowhawk "MUSTANG" has none). Its cargo is PHYSICAL: a heavy object
//    the player drops into the bed lies there; VanHeavyItems.triggerSensor (SensorToolkit TriggerSensor) detects it.
//    CarController.EnterVehicle -> VanHeavyItems.BlockHeavyFromMoving: every detected object kinematic + trigger colliders +
//    parented to the car (+ item weight x 10 on the car mass); CarController exit -> UnblockHeavy: dynamic again, unparented.
//  - Persistence: SaveGame.SaveGameNewSystem -> PlayerScript.TrySaveTrunkItem -> VehiclesManager.SaveAllCarsTrunks ->
//    firstCar.van_heavy.SaveItems() ONLY while firstCar is active in the hierarchy: keys HeavyVan_<i> (item_ID, -1 = empty) and
//    PosX/Y/Z_item<i>, RotX/Y/Z_item<i> (car-local position / euler angles) for i < 24 detected objects (30 slots cleared).
//    VanHeavyItems.OnEnable re-arms LoadItems (0.5 s later, after every activation): for i < 30 until HeavyVan_<i> <= 0 it
//    instantiates item_IDs2[id].drop_object, parents it to the car at the saved local pose, x10XP = add_any_XP = false, and
//    (CarController.blockHeavysOnStart) kinematic + Pickupable.PlacedInTrunk, else dynamic.
//  - Side effects of bed loading: NONE (no XP, no key, no story, no StolenBigItems). VanHeavyItems.SaveXPForItems (XP,
//    Item_<name>, stolenBigItemsNr++) has no caller in 1.27c; StolenBigItems grows only at the heavy shop sale
//    (Inventory.FindHeavyInShopAndDestroy). The separate single trunk slot (Place_Heavy_Trigger -> CarController.heavyItem,
//    PlayerScript.HeavyItemPlacedInCar, Pickupable.WasPlacedInCar story hook) is never persisted ("TrunkItem" is read by
//    CarController.LoadTrunkItem but written nowhere) and its trigger is inactive in the Ranger asset; it stays H1 (host-local).
// Multiplayer model: one host cargo list per supported car (registry "hcargo", Session scope, arg = vehicle id), committed by
// the host only. Entry = cargo object id (host-assigned, unique per host session) + item id + car-local pose. Every instance
// shows each entry as ONE original drop_object instance parented to its own copy of the car at that pose, kinematic +
// PlacedInTrunk (the original LoadItems representation of saved cargo); no physics is streamed for stored cargo.
//  - Load: a heavy record (census object or synthetic cargo object) held by the requester comes to rest in the cargo bed ->
//    host validates lease, vehicle, region, capacity -> record leaves the shared world (census: Removed; synthetic: deleted)
//    and the entry is added - one host step, both registry entries committed together.
//  - Unload: a player picks a cargo object up (original PickUpHeavy) -> the entry becomes a heavy record with the synthetic
//    id Synthetic(item, id), carried by that player (H1 lease rules from here: drop, motion, settle, handover, re-load).
//    A carry that ends without any pose (grant unusable, disconnect) puts the entry back into its car.
//  - Persistence: the host's own save writes the list in the original key format (VanHeavyItems.SaveItems format, <= 24);
//    a host start seeds the list from those keys. Player 2 never saves or loads cargo keys during a session.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public struct CargoEntry
    {
        public ushort Id;        // cargo object id (synthetic record id while it is out of the car)
        public ushort ItemId;    // ItemID.item_ID of the object (drop_object of that item is the presentation)
        public Vec3 Pos;         // car-local position (VanHeavyItems transform = car root)
        public Quat16 Rot;       // car-local rotation
        public const int WireSize = 24;
        public void Write(NetDataWriter w) { w.Put(Id); w.Put(ItemId); w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); Rot.Write(w); }
        public static CargoEntry Read(NetDataReader r)
        {
            return new CargoEntry { Id = r.GetUShort(), ItemId = r.GetUShort(), Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat()), Rot = Quat16.Read(r) };
        }
        public override string ToString() { return "#" + Id + " item " + ItemId + " at local " + Pos; }
    }

    public static class HeavyCargo
    {
        // VanHeavyItems.SaveItems persists at most 24 detected objects (loop j < 24): a load beyond that would be lost at the
        // next original save, so the shared bed takes 24. Entries put back by an abandoned carry may exceed it up to MaxEntries
        // (registry value limit); the save keeps the first 24 like the original.
        public const int Capacity = 24;
        public const int MaxEntries = 32;           // 4 + 24 x 32 = 772 B <= StateEntry.MaxValueBytes
        public const int SaveSlots = 30;            // keys cleared by SaveItems / read by LoadItems
        public const float MaxLocal = 8f;           // a car-local pose further away is not inside a car's cargo bed
        const byte Version = 1;

        // Synthetic heavy-record id of a cargo object outside the car: bit 31 + 15-bit item id + 16-bit cargo id. Census hashes use
        // the full 32-bit range, so the id alone never decides the kind: records carry HeavyRecord.Cargo and requests
        // HeavyTxnRequest.Cargo (op byte bit 0x80). The host refuses to take an entry out under an id that equals a census hash
        // of that region (WorldHost.CargoLease). IsSynthetic is only the encoding check.
        public static uint Synthetic(int itemId, ushort id) { return 0x80000000u | ((uint)(itemId & 0x7FFF) << 16) | id; }
        public static bool IsSynthetic(uint hash) { return (hash & 0x80000000u) != 0 && IdOf(hash) != 0; }
        public static ushort IdOf(uint hash) { return (ushort)(hash & 0xFFFF); }
        public static int ItemOf(uint hash) { return (int)((hash >> 16) & 0x7FFF); }

        public static bool LocalPoseOk(Vec3 p)
        {
            return WorldItemList.Finite(p) && Math.Abs(p.X) <= MaxLocal && Math.Abs(p.Y) <= MaxLocal && Math.Abs(p.Z) <= MaxLocal;
        }

        public static byte[] Encode(uint rev, List<CargoEntry> l)
        {
            var w = new NetDataWriter(); w.Put(Version); w.Put((byte)l.Count); w.Put((ushort)(rev & 0xFFFF));
            foreach (var e in l) e.Write(w);
            return w.CopyData();
        }
        public static bool TryParse(byte[] v, out List<CargoEntry> l)
        {
            l = null;
            if (v == null || v.Length < 4 || v[0] != Version) return false;
            int n = v[1];
            if (n > MaxEntries || v.Length != 4 + n * CargoEntry.WireSize) return false;
            try
            {
                var r = new NetDataReader(v); r.GetByte(); r.GetByte(); r.GetUShort();
                l = new List<CargoEntry>(n);
                var ids = new HashSet<ushort>();
                for (int i = 0; i < n; i++)
                {
                    var e = CargoEntry.Read(r);
                    if (e.Id == 0 || e.ItemId == 0 || e.ItemId > 0x7FFF || !ids.Add(e.Id) || !LocalPoseOk(e.Pos)) { l = null; return false; }
                    l.Add(e);
                }
                return true;
            }
            catch { l = null; return false; }
        }

        // Original LoadItems order: slots 0.. until the first HeavyVan_<i> <= 0 (pure: keys passed as values; host seed).
        public static List<CargoEntry> FromSaveSlots(int[] items, Vec3[] pos, Vec3[] euler, Func<Vec3, Quat16> rot)
        {
            var l = new List<CargoEntry>();
            for (int i = 0; i < items.Length && i < SaveSlots; i++)
            {
                if (items[i] <= 0) break;
                if (items[i] > 0x7FFF || l.Count >= MaxEntries || !LocalPoseOk(pos[i])) continue;
                l.Add(new CargoEntry { ItemId = (ushort)items[i], Pos = pos[i], Rot = rot(euler[i]) });
            }
            return l;
        }
    }

    // Host-only cargo book: one list per supported vehicle; cargo object ids unique per host session.
    public sealed class CargoBook
    {
        readonly Dictionary<ushort, List<CargoEntry>> _byCar = new Dictionary<ushort, List<CargoEntry>>();
        // entries taken out of a car whose carry has no pose yet: put back when that carry is abandoned
        readonly Dictionary<ushort, KeyValuePair<ushort, CargoEntry>> _out = new Dictionary<ushort, KeyValuePair<ushort, CargoEntry>>();
        readonly HashSet<ushort> _seeded = new HashSet<ushort>();
        ushort _nextId; uint _rev;
        public long Loads, Unloads, Restores, Refused, Seeds;
        public const int MaxOut = 64;

        public int Count(ushort car) { List<CargoEntry> l; return _byCar.TryGetValue(car, out l) ? l.Count : 0; }
        public List<CargoEntry> Of(ushort car) { List<CargoEntry> l; return _byCar.TryGetValue(car, out l) ? new List<CargoEntry>(l) : new List<CargoEntry>(); }
        public bool Seeded(ushort car) { return _seeded.Contains(car); }
        public bool TryFind(ushort id, out ushort car, out CargoEntry e)
        {
            foreach (var kv in _byCar) foreach (var x in kv.Value) if (x.Id == id) { car = kv.Key; e = x; return true; }
            car = 0; e = default(CargoEntry); return false;
        }

        // inUse = synthetic ids of records still in the world (HeavyBook.SyntheticIds)
        public ushort NextId(HashSet<ushort> inUse)
        {
            for (int k = 0; k < 65535; k++)
            {
                _nextId++; if (_nextId == 0) _nextId = 1;
                ushort c; CargoEntry e;
                if (!inUse.Contains(_nextId) && !_out.ContainsKey(_nextId) && !TryFind(_nextId, out c, out e)) return _nextId;
            }
            return 0;
        }

        // Host start: the list from the host's own saved keys (once per vehicle and host session; a later call is ignored).
        public bool Seed(ushort car, List<CargoEntry> fromSave, HashSet<ushort> inUse, StateRegistry reg, List<StateEntry> outbox)
        {
            if (!_seeded.Add(car)) return false;
            var l = List(car);
            foreach (var e in fromSave)
            {
                if (l.Count >= HeavyCargo.MaxEntries) break;
                var x = e; x.Id = NextId(inUse); if (x.Id == 0) break;
                l.Add(x);
            }
            Seeds++;
            Commit(car, l, reg, outbox);
            return true;
        }

        public bool Full(ushort car) { return Count(car) >= HeavyCargo.Capacity; }

        public void Add(ushort car, CargoEntry e, StateRegistry reg, List<StateEntry> outbox)
        {
            var l = List(car);
            _out.Remove(e.Id);
            l.Add(e); Loads++;
            Commit(car, l, reg, outbox);
        }

        // The entry leaves the car (picked up); remembered until its carry gets a pose or is abandoned.
        public bool Take(ushort car, ushort id, StateRegistry reg, List<StateEntry> outbox, out CargoEntry e)
        {
            e = default(CargoEntry);
            List<CargoEntry> l; if (!_byCar.TryGetValue(car, out l)) return false;
            for (int i = 0; i < l.Count; i++)
            {
                if (l[i].Id != id) continue;
                e = l[i]; l.RemoveAt(i); Unloads++;
                if (_out.Count >= MaxOut) _out.Clear();   // bounded: only the newest carries can still be put back
                _out[id] = new KeyValuePair<ushort, CargoEntry>(car, e);
                Commit(car, l, reg, outbox);
                return true;
            }
            return false;
        }

        // The carry of a taken entry ended before any drop pose: back into its car at its old pose (never destroyed).
        public bool Restore(ushort id, StateRegistry reg, List<StateEntry> outbox)
        {
            KeyValuePair<ushort, CargoEntry> kv;
            if (!_out.TryGetValue(id, out kv)) return false;
            _out.Remove(id);
            var l = List(kv.Key);
            if (l.Count >= HeavyCargo.MaxEntries) return false;
            foreach (var x in l) if (x.Id == id) return false;
            l.Add(kv.Value); Restores++;
            Commit(kv.Key, l, reg, outbox);
            return true;
        }
        // its carry got a drop pose: it is a world object now (an abandoned later carry keeps that pose, H1 rules)
        public void Placed(ushort id) { _out.Remove(id); }

        List<CargoEntry> List(ushort car) { List<CargoEntry> l; if (!_byCar.TryGetValue(car, out l)) { l = new List<CargoEntry>(); _byCar[car] = l; } return l; }
        void Commit(ushort car, List<CargoEntry> l, StateRegistry reg, List<StateEntry> outbox)
        {
            _rev++;
            var e = reg.Commit(StateScope.Session, car, WorldKeys.Cargo, HeavyCargo.Encode(_rev, l));
            if (e != null) outbox.Add(e);
        }
    }
}
