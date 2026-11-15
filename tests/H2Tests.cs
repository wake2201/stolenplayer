// H2 offline tests: shared heavy vehicle cargo (src/Shared/HeavyCargo.cs + WorldHost.HeavyTransaction Load / synthetic Lease)
// and source scans of the Unity glue (HeavyCargoSync, HeavySync H2 parts, Plugin wiring). Exercises exactly the pure pieces the
// plugin uses: cargo list codec, synthetic ids, host validation (lease, vehicle, region, capacity), exactly-once transitions,
// abandoned-carry restore, travel / region lifetime, host seed from the original save keys, save isolation rules.
// In-game behaviour (original cargo sensor, PlacedInTrunk presentation, car following, original save / load) is manual two-PC
// QA (docs/TESTING.md §39).
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class H2Tests
{
    static int _passed, _failed;
    const int Madison = 99, Hillville = 97;
    const uint TV = 0xA1B2C3D4, Printer = 0x0BADF00D, Clock = 0x0C10C700;
    const uint Digest = 0x50D2F273;
    const int TVItem = 28, PrinterItem = 84, ClockItem = 388;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);
    static uint _pid;
    static readonly HashSet<uint> _syn = new HashSet<uint>();   // synthetic ids handed out in these tests (requests carry the cargo flag)
    static uint Syn(int item, ushort id) { var h = HeavyCargo.Synthetic(item, id); _syn.Add(h); return h; }
    static uint Syn(CargoEntry e) { return Syn(e.ItemId, e.Id); }

    static int Main()
    {
        Run("01 wire: cargo list / synthetic id / Load request codec, limits, malformed values refused, original save-slot order", Codec);
        Run("02 Player 1 loading: held object at rest in the bed -> Load in place: record Removed once, entry with the catalog item id", P1Loading);
        Run("03 Player 2 loading: heavyTxn Load proposal -> same host transaction; both instances read one committed list", P2Loading);
        Run("04 duplicate / simultaneous loads: retransmission, second Load of the same object, foreign lease, two objects at once", Duplicates);
        Run("05 invalid requests: wrong lease, unsupported vehicle, car in another region, pose outside the car, unknown item -> nothing changes", Invalid);
        Run("06 capacity: the original 24-slot save limit - the 25th load is refused and the object keeps its rest state", Capacity);
        Run("07 unload: pickup of a cargo object = synthetic lease (first wins), H1 drop / settle, re-load keeps the cargo id", Unload);
        Run("08 abandoned carry: grant unusable / disconnect before a drop puts the object back into its car; after a drop it stays a world object", Abandoned);
        Run("09 travel + regions: cargo follows the car across regions, load / unload only where the car is, emptied region resets only world records", Travel_);
        Run("10 persistence: host seed from the original HeavyVan_ keys once, restart reproduces the list, save isolation keys stay host-only", Persistence);
        Run("11 one-time effects: loading touches only wheavy + hcargo (no wallet, loot, story or counters), glue never writes rewards", OneTime);
        Run("12 disconnect / reconnect: the cargo list is session state, unchanged by Player 2 leaving; the rejoined Player 2 can unload", Reconnect);
        Run("13 H1.1 regression: lease / drop / settle / release / handover unchanged without a cargo car; Removed objects stay unpickable for Player 2", H1Regression);
        Run("14 Unity glue: original LoadItems / SaveItems handling, Load after the Settle, Player-2 drop rule, presentation from the original drop_object", SourceScan);
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
    static WorldHost Host(int p1Region, int p2Region, int carRegion = Madison)
    {
        var wh = new WorldHost(9) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, p1Region, 0);
        if (p2Region != Regions.None) wh.SetPlayerRegion(2, p2Region, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);
        wh.Fleet.Register(GT, 1, true, wh.Outbox);
        wh.Fleet.Place(Ranger, carRegion, wh.Outbox);
        wh.Fleet.Place(GT, carRegion, wh.Outbox);
        wh.CargoVehicles.Add(Ranger);   // the host plugin adds the car whose original object has a VanHeavyItems bed
        wh.HeavyCatalog = (int ns, uint hash, uint digest, out string why) =>
        {
            why = null;
            if (digest != Digest) { why = "digest"; return ProposalCode.NotAuthorized; }
            if (hash != TV && hash != Printer && hash != Clock) { why = "unknown"; return ProposalCode.NotAuthorized; }
            return ProposalCode.Accepted;
        };
        wh.HeavyItemId = (ns, hash) => hash == TV ? TVItem : hash == Printer ? PrinterItem : hash == Clock ? ClockItem : 0;
        wh.Outbox.Clear();
        return wh;
    }
    static HeavyTxnRequest Req(HeavyOp op, uint hash, ushort lease, int ns = Madison, float x = 0)
    {
        return new HeavyTxnRequest { Op = op, Ns = ns, Hash = hash, Digest = Digest, Lease = lease, Pos = new Vec3(x, 1, 2), Rot = Quat16.From(0, 0, 0, 1), Cargo = _syn.Contains(hash) };
    }
    static HeavyTxnRequest LoadReq(uint hash, ushort lease, ushort car = 0, int ns = Madison, float lx = 0.2f)
    {
        return new HeavyTxnRequest { Op = HeavyOp.Load, Ns = ns, Hash = hash, Digest = car == 0 ? Ranger : car, Lease = lease, Pos = new Vec3(lx, 0.9f, -1.6f), Rot = Quat16.From(0, 0.38f, 0, 0.92f), Cargo = _syn.Contains(hash) };
    }
    static HeavyTxnRequest Pick(uint synth, int ns = Madison) { return new HeavyTxnRequest { Op = HeavyOp.Lease, Ns = ns, Hash = synth, Digest = 0, Rot = new Quat16 { W = 32767 }, Cargo = true }; }
    static ProposalCode P2(WorldHost wh, HeavyTxnRequest t, uint id = 0)
    {
        uint rev; int sr; float sh;
        return wh.HandleProposal("cli", 2, id != 0 ? id : ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = t.Encode() }, 1, out rev, out sr, out sh);
    }
    static ProposalCode P1(WorldHost wh, HeavyTxnRequest t, out ushort lease) { string why; return wh.HeavyTransaction(1, t, 1, out lease, out why); }
    static ProposalCode P1(WorldHost wh, HeavyTxnRequest t) { ushort l; return P1(wh, t, out l); }
    static bool TryRec(WorldHost wh, uint hash, out HeavyRecord rec, int ns = Madison)
    {
        rec = default(HeavyRecord);
        var e = wh.Reg.Get(StateScope.Region, ns, WorldKeys.Heavy);
        List<HeavyRecord> l; if (e == null || !HeavyList.TryParse(e.Value, out l)) return false;
        foreach (var r in l) if (r.Hash == hash) { rec = r; return true; }
        return false;
    }
    static HeavyRecord Rec(WorldHost wh, uint hash, int ns = Madison) { HeavyRecord r; Expect(TryRec(wh, hash, out r, ns), "record " + hash.ToString("X8")); return r; }
    static List<CargoEntry> Cargo(WorldHost wh, ushort car = 0)
    {
        var e = wh.Reg.Get(StateScope.Session, car == 0 ? Ranger : car, WorldKeys.Cargo);
        if (e == null) return new List<CargoEntry>();
        List<CargoEntry> l; Expect(HeavyCargo.TryParse(e.Value, out l), "cargo list readable"); return l;
    }
    // P1 picks up + drops + settles a census object (lease returned): the object rests in the bed
    static ushort P1Rest(WorldHost wh, uint hash, int ns = Madison)
    {
        ushort l; Expect(P1(wh, Req(HeavyOp.Lease, hash, 0, ns), out l) == ProposalCode.Accepted, "P1 lease");
        Expect(P1(wh, Req(HeavyOp.Drop, hash, l, ns)) == ProposalCode.Accepted && P1(wh, Req(HeavyOp.Settle, hash, l, ns, 0.5f)) == ProposalCode.Accepted, "P1 drop + settle");
        return l;
    }
    static ushort P2Rest(WorldHost wh, uint hash, int ns = Madison)
    {
        Expect(P2(wh, Req(HeavyOp.Lease, hash, 0, ns)) == ProposalCode.Accepted, "P2 lease");
        ushort l = Rec(wh, hash, ns).Lease;
        Expect(P2(wh, Req(HeavyOp.Drop, hash, l, ns)) == ProposalCode.Accepted && P2(wh, Req(HeavyOp.Settle, hash, l, ns, 0.5f)) == ProposalCode.Accepted, "P2 drop + settle");
        return l;
    }
    static List<CargoEntry> Slots(int n)
    {
        var items = new int[30]; var pos = new Vec3[30]; var eul = new Vec3[30];
        for (int i = 0; i < 30; i++) { items[i] = i < n ? 100 + i : -1; pos[i] = new Vec3(0.1f * i, 0.8f, -1.5f); eul[i] = new Vec3(0, 10 * i, 0); }
        return HeavyCargo.FromSaveSlots(items, pos, eul, e => Quat16.From(0, 0, 0, 1));
    }

    // ------------------------------------------------------------------------------------------------ 01
    static void Codec()
    {
        var l = new List<CargoEntry>();
        for (int i = 0; i < HeavyCargo.MaxEntries; i++) l.Add(new CargoEntry { Id = (ushort)(i + 1), ItemId = (ushort)(20 + i), Pos = new Vec3(0.1f * i, 0.9f, -1.5f), Rot = Quat16.From(0, 1, 0, 1) });
        var v = HeavyCargo.Encode(7, l);
        Expect(v.Length == 4 + 32 * CargoEntry.WireSize && v.Length <= StateEntry.MaxValueBytes, "32 entries fit one registry value (" + v.Length + " B)");
        List<CargoEntry> back; Expect(HeavyCargo.TryParse(v, out back) && back.Count == 32, "round trip");
        for (int i = 0; i < 32; i++) Expect(back[i].Id == l[i].Id && back[i].ItemId == l[i].ItemId && back[i].Pos.X == l[i].Pos.X && back[i].Rot.Y == l[i].Rot.Y, "entry " + i);
        l.Add(new CargoEntry { Id = 99, ItemId = 5 }); Expect(!HeavyCargo.TryParse(HeavyCargo.Encode(1, l), out back), "33 entries refused");
        var dup = new List<CargoEntry> { new CargoEntry { Id = 3, ItemId = 5 }, new CargoEntry { Id = 3, ItemId = 6 } };
        Expect(!HeavyCargo.TryParse(HeavyCargo.Encode(1, dup), out back), "duplicate cargo id refused");
        Expect(!HeavyCargo.TryParse(HeavyCargo.Encode(1, new List<CargoEntry> { new CargoEntry { Id = 0, ItemId = 5 } }), out back), "id 0 refused");
        Expect(!HeavyCargo.TryParse(HeavyCargo.Encode(1, new List<CargoEntry> { new CargoEntry { Id = 1, ItemId = 0 } }), out back), "item 0 refused");
        Expect(!HeavyCargo.TryParse(HeavyCargo.Encode(1, new List<CargoEntry> { new CargoEntry { Id = 1, ItemId = 5, Pos = new Vec3(50, 0, 0) } }), out back), "pose outside any car refused");
        Expect(!HeavyCargo.TryParse(HeavyCargo.Encode(1, new List<CargoEntry> { new CargoEntry { Id = 1, ItemId = 5, Pos = new Vec3(float.NaN, 0, 0) } }), out back), "non-finite pose refused");
        var bad = (byte[])v.Clone(); bad[0] = 9; Expect(!HeavyCargo.TryParse(bad, out back), "version");
        Expect(!HeavyCargo.TryParse(new byte[] { 1, 1, 0, 0 }, out back), "truncated");
        Expect(HeavyCargo.TryParse(HeavyCargo.Encode(1, new List<CargoEntry>()), out back) && back.Count == 0, "empty car");
        // synthetic ids
        uint s = HeavyCargo.Synthetic(388, 513);
        Expect(HeavyCargo.IsSynthetic(s) && HeavyCargo.IdOf(s) == 513 && HeavyCargo.ItemOf(s) == 388, "synthetic id carries cargo id + item id");
        Expect(!HeavyCargo.IsSynthetic(HeavyCargo.Synthetic(388, 0)) && !HeavyCargo.IsSynthetic(TV & 0x7FFFFFFF), "id 0 / census-range values are not synthetic");
        // explicit kind flags (census hashes use all 32 bits: TV = 0xA1B2C3D4 has bit 31 set too)
        Expect(HeavyCargo.IsSynthetic(TV), "a census hash can look like a synthetic id - the kind is never derived from the value");
        var cr = new HeavyRecord { Hash = s, State = HeavyState.Carried, Actor = 2, Lease = 3, Rot = Quat16.From(0, 0, 0, 1), Cargo = true };
        var rl = new List<HeavyRecord> { cr, new HeavyRecord { Hash = TV, State = HeavyState.Placed, Actor = 0, Lease = 1, HasPose = true, Rot = Quat16.From(0, 0, 0, 1) } };
        List<HeavyRecord> rb; Expect(HeavyList.TryParse(HeavyList.Encode(rl), out rb) && rb[0].Cargo && !rb[1].Cargo, "record cargo flag round trip (list v3, size unchanged)");
        cr.State = HeavyState.Removed; cr.Actor = 0; rl[0] = cr;
        Expect(!HeavyList.TryParse(HeavyList.Encode(rl), out rb), "a Removed cargo record is invalid (stowed cargo objects leave the list)");
        var ct = new HeavyTxnRequest { Op = HeavyOp.Lease, Ns = Madison, Hash = s, Rot = Quat16.From(0, 0, 0, 1), Cargo = true }; HeavyTxnRequest ct2;
        Expect(HeavyTxnRequest.TryParse(ct.Encode(), out ct2) && ct2.Cargo && ct2.Op == HeavyOp.Lease && ct2.Hash == s, "request cargo flag (op byte bit 0x80) round trip");
        ct.Hash = HeavyCargo.Synthetic(5, 0); Expect(!HeavyTxnRequest.TryParse(ct.Encode(), out ct2), "cargo flag with an invalid synthetic id refused");
        var plain = Req(HeavyOp.Lease, TV, 0); Expect(HeavyTxnRequest.TryParse(plain.Encode(), out ct2) && !ct2.Cargo, "census request without the flag");
        // Load request: vehicle in Digest, car-local pose
        var t = LoadReq(TV, 5); HeavyTxnRequest t2;
        Expect(HeavyTxnRequest.TryParse(t.Encode(), out t2) && t2.Op == HeavyOp.Load && t2.Digest == Ranger && t2.Lease == 5 && t2.Pos.Z == -1.6f, "Load request round trip (35 B, op 6)");
        // original LoadItems order: stops at the first slot <= 0
        var items = new[] { 28, 84, -1, 36 }; var pos = new[] { new Vec3(0, 1, -1), new Vec3(0.3f, 1, -1), new Vec3(), new Vec3() }; var eul = new Vec3[4];
        var from = HeavyCargo.FromSaveSlots(items, pos, eul, e => Quat16.From(0, 0, 0, 1));
        Expect(from.Count == 2 && from[0].ItemId == 28 && from[1].ItemId == 84, "save slots read like the original LoadItems (break at the first empty slot)");
        Expect(HeavyCargo.Capacity == 24 && HeavyCargo.SaveSlots == 30, "VanHeavyItems.SaveItems: 30 slots cleared, 24 written");
    }

    // ------------------------------------------------------------------------------------------------ 02
    static void P1Loading()
    {
        var wh = Host(Madison, Madison);
        ushort l = P1Rest(wh, TV);
        wh.Outbox.Clear();
        string why; ushort lo;
        var code = wh.HeavyTransaction(1, LoadReq(TV, l), 1, out lo, out why);
        Expect(code == ProposalCode.Accepted, "P1 Load accepted: " + why);
        var r = Rec(wh, TV);
        Expect(r.State == HeavyState.Removed && r.Actor == 0 && !r.HasPose, "census record Removed once (hidden on both instances, never a second copy)");
        var c = Cargo(wh);
        Expect(c.Count == 1 && c[0].ItemId == TVItem && c[0].Id != 0 && Math.Abs(c[0].Pos.Z + 1.6f) < 1e-4, "one entry: catalog item id + committed car-local pose");
        bool heavy = false, cargo = false;
        foreach (var e in wh.Outbox) { if (e.Key == WorldKeys.Heavy) heavy = true; if (e.Key == WorldKeys.Cargo && e.ScopeArg == Ranger) cargo = true; }
        Expect(heavy && cargo, "one host step commits both registry entries (wheavy + hcargo) for the client");
        Expect(wh.Heavy.Stowed == 1 && wh.Cargo.Loads == 1, "counted once");
        Expect(wh.HeavyTransaction(1, LoadReq(TV, l), 1, out lo, out why) == ProposalCode.Stale && Cargo(wh).Count == 1, "a repeated Load of the same object is stale (exactly once)");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "the stowed census object cannot be picked by Player 2 (in the car now)");
    }

    // ------------------------------------------------------------------------------------------------ 03
    static void P2Loading()
    {
        var wh = Host(Madison, Madison);
        ushort l = P2Rest(wh, Printer);
        Expect(P2(wh, LoadReq(Printer, l)) == ProposalCode.Accepted, "P2 Load proposal accepted");
        Expect(Rec(wh, Printer).State == HeavyState.Removed, "record Removed");
        var c = Cargo(wh);
        Expect(c.Count == 1 && c[0].ItemId == PrinterItem, "entry committed by the host (item id from the host catalog, not from the client)");
        // the client applies the very same registry entry (it never builds its own list)
        var client = StateRegistry.CreateHost(1);
        var e = wh.Reg.Get(StateScope.Session, Ranger, WorldKeys.Cargo);
        client.Commit(StateScope.Session, Ranger, WorldKeys.Cargo, e.Value);
        List<CargoEntry> cl; Expect(HeavyCargo.TryParse(client.Get(StateScope.Session, Ranger, WorldKeys.Cargo).Value, out cl) && cl.Count == 1 && cl[0].Id == c[0].Id, "both instances read one committed list");
        // P1 loads a second object: P2 sees it in the same list
        ushort l1 = P1Rest(wh, TV);
        Expect(P1(wh, LoadReq(TV, l1, 0, Madison, -0.3f)) == ProposalCode.Accepted && Cargo(wh).Count == 2, "host load joins the same list");
        Expect(Cargo(wh)[0].Id != Cargo(wh)[1].Id, "unique cargo ids");
    }

    // ------------------------------------------------------------------------------------------------ 04
    static void Duplicates()
    {
        var wh = Host(Madison, Madison);
        ushort l = P2Rest(wh, Printer);
        uint id = ++_pid;
        Expect(P2(wh, LoadReq(Printer, l), id) == ProposalCode.Accepted, "first");
        Expect(P2(wh, LoadReq(Printer, l), id) == ProposalCode.Accepted && Cargo(wh).Count == 1, "retransmission (same proposal id) answered from the gate cache, never applied twice");
        Expect(P2(wh, LoadReq(Printer, l)) == ProposalCode.Stale && Cargo(wh).Count == 1, "a new Load of the stowed object is stale");
        // foreign lease: P1 carries TV, P2 tries to load it
        ushort lt; Expect(P1(wh, Req(HeavyOp.Lease, TV, 0), out lt) == ProposalCode.Accepted, "P1 carries TV");
        Expect(P2(wh, LoadReq(TV, lt)) == ProposalCode.Stale && Rec(wh, TV).CarriedBy(1), "Player 2 cannot load an object Player 1 holds (lease belongs to P1)");
        // two objects loaded in the same host frame by both players
        Expect(P1(wh, Req(HeavyOp.Drop, TV, lt)) == ProposalCode.Accepted, "P1 drop");
        ushort lc = P2RestClock(wh);
        Expect(P1(wh, LoadReq(TV, lt)) == ProposalCode.Accepted && P2(wh, LoadReq(Clock, lc, 0, Madison, 0.6f)) == ProposalCode.Accepted, "simultaneous loads of two objects both accepted");
        Expect(Cargo(wh).Count == 3, "three entries, no duplicate");
        var ids = new HashSet<ushort>(); foreach (var e in Cargo(wh)) Expect(ids.Add(e.Id), "ids unique");
    }
    static ushort P2RestClock(WorldHost wh) { return P2Rest(wh, Clock); }

    // ------------------------------------------------------------------------------------------------ 05
    static void Invalid()
    {
        var wh = Host(Madison, Madison);
        ushort l = P2Rest(wh, Printer);
        Expect(P2(wh, LoadReq(Printer, (ushort)(l + 1))) == ProposalCode.Stale, "wrong lease");
        Expect(P2(wh, LoadReq(Printer, l, GT)) == ProposalCode.NotAuthorized, "the Shadowhawk has no cargo bed (unsupported vehicle never modified)");
        Expect(P2(wh, LoadReq(Printer, l, 77)) == ProposalCode.NotAuthorized, "unknown vehicle");
        var far = LoadReq(Printer, l); far.Pos = new Vec3(0, 0, 40);
        Expect(P2(wh, far) == ProposalCode.Invalid, "car-local pose outside any car");
        wh.Fleet.Place(Ranger, Hillville, wh.Outbox);
        Expect(P2(wh, LoadReq(Printer, l)) == ProposalCode.NotAuthorized, "car in another region");
        wh.Fleet.Place(Ranger, Madison, wh.Outbox);
        wh.HeavyItemId = (ns, h) => 0;
        Expect(P2(wh, LoadReq(Printer, l)) == ProposalCode.NotAuthorized, "item id unknown on the host");
        var r = Rec(wh, Printer);
        Expect(r.State == HeavyState.Placed && r.Actor == 2 && r.Lease == l && r.HasPose && Cargo(wh).Count == 0, "a refused Load changes nothing: the object keeps its committed rest pose and lease");
        Expect(wh.Cargo.Refused >= 5, "refusals counted");
        // a client can never use Remove, and Load does not reopen that door
        Expect(P2(wh, Req(HeavyOp.Remove, Printer, l)) == ProposalCode.NotAuthorized, "client Remove still refused");
    }

    // ------------------------------------------------------------------------------------------------ 06
    static void Capacity()
    {
        var wh = Host(Madison, Madison);
        Expect(wh.CargoSeed(Ranger, Slots(23)) && Cargo(wh).Count == 23, "23 saved objects");
        ushort l = P2Rest(wh, Printer);
        Expect(P2(wh, LoadReq(Printer, l)) == ProposalCode.Accepted && Cargo(wh).Count == 24, "24th accepted (original SaveItems keeps 24)");
        ushort l1 = P1Rest(wh, TV);
        Expect(P1(wh, LoadReq(TV, l1)) == ProposalCode.Busy && Cargo(wh).Count == 24, "25th refused: bed full");
        var r = Rec(wh, TV);
        Expect(r.State == HeavyState.Placed && r.Actor == 1 && r.HasPose, "refused object keeps its H1 rest state (never destroyed)");
        // taking one out frees a slot
        var first = Cargo(wh)[0];
        Expect(P1(wh, Pick(Syn(first))) == ProposalCode.Accepted && Cargo(wh).Count == 23, "unload frees a slot");
        Expect(P1(wh, LoadReq(TV, l1)) == ProposalCode.Accepted && Cargo(wh).Count == 24, "now the TV fits");
    }

    // ------------------------------------------------------------------------------------------------ 07
    static void Unload()
    {
        var wh = Host(Madison, Madison);
        ushort l = P1Rest(wh, TV);
        Expect(P1(wh, LoadReq(TV, l)) == ProposalCode.Accepted, "loaded");
        var e = Cargo(wh)[0]; uint syn = Syn(e);
        // both try to take it out: the host commits the first, the second is stale
        Expect(P2(wh, Pick(syn)) == ProposalCode.Accepted, "P2 takes it out");
        Expect(P1(wh, Pick(syn)) == ProposalCode.Stale, "P1's simultaneous pickup refused (carried by P2)");
        var r = Rec(wh, syn);
        Expect(r.CarriedBy(2) && !r.HasPose && Cargo(wh).Count == 0, "entry -> synthetic record carried by Player 2, no pose yet");
        Expect(P2(wh, Pick(syn)) == ProposalCode.Unchanged, "P2 retransmission Unchanged");
        // H1 rules for the synthetic object: drop / settle elsewhere
        Expect(P2(wh, Req(HeavyOp.Drop, syn, r.Lease, Madison, 3)) == ProposalCode.Accepted && P2(wh, Req(HeavyOp.Settle, syn, r.Lease, Madison, 3.2f)) == ProposalCode.Accepted, "drop + settle of the cargo object");
        Expect(Rec(wh, syn).State == HeavyState.Placed && Rec(wh, syn).HasPose, "now a world object at its rest pose (shown on both instances)");
        // P1 picks it from the ground (ordinary H1 lease on the synthetic id) and loads it again
        ushort l2; Expect(P1(wh, Pick(syn), out l2) == ProposalCode.Accepted && Rec(wh, syn).CarriedBy(1), "P1 leases the world object");
        Expect(P1(wh, Req(HeavyOp.Drop, syn, l2)) == ProposalCode.Accepted && P1(wh, LoadReq(syn, l2)) == ProposalCode.Accepted, "re-load");
        bool still; HeavyRecord dummy; still = TryRec(wh, syn, out dummy);
        Expect(!still && Cargo(wh).Count == 1 && Cargo(wh)[0].Id == e.Id && Cargo(wh)[0].ItemId == e.ItemId, "synthetic record deleted, same cargo object back in the car");
    }

    // ------------------------------------------------------------------------------------------------ 08
    static void Abandoned()
    {
        var wh = Host(Madison, Madison);
        Expect(wh.CargoSeed(Ranger, Slots(2)), "seed");
        var e0 = Cargo(wh)[0]; uint s0 = Syn(e0);
        Expect(P2(wh, Pick(s0)) == ProposalCode.Accepted && Cargo(wh).Count == 1, "P2 granted");
        // P2's grant turned out unusable (moved away): it releases -> back into the car, same id / pose
        Expect(P2(wh, Req(HeavyOp.Release, s0, Rec(wh, s0).Lease)) == ProposalCode.Accepted, "release");
        HeavyRecord r;
        Expect(!TryRec(wh, s0, out r) && Cargo(wh).Count == 2 && wh.Cargo.Restores == 1, "abandoned carry restored into the car (never lost)");
        bool found = false; foreach (var e in Cargo(wh)) if (e.Id == e0.Id && e.Pos.X == e0.Pos.X) found = true;
        Expect(found, "same cargo id and pose");
        // disconnect while carrying it out
        Expect(P2(wh, Pick(s0)) == ProposalCode.Accepted, "P2 takes it again");
        wh.SetPlayerRegion(2, Regions.None, 5);
        Expect(!TryRec(wh, s0, out r) && Cargo(wh).Count == 2, "disconnect while carrying: back in the car");
        // P1 carries it out, drops it on the ground, then leaves: it stays a world object (H1), not restored
        wh.SetPlayerRegion(2, Madison, 6);
        ushort l; Expect(P1(wh, Pick(s0), out l) == ProposalCode.Accepted, "P1 takes it");
        Expect(P1(wh, Req(HeavyOp.Drop, s0, l, Madison, 4)) == ProposalCode.Accepted && P1(wh, Req(HeavyOp.Settle, s0, l, Madison, 4)) == ProposalCode.Accepted, "drop + settle");
        Expect(P1(wh, Req(HeavyOp.Lease, s0, 0)) == ProposalCode.Accepted, "P1 picks it up again");
        wh.SetPlayerRegion(1, Hillville, 7);
        Expect(TryRec(wh, s0, out r) && r.State == HeavyState.Placed && r.HasPose && Cargo(wh).Count == 1, "after a drop the abandoned carry returns to its last rest pose (H1), not into the car");
    }

    // ------------------------------------------------------------------------------------------------ 09
    static void Travel_()
    {
        var wh = Host(Madison, Madison);
        ushort l = P1Rest(wh, TV);
        Expect(P1(wh, LoadReq(TV, l)) == ProposalCode.Accepted, "loaded in Madison");
        var e = Cargo(wh)[0];
        // the Ranger travels to Hillville with Player 1 (fleet decides the region; cargo is per car, Session scope)
        wh.SetPlayerRegion(1, Hillville, 2);
        wh.Fleet.Place(Ranger, Hillville, wh.Outbox);
        Expect(Cargo(wh).Count == 1 && Cargo(wh)[0].Id == e.Id, "cargo unchanged by travel");
        // Player 2 still in Madison: the car is not there -> no unload, no load
        uint syn = Syn(e);
        Expect(P2(wh, Pick(syn)) == ProposalCode.NotAuthorized, "unload refused where the car is not");
        ushort lp = P2Rest(wh, Printer);
        Expect(P2(wh, LoadReq(Printer, lp)) == ProposalCode.NotAuthorized, "load refused where the car is not");
        // Player 1 unloads in Hillville
        Expect(P1(wh, Pick(syn, Hillville)) == ProposalCode.Accepted && Rec(wh, syn, Hillville).CarriedBy(1), "unload in Hillville (record in Hillville's list)");
        Expect(P1(wh, Req(HeavyOp.Drop, syn, Rec(wh, syn, Hillville).Lease, Hillville, 9)) == ProposalCode.Accepted, "dropped on the Hillville ground");
        // everybody leaves Hillville: the region reset ends its world records (the original MainScene reload destroys runtime objects)
        wh.SetPlayerRegion(1, Madison, 3);
        HeavyRecord r;
        Expect(!TryRec(wh, syn, out r, Hillville) && Cargo(wh).Count == 0, "dropped cargo object gone with the emptied region (original: dropped heavy objects are not saved)");
        // Madison: Player 2's census Printer record unaffected, TV record still Removed (Player 2 stayed)
        Expect(Rec(wh, TV).State == HeavyState.Removed && Rec(wh, Printer).State == HeavyState.Placed, "Madison records unaffected");
    }

    // ------------------------------------------------------------------------------------------------ 10
    static void Persistence()
    {
        var wh = Host(Madison, Madison);
        var slots = Slots(5);
        Expect(wh.CargoSeed(Ranger, slots) && Cargo(wh).Count == 5, "seeded from the original keys");
        Expect(!wh.CargoSeed(Ranger, Slots(9)) && Cargo(wh).Count == 5 && wh.Cargo.Seeded(Ranger), "seeded once per host session (a later scene / reload never re-seeds)");
        var ids = new HashSet<ushort>(); foreach (var e in Cargo(wh)) Expect(ids.Add(e.Id) && e.Id != 0, "unique ids");
        // host restart: a new host from the same keys shows the same items / poses (ids are session-local)
        var wh2 = Host(Madison, Regions.None);
        Expect(wh2.CargoSeed(Ranger, slots), "restart seed");
        var a = Cargo(wh); var b = Cargo(wh2);
        Expect(a.Count == b.Count, "same count");
        for (int i = 0; i < a.Count; i++) Expect(a[i].ItemId == b[i].ItemId && a[i].Pos.X == b[i].Pos.X && a[i].Rot.W == b[i].Rot.W, "same item + pose " + i);
        // save isolation: the cargo keys are host storage, never copied into the client sandbox
        foreach (var k in new[] { "HeavyVan_0", "HeavyVan_29", "PosX_item3", "RotZ_item7", "TrunkItem" })
            Expect(CampaignPolicy.Classify(k) == CampaignKeyClass.HostStorage, k + " is host storage (not in the Player-2 snapshot)");
        var src = Src("Plugin", "HeavyCargoSync.cs");
        Expect(src.Contains("if (_host) _h.Patch(saveAll, null, new HarmonyMethod(AccessTools.Method(typeof(HeavyCargoSync), \"SaveAllPostfix\")));")
            && src.Contains("_h.Patch(save, new HarmonyMethod(AccessTools.Method(typeof(HeavyCargoSync), \"SavePrefix\")));"), "host writes the keys at its own save; the original sensor-only SaveItems never runs in a session (Player 2 never saves cargo)");
        var sap = Body(src, "static void SaveAllPostfix()");
        Expect(sap.IndexOf("i.Seed();", StringComparison.Ordinal) > 0 && sap.IndexOf("i.Seed();", StringComparison.Ordinal) < sap.IndexOf("i.WriteSave();", StringComparison.Ordinal), "a save before the first seed seeds from the intact keys first (never writes an empty list over saved cargo)");
        Expect(Body(src, "void WriteSave()").Contains("!_world.Host.Cargo.Seeded(_vid)) return;"), "no unseeded write");
        var w = Body(src, "void WriteSave()");
        Expect(w.Contains("\"HeavyVan_\" + i, -1") && w.Contains("if (n >= HeavyCargo.Capacity) break;") && w.Contains("_world.Host.Cargo.Of(_vid)"), "original key format: 30 slots cleared, committed list first, <= 24 written");
        var seed = Body(src, "void Seed()");
        Expect(seed.Contains("if (items[i] <= 0) break;") && seed.Contains("_world.Host.CargoSeed(_vid, l)"), "seed reads the keys like the original LoadItems");
    }

    // ------------------------------------------------------------------------------------------------ 11
    static void OneTime()
    {
        var wh = Host(Madison, Madison);
        ushort l = P2Rest(wh, Printer);
        wh.Outbox.Clear();
        Expect(P2(wh, LoadReq(Printer, l)) == ProposalCode.Accepted, "load");
        foreach (var e in wh.Outbox) Expect(e.Key == WorldKeys.Heavy || e.Key == WorldKeys.Cargo, "Load commits only wheavy + hcargo (found '" + e.Key + "')");
        Expect(wh.Reg.Get(StateScope.Session, 0, WorldKeys.Wallet) == null, "no wallet change from loading (original bed loading pays nothing)");
        var src = Src("Plugin", "HeavyCargoSync.cs");
        foreach (var bad in new[] { "stolenBigItemsNr", "StolenBigItems\"", "AdvanceStory", "currentXPPoints", "SetDayRespawn", "parcel_strings_to_set", "WasPlacedInCar", "SaveXPForItems" })
            Expect(!src.Contains(bad), "HeavyCargoSync never applies '" + bad + "' (no original reward / story effect exists for the cargo bed)");
        Expect(src.Contains("_setX10(pick, false); _setAnyXp(pick, false);"), "cargo copies carry no XP, like the original LoadItems copies");
        var h = Src("Plugin", "HeavySync.cs");
        Expect(!h.Contains("stolenBigItemsNr") && !h.Contains("AdvanceStory"), "HeavySync applies no story / counter either");
        // story hooks of the separate trunk slot stay with the original (Place_Heavy_Trigger is still guarded for Player 2)
        Expect(h.Contains("foreach (var tn in new[] { \"Place_Heavy_Trigger\", \"Shop_Heavy_Trigger\" })") && h.Contains("\"DepositPrefix\""), "trunk slot / heavy shop stay blocked for Player 2 (not part of H2)");
    }

    // ------------------------------------------------------------------------------------------------ 12
    static void Reconnect()
    {
        var wh = Host(Madison, Madison);
        ushort l = P2Rest(wh, Printer);
        Expect(P2(wh, LoadReq(Printer, l)) == ProposalCode.Accepted, "P2 loaded");
        wh.SetPlayerRegion(2, Regions.None, 3);       // disconnect
        Expect(Cargo(wh).Count == 1, "cargo kept while Player 2 is away (host-owned)");
        wh.SetPlayerRegion(2, Madison, 4);            // rejoin: the client receives the registry snapshot incl. "hcargo"
        var e = Cargo(wh)[0];
        Expect(wh.Reg.Get(StateScope.Session, Ranger, WorldKeys.Cargo) != null, "list in the session registry (part of every snapshot)");
        Expect(P2(wh, Pick(Syn(e))) == ProposalCode.Accepted, "rejoined Player 2 can take it out");
        Expect(P2(wh, Pick(Syn(e.ItemId + 1, e.Id))) == ProposalCode.Stale, "a synthetic id with the wrong item never matches an entry");
        // kind mismatches never touch the other object: a census request on a cargo record / a cargo request on a census record
        Expect(P1(wh, Pick(Syn(e))) == ProposalCode.Stale && Rec(wh, Syn(e)).CarriedBy(2), "the carried cargo object stays with Player 2");
        var censusAsCargo = Req(HeavyOp.Lease, TV, 0); censusAsCargo.Cargo = true;
        ushort lt; Expect(P1(wh, Req(HeavyOp.Lease, TV, 0), out lt) == ProposalCode.Accepted, "P1 leases the census TV");
        Expect(P2(wh, censusAsCargo) == ProposalCode.Stale && Rec(wh, TV).CarriedBy(1), "a cargo-flagged request never takes a census record");
        // a synthetic id equal to a census hash of the region is never taken out (presentation would be ambiguous)
        var wh2 = Host(Madison, Madison);
        Expect(wh2.CargoSeed(Ranger, Slots(1)), "seed");
        var e2 = Cargo(wh2)[0]; uint s2 = Syn(e2);
        wh2.HeavyItemId = (ns, h) => h == s2 ? 7 : 0;
        Expect(P1(wh2, Pick(s2)) == ProposalCode.Stale && Cargo(wh2).Count == 1, "id collision with a census object refused, object stays in the car");
    }

    // ------------------------------------------------------------------------------------------------ 13
    static void H1Regression()
    {
        var wh = Host(Madison, Madison);
        wh.CargoVehicles.Clear();
        ushort l; Expect(P1(wh, Req(HeavyOp.Lease, TV, 0), out l) == ProposalCode.Accepted, "lease");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "second holder refused");
        Expect(P1(wh, Req(HeavyOp.Drop, TV, l)) == ProposalCode.Accepted && Rec(wh, TV).Moving, "Drop = Placed + Moving");
        Expect(P1(wh, Req(HeavyOp.Settle, TV, l, Madison, 1)) == ProposalCode.Accepted && !Rec(wh, TV).Moving, "Settle");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Accepted && Rec(wh, TV).CarriedBy(2), "P2 leases the placed object");
        ushort l2 = Rec(wh, TV).Lease;
        Expect(P2(wh, Req(HeavyOp.Drop, TV, l2)) == ProposalCode.Accepted, "P2 drop (moving)");
        wh.SetPlayerRegion(2, Regions.None, 5);
        Expect(Rec(wh, TV).Actor == 1 && wh.Heavy.Handovers == 1, "handover to P1 while moving");
        Expect(P1(wh, LoadReq(TV, Rec(wh, TV).Lease)) == ProposalCode.NotAuthorized && Rec(wh, TV).State == HeavyState.Placed, "without a cargo car nothing can be loaded");
        // H1 host-local deposit (trunk slot / shop): Removed stays unpickable for Player 2
        wh.SetPlayerRegion(2, Madison, 6);
        Expect(P1(wh, Req(HeavyOp.Remove, TV, Rec(wh, TV).Lease)) == ProposalCode.Accepted, "host Remove");
        Expect(P2(wh, Req(HeavyOp.Lease, TV, 0)) == ProposalCode.Stale, "Removed: Player 2 refused");
        Expect(wh.Cargo.Loads == 0 && Cargo(wh).Count == 0, "no cargo side effect");
    }

    // ------------------------------------------------------------------------------------------------ 14
    static void SourceScan()
    {
        var c = Src("Plugin", "HeavyCargoSync.cs");
        Expect(c.Contains("static bool LoadPrefix(object __instance, ref IEnumerator __result)") && c.Contains("__result = Empty();"), "original LoadItems coroutine replaced by an empty one during a session (no null coroutine)");
        Expect(Body(c, "void PlaceInCar(CObj o, Vector3 lp, Quaternion lq, bool force)").Contains("_placedInTrunk(o.Pick);") && Body(c, "void PlaceInCar(CObj o, Vector3 lp, Quaternion lq, bool force)").Contains("o.Rb.isKinematic = true;"), "presentation = original LoadItems saved-cargo state (kinematic + PlacedInTrunk)");
        Expect(Body(c, "CObj Ensure(ushort id, ushort itemId, Vector3 lp, Quaternion lq, bool inCar)").Contains("DropObject(itemId)"), "objects come from the item's original drop_object");
        Expect(Count(c, "FindObjectOfType(") == 1 && Body(c, "void Bind()").Contains("FindObjectOfType("), "the only scene search is once per scene instance (Bind)");
        Expect(!Body(c, "public void Update()").Contains("GetComponentsInChildren") && !Body(c, "public void Update()").Contains("Instantiate("), "no per-frame scene work");
        foreach (var bad in new[] { "OnGUI", "GUI.", "Time.timeScale =", "SetActive(false)" })
            Expect(!c.Contains(bad), "HeavyCargoSync contains no '" + bad + "' (no UI, never pauses, never hides the car)");
        Expect(c.Contains("\"UnblockPostfix\"") && Body(c, "static void UnblockPostfix(object __instance)").Contains("PlaceInCar(o, o.LocalPos, o.LocalRot, true)"), "committed cargo stays at its committed bed pose after the original UnblockHeavy");
        var h = Src("Plugin", "HeavySync.cs");
        var sim = Body(h, "void SimFrame(double now, int region)");
        int settle = sim.IndexOf("SendTxn(HeavyOp.Settle, s.Ns, s.Hash, s.Lease, t.position, t.rotation, first ?", StringComparison.Ordinal), load = sim.IndexOf("if (bed) TryLoad(s, t);", StringComparison.Ordinal);
        Expect(settle > 0 && load > settle, "the rest pose is committed first, then the Load is requested");
        Expect(Body(h, "void TryLoad(Sim s, Transform t)").Contains("s.LoadTried = true;") && Body(h, "void StartMoving(Sim s, double now, string why)").Contains("s.LoadTried = false;"), "one Load per rest");
        Expect(Body(h, "static bool StopPrefix(object __instance)").Contains("HeavyCargoSync.Instance.IsCargoCar(car)) return true;"), "Player 2's drop next to the Ranger is the original drop; other cars stay refused");
        Expect(Body(h, "static void TrunkPostfix(object __instance)").Contains("HeavyCargoSync.Applying"), "our own PlacedInTrunk is not treated as a deposit");
        Expect(Body(h, "Obj Get(LootCatalog cat, uint hash, bool cargo)").Contains("if (cargo)"), "cargo records resolve to the cargo object, never to a census object");
        Expect(Body(h, "void SendTxn(HeavyOp op, int ns, uint hash, ushort lease, Vector3 p, Quaternion q, uint digest, bool cargo, string why)").Contains("Cargo = cargo"), "requests carry the explicit cargo flag");
        Expect(Count(h, "_pickUpHeavy(") == 1 && Count(h, "_stopCarry(") == 1, "original carry calls unchanged (H1)");
        foreach (var bad in new[] { "WaitForSeconds", "StartCoroutine", "Instantiate(", "AddComponent", "OnGUI", "Time.timeScale =" })
            Expect(!h.Contains(bad), "HeavySync contains no '" + bad + "'");
        var p = Src("Plugin", "Plugin.cs");
        Expect(p.Contains("_cargo = new HeavyCargoSync(log, _world, node.Role == Role.Host)") && p.Contains("Release(ref _cargo, \"CARGO\"); Release(ref _heavy, \"HEAVY\");"), "built after HeavySync, disposed before it");
        var t = Src("Shared", "WorldTime.cs");
        Expect(t.Contains("t.Op == HeavyOp.Remove) { code = ProposalCode.NotAuthorized;"), "client Remove still refused");
        var cl = Body(t, "ProposalCode CargoLoad(byte slot, int region, HeavyTxnRequest t, out string why)");
        Expect(cl.IndexOf("Heavy.Stow(", StringComparison.Ordinal) > cl.IndexOf("Cargo.Full(car)", StringComparison.Ordinal) && cl.IndexOf("Heavy.Stow(", StringComparison.Ordinal) > cl.IndexOf("Heavy.Holds(", StringComparison.Ordinal), "every check before any mutation (atomic host step)");
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
        throw new Exception("source not found: " + string.Join("/", p));
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
