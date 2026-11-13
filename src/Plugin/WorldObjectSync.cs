// W4.2B: shared doors, locks (lockpicking) and windows of a supported region (Madison Street) on the host-owned region-object
// registry. Design, authority and lifetime rules: src/Shared/RegionState.cs; investigation: docs/research/*_SYSTEM.md; the
// replica side lives in src/Plugin/WorldObjectBridge.cs (no gameplay side effects). Supersedes the Phase A WorldDoorSync.
//
// Per region visit (scene instance = Unity scene handle):
//   1. 0.5 s after the region scene loaded (every Door/Lockpick/LockedWindow Start ran: SaveRot, randomlyOpen roll) the
//      local lock/window states are read once and "objScene" is reported (host: in place).
//   2. The region record "wrgn" names the epoch, the initializer and the owner. The initializer sends its scene's canonical
//      initial state ("objInit" chunks: every supported object not in its default state). Everybody reconciles silently
//      to the committed states (absent = default: closed / locked / intact) once the epoch is initialized - this aligns
//      the per-instance randomlyOpen doors without a 100-object limit.
//   3. Afterwards only committed changes are applied (animated, original audio), in the order locks -> windows -> doors.
// Local changes are captured from the original code paths:
//   - doors: PlayerScript.assignDoor watch (≤ 4, quick + slow use), Door.OpenDestroyed postfix (crowbar break-in),
//     Door.ResetLock (player relock; NPC relock only on the region's simulation owner, suppressed on the other instance).
//   - locks: Lockpick.StartLockpicking / PlayerScript.BreakinCrowbar prefixes = host RESERVATION before the original
//     minigame starts (refused -> the press does nothing); Lockpick.DoorUnlock / DoorUnlockKey / BreakPadlock postfixes =
//     the result; the minigame end (PlayerScript.isLockpicking falls) without a result releases the reservation.
//   - windows: LockedWindow.BeginCut prefix (reservation), UnlockWindow / UnlockWindowNoAlarm / BrokeWindow prefix+postfix
//     (the window id is read in the prefix, before the original destroys the LockedWindow component).
//   - Player 2 (client): story locks and mission windows (census metadata, fail closed) are blocked in the prefixes, so
//     no private story/mission progression can start; Player 1 keeps the original behaviour.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    internal sealed class WorldObjectSync : IDisposable
    {
        public static WorldObjectSync Instance;
        static Harmony _h; static bool _patched; static bool _bypass; static int _npcDepth; static int _insideDepth;
        static MethodInfo _startLockpicking, _breakin, _beginCut;

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        readonly WorldObjectBridge _b; bool _ok;
        Func<object, object> _gmPs, _psAssign; Func<object, bool> _gmLoaded, _psLockpicking;
        static readonly ObjectKind[] Kinds = { ObjectKind.Lockpick, ObjectKind.LockedWindow, ObjectKind.Door };   // apply order
        static readonly string[][] PageKeys = BuildKeys();

        enum Phase { Idle, Reported, Initializing, Ready }
        Phase _phase = Phase.Idle;
        int _region = Regions.None, _serial, _statesSerial; double _regionAt, _reportAt, _nextSerialCheck, _retryAt = -1;
        RegionObjectRecord _rec; bool _recOk; uint _recRev = uint.MaxValue, _seenHead = uint.MaxValue; ushort _reconciledEpoch;
        uint _reportPid; bool _initSent; uint _txn;

        // committed state per kind (from the registry pages) and the page revisions it was built from
        readonly Dictionary<uint, ObjectState>[] _com = { new Dictionary<uint, ObjectState>(), new Dictionary<uint, ObjectState>(), new Dictionary<uint, ObjectState>() };
        readonly uint[][] _pageRev = { new uint[ObjectStateRules.MaxPages], new uint[ObjectStateRules.MaxPages], new uint[ObjectStateRules.MaxPages] };
        // local tracked states (this scene instance): locks and window glass are not reliably readable from tags
        readonly Dictionary<uint, byte> _lockLocal = new Dictionary<uint, byte>(), _glassLocal = new Dictionary<uint, byte>();
        readonly Dictionary<uint, Component> _sashOf = new Dictionary<uint, Component>();   // window hash -> sash Door
        readonly HashSet<long> _dirty = new HashSet<long>();                                // refused local changes: re-apply committed
        readonly HashSet<long> _logged = new HashSet<long>();

        sealed class Watch { public Component Door; public bool Open, Broken, Slow; public double Since; }
        readonly List<Watch> _watch = new List<Watch>(4);
        sealed class Pending { public ObjectKind Kind; public uint Hash; public ObjectOp Op; }
        readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>();   // client proposal id -> object
        readonly Dictionary<long, ushort> _expect = new Dictionary<long, ushort>();      // object -> version after our in-flight change(s)
        struct Queued { public ObjectKind Kind; public uint Hash; public byte State; public ObjectOp Op; }
        readonly List<Queued> _queue = new List<Queued>();
        sealed class Res { public ObjectKind Kind; public uint Hash; public Component Comp; public Action Resume; public uint Pid; public bool Held, Started, Committed; public double At; }
        readonly Dictionary<long, Res> _res = new Dictionary<long, Res>();

        long _local, _proposed, _committed, _refused, _applied, _silent, _skipLocked, _skipBusy, _unidentified, _diverged, _npcSuppressed, _storyBlocked, _gateRefused, _initEntries;
        long _localOnly, _slowApplied, _fastFinishes, _fastFinishSent; double _nextStats; long _statsSig = -1;

        static long Key(ObjectKind k, uint h) { return ((long)(byte)k << 32) | h; }
        static int KI(ObjectKind k) { return k == ObjectKind.Lockpick ? 0 : k == ObjectKind.LockedWindow ? 1 : 2; }
        static string[][] BuildKeys()
        {
            var r = new string[3][];
            for (int i = 0; i < 3; i++) { r[i] = new string[ObjectStateRules.MaxPages]; for (int p = 0; p < ObjectStateRules.MaxPages; p++) r[i][p] = ObjectStateRules.RegistryKey(Kinds[i], p); }
            return r;
        }

        public WorldObjectSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            _b = new WorldObjectBridge(log);
            var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript");
            _gmPs = Fast.Getter<object>(gm, "PS"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap"); _psAssign = Fast.Getter<object>(ps, "assignDoor"); _psLockpicking = Fast.Getter<bool>(ps, "isLockpicking");
            _ok = _b.Ok && _gmPs != null && _gmLoaded != null && _psAssign != null && _psLockpicking != null && WorldCensus.Instance != null;
            if (!_ok) { _log.Error("WOBJ original members or census missing: shared doors / locks / windows disabled"); return; }
            if (!Patch(ps)) { _ok = false; return; }
            var census = WorldCensus.Instance;
            if (_host)
            {
                world.Host.ObjectCatalog = census.ObjectKnown;
                world.Host.Objects.Restricted = census.ObjectRestricted;
                census.ObjectsCensused += OnCensused;
            }
            else world.ProposalResultHook7 = OnResult;
            Instance = this;
            // Developer test hook (Local Test only, never used by players): TS2MP_DEV_WOBJ=1 in the CLIENT process drives one
            // door use, one lock unlock, one window unlock and one reservation round trip through the original methods once
            // the region is ready (keyboard interaction cannot be automated). Read once and cleared.
            if (!_host && Environment.GetEnvironmentVariable("TS2MP_DEV_WOBJ") == "1") { _dev = 1; try { Environment.SetEnvironmentVariable("TS2MP_DEV_WOBJ", null); } catch { } _log.Info("DEV_WOBJ test hook armed"); }
            census.CensusLoadedScenes();   // the host pressed F8 inside a region: census that already-loaded scene (objects only)
            _log.Info("WOBJ ready (" + (_host ? "host: commits its own changes, decides Player 2's" : "client: proposes its changes") + "; doors / locks / windows in Madison Street; NPC door swings stay local)");
        }

        bool Patch(Type ps)
        {
            if (_patched) return true;
            try
            {
                _h = new Harmony("com.ts2mp.multiplayer.worldobjects");
                var me = typeof(WorldObjectSync);
                Func<string, HarmonyMethod> hm = n => new HarmonyMethod(AccessTools.Method(me, n));
                var d = _b.DoorT; var lp = _b.LockpickT; var lw = _b.WindowT; var ai = AccessTools.TypeByName("AI");
                _startLockpicking = AccessTools.Method(lp, "StartLockpicking"); _breakin = AccessTools.Method(ps, "BreakinCrowbar"); _beginCut = AccessTools.Method(lw, "BeginCut");
                var targets = new object[][]
                {
                    new object[] { AccessTools.Method(d, "OpenDestroyed"), null, "DoorBrokenPost", null },
                    new object[] { AccessTools.Method(d, "ResetLock"), "ResetLockPre", "ResetLockPost", null },
                    new object[] { AccessTools.Method(ai, "OnTriggerEnter"), "NpcPre", null, "NpcFinal" },
                    new object[] { _startLockpicking, "StartPickPre", null, null },
                    new object[] { _breakin, "BreakinPre", null, null },
                    new object[] { AccessTools.Method(lp, "DoorUnlock"), "UnlockPre", "UnlockPost", null },
                    new object[] { AccessTools.Method(lp, "DoorUnlockKey"), "KeyPre", "KeyPost", null },
                    new object[] { AccessTools.Method(lp, "BreakPadlock"), "PadlockPre", "PadlockPost", null },
                    new object[] { _beginCut, "BeginCutPre", null, null },
                    new object[] { AccessTools.Method(lw, "UnlockWindowNoAlarm"), "InsidePre", null, "InsideFinal" },
                    new object[] { AccessTools.Method(lw, "UnlockWindow"), "GlassPre", "UnlockWindowPost", null },
                    new object[] { AccessTools.Method(lw, "BrokeWindow"), "BrokePre", "BrokePost", null },
                };
                foreach (var t in targets) if (t[0] == null) { _log.Error("WOBJ original method missing (index " + Array.IndexOf(targets, t) + "): shared doors / locks / windows disabled"); return false; }
                foreach (var t in targets)
                    _h.Patch((MethodBase)t[0], prefix: t[1] != null ? hm((string)t[1]) : null, postfix: t[2] != null ? hm((string)t[2]) : null, finalizer: t[3] != null ? hm((string)t[3]) : null);
                _patched = true;
                _log.Info("WOBJ hooks: Door.OpenDestroyed/ResetLock, AI.OnTriggerEnter (NPC context), Lockpick.StartLockpicking/DoorUnlock/DoorUnlockKey/BreakPadlock, PlayerScript.BreakinCrowbar, LockedWindow.BeginCut/UnlockWindow/UnlockWindowNoAlarm/BrokeWindow");
                return true;
            }
            catch (Exception ex) { _log.Error("WOBJ Harmony patching failed: shared doors / locks / windows disabled", ex); try { if (_h != null) _h.UnpatchSelf(); } catch { } return false; }
        }

        static double Now { get { return Time.realtimeSinceStartup; } }
        bool Sharing { get { return _ok && _serial != 0 && ObjectStateRules.SupportedNamespace(_region) && Online; } }
        bool Online { get { return _host || (_node.IsConnected && _world.Synced); } }

        // ================================================================ per frame
        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            int region = _world.LocalRegion;
            if (region != _region) EnterRegion(region, now);
            if (region == Regions.None || !ObjectStateRules.SupportedNamespace(region)) return;   // no work elsewhere
            if (_b.SlowCount > 0) _b.SlowTick();   // W4.2B.1: replica hold-E swings (<= 8 speed writes)
            if (now >= _nextStats) { _nextStats = now + 30.0; LogStatsIfChanged(); }
            var gm = _world.GameManagerObject; var census = WorldCensus.Instance;
            if (gm == null || census == null || !_gmLoaded(gm) || !Online) return;
            if (now >= _nextSerialCheck)
            {
                _nextSerialCheck = now + 1.0;
                int serial = SceneSerial(region);
                if (serial != _serial)
                {
                    _serial = serial; _phase = Phase.Idle; _regionAt = now; ClearTransient();
                    _lockLocal.Clear(); _glassLocal.Clear(); _sashOf.Clear(); _logged.Clear(); _statesSerial = -1;   // a new scene instance: tracked states start over
                    _log.Info("WOBJ " + Regions.Name(region) + " scene instance " + serial);
                }
            }
            if (census.Objects(region) == null) return;
            if (_world.Registry.HeadRev != _seenHead) { _seenHead = _world.Registry.HeadRev; ReadRecord(); }
            switch (_phase)
            {
                case Phase.Idle:
                    if (now - _regionAt < 0.5) return;   // every Start of the region ran (Door.Start SaveRot + randomlyOpen roll)
                    if (_statesSerial != _serial) ReadLocalStates(census);
                    Report(now);
                    break;
                case Phase.Reported:
                case Phase.Initializing:
                    if (!_recOk || !_rec.Active || !_rec.IsPresent(_world.LocalSlot)) { if (now - _reportAt > 2.0) Report(now); break; }
                    if (_rec.Initialized) { BecomeReady(census, now); break; }
                    if (_rec.InitBy == _world.LocalSlot && !_initSent) SendInit(census, now);
                    else if (_rec.InitBy != _world.LocalSlot) _initSent = false;
                    break;
                case Phase.Ready:
                    if (!_recOk || !_rec.Active || !_rec.IsPresent(_world.LocalSlot) || _rec.Epoch != _reconciledEpoch || !_rec.Initialized)
                    {
                        _log.Info("WOBJ region record changed (" + (_recOk ? _rec.ToString() : "none") + "): re-reporting the scene");
                        _phase = Phase.Idle; _regionAt = now - 1; ClearTransient(); break;
                    }
                    if (_world.Registry.HeadRev != _appliedHead || _dirty.Count > 0 || (_retryAt > 0 && now >= _retryAt)) { _retryAt = -1; Reconcile(census, false); }
                    Detect(gm, census, now);
                    if (_queue.Count > 0) FlushQueue(now);
                    if (_res.Count > 0) ReservationFrame(gm, now);
                    if (_dev > 0) DevStep(census, now);
                    break;
            }
        }

        uint _appliedHead = uint.MaxValue;
        readonly HashSet<uint> _initPids = new HashSet<uint>();

        void EnterRegion(int region, double now)
        {
            _region = region; _regionAt = now; _phase = Phase.Idle; _nextSerialCheck = 0;
            ClearTransient();
            if (region == Regions.None || !ObjectStateRules.SupportedNamespace(region)) { _serial = 0; }
        }

        // W4.2B.1: WOBJ_STATS was never written to the log; now every 30 s while in a shared region, only when a counter moved.
        void LogStatsIfChanged()
        {
            long sig = _local + _proposed * 3 + _committed * 7 + _refused * 11 + _applied * 13 + _silent * 17 + _skipLocked * 19 + _skipBusy * 23 + _unidentified * 29
                + _diverged * 31 + _localOnly * 37 + _fastFinishes * 41 + _fastFinishSent * 43 + _storyBlocked * 47 + _gateRefused * 53;
            if (sig == _statsSig) return;
            _statsSig = sig;
            _log.Info(Stats);
        }

        void ClearTransient()
        {
            _b.ClearSlow();
            _watch.Clear(); _expect.Clear(); _pending.Clear(); _queue.Clear(); _dirty.Clear(); _initPids.Clear(); _initSent = false; _recOk = false; _recRev = uint.MaxValue; _appliedHead = uint.MaxValue; _seenHead = uint.MaxValue;
            foreach (var c in _com) c.Clear();
            foreach (var r in _pageRev) for (int i = 0; i < r.Length; i++) r[i] = uint.MaxValue;
            _res.Clear();
        }

        static int SceneSerial(int region)
        {
            var name = Regions.ExpectedScene(region);
            if (name == null) return 0;
            var s = SceneManager.GetSceneByName(name);
            return s.IsValid() && s.isLoaded ? s.handle : 0;
        }

        void ReadRecord()
        {
            var e = _world.Registry.Get(StateScope.Region, _region, ObjectStateRules.RegionKey);
            uint rev = e != null ? e.Rev : 0;
            if (rev == _recRev) return;
            _recRev = rev;
            _recOk = e != null && RegionObjectRecord.TryParse(e.Value, out _rec);
        }

        // Lock / window states readable from this scene instance (fresh load: all default; late census: was_locked / padlock).
        void ReadLocalStates(WorldCensus census)
        {
            _statesSerial = _serial;
            foreach (var r in census.Objects(_region))
            {
                if ((object)r.Comp == null || r.Comp == null) continue;
                if (r.Kind == ObjectKind.Lockpick) { byte s = _b.ReadLockState(r.Comp); if (s != 0 && !_lockLocal.ContainsKey(r.Id.Hash)) _lockLocal[r.Id.Hash] = s; }
                else if (r.Kind == ObjectKind.Door) { WorldObjectId w; if (census.TryWindowOfDoor(r.Comp, out w)) _sashOf[w.Hash] = r.Comp; }
            }
        }

        void Report(double now)
        {
            var census = WorldCensus.Instance; uint digest; int count;
            census.TryDigest(_region, out digest, out count);
            if (census.IsLate(_region)) digest = 0;   // not a pristine scene: no scene metadata to compare
            var rep = new ObjectSceneReport { Ns = _region, Serial = _serial, Digest = digest, Count = (ushort)Math.Min(count, ushort.MaxValue) };
            _reportAt = now; _phase = Phase.Reported; _initSent = false;
            if (_host)
            {
                string why; var code = _world.Host.ObjectScene(_world.LocalSlot, rep, now, out why);
                _world.FlushOutbox();
                _log.Info("WOBJ host scene " + Regions.Name(_region) + " serial " + _serial + " objects " + count + " -> " + code + (why != null ? " (" + why + ")" : ""));
                ReadRecord();
                return;
            }
            _reportPid = _world.SendProposal(new StateEntry { Scope = StateScope.Player, ScopeArg = _world.LocalSlot, Key = WorldKeys.ObjectScene, Value = rep.ToBytes() });
            _log.Info("WOBJ scene report #" + _reportPid + " " + Regions.Name(_region) + " serial " + _serial + " objects " + count + " digest " + digest.ToString("X8"));
        }

        // ================================================================ initialization
        void SendInit(WorldCensus census, double now)
        {
            _initSent = true;
            var all = new List<ObjectInitEntry>();
            foreach (var r in census.Objects(_region))
            {
                if ((object)r.Comp == null || r.Comp == null) continue;
                byte s = LocalState(r.Kind, r.Id.Hash, r.Comp, census);
                if (s != 0) all.Add(new ObjectInitEntry { Kind = (byte)r.Kind, Hash = r.Id.Hash, State = s });
            }
            _initEntries += all.Count;
            var chunks = ObjectInitChunk.Split(_region, _rec.Epoch, all);
            if (chunks == null) { _log.Warn("WOBJ initial state too large (" + all.Count + " objects): the region stays local"); return; }
            int doors = 0, locks = 0, windows = 0;
            foreach (var e in all) { if (e.Kind == (byte)ObjectKind.Door) doors++; else if (e.Kind == (byte)ObjectKind.Lockpick) locks++; else windows++; }
            _log.Info("WOBJ initializer of " + Regions.Name(_region) + " epoch " + _rec.Epoch + ": " + all.Count + " non-default object(s) (doors " + doors + ", locks " + locks + ", windows " + windows + ") in " + chunks.Count + " chunk(s)");
            foreach (var c in chunks)
            {
                if (_host) { string why; var code = _world.Host.ObjectInit(_world.LocalSlot, c, now, out why); _log.Info("WOBJ host init " + (c.Index + 1) + "/" + c.Count + " -> " + code + (why != null ? " (" + why + ")" : "")); }
                else _initPids.Add(_world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectInit, Value = c.ToBytes() }));
            }
            if (_host) { _world.FlushOutbox(); ReadRecord(); }
            _phase = Phase.Initializing;
        }

        // Current local state of one shared object in the registry encoding.
        byte LocalState(ObjectKind k, uint hash, Component c, WorldCensus census)
        {
            byte s;
            switch (k)
            {
                case ObjectKind.Door:
                    WorldObjectId w;
                    if (census.TryWindowOfDoor(c, out w) || !_b.Ordinary(c)) return 0;   // sashes are part of the window id
                    return _b.DoorBits(c);
                case ObjectKind.Lockpick: return _lockLocal.TryGetValue(hash, out s) ? s : (byte)0;
                case ObjectKind.LockedWindow:
                    byte glass = _glassLocal.TryGetValue(hash, out s) ? s : (byte)0;
                    if (glass == 0) return 0;
                    Component sash; return _sashOf.TryGetValue(hash, out sash) && sash != null ? WorldDoorRules.WithSash(glass, _b.DoorBits(sash)) : glass;
            }
            return 0;
        }

        void BecomeReady(WorldCensus census, double now)
        {
            _phase = Phase.Ready; _reconciledEpoch = _rec.Epoch;
            Reconcile(census, true);
            _log.Info("WOBJ " + Regions.Name(_region) + " ready: epoch " + _rec.Epoch + ", " + _rec + "; committed doors " + _com[2].Count + ", locks " + _com[0].Count + ", windows " + _com[1].Count);
        }

        // ================================================================ committed -> local
        // Rebuilds the committed dictionary of one kind when any of its pages changed; returns the hashes whose state changed
        // (removed = back to the default state).
        List<uint> RefreshKind(int ki)
        {
            bool changed = false;
            var revs = _pageRev[ki]; var entries = new StateEntry[ObjectStateRules.MaxPages];
            for (int p = 0; p < ObjectStateRules.MaxPages; p++)
            {
                var e = _world.Registry.Get(StateScope.Region, _region, PageKeys[ki][p]);
                entries[p] = e; uint rev = e != null ? e.Rev : 0;
                if (rev != revs[p]) { revs[p] = rev; changed = true; }
            }
            if (!changed) return null;
            var fresh = new Dictionary<uint, ObjectState>();
            for (int p = 0; p < ObjectStateRules.MaxPages; p++)
            {
                ObjectStateList l;
                if (entries[p] != null && ObjectStateList.TryParse(entries[p].Value, out l)) foreach (var s in l.Items) fresh[s.Hash] = s;
            }
            var diff = new List<uint>();
            var old = _com[ki];
            foreach (var kv in fresh) { ObjectState o; if (!old.TryGetValue(kv.Key, out o) || o.State != kv.Value.State || o.Ver != kv.Value.Ver) diff.Add(kv.Key); }
            foreach (var kv in old) if (!fresh.ContainsKey(kv.Key)) diff.Add(kv.Key);
            _com[ki] = fresh;
            return diff;
        }

        byte Committed(ObjectKind k, uint hash) { ObjectState s; return _com[KI(k)].TryGetValue(hash, out s) ? s.State : (byte)0; }
        ushort CommittedVer(ObjectKind k, uint hash) { ObjectState s; return _com[KI(k)].TryGetValue(hash, out s) ? s.Ver : (ushort)0; }

        void Reconcile(WorldCensus census, bool full)
        {
            _appliedHead = _world.Registry.HeadRev;
            int applied = 0;
            var changed = new List<uint>[3];
            for (int ki = 0; ki < 3; ki++) changed[ki] = RefreshKind(ki);
            if (full)
            {
                // every shared object of the scene: committed state, absent = default (closed / locked / intact)
                foreach (var kind in Kinds)
                    foreach (var r in census.Objects(_region))
                    {
                        if (r.Kind != kind) continue;
                        if (kind == ObjectKind.LockedWindow) { applied += ApplyWindow(r.Id.Hash, r.Comp, true) ? 1 : 0; continue; }   // component may be gone: the sash still applies
                        if ((object)r.Comp != null && r.Comp != null) applied += Apply(kind, r.Id.Hash, r.Comp, true, census) ? 1 : 0;
                    }
                _dirty.Clear();
            }
            else
            {
                for (int ki = 0; ki < 3; ki++)
                {
                    var kind = Kinds[ki]; var list = changed[ki];
                    if (list != null) foreach (var h in list) applied += ApplyById(kind, h, census) ? 1 : 0;
                    if (_dirty.Count > 0) foreach (var key in new List<long>(_dirty)) if ((ObjectKind)(byte)(key >> 32) == kind) { _dirty.Remove(key); applied += ApplyById(kind, (uint)key, census) ? 1 : 0; }
                }
            }
            if (applied > 0) _log.Info("WOBJ " + (full ? "region ready" : "update") + ": " + applied + " object(s) of " + Regions.Name(_region) + " set to the committed state" + (full ? " (silently)" : ""));
        }

        bool ApplyById(ObjectKind kind, uint hash, WorldCensus census)
        {
            Component c = null;
            if (kind == ObjectKind.LockedWindow)
            {
                Component lw; Component sash;
                bool alive = census.TryResolve(new WorldObjectId(_region, hash), out lw) && lw != null;
                if (alive) c = lw; else if (_sashOf.TryGetValue(hash, out sash) && sash != null) c = sash;
                if (c == null) return false;
                return ApplyWindow(hash, alive ? lw : null, false);
            }
            if (!census.TryResolve(new WorldObjectId(_region, hash), out c) || c == null) return false;
            return Apply(kind, hash, c, false, census);
        }

        bool Apply(ObjectKind kind, uint hash, Component c, bool silent, WorldCensus census)
        {
            byte want = Committed(kind, hash);
            long key = Key(kind, hash);
            bool pending = PendingFor(key);
            switch (kind)
            {
                case ObjectKind.Lockpick:
                {
                    byte local; _lockLocal.TryGetValue(hash, out local);
                    Res res; bool busy = _res.TryGetValue(key, out res) && res.Held && res.Started;
                    var a = WorldLockRules.Decide(want, local, pending, busy);
                    if (a == LockApply.Unlock) { _b.Unlock(c, want, silent); _lockLocal[hash] = want; Count(silent); return true; }
                    if (a == LockApply.Relock) { _b.Relock(c); _lockLocal.Remove(hash); Count(silent); return true; }
                    if (a == LockApply.SkipBusy || a == LockApply.SkipPending) { _skipBusy++; _retryAt = Now + 0.5; _dirty.Add(key); }
                    else if (a == LockApply.Diverged) Diverged(key, "lock " + Hex(hash) + " committed " + want + " but its padlock already fell here");
                    return false;
                }
                case ObjectKind.LockedWindow:
                    return ApplyWindow(hash, c, silent);
                case ObjectKind.Door:
                {
                    WorldObjectId w;
                    if (census.TryWindowOfDoor(c, out w) || !_b.Ordinary(c)) return false;   // sash: via its window; drawers etc.: W4.2C
                    return ApplyDoorBits(key, c, want, silent, pending);
                }
            }
            return false;
        }

        bool ApplyWindow(uint hash, Component lw, bool silent)
        {
            byte want = Committed(ObjectKind.LockedWindow, hash);
            long key = Key(ObjectKind.LockedWindow, hash);
            bool pending = PendingFor(key);
            byte local; _glassLocal.TryGetValue(hash, out local);
            Res res; bool busy = _res.TryGetValue(key, out res) && res.Held && res.Started;
            bool alive = (object)lw != null && lw != null;
            bool did = false;
            var a = WorldWindowRules.Decide(want, local, alive, pending, busy);
            if (a == WindowApply.Glass) { byte g = (byte)(want & ObjectStateRules.GlassMask); _b.Glass(lw, g, silent); _glassLocal[hash] = g; Count(silent); did = true; }
            else if (a == WindowApply.SkipBusy || a == WindowApply.SkipPending) { _skipBusy++; _retryAt = Now + 0.5; _dirty.Add(key); return false; }
            else if (a == WindowApply.Diverged) Diverged(key, "window " + Hex(hash) + " committed glass " + (want & 3) + " but this instance has glass " + local + (alive ? "" : " (component gone)"));
            Component sash;
            if ((want & ObjectStateRules.GlassMask) != 0 && _sashOf.TryGetValue(hash, out sash) && sash != null)
                did |= ApplyDoorBits(key, sash, WorldDoorRules.SashAsDoor(want), silent, pending);
            return did;
        }

        bool ApplyDoorBits(long key, Component door, byte bits, bool silent, bool pending)
        {
            var assigned = AssignedDoor();
            if (!silent && !pending && WorldDoorRules.FastFinish(bits, _b.DoorOpen(door), _b.DoorBroken(door), _b.IsSlowReplica(door)))
            { _b.FinishFast(door); _fastFinishes++; Count(false); return true; }
            var a = WorldDoorRules.Decide(bits, _b.DoorOpen(door), _b.DoorBroken(door), _b.DoorMoving(door), ReferenceEquals(door, assigned), WorldObjectBridge.DoorUsable(door), pending);
            switch (a)
            {
                case DoorApply.Swing:
                    bool open = (bits & ObjectStateRules.DoorOpen) != 0, slow = (bits & ObjectStateRules.DoorSlow) != 0;
                    if (silent) _b.Snap(door, open); else { _b.Swing(door, open, slow); if (slow) _slowApplied++; }
                    SyncWatch(door); Count(silent); return true;
                case DoorApply.Break:
                    _b.Break(door, silent); SyncWatch(door); Count(silent); return true;
                case DoorApply.SkipBusy: case DoorApply.SkipPending:
                    _skipBusy++; _retryAt = Now + 0.25; _dirty.Add(key); return false;
                case DoorApply.SkipLocked:
                    _skipLocked++; if (_logged.Add(key)) _log.Info("WOBJ " + Hex((uint)key) + " committed open but locked here (tag " + door.tag + "): waits for its lock state"); _dirty.Add(key); _retryAt = Now + 1.0; return false;
                case DoorApply.Diverged:
                    Diverged(key, "door " + Hex((uint)key) + " has a broken hinge here but not in the committed state"); return false;
            }
            return false;
        }

        void Count(bool silent) { if (silent) _silent++; else _applied++; }
        void Diverged(long key, string what) { _diverged++; if (_logged.Add(key)) _log.Warn("WOBJ diverged: " + what + " (original cannot revert it in this scene instance)"); }
        static string Hex(uint h) { return h.ToString("X8"); }
        bool PendingFor(long key) { foreach (var p in _pending.Values) if (Key(p.Kind, p.Hash) == key && (p.Op == ObjectOp.Set || p.Op == ObjectOp.NpcSet)) return true; return false; }

        Component AssignedDoor()
        {
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPs(gm) : null;
            return ps != null ? _psAssign(ps) as Component : null;
        }

        void SyncWatch(Component door) { foreach (var w in _watch) if (ReferenceEquals(w.Door, door)) { w.Open = _b.DoorOpen(door); w.Broken = _b.DoorBroken(door); } }

        // ================================================================ local changes
        void Detect(UnityEngine.Object gm, WorldCensus census, double now)
        {
            var assigned = AssignedDoor();
            if ((object)assigned != null && assigned != null && FindWatch(assigned) < 0 && _watch.Count < 4)
                _watch.Add(new Watch { Door = assigned, Open = _b.DoorOpen(assigned), Broken = _b.DoorBroken(assigned), Since = now });
            for (int i = _watch.Count - 1; i >= 0; i--)
            {
                var w = _watch[i];
                if (w.Door == null) { _watch.RemoveAt(i); continue; }
                bool open = _b.DoorOpen(w.Door), broken = _b.DoorBroken(w.Door);
                if (open != w.Open || broken != w.Broken)
                {
                    bool slow = _b.DoorSlow(w.Door);
                    w.Open = open; w.Broken = broken; w.Since = now; w.Slow = slow && !broken;
                    byte bits = (byte)((open ? ObjectStateRules.DoorOpen : 0) | (slow ? ObjectStateRules.DoorSlow : 0) | (broken ? ObjectStateRules.DoorBroken : 0));
                    LocalDoor(w.Door, bits, ObjectOp.Set, census);
                }
                else if (w.Slow && !_b.DoorMoving(w.Door)) w.Slow = false;   // the slow swing ended (Resett)
                else if (w.Slow && !_b.DoorSlow(w.Door))
                {
                    // W4.2B.1: original Door.SlowFailed while still slow (E released / ray left the door): openingSlowly
                    // falls, the swing continues at 150 x speedMultiplier. One extra transition (slow mark dropped) lets
                    // the replica finish its swing at the same moment.
                    w.Slow = false; w.Since = now; _fastFinishSent++;
                    LocalDoor(w.Door, (byte)(open ? ObjectStateRules.DoorOpen : 0), ObjectOp.Set, census);
                }
                else if (!ReferenceEquals(w.Door, assigned) && !_b.DoorMoving(w.Door) && now - w.Since > 0.3) _watch.RemoveAt(i);
            }
        }

        int FindWatch(Component door) { for (int i = 0; i < _watch.Count; i++) if (ReferenceEquals(_watch[i].Door, door)) return i; return -1; }

        void LocalDoor(Component door, byte bits, ObjectOp op, WorldCensus census)
        {
            WorldObjectId id;
            if (census.TryWindowOfDoor(door, out id) && id.Namespace == _region)
            {
                byte glass; _glassLocal.TryGetValue(id.Hash, out glass);
                if (glass == 0) return;   // a locked window's sash cannot move in the original
                LocalChange(ObjectKind.LockedWindow, id.Hash, WorldDoorRules.WithSash(glass, bits), op);
                return;
            }
            if (!_b.Ordinary(door))
            {
                // W4.2B.1: was a silent return - the user's crowbar / hold-E doors left no trace in the log.
                _localOnly++;
                if (_logged.Add(0x7D00000000L | (uint)door.GetInstanceID())) _log.Info("WOBJ door '" + door.name + "' (" + _b.LocalOnlyType(door) + ") stays local: furniture / car doors are not shared yet (W4.2C)");
                return;
            }
            if (!census.TryDoorId(door, out id) || id.Namespace != _region) { Unidentified(door); return; }
            LocalChange(ObjectKind.Door, id.Hash, bits, op);
        }

        void Unidentified(Component c)
        {
            _unidentified++;
            if (_logged.Add(0x7F00000000L | (uint)c.GetInstanceID())) _log.Info("WOBJ '" + c.name + "' has no unambiguous census id: stays local");
        }

        ushort BaseVersion(long key, ObjectKind k, uint hash) { ushort v; return _expect.TryGetValue(key, out v) ? v : CommittedVer(k, hash); }

        void LocalChange(ObjectKind kind, uint hash, byte state, ObjectOp op)
        {
            _local++;
            if (_phase != Phase.Ready) { if (_queue.Count < 64) _queue.Add(new Queued { Kind = kind, Hash = hash, State = state, Op = op }); return; }
            Propose(kind, hash, state, op);
        }

        void FlushQueue(double now)
        {
            var q = new List<Queued>(_queue); _queue.Clear();
            foreach (var x in q) Propose(x.Kind, x.Hash, x.State, x.Op);
        }

        uint Propose(ObjectKind kind, uint hash, byte state, ObjectOp op)
        {
            long key = Key(kind, hash);
            var req = new ObjectTxnRequest { Txn = ++_txn, Op = op, Kind = (byte)kind, Ns = _region, Hash = hash, State = state, BaseVer = BaseVersion(key, kind, hash), Epoch = _rec.Epoch };
            if (_host)
            {
                string why; double now = Now;
                var code = _world.Host.ObjectTransaction(_world.LocalSlot, req, now, out why);
                _world.FlushOutbox();
                if (code == ProposalCode.Accepted) _committed++; else _refused++;
                _log.Info("WOBJ host " + req + " -> " + code + (why != null ? " (" + why + ")" : ""));
                if (op == ObjectOp.Set || op == ObjectOp.NpcSet) { if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) { _dirty.Add(key); _retryAt = now + 0.1; } }
                HostResult(key, op, code);
                return 0;
            }
            if (!Online) { _log.Info("WOBJ " + req + " not proposed (offline): stays local until the next reconcile"); return 0; }
            uint pid = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = req.ToBytes() });
            _pending[pid] = new Pending { Kind = kind, Hash = hash, Op = op };
            if (op == ObjectOp.Set || op == ObjectOp.NpcSet) _expect[key] = (ushort)(req.BaseVer == ushort.MaxValue ? 1 : req.BaseVer + 1);
            _proposed++;
            _log.Info("WOBJ proposed #" + pid + " " + req);
            return pid;
        }

        bool OnResult(uint pid, ProposalCode code)
        {
            if (pid == _reportPid) { _log.Info("WOBJ scene report #" + pid + " -> " + code); if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) _reportAt = Now; return true; }
            if (_initPids.Remove(pid))
            {
                _log.Info("WOBJ init chunk #" + pid + " -> " + code);
                if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) { _initSent = false; _initPids.Clear(); if (_phase == Phase.Initializing) _phase = Phase.Reported; }
                return true;
            }
            Pending p;
            if (!_pending.TryGetValue(pid, out p)) return false;
            _pending.Remove(pid);
            long key = Key(p.Kind, p.Hash);
            bool stillPending = false; foreach (var v in _pending.Values) if (Key(v.Kind, v.Hash) == key) { stillPending = true; break; }
            if (p.Op == ObjectOp.Reserve) { ReserveResult(key, pid, code); return true; }
            if (p.Op == ObjectOp.Release) return true;
            if (code == ProposalCode.Accepted) { _committed++; if (!stillPending) _expect.Remove(key); }
            else
            {
                _refused++; _expect.Remove(key);
                _log.Info("WOBJ proposal #" + pid + " " + p.Kind + " " + Hex(p.Hash) + " -> " + code + (code == ProposalCode.Unchanged ? "" : ": the committed state is restored"));
                _dirty.Add(key); _retryAt = Now + 0.1;
            }
            Res r; if (_res.TryGetValue(key, out r) && r.Committed) _res.Remove(key);
            return true;
        }

        void HostResult(long key, ObjectOp op, ProposalCode code)
        {
            Res r;
            if ((op == ObjectOp.Set) && _res.TryGetValue(key, out r) && r.Committed) _res.Remove(key);
        }

        // ================================================================ reservations (lockpicking / glass cutting / crowbar)
        // Returns true when the original may run now.
        bool Gate(ObjectKind kind, Component c, Action resume, string what)
        {
            if (_bypass) return true;
            if (!Sharing) return true;
            var census = WorldCensus.Instance; WorldObjectId id;
            if (census == null || !census.TryObjectId(c, kind, out id) || id.Namespace != _region) { Unidentified(c); return true; }   // not shared: vanilla, local only
            if (_phase != Phase.Ready) { _gateRefused++; _log.Info("WOBJ " + what + " " + Hex(id.Hash) + " refused: region still initializing"); return false; }
            long key = Key(kind, id.Hash);
            if (Committed(kind, id.Hash) != 0) { _gateRefused++; _dirty.Add(key); _log.Info("WOBJ " + what + " " + Hex(id.Hash) + " refused: already " + Committed(kind, id.Hash) + " in the shared state"); return false; }
            Res r;
            if (_res.TryGetValue(key, out r)) { if (r.Held && !r.Committed) { r.At = Now; r.Started = false; return true; } return false; }
            r = new Res { Kind = kind, Hash = id.Hash, Comp = c, Resume = resume, At = Now };
            _res[key] = r;
            if (_host)
            {
                string why; var code = _world.Host.ObjectTransaction(_world.LocalSlot, new ObjectTxnRequest { Txn = ++_txn, Op = ObjectOp.Reserve, Kind = (byte)kind, Ns = _region, Hash = id.Hash, BaseVer = CommittedVer(kind, id.Hash), Epoch = _rec.Epoch }, Now, out why);
                _log.Info("WOBJ host reserve " + what + " " + Hex(id.Hash) + " -> " + code + (why != null ? " (" + why + ")" : ""));
                if (code == ProposalCode.Accepted) { r.Held = true; return true; }
                _res.Remove(key); _gateRefused++; return false;
            }
            r.Pid = Propose(kind, id.Hash, 0, ObjectOp.Reserve);
            if (r.Pid == 0) { _res.Remove(key); return false; }
            return false;   // the original runs when the host grants the reservation (ReserveResult)
        }

        void ReserveResult(long key, uint pid, ProposalCode code)
        {
            Res r;
            if (!_res.TryGetValue(key, out r) || r.Pid != pid) return;
            if (code != ProposalCode.Accepted) { _res.Remove(key); _gateRefused++; _log.Info("WOBJ reservation " + r.Kind + " " + Hex(r.Hash) + " -> " + code + ": the interaction does nothing"); return; }
            r.Held = true; r.At = Now;
            // the player may have moved on during the round trip: only resume a still-valid original interaction
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPs(gm) as Component : null;
            bool near = ps != null && r.Comp != null && (ps.transform.position - r.Comp.transform.position).sqrMagnitude < 4.0f * 4.0f;
            bool busy = ps != null && _psLockpicking(ps);
            if (!near || busy || _phase != Phase.Ready) { _log.Info("WOBJ reservation " + Hex(r.Hash) + " granted but the interaction is no longer valid (" + (!near ? "moved away" : busy ? "already in a minigame" : "region changed") + "): released"); Release(key, r); return; }
            _log.Info("WOBJ reservation " + r.Kind + " " + Hex(r.Hash) + " granted: original interaction starts");
            _bypass = true;
            try { r.Resume(); }
            catch (Exception ex) { _log.Error("WOBJ resuming the original interaction failed", ex); Release(key, r); }
            finally { _bypass = false; }
        }

        void Release(long key, Res r)
        {
            _res.Remove(key);
            if (!r.Held || r.Committed) return;
            if (_host) { string why; _world.Host.ObjectTransaction(_world.LocalSlot, new ObjectTxnRequest { Txn = ++_txn, Op = ObjectOp.Release, Kind = (byte)r.Kind, Ns = _region, Hash = r.Hash, Epoch = _rec.Epoch }, Now, out why); }
            else if (Online) Propose(r.Kind, r.Hash, 0, ObjectOp.Release);
        }

        void ReservationFrame(UnityEngine.Object gm, double now)
        {
            var ps = _gmPs(gm);
            bool picking = ps != null && _psLockpicking(ps);
            foreach (var kv in new List<KeyValuePair<long, Res>>(_res))
            {
                var r = kv.Value;
                if (!r.Held) { if (now - r.At > 5.0) { _res.Remove(kv.Key); _log.Info("WOBJ reservation " + Hex(r.Hash) + " unanswered: dropped"); } continue; }
                if (r.Committed) { if (now - r.At > 5.0) _res.Remove(kv.Key); continue; }
                if (picking) { r.Started = true; continue; }
                if (r.Started) { _log.Info("WOBJ " + r.Kind + " " + Hex(r.Hash) + ": interaction ended without a result (failed / cancelled): reservation released"); Release(kv.Key, r); }
                else if (now - r.At > 2.0) { _log.Info("WOBJ " + r.Kind + " " + Hex(r.Hash) + ": original interaction did not start: reservation released"); Release(kv.Key, r); }
            }
        }

        // ================================================================ hook entry points (instance side)
        void OnLock(Component lp, byte state)
        {
            var census = WorldCensus.Instance; WorldObjectId id;
            if (!Sharing || census == null) return;
            if (!census.TryObjectId(lp, ObjectKind.Lockpick, out id) || id.Namespace != _region) { Unidentified(lp); return; }
            byte cur; _lockLocal.TryGetValue(id.Hash, out cur);
            if (ObjectStateRules.SamePhysical(ObjectKind.Lockpick, cur, state) && state != ObjectStateRules.LockLocked) return;   // repeated callback (auto lockpick per door)
            if (state == ObjectStateRules.LockLocked && cur == ObjectStateRules.LockLocked) return;
            if (state == 0) _lockLocal.Remove(id.Hash); else _lockLocal[id.Hash] = state;
            Res r; if (_res.TryGetValue(Key(ObjectKind.Lockpick, id.Hash), out r)) { r.Committed = true; r.At = Now; }
            LocalChange(ObjectKind.Lockpick, id.Hash, state, ObjectOp.Set);
        }

        void OnGlass(WorldObjectId id, byte glass)
        {
            if (!Sharing || id.Namespace != _region) return;
            byte cur; _glassLocal.TryGetValue(id.Hash, out cur);
            if (cur != 0) return;
            _glassLocal[id.Hash] = glass;
            Res r; if (_res.TryGetValue(Key(ObjectKind.LockedWindow, id.Hash), out r)) { r.Committed = true; r.At = Now; }
            byte state = glass; Component sash;
            if (_sashOf.TryGetValue(id.Hash, out sash) && sash != null) state = WorldDoorRules.WithSash(glass, _b.DoorBits(sash));
            LocalChange(ObjectKind.LockedWindow, id.Hash, state, ObjectOp.Set);
        }

        void OnDoorBroken(Component door)
        {
            if (!Sharing) return;
            SyncWatch(door);
            LocalDoor(door, (byte)(ObjectStateRules.DoorOpen | ObjectStateRules.DoorBroken), ObjectOp.Set, WorldCensus.Instance);
        }

        // true = let the original ResetLock run
        bool AllowReset(Component door)
        {
            if (!Sharing || _npcDepth == 0) return true;
            if (_phase != Phase.Ready || !_recOk) return true;
            if (_rec.Owner == _world.LocalSlot) return true;
            var census = WorldCensus.Instance; WorldObjectId id;
            if (census == null || !(census.TryDoorId(door, out id) || census.TryWindowOfDoor(door, out id))) return true;
            _npcSuppressed++;
            if (_logged.Add(Key(ObjectKind.Door, id.Hash) | 0x100000000000L)) _log.Info("WOBJ NPC relock of " + Hex(id.Hash) + " suppressed here: P" + _rec.Owner + " simulates this region's NPC transitions");
            return false;
        }

        void OnReset(Component door, bool wasOpen)
        {
            if (!Sharing) return;
            var census = WorldCensus.Instance;
            var op = _npcDepth > 0 ? ObjectOp.NpcSet : ObjectOp.Set;
            var lp = door.GetComponent(_b.LockpickT);
            if (lp != null) OnLockReset(lp, op);
            if (wasOpen) { SyncWatch(door); LocalDoor(door, 0, op, census); }
        }

        void OnLockReset(Component lp, ObjectOp op)
        {
            var census = WorldCensus.Instance; WorldObjectId id;
            if (!census.TryObjectId(lp, ObjectKind.Lockpick, out id) || id.Namespace != _region) return;
            byte cur; _lockLocal.TryGetValue(id.Hash, out cur);
            if (cur == 0) return;
            _lockLocal.Remove(id.Hash);
            LocalChange(ObjectKind.Lockpick, id.Hash, 0, op);
        }

        // Player 2 never starts story/mission progression through these objects (fail closed).
        bool StoryBlocked(ObjectKind kind, Component c, string what)
        {
            if (_host || !_ok) return false;
            if (!_b.StoryCritical(kind, c)) return false;
            _storyBlocked++;
            if (_logged.Add(0x7E00000000L | (uint)c.GetInstanceID())) _log.Info("WOBJ " + what + " '" + c.name + "' is a story/mission object: Player 2 cannot use it until shared missions exist");
            return true;
        }

        // ================================================================ Harmony hooks (static)
        static void DoorBrokenPost(object __instance) { var i = Instance; if (i == null || WorldObjectBridge.Applying) return; try { i.OnDoorBroken((Component)__instance); } catch (Exception ex) { i._log.Error("WOBJ hook OpenDestroyed", ex); } }
        // __state: 0 = ran (door was closed), 1 = ran (door was open), 2 = original skipped (Harmony still runs the postfix)
        static bool ResetLockPre(object __instance, ref int __state)
        {
            __state = 0; var i = Instance;
            if (i == null || WorldObjectBridge.Applying) return true;
            try { __state = i._b.DoorOpen((Component)__instance) ? 1 : 0; if (i.AllowReset((Component)__instance)) return true; __state = 2; return false; }
            catch (Exception ex) { i._log.Error("WOBJ hook ResetLock", ex); return true; }
        }
        static void ResetLockPost(object __instance, int __state) { var i = Instance; if (i == null || __state == 2 || WorldObjectBridge.Applying) return; try { i.OnReset((Component)__instance, __state == 1); } catch (Exception ex) { i._log.Error("WOBJ hook ResetLock", ex); } }
        static void NpcPre() { _npcDepth++; }
        static void NpcFinal() { if (_npcDepth > 0) _npcDepth--; }

        static bool StartPickPre(object __instance)
        {
            var i = Instance; if (i == null) return true;
            var lp = (Component)__instance;
            if (i.StoryBlocked(ObjectKind.Lockpick, lp, "lock")) return false;
            return i.Gate(ObjectKind.Lockpick, lp, () => _startLockpicking.Invoke(lp, null), "lockpicking");
        }
        static bool BreakinPre(object __instance, GameObject __0, RaycastHit __1)
        {
            var i = Instance; if (i == null || __0 == null) return true;
            var ps = __instance; var go = __0; var hit = __1;
            var lp = go.GetComponent(i._b.LockpickT); var lw = go.GetComponent(i._b.WindowT);
            Action resume = () => _breakin.Invoke(ps, new object[] { go, hit });
            if (lp != null) { if (i.StoryBlocked(ObjectKind.Lockpick, lp, "crowbar on lock")) return false; return i.Gate(ObjectKind.Lockpick, lp, resume, "crowbar break-in"); }
            if (lw != null) { if (i.StoryBlocked(ObjectKind.LockedWindow, lw, "crowbar on window")) return false; return i.Gate(ObjectKind.LockedWindow, lw, resume, "crowbar break-in"); }
            return true;
        }
        // __state = the original ran (a skipped original still runs the postfix)
        static bool UnlockPre(object __instance, ref bool __state) { var i = Instance; __state = i == null || !i.StoryBlocked(ObjectKind.Lockpick, (Component)__instance, "unlock"); return __state; }
        static void UnlockPost(object __instance, bool __state) { var i = Instance; if (i == null || !__state || WorldObjectBridge.Applying) return; try { i.OnLock((Component)__instance, ObjectStateRules.LockUnlocked); } catch (Exception ex) { i._log.Error("WOBJ hook DoorUnlock", ex); } }
        static bool KeyPre(object __instance, ref bool __state) { var i = Instance; __state = i == null || !i.StoryBlocked(ObjectKind.Lockpick, (Component)__instance, "key unlock"); return __state; }
        static void KeyPost(object __instance, bool __state) { var i = Instance; if (i == null || !__state || WorldObjectBridge.Applying) return; try { i.OnLock((Component)__instance, ObjectStateRules.LockKey); } catch (Exception ex) { i._log.Error("WOBJ hook DoorUnlockKey", ex); } }
        static bool PadlockPre(object __instance, ref bool __state) { var i = Instance; __state = i == null || !i.StoryBlocked(ObjectKind.Lockpick, (Component)__instance, "padlock"); return __state; }
        static void PadlockPost(object __instance, bool __state) { var i = Instance; if (i == null || !__state || WorldObjectBridge.Applying) return; try { i.OnLock((Component)__instance, ObjectStateRules.LockPadlock); } catch (Exception ex) { i._log.Error("WOBJ hook BreakPadlock", ex); } }
        static bool BeginCutPre(object __instance)
        {
            var i = Instance; if (i == null) return true;
            var lw = (Component)__instance;
            if (i.StoryBlocked(ObjectKind.LockedWindow, lw, "window")) return false;
            return i.Gate(ObjectKind.LockedWindow, lw, () => _beginCut.Invoke(lw, null), "glass cutting");
        }
        static bool InsidePre(object __instance) { var i = Instance; if (i != null && i.StoryBlocked(ObjectKind.LockedWindow, (Component)__instance, "window")) return false; _insideDepth++; return true; }
        static void InsideFinal() { if (_insideDepth > 0) _insideDepth--; }
        // id read before the original destroys the LockedWindow component (Destroy is deferred to the end of the frame anyway)
        static bool GlassPre(object __instance, ref long __state)
        {
            __state = 0; var i = Instance; if (i == null) return true;
            var lw = (Component)__instance;
            if (i.StoryBlocked(ObjectKind.LockedWindow, lw, "window")) return false;
            WorldObjectId id; var c = WorldCensus.Instance;
            if (c != null && c.TryObjectId(lw, ObjectKind.LockedWindow, out id)) __state = ((long)id.Namespace << 32) | id.Hash;
            else if (i.Sharing) i.Unidentified(lw);
            return true;
        }
        static void UnlockWindowPost(long __state) { var i = Instance; if (i == null || __state == 0 || WorldObjectBridge.Applying) return; try { i.OnGlass(new WorldObjectId((int)(__state >> 32), (uint)__state), _insideDepth > 0 ? ObjectStateRules.GlassInside : ObjectStateRules.GlassCut); } catch (Exception ex) { i._log.Error("WOBJ hook UnlockWindow", ex); } }
        static Func<object, bool> _brokeGlassGet, _lwTutGet;
        static bool BrokePre(object __instance, ref long __state)
        {
            __state = 0; var i = Instance; if (i == null) return true;
            var lw = (Component)__instance;
            if (i.StoryBlocked(ObjectKind.LockedWindow, lw, "window")) return false;
            if (_brokeGlassGet == null) { _brokeGlassGet = Fast.Getter<bool>(i._b.WindowT, "brokeGlass"); _lwTutGet = Fast.Getter<bool>(i._b.WindowT, "isTutorial"); }
            if (_brokeGlassGet(lw) || _lwTutGet(lw)) return true;   // the original returns early
            WorldObjectId id; var c = WorldCensus.Instance;
            if (c != null && c.TryObjectId(lw, ObjectKind.LockedWindow, out id)) __state = ((long)id.Namespace << 32) | id.Hash;
            else if (i.Sharing) i.Unidentified(lw);
            return true;
        }
        static void BrokePost(long __state) { var i = Instance; if (i == null || __state == 0 || WorldObjectBridge.Applying) return; try { i.OnGlass(new WorldObjectId((int)(__state >> 32), (uint)__state), ObjectStateRules.GlassBroken); } catch (Exception ex) { i._log.Error("WOBJ hook BrokeWindow", ex); } }

        // ================================================================ developer test hook (TS2MP_DEV_WOBJ)
        int _dev; double _devAt; Component _devDoor;
        void DevStep(WorldCensus census, double now)
        {
            if (now < _devAt) return;
            _devAt = now + 1.5;
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPs(gm) as Component : null;
            if (ps == null) return;
            Vector3 me = ps.transform.position;
            try
            {
                switch (_dev)
                {
                    case 1:   // nearest ordinary closed, unlocked door: assign it like PlayerScript.Update does (watch picks it up)
                        _devDoor = Nearest(census, ObjectKind.Door, me, c => _b.Ordinary(c) && !_b.DoorOpen(c) && WorldObjectBridge.DoorUsable(c) && !census.TryWindowOfDoor(c, out _devId));
                        if (_devDoor == null) { _log.Info("DEV_WOBJ no door found"); _dev = 3; break; }
                        AccessTools.Field(ps.GetType(), "assignDoor").SetValue(ps, _devDoor); _log.Info("DEV_WOBJ door '" + _devDoor.name + "' assigned"); _dev = 2; _devAt = now + 0.2; break;
                    case 2:   // the original quick use (Door.Use)
                        AccessTools.Method(_b.DoorT, "Use").Invoke(_devDoor, null); _log.Info("DEV_WOBJ Door.Use -> isOpen=" + _b.DoorOpen(_devDoor)); _dev = 3; break;
                    case 3:   // a non-story lock: the original success path (Lockpick.DoorUnlock, as the companion path calls it)
                        AccessTools.Field(ps.GetType(), "assignDoor").SetValue(ps, null);
                        var lp = Nearest(census, ObjectKind.Lockpick, me, c => !_b.StoryCritical(ObjectKind.Lockpick, c) && Committed(ObjectKind.Lockpick, IdOf(census, c, ObjectKind.Lockpick)) == 0 && !LocalUnlocked(census, c));
                        if (lp != null) { _log.Info("DEV_WOBJ Lockpick.DoorUnlock on '" + lp.name + "'"); AccessTools.Method(_b.LockpickT, "DoorUnlock").Invoke(lp, null); } else _log.Info("DEV_WOBJ no lock found");
                        _dev = 4; break;
                    case 4:   // a non-mission window: the original unlock from inside (UnlockWindowNoAlarm: no alarm, no mission)
                        var lw = Nearest(census, ObjectKind.LockedWindow, me, c => !_b.StoryCritical(ObjectKind.LockedWindow, c));
                        if (lw != null) { _log.Info("DEV_WOBJ LockedWindow.UnlockWindowNoAlarm on '" + lw.name + "'"); AccessTools.Method(_b.WindowT, "UnlockWindowNoAlarm").Invoke(lw, null); } else _log.Info("DEV_WOBJ no window found");
                        _dev = 5; break;
                    case 5:   // reservation round trip through the real gate (the resume does not open a minigame UI)
                        var lp2 = Nearest(census, ObjectKind.Lockpick, me, c => !_b.StoryCritical(ObjectKind.Lockpick, c) && Committed(ObjectKind.Lockpick, IdOf(census, c, ObjectKind.Lockpick)) == 0 && !LocalUnlocked(census, c));
                        if (lp2 != null) { bool now0 = Gate(ObjectKind.Lockpick, lp2, () => _log.Info("DEV_WOBJ reservation granted: resume called (no minigame started)"), "dev reservation"); _log.Info("DEV_WOBJ gate on '" + lp2.name + "' -> " + (now0 ? "run now" : "waiting for the host")); }
                        _dev = 0; _log.Info("DEV_WOBJ done; " + Stats); break;
                }
            }
            catch (Exception ex) { _log.Error("DEV_WOBJ step " + _dev + " failed", ex); _dev = 0; }
        }
        WorldObjectId _devId;
        uint IdOf(WorldCensus census, Component c, ObjectKind k) { WorldObjectId id; return census.TryObjectId(c, k, out id) ? id.Hash : 0; }
        bool LocalUnlocked(WorldCensus census, Component c) { byte s; return _lockLocal.TryGetValue(IdOf(census, c, ObjectKind.Lockpick), out s) && s != 0; }
        Component Nearest(WorldCensus census, ObjectKind k, Vector3 me, Func<Component, bool> ok)
        {
            Component best = null; float bd = float.MaxValue;
            foreach (var r in census.Objects(_region))
            {
                if (r.Kind != k || (object)r.Comp == null || r.Comp == null || !r.Comp.gameObject.activeInHierarchy || !((Behaviour)r.Comp).isActiveAndEnabled || !ok(r.Comp)) continue;   // only objects a player can reach (Start ran)
                float d = (r.Comp.transform.position - me).sqrMagnitude;
                if (d < bd) { bd = d; best = r.Comp; }
            }
            return best;
        }

        // ================================================================ host census of a region
        void OnCensused(int ns, bool late)
        {
            if (!_host || late || !ObjectStateRules.SupportedNamespace(ns)) return;
            uint d; int n; WorldCensus.Instance.TryDigest(ns, out d, out n);
            int removed = _world.Host.Objects.PruneUnknown(ns, d, _world.Host.Reg, _world.Host.Outbox);
            _world.FlushOutbox();
            if (removed > 0) _log.Warn("WOBJ host census of " + IdentityNamespace.Name(ns) + ": " + removed + " unverified state(s) of unknown ids removed");
        }

        public string Stats
        {
            get
            {
                return "WOBJ_STATS phase=" + _phase + " local=" + _local + " proposed=" + _proposed + " committed=" + _committed + " refused=" + _refused + " applied=" + _applied + " restoredSilently=" + _silent
                    + " skippedLocked=" + _skipLocked + " skippedBusy=" + _skipBusy + " unidentified=" + _unidentified + " diverged=" + _diverged + " npcRelockSuppressed=" + _npcSuppressed
                    + " storyBlocked=" + _storyBlocked + " gateRefused=" + _gateRefused + " initEntries=" + _initEntries + " localOnlyDoors=" + _localOnly + " slowReplicas=" + _slowApplied + " fastFinish sent/applied=" + _fastFinishSent + "/" + _fastFinishes + (_host && _world.Host != null ? " | book " + _world.Host.Objects.Stats : "");
            }
        }

        public void Dispose()
        {
            if (Instance == this) Instance = null;   // hooks become no-ops: vanilla behaviour after the session
            if (!_ok) return;
            var census = WorldCensus.Instance;
            if (_host && _world.Host != null)
            {
                if (census != null) census.ObjectsCensused -= OnCensused;
                _world.Host.ObjectCatalog = null; _world.Host.Objects.Restricted = null;
            }
            if (!_host && _world.ProposalResultHook7 == (Func<uint, ProposalCode, bool>)OnResult) _world.ProposalResultHook7 = null;
        }
    }
}
