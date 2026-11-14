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
        public readonly JointTravel Joint;                           // M4.3 Phase B: driver + passenger journey
        public readonly VehicleMotion Motion = new VehicleMotion();   // M4.2.2: handoff only for settled cars
        public readonly CarBoxLease CarBox = new CarBoxLease();      // M4.3.2b: exclusive shared car storage
        public readonly CarBoxLease Locker = new CarBoxLease();      // W3: exclusive shared hideout locker (same lease rules, registry arg 1)
        public Func<byte[]> CarBoxSnapshot;                          // host plugin: encoded CarBoxItems of the host's real storage, null if unavailable
        public Func<byte[]> LockerSnapshot;                          // W3 host plugin: encoded CarBoxItems of the host's real locker counts
        public readonly WorldItemBook Items = new WorldItemBook();   // W3: shared dropped items (host-authoritative registry)
        public Func<byte, ItemTxnRequest, ProposalCode> ItemTxnHook; // W3 host plugin: Player-2 record / host journal side of an item transaction
        public readonly LootBook Loot = new LootBook();              // W4.1: taken pre-placed world loot (host-authoritative)
        public delegate ProposalCode LootTxnFn(byte slot, LootTxnRequest t, bool inRegion, out string why);
        public LootTxnFn LootTxnHook;                                // W4.1 host plugin: validation against the host catalog + durable Player-2 side
        public readonly RegionStateBook Objects = new RegionStateBook(); // W4.2/W4.2B: host-owned region epochs + door / lock / window states
        public Func<ObjectKind, int, uint, bool?> ObjectCatalog { get { return Objects.Catalog; } set { Objects.Catalog = value; } }   // host census check (null result = no census of that region)
        public Func<P2PosReport, ProposalCode> P2PosHook;            // W4.2 host plugin: Player-2 spawn store (p2spawn-<profile>.dat)
        public CarBoxLease Lease(byte kind) { return kind == StorageKind.Locker ? Locker : CarBox; }
        // M4.3.3: host plugin hooks (they touch the host's ItemID counters and the p2inv file); null = feature unavailable
        public Func<P2Inventory, ProposalCode> P2ReportHook;
        public Func<StorageTxnRequest, ProposalCode> StorageTxnHook;
        public delegate bool LiveParkFn(ushort id, out ParkRecord p);
        public LiveParkFn LivePark;   // M4.2.2: freshest pose/fuel known to the host plugin (own physics or last snapshot)

        double _now;   // time of the call in progress (TravelDesk.Finished carries no time)

        public WorldHost(uint epoch)
        {
            Reg = StateRegistry.CreateHost(epoch); Clocks = new RegionClockBook(Reg); Fleet = new FleetBook(Reg); Travel = new TravelDesk(Reg, Fleet);
            Joint = new JointTravel(Reg, Fleet, Travel.Notes);
            Travel.Finished = (slot, reqId, committed) => Joint.OnReservationEnded(slot, reqId, committed, _now, Outbox);
        }

        int RegionOfSlot(byte s) { return RegionDirectory.RegionOf(Reg, s); }

        void AfterFleetChange()
        {
            Fleet.ReassignHolders(RegionOfSlot, Outbox);
            ResetDoorsOfMovedCars();
        }

        // W4.2B.3: the host plugin placed / re-placed the fleet of a newly loaded save (FleetBook.Place resets Holder to 0).
        // The holder invariant must be re-established at once: when the host's region was already known before its cars were
        // registered (session attached in a running world, or a fast region probe), no later fleet change would assign the
        // host as holder, and the host's own (possibly occupied) car would be presented as another player's car. With the
        // region still unknown this changes nothing (the later SetPlayerRegion assigns it, the launcher-proven order).
        public int FleetInitialized(double now) { _now = now; int before = Outbox.Count; AfterFleetChange(); return Outbox.Count - before; }

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
        public TravelGrant HostTravel(uint id, TravelRequest req, double now) { string refused; return HostTravel(id, req, now, out refused); }

        // refused != null: nothing was decided or committed (the original menu stays open, like a refused client request).
        public TravelGrant HostTravel(uint id, TravelRequest req, double now, out string refused)
        {
            _now = now;
            bool joint;
            refused = TravelCheck(HostSlot, req, out joint);
            if (refused != null) { Joint.Refused++; return new TravelGrant { Id = id, Dest = req.Dest, Outcome = TravelOutcome.None }; }
            return DecideTravel(HostSlot, id, req, joint, now);
        }

        TravelGrant DecideTravel(byte slot, uint id, TravelRequest req, bool joint, double now)
        {
            int origin = RegionOfSlot(slot);
            byte passenger = 0;
            if (joint) { VehicleRecord r; if (Fleet.TryGet(req.Car, out r)) passenger = r.Passenger; }
            // The trip entry is committed BEFORE the car record moves to the destination: a client that applies the deltas
            // over two frames must never see "car moved, no trip" (it would treat its passenger seat as invalid and leave it).
            if (joint && passenger != 0) Joint.Begin(req.Car, slot, passenger, origin, req.Dest, id, now, Outbox);
            var g = Travel.Decide(slot, id, req, origin, RegionOfSlot, now, Outbox);
            if (joint && passenger != 0 && (g.Outcome != TravelOutcome.Carry || g.Car != req.Car))
            {
                LastNote = "joint travel expected Carry of car#" + req.Car + " but got " + g;   // TravelCheck mirrors Decide: not expected
                Joint.Abort("the decision did not carry the car (" + g.Outcome + ")", now, Outbox);
            }
            AfterFleetChange();
            return g;
        }

        // M4.3 Phase B (replaces the Phase A refusal): who may travel from a seat. A passenger never chooses the destination
        // (the companion seat in the original game has no travel action); a driver with a confirmed passenger travels
        // jointly when the car can be carried to the destination; otherwise the request is refused and nothing moves.
        public string TravelCheck(byte slot, TravelRequest req, out bool joint)
        {
            joint = false;
            if (Fleet.RiddenBy(slot) != VehicleIds.None) return "passenger: only the driver chooses the destination";
            if (Joint.Blocks(slot)) return "joint journey " + Joint.Current.Id + " in progress";
            var d = Fleet.DrivenBy(slot);
            VehicleRecord r;
            if (d == VehicleIds.None || !Fleet.TryGet(d, out r) || r.Passenger == 0) return null;   // solo travel: unchanged M4.2 rules
            if (r.PassengerPending) return "passenger still getting in";
            if (req.Car != d) return "request names car#" + req.Car + " but P" + slot + " drives car#" + d;
            int origin = RegionOfSlot(slot);
            if (origin == Regions.None || r.Region != origin || RegionOfSlot(r.Passenger) != origin) return "passenger not in the car's region";
            if (CarBox.Owner == slot || CarBox.Owner == r.Passenger) return "car storage open by P" + CarBox.Owner;
            if (!Travel.WouldCarry(slot, req, origin, RegionOfSlot)) return "car#" + d + " cannot be taken to " + Regions.Name(req.Dest) + " (destination car / special destination)";
            joint = true;
            return null;
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
            _now = now;
            Joint.OnDisconnect(slot, now, Outbox);
            Travel.Abort(slot, "P" + slot + " disconnected", Outbox);
            SetPlayerRegion(slot, Regions.None, now);
            Objects.OnDisconnect(slot, Reg, Outbox);                      // W4.2B: its scene instances are no longer part of the session
            AfterFleetChange();
        }

        public void Tick(double now)
        {
            _now = now;
            if (Joint.Active && Joint.Tick(now, Outbox)) AfterFleetChange();
            if (Travel.Pending > 0 && Travel.Expire(now, Outbox) > 0) AfterFleetChange();
            Fleet.ExpirePassengers(now, Outbox);
            if (CarBox.Expire(now)) CommitCarBox();
            if (Locker.Expire(now)) CommitLease(StorageKind.Locker);
            Objects.Tick(now, Reg, Outbox);                               // W4.2B: member grace periods, epoch end, reservation expiry
        }

        // ---- M4.3.2b shared car storage lease (host in place or client proposal "carboxReq") ----
        // A client grant first commits the host's current storage contents, then the lease; the proposal result follows both
        // (registry deltas precede results), so the client shows the host-authoritative contents in its original UI.
        public ProposalCode CarBoxRequest(byte slot, CarBoxOp op, uint session, double now, out string why) { return StorageRequest(StorageKind.Car, slot, op, session, now, out why); }

        // W3: the hideout locker uses the same exclusive lease and host-contents-before-grant rule; a client may open it only
        // while it is in the hideout region (the original locker exists only in PlayerHideout).
        public ProposalCode StorageRequest(byte kind, byte slot, CarBoxOp op, uint session, double now, out string why)
        {
            why = null;
            if (kind == StorageKind.Locker)
            {
                if (op == CarBoxOp.Open && slot != HostSlot)
                {
                    if (RegionOfSlot(slot) != 0) { why = "requester is not in the hideout"; return ProposalCode.NotAuthorized; }
                    if (Locker.Owner == 0)
                    {
                        var snap = LockerSnapshot != null ? LockerSnapshot() : null;
                        if (snap == null) { why = "host locker contents unavailable"; return ProposalCode.Busy; }
                        var ie = Reg.Commit(StateScope.Session, StorageKind.Locker, WorldKeys.CarBoxItems, snap);
                        if (ie != null) Outbox.Add(ie);
                    }
                }
                var lc = Locker.Request(slot, op, session, now);
                if (lc == ProposalCode.Accepted) CommitLease(StorageKind.Locker);
                if (lc == ProposalCode.Busy) why = "locker open by P" + Locker.Owner;
                return lc;
            }
            if (op == CarBoxOp.Open && Joint.Blocks(slot)) { why = "joint journey in progress"; return ProposalCode.Busy; }   // M4.3 Phase B
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
            if (Lease(t.Kind).Request(slot, CarBoxOp.Renew, t.Session, now) != ProposalCode.Unchanged) return ProposalCode.Stale;
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

        void CommitCarBox() { CommitLease(StorageKind.Car); }
        void CommitLease(byte kind)
        {
            var e = Reg.Commit(StateScope.Session, kind, WorldKeys.CarBox, Lease(kind).State.ToBytes());
            if (e != null) Outbox.Add(e);
        }

        // ---- W3 shared dropped items ----
        // One item transaction (host in place or client proposal "itemTxn"). The book decides validity (region, existence,
        // single winner); the plugin hook applies the owner's inventory side (Player-2 record written durably, host journal)
        // BEFORE the registry commit, so an accepted result means both sides are committed.
        public ProposalCode ItemTransaction(byte slot, ItemTxnRequest t, double now, out string why)
        {
            why = null;
            int region = RegionOfSlot(slot);
            var code = Items.Check(slot, region, t, out why);
            if (code != ProposalCode.Accepted) return code;
            if (t.Op != ItemOp.Settle)
            {
                var h = ItemTxnHook != null ? ItemTxnHook(slot, t) : ProposalCode.NotAuthorized;
                if (h != ProposalCode.Accepted) { why = "owner side " + h; return h; }
            }
            Items.Apply(slot, region, t, Reg, Outbox);
            return ProposalCode.Accepted;
        }

        // ---- W4.1 shared world loot ----
        // Player 2 picks up one verified pre-placed object. The plugin hook decides (LootDecision) and commits the Player-2 record +
        // durable world take BEFORE the registry commit; an accepted result means both are committed.
        public ProposalCode LootTransaction(byte slot, LootTxnRequest t, double now, out string why)
        {
            why = null;
            if (slot != 2) { why = "only Player 2 proposes (Player 1 = the original pickup on the host)"; return ProposalCode.NotAuthorized; }
            if (LootTxnHook == null) { why = "shared world loot unavailable on the host"; return ProposalCode.NotAuthorized; }
            var code = LootTxnHook(slot, t, RegionOfSlot(slot) == t.Ns, out why);
            if (code != ProposalCode.Accepted) return code;
            var c = Loot.Commit(t.Ns, t.Hash, Reg, Outbox);
            if (c == ProposalCode.Busy) why = "registry chunk full after the durable commit (should not happen: room checked first)";
            return ProposalCode.Accepted;
        }

        // ---- W4.2 region-independent object state (host in place for its own interactions, or client proposal "objTxn") ----
        public ProposalCode ObjectTransaction(byte slot, ObjectTxnRequest t, double now, out string why)
        {
            return Objects.Change(slot, RegionOfSlot(slot), t, now, Reg, Outbox, out why);
        }
        // W4.2B: scene instance of a region loaded by a player ("objScene") and the initializer's canonical initial state ("objInit").
        public ProposalCode ObjectScene(byte slot, ObjectSceneReport s, double now, out string why)
        {
            return Objects.OnScene(slot, RegionOfSlot(slot), s, now, Reg, Outbox, out why);
        }
        public ProposalCode ObjectInit(byte slot, ObjectInitChunk c, double now, out string why)
        {
            return Objects.Init(slot, c, now, Reg, Outbox, out why);
        }

        // ---- M4.3 passenger seat: op 1 = reserve, 2 = seated (confirm), 0 = leave. Host in place or client proposal.
        public ProposalCode Ride(byte slot, ushort id, byte op, double now, out string why)
        {
            why = null;
            switch (op)
            {
                case 1: return Fleet.ReservePassenger(id, slot, RegionOfSlot(slot), Motion.Stopped(id, now), now, Outbox, out why);
                case 2:
                {
                    var c = Fleet.ConfirmPassenger(id, slot, Outbox);
                    if (c == ProposalCode.NotAuthorized) why = "no reservation (expired or released)";
                    else if (Joint.Active && Joint.Current.Car == id && RegionOfSlot(slot) == Joint.Current.Dest) Joint.OnPassengerConfirmed(slot, now, Outbox);   // re-seated after a joint journey
                    return c;
                }
                case 0:
                    _now = now;
                    if (Joint.Active && Joint.Current.Car == id) Joint.OnPassengerReleased(slot, now, Outbox);
                    return Fleet.ReleasePassenger(id, slot, Outbox);
                default: why = "unknown op"; return ProposalCode.Invalid;
            }
        }

        // Phase A name kept for diagnostics/tests: non-null while the slot may not choose a destination from its seat (passenger).
        public string SeatTravelBlock(byte slot)
        {
            return Fleet.RiddenBy(slot) != VehicleIds.None ? "passenger: only the driver chooses the destination" : null;
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
            if (Locker.Release(slot)) CommitLease(StorageKind.Locker);    // W3: same for the hideout locker
            Items.ReleaseSim(slot, Reg, Outbox);                          // W3: its original item objects are gone with its scene
            if (exists && prev != region && prev != Regions.None && !RegionDirectory.Occupants(Reg).ContainsKey(prev))
            {
                Items.ClearRegion(prev, Reg, Outbox);                     // W3: region emptied = the original scene reload destroys its dropped items
                Loot.ClearRegion(prev, Reg, Outbox);                      // W4.1: the reload re-creates the loot; the original ItemDay_ keys decide
            }
            Objects.OnPlayerRegion(slot, region, now, Reg, Outbox);       // W4.2B: scene-lifetime objects end with the region EPOCH (member grace period, RegionStateBook)
            _now = now;
            Joint.OnRegion(slot, region, now, Outbox);                    // M4.3 Phase B: before any seat release (may end the trip)
            // disconnect / menu: no lease survives - except the joint passenger's seat during its scene load
            if (region == Regions.None) Fleet.ReleaseAll(slot, Outbox, Joint.KeepsSeat(slot) ? Joint.Current.Car : VehicleIds.None);
            else Fleet.ReleasePassengerOutside(slot, region, Outbox);     // M4.3: never seated in a car of another region (a joint trip car is already at the destination)
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
                else
                {
                    _now = now;
                    bool joint;
                    string refused = TravelCheck(senderSlot, req, out joint);
                    if (refused != null) { code = ProposalCode.Busy; Joint.Refused++; LastNote = "travel P" + senderSlot + " refused: " + refused; }
                    else { DecideTravel(senderSlot, id, req, joint, now); code = ProposalCode.Accepted; }
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
                if (!StorageKind.Valid(p.ScopeArg) || !CarBox_TryParse(p.Value, out op, out session)) code = ProposalCode.Invalid;
                else code = StorageRequest((byte)p.ScopeArg, senderSlot, op, session, now, out why);
                LastNote = StorageKind.Name(p.ScopeArg) + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.ItemTxn)
            {
                ItemTxnRequest t = default(ItemTxnRequest); string why = null;
                if (p.ScopeArg != 0 || !ItemTxnRequest.TryParse(p.Value, out t)) code = ProposalCode.Invalid;
                else code = ItemTransaction(senderSlot, t, now, out why);
                LastNote = "itemTxn " + t.Op + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.LootTxn)
            {
                LootTxnRequest t = default(LootTxnRequest); string why = null;
                if (p.ScopeArg != 0 || !LootTxnRequest.TryParse(p.Value, out t)) code = ProposalCode.Invalid;
                else code = LootTransaction(senderSlot, t, now, out why);
                LastNote = "lootTxn #" + t.Txn + " " + IdentityNamespace.Name(t.Ns) + ":" + t.Hash.ToString("X8") + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.ObjectTxn)
            {
                ObjectTxnRequest t = default(ObjectTxnRequest); string why = null;
                if (p.ScopeArg != 0 || !ObjectTxnRequest.TryParse(p.Value, out t)) code = ProposalCode.Invalid;
                else code = ObjectTransaction(senderSlot, t, now, out why);
                LastNote = "objTxn #" + t.Txn + " " + t + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Player && p.Key == WorldKeys.ObjectScene)
            {
                ObjectSceneReport s = default(ObjectSceneReport); string why = null;
                if (p.ScopeArg != senderSlot || !ObjectSceneReport.TryParse(p.Value, out s)) code = ProposalCode.Invalid;
                else code = ObjectScene(senderSlot, s, now, out why);
                LastNote = "objScene " + IdentityNamespace.Name(s.Ns) + " serial " + s.Serial + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Session && p.Key == WorldKeys.ObjectInit)
            {
                ObjectInitChunk c = null; string why = null;
                if (p.ScopeArg != 0 || !ObjectInitChunk.TryParse(p.Value, out c)) code = ProposalCode.Invalid;
                else code = ObjectInit(senderSlot, c, now, out why);
                LastNote = "objInit " + (c != null ? IdentityNamespace.Name(c.Ns) + " epoch " + c.Epoch : "?") + " by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Player && p.Key == WorldKeys.P2Pos)
            {
                P2PosReport r; string why = null;
                if (!P2PosReport.TryParse(p.Value, out r)) code = ProposalCode.Invalid;
                else if ((why = SpawnRules.CheckReport(senderSlot, p.ScopeArg, RegionOfSlot(senderSlot), r)) != null) code = ProposalCode.NotAuthorized;
                else code = P2PosHook != null ? P2PosHook(r) : ProposalCode.NotAuthorized;
                LastNote = code == ProposalCode.Accepted ? null : "p2pos by P" + senderSlot + " -> " + code + (why != null ? " (" + why + ")" : "");
            }
            else if (p.Scope == StateScope.Player && p.Key == WorldKeys.TravelCancel)
            {
                if (p.ScopeArg != senderSlot) code = ProposalCode.NotAuthorized;
                else if (p.Value == null || p.Value.Length != 4) code = ProposalCode.Invalid;
                else { _now = now; code = Travel.Cancel(senderSlot, BitConverter.ToUInt32(p.Value, 0), Outbox); AfterFleetChange(); }
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
