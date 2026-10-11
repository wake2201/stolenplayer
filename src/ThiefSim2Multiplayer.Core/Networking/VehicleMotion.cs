using System;
using System.Runtime.InteropServices;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    // Motion has its own unreliable sequence. Seat revisions remain on the reliable world stream.
    public struct VehicleMotion
    {
        public ulong SessionId;
        public uint Epoch, Sequence, Tick;
        public EntityRef Vehicle;
        public byte Actor;
        public ulong AuthorityGeneration;
        public float Fuel, FuelUsed, Steer, Throttle, Brake, Handbrake;
        public bool EngineRunning;
        public float X,Y,Z,Qx,Qy,Qz,Qw,Vx,Vy,Vz,Ax,Ay,Az;
        public bool Valid => SessionId!=0 && Epoch!=0 && Sequence!=0 && Vehicle.Id.IsValid && Vehicle.Generation!=0 && Actor<=2 && AuthorityGeneration!=0 &&
            Bounded(Fuel,10000) && Bounded(FuelUsed,10000) && Bounded(Steer,2) && Bounded(Throttle,2) && Bounded(Brake,1) && Brake>=0 && Bounded(Handbrake,1) && Handbrake>=0 &&
            Bounded(X,100000) && Bounded(Y,100000) && Bounded(Z,100000) && Bounded(Vx,200) && Bounded(Vy,200) && Bounded(Vz,200) &&
            Bounded(Ax,100) && Bounded(Ay,100) && Bounded(Az,100) && Bounded(Qx,1) && Bounded(Qy,1) && Bounded(Qz,1) && Bounded(Qw,1) &&
            Qx*Qx+Qy*Qy+Qz*Qz+Qw*Qw>.98f && Qx*Qx+Qy*Qy+Qz*Qz+Qw*Qw<1.02f;
        private static bool Bounded(float f,float limit) => Protocol.Finite(f) && Math.Abs(f)<=limit;
        public void Apply(VehicleState v) { v.X=X; v.Y=Y; v.Z=Z; v.Qx=Qx; v.Qy=Qy; v.Qz=Qz; v.Qw=Qw; v.Vx=Vx; v.Vy=Vy; v.Vz=Vz; v.Ax=Ax; v.Ay=Ay; v.Az=Az; v.EngineRunning=EngineRunning; v.Fuel=Fuel; }
    }
    public interface IVehicleMotionAdapter { void ApplyMotion(VehicleMotion motion, double receivedAt); }
    // Fixed-size sender timeline. Packet arrivals never restart interpolation at
    // an older target and no extrapolated position is pulled back on each arrival.
    public sealed class VehicleMotionBuffer
    {
        private struct Sample { public VehicleMotion Motion; public double Time; }
        private readonly Sample[] samples=new Sample[32];
        private int start, count;
        private double time, offset, rendered;
        private uint sequence, tick;
        public int Count => count;
        public bool Add(VehicleMotion motion,double arrival)
        {
            if(!motion.Valid || double.IsNaN(arrival) || double.IsInfinity(arrival) || arrival<0) return false;
            if(count>0) {
                int elapsed=unchecked((int)(motion.Tick-tick));
                if(!Protocol.IsNewer(motion.Sequence,sequence) || elapsed<=0) return false;
                var last=samples[(start+count-1)%samples.Length].Motion;
                if(last.AuthorityGeneration!=motion.AuthorityGeneration || last.Actor!=motion.Actor) { Clear(); return Add(motion,arrival); }
                double dx=motion.X-last.X,dy=motion.Y-last.Y,dz=motion.Z-last.Z;
                if(dx*dx+dy*dy+dz*dz>25) Clear();
                else time+=elapsed*.001;
            }
            if(count==0) {time=0; offset=arrival; rendered=0;}
            else offset=Math.Min(offset,arrival-time);
            if(count==samples.Length) {start=(start+1)%samples.Length; count--;}
            samples[(start+count++)%samples.Length]=new Sample {Motion=motion,Time=time};
            sequence=motion.Sequence; tick=motion.Tick; return true;
        }
        public bool SampleAt(double now,out VehicleMotion from,out VehicleMotion to,out float amount)
        {
            from=to=default(VehicleMotion); amount=0;
            if(count==0 || double.IsNaN(now) || double.IsInfinity(now)) return false;
            double newest=samples[(start+count-1)%samples.Length].Time;
            rendered=Math.Max(rendered,Math.Min(newest,now-offset-.1));
            while(count>2 && samples[(start+1)%samples.Length].Time<=rendered) {start=(start+1)%samples.Length; count--;}
            var a=samples[start]; var b=count>1 ? samples[(start+1)%samples.Length] : a;
            from=a.Motion; to=b.Motion;
            amount=b.Time>a.Time ? (float)Math.Max(0,Math.Min(1,(rendered-a.Time)/(b.Time-a.Time))) : 1;
            return true;
        }
        public void Clear() {start=count=0; time=offset=rendered=0; sequence=tick=0;}
    }
    public static class VehicleMotionProtocol
    {
        public const byte Type=14;
        public const int Bytes=120;
        [StructLayout(LayoutKind.Explicit)] private struct FloatBits { [FieldOffset(0)] public float Float; [FieldOffset(0)] public uint Bits; }
        private static void Put(byte[] b,int o,ulong v,int n) { for(int i=0;i<n;i++) b[o+i]=(byte)(v>>(i*8)); }
        private static ulong Get(byte[] b,int o,int n) { ulong v=0; for(int i=0;i<n;i++) v|=(ulong)b[o+i]<<(i*8); return v; }
        private static void Put(byte[] b,int o,float v) => Put(b,o,new FloatBits {Float=v}.Bits,4);
        private static float Float(byte[] b,int o) => new FloatBits {Bits=(uint)Get(b,o,4)}.Float;
        public static bool Write(VehicleMotion m,byte[] b)
        {
            if(!m.Valid || b==null || b.Length!=Bytes) return false;
            b[0]=Type; Put(b,1,m.SessionId,8); Put(b,9,m.Epoch,4); b[13]=(byte)m.Vehicle.Id.Scope; Put(b,14,m.Vehicle.Id.Value,8); Put(b,22,m.Vehicle.Generation,4); Put(b,26,m.Sequence,4); Put(b,30,m.Tick,4);
            Put(b,34,m.X); Put(b,38,m.Y); Put(b,42,m.Z); Put(b,46,m.Qx); Put(b,50,m.Qy); Put(b,54,m.Qz); Put(b,58,m.Qw);
            Put(b,62,m.Vx); Put(b,66,m.Vy); Put(b,70,m.Vz); Put(b,74,m.Ax); Put(b,78,m.Ay); Put(b,82,m.Az);
            b[86]=m.Actor; Put(b,87,m.AuthorityGeneration,8); Put(b,95,m.Fuel); Put(b,99,m.Steer); Put(b,103,m.Throttle); Put(b,107,m.Brake); Put(b,111,m.Handbrake); b[115]=(byte)(m.EngineRunning ? 1 : 0); Put(b,116,m.FuelUsed); return true;
        }
        public static bool Read(byte[] b,int o,int n,out VehicleMotion m)
        {
            m=default(VehicleMotion); if(b==null || n!=Bytes || o<0 || o>b.Length-n || b[o]!=Type || b[o+115]>1) return false;
            try { m=new VehicleMotion {SessionId=Get(b,o+1,8),Epoch=(uint)Get(b,o+9,4),Vehicle=new EntityRef(new NetworkEntityId((EntityScope)b[o+13],Get(b,o+14,8)),(uint)Get(b,o+22,4)),Sequence=(uint)Get(b,o+26,4),Tick=(uint)Get(b,o+30,4),
                X=Float(b,o+34),Y=Float(b,o+38),Z=Float(b,o+42),Qx=Float(b,o+46),Qy=Float(b,o+50),Qz=Float(b,o+54),Qw=Float(b,o+58),Vx=Float(b,o+62),Vy=Float(b,o+66),Vz=Float(b,o+70),Ax=Float(b,o+74),Ay=Float(b,o+78),Az=Float(b,o+82),Actor=b[o+86],AuthorityGeneration=Get(b,o+87,8),Fuel=Float(b,o+95),Steer=Float(b,o+99),Throttle=Float(b,o+103),Brake=Float(b,o+107),Handbrake=Float(b,o+111),EngineRunning=b[o+115]!=0,FuelUsed=Float(b,o+116)};
            return m.Valid; } catch(ArgumentException) { m=default(VehicleMotion); return false; }
        }
    }
}
