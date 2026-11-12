// M5 W2: host-authoritative world-key mirror (rules + allowlist: src/Shared/WorldMirror.cs).
// Host: keeps HostMirror equal to its live SPrefs (FBPP in-memory) values of the allowlisted families. Changes are detected
// by narrow postfixes on the only original writers (verified IL, see KeyPolicy evidence) and coalesced into one delta per
// frame; a host save (re)load (FBPP._latestData replaced: Continue, profile load, new game) is a new generation with a
// full snapshot. A 60 s audit rescan catches writers we do not hook (logged; should stay 0). Nothing is written to disk.
// Client: applies the host state into its sandbox SPrefs memory - incremental writes for deltas, a full reconcile at every
// MainScene load (SceneManager.sceneLoaded fires before Start(), where the original objects read these keys) and after its
// own game rewrote a family (daily events / hot item rollover). Its own loot picks stay local until the host has a value.
// Its Inventory.daysInGame follows the host's live day count. The client never writes the host save (SaveGuard sandbox).
// Not done here (documented): live refresh of already-started objects (existing NPC daily events, hot-item shop flags,
// already-spawned loot) - they pick the mirrored values up at the next scene load.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    internal sealed class WorldKeyMirror : IDisposable
    {
        public static WorldKeyMirror Instance;
        const string DaysKey = "DaysInGame";
        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        readonly HostMirror _hm; readonly ClientMirror _cm;
        readonly NetDataWriter _w = new NetDataWriter(), _scratch = new NetDataWriter();
        bool _ok; Harmony _h;

        // FBPP / game access (compiled once)
        FieldInfo _latest, _config, _intData; MethodInfo _getSaveFile;
        Func<object, string> _itemKey; Func<object, int> _itemValue;
        Func<string, int> _get; Action<string, int> _set; Action<string> _deleteInt;
        Func<object, object> _gmPs, _psInv; Func<object, bool> _gmLoaded; Func<object, int> _invDays; Action<object, int> _setInvDays;

        readonly Dictionary<string, int> _scan = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly List<KeyWrite> _writes = new List<KeyWrite>();
        readonly HashSet<string> _dirtyKeys = new HashSet<string>(StringComparer.Ordinal);
        int _dirtyFams;   // bit per mirrored family id
        object _seenData; double _nextPoll, _nextAudit, _nextStats, _nextResync, _nextSnapTry; bool _sentToClient;

        // diagnostics
        readonly CostMeter _scanCost = new CostMeter(), _applyCost = new CostMeter(), _frameCost = new CostMeter();
        long _snapOut, _snapIn, _deltaOut, _deltaIn, _bytesOut, _bytesIn, _dupes, _gaps, _resyncs, _rejected, _writesSet, _writesDel, _localMarks, _auditFixes, _hookCalls, _daysAligned;
        int _maxSnapBytes;

        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool Ready { get { return _ok; } }

        public WorldKeyMirror(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var fbpp = AccessTools.TypeByName("FBPP"); var model = AccessTools.TypeByName("FBPPFileModel");
                var sp = AccessTools.TypeByName("SPrefs"); var gm = AccessTools.TypeByName("GameManager");
                var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory");
                _latest = fbpp != null ? AccessTools.Field(fbpp, "_latestData") : null;
                _config = fbpp != null ? AccessTools.Field(fbpp, "_config") : null;
                _getSaveFile = fbpp != null ? AccessTools.Method(fbpp, "GetSaveFile", Type.EmptyTypes) : null;
                _intData = model != null ? AccessTools.Field(model, "IntData") : null;
                var itemT = _intData != null ? _intData.FieldType.GetElementType() : null;
                _itemKey = Fast.Getter<string>(itemT, "Key"); _itemValue = Fast.Getter<int>(itemT, "Value");
                var gi = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
                var si = sp != null ? AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) }) : null;
                var di = fbpp != null ? AccessTools.Method(fbpp, "DeleteInt", new[] { typeof(string) }) : null;
                if (gi != null) _get = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                if (si != null) _set = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), si);
                if (di != null) _deleteInt = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), di);
                _gmPs = Fast.Getter<object>(gm, "PS"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap"); _psInv = Fast.Getter<object>(ps, "invScript");
                _invDays = Fast.Getter<int>(inv, "daysInGame"); _setInvDays = Fast.Setter<int>(inv, "daysInGame");
                bool core = _latest != null && _config != null && _getSaveFile != null && _intData != null && itemT != null && _itemKey != null && _itemValue != null
                    && _get != null && _set != null && _deleteInt != null && _gmPs != null && _gmLoaded != null && _psInv != null && _invDays != null && _setInvDays != null;
                if (!core) { _log.Warn("MIRROR game API incomplete: world-key mirror disabled (original behaviour, no shared world keys)"); return; }

                if (_host) _hm = new HostMirror(NewEpoch()); else _cm = new ClientMirror();
                InstallHooks();
                _node.MessageReceived += OnMessage;
                _node.RemoteConnected += OnConnected;
                _node.RemoteDisconnected += OnDisconnected;
                SceneManager.sceneLoaded += OnSceneLoaded;
                _ok = true; Instance = this;
                _log.Info("MIRROR ready (" + (_host ? "host: epoch " + _hm.Header.Epoch.ToString("X8") + ", publishes" : "client: applies") + " families: ItemDay_*, DailyEvent*, EventDay, GeneratedEvents, CurrentHotItem, CurrentDayHotItem + live day count)");
            }
            catch (Exception ex) { _log.Error("MIRROR init failed (world-key mirror disabled)", ex); _ok = false; }
        }

        static uint NewEpoch() { uint e = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0); return e == 0 ? 1u : e; }

        // ---------------------------------------------------------------- hooks (verified original writers)
        void InstallHooks()
        {
            _h = new Harmony("com.ts2mp.multiplayer.worldkeys");
            Hook("Pickupable", "SetDayRespawn", "DayRespawnPostfix");
            Hook("DailyEventManager", "SetNextDay", "DailyPostfix");
            Hook("DailyEventManager", "GenerateMonthEvents", "DailyPostfix");
            Hook("DailyEventManager", "SetTodayAsBurglarDay", "DailyPostfix");
            Hook("HotItemManager", "NewRandomItem", "HotPostfix");
        }

        void Hook(string type, string method, string postfix)
        {
            var t = AccessTools.TypeByName(type);
            var m = t != null ? AccessTools.Method(t, method, Type.EmptyTypes) : null;
            if (m == null) { _log.Warn("MIRROR hook target missing: " + type + "." + method + " (audit rescan still covers it)"); return; }
            try { _h.Patch(m, null, new HarmonyMethod(AccessTools.Method(typeof(WorldKeyMirror), postfix))); }
            catch (Exception ex) { _log.Warn("MIRROR hook failed: " + type + "." + method + " (" + ex.Message + ")"); }
        }

        static void DayRespawnPostfix(object __instance)
        {
            var self = Instance; var c = __instance as Component;
            if (self == null || c == null) return;
            try
            {
                self._hookCalls++;
                string key = "ItemDay_" + c.transform.name;
                if (KeyPolicy.MirroredFamily(key) == 0) return;   // "dropped" etc.: the original writes no key
                if (self._host) self._dirtyKeys.Add(key);
                else if (self._cm.MarkLocal(key)) { self._localMarks++; self._log.Info("MIRROR local loot marker " + key + " (client pick, kept until the host has a value)"); }
            }
            catch (Exception ex) { self._log.Warn("MIRROR respawn hook: " + ex.Message); }
        }

        static void DailyPostfix() { var s = Instance; if (s != null) { s._hookCalls++; s._dirtyFams |= Bit(KeyPolicy.FamDailyEvent) | Bit(KeyPolicy.FamEventDay) | Bit(KeyPolicy.FamGeneratedEvents); } }
        static void HotPostfix() { var s = Instance; if (s != null) { s._hookCalls++; s._dirtyFams |= Bit(KeyPolicy.FamHotItem) | Bit(KeyPolicy.FamHotItemDay); } }
        static int Bit(byte fam) { return 1 << fam; }

        // ---------------------------------------------------------------- FBPP memory access
        object Data()
        {
            if (_config.GetValue(null) == null) return null;   // FBPP not started yet
            var d = _latest.GetValue(null);
            if (d == null) d = _getSaveFile.Invoke(null, null);   // original lazy load
            return d;
        }

        // All allowlisted keys currently in SPrefs memory (one pass over FBPPFileModel.IntData; no per-key search).
        bool Scan(Dictionary<string, int> into)
        {
            into.Clear();
            long t0 = Stopwatch.GetTimestamp();
            var d = Data(); if (d == null) return false;
            var arr = _intData.GetValue(d) as object[];
            if (arr != null)
                for (int i = 0; i < arr.Length; i++)
                {
                    var it = arr[i]; if (it == null) continue;
                    string k = _itemKey(it);
                    if (KeyPolicy.MirroredFamily(k) != 0) into[k] = _itemValue(it);
                }
            _scanCost.Add(Stopwatch.GetTimestamp() - t0);
            return true;
        }

        object Inventory(bool requireLoaded)
        {
            var gm = _world.GameManagerObject; if (gm == null) return null;
            try
            {
                if (requireLoaded && !_gmLoaded(gm)) return null;
                var ps = _gmPs(gm); if (ps == null) return null;
                return _psInv(ps);
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- per frame
        public void Update()
        {
            if (!_ok) return;
            long t0 = Stopwatch.GetTimestamp();
            if (_host) HostFrame(); else ClientFrame();
            _frameCost.Add(Stopwatch.GetTimestamp() - t0);
            if (Now >= _nextStats) { _nextStats = Now + 60; _log.Info(Stats); }
        }

        void HostFrame()
        {
            double now = Now;
            if (now >= _nextPoll)
            {
                _nextPoll = now + 1.0;
                ReloadCheck("poll");
                var inv = Inventory(true);
                if (inv != null && _hm.Based)
                {
                    int days = _invDays(inv);
                    if (days > _hm.Header.Days) _hm.SetDays(days);   // original only increments within one loaded save
                }
            }
            if (!_hm.Based) { _dirtyKeys.Clear(); _dirtyFams = 0; return; }   // the base scan will contain these changes
            if (_node.IsConnected && !_sentToClient && now >= _nextSnapTry) { _nextSnapTry = now + 5; SendSnapshot("client not yet based"); }
            if (_dirtyKeys.Count > 0)
            {
                foreach (var k in _dirtyKeys) _hm.Set(k, _get(k));
                _dirtyKeys.Clear();
            }
            if (_dirtyFams != 0 || now >= _nextAudit)
            {
                bool audit = now >= _nextAudit;
                if (audit) _nextAudit = now + 60;
                if (Scan(_scan))
                {
                    int n = 0;
                    if (audit) n = _hm.Reconcile(0, _scan);
                    else for (byte f = 1; f < 31; f++) if ((_dirtyFams & Bit(f)) != 0) n += _hm.Reconcile(f, _scan);
                    if (audit && n > 0) { _auditFixes += n; _log.Warn("MIRROR_AUDIT " + n + " change(s) found without a writer hook (now mirrored)"); }
                }
                _dirtyFams = 0;
            }
            if (_hm.HasPending)
            {
                int n;
                if (_node.IsConnected && _sentToClient)
                {
                    _w.Reset();
                    if (_hm.TakeDelta(_w, out n)) { Send(); _deltaOut++; if (_deltaOut <= 20 || _deltaOut % 50 == 0) _log.Info("MIRROR_DELTA sent " + _hm.Header + " changes=" + n + " bytes=" + _w.Length); }
                    else _log.Error("MIRROR_DELTA could not be encoded (limit) - client will resync");
                }
                else { _scratch.Reset(); _hm.TakeDelta(_scratch, out n); }   // nobody to tell; the next snapshot carries the state
            }
        }

        // A replaced FBPP model = the host loaded a save (Continue / profile / new game): new generation, full snapshot.
        void ReloadCheck(string why)
        {
            object d;
            try { d = _config.GetValue(null) != null ? _latest.GetValue(null) : null; } catch { d = null; }
            if (d == null || ReferenceEquals(d, _seenData)) return;
            _seenData = d;
            if (!Scan(_scan)) return;
            int profile = PlayerPrefs.GetInt("Profile", 0);
            var inv = Inventory(true);
            int days = inv != null ? _invDays(inv) : _get(DaysKey);
            _hm.Rebase(profile, days, _scan);
            _log.Info("MIRROR_BASE host save loaded (" + why + "): " + _hm.Header + " keys=" + _hm.Count);
            if (_node.IsConnected) SendSnapshot("host save (re)loaded");
        }

        void SendSnapshot(string why)
        {
            if (!_hm.Based) ReloadCheck("snapshot request");
            if (!_hm.Based || !_node.IsConnected) return;
            _w.Reset();
            if (!_hm.WriteSnapshot(_w)) { _log.Error("MIRROR_SNAPSHOT exceeds limits (" + _hm.Count + " keys): not sent, client keeps its own world keys"); return; }
            Send(); _snapOut++; _sentToClient = true;
            if (_w.Length > _maxSnapBytes) _maxSnapBytes = _w.Length;
            _log.Info("MIRROR_SNAPSHOT sent (" + why + ") " + _hm.Header + " keys=" + _hm.Count + " bytes=" + _w.Length);
        }

        void ClientFrame()
        {
            if (!_cm.Has) return;
            if (_dirtyFams != 0)
            {
                int fams = _dirtyFams; _dirtyFams = 0;
                for (byte f = 1; f < 31; f++) if ((fams & Bit(f)) != 0) Reconcile(f, "client rollover re-assert");
            }
            if (Now >= _nextPoll)
            {
                _nextPoll = Now + 1.0;
                AlignDays();
            }
        }

        void AlignDays()
        {
            var inv = Inventory(true);
            if (inv == null) return;
            int host = _cm.Header.Days, local = _invDays(inv);
            if (local == host) return;
            _setInvDays(inv, host); _daysAligned++;
            _log.Info("MIRROR day count " + local + " -> " + host + " (host)");
        }

        // Make the client's SPrefs family keys equal to the host state (fam 0 = all), plus the DaysInGame key read at scene start.
        void Reconcile(byte fam, string why)
        {
            if (!_cm.Has) return;
            long t0 = Stopwatch.GetTimestamp();
            if (!Scan(_scan)) return;
            _writes.Clear();
            _cm.Plan(_scan, fam, _writes);
            Execute(_writes);
            if (fam == 0 && _get(DaysKey) != _cm.Header.Days) _set(DaysKey, _cm.Header.Days);
            _applyCost.Add(Stopwatch.GetTimestamp() - t0);
            if (_writes.Count > 0 || fam == 0)
                _log.Info("MIRROR_APPLY " + why + ": " + _writes.Count + " write(s) " + _cm.Header + " hostKeys=" + _cm.Count + " localKeys=" + _cm.LocalCount
                    + " time=" + ((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency).ToString("0.00") + "ms");
        }

        void Execute(List<KeyWrite> writes)
        {
            if (writes.Count == 0 || Data() == null) return;   // FBPP not started: applied at the next scene load
            for (int i = 0; i < writes.Count; i++)
            {
                var w = writes[i];
                if (w.Delete) { _deleteInt(w.Key); _writesDel++; }
                else { _set(w.Key, w.Value); _writesSet++; }
            }
        }

        // ---------------------------------------------------------------- scenes / network
        void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            if (!_ok || mode != LoadSceneMode.Single || s.name != "MainScene") return;
            try
            {
                if (_host) ReloadCheck("scene load");
                else Reconcile(0, "scene load (before Start)");
            }
            catch (Exception ex) { _log.Error("MIRROR scene-load step failed", ex); }
        }

        void OnConnected(string id)
        {
            _sentToClient = false;
            if (_host) { try { SendSnapshot("client connected " + id); } catch (Exception ex) { _log.Error("MIRROR snapshot on connect failed", ex); } }
        }

        void OnDisconnected(string reason) { _sentToClient = false; }   // client keeps the last host state; a new snapshot replaces it

        void OnMessage(NetPeer peer, byte type, NetPacketReader r)
        {
            if (type < (byte)MsgType.WorldKeySnapshot || type > (byte)MsgType.WorldKeyResync || !_ok) return;
            try { Handle(type, r); }
            catch (Exception ex) { _rejected++; _log.Error("MIRROR message " + type + " handling failed (ignored)", ex); }
        }

        void Handle(byte type, NetPacketReader r)
        {
            _bytesIn += r.AvailableBytes + 1;
            if (_host)
            {
                uint epoch, gen, rev;
                if (type != (byte)MsgType.WorldKeyResync || !MirrorCodec.TryReadResync(r, out epoch, out gen, out rev)) { _rejected++; _log.Warn("MIRROR unexpected/malformed message " + type + " from client"); return; }
                _resyncs++;
                SendSnapshot("client resync (had epoch=" + epoch.ToString("X8") + " gen=" + gen + " rev=" + rev + ")");
                return;
            }
            if (type == (byte)MsgType.WorldKeySnapshot)
            {
                MirrorHeader h; Dictionary<string, int> values;
                if (!MirrorCodec.TryReadSnapshot(r, out h, out values)) { _rejected++; _log.Error("MIRROR_SNAPSHOT rejected (malformed, over limits or non-allowlisted key)"); return; }
                bool newHistory;
                var res = _cm.ApplySnapshot(h, values, out newHistory);
                if (res != MirrorApply.Applied) { _dupes++; _log.Info("MIRROR_SNAPSHOT ignored (" + res + ") " + h); return; }
                _snapIn++;
                _log.Info("MIRROR_SNAPSHOT received " + h + " keys=" + _cm.Count + (newHistory ? " (new host history)" : ""));
                Reconcile(0, "snapshot");
                AlignDays();
            }
            else if (type == (byte)MsgType.WorldKeyDelta)
            {
                MirrorHeader h; List<KeyChange> changes;
                if (!MirrorCodec.TryReadDelta(r, out h, out changes)) { _rejected++; _log.Error("MIRROR_DELTA rejected (malformed or non-allowlisted key)"); RequestResync("malformed delta"); return; }
                _writes.Clear();
                long t0 = Stopwatch.GetTimestamp();
                var res = _cm.ApplyDelta(h, changes, _writes);
                if (res == MirrorApply.Applied)
                {
                    _deltaIn++;
                    Execute(_writes);
                    _applyCost.Add(Stopwatch.GetTimestamp() - t0);
                    if (_deltaIn <= 20 || _deltaIn % 50 == 0) _log.Info("MIRROR_DELTA applied " + h + " changes=" + changes.Count + " writes=" + _writes.Count);
                    if (h.Days != _invDaysSafe()) AlignDays();
                }
                else if (res == MirrorApply.Duplicate) _dupes++;
                else { _gaps++; RequestResync(res + " " + h + " (have " + _cm.Header + ")"); }
            }
        }

        int _invDaysSafe() { var inv = Inventory(true); return inv != null ? _invDays(inv) : _cm.Header.Days; }

        void RequestResync(string why)
        {
            if (!_node.IsConnected || Now < _nextResync) return;
            _nextResync = Now + 2.0;
            _resyncs++;
            _log.Info("MIRROR resync requested: " + why);
            _w.Reset(); MirrorCodec.WriteResync(_w, _cm.Header, why); Send();
        }

        void Send() { _node.Send(_w, DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable); _bytesOut += _w.Length; }

        public string Stats
        {
            get
            {
                long f = Stopwatch.Frequency;
                return "MIRROR_STATS " + (_host ? "host " + _hm.Header + " keys=" + _hm.Count + " rebases=" + _hm.Rebases + " snapOut=" + _snapOut + " maxSnapB=" + _maxSnapBytes + " deltaOut=" + _deltaOut + " auditFixes=" + _auditFixes
                                               : "client " + (_cm.Has ? _cm.Header.ToString() : "no snapshot") + " hostKeys=" + _cm.Count + " localKeys=" + _cm.LocalCount + " snapIn=" + _snapIn + " deltaIn=" + _deltaIn
                                                 + " dupes=" + _dupes + " gaps=" + _gaps + " set=" + _writesSet + " del=" + _writesDel + " daysAligned=" + _daysAligned)
                    + " resyncs=" + _resyncs + " rejected=" + _rejected + " hooks=" + _hookCalls + " bytesOut=" + _bytesOut + " bytesIn=" + _bytesIn
                    + " | frame " + _frameCost.Text(f) + " | scan " + _scanCost.Text(f) + " | apply " + _applyCost.Text(f);
            }
        }

        public void Dispose()
        {
            if (!_ok) return;
            _node.MessageReceived -= OnMessage;
            _node.RemoteConnected -= OnConnected;
            _node.RemoteDisconnected -= OnDisconnected;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
            if (Instance == this) Instance = null;
            _ok = false;
        }
    }
}
