// W2B: shared parcel-locker codes on the ORIGINAL objects and code paths (design + verified IL: src/Shared/Parcels.cs).
//   - Discovery stays the original's on the PC that reads the note / hacks the item (ReadNote.BeginRead, ItemID.HackedElectronic:
//     phone animation, discoveredOnMap, parcel_strings_to_set, achievement, XP). Player 2's discovery is then proposed
//     ("parcelCode") and the host applies the same original mutation to ITS campaign (durable with the host's next save).
//   - The host publishes "parcels" = its live discoveredOnMap set (2 s scan of 14 codes + right after a hook / an applied
//     request). Every PC sets discoveredOnMap = true for each shared code (never false) - after a change, after every scene
//     (re)load (MainScene's ParcelStorage instance changed: travel / reload) and after a reconnect snapshot - so the original
//     ParcelMinigame accepts the code on both PCs. A code learned from the other player plays the original "new code" phone
//     animation (Inventory.AddNewParcelCode, live changes only).
//   - Story clue: W6A (src/Plugin/StorySync.cs) owns the story step of a note read by Player 2: the original
//     AdvanceStory call becomes a host-applied shared step (note_103 -> story14). Fallback only while StorySync is not
//     running: the prefix clears ReadNote.advanceStory for that one call and the postfix restores it (W2B behaviour, no
//     private story copy on Player 2); everything else of the note runs. The host's own reading keeps the original step.
//   - Locker doors (ParcelLocker.wasOpened) are region objects: WorldObjectSync / WorldObjectBridge.OpenLocker. Parcel loot
//     inside: WorldLootSync (W4.2D: one take, the taker's inventory, host parcel list mutation).
// Cost: client = one field read + one registry head compare per frame; host = one 14-entry bool scan every 2 s; hooks once per
// note read / hack. Network: one <= 50 B proposal per Player-2 discovery, one <= 300 B state per change.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class ParcelSync : IDisposable, IParcelHost
    {
        public static ParcelSync Instance;

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        Func<object, object> _gmStorage, _gmPs, _psInv, _storageCodes; Func<object, bool> _gmLoaded, _codeDiscovered; Action<object, bool> _setDiscovered;
        Func<object, string> _codePref; Func<object, List<string>> _gmParcelList; Action<object> _addNewCode;
        static Func<object, bool> _noteAddCode, _noteAdvance; static Action<object, bool> _setNoteAdvance; static Func<object, string> _notePref, _hackPref;
        static Func<string, int> _prefInt;

        object _boundStorage; uint _seenHead = uint.MaxValue, _seenEpoch; ParcelState _committed; bool _applyLive;
        ulong _publishedMask = ulong.MaxValue; double _nextScan; bool _scanNow;
        sealed class Pending { public ParcelCodeRequest Req; public int Tries; public double At; }
        readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();
        readonly List<Pending> _retry = new List<Pending>();
        long _localNotes, _localHacks, _proposed, _accepted, _unchanged, _refused, _hostApplied, _published, _applied, _storySuppressed;
        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool Ready { get { return _ok; } }

        public ParcelSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory");
                var st = AccessTools.TypeByName("ParcelStorage"); var pc = AccessTools.TypeByName("ParcelCode"); var note = AccessTools.TypeByName("ReadNote");
                var item = AccessTools.TypeByName("ItemID"); var sp = AccessTools.TypeByName("SPrefs");
                _gmStorage = Fast.Getter<object>(gm, "ParcelStorag"); _gmPs = Fast.Getter<object>(gm, "PS"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap");
                _gmParcelList = Fast.Getter<List<string>>(gm, "parcel_strings_to_set"); _psInv = Fast.Getter<object>(ps, "invScript");
                _storageCodes = Fast.Getter<object>(st, "parcelCodes"); _codePref = Fast.Getter<string>(pc, "parcel_code_pref");
                _codeDiscovered = Fast.Getter<bool>(pc, "discoveredOnMap"); _setDiscovered = Fast.Setter<bool>(pc, "discoveredOnMap");
                _addNewCode = Fast.Call0(inv, "AddNewParcelCode");
                _noteAddCode = Fast.Getter<bool>(note, "addParcelCode"); _notePref = Fast.Getter<string>(note, "parcel_pref");
                _noteAdvance = Fast.Getter<bool>(note, "advanceStory"); _setNoteAdvance = Fast.Setter<bool>(note, "advanceStory");
                _hackPref = Fast.Getter<string>(item, "hacked_parcel_pref");
                var gi = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
                if (gi != null) _prefInt = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                var beginRead = note != null ? AccessTools.Method(note, "BeginRead", Type.EmptyTypes) : null;
                var hacked = item != null ? AccessTools.Method(item, "HackedElectronic", Type.EmptyTypes) : null;
                if (_gmStorage == null || _gmPs == null || _gmLoaded == null || _gmParcelList == null || _psInv == null || _storageCodes == null || _codePref == null || _codeDiscovered == null
                    || _setDiscovered == null || _addNewCode == null || _noteAddCode == null || _notePref == null || _noteAdvance == null || _setNoteAdvance == null || _hackPref == null
                    || _prefInt == null || beginRead == null || hacked == null)
                { _log.Warn("PARCEL game API incomplete: shared parcel codes disabled (codes stay per PC)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.parcels");
                _h.Patch(beginRead, prefix: new HarmonyMethod(AccessTools.Method(typeof(ParcelSync), "NotePre")), postfix: new HarmonyMethod(AccessTools.Method(typeof(ParcelSync), "NotePost")));
                _h.Patch(hacked, prefix: new HarmonyMethod(AccessTools.Method(typeof(ParcelSync), "HackPre")), postfix: new HarmonyMethod(AccessTools.Method(typeof(ParcelSync), "HackPost")));
                if (_host && world.Host != null) world.Host.ParcelHost = this;
                else world.ProposalResultHook11 = OnProposalResult;
                node.RemoteDisconnected += OnDisconnected;
                _ok = true; Instance = this;
                _log.Info("PARCEL shared parcel codes ready (" + (_host ? "host: publishes its campaign's discovered codes, applies Player-2 discoveries" : "client: own discoveries proposed to the host, shared codes applied") + "; hooks ReadNote.BeginRead, ItemID.HackedElectronic)");
            }
            catch (Exception ex) { _log.Error("PARCEL init failed: shared parcel codes disabled", ex); _ok = false; if (_h != null) { _h.UnpatchSelf(); _h = null; } }
        }

        // ================================================================ hooks
        // __state bit 0: the code of this note was not yet discovered here; bit 1: advanceStory cleared for this call.
        static void NotePre(object __instance, ref int __state)
        {
            __state = 0; var i = Instance; if (i == null) return;
            try
            {
                if (_noteAddCode(__instance) && !i.Discovered(_notePref(__instance))) __state |= 1;
                if (!i._host && !StorySync.ClientHandlesStory && _noteAdvance(__instance)) { _setNoteAdvance(__instance, false); __state |= 2; i._storySuppressed++; }   // W6A: fallback only
            }
            catch (Exception ex) { i._log.Error("PARCEL hook ReadNote (prefix)", ex); }
        }
        static void NotePost(object __instance, int __state)
        {
            if ((__state & 2) != 0) _setNoteAdvance(__instance, true);   // restored even if the session ended during the call
            var i = Instance; if (i == null) return;
            try
            {
                if ((__state & 2) != 0) { i._log.Info("PARCEL story note '" + ((Component)__instance).name + "' read by Player 2: its story step stays with the host (StorySync not running)"); }
                string pref = _notePref(__instance);
                if ((__state & 1) != 0 && i.Discovered(pref)) i.OnLocal(ParcelSource.Note, pref, ((Component)__instance).name);
            }
            catch (Exception ex) { i._log.Error("PARCEL hook ReadNote (postfix)", ex); }
        }
        // __state: the hacked code was not queued in parcel_strings_to_set before the original call
        static void HackPre(object __instance, ref bool __state)
        {
            __state = false; var i = Instance; if (i == null) return;
            try { string pref = _hackPref(__instance); __state = !string.IsNullOrEmpty(pref) && !i.Queued(pref); }
            catch (Exception ex) { i._log.Error("PARCEL hook HackedElectronic (prefix)", ex); }
        }
        static void HackPost(object __instance, bool __state)
        {
            var i = Instance; if (i == null || !__state) return;
            try { string pref = _hackPref(__instance); if (i.Queued(pref)) i.OnLocal(ParcelSource.Hack, pref, "hacked item"); }
            catch (Exception ex) { i._log.Error("PARCEL hook HackedElectronic (postfix)", ex); }
        }

        void OnLocal(ParcelSource src, string pref, string what)
        {
            if (src == ParcelSource.Note) _localNotes++; else _localHacks++;
            if (_host) { _scanNow = true; _log.Info("PARCEL P1 " + (src == ParcelSource.Note ? "discovered code '" + pref + "' (note '" + what + "')" : "queued code '" + pref + "' (" + what + ", usable after the next save + load like the original)")); return; }
            if (!ParcelRules.ValidPref(pref)) return;
            Send(new Pending { Req = new ParcelCodeRequest { Source = src, Pref = pref } }, what);
        }

        void Send(Pending p, string what)
        {
            if (!_node.IsConnected || !_world.Synced) { _log.Info("PARCEL code '" + p.Req.Pref + "' known here only: not connected (proposed again on the next discovery)"); return; }
            p.Tries++; p.At = Now;
            uint id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ParcelCode, Value = p.Req.ToBytes() });
            _pending[id] = p; _proposed++;
            _log.Info("PARCEL Player-2 " + p.Req + " (" + what + ") proposed #" + id);
        }

        internal bool OnProposalResult(uint id, ProposalCode code)
        {
            Pending p;
            if (_host || !_pending.TryGetValue(id, out p)) return false;
            _pending.Remove(id);
            if (code == ProposalCode.Accepted) _accepted++;
            else if (code == ProposalCode.Unchanged) _unchanged++;
            else if (code == ProposalCode.Busy && p.Tries < 5) _retry.Add(p);
            else _refused++;
            _log.Info("PARCEL code proposal #" + id + " " + p.Req + " -> " + code + (code == ProposalCode.Busy && p.Tries < 5 ? " (retried)" : ""));
            return true;
        }

        void OnDisconnected(string reason) { _pending.Clear(); _retry.Clear(); }

        // ================================================================ per frame (Plugin.Update)
        public void Update()
        {
            if (!_ok) return;
            var gm = _world.GameManagerObject;
            var storage = gm != null ? _gmStorage(gm) : null;
            if (!ReferenceEquals(storage, _boundStorage))
            {
                _boundStorage = storage; _applyLive = false; _scanNow = true;   // new MainScene instance: re-apply silently
                if (storage != null) _seenHead = uint.MaxValue;
            }
            if (storage == null || !_gmLoaded(gm)) return;
            double now = Now;
            if (_host)
            {
                if (_scanNow || now >= _nextScan) { _scanNow = false; _nextScan = now + 2.0; HostScan(); }
                return;
            }
            if (_retry.Count > 0 && now - _retry[0].At > 3.0) { var p = _retry[0]; _retry.RemoveAt(0); Send(p, "retry"); }
            ReadCommitted(storage);
        }

        void HostScan()
        {
            ulong mask = Mask();
            if (mask == _publishedMask || _world.Host == null) return;
            _publishedMask = mask;
            if (_world.Host.PublishParcels()) { _published++; _world.FlushOutbox(); _log.Info("PARCEL host codes published: " + Committed()); }
        }

        string Committed() { var e = _world.Registry.Get(StateScope.Session, 0, WorldKeys.Parcels); ParcelState s; return e != null && ParcelState.TryParse(e.Value, out s) ? s.ToString() : "none"; }

        void ReadCommitted(object storage)
        {
            var reg = _world.Registry;
            if (reg.HeadRev == _seenHead && reg.Epoch == _seenEpoch) return;
            _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch;
            var e = reg.Get(StateScope.Session, 0, WorldKeys.Parcels);
            ParcelState s;
            if (e == null || !ParcelState.TryParse(e.Value, out s)) return;
            bool changed = _committed == null || !_committed.SameAs(s);
            _committed = s;
            int n = 0;
            var codes = _storageCodes(storage) as IList;
            if (codes != null)
                foreach (var c in codes)
                    if (c != null && !_codeDiscovered(c) && s.Has(_codePref(c))) { _setDiscovered(c, true); n++; }
            if (n > 0)
            {
                _applied += n;
                if (_applyLive) { var inv = Inventory(); if (inv != null) _addNewCode(inv); }   // original "new code" phone animation
                _log.Info("PARCEL " + n + " shared code(s) applied (" + (_applyLive ? "learned from the other player" : "after a scene load / join, silently") + "): " + s);
            }
            else if (changed) _log.Info("PARCEL shared codes: " + s);
            _applyLive = true;
        }

        // ================================================================ host campaign (IParcelHost)
        IList Codes() { var gm = _world.GameManagerObject; var st = gm != null ? _gmStorage(gm) : null; return st != null ? _storageCodes(st) as IList : null; }
        object Inventory() { var gm = _world.GameManagerObject; var ps = gm != null ? _gmPs(gm) : null; return ps != null ? _psInv(ps) : null; }

        ulong Mask()
        {
            var codes = Codes(); ulong m = 0;
            if (codes == null) return ulong.MaxValue;
            for (int i = 0; i < codes.Count && i < 64; i++) { var c = codes[i]; if (c != null && _codeDiscovered(c)) m |= 1UL << i; }
            return m;
        }

        bool Discovered(string pref)
        {
            var codes = Codes(); if (codes == null || string.IsNullOrEmpty(pref)) return false;
            foreach (var c in codes) if (c != null && _codeDiscovered(c) && string.Equals(_codePref(c), pref, StringComparison.Ordinal)) return true;
            return false;
        }

        bool Queued(string pref)
        {
            var gm = _world.GameManagerObject; var l = gm != null ? _gmParcelList(gm) : null;
            return l != null && l.Contains(pref);
        }

        public bool TryDiscovered(out List<string> prefs)
        {
            prefs = null;
            var gm = _world.GameManagerObject; var codes = Codes();
            if (gm == null || codes == null || !_gmLoaded(gm)) return false;
            prefs = new List<string>(codes.Count);
            foreach (var c in codes) if (c != null && _codeDiscovered(c)) prefs.Add(_codePref(c));
            return true;
        }

        public bool Known(string pref)
        {
            var codes = Codes(); if (codes == null) return false;
            foreach (var c in codes) if (c != null && string.Equals(_codePref(c), pref, StringComparison.Ordinal)) return true;
            return false;
        }

        public ParcelApply Apply(byte slot, ParcelSource source, string pref)
        {
            var gm = _world.GameManagerObject; var list = gm != null ? _gmParcelList(gm) : null; var codes = Codes();
            if (list == null || codes == null) return ParcelApply.NotAllowed;
            int saved = _prefInt(pref);
            if (source == ParcelSource.Hack)
            {
                // ItemID.HackedElectronic IL_0033..IL_005c (queue only; discoveredOnMap follows the original save + load)
                if (!ParcelRules.HackQueues(saved) || list.Contains(pref)) return ParcelApply.AlreadyKnown;
                list.Add(pref); _hostApplied++;
                _log.Info("PARCEL P" + slot + " hacked code '" + pref + "' queued in the host campaign (original parcel_strings_to_set, saved at the next checkpoint)");
                return ParcelApply.Applied;
            }
            // ReadNote.BeginRead IL_004d..IL_00d8: the first matching, not yet discovered code with SPrefs == 0
            foreach (var c in codes)
            {
                if (c == null || !string.Equals(_codePref(c), pref, StringComparison.Ordinal) || !ParcelRules.NoteDiscovers(_codeDiscovered(c), saved)) continue;
                _setDiscovered(c, true);
                var inv = Inventory(); if (inv != null) _addNewCode(inv);
                list.Add(pref); _hostApplied++; _scanNow = true;
                _log.Info("PARCEL P" + slot + " discovered code '" + pref + "': applied to the host campaign (original discoveredOnMap + parcel_strings_to_set, saved at the next checkpoint)");
                return ParcelApply.Applied;
            }
            return ParcelApply.AlreadyKnown;
        }

        public string Stats { get { return "PARCEL_STATS " + (_host ? "host" : "client") + " notes " + _localNotes + " hacks " + _localHacks + " proposed " + _proposed + " ok " + _accepted + " unchanged " + _unchanged + " refused " + _refused + " hostApplied " + _hostApplied + " published " + _published + " applied " + _applied + " storySuppressed " + _storySuppressed + " | " + Committed(); } }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_ok)
            {
                _node.RemoteDisconnected -= OnDisconnected;
                if (_world.ProposalResultHook11 == (Func<uint, ProposalCode, bool>)OnProposalResult) _world.ProposalResultHook11 = null;
            }
            if (_world.Host != null && ReferenceEquals(_world.Host.ParcelHost, this)) _world.Host.ParcelHost = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
