// M3.1: shared world state glue with per-region clocks (Unity main thread only; SessionNode raises events from
// Tick() in NetRunner.Update). Host: WorldHost (registry, region clocks, proposals). Client: registry mirror.
// Region time rules: CODEBASE_MAP §7. Hot path is allocation-free (compiled accessors, cached objects, no per-frame
// logging/strings); global searches only on scene changes, at most once per second while a reference is missing.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    public sealed class WorldSettings
    {
        public double ClockRefreshSeconds = 10;
    }

    internal sealed class WorldSync : IDisposable
    {
        readonly SessionNode _node;
        readonly InstanceLog _log;
        readonly bool _host;
        readonly byte _localSlot, _remoteSlot;
        readonly WorldHost _wh;              // host only
        readonly StateRegistry _reg;         // host: _wh.Reg, client: mirror
        readonly HostTimeSampler _sampler = new HostTimeSampler();
        readonly NetDataWriter _w = new NetDataWriter();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly HashSet<string> _scenes = new HashSet<string>();

        // compiled game accessors
        Type _gmType, _dntType;
        Func<object, int> _currentLoc, _day, _week, _prevHour, _progress, _hour;
        Func<object, float> _cycle; Action<object, float> _setCycle;   // EnviroTime.cycleLengthInMinutes (time-lapse)
        Func<object, UnityEngine.Object> _enviro, _gmPS;
        Func<object, object> _gameTime;
        Func<object, float> _internalHour;
        Func<object, bool> _passingOn;
        Action<object, int> _setDay, _setWeek, _setHour, _setPrevHour;
        Action<object, float> _setTime;
        Action<object> _refreshDay;
        bool _apiOk, _hooksOk, _lapseOk;

        // caches (invalidated on scene changes)
        UnityEngine.Object _gm; double _nextGmSearch;
        UnityEngine.Object[] _dayNight;
        int _scenesVersion, _probedVersion = -1, _probedLoc = int.MinValue; bool _probedPresent;
        int _region = Regions.None, _proposedRegion = int.MaxValue;
        byte _localMode = 1; double _nextModeRead, _nextCalendar;
        SleepPresenter _presenter;                 // M4.3.3 original PassTime presentation of the other player's sleep
        int _showA = Regions.None, _showB = Regions.None; uint _shownEvent; uint _showRev;

        // clock state
        bool _adoptPending;                  // entered a region: adopt its established clock before publishing
        uint _clockRev; double _clockRecvAt; RegionClock _clock0; bool _hasClock; int _clockRegion = Regions.None;
        uint _ownerRev = uint.MaxValue; byte _owner;
        readonly SleepTransaction _sleep = new SleepTransaction();
        uint _nextProposalId = 1, _lastReportId;
        bool _stepping; int _stepRegion, _stepWait, _stepTicks;   // host catch-up after a client's sleep (host not watching)
        // M3.1.2 host time-lapse while a client sleeps in the host's region: the game's own Enviro fast-forward
        // (GameTime.cycleLengthInMinutes = 0.2, as PassTime.Update) without the sleep UI / player state of PassStart.
        bool _lapse; object _lapseTime; float _lapsePrevCycle, _lapseUntil = float.NaN; int _lapseTarget; double _lapseStartedAt; long _lapses;

        // diagnostics
        long _msgOut, _msgIn, _bytesOut, _bytesIn, _reports, _adopts, _blends, _snaps, _suppressed, _stepFallbacks, _deltas, _dupes, _gaps, _resyncs, _proposals, _rejected;
        float _maxErr; long _costTicks, _costMax, _frames;
        double _nextStats, _nextStatus;
        string _status = "-", _timeStatus = "-";

        public WorldSync(SessionNode node, InstanceLog log, WorldSettings cfg)
        {
            _node = node; _log = log;
            _host = node.Role == Role.Host;
            _localSlot = _host ? (byte)1 : (byte)2;
            _remoteSlot = _host ? (byte)2 : (byte)1;
            if (_host) { _wh = new WorldHost(NewEpoch()); _reg = _wh.Reg; } else _reg = StateRegistry.CreateClient();
            _sampler.RefreshSeconds = Math.Max(5, Math.Min(60, cfg.ClockRefreshSeconds));
            node.MessageReceived += OnMessage;
            node.RemoteConnected += OnRemoteConnected;
            node.RemoteDisconnected += OnRemoteDisconnected;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            RefreshScenes();
            ResolveApi();
            string hookErr = null;
            try { hookErr = SleepHooks.Install("com.ts2mp.multiplayer.sleep"); } catch (Exception ex) { hookErr = ex.Message; }
            _hooksOk = hookErr == null;
            _presenter = new SleepPresenter(log, _host);
            if (_hooksOk) { SleepHooks.Started += OnSleepStarted; SleepHooks.Ended += OnSleepEnded; }
            else _log.Error("WORLD sleep hooks not installed (" + hookErr + "): client sleep requests disabled");
            log.Info("WORLD_INIT role=" + node.Role + " slot=" + _localSlot + (_host ? " epoch=" + _reg.Epoch.ToString("X8") : "")
                + " api=" + (_apiOk ? "ok" : "MISSING") + " sleepHooks=" + (_hooksOk ? "ok" : "off") + " timelapse=" + (_lapseOk ? "ok" : "off") + " clockRefresh=" + _sampler.RefreshSeconds + "s");
        }

        static uint NewEpoch()
        {
            uint e = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);
            return e == 0 ? 1u : e;
        }

        double Now { get { return _clock.Elapsed.TotalSeconds; } }

        void ResolveApi()
        {
            try
            {
                _gmType = AccessTools.TypeByName("GameManager");
                _dntType = AccessTools.TypeByName("DayNightTime");
                var core = AccessTools.TypeByName("EnviroCore");
                var etime = AccessTools.TypeByName("EnviroTime");
                var ps = AccessTools.TypeByName("PlayerScript");
                _currentLoc = Fast.Getter<int>(_gmType, "currentLoc");
                _enviro = Fast.Getter<UnityEngine.Object>(_gmType, "enviro");
                _gmPS = Fast.Getter<UnityEngine.Object>(_gmType, "PS");
                _day = Fast.Getter<int>(_gmType, "currentDayInt"); _setDay = Fast.Setter<int>(_gmType, "currentDayInt");
                _week = Fast.Getter<int>(_gmType, "currentWeek"); _setWeek = Fast.Setter<int>(_gmType, "currentWeek");
                _setHour = Fast.Setter<int>(_gmType, "currentHour"); _hour = Fast.Getter<int>(_gmType, "currentHour");
                _cycle = Fast.Getter<float>(etime, "cycleLengthInMinutes"); _setCycle = Fast.Setter<float>(etime, "cycleLengthInMinutes");
                _internalHour = Fast.Getter<float>(core, "internalHour");
                _gameTime = Fast.Getter<object>(core, "GameTime");
                _setTime = Fast.Call<float>(core, "SetInternalTimeOfDay");
                _progress = Fast.Getter<int>(etime, "ProgressTime");
                _prevHour = Fast.Getter<int>(_dntType, "prevHour"); _setPrevHour = Fast.Setter<int>(_dntType, "prevHour");
                _refreshDay = Fast.Call0(_dntType, "RefreshDay");
                _passingOn = Fast.Getter<bool>(ps, "passingTimeOn");
            }
            catch (Exception ex) { _log.Error("WORLD accessor compilation failed", ex); }
            _apiOk = _currentLoc != null && _enviro != null && _gmPS != null && _day != null && _setDay != null && _week != null && _setWeek != null && _setHour != null
                && _internalHour != null && _gameTime != null && _setTime != null && _progress != null && _prevHour != null && _setPrevHour != null && _refreshDay != null && _passingOn != null;
            if (!_apiOk) _log.Error("WORLD time/region API incomplete (time sync disabled; region stays 'none')");
            _lapseOk = _apiOk && _hour != null && _cycle != null && _setCycle != null;
        }

        // ---------------- scenes / region ----------------
        void OnSceneLoaded(Scene s, LoadSceneMode m) { if (m == LoadSceneMode.Single) _gm = null; RefreshScenes(); }
        void OnSceneUnloaded(Scene s) { RefreshScenes(); }
        void RefreshScenes()
        {
            _scenes.Clear();
            for (int i = 0; i < SceneManager.sceneCount; i++) { var s = SceneManager.GetSceneAt(i); if (s.isLoaded) _scenes.Add(s.name); }
            _dayNight = null; _scenesVersion++;
        }

        UnityEngine.Object GameManagerObj()
        {
            if (_gm != null) return _gm;
            if (_gmType == null || Now < _nextGmSearch) return null;
            _nextGmSearch = Now + 1.0;
            _gm = UnityEngine.Object.FindObjectOfType(_gmType);
            return _gm;
        }

        UnityEngine.Object DayNight()
        {
            if (_dayNight == null || (_dayNight.Length > 0 && _dayNight[0] == null)) _dayNight = UnityEngine.Object.FindObjectsOfType(_dntType);
            return _dayNight.Length > 0 ? _dayNight[0] : null;
        }

        int ProbeRegion(UnityEngine.Object gm, out Component player)
        {
            player = null;
            if (gm == null) return Regions.None;
            player = _gmPS(gm) as Component;
            bool present = player != null && player.gameObject.activeInHierarchy;
            int loc = _currentLoc(gm);
            if (loc == _probedLoc && present == _probedPresent && _scenesVersion == _probedVersion) return _region;
            _probedLoc = loc; _probedPresent = present; _probedVersion = _scenesVersion;
            return Regions.Resolve(loc, present, _scenes);
        }

        // ---------------- per frame ----------------
        public void Update(float dt)
        {
            long t0 = Stopwatch.GetTimestamp();
            double now = Now;
            var gm = _apiOk ? GameManagerObj() : null;
            Component player;
            int region = ProbeRegion(gm, out player);
            UnityEngine.Object enviro = gm != null ? _enviro(gm) : null;
            bool hasClock = enviro != null;
            float local = hasClock ? _internalHour(enviro) : 0f;
            if (hasClock && now >= _nextModeRead) { _nextModeRead = now + 1.0; _localMode = (byte)_progress(_gameTime(enviro)); }
            bool sleeping = player != null && _passingOn(player);

            if (region != _region) OnLocalRegionChanged(region, now);
            if (_hooksOk) SleepHooks.Poll();   // completed sleep: passingTime edge (no StopIt call)
            if (_presenter != null && _presenter.Active && _presenter.Frame(local)) { _sampler.Reset(); if (!_host) _adoptPending = _region != Regions.None; }
            if (_presenter != null && !_host) ClientShowFrame(gm);

            if (_host) HostFrame(now, gm, enviro, hasClock, local, sleeping);
            else ClientFrame(now, dt, gm, enviro, hasClock, local, sleeping);

            if (now >= _nextStatus) { _nextStatus = now + 0.25; BuildStatus(); }
            long cost = Stopwatch.GetTimestamp() - t0;
            _costTicks += cost; _frames++; if (cost > _costMax) _costMax = cost;
            if (now >= _nextStats) { _nextStats = now + 30; LogStats(); }
        }

        void OnLocalRegionChanged(int region, double now)
        {
            _log.Info("REGION local " + Regions.Name(_region) + " -> " + Regions.Name(region) + " scenes=" + string.Join("+", new List<string>(_scenes).ToArray()));
            _region = region;
            _adoptPending = region != Regions.None;
            _sampler.Reset();
            if (_sleep.Sleeping || _sleep.Awaiting) _log.Info("SLEEP transaction dropped (region changed)");
            _sleep.Cancel(); _ownerRev = uint.MaxValue;
            if (_stepping) { _stepping = false; _log.Warn("TIME_STEP cancelled (left region)"); }
            StopLapse("left region", true);
            if (_presenter != null && _presenter.Active) _presenter.Stop("region changed");
            if (_host)
            {
                // Host entering a region without an established clock: its own game clock (restored from its save,
                // i.e. its previous region's time) becomes the region clock; only established clocks are adopted.
                RegionClock existing;
                if (region != Regions.None && !_wh.Clocks.TryGet(region, out existing)) _adoptPending = false;
                _wh.SetPlayerRegion(_localSlot, region, now);
                if (_wh.LastNote != null) _log.Info("REGION_CLOCK " + _wh.LastNote);
                _log.Info("REGION_OWNERS " + OwnersText());
                FlushOutbox();
            }
        }

        // DayNightTime.Update counts an hour whenever GameManager.currentHour != prevHour. Clock writes made by the mod are
        // not time passing, so both are moved to the written hour (no rent/abilities/day-rollover/bank tick).
        void WriteClock(UnityEngine.Object gm, UnityEngine.Object enviro, float hours, bool suppressTick)
        {
            _setTime(enviro, hours);
            if (!suppressTick) return;
            int h = (int)hours;
            _setHour(gm, h);
            var dn = DayNight();
            if (dn != null) _setPrevHour(dn, h);
            _suppressed++;
        }

        // ---------------- host ----------------
        void HostFrame(double now, UnityEngine.Object gm, UnityEngine.Object enviro, bool hasClock, float local, bool sleeping)
        {
            if (hasClock && !_adoptPending && !_stepping) _wh.FallbackHours = local;   // session base clock = host game clock
            _wh.HostSleeping = _hooksOk && SleepHooks.Active;
            if (_wh.Active != null && _wh.ExpireSleep(now)) _log.Warn("SLEEP_START transaction expired after " + WorldHost.SleepMaxSeconds + " s without completion");
            if (_presenter != null && _presenter.Active && _wh.Active == null && !_presenter.HasStopAt) _presenter.Stop("the client's sleep transaction ended without completion");
            SleepShowFrame();
            if (_lapse) LapseFrame(gm, hasClock, local);
            if (_region != Regions.None && hasClock)
            {
                if (_adoptPending)
                {
                    _adoptPending = false;
                    float model = _wh.Clocks.Model(_region, now);
                    if (!float.IsNaN(model)) Adopt(gm, enviro, local, model);
                    _sampler.Reset();
                }
                else if (_stepping) Step(now, gm, enviro, local);
                else
                {
                    RegionClock anchor;
                    if (_sampler.Sample(now, true, _localMode, local, out anchor))
                    {
                        _wh.Clocks.Report(_region, _localSlot, anchor, now, true, _wh.Outbox);
                        _reports++;
                    }
                }
                _timeStatus = Fmt(local) + (_stepping ? " catching up to " + Fmt(_wh.Clocks.Model(_region, now)) : _lapse ? " time-lapse (P" + _remoteSlot + " sleeping)" : " (region authority)") + (sleeping ? " sleeping" : "");
            }
            else _timeStatus = hasClock ? Fmt(local) + " (no region)" : "no clock";

            if (gm != null && now >= _nextCalendar)
            {
                _nextCalendar = now + 1.0;
                _wh.SetCalendar(new Calendar { Day = _day(gm), Week = _week(gm) });
            }
            _wh.Tick(now);
            FlushOutbox();
        }

        // M4.2: client proposals are applied in arrival order once per frame, after this frame's UI events (the host's
        // own travel click is decided inside its Select* call) -> requests in the same frame: the host's wins.
        readonly Queue<KeyValuePair<uint, StateEntry>> _queued = new Queue<KeyValuePair<uint, StateEntry>>();
        const int MaxQueued = 256;

        public void LateUpdate()
        {
            if (!_host || _queued.Count == 0) return;
            while (_queued.Count > 0) { var q = _queued.Dequeue(); HostProposal(q.Key, q.Value); }
        }

        internal void DrainTravelNotes()
        {
            if (_wh == null || _wh.Travel.Notes.Count == 0) return;
            foreach (var n in _wh.Travel.Notes) _log.Info("TRAVEL " + n);
            _wh.Travel.Notes.Clear();
        }

        void Adopt(UnityEngine.Object gm, UnityEngine.Object enviro, float local, float target)
        {
            var c = TimeMath.Decide(local, target, 0f, true);
            if (c.Action == TimeAction.None) return;
            WriteClock(gm, enviro, c.NewHours, c.HourChanged);
            _adopts++;
            _log.Info("TIME adopt " + Regions.Name(_region) + " clock " + Fmt(local) + " -> " + Fmt(c.NewHours) + (c.HourChanged ? " (hour tick suppressed)" : ""));
        }

        // ---- host time-lapse (client sleeping in the host's region) ----
        bool StartLapse(UnityEngine.Object enviro, int targetHour)
        {
            if (!_lapseOk || enviro == null || targetHour < 0 || targetHour > 23 || (_localMode != 1 && _localMode != 2) || SleepHooks.Active) return false;
            var gt = _gameTime(enviro); if (gt == null) return false;
            float cyc = _cycle(gt);
            if (Math.Abs(cyc - TimeMath.NominalCycleMinutes) > 0.01f) return false;   // game runs its own day speed (own sleep, slowdown, ending)
            _lapseTime = gt; _lapsePrevCycle = cyc; _lapseTarget = targetHour; _lapseUntil = float.NaN; _lapseStartedAt = Now;
            _setCycle(gt, TimeMath.SleepCycleMinutes);
            _lapse = true; _lapses++;
            return true;
        }

        // Per frame only while the time-lapse runs. Forward only: stops at the game's own rule (target hour index) or,
        // once the sleeper's end arrived, when the local clock reaches the sleeper's final time.
        void LapseFrame(UnityEngine.Object gm, bool hasClock, float local)
        {
            if (Math.Abs(_cycle(_lapseTime) - TimeMath.SleepCycleMinutes) > 1e-4f) { StopLapse("day length changed by the game", false); return; }
            var a = _wh.Active;
            if (float.IsNaN(_lapseUntil) && (a == null || a.Region != _region || !a.HostWatching)) { StopLapse("sleep transaction ended", true); return; }
            if (gm == null || !hasClock) return;
            if (TimeMath.LapseDone(local, _hour(gm), _lapseTarget, _lapseUntil))
                StopLapse(float.IsNaN(_lapseUntil) ? "target hour " + _lapseTarget + " reached" : "reached sleeper's end time " + Fmt(_lapseUntil), true);
        }

        void StopLapse(string why, bool restore)
        {
            if (!_lapse) return;
            _lapse = false;
            if (restore && _lapseTime != null && Math.Abs(_cycle(_lapseTime) - TimeMath.SleepCycleMinutes) < 1e-4f) _setCycle(_lapseTime, _lapsePrevCycle);
            _lapseTime = null;
            _sampler.Reset();   // publish the settled clock at normal rate next frame
            var gm = _gm; var env = gm != null ? _enviro(gm) : null;
            _log.Info("TIMELAPSE stop (" + why + ") at " + (env != null ? Fmt(_internalHour(env)) : "--:--") + " after " + (Now - _lapseStartedAt).ToString("0.0") + " s");
        }

        // Catch-up after a validated client sleep in the host's region: advance hour by hour through the game's own
        // setter and let DayNightTime run its normal hourly tick once per hour (as during the host's own sleep).
        void Step(double now, UnityEngine.Object gm, UnityEngine.Object enviro, float local)
        {
            float target = _wh.Clocks.Model(_region, now);
            float remaining = TimeMath.ForwardDiff(target, local);
            if (remaining < TimeMath.Deadband || remaining > 23.5f) { FinishStep(gm, enviro, local, target, false); return; }
            var dn = DayNight();
            if (dn == null) { FinishStep(gm, enviro, local, target, true); return; }
            if (_prevHour(dn) != (int)local)
            {
                if (++_stepWait > 60) FinishStep(gm, enviro, local, target, true);   // DayNightTime not ticking: no stepping
                return;
            }
            _stepWait = 0;
            bool final;
            float next = TimeMath.StepPlan(local, target, out final);
            if (!final) { _setTime(enviro, next); _stepTicks++; }
            else FinishStep(gm, enviro, local, target, false);
        }

        void FinishStep(UnityEngine.Object gm, UnityEngine.Object enviro, float local, float target, bool fallback)
        {
            if (Math.Abs(TimeMath.SignedDiff(target, local)) >= TimeMath.Deadband)
                WriteClock(gm, enviro, target, fallback);   // normal path: final write stays inside the current hour
            _stepping = false; _sampler.Reset();
            if (fallback) _stepFallbacks++;
            _log.Info("TIME_STEP done " + Regions.Name(_region) + " now " + Fmt(target) + " hourly ticks=" + _stepTicks + (fallback ? " (FALLBACK: DayNightTime not ticking, set directly, ticks suppressed)" : ""));
        }

        internal void FlushOutbox()
        {
            DrainTravelNotes();
            if (_wh.Outbox.Count == 0) return;
            if (_node.IsConnected)
                foreach (var e in _wh.Outbox) { _w.Reset(); StateMessages.WriteDelta(_w, _reg.Epoch, e); Send(); }
            _wh.Outbox.Clear();
        }

        void Send()
        {
            _node.Send(_w, DeliveryMethod.ReliableOrdered, SessionNode.ChannelReliable);
            _msgOut++; _bytesOut += _w.Length;
        }

        void SendSnapshot(string why)
        {
            FlushOutbox();
            _wh.Clocks.RefreshRunning(Now, _wh.Outbox);
            _wh.Outbox.Clear();   // contained in the snapshot
            _w.Reset(); StateMessages.WriteSnapshot(_w, _reg); Send();
            _log.Info("STATE_SNAPSHOT sent (" + why + ") epoch=" + _reg.Epoch.ToString("X8") + " rev=" + _reg.HeadRev + " entries=" + _reg.Count + " bytes=" + _w.Length);
        }

        void HostProposal(uint id, StateEntry p)
        {
            _proposals++;
            uint rev; int sleepRegion; float sleepHours;
            var code = _wh.HandleProposal(_node.RemoteInstanceId ?? "?", _remoteSlot, id, p, Now, out rev, out sleepRegion, out sleepHours);
            if (p.Key == WorldKeys.VehicleTake && _wh.LastNote != null) _log.Info("VEHICLE " + _wh.LastNote);
            if (p.Key == WorldKeys.Region && code == ProposalCode.Accepted)
            {
                if (_wh.LastNote != null) _log.Info("REGION_CLOCK " + _wh.LastNote);
                _log.Info("REGION_OWNERS " + OwnersText());
            }
            if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged)
            {
                _rejected++;
                string detail = "";
                SleepRequest s; RegionClock rc;
                SleepStart ss;
                if (p.Key == WorldKeys.SleepStart && SleepStart.TryParse(p.Value, out ss))
                    detail = " from=" + Fmt(ss.From) + " target=" + ss.TargetHour + " model=" + Fmt(_wh.Clocks.Model(p.ScopeArg, Now)) + " senderRegion=" + Regions.Name(RegionDirectory.RegionOf(_reg, _remoteSlot))
                        + (_wh.Active != null ? " active=P" + _wh.Active.Slot + "@" + Regions.Name(_wh.Active.Region) : "") + (_wh.HostSleeping ? " hostSleeping" : "");
                else if (p.Key == WorldKeys.Sleep && SleepRequest.TryParse(p.Value, out s))
                    detail = " from=" + Fmt(s.From) + " advance=" + s.Advance.ToString("0.00") + "h model=" + Fmt(_wh.Clocks.Model(p.ScopeArg, Now)) + " senderRegion=" + Regions.Name(RegionDirectory.RegionOf(_reg, _remoteSlot));
                else if (p.Key == WorldKeys.Clock && RegionClock.TryParse(p.Value, out rc))
                    detail = " report=" + rc + " model=" + Fmt(_wh.Clocks.Model(p.ScopeArg, Now));
                _log.Warn("PROPOSAL id=" + id + " " + p.Id + " -> " + code + detail);
            }
            if (p.Key == WorldKeys.SleepStart && code == ProposalCode.Accepted && _wh.Active != null && _wh.Active.StartId == id)
            {
                var a = _wh.Active;
                bool watch = false;
                string showWhy = null; bool shown = false;
                if (a.Region == _region && !_adoptPending && !_stepping)
                {
                    var gm = _gm;
                    // M4.3.3: the host presents the client's sleep with the original PassTime; its fast-forward IS the region
                    // clock (watched: no second advance). Fallback: the M3.1.2 day-speed time-lapse, else catch-up afterwards.
                    shown = _presenter != null && _presenter.TryStart(gm, a.TargetHour, id, out showWhy);
                    watch = shown || StartLapse(gm != null ? _enviro(gm) : null, a.TargetHour);
                }
                _wh.SetWatching(watch);
                _log.Info("SLEEP_START accepted id=" + id + " P" + a.Slot + " " + Regions.Name(a.Region) + " from " + Fmt(a.From) + " until hour " + a.TargetHour
                    + (a.Region != _region ? " (host elsewhere: no presentation)" : shown ? " -> host shows the original sleep presentation" : watch ? " -> host day-speed time-lapse (presentation not possible: " + showWhy + ")" : " (host in region, presentation not possible: " + showWhy + "; catch-up after completion)"));
            }
            if (sleepRegion != Regions.None)
            {
                if (_wh.SleepWatched)
                {
                    _log.Info("SLEEP accepted id=" + id + " " + Regions.Name(sleepRegion) + " sleeper end " + Fmt(sleepHours) + " (host watched: host clock authoritative, no extra advance)");
                    if (_lapse) _lapseUntil = sleepHours;   // forward-only settle; stops immediately if already there
                    if (_presenter != null && _presenter.Active) _presenter.StopAtHours(sleepHours);
                }
                else
                {
                    _log.Info("SLEEP accepted id=" + id + " " + Regions.Name(sleepRegion) + " -> " + Fmt(sleepHours) + (sleepRegion == _region ? " (host in region: catching up hour by hour)" : ""));
                    if (sleepRegion == _region && !_adoptPending) { StopLapse("superseded by catch-up", true); _stepping = true; _stepRegion = sleepRegion; _stepWait = 0; _stepTicks = 0; }
                }
            }
            FlushOutbox();
            _w.Reset(); StateMessages.WriteResult(_w, id, code, rev); Send();
        }

        // ---------------- client ----------------
        void ClientFrame(double now, float dt, UnityEngine.Object gm, UnityEngine.Object enviro, bool hasClock, float local, bool sleeping)
        {
            if (_node.IsConnected && _reg.HasSnapshot && _proposedRegion != _region) ProposeRegion();
            if (_reg.HeadRev != _ownerRev) { _ownerRev = _reg.HeadRev; _owner = _region == Regions.None ? (byte)0 : RegionDirectory.OwnerOf(_reg, _region); TrackClockEntry(now); }

            if (!_node.IsConnected || !_reg.HasSnapshot) { _timeStatus = hasClock ? Fmt(local) + " (offline, local clock)" : "no clock"; return; }
            if (_region == Regions.None || !hasClock) { _timeStatus = "not in region"; return; }
            if (_sleep.Awaiting && _sleep.TimedOut(now))
            {
                _rejected++; _adoptPending = true;
                _log.Warn("SLEEP no host result within " + SleepTransaction.AckTimeout + " s (re-adopting region clock)");
            }
            // Sleep UI open (PassTime.Begin .. QuitIt, covers the whole fast-forward): no reports, no corrections. The
            // sampler restarts afterwards so no rate window spans the pause.
            if (sleeping || _sleep.Sleeping) { _sampler.Reset(); _timeStatus = Fmt(local) + " sleeping (sync paused)"; return; }
            if (_sleep.Awaiting) { _timeStatus = Fmt(local) + " sleep sent, awaiting host"; return; }
            if (RegionDirectory.RegionOf(_reg, _localSlot) != _region || !_hasClock || _clockRegion != _region) { _timeStatus = Fmt(local) + " awaiting region clock"; return; }

            float target = TimeMath.Extrapolate(_clock0, now - _clockRecvAt);
            if (_adoptPending)
            {
                _adoptPending = false;
                Adopt(gm, enviro, local, target);
                _sampler.Reset();
                return;
            }
            if (_owner == _localSlot)
            {
                RegionClock anchor;
                if (_sampler.Sample(now, true, _localMode, local, out anchor))
                {
                    var e = new StateEntry { Scope = StateScope.Region, ScopeArg = _region, Key = WorldKeys.Clock, Value = anchor.ToBytes() };
                    _lastReportId = _nextProposalId++;
                    _w.Reset(); StateMessages.WriteProposal(_w, _lastReportId, e); Send();
                    _reports++;
                }
                _timeStatus = Fmt(local) + " (region owner, host validates)";
            }
            else
            {
                float err = Math.Abs(TimeMath.SignedDiff(target, local));
                if (err > _maxErr) _maxErr = err;
                if (_clock0.Progresses || err >= TimeMath.Deadband)
                {
                    var c = TimeMath.Decide(local, target, dt, false);
                    if (c.Action != TimeAction.None)
                    {
                        WriteClock(gm, enviro, c.NewHours, c.HourChanged);
                        if (c.Action == TimeAction.Snap) { _snaps++; _log.Info("TIME snap " + Fmt(local) + " -> " + Fmt(c.NewHours) + (c.HourChanged ? " (hour tick suppressed)" : "")); }
                        else _blends++;
                    }
                }
                _timeStatus = Fmt(local) + " region " + Fmt(target) + (err < TimeMath.Deadband ? " in sync" : " correcting");
            }

            if (gm != null && now >= _nextCalendar)
            {
                _nextCalendar = now + 1.0;
                var ce = _reg.Get(StateScope.Session, 0, WorldKeys.Calendar);
                Calendar cal;
                if (ce != null && Calendar.TryParse(ce.Value, out cal) && (_day(gm) != cal.Day || _week(gm) != cal.Week))
                {
                    _log.Info("CALENDAR day " + _day(gm) + "/w" + _week(gm) + " -> " + cal.Day + "/w" + cal.Week + " (host)");
                    _setDay(gm, cal.Day); _setWeek(gm, cal.Week);
                    var dn = DayNight(); if (dn != null) _refreshDay(dn);
                }
            }
        }

        void TrackClockEntry(double now)
        {
            if (_region == Regions.None) { _hasClock = false; return; }
            var e = _reg.Get(StateScope.Region, _region, WorldKeys.Clock);
            if (e == null) { _hasClock = false; return; }
            if (e.Rev == _clockRev && _clockRegion == _region) return;
            RegionClock c;
            if (!RegionClock.TryParse(e.Value, out c)) { _log.Warn("TIME invalid region clock rev=" + e.Rev); return; }
            bool first = !_hasClock || _clockRegion != _region;
            _clockRev = e.Rev; _clockRecvAt = now; _clock0 = c; _hasClock = true; _clockRegion = _region;
            if (first) _log.Info("TIME region clock " + Regions.Name(_region) + " " + c + " rev=" + e.Rev);
        }

        void ProposeRegion()
        {
            var e = new StateEntry { Scope = StateScope.Player, ScopeArg = _localSlot, Key = WorldKeys.Region, Value = WorldKeys.Int(_region) };
            uint id = _nextProposalId++;
            _w.Reset(); StateMessages.WriteProposal(_w, id, e); Send();
            _proposedRegion = _region;
            _proposals++;
            _log.Info("PROPOSAL sent id=" + id + " region=" + Regions.Name(_region));
        }

        // ---------------- M4.3.3 co-op sleep presentation event ----------------
        // Host: which regions have a running original sleep (its own, or the client's accepted one) -> "sleepShow" committed on
        // change only. Client: presents the event of its own region unless it is the sleeper.
        void SleepShowFrame()
        {
            int a = Regions.None, b = Regions.None;
            if (_wh.HostSleeping && _region != Regions.None) { a = _region; _wh.SetSleepShow(_region, true, _localSlot, SleepHooks.TargetHour); }
            var act = _wh.Active;
            if (act != null) { b = act.Region; _wh.SetSleepShow(act.Region, true, act.Slot, act.TargetHour); }
            if (_showA != Regions.None && _showA != a && _showA != b) _wh.SetSleepShow(_showA, false, 0, 0);
            if (_showB != Regions.None && _showB != a && _showB != b && _showB != _showA) _wh.SetSleepShow(_showB, false, 0, 0);
            _showA = a; _showB = b;
            if (_wh.Outbox.Count > 0) FlushOutbox();
        }

        void ClientShowFrame(UnityEngine.Object gm)
        {
            if (_region == Regions.None) { if (_presenter.Active) _presenter.Stop("no region"); return; }
            var e = _reg.Get(StateScope.Region, _region, WorldKeys.SleepShow);
            if (e == null) return;
            SleepShowState st;
            if (!SleepShowState.TryParse(e.Value, out st)) return;
            if (_presenter.Active && (!st.Active || st.EventId != _presenter.EventId)) { _presenter.Stop("the sleeper finished or stopped"); _adoptPending = true; return; }
            if (e.Rev == _showRev) return;
            _showRev = e.Rev;
            if (!SleepShowState.ShouldPresent(st, _region, _localSlot, _region) || st.EventId == _shownEvent) return;
            _shownEvent = st.EventId;
            string why;
            if (_presenter.TryStart(gm, st.TargetHour, st.EventId, out why)) _log.Info("SLEEP_SHOW P" + st.Initiator + " sleeps in " + Regions.Name(_region) + ": presenting (event " + st.EventId + ")");
            else _log.Info("SLEEP_SHOW P" + st.Initiator + " sleeps in " + Regions.Name(_region) + ": not presented (" + why + "), the clock follows the host");
        }

        // ---------------- sleep (original PassTime fast-forward) ----------------
        void OnSleepStarted()
        {
            var gm = _gm; if (gm == null || _region == Regions.None) return;
            var enviro = _enviro(gm); if (enviro == null) return;
            float from = _internalHour(enviro);
            if (!_sleep.Start(_region, from)) { _log.Warn("SLEEP start ignored (already sleeping since " + Fmt(_sleep.From) + ")"); return; }
            int target = SleepHooks.TargetHour;
            _log.Info("SLEEP start " + Regions.Name(_region) + " at " + Fmt(from) + " until hour " + target + (_host ? "" : " owner=P" + _owner + " pendingResult=" + _sleep.PendingId));
            if (_host)
            {
                _wh.HostSleeping = true;
                StopLapse("host started its own sleep", false);   // PassTime now drives the day length
                return;
            }
            if (!_node.IsConnected || !_reg.HasSnapshot || target < 0 || target > 23) { _log.Warn("SLEEP_START not sent (" + (target < 0 || target > 23 ? "target hour unknown" : "offline") + "): region clock will be restored from the host"); return; }
            uint id = _nextProposalId++;
            var e = new StateEntry { Scope = StateScope.Region, ScopeArg = _region, Key = WorldKeys.SleepStart, Value = new SleepStart { From = from, TargetHour = (byte)target }.ToBytes() };
            _w.Reset(); StateMessages.WriteProposal(_w, id, e); Send();
            _sleep.StartSent(id);
            _proposals++;
            _log.Info("SLEEP_START sent id=" + id + " " + Regions.Name(_region) + " from=" + Fmt(from) + " target=" + target);
        }

        void OnSleepEnded(bool completed)
        {
            var gm = _gm; var enviro = gm != null ? _enviro(gm) : null;
            if (enviro == null) { if (_sleep.Sleeping) _log.Warn("SLEEP end without clock (dropped)"); _sleep.Cancel(); return; }
            float to = _internalHour(enviro);
            SleepRequest req;
            var end = _sleep.End(_region, to, out req);
            if (end == SleepEnd.NotSleeping) return;
            _sampler.Reset();
            _log.Info("SLEEP end " + Regions.Name(_region) + " " + Fmt(req.From) + " -> " + Fmt(to) + " (+" + req.Advance.ToString("0.00") + " h, "
                + (completed ? "completed" : "stopped") + ")" + (end == SleepEnd.OtherRegion ? " region changed: not sent" : ""));
            if (end != SleepEnd.Ended || _host || req.Advance < SleepTransaction.MinAdvance) return;   // host: its own sampler publishes the region clock
            if (!_node.IsConnected || !_reg.HasSnapshot) { _log.Warn("SLEEP not sent (offline): region clock will be restored from the host"); return; }
            if (!_sleep.MayComplete) { _log.Warn("SLEEP not sent (start " + _sleep.StartState + "): re-adopting the region clock"); _adoptPending = true; return; }
            uint id = _nextProposalId++;
            var e = new StateEntry { Scope = StateScope.Region, ScopeArg = _region, Key = WorldKeys.Sleep, Value = req.ToBytes() };
            _w.Reset(); StateMessages.WriteProposal(_w, id, e); Send();
            _sleep.Sent(id, Now);
            _proposals++;
            _log.Info("SLEEP request sent id=" + id + " " + Regions.Name(_region) + " from=" + Fmt(req.From) + " advance=" + req.Advance.ToString("0.00") + "h rev=" + _reg.HeadRev);
        }

        static string Fmt(float h) { return float.IsNaN(h) ? "--:--" : string.Format("{0:00}:{1:00}", (int)h, (int)((h % 1) * 60)); }

        // ---------------- network ----------------
        void OnRemoteConnected(string id)
        {
            if (_host) SendSnapshot("remote connected " + id);
            else { _reg.Reset(); _hasClock = false; _clockRegion = Regions.None; _proposedRegion = int.MaxValue; _sleep.Disconnected(); _adoptPending = _region != Regions.None; }
        }

        void OnRemoteDisconnected(string reason)
        {
            if (_host)
            {
                _queued.Clear();
                _wh.PlayerDisconnected(_remoteSlot, Now);
                if (_wh.LastNote != null) _log.Info("REGION_CLOCK " + _wh.LastNote);
                DrainTravelNotes();
                _wh.Outbox.Clear();
            }
            else
            {
                if (_sleep.Awaiting) _log.Warn("SLEEP id=" + _sleep.PendingId + " unresolved at disconnect (state restored from the host snapshot on reconnect)");
                _proposedRegion = int.MaxValue; _sleep.Disconnected();
            }
        }

        void OnMessage(NetPeer peer, byte type, NetPacketReader r)
        {
            if (type < (byte)MsgType.StateSnapshot || type > (byte)MsgType.ProposalResult) return;   // M2 types: PlayerSync; 9+: VehicleSync
            _msgIn++; _bytesIn += r.AvailableBytes + 1;
            if (_host)
            {
                if (type == (byte)MsgType.StateProposal)
                {
                    uint id; StateEntry p;
                    if (!StateMessages.TryReadProposal(r, out id, out p)) { _log.Warn("PROPOSAL malformed"); return; }
                    if (_queued.Count >= MaxQueued) { _rejected++; _log.Warn("PROPOSAL queue full: id=" + id + " dropped"); return; }
                    _queued.Enqueue(new KeyValuePair<uint, StateEntry>(id, p));
                }
                else if (type == (byte)MsgType.StateResyncRequest)
                {
                    uint epoch, head;
                    if (!StateMessages.TryReadResync(r, out epoch, out head)) return;
                    _resyncs++;
                    SendSnapshot("resync request epoch=" + epoch.ToString("X8") + " rev=" + head);
                }
                else _log.Warn("WORLD unexpected message " + type + " from client");
                return;
            }

            if (type == (byte)MsgType.StateSnapshot)
            {
                uint epoch, head; List<StateEntry> entries;
                if (!StateMessages.TryReadSnapshot(r, out epoch, out head, out entries)) { _log.Error("STATE_SNAPSHOT malformed"); return; }
                bool newHistory = _reg.Epoch != 0 && _reg.Epoch != epoch;
                _reg.LoadSnapshot(epoch, head, entries);
                _ownerRev = uint.MaxValue; _hasClock = false;
                _log.Info("STATE_SNAPSHOT received epoch=" + epoch.ToString("X8") + " rev=" + head + " entries=" + entries.Count + (newHistory ? " (NEW host history)" : ""));
                _proposedRegion = int.MaxValue;
                _adoptPending = _region != Regions.None;
            }
            else if (type == (byte)MsgType.StateDelta)
            {
                uint epoch; StateEntry e;
                if (!StateMessages.TryReadDelta(r, out epoch, out e)) { _log.Warn("STATE_DELTA malformed"); return; }
                var res = _reg.ApplyDelta(epoch, e);
                if (res == DeltaResult.Applied)
                {
                    _deltas++;
                    if (e.Key == WorldKeys.Region) _log.Info("STATE_DELTA rev=" + e.Rev + " " + Describe(e));
                }
                else if (res == DeltaResult.Duplicate || res == DeltaResult.NoSnapshot) _dupes++;
                else
                {
                    _gaps++;
                    _log.Warn("STATE_DELTA " + res + " rev=" + e.Rev + " local=" + _reg.HeadRev + "; requesting resync");
                    _w.Reset(); StateMessages.WriteResync(_w, _reg.Epoch, _reg.HeadRev); Send();
                }
            }
            else if (type == (byte)MsgType.ProposalResult)
            {
                uint id, rev; ProposalCode code;
                if (!StateMessages.TryReadResult(r, out id, out code, out rev)) return;
                bool ok = code == ProposalCode.Accepted || code == ProposalCode.Unchanged;
                if (ProposalResultHook != null && ProposalResultHook(id, code)) return;   // M4.2 travel / vehicle proposals
                if (ProposalResultHook2 != null && ProposalResultHook2(id, code)) return; // M4.3.2b shared car storage
                if (ProposalResultHook3 != null && ProposalResultHook3(id, code)) return; // M4.3.3 Player-2 inventory reports
                if (_sleep.IsStart(id))
                {
                    _sleep.StartResult(ok);
                    if (ok) _log.Info("SLEEP_START ack id=" + id);
                    else { _rejected++; _log.Warn("SLEEP_START rejected by host id=" + id + " " + code + " (sleep stays local; region clock restored afterwards)"); }
                }
                else if (_sleep.IsPending(id))
                {
                    _sleep.Resolve();
                    if (ok) _log.Info("SLEEP ack id=" + id + " rev=" + rev + " clockRev=" + _clockRev);
                    else { _rejected++; _log.Warn("SLEEP rejected by host id=" + id + " " + code + " (region clock restored)"); _adoptPending = true; }
                }
                else if (!ok)
                {
                    _rejected++;
                    _log.Warn("PROPOSAL rejected by host id=" + id + (id == _lastReportId ? " (clock report)" : "") + " " + code + " (re-adopting region clock)");
                    _adoptPending = true;
                }
            }
        }

        static string Describe(StateEntry e)
        {
            int v;
            if (e.Key == WorldKeys.Region && WorldKeys.TryInt(e.Value, out v)) return "P" + e.ScopeArg + " in " + Regions.Name(v);
            return e.Id;
        }

        string OwnersText()
        {
            var parts = new List<string>();
            foreach (var kv in RegionDirectory.Occupants(_reg)) parts.Add(Regions.Name(kv.Key) + ":P" + kv.Value[0]);
            return parts.Count == 0 ? "-" : string.Join(",", parts.ToArray());
        }

        // ---------------- diagnostics / overlay ----------------
        void BuildStatus()
        {
            int remote = RegionDirectory.RegionOf(_reg, _remoteSlot);
            _status = "region " + Regions.Name(_region) + " / remote " + Regions.Name(remote) + " | time " + _timeStatus + " | rev " + _reg.HeadRev;
        }

        void LogStats()
        {
            double avgUs = _frames > 0 ? _costTicks * 1e6 / Stopwatch.Frequency / _frames : 0;
            double maxUs = _costMax * 1e6 / Stopwatch.Frequency;
            _log.Info("WORLD_STATS rev=" + _reg.HeadRev + " entries=" + _reg.Count + " region=" + Regions.Name(_region) + " owners=" + OwnersText()
                + " time=" + _timeStatus + " | msgs out/in=" + _msgOut + "/" + _msgIn + " bytes out/in=" + _bytesOut + "/" + _bytesIn
                + " reports=" + _reports + " adopts=" + _adopts + " blends=" + _blends + " snaps=" + _snaps + " suppressedTicks=" + _suppressed
                + " stepFallbacks=" + _stepFallbacks + " maxErr=" + _maxErr.ToString("0.000") + "h deltas=" + _deltas + " dupes=" + _dupes + " gaps=" + _gaps
                + " resyncs=" + _resyncs + " timelapses=" + _lapses + " proposals=" + _proposals + " rejected=" + _rejected
                + " | update avg=" + avgUs.ToString("0.0") + "us max=" + maxUs.ToString("0") + "us frames=" + _frames);
            _maxErr = 0; _costTicks = 0; _costMax = 0; _frames = 0;
        }

        public string StatusLine { get { return _status; } }

        // M4.1 VehicleSync access (same main thread): cached GameManager, confirmed local region, registry, host logic.
        internal UnityEngine.Object GameManagerObject { get { return _gm; } }
        internal int LocalRegion { get { return _region; } }
        internal StateRegistry Registry { get { return _reg; } }
        internal WorldHost Host { get { return _wh; } }
        internal byte LocalSlot { get { return _localSlot; } }
        internal byte RemoteSlot { get { return _remoteSlot; } }
        internal bool Synced { get { return _host || (_node.IsConnected && _reg.HasSnapshot); } }
        internal double Clock { get { return Now; } }

        // M4.2 client proposals from VehicleSync share the per-instance increasing proposal id (ProposalGate order).
        internal Func<uint, ProposalCode, bool> ProposalResultHook;
        internal Func<uint, ProposalCode, bool> ProposalResultHook2;
        internal Func<uint, ProposalCode, bool> ProposalResultHook3;
        // M4.3.3 hotfix: bounded recovery when an expected entry (Player-2 record) is missing on the client.
        internal void RequestResync(string why)
        {
            if (_host || !_node.IsConnected) return;
            _resyncs++;
            _log.Info("STATE resync requested (" + why + ") epoch=" + _reg.Epoch.ToString("X8") + " rev=" + _reg.HeadRev);
            _w.Reset(); StateMessages.WriteResync(_w, _reg.Epoch, _reg.HeadRev); Send();
        }

        internal uint SendProposal(StateEntry e)
        {
            uint id = _nextProposalId++;
            _w.Reset(); StateMessages.WriteProposal(_w, id, e); Send();
            _proposals++;
            return id;
        }

        public void Dispose()
        {
            _node.MessageReceived -= OnMessage;
            _node.RemoteConnected -= OnRemoteConnected;
            _node.RemoteDisconnected -= OnRemoteDisconnected;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            StopLapse("shutdown", true);
            if (_presenter != null) { _presenter.Dispose(); _presenter = null; }
            if (_hooksOk) { SleepHooks.Started -= OnSleepStarted; SleepHooks.Ended -= OnSleepEnded; SleepHooks.Uninstall(); }
        }
    }
}
