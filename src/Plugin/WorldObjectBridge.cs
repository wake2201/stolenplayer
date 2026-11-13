// W4.2B replica bridge: applies an AUTHORIZED door / lock / window state to the local copy of the original objects using
// only their state and presentation paths. It never calls an original method that has gameplay consequences: no
// Lockpick.DoorUnlock / DoorUnlockKey / BreakPadlock, no LockedWindow.UnlockWindow / BrokeWindow / CheckAlarm, no Door.Use /
// SlowFailed / ResetLock, no StartPoliceChase / AlertNPCs / MakeNoise / Mission_Complete / AdvanceStory, no tool wear
// (source-scanned by tests/W42BTests.cs). Originals it does call (all side-effect free, verified IL in
// docs/research/*_SYSTEM.md): Door.OpenOnStartt (snap open + Resett), Door.Resett, Door.OpenDestroyed (sound + swing +
// brokeHinge, no player access), Lockpick.RelockDoor (tags / was_locked / collider only).
//
// Mirrored IL:
//   - Swing      = Door.Use IL_0031..IL_0098 + clip (minus PCB/PMB/MakeNoise/noNoise of the local player) - as Phase A.
//   - SnapClosed = Door.Update closing branch end pose (SaveRot / SaveRotVector / SavePos) + Resett.
//   - Unlock     = Lockpick.DoorUnlock IL_004e..IL_00ad (own tag Untagged; per door enabled, was_locked, tag Door,
//                  requires_key = false; Disable_This_Collider) - minus story (IL_0001..0044) and openedLocks (IL_00b3..).
//   - KeyUnlock  = Door.Unlock_Key per door (requires_key = false, tag = set_tag_after_unlock without the 0.5 s delay, unlock clip).
//   - Padlock    = Lockpick.BreakPadlock IL_0001..IL_0079 minus MakeNoise (IL_007c..).
//   - Glass      = LockedWindow.UnlockWindow IL_0010..IL_00b8 / UnlockWindowNoAlarm / BrokeWindow IL_0015..IL_0164 minus
//                  CheckAlarm, AlertNPCsOnPossesion, MakeNoise and Mission_Complete.
//   - hasAlarm is cleared before any retag: Lockpick.Update would otherwise start a police chase for THIS player when the
//     lock's own tag becomes "Door" (the actor's original path already consumed that alarm).
using System;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class WorldObjectBridge
    {
        public static bool Applying;   // the hooks ignore calls made while the bridge applies a replica
        public readonly bool Ok;
        public readonly Type DoorT, LockpickT, WindowT, MissionItemT;

        // Door
        readonly Func<object, bool> _open, _opening, _canUse, _broken, _slowly, _rotateOpen, _useVector, _moveOpen;
        readonly Func<object, bool>[] _special;
        readonly Action<object, bool> _setOpen, _setByPlayer, _setOpening, _setCanUse, _setBroken, _setRequiresKey, _setWasLocked;
        readonly Func<object, bool> _wasLocked;
        readonly Action<object, float> _setSpeed; readonly Func<object, float> _speedMul, _maxVolume, _slowMul;
        readonly Func<object, AudioSource> _sound; readonly Func<object, AudioClip> _clipOpen, _clipClose, _clipSqueak, _clipUnlock;
        readonly Func<object, Quaternion> _saveRot; readonly Func<object, Vector3> _saveRotVec, _savePos;
        readonly Func<object, string> _tagAfterUnlock;
        readonly Action<object> _openOnStart, _resett, _openDestroyed;
        // Lockpick
        readonly Func<object, object> _lpDoors; readonly Func<object, bool> _lpPadlock, _lpDisableCollider, _lpStory, _lpTut; readonly Func<object, string> _lpStoryName;
        readonly Action<object, bool> _lpSetAlarm; readonly Func<object, GameObject> _lpDust; readonly Action<object> _relockDoor;
        // LockedWindow
        readonly Func<object, GameObject[]> _windows; readonly Func<object, bool> _noDoor, _unconvex, _instGlass, _brokeGlass, _lwMission, _lwTut;
        readonly Action<object, bool> _setBrokeGlass, _lwSetAlarm; readonly Func<object, GameObject> _enterables;

        public WorldObjectBridge(InstanceLog log)
        {
            DoorT = AccessTools.TypeByName("Door"); LockpickT = AccessTools.TypeByName("Lockpick"); WindowT = AccessTools.TypeByName("LockedWindow"); MissionItemT = AccessTools.TypeByName("Mission_Item");
            var d = DoorT;
            _open = Fast.Getter<bool>(d, "isOpen"); _opening = Fast.Getter<bool>(d, "opening"); _canUse = Fast.Getter<bool>(d, "canUse"); _broken = Fast.Getter<bool>(d, "brokeHinge"); _slowly = Fast.Getter<bool>(d, "openingSlowly");
            _rotateOpen = Fast.Getter<bool>(d, "rotateOpen"); _useVector = Fast.Getter<bool>(d, "useVectorRotation"); _moveOpen = Fast.Getter<bool>(d, "moveOpen");
            // W4.2B.1: gates are entrance doors with the same Door mechanics (lock-gated, crowbar, slow use) and are shared now;
            // a lock of a gate was shared while its leaf stayed local (remote saw the lock open but no swing). Car doors
            // (M5.0 vdoor) and furniture (drawers, cupboards, fridges, cabinets, containers: W4.2C) stay local.
            var names = SpecialNames;
            _special = new Func<object, bool>[names.Length];
            bool specialOk = true;
            for (int i = 0; i < names.Length; i++) { _special[i] = Fast.Getter<bool>(d, names[i]); if (_special[i] == null) specialOk = false; }
            _setOpen = Fast.Setter<bool>(d, "isOpen"); _setByPlayer = Fast.Setter<bool>(d, "openedByPlayer"); _setOpening = Fast.Setter<bool>(d, "opening"); _setCanUse = Fast.Setter<bool>(d, "canUse");
            _setBroken = Fast.Setter<bool>(d, "brokeHinge"); _setRequiresKey = Fast.Setter<bool>(d, "requires_key"); _setWasLocked = Fast.Setter<bool>(d, "was_locked"); _wasLocked = Fast.Getter<bool>(d, "was_locked");
            _setSpeed = Fast.Setter<float>(d, "openSpeed"); _speedMul = Fast.Getter<float>(d, "speedMultiplier"); _maxVolume = Fast.Getter<float>(d, "maxVolume"); _slowMul = Fast.Getter<float>(d, "slowOpenSpeedMulti");
            _sound = Fast.Getter<AudioSource>(d, "DoorSound"); _clipOpen = Fast.Getter<AudioClip>(d, "DoorOpen"); _clipClose = Fast.Getter<AudioClip>(d, "DoorClose");
            _clipSqueak = Fast.Getter<AudioClip>(d, "DoorSqueak"); _clipUnlock = Fast.Getter<AudioClip>(d, "unlockSound");
            _saveRot = Fast.Getter<Quaternion>(d, "SaveRot"); _saveRotVec = Fast.Getter<Vector3>(d, "SaveRotVector"); _savePos = Fast.Getter<Vector3>(d, "SavePos");
            _tagAfterUnlock = Fast.Getter<string>(d, "set_tag_after_unlock");
            _openOnStart = Fast.Call0(d, "OpenOnStartt"); _resett = Fast.Call0(d, "Resett"); _openDestroyed = Fast.Call0(d, "OpenDestroyed");
            var lp = LockpickT;
            _lpDoors = Fast.Getter<object>(lp, "unlockDoor"); _lpPadlock = Fast.Getter<bool>(lp, "isPadlock"); _lpDisableCollider = Fast.Getter<bool>(lp, "Disable_This_Collider");
            _lpStory = Fast.Getter<bool>(lp, "on_unlock_advance_story"); _lpStoryName = Fast.Getter<string>(lp, "story_name"); _lpTut = Fast.Getter<bool>(lp, "is_tutorial");
            _lpSetAlarm = Fast.Setter<bool>(lp, "hasAlarm"); _lpDust = Fast.Getter<GameObject>(lp, "SpawnDust"); _relockDoor = Fast.Call0(lp, "RelockDoor");
            var lw = WindowT;
            _windows = Fast.Getter<GameObject[]>(lw, "Windows"); _noDoor = Fast.Getter<bool>(lw, "noDoor"); _unconvex = Fast.Getter<bool>(lw, "unconvexCollider"); _instGlass = Fast.Getter<bool>(lw, "instantiateGlass");
            _brokeGlass = Fast.Getter<bool>(lw, "brokeGlass"); _setBrokeGlass = Fast.Setter<bool>(lw, "brokeGlass"); _lwSetAlarm = Fast.Setter<bool>(lw, "hasAlarm"); _enterables = Fast.Getter<GameObject>(lw, "enterablesEnable");
            _lwMission = Fast.Getter<bool>(lw, "complete_mission"); _lwTut = Fast.Getter<bool>(lw, "isTutorial");
            Ok = specialOk && _open != null && _opening != null && _canUse != null && _broken != null && _slowly != null && _rotateOpen != null && _useVector != null && _moveOpen != null
                && _setOpen != null && _setByPlayer != null && _setOpening != null && _setCanUse != null && _setBroken != null && _setRequiresKey != null && _setWasLocked != null && _wasLocked != null
                && _setSpeed != null && _speedMul != null && _maxVolume != null && _slowMul != null && _sound != null && _clipOpen != null && _clipClose != null && _clipSqueak != null && _clipUnlock != null
                && _saveRot != null && _saveRotVec != null && _savePos != null && _tagAfterUnlock != null && _openOnStart != null && _resett != null && _openDestroyed != null
                && _lpDoors != null && _lpPadlock != null && _lpDisableCollider != null && _lpStory != null && _lpStoryName != null && _lpTut != null && _lpSetAlarm != null && _lpDust != null && _relockDoor != null
                && _windows != null && _noDoor != null && _unconvex != null && _instGlass != null && _brokeGlass != null && _setBrokeGlass != null && _lwSetAlarm != null && _enterables != null
                && _lwMission != null && _lwTut != null;
            if (!Ok) log.Error("WOBJ original Door / Lockpick / LockedWindow members not found: shared doors, locks and windows disabled");
        }

        // ---------------- readers ----------------
        public bool DoorOpen(Component d) { return _open(d); }
        public bool DoorBroken(Component d) { return _broken(d); }
        public bool DoorMoving(Component d) { return _opening(d); }
        public bool DoorSlow(Component d) { return _slowly(d); }
        public static bool DoorUsable(Component d) { return d.CompareTag("Door"); }
        public byte DoorBits(Component d) { return (byte)((_open(d) ? ObjectStateRules.DoorOpen : 0) | (_broken(d) ? ObjectStateRules.DoorBroken | ObjectStateRules.DoorOpen : 0)); }
        // Ordinary = not a car door / drawer / cupboard / fridge / cabinet / container (W4.2C); gates and window sashes included.
        static readonly string[] SpecialNames = { "isCarDoor", "isDrawer", "isCupboard", "isFridge", "isFileCabinet", "isGlassCabinet", "isContainer" };
        public bool Ordinary(Component d) { for (int i = 0; i < _special.Length; i++) if (_special[i](d)) return false; return true; }
        // The type flag that keeps a door local (diagnostics), null = shared type.
        public string LocalOnlyType(Component d) { for (int i = 0; i < _special.Length; i++) if (_special[i](d)) return SpecialNames[i]; return null; }

        // Lock state readable from the scene (late census / region start): fallen padlock or doors marked was_locked.
        public byte ReadLockState(Component lp)
        {
            if (_lpPadlock(lp)) { var rb = lp.GetComponent<Rigidbody>(); if (rb != null && !rb.isKinematic) return ObjectStateRules.LockPadlock; }
            var doors = _lpDoors(lp) as Array;
            if (doors != null) foreach (var o in doors) { var d = o as Component; if (d != null && _wasLocked(d)) return ObjectStateRules.LockUnlocked; }
            return ObjectStateRules.LockLocked;
        }

        // Story locks / mission windows (fail closed when the metadata is unreadable).
        public bool StoryCritical(ObjectKind k, Component c)
        {
            if (c == null) return true;
            if (k == ObjectKind.Lockpick) return _lpStory(c) || !string.IsNullOrEmpty(_lpStoryName(c)) || _lpTut(c);
            if (k == ObjectKind.LockedWindow) return _lwMission(c) || _lwTut(c) || (MissionItemT != null && c.GetComponent(MissionItemT) != null);
            return false;
        }

        // ---------------- doors ----------------
        // Quick swing = Door.Use IL_0031..IL_0098 + clip. Slow swing (W4.2B.1) = the original hold-E motion of Door.Update:
        // the replica keeps openingSlowly FALSE (the slow branch writes the local HUD fill PS.SlowOpen and, with canUse, the
        // LOCAL player's mouse sensitivity / canMove) and instead drives openSpeed every frame with the original profile
        // (SlowTick): openSpeed2 ramps by 45 x slowOpenSpeedMulti per second toward 17 x speedMultiplier; once slowMoving
        // (+0.85 x slowOpenSpeedMulti per second) passes 0.8 the target becomes 70 x speedMultiplier (IL_018e..IL_01f2).
        // Door.Update's own fast ramp (>= 100/s) follows that target within a frame. Squeak = IL_012b..IL_0188.
        public void Swing(Component d, bool open, bool slow)
        {
            float mul = _speedMul(d);
            _setOpen(d, open); _setByPlayer(d, open); _setOpening(d, true);
            _setCanUse(d, false);
            var src = _sound(d);
            if (slow)
            {
                _setSpeed(d, 0.5f * mul);   // SlowTick raises it from the first frame on
                StartSlow(d);
                var sq = _clipSqueak(d);
                if (src != null && sq != null) { src.volume = 0.3f; src.pitch = UnityEngine.Random.Range(0.9f, 1.1f); src.clip = sq; src.Play(); }
                return;
            }
            _setSpeed(d, 150f * mul);
            var clip = open ? _clipOpen(d) : _clipClose(d);
            if (src != null && clip != null) { src.volume = _maxVolume(d); src.pitch = 1f; src.PlayOneShot(clip); }
        }

        // The actor released E while its swing was still slow (original Door.SlowFailed IL_0065..IL_016a, without the
        // actor's HUD / mouse / movement / noise effects): finish at 150 x speedMultiplier with the loud clip.
        public void FinishFast(Component d)
        {
            StopSlow(d);
            _setCanUse(d, false); _setSpeed(d, 150f * _speedMul(d));
            var src = _sound(d);
            if (src == null) return;
            src.volume = _maxVolume(d); src.pitch = 1f; src.Stop();
            var clip = _open(d) ? _clipOpen(d) : _clipClose(d);
            if (clip != null) src.PlayOneShot(clip);
        }

        sealed class SlowSwing { public Component Door; public float T0; }
        readonly List<SlowSwing> _slow = new List<SlowSwing>(4);
        public int SlowCount { get { return _slow.Count; } }
        public bool IsSlowReplica(Component d) { for (int i = 0; i < _slow.Count; i++) if (ReferenceEquals(_slow[i].Door, d)) return true; return false; }
        void StartSlow(Component d)
        {
            StopSlow(d);
            if (_slow.Count >= 8) _slow.RemoveAt(0);   // bounded: older replicas keep their last speed
            _slow.Add(new SlowSwing { Door = d, T0 = Time.time });
        }
        void StopSlow(Component d) { for (int i = _slow.Count - 1; i >= 0; i--) if (ReferenceEquals(_slow[i].Door, d)) _slow.RemoveAt(i); }
        public void ClearSlow() { _slow.Clear(); }

        // Per frame while replica slow swings exist (<= 8): one speed write each; ends when Door.Update finished the swing
        // (Resett: opening = false - also the original 3 s safety, which applies to the actor's door in the same way).
        public void SlowTick()
        {
            float now = Time.time;
            for (int i = _slow.Count - 1; i >= 0; i--)
            {
                var s = _slow[i]; var d = s.Door;
                if ((object)d == null || d == null || !_opening(d)) { _slow.RemoveAt(i); continue; }
                _setSpeed(d, SlowSpeed(now - s.T0, _slowMul(d), _speedMul(d)));
            }
        }

        // Original slow-swing speed target at time t after the slow start (see Swing); pure function, tested offline in spirit.
        public static float SlowSpeed(float t, float slowMul, float mul)
        {
            if (slowMul <= 0f) slowMul = 0.75f;
            float ramp = 45f * slowMul, t1 = 0.8f / (0.85f * slowMul);
            if (t < t1) return Mathf.Max(0.5f * mul, Mathf.Min(17f * mul, ramp * t));
            float v1 = Mathf.Min(17f * mul, ramp * t1);
            return Mathf.Min(70f * mul, v1 + ramp * (t - t1));
        }

        public void Snap(Component d, bool open)
        {
            StopSlow(d);
            Applying = true;
            try
            {
                if (open) { _openOnStart(d); return; }
                _setOpen(d, false); _setByPlayer(d, false);
                var t = d.transform;
                if (_rotateOpen(d)) t.localRotation = _saveRot(d);
                else if (_useVector(d)) { var v = _saveRotVec(d); t.localRotation = Quaternion.Euler(v.x, v.y, v.z); }
                else if (_moveOpen(d)) t.localPosition = _savePos(d);
                _resett(d);
            }
            finally { Applying = false; }
        }

        // W4.2B.1: the original crowbar break-in (PlayerScript.CrowbarBreakin IL_0117..IL_013f) always runs
        // Lockpick.DoorUnlock first, which enables the Door behaviour (IL_006d), then OpenDestroyed. A disabled Door never
        // runs Door.Update, so the replica would show the broken/open state without any swing: enable it like the original.
        public void Break(Component d, bool silent)
        {
            StopSlow(d);
            var b = d as Behaviour; if (b != null && !b.enabled) b.enabled = true;
            Applying = true;
            try
            {
                if (silent) { _openOnStart(d); _setBroken(d, true); }
                else _openDestroyed(d);
            }
            finally { Applying = false; }
        }

        // ---------------- locks ----------------
        public void Unlock(Component lp, byte state, bool silent)
        {
            _lpSetAlarm(lp, false);
            var doors = _lpDoors(lp) as Array;
            if (state == ObjectStateRules.LockKey)
            {
                if (doors != null) foreach (var o in doors)
                {
                    var d = o as Component; if (d == null) continue;
                    _setRequiresKey(d, false);
                    string tag = _tagAfterUnlock(d); d.transform.tag = string.IsNullOrEmpty(tag) ? "Untagged" : tag;
                    if (!silent) { var src = _sound(d); var clip = _clipUnlock(d); if (src != null && clip != null) src.PlayOneShot(clip); }
                }
                return;
            }
            if (state == ObjectStateRules.LockPadlock)
            {
                var rb = lp.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = false;
                var bc = lp.GetComponent<BoxCollider>(); if (bc != null) bc.enabled = false;
                var dust = _lpDust(lp); if (!silent && dust != null) UnityEngine.Object.Instantiate(dust, lp.transform.position, lp.transform.rotation);
                lp.gameObject.tag = "Untagged";
                if (doors != null) foreach (var o in doors) { var d = o as Component; if (d != null) d.transform.tag = "Door"; }
                return;
            }
            lp.transform.tag = "Untagged";
            if (doors != null) foreach (var o in doors)
            {
                var d = o as Component; if (d == null) continue;
                var b = d as Behaviour; if (b != null) b.enabled = true;
                _setWasLocked(d, true); d.transform.tag = "Door"; _setRequiresKey(d, false);
            }
            if (_lpDisableCollider(lp)) { var bc = lp.GetComponent<BoxCollider>(); if (bc != null) bc.enabled = false; }
        }

        public void Relock(Component lp)
        {
            Applying = true;
            try { _relockDoor(lp); }
            finally { Applying = false; }
        }

        // ---------------- windows ----------------
        public void Glass(Component lw, byte glass, bool silent)
        {
            _lwSetAlarm(lw, false);
            var w = _windows(lw);
            var g0 = w != null && w.Length > 0 ? w[0] : null;
            if (glass == ObjectStateRules.GlassBroken)
            {
                if (_brokeGlass(lw)) return;
                _setBrokeGlass(lw, true);
                if (g0 != null) g0.SetActive(false);
                var g1 = w != null && w.Length > 1 ? w[1] : null;
                if (g1 != null)
                {
                    if (_instGlass(lw)) { if (g0 != null) { var shards = UnityEngine.Object.Instantiate(g1, g0.transform.position, g0.transform.rotation); shards.transform.localScale = g0.transform.localScale; } }
                    else g1.SetActive(true);
                }
                OpenSash(lw, false);
                Unconvex(lw);
                if (g0 != null) UnityEngine.Object.Destroy(g0);
                UnityEngine.Object.Destroy(lw);
                return;
            }
            if (g0 != null) g0.SetActive(false);
            OpenSash(lw, true);
            Unconvex(lw);
            if (glass == ObjectStateRules.GlassInside && g0 != null) g0.SetActive(true);   // opened from inside: the glass stays
            UnityEngine.Object.Destroy(lw);
        }

        void OpenSash(Component lw, bool setLayer)
        {
            if (_noDoor(lw)) { var e = _enterables(lw); if (e != null) e.SetActive(true); return; }
            var d = lw.GetComponent(DoorT) as Behaviour;
            if (d == null) return;
            d.enabled = true;
            if (setLayer) lw.gameObject.layer = 13;
            lw.gameObject.tag = "Door";
        }

        void Unconvex(Component lw) { if (_unconvex(lw)) { var mc = lw.GetComponent<MeshCollider>(); if (mc != null) mc.convex = false; } }
    }
}
