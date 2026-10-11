using System;
using UnityEngine;
using ThiefSim2Multiplayer.Core;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class AvatarEquipmentController : IDisposable
    {
        private readonly EquipmentPresentationState state = new EquipmentPresentationState();
        private readonly Transform hand;
        private readonly AvatarDefinition definition;
        private readonly GameObject crowbarSource;
        private readonly Action<string> log;
        private GameObject visual;
        internal bool Exists { get { return visual; } }
        internal AvatarEquipmentController(AvatarDefinition definition, AvatarRig rig, GameObject crowbar, Action<string> log)
        { this.definition = definition; hand = rig.RightHand; crowbarSource = crowbar; this.log = log; }
        internal void Apply(PlayerReplicationState sample)
        {
            if (!state.Change(sample)) return;
            Remove();
            if (state.Item != 0) { log("[TS2MP] Equipment=None; visuals=0"); return; }
            if (!definition.CrowbarCalibrated) { log("[TS2MP] Equipment visual LIMITED: rig-specific crowbar calibration pending."); return; }
            try
            {
                if (!crowbarSource) throw new InvalidOperationException("Game-owned CrowbarObject unavailable.");
                var filters = crowbarSource.GetComponentsInChildren<MeshFilter>(true);
                if (filters.Length == 0 || filters.Length > 16) throw new InvalidOperationException("Crowbar renderer signature invalid.");
                visual = new GameObject("CrowbarVisual"); visual.SetActive(false); visual.transform.SetParent(hand, false);
                visual.transform.localPosition = definition.CrowbarPosition; visual.transform.localRotation = Quaternion.Euler(definition.CrowbarRotation);
                // Source-relative child matrices remove the root's scale. Preserve the installed visual's
                // actual world scale (0.385 in the verified build), then apply rig/item metadata correction.
                Vector3 sourceScale = crowbarSource.transform.lossyScale;
                if (sourceScale.x <= 0 || sourceScale.y <= 0 || sourceScale.z <= 0 || sourceScale.x > 4 || sourceScale.y > 4 || sourceScale.z > 4 ||
                    !NetworkingFinite(sourceScale)) throw new InvalidOperationException("Crowbar source scale invalid.");
                visual.transform.localScale = Vector3.Scale(definition.CrowbarScale, sourceScale);
                foreach (var original in filters)
                {
                    var renderer = original.GetComponent<MeshRenderer>();
                    if (!renderer || !original.sharedMesh || renderer.sharedMaterials.Length < original.sharedMesh.subMeshCount) throw new InvalidOperationException("Crowbar mesh/material missing.");
                    foreach (var material in renderer.sharedMaterials) if (!material || !material.shader) throw new InvalidOperationException("Crowbar material invalid.");
                    var copy = new GameObject(original.name); copy.transform.SetParent(visual.transform, false);
                    // Preserve the visual's source-relative transform; never copy a gameplay object or material instance.
                    Matrix4x4 m = crowbarSource.transform.worldToLocalMatrix * original.transform.localToWorldMatrix;
                    copy.transform.localPosition = m.GetColumn(3); copy.transform.localRotation = m.rotation; copy.transform.localScale = m.lossyScale;
                    copy.AddComponent<MeshFilter>().sharedMesh = original.sharedMesh; copy.AddComponent<MeshRenderer>().sharedMaterials = renderer.sharedMaterials;
                }
                AvatarVisual.Validate(visual); visual.SetActive(true);
                log("[TS2MP] Equipment=Crowbar; socket=mixamorig:RightHand; renderers=" + filters.Length + "; nativeScale=" + sourceScale.ToString("F4") + "; offset=" + definition.CrowbarPosition.ToString("F4") + "; rotation=" + definition.CrowbarRotation.ToString("F1"));
            }
            catch (Exception e) { Remove(); log("[TS2MP] Equipment visual unavailable: " + e.Message); }
        }
        private static bool NetworkingFinite(Vector3 value)
        { return Core.Networking.Protocol.Finite(value.x) && Core.Networking.Protocol.Finite(value.y) && Core.Networking.Protocol.Finite(value.z); }
        private void Remove() { if (visual) { visual.SetActive(false); visual.transform.SetParent(null, false); UnityEngine.Object.Destroy(visual); visual = null; } }
        public void Dispose() { Remove(); state.Reset(); }
    }
}
