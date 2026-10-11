using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.TransportHarness;

internal static class CooperativeTravelTests
{
    internal static void Run(Action<bool,string> check)
    {
        var buffered=new VehicleMotionBuffer();
        var moving=new VehicleMotion {SessionId=1,Epoch=1,AuthorityGeneration=1,Vehicle=new EntityRef(new NetworkEntityId(EntityScope.Static,8),1),Sequence=1,Tick=100,Qw=1};
        buffered.Add(moving,1); float last=0; bool monotonic=true;
        for(int frame=1;frame<300;frame++) {
            double now=1+frame*.01;
            if(frame%5==0) { moving.Sequence++; moving.Tick+=50; moving.X+=.25f; buffered.Add(moving,now+(frame%20==0 ? .035 : 0)); }
            buffered.SampleAt(now,out var from,out var to,out float alpha); float x=from.X+(to.X-from.X)*alpha;
            monotonic &= x>=last-.00001f; last=x;
        }
        check(monotonic && last>10,"Vehicle sender timeline does not rewind on delayed arrivals at 20 Hz");
        var stale=moving; stale.Sequence++; stale.Tick-=10;
        check(!buffered.Add(stale,5) && !buffered.Add(moving,5),"Vehicle buffer rejects stale sender ticks and duplicate sequences");
        long motionAllocation=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++) {moving.Sequence++; moving.Tick+=50; moving.X+=.01f; buffered.Add(moving,5+i*.05); buffered.SampleAt(5+i*.05,out _,out _,out _);}
        check(GC.GetAllocatedBytesForCurrentThread()==motionAllocation && buffered.Count<=32,"Steady vehicle interpolation uses fixed storage and allocates no managed objects");
        bool outerDisposed=false, innerDisposed=false, reported=false;
        IEnumerator Child() { try { yield return null; throw new InvalidOperationException("Injected native fixture failure"); } finally { innerDisposed=true; } }
        IEnumerator Parent() { try { yield return Child(); } finally { outerDisposed=true; } }
        var routine=GuardedRoutine.Run(Parent(),_=>reported=true);
        check(routine.MoveNext() && routine.Current==null,"Native routine guard preserves frame yield while owning nested iterators");
        check(!routine.MoveNext() && reported && outerDisposed && innerDisposed,"Nested native fixture failure disposes both iterator scopes before report");
        outerDisposed=innerDisposed=false; routine=GuardedRoutine.Run(Parent(),_=>{}); routine.MoveNext(); (routine as IDisposable).Dispose();
        check(outerDisposed && innerDisposed,"Native fixture cancellation disposes all suspended scopes");
        var readiness=new SceneContinuity(); var map=new NetworkSceneDescriptor("MadisonSt"); readiness.Begin(1,1,map); readiness.Loading();
        check(readiness.TimeoutSeconds==120 && readiness.PeerReady(1,1,map.SceneKey) && !readiness.BarrierOpen && !readiness.LocalLoaded,"Fast Client acknowledgement cannot bypass slow Host native readiness");
        check(readiness.LocalReady(map.SceneKey) && readiness.BarrierOpen && readiness.State==SceneTransitionState.BaselineSynchronizing && readiness.TimeoutSeconds==45,"Slow Host completion starts baseline phase with its own timeout");
        readiness.Ready(); readiness.PeerReady(1,1,map.SceneKey); readiness.LocalReady(map.SceneKey);
        check(readiness.State==SceneTransitionState.WorldReady,"Duplicate readiness cannot reopen completed baseline phase");
        bool duplicate=false; try { readiness.Begin(1,1,map); } catch(ArgumentException) { duplicate=true; }
        check(duplicate && readiness.State==SceneTransitionState.WorldReady,"Duplicate transition cannot mutate completed world readiness");
        readiness.Begin(2,2,map); readiness.Fail("Injected load failure");
        check(!readiness.PeerReady(2,2,map.SceneKey) && !readiness.LocalReady(map.SceneKey) && !readiness.BarrierOpen,"Load failure rejects late acknowledgements and remains frozen");
        readiness.Begin(3,3,map); readiness.LocalReady(map.SceneKey);
        check(!readiness.BarrierOpen && readiness.TimeoutSeconds==60 && readiness.PeerReady(3,3,map.SceneKey),"Fresh transition recovers failure and waits for slow Client");
        var motion=new VehicleMotion {SessionId=3,Epoch=2,AuthorityGeneration=4,Actor=2,Fuel=59,Throttle=.4f,Sequence=1,Tick=200,Vehicle=new EntityRef(new NetworkEntityId(EntityScope.Static,42),1),Qw=1,X=10,Vx=3,Ay=.2f};
        var bytes=new byte[VehicleMotionProtocol.Bytes];
        check(VehicleMotionProtocol.Write(motion,bytes) && VehicleMotionProtocol.Read(bytes,0,bytes.Length,out var m) && m.Ay==.2f && m.Tick==200 && m.AuthorityGeneration==4 && m.Actor==2 && m.Fuel==59 && m.Throttle==.4f,"Motion fixed semantic pose/velocity/angular/driver/fuel codec");
        for(int n=0;n<bytes.Length;n++) if(VehicleMotionProtocol.Read(bytes,0,n,out _)) throw new Exception("Motion truncation accepted");
        check(true,"Motion rejects every truncated packet length");
        check(!VehicleMotionProtocol.Read(bytes,0,bytes.Length+1,out _),"Motion rejects trailing packet bytes");
        motion.Vx=float.PositiveInfinity; check(!VehicleMotionProtocol.Write(motion,bytes),"Motion rejects infinite velocity"); motion.Vx=3;
        motion.Qw=0; check(!motion.Valid,"Motion rejects zero rotation"); motion.Qw=1;
        Array.Clear(bytes,22,4); check(!VehicleMotionProtocol.Read(bytes,0,bytes.Length,out _),"Motion rejects zero vehicle generation");
        var state=new WorldTimeState {SessionId=3,Epoch=2,Sequence=1,Hour=23.5f,Year=1,Day=1,Weekday=6,Week=3,TotalHours=200,RentHours=-2,DaysPlayed=2};
        var packet=CooperativeTimeProtocol.Encode(state);
        check(CooperativeTimeProtocol.Decode(packet,out var t,out _) && t.Hour==23.5f && t.RentHours==-2,"Shared time baseline roundtrip includes clock and native counters");
        check(!CooperativeTimeProtocol.Decode(packet.Take(20).ToArray(),out _,out _) && !CooperativeTimeProtocol.Decode(packet.Concat(new byte[1]).ToArray(),out _,out _),"Shared time truncation/trailing rejected");
        state.Hour=24; check(!state.Valid,"Shared time invalid hour rejected"); state.Hour=float.NaN; check(!state.Valid,"Shared time NaN rejected"); state.Hour=0; state.Weekday=7; check(!state.Valid,"Shared time invalid weekday rejected");
        var random=new Random(871); for(int i=0;i<10000;i++) { var b=new byte[random.Next(130)]; random.NextBytes(b); VehicleMotionProtocol.Read(b,0,b.Length,out _); CooperativeTimeProtocol.Decode(b,out _,out _); }
        check(true,"10000 malformed motion/time envelopes cannot throw");
        var sleep=new CooperativeSleep(); sleep.Begin(1);
        check(sleep.Frozen && !sleep.Acknowledge(2),"Sleep start freezes and stale acknowledgement fails");
        check(sleep.Acknowledge(1) && sleep.Phase==CooperativeSleepPhase.Running,"Sleep starts only after matching acknowledgement");
        check(!sleep.Acknowledge(1),"Repeated sleep start acknowledgement cannot wake");
        check(sleep.Wake(1) && sleep.Frozen,"Wake retains transaction freeze until acknowledgement");
        check(!sleep.Wake(1) && sleep.Acknowledge(1) && !sleep.Frozen,"Wake acknowledgement idempotent lifecycle");
        sleep.Begin(2); sleep.Fail(); check(sleep.Frozen,"Failed sleep stays frozen until recovery"); sleep.Clear(); check(!sleep.Frozen && sleep.Id==0,"Disconnect/scene boundary clears sleep lifecycle");
        var descriptor=new NetworkSceneDescriptor("MadisonSt",1,true);
        packet=SceneProtocol.Encode(new SceneMessage {Type=8,SessionId=3,Epoch=2,TransitionId=1,Target=descriptor});
        check(SceneProtocol.TryDecode(packet,out var scene) && scene.Target.VehicleVariant==1 && scene.Target.SecondVehicleAvailable,"Scene descriptor includes Host native vehicle variant/availability"); packet[21]=2;
        check(!SceneProtocol.TryDecode(packet,out _),"Scene rejects unknown native vehicle variant");
        Transport(check);
        ImpairedMotion(check);
    }
    private static void Transport(Action<bool,string> check)
    {
        using var host=new NetworkManager("travel-time",Console.WriteLine); using var client=new NetworkManager("travel-time",Console.WriteLine);
        client.World.PrepareBaseline=s=>InteractionHarness.BindPresentationBaseline(client,s);
        host.World.ConfigureRole(true); var a=new ManagedVehicleAdapter(); var car=host.World.Registry.RegisterStatic(new StaticEntityDescriptor("travel-time","MainScene","car[0]","vehicle",0),a,new EntityOwnership(OwnershipKind.System));
        host.ValidateSeat=(_,__,___,____,_____)=>true;
        host.Host(FreePort(),"Host"); host.SetLocalScene(true,new NetworkSceneDescriptor("MadisonSt").SceneKey); host.PublishStableScene(new NetworkSceneDescriptor("MadisonSt"));
        bool follow=false; client.HostSceneRequested+=s=>follow=true; client.Join("127.0.0.1",host.Port,"Client");
        void Pump(Func<bool> done) { var watch=Stopwatch.StartNew(); while(!done() && watch.Elapsed.TotalSeconds<8) { host.Poll(); client.Poll(); Thread.Sleep(5); } if(!done()) throw new Exception("Travel/time UDP timeout: "+host.Scenes.State+"/"+client.Scenes.State+" occupants="+a.State.Driver+"/"+a.State.Passenger+" pending="+a.State.PendingDriver+"/"+a.State.PendingPassenger+" alive="+a.IsAlive+" bind="+(host.World.Registry.GetBinding(car)==a)+" client="+(client.World.Registry.GetBinding(car) as IVehicleAdapter)?.State.OccupancyRevision+" reject="+host.Telemetry.Rejected); }
        Pump(()=>follow);
        check(!host.Scenes.BarrierOpen && !host.GameplayReady,"Different Client load duration retains loading barrier");
        client.MarkSceneLoaded(host.Scenes.Target.SceneKey); Pump(()=>host.GameplayReady && client.GameplayReady);
        host.RequireTimeBaseline=client.RequireTimeBaseline=true;
        check(!client.GameplayReady,"Native gameplay waits for fresh time baseline after world snapshot");
        var s=new WorldTimeState {Hour=23.9f,Year=1,Day=1,Weekday=6,Week=0,TotalHours=3,RentHours=2,DaysPlayed=2};
        int applications=0; client.ApplyWorldTime=_=>{applications++; return true;};
        host.PublishTime(s); Pump(()=>host.GameplayReady && client.GameplayReady);
        check(applications==1 && client.LastWorldTime.Hour==23.9f,"Time baseline applies once before native gameplay opens");
        check(!client.BeginSleep(false),"Client cannot initiate global sleep");
        check(!host.BeginSleep(true),"Mission sleep denied without both seats");
        host.RequestSeat(car,SeatRole.Passenger,SeatAction.Enter,new EntityRef(new NetworkEntityId(EntityScope.Static,0x7FFFFFFFFFFFFFFEUL),1)); Pump(()=>a.State.PendingPassenger==1);
        check(a.State.PendingPassenger==1,"Host seat mutation executes without result event subscribers");
        host.RequestSeat(car,SeatRole.Passenger,SeatAction.Confirm); Pump(()=>a.State.Passenger==1 && (client.World.Registry.GetBinding(car) as IVehicleAdapter)?.State.Passenger==1);
        client.RequestSeat(car,SeatRole.Driver,SeatAction.Enter); Pump(()=>a.State.PendingDriver==2 && (client.World.Registry.GetBinding(car) as IVehicleAdapter)?.State.PendingDriver==2); client.RequestSeat(car,SeatRole.Driver,SeatAction.Confirm); Pump(()=>a.State.Driver==2);
        check(host.BeginSleep(true) && !host.GameplayReady,"Host passenger begins mission sleep with both confirmed roles");
        Pump(()=>host.Sleep.Phase==CooperativeSleepPhase.Running && client.Sleep.Frozen);
        check(!host.CanGlobalAction(true,out _) && host.RequestSeat(car,SeatRole.Passenger,SeatAction.Exit)==0,"Sleep freezes travel and new seat transactions");
        check(!client.SendDriverInput(car,1,1,0),"Sleep freezes Client driver inputs");
        s.Hour=.1f; s.Day=2; s.Weekday=0; s.Week=1; s.TotalHours=4; s.RentHours=1; s.DaysPlayed=3;
        check(host.WakeSleep(s),"Host publishes canonical midnight wake result"); Pump(()=>!host.Sleep.Frozen && !client.Sleep.Frozen);
        check(client.LastWorldTime.Weekday==0 && client.LastWorldTime.Week==1 && client.LastWorldTime.TotalHours==4,"Wake replicates day/week/hour effects without duplicate advancement");
        ulong token=host.Session.SessionId; uint epoch=host.World.Registry.SceneEpoch;
        host.BeginSceneTransition(new NetworkSceneDescriptor("PlayerHideout",1,true)); Pump(()=>client.Scenes.Target.Map=="PlayerHideout");
        check(!client.TimeReady && host.World.Registry.SceneEpoch==epoch+1,"Scene boundary invalidates old time/vehicle baselines");
        host.MarkSceneLoaded(host.Scenes.Target.SceneKey); client.MarkSceneLoaded(host.Scenes.Target.SceneKey); Pump(()=>host.World.State==WorldSyncState.WorldReady && client.World.State==WorldSyncState.WorldReady);
        host.PublishTime(s); Pump(()=>host.GameplayReady && client.GameplayReady);
        check(host.Session.SessionId==token && host.PeerConnected && client.PeerConnected,"Two-seat return retains transport and session token");
        check(host.BeginSleep(false),"Hideout sleep has no absent vehicle/passenger requirement"); Pump(()=>host.Sleep.Phase==CooperativeSleepPhase.Running);
        client.Disconnect(); Pump(()=>host.Session.State==SessionState.Hosting);
        check(!host.Sleep.Frozen && host.CanGlobalAction(true,out _),"Disconnect clears sleep and restores supported solo Host actions");
        host.Disconnect();
        check(!host.Scenes.Active && host.World.Registry.SceneEpoch==1 && host.World.Registry.Count==0,"Stopping Host clears former scene epoch and sealed bindings");
        using var newHost=new NetworkManager("travel-time"); newHost.Host(FreePort(),"NewHost");
        var target=new NetworkSceneDescriptor("MadisonSt"); newHost.SetLocalScene(true,target.SceneKey); newHost.PublishStableScene(target);
        host.HostSceneRequested+=d=>host.MarkSceneLoaded(d.SceneKey); host.Join("127.0.0.1",newHost.Port,"FormerHost");
        var joinWatch=Stopwatch.StartNew();
        while(!host.GameplayReady && joinWatch.Elapsed.TotalSeconds<8) {newHost.Poll(); host.Poll(); newHost.PublishTime(s); Thread.Sleep(5);}
        check(host.GameplayReady && host.Scenes.Target.Map=="MadisonSt" && host.World.Registry.SceneEpoch==1,"Former Host joins new Host with lower epoch without stale scene rejection");
    }
    private static int FreePort() { using var s=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Dgram,System.Net.Sockets.ProtocolType.Udp); s.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0)); return ((System.Net.IPEndPoint)s.LocalEndPoint).Port; }
    private static void ImpairedMotion(Action<bool,string> check)
    {
        foreach(byte driver in new byte[] {1,2}) {
            using var host=new NetworkManager("motion-loss"); using var client=new NetworkManager("motion-loss");
            host.Host(FreePort(),"Host");
            var a=new ManagedVehicleAdapter {State=new VehicleState {PassengerSeat=true,Driver=driver,Passenger=(byte)(3-driver)}};
            var car=host.World.Registry.RegisterStatic(new StaticEntityDescriptor("motion-loss","MainScene","car[0]","vehicle",0),a,new EntityOwnership(OwnershipKind.System));
            client.World.PrepareBaseline=s=>InteractionHarness.BindPresentationBaseline(client,s);
            var scene=new NetworkSceneDescriptor("MadisonSt"); host.SetLocalScene(true,scene.SceneKey); host.PublishStableScene(scene);
            client.HostSceneRequested+=s=>client.MarkSceneLoaded(s.SceneKey);
            using var link=new UdpConditionLink(0,host.Port,40,100,20,20);
            client.Join("127.0.0.1",link.Port,"Client"); var timer=Stopwatch.StartNew();
            while((!host.GameplayReady || !client.GameplayReady) && timer.Elapsed.TotalSeconds<8) { host.Poll(); client.Poll(); Thread.Sleep(5); }
            check(host.GameplayReady && client.GameplayReady,"Vehicle loss link establishes world baseline role "+driver);
            ulong revision=host.World.Registry.WorldRevision; timer.Restart(); double next=0; float prior=0; bool rewound=false; int driverArrivals=0;
            while(timer.Elapsed.TotalSeconds<2.5) {
                host.Poll(); client.Poll(); double now=timer.Elapsed.TotalSeconds;
                if(now>=next) { next=now+.05; (driver==1 ? host : client).SendDriverInput(car,.2f,.4f,0); (driver==1 ? host : client).SendVehicleMotion(new VehicleMotion {Vehicle=car,X=(float)now,Qw=1,Vx=1,Ay=.1f}); }
                var replica=(driver==1 ? client : host).World.Registry.GetBinding(car) as IVehicleAdapter;
                if(replica!=null) { if(replica.State.X<prior) rewound=true; prior=replica.State.X; }
                if(host.Vehicles.Latest(car,host.Now)!=null) driverArrivals++;
                Thread.Sleep(5);
            }
            check((driver==1 ? client.VehicleMotionReceived : host.VehicleMotionReceived)>10 && driverArrivals>20 && !rewound,"Driver-owned motion survives latency jitter loss duplication role "+driver);
            check(host.World.Registry.WorldRevision==revision && host.PeerConnected,"Motion loss causes no reliable backlog/world revisions role "+driver);
            check(!(driver==1 ? client : host).SendVehicleMotion(new VehicleMotion {Vehicle=car,Qw=1}),"Observer cannot author driver's vehicle motion role "+driver);
            check(link.Dropped>0 && link.Duplicated>0 && link.QueueOverflow==0 && link.Error.Length==0,"Actual datagram impairment remains bounded role "+driver);
            // Drain the final delayed/duplicated datagrams before measuring the 350-ms
            // deadman. Otherwise a valid last input can arrive during the assertion window.
            double until=host.Now+.8; while(host.Now<until) { host.Poll(); client.Poll(); Thread.Sleep(5); }
            check(host.Vehicles.Latest(car,host.Now)==null,"Validated input deadman resolves lost driver stream role "+driver);
        }
    }
}
