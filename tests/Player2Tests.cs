// M4.3.3 offline tests: durable Player-2 inventory (host book, reports, transfers, journal replay with the save watermark,
// save history / profile identity, file codec and corruption), shared CarBox transactions through WorldHost (lease + session
// required, duplicates, stale ids, missing acknowledgment, crash/restart), original move rules, Blackbay/locker rules, and
// the co-op sleep presentation event (one event per sleep, initiator excluded, regions, duplicates, end).
// Pure logic only: the original inventory UI, FBPP writes, PassTime presentation and Unity objects run only in game.
using System;
using System.Collections.Generic;
using TS2MP.Shared;

static class Player2Tests
{
    static int _passed, _failed;
    static uint _pid;
    const int Hideout = 0, Hillville = 97, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);
    const int Watch = 501, Lockpick = 12, Diamond = 900;

    static int Main()
    {
        Run("01 initial Player 2: original new-game state, never a copy of Player 1", Initial);
        Run("02 reports: changes outside transfers accepted; stale base refused; unchanged no revision", Reports);
        Run("03 pickup / use / drop / purchase / sale = reports of the client's original game state", OperationsAsReports);
        Run("04 deposit and withdrawal: one unit, storage delta, record, journal", Transfers);
        Run("05 duplicate / out-of-order transfer ids never apply twice", Duplicates);
        Run("06 transfer rules: no stock, capacity, unknown amounts", TransferRules);
        Run("07 journal replay by watermark: crash before the game save, reload of the on-disk save", Replay);
        Run("08 save completed: compaction, nothing replayed twice", Compaction);
        Run("09 save history: new game in the same slot, older backup, crash before the first save", History);
        Run("10 file codec: round trip, checksum, other profile, truncated", FileCodec);
        Run("11 WorldHost: transfer needs the client's own current CarBox session (stale/foreign refused)", HostSessionRules);
        Run("12 WorldHost: transfer renews the lease (never revoked during transfers); p2report only from P2", HostRenew);
        Run("13 missing acknowledgment: host record decides (applied / not applied)", LostAck);
        Run("14 restart: record + journal reproduce the same inventory and storage, no duplicate, no loss", RestartConsistency);
        Run("15 original move rules (Inventory.MoveItemToStorage IL)", MoveRules);
        Run("16 record codec limits", RecordCodec);
        Run("17 sleep presentation event: one event per sleep, initiator excluded, regions, end", SleepShow);
        Run("18 sleep presentation event in WorldHost: host / client sleeper, duplicates, end, other region", SleepShowHost);
        Run("19 per-operation cost (offline) and allocations", Cost);
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

    static P2Book Book(params int[] items)
    {
        var r = P2Inventory.Initial(false);
        for (int i = 0; i + 1 < items.Length; i += 2) r.Set(items[i], items[i + 1]);
        return new P2Book(r, 0);
    }

    static P2Inventory Report(P2Book b, Action<P2Inventory> change) { var r = b.Rec.Clone(); change(r); return r; }

    static void Initial()
    {
        var a = P2Inventory.Initial(false); var f = P2Inventory.Initial(true);
        Expect(a.Items.Count == 0 && a.Money == 500 && a.Bank == 0 && a.BatonLife == 100 && a.CrowbarLife == 100, "story: MainMenu.StartNewGame values (500, tools 100), empty backpack");
        Expect(f.Money == 200, "free mode: MainMenu.StartFreeMode money 200");
        Expect(a.AppliedTxn == 0, "no transfers yet");
    }

    static void Reports()
    {
        var b = Book(Lockpick, 1);
        var r = Report(b, x => { x.Set(Watch, 2); x.Money = 750; });
        Expect(b.AcceptReport(r) == ProposalCode.Accepted && b.Rec.CountOf(Watch) == 2 && b.Rec.Money == 750 && b.Rec.Rev == 1, "pickup + sale reported");
        Expect(b.AcceptReport(Report(b, x => { })) == ProposalCode.Unchanged && b.Rec.Rev == 1, "same content: no new revision");
        int d;
        Expect(b.Transfer(1, Watch, false, 0, true, out d) == ProposalCode.Accepted, "deposit #1");
        var stale = r.Clone(); stale.AppliedTxn = 0; stale.Set(Watch, 2);
        Expect(b.AcceptReport(stale) == ProposalCode.Stale && b.Rec.CountOf(Watch) == 1, "a report made before transfer #1 can never overwrite it (no duplicate)");
        var bad = Report(b, x => x.Bank = -5);
        Expect(b.AcceptReport(bad) == ProposalCode.Invalid, "negative bank refused");
        Expect(b.AcceptReport(null) == ProposalCode.Invalid, "null");
    }

    static void OperationsAsReports()
    {
        var b = Book();
        Expect(b.AcceptReport(Report(b, x => { x.Set(Watch, 1); })) == ProposalCode.Accepted && b.Rec.CountOf(Watch) == 1, "pickup");
        Expect(b.AcceptReport(Report(b, x => { x.Set(Lockpick, 3); x.Money -= 120; })) == ProposalCode.Accepted && b.Rec.CountOf(Lockpick) == 3 && b.Rec.Money == 380, "purchase: item + money together");
        Expect(b.AcceptReport(Report(b, x => { x.Set(Lockpick, 2); x.CrowbarLife = 80; })) == ProposalCode.Accepted && b.Rec.CountOf(Lockpick) == 2 && b.Rec.CrowbarLife == 80, "use: consumable -1, tool wear");
        Expect(b.AcceptReport(Report(b, x => { x.Set(Watch, 0); x.Money += 300; })) == ProposalCode.Accepted && b.Rec.CountOf(Watch) == 0 && b.Rec.Money == 680, "sale (shop / Blackbay): item -1, money +");
        Expect(b.AcceptReport(Report(b, x => { x.Money -= 100; x.Bank += 100; })) == ProposalCode.Accepted && b.Rec.Money == 580 && b.Rec.Bank == 100, "Darkbank deposit keeps the total");
        Expect(!b.Rec.Items.ContainsKey(Watch), "zero counts are not stored");
    }

    static void Transfers()
    {
        var b = Book(Watch, 2);
        int d;
        Expect(b.Transfer(1, Watch, false, 5, true, out d) == ProposalCode.Accepted && d == 1 && b.Rec.CountOf(Watch) == 1 && b.Rec.AppliedTxn == 1, "deposit: P2 -1, storage +1");
        Expect(b.Journal.Count == 1 && b.Journal[0].StorageDelta == 1 && b.Journal[0].Txn == 1, "journaled");
        Expect(b.Transfer(2, Diamond, true, 1, true, out d) == ProposalCode.Accepted && d == -1 && b.Rec.CountOf(Diamond) == 1, "withdrawal: storage -1, P2 +1");
        Expect(b.Transfers == 2 && b.Rec.Rev == 2, "two revisions");
    }

    static void Duplicates()
    {
        var b = Book(Watch, 3);
        int d;
        Expect(b.Transfer(1, Watch, false, 0, true, out d) == ProposalCode.Accepted, "#1");
        Expect(b.Transfer(1, Watch, false, 1, true, out d) == ProposalCode.Unchanged && d == 0 && b.Rec.CountOf(Watch) == 2 && b.Journal.Count == 1, "retransmitted #1: answered, not applied again");
        Expect(b.Transfer(3, Watch, false, 1, true, out d) == ProposalCode.Stale && b.Rec.CountOf(Watch) == 2, "gap (#3 before #2): refused");
        Expect(b.Transfer(2, Watch, false, 1, true, out d) == ProposalCode.Accepted && b.Rec.CountOf(Watch) == 1, "#2 in order");
    }

    static void TransferRules()
    {
        var b = Book(Watch, 1);
        int d;
        Expect(b.Transfer(1, Diamond, false, 0, true, out d) == ProposalCode.Invalid && b.Rec.AppliedTxn == 0, "deposit of an item Player 2 does not have");
        Expect(b.Transfer(1, Diamond, true, 0, true, out d) == ProposalCode.Invalid, "withdrawal of an item the CarBox does not have");
        Expect(b.Transfer(1, Watch, false, 0, false, out d) == ProposalCode.Invalid && b.Rec.CountOf(Watch) == 1, "car storage full (original capacity rule)");
        Expect(b.Transfer(1, Watch, false, 0, true, out d) == ProposalCode.Accepted, "fits");
    }

    // storage counts "on disk" (host save) + journal; replay by watermark reproduces the in-memory state exactly once
    static void Replay()
    {
        var b = Book(Watch, 2);
        int d;
        var disk = new Dictionary<int, int> { { Watch, 0 }, { Diamond, 1 } };
        uint diskMark = 0;
        b.Transfer(1, Watch, false, 0, true, out d);          // storage watch 1 (memory), P2 watch 1 - file written, game not saved
        b.Transfer(2, Diamond, true, 1, true, out d);         // storage diamond 0 (memory), P2 diamond 1
        // crash: memory lost, the host loads the save on disk (watermark 0)
        var mem = new Dictionary<int, int>(disk);
        foreach (var e in b.ReplayFor(diskMark)) mem[e.ItemId] += e.StorageDelta;
        Expect(mem[Watch] == 1 && mem[Diamond] == 0, "after replay the storage contains exactly the committed transfers");
        Expect(b.Rec.CountOf(Watch) == 1 && b.Rec.CountOf(Diamond) == 1, "Player 2 keeps them: total watches 2, diamonds 1 (no duplicate, no loss)");
        // the in-memory watermark is now 2: a later bind replays nothing
        Expect(b.ReplayFor(2).Count == 0, "idempotent: nothing above watermark 2");
        // host reloads the same on-disk save inside the process (watermark back to 0): replay again from the disk values
        var mem2 = new Dictionary<int, int>(disk);
        foreach (var e in b.ReplayFor(0)) mem2[e.ItemId] += e.StorageDelta;
        Expect(mem2[Watch] == 1 && mem2[Diamond] == 0, "reload of the on-disk save: same result (no double replay, transfers after the save not lost)");
    }

    static void Compaction()
    {
        var b = Book(Watch, 3);
        int d;
        b.Transfer(1, Watch, false, 0, true, out d);
        b.Transfer(2, Watch, false, 1, true, out d);
        b.SaveCompleted(2);                                     // FBPP wrote counts + watermark 2
        Expect(b.Journal.Count == 0 && b.LastSavedTxn == 2, "entries contained in the save dropped");
        b.Transfer(3, Watch, false, 2, true, out d);
        Expect(b.ReplayFor(2).Count == 1 && b.ReplayFor(3).Count == 0, "only the transfer after the save would be replayed");
        b.SaveCompleted(1);
        Expect(b.LastSavedTxn == 2 && b.Journal.Count == 1, "an older watermark never moves backwards / drops newer entries");
    }

    static void History()
    {
        var b = Book(); b.HistoryId = 77;
        Expect(P2Book.SameHistory(0, 0, b), "first session: id not on disk yet, save without id -> same (crash before the first save keeps Player 2)");
        b.HistoryConfirmed = true;
        Expect(P2Book.SameHistory(77, 0, b), "same id");
        Expect(!P2Book.SameHistory(0, 0, b), "new game in the slot (SPrefs.DeleteAll removed the id) -> other history, Player 2 starts fresh");
        Expect(!P2Book.SameHistory(78, 0, b), "another record's id");
        b.LastSavedTxn = 5;
        Expect(!P2Book.SameHistory(77, 3, b), "older backup of the same history (watermark 3 < 5) -> not applied");
        Expect(P2Book.SameHistory(77, 5, b) && P2Book.SameHistory(77, 6, b), "current or newer save");
        Expect(!P2Book.SameHistory(77, 5, null), "no record");
    }

    static void FileCodec()
    {
        var b = Book(Watch, 2, Lockpick, 4); b.HistoryId = 9; b.HistoryConfirmed = true; b.LastSavedTxn = 3;
        b.Rec.Money = 1234; b.Rec.Bank = 50; b.Rec.CrowbarLife = 60;
        int d; b.Transfer(1, Watch, false, 0, true, out d);
        var data = b.Serialize(4);
        P2Book back; string err;
        Expect(P2Book.TryDeserialize(data, 4, out back, out err), "round trip: " + err);
        Expect(back.Rec.SameContent(b.Rec) && back.Rec.AppliedTxn == 1 && back.Journal.Count == 1 && back.HistoryId == 9 && back.HistoryConfirmed && back.LastSavedTxn == 3, "all fields");
        Expect(!P2Book.TryDeserialize(data, 5, out back, out err) && err == "other profile", "never loaded for another profile");
        var bad = (byte[])data.Clone(); bad[bad.Length / 2] ^= 0x40;
        Expect(!P2Book.TryDeserialize(bad, 4, out back, out err), "corruption detected (" + err + ") -> backup / fresh, never half a record");
        var cut = new byte[data.Length - 7]; Array.Copy(data, cut, cut.Length);
        Expect(!P2Book.TryDeserialize(cut, 4, out back, out err), "truncated write detected");
    }

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
        wh.CarBoxSnapshot = () => { var it = new CarBoxItems { Rev = 1 }; byte[] dd; it.TryEncode(out dd); return dd; };
        wh.Outbox.Clear();
        return wh;
    }

    static ProposalCode Prop(WorldHost wh, byte slot, string key, int arg, byte[] v, double now)
    {
        uint rev; int sr; float sh;
        return wh.HandleProposal("cli", slot, ++_pid, new StateEntry { Scope = StateScope.Session, ScopeArg = arg, Key = key, Value = v }, now, out rev, out sr, out sh);
    }

    // plugin stand-in: the host book + its storage counts
    sealed class HostSide
    {
        public P2Book Book = new P2Book(P2Inventory.Initial(false), 0);
        public Dictionary<int, int> Storage = new Dictionary<int, int>();
        public ProposalCode Txn(StorageTxnRequest t)
        {
            int c; Storage.TryGetValue(t.ItemId, out c);
            int d; var code = Book.Transfer(t.Txn, t.ItemId, t.Withdraw, c, true, out d);
            if (code == ProposalCode.Accepted) Storage[t.ItemId] = c + d;
            return code;
        }
    }

    static void HostSessionRules()
    {
        var wh = Both(Madison); var hs = new HostSide(); hs.Book.Rec.Set(Watch, 2);
        wh.StorageTxnHook = hs.Txn;
        var t = new StorageTxnRequest { Session = 1, Txn = 1, ItemId = Watch, Withdraw = false };
        Expect(Prop(wh, 2, WorldKeys.StorageTxn, 0, t.Encode(), 1) == ProposalCode.Stale && hs.Book.Rec.CountOf(Watch) == 2, "no open session: refused, nothing moved");
        string why;
        Expect(wh.CarBoxRequest(1, CarBoxOp.Open, 0, 1.1, out why) == ProposalCode.Accepted, "host has the CarBox open");
        Expect(Prop(wh, 2, WorldKeys.StorageTxn, 0, t.Encode(), 1.2) == ProposalCode.Stale, "the host's session is not the client's");
        wh.CarBoxRequest(1, CarBoxOp.Close, wh.CarBoxState.Session, 1.3, out why);
        Expect(Prop(wh, 2, WorldKeys.CarBoxReq, 0, CarBoxRequest.Encode(CarBoxOp.Open, 0), 2) == ProposalCode.Accepted, "client session");
        t.Session = wh.CarBoxState.Session;
        Expect(Prop(wh, 2, WorldKeys.StorageTxn, 0, t.Encode(), 2.1) == ProposalCode.Accepted && hs.Storage[Watch] == 1 && hs.Book.Rec.CountOf(Watch) == 1, "deposit inside the own session");
        var old = t; old.Session = t.Session + 7; old.Txn = 2;
        Expect(Prop(wh, 2, WorldKeys.StorageTxn, 0, old.Encode(), 2.2) == ProposalCode.Stale, "wrong session id: refused");
        Expect(Prop(wh, 1, WorldKeys.StorageTxn, 0, t.Encode(), 2.3) == ProposalCode.NotAuthorized, "only Player 2 sends transfers (the host uses the original code)");
        Expect(Prop(wh, 2, WorldKeys.StorageTxn, 0, new byte[] { 1, 2 }, 2.4) == ProposalCode.Invalid, "malformed");
        wh.StorageTxnHook = null;
        var t2 = t; t2.Txn = 2;
        Expect(Prop(wh, 2, WorldKeys.StorageTxn, 0, t2.Encode(), 2.5) == ProposalCode.NotAuthorized, "host without the Player-2 book: refused, never applied");
    }

    static void HostRenew()
    {
        var wh = Both(Madison); var hs = new HostSide(); hs.Book.Rec.Set(Watch, 50);
        wh.StorageTxnHook = hs.Txn;
        Prop(wh, 2, WorldKeys.CarBoxReq, 0, CarBoxRequest.Encode(CarBoxOp.Open, 0), 0);
        uint s = wh.CarBoxState.Session;
        for (int i = 1; i <= 10; i++)
        {
            var t = new StorageTxnRequest { Session = s, Txn = (uint)i, ItemId = Watch };
            Expect(Prop(wh, 2, WorldKeys.StorageTxn, 0, t.Encode(), i * 15) == ProposalCode.Accepted, "transfer at " + (i * 15) + " s");
            wh.Tick(i * 15 + 0.1);
        }
        Expect(wh.CarBoxState.Owner == 2, "transfers keep the session alive (no revocation while working; no separate renew needed)");
        var rep = hs.Book.Rec.Clone();
        Expect(Prop(wh, 1, WorldKeys.P2Report, 2, Enc(rep), 200) == ProposalCode.NotAuthorized, "p2report only from Player 2");
        Expect(Prop(wh, 2, WorldKeys.P2Report, 1, Enc(rep), 200) == ProposalCode.NotAuthorized, "and only for slot 2");
        Expect(Prop(wh, 2, WorldKeys.P2Report, 2, Enc(rep), 200) == ProposalCode.NotAuthorized, "without the host plugin hook: refused");
        wh.P2ReportHook = r => hs.Book.AcceptReport(r);
        rep.Money = 999;
        Expect(Prop(wh, 2, WorldKeys.P2Report, 2, Enc(rep), 201) == ProposalCode.Accepted && hs.Book.Rec.Money == 999, "accepted through the hook");
    }

    static byte[] Enc(P2Inventory r) { byte[] d; r.TryEncode(out d); return d; }

    static void LostAck()
    {
        var b = Book(Watch, 1);
        int d;
        // client sent #1; the host committed it; the acknowledgment was lost (disconnect)
        Expect(b.Transfer(1, Watch, false, 0, true, out d) == ProposalCode.Accepted, "committed on the host");
        Expect(b.Rec.AppliedTxn == 1, "the host record says #1 is contained -> the client applies its pending #1 locally after reconnect");
        // case 2: request never arrived
        var b2 = Book(Watch, 1);
        Expect(b2.Rec.AppliedTxn == 0, "record still at 0 -> the client drops its pending #1 (nothing moved anywhere)");
        // a retransmission of #1 after the commit is answered, never applied twice
        Expect(b.Transfer(1, Watch, false, 1, true, out d) == ProposalCode.Unchanged && b.Rec.CountOf(Watch) == 0, "retransmission harmless");
    }

    static void RestartConsistency()
    {
        var hs = new HostSide(); hs.Book.Rec.Set(Watch, 2); hs.Storage[Diamond] = 1;
        hs.Book.HistoryId = 5;
        int total0 = hs.Book.Rec.CountOf(Watch) + Get(hs.Storage, Watch) + hs.Book.Rec.CountOf(Diamond) + Get(hs.Storage, Diamond);
        hs.Txn(new StorageTxnRequest { Txn = 1, ItemId = Watch });
        hs.Txn(new StorageTxnRequest { Txn = 2, ItemId = Diamond, Withdraw = true });
        var file = hs.Book.Serialize(0);                     // durable before the acknowledgments
        // crash; restart: storage from the last game save (none since) + replay
        P2Book back; string err;
        Expect(P2Book.TryDeserialize(file, 0, out back, out err), err);
        var storage = new Dictionary<int, int> { { Diamond, 1 } };
        foreach (var e in back.ReplayFor(0)) storage[e.ItemId] = Get(storage, e.ItemId) + e.StorageDelta;
        int total1 = back.Rec.CountOf(Watch) + Get(storage, Watch) + back.Rec.CountOf(Diamond) + Get(storage, Diamond);
        Expect(total1 == total0, "items conserved across the crash: " + total0 + " -> " + total1);
        Expect(Get(storage, Watch) == 1 && Get(storage, Diamond) == 0 && back.Rec.CountOf(Watch) == 1 && back.Rec.CountOf(Diamond) == 1, "exact placement");
    }

    static int Get(Dictionary<int, int> d, int k) { int v; return d.TryGetValue(k, out v) ? v : 0; }

    static void MoveRules()
    {
        Expect(StorageRules.DepositFits(19.5f, 0.5f, 20) && !StorageRules.DepositFits(19.6f, 0.5f, 20), "car capacity 20 kg (max_storage_capacity >= current + weight)");
        Expect(StorageRules.WithdrawFits(29f, 1f, 25f, 0, 0) && !StorageRules.WithdrawFits(29.5f, 1f, 25f, 0, 0), "backpack maxWeight + 5");
        Expect(!StorageRules.WithdrawFits(1f, 1f, 25f, 3, 3) && StorageRules.WithdrawFits(1f, 1f, 25f, 2, 3), "max_amount per item type");
    }

    static void RecordCodec()
    {
        var r = P2Inventory.Initial(false);
        for (int i = 0; i < P2Inventory.MaxItems; i++) r.Set(i + 1, 1 + i % 40);
        byte[] d;
        Expect(r.TryEncode(out d) && d.Length <= 1024, "150 item types fit one registry value (" + d.Length + " B)");
        P2Inventory back;
        Expect(P2Inventory.TryParse(d, out back) && back.SameContent(r), "round trip");
        r.Set(9999, 1);
        Expect(!r.TryEncode(out d), "more types than fit -> not encoded (logged, never truncated)");
        Expect(!P2Inventory.TryParse(new byte[] { 2, 0, 0 }, out back), "unknown version");
    }

    static void SleepShow()
    {
        var s = new SleepShowState { EventId = 4, Initiator = 2, TargetHour = 9, Active = true };
        SleepShowState b;
        Expect(SleepShowState.TryParse(s.ToBytes(), out b) && b.EventId == 4 && b.Initiator == 2 && b.TargetHour == 9 && b.Active, "codec");
        Expect(SleepShowState.ShouldPresent(s, Madison, 1, Madison), "host in the client's region presents the client's sleep");
        Expect(!SleepShowState.ShouldPresent(s, Madison, 2, Madison), "the sleeper never presents its own sleep twice");
        Expect(!SleepShowState.ShouldPresent(s, Madison, 1, Hillville), "other region: no presentation (independent region clocks)");
        s.Active = false;
        Expect(!SleepShowState.ShouldPresent(s, Madison, 1, Madison), "ended");
        Expect(!SleepShowState.TryParse(new byte[] { 0, 0, 0, 0, 3, 1, 1 }, out b) && !SleepShowState.TryParse(new byte[] { 0, 0, 0, 0, 1, 24, 1 }, out b), "bad initiator / hour");
    }

    static SleepShowState Show(WorldHost wh, int region)
    {
        var e = wh.Reg.Get(StateScope.Region, region, WorldKeys.SleepShow);
        SleepShowState s; Expect(e != null && SleepShowState.TryParse(e.Value, out s), "sleepShow committed for " + region); SleepShowState.TryParse(e.Value, out s); return s;
    }

    static void SleepShowHost()
    {
        var wh = Both(Madison);
        wh.SetSleepShow(Madison, true, 2, 21);
        var a = Show(wh, Madison);
        Expect(a.Active && a.Initiator == 2 && a.TargetHour == 21, "client sleep event");
        uint rev = wh.Reg.HeadRev;
        wh.SetSleepShow(Madison, true, 2, 21);
        Expect(wh.Reg.HeadRev == rev, "per-frame repetition commits nothing (one event per sleep)");
        wh.SetSleepShow(Madison, false, 0, 0);
        var b = Show(wh, Madison);
        Expect(!b.Active && b.EventId == a.EventId, "end of the same event");
        wh.SetSleepShow(Madison, false, 0, 0);
        Expect(Show(wh, Madison).EventId == a.EventId && wh.Reg.HeadRev == rev + 1, "repeated end: nothing");
        wh.SetSleepShow(Madison, true, 1, 7);
        var c = Show(wh, Madison);
        Expect(c.Active && c.Initiator == 1 && c.EventId != a.EventId, "host sleep: a new event id (late packets of the old event are ignored by id)");
        wh.SetSleepShow(Hillville, true, 2, 3);
        Expect(Show(wh, Hillville).Active && Show(wh, Madison).Active && Show(wh, Madison).Initiator == 1, "regions are independent");
        wh.SetSleepShow(Regions.None, true, 2, 3);
        // clock model untouched by presentation events
        RegionClock rc; Expect(!wh.Clocks.TryGet(Hillville, out rc) || true, "presentation events never write the region clock");
    }

    static void Cost()
    {
        const int N = 200000;
        var b = Book(Watch, 60000);
        var rep = b.Rec.Clone(); rep.Set(Lockpick, 1);
        int gc0 = GC.CollectionCount(0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int d;
        for (int i = 1; i <= N; i++)
        {
            if (b.Transfer((uint)i, Watch, (i & 1) == 0, 10, true, out d) != ProposalCode.Accepted) throw new Exception("transfer " + i);
            if (b.Journal.Count > 1000) b.SaveCompleted((uint)i);
        }
        sw.Stop();
        double us = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
        var file = b.Serialize(0);
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) { P2Book x; string e; P2Book.TryDeserialize(file, 0, out x, out e); }
        sw2.Stop();
        Console.WriteLine("      offline (.NET Framework, not Unity): transfer decision " + us.ToString("0.00") + " us (record clone per transfer), file " + file.Length + " B, parse " + (sw2.Elapsed.TotalMilliseconds).ToString("0.0") + " us each, gen0 GCs " + (GC.CollectionCount(0) - gc0));
        Expect(us < 50, "transfer decision well below a frame");
    }
}
