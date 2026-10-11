using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    // Cosmetic identity/seat source only. No CarController, Rigidbody, EVP or input is copied.
    internal sealed class VehiclePresentation : IDisposable
    {
        private readonly Dictionary<ulong, Component> vehicles = new Dictionary<ulong, Component>();
        private readonly PlayerAvatarFactory factory = new PlayerAvatarFactory();
        private AvatarVisual driver;
        private ulong currentKey;
        private SeatRole currentRole;
        private bool scanned, attempted;
        private Component boundCar;
        private Transform anchor, nativeSeat, sourceTransform;
        internal bool Visible => driver != null;
        internal static ulong Identity(Component car)
        {
            if (!car) return 0;
            try { return UnityNetworkEntityAdapter.Describe(car, "vehicle-presentation-v3", "CarController-rear-center-v3", 0,SemanticSlot(car)).DeriveId().Value; }
            catch (ArgumentException) { return 0; }
        }
        internal static string SemanticSlot(Component car)
        {
            var p=GameAccess.Get(car,"PS") as Component; var gm=p ? GameAccess.Get(p,"GM") : null; var vm=gm!=null ? GameAccess.Get(gm,"VM") : null;
            if(vm!=null && GameAccess.Get(vm,"firstCar") as Component==car) return "VehiclesManager.firstCar";
            if(vm!=null && GameAccess.Get(vm,"van") as Component==car) return "VehiclesManager.van";
            throw new ArgumentException("Vehicle is not a verified manager slot.");
        }
        private void Scan()
        {
            scanned = true; var type = Type.GetType("CarController, Assembly-CSharp", true);
            for (int s = 0; s < SceneManager.sceneCount; s++) {
                var scene = SceneManager.GetSceneAt(s); if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) foreach (Component car in root.GetComponentsInChildren(type, true)) {
                    ulong key = Identity(car); if (key == 0) continue;
                    if (vehicles.ContainsKey(key)) vehicles[key] = null; else vehicles.Add(key, car);
                }
            }
        }
        internal bool Apply(PlayerTransform pose, Transform parent, Action<string> log)
        {
            if (!pose.State.Vehicle.IsInVehicle) { Exit(); return false; }
            ulong key = pose.State.Vehicle.PresentationKey;
            if (key != currentKey || currentRole != pose.State.Vehicle.Seat) { Exit(); currentKey = key; currentRole = pose.State.Vehicle.Seat; }
            if (!scanned) Scan();
            if (!attempted) {
                attempted = true; Component car;
                if (!vehicles.TryGetValue(key, out car) || !car) { log("[TS2MP] Vehicle seat unresolved; visible civilian fallback at replicated root"); return false; }
                var source = GameAccess.Get(car, "tpp_character") as GameObject;
                nativeSeat = GameAccess.Get(car, "sit_transform") as Transform;
                anchor = currentRole==SeatRole.Passenger ? RearPassengerSeat.Resolve(car) : nativeSeat;
                boundCar=car; sourceTransform=source ? source.transform : null;
                try { if (sourceTransform && anchor && nativeSeat) { driver = factory.CreateDriver(sourceTransform, parent, nativeSeat); log("[TS2MP] Remote seated sanitized driving_front rig created; role=" + currentRole + "; vehicleKey=" + key); } }
                catch (Exception error) { driver?.Dispose(); driver = null; log("[TS2MP] Driver source incompatible: " + error.Message + "; visible civilian fallback"); }
            }
            if (driver != null) {
                if(!boundCar || !boundCar.gameObject.activeInHierarchy || !anchor || !nativeSeat || !sourceTransform) { driver.Dispose(); driver=null; return false; }
                driver.Root.transform.SetPositionAndRotation(anchor.TransformPoint(nativeSeat.InverseTransformPoint(sourceTransform.position)),anchor.rotation*Quaternion.Inverse(nativeSeat.rotation)*sourceTransform.rotation);
                driver.Animation.SetState(LocomotionState.Idle); driver.Rig.RemoveWorldMotion(); return true;
            }
            return false;
        }
        private void Exit() { driver?.Dispose(); driver = null; boundCar=null; anchor=nativeSeat=sourceTransform=null; currentKey = 0; currentRole = SeatRole.None; attempted = false; }
        public void Dispose() { Exit(); vehicles.Clear(); scanned = false; }
    }
}
