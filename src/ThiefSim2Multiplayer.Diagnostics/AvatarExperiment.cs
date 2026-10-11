using System.Collections;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThiefSim2Multiplayer.Diagnostics
{
    public sealed partial class Plugin
    {
        private GameObject testAvatar;
        private AnimationClip testIdleClip;
        private void RemoveTestAvatar()
        { if (testAvatar) { testAvatar.SetActive(false); Destroy(testAvatar); testAvatar = null; Emit("avatar_test_removed"); } if (testIdleClip) { Destroy(testIdleClip); testIdleClip=null; } }
        private IEnumerator TestAvatar()
        {
            if (testAvatar) { Emit("avatar_test_blocked", "reason", "one_test_already_exists"); yield break; }
            string selectionFile=Path.Combine(outputDirectory,"avatar-source.txt");
            string selected=File.Exists(selectionFile) && new FileInfo(selectionFile).Length<=1024 ? File.ReadAllText(selectionFile).Trim() : "";
            Transform source = null, player = null;
            var queue = new Queue<Transform>();
            for (int i=0; i<SceneManager.sceneCount; i++)
                foreach (GameObject root in SceneManager.GetSceneAt(i).GetRootGameObjects()) queue.Enqueue(root.transform);
            int visited=0;
            while (queue.Count!=0 && visited++<50000)
            {
                Transform node=queue.Dequeue(); if(!node) continue;
                if (PathOf(node)==selected) source=node;
                if (node.GetComponents<Component>().Any(c=>c && c.GetType().Name=="PlayerScript")) player=node;
                for(int i=0;i<node.childCount && queue.Count+visited<50000;i++) queue.Enqueue(node.GetChild(i));
                if (visited%256==0) yield return null;
            }
            if (!source || !player) { Emit("avatar_test_blocked", "reason", "selected_source_or_live_player_missing"); yield break; }
            var nodes=source.GetComponentsInChildren<Transform>(true);
            var skins=source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if(nodes.Length>512 || skins.Length==0 || skins.Length>24 || skins.Any(s=>!s.sharedMesh || s.bones.Any(b=>!b || !nodes.Contains(b))))
            { Emit("avatar_test_blocked", "reason", "invalid_or_external_skeleton_or_bounds"); yield break; }
            // Never instantiate the original object, even inactive: NPC lifecycle methods must not execute.
            // Only new Transforms and renderers are constructed; shared assets stay inside this process.
            testAvatar=new GameObject("TS2Recon_NetworkAvatar"); testAvatar.SetActive(false);
            var map=new Dictionary<Transform,Transform>();
            var visual=new GameObject("VisualRoot"); visual.transform.SetParent(testAvatar.transform,false);
            bool completed=false;
            try
            {
                foreach(Transform original in nodes)
                {
                    var copy=new GameObject(original.name).transform;
                    map.Add(original,copy);
                }
                foreach(Transform original in nodes)
                {
                    Transform copy=map[original];
                    copy.SetParent(original==source ? visual.transform : map[original.parent],false);
                    copy.localPosition=original==source ? Vector3.zero : original.localPosition;
                    copy.localRotation=original==source ? Quaternion.identity : original.localRotation;
                    copy.localScale=original==source ? original.lossyScale : original.localScale;
                    copy.gameObject.SetActive(original==source || original.gameObject.activeSelf);
                }
                foreach(var original in skins)
                {
                    var copy=map[original.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
                    copy.sharedMesh=original.sharedMesh; copy.sharedMaterials=original.sharedMaterials;
                    copy.bones=original.bones.Select(b=>map[b]).ToArray();
                    copy.rootBone=original.rootBone && map.ContainsKey(original.rootBone) ? map[original.rootBone] : null;
                    // Source NPC distance culling must not disable the independent network visual.
                    copy.localBounds=original.localBounds; copy.updateWhenOffscreen=true; copy.enabled=true;
                }
                Animation originalAnimation=source.GetComponentsInChildren<Animation>(true).FirstOrDefault(a=>a.GetClip("BreatheIdle"));
                Animation ownAnimation=null;
                if(originalAnimation)
                {
                    // Clone only an in-process clip; clear events on OUR copy and attach no original scripts.
                    testIdleClip=Instantiate(originalAnimation.GetClip("BreatheIdle"));
                    testIdleClip.name="TS2Recon_Idle"; testIdleClip.events=new AnimationEvent[0]; testIdleClip.wrapMode=WrapMode.Loop;
                    ownAnimation=map[originalAnimation.transform].gameObject.AddComponent<Animation>();
                    ownAnimation.AddClip(testIdleClip,"idle"); ownAnimation.wrapMode=WrapMode.Loop; ownAnimation.cullingType=AnimationCullingType.AlwaysAnimate;
                }
                testAvatar.transform.position=player.position+player.forward*3;
                testAvatar.transform.rotation=Quaternion.Euler(0,player.eulerAngles.y+180,0);
                testAvatar.SetActive(true);
                if(ownAnimation) ownAnimation.Play("idle");
                yield return null;
                var copies=testAvatar.GetComponentsInChildren<SkinnedMeshRenderer>();
                float bottom=copies.Min(s=>s.bounds.min.y);
                var cc=player.GetComponent<CharacterController>();
                float ground=cc ? player.TransformPoint(cc.center).y-cc.height*Mathf.Abs(player.lossyScale.y)*0.5f : player.position.y;
                RaycastHit hit;
                if(Physics.Raycast(testAvatar.transform.position+Vector3.up*2,Vector3.down,out hit,6,~0,QueryTriggerInteraction.Ignore)) ground=hit.point.y;
                visual.transform.position+=Vector3.up*(ground-bottom);
                Emit("avatar_test_spawned", "source", PathOf(source), "skins", copies.Length.ToString(), "nodes", nodes.Length.ToString(),
                    "position", testAvatar.transform.position.ToString("F3"), "ground", ground.ToString("F3"),
                    "strategy", "new_transforms_and_shared_skins_no_source_instantiation_no_animator_controller", "pose", ownAnimation ? "sanitized_legacy_idle" : "frozen_source_runtime_pose");
                yield return new WaitForSecondsRealtime(2);
                if(testAvatar)
                {
                    Component[] components=testAvatar.GetComponentsInChildren<Component>(true);
                    bool safe=components.All(c=>c is Transform || c is SkinnedMeshRenderer || c is Animation) && components.All(c=>c.gameObject.tag=="Untagged");
                    Emit("avatar_test_audit", "safe_components", safe.ToString(), "component_count", components.Length.ToString(),
                        "has_ai_navigation_audio_input_camera_save", (!safe).ToString(), "bounds_bottom", copies.Min(s=>s.bounds.min.y).ToString("F3"),
                        "network_position", testAvatar.transform.position.ToString("F3"), "animation", ownAnimation && ownAnimation.IsPlaying("idle") ? "sanitized_legacy_idle_playing" : "not_enabled",
                        "visible_renderers", copies.Count(s=>s.enabled && s.isVisible).ToString(), "registration", "no_game_scripts_or_game_tags_added");
                }
                completed=true;
            }
            finally { if (!completed) RemoveTestAvatar(); }
        }
    }
}
