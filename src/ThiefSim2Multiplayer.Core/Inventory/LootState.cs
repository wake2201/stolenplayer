using System;
using System.IO;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Inventory
{
    public sealed class LootState
    {
        public InventoryItemState Item;
        public bool Available = true;
        public int MoneyValue;
        public float X, Y, Z, Qx, Qy, Qz, Qw = 1;
        public byte[] Encode()
        {
            if (Item == null || MoneyValue < 0 || !ValidTransform()) throw new ArgumentException("Invalid loot state.");
            return InventoryProtocol.Write(w => { w.Write((byte)(MoneyValue > 0 ? 2 : 1)); w.Write(Item.Instance.Value.ToByteArray()); w.Write(Item.Definition); w.Write(Item.Quantity); w.Write(X); w.Write(Y); w.Write(Z); w.Write(Qx); w.Write(Qy); w.Write(Qz); w.Write(Qw); w.Write(Available); if (MoneyValue > 0) w.Write(MoneyValue); });
        }
        private bool ValidTransform() => InventoryProtocol.Position(X, Y, Z) && Protocol.Finite(Qx) && Protocol.Finite(Qy) && Protocol.Finite(Qz) && Protocol.Finite(Qw) && Math.Abs(Qx * Qx + Qy * Qy + Qz * Qz + Qw * Qw - 1) < .01f;
        public static bool TryDecode(byte[] bytes, out LootState state)
        {
            state = null; if (bytes == null || !(bytes.Length == 52 && bytes[0] == 1 || bytes.Length == 56 && bytes[0] == 2) || bytes[51] > 1) return false;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, false)))
                { byte schema = r.ReadByte(); var item = new InventoryItemState(new NetworkItemInstanceId(new Guid(r.ReadBytes(16))), r.ReadUInt16()); if (r.ReadInt32() != 1) return false; var v = new LootState { Item = item, X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(), Qx = r.ReadSingle(), Qy = r.ReadSingle(), Qz = r.ReadSingle(), Qw = r.ReadSingle(), Available = r.ReadBoolean() }; if(schema == 2) { v.MoneyValue = r.ReadInt32(); if(v.MoneyValue <= 0) return false; } if (!v.ValidTransform()) return false; state = v; return true; }
            }
            catch (ArgumentException) { return false; } catch (IOException) { return false; }
        }
    }
    public interface ILootAdapter : INetworkEntityAdapter { }
    public interface IDespawnPresentation { void ApplyDespawn(); }
    public sealed class ManagedLootAdapter : ILootAdapter
    {
        public LootState State;
        public EntityTypeId EntityType => EntityTypeId.Pickup;
        public bool IsAlive { get; private set; } = true;
        public ManagedLootAdapter(LootState state) { State = state; state.Encode(); }
        public byte[] CaptureState() => State.Encode();
        public bool ValidateState(byte[] bytes) { LootState state; return LootState.TryDecode(bytes, out state); }
        public void ApplyState(byte[] bytes) { LootState state; if (!LootState.TryDecode(bytes, out state)) throw new ArgumentException("Invalid loot."); State = state; }
        public void Release() { IsAlive = false; }
    }
}
