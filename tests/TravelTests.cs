// M4.2 offline tests: host-authoritative travel decisions, region car assignment, reservations, rollback, parking/fuel,
// fleet persistence, client travel gate, continuation guard, spawn candidate selection. Pure logic (no Unity): these do
// not prove in-game vehicle physics, seating or spawn behaviour.
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class TravelTests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("01 one permanent car per region (placement + travel never adds a second car)", OnePerRegion);
        Run("02 two cars in two regions, driven independently (Ranger Madison / GT Hillville)", TwoRegions);
        Run("03 existing destination car wins over the traveller's car", ExistingWins);
        Run("04 host travels into the client's region: joins, Ranger stays in Madison", HostIntoClientRegion);
        Run("05 client travels into the host's region: joins on foot", ClientIntoHostRegion);
        Run("06 simultaneous requests: same batch -> host wins; committed client reservation never overridden", Simultaneous);
        Run("07 duplicate / retransmitted / out-of-order travel requests", DuplicateRequests);
        Run("08 late grant or denial at the client gate (cancel rolls the reservation back)", LateGrant);
        Run("09 reservation timeout and rollback", Timeout);
        Run("10 scene load failure: departed but arrived elsewhere -> rollback", LoadFailure);
        Run("11 returning to a parked car restores its region, pose and fuel (no duplicate)", ReturnToParked);
        Run("12 client disconnect during a reservation and reconnect", DisconnectReconnect);
        Run("13 host restart: fleet record restores assignments and fuel", HostRestart);
        Run("14 wrong-region / wrong-sender vehicle snapshots are refused", WrongRegionSnapshots);
        Run("15 client authority validation (forged slot, foreign pose, foreign car)", ClientAuthority);
        Run("16 duplicate vehicle detection (identity conflict, fleet file duplicates)", DuplicateDetection);
        Run("17 original travel continuation runs exactly once", ContinuationOnce);
        Run("18 travel without a vehicle", TravelOnFoot);
        Run("19 per-vehicle fuel kept separately", FuelSeparation);
        Run("20 phone fast travel to car without an own car", FastTravelGuard);
        Run("21 safe spawn candidate selection order", SpawnSelection);
        Run("22 fleet file format, atomic write, backup recovery, stale detection, no partial transaction persisted", FleetFile);
        Run("23 holder handoff when the simulating player leaves; hideout keeps both cars", HolderHandoff);
        Run("24 special destinations keep the original behaviour; codecs and park policy", SpecialAndCodecs);
        Run("25 M4.2.1 host leaves the hideout with the Ranger: client keeps an E travel proxy (Bug A)", HideoutHostLeaves);
        Run("26 M4.2.1 travel from the proxy never brings the car; joins the host (Examples A/B, symmetric)", ProxyTravelNoCarry);
        Run("27 M4.2.1 returning car switches proxy -> parked/real on both machines (Bug B, Example C)", ReturnRestores);
        Run("28 M4.2.1 fresh hideout: both cars home are parked/real, never away proxies (Bug C, Example E)", BothHome);
        Run("29 M4.2.1 cars in two regions: two away proxies, separate identities (Example D)", TwoAwayProxies);
        Run("30 M4.2.1 proxies grant no driver lease, no storage, no driving; unbought GT hidden", ProxyRestrictions);
        Run("31 M4.2.1 sender sequence restart after reload/handoff is accepted after a record change (Bug B root cause)", SequenceRestart);
        Run("32 M4.2.1 many transitions, reconnect and reload rebuild the presentation from the registry only", ManyTransitions);
        Run("33 M4.2.1 invalid/delayed fleet state and refused authorization never move or duplicate a car", InvalidAndRefused);
        Run("34 M5 hotfix fall rule: lost only after a long fall far below ground; bounded recoveries", FallRule);
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

    static void FallRule()
    {
        // the reported case: Madison ground ~50 m, player at y = -50026 after a long fall
        Expect(FallRules.IsLost(30, -50026f, true, 50f) && FallRules.IsLost(30, -50026f, false, 0f), "deep fall lost (with or without a grounded reference)");
        Expect(!FallRules.IsLost(1.0, -50026f, true, 50f), "not before 3 s airborne");
        Expect(!FallRules.IsLost(10, 30f, true, 50f), "a 20 m drop (roof) is a legitimate fall");
        Expect(!FallRules.IsLost(10, -9f, true, 50f) && FallRules.IsLost(10, -11f, true, 50f), "60 m below the last ground");
        Expect(!FallRules.IsLost(10, -400f, false, 0f) && FallRules.IsLost(10, -501f, false, 0f), "absolute floor without a reference");
        Expect(FallRules.IsLost(0, float.NaN, true, 50f), "NaN position is always lost");
        Expect(FallRules.MayRecover(0, 100) && !FallRules.MayRecover(1, 5) && FallRules.MayRecover(2, 10) && !FallRules.MayRecover(3, 100), "max 3 per region, 10 s apart (no loop)");
    }

    const int Hideout = 0, Hillville = 97, Madison = 99, Museum = 96, Jail = 93;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);
    static uint _cid = 100;

    // Host in hostRegion, client in clientRegion (None = not connected); Ranger/GT placed as given.
    static WorldHost Setup(int rangerRegion, int gtRegion, int hostRegion, int clientRegion)
    {
        var wh = new WorldHost(9) { FallbackHours = 12f };
        Expect(wh.Fleet.Register(Ranger, 0, true, wh.Outbox) == ProposalCode.Accepted, "register ranger");
        Expect(wh.Fleet.Register(GT, 1, true, wh.Outbox) == ProposalCode.Accepted, "register gt");
        Expect(wh.Fleet.Place(Ranger, rangerRegion, wh.Outbox) && wh.Fleet.Place(GT, gtRegion, wh.Outbox), "place");
        wh.SetPlayerRegion(1, hostRegion, 0);
        if (clientRegion != Regions.None) wh.SetPlayerRegion(2, clientRegion, 0);
        wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox);
        wh.Outbox.Clear();
        return wh;
    }

    static VehicleRecord R(WorldHost wh, ushort id) { VehicleRecord r; Expect(wh.Fleet.TryGet(id, out r), "record " + id); return r; }

    static ParkRecord Pose(int region, float x, float fuel) { return new ParkRecord { Region = region, HasPose = true, Pos = new Vec3(x, 0, 5), Rot = Quat.Identity, Fuel = fuel }; }

    static ProposalCode ClientTravel(WorldHost wh, uint id, int dest, ushort car, ParkRecord pose, out TravelGrant g)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = dest, Car = car, Pose = pose }.ToBytes() };
        var code = wh.HandleProposal("client-1", 2, id, p, 0, out rev, out sr, out sh);
        g = default(TravelGrant);
        var e = wh.Reg.Get(StateScope.Player, 2, WorldKeys.TravelGrant);
        if (e != null) TravelGrant.TryParse(e.Value, out g);
        return code;
    }

    static TravelGrant ClientTravel(WorldHost wh, int dest, ushort car)
    {
        TravelGrant g; uint id = ++_cid;
        Expect(ClientTravel(wh, id, dest, car, new ParkRecord { Region = Regions.None, Fuel = -1 }, out g) == ProposalCode.Accepted && g.Id == id, "client request accepted");
        return g;
    }

    static ProposalCode Cancel(WorldHost wh, uint pid, uint requestId)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.TravelCancel, Value = BitConverter.GetBytes(requestId) };
        return wh.HandleProposal("client-1", 2, pid, p, 0, out rev, out sr, out sh);
    }

    static uint _hid;
    static TravelGrant HostTravel(WorldHost wh, int dest, ushort car) { return wh.HostTravel(++_hid, new TravelRequest { Dest = dest, Car = car, Pose = new ParkRecord { Region = Regions.None, Fuel = -1 } }, 0); }

    // Arrival: leave (None) then confirm the destination region.
    static void Arrive(WorldHost wh, byte slot, int dest) { wh.SetPlayerRegion(slot, Regions.None, 1); wh.SetPlayerRegion(slot, dest, 2); }

    static void Invariant(WorldHost wh)
    {
        var seen = new Dictionary<int, ushort>();
        foreach (var id in wh.Fleet.Ids())
        {
            var r = R(wh, id);
            if (!TravelRules.OneCarRegion(r.Region)) continue;
            Expect(!seen.ContainsKey(r.Region), "two permanent cars assigned to " + Regions.Name(r.Region));
            seen[r.Region] = id;
        }
    }

    // ------------------------------------------------------------------------------------------------
    static void OnePerRegion()
    {
        var wh = Setup(Madison, Hideout, Madison, Hideout);
        Expect(!wh.Fleet.Place(GT, Madison, wh.Outbox), "placement refuses a second car");
        var g = ClientTravel(wh, Madison, GT);   // client drives GT from the hideout to Madison (Ranger already there)
        Expect(g.Outcome == TravelOutcome.Join && g.Car == Ranger, "client joins the Ranger: " + g);
        Expect(R(wh, GT).Region == Hideout && !R(wh, GT).Reserved, "GT stays in the hideout");
        Arrive(wh, 2, Madison);
        Invariant(wh);
        Expect(wh.Fleet.VehiclesIn(Madison).Count == 1, "Madison still one car");
    }

    static void TwoRegions()
    {
        var wh = Setup(Hideout, Hideout, Hideout, Hideout);
        // host holds both cars in the garage (lowest present slot); client cannot carry a car the host simulates
        Expect(R(wh, Ranger).Holder == 1 && R(wh, GT).Holder == 1, "host simulates both garage cars");
        var gh = HostTravel(wh, Madison, Ranger);
        Expect(gh.Outcome == TravelOutcome.Carry && gh.Car == Ranger, "host brings the Ranger: " + gh);
        Arrive(wh, 1, Madison);
        Expect(R(wh, Ranger).Region == Madison && !R(wh, Ranger).Reserved && R(wh, Ranger).Holder == 1, "Ranger committed to Madison");
        Expect(R(wh, GT).Holder == 2, "GT handed to the client in the garage");
        var gc = ClientTravel(wh, Hillville, GT);
        Expect(gc.Outcome == TravelOutcome.Carry && gc.Car == GT, "client brings the GT: " + gc);
        Arrive(wh, 2, Hillville);
        Expect(R(wh, GT).Region == Hillville && R(wh, GT).Holder == 2, "GT committed to Hillville, simulated by the client");
        Expect(wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox) == ProposalCode.Accepted && wh.Fleet.Claim(GT, 2, Hillville, wh.Outbox) == ProposalCode.Accepted, "both drive independently");
        Invariant(wh);
    }

    static void ExistingWins()
    {
        var wh = Setup(Hillville, Madison, Madison, Regions.None);   // GT parked in Madison with the host; Ranger parked in Hillville, nobody there
        var g = HostTravel(wh, Hillville, GT);
        Expect(g.Outcome == TravelOutcome.Parked && g.Car == Ranger, "Hillville already has the Ranger: " + g);
        Expect(R(wh, GT).Region == Madison, "GT not carried");
        Expect(R(wh, Ranger).Reserved && R(wh, Ranger).Holder == 1, "Ranger reserved for the arriving host");
        Arrive(wh, 1, Hillville);
        Expect(!R(wh, Ranger).Reserved && R(wh, Ranger).Holder == 1, "committed");
        Expect(R(wh, GT).Holder == 0 && R(wh, GT).Region == Madison, "GT parked in Madison, nobody simulates it");
        Invariant(wh);
    }

    static void HostIntoClientRegion()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        Expect(R(wh, GT).Holder == 2 && R(wh, Ranger).Holder == 1, "setup holders");
        var g = HostTravel(wh, Hillville, Ranger);
        Expect(g.Outcome == TravelOutcome.Join && g.Car == GT, "host joins the client's GT: " + g);
        Expect(R(wh, Ranger).Region == Madison && !R(wh, Ranger).Reserved, "Ranger not carried");
        Arrive(wh, 1, Hillville);
        Expect(R(wh, Ranger).Region == Madison && R(wh, Ranger).Holder == 0, "Ranger parked in Madison (registered, nobody there)");
        Expect(R(wh, GT).Holder == 2, "the host does not take over the client's car because it is the host");
        Invariant(wh);
    }

    static void ClientIntoHostRegion()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        var g = ClientTravel(wh, Madison, GT);
        Expect(g.Outcome == TravelOutcome.Join && g.Car == Ranger, "client joins the host's Ranger: " + g);
        Arrive(wh, 2, Madison);
        Expect(R(wh, GT).Region == Hillville && R(wh, GT).Holder == 0, "GT stays parked in Hillville");
        Expect(R(wh, Ranger).Holder == 1, "host keeps simulating the Ranger");
        Invariant(wh);
    }

    static void Simultaneous()
    {
        // (a) both in the garage-less start: host in Madison with Ranger, client in Hillville with GT, both pick empty Verwood (1)
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        var gh = HostTravel(wh, 1, Ranger);                  // same frame: the host's own click is decided first
        var gc = ClientTravel(wh, 1, GT);                    // client request processed afterwards (LateUpdate batch)
        Expect(gh.Outcome == TravelOutcome.Carry && gc.Outcome == TravelOutcome.Join && gc.Car == Ranger, "host wins the tie: " + gh + " / " + gc);
        Expect(R(wh, GT).Region == Hillville && !R(wh, GT).Reserved, "loser's car stays where it was");
        Arrive(wh, 1, 1); Arrive(wh, 2, 1);
        Invariant(wh);
        Expect(wh.Fleet.VehiclesIn(1).Count == 1, "one car in Verwood");

        // (b) client reservation committed first, later host request never overrides it (even before the client arrives)
        wh = Setup(Madison, Hillville, Madison, Hillville);
        gc = ClientTravel(wh, 1, GT);
        Expect(gc.Outcome == TravelOutcome.Carry && R(wh, GT).Reserved, "client reserved GT for Verwood");
        gh = HostTravel(wh, 1, Ranger);
        Expect(gh.Outcome == TravelOutcome.Join && gh.Car == GT, "later host request joins: " + gh);
        Expect(R(wh, Ranger).Region == Madison && R(wh, GT).Holder == 2, "host did not steal or duplicate");
        Arrive(wh, 2, 1); Arrive(wh, 1, 1);
        Invariant(wh);

        // (c) two attempts to reserve the same car: the car is no longer carriable once reserved
        wh = Setup(Hideout, Hillville, Hideout, Hideout);   // Ranger in the garage, both players there
        Expect(R(wh, Ranger).Holder == 1, "host simulates the garage Ranger");
        var g1 = ClientTravel(wh, Madison, Ranger);
        Expect(g1.Outcome == TravelOutcome.None, "client cannot carry the car the host simulates: " + g1);
        var g2 = HostTravel(wh, Madison, Ranger);
        Expect(g2.Outcome == TravelOutcome.Carry, "host carries it: " + g2);
        var g3 = HostTravel(wh, Hillville, Ranger);
        Expect(g3.Outcome == TravelOutcome.Parked && g3.Car == GT, "new host request supersedes its own unfinished one: " + g3);
        Expect(R(wh, Ranger).Region == Hideout && !R(wh, Ranger).Reserved, "superseded reservation rolled back (no double move)");
    }

    static void DuplicateRequests()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        TravelGrant g; var none = new ParkRecord { Region = Regions.None, Fuel = -1 };
        Expect(ClientTravel(wh, 50, 1, GT, none, out g) == ProposalCode.Accepted && g.Outcome == TravelOutcome.Carry, "first");
        long decisions = wh.Travel.Decisions;
        Expect(ClientTravel(wh, 50, 1, GT, none, out g) == ProposalCode.Accepted && wh.Travel.Decisions == decisions, "retransmission answered from cache, not decided twice");
        Expect(ClientTravel(wh, 49, Madison, GT, none, out g) == ProposalCode.Stale && wh.Travel.Decisions == decisions, "older (out-of-order) id refused");
        Expect(R(wh, GT).Region == 1 && R(wh, GT).Reserved, "state unchanged by the duplicates");
    }

    static void LateGrant()
    {
        var gate = new TravelGate { ResultTimeout = 15 };
        Expect(gate.Begin(7, Madison, 0) && gate.Pending && !gate.Begin(8, Hillville, 0.1), "one pending click at a time");
        Expect(gate.OnResult(99, true) == GateAction.Ignore, "unrelated result ignored");
        Expect(!gate.CheckTimeout(14.9) && gate.CheckTimeout(15.1) && !gate.Pending, "failsafe timeout releases the menu");
        Expect(gate.OnResult(7, true) == GateAction.CancelLate, "late acceptance -> cancel at the host");
        Expect(gate.OnResult(7, true) == GateAction.Ignore, "only once");
        Expect(gate.Begin(9, Madison, 20) && gate.OnResult(9, false) == GateAction.Denied && !gate.Pending, "denial releases the menu, retry possible");
        Expect(gate.Begin(10, Madison, 21) && gate.OnResult(10, true) == GateAction.Resume && !gate.Pending, "normal grant resumes once");
        gate.Begin(11, Madison, 22); Expect(gate.Abandon() == 11 && gate.OnResult(11, false) == GateAction.Ignore, "late denial after abandon ignored");

        // host side of the late cancel
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        TravelGrant g;
        ClientTravel(wh, 60, 1, GT, new ParkRecord { Region = Regions.None, Fuel = -1 }, out g);
        Expect(R(wh, GT).Reserved && R(wh, GT).Region == 1, "reserved");
        Expect(Cancel(wh, 61, 60) == ProposalCode.Accepted, "cancel accepted");
        Expect(R(wh, GT).Region == Hillville && !R(wh, GT).Reserved && R(wh, GT).Holder == 2, "rolled back to Hillville, client simulates it again");
        Expect(Cancel(wh, 62, 60) == ProposalCode.Unchanged, "second cancel harmless");
    }

    static void Timeout()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        ClientTravel(wh, 1, GT);
        wh.Tick(TravelRules.ReservationTimeout - 1);
        Expect(R(wh, GT).Reserved, "still reserved before the safety timeout");
        wh.Tick(TravelRules.ReservationTimeout + 1);
        Expect(!R(wh, GT).Reserved && R(wh, GT).Region == Hillville && wh.Travel.Rollbacks == 1, "rolled back after the timeout");
    }

    static void LoadFailure()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        ClientTravel(wh, 1, GT);
        wh.SetPlayerRegion(2, Hillville, 1);   // re-report of the origin before the reload: no effect
        Expect(R(wh, GT).Reserved, "not departed yet");
        wh.SetPlayerRegion(2, Regions.None, 2);
        wh.SetPlayerRegion(2, Hillville, 3);   // reload failed / came back to the origin
        Expect(!R(wh, GT).Reserved && R(wh, GT).Region == Hillville && R(wh, GT).Holder == 2, "rollback after arriving somewhere else");
        Invariant(wh);
    }

    static void ReturnToParked()
    {
        var wh = Setup(Madison, Hideout, Madison, Regions.None);
        // host parks the Ranger in Madison (pose + fuel reported with the travel request), walks to Hillville
        var g = wh.HostTravel(++_hid, new TravelRequest { Dest = Hillville, Car = Ranger, Pose = Pose(Madison, 123f, 33f) }, 0);
        Expect(g.Outcome == TravelOutcome.Carry, "nobody stays in Madison: the original travel would bring it: " + g);
        // the player walked: in the original the car follows; reproduce the "left it" case by cancelling
        wh.Travel.Abort(1, "test", wh.Outbox);
        Expect(R(wh, Ranger).Region == Madison, "rolled back");
        ParkRecord p; Expect(wh.Fleet.TryGetPark(Ranger, out p) && p.HasPose && p.Pos.X == 123f && p.Fuel == 33f && p.Region == Madison, "parking pose + fuel stored: " + p);
        // host goes elsewhere without the car (to the GT in the hideout), later returns
        g = HostTravel(wh, Hideout, VehicleIds.None);
        Arrive(wh, 1, Hideout);
        Expect(R(wh, Ranger).Holder == 0 && R(wh, Ranger).Region == Madison, "Ranger parked in Madison while away");
        g = HostTravel(wh, Madison, GT);
        Expect(g.Outcome == TravelOutcome.Parked && g.Car == Ranger, "returning: the parked Ranger is taken over, GT stays: " + g);
        Arrive(wh, 1, Madison);
        Expect(wh.Fleet.TryGetPark(Ranger, out p) && p.Pos.X == 123f && p.Fuel == 33f, "pose and fuel unchanged");
        Expect(R(wh, GT).Region == Hideout && wh.Fleet.VehiclesIn(Madison).Count == 1, "no duplicate");
    }

    static void DisconnectReconnect()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        ClientTravel(wh, 1, GT);
        wh.SetPlayerRegion(2, Regions.None, 1);
        wh.PlayerDisconnected(2, 2);   // left during the pending reservation
        Expect(!R(wh, GT).Reserved && R(wh, GT).Region == Hillville && R(wh, GT).Holder == 0, "rolled back, parked in Hillville");
        // reconnect: the client's snapshot shows the authoritative state; it arrives in Verwood on foot
        var cli = StateRegistry.CreateClient();
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
        var r = new NetDataReader(w.CopyData()); r.GetByte();
        uint e, h; List<StateEntry> list;
        Expect(StateMessages.TryReadSnapshot(r, out e, out h, out list), "snapshot");
        cli.LoadSnapshot(e, h, list);
        var v = new FleetView(); v.Refresh(cli);
        VehicleRecord rec;
        Expect(v.TryGet(GT, out rec) && rec.Region == Hillville && !rec.Reserved, "client sees the rolled back GT");
        wh.SetPlayerRegion(2, 1, 3);
        Expect(wh.Fleet.VehiclesIn(1).Count == 0, "no car appears in Verwood");
        wh.SetPlayerRegion(2, Hillville, 4);
        Expect(R(wh, GT).Holder == 2, "back in Hillville: simulates its parked GT again");
    }

    static void HostRestart()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        wh.Fleet.SetPark(Ranger, Pose(Madison, 10, 40), wh.Outbox);
        wh.Fleet.SetPark(GT, Pose(Hillville, 20, 15), wh.Outbox);
        var fs = Export(wh, 2, 5, 30);
        var bytes = fs.Serialize();
        FleetSave back; string err;
        Expect(FleetSave.TryParse(bytes, out back, out err), "parse: " + err);
        var w2 = new WorldHost(10);
        w2.Fleet.Register(Ranger, 0, true, w2.Outbox); w2.Fleet.Register(GT, 1, true, w2.Outbox);
        foreach (var c in back.Cars) { Expect(w2.Fleet.Place(c.Id, c.Region, w2.Outbox), "place " + c.Id); w2.Fleet.SetPark(c.Id, c.Park, w2.Outbox); }
        ParkRecord p;
        Expect(R(w2, Ranger).Region == Madison && R(w2, GT).Region == Hillville, "assignments restored");
        Expect(w2.Fleet.TryGetPark(GT, out p) && p.Fuel == 15 && p.Pos.X == 20 && w2.Fleet.TryGetPark(Ranger, out p) && p.Fuel == 40, "pose + fuel restored");
        w2.SetPlayerRegion(1, Madison, 0);
        Expect(R(w2, Ranger).Holder == 1 && R(w2, GT).Holder == 0, "host simulates its region car, GT waits parked for the client");
    }

    static FleetSave Export(WorldHost wh, int profile, int story, int day)
    {
        var fs = new FleetSave { Profile = profile, Story = story, DayIndex = day, SavedUtcTicks = 1 };
        foreach (var id in wh.Fleet.Ids())
        {
            var r = R(wh, id); ParkRecord p;
            if (!wh.Fleet.TryGetPark(id, out p)) p = new ParkRecord { Region = r.Region, Fuel = -1 };
            int committed = wh.Travel.CommittedRegion(id, r.Region);
            if (p.Region != committed) { p.HasPose = false; p.Region = committed; }
            fs.Cars.Add(new FleetSaveCar { Id = id, Kind = r.Kind, Unlocked = r.Unlocked, Region = committed, Park = p });
        }
        return fs;
    }

    static void WrongRegionSnapshots()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        var v = new FleetView(); v.Refresh(wh.Reg);
        VehicleRecord gt; v.TryGet(GT, out gt);
        Expect(FleetView.SenderSimulates(gt, 2, Hillville), "client streams its Hillville car");
        Expect(!FleetView.SenderSimulates(gt, 2, Madison), "same car claimed in Madison: refused");
        VehicleRecord rg; v.TryGet(Ranger, out rg);
        Expect(!FleetView.SenderSimulates(rg, 2, Madison), "client cannot stream the host's car");
        var f = new VehicleSnapshotFilter();
        var s = new VehicleSnapshot { Id = GT, Seq = 1, Region = Hillville, Rot = Quat.Identity };
        Expect(f.Check(s, true, FleetView.SenderSimulates(gt, 2, s.Region), Madison) == VehicleVerdict.WrongRegion, "host in Madison: not rendered (still ingested for persistence)");
    }

    static void ClientAuthority()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        uint rev; int sr; float sh;
        var forged = new StateEntry { Scope = StateScope.Player, ScopeArg = 1, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = 1, Car = Ranger, Pose = Pose(Madison, 0, 1) }.ToBytes() };
        Expect(wh.HandleProposal("client-1", 2, 70, forged, 0, out rev, out sr, out sh) == ProposalCode.NotAuthorized, "request on behalf of the host refused");
        var bad = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new byte[5] };
        Expect(wh.HandleProposal("client-1", 2, 71, bad, 0, out rev, out sr, out sh) == ProposalCode.Invalid, "malformed request");
        var jail = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = Jail, Pose = new ParkRecord { Region = Regions.None, Fuel = -1 } }.ToBytes() };
        Expect(wh.HandleProposal("client-1", 2, 72, jail, 0, out rev, out sr, out sh) == ProposalCode.Invalid, "jail is not a menu destination");
        TravelGrant g;
        ParkRecord before; wh.Fleet.TryGetPark(Ranger, out before);
        Expect(ClientTravel(wh, 73, 1, Ranger, Pose(Madison, 999, 1), out g) == ProposalCode.Accepted && g.Outcome == TravelOutcome.None, "client cannot carry the host's car: " + g);
        ParkRecord after; bool has = wh.Fleet.TryGetPark(Ranger, out after);
        Expect(!has || after.Pos.X != 999, "pose of a car the client does not simulate is ignored");
        Expect(R(wh, Ranger).Region == Madison, "host's car untouched");
    }

    static void DuplicateDetection()
    {
        var wh = Setup(Madison, Hillville, Madison, Regions.None);
        Expect(wh.Fleet.Register(GT, 0, true, wh.Outbox) == ProposalCode.Invalid, "same id with another carNr refused");
        var fs = new FleetSave { Profile = 1 };
        fs.Cars.Add(new FleetSaveCar { Id = Ranger, Kind = 0, Unlocked = true, Region = Madison, Park = new ParkRecord { Region = Madison, Fuel = -1 } });
        fs.Cars.Add(new FleetSaveCar { Id = Ranger, Kind = 0, Unlocked = true, Region = Hideout, Park = new ParkRecord { Region = Hideout, Fuel = -1 } });
        FleetSave o; string err;
        Expect(!FleetSave.TryParse(fs.Serialize(), out o, out err) && err.Contains("duplicate"), "duplicate id in the file refused: " + err);
        fs.Cars.Clear();
        fs.Cars.Add(new FleetSaveCar { Id = Ranger, Kind = 0, Unlocked = true, Region = Madison, Park = new ParkRecord { Region = Madison, Fuel = -1 } });
        fs.Cars.Add(new FleetSaveCar { Id = GT, Kind = 1, Unlocked = true, Region = Madison, Park = new ParkRecord { Region = Madison, Fuel = -1 } });
        Expect(!FleetSave.TryParse(fs.Serialize(), out o, out err) && err.Contains("two cars"), "two cars in one region refused: " + err);
        fs.Cars[1].Kind = 0;
        Expect(!FleetSave.TryParse(fs.Serialize(), out o, out err), "id/kind mismatch refused");
    }

    static void ContinuationOnce()
    {
        var guard = new OneShot();
        object methodA = "SelectHillville", methodB = "SelectTestMap";
        int originalRuns = 0;
        Func<object, bool> prefix = m => guard.TryConsume(m);   // true = run the original
        Expect(!prefix(methodA), "first click held");
        guard.Arm(methodA);
        Expect(!prefix(methodB), "a different travel method is not released by the guard");
        if (prefix(methodA)) originalRuns++;
        if (prefix(methodA)) originalRuns++;   // re-entry / double click
        Expect(originalRuns == 1 && !guard.Armed, "original resumed exactly once");
        guard.Arm(methodA); guard.Disarm();
        Expect(!prefix(methodA), "disarmed guard (failed resume) releases nothing");
        var gate = new TravelGate();
        gate.Begin(5, Hillville, 0);
        int resumes = 0;
        if (gate.OnResult(5, true) == GateAction.Resume) resumes++;
        if (gate.OnResult(5, true) == GateAction.Resume) resumes++;   // duplicated result delivery
        Expect(resumes == 1, "one grant resumes once");
    }

    static void TravelOnFoot()
    {
        var wh = Setup(Hideout, Hideout, Madison, Regions.None);
        var g = HostTravel(wh, Hillville, VehicleIds.None);
        Expect(g.Outcome == TravelOutcome.None && g.Car == VehicleIds.None, "no car: " + g);
        Arrive(wh, 1, Hillville);
        Expect(wh.Fleet.VehiclesIn(Hillville).Count == 0 && R(wh, Ranger).Region == Hideout && R(wh, GT).Region == Hideout, "no car created, both stay in the garage");
        var wh2 = Setup(Madison, Hideout, Madison, Madison);
        var gc = ClientTravel(wh2, Hillville, Ranger);
        Expect(gc.Outcome == TravelOutcome.None && R(wh2, Ranger).Region == Madison, "the region car stays with the player who stays: " + gc);
    }

    static void FuelSeparation()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        wh.Fleet.SetPark(Ranger, Pose(Madison, 1, 50), wh.Outbox);
        wh.Fleet.SetPark(GT, Pose(Hillville, 2, 20), wh.Outbox);
        TravelGrant g;
        ClientTravel(wh, 80, Hideout, GT, Pose(Hillville, 3, 12.5f), out g);   // client drove the GT down to 12.5 and leaves with it
        ParkRecord pr, pg;
        Expect(wh.Fleet.TryGetPark(Ranger, out pr) && pr.Fuel == 50, "Ranger fuel untouched by GT driving");
        Expect(wh.Fleet.TryGetPark(GT, out pg) && pg.Fuel == 12.5f, "GT fuel updated from its own report");
        Arrive(wh, 2, Hideout);
        Expect(wh.Fleet.TryGetPark(GT, out pg) && pg.Fuel == 12.5f, "travel does not reset fuel");
        var s = new VehicleSnapshot { Id = GT, Seq = 1, Region = Hideout, Rot = Quat.Identity, Fuel = 61f };
        var w = new NetDataWriter(); s.Write(w); var r = new NetDataReader(w.CopyData()); r.GetByte();
        VehicleSnapshot o; Expect(VehicleSnapshot.TryRead(r, out o) && Math.Abs(o.Fuel - 61f) < 0.01f, "fuel carried per car in snapshots");
        var p = new ParkRecord { Region = Madison, Fuel = 101f };
        Expect(!p.IsValid, "implausible fuel refused");
    }

    static void FastTravelGuard()
    {
        Expect(!TravelRules.FastTravelAllowed(Hillville, false), "no own car in Hillville: blocked (no charge)");
        Expect(TravelRules.FastTravelAllowed(Hillville, true), "own active car: allowed");
        Expect(TravelRules.FastTravelAllowed(Museum, false), "heist region: original behaviour");
        Expect(!TravelRules.FastTravelAllowed(Hideout, false), "hideout with only frozen access copies: blocked");
    }

    static void SpawnSelection()
    {
        var c = new List<Vec3>();
        SpawnPlanner.Ring(new Vec3(0, 0, 0), 0f, c);
        Expect(c.Count == SpawnPlanner.Radii.Length * SpawnPlanner.Angles, "24 candidates");
        Expect(Math.Abs(Vec3.HorizontalDistance(c[0], new Vec3(0, 0, 0)) - 2f) < 1e-4f && Math.Abs(Vec3.HorizontalDistance(c[c.Count - 1], new Vec3(0, 0, 0)) - 4.5f) < 1e-4f, "nearest ring first");
        // a wall at x > 1 and the other player at (0,0,2): first safe candidate is clear of both and nearest
        int i = SpawnPlanner.Choose(c, p => p.X <= 1f && Vec3.HorizontalDistance(p, new Vec3(0, 0, 2)) >= 1.2f);
        Expect(i >= 0 && c[i].X <= 1f && Vec3.HorizontalDistance(c[i], new Vec3(0, 0, 2)) >= 1.2f && Vec3.HorizontalDistance(c[i], new Vec3(0, 0, 0)) < 2.01f, "first safe candidate: " + c[i]);
        Expect(SpawnPlanner.Choose(c, p => false) == -1, "nothing safe -> fallback path");
    }

    static void FleetFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ts2mp-fleet-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FleetStore(dir, 3);
            FleetSave s; string info;
            Expect(!store.TryLoad(out s, out info) && info == "missing", "missing file");
            var wh = Setup(Madison, Hillville, Madison, Hillville);
            wh.Fleet.SetPark(GT, Pose(Hillville, 7, 22), wh.Outbox);
            ClientTravel(wh, 1, GT);   // reservation in progress: must not be persisted
            var fs = Export(wh, 3, 4, 20);
            var gtSaved = fs.Cars.Find(c => c.Id == GT);
            Expect(gtSaved.Region == Hillville && gtSaved.Park.HasPose && gtSaved.Park.Fuel == 22, "uncommitted reservation persisted as the previous (committed) state");
            store.WriteAtomic(fs.Serialize());
            Expect(store.TryLoad(out s, out info) && info == "loaded" && s.Cars.Count == 2 && s.Profile == 3, "round trip");
            fs.Story = 5; store.WriteAtomic(fs.Serialize());
            Expect(File.Exists(store.Path + ".bak"), "previous version kept by the atomic replace");
            var b = File.ReadAllBytes(store.Path); b[20] ^= 0xFF; File.WriteAllBytes(store.Path, b);   // corrupt main file
            Expect(store.TryLoad(out s, out info) && s.Story == 4 && info.Contains("backup"), "checksum failure -> backup: " + info);
            var v2 = fs.Serialize(); v2[8] = 9;   // version field
            FleetSave o; string err;
            Expect(!FleetSave.TryParse(v2, out o, out err), "unsupported version / tampering refused");
            Expect(fs.StaleReason(3, 5, 20) == null, "same history");
            Expect(fs.StaleReason(3, 1, 20) != null, "older story: other history (new game in this slot)");
            Expect(fs.StaleReason(3, 5, 5) != null, "older day: other history");
            Expect(fs.StaleReason(4, 5, 20) != null, "other profile");
            var q = store.Quarantine("stale");
            Expect(q != null && File.Exists(q) && !File.Exists(store.Path), "quarantined, not deleted");
            var writer = new FleetWriter(store);
            for (int i = 0; i < 20; i++) { fs.DayIndex = 100 + i; writer.Queue(fs.Serialize()); }
            Expect(writer.Flush(5000) && writer.Failures == 0, "background writer flushes");
            Expect(store.TryLoad(out s, out info) && s.DayIndex == 119, "newest payload written last (coalesced " + writer.Writes + " writes)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    static void HolderHandoff()
    {
        var wh = Setup(Madison, Hideout, Madison, Madison);
        Expect(R(wh, Ranger).Holder == 1, "host simulates the shared Madison car");
        var g = HostTravel(wh, Hillville, Ranger);
        Expect(g.Outcome == TravelOutcome.None, "host may not take the car away from the client who stays: " + g);
        Arrive(wh, 1, Hillville);
        Expect(R(wh, Ranger).Holder == 2 && R(wh, Ranger).Region == Madison, "client now simulates the Ranger where it was left");
        wh.SetPlayerRegion(2, Regions.None, 5);
        Expect(R(wh, Ranger).Holder == 0, "nobody left: parked logically");
        var w2 = Setup(Hideout, Hideout, Hideout, Hideout);
        Expect(w2.Fleet.VehiclesIn(Hideout).Count == 2, "garage stores both");
        var gh = HostTravel(w2, Madison, GT);
        Expect(gh.Outcome == TravelOutcome.Carry, "host leaves the garage with the GT although the client stays (garage exception): " + gh);
        Arrive(w2, 1, Madison);
        Expect(R(w2, Ranger).Holder == 2 && R(w2, GT).Region == Madison, "client simulates the Ranger left in the garage");
    }

    static CarPresentation P(WorldHost wh, ushort id, int localRegion, byte slot) { return PresentationRules.Decide(R(wh, id), localRegion, slot); }

    static void HideoutHostLeaves()
    {
        var wh = Setup(Hideout, Hideout, Hideout, Hideout);
        wh.Fleet.Register(GT, 1, false, wh.Outbox);   // GT not bought
        Expect(P(wh, Ranger, Hideout, 1) == CarPresentation.Real && P(wh, Ranger, Hideout, 2) == CarPresentation.ParkedOther, "both home: host real, client parked copy (E travel)");
        var g = HostTravel(wh, Madison, Ranger);
        Expect(g.Outcome == TravelOutcome.Carry, "host takes the Ranger: " + g);
        Expect(P(wh, Ranger, Hideout, 2) == CarPresentation.AwayProxy, "client keeps a visible travel proxy while the car is away (was: deactivated)");
        Arrive(wh, 1, Madison);
        Expect(P(wh, Ranger, Hideout, 2) == CarPresentation.AwayProxy && PresentationRules.EnterOpensTravelMenu(CarPresentation.AwayProxy), "still a travel access point after the host arrived");
        Expect(P(wh, GT, Hideout, 2) == CarPresentation.Hidden, "unbought GT not shown as owned");
    }

    static void ProxyTravelNoCarry()
    {
        // client (hideout) -> Madison through the proxy: the request carries no car (it simulates none)
        var wh = Setup(Hideout, Hideout, Hideout, Hideout);
        HostTravel(wh, Madison, Ranger); Arrive(wh, 1, Madison);
        var g = ClientTravel(wh, Madison, VehicleIds.None);
        Expect(g.Outcome == TravelOutcome.Join && g.Car == Ranger, "client joins the host's Ranger: " + g);
        Arrive(wh, 2, Madison);
        Expect(wh.Fleet.VehiclesIn(Madison).Count == 1 && R(wh, GT).Region == Hideout, "no second car brought, GT stays home");
        // even naming the proxy's car (stale current_vehicle) never carries it: it is not in the traveller's region
        var w2 = Setup(Hideout, Hideout, Hideout, Hideout);
        HostTravel(w2, Madison, Ranger); Arrive(w2, 1, Madison);
        var g2 = ClientTravel(w2, Hillville, Ranger);
        Expect(g2.Outcome == TravelOutcome.None && R(w2, Ranger).Region == Madison, "proxy car never moved: " + g2);
        // reverse roles: client takes the GT away (host gone first), host stays home and travels via its proxy
        var w3 = Setup(Hideout, Hideout, Madison, Hideout);   // host elsewhere: client simulates both garage cars
        Expect(R(w3, GT).Holder == 2, "client simulates the garage cars while the host is away");
        var gc = ClientTravel(w3, Hillville, GT);
        Expect(gc.Outcome == TravelOutcome.Carry, "client takes the GT: " + gc);
        Arrive(w3, 2, Hillville);
        HostTravel(w3, Hideout, VehicleIds.None); Arrive(w3, 1, Hideout);
        Expect(P(w3, GT, Hideout, 1) == CarPresentation.AwayProxy && P(w3, Ranger, Hideout, 1) == CarPresentation.Real, "host home: GT proxy, Ranger real");
        var gh = HostTravel(w3, Hillville, VehicleIds.None);
        Expect(gh.Outcome == TravelOutcome.Join && gh.Car == GT && R(w3, Ranger).Region == Hideout, "host joins the client's GT without a second car: " + gh);
    }

    static void ReturnRestores()
    {
        var wh = Setup(Hideout, Hideout, Hideout, Hideout);
        HostTravel(wh, Madison, Ranger); Arrive(wh, 1, Madison);
        Expect(P(wh, Ranger, Hideout, 2) == CarPresentation.AwayProxy, "away");
        var g = HostTravel(wh, Hideout, Ranger);
        Expect(g.Outcome == TravelOutcome.Carry, "host returns with the Ranger (garage): " + g);
        Arrive(wh, 1, Hideout);
        Expect(P(wh, Ranger, Hideout, 1) == CarPresentation.Real, "host: real car");
        Expect(P(wh, Ranger, Hideout, 2) == CarPresentation.ParkedOther && !PresentationRules.IsTravelProxy(CarPresentation.Real), "client: no longer an away proxy, follows the real car");
        Expect(R(wh, GT).Holder == 2 && P(wh, GT, Hideout, 1) == CarPresentation.ParkedOther, "the GT the client simulated meanwhile stays with the client (symmetric)");
    }

    static void BothHome()
    {
        var wh = Setup(Hideout, Hideout, Hideout, Hideout);
        foreach (var id in new[] { Ranger, GT })
        {
            Expect(P(wh, id, Hideout, 1) == CarPresentation.Real, "host simulates " + id);
            Expect(P(wh, id, Hideout, 2) == CarPresentation.ParkedOther, "client: physically present copy, not an away proxy");
        }
        Expect(wh.Fleet.Claim(Ranger, 2, Hideout, wh.Outbox) == ProposalCode.NotAuthorized, "driver claim rules: the client cannot take the car the host simulates (handoff pending)");
        Expect(wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox) == ProposalCode.Accepted && wh.Fleet.Claim(Ranger, 1, Hideout, wh.Outbox) == ProposalCode.Unchanged, "one driver");
    }

    static void TwoAwayProxies()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        wh.SetPlayerRegion(1, Hideout, 1);   // host walks home
        Expect(P(wh, Ranger, Hideout, 1) == CarPresentation.AwayProxy && P(wh, GT, Hideout, 1) == CarPresentation.AwayProxy, "both away -> two travel proxies");
        Expect(wh.Fleet.VehiclesIn(Hideout).Count == 0 && wh.Fleet.Ids().Count == 2, "proxies are not fleet vehicles");
        var g = HostTravel(wh, Hillville, VehicleIds.None);
        Expect(g.Outcome == TravelOutcome.Join && g.Car == GT && R(wh, Ranger).Region == Madison, "travel toward the GT without taking the Ranger: " + g);
        Expect(Ranger != GT && VehicleIds.ToCarNr(Ranger) == 0 && VehicleIds.ToCarNr(GT) == 1, "separate identities");
    }

    static void ProxyRestrictions()
    {
        foreach (var p in new[] { CarPresentation.AwayProxy, CarPresentation.ParkedOther })
            Expect(!PresentationRules.AllowsDriving(p) && !PresentationRules.AllowsStorage(p), "proxy " + p + ": no driving/storage without a handoff");
        Expect(PresentationRules.EnterOpensTravelMenu(CarPresentation.AwayProxy) && !PresentationRules.EnterOpensTravelMenu(CarPresentation.ParkedOther), "M4.2.2: only the away proxy opens the travel menu");
        Expect(PresentationRules.AllowsDriving(CarPresentation.Real) && PresentationRules.AllowsStorage(CarPresentation.Real) && !PresentationRules.EnterOpensTravelMenu(CarPresentation.Real), "real car: original interactions");
        var wh = Setup(Madison, Hideout, Madison, Hideout);
        Expect(wh.Fleet.Claim(Ranger, 2, Hideout, wh.Outbox) == ProposalCode.NotAuthorized, "lease through the away proxy refused");
        wh.Fleet.Register(GT, 1, false, wh.Outbox);
        Expect(P(wh, GT, Hideout, 2) == CarPresentation.Hidden && P(wh, GT, Hideout, 1) == CarPresentation.Hidden, "unbought GT hidden on both");
        Expect(P(wh, Ranger, Madison, 2) == CarPresentation.RemoteVisual, "outside the hideout: other player's car = visual only");
        Expect(P(wh, Ranger, Hillville, 2) == CarPresentation.Hidden && P(wh, Ranger, Museum, 1) == CarPresentation.Hidden, "elsewhere / mission region: not shown");
    }

    static void SequenceRestart()
    {
        var wh = Setup(Hideout, Hideout, Hideout, Hideout);
        var view = new FleetView(); var tracker = new FleetChangeTracker(); var filter = new VehicleSnapshotFilter();
        view.Refresh(wh.Reg); Expect(tracker.Changed(view.All) == null, "first view: nothing to forget");
        VehicleRecord r; view.TryGet(Ranger, out r);
        var s = new VehicleSnapshot { Id = Ranger, Seq = 500, Region = Hideout, Rot = Quat.Identity };
        Expect(filter.Check(s, true, FleetView.SenderSimulates(r, 1, Hideout), Hideout) == VehicleVerdict.Accept, "host streams");
        HostTravel(wh, Madison, Ranger); Arrive(wh, 1, Madison);
        HostTravel(wh, Hideout, Ranger); Arrive(wh, 1, Hideout);   // scene reloads: the old plugin restarted at seq 1
        view.Refresh(wh.Reg);
        var changed = tracker.Changed(view.All);
        Expect(changed == null || !changed.Contains(Ranger), "Ranger net record identical after the round trip -> the sender-side sequence must survive reloads (fixed in VehicleSync)");
        // a real change (new holder) forgets the sequence: a restarted sender is accepted again
        wh.SetPlayerRegion(1, Madison, 9); view.Refresh(wh.Reg);
        changed = tracker.Changed(view.All);
        Expect(changed != null && changed.Contains(Ranger), "holder/region change detected");
        foreach (var id in changed) filter.Forget(id);
        view.TryGet(Ranger, out r);
        var s2 = new VehicleSnapshot { Id = Ranger, Seq = 1, Region = Hideout, Rot = Quat.Identity };
        Expect(filter.Check(s2, true, FleetView.SenderSimulates(r, 2, Hideout), Hideout) == VehicleVerdict.Accept, "new holder's seq 1 accepted");
        var stale = new VehicleSnapshotFilter(); stale.Check(s, true, true, Hideout);
        Expect(stale.Check(s2, true, true, Hideout) == VehicleVerdict.Stale, "without forgetting, a restarted sequence is dropped (the observed Bug B)");
    }

    static void ManyTransitions()
    {
        var wh = Setup(Hideout, Hideout, Hideout, Hideout);
        var seen = new List<CarPresentation>();
        for (int i = 0; i < 5; i++)
        {
            HostTravel(wh, Madison, Ranger); Arrive(wh, 1, Madison); seen.Add(P(wh, Ranger, Hideout, 2));
            HostTravel(wh, Hideout, Ranger); Arrive(wh, 1, Hideout); seen.Add(P(wh, Ranger, Hideout, 2));
            Invariant(wh);
        }
        for (int i = 0; i < seen.Count; i++) Expect(seen[i] == (i % 2 == 0 ? CarPresentation.AwayProxy : CarPresentation.ParkedOther), "transition " + i + ": " + seen[i]);
        Expect(wh.Fleet.Ids().Count == 2 && wh.Travel.Pending == 0, "no stale reservations or extra vehicles");
        HostTravel(wh, Madison, Ranger); Arrive(wh, 1, Madison);
        wh.PlayerDisconnected(2, 50);
        Expect(R(wh, GT).Holder == 0 && R(wh, GT).Region == Hideout, "client gone: GT parked in the garage");
        var cli = StateRegistry.CreateClient();
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
        var rd = new NetDataReader(w.CopyData()); rd.GetByte();
        uint e, h; List<StateEntry> list; StateMessages.TryReadSnapshot(rd, out e, out h, out list); cli.LoadSnapshot(e, h, list);
        wh.SetPlayerRegion(2, Hideout, 51);
        var v = new FleetView(); v.Refresh(wh.Reg);
        VehicleRecord rr; v.TryGet(Ranger, out rr); VehicleRecord rg; v.TryGet(GT, out rg);
        Expect(PresentationRules.Decide(rr, Hideout, 2) == CarPresentation.AwayProxy && PresentationRules.Decide(rg, Hideout, 2) == CarPresentation.Real, "reconnect + hideout reload: Ranger proxy, GT real for the client");
    }

    static void InvalidAndRefused()
    {
        var unplaced = new VehicleRecord { Kind = 0, Flags = VehicleFlags.Unlocked, Region = Regions.None, Holder = 0 };
        Expect(PresentationRules.Decide(unplaced, Hideout, 2) == CarPresentation.Hidden, "unplaced record: nothing shown (no guessed car)");
        Expect(PresentationRules.Decide(unplaced, Regions.None, 2) == CarPresentation.Hidden, "not in a region: nothing");
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        var gate = new TravelGate(); gate.Begin(500, 1, 0);
        Expect(gate.OnResult(500, false) == GateAction.Denied, "refused -> no resume");
        Expect(R(wh, GT).Region == Hillville && !R(wh, GT).Reserved && wh.Travel.Pending == 0, "refusal: nothing reserved or moved");
        uint rev; int sr; float sh;
        var bad = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new byte[3] };
        Expect(wh.HandleProposal("client-1", 2, 900, bad, 0, out rev, out sr, out sh) == ProposalCode.Invalid && wh.Fleet.VehiclesIn(1).Count == 0, "invalid request: no car anywhere new");
        var cli = StateRegistry.CreateClient();
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
        var rd = new NetDataReader(w.CopyData()); rd.GetByte();
        uint e, h; List<StateEntry> list; StateMessages.TryReadSnapshot(rd, out e, out h, out list); cli.LoadSnapshot(e, h, list);
        HostTravel(wh, 1, Ranger);
        var delta = wh.Outbox[wh.Outbox.Count - 1];
        Expect(cli.ApplyDelta(wh.Reg.Epoch, delta) == DeltaResult.Gap, "delayed/missing deltas detected -> resync instead of a stale presentation");
    }

    static void SpecialAndCodecs()
    {
        var wh = Setup(Madison, Hillville, Madison, Hillville);
        var g = HostTravel(wh, Museum, Ranger);
        Expect(g.Outcome == TravelOutcome.Special && R(wh, Ranger).Region == Madison && wh.Travel.Pending == 0, "heist: original behaviour, fleet untouched: " + g);
        var req = new TravelRequest { Dest = Hillville, Car = GT, Pose = Pose(Hillville, 1.5f, 9f) };
        TravelRequest rq; Expect(TravelRequest.TryParse(req.ToBytes(), out rq) && rq.Dest == Hillville && rq.Car == GT && rq.Pose.HasPose && rq.Pose.Fuel == 9f && req.ToBytes().Length == 35, "request 35 B");
        var gr = new TravelGrant { Id = 77, Dest = Madison, Outcome = TravelOutcome.Parked, Car = Ranger };
        TravelGrant go; Expect(TravelGrant.TryParse(gr.ToBytes(), out go) && go.Id == 77 && go.Outcome == TravelOutcome.Parked && gr.ToBytes().Length == 11, "grant 11 B");
        var bad = gr.ToBytes(); bad[8] = 9; Expect(!TravelGrant.TryParse(bad, out go), "unknown outcome refused");
        ParkRecord pk; Expect(ParkRecord.TryParse(Pose(Madison, 3, 4).ToBytes(), out pk) && Pose(Madison, 3, 4).ToBytes().Length == ParkRecord.WireSize, "park 29 B");
        var nanPose = Pose(Madison, float.NaN, 4); Expect(!nanPose.IsValid, "NaN pose refused");
        var pol = new ParkPolicy(); int commits = 0;
        for (int i = 0; i < 600; i++) { double t = i / 60.0; if (pol.ShouldCommit(t, new Vec3((float)t * 10, 0, 0), Quat.Identity, 50f, 3)) commits++; }
        Expect(commits >= 5 && commits <= 6, "driving: ≤ one parking commit per 2 s: " + commits);
        Expect(pol.ShouldCommit(10.01, new Vec3(100, 0, 0), Quat.Identity, 50f, 0), "engine off / stopped: immediate commit");
        Expect(!pol.ShouldCommit(30, new Vec3(100, 0, 0), Quat.Identity, 50f, 0), "parked and unchanged: no commit");
        Console.WriteLine("      sizes: request 35 B, grant 11 B, park 29 B, vehicle record 9 B, snapshot 39 B");
    }
}
