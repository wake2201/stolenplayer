using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ThiefSim2Multiplayer.Diagnostics
{
    public sealed partial class Plugin
    {
        private static bool IsNpc(string name)
        { return name == "AI" || name == "SidewalkAI" || name == "GuardAI" || name == "PolicemanAI"; }

        private static bool MatchesTarget(string name, string target)
        {
            if (target == "all") return true;
            if (target == "avatars" || target == "ai") return IsNpc(name) || name == "SidewalkSpawner" || name == "Police_Manager" || name == "CarController";
            if (target == "player") return name.StartsWith("Player") || name == "HeadTilt" || name == "LegsController" || name == "GameManager";
            if (target == "interactions") return new[] { "Door", "WindowUse", "LockedWindow", "WindowOpenMaster", "Lockpick", "Pickupable", "Mission_Item", "SelectResidence", "EnterCar", "LightSwitch", "GateSwitch", "FuseBoxSwitch", "Garage_Switch", "UseComputer", "ParcelContainer", "Shop_Item" }.Contains(name);
            if (target == "inventory") return new[] { "Inventory", "ItemID", "PlayerScript", "CheckItemEnable", "VanHeavyItems", "Skill_Manager" }.Contains(name);
            if (target == "vehicles") return new[] { "CarController", "VehiclesManager", "EnterCar", "VanHeavyItems" }.Contains(name);
            return name.Contains("Manager") || new[]{"SaveGame","InitializePrefs","StoryScript","SelectResidence","SteamManager","DayNightTime","EnviroSky","Soundwave"}.Contains(name);
        }

        private void PollRequest()
        {
            try
            {
                string file = Path.Combine(outputDirectory, "request.txt");
                if (!File.Exists(file)) return;
                if (new FileInfo(file).Length > 128) { File.Delete(file); Emit("request_rejected", "reason", "length"); return; }
                string request = File.ReadAllText(file).Trim(); File.Delete(file);
                if (MilestoneBRequest(request)) return;
                if (request == "trace-on") { EnableActionTrace(); return; }
                if (request == "trace-off") { DisableActionTrace(); return; }
                if (request == "avatar-remove") { RemoveTestAvatar(); return; }
                if (request == "avatar-test") { StartCoroutine(TestAvatar()); return; }
                if (request == "pickup-test") { StartCoroutine(TestDroppedPickup()); return; }
                if (!new[] { "all", "player", "avatars", "interactions", "inventory", "ai", "vehicles", "managers" }.Contains(request))
                { Emit("request_rejected", "reason", "unknown_command"); return; }
                if (writer != null) writer.Dispose();
                string output=Path.Combine(outputDirectory,"recon-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+runtimeSession.Substring(0,8)+"-"+(++captureFile)+"-"+request+".ndjson");
                writer=new StreamWriter(output,false,new System.Text.UTF8Encoding(false)){AutoFlush=true};
                bytes=0; limitReported=false;
                Emit("capture_file", "session",runtimeSession,"target",request);
                StartCoroutine(Capture("request", request));
            }
            catch (Exception error) { Emit("request_error", "error", error.GetType().Name); }
        }

        private string FieldValue(object value)
        {
            if (value == null) return "null";
            var obj = value as UnityEngine.Object;
            if (obj != null)
            {
                var component = obj as Component; var go = obj as GameObject;
                return !obj ? "destroyed" : obj.GetInstanceID() + ":" + (component ? PathOf(component.transform) : go ? PathOf(go.transform) : obj.name);
            }
            Type type = value.GetType();
            var text=value as string;
            if(text!=null) return text.Length<=512 ? text : text.Substring(0,512)+"[truncated]";
            if (type.IsPrimitive || type.IsEnum || value is string || value is Vector3 || value is Vector2 || value is Quaternion)
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            var list = value as IList;
            if (list != null)
            {
                var parts = new List<string>();
                for (int i = 0; i < Math.Min(24, list.Count); i++)
                {
                    object entry = list[i];
                    parts.Add(entry is IList ? "nested_list" : FieldValue(entry));
                }
                return "count=" + list.Count + ";" + string.Join("|", parts.ToArray());
            }
            return type.FullName;
        }

        private void ReadStateFields(Component component)
        {
            int count = 0;
            foreach (FieldInfo field in component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(f=>new[] {"PS","Player","player","GM","agent","anim","animator","currentHour","currentDayInt","isOpen","locked","savePref","item_ID","currentItem","moneyAm","maxWeight","currentWeight","currentXPPoints","skillXP","item_IDs","item_IDs2","current_vehicle","GameTime","canInteract","isPickingUp","isCarrying","allowRotation","current_stars","NPC_who_called","last_known_location","spawned_policemen","spawned_cars","prefName","prefsHabitName","isMan","isWoman","isDog","canUse","opening","isWindow","npcUsing"}.Contains(f.Name)?0:1))
            {
                if (count++ >= 240) { Emit("state_fields_truncated", "path", PathOf(component.transform)); break; }
                // Field reads only: no arbitrary properties, methods or singleton getters.
                try {
                    object value=field.GetValue(component);
                    Emit("state_field", "scene", component.gameObject.scene.name, "instance_id", component.GetInstanceID().ToString(), "path", PathOf(component.transform), "type", component.GetType().FullName,
                        "field", field.Name, "value", FieldValue(value));
                    if(field.Name=="GameTime" && value!=null)
                        foreach(FieldInfo clock in value.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public).Take(32))
                            Emit("clock_field","path",PathOf(component.transform),"field",clock.Name,"value",FieldValue(clock.GetValue(value)));
                }
                catch (Exception error) { Emit("reflection_error", "field", field.Name, "error", error.GetType().Name); }
            }
        }

        private void InspectEngineComponent(Component component)
        {
            string path = PathOf(component.transform);
            var behaviour = component as Behaviour;
            if (behaviour) Emit("behaviour", "scene", component.gameObject.scene.name, "path", path,
                "instance_id", component.GetInstanceID().ToString(), "type", component.GetType().FullName,
                "enabled", behaviour.enabled.ToString(), "active_and_enabled", behaviour.isActiveAndEnabled.ToString());
            var transform = component as Transform;
            if (transform) Emit("transform", "scene", component.gameObject.scene.name, "path", path,
                "parent", transform.parent ? PathOf(transform.parent) : "none", "child_count", transform.childCount.ToString(),
                "position", transform.position.ToString("F4"), "rotation", transform.rotation.ToString("F4"),
                "local_position", transform.localPosition.ToString("F4"), "local_rotation", transform.localRotation.ToString("F4"),
                "local_scale", transform.localScale.ToString("F4"), "world_scale", transform.lossyScale.ToString("F4"));
            var camera = component as Camera;
            if (camera) Emit("camera", "scene", component.gameObject.scene.name, "path", path,
                "mask", camera.cullingMask.ToString(), "field_of_view", camera.fieldOfView.ToString(),
                "near", camera.nearClipPlane.ToString(), "far", camera.farClipPlane.ToString(), "depth", camera.depth.ToString());
            var audio = component as AudioSource;
            if (audio) Emit("audio_source", "scene", component.gameObject.scene.name, "path", path,
                "clip", audio.clip ? audio.clip.name : "none", "playing", audio.isPlaying.ToString(),
                "volume", audio.volume.ToString(), "spatial_blend", audio.spatialBlend.ToString(), "loop", audio.loop.ToString());
            var controller = component as CharacterController;
            if (controller) Emit("character_controller", "path", path, "center", controller.center.ToString("F4"),
                "height", controller.height.ToString(), "radius", controller.radius.ToString(), "skin_width", controller.skinWidth.ToString(),
                "step_offset", controller.stepOffset.ToString(), "slope_limit", controller.slopeLimit.ToString(), "grounded", controller.isGrounded.ToString(), "enabled", controller.enabled.ToString());
            var collider = component as Collider;
            if (collider) Emit("collider", "path", path, "type", component.GetType().FullName, "enabled", collider.enabled.ToString(),
                "trigger", collider.isTrigger.ToString(), "bounds_center", collider.bounds.center.ToString("F3"), "bounds_size", collider.bounds.size.ToString("F3"));
            var rb = component as Rigidbody;
            if (rb) Emit("rigidbody", "path", path, "mass", rb.mass.ToString(), "kinematic", rb.isKinematic.ToString(),
                "gravity", rb.useGravity.ToString(), "constraints", rb.constraints.ToString(), "velocity", rb.velocity.ToString("F3"));
            var skinned = component as SkinnedMeshRenderer;
            if (skinned) InspectSkin(skinned);
            var renderer = component as Renderer;
            if (renderer)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                Emit("renderer", "path", path, "type", renderer.GetType().FullName, "enabled", renderer.enabled.ToString(),
                    "mesh", filter && filter.sharedMesh ? filter.sharedMesh.name : "", "materials",
                    string.Join("|", renderer.sharedMaterials.Take(16).Select(m => m ? m.name + ":" + (m.shader ? m.shader.name : "") : "null").ToArray()),
                    "bounds_center", renderer.bounds.center.ToString("F3"), "bounds_size", renderer.bounds.size.ToString("F3"));
            }
            var animator = component as Animator;
            if (animator) InspectAnimator(animator);
            var animation = component as Animation;
            if (animation)
            {
                int clips = 0;
                foreach (AnimationState state in animation)
                { if (clips++ >= 160) break; InspectClip(state.clip, path, "legacy", state.name);
                    Emit("legacy_state","path",path,"state",state.name,"wrap_mode",state.wrapMode.ToString(),"animation_wrap_mode",animation.wrapMode.ToString(),"enabled",state.enabled.ToString(),"weight",state.weight.ToString(),"time",state.time.ToString()); }
            }
            string concrete=component.GetType().FullName;
            if(concrete=="Pathfinding.RichAI" || concrete=="UnityEngine.AI.NavMeshAgent")
                foreach(string property in new[]{"destination","velocity","isStopped","remainingDistance","pathPending","reachedDestination","maxSpeed"})
                    try { var info=component.GetType().GetProperty(property,BindingFlags.Public|BindingFlags.Instance);
                        if(info!=null && info.CanRead) Emit("navigation_property","path",path,"type",concrete,"property",property,"value",FieldValue(info.GetValue(component,null))); }
                    catch(Exception error) { Emit("navigation_read_error","path",path,"property",property,"error",error.GetType().Name); }
        }

        private void InspectSkin(SkinnedMeshRenderer skin)
        {
            Emit("skin", "path", PathOf(skin.transform), "mesh", skin.sharedMesh ? skin.sharedMesh.name : "null",
                "vertices", skin.sharedMesh ? skin.sharedMesh.vertexCount.ToString() : "0", "bone_count", skin.bones.Length.ToString(),
                "root_bone", skin.rootBone ? PathOf(skin.rootBone) : "null", "local_bounds_center", skin.localBounds.center.ToString("F3"),
                "local_bounds_size", skin.localBounds.size.ToString("F3"), "enabled", skin.enabled.ToString());
            int index = 0;
            foreach (Transform bone in skin.bones.Take(256))
                Emit("bone", "skin", PathOf(skin.transform), "index", (index++).ToString(), "name", bone ? bone.name : "null",
                    "path", bone ? PathOf(bone) : "null", "local_position", bone ? bone.localPosition.ToString("F4") : "",
                    "world_position", bone ? bone.position.ToString("F4") : "");
        }

        private void InspectClip(AnimationClip clip, string path, string controller, string state)
        {
            if (!clip) return;
            Emit("clip", "path", path, "controller", controller, "state", state, "name", clip.name,
                "instance_id", clip.GetInstanceID().ToString(), "length", clip.length.ToString(), "frame_rate", clip.frameRate.ToString(),
                "loop", clip.isLooping.ToString(), "human_motion", clip.humanMotion.ToString(), "legacy", clip.legacy.ToString(),
                "events", clip.events.Length.ToString());
        }

        private void InspectAnimator(Animator animator)
        {
            string path = PathOf(animator.transform);
            var controller = animator.runtimeAnimatorController;
            Emit("animator", "path", path, "enabled", animator.enabled.ToString(), "initialized", animator.isInitialized.ToString(),
                "human", animator.isHuman.ToString(), "avatar", animator.avatar ? animator.avatar.name : "null",
                "avatar_valid", animator.avatar ? animator.avatar.isValid.ToString() : "false", "avatar_human", animator.avatar ? animator.avatar.isHuman.ToString() : "false",
                "controller", controller ? controller.name : "null", "controller_type", controller ? controller.GetType().FullName : "",
                "apply_root_motion", animator.applyRootMotion.ToString(), "has_root_motion", animator.hasRootMotion.ToString(),
                "culling", animator.cullingMode.ToString(), "update_mode", animator.updateMode.ToString(), "scale", animator.transform.lossyScale.ToString("F3"));
            if (controller)
                foreach (AnimationClip clip in controller.animationClips.Distinct().Take(160)) InspectClip(clip, path, controller.name, "controller_clip_state_mapping_unknown");
            if (!animator.isInitialized) return;
            foreach (AnimatorControllerParameter parameter in animator.parameters.Take(80))
            {
                string value = parameter.type == AnimatorControllerParameterType.Float ? animator.GetFloat(parameter.nameHash).ToString() :
                    parameter.type == AnimatorControllerParameterType.Int ? animator.GetInteger(parameter.nameHash).ToString() :
                    parameter.type == AnimatorControllerParameterType.Bool ? animator.GetBool(parameter.nameHash).ToString() : "trigger_not_read";
                Emit("animator_parameter", "path", path, "name", parameter.name, "type", parameter.type.ToString(), "value", value);
            }
            for (int layer = 0; layer < Math.Min(8, animator.layerCount); layer++)
            {
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
                Emit("animator_state", "path", path, "layer", animator.GetLayerName(layer), "full_path_hash", state.fullPathHash.ToString(),
                    "short_hash", state.shortNameHash.ToString(), "normalized_time", state.normalizedTime.ToString(), "loop", state.loop.ToString());
                foreach (AnimatorClipInfo clip in animator.GetCurrentAnimatorClipInfo(layer).Take(16))
                    Emit("animator_current_clip", "path", path, "layer", layer.ToString(), "name", clip.clip ? clip.clip.name : "null", "weight", clip.weight.ToString());
            }
            if (animator.isHuman && animator.avatar && animator.avatar.isValid)
                foreach (HumanBodyBones bone in new[] { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                { Transform transform = animator.GetBoneTransform(bone); Emit("human_bone", "path", path, "slot", bone.ToString(), "bone", transform ? PathOf(transform) : "null"); }
        }

        private void InspectVisualCandidate(Transform root, string category)
        {
            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var animators = root.GetComponentsInChildren<Animator>(true);
            Emit("avatar_candidate", "category", category, "path", PathOf(root), "scene", root.gameObject.scene.name,
                "active", root.gameObject.activeInHierarchy.ToString(), "position", root.position.ToString("F3"), "forward", root.forward.ToString("F3"),
                "scale", root.lossyScale.ToString("F3"), "skins", skins.Length.ToString(), "animators", animators.Length.ToString());
            var queue = new Queue<Transform>(); queue.Enqueue(root); int count = 0;
            while (queue.Count != 0 && count++ < 512)
            {
                Transform node = queue.Dequeue(); if (!node) continue;
                var components = node.GetComponents<Component>();
                Emit("candidate_node", "candidate", PathOf(root), "path", PathOf(node), "active", node.gameObject.activeSelf.ToString(),
                    "local_position", node.localPosition.ToString("F4"), "local_rotation", node.localEulerAngles.ToString("F3"), "scale", node.localScale.ToString("F3"),
                    "components", string.Join(",", components.Where(c => c).Select(c => c.GetType().FullName).ToArray()));
                foreach (Component component in components.Where(c => c)) InspectEngineComponent(component);
                for (int i=0; i<node.childCount && queue.Count+count<512; i++) queue.Enqueue(node.GetChild(i));
            }
            Emit("candidate_end", "path", PathOf(root), "visited", count.ToString(), "truncated", (queue.Count!=0 || root.GetComponentsInChildren<Transform>(true).Length>512).ToString());
        }
    }
}
