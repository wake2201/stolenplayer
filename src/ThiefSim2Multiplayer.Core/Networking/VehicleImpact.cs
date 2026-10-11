using System;
using System.IO;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    // Reliable, rare native collision consequences; never a second physics solver.
    public struct VehicleImpact
    {
        public ulong SessionId, AuthorityGeneration, Target;
        public uint Epoch, Sequence;
        public byte Actor;
        public EntityRef Vehicle;
        public float X,Y,Z,Vx,Vy,Vz;
        public bool Valid => SessionId!=0 && Epoch!=0 && Sequence!=0 && Actor>=1 && Actor<=2 && AuthorityGeneration!=0 && Target!=0 && Vehicle.Id.IsValid && Vehicle.Generation!=0 &&
            Protocol.Finite(X) && Protocol.Finite(Y) && Protocol.Finite(Z) && Math.Abs(X)<=100000 && Math.Abs(Y)<=100000 && Math.Abs(Z)<=100000 &&
            Protocol.Finite(Vx) && Protocol.Finite(Vy) && Protocol.Finite(Vz) && Vx*Vx+Vy*Vy+Vz*Vz<=62500;
    }
    public static class VehicleImpactProtocol
    {
        public const byte Type=34;
        public static byte[] Write(VehicleImpact q)
        {
            if(!q.Valid) throw new ArgumentException();
            using(var s=new MemoryStream()) using(var w=new BinaryWriter(s)) {
                w.Write(Type); w.Write(q.SessionId); w.Write(q.Epoch); w.Write(q.Actor); w.Write((byte)q.Vehicle.Id.Scope); w.Write(q.Vehicle.Id.Value); w.Write(q.Vehicle.Generation);
                w.Write(q.AuthorityGeneration); w.Write(q.Sequence); w.Write(q.Target);
                w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.Vx); w.Write(q.Vy); w.Write(q.Vz); return s.ToArray();
            }
        }
        public static bool Read(byte[] bytes,out VehicleImpact q)
        {
            q=default; if(bytes==null || bytes.Length!=71 || bytes[0]!=Type) return false;
            try { using(var r=new BinaryReader(new MemoryStream(bytes,false))) {
                r.ReadByte(); q.SessionId=r.ReadUInt64(); q.Epoch=r.ReadUInt32(); q.Actor=r.ReadByte(); q.Vehicle=new EntityRef(new NetworkEntityId((EntityScope)r.ReadByte(),r.ReadUInt64()),r.ReadUInt32());
                q.AuthorityGeneration=r.ReadUInt64(); q.Sequence=r.ReadUInt32(); q.Target=r.ReadUInt64(); q.X=r.ReadSingle(); q.Y=r.ReadSingle(); q.Z=r.ReadSingle(); q.Vx=r.ReadSingle(); q.Vy=r.ReadSingle(); q.Vz=r.ReadSingle(); return q.Valid;
            }} catch(ArgumentException) {q=default;return false;} catch(IOException) {q=default;return false;}
        }
    }
}
