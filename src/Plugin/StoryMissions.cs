// W6B.3 cooperative story missions on the ORIGINAL residence-mission code (rules + verified IL: src/Shared/Missions.cs,
// docs/research/STORY_SYSTEM.md section 4d). Hooks (narrow, event-only):
//   Mission_Item.Mission_Complete      prefix (Player 2, listed task only): the original completion does not run yet; the action is
//       proposed ("missionAct"); on acceptance the ORIGINAL Mission_Complete runs on Player 2 (UI, own XP, local objects; money
//       reverted by the wallet, AdvanceStory suppressed - the host already paid and advanced). Refused: the original interaction is
//       undone (plant: Fake / planted objects + item_plant count restored; photo: Photograph re-enabled) - nothing happens.
//                                      postfix (host): a completion by Player 1 updates "missions".
//   PlantEvidence.Planted              prefix / postfix (Player 2): marks the plant interaction (context for the prefix above).
//   BlackBay_Object.SellIt             prefix / postfix (Player 2): story trigger context for the listed story16 sale (StoryTriggers).
// Host (IMissionHost): key 1 -> 2, display_as_complete, shared payout once (PlayerScript.moneyAm = shared balance, AddMoneyMade),
// AdvanceStory like MissionComplete1, then the world part on its own loaded objects. XP and the mission-complete UI are the actor's.
// Cost: hooks only at a completion / plant / Blackbay sale; per frame one registry head compare (client) / 1 Hz two SPrefs reads (host).
using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class StorySync : IMissionHost
    {
        public PlayerSync Players;   // set by Plugin.BuildSystems (distance check when both players share the scene)
        static Type _miT, _peT, _phT, _resT, _aiT, _doorT;
        static Func<object, int> _miHouse, _miInt, _resHouse, _misPay, _itemAmount; static Func<object, object> _miMarker, _peMission, _phMission, _resMissions, _gmPs, _psInv, _gmPlantItem;
        static Func<object, GameObject> _peDisable, _peEnable; static Func<object, string> _misDesc; static Func<object, bool> _misAdvance, _psPhoto;
        static Action<object, bool> _setMisDone; static Action<object, int> _setMoney, _setItemAmount, _addMoneyMade; static Func<object, int> _money;
        static Action<object> _missionComplete; static Action<string, int> _prefSet;
        static bool _missionsOk, _missionResume; static object _ctxPlant;

        sealed class MissionPending { public uint Id; public MissionActionRequest Req; public MissionTarget T; public object Mi; public object Plant, Photo; public double At; }
        MissionPending _mission;
        MissionState _missionsCommitted; uint _missionsHead = uint.MaxValue, _missionsEpoch; bool _missionsDirty; double _nextMissionPoll;
        long _mProposed, _mAccepted, _mRefused, _mUndone, _mHostDone, _mPresented, _mFollowed;
        public const double MissionTimeout = 15.0;

        void PatchMissions()
        {
            try
            {
                _miT = AccessTools.TypeByName("Mission_Item"); _peT = AccessTools.TypeByName("PlantEvidence"); _phT = AccessTools.TypeByName("Photograph");
                _resT = AccessTools.TypeByName("SelectResidence"); var mis = AccessTools.TypeByName("Mission_Inventory"); _aiT = AccessTools.TypeByName("AI"); _doorT = AccessTools.TypeByName("Door");
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory");
                var item = AccessTools.TypeByName("ItemID"); var sp = AccessTools.TypeByName("SPrefs"); var bb = AccessTools.TypeByName("BlackBay_Object");
                _miHouse = Fast.Getter<int>(_miT, "house_number"); _miInt = Fast.Getter<int>(_miT, "mission_int"); _miMarker = Fast.Getter<object>(_miT, "diamondMarker");
                _peMission = Fast.Getter<object>(_peT, "missionToComplete"); _peDisable = Fast.Getter<GameObject>(_peT, "disableObj"); _peEnable = Fast.Getter<GameObject>(_peT, "enableObj");
                _phMission = Fast.Getter<object>(_phT, "missionScript");
                _resHouse = Fast.Getter<int>(_resT, "houseNumber"); _resMissions = Fast.Getter<object>(_resT, "residence_missions");
                _misPay = Fast.Getter<int>(mis, "mission_pay"); _misDesc = Fast.Getter<string>(mis, "story_desc"); _misAdvance = Fast.Getter<bool>(mis, "on_complete_advance_story");
                _setMisDone = Fast.Setter<bool>(mis, "display_as_complete");
                _gmPs = Fast.Getter<object>(gm, "PS"); _gmPlantItem = Fast.Getter<object>(gm, "plantPackageItem");
                _psInv = Fast.Getter<object>(ps, "invScript"); _psPhoto = Fast.Getter<bool>(ps, "takingPhoto");
                _money = Fast.Getter<int>(ps, "moneyAm"); _setMoney = Fast.Setter<int>(ps, "moneyAm"); _addMoneyMade = Fast.Call<int>(inv, "AddMoneyMade");
                _itemAmount = Fast.Getter<int>(item, "item_amount"); _setItemAmount = Fast.Setter<int>(item, "item_amount");
                _missionComplete = Fast.Call0(_miT, "Mission_Complete");
                var si = sp != null ? AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) }) : null;
                if (si != null) _prefSet = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), si);
                var complete = _miT != null ? AccessTools.Method(_miT, "Mission_Complete", Type.EmptyTypes) : null;
                var planted = _peT != null ? AccessTools.Method(_peT, "Planted", Type.EmptyTypes) : null;
                var sell = bb != null ? AccessTools.Method(bb, "SellIt", Type.EmptyTypes) : null;
                _missionsOk = _miHouse != null && _miInt != null && _miMarker != null && _peMission != null && _peDisable != null && _peEnable != null && _phMission != null
                    && _resHouse != null && _resMissions != null && _misPay != null && _misDesc != null && _misAdvance != null && _setMisDone != null && _gmPs != null
                    && _gmPlantItem != null && _psInv != null && _psPhoto != null && _money != null && _setMoney != null && _addMoneyMade != null && _itemAmount != null
                    && _setItemAmount != null && _missionComplete != null && _prefSet != null && complete != null && planted != null && _aiT != null && _doorT != null;
                if (!_missionsOk)
                {   // fail closed on Player 2: a listed task never completes locally only (it would diverge from the host campaign)
                    _log.Warn("STORY mission API incomplete: cooperative missions disabled" + (_host ? "" : " - Player 2's listed task completions are refused"));
                    if (!_host && complete != null) _h.Patch(complete, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "MissionBlockedPre")));
                    return;
                }
                _h.Patch(complete, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "MissionPre")), postfix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "MissionPost")));
                if (!_host)
                {
                    _h.Patch(planted, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "PlantedPre")), postfix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "PlantedPost")));
                    if (sell != null) _h.Patch(sell, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "SellPre")), postfix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "SellPost")));
                    else _log.Warn("STORY BlackBay_Object.SellIt not found: Player 2's story Blackbay sale is not a shared trigger");
                }
                if (_host && _world.Host != null) _world.Host.MissionHost = this;
                _log.Info("STORY cooperative missions ready (" + MissionTargets.All.Length + " listed task(s): " + string.Join(", ", Array.ConvertAll(MissionTargets.All, t => t.ToString())) + ")");
            }
            catch (Exception ex) { _missionsOk = false; _log.Error("STORY mission hooks failed: cooperative missions disabled", ex); }
        }

        static bool MissionBlockedPre(object __instance)
        {
            try { return _miHouse == null || _miInt == null || MissionTargets.Find(_miHouse(__instance), _miInt(__instance)) == null; }
            catch { return false; }
        }

        // ---------------------------------------------------------------- hooks
        static void PlantedPre(object __instance) { _ctxPlant = __instance; }
        static void PlantedPost() { _ctxPlant = null; }
        static void SellPre(object __instance)
        {
            if (Instance == null) return;
            try { _ctxKind = StoryTriggerKind.BlackbaySale; _ctxPath = PathOf(((Component)__instance).transform); }
            catch (Exception ex) { _ctxPath = null; Instance._log.Error("STORY hook BlackBay_Object.SellIt (prefix)", ex); }
        }
        static void SellPost() { _ctxPath = null; }

        static bool MissionPre(object __instance)
        {
            var i = Instance; if (i == null || i._host || _missionResume) return true;
            try { return i.ClientMission(__instance); }
            catch (Exception ex) { i._log.Error("STORY mission gate failed (completion refused, nothing happens)", ex); return MissionTargets.Find(_miHouse(__instance), _miInt(__instance)) == null; }
        }

        static void MissionPost(object __instance)
        {
            var i = Instance; if (i == null || !i._host) return;
            try
            {
                var t = MissionTargets.Find(_miHouse(__instance), _miInt(__instance)); if (t == null) return;
                i._log.Info("STORY host task " + t + ": original completion by Player 1 (key " + i.Pref(t) + ")");
                i.Publish(); i._world.FlushOutbox();
            }
            catch (Exception ex) { i._log.Error("STORY mission postfix (host)", ex); }
        }

        int Pref(MissionTarget t) { return _prefInt(t.PrefKey); }

        // Player 2: one listed task completed with its original interaction.
        bool ClientMission(object mi)
        {
            var t = MissionTargets.Find(_miHouse(mi), _miInt(mi));
            if (t == null) return true;                                     // unlisted task: original (money excluded, story suppressed as before)
            if (Pref(t) != 1) return true;                                  // the original returns at once (not accepted / already done)
            var mb = (Component)mi; object plant = null, photo = null; string path = null;
            if (t.Kind == MissionActionKind.Plant)
            {
                if (_ctxPlant != null && ReferenceEquals(_peMission(_ctxPlant), mi)) { plant = _ctxPlant; path = PathOf(((Component)plant).transform); }
            }
            else
            {
                var ps = Ps();
                if (ps != null && _psPhoto(ps))
                    foreach (var c in mb.GetComponentsInChildren(_phT, true)) if (ReferenceEquals(_phMission(c), mi)) { photo = c; path = PathOf(c.transform); break; }
            }
            string why = null;
            if (path == null || !MissionTargets.PathMatches(t, path)) why = "not the listed original " + t.Kind + " interaction (" + (path ?? "no trigger") + ")";
            int idx = 0; string key = null; bool free = false;
            if (why == null && !Local(out idx, out key, out free)) why = "story not ready";
            if (why == null && !string.Equals(key, t.StageKey, StringComparison.Ordinal)) why = "story stage is '" + key + "', task belongs to '" + t.StageKey + "'";
            if (why == null && _mission != null) why = "another task is pending";
            if (why == null && (!_node.IsConnected || !_world.Synced)) why = "not connected";
            if (why != null)
            {
                _mRefused++;
                _log.Info("STORY Player-2 task " + t + " refused before the host: " + why + " - nothing happens");
                Undo(t, plant, photo);
                return false;
            }
            var req = new MissionActionRequest { FromIndex = idx, House = t.House, Mission = t.Mission, Kind = t.Kind, StageKey = t.StageKey };
            _mission = new MissionPending { Req = req, T = t, Mi = mi, Plant = plant, Photo = photo, At = Now };
            _mission.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.MissionAction, Value = req.ToBytes() });
            _mProposed++;
            _log.Info("STORY Player-2 " + req + " via '" + path + "' proposed #" + _mission.Id + ": the host completes the task once, then the original completion runs here");
            return false;
        }

        object Ps() { var gm = _world.GameManagerObject; return gm != null ? _gmPs(gm) : null; }

        // The original interaction already changed these before Mission_Complete: restore them (nothing happened).
        void Undo(MissionTarget t, object plant, object photo)
        {
            try
            {
                if (plant != null)
                {
                    var d = _peDisable(plant); var e = _peEnable(plant); if (d != null) d.SetActive(true); if (e != null) e.SetActive(false);
                    var gm = _world.GameManagerObject; var it = gm != null ? _gmPlantItem(gm) : null; if (it != null) _setItemAmount(it, _itemAmount(it) + 1);   // PlayerScript IL item_amount--
                }
                if (photo != null) ((Behaviour)photo).enabled = true;   // Photograph.Update set enabled = false after the call
                _mUndone++;
            }
            catch (Exception ex) { _log.Error("STORY task " + t + " undo failed", ex); }
        }

        bool OnMissionResult(uint id, ProposalCode code)
        {
            var p = _mission; if (p == null || p.Id != id) return false;
            _mission = null;
            if (code != ProposalCode.Accepted)
            {
                _mRefused++;
                _log.Info("STORY Player-2 " + p.Req + " #" + id + " -> " + code + ": nothing happens (original interaction undone)");
                Undo(p.T, p.Plant, p.Photo); _missionsDirty = true;
                return true;
            }
            _mAccepted++;
            RunOriginalCompletion(p, "committed by the host (" + ((Now - p.At) * 1000).ToString("0") + " ms)");
            return true;
        }

        // Accepted: the ORIGINAL completion on Player 2 (mission-complete UI, own XP, key 2 in the sandbox, local objects). Its
        // money change is reverted by the wallet (the host paid the shared balance) and its AdvanceStory is suppressed (host index).
        void RunOriginalCompletion(MissionPending p, string why)
        {
            var mb = p.Mi as Component;
            if (mb == null || Pref(p.T) != 1) { _log.Info("STORY task " + p.T + " " + why + ": local object gone / already done - presented from the shared key"); _missionsDirty = true; return; }
            _missionResume = true;
            try { _missionComplete(p.Mi); }
            catch (Exception ex) { _log.Error("STORY task " + p.T + ": original Mission_Complete after the host commit failed", ex); }
            finally { _missionResume = false; }
            _missionsDirty = true;
            _log.Info("STORY Player-2 task " + p.T + " " + why + ": original completion ran here (key " + Pref(p.T) + ")");
        }

        void MissionFrame(double now)
        {
            if (!_missionsOk) return;
            if (_host)
            {
                if (now < _nextMissionPoll || _world.Host == null) return;
                _nextMissionPoll = now + 1.0;
                if (_world.GameManagerObject != null && Publish(false)) _world.FlushOutbox();
                return;
            }
            if (_mission != null && (!_node.IsConnected || now - _mission.At > MissionTimeout))
            {
                var p = _mission; _mission = null; _mRefused++;
                _log.Warn("STORY Player-2 " + p.Req + " unanswered" + (_node.IsConnected ? " after " + MissionTimeout + " s" : " (disconnected)") + ": undone here; a late host commit is shown from the shared key");
                Undo(p.T, p.Plant, p.Photo); _missionsDirty = true;
            }
            var reg = _world.Registry;
            if (reg.HeadRev != _missionsHead || reg.Epoch != _missionsEpoch)
            {
                _missionsHead = reg.HeadRev; _missionsEpoch = reg.Epoch;
                var e = reg.Get(StateScope.Session, 0, WorldKeys.Missions); MissionState s;
                if (e != null && MissionState.TryParse(e.Value, out s) && (_missionsCommitted == null || !_missionsCommitted.SameAs(s))) { _missionsCommitted = s; _missionsDirty = true; _log.Info("STORY shared " + s); }
            }
            if (_missionsDirty && _mission == null && _missionsCommitted != null && _world.GameManagerObject != null) { _missionsDirty = false; FollowMissions(); }
        }

        // Player 2: the host's key per listed task into the sandbox; a completion by Player 1 is presented on loaded objects.
        void FollowMissions()
        {
            foreach (var t in MissionTargets.All)
            {
                int host; if (!_missionsCommitted.TryGet(t.House, t.Mission, out host)) continue;
                int local = Pref(t); if (local == host) continue;
                bool present = MissionRules.ClientPresentDone(local, host);
                _prefSet(t.PrefKey, host); _mFollowed++;
                int shown = present ? PresentDone(t) : 0;
                _log.Info("STORY task " + t + " key " + local + " -> " + host + " (host campaign)" + (present ? ", completion shown on " + shown + " loaded object(s)" : host < local ? " - host checkpoint restored the task (loaded objects refresh at the next scene load)" : ""));
            }
        }

        // World part of MissionComplete1 / Mission_Complete / the trigger on this PC's loaded copy (no UI, no money, no XP).
        int PresentDone(MissionTarget t)
        {
            int n = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_miT))
            {
                if (_miHouse(o) != t.House || _miInt(o) != t.Mission) continue;
                var mb = (Behaviour)o;
                var marker = _miMarker(o) as GameObject; if (marker != null) UnityEngine.Object.Destroy(marker);
                if (t.Kind == MissionActionKind.Plant)
                    foreach (var pe in UnityEngine.Object.FindObjectsOfType(_peT))
                        if (ReferenceEquals(_peMission(pe), o)) { var d = _peDisable(pe); var e = _peEnable(pe); if (d != null) d.SetActive(false); if (e != null) e.SetActive(true); }
                foreach (var ph in mb.GetComponentsInChildren(_phT, true)) if (ReferenceEquals(_phMission(ph), o)) ((Behaviour)ph).enabled = false;
                if (mb.GetComponent(_aiT) == null)
                {
                    mb.enabled = false;
                    if (mb.GetComponent(_doorT) == null) mb.gameObject.SetActive(false);
                }
                n++; _mPresented++;
            }
            return n;
        }

        // ---------------------------------------------------------------- IMissionHost (host)
        public bool TryMissionPref(MissionTarget t, out int pref)
        {
            pref = 0; var gm = _world.GameManagerObject; if (gm == null || !_gmLoaded(gm)) return false;
            pref = Pref(t); return true;
        }

        public bool PlayerNear(byte slot, MissionTarget t, out string why)
        {
            why = null;
            int region = _world.Host != null ? _world.Host.RegionOf(slot) : Regions.None;
            if (region != t.Region) { why = "Player " + slot + " is in " + Regions.Name(region) + ", the task is in " + Regions.Name(t.Region); return false; }
            Vector3 feet; bool inCar; float speed; Vector3 target;
            if (_world.LocalRegion != t.Region || Players == null || !Players.TryGetRemote(out feet, out inCar, out speed) || !TargetPos(t, out target))
            {
                _log.Info("STORY task " + t + ": distance not checked (host not in the same scene); region " + Regions.Name(region) + " ok");
                return true;
            }
            float d = Vector3.Distance(feet, target);
            if (inCar || d > t.MaxDistance) { why = "Player " + slot + " is " + d.ToString("0.0") + " m from the target" + (inCar ? " (in a car)" : "") + ", max " + t.MaxDistance.ToString("0.0") + " m"; return false; }
            _log.Info("STORY task " + t + ": Player " + slot + " " + d.ToString("0.0") + " m from the target (max " + t.MaxDistance.ToString("0.0") + ")");
            return true;
        }

        bool TargetPos(MissionTarget t, out Vector3 pos)
        {
            pos = Vector3.zero;
            var type = t.Kind == MissionActionKind.Plant ? _peT : _phT;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(type))
            {
                var c = (Component)o;
                if (MissionTargets.PathMatches(t, PathOf(c.transform))) { pos = c.transform.position; return true; }
            }
            return false;
        }

        // Campaign part of MissionComplete1 (shared): key = 2, display_as_complete, payout to the shared balance once, AdvanceStory.
        public bool Complete(MissionTarget t, byte slot, out string why)
        {
            why = null;
            object res = null;
            foreach (var go in GameObject.FindGameObjectsWithTag("Residence_Script"))   // Mission_Item.LoadAll lookup (event only)
            {
                var r = go.GetComponent(_resT); if (r != null && _resHouse(r) == t.House) { res = r; break; }
            }
            var list = res != null ? _resMissions(res) as IList : null;
            var mis = list != null && t.Mission < list.Count ? list[t.Mission] : null;
            var ps = Ps(); var gm = _world.GameManagerObject; var sm = gm != null ? _gmStory(gm) : null;
            if (mis == null || ps == null || sm == null) { why = "host residence " + t.House + " / player / story manager not available"; return false; }
            int pay = _misPay(mis), before = _money(ps);
            _prefSet(t.PrefKey, 2);
            _setMoney(ps, before + pay);
            var inv = _psInv(ps); if (inv != null) _addMoneyMade(inv, pay);
            _setMisDone(mis, true);
            int idx; string key; bool free; string adv = "story unchanged";
            if (_misAdvance(mis) && Local(out idx, out key, out free) && string.Equals(key, _misDesc(mis), StringComparison.Ordinal))
            {
                _advance(sm);
                adv = "story " + idx + " -> " + _smStory(sm);
            }
            int shown = PresentDone(t);
            _mHostDone++;
            _log.Info("STORY host completed " + t + " for Player " + slot + ": key 2, payout " + pay + " (shared balance " + before + " -> " + _money(ps) + "), " + adv + ", " + shown + " loaded object(s) updated here");
            return true;
        }

        public void Publish() { Publish(true); }
        bool Publish(bool log)
        {
            if (_world.Host == null) return false;
            var s = new MissionState();
            foreach (var t in MissionTargets.All) s.Set(t.House, t.Mission, Pref(t));
            bool changed = _world.Host.PublishMissions(s);
            if (changed) _log.Info("STORY host published " + s);
            return changed;
        }

        string MissionStats { get { return " | missions proposed " + _mProposed + " ok " + _mAccepted + " refused " + _mRefused + " undone " + _mUndone + " hostDone " + _mHostDone + " presented " + _mPresented + " followed " + _mFollowed + (_host && _world.Host != null ? " hostCommits " + _world.Host.MissionCompletions + " hostRefusals " + _world.Host.MissionRefusals : ""); } }
    }
}
