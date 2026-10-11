using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    // Scoped suspension of the genuine controller. No camera, listener, inventory or player clone.
    internal sealed class LocalPassengerState : IDisposable
    {
        internal Component Player { get; private set; }
        private Transform seat, parent;
        private Component car;
        private Behaviour motor, look;
        private bool motorEnabled, lookEnabled;
        private readonly Dictionary<Collider,bool> colliders = new Dictionary<Collider,bool>();
        private readonly Dictionary<Renderer,bool> renderers = new Dictionary<Renderer,bool>();
        private readonly Dictionary<string,object> fields = new Dictionary<string,object>();
        private Camera camera;
        private Vector3 cameraLocal, eyeOffset;
        private Quaternion cameraRotation;
        private Quaternion actorRotation;
        private Vector3 fallbackExit;
        private float nearClip;
        private readonly Dictionary<string,object> lookFields=new Dictionary<string,object>();
        private bool captured;
        private float yaw, pitch;
        internal bool Active => Player && seat && car && camera && motor && look;
        // Only the control gates suspended by our seat adaptation may use their
        // captured values. All other native storage restrictions remain live.
        internal bool StorageControlsReady => Active && motorEnabled && lookEnabled && (bool)fields["canInteract"];
        internal bool InventoryControlsReady => Active && (motorEnabled || GameAccess.Bool(Player,"isHiding"));
        internal void Enter(Component player, Component vehicle, Transform anchor)
        {
            if (captured) throw new InvalidOperationException("Passenger already attached.");
            if (!player || !vehicle || !anchor || !anchor.IsChildOf(vehicle.transform)) throw new ArgumentException("Verified authored passenger seat required.");
            // Preflight all essential references before changing even one native component.
            var m=GameAccess.Get(player,"PMB") as Behaviour; var l=GameAccess.Get(player,"PCB") as Behaviour; var cam=GameAccess.Get(player,"MainCam") as Camera;
            if(!m || !l || !cam || !cam.enabled || !cam.gameObject.activeInHierarchy || !player.GetComponent<CharacterController>()) throw new InvalidOperationException("Original passenger controller/camera unavailable.");
            Player=player; car=vehicle; seat=anchor; parent=player.transform.parent;
            motor=m; look=l; camera=cam;
            motorEnabled=motor.enabled; lookEnabled=look.enabled;
            foreach (string key in new[] { "is_in_vehicle", "current_vehicle", "current_vehicle_running", "can_drive_car", "canInteract" }) fields.Add(key,GameAccess.Get(player,key));
            cameraLocal=camera.transform.localPosition; cameraRotation=camera.transform.localRotation;
            actorRotation=player.transform.rotation; fallbackExit=player.transform.position; nearClip=camera.nearClipPlane;
            foreach(string key in new[] { "yaw","pitch","currentRotation","cameraRotation","rotationSmoothVelocity" }) lookFields.Add(key,GameAccess.Get(look,key));
            var eyes=RearPassengerSeat.Eyes(anchor);
            if(!eyes) { ClearCapture(); throw new InvalidOperationException("Verified rear seated eye reference required."); }
            eyeOffset=anchor.InverseTransformPoint(eyes.position);
            foreach (var c in player.GetComponentsInChildren<Collider>(true)) colliders[c]=c.enabled;
            foreach (var r in player.GetComponentsInChildren<Renderer>(true)) renderers[r]=r.enabled;
            captured=true;
            try {
            foreach(var c in colliders.Keys) c.enabled=false;
            foreach(var r in renderers.Keys) r.enabled=false;
            motor.enabled=false; look.enabled=false;
            camera.nearClipPlane=Mathf.Min(nearClip,.03f);
            GameAccess.Set(player,"is_in_vehicle",true); GameAccess.Set(player,"current_vehicle",vehicle); GameAccess.Set(player,"can_drive_car",false); GameAccess.Set(player,"canInteract",false);
            // Keep Unity lifetime ownership under the original parent. Destroying a car must never destroy PlayerScript.
            player.transform.SetPositionAndRotation(anchor.position,anchor.rotation);
            yaw=pitch=0; Tick(0,0);
            } catch { Exit(fallbackExit); throw; }
        }
        internal void Tick(float dx,float dy)
        {
            if (!Active) return;
            motor.enabled=false; look.enabled=false;
            foreach (var c in colliders.Keys) if (c) c.enabled=false;
            yaw=Mathf.Clamp(yaw+dx,-100,100); pitch=Mathf.Clamp(pitch-dy,-65,65);
            Player.transform.SetPositionAndRotation(seat.position,seat.rotation);
            // Retain the existing camera chain and its AudioListener; suppress native camera writes only while seated.
            camera.transform.SetPositionAndRotation(seat.TransformPoint(eyeOffset),seat.rotation*Quaternion.Euler(pitch,yaw,0));
        }
        internal void Exit(Vector3? exit)
        {
            if(!captured) { ClearCapture(); return; }
            var p=Player;
            try {
            if (p) {
                p.transform.SetParent(parent ? parent : null,true);
                p.transform.position=exit ?? fallbackExit;
                p.transform.rotation=actorRotation;
                foreach (var pair in fields) GameAccess.Set(p,pair.Key,pair.Value);
                if (camera) { camera.transform.localPosition=cameraLocal; camera.transform.localRotation=cameraRotation; camera.nearClipPlane=nearClip; }
                if (motor) motor.enabled=motorEnabled;
                if (look) { foreach(var pair in lookFields) GameAccess.Set(look,pair.Key,pair.Value); look.enabled=lookEnabled; }
                foreach (var pair in colliders) if (pair.Key) pair.Key.enabled=pair.Value;
                foreach (var pair in renderers) if (pair.Key) pair.Key.enabled=pair.Value;
            }
            } finally { ClearCapture(); }
        }
        public void Dispose() { Exit(null); }
        private void ClearCapture() { Player=null; seat=parent=null; car=null; motor=look=null; camera=null; captured=false; colliders.Clear(); renderers.Clear(); fields.Clear(); lookFields.Clear(); }
    }
}
