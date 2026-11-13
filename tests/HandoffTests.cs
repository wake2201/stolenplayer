// M4.2.2 offline tests: driver seat vs simulation holder, host-authoritative driver handoff, E interaction priority,
// seated remote driver rules, local collision modes, stay handover on travel, stale snapshots after holder/region changes.
// Pure logic only: no Unity physics, animation or rendering is exercised here.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class HandoffTests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("01 host enters and exits the Ranger (lease claim/release by the holder)", HostEntersExits);
        Run("02 driver seat free immediately after the confirmed exit", SeatFreeAfterExit);
        Run("03 vehicle identity survives driver release and handoff", IdentitySurvives);
        Run("04 simulation holder and driver seat are separate", HolderVsSeat);
        Run("05 client requests the free parked Ranger (vehicleTake proposal)", ClientRequests);
        Run("06 host authorizes client driving (take, then normal lease)", ClientDrives);
        Run("07 previous simulator relinquishes authority (snapshots, presentation)", PreviousRelinquishes);
        Run("08 no overlapping driver grants", NoOverlap);
        Run("09 repeated host->client->host handoff without region reload", Repeated);
        Run("10 simultaneous driver requests: first wins, handoff lock", Simultaneous);
        Run("11 handoff with disconnect / retransmission", DisconnectAndRetransmit);
        Run("12 handoff rejected while the car moves or is driven", RejectMoving);
        Run("13 stale / invalid / foreign-region handoff messages", StaleMessages);
        Run("14 E on an away proxy still opens the travel menu", AwayProxyTravel);
        Run("15 E on a free shared car requests driving, not travel", ParkedOtherRequests);
        Run("16 remote driver avatar enters the seated state", SeatedState);
        Run("17 remote driver avatar walks again after exit", WalkingAfterExit);
        Run("18 vehicle-specific driver seat anchors", SeatAnchors);
        Run("19 remote collision mode transitions + motion latch", CollisionModes);
        Run("20 never two simulating instances for one car", NoDuplicatePhysics);
        Run("21 car leaves a region: old visual presentation removed", CarLeaves);
        Run("22 player leaves, car stays: handed over at decision time, stays visible", PlayerLeavesCarStays);
        Run("23 old movement packets do not recreate departed / handed-over cars", OldPackets);
        Run("24 reconnect and scene reload keep one car and a valid holder", ReconnectReload);
        Run("25 regressions: carry travel, protocol 8, snapshot Moving flag codec", Regressions);
        Run("26 per-frame decision cost (offline) and zero allocations", PerFrameCost);
        Run("27 M4.2.3 host driver travels WITH the car while the client stays (rule A)", HostDrivesAway);
        Run("28 M4.2.3 client driver travels WITH the car while the host stays (rule A)", ClientDrivesAway);
        Run("29 M4.2.3 on foot: the parked car stays, other player may take it (rule B)", OnFootStays);
        Run("30 M4.2.3 other player drives: traveller cannot take it, no reassignment (rule C)", OtherDrives);
        Run("31 M4.2.3 departure reserved: no lease/handoff/snapshots in the old region", NoOldRegionUse);
        Run("32 M4.2.3 failed load / disconnect during car travel rolls back without duplicate", CarTravelRollback);
        Run("33 M4.2.3 arrival at the hideout: car usable there, return trip, one car", ArrivalAndReturn);
        Run("34 M4.2.3 concurrent travel and entry request", ConcurrentTravelEntry);
        Run("35 M4.2.3 seat calibration from the real SEAT_PROBE data + manual adjust", SeatCalibration);
        Run("36 M5.0 car door state: commits, holder priority, moving refusal, echo/stale suppression, late join", DoorSync);
        Run("37 M5.0 door rules: invalid proposals, foreign region, reset when the car travels", DoorRules);
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

    const int Hideout = 0, Hillville = 97, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);
    static uint _pid = 100;

    // Both players in 'region' with the Ranger there (GT locked in the hideout). Host (P1) simulates.
    static WorldHost Both(int region)
    {
        var wh = new WorldHost(9) { FallbackHours = 12f };
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

    static ProposalCode TakeP(WorldHost wh, ushort id, double now) { return Prop(wh, 2, WorldKeys.VehicleTake, id, 1, now); }
    static ProposalCode ClaimP(WorldHost wh, ushort id, bool claim, double now) { return Prop(wh, 2, WorldKeys.VehicleClaim, id, (byte)(claim ? 1 : 0), now); }
    static ProposalCode HostTake(WorldHost wh, ushort id, double now) { string why; return wh.TakeOver(1, id, now, out why); }

    static void HostEntersExits()
    {
        var wh = Both(Hideout);
        Expect(R(wh, Ranger).Holder == 1, "host simulates the Ranger at the hideout");
        Expect(wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox) == ProposalCode.Accepted && R(wh, Ranger).Driver == 1, "host lease");
        Expect(wh.Fleet.Release(Ranger, 1, wh.Outbox) == ProposalCode.Accepted && R(wh, Ranger).Driver == 0, "host release");
    }

    static void SeatFreeAfterExit()
    {
        var wh = Both(Hideout);
        wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox);
        wh.Motion.Report(Ranger, false, true, 10);           // host sat inside until t=10
        wh.Fleet.Release(Ranger, 1, wh.Outbox);
        Expect(R(wh, Ranger).Driver == 0, "seat empty right after release");
        Expect(TakeP(wh, Ranger, 10.5) == ProposalCode.Busy, "handoff waits for the settle time (driven just now)");
        Expect(TakeP(wh, Ranger, 11.1) == ProposalCode.Accepted, "free seat can be requested ~1 s after the exit");
    }

    static void IdentitySurvives()
    {
        var wh = Both(Hideout);
        wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox); wh.Fleet.Release(Ranger, 1, wh.Outbox);
        TakeP(wh, Ranger, 5);
        var ids = wh.Fleet.Ids();
        Expect(ids.Count == 2 && ids.Contains(Ranger) && ids.Contains(GT), "both identities kept");
        Expect(R(wh, Ranger).Kind == 0 && R(wh, Ranger).Region == Hideout && R(wh, Ranger).Unlocked, "same car, same region, still unlocked");
        Expect(wh.Fleet.Register(Ranger, 0, true, wh.Outbox) == ProposalCode.Unchanged, "re-registration idempotent (no new car)");
    }

    static void HolderVsSeat()
    {
        var wh = Both(Madison);
        var r = R(wh, Ranger);
        Expect(r.Holder == 1 && r.Driver == 0, "parked: holder P1, seat empty (separate fields)");
        Expect(SeatRules.RoleOf(r, 1) == SeatRole.None && SeatRules.RoleOf(r, 2) == SeatRole.None, "holder is not a seat occupant");
        Expect(PresentationRules.Decide(r, Madison, 2) == CarPresentation.RemoteVisual
            && PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, true) == EnterAction.RequestDriver, "P2 may request the free seat although P1 simulates the car");
    }

    static void ClientRequests()
    {
        var wh = Both(Hideout);
        Expect(PresentationRules.Decide(R(wh, Ranger), Hideout, 2) == CarPresentation.ParkedOther, "client sees the shared copy");
        Expect(TakeP(wh, Ranger, 3) == ProposalCode.Accepted, "take accepted");
        var r = R(wh, Ranger);
        Expect(r.Holder == 2 && r.Driver == 0, "simulation moved to P2, seat not yet taken (entry follows through the original method)");
        Expect(wh.Outbox.Count > 0, "change replicated through the registry outbox");
    }

    static void ClientDrives()
    {
        var wh = Both(Hideout);
        TakeP(wh, Ranger, 3);
        Expect(ClaimP(wh, Ranger, true, 3.2) == ProposalCode.Accepted && R(wh, Ranger).Driver == 2, "client lease after the grant");
        Expect(PresentationRules.Decide(R(wh, Ranger), Hideout, 2) == CarPresentation.Real, "client simulates the real car");
    }

    static void PreviousRelinquishes()
    {
        var wh = Both(Madison);
        TakeP(wh, Ranger, 3);
        var r = R(wh, Ranger);
        Expect(!FleetView.SenderSimulates(r, 1, Madison) && FleetView.SenderSimulates(r, 2, Madison), "only the new holder may stream");
        var f = new VehicleSnapshotFilter();
        Expect(f.Check(Snap(Ranger, 50, Madison), true, FleetView.SenderSimulates(r, 1, Madison), Madison) == VehicleVerdict.NotDriverAuthority, "old holder's snapshot refused");
        Expect(PresentationRules.Decide(r, Madison, 1) == CarPresentation.RemoteVisual && PresentationRules.Decide(r, Madison, 2) == CarPresentation.Real, "P1 now shows a visual, P2 the real car");
    }

    static void NoOverlap()
    {
        var wh = Both(Hideout);
        TakeP(wh, Ranger, 3); ClaimP(wh, Ranger, true, 3.1);
        Expect(HostTake(wh, Ranger, 20) == ProposalCode.Busy, "host cannot take an occupied seat");
        Expect(wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox) == ProposalCode.Busy, "host lease refused while P2 drives");
        Expect(R(wh, Ranger).Driver == 2 && R(wh, Ranger).Holder == 2, "unchanged");
    }

    static void Repeated()
    {
        var wh = Both(Hideout);
        double t = 0;
        for (int i = 0; i < 3; i++)
        {
            t += 10;
            Expect(TakeP(wh, Ranger, t) == ProposalCode.Accepted, "round " + i + ": client take");
            Expect(ClaimP(wh, Ranger, true, t + 0.2) == ProposalCode.Accepted, "round " + i + ": client drives");
            Expect(ClaimP(wh, Ranger, false, t + 2) == ProposalCode.Accepted, "round " + i + ": client exits");
            t += 10;
            Expect(HostTake(wh, Ranger, t) == ProposalCode.Accepted && R(wh, Ranger).Holder == 1, "round " + i + ": host take");
            Expect(wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox) == ProposalCode.Accepted, "round " + i + ": host drives");
            Expect(wh.Fleet.Release(Ranger, 1, wh.Outbox) == ProposalCode.Accepted, "round " + i + ": host exits");
        }
        Expect(wh.Fleet.Ids().Count == 2 && R(wh, Ranger).Region == Hideout, "same fleet after 6 handoffs");
    }

    static void Simultaneous()
    {
        var wh = Both(Madison);
        // P1 (host) holds; P2 requests and P1's own late take arrives right after in the same tick
        Expect(TakeP(wh, Ranger, 5) == ProposalCode.Accepted, "first request wins");
        Expect(HostTake(wh, Ranger, 5) == ProposalCode.Busy, "second request inside the handoff lock refused");
        Expect(wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox) == ProposalCode.NotAuthorized, "old holder cannot claim the seat");
        Expect(HostTake(wh, Ranger, 5 + FleetBook.TakeLockSeconds + 0.1) == ProposalCode.Accepted, "after the lock ran out (P2 never entered) the host may take it back");
        var w2 = Both(Madison);
        TakeP(w2, Ranger, 5); ClaimP(w2, Ranger, true, 5.1);
        Expect(!w2.Fleet.TakeLocked(Ranger, 1, 5.2), "lock cleared once the new holder claimed the seat");
    }

    static void DisconnectAndRetransmit()
    {
        var wh = Both(Hideout);
        uint id = ++_pid;
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 5, id) == ProposalCode.Accepted, "take");
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 5, id) == ProposalCode.Accepted && R(wh, Ranger).Holder == 2, "retransmission answered from cache, applied once");
        wh.PlayerDisconnected(2, 6);
        var r = R(wh, Ranger);
        Expect(r.Holder == 1 && r.Driver == 0 && r.Region == Hideout, "disconnect: simulation back to the host, car kept");
        Expect(!wh.Fleet.TakeLocked(Ranger, 1, 6.1), "handoff lock of the leaver dropped");
        Expect(wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox) == ProposalCode.Accepted, "host can drive at once");
    }

    static void RejectMoving()
    {
        var wh = Both(Madison);
        wh.Motion.Report(Ranger, true, false, 10);
        Expect(TakeP(wh, Ranger, 10.2) == ProposalCode.Busy && R(wh, Ranger).Holder == 1, "rolling car: refused");
        wh.Motion.Report(Ranger, false, false, 10.5);   // stationary reports do not refresh activity
        Expect(TakeP(wh, Ranger, 11.05) == ProposalCode.Accepted, "settled 1 s after the last movement");
        var w2 = Both(Madison);
        w2.Fleet.Claim(Ranger, 1, Madison, w2.Outbox);
        Expect(TakeP(w2, Ranger, 30) == ProposalCode.Busy, "seat occupied: refused regardless of motion");
    }

    static void StaleMessages()
    {
        var wh = Both(Hideout);
        uint hi = ++_pid + 5;
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 3, hi) == ProposalCode.Accepted, "take #hi");
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 1, 3, hi - 2) == ProposalCode.Stale, "older id: stale, not applied");
        _pid = hi;
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, Ranger, 7, 3) == ProposalCode.Invalid, "bad value");
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, VehicleIds.None, 1, 3) == ProposalCode.Invalid, "no vehicle");
        Expect(Prop(wh, 2, WorldKeys.VehicleTake, GT, 1, 3) == ProposalCode.NotAuthorized, "locked (unbought) car");
        var w2 = Both(Madison);
        w2.SetPlayerRegion(2, Hillville, 1);
        Expect(TakeP(w2, Ranger, 5) == ProposalCode.NotAuthorized, "requester in another region");
    }

    static void AwayProxyTravel()
    {
        var wh = Both(Hideout);
        wh.SetPlayerRegion(1, Madison, 1);   // host took nothing; place the Ranger in Madison
        wh.Fleet.Place(Ranger, Madison, wh.Outbox); wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox);
        var r = R(wh, Ranger);
        Expect(PresentationRules.Decide(r, Hideout, 2) == CarPresentation.AwayProxy, "away proxy");
        Expect(PresentationRules.Enter(CarPresentation.AwayProxy, r, 2, true) == EnterAction.TravelMenu, "E: travel menu");
    }

    static void ParkedOtherRequests()
    {
        var wh = Both(Hideout);
        var r = R(wh, Ranger);
        Expect(PresentationRules.Enter(CarPresentation.ParkedOther, r, 2, true) == EnterAction.RequestDriver, "free + settled: request driver");
        Expect(PresentationRules.Enter(CarPresentation.ParkedOther, r, 2, false) == EnterAction.Wait, "moving: wait");
        r.Driver = 1;
        Expect(PresentationRules.Enter(CarPresentation.ParkedOther, r, 2, true) == EnterAction.RequestPassenger, "M4.3: occupied driver seat -> passenger seat request, never travel");
        r.Driver = 0; r.Flags |= VehicleFlags.Reserved;
        Expect(PresentationRules.Enter(CarPresentation.ParkedOther, r, 2, true) == EnterAction.Unavailable, "travelling car: unavailable");
        Expect(!PresentationRules.EnterOpensTravelMenu(CarPresentation.ParkedOther), "ParkedOther is not a travel proxy any more");
        Expect(PresentationRules.Enter(CarPresentation.Real, r, 1, true) == EnterAction.Original, "own car: original");
    }

    static void SeatedState()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        var v = new FleetView(); v.Refresh(wh.Reg);
        ushort id; SeatRole role;
        Expect(SeatRules.Find(v.All, 1, Madison, out id, out role) && id == Ranger && role == SeatRole.Driver, "P2 finds the host in the Ranger's driver seat");
        Expect(SeatRules.Occupant(true, true, role, true) == OccupantView.Seated, "seated presentation");
        Expect(SeatRules.Occupant(true, true, SeatRole.Passenger, true) == OccupantView.Seated, "M4.3: remote passenger seated at its own anchor");
        Expect(SeatRules.Occupant(true, false, SeatRole.None, false) == OccupantView.Hidden, "unsynchronized (stolen/NPC) car: hidden as before");
        Expect(!SeatRules.Find(v.All, 1, Hillville, out id, out role), "not seated in a car of another region");
        ushort id2; SeatRole role2;
        Expect(v.FindSeat(1, Madison, out id2, out role2) && id2 == Ranger && role2 == SeatRole.Driver && !v.FindSeat(2, Madison, out id2, out role2), "allocation-free FleetView.FindSeat matches");
    }

    static void PerFrameCost()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        var v = new FleetView(); v.Refresh(wh.Reg);
        VehicleRecord r; v.TryGet(Ranger, out r);
        var latch = new MotionLatch();
        const int N = 1000000;
        int gc0 = GC.CollectionCount(0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int acc = 0;
        for (int i = 0; i < N; i++)
        {
            ushort id; SeatRole role;
            if (v.FindSeat(1, Madison, out id, out role)) acc++;                                   // SeatFrame
            if (PresentationRules.Collision(CarPresentation.RemoteVisual, r, (i & 1) == 0) == CollisionMode.Solid) acc++;   // CollisionFrame
            if (latch.Update((i % 100) * 0.01f, i / 60.0)) acc++;                                   // StreamFrame
        }
        sw.Stop();
        int gcs = GC.CollectionCount(0) - gc0;
        double us = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
        Console.WriteLine("      offline (.NET Framework, not Unity): seat+collision+latch decisions " + us.ToString("0.000") + " us/frame, gen0 GCs over 1M frames: " + gcs + " (acc " + acc + ")");
        Expect(gcs == 0, "no allocations in the per-frame decision path");
    }

    static void WalkingAfterExit()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        wh.Fleet.Release(Ranger, 1, wh.Outbox);
        var v = new FleetView(); v.Refresh(wh.Reg);
        ushort id; SeatRole role;
        Expect(!SeatRules.Find(v.All, 1, Madison, out id, out role), "no seat after release");
        Expect(SeatRules.Occupant(false, false, SeatRole.None, false) == OccupantView.Walking, "walking avatar again");
    }

    static void SeatAnchors()
    {
        var id = Quat.Identity;
        // Shadowhawk-like: Associate Sit front passenger x=+0.44, driver sit_transform on the other side of the same row
        var gt = SeatRules.Driver(true, new Vec3(0.44f, 0.35f, 0.05f), id, true, new Vec3(-0.40f, 0.9f, 0.0f), true, new Vec3(-0.4f, 0.2f, 0f), id);
        Expect(gt.Source == SeatSource.MirroredAssociate && Math.Abs(gt.Pos.X + 0.44f) < 1e-4 && Math.Abs(gt.Pos.Y - 0.35f) < 1e-4 && gt.CodenameRoot, "GT: mirrored associate seat (same model + clip as CodenameAI)");
        // Ranger-like: associate anchor is a rear centre position (not the front row) -> the original driver model anchor
        var ranger = SeatRules.Driver(true, new Vec3(0f, 0.4f, -0.9f), id, true, new Vec3(-0.38f, 0.95f, 0.1f), true, new Vec3(-0.38f, 0.25f, 0.05f), id);
        Expect(ranger.Source == SeatSource.TppCharacter && Math.Abs(ranger.Pos.X + 0.38f) < 1e-4 && !ranger.CodenameRoot, "Ranger: tpp_character anchor");
        var noTpp = SeatRules.Driver(false, default(Vec3), id, true, new Vec3(-0.38f, 0.95f, 0.1f), true, new Vec3(0, 0, 0), id);
        Expect(noTpp.Source == SeatSource.SitTransform, "driver model at the car origin is not trusted -> sit_transform");
        Expect(!SeatRules.Driver(false, default(Vec3), id, false, default(Vec3), false, default(Vec3), id).Valid, "no anchor -> none (avatar hidden, no guessed offset)");
        Expect(gt.Pos.X != ranger.Pos.X, "anchors are per vehicle");
    }

    static void CollisionModes()
    {
        var wh = Both(Madison);
        var r = R(wh, Ranger);
        // M5.0: a presented car is solid in every state (the earlier "ghost while driven/moving" policy is withdrawn)
        Expect(PresentationRules.Collision(CarPresentation.RemoteVisual, r, true) == CollisionMode.Solid, "parked + settled: solid");
        Expect(PresentationRules.Collision(CarPresentation.RemoteVisual, r, false) == CollisionMode.Solid, "moving: solid");
        r.Driver = 1;
        Expect(PresentationRules.Collision(CarPresentation.ParkedOther, r, false) == CollisionMode.Solid && PresentationRules.Collision(CarPresentation.RemoteVisual, r, false) == CollisionMode.Solid, "driven: solid");
        r.Driver = 0; r.Flags |= VehicleFlags.Reserved;
        Expect(PresentationRules.Collision(CarPresentation.ParkedOther, r, true) == CollisionMode.Solid, "travelling: solid until it leaves the region");
        Expect(PresentationRules.IsObserved(CarPresentation.ParkedOther) && PresentationRules.IsObserved(CarPresentation.RemoteVisual) && !PresentationRules.IsObserved(CarPresentation.AwayProxy)
            && PresentationRules.IsTravelProxy(CarPresentation.AwayProxy) && !PresentationRules.IsTravelProxy(CarPresentation.ParkedOther), "observed vs travel proxy");
        Expect(PresentationRules.Collision(CarPresentation.AwayProxy, r, false) == CollisionMode.Solid, "away proxy never moves: solid");
        Expect(PresentationRules.Collision(CarPresentation.Real, r, false) == CollisionMode.Original && PresentationRules.Collision(CarPresentation.Hidden, r, true) == CollisionMode.None, "real/hidden");
        var l = new MotionLatch();
        Expect(!l.Update(0.3f, 0) && l.Update(0.6f, 0.1) && l.Update(0.3f, 0.2), "on above 0.5 m/s, stays on between thresholds");
        Expect(l.Update(0.1f, 0.3) && l.Update(0.1f, 0.7) && !l.Update(0.1f, 0.85), "off only after 0.5 s below 0.15 m/s");
        Expect(!l.Update(0.4f, 0.9), "no flapping near standstill");
    }

    static void NoDuplicatePhysics()
    {
        var wh = Both(Hideout);
        Action check = () =>
        {
            var r = R(wh, Ranger);
            int real = 0;
            for (byte s = 1; s <= 2; s++) if (PresentationRules.Decide(r, Hideout, s) == CarPresentation.Real) real++;
            int streamers = 0;
            for (byte s = 1; s <= 2; s++) if (FleetView.SenderSimulates(r, s, Hideout)) streamers++;
            Expect(real == 1 && streamers == 1, "exactly one simulating instance (real=" + real + " streamers=" + streamers + ")");
        };
        check();
        TakeP(wh, Ranger, 3); check();
        ClaimP(wh, Ranger, true, 3.1); check();
        ClaimP(wh, Ranger, false, 4); check();
        HostTake(wh, Ranger, 10); check();
    }

    static TravelGrant ClientTravel(WorldHost wh, int dest, ushort car, ParkRecord pose, double now)
    {
        uint rev; int sr; float sh;
        uint id = ++_pid;
        var req = new TravelRequest { Dest = dest, Car = car, Pose = pose };
        var p = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = req.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, id, p, now, out rev, out sr, out sh) == ProposalCode.Accepted, "travel proposal");
        TravelGrant g;
        Expect(TravelGrant.TryParse(wh.Reg.Get(StateScope.Player, 2, WorldKeys.TravelGrant).Value, out g) && g.Id == id, "grant");
        return g;
    }

    static void CarLeaves()
    {
        var wh = Both(Hideout);
        TakeP(wh, Ranger, 3);                                     // P2 simulates the Ranger at the hideout
        var g = ClientTravel(wh, Madison, Ranger, new ParkRecord { Region = Hideout, Fuel = 50 }, 5);
        Expect(g.Outcome == TravelOutcome.Carry, "P2 carries the Ranger to Madison: " + g);
        var r = R(wh, Ranger);
        Expect(r.Region == Madison && r.Reserved, "record moved at decision time");
        Expect(PresentationRules.Decide(r, Hideout, 1) == CarPresentation.AwayProxy, "host in the hideout: no shared copy any more (travel access point only)");
        var f = new VehicleSnapshotFilter();
        Expect(f.Check(Snap(Ranger, 99, Hideout), true, FleetView.SenderSimulates(r, 2, Hideout), Hideout) == VehicleVerdict.NotDriverAuthority, "late hideout snapshot refused");
        var w2 = Both(Madison);
        w2.SetPlayerRegion(1, Hillville, 1);                       // host left Madison: P2 holds the Ranger there
        var r2 = R(w2, Ranger);
        Expect(r2.Holder == 2, "P2 simulates in Madison");
        Expect(PresentationRules.Decide(r2, Hillville, 1) == CarPresentation.Hidden, "observer elsewhere: nothing shown");
    }

    static void PlayerLeavesCarStays()
    {
        var wh = Both(Madison);
        TakeP(wh, Ranger, 3); ClaimP(wh, Ranger, true, 3.1);       // P2 drives the Ranger to the map exit ...
        ClaimP(wh, Ranger, false, 10);                            // ... gets out and leaves on foot (M4.2.3 rule B)
        var exitPose = new ParkRecord { Region = Madison, HasPose = true, Pos = new Vec3(640, 46, 120), Rot = Quat.Identity, Fuel = 42 };
        var g = ClientTravel(wh, Hideout, Ranger, exitPose, 20);
        Expect(g.Outcome == TravelOutcome.None, "on foot while the host is in Madison: the region car stays (" + g + ")");
        var r = R(wh, Ranger);
        Expect(r.Region == Madison && !r.Reserved && r.Holder == 1 && r.Driver == 0, "handed to the host at decision time (no wait for the scene unload)");
        ParkRecord p;
        Expect(wh.Fleet.TryGetPark(Ranger, out p) && p.HasPose && Math.Abs(p.Pos.X - 640) < 0.01 && Math.Abs(p.Fuel - 42) < 0.01, "pose/fuel reported by the leaver kept");
        Expect(PresentationRules.Decide(r, Madison, 1) == CarPresentation.Real, "car stays visible (real) for the host");
        wh.SetPlayerRegion(2, Regions.None, 21);
        Expect(R(wh, Ranger).Holder == 1 && R(wh, Ranger).Region == Madison, "after the leaver's scene unload nothing changes");
        // no handover when nobody else is there
        var w2 = Both(Madison);
        w2.SetPlayerRegion(1, Hillville, 1);
        var g2 = ClientTravel(w2, Hideout, Ranger, exitPose, 5);
        Expect(g2.Outcome == TravelOutcome.Carry, "alone: the car goes with the traveller as before");
    }

    static void OldPackets()
    {
        var wh = Both(Madison);
        var tracker = new FleetChangeTracker();
        var v = new FleetView(); v.Refresh(wh.Reg); tracker.Changed(v.All);
        var f = new VehicleSnapshotFilter();
        Expect(f.Check(Snap(Ranger, 10, Madison), true, true, Madison) == VehicleVerdict.Accept, "P1 stream");
        TakeP(wh, Ranger, 3);
        v.Refresh(wh.Reg);
        var changed = tracker.Changed(v.All);
        Expect(changed != null && changed.Contains(Ranger), "holder change detected");
        foreach (var id in changed) f.Forget(id);
        VehicleRecord r; v.TryGet(Ranger, out r);
        Expect(f.Check(Snap(Ranger, 11, Madison), true, FleetView.SenderSimulates(r, 1, Madison), Madison) == VehicleVerdict.NotDriverAuthority, "newer packet from the old holder refused");
        Expect(f.Check(Snap(Ranger, 1, Madison), true, FleetView.SenderSimulates(r, 2, Madison), Madison) == VehicleVerdict.Accept, "new holder's restarted sequence accepted");
        Expect(f.Check(Snap(Ranger, 2, Hillville), true, FleetView.SenderSimulates(r, 2, Hillville), Madison) == VehicleVerdict.NotDriverAuthority, "packet for another region never creates a visual here");
    }

    static void ReconnectReload()
    {
        var wh = Both(Hideout);
        TakeP(wh, Ranger, 3);
        wh.PlayerDisconnected(2, 4);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);           // host scene reload re-registers
        wh.SetPlayerRegion(2, Hideout, 6);                       // client back
        var c = StateRegistry.CreateClient();
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
        var rd = new NetDataReader(w.CopyData()); rd.GetByte();
        uint ep, hd; List<StateEntry> list;
        Expect(StateMessages.TryReadSnapshot(rd, out ep, out hd, out list), "snapshot");
        c.LoadSnapshot(ep, hd, list);
        var v = new FleetView(); v.Refresh(c);
        VehicleRecord r;
        Expect(v.Count == 2 && v.TryGet(Ranger, out r) && r.Holder == 1 && r.Driver == 0 && r.Region == Hideout, "one Ranger, host simulates after the reconnect");
        Expect(TakeP(wh, Ranger, 20) == ProposalCode.Accepted, "client can take it again after reconnect");
    }

    static void Regressions()
    {
        Expect(ProtocolInfo.ProtocolVersion == 23 && ProtocolInfo.ModVersion == "0.13.0", "protocol 23 / mod 0.13.0");
        var s = Snap(Ranger, 5, Madison); s.Flags = VehicleStateFlags.Moving | VehicleStateFlags.Driven | VehicleStateFlags.EngineOn;
        var w = new NetDataWriter(); s.Write(w);
        Expect(w.Length == VehicleSnapshot.WireSize && w.Length == 39, "39 bytes (M5.0 steer)");
        var rd = new NetDataReader(w.CopyData()); rd.GetByte();
        VehicleSnapshot o;
        Expect(VehicleSnapshot.TryRead(rd, out o) && o.Flags == s.Flags, "Moving flag round trip");
        var bad = w.CopyData(); bad[35] = 0x10;   // 0x08 = Tpp since protocol 10
        var rb = new NetDataReader(bad); rb.GetByte();
        Expect(!VehicleSnapshot.TryRead(rb, out o), "unknown flag bit rejected");
        // M4.2 carry rule unchanged: alone in Madison -> carry to Hillville
        var wh = Both(Madison);
        wh.SetPlayerRegion(2, Hideout, 1);
        var g = wh.HostTravel(1, new TravelRequest { Dest = Hillville, Car = Ranger, Pose = new ParkRecord { Region = Madison, Fuel = -1f } }, 2);
        Expect(g.Outcome == TravelOutcome.Carry && R(wh, Ranger).Region == Hillville, "carry unchanged");
    }

    static TravelGrant HostTravelReq(WorldHost wh, int dest, ushort car, double now)
    {
        return wh.HostTravel(++_pid, new TravelRequest { Dest = dest, Car = car, Pose = new ParkRecord { Region = RegionDirectory.RegionOf(wh.Reg, 1), HasPose = true, Pos = new Vec3(615, 45, 85), Rot = Quat.Identity, Fuel = 55 } }, now);
    }

    static void HostDrivesAway()
    {
        var wh = Both(Madison);                                   // log 21:56: host holds + drives, client in Madison
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        var g = HostTravelReq(wh, Hideout, Ranger, 10);
        Expect(g.Outcome == TravelOutcome.Carry && g.Car == Ranger, "driver carries the car although the client stays: " + g);
        var r = R(wh, Ranger);
        Expect(r.Region == Hideout && r.Reserved && r.Holder == 1 && r.Driver == 0, "record moved to the destination at decision time");
        Expect(PresentationRules.Decide(r, Madison, 2) == CarPresentation.Hidden, "client in Madison: no car left there");
        Expect(wh.Fleet.VehiclesIn(Madison).Count == 0, "Madison has no permanent car any more");
    }

    static void ClientDrivesAway()
    {
        var wh = Both(Madison);
        TakeP(wh, Ranger, 3); ClaimP(wh, Ranger, true, 3.1);
        var g = ClientTravel(wh, Hideout, Ranger, new ParkRecord { Region = Madison, HasPose = true, Pos = new Vec3(1, 2, 3), Rot = Quat.Identity, Fuel = 40 }, 10);
        Expect(g.Outcome == TravelOutcome.Carry, "client driver carries: " + g);
        Expect(R(wh, Ranger).Region == Hideout && PresentationRules.Decide(R(wh, Ranger), Madison, 1) == CarPresentation.Hidden, "host keeps no Madison car");
        ParkRecord p;
        Expect(wh.Fleet.TryGetPark(Ranger, out p) && Math.Abs(p.Fuel - 40) < 0.01, "fuel continuity from the request");
    }

    static void OnFootStays()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox); wh.Fleet.Release(Ranger, 1, wh.Outbox);
        var g = HostTravelReq(wh, Hideout, Ranger, 10);
        Expect(g.Outcome == TravelOutcome.None, "on foot: no carry (" + g + ")");
        var r = R(wh, Ranger);
        Expect(r.Region == Madison && !r.Reserved && r.Holder == 2, "car stays, simulated by the client who stays");
        Expect(PresentationRules.Decide(r, Hideout, 1) == CarPresentation.AwayProxy, "host at home sees only the travel proxy");
        Expect(PresentationRules.Enter(CarPresentation.AwayProxy, r, 1, true) == EnterAction.TravelMenu, "away proxy stays travel-only");
        ClaimP(wh, Ranger, true, 11);
        Expect(R(wh, Ranger).Driver == 2, "client drives the car that stayed");
    }

    static void OtherDrives()
    {
        var wh = Both(Madison);
        TakeP(wh, Ranger, 3); ClaimP(wh, Ranger, true, 3.1);     // client drives
        var g = HostTravelReq(wh, Hideout, Ranger, 10);
        Expect(g.Outcome == TravelOutcome.None, "host cannot take the client's driven car: " + g);
        var r = R(wh, Ranger);
        Expect(r.Region == Madison && r.Holder == 2 && r.Driver == 2, "holder and driver unchanged");
    }

    static void NoOldRegionUse()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        HostTravelReq(wh, Hideout, Ranger, 10);
        Expect(TakeP(wh, Ranger, 30) == ProposalCode.NotAuthorized, "client handoff in Madison refused (car reserved for the hideout)");
        Expect(ClaimP(wh, Ranger, true, 30.1) == ProposalCode.NotAuthorized, "client lease in Madison refused");
        var r = R(wh, Ranger);
        var f = new VehicleSnapshotFilter();
        Expect(f.Check(Snap(Ranger, 500, Madison), true, FleetView.SenderSimulates(r, 1, Madison), Madison) == VehicleVerdict.NotDriverAuthority, "late Madison snapshot of the leaving holder refused");
        Expect(f.Check(Snap(Ranger, 501, Madison), true, FleetView.SenderSimulates(r, 1, Madison), Madison) == VehicleVerdict.NotDriverAuthority, "never recreates a visual");
    }

    static void CarTravelRollback()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        HostTravelReq(wh, Hideout, Ranger, 10);
        wh.SetPlayerRegion(1, Regions.None, 11);                  // departed (loading)
        wh.SetPlayerRegion(1, Hillville, 13);                     // load ended elsewhere -> rollback
        var r = R(wh, Ranger);
        Expect(r.Region == Madison && !r.Reserved && r.Holder == 2, "car back in Madison, simulated by the client there");
        Expect(wh.Fleet.Ids().Count == 2 && wh.Fleet.VehiclesIn(Hideout).Count == 1, "no duplicate (hideout holds only the GT)");
        var w2 = Both(Madison);
        TakeP(w2, Ranger, 3); ClaimP(w2, Ranger, true, 3.1);
        ClientTravel(w2, Hideout, Ranger, new ParkRecord { Region = Madison, Fuel = -1f }, 10);
        w2.PlayerDisconnected(2, 12);
        var r2 = R(w2, Ranger);
        Expect(r2.Region == Madison && !r2.Reserved && r2.Holder == 1, "client disconnect during car travel: car back with the host in Madison");
    }

    static void ArrivalAndReturn()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        HostTravelReq(wh, Hideout, Ranger, 10);
        wh.SetPlayerRegion(1, Regions.None, 11);
        wh.SetPlayerRegion(1, Hideout, 13);
        var r = R(wh, Ranger);
        Expect(r.Region == Hideout && !r.Reserved && r.Holder == 1, "committed at the hideout, host simulates it");
        Expect(PresentationRules.Decide(r, Hideout, 1) == CarPresentation.Real, "real car usable at the hideout");
        wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox);
        var back = HostTravelReq(wh, Madison, Ranger, 20);
        Expect(back.Outcome == TravelOutcome.Carry && R(wh, Ranger).Region == Madison && wh.Fleet.Ids().Count == 2, "return trip with the car: still one Ranger (" + back + ")");
        // client returns with the car too
        var w2 = Both(Madison);
        TakeP(w2, Ranger, 3); ClaimP(w2, Ranger, true, 3.1);
        ClientTravel(w2, Hideout, Ranger, new ParkRecord { Region = Madison, Fuel = -1f }, 10);
        w2.SetPlayerRegion(2, Regions.None, 11); w2.SetPlayerRegion(2, Hideout, 12);
        Expect(PresentationRules.Decide(R(w2, Ranger), Hideout, 2) == CarPresentation.Real, "client at the hideout with its car");
    }

    static void ConcurrentTravelEntry()
    {
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        Expect(TakeP(wh, Ranger, 30) == ProposalCode.Busy, "entry request while the host drives: refused");
        HostTravelReq(wh, Hideout, Ranger, 30);
        Expect(TakeP(wh, Ranger, 30) == ProposalCode.NotAuthorized, "entry request right after the departure decision: refused");
        // request first, travel second: the car the client just took stays (host is no longer driver/holder)
        var w2 = Both(Madison);
        Expect(TakeP(w2, Ranger, 30) == ProposalCode.Accepted, "client takes the parked car first");
        var g = HostTravelReq(w2, Hideout, Ranger, 30.1);
        Expect(g.Outcome == TravelOutcome.None && R(w2, Ranger).Holder == 2 && R(w2, Ranger).Region == Madison, "host's later trip cannot carry it: " + g);
    }

    static void SeatCalibration()
    {
        var id = Quat.Identity;
        // real SEAT_PROBE values (2026-11-10 logs, car-local)
        Vec3 rAssoc = new Vec3(-0.01f, -0.40f, -0.23f), rSit = new Vec3(-0.56f, 0.19f, 0.51f), rTpp = new Vec3(-0.57f, -0.15f, 0.51f);
        Vec3 gAssoc = new Vec3(0.44f, -0.78f, 0.09f), gSit = new Vec3(-0.41f, -0.26f, -0.13f);
        float k = SeatRules.CodenameBelowSit(true, gAssoc, true, gSit);
        Expect(Math.Abs(k + 0.52f) < 1e-3, "Codename root sits 0.52 m below the sit root (Shadowhawk)");
        Expect(float.IsNaN(SeatRules.CodenameBelowSit(true, rAssoc, true, rSit)), "Ranger's associate seat is the rear bench (not used)");
        var s = SeatRules.Driver(true, rAssoc, id, true, rSit, true, rTpp, id, k);
        Expect(s.Source == SeatSource.TppCalibrated && s.CodenameRoot && Math.Abs(s.Pos.Y - (-0.33f)) < 1e-3 && Math.Abs(s.Pos.X + 0.57f) < 1e-3, "Ranger driver lowered from -0.15 to -0.33 (was through the roof)");
        var adj = SeatRules.ParseAdjust("0:0,-0.05,0.02; 1:x,1,1; 7:9,9,9");
        Expect(adj.Count == 2 && Math.Abs(adj[0].Y + 0.05f) < 1e-4 && Math.Abs(adj[7].X - 0.5f) < 1e-4, "manual adjust parsed, invalid part ignored, clamped to 0.5 m");
        Expect(Math.Abs(SeatRules.Adjust(s, adj[0]).Pos.Y - (-0.38f)) < 1e-3, "adjust applied");
        Expect(SeatRules.ParseAdjust("").Count == 0 && SeatRules.ParseAdjust(null).Count == 0, "empty");
    }

    static ProposalCode DoorP(WorldHost wh, byte slot, ushort id, int door, bool open, double now, uint pid = 0, DoorMode mode = DoorMode.Quick)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = id, Key = WorldKeys.VehicleDoor, Value = VehicleDoors.Proposal(door, open, mode) };
        return wh.HandleProposal("cli", slot, pid != 0 ? pid : ++_pid, p, now, out rev, out sr, out sh);
    }

    static void DoorSync()
    {
        var wh = Both(Madison);                                   // host simulates the Ranger, both players in Madison
        Expect(wh.DoorMask(Ranger) == 0, "doors start closed (fresh scene objects)");
        // client (observer) opens the driver door of the parked car
        Expect(DoorP(wh, 2, Ranger, 0, true, 10) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 1, "observer opens L: committed");
        Expect(DoorP(wh, 2, Ranger, 0, true, 11) == ProposalCode.Unchanged, "same state again: no new revision");
        // holder's original entry coroutine closes it while the car is driven: always allowed for the holder
        wh.Motion.Report(Ranger, true, true, 12);
        string why;
        Expect(wh.SetDoor(1, Ranger, 0, false, 12, out why) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0, "holder closes its door while driving");
        Expect(DoorP(wh, 2, Ranger, 1, true, 12.2) == ProposalCode.Busy && wh.DoorMask(Ranger) == 0, "observer cannot open a door of a moving/driven car");
        Expect(DoorP(wh, 2, Ranger, 1, false, 12.3) == ProposalCode.Unchanged, "closing an already closed door is harmless");
        Expect(DoorP(wh, 2, Ranger, 1, true, 13.5) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 2, "settled again (>= 1 s): passenger door opens");
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        Expect(DoorP(wh, 2, Ranger, 0, true, 13.6) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 3, "M4.3: driver seated, car standing: doors open for a passenger (Shadowhawk has only car_door_L)");
        Expect(DoorP(wh, 2, Ranger, 0, false, 13.65) == ProposalCode.Accepted, "and close again");
        Expect(wh.SetDoor(1, Ranger, 0, true, 13.7, out why) == ProposalCode.Accepted && wh.SetDoor(1, Ranger, 0, false, 13.8, out why) == ProposalCode.Accepted, "holder's own code may");
        wh.Fleet.Release(Ranger, 1, wh.Outbox);
        // retransmission of the same proposal id is never applied twice
        uint pid = 777;
        Expect(DoorP(wh, 2, Ranger, 1, false, 14, pid) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0, "close");
        wh.SetDoor(1, Ranger, 1, true, 14.1, out why);
        Expect(DoorP(wh, 2, Ranger, 1, false, 14.2, pid) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 2, "retransmitted close answered from the cache, not re-applied");

        // client replica: own change in flight -> committed deltas of OLDER reports are not applied (no flicker)
        var rep = new DoorReplica();
        rep.Bind(0); rep.OnCommitted(0);
        byte want;
        Expect(rep.TakeLocalChanges(1) == 1, "local open detected"); rep.Sent();
        Expect(rep.TakeLocalChanges(0) == 1, "local close detected"); rep.Sent();
        rep.OnCommitted(1);                                       // delta of the first report arrives
        Expect(!rep.Wanted(out want), "open delta ignored while reports are in flight (door stays closed)");
        rep.Result();
        rep.OnCommitted(0); rep.Result();                         // second delta, then its result
        Expect(!rep.Wanted(out want), "converged: nothing to apply");
        // refusal: committed state wins again after the result
        Expect(rep.TakeLocalChanges(2) == 2, "local open of R"); rep.Sent();
        rep.Result();                                             // Busy: no delta
        Expect(rep.Wanted(out want) && want == 0, "refused open is undone (door returns to the committed state)");
        rep.Applied();
        // remote change while idle -> applied once
        rep.OnCommitted(1);
        Expect(rep.Wanted(out want) && want == 1, "remote open applied"); rep.Applied();
        Expect(!rep.Wanted(out want) && rep.TakeLocalChanges(1) == 0, "applied state is not reported back (no echo)");
        // late join / new scene objects: closed doors bound, committed state applied
        var late = new DoorReplica(); late.OnCommitted(2); late.Bind(0);
        Expect(late.Wanted(out want) && want == 2, "late joiner opens the door that is open in the session");
    }

    static void DoorRules()
    {
        var wh = Both(Madison);
        string why;
        Expect(DoorP(wh, 2, Ranger, 2, true, 1) == ProposalCode.Invalid, "door index out of range");
        uint rev; int sr; float sh;
        var bad = new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.VehicleDoor, Value = new byte[] { 0, 2 } };
        Expect(wh.HandleProposal("cli", 2, ++_pid, bad, 1, out rev, out sr, out sh) == ProposalCode.Invalid, "malformed value");
        Expect(DoorP(wh, 2, GT, 0, true, 1) == ProposalCode.NotAuthorized, "locked / other-region car");
        wh.SetPlayerRegion(2, Hillville, 2);
        Expect(DoorP(wh, 2, Ranger, 0, true, 3) == ProposalCode.NotAuthorized, "requester not in the car's region");
        wh.SetPlayerRegion(2, Madison, 4);
        Expect(wh.SetDoor(1, Ranger, 0, true, 5, out why) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 1, "host opens");
        // the car travels: its scene objects at the destination start closed -> committed state reset, never re-opened there
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        var g = HostTravelReq(wh, Hideout, Ranger, 6);
        Expect(g.Outcome == TravelOutcome.Carry && wh.DoorMask(Ranger) == 0, "door state reset when the car changes region");
        Expect(DoorP(wh, 2, Ranger, 0, true, 7) != ProposalCode.Accepted, "old region: no door changes of the departed car");
        byte m;
        Expect(VehicleDoors.TryParseState(new byte[] { 3 }, out m) && m == 3 && VehicleDoors.TryParseState(new byte[] { 0x0F }, out m) && !VehicleDoors.TryParseState(new byte[] { 16 }, out m) && !VehicleDoors.TryParseState(null, out m), "state codec (M4.3.1: slow bits 2-3)");
        var old = new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.VehicleDoor, Value = new byte[] { 0, 1 } };
        Expect(wh.HandleProposal("cli", 2, ++_pid, old, 8, out rev, out sr, out sh) == ProposalCode.Invalid, "protocol-10 2-byte door proposal rejected");
        Expect(VehicleDoors.With(0, 1, true) == 2 && VehicleDoors.With(3, 0, false) == 2 && VehicleDoors.IsOpen(2, 1) && !VehicleDoors.IsOpen(2, 0), "mask helpers");
    }

    static VehicleSnapshot Snap(ushort id, uint seq, int region)
    {
        return new VehicleSnapshot { Id = id, Seq = seq, TimeMs = seq * 50, Region = region, Pos = new Vec3(1, 2, 3), Rot = Quat.Identity, Flags = VehicleStateFlags.None, Fuel = 10 };
    }
}
