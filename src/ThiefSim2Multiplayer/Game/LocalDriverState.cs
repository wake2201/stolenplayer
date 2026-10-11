using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    // Captures recovery state without changing the native driver's transform,
    // attachment, camera, animation, controller, or input during ordinary entry.
    internal sealed class LocalDriverState
    {
        internal Component Player {get;private set;}
        internal bool Active => Player;
        internal bool Exiting => captured && exiting;
        private VehicleNetworkAdapter vehicle;
        private Transform parent;
        private Vector3 origin, cameraPosition, centre;
        private Quaternion rotation, cameraRotation;
        private float height, clip;
        private Behaviour motor, look;
        private bool motorOn, lookOn;
        private Camera camera;
        private readonly Dictionary<string,object> flags=new Dictionary<string,object>();
        private readonly Dictionary<string,object> motorFlags=new Dictionary<string,object>();
        private readonly Dictionary<string,object> lookFlags=new Dictionary<string,object>();
        private readonly Dictionary<Collider,bool> colliders=new Dictionary<Collider,bool>();
        private readonly Dictionary<GameObject,bool> objects=new Dictionary<GameObject,bool>();
        private readonly Dictionary<Camera,bool> cameras=new Dictionary<Camera,bool>();
        private readonly Dictionary<GameObject,string> doorTags=new Dictionary<GameObject,string>();
        private bool exiting, captured;
        private double exitDeadline;
        private Vector3? exitPosition;
        internal void Capture(Component player,VehicleNetworkAdapter car)
        {
            if(captured) { if(Player==player && vehicle==car && !exiting) return; throw new InvalidOperationException("Original driver recovery still pending."); }
            if(!player || car==null || !car.IsAlive) throw new InvalidOperationException("Native driver actor/vehicle unavailable.");
            var m=GameAccess.Get(player,"PMB") as Behaviour; var l=GameAccess.Get(player,"PCB") as Behaviour;
            var cam=GameAccess.Get(player,"MainCam") as Camera; var cc=player.GetComponent<CharacterController>();
            if(!player || !car.IsAlive || !m || !l || !cam || !cc) throw new InvalidOperationException("Native driver recovery contract unavailable.");
            bool seated=GameAccess.Bool(player,"is_in_vehicle");
            // Complete the read-only capture before any lifetime/driver mutation.
            try {
            foreach(string key in new[] {"is_in_vehicle","current_vehicle","current_vehicle_running","can_drive_car","canInteract","currentItem","current_button_ID"}) flags.Add(key,GameAccess.Get(player,key));
            foreach(string key in new[] {"canMove","isCrouching","velocityY"}) motorFlags.Add(key,GameAccess.Get(m,key));
            foreach(string key in new[] {"yaw","pitch","currentRotation","cameraRotation","rotationSmoothVelocity","allowRotation"}) lookFlags.Add(key,GameAccess.Get(l,key));
            foreach(var c in player.GetComponentsInChildren<Collider>(true)) colliders[c]=c.enabled;
            foreach(var t in player.GetComponentsInChildren<Transform>(true)) objects[t.gameObject]=t.gameObject.activeSelf;
            foreach(var control in (GameObject[])GameAccess.Get(player,"ControlsObjects")) if(control) objects[control]=seated ? false : control.activeSelf;
            if(seated) objects[((GameObject[])GameAccess.Get(player,"ControlsObjects"))[0]]=true;
            foreach(var c in player.GetComponentsInChildren<Camera>(true)) cameras[c]=c.enabled;
            foreach(string key in new[] {"car_door_L","car_door_R"}) { var door=GameAccess.Get(car.Car,key) as Component; if(door) doorTags[door.gameObject]=seated ? "Door" : door.tag; }
            Player=player; vehicle=car; motor=m; look=l; camera=cam;
            parent=player.transform.parent; if(parent && parent.IsChildOf(car.Car.transform)) parent=null;
            origin=seated ? car.Exit.position+Vector3.up*(cc.height*.5f-cc.center.y) : player.transform.position;
            rotation=player.transform.rotation; cameraPosition=cam.transform.localPosition; cameraRotation=cam.transform.localRotation; clip=cam.nearClipPlane;
            centre=cc.center; height=cc.height; motorOn=m.enabled; lookOn=l.enabled;
            if(seated) {
                // Joining while Host is already driving has no pre-entry snapshot. These
                // are the verified original ExitCar defaults, not invented player objects.
                flags["is_in_vehicle"]=false; flags["current_vehicle"]=null; flags["current_vehicle_running"]=false; flags["can_drive_car"]=false; flags["canInteract"]=true;
                motorFlags["canMove"]=true; motorFlags["isCrouching"]=false; motorFlags["velocityY"]=0f;
                lookFlags["allowRotation"]=true; motorOn=lookOn=true;
                colliders[cc]=true; var capsule=player.GetComponent<CapsuleCollider>(); if(capsule) colliders[capsule]=true;
                objects[cam.gameObject]=true; cameras[cam]=true;
                var hands=GameAccess.Get(player,"HandCamera") as Camera; if(hands) cameras[hands]=true;
            }
            captured=true;
            } catch { Clear(); throw; }
        }
        internal void BeginExit(double now,Vector3? position=null) { if(captured) { exiting=true; exitDeadline=now+3; exitPosition=position; } }
        internal bool Tick(double now,Action<string> log)
        {
            if(!captured) return false;
            if(!Player) { Clear(); return true; }
            if(!vehicle.Operational || !camera || !motor || !look || !exiting && !GameAccess.Bool(Player,"is_in_vehicle")) { Restore(null,log); return true; }
            if(exiting) {
                var cc=Player.GetComponent<CharacterController>();
                if(!GameAccess.Bool(Player,"is_in_vehicle") && cc && cc.enabled) { Clear(); return false; }
                if(now>=exitDeadline) { log("[TS2MP] VEHICLE native driver exit timed out; original controller restored."); Restore(null,log); }
            }
            return false;
        }
        internal void Restore(Vector3? position,Action<string> log)
        {
            if(!captured) { Clear(); return; }
            try {
                if(vehicle?.Car) try {
                    // Only emergency restoration stops native vehicle-owned coroutines;
                    // ordinary exit retains the original door/camera sequence.
                    (vehicle.Car as MonoBehaviour)?.StopAllCoroutines();
                    try { vehicle.Car.GetType().GetMethod("Set_TPP").Invoke(vehicle.Car,new object[] {false}); }
                    catch(Exception e) { log("[TS2MP] VEHICLE TPP recovery unavailable: "+e.GetType().Name); }
                    foreach(string key in new[] {"is_sitting_inside","engine_running","gettingOut","blockDriving","blockPassingTime"}) GameAccess.Set(vehicle.Car,key,false);
                    if(vehicle.Body && !vehicle.Body.isKinematic) { vehicle.Body.velocity=Vector3.zero; vehicle.Body.angularVelocity=Vector3.zero; }
                    foreach(var f in doorTags) if(f.Key) f.Key.tag=f.Value;
                    // RunCar removes the native alarm body. A cancelled ExitCar
                    // coroutine cannot reach its normal body-restoration step.
                    var alarm=GameAccess.Get(vehicle.Car,"alarm_script") as Component;
                    if(alarm && !alarm.GetComponent<Rigidbody>()) alarm.gameObject.AddComponent<Rigidbody>().isKinematic=true;
                } catch(Exception e) { log("[TS2MP] VEHICLE native car recovery failed; restoring surviving actor independently: "+e.GetType().Name); }
                if(Player) {
                    Player.transform.SetParent(parent ? parent : null,true); Player.transform.SetPositionAndRotation(position ?? exitPosition ?? origin,rotation);
                    foreach(var f in flags) GameAccess.Set(Player,f.Key,f.Value);
                    if(motor) { foreach(var f in motorFlags) GameAccess.Set(motor,f.Key,f.Value); motor.enabled=motorOn; }
                    if(look) { foreach(var f in lookFlags) GameAccess.Set(look,f.Key,f.Value); look.enabled=lookOn; }
                    var cc=Player.GetComponent<CharacterController>(); if(cc) { cc.center=centre; cc.height=height; }
                    foreach(var f in objects) if(f.Key) f.Key.SetActive(f.Value);
                    foreach(var f in cameras) if(f.Key) f.Key.enabled=f.Value;
                    foreach(var f in colliders) if(f.Key) f.Key.enabled=f.Value;
                    if(camera) { camera.transform.localPosition=cameraPosition; camera.transform.localRotation=cameraRotation; camera.nearClipPlane=clip; }
                }
            } finally { Clear(); }
        }
        private void Clear()
        {
            parent=null; Player=null; vehicle=null; motor=look=null; camera=null; captured=exiting=false;
            exitPosition=null;
            flags.Clear(); motorFlags.Clear(); lookFlags.Clear(); colliders.Clear(); objects.Clear(); cameras.Clear(); doorTags.Clear();
        }
    }
}
