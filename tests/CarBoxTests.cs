// M4.3.2b offline tests: shared CarBox lease (host-authoritative, exclusive, sessions, stale protection, concurrency,
// release on close / disconnect / region change / expiry), host contents snapshot (codec, commit order before the lease and
// the result), access from every role (host/client, driver/passenger/on foot - the lease does not depend on seats), and the
// client move block rule. Pure logic only: the original storage UI, Harmony hooks and item counters run only in game.
using System;
using System.Collections.Generic;
using TS2MP.Shared;

static class CarBoxTests
{
    static int _passed, _failed;
    static uint _pid;
    const int Hideout = 0, Hillville = 97, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);

    static int Main()
    {
        Run("01 exclusive lease: P1 open -> P2 refused -> P1 close -> P2 open (and reverse)", Exclusive);
        Run("02 sessions: new id per grant; stale close/renew of an older session changes nothing", StaleSessions);
        Run("03 concurrent opens: first committed wins, the second is Busy, repeated open of the owner is harmless", Concurrent);
        Run("04 release on client disconnect and on region change (no stale lock)", ReleaseOnLeave);
        Run("05 client owner that stops renewing expires; renewals keep it; the host owner never expires by time", Expiry);
        Run("06 client grant commits host contents before the lease (registry order), contents codec", GrantOrder);
        Run("07 access from every role: driver seated / passenger / on foot does not block the storage lease", Roles);
        Run("08 context: client needs a player car in its region; one lease for both cars (global storage)", Context);
        Run("09 protocol codecs and malformed proposals", Codecs);
        Run("10 snapshot limits and duplicates rejected", SnapshotLimits);
        Run("11 seat storage rule: both roles once the shared lease is active", SeatRule);
        Run("12 duplicated / retransmitted proposals answered from the cache", Retransmit);
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

    static WorldHost Both(int region)
    {
        var wh = new WorldHost(7) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, region, 0);
        wh.SetPlayerRegion(2, region, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);
        wh.Fleet.Register(GT, 1, true, wh.Outbox);
        wh.Fleet.Place(Ranger, region, wh.Outbox);
        wh.Fleet.Place(GT, Hideout, wh.Outbox);
        wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox);
        wh.CarBoxSnapshot = () => { var it = new CarBoxItems { Rev = 1 }; it.Counts.Add(new KeyValuePair<int, int>(42, 3)); byte[] d; it.TryEncode(out d); return d; };
        wh.Outbox.Clear();
        return wh;
    }

    static ProposalCode Cli(WorldHost wh, CarBoxOp op, uint session, double now, uint pid = 0)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.CarBoxReq, Value = CarBoxRequest.Encode(op, session) };
        return wh.HandleProposal("cli", 2, pid != 0 ? pid : ++_pid, p, now, out rev, out sr, out sh);
    }

    static ProposalCode Host(WorldHost wh, CarBoxOp op, uint session, double now) { string why; return wh.CarBoxRequest(1, op, session, now, out why); }

    static CarBoxLeaseState Lease(WorldHost wh)
    {
        var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.CarBox);
        CarBoxLeaseState s; Expect(e != null && CarBoxLeaseState.TryParse(e.Value, out s), "lease committed in the registry"); CarBoxLeaseState.TryParse(e.Value, out s); return s;
    }

    static void Exclusive()
    {
        var wh = Both(Madison);
        Expect(Host(wh, CarBoxOp.Open, 0, 1) == ProposalCode.Accepted && Lease(wh).Owner == 1, "host opens");
        Expect(Cli(wh, CarBoxOp.Open, 0, 1.1) == ProposalCode.Busy && Lease(wh).Owner == 1, "client refused while the host has it open (nothing opens, no text)");
        uint s1 = Lease(wh).Session;
        Expect(Host(wh, CarBoxOp.Close, s1, 2) == ProposalCode.Accepted && Lease(wh).Owner == 0, "host closes");
        Expect(Cli(wh, CarBoxOp.Open, 0, 2.1) == ProposalCode.Accepted && Lease(wh).Owner == 2, "client opens after the host closed");
        Expect(Host(wh, CarBoxOp.Open, 0, 2.2) == ProposalCode.Busy && Lease(wh).Owner == 2, "host refused while the client has it open");
        uint s2 = Lease(wh).Session;
        Expect(s2 != s1, "new session id per grant");
        Expect(Cli(wh, CarBoxOp.Close, s2, 3) == ProposalCode.Accepted && Lease(wh).Owner == 0, "client closes");
        Expect(Host(wh, CarBoxOp.Open, 0, 3.1) == ProposalCode.Accepted, "host can open again");
    }

    static void StaleSessions()
    {
        var wh = Both(Madison);
        Expect(Cli(wh, CarBoxOp.Open, 0, 1) == ProposalCode.Accepted, "client session A");
        uint a = Lease(wh).Session;
        Expect(Cli(wh, CarBoxOp.Close, a, 2) == ProposalCode.Accepted, "close A");
        Expect(Host(wh, CarBoxOp.Open, 0, 2.1) == ProposalCode.Accepted, "host session B");
        uint b = Lease(wh).Session;
        Expect(Cli(wh, CarBoxOp.Close, a, 2.2) == ProposalCode.Stale && Lease(wh).Owner == 1 && Lease(wh).Session == b, "delayed close of A never ends B");
        Expect(Cli(wh, CarBoxOp.Renew, a, 2.3) == ProposalCode.Stale, "stale renew rejected");
        Expect(Host(wh, CarBoxOp.Close, a, 2.4) == ProposalCode.Stale && Lease(wh).Owner == 1, "even the owner needs the right session id");
        Expect(Host(wh, CarBoxOp.Close, b, 2.5) == ProposalCode.Accepted && Lease(wh).Owner == 0, "close B");
        Expect(Cli(wh, CarBoxOp.Close, b, 2.6) == ProposalCode.Unchanged && Lease(wh).Owner == 0, "close of a free lease: no change");
    }

    static void Concurrent()
    {
        var wh = Both(Madison);
        // host click and client proposal in the same frame: client proposals are applied in LateUpdate after host UI events
        Expect(Host(wh, CarBoxOp.Open, 0, 1) == ProposalCode.Accepted, "first committed (host) wins");
        Expect(Cli(wh, CarBoxOp.Open, 0, 1) == ProposalCode.Busy, "second (client) is Busy");
        var wh2 = Both(Madison);
        Expect(Cli(wh2, CarBoxOp.Open, 0, 1) == ProposalCode.Accepted && Host(wh2, CarBoxOp.Open, 0, 1) == ProposalCode.Busy, "client first: host refused");
        uint s = Lease(wh2).Session;
        Expect(Cli(wh2, CarBoxOp.Open, 0, 1.2) == ProposalCode.Unchanged && Lease(wh2).Session == s, "repeated open by the owner keeps its session");
    }

    static void ReleaseOnLeave()
    {
        var wh = Both(Madison);
        Expect(Cli(wh, CarBoxOp.Open, 0, 1) == ProposalCode.Accepted, "client opens");
        wh.PlayerDisconnected(2, 2);
        Expect(Lease(wh).Owner == 0, "client disconnect frees the storage");
        Expect(Host(wh, CarBoxOp.Open, 0, 3) == ProposalCode.Accepted, "host can open after the disconnect");
        var wh2 = Both(Madison);
        Expect(Cli(wh2, CarBoxOp.Open, 0, 1) == ProposalCode.Accepted, "client opens");
        wh2.SetPlayerRegion(2, Hillville, 2);
        Expect(Lease(wh2).Owner == 0, "client travels away: released");
        Expect(Host(wh2, CarBoxOp.Open, 0, 2.1) == ProposalCode.Accepted, "host opens");
        wh2.SetPlayerRegion(1, Hillville, 3);
        Expect(Lease(wh2).Owner == 0, "host region change also releases its own session");
    }

    static void Expiry()
    {
        var wh = Both(Madison);
        Expect(Cli(wh, CarBoxOp.Open, 0, 0) == ProposalCode.Accepted, "client opens");
        uint s = Lease(wh).Session;
        for (double t = 5; t <= 60; t += 5) { Expect(Cli(wh, CarBoxOp.Renew, s, t) == ProposalCode.Unchanged, "renew at " + t); wh.Tick(t + 0.1); }
        Expect(Lease(wh).Owner == 2, "renewed session survives (an active transfer is never revoked)");
        wh.Tick(60 + CarBoxLease.LeaseSeconds + 0.1);
        Expect(Lease(wh).Owner == 0 && wh.CarBox.Expired == 1, "silent client (crash without disconnect) expires");
        Expect(Host(wh, CarBoxOp.Open, 0, 100) == ProposalCode.Accepted, "host opens");
        wh.Tick(10000);
        Expect(Lease(wh).Owner == 1, "host owner is released by its own UI watchdog, never by time");
    }

    static void GrantOrder()
    {
        var wh = Both(Madison);
        Expect(Cli(wh, CarBoxOp.Open, 0, 1) == ProposalCode.Accepted, "grant");
        int iItems = -1, iLease = -1;
        for (int i = 0; i < wh.Outbox.Count; i++)
        {
            if (wh.Outbox[i].Key == WorldKeys.CarBoxItems) iItems = i;
            if (wh.Outbox[i].Key == WorldKeys.CarBox) iLease = i;
        }
        Expect(iItems >= 0 && iLease > iItems, "contents committed before the lease (client applies them before the UI opens)");
        CarBoxItems it;
        Expect(CarBoxItems.TryParse(wh.Reg.Get(StateScope.Session, 0, WorldKeys.CarBoxItems).Value, out it) && it.CountOf(42) == 3 && it.CountOf(7) == 0, "host contents in the registry");
        var wh2 = Both(Madison);
        wh2.CarBoxSnapshot = () => null;
        Expect(Cli(wh2, CarBoxOp.Open, 0, 1) == ProposalCode.Busy && wh2.CarBox.Owner == 0, "no host contents -> no lease (never a client UI on stale data)");
        wh2.Outbox.Clear();
        Expect(Host(wh2, CarBoxOp.Open, 0, 2) == ProposalCode.Accepted, "the host itself needs no snapshot (its counters are the storage)");
        foreach (var e in wh2.Outbox) Expect(e.Key != WorldKeys.CarBoxItems, "no contents commit for the host's own session");
    }

    static void Roles()
    {
        // host drives the Ranger, client rides / stands next to it: the lease is independent of seats and simulation
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        string why;
        wh.Motion.Report(Ranger, false, true, 1);
        Expect(wh.Ride(2, Ranger, 1, 1.5, out why) == ProposalCode.Accepted && wh.Ride(2, Ranger, 2, 1.6, out why) == ProposalCode.Accepted, "client seated as passenger");
        Expect(Cli(wh, CarBoxOp.Open, 0, 2) == ProposalCode.Accepted, "client passenger opens while the host drives");
        uint s = Lease(wh).Session;
        Expect(Cli(wh, CarBoxOp.Close, s, 3) == ProposalCode.Accepted, "closes");
        Expect(Host(wh, CarBoxOp.Open, 0, 3.1) == ProposalCode.Accepted, "host driver opens (vanilla driver access is not changed)");
        Expect(R(wh, Ranger).Driver == 1 && R(wh, Ranger).Passenger == 2, "seats untouched by storage sessions");
        // client drives, host rides / stands
        var wh2 = Both(Madison);
        uint rv; int sr; float sh;
        wh2.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.VehicleTake, Value = new byte[] { 1 } }, 1, out rv, out sr, out sh);
        wh2.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.VehicleClaim, Value = new byte[] { 1 } }, 1.1, out rv, out sr, out sh);
        Expect(R(wh2, Ranger).Driver == 2, "client drives");
        Expect(Host(wh2, CarBoxOp.Open, 0, 2) == ProposalCode.Accepted, "host opens while the client drives");
        Expect(Cli(wh2, CarBoxOp.Open, 0, 2.1) == ProposalCode.Busy, "client driver refused meanwhile");
    }

    static void Context()
    {
        var wh = Both(Madison);
        wh.SetPlayerRegion(2, Hillville, 1);
        Expect(Cli(wh, CarBoxOp.Open, 0, 2) == ProposalCode.NotAuthorized && wh.CarBox.Owner == 0, "client in a region without a player car cannot open storage");
        wh.SetPlayerRegion(2, Hideout, 3);
        Expect(Cli(wh, CarBoxOp.Open, 0, 4) == ProposalCode.Accepted, "hideout with the Shadowhawk: allowed");
        Expect(Host(wh, CarBoxOp.Open, 0, 4.1) == ProposalCode.Busy, "host at the Ranger in Madison: the same (global) storage is busy");
    }

    static void Codecs()
    {
        CarBoxOp op; uint s;
        Expect(CarBoxRequest.TryParse(CarBoxRequest.Encode(CarBoxOp.Renew, 77), out op, out s) && op == CarBoxOp.Renew && s == 77, "request codec");
        Expect(!CarBoxRequest.TryParse(new byte[] { 3, 0, 0, 0, 0 }, out op, out s) && !CarBoxRequest.TryParse(new byte[] { 1 }, out op, out s), "malformed request");
        CarBoxLeaseState st;
        Expect(CarBoxLeaseState.TryParse(new CarBoxLeaseState { Owner = 2, Session = 9 }.ToBytes(), out st) && st.Owner == 2 && st.Session == 9, "lease codec");
        Expect(!CarBoxLeaseState.TryParse(new byte[] { 3, 0, 0, 0, 0 }, out st), "bad owner");
        var wh = Both(Madison);
        uint rev; int sr; float sh;
        Expect(wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.CarBoxReq, Value = new byte[] { 1 } }, 1, out rev, out sr, out sh) == ProposalCode.Invalid, "short proposal");
        Expect(wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 5, Key = WorldKeys.CarBoxReq, Value = CarBoxRequest.Encode(CarBoxOp.Open, 0) }, 1, out rev, out sr, out sh) == ProposalCode.Invalid, "wrong scope arg");
        Expect(wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.CarBox, Value = new CarBoxLeaseState { Owner = 2, Session = 1 }.ToBytes() }, 1, out rev, out sr, out sh) == ProposalCode.NotAuthorized, "the client can never write the lease itself");
        Expect(wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.CarBoxItems, Value = new byte[6] }, 1, out rev, out sr, out sh) == ProposalCode.NotAuthorized, "the client can never write storage contents");
        Expect(ProtocolInfo.ProtocolVersion == 26 && ProtocolInfo.ModVersion == "0.14.2", "protocol 26 / mod 0.14.2");
    }

    static void SnapshotLimits()
    {
        var it = new CarBoxItems { Rev = 3 };
        for (int i = 0; i < CarBoxItems.MaxEntries; i++) it.Counts.Add(new KeyValuePair<int, int>(i + 1, 1 + i % 50));
        byte[] d;
        Expect(it.TryEncode(out d) && d.Length <= 1024, "160 item types fit one registry value (" + d.Length + " B)");
        CarBoxItems back;
        Expect(CarBoxItems.TryParse(d, out back) && back.Counts.Count == CarBoxItems.MaxEntries && back.CountOf(160) == it.CountOf(160), "round trip");
        it.Counts.Add(new KeyValuePair<int, int>(999, 1));
        Expect(!it.TryEncode(out d), "more types than fit -> no snapshot (client open refused, logged)");
        var dup = new CarBoxItems(); dup.Counts.Add(new KeyValuePair<int, int>(5, 1)); dup.Counts.Add(new KeyValuePair<int, int>(5, 2));
        Expect(dup.TryEncode(out d) && !CarBoxItems.TryParse(d, out back), "duplicate item type rejected by the parser");
        var zero = new CarBoxItems(); zero.Counts.Add(new KeyValuePair<int, int>(5, 0));
        Expect(!zero.TryEncode(out d), "zero counts are never encoded (absent = 0)");
        Expect(!CarBoxItems.TryParse(new byte[] { 0, 0, 0, 0, 2, 0, 1, 0, 0, 0, 1, 0 }, out back), "truncated payload");
    }

    static void SeatRule()
    {
        Expect(PassengerInteraction.SeatStorageAllowed(true, false), "host passenger without the shared lease (single player / API missing): its own real storage");
        Expect(!PassengerInteraction.SeatStorageAllowed(false, false), "client without the shared lease: nothing");
        Expect(PassengerInteraction.SeatStorageAllowed(false, true) && PassengerInteraction.SeatStorageAllowed(true, true), "both roles with the shared lease");
    }

    static void Retransmit()
    {
        var wh = Both(Madison);
        uint pid = 500;
        Expect(Cli(wh, CarBoxOp.Open, 0, 1, pid) == ProposalCode.Accepted, "open");
        uint s = Lease(wh).Session;
        _pid = 600;
        Expect(Cli(wh, CarBoxOp.Close, s, 2) == ProposalCode.Accepted && Lease(wh).Owner == 0, "close");
        Expect(Cli(wh, CarBoxOp.Open, 0, 2.1, pid) == ProposalCode.Stale && Lease(wh).Owner == 0, "retransmitted old open (superseded id) is Stale, never re-opens");
    }

    static VehicleRecord R(WorldHost wh, ushort id) { VehicleRecord r; Expect(wh.Fleet.TryGet(id, out r), "record " + id); return r; }
}
