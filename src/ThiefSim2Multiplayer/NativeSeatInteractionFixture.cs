using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Game;

namespace ThiefSim2Multiplayer
{
    public sealed partial class Plugin
    {
        private static object seatFixtureInput;
        private static int seatFixtureFrame=-1;
        private static bool seatFixtureAllowPassengerInput;
        internal static bool NativeInputFixtureActive => seatFixtureInput!=null || stabilityFixtureActive;
        private static bool SeatFixtureButton(object __instance,string __0,MethodBase __originalMethod,ref bool __result)
        {
            if(!ReferenceEquals(__instance,seatFixtureInput)) return true;
            __result=__0=="interact" && __originalMethod.Name=="GetButtonDown" && Time.frameCount==seatFixtureFrame; return false;
        }
        private static bool SeatFixturePlayerUpdate(Component __instance)
        {
            // This fixture directly invokes a native entry boundary, rather than
            // aiming a human raycast. Suppress unrelated on-foot raycast actions
            // throughout the fixture; seated native handling remains enabled.
            return GameAccess.Bool(__instance,"is_in_vehicle") || !ReferenceEquals(GameAccess.Get(__instance,"rewiredPlayer"),seatFixtureInput) || seatFixtureAllowPassengerInput && Time.frameCount==seatFixtureFrame;
        }
        private static bool SeatFixtureAxis(object __instance,string __0,ref float __result)
        {
            if(!ReferenceEquals(__instance,seatFixtureInput)) return true;
            if(__0=="forward_back" || __0=="left_right" || __0=="look_left_right" || __0=="look_up_down") { __result=0; return false; }
            if(__0!="left_right_car" && __0!="drive_forward" && __0!="handbrake_axis") return true;
            __result=__0=="handbrake_axis" ? 1f : 0f; return false;
        }
        private IEnumerator NativeSeatInteractionFixture(int cycles=20)
        {
            if(network.Session.State!=SessionState.Offline) throw new InvalidOperationException("Seat input fixture requires offline backed-up gameplay.");
            var p=new PlayerLocator().Find(); if(!p || GameAccess.Bool(p,"is_in_vehicle")) throw new InvalidOperationException("Native on-foot actor required.");
            var origin=p.transform.position; var orientation=p.transform.rotation; var camera=GameAccess.Get(p,"MainCam") as Camera; var inventory=GameAccess.Get(p,"invScript");
            var nativeCamera=GameAccess.Get(p,"cam") as Transform; var nativeCameraRotation=nativeCamera ? nativeCamera.rotation : Quaternion.identity;
            var cameraIds=p.GetComponentsInChildren<Camera>(true).Select(c=>c.GetInstanceID()).ToArray();
            var listenerIds=p.GetComponentsInChildren<AudioListener>(true).Select(c=>c.GetInstanceID()).ToArray();
            NetworkManager peer=null; var inputPatch=new Harmony(Id+".seat-input-fixture"); int checks=0;
            void Check(bool value,string label) { if(!value) throw new InvalidOperationException("Native seat input FAIL: "+label); checks++; Trace("[TS2MP] NATIVE SEAT INPUT PASS: "+label); }
            PlayerTransform pose=default(PlayerTransform);
            string lifecycle="";
            void Lifecycle() {
                var cc=p ? p.GetComponent<CharacterController>() : null;
                string current="player="+(p ? p.GetInstanceID() : 0)+" inVehicle="+(p && GameAccess.Bool(p,"is_in_vehicle"))+" cc="+(cc && cc.enabled)+" located="+(new PlayerLocator().Find()==p)+" scene="+network.Scenes.State+" available="+local.IsAvailable+" remoteVisible="+remote.Visible;
                if(p) current+=" canInteract="+GameAccess.Bool(p,"canInteract")+" closeUp="+GameAccess.Bool(p,"closeUp")+" caught="+GameAccess.Bool(p,"caughtUp")+" carry="+GameAccess.Bool(p,"isCarrying")+" phone="+GameAccess.Bool(p,"having_phone_convo")+" scale="+Time.timeScale;
                if(current!=lifecycle) { lifecycle=current; Trace("[TS2MP] NATIVE LIFECYCLE "+current); }
            }
            void Pump() {
                // Background native input can open a menu after the initial fixture
                // cleanup. Resume through its original close routine, so a paused
                // native seat lerp cannot consume the unscaled reservation deadline.
                // This is confined to the explicitly invoked development fixture.
                if(Time.timeScale<=0 && p) {
                    if((GameAccess.Get(p,"TownSelection") as GameObject)?.activeInHierarchy==true) {
                        Trace("[TS2MP] NATIVE SEAT INPUT fixture closes original town selection");
                        p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {GameAccess.Bool(p,"is_in_vehicle")});
                    } else if((GameAccess.Get(p,"PauseMenuObj") as GameObject)?.activeInHierarchy==true || GameAccess.Bool(GameAccess.Get(p,"invScript"),"isEnabled")) {
                        Trace("[TS2MP] NATIVE SEAT INPUT fixture closes original inventory/pause menu");
                        GameAccess.Call(p,"DisableInventory");
                    }
                }
                Lifecycle(); peer.Poll(); peer.SendState(pose);
            }
            IEnumerator Wait(Func<bool> ready,string phase,int seconds=10) {
                double end=network.Now+seconds;
                while(!ready()) { Pump(); if(network.Now>end || network.Scenes.State==SceneTransitionState.Failed) throw new InvalidOperationException("Native seat input timeout: "+phase); yield return null; }
                Check(true,phase);
            }
            IEnumerator Press(Component car,string nativeBoundary) {
                seatFixtureFrame=Time.frameCount+1; yield return null;
                GameAccess.Call(car,nativeBoundary); yield return new WaitForEndOfFrame();
            }
            IEnumerator Enter(Component car,string nativeBoundary,SeatRole localRole,VehicleNetworkAdapter adapter) {
                // Driver requests exercise the verified native boundary. Passenger
                // requests aim at an open rear Door and exercise the real native E input.
                if(localRole==SeatRole.Driver) {
                    seatFixtureFrame=Time.frameCount+1; yield return null; GameAccess.Call(car,nativeBoundary);
                } else {
                    yield return EnsureRearDoorOpenAndPressEntry(adapter,true);
                }
                yield return new WaitForEndOfFrame();
            }
            var aBoundDoor=default(Component); var aBoundDoorRef=default(EntityRef); bool originalRearDoorOpen=false;
            IEnumerator EnsureRearDoorOpenAndPressEntry(VehicleNetworkAdapter adapter,bool pressEntry) {
                if(!aBoundDoor || !adapter.IsRearPassengerDoor(aBoundDoor) || WorldInteractionController.Current==null ||
                    !WorldInteractionController.Current.TryGetRearPassengerDoor(aBoundDoor,adapter,out aBoundDoorRef)) throw new InvalidOperationException("A registered native rear passenger door is required for the fixture.");
                if(!GameAccess.Bool(aBoundDoor,"isOpen")) { GameAccess.Call(aBoundDoor,"Use"); yield return null; }
                yield return Wait(()=>GameAccess.Bool(aBoundDoor,"isOpen") && !GameAccess.Bool(aBoundDoor,"opening") && !GameAccess.Bool(aBoundDoor,"openingSlowly") && !GameAccess.Bool(aBoundDoor,"load_slow"),"native rear passenger door open and settled",15);
                if(!pressEntry) yield break;
                var collider=aBoundDoor.GetComponents<Collider>().FirstOrDefault(c=>c && c.enabled && c.gameObject.activeInHierarchy);
                if(!collider || !nativeCamera) throw new InvalidOperationException("Native rear door collider/camera unavailable.");
                Vector3 outward=(aBoundDoor.transform.position-adapter.PassengerSeat.position).normalized;
                if(outward.sqrMagnitude<.01f) outward=adapter.Car.transform.right;
                Vector3 localEye=p.transform.InverseTransformPoint(nativeCamera.position);
                Vector3 desiredEye=aBoundDoor.transform.position+outward*1.4f+adapter.Car.transform.up*.15f;
                p.transform.position=desiredEye-p.transform.TransformVector(localEye); Physics.SyncTransforms();
                Vector3 eye=nativeCamera.position, target=collider.ClosestPoint(eye);
                if((target-eye).sqrMagnitude<.01f) target=collider.bounds.center;
                nativeCamera.rotation=Quaternion.LookRotation(target-eye,adapter.Car.transform.up);
                Physics.SyncTransforms();
                seatFixtureFrame=Time.frameCount+1; seatFixtureAllowPassengerInput=true; yield return null;
                yield return new WaitForEndOfFrame(); seatFixtureAllowPassengerInput=false;
            }
            IEnumerator Confirm(VehicleNetworkAdapter a,SeatRole role,SeatAction action,Func<bool> ready,string phase) {
                double end=network.Now+10,next=0;
                while(!ready()) { Pump(); if(network.Now>=next) { next=network.Now+.3; peer.RequestSeat(a.Entity,role,action); } if(network.Now>end) throw new InvalidOperationException("Native seat input timeout: "+phase); yield return null; }
                Check(true,phase);
            }
            try {
                seatFixtureInput=GameAccess.Get(p,"rewiredPlayer");
                foreach(string method in new[] {"GetButtonDown","GetButton","GetButtonUp"}) inputPatch.Patch(seatFixtureInput.GetType().GetMethod(method,new[] {typeof(string)}),prefix:new HarmonyMethod(typeof(Plugin),nameof(SeatFixtureButton)));
                inputPatch.Patch(AccessTools.Method(p.GetType(),"Update"),prefix:new HarmonyMethod(typeof(Plugin),nameof(SeatFixturePlayerUpdate)));
                inputPatch.Patch(seatFixtureInput.GetType().GetMethod("GetAxis",new[] {typeof(string)}),prefix:new HarmonyMethod(typeof(Plugin),nameof(SeatFixtureAxis)));
                // Close only original native UI before taking the gameplay fixture.
                // Physical background typing must not pause or select maps mid-test.
                if(GameAccess.Bool(GameAccess.Get(p,"invScript"),"isEnabled") || (GameAccess.Get(p,"PauseMenuObj") as GameObject)?.activeInHierarchy==true) GameAccess.Call(p,"DisableInventory");
                p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {false});
                yield return new WaitForSecondsRealtime(.6f);
                peer=new NetworkManager(network.BuildFingerprintForDiagnostics,Trace);
                peer.SeatCompleted+=(id,result)=>Trace("[TS2MP] NATIVE SEAT INPUT managed request="+id+" result="+result);
                peer.World.AdapterFactory=type=>new FixtureRecord(type,new byte[0]);
                peer.World.PrepareBaseline=s=>MirrorFixture(peer.World.Registry,s.Entities); peer.HostSceneRequested+=s=>peer.MarkSceneLoaded(s.SceneKey);
                Check(network.Host(FixturePort(),"NativeInputHost"),"production Host starts for original Rewired interaction");
                yield return null; yield return null; peer.Join("127.0.0.1",network.Port,"ManagedSeatPeer");
                yield return Wait(()=>network.GameplayReady && peer.GameplayReady,"production scene/world/time readiness",45);
                var currentCar=GameAccess.Get(GameAccess.Get(p,"GM"),"Car") as GameObject;
                var a=network.World.Registry.Capture().Select(r=>network.World.Registry.GetBinding(r.Entity)).OfType<VehicleNetworkAdapter>().First(v=>v.PassengerSeat && v.Car.gameObject==currentCar);
                aBoundDoor=a.NativeDoors.FirstOrDefault(a.IsRearPassengerDoor);
                if(!aBoundDoor || !WorldInteractionController.Current.TryGetRearPassengerDoor(aBoundDoor,a,out aBoundDoorRef)) throw new InvalidOperationException("Supported player car has no registered native rear passenger door.");
                originalRearDoorOpen=GameAccess.Bool(aBoundDoor,"isOpen");
                int carId=a.Car.GetInstanceID(); var entity=a.Entity; uint sceneEpoch=network.World.Registry.SceneEpoch; var visualKey=a.PresentationKey;
                var box=GameAccess.Get(a.Car,"car_box_script") as Component; var boxPosition=box ? box.transform.localPosition : Vector3.zero; bool boxActive=box && box.gameObject.activeSelf;
                var alarm=GameAccess.Get(a.Car,"alarm_script") as Component;
                var originalBodies=a.Car.GetComponentsInChildren<Rigidbody>(true).Where(b=>!alarm || b.gameObject!=alarm.gameObject).Select(b=>b.GetInstanceID()).OrderBy(id=>id).ToArray();
                for(int cycle=0;cycle<cycles;cycle++) {
                    var remoteRole=cycle%2==0 ? SeatRole.Driver : SeatRole.Passenger;
                    var anchor=remoteRole==SeatRole.Driver ? a.DriverSeat : a.PassengerSeat;
                    pose=new PlayerTransform {X=anchor.position.x,Y=anchor.position.y,Z=anchor.position.z,State=new PlayerReplicationState {SeatEligibility=SeatEligibility.Eligible,EquippedItemId=-1}};
                    yield return Wait(()=>network.Telemetry.HasPose && network.Telemetry.Latest.State.SeatEligibility==SeatEligibility.Eligible,"authenticated managed peer outside role "+remoteRole);
                    if(remoteRole==SeatRole.Passenger) yield return EnsureRearDoorOpenAndPressEntry(a,false);
                    peer.RequestSeat(a.Entity,remoteRole,SeatAction.Enter,remoteRole==SeatRole.Passenger ? aBoundDoorRef : default(EntityRef));
                    yield return Wait(()=>((IVehicleAdapter)peer.World.Registry.GetBinding(a.Entity)).State.Pending(remoteRole)==2,"managed peer reserves "+remoteRole);
                    pose.State.Vehicle=new VehicleOccupancyState {Seat=remoteRole,PresentationKey=a.PresentationKey}; pose.State.SeatEligibility=SeatEligibility.Ineligible;
                    for(int n=0;n<5;n++) { Pump(); yield return null; }
                    yield return Confirm(a,remoteRole,SeatAction.Confirm,()=>a.State.Occupant(remoteRole)==2,"managed peer confirms "+remoteRole);
                    p.transform.position=a.Exit.position;
                    yield return Wait(()=>SharedVehicleController.Eligible(p) && !GameAccess.Bool(p,"is_in_vehicle"),"original on-foot entry eligibility restored before "+remoteRole);
                    var localRole=remoteRole==SeatRole.Driver ? SeatRole.Passenger : SeatRole.Driver;
                    yield return Enter(a.Car,"EnterVehicle",localRole,a);
                    yield return Wait(()=>a.State.Occupant(localRole)==1,localRole==SeatRole.Driver ? "native driver entry follows original boundary after authorization" : "passenger-specific presentation attaches to approved seat");
                    for(int n=0;n<5;n++) { Pump(); yield return new WaitForEndOfFrame(); }
                    Check(a.State.Occupant(localRole)==1 && GameAccess.Bool(p,"is_in_vehicle") && !a.State.Exiting(localRole),"entry pulse is not reused as immediate exit role "+localRole);
                    Check(remote.Visible && remote.DriverVisible,"NetworkAvatar remote seated presentation role "+remoteRole);
                    if(cycle<2) {
                        var root=Resources.FindObjectsOfTypeAll<GameObject>().Single(g=>g && g.activeInHierarchy && g.name.StartsWith("TS2MP_RemotePlayer_",StringComparison.Ordinal));
                        var rig=root.GetComponentsInChildren<Transform>(true).First(t=>t.name=="AvatarRig" && t.gameObject.activeInHierarchy);
                        var hips=rig.GetComponentsInChildren<Transform>(true).First(t=>t.name=="mixamorig:Hips"); var head=rig.GetComponentsInChildren<Transform>(true).First(t=>t.name=="mixamorig:Head");
                        var h=a.Car.transform.InverseTransformPoint(hips.position); var eye=a.Car.transform.InverseTransformPoint(camera.transform.position);
                        Trace("[TS2MP] SEAT GEOMETRY role="+remoteRole+" vehicle="+a.Car.transform.position.ToString("F4")+" driver="+a.Car.transform.InverseTransformPoint(a.DriverSeat.position).ToString("F4")+" rear="+a.Car.transform.InverseTransformPoint(a.PassengerSeat.position).ToString("F4")+" player="+a.Car.transform.InverseTransformPoint(p.transform.position).ToString("F4")+" network="+a.Car.transform.InverseTransformPoint(root.transform.position).ToString("F4")+" visual="+a.Car.transform.InverseTransformPoint(rig.parent.position).ToString("F4")+" rig="+a.Car.transform.InverseTransformPoint(rig.position).ToString("F4")+" hips="+h.ToString("F4")+" head="+a.Car.transform.InverseTransformPoint(head.position).ToString("F4")+" camera="+eye.ToString("F4"));
                        if(remoteRole==SeatRole.Passenger) Check(Mathf.Abs(h.x)<.04f && h.z<a.Car.transform.InverseTransformPoint(a.DriverSeat.position).z-.3f,"remote hips occupy the verified rear center of the cabin");
                    }
                    Check(GameAccess.Get(p,"invScript")==inventory && GameAccess.Get(p,"MainCam") as Camera==camera,"original player inventory/camera retained role "+localRole);
                    if(cycle==0 && remoteRole==SeatRole.Driver) {
                        Check(peer.SendDriverInput(a.Entity,0,0,1,true),"managed remote driver publishes running engine before exit");
                        for(int n=0;n<5;n++) { Pump(); yield return new WaitForEndOfFrame(); }
                        Check(GameAccess.Bool(a.Car,"engine_running"),"Host mirrors running remote engine before exit");
                    }
                    if(localRole==SeatRole.Driver) {
                        a.Car.GetType().GetMethod("RunEngine").Invoke(a.Car,new object[] {true});
                        yield return new WaitForSecondsRealtime(.3f); p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {true});
                        yield return new WaitForEndOfFrame();
                        Check(!p.transform.IsChildOf(a.Car.transform),"native driver camera retained outside vehicle destruction hierarchy");
                    }
                    yield return Wait(()=>a.Body.velocity.magnitude<.5f,"controlled vehicle stopped before input exit "+localRole);
                    Vector3 exitPoint; bool safe=SharedVehicleController.SafeExit(a,localRole,out exitPoint);
                    Trace("[TS2MP] NATIVE SEAT EXIT PROOF role="+localRole+" speed="+a.Body.velocity.magnitude+" safe="+safe+" exit="+exitPoint);
                    yield return Press(a.Car,"ExitVehicle");
                    yield return Wait(()=>a.State.Occupant(localRole)==0 && p.GetComponent<CharacterController>().enabled,"native input exit restores and authoritatively releases "+localRole);
                    Check(!GameAccess.Bool(p,"is_in_vehicle") && camera.enabled,"original first person resumes after "+localRole);
                    // Keep the actual opposite-side native exit. The fixture's initial
                    // on-foot origin can itself be the other seat's exit capsule.
                    yield return Confirm(a,remoteRole,SeatAction.Exit,()=>a.State.Exiting(remoteRole),"managed occupant begins exit "+remoteRole);
                    pose.State.Vehicle=default(VehicleOccupancyState); pose.State.SeatEligibility=SeatEligibility.Eligible;
                    for(int n=0;n<5;n++) { Pump(); yield return null; }
                    yield return Confirm(a,remoteRole,SeatAction.ExitConfirm,()=>a.State.Occupant(remoteRole)==0,"managed occupant releases "+remoteRole);
                    for(int n=0;n<5;n++) { Pump(); yield return new WaitForEndOfFrame(); }
                    Check(remote.Visible && !remote.DriverVisible,"remote resumes ordinary avatar after "+remoteRole);
                    Check(!GameAccess.Bool(a.Car,"engine_running"),"native engine flag cleared after both occupants exit cycle "+cycle);
                    Check(Resources.FindObjectsOfTypeAll<GameObject>().Count(g=>g && g.activeInHierarchy && g.name.StartsWith("TS2MP_RemotePlayer_",StringComparison.Ordinal))==1,"one visible remote character after "+remoteRole);
                    Check(a.Car && a.Car.GetInstanceID()==carId && a.Entity.Equals(entity) && network.World.Registry.GetBinding(entity)==a && !network.World.Registry.Lookup(entity).State.Tombstone && network.World.Registry.SceneEpoch==sceneEpoch,"persistent native vehicle/entity/epoch cycle "+cycle);
                    Check(VehiclePresentation.Identity(a.Car)==visualKey,"seat parenting preserves semantic vehicle identity cycle "+cycle);
                    var bodies=a.Car.GetComponentsInChildren<Rigidbody>(true).Where(b=>!alarm || b.gameObject!=alarm.gameObject).Select(b=>b.GetInstanceID()).OrderBy(id=>id).ToArray();
                    Check(a.Car.gameObject.activeInHierarchy && ((Behaviour)a.Car).enabled && a.Car.GetComponent<Rigidbody>()==a.Body && bodies.SequenceEqual(originalBodies) && (!alarm || alarm.GetComponents<Rigidbody>().Length==1),"original physics identities and one native alarm body restored cycle "+cycle);
                    Check(box && box.transform.localPosition==boxPosition && box.gameObject.activeSelf==boxActive,"front lootbox untouched cycle "+cycle);
                    Check(network.Scenes.State==SceneTransitionState.WorldReady && network.GameplayReady && peer.GameplayReady,"exit does not close scene barrier cycle "+cycle);
                }
                if(!originalRearDoorOpen && GameAccess.Bool(aBoundDoor,"isOpen")) {
                    if(GameAccess.Bool(aBoundDoor,"opening") || GameAccess.Bool(aBoundDoor,"openingSlowly")) GameAccess.Call(aBoundDoor,"Resett");
                    GameAccess.Set(aBoundDoor,"canUse",true); GameAccess.Call(aBoundDoor,"Use");
                    yield return Wait(()=>!GameAccess.Bool(aBoundDoor,"isOpen") && !GameAccess.Bool(aBoundDoor,"opening") && !GameAccess.Bool(aBoundDoor,"openingSlowly"),"fixture restores original native rear door state",15);
                }
                Check(cameraIds.SequenceEqual(p.GetComponentsInChildren<Camera>(true).Select(c=>c.GetInstanceID())) && listenerIds.SequenceEqual(p.GetComponentsInChildren<AudioListener>(true).Select(c=>c.GetInstanceID())),"all original camera/listener identities preserved");
                Trace("[TS2MP] NATIVE SEAT INPUT COMPLETE checks="+checks+"; original Rewired/native boundary; managed peer; human approach/two-machine gate deferred");
            } finally {
                seatFixtureAllowPassengerInput=false;
                if(aBoundDoor && !originalRearDoorOpen && GameAccess.Bool(aBoundDoor,"isOpen")) try { if(GameAccess.Bool(aBoundDoor,"opening") || GameAccess.Bool(aBoundDoor,"openingSlowly")) GameAccess.Call(aBoundDoor,"Resett"); GameAccess.Set(aBoundDoor,"canUse",true); GameAccess.Call(aBoundDoor,"Use"); } catch { }
                seatFixtureInput=null; seatFixtureFrame=-1; inputPatch.UnpatchSelf(); network.Disconnect("Native input fixture cleanup."); peer?.Dispose();
                if(p && !GameAccess.Bool(p,"is_in_vehicle")) p.transform.SetPositionAndRotation(origin,orientation);
                if(nativeCamera) nativeCamera.rotation=nativeCameraRotation;
            }
        }
    }
}
