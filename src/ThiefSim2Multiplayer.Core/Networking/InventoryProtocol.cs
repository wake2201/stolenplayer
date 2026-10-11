using System;
using System.IO;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public enum InventoryAction : byte { Pickup = 1, Drop = 2, Equip = 3, Unequip = 4, NativeRemoval = 5, Buy = 6, Sell = 7, Deposit = 8, Withdraw = 9 }
    public enum InventoryResultCode : byte { Reserved, Success, Aborted, Duplicate, WrongSession, WrongEpoch, WrongActor, NotReady, EntityUnavailable, StaleRevision, OutOfRange, InventoryFull, NotOwner, UnsupportedItem, InvalidState, RateLimited, InsufficientFunds }
    public sealed class InventoryRequest
    {
        public ulong SessionId, RequestId, ExpectedEntityRevision, ExpectedInventoryRevision;
        public uint SceneEpoch;
        public byte ActorId;
        public InventoryAction Action;
        public EntityRef Entity;
        public Guid ItemInstance;
        public float X, Y, Z;
        // Storage opening uses the original camera ray, authenticated against the actor pose.
        public float AimX, AimY, AimZ;
        public ushort Definition;
        public byte Container;
        public ulong ExpectedEconomyRevision;
    }
    public sealed class InventoryTransactionResult
    {
        public ulong SessionId, RequestId, TransactionId, InventoryRevision, WorldRevision, EconomyRevision;
        public int MoneyDelta;
        public uint SceneEpoch;
        public InventoryResultCode Code;
    }
    public sealed class InventoryCompletion
    {
        public ulong SessionId, RequestId, TransactionId;
        public uint SceneEpoch;
        public bool Observed;
    }
    public static class InventoryProtocol
    {
        public const byte RequestType = 28, ResultType = 29, CompletionType = 30, IdentityType = 31, SnapshotType = 32, EconomySnapshotType = 33;
        public const int RequestBytes = 103, ResultBytes = 58, CompletionBytes = 30;
        public static bool Valid(InventoryRequest r)
        {
            if (r == null || r.SessionId == 0 || r.SceneEpoch == 0 || r.RequestId == 0 || r.ActorId < 1 || r.ActorId > 2 || r.ExpectedInventoryRevision == 0 || (byte)r.Action < 1 || (byte)r.Action > 9 || !Position(r.X, r.Y, r.Z)) return false;
            bool storage=r.Action>=InventoryAction.Deposit;
            float aimNorm=r.AimX*r.AimX+r.AimY*r.AimY+r.AimZ*r.AimZ;
            if(!Protocol.Finite(aimNorm) || (storage ? aimNorm<.98f || aimNorm>1.02f : r.AimX!=0 || r.AimY!=0 || r.AimZ!=0)) return false;
            if (r.Action >= InventoryAction.Buy) return r.ExpectedEconomyRevision != 0 && r.Definition < InventoryLimits.MaxDefinitions && r.Container <= 1 && (storage || r.Container == 0) && !r.Entity.Id.IsValid && r.Entity.Generation == 0 && r.ExpectedEntityRevision == 0 && (storage || r.X == 0 && r.Y == 0 && r.Z == 0) && (r.Action == InventoryAction.Buy ? r.ItemInstance == Guid.Empty : r.ItemInstance != Guid.Empty);
            if (r.Definition != 0 || r.Container != 0 || r.ExpectedEconomyRevision != 0) return false;
            if (r.Action == InventoryAction.Pickup) return r.Entity.Id.IsValid && r.Entity.Generation != 0 && r.ExpectedEntityRevision != 0 && r.ItemInstance == Guid.Empty && r.X == 0 && r.Y == 0 && r.Z == 0;
            if (r.Entity.Id.IsValid || r.Entity.Generation != 0 || r.ExpectedEntityRevision != 0) return false;
            if (r.Action != InventoryAction.Drop && (r.X != 0 || r.Y != 0 || r.Z != 0)) return false;
            return r.Action == InventoryAction.Unequip ? r.ItemInstance == Guid.Empty : r.ItemInstance != Guid.Empty;
        }
        public static bool Position(float x, float y, float z) => Protocol.Finite(x) && Protocol.Finite(y) && Protocol.Finite(z) && Math.Abs(x) <= 100000 && Math.Abs(y) <= 100000 && Math.Abs(z) <= 100000;
        public static byte[] Encode(InventoryRequest v)
        {
            if (!Valid(v)) throw new ArgumentException("Invalid inventory request.");
            return Write(w => { w.Write(RequestType); w.Write(v.SessionId); w.Write(v.SceneEpoch); w.Write(v.RequestId); w.Write(v.ActorId); w.Write((byte)v.Action); w.Write((byte)v.Entity.Id.Scope); w.Write(v.Entity.Id.Value); w.Write(v.Entity.Generation); w.Write(v.ExpectedEntityRevision); w.Write(v.ExpectedInventoryRevision); w.Write(v.ItemInstance.ToByteArray()); w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.Definition); w.Write(v.Container); w.Write(v.ExpectedEconomyRevision); w.Write(v.AimX); w.Write(v.AimY); w.Write(v.AimZ); });
        }
        public static bool TryDecode(byte[] bytes, out InventoryRequest v)
        {
            v = null; if (bytes == null || bytes.Length != RequestBytes) return false;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    if (r.ReadByte() != RequestType) return false;
                    var q = new InventoryRequest { SessionId = r.ReadUInt64(), SceneEpoch = r.ReadUInt32(), RequestId = r.ReadUInt64(), ActorId = r.ReadByte(), Action = (InventoryAction)r.ReadByte() };
                    byte scope = r.ReadByte(); ulong id = r.ReadUInt64(); uint generation = r.ReadUInt32();
                    if (scope != 0 || id != 0 || generation != 0) q.Entity = new EntityRef(new NetworkEntityId((EntityScope)scope, id), generation);
                    q.ExpectedEntityRevision = r.ReadUInt64(); q.ExpectedInventoryRevision = r.ReadUInt64(); q.ItemInstance = new Guid(r.ReadBytes(16)); q.X = r.ReadSingle(); q.Y = r.ReadSingle(); q.Z = r.ReadSingle(); q.Definition = r.ReadUInt16(); q.Container = r.ReadByte(); q.ExpectedEconomyRevision = r.ReadUInt64(); q.AimX=r.ReadSingle(); q.AimY=r.ReadSingle(); q.AimZ=r.ReadSingle();
                    if (!Valid(q)) return false; v = q; return true;
                }
            }
            catch (ArgumentException) { return false; } catch (IOException) { return false; }
        }
        public static byte[] EncodeResult(InventoryTransactionResult v)
        {
            if (v == null || v.SessionId == 0 || v.SceneEpoch == 0 || v.RequestId == 0 || (byte)v.Code > (byte)InventoryResultCode.InsufficientFunds) throw new ArgumentException("Invalid transaction result.");
            return Write(w => { w.Write(ResultType); w.Write(v.SessionId); w.Write(v.SceneEpoch); w.Write(v.RequestId); w.Write(v.TransactionId); w.Write((byte)v.Code); w.Write(v.InventoryRevision); w.Write(v.WorldRevision); w.Write(v.EconomyRevision); w.Write(v.MoneyDelta); });
        }
        public static bool TryDecodeResult(byte[] bytes, out InventoryTransactionResult v)
        {
            v = null; if (bytes == null || bytes.Length != ResultBytes) return false;
            using (var r = new BinaryReader(new MemoryStream(bytes, false)))
            { if (r.ReadByte() != ResultType) return false; var q = new InventoryTransactionResult { SessionId = r.ReadUInt64(), SceneEpoch = r.ReadUInt32(), RequestId = r.ReadUInt64(), TransactionId = r.ReadUInt64(), Code = (InventoryResultCode)r.ReadByte(), InventoryRevision = r.ReadUInt64(), WorldRevision = r.ReadUInt64(), EconomyRevision = r.ReadUInt64(), MoneyDelta = r.ReadInt32() }; if (q.SessionId == 0 || q.SceneEpoch == 0 || q.RequestId == 0 || (byte)q.Code > (byte)InventoryResultCode.InsufficientFunds) return false; v = q; return true; }
        }
        public static byte[] EncodeCompletion(InventoryCompletion v)
        {
            if (v == null || v.SessionId == 0 || v.SceneEpoch == 0 || v.RequestId == 0 || v.TransactionId == 0) throw new ArgumentException("Invalid completion.");
            return Write(w => { w.Write(CompletionType); w.Write(v.SessionId); w.Write(v.SceneEpoch); w.Write(v.RequestId); w.Write(v.TransactionId); w.Write(v.Observed); });
        }
        public static bool TryDecodeCompletion(byte[] bytes, out InventoryCompletion v)
        {
            v = null; if (bytes == null || bytes.Length != CompletionBytes || bytes[29] > 1) return false;
            using (var r = new BinaryReader(new MemoryStream(bytes, false)))
            { if (r.ReadByte() != CompletionType) return false; var q = new InventoryCompletion { SessionId = r.ReadUInt64(), SceneEpoch = r.ReadUInt32(), RequestId = r.ReadUInt64(), TransactionId = r.ReadUInt64(), Observed = r.ReadBoolean() }; if (q.SessionId == 0 || q.SceneEpoch == 0 || q.RequestId == 0 || q.TransactionId == 0) return false; v = q; return true; }
        }
        // Owner-only bounded snapshot chunks. Strict sequential assembly on reliable ordered channel.
        public static byte[][] Snapshot(ulong session, uint epoch, InventoryState state, ulong worldRevision = 0)
        {
            return SnapshotPayload(SnapshotType, session, epoch, state.Revision, InventoryCodec.Encode(state), worldRevision);
        }
        public static byte[][] EconomySnapshot(ulong session, uint epoch, EconomyState state) => SnapshotPayload(EconomySnapshotType, session, epoch, state.Revision, EconomyCodec.Encode(state), 0);
        private static byte[][] SnapshotPayload(byte type, ulong session, uint epoch, ulong revision, byte[] bytes, ulong worldRevision)
        {
            int count = (bytes.Length + 383) / 384; var packets = new byte[count][];
            for (int i = 0; i < count; i++) { int index = i, length = Math.Min(384, bytes.Length - i * 384); packets[i] = Write(w => { w.Write(type); w.Write(session); w.Write(epoch); w.Write(revision); w.Write((ushort)index); w.Write((ushort)count); w.Write(bytes.Length); w.Write(worldRevision); w.Write(bytes, index * 384, length); }); }
            return packets;
        }
        internal static byte[] Write(Action<BinaryWriter> write) { using (var s = new MemoryStream()) using (var w = new BinaryWriter(s)) { write(w); return s.ToArray(); } }
    }
    public sealed class InventorySnapshotAssembler
    {
        private byte[] pending;
        private ulong revision;
        private int next;
        private double started;
        private ulong worldRevision;
        public ulong CompletedWorldRevision { get; private set; }
        public void Clear() { pending = null; next = 0; revision = 0; }
        public bool Accept(byte[] bytes, ulong session, uint epoch, double now, out InventoryState state)
        {
            state = null; byte[] complete; ulong rev;
            if (!AcceptPayload(bytes,session,epoch,now,InventoryProtocol.SnapshotType,InventoryCodec.HeaderBytes,InventoryCodec.MaxBytes,out complete,out rev)) return false;
            return complete == null || InventoryCodec.TryDecode(complete,out state) && state.Revision == rev;
        }
        public bool AcceptPayload(byte[] bytes, ulong session, uint epoch, double now, byte type, int minimum, int maximum, out byte[] payload, out ulong completedRevision)
        {
            payload = null; completedRevision = 0; if (bytes == null || bytes.Length < 37 || bytes.Length > 421) return false;
            using (var r = new BinaryReader(new MemoryStream(bytes, false)))
            {
                if (r.ReadByte() != type || r.ReadUInt64() != session || r.ReadUInt32() != epoch) return false;
                ulong rev = r.ReadUInt64(); int index = r.ReadUInt16(), count = r.ReadUInt16(), total = r.ReadInt32();
                ulong world = r.ReadUInt64();
                if (rev == 0 || total < minimum || total > maximum || count != (total + 383) / 384 || index >= count || bytes.Length != 37 + Math.Min(384, total - index * 384)) return false;
                if (index == 0) { pending = new byte[total]; revision = rev; worldRevision = world; next = 0; started = now; }
                if (pending == null || now - started > 5 || revision != rev || worldRevision != world || next != index || pending.Length != total) { Clear(); return false; }
                Buffer.BlockCopy(bytes, 37, pending, index * 384, bytes.Length - 37); next++;
                if (next == count) { byte[] complete = pending; CompletedWorldRevision = world; Clear(); payload=complete; completedRevision=rev; return true; }
                return true;
            }
        }
    }
}
