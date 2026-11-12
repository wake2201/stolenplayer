// M4.3 Phase A offline tests: host-authoritative passenger seat (reservation, confirmation, release, timeout,
// disconnect), driver/passenger/holder separation, role symmetry, driver exit with a passenger aboard, camera-mode
// replication (record + snapshot flag), remote occupant presentation, door rule "stopped", Phase A travel guard.
// Pure logic only: no Unity physics, cameras, animation or rendering is exercised here.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class PassengerTests
{
    static int _passed, _failed;
    static uint _pid;
    const int Hideout = 0, Hillville = 97, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);

    static int Main()
    {
        Run("01 client rides with the host driver: reserve -> confirm -> release", ClientRidesWithHost);
        Run("02 host rides with the client driver (role symmetry)", HostRidesWithClient);
        Run("03 passenger never drives, never holds the simulation, never takes the driver seat", AuthoritySeparation);
        Run("04 seat conditions: driver required, seat free, stopped, same region, unlocked, not travelling", Conditions);
        Run("05 duplicate / retransmitted / conflicting requests", Duplicates);
        Run("06 simultaneous requests for one passenger seat: first committed wins", Simultaneous);
        Run("07 entry timeout cancels an unconfirmed reservation", Timeout);
        Run("08 disconnect and region change free the seat", DisconnectAndRegion);
        Run("09 driver exits while the passenger stays; no automatic takeover", DriverExitsPassengerStays);
        Run("10 seat assignment after driver changes (re-entry, handoff)", AfterDriverChanges);
        Run("11 camera mode: record flag, snapshot flag, initial state for a new passenger", CameraMode);
        Run("12 remote occupant presentation: driver and passenger anchors", Occupants);
        Run("13 E routing: passenger request vs driver handoff vs occupied", EnterRouting);
        Run("14 door rule: doors of a stopped occupied car open, moving car refused", DoorRule);
        Run("15 travel guard (Phase B): passenger cannot choose a destination, driver with passenger travels jointly", TravelGuard);
        Run("16 holder leaves the region: seat stays logical, holder moves only to a present player", HolderLeaves);
        Run("17 codecs: record flags, proposal values, protocol 10", Codecs);
        Run("18 passenger seat geometry from the real SEAT_PROBE data", SeatGeometry);
        Run("19 per-frame decision cost (offline) and zero allocations", Cost);
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

    // both players in 'region'; the Ranger there, host (P1) simulates it; GT locked in the hideout
    static WorldHost Both(int region)
    {
        var wh = new WorldHost(7) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, region, 0);
        wh.SetPlayerRegion(2, region, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);
        wh.Fleet.Register(GT, 1, false, wh.Outbox);
        wh.Fleet.Place(Ranger, region, wh.Outbox);
        wh.Fleet.Place(GT, Hideout, wh.Outbox);
        wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox);
        wh.Outbox.Clear();
        return wh;
    }

    static VehicleRecord R(WorldHost wh, ushort id) { VehicleRecord r; Expect(wh.Fleet.TryGet(id, out r), "record " + id); return r; }

    static ProposalCode Prop(WorldHost wh, byte slot, string key, ushort id, byte v, double now, uint pid = 0)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = id, Key = key, Value = new[] { v } };
        return wh.HandleProposal("cli", slot, pid != 0 ? pid : ++_pid, p, now, out rev, out sr, out sh);
    }

    static ProposalCode RideP(WorldHost wh, ushort id, byte op, double now, uint pid = 0) { return Prop(wh, 2, WorldKeys.VehicleRide, id, op, now, pid); }
    static ProposalCode HostRide(WorldHost wh, ushort id, byte op, double now) { string why; return wh.Ride(1, id, op, now, out why); }
    static ProposalCode HostTake(WorldHost wh, ushort id, double now) { string why; return wh.TakeOver(1, id, now, out why); }

    // the client drives the Ranger: handoff to P2, then its lease
    static WorldHost ClientDriving(int region)
    {
        var wh = Both(region);
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 1) == ProposalCode.Accepted, "client takes the car");
        Expect(Prop(wh, 2, WorldKeys.VehicleClaim, Ranger, 1, 1.1) == ProposalCode.Accepted, "client lease");
        return wh;
    }

    static void ClientRidesWithHost()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);                    // host drives (original entry)
        wh.Motion.Report(Ranger, false, true, 2);                         // engine on, standing
        Expect(RideP(wh, Ranger, 1, 3) == ProposalCode.Accepted, "client reserves the passenger seat");
        var r = R(wh, Ranger);
        Expect(r.Passenger == 2 && r.PassengerPending && r.Driver == 1 && r.Holder == 1, "reserved: passenger P2 entering, host still driver + holder: " + r);
        Expect(RideP(wh, Ranger, 2, 3.5) == ProposalCode.Accepted && !R(wh, Ranger).PassengerPending, "confirmed seated");
        Expect(RideP(wh, Ranger, 2, 3.6) == ProposalCode.Unchanged, "second confirm harmless");
        Expect(RideP(wh, Ranger, 0, 10) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 0, "released on exit");
        Expect(RideP(wh, Ranger, 0, 10.1) == ProposalCode.Unchanged, "second release harmless");
        Expect(R(wh, Ranger).Driver == 1 && R(wh, Ranger).Holder == 1, "driver and holder untouched");
    }

    static void HostRidesWithClient()
    {
        var wh = ClientDriving(Hillville);
        wh.Motion.Report(Ranger, false, true, 2);
        Expect(HostRide(wh, Ranger, 1, 3) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 1, "host reserves in place");
        Expect(HostRide(wh, Ranger, 2, 3.2) == ProposalCode.Accepted, "host confirms");
        var r = R(wh, Ranger);
        Expect(r.Driver == 2 && r.Holder == 2 && r.Passenger == 1, "client drives and simulates, host rides: " + r);
        Expect(HostRide(wh, Ranger, 0, 9) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 0, "host gets out");
    }

    static void AuthoritySeparation()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        RideP(wh, Ranger, 1, 1); RideP(wh, Ranger, 2, 1.1);
        Expect(Prop(wh, 2, WorldKeys.VehicleClaim, Ranger, 1, 2) == ProposalCode.Busy, "passenger cannot claim the occupied driver seat");
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 2) == ProposalCode.Busy, "passenger cannot take the simulation");
        wh.Fleet.Release(Ranger, 1, wh.Outbox);                           // driver gets out
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 5) == ProposalCode.Busy, "passenger still seated: no takeover even with a free driver seat");
        Expect(Prop(wh, 2, WorldKeys.VehicleClaim, Ranger, 1, 5) != ProposalCode.Accepted, "no driver lease for the passenger");
        var r = R(wh, Ranger);
        Expect(r.Holder == 1 && r.Driver == 0 && r.Passenger == 2, "simulation stays with the host: " + r);
        Expect(wh.Fleet.DrivenBy(2) == VehicleIds.None && wh.Fleet.RiddenBy(2) == Ranger, "P2 rides, drives nothing");
    }

    static void Conditions()
    {
        var wh = Both(Madison);
        Expect(RideP(wh, Ranger, 1, 1) == ProposalCode.Busy, "no driver: the free driver seat is the handoff path, not a passenger seat");
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        wh.Motion.Report(Ranger, true, true, 2);
        Expect(RideP(wh, Ranger, 1, 2.5) == ProposalCode.Busy, "moving car refused");
        Expect(RideP(wh, Ranger, 1, 3.1) == ProposalCode.Accepted, "stopped >= 1 s (driver still inside) -> allowed");
        RideP(wh, Ranger, 0, 3.2);
        wh.SetPlayerRegion(2, Hillville, 4);
        Expect(RideP(wh, Ranger, 1, 4.1) == ProposalCode.NotAuthorized, "other region");
        wh.SetPlayerRegion(2, Madison, 5);
        Expect(RideP(wh, GT, 1, 5.1) == ProposalCode.NotAuthorized, "locked GT");
        var g = wh.HostTravel(++_pid, new TravelRequest { Dest = Hideout, Car = Ranger, Pose = new ParkRecord { Region = Madison, Fuel = -1 } }, 6);
        Expect(g.Outcome == TravelOutcome.Carry && R(wh, Ranger).Reserved, "host drives away with it (no passenger aboard)");
        Expect(RideP(wh, Ranger, 1, 6.1) != ProposalCode.Accepted, "travelling car refused");
    }

    static void Duplicates()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        uint pid = ++_pid;                                                 // ids increase per client process (ProposalGate)
        Expect(RideP(wh, Ranger, 1, 2, pid) == ProposalCode.Accepted, "first");
        RideP(wh, Ranger, 0, 3);
        Expect(RideP(wh, Ranger, 1, 3.5, pid) == ProposalCode.Stale && R(wh, Ranger).Passenger == 0, "late retransmission of an older request: Stale, never applied again");
        Expect(RideP(wh, Ranger, 1, 4) == ProposalCode.Accepted && RideP(wh, Ranger, 1, 4.1) == ProposalCode.Unchanged, "duplicate request: unchanged");
        Expect(RideP(wh, Ranger, 3, 4.2) == ProposalCode.Invalid, "unknown op");
        uint rev; int sr; float sh;
        var bad = new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.VehicleRide, Value = new byte[] { 1, 1 } };
        Expect(wh.HandleProposal("cli", 2, ++_pid, bad, 5, out rev, out sr, out sh) == ProposalCode.Invalid, "malformed");
        Expect(Prop(wh, 2, WorldKeys.VehicleRide, 0, 1, 5) == ProposalCode.Invalid, "vehicle id 0");
    }

    static void Simultaneous()
    {
        // P1 holds and P2 drives? No: one driver + two candidates is impossible with two players. The race is the
        // passenger request against the driver handoff of the same car (both need the requester to be the non-holder).
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        Expect(RideP(wh, Ranger, 1, 2) == ProposalCode.Accepted, "passenger request first");
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 2) == ProposalCode.Busy, "same player's later handoff request refused");
        Expect(HostRide(wh, Ranger, 1, 2) == ProposalCode.Busy, "the driver cannot also be the passenger");
        RideP(wh, Ranger, 0, 3);
        wh.Fleet.Release(Ranger, 1, wh.Outbox);
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 5) == ProposalCode.Accepted, "handoff first (free driver seat, settled)");
        Expect(HostRide(wh, Ranger, 1, 5) == ProposalCode.Busy, "no passenger seat without a seated driver");
        Expect(R(wh, Ranger).Passenger == 0 && R(wh, Ranger).Holder == 2, "one result only");
    }

    static void Timeout()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        RideP(wh, Ranger, 1, 10);
        wh.Tick(10 + FleetBook.PassengerEntrySeconds - 0.1);
        Expect(R(wh, Ranger).Passenger == 2, "still reserved before the timeout");
        wh.Tick(10 + FleetBook.PassengerEntrySeconds + 0.1);
        Expect(R(wh, Ranger).Passenger == 0 && !R(wh, Ranger).PassengerPending, "unconfirmed entry cancelled");
        Expect(RideP(wh, Ranger, 2, 21) == ProposalCode.NotAuthorized, "late confirm refused -> the client restores on foot");
        RideP(wh, Ranger, 1, 30); RideP(wh, Ranger, 2, 30.5);
        wh.Tick(60);
        Expect(R(wh, Ranger).Passenger == 2, "a confirmed passenger never times out");
    }

    static void DisconnectAndRegion()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        RideP(wh, Ranger, 1, 1); RideP(wh, Ranger, 2, 1.2);
        wh.PlayerDisconnected(2, 5);
        Expect(R(wh, Ranger).Passenger == 0 && R(wh, Ranger).Driver == 1, "passenger disconnect frees the seat only");
        wh.SetPlayerRegion(2, Madison, 6);
        RideP(wh, Ranger, 1, 7); RideP(wh, Ranger, 2, 7.2);
        wh.SetPlayerRegion(2, Hillville, 8);                               // e.g. a scripted transition
        Expect(R(wh, Ranger).Passenger == 0, "a passenger is never seated in a car of another region");
        wh.SetPlayerRegion(2, Madison, 9);
        RideP(wh, Ranger, 1, 10); RideP(wh, Ranger, 2, 10.2);
        wh.PlayerDisconnected(1, 11);                                      // driver/host side disconnect (holder leaves)
        var r = R(wh, Ranger);
        Expect(r.Driver == 0 && r.Holder == 2 && r.Passenger == 2, "driver gone: holder moves to the present player; the plugin then exits that passenger: " + r);
        Expect(RideP(wh, Ranger, 0, 12) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 0, "the forced exit releases the seat");
    }

    static void DriverExitsPassengerStays()
    {
        var wh = ClientDriving(Madison);
        HostRide(wh, Ranger, 1, 3); HostRide(wh, Ranger, 2, 3.1);
        Expect(Prop(wh, 2, WorldKeys.VehicleClaim, Ranger, 0, 6) == ProposalCode.Accepted, "client driver gets out");
        var r = R(wh, Ranger);
        Expect(r.Driver == 0 && r.Passenger == 1 && r.Holder == 2 && r.Region == Madison, "driver seat empty, host still passenger, client still simulates: " + r);
        Expect(HostTake(wh, Ranger, 9) == ProposalCode.Busy, "passenger does not become driver/holder automatically or by request");
        Expect(Prop(wh, 2, WorldKeys.VehicleClaim, Ranger, 1, 10) == ProposalCode.Accepted, "the former driver may take the free seat again (holder)");
        Expect(HostRide(wh, Ranger, 0, 12) == ProposalCode.Accepted, "passenger exits normally");
        Prop(wh, 2, WorldKeys.VehicleClaim, Ranger, 0, 13);
        Expect(HostTake(wh, Ranger, 15) == ProposalCode.Accepted && R(wh, Ranger).Holder == 1, "after getting out the host may take the driver seat through the handoff");
    }

    static void AfterDriverChanges()
    {
        var wh = Both(Hideout);
        wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox);
        RideP(wh, Ranger, 1, 1); RideP(wh, Ranger, 2, 1.1);
        wh.Fleet.Release(Ranger, 1, wh.Outbox);
        Expect(wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 2, "driver re-enters, passenger keeps the seat");
        int records = 0; foreach (var id in wh.Fleet.Ids()) records++;
        Expect(records == 2, "no duplicate vehicles");
    }

    static void CameraMode()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        Expect(wh.Fleet.SetTpp(Ranger, true, wh.Outbox) && R(wh, Ranger).Tpp, "driver switches to third person (host physics)");
        Expect(!wh.Fleet.SetTpp(Ranger, true, wh.Outbox), "no commit when unchanged (no per-frame traffic)");
        RideP(wh, Ranger, 1, 2);
        var c = new StateRegistry(); c.LoadSnapshot(wh.Reg.Epoch, wh.Reg.HeadRev, wh.Reg.Entries);
        var v = new FleetView(); v.Refresh(c);
        VehicleRecord r; v.TryGet(Ranger, out r);
        Expect(r.Tpp && r.Passenger == 2, "a new passenger reads the driver's current mode from the authoritative record (initial state)");
        Expect(wh.Fleet.SetTpp(Ranger, false, wh.Outbox) && !R(wh, Ranger).Tpp, "back to first person");
        // client driver: Tpp travels in the snapshot flags, the host commits it from IngestRemote
        var s = new VehicleSnapshot { Id = Ranger, Seq = 1, TimeMs = 1, Region = Madison, Pos = new Vec3(1, 2, 3), Rot = Quat.Identity, Flags = VehicleStateFlags.Driven | VehicleStateFlags.Tpp };
        var w = new NetDataWriter(); s.Write(w);
        var rd = new NetDataReader(w.CopyData()); rd.GetByte();
        VehicleSnapshot o;
        Expect(VehicleSnapshot.TryRead(rd, out o) && (o.Flags & VehicleStateFlags.Tpp) != 0, "snapshot Tpp flag round trip");
        var bad = w.CopyData(); bad[35] = 0x10;
        var rb = new NetDataReader(bad); rb.GetByte();
        Expect(!VehicleSnapshot.TryRead(rb, out o), "unknown snapshot flag bits rejected");
        var sp = new VehicleSendPolicy();
        Expect(sp.ShouldSend(0, s.Pos, s.Rot, VehicleStateFlags.Driven, Madison) && !sp.ShouldSend(0.01, s.Pos, s.Rot, VehicleStateFlags.Driven, Madison)
            && !sp.ShouldSend(1.0, s.Pos, s.Rot, VehicleStateFlags.Driven, Madison)
            && sp.ShouldSend(1.05, s.Pos, s.Rot, VehicleStateFlags.Driven | VehicleStateFlags.Tpp, Madison), "a mode change goes out in the next 50 ms send slot; an unchanged standing car sends nothing (heartbeat 2 s)");
    }

    static void Occupants()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        RideP(wh, Ranger, 1, 1); RideP(wh, Ranger, 2, 1.1);
        var v = new FleetView(); v.Refresh(wh.Reg);
        ushort id; SeatRole role;
        Expect(v.FindSeat(1, Madison, out id, out role) && role == SeatRole.Driver, "host instance: P1 driver");
        Expect(v.FindSeat(2, Madison, out id, out role) && role == SeatRole.Passenger && id == Ranger, "P2 passenger of the Ranger");
        Expect(SeatRules.Occupant(true, true, SeatRole.Passenger, true) == OccupantView.Seated, "remote passenger seated (was hidden in 0.5.0)");
        Expect(SeatRules.Occupant(true, true, SeatRole.Driver, true) == OccupantView.Seated, "remote driver seated");
        Expect(SeatRules.Occupant(true, true, SeatRole.Passenger, false) == OccupantView.Hidden, "no anchor -> hidden, never a guessed position");
        Expect(SeatRules.Occupant(false, false, SeatRole.None, false) == OccupantView.Walking, "walking after exit");
        var ps = SeatRules.Passenger(true, new Vec3(-0.01f, -0.40f, -0.23f), Quat.Identity);
        Expect(ps.Valid && ps.Source == SeatSource.AssociateSeat && ps.CodenameRoot && Math.Abs(ps.Pos.Z + 0.23f) < 1e-4, "remote passenger = original associateSitPos with Codename-root semantics");
        Expect(!SeatRules.Passenger(false, default(Vec3), Quat.Identity).Valid, "no associate seat -> no passenger seat");
    }

    static void EnterRouting()
    {
        var wh = Both(Madison);
        var r = R(wh, Ranger);
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, true, true) == EnterAction.RequestDriver, "empty driver seat: handoff (unchanged)");
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox); r = R(wh, Ranger);
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, false, true) == EnterAction.RequestPassenger, "driver inside, standing: passenger request");
        Expect(PresentationRules.Enter(CarPresentation.ParkedOther, r, 2, false, true) == EnterAction.RequestPassenger, "same in the hideout");
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, false, false) == EnterAction.Wait, "moving: wait");
        RideP(wh, Ranger, 1, 2); r = R(wh, Ranger);
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, false, true) == EnterAction.Unavailable, "already the passenger");
        r.Passenger = 3;   // seat taken (synthetic)
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, false, true) == EnterAction.Occupied, "both seats taken");
        Expect(PresentationRules.Enter(CarPresentation.AwayProxy, r, 2, true, true) == EnterAction.TravelMenu, "hideout away proxy stays travel-only");
        Expect(PresentationRules.Enter(CarPresentation.Real, r, 1, true, true) == EnterAction.Original, "own car: original entry");
    }

    static void DoorRule()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        wh.Motion.Report(Ranger, false, true, 1);                          // driver inside, engine on, standing
        string why;
        Expect(wh.SetDoor(2, Ranger, 1, true, 1.2, out why) == ProposalCode.Accepted, "stopped occupied car: passenger door opens (0.5.0 logged vdoor -> Busy here)");
        Expect(wh.SetDoor(2, Ranger, 0, true, 1.3, out why) == ProposalCode.Accepted, "Shadowhawk has only car_door_L: it must open for the passenger too");
        wh.Motion.Report(Ranger, true, true, 2);
        wh.SetDoor(2, Ranger, 1, false, 2.1, out why);
        Expect(wh.SetDoor(2, Ranger, 1, true, 2.2, out why) == ProposalCode.Busy, "moving car: refused");
        Expect(wh.SetDoor(1, Ranger, 1, true, 2.3, out why) == ProposalCode.Accepted, "holder always");
    }

    static void TravelGuard()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        RideP(wh, Ranger, 1, 1); RideP(wh, Ranger, 2, 1.1);
        Expect(wh.SeatTravelBlock(1) == null && wh.SeatTravelBlock(2) != null, "M4.3 Phase B: the passenger may not choose a destination, the driver may (joint travel)");
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = Hideout, Car = VehicleIds.None, Pose = new ParkRecord { Region = Madison, Fuel = -1 } }.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, ++_pid, p, 2, out rev, out sr, out sh) == ProposalCode.Busy, "client passenger travel refused by the host");
        Expect(R(wh, Ranger).Region == Madison && !R(wh, Ranger).Reserved && R(wh, Ranger).Passenger == 2, "nothing moved, nobody detached");
        RideP(wh, Ranger, 0, 3);
        Expect(wh.SeatTravelBlock(1) == null && wh.SeatTravelBlock(2) == null, "after getting out both may travel again");
        var c = ClientDriving(Madison);
        HostRide(c, Ranger, 1, 3); HostRide(c, Ranger, 2, 3.1);
        var q = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = Hillville, Car = Ranger, Pose = new ParkRecord { Region = Madison, Fuel = -1 } }.ToBytes() };
        Expect(c.HandleProposal("cli", 2, ++_pid, q, 4, out rev, out sr, out sh) == ProposalCode.Accepted && R(c, Ranger).Region == Hillville && R(c, Ranger).Passenger == 1 && c.Joint.Active,
            "M4.3 Phase B: the client driver with the host aboard leaves with the car as one joint journey (Phase A refused this)");
    }

    static void HolderLeaves()
    {
        var wh = ClientDriving(Madison);
        HostRide(wh, Ranger, 1, 3); HostRide(wh, Ranger, 2, 3.1);
        Prop(wh, 2, WorldKeys.VehicleClaim, Ranger, 0, 5);                // client gets out ...
        wh.SetPlayerRegion(2, Hillville, 8);                               // ... and walks to another region
        var r = R(wh, Ranger);
        Expect(r.Holder == 1 && r.Passenger == 1 && r.Region == Madison, "holder moves to the present player (the seated host); the plugin then gets the host out: " + r);
        Expect(HostRide(wh, Ranger, 0, 9) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 0, "seat released, car parked with one holder");
    }

    static void Codecs()
    {
        var r = new VehicleRecord { Kind = 0, Region = Madison, Driver = 1, Passenger = 2, Holder = 1, Flags = VehicleFlags.Unlocked | VehicleFlags.PassengerPending | VehicleFlags.Tpp | VehicleFlags.EngineOn };
        VehicleRecord p;
        Expect(VehicleRecord.TryParse(r.ToBytes(), out p) && p.PassengerPending && p.Tpp && p.Passenger == 2, "record round trip with the new flags");
        var b = r.ToBytes(); b[1] = 0x20;
        Expect(!VehicleRecord.TryParse(b, out p), "unknown record flag bits rejected");
        Expect(ProtocolInfo.ProtocolVersion == 19 && ProtocolInfo.ModVersion == "0.10.4", "protocol 19 / mod 0.10.4");
        Expect(WorldKeys.VehicleRide == "vehicleRide", "key");
    }

    static void SeatGeometry()
    {
        // real SEAT_PROBE values (2026-11-11 logs, car-local): Ranger assoc (-0.01,-0.40,-0.23) sit (-0.56,0.19,0.51);
        // Shadowhawk assoc (0.44,-0.78,0.09) sit (-0.41,-0.26,-0.13); codenameBelowSit -0.52 (Shadowhawk front row)
        float k = SeatRules.CodenameBelowSit(true, new Vec3(0.44f, -0.78f, 0.09f), true, new Vec3(-0.41f, -0.26f, -0.13f));
        var gt = SeatRules.PassengerRoot(new Vec3(0.44f, -0.78f, 0.09f), true, new Vec3(-0.41f, -0.26f, -0.13f), k);
        Expect(Math.Abs(gt.Y + 0.26f) < 1e-3 && Math.Abs(gt.X - 0.44f) < 1e-4, "Shadowhawk passenger root at the driver root height, front passenger side: " + gt);
        var rg = SeatRules.PassengerRoot(new Vec3(-0.01f, -0.40f, -0.23f), true, new Vec3(-0.56f, 0.19f, 0.51f), k);
        Expect(Math.Abs(rg.Y - 0.12f) < 1e-3 && Math.Abs(rg.X + 0.01f) < 1e-4 && Math.Abs(rg.Z + 0.23f) < 1e-4, "Ranger rear centre root 7 cm below the front root (calibration pending): " + rg);
        var nk = SeatRules.PassengerRoot(new Vec3(-0.01f, -0.40f, -0.23f), true, new Vec3(-0.56f, 0.19f, 0.51f), float.NaN);
        Expect(Math.Abs(nk.Y - 0.19f) < 1e-4, "without calibration: the car's own sit height");
        var adj = SeatRules.ParseAdjust("0:0,-0.08,0.05");
        Expect(adj.Count == 1 && Math.Abs(adj[0].Y + 0.08f) < 1e-4, "PassengerSeatAdjust uses the same parser");
    }

    static void Cost()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        RideP(wh, Ranger, 1, 1); RideP(wh, Ranger, 2, 1.1);
        var v = new FleetView(); v.Refresh(wh.Reg);
        VehicleRecord r; v.TryGet(Ranger, out r);
        const int N = 1000000;
        int gc0 = GC.CollectionCount(0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int acc = 0;
        for (int i = 0; i < N; i++)
        {
            ushort id; SeatRole role;
            if (v.FindSeat(2, Madison, out id, out role)) acc++;                            // SeatFrame (both roles)
            if (r.Tpp == ((i & 1) == 0)) acc++;                                             // passenger camera mode check
            if (SeatRules.Occupant(true, true, role, true) == OccupantView.Seated) acc++;
        }
        sw.Stop();
        int gcs = GC.CollectionCount(0) - gc0;
        Console.WriteLine("      offline (.NET Framework, not Unity): seat + camera-mode decisions " + (sw.Elapsed.TotalMilliseconds * 1000.0 / N).ToString("0.000") + " us/frame, gen0 GCs over 1M frames: " + gcs + " (acc " + acc + ")");
        Expect(gcs == 0, "no allocations");
    }
}
