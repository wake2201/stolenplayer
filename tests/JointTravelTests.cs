// M4.3 Phase B offline tests: host-coordinated joint vehicle travel (driver + seated passenger in one car).
// Host side = the real WorldHost (TravelDesk reservation + JointTravel trip + FleetBook seats + CarBox lease + clocks);
// client side = the pure passenger decisions (JointRules.Passenger / ReadyToSeat) the plugin runs each frame, fed with
// registry entries relayed through the real StateMessages codecs. Unity scene loading, cameras, the original Select*/
// IsStartingCar methods and rendering are NOT exercised here - those remain manual QA (docs/TESTING.md, M4.3 Phase B).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class JointTravelTests
{
    static int _passed, _failed;
    static uint _pid;
    const int Hideout = 0, Verwood = 1, Hillville = 97, Museum = 96, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);

    static int Main()
    {
        Run("01 P1 drives, P2 rides: one joint trip, both arrive, seats restored", HostDrives);
        Run("02 P2 drives, P1 rides: one joint trip, P2 keeps the physics authority", ClientDrives);
        Run("03 Ranger passenger anchor (rear-bench associate seat) restored at the destination", RangerAnchor);
        Run("04 Shadowhawk passenger anchor (Associate Sit) restored; full trip with the Shadowhawk", ShadowhawkTrip);
        Run("05 host-authoritative trip: only the driver's travel decision creates it; clients cannot write it; codec", HostAuthority);
        Run("06 source / destination validation: same region, special, occupied destination, pending seat, regions", Validation);
        Run("07 duplicate requests: retransmission cached, second request and passenger request refused", Duplicates);
        Run("08 stale transactions: terminal phases never reopen, newer trip ids, old ids ignored by the passenger", Stale);
        Run("09 passenger participates without a second travel command (Depart once per trip id)", NoSecondCommand);
        Run("10 driver arrives before the passenger", DriverFirst);
        Run("11 passenger arrives before the driver (car still reserved: not ready)", PassengerFirst);
        Run("12 seat restoration only when both are ready; confirm order independent", SeatAfterBoth);
        Run("13 vehicle identity preserved: same id/kind, never two cars in a one-car region", Identity);
        Run("14 driver physics authority preserved (holder = driver, never the host for a client driver)", Authority);
        Run("15 no duplicate simulation: only the holder may stream the car at the destination", NoDuplicateSim);
        Run("16 fuel and vehicle state: final pose/fuel recorded, engine flag cleared, park record kept", FuelState);
        Run("17 passenger camera mode after arrival comes from the current record, not an old event", CameraMode);
        Run("18 Player-2 inventory: trip never touches p2inv; exit capture + restore in the new scene", Inventory);
        Run("19 CarBox before departure: open storage blocks the trip; transfers after departure are stale", CarBoxBefore);
        Run("20 CarBox lock cleanup: lease released on departure, no new session during the trip", CarBoxLock);
        Run("21 passenger disconnects during travel: seat released, driver continues alone", PassengerDisconnect);
        Run("22 driver disconnects during travel: no second driver, no promotion, car identity/location kept", DriverDisconnect);
        Run("23 loading timeout and arrival timeout end the trip and free the seat", Timeouts);
        Run("24 unexpected scene: wrong destination for passenger or driver", WrongScene);
        Run("25 reconnection recovery: no stale seat, normal ride possible again", Reconnect);
        Run("26 passenger seat reservation cleanup in every terminal path", SeatCleanup);
        Run("27 regression: driver alone carries the car (no joint trip)", SoloCarry);
        Run("28 regression: on-foot travel (no car, Join/None outcomes)", OnFoot);
        Run("29 regression: players in different regions travel independently", DifferentRegions);
        Run("30 regression: region clocks and sleep across a joint trip (no replay, one clock per region)", Clocks);
        Run("31 trip entry replicated through StateMessages; passenger decisions from the client registry", Replication);
        Run("32 non-seating destination (hideout): both arrive on foot like the original, seat released at commit", HideoutArrival);
        Run("33 cost: decisions per frame, zero allocations, registry traffic per trip", Cost);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }

    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- fixtures
    // both players in 'region' with 'car' there (the other car locked in the hideout unless gtUnlocked)
    static WorldHost Both(int region, ushort car = 0, bool gtUnlocked = false)
    {
        if (car == 0) car = Ranger;
        var wh = new WorldHost(7) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, region, 0);
        wh.SetPlayerRegion(2, region, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);
        wh.Fleet.Register(GT, 1, car == GT || gtUnlocked, wh.Outbox);
        ushort other = car == Ranger ? GT : Ranger;
        wh.Fleet.Place(car, region, wh.Outbox);
        wh.Fleet.Place(other, Hideout, wh.Outbox);
        wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox);
        wh.Outbox.Clear();
        return wh;
    }

    static ProposalCode Prop(WorldHost wh, byte slot, string key, ushort id, byte v, double now, uint pid = 0)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = id, Key = key, Value = new[] { v } };
        return wh.HandleProposal("cli", slot, pid != 0 ? pid : ++_pid, p, now, out rev, out sr, out sh);
    }

    static ProposalCode Ride(WorldHost wh, byte slot, ushort id, byte op, double now)
    {
        if (slot == 2) return Prop(wh, 2, WorldKeys.VehicleRide, id, op, now);
        string why; return wh.Ride(1, id, op, now, out why);
    }

    static ProposalCode Claim(WorldHost wh, byte slot, ushort id, double now)
    {
        if (slot == 2) return Prop(wh, 2, WorldKeys.VehicleClaim, id, 1, now);
        return wh.Fleet.Claim(id, 1, RegionDirectory.RegionOf(wh.Reg, 1), wh.Outbox);
    }

    // driver seated with the engine on, passenger seated (confirmed)
    static WorldHost Pair(int region, byte driver, ushort car = 0, bool gtUnlocked = false)
    {
        if (car == 0) car = Ranger;
        var wh = Both(region, car, gtUnlocked);
        if (driver == 2) Expect(Prop(wh, 2, WorldKeys.VehicleTake, car, 1, 0.5) == ProposalCode.Accepted, "client takes the car");
        Expect(Claim(wh, driver, car, 0.6) == ProposalCode.Accepted, "driver lease");
        byte passenger = (byte)(driver == 1 ? 2 : 1);
        Expect(Ride(wh, passenger, car, 1, 1) == ProposalCode.Accepted, "passenger reserve");
        Expect(Ride(wh, passenger, car, 2, 1.1) == ProposalCode.Accepted, "passenger confirm");
        wh.Outbox.Clear();
        return wh;
    }

    static TravelRequest Req(int dest, ushort car, int origin, float fuel = 42f)
    {
        return new TravelRequest { Dest = dest, Car = car, Pose = new ParkRecord { Region = origin, HasPose = true, Pos = new Vec3(10, 1, 20), Rot = Quat.Identity, Fuel = fuel } };
    }

    // the driver's travel click (host in place or client proposal); true = accepted
    static bool Travel(WorldHost wh, byte driver, int dest, ushort car, double now, out string why, uint pid = 0)
    {
        int origin = RegionDirectory.RegionOf(wh.Reg, driver);
        why = null;
        if (driver == 1) { wh.HostTravel(++_pid, Req(dest, car, origin), now, out why); return why == null; }
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = Req(dest, car, origin).ToBytes() };
        var code = wh.HandleProposal("cli", 2, pid != 0 ? pid : ++_pid, p, now, out rev, out sr, out sh);
        if (code != ProposalCode.Accepted) why = code + ": " + wh.LastNote;
        return code == ProposalCode.Accepted;
    }

    static bool Travel(WorldHost wh, byte driver, int dest, ushort car, double now) { string why; return Travel(wh, driver, dest, car, now, out why); }

    static VehicleRecord R(WorldHost wh, ushort id) { VehicleRecord r; Expect(wh.Fleet.TryGet(id, out r), "record " + id); return r; }

    static JointTrip Trip(StateRegistry reg)
    {
        var e = reg.Get(StateScope.Session, 0, WorldKeys.JointTrip);
        JointTrip t = default(JointTrip); Expect(e != null && JointTrip.TryParse(e.Value, out t), "trip entry"); return t;
    }

    static bool HasTrip(StateRegistry reg) { return reg.Get(StateScope.Session, 0, WorldKeys.JointTrip) != null; }

    static void Leave(WorldHost wh, byte slot, double now) { wh.SetPlayerRegion(slot, Regions.None, now); }
    static void Arrive(WorldHost wh, byte slot, int region, double now) { wh.SetPlayerRegion(slot, region, now); }

    // global invariants after every step
    static void Invariants(WorldHost wh)
    {
        var oneCar = new HashSet<int>();
        foreach (var id in wh.Fleet.Ids())
        {
            var r = R(wh, id);
            Expect(r.Driver == 0 || r.Driver != r.Passenger, "driver != passenger: " + r);
            Expect(r.Driver == 0 || r.Driver == r.Holder, "a driver is always the holder: " + r);
            if (TravelRules.OneCarRegion(r.Region)) Expect(oneCar.Add(r.Region), "one car per one-car region");
            if (r.Passenger != 0 && !wh.Joint.KeepsSeat(r.Passenger))
                Expect(RegionDirectory.RegionOf(wh.Reg, r.Passenger) == r.Region, "no passenger seat in a region the passenger is not in (outside a joint load): " + r);
        }
        int drivers1 = 0, drivers2 = 0;
        foreach (var id in wh.Fleet.Ids()) { var r = R(wh, id); if (r.Driver == 1) drivers1++; if (r.Driver == 2) drivers2++; }
        Expect(drivers1 <= 1 && drivers2 <= 1, "one car per driver");
    }

    // full happy path of one trip; returns the final record
    static VehicleRecord FullTrip(WorldHost wh, byte driver, ushort car, int dest, double t0)
    {
        byte passenger = (byte)(driver == 1 ? 2 : 1);
        Expect(Travel(wh, driver, dest, car, t0), "travel accepted");
        Invariants(wh);
        Leave(wh, driver, t0 + 0.4); Invariants(wh);
        Expect(Trip(wh.Reg).Phase == JointPhase.Travelling, "travelling after the driver left");
        Leave(wh, passenger, t0 + 1.0); Invariants(wh);
        Expect(R(wh, car).Passenger == passenger, "seat kept while the passenger loads");
        Arrive(wh, driver, dest, t0 + 5); Invariants(wh);
        Expect(Claim(wh, driver, car, t0 + 5.1) == ProposalCode.Accepted, "driver re-claims its lease (IsStartingCar seated it)");
        Arrive(wh, passenger, dest, t0 + 8); Invariants(wh);
        Expect(Trip(wh.Reg).Phase == JointPhase.Arrived, "arrived: " + Trip(wh.Reg));
        Expect(Ride(wh, passenger, car, 2, t0 + 8.5) == ProposalCode.Unchanged, "re-seat confirm (seat was kept, no new reservation)");
        Expect(Trip(wh.Reg).Phase == JointPhase.Committed, "committed: " + Trip(wh.Reg));
        Invariants(wh);
        return R(wh, car);
    }

    // ---------------------------------------------------------------- tests
    static void HostDrives()
    {
        var wh = Pair(Madison, 1);
        var r = FullTrip(wh, 1, Ranger, Hillville, 10);
        Expect(r.Region == Hillville && r.Driver == 1 && r.Passenger == 2 && r.Holder == 1 && !r.Reserved, "P1 driver, P2 passenger, in Hillville: " + r);
        Expect(wh.Joint.Started == 1 && wh.Joint.Committed == 1 && wh.Travel.Decisions == 1, "one decision, one trip");
        Expect(wh.Travel.Pending == 0 && !wh.Joint.Active, "nothing left pending");
        Expect(Travel(wh, 1, Madison, Ranger, 30), "they can continue travelling together at once");
        Expect(wh.Joint.Active && Trip(wh.Reg).Id == 2, "second joint trip");
    }

    static void ClientDrives()
    {
        var wh = Pair(Madison, 2);
        var t = default(JointTrip);
        Expect(Travel(wh, 2, Hillville, Ranger, 10), "client driver's request accepted");
        t = Trip(wh.Reg);
        Expect(t.Driver == 2 && t.Passenger == 1 && t.Origin == Madison && t.Dest == Hillville && t.Phase == JointPhase.Authorized, "trip " + t);
        Expect(R(wh, Ranger).Holder == 2, "reserved for the client driver (holder P2), not the coordinating host");
        Leave(wh, 2, 10.5); Leave(wh, 1, 11);
        Arrive(wh, 1, Hillville, 13);                                   // the host loads faster
        Expect(R(wh, Ranger).Holder == 2 && R(wh, Ranger).Reserved, "host arrived first: still reserved for P2, the host never simulates it");
        Arrive(wh, 2, Hillville, 18); Claim(wh, 2, Ranger, 18.1);
        Expect(Ride(wh, 1, Ranger, 2, 19) == ProposalCode.Unchanged, "host re-seated");
        var r = R(wh, Ranger);
        Expect(r.Driver == 2 && r.Holder == 2 && r.Passenger == 1 && Trip(wh.Reg).Phase == JointPhase.Committed, "P2 drives and simulates, P1 rides: " + r);
        Invariants(wh);
    }

    static void RangerAnchor()
    {
        // real SEAT_PROBE values (car-local): Ranger associateSitPos (-0.01,-0.40,-0.23) = rear bench centre; sit (-0.56,0.19,0.51)
        var seat = SeatRules.Passenger(true, new Vec3(-0.01f, -0.40f, -0.23f), Quat.Identity);
        Expect(seat.Valid && seat.Source == SeatSource.AssociateSeat && seat.CodenameRoot && Math.Abs(seat.Pos.Z + 0.23f) < 1e-4, "the Ranger's own associate seat: " + seat);
        Expect(!SeatRules.Passenger(false, new Vec3(), Quat.Identity).Valid, "a car without that anchor has no passenger seat");
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1); Leave(wh, 1, 1.2); Leave(wh, 2, 1.5); Arrive(wh, 1, Hillville, 4); Claim(wh, 1, Ranger, 4.1); Arrive(wh, 2, Hillville, 6);
        var t = Trip(wh.Reg); var r = R(wh, Ranger);
        Expect(!JointRules.ReadyToSeat(r, t, 2, Hillville, true, false, true), "no anchor (seat probe failed) -> never seated (timeout -> on foot)");
        Expect(JointRules.ReadyToSeat(r, t, 2, Hillville, true, true, true), "anchor present -> ready");
    }

    static void ShadowhawkTrip()
    {
        var seat = SeatRules.Passenger(true, new Vec3(0.44f, -0.78f, 0.09f), Quat.Identity);   // Shadowhawk "Associate Sit" (front passenger)
        Expect(seat.Valid && Math.Abs(seat.Pos.X - 0.44f) < 1e-4, "Shadowhawk associate seat: " + seat);
        var wh = Pair(Hillville, 2, GT);
        var r = FullTrip(wh, 2, GT, Madison, 5);
        Expect(r.Kind == 1 && r.Region == Madison && r.Driver == 2 && r.Passenger == 1, "Shadowhawk in Madison with P2 driving, P1 at Associate Sit: " + r);
        Expect(R(wh, Ranger).Region == Hideout && R(wh, Ranger).Passenger == 0 && R(wh, Ranger).Driver == 0, "the Ranger untouched");
    }

    static void HostAuthority()
    {
        var wh = Pair(Madison, 1);
        uint rev; int sr; float sh;
        var forged = new JointTrip { Id = 99, Car = Ranger, Driver = 1, Passenger = 2, Origin = Madison, Dest = Hillville, Phase = JointPhase.Authorized };
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.JointTrip, Value = forged.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, ++_pid, p, 1, out rev, out sr, out sh) == ProposalCode.NotAuthorized && !HasTrip(wh.Reg), "a client cannot create or edit a trip");
        Expect(Travel(wh, 1, Hillville, Ranger, 2) && HasTrip(wh.Reg), "only the driver's travel decision creates it");
        var t = Trip(wh.Reg);
        JointTrip back; Expect(JointTrip.TryParse(t.ToBytes(), out back) && back.Id == t.Id && back.Dest == Hillville && t.ToBytes().Length == JointTrip.WireSize, "18-byte codec round trip");
        var bad = t; bad.Driver = bad.Passenger; Expect(!JointTrip.TryParse(bad.ToBytes(), out back), "driver == passenger rejected");
        bad = t; bad.Dest = Museum; Expect(!JointTrip.TryParse(bad.ToBytes(), out back), "non-fleet destination rejected");
        bad = t; bad.Dest = bad.Origin; Expect(!JointTrip.TryParse(bad.ToBytes(), out back), "origin == destination rejected");
        bad = t; bad.Id = 0; Expect(!JointTrip.TryParse(bad.ToBytes(), out back), "id 0 rejected");
        bad = t; bad.Progress = (JointProgress)0x40; Expect(!JointTrip.TryParse(bad.ToBytes(), out back), "unknown progress bits rejected");
        Expect(!JointTrip.TryParse(new byte[17], out back), "short value rejected");
    }

    static void Validation()
    {
        string why;
        var wh = Pair(Madison, 1);
        Expect(!Travel(wh, 1, Madison, Ranger, 1, out why) && !HasTrip(wh.Reg), "same region refused: " + why);
        Expect(!Travel(wh, 1, Museum, Ranger, 1, out why) && R(wh, Ranger).Region == Madison && !R(wh, Ranger).Reserved, "special destination refused, nothing moved: " + why);
        var w2 = Pair(Madison, 1, Ranger, true);
        w2.Fleet.Place(GT, Hillville, w2.Outbox);                       // the Shadowhawk parked in Hillville (one-car region)
        Expect(!Travel(w2, 1, Hillville, Ranger, 1, out why) && R(w2, Ranger).Region == Madison && R(w2, GT).Region == Hillville, "occupied destination refused (no overlap, no swap): " + why);
        var w3 = Both(Madison); Claim(w3, 1, Ranger, 0.5); Ride(w3, 2, Ranger, 1, 1);   // reserved, not yet seated
        Expect(!Travel(w3, 1, Hillville, Ranger, 1.2, out why) && !HasTrip(w3.Reg), "passenger still getting in refused: " + why);
        var w4 = Pair(Madison, 1);
        Expect(!Travel(w4, 1, Hillville, GT, 1, out why), "request naming another car refused: " + why);
        Expect(Travel(w4, 1, Verwood, Ranger, 2) && Trip(w4.Reg).Dest == Verwood, "Verwood (fleet region) accepted");
        var w5 = Pair(Madison, 2);
        Expect(!Travel(w5, 1, Hillville, Ranger, 1, out why) && !HasTrip(w5.Reg), "the host as passenger cannot choose a destination: " + why);
    }

    static void Duplicates()
    {
        var wh = Pair(Madison, 2);
        string why;
        Expect(Travel(wh, 2, Hillville, Ranger, 1, out why, 5000), "first");
        Expect(Travel(wh, 2, Hillville, Ranger, 1.1, out why, 5000), "retransmission returns the cached result");
        Expect(wh.Joint.Started == 1 && wh.Travel.Decisions == 1, "applied once");
        Expect(!Travel(wh, 2, Verwood, Ranger, 1.2, out why) && Trip(wh.Reg).Dest == Hillville, "a second request during the trip refused: " + why);
        Expect(!Travel(wh, 1, Verwood, Ranger, 1.3, out why), "the passenger's request refused: " + why);
        Expect(wh.Joint.Started == 1 && Trip(wh.Reg).Phase == JointPhase.Authorized, "still the same trip");
    }

    static void Stale()
    {
        var wh = Pair(Madison, 1);
        FullTrip(wh, 1, Ranger, Hillville, 1);
        var done = Trip(wh.Reg);
        Leave(wh, 2, 20); Arrive(wh, 2, Madison, 21);                    // late/odd region events after the commit
        Expect(Trip(wh.Reg).Phase == JointPhase.Committed && Trip(wh.Reg).Id == done.Id, "a terminal trip never reopens");
        Expect(R(wh, Ranger).Passenger == 0, "passenger left the car's region afterwards: normal seat rule released it");
        // passenger side: an old trip id is never acted on
        var old = done; var cur = done; cur.Id = done.Id + 1; cur.Phase = JointPhase.Travelling; cur.Progress = JointProgress.DriverLeft;
        Expect(JointRules.Passenger(true, old, 2, Madison, true, false, false, false) == JointStep.None, "terminal old trip -> nothing");
        Expect(JointRules.Passenger(false, cur, 2, Madison, true, true, false, false) == JointStep.Recover, "departed for an id that is no longer current -> forward recovery, never a second travel");
    }

    static void NoSecondCommand()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1);
        var t = Trip(wh.Reg);
        Expect(JointRules.Passenger(true, t, 2, Madison, true, false, false, false) == JointStep.Hold, "authorized: hold (driver still choosing/saving)");
        Leave(wh, 1, 1.4);
        t = Trip(wh.Reg);
        // the plugin loop: Depart once, remember the id
        uint lastDeparted = 0; int departs = 0;
        for (int frame = 0; frame < 5; frame++)
        {
            bool departed = lastDeparted == t.Id;
            var step = JointRules.Passenger(true, t, 2, Madison, !departed, departed, false, false);
            if (step == JointStep.Depart && t.Id != lastDeparted) { departs++; lastDeparted = t.Id; }
        }
        Expect(departs == 1, "exactly one original Select on the passenger instance (" + departs + ")");
        Expect(wh.Travel.Decisions == 1, "no second travel request was needed or made");
        Expect(JointRules.Passenger(true, t, 2, Madison, false, false, false, false) == JointStep.None, "not seated (exit raced the decision) -> no departure");
    }

    static void DriverFirst()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1); Leave(wh, 1, 1.4); Leave(wh, 2, 2);
        Arrive(wh, 1, Hillville, 4); Claim(wh, 1, Ranger, 4.05);
        var t = Trip(wh.Reg); var r = R(wh, Ranger);
        Expect(t.Phase == JointPhase.Travelling && t.Has(JointProgress.DriverArrived) && !t.Has(JointProgress.PassengerArrived), "driver there, passenger loading: " + t);
        Expect(JointRules.Passenger(true, t, 2, Regions.None, false, true, false, false) == JointStep.Wait, "passenger loading -> wait");
        Arrive(wh, 2, Hillville, 9);
        t = Trip(wh.Reg); r = R(wh, Ranger);
        Expect(JointRules.Passenger(true, t, 2, Hillville, false, true, JointRules.ReadyToSeat(r, t, 2, Hillville, true, true, false), false) == JointStep.Wait, "no pose received after arrival yet -> wait");
        Expect(JointRules.Passenger(true, t, 2, Hillville, false, true, JointRules.ReadyToSeat(r, t, 2, Hillville, true, true, true), false) == JointStep.Seat, "fresh pose -> seat");
    }

    static void PassengerFirst()
    {
        var wh = Pair(Madison, 2);
        Travel(wh, 2, Hillville, Ranger, 1); Leave(wh, 2, 1.4); Leave(wh, 1, 2);
        Arrive(wh, 1, Hillville, 3);                                     // host passenger loaded in 1 s
        var t = Trip(wh.Reg); var r = R(wh, Ranger);
        Expect(r.Reserved && r.Passenger == 1 && r.Holder == 2, "car reserved for the driver, seat kept: " + r);
        Expect(!JointRules.ReadyToSeat(r, t, 1, Hillville, true, true, true), "reserved / no driver -> not ready");
        Arrive(wh, 2, Hillville, 11);                                    // driver loaded in 9.6 s
        r = R(wh, Ranger);
        Expect(!JointRules.ReadyToSeat(r, t, 1, Hillville, true, true, true), "driver arrived but not seated yet -> not ready");
        Claim(wh, 2, Ranger, 11.2);
        r = R(wh, Ranger); t = Trip(wh.Reg);
        Expect(JointRules.ReadyToSeat(r, t, 1, Hillville, true, true, true), "driver seated and simulating -> ready");
        Expect(Ride(wh, 1, Ranger, 2, 11.5) == ProposalCode.Unchanged && Trip(wh.Reg).Phase == JointPhase.Committed, "committed");
    }

    static void SeatAfterBoth()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1); Leave(wh, 1, 1.4); Leave(wh, 2, 2);
        Expect(Ride(wh, 2, Ranger, 2, 2.5) == ProposalCode.Unchanged && !Trip(wh.Reg).Has(JointProgress.PassengerSeated), "a confirm before the passenger arrived does not count");
        Arrive(wh, 2, Hillville, 3);
        Ride(wh, 2, Ranger, 2, 3.2);
        Expect(Trip(wh.Reg).Phase == JointPhase.Travelling && Trip(wh.Reg).Has(JointProgress.PassengerSeated), "seated, driver still loading: not committed");
        Arrive(wh, 1, Hillville, 6);
        Expect(Trip(wh.Reg).Phase == JointPhase.Committed, "commit once both are there");
    }

    static void Identity()
    {
        var wh = Pair(Madison, 1, Ranger, true);
        var ids = wh.Fleet.Ids();
        FullTrip(wh, 1, Ranger, Hillville, 1);
        Expect(wh.Fleet.Ids().Count == ids.Count, "no vehicle created");
        Expect(R(wh, Ranger).Kind == 0 && wh.Fleet.VehiclesIn(Hillville).Count == 1 && wh.Fleet.VehiclesIn(Madison).Count == 0, "the same car moved, the origin is empty");
        Expect(R(wh, GT).Region == Hideout, "the other permanent car untouched");
    }

    static void Authority()
    {
        var wh = Pair(Madison, 2);
        Travel(wh, 2, Hillville, Ranger, 1);
        string why;
        Expect(wh.TakeOver(1, Ranger, 1.1, out why) != ProposalCode.Accepted, "host cannot take the car during the trip: " + why);
        Leave(wh, 2, 1.4); Leave(wh, 1, 1.6); Arrive(wh, 1, Hillville, 3);
        Expect(R(wh, Ranger).Holder == 2, "host arrived first: holder stays P2");
        Arrive(wh, 2, Hillville, 5); Claim(wh, 2, Ranger, 5.1); Ride(wh, 1, Ranger, 2, 5.5);
        Expect(R(wh, Ranger).Holder == 2 && R(wh, Ranger).Driver == 2, "P2 simulates and drives after arrival");
        Expect(wh.TakeOver(1, Ranger, 30, out why) != ProposalCode.Accepted, "the seated passenger is never an implicit driver: " + why);
    }

    static void NoDuplicateSim()
    {
        var wh = Pair(Madison, 2);
        var r = FullTrip(wh, 2, Ranger, Hillville, 1);
        Expect(FleetView.SenderSimulates(r, 2, Hillville) && !FleetView.SenderSimulates(r, 1, Hillville), "only the holder (P2) may stream");
        Expect(!FleetView.SenderSimulates(r, 2, Madison), "no stream for the origin");
        Expect(PresentationRules.Decide(r, Hillville, 1) == CarPresentation.RemoteVisual && PresentationRules.Decide(r, Hillville, 2) == CarPresentation.Real, "observed on P1, simulated on P2");
    }

    static void FuelState()
    {
        var wh = Pair(Madison, 1);
        wh.Fleet.SetEngine(Ranger, true, wh.Outbox);
        Travel(wh, 1, Hillville, Ranger, 1);
        ParkRecord p; Expect(wh.Fleet.TryGetPark(Ranger, out p) && Math.Abs(p.Fuel - 42f) < 1e-3 && p.Region == Madison && p.HasPose, "final pose + fuel recorded at the click: " + p);
        Expect((R(wh, Ranger).Flags & VehicleFlags.EngineOn) == 0, "engine flag cleared for the reload (original arrival starts it)");
        Leave(wh, 1, 1.4); Leave(wh, 2, 2); Arrive(wh, 1, Hillville, 4); Claim(wh, 1, Ranger, 4.1); Arrive(wh, 2, Hillville, 5); Ride(wh, 2, Ranger, 2, 5.2);
        Expect(wh.Fleet.TryGetPark(Ranger, out p) && Math.Abs(p.Fuel - 42f) < 1e-3, "fuel kept in the park record for the destination");
    }

    static void CameraMode()
    {
        var wh = Pair(Madison, 1);
        wh.Fleet.SetTpp(Ranger, true, wh.Outbox);                       // TPP before the trip
        FullTrip(wh, 1, Ranger, Hillville, 1);
        // after arrival the holder reports its CURRENT camera (state on every snapshot / host stream), never a replayed event
        wh.Fleet.SetTpp(Ranger, false, wh.Outbox);
        Expect(!R(wh, Ranger).Tpp, "first-person after arrival follows the driver's current camera");
        wh.Fleet.SetTpp(Ranger, true, wh.Outbox);
        Expect(R(wh, Ranger).Tpp, "mode changes keep working");
        // the re-seated passenger starts in its own first-person seat camera and switches to the record mode (plugin RideFrame)
        var r = R(wh, Ranger); bool seatTpp = false;
        bool want = r.Tpp && r.Driver != 0;
        Expect(want != seatTpp, "a fresh seat with a TPP driver switches to the original TPP vehicle camera on the next frame");
    }

    static void Inventory()
    {
        var wh = Pair(Madison, 1);
        var rec = P2Inventory.Initial(false); rec.Id = 7; rec.Set(31, 1);
        wh.CommitP2(rec);
        var p2rev = wh.Reg.Get(StateScope.Session, 2, WorldKeys.P2Inv).Rev;
        FullTrip(wh, 1, Ranger, Hillville, 1);
        Expect(wh.Reg.Get(StateScope.Session, 2, WorldKeys.P2Inv).Rev == p2rev, "the joint trip never writes the Player-2 record");
        // client lifecycle through the scene change (real P2ClientSync): restored, acknowledged, exit capture, restored again
        var sync = new P2ClientSync();
        P2Inventory local = null; uint sent = 0, nextId = 1;
        sync.Apply = x => { local = x.Clone(); return true; };
        sync.Capture = () => local != null ? local.Clone() : null;
        sync.Send = x => { sent = nextId++; return sent; };
        sync.Persist = (a, b) => { };
        var hostRec = rec.Clone(); hostRec.Rev = 3;
        sync.Tick(0, true, hostRec, false, true, false);
        Expect(sync.OnResult(sent, ProposalCode.Unchanged, hostRec), "acknowledged");
        Expect(sync.Ready && sync.Restores == 1, "ready in the origin");
        sync.CaptureExit(1, true, hostRec);                               // SaveGameNewSystem inside the passenger's original Select
        sync.SceneLeft();                                                // MainScene reload
        local = null;
        sync.Tick(9, true, hostRec, false, true, false);                  // destination scene ready
        sync.OnResult(sent, ProposalCode.Unchanged, hostRec);
        Expect(sync.Ready && sync.Restores == 2 && local != null && local.CountOf(31) == 1, "restored from the host record in the destination (item kept, not reseeded)");
    }

    static void CarBoxBefore()
    {
        var wh = Pair(Madison, 1);
        string why;
        wh.CarBoxSnapshot = () => new byte[] { 0, 0 };
        Expect(wh.CarBoxRequest(2, CarBoxOp.Open, 11, 0.6, out why) == ProposalCode.Accepted, "passenger opened the shared storage: " + why);
        Expect(!Travel(wh, 1, Hillville, Ranger, 1, out why) && !HasTrip(wh.Reg) && R(wh, Ranger).Region == Madison, "open storage blocks the joint trip: " + why);
        uint session = wh.CarBox.Session;
        Expect(wh.CarBoxRequest(2, CarBoxOp.Close, session, 1.5, out why) == ProposalCode.Accepted, "closed");
        Expect(Travel(wh, 1, Hillville, Ranger, 2), "after closing: accepted");
        Leave(wh, 2, 3);
        Expect(wh.StorageTransfer(2, new StorageTxnRequest { Session = session, Txn = 1 }, 3.1) == ProposalCode.Stale, "a late transfer of the old session is stale (never applied)");
    }

    static void CarBoxLock()
    {
        var wh = Pair(Madison, 1);
        wh.CarBoxSnapshot = () => new byte[] { 0, 0 };
        string why;
        Travel(wh, 1, Hillville, Ranger, 1);
        Expect(wh.CarBoxRequest(2, CarBoxOp.Open, 12, 1.1, out why) == ProposalCode.Busy && wh.CarBox.Owner == 0, "no new session during the trip (passenger): " + why);
        Expect(wh.CarBoxRequest(1, CarBoxOp.Open, 13, 1.2, out why) == ProposalCode.Busy, "no new session during the trip (driver)");
        Leave(wh, 1, 1.4); Leave(wh, 2, 2); Arrive(wh, 1, Hillville, 4); Claim(wh, 1, Ranger, 4.1); Arrive(wh, 2, Hillville, 5); Ride(wh, 2, Ranger, 2, 5.2);
        Expect(wh.CarBoxRequest(2, CarBoxOp.Open, 14, 6, out why) == ProposalCode.Accepted, "storage usable again after the commit: " + why);
    }

    static void PassengerDisconnect()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1); Leave(wh, 1, 1.4); Leave(wh, 2, 2);
        wh.PlayerDisconnected(2, 3);
        Expect(Trip(wh.Reg).Phase == JointPhase.Failed && R(wh, Ranger).Passenger == 0, "trip failed, seat released: " + Trip(wh.Reg));
        Expect(wh.Travel.HasReservation(1), "driver's own travel continues");
        Arrive(wh, 1, Hillville, 5); Claim(wh, 1, Ranger, 5.1);
        var r = R(wh, Ranger);
        Expect(r.Region == Hillville && r.Driver == 1 && r.Holder == 1 && r.Passenger == 0, "driver arrives and drives alone, one car: " + r);
        Invariants(wh);
        var w2 = Pair(Madison, 1); Travel(w2, 1, Hillville, Ranger, 1);
        w2.PlayerDisconnected(2, 1.2);
        Expect(Trip(w2.Reg).Phase == JointPhase.Cancelled && R(w2, Ranger).Passenger == 0, "disconnect before departure: cancelled");
    }

    static void DriverDisconnect()
    {
        var wh = Pair(Madison, 2);
        Travel(wh, 2, Hillville, Ranger, 1); Leave(wh, 2, 1.4); Leave(wh, 1, 2);
        wh.PlayerDisconnected(2, 3);
        var r = R(wh, Ranger);
        Expect(Trip(wh.Reg).Phase == JointPhase.Failed, "trip failed: " + Trip(wh.Reg));
        Expect(r.Region == Madison && !r.Reserved && r.Driver == 0 && r.Passenger == 0, "reservation rolled back: car stays in Madison, no driver, no seat: " + r);
        Arrive(wh, 1, Hillville, 6);
        Expect(JointRules.Passenger(true, Trip(wh.Reg), 1, Hillville, false, true, false, false) == JointStep.Recover, "host passenger recovers on foot at the destination");
        Expect(R(wh, Ranger).Driver == 0 && R(wh, Ranger).Holder == 0, "host never promoted to driver/holder of a car in another region");
        Invariants(wh);
        // driver arrived (reservation committed), then disconnects before the passenger is seated
        var w2 = Pair(Madison, 2);
        Travel(w2, 2, Hillville, Ranger, 1); Leave(w2, 2, 1.4); Leave(w2, 1, 2); Arrive(w2, 2, Hillville, 4); Claim(w2, 2, Ranger, 4.1);
        w2.PlayerDisconnected(2, 5);
        Arrive(w2, 1, Hillville, 7);
        r = R(w2, Ranger);
        Expect(r.Region == Hillville && r.Driver == 0 && r.Passenger == 0 && r.Holder == 1, "car stays committed in Hillville, parked; the host may simulate it (on foot), not driving: " + r);
        Invariants(w2);
    }

    static void Timeouts()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1); Leave(wh, 1, 1.4); Leave(wh, 2, 2);
        wh.Tick(1 + JointRules.TripTimeout - 1);
        Expect(wh.Joint.Active, "not before the timeout");
        wh.Tick(1 + JointRules.TripTimeout + 1);
        Expect(Trip(wh.Reg).Phase == JointPhase.Failed && R(wh, Ranger).Passenger == 0, "loading timeout: failed, seat released");
        var w2 = Pair(Madison, 1);
        Travel(w2, 1, Hillville, Ranger, 1); Leave(w2, 1, 1.4); Leave(w2, 2, 2); Arrive(w2, 1, Hillville, 4); Arrive(w2, 2, Hillville, 5);
        w2.Tick(5 + JointRules.ArrivedTimeout + 1);
        Expect(Trip(w2.Reg).Phase == JointPhase.Failed && R(w2, Ranger).Passenger == 0, "driver never seated / passenger never ready: failed after the arrival timeout");
        Expect(JointRules.Passenger(true, Trip(w2.Reg), 2, Hillville, false, true, false, false) == JointStep.Recover, "passenger forward recovery");
        Expect(JointRules.Passenger(true, Trip(w2.Reg), 2, Hillville, false, true, false, true) == JointStep.Recover, "local timeout too");
    }

    static void WrongScene()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1); Leave(wh, 1, 1.4); Leave(wh, 2, 2);
        Arrive(wh, 2, Hideout, 4);
        Expect(Trip(wh.Reg).Phase == JointPhase.Failed && R(wh, Ranger).Passenger == 0, "passenger in the wrong scene: failed, seat released");
        Expect(JointRules.Passenger(true, Trip(wh.Reg), 2, Hideout, false, true, false, false) == JointStep.Recover, "recover on foot there");
        Invariants(wh);
        var w2 = Pair(Madison, 1);
        Travel(w2, 1, Hillville, Ranger, 1); Leave(w2, 1, 1.4); Leave(w2, 2, 2);
        Arrive(w2, 1, Verwood, 4);
        Expect(Trip(w2.Reg).Phase == JointPhase.Failed && R(w2, Ranger).Region == Madison && !R(w2, Ranger).Reserved && R(w2, Ranger).Passenger == 0, "driver in the wrong scene: rollback, failed");
        Invariants(w2);
        Expect(JointRules.Passenger(true, Trip(w2.Reg), 2, Regions.None, false, true, false, false) == JointStep.Wait, "still loading: wait (recovers once the scene exists)");
    }

    static void Reconnect()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hillville, Ranger, 1); Leave(wh, 1, 1.4); Leave(wh, 2, 2);
        wh.PlayerDisconnected(2, 3);
        Arrive(wh, 1, Hillville, 4); Claim(wh, 1, Ranger, 4.1);
        Arrive(wh, 2, Hillville, 20);                                   // reconnected, its game finished loading Hillville
        Expect(R(wh, Ranger).Passenger == 0 && !wh.Joint.KeepsSeat(2), "no stale seat after reconnect");
        bool joint;
        Expect(wh.TravelCheck(1, Req(Madison, Ranger, Hillville), out joint) == null && !joint, "driver not blocked by the failed trip");
        Expect(Ride(wh, 2, Ranger, 1, 21) == ProposalCode.Accepted && Ride(wh, 2, Ranger, 2, 21.5) == ProposalCode.Accepted, "normal passenger entry works again");
    }

    static void SeatCleanup()
    {
        Action<WorldHost, string> check = (w, what) =>
        {
            Expect(!w.Joint.Active, what + ": trip ended");
            foreach (var id in w.Fleet.Ids()) { var r = R(w, id); Expect(!r.PassengerPending, what + ": no pending seat"); if (r.Passenger != 0) Expect(RegionDirectory.RegionOf(w.Reg, r.Passenger) == r.Region, what + ": seat only where the passenger is: " + r); }
        };
        var a = Pair(Madison, 1); Travel(a, 1, Hillville, Ranger, 1); Leave(a, 1, 1.4); Leave(a, 2, 2); a.PlayerDisconnected(2, 3); check(a, "passenger disconnect");
        var b = Pair(Madison, 1); Travel(b, 1, Hillville, Ranger, 1); Leave(b, 1, 1.4); Leave(b, 2, 2); b.Tick(500); check(b, "timeout");
        var c = Pair(Madison, 1); Travel(c, 1, Hillville, Ranger, 1); Ride(c, 2, Ranger, 0, 1.2); check(c, "passenger got out before departure (raced the click)");
        Expect(Trip(c.Reg).Phase == JointPhase.Cancelled && c.Travel.HasReservation(1), "driver continues alone");
        var d = Pair(Madison, 2); Travel(d, 2, Hillville, Ranger, 1);
        uint rev; int sr; float sh;
        var cancel = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.TravelCancel, Value = BitConverter.GetBytes(_pid) };
        d.HandleProposal("cli", 2, ++_pid, cancel, 1.3, out rev, out sr, out sh);
        check(d, "driver's grant arrived after its menu closed (cancel)");
        Expect(Trip(d.Reg).Phase == JointPhase.Cancelled && R(d, Ranger).Passenger == 0 && R(d, Ranger).Region == Madison, "cancelled before departure: car back, nobody moved");
        var e = Pair(Madison, 1); Travel(e, 1, Hillville, Ranger, 1); Leave(e, 2, 1.2); check(e, "passenger quit to the menu before the driver left");
    }

    static void SoloCarry()
    {
        var wh = Both(Madison); Claim(wh, 1, Ranger, 0.5);
        Expect(Travel(wh, 1, Hillville, Ranger, 1) && !HasTrip(wh.Reg) && wh.Joint.Started == 0, "driver alone: plain Carry, no trip");
        Expect(R(wh, Ranger).Region == Hillville && R(wh, Ranger).Reserved && R(wh, Ranger).Holder == 1, "M4.2 reservation");
        Leave(wh, 1, 1.4); Arrive(wh, 1, Hillville, 3);
        Expect(!R(wh, Ranger).Reserved && wh.Travel.Commits == 1, "committed as before");
    }

    static void OnFoot()
    {
        var wh = Both(Madison);
        var g = wh.HostTravel(++_pid, new TravelRequest { Dest = Hillville, Car = VehicleIds.None, Pose = new ParkRecord { Region = Madison, Fuel = -1 } }, 1);
        Expect(g.Outcome == TravelOutcome.None && !HasTrip(wh.Reg), "host on foot, the car stays with P2: " + g);
        Arrive(wh, 1, Hillville, 2);
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = Hideout, Car = VehicleIds.None, Pose = new ParkRecord { Region = Madison, Fuel = -1 } }.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, ++_pid, p, 3, out rev, out sr, out sh) == ProposalCode.Accepted && !HasTrip(wh.Reg), "client on foot: accepted, no trip");
    }

    static void DifferentRegions()
    {
        var wh = Both(Madison, Ranger, true);
        wh.Fleet.Place(GT, Hillville, wh.Outbox);
        wh.SetPlayerRegion(2, Hillville, 1);
        wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox);
        Claim(wh, 1, Ranger, 1.1);
        Expect(Travel(wh, 1, Verwood, Ranger, 2) && !HasTrip(wh.Reg), "P1 travels alone with its car");
        Expect(RegionDirectory.RegionOf(wh.Reg, 2) == Hillville && R(wh, GT).Region == Hillville && R(wh, GT).Holder == 2, "P2 and its car untouched");
    }

    static void Clocks()
    {
        var wh = Pair(Madison, 1);
        RegionClock before; Expect(wh.Clocks.TryGet(Madison, out before), "Madison clock");
        uint rev; int sr; float sh;
        float from = wh.Clocks.Model(Madison, 0.9);
        var ss = new StateEntry { Scope = StateScope.Region, ScopeArg = Madison, Key = WorldKeys.SleepStart, Value = new SleepStart { From = from, TargetHour = 8 }.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, ++_pid, ss, 0.9, out rev, out sr, out sh) == ProposalCode.Accepted && wh.Active != null, "a sleep started by P2 (precondition)");
        FullTrip(wh, 1, Ranger, Hillville, 1);
        Expect(wh.Active == null, "the sleep was dropped when its sleeper left (never replayed after arrival)");
        var sl = new StateEntry { Scope = StateScope.Region, ScopeArg = Hillville, Key = WorldKeys.Sleep, Value = new SleepRequest { From = from, Advance = 4f }.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, ++_pid, sl, 12, out rev, out sr, out sh) == ProposalCode.Invalid, "an old sleep completion never advances the destination clock");
        RegionClock h; Expect(wh.Clocks.TryGet(Hillville, out h), "one clock for the destination, shared by both participants");
        float m1 = wh.Clocks.Model(Hillville, 12);
        Expect(!float.IsNaN(m1), "destination clock established");
    }

    static void Replication()
    {
        var wh = Pair(Madison, 1);
        var client = StateRegistry.CreateClient();
        client.LoadSnapshot(wh.Reg.Epoch, wh.Reg.HeadRev, wh.Reg.Entries);
        Func<int> relay = () =>
        {
            int n = 0;
            foreach (var e in wh.Outbox)
            {
                var w = new NetDataWriter(); StateMessages.WriteDelta(w, wh.Reg.Epoch, e);
                var r = new NetDataReader(w.CopyData()); r.GetByte();
                uint ep; StateEntry de; Expect(StateMessages.TryReadDelta(r, out ep, out de), "delta codec");
                var res = client.ApplyDelta(ep, de);
                Expect(res == DeltaResult.Applied || res == DeltaResult.Duplicate, "delta " + res);
                if (de.Key == WorldKeys.JointTrip) n++;
            }
            wh.Outbox.Clear();
            return n;
        };
        Travel(wh, 1, Hillville, Ranger, 1);
        int iTrip = wh.Outbox.FindIndex(e => e.Key == WorldKeys.JointTrip), iCar = wh.Outbox.FindIndex(e => e.Key == WorldKeys.Vehicle && e.ScopeArg == Ranger);
        Expect(iTrip >= 0 && iCar > iTrip, "the trip entry is sent before the car record moves (a split delivery never shows a moved car without its trip)");
        var partial = StateRegistry.CreateClient(); partial.LoadSnapshot(client.Epoch, client.HeadRev, client.Entries);
        for (int i = 0; i <= iTrip; i++) partial.ApplyDelta(wh.Reg.Epoch, wh.Outbox[i]);
        VehicleRecord pr; var pv = new FleetView(); pv.Refresh(partial); pv.TryGet(Ranger, out pr);
        Expect(pr.Region == Madison && Trip(partial).Phase == JointPhase.Authorized, "first half of a split delivery: trip known, car still in the origin -> hold, seat valid");
        relay();
        var ct = Trip(client);
        Expect(JointRules.Passenger(true, ct, 2, Madison, true, false, false, false) == JointStep.Hold, "client: hold");
        Leave(wh, 1, 1.4); relay();
        ct = Trip(client);
        Expect(JointRules.Passenger(true, ct, 2, Madison, true, false, false, false) == JointStep.Depart, "client: depart after the driver left");
        var phases = new List<JointPhase> { ct.Phase };
        Leave(wh, 2, 2); relay(); Arrive(wh, 1, Hillville, 4); Claim(wh, 1, Ranger, 4.1); relay(); phases.Add(Trip(client).Phase);
        Arrive(wh, 2, Hillville, 6); relay(); phases.Add(Trip(client).Phase);
        Ride(wh, 2, Ranger, 2, 6.5); relay(); phases.Add(Trip(client).Phase);
        for (int i = 1; i < phases.Count; i++) Expect(phases[i] >= phases[i - 1], "phases monotonic: " + string.Join(",", phases.ConvertAll(x => x.ToString()).ToArray()));
        Expect(phases[phases.Count - 1] == JointPhase.Committed, "client sees the commit");
    }

    static void HideoutArrival()
    {
        var wh = Pair(Madison, 1);
        Travel(wh, 1, Hideout, Ranger, 1); Leave(wh, 1, 1.4); Leave(wh, 2, 2);
        Arrive(wh, 2, Hideout, 4);
        Expect(JointRules.Passenger(true, Trip(wh.Reg), 2, Hideout, false, true, false, false) == JointStep.Recover, "passenger arrives on foot at the hideout (original: the driver arrives on foot too)");
        Arrive(wh, 1, Hideout, 6);
        Expect(Trip(wh.Reg).Phase == JointPhase.Committed && R(wh, Ranger).Passenger == 0 && R(wh, Ranger).Region == Hideout, "committed, seat released, car in the garage");
        Invariants(wh);
    }

    static void Cost()
    {
        var wh = Pair(Madison, 1);
        int commits = 0, bytes = 0;
        Action count = () => { foreach (var e in wh.Outbox) if (e.Key == WorldKeys.JointTrip) { commits++; bytes += e.Value.Length; } wh.Outbox.Clear(); };
        Travel(wh, 1, Hillville, Ranger, 1); count(); Leave(wh, 1, 1.4); count(); Leave(wh, 2, 2); count();
        Arrive(wh, 1, Hillville, 4); count(); Claim(wh, 1, Ranger, 4.1); count(); Arrive(wh, 2, Hillville, 6); count(); Ride(wh, 2, Ranger, 2, 6.5); count();
        Expect(commits <= 7 && bytes == commits * JointTrip.WireSize, "bounded trip traffic: " + commits + " entries");
        var t = Trip(wh.Reg); var r = R(wh, Ranger);
        const int N = 1000000;
        int gc0 = GC.CollectionCount(0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int acc = 0;
        for (int i = 0; i < N; i++)
        {
            bool ready = JointRules.ReadyToSeat(r, t, 2, Hillville, true, true, (i & 1) == 0);
            acc += (int)JointRules.Passenger(true, t, 2, Hillville, false, true, ready, false);
        }
        sw.Stop();
        Expect(GC.CollectionCount(0) == gc0, "no allocations in the per-frame decisions");
        Console.WriteLine("      trip registry traffic: " + commits + " jointTrip entries x " + JointTrip.WireSize + " B = " + bytes + " B (plus the existing vehicle/park/region entries); per-frame decision "
            + (sw.Elapsed.TotalMilliseconds * 1e6 / N).ToString("0.0") + " ns offline (.NET Framework, not Unity) acc=" + acc);
    }
}
