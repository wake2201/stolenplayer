using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Reflection;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class LocalPlayerAdapter
    {
        private readonly PlayerLocator locator = new PlayerLocator();
        private readonly MotorLocomotionSelector locomotion = new MotorLocomotionSelector();
        private Camera[] originalCameras;
        private AudioListener[] originalListeners;
        private int[] originalCameraIds, originalListenerIds;
        private Component player;
        private CharacterController controller;
        private Component motor, cameraBehavior;
        private Component headTilt;
        private Transform nativeHead;
        private Vector3 neutralHead;
        private FieldInfo grounded, crouched, sprinting, moving, item, crowbar, pitch, moveSpeed, runSpeed;
        private float nextObservation;
        private float minSpeed = float.PositiveInfinity, maxSpeed;
        private int sampleCount;
        private float standingHeight = 1.6f;
        private FieldInfo inVehicle, currentVehicle;
        private Component lastVehicle;
        private ulong vehicleKey;
        internal bool InVehicle => player && inVehicle != null && (bool)inVehicle.GetValue(player);
        internal GameObject CrowbarSource { get; private set; }
        private float nextSearch;
        private float suspendedUntil;
        internal Transform VisualSource { get { return player ? player.transform : null; } }
        public bool IsAvailable { get { return Time.realtimeSinceStartup >= suspendedUntil && player && player.gameObject.activeInHierarchy && controller && (controller.enabled || InVehicle || SharedVehicleController.Current?.NativeExitInProgress(player)==true || CooperativeEconomyController.NativeComputerTransition(player)) && player.gameObject.scene.isLoaded; } }
        public Vector3 Position { get { return IsAvailable ? player.transform.position : Vector3.zero; } }
        public float Yaw { get { return IsAvailable ? player.transform.eulerAngles.y : 0; } }
        internal string SceneName { get { return player ? player.gameObject.scene.name : "unavailable"; } }
        internal Vector3 ControllerCenter { get { return controller ? controller.center : Vector3.zero; } }
        internal float ControllerHeight { get { return standingHeight; } }
        internal float CurrentControllerHeight => controller ? controller.height : 0;
        internal string SceneKey { get; private set; } = "";
        internal bool Refresh()
        {
            if (Time.realtimeSinceStartup < suspendedUntil) return false;
            // The native actor may be found before its controller becomes ready.
            // When native initialization enables it later, an empty scene key still
            // needs acquisition; availability alone must not strand the load barrier.
            if ((!IsAvailable || SceneKey.Length==0) && Time.realtimeSinceStartup >= nextSearch)
            {
                nextSearch = Time.realtimeSinceStartup + 0.5f;
                var found=locator.Find(); bool changed=found!=player || !controller;
                player = found; controller = player ? player.GetComponent<CharacterController>() : null;
                if (player && (changed || SceneKey.Length==0)) CacheFields();
                SceneKey = IsAvailable ? GetSceneKey() : "";
                return IsAvailable;
            }
            return false;
        }
        internal void Reset() { player = null; controller = null; motor = cameraBehavior = headTilt = null; nativeHead=null; CrowbarSource = null; SceneKey = ""; nextSearch = 0; sampleCount = 0; minSpeed = float.PositiveInfinity; maxSpeed = 0; locomotion.Reset(); originalCameras = null; originalListeners = null; }
        private void CacheFields()
        {
            var motorType = Type.GetType("PlayerMotorBehavior, Assembly-CSharp", true);
            var cameraType = Type.GetType("PlayerCameraBehavior, Assembly-CSharp", true);
            motor = player.GetComponent(motorType); cameraBehavior = player.GetComponent(cameraType);
            headTilt = player.GetComponentInChildren(Type.GetType("HeadTilt, Assembly-CSharp", true), true);
            nativeHead = headTilt ? GameAccess.Get(headTilt, "headT") as Transform : null;
            neutralHead = headTilt ? (Vector3)GameAccess.Get(headTilt, "position_normal") : Vector3.zero;
            var defaultHeight = Field(motorType, "defaultCrouchHeight2");
            standingHeight = motor && defaultHeight != null ? (float)defaultHeight.GetValue(motor) : controller.height;
            if (!Protocol.Finite(standingHeight) || standingHeight < 0.5f || standingHeight > 4) standingHeight = controller.height;
            grounded = Field(motorType, "isGrounded"); crouched = Field(motorType, "isCrouching"); sprinting = Field(motorType, "isRunning"); moving = Field(motorType, "isMoving");
            moveSpeed = Field(motorType, "moveSpeed"); runSpeed = Field(motorType, "runSpeed");
            item = Field(player.GetType(), "currentItem"); crowbar = Field(player.GetType(), "CrowbarObject"); pitch = Field(cameraType, "pitch");
            inVehicle = Field(player.GetType(), "is_in_vehicle"); currentVehicle = Field(player.GetType(), "current_vehicle");
            CrowbarSource = crowbar == null ? null : crowbar.GetValue(player) as GameObject;
            originalCameras = player.GetComponentsInChildren<Camera>(true);
            originalListeners = player.GetComponentsInChildren<AudioListener>(true);
            originalCameraIds = Array.ConvertAll(originalCameras, c => c.GetInstanceID());
            originalListenerIds = Array.ConvertAll(originalListeners, c => c.GetInstanceID());
        }
        private static FieldInfo Field(Type type, string name) { return type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
        internal PlayerTransform Sample()
        {
            var pose = new PlayerTransform { X = Position.x, Y = Position.y, Z = Position.z, Yaw = Yaw };
            if (!IsAvailable) return pose;
            Vector3 velocity = controller.enabled ? controller.velocity : Vector3.zero;
            var state = new PlayerReplicationState { VelocityX = velocity.x, VelocityY = velocity.y, VelocityZ = velocity.z, Known = SampledFields.Velocity, EquippedItemId = -1 };
            state.SeatEligibility = SharedVehicleController.Eligible(player) && !InVehicle ? SeatEligibility.Eligible : SeatEligibility.Ineligible;
            if (motor && grounded != null) { state.Grounded = (bool)grounded.GetValue(motor); state.Known |= SampledFields.Grounded; }
            if (motor && crouched != null) { state.Crouched = (bool)crouched.GetValue(motor); state.Known |= SampledFields.Crouched; }
            if (motor && sprinting != null) { state.Sprinting = (bool)sprinting.GetValue(motor); state.Known |= SampledFields.Sprinting; }
            state.GroundFeet = controller.center.y - controller.height * 0.5f; state.Known |= SampledFields.GroundFeet;
            if (InVehicle)
            {
                var vehicle = currentVehicle.GetValue(player) as Component;
                if (vehicle != lastVehicle) { lastVehicle = vehicle; vehicleKey = VehiclePresentation.Identity(vehicle); }
                state.Vehicle = new VehicleOccupancyState { Seat = SeatRole.Driver, PresentationKey = vehicleKey };
                if (SharedVehicleController.Current != null) state.Vehicle = SharedVehicleController.Current.LocalOccupancy;
                state.Locomotion = LocomotionState.Idle;
            }
            else { lastVehicle = null; vehicleKey = 0; }
            // currentItem is a first-person slot, NOT an ItemID. Verified slot 1 = definition 0; 0/99 = no held tool.
            if (item != null)
            {
                int slot = (int)item.GetValue(player);
                short equipped;
                if (EquipmentSlotMap.TryGetItem(slot, out equipped)) { state.EquippedItemId = equipped; state.Known |= SampledFields.Equipment; }
            }
            if (cameraBehavior && pitch != null) { state.LookPitch = Mathf.Clamp((float)pitch.GetValue(cameraBehavior), -80, 80); state.Known |= SampledFields.LookPitch; }
            if (nativeHead && !InVehicle && !GameAccess.Bool(headTilt,"feel_damage") && !GameAccess.Bool(headTilt,"feel_damage_weak")) {
                // Observe the native resulting pose, including blocked peeks and return to centre.
                state.LeanOffset = Mathf.Clamp(nativeHead.localPosition.x - neutralHead.x, -1, 1);
                state.LeanRoll = Mathf.Clamp(Mathf.DeltaAngle(0, nativeHead.localEulerAngles.z), -12, 12);
                state.Known |= SampledFields.Lean;
            }
            // Verified motor semantics avoid guessing walk/run velocity thresholds.
            bool isMoving = motor && moving != null && (bool)moving.GetValue(motor);
            if (!InVehicle) state.Locomotion = locomotion.Select(state, isMoving, Time.realtimeSinceStartup);
            pose.State = state; return pose;
        }
        internal void Audit(Action<string> log)
        {
            if (!IsAvailable) { log("[TS2MP] Local presentation audit: unavailable"); return; }
            var cameras = player.GetComponentsInChildren<Camera>(true);
            var listeners = player.GetComponentsInChildren<AudioListener>(true);
            bool unchanged = cameras.Length == originalCameras.Length && listeners.Length == originalListeners.Length;
            for (int i = 0; unchanged && i < cameras.Length; i++) unchanged &= cameras[i].GetInstanceID() == originalCameraIds[i];
            for (int i = 0; unchanged && i < listeners.Length; i++) unchanged &= listeners[i].GetInstanceID() == originalListenerIds[i];
            log("[TS2MP] Local presentation audit: cameras=" + cameras.Length + "; listeners=" + listeners.Length + "; originalIdsUnchanged=" + unchanged + "; controllerEnabled=" + controller.enabled + "; playerEnabled=" + ((Behaviour)player).enabled + "; crowbarSource=" + (CrowbarSource ? CrowbarSource.GetInstanceID().ToString() : "unavailable"));
        }
        internal void Observe(Action<string> log, bool enabled)
        {
            if (!enabled || !IsAvailable) return;
            var sample = Sample(); float speed = sample.State.PlanarSpeed;
            minSpeed = Mathf.Min(minSpeed, speed); maxSpeed = Mathf.Max(maxSpeed, speed); sampleCount++;
            if (Time.realtimeSinceStartup < nextObservation) return;
            nextObservation = Time.realtimeSinceStartup + 1;
            log("[TS2MP] Local semantic observation: state=" + sample.State.Locomotion + "; planar=" + speed.ToString("F4") + "; range=" + minSpeed.ToString("F4") + ".." + maxSpeed.ToString("F4") + "; samples=" + sampleCount + "; grounded=" + sample.State.Grounded + "; crouched=" + sample.State.Crouched + "; sprint=" + sample.State.Sprinting + "; item=" + sample.State.EquippedItemId + "; pitch=" + sample.State.LookPitch.ToString("F2") + "; motorWalk=" + (moveSpeed == null ? "unknown" : moveSpeed.GetValue(motor).ToString()) + "; motorRun=" + (runSpeed == null ? "unknown" : runSpeed.GetValue(motor).ToString()));
            minSpeed = float.PositiveInfinity; maxSpeed = 0; sampleCount = 0;
        }
        internal void SuspendForAcceptanceTest()
        { Reset(); suspendedUntil = Time.realtimeSinceStartup + 2; }
        private static string GetSceneKey()
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !string.IsNullOrEmpty(scene.path)) names.Add(scene.name);
            }
            names.Sort(StringComparer.Ordinal);
            string key = string.Join("|", names.ToArray());
            // Never encode a truncated ambiguous scene identity.
            return System.Text.Encoding.UTF8.GetByteCount(key) <= 160 ? key : "";
        }
    }
}
