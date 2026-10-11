using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using LiteNetLib;
using ThiefSim2Multiplayer.Core.Diagnostics;
using ThiefSim2Multiplayer.Core.World;

internal static class FrameStabilityTests
{
    internal static void Run(Action<bool,string> check)
    {
        var profile=new FrameStabilityProfiler {Enabled=true,SpikeMilliseconds=100};
        for(int i=0;i<200;i++) {profile.Record(RuntimePhase.VehicleBinding,2); profile.Frame(5,3,7,4,10,12);}
        profile.Frame(125,3,7,4,128,256);
        string summary=profile.Summary();
        check(summary.Contains("p99Ms=") && profile.SpikeCount==1 && summary.Contains("epoch=7") && summary.Contains("queue=256") && summary.Contains("VehicleBinding=2.000"),"Frame spikes retain bounded phase/state/epoch/packet context");
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++) {profile.Record(RuntimePhase.RegistryRebuild,.01); profile.Frame(4,3,7,4,1,0);}
        check(GC.GetAllocatedBytesForCurrentThread()==allocated,"Steady frame/phase collection allocates no managed objects");
        for(int i=0;i<32;i++) profile.Frame(110,3,7,4,2,0);
        check(profile.Summary().Split("SPIKE frame=").Length==17 && profile.SpikeCount==33,"Spike export retains last 16 timelines without unbounded growth");
        profile.Reset(); check(profile.Frames.Count==0 && profile.SpikeCount==0,"Frame profile reset clears previous-session evidence");
        var window=new TimingWindow(); window.Add(1000); for(int i=0;i<8192;i++) window.Add(4);
        check(window.Count==8192 && window.Average==4 && window.Summary().Contains("maxMs=4.0000"),"Timing window rolls beyond capacity and evicts old spikes without losing later evidence");
        profile.Enabled=false; profile.Record(RuntimePhase.BaselineApply,200); profile.Frame(200,0,0,0,0,0);
        check(profile.SpikeCount==0 && profile.Phases[(int)RuntimePhase.BaselineApply].Count==0,"Disabled profiler records no phase/frame work");
        var before=new StaticEntityDescriptor("build","MainScene","PLAYER CAR[20]","coop-vehicle-v2-rear",0,"VehiclesManager.firstCar");
        var after=new StaticEntityDescriptor("build","MainScene","PLAYER CAR[19]","coop-vehicle-v2-rear",0,"VehiclesManager.firstCar");
        check(before.DeriveId().Equals(after.DeriveId()),"Semantic vehicle identity survives native actor root reparenting");
        check(!before.DeriveId().Equals(new StaticEntityDescriptor("build","MainScene","PLAYER CAR[20]","coop-vehicle-v2-rear",0,"VehiclesManager.van").DeriveId()),"Verified native vehicle slots remain distinct");
        var authoritative=new NetworkEntityRegistry(true,1);
        var descriptor=new StaticEntityDescriptor("build","scene","door[0]","timing-fixture",0);
        authoritative.RegisterStatic(descriptor,new TestEntityAdapter {Value=73},new EntityOwnership(OwnershipKind.System));
        var replica=new NetworkEntityRegistry(false,1) {ProfileBaseline=true};
        replica.RegisterStatic(descriptor,new TestEntityAdapter(),new EntityOwnership(OwnershipKind.System));
        check(replica.ApplyBaseline(authoritative.Capture(),authoritative.WorldRevision,_=>null) && replica.DevelopmentSummary()==authoritative.DevelopmentSummary() && replica.BaselineAdapterCounts[1]==1 && replica.BaselineAdapterMilliseconds[1]>=0,"Baseline cost profiling preserves canonical transaction and records applied native type");
        replica.ProfileBaseline=false;
        check(replica.ApplyBaseline(authoritative.Capture(),authoritative.WorldRevision,_=>null) && replica.BaselineAdapterCounts[1]==0 && replica.BaselineValidationMilliseconds==0,"Disabled baseline profiling clears old measurements without changing application");
        Burst(check);
    }
    private static void Burst(Action<bool,string> check)
    {
        var received=new List<int>(); var serverEvents=new EventBasedNetListener(); var clientEvents=new EventBasedNetListener();
        var server=new NetManager(serverEvents) {AutoRecycle=true}; var client=new NetManager(clientEvents) {AutoRecycle=true};
        serverEvents.ConnectionRequestEvent+=r=>r.AcceptIfKey("bounded");
        bool connected=false; clientEvents.PeerConnectedEvent+=_=>connected=true;
        bool slow=false;
        serverEvents.NetworkReceiveEvent+=(_,reader,__,___)=>{ received.Add(reader.GetInt()); if(slow) Thread.Sleep(5); };
        using var socket=new UdpClient(0); int port=((IPEndPoint)socket.Client.LocalEndPoint).Port; socket.Close();
        try {
            check(server.Start(port) && client.Start(),"Bounded reliable burst sockets start");
            var peer=client.Connect("127.0.0.1",port,"bounded");
            var clock=Stopwatch.StartNew();
            while(!connected && clock.ElapsedMilliseconds<5000) {server.PollEvents(); client.PollEvents(); Thread.Sleep(5);}
            check(connected,"Bounded reliable burst peers connect");
            for(int i=0;i<300;i++) peer.Send(BitConverter.GetBytes(i),DeliveryMethod.ReliableOrdered);
            clock.Restart(); while(server.PendingEventCount<300 && clock.ElapsedMilliseconds<5000) {client.PollEvents(); Thread.Sleep(5);}
            check(server.PendingEventCount==300,"Reliable event backlog collected before owner-thread polling");
            server.PollEvents(7);
            check(received.Count==7 && server.PendingEventCount==293 && server.LastProcessedEvents==7,"Event-count budget preserves all 293 deferred reliable callbacks");
            slow=true; server.PollEvents(128,.1); slow=false;
            check(received.Count==8 && server.PendingEventCount==292,"Time budget yields between atomic callbacks without dropping remainder");
            while(server.PendingEventCount>0) server.PollEvents(7);
            bool ordered=received.Count==300; for(int i=0;i<received.Count;i++) ordered &= received[i]==i;
            check(ordered && server.PendingEventCount==0,"All 300 reliable callbacks delivered once and in original order after bounded polls");
        } finally {client.Stop(); server.Stop();}
    }
}
