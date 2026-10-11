using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ThiefSim2Multiplayer.Diagnostics
{
    // Explicit, bounded development input at the existing Rewired read boundary.
    // Never writes player transforms, motor flags, inventory, camera fields or tools.
    public sealed partial class Plugin
    {
        private bool milestoneBEnabled;
        private static Plugin inputOwner;
        private Harmony inputFixture;
        private object fixturePlayer;
        private float fixtureEnd, fixtureForward, fixtureRight, fixtureLookX, fixtureLookY;
        private bool fixtureRun;
        private string fixtureButton = "";
        private int fixtureFrame;
        private int fixtureReleaseFrame = -1;
        private float nextBObservation;

        private bool MilestoneBRequest(string request)
        {
            if (!request.StartsWith("mb-", StringComparison.Ordinal)) return false;
            if (!milestoneBEnabled) { Emit("mb_rejected", "reason", "development fixture disabled"); return true; }
            try
            {
                if (request == "mb-input") StartInputFixture();
                else if (request == "mb-input-stop") StopInputFixture();
                else
                {
                    Type type = Type.GetType("ThiefSim2Multiplayer.Plugin, ThiefSim2Multiplayer", true);
                    var plugin = Resources.FindObjectsOfTypeAll(type).OfType<Component>().Single();
                    object network = ReadB(plugin, "network");
                    if (request.StartsWith("mb-vehicle-", StringComparison.Ordinal))
                    {
                        string action=request.Substring("mb-vehicle-".Length);
                        string result=action=="variant-cycle" ? (string)type.GetMethod("DevelopmentVehicleVariantCycle").Invoke(plugin,new object[] {outputDirectory}) : action=="stability-cycle" || action=="mission-drive-cycle" ? (string)type.GetMethod("DevelopmentStabilityCycle").Invoke(plugin,new object[] {outputDirectory,action=="mission-drive-cycle"}) : action=="passenger-preview" ? (string)type.GetMethod("DevelopmentPassengerPreview").Invoke(plugin,new object[] {outputDirectory}) : action=="geometry" ? (string)type.GetMethod("DevelopmentVehicleGeometry").Invoke(plugin,new object[] {outputDirectory}) : (string)type.GetMethod("DevelopmentSharedVehicle").Invoke(plugin,new object[] { action });
                        Emit("vehicle_native_fixture","action",action,"result",result,"evidence","CONTROLLED NATIVE FIXTURE");
                    }
                    else if (request.StartsWith("mb-loot-", StringComparison.Ordinal))
                    {
                        string action = request.Substring("mb-loot-".Length);
                        string result = (string)type.GetMethod("DevelopmentLootInventory").Invoke(plugin, new object[] { action });
                        if (action == "probe") File.WriteAllText(Path.Combine(outputDirectory, "me-target.txt"), result);
                        if (action == "catalog") File.WriteAllText(Path.Combine(outputDirectory, "me-catalog.tsv"), result);
                        Emit("me_loot_fixture", "action", action, "target", action == "catalog" ? "catalog-written" : result, "evidence", "DEVELOPMENT ORIGINAL INVENTORY FIXTURE");
                    }
                    else if (request.StartsWith("mb-world-", StringComparison.Ordinal))
                    {
                        string action = request.Substring("mb-world-".Length);
                        string target = (string)type.GetMethod("DevelopmentWorldInteraction").Invoke(plugin, new object[] { action });
                        if (action == "probe") File.WriteAllText(Path.Combine(outputDirectory, "md-target.txt"), target);
                        Emit("md_world_fixture", "action", action, "target", target, "evidence", "DEVELOPMENT ORIGINAL INTERACTION FIXTURE");
                    }
                    else if (request == "mb-host") network.GetType().GetMethod("Host").Invoke(network, new object[] { 9050, "MilestoneBGame" });
                    else if (request == "mb-join") network.GetType().GetMethod("Join").Invoke(network, new object[] { "127.0.0.1", 9050, "MilestoneCGame" });
                    else if (request == "mb-disconnect") network.GetType().GetMethod("Disconnect").Invoke(network, new object[] { "Development acceptance disconnect." });
                    else if (request == "mb-audit")
                    {
                        Action<string> log = message => Logger.LogInfo("[MilestoneB] " + message);
                        foreach (string owner in new[] { "local", "remote" })
                        {
                            object adapter = ReadB(plugin, owner);
                            adapter.GetType().GetMethod("Audit", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(adapter, new object[] { log });
                        }
                        ObserveB(true);
                    }
                    else Emit("mb_rejected", "reason", "unknown command");
                }
            }
            catch (Exception error) { StopInputFixture(); Emit("mb_error", "command", request, "error", error.ToString()); }
            return true;
        }

        private static object ReadB(object owner, string field)
        { return owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(owner); }

        private static Component GenuineFixturePlayer()
        {
            Type type = Type.GetType("ThiefSim2Multiplayer.Plugin, ThiefSim2Multiplayer", true);
            var plugin = Resources.FindObjectsOfTypeAll(type).OfType<Component>().Single();
            var player = ReadB(ReadB(plugin, "local"), "player") as Component;
            if (!player || !player.gameObject.activeInHierarchy || player.gameObject.scene.name != "MainScene") return null;
            var controller = player.GetComponent<CharacterController>();
            return controller && controller.enabled ? player : null;
        }

        private void StartInputFixture()
        {
            StopInputFixture();
            string file = Path.Combine(outputDirectory, "mb-input.txt");
            if (!File.Exists(file) || new FileInfo(file).Length > 256) throw new InvalidOperationException("Bounded input file missing.");
            string text = File.ReadAllText(file);
            File.Delete(file);
            MilestoneBInput input;
            if (!MilestoneBInput.TryParse(text, System.Diagnostics.Process.GetCurrentProcess().Id, out input))
                throw new InvalidOperationException("Input bounds/process identity/button rejected.");
            var player = GenuineFixturePlayer();
            if (!player || Time.timeScale <= 0) throw new InvalidOperationException("Active unpaused genuine player required.");
            fixturePlayer = ReadB(player, "rewiredPlayer");
            if (fixturePlayer == null) throw new InvalidOperationException("Original Rewired player unavailable.");
            fixtureForward = input.Forward; fixtureRight = input.Right; fixtureLookX = input.LookX; fixtureLookY = input.LookY;
            fixtureRun = input.Run; fixtureButton = input.Button; fixtureFrame = Time.frameCount + 1; fixtureReleaseFrame = -1;
            fixtureEnd = Time.realtimeSinceStartup + input.Seconds; inputOwner = this;
            inputFixture = new Harmony(Id + ".milestone-b-input");
            Type inputType = fixturePlayer.GetType();
            foreach (string method in new[] { "GetAxis", "GetAxisRaw", "GetButton", "GetButtonDown", "GetButtonUp" })
            {
                var target = inputType.GetMethod(method, new[] { typeof(string) });
                if (target == null) throw new InvalidOperationException("Original input getter missing: " + method);
                inputFixture.Patch(target, new HarmonyMethod(typeof(Plugin), method.StartsWith("GetAxis") ? nameof(FixtureAxis) : nameof(FixtureButton)));
            }
            Emit("mb_input_begin", "evidence", "DEVELOPMENT INPUT FIXTURE", "parameters", string.Join(" ", text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(7).ToArray()), "frame", fixtureFrame.ToString());
        }

        private static bool FixtureAxis(object __instance, string __0, ref float __result)
        {
            Plugin owner = inputOwner;
            if (!owner || !ReferenceEquals(__instance, owner.fixturePlayer) || Time.realtimeSinceStartup >= owner.fixtureEnd || Time.timeScale <= 0) return true;
            switch (__0)
            {
                case "forward_back": __result = owner.fixtureForward; return false;
                case "left_right": __result = owner.fixtureRight; return false;
                case "look_left_right": __result = owner.fixtureLookX; return false;
                case "look_up_down": __result = owner.fixtureLookY; return false;
                default: return true;
            }
        }
        private static bool FixtureButton(object __instance, string __0, MethodBase __originalMethod, ref bool __result)
        {
            Plugin owner = inputOwner;
            if (!owner || !ReferenceEquals(__instance, owner.fixturePlayer) || Time.timeScale <= 0) return true;
            if (__0 != "run" && __0 != owner.fixtureButton) return true;
            bool active = (__0 == "run" && owner.fixtureRun) || __0 == owner.fixtureButton;
            bool released = Time.realtimeSinceStartup >= owner.fixtureEnd;
            if (released && owner.fixtureReleaseFrame < 0) owner.fixtureReleaseFrame = Time.frameCount;
            __result = active && (__originalMethod.Name == "GetButtonUp" ? released && Time.frameCount == owner.fixtureReleaseFrame : !released && (__originalMethod.Name == "GetButton" || Time.frameCount == owner.fixtureFrame));
            return false;
        }
        private void StopInputFixture()
        {
            if (inputFixture != null) { inputFixture.UnpatchSelf(); inputFixture = null; Emit("mb_input_end", "evidence", "DEVELOPMENT INPUT FIXTURE"); }
            fixturePlayer = null; if (inputOwner == this) inputOwner = null;
        }
        private void UpdateB()
        {
            if (!milestoneBEnabled) return;
            if (inputFixture != null)
            {
                if (Time.timeScale <= 0 || (fixtureReleaseFrame >= 0 && Time.frameCount > fixtureReleaseFrame)) StopInputFixture();
                else if (Time.realtimeSinceStartup >= fixtureEnd && fixtureReleaseFrame < 0) fixtureReleaseFrame = Time.frameCount;
            }
            if (Time.realtimeSinceStartup < nextBObservation) return;
            nextBObservation = Time.realtimeSinceStartup + 0.1f;
            ObserveB(false);
        }
        private void ObserveB(bool audit)
        {
            try
            {
                var player = GenuineFixturePlayer();
                if (!player) return;
                GameObject actor = player.gameObject;
                var motor = actor.GetComponent(Type.GetType("PlayerMotorBehavior, Assembly-CSharp", true));
                var camera = actor.GetComponent(Type.GetType("PlayerCameraBehavior, Assembly-CSharp", true));
                var controller = actor.GetComponent<CharacterController>();
                if (!player || !motor || !camera || !controller) return;
                Emit("mb_sample", "evidence", inputFixture != null ? "DEVELOPMENT INPUT FIXTURE" : "NO DEVELOPMENT INPUT INJECTION",
                    "time", Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture), "velocity", controller.velocity.ToString("F6"),
                    "moving", Convert.ToString(ReadB(motor, "isMoving")), "running", Convert.ToString(ReadB(motor, "isRunning")),
                    "grounded", Convert.ToString(ReadB(motor, "isGrounded")), "crouching", Convert.ToString(ReadB(motor, "isCrouching")),
                    "yaw", actor.transform.eulerAngles.y.ToString("F4", CultureInfo.InvariantCulture), "pitch", Convert.ToString(ReadB(camera, "pitch"), CultureInfo.InvariantCulture),
                    "cameraPitch", ((Camera)ReadB(camera, "playerCamera")).transform.localEulerAngles.x.ToString("F4", CultureInfo.InvariantCulture),
                    "slot", Convert.ToString(ReadB(player, "currentItem")), "attacking", Convert.ToString(ReadB(player, "attacking")),
                    "motorWalk", Convert.ToString(ReadB(motor, "moveSpeed"), CultureInfo.InvariantCulture), "motorRun", Convert.ToString(ReadB(motor, "runSpeed"), CultureInfo.InvariantCulture),
                    "exhaustion", Convert.ToString(ReadB(motor, "exhaustedSpeed"), CultureInfo.InvariantCulture), "position", actor.transform.position.ToString("F4"));
                if (!audit) return;
                Emit("mb_identity", "player", player.GetInstanceID().ToString(), "actor", actor.GetInstanceID().ToString(), "controller", controller.GetInstanceID().ToString(),
                    "motor", motor.GetInstanceID().ToString(), "cameraBehavior", camera.GetInstanceID().ToString(),
                    "cameras", string.Join(",", actor.GetComponentsInChildren<Camera>(true).Select(c => c.GetInstanceID().ToString()).ToArray()),
                    "listeners", string.Join(",", actor.GetComponentsInChildren<AudioListener>(true).Select(c => c.GetInstanceID().ToString()).ToArray()),
                    "hands", FieldValue(ReadB(player, "R_Hand")), "crowbar", FieldValue(ReadB(player, "CrowbarObject")), "managedBytes", GC.GetTotalMemory(false).ToString());
                int roots = 0, avatars = 0, animations = 0, equipment = 0;
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (!go || !go.scene.IsValid()) continue;
                    if (go.name.StartsWith("TS2MP_RemotePlayer_", StringComparison.Ordinal)) roots++;
                    // Global counts also catch detached orphans after a root is destroyed.
                    if (go.name == "AvatarRig") { avatars++; animations += go.GetComponentsInChildren<Animation>(true).Length; }
                    if (go.name == "CrowbarVisual") equipment++;
                }
                int clips = Resources.FindObjectsOfTypeAll<AnimationClip>().Count(c => c && c.name.StartsWith("TS2MP_", StringComparison.Ordinal));
                Emit("mb_lifecycle", "roots", roots.ToString(), "avatars", avatars.ToString(), "animations", animations.ToString(), "equipment", equipment.ToString(), "privateClips", clips.ToString());
            }
            catch (Exception error) { Emit("mb_observation_error", "error", error.GetType().Name); }
        }
    }
}
