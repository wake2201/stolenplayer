// Offline tests for M3/M3.1 shared world state: registry, revisions, snapshot/delta, proposals, regions, region clocks.
// No Unity, no network (the UDP round trip is in ProtocolTests). Exit code 0 = all passed.
using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class WorldTests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("entry round-trip; malformed/oversized entries rejected", EntryCodec);
        Run("commit: monotonic revisions, unchanged value is idempotent", CommitRevisions);
        Run("snapshot serialization round-trip reproduces host state", SnapshotRoundTrip);
        Run("delta: in-order applied, duplicate / gap / wrong epoch / no snapshot rejected", DeltaRules);
        Run("out-of-order delta -> resync snapshot converges", OutOfOrderResync);
        Run("late join: snapshot after many commits, then live deltas", LateJoin);
        Run("reconnect same host: reset + snapshot restores state", ReconnectSameHost);
        Run("host restart: new epoch rejects old deltas, snapshot replaces history", HostRestart);
        Run("proposal gate: duplicate id gets cached answer, older id stale, per instance", ProposalGateRules);
        Run("proposal rules: own region only, calendar host-only, invalid region", ProposalRuleMatrix);
        Run("region identity from CurrentLocation + loaded scenes", RegionResolve);
        Run("independent region transitions, owners, region state retained when empty", IndependentRegions);
        Run("RegionClock / Calendar / SleepRequest codecs and validation", ClockCodecs);
        Run("time math: wrap, signed/forward diff, extrapolation (frozen/locked do not move)", TimeMathBasics);
        Run("time correction: deadband / blend / snap / hour-change flag both directions", TimeCorrectionRules);
        Run("clock correction across midnight flags the hour change (no day rollover)", MidnightCorrection);
        Run("owner sampler: commit triggers (first, refresh, rate, mode, jump)", SamplerTriggers);
        Run("independent clocks in two regions", IndependentClocks);
        Run("host joining a client-occupied region adopts its time", HostJoinsClientRegion);
        Run("client joining a host-occupied region adopts its time", ClientJoinsHostRegion);
        Run("new region initialized from traveller's previous region, else session base", DeterministicInit);
        Run("same-region sleep by host advances the shared region clock", SameRegionHostSleep);
        Run("same-region sleep by client: accepted, host catches up hour by hour", SameRegionClientSleep);
        Run("sleep in one region leaves the other region unchanged", SleepIsolation);
        Run("empty region keeps its time and resumes it", RegionPersistsWhenEmpty);
        Run("reconnect: snapshot restores fresh running and frozen clocks", ReconnectRestore);
        Run("duplicate / stale sleep requests never advance twice", DuplicateSleep);
        Run("client reports/sleeps validated (owner, tolerance, rate, region)", ReportValidation);
        Run("hourly ticks: corrections add none, catch-up adds exactly the slept hours", HourlyTickAccounting);
        Run("client follows region clock through deltas (converges, crosses midnight)", ClientFollows);
        // M3.1.1 client sleep transaction
        Run("sleep tx: completed sleep (no StopIt) yields one request; later StopIt is a no-op", SleepTxCompleted);
        Run("sleep tx: repeated PassStart keeps the original start time", SleepTxDuplicateStart);
        Run("sleep tx: region change during sleep / pending result drops the transaction", SleepTxRegionChange);
        Run("sleep tx: result matching, rejection, timeout, disconnect", SleepTxResults);
        Run("client alone in Hillville sleeps: host commits, client adopts, no rollback, Madison unchanged", ClientSleepAloneEndToEnd);
        Run("pre-fix failure mode: post-sleep report without accepted request is rejected", PostSleepReportWithoutRequest);
        Run("client sleep across midnight: 22:30 + 7.5 h -> 06:00", ClientSleepMidnight);
        Run("client sleep in the host's region: client target = host commit, host steps exactly the slept hours", ClientSleepSharedRegion);
        Run("stale report / stale delta after an accepted sleep cannot roll it back", SleepStaleProtection);
        Run("sleep request after the client left the region is refused, clock untouched", SleepAfterRegionChange);
        Run("reconnect after sleep: snapshot carries the slept region time", SleepReconnect);
        // M3.1.2 shared sleep time-lapse
        Run("sleepStart codec and validation", SleepStartCodec);
        Run("time-lapse stop rule: target hour index, or forward-only settle to the sleeper's end", LapseRule);
        Run("client sleeps with host in region: start accepted, host time-lapse runs before completion, same end, no double advance", SharedSleepWatched);
        Run("shared sleep across midnight: host gets exactly the slept hourly ticks and one day", SharedSleepMidnightTicks);
        Run("Esc during a watched sleep: host settles forward to the sleeper's end, never backward", SharedSleepCancelled);
        Run("one sleep at a time: second start / host's own sleep in that region -> Busy", SleepBusy);
        Run("client sleep with host elsewhere: no time-lapse, commit, other region unchanged", SleepHostElsewhere);
        Run("reordered / duplicate start and end never advance twice", SleepReorder);
        Run("disconnect, sleeper region change, host leaving, expiry end the transaction cleanly", SleepInterrupts);
        Run("client tx: rejected start or disconnect blocks the completion request", SleepTxStartGate);
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

    static NetDataReader Reader(NetDataWriter w) { return new NetDataReader(w.CopyData()); }

    // Simulates the wire: host snapshot -> client registry.
    static void Transfer(StateRegistry host, StateRegistry client)
    {
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, host);
        var r = Reader(w);
        Expect(r.GetByte() == (byte)MsgType.StateSnapshot, "type");
        uint e, h; List<StateEntry> list;
        Expect(StateMessages.TryReadSnapshot(r, out e, out h, out list), "snapshot parse");
        client.LoadSnapshot(e, h, list);
    }

    static DeltaResult Deliver(StateRegistry host, StateEntry e, StateRegistry client)
    {
        var w = new NetDataWriter(); StateMessages.WriteDelta(w, host.Epoch, e);
        var r = Reader(w); r.GetByte();
        uint ep; StateEntry got;
        Expect(StateMessages.TryReadDelta(r, out ep, out got), "delta parse");
        return client.ApplyDelta(ep, got);
    }

    static void SameState(StateRegistry a, StateRegistry b)
    {
        Expect(a.Epoch == b.Epoch && a.HeadRev == b.HeadRev && a.Count == b.Count, "head/epoch/count differ: " + a.HeadRev + "/" + b.HeadRev + " " + a.Count + "/" + b.Count);
        foreach (var e in a.Entries)
        {
            var o = b.Get(e.Scope, e.ScopeArg, e.Key);
            Expect(o != null && o.Rev == e.Rev && o.Value.SequenceEqual(e.Value), "entry differs " + e.Id);
        }
    }

    static void EntryCodec()
    {
        var e = new StateEntry { Scope = StateScope.Region, ScopeArg = 99, Key = "lastVisit", Rev = 7, Value = new byte[] { 1, 2, 3 } };
        var w = new NetDataWriter(); e.Write(w);
        StateEntry g; Expect(StateEntry.TryRead(Reader(w), out g), "read");
        Expect(g.Id == e.Id && g.Rev == 7 && g.Value.SequenceEqual(e.Value), "round-trip");
        var t = w.CopyData(); Array.Resize(ref t, t.Length - 2);
        Expect(!StateEntry.TryRead(new NetDataReader(t), out g), "truncated accepted");
        w.Reset(); w.Put((byte)9); w.Put(0); w.Put("k"); w.Put(1u); w.Put((ushort)0);
        Expect(!StateEntry.TryRead(Reader(w), out g), "bad scope accepted");
        w.Reset(); w.Put((byte)1); w.Put(0); w.Put("k"); w.Put(1u); w.Put((ushort)5000); w.Put(new byte[5000]);
        Expect(!StateEntry.TryRead(Reader(w), out g), "oversized accepted");
        w.Reset(); w.Put((byte)1); w.Put(0); w.Put(""); w.Put(1u); w.Put((ushort)0);
        Expect(!StateEntry.TryRead(Reader(w), out g), "empty key accepted");
    }

    static void CommitRevisions()
    {
        var h = StateRegistry.CreateHost(5);
        var a = h.Commit(StateScope.Session, 0, "x", new byte[] { 1 });
        var b = h.Commit(StateScope.Player, 2, "region", WorldKeys.Int(0));
        Expect(a.Rev == 1 && b.Rev == 2 && h.HeadRev == 2, "revisions");
        Expect(h.Commit(StateScope.Session, 0, "x", new byte[] { 1 }) == null && h.HeadRev == 2, "unchanged created a revision");
        var c = h.Commit(StateScope.Session, 0, "x", new byte[] { 2 });
        Expect(c.Rev == 3 && h.Count == 2, "update");
        Expect(h.Commit(StateScope.Player, 1, "region", WorldKeys.Int(0)).Rev == 4 && h.Count == 3, "same key, other slot must be separate");
    }

    static StateRegistry SampleHost(uint epoch)
    {
        var h = StateRegistry.CreateHost(epoch);
        h.Commit(StateScope.Player, 1, WorldKeys.Region, WorldKeys.Int(0));
        h.Commit(StateScope.Player, 2, WorldKeys.Region, WorldKeys.Int(99));
        h.Commit(StateScope.Region, 99, WorldKeys.LastVisit, new byte[] { 2, 0, 0, 0, 0 });
        h.Commit(StateScope.Region, 99, WorldKeys.Clock, new RegionClock { Hours = 13.5f, Rate = 0.01f, Mode = 1, Running = true }.ToBytes());
        h.Commit(StateScope.Session, 0, WorldKeys.Calendar, new Calendar { Day = 2, Week = 1 }.ToBytes());
        return h;
    }

    static void SnapshotRoundTrip()
    {
        var h = SampleHost(0xABCDu);
        var c = StateRegistry.CreateClient();
        Transfer(h, c);
        SameState(h, c);
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, h);
        var t = w.CopyData(); Array.Resize(ref t, t.Length - 1);
        var r = new NetDataReader(t); r.GetByte();
        uint e, hd; List<StateEntry> l;
        Expect(!StateMessages.TryReadSnapshot(r, out e, out hd, out l), "truncated snapshot accepted");
    }

    static void DeltaRules()
    {
        var h = StateRegistry.CreateHost(1);
        var c = StateRegistry.CreateClient();
        var e1 = h.Commit(StateScope.Session, 0, "a", new byte[] { 1 });
        Expect(Deliver(h, e1, c) == DeltaResult.NoSnapshot, "no snapshot");
        Transfer(h, c);
        var e2 = h.Commit(StateScope.Session, 0, "a", new byte[] { 2 });
        Expect(Deliver(h, e2, c) == DeltaResult.Applied, "in order");
        Expect(Deliver(h, e2, c) == DeltaResult.Duplicate, "duplicate");
        Expect(Deliver(h, e1, c) == DeltaResult.Duplicate, "older");
        h.Commit(StateScope.Session, 0, "a", new byte[] { 3 });
        var e4 = h.Commit(StateScope.Session, 0, "a", new byte[] { 4 });
        Expect(Deliver(h, e4, c) == DeltaResult.Gap, "gap");
        Expect(c.Get(StateScope.Session, 0, "a").Value[0] == 2, "gap delta must not apply");
        Expect(c.ApplyDelta(77, e4.Clone()) == DeltaResult.WrongEpoch, "epoch");
    }

    static void OutOfOrderResync()
    {
        var h = StateRegistry.CreateHost(3); var c = StateRegistry.CreateClient();
        Transfer(h, c);
        var e1 = h.Commit(StateScope.Session, 0, "a", new byte[] { 1 });
        var e2 = h.Commit(StateScope.Session, 0, "b", new byte[] { 2 });
        Expect(Deliver(h, e2, c) == DeltaResult.Gap, "expected gap");
        Transfer(h, c);   // resync request answered with a snapshot
        Expect(Deliver(h, e1, c) == DeltaResult.Duplicate, "late e1 must be a duplicate after resync");
        SameState(h, c);
    }

    static void LateJoin()
    {
        var h = StateRegistry.CreateHost(9);
        for (int i = 0; i < 200; i++) h.Commit(StateScope.Session, 0, "t", WorldKeys.Int(i));
        h.Commit(StateScope.Player, 1, WorldKeys.Region, WorldKeys.Int(3));
        var c = StateRegistry.CreateClient();
        Transfer(h, c);
        SameState(h, c);
        Expect(c.HeadRev == 201 && c.Count == 2, "late joiner should get latest values only");
        Expect(Deliver(h, h.Commit(StateScope.Session, 0, "t", WorldKeys.Int(500)), c) == DeltaResult.Applied, "live delta after join");
        SameState(h, c);
    }

    static void ReconnectSameHost()
    {
        var h = SampleHost(11); var c = StateRegistry.CreateClient();
        Transfer(h, c);
        c.Reset();   // connection lost; changes happen while away
        h.Commit(StateScope.Player, 2, WorldKeys.Region, WorldKeys.Int(Regions.None));
        h.Commit(StateScope.Player, 1, WorldKeys.Region, WorldKeys.Int(99));
        Expect(!c.HasSnapshot, "reset");
        Transfer(h, c);
        SameState(h, c);
    }

    static void HostRestart()
    {
        var h1 = SampleHost(100); var c = StateRegistry.CreateClient();
        Transfer(h1, c);
        var h2 = StateRegistry.CreateHost(200);
        h2.Commit(StateScope.Player, 1, WorldKeys.Region, WorldKeys.Int(3));
        var d = h2.Commit(StateScope.Session, 0, "x", new byte[] { 1 });
        Expect(Deliver(h2, d, c) == DeltaResult.WrongEpoch, "new-epoch delta must not mix into old history");
        Transfer(h2, c);
        SameState(h2, c);
        Expect(c.Get(StateScope.Region, 99, WorldKeys.LastVisit) == null, "old history must be gone");
    }

    static void ProposalGateRules()
    {
        var g = new ProposalGate(); ProposalCode code; uint rev;
        Expect(!g.TryGetCached("cli", 1, out code, out rev), "first id must be new");
        g.Record("cli", 1, ProposalCode.Accepted, 12);
        Expect(g.TryGetCached("cli", 1, out code, out rev) && code == ProposalCode.Accepted && rev == 12, "duplicate gets cached answer");
        Expect(!g.TryGetCached("cli", 2, out code, out rev), "next id new");
        g.Record("cli", 2, ProposalCode.Unchanged, 12);
        Expect(g.TryGetCached("cli", 1, out code, out rev) && code == ProposalCode.Stale, "older id stale");
        Expect(!g.TryGetCached("other", 1, out code, out rev), "per instance");
    }

    static StateEntry Prop(StateScope s, int arg, string key, byte[] v) { return new StateEntry { Scope = s, ScopeArg = arg, Key = key, Value = v }; }

    static void ProposalRuleMatrix()
    {
        Expect(ProposalRules.Validate(Prop(StateScope.Player, 2, WorldKeys.Region, WorldKeys.Int(99)), 2) == ProposalCode.Accepted, "own region");
        Expect(ProposalRules.Validate(Prop(StateScope.Player, 2, WorldKeys.Region, WorldKeys.Int(Regions.None)), 2) == ProposalCode.Accepted, "none allowed");
        Expect(ProposalRules.Validate(Prop(StateScope.Player, 1, WorldKeys.Region, WorldKeys.Int(0)), 2) == ProposalCode.NotAuthorized, "other player's region");
        Expect(ProposalRules.Validate(Prop(StateScope.Player, 2, WorldKeys.Region, WorldKeys.Int(42)), 2) == ProposalCode.Invalid, "unknown region");
        Expect(ProposalRules.Validate(Prop(StateScope.Player, 2, WorldKeys.Region, new byte[] { 1 }), 2) == ProposalCode.Invalid, "bad value");
        Expect(ProposalRules.Validate(Prop(StateScope.Session, 0, WorldKeys.Calendar, new Calendar { Day = 1 }.ToBytes()), 2) == ProposalCode.NotAuthorized, "calendar is host-only");
        Expect(ProposalRules.Validate(Prop(StateScope.Region, 99, WorldKeys.LastVisit, new byte[1]), 2) == ProposalCode.NotAuthorized, "region state host-only");
    }

    static void RegionResolve()
    {
        Func<string[], HashSet<string>> S = a => new HashSet<string>(a);
        Expect(Regions.Resolve(0, true, S(new[] { "MainScene", "PlayerHideout" })) == 0, "hideout");
        Expect(Regions.Resolve(0, true, S(new[] { "MainScene" })) == Regions.None, "currentLoc 0 before additive load must not count as hideout");
        Expect(Regions.Resolve(3, true, S(new[] { "MainScene", "Auto Shop" })) == 3, "auto shop");
        Expect(Regions.Resolve(99, true, S(new[] { "MainScene", "MadisonSt" })) == 99, "madison");
        Expect(Regions.Resolve(99, true, S(new[] { "MainScene", "PlayerHideout" })) == Regions.None, "mismatch");
        Expect(Regions.Resolve(1, true, S(new[] { "MainScene" })) == 1, "verwood = MainScene only");
        Expect(Regions.Resolve(1, true, S(new[] { "MainScene", "Auto Shop" })) == Regions.None, "verwood with another region scene");
        Expect(Regions.Resolve(0, false, S(new[] { "MainScene", "PlayerHideout" })) == Regions.None, "no player");
        Expect(Regions.Resolve(0, true, S(new[] { "Menu" })) == Regions.None, "menu");
        Expect(Regions.Resolve(42, true, S(new[] { "MainScene" })) == Regions.None, "unknown loc");
        Expect(Regions.Name(99) == "MadisonSt" && Regions.Name(0) == "Hideout" && Regions.Name(3) == "AutoShop" && Regions.Name(Regions.None) == "none", "names");
    }

    static void IndependentRegions()
    {
        var h = StateRegistry.CreateHost(1); var c = StateRegistry.CreateClient();
        Action<byte, int> move = (slot, region) =>
        {
            var e = h.Commit(StateScope.Player, slot, WorldKeys.Region, WorldKeys.Int(region));
            if (c.HasSnapshot && e != null) Expect(Deliver(h, e, c) == DeltaResult.Applied, "delta");
            if (region != Regions.None)
            {
                var v = h.Commit(StateScope.Region, region, WorldKeys.LastVisit, new byte[] { slot, 0, 0, 0, 0 });
                if (c.HasSnapshot && v != null) Expect(Deliver(h, v, c) == DeltaResult.Applied, "delta");
            }
        };
        move(1, 0); Transfer(h, c); move(2, 0);
        Expect(RegionDirectory.OwnerOf(h, 0) == 1, "host owns shared hideout");
        move(1, Regions.None); move(1, 99);   // P1 travels to Madison, P2 stays
        Expect(RegionDirectory.OwnerOf(h, 0) == 2 && RegionDirectory.OwnerOf(h, 99) == 1, "split owners");
        move(2, Regions.None); move(2, 99);   // P2 follows independently
        Expect(RegionDirectory.Occupants(h)[99].SequenceEqual(new byte[] { 1, 2 }) && RegionDirectory.OwnerOf(h, 0) == 0, "both in Madison, hideout unowned");
        Expect(h.Get(StateScope.Region, 0, WorldKeys.LastVisit) != null, "hideout state retained while empty");
        move(1, Regions.None); move(1, 0);    // P1 back home, P2 stays in Madison
        Expect(RegionDirectory.OwnerOf(h, 99) == 2 && RegionDirectory.RegionOf(c, 1) == 0 && RegionDirectory.RegionOf(c, 2) == 99, "client view follows");
        SameState(h, c);
    }

    static void Near(float a, float b, float eps, string m) { Expect(Math.Abs(a - b) <= eps, m + " (" + a + " vs " + b + ")"); }

    // ---------------- M3.1 region clocks ----------------
    const int Hideout = 0, Hillville = 97, Madison = 99;
    static uint _pid = 100;

    static RegionClock ClockOf(StateRegistry r, int region)
    {
        var e = r.Get(StateScope.Region, region, WorldKeys.Clock);
        RegionClock c;
        Expect(e != null && RegionClock.TryParse(e.Value, out c), "no clock for " + Regions.Name(region));
        RegionClock.TryParse(e.Value, out c);
        return c;
    }

    static ProposalCode Propose(WorldHost wh, StateEntry p, double t, uint id)
    {
        uint rev; int sr; float sh;
        return wh.HandleProposal("cli", 2, id, p, t, out rev, out sr, out sh);
    }
    static ProposalCode Propose(WorldHost wh, StateEntry p, double t) { return Propose(wh, p, t, _pid++); }

    static StateEntry RegionP(int region) { return new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Region, Value = WorldKeys.Int(region) }; }
    static StateEntry StartP(int region, float from, int target) { return new StateEntry { Scope = StateScope.Region, ScopeArg = region, Key = WorldKeys.SleepStart, Value = new SleepStart { From = from, TargetHour = (byte)target }.ToBytes() }; }
    static void Begin(WorldHost wh, int region, float from, double t) { var c = Propose(wh, StartP(region, from, (int)TimeMath.Wrap24(from + 1)), t); Expect(c == ProposalCode.Accepted, "sleep start: " + c); }
    static StateEntry SleepP(int region, float from, float adv) { return new StateEntry { Scope = StateScope.Region, ScopeArg = region, Key = WorldKeys.Sleep, Value = new SleepRequest { From = from, Advance = adv }.ToBytes() }; }
    static StateEntry ReportP(int region, float hours, float rate) { return new StateEntry { Scope = StateScope.Region, ScopeArg = region, Key = WorldKeys.Clock, Value = new RegionClock { Hours = hours, Rate = rate, Mode = 1, Running = true }.ToBytes() }; }

    // Host in Madison at 12:00, client in Hillville at 16:00 (client slept 4 h there).
    static WorldHost Split()
    {
        var wh = new WorldHost(7) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, Madison, 0);
        Expect(Propose(wh, RegionP(Hillville), 0) == ProposalCode.Accepted, "client region");
        Begin(wh, Hillville, 12f, 0);
        Expect(Propose(wh, SleepP(Hillville, 12f, 4f), 0) == ProposalCode.Accepted, "client sleep");
        Near(wh.Clocks.Model(Madison, 0), 12f, 1e-4f, "madison");
        Near(wh.Clocks.Model(Hillville, 0), 16f, 1e-4f, "hillville");
        wh.Outbox.Clear();
        return wh;
    }

    static void ClockCodecs()
    {
        var c = new RegionClock { Hours = 23.99f, Rate = TimeMath.NominalRate, Mode = 1, Running = true };
        var b = c.ToBytes();
        Expect(b.Length == RegionClock.WireSize, "size " + b.Length);
        RegionClock g; Expect(RegionClock.TryParse(b, out g) && g.Hours == c.Hours && g.Rate == c.Rate && g.Mode == 1 && g.Running, "round-trip");
        Func<RegionClock, bool> ok = x => { RegionClock y; return RegionClock.TryParse(x.ToBytes(), out y); };
        Expect(!ok(new RegionClock { Hours = 24f }) && !ok(new RegionClock { Hours = float.NaN }) && !ok(new RegionClock { Hours = 1, Rate = 30f }) && !ok(new RegionClock { Hours = 1, Mode = 4 }), "invalid clocks");
        Calendar cal; Expect(Calendar.TryParse(new Calendar { Day = 6, Week = 2 }.ToBytes(), out cal) && cal.Day == 6 && cal.Week == 2, "calendar");
        Expect(!Calendar.TryParse(new Calendar { Day = 9 }.ToBytes(), out cal), "bad day");
        SleepRequest s; Expect(SleepRequest.TryParse(new SleepRequest { From = 22f, Advance = 8f }.ToBytes(), out s) && s.Advance == 8f, "sleep");
        Expect(!SleepRequest.TryParse(new SleepRequest { From = 22f, Advance = 0f }.ToBytes(), out s) && !SleepRequest.TryParse(new SleepRequest { From = 22f, Advance = 25f }.ToBytes(), out s), "bad sleep");
        Near(TimeMath.NominalRate, 24f / 1680f, 1e-7f, "nominal rate = 24 h per 28 min");
    }

    static void TimeMathBasics()
    {
        Near(TimeMath.Wrap24(25.5), 1.5f, 1e-5f, "wrap+"); Near(TimeMath.Wrap24(-0.5), 23.5f, 1e-5f, "wrap-");
        Near(TimeMath.SignedDiff(0.1f, 23.9f), 0.2f, 1e-4f, "midnight forward");
        Near(TimeMath.SignedDiff(23.9f, 0.1f), -0.2f, 1e-4f, "midnight backward");
        Near(TimeMath.ForwardDiff(6f, 22f), 8f, 1e-4f, "forward across midnight");
        var c = new RegionClock { Hours = 23.5f, Rate = 0.1f, Mode = 1, Running = true };
        Near(TimeMath.Extrapolate(c, 10), 0.5f, 1e-4f, "extrapolate wraps");
        Near(TimeMath.Extrapolate(c, 1000), TimeMath.Wrap24(23.5 + 0.1 * TimeMath.MaxExtrapolation), 1e-4f, "receiver clamp");
        c.Running = false; Near(TimeMath.Extrapolate(c, 10), 23.5f, 0, "frozen");
        c.Running = true; c.Mode = 0; Near(TimeMath.Extrapolate(c, 10), 23.5f, 0, "locked mode");
    }

    static void TimeCorrectionRules()
    {
        Expect(TimeMath.Decide(10f, 10.005f, 0.016f, false).Action == TimeAction.None, "deadband");
        var c = TimeMath.Decide(10f, 10.1f, 0.016f, false);
        Expect(c.Action == TimeAction.Blend && c.NewHours > 10f && c.NewHours < 10.1f && !c.HourChanged, "blend");
        c = TimeMath.Decide(10f, 10.1f, 0f, true);
        Expect(c.Action == TimeAction.Snap && c.NewHours == 10.1f, "forced snap (adopt)");
        c = TimeMath.Decide(10f, 14f, 0.016f, false);
        Expect(c.Action == TimeAction.Snap && c.HourChanged, "forward snap changes hour -> suppressed");
        c = TimeMath.Decide(14.2f, 13.5f, 0.016f, false);
        Expect(c.Action == TimeAction.Snap && c.HourChanged, "backward snap");
        c = TimeMath.Decide(14.6f, 14.5f, 0.016f, false);
        Expect(c.Action == TimeAction.Blend && !c.HourChanged, "within hour");
    }

    static void MidnightCorrection()
    {
        var c = TimeMath.Decide(23.95f, 0.05f, 2f, false);
        Expect(c.Action == TimeAction.Blend && c.HourChanged && c.NewHours < 1f, "blend across midnight flags hour change");
        c = TimeMath.Decide(23.95f, 0.05f, 0f, true);
        Expect(c.Action == TimeAction.Snap && c.NewHours == 0.05f && c.HourChanged, "snap across midnight flags hour change");
        c = TimeMath.Decide(0.05f, 23.5f, 0f, false);
        Expect(c.Action == TimeAction.Snap && c.HourChanged && c.NewHours == 23.5f, "backward across midnight");
    }

    static void SamplerTriggers()
    {
        var s = new HostTimeSampler(); RegionClock a;
        double t = 0; float h = 8f; float rate = TimeMath.NominalRate;
        Expect(s.Sample(t, true, 1, h, out a) && Math.Abs(a.Rate - rate) < 1e-6f, "first commit uses nominal rate");
        int commits = 0;
        for (int i = 1; i <= 480; i++) { t = i / 60.0; h = 8f + rate * (float)t; if (s.Sample(t, true, 1, h, out a)) commits++; }
        Expect(commits <= 1, "steady clock churn: " + commits);
        bool refreshed = false;
        for (int i = 481; i <= 900; i++) { t = i / 60.0; h = 8f + rate * (float)t; if (s.Sample(t, true, 1, h, out a)) refreshed = true; }
        Expect(refreshed, "periodic refresh");
        bool fast = false;
        for (int i = 0; i < 120; i++) { t += 1 / 60.0; h = TimeMath.Wrap24(h + 2f / 60f); if (s.Sample(t, true, 1, h, out a) && a.Rate > 1f) fast = true; }
        Expect(fast, "sleep fast-forward rate committed");
        Expect(s.Sample(t + 0.01, true, 0, h, out a) && a.Mode == 0 && a.Rate == 0, "locked mode committed with rate 0");
        Expect(!s.Sample(t + 0.02, false, 1, 0, out a), "invalid reading never commits");
        Expect(s.Sample(t + 0.03, true, 1, 3f, out a) && a.Hours == 3f, "jumped reading committed");
    }

    static void IndependentClocks()
    {
        var wh = Split();
        double t = 100;
        Expect(wh.Clocks.Report(Madison, 1, new RegionClock { Hours = 12f + TimeMath.NominalRate * 100, Rate = TimeMath.NominalRate, Mode = 1 }, t, true, wh.Outbox) != ProposalCode.Invalid, "host report");
        Expect(Propose(wh, ReportP(Hillville, 16f + TimeMath.NominalRate * 100, TimeMath.NominalRate), t) != ProposalCode.Invalid, "client owner report");
        Near(TimeMath.SignedDiff(wh.Clocks.Model(Hillville, t), wh.Clocks.Model(Madison, t)), 4f, 0.01f, "regions keep their own times");
        Expect(RegionDirectory.OwnerOf(wh.Reg, Madison) == 1 && RegionDirectory.OwnerOf(wh.Reg, Hillville) == 2, "owners");
    }

    static void HostJoinsClientRegion()
    {
        var wh = Split();
        float hill = wh.Clocks.Model(Hillville, 50);
        wh.SetPlayerRegion(1, Regions.None, 50);   // host travels (loading screen)
        wh.SetPlayerRegion(1, Hillville, 60);
        Near(wh.Clocks.Model(Hillville, 60), TimeMath.Wrap24(hill + TimeMath.NominalRate * 10), 1e-3f, "host arrival must not overwrite Hillville's time");
        Expect(!ClockOf(wh.Reg, Madison).Running, "Madison frozen after the host left");
        var adopt = TimeMath.Decide(12.7f, wh.Clocks.Model(Hillville, 60), 0f, true);
        Expect(adopt.Action == TimeAction.Snap && Math.Abs(adopt.NewHours - wh.Clocks.Model(Hillville, 60)) < 1e-4f && adopt.HourChanged, "host adopts Hillville time (tick suppressed)");
        Expect(RegionDirectory.OwnerOf(wh.Reg, Hillville) == 1, "host now owns Hillville");
    }

    static void ClientJoinsHostRegion()
    {
        var wh = Split();
        float mad = wh.Clocks.Model(Madison, 30);
        Propose(wh, RegionP(Regions.None), 30);
        Expect(Propose(wh, RegionP(Madison), 40) == ProposalCode.Accepted, "enter Madison");
        Near(wh.Clocks.Model(Madison, 40), TimeMath.Wrap24(mad + TimeMath.NominalRate * 10), 1e-3f, "client arrival must not overwrite Madison");
        Expect(!ClockOf(wh.Reg, Hillville).Running, "Hillville frozen");
        Expect(RegionDirectory.OwnerOf(wh.Reg, Madison) == 1, "host stays owner");
    }

    static void DeterministicInit()
    {
        var wh = new WorldHost(3) { FallbackHours = 9f };
        Expect(Propose(wh, RegionP(3), 0) == ProposalCode.Accepted, "client first region");
        Near(ClockOf(wh.Reg, 3).Hours, 9f, 1e-4f, "first-ever region from session base clock");
        wh.SetPlayerRegion(1, Hideout, 0);
        wh.Clocks.Report(Hideout, 1, new RegionClock { Hours = 14f, Rate = TimeMath.NominalRate, Mode = 1 }, 0, true, wh.Outbox);
        Propose(wh, RegionP(Hideout), 0);
        Propose(wh, RegionP(Regions.None), 0);
        Propose(wh, RegionP(Madison), 0);
        Near(ClockOf(wh.Reg, Madison).Hours, 14f, 1e-4f, "new region continues the traveller's previous region time");
    }

    static void SameRegionHostSleep()
    {
        var wh = new WorldHost(4) { FallbackHours = 22f };
        wh.SetPlayerRegion(1, Hideout, 0);
        Propose(wh, RegionP(Hideout), 0);
        var s = new HostTimeSampler(); RegionClock a;
        float local = 22f; double t = 0;
        for (int i = 0; i < 300; i++)   // 5 s of PassTime fast-forward at 2 h/s => 10 h
        {
            t = i / 60.0; local = TimeMath.Wrap24(local + 2f / 60f);
            if (s.Sample(t, true, 1, local, out a)) wh.Clocks.Report(Hideout, 1, a, t, true, wh.Outbox);
        }
        Near(TimeMath.SignedDiff(wh.Clocks.Model(Hideout, t), local), 0f, 0.05f, "region clock follows the sleeping host");
        var c = TimeMath.Decide(22.01f, wh.Clocks.Model(Hideout, t), 0.016f, false);
        Expect(c.Action == TimeAction.Snap && c.HourChanged, "client in the region jumps with it (tick suppressed)");
    }

    static void SameRegionClientSleep()
    {
        var wh = new WorldHost(5) { FallbackHours = 10f };
        wh.SetPlayerRegion(1, Hideout, 0);
        Propose(wh, RegionP(Hideout), 0);
        uint rev; int sr; float sh;
        Begin(wh, Hideout, 10f, 0);   // host time-lapse not started here (pure test): catch-up path
        var code = wh.HandleProposal("cli", 2, _pid++, SleepP(Hideout, 10f, 8f), 0, out rev, out sr, out sh);
        Expect(code == ProposalCode.Accepted && sr == Hideout && sh == 18f, "client sleep accepted in the host's region");
        float local = 10.3f; int ticks = 0; bool final = false; int guard = 0;
        while (!final && guard++ < 50) { float next = TimeMath.StepPlan(local, wh.Clocks.Model(Hideout, 0), out final); if (!final) ticks++; local = next; }
        Expect(final && Math.Abs(local - 18f) < 1e-4f && ticks == 8, "host catches up 10:18 -> 18:00 with 8 hourly steps, got " + ticks);
    }

    static void SleepIsolation()
    {
        var wh = Split();
        Begin(wh, Hillville, 16f, 10);
        Expect(Propose(wh, SleepP(Hillville, 16f, 6f), 10) == ProposalCode.Accepted, "sleep");
        Near(ClockOf(wh.Reg, Hillville).Hours, 22f, 1e-4f, "Hillville advanced");
        Near(wh.Clocks.Model(Madison, 10), 12f + TimeMath.NominalRate * 10, 1e-3f, "Madison unaffected");
    }

    static void RegionPersistsWhenEmpty()
    {
        var wh = Split();
        Propose(wh, RegionP(Regions.None), 0);   // client leaves Hillville
        Expect(!ClockOf(wh.Reg, Hillville).Running, "frozen");
        Near(wh.Clocks.Model(Hillville, 5000), 16f, 1e-4f, "unchanged while empty");
        Propose(wh, RegionP(Hillville), 5000);
        Expect(ClockOf(wh.Reg, Hillville).Running, "resumed");
        Near(wh.Clocks.Model(Hillville, 5010), 16f + TimeMath.NominalRate * 10, 1e-3f, "runs again from 16:00");
    }

    static void ReconnectRestore()
    {
        var wh = Split();
        var cli = StateRegistry.CreateClient();
        Transfer(wh.Reg, cli);
        cli.Reset();
        Propose(wh, RegionP(Regions.None), 20);   // client disconnected: Hillville frozen
        wh.Clocks.RefreshRunning(200, wh.Outbox);
        Transfer(wh.Reg, cli);
        Near(ClockOf(cli, Madison).Hours, 12f + TimeMath.NominalRate * 200, 1e-3f, "fresh anchor for the running region");
        var hv = ClockOf(cli, Hillville);
        Expect(!hv.Running && Math.Abs(hv.Hours - (16f + TimeMath.NominalRate * 20)) < 1e-3f, "frozen region restored");
    }

    static void DuplicateSleep()
    {
        var wh = Split();
        uint rev; int sr; float sh;
        Begin(wh, Hillville, 16f, 0);
        uint id = _pid + 5; _pid += 10;
        Expect(wh.HandleProposal("cli", 2, id, SleepP(Hillville, 16f, 8f), 0, out rev, out sr, out sh) == ProposalCode.Accepted && sr == Hillville, "first");
        Expect(wh.HandleProposal("cli", 2, id, SleepP(Hillville, 16f, 8f), 0, out rev, out sr, out sh) == ProposalCode.Accepted && sr == Regions.None, "retransmission answered from cache, not applied");
        Expect(wh.HandleProposal("cli", 2, id - 1, SleepP(Hillville, 16f, 8f), 0, out rev, out sr, out sh) == ProposalCode.Stale, "older id stale");
        Expect(wh.HandleProposal("cli", 2, id + 1, SleepP(Hillville, 16f, 8f), 0, out rev, out sr, out sh) == ProposalCode.Invalid && sr == Regions.None, "same sleep again under a new id: no accepted start -> refused");
        Near(ClockOf(wh.Reg, Hillville).Hours, 0f, 1e-4f, "advanced exactly once (16 + 8 = 24 -> 00:00)");
    }

    static void ReportValidation()
    {
        var wh = Split();
        float m = wh.Clocks.Model(Hillville, 0);
        Expect(Propose(wh, ReportP(Hillville, TimeMath.Wrap24(m + 0.5f), TimeMath.NominalRate), 0) == ProposalCode.Invalid, "jump via report");
        Expect(Propose(wh, ReportP(Hillville, m, 0.2f), 0) == ProposalCode.Invalid, "rate too high via report");
        Expect(Propose(wh, ReportP(Hillville, m + 0.05f, TimeMath.NominalRate), 0) == ProposalCode.Accepted, "drift within tolerance");
        Expect(Propose(wh, ReportP(Madison, 12f, TimeMath.NominalRate), 0) == ProposalCode.NotAuthorized, "not owner of Madison");
        Expect(Propose(wh, SleepP(Madison, 12f, 2f), 0) == ProposalCode.NotAuthorized, "sleep outside own region");
        Expect(Propose(wh, StartP(Hillville, TimeMath.Wrap24(m + 3f), 3), 0) == ProposalCode.Invalid, "sleep start far from region clock");
        Expect(Propose(wh, SleepP(Hillville, m, 2f), 0) == ProposalCode.Invalid, "sleep without accepted start");
        Expect(Propose(wh, new StateEntry { Scope = StateScope.Region, ScopeArg = Hillville, Key = WorldKeys.Sleep, Value = new byte[3] }, 0) == ProposalCode.Invalid, "malformed sleep");
        Propose(wh, RegionP(Madison), 0);
        Expect(Propose(wh, ReportP(Madison, wh.Clocks.Model(Madison, 0), TimeMath.NominalRate), 0) == ProposalCode.NotAuthorized, "host-owned region: client is follower");
    }

    // Mirrors DayNightTime.Update: one tick per hour-index change; day rollover on reaching hour 0.
    // ---------------- M3.1.1 client sleep transaction ----------------
    static void SleepTxCompleted()
    {
        var tx = new SleepTransaction(); SleepRequest req;
        Expect(tx.Start(Hillville, 23.87f) && tx.Sleeping, "start");
        Expect(tx.End(Hillville, 4.42f, out req) == SleepEnd.Ended, "passingTime edge ends the sleep");
        Near(req.From, 23.87f, 1e-5f, "from"); Near(req.Advance, 4.55f, 1e-3f, "advance across midnight");
        Expect(!tx.Sleeping && tx.End(Hillville, 4.5f, out req) == SleepEnd.NotSleeping, "StopIt afterwards does not end it twice");
        SleepRequest parsed;
        Expect(SleepRequest.TryParse(new SleepRequest { From = 23.87f, Advance = 4.55f }.ToBytes(), out parsed), "request encodes");
    }

    static void SleepTxDuplicateStart()
    {
        var tx = new SleepTransaction(); SleepRequest req;
        tx.Start(Hillville, 4.45f);
        Expect(!tx.Start(Hillville, 5.9f), "second PassStart ignored");
        tx.End(Hillville, 8.0f, out req);
        Near(req.Advance, 3.55f, 1e-3f, "advance measured from the first start");
    }

    static void SleepTxRegionChange()
    {
        var tx = new SleepTransaction(); SleepRequest req;
        tx.Start(Hillville, 10f);
        Expect(tx.End(Madison, 12f, out req) == SleepEnd.OtherRegion, "ended in another region -> not sent");
        tx.Start(Hillville, 10f); tx.Cancel();
        Expect(tx.End(Hillville, 12f, out req) == SleepEnd.NotSleeping, "cancelled by region change");
        tx.Sent(7, 0); tx.Cancel();
        Expect(!tx.Awaiting && !tx.IsPending(7), "pending result dropped on region change");
    }

    static void SleepTxResults()
    {
        var tx = new SleepTransaction();
        tx.Sent(151, 100);
        Expect(tx.Awaiting && tx.IsPending(151) && !tx.IsPending(152) && !tx.IsPending(0), "only the request id matches");
        Expect(!tx.TimedOut(100 + SleepTransaction.AckTimeout - 0.1), "not yet timed out");
        Expect(tx.TimedOut(100 + SleepTransaction.AckTimeout) && !tx.Awaiting, "timeout clears the hold");
        Expect(!tx.TimedOut(1000), "timeout fires once");
        tx.Sent(153, 0); tx.Resolve();
        Expect(!tx.Awaiting, "result / disconnect resolves");
    }

    // Client owner of Hillville (host in Madison) at 23:52; reports before sleep, none while the sleep UI is open,
    // request after the sleep, then a fresh post-sleep report. Mirrors WorldSync.ClientFrame / OnSleepEnded.
    static void ClientSleepAloneEndToEnd()
    {
        var wh = new WorldHost(11) { FallbackHours = 23.8667f };
        wh.SetPlayerRegion(1, Madison, 0);
        Expect(Propose(wh, RegionP(Hillville), 0) == ProposalCode.Accepted, "client region");
        var cli = StateRegistry.CreateClient(); Transfer(wh.Reg, cli); wh.Outbox.Clear();
        float madison0 = wh.Clocks.Model(Madison, 0);
        double t = 0; float local = wh.Clocks.Model(Hillville, 0);
        var s = new HostTimeSampler(); RegionClock a;
        for (int i = 0; i < 60; i++) { t += 1 / 60.0; local = TimeMath.Wrap24(local + TimeMath.NominalRate / 60f); if (s.Sample(t, true, 1, local, out a)) Expect(Propose(wh, ReportP(Hillville, a.Hours, a.Rate), t) == ProposalCode.Accepted, "pre-sleep report"); }
        var tx = new SleepTransaction(); tx.Start(Hillville, local);
        Expect(Propose(wh, StartP(Hillville, local, 4), t) == ProposalCode.Accepted, "sleep start accepted");
        for (int i = 0; i < 137; i++) { t += 1 / 60.0; local = TimeMath.Wrap24(local + 2f / 60f); }   // ~4.55 h fast-forward, no reports
        SleepRequest req;
        Expect(tx.End(Hillville, local, out req) == SleepEnd.Ended && req.Advance >= SleepTransaction.MinAdvance, "request generated");
        uint rev; int sr; float sh; uint id = _pid++;
        Expect(wh.HandleProposal("cli", 2, id, SleepP(Hillville, req.From, req.Advance), t, out rev, out sr, out sh) == ProposalCode.Accepted && sr == Hillville && !wh.SleepWatched, "host accepts (host elsewhere: commit)");
        Near(sh, local, 1e-3f, "host commits the client's slept time");
        foreach (var e in wh.Outbox) Expect(Deliver(wh.Reg, e, cli) == DeltaResult.Applied, "delta before result");
        wh.Outbox.Clear(); tx.Sent(id, t);
        Expect(tx.IsPending(id), "result matches"); tx.Resolve();
        Near(ClockOf(cli, Hillville).Hours, local, 1e-3f, "client registry has the new Hillville time");
        var follow = TimeMath.Decide(local, ClockOf(cli, Hillville).Hours, 0f, true);
        Expect(follow.Action == TimeAction.None, "adopting the committed clock does not move the client back");
        s.Reset();
        for (int i = 0; i < 900; i++) { t += 1 / 60.0; local = TimeMath.Wrap24(local + TimeMath.NominalRate / 60f); if (s.Sample(t, true, 1, local, out a)) { var pc = Propose(wh, ReportP(Hillville, a.Hours, a.Rate), t); Expect(pc == ProposalCode.Accepted || pc == ProposalCode.Unchanged, "post-sleep report refused: " + pc); } }
        Near(TimeMath.SignedDiff(wh.Clocks.Model(Hillville, t), local), 0f, 0.02f, "host model stays on the slept clock");
        Near(wh.Clocks.Model(Madison, t), TimeMath.Wrap24(madison0 + TimeMath.NominalRate * t), 1e-3f, "Madison unaffected");
    }

    static void PostSleepReportWithoutRequest()
    {
        var wh = Split();   // Hillville 16:00, client owner
        Expect(Propose(wh, ReportP(Hillville, 20.5f, TimeMath.NominalRate), 0) == ProposalCode.Invalid, "a 4.5 h jump through a report is refused (the request is the only path)");
        Near(wh.Clocks.Model(Hillville, 0), 16f, 1e-4f, "region clock unchanged");
    }

    static void ClientSleepMidnight()
    {
        var wh = new WorldHost(12) { FallbackHours = 22.5f };
        wh.SetPlayerRegion(1, Madison, 0);
        Propose(wh, RegionP(Hillville), 0);
        var tx = new SleepTransaction(); SleepRequest req;
        tx.Start(Hillville, 22.5f); tx.End(Hillville, 6.0f, out req);
        Near(req.Advance, 7.5f, 1e-4f, "advance");
        Begin(wh, Hillville, 22.5f, 0);
        Expect(Propose(wh, SleepP(Hillville, req.From, req.Advance), 0) == ProposalCode.Accepted, "accepted");
        Near(ClockOf(wh.Reg, Hillville).Hours, 6f, 1e-4f, "06:00");
        Near(wh.Clocks.Model(Madison, 0), 22.5f, 1e-4f, "Madison unaffected");
    }

    static void ClientSleepSharedRegion()
    {
        var wh = new WorldHost(13) { FallbackHours = 21f };
        wh.SetPlayerRegion(1, Hillville, 0);
        Propose(wh, RegionP(Hillville), 0);
        var cli = StateRegistry.CreateClient(); Transfer(wh.Reg, cli); wh.Outbox.Clear();
        double t = 3;   // ~3 s of fast-forward; host clock kept running at nominal rate meanwhile
        uint rev; int sr; float sh;
        Begin(wh, Hillville, 21f, 0);
        uint sid = _pid++;
        Expect(wh.HandleProposal("cli", 2, sid, SleepP(Hillville, 21f, 6f), t, out rev, out sr, out sh) == ProposalCode.Accepted && sr == Hillville, "accepted despite normal drift during sleep");
        foreach (var e in wh.Outbox) Deliver(wh.Reg, e, cli);
        wh.Outbox.Clear();
        Near(ClockOf(cli, Hillville).Hours, 3f, 1e-4f, "client sees 03:00");
        float host = TimeMath.Wrap24(21f + TimeMath.NominalRate * t); var dn = new FakeDayNight(host);
        bool final = false; int guard = 0;
        while (!final && guard++ < 50) { host = TimeMath.StepPlan(host, wh.Clocks.Model(Hillville, t), out final); dn.Frame(host); }
        Near(host, 3f, 1e-4f, "host caught up");
        Expect(dn.Ticks == 6 && dn.Days == 1, "exactly 6 hourly ticks and 1 day: " + dn.Ticks + "/" + dn.Days);
        Expect(wh.HandleProposal("cli", 2, sid, SleepP(Hillville, 21f, 6f), t, out rev, out sr, out sh) == ProposalCode.Accepted && sr == Regions.None, "retransmission: no second catch-up");
    }

    static void SleepStaleProtection()
    {
        var wh = Split();   // Hillville 16:00
        var cli = StateRegistry.CreateClient(); Transfer(wh.Reg, cli);
        var oldClock = cli.Get(StateScope.Region, Hillville, WorldKeys.Clock);
        var old = new StateEntry { Scope = oldClock.Scope, ScopeArg = oldClock.ScopeArg, Key = oldClock.Key, Rev = oldClock.Rev, Value = oldClock.Value };
        uint b = _pid; _pid += 10;
        Expect(Propose(wh, StartP(Hillville, 16f, 21), 1, b + 1) == ProposalCode.Accepted, "start");
        Expect(Propose(wh, SleepP(Hillville, 16f, 5f), 1, b + 3) == ProposalCode.Accepted, "sleep");
        Expect(Propose(wh, ReportP(Hillville, 16.01f, TimeMath.NominalRate), 1, b + 2) == ProposalCode.Stale, "report sent before the sleep but delivered late is stale");
        Expect(Propose(wh, ReportP(Hillville, 16.01f, TimeMath.NominalRate), 1, b + 4) == ProposalCode.Invalid, "pre-sleep time reported afterwards is refused");
        Near(ClockOf(wh.Reg, Hillville).Hours, 21f, 1e-4f, "accepted sleep stays");
        foreach (var e in wh.Outbox) Deliver(wh.Reg, e, cli);
        Expect(cli.ApplyDelta(wh.Reg.Epoch, old) != DeltaResult.Applied, "old clock revision cannot overwrite");
        Near(ClockOf(cli, Hillville).Hours, 21f, 1e-4f, "client keeps 21:00");
    }

    static void SleepAfterRegionChange()
    {
        var wh = Split();
        Expect(Propose(wh, RegionP(Madison), 0) == ProposalCode.Accepted, "client travelled");
        float frozen = ClockOf(wh.Reg, Hillville).Hours;
        Expect(Propose(wh, SleepP(Hillville, 16f, 3f), 0) == ProposalCode.NotAuthorized, "sleep for the old region refused");
        Near(ClockOf(wh.Reg, Hillville).Hours, frozen, 1e-4f, "Hillville untouched");
    }

    static void SleepReconnect()
    {
        var wh = Split();   // Hillville 16:00 after sleep
        Begin(wh, Hillville, 16f, 0);
        Expect(Propose(wh, SleepP(Hillville, 16f, 4f), 0) == ProposalCode.Accepted, "second sleep");
        var cli = StateRegistry.CreateClient(); Transfer(wh.Reg, cli);
        Near(ClockOf(cli, Hillville).Hours, 20f, 1e-3f, "reconnected client gets 20:00");
        wh.SetPlayerRegion(2, Regions.None, 5);   // disconnect
        Expect(!ClockOf(wh.Reg, Hillville).Running, "region frozen while empty");
        Near(ClockOf(wh.Reg, Hillville).Hours, TimeMath.Wrap24(20f + TimeMath.NominalRate * 5), 1e-3f, "frozen at the slept time + elapsed");
    }

    // ---------------- M3.1.2 shared sleep time-lapse ----------------
    static WorldHost Together(float hours, uint epoch)
    {
        var wh = new WorldHost(epoch) { FallbackHours = hours };
        wh.SetPlayerRegion(1, Hillville, 0);
        Expect(Propose(wh, RegionP(Hillville), 0) == ProposalCode.Accepted, "client joins host region");
        wh.Outbox.Clear();
        return wh;
    }

    // Frame simulation of a client sleep in the host's region (mirrors PassTime.Update on the client and
    // WorldSync.LapseFrame on the host). delayFrames = network delay of the start; cancelAt = client Esc time (NaN: run to target).
    sealed class LapseRun { public float Host, Client, ClientEnd; public bool HostMovedBeforeEnd; public int HostTicks, HostDays, Frames; public bool Backward; }

    static LapseRun RunSharedSleep(WorldHost wh, float from, int target, int delayFrames, float cancelAt)
    {
        const float dt = 1 / 60f, fast = 24f / (TimeMath.SleepCycleMinutes * 60f);
        var r = new LapseRun { Host = from, Client = from };
        var dn = new FakeDayNight(from); var s = new HostTimeSampler(); RegionClock a;
        Expect(Propose(wh, StartP(Hillville, from, target), 0) == ProposalCode.Accepted, "start accepted");
        Expect(wh.Active != null && wh.Active.Region == RegionDirectory.RegionOf(wh.Reg, 1), "host shares the region");
        bool clientSleeping = true, hostLapse = false, ended = false; float until = float.NaN; double t = 0;
        for (int f = 0; f < 60 * 40 && (clientSleeping || hostLapse); f++)
        {
            t += dt; r.Frames++;
            if (f == delayFrames) { wh.SetWatching(true); hostLapse = true; }
            float prevHost = r.Host;
            r.Host = TimeMath.Wrap24(r.Host + (hostLapse ? fast : TimeMath.NominalRate) * dt); dn.Frame(r.Host);
            if (TimeMath.SignedDiff(r.Host, prevHost) < 0) r.Backward = true;
            if (s.Sample(t, true, 1, r.Host, out a)) wh.Clocks.Report(Hillville, 1, a, t, true, wh.Outbox);
            if (clientSleeping)
            {
                r.Client = TimeMath.Wrap24(r.Client + fast * dt);
                bool stop = (int)r.Client == target || (!float.IsNaN(cancelAt) && TimeMath.ForwardDiff(r.Client, from) >= cancelAt);
                if (stop)
                {
                    clientSleeping = false; r.ClientEnd = r.Client;
                    if (TimeMath.ForwardDiff(r.Host, from) > 0.5f) r.HostMovedBeforeEnd = true;
                    uint rev; int sr; float sh; uint id = _pid++;
                    var code = wh.HandleProposal("cli", 2, id, SleepP(Hillville, from, TimeMath.ForwardDiff(r.Client, from)), t, out rev, out sr, out sh);
                    Expect(code == ProposalCode.Accepted && sr == Hillville && wh.SleepWatched, "end accepted as watched: " + code);
                    until = sh;
                    Expect(wh.HandleProposal("cli", 2, id, SleepP(Hillville, from, TimeMath.ForwardDiff(r.Client, from)), t, out rev, out sr, out sh) == ProposalCode.Accepted && sr == Regions.None, "duplicate end not applied");
                    Expect(wh.Active == null, "transaction closed");
                    ended = true;
                }
            }
            else r.Client = TimeMath.Wrap24(r.Client + TimeMath.NominalRate * dt);
            if (hostLapse && TimeMath.LapseDone(r.Host, (int)r.Host, target, ended ? until : float.NaN)) hostLapse = false;
        }
        r.HostTicks = dn.Ticks; r.HostDays = dn.Days;
        return r;
    }

    static void SleepStartCodec()
    {
        SleepStart s;
        Expect(SleepStart.TryParse(new SleepStart { From = 22.5f, TargetHour = 6 }.ToBytes(), out s) && s.TargetHour == 6 && s.From == 22.5f, "round trip");
        Expect(new SleepStart().ToBytes().Length == SleepStart.WireSize, "5 bytes");
        Expect(!SleepStart.TryParse(new SleepStart { From = 22.5f, TargetHour = 24 }.ToBytes(), out s), "hour 24 invalid");
        Expect(!SleepStart.TryParse(new SleepStart { From = float.NaN, TargetHour = 6 }.ToBytes(), out s) && !SleepStart.TryParse(new byte[4], out s), "NaN / short invalid");
        var w = new NetDataWriter(); StateMessages.WriteResult(w, 9, ProposalCode.Busy, 3);
        var r = Reader(w); r.GetByte(); uint id, rev; ProposalCode code;
        Expect(StateMessages.TryReadResult(r, out id, out code, out rev) && code == ProposalCode.Busy, "Busy result parses");
    }

    static void LapseRule()
    {
        Expect(!TimeMath.LapseDone(5.9f, 5, 6, float.NaN) && TimeMath.LapseDone(6.01f, 6, 6, float.NaN), "game rule: hour index == target");
        Expect(!TimeMath.LapseDone(23.9f, 23, 0, float.NaN) && TimeMath.LapseDone(0.02f, 0, 0, float.NaN), "target midnight");
        Expect(!TimeMath.LapseDone(5.95f, 5, 6, 6.02f) && TimeMath.LapseDone(6.03f, 6, 6, 6.02f), "settle: behind continues, reached stops");
        Expect(TimeMath.LapseDone(6.2f, 6, 6, 6.02f), "settle: already ahead stops (no backward write)");
        Expect(!TimeMath.LapseDone(23.95f, 23, 0, 0.05f) && TimeMath.LapseDone(0.06f, 0, 0, 0.05f), "settle across midnight");
    }

    static void SharedSleepWatched()
    {
        var wh = Together(22f, 21);
        var r = RunSharedSleep(wh, 22f, 6, 3, float.NaN);
        Expect(r.HostMovedBeforeEnd, "host sky fast-forwards before the client's completion arrives");
        Expect(!r.Backward, "host clock never moved backward");
        Near(Math.Abs(TimeMath.SignedDiff(r.Host, r.Client)), 0f, 0.1f, "both end on the same time");
        Expect((int)r.Host == 6, "host ended in the target hour: " + r.Host);
        var c = ClockOf(wh.Reg, Hillville);
        Near(TimeMath.SignedDiff(c.Hours, r.Host), 0f, 0.15f, "region clock follows the host, not a second from+advance jump");
        Expect(c.Rate <= RegionClockBook.MaxReportRate, "no fast rate left after the end");
    }

    static void SharedSleepMidnightTicks()
    {
        var wh = Together(22.3f, 22);
        var r = RunSharedSleep(wh, 22.3f, 6, 2, float.NaN);
        Expect(r.HostTicks == 8 && r.HostDays == 1, "host ticks/days = " + r.HostTicks + "/" + r.HostDays + " (expected 8/1)");
    }

    static void SharedSleepCancelled()
    {
        var wh = Together(21f, 23);
        var r = RunSharedSleep(wh, 21f, 7, 4, 2.3f);   // Esc after 2.3 h
        Expect(!r.Backward, "no backward write on the host");
        Expect(TimeMath.SignedDiff(r.Host, r.ClientEnd) >= -1e-4f && TimeMath.SignedDiff(r.Host, r.ClientEnd) < 0.1f, "host settled at the sleeper's end " + r.ClientEnd + " (host " + r.Host + ")");
        Expect(r.HostTicks == 2, "2 hourly ticks for 2.3 h, got " + r.HostTicks);
        // host already ahead when the end arrives: stops at once, stays ahead (client follows the host)
        Expect(TimeMath.LapseDone(23.4f, 23, 7, 23.3f), "ahead host stops immediately");
    }

    static void SleepBusy()
    {
        var wh = Together(20f, 24);
        Begin(wh, Hillville, 20f, 0);
        Expect(Propose(wh, StartP(Hillville, 20f, 2), 0) == ProposalCode.Busy, "second start while one runs");
        var wh2 = Together(20f, 25);
        wh2.HostSleeping = true;
        Expect(Propose(wh2, StartP(Hillville, 20f, 2), 0) == ProposalCode.Busy, "host's own sleep in that region wins");
        var wh3 = Split(); wh3.HostSleeping = true;   // host sleeps in Madison, client in Hillville
        Expect(Propose(wh3, StartP(Hillville, 16f, 20), 0) == ProposalCode.Accepted, "host sleeping elsewhere does not block");
    }

    static void SleepHostElsewhere()
    {
        var wh = Split();   // host Madison 12:00, client Hillville 16:00
        Begin(wh, Hillville, 16f, 1);
        Expect(wh.Active.Region != RegionDirectory.RegionOf(wh.Reg, 1) && !wh.Active.HostWatching, "host elsewhere: not watching");
        uint rev; int sr; float sh;
        Expect(wh.HandleProposal("cli", 2, _pid++, SleepP(Hillville, 16f, 8f), 5, out rev, out sr, out sh) == ProposalCode.Accepted && !wh.SleepWatched && sh == 0f, "commit 00:00");
        Near(ClockOf(wh.Reg, Hillville).Hours, 0f, 1e-4f, "Hillville advanced");
        Near(wh.Clocks.Model(Madison, 5), 12f + TimeMath.NominalRate * 5, 1e-3f, "Madison unchanged");
    }

    static void SleepReorder()
    {
        var wh = Together(10f, 26);
        uint b = _pid; _pid += 10;
        Expect(Propose(wh, SleepP(Hillville, 10f, 4f), 0, b + 2) == ProposalCode.Invalid, "end arriving before its start is refused");
        Expect(Propose(wh, StartP(Hillville, 10f, 14), 0, b + 1) == ProposalCode.Stale, "older start after a newer id is stale");
        Near(ClockOf(wh.Reg, Hillville).Hours, 10f, 1e-4f, "no advance");
        Expect(Propose(wh, StartP(Hillville, 10f, 14), 0, b + 3) == ProposalCode.Accepted, "fresh start");
        Expect(Propose(wh, StartP(Hillville, 10f, 14), 0, b + 3) == ProposalCode.Accepted && wh.Active.StartId == b + 3, "duplicate start: cached, still one transaction");
        Expect(Propose(wh, SleepP(Hillville, 10.5f, 4f), 0, b + 4) == ProposalCode.Invalid && wh.Active != null, "end with a different start time is refused");
        Expect(Propose(wh, SleepP(Hillville, 10f, 4f), 0, b + 5) == ProposalCode.Accepted && Propose(wh, SleepP(Hillville, 10f, 4f), 0, b + 6) == ProposalCode.Invalid, "completed once; a later end has no start");
        Near(ClockOf(wh.Reg, Hillville).Hours, 14f, 1e-4f, "advanced exactly once");
    }

    static void SleepInterrupts()
    {
        // client disconnects during a watched sleep: transaction dropped, fast rate normalized, host keeps its advanced time
        var wh = Together(22f, 27);
        Begin(wh, Hillville, 22f, 0); wh.SetWatching(true);
        wh.Clocks.Report(Hillville, 1, new RegionClock { Hours = 23.5f, Rate = 2f, Mode = 1, Running = true }, 1, true, wh.Outbox);
        wh.SetPlayerRegion(2, Regions.None, 1.5);
        Expect(wh.Active == null, "dropped on disconnect");
        var c = ClockOf(wh.Reg, Hillville);
        Expect(c.Rate <= RegionClockBook.MaxReportRate && Math.Abs(TimeMath.SignedDiff(c.Hours, 0.5f)) < 0.01f, "normal rate from the reached time: " + c);
        // sleeper changes region: dropped, later end refused
        wh = Together(9f, 28);
        Begin(wh, Hillville, 9f, 0);
        Expect(Propose(wh, RegionP(Madison), 0) == ProposalCode.Accepted && wh.Active == null, "dropped on sleeper travel");
        Expect(Propose(wh, SleepP(Hillville, 9f, 3f), 0) == ProposalCode.NotAuthorized, "stale end after travel refused");
        // host leaves during a watched sleep: completion falls back to a validated commit
        wh = Together(9f, 29);
        Begin(wh, Hillville, 9f, 0); wh.SetWatching(true);
        wh.SetPlayerRegion(1, Madison, 1);
        Expect(wh.Active != null && !wh.Active.HostWatching, "host no longer watching");
        uint rev; int sr; float sh;
        Expect(wh.HandleProposal("cli", 2, _pid++, SleepP(Hillville, 9f, 3f), 2, out rev, out sr, out sh) == ProposalCode.Accepted && !wh.SleepWatched, "commit path");
        Near(ClockOf(wh.Reg, Hillville).Hours, 12f, 1e-4f, "Hillville 12:00");
        Near(ClockOf(wh.Reg, Hillville).Rate, TimeMath.NominalRate, 1e-6f, "normal rate");
        // lost completion expires
        wh = Together(9f, 30);
        Begin(wh, Hillville, 9f, 0);
        Expect(!wh.ExpireSleep(WorldHost.SleepMaxSeconds - 1) && wh.ExpireSleep(WorldHost.SleepMaxSeconds) && wh.Active == null, "expiry");
        Expect(Propose(wh, StartP(Hillville, wh.Clocks.Model(Hillville, 41), 12), 41) == ProposalCode.Accepted, "new sleep possible afterwards");
    }

    static void SleepTxStartGate()
    {
        var tx = new SleepTransaction(); SleepRequest req;
        tx.Start(Hillville, 22f); tx.StartSent(40);
        Expect(tx.IsStart(40) && tx.MayComplete, "pending start may complete (ordered before the end)");
        tx.StartResult(false);
        tx.End(Hillville, 23f, out req);
        Expect(!tx.MayComplete && tx.StartState == SleepStartState.Rejected, "rejected start blocks the request");
        tx.Start(Hillville, 22f); tx.StartSent(41); tx.StartResult(true);
        Expect(tx.MayComplete && !tx.IsStart(41), "accepted");
        tx.Disconnected();
        Expect(!tx.MayComplete, "disconnect during sleep blocks the request (host dropped the transaction)");
        tx.Cancel();
        Expect(tx.Start(Hillville, 22f), "new sleep");
        Expect(!tx.MayComplete, "never announced (offline) -> no request");
    }

    sealed class FakeDayNight
    {
        public int Prev, Ticks, Days;
        public FakeDayNight(float h) { Prev = (int)h; }
        public void Frame(float h) { int hr = (int)h; if (hr != Prev) { Prev = hr; Ticks++; if (hr == 0) Days++; } }
        public void Suppress(float h) { Prev = (int)h; }
    }

    static void HourlyTickAccounting()
    {
        float local = 23.9f; var dn = new FakeDayNight(local);
        foreach (var target in new[] { 0.2f, 23.7f, 3.5f, 22.1f, 22.15f, 1.0f })
        {
            for (int i = 0; i < 400; i++)
            {
                var c = TimeMath.Decide(local, target, 1 / 60f, false);
                if (c.Action == TimeAction.None) break;
                if (c.HourChanged) dn.Suppress(c.NewHours);
                local = c.NewHours; dn.Frame(local);
            }
        }
        Expect(dn.Ticks == 0 && dn.Days == 0, "corrections produced " + dn.Ticks + " ticks / " + dn.Days + " days");
        local = 22.5f; dn = new FakeDayNight(local);
        bool final = false; int guard = 0;
        while (!final && guard++ < 50) { local = TimeMath.StepPlan(local, 6.25f, out final); dn.Frame(local); }
        Expect(Math.Abs(local - 6.25f) < 1e-4f && dn.Ticks == 8 && dn.Days == 1, "catch-up 22:30 -> 06:15: ticks=" + dn.Ticks + " days=" + dn.Days);
    }

    static void ClientFollows()
    {
        var wh = new WorldHost(42) { FallbackHours = 22.8f };
        wh.SetPlayerRegion(1, Hideout, 0);
        Propose(wh, RegionP(Hideout), 0);
        wh.Outbox.Clear();
        var cli = StateRegistry.CreateClient(); Transfer(wh.Reg, cli);
        var s = new HostTimeSampler(); RegionClock a, anchor = ClockOf(cli, Hideout);
        const double dt = 1 / 60.0;
        float hostH = 22.8f, cliH = 22.6f; double anchorAt = 0;
        int snaps = 0, backward = 0;
        for (int i = 1; i < 60 * 120; i++)
        {
            double t = i * dt;
            hostH = TimeMath.Wrap24(hostH + TimeMath.NominalRate * dt);
            cliH = TimeMath.Wrap24(cliH + TimeMath.NominalRate * 1.03 * dt);
            if (s.Sample(t, true, 1, hostH, out a)) wh.Clocks.Report(Hideout, 1, a, t, true, wh.Outbox);
            foreach (var e in wh.Outbox) { Expect(Deliver(wh.Reg, e, cli) == DeltaResult.Applied, "delta"); if (e.Key == WorldKeys.Clock) { anchor = ClockOf(cli, Hideout); anchorAt = t; } }
            wh.Outbox.Clear();
            var c = TimeMath.Decide(cliH, TimeMath.Extrapolate(anchor, t - anchorAt), (float)dt, false);
            if (c.Action == TimeAction.Snap) snaps++;
            if (c.Action != TimeAction.None) { if (TimeMath.SignedDiff(c.NewHours, cliH) < 0 && i < 300) backward++; cliH = c.NewHours; }
        }
        Expect(snaps == 0, "12-minute offset should blend: snaps=" + snaps);
        Expect(backward == 0, "client behind only moves forward while catching up");
        Near(Math.Abs(TimeMath.SignedDiff(cliH, hostH)), 0f, 0.02f, "converged");
        Expect(hostH < 1f, "crossed midnight");
    }
}
