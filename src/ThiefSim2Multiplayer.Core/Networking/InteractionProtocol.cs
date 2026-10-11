using System;
using System.IO;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public enum InteractionKind : byte { Open = 1, Close = 2, SlowBegin = 3, SlowRelease = 4, BreachBegin = 5, BreachComplete = 6, UnlockKey = 7, Ring = 8 }
    public enum InteractionResult : byte { Accepted, Duplicate, WrongSession, WrongEpoch, NotReady, WrongActor, UnknownEntity, WrongGeneration, Unsupported, StaleRevision, FutureRevision, OutOfRange, InvalidTarget, NotPermitted, ExecutionFailed, RateLimited }
    public sealed class InteractionRequest
    {
        public ulong SessionId, RequestId, ExpectedRevision;
        public uint SceneEpoch;
        public byte ActorId;
        public EntityRef Entity;
        public InteractionKind Kind;
    }
    public static class InteractionProtocol
    {
        public const byte RequestType = 26, ResultType = 27;
        public const int RequestBytes = 44, ResultBytes = 30;
        public static bool Valid(InteractionRequest r) => r != null && r.SessionId != 0 && r.SceneEpoch != 0 && r.RequestId != 0 && r.ExpectedRevision != 0 && r.ActorId >= 1 && r.ActorId <= 2 && r.Entity.Id.IsValid && r.Entity.Generation != 0 && r.Kind >= InteractionKind.Open && r.Kind <= InteractionKind.Ring;
        public static byte[] Encode(InteractionRequest r)
        {
            if (!Valid(r)) throw new ArgumentException("Invalid interaction request.");
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            { w.Write(RequestType); w.Write(r.SessionId); w.Write(r.SceneEpoch); w.Write(r.RequestId); w.Write(r.ActorId); w.Write((byte)r.Entity.Id.Scope); w.Write(r.Entity.Id.Value); w.Write(r.Entity.Generation); w.Write((byte)r.Kind); w.Write(r.ExpectedRevision); return s.ToArray(); }
        }
        public static bool TryDecode(byte[] bytes, int offset, int count, out InteractionRequest request)
        {
            request = null;
            if (bytes == null || count != RequestBytes || offset < 0 || offset > bytes.Length - count) return false;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, offset, count, false)))
                {
                    if (r.ReadByte() != RequestType) return false;
                    var v = new InteractionRequest { SessionId = r.ReadUInt64(), SceneEpoch = r.ReadUInt32(), RequestId = r.ReadUInt64(), ActorId = r.ReadByte(), Entity = new EntityRef(new NetworkEntityId((EntityScope)r.ReadByte(), r.ReadUInt64()), r.ReadUInt32()), Kind = (InteractionKind)r.ReadByte(), ExpectedRevision = r.ReadUInt64() };
                    if (!Valid(v)) return false; request = v; return true;
                }
            }
            catch (ArgumentException) { return false; } catch (IOException) { return false; }
        }
        public static byte[] EncodeResult(ulong session, uint epoch, ulong request, InteractionResult result, ulong revision)
        {
            if (session == 0 || epoch == 0 || request == 0 || (byte)result > (byte)InteractionResult.RateLimited) throw new ArgumentException("Invalid result.");
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            { w.Write(ResultType); w.Write(session); w.Write(epoch); w.Write(request); w.Write((byte)result); w.Write(revision); return s.ToArray(); }
        }
        public static bool TryDecodeResult(byte[] bytes, int offset, int count, out ulong session, out uint epoch, out ulong request, out InteractionResult result, out ulong revision)
        {
            session = request = revision = 0; epoch = 0; result = InteractionResult.ExecutionFailed;
            if (bytes == null || count != ResultBytes || offset < 0 || offset > bytes.Length - count) return false;
            using (var r = new BinaryReader(new MemoryStream(bytes, offset, count, false)))
            { if (r.ReadByte() != ResultType) return false; session = r.ReadUInt64(); epoch = r.ReadUInt32(); request = r.ReadUInt64(); result = (InteractionResult)r.ReadByte(); revision = r.ReadUInt64(); return session != 0 && epoch != 0 && request != 0 && (byte)result <= (byte)InteractionResult.RateLimited; }
        }
    }
}
