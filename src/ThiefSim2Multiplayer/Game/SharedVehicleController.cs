using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class SharedVehicleController : IDisposable
    {
        internal static SharedVehicleController Current;
        private readonly NetworkManager network;
        private readonly Action<string> log;
        internal Action<Component> OpenSleep;
        private readonly Harmony harmony = new Harmony(Plugin.Id+".shared-vehicle");
        private readonly PlayerLocator locator = new PlayerLocator();
        private readonly Dictionary<Component,VehicleNetworkAdapter> cars = new Dictionary<Component,VehicleNetworkAdapter>();
        private readonly LocalPassengerState passenger = new LocalPassengerState();
        private readonly LocalDriverState driver = new LocalDriverState();
        private VehicleNetworkAdapter localCar;
        private SeatRole localRole;
        private uint epoch;
        private bool prepared, hostRole, nativeApply;
        private double nextMotion, nextIntent;
        private double nextConfirmation;
        private ulong confirmationRevision;
        private int attachedFrame=-1;
        private Component attachedActor;
        private VehicleNetworkAdapter intentCar;
        private SeatRole intentRole;
        private SeatAction intentAction;
        private EntityRef intentDoor;
        private ulong intentId;
        private double intentUntil, intentAt;
        private bool retryIntent;
        private int nativeEntryFrame=-1;
        internal string Message { get; private set; } = "";
        private string prompt="";
        private bool fixture;
        private NetworkManager fixtureNetwork;
        private VehicleNetworkAdapter fixtureCar;
        private byte fixtureDriver;
        private DriverInput fixtureInput;
        private Component inputActor;
        private Func<string,float> readAxis;
        private Func<string,bool> readButton;
        private static readonly RaycastHit[] exitGroundHits=new RaycastHit[32];
        private static readonly Collider[] exitObstacles=new Collider[32];
        private bool Online => network.Session.State!=SessionState.Offline;
        private bool Cooperative => network.Session.State==SessionState.Connected && network.PeerConnected;
        internal bool NativeExitInProgress(Component player) => driver.Player==player && driver.Exiting;
        internal bool PassengerStorageControls(Component player) => Cooperative && localRole==SeatRole.Passenger && passenger.Player==player && passenger.StorageControlsReady;
        internal static bool StorageBoxAvailable(Component box)
        {
            var c=Current;
            if(c==null || !c.Cooperative) return box && box.CompareTag("CarBox");
            foreach(var a in c.cars.Values) if(a.Car && GameAccess.Get(a.Car,"car_box_script") as Component==box) {
                bool engine=c.network.Session.IsHost ? GameAccess.Bool(a.Car,"engine_running") : a.State.EngineRunning || a.State.Driver==c.network.Session.LocalPlayerId && GameAccess.Bool(a.Car,"engine_running");
                return a.Operational && a.StorageAvailable(engine);
            }
            return box && box.CompareTag("CarBox");
        }
        internal static void RefreshStorageTags()
        {
            var c=Current;
            if(c==null || !c.Cooperative) return;
            foreach(var a in c.cars.Values) if(a.Operational) a.RefreshStorageTag(StorageBoxAvailable(GameAccess.Get(a.Car,"car_box_script") as Component));
        }
        internal SharedVehicleController(NetworkManager n,Action<string> logger)
        {
            network=n; log=logger; Current=this; network.World.PrepareRegistry+=Prepare; network.ValidateSeat=Validate;
            network.SeatCompleted+=Completed;
            network.RemoteRemoved+=Disconnected;
            network.CaptureTravelOccupants=CaptureTravel;
            network.ApplyVehicleImpact=ReplayImpact;
            var car=Type.GetType("CarController, Assembly-CSharp",true);
            Patch(car,"EnterVehicle",nameof(EnterIntent)); Patch(car,"ExitVehicle",nameof(ExitIntent));
            harmony.Patch(AccessTools.Method(car,"Update"),prefix:new HarmonyMethod(typeof(SharedVehicleController),nameof(CarUpdate)),postfix:new HarmonyMethod(typeof(SharedVehicleController),nameof(CarFuelConsumed)));
            harmony.Patch(AccessTools.Method(car,"Update"),transpiler:new HarmonyMethod(typeof(SharedVehicleController),nameof(NativeVehicleUpdateReads)));
            harmony.Patch(AccessTools.Method(car,"OnCollisionEnter"),prefix:new HarmonyMethod(typeof(SharedVehicleController),nameof(CollisionObserved)),postfix:new HarmonyMethod(typeof(SharedVehicleController),nameof(CollisionCompleted)));
            Patch(Type.GetType("PlayerScript, Assembly-CSharp",true),"GetTazed",nameof(NativeImpactTaze));
            var evp=Type.GetType("EVP.VehicleController, Assembly-CSharp",true);
            Patch(evp,"FixedUpdate",nameof(PhysicsStep));
            Patch(evp,"OnCollisionEnter",nameof(PhysicsContact)); Patch(evp,"OnCollisionStay",nameof(PhysicsContact));
            var input=Type.GetType("EVP.VehicleStandardInput, Assembly-CSharp",true);
            Patch(input,"FixedUpdate",nameof(InputStep)); Patch(input,"Update",nameof(InputStep));
            harmony.Patch(AccessTools.Method(input,"FixedUpdate"),transpiler:new HarmonyMethod(typeof(SharedVehicleController),nameof(NativeDrivingReads)));
            var playerUpdate=AccessTools.Method(Type.GetType("PlayerScript, Assembly-CSharp",true),"Update");
            harmony.Patch(playerUpdate,prefix:new HarmonyMethod(typeof(SharedVehicleController),nameof(PlayerUpdate)),finalizer:new HarmonyMethod(typeof(SharedVehicleController),nameof(PlayerUpdateFinalizer)));
        }
        private void Patch(Type type,string method,string prefix) => harmony.Patch(AccessTools.Method(type,method),prefix:new HarmonyMethod(typeof(SharedVehicleController),prefix));
        internal void Prepare() => Prepare(false);
        private void Prepare(bool destinationReady)
        {
            using var phase=network.FrameProfile.Measure(Core.Diagnostics.RuntimePhase.VehicleBinding);
            if (!Online || network.World.TestEntitiesEnabled || !destinationReady && network.Scenes.Active && !network.Scenes.LocalLoaded) return;
            var registry=network.World.Registry;
            bool bindingsCurrent=prepared && epoch==registry.SceneEpoch && hostRole==registry.IsHost;
            if(bindingsCurrent) foreach(var a in cars.Values) if(!a.NativeDestroyed && registry.GetBinding(a.Entity)!=a) { bindingsCurrent=false; break; }
            if (bindingsCurrent) {
                if (registry.IsHost && network.World.State==WorldSyncState.Connected) foreach (var a in cars.Values) {
                    if(!a.Operational) continue;
                    a.SampleMotion(); if (a.State.Valid() && !StableHash.Equal(registry.Lookup(a.Entity).State.Payload,a.CaptureState())) registry.CommitState(a.Entity);
                }
                return;
            }
            SceneInvalidating(); epoch=registry.SceneEpoch; hostRole=registry.IsHost;
            var actor=locator.Find(); var gm=actor ? GameAccess.Get(actor,"GM") : null; var manager=gm!=null ? GameAccess.Get(gm,"VM") : null;
            var first=manager!=null ? GameAccess.Get(manager,"firstCar") as Component : null; var van=manager!=null ? GameAccess.Get(manager,"van") as Component : null;
            var candidates=new List<KeyValuePair<StaticEntityDescriptor,Component>>();
            // The only supported cars are these two verified native manager slots.
            // Avoid walking every map hierarchy merely to find those same objects.
            foreach (Component car in new[] {first,van}) {
                    // Authored player cars only: stolen cars and unverified unlockables have no safe passenger contract.
                    if (!car || !car.gameObject.scene.isLoaded || !car.gameObject.activeInHierarchy || !GameAccess.Bool(car,"is_player_car") || GameAccess.Get(car,"PS") as Component!=actor) continue;
                    candidates.Add(new KeyValuePair<StaticEntityDescriptor,Component>(UnityNetworkEntityAdapter.Describe(car,network.BuildFingerprintForDiagnostics,"coop-vehicle-v3-rear-center",0,VehiclePresentation.SemanticSlot(car)),car));
            }
            foreach (var pair in candidates.OrderBy(p=>p.Key.Canonical,StringComparer.Ordinal)) {
                VehicleNetworkAdapter a=null;
                try {
                    a=new VehicleNetworkAdapter(pair.Value,network.Session.LocalPlayerId,registry.IsHost);
                    if (network.Scenes.Target?.HasTravelOccupants == true && network.Scenes.Epoch == epoch) {
                        bool vanSlot=VehiclePresentation.SemanticSlot(pair.Value)=="VehiclesManager.van";
                        a.State.Driver=vanSlot ? network.Scenes.Target.VanDriver : network.Scenes.Target.FirstDriver;
                        a.State.Passenger=vanSlot ? network.Scenes.Target.VanPassenger : network.Scenes.Target.FirstPassenger;
                        if(!network.PeerConnected) { if(a.State.Driver==network.Session.RemotePlayerId) a.State.Driver=0; if(a.State.Passenger==network.Session.RemotePlayerId) a.State.Passenger=0; }
                    }
                    else if (network.Session.IsHost && GameAccess.Get(actor,"current_vehicle") as Component==pair.Value && NativeAtSeat(actor,a,SeatRole.Driver)) a.State.Driver=network.Session.LocalPlayerId;
                    a.Entity=registry.RegisterStatic(pair.Key,a,new EntityOwnership(OwnershipKind.System)); cars.Add(pair.Value,a);
                    log("[TS2MP] VEHICLE bound "+a.Entity+" driver="+a.DriverSeat.name+" passenger="+(a.PassengerSeat ? a.PassengerSeat.name : "unavailable"));
                } catch (ArgumentException e) { a?.Restore(); log("[TS2MP] VEHICLE unsupported: "+e.Message); }
            }
            prepared=true;
        }
        private NetworkSceneDescriptor CaptureTravel(NetworkSceneDescriptor target)
        {
            if(!Cooperative) return target;
            byte fd=0,fp=0,vd=0,vp=0;
            foreach(var a in cars.Values) {
                if(!a.Operational) continue;
                byte d=a.State.ExitingDriver ? (byte)0 : a.State.Driver, p=a.State.ExitingPassenger ? (byte)0 : a.State.Passenger;
                if(VehiclePresentation.SemanticSlot(a.Car)=="VehiclesManager.van") {vd=d; vp=p;} else {fd=d; fp=p;}
            }
            return new NetworkSceneDescriptor(target.Map,target.VehicleVariant,target.SecondVehicleAvailable,fd,fp,vd,vp);
        }
        internal bool RestoreDestination(Component actor)
        {
            if(network.Scenes.Target?.HasTravelOccupants != true) return true;
            Prepare(true); if(!prepared || epoch!=network.Scenes.Epoch) return false;
            Reconcile(actor);
            if(network.Scenes.State==SceneTransitionState.Failed) return false;
            var target=network.Scenes.Target; byte id=network.Session.LocalPlayerId;
            bool expected=target.FirstDriver==id || target.FirstPassenger==id || target.VanDriver==id || target.VanPassenger==id;
            return !expected || localCar!=null && NativeAtSeat(actor,localCar,localRole);
        }
        internal bool RestoringDestinationDriver(Component car) => nativeApply && localRole==SeatRole.Driver && localCar?.Car==car &&
            network.Scenes.Target?.HasTravelOccupants==true && !network.Scenes.LocalLoaded;
        internal VehicleOccupancyState Occupancy(byte actor)
        {
            foreach (var a in cars.Values) {
                if(!a.Operational) continue;
                if (a.State.Driver==actor && !a.State.ExitingDriver) return new VehicleOccupancyState { Seat=SeatRole.Driver, PresentationKey=a.PresentationKey };
                if (a.State.Passenger==actor && !a.State.ExitingPassenger) return new VehicleOccupancyState { Seat=SeatRole.Passenger, PresentationKey=a.PresentationKey };
            }
            return default(VehicleOccupancyState);
        }
        internal VehicleOccupancyState LocalOccupancy => localCar != null && NativeAtSeat(locator.Find(),localCar,localRole) ? new VehicleOccupancyState { Seat=localRole, PresentationKey=localCar.PresentationKey } : Occupancy(network.Session.LocalPlayerId);
        private bool Validate(byte actor,EntityRef entity,SeatRole seatRole,SeatAction action,EntityRef door)
        {
            var a=network.World.Registry.GetBinding(entity) as VehicleNetworkAdapter;
            if (a==null || !a.Operational) return false;
            if(VehicleDoorPolicy.RequiresDoorTarget(seatRole,action)) {
                if(WorldInteractionController.Current==null || !WorldInteractionController.Current.ValidateRearPassengerEntry(actor,entity,door)) return false;
            } else if(door.Id.IsValid) return false;
            Vector3 position;
            if (actor==network.Session.LocalPlayerId) {
                var p=locator.Find(); if (!p) return false;
                position=p.transform.position;
                if (action==SeatAction.Enter && (!Eligible(p) || GameAccess.Bool(p,"is_in_vehicle"))) return false;
                if (action==SeatAction.Confirm && (!GameAccess.Bool(p,"is_in_vehicle") || GameAccess.Get(p,"current_vehicle") as Component != a.Car)) return false;
                if (action==SeatAction.ExitConfirm) return !GameAccess.Bool(p,"is_in_vehicle") && p.GetComponent<CharacterController>().enabled;
            } else {
                if (!network.Telemetry.HasPose || network.Telemetry.ReceiveAge(network.Now)>.75) return false;
                var pose=network.Telemetry.Latest; position=new Vector3(pose.X,pose.Y,pose.Z);
                // Host uses the last authenticated actor pose; no request-supplied position is accepted.
                if (action==SeatAction.Enter && (pose.State.Vehicle.IsInVehicle || pose.State.SeatEligibility != SeatEligibility.Eligible)) return false;
                if (action==SeatAction.Confirm && (pose.State.Vehicle.PresentationKey!=a.PresentationKey || a.State.Pending(pose.State.Vehicle.Seat)!=actor)) return false;
                if (action==SeatAction.ExitConfirm) return !pose.State.Vehicle.IsInVehicle && pose.State.SeatEligibility==SeatEligibility.Eligible;
            }
            if (action==SeatAction.Exit) return a.State.PendingDriver==actor || a.State.PendingPassenger==actor || a.MotionSpeed<5 && SafeExit(a,a.State.Passenger==actor ? SeatRole.Passenger : SeatRole.Driver,out _);
            if (action==SeatAction.Confirm) {
                var seat=seatRole==SeatRole.Driver ? a.DriverSeat : a.PassengerSeat;
                return seat && Vector3.Distance(position,seat.position)<.4f && VehicleMultiplayerPolicy.MeetsSeatSpeedGate(seatRole,a.MotionSpeed);
            }
            return Vector3.Distance(position,a.Car.transform.position)<4 && VehicleMultiplayerPolicy.MeetsSeatSpeedGate(seatRole,a.MotionSpeed);
        }
        internal static bool Eligible(Component p) => p && GameAccess.Bool(p,"canInteract") && !GameAccess.Bool(p,"caughtUp") && !GameAccess.Bool(p,"closeUp") && !GameAccess.Bool(p,"isCarrying") && !GameAccess.Bool(p,"isDismantlingCar") && !GameAccess.Bool(p,"having_phone_convo") && Time.timeScale>0;
        private static bool NativeAtSeat(Component p,VehicleNetworkAdapter a,SeatRole role)
        { var seat=role==SeatRole.Driver ? a.DriverSeat : a.PassengerSeat; return p && seat && GameAccess.Bool(p,"is_in_vehicle") && Vector3.Distance(p.transform.position,seat.position)<.25f; }
        internal static bool SafeExit(VehicleNetworkAdapter a,SeatRole role,out Vector3 position)
        {
            position=default(Vector3); if(a==null || !a.Operational) return false;
            position=a.Exit.position;
            if (role==SeatRole.Passenger) {
                // Reflect authored driver exit across the vehicle centreline for the passenger side.
                var local=a.Car.transform.InverseTransformPoint(a.Exit.position); local.x=-local.x; position=a.Car.transform.TransformPoint(local);
            }
            RaycastHit hit=default(RaycastHit); bool found=false;
            int hits=Physics.RaycastNonAlloc(position+Vector3.up*2,Vector3.down,exitGroundHits,5,~0,QueryTriggerInteraction.Ignore);
            if(hits==exitGroundHits.Length) { Array.Clear(exitGroundHits,0,exitGroundHits.Length); return false; }
            for(int i=0;i<hits;i++) {
                var candidate=exitGroundHits[i]; var collider=candidate.collider;
                if(!collider || collider.transform.IsChildOf(a.Car.transform)) continue;
                if(!found || candidate.distance<hit.distance) { hit=candidate; found=true; }
            }
            Array.Clear(exitGroundHits,0,exitGroundHits.Length);
            if(!found || hit.normal.y<.5f || hit.collider.GetComponentInParent<CharacterController>()) return false;
            var actor=new PlayerLocator().Find(); var cc=actor ? actor.GetComponent<CharacterController>() : null;
            float height=cc ? cc.height : 1.6f, centre=cc ? cc.center.y : 0, radius=cc ? cc.radius : .25f;
            position=hit.point+Vector3.up*(height*.5f-centre+.1f);
            // Native exit opens the car's own door. Its current collider must not
            // veto a verified authored exit; other world/actor obstacles still do.
            int count=Physics.OverlapCapsuleNonAlloc(hit.point+Vector3.up*(radius+.1f),hit.point+Vector3.up*(height-radius+.1f),radius,exitObstacles,~0,QueryTriggerInteraction.Ignore);
            bool clear=count<exitObstacles.Length;
            for(int i=0;i<count && clear;i++) if(exitObstacles[i] && !exitObstacles[i].transform.IsChildOf(a.Car.transform)) clear=false;
            Array.Clear(exitObstacles,0,exitObstacles.Length); return clear;
        }
        private void Completed(ulong id,SeatResult result)
        {
            if(id==intentId && intentCar!=null) {
                if(result==SeatResult.StaleRevision && network.Now<intentUntil) { retryIntent=true; intentAt=network.Now+.25; return; }
                ClearIntent();
            }
            if (result>SeatResult.Duplicate && result!=SeatResult.Exiting) { Message="Seat request: "+result; log("[TS2MP] VEHICLE request="+id+" result="+result); confirmationRevision=0; }
        }
        private void ClearIntent() { intentCar=null; intentId=0; intentDoor=default(EntityRef); retryIntent=false; }
        private bool Intent(VehicleNetworkAdapter a,SeatRole role,SeatAction action,EntityRef door=default(EntityRef))
        {
            ClearIntent();
            if(!network.Session.IsHost) { intentCar=a; intentRole=role; intentAction=action; intentDoor=door; intentUntil=network.Now+2; }
            var id=network.RequestSeat(a.Entity,role,action,door);
            if(intentCar!=null) { intentId=id; if(id==0) ClearIntent(); }
            return id!=0;
        }
        internal void RequestExit(VehicleNetworkAdapter a,SeatRole role) => Intent(a,role,SeatAction.Exit);
        internal void RequestEnter(VehicleNetworkAdapter a,SeatRole role,EntityRef door=default(EntityRef)) => Request(a,role,door);
        private void RetryIntent(Component p)
        {
            if(intentCar==null) return;
            if(network.Now>=intentUntil || !network.GameplayReady || !intentCar.IsAlive || network.World.Registry.GetBinding(intentCar.Entity)!=intentCar || intentAction==SeatAction.Enter && !Eligible(p)) { ClearIntent(); return; }
            if(!retryIntent || network.Now<intentAt) return;
            retryIntent=false; intentId=network.RequestSeat(intentCar.Entity,intentRole,intentAction,intentDoor);
            if(intentId==0) ClearIntent();
        }
        private static bool EnterIntent(Component __instance)
        {
            var c=Current; if (c==null || VehicleMultiplayerPolicy.PassThroughNativeEntry(c.Cooperative,c.nativeApply)) return true;
            VehicleNetworkAdapter a; if (!c.cars.TryGetValue(__instance,out a)) { c.Message="This vehicle has no verified cooperative seat binding."; return false; }
            c.nativeEntryFrame=Time.frameCount;
            var p=c.locator.Find();
            c.Request(a,VehicleMultiplayerPolicy.NativeEntryRole);
            if(c.network.Session.IsHost && a.State.PendingDriver==c.network.Session.LocalPlayerId) {
                try {
                    c.driver.Capture(p,a);
                    c.localCar=a; c.localRole=SeatRole.Driver; c.attachedActor=p; c.attachedFrame=Time.frameCount; c.confirmationRevision=0;
                    return true;
                } catch(Exception e) {
                    c.log("[TS2MP] VEHICLE Host native driver entry preparation failed: "+e.Message);
                    c.network.RequestSeat(a.Entity,SeatRole.Driver,SeatAction.Exit);
                }
            }
            return false;
        }
        private static bool ExitIntent(Component __instance)
        {
            var c=Current; if (c==null || !c.Cooperative || c.nativeApply) return true;
            VehicleNetworkAdapter a; if (c.cars.TryGetValue(__instance,out a)) c.RequestExit(a,c.localCar==a ? c.localRole : SeatRole.Driver);
            return false;
        }
        internal static bool LocalDriverSimulating()
        {
            var c=Current; if(c==null) return false;
            foreach(var a in c.cars.Values) if(a.State.Driver==c.network.Session.LocalPlayerId && a.Simulates) return true;
            return false;
        }
        private static void CarFuelConsumed(Component __instance,float __state)
        {
            if(float.IsNaN(__state)) return; var c=Current;
            var a=c.fixtureCar?.Car==__instance ? c.fixtureCar : c.cars[__instance]; a.ObserveFuel(__state);
        }
        private static bool CarUpdate(Component __instance,out float __state)
        {
            __state=float.NaN;
            var c=Current;
            if(c?.fixtureCar!=null && __instance==c.fixtureCar.Car) {if(c.fixtureDriver!=0) {c.fixtureCar.SyncAuthority(); __state=c.fixtureCar.NativeFuel;} return c.fixtureDriver!=0;}
            if (c==null || !c.Cooperative) return true;
            VehicleNetworkAdapter a; if (!c.cars.TryGetValue(__instance,out a)) return true;
            if(a.State.Driver==c.network.Session.LocalPlayerId) {a.SyncAuthority(); __state=a.NativeFuel;}
            bool idle=a.State.Driver==0 && a.State.PendingDriver==0 && a.State.Passenger==0 && a.State.PendingPassenger==0 && !GameAccess.Bool(a.Car,"is_sitting_inside") && !GameAccess.Bool(a.Car,"engine_running");
            // Let the original idle lifecycle refresh the entry trigger/door/lootbox.
            // Remote Host driving needs the native fuel/gauges/audio/repair lifecycle.
            // Its local input and actor writes are filtered by NativeVehicleUpdateReads.
            return c.network.GameplayReady && a.Operational && (idle || a.State.Driver==c.network.Session.LocalPlayerId || a.State.PendingDriver==c.network.Session.LocalPlayerId || GameAccess.Bool(a.Car,"gettingOut") || a.State.Driver!=0);
        }
        private static bool InputStep(Component __instance)
        { var c=Current; if(nativeInputActive) return true; if(c?.fixtureCar!=null && __instance.gameObject==c.fixtureCar.Car.gameObject) return false; if (c==null || !c.Cooperative) return true; var car=__instance.GetComponent(Type.GetType("CarController, Assembly-CSharp",true)); VehicleNetworkAdapter a; return !car || !c.cars.TryGetValue(car,out a) || c.network.GameplayReady && a.Simulates && a.State.Driver==c.network.Session.LocalPlayerId; }
        private static bool PlayerUpdate(Component __instance,out bool __state)
        {
            __state=false; var c=Current;
            if(c==null) return true;
            if(c.passenger.Active && c.passenger.Player==__instance) return false;
            if(c.TryPassengerDoorInput(__instance)) { GameAccess.Set(__instance,"canInteract",false); __state=true; }
            return true;
        }
        private static Exception PlayerUpdateFinalizer(Component __instance,bool __state,Exception __exception)
        {
            if(__state && __instance) GameAccess.Set(__instance,"canInteract",true);
            return __exception;
        }
        private static bool PhysicsContact(Component __instance)
        {
            var c=Current; if(c==null || !c.Cooperative) return true;
            var car=__instance.GetComponent(Type.GetType("CarController, Assembly-CSharp",true));
            return !car || !c.cars.TryGetValue(car,out var a) || a.Simulates;
        }
        private static bool PhysicsStep(Component __instance)
        {
            var c=Current;
            if(c?.fixtureCar!=null && c.fixtureCar.Drive==__instance) { if(c.fixtureDriver!=0) ApplyDriving(c.fixtureCar,c.fixtureInput,true); return NativeSimulation(c.fixtureCar,true); }
            if (c==null || !c.Cooperative) return true;
            var car=__instance.GetComponent(Type.GetType("CarController, Assembly-CSharp",true));
            VehicleNetworkAdapter a; if (!car || !c.cars.TryGetValue(car,out a)) return true;
            if(!a.Operational) return false;
            return NativeSimulation(a,c.network.GameplayReady);
        }
        private static bool NativeSimulation(VehicleNetworkAdapter a,bool ready)
        {
            a.SyncAuthority();
            if(!a.Simulates) return ClientPhysics(a);
            // Either confirmed local driver executes unmodified native EVP. Input
            // never waits for a packet. Readiness suspension retains the old neutral gate.
            if(!ready) ApplyDriving(a,null,true);
            return true;
        }
        private static bool ClientPhysics(VehicleNetworkAdapter a) { a.Body.isKinematic=true; a.Body.interpolation=RigidbodyInterpolation.None; return false; }
        private static void ApplyDriving(VehicleNetworkAdapter a,DriverInput q,bool localDriver)
        {
            if(!localDriver && a.State.Driver==0 && !a.RemoteControlled) return;
            bool run=q!=null && q.EngineRunning && (localDriver ? GameAccess.Bool(a.Car,"engine_running") : true);
            // A missing input packet neutralizes driving, not the last verified
            // engine state. Keep storage locked until an engine-off observation or exit.
            bool engine=a.State.Driver!=0 && (q!=null ? q.EngineRunning : GameAccess.Bool(a.Car,"engine_running"));
            if(!localDriver) a.RemoteEngine(engine);
            // Remote driving never runs RunCar on the Host's passenger controller. EVP still needs a dynamic body.
            if (!localDriver) {
                // Remote entry never runs the native player coroutine. Its mirrored
                // engine flag must also clear when the driver exits or disconnects,
                // otherwise CarUpdate never resumes the original idle interactions.
                a.RemoteDriving(engine);
            }
            // Execute the real native input method rather than reconstructing its
            // throttle/brake model. Only its input source and single-player gate change.
            try { nativeInputActive=true; nativeInput=run ? q : null; a.ReadNativeInput(); }
            finally { nativeInputActive=false; nativeInput=null; }
        }
        private void BindInput(Component p) {
            if(inputActor==p && readAxis!=null) return;
            var input=GameAccess.Get(p,"rewiredPlayer"); var type=input.GetType();
            readAxis=(Func<string,float>)Delegate.CreateDelegate(typeof(Func<string,float>),input,type.GetMethod("GetAxis",new[] {typeof(string)}));
            readButton=(Func<string,bool>)Delegate.CreateDelegate(typeof(Func<string,bool>),input,type.GetMethod("GetButtonDown",new[] {typeof(string)})); inputActor=p;
        }
        private static float Axis(Component p,string action) { Current.BindInput(p); return Current.readAxis(action); }
        private static bool Button(Component p,string action) { Current.BindInput(p); return Current.readButton(action); }
        private void Request(VehicleNetworkAdapter a,SeatRole role)
        {
            Request(a,role,default(EntityRef));
        }
        private bool Request(VehicleNetworkAdapter a,SeatRole role,EntityRef door)
        {
            if (role==SeatRole.None) { Message="Both vehicle seats are occupied."; return false; }
            if (network.Now<nextIntent || !network.GameplayReady) return false;
            nextIntent=network.Now+.4;
            return Intent(a,role,SeatAction.Enter,door);
        }
        private bool NativeDoorInteractionGates(Component p)
        {
            if(!p || Time.timeScale<=0 || !GameAccess.Bool(p,"canInteract") || GameAccess.Bool(p,"attacking") ||
                GameAccess.Bool(p,"isCarrying") || GameAccess.Bool(p,"closeUp") || GameAccess.Bool(p,"hideHands") ||
                GameAccess.Bool(p,"callIncoming") || GameAccess.Bool(p,"opening_slowly") || GameAccess.Bool(p,"pickingUp") || GameAccess.Bool(p,"isHiding")) return false;
            var motor=GameAccess.Get(p,"PMB") as Behaviour; var camera=GameAccess.Get(p,"PCB") as Behaviour;
            var radial=GameAccess.Get(p,"radial_menu"); var pickpocket=GameAccess.Get(p,"pickpocket_Manager");
            return motor && motor.enabled && !GameAccess.Bool(motor,"isRunning") && camera && camera.enabled &&
                !NativeBooleanMember.Read(radial,"RadialMenuActive") && !GameAccess.Bool(pickpocket,"isOn");
        }
        private bool TryRearPassengerDoorTarget(Component p,out VehicleNetworkAdapter vehicle,out Component door,out EntityRef doorRef)
        {
            vehicle=null; door=null; doorRef=default(EntityRef);
            if(!NativeDoorInteractionGates(p)) return false;
            var camera=GameAccess.Get(p,"cam") as Transform; if(!camera) return false;
            var mask=(LayerMask)GameAccess.Get(p,"mask"); RaycastHit hit;
            if(!Physics.Raycast(camera.position,camera.TransformDirection(Vector3.forward),out hit,2.25f,mask.value) ||
                !hit.transform || hit.transform.tag!="Door") return false;
            var type=Type.GetType("Door, Assembly-CSharp",true); door=hit.transform.gameObject.GetComponent(type) as Component;
            if(!door || !GameAccess.Bool(door,"canUse") || GameAccess.Bool(door,"brokeHinge")) { door=null; return false; }
            foreach(var candidate in cars.Values) {
                if(!candidate.Operational || !candidate.IsRearPassengerDoor(door)) continue;
                if(WorldInteractionController.Current!=null && WorldInteractionController.Current.TryGetRearPassengerDoor(door,candidate,out doorRef)) { vehicle=candidate; return true; }
            }
            door=null; return false;
        }
        internal bool NativeRearDoorHit(Component p,Component door)
        {
            if(!door || !NativeDoorInteractionGates(p)) return false;
            var camera=GameAccess.Get(p,"cam") as Transform; if(!camera) return false;
            var mask=(LayerMask)GameAccess.Get(p,"mask"); RaycastHit hit;
            var nativeDoorType=Type.GetType("Door, Assembly-CSharp",true);
            return Physics.Raycast(camera.position,camera.TransformDirection(Vector3.forward),out hit,2.25f,mask.value) &&
                hit.transform==door.transform && hit.transform.tag=="Door" && hit.transform.gameObject.GetComponent(nativeDoorType) as Component==door &&
                GameAccess.Bool(door,"canUse") && !GameAccess.Bool(door,"brokeHinge");
        }
        internal bool IsLocalDriverAutoClose(VehicleNetworkAdapter vehicle,Component door)
        {
            if(!Cooperative || vehicle==null || localCar!=vehicle || localRole!=SeatRole.Driver || !vehicle.IsNativeFrontEntryDoor(door)) return false;
            var player=locator.Find();
            return player && GameAccess.Get(player,"current_vehicle") as Component==vehicle.Car && GameAccess.Bool(player,"is_in_vehicle") &&
                (vehicle.State.Driver==network.Session.LocalPlayerId || vehicle.State.PendingDriver==network.Session.LocalPlayerId);
        }
        private bool TryPassengerDoorInput(Component p)
        {
            if(!Cooperative || !network.GameplayReady || localCar!=null || localRole!=SeatRole.None ||
                GameAccess.Bool(p,"is_in_vehicle") || GameAccess.Get(p,"current_vehicle") as Component || !Eligible(p) || !Button(p,"interact")) return false;
            if(!TryRearPassengerDoorTarget(p,out var vehicle,out var door,out var doorRef)) return false;
            if(!GameAccess.Bool(door,"isOpen") || GameAccess.Bool(door,"opening") || GameAccess.Bool(door,"openingSlowly") || GameAccess.Bool(door,"load_slow")) return false;
            if(vehicle.State.Passenger!=0 || vehicle.State.PendingPassenger!=0 || vehicle.State.ExitingPassenger) return false;
            bool sameRequestPending=intentCar==vehicle && intentRole==SeatRole.Passenger && intentAction==SeatAction.Enter && intentDoor.Equals(doorRef) && intentId!=0;
            if(sameRequestPending) return VehicleMultiplayerPolicy.ShouldConsumePassengerEntryInput(false,true);
            // The native open action completed on an earlier E press. Consume only this
            // rear-door entry press so it cannot also start the native door-close gesture.
            // If no seat request can be queued, let PlayerScript handle the input.
            return VehicleMultiplayerPolicy.ShouldConsumePassengerEntryInput(Request(vehicle,SeatRole.Passenger,doorRef),false);
        }
        internal void Tick()
        {
            prompt="";
            if (fixture) { if(passenger.Active) passenger.Tick(0,0); return; }
            // Render remote vehicles before reconciling native local actor state.
            if(prepared && Cooperative)
                foreach(var a in cars.Values) if(a.Operational) a.Follow(network.Now);
            bool driverLost=driver.Tick(network.Now,log);
            if (!Online) { if (prepared) SceneInvalidating(); return; }
            if (!prepared) return;
            RefreshStorageTags();
            var p=locator.Find();
            if (driverLost || !p || localCar!=null && (attachedActor!=p || !localCar.Operational) || passenger.Player && !passenger.Active) {
                if(!network.Session.IsHost && localCar!=null && network.GameplayReady) network.RequestSeat(localCar.Entity,localRole,SeatAction.Exit);
                if(network.Session.IsHost) {
                    network.Vehicles.RemoveActor(network.World.Registry,network.Session.LocalPlayerId);
                }
                // Recover this occupant without failing the entire world/session.
                ExitLocal(false); return;
            }
            foreach(var a in cars.Values) {
                if(a.NativeDestroyed && network.Session.IsHost && network.World.Registry.Lookup(a.Entity)?.State.Tombstone==false) {
                    network.World.Registry.Despawn(a.Entity); log("[TS2MP] VEHICLE verified native destruction entity="+a.Entity);
                }
                if(!a.Operational && network.Session.IsHost) {
                    if(a.State.Driver!=0) network.Vehicles.RemoveActor(network.World.Registry,a.State.Driver);
                    if(a.State.Passenger!=0) network.Vehicles.RemoveActor(network.World.Registry,a.State.Passenger);
                    if(a.State.PendingDriver!=0) network.Vehicles.RemoveActor(network.World.Registry,a.State.PendingDriver);
                    if(a.State.PendingPassenger!=0) network.Vehicles.RemoveActor(network.World.Registry,a.State.PendingPassenger);
                }
            }
            if (!Cooperative && network.Session.IsHost) {
                foreach (var a in cars.Values) {
                    byte nativeDriver=GameAccess.Get(p,"current_vehicle") as Component==a.Car && NativeAtSeat(p,a,SeatRole.Driver) ? network.Session.LocalPlayerId : (byte)0;
                    if (a.State.Driver!=nativeDriver) { a.State.Driver=nativeDriver; a.State.PendingDriver=a.State.PendingPassenger=0; a.State.Passenger=0; a.State.OccupancyRevision++; a.SampleMotion(); network.World.Registry.CommitState(a.Entity); }
                }
                return;
            }
            if(network.Scenes.State==SceneTransitionState.Failed) { ExitLocal(false); return; }
            Reconcile(p);
            if (!network.GameplayReady || !network.ScenesCompatible) { ClearIntent(); if(passenger.Active) passenger.Tick(0,0); return; }
            RetryIntent(p);
            if (localCar!=null) {
                if (passenger.Active) {
                    if (passenger.InventoryControlsReady && (bool)p.GetType().GetMethod("TryOpenInventory").Invoke(p,null)) {
                        var inv=GameAccess.Get(p,"invScript"); GameAccess.Call(p,GameAccess.Bool(inv,"isEnabled") || GameAccess.Bool(inv,"isStorage") || GameAccess.Bool(inv,"isShop") ? "DisableInventory" : "EnableInventory");
                    }
                    var look=GameAccess.Get(p,"PCB"); var gm=GameAccess.Get(p,"GM");
                    float sensitivity=(float)GameAccess.Get(look,"mouseSensitivity")/(GameAccess.Bool(gm,"is_Xbox_controller") ? 10f : 150f);
                    float invert=(int)GameAccess.Get(look,"invertPref")==0 ? 1 : -1;
                    bool canLook=Time.timeScale>0 && GameAccess.Bool(p,"cursorLock");
                    passenger.Tick(canLook ? Axis(p,"look_left_right")*sensitivity : 0,canLook ? Axis(p,"look_up_down")*sensitivity*invert : 0);
                    bool storage=CooperativeEconomyController.PassengerStorage(p,false);
                    prompt=storage ? "Storage — interact to open" : "Passenger — interact to exit"+(network.Session.IsHost ? "; T to return; Y to sleep" : "");
                    // Native EnterCar can reserve during Update; that same button pulse must
                    // not immediately exit the passenger attached by this LateUpdate.
                    if (Time.frameCount>attachedFrame && Time.timeScale>0) {
                        if(Button(p,"interact")) {if(storage) CooperativeEconomyController.PassengerStorage(p,true); else RequestExit(localCar,SeatRole.Passenger);}
                        else if(Button(p,"b_button")) RequestExit(localCar,SeatRole.Passenger);
                    }
                    if(!Plugin.NativeInputFixtureActive && network.Session.IsHost && Input.GetKeyDown(KeyCode.Y) && localCar.MotionSpeed<1) OpenSleep?.Invoke(p);
                    if (!Plugin.NativeInputFixtureActive && network.Session.IsHost && Input.GetKeyDown(KeyCode.T) && network.Scenes.Target!=null && network.Scenes.Target.Map!="PlayerHideout") {
                        string reason; if (network.CanGlobalAction(true,out reason) && localCar.MotionSpeed<1) GameAccess.Call(GameAccess.Get(p,"GM"),"SelectHideout");
                        else Message=reason.Length>0 ? reason : "Stop the vehicle before returning.";
                        return;
                    }
                }
            } else if (Cooperative && TryRearPassengerDoorTarget(p,out var targetVehicle,out var targetDoor,out _) &&
                GameAccess.Bool(targetDoor,"isOpen") && !GameAccess.Bool(targetDoor,"opening") && !GameAccess.Bool(targetDoor,"openingSlowly") &&
                !GameAccess.Bool(targetDoor,"load_slow") && targetVehicle.State.Passenger==0 && targetVehicle.State.PendingPassenger==0 && !targetVehicle.State.ExitingPassenger) {
                prompt="Passenger seat — interact to enter";
            }
            if (network.Now>=nextMotion) {
                nextMotion=network.Now+.05;
                foreach (var a in cars.Values) {
                    if (!a.Operational || !a.Simulates || network.World.Registry.GetBinding(a.Entity)!=a) continue;
                    var motion=a.Motion(); if(a.MotionChanged(motion,network.Now)) network.SendVehicleMotion(motion);
                }
            }
        }
        private void Reconcile(Component p)
        {
            VehicleNetworkAdapter wanted=null; SeatRole role=SeatRole.None; bool pending=false;
            foreach (var a in cars.Values) {
                if(!a.Operational) continue;
                byte actor=network.Session.LocalPlayerId;
                if (a.State.Driver==actor && !a.State.ExitingDriver || a.State.PendingDriver==actor) { wanted=a; role=SeatRole.Driver; pending=a.State.PendingDriver==actor; break; }
                if (a.State.Passenger==actor && !a.State.ExitingPassenger || a.State.PendingPassenger==actor) { wanted=a; role=SeatRole.Passenger; pending=a.State.PendingPassenger==actor; break; }
            }
            if (localCar!=wanted || localRole!=role) {
                ExitLocal(true); localCar=wanted; localRole=role; confirmationRevision=0; attachedFrame=Time.frameCount; attachedActor=p;
                if (wanted!=null) {
                    try {
                        nativeApply=true;
                        if (VehicleMultiplayerPolicy.UsesPassengerPresentation(role)) passenger.Enter(p,wanted.Car,wanted.PassengerSeat);
                        else {
                            driver.Capture(p,wanted);
                            if (!GameAccess.Bool(p,"is_in_vehicle")) {
                                // Travel restores an already confirmed driver. Native
                                // IsStartingCar places the actor at its authored seat;
                                // ordinary EnterVehicle needs Update's seat lerp, which
                                // is intentionally gated until WorldReady during loading.
                                bool travel=network.Scenes.Target?.HasTravelOccupants==true && !network.Scenes.LocalLoaded && !pending;
                                GameAccess.Call(wanted.Car,travel ? "IsStartingCar" : "EnterVehicle");
                            }
                        }
                        log("[TS2MP] VEHICLE local applied role="+role+" playerId="+p.GetInstanceID()+" vehicle="+wanted.Entity);
                    } catch (Exception e) {
                        log("[TS2MP] VEHICLE local apply failed: "+e.Message); passenger.Dispose(); driver.Restore(null,log);
                        if(network.Scenes.Active && !network.Scenes.LocalLoaded) network.FailSceneTransition("Destination "+role+" restoration failed: "+e.GetType().Name);
                        else network.RequestSeat(wanted.Entity,role,SeatAction.Exit);
                        localCar=null; localRole=SeatRole.None; attachedActor=null; return;
                    }
                    finally { nativeApply=false; }
                }
            }
            if (wanted!=null && pending && network.Now>=nextConfirmation && NativeAtSeat(p,wanted,role)) {
                // Send the attached actor pose before the confirmation. Host additionally validates proximity.
                confirmationRevision=wanted.State.OccupancyRevision;
                nextConfirmation=network.Now+.25;
                network.RequestSeat(wanted.Entity,role,SeatAction.Confirm);
            }
            if(wanted==null && !GameAccess.Bool(p,"is_in_vehicle") && p.GetComponent<CharacterController>().enabled && network.Now>=nextConfirmation) {
                foreach(var a in cars.Values) {
                    byte actor=network.Session.LocalPlayerId;
                    if(a.State.Driver==actor && a.State.ExitingDriver) { nextConfirmation=network.Now+.25; network.RequestSeat(a.Entity,SeatRole.Driver,SeatAction.ExitConfirm); }
                    if(a.State.Passenger==actor && a.State.ExitingPassenger) { nextConfirmation=network.Now+.25; network.RequestSeat(a.Entity,SeatRole.Passenger,SeatAction.ExitConfirm); }
                }
            }
        }
        private void ExitLocal(bool normal)
        {
            if (passenger.Player) {
                Vector3 position; passenger.Exit(normal && localCar!=null && SafeExit(localCar,SeatRole.Passenger,out position) ? (Vector3?)position : null);
            } else if (localCar!=null && localRole==SeatRole.Driver && localCar.Car && normal && driver.Active && GameAccess.Bool(driver.Player,"is_in_vehicle")) {
                // The Host already verified a clear capsule/ground exit. Use the native routine with its door retry bypass.
                try { nativeApply=true; Vector3 point; driver.BeginExit(network.Now,SafeExit(localCar,SeatRole.Driver,out point) ? (Vector3?)point : null); GameAccess.Call(localCar.Car,"ExitCarPolice"); }
                catch(Exception e) { log("[TS2MP] VEHICLE native exit failed; restoring original actor: "+e.Message); driver.Restore(null,log); }
                finally { nativeApply=false; }
            }
            if(!normal || driver.Active && (localCar==null || !localCar.Car)) driver.Restore(null,log);
            localCar=null; localRole=SeatRole.None; confirmationRevision=0; attachedActor=null;
        }
        internal void SceneInvalidating()
        {
            // Restore before releasing references; never carry a seat Transform into the next epoch.
            ExitLocal(false); foreach (var a in cars.Values) a.Restore(); cars.Clear(); prepared=false; prompt="";
            inputActor=null; readAxis=null; readButton=null;
            ClearIntent();
        }
        private void Disconnected()
        {
            ClearIntent();
            if (passenger.Player) { ExitLocal(true); network.Vehicles.RemoveActor(network.World.Registry,network.Session.LocalPlayerId); }
            foreach (var a in cars.Values) a.Restore();
            if (!network.Session.IsHost) { ExitLocal(true); cars.Clear(); prepared=false; }
        }
        internal void Draw() { if(!Online) return; if (prompt.Length>0) GUI.Label(new Rect(Screen.width/2-160,Screen.height*.7f,400,28),prompt); if (Message.Length>0) GUI.Label(new Rect(20,Screen.height-55,600,28),Message); }
        internal IEnumerator NativePassengerFixture(Action<Camera> capture=null)
        {
            if (Online || fixture) throw new InvalidOperationException("Native fixture requires offline disposable session.");
            var p=locator.Find(); if (!p || !Eligible(p) || GameAccess.Bool(p,"is_in_vehicle")) throw new InvalidOperationException("Native on-foot actor unavailable.");
            var car=CurrentFixtureCar(p);
            if (!car) throw new InvalidOperationException("Authored passenger car unavailable.");
            var a=new VehicleNetworkAdapter(car); if (!a.PassengerSeat) throw new InvalidOperationException("Passenger anchor contract failed.");
            fixture=true; int checks=0;
            var origin=p.transform.position; var actorRotation=p.transform.rotation; var carPosition=car.transform.position; var carRotation=car.transform.rotation;
            var oldVelocity=a.Body.velocity; var oldAngular=a.Body.angularVelocity;
            var controller=p.GetComponent<CharacterController>(); bool controllerEnabled=controller.enabled;
            var camera=GameAccess.Get(p,"MainCam") as Camera; var inventory=GameAccess.Get(p,"invScript");
            float oldClip=camera.nearClipPlane;
            var items=(Array)GameAccess.Get(p,"item_IDs2"); var amounts=items.Cast<object>().Select(i=>GameAccess.Get(i,"item_amount")).ToArray();
            var cameras=p.GetComponentsInChildren<Camera>(true).Select(c=>c.GetInstanceID()).ToArray();
            var listeners=p.GetComponentsInChildren<AudioListener>(true).Select(c=>c.GetInstanceID()).ToArray();
            var motor=(Behaviour)GameAccess.Get(p,"PMB"); var look=(Behaviour)GameAccess.Get(p,"PCB"); bool motorOn=motor.enabled, lookOn=look.enabled;
            var drive=(Behaviour)a.Drive; bool driveOn=drive.enabled;
            var input=(Behaviour)car.GetComponent(Type.GetType("EVP.VehicleStandardInput, Assembly-CSharp",true)); bool inputOn=input.enabled;
            void Check(bool ok,string label) { if (!ok) throw new InvalidOperationException("Native passenger fixture FAIL: "+label); checks++; log("[TS2MP] NATIVE PASSENGER FIXTURE PASS: "+label); }
            try {
                passenger.Enter(p,car,a.PassengerSeat);
                Check(passenger.Player==p,"original PlayerScript retained"); Check(!controller.enabled && !motor.enabled,"native on-foot controller suspended");
                Check(!GameAccess.Bool(p,"can_drive_car"),"passenger cannot drive"); Check(GameAccess.Get(p,"invScript")==inventory,"inventory reference retained");
                Check(p.GetComponentsInChildren<Collider>(true).All(c=>!c.enabled),"passenger collisions suspended");
                Check(cameras.SequenceEqual(p.GetComponentsInChildren<Camera>(true).Select(c=>c.GetInstanceID())),"original cameras retained");
                Check(listeners.SequenceEqual(p.GetComponentsInChildren<AudioListener>(true).Select(c=>c.GetInstanceID())),"original listeners retained");
                // Controlled native object attachment test. This motion is a fixture, not driver gameplay acceptance.
                drive.enabled=false; input.enabled=false; a.Body.isKinematic=true;
                for (int frame=0;frame<60;frame++) {
                    car.transform.position=carPosition+car.transform.forward*(frame*.02f); car.transform.rotation=carRotation*Quaternion.Euler(0,frame*.2f,0);
                    passenger.Tick(.25f,.1f);
                    if (Vector3.Distance(p.transform.position,a.PassengerSeat.position)>.001f) throw new InvalidOperationException("Passenger detached during motion.");
                    yield return new WaitForEndOfFrame();
                }
                Check(Vector3.Distance(car.transform.position,carPosition)>1,"native car fixture moved over one metre");
                Check(Vector3.Distance(p.transform.position,a.PassengerSeat.position)<.001f,"passenger reference follows seat across real Unity frames");
                Check(camera.enabled && camera.gameObject.activeInHierarchy,"existing first-person camera stays active");
                Check(Vector3.Distance(camera.transform.position,a.PassengerSeat.position)<2.5f,"passenger first-person viewpoint bounded near seat");
                Check(amounts.SequenceEqual(items.Cast<object>().Select(i=>GameAccess.Get(i,"item_amount"))),"inventory quantities unchanged while seated");
                Check(camera.nearClipPlane<=.03f,"passenger near clipping adapted within original camera");
                capture?.Invoke(camera);
                passenger.Exit(origin); p.transform.rotation=actorRotation;
                Check(controller.enabled==controllerEnabled && motor.enabled==motorOn && look.enabled==lookOn,"original controller and look enable states restored");
                Check(!GameAccess.Bool(p,"is_in_vehicle") && GameAccess.Get(p,"current_vehicle")==null,"native vehicle fields restored on exit");
                Check(cameras.SequenceEqual(p.GetComponentsInChildren<Camera>(true).Select(c=>c.GetInstanceID())) && listeners.SequenceEqual(p.GetComponentsInChildren<AudioListener>(true).Select(c=>c.GetInstanceID())),"camera and listener identities preserved after exit");
                Check(amounts.SequenceEqual(items.Cast<object>().Select(i=>GameAccess.Get(i,"item_amount"))),"inventory quantities unchanged after exit");
                Check(camera.nearClipPlane==oldClip,"original camera near clipping restored");
                var temporary=new GameObject("TS2MP fixture destroyed seat"); temporary.transform.SetParent(car.transform,true); temporary.transform.SetPositionAndRotation(a.PassengerSeat.position,a.PassengerSeat.rotation);
                var temporaryEyes=new GameObject(RearPassengerSeat.EyeName).transform; temporaryEyes.SetParent(temporary.transform,false); temporaryEyes.position=RearPassengerSeat.Eyes(a.PassengerSeat).position;
                passenger.Enter(p,car,temporary.transform); UnityEngine.Object.Destroy(temporary); yield return null;
                Check(p && !passenger.Active && !p.transform.IsChildOf(car.transform),"destroyed seat preserves original PlayerScript lifetime");
                passenger.Exit(origin);
                Check(controller.enabled==controllerEnabled && camera.nearClipPlane==oldClip,"destroyed seat exit restores original controller/camera");
                log("[TS2MP] NATIVE PASSENGER FIXTURE COMPLETE checks="+checks+"; evidence=controlled native fixture; real two-machine gameplay untested");
            } finally {
                passenger.Exit(origin); p.transform.rotation=actorRotation; car.transform.SetPositionAndRotation(carPosition,carRotation);
                a.Restore(); drive.enabled=driveOn; input.enabled=inputOn; if (!a.Body.isKinematic) { a.Body.velocity=oldVelocity; a.Body.angularVelocity=oldAngular; }
                fixture=false;
            }
        }
        private static Component CurrentFixtureCar(Component player)
        {
            var native=GameAccess.Get(GameAccess.Get(player,"GM"),"Car") as GameObject;
            var car=native ? native.GetComponent(Type.GetType("CarController, Assembly-CSharp",true)) : null;
            return car && native.activeInHierarchy && GameAccess.Bool(car,"is_player_car") ? car : null;
        }
        public void Dispose() { Disconnected(); SceneInvalidating(); network.World.PrepareRegistry-=Prepare; network.SeatCompleted-=Completed; network.RemoteRemoved-=Disconnected; network.ValidateSeat=null; network.ApplyVehicleImpact=null; network.CaptureTravelOccupants=null; harmony.UnpatchSelf(); if (Current==this) Current=null; }
    }
}
