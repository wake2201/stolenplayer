// W6A: shared story progression on the ORIGINAL StoryManager (design + verified IL: src/Shared/Story.cs,
// docs/research/STORY_SYSTEM.md).
//   - Host: its own StoryManager is the campaign. Every change of its live story index (own triggers, polls, loads, applied
//     Player-2 triggers) is published as "story" (per-frame int compare, commit only on change and only while consistent:
//     map loaded, currentStoryScript == story_scripts[story]).
//   - Player 2: StoryManager.AdvanceStory never runs on its copy (prefix). If the call comes from a listed original trigger
//     (StoryTriggers; W6A: note_103 -> story14) whose own original condition just passed on Player 2's PC, it is proposed
//     ("storyAdvance") and the host runs its original AdvanceStory exactly once. Unlisted triggers / polls do nothing on
//     Player 2 (logged once per stage) - the host's campaign decides, like a refused interaction.
//   - Player 2 follows the host index like the original scene load (StoryManager.BeginStory): story = host index,
//     InitiateStory() (HUD text, StoryObject gates, tutorials, phone call, job markers), SaveStory() (sandbox SPrefs).
//     StoryRules.ClientStrippedFlags are cleared on that StoryScript for this one call and restored (teleport, chase, debt,
//     location skips stay with the host campaign).
//   - Note visibility: ReadNote.Start hides a code note whose parcel was already taken (SPrefs >= 2). A parcel taken through
//     the shared parcel / loot sync before the story step happened would then block the step for everyone; the postfix keeps
//     a note visible while its own advanceStory step is the current one (never otherwise).
// Cost: per frame one int field read (+ client: one registry head compare); hooks once per AdvanceStory / note read / note
// Start. Network: one <= 140 B proposal per Player-2 trigger, one <= 40 B state per story change.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class StorySync : IDisposable, IStoryHost
    {
        public static StorySync Instance;
        // ParcelSync: when true, Player 2's note story step is handled here (W2B suppression no longer needed).
        public static bool ClientHandlesStory { get { var i = Instance; return i != null && !i._host; } }

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        static Func<object, object> _gmStory, _smCurrent, _smScripts; static Func<object, bool> _gmLoaded, _smFree;
        static Func<object, int> _smStory; static Action<object, int> _setStory; static Action<object> _advance, _initiate, _saveStory;
        static Func<object, string> _scriptKey; static Func<object, bool>[] _flagGet; static Action<object, bool>[] _flagSet;
        static Func<object, bool> _noteAdvance, _noteAddCode; static Func<object, string> _noteDesc, _notePref; static Func<string, int> _prefInt;

        // trigger context (set by the original trigger's hook around its own AdvanceStory call)
        static StoryTriggerKind _ctxKind; static string _ctxPath; static bool _applying;

        object _boundSm; int _lastLocal = int.MinValue; uint _seenHead = uint.MaxValue, _seenEpoch; StoryState _committed; double _nextApply;
        sealed class Pending { public StoryAdvanceRequest Req; public int Tries; public double At; }
        readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        readonly List<Pending> _retry = new List<Pending>();
        readonly HashSet<string> _loggedUnsupported = new HashSet<string>(StringComparer.Ordinal);
        long _suppressed, _proposed, _accepted, _stale, _refused, _published, _followed, _notesKept, _hostLocal;
        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool Ready { get { return _ok; } }

        public StorySync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var sm = AccessTools.TypeByName("StoryManager"); var ss = AccessTools.TypeByName("StoryScript");
                var note = AccessTools.TypeByName("ReadNote"); var sp = AccessTools.TypeByName("SPrefs");
                _gmStory = Fast.Getter<object>(gm, "story_manager"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap");
                _smStory = Fast.Getter<int>(sm, "story"); _setStory = Fast.Setter<int>(sm, "story"); _smCurrent = Fast.Getter<object>(sm, "currentStoryScript");
                _smScripts = Fast.Getter<object>(sm, "story_scripts"); _smFree = Fast.Getter<bool>(sm, "isFreeMode");
                _advance = Fast.Call0(sm, "AdvanceStory"); _initiate = Fast.Call0(sm, "InitiateStory"); _saveStory = Fast.Call0(sm, "SaveStory");
                _scriptKey = Fast.Getter<string>(ss, "story_description");
                var flags = StoryRules.ClientStrippedFlags; _flagGet = new Func<object, bool>[flags.Length]; _flagSet = new Action<object, bool>[flags.Length];
                bool flagsOk = true;
                for (int i = 0; i < flags.Length; i++) { _flagGet[i] = Fast.Getter<bool>(ss, flags[i]); _flagSet[i] = Fast.Setter<bool>(ss, flags[i]); flagsOk &= _flagGet[i] != null && _flagSet[i] != null; }
                _noteAdvance = Fast.Getter<bool>(note, "advanceStory"); _noteAddCode = Fast.Getter<bool>(note, "addParcelCode");
                _noteDesc = Fast.Getter<string>(note, "storyDesc"); _notePref = Fast.Getter<string>(note, "parcel_pref");
                var gi = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
                if (gi != null) _prefInt = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                var advance = sm != null ? AccessTools.Method(sm, "AdvanceStory", Type.EmptyTypes) : null;
                var beginRead = note != null ? AccessTools.Method(note, "BeginRead", Type.EmptyTypes) : null;
                var noteStart = note != null ? AccessTools.Method(note, "Start", Type.EmptyTypes) : null;
                if (_gmStory == null || _gmLoaded == null || _smStory == null || _setStory == null || _smCurrent == null || _smScripts == null || _smFree == null
                    || _advance == null || _initiate == null || _saveStory == null || _scriptKey == null || !flagsOk || _noteAdvance == null || _noteAddCode == null
                    || _noteDesc == null || _notePref == null || _prefInt == null || advance == null || beginRead == null || noteStart == null)
                { _log.Warn("STORY game API incomplete: shared story disabled (each PC keeps its own story copy)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.story");
                _h.Patch(advance, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "AdvancePre")));
                _h.Patch(beginRead, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "NoteReadPre")), postfix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "NoteReadPost")));
                _h.Patch(noteStart, postfix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "NoteStartPost")));
                if (_host && world.Host != null) world.Host.StoryHost = this;
                else world.ProposalResultHook12 = OnProposalResult;
                node.RemoteDisconnected += OnDisconnected;
                _ok = true; Instance = this;
                _log.Info("STORY shared story ready (" + (_host ? "host: publishes its StoryManager index, applies listed Player-2 triggers with the original AdvanceStory" : "client: follows the host index (original InitiateStory presentation), listed triggers proposed, own AdvanceStory never runs") + "; " + StoryTriggers.All.Length + " listed trigger(s))");
            }
            catch (Exception ex) { _log.Error("STORY init failed: shared story disabled", ex); _ok = false; if (_h != null) { _h.UnpatchSelf(); _h = null; } }
        }

        // ================================================================ hooks
        // Player 2: the original AdvanceStory never runs on its copy (the host index is followed instead).
        static bool AdvancePre(object __instance)
        {
            var i = Instance; if (i == null || i._host || _applying) return true;
            try { i.OnClientAdvance(__instance); }
            catch (Exception ex) { i._log.Error("STORY hook AdvanceStory (client)", ex); }
            return false;
        }
        static void NoteReadPre(object __instance)
        {
            if (Instance == null) return;
            try { _ctxKind = StoryTriggerKind.ReadNote; _ctxPath = _noteAdvance(__instance) ? PathOf(((Component)__instance).transform) : null; }
            catch (Exception ex) { _ctxPath = null; Instance._log.Error("STORY hook ReadNote (prefix)", ex); }
        }
        static void NoteReadPost() { _ctxPath = null; }

        // ReadNote.Start IL_0062..IL_007f hid the note (parcel taken). Keep it while its own story step is current.
        static void NoteStartPost(object __instance)
        {
            var i = Instance; if (i == null) return;
            try
            {
                var c = (Component)__instance; var go = c.gameObject;
                if (go.activeSelf || !_noteAdvance(__instance) || !_noteAddCode(__instance) || _prefInt(_notePref(__instance)) < 2) return;
                int idx; string key; bool free;
                if (!i.Local(out idx, out key, out free) || free || !string.Equals(key, _noteDesc(__instance), StringComparison.Ordinal)) return;
                go.SetActive(true); i._notesKept++;
                i._log.Info("STORY note '" + PathOf(c.transform) + "' kept visible: its parcel was taken through the shared world, its story step '" + key + "' is current (original advance path stays available)");
            }
            catch (Exception ex) { i._log.Error("STORY hook ReadNote.Start (postfix)", ex); }
        }

        static string PathOf(Transform t)
        {
            var sb = new StringBuilder(96); var parts = new List<string>(8);
            for (; t != null && parts.Count < 12; t = t.parent) parts.Add(t.name);
            for (int k = parts.Count - 1; k >= 0; k--) { sb.Append(parts[k]); if (k > 0) sb.Append('/'); }
            return sb.ToString();
        }

        void OnClientAdvance(object sm)
        {
            _suppressed++;
            int idx; string key; bool free;
            if (!Local(out idx, out key, out free)) return;
            var t = _ctxPath != null ? StoryTriggers.Find(key, _ctxKind, _ctxPath) : null;
            if (t == null || free)
            {
                string tag = key + "|" + (_ctxPath ?? "-");
                if (_loggedUnsupported.Count < 200 && _loggedUnsupported.Add(tag))
                    _log.Info("STORY Player-2 story step '" + key + "' (" + idx + ") from " + (_ctxPath != null ? _ctxKind + " '" + _ctxPath + "'" : "an unlisted trigger / poll") + ": not a listed shared trigger, the host campaign keeps the step");
                return;
            }
            Send(new Pending { Req = new StoryAdvanceRequest { FromIndex = idx, StageKey = key, Kind = t.Kind, Path = StoryTriggers.Canonical(t) } });
        }

        void Send(Pending p)
        {
            if (!_node.IsConnected || !_world.Synced) { _log.Info("STORY " + p.Req + " not proposed: not connected (the original trigger stays available, repeat it after reconnecting)"); return; }
            p.Tries++; p.At = Now;
            uint id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StoryAdvance, Value = p.Req.ToBytes() });
            _pending[id] = p; _proposed++;
            _log.Info("STORY Player-2 " + p.Req + " proposed #" + id);
        }

        internal bool OnProposalResult(uint id, ProposalCode code)
        {
            Pending p;
            if (_host || !_pending.TryGetValue(id, out p)) return false;
            _pending.Remove(id);
            if (code == ProposalCode.Accepted) _accepted++;
            else if (code == ProposalCode.Stale || code == ProposalCode.Unchanged) _stale++;
            else if (code == ProposalCode.Busy && p.Tries < 5) _retry.Add(p);
            else _refused++;
            _log.Info("STORY proposal #" + id + " " + p.Req + " -> " + code + (code == ProposalCode.Busy && p.Tries < 5 ? " (retried)" : ""));
            return true;
        }

        void OnDisconnected(string reason) { _pending.Clear(); _retry.Clear(); }

        // ================================================================ per frame (Plugin.Update)
        public void Update()
        {
            if (!_ok) return;
            var gm = _world.GameManagerObject; if (gm == null || !_gmLoaded(gm)) return;
            var sm = _gmStory(gm); if (sm == null) return;
            if (!ReferenceEquals(sm, _boundSm)) { _boundSm = sm; _lastLocal = int.MinValue; _seenHead = uint.MaxValue; }
            int local = _smStory(sm);
            if (_host)
            {
                if (local == _lastLocal || _world.Host == null) return;
                if (_world.Host.PublishStory(0)) { _published++; _hostLocal++; _world.FlushOutbox(); _log.Info("STORY host published " + Committed()); }
                int idx; string key; bool free;
                if (Local(out idx, out key, out free)) _lastLocal = local;   // inconsistent while loading: try again next frame
                return;
            }
            double now = Now;
            if (_retry.Count > 0 && now - _retry[0].At > 3.0) { var p = _retry[0]; _retry.RemoveAt(0); Send(p); }
            var reg = _world.Registry;
            if (reg.HeadRev != _seenHead || reg.Epoch != _seenEpoch)
            {
                _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch;
                var e = reg.Get(StateScope.Session, 0, WorldKeys.Story); StoryState s;
                if (e != null && StoryState.TryParse(e.Value, out s) && (_committed == null || !_committed.SameAs(s))) { _committed = s; _nextApply = 0; _log.Info("STORY shared " + s); }
            }
            if (_committed == null || local == _committed.Index || now < _nextApply) return;
            _nextApply = now + 1.0;   // at most one follow per second (a scene load may reset the index once more)
            Follow(sm, local);
        }

        // Original scene-load path (StoryManager.BeginStory): story = index, InitiateStory(); then SaveStory() (sandbox).
        void Follow(object sm, int local)
        {
            if (_smCurrent(sm) == null) return;   // BeginStory not run yet for this StoryManager
            var scripts = _smScripts(sm) as IList; int target = _committed.Index;
            if (scripts == null || target < 0 || target >= scripts.Count) { _log.Warn("STORY host index " + target + " out of range (" + (scripts != null ? scripts.Count : 0) + " stages here): not followed"); return; }
            var script = scripts[target];
            if (!string.Equals(_scriptKey(script), _committed.Key, StringComparison.Ordinal)) { _log.Warn("STORY host stage " + _committed + " != local stage '" + _scriptKey(script) + "' at that index: not followed (game data differs)"); return; }
            var saved = new bool[_flagGet.Length];
            _applying = true;
            try
            {
                for (int k = 0; k < saved.Length; k++) { saved[k] = _flagGet[k](script); if (saved[k]) _flagSet[k](script, false); }
                _setStory(sm, target); _initiate(sm); _saveStory(sm);
            }
            finally
            {
                for (int k = 0; k < saved.Length; k++) if (saved[k]) _flagSet[k](script, true);
                _applying = false;
            }
            _followed++;
            _log.Info("STORY followed host " + _committed + " (local was " + local + "; original InitiateStory presentation" + (Array.IndexOf(saved, true) >= 0 ? ", host-only start effects skipped" : "") + ")");
        }

        // ================================================================ host campaign (IStoryHost)
        bool Local(out int index, out string key, out bool freeMode)
        {
            index = 0; key = null; freeMode = false;
            var gm = _world.GameManagerObject; if (gm == null || !_gmLoaded(gm)) return false;
            var sm = _gmStory(gm); if (sm == null) return false;
            var cur = _smCurrent(sm); var scripts = _smScripts(sm) as IList;
            if (cur == null || scripts == null) return false;
            index = _smStory(sm);
            if (index < 0 || index >= scripts.Count || !ReferenceEquals(scripts[index], cur)) return false;   // between story++ and InitiateStory / loading
            key = _scriptKey(cur); freeMode = _smFree(sm);
            return !string.IsNullOrEmpty(key);
        }

        public bool TryCurrent(out int index, out string key, out bool freeMode) { return Local(out index, out key, out freeMode); }

        public bool Advance(int expectedIndex)
        {
            var gm = _world.GameManagerObject; var sm = gm != null ? _gmStory(gm) : null;
            if (sm == null || _smStory(sm) != expectedIndex) return false;
            _advance(sm);   // original StoryManager.AdvanceStory (host: prefix passes through)
            bool moved = _smStory(sm) != expectedIndex;
            if (moved) _log.Info("STORY host AdvanceStory for Player 2: " + expectedIndex + " -> " + _smStory(sm));
            return moved;
        }

        string Committed() { var e = _world.Registry.Get(StateScope.Session, 0, WorldKeys.Story); StoryState s; return e != null && StoryState.TryParse(e.Value, out s) ? s.ToString() : "none"; }

        public string Stats { get { return "STORY_STATS " + (_host ? "host" : "client") + " suppressed " + _suppressed + " proposed " + _proposed + " ok " + _accepted + " stale " + _stale + " refused " + _refused + " published " + _published + " followed " + _followed + " notesKept " + _notesKept + (_host && _world.Host != null ? " hostApplied " + _world.Host.StoryAdvances : "") + " | " + Committed(); } }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) { Instance = null; _ctxPath = null; _applying = false; }
            if (_ok)
            {
                _node.RemoteDisconnected -= OnDisconnected;
                if (_world.ProposalResultHook12 == (Func<uint, ProposalCode, bool>)OnProposalResult) _world.ProposalResultHook12 = null;
            }
            if (_world.Host != null && ReferenceEquals(_world.Host.StoryHost, this)) _world.Host.StoryHost = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
