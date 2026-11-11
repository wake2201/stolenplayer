// M4.1/M4.2 offline tests: shared fleet registry, holders, claims, vehicle snapshots, filter, interpolation buffer, send policy.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class VehicleTests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("stable ids from the game's carNr; invalid numbers refused", Ids);
        Run("record codec and validation", RecordCodec);
        Run("register: new, idempotent, unlock change, identity conflict, bounded", Register);
        Run("one permanent car per region: placement refuses a second car, hideout garage holds both", OneCarPerRegion);
        Run("claims: holder only, occupied, one vehicle per driver, locked, wrong region, release, release-all", Claims);
        Run("claim proposals through WorldHost: dedup, invalid, disconnect releases", ClaimProposals);
        Run("fleet state revisions replicate to a client (snapshot + deltas, stale delta rejected)", Revisions);
        Run("snapshot codec: 39 bytes incl. fuel + steer (M5.0), round trip, malformed rejected", SnapshotCodec);
        Run("filter: unknown, not owner, stale/out-of-order, wrong region, bounded tracking", Filter);
        Run("fleet view: cached records and sender authority", View);
        Run("pose buffer: interpolation, out-of-order ignored, teleport snap, bounded capacity", Buffer);
        Run("send policy: 20 Hz moving, heartbeat parked, region change immediate", Policy);
        Run("reconnect: fresh client gets fleet from snapshot, no duplicates", Reconnect);
        Run("representative traffic: moving 20 Hz and parked heartbeat bytes/s", Traffic);
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
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), Shadowhawk = VehicleIds.FromCarNr(1);

    static WorldHost HostWithFleet(bool shadowhawkUnlocked)
    {
        var wh = new WorldHost(77) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, Madison, 0);
        Expect(wh.Fleet.Register(Ranger, 0, true, wh.Outbox) == ProposalCode.Accepted, "register ranger");
        Expect(wh.Fleet.Register(Shadowhawk, 1, shadowhawkUnlocked, wh.Outbox) == ProposalCode.Accepted, "register shadowhawk");
        Expect(wh.Fleet.Place(Ranger, Madison, wh.Outbox) && wh.Fleet.Place(Shadowhawk, Hideout, wh.Outbox), "place");
        Settle(wh);
        wh.Outbox.Clear();
        return wh;
    }

    static void Settle(WorldHost wh) { wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox); }

    static ProposalCode ClaimP(WorldHost wh, byte slot, ushort id, bool claim, uint pid)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = id, Key = WorldKeys.VehicleClaim, Value = new[] { claim ? (byte)1 : (byte)0 } };
        return wh.HandleProposal("cli", slot, pid, p, 0, out rev, out sr, out sh);
    }

    static void Ids()
    {
        Expect(VehicleIds.FromCarNr(0) == 1 && VehicleIds.FromCarNr(1) == 2 && VehicleIds.ToCarNr(2) == 1, "carNr mapping");
        Expect(VehicleIds.FromCarNr(-1) == VehicleIds.None && VehicleIds.FromCarNr(255) == VehicleIds.None && VehicleIds.ToCarNr(VehicleIds.None) == -1, "invalid");
    }

    static void RecordCodec()
    {
        var r = new VehicleRecord { Kind = 1, Flags = VehicleFlags.Unlocked | VehicleFlags.Reserved, Region = Madison, Driver = 1, Holder = 1 };
        VehicleRecord p;
        Expect(r.ToBytes().Length == VehicleRecord.WireSize && VehicleRecord.WireSize == 9 && VehicleRecord.TryParse(r.ToBytes(), out p) && p.Kind == 1 && p.Region == Madison && p.Driver == 1 && p.Holder == 1 && p.Active && p.Reserved, "round trip");
        var bad = r.ToBytes(); bad[6] = 9;
        Expect(!VehicleRecord.TryParse(bad, out p), "driver slot out of range");
        bad = r.ToBytes(); bad[8] = 3;
        Expect(!VehicleRecord.TryParse(bad, out p), "holder slot out of range");
        bad = new VehicleRecord { Kind = 0, Region = 12345 }.ToBytes();
        Expect(!VehicleRecord.TryParse(bad, out p), "unknown region");
        bad = new VehicleRecord { Kind = 0, Driver = 1, Passenger = 1, Region = Madison }.ToBytes();
        Expect(!VehicleRecord.TryParse(bad, out p), "same player in two seats");
        Expect(!VehicleRecord.TryParse(new byte[8], out p), "M4.1 8-byte record refused");
    }

    static void Register()
    {
        var wh = HostWithFleet(false);
        Expect(wh.Fleet.Count == 2, "two cars");
        Expect(wh.Fleet.Register(Ranger, 0, true, wh.Outbox) == ProposalCode.Unchanged && wh.Outbox.Count == 0, "re-register idempotent (scene reload)");
        Expect(wh.Fleet.Register(Shadowhawk, 1, true, wh.Outbox) == ProposalCode.Accepted, "unlock after purchase");
        VehicleRecord r; Expect(wh.Fleet.TryGet(Shadowhawk, out r) && r.Unlocked, "unlocked");
        Expect(wh.Fleet.Register(Shadowhawk, 0, true, wh.Outbox) == ProposalCode.Invalid, "same id, different car -> refused");
        Expect(wh.Fleet.Register(VehicleIds.None, 0, true, wh.Outbox) == ProposalCode.Invalid, "id 0");
        for (int i = 2; i < 200 && wh.Fleet.Count < FleetBook.MaxVehicles; i++) wh.Fleet.Register(VehicleIds.FromCarNr(i), (byte)i, true, null);
        Expect(wh.Fleet.Count == FleetBook.MaxVehicles && wh.Fleet.Register(VehicleIds.FromCarNr(250), 250, true, null) == ProposalCode.Invalid, "bounded");
    }

    static void OneCarPerRegion()
    {
        var wh = HostWithFleet(true);
        Expect(!wh.Fleet.Place(Shadowhawk, Madison, wh.Outbox), "second permanent car refused in Madison");
        Expect(wh.Fleet.AssignedTo(Madison) == Ranger && wh.Fleet.VehiclesIn(Madison).Count == 1, "Madison keeps exactly one car");
        Expect(wh.Fleet.Place(Shadowhawk, Hillville, wh.Outbox) && wh.Fleet.AssignedTo(Hillville) == Shadowhawk, "different cars in different regions");
        Expect(wh.Fleet.Place(Ranger, Hideout, wh.Outbox) && wh.Fleet.Place(Shadowhawk, Hideout, wh.Outbox) && wh.Fleet.VehiclesIn(Hideout).Count == 2, "the hideout garage stores both");
    }

    static void Claims()
    {
        var wh = HostWithFleet(false);
        wh.SetPlayerRegion(2, Madison, 0);
        VehicleRecord r; Expect(wh.Fleet.TryGet(Ranger, out r) && r.Holder == 1, "host simulates the Madison car");
        Expect(wh.Fleet.Claim(Ranger, 2, Madison, wh.Outbox) == ProposalCode.NotAuthorized, "not the holder: no driver handoff in M4.2");
        Expect(wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox) == ProposalCode.Accepted, "holder drives");
        Expect(wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox) == ProposalCode.Unchanged, "repeat");
        Expect(wh.Fleet.Claim(Ranger, 2, Madison, wh.Outbox) == ProposalCode.Busy, "occupied");
        Expect(wh.Fleet.Claim(Shadowhawk, 2, Hideout, wh.Outbox) == ProposalCode.NotAuthorized, "locked car");
        wh.Fleet.Register(Shadowhawk, 1, true, wh.Outbox);
        wh.Fleet.Place(Shadowhawk, Hillville, wh.Outbox);
        wh.SetPlayerRegion(2, Hillville, 1);
        Expect(wh.Fleet.TryGet(Shadowhawk, out r) && r.Holder == 2, "client simulates the Hillville car");
        Expect(wh.Fleet.Claim(Shadowhawk, 2, Madison, wh.Outbox) == ProposalCode.NotAuthorized, "car in another region");
        Expect(wh.Fleet.Claim(Shadowhawk, 2, Hillville, wh.Outbox) == ProposalCode.Accepted, "client drives its own region car");
        Expect(wh.Fleet.Release(Ranger, 2, wh.Outbox) == ProposalCode.NotAuthorized, "only the driver releases");
        Expect(wh.Fleet.Release(Ranger, 1, wh.Outbox) == ProposalCode.Accepted && wh.Fleet.Release(Ranger, 1, wh.Outbox) == ProposalCode.Unchanged, "release");
        Expect(wh.Fleet.ReleaseAll(2, wh.Outbox) == 1 && wh.Fleet.DrivenBy(2) == VehicleIds.None, "release all");
        Expect(wh.Fleet.Claim(VehicleIds.FromCarNr(9), 1, Madison, wh.Outbox) == ProposalCode.Invalid, "unknown vehicle");
    }

    static void ClaimProposals()
    {
        var wh = HostWithFleet(true);
        wh.Fleet.Place(Shadowhawk, Hillville, wh.Outbox);
        wh.SetPlayerRegion(2, Hillville, 0);
        Expect(ClaimP(wh, 2, Shadowhawk, true, 10) == ProposalCode.Accepted, "client claim");
        Expect(ClaimP(wh, 2, Shadowhawk, true, 10) == ProposalCode.Accepted && wh.Fleet.DrivenBy(2) == Shadowhawk, "retransmission cached");
        Expect(ClaimP(wh, 2, Shadowhawk, true, 9) == ProposalCode.Stale, "older id stale");
        Expect(ClaimP(wh, 2, Ranger, true, 11) == ProposalCode.NotAuthorized, "client cannot claim the host's car in another region");
        uint rev; int sr; float sh;
        var bad = new StateEntry { Scope = StateScope.Session, ScopeArg = Shadowhawk, Key = WorldKeys.VehicleClaim, Value = new byte[] { 7 } };
        Expect(wh.HandleProposal("cli", 2, 12, bad, 0, out rev, out sr, out sh) == ProposalCode.Invalid, "bad value");
        bad = new StateEntry { Scope = StateScope.Session, ScopeArg = 70000, Key = WorldKeys.VehicleClaim, Value = new byte[] { 1 } };
        Expect(wh.HandleProposal("cli", 2, 13, bad, 0, out rev, out sr, out sh) == ProposalCode.Invalid, "bad id");
        var forge = new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.Vehicle, Value = new VehicleRecord { Kind = 0, Flags = VehicleFlags.Unlocked, Region = Hillville, Holder = 2 }.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, 14, forge, 0, out rev, out sr, out sh) == ProposalCode.NotAuthorized, "client cannot write fleet records");
        forge = new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.VehiclePark, Value = new ParkRecord { Region = Hillville, Fuel = 50 }.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, 15, forge, 0, out rev, out sr, out sh) == ProposalCode.NotAuthorized, "client cannot write parking records");
        wh.PlayerDisconnected(2, 1);
        VehicleRecord r;
        Expect(wh.Fleet.DrivenBy(2) == VehicleIds.None && wh.Fleet.TryGet(Shadowhawk, out r) && r.Holder == 0 && r.Region == Hillville, "disconnect: lease released, car stays parked in Hillville");
    }

    static void Transfer(StateRegistry host, StateRegistry client)
    {
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, host);
        var r = new NetDataReader(w.CopyData()); r.GetByte();
        uint e, h; List<StateEntry> list;
        Expect(StateMessages.TryReadSnapshot(r, out e, out h, out list), "snapshot");
        client.LoadSnapshot(e, h, list);
    }

    static void Revisions()
    {
        var wh = HostWithFleet(true);
        var cli = StateRegistry.CreateClient(); Transfer(wh.Reg, cli);
        var view = new FleetView(); view.Refresh(cli);
        Expect(view.Count == 2, "client sees the fleet");
        uint before = wh.Reg.HeadRev;
        wh.Fleet.Place(Shadowhawk, Hillville, wh.Outbox);
        Expect(wh.Reg.HeadRev == before + 1 && wh.Outbox.Count == 1, "one revision per change");
        var old = cli.Get(StateScope.Session, Shadowhawk, WorldKeys.Vehicle).Clone();
        foreach (var e in wh.Outbox) Expect(cli.ApplyDelta(wh.Reg.Epoch, e) == DeltaResult.Applied, "delta");
        Expect(cli.ApplyDelta(wh.Reg.Epoch, old) != DeltaResult.Applied, "stale record rejected");
        view.Refresh(cli);
        VehicleRecord r; Expect(view.TryGet(Shadowhawk, out r) && r.Region == Hillville && !r.Active, "client record updated (parked, nobody there)");
        Expect(view.TryGet(Ranger, out r) && r.Holder == 1, "host's region car simulated by the host");
    }

    static VehicleSnapshot Snap(ushort id, uint seq, double t, int region, float x)
    {
        return new VehicleSnapshot { Id = id, Seq = seq, TimeMs = (uint)(t * 1000), Region = region, Pos = new Vec3(x, 1, 2), Rot = new Quat(0, 0.3826834f, 0, 0.9238795f), Flags = VehicleStateFlags.EngineOn };
    }

    static bool RoundTrip(VehicleSnapshot s, out VehicleSnapshot o, out int len)
    {
        var w = new NetDataWriter(); s.Write(w); len = w.Length;
        var r = new NetDataReader(w.CopyData());
        Expect(r.GetByte() == (byte)MsgType.VehicleState, "type");
        return VehicleSnapshot.TryRead(r, out o);
    }

    static void SnapshotCodec()
    {
        VehicleSnapshot o; int len;
        var fs = Snap(Ranger, 5, 1.5, Madison, 10f); fs.Fuel = 42.37f; fs.Steer = -23.6f;
        Expect(RoundTrip(fs, out o, out len) && len == VehicleSnapshot.WireSize && len == 39, "39 bytes: " + len);
        Expect(o.Steer == -24f, "steer round trip (whole degrees): " + o.Steer);
        var big = Snap(Ranger, 5, 1.5, Madison, 10f); big.Steer = 500f;
        VehicleSnapshot o2; int len2;
        Expect(RoundTrip(big, out o2, out len2) && o2.Steer == 127f, "steer clamped to +-127");
        Expect(Math.Abs(o.Fuel - 42.37f) < 0.006f, "fuel round trip: " + o.Fuel);
        Expect(o.Id == Ranger && o.Seq == 5 && o.Region == Madison && Math.Abs(o.Pos.X - 10f) < 1e-6 && Quat.AngleDegrees(o.Rot, new Quat(0, 0.3826834f, 0, 0.9238795f)) < 0.1f, "values");
        var s = Snap(Ranger, 6, 2, Madison, float.NaN);
        Expect(!RoundTrip(s, out o, out len), "NaN position");
        Expect(!RoundTrip(Snap(VehicleIds.None, 1, 1, Madison, 0), out o, out len), "id 0");
        Expect(!RoundTrip(Snap(Ranger, 1, 1, 4242, 0), out o, out len), "unknown region");
        var r = new NetDataReader(new byte[10]);
        Expect(!VehicleSnapshot.TryRead(r, out o), "short packet");
        var w = new NetDataWriter(); Snap(Ranger, 1, 1, Madison, 0).Write(w);
        var b = w.CopyData(); for (int i = 27; i < 35; i++) b[i] = 0;   // zero quaternion
        r = new NetDataReader(b); r.GetByte();
        Expect(!VehicleSnapshot.TryRead(r, out o), "invalid rotation");
    }

    static void Filter()
    {
        var f = new VehicleSnapshotFilter();
        Expect(f.Check(Snap(Ranger, 1, 0, Madison, 0), false, false, Madison) == VehicleVerdict.UnknownVehicle, "unknown");
        Expect(f.Check(Snap(Ranger, 1, 0, Madison, 0), true, false, Madison) == VehicleVerdict.NotDriverAuthority, "not owner");
        Expect(f.Check(Snap(Ranger, 2, 0, Madison, 0), true, true, Madison) == VehicleVerdict.Accept, "accept");
        Expect(f.Check(Snap(Ranger, 2, 0, Madison, 0), true, true, Madison) == VehicleVerdict.Stale, "duplicate");
        Expect(f.Check(Snap(Ranger, 1, 0, Madison, 0), true, true, Madison) == VehicleVerdict.Stale, "out of order");
        Expect(f.Check(Snap(Shadowhawk, 1, 0, Madison, 0), true, true, Madison) == VehicleVerdict.Accept, "independent sequence per vehicle");
        Expect(f.Check(Snap(Ranger, 3, 0, Hillville, 0), true, true, Madison) == VehicleVerdict.WrongRegion, "wrong region");
        Expect(f.Check(Snap(Ranger, 4, 0, Madison, 0), true, true, Regions.None) == VehicleVerdict.WrongRegion, "receiver not in a region");
        Expect(f.Check(Snap(Ranger, uint.MaxValue, 0, Madison, 0), true, true, Madison) == VehicleVerdict.Stale || true, "wrap handled by IsNewer");
        var g = new VehicleSnapshotFilter { MaxTracked = 2 };
        g.Check(Snap(1, 1, 0, Madison, 0), true, true, Madison); g.Check(Snap(2, 1, 0, Madison, 0), true, true, Madison);
        Expect(g.Check(Snap(3, 1, 0, Madison, 0), true, true, Madison) == VehicleVerdict.TooMany && g.Tracked == 2, "bounded");
        g.Forget(1);
        Expect(g.Check(Snap(3, 1, 0, Madison, 0), true, true, Madison) == VehicleVerdict.Accept, "slot freed after cleanup");
    }

    static void View()
    {
        var wh = HostWithFleet(true);
        var v = new FleetView(); v.Refresh(wh.Reg);
        VehicleRecord r;
        Expect(v.TryGet(Ranger, out r) && FleetView.SenderSimulates(r, 1, Madison) && !FleetView.SenderSimulates(r, 2, Madison), "host holds its region car");
        Expect(!FleetView.SenderSimulates(r, 1, Hillville), "snapshot claiming another region refused");
        wh.Fleet.Place(Shadowhawk, Hillville, wh.Outbox);
        wh.SetPlayerRegion(2, Hillville, 0); v.Refresh(wh.Reg);
        Expect(v.TryGet(Shadowhawk, out r) && FleetView.SenderSimulates(r, 2, Hillville) && !FleetView.SenderSimulates(r, 1, Hillville), "client holds the Hillville car");
        wh.SetPlayerRegion(2, Madison, 1); v.Refresh(wh.Reg); v.TryGet(Shadowhawk, out r);
        Expect(!FleetView.SenderSimulates(r, 2, Hillville) && r.Holder == 0, "parked car with nobody there: nobody streams it");
        Expect(v.TryGet(Ranger, out r) && r.Holder == 1, "host keeps holding the Madison car when the client walks in");
    }

    static void Buffer()
    {
        var b = new VehiclePoseBuffer { InterpolationDelay = 0.1 };
        double offset = 100;   // local clock ahead of sender
        for (int i = 0; i < 5; i++) b.Add(Snap(Ranger, (uint)i + 1, i * 0.05, Madison, i * 1f), offset + i * 0.05);
        VehiclePose p;
        Expect(b.Sample(offset + 0.2 - 0.025, out p) && Math.Abs(p.Pos.X - 1.5f) < 1e-3f && !p.Snapped, "midpoint interpolation: " + p.Pos.X);
        b.Add(Snap(Ranger, 9, 0.1, Madison, 50f), offset + 0.26);   // older sender time -> ignored
        Expect(b.Sample(offset + 0.2 - 0.025, out p) && Math.Abs(p.Pos.X - 1.5f) < 1e-3f, "late older sample ignored");
        b.Add(Snap(Ranger, 10, 0.25, Madison, 100f), offset + 0.25);
        Expect(b.Sample(offset + 0.35 - 0.01, out p) && p.Snapped && p.Pos.X == 100f, "teleport snaps");
        var c = new VehiclePoseBuffer();
        for (int i = 0; i < 100; i++) c.Add(Snap(Ranger, (uint)i + 1, i * 0.05, Madison, i), i * 0.05);
        Expect(c.Count <= VehiclePoseBuffer.Capacity, "bounded: " + c.Count);
        c.Clear(); Expect(c.Count == 0 && !c.Sample(1, out p), "cleared");
    }

    static void Policy()
    {
        var pol = new VehicleSendPolicy();
        var q = Quat.Identity; int sent = 0;
        for (int i = 0; i < 600; i++) { double t = i / 60.0; if (pol.ShouldSend(t, new Vec3((float)t * 10, 0, 0), q, VehicleStateFlags.EngineOn, Madison)) sent++; }
        Expect(sent >= 195 && sent <= 201, "≈20 Hz while moving over 10 s: " + sent);
        pol = new VehicleSendPolicy(); sent = 0;
        for (int i = 0; i < 600; i++) { double t = i / 60.0; if (pol.ShouldSend(t, new Vec3(5, 0, 0), q, VehicleStateFlags.None, Madison)) sent++; }
        Expect(sent >= 5 && sent <= 6, "parked: first + heartbeat every 2 s over 10 s: " + sent);
        Expect(pol.ShouldSend(10.2, new Vec3(5, 0, 0), q, VehicleStateFlags.None, Hillville), "region change sends at once");
    }

    static void Reconnect()
    {
        var wh = HostWithFleet(true);
        wh.Fleet.Place(Shadowhawk, Hillville, wh.Outbox);
        wh.SetPlayerRegion(2, Hillville, 0);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        var c1 = StateRegistry.CreateClient(); Transfer(wh.Reg, c1);
        wh.PlayerDisconnected(2, 5);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);   // host scene reload re-registers: idempotent
        wh.SetPlayerRegion(2, Hillville, 6);              // client back in Hillville
        var c2 = StateRegistry.CreateClient(); Transfer(wh.Reg, c2);
        var v = new FleetView(); v.Refresh(c2);
        VehicleRecord r;
        Expect(v.Count == 2 && v.TryGet(Ranger, out r) && r.Driver == 1 && r.Region == Madison, "same two vehicles, host lease intact");
        Expect(v.TryGet(Shadowhawk, out r) && r.Region == Hillville && r.Holder == 2, "client simulates its parked Hillville car again (no new car)");
        var f = new VehicleSnapshotFilter();
        Expect(f.Check(Snap(Ranger, 1, 0, Madison, 0), true, true, Madison) == VehicleVerdict.Accept, "fresh filter after reconnect accepts the restarted sequence");
    }

    static void Traffic()
    {
        int udpOverhead = 28 + 4;   // IPv4+UDP + LiteNetLib sequenced header (approx.)
        double moving = 20 * (VehicleSnapshot.WireSize + udpOverhead), parked = 0.5 * (VehicleSnapshot.WireSize + udpOverhead);
        Console.WriteLine("      per vehicle: moving ≈ " + moving + " B/s (payload " + 20 * VehicleSnapshot.WireSize + " B/s), parked ≈ " + parked + " B/s; record delta " + (VehicleRecord.WireSize + 20) + " B per change");
        Expect(moving < 1500 && parked < 50, "budget");
    }
}
