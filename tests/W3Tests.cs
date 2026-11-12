// M5 W3 / W3.1 offline tests: shared hideout locker (lease, transfers, journal, isolation from the car storage), shared dropped
// items (registry, single-winner claims, exactly-once Player-2 ownership, cross-player host journal, region lifetime,
// original Hillville conversion, persistence/replay), and the W3C/W3D gear fields of the player snapshot.
// Pure logic only (WorldHost + P2Book + a simulated host plugin hook). Unity objects, Harmony hooks, the original UI,
// physics and rendering run only in game (manual QA).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W3Tests
{
    static int _passed, _failed;
    static uint _pid;
    const int Hideout = 0, Hillville = 97, Madison = 99;
    const int Watch = 12, Diamond = 30, Crowbar = 1;

    static int Main()
    {
        Run("01 locker: P1 host session, P2 deposit + withdraw as host transactions, counts and record", LockerBasics);
        Run("02 locker: exclusive (concurrent open Busy), independent of the CarBox lease", LockerExclusive);
        Run("03 locker: duplicate / stale / wrong-session / wrong-kind transfers never apply twice", LockerDuplicates);
        Run("04 locker: client only inside the hideout; released on region change, disconnect, expiry", LockerContext);
        Run("05 locker journal: replay after a reload above the watermark only; car and locker keys separate", LockerJournal);
        Run("06 p2inv file v2 round trip (host sequence, kinds) and v1 compatibility", FileVersions);
        Run("07 P1 drop -> P2 pickup: registry, record +1, host journal for P1's backpack", P1DropP2Pick);
        Run("08 P2 drop -> P1 pickup: record -1 durable before the item exists, P1 claims", P2DropP1Pick);
        Run("09 a player picks up its own drop", OwnPickup);
        Run("10 simultaneous pickup: exactly one winner, the loser gains nothing", SimultaneousPickup);
        Run("11 invalid item / quantity / region / foreign item id refused, nothing changes", InvalidRequests);
        Run("12 duplicate drop and duplicate pickup requests are never applied twice", Duplicates);
        Run("13 region lifetime: kept while someone stays, cleared when the region empties (original reload)", RegionLifetime);
        Run("14 reconnect / disconnect: client items survive only while the host stays in that region", Reconnect);
        Run("15 original Hillville conversion on the host locker: registry -> locker exactly once, P2 items journaled", Conversion);
        Run("16 restart safety: host journal replay decides by the save watermark (no dup, no loss)", RestartSafety);
        Run("17 region item limit refuses further drops (item stays in the backpack)", RegionLimit);
        Run("18 settle: only the simulating player may move an item; released when it leaves", Settle);
        Run("19 codecs: item request, region list (limits, malformed, fuzz), storage request 13/14 B", Codecs);
        Run("20 snapshot 34 B with gear (pitch, flashlight, held tool, carry) and sample propagation", GearSnapshot);
        Run("21 Blackbay host guard for the locker lease", Blackbay);
        Run("22 W3.1 drop gate: refusals happen before the original changes the inventory", DropGateRules);
        Run("23 W3.1 drop capture: original Instantiate -> postfix binding order, failed spawn, re-arm", DropCaptureOrder);
        Run("24 W3.1 full region: host and client drops refused, inventory and registry unchanged", FullRegionRefusal);
        Run("25 W3.1 reconnect during a drop: committed once (record decides), retry never duplicates", ReconnectDuringDrop);
        Run("26 W3.1 gear: swing counter (wire, wrap, one swing per change, no replay), lamp/tool visibility incl. vehicles", GearRulesTest);
        Run("27 W3.2 lifecycle P1->P2 and P2->P1: new item Falling at the drop point, owner settle marks it Settled", Lifecycle);
        Run("28 W3.2 rejoin after settle: the region value carries the rest pose + Settled bit (never the airborne drop pose)", RejoinSettled);
        Run("29 W3.2 stale settle after pickup / region clear / from the wrong player never resurrects or moves an item", StaleSettle);
        Run("30 W3.2 elevated surfaces + observer surface rule (settled exact, falling projected, no snap through geometry)", SurfaceRules);
        Run("31 W3.2 owner settle rule: never while paused, first rest from the original rigidbody lifecycle, re-report on movement", OwnerSettleRule);
        Run("32 W3.2 drop while the inventory stays open: one item, one decrement, rejected drop keeps the backpack", DropWhileOpen);
        Run("33 W3.2 settled bit codec: 34 B record, 30 items fit, sim slot validated with the bit set", SettledCodec);
        Run("34 W3.2 gear: crowbar shaft middle in the palm, no arm lift, swing unchanged, diag budget", GearW32);
        Run("35 W3.3 no remote flashlight visual: flashlight bits round-trip but create nothing; local flashlight never written by the gear code", NoRemoteFlashlight);
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

    // ---------------------------------------------------------------- simulated host (WorldHost + plugin side)
    sealed class Host
    {
        public WorldHost Wh = new WorldHost(9) { FallbackHours = 12f };
        public P2Book Book;
        public readonly Dictionary<int, int> P1 = new Dictionary<int, int>(), Locker = new Dictionary<int, int>(), Car = new Dictionary<int, int>();
        public readonly Dictionary<string, int> Prefs = new Dictionary<string, int>();   // host SPrefs memory
        public int MaxAmountCrowbar = 1;
        public byte[] Disk;
        public int P1PickJournals;

        public Host()
        {
            Book = new P2Book(P2Inventory.Initial(false), 0) { HistoryId = 5 }; Book.Rec.Id = 5; Book.Rec.Rev = 1;
            Book.Rec.Set(Watch, 3); Book.Rec.Set(Diamond, 1);
            P1[Watch] = 2; P1[Diamond] = 2; P1[Crowbar] = 1;
            Wh.LockerSnapshot = () => Snap(Locker);
            Wh.CarBoxSnapshot = () => Snap(Car);
            Wh.StorageTxnHook = Transfer;
            Wh.ItemTxnHook = Hook;
            Disk = Book.Serialize(0);
        }

        static byte[] Snap(Dictionary<int, int> d) { var it = new CarBoxItems { Rev = 1 }; foreach (var kv in d) if (kv.Value > 0) it.Counts.Add(new KeyValuePair<int, int>(kv.Key, kv.Value)); byte[] b; it.TryEncode(out b); return b; }
        static int Get(Dictionary<int, int> d, int k) { int v; return d.TryGetValue(k, out v) ? v : 0; }

        ProposalCode Transfer(StorageTxnRequest t)
        {
            var store = t.Kind == StorageKind.Locker ? Locker : Car;
            int c = Get(store, t.ItemId), d;
            var code = Book.Transfer(t.Txn, t.Kind, t.ItemId, t.Withdraw, c, true, true, out d);
            if (code != ProposalCode.Accepted) return code;
            store[t.ItemId] = c + d; Prefs[StorageKind.KeyPrefix(t.Kind) + t.ItemId] = c + d; Prefs["TS2MP_StorageTxn"] = (int)t.Txn;
            Disk = Book.Serialize(0);
            return code;
        }

        ProposalCode Hook(byte slot, ItemTxnRequest t)
        {
            switch (t.Op)
            {
                case ItemOp.Drop:
                    if (slot == 1) return ProposalCode.Accepted;
                    if (t.ItemId == 777) return ProposalCode.Invalid;          // unknown on the host
                    var dc = Book.ItemTxn(t.Txn, t.ItemId, -1); if (dc == ProposalCode.Accepted) Disk = Book.Serialize(0); return dc;
                case ItemOp.Pickup:
                {
                    WorldItem it; int r; if (!Wh.Items.TryGet(t.WorldId, out it, out r)) return ProposalCode.Stale;
                    if (slot == 1) { P1[it.ItemId] = Get(P1, it.ItemId) + 1; if (it.Origin == 2) { P1PickJournals++; uint s = Book.NextHostSeq(); Book.AddHostEntry(s, JournalKind.HostItem, it.ItemId, +1); Prefs["TS2MP_HostTxn"] = (int)s; Prefs["Item_" + it.ItemId] = P1[it.ItemId]; Disk = Book.Serialize(0); } return ProposalCode.Accepted; }
                    var pc = Book.ItemTxn(t.Txn, it.ItemId, +1);
                    if (pc != ProposalCode.Accepted) return pc;
                    if (it.Origin == 1) Book.AddHostEntry(it.OriginTxn, JournalKind.HostItem, it.ItemId, -1);
                    Disk = Book.Serialize(0);
                    return pc;
                }
                case ItemOp.Convert:
                    foreach (var it in Wh.Items.InRegion(t.Region))
                    {
                        if (it.ItemId == Crowbar && MaxAmountCrowbar > 0) continue;   // original: max_amount > 0 stays on the ground
                        Locker[it.ItemId] = Get(Locker, it.ItemId) + 1;
                        Wh.Items.Remove(it.Id, Wh.Reg, Wh.Outbox);
                        if (it.Origin == 2) { uint s = Book.NextHostSeq(); Book.AddHostEntry(s, JournalKind.HostLocker, it.ItemId, +1); Prefs["TS2MP_HostTxn"] = (int)s; }
                    }
                    Disk = Book.Serialize(0);
                    return ProposalCode.Accepted;
            }
            return ProposalCode.Invalid;
        }

        // host original drop: P1 backpack -1 now, host sequence + watermark in the save memory, then registered
        public uint HostDrop(int item, int region, float x = 1)
        {
            P1[item] = Get(P1, item) - 1;
            uint seq = Book.NextHostSeq(); Prefs["TS2MP_HostTxn"] = (int)seq; Prefs["Item_" + item] = P1[item];
            string why;
            var c = Wh.ItemTransaction(1, new ItemTxnRequest { Op = ItemOp.Drop, Txn = seq, ItemId = item, Region = region, Pos = new Vec3(x, 0, 0), Rot = Quat16.From(0, 0, 0, 1) }, 1, out why);
            if (c != ProposalCode.Accepted) { P1[item]++; return 0; }
            return Wh.Items.LastApplied;
        }

        public ProposalCode Cli(ItemTxnRequest t, uint pid = 0)
        {
            uint rev; int sr; float sh;
            return Wh.HandleProposal("cli", 2, pid != 0 ? pid : ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ItemTxn, Value = t.Encode() }, 1, out rev, out sr, out sh);
        }
        public ProposalCode CliStorage(byte kind, CarBoxOp op, uint session)
        {
            uint rev; int sr; float sh;
            return Wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = kind, Key = WorldKeys.CarBoxReq, Value = CarBoxRequest.Encode(op, session) }, 1, out rev, out sr, out sh);
        }
        public ProposalCode CliTxn(StorageTxnRequest t)
        {
            uint rev; int sr; float sh;
            return Wh.HandleProposal("cli", 2, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StorageTxn, Value = t.Encode() }, 1, out rev, out sr, out sh);
        }
        public List<WorldItem> Listed(int region)
        {
            var e = Wh.Reg.Get(StateScope.Region, region, WorldKeys.WorldItems);
            List<WorldItem> l; if (e == null) return new List<WorldItem>();
            Expect(WorldItemList.TryParse(e.Value, out l), "registry list parses");
            return l;
        }
    }

    static Host Both(int region) { var h = new Host(); h.Wh.SetPlayerRegion(1, region, 0); h.Wh.SetPlayerRegion(2, region, 0); h.Wh.Outbox.Clear(); return h; }
    static ItemTxnRequest Drop(uint txn, int item, int region) { return new ItemTxnRequest { Op = ItemOp.Drop, Txn = txn, ItemId = item, Region = region, Pos = new Vec3(2, 0, 0), Rot = Quat16.From(0, 0, 0, 1) }; }
    static ItemTxnRequest Pick(uint txn, uint id, int region) { return new ItemTxnRequest { Op = ItemOp.Pickup, Txn = txn, WorldId = id, Region = region }; }
    static int Count(Dictionary<int, int> d, int k) { int v; return d.TryGetValue(k, out v) ? v : 0; }

    // ---------------------------------------------------------------- locker
    static void LockerBasics()
    {
        var h = Both(Hideout);
        string why;
        Expect(h.Wh.StorageRequest(StorageKind.Locker, 1, CarBoxOp.Open, 0, 1, out why) == ProposalCode.Accepted, "P1 opens the locker");
        Expect(h.Wh.Locker.Owner == 1 && h.Wh.CarBox.Owner == 0, "locker lease, CarBox untouched");
        uint s1 = h.Wh.Locker.Session;
        Expect(h.Wh.StorageRequest(StorageKind.Locker, 1, CarBoxOp.Close, s1, 2, out why) == ProposalCode.Accepted, "P1 closes");
        h.Locker[Diamond] = 4;
        h.Wh.Outbox.Clear();
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.Accepted, "P2 opens the locker");
        int iItems = -1, iLease = -1;
        for (int i = 0; i < h.Wh.Outbox.Count; i++) { var e = h.Wh.Outbox[i]; if (e.ScopeArg == StorageKind.Locker && e.Key == WorldKeys.CarBoxItems) iItems = i; if (e.ScopeArg == StorageKind.Locker && e.Key == WorldKeys.CarBox) iLease = i; }
        Expect(iItems >= 0 && iLease > iItems, "locker contents committed (arg 1) before the locker lease");
        CarBoxItems ci; Expect(CarBoxItems.TryParse(h.Wh.Reg.Get(StateScope.Session, 1, WorldKeys.CarBoxItems).Value, out ci) && ci.CountOf(Diamond) == 4, "host locker contents in the registry");
        uint s2 = h.Wh.Locker.Session;
        Expect(h.CliTxn(new StorageTxnRequest { Session = s2, Txn = 1, ItemId = Watch, Withdraw = false, Kind = StorageKind.Locker }) == ProposalCode.Accepted, "P2 deposit");
        Expect(Count(h.Locker, Watch) == 1 && h.Book.Rec.CountOf(Watch) == 2, "locker +1, P2 -1");
        Expect(h.CliTxn(new StorageTxnRequest { Session = s2, Txn = 2, ItemId = Diamond, Withdraw = true, Kind = StorageKind.Locker }) == ProposalCode.Accepted, "P2 withdraw");
        Expect(Count(h.Locker, Diamond) == 3 && h.Book.Rec.CountOf(Diamond) == 2, "locker -1, P2 +1");
        Expect(h.Prefs["Locker_Item_" + Diamond] == 3 && h.Prefs["TS2MP_StorageTxn"] == 2, "Locker_Item_ key + watermark in the save memory");
        Expect(h.Book.Journal.Count == 2 && h.Book.Journal[0].Kind == JournalKind.Locker, "journal entries name the locker");
    }

    static void LockerExclusive()
    {
        var h = Both(Hideout);
        string why;
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.Accepted, "P2 has the locker");
        Expect(h.Wh.StorageRequest(StorageKind.Locker, 1, CarBoxOp.Open, 0, 1, out why) == ProposalCode.Busy, "P1 refused while P2 uses it: " + why);
        h.Wh.Fleet.Register(VehicleIds.FromCarNr(0), 0, true, h.Wh.Outbox); h.Wh.Fleet.Place(VehicleIds.FromCarNr(0), Hideout, h.Wh.Outbox);
        Expect(h.Wh.CarBoxRequest(1, CarBoxOp.Open, 0, 1, out why) == ProposalCode.Accepted, "the CarBox is a separate lease");
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.Unchanged, "repeated open of the owner harmless");
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Close, h.Wh.Locker.Session) == ProposalCode.Accepted && h.Wh.Locker.Owner == 0, "P2 closes");
        Expect(h.Wh.StorageRequest(StorageKind.Locker, 1, CarBoxOp.Open, 0, 2, out why) == ProposalCode.Accepted, "P1 now");
    }

    static void LockerDuplicates()
    {
        var h = Both(Hideout);
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.Accepted, "open");
        uint s = h.Wh.Locker.Session;
        var t1 = new StorageTxnRequest { Session = s, Txn = 1, ItemId = Watch, Withdraw = false, Kind = StorageKind.Locker };
        Expect(h.CliTxn(t1) == ProposalCode.Accepted, "#1");
        Expect(h.CliTxn(t1) == ProposalCode.Unchanged && Count(h.Locker, Watch) == 1 && h.Book.Rec.CountOf(Watch) == 2, "retransmitted #1 not applied again");
        Expect(h.CliTxn(new StorageTxnRequest { Session = s, Txn = 3, ItemId = Watch, Kind = StorageKind.Locker }) == ProposalCode.Stale, "gap refused");
        Expect(h.CliTxn(new StorageTxnRequest { Session = s + 5, Txn = 2, ItemId = Watch, Kind = StorageKind.Locker }) == ProposalCode.Stale, "other session refused");
        Expect(h.CliTxn(new StorageTxnRequest { Session = s, Txn = 2, ItemId = Watch, Kind = StorageKind.Car }) == ProposalCode.Stale, "car-storage transfer inside a locker session refused (no CarBox lease)");
        Expect(h.Book.Rec.AppliedTxn == 1 && Count(h.Locker, Watch) == 1, "nothing else applied");
    }

    static void LockerContext()
    {
        var h = new Host(); h.Wh.SetPlayerRegion(1, Hideout, 0); h.Wh.SetPlayerRegion(2, Madison, 0);
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.NotAuthorized, "client outside the hideout cannot open the locker");
        h.Wh.SetPlayerRegion(2, Hideout, 1);
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.Accepted, "inside the hideout");
        h.Wh.SetPlayerRegion(2, Madison, 2);
        Expect(h.Wh.Locker.Owner == 0, "released on region change");
        h.Wh.SetPlayerRegion(2, Hideout, 3);
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.Accepted, "again");
        h.Wh.SetPlayerRegion(2, Regions.None, 4);
        Expect(h.Wh.Locker.Owner == 0, "released on disconnect / menu");
        h.Wh.SetPlayerRegion(2, Hideout, 5);
        Expect(h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0) == ProposalCode.Accepted, "again");
        h.Wh.Tick(5 + CarBoxLease.LeaseSeconds + 1);
        Expect(h.Wh.Locker.Owner == 0, "a silent client lease expires (no permanent lock)");
    }

    static void LockerJournal()
    {
        var h = Both(Hideout);
        h.CliStorage(StorageKind.Locker, CarBoxOp.Open, 0); uint s = h.Wh.Locker.Session;
        h.CliTxn(new StorageTxnRequest { Session = s, Txn = 1, ItemId = Watch, Kind = StorageKind.Locker });
        h.CliStorage(StorageKind.Locker, CarBoxOp.Close, s);
        h.Wh.Fleet.Register(VehicleIds.FromCarNr(0), 0, true, h.Wh.Outbox); h.Wh.Fleet.Place(VehicleIds.FromCarNr(0), Hideout, h.Wh.Outbox);
        h.CliStorage(StorageKind.Car, CarBoxOp.Open, 0); uint cs = h.Wh.CarBox.Session;
        Expect(h.CliTxn(new StorageTxnRequest { Session = cs, Txn = 2, ItemId = Diamond, Kind = StorageKind.Car }) == ProposalCode.Accepted, "car deposit #2");
        // host crash before any save: disk save has watermark 0 and no counts; replay applies both, to the right key family
        var mem = new Dictionary<string, int>();
        foreach (var e in h.Book.ReplayFor(0)) { string k = JournalKind.KeyPrefix(e.Kind) + e.ItemId; int v; mem.TryGetValue(k, out v); mem[k] = v + e.StorageDelta; }
        Expect(mem["Locker_Item_" + Watch] == 1 && mem["Storage_Item_" + Diamond] == 1 && mem.Count == 2, "locker and car entries replay into their own keys");
        Expect(h.Book.ReplayFor(2).Count == 0 && h.Book.ReplayFor(1).Count == 1, "only entries above the loaded watermark");
        h.Book.SaveCompleted(2, 0);
        Expect(h.Book.Journal.Count == 0, "compacted after the save");
    }

    static void FileVersions()
    {
        var b = new P2Book(P2Inventory.Initial(false), 3) { HistoryId = 11, HistoryConfirmed = true }; b.Rec.Id = 11;
        b.Rec.Set(Watch, 1); int d; Expect(b.Transfer(1, StorageKind.Locker, Watch, false, 0, true, true, out d) == ProposalCode.Accepted, "locker deposit #1");
        b.Rec.Set(Watch, 0);
        uint s = b.NextHostSeq(); b.AddHostEntry(s, JournalKind.HostItem, Diamond, -1);
        uint s2 = b.NextHostSeq(); b.AddHostEntry(s2, JournalKind.HostLocker, Watch, +1);
        b.LastSavedHost = 0;
        P2Book back; string err;
        Expect(P2Book.TryDeserialize(b.Serialize(0), 0, out back, out err), "v2 round trip: " + err);
        Expect(back.HostSeq == 2 && back.Journal.Count == 3 && back.Journal[0].Kind == JournalKind.Locker && back.Journal[1].Kind == JournalKind.HostItem && back.Journal[2].Kind == JournalKind.HostLocker, "kinds + host sequence kept");
        Expect(back.ReplayFor(0).Count == 1 && back.ReplayHost(0).Count == 2 && back.ReplayHost(1).Count == 1, "P2 and host replay spaces are separate");
        // v1 file (0.7.x-0.9.x layout): no host fields, no kind bytes
        var w = new NetDataWriter(); w.Put((uint)0x32504D54); w.Put((byte)1); w.Put(0); w.Put((uint)2); w.Put(11); w.Put(true);
        var rec = P2Inventory.Initial(false); rec.Id = 11; rec.Write(w); w.Put(1); w.Put((uint)3); w.Put(Watch); w.Put(1);
        var body = w.CopyData(); uint h = 2166136261; foreach (var x in body) { h ^= x; h *= 16777619; }
        var o = new NetDataWriter(); o.Put(body.Length); o.Put(body); o.Put(h);
        Expect(P2Book.TryDeserialize(o.CopyData(), 0, out back, out err) && back.Journal.Count == 1 && back.Journal[0].Kind == JournalKind.Car && back.HostSeq == 0, "v1 file still readable as car-storage journal: " + err);
        var bad = b.Serialize(0); bad[bad.Length - 5] ^= 0x55;
        Expect(!P2Book.TryDeserialize(bad, 0, out back, out err), "corrupt file refused");
    }

    // ---------------------------------------------------------------- dropped items
    static void P1DropP2Pick()
    {
        var h = Both(Madison);
        uint id = h.HostDrop(Diamond, Madison);
        Expect(id != 0 && h.P1[Diamond] == 1, "P1 dropped one diamond");
        var l = h.Listed(Madison);
        Expect(l.Count == 1 && l[0].Origin == 1 && l[0].ItemId == Diamond && l[0].Sim == 1, "registered for both players (P1 simulates)");
        Expect(h.Cli(Pick(1, id, Madison)) == ProposalCode.Accepted, "P2 picks it up");
        Expect(h.Book.Rec.CountOf(Diamond) == 2 && h.Book.Rec.AppliedTxn == 1, "P2 owns it (record +1)");
        Expect(h.Listed(Madison).Count == 0, "gone for both");
        var hj = h.Book.ReplayHost(0);
        Expect(hj.Count == 1 && hj[0].Kind == JournalKind.HostItem && hj[0].StorageDelta == -1 && hj[0].Txn == l[0].OriginTxn, "P1's loss journaled with the drop's host sequence");
        Expect(h.P1[Diamond] == 1, "P1 keeps exactly one");
    }

    static void P2DropP1Pick()
    {
        var h = Both(Madison);
        Expect(h.Cli(Drop(1, Watch, Madison)) == ProposalCode.Accepted, "P2 drop committed");
        Expect(h.Book.Rec.CountOf(Watch) == 2 && h.Book.Rec.AppliedTxn == 1, "record -1 (durable) before the item exists in the world");
        P2Book disk; string err; Expect(P2Book.TryDeserialize(h.Disk, 0, out disk, out err) && disk.Rec.CountOf(Watch) == 2, "written to the p2inv file");
        var l = h.Listed(Madison);
        Expect(l.Count == 1 && l[0].Origin == 2 && l[0].OriginTxn == 1 && l[0].Sim == 2, "registered as P2's item (P2 simulates)");
        string why;
        Expect(h.Wh.ItemTransaction(1, Pick(0, l[0].Id, Madison), 2, out why) == ProposalCode.Accepted, "P1 picks it up");
        Expect(h.P1[Watch] == 3 && h.Book.Rec.CountOf(Watch) == 2 && h.P1PickJournals == 1, "P1 +1 (journaled), P2 unchanged");
        Expect(h.Listed(Madison).Count == 0, "gone for both");
    }

    static void OwnPickup()
    {
        var h = Both(Madison);
        h.Cli(Drop(1, Watch, Madison)); uint id = h.Listed(Madison)[0].Id;
        Expect(h.Cli(Pick(2, id, Madison)) == ProposalCode.Accepted && h.Book.Rec.CountOf(Watch) == 3 && h.Book.Rec.AppliedTxn == 2, "P2 takes its own item back");
        uint id2 = h.HostDrop(Watch, Madison); string why;
        Expect(h.Wh.ItemTransaction(1, Pick(0, id2, Madison), 3, out why) == ProposalCode.Accepted && h.P1[Watch] == 2 && h.Book.ReplayHost(0).Count == 0, "P1 takes its own item back (no journal)");
    }

    static void SimultaneousPickup()
    {
        var h = Both(Madison);
        uint id = h.HostDrop(Diamond, Madison);
        string why;
        // host click processed first (in place), the client's request arrives afterwards
        Expect(h.Wh.ItemTransaction(1, Pick(0, id, Madison), 2, out why) == ProposalCode.Accepted, "first claim wins");
        Expect(h.Cli(Pick(1, id, Madison)) == ProposalCode.Stale, "second claim loses (already claimed)");
        Expect(h.Book.Rec.CountOf(Diamond) == 1 && h.Book.Rec.AppliedTxn == 0 && h.P1[Diamond] == 2, "loser gained nothing, winner exactly one");
        // reverse order
        uint id2 = h.HostDrop(Diamond, Madison);
        Expect(h.Cli(Pick(1, id2, Madison)) == ProposalCode.Accepted, "client first");
        Expect(h.Wh.ItemTransaction(1, Pick(0, id2, Madison), 3, out why) == ProposalCode.Stale, "host second refused");
        Expect(h.Book.Rec.CountOf(Diamond) == 2 && h.P1[Diamond] == 1, "exactly one owner");
    }

    static void InvalidRequests()
    {
        var h = Both(Madison);
        Expect(h.Cli(Drop(1, 555, Madison)) == ProposalCode.Invalid, "drop of an item P2 does not own");
        Expect(h.Cli(Drop(1, 777, Madison)) == ProposalCode.Invalid, "item unknown on the host");
        Expect(h.Cli(Drop(1, Watch, Hillville)) == ProposalCode.NotAuthorized, "drop into another region");
        Expect(h.Cli(Pick(1, 12345, Madison)) == ProposalCode.Stale, "pickup of an unknown world id");
        uint id = h.HostDrop(Watch, Madison);
        Expect(h.Cli(Pick(1, id, Hillville)) == ProposalCode.NotAuthorized, "pickup claimed for another region");
        Expect(h.Cli(Drop(5, Watch, Madison)) == ProposalCode.Stale, "transaction id gap");
        Expect(h.Book.Rec.AppliedTxn == 0 && h.Book.Rec.CountOf(Watch) == 3 && h.Listed(Madison).Count == 1, "nothing changed");
        h.Book.Rec.Set(Diamond, ushort.MaxValue);
        Expect(h.Cli(Pick(1, id, Madison)) == ProposalCode.Invalid || h.Book.Rec.CountOf(Watch) == 4, "count overflow guarded");
    }

    static void Duplicates()
    {
        var h = Both(Madison);
        var d = Drop(1, Watch, Madison);
        Expect(h.Cli(d, 9000) == ProposalCode.Accepted, "drop");
        Expect(h.Cli(d, 9000) == ProposalCode.Accepted && h.Listed(Madison).Count == 1, "same proposal id: cached answer, no second item");
        Expect(h.Cli(d, 9001) == ProposalCode.Unchanged && h.Listed(Madison).Count == 1 && h.Book.Rec.CountOf(Watch) == 2, "same transaction id in a new proposal: Unchanged, no second item, no second -1");
        uint id = h.Listed(Madison)[0].Id;
        Expect(h.Cli(Pick(2, id, Madison), 9002) == ProposalCode.Accepted, "pickup");
        Expect(h.Cli(Pick(2, id, Madison), 9003) == ProposalCode.Stale && h.Book.Rec.CountOf(Watch) == 3, "duplicate pickup: no second +1");
    }

    static void RegionLifetime()
    {
        var h = Both(Madison);
        h.HostDrop(Diamond, Madison); h.Cli(Drop(1, Watch, Madison));
        h.Wh.SetPlayerRegion(1, Hideout, 5);
        Expect(h.Listed(Madison).Count == 2, "P1 left, P2 stays: both items stay");
        h.Wh.SetPlayerRegion(1, Madison, 6);
        Expect(h.Listed(Madison).Count == 2, "P1 returns: items still there");
        h.Wh.SetPlayerRegion(1, Hideout, 7); h.Wh.SetPlayerRegion(2, Hideout, 8);
        Expect(h.Listed(Madison).Count == 0 && h.Wh.Items.Cleared == 2, "both left: cleared like the original scene reload");
        Expect(h.Book.Rec.CountOf(Watch) == 2, "the P2 record keeps the committed drop (item lost with the world, never duplicated)");
    }

    static void Reconnect()
    {
        var h = Both(Madison);
        h.Cli(Drop(1, Watch, Madison));
        h.Wh.SetPlayerRegion(2, Regions.None, 3);          // client disconnect / menu
        Expect(h.Listed(Madison).Count == 1 && h.Listed(Madison)[0].Sim == 0, "host stays: item kept, nobody simulates it");
        h.Wh.SetPlayerRegion(2, Madison, 4);                // rejoin
        uint id = h.Listed(Madison)[0].Id;
        Expect(h.Cli(Pick(2, id, Madison)) == ProposalCode.Accepted && h.Book.Rec.CountOf(Watch) == 3, "after reconnect the item is still claimable");
        var h2 = new Host(); h2.Wh.SetPlayerRegion(1, Hideout, 0); h2.Wh.SetPlayerRegion(2, Madison, 0);
        h2.Cli(Drop(1, Watch, Madison));
        h2.Wh.SetPlayerRegion(2, Regions.None, 3);
        Expect(h2.Listed(Madison).Count == 0, "host elsewhere: region emptied, item gone (original reload semantics)");
    }

    static void Conversion()
    {
        var h = Both(Hillville);
        h.HostDrop(Diamond, Hillville, 1); h.Cli(Drop(1, Watch, Hillville)); h.HostDrop(Crowbar, Hillville, 3);
        Expect(h.Listed(Hillville).Count == 3, "three dropped");
        Expect(h.Cli(new ItemTxnRequest { Op = ItemOp.Convert, Region = Hillville }) == ProposalCode.Accepted, "P2 travel save in Hillville -> host converts");
        var l = h.Listed(Hillville);
        Expect(l.Count == 1 && l[0].ItemId == Crowbar, "max_amount item stays on the ground (original)");
        Expect(Count(h.Locker, Diamond) == 1 && Count(h.Locker, Watch) == 1, "host locker +1 each");
        var hj = h.Book.ReplayHost(0);
        Expect(hj.Count == 1 && hj[0].Kind == JournalKind.HostLocker && hj[0].ItemId == Watch, "only the Player-2 item is journaled (P1's drop and locker stay in the same memory checkpoint)");
        Expect(h.Cli(new ItemTxnRequest { Op = ItemOp.Convert, Region = Hillville }) == ProposalCode.Accepted && Count(h.Locker, Diamond) == 1, "a second conversion converts nothing twice");
        Expect(h.Cli(new ItemTxnRequest { Op = ItemOp.Convert, Region = Madison }) == ProposalCode.NotAuthorized, "only the requester's own region");
    }

    static void RestartSafety()
    {
        // P1 drops (host seq 1), P2 picks it up. Case A: host save on disk predates the drop (watermark 0) -> P1's backpack
        // from disk still has the item -> replay removes it. Case B: the save already contains the drop (watermark 1) -> skip.
        var h = Both(Madison);
        uint id = h.HostDrop(Diamond, Madison);
        h.Cli(Pick(1, id, Madison));
        var a = h.Book.ReplayHost(0);
        Expect(a.Count == 1 && a[0].StorageDelta == -1, "A: replay -1 on Player 1 (no duplicate)");
        Expect(h.Book.ReplayHost(1).Count == 0, "B: save contains the drop -> nothing (no loss)");
        // P2 drops, P1 picks it up (host seq 2). A save before the pickup -> replay +1 (no loss); after -> skip.
        h.Cli(Drop(2, Watch, Madison)); string why;
        h.Wh.ItemTransaction(1, Pick(0, h.Listed(Madison)[0].Id, Madison), 3, out why);
        var b = h.Book.ReplayHost(1);
        Expect(b.Count == 1 && b[0].StorageDelta == +1 && b[0].Kind == JournalKind.HostItem, "save before P1's pickup: +1 restored");
        Expect(h.Book.ReplayHost(2).Count == 0, "save after the pickup: nothing");
        h.Book.SaveCompleted(h.Book.Rec.AppliedTxn, 2);
        Expect(h.Book.Journal.Count == 0, "journal compacted at the save");
        P2Book back; string err; Expect(P2Book.TryDeserialize(h.Book.Serialize(0), 0, out back, out err) && back.HostSeq == 2, "host sequence survives a restart (stays monotonic)");
    }

    static void RegionLimit()
    {
        var h = Both(Madison);
        h.P1[Watch] = 100;
        for (int i = 0; i < WorldItemList.MaxPerRegion; i++) Expect(h.HostDrop(Watch, Madison, i) != 0, "drop " + i);
        Expect(h.HostDrop(Watch, Madison, 99) == 0 && h.P1[Watch] == 100 - WorldItemList.MaxPerRegion, "31st refused, item stays in the backpack");
        Expect(h.Cli(Drop(1, Watch, Madison)) == ProposalCode.Busy && h.Book.Rec.CountOf(Watch) == 3, "client refused too, record untouched");
        Expect(h.Wh.Reg.Get(StateScope.Region, Madison, WorldKeys.WorldItems).Value.Length <= StateEntry.MaxValueBytes, "region list fits one registry value");
    }

    static void Settle()
    {
        var h = Both(Madison);
        h.Cli(Drop(1, Watch, Madison)); uint id = h.Listed(Madison)[0].Id;
        string why;
        Expect(h.Wh.ItemTransaction(1, new ItemTxnRequest { Op = ItemOp.Settle, WorldId = id, Region = Madison, Pos = new Vec3(5, 0, 5), Rot = Quat16.From(0, 0, 0, 1) }, 2, out why) == ProposalCode.NotAuthorized, "host is not the simulating player of P2's item");
        Expect(h.Cli(new ItemTxnRequest { Op = ItemOp.Settle, WorldId = id, Region = Madison, Pos = new Vec3(5, 0.1f, 5), Rot = Quat16.From(0, 0.7f, 0, 0.7f) }) == ProposalCode.Accepted, "P2 reports the rest pose");
        var it = h.Listed(Madison)[0];
        Expect(Math.Abs(it.Pos.X - 5) < 1e-4 && Math.Abs(it.Rot.Fy - 0.7071f) < 0.01f, "rest pose committed");
        h.Wh.SetPlayerRegion(2, Hideout, 3);
        Expect(h.Listed(Madison)[0].Sim == 0, "simulation released when P2 leaves");
        Expect(h.Cli(new ItemTxnRequest { Op = ItemOp.Settle, WorldId = id, Region = Hideout, Pos = new Vec3(1, 1, 1), Rot = Quat16.From(0, 0, 0, 1) }) == ProposalCode.Stale, "no remote moves from elsewhere");
    }

    static void Codecs()
    {
        var t = new ItemTxnRequest { Op = ItemOp.Pickup, Txn = 77, WorldId = 5, ItemId = Watch, Region = Madison, Pos = new Vec3(1.5f, -2, 3), Rot = Quat16.From(0.1f, 0.2f, 0.3f, 0.9f) };
        var b = t.Encode(); ItemTxnRequest r;
        Expect(b.Length == ItemTxnRequest.WireSize && ItemTxnRequest.TryParse(b, out r) && r.Op == ItemOp.Pickup && r.Txn == 77 && r.WorldId == 5 && r.Region == Madison && Math.Abs(r.Pos.Y + 2) < 1e-6, "37-byte item request round trip");
        Expect(!ItemTxnRequest.TryParse(new byte[3], out r) && !ItemTxnRequest.TryParse(null, out r), "short / null refused");
        var bad = (byte[])b.Clone(); bad[0] = 9; Expect(!ItemTxnRequest.TryParse(bad, out r), "unknown op refused");
        var nan = new ItemTxnRequest { Op = ItemOp.Drop, Pos = new Vec3(float.NaN, 0, 0) }.Encode(); Expect(!ItemTxnRequest.TryParse(nan, out r), "NaN pose refused");
        var far = new ItemTxnRequest { Op = ItemOp.Drop, Pos = new Vec3(1e7f, 0, 0) }.Encode(); Expect(!ItemTxnRequest.TryParse(far, out r), "out-of-world pose refused");
        var list = new List<WorldItem>();
        for (int i = 1; i <= WorldItemList.MaxPerRegion; i++) list.Add(new WorldItem { Id = (uint)i, ItemId = i, Origin = 1, OriginTxn = 3, Pos = new Vec3(i, 0, 0), Rot = Quat16.From(0, 0, 0, 1), Sim = 1 });
        var enc = WorldItemList.Encode(list); List<WorldItem> back;
        Expect(enc.Length <= StateEntry.MaxValueBytes && WorldItemList.TryParse(enc, out back) && back.Count == 30 && back[29].ItemId == 30, "30 items fit one registry value (" + enc.Length + " B)");
        var dup = new List<WorldItem> { list[0], list[0] }; Expect(!WorldItemList.TryParse(WorldItemList.Encode(dup), out back), "duplicate ids refused");
        var badOrigin = new List<WorldItem> { new WorldItem { Id = 1, Origin = 7 } }; Expect(!WorldItemList.TryParse(WorldItemList.Encode(badOrigin), out back), "invalid origin refused");
        var rnd = new Random(3); int accepted = 0;
        for (int i = 0; i < 20000; i++)
        {
            var junk = new byte[rnd.Next(0, 80)]; rnd.NextBytes(junk);
            if (junk.Length > 1) junk[0] = 1;
            try { if (WorldItemList.TryParse(junk, out back)) accepted++; ItemTxnRequest.TryParse(junk, out r); }
            catch (Exception ex) { throw new Exception("decoder threw on fuzz input: " + ex.GetType().Name); }
        }
        Expect(accepted < 20000, "fuzz: decoders never throw (" + accepted + " random lists accepted as well-formed)");
        StorageTxnRequest s;
        Expect(new StorageTxnRequest { Session = 1, Txn = 2, ItemId = 3 }.Encode().Length == 13, "car transfer keeps the 13-byte M4.3.3 format");
        var lk = new StorageTxnRequest { Session = 1, Txn = 2, ItemId = 3, Withdraw = true, Kind = StorageKind.Locker }.Encode();
        Expect(lk.Length == 14 && StorageTxnRequest.TryParse(lk, out s) && s.Kind == StorageKind.Locker && s.Withdraw, "locker transfer 14 bytes");
        lk[13] = 5; Expect(!StorageTxnRequest.TryParse(lk, out s), "unknown storage kind refused");
        uint rev; int sr; float sh; var wh = new WorldHost(1);
        Expect(wh.HandleProposal("c", 2, 1, new StateEntry { Scope = StateScope.Session, ScopeArg = 2, Key = WorldKeys.CarBoxReq, Value = CarBoxRequest.Encode(CarBoxOp.Open, 0) }, 0, out rev, out sr, out sh) == ProposalCode.Invalid, "storage kind 2 refused");
        Expect(wh.HandleProposal("c", 2, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.WorldItems, Value = new byte[2] { 1, 0 } }, 0, out rev, out sr, out sh) == ProposalCode.NotAuthorized, "the client can never write the item registry");
        Expect(wh.HandleProposal("c", 2, 3, new StateEntry { Scope = StateScope.Session, ScopeArg = 4, Key = WorldKeys.ItemTxn, Value = t.Encode() }, 0, out rev, out sr, out sh) == ProposalCode.Invalid, "item txn with a wrong scope arg");
    }

    static void GearSnapshot()
    {
        var s = new PlayerSnapshot { Slot = 2, Seq = 9, TimeMs = 100, SceneHash = 4, Pos = new Vec3(1, 2, 3), Yaw = 90, Flags = MoveFlags.Present | MoveFlags.Crouching, Pitch = -35, Gear = GearFlags.Torch | GearFlags.Carrying, Held = 102, CarryItem = 301 };
        var w = new NetDataWriter(); PlayerStateCodec.Write(w, s);
        Expect(w.Length == PlayerStateCodec.WireSize && w.Length == 34, "34 bytes");
        var r = new NetDataReader(w.CopyData()); r.GetByte(); PlayerSnapshot d;
        Expect(PlayerStateCodec.TryRead(r, out d) && d.Pitch == -35 && d.Gear == (GearFlags.Torch | GearFlags.Carrying) && d.Held == 102 && d.CarryItem == 301 && d.Has(MoveFlags.Crouching), "gear round trip");
        var raw = w.CopyData(); raw[30] = 0xFF; r = new NetDataReader(raw); r.GetByte(); PlayerStateCodec.TryRead(r, out d);
        Expect(d.Gear == (GearFlags.Torch | GearFlags.Carrying | GearFlags.HasLight) && d.Swing == 15, "gear byte: low bits = flags (bit 3 ignored), high nibble = swing counter");
        Expect(PlayerStateCodec.QuantizePitch(350f) == -10 && PlayerStateCodec.QuantizePitch(30f) == 30 && PlayerStateCodec.QuantizePitch(200f) == -90 && PlayerStateCodec.QuantizePitch(89.6f) == 90, "pitch quantization (Unity euler x)");
        var buf = new SnapshotBuffer();
        buf.Add(new PlayerSnapshot { Slot = 2, Seq = 1, TimeMs = 0, Flags = MoveFlags.Present, Gear = GearFlags.None, Held = 0 }, 0);
        buf.Add(new PlayerSnapshot { Slot = 2, Seq = 2, TimeMs = 50, Flags = MoveFlags.Present, Gear = GearFlags.Torch, Held = 1, Pitch = 10 }, 0.05);
        RemoteSample smp; Expect(buf.TrySample(1.0, out smp) && smp.Gear == GearFlags.Torch && smp.Held == 1 && Math.Abs(smp.Pitch - 10) < 1e-4, "latest gear reaches the presentation (repeated state = same values, idempotent)");
        Expect(!buf.Add(new PlayerSnapshot { Slot = 2, Seq = 2, TimeMs = 50, Flags = MoveFlags.Present, Gear = GearFlags.None }, 0.06), "duplicate packet (same sequence) ignored");
        var trunc = new NetDataReader(new byte[30]); Expect(!PlayerStateCodec.TryRead(trunc, out d), "29-byte (old protocol) snapshot refused");
    }

    static void DropGateRules()
    {
        string why;
        Expect(DropGate.Decide(false, false, true, Madison, 0, false, true, out why) == DropDecision.Original, "offline: original drop");
        Expect(DropGate.Decide(true, true, false, Madison, 0, false, true, out why) == DropDecision.Original, "special item: original local drop");
        Expect(DropGate.Decide(true, true, true, Regions.None, 0, false, true, out why) == DropDecision.Refuse, "region unknown: refused (no untracked object)");
        Expect(DropGate.Decide(true, true, true, Madison, WorldItemList.MaxPerRegion, false, true, out why) == DropDecision.Refuse && why.Contains("limit"), "host at the limit: refused");
        Expect(DropGate.Decide(true, false, true, Madison, WorldItemList.MaxPerRegion, false, true, out why) == DropDecision.Refuse, "client at the limit: refused");
        Expect(DropGate.Decide(true, true, true, Madison, 3, true, false, out why) == DropDecision.HostOriginal, "host: original now, captured + registered");
        Expect(DropGate.Decide(true, false, true, Madison, 3, true, true, out why) == DropDecision.Refuse, "client busy: refused");
        Expect(DropGate.Decide(true, false, true, Madison, 3, false, false, out why) == DropDecision.Refuse, "client not restored: refused");
        Expect(DropGate.Decide(true, false, true, Madison, 3, false, true, out why) == DropDecision.ClientRequest, "client: host request first, original after the commit");
    }

    static void DropCaptureOrder()
    {
        var c = new DropCapture(); object item; var itemA = new object(); var objA = new object(); var other = new object();
        c.Seen(other);
        Expect(!c.Armed, "an Instantiate outside a drop is ignored");
        c.Arm(itemA); c.Seen(objA); c.Seen(other);
        Expect(ReferenceEquals(c.Take(out item), objA) && ReferenceEquals(item, itemA) && !c.Armed, "first Instantiate of the armed drop is bound in the postfix");
        c.Arm(itemA);
        Expect(c.Take(out item) == null && ReferenceEquals(item, itemA), "original created nothing: failure reported, nothing bound");
        Expect(c.Take(out item) == null && item == null, "disarmed after take");
        // the W3 defect: the original tags the object only 0.05 s later (Pickupable.<Start>); binding no longer depends on the tag
        c.Arm(itemA); c.Seen(objA); var got = c.Take(out item);
        Expect(got != null, "binding is independent of the Pickupable tag / Start timing");
    }

    static void FullRegionRefusal()
    {
        var h = Both(Madison);
        h.P1[Watch] = 100;
        for (int i = 0; i < WorldItemList.MaxPerRegion; i++) h.HostDrop(Watch, Madison, i);
        string why;
        Expect(DropGate.Decide(true, true, true, Madison, h.Wh.Items.CountIn(Madison), false, true, out why) == DropDecision.Refuse, "host gate refuses before its original drop");
        Expect(h.P1[Watch] == 100 - WorldItemList.MaxPerRegion, "P1 inventory unchanged by the refused drop");
        Expect(DropGate.Decide(true, false, true, Madison, h.Listed(Madison).Count, false, true, out why) == DropDecision.Refuse, "client gate refuses from its registry copy");
        Expect(h.Cli(Drop(1, Watch, Madison)) == ProposalCode.Busy && h.Book.Rec.CountOf(Watch) == 3 && h.Book.Rec.AppliedTxn == 0, "a forged client request is refused by the host too, record untouched");
        Expect(h.Listed(Madison).Count == WorldItemList.MaxPerRegion, "no extra untracked item");
    }

    static void ReconnectDuringDrop()
    {
        var h = Both(Madison);
        var d = Drop(1, Watch, Madison);
        Expect(h.Cli(d, 20000) == ProposalCode.Accepted, "committed, acknowledgment lost with the connection");
        h.Wh.SetPlayerRegion(2, Regions.None, 3); h.Wh.SetPlayerRegion(2, Madison, 4);
        Expect(h.Book.Rec.AppliedTxn == 1 && h.Book.Rec.CountOf(Watch) == 2, "the record says committed -> client applies its original drop once");
        Expect(h.Cli(d, 20001) == ProposalCode.Unchanged && h.Listed(Madison).Count == 1 && h.Book.Rec.CountOf(Watch) == 2, "a retried request never creates a second item or a second -1");
        var h2 = Both(Madison);                                 // not committed: refused (gap) -> nothing changed, the client retries with the same id
        Expect(h2.Cli(Drop(2, Watch, Madison), 20002) == ProposalCode.Stale && h2.Book.Rec.CountOf(Watch) == 3, "uncommitted / out-of-order: nothing changed");
        Expect(h2.Cli(Drop(1, Watch, Madison), 20003) == ProposalCode.Accepted && h2.Listed(Madison).Count == 1, "retry with the next id applies exactly once");
    }

    static void GearRulesTest()
    {
        for (int k = 0; k < 40; k++)
        {
            var s = new PlayerSnapshot { Slot = 1, Seq = (uint)(k + 1), Flags = MoveFlags.Present, Gear = GearFlags.HasLight, Swing = (byte)(k & 15) };
            var w = new NetDataWriter(); PlayerStateCodec.Write(w, s);
            var r = new NetDataReader(w.CopyData()); r.GetByte(); PlayerSnapshot d;
            Expect(PlayerStateCodec.TryRead(r, out d) && d.Swing == (k & 15) && d.Gear == GearFlags.HasLight, "swing counter round trip " + k);
        }
        Expect(!GearRules.SwingStarts(-1, 7, 1), "first sample after bind/reconnect: no replayed swing");
        Expect(GearRules.SwingStarts(7, 8, 1) && GearRules.SwingStarts(15, 0, 5), "one swing per change (wraps 15 -> 0), crowbar and baton");
        Expect(!GearRules.SwingStarts(8, 8, 1), "repeated / duplicated state: no second swing");
        Expect(!GearRules.SwingStarts(7, 8, 102) && !GearRules.SwingStarts(7, 8, 0), "brick throw / empty hands: no melee swing");
        Expect(RemoteGear_SwingCurveOk(), "swing curve bounded, ends at rest");
        Expect(!GearRules.RemoteFlashlightVisual, "W3.3: no remote flashlight presentation (bits still decoded)");
        Expect(GearRules.ToolShown(1, GearFlags.None, false) == 1 && GearRules.ToolShown(1, GearFlags.None, true) == 0 && GearRules.ToolShown(1, GearFlags.Carrying, false) == 0, "tool: in hand on foot, hidden seated / while carrying, back after exit");
    }

    // GearRules.SwingCurve (used by RemoteGear.LateFrame): raise -150, strike to -40, recover to 0 within 0.55 s
    static bool RemoteGear_SwingCurveOk()
    {
        Func<float, float> curve = GearRules.SwingCurve;
        for (float t = 0; t <= 0.6f; t += 0.01f) { float a = curve(t); if (a > 0.01f || a < -150.01f) return false; }
        return Math.Abs(curve(0.549f)) < 1f && curve(0.6f) == 0f;
    }

    static void Blackbay()
    {
        Expect(BlackbayRules.HostLockerSaleBlocked(2, 0, 3, 1), "host sale from the locker refused while P2 holds the locker");
        Expect(!BlackbayRules.HostLockerSaleBlocked(2, 1, 3, 1), "backpack serves it: allowed");
        Expect(!BlackbayRules.HostLockerSaleBlocked(1, 0, 3, 1) && !BlackbayRules.HostLockerSaleBlocked(0, 0, 3, 1), "host session / free: allowed");
    }

    // ---------------------------------------------------------------- W3.2
    static ItemTxnRequest SettleReq(uint id, int region, float x, float y, float z) { return new ItemTxnRequest { Op = ItemOp.Settle, WorldId = id, Region = region, Pos = new Vec3(x, y, z), Rot = Quat16.From(0, 0.38f, 0, 0.92f) }; }
    static ProposalCode HostSettle(Host h, uint id, int region, float x, float y, float z) { string why; return h.Wh.ItemTransaction(1, SettleReq(id, region, x, y, z), 2, out why); }

    static void Lifecycle()
    {
        var h = Both(Madison);
        // P1 -> P2: the host's original drop registers the drop point (in the air: Inventory.dropPos, menu open)
        uint a = h.HostDrop(Diamond, Madison, 3);
        var it = h.Listed(Madison)[0];
        Expect(it.Id == a && !it.Settled && it.Sim == 1 && Math.Abs(it.Pos.X - 3) < 1e-4, "P1 drop: Falling, simulated by P1, at the drop point");
        Expect(HostSettle(h, a, Madison, 3, -1.1f, 0.2f) == ProposalCode.Accepted, "P1 reports the rest pose");
        it = h.Listed(Madison)[0];
        Expect(it.Settled && Math.Abs(it.Pos.Y + 1.1f) < 1e-4, "P1 item Settled at the reported pose (what P2 now shows)");
        Expect(h.Cli(Pick(1, a, Madison)) == ProposalCode.Accepted && h.Listed(Madison).Count == 0, "P2 picks it up");
        // P2 -> P1
        Expect(h.Cli(Drop(2, Watch, Madison)) == ProposalCode.Accepted, "P2 drop committed");
        uint b = h.Listed(Madison)[0].Id;
        Expect(!h.Listed(Madison)[0].Settled && h.Listed(Madison)[0].Sim == 2, "P2 drop: Falling, simulated by P2 (the host never simulates it)");
        Expect(HostSettle(h, b, Madison, 9, 9, 9) == ProposalCode.NotAuthorized && !h.Listed(Madison)[0].Settled, "host cannot settle P2's item (one simulation)");
        Expect(h.Cli(SettleReq(b, Madison, 2, -1.2f, 0)) == ProposalCode.Accepted && h.Listed(Madison)[0].Settled, "P2 settle relayed by the host: Settled");
        string why;
        Expect(h.Wh.ItemTransaction(1, Pick(0, b, Madison), 3, out why) == ProposalCode.Accepted && h.Listed(Madison).Count == 0, "P1 picks it up");
        Expect(h.Wh.Items.Drops == 2 && h.Wh.Items.Pickups == 2, "no duplicate spawn in the registry");
    }

    static void RejoinSettled()
    {
        var h = Both(Madison);
        h.Cli(Drop(1, Watch, Madison)); uint id = h.Listed(Madison)[0].Id;
        h.Cli(SettleReq(id, Madison, 2, -0.9f, 0));
        h.Wh.SetPlayerRegion(2, Regions.None, 3);              // P2 leaves (menu / disconnect): simulation released
        var e = h.Wh.Reg.Get(StateScope.Region, Madison, WorldKeys.WorldItems);
        List<WorldItem> joined = null; Expect(e != null && WorldItemList.TryParse((byte[])e.Value.Clone(), out joined), "a joining instance parses the region value");
        Expect(joined.Count == 1 && joined[0].Settled && joined[0].Sim == 0 && Math.Abs(joined[0].Pos.Y + 0.9f) < 1e-4, "rejoin: latest rest pose, Settled, nobody simulating");
        Expect(!SettleRules.ProjectToSurface(joined[0].Settled), "the joiner shows the exact rest pose (no re-projection of a settled item)");
        // an item whose dropper left before it settled stays Falling -> every observer shows the surface estimate
        var h2 = Both(Madison); h2.Cli(Drop(1, Watch, Madison)); h2.Wh.SetPlayerRegion(2, Hideout, 3);
        Expect(!h2.Listed(Madison)[0].Settled && SettleRules.ProjectToSurface(false), "unsettled item without a simulator: surface estimate");
    }

    static void StaleSettle()
    {
        var h = Both(Madison);
        uint id = h.HostDrop(Diamond, Madison);
        Expect(h.Cli(Pick(1, id, Madison)) == ProposalCode.Accepted, "P2 claims the falling item");
        Expect(HostSettle(h, id, Madison, 1, 1, 1) == ProposalCode.Stale && h.Listed(Madison).Count == 0, "late settle after the pickup: Stale, item not resurrected");
        Expect(h.Wh.Items.Settles == 0, "nothing applied");
        uint id2 = h.HostDrop(Diamond, Madison);
        h.Wh.SetPlayerRegion(1, Hideout, 4); h.Wh.SetPlayerRegion(2, Hideout, 5);      // region empties: original reload
        Expect(HostSettle(h, id2, Hideout, 1, 1, 1) == ProposalCode.Stale && h.Listed(Madison).Count == 0, "settle after the region cleared: Stale, nothing reappears");
        var h3 = Both(Madison); uint id3 = h3.HostDrop(Diamond, Madison);
        Expect(h3.Cli(SettleReq(id3, Madison, 5, 5, 5)) == ProposalCode.NotAuthorized && !h3.Listed(Madison)[0].Settled, "P2 cannot settle P1's item");
        Expect(h3.Cli(SettleReq(id3, Hillville, 5, 5, 5)) != ProposalCode.Accepted, "no region updates from another region");
    }

    static void SurfaceRules()
    {
        var h = Both(Madison);
        uint id = h.HostDrop(Diamond, Madison);
        Expect(HostSettle(h, id, Madison, 1, 1.2f, 0) == ProposalCode.Accepted, "rest on a table (y 1.2)");
        var it = h.Listed(Madison)[0];
        Expect(it.Settled && Math.Abs(it.Pos.Y - 1.2f) < 1e-4 && !SettleRules.ProjectToSurface(it.Settled), "elevated rest pose kept exactly, never projected to the floor");
        Expect(SettleRules.ProjectToSurface(false), "falling item: projected");
        float dy;
        Expect(SettleRules.SurfaceOffset(1.5f, true, 0.2f, out dy) && Math.Abs(dy + 1.3f) < 1e-5, "drop point 1.3 m above the floor -> moved down onto it");
        Expect(SettleRules.SurfaceOffset(1.0f, true, 1.1f, out dy) && Math.Abs(dy - 0.1f) < 1e-5, "item slightly inside a surface -> lifted onto it");
        Expect(!SettleRules.SurfaceOffset(1.0f, true, 1.4f, out dy) && dy == 0f, "surface far above the item bottom: never snapped up through geometry");
        Expect(!SettleRules.SurfaceOffset(10f, true, 2f, out dy), "deeper than the cast: unchanged");
        Expect(!SettleRules.SurfaceOffset(1f, false, 0f, out dy) && !SettleRules.SurfaceOffset(1f, true, float.NaN, out dy), "no hit / invalid hit: reported pose kept (never underground)");
    }

    static void OwnerSettleRule()
    {
        string why;
        // the W3.1 false negative / false positive cases: menu open -> velocity 0 but nothing fell
        Expect(!SettleRules.OwnerShouldReport(false, true, 0, false, false, false, 0f, 0f, 0f, out why), "paused (menu open): never reported, even with velocity 0");
        Expect(!SettleRules.OwnerShouldReport(false, true, 5, true, false, false, 0f, 1f, 0f, out why), "paused even after the rigidbody is gone: not reported yet");
        Expect(!SettleRules.OwnerShouldReport(false, false, 0.05, false, false, false, 0f, 0f, 0f, out why), "just unpaused, velocity still 0: not yet (no physics step proven)");
        Expect(!SettleRules.OwnerShouldReport(false, false, 0.6, false, false, false, 4f, 0.5f, 0f, out why) && why == "falling", "falling: no report, nothing streamed");
        Expect(SettleRules.OwnerShouldReport(false, false, 0.05, true, false, false, 0f, 0f, 0f, out why), "original removed the rigidbody (Pickupable.Update at rest): reported");
        Expect(SettleRules.OwnerShouldReport(false, false, 0.3, false, true, false, 0f, 0f, 0f, out why), "asleep after running physics: first report even without displacement (marks Settled)");
        Expect(!SettleRules.OwnerShouldReport(true, false, 9, true, false, false, 0f, 0.0001f, 1f, out why), "already reported, no real movement: silent");
        Expect(SettleRules.OwnerShouldReport(true, false, 9, true, false, false, 0f, 0.01f, 0f, out why), "moved 10 cm after the report: re-reported");
        Expect(SettleRules.OwnerShouldReport(true, false, 9, true, false, false, 0f, 0f, 30f, out why), "turned 30 deg: re-reported");
    }

    static void DropWhileOpen()
    {
        // the original drop runs inside the pause menu; the gate has no pause input: the drop is authorized at once
        string why;
        Expect(DropGate.Decide(true, false, true, Madison, 0, false, true, out why) == DropDecision.ClientRequest, "client drop with the menu open: requested at once");
        Expect(DropGate.Decide(true, true, true, Madison, 0, false, true, out why) == DropDecision.HostOriginal, "host drop with the menu open: original at once");
        var h = Both(Madison);
        var req = Drop(1, Watch, Madison);
        Expect(h.Cli(req, 500) == ProposalCode.Accepted && h.Book.Rec.CountOf(Watch) == 2, "approved: record -1 once");
        Expect(h.Listed(Madison).Count == 1 && !h.Listed(Madison)[0].Settled, "item exists at once (Falling) -> the observer can show it while the dropper's menu stays open");
        h.Cli(req, 500);
        Expect(h.Book.Rec.CountOf(Watch) == 2 && h.Listed(Madison).Count == 1, "delayed duplicate of the same request: no second decrement, no second item");
        Expect(h.Cli(Drop(2, Diamond + 1000, Madison)) != ProposalCode.Accepted && h.Book.Rec.CountOf(Watch) == 2 && h.Listed(Madison).Count == 1, "rejected drop: inventory and registry unchanged");
        for (int i = 0; i < WorldItemList.MaxPerRegion + 3; i++) h.HostDrop(Diamond, Madison, i);
        Expect(h.Listed(Madison).Count == WorldItemList.MaxPerRegion, "registry capacity enforced (30 per region)");
    }

    static void SettledCodec()
    {
        var a = new WorldItem { Id = 7, ItemId = Watch, Origin = 2, OriginTxn = 4, Pos = new Vec3(1, 2, 3), Rot = Quat16.From(0, 0, 0, 1), Sim = 2, Settled = true };
        var b = new WorldItem { Id = 8, ItemId = Watch, Origin = 1, OriginTxn = 5, Pos = new Vec3(1, 2, 3), Rot = Quat16.From(0, 0, 0, 1), Sim = 0, Settled = false };
        var enc = WorldItemList.Encode(new List<WorldItem> { a, b }); List<WorldItem> back;
        Expect(enc.Length == 2 + 2 * WorldItem.WireSize && WorldItem.WireSize == 34, "record stays 34 B");
        Expect(WorldItemList.TryParse(enc, out back) && back[0].Settled && back[0].Sim == 2 && !back[1].Settled && back[1].Sim == 0, "settled bit and sim slot round trip independently");
        var bad = (byte[])enc.Clone(); bad[2 + WorldItem.WireSize - 1] = 0x83;   // settled + sim 3
        Expect(!WorldItemList.TryParse(bad, out back), "invalid sim slot refused even with the settled bit");
        var full = new List<WorldItem>(); for (int i = 1; i <= WorldItemList.MaxPerRegion; i++) full.Add(new WorldItem { Id = (uint)i, ItemId = 1, Origin = 1, Rot = Quat16.From(0, 0, 0, 1), Sim = 1, Settled = i % 2 == 0 });
        Expect(WorldItemList.Encode(full).Length <= StateEntry.MaxValueBytes, "30 items still fit one registry value");
        Expect(ProtocolInfo.ProtocolVersion >= 19, "W3.2 wire change -> protocol 19 or later (W4.1: 20)");
    }

    static void GearW32()
    {
        Expect(GearRules.GripAlongShaft == 0.5f && GearRules.GripOffset(0.265f) == 0f, "grip point = middle of the shaft");
        Expect(GearRules.PalmCentre > 0.3f && GearRules.PalmCentre < 0.7f, "palm centre between wrist and knuckles");
        Expect(GearRules.HoldArmLiftDeg == 0f, "W3.1 upper-arm lift removed");
        Expect(GearRules.SwingCurve(0.15f) == -150f && Math.Abs(GearRules.SwingCurve(0.32f) + 40f) < 1e-3 && GearRules.SwingCurve(GearRules.SwingSeconds) == 0f && GearRules.SwingSeconds == 0.55f, "swing curve unchanged (W3.1 keyframes)");
        Expect(GearRules.ToolShown(1, 0, false) == 1 && GearRules.ToolShown(1, GearFlags.Carrying, false) == 0 && GearRules.ToolShown(5, 0, true) == 0, "equipment switching: tool / carry / seated");
        var d = new DiagBudget(2); Expect(d.Want(1) && d.Want(1) && d.Want(2) && !d.Want(3) && !d.Want(0) && d.Count == 2, "diagnostics bounded to the first items");
    }

    // W3.3: the remote flashlight presentation is removed. Pure part: ownership / ON-OFF still travel in the gear byte and do
    // not change what the avatar shows (only held tool / carry). Source part (like InteractionTests case 15): the remote gear
    // code contains no mesh/material/light path for a flashlight, and no plugin file writes the local flashlight except the
    // pre-existing passenger-seat switch-off (VehicleSync, mirrors CarController.EnterVehicle).
    static void NoRemoteFlashlight()
    {
        foreach (var g in new[] { GearFlags.None, GearFlags.HasLight, GearFlags.HasLight | GearFlags.Torch, GearFlags.Torch })
        {
            var w = new NetDataWriter();
            PlayerStateCodec.Write(w, new PlayerSnapshot { Slot = 2, Gear = g, Held = 1, Swing = 3 });
            PlayerSnapshot back; var r = new NetDataReader(w.CopyData()); r.GetByte();
            Expect(PlayerStateCodec.TryRead(r, out back) && back.Gear == g && back.Held == 1 && back.Swing == 3, "gear bits " + g + " round trip unchanged (protocol 19)");
            Expect(GearRules.ToolShown(back.Held, back.Gear, false) == 1 && GearRules.ToolShown(back.Held, back.Gear, true) == 0, "flashlight state " + g + " does not change the tool presentation");
        }
        string root = AppDomain.CurrentDomain.BaseDirectory, plugin = null;
        for (int i = 0; i < 6 && root != null; i++)
        {
            var c = System.IO.Path.Combine(root, "src", "Plugin");
            if (System.IO.Directory.Exists(c)) { plugin = c; break; }
            root = System.IO.Path.GetDirectoryName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }
        Expect(plugin != null, "src/Plugin found from the test folder");
        var gear = System.IO.File.ReadAllText(System.IO.Path.Combine(plugin, "RemoteGear.cs"));
        foreach (var bad in new[] { "PolicemanAI", "new Material", "_EmissionColor", "EnableKeyword", "ChestLamp", "Lamp(", "<Light>", "typeof(Light)", "GearFlags.Torch", "GearFlags.HasLight" })
            Expect(gear.IndexOf(bad, StringComparison.Ordinal) < 0, "RemoteGear has no remote flashlight path (found '" + bad + "')");
        foreach (var f in System.IO.Directory.GetFiles(plugin, "*.cs"))
        {
            var t = System.IO.File.ReadAllText(f); var n = System.IO.Path.GetFileName(f);
            Expect(t.IndexOf("AddComponent<Light>", StringComparison.Ordinal) < 0 && t.IndexOf("AddComponent(typeof(Light))", StringComparison.Ordinal) < 0, n + ": no light source created by the mod");
            if (n != "VehicleSync.cs") Expect(t.IndexOf("Setter<bool>(ps, \"torch\")", StringComparison.Ordinal) < 0 && t.IndexOf("Setter<bool>(_psType, \"torch\")", StringComparison.Ordinal) < 0, n + ": local flashlight state not written");
        }
    }
}
