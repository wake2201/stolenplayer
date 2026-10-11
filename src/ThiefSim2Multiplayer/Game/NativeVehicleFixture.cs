using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class SharedVehicleController
    {
        // One native game, actual EVP physics, two localhost transports; the second actor is a harness.
        // This deliberately cannot claim human or second-native-machine acceptance.
        internal IEnumerator NativeDrivingFixture()
        {
            if(Online || fixture) throw new InvalidOperationException("Driving fixture requires offline backed-up session.");
            var p=locator.Find(); if(!p || GameAccess.Bool(p,"is_in_vehicle")) throw new InvalidOperationException("Exit native driver seat before fixture.");
            var car=CurrentFixtureCar(p);
            if(!car) throw new InvalidOperationException("Verified native passenger vehicle unavailable.");
            var actorOrigin=p.transform.position; var actorRotation=p.transform.rotation; var origin=car.transform.position; var orientation=car.transform.rotation;
            var body=car.GetComponent<Rigidbody>(); var velocity=body.velocity; var angular=body.angularVelocity; bool kinematic=body.isKinematic;
            var input=car.GetComponent(Type.GetType("EVP.VehicleStandardInput, Assembly-CSharp",true)) as Behaviour; bool inputOn=input.enabled;
            var camera=GameAccess.Get(p,"MainCam") as Camera; int cameraId=camera.GetInstanceID(); var inventory=GameAccess.Get(p,"invScript");
            bool engineBefore=GameAccess.Bool(car,"engine_running"), seatedBefore=GameAccess.Bool(car,"is_sitting_inside");
            int checks=0; fixture=true; var nativeDriver=new LocalDriverState();
            void Check(bool ok,string label) { if(!ok) throw new InvalidOperationException("Native EVP fixture FAIL: "+label); checks++; log("[TS2MP] NATIVE EVP FIXTURE PASS: "+label); }
            try {
                foreach(byte driver in new byte[] {2,1}) {
                    using(var host=new NetworkManager("native-evp",log)) using(var client=new NetworkManager("native-evp",log)) {
                        var socket=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Dgram,System.Net.Sockets.ProtocolType.Udp);
                        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0)); int port=((System.Net.IPEndPoint)socket.LocalEndPoint).Port; socket.Dispose();
                        Check(host.Host(port,"NativeHost"),"native Host transport starts role "+driver);
                        body.isKinematic=true;
                        // Exercise a real stopped-car role handover. Teleporting the
                        // second role back through cached native wheel/contact state
                        // is not ordinary cooperative driving and invalidates evidence.
                        if(driver==2) { body.position=origin; body.rotation=orientation; car.transform.SetPositionAndRotation(origin,orientation); Physics.SyncTransforms(); }
                        var a=new VehicleNetworkAdapter(car,driver,driver==1); var descriptor=UnityNetworkEntityAdapter.Describe(car,"native-evp","native-evp-v1",0);
                        VehicleState.TryDecode(a.State.Encode(),out var initialState);
                        var replica=new ManagedVehicleAdapter {State=initialState};
                        IVehicleAdapter hostCar=driver==1 ? (IVehicleAdapter)a : replica;
                        IVehicleAdapter clientCar=driver==2 ? (IVehicleAdapter)a : replica;
                        a.Entity=host.World.Registry.RegisterStatic(descriptor,hostCar,new EntityOwnership(OwnershipKind.System));
                        client.World.PrepareRegistry=()=> { if(client.World.Registry.Count==0) client.World.Registry.RegisterStatic(descriptor,clientCar,new EntityOwnership(OwnershipKind.System)); };
                        host.ValidateSeat=(_,__,___,____,_____)=>true;
                        var map=new NetworkSceneDescriptor("MadisonSt"); host.SetLocalScene(true,map.SceneKey); host.PublishStableScene(map);
                        client.HostSceneRequested+=target=>client.MarkSceneLoaded(target.SceneKey); client.Join("127.0.0.1",port,"HarnessClient");
                        double deadline=host.Now+8;
                        void PollProof() { host.Poll(); client.Poll(); if(host.Now>deadline) throw new InvalidOperationException("Native EVP fixture phase timed out: pending="+a.State.PendingDriver+"/"+a.State.PendingPassenger+"; error="+host.LastError+"/"+client.LastError); }
                        while(!host.GameplayReady || !client.GameplayReady) { host.Poll(); client.Poll(); if(host.Now>deadline) throw new InvalidOperationException("Native EVP baseline timeout."); yield return null; }
                        var driverNetwork=driver==1 ? host : client; var passengerNetwork=driver==1 ? client : host;
                        fixtureNetwork=host; fixtureCar=a; fixtureDriver=driver; input.enabled=false;
                        void SetInput(float steer,float forward,float handbrake,bool running=true) {fixtureInput=new DriverInput {Steer=steer,Forward=forward,Handbrake=handbrake,EngineRunning=running}; driverNetwork.SendDriverInput(a.Entity,steer,forward,handbrake,running);}
                        driverNetwork.RequestSeat(a.Entity,SeatRole.Driver,SeatAction.Enter);
                        while(a.State.PendingDriver!=driver || replica.State.PendingDriver!=driver) { PollProof(); yield return null; }
                        {
                            nativeDriver.Capture(p,a);
                            p.transform.position=a.DriverSeat.position;
                            GameAccess.Call(car,"EnterVehicle");
                            car.GetType().GetMethod("RunEngine").Invoke(car,new object[] {true});
                            p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {true});
                            deadline=host.Now+3;
                            while(!NativeAtSeat(p,a,SeatRole.Driver)) { nativeDriver.Tick(host.Now,log); host.Poll(); client.Poll(); if(host.Now>deadline) throw new InvalidOperationException("Native driver attachment timeout."); yield return null; }
                        }
                        driverNetwork.RequestSeat(a.Entity,SeatRole.Driver,SeatAction.Confirm);
                        deadline=host.Now+8;
                        while(a.State.Driver!=driver || replica.State.Driver!=driver) { PollProof(); yield return null; }
                        passengerNetwork.RequestSeat(a.Entity,SeatRole.Passenger,SeatAction.Enter,new EntityRef(new NetworkEntityId(EntityScope.Static,0x7FFFFFFFFFFFFFFEUL),1));
                        while(a.State.PendingPassenger==0 || replica.State.PendingPassenger==0) { PollProof(); yield return null; }
                        passengerNetwork.RequestSeat(a.Entity,SeatRole.Passenger,SeatAction.Confirm);
                        while(!VehicleAuthority.BothConfirmed(host.World.Registry) || replica.State.Passenger==0 || a.State.Passenger==0) { PollProof(); yield return null; }
                        Check(a.State.Driver==driver && a.State.Passenger==3-driver,"both semantic seats confirmed role "+driver);
                        // RunCar opens the original town selector after its delayed
                        // entry sequence. Close that native UI after it has run, rather
                        // than waiting forever for FixedUpdate in a paused fixture.
                        {
                            yield return new WaitForSecondsRealtime(.6f);
                            p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {true});
                        }
                        Check(Time.timeScale>0,"native driving fixture is unpaused role "+driver);
                        a.SyncAuthority(); body.velocity=Vector3.zero; body.angularVelocity=Vector3.zero;
                        Check(a.Simulates && !body.isKinematic && a.State.Driver==driver,"confirmed local native simulation owner role "+driver);
                        // A teleported controlled fixture must settle onto its real
                        // wheels before acceleration is measured. Falling/setup motion
                        // is not evidence that validated throttle drove the vehicle.
                        double settle=host.Now+5, settleStarted=host.Now, nextSettle=0;
                        while(host.Now<settle) {
                            host.Poll(); client.Poll();
                            if(host.Now>=nextSettle) { nextSettle=host.Now+.05; SetInput(0,0,1); driverNetwork.SendVehicleMotion(a.Motion()); }
                            nativeDriver.Tick(host.Now,log);
                            // A wall-clock deadline must still advance if native UI pauses.
                            yield return null;
                            if(host.Now-settleStarted>.2 && body.velocity.magnitude<.2f && car.GetComponentsInChildren<WheelCollider>().Count(w=>w.isGrounded)>=2) break;
                        }
                        Check(car.GetComponentsInChildren<WheelCollider>().Count(w=>w.isGrounded)>=2,"native wheels contact ground before acceleration role "+driver);
                        var start=body.position; float maxSpeed=0; double end=host.Now+2.5, next=0, nextDriveProof=0;
                        ulong revision=host.World.Registry.WorldRevision;
                        while(host.Now<end) {
                            host.Poll(); client.Poll();
                            if(host.Now>=next) { next=host.Now+.05; SetInput(.08f,.45f,0); driverNetwork.SendVehicleMotion(a.Motion()); }
                            nativeDriver.Tick(host.Now,log);
                            maxSpeed=Mathf.Max(maxSpeed,body.velocity.magnitude);
                            if(host.Now>=nextDriveProof) {
                                nextDriveProof=host.Now+.5;
                                var q=host.Vehicles.Latest(a.Entity,host.Now); RaycastHit obstacle;
                                string ahead=Physics.Raycast(body.worldCenterOfMass+car.transform.forward*1.8f,car.transform.forward,out obstacle,2,~0,QueryTriggerInteraction.Ignore) ? obstacle.collider.name+"@"+obstacle.distance.ToString("F3") : "clear";
                                string ground=string.Join(",",car.GetComponentsInChildren<WheelCollider>().Select(w=>{WheelHit h;return w.GetGroundHit(out h) && h.collider ? h.collider.name : "none";}));
                                log("[TS2MP] NATIVE DRIVE PROOF role="+driver+" input="+(q?.Forward.ToString() ?? "missing")+" engine="+GameAccess.Bool(car,"engine_running")+" throttle="+GameAccess.Get(a.Drive,"throttleInput")+" brake="+GameAccess.Get(a.Drive,"brakeInput")+" handbrake="+GameAccess.Get(a.Drive,"handbrakeInput")+" enabled="+((Behaviour)a.Drive).enabled+" sleeping="+body.IsSleeping()+" kinematic="+body.isKinematic+" paused="+a.Drive.GetType().GetProperty("paused").GetValue(a.Drive,null)+" bodies="+string.Join(",",car.GetComponentsInChildren<Rigidbody>().Select(r=>r.name+":"+r.isKinematic))+" position="+body.position.ToString("F3")+" ahead="+ahead+" wheelGround="+ground);
                            }
                            yield return new WaitForEndOfFrame();
                        }
                        float travelled=Vector3.Distance(body.position,start);
                        Check(travelled>.25f && maxSpeed>.25f,"actual EVP acceleration/displacement role "+driver+" metres="+travelled.ToString("F3")+" maxSpeed="+maxSpeed.ToString("F3"));
                        Check(fixtureInput!=null && (float)GameAccess.Get(a.Drive,"throttleInput")>0,"local fixture input reaches original EVP without waiting for Host input role "+driver);
                        Check(host.World.Registry.WorldRevision==revision && (driver==1 ? client.VehicleMotionReceived : host.VehicleMotionReceived)>5,"driver-owned motion uses separate sequenced stream role "+driver);
                        Check(Math.Abs(replica.State.X-a.State.X)<3 && Math.Abs(replica.State.Z-a.State.Z)<3,"native sampled motion converges on transport replica role "+driver);
                        Check(!p.transform.IsChildOf(car.transform) && Vector3.Distance(p.transform.position,a.DriverSeat.position)<.05f && camera.GetInstanceID()==cameraId,"native driver reference/camera retained on local simulator role "+driver);
                        if(driver==2) {
                            var live=body.position; var reliable=a.State.Encode();
                            a.ApplyState(reliable); a.ApplyMotion(new VehicleMotion {Actor=2,AuthorityGeneration=a.State.AuthorityGeneration,X=origin.x,Y=origin.y,Z=origin.z,Qw=1},host.Now);
                            Check(!body.isKinematic && Vector3.Distance(body.position,live)<.001f,"Client owner ignores old motion and reliable seat baselines without disabling native physics");
                        }
                        using(var presentation=new VehiclePresentation()) {
                            var root=new GameObject("TS2MP native fixture remote occupant");
                            try { foreach(var role in new[] {SeatRole.Driver,SeatRole.Passenger}) Check(presentation.Apply(new PlayerTransform {State=new PlayerReplicationState {Vehicle=new VehicleOccupancyState {Seat=role,PresentationKey=a.PresentationKey}}},root.transform,log),"native sanitized remote seated rig role "+role+" driver "+driver); }
                            finally { UnityEngine.Object.Destroy(root); }
                        }
                        end=host.Now+4; next=0; bool stopped=false; double nextProof=0;
                        float startSpeed=Mathf.Abs(a.Speed), minimum=startSpeed;
                        // Vanilla opposite-direction input is the service brake. Latch neutral when stopped so
                        // automatic reversing does not invalidate the stopping assertion. Keep input at 20 Hz.
                        while(host.Now<end) {
                            host.Poll(); client.Poll(); float actual=Mathf.Abs(a.Speed); minimum=Mathf.Min(minimum,actual);
                            nativeDriver.Tick(host.Now,log);
                            if(actual<.2f) stopped=true;
                            if(host.Now>=next) { next=host.Now+.05; SetInput(0,stopped ? 0 : a.Speed>0 ? -1 : 1,1); driverNetwork.SendVehicleMotion(a.Motion()); }
                            if(host.Now>=nextProof) {
                                nextProof=host.Now+.5;
                                var q=host.Vehicles.Latest(a.Entity,host.Now);
                                log("[TS2MP] NATIVE BRAKE PROOF role="+driver+" speed="+a.Speed+" input="+(q?.Forward.ToString() ?? "missing")+" throttle="+GameAccess.Get(a.Drive,"throttleInput")+" brake="+GameAccess.Get(a.Drive,"brakeInput")+" handbrake="+GameAccess.Get(a.Drive,"handbrakeInput")+" enabled="+((Behaviour)a.Drive).enabled+" groundedWheels="+car.GetComponentsInChildren<WheelCollider>().Count(w=>w.isGrounded));
                            }
                            yield return new WaitForEndOfFrame();
                        }
                        Check(stopped && minimum<.2f,"native service braking stops role "+driver+" initial="+startSpeed.ToString("F3")+" minimum="+minimum.ToString("F3")+" final="+a.Speed.ToString("F3")+" velocity="+body.velocity.ToString("F3"));
                        SetInput(0,0,1,false);
                        nativeDriver.BeginExit(host.Now); GameAccess.Call(car,"ExitCarPolice"); yield return new WaitForSeconds(1.5f); nativeDriver.Tick(host.Now,log);
                        // Each role is an independent controlled transport segment.
                        // Perform the server's disconnect cleanup before the next
                        // segment; retained input must not toggle its native engine.
                        host.Vehicles.RemoveActor(host.World.Registry,driver);
                        GameAccess.Set(car,"engine_running",false);
                        host.Poll(); client.Poll(); yield return new WaitForFixedUpdate();
                        a.RemoteEngine(false);
                        Check(!GameAccess.Bool(p,"is_in_vehicle") && p.GetComponent<CharacterController>().enabled,"original local controller restored after driving role "+driver);
                        Check(GameAccess.Get(p,"invScript")==inventory && camera.GetInstanceID()==cameraId,"original inventory/camera retained after driving role "+driver);
                        fixtureCar=null; fixtureNetwork=null;
                    }
                }
                // Exercise the actual Client presentation adapter and native physics
                // suppression on this genuine car, after Host EVP role checks.
                var clientPresentation=new VehicleNetworkAdapter(car,2,false);
                clientPresentation.State.Driver=1;
                fixtureCar=clientPresentation; fixtureDriver=0; fixtureNetwork=null;
                body.isKinematic=true; body.velocity=Vector3.zero; body.angularVelocity=Vector3.zero;
                clientPresentation.ApplyState(clientPresentation.State.Encode());
                nativeDriver.Capture(p,clientPresentation); p.transform.position=clientPresentation.DriverSeat.position;
                GameAccess.Call(car,"EnterVehicle"); car.GetType().GetMethod("RunEngine").Invoke(car,new object[] {true});
                yield return new WaitForSecondsRealtime(.7f);
                p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {true});
                var basePosition=body.position; var direction=car.transform.forward; var rotation=body.rotation;
                double began=Time.realtimeSinceStartup,until=began+2.5,nextSample=began; uint sequence=0;
                float last=-.01f; bool monotonic=true,stableCamera=true;
                while(Time.realtimeSinceStartup<until) {
                    double now=Time.realtimeSinceStartup;
                    if(now>=nextSample) {nextSample=now+.05; var point=basePosition+direction*(float)(now-began)*2; clientPresentation.ApplyMotion(new VehicleMotion {SessionId=1,Epoch=1,Actor=1,AuthorityGeneration=clientPresentation.State.AuthorityGeneration,Vehicle=new EntityRef(new NetworkEntityId(EntityScope.Static,8),1),Sequence=++sequence,Tick=(uint)(1000+(now-began)*1000),X=point.x,Y=point.y,Z=point.z,Qx=rotation.x,Qy=rotation.y,Qz=rotation.z,Qw=rotation.w},now);}
                    clientPresentation.Follow(now); nativeDriver.Tick(now,log);
                    float distance=Vector3.Dot(body.position-basePosition,direction); monotonic&=distance+.005f>=last; last=distance;
                    stableCamera&=Vector3.Distance(p.transform.position,clientPresentation.DriverSeat.position)<.05f;
                    yield return null;
                }
                Check(monotonic && last>3 && body.isKinematic && body.interpolation==RigidbodyInterpolation.None,"genuine Client presentation has one pose writer and no backward correction with native EVP suppressed");
                Check(stableCamera && camera.GetInstanceID()==cameraId,"Client driver follows the rendered native seat before camera attachment");
                nativeDriver.BeginExit(Time.realtimeSinceStartup); GameAccess.Call(car,"ExitCarPolice"); yield return new WaitForSecondsRealtime(1.5f); nativeDriver.Restore(actorOrigin,log); clientPresentation.Restore();
                log("[TS2MP] NATIVE EVP FIXTURE COMPLETE checks="+checks+"; local Host/Client EVP + native observer presentation + synthetic peer; human two-machine gate deferred");
            } finally {
                nativeDriver.Restore(actorOrigin,log);
                passenger.Exit(actorOrigin); fixtureCar=null; fixtureNetwork=null; fixtureInput=null; fixture=false; input.enabled=inputOn;
                GameAccess.Set(car,"engine_running",engineBefore); GameAccess.Set(car,"is_sitting_inside",seatedBefore);
                p.transform.SetParent(null,true); p.transform.SetPositionAndRotation(actorOrigin,actorRotation);
                body.isKinematic=true; body.position=origin; body.rotation=orientation; body.isKinematic=kinematic; if(!kinematic) { body.velocity=velocity; body.angularVelocity=angular; }
            }
        }
    }
}
