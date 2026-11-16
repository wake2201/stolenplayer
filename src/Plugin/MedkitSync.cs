// V1.4.1: shared single-use car medkit on the ORIGINAL objects and code path (design + verified IL: src/Shared/Medkit.cs).
//   Bridge (one narrowly scoped transpiler on PlayerScript.Update): the original use sequence of the medkit branch
//     IL_72f0..IL_7328  ldarg.0; call Heal(); <hit>.GetComponent<Medkit>().healSound.Play(); Object.Destroy(<hit>.gameObject);
//                       ldstr "Medkit"; ldc.i4.1; call SPrefs::SetInt
//   is replaced by  ldarg.0; ldloca.s <hit>; call MedkitSync.OriginalUse(object, ref RaycastHit). Everything before it (tag
//   branch, highlight, E, the isBleeding check, the "not_bleeding" warning) stays the original's.
//   - no session / host: the hook runs exactly the replaced sequence (same calls, same order); the host then commits "medkit".
//   - client: the use becomes the host proposal "medkitUse" (arg = car id); accepted -> the same original sequence runs here.
//   - a medkit of a car that is only a travel proxy on this PC: nothing happens (also its prompt: Medkit.Update postfix keeps
//     the proxy's medkit Untagged); the host refuses it too (car region != requester region).
//   Passenger seat (VehicleSync.SeatInteraction -> SeatUse): first person, original 2.25 m ray on the medkit, reachable by
//   MedkitRules.SeatReachable; the original bleeding check / warning, then the same use path.
//   Committed state "medkit" used -> every medkit object on this PC is removed (original Medkit.Start rule for key 1) and the
//   client's sandbox key follows the host (the host's own key is written by the original / Apply).
// Cost: no per-frame work except one float compare per medkit in the Medkit.Update postfix (2 objects); FindObjectsOfType
// only on a committed state change. Network: 1 B proposal per use, 4 B state per change.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class MedkitSync : IDisposable, IMedkitHost
    {
        public static MedkitSync Instance;

        sealed class Pending { public uint Id; public object Ps; public GameObject Go; public ushort Car; public bool Seat; public double At; }

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        static Type _medkitT;
        static Action<object> _heal; static Func<object, bool> _bleeding; static Func<object, object> _warner; static MethodInfo _spawnWarning;
        static Func<object, object> _healSound, _myCar; static Func<object, float> _check;
        static Func<string, int> _prefInt; static Action<string, int> _prefSet;
        static int _sites;

        Pending _pend; bool _removeDeferred;
        object _boundGm; uint _seenHead = uint.MaxValue, _seenEpoch; MedkitState _committed; bool _hasCommitted;
        long _uses, _requests, _accepted, _refused, _proxyRefused, _removed, _sandboxWrites;
        const double Timeout = 8;
        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool Ready { get { return _ok; } }

        public MedkitSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var ps = AccessTools.TypeByName("PlayerScript"); var sp = AccessTools.TypeByName("SPrefs"); var ws = AccessTools.TypeByName("WarningTextSpawner");
                _medkitT = AccessTools.TypeByName("Medkit");
                var update = ps != null ? AccessTools.Method(ps, "Update", Type.EmptyTypes) : null;
                var mkUpdate = _medkitT != null ? AccessTools.Method(_medkitT, "Update", Type.EmptyTypes) : null;
                _heal = Fast.Call0(ps, "Heal"); _bleeding = Fast.Getter<bool>(ps, "isBleeding"); _warner = Fast.Getter<object>(ps, "warningSpawner");
                _spawnWarning = ws != null ? AccessTools.Method(ws, "SpawnWarning", new[] { typeof(string) }) : null;
                _healSound = Fast.Getter<object>(_medkitT, "healSound"); _myCar = Fast.Getter<object>(_medkitT, "myCar"); _check = Fast.Getter<float>(_medkitT, "check");
                var gi = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null; var si = sp != null ? AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) }) : null;
                if (gi != null) _prefInt = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                if (si != null) _prefSet = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), si);
                if (update == null || mkUpdate == null || _heal == null || _bleeding == null || _warner == null || _spawnWarning == null || _healSound == null || _myCar == null || _check == null || _prefInt == null || _prefSet == null)
                { _log.Warn("MEDKIT game API incomplete: shared medkit disabled (the original medkit stays local on this PC)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.medkit");
                _sites = 0;
                _h.Patch(update, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MedkitSync), "UseTranspiler")));
                if (_sites != 1)
                {
                    _h.UnpatchSelf(); _h = null;
                    _log.Warn("MEDKIT original use sequence not found exactly once in PlayerScript.Update (found " + _sites + "): shared medkit disabled (the original medkit stays local on this PC)");
                    return;
                }
                _h.Patch(mkUpdate, postfix: new HarmonyMethod(AccessTools.Method(typeof(MedkitSync), "TagPostfix")));
                if (_host && world.Host != null) world.Host.MedkitHost = this;
                node.RemoteDisconnected += OnDisconnected;
                world.ProposalResultHook10 = OnProposalResult;
                _ok = true; Instance = this;
                _log.Info("MEDKIT shared single-use medkit ready (" + (_host ? "host: original use + Player-2 transactions" : "client: uses are host transactions") + ", PlayerScript.Update use sequence bridged at 1 site)");
            }
            catch (Exception ex) { _log.Error("MEDKIT init failed: shared medkit disabled", ex); _ok = false; if (_h != null) { _h.UnpatchSelf(); _h = null; } }
        }

        // ---- transpiler: the replaced original sequence (see header); any other shape -> no change (_sites != 1 -> disabled) ----
        static IEnumerable<CodeInstruction> UseTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return MedkitIL.Rewrite(new List<CodeInstruction>(instructions), AccessTools.Method(typeof(MedkitSync), "OriginalUse"), out _sites);
        }

        // Called in place of the original sequence (only reached with isBleeding true, after E on a tagged medkit).
        static void OriginalUse(object ps, ref RaycastHit hit)
        {
            var go = hit.transform != null ? hit.transform.gameObject : null;
            var i = Instance;
            if (i == null) { RunOriginal(ps, go); return; }
            try { i.Use(ps, go, false); }
            catch (Exception ex) { i._log.Error("MEDKIT use failed (nothing consumed)", ex); }
        }

        // The replaced original sequence, unchanged: Heal(); healSound.Play(); Object.Destroy(medkit); SPrefs.SetInt("Medkit", 1).
        static void RunOriginal(object ps, GameObject go)
        {
            _heal(ps);
            var mk = go != null ? go.GetComponent(_medkitT) : null;
            var snd = mk != null ? _healSound(mk) as AudioSource : null;
            if (snd != null) snd.Play();
            if (go != null) UnityEngine.Object.Destroy(go);
            _prefSet(MedkitRules.PrefKey, 1);
        }

        // Passenger seat (VehicleSync): the original bleeding check / warning first, then the shared use path.
        internal void SeatUse(object ps, GameObject go)
        {
            if (ps == null || go == null) return;
            if (!_bleeding(ps)) { var w = _warner(ps); if (w != null) _spawnWarning.Invoke(w, new object[] { "not_bleeding" }); return; }   // original warning key
            Use(ps, go, true);
        }

        void Use(object ps, GameObject go, bool seat)
        {
            var mk = go != null ? go.GetComponent(_medkitT) : null;
            ushort car = VehicleIds.None; bool proxy = false;
            var vs = VehicleSync.Current;
            if (mk != null && vs != null) vs.CarOf(_myCar(mk), out car, out proxy);
            if (proxy) { _proxyRefused++; _log.Info("MEDKIT " + (seat ? "seat " : "") + "use on a travel proxy (car#" + car + " is elsewhere): nothing happens"); return; }
            if (_host)
            {
                RunOriginal(ps, go);
                _uses++;
                _log.Info("MEDKIT used by P1" + (seat ? " from the passenger seat" : "") + " (car#" + car + "): original heal, medkit consumed");
                if (_world.Host != null && _world.Host.PublishMedkit(new MedkitState { Used = true, Car = car, By = 1 })) _world.FlushOutbox();
                return;
            }
            if (_pend != null) return;   // one use waits for the host
            if (car == VehicleIds.None || !_node.IsConnected || !_world.Synced) { _refused++; _log.Info("MEDKIT use ignored: " + (car == VehicleIds.None ? "medkit of an unknown car" : "not connected") + " (nothing consumed)"); return; }
            uint id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = car, Key = WorldKeys.MedkitUse, Value = new byte[] { 1 } });
            _pend = new Pending { Id = id, Ps = ps, Go = go, Car = car, Seat = seat, At = Now };
            _requests++;
            _log.Info("MEDKIT use of car#" + car + (seat ? " from the passenger seat" : "") + " requested #" + id + " (decided by the host)");
        }

        internal bool OnProposalResult(uint id, ProposalCode code)
        {
            if (_host || _pend == null || id != _pend.Id) return false;
            var p = _pend; _pend = null;
            if (code == ProposalCode.Accepted)
            {
                _accepted++;
                var u = p.Ps as UnityEngine.Object;
                if (u != null && u) { RunOriginal(p.Ps, p.Go != null ? p.Go : null); _uses++; }
                _log.Info("MEDKIT use #" + id + " accepted by the host: original heal, medkit consumed on both PCs");
            }
            else { _refused++; _log.Info("MEDKIT use #" + id + " refused by the host: " + code + " (nothing consumed here)"); }
            if (_removeDeferred) { _removeDeferred = false; RemoveAll("committed state"); }
            return true;
        }

        void OnDisconnected(string reason) { if (_pend != null) { _log.Info("MEDKIT use #" + _pend.Id + " dropped: disconnected (nothing consumed)"); _pend = null; } }

        // ---- per frame (Plugin.Update): scene bind (host publish), committed state (both), pending timeout ----
        public void Update()
        {
            if (!_ok) return;
            var gm = _world.GameManagerObject;
            if (_host && gm != null && !ReferenceEquals(gm, _boundGm))
            {
                _boundGm = gm;
                bool used;
                if (TryUsed(out used) && _world.Host != null)
                {
                    var s = used ? (_hasCommitted && _committed.Used ? _committed : new MedkitState { Used = true }) : new MedkitState();
                    if (_world.Host.PublishMedkit(s)) { _world.FlushOutbox(); _log.Info("MEDKIT host save state published at scene bind: " + s); }
                }
            }
            else if (gm == null) _boundGm = null;
            if (_pend != null && Now - _pend.At > Timeout) { _log.Info("MEDKIT use #" + _pend.Id + " timed out (nothing consumed)"); _pend = null; if (_removeDeferred) { _removeDeferred = false; RemoveAll("committed state"); } }
            ReadCommitted();
        }

        void ReadCommitted()
        {
            var reg = _world.Registry;
            if (reg.HeadRev == _seenHead && reg.Epoch == _seenEpoch) return;
            _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch;
            var e = reg.Get(StateScope.Session, 0, WorldKeys.Medkit);
            MedkitState s;
            if (e == null || !MedkitState.TryParse(e.Value, out s)) return;
            bool changed = !_hasCommitted || s.Used != _committed.Used;
            _committed = s; _hasCommitted = true;
            if (!changed) return;
            _log.Info("MEDKIT committed state: " + s);
            if (!_host)
            {
                int k = Pref(); int want = s.Used ? 1 : 0;
                if (k != want) { _prefSet(MedkitRules.PrefKey, want); _sandboxWrites++; }   // next original scene load follows the host (Medkit.Start)
            }
            if (s.Used) { if (_pend != null) _removeDeferred = true; else RemoveAll("committed state"); }
        }

        void RemoveAll(string why)
        {
            int n = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_medkitT)) { var c = o as Component; if (c != null) { UnityEngine.Object.Destroy(c.gameObject); n++; } }
            if (n > 0) { _removed += n; _log.Info("MEDKIT " + n + " medkit object(s) removed (" + why + ": single use per save, original Medkit.Start rule)"); }
        }

        static int Pref() { try { return _prefInt(MedkitRules.PrefKey); } catch { return 0; } }

        // IMedkitHost
        public bool TryUsed(out bool used)
        {
            used = false;
            var gm = _world.GameManagerObject;
            if (gm == null) return false;
            used = Pref() == 1; return true;
        }

        public bool Apply(byte slot, ushort car)
        {
            _prefSet(MedkitRules.PrefKey, 1);   // the original key, written at once like the original use (saved by the next checkpoint)
            RemoveAll("P" + slot + " used the medkit of car#" + car);
            return true;
        }

        // Medkit.Update postfix: right after the original retags (check reset to 0), a travel proxy's medkit stays Untagged.
        static void TagPostfix(Component __instance)
        {
            var i = Instance; if (i == null || __instance == null || _check(__instance) != 0f) return;
            var vs = VehicleSync.Current; if (vs == null) return;
            ushort car; bool proxy;
            if (vs.CarOf(_myCar(__instance), out car, out proxy) && proxy && !__instance.CompareTag("Untagged")) __instance.tag = "Untagged";
        }

        public string Stats { get { return "MEDKIT_STATS " + (_host ? "host" : "client") + " uses " + _uses + " req " + _requests + " ok " + _accepted + " refused " + _refused + " proxyRefused " + _proxyRefused + " removed " + _removed + " sandboxWrites " + _sandboxWrites + (_hasCommitted ? " | " + _committed : ""); } }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_ok) { _node.RemoteDisconnected -= OnDisconnected; if (_world.ProposalResultHook10 == (Func<uint, ProposalCode, bool>)OnProposalResult) _world.ProposalResultHook10 = null; }
            if (_world.Host != null && ReferenceEquals(_world.Host.MedkitHost, this)) _world.Host.MedkitHost = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
