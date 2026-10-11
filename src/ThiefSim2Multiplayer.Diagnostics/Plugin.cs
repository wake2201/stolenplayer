using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThiefSim2Multiplayer.Diagnostics
{
    [BepInPlugin(Id, "ThiefSim2Multiplayer.Diagnostics", "0.1.0")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.thiefsim2.multiplayer.diagnostics";
        private StreamWriter writer;
        private ConfigEntry<int> objectLimit;
        private ConfigEntry<int> recordLimit;
        private bool capturing;
        private bool pendingCapture;
        private bool loggingFailed;
        private long bytes;
        private const long MaxBytes = 32 * 1024 * 1024;
        private string outputDirectory;
        private float nextRequestPoll;
        private readonly string runtimeSession = Guid.NewGuid().ToString("N");
        private int captureFile;
        private bool limitReported;
        private readonly HashSet<string> describedTypes = new HashSet<string>();
        private readonly HashSet<string> interestingTypes = new HashSet<string>(new[] {
            "PlayerScript", "PlayerMotorBehavior", "PlayerCameraBehavior", "GameManager", "Inventory",
            "Pickupable", "ItemID", "Door", "WindowUse", "LootBox", "AI", "NPCHabits", "Police_Manager",
            "PolicemanAI", "GuardAI", "SidewalkAI", "SidewalkSpawner", "CarController", "EnterCar", "VehiclesManager",
            "VanHeavyItems", "SaveGame", "Skill_Manager", "StoryEvents", "StoryManager", "StoryScript", "Mission_Item",
            "Mission_Manager", "SelectResidence", "LockedWindow", "WindowOpenMaster", "Lockpick", "ItemSpawner",
            "SteamManager", "InitializePrefs", "HeadTilt", "LegsController", "CheckItemEnable", "Sensor_Range", "DayNightTime", "EnviroSky",
            "LightSwitch", "GateSwitch", "FuseBoxSwitch", "Garage_Switch", "UseComputer", "ParcelContainer", "Soundwave", "Shop_Item", "Shop_Manager"
        });

        private void Awake()
        {
            milestoneBEnabled = Config.Bind("MilestoneB", "Enabled", false, "Explicit bounded development input and acceptance observations; disable for normal play.").Value;
            objectLimit = Config.Bind("Capture", "ObjectLimit", 12000, "Maximum scene objects visited per snapshot.");
            recordLimit = Config.Bind("Capture", "RecordLimit", 300, "Maximum filtered component records per snapshot.");
            string directory = Config.Bind("Capture", "OutputDirectory",
                Path.Combine(Paths.BepInExRootPath, "runtime-dumps"), "Local NDJSON directory; no save data is read.").Value;
            outputDirectory = directory;
            try
            {
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, "recon-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ndjson");
                writer = new StreamWriter(file, false, new UTF8Encoding(false)) { AutoFlush = true };
                Logger.LogInfo("Diagnostics loaded; capture output: " + file + "; F9 takes another snapshot.");
            }
            catch (Exception error) { Logger.LogError("Cannot create diagnostic log: " + error.Message); }
            Emit("plugin_loaded", "unity", Application.unityVersion, "scene", SceneManager.GetActiveScene().name);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName))
                Emit("assembly", "identity", assembly.FullName);
            DescribeGameTypes();
            InspectHarmony();
            if (Config.Bind("Trace", "Enabled", false, "Bounded read-only action/save traces; never changes method results.").Value) EnableActionTrace();
            if (Config.Bind("Verification", "Enabled", false, "Opt-in temporary harmless detour and loopback dependency test.").Value)
                StartCoroutine(VerifyRuntime());
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
            SceneManager.activeSceneChanged += ActiveSceneChanged;
            StartCoroutine(DelayedCapture("startup", 3));
            StartCoroutine(DelayedCapture("startup_late", 20));
        }

        private void Update()
        {
            UpdateB();
            if (Input.GetKeyDown(KeyCode.F9) && !capturing) StartCoroutine(Capture("F9"));
            if (Time.unscaledTime >= nextRequestPoll && !capturing)
            {
                nextRequestPoll = Time.unscaledTime + 1;
                PollRequest();
            }
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StopInputFixture();
            Emit("scene_loaded", "scene", scene.name, "path", scene.path, "mode", mode.ToString());
            StartCoroutine(DelayedCapture("scene_loaded", 3));
            StartCoroutine(DelayedCapture("scene_loaded_late", 20));
        }
        private void SceneUnloaded(Scene scene) { StopInputFixture(); Emit("scene_unloaded", "scene", scene.name); }
        private void ActiveSceneChanged(Scene oldScene, Scene newScene)
        { Emit("scene_changed", "old", oldScene.name, "new", newScene.name); }
        private IEnumerator DelayedCapture(string reason, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (capturing) { pendingCapture = true; yield break; }
            yield return Capture(reason, "managers");
        }

        private IEnumerator Capture(string reason, string target = "all")
        {
            capturing = true;
            try
            {
                int visited = 0, records = 0, rootRecords = 0;
                bool traversalTruncated = false;
                int maxObjects = Math.Max(100, Math.Min(50000, objectLimit.Value));
                if (target != "managers") maxObjects=50000;
                int maxRecords = target == "inventory" ? 1000 : Math.Max(20, Math.Min(1000, recordLimit.Value));
                string scopeFile=Path.Combine(outputDirectory,"scope-path.txt");
                string scope=File.Exists(scopeFile) && new FileInfo(scopeFile).Length<=1024 ? File.ReadAllText(scopeFile).Trim() : "";
                var queue = new Queue<Transform>();
                Emit("snapshot_begin", "reason", reason, "target", target, "active_scene", SceneManager.GetActiveScene().name,
                    "frame", Time.frameCount.ToString(), "time_scale", Time.timeScale.ToString(),"scope_root",scope);
                // Prioritize the actual known player/manager tags before large authored scene trees.
                foreach (string tag in new[] { "Player", "GameManager", "Inventory" })
                {
                    GameObject tagged = null;
                    try { tagged = GameObject.FindGameObjectWithTag(tag); }
                    catch (UnityException) { }
                    if (tagged && (scope.Length==0 || PathOf(tagged.transform).StartsWith(scope+"/",StringComparison.Ordinal))) queue.Enqueue(tagged.transform);
                }
                if (target == "avatars" || target == "ai")
                {
                    try { foreach (GameObject npc in GameObject.FindGameObjectsWithTag("NPC").Where(n=>scope.Length==0 || PathOf(n.transform).StartsWith(scope+"/",StringComparison.Ordinal)).Take(24)) queue.Enqueue(npc.transform); }
                    catch (UnityException) { }
                }
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    Emit("scene", "name", scene.name, "path", scene.path, "handle", scene.handle.ToString(), "root_count", scene.rootCount.ToString());
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        Transform captureRoot = root.transform;
                        if (scope.Length != 0 && PathOf(captureRoot) != scope)
                        {
                            if (!scope.StartsWith(PathOf(captureRoot) + "/", StringComparison.Ordinal)) continue;
                            captureRoot = root.GetComponentsInChildren<Transform>(true).Take(50000).FirstOrDefault(t => PathOf(t) == scope);
                            if (!captureRoot) continue;
                        }
                        queue.Enqueue(captureRoot);
                        if (rootRecords++ < 80) Emit("root", "scene", scene.name, "path", PathOf(captureRoot));
                    }
                }
                // An explicit player reference also discovers a player in DontDestroyOnLoad.
                if(scope.Length==0)
                {
                    foreach (Camera camera in Camera.allCameras.Take(12))
                        if (camera) queue.Enqueue(camera.transform.root);
                    AddSingletonRoots(queue);
                }
                var seen = new HashSet<int>();
                var frame = Stopwatch.StartNew();
                while (queue.Count != 0 && visited < maxObjects && records < maxRecords)
                {
                    Transform transform = queue.Dequeue();
                    if (!transform || !seen.Add(transform.GetInstanceID())) continue;
                    visited++;
                    Component[] components = transform.GetComponents<Component>();
                    bool player = components.Any(c => c && c.GetType().Name == "PlayerScript");
                    if (components.Any(c=>c && interestingTypes.Contains(c.GetType().Name) && MatchesTarget(c.GetType().Name,target)))
                    {
                        Emit("object_layout", "path", PathOf(transform), "scene",transform.gameObject.scene.name,"instance_id",transform.gameObject.GetInstanceID().ToString(),
                            "tag",transform.gameObject.tag,"layer",transform.gameObject.layer.ToString(),"active",transform.gameObject.activeInHierarchy.ToString(),
                            "components",string.Join(",",components.Where(c=>c).Select(c=>c.GetType().FullName).ToArray()));
                        foreach(Component attached in components.Where(c=>c))
                        {
                            InspectEngineComponent(attached);
                            string name=attached.GetType().FullName;
                            if(name.StartsWith("Pathfinding.") || name.StartsWith("EVP.") || name=="UnityEngine.AI.NavMeshAgent") ReadStateFields(attached);
                        }
                    }
                    foreach (Component component in components)
                    {
                        if (!component || records >= maxRecords) continue;
                        Type type = component.GetType();
                        if (!interestingTypes.Contains(type.Name) || !MatchesTarget(type.Name, target)) continue;
                        Emit("component", "scene", component.gameObject.scene.name, "path", PathOf(transform),
                            "type", type.FullName, "instance_id", component.GetInstanceID().ToString(),
                            "active", component.gameObject.activeInHierarchy.ToString(), "position", transform.position.ToString("F3"));
                        DescribeType(type);
                        InspectFields(component);
                        ReadStateFields(component);
                        InspectEngineComponent(component);
                        if (IsNpc(type.Name)) InspectVisualCandidate(component.transform, type.Name);
                        if (type.Name == "CarController") InspectVehicleVisual(component);
                        records++;
                    }
                    if ((target == "avatars") && !components.Any(c => c && IsNpc(c.GetType().Name)) && components.Any(c => c is Animator)
                        && transform.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length != 0)
                        InspectVisualCandidate(transform, "animator_visual");
                    if (player && (target == "all" || target == "player" || target == "avatars" || target == "inventory"))
                    { Emit("player_candidate", "path", PathOf(transform)); PlayerHierarchy(transform); }
                    // Cap queue growth; traversal includes inactive objects but has no global resource scan.
                    int available = maxObjects - visited - queue.Count;
                    if (transform.childCount > Math.Max(0, available)) traversalTruncated = true;
                    for (int i = 0; i < transform.childCount && i < available; i++) queue.Enqueue(transform.GetChild(i));
                    if (frame.ElapsedMilliseconds >= 4) { yield return null; frame.Restart(); }
                }
                Emit("snapshot_end", "visited", visited.ToString(), "records", records.ToString(),
                    "truncated", (queue.Count != 0 || traversalTruncated || rootRecords > 80).ToString());
                Logger.LogInfo("Diagnostics snapshot completed: " + reason + ", " + visited + " objects, " + records + " filtered records.");
            }
            finally { capturing = false; }
            if (pendingCapture) { pendingCapture = false; StartCoroutine(DelayedCapture("queued_scene_capture", 1)); }
        }

        private void PlayerHierarchy(Transform root)
        {
            var queue = new Queue<Transform>(); queue.Enqueue(root);
            int count = 0;
            bool truncated = false;
            while (queue.Count != 0 && count < 4096)
            {
                count++;
                Transform node = queue.Dequeue(); if (!node) continue;
                Component[] components = node.GetComponents<Component>();
                Emit("player_hierarchy", "path", PathOf(node), "active", node.gameObject.activeSelf.ToString(),
                    "position", node.position.ToString("F3"), "yaw", node.eulerAngles.y.ToString("F3"),
                    "local_position", node.localPosition.ToString("F3"), "local_rotation", node.localEulerAngles.ToString("F3"),
                    "scale", node.lossyScale.ToString("F3"), "tag", node.gameObject.tag, "layer", node.gameObject.layer.ToString(),
                    "components", string.Join(",", components.Where(c => c).Select(c => c.GetType().FullName).ToArray()));
                foreach (Component component in components.Where(c => c)) InspectEngineComponent(component);
                foreach (Renderer renderer in components.OfType<Renderer>())
                {
                    var skinned = renderer as SkinnedMeshRenderer;
                    Emit("player_renderer", "path", PathOf(node), "type", renderer.GetType().Name,
                        "enabled", renderer.enabled.ToString(), "mesh", skinned && skinned.sharedMesh ? skinned.sharedMesh.name : "not_skinned");
                }
                foreach (Camera camera in components.OfType<Camera>())
                    Emit("player_camera", "path", PathOf(node), "enabled", camera.enabled.ToString(), "mask", camera.cullingMask.ToString(), "local_pitch", node.localEulerAngles.x.ToString("F3"));
                if (node.childCount > Math.Max(0, 4096 - count - queue.Count)) truncated = true;
                for (int i = 0; i < node.childCount && queue.Count + count < 4096; i++) queue.Enqueue(node.GetChild(i));
            }
            Emit("player_hierarchy_end", "path", PathOf(root), "count", count.ToString(), "truncated", (truncated || queue.Count != 0).ToString());
        }

        private void InspectVehicleVisual(Component vehicle)
        {
            FieldInfo field = vehicle.GetType().GetField("tpp_character", BindingFlags.Instance | BindingFlags.Public);
            if (field == null) return;
            try
            {
                var visual = field.GetValue(vehicle) as GameObject;
                if (!visual) return;
                Emit("vehicle_visual_candidate", "owner", PathOf(vehicle.transform), "path", PathOf(visual.transform));
                InspectVisualCandidate(visual.transform, "vehicle_tpp");
                PlayerHierarchy(visual.transform);
            }
            catch (Exception error) { Emit("reflection_error", "field", "tpp_character", "error", error.GetType().Name); }
        }

        private void AddSingletonRoots(Queue<Transform> queue)
        {
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (game == null) return;
            // The game's lazy getter creates this exact name. Find the existing object only;
            // never call that getter or trigger Steam initialization from reconnaissance.
            GameObject steam=GameObject.Find("SteamManager");
            if(steam) { Emit("existing_steam_object","path",PathOf(steam.transform),"scene",steam.scene.name); queue.Enqueue(steam.transform.root); }
            foreach (string name in interestingTypes.Concat(new[] { "SteamManager" }))
            {
                Type type = game.GetType(name, false); if (type == null) continue;
                foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!(field.Name == "instance" || field.Name == "Instance" || field.Name == "s_instance")) continue;
                    // Do not trigger static constructors or invoke lazy singleton property getters.
                    if (type.TypeInitializer != null) { Emit("singleton_skipped", "type", name, "reason", "static_constructor"); continue; }
                    try
                    {
                        var component = field.GetValue(null) as Component;
                        if (!component) continue;
                        Emit("singleton", "type", name, "field", field.Name, "path", PathOf(component.transform));
                        queue.Enqueue(component.transform.root);
                    }
                    catch (Exception error) { Emit("reflection_error", "type", name, "error", error.GetType().Name); }
                }
            }
        }

        private void InspectFields(Component component)
        {
            foreach (FieldInfo field in component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!(field.Name == "Player" || field.Name == "CC" || field.Name == "PMB" || field.Name == "PCB" ||
                    field.Name == "playerCamera" || field.Name == "headTransform" || field.Name == "item_ID" ||
                    field.Name == "savePref" || field.Name == "isOpen" || field.Name == "doorPref" || field.Name == "itemPref")) continue;
                try
                {
                    object value = field.GetValue(component);
                    var go = value as GameObject; var other = value as Component;
                    string text = go ? PathOf(go.transform) : other ? PathOf(other.transform) :
                        value == null ? "null" : field.FieldType.IsPrimitive || field.FieldType == typeof(string) ? Convert.ToString(value) : field.FieldType.FullName;
                    Emit("field", "path", PathOf(component.transform), "type", component.GetType().FullName, "field", field.Name, "value", text);
                }
                catch (Exception error) { Emit("reflection_error", "field", field.Name, "error", error.GetType().Name); }
            }
        }
        private void DescribeGameTypes()
        {
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (game == null) { Emit("game_assembly_missing"); return; }
            foreach (string name in interestingTypes)
            {
                Type type = game.GetType(name, false); if (type != null) DescribeType(type);
            }
        }
        private void DescribeType(Type type)
        {
            if (!describedTypes.Add(type.FullName)) return;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            Emit("type", "name", type.FullName, "base", type.BaseType == null ? "" : type.BaseType.FullName);
            foreach (FieldInfo field in type.GetFields(flags).Take(400)) Emit("field_signature", "type", type.FullName, "signature", field.ToString());
            // Metadata only: calling arbitrary getters can execute gameplay or instantiate managers.
            foreach (PropertyInfo property in type.GetProperties(flags).Take(100)) Emit("property_signature", "type", type.FullName, "signature", property.ToString());
            foreach (MethodInfo method in type.GetMethods(flags).Take(400)) Emit("method_signature", "type", type.FullName, "signature", method.ToString());
        }

        private void InspectHarmony()
        {
            Emit("harmony", "assembly", typeof(Harmony).Assembly.FullName, "action", "inspection_only_no_patches");
            Assembly game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (game == null) return;
            foreach (string target in new[] { "PlayerScript:Start", "PlayerMotorBehavior:CalculateMovement", "Door:Use",
                "Pickupable:PickUp", "ItemID:Item_Picked", "GameManager:Awake", "GameManager:LoadNewSceneAsync" })
            {
                string[] split = target.Split(':'); Type type = game.GetType(split[0], false); if (type == null) continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == split[1]))
                {
                    Patches patches = Harmony.GetPatchInfo(method);
                    Emit("hook_candidate", "type", type.FullName, "signature", method.ToString(),
                        "existing_patch_owners", patches == null ? "" : string.Join(",", patches.Owners.ToArray()));
                }
            }
        }
        private static string PathOf(Transform transform)
        {
            var parts = new List<string>();
            while (transform && parts.Count < 64)
            { parts.Add(transform.name + "[" + transform.GetSiblingIndex() + "]"); transform = transform.parent; }
            parts.Reverse(); return string.Join("/", parts.ToArray());
        }
        private static string Quote(string value)
        {
            if (value == null) return "null";
            if (value.Length > 2048) value = value.Substring(0, 2048);
            var text = new StringBuilder("\"");
            foreach (char c in value)
            {
                if (c == '\\' || c == '"') text.Append('\\').Append(c);
                else if (c < 32) text.Append("\\u").Append(((int)c).ToString("x4"));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }
        private void Emit(string eventName, params string[] pairs)
        {
            if (writer == null) return;
            if (bytes >= MaxBytes)
            { if (!limitReported) Logger.LogWarning("Diagnostic file reached its explicit byte cap; request a new targeted capture."); limitReported=true; return; }
            var line = new StringBuilder("{\"utc\":").Append(Quote(DateTime.UtcNow.ToString("o")))
                .Append(",\"event\":").Append(Quote(eventName));
            for (int i = 0; i + 1 < pairs.Length; i += 2) line.Append(',').Append(Quote(pairs[i])).Append(':').Append(Quote(pairs[i + 1]));
            string record = line.Append('}').ToString();
            try { writer.WriteLine(record); bytes += Encoding.UTF8.GetByteCount(record) + 2; }
            catch (Exception error) { if (!loggingFailed) Logger.LogError("Diagnostic write failed: " + error.Message); loggingFailed = true; bytes = MaxBytes; }
        }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded;
            SceneManager.activeSceneChanged -= ActiveSceneChanged;
            StopAllCoroutines();
            RemoveTestAvatar();
            DisableActionTrace();
            StopInputFixture();
            Emit("plugin_unloaded");
            if (writer != null) { try { writer.Dispose(); } catch { } writer = null; }
        }
    }
}
