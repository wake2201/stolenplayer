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
//   - W6B cooperative story call (src/Shared/Story.cs StoryCallState): StorySequence.Beginn prefix detects an ANSWER of a
//     ringing call (the original answer branch calls Beginn while PlayerScript.callIncoming / the vibrate source are on; the
//     bypass branch and the nextSequence chain call it without). The answer is committed once ("storyCall": host directly,
//     Player 2 by proposal). The other PC mirrors it with the original answer branch (ringing) or, when its ring is still
//     pending, with the original bypassCall branch at PhoneCall1 (no ring, dialogue at once). Declining stays personal.
// Cost: per frame one int field read (+ client: one registry head compare); hooks once per AdvanceStory / note read / note
// Start / call answer / ring start. Network: one <= 140 B proposal per Player-2 trigger, one <= 40 B state per story change.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class StorySync : IDisposable, IStoryHost
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

        // W6B story call
        static Func<object, object> _seqManager, _smPickUp, _smVibrate, _smPs, _gmSeqs; static Func<object, bool> _seqCalling, _seqTry, _psIncoming, _psConvo;
        static Action<object, bool> _setCalling; static Action<object> _seqBeginn, _psStopCall; static bool _callOk, _mirroring;
        StoryCallState _call; string _mirroredCall; uint _callHead = uint.MaxValue, _callEpoch; double _nextCallCheck; readonly Dictionary<uint, StoryCallState> _callPending = new Dictionary<uint, StoryCallState>();
        long _callsAnswered, _callsMirrored, _callsBypassed;
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
                PatchCalls(gm);
                PatchMissions();   // W6B.3 (src/Plugin/StoryMissions.cs)
                if (_host && world.Host != null) world.Host.StoryHost = this;
                else world.ProposalResultHook12 = OnProposalResult;
                node.RemoteDisconnected += OnDisconnected;
                _ok = true; Instance = this;
                _log.Info("STORY shared story ready (" + (_host ? "host: publishes its StoryManager index, applies listed Player-2 triggers with the original AdvanceStory" : "client: follows the host index (original InitiateStory presentation), listed triggers proposed, own AdvanceStory never runs") + "; " + StoryTriggers.All.Length + " listed trigger(s))");
            }
            catch (Exception ex) { _log.Error("STORY init failed: shared story disabled", ex); _ok = false; if (_h != null) { _h.UnpatchSelf(); _h = null; } }
        }

        // ================================================================ W6B story call
        void PatchCalls(Type gm)
        {
            try
            {
                var seq = AccessTools.TypeByName("StorySequence"); var sm = AccessTools.TypeByName("StoryManager"); var ps = AccessTools.TypeByName("PlayerScript");
                _seqManager = Fast.Getter<object>(seq, "Manager"); _seqCalling = Fast.Getter<bool>(seq, "isCalling"); _setCalling = Fast.Setter<bool>(seq, "isCalling");
                _seqTry = Fast.Getter<bool>(seq, "tryToCall"); _seqBeginn = Fast.Call0(seq, "Beginn");
                _smPickUp = Fast.Getter<object>(sm, "pickUp"); _smVibrate = Fast.Getter<object>(sm, "phoneVibrateSource"); _smPs = Fast.Getter<object>(sm, "PS");
                _psIncoming = Fast.Getter<bool>(ps, "callIncoming"); _psConvo = Fast.Getter<bool>(ps, "having_phone_convo"); _psStopCall = Fast.Call0(ps, "StopPhoneCall");
                _gmSeqs = Fast.Getter<object>(gm, "StorySequences");
                var beginn = seq != null ? AccessTools.Method(seq, "Beginn", Type.EmptyTypes) : null; var ring = seq != null ? AccessTools.Method(seq, "PhoneCall1", Type.EmptyTypes) : null;
                _callOk = _seqManager != null && _seqCalling != null && _setCalling != null && _seqTry != null && _seqBeginn != null && _smPickUp != null && _smVibrate != null && _smPs != null
                    && _psIncoming != null && _psConvo != null && _psStopCall != null && _gmSeqs != null && beginn != null && ring != null;
                if (!_callOk) { _log.Warn("STORY call members not found: story calls stay per PC"); return; }
                _h.Patch(beginn, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "CallBeginnPre")));
                _h.Patch(ring, prefix: new HarmonyMethod(AccessTools.Method(typeof(StorySync), "CallRingPre")));
            }
            catch (Exception ex) { _callOk = false; _log.Error("STORY call hooks failed: story calls stay per PC", ex); }
        }

        int SeqIndex(object seq)
        {
            var gm = _world.GameManagerObject; var arr = gm != null ? _gmSeqs(gm) as Array : null;
            if (arr == null) return -1;
            int i = Array.IndexOf(arr, seq); return i >= 0 && i < 256 ? i : -1;
        }

        static void CallBeginnPre(object __instance)
        {
            var i = Instance; if (i == null || _mirroring || !_callOk) return;
            try
            {
                var sm = _seqManager(__instance); var ps = sm != null ? _smPs(sm) : null; var vib = sm != null ? _smVibrate(sm) as AudioSource : null;
                bool answer = (ps != null && _psIncoming(ps)) || (vib != null && vib.isPlaying);   // original answer branch (not bypass / chain)
                if (answer) i.OnLocalAnswer(__instance);
            }
            catch (Exception ex) { i._log.Error("STORY hook StorySequence.Beginn", ex); }
        }

        // Ring about to start (StorySequence.Update tryToCall branch): the other player already answered this call -> the
        // original bypassCall branch instead (isCalling = false, pickUp.Play, Beginn), no ring on this PC.
        static bool CallRingPre(object __instance)
        {
            var i = Instance; if (i == null || !_callOk) return true;
            if (i._call == null) { i.LogRing(null, __instance); return true; }
            try
            {
                int idx; string key; bool free;
                if (!i.Local(out idx, out key, out free)) return true;
                var m = StoryCallRules.Decide(i._call, i.Slot, key, i.SeqIndex(__instance), false, true, false);
                if (m != CallMirror.AnswerWhenRung) { i.LogRing(key, __instance); return true; }
                i.Mirror(__instance, false);
                i._callsBypassed++;
                return false;
            }
            catch (Exception ex) { i._log.Error("STORY hook StorySequence.PhoneCall1", ex); return true; }
        }

        byte Slot { get { return _host ? (byte)1 : (byte)2; } }

        // W6B.1 diagnostics: one line per ring start (event, not per frame)
        void LogRing(string key, object seq)
        {
            if (key == null) { int idx; bool free; Local(out idx, out key, out free); }
            _log.Info("STORY call ringing here: stage '" + (key ?? "?") + "' sequence " + SeqIndex(seq) + " (original PhoneCall1; answer on either PC is shared)");
        }

        void OnLocalAnswer(object seq)
        {
            int idx; string key; bool free;
            if (!Local(out idx, out key, out free) || free) return;
            int si = SeqIndex(seq); if (si < 0) { _log.Info("STORY call answered here (sequence not in GameManager.StorySequences: not shared)"); return; }
            var st = new StoryCallState { Key = key, Seq = (byte)si, By = Slot };
            _callsAnswered++;
            if (_call != null && _call.SameCall(st)) return;   // the other player answered first: nothing to commit
            if (_host)
            {
                string why = null; var code = _world.Host != null ? _world.Host.AnswerStoryCall(1, st, out why) : ProposalCode.Busy;
                _world.FlushOutbox();
                _log.Info("STORY host answered the " + st + " -> " + code + (code != ProposalCode.Accepted && why != null ? " (" + why + ")" : ""));
                return;
            }
            if (!_node.IsConnected || !_world.Synced) { _log.Info("STORY " + st + " not shared: not connected"); return; }
            uint id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StoryCallAnswer, Value = st.ToBytes() });
            _callPending[id] = st;
            _log.Info("STORY Player-2 answered the " + st + ": proposed #" + id);
        }

        // The original answer branch (ringing) / bypass branch (ring pending), run for the other player's committed answer.
        void Mirror(object seq, bool ringing)
        {
            var sm = _seqManager(seq); var ps = sm != null ? _smPs(sm) : null;
            var pick = sm != null ? _smPickUp(sm) as AudioSource : null; var vib = sm != null ? _smVibrate(sm) as AudioSource : null;
            _mirroring = true;
            try
            {
                _setCalling(seq, false);
                if (pick != null) pick.Play();
                _seqBeginn(seq);
                if (ringing) { if (vib != null) vib.Stop(); if (ps != null) _psStopCall(ps); }
            }
            finally { _mirroring = false; }
            _callsMirrored++; _mirroredCall = _call.Key + "|" + _call.Seq;
            _log.Info("STORY " + _call + ": " + (ringing ? "ringing here -> original answer branch" : "ring pending here -> original bypass branch (no ring)") + ", same dialogue on both PCs");
        }

        void CallTick()
        {
            if (!_callOk) return;
            var reg = _world.Registry;
            if (reg.HeadRev != _callHead || reg.Epoch != _callEpoch)   // parse only when the registry changed (no per-frame allocation)
            {
                _callHead = reg.HeadRev; _callEpoch = reg.Epoch;
                var e = reg.Get(StateScope.Session, 0, WorldKeys.StoryCall); StoryCallState s;
                if (e != null && StoryCallState.TryParse(e.Value, out s) && (_call == null || !_call.SameCall(s) || _call.By != s.By)) { _call = s; _log.Info("STORY shared " + s); }
            }
            if (_call == null || _call.By == Slot || _mirroredCall == _call.Key + "|" + _call.Seq) return;
            double now = Now; if (now < _nextCallCheck) return; _nextCallCheck = now + 0.25;
            int idx; string key; bool free;
            if (!Local(out idx, out key, out free)) return;
            var gm = _world.GameManagerObject; var arr = gm != null ? _gmSeqs(gm) as Array : null;
            if (arr == null || _call.Seq >= arr.Length) return;
            var seq = arr.GetValue(_call.Seq); if (seq == null) return;
            var sm = _seqManager(seq); var ps = sm != null ? _smPs(sm) : null;
            var m = StoryCallRules.Decide(_call, Slot, key, _call.Seq, _seqCalling(seq), _seqTry(seq), ps != null && _psConvo(ps));
            if (m == CallMirror.AnswerRinging) Mirror(seq, true);
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
            if (_missionResume)
            {   // W6B.3: the original MissionComplete1 after the host commit - the host already ran AdvanceStory once
                _log.Info("STORY Player-2 story step '" + key + "' (" + idx + ") from the committed task completion: not advanced here (the host advanced once, this PC follows)");
                return;
            }
            var t = _ctxPath != null ? StoryTriggers.Find(key, _ctxKind, _ctxPath) : null;
            if (t == null && WorldObjectSync.InCameraDisable)
            {   // W6B.1: SecurityCamera.DisableCamera onDisableAdvanceStory on Player 2 - the shared camera commit decides on the host
                if (_loggedUnsupported.Count < 200 && _loggedUnsupported.Add(key + "|camera"))
                    _log.Info("STORY Player-2 story step '" + key + "' (" + idx + ") from SecurityCamera onDisableAdvanceStory: not advanced here; the host applies the shared camera disable with its original DisableCamera, which advances the host story once");
                return;
            }
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
            StoryCallState cs;
            if (_callPending.TryGetValue(id, out cs)) { _callPending.Remove(id); _log.Info("STORY call answer #" + id + " " + cs + " -> " + code); return true; }
            if (!_host && OnMissionResult(id, code)) return true;   // W6B.3
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

        void OnDisconnected(string reason) { _pending.Clear(); _retry.Clear(); _callPending.Clear(); _missionsDirty = true; }

        // ================================================================ per frame (Plugin.Update)
        public void Update()
        {
            if (!_ok) return;
            var gm = _world.GameManagerObject; if (gm == null || !_gmLoaded(gm)) return;
            var sm = _gmStory(gm); if (sm == null) return;
            if (!ReferenceEquals(sm, _boundSm)) { _boundSm = sm; _lastLocal = int.MinValue; _seenHead = uint.MaxValue; }
            int local = _smStory(sm);
            if (_callOk && (_host || _world.Synced)) { try { CallTick(); } catch (Exception ex) { _callOk = false; _log.Error("STORY call sync failed: story calls stay per PC", ex); } }
            if (_missionsOk && (_host || _world.Synced)) { try { MissionFrame(Now); } catch (Exception ex) { _missionsOk = false; _log.Error("STORY mission sync failed: cooperative missions disabled", ex); } }
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

        public string Stats { get { return "STORY_STATS " + (_host ? "host" : "client") + " suppressed " + _suppressed + " proposed " + _proposed + " ok " + _accepted + " stale " + _stale + " refused " + _refused + " published " + _published + " followed " + _followed + " notesKept " + _notesKept + " calls answered/mirrored/bypassed " + _callsAnswered + "/" + _callsMirrored + "/" + _callsBypassed + (_host && _world.Host != null ? " hostApplied " + _world.Host.StoryAdvances : "") + MissionStats + " | " + Committed(); } }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) { Instance = null; _ctxPath = null; _applying = false; _ctxPlant = null; _missionResume = false; }
            if (_ok)
            {
                _node.RemoteDisconnected -= OnDisconnected;
                if (_world.ProposalResultHook12 == (Func<uint, ProposalCode, bool>)OnProposalResult) _world.ProposalResultHook12 = null;
            }
            if (_world.Host != null && ReferenceEquals(_world.Host.StoryHost, this)) _world.Host.StoryHost = null;
            if (_world.Host != null && ReferenceEquals(_world.Host.MissionHost, this)) _world.Host.MissionHost = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
