// H3 offline tests: heavy loot sale integrity (HeavyBook.Sell / Unsell, WorldHost.HeavyTransaction ops 7 / 8) and the Ranger
// rear hatch on the existing vdoor sync (VehicleDoors mask v2, door 2), plus source scans of the Unity glue (HeavySaleSync.cs,
// HeavySync wiring, WalletSync.CountNow, VehicleSync.FindHatch). Reproduces the two-PC report (0.22.1: one cargo TV sold again
// and again, +298 each time) at the host model level. In-game behaviour (original shop UI, original destroy, money display,
// hatch animation) is manual two-PC QA (docs/TESTING.md section 46).
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class H3Tests
{
    static int _passed, _failed;
    const int Hideout = 0, Madison = 99;
    const uint TV = 0xA1B2C3D4, Printer = 0x0BADF00D;
    const uint Digest = 0x50D2F273;
    const int TVItem = 28, PrinterItem = 84;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);
    static uint _pid = 100;

    static int Main()
    {
        Run("01 wire: heavyTxn Sell = 7 / Unsell = 8 round trip, op 9 refused; vdoor mask v2 (3 doors), door 3 refused; protocol 34 / mod 0.23.0", Wire);
        Run("02 double selling (two-PC report): the cargo TV is sold once; repeated sale, other player's sale, release / drop / lease afterwards never bring it back", DoubleSell);
        Run("03 concurrent sales: host in place vs Player-2 proposal in both orders - exactly one sale; a retransmitted proposal is answered from the cache", Concurrent);
        Run("04 Player 1 / Player 2: untouched census sale via the host catalog, resting objects of the other player, moving / carried / other region / bad digest refused", Players);
        Run("05 different objects stay sellable: selling one TV leaves the second TV and the printer untouched; stale views never hit a newer object", DifferentObjects);
        Run("06 cargo: an object in the car is not sellable, the cargo list is never touched by a sale, a sold id cannot be loaded or picked, id reuse is lease-guarded", Cargo_);
        Run("07 Player-2 undo (granted sale no longer possible): exact restore once, only by the seller, only within the window, only if nothing changed", Undo);
        Run("08 reload / reconnect: a consumed sale survives Player 2 leaving and rejoining; leaving drops the undo right; emptied region = original reload", Reconnect);
        Run("09 Ranger rear hatch: door 2 committed / replayed like the side doors (quick, slow, fast finish), independent bits, moving-car rule, region reset", Hatch);
        Run("10 regressions: H1 / H2 lease, drop, settle, load, unload unchanged; registry list v3; sale decision cost bounded", Regressions);
        Run("11 Unity glue: original Sell_Item_NewSystem gated on both roles, original selection rule, resume once, hold, undo, wallet report, hatch binding", Glue);
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
    static WorldHost Host(int p1Region, int p2Region, int carRegion)
    {
        var wh = new WorldHost(9) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, p1Region, 0);
        if (p2Region != Regions.None) wh.SetPlayerRegion(2, p2Region, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);
        wh.Fleet.Register(GT, 1, true, wh.Outbox);
        wh.Fleet.Place(Ranger, carRegion, wh.Outbox);
        wh.Fleet.Place(GT, carRegion, wh.Outbox);
        wh.CargoVehicles.Add(Ranger);
        wh.HeavyCatalog = (int ns, uint hash, uint digest, out string why) =>
        {
            why = null;
            if (digest != Digest) { why = "digest"; return ProposalCode.NotAuthorized; }
            if (hash != TV && hash != Printer) { why = "unknown"; return ProposalCode.NotAuthorized; }
            return ProposalCode.Accepted;
        };
        wh.HeavyItemId = (ns, hash) => hash == TV ? TVItem : hash == Printer ? PrinterItem : 0;
        wh.Outbox.Clear();
        return wh;
    }
    static HeavyTxnRequest Req(HeavyOp op, uint hash, ushort lease, int ns, bool cargo, float x = 0)
    {
        return new HeavyTxnRequest { Op = op, Ns = ns, Hash = hash, Digest = cargo ? 0u : Digest, Lease = lease, Pos = new Vec3(x, 1, 2), Rot = Quat16.From(0, 0, 0, 1), Cargo = cargo };
    }
    static HeavyTxnRequest Sell(uint hash, ushort seen, int ns, bool cargo) { return Req(HeavyOp.Sell, hash, seen, ns, cargo); }
    static ProposalCode P2(WorldHost wh, HeavyTxnRequest t, double now = 1, uint id = 0)
    {
        uint rev; int sr; float sh;
        return wh.HandleProposal("cli", 2, id != 0 ? id : ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = t.Encode() }, now, out rev, out sr, out sh);
    }
    static ProposalCode P1(WorldHost wh, HeavyTxnRequest t, out ushort lease, double now = 1) { string why; return wh.HeavyTransaction(1, t, now, out lease, out why); }
    static ProposalCode P1(WorldHost wh, HeavyTxnRequest t, double now = 1) { ushort l; return P1(wh, t, out l, now); }
    static bool TryRec(WorldHost wh, uint hash, int ns, out HeavyRecord rec)
    {
        rec = default(HeavyRecord);
        var e = wh.Reg.Get(StateScope.Region, ns, WorldKeys.Heavy);
        List<HeavyRecord> l; if (e == null || !HeavyList.TryParse(e.Value, out l)) return false;
        foreach (var r in l) if (r.Hash == hash) { rec = r; return true; }
        return false;
    }
    static HeavyRecord Rec(WorldHost wh, uint hash, int ns) { HeavyRecord r; Expect(TryRec(wh, hash, ns, out r), "record " + hash.ToString("X8")); return r; }
    static bool Has(WorldHost wh, uint hash, int ns) { HeavyRecord r; return TryRec(wh, hash, ns, out r); }
    static List<CargoEntry> CargoList(WorldHost wh)
    {
        var e = wh.Reg.Get(StateScope.Session, Ranger, WorldKeys.Cargo);
        if (e == null) return new List<CargoEntry>();
        List<CargoEntry> l; Expect(HeavyCargo.TryParse(e.Value, out l), "cargo list"); return l;
    }
    static List<CargoEntry> Slots(params int[] items)
    {
        var it = new int[30]; var pos = new Vec3[30]; var eul = new Vec3[30];
        for (int i = 0; i < 30; i++) { it[i] = i < items.Length ? items[i] : -1; pos[i] = new Vec3(0.1f * i, 0.8f, -1.5f); eul[i] = new Vec3(0, 10 * i, 0); }
        return HeavyCargo.FromSaveSlots(it, pos, eul, e => Quat16.From(0, 0, 0, 1));
    }
    // The car arrives in the hideout with saved cargo; the given slot takes object #index out of the bed and puts it down next
    // to the computer (drop + settle). Returns the synthetic id; its record rests there, simulated by the dropper.
    static uint Unload(WorldHost wh, byte slot, int index, int ns = Hideout)
    {
        var e = CargoList(wh)[index]; uint syn = HeavyCargo.Synthetic(e.ItemId, e.Id);
        var pick = Req(HeavyOp.Lease, syn, 0, ns, true);
        ushort l;
        if (slot == 1) Expect(P1(wh, pick, out l) == ProposalCode.Accepted, "P1 takes the cargo object out");
        else { Expect(P2(wh, pick) == ProposalCode.Accepted, "P2 takes the cargo object out"); l = Rec(wh, syn, ns).Lease; }
        var d = Req(HeavyOp.Drop, syn, l, ns, true, 3); var s = Req(HeavyOp.Settle, syn, l, ns, true, 3.2f);
        if (slot == 1) Expect(P1(wh, d) == ProposalCode.Accepted && P1(wh, s) == ProposalCode.Accepted, "P1 drop + settle");
        else Expect(P2(wh, d) == ProposalCode.Accepted && P2(wh, s) == ProposalCode.Accepted, "P2 drop + settle");
        return syn;
    }
    static ushort P1RestCensus(WorldHost wh, uint hash, int ns)
    {
        ushort l; Expect(P1(wh, Req(HeavyOp.Lease, hash, 0, ns, false), out l) == ProposalCode.Accepted, "P1 lease");
        Expect(P1(wh, Req(HeavyOp.Drop, hash, l, ns, false)) == ProposalCode.Accepted && P1(wh, Req(HeavyOp.Settle, hash, l, ns, false, 0.5f)) == ProposalCode.Accepted, "P1 drop + settle");
        return l;
    }

    // ------------------------------------------------------------------------------------------------ 01
    static void Wire()
    {
        foreach (var op in new[] { HeavyOp.Sell, HeavyOp.Unsell })
            foreach (var cargo in new[] { false, true })
            {
                var t = Req(op, cargo ? HeavyCargo.Synthetic(TVItem, 7) : TV, 42, Hideout, cargo);
                var v = t.Encode(); HeavyTxnRequest back;
                Expect(v.Length == HeavyTxnRequest.WireSize && HeavyTxnRequest.TryParse(v, out back) && back.Op == op && back.Cargo == cargo && back.Lease == 42 && back.Hash == t.Hash, op + " codec (cargo " + cargo + ")");
            }
        var bad = Req(HeavyOp.Sell, TV, 0, Hideout, false).Encode(); bad[0] = 10; HeavyTxnRequest x;   // W6B.2: op 9 = Unpack
        Expect(!HeavyTxnRequest.TryParse(bad, out x), "op 10 refused (op 9 = W6B.2 Unpack)");
        Expect(Sell(TV, 0, Hideout, false).ToString().Contains("(untouched)"), "log shows an untouched sale");
        // vdoor v2
        Expect(VehicleDoors.Count == 3 && VehicleDoors.OpenBits == 0x07 && VehicleDoors.SlowBits == 0x38 && VehicleDoors.SlowBit(2) == 0x20, "3 doors: open bits 0-2, slow bits 3-5");
        byte m; Expect(VehicleDoors.TryParseState(new byte[] { 0x3F }, out m) && m == 0x3F && !VehicleDoors.TryParseState(new byte[] { 0x40 }, out m), "state range");
        int d; bool open; DoorMode mode;
        Expect(VehicleDoors.TryParseProposal(VehicleDoors.Proposal(2, true, DoorMode.Slow), out d, out open, out mode) && d == 2 && open && mode == DoorMode.Slow, "door 2 proposal");
        Expect(!VehicleDoors.TryParseProposal(new byte[] { 3, 1, 0 }, out d, out open, out mode), "door 3 refused");
        Expect(VehicleDoors.Name(2) == "rear hatch" && VehicleDoors.Name(0) == "L" && VehicleDoors.Name(1) == "R", "log names");
        Expect(ProtocolInfo.ProtocolVersion == 41 && ProtocolInfo.ModVersion == "0.30.0", "protocol 34 / mod 0.23.0 (wire change: both PCs need 0.23.0)");
        Expect(Validation.Check(new Hello { ModVersion = "0.22.1", ClientNonce = new byte[JoinAuth.NonceBytes], InstanceId = "x" }, null, null, "h", false) == RejectCode.ModVersionMismatch, "0.22.1 client refused");
    }

    // ------------------------------------------------------------------------------------------------ 02
    static void DoubleSell()
    {
        // 0.22.1 log: Ranger in the hideout with a TV in the bed; Player 1 takes it out, it rests next to the computer and is sold
        var wh = Host(Hideout, Hideout, Hideout);
        Expect(wh.CargoSeed(Ranger, Slots(TVItem)), "seed");
        uint tv = Unload(wh, 1, 0);
        var r = Rec(wh, tv, Hideout);
        Expect(r.State == HeavyState.Placed && r.Cargo && r.Actor == 1, "TV rests in the hideout, simulated by P1");
        Expect(P1(wh, Sell(tv, r.Lease, Hideout, true)) == ProposalCode.Accepted, "P1's original sale: host consumes the record");
        Expect(!Has(wh, tv, Hideout) && CargoList(wh).Count == 0 && wh.Heavy.Sold == 1, "record gone on both PCs (no respawn source), not back in the car");
        // the bug path: the destroyed object's release used to keep the record and re-create it
        Expect(P1(wh, Req(HeavyOp.Release, tv, r.Lease, Hideout, true)) == ProposalCode.Unchanged && !Has(wh, tv, Hideout), "late 'object destroyed' release: no record, nothing re-created");
        // every way to sell it again
        Expect(P1(wh, Sell(tv, r.Lease, Hideout, true)) == ProposalCode.Stale, "P1 repeats the sale (same view): Stale");
        Expect(P1(wh, Sell(tv, 0, Hideout, true)) == ProposalCode.Stale, "P1 sale without a record: Stale");
        Expect(P2(wh, Sell(tv, r.Lease, Hideout, true)) == ProposalCode.Stale, "P2 sells its stale copy: Stale");
        Expect(P2(wh, Req(HeavyOp.Lease, tv, 0, Hideout, true)) == ProposalCode.Stale && P1(wh, Req(HeavyOp.Lease, tv, 0, Hideout, true)) == ProposalCode.Stale, "nobody can pick the sold TV up (not in any car, no record)");
        Expect(P2(wh, Req(HeavyOp.Drop, tv, r.Lease, Hideout, true)) == ProposalCode.Stale && P2(wh, Req(HeavyOp.Settle, tv, r.Lease, Hideout, true)) == ProposalCode.Stale, "no drop / settle of a sold object");
        Expect(wh.Heavy.Sold == 1 && wh.Heavy.SaleRefused == 3, "one sale, the three repeated sales refused");
    }

    // ------------------------------------------------------------------------------------------------ 03
    static void Concurrent()
    {
        // P2 first, then the host's own click on its stale copy
        var wh = Host(Hideout, Hideout, Hideout);
        Expect(wh.CargoSeed(Ranger, Slots(TVItem)), "seed");
        uint tv = Unload(wh, 2, 0); ushort seen = Rec(wh, tv, Hideout).Lease;
        uint pid = 9000;
        Expect(P2(wh, Sell(tv, seen, Hideout, true), 1, pid) == ProposalCode.Accepted, "P2 committed first");
        Expect(P1(wh, Sell(tv, seen, Hideout, true)) == ProposalCode.Stale, "host's simultaneous click: Stale (nothing happens, no money)");
        Expect(P2(wh, Sell(tv, seen, Hideout, true), 1.2, pid) == ProposalCode.Accepted && wh.Heavy.Sold == 1, "retransmitted proposal answered from the cache, not applied twice");
        Expect(P2(wh, Sell(tv, seen, Hideout, true), 1.3) == ProposalCode.Stale, "a new proposal for the same sale (replay): Stale");
        // host first, then Player 2's proposal arrives
        var wh2 = Host(Hideout, Hideout, Hideout);
        Expect(wh2.CargoSeed(Ranger, Slots(TVItem)), "seed 2");
        uint tv2 = Unload(wh2, 1, 0); ushort s2 = Rec(wh2, tv2, Hideout).Lease;
        Expect(P1(wh2, Sell(tv2, s2, Hideout, true)) == ProposalCode.Accepted, "host committed first");
        Expect(P2(wh2, Sell(tv2, s2, Hideout, true)) == ProposalCode.Stale && wh2.Heavy.Sold == 1, "Player 2's sale arrives later: Stale");
    }

    // ------------------------------------------------------------------------------------------------ 04
    static void Players()
    {
        var wh = Host(Hideout, Hideout, Madison);
        // an untouched census heavy object of the hideout, sold by Player 2: host catalog check, then Removed
        var bad = Sell(TV, 0, Hideout, false); bad.Digest = 1;
        Expect(P2(wh, bad) == ProposalCode.NotAuthorized && !Has(wh, TV, Hideout), "wrong census digest: refused, no record");
        Expect(P2(wh, Sell(0xDEADBEEF, 0, Hideout, false)) == ProposalCode.NotAuthorized, "unknown census id refused");
        Expect(P2(wh, Sell(TV, 0, Hideout, false)) == ProposalCode.Accepted, "P2 sells the untouched TV");
        var r = Rec(wh, TV, Hideout);
        Expect(r.State == HeavyState.Removed && r.Actor == 0 && !r.HasPose, "Removed = hidden on both PCs");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0, Hideout, false)) == ProposalCode.Stale, "Player 2 cannot pick the sold object up");
        Expect(P1(wh, Sell(TV, 0, Hideout, false)) == ProposalCode.Stale && P1(wh, Sell(TV, r.Lease, Hideout, false)) == ProposalCode.Stale, "host sale of the sold object: Stale");
        // Player 1 sells a census object Player 2 put down (resting, simulated by P2)
        Expect(P2(wh, Req(HeavyOp.Lease, Printer, 0, Hideout, false)) == ProposalCode.Accepted, "P2 lease printer");
        ushort pl = Rec(wh, Printer, Hideout).Lease;
        Expect(P2(wh, Req(HeavyOp.Drop, Printer, pl, Hideout, false)) == ProposalCode.Accepted, "P2 drop (moving)");
        Expect(P1(wh, Sell(Printer, pl, Hideout, false)) == ProposalCode.Busy, "still moving under P2's physics: host sale Busy");
        Expect(P2(wh, Req(HeavyOp.Settle, Printer, pl, Hideout, false, 0.5f)) == ProposalCode.Accepted, "P2 settle");
        Expect(P1(wh, Sell(Printer, pl, Hideout, false)) == ProposalCode.Accepted && Rec(wh, Printer, Hideout).State == HeavyState.Removed, "host sells the resting printer once");
        // carried / other region
        var w2 = Host(Hideout, Madison, Madison);
        ushort tl; Expect(P1(w2, Req(HeavyOp.Lease, TV, 0, Hideout, false), out tl) == ProposalCode.Accepted, "P1 carries the TV");
        Expect(P1(w2, Sell(TV, tl, Hideout, false)) == ProposalCode.Stale, "a carried object is not sold");
        Expect(P2(w2, Sell(TV, tl, Hideout, false)) == ProposalCode.NotAuthorized, "Player 2 in another region: NotAuthorized");
        // the seller's own moving object (it simulates it) may be sold
        Expect(P1(w2, Req(HeavyOp.Drop, TV, tl, Hideout, false)) == ProposalCode.Accepted && P1(w2, Sell(TV, tl, Hideout, false)) == ProposalCode.Accepted, "own falling object: sale accepted");
    }

    // ------------------------------------------------------------------------------------------------ 05
    static void DifferentObjects()
    {
        var wh = Host(Hideout, Hideout, Hideout);
        Expect(wh.CargoSeed(Ranger, Slots(TVItem, TVItem, PrinterItem)), "seed 2 TVs + printer");
        uint a = Unload(wh, 1, 0), b = Unload(wh, 2, 0), c = Unload(wh, 1, 0);
        Expect(a != b && HeavyCargo.ItemOf(a) == HeavyCargo.ItemOf(b), "two different TVs of the same item");
        ushort la = Rec(wh, a, Hideout).Lease, lb = Rec(wh, b, Hideout).Lease, lc = Rec(wh, c, Hideout).Lease;
        Expect(P1(wh, Sell(a, la, Hideout, true)) == ProposalCode.Accepted, "first TV sold");
        Expect(Has(wh, b, Hideout) && Has(wh, c, Hideout) && Rec(wh, b, Hideout).Lease == lb, "second TV and printer untouched");
        Expect(P1(wh, Sell(b, la, Hideout, true)) == ProposalCode.Stale, "a view of the first TV never sells the second");
        Expect(P2(wh, Sell(b, lb, Hideout, true)) == ProposalCode.Accepted && P1(wh, Sell(c, lc, Hideout, true)) == ProposalCode.Accepted, "second TV (P2) and printer (P1) sold legitimately");
        Expect(wh.Heavy.Sold == 3 && wh.Heavy.Total == 0, "three sales, nothing left");
    }

    // ------------------------------------------------------------------------------------------------ 06
    static void Cargo_()
    {
        var wh = Host(Hideout, Hideout, Hideout);
        Expect(wh.CargoSeed(Ranger, Slots(TVItem, PrinterItem)), "seed");
        var inCar = CargoList(wh)[1]; uint pr = HeavyCargo.Synthetic(inCar.ItemId, inCar.Id);
        Expect(P1(wh, Sell(pr, 0, Hideout, true)) == ProposalCode.Stale && P2(wh, Sell(pr, 0, Hideout, true)) == ProposalCode.Stale, "an object still in the car is not sold");
        Expect(CargoList(wh).Count == 2, "cargo list untouched");
        uint tv = Unload(wh, 1, 0); ushort l = Rec(wh, tv, Hideout).Lease;
        Expect(CargoList(wh).Count == 1, "TV out of the car");
        Expect(P1(wh, Sell(tv, l, Hideout, true)) == ProposalCode.Accepted && CargoList(wh).Count == 1 && CargoList(wh)[0].Id == inCar.Id, "sale touches only the world record; the printer stays in the car");
        var load = new HeavyTxnRequest { Op = HeavyOp.Load, Ns = Hideout, Hash = tv, Digest = Ranger, Lease = l, Pos = new Vec3(0.2f, 0.9f, -1.6f), Rot = Quat16.From(0, 0, 0, 1), Cargo = true };
        Expect(P1(wh, load) == ProposalCode.Stale && CargoList(wh).Count == 1, "a sold object cannot be loaded back");
        // the printer is unloaded, re-loaded and still sellable afterwards (legitimate cargo transfers preserved)
        uint p = Unload(wh, 2, 0); ushort lp = Rec(wh, p, Hideout).Lease;
        var reload = new HeavyTxnRequest { Op = HeavyOp.Load, Ns = Hideout, Hash = p, Digest = Ranger, Lease = lp, Pos = new Vec3(0.2f, 0.9f, -1.6f), Rot = Quat16.From(0, 0, 0, 1), Cargo = true };
        Expect(P2(wh, Req(HeavyOp.Lease, p, 0, Hideout, true)) == ProposalCode.Accepted, "P2 picks the printer up again");
        lp = Rec(wh, p, Hideout).Lease; reload.Lease = lp;
        Expect(P2(wh, Req(HeavyOp.Drop, p, lp, Hideout, true)) == ProposalCode.Accepted && P2(wh, reload) == ProposalCode.Accepted && CargoList(wh).Count == 1, "re-loaded into the car");
        Expect(P2(wh, Sell(p, lp, Hideout, true)) == ProposalCode.Stale, "stale view of the re-loaded printer: not sold");
        uint p2 = Unload(wh, 1, 0); ushort lp2 = Rec(wh, p2, Hideout).Lease;
        Expect(p2 == p && lp2 != lp, "same cargo id, new lease after the next unload");
        Expect(P2(wh, Sell(p, lp, Hideout, true)) == ProposalCode.Stale, "a replay with the old lease never sells the reused id");
        Expect(P2(wh, Sell(p2, lp2, Hideout, true)) == ProposalCode.Accepted, "current view sells it once");
    }

    // ------------------------------------------------------------------------------------------------ 07
    static void Undo()
    {
        var wh = Host(Hideout, Hideout, Hideout);
        Expect(wh.CargoSeed(Ranger, Slots(TVItem)), "seed");
        uint tv = Unload(wh, 1, 0); var before = Rec(wh, tv, Hideout);
        Expect(P2(wh, Sell(tv, before.Lease, Hideout, true), 5) == ProposalCode.Accepted && wh.Heavy.PendingSales == 1, "P2 granted");
        Expect(P1(wh, Req(HeavyOp.Unsell, tv, before.Lease, Hideout, true), 5.1) == ProposalCode.Stale, "the host never undoes Player 2's sale");
        Expect(P2(wh, Req(HeavyOp.Unsell, tv, (ushort)(before.Lease + 1), Hideout, true), 5.1) == ProposalCode.Stale, "wrong view: refused");
        Expect(P2(wh, Req(HeavyOp.Unsell, tv, before.Lease, Hideout, true), 5.2) == ProposalCode.Accepted, "Player 2's shop closed in the round trip: undo");
        var after = Rec(wh, tv, Hideout);
        Expect(after.State == HeavyState.Placed && after.Cargo && after.Actor == 0 && after.HasPose && Vec3.Distance(after.Pos, before.Pos) < 1e-4f && after.Lease != before.Lease, "exact rest pose back, simulated by nobody, new lease");
        Expect(P2(wh, Req(HeavyOp.Unsell, tv, before.Lease, Hideout, true), 5.3) == ProposalCode.Stale && Has(wh, tv, Hideout) && wh.Heavy.Unsold == 1, "second undo refused");
        Expect(P1(wh, Sell(tv, after.Lease, Hideout, true)) == ProposalCode.Accepted && wh.Heavy.Sold == 2, "the returned object is sellable once more (it was never paid)");
        // window
        var w2 = Host(Hideout, Hideout, Hideout);
        Expect(P2(w2, Sell(TV, 0, Hideout, false), 10) == ProposalCode.Accepted, "untouched census sale by P2");
        Expect(P2(w2, Req(HeavyOp.Unsell, TV, 0, Hideout, false), 10 + HeavyBook.UnsellWindow + 0.1) == ProposalCode.Stale && Rec(w2, TV, Hideout).State == HeavyState.Removed, "too late: stays sold");
        var w3 = Host(Hideout, Hideout, Hideout);
        Expect(P2(w3, Sell(TV, 0, Hideout, false), 10) == ProposalCode.Accepted && P2(w3, Req(HeavyOp.Unsell, TV, 0, Hideout, false), 10.2) == ProposalCode.Accepted && !Has(w3, TV, Hideout), "undo of an untouched census sale: original scene pose (no record)");
        // something changed since the sale: the region reset in between
        var w4 = Host(Hideout, Hideout, Hideout);
        Expect(P2(w4, Sell(Printer, 0, Hideout, false), 1) == ProposalCode.Accepted, "sale");
        w4.SetPlayerRegion(1, Madison, 1.1); w4.SetPlayerRegion(2, Madison, 1.1);   // hideout empties -> original reload
        Expect(!Has(w4, Printer, Hideout) && w4.Heavy.PendingSales == 0, "reset drops the record and the undo right");
        Expect(P2(w4, Req(HeavyOp.Unsell, Printer, 0, Hideout, false), 1.2) == ProposalCode.Stale, "undo after a reset refused");
    }

    // ------------------------------------------------------------------------------------------------ 08
    static void Reconnect()
    {
        var wh = Host(Hideout, Hideout, Hideout);
        Expect(wh.CargoSeed(Ranger, Slots(TVItem, TVItem)), "seed");
        uint a = Unload(wh, 2, 0), b = Unload(wh, 1, 0);
        ushort la = Rec(wh, a, Hideout).Lease;
        Expect(P2(wh, Sell(a, la, Hideout, true), 2) == ProposalCode.Accepted, "P2 sells TV a");
        wh.SetPlayerRegion(2, Regions.None, 3);   // disconnect
        Expect(!Has(wh, a, Hideout) && Has(wh, b, Hideout) && wh.Heavy.PendingSales == 0, "sale stays consumed; P2's undo right dropped; TV b kept (P1 still there)");
        wh.SetPlayerRegion(2, Hideout, 4);        // rejoin: the client receives the registry snapshot (no record for a)
        Expect(P2(wh, Sell(a, la, Hideout, true), 4.1) == ProposalCode.Stale, "rejoined Player 2 replays the old sale: Stale");
        Expect(P2(wh, Req(HeavyOp.Unsell, a, la, Hideout, true), 4.2) == ProposalCode.Stale && !Has(wh, a, Hideout), "no undo after the reconnect");
        ushort lb = Rec(wh, b, Hideout).Lease;
        Expect(P2(wh, Sell(b, lb, Hideout, true), 4.3) == ProposalCode.Accepted && wh.Heavy.Sold == 2, "TV b still sellable once");
        // host reload of the hideout scene (same region): records are host session state, the sale is not undone
        var w2 = Host(Hideout, Hideout, Hideout);
        Expect(P1(w2, Sell(TV, 0, Hideout, false)) == ProposalCode.Accepted, "host sells the untouched census TV");
        Expect(Rec(w2, TV, Hideout).State == HeavyState.Removed && P2(w2, Sell(TV, 0, Hideout, false)) == ProposalCode.Stale, "Removed until the region empties");
        w2.SetPlayerRegion(1, Madison, 2); w2.SetPlayerRegion(2, Madison, 2);
        Expect(!Has(w2, TV, Hideout), "region emptied = original scene reload (single-player rule for scene heavy objects)");
    }

    // ------------------------------------------------------------------------------------------------ 09
    static void Hatch()
    {
        var wh = Host(Madison, Madison, Madison);
        wh.FleetInitialized(0.5);                 // holder invariant: Player 1 simulates the Ranger
        string why;
        uint rev; int sr; float sh;
        Func<int, bool, DoorMode, double, ProposalCode> p2 = (door, open, mode, t) => wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = Ranger, Key = WorldKeys.VehicleDoor, Value = VehicleDoors.Proposal(door, open, mode) }, t, out rev, out sr, out sh);
        Expect(p2(2, true, DoorMode.Quick, 1) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0x04, "Player 2 opens the hatch: committed for both PCs");
        Expect(wh.SetDoor(1, Ranger, 0, true, DoorMode.Quick, 1.1, out why) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0x05, "side door independent of the hatch");
        Expect(wh.SetDoor(1, Ranger, 2, false, DoorMode.Slow, 1.2, out why) == ProposalCode.Accepted && wh.DoorMask(Ranger) == (0x01 | 0x20), "Player 1 closes the hatch slowly (slow mark bit 5)");
        Expect(p2(2, false, DoorMode.FastFinish, 1.3) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0x01, "fast finish clears only the hatch slow mark");
        Expect(p2(2, false, DoorMode.Quick, 1.4) == ProposalCode.Unchanged, "closing a closed hatch: unchanged");
        // observer replica replays door 2 like the side doors
        var o = new DoorReplica(); o.Bind(0); o.OnCommitted(0); o.Applied(); byte want;
        o.OnCommitted(0x04 | 0x20);
        Expect(o.Wanted(out want) && o.Plan(2) == DoorStep.OpenSlow && o.Plan(0) == DoorStep.None && o.Plan(1) == DoorStep.None, "remote slow hatch open -> slow swing of door 2 only");
        o.Applied();
        Expect(!o.Wanted(out want) && VehicleDoors.IsSlow(o.AppliedSlow, 2), "slow replay running");
        o.OnCommitted(0x04);
        Expect(o.Wanted(out want) && o.Plan(2) == DoorStep.FinishFast, "fast finish of the hatch");
        o.Applied(); o.SlowEnded(2);
        Expect(!o.Wanted(out want) && o.AppliedSlow == 0 && o.Local == 0x04, "hatch open, converged");
        var own = new DoorReplica(); own.Bind(0); own.OnCommitted(0); own.Applied();
        Expect(own.TakeLocalChanges(0x04) == 0x04, "local hatch change detected as door 2");
        own.Sent(); own.OnCommitted(0x04); own.Result();
        Expect(!own.Wanted(out want), "own hatch change not echoed");
        // moving car: the non-holder may not open it; the holder (driver) may
        wh.Motion.Report(Ranger, true, true, 2);
        Expect(p2(2, true, DoorMode.Quick, 2.1) == ProposalCode.Busy, "moving car: Player 2's hatch open refused");
        Expect(wh.SetDoor(1, Ranger, 2, true, DoorMode.Quick, 2.2, out why) == ProposalCode.Accepted, "holder may");
        // a car that changes region starts with every door closed (fresh MainScene objects)
        wh.FleetInitialized(2.9);                 // the host knows the car's region (Madison)
        wh.Fleet.Place(Ranger, Hideout, wh.Outbox); wh.FleetInitialized(3);
        Expect(wh.DoorMask(Ranger) == 0, "region change resets the hatch with the side doors");
    }

    // ------------------------------------------------------------------------------------------------ 10
    static void Regressions()
    {
        var wh = Host(Madison, Madison, Madison);
        ushort l = P1RestCensus(wh, TV, Madison);
        var load = new HeavyTxnRequest { Op = HeavyOp.Load, Ns = Madison, Hash = TV, Digest = Ranger, Lease = l, Pos = new Vec3(0.2f, 0.9f, -1.6f), Rot = Quat16.From(0, 0, 0, 1) };
        Expect(P1(wh, load) == ProposalCode.Accepted && Rec(wh, TV, Madison).State == HeavyState.Removed && CargoList(wh).Count == 1, "H2 load unchanged");
        Expect(P2(wh, Sell(TV, Rec(wh, TV, Madison).Lease, Madison, false)) == ProposalCode.Stale, "a loaded census object (Removed) is not sellable");
        Expect(P2(wh, Req(HeavyOp.Lease, Printer, 0, Madison, false)) == ProposalCode.Accepted, "H1 P2 lease unchanged");
        ushort pl = Rec(wh, Printer, Madison).Lease;
        Expect(P2(wh, Req(HeavyOp.Release, Printer, pl, Madison, false)) == ProposalCode.Accepted && !Has(wh, Printer, Madison), "H1 release of a carry without pose: record removed (scene pose)");
        Expect(P2(wh, Req(HeavyOp.Remove, Printer, 0, Madison, false)) == ProposalCode.NotAuthorized, "client Remove still refused");
        var list = new List<HeavyRecord> { new HeavyRecord { Hash = 1, State = HeavyState.Removed, Lease = 3, Rot = new Quat16 { W = 32767 } } };
        var v = HeavyList.Encode(list);
        Expect(v[0] == 3 && v.Length == 2 + HeavyRecord.WireSize, "registry list v3 unchanged (no new record state)");
        // cost: 20000 sell / unsell decisions on the host book
        var book = new HeavyBook(); var reg = new StateRegistry(); var box = new List<StateEntry>(); string why;
        var sw = System.Diagnostics.Stopwatch.StartNew(); int gc0 = GC.CollectionCount(0);
        for (int i = 0; i < 20000; i++)
        {
            uint h = (uint)(0x1000 + (i & 15));
            book.Sell(2, Madison, Madison, h, 0, true, false, i, reg, box, out why);
            book.Unsell(2, Madison, h, 0, i + 0.1, reg, box, out why);
            if (box.Count > 64) box.Clear();
        }
        sw.Stop();
        Expect(book.Sold == 20000 && book.Unsold == 20000, "every decision exact");
        Console.WriteLine("      cost: " + (sw.Elapsed.TotalMilliseconds * 1000.0 / 40000).ToString("0.00") + " us per sale decision incl. registry commit, gen0 GCs " + (GC.CollectionCount(0) - gc0));
        Expect(sw.Elapsed.TotalMilliseconds < 4000, "bounded cost");
    }

    // ------------------------------------------------------------------------------------------------ 11
    static void Glue()
    {
        var s = Src("Plugin", "HeavySaleSync.cs");
        Expect(s.Contains("AccessTools.Method(inv, \"Sell_Item_NewSystem\", new[] { typeof(bool) })") && s.Contains("\"SalePrefix\""), "prefix on the original Inventory.Sell_Item_NewSystem(bool) (both roles)");
        var allow = Body(s, "bool AllowSale(object inv, bool chainSell)");
        Expect(allow.Contains("if (_resumeSale) return true;") && allow.Contains("if (_invSelling(inv) && !chainSell) return true;") && allow.Contains("!_itemInShop(item)) return true;"), "ordinary sales and the original's own no-op gate pass untouched");
        Expect(allow.IndexOf("HeavyTransaction(1, t", StringComparison.Ordinal) > 0 && allow.IndexOf("return true;", allow.IndexOf("HeavyTransaction(1, t", StringComparison.Ordinal), StringComparison.Ordinal) > 0, "host: in-place decision before the original runs");
        Expect(allow.Contains("_sims.Remove(hash)") && allow.Contains("Op = HeavyOp.Sell"), "host: no 'object destroyed' release after its own sale");
        Expect(allow.Contains("_world.SendProposal(") && allow.Contains("if (_sale != null) return false;"), "client: one proposal at a time, original refused until the grant");
        Expect(allow.Contains("if (go == null)") && allow.Contains("return false;"), "a listed heavy item without its object pays nothing");
        Expect(allow.Contains("if (_host) { _salesUntracked++;") && allow.Contains("its copy would be a duplicate"), "untracked objects: host original, Player 2 refused");
        var target = Body(s, "GameObject ShopTarget(object item)");
        Expect(target.Contains("FindGameObjectWithTag(\"HeavyShopTrigger\")") && target.Contains("ReferenceEquals(_pickItem(pick), item)") && target.Contains("return go;"), "original selection rule: first sensor object of that item");
        var res = Body(s, "void OnSaleResult(SaleReq q, ProposalCode code)");
        Expect(res.Contains("_resumeSale = true;") && res.Contains("_sellOriginal(q.Inv, true)") && res.Contains("finally { _resumeSale = false; }") && Count(res, "_sellOriginal(") == 1, "granted sale: the original runs exactly once");
        Expect(res.Contains("HeavyOp.Unsell") && res.Contains("SaleBlock(q)") && res.Contains("CountNow("), "undo when it cannot run; wallet report sent at once");
        var block = Body(s, "string SaleBlock(SaleReq q)");
        foreach (var k in new[] { "shop closed", "another item selected", "object gone", "ShopTarget(q.Item), q.Go" }) Expect(block.Contains(k), "grant check: " + k);
        Expect(s.Contains("SaleTimeout") && Body(s, "bool OnSaleResultHook(uint id, ProposalCode code)").Contains("late grant of an abandoned sale"), "abandoned request: a late grant is returned");
        foreach (var bad in new[] { "OnGUI", "GUI.", "Time.timeScale =", "moneyAm +=", "_setMoney" }) Expect(!s.Contains(bad), "HeavySaleSync contains no '" + bad + "' (no UI, never pays itself)");
        var h = Src("Plugin", "HeavySync.cs");
        Expect(h.Contains("internal sealed partial class HeavySync") && h.Contains("InitSale();") && Body(h, "bool OnResult(uint id, ProposalCode code)").Contains("OnSaleResultHook(id, code)"), "HeavySync wiring");
        var rec = Body(h, "bool Reconcile(int region)");
        Expect(rec.Contains("SaleHold(r.Hash)") && rec.Contains("!SaleHold(kv.Key)"), "pending sale keeps its object as shown");
        Expect(Body(h, "public void Update()").Contains("SaleFrame(now);"), "pending sale timeout per frame (null check)");
        var w = Src("Plugin", "WalletSync.cs");
        Expect(Body(w, "public void CountNow(string why)").Contains("Observe(ps);") && Body(w, "public void CountNow(string why)").Contains("ReportNow()"), "Player-2 sale money counted and reported at once");
        var wt = Src("Shared", "WorldTime.cs");
        Expect(Body(wt, "public ProposalCode HeavyTransaction(byte slot, HeavyTxnRequest t, double now, out ushort lease, out string why)").Contains("Heavy.Sell(") && wt.Contains("t.Op == HeavyOp.Remove) { code = ProposalCode.NotAuthorized;"), "host dispatch; client Remove still refused");
        // hatch
        var v = Src("Plugin", "VehicleSync.cs");
        var hatch = Body(v, "Component FindHatch(LocalCar lc, Component dl, Component dr)");
        Expect(hatch.Contains("_doorIsCarDoor(c)") && hatch.Contains("ReferenceEquals(c, dl)") && hatch.Contains("return null;"), "hatch = the only other isCarDoor Door child; ambiguous -> none");
        Expect(Body(v, "void DoorFrame(int region)").Contains("FindHatch(lc, dl, dr)") && Body(v, "void DoorFrame(int region)").Contains("if (!lc.DoorsBound)"), "hatch bound once per car binding");
        Expect(Count(v, "FindHatch(") == 2, "no other caller (no per-frame scene search)");
        Expect(Body(v, "void CloseDoorsLocal(LocalCar lc)").Contains("d <= VehicleDoors.Right"), "entry close touches only the side doors (original L_/R_Door_Close_Delay)");
        Expect(!v.Contains("new[] { dl, dr }"), "no two-door array left");
    }

    // ------------------------------------------------------------------------------------------------ source helpers
    static string Src(params string[] p)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var f = Path.Combine(dir, "src", Path.Combine(p));
            if (File.Exists(f)) return File.ReadAllText(f);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new Exception("source not found: " + Path.Combine(p));
    }
    static int Count(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }
    static string Body(string src, string signature)
    {
        int a = src.IndexOf(signature, StringComparison.Ordinal); if (a < 0) throw new Exception("missing " + signature);
        int open = src.IndexOf('{', a), depth = 0;
        for (int i = open; i < src.Length; i++) { if (src[i] == '{') depth++; else if (src[i] == '}' && --depth == 0) return src.Substring(a, i - a + 1); }
        throw new Exception("unbalanced " + signature);
    }
}
