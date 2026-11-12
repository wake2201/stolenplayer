// Visual-only remote player. Reuses the game's own "associate" thief model (MainScene: Codename/anim) with its
// legacy Animation clips (Idle, Walk, Run, Sneaking). Verified in M2 from level1 asset data (docs/CODEBASE_MAP.md §3.2).
// The clone keeps only Transform, Animation, SkinnedMeshRenderer, MeshRenderer, MeshFilter: no scripts, colliders,
// rigidbodies, audio, cameras or AI, untagged, so no game system can mistake it for the player or an NPC.
using System;
using System.Collections.Generic;
using System.Linq;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class RemoteAvatar
    {
        static readonly string[] RequiredClips = { "Idle", "Walk", "Run" };
        static GameObject _holder, _template;
        static float _nextSearch;
        public static string TemplateSource = "none";
        // M4.2.2: local pose of 'anim' inside its 'Codename' root (CodenameAI places the ROOT at a seat anchor)
        static Vector3 _rootLocalPos; static Quaternion _rootLocalRot = Quaternion.identity;

        GameObject _go;
        Animation _anim;
        string _idle, _walk, _run, _crouch, _sit, _carry, _current;
        public GameObject Root { get { return _go; } }
        float _smoothSpeed;
        public bool IsFallback { get; private set; }
        public Vector3 Position { get { return _go != null ? _go.transform.position : Vector3.zero; } }
        public bool Visible { get { return _go != null && _go.activeSelf; } }

        // ---- template (shared, survives scene loads) ----
        public static bool TryEnsureTemplate(InstanceLog log)
        {
            if (_template != null) return true;
            if (Time.unscaledTime < _nextSearch) return false;
            _nextSearch = Time.unscaledTime + 2f;

            string path;
            var src = FindCandidate(out path);
            if (src == null) return false;
            try
            {
                if (_holder == null)
                {
                    _holder = new GameObject("TS2MP_TemplateHolder");
                    _holder.SetActive(false);                      // children instantiated here never run Awake/OnEnable
                    _holder.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                }
                var clone = UnityEngine.Object.Instantiate(src.gameObject, _holder.transform, false);
                clone.name = "TS2MP_RemoteTemplate";
                if (src.transform.parent != null && src.transform.parent.name == "Codename") { _rootLocalPos = src.transform.localPosition; _rootLocalRot = src.transform.localRotation; }
                int removed = Strip(clone);
                _template = clone;
                var clips = ClipNames(clone.GetComponent<Animation>());
                TemplateSource = path;
                log.Info("AVATAR_TEMPLATE source=" + path + " clips=" + string.Join(",", clips) + " strippedComponents=" + removed
                    + " renderers=" + clone.GetComponentsInChildren<Renderer>(true).Length);
                return true;
            }
            catch (Exception ex) { log.Error("AVATAR_TEMPLATE clone failed for " + path, ex); return false; }
        }

        static Animation FindCandidate(out string path)
        {
            path = null;
            Animation best = null; int bestScore = 0;
            foreach (var a in Resources.FindObjectsOfTypeAll<Animation>())
            {
                if (a == null) continue;
                var go = a.gameObject;
                if (!go.scene.IsValid() || go.layer == 5 || go.layer == 23) continue;        // assets / UI previews
                if (go.GetComponentsInParent<RectTransform>(true).Length > 0 || go.GetComponentsInParent<Canvas>(true).Length > 0) continue;
                var clips = ClipNames(a);
                if (!RequiredClips.All(clips.Contains)) continue;
                if (go.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0) continue;
                int score = 1;
                if (clips.Contains("Sneaking")) score += 2;
                if (go.transform.parent != null && go.transform.parent.name == "Codename") score += 4;
                if (score > bestScore) { best = a; bestScore = score; }
            }
            if (best != null) path = best.gameObject.scene.name + ":" + PathOf(best.transform);
            return best;
        }

        static int Strip(GameObject root)
        {
            int removed = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                var victims = root.GetComponentsInChildren<Component>(true).Where(c => c != null && !Keep(c))
                    .OrderBy(c => c is Joint ? 0 : c is Rigidbody ? 2 : 1).ToList();
                if (victims.Count == 0) break;
                foreach (var c in victims)
                {
                    try { UnityEngine.Object.DestroyImmediate(c); removed++; } catch { }
                }
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.tag = "Untagged";
                if (t.gameObject.layer == 5 || t.gameObject.layer == 23) t.gameObject.layer = 0;
            }
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;
            return removed;
        }

        static bool Keep(Component c)
        {
            return c is Transform || c is Animation || c is SkinnedMeshRenderer || c is MeshRenderer || c is MeshFilter;
        }

        static List<string> ClipNames(Animation a)
        {
            var list = new List<string>();
            if (a == null) return list;
            foreach (AnimationState s in a) list.Add(s.name);
            return list;
        }

        static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (; t != null; t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        // ---- instance ----
        public static RemoteAvatar Spawn(string label, InstanceLog log, bool allowFallback)
        {
            var av = new RemoteAvatar();
            if (_template != null)
            {
                av._go = UnityEngine.Object.Instantiate(_template);
                av._anim = av._go.GetComponent<Animation>();
                av.ResolveClips();
            }
            else if (allowFallback)
            {
                // Diagnostic fallback only (no compatible model found): Unity's built-in capsule, no collider.
                av._go = new GameObject();
                var cap = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                UnityEngine.Object.DestroyImmediate(cap.GetComponent<Collider>());
                cap.transform.SetParent(av._go.transform, false);
                cap.transform.localPosition = new Vector3(0, 1f, 0);
                av.IsFallback = true;
                log.Warn("AVATAR_FALLBACK no compatible game character found; showing a capsule");
            }
            else return null;
            av._go.name = "TS2MP_RemotePlayer_" + label;
            av._go.hideFlags = HideFlags.DontSave;
            UnityEngine.Object.DontDestroyOnLoad(av._go);
            av._go.SetActive(false);
            log.Info("AVATAR_SPAWNED " + av._go.name + (av.IsFallback ? " (fallback capsule)" : " clips idle=" + av._idle + " walk=" + av._walk + " run=" + av._run + " crouch=" + av._crouch));
            return av;
        }

        void ResolveClips()
        {
            if (_anim == null) return;
            var names = ClipNames(_anim);
            _idle = names.Contains("Idle") ? "Idle" : null;
            _walk = names.Contains("Walk") ? "Walk" : null;
            _run = names.Contains("Run") ? "Run" : (names.Contains("CarryRun") ? "CarryRun" : _walk);
            _crouch = names.Contains("Sneaking") ? "Sneaking" : null;
            _sit = names.Contains("Sitting") ? "Sitting" : null;   // original clip, also used by CodenameAI in cars
            _carry = names.Contains("CarryRun") ? "CarryRun" : null; // W3C: original heavy-carry clip of the associate model
            foreach (var n in new[] { _idle, _walk, _run, _crouch, _sit, _carry })
                if (n != null) _anim[n].wrapMode = WrapMode.Loop;
            _anim.playAutomatically = false;
            _anim.cullingType = AnimationCullingType.AlwaysAnimate;
            if (_idle != null) { _anim.Play(_idle); _current = _idle; }
        }

        public void SetVisible(bool v)
        {
            if (_go == null || _go.activeSelf == v) return;
            _go.SetActive(v);
            // Legacy Animation stops playing when its GameObject is deactivated: resume the current clip.
            if (v && _anim != null && _current != null) _anim.Play(_current);
        }

        public void Apply(RemoteSample s, float dt, float yawOffset, float walkRef, float runRef, float crouchRef)
        {
            if (_go == null) return;
            _go.transform.position = new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z);
            _go.transform.rotation = Quaternion.Euler(0f, s.Yaw + yawOffset, 0f);
            if (_anim == null) return;

            float k = 1f - Mathf.Exp(-dt * 10f);
            _smoothSpeed = s.Snapped ? s.HorizontalSpeed : Mathf.Lerp(_smoothSpeed, s.HorizontalSpeed, k);
            float sp = _smoothSpeed;
            bool crouch = (s.Flags & MoveFlags.Crouching) != 0;
            bool running = (s.Flags & MoveFlags.Running) != 0;

            string clip; float speed = 1f;
            // W3C heavy carry: the original CarryRun clip; standing still holds its carrying pose (clip paused)
            if ((s.Gear & GearFlags.Carrying) != 0 && _carry != null) { clip = _carry; speed = sp < 0.2f ? 0f : Mathf.Clamp(sp / walkRef, 0.5f, 1.4f); }
            else if (crouch && _crouch != null) { clip = _crouch; speed = sp < 0.15f ? 0f : Mathf.Clamp(sp / crouchRef, 0.5f, 1.5f); }
            else if (sp < 0.2f) clip = _idle ?? _walk;
            else if ((running && sp > walkRef * 1.15f) || sp > (walkRef + runRef) * 0.5f) { clip = _run; speed = Mathf.Clamp(sp / runRef, 0.6f, 1.5f); }
            else { clip = _walk; speed = Mathf.Clamp(sp / walkRef, 0.5f, 1.6f); }
            if (clip == null) return;
            if (clip != _current) { _anim.CrossFade(clip, 0.2f); _current = clip; }
            _anim[clip].speed = speed;
        }

        // M4.2.2: seated in a car seat. The anchor comes from the car's own seat transforms (VehicleSync.ProbeSeat); only the
        // pose is set (no walking movement is applied while seated), so turning follows the car exactly.
        public void ApplySeated(Vector3 anchorPos, Quaternion anchorRot, bool codenameRoot)
        {
            if (_go == null) return;
            if (codenameRoot) _go.transform.SetPositionAndRotation(anchorPos + anchorRot * _rootLocalPos, anchorRot * _rootLocalRot);
            else _go.transform.SetPositionAndRotation(anchorPos, anchorRot);
            _smoothSpeed = 0f;
            if (_anim == null || _sit == null) return;
            if (_current != _sit) { _anim.CrossFade(_sit, 0.2f); _current = _sit; }
            _anim[_sit].speed = 1f;
        }

        public string CurrentClip { get { return _current ?? "-"; } }

        public void Destroy()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
            _go = null; _anim = null;
        }
    }
}
