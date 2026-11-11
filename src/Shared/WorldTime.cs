// M3.1: per-region time of day. Game facts (CODEBASE_MAP §3.12): Enviro keeps time as EnviroCore.internalHour (0..24),
// advanced by deltaTime / GameTime.cycleLengthInMinutes (28 min per day normally, 0.2 while PassTime sleeps);
// EnviroCore.SetInternalTimeOfDay(float) is the game's own setter (SaveGame.LoadGame). DayNightTime.Update runs one
// hourly tick (rent, abilities, day/week rollover, bank interest) whenever the hour index changes.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    // Authoritative clock of one region (Region scope, key "clock"). 10 bytes on the wire.
    public struct RegionClock
    {
        public float Hours;     // 0 <= Hours < 24
        public float Rate;      // game hours per real second
        public byte Mode;       // EnviroTime.TimeProgressMode: 0 None, 1 Simulated, 2 OneDay, 3 SystemTime
        public bool Running;    // false = frozen (region unoccupied)

        public const int WireSize = 10;
        public bool Progresses { get { return Running && (Mode == 1 || Mode == 2) && Rate > 0; } }

        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put(Hours); w.Put(Rate); w.Put(Mode); w.Put(Running);
            return w.CopyData();
        }

        public static bool TryParse(byte[] b, out RegionClock c)
        {
            c = default(RegionClock);
            if (b == null || b.Length != WireSize) return false;
            var r = new NetDataReader(b);
            c.Hours = r.GetFloat(); c.Rate = r.GetFloat(); c.Mode = r.GetByte(); c.Running = r.GetBool();
            if (float.IsNaN(c.Hours) || float.IsInfinity(c.Hours) || c.Hours < 0 || c.Hours >= 24) return false;
            if (float.IsNaN(c.Rate) || float.IsInfinity(c.Rate) || c.Rate < 0 || c.Rate > 24f) return false;   // sleep fast-forward is 2 h/s
            return c.Mode <= 3;
        }

        public override string ToString()
        {
            return string.Format("{0:00}:{1:00} rate={2:0.#####}h/s mode={3}{4}", (int)Hours, (int)((Hours % 1) * 60), Rate, Mode, Running ? "" : " frozen");
        }
    }

    // Shared calendar (Session scope, key "calendar"): global progression, from the Host's game (GameManager.currentDayInt/currentWeek).
    public struct Calendar
    {
        public int Day, Week;
        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(Day); w.Put(Week); return w.CopyData(); }
        public static bool TryParse(byte[] b, out Calendar c)
        {
            c = default(Calendar);
            if (b == null || b.Length != 8) return false;
            c.Day = BitConverter.ToInt32(b, 0); c.Week = BitConverter.ToInt32(b, 4);
            return c.Day >= 0 && c.Day <= 7 && c.Week >= 0;
        }
    }

    // Sleep request payload (Region scope, key "sleep", client -> host only; never stored).
    public struct SleepRequest
    {
        public float From, Advance;
        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(From); w.Put(Advance); return w.CopyData(); }
        public static bool TryParse(byte[] b, out SleepRequest s)
        {
            s = default(SleepRequest);
            if (b == null || b.Length != 8) return false;
            s.From = BitConverter.ToSingle(b, 0); s.Advance = BitConverter.ToSingle(b, 4);
            return !float.IsNaN(s.From) && s.From >= 0 && s.From < 24 && !float.IsNaN(s.Advance) && s.Advance > 0 && s.Advance <= 24;
        }
    }

    // Sleep start (Region scope, key "sleepStart", client -> host only; never stored; M3.1.2). TargetHour = the game's
    // PassTime.currentHr (slider value): PassTime.Update fast-forwards until GameManager.currentHour == currentHr.
    public struct SleepStart
    {
        public float From; public byte TargetHour;
        public const int WireSize = 5;
        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(From); w.Put(TargetHour); return w.CopyData(); }
        public static bool TryParse(byte[] b, out SleepStart s)
        {
            s = default(SleepStart);
            if (b == null || b.Length != WireSize) return false;
            s.From = BitConverter.ToSingle(b, 0); s.TargetHour = b[4];
            return !float.IsNaN(s.From) && s.From >= 0 && s.From < 24 && s.TargetHour < 24;
        }
    }

    public enum SleepEnd { NotSleeping, OtherRegion, Ended }
    public enum SleepStartState { None, Pending, Accepted, Rejected }

    // One sleep transaction of the local player (pure; M3.1.1). Verified (PassTime IL): a sleep that runs to its target
    // hour is finished inside PassTime.Update (passingTime = false, cycle 28) WITHOUT calling StopIt; StopIt only runs on
    // Esc / survival abort. The caller therefore reports the end from the passingTime edge as well as from StopIt; End
    // is idempotent. The request id is held until the host's result, a disconnect, or AckTimeout.
    public sealed class SleepTransaction
    {
        public const double AckTimeout = 15.0;
        public const float MinAdvance = 0.05f;

        public bool Sleeping { get; private set; }
        public float From { get; private set; }
        public int Region { get; private set; }
        public uint PendingId { get; private set; }
        // M3.1.2: the start is announced to the host (reliable "sleepStart"); only an accepted (or still unanswered,
        // ordered before the end) start may be completed by a "sleep" request.
        public uint StartId { get; private set; }
        public SleepStartState StartState { get; private set; }
        double _sentAt;

        public bool Awaiting { get { return PendingId != 0; } }
        public bool MayComplete { get { return StartState == SleepStartState.Pending || StartState == SleepStartState.Accepted; } }

        // false: already sleeping (repeated PassStart) -> the original start time is kept.
        public bool Start(int region, float hours)
        {
            if (Sleeping) return false;
            Sleeping = true; From = hours; Region = region;
            StartId = 0; StartState = SleepStartState.None;
            return true;
        }

        public void StartSent(uint id) { StartId = id; StartState = SleepStartState.Pending; }
        public bool IsStart(uint id) { return id != 0 && id == StartId; }
        public void StartResult(bool ok) { StartState = ok ? SleepStartState.Accepted : SleepStartState.Rejected; StartId = 0; }

        // Connection lost: the host dropped the transaction (SetPlayerRegion None); a later end must not be sent.
        public void Disconnected()
        {
            PendingId = 0; StartId = 0;
            if (Sleeping) StartState = SleepStartState.Rejected;
        }

        public SleepEnd End(int region, float hours, out SleepRequest req)
        {
            req = default(SleepRequest);
            if (!Sleeping) return SleepEnd.NotSleeping;
            Sleeping = false;
            req.From = From; req.Advance = TimeMath.ForwardDiff(hours, From);
            return region == Region && region != Regions.None ? SleepEnd.Ended : SleepEnd.OtherRegion;
        }

        public void Sent(uint id, double now) { PendingId = id; _sentAt = now; }
        public bool IsPending(uint id) { return id != 0 && id == PendingId; }
        public void Resolve() { PendingId = 0; }

        public bool TimedOut(double now)
        {
            if (PendingId == 0 || now - _sentAt < AckTimeout) return false;
            PendingId = 0;
            return true;
        }

        public void Cancel() { Sleeping = false; PendingId = 0; StartId = 0; StartState = SleepStartState.None; }
    }

    // Host-side record of the one running remote (client) sleep. One at a time per session (two players).
    public sealed class ActiveSleep
    {
        public byte Slot; public int Region; public uint StartId; public float From; public int TargetHour; public double StartedAt;
        public bool HostWatching;   // host runs the original Enviro fast-forward in that region -> its clock is authoritative
    }

    public enum TimeAction { None, Blend, Snap }

    public struct TimeCorrection
    {
        public TimeAction Action;
        public float NewHours;
        public bool HourChanged;   // our write changed the hour index: caller suppresses DayNightTime's hourly tick
    }

    public static class TimeMath
    {
        public const float NominalRate = 24f / (28f * 60f);   // PassTime.StopIt restores cycleLengthInMinutes = 28
        public const float NominalCycleMinutes = 28f;
        public const float SleepCycleMinutes = 0.2f;          // PassTime.Update while passingTime (≈ 2 game h per real s)

        // Observer time-lapse stop rule. Without a final time: the game's own rule (hour index reached the target hour).
        // With the sleeper's final time: forward-only settle (stop once local has reached or passed it).
        public static bool LapseDone(float local, int hourIndex, int targetHour, float until)
        {
            if (!float.IsNaN(until)) return SignedDiff(local, until) >= 0f;
            return hourIndex == targetHour;
        }
        public const float Deadband = 0.01f;     // 36 game seconds: ignore
        public const float BlendLimit = 0.25f;   // 15 game minutes: above this, snap
        public const float BlendSeconds = 1.5f;
        public const double MaxExtrapolation = 30.0;

        public static float Wrap24(double h)
        {
            double r = h % 24.0; if (r < 0) r += 24.0;
            return r >= 24.0 ? 0f : (float)r;
        }

        // Shortest signed distance from 'from' to 'to' in hours, in (-12, 12].
        public static float SignedDiff(float to, float from)
        {
            double d = (to - from) % 24.0;
            if (d > 12) d -= 24; else if (d <= -12) d += 24;
            return (float)d;
        }

        // Forward distance (0..24) from 'from' to 'to'.
        public static float ForwardDiff(float to, float from) { return Wrap24(to - from); }

        // Receiver side: anchors older than MaxExtrapolation are not trusted further (owner re-anchors every <= 10 s).
        public static float Extrapolate(RegionClock c, double ageSeconds) { return Extrapolate(c, ageSeconds, MaxExtrapolation); }

        public static float Extrapolate(RegionClock c, double ageSeconds, double maxAge)
        {
            if (!c.Progresses) return c.Hours;
            if (ageSeconds < 0) ageSeconds = 0; else if (ageSeconds > maxAge) ageSeconds = maxAge;
            return Wrap24(c.Hours + c.Rate * ageSeconds);
        }

        // Catch-up plan for a validated sleep: write just past the next hour boundary (one natural hourly tick per
        // step) until the remaining distance fits inside the current hour, then write the target (final).
        public static float StepPlan(float local, float target, out bool final)
        {
            float remaining = ForwardDiff(target, local);
            float toBoundary = (float)Math.Floor(local) + 1f - local;
            if (remaining >= toBoundary && remaining <= 23.5f) { final = false; return Wrap24(Math.Floor(local) + 1.001); }
            final = true; return target;
        }

        public static TimeCorrection Decide(float local, float target, float dt, bool forceSnap)
        {
            var c = new TimeCorrection { Action = TimeAction.None, NewHours = local };
            float diff = SignedDiff(target, local);
            float abs = Math.Abs(diff);
            if (abs < Deadband) return c;
            if (abs <= BlendLimit && !forceSnap)
            {
                float k = Math.Min(1f, Math.Max(0f, dt) / BlendSeconds);
                c.Action = TimeAction.Blend;
                c.NewHours = Wrap24(local + diff * Math.Max(k, 0.05f));
            }
            else { c.Action = TimeAction.Snap; c.NewHours = target; }
            c.HourChanged = (int)c.NewHours != (int)local;
            return c;
        }
    }

    // Owner side: decides when a region clock reading must be (re)published. Keeps revision churn low.
    public sealed class HostTimeSampler
    {
        public double RefreshSeconds = 10.0;
        public const double RateWindow = 0.5;
        public const float PredictionTolerance = 0.02f;

        RegionClock _committed; double _committedAt = -1;
        double _winStart = -1; float _winHours; float _rate = -1;

        public float MeasuredRate { get { return _rate < 0 ? 0 : _rate; } }

        public bool Sample(double now, bool valid, byte mode, float hours, out RegionClock anchor)
        {
            anchor = default(RegionClock);
            if (!valid) { _winStart = -1; return false; }
            if (_winStart < 0) { _winStart = now; _winHours = hours; }
            else if (now - _winStart >= RateWindow)
            {
                float d = TimeMath.SignedDiff(hours, _winHours);
                if (d < 0 || d > 6f) _rate = -1;   // set back / jumped: re-measure
                else _rate = (float)(d / (now - _winStart));
                _winStart = now; _winHours = hours;
            }
            float rate = _rate < 0 ? (_committedAt < 0 ? TimeMath.NominalRate : _committed.Rate) : _rate;
            if (mode != 1 && mode != 2) rate = 0;
            var cur = new RegionClock { Hours = TimeMath.Wrap24(hours), Rate = rate, Mode = mode, Running = true };
            bool need = _committedAt < 0 || cur.Mode != _committed.Mode || now - _committedAt >= RefreshSeconds
                || Math.Abs(cur.Rate - _committed.Rate) > Math.Max(0.15f * _committed.Rate, 1e-5f)
                || Math.Abs(TimeMath.SignedDiff(cur.Hours, TimeMath.Extrapolate(_committed, now - _committedAt))) > PredictionTolerance;
            if (!need) return false;
            _committed = cur; _committedAt = now;
            anchor = cur;
            return true;
        }

        public void Reset() { _committedAt = -1; _winStart = -1; _rate = -1; }
    }

    // Host-only bookkeeping of all region clocks inside the StateRegistry. Pure (time passed in).
    public sealed class RegionClockBook
    {
        public const float ReportTolerance = 0.1f;   // owner reports may drift at most 6 game minutes from the model
        public const float SleepTolerance = 1.0f;    // sleep start must match the region clock within 1 h
        public const float MaxReportRate = 0.05f;    // ~3.5x nominal; faster only through a validated sleep

        readonly StateRegistry _reg;
        readonly Dictionary<int, double> _anchorAt = new Dictionary<int, double>();
        readonly Dictionary<byte, int> _lastRegion = new Dictionary<byte, int>();

        public RegionClockBook(StateRegistry reg) { _reg = reg; }

        public bool TryGet(int region, out RegionClock c)
        {
            c = default(RegionClock);
            var e = _reg.Get(StateScope.Region, region, WorldKeys.Clock);
            return e != null && RegionClock.TryParse(e.Value, out c);
        }

        public float Model(int region, double now)
        {
            RegionClock c; double at;
            if (!TryGet(region, out c)) return float.NaN;
            // Host model: its own clock is reliable, so no age clamp (a region keeps running while its owner is silent).
            return TimeMath.Extrapolate(c, _anchorAt.TryGetValue(region, out at) ? now - at : 0, double.MaxValue);
        }

        StateEntry Put(int region, RegionClock c, double now, List<StateEntry> outbox)
        {
            c.Hours = TimeMath.Wrap24(c.Hours);
            var e = _reg.Commit(StateScope.Region, region, WorldKeys.Clock, c.ToBytes());
            _anchorAt[region] = now;
            if (e != null && outbox != null) outbox.Add(e);
            return e;
        }

        bool Occupied(int region) { return RegionDirectory.OwnerOf(_reg, region) != 0; }

        // Call after the player's region entry was committed. fallbackHours: session base clock for a first-ever region.
        public string PlayerMoved(byte slot, int from, int to, double now, float fallbackHours, List<StateEntry> outbox)
        {
            string why = null;
            RegionClock c;
            if (from != Regions.None)
            {
                _lastRegion[slot] = from;
                if (!Occupied(from) && TryGet(from, out c) && c.Running)
                {
                    Put(from, new RegionClock { Hours = Model(from, now), Rate = 0, Mode = c.Mode, Running = false }, now, outbox);
                    why = Regions.Name(from) + " frozen";
                }
            }
            if (to == Regions.None) return why;
            if (!TryGet(to, out c))
            {
                int prev; float h; string src;
                if (_lastRegion.TryGetValue(slot, out prev) && prev != to && TryGet(prev, out c)) { h = Model(prev, now); src = "P" + slot + " previous region " + Regions.Name(prev); }
                else { h = fallbackHours; src = "session base clock"; }
                Put(to, new RegionClock { Hours = h, Rate = TimeMath.NominalRate, Mode = 1, Running = true }, now, outbox);
                why = (why != null ? why + "; " : "") + Regions.Name(to) + " initialized from " + src;
            }
            else if (!c.Running)
            {
                Put(to, new RegionClock { Hours = c.Hours, Rate = (c.Mode == 1 || c.Mode == 2) ? TimeMath.NominalRate : 0, Mode = c.Mode, Running = true }, now, outbox);
                why = (why != null ? why + "; " : "") + Regions.Name(to) + " resumed at " + c;
            }
            _lastRegion[slot] = to;
            return why;
        }

        // Owner report (host's own reading is trusted; client readings are validated against the model).
        public ProposalCode Report(int region, byte slot, RegionClock rep, double now, bool trusted, List<StateEntry> outbox)
        {
            RegionClock cur;
            if (!TryGet(region, out cur)) return ProposalCode.Invalid;
            if (!trusted)
            {
                if (RegionDirectory.OwnerOf(_reg, region) != slot) return ProposalCode.NotAuthorized;
                if (rep.Rate < 0 || rep.Rate > MaxReportRate || rep.Mode > 3) return ProposalCode.Invalid;
                if (Math.Abs(TimeMath.SignedDiff(rep.Hours, Model(region, now))) > ReportTolerance) return ProposalCode.Invalid;
            }
            rep.Running = true;
            return Put(region, rep, now, outbox) == null ? ProposalCode.Unchanged : ProposalCode.Accepted;
        }

        // Sleep start check: requester is in the region and its start time matches the region clock (normal drift allowed).
        public ProposalCode SleepStartCheck(int region, byte slot, float from, double now)
        {
            RegionClock cur;
            if (RegionDirectory.RegionOf(_reg, slot) != region) return ProposalCode.NotAuthorized;
            if (!TryGet(region, out cur)) return ProposalCode.Invalid;
            if (Math.Abs(TimeMath.SignedDiff(from, Model(region, now))) > SleepTolerance) return ProposalCode.Invalid;
            return ProposalCode.Accepted;
        }

        // Completed sleep (start already validated): the new region time is the start time + slept hours, normal rate.
        public ProposalCode Sleep(int region, SleepRequest s, double now, List<StateEntry> outbox, out float newHours)
        {
            newHours = float.NaN;
            RegionClock cur;
            if (!TryGet(region, out cur)) return ProposalCode.Invalid;
            newHours = TimeMath.Wrap24(s.From + s.Advance);
            float rate = cur.Mode == 1 || cur.Mode == 2 ? TimeMath.NominalRate : 0f;   // never keep a fast-forward rate
            Put(region, new RegionClock { Hours = newHours, Rate = rate, Mode = cur.Mode, Running = true }, now, outbox);
            return ProposalCode.Accepted;
        }

        // A region clock left at a fast-forward rate (time-lapse ended without a fresh owner reading) continues at the
        // normal rate from its current model time.
        public void NormalizeRate(int region, double now, List<StateEntry> outbox)
        {
            RegionClock c;
            if (!TryGet(region, out c) || c.Rate <= MaxReportRate) return;
            Put(region, new RegionClock { Hours = Model(region, now), Rate = TimeMath.NominalRate, Mode = c.Mode, Running = c.Running }, now, outbox);
        }

        // Re-anchor running clocks at 'now' (before a snapshot, so late joiners get fresh anchors).
        public void RefreshRunning(double now, List<StateEntry> outbox)
        {
            var ids = new List<int>();
            foreach (var e in _reg.Entries) if (e.Scope == StateScope.Region && e.Key == WorldKeys.Clock) ids.Add(e.ScopeArg);
            foreach (var r in ids)
            {
                RegionClock c;
                if (TryGet(r, out c) && c.Progresses) { c.Hours = Model(r, now); Put(r, c, now, outbox); }
            }
        }
    }

    // Host-side world logic (pure): region membership, region clocks, calendar, proposals with duplicate protection.
    public sealed class WorldHost
    {
        public readonly StateRegistry Reg;
        public readonly RegionClockBook Clocks;
        readonly ProposalGate _gate = new ProposalGate();
        public readonly List<StateEntry> Outbox = new List<StateEntry>();
        public float FallbackHours = 12f;   // session base clock: the host's own game clock (from its save)
        public string LastNote;

        public readonly FleetBook Fleet;
        public readonly TravelDesk Travel;
        public readonly VehicleMotion Motion = new VehicleMotion();   // M4.2.2: handoff only for settled cars
        public readonly CarBoxLease CarBox = new CarBoxLease();      // M4.3.2b: exclusive shared car storage
        public Func<byte[]> CarBoxSnapshot;                          // host plugin: encoded CarBoxItems of the host's real storage, null if unavailable
        // M4.3.3: host plugin hooks (they touch the host's ItemID counters and the p2inv file); null = feature unavailable
        public Func<P2Inventory, ProposalCode> P2ReportHook;
        public Func<StorageTxnRequest, ProposalCode> StorageTxnHook;
        public delegate bool LiveParkFn(ushort id, out ParkRecord p);
        public LiveParkFn LivePark;   // M4.2.2: freshest pose/fuel known to the host plugin (own physics or last snapshot)

        public WorldHost(uint epoch) { Reg = StateRegistry.CreateHost(epoch); Clocks = new RegionClockBook(Reg); Fleet = new FleetBook(Reg); Travel = new TravelDesk(Reg, Fleet); }

        int RegionOfSlot(byte s) { return RegionDirectory.RegionOf(Reg, s); }

        void AfterFleetChange()
        {
            Fleet.ReassignHolders(RegionOfSlot, Outbox);
            ResetDoorsOfMovedCars();
        }

        // ---- M5.0 car doors ----
        // A local or remote change of an original car door (car_door_L = 0 / car_door_R = 1). The car's simulation holder may
        // always change its own doors (its original CarController opens/closes them on entry/exit); anybody else in the
        // car's region may change them while the car is settled (not driven/moving per the holder's reports).
        public ProposalCode SetDoor(byte slot, ushort id, int door, bool open, double now, out string why) { return SetDoor(slot, id, door, open, DoorMode.Quick, now, out why); }

        // M4.3.1: mode = how the original Door moved (Quick = Door.Use, Slow = hold-E slow swing, FastFinish = SlowFailed).
        public ProposalCode SetDoor(byte slot, ushort id, int door, bool open, DoorMode mode, double now, out string why)
        {
            why = null;
            VehicleRecord r;
            if (slot == 0 || slot > 2 || door < 0 || door >= VehicleDoors.Count || !Fleet.TryGet(id, out r)) { why = "unknown vehicle/door"; return ProposalCode.Invalid; }
            if (!r.Unlocked) { why = "locked"; return ProposalCode.NotAuthorized; }
            if (r.Region == Regions.None || RegionOfSlot(slot) != r.Region) { why = "not in the car's region"; return ProposalCode.NotAuthorized; }
            if (r.Reserved) { why = "travelling"; return ProposalCode.Busy; }
            // M4.3: "stopped" (no movement), not "settled": a driver may sit in a standing car while the other player opens a
            // door to get in as passenger (the 0.5.0 rule refused every door of an occupied car, logged as vdoor -> Busy).
            if (slot != r.Holder && open && !Motion.Stopped(id, now)) { why = "car is moving"; return ProposalCode.Busy; }
            byte mask = DoorMask(id);
            byte next = VehicleDoors.Next(mask, door, open, mode);
            if (next == mask) return ProposalCode.Unchanged;
            var e = Reg.Commit(StateScope.Session, id, WorldKeys.VehicleDoor, new[] { next });
            if (e != null) Outbox.Add(e);
            return ProposalCode.Accepted;
        }

        public byte DoorMask(ushort id)
        {
            var e = Reg.Get(StateScope.Session, id, WorldKeys.VehicleDoor);
            byte m; return e != null && VehicleDoors.TryParseState(e.Value, out m) ? m : (byte)0;
        }

        // Scene objects of a car that changed region start with closed doors (fresh MainScene load): the committed state
        // follows, so a door left open in the old region never re-opens at the destination.
        readonly Dictionary<ushort, int> _doorRegion = new Dictionary<ushort, int>();
        void ResetDoorsOfMovedCars()
        {
            foreach (var id in Fleet.Ids())
            {
                VehicleRecord r; if (!Fleet.TryGet(id, out r)) continue;
                int prev;
                bool known = _doorRegion.TryGetValue(id, out prev);
                _doorRegion[id] = r.Region;
                if (!known || prev == r.Region || DoorMask(id) == 0) continue;
                var e = Reg.Commit(StateScope.Session, id, WorldKeys.VehicleDoor, new byte[] { 0 });
                if (e != null) Outbox.Add(e);
            }
        }

        // M4.2: the host's own travel click, decided in place (same serialized path as client requests).
        public TravelGrant HostTravel(uint id, TravelRequest req, double now)
        {
            var g = Travel.Decide(HostSlot, id, req, RegionOfSlot(HostSlot), RegionOfSlot, now, Outbox);
            AfterFleetChange();
            return g;
        }

        // M4.2.2 driver handoff (client proposal "vehicleTake" or the host in place). The freshest pose/fuel is committed
        // first (ordered before the record change) so the new holder starts exactly where the car stands.
        public ProposalCode TakeOver(byte slot, ushort id, double now, out string why)
        {
            int region = RegionOfSlot(slot);
            bool settled = Motion.Settled(id, now);
            var code = Fleet.TakeOver(id, slot, region, settled, now, false, Outbox, out why);
            if (code != ProposalCode.Accepted) return code;
            ParkRecord p;
            if (LivePark != null && LivePark(id, out p) && p.IsValid && p.HasPose && p.Region == region) Fleet.SetPark(id, p, Outbox);
            code = Fleet.TakeOver(id, slot, region, settled, now, true, Outbox, out why);
            if (code == ProposalCode.Accepted) Motion.Forget(id);
            return code;
        }

        public void PlayerDisconnected(byte slot, double now)
        {
            Travel.Abort(slot, "P" + slot + " disconnected", Outbox);
            SetPlayerRegion(slot, Regions.None, now);
            AfterFleetChange();
        }

        public void Tick(double now)
        {
            if (Travel.Pending > 0 && Travel.Expire(now, Outbox) > 0) AfterFleetChange();
            Fleet.ExpirePassengers(now, Outbox);
            if (CarBox.Expire(now)) CommitCarBox();
        }

        // ---- M4.3.2b shared car storage lease (host in place or client proposal "carboxReq") ----
        // A client grant first commits the host's current storage contents, then the lease; the proposal result follows both
        // (registry deltas precede results), so the client shows the host-authoritative contents in its original UI.
        public ProposalCode CarBoxRequest(byte slot, CarBoxOp op, uint session, double now, out string why)
        {
            why = null;
            if (op == CarBoxOp.Open && slot != HostSlot)
            {
                int region = RegionOfSlot(slot);
                bool carHere = false;
                foreach (var id in Fleet.Ids()) { VehicleRecord r; if (Fleet.TryGet(id, out r) && r.Unlocked && r.Region == region && region != Regions.None) carHere = true; }
                if (!carHere) { why = "no player car in the requester's region"; return ProposalCode.NotAuthorized; }
                if (CarBox.Owner == 0)
                {
                    var snap = CarBoxSnapshot != null ? CarBoxSnapshot() : null;
                    if (snap == null) { why = "host storage contents unavailable"; return ProposalCode.Busy; }
                    var ie = Reg.Commit(StateScope.Session, 0, WorldKeys.CarBoxItems, snap);
                    if (ie != null) Outbox.Add(ie);
                }
            }
            var code = CarBox.Request(slot, op, session, now);
            if (code == ProposalCode.Accepted) CommitCarBox();
            if (code == ProposalCode.Busy) why = "storage open by P" + CarBox.Owner;
            return code;
        }

        public CarBoxLeaseState CarBoxState { get { return CarBox.State; } }

        // ---- M4.3.3 Player-2 inventory ----
        public void CommitP2(P2Inventory rec)
        {
            byte[] d;
            if (rec == null || !rec.TryEncode(out d)) return;
            var e = Reg.Commit(StateScope.Session, 2, WorldKeys.P2Inv, d);
            if (e != null) Outbox.Add(e);
        }

        // A client transfer is valid only inside its own current CarBox session; it also counts as a lease renewal, so a
        // session is never expired while transfers are still arriving.
        public ProposalCode StorageTransfer(byte slot, StorageTxnRequest t, double now)
        {
            if (slot != 2) return ProposalCode.NotAuthorized;
            if (CarBox.Request(slot, CarBoxOp.Renew, t.Session, now) != ProposalCode.Unchanged) return ProposalCode.Stale;
            return StorageTxnHook != null ? StorageTxnHook(t) : ProposalCode.NotAuthorized;
        }

        // ---- M4.3.3 co-op sleep presentation event ----
        uint _sleepShowId;
        readonly Dictionary<int, SleepShowState> _sleepShow = new Dictionary<int, SleepShowState>();
        public void SetSleepShow(int region, bool active, byte initiator, int targetHour)
        {
            if (region == Regions.None) return;
            SleepShowState cur;
            bool has = _sleepShow.TryGetValue(region, out cur);
            if (active && has && cur.Active && cur.Initiator == initiator && cur.TargetHour == targetHour) return;
            if (!active && (!has || !cur.Active)) return;
            var s = active ? new SleepShowState { EventId = ++_sleepShowId, Initiator = initiator, TargetHour = (byte)Math.Max(0, Math.Min(23, targetHour)), Active = true }
                           : new SleepShowState { EventId = cur.EventId, Initiator = cur.Initiator, TargetHour = cur.TargetHour, Active = false };
            _sleepShow[region] = s;
            var e = Reg.Commit(StateScope.Region, region, WorldKeys.SleepShow, s.ToBytes());
            if (e != null) Outbox.Add(e);
        }
        static bool CarBox_TryParse(byte[] v, out CarBoxOp op, out uint session) { return TS2MP.Shared.CarBoxRequest.TryParse(v, out op, out session); }

        void CommitCarBox()
        {
            var e = Reg.Commit(StateScope.Session, 0, WorldKeys.CarBox, CarBox.State.ToBytes());
            if (e != null) Outbox.Add(e);
        }

        // ---- M4.3 passenger seat: op 1 = reserve, 2 = seated (confirm), 0 = leave. Host in place or client proposal.
        public ProposalCode Ride(byte slot, ushort id, byte op, double now, out string why)
        {
            why = null;
            switch (op)
            {
                case 1: return Fleet.ReservePassenger(id, slot, RegionOfSlot(slot), Motion.Stopped(id, now), now, Outbox, out why);
                case 2: { var c = Fleet.ConfirmPassenger(id, slot, Outbox); if (c == ProposalCode.NotAuthorized) why = "no reservation (expired or released)"; return c; }
                case 0: return Fleet.ReleasePassenger(id, slot, Outbox);
                default: why = "unknown op"; return ProposalCode.Invalid;
            }
        }

        // Phase A (M4.3): joint travel is not implemented yet. A passenger cannot travel alone while seated, and a driver with
        // a passenger aboard cannot leave with the car; both are refused visibly instead of detaching or stranding anyone.
        public string SeatTravelBlock(byte slot)
        {
            if (Fleet.RiddenBy(slot) != VehicleIds.None) return "you are a passenger: get out first (joint travel comes in the next stage)";
            var d = Fleet.DrivenBy(slot);
            VehicleRecord r;
            if (d != VehicleIds.None && Fleet.TryGet(d, out r) && r.Passenger != 0) return "passenger aboard: joint travel comes in the next stage, let them get out first";
            return null;
        }

        public bool SetPlayerRegion(byte slot, int region, double now)
        {
            int prev = RegionDirectory.RegionOf(Reg, slot);
            bool exists = Reg.Get(StateScope.Player, slot, WorldKeys.Region) != null;
            if (prev == region && exists) return false;
            var e = Reg.Commit(StateScope.Player, slot, WorldKeys.Region, WorldKeys.Int(region));
            if (e != null) Outbox.Add(e);
            if (region != Regions.None)
            {
                var w = new NetDataWriter(); w.Put(slot); w.Put(Reg.HeadRev);
                var v = Reg.Commit(StateScope.Region, region, WorldKeys.LastVisit, w.CopyData());
                if (v != null) Outbox.Add(v);
            }
            LastNote = Clocks.PlayerMoved(slot, exists ? prev : Regions.None, region, now, FallbackHours, Outbox);
            SleepOnMove(slot, region, now);
            if (CarBox.Release(slot)) CommitCarBox();                     // M4.3.2b: a storage session never survives a region change
            if (region == Regions.None) Fleet.ReleaseAll(slot, Outbox);   // disconnect / menu: no lease survives
            else Fleet.ReleasePassengerOutside(slot, region, Outbox);     // M4.3: a passenger is never seated in a car of another region
            Travel.OnPlayerRegion(slot, region, Outbox);
            AfterFleetChange();
            return true;
        }

        public void SetCalendar(Calendar c)
        {
            var e = Reg.Commit(StateScope.Session, 0, WorldKeys.Calendar, c.ToBytes());
            if (e != null) Outbox.Add(e);
        }

        // sleepRegion/sleepHours are set when a sleep was applied (host must catch up locally if it is in that region).
        public ProposalCode HandleProposal(string instance, byte senderSlot, uint id, StateEntry p, double now, out uint rev, out int sleepRegion, out float sleepHours)
        {
            sleepRegion = Regions.None; sleepHours = float.NaN; SleepWatched = false;
            ProposalCode code;
            if (_gate.TryGetCached(instance, id, out code, out rev)) return code;   // retransmission: never applied twice
            if (p.Scope == StateScope.Player && p.Key == WorldKeys.Region)
            {
                code = ProposalRules.Validate(p, senderSlot);
                if (code == ProposalCode.Accepted)
                {
                    int region; WorldKeys.TryInt(p.Value, out region);
                    if (!SetPlayerRegion(senderSlot, region, now)) code = ProposalCode.Unchanged;
                }
            }
            else if (p.Scope == StateScope.Region && p.Key == WorldKeys.Clock)
            {
                RegionClock c;
                code = RegionClock.TryParse(p.Value, out c) ? Clocks.Report(p.ScopeArg, senderSlot, c, now, false, Outbox) : ProposalCode.Invalid;
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.VehicleClaim)
            {
                ushort vid = p.ScopeArg > 0 && p.ScopeArg <= ushort.MaxValue ? (ushort)p.ScopeArg : VehicleIds.None;
                if (vid == VehicleIds.None || p.Value == null || p.Value.Length != 1 || p.Value[0] > 1) code = ProposalCode.Invalid;
                else if (p.Value[0] == 1) code = Fleet.Claim(vid, senderSlot, RegionDirectory.RegionOf(Reg, senderSlot), Outbox);
                else code = Fleet.Release(vid, senderSlot, Outbox);
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.VehicleTake)
            {
                ushort vid = p.ScopeArg > 0 && p.ScopeArg <= ushort.MaxValue ? (ushort)p.ScopeArg : VehicleIds.None;
                string why = null;
                if (vid == VehicleIds.None || p.Value == null || p.Value.Length != 1 || p.Value[0] != 1) code = ProposalCode.Invalid;
                else code = TakeOver(senderSlot, vid, now, out why);
                LastNote = "take car#" + vid + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.VehicleRide)
            {
                ushort vid = p.ScopeArg > 0 && p.ScopeArg <= ushort.MaxValue ? (ushort)p.ScopeArg : VehicleIds.None;
                string why = null;
                if (vid == VehicleIds.None || p.Value == null || p.Value.Length != 1 || p.Value[0] > 2) code = ProposalCode.Invalid;
                else code = Ride(senderSlot, vid, p.Value[0], now, out why);
                LastNote = "ride car#" + vid + " op " + (p.Value != null && p.Value.Length == 1 ? p.Value[0].ToString() : "?") + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.VehicleDoor)
            {
                ushort vid = p.ScopeArg > 0 && p.ScopeArg <= ushort.MaxValue ? (ushort)p.ScopeArg : VehicleIds.None;
                int door; bool open; DoorMode mode; string why = null;
                if (vid == VehicleIds.None || !VehicleDoors.TryParseProposal(p.Value, out door, out open, out mode)) code = ProposalCode.Invalid;
                else code = SetDoor(senderSlot, vid, door, open, mode, now, out why);
                LastNote = "door car#" + vid + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Player && p.Key == WorldKeys.Travel)
            {
                TravelRequest req;
                if (p.ScopeArg != senderSlot) code = ProposalCode.NotAuthorized;
                else if (!TravelRequest.TryParse(p.Value, out req) || !TravelRules.IsMenuDestination(req.Dest)) code = ProposalCode.Invalid;
                else if (SeatTravelBlock(senderSlot) != null) { code = ProposalCode.Busy; LastNote = "travel P" + senderSlot + " refused: " + SeatTravelBlock(senderSlot); }
                else
                {
                    Travel.Decide(senderSlot, id, req, RegionOfSlot(senderSlot), RegionOfSlot, now, Outbox);
                    AfterFleetChange();
                    code = ProposalCode.Accepted;
                }
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.P2Report)
            {
                P2Inventory rep;
                if (senderSlot != 2 || p.ScopeArg != 2) code = ProposalCode.NotAuthorized;
                else if (!P2Inventory.TryParse(p.Value, out rep)) code = ProposalCode.Invalid;
                else code = P2ReportHook != null ? P2ReportHook(rep) : ProposalCode.NotAuthorized;
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.StorageTxn)
            {
                StorageTxnRequest t = default(StorageTxnRequest);
                if (p.ScopeArg != 0 || !StorageTxnRequest.TryParse(p.Value, out t)) code = ProposalCode.Invalid;
                else code = StorageTransfer(senderSlot, t, now);
                LastNote = "storageTxn #" + t.Txn + " by P" + senderSlot + " -> " + code;
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.CarBoxReq)
            {
                CarBoxOp op; uint session; string why = null;
                if (p.ScopeArg != 0 || !CarBox_TryParse(p.Value, out op, out session)) code = ProposalCode.Invalid;
                else code = CarBoxRequest(senderSlot, op, session, now, out why);
                LastNote = "carbox by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Player && p.Key == WorldKeys.TravelCancel)
            {
                if (p.ScopeArg != senderSlot) code = ProposalCode.NotAuthorized;
                else if (p.Value == null || p.Value.Length != 4) code = ProposalCode.Invalid;
                else { code = Travel.Cancel(senderSlot, BitConverter.ToUInt32(p.Value, 0), Outbox); AfterFleetChange(); }
            }
            else if (p.Scope == StateScope.Region && p.Key == WorldKeys.SleepStart)
            {
                SleepStart s;
                if (!SleepStart.TryParse(p.Value, out s)) code = ProposalCode.Invalid;
                else
                {
                    code = Clocks.SleepStartCheck(p.ScopeArg, senderSlot, s.From, now);
                    if (code == ProposalCode.Accepted && (Active != null || (HostSleeping && RegionDirectory.RegionOf(Reg, HostSlot) == p.ScopeArg)))
                        code = ProposalCode.Busy;   // one sleep at a time; the host's own sleep in that region wins
                    if (code == ProposalCode.Accepted)
                        Active = new ActiveSleep { Slot = senderSlot, Region = p.ScopeArg, StartId = id, From = s.From, TargetHour = s.TargetHour, StartedAt = now };
                }
            }
            else if (p.Scope == StateScope.Region && p.Key == WorldKeys.Sleep)
            {
                SleepRequest s;
                if (!SleepRequest.TryParse(p.Value, out s)) code = ProposalCode.Invalid;
                else if (RegionDirectory.RegionOf(Reg, senderSlot) != p.ScopeArg) code = ProposalCode.NotAuthorized;
                else if (Active == null || Active.Slot != senderSlot || Active.Region != p.ScopeArg || Math.Abs(TimeMath.SignedDiff(s.From, Active.From)) > 1e-3f)
                    code = ProposalCode.Invalid;   // no accepted start for this sleep: never advance
                else
                {
                    sleepHours = TimeMath.Wrap24(s.From + s.Advance);
                    sleepRegion = p.ScopeArg;
                    SleepWatched = Active.HostWatching;
                    if (SleepWatched) { Clocks.NormalizeRate(p.ScopeArg, now, Outbox); code = ProposalCode.Accepted; }   // host clock already advanced
                    else code = Clocks.Sleep(p.ScopeArg, s, now, Outbox, out sleepHours);
                    Active = null;
                }
            }
            else code = ProposalCode.NotAuthorized;   // session state (calendar) and other keys are host-only
            rev = Reg.HeadRev;
            _gate.Record(instance, id, code, rev);
            return code;
        }

        // ---- remote sleep coordination (M3.1.2) ----
        public const byte HostSlot = 1;
        public const double SleepMaxSeconds = 40;   // 24 h at ≈ 2 h/s is 12 s; anything longer is a lost transaction
        public ActiveSleep Active { get; private set; }
        public bool HostSleeping;                    // set by the plugin while the host's own PassTime fast-forward runs
        public bool SleepWatched;                    // last accepted "sleep": host had run the time-lapse (no commit)

        // Call when the host started (true) or could not start (false) its time-lapse for the active sleep.
        public void SetWatching(bool watching) { if (Active != null) Active.HostWatching = watching; }

        public bool ExpireSleep(double now)
        {
            if (Active == null || now - Active.StartedAt < SleepMaxSeconds) return false;
            Clocks.NormalizeRate(Active.Region, now, Outbox);
            Active = null;
            return true;
        }

        // Region membership changes end or detach the active sleep: the sleeper leaving drops it; the watching host
        // leaving stops being authoritative for it (the region clock returns to normal rate).
        void SleepOnMove(byte slot, int region, double now)
        {
            if (Active == null) return;
            if (slot == Active.Slot) { Clocks.NormalizeRate(Active.Region, now, Outbox); Active = null; }
            else if (region != Active.Region && Active.HostWatching) { Active.HostWatching = false; Clocks.NormalizeRate(Active.Region, now, Outbox); }
        }
    }
}
