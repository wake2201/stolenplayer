// W6B.4 mission minimap blips on the ORIGINAL marker instances (verified 1.27c IL, docs/research/STORY_SYSTEM.md section 4e).
//   Original: a story marker is a "STORY MARKER" child (StoryObject-gated) holding a bl_MiniMapItem (story icon) and a
//   Diamond_Marker (3D diamond: its Update re-enables its SpriteRenderer every frame within 2.5..30 m). bl_MiniMapItem.OnEnable
//   -> CreateIcon (one UI icon under the minimap, Target = own transform when unset) + RegisterItem; OnDisable -> HideItem
//   (icon inactive); the icon is drawn at Target.position every minimap update. The icon is NOT a child of the item, so hiding
//   the item's renderers (HeavySync.Hide: remote carry, cargo / unpack / sale removal, synthetic cargo outside its car) left
//   the icon and the diamond at the old place: the 0.29.0 ghost markers at the house / car / table, and a second marker next
//   to the cargo copy that carries its own original marker.
//   This helper only toggles those original components: hidden object -> its markers disabled (original OnDisable hides the
//   icon; a disabled behaviour also never runs OnEnable when StoryObject re-activates the marker); hidden because the other
//   player carries it -> the same original icon follows the remote avatar (Target retargeted, no new marker); shown again ->
//   targets and enabled flags restored (original OnEnable creates the icon as on every vanilla re-activation).
// Cost: event-only (HeavySync presentation changes), one GetComponentsInChildren per object per hide; no per-frame work.
using System;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class MarkerState
    {
        public bool Active, Off; public Transform Following;
        public readonly List<Behaviour> Items = new List<Behaviour>(2);
        public readonly List<Transform> Targets = new List<Transform>(2);
        public readonly List<Behaviour> Diamonds = new List<Behaviour>(2);
    }

    internal static class MissionMarkers
    {
        static bool _tried, _ok; static Type _itemT, _diamondT;
        static Func<object, Transform> _getTarget; static Action<object, Transform> _setTarget;
        public static long Hidden, Followed, Restored;
        static InstanceLog _log;

        static bool Ready(InstanceLog log)
        {
            if (_tried) return _ok;
            _tried = true; _log = log;
            _itemT = AccessTools.TypeByName("bl_MiniMapItem"); _diamondT = AccessTools.TypeByName("Diamond_Marker");
            _getTarget = Fast.Getter<Transform>(_itemT, "Target"); _setTarget = Fast.Setter<Transform>(_itemT, "Target");
            _ok = _itemT != null && _diamondT != null && _getTarget != null && _setTarget != null;
            if (!_ok && log != null) log.Warn("BLIP bl_MiniMapItem / Diamond_Marker members not found: markers of hidden objects are not managed");
            return _ok;
        }

        static bool Alive(UnityEngine.Object o) { return o != null; }

        // The object is hidden by the mod. follow != null: the other player carries it -> its icon follows that avatar.
        public static void Hide(InstanceLog log, GameObject go, MarkerState st, Transform follow, string why)
        {
            if (go == null || st == null || !Ready(log)) return;
            if (!st.Active)
            {
                st.Items.Clear(); st.Targets.Clear(); st.Diamonds.Clear();
                foreach (var c in go.GetComponentsInChildren(_itemT, true)) { var b = c as Behaviour; if (b != null && b.enabled) { st.Items.Add(b); st.Targets.Add(_getTarget(b)); } }
                foreach (var c in go.GetComponentsInChildren(_diamondT, true)) { var b = c as Behaviour; if (b != null && b.enabled) st.Diamonds.Add(b); }
                st.Active = true; st.Following = null; st.Off = false;
                if (st.Items.Count == 0 && st.Diamonds.Count == 0) return;   // no original marker on this object: nothing to log
                foreach (var d in st.Diamonds) if (Alive(d)) d.enabled = false;
            }
            else if (st.Items.Count == 0) return;
            if (!Alive(follow)) follow = null;
            if (follow == st.Following && st.Following != null) return;
            if (follow != null)
            {
                for (int i = 0; i < st.Items.Count; i++) { var b = st.Items[i]; if (!Alive(b)) continue; _setTarget(b, follow); if (!b.enabled) b.enabled = true; }
                st.Following = follow; st.Off = false; Followed++;
                if (st.Items.Count > 0) _log.Info("BLIP '" + go.name + "' " + st.Items.Count + " minimap marker(s) follow " + follow.name + " (" + why + ")");
                return;
            }
            if (st.Off) return;
            bool was = st.Following != null;
            for (int i = 0; i < st.Items.Count; i++) { var b = st.Items[i]; if (!Alive(b)) continue; if (b.enabled) b.enabled = false; _setTarget(b, st.Targets[i]); }
            st.Following = null; st.Off = true; Hidden++;
            _log.Info("BLIP '" + go.name + "' " + st.Items.Count + " minimap marker(s) + " + st.Diamonds.Count + " diamond(s) hidden (" + why + (was ? ", no longer following the carrier" : "") + ")");
        }

        // The object is shown by the original again: targets + enabled flags as they were before the mod hid it.
        public static void Show(GameObject go, MarkerState st, string why)
        {
            if (st == null || !st.Active) return;
            st.Active = false;
            int n = st.Items.Count + st.Diamonds.Count;
            for (int i = 0; i < st.Items.Count; i++) { var b = st.Items[i]; if (!Alive(b)) continue; _setTarget(b, st.Targets[i]); if (!b.enabled) b.enabled = true; }
            foreach (var d in st.Diamonds) if (Alive(d)) d.enabled = true;
            st.Items.Clear(); st.Targets.Clear(); st.Diamonds.Clear(); st.Following = null; st.Off = false;
            if (n > 0) { Restored++; _log.Info("BLIP '" + (go != null ? go.name : "?") + "' original marker(s) restored (" + why + ")"); }
        }

        public static string Stats { get { return "BLIP_STATS hidden " + Hidden + " followed " + Followed + " restored " + Restored; } }
    }
}
