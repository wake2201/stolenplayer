using System;
using System.Collections.Generic;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class RemotePlayerProxy : IDisposable
    {
        private GameObject root;
        private Transform visualRoot;
        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly List<MeshRenderer> fallbackRenderers = new List<MeshRenderer>();
        private Material markerMaterial;
        private PlayerAvatarController avatar;
        private readonly VehiclePresentation vehicle = new VehiclePresentation();
        private float visualHeight, standingFeet;
        private float visualYaw;
        internal bool DriverVisible => vehicle.Visible;
        private readonly PlayerAvatarFactory factory = new PlayerAvatarFactory();
        private Action<string> logger;
        private bool constructionAttempted;
        private bool presentationFailed;
        private long presentationChecks;
        private PlayerTransform lastPose;
        internal string Strategy { get; private set; }
        internal bool Exists { get { return root; } }
        internal bool Visible { get { return root && root.activeSelf; } }
        internal Vector3 Position { get { return root ? root.transform.position : Vector3.zero; } }
        internal void Create(byte playerId, Transform local, Action<string> log, Vector3 controllerCenter, float controllerHeight, float heightOffset, float yawOffset, byte avatarId, GameObject crowbar)
        {
            if (root || constructionAttempted) return;
            constructionAttempted = true;
            logger = log;
            root = new GameObject("TS2MP_RemotePlayer_" + playerId);
            root.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(root);
            // Ordinary world rendering layer; no collider or Player tag to enter interaction lookups.
            root.layer = 0;
            var visualObject = new GameObject("VisualRoot"); visualObject.transform.SetParent(root.transform, false);
            visualRoot = visualObject.transform;
            visualHeight = Protocol.Finite(heightOffset) ? Mathf.Clamp(heightOffset, -10, 10) : 0;
            standingFeet = controllerCenter.y - controllerHeight * 0.5f;
            visualRoot.localPosition = new Vector3(0, Protocol.Finite(heightOffset) ? Mathf.Clamp(heightOffset, -10, 10) : 0, 0);
            visualRoot.localRotation = Quaternion.Euler(0, Protocol.Finite(yawOffset) ? yawOffset : 0, 0);
            visualYaw = Protocol.Finite(yawOffset) ? yawOffset : 0;
            try
            {
                AvatarVisual body = null;
                try
                {
                    float feet = controllerCenter.y - controllerHeight * 0.5f;
                    body = factory.Create(avatarId, visualRoot, feet);
                    avatar = new PlayerAvatarController(body, AvatarDefinition.Find(avatarId), crowbar, log);
                    Strategy = "Civilian AvatarId=" + avatarId;
                    log("[TS2MP] Avatar initialized; skins=" + body.Skins.Length + "; feetOffset=" + body.FeetOffset.ToString("F4") + "; clips=Idle,Walk,Run");
                }
                catch (Exception e)
                {
                    avatar?.Dispose(); avatar = null; body?.Dispose();
                    Strategy = "Fallback: " + e.Message;
                    log("[TS2MP] Avatar=Fallback; reason=" + e.Message);
                    AddFallback(playerId, controllerCenter, controllerHeight);
                }
                AssertVisualOnly(); log("[TS2MP] Remote player spawned: " + root.name + "; " + Strategy);
            }
            catch (Exception e)
            {
                Dispose(); constructionAttempted = true; Strategy = "Fallback unavailable: " + e.Message;
                log("[TS2MP] Cosmetic construction failed safely: " + e.Message);
            }
        }

        private void AddFallback(byte playerId, Vector3 center, float height)
        {
            Shader shader = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (!shader) { logger("[TS2MP] Primitive fallback unavailable: no presentation shader; networking continues."); return; }
            markerMaterial = new Material(shader) { color = playerId == 1 ? Color.cyan : new Color(1, 0.45f, 0.1f) };
            AddVisual("Capsule", CreateCapsule(center, height), new[] { markerMaterial }, Matrix4x4.identity);
        }

        private void AddVisual(string name, Mesh mesh, Material[] materials, Matrix4x4 matrix)
        {
            if (!meshes.Contains(mesh)) meshes.Add(mesh);
            // Convert baked vertices to the genuine player's root coordinates. No bones/IK/Animator copied.
            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            mesh.vertices = vertices; mesh.RecalculateNormals(); mesh.RecalculateBounds();
            var child = new GameObject(name); child.layer = root.layer; child.transform.SetParent(visualRoot, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>(); renderer.sharedMaterials = materials; fallbackRenderers.Add(renderer);
        }
        internal void SetPose(PlayerTransform pose, bool assertPresentationRoot = false)
        {
            if (!root) return;
            lastPose = pose;
            root.transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), Quaternion.Euler(0, pose.Yaw, 0));
            root.SetActive(true);
            try
            {
                Vector3 position = root.transform.position; Quaternion rotation = root.transform.rotation;
                bool lean = !pose.State.Vehicle.IsInVehicle && (pose.State.Known & Core.SampledFields.Lean) != 0;
                visualRoot.localPosition = new Vector3(lean ? pose.State.LeanOffset : 0, visualHeight + Core.GroundingPresentation.Offset(standingFeet, pose.State), 0);
                visualRoot.localRotation = Quaternion.Euler(0, visualYaw, lean ? pose.State.LeanRoll : 0);
                bool driverVisible = !presentationFailed && vehicle.Apply(pose, visualRoot, logger);
                foreach (var renderer in fallbackRenderers) if (renderer) renderer.enabled = !driverVisible;
                if (avatar != null) { avatar.Visual.Root.SetActive(!driverVisible); if (!driverVisible) avatar.Apply(pose); }
                if (assertPresentationRoot)
                {
                    presentationChecks++;
                    if (!root.transform.position.Equals(position) || !root.transform.rotation.Equals(rotation))
                    {
                        // This controller alone may restore authoritative root coordinates.
                        root.transform.SetPositionAndRotation(position, rotation);
                        throw new InvalidOperationException("Presentation modified NetworkRoot position/rotation.");
                    }
                }
            }
            catch (Exception e)
            {
                // Retire the failing presentation once. Retrying it every frame used
                // to allocate another mesh/material while retaining the old fallback.
                presentationFailed = true;
                vehicle.Dispose();
                avatar?.Dispose(); avatar = null;
                Strategy = "Fallback: " + e.Message;
                logger("[TS2MP] Avatar=Fallback; reason=" + e.Message);
                try { if(fallbackRenderers.Count==0) AddFallback(pose.PlayerId, Vector3.zero, 1.6f); }
                catch (Exception failure) { logger("[TS2MP] Primitive fallback unavailable: " + failure.Message); }
            }
        }
        internal void Hide() { if (root) root.SetActive(false); }
        private void AssertVisualOnly()
        {
            AvatarVisual.Validate(root);
        }
        internal void Audit(Action<string> log)
        {
            int roots = 0, renderers = 0, validMeshes = 0, validMaterials = 0;
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                if (candidate && candidate.scene.IsValid() && candidate.name.StartsWith("TS2MP_RemotePlayer_", StringComparison.Ordinal)) roots++;
            if (!root) { log("[TS2MP] Proxy audit: absent; roots=" + roots + "; avatars=0; animations=0; equipmentVisuals=0"); return; }
            AssertVisualOnly();
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true)) if (filter.sharedMesh && filter.sharedMesh.vertexCount > 0) validMeshes++;
            foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh && skin.sharedMesh.vertexCount > 0) validMeshes++;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                foreach (Material material in renderer.sharedMaterials) if (material && material.shader) validMaterials++;
            }
            log("[TS2MP] Proxy audit: name=" + root.name + "; roots=" + roots + "; scene=" + root.scene.name +
                "; renderers=" + renderers + "; validMeshes=" + validMeshes + "; validMaterials=" + validMaterials +
                "; forbidden=0; networkPosition=" + Position.ToString("F2") + "; networkYaw=" + root.transform.eulerAngles.y.ToString("F4") + "; networkPitch=" + root.transform.eulerAngles.x.ToString("F4") + "; visualOffset=" + visualRoot.localPosition.ToString("F2") + "; visualYaw=" + visualRoot.localEulerAngles.y.ToString("F1"));
            log("[TS2MP] NetworkRoot presentation assertions=" + presentationChecks);
            log("[TS2MP] Proxy semantic audit: crouched=" + lastPose.State.Crouched + "; relativeFeet=" + lastPose.State.GroundFeet.ToString("F4") + "; seat=" + lastPose.State.Vehicle.Seat + "; vehicleKey=" + lastPose.State.Vehicle.PresentationKey + "; driverVisible=" + vehicle.Visible);
            if (avatar != null)
            {
                float bottom = float.PositiveInfinity; int visible = 0;
                foreach (var skin in avatar.Visual.Skins) { bottom = Mathf.Min(bottom, skin.bounds.min.y); if (skin.enabled && skin.isVisible) visible++; }
                log("[TS2MP] Avatar audit: avatars=1; animations=1; clip=" + avatar.Visual.Animation.Current + "; playing=" + avatar.Visual.Animation.Animation.IsPlaying(avatar.Visual.Animation.Current) + "; skins=" + avatar.Visual.Skins.Length + "; visibleSkins=" + visible + "; rendererBoundsMinY=" + bottom.ToString("F4") + "; feetOffset=" + avatar.Visual.FeetOffset.ToString("F4") + "; equipmentVisuals=" + (avatar.Equipment.Exists ? 1 : 0) + "; sourceLocalMotion=" + avatar.Visual.Root.transform.GetChild(0).localPosition.ToString("F4"));
            }
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                log("[TS2MP] Proxy mesh: " + filter.name + "; boundsMin=" + filter.sharedMesh.bounds.min.ToString("F2") + "; boundsMax=" + filter.sharedMesh.bounds.max.ToString("F2"));
            if (roots != 1) throw new InvalidOperationException("Remote proxy uniqueness violated.");
        }
        private static Mesh CreateCapsule(Vector3 center, float height)
        {
            const int rings = 14, segments = 16;
            var vertices = new Vector3[(rings + 1) * segments]; var triangles = new int[rings * segments * 6];
            for (int ring = 0; ring <= rings; ring++)
            {
                float latitude = -Mathf.PI / 2 + Mathf.PI * ring / rings;
                float safeHeight = Protocol.Finite(height) ? Mathf.Clamp(height, 0.5f, 4) : 1.8f;
                float feet = center.y - safeHeight / 2;
                float ringCenter = latitude < 0 ? feet + 0.25f : feet + safeHeight - 0.25f;
                for (int segment = 0; segment < segments; segment++)
                {
                    float angle = Mathf.PI * 2 * segment / segments;
                    vertices[ring * segments + segment] = new Vector3(center.x + Mathf.Cos(angle) * Mathf.Cos(latitude) * 0.25f, ringCenter + Mathf.Sin(latitude) * 0.25f, center.z + Mathf.Sin(angle) * Mathf.Cos(latitude) * 0.25f);
                    if (ring == rings) continue;
                    int a = ring * segments + segment, b = ring * segments + (segment + 1) % segments, offset = (ring * segments + segment) * 6;
                    triangles[offset] = a; triangles[offset+1] = a+segments; triangles[offset+2] = b;
                    triangles[offset+3] = b; triangles[offset+4] = a+segments; triangles[offset+5] = b+segments;
                }
            }
            var mesh = new Mesh { name = "TS2MP_OriginalCapsule", vertices = vertices, triangles = triangles };
            mesh.RecalculateBounds(); mesh.RecalculateNormals(); return mesh;
        }
        internal void ResetSources() { factory.Reset(); }
        public void Dispose()
        {
            constructionAttempted = false;
            presentationFailed = false;
            avatar?.Dispose(); avatar = null;
            vehicle.Dispose();
            factory.Reset();
            if (root) { root.SetActive(false); root.name = "TS2MP_DisposedVisual"; UnityEngine.Object.Destroy(root); root = null; }
            visualRoot = null;
            foreach (Mesh mesh in meshes) if (mesh) UnityEngine.Object.Destroy(mesh);
            meshes.Clear();
            fallbackRenderers.Clear();
            if (markerMaterial) UnityEngine.Object.Destroy(markerMaterial); markerMaterial = null;
        }
    }
}
