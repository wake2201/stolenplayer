// M5 W3C/W3D (+ W3.1 polish): what the other player holds, carries and swings, shown on the existing remote avatar
// (RemoteAvatar = the game's own associate model with its legacy clips). Visual only: no colliders, rigidbodies, scripts,
// audio or Light components survive the copies, so nothing here can be picked up, pushed, seen by game logic or light the
// world.
// Sources (all ORIGINAL assets of the observing instance, verified field names, IL refs in src/Shared/PlayerState.cs):
//  - held tool: the observer's own first-person tool object for PlayerScript.currentItem (MeshRenderers only, first-person
//    arms dropped) parented to the avatar's mixamorig:RightHand. W3.2 grip (user decision: simple attachment, open hand
//    accepted, no grip animation / IK): long axis across the palm (pinky -> index knuckle), the MIDDLE of the shaft at the palm
//    centre (GearRules.GripAlongShaft / PalmCentre); the W3.1 upper-arm lift is removed. The game has no third-person tool
//    pose or attachment data (verified: no NPC melee clip) -> DESIGN COMPROMISE, documented.
//  - swing: PlayerScript.Attack() (one call per original attack, PlayerScript.Update IL_3994) increments a 4-bit counter in
//    the snapshot; a changed counter plays one short procedural right-arm swing (crowbar / baton) in LateUpdate, after the
//    legacy Animation has posed the avatar (one bone rotation per frame for 0.55 s; locomotion untouched).
//  - heavy carry: the original CarryRun clip + the carried item's original drop_object model in front of the chest.
//  - flashlight: W3.3 product decision - NO remote flashlight visual at all (no mesh, material, light, shadow or attachment).
//    W3.1 (all-emissive police torch: white triangle) and W3.2 (police torch, lens-only emission: magenta triangle in game)
//    were removed. The flashlight bits stay in the snapshot gear byte (protocol unchanged) and are simply not presented.
//    The local player's original flashlight (PlayerScript.torch / LightObj) is never touched by this class.
using System;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class RemoteGear
    {
        static readonly Dictionary<int, string> ToolFields = new Dictionary<int, string>
        {
            { 1, "CrowbarObject" }, { 2, "Binoculars" }, { 3, "PDA1" }, { 4, "hackerLaptopObj" }, { 5, "batonObj" }, { 6, "blowtorchObject" },
            { 7, "gasTankObject" }, { 8, "keyfobObj" }, { 9, "meat_obj" }, { 10, "tranq_obj" }, { 11, "radioObject" }, { 12, "cloakObj" },
            { 99, "phoneObj" }, { 102, "brick_obj" }
        };


        readonly InstanceLog _log;
        Type _psT, _itemT;
        Func<object, object> _psItems, _itemDrop; Func<object, int> _itemId;
        readonly Dictionary<int, Func<object, object>> _toolGetters = new Dictionary<int, Func<object, object>>();

        GameObject _avatarGo; Transform _hand, _palmRef, _index1, _pinky1, _upperArm, _chest;
        readonly Dictionary<int, GameObject> _tools = new Dictionary<int, GameObject>();   // pooled held-tool copies (built once per avatar)
        readonly Dictionary<int, GameObject> _carries = new Dictionary<int, GameObject>(); // pooled carried-item copies
        int _shownTool = -1, _shownCarry = -1;
        int _lastSwing = -1; float _swingT = -1f; bool _holding;
        readonly HashSet<int> _failedTools = new HashSet<int>(), _failedCarries = new HashSet<int>();
        public long ToolSwitches, CarrySwitches, Swings;

        public RemoteGear(InstanceLog log)
        {
            _log = log;
            _psT = AccessTools.TypeByName("PlayerScript"); _itemT = AccessTools.TypeByName("ItemID");
            _psItems = Fast.Getter<object>(_psT, "item_IDs2"); _itemDrop = Fast.Getter<object>(_itemT, "drop_object"); _itemId = Fast.Getter<int>(_itemT, "item_ID");
            foreach (var kv in ToolFields) { var g = Fast.Getter<object>(_psT, kv.Value); if (g != null) _toolGetters[kv.Key] = g; }
        }

        // Update, after RemoteAvatar.Apply (on foot), every frame the avatar is visible. ps = the observer's own PlayerScript.
        public void Apply(GameObject avatar, RemoteSample s, object ps)
        {
            if (avatar == null) return;
            if (!ReferenceEquals(avatar, _avatarGo)) Bind(avatar);
            bool carrying = (s.Gear & GearFlags.Carrying) != 0;
            int tool = GearRules.ToolShown(s.Held, s.Gear, false);
            ShowTool(tool, ps);
            ShowCarry(carrying ? s.CarryItem - 1 : -1, ps);
            _holding = tool > 0 && _hand != null;
            if (GearRules.SwingStarts(_lastSwing, s.Swing, tool) && _upperArm != null) { _swingT = 0f; Swings++; }
            _lastSwing = s.Swing;                                      // first sample after (re)bind only records (no replay)
        }

        // Seated in a car (driver / passenger): the original game puts tools away and switches the flashlight off on entry.
        public void Seated(GameObject avatar)
        {
            if (avatar == null) return;
            if (!ReferenceEquals(avatar, _avatarGo)) Bind(avatar);
            ShowTool(0, null); ShowCarry(-1, null);
            _holding = false; _swingT = -1f;
        }

        // LateUpdate: after the legacy Animation posed the avatar this frame (its pose is resampled every frame, so these
        // rotations never accumulate). Only the right upper arm, only while a tool is held / a swing runs.
        public void LateFrame(float dt)
        {
            if (_upperArm == null || _avatarGo == null || !_avatarGo.activeInHierarchy) { _swingT = -1f; return; }
            float angle = 0f;
            if (_holding) angle = GearRules.HoldArmLiftDeg;            // W3.2: 0 (the W3.1 forward lift is removed)
            if (_swingT >= 0f)
            {
                _swingT += dt;
                angle += GearRules.SwingCurve(_swingT);
                if (_swingT >= GearRules.SwingSeconds) _swingT = -1f;
            }
            if (angle != 0f) _upperArm.rotation = Quaternion.AngleAxis(angle, _avatarGo.transform.right) * _upperArm.rotation;
        }


        void Bind(GameObject avatar)
        {
            // copies of the previous avatar were children of it (destroyed with it)
            _avatarGo = avatar; _tools.Clear(); _carries.Clear();
            _shownTool = -1; _shownCarry = -1; _lastSwing = -1; _swingT = -1f; _holding = false;
            var t = avatar.transform;
            _hand = FindBone(t, "mixamorig:RightHand"); _palmRef = FindBone(t, "mixamorig:RightHandMiddle1"); _upperArm = FindBone(t, "mixamorig:RightArm");
            _index1 = FindBone(t, "mixamorig:RightHandIndex1"); _pinky1 = FindBone(t, "mixamorig:RightHandPinky1");
            _chest = FindBone(t, "mixamorig:Spine2") ?? FindBone(t, "mixamorig:Spine1");
            _log.Info("AVATAR_GEAR bones hand=" + N(_hand) + " palm=" + N(_palmRef) + " index=" + N(_index1) + " pinky=" + N(_pinky1) + " upperArm=" + N(_upperArm) + " chest=" + N(_chest));
        }
        static string N(Transform t) { return t != null ? t.name : "-"; }

        static Transform FindBone(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
            return null;
        }

        // ---- held tool ----
        void ShowTool(int tool, object ps)
        {
            if (tool == _shownTool) return;
            if (_shownTool > 0) { GameObject old; if (_tools.TryGetValue(_shownTool, out old) && old != null) old.SetActive(false); }
            _shownTool = tool; ToolSwitches++;
            if (tool <= 0 || _hand == null) return;
            GameObject go;
            if (!_tools.TryGetValue(tool, out go) || go == null)
            {
                go = BuildTool(tool, ps);
                if (go == null) return;
                _tools[tool] = go;
            }
            go.SetActive(true);
        }

        GameObject BuildTool(int tool, object ps)
        {
            if (_failedTools.Contains(tool) || ps == null) return null;
            Func<object, object> g; var src = _toolGetters.TryGetValue(tool, out g) ? g(ps) as GameObject : null;
            if (src == null) { _failedTools.Add(tool); _log.Info("AVATAR_GEAR held tool " + tool + ": no original model on this instance (not shown)"); return null; }
            var copy = VisualCopy(src, "TS2MP_HeldTool_" + tool, true);
            if (copy == null) { _failedTools.Add(tool); _log.Info("AVATAR_GEAR held tool " + tool + " (" + src.name + "): no mesh renderers (not shown)"); return null; }
            float dist; string how; float len = Grip(copy, out dist, out how);
            _log.Info("AVATAR_GEAR held tool " + tool + " from " + src.name + " length " + len.ToString("0.00") + " m, shaft middle in the palm (" + how + "), shaft-to-palm " + dist.ToString("0.000") + " m");
            return copy;
        }

        // W3.2 simple attachment (GearRules): computed once from the hand bones in the avatar's CURRENT pose, then parented to
        // the hand, so it follows idle / walk / run / crouch and the swing without any per-frame work.
        //  - long axis across the palm: little-finger knuckle -> index knuckle (made perpendicular to wrist -> middle knuckle);
        //  - second axis of the mesh (e.g. the crowbar's hook bend) along the fingers;
        //  - shaft middle (GripAlongShaft 0.5) at the palm centre (PalmCentre 0.5 of wrist -> middle knuckle).
        float Grip(GameObject copy, out float dist, out string how)
        {
            var av = _avatarGo.transform;
            Vector3 axisL, secondL, centerL; float len = LocalAxes(copy, out axisL, out secondL, out centerL);
            if (len > 0.9f) { copy.transform.localScale *= 0.7f / len; len = LocalAxes(copy, out axisL, out secondL, out centerL); }
            Vector3 palm = _palmRef != null ? Vector3.Lerp(_hand.position, _palmRef.position, GearRules.PalmCentre) : _hand.position;
            Vector3 finger = _palmRef != null ? (_palmRef.position - _hand.position).normalized : -av.up;
            Vector3 across = _index1 != null && _pinky1 != null ? _index1.position - _pinky1.position : av.forward;
            across = Vector3.ProjectOnPlane(across, finger);
            if (across.sqrMagnitude < 1e-6f) across = Vector3.ProjectOnPlane(av.forward, finger);
            across.Normalize();
            how = _index1 != null && _pinky1 != null ? "pinky->index knuckles" : "avatar forward (finger bones missing)";
            // orientation: long axis -> across, then roll about it so the second axis follows the fingers
            copy.transform.rotation = av.rotation;
            copy.transform.rotation = Quaternion.FromToRotation(copy.transform.TransformDirection(axisL).normalized, across) * copy.transform.rotation;
            Vector3 sec = Vector3.ProjectOnPlane(copy.transform.TransformDirection(secondL), across), fin = Vector3.ProjectOnPlane(finger, across);
            if (sec.sqrMagnitude > 1e-8f && fin.sqrMagnitude > 1e-8f) copy.transform.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(sec, fin, across), across) * copy.transform.rotation;
            // position: the grip point on the shaft (its middle) at the palm centre
            Vector3 gripL = centerL + axisL.normalized * GearRules.GripOffset(axisL.magnitude);
            copy.transform.position += palm - copy.transform.TransformPoint(gripL);
            dist = Vector3.Distance(copy.transform.TransformPoint(gripL), palm);
            copy.transform.SetParent(_hand, true);
            copy.SetActive(false);
            return len;
        }

        // Longest and second-longest axes of the copy's meshes in the copy's local space (half-extent vectors), local centre;
        // returns the world length of the long axis.
        static float LocalAxes(GameObject copy, out Vector3 axis, out Vector3 second, out Vector3 center)
        {
            float len = LocalLongAxis(copy, out axis, out center);
            var s = _lastLocalSize;                                    // local bounds size of that same pass
            // second = the larger of the two remaining extents
            if (axis.x != 0f) second = s.y >= s.z ? new Vector3(0, s.y * 0.5f, 0) : new Vector3(0, 0, s.z * 0.5f);
            else if (axis.y != 0f) second = s.x >= s.z ? new Vector3(s.x * 0.5f, 0, 0) : new Vector3(0, 0, s.z * 0.5f);
            else second = s.x >= s.y ? new Vector3(s.x * 0.5f, 0, 0) : new Vector3(0, s.y * 0.5f, 0);
            return len;
        }

        static Vector3 _lastLocalSize;   // main thread only

        // Longest axis of the copy's meshes in the copy's local space (half-extent vector, local centre, world length).
        static float LocalLongAxis(GameObject copy, out Vector3 axis, out Vector3 center)
        {
            var root = copy.transform; bool any = false; var b = new Bounds();
            foreach (var mf in copy.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = root.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            center = b.center;
            var s = b.size; var ls = root.lossyScale; _lastLocalSize = s;
            float wx = s.x * Mathf.Abs(ls.x), wy = s.y * Mathf.Abs(ls.y), wz = s.z * Mathf.Abs(ls.z);
            if (wx >= wy && wx >= wz) { axis = new Vector3(s.x * 0.5f, 0, 0); return wx; }
            if (wy >= wz) { axis = new Vector3(0, s.y * 0.5f, 0); return wy; }
            axis = new Vector3(0, 0, s.z * 0.5f); return wz;
        }

        // ---- heavy carry ----
        void ShowCarry(int itemId, object ps)
        {
            if (itemId == _shownCarry) return;
            if (_shownCarry >= 0) { GameObject old; if (_carries.TryGetValue(_shownCarry, out old) && old != null) old.SetActive(false); }
            _shownCarry = itemId; CarrySwitches++;
            if (itemId < 0) return;
            GameObject go;
            if (!_carries.TryGetValue(itemId, out go) || go == null)
            {
                go = BuildCarry(itemId, ps);
                if (go == null) return;
                _carries[itemId] = go;
            }
            go.SetActive(true);
        }

        GameObject BuildCarry(int itemId, object ps)
        {
            if (_failedCarries.Contains(itemId) || ps == null || _chest == null) return null;
            var prefab = ItemModel(ps, itemId);
            if (prefab == null) { _failedCarries.Add(itemId); _log.Info("AVATAR_GEAR carried item " + itemId + ": no original world model on this instance (CarryRun pose only)"); return null; }
            var copy = VisualCopy(prefab, "TS2MP_Carried_" + itemId, true);
            if (copy == null) { _failedCarries.Add(itemId); return null; }
            var av = _avatarGo.transform;
            copy.transform.rotation = av.rotation;
            var bb = Bounds(copy);
            copy.transform.position += av.TransformPoint(new Vector3(0f, 1.15f, 0.45f)) - bb.center;
            copy.transform.SetParent(av, true);
            copy.SetActive(false);
            _log.Info("AVATAR_GEAR carried item " + itemId + " from " + prefab.name + " size " + bb.size.ToString("0.00"));
            return copy;
        }

        GameObject ItemModel(object ps, int itemId)
        {
            var arr = _psItems != null ? _psItems(ps) as Array : null;
            if (arr != null) foreach (var o in arr) if (o != null && _itemId(o) == itemId) return _itemDrop(o) as GameObject;
            return null;
        }

        // ---- copies ----
        // Instantiated under a disabled holder (no Awake/OnEnable of game scripts), then only Transform, MeshFilter and
        // MeshRenderer survive; layer Default, untagged.
        static GameObject _holder;
        static GameObject VisualCopy(GameObject src, string name, bool resetRoot)
        {
            if (_holder == null) { _holder = new GameObject("TS2MP_GearHolder"); _holder.SetActive(false); _holder.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(_holder); }
            var copy = UnityEngine.Object.Instantiate(src, _holder.transform, false);
            copy.name = name;
            Strip(copy);
            if (copy.GetComponentsInChildren<MeshRenderer>(true).Length == 0) { UnityEngine.Object.Destroy(copy); return null; }
            if (resetRoot) { copy.transform.localPosition = Vector3.zero; copy.transform.localRotation = Quaternion.identity; }
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) { t.gameObject.layer = 0; t.gameObject.tag = "Untagged"; }
            foreach (var r in copy.GetComponentsInChildren<MeshRenderer>(true)) r.enabled = true;
            copy.transform.SetParent(null, true);
            copy.SetActive(true);
            return copy;
        }

        static void Strip(GameObject root)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                var victims = new List<Component>();
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                    if (c != null && !(c is Transform) && !(c is MeshFilter) && !(c is MeshRenderer)) victims.Add(c);
                if (victims.Count == 0) break;
                victims.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
                foreach (var c in victims) { try { UnityEngine.Object.DestroyImmediate(c); } catch { } }
            }
        }
        static int Rank(Component c) { return c is Joint ? 0 : c is Rigidbody ? 2 : 1; }

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<MeshRenderer>(true);
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
            var b = rs[0].bounds; for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        public string Status { get { return "tool=" + _shownTool + " carry=" + _shownCarry + " switches tool/carry=" + ToolSwitches + "/" + CarrySwitches + " swings=" + Swings; } }
    }
}
