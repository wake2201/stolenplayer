// H1 offline tests: heavy-object ownership lease (src/Shared/HeavyItems.cs + WorldHost.HeavyTransaction / "heavyTxn" proposals)
// and source scans of the Unity glue (HeavySync, WorldLootSync heavy hook, RemoteGear). Exercises exactly the pure pieces the
// plugin uses: HeavyBook decisions, registry list codec, region / disconnect lifetime, presentation rule, grant preconditions.
// In-game behaviour (original PickUpHeavy / DropHeavy, physics settle, avatar presentation) is manual two-PC QA (TESTING §38).
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class H1Tests
{
    static int _passed, _failed;
    const int Madison = 99, Hillville = 97;
    const uint TV = 0xA1B2C3D4, Printer = 0x0BADF00D, Unknown = 0x12345678;
    const uint Digest = 0x50D2F273;
    static uint _pid;

    static int Main()
    {
        Run("01 wire: record list / request codec round trip, size limits, malformed values refused", Codec);
        Run("02 ownership: P1 lease -> P2 refused; drop commits Placed; P2 leases the placed object; old holder's stale drop refused", Ownership);
        Run("03 simultaneous pickup: whichever the host commits first wins, the other is refused; retransmissions never grant twice", Simultaneous);
        Run("04 drop relocation: first rest pose, later movement re-committed, identical pose Unchanged, wrong holder / region refused", DropRelocation);
        Run("05 release: carry without a drop -> previous rest pose (simulated by nobody) or the original scene pose (record removed)", ReleaseRules);
        Run("06 disconnect while carrying / simulating: host releases the slot's leases; late requests of the old lease are stale", Disconnect);
        Run("07 region transitions: leaving releases, emptied region resets every object, MainScene / ambiguous objects never leased", Regions_);
        Run("08 host validation: catalog Busy / unknown / digest mismatch; capacity; Player 2 can never Remove; host-local storage", HostValidation);
        Run("09 presentation rule + grant preconditions (original interact branch) on the observer / holder", PresentAndGrant);
        Run("10 Unity glue: original PickUpHeavy once after the grant, refusal fallback, drop / deposit hooks, no fake objects, no UI", SourceScan);
        Run("11 H1.1 immediate drop: release pose committed at DropHeavy (Placed+Moving), exact rest pose by Settle, re-movement cycle", ImmediateDrop);
        Run("12 H1.1 stale / out-of-order: a late Drop / Settle / snapshot never overwrites a newer pickup, storage or reset", StaleOrder);
        Run("13 H1.1 motion snapshots: codec, holder + lease + region + sequence filter, buffered before the Drop commit, rejected after the Settle", MotionFilter);
        Run("14 H1.1 interpolation + send policy: reused VehiclePoseBuffer / VehicleSendPolicy, bounded rate, no unchanged sends, exact final pose", MotionBuffer);
        Run("15 H1.1 handover: simulator leaves / disconnects / releases while moving -> other player in the region continues, else scene pose", Handover);
        Run("16 H1.1 cycles + simultaneous requests: repeated pickup / drop / settle by both players, one holder, no duplicate record", Cycles);
        Run("17 H1.1 glue scan: commit at the DropHeavy postfix, no delays / fake objects, lease-driven avatar copy, TAB drops untouched", SourceScanH11);
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

    // ------------------------------------------------------------------------------------------------ helpers
    static WorldHost Host(int p1Region, int p2Region)
    {
        var wh = new WorldHost(9) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, p1Region, 0);
        if (p2Region != Regions.None) wh.SetPlayerRegion(2, p2Region, 0);
        wh.HeavyCatalog = (int ns, uint hash, uint digest, out string why) =>
        {
            why = null;
            if (ns == Hillville) { why = "catalog building"; return ProposalCode.Busy; }
            if (digest != Digest) { why = "digest"; return ProposalCode.NotAuthorized; }
            if (hash != TV && hash != Printer) { why = "unknown"; return ProposalCode.NotAuthorized; }
            return ProposalCode.Accepted;
        };
        wh.Outbox.Clear();
        return wh;
    }
    static HeavyTxnRequest Req(HeavyOp op, uint hash, ushort lease, float x = 0)
    {
        return new HeavyTxnRequest { Op = op, Ns = Madison, Hash = hash, Digest = Digest, Lease = lease, Pos = new Vec3(x, 1, 2), Rot = Quat16.From(0, 0, 0, 1) };
    }
    // Player 2 = a real "heavyTxn" proposal through WorldHost.HandleProposal (gate, parsing, dispatch)
    static ProposalCode P2(WorldHost wh, HeavyTxnRequest t, uint id = 0)
    {
        uint rev; int sr; float sh;
        return wh.HandleProposal("cli", 2, id != 0 ? id : ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = t.Encode() }, 1, out rev, out sr, out sh);
    }
    // Player 1 = the host plugin in place (inside its original PickUpHeavy / settle)
    static ProposalCode P1(WorldHost wh, HeavyTxnRequest t, out ushort lease) { string why; return wh.HeavyTransaction(1, t, 1, out lease, out why); }
    static ProposalCode P1(WorldHost wh, HeavyTxnRequest t) { ushort l; return P1(wh, t, out l); }
    static HeavyRecord Rec(WorldHost wh, uint hash, int ns = Madison)
    {
        var e = wh.Reg.Get(StateScope.Region, ns, WorldKeys.Heavy);
        List<HeavyRecord> l; Expect(e != null && HeavyList.TryParse(e.Value, out l), "registry list of the region readable");
        HeavyList.TryParse(e.Value, out l);
        foreach (var r in l) if (r.Hash == hash) return r;
        throw new Exception("no record " + hash.ToString("X8"));
    }
    static bool HasRec(WorldHost wh, uint hash, int ns = Madison)
    {
        var e = wh.Reg.Get(StateScope.Region, ns, WorldKeys.Heavy);
        List<HeavyRecord> l; if (e == null || !HeavyList.TryParse(e.Value, out l)) return false;
        foreach (var r in l) if (r.Hash == hash) return true;
        return false;
    }
    static ushort Lease(WorldHost wh, uint hash) { return Rec(wh, hash).Lease; }

    // ------------------------------------------------------------------------------------------------ 01
    static void Codec()
    {
        var l = new List<HeavyRecord>();
        for (int i = 0; i < HeavyList.MaxPerRegion; i++)
        {
            var hr = new HeavyRecord { Hash = (uint)(i + 1), State = (HeavyState)(1 + i % 3), Actor = (byte)(i % 3 == 2 ? 0 : 1 + i % 2), Lease = (ushort)(i + 7), HasPose = i % 2 == 0, Pos = new Vec3(i, -i, 0.5f), Rot = Quat16.From(0, 1, 0, 1) };
            hr.Moving = hr.State == HeavyState.Placed && hr.HasPose && hr.Actor != 0 && i % 4 == 0;
            l.Add(hr);
        }
        var v = HeavyList.Encode(l);
        Expect(v.Length == 2 + 32 * HeavyRecord.WireSize && v.Length <= StateEntry.MaxValueBytes, "32 records fit one registry value (" + v.Length + " B)");
        List<HeavyRecord> back; Expect(HeavyList.TryParse(v, out back) && back.Count == 32, "round trip");
        for (int i = 0; i < 32; i++) Expect(back[i].Hash == l[i].Hash && back[i].State == l[i].State && back[i].Actor == l[i].Actor && back[i].Lease == l[i].Lease && back[i].HasPose == l[i].HasPose && back[i].Moving == l[i].Moving && back[i].Pos.X == l[i].Pos.X && back[i].Rot.Y == l[i].Rot.Y, "record " + i);
        l.Add(l[0]); Expect(!HeavyList.TryParse(HeavyList.Encode(l), out back), "33 records refused");
        var bad = (byte[])v.Clone(); bad[0] = 9; Expect(!HeavyList.TryParse(bad, out back), "version");
        bad = (byte[])v.Clone(); bad[2 + 4] = 7; Expect(!HeavyList.TryParse(bad, out back), "invalid state");
        Expect(!HeavyList.TryParse(new byte[] { 1, 1, 0 }, out back), "truncated");
        var t = Req(HeavyOp.Drop, TV, 42, 3.25f); var e = t.Encode();
        HeavyTxnRequest t2; Expect(e.Length == HeavyTxnRequest.WireSize && HeavyTxnRequest.TryParse(e, out t2) && t2.Op == HeavyOp.Drop && t2.Ns == Madison && t2.Hash == TV && t2.Lease == 42 && t2.Pos.X == 3.25f && t2.Digest == Digest, "request round trip");
        var nan = Req(HeavyOp.Drop, TV, 1, float.NaN).Encode(); Expect(!HeavyTxnRequest.TryParse(nan, out t2), "non-finite pose refused");
        e[0] = 0; Expect(!HeavyTxnRequest.TryParse(e, out t2), "op 0 refused");
        e[0] = 5; Expect(HeavyTxnRequest.TryParse(e, out t2) && t2.Op == HeavyOp.Settle, "op 5 = H1.1 Settle");
        e[0] = 6; Expect(HeavyTxnRequest.TryParse(e, out t2) && t2.Op == HeavyOp.Load, "op 6 = H2 Load");
        e[0] = 7; Expect(HeavyTxnRequest.TryParse(e, out t2) && t2.Op == HeavyOp.Sell, "op 7 = H3 Sell");
        e[0] = 8; Expect(HeavyTxnRequest.TryParse(e, out t2) && t2.Op == HeavyOp.Unsell, "op 8 = H3 Unsell");
        e[0] = 9; Expect(HeavyTxnRequest.TryParse(e, out t2) && t2.Op == HeavyOp.Unpack, "op 9 = W6B.2 Unpack");
        e[0] = 10; Expect(!HeavyTxnRequest.TryParse(e, out t2), "op 10 refused");
        var wh = Host(Madison, Madison);
        uint rev; int sr; float sh;
        Expect(wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = new byte[] { 1, 2 } }, 1, out rev, out sr, out sh) == ProposalCode.Invalid, "short proposal Invalid");
        Expect(wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 3, Key = WorldKeys.HeavyTxn, Value = Req(HeavyOp.Lease, TV, 0).Encode() }, 1, out rev, out sr, out sh) == ProposalCode.Invalid, "wrong scope arg Invalid");
        Expect(!HasRec(wh, TV), "nothing committed by invalid proposals");
    }

    // ------------------------------------------------------------------------------------------------ 02
    static void Ownership()
    {
        var wh = Host(Madison, Madison);
        ushort l1; Expect(P1(wh, Req(HeavyOp.Lease, TV, 0), out l1) == ProposalCode.Accepted && l1 != 0, "P1 lease granted in place");
        var r = Rec(wh, TV); Expect(r.State == HeavyState.Carried && r.Actor == 1 && r.Lease == l1 && !r.HasPose, "carried by P1, scene pose kept as fallback");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "P2 refused while P1 carries");
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l1)) == ProposalCode.Stale, "P2 cannot commit P1's drop even with P1's lease id");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Unchanged, "P1 repeat = Unchanged (no second lease)");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "P2 still refused before the rest pose is committed (P1's physics settles it)");
        Expect(P1(wh, Req(HeavyOp.Drop, TV, l1, 5f)) == ProposalCode.Accepted, "P1 release pose (H1.1: committed at the original drop)");
        r = Rec(wh, TV); Expect(r.State == HeavyState.Placed && r.Actor == 1 && r.HasPose && r.Moving && r.Pos.X == 5f, "placed + moving, simulated by P1");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 leases the placed object");
        ushort l2 = Lease(wh, TV); r = Rec(wh, TV);
        Expect(l2 != l1 && r.CarriedBy(2) && r.HasPose && !r.Moving && r.Pos.X == 5f, "new lease id, previous pose kept, no longer moving");
        Expect(P1(wh, Req(HeavyOp.Drop, TV, l1, 6f)) == ProposalCode.Stale, "P1's late re-settle of its old lease refused");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "P1 cannot take it while P2 carries");
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l2, 9f)) == ProposalCode.Accepted && Rec(wh, TV).Actor == 2 && Rec(wh, TV).Pos.X == 9f, "P2 drop committed: placed, simulated by P2");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted && Rec(wh, TV).CarriedBy(1), "P1 picks it up again");
        Expect(wh.Heavy.Leases == 3 && wh.Heavy.Total == 1, "one record, three leases");
    }

    // ------------------------------------------------------------------------------------------------ 03
    static void Simultaneous()
    {
        // (a) the host's click is decided inside its original call first; Player 2's proposal arrives afterwards
        var wh = Host(Madison, Madison);
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P1 first");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "P2 second refused");
        Expect(Rec(wh, TV).CarriedBy(1), "exactly one holder (P1)");
        // (b) Player 2's proposal is applied first (host LateUpdate queue); the host's click in the next frame is refused
        wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 first");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "P1 second refused");
        Expect(Rec(wh, TV).CarriedBy(2) && wh.Heavy.Leases == 1, "exactly one holder (P2), one lease");
        // (c) retransmission of the same proposal id: cached answer, never applied twice; a NEW id is Unchanged
        wh = Host(Madison, Madison);
        uint id = ++_pid;
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0), id) == ProposalCode.Accepted, "first");
        ushort l = Lease(wh, TV);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0), id) == ProposalCode.Accepted && Lease(wh, TV) == l, "retransmission: cached, same lease");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Unchanged && Lease(wh, TV) == l && wh.Heavy.Leases == 1, "duplicate request: Unchanged, same lease");
        // (d) two different objects at once are independent
        Expect(P1(wh, Req(HeavyOp.Lease, Printer, 0)) == ProposalCode.Accepted && Rec(wh, Printer).CarriedBy(1) && Rec(wh, TV).CarriedBy(2), "independent objects");
    }

    // ------------------------------------------------------------------------------------------------ 04
    static void DropRelocation()
    {
        var wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "lease");
        ushort l = Lease(wh, TV);
        Expect(P2(wh, Req(HeavyOp.Drop, TV, (ushort)(l + 1), 1f)) == ProposalCode.Stale, "wrong lease id");
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l, 1f)) == ProposalCode.Accepted && Rec(wh, TV).Pos.X == 1f, "first rest pose");
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l, 1f)) == ProposalCode.Unchanged, "identical pose Unchanged (nothing re-sent to observers)");
        uint before = wh.Reg.HeadRev;
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l, 1.6f)) == ProposalCode.Accepted && Rec(wh, TV).Pos.X == 1.6f && wh.Reg.HeadRev != before, "later movement re-committed");
        string why;
        Expect(wh.Heavy.Drop(1, Madison, Madison, TV, l, new Vec3(2, 1, 2), Quat16.From(0, 0, 0, 1), wh.Reg, wh.Outbox, out why) == ProposalCode.Stale, "another slot with the same lease id refused");
        Expect(P2(wh, Req(HeavyOp.Settle, TV, l, 1.6f)) == ProposalCode.Accepted && !Rec(wh, TV).Moving, "rest pose (Settle)");
        wh.SetPlayerRegion(2, Hillville, 2);
        Expect(Rec(wh, TV).State == HeavyState.Placed && Rec(wh, TV).Actor == 0 && Rec(wh, TV).Pos.X == 1.6f, "dropper left: stays at its rest pose, simulated by nobody");
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l, 3f)) == ProposalCode.NotAuthorized, "report from another region refused");
        Expect(Rec(wh, TV).Pos.X == 1.6f, "pose unchanged");
    }

    // ------------------------------------------------------------------------------------------------ 05
    static void ReleaseRules()
    {
        var wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "lease");
        ushort l = Lease(wh, TV);
        Expect(P2(wh, Req(HeavyOp.Release, TV, (ushort)(l + 3))) == ProposalCode.Stale && HasRec(wh, TV), "stale release refused");
        Expect(P2(wh, Req(HeavyOp.Release, TV, l)) == ProposalCode.Accepted && !HasRec(wh, TV), "never placed: record removed = original scene pose");
        Expect(P2(wh, Req(HeavyOp.Release, TV, l)) == ProposalCode.Unchanged, "release again: nothing to do");
        // placed earlier -> carry ends without a drop -> back to that rest pose
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P1 lease");
        ushort l1 = Lease(wh, TV);
        Expect(P1(wh, Req(HeavyOp.Drop, TV, l1, 4f)) == ProposalCode.Accepted, "P1 placed at 4");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 lease");
        ushort l2 = Lease(wh, TV);
        Expect(P2(wh, Req(HeavyOp.Release, TV, l2)) == ProposalCode.Accepted, "P2 release (grant unusable / object gone)");
        var r = Rec(wh, TV); Expect(r.State == HeavyState.Placed && r.Actor == 0 && r.Pos.X == 4f, "previous rest pose, nobody simulates");
        Expect(P1(wh, Req(HeavyOp.Release, TV, l1)) == ProposalCode.Stale, "P1's old lease cannot release it");
    }

    // ------------------------------------------------------------------------------------------------ 06
    static void Disconnect()
    {
        var wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 carries TV");
        ushort lt = Lease(wh, TV);
        Expect(P2(wh, Req(HeavyOp.Lease, Printer, 0)) == ProposalCode.Accepted, "P2 carries printer (test: second record)");
        ushort lp = Lease(wh, Printer);
        Expect(P2(wh, Req(HeavyOp.Drop, Printer, lp, 7f)) == ProposalCode.Accepted && P2(wh, Req(HeavyOp.Settle, Printer, lp, 7f)) == ProposalCode.Accepted, "printer dropped and at rest, simulated by P2");
        wh.PlayerDisconnected(2, 3);
        Expect(!HasRec(wh, TV), "carried TV (never placed) back at its scene pose - no copy lost in the void");
        var p = Rec(wh, Printer); Expect(p.State == HeavyState.Placed && p.Actor == 0 && p.Pos.X == 7f, "placed printer stays, simulated by nobody");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P1 can pick the TV up at once");
        // reconnect: stale requests of the old session's lease are refused
        wh.SetPlayerRegion(2, Madison, 4);
        Expect(P2(wh, Req(HeavyOp.Drop, Printer, lp, 8f)) == ProposalCode.Stale && Rec(wh, Printer).Pos.X == 7f, "old lease cannot move the printer after the reconnect");
        Expect(P2(wh, Req(HeavyOp.Release, TV, lt)) == ProposalCode.Stale && Rec(wh, TV).CarriedBy(1), "old lease cannot release P1's new carry");
        // host-side carry is never touched by a client disconnect
        wh.PlayerDisconnected(2, 5);
        Expect(Rec(wh, TV).CarriedBy(1), "P1's carry survives Player 2's disconnect");
        Expect(wh.Heavy.SlotReleases >= 2, "slot releases counted");
    }

    // ------------------------------------------------------------------------------------------------ 07
    static void Regions_()
    {
        var wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 lease");
        ushort l = Lease(wh, TV);
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l, 2f)) == ProposalCode.Accepted && P2(wh, Req(HeavyOp.Settle, TV, l, 2f)) == ProposalCode.Accepted, "placed, at rest");
        Expect(P1(wh, Req(HeavyOp.Lease, Printer, 0)) == ProposalCode.Accepted, "P1 carries printer");
        wh.SetPlayerRegion(1, Hillville, 2);
        Expect(!HasRec(wh, Printer), "P1 left while carrying (scene unloaded): printer back at the scene pose");
        Expect(Rec(wh, TV).Actor == 2, "P2's placed TV unaffected by P1 leaving");
        wh.SetPlayerRegion(2, Hillville, 3);
        var e = wh.Reg.Get(StateScope.Region, Madison, WorldKeys.Heavy);
        List<HeavyRecord> lst; Expect(e != null && HeavyList.TryParse(e.Value, out lst) && lst.Count == 0, "Madison emptied: every record cleared (original reload restores the scene poses)");
        Expect(wh.Heavy.Cleared == 1, "cleared counted");
        // lease outside the requester's region
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.NotAuthorized, "Madison object requested from Hillville refused");
        // supported objects only
        Expect(HeavyRules.Supported(Madison, LootFlags.Heavy) && !HeavyRules.Supported(IdentityNamespace.MainScene, LootFlags.Heavy), "MainScene heavy objects never leased (reloads with every region change)");
        Expect(!HeavyRules.Supported(Madison, LootFlags.Heavy | LootFlags.Ambiguous) && !HeavyRules.Supported(Madison, LootFlags.None) && !HeavyRules.Supported(Regions.None, LootFlags.Heavy), "ambiguous / non-heavy / no region refused");
        string why; ushort l2;
        Expect(wh.Heavy.Lease(1, Madison, Madison, TV, false, wh.Reg, wh.Outbox, out l2, out why) == ProposalCode.NotAuthorized, "unknown object never leased");
    }

    // ------------------------------------------------------------------------------------------------ 08
    static void HostValidation()
    {
        var wh = Host(Hillville, Hillville);
        var hv = Req(HeavyOp.Lease, TV, 0); hv.Ns = Hillville;
        Expect(P2(wh, hv) == ProposalCode.Busy, "host catalog still building -> Busy (retry, never permanent)");
        wh = Host(Madison, Madison);
        var bad = Req(HeavyOp.Lease, TV, 0); bad.Digest = 1;
        Expect(P2(wh, bad) == ProposalCode.NotAuthorized, "census digest mismatch refused");
        Expect(P2(wh, Req(HeavyOp.Lease, Unknown, 0)) == ProposalCode.NotAuthorized, "object not in the host catalog refused");
        Expect(!HasRec(wh, TV) && !HasRec(wh, Unknown), "nothing committed");
        var off = Host(Madison, Madison); off.HeavyCatalog = null;
        Expect(P2(off, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.NotAuthorized, "no host heavy sync -> refused");
        // Player 2 can never Remove (deposits are the host's own original only)
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 lease");
        Expect(P2(wh, Req(HeavyOp.Remove, TV, Lease(wh, TV))) == ProposalCode.NotAuthorized && Rec(wh, TV).CarriedBy(2), "client Remove refused");
        // host deposit: Removed (hidden for Player 2), only the host's original can take it out again
        var h2 = Host(Madison, Madison);
        ushort l1; Expect(P1(h2, Req(HeavyOp.Lease, TV, 0), out l1) == ProposalCode.Accepted, "P1 lease");
        Expect(P1(h2, Req(HeavyOp.Remove, TV, l1)) == ProposalCode.Accepted && Rec(h2, TV).State == HeavyState.Removed, "P1 original deposit -> Removed");
        Expect(P2(h2, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "P2 cannot take it out of host-local storage");
        Expect(P1(h2, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted && Rec(h2, TV).CarriedBy(1) && !Rec(h2, TV).HasPose, "P1's original takes it out again: tracked again");
        // capacity
        var cap = new HeavyBook(); var reg = StateRegistry.CreateHost(1); var ob = new List<StateEntry>(); string why; ushort lx;
        for (uint i = 1; i <= HeavyList.MaxPerRegion; i++) Expect(cap.Lease(1, Madison, Madison, i, true, reg, ob, out lx, out why) == ProposalCode.Accepted, "lease " + i);
        Expect(cap.Lease(2, Madison, Madison, 999, true, reg, ob, out lx, out why) == ProposalCode.Busy, "33rd object of a region: Busy (nothing happens, no duplicate)");
        Expect(reg.Get(StateScope.Region, Madison, WorldKeys.Heavy).Value.Length <= StateEntry.MaxValueBytes, "full list fits the registry value limit");
        Expect(cap.Release(1, Madison, 1, cap.InRegion(Madison)[0].Lease, reg, ob, out why) == ProposalCode.Accepted && cap.Lease(2, Madison, Madison, 999, true, reg, ob, out lx, out why) == ProposalCode.Accepted, "room again after a release to the scene pose");
    }

    // ------------------------------------------------------------------------------------------------ 09
    static void PresentAndGrant()
    {
        var none = default(HeavyRecord);
        Expect(HeavyRules.Present(false, none, 2, false) == HeavyRules.Show.ScenePose, "no record: original scene pose");
        var c1 = new HeavyRecord { State = HeavyState.Carried, Actor = 1, HasPose = true };
        Expect(HeavyRules.Present(true, c1, 2, false) == HeavyRules.Show.Hidden && HeavyRules.Present(true, c1, 1, true) == HeavyRules.Show.Own, "carried: hidden for the observer (avatar shows it), own for the holder");
        var p1 = new HeavyRecord { State = HeavyState.Placed, Actor = 1, HasPose = true };
        Expect(HeavyRules.Present(true, p1, 2, false) == HeavyRules.Show.AtPose, "placed: observer shows the committed pose (kinematic)");
        Expect(HeavyRules.Present(true, p1, 1, true) == HeavyRules.Show.Own, "placed: the dropper's own physics");
        Expect(HeavyRules.Present(true, p1, 1, false) == HeavyRules.Show.AtPose, "placed by me but not simulated here (scene reloaded): committed pose, no second simulation");
        var p0 = new HeavyRecord { State = HeavyState.Placed, Actor = 0, HasPose = true };
        Expect(HeavyRules.Present(true, p0, 1, false) == HeavyRules.Show.AtPose && HeavyRules.Present(true, p0, 2, false) == HeavyRules.Show.AtPose, "placed, nobody simulates: both at rest");
        Expect(HeavyRules.Present(true, new HeavyRecord { State = HeavyState.Removed }, 2, false) == HeavyRules.Show.Hidden, "host-local storage: hidden for Player 2");
        Expect(HeavyRules.GrantBlock(true, false, false, true, false, 1.2f) == null, "normal grant runs the original");
        Expect(HeavyRules.GrantBlock(false, false, false, true, false, 1f) != null, "object gone");
        Expect(HeavyRules.GrantBlock(true, true, false, true, false, 1f) != null, "already carrying");
        Expect(HeavyRules.GrantBlock(true, false, true, true, false, 1f) != null, "entered a car meanwhile");
        Expect(HeavyRules.GrantBlock(true, false, false, false, false, 1f) != null, "controls off (menu / minigame)");
        Expect(HeavyRules.GrantBlock(true, false, false, true, true, 1f) != null, "paused");
        Expect(HeavyRules.GrantBlock(true, false, false, true, false, HeavyRules.MaxGrantDistance + 0.1f) != null, "walked away");
    }

    // ------------------------------------------------------------------------------------------------ 10
    static string Src(params string[] p)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var f = Path.Combine(dir, "src", Path.Combine(p));
            if (File.Exists(f)) return File.ReadAllText(f);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new Exception("source not found: " + string.Join("/", p));
    }
    static int Count(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }
    static void SourceScan()
    {
        var h = Src("Plugin", "HeavySync.cs");
        Expect(Count(h, "_pickUpHeavy(") == 1, "the original PickUpHeavy is invoked at exactly one place");
        int grant = h.IndexOf("void OnLeaseResult(", StringComparison.Ordinal), call = h.IndexOf("_pickUpHeavy(q.Pick, q.Dist)", StringComparison.Ordinal);
        Expect(grant > 0 && call > grant && h.IndexOf("HeavyRules.GrantBlock(", grant, StringComparison.Ordinal) < call && h.IndexOf("_resume = q.Pick;", grant, StringComparison.Ordinal) < call, "only after the host grant + precondition re-check, with the resume guard");
        Expect(h.Contains("if (ReferenceEquals(_resume, pick)) { _resume = null; return true; }"), "the resumed call passes the prefix once");
        int allow = h.IndexOf("public bool AllowPickUpHeavy(", StringComparison.Ordinal);
        int clientPart = h.IndexOf("if (!Live)", allow, StringComparison.Ordinal);
        Expect(clientPart > 0 && h.IndexOf("Key = WorldKeys.HeavyTxn", clientPart, StringComparison.Ordinal) > 0, "client path sends a heavyTxn proposal");
        var clientTail = h.Substring(clientPart, h.IndexOf("void CaptureBeforePickup(", StringComparison.Ordinal) - clientPart);
        Expect(!clientTail.Contains("return true;"), "Player 2's original never runs on the click itself");
        Expect(h.Contains("HeavyTransaction(1, new HeavyTxnRequest { Op = HeavyOp.Lease"), "host decides its own pickup in place");
        Expect(h.Contains("\"DropPostfix\"") && h.Contains("\"TrunkPostfix\"") && h.Contains("\"StopPrefix\"") && h.Contains("\"DepositPrefix\"") && h.Contains("\"AssociatePrefix\""), "drop / deposit / vehicle / associate hooks present");
        Expect(h.Contains("SettleRules.OwnerShouldReport("), "rest rule shared with W3 dropped items");
        Expect(!h.Contains("Instantiate(") && !h.Contains("AddComponent"), "no replacement objects created (the scene object itself is presented)");
        Expect(!h.Contains("GUI.") && !h.Contains("OnGUI") && !h.Contains("SpawnWarning") && !h.Contains("SpawnMessage"), "no mod text in the game UI");
        Expect(h.Contains("HeavyRules.Supported(ns, e.Flags)") && h.Contains("if (ns != _world.LocalRegion) return null;"), "census heavy objects of the current region only");
        Expect(h.Contains("FindObjectOfType") == false && h.Contains("GameObject.Find") == false, "no scene-wide searches");
        var w = Src("Plugin", "WorldLootSync.cs");
        Expect(w.Contains("static bool HeavyPrefix(object __instance, float dist)") && w.Contains("if (hs != null) return hs.AllowPickUpHeavy(c, dist);") && w.Contains("return i._host || i.RefuseP2Heavy(c);"), "PickUpHeavy hook: lease decides; without HeavySync the v0.16.1 client refusal stays");
        var g = Src("Plugin", "RemoteGear.cs");
        Expect(g.Contains("carry = hs.CarryShown(carry, out scene);") && g.Contains("var prefab = scene ?? ItemModel(ps, itemId);"), "remote carry shows the leased scene object from the lease record (prefab fallback)");
        var p = Src("Plugin", "Plugin.cs");
        Expect(p.Contains("new HeavySync(node, log, _world)") && p.Contains("Release(ref _heavy, \"HEAVY\"); Release(ref _loot, \"WLOOT\")"), "built after / disposed before WorldLootSync");
        var t = Src("Shared", "WorldTime.cs");
        Expect(t.Contains("Heavy.ReleaseSlot(slot, RegionOfSlot((byte)(slot == 1 ? 2 : 1)), Reg, Outbox);") && t.Contains("Heavy.ClearRegion(prev, Reg, Outbox);"), "region change / empty region wired in SetPlayerRegion (other slot's region for the H1.1 handover)");
        Expect(t.Contains("t.Op == HeavyOp.Remove) { code = ProposalCode.NotAuthorized;"), "proposal Remove refused");
    }

    // ------------------------------------------------------------------------------------------------ 11
    static HeavyTxnRequest At(HeavyOp op, uint hash, ushort lease, float x, float y)
    {
        var t = Req(op, hash, lease, x); t.Pos = new Vec3(x, y, 2); return t;
    }
    static void ImmediateDrop()
    {
        var wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 carries the TV");
        ushort l = Lease(wh, TV);
        uint before = wh.Reg.HeadRev; wh.Outbox.Clear();
        // the release pose (carry pose in front of the player, in the air) is committed by the original drop itself
        Expect(P2(wh, At(HeavyOp.Drop, TV, l, 1f, 1.4f)) == ProposalCode.Accepted, "Drop at the release pose accepted at once (no rest wait)");
        var r = Rec(wh, TV);
        Expect(r.State == HeavyState.Placed && r.Actor == 2 && r.Lease == l && r.Moving && r.HasPose && r.Pos.Y == 1.4f, "placed + moving, simulated by the dropper, at the exact release pose");
        Expect(wh.Reg.HeadRev != before && wh.Outbox.Count == 1, "one registry delta for observers in the same host frame (the reveal needs nothing else)");
        Expect(HeavyRules.Present(true, r, 1, false) == HeavyRules.Show.AtPose && HeavyRules.Present(true, r, 2, true) == HeavyRules.Show.Own, "observer: visible at once; dropper: its original physics");
        Expect(P2(wh, At(HeavyOp.Drop, TV, l, 1f, 1.4f)) == ProposalCode.Unchanged, "duplicate Drop: Unchanged (no extra delta)");
        Expect(P2(wh, At(HeavyOp.Settle, TV, l, 1.1f, 0.31f)) == ProposalCode.Accepted, "exact rest pose");
        r = Rec(wh, TV); Expect(r.State == HeavyState.Placed && !r.Moving && r.Actor == 2 && r.Pos.Y == 0.31f && r.Pos.X == 1.1f, "settled at the exact pose, still simulated by the dropper");
        Expect(P2(wh, At(HeavyOp.Settle, TV, l, 1.1f, 0.31f)) == ProposalCode.Unchanged, "duplicate Settle: Unchanged");
        // a later real movement (pushed): new Drop -> Moving -> Settle
        Expect(P2(wh, At(HeavyOp.Drop, TV, l, 1.3f, 0.31f)) == ProposalCode.Accepted && Rec(wh, TV).Moving, "moving again");
        Expect(P2(wh, At(HeavyOp.Settle, TV, l, 1.9f, 0.31f)) == ProposalCode.Accepted && !Rec(wh, TV).Moving && Rec(wh, TV).Pos.X == 1.9f, "rest again");
        // a carry that ends without DropHeavy can settle directly from Carried
        Expect(P1(wh, Req(HeavyOp.Lease, Printer, 0)) == ProposalCode.Accepted, "P1 carries the printer");
        ushort lp = Lease(wh, Printer);
        Expect(P1(wh, At(HeavyOp.Settle, Printer, lp, 4f, 0f)) == ProposalCode.Accepted && !Rec(wh, Printer).Moving && Rec(wh, Printer).State == HeavyState.Placed, "Settle from Carried");
        Expect(wh.Heavy.Drops == 2 && wh.Heavy.Settles == 3, "counters: 2 drops / 3 settles");
        // immediate re-pickup while it is still moving: allowed (the object is Placed); the new lease wins
        Expect(P2(wh, At(HeavyOp.Drop, TV, l, 2f, 1f)) == ProposalCode.Accepted, "moving again");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted && Rec(wh, TV).CarriedBy(1) && !Rec(wh, TV).Moving, "picked up again while moving: carried, Moving cleared");
    }

    // ------------------------------------------------------------------------------------------------ 12
    static void StaleOrder()
    {
        var wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 lease");
        ushort l2 = Lease(wh, TV);
        Expect(P2(wh, At(HeavyOp.Drop, TV, l2, 1f, 1.5f)) == ProposalCode.Accepted, "P2 drop (moving)");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P1 picks it up while it still falls on P2's PC");
        ushort l1 = Lease(wh, TV);
        Expect(P2(wh, At(HeavyOp.Settle, TV, l2, 1f, 0f)) == ProposalCode.Stale && Rec(wh, TV).CarriedBy(1), "P2's late Settle (old lease) never overwrites the newer carry");
        Expect(P2(wh, At(HeavyOp.Drop, TV, l2, 1f, 0f)) == ProposalCode.Stale && Rec(wh, TV).CarriedBy(1), "P2's late Drop refused too");
        Expect(P2(wh, Req(HeavyOp.Release, TV, l2)) == ProposalCode.Stale && Rec(wh, TV).CarriedBy(1), "P2's late Release refused");
        // host-local storage
        Expect(P1(wh, Req(HeavyOp.Remove, TV, l1)) == ProposalCode.Accepted && Rec(wh, TV).State == HeavyState.Removed && !Rec(wh, TV).Moving, "P1 deposit: Removed");
        Expect(P1(wh, At(HeavyOp.Settle, TV, l1, 3f, 0f)) == ProposalCode.Stale && Rec(wh, TV).State == HeavyState.Removed, "a late Settle never brings a stored object back");
        // region reset
        var w2 = Host(Madison, Madison);
        Expect(P2(w2, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "lease");
        ushort lr = Lease(w2, TV);
        Expect(P2(w2, At(HeavyOp.Drop, TV, lr, 1f, 1f)) == ProposalCode.Accepted, "drop");
        w2.SetPlayerRegion(1, Hillville, 2); w2.SetPlayerRegion(2, Hillville, 3);
        Expect(!HasRec(w2, TV), "region emptied: record gone (original reload)");
        w2.SetPlayerRegion(2, Madison, 4);
        Expect(P2(w2, At(HeavyOp.Settle, TV, lr, 1f, 0f)) == ProposalCode.Stale && !HasRec(w2, TV), "a Settle of the old region occupancy is refused, nothing re-created");
        // observer side: a snapshot of the old lease after the new pickup
        var f = new HeavyMotionFilter();
        var carried = new HeavyRecord { Hash = TV, State = HeavyState.Carried, Actor = 1, Lease = l1 };
        Expect(f.Check(Mo(TV, l2, 50), true, carried, 2, Madison) == HeavyMotionVerdict.NotHolder, "old holder's snapshot after the new pickup rejected");
    }

    // ------------------------------------------------------------------------------------------------ 13
    static HeavyMotion Mo(uint hash, ushort lease, uint seq, float y = 1f, bool final = false)
    {
        return new HeavyMotion { Ns = Madison, Hash = hash, Lease = lease, Seq = seq, TimeMs = seq * 50, Pos = new Vec3(1, y, 2), Rot = Quat16.From(0, 0.2f, 0, 1), Final = final };
    }
    static bool RoundTrips(HeavyMotion x)
    {
        var w = new LiteNetLib.Utils.NetDataWriter(); x.Write(w);
        var r = new LiteNetLib.Utils.NetDataReader(w.CopyData()); r.GetByte();
        HeavyMotion o; return HeavyMotion.TryRead(r, out o);
    }
    static void MotionFilter()
    {
        var w = new LiteNetLib.Utils.NetDataWriter(); var m = Mo(TV, 7, 42, 1.25f, true); m.Write(w);
        Expect(w.Length == HeavyMotion.WireSize && w.Data[0] == (byte)MsgType.HeavyMotion, "40 B incl. type");
        var rd = new LiteNetLib.Utils.NetDataReader(w.CopyData()); rd.GetByte();
        HeavyMotion b; Expect(HeavyMotion.TryRead(rd, out b) && b.Ns == Madison && b.Hash == TV && b.Lease == 7 && b.Seq == 42 && b.TimeMs == 2100 && b.Pos.Y == 1.25f && b.Final && Math.Abs(b.RotQ.Y - 0.196f) < 0.01f, "round trip");
        var bad = m; bad.Pos = new Vec3(float.NaN, 0, 0); Expect(!RoundTrips(bad), "non-finite refused");
        bad = m; bad.Pos = new Vec3(1e6f, 0, 0); Expect(!RoundTrips(bad), "out-of-world refused");
        bad = m; bad.Lease = 0; Expect(!RoundTrips(bad), "lease 0 refused");
        bad = m; bad.Rot = new Quat16(); Expect(!RoundTrips(bad), "zero quaternion refused");
        Expect(!HeavyMotion.TryRead(new LiteNetLib.Utils.NetDataReader(new byte[] { 1, 2, 3 }), out b), "wrong length refused");
        var flag = w.CopyData(); flag[flag.Length - 1] = 2; var fr = new LiteNetLib.Utils.NetDataReader(flag); fr.GetByte(); Expect(!HeavyMotion.TryRead(fr, out b), "unknown flag refused");
        var f = new HeavyMotionFilter();
        var carried = new HeavyRecord { Hash = TV, State = HeavyState.Carried, Actor = 2, Lease = 7 };
        var moving = new HeavyRecord { Hash = TV, State = HeavyState.Placed, Actor = 2, Lease = 7, HasPose = true, Moving = true };
        var settled = moving; settled.Moving = false;
        Expect(f.Check(Mo(TV, 7, 1), true, carried, 2, Madison) == HeavyMotionVerdict.Buffer, "snapshot overtook the reliable Drop commit: buffered, not shown");
        Expect(f.Check(Mo(TV, 7, 2), true, moving, 2, Madison) == HeavyMotionVerdict.Follow, "moving record: followed");
        Expect(f.Check(Mo(TV, 7, 2), true, moving, 2, Madison) == HeavyMotionVerdict.Stale, "duplicate packet stale");
        Expect(f.Check(Mo(TV, 7, 4), true, moving, 2, Madison) == HeavyMotionVerdict.Follow, "seq 4");
        Expect(f.Check(Mo(TV, 7, 3), true, moving, 2, Madison) == HeavyMotionVerdict.Stale, "reordered older packet stale");
        Expect(f.Check(Mo(TV, 7, 5), true, settled, 2, Madison) == HeavyMotionVerdict.NotMoving, "after the Settle: a late snapshot never moves it again");
        Expect(f.Check(Mo(TV, 6, 9), true, moving, 2, Madison) == HeavyMotionVerdict.NotHolder, "older lease rejected");
        Expect(f.Check(Mo(TV, 7, 9), true, moving, 1, Madison) == HeavyMotionVerdict.NotHolder, "sender is not the simulating slot");
        var mh = Mo(TV, 7, 10); mh.Ns = Hillville;
        Expect(f.Check(mh, true, moving, 2, Madison) == HeavyMotionVerdict.WrongRegion && f.Check(Mo(TV, 7, 10), true, moving, 2, Regions.None) == HeavyMotionVerdict.WrongRegion, "other region / no region rejected");
        Expect(f.Check(Mo(TV, 7, 10), false, default(HeavyRecord), 2, Madison) == HeavyMotionVerdict.NoRecord, "no record (reset / never leased) rejected");
        Expect(f.Check(Mo(Printer, 7, 10), true, moving, 2, Madison) == HeavyMotionVerdict.NoRecord, "record of another object rejected");
        var removed = new HeavyRecord { Hash = TV, State = HeavyState.Removed, Actor = 2, Lease = 7 };
        Expect(f.Check(Mo(TV, 7, 11), true, removed, 2, Madison) == HeavyMotionVerdict.NotHolder, "stored object rejected");
        var newLease = moving; newLease.Lease = 8;
        Expect(f.Check(Mo(TV, 8, 3), true, newLease, 2, Madison) == HeavyMotionVerdict.Follow, "a new lease starts its own sequence");
        Expect(f.Stale == 2 && f.Rejected >= 7, "counted");
        // record invariant: Moving only on Placed + pose + simulator
        var id = Quat16.From(0, 0, 0, 1);
        var lst = new List<HeavyRecord> { new HeavyRecord { Hash = 1, State = HeavyState.Carried, Actor = 1, Lease = 1, Moving = true, Rot = id } };
        List<HeavyRecord> back; Expect(!HeavyList.TryParse(HeavyList.Encode(lst), out back), "Moving on a carried record refused");
        lst[0] = new HeavyRecord { Hash = 1, State = HeavyState.Placed, Actor = 0, Lease = 1, HasPose = true, Moving = true, Rot = id };
        Expect(!HeavyList.TryParse(HeavyList.Encode(lst), out back), "Moving without a simulator refused");
        lst[0] = new HeavyRecord { Hash = 1, State = HeavyState.Placed, Actor = 2, Lease = 1, HasPose = true, Moving = true, Rot = id };
        var enc = HeavyList.Encode(lst);
        Expect(HeavyList.TryParse(enc, out back) && back[0].Moving, "valid moving record");
        enc[2 + 8] = 7; Expect(!HeavyList.TryParse(enc, out back), "unknown flag bits refused");
        enc = HeavyList.Encode(lst); enc[0] = 1; Expect(!HeavyList.TryParse(enc, out back), "H1 list version 1 refused (both PCs need the H1.1 build)");
    }

    // ------------------------------------------------------------------------------------------------ 14
    static void MotionBuffer()
    {
        // observer: the vehicle interpolation buffer (proven in M4/M5.0) through the additive H1.1 overload
        var buf = new VehiclePoseBuffer { InterpolationDelay = 0.1 };
        var rot = new Quat(0, 0, 0, 1);
        double recv = 10.0;
        buf.Add(0, new Vec3(1, 1.5f, 2), rot, recv);
        buf.Add(50, new Vec3(1, 1.2f, 2), rot, recv + 0.05);
        buf.Add(100, new Vec3(1, 0.8f, 2), rot, recv + 0.10);
        VehiclePose p;
        Expect(buf.Sample(recv + 0.1, out p) && Math.Abs(p.Pos.Y - 1.5f) < 1e-4f, "starts at the release pose");
        Expect(buf.Sample(recv + 0.125, out p) && Math.Abs(p.Pos.Y - 1.35f) < 1e-3f, "interpolated between snapshots");
        Expect(!buf.Drained(recv + 0.15) && buf.Drained(recv + 0.21), "drained once the newest snapshot is rendered");
        Expect(buf.Sample(recv + 0.3, out p) && p.Pos.Y == 0.8f, "holds the newest pose (no extrapolation / predicted landing)");
        buf.Add(100, new Vec3(9, 9, 9), rot, recv + 0.4);
        Expect(buf.Sample(recv + 0.5, out p) && p.Pos.Y == 0.8f, "equal sender time ignored");
        // holder: the vehicle send policy, configured like HeavySync.Sim
        var pol = new VehicleSendPolicy { RateHz = 20, MoveEpsilon = 0.01f, AngleEpsilon = 1f, HeartbeatSeconds = double.MaxValue };
        int sent = 0; double t = 0; float y = 2f;
        for (int frame = 0; frame < 60; frame++, t += 1.0 / 60) { y -= 0.05f; if (pol.ShouldSend(t, new Vec3(0, y, 0), rot, VehicleStateFlags.None, Madison)) sent++; }
        Expect(sent >= 18 && sent <= 21, "falling for 1 s at 60 fps: <= 20 snapshots (" + sent + ")");
        int idle = 0;
        for (int frame = 0; frame < 600; frame++, t += 1.0 / 60) if (pol.ShouldSend(t, new Vec3(0, y, 0), rot, VehicleStateFlags.None, Madison)) idle++;
        Expect(idle <= 1, "unchanged transform for 10 s: at most the catch-up of the last real movement, then nothing (no heartbeat) - " + idle);
        int creep = 0;
        for (int frame = 0; frame < 60; frame++, t += 1.0 / 60) { y -= 0.0001f; if (pol.ShouldSend(t, new Vec3(0, y, 0), rot, VehicleStateFlags.None, Madison)) creep++; }
        Expect(creep == 0, "sub-centimetre creep not sent");
        Expect(sent * HeavyMotion.WireSize <= 21 * 40, "<= ~840 B/s payload per moving object while it moves");
    }

    // ------------------------------------------------------------------------------------------------ 15
    static void Handover()
    {
        var wh = Host(Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 lease");
        ushort l2 = Lease(wh, TV);
        Expect(P2(wh, At(HeavyOp.Drop, TV, l2, 1f, 1.5f)) == ProposalCode.Accepted, "P2 drop");
        wh.PlayerDisconnected(2, 3);
        var r = Rec(wh, TV);
        Expect(r.MovingBy(1) && r.Lease != l2 && r.Pos.Y == 1.5f && wh.Heavy.Handovers == 1, "handed to P1 with a new lease (never frozen in the air)");
        Expect(P1(wh, At(HeavyOp.Settle, TV, r.Lease, 1f, 0.2f)) == ProposalCode.Accepted && !Rec(wh, TV).Moving && Rec(wh, TV).Actor == 1, "P1 commits the rest pose");
        wh.SetPlayerRegion(2, Madison, 4);
        Expect(P2(wh, At(HeavyOp.Settle, TV, l2, 5f, 0f)) == ProposalCode.Stale && Rec(wh, TV).Pos.Y == 0.2f, "P2's old lease after the reconnect: refused");
        Expect(P1(wh, Req(HeavyOp.Lease, Printer, 0)) == ProposalCode.Accepted, "P1 lease printer");
        ushort l1 = Lease(wh, Printer);
        Expect(P1(wh, At(HeavyOp.Drop, Printer, l1, 3f, 1.2f)) == ProposalCode.Accepted, "P1 drop");
        wh.SetPlayerRegion(1, Hillville, 5);
        Expect(Rec(wh, Printer).MovingBy(2) && Rec(wh, Printer).Lease != l1, "P1 travels while it falls: handed to P2");
        Expect(Rec(wh, TV).State == HeavyState.Placed && Rec(wh, TV).Actor == 0 && !Rec(wh, TV).Moving, "P1's resting TV: at rest, simulated by nobody");
        var w2 = Host(Madison, Hillville);
        Expect(P1(w2, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P1 lease (alone in Madison)");
        ushort la = Lease(w2, TV);
        Expect(P1(w2, At(HeavyOp.Drop, TV, la, 1f, 1.5f)) == ProposalCode.Accepted, "P1 drop");
        Expect(P1(w2, Req(HeavyOp.Release, TV, la)) == ProposalCode.Accepted && !HasRec(w2, TV), "moving object released with nobody else there: scene pose (original reload), never frozen mid-air");
        var w3 = Host(Madison, Madison);
        Expect(P2(w3, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 lease");
        ushort lb = Lease(w3, TV);
        Expect(P2(w3, At(HeavyOp.Drop, TV, lb, 1f, 1.5f)) == ProposalCode.Accepted, "P2 drop");
        Expect(P2(w3, Req(HeavyOp.Release, TV, lb)) == ProposalCode.Accepted && Rec(w3, TV).MovingBy(1), "P2 releases while moving (scene reloaded): P1 continues");
        var w4 = Host(Madison, Madison);
        Expect(P2(w4, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 carries");
        w4.PlayerDisconnected(2, 3);
        Expect(!HasRec(w4, TV) && w4.Heavy.Handovers == 0, "carried + disconnect: scene pose as before (no handover)");
    }

    // ------------------------------------------------------------------------------------------------ 16
    static void Cycles()
    {
        var wh = Host(Madison, Madison);
        var seen = new HashSet<ushort>();
        for (int i = 0; i < 6; i++)
        {
            bool p2 = i % 2 == 1;
            var lease = Req(HeavyOp.Lease, TV, 0);
            Expect((p2 ? P2(wh, lease) : P1(wh, lease)) == ProposalCode.Accepted, "cycle " + i + ": lease");
            ushort l = Lease(wh, TV); Expect(seen.Add(l), "cycle " + i + ": fresh lease id");
            Expect((p2 ? P1(wh, lease) : P2(wh, lease)) == ProposalCode.Stale, "cycle " + i + ": simultaneous request of the other player refused");
            var d = At(HeavyOp.Drop, TV, l, i, 1.5f); var s = At(HeavyOp.Settle, TV, l, i, 0.2f);
            Expect((p2 ? P2(wh, d) : P1(wh, d)) == ProposalCode.Accepted && Rec(wh, TV).Moving, "cycle " + i + ": drop");
            Expect((p2 ? P2(wh, s) : P1(wh, s)) == ProposalCode.Accepted && !Rec(wh, TV).Moving && Rec(wh, TV).Pos.X == i, "cycle " + i + ": settle");
        }
        Expect(wh.Heavy.Total == 1 && wh.Heavy.Leases == 6 && wh.Heavy.Drops == 6 && wh.Heavy.Settles == 6, "one record for six cycles (no duplicate)");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted, "P2 grabs it");
        Expect(P1(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale && Rec(wh, TV).CarriedBy(2), "exactly one holder");
    }

    // ------------------------------------------------------------------------------------------------ 17
    static string Body(string src, string signature)
    {
        int a = src.IndexOf(signature, StringComparison.Ordinal); if (a < 0) throw new Exception("missing " + signature);
        int open = src.IndexOf('{', a), depth = 0;
        for (int i = open; i < src.Length; i++) { if (src[i] == '{') depth++; else if (src[i] == '}' && --depth == 0) return src.Substring(a, i - a + 1); }
        throw new Exception("unbalanced " + signature);
    }
    static void SourceScanH11()
    {
        var h = Src("Plugin", "HeavySync.cs");
        Expect(Body(h, "void OnDropped(Component pick)").Contains("StartMoving(hit, Now, \"dropped (original DropHeavy)\")"), "DropHeavy postfix commits the release pose at once");
        Expect(Body(h, "void StartMoving(Sim s, double now, string why)").Contains("SendTxn(HeavyOp.Drop, s.Ns, s.Hash, s.Lease, t.position, t.rotation, why);"), "Drop = the actual transform after the original DropHeavy");
        var stream = Body(h, "void Stream(Sim s, Transform t, double now, bool final)");
        Expect(stream.Contains("DeliveryMethod.Unreliable") && stream.Contains("s.Policy.ShouldSend(") && !stream.Contains("SendTxn") && !stream.Contains("Commit") && !stream.Contains("new NetDataWriter"), "snapshots: unreliable, rate/epsilon-limited, no registry commit, reused writer");
        var sim = Body(h, "void SimFrame(double now, int region)");
        Expect(sim.Contains("Stream(s, t, now, true);") && sim.Contains("SendTxn(HeavyOp.Settle,") && sim.Contains("SettleRules.OwnerShouldReport("), "final snapshot + Settle at the W3 rest rule");
        Expect(sim.Contains("if (s.Phase == Phase.Falling && stream && !paused) Stream("), "streams only while its physics moves it and the other player is in the region");
        var mf = Body(h, "void MotionFrame(double now)");
        Expect(mf.Contains("m.Buf.Sample(now, out p)") && mf.Contains("m.Buf.Drained(now)") && mf.Contains("if (r.Moving ||"), "observer follows the buffer, exact pose once settled and played out");
        Expect(Body(h, "void OnMessage(NetPeer peer, byte type, NetPacketReader r)").Contains("_filter.Check(m, has, rec, Other, _world.LocalRegion)"), "lease / region / sequence filter on every snapshot");
        Expect(Body(h, "void Adopt(Obj o, HeavyRecord r, int region)").Contains("o.Rb.isKinematic = false; o.Rb.useGravity = true;"), "handover continues the original post-DropHeavy physics state");
        foreach (var bad in new[] { "WaitForSeconds", "Invoke(", "StartCoroutine", "Instantiate(", "AddComponent", "OnGUI", "Time.timeScale =" })
            Expect(!h.Contains(bad), "HeavySync contains no '" + bad + "' (no delays, no fake objects, no UI, never unpauses)");
        Expect(Count(h, "_stopCarry(") == 1 && Count(h, "_pickUpHeavy(") == 1, "original calls unchanged");
        Expect(h.Contains("_node.MessageReceived += OnMessage;") && h.Contains("_node.MessageReceived -= OnMessage;"), "message handler registered + removed");
        var g = Src("Plugin", "RemoteGear.cs");
        Expect(g.Contains("void ShowCarry(int itemId, GameObject scene, object ps)") && !g.Contains("RemoteCarried("), "avatar copy from the lease record");
        var shown = Body(h, "public int CarryShown(int snapshotItem, out GameObject scene)");
        Expect(shown.Contains("Refresh();") && shown.Contains("snapshotItem == _suppressItem ? -1"), "same-frame switch; no snapshot-driven duplicate after the drop");
        // Phase D: ordinary TAB drops keep their own original flow (WorldItemSync untouched by H1.1)
        var wi = Src("Plugin", "WorldItemSync.cs");
        Expect(wi.Contains("case DropDecision.HostOriginal: _capture.Arm(item); return true;"), "host: the original DropItemNewSystem runs at once (TAB open)");
        Expect(wi.Contains("\"DropTranspiler\"") && wi.Contains("i._capture.Seen(go);"), "the original instantiated object is captured from the Instantiate call");
        Expect(wi.Contains("if (SettleRules.ProjectToSurface(it.Settled))") && wi.Contains("SettleRules.OwnerShouldReport("), "observer surface projection while the original physics is paused; rest pose reported later");
        Expect(!wi.Contains("Time.timeScale =") && !wi.Contains("HeavyMotion") && !wi.Contains("DropHeavy"), "TAB drops never unpause physics and do not use the heavy-drop path");
        var t = Src("Shared", "WorldTime.cs");
        Expect(t.Contains("case HeavyOp.Settle: lease = t.Lease; return Heavy.Settle(") && t.Contains("Heavy.Release(slot, t.Ns, t.Hash, t.Lease, RegionOfSlot("), "Settle + handover-aware Release wired in HeavyTransaction");
    }
}
