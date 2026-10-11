using System;
using System.IO;
using System.Linq;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

internal static class Health068Tests
{
    internal static void Run(Action<bool,string> check)
    {
        var scene=new SceneContinuity(); var target=new NetworkSceneDescriptor("MadisonSt",0,false,1,2);
        scene.Begin(1,1,target); scene.Fail("native seat unavailable"); scene.Ready();
        check(!scene.LocalReady(target.SceneKey) && !scene.PeerReady(1,1,target.SceneKey) && !scene.BarrierOpen && scene.Target.HasTravelOccupants,"failed loading cannot reopen or consume travel occupants through delayed callbacks");
        scene.Begin(2,2,target); scene.LocalReady(target.SceneKey); scene.PeerReady(2,2,target.SceneKey); scene.Ready(); var ready=scene.Target; scene.Ready();
        check(ReferenceEquals(ready,scene.Target) && !ready.HasTravelOccupants && scene.State==SceneTransitionState.WorldReady,"WorldReady retires travel metadata once and duplicate baseline completion is idempotent");
        var buffer=new TransformBuffer();
        PlayerTransform Pose(uint n) => new PlayerTransform {PlayerId=2,SceneEpoch=2,Sequence=n,Tick=n*50,X=n*.1f,Yaw=359};
        for(uint i=1;i<=96;i++) buffer.Add(Pose(i),i*.05);
        bool sampled=buffer.TrySample(buffer.LastArrival,.1,out var pose);
        check(sampled && Math.Abs(pose.X-9.4f)<.001 && buffer.Count<=32,"remote interpolation retains sender timing after multiple bounded-buffer wraps");
        check(!buffer.Add(Pose(95),4.85) && buffer.TrySample(4.9,0,out pose) && Math.Abs(pose.X-9.6f)<.001,"stale movement cannot replace newest wrapped sample or regress its endpoint");
        var seated=Pose(97); seated.X=50; buffer.Add(seated,4.85);
        check(buffer.Count==1 && buffer.SnapCount==1 && buffer.TrySample(4.85,0,out pose) && pose.X==50,"teleport discards wrapped history without interpolating across scene-sized displacement");
        buffer.Clear(); buffer.Add(Pose(1),0); buffer.TrySample(0,0,out _);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(uint i=2;i<=10002;i++) {buffer.Add(Pose(i),i*.05);buffer.TrySample(i*.05,.1,out _);}
        check(GC.GetAllocatedBytesForCurrentThread()==before && buffer.Count<=32,"10000 remote movement receive/render iterations allocate zero bytes and retain bounded history");
        var motion=new DoorMotionState {Opening=true,CanUse=true,Loading=true,Progress=.37f,X=-12,Y=2,Z=33,Qw=1};
        var door=new DoorState {Open=true,Enabled=true,RelockAvailable=true,Tag=InteractionTag.Door,HasMotion=true,Motion=motion};
        var window=new WindowState {Open=true,Enabled=true,PaneVisible=true,Glass=WindowGlass.Cut,Tag=InteractionTag.Window,HasMotion=true,Motion=motion};
        byte[] Legacy(bool pane) {
            using(var stream=new MemoryStream()) using(var writer=new BinaryWriter(stream)) {
                writer.Write((byte)2);writer.Write((byte)11);
                if(pane) writer.Write((byte)WindowGlass.Cut);
                writer.Write((byte)(pane ? InteractionTag.Window : InteractionTag.Door));writer.Write((byte)13);
                foreach(float value in new[] {motion.Progress,motion.X,motion.Y,motion.Z,motion.Qx,motion.Qy,motion.Qz,motion.Qw}) writer.Write(value);
                return stream.ToArray();
            }
        }
        check(door.Encode().SequenceEqual(Legacy(false)) && window.Encode().SequenceEqual(Legacy(true)),"door/window motion encoders preserve protocol 10 bytes including flags and intermediate positions");
        door.Encode(); window.Encode(); before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++) {door.Encode();window.Encode();}
        check(GC.GetAllocatedBytesForCurrentThread()-before<=128000,"2000 door/window motion captures allocate only their final packet arrays");
    }
}
