// W6C.1: multiplayer-safe death / arrest (rules + verified IL: src/Shared/Death.cs header).
//   - GotCaught prefix/postfix: DEATH / CAUGHT diagnostics (slot, reason, story / world revision, heavy + cargo identity);
//     while a session is active the caught screen's "Load checkpoint" buttons show the game's own "Continue" key (Respawn).
//   - LoadCheckpoint prefix: caught screen -> Respawn = the ORIGINAL GameManager.SelectHideout transition (the original's own
//     hardcore respawn and jail exit: SaveGameNewSystem of the live state, then LoadWait -> MainScene + hideout, player at
//     SpawnHideoutPlayer); pause menu "Load save?" -> refused during the session (would reload the older disk save).
//   - LoadProfile prefix: pause-menu save-slot load refused during the session (before its File.Copy).
//   - SelectJail prefix/postfix: the original jail transition (saves first) runs unchanged; one choice per caught screen.
// Installed only while a multiplayer session runs (built with the session systems), so single-player is the original.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class DeathSync : IDisposable
    {
        static DeathSync _inst;
        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        static Func<object, bool> _gmCaught, _gmDisableSound; static Func<object, object> _gmCaughtObj, _gmPS; static Func<object, string> _gmMap;
        static Func<object, bool> _psInVehicle, _psCarrying; static MethodInfo _selectHideout, _getText; static Func<string, int> _prefInt;

        readonly DeathEpisode _ep = new DeathEpisode();
        UnityEngine.Object _epGm; int _epLoc = -1;
        readonly List<Component> _relabelled = new List<Component>(2);
        long _deaths, _respawns, _jails, _quits, _refusedLoads, _failures, _dupClicks;
        const double ChoiceTimeout = 60;
        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool Ready { get { return _ok; } }

        public DeathSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var sp = AccessTools.TypeByName("SPrefs");
                var loc = AccessTools.TypeByName("GleyLocalization.Manager");
                var caught = gm != null ? AccessTools.Method(gm, "GotCaught", new[] { typeof(int) }) : null;
                var loadCp = gm != null ? AccessTools.Method(gm, "LoadCheckpoint", Type.EmptyTypes) : null;
                var loadProfile = gm != null ? AccessTools.Method(gm, "LoadProfile", new[] { typeof(int) }) : null;
                var jail = gm != null ? AccessTools.Method(gm, "SelectJail", Type.EmptyTypes) : null;
                var menu = gm != null ? AccessTools.Method(gm, "LoadMenu", Type.EmptyTypes) : null;
                _selectHideout = gm != null ? AccessTools.Method(gm, "SelectHideout", Type.EmptyTypes) : null;
                _getText = loc != null ? AccessTools.Method(loc, "GetText", new[] { typeof(string) }) : null;
                _gmCaught = Fast.Getter<bool>(gm, "caught"); _gmDisableSound = Fast.Getter<bool>(gm, "disablesound");
                _gmCaughtObj = Fast.Getter<object>(gm, "CaughtObject"); _gmPS = Fast.Getter<object>(gm, "PS"); _gmMap = Fast.Getter<string>(gm, "loaded_map_name");
                _psInVehicle = Fast.Getter<bool>(ps, "is_in_vehicle"); _psCarrying = Fast.Getter<bool>(ps, "isCarrying");
                var gi = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
                if (gi != null) _prefInt = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                if (caught == null || loadCp == null || loadProfile == null || jail == null || menu == null || _selectHideout == null || _gmCaught == null
                    || _gmDisableSound == null || _gmCaughtObj == null || _gmPS == null || _gmMap == null || _prefInt == null)
                {
                    _log.Error("DEATH game API incomplete: multiplayer respawn NOT installed - the original caught screen would reload the checkpoint");
                    return;
                }
                _h = new Harmony("com.ts2mp.multiplayer.death");
                _h.Patch(caught, new HarmonyMethod(AccessTools.Method(typeof(DeathSync), "CaughtPrefix")), new HarmonyMethod(AccessTools.Method(typeof(DeathSync), "CaughtPostfix")));
                _h.Patch(loadCp, new HarmonyMethod(AccessTools.Method(typeof(DeathSync), "LoadCheckpointPrefix")));
                _h.Patch(loadProfile, new HarmonyMethod(AccessTools.Method(typeof(DeathSync), "LoadProfilePrefix")));
                _h.Patch(jail, new HarmonyMethod(AccessTools.Method(typeof(DeathSync), "JailPrefix")), new HarmonyMethod(AccessTools.Method(typeof(DeathSync), "JailPostfix")));
                _h.Patch(menu, new HarmonyMethod(AccessTools.Method(typeof(DeathSync), "MenuPrefix")));
                _ok = true; _inst = this;
                _log.Info("DEATH multiplayer death / arrest ready (" + (_host ? "host" : "client") + "): caught screen Load checkpoint -> Respawn (original SelectHideout, live state saved, no rollback), Go to Jail original, checkpoint / slot loads refused during the session"
                    + (_getText == null ? "; button label key unavailable (label unchanged)" : ""));
            }
            catch (Exception ex) { _log.Error("DEATH init failed: multiplayer respawn NOT installed", ex); _ok = false; if (_h != null) { _h.UnpatchSelf(); _h = null; } }
        }

        string Slot { get { return "P" + _world.LocalSlot; } }

        int Pref(string k) { try { return _prefInt(k); } catch { return -1; } }

        // Authoritative world / story revision + heavy / cargo identity (event-driven, one line per transition).
        string Diag()
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append("region ").Append(Regions.Name(Pref("CurrentLocation"))).Append(" story ").Append(StorySync.Instance != null ? StorySync.Instance.CommittedText : "n/a")
              .Append(" localStory ").Append(Pref("Story")).Append(" worldRev ").Append(_world.Registry != null ? _world.Registry.HeadRev.ToString() : "-")
              .Append(" epoch ").Append(_world.Registry != null ? _world.Registry.Epoch.ToString("X8") : "-");
            if (HeavySync.Instance != null) sb.Append(" | ").Append(HeavySync.Instance.IdentityText);
            if (HeavyCargoSync.Instance != null) sb.Append(" | ").Append(HeavyCargoSync.Instance.IdentityText);
            return sb.ToString();
        }

        // ---- GotCaught ----
        static void CaughtPrefix(object __instance, int __0)
        {
            var d = _inst; if (d == null) return;
            try { d.OnCaughtBegin(__instance, __0); } catch (Exception ex) { d._log.Error("DEATH GotCaught diagnostics failed", ex); }
        }

        void OnCaughtBegin(object gm, int reason)
        {
            bool already = _gmCaught(gm);
            if (_ep.Open && !ReferenceEquals(_epGm, gm)) { _ep.Complete(); _relabelled.Clear(); }   // previous scene's episode (not yet seen by Update)
            if (!_ep.Begin(reason, already))
            {
                _log.Info("DEATH repeated GotCaught(" + reason + ") ignored by the original (episode #" + _ep.Id + " already open, " + _ep.Duplicates + " duplicate(s))");
                return;
            }
            _epGm = gm as UnityEngine.Object; _epLoc = Pref("CurrentLocation"); _deaths++;
            var ps = _gmPS(gm);
            TravelHooks.LastLoadReason = "caught (GotCaught " + reason + ")";
            _log.Info(DeathRules.Kind(reason) + " " + Slot + " GotCaught(" + reason + ") '" + DeathRules.ReasonKey(reason) + "' episode #" + _ep.Id
                + " inVehicle=" + (ps != null && _psInVehicle != null && _psInVehicle(ps)) + " carrying=" + (ps != null && _psCarrying != null && _psCarrying(ps))
                + " | before: " + Diag());
        }

        static void CaughtPostfix(object __instance, int __0)
        {
            var d = _inst; if (d == null) return;
            try { d.OnCaughtShown(__instance, __0); } catch (Exception ex) { d._log.Error("DEATH caught screen setup failed (original labels kept)", ex); }
        }

        void OnCaughtShown(object gm, int reason)
        {
            var co = _gmCaughtObj(gm) as GameObject;
            if (co == null || !co.activeInHierarchy)
            {
                // hardcore: SelectJail / SelectHideout ran inside GotCaught (both save the live state first)
                _log.Info("DEATH_SCREEN " + Slot + " not shown (original hardcore transition inside GotCaught, saves the live state first)");
                return;
            }
            int n = Relabel(co, DeathRules.RespawnLabelKey);
            _log.Info("DEATH_SCREEN " + Slot + " shown: options Respawn (" + n + " Load checkpoint button(s) relabelled '" + DeathRules.RespawnLabelKey + "')"
                + (DeathRules.JailOffered(reason, _gmMap(gm)) ? ", Go to Jail" : "") + ", Quit to Menu; game paused locally (original timeScale 0), network continues");
        }

        int Relabel(GameObject caughtObj, string key)
        {
            if (_getText == null) return 0;
            string text = _getText.Invoke(null, new object[] { key }) as string;
            if (string.IsNullOrEmpty(text)) return 0;
            int n = 0;
            foreach (var t in caughtObj.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "LOAD CHECKPOINT") continue;
                foreach (var c in t.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c.GetType().FullName != "UnityEngine.UI.Text") continue;
                    var p = c.GetType().GetProperty("text");
                    if (p == null) continue;
                    p.SetValue(c, text, null); n++;
                    if (!_relabelled.Contains(c)) _relabelled.Add(c);
                }
            }
            return n;
        }

        // ---- checkpoint loads ----
        // GameManager.LoadProfile(slot): File.Copy of the slot over saveData.thief, then LoadCheckpoint (IL_0057..IL_009f).
        // Refused before the copy: the session's save file is never replaced by another slot.
        static bool LoadProfilePrefix(int __0)
        {
            var d = _inst; if (d == null) return true;
            try
            {
                if (DeathRules.Decide(true, LoadSource.SaveSlot) != LoadDecision.Refuse) return true;
                d._refusedLoads++;
                d._log.Info("CHECKPOINT_LOAD " + d.Slot + " " + DeathRules.SourceName(LoadSource.SaveSlot) + " slot " + __0 + " refused during the multiplayer session (no file copied, no reload); allowed again after the session ends");
            }
            catch { }
            return false;
        }

        static bool LoadCheckpointPrefix(object __instance)
        {
            var d = _inst; if (d == null) return true;
            try { return d.OnLoadCheckpoint(__instance); }
            catch (Exception ex) { d._log.Error("CHECKPOINT_LOAD handler failed: load refused during the session (fail closed)", ex); return false; }
        }

        bool OnLoadCheckpoint(object gm)
        {
            var co = _gmCaughtObj(gm) as GameObject;
            // callers (verified): caught-screen buttons, PAUSE MENU/Load save?/Yes, LoadProfile (refused in its own prefix)
            LoadSource src = _gmCaught(gm) && co != null && co.activeInHierarchy ? LoadSource.CaughtScreen : LoadSource.PauseMenu;
            var dec = DeathRules.Decide(true, src);
            if (dec == LoadDecision.Refuse)
            {
                _refusedLoads++;
                _log.Info("CHECKPOINT_LOAD " + Slot + " " + DeathRules.SourceName(src) + " refused during the multiplayer session (it would reload the older disk save and roll back the shared world); allowed again after the session ends");
                return false;
            }
            if (!_ep.TryChoose(DeathChoice.Respawn, Now)) { _dupClicks++; _log.Info("DEATH_SCREEN " + Slot + " repeated choice ignored (episode #" + _ep.Id + " already chose " + _ep.Choice + ")"); return false; }
            _log.Info("DEATH_SCREEN " + Slot + " choice Respawn (episode #" + _ep.Id + "); CHECKPOINT_LOAD " + DeathRules.SourceName(src) + " replaced: GameManager.LoadCheckpoint NOT called");
            _log.Info("RESPAWN begin " + Slot + ": original SelectHideout transition (live state saved first, hideout spawn) | before: " + Diag());
            bool started = false;
            try
            {
                if (co != null) co.SetActive(false);   // as SelectJail IL_0001-0007 / LoadCheckpoint IL_001c-0023
                TravelHooks.LastLoadReason = "multiplayer respawn (episode #" + _ep.Id + ")";
                _selectHideout.Invoke(gm, null);
                started = _gmDisableSound(gm);       // LoadWait sets disablesound before its first yield
            }
            catch (Exception ex) { _log.Error("RESPAWN SelectHideout threw", ex); }
            if (started) { _respawns++; return false; }
            _failures++; _ep.Failed();
            if (co != null) co.SetActive(true);
            _log.Warn("RESPAWN failure " + Slot + ": the original hideout transition did not start (travel hook refused / exception); caught screen shown again, choose again or quit to menu");
            return false;
        }

        // ---- jail / quit ----
        static bool JailPrefix(object __instance)
        {
            var d = _inst; if (d == null) return true;
            try { return d.OnJail(__instance); } catch (Exception ex) { d._log.Error("JAIL diagnostics failed (original jail kept)", ex); return true; }
        }

        bool OnJail(object gm)
        {
            var co = _gmCaughtObj(gm) as GameObject;
            bool screen = co != null && co.activeInHierarchy;
            if (screen && !_ep.TryChoose(DeathChoice.Jail, Now)) { _dupClicks++; _log.Info("DEATH_SCREEN " + Slot + " repeated choice ignored (episode #" + _ep.Id + " already chose " + _ep.Choice + ")"); return false; }
            if (!screen && _ep.Open && !_ep.Chosen) _ep.TryChoose(DeathChoice.Jail, Now);   // hardcore GotCaught(0) path
            _jails++;
            _log.Info((screen ? "DEATH_SCREEN " + Slot + " choice Go to Jail (episode #" + _ep.Id + "); " : "") + "JAIL begin " + Slot
                + ": original SelectJail (saves the live state first, no checkpoint reload) | before: " + Diag());
            return true;
        }

        static void JailPostfix(object __instance)
        {
            var d = _inst; if (d == null) return;
            try
            {
                if (d._gmDisableSoundSafe(__instance)) return;
                d._failures++; d._ep.Failed();
                d._log.Warn("JAIL failure " + d.Slot + ": the original jail transition did not start loading");
            }
            catch { }
        }

        bool _gmDisableSoundSafe(object gm) { try { return _gmDisableSound(gm); } catch { return true; } }

        static void MenuPrefix(object __instance)
        {
            var d = _inst; if (d == null) return;
            try
            {
                var co = _gmCaughtObj(__instance) as GameObject;
                if (co == null || !co.activeInHierarchy) return;
                if (d._ep.TryChoose(DeathChoice.QuitToMenu, Now)) { d._quits++; d._log.Info("DEATH_SCREEN " + d.Slot + " choice Quit to Menu (episode #" + d._ep.Id + "): original LoadMenu (no save, leaves the session world)"); }
            }
            catch { }
        }

        // ---- per frame: one reference compare; completion / timeout are events ----
        public void Update()
        {
            if (!_ep.Open) return;
            var gm = _world.GameManagerObject;
            if (_ep.Chosen && gm != null && !ReferenceEquals(gm, _epGm))
            {
                string what = _ep.Choice == DeathChoice.Jail ? "JAIL" : _ep.Choice == DeathChoice.Respawn ? "RESPAWN" : "DEATH_SCREEN quit";
                _log.Info(what + " complete " + Slot + " (episode #" + _ep.Id + "): new scene, region " + Regions.Name(Pref("CurrentLocation")) + " (was " + Regions.Name(_epLoc) + ") | after: " + Diag());
                _ep.Complete(); _epGm = null; _relabelled.Clear();
                return;
            }
            if (gm != null && !ReferenceEquals(gm, _epGm) && !_ep.Chosen) { _ep.Complete(); _epGm = null; _relabelled.Clear(); return; }   // scene changed without a choice
            if (_ep.TimedOut(Now, ChoiceTimeout))
            {
                _failures++;
                _log.Warn((_ep.Choice == DeathChoice.Jail ? "JAIL" : "RESPAWN") + " failure " + Slot + ": no scene load " + ChoiceTimeout + " s after the choice (episode #" + _ep.Id + "); choice re-opened");
                _ep.Failed();
                var co = _epGm != null ? _gmCaughtObj(_epGm) as GameObject : null;
                if (co != null && !co.activeSelf) co.SetActive(true);
            }
        }

        public string Stats { get { return "DEATH_STATS deaths " + _deaths + " respawns " + _respawns + " jails " + _jails + " quits " + _quits + " refusedLoads " + _refusedLoads + " failures " + _failures + " dupClicks " + _dupClicks + " dupEvents " + _ep.Duplicates; } }

        public void Dispose()
        {
            if (ReferenceEquals(_inst, this)) _inst = null;
            // a caught screen still open when the session ends gets its original label back (its button reloads the checkpoint again)
            if (_getText != null)
            {
                string orig = null;
                try { orig = _getText.Invoke(null, new object[] { DeathRules.OriginalLabelKey }) as string; } catch { }
                foreach (var c in _relabelled) { try { if (c != null && orig != null) c.GetType().GetProperty("text").SetValue(c, orig, null); } catch { } }
            }
            _relabelled.Clear();
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
