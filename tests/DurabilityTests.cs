// M5 W3.4 offline tests: CarBox / hideout-locker durability against a host quit without the original save.
// The model mirrors the host exactly as the plugin drives it (src/Plugin/Player2Sync.cs HostTransfer / HostBind /
// SavePrefix+SavePostfix / DepositPostfix / GameSavePrefix), on the pure pieces it uses (P2Book.HostTransfer, ReplayAll,
// HostDepositLedger, Serialize/TryDeserialize):
//   Mem  = FBPP save memory (SPrefs keys), Disk = the save file on disk, File = the durable p2inv file,
//   live = ItemID fields (item_amount = Player 1, item_amount_storage = car, item_amount_locker = locker).
//   GameSave = SaveGameNewSystem (Save_Item_Amount: live -> keys, then FBPP write); StraySave = an FBPP write without it;
//   Reload = quit without saving + load (memory from disk, live from keys = ItemID.Start, then HostBind replay).
// Verified IL behind the model: src/Shared/Player2Inventory.cs "W3.4 durability model". Unity, Harmony and the real save
// file run only in game (manual QA, docs/TESTING.md section 24).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class DurabilityTests
{
    static int _passed, _failed;
    const int X = 12, Y = 30;                 // two item types
    const byte Car = StorageKind.Car, Lock = StorageKind.Locker;

    static int Main()
    {
        Run("00 reproduction: the W3.3 behaviour duplicates (CarBox deposit -> P2 withdraw -> quit without save)", Reproduction);
        Run("A  CarBox: P1 deposit, P2 withdraw, quit without save -> exactly one item, owned by P2", CaseA);
        Run("B  locker: same with the hideout locker (never replayed against the car storage)", CaseB);
        Run("C  original save after the transfer: no replay, no duplicate, idempotent reloads", CaseC);
        Run("D  unconsumed P1 deposit + quit without save: original rollback, nothing journaled", CaseD);
        Run("E  withdrawal of a unit already in the saved storage: no invented P1 deposit", CaseE);
        Run("F  multiple quantities: saved stock first, then P1 deposits, partial returns, mid-session save", CaseF);
        Run("G  interrupted persistence: failed write, crash before/after write, duplicate retry, reconnect", CaseG);
        Run("H  watermarks: stray FBPP write, save then reload, profile reload twice, unattributed unit refused", CaseH);
        Run("I  p2inv migration v1/v2 -> v3: record, money, bank, journal, host sequence, watermarks preserved", CaseI);
        Run("J  interplay with the W3 dropped-item host journal and both storages in one session", CaseJ);
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

    // ------------------------------------------------------------------------------------------------ host model
    sealed class Sim
    {
        public Dictionary<string, int> Mem = new Dictionary<string, int>(), Disk;
        public Dictionary<int, int> P1 = new Dictionary<int, int>(), CarS = new Dictionary<int, int>(), LockS = new Dictionary<int, int>();
        public HostDepositLedger L = new HostDepositLedger();
        public P2Book Book; public byte[] File;
        public bool Legacy, FailNextWrite;
        const string W = "TS2MP_StorageTxn", H = "TS2MP_HostTxn", Hist = "TS2MP_P2History";

        public Sim(int p1x, int carx, int lockx, int p2x = 0)
        {
            P1[X] = p1x; CarS[X] = carx; LockS[X] = lockx; P1[Y] = 2; CarS[Y] = 0; LockS[Y] = 0;
            Book = new P2Book(P2Inventory.Initial(false), 0) { HistoryId = 5 }; Book.Rec.Id = 5; Book.Rec.Rev = 1; Book.Rec.Set(X, p2x);
            Mem[Hist] = 5; GameSave(); Book.HistoryConfirmed = true; File = Book.Serialize(0);
        }

        public int Get(string k) { int v; return Mem.TryGetValue(k, out v) ? v : 0; }
        static int G(Dictionary<int, int> d, int k) { int v; return d.TryGetValue(k, out v) ? v : 0; }
        public Dictionary<int, int> Store(byte kind) { return kind == Lock ? LockS : CarS; }
        public int Total(int id) { return G(P1, id) + G(CarS, id) + G(LockS, id) + Book.Rec.CountOf(id); }
        public int P1Of(int id) { return G(P1, id); }
        public int CarOf(int id) { return G(CarS, id); }
        public int LockOf(int id) { return G(LockS, id); }
        public int P2Of(int id) { return Book.Rec.CountOf(id); }
        public uint NextTxn { get { return Book.Rec.AppliedTxn + 1; } }

        // Inventory.MoveItemToStorage -> ItemID.Move_To_Storage(kind) (fields only) + host DepositPostfix
        public void P1Deposit(byte kind, int id) { Expect(G(P1, id) > 0, "P1 owns the item"); P1[id] = G(P1, id) - 1; Store(kind)[id] = G(Store(kind), id) + 1; L.Deposit(kind, id); }
        public void P1Withdraw(byte kind, int id) { Expect(G(Store(kind), id) > 0, "stock"); Store(kind)[id] = G(Store(kind), id) - 1; P1[id] = G(P1, id) + 1; L.Withdraw(kind, id); }
        public void UnattributedAdd(byte kind, int id) { Store(kind)[id] = G(Store(kind), id) + 1; }   // e.g. CMS_Part.AddPartToLocker

        // Player2Sync.HostTransfer (W3.4); Legacy = the W3.3 code (key = live + delta, no prerequisite)
        public ProposalCode P2(byte kind, int id, bool withdraw, uint txn = 0)
        {
            if (txn == 0) txn = NextTxn;
            var st = Store(kind); int count = G(st, id);
            string sk = StorageKind.KeyPrefix(kind) + id, ik = "Item_" + id;
            if (Legacy)
            {
                int d; var c = Book.Transfer(txn, kind, id, withdraw, count, true, true, out d);
                if (c != ProposalCode.Accepted) return c;
                st[id] = count + d; Mem[sk] = count + d; Mem[W] = (int)txn; File = Book.Serialize(0); return c;
            }
            int stoKey = Get(sk), itemKey = Get(ik), prevW = Get(W), prevH = Get(H);
            int deposits = L.Count(kind, id);
            var mark = Book.Mark(); StoragePlan plan;
            var code = Book.HostTransfer(txn, kind, id, withdraw, count, stoKey, itemKey, deposits, true, true, out plan);
            if (code != ProposalCode.Accepted) return code;
            st[id] = count + plan.StorageDelta; Mem[sk] = plan.NewStorageKey; Mem[W] = (int)txn;
            if (plan.Prerequisite) { L.Consume(kind, id); Mem[ik] = plan.NewItemKey; Mem[H] = (int)plan.PrereqSeq; }
            if (FailNextWrite)
            {
                FailNextWrite = false;
                st[id] = count; Mem[sk] = stoKey; Mem[W] = prevW;
                if (plan.Prerequisite) { L.Restore(kind, id); Mem[ik] = itemKey; Mem[H] = prevH; }
                Book.Restore(mark);
                return ProposalCode.Busy;
            }
            File = Book.Serialize(0);
            return code;
        }

        // SaveGame.SaveGameNewSystem: Save_Item_Amount for every item, then the FBPP write
        public void GameSave()
        {
            foreach (var id in new[] { X, Y }) { Mem["Item_" + id] = G(P1, id); Mem["Storage_Item_" + id] = G(CarS, id); Mem["Locker_Item_" + id] = G(LockS, id); }
            FbppWrite(true);
        }
        public void StraySave() { FbppWrite(false); }   // e.g. SPrefs.Save: keys as they are
        void FbppWrite(bool gameSave)
        {
            uint sw = (uint)Math.Max(0, Get(W)), sh = (uint)Math.Max(0, Get(H));   // SavePrefix
            Disk = new Dictionary<string, int>(Mem);
            if (gameSave) L.Clear();                                                // SavePostfix inside the original save
            if (Book != null) { Book.SaveCompleted(sw, sh); File = Book.Serialize(0); }
        }

        // Quit without saving (or crash) + load the save: memory from disk, live from keys, book from the p2inv file, replay
        public int Reload()
        {
            Mem = new Dictionary<string, int>(Disk);
            P2Book b; string err;
            Expect(P2Book.TryDeserialize(File, 0, out b, out err), "p2inv file loads: " + err);
            uint mark = (uint)Math.Max(0, Get(W)), hmark = (uint)Math.Max(0, Get(H));
            Expect(P2Book.SameHistory(Get(Hist), mark, b), "same save history (record not quarantined)");
            Book = b;
            foreach (var id in new[] { X, Y }) { P1[id] = Get("Item_" + id); CarS[id] = Get("Storage_Item_" + id); LockS[id] = Get("Locker_Item_" + id); }
            L.Clear();
            if (hmark > Book.HostSeq) Book.HostSeq = hmark;
            List<JournalEntry> list;
            if (Legacy) { list = Book.ReplayFor(mark); list.AddRange(Book.ReplayHost(hmark)); }
            else { uint mt, mh; list = Book.ReplayAll(mark, hmark, out mt, out mh); if (mt > mark) Mem[W] = (int)mt; if (mh > hmark) Mem[H] = (int)mh; }
            foreach (var e in list)
            {
                string key = JournalKind.KeyPrefix(e.Kind) + e.ItemId;
                int v = Math.Max(0, Get(key) + e.StorageDelta); Mem[key] = v;
                if (JournalKind.IsLockerKey(e.Kind)) LockS[e.ItemId] = v; else if (e.Kind == JournalKind.HostItem) P1[e.ItemId] = v; else CarS[e.ItemId] = v;
            }
            return list.Count;
        }
    }

    // ------------------------------------------------------------------------------------------------ cases
    static void Reproduction()
    {
        var s = new Sim(1, 0, 0) { Legacy = true };
        s.P1Deposit(Car, X);
        Expect(s.P2(Car, X, true) == ProposalCode.Accepted && s.Total(X) == 1, "live: one item, now P2's");
        s.Reload();
        Expect(s.P1Of(X) == 1 && s.P2Of(X) == 1 && s.Total(X) == 2, "W3.3 model reproduces the duplicate (P1 " + s.P1Of(X) + ", P2 " + s.P2Of(X) + ")");
    }

    static void CaseA()
    {
        var s = new Sim(1, 0, 0);
        s.P1Deposit(Car, X);
        Expect(s.P2(Car, X, true) == ProposalCode.Accepted, "withdrawal committed");
        Expect(s.Book.Prerequisites == 1 && s.L.Count(Car, X) == 0, "one prerequisite P1 deposit journaled, ledger consumed");
        var j = s.Book.Journal;
        Expect(j.Count == 3 && j[0].Kind == JournalKind.HostItem && j[0].StorageDelta == -1 && j[1].Kind == JournalKind.HostCar && j[1].StorageDelta == 1 && j[2].Kind == JournalKind.Car && j[2].StorageDelta == -1 && j[0].Txn == j[1].Txn,
            "journal order: P1 backpack -1, car +1 (same host seq), then the withdrawal");
        s.Reload();
        Expect(s.P1Of(X) == 0 && s.CarOf(X) == 0 && s.P2Of(X) == 1 && s.Total(X) == 1, "after the unsaved quit: one item, owned by P2 (P1 " + s.P1Of(X) + ", car " + s.CarOf(X) + ", P2 " + s.P2Of(X) + ")");
        Expect(s.Reload() == 3 && s.Total(X) == 1 && s.P2Of(X) == 1, "a second reload of the same old save gives the same result");
    }

    static void CaseB()
    {
        var s = new Sim(1, 0, 0);
        s.P1Deposit(Lock, X);
        Expect(s.P2(Lock, X, true) == ProposalCode.Accepted, "locker withdrawal committed");
        Expect(s.Book.Journal[1].Kind == JournalKind.HostLocker && s.Book.Journal[2].Kind == JournalKind.Locker, "prerequisite names the locker");
        s.Reload();
        Expect(s.P1Of(X) == 0 && s.LockOf(X) == 0 && s.CarOf(X) == 0 && s.P2Of(X) == 1 && s.Total(X) == 1, "one item, P2's; the car storage untouched");
        var t = new Sim(1, 0, 0); t.P1Deposit(Car, X);
        Expect(t.P2(Lock, X, true) != ProposalCode.Accepted && t.P2Of(X) == 0, "a car deposit never backs a locker withdrawal (live locker empty)");
        t.UnattributedAdd(Lock, X);
        Expect(t.P2(Lock, X, true) == ProposalCode.Invalid && t.P2Of(X) == 0 && t.CarOf(X) == 1, "nor through the ledger of the other storage: refused, nothing changed");
    }

    static void CaseC()
    {
        var s = new Sim(1, 0, 0);
        s.P1Deposit(Car, X); s.P2(Car, X, true);
        s.GameSave();
        Expect(s.Book.Journal.Count == 0, "the save contains everything: journal compacted");
        Expect(s.Reload() == 0 && s.P1Of(X) == 0 && s.CarOf(X) == 0 && s.P2Of(X) == 1 && s.Total(X) == 1, "reload after the save: no replay, one item");
        Expect(s.Reload() == 0 && s.Total(X) == 1, "idempotent");
        var u = new Sim(1, 0, 0); u.P1Deposit(Car, X); u.P2(Car, X, true);
        uint before = u.Book.HostSeq; u.GameSave(); u.P1Deposit(Car, Y); u.Reload();
        Expect(u.P1Of(X) == 0 && u.P2Of(X) == 1 && u.P1Of(Y) == 2 && u.CarOf(Y) == 0 && u.Book.HostSeq >= before, "later unsaved P1 deposit of another item rolls back, the saved transfer stays");
    }

    static void CaseD()
    {
        var s = new Sim(1, 0, 0);
        s.P1Deposit(Car, X); s.P1Deposit(Lock, Y);
        Expect(s.Book.Journal.Count == 0, "P1's own moves are never journaled");
        s.Reload();
        Expect(s.P1Of(X) == 1 && s.CarOf(X) == 0 && s.P1Of(Y) == 2 && s.LockOf(Y) == 0 && s.L.Entries == 0, "original unsaved rollback");
    }

    static void CaseE()
    {
        var s = new Sim(1, 2, 0);
        Expect(s.P2(Car, X, true) == ProposalCode.Accepted && s.Book.Prerequisites == 0, "saved stock: no prerequisite");
        s.P1Deposit(Car, X);                                   // P1's unsaved unit stays its own (newest deposit NOT consumed)
        Expect(s.P2(Car, X, true) == ProposalCode.Accepted && s.Book.Prerequisites == 0, "second unit still from the saved stock");
        s.Reload();
        Expect(s.P1Of(X) == 1 && s.CarOf(X) == 0 && s.P2Of(X) == 2 && s.Total(X) == 3, "P1 keeps its saved unit, P2 has the two saved storage units");
    }

    static void CaseF()
    {
        var s = new Sim(3, 2, 0);                              // disk: P1 3, car 2 -> total 5
        s.P1Deposit(Car, X); s.P1Deposit(Car, X);              // live: P1 1, car 4
        for (int i = 0; i < 3; i++) Expect(s.P2(Car, X, true) == ProposalCode.Accepted, "withdraw " + (i + 1));
        Expect(s.Book.Prerequisites == 1 && s.L.Count(Car, X) == 1, "two from the saved stock, the third backed by one P1 deposit");
        Expect(s.P2(Car, X, false) == ProposalCode.Accepted, "P2 returns one unit");
        Expect(s.Total(X) == 5, "live total 5");
        s.Reload();
        Expect(s.P1Of(X) == 2 && s.CarOf(X) == 1 && s.P2Of(X) == 2 && s.Total(X) == 5, "recovered: P1 2 (one deposit became durable), car 1, P2 2 -> total 5 (P1 " + s.P1Of(X) + ", car " + s.CarOf(X) + ", P2 " + s.P2Of(X) + ")");
        // mid-session original save between the transfers
        var m = new Sim(2, 0, 0);
        m.P1Deposit(Car, X); m.P2(Car, X, true); m.GameSave();
        m.P1Deposit(Car, X); m.P2(Car, X, true);
        Expect(m.Book.Prerequisites == 2 && m.Book.Journal.Count == 3 && m.Book.Journal[2].Kind == JournalKind.Car, "only the transfer after the save is still journaled (with its prerequisite); the first was compacted by the save");
        m.Reload();
        Expect(m.P1Of(X) == 0 && m.CarOf(X) == 0 && m.P2Of(X) == 2 && m.Total(X) == 2, "both units P2's, none duplicated");
        // P1 takes a unit back before P2 withdraws
        var w = new Sim(2, 0, 0);
        w.P1Deposit(Car, X); w.P1Deposit(Car, X); w.P1Withdraw(Car, X);
        Expect(w.L.Count(Car, X) == 1 && w.P2(Car, X, true) == ProposalCode.Accepted && w.P2(Car, X, true) != ProposalCode.Accepted, "net one deposit backs one withdrawal; the live stock is then empty");
        w.Reload();
        Expect(w.P1Of(X) == 1 && w.P2Of(X) == 1 && w.Total(X) == 2, "never more P1 deductions than confirmed withdrawals needed");
    }

    static void CaseG()
    {
        var s = new Sim(1, 0, 0);
        s.P1Deposit(Car, X);
        s.FailNextWrite = true;
        Expect(s.P2(Car, X, true) == ProposalCode.Busy, "durable write fails: Busy");
        Expect(s.P2Of(X) == 0 && s.CarOf(X) == 1 && s.Book.Journal.Count == 0 && s.L.Count(Car, X) == 1 && s.Get("Storage_Item_" + X) == 0 && s.Get("Item_" + X) == 1 && s.Book.HostSeq == 0,
            "nothing changed: record, live, journal, ledger, keys, host sequence");
        uint t = s.NextTxn;
        Expect(s.P2(Car, X, true, t) == ProposalCode.Accepted, "client retries the same transaction: committed once");
        Expect(s.P2(Car, X, true, t) == ProposalCode.Unchanged && s.Book.Prerequisites == 1 && s.P2Of(X) == 1, "retransmission after a lost ack: Unchanged, no second prerequisite");
        s.Reload();                                            // crash after the durable write, before / after the ack
        Expect(s.Total(X) == 1 && s.P2Of(X) == 1, "crash after the write: the grant is reproduced, no duplicate");
        Expect(s.P2(Car, X, true, t) == ProposalCode.Unchanged, "reconnect + resend after the restart: still Unchanged");
        var c = new Sim(1, 0, 0); c.P1Deposit(Car, X); c.FailNextWrite = true; c.P2(Car, X, true);
        c.Reload();                                            // crash before any durable write
        Expect(c.P1Of(X) == 1 && c.CarOf(X) == 0 && c.P2Of(X) == 0 && c.Total(X) == 1, "crash before the write: original rollback, P2 got nothing");
        var o = new Sim(1, 0, 0); o.P1Deposit(Car, X);
        Expect(o.P2(Car, X, true, 7) == ProposalCode.Stale && o.Book.Journal.Count == 0 && o.L.Count(Car, X) == 1, "out-of-order transaction: nothing journaled");
    }

    static void CaseH()
    {
        var s = new Sim(1, 0, 0);
        s.P1Deposit(Car, X); s.P2(Car, X, true);
        s.StraySave();                                         // FBPP write without Save_Item_Amount: keys as written by the mod
        Expect(s.Disk["Item_" + X] == 0 && s.Disk["Storage_Item_" + X] == 0 && s.Book.Journal.Count == 0, "the stray write already contains the prerequisite effect (keys = recoverable)");
        Expect(s.Reload() == 0 && s.P1Of(X) == 0 && s.CarOf(X) == 0 && s.P2Of(X) == 1, "no replay, consistent");
        Expect(s.Reload() == 0 && s.Total(X) == 1, "profile reloaded again: unchanged");
        // a P1 deposit saved by the original save is recoverable stock afterwards: no prerequisite
        var v = new Sim(1, 0, 0); v.P1Deposit(Car, X); v.GameSave();
        Expect(v.L.Entries == 0 && v.P2(Car, X, true) == ProposalCode.Accepted && v.Book.Prerequisites == 0, "deposit saved -> plain withdrawal");
        v.Reload(); Expect(v.P1Of(X) == 0 && v.P2Of(X) == 1 && v.Total(X) == 1, "consistent after reload");
        // stray write BEFORE the transfer does not clear the ledger (it did not save the live counts)
        var w = new Sim(1, 0, 0); w.P1Deposit(Car, X); w.StraySave();
        Expect(w.L.Count(Car, X) == 1 && w.P2(Car, X, true) == ProposalCode.Accepted && w.Book.Prerequisites == 1, "stray write keeps the deposit attributed");
        w.Reload(); Expect(w.Total(X) == 1 && w.P2Of(X) == 1, "no duplicate");
        // unit neither saved nor a P1 deposit
        var u = new Sim(1, 0, 0); u.UnattributedAdd(Lock, Y);
        Expect(u.P2(Lock, Y, true) == ProposalCode.Invalid && u.P2Of(Y) == 0 && u.LockOf(Y) == 1 && u.Book.Journal.Count == 0, "refused until saved, nothing changed");
        u.GameSave(); Expect(u.P2(Lock, Y, true) == ProposalCode.Accepted, "after the original save it is recoverable stock");
    }

    static void CaseI()
    {
        // v1 file (0.7.x-0.9.x): no host fields, record v1 (no id), journal entries without kind
        var rec = new NetDataWriter();
        rec.Put((byte)1); rec.Put((uint)9); rec.Put((uint)4); rec.Put(1234); rec.Put(5678); rec.Put(80); rec.Put(70); rec.Put((ushort)2); rec.Put(X); rec.Put((ushort)3); rec.Put(Y); rec.Put((ushort)1);
        var v1 = Wrap(1, w => { w.Put(0); w.Put((uint)3); w.Put(77); w.Put(true); w.Put(rec.CopyData()); w.Put(1); w.Put((uint)4); w.Put(X); w.Put(-1); });
        P2Book b; string err;
        Expect(P2Book.TryDeserialize(v1, 0, out b, out err), "v1 loads: " + err);
        Expect(b.Rec.Money == 1234 && b.Rec.Bank == 5678 && b.Rec.CountOf(X) == 3 && b.Rec.CountOf(Y) == 1 && b.Rec.AppliedTxn == 4 && b.Rec.Id == 77 && b.LastSavedTxn == 3 && b.Journal.Count == 1 && b.Journal[0].Kind == JournalKind.Car, "v1 content");
        CheckRoundTrip(b, "v1");
        // v2 file (W3 0.10.x): host fields + kinds 0..3
        var r2 = new P2Inventory { Id = 77, Rev = 9, AppliedTxn = 6, Money = 1234, Bank = 5678, BatonLife = 80, CrowbarLife = 70 }; r2.Set(X, 3);
        var rw = new NetDataWriter(); r2.Write(rw);
        var v2 = Wrap(2, w => { w.Put(0); w.Put((uint)5); w.Put(77); w.Put(true); w.Put((uint)11); w.Put((uint)8); w.Put(rw.CopyData()); w.Put(2);
                                w.Put((uint)6); w.Put(X); w.Put(-1); w.Put(JournalKind.Locker); w.Put((uint)10); w.Put(X); w.Put(1); w.Put(JournalKind.HostItem); });
        Expect(P2Book.TryDeserialize(v2, 0, out b, out err), "v2 loads: " + err);
        Expect(b.HostSeq == 11 && b.LastSavedHost == 8 && b.LastSavedTxn == 5 && b.Journal.Count == 2 && b.Journal[0].Kind == JournalKind.Locker && b.Journal[1].Kind == JournalKind.HostItem && b.Rec.Money == 1234 && b.Rec.Bank == 5678, "v2 content");
        CheckRoundTrip(b, "v2");
        var v2bad = Wrap(2, w => { w.Put(0); w.Put((uint)5); w.Put(77); w.Put(true); w.Put((uint)11); w.Put((uint)8); w.Put(rw.CopyData()); w.Put(1); w.Put((uint)6); w.Put(X); w.Put(1); w.Put(JournalKind.HostCar); });
        Expect(!P2Book.TryDeserialize(v2bad, 0, out b, out err) && err == "journal entry", "a v2 file cannot carry the new kind (corruption detected, not guessed)");
        Expect(!P2Book.TryDeserialize(v2, 1, out b, out err) && err == "other profile", "never another host profile");
        var cut = (byte[])v2.Clone(); cut[cut.Length - 1] ^= 0x5A;
        Expect(!P2Book.TryDeserialize(cut, 0, out b, out err), "corrupt file rejected (caller falls back to .bak, never silently fresh)");
    }

    static void CheckRoundTrip(P2Book b, string from)
    {
        var data = b.Serialize(0); P2Book back; string err;
        Expect(data[8] == 3, from + " re-written as v3");
        Expect(P2Book.TryDeserialize(data, 0, out back, out err), from + " -> v3 loads: " + err);
        Expect(back.Rec.SameContent(b.Rec) && back.Rec.Id == b.Rec.Id && back.Rec.AppliedTxn == b.Rec.AppliedTxn && back.Rec.Rev == b.Rec.Rev && back.HostSeq == b.HostSeq && back.LastSavedHost == b.LastSavedHost
               && back.LastSavedTxn == b.LastSavedTxn && back.HistoryId == b.HistoryId && back.HistoryConfirmed == b.HistoryConfirmed && back.Journal.Count == b.Journal.Count, from + " -> v3: nothing lost");
        for (int i = 0; i < b.Journal.Count; i++) Expect(back.Journal[i].Txn == b.Journal[i].Txn && back.Journal[i].Kind == b.Journal[i].Kind && back.Journal[i].StorageDelta == b.Journal[i].StorageDelta && back.Journal[i].ItemId == b.Journal[i].ItemId, from + " journal order kept");
    }

    static byte[] Wrap(byte ver, Action<NetDataWriter> rest)
    {
        var w = new NetDataWriter(); w.Put((uint)0x32504D54); w.Put(ver); rest(w);
        var body = w.CopyData(); uint h = 2166136261; foreach (var x in body) { h ^= x; h *= 16777619; }
        var o = new NetDataWriter(); o.Put(body.Length); o.Put(body); o.Put(h); return o.CopyData();
    }

    static void CaseJ()
    {
        // W3 drop journal: P1 drops X (host seq D, Item_ written), P2 picks it up (HostItem -1 with seq D), then a storage
        // prerequisite in the same session: both host entries replay in commit order, nothing doubled
        var s = new Sim(2, 0, 0, 0);
        uint d = s.Book.NextHostSeq(); s.Mem["TS2MP_HostTxn"] = (int)d; s.P1[X] = 1; s.Mem["Item_" + X] = 1;   // Player2Sync.HostDropMark
        Expect(s.Book.ItemTxn(s.NextTxn, X, +1) == ProposalCode.Accepted && s.Book.AddHostEntry(d, JournalKind.HostItem, X, -1), "P2 picks up P1's drop (W3 path)");
        s.File = s.Book.Serialize(0);
        s.P1Deposit(Car, X);
        Expect(s.P2(Car, X, true) == ProposalCode.Accepted && s.Book.Prerequisites == 1, "then the CarBox withdrawal with its prerequisite");
        Expect(s.Total(X) == 2, "live total 2");
        s.Reload();
        Expect(s.P1Of(X) == 0 && s.CarOf(X) == 0 && s.P2Of(X) == 2 && s.Total(X) == 2, "recovered: both units P2's (P1 " + s.P1Of(X) + ", P2 " + s.P2Of(X) + ")");
        // both storages in one session, different items
        var t = new Sim(1, 0, 0);
        t.P1Deposit(Car, X); t.P1Deposit(Lock, Y);
        Expect(t.P2(Car, X, true) == ProposalCode.Accepted && t.P2(Lock, Y, true) == ProposalCode.Accepted && t.Book.Prerequisites == 2, "one prerequisite per storage");
        t.Reload();
        Expect(t.P1Of(X) == 0 && t.CarOf(X) == 0 && t.P2Of(X) == 1 && t.P1Of(Y) == 1 && t.LockOf(Y) == 0 && t.P2Of(Y) == 1 && t.Total(X) == 1 && t.Total(Y) == 2, "each storage replayed against its own key");
    }
}
