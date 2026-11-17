// W6B: the other player's drone, shown as a visual-only clone of the ORIGINAL drone model of the item the pilot flies
// (ItemID.droneObject of the same item_ID on this PC's PlayerScript.item_IDs; every PC has every ItemID, ownership is the
// item_amount). Same stripping as RemoteAvatar: only Transform / renderers / mesh filters / Animation survive - no
// DroneMovement, rigidbody, colliders, cameras, lights, audio or sensors, untagged, so no game system (NPC perception, camera
// disabling, pickups, the local DroneManager) can mistake it for a drone. The clone is instantiated under an inactive holder
// (no Awake / OnEnable of the original scripts before they are stripped). Pose: interpolated drone samples (PlayerSync).
//
// Local drone state (pilot side): DroneManager reached through PlayerScript.droneManager; activated, droneObj,
// joystick_character (the standing pilot body), assigned_drone_script.item_ID. Read at the send rate only.
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal static class LocalDrone
    {
        static bool _resolved, _ok;
        static Func<object, object> _psDrone, _dmDroneObj, _dmJoystick, _dmAssigned, _psItems, _itemDroneObj;
        static Func<object, bool> _dmActivated; static Func<object, int> _itemId;

        static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            var ps = AccessTools.TypeByName("PlayerScript"); var dm = AccessTools.TypeByName("DroneManager"); var it = AccessTools.TypeByName("ItemID");
            if (ps == null || dm == null || it == null) return;
            _psDrone = Fast.Getter<object>(ps, "droneManager"); _psItems = Fast.Getter<object>(ps, "item_IDs");
            _dmActivated = Fast.Getter<bool>(dm, "activated"); _dmDroneObj = Fast.Getter<object>(dm, "droneObj");
            _dmJoystick = Fast.Getter<object>(dm, "joystick_character"); _dmAssigned = Fast.Getter<object>(dm, "assigned_drone_script");
            _itemId = Fast.Getter<int>(it, "item_ID"); _itemDroneObj = Fast.Getter<object>(it, "droneObject");
            _ok = _psDrone != null && _psItems != null && _dmActivated != null && _dmDroneObj != null && _dmJoystick != null && _dmAssigned != null && _itemId != null && _itemDroneObj != null;
        }

        public static bool Ok { get { Resolve(); return _ok; } }

        static bool Alive(object o) { var u = o as UnityEngine.Object; return u != null && u; }

        // The local player pilots its drone now (original DroneManager.activated with a live drone object).
        public static bool Active(object playerScript, out Transform drone, out Transform body, out int itemId)
        {
            drone = null; body = null; itemId = 0;
            Resolve();
            if (!_ok || !Alive(playerScript)) return false;
            var dm = _psDrone(playerScript);
            if (!Alive(dm) || !_dmActivated(dm)) return false;
            var d = _dmDroneObj(dm) as GameObject; if (d == null || !d.activeInHierarchy) return false;
            drone = d.transform;
            var j = _dmJoystick(dm) as GameObject; if (j != null && j.activeInHierarchy) body = j.transform;
            var a = _dmAssigned(dm); if (Alive(a)) itemId = _itemId(a);
            return true;
        }

        public static bool Active(object playerScript) { Transform d, b; int id; return Active(playerScript, out d, out b, out id); }

        // The original drone model of an item id on this PC (null when this PC has no such drone item).
        public static GameObject Model(object playerScript, int itemId)
        {
            Resolve();
            if (!_ok || !Alive(playerScript) || itemId <= 0) return null;
            var list = _psItems(playerScript) as IList; if (list == null) return null;
            foreach (var it in list) if (Alive(it) && _itemId(it) == itemId) return _itemDroneObj(it) as GameObject;
            return null;
        }
    }

    internal sealed class RemoteDrone
    {
        static GameObject _holder;
        static readonly Dictionary<int, GameObject> _templates = new Dictionary<int, GameObject>();
        GameObject _go; public readonly int ItemId;

        RemoteDrone(int itemId) { ItemId = itemId; }

        public static RemoteDrone Spawn(object playerScript, int itemId, InstanceLog log)
        {
            GameObject tpl;
            if (!_templates.TryGetValue(itemId, out tpl) || tpl == null)
            {
                var src = LocalDrone.Model(playerScript, itemId);
                if (src == null) { log.Warn("DRONE_REMOTE no original drone model for item " + itemId + " on this PC: the other player's drone is not shown"); _templates[itemId] = null; return null; }
                if (_holder == null)
                {
                    _holder = new GameObject("TS2MP_DroneTemplates"); _holder.SetActive(false);
                    _holder.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(_holder);
                }
                tpl = UnityEngine.Object.Instantiate(src, _holder.transform, false);   // inactive parent: no Awake / OnEnable
                tpl.name = "TS2MP_DroneTemplate_" + itemId;
                int removed = RemoteAvatar.Strip(tpl);
                foreach (var r in tpl.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                _templates[itemId] = tpl;
                log.Info("DRONE_REMOTE template for item " + itemId + " from the original '" + src.name + "' (" + removed + " component(s) stripped, renderers only)");
            }
            var d = new RemoteDrone(itemId);
            d._go = UnityEngine.Object.Instantiate(tpl);
            d._go.name = "TS2MP_RemoteDrone_" + itemId;
            UnityEngine.Object.DontDestroyOnLoad(d._go);
            d._go.SetActive(false);
            return d;
        }

        public void Apply(RemoteSample s, float roll)
        {
            if (_go == null) return;
            _go.transform.SetPositionAndRotation(new Vector3(s.Pos.X, s.Pos.Y, s.Pos.Z), Quaternion.Euler(s.Pitch, s.Yaw, roll));
            if (!_go.activeSelf) _go.SetActive(true);
        }

        public void Hide() { if (_go != null && _go.activeSelf) _go.SetActive(false); }
        public void Destroy() { if (_go != null) UnityEngine.Object.Destroy(_go); _go = null; }
    }
}
