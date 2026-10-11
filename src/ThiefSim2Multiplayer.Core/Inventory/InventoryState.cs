using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Inventory
{
    public static class InventoryLimits
    {
        public const int MaxItems = 128, MaxDefinitions = 452, MaxPending = 16, History = 256;
        public const int MaxDynamicDrops = 128;
        public const double ReservationSeconds = 5;
    }
    public struct NetworkItemInstanceId : IEquatable<NetworkItemInstanceId>
    {
        public readonly Guid Value;
        public NetworkItemInstanceId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("Empty item instance."); Value = value; }
        public static NetworkItemInstanceId FromEntity(ulong worldSession, uint epoch, EntityRef entity)
        {
            if (worldSession == 0 || epoch == 0 || !entity.Id.IsValid || entity.Generation == 0) throw new ArgumentException("Invalid item origin.");
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            { w.Write("item-v1"); w.Write(worldSession); w.Write(epoch); w.Write((byte)entity.Id.Scope); w.Write(entity.Id.Value); w.Write(entity.Generation); return new NetworkItemInstanceId(new Guid(StableHash.Digest(s.ToArray()).Take(16).ToArray())); }
        }
        public bool Equals(NetworkItemInstanceId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is NetworkItemInstanceId && Equals((NetworkItemInstanceId)obj);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("N");
    }
    // One native Pickupable increments ItemID.item_amount by ONE. Multiple physical units
    // of the same definition remain separate here and aggregate only at the native adapter.
    public sealed class InventoryItemState
    {
        public NetworkItemInstanceId Instance { get; }
        public ushort Definition { get; }
        public int Quantity => 1;
        public InventoryItemState(NetworkItemInstanceId instance, ushort definition)
        { if (instance.Value == Guid.Empty || definition >= InventoryLimits.MaxDefinitions) throw new ArgumentException("Invalid item."); Instance = instance; Definition = definition; }
    }
    public sealed class InventoryState
    {
        private readonly InventoryItemState[] items;
        public PersistentCoopPlayerId Owner { get; }
        public ulong Revision { get; }
        public float Capacity { get; }
        public NetworkItemInstanceId? Equipped { get; }
        public InventoryItemState[] Items => (InventoryItemState[])items.Clone();
        public int ItemCount => items.Length;
        public InventoryState(PersistentCoopPlayerId owner, ulong revision, float capacity, IEnumerable<InventoryItemState> records, NetworkItemInstanceId? equipped = null)
        {
            if (owner.Value == Guid.Empty || revision == 0 || !Protocol.Finite(capacity) || capacity < 0 || capacity > 1000 || records == null) throw new ArgumentException("Invalid inventory.");
            items = records.Take(InventoryLimits.MaxItems + 1).ToArray();
            if (items.Length > InventoryLimits.MaxItems || items.Any(i => i == null) || items.Select(i => i.Instance).Distinct().Count() != items.Length || (equipped.HasValue && !items.Any(i => i.Instance.Equals(equipped.Value)))) throw new ArgumentException("Invalid inventory ownership.");
            items = items.OrderBy(i => i.Instance.ToString(), StringComparer.Ordinal).ToArray(); Owner = owner; Revision = revision; Capacity = capacity; Equipped = equipped;
        }
        public InventoryItemState Find(NetworkItemInstanceId id) => items.FirstOrDefault(i => i.Instance.Equals(id));
        public int Count(ushort definition) => items.Count(i => i.Definition == definition);
        public string Hash => BitConverter.ToString(StableHash.Digest(InventoryCodec.Encode(this))).Replace("-", "");
        public bool NewerThan(InventoryState old) => old == null || (Owner.Equals(old.Owner) && Revision > old.Revision);
    }
    public sealed class ItemDefinitionRule
    {
        public readonly ushort Id;
        public readonly float Weight;
        public readonly int MaxAmount;
        public readonly bool Droppable;
        public ItemDefinitionRule(ushort id, float weight, int maxAmount, bool droppable)
        { if (id >= InventoryLimits.MaxDefinitions || !Protocol.Finite(weight) || weight < 0 || weight > 1000 || maxAmount < 0 || maxAmount > 10000) throw new ArgumentException("Invalid definition rule."); Id = id; Weight = weight; MaxAmount = maxAmount; Droppable = droppable; }
    }
    public static class InventoryCodec
    {
        public const int HeaderBytes = 47, ItemBytes = 22, MaxBytes = HeaderBytes + InventoryLimits.MaxItems * ItemBytes;
        public static byte[] Encode(InventoryState state)
        {
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            {
                w.Write((byte)1); w.Write(state.Owner.Value.ToByteArray()); w.Write(state.Revision); w.Write(state.Capacity);
                w.Write((state.Equipped.HasValue ? state.Equipped.Value.Value : Guid.Empty).ToByteArray()); w.Write((ushort)state.Items.Length);
                foreach (var item in state.Items) { w.Write(item.Instance.Value.ToByteArray()); w.Write(item.Definition); w.Write(item.Quantity); }
                return s.ToArray();
            }
        }
        public static bool TryDecode(byte[] bytes, out InventoryState state)
        {
            state = null; if (bytes == null || bytes.Length < HeaderBytes || bytes.Length > MaxBytes) return false;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    if (r.ReadByte() != 1) return false;
                    var owner = new PersistentCoopPlayerId(new Guid(r.ReadBytes(16))); ulong revision = r.ReadUInt64(); float capacity = r.ReadSingle(); Guid equip = new Guid(r.ReadBytes(16)); int count = r.ReadUInt16();
                    if (count > InventoryLimits.MaxItems || bytes.Length != HeaderBytes + count * ItemBytes) return false;
                    var items = new InventoryItemState[count];
                    for (int i = 0; i < count; i++) { var id = new NetworkItemInstanceId(new Guid(r.ReadBytes(16))); ushort definition = r.ReadUInt16(); if (r.ReadInt32() != 1) return false; items[i] = new InventoryItemState(id, definition); }
                    state = new InventoryState(owner, revision, capacity, items, equip == Guid.Empty ? (NetworkItemInstanceId?)null : new NetworkItemInstanceId(equip)); return true;
                }
            }
            catch (ArgumentException) { return false; } catch (IOException) { return false; }
        }
    }
}
