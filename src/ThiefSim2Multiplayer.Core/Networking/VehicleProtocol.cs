using System;
using System.IO;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public enum SeatAction : byte { Enter = 1, Confirm = 2, Exit = 3, ExitConfirm = 4 }
    public enum SeatResult : byte { Reserved, Confirmed, Exited, Duplicate, WrongSession, WrongEpoch, WrongActor, NotReady, Unavailable, SeatTaken, AlreadySeated, StaleRevision, Ineligible, RateLimited, Exiting }
    public sealed class SeatRequest
    { public ulong SessionId, RequestId, OccupancyRevision; public uint Epoch; public byte Actor; public EntityRef Vehicle, Door; public SeatRole Seat; public SeatAction Action; }
    public sealed class DriverInput
    { public ulong SessionId, OccupancyRevision; public uint Epoch, Sequence; public byte Actor; public EntityRef Vehicle; public float Steer, Forward, Handbrake; public bool EngineRunning; }
    public static class VehicleProtocol
    {
        public const byte Request = 11, Result = 12, Input = 13;
        private static void Ref(BinaryWriter w, EntityRef e) { w.Write((byte)e.Id.Scope); w.Write(e.Id.Value); w.Write(e.Generation); }
        private static EntityRef Ref(BinaryReader r) => new EntityRef(new NetworkEntityId((EntityScope)r.ReadByte(), r.ReadUInt64()), r.ReadUInt32());
        private static EntityRef OptionalRef(BinaryReader r)
        {
            var scope=(EntityScope)r.ReadByte(); ulong id=r.ReadUInt64(); uint generation=r.ReadUInt32();
            return scope==0 && id==0 && generation==0 ? default(EntityRef) : new EntityRef(new NetworkEntityId(scope,id),generation);
        }
        private static byte[] Write(Action<BinaryWriter> write) { using (var s=new MemoryStream()) using (var w=new BinaryWriter(s)) { write(w); return s.ToArray(); } }
        public static bool Valid(SeatRequest r)
        {
            if (r==null || r.SessionId==0 || r.RequestId==0 || r.Epoch==0 || r.OccupancyRevision==0 || r.Actor<1 || r.Actor>2 || !r.Vehicle.Id.IsValid || r.Vehicle.Generation==0 || r.Seat<SeatRole.Driver || r.Seat>SeatRole.Passenger || r.Action<SeatAction.Enter || r.Action>SeatAction.ExitConfirm) return false;
            bool hasDoor=r.Door.Id.IsValid && r.Door.Generation!=0;
            return hasDoor==(r.Seat==SeatRole.Passenger && r.Action==SeatAction.Enter);
        }
        public static bool Valid(DriverInput r) => r != null && r.SessionId != 0 && r.Epoch != 0 && r.Sequence != 0 && r.OccupancyRevision != 0 && r.Actor >= 1 && r.Actor <= 2 && r.Vehicle.Id.IsValid && r.Vehicle.Generation != 0 && Protocol.Finite(r.Steer) && Protocol.Finite(r.Forward) && Protocol.Finite(r.Handbrake) && Math.Abs(r.Steer) <= 1 && Math.Abs(r.Forward) <= 1 && r.Handbrake >= 0 && r.Handbrake <= 1;
        public static byte[] Encode(SeatRequest r) { if (!Valid(r)) throw new ArgumentException(); return Write(w => { w.Write(Request); w.Write(r.SessionId); w.Write(r.Epoch); w.Write(r.RequestId); w.Write(r.Actor); Ref(w,r.Vehicle); Ref(w,r.Door); w.Write((byte)r.Seat); w.Write((byte)r.Action); w.Write(r.OccupancyRevision); }); }
        public static bool Decode(byte[] bytes, out SeatRequest r)
        {
            r=null; if (bytes==null || bytes.Length!=58 || bytes[0]!=Request) return false;
            try { using (var b=new BinaryReader(new MemoryStream(bytes,false))) { b.ReadByte(); var q=new SeatRequest { SessionId=b.ReadUInt64(), Epoch=b.ReadUInt32(), RequestId=b.ReadUInt64(), Actor=b.ReadByte(), Vehicle=Ref(b), Door=OptionalRef(b), Seat=(SeatRole)b.ReadByte(), Action=(SeatAction)b.ReadByte(), OccupancyRevision=b.ReadUInt64() }; if (!Valid(q)) return false; r=q; return true; } } catch (ArgumentException) { return false; } catch (IOException) { return false; }
        }
        public static byte[] Encode(DriverInput r) { if (!Valid(r)) throw new ArgumentException(); return Write(w => { w.Write(Input); w.Write(r.SessionId); w.Write(r.Epoch); w.Write(r.Sequence); w.Write(r.Actor); Ref(w,r.Vehicle); w.Write(r.OccupancyRevision); w.Write(r.Steer); w.Write(r.Forward); w.Write(r.Handbrake); w.Write(r.EngineRunning); }); }
        public static bool Decode(byte[] bytes, out DriverInput r)
        {
            r=null; if (bytes==null || bytes.Length!=52 || bytes[0]!=Input || bytes[51]>1) return false;
            try { using (var b=new BinaryReader(new MemoryStream(bytes,false))) { b.ReadByte(); var q=new DriverInput { SessionId=b.ReadUInt64(), Epoch=b.ReadUInt32(), Sequence=b.ReadUInt32(), Actor=b.ReadByte(), Vehicle=Ref(b), OccupancyRevision=b.ReadUInt64(), Steer=b.ReadSingle(), Forward=b.ReadSingle(), Handbrake=b.ReadSingle(), EngineRunning=b.ReadBoolean() }; if (!Valid(q)) return false; r=q; return true; } } catch (ArgumentException) { return false; } catch (IOException) { return false; }
        }
        public static byte[] EncodeResult(ulong session,uint epoch,ulong request,SeatResult result) => Write(w => { w.Write(Result); w.Write(session); w.Write(epoch); w.Write(request); w.Write((byte)result); });
        public static bool DecodeResult(byte[] bytes,out ulong session,out uint epoch,out ulong request,out SeatResult result)
        {
            session=request=0; epoch=0; result=default(SeatResult); if (bytes==null || bytes.Length!=22 || bytes[0]!=Result) return false;
            using (var b=new BinaryReader(new MemoryStream(bytes,false))) { b.ReadByte(); session=b.ReadUInt64(); epoch=b.ReadUInt32(); request=b.ReadUInt64(); result=(SeatResult)b.ReadByte(); return session!=0 && epoch!=0 && request!=0 && result<=SeatResult.Exiting; }
        }
    }
}
