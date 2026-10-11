using System;
using System.IO;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Core.World
{
    public struct DoorMotionState
    {
        public bool Opening, Slow, CanUse, Loading;
        public float Progress, X,Y,Z,Qx,Qy,Qz,Qw;
        public bool Valid => Protocol.Finite(Progress) && Progress >= 0 && Progress <= 10 &&
            Protocol.Finite(X) && Protocol.Finite(Y) && Protocol.Finite(Z) && Math.Abs(X)<=100000 && Math.Abs(Y)<=100000 && Math.Abs(Z)<=100000 &&
            Protocol.Finite(Qx) && Protocol.Finite(Qy) && Protocol.Finite(Qz) && Protocol.Finite(Qw) && Math.Abs(Qx*Qx+Qy*Qy+Qz*Qz+Qw*Qw-1)<.01f;
        internal void Write(byte[] bytes,int offset)
        {
            bytes[offset]=(byte)((Opening?1:0)|(Slow?2:0)|(CanUse?4:0)|(Loading?8:0));
            Protocol.WriteFloat(bytes,offset+1,Progress); Protocol.WriteFloat(bytes,offset+5,X); Protocol.WriteFloat(bytes,offset+9,Y); Protocol.WriteFloat(bytes,offset+13,Z);
            Protocol.WriteFloat(bytes,offset+17,Qx); Protocol.WriteFloat(bytes,offset+21,Qy); Protocol.WriteFloat(bytes,offset+25,Qz); Protocol.WriteFloat(bytes,offset+29,Qw);
        }
        internal static DoorMotionState Read(BinaryReader r) { byte f=r.ReadByte(); if(f>15) throw new ArgumentException("Invalid motion flags."); return new DoorMotionState {Opening=(f&1)!=0,Slow=(f&2)!=0,CanUse=(f&4)!=0,Loading=(f&8)!=0,Progress=r.ReadSingle(),X=r.ReadSingle(),Y=r.ReadSingle(),Z=r.ReadSingle(),Qx=r.ReadSingle(),Qy=r.ReadSingle(),Qz=r.ReadSingle(),Qw=r.ReadSingle()}; }
    }
    // Gameplay capability comes from tags as well as flags; no inferred universal locked bit.
    public enum InteractionTag : byte { Door = 0, Lock1 = 1, ElectricDoor = 2, Untagged = 3, Window = 4 }
    public enum WindowGlass : byte { Intact = 0, Cut = 1, Broken = 2 }
    public struct DoorState
    {
        public bool Open, Enabled, RequiresKey, RelockAvailable, BrokenHinge;
        public InteractionTag Tag;
        public bool HasMotion;
        public DoorMotionState Motion;
        public byte[] Encode() { if(!HasMotion) return new[] {(byte)1,Flags(),(byte)Tag}; if(!Motion.Valid) throw new ArgumentException("Invalid door motion."); var bytes=new byte[36]; bytes[0]=2; bytes[1]=Flags(); bytes[2]=(byte)Tag; Motion.Write(bytes,3); return bytes; }
        private byte Flags() => (byte)((Open ? 1 : 0) | (Enabled ? 2 : 0) | (RequiresKey ? 4 : 0) | (RelockAvailable ? 8 : 0) | (BrokenHinge ? 16 : 0));
        public static bool TryDecode(byte[] bytes, out DoorState state)
        {
            state = default(DoorState);
            if (bytes == null || !(bytes.Length == 3 && bytes[0] == 1 || bytes.Length == 36 && bytes[0] == 2) || bytes[1] > 31 || bytes[2] > 4) return false;
            byte f = bytes[1]; state = new DoorState { Open = (f & 1) != 0, Enabled = (f & 2) != 0, RequiresKey = (f & 4) != 0, RelockAvailable = (f & 8) != 0, BrokenHinge = (f & 16) != 0, Tag = (InteractionTag)bytes[2] };
            if(bytes[0]==2) { try {using(var r=new BinaryReader(new MemoryStream(bytes,3,bytes.Length-3))) state.Motion=DoorMotionState.Read(r); state.HasMotion=true; return state.Motion.Valid;} catch(ArgumentException) {return false;} } return true;
        }
    }
    public struct WindowState
    {
        public bool Open, Enabled, Locked, PaneVisible;
        public WindowGlass Glass;
        public InteractionTag Tag;
        public bool HasMotion;
        public DoorMotionState Motion;
        public byte[] Encode() { byte f=(byte)((Open?1:0)|(Enabled?2:0)|(Locked?4:0)|(PaneVisible?8:0)); if(!HasMotion) return new[] {(byte)1,f,(byte)Glass,(byte)Tag}; if(!Motion.Valid) throw new ArgumentException("Invalid window motion."); var bytes=new byte[37]; bytes[0]=2; bytes[1]=f; bytes[2]=(byte)Glass; bytes[3]=(byte)Tag; Motion.Write(bytes,4); return bytes; }
        public static bool TryDecode(byte[] bytes, out WindowState state)
        {
            state = default(WindowState);
            if (bytes == null || !(bytes.Length == 4 && bytes[0] == 1 || bytes.Length == 37 && bytes[0] == 2) || bytes[1] > 15 || bytes[2] > 2 || bytes[3] > 4 || ((bytes[1] & 4) != 0 && bytes[2] != 0)) return false;
            state = new WindowState { Open = (bytes[1] & 1) != 0, Enabled = (bytes[1] & 2) != 0, Locked = (bytes[1] & 4) != 0, PaneVisible = (bytes[1] & 8) != 0, Glass = (WindowGlass)bytes[2], Tag = (InteractionTag)bytes[3] };
            if(bytes[0]==2) {try {using(var r=new BinaryReader(new MemoryStream(bytes,4,bytes.Length-4))) state.Motion=DoorMotionState.Read(r);state.HasMotion=true;return state.Motion.Valid;}catch(ArgumentException){return false;}} return true;
        }
    }
    // Thread local because registry and Unity adapters assert their creating thread.
    public sealed class NetworkApplyScope : IDisposable
    {
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static int silentDepth;
        public static bool Active => depth != 0;
        public static bool Silent => silentDepth != 0;
        private readonly bool silent;
        private bool disposed;
        public NetworkApplyScope(bool silentPresentation = false) { depth++; silent = silentPresentation; if (silent) silentDepth++; }
        public void Dispose() { if (!disposed) { disposed = true; depth--; if (silent) silentDepth--; } }
    }
}
