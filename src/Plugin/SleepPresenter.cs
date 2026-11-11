// M4.3.3 co-op sleep presentation on the ORIGINAL PassTime system (verified IL, CODEBASE_MAP §3.12 "M4.3.3"):
//   PassTime.Begin(Transform)  : main camera off, sleep camera cam_object at the given point, HUD (MainCanvas) hidden,
//                                passingTimeOn, hour picker UI (mainPassUI)
//   PassTime.SetSleepValue()   : currentHr = slider value (target hour)
//   PassTime.PassStart()       : canInteract off, passingTime, ticking sound, blur, time-passing UI
//   PassTime.Update            : while passingTime: day cycle 0.2 min + wave/flash filters until GameManager.currentHour ==
//                                currentHr, then restores sound/cycle/UI (passingTime = false)
//   PassTime.StopIt()          : the same restore when stopped early
//   PassTime.QuitIt()          : back to the game; dereferences the bed or PS.current_vehicle.character_sleeping_Object, so it
//                                is only valid for a bed / car driver - the observer uses its exact restore steps without that
//                                (mirrored IL: DelayTurnOnExtra, InteractDelayed(0.5), mainCamera on, cam_object off,
//                                passingTimeOn off, main_object off, MainCanvas alpha 1, PCB on, cursorLock, interior PP,
//                                fadeSleep 0, AudioListener volume 1).
// The OTHER player in the sleeper's region sees this genuine presentation from its current view (sleep camera placed at the
// camera it was looking through). Clock authority is unchanged: on the host the original PassTime fast-forward IS the region
// clock (host watched, no second advance); on the client the follower is paused while it runs and DayNightTime's hourly tick
// is suppressed (the host owns the economy ticks). Not started (and the previous behaviour kept) when the observer is busy:
// paused / in a UI / its own sleep / a moving car it drives / PassTime unavailable.
using System;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class SleepPresenter
    {
        public static SleepPresenter Instance;
        public static Func<bool> PassengerSeated;    // VehicleSync: the local player sits in a passenger seat (mod adapter)
        public static bool Observing { get { var i = Instance; return i != null && i._active; } }
        public static bool SuppressTicks { get { var i = Instance; return i != null && i._active && !i._host; } }

        readonly InstanceLog _log; readonly bool _host; Harmony _h;
        Type _ptT; bool _ok;
        Func<object, object> _ptMainObj, _ptCamObj, _ptMainCam, _ptFade, _ptSlider, _psMainCanvas, _psPCB, _psPMB, _psVehicle, _psGM;
        Func<object, bool> _ptPassing, _ptCanSleep, _psPassingOn, _psInVehicle;
        Action<object, Transform> _ptBegin; Action<object> _ptSetValue, _ptStart, _ptStop, _psDelayExtra;
        Action<object, bool> _psSetPassingOn, _psSetCursor, _psSetInteriorPP, _pmbSetCanMove; Action<object, float> _psInteractDelayed;
        Func<object, int> _gmHour; Func<object, int> _dnPrev; Action<object, int> _dnSetPrev; Func<object, bool> _pmbCanMove;
        PropertyInfo _sliderValue; Type _evpInputT;

        // running presentation
        bool _active; uint _eventId; object _pt, _ps; float _stopAt = float.NaN; double _startedAt;
        bool _pcbWas, _canMoveWas; Behaviour _extraCam, _carInput; string _context;
        public long Presented, Skipped;

        public SleepPresenter(InstanceLog log, bool host)
        {
            _log = log; _host = host; Instance = this;
            try
            {
                _ptT = AccessTools.TypeByName("PassTime"); var ps = AccessTools.TypeByName("PlayerScript"); var pmb = AccessTools.TypeByName("PlayerMotorBehavior");
                var gm = AccessTools.TypeByName("GameManager"); var dn = AccessTools.TypeByName("DayNightTime");
                _ptMainObj = Fast.Getter<object>(_ptT, "main_object"); _ptCamObj = Fast.Getter<object>(_ptT, "cam_object"); _ptMainCam = Fast.Getter<object>(_ptT, "mainCamera");
                _ptFade = Fast.Getter<object>(_ptT, "fadeSleep"); _ptSlider = Fast.Getter<object>(_ptT, "timeSlider"); _ptPassing = Fast.Getter<bool>(_ptT, "passingTime");
                _ptBegin = Fast.Call<Transform>(_ptT, "Begin"); _ptSetValue = Fast.Call0(_ptT, "SetSleepValue"); _ptStart = Fast.Call0(_ptT, "PassStart"); _ptStop = Fast.Call0(_ptT, "StopIt");
                var can = AccessTools.Method(_ptT, "CheckIfCanSleepAnymore", Type.EmptyTypes);
                if (can != null && can.ReturnType == typeof(bool))
                {
                    var p = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
                    _ptCanSleep = System.Linq.Expressions.Expression.Lambda<Func<object, bool>>(System.Linq.Expressions.Expression.Call(System.Linq.Expressions.Expression.Convert(p, _ptT), can), p).Compile();
                }
                var sf = AccessTools.Field(_ptT, "timeSlider"); _sliderValue = sf != null ? AccessTools.Property(sf.FieldType, "value") : null;
                _psMainCanvas = Fast.Getter<object>(ps, "MainCanvas"); _psPCB = Fast.Getter<object>(ps, "PCB"); _psPMB = Fast.Getter<object>(ps, "PMB"); _psVehicle = Fast.Getter<object>(ps, "current_vehicle");
                _psPassingOn = Fast.Getter<bool>(ps, "passingTimeOn"); _psSetPassingOn = Fast.Setter<bool>(ps, "passingTimeOn"); _psInVehicle = Fast.Getter<bool>(ps, "is_in_vehicle");
                _psSetCursor = Fast.Setter<bool>(ps, "cursorLock"); _psSetInteriorPP = Fast.Setter<bool>(ps, "deactivate_interior_PP"); _psInteractDelayed = Fast.Call<float>(ps, "InteractDelayed"); _psDelayExtra = Fast.Call0(ps, "DelayTurnOnExtra");
                _pmbSetCanMove = Fast.Setter<bool>(pmb, "canMove"); _pmbCanMove = Fast.Getter<bool>(pmb, "canMove");
                _gmHour = Fast.Getter<int>(gm, "currentHour"); _psGM = Fast.Getter<object>(gm, "PS"); _dnPrev = Fast.Getter<int>(dn, "prevHour"); _dnSetPrev = Fast.Setter<int>(dn, "prevHour");
                _evpInputT = AccessTools.TypeByName("EVP.VehicleStandardInput");
                _ok = _ptT != null && _ptMainObj != null && _ptCamObj != null && _ptMainCam != null && _ptPassing != null && _ptBegin != null && _ptSetValue != null && _ptStart != null && _ptStop != null
                    && _sliderValue != null && _psMainCanvas != null && _psPCB != null && _psPassingOn != null && _psSetPassingOn != null && _psSetCursor != null && _psInteractDelayed != null;
                if (!_ok) { _log.Warn("SLEEP_SHOW PassTime accessors missing: the other player's sleep is not presented (clock sync unchanged)"); return; }
                if (!_host && dn != null && _dnSetPrev != null && _gmHour != null)
                {
                    var up = AccessTools.Method(dn, "Update", Type.EmptyTypes);
                    if (up != null) { _h = new Harmony("com.ts2mp.multiplayer.sleepshow"); _h.Patch(up, new HarmonyMethod(AccessTools.Method(typeof(SleepPresenter), "DayNightPrefix"))); }
                }
            }
            catch (Exception ex) { _log.Error("SLEEP_SHOW init failed (presentation disabled)", ex); _ok = false; }
        }

        public bool Active { get { return _active; } }
        public uint EventId { get { return _eventId; } }

        // DayNightTime.Update prefix (client, only while presenting): the hour it compares against is the current one, so the
        // original fast-forward on the client produces no local rent/abilities/day/bank tick (the host owns them).
        static object _gmForTicks;
        static void DayNightPrefix(object __instance)
        {
            var i = Instance;
            if (i == null || !SuppressTicks || i._ps == null) return;
            try
            {
                if (_gmForTicks == null) return;
                int h = i._gmHour(_gmForTicks);
                if (i._dnPrev(__instance) != h) i._dnSetPrev(__instance, h);
            }
            catch { }
        }

        // Starts the presentation for an accepted sleep of the other player. False = not possible now (logged; the caller keeps
        // the previous clock behaviour).
        public bool HasStopAt { get { return !float.IsNaN(_stopAt); } }

        public bool TryStart(object gm, int targetHour, uint eventId, out string why)
        {
            why = null;
            var ps = gm != null && _psGM != null ? _psGM(gm) : null;
            if (!_ok) { why = "PassTime unavailable"; return false; }
            if (_active) { why = "already presenting"; return false; }
            if (ps == null || gm == null) { why = "no player"; return false; }
            if (Time.timeScale <= 0f) { why = "game paused / UI open"; return false; }
            if (_psPassingOn(ps)) { why = "own sleep UI open"; return false; }
            var pt = FindPassTime();
            if (pt == null) { why = "no active PassTime object"; return false; }
            if (_ptPassing(pt)) { why = "PassTime already running"; return false; }
            if (_ptCanSleep != null && !_ptCanSleep(pt)) { why = "the game's own sleep rule refuses (survival energy)"; return false; }
            bool driver = _psInVehicle != null && _psInVehicle(ps) && _psVehicle != null && _psVehicle(ps) != null;
            Rigidbody rb = null;
            if (driver)
            {
                var car = _psVehicle(ps) as Component;
                rb = car != null ? car.GetComponent<Rigidbody>() : null;
                if (rb != null && !rb.isKinematic && rb.velocity.magnitude > 0.5f) { why = "driving a moving car"; return false; }
            }
            bool passenger = PassengerSeated != null && PassengerSeated();
            // the camera the player is looking through (main camera, or the TPP vehicle camera)
            var mainCam = _ptMainCam(pt) as Camera;
            Camera view = null; float depth = float.MinValue;
            foreach (var c in Camera.allCameras) if (c != null && c.enabled && c.gameObject.activeInHierarchy && c.depth >= depth && !c.targetTexture) { view = c; depth = c.depth; }
            if (view == null) view = mainCam;
            if (view == null) { why = "no camera"; return false; }

            _pt = pt; _ps = ps; _eventId = eventId; _stopAt = float.NaN; _startedAt = Time.realtimeSinceStartup; _gmForTicks = gm;
            var pcb = _psPCB(ps) as Behaviour; _pcbWas = pcb != null && pcb.enabled;
            var pmb = _psPMB(ps);
            _canMoveWas = pmb != null && _pmbCanMove != null && _pmbCanMove(pmb);
            _extraCam = view != mainCam ? view : null;
            _carInput = null;
            try
            {
                if (driver && _evpInputT != null)
                {
                    var car = _psVehicle(ps) as Component;
                    var inp = car != null ? car.GetComponentInChildren(_evpInputT) as Behaviour : null;
                    if (inp != null && inp.enabled) { inp.enabled = false; _carInput = inp; }   // no driving under the sleep camera
                }
                if (!driver && !passenger && pmb != null && _pmbSetCanMove != null) _pmbSetCanMove(pmb, false);   // on foot: stand still like in a bed
                _ptBegin(pt, view.transform);
                if (_extraCam != null) _extraCam.enabled = false;     // the sleep camera replaces the TPP camera too
                _sliderValue.SetValue(_ptSlider(pt), (float)targetHour, null);
                _ptSetValue(pt);
                SleepHooks.Observer = true;                          // not this player's own sleep (no sleep proposal)
                _ptStart(pt);
                if (!_ptPassing(pt)) { why = "PassStart refused"; End("PassStart refused", false); Skipped++; return false; }
            }
            catch (Exception ex) { _log.Error("SLEEP_SHOW start failed", ex); End("start failed", false); why = "start failed"; Skipped++; return false; }
            _active = true; Presented++;
            _context = driver ? "driver" : passenger ? "passenger" : "on foot";
            _log.Info("SLEEP_SHOW presenting the other player's sleep with the original PassTime (event " + eventId + ", until hour " + targetHour + ", " + _context + ")");
            return true;
        }

        // Host observer: the sleeper stopped early -> stop when the local clock reaches the sleeper's end (forward only).
        public void StopAtHours(float hours) { if (_active) _stopAt = hours; }

        // Per frame while presenting. Returns true when it ended this frame.
        public bool Frame(float localHours)
        {
            if (!_active) return false;
            var o = _pt as UnityEngine.Object;
            if (o == null || _ps as UnityEngine.Object == null) { End("PassTime / player destroyed (scene change)", false); return true; }
            if (!_ptPassing(_pt)) { End("target hour reached", true); return true; }
            if (!float.IsNaN(_stopAt)) { float d = TS2MP.Shared.TimeMath.ForwardDiff(_stopAt, localHours); if (d > 12f || d < 0.02f) { Stop("sleeper's end time reached"); return true; } }
            if (Time.realtimeSinceStartup - _startedAt > 120) { Stop("safety timeout"); return true; }
            return false;
        }

        public void Stop(string why)
        {
            if (!_active && _pt == null) return;
            try { if (_pt != null && _ptPassing(_pt)) _ptStop(_pt); } catch (Exception ex) { _log.Warn("SLEEP_SHOW StopIt failed: " + ex.Message); }
            End(why, true);
        }

        void End(string why, bool wasRunning)
        {
            var pt = _pt; var ps = _ps;
            _active = false; _pt = null; _ps = null; _stopAt = float.NaN;
            SleepHooks.Observer = false;
            try
            {
                if (ps as UnityEngine.Object != null)
                {
                    if (_psDelayExtra != null) _psDelayExtra(ps);
                    if (!(PassengerSeated != null && PassengerSeated())) _psInteractDelayed(ps, 0.5f);   // a seated passenger keeps interaction off
                    _psSetPassingOn(ps, false);
                    var mc = _psMainCanvas(ps) as GameObject; var cg = mc != null ? mc.GetComponent<CanvasGroup>() : null; if (cg != null) cg.alpha = 1f;
                    var pcb = _psPCB(ps) as Behaviour; if (pcb != null) pcb.enabled = _pcbWas;
                    _psSetCursor(ps, true);
                    if (_psSetInteriorPP != null) _psSetInteriorPP(ps, false);
                    var pmb = _psPMB(ps); if (pmb != null && _pmbSetCanMove != null && _pmbCanMove != null && !_pmbCanMove(pmb) && _canMoveWas) _pmbSetCanMove(pmb, true);
                }
                if (pt as UnityEngine.Object != null)
                {
                    var cam = _ptMainCam(pt) as Behaviour; if (cam != null) cam.enabled = true;
                    var co = _ptCamObj(pt) as GameObject; if (co != null) co.SetActive(false);
                    var mo = _ptMainObj(pt) as GameObject; if (mo != null) mo.SetActive(false);
                    var fade = _ptFade != null ? _ptFade(pt) as CanvasGroup : null; if (fade != null) fade.alpha = 0f;
                }
                if (_extraCam != null) _extraCam.enabled = true;
                if (_carInput != null) _carInput.enabled = true;
                AudioListener.volume = 1f;
            }
            catch (Exception ex) { _log.Error("SLEEP_SHOW restore failed", ex); }
            _extraCam = null; _carInput = null;
            if (pt != null) _log.Info("SLEEP_SHOW ended (" + why + ", " + _context + ") after " + (Time.realtimeSinceStartup - _startedAt).ToString("0.0") + " s");
        }

        object FindPassTime()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(_ptT))
            {
                var b = o as Behaviour;
                if (b != null && b.gameObject.scene.IsValid() && b.isActiveAndEnabled) return o;
            }
            return null;
        }

        public void Dispose()
        {
            if (_active) Stop("shutdown");
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
