// W6B.2 offline tests: mission-box unpacking exactly once (two-PC report 0.27.1: P2 placed the story box on the table, P1
// opened it, the box stayed closed on P2, P2 opened it again -> duplicated contents). Real Shared code: WorldHost.HeavyTransaction
// (HeavyOp.Unpack) on HeavyBook + H2 cargo, UnpackRules; the host plugin (IUnpackHost) and the ORIGINAL CardboardMinigame are
// modelled from the IL (SkipMinigameWin: SpawnItem, fade, cut sound, Destroy(current_box); SpawnItemOnStart from ItemCardboard).
// Unity glue is source-scanned. In-game: docs/TESTING.md section 56.
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class W6B2Tests
{
    static int _passed, _failed;
    const int Hideout = 0, Madison = 99;
    const int BoxItem = 90, Content = 91;   // ITEM IDs/90_cardboard_box -> cardboard_spawn_item_ID 91_black_package
    const uint MadisonBox = 0xFB1998DA;     // MadisonSt:FB1998DA CardboardBox_FlybyLiteDrone (real log id)
    static readonly ushort Ranger = VehicleIds.FromCarNr(0);
    static uint _pid = 5000;

    static int Main()
    {
        Run("01 P1 opens first: one commit, host original creates the contents once, P2's copy disappears, P2's open is Stale (no contents)", P1First);
        Run("02 P2 opens first: host original SpawnItem on the host's table copy once; P1's own open afterwards Stale", P2First);
        Run("03 simultaneous opens in both orders: exactly one commit, one contents", Simultaneous);
        Run("04 retransmitted / replayed / undo attempts: cached answer, Stale replay, Unsell refused (contents never duplicated)", Replay);
        Run("05 disconnect / rejoin / save-reload: consumed box stays consumed, P2 never re-creates contents from ItemCardboard", Reload);
        Run("06 validation failures create nothing: box not on the host's table, host elsewhere, box in the car, unknown id, wrong region", Validation);
        Run("07 identity chain Madison census box -> Ranger cargo item 90 -> hideout cargo object -> one host-owned package; other heavy objects untouched", Chain);
        Run("08 gate rules: vanilla without session, untracked boxes (host local, P2 fail closed), pending / offline P2 refused", Gates);
        Run("09 Unity glue: hooks on SkipMinigameWin / MinigameWon / SpawnItem / SpawnItemOnStart, host runs the original SpawnItem, P2 feedback only, logs", Glue);
        Run("10 wire + regressions: op 9 Unpack round trip, op 10 refused, H3 sale + undo unchanged; protocol 39 / mod 0.28.0", Wire);
        Console.WriteLine(_failed == 0 ? "W6B2 TESTS PASSED (" + _passed + ")" : "W6B2 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- host plugin model (HeavyUnpackSync IUnpackHost)
    sealed class HostPc : IUnpackHost
    {
        public bool InRegion = true; public uint OnTable; public bool OnTableCargo = true; public int HostSpawns, Fades;
        public readonly List<string> Log = new List<string>();
        public bool CanUnpack(int ns, uint hash, bool cargo, out string why)
        {
            why = null;
            if (!InRegion) { why = "host is not in " + ns; return false; }
            if (OnTable != hash || OnTableCargo != cargo) { why = "no / another box on the host's table"; return false; }
            return true;
        }
        public void RunUnpack(int ns, uint hash, bool cargo) { HostSpawns++; OnTable = 0; Log.Add("host SpawnItem " + Content); }   // original SpawnItem + Destroy(box), no fade
        // Player 1's own open: the prefix commits, then the ORIGINAL SkipMinigameWin (SpawnItem + fade + sound + destroy)
        public ProposalCode OpenOwn(WorldHost wh, uint hash, bool cargo, ushort lease, double now)
        {
            string why; ushort l;
            var code = wh.HeavyTransaction(1, Unpack(hash, lease, Hideout, cargo), now, out l, out why);
            if (code == ProposalCode.Accepted) { HostSpawns++; Fades++; OnTable = 0; }
            return code;
        }
    }

    static WorldHost Host(out HostPc hp, int p1 = Hideout, int p2 = Hideout)
    {
        var wh = new WorldHost(11) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, p1, 0); wh.SetPlayerRegion(2, p2, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox); wh.Fleet.Place(Ranger, Hideout, wh.Outbox); wh.CargoVehicles.Add(Ranger);
        wh.HeavyCatalog = (int ns, uint hash, uint digest, out string why) => { why = null; return hash == MadisonBox ? ProposalCode.Accepted : ProposalCode.NotAuthorized; };
        wh.HeavyItemId = (ns, hash) => hash == MadisonBox ? BoxItem : 0;
        hp = new HostPc(); wh.UnpackHost = hp;
        wh.Outbox.Clear();
        return wh;
    }
    static HeavyTxnRequest Req(HeavyOp op, uint hash, ushort lease, int ns, bool cargo, float x = 0)
    { return new HeavyTxnRequest { Op = op, Ns = ns, Hash = hash, Digest = 0, Lease = lease, Pos = new Vec3(x, 1, 2), Rot = Quat16.From(0, 0, 0, 1), Cargo = cargo }; }
    static HeavyTxnRequest Unpack(uint hash, ushort lease, int ns, bool cargo) { return Req(HeavyOp.Unpack, hash, lease, ns, cargo); }
    static ProposalCode P2(WorldHost wh, HeavyTxnRequest t, double now = 1, uint id = 0)
    { uint rev; int sr; float sh; return wh.HandleProposal("cli", 2, id != 0 ? id : ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = t.Encode() }, now, out rev, out sr, out sh); }
    static ProposalCode P1(WorldHost wh, HeavyTxnRequest t, out ushort lease, double now = 1) { string why; return wh.HeavyTransaction(1, t, now, out lease, out why); }
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
    // P2 takes the box out of the Ranger in the hideout and puts it on the mission table (real-log flow).
    static uint ToTable(WorldHost wh, HostPc hp, byte slot = 2)
    {
        Expect(wh.CargoSeed(Ranger, Slots(BoxItem)), "Ranger carries the box (cargo #1 item 90)");
        var e = CargoList(wh)[0]; uint syn = HeavyCargo.Synthetic(e.ItemId, e.Id);
        ushort l;
        if (slot == 1) Expect(P1(wh, Req(HeavyOp.Lease, syn, 0, Hideout, true), out l) == ProposalCode.Accepted, "P1 takes the box out");
        else { Expect(P2(wh, Req(HeavyOp.Lease, syn, 0, Hideout, true)) == ProposalCode.Accepted, "P2 takes the box out"); l = Rec(wh, syn, Hideout).Lease; }
        var d = Req(HeavyOp.Drop, syn, l, Hideout, true, 3); var s = Req(HeavyOp.Settle, syn, l, Hideout, true, 3.2f);
        if (slot == 1) Expect(P1(wh, d, out l) == ProposalCode.Accepted && P1(wh, s, out l) == ProposalCode.Accepted, "P1 places it on the table");
        else Expect(P2(wh, d) == ProposalCode.Accepted && P2(wh, s) == ProposalCode.Accepted, "P2 places it on the table");
        hp.OnTable = syn;   // host's PlaceCardboard sensor sees its copy at the shared rest pose
        return syn;
    }

    // ---------------------------------------------------------------- 01..05
    static void P1First()
    {
        HostPc hp; var wh = Host(out hp); uint box = ToTable(wh, hp); ushort seen = Rec(wh, box, Hideout).Lease;
        Expect(hp.OpenOwn(wh, box, true, seen, 2) == ProposalCode.Accepted && hp.HostSpawns == 1, "P1 opens: contents once (original)");
        Expect(!Has(wh, box, Hideout), "box record consumed -> P2's copy disappears (opened state)");
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 3) == ProposalCode.Stale && hp.HostSpawns == 1, "P2 cannot reopen: Stale, no second package");
        Expect(wh.Unpacks == 1 && wh.UnpackRefusals >= 1, "counters");
    }

    static void P2First()
    {
        HostPc hp; var wh = Host(out hp); uint box = ToTable(wh, hp); ushort seen = Rec(wh, box, Hideout).Lease;
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 2) == ProposalCode.Accepted, "P2 opens: accepted");
        Expect(hp.HostSpawns == 1 && hp.Fades == 0 && hp.Log.Count == 1, "host original SpawnItem once, no fade on the host's screen");
        Expect(!Has(wh, box, Hideout), "box consumed");
        hp.OnTable = box;   // even if the host's sensor still held the destroyed copy for one frame
        Expect(hp.OpenOwn(wh, box, true, seen, 2.1) == ProposalCode.Stale && hp.HostSpawns == 1, "P1 cannot reopen");
    }

    static void Simultaneous()
    {
        HostPc hp; var wh = Host(out hp); uint box = ToTable(wh, hp); ushort seen = Rec(wh, box, Hideout).Lease;
        var a = hp.OpenOwn(wh, box, true, seen, 2); var b = P2(wh, Unpack(box, seen, Hideout, true), 2);
        Expect(a == ProposalCode.Accepted && b == ProposalCode.Stale && hp.HostSpawns == 1, "host first: one contents (" + a + "/" + b + ")");
        HostPc hp2; var w2 = Host(out hp2); uint box2 = ToTable(w2, hp2); ushort s2 = Rec(w2, box2, Hideout).Lease;
        var c = P2(w2, Unpack(box2, s2, Hideout, true), 2); var d = hp2.OpenOwn(w2, box2, true, s2, 2);
        Expect(c == ProposalCode.Accepted && d == ProposalCode.Stale && hp2.HostSpawns == 1, "P2 first: one contents (" + c + "/" + d + ")");
    }

    static void Replay()
    {
        HostPc hp; var wh = Host(out hp); uint box = ToTable(wh, hp); ushort seen = Rec(wh, box, Hideout).Lease;
        uint id = ++_pid;
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 2, id) == ProposalCode.Accepted, "first");
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 2.1, id) == ProposalCode.Accepted && hp.HostSpawns == 1, "retransmission (dropped answer): cached, contents once");
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 2.2) == ProposalCode.Stale && hp.HostSpawns == 1, "replay with a new id: Stale");
        Expect(P2(wh, Req(HeavyOp.Unsell, box, seen, Hideout, true), 2.3) == ProposalCode.Stale && !Has(wh, box, Hideout), "Unsell can never bring an unpacked box back");
        Expect(wh.Heavy.PendingSales == 0, "an unpack leaves no undo right");
    }

    static void Reload()
    {
        HostPc hp; var wh = Host(out hp); uint box = ToTable(wh, hp); ushort seen = Rec(wh, box, Hideout).Lease;
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 2) == ProposalCode.Accepted, "opened");
        wh.SetPlayerRegion(2, Regions.None, 3); wh.SetPlayerRegion(2, Hideout, 4);   // disconnect + rejoin (registry snapshot)
        Expect(!Has(wh, box, Hideout) && P2(wh, Unpack(box, seen, Hideout, true), 4.1) == ProposalCode.Stale && hp.HostSpawns == 1, "rejoined P2: no box, replay Stale");
        Expect(CargoList(wh).Count == 0, "the cargo list never gets the box back");
        // original persistence: TrySaveItem (host save) -> ItemCardboard = 91 while the package lies there; World key mirrored to P2
        Expect(UnpackRules.ClientMaySpawn(true, true, false), "host: SpawnItemOnStart re-creates its package after a reload (original)");
        Expect(!UnpackRules.ClientMaySpawn(true, false, false), "P2 load with the mirrored ItemCardboard = 91: no second package");
        Expect(UnpackRules.ClientMaySpawn(false, false, false), "single player unchanged");
        // both leave the hideout: region reset (cargo object records belong to the session cargo list, the box is gone for good)
        wh.SetPlayerRegion(1, Madison, 5); wh.SetPlayerRegion(2, Madison, 5); wh.SetPlayerRegion(1, Hideout, 6);
        Expect(!Has(wh, box, Hideout) && CargoList(wh).Count == 0, "region reload never restores the consumed cargo box");
    }

    static void Validation()
    {
        HostPc hp; var wh = Host(out hp); uint box = ToTable(wh, hp); ushort seen = Rec(wh, box, Hideout).Lease;
        hp.OnTable = 0;
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 2) == ProposalCode.Busy && Has(wh, box, Hideout) && hp.HostSpawns == 0, "box not on the host's table: nothing committed, nothing created");
        hp.OnTable = box; hp.InRegion = false;
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 2.1) == ProposalCode.Busy && Has(wh, box, Hideout) && hp.HostSpawns == 0, "host in another region: refused (fail closed)");
        hp.InRegion = true;
        Expect(P2(wh, Unpack(box, (ushort)(seen + 7), Hideout, true), 2.2) == ProposalCode.Stale && hp.HostSpawns == 0, "stale view refused");
        Expect(P2(wh, Unpack(0xDEADBEEF, 0, Hideout, false), 2.3) != ProposalCode.Accepted && hp.HostSpawns == 0, "unknown id refused");
        Expect(P2(wh, Unpack(box, seen, Madison, true), 2.4) != ProposalCode.Accepted && hp.HostSpawns == 0, "wrong region refused");
        HostPc hn; var none = Host(out hn); uint nb = ToTable(none, hn); none.UnpackHost = null;
        Expect(P2(none, Unpack(nb, Rec(none, nb, Hideout).Lease, Hideout, true), 1) == ProposalCode.NotAuthorized && Has(none, nb, Hideout), "no unpack host plugin on the host: refused, box kept");
        // box still in the car
        HostPc hp3; var w3 = Host(out hp3); Expect(w3.CargoSeed(Ranger, Slots(BoxItem)), "seed");
        var e = CargoList(w3)[0]; uint syn = HeavyCargo.Synthetic(e.ItemId, e.Id); hp3.OnTable = syn;
        Expect(P2(w3, Unpack(syn, 0, Hideout, true), 1) == ProposalCode.Stale && hp3.HostSpawns == 0 && CargoList(w3).Count == 1, "box inside the Ranger: refused, cargo untouched");
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 3) == ProposalCode.Accepted && hp.HostSpawns == 1, "valid after all: exactly one");
    }

    static void Chain()
    {
        HostPc hp; var wh = Host(out hp, Madison, Madison);
        Expect(P2(wh, Req(HeavyOp.Lease, MadisonBox, 0, Madison, false)) == ProposalCode.Accepted, "P2 picks up MadisonSt:FB1998DA (census, host catalog)");
        Expect(Rec(wh, MadisonBox, Madison).State == HeavyState.Carried, "carried by P2");
        // loading into the Ranger and travelling are H2 (tests H2Tests); the hideout object is the cargo synthetic of item 90
        Expect(HeavyCargo.IsSynthetic(HeavyCargo.Synthetic(BoxItem, 1)) && HeavyCargo.Synthetic(BoxItem, 1) == 0x805A0001u, "hideout id = cargo synthetic 805A0001 (item 90, cargo #1) as in the real log");
        wh.SetPlayerRegion(1, Hideout, 5); wh.SetPlayerRegion(2, Hideout, 5);
        uint box = ToTable(wh, hp); ushort seen = Rec(wh, box, Hideout).Lease;
        Expect(P2(wh, Unpack(box, seen, Hideout, true), 6) == ProposalCode.Accepted && hp.HostSpawns == 1, "opened once at the end of the chain");
        Expect(hp.Log[0] == "host SpawnItem " + Content, "contents = original item 91 drop object, host-owned (story item: never a shared drop)");
        Expect(box != MadisonBox && wh.Unpacks == 1, "the census id and the cargo id are different records; only the table box was consumed");
    }

    static void Gates()
    {
        Expect(UnpackRules.Gate(false, false, true, true, false) == UnpackGate.RunOriginal, "no session: vanilla");
        Expect(UnpackRules.Gate(true, true, false, true, false) == UnpackGate.RunOriginal, "host, untracked box: original local");
        Expect(UnpackRules.Gate(true, false, false, true, false) == UnpackGate.Refuse, "P2, untracked box: refused (no unvalidated contents)");
        Expect(UnpackRules.Gate(true, true, true, true, false) == UnpackGate.CommitThenRun, "host: commit then original");
        Expect(UnpackRules.Gate(true, false, true, true, false) == UnpackGate.Propose, "P2: propose");
        Expect(UnpackRules.Gate(true, false, true, false, false) == UnpackGate.Refuse && UnpackRules.Gate(true, false, true, true, true) == UnpackGate.Refuse, "P2 offline / pending: refused");
    }

    static void Glue()
    {
        var u = Src("Plugin", "HeavyUnpackSync.cs"); var hs = Src("Plugin", "HeavySync.cs");
        Expect(u.Contains("AccessTools.Method(_mgT, \"SkipMinigameWin\", Type.EmptyTypes)") && u.Contains("AccessTools.Method(_mgT, \"MinigameWon\", Type.EmptyTypes)") && u.Contains("_h.Patch(skip, pre); _h.Patch(won, pre);"), "both original open paths gated");
        Expect(u.Contains("_h.Patch(spawn, sp); _h.Patch(spawnStart, sp);") && u.Contains("UnpackRules.ClientMaySpawn(true, hs._host, _resumeUnpack)"), "P2 never runs SpawnItem / SpawnItemOnStart");
        int r = u.IndexOf("public void RunUnpack(", StringComparison.Ordinal);
        string run = u.Substring(r, u.IndexOf("string UnpackStats", r, StringComparison.Ordinal) - r);
        Expect(run.Contains("_setMgBox(mg, box);") && run.Contains("_mgSpawn(mg);") && run.Contains("UnityEngine.Object.Destroy(box);") && run.IndexOf("_psFade", StringComparison.Ordinal) < 0, "host runs the original SpawnItem on its own table copy, no fade");
        Expect(u.Contains("FindObjectOfType(_placeT)") && u.Contains("_placeObj(place) as Transform"), "location check: the host's PlaceCardboard.placedObj");
        Expect(u.Contains("if (fade != null) fade.Play();") && u.Contains("if (cut != null) cut.Play();"), "P2 feedback on acceptance");
        Expect(hs.Contains("InitUnpack();") && hs.Contains("if (OnUnpackResultHook(id, code)) return true;") && hs.Contains("UnpackFrame(now);") && hs.Contains("UnpackHold(r.Hash)"), "wired into HeavySync");
        Expect(u.Contains("HEAVY unpack of '") && u.Contains("requested (proposal ") && u.Contains("host original SpawnItem created the contents once") && u.Contains("granted (") && u.Contains("refused"), "event logs: request, host decision, contents, replica");
    }

    static void Wire()
    {
        var t = Unpack(0x805A0001, 3, Hideout, true); HeavyTxnRequest b;
        Expect(HeavyTxnRequest.TryParse(t.Encode(), out b) && b.Op == HeavyOp.Unpack && b.Cargo && b.Lease == 3, "op 9 round trip");
        var bad = t.Encode(); bad[0] = (byte)(10 | (bad[0] & 0x80)); Expect(!HeavyTxnRequest.TryParse(bad, out b), "op 10 refused");
        HostPc hp; var wh = Host(out hp); Expect(wh.CargoSeed(Ranger, Slots(28)), "TV cargo");
        var e = CargoList(wh)[0]; uint tv = HeavyCargo.Synthetic(e.ItemId, e.Id); ushort l;
        Expect(P1(wh, Req(HeavyOp.Lease, tv, 0, Hideout, true), out l) == ProposalCode.Accepted && P1(wh, Req(HeavyOp.Drop, tv, l, Hideout, true), out l) == ProposalCode.Accepted && P1(wh, Req(HeavyOp.Settle, tv, l, Hideout, true), out l) == ProposalCode.Accepted, "TV rests");
        ushort seen = Rec(wh, tv, Hideout).Lease;
        Expect(P2(wh, Req(HeavyOp.Sell, tv, seen, Hideout, true), 2) == ProposalCode.Accepted && wh.Heavy.PendingSales == 1, "H3 sale keeps its undo right");
        Expect(P2(wh, Req(HeavyOp.Unsell, tv, seen, Hideout, true), 2.1) == ProposalCode.Accepted, "H3 undo unchanged");
        Expect(ProtocolInfo.ProtocolVersion == 39 && ProtocolInfo.ModVersion == "0.28.0", "protocol 39 / mod 0.28.0");
    }

    static string Root()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && root != null; i++) { if (Directory.Exists(Path.Combine(root, "src", "Plugin"))) return Path.Combine(root, "src"); root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)); }
        throw new Exception("src not found");
    }
    static string Src(string dir, string f) { return File.ReadAllText(Path.Combine(Root(), dir, f)).Replace("\r\n", "\n"); }
}
