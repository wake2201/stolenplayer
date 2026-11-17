// W6B.2 mission-box unpacking on the ORIGINAL CardboardMinigame (rules + verified IL: src/Shared/HeavyItems.cs UnpackRules,
// docs/research/STORY_SYSTEM.md section 4c). Hooks (narrow):
//   CardboardMinigame.SkipMinigameWin / MinigameWon   prefix: the box (current_box) is a shared heavy object -> the host
//       consumes its record once (HeavyOp.Unpack). Host: commit, then its original runs (contents, fade, sound, box destroyed).
//       Player 2: the original never runs; "heavyTxn" Unpack proposal; the HOST runs its original SpawnItem on its own copy
//       of that box (must lie on the host's mission table, PlaceCardboard.placedObj) in the same step; Player 2 gets the
//       original feedback (fade + cut sound) on acceptance; its box vanishes with the consumed record (HeavySync reconcile).
//   CardboardMinigame.SpawnItem / SpawnItemOnStart    prefix (Player 2): never - the contents exist only in the host campaign
//       (story item 91; the mirrored ItemCardboard key would otherwise re-create a second package at load).
// Untracked boxes (MainScene / ambiguous id): host = vanilla local original; Player 2 = refused (fail closed).
// Cost: hooks only at an open / load; per frame one null check of the pending request.
using System;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class HeavySync : IUnpackHost
    {
        Type _mgT, _placeT; Func<object, object> _psMg, _mgBox, _mgCut, _psFade, _placeObj, _pickScript, _itemSpawn;
        Action<object, GameObject> _setMgBox; Action<object> _mgSpawn; bool _unpackOk;
        static bool _resumeUnpack;
        sealed class UnpackReq { public uint Id; public int Ns; public uint Hash; public bool Cargo; public object Mg; public double At; public string Name; }
        UnpackReq _unpack;
        long _unpackHost, _unpackRequested, _unpackGranted, _unpackHostRuns, _unpackRefused, _unpackUntracked, _spawnBlocked;
        public const double UnpackTimeout = 15.0;

        void InitUnpack()
        {
            _mgT = AccessTools.TypeByName("CardboardMinigame"); _placeT = AccessTools.TypeByName("PlaceCardboard");
            var ps = AccessTools.TypeByName("PlayerScript"); var it = AccessTools.TypeByName("ItemID");
            _psMg = Fast.Getter<object>(ps, "cardboard_minigame_script"); _psFade = Fast.Getter<object>(ps, "animVentFade");
            _mgBox = Fast.Getter<object>(_mgT, "current_box"); _setMgBox = Fast.Setter<GameObject>(_mgT, "current_box"); _mgCut = Fast.Getter<object>(_mgT, "cutSound");
            _mgSpawn = Fast.Call0(_mgT, "SpawnItem"); _placeObj = Fast.Getter<object>(_placeT, "placedObj");
            _pickScript = Fast.Getter<object>(_pickT, "itemScript"); _itemSpawn = Fast.Getter<object>(it, "cardboard_spawn_item_ID");
            var skip = _mgT != null ? AccessTools.Method(_mgT, "SkipMinigameWin", Type.EmptyTypes) : null; var won = _mgT != null ? AccessTools.Method(_mgT, "MinigameWon", Type.EmptyTypes) : null;
            var spawn = _mgT != null ? AccessTools.Method(_mgT, "SpawnItem", Type.EmptyTypes) : null; var spawnStart = _mgT != null ? AccessTools.Method(_mgT, "SpawnItemOnStart", Type.EmptyTypes) : null;
            if (skip == null || won == null || spawn == null || spawnStart == null || _psMg == null || _mgBox == null || _setMgBox == null || _mgCut == null || _mgSpawn == null || _placeObj == null || _pickScript == null || _itemSpawn == null)
            {
                _log.Warn("HEAVY CardboardMinigame API incomplete: mission-box unpacking is not shared - Player 2's unpacking is refused (fail closed)");
                _unpackOk = false;
                if (!_host && spawn != null && skip != null) { _h.Patch(skip, new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "UnpackBlockedPrefix"))); }
                return;
            }
            var pre = new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "UnpackPrefix"));
            _h.Patch(skip, pre); _h.Patch(won, pre);
            var sp = new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "SpawnPrefix"));
            _h.Patch(spawn, sp); _h.Patch(spawnStart, sp);
            if (_host && _world.Host != null) _world.Host.UnpackHost = this;
            _unpackOk = true;
        }

        static bool UnpackBlockedPrefix() { return false; }

        static bool UnpackPrefix(object __instance)
        {
            var hs = Instance;
            if (hs == null || !hs._ok || !hs._unpackOk || _resumeUnpack) return true;
            return hs.AllowUnpack(__instance);
        }

        static bool SpawnPrefix()
        {
            var hs = Instance; if (hs == null) return true;
            if (UnpackRules.ClientMaySpawn(true, hs._host, _resumeUnpack)) return true;
            hs._spawnBlocked++;
            hs.LogOnce("spawnblock", "HEAVY Player 2: CardboardMinigame contents not created here (they exist only in the host campaign; prevents a second package, e.g. from the mirrored ItemCardboard key)");
            return false;
        }

        // Box id of a GameObject: census heavy object or cargo object (as the heavy sale).
        bool BoxId(GameObject box, out int ns, out uint hash, out bool cargo, out string name)
        {
            ns = Regions.None; hash = 0; cargo = false; name = box != null ? box.name : "?";
            var pick = box != null ? box.GetComponent(_pickT) : null; if (pick == null) return false;
            var e = Resolve(pick, out ns);
            if (e != null) { hash = e.Hash; name = e.Name; }
            else if (HeavyCargoSync.Instance != null && HeavyCargoSync.Instance.TryCargoObject(pick, out hash)) { cargo = true; ns = _world.LocalRegion; }
            else return false;
            return ns != Regions.None && ns != IdentityNamespace.MainScene;
        }

        bool AllowUnpack(object mg)
        {
            string name = "?";
            try
            {
                var box = _mgBox(mg) as GameObject;
                if (box == null) return true;                                   // nothing to open: the original NREs / does nothing as before
                int ns; uint hash; bool cargo;
                bool tracked = BoxId(box, out ns, out hash, out cargo, out name);
                var g = UnpackRules.Gate(true, _host, tracked, Live, _unpack != null);
                if (g == UnpackGate.RunOriginal)
                {
                    _unpackUntracked++;
                    _log.Info("HEAVY unpack of '" + name + "' by Player 1: not a shared heavy object - original local unpacking");
                    return true;
                }
                if (g == UnpackGate.Refuse)
                {
                    _unpackRefused++;
                    _log.Info("HEAVY unpack of '" + name + "' by Player 2 refused: " + (!tracked ? "not a shared heavy object here (its contents would be a duplicate)" : !Live ? "not connected" : "a request is already pending") + " - nothing happens");
                    return false;
                }
                HeavyRecord r; ushort seen = TryRecord(ns, hash, out r) ? r.Lease : (ushort)0;
                var t = new HeavyTxnRequest { Op = HeavyOp.Unpack, Ns = ns, Hash = hash, Lease = seen, Rot = new Quat16 { W = 32767 }, Cargo = cargo };
                if (g == UnpackGate.CommitThenRun)
                {
                    if (_world.Host == null) return false;
                    string why; ushort l;
                    var code = _world.Host.HeavyTransaction(1, t, _world.Clock, out l, out why);
                    _world.FlushOutbox();
                    if (code != ProposalCode.Accepted) { _unpackRefused++; _log.Info("HEAVY unpack of '" + name + "' by Player 1 (" + t + ") refused: " + code + (why != null ? " (" + why + ")" : "") + " - nothing happens, no contents"); return false; }
                    _sims.Remove(hash); _dirty = true; _unpackHost++;
                    _log.Info("HEAVY unpack of '" + name + "' by Player 1 (" + t + "): box record consumed once; the original SkipMinigameWin creates the contents and removes the box (Player 2's copy disappears with the record)");
                    return true;
                }
                var cat = cargo ? null : LiveCatalog(ns); t.Digest = cat != null ? cat.Digest : 0u;
                _unpack = new UnpackReq { Ns = ns, Hash = hash, Cargo = cargo, Mg = mg, At = Now, Name = name };
                _unpack.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = t.Encode() });
                _unpackRequested++;
                _log.Info("HEAVY unpack of '" + name + "' by Player 2 (" + t + ") requested (proposal " + _unpack.Id + "): the host opens its copy once and owns the contents");
                return false;
            }
            catch (Exception ex) { _unpackRefused++; _log.Error("HEAVY unpack gate failed for '" + name + "': " + (_host ? "original runs" : "refused (nothing happens)"), ex); return _host; }
        }

        bool OnUnpackResultHook(uint id, ProposalCode code)
        {
            if (_unpack == null || id != _unpack.Id) return false;
            var q = _unpack; _unpack = null; _dirty = true;
            if (code != ProposalCode.Accepted) { _unpackRefused++; _log.Info("HEAVY unpack of '" + q.Name + "' by Player 2 -> " + code + ": nothing happens, no contents (opened on the other PC, box not on the host's table, or stale)"); return true; }
            _unpackGranted++; _sims.Remove(q.Hash);
            try
            {   // original actor feedback of SkipMinigameWin on the opener's PC (fade + cut sound); contents + box removal are the host's
                var ps = Ps(); var fade = ps != null && _psFade != null ? _psFade(ps) as Animation : null; if (fade != null) fade.Play();
                var cut = _mgCut(q.Mg) as AudioSource; if (cut != null) cut.Play();
            }
            catch (Exception ex) { _log.Error("HEAVY unpack feedback failed (no gameplay effect)", ex); }
            _log.Info("HEAVY unpack of '" + q.Name + "' by Player 2 granted (" + ((Now - q.At) * 1000).ToString("0") + " ms): the host created the contents once; this box disappears with the consumed record");
            return true;
        }

        void UnpackFrame(double now)
        {
            if (_unpack == null || (Live && now - _unpack.At <= UnpackTimeout)) return;
            _unpackRefused++;
            _log.Warn("HEAVY unpack of '" + _unpack.Name + "' by Player 2 unanswered" + (Live ? " after " + UnpackTimeout + " s" : " (disconnected)") + ": abandoned locally (a late grant was already applied by the host exactly once)");
            _unpack = null; _dirty = true;
        }

        bool UnpackHold(uint hash) { return _unpack != null && _unpack.Hash == hash; }

        // ---------------- IUnpackHost (host) ----------------
        GameObject HostTableBox(int ns, uint hash, bool cargo, out string why)
        {
            why = null;
            if (ns != _world.LocalRegion) { why = "host is not in " + IdentityNamespace.Name(ns); return null; }
            var place = _placeT != null ? UnityEngine.Object.FindObjectOfType(_placeT) : null;
            var t = place != null ? _placeObj(place) as Transform : null;
            if (t == null) { why = "no box on the host's mission table"; return null; }
            int n2; uint h2; bool c2; string name;
            if (!BoxId(t.gameObject, out n2, out h2, out c2, out name) || n2 != ns || h2 != hash || c2 != cargo) { why = "the host's table holds another object ('" + t.name + "')"; return null; }
            var pick = t.GetComponent(_pickT); var item = pick != null ? _pickScript(pick) : null;
            if (item == null || _itemSpawn(item) == null) { why = "'" + t.name + "' has no original contents"; return null; }
            return t.gameObject;
        }

        public bool CanUnpack(int ns, uint hash, bool cargo, out string why)
        {
            if (!_unpackOk) { why = "unpacking API incomplete on the host"; return false; }
            var ps = Ps(); var mg = ps != null ? _psMg(ps) : null;
            if (mg == null) { why = "no CardboardMinigame on the host"; return false; }
            var box = HostTableBox(ns, hash, cargo, out why);
            if (box == null) _log.Info("HEAVY unpack by Player 2 of " + IdentityNamespace.Name(ns) + ":" + hash.ToString("X8") + " refused: " + why);
            return box != null;
        }

        // Same host step as the commit: the host's ORIGINAL SpawnItem on its own copy (contents once), the cut sound, the box
        // destroyed like SkipMinigameWin. The screen fade belongs to the opener (Player 2) and is not played here.
        public void RunUnpack(int ns, uint hash, bool cargo)
        {
            string why; var box = HostTableBox(ns, hash, cargo, out why);
            var ps = Ps(); var mg = ps != null ? _psMg(ps) : null;
            if (box == null || mg == null) { _log.Error("HEAVY unpack: host copy vanished inside the host step (" + why + ") - box consumed, no contents created"); return; }
            _resumeUnpack = true;
            try
            {
                _setMgBox(mg, box);
                _mgSpawn(mg);
                var cut = _mgCut(mg) as AudioSource; if (cut != null) cut.Play();
                UnityEngine.Object.Destroy(box);
            }
            catch (Exception ex) { _log.Error("HEAVY unpack: host original SpawnItem failed (box consumed, contents not created)", ex); return; }
            finally { _resumeUnpack = false; }
            _sims.Remove(hash); _dirty = true; _unpackHostRuns++;
            _log.Info("HEAVY unpack of '" + box.name + "' (" + IdentityNamespace.Name(ns) + ":" + (cargo ? "cargo " : "") + hash.ToString("X8") + ") opened by Player 2: host original SpawnItem created the contents once on the host's table (host-owned)");
        }

        string UnpackStats { get { return " | unpack host " + _unpackHost + " req " + _unpackRequested + " granted " + _unpackGranted + " hostRuns " + _unpackHostRuns + " refused " + _unpackRefused + " untracked " + _unpackUntracked + " spawnBlocked " + _spawnBlocked; } }
    }
}
