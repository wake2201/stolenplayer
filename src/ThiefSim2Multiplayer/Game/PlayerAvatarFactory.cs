using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class PlayerAvatarFactory
    {
        private readonly Dictionary<byte, Transform> sources = new Dictionary<byte, Transform>();
        internal AvatarVisual Create(byte id, Transform parent, float feetY)
        {
            var definition = AvatarDefinition.Find(id);
            if (definition == null) throw new InvalidOperationException("Unsupported AvatarId " + id);
            Transform source;
            if (!sources.TryGetValue(id, out source) || !source)
            {
                Type ai = Type.GetType("AI, Assembly-CSharp", true);
                FieldInfo key = ai.GetField("prefsHabitName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (key == null) throw new InvalidOperationException("Civilian identity field unavailable.");
                // The game's habit key is reused on other citizens. Source selection is internal metadata;
                // only AvatarId is transmitted, never a scene path or Unity instance ID.
                var matches = Resources.FindObjectsOfTypeAll(ai).OfType<Component>().Where(c => c && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded &&
                    c.name == "NPC" && c.transform.parent && c.transform.parent.name == definition.SourceGroup && (string)key.GetValue(c) == definition.SourceKey).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Civilian source " + definition.SourceKey + " count=" + matches.Length);
                source = matches[0].transform; sources[id] = source;
            }
            return CreateSource(source, parent, feetY, definition.SkinCount, false);
        }
        internal AvatarVisual CreateDriver(Transform source, Transform parent, Transform seat)
        {
            var visual = CreateSource(source, parent, 0, -1, true);
            visual.Root.transform.localPosition = seat.InverseTransformPoint(source.position);
            visual.Root.transform.localRotation = Quaternion.Inverse(seat.rotation) * source.rotation;
            visual.Root.transform.localScale = source.lossyScale;
            return visual;
        }
        private AvatarVisual CreateSource(Transform source, Transform parent, float feetY, int expectedSkins, bool driver)
        {
            var nodes = source.GetComponentsInChildren<Transform>(true);
            var skins = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var animation = source.GetComponentsInChildren<Animation>(true).FirstOrDefault(a => driver ? a.GetClip("driving_front") : a.GetClip("BreatheIdle") && a.GetClip("Walk") && a.GetClip("Running"));
            if (nodes.Length > 512 || skins.Length == 0 || skins.Length > 16 || expectedSkins >= 0 && skins.Length != expectedSkins || !animation || !driver && source.lossyScale != Vector3.one)
                throw new InvalidOperationException("Visual rig signature/animations differ: nodes=" + nodes.Length + "; skins=" + skins.Length + "; compatibleClip=" + (bool)animation);
            var visual = new AvatarVisual { Root = new GameObject("AvatarRig") };
            visual.Root.SetActive(false); visual.Root.transform.SetParent(parent, false);
            try
            {
                var map = new Dictionary<Transform, Transform>();
                foreach (var node in nodes)
                {
                    var copy = new GameObject(node.name).transform;
                    copy.SetParent(visual.Root.transform, false); map.Add(node, copy);
                }
                foreach (var node in nodes)
                {
                    var copy = map[node]; copy.SetParent(node == source ? visual.Root.transform : map[node.parent], false);
                    copy.localPosition = node == source ? Vector3.zero : node.localPosition;
                    copy.localRotation = node == source ? Quaternion.identity : node.localRotation;
                    copy.localScale = node == source ? Vector3.one : node.localScale;
                }
                visual.Skins = new SkinnedMeshRenderer[skins.Length];
                for (int i = 0; i < skins.Length; i++)
                {
                    var original = skins[i];
                    if (!original.sharedMesh || original.bones.Any(b => !b || !map.ContainsKey(b)) || !original.rootBone || !map.ContainsKey(original.rootBone)) throw new InvalidOperationException("Missing mesh or external skin binding: " + original.name);
                    var materials = original.sharedMaterials;
                    if (materials.Length < original.sharedMesh.subMeshCount || materials.Any(m => !m || !m.shader)) throw new InvalidOperationException("Invalid shared materials: " + original.name);
                    var copy = map[original.transform].gameObject.AddComponent<SkinnedMeshRenderer>();
                    copy.sharedMesh = original.sharedMesh; copy.sharedMaterials = materials; copy.bones = original.bones.Select(b => map[b]).ToArray(); copy.rootBone = map[original.rootBone];
                    copy.localBounds = original.localBounds; copy.updateWhenOffscreen = true; copy.enabled = true;
                    for (int j = 0; j < original.sharedMesh.blendShapeCount; j++) copy.SetBlendShapeWeight(j, original.GetBlendShapeWeight(j));
                    visual.Skins[i] = copy;
                }
                visual.Animation = new AvatarAnimationController(animation, map[animation.transform].gameObject, driver);
                visual.Rig = new AvatarRig(map[source], visual.Animation.Animation, map);
                visual.Rig.RemoveWorldMotion();
                // Initialization only: bake the sanitized idle pose for an accurate renderer-space sole minimum.
                float bottom = driver ? 0 : float.PositiveInfinity;
                if(!driver) foreach (var skin in visual.Skins)
                {
                    var mesh = new Mesh();
                    try
                    {
                        skin.BakeMesh(mesh);
                        if (mesh.vertexCount == 0) throw new InvalidOperationException("Idle skin bake empty.");
                        var matrix = visual.Root.transform.worldToLocalMatrix * skin.transform.localToWorldMatrix;
                        foreach (var vertex in mesh.vertices) bottom = Mathf.Min(bottom, matrix.MultiplyPoint3x4(vertex).y);
                    }
                    finally { UnityEngine.Object.Destroy(mesh); }
                }
                if (!float.IsNaN(bottom) && !float.IsInfinity(bottom)) visual.FeetOffset = driver ? 0 : feetY - bottom;
                else throw new InvalidOperationException("Feet calibration is not finite.");
                visual.Root.transform.localPosition = new Vector3(0, visual.FeetOffset, 0);
                AvatarVisual.Validate(visual.Root); visual.Root.SetActive(true); return visual;
            }
            catch { visual.Dispose(); throw; }
        }
        internal void Reset() { sources.Clear(); }
    }
}
