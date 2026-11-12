// M5 W4.1 offline tests: shared pre-placed world loot (host authority, exactly-once ownership, durability, provenance).
// The host model drives the SAME pure pieces as the plugin (src/Plugin/WorldLootSync.cs + Player2Sync.cs W4.1 parts):
// LootBook (claims / taken / registry chunks), LootDecision.DecideP2, P2Book (ItemTxn, AddWorldTake, ReplayAll, ReplayWorld,
// SaveCompleted, Mark/Restore, Serialize v4), LootKeys.Apply, LootProvenance, WorldHost.LootTransaction / HandleProposal.
//   Mem  = FBPP save memory (SPrefs keys), Disk = the save on disk, File = the durable p2inv file, P1 = Player 1 live counts.
//   OriginalPick = the original coroutine's irreversible step on the host (SetDayRespawn writes ItemDay_, Item_Picked +1 and
//   Item_<name>); GameSave = SaveGameNewSystem (live -> keys, disk write); Reload = quit without saving + load + HostBind.
// Unity, Harmony, the census in a real scene and the original coroutine run only in game (manual QA, docs/TESTING.md §25).
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class WorldLootTests
{
    static int _passed, _failed;
    const int Ns = 99;                  // MadisonSt
    const int Phone = 41, Vase = 57;    // item types
    const string W = "TS2MP_StorageTxn", H = "TS2MP_HostTxn";

    static int Main()
    {
        Run("01 P1 pickup: pending claim, committed at the original's irreversible step, P2 sees it taken, granted once", P1Pickup);
        Run("02 P2 pickup: host validates, record +1 and world take in ONE durable write, keys written, P1 cannot take it", P2Pickup);
        Run("03 simultaneous: P1 pending -> P2 Busy; committed -> Stale; P2 first -> P1 refused; never two owners", Simultaneous);
        Run("04 duplicate / retransmitted / out-of-order requests never grant twice", Duplicates);
        Run("05 inventory full (max_amount) refused before anything changes", InventoryFull);
        Run("06 interrupted original pickup: claim released, nothing granted, object available again", Interrupted);
        Run("07 late join: registry snapshot + mirrored ItemDay_ key -> the client sees it unavailable", LateJoin);
        Run("08 region exit + return: live set cleared, original key keeps it gone until its respawn day, then respawns", RegionReturn);
        Run("09 normal host save after a P2 pickup: no replay, journal compacted, idempotent reloads", NormalSave);
        Run("10 unsaved host exit after a P2 pickup: P2 keeps the item AND the loot stays taken (replay)", UnsavedExit);
        Run("11 P1 unsaved loot -> drop -> P2 pickup -> unsaved exit: world take journaled, no duplicate (+ reproduction)", TransferViaDrop);
        Run("12 P1 unsaved loot -> CarBox deposit -> P2 withdraw (W3.4 prerequisite) -> unsaved exit: no duplicate", TransferViaCarBox);
        Run("13 saved vs unsaved quantities: a saved unit is consumed first, never attributed to the new pickup", SavedFirst);
        Run("14 identical item types in different world instances stay separate (ids, keys, provenance)", SameTypeDifferentObjects);
        Run("15 shared-name family: excluded, never merged into one state entry", SharedNames);
        Run("16 exclusions: flags, item facts, unsupported region, no host catalog, digest mismatch", Exclusions);
        Run("17 p2inv migration v3 -> v4, world list round trip, corruption rejected, other profile refused", Migration);
        Run("18 save-history isolation: a world take of another history is never replayed", HistoryIsolation);
        Run("19 codecs: lootTxn 20 B, registry chunks (cap, wrong chunk), fuzz never throws", Codecs);
        Run("20 failed durable write: record, journal, sequence and provenance unchanged (Busy)", FailedWrite);
        Run("21 WorldHost dispatch: lootTxn proposal, slot rules, region-empty clears loot + dropped items together", HostDispatch);
        Run("22 dropped-item regression: W3 registry pickup/claim rules unchanged next to world loot", DroppedRegression);
        Run("23 performance guard: no per-frame scene scans, no UI text, census-only catalog build", SourceScan);
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
    sealed class Store : IKeyStore
    {
        public Dictionary<string, int> D = new Dictionary<string, int>();
        public int Get(string k) { int v; return D.TryGetValue(k, out v) ? v : 0; }
        public void Set(string k, int v) { D[k] = v; }
    }

    sealed class Obj { public uint Hash; public string Name; public int Item; public float Value = 150f; }

    sealed class Sim
    {
        public Store Mem = new Store(); public Dictionary<string, int> Disk = new Dictionary<string, int>();
        public Dictionary<int, int> P1 = new Dictionary<int, int>();
        public P2Book Book; public byte[] File;
        public LootBook Loot = new LootBook(); public LootProvenance Prov = new LootProvenance();
        public StateRegistry Reg = StateRegistry.CreateHost(7); public List<StateEntry> Out = new List<StateEntry>();
        public LootCatalog Cat = new LootCatalog(Ns);
        public int Days = 10; public bool FailNextWrite, NoProvenance;
        public List<Obj> Objs = new List<Obj>();

        public Sim()
        {
            Book = new P2Book(P2Inventory.Initial(false), 0) { HistoryId = 5 }; Book.Rec.Id = 5; Book.Rec.Rev = 1;
            P1[Phone] = 0; P1[Vase] = 0;
            Add(0x1001, "Landline_Phone_Kitchen", Phone); Add(0x2002, "China_vase_Hall", Vase); Add(0x3003, "Landline_Phone_Office", Phone);
            Cat.Seal(null); Cat.Digest = 0xD16E57;
            Persist(); GameSave();
        }
        void Add(uint h, string n, int item) { Objs.Add(new Obj { Hash = h, Name = n, Item = item }); Cat.Add(h, n, item, LootFlags.None, null, 0); }
        public Obj O(int i) { return Objs[i]; }
        public static ItemFacts Facts(int item) { return new ItemFacts { Known = true, Value = 150f, AddXp = 20f, MaxAmount = item == Vase ? 1 : 0 }; }

        public bool Write() { if (FailNextWrite) { FailNextWrite = false; return false; } Persist(); return true; }
        void Persist() { File = Book.Serialize(0); }

        // ---- Player 1 (host): the original pickup
        public bool P1Claim(Obj o, int token) { string why; return Loot.Claim(Ns, o.Hash, token, out why); }
        public void OriginalCompletes(Obj o, int token)
        {
            Expect(Loot.HasClaim(Ns, o.Hash, token), "claim present at the irreversible step");
            Mem.Set(LootKeys.Day(o.Name), Days + LootRules.RespawnDays(o.Value, false));   // SetDayRespawn
            P1[o.Item] = P1[o.Item] + 1; Mem.Set(LootKeys.Picked(o.Name), 1);                // Item_Picked
            Loot.Commit(Ns, o.Hash, Reg, Out);                                               // postfix: commit
            Prov.P1Picked(new LootTake { Ns = Ns, Hash = o.Hash, ItemId = o.Item, Day = Mem.Get(LootKeys.Day(o.Name)), ItemFlag = true, Name = o.Name });
        }
        public void P1Pick(Obj o) { Expect(P1Claim(o, (int)o.Hash), "P1 claim"); OriginalCompletes(o, (int)o.Hash); }

        // ---- Player 2: WorldLootSync.HostDecide + Player2Sync.HostLootTake
        public ProposalCode P2Pick(uint txn, Obj o, uint digest = 0xD16E57, bool inRegion = true)
        {
            string why;
            var code = LootDecision.DecideP2(txn, Book.Rec.AppliedTxn, inRegion, Cat, digest, o.Hash, o.Item, Facts(o.Item), Loot.StateOf(Ns, o.Hash), Loot.HasRoom(Ns, o.Hash), Book.Rec.CountOf(o.Item), out why);
            if (code != ProposalCode.Accepted) return code;
            var take = new LootTake { Ns = Ns, Hash = o.Hash, ItemId = o.Item, Day = Days + LootRules.RespawnDays(o.Value, false), ItemFlag = true, Name = o.Name };
            var mark = Book.Mark();
            code = Book.ItemTxn(txn, o.Item, +1);
            if (code != ProposalCode.Accepted) return code;
            uint seq = Book.NextHostSeq();
            if (!Book.AddWorldTake(seq, take)) { Book.Restore(mark); return ProposalCode.Busy; }
            if (!Write()) { Book.Restore(mark); return ProposalCode.Busy; }
            LootKeys.Apply(Mem, take); Mem.Set(H, (int)seq);
            Loot.Commit(Ns, o.Hash, Reg, Out);
            return ProposalCode.Accepted;
        }

        // ---- W3 dropped items: Player2Sync.HostDropMark / HostP2ItemTxn (pickup of a Player-1 drop)
        public uint P1Drop(int item)
        {
            uint seq = Book.NextHostSeq(); Mem.Set(H, (int)seq);
            Prov.EnsureBase(item, Mem.Get("Item_" + item));
            P1[item] = P1[item] - 1; Mem.Set("Item_" + item, P1[item]);
            return seq;
        }
        public ProposalCode P2PickupDrop(uint txn, int item, uint originTxn)
        {
            var mark = Book.Mark();
            var code = Book.ItemTxn(txn, item, +1);
            if (code != ProposalCode.Accepted) return code;
            var attr = Attribution.SavedUnit; LootTake take = default(LootTake); uint takeSeq = 0; int prevHost = Mem.Get(H);
            bool p1Loss = originTxn > Book.LastSavedHost && !NoProvenance;
            Book.AddHostEntry(originTxn, JournalKind.HostItem, item, -1);
            if (p1Loss)
            {
                attr = Prov.P1Lost(item, Mem.Get("Item_" + item), out take);
                if (attr == Attribution.WorldTake) { takeSeq = Book.NextHostSeq(); Book.AddWorldTake(takeSeq, take); }
            }
            if (!Write()) { Book.Restore(mark); if (p1Loss) Prov.UndoLost(item, attr, take); return ProposalCode.Busy; }
            if (takeSeq != 0 && takeSeq > prevHost) Mem.Set(H, (int)takeSeq);
            LastAttribution = attr;
            return ProposalCode.Accepted;
        }
        public Attribution LastAttribution;

        // ---- W3.4 CarBox: P1 deposit (ledger) + Player2Sync.HostTransfer with prerequisite and attribution
        public HostDepositLedger Ledger = new HostDepositLedger(); public int CarLive, CarKey;
        public void P1Deposit(int item) { P1[item] = P1[item] - 1; CarLive++; Ledger.Deposit(StorageKind.Car, item); }
        public ProposalCode P2Withdraw(uint txn, int item)
        {
            int itemKey = Mem.Get("Item_" + item), prevHost = Mem.Get(H);
            var mark = Book.Mark(); StoragePlan plan;
            var code = Book.HostTransfer(txn, StorageKind.Car, item, true, CarLive, CarKey, itemKey, Ledger.Count(StorageKind.Car, item), true, true, out plan);
            if (code != ProposalCode.Accepted) return code;
            var attr = Attribution.SavedUnit; LootTake take = default(LootTake); uint takeSeq = 0;
            if (plan.Prerequisite)
            {
                attr = Prov.P1Lost(item, itemKey, out take);
                if (attr == Attribution.WorldTake) { takeSeq = Book.NextHostSeq(); Book.AddWorldTake(takeSeq, take); }
            }
            CarLive += plan.StorageDelta; CarKey = plan.NewStorageKey; Mem.Set(W, (int)txn);
            if (plan.Prerequisite) { Ledger.Consume(StorageKind.Car, item); Mem.Set("Item_" + item, plan.NewItemKey); Mem.Set(H, (int)(takeSeq != 0 ? takeSeq : plan.PrereqSeq)); }
            if (!Write()) { Book.Restore(mark); return ProposalCode.Busy; }
            LastAttribution = attr;
            return ProposalCode.Accepted;
        }

        // ---- original save / reload
        public void GameSave()
        {
            foreach (var kv in P1) Mem.Set("Item_" + kv.Key, kv.Value);
            Mem.Set("Storage_Item_" + Phone, CarLive); CarKey = CarLive;
            Disk = new Dictionary<string, int>(Mem.D);
            Book.SaveCompleted((uint)Math.Max(0, Mem.Get(W)), (uint)Math.Max(0, Mem.Get(H)));
            Persist(); Prov.Reset(); Ledger.Clear();
        }
        public void Reload()
        {
            Mem.D = new Dictionary<string, int>(Disk);
            foreach (var k in new List<int>(P1.Keys)) P1[k] = Mem.Get("Item_" + k);
            CarLive = Mem.Get("Storage_Item_" + Phone); CarKey = CarLive;
            P2Book b; string err; Expect(P2Book.TryDeserialize(File, 0, out b, out err), "p2inv loads: " + err); Book = b;
            uint mark = (uint)Math.Max(0, Mem.Get(W)), hmark = (uint)Math.Max(0, Mem.Get(H)), maxTxn, maxHost, maxWorld;
            if (hmark > Book.HostSeq) Book.HostSeq = hmark;
            foreach (var e in Book.ReplayAll(mark, hmark, out maxTxn, out maxHost))
            {
                string key = JournalKind.KeyPrefix(e.Kind) + e.ItemId; int v = Math.Max(0, Mem.Get(key) + e.StorageDelta); Mem.Set(key, v);
                if (e.Kind == JournalKind.HostItem) P1[e.ItemId] = v; else if (JournalKind.IsCarKey(e.Kind)) { CarLive = v; CarKey = v; }
            }
            if (maxTxn > mark) Mem.Set(W, (int)maxTxn);
            if (maxHost > hmark) Mem.Set(H, (int)maxHost);
            foreach (var w in Book.ReplayWorld(hmark, out maxWorld)) LootKeys.Apply(Mem, w.Take);
            if (maxWorld > (uint)Math.Max(0, Mem.Get(H))) Mem.Set(H, (int)maxWorld);
            Prov.Reset(); Ledger.Clear();
            Loot = new LootBook(); Reg = StateRegistry.CreateHost(8); Out.Clear();   // new process / scene: live set rebuilt by the original
        }
        // The original Pickupable.<Start>: destroyed while daysInGame < ItemDay_<name>.
        public bool AvailableAfterLoad(Obj o) { return Days >= Mem.Get(LootKeys.Day(o.Name)); }
        public int P2Count(int item) { return Book.Rec.CountOf(item); }
    }

    static HashSet<uint> TakenIn(StateRegistry reg, int ns)
    {
        var s = new HashSet<uint>();
        for (int c = 0; c < LootBook.Chunks; c++) { var e = reg.Get(StateScope.Region, ns, LootBook.Keys[c]); if (e != null) Expect(LootBook.TryParse(e.Value, c, s), "chunk parses"); }
        return s;
    }

    // ------------------------------------------------------------------------------------------------ cases
    static void P1Pickup()
    {
        var s = new Sim(); var o = s.O(0);
        Expect(s.P1Claim(o, 11), "claim");
        Expect(s.Loot.StateOf(Ns, o.Hash) == LootState.Pending && !TakenIn(s.Reg, Ns).Contains(o.Hash), "pending, not yet published (coroutine may still be interrupted)");
        Expect(!s.P1Claim(o, 11), "a second E press does not start a second original pickup");
        s.OriginalCompletes(o, 11);
        Expect(s.Loot.StateOf(Ns, o.Hash) == LootState.Taken && TakenIn(s.Reg, Ns).Contains(o.Hash), "taken + published -> P2's observer removes it");
        Expect(s.P1[Phone] == 1 && s.P2Count(Phone) == 0, "P1 received it once, P2 nothing");
        Expect(s.P2Pick(1, o) == ProposalCode.Stale && s.P2Count(Phone) == 0, "P2 cannot take it again");
        Expect(s.Book.World.Count == 0, "a P1 pickup is not journaled (original rollback semantics for P1 + world together)");
    }

    static void P2Pickup()
    {
        var s = new Sim(); var o = s.O(1);
        Expect(s.P2Pick(1, o) == ProposalCode.Accepted, "accepted");
        Expect(s.P2Count(Vase) == 1 && s.Book.Rec.AppliedTxn == 1 && s.P1[Vase] == 0, "P2 +1 exactly once, P1 nothing");
        P2Book f; string err; Expect(P2Book.TryDeserialize(s.File, 0, out f, out err) && f.Rec.CountOf(Vase) == 1 && f.World.Count == 1 && f.World[0].Take.Name == o.Name, "record and world take in the SAME durable file");
        Expect(s.Mem.Get(LootKeys.Day(o.Name)) == s.Days + 3 && s.Mem.Get(LootKeys.Picked(o.Name)) == 1 && s.Mem.Get(H) == (int)f.World[0].Seq, "original keys + watermark in the save memory");
        Expect(TakenIn(s.Reg, Ns).Contains(o.Hash), "published");
        Expect(!s.P1Claim(o, 5), "P1's original pickup of it is refused");
    }

    static void Simultaneous()
    {
        var s = new Sim(); var o = s.O(0);
        Expect(s.P1Claim(o, 7), "P1 first");
        Expect(s.P2Pick(1, o) == ProposalCode.Busy && s.P2Count(Phone) == 0 && s.Book.Rec.AppliedTxn == 0, "P2 during P1's pickup: Busy, nothing changed");
        s.OriginalCompletes(o, 7);
        Expect(s.P2Pick(1, o) == ProposalCode.Stale && s.P2Count(Phone) == 0, "P2 retry after P1 committed: Stale");
        var o2 = s.O(2);
        Expect(s.P2Pick(1, o2) == ProposalCode.Accepted, "P2 first on another object");
        Expect(!s.P1Claim(o2, 9) && s.P1[Phone] == 1, "P1 refused, no second grant");
        Expect(s.P1[Phone] + s.P2Count(Phone) == 2, "two objects -> exactly two units in total");
    }

    static void Duplicates()
    {
        var s = new Sim(); var o = s.O(1);
        Expect(s.P2Pick(1, o) == ProposalCode.Accepted, "first");
        int world = s.Book.World.Count; uint seq = s.Book.HostSeq;
        Expect(s.P2Pick(1, o) == ProposalCode.Unchanged, "retransmission of txn 1 -> Unchanged before any other check");
        Expect(s.P2Pick(1, s.O(0)) == ProposalCode.Unchanged, "txn 1 again for another object -> Unchanged, nothing applied");
        Expect(s.P2Pick(5, s.O(0)) == ProposalCode.Stale, "gap -> Stale");
        Expect(s.P2Count(Vase) == 1 && s.P2Count(Phone) == 0 && s.Book.World.Count == world && s.Book.HostSeq == seq, "no second grant, no second journal entry");
    }

    static void InventoryFull()
    {
        var s = new Sim(); s.Book.Rec.Set(Vase, 1);   // max_amount 1 (Sim.Facts)
        Expect(s.P2Pick(1, s.O(1)) == ProposalCode.Invalid, "max_amount refused");
        Expect(s.P2Count(Vase) == 1 && s.Book.Rec.AppliedTxn == 0 && s.Book.World.Count == 0 && s.Loot.StateOf(Ns, s.O(1).Hash) == LootState.Available, "nothing changed, object still available");
    }

    static void Interrupted()
    {
        var s = new Sim(); var o = s.O(0);
        Expect(s.P1Claim(o, 3), "claim");
        Expect(s.Loot.Release(Ns, o.Hash, 3), "object gone before SetDayRespawn -> released");
        Expect(!s.Loot.Release(Ns, o.Hash, 3), "released once");
        Expect(s.P1[Phone] == 0 && s.Loot.StateOf(Ns, o.Hash) == LootState.Available && !TakenIn(s.Reg, Ns).Contains(o.Hash), "nothing granted, available, never published");
        Expect(s.P2Pick(1, o) == ProposalCode.Accepted, "P2 may take it afterwards");
        Expect(!s.Loot.Release(Ns, o.Hash, 3), "a stale release never undoes a committed take");
        Expect(s.Loot.StateOf(Ns, o.Hash) == LootState.Taken, "still taken");
    }

    static void LateJoin()
    {
        var s = new Sim(); s.P1Pick(s.O(0)); s.P2Pick(1, s.O(1));
        var client = StateRegistry.CreateClient();
        client.LoadSnapshot(s.Reg.Epoch, s.Reg.HeadRev, s.Reg.Entries);
        var t = TakenIn(client, Ns);
        Expect(t.Contains(s.O(0).Hash) && t.Contains(s.O(1).Hash) && !t.Contains(s.O(2).Hash), "joiner gets both taken objects, the untouched one stays");
        Expect(!s.AvailableAfterLoad(s.O(0)) && !s.AvailableAfterLoad(s.O(1)) && s.AvailableAfterLoad(s.O(2)), "mirrored ItemDay_ keys: the original Start removes them on the joiner too");
    }

    static void RegionReturn()
    {
        var s = new Sim(); s.P2Pick(1, s.O(1));
        Expect(s.Loot.ClearRegion(Ns, s.Reg, s.Out) == 1 && TakenIn(s.Reg, Ns).Count == 0, "region empty -> live set cleared (original scene reload)");
        Expect(!s.AvailableAfterLoad(s.O(1)), "re-entry: the original key keeps it gone");
        s.Days += 3;
        Expect(s.AvailableAfterLoad(s.O(1)), "after its respawn day the original respawns it (not suppressed permanently)");
    }

    static void NormalSave()
    {
        var s = new Sim(); var o = s.O(1);
        s.P2Pick(1, o); s.GameSave();
        Expect(s.Book.World.Count == 0, "save watermark covers the take -> compacted");
        s.Reload(); s.Reload();
        Expect(s.P2Count(Vase) == 1 && !s.AvailableAfterLoad(o) && s.Mem.Get(LootKeys.Picked(o.Name)) == 1, "P2 keeps it, loot stays gone, after two reloads");
    }

    static void UnsavedExit()
    {
        var s = new Sim(); var o = s.O(1);
        s.P2Pick(1, o);
        s.Reload();
        Expect(s.P2Count(Vase) == 1, "P2 record kept (never rolled back)");
        Expect(!s.AvailableAfterLoad(o) && s.Mem.Get(LootKeys.Picked(o.Name)) == 1, "replayed: loot not collectable again");
        int h = s.Mem.Get(H); s.Reload();
        Expect(!s.AvailableAfterLoad(o) && s.Mem.Get(H) == h && s.Book.World.Count == 1, "second unsaved reload: idempotent (same keys, same watermark)");
        s.GameSave(); Expect(s.Book.World.Count == 0, "the next original save contains it");
    }

    static void TransferViaDrop()
    {
        foreach (bool legacy in new[] { true, false })
        {
            var s = new Sim { NoProvenance = legacy }; var o = s.O(0);
            s.P1Pick(o);                                          // unsaved P1 acquisition
            uint seq = s.P1Drop(Phone);
            Expect(s.P2PickupDrop(1, Phone, seq) == ProposalCode.Accepted, "P2 picks up P1's drop");
            s.Reload();
            int units = s.P1[Phone] + s.P2Count(Phone) + (s.AvailableAfterLoad(o) ? 1 : 0);
            if (legacy) Expect(units == 2, "reproduction: without provenance the loot respawns while P2 keeps it (2 units from 1)");
            else
            {
                Expect(s.LastAttribution == Attribution.WorldTake, "the unit is attributed to P1's unsaved world pickup");
                Expect(units == 1 && s.P2Count(Phone) == 1 && s.P1[Phone] == 0 && !s.AvailableAfterLoad(o), "W4.1: exactly one unit, owned by P2, loot stays taken");
            }
        }
    }

    static void TransferViaCarBox()
    {
        var s = new Sim(); var o = s.O(0);
        s.P1Pick(o); s.P1Deposit(Phone);
        Expect(s.P2Withdraw(1, Phone) == ProposalCode.Accepted && s.LastAttribution == Attribution.WorldTake, "prerequisite + world take attributed");
        s.Reload();
        Expect(s.P2Count(Phone) == 1 && s.P1[Phone] == 0 && s.CarLive == 0 && !s.AvailableAfterLoad(o), "one unit (P2), storage empty, loot stays taken");
    }

    static void SavedFirst()
    {
        var s = new Sim(); var o = s.O(0);
        s.P1[Phone] = 1; s.GameSave();                        // one saved phone
        s.P1Pick(o);                                          // + one unsaved pickup (live 2)
        uint a = s.P1Drop(Phone);
        Expect(s.P2PickupDrop(1, Phone, a) == ProposalCode.Accepted && s.LastAttribution == Attribution.SavedUnit, "first transfer consumes the SAVED unit");
        Expect(s.Book.World.Count == 0, "no world take invented for a saved unit");
        var copy = s.File; var disk = new Dictionary<string, int>(s.Disk);
        uint b = s.P1Drop(Phone);
        Expect(s.P2PickupDrop(2, Phone, b) == ProposalCode.Accepted && s.LastAttribution == Attribution.WorldTake && s.Book.World.Count == 1, "second transfer: no saved unit left -> the unsaved pickup is attributed");
        s.Reload();
        Expect(s.P2Count(Phone) == 2 && s.P1[Phone] == 0 && !s.AvailableAfterLoad(o), "2 units total (saved + loot), both with P2, loot stays taken");
        // only the first transfer, then unsaved exit: the saved unit moved, the loot rolls back with P1's pickup -> conserved
        var t = new Sim(); var ot = t.O(0); t.P1[Phone] = 1; t.GameSave(); t.P1Pick(ot); uint c = t.P1Drop(Phone); t.P2PickupDrop(1, Phone, c); t.Reload();
        Expect(t.P2Count(Phone) == 1 && t.P1[Phone] == 0 && t.AvailableAfterLoad(ot), "saved unit with P2, unsaved pickup rolled back (loot back): 2 units, as before");
    }

    static void SameTypeDifferentObjects()
    {
        var s = new Sim(); var a = s.O(0); var b = s.O(2);   // both Phone, different objects and names
        Expect(s.P2Pick(1, a) == ProposalCode.Accepted, "P2 takes A");
        Expect(s.Loot.StateOf(Ns, b.Hash) == LootState.Available, "B unaffected");
        s.P1Pick(b);
        var t = TakenIn(s.Reg, Ns); Expect(t.Contains(a.Hash) && t.Contains(b.Hash) && t.Count == 2, "two entries");
        Expect(s.Mem.Get(LootKeys.Day(a.Name)) > 0 && s.Mem.Get(LootKeys.Day(b.Name)) > 0 && a.Name != b.Name, "separate original keys");
        uint seq = s.P1Drop(Phone); s.P2PickupDrop(2, Phone, seq);
        Expect(s.LastAttribution == Attribution.WorldTake && s.Book.World[s.Book.World.Count - 1].Take.Hash == b.Hash, "provenance names P1's own pickup (B), not P2's (A)");
    }

    static void SharedNames()
    {
        var c = new LootCatalog(Ns);
        c.Add(1, "Pot", 9, LootFlags.None, null, 0); c.Add(2, "Pot", 9, LootFlags.None, null, 0); c.Add(3, "Pan_Unique", 9, LootFlags.None, null, 0);
        c.Seal(null); c.Digest = 1;
        LootEntry e1, e2, e3; c.TryGet(1, out e1); c.TryGet(2, out e2); c.TryGet(3, out e3);
        Expect(c.Count == 3 && e1 != e2, "two physical objects keep two ids");
        Expect((e1.Flags & LootFlags.SharedName) != 0 && (e2.Flags & LootFlags.SharedName) != 0 && e3.Flags == LootFlags.None && c.Eligible == 1 && c.SharedNameObjects == 2, "shared-name family flagged");
        string why;
        Expect(LootDecision.DecideP2(1, 0, true, c, 1, 1, 9, Sim.Facts(9), LootState.Available, true, 0, out why) == ProposalCode.Invalid && why.Contains("SharedName"), "excluded in Phase B: " + why);
        Expect(!c.Add(1, "Other", 9, LootFlags.None, null, 0), "same hash twice is never a second entry");
        c.Seal(null); c.TryGet(1, out e1); Expect((e1.Flags & LootFlags.Ambiguous) != 0, "duplicate hash -> ambiguous, never resolves to a merged entry");
    }

    static void Exclusions()
    {
        foreach (var f in new[] { LootFlags.Heavy, LootFlags.Money, LootFlags.StoryPick, LootFlags.StoryName, LootFlags.Contract, LootFlags.Parcel, LootFlags.RandomCabinet, LootFlags.DestroyIfPicked, LootFlags.DeleteIfPref, LootFlags.NoXp, LootFlags.Dropped, LootFlags.NoDays, LootFlags.NeverDestroy })
        {
            var c = new LootCatalog(Ns); c.Add(1, "Thing_" + f, 9, f, null, 0); c.Seal(null); c.Digest = 1; string why;
            Expect(LootDecision.DecideP2(1, 0, true, c, 1, 1, 9, Sim.Facts(9), LootState.Available, true, 0, out why) == ProposalCode.Invalid, f + " excluded");
        }
        var n = new LootCatalog(Ns); n.Add(1, "12", 9, LootFlags.None, null, 0); n.Seal(null);
        LootEntry ne; n.TryGet(1, out ne); Expect((ne.Flags & LootFlags.NumericName) != 0, "numeric name excluded (Item_<name> would hit a backpack count key)");
        var ok = new LootCatalog(Ns); ok.Add(1, "Lamp", 9, LootFlags.None, null, 0); ok.Seal(null); ok.Digest = 1; string w;
        var bad = new[] { new ItemFacts { Known = true, Tool = true, Value = 50, AddXp = 5 }, new ItemFacts { Known = true, Story = true, Value = 50, AddXp = 5 }, new ItemFacts { Known = true, Value = 0, AddXp = 5 },
                          new ItemFacts { Known = true, Value = 50, AddXp = 0.5f }, new ItemFacts { Known = true, Heavy = true, Value = 50, AddXp = 5 }, new ItemFacts { Known = false } };
        foreach (var fx in bad) Expect(LootDecision.DecideP2(1, 0, true, ok, 1, 1, 9, fx, LootState.Available, true, 0, out w) == ProposalCode.Invalid, "item facts excluded: " + w);
        Expect(LootRules.XpAdd(0.5f) == 0 && LootRules.XpAdd(1.5f) == 2 && LootRules.XpAdd(2.5f) == 2, "banker's rounding like Mathf.RoundToInt");
        Expect(LootRules.RespawnDays(301, false) == 5 && LootRules.RespawnDays(201, false) == 4 && LootRules.RespawnDays(101, false) == 3 && LootRules.RespawnDays(1, false) == 2 && LootRules.RespawnDays(0, false) == 0 && LootRules.RespawnDays(0, true) == 5, "SetDayRespawn tiers");
        Expect(LootDecision.DecideP2(1, 0, true, null, 1, 1, 9, Sim.Facts(9), LootState.Available, true, 0, out w) == ProposalCode.NotAuthorized && w.Contains("catalog"), "host without a verified catalog refuses (no client metadata trusted)");
        Expect(LootDecision.DecideP2(1, 0, true, ok, 2, 1, 9, Sim.Facts(9), LootState.Available, true, 0, out w) == ProposalCode.Invalid, "digest mismatch refused");
        Expect(LootDecision.DecideP2(1, 0, false, ok, 1, 1, 9, Sim.Facts(9), LootState.Available, true, 0, out w) == ProposalCode.NotAuthorized, "requester not in the region");
        Expect(LootDecision.DecideP2(1, 0, true, ok, 1, 1, 10, Sim.Facts(9), LootState.Available, true, 0, out w) == ProposalCode.Invalid, "item type mismatch refused");
        var hill = new LootCatalog(97); hill.Add(1, "Lamp", 9, LootFlags.None, null, 0); hill.Seal(null); hill.Digest = 1;
        Expect(LootDecision.DecideP2(1, 0, true, hill, 1, 1, 9, Sim.Facts(9), LootState.Available, true, 0, out w) == ProposalCode.NotAuthorized && !LootRules.SupportedNamespace(IdentityNamespace.MainScene), "unsupported region / MainScene");
        Expect(LootDecision.DecideP2(1, 0, true, ok, 1, 1, 9, Sim.Facts(9), LootState.Available, true, 0, out w) == ProposalCode.Accepted, "the eligible case is accepted");
    }

    static void Migration()
    {
        // a v3 file (W3.4 layout) loads into the v4 reader; written back as v4 with an empty world list
        var rec = P2Inventory.Initial(false); rec.Id = 77; rec.Rev = 4; rec.AppliedTxn = 6; rec.Money = 1234; rec.Set(Phone, 3);
        var rw = new NetDataWriter(); rec.Write(rw);
        var v3 = Wrap(3, w => { w.Put(0); w.Put((uint)6); w.Put(77); w.Put(true); w.Put((uint)12); w.Put((uint)9); w.Put(rw.CopyData()); w.Put(1);
                                w.Put((uint)12); w.Put(Phone); w.Put(-1); w.Put(JournalKind.HostItem); });
        P2Book b; string err;
        Expect(P2Book.TryDeserialize(v3, 0, out b, out err), "v3 loads: " + err);
        Expect(b.World.Count == 0 && b.Rec.CountOf(Phone) == 3 && b.Rec.Money == 1234 && b.HostSeq == 12 && b.LastSavedHost == 9 && b.LastSavedTxn == 6 && b.Journal.Count == 1, "v3 content preserved");
        b.AddWorldTake(13, new LootTake { Ns = Ns, Hash = 0xABCDEF01, ItemId = Phone, Day = 14, ItemFlag = true, Name = "Landline_Phone_Kitchen" });
        var v4 = b.Serialize(0); Expect(v4[8] == 4, "written as v4");
        P2Book c; Expect(P2Book.TryDeserialize(v4, 0, out c, out err), "v4 loads: " + err);
        Expect(c.World.Count == 1 && c.World[0].Seq == 13 && c.World[0].Take.Name == "Landline_Phone_Kitchen" && c.World[0].Take.Hash == 0xABCDEF01 && c.World[0].Take.Day == 14 && c.World[0].Take.ItemFlag
               && c.Rec.SameContent(b.Rec) && c.Journal.Count == 1 && c.HostSeq == 13, "round trip: nothing lost");
        var badName = Wrap(4, w => { w.Put(0); w.Put((uint)6); w.Put(77); w.Put(true); w.Put((uint)12); w.Put((uint)9); w.Put(rw.CopyData()); w.Put(0); w.Put(1);
                                     w.Put((uint)13); new LootTake { Ns = Ns, Hash = 1, ItemId = Phone, Day = 1, Name = "5" }.Write(w); });
        Expect(!P2Book.TryDeserialize(badName, 0, out b, out err) && err == "world take", "an invalid world take is corruption (never guessed)");
        var cut = (byte[])v4.Clone(); cut[cut.Length - 6] ^= 0x33;
        Expect(!P2Book.TryDeserialize(cut, 0, out b, out err), "checksum: caller falls back to .bak, never silently fresh");
        Expect(!P2Book.TryDeserialize(v4, 1, out b, out err) && err == "other profile", "never another profile");
    }

    static void HistoryIsolation()
    {
        var s = new Sim(); s.P2Pick(1, s.O(1));
        Expect(!P2Book.SameHistory(999, 0, s.Book), "a save with another history id does not belong to this book");
        Expect(P2Book.SameHistory(5, 0, s.Book), "the own history does");
        s.Book.LastSavedTxn = 10;
        Expect(!P2Book.SameHistory(5, 3, s.Book), "an older save (lower watermark) is another history -> the plugin starts Player 2 fresh, nothing replayed");
    }

    static void Codecs()
    {
        var t = new LootTxnRequest { Txn = 9, Ns = Ns, Hash = 0xDEADBEEF, ItemId = 41, Digest = 0x1234 };
        var v = t.Encode(); LootTxnRequest b;
        Expect(v.Length == LootTxnRequest.WireSize && LootTxnRequest.TryParse(v, out b) && b.Txn == 9 && b.Ns == Ns && b.Hash == 0xDEADBEEF && b.ItemId == 41 && b.Digest == 0x1234, "20 B round trip");
        Expect(!LootTxnRequest.TryParse(new byte[19], out b) && !LootTxnRequest.TryParse(new byte[21], out b) && !LootTxnRequest.TryParse(new byte[20], out b), "wrong size / txn 0 refused");
        var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>();
        int accepted = 0;
        for (uint h = 0; h < 4 * 260; h++) if (book.Commit(Ns, h, reg, o) == ProposalCode.Accepted) accepted++;
        Expect(accepted == LootBook.Chunks * LootBook.MaxPerChunk && book.Full > 0, "4 x 250 per region, beyond that Busy");
        foreach (var e in reg.Entries) Expect(e.Value.Length <= StateEntry.MaxValueBytes, "chunk fits a registry value");
        Expect(!book.HasRoom(Ns, 0) && book.Commit(Ns, 0, reg, o) == ProposalCode.Unchanged, "already taken -> Unchanged");
        string why; Expect(!book.Claim(Ns, 2000, 1, out why) && why.Contains("full"), "a P1 claim is refused when its chunk is full (never an untracked pickup)");
        var wrong = LootBook.Encode(new List<uint> { 1 });   // hash 1 belongs to chunk 1
        Expect(!LootBook.TryParse(wrong, 0, new HashSet<uint>()) && LootBook.TryParse(wrong, 1, new HashSet<uint>()), "a hash in the wrong chunk is refused");
        var rnd = new Random(4); int ok = 0;
        for (int i = 0; i < 20000; i++)
        {
            var d = new byte[rnd.Next(0, 40)]; rnd.NextBytes(d);
            try { LootTxnRequest x; if (LootTxnRequest.TryParse(d, out x)) ok++; LootBook.TryParse(d, rnd.Next(4), new HashSet<uint>()); var r = new NetDataReader(d); LootTake lt; LootTake.TryRead(r, out lt); }
            catch (Exception ex) { throw new Exception("decoder threw: " + ex.GetType().Name); }
        }
        // a P1 pending claim reserves its slot: its commit is never refused
        var b2 = new LootBook(); var r2 = StateRegistry.CreateHost(1);
        for (uint h = 0; h < 249; h++) b2.Commit(Ns, h * 4, r2, o);
        Expect(b2.Claim(Ns, 249 * 4, 1, out why) && !b2.HasRoom(Ns, 250 * 4) && b2.Commit(Ns, 249 * 4, r2, o) == ProposalCode.Accepted, "pending claim reserves room; P2 refused meanwhile, P1 commit succeeds");
    }

    static void FailedWrite()
    {
        var s = new Sim(); var o = s.O(1);
        s.FailNextWrite = true;
        Expect(s.P2Pick(1, o) == ProposalCode.Busy, "Busy");
        Expect(s.P2Count(Vase) == 0 && s.Book.Rec.AppliedTxn == 0 && s.Book.World.Count == 0 && s.Book.HostSeq == 0 && s.Mem.Get(LootKeys.Day(o.Name)) == 0 && s.Loot.StateOf(Ns, o.Hash) == LootState.Available, "nothing changed");
        Expect(s.P2Pick(1, o) == ProposalCode.Accepted, "retry succeeds");
        // provenance undo on a failed drop-pickup write
        var t = new Sim(); t.P1Pick(t.O(0)); uint seq = t.P1Drop(Phone); t.FailNextWrite = true;
        Expect(t.P2PickupDrop(1, Phone, seq) == ProposalCode.Busy && t.Prov.UnsavedPicks(Phone) == 1 && t.Book.World.Count == 0, "attribution undone with the write");
        Expect(t.P2PickupDrop(1, Phone, seq) == ProposalCode.Accepted && t.Prov.UnsavedPicks(Phone) == 0 && t.Book.World.Count == 1, "retry attributes once");
    }

    static void HostDispatch()
    {
        var host = new WorldHost(3); double now = 1;
        host.SetPlayerRegion(1, 0, now); host.SetPlayerRegion(2, Ns, now);
        int calls = 0;
        host.LootTxnHook = (byte slot, LootTxnRequest t, bool inRegion, out string why) => { why = null; calls++; return inRegion ? ProposalCode.Accepted : ProposalCode.NotAuthorized; };
        var req = new LootTxnRequest { Txn = 1, Ns = Ns, Hash = 0x55, ItemId = 41, Digest = 1 };
        uint rev; int sr; float sh;
        var code = host.HandleProposal("c", 2, 1, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.LootTxn, Value = req.Encode() }, now, out rev, out sr, out sh);
        Expect(code == ProposalCode.Accepted && calls == 1 && host.Loot.StateOf(Ns, 0x55) == LootState.Taken && TakenIn(host.Reg, Ns).Contains(0x55), "accepted and committed after the hook");
        string w; Expect(host.LootTransaction(1, req, now, out w) == ProposalCode.NotAuthorized, "Player 1 never proposes (original pickup on the host)");
        code = host.HandleProposal("c", 2, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.LootTxn, Value = new byte[5] }, now, out rev, out sr, out sh);
        Expect(code == ProposalCode.Invalid, "malformed proposal Invalid");
        var other = new LootTxnRequest { Txn = 2, Ns = 97, Hash = 0x66, ItemId = 41, Digest = 1 };
        Expect(host.LootTransaction(2, other, now, out w) == ProposalCode.NotAuthorized, "not in that region (hook told)");
        // region empty -> loot + dropped items cleared together
        host.Items.Apply(2, Ns, new ItemTxnRequest { Op = ItemOp.Drop, Txn = 3, ItemId = 41, Region = Ns, Rot = Quat16.From(0, 0, 0, 1) }, host.Reg, host.Outbox);
        host.SetPlayerRegion(2, 0, now);
        Expect(host.Loot.TakenCount(Ns) == 0 && TakenIn(host.Reg, Ns).Count == 0 && host.Items.CountIn(Ns) == 0, "region emptied: original reload semantics for both");
    }

    static void DroppedRegression()
    {
        var host = new WorldHost(3); double now = 1;
        host.SetPlayerRegion(1, Ns, now); host.SetPlayerRegion(2, Ns, now);
        host.ItemTxnHook = (slot, t) => ProposalCode.Accepted;
        string why;
        Expect(host.ItemTransaction(1, new ItemTxnRequest { Op = ItemOp.Drop, Txn = 1, ItemId = 41, Region = Ns, Rot = Quat16.From(0, 0, 0, 1) }, now, out why) == ProposalCode.Accepted, "drop");
        uint id = host.Items.LastApplied;
        Expect(host.ItemTransaction(2, new ItemTxnRequest { Op = ItemOp.Pickup, Txn = 1, WorldId = id, ItemId = 41, Region = Ns }, now, out why) == ProposalCode.Accepted, "P2 picks up the drop");
        Expect(host.ItemTransaction(1, new ItemTxnRequest { Op = ItemOp.Pickup, WorldId = id, ItemId = 41, Region = Ns }, now, out why) == ProposalCode.Stale, "single winner unchanged");
        Expect(host.Loot.TakenCount(Ns) == 0, "dropped items never enter the world-loot set (separate registries, ids, keys)");
        Expect(ObjectKinds.PickupableCategory("dropped", false) == IdentityCategory.Dynamic && ObjectKinds.PickupableCategory("Lamp", true) == IdentityCategory.Dynamic, "dropped objects are dynamic (census flag Dropped -> excluded)");
    }

    static void SourceScan()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory, plugin = null;
        for (int i = 0; i < 6 && root != null; i++)
        {
            var c = System.IO.Path.Combine(root, "src", "Plugin");
            if (System.IO.Directory.Exists(c)) { plugin = c; break; }
            root = System.IO.Path.GetDirectoryName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }
        Expect(plugin != null, "src/Plugin found");
        var loot = System.IO.File.ReadAllText(System.IO.Path.Combine(plugin, "WorldLootSync.cs"));
        foreach (var bad in new[] { "FindObjectsOfType", "FindObjectOfType", "Resources.FindObjectsOfTypeAll", "GameObject.Find", "GetComponentsInChildren", "FindGameObjectsWithTag", "GetRootGameObjects" })
            Expect(loot.IndexOf(bad, StringComparison.Ordinal) < 0, "WorldLootSync has no scene scan (" + bad + ")");
        foreach (var bad in new[] { "OnGUI", "GUI.", "SpawnWarning", "SpawnMessage", "UI.Text", "TextMeshPro" })
            Expect(loot.IndexOf(bad, StringComparison.Ordinal) < 0, "no mod text in the game UI (" + bad + ")");
        var census = System.IO.File.ReadAllText(System.IO.Path.Combine(plugin, "WorldCensus.cs"));
        Expect(census.IndexOf("void Update", StringComparison.Ordinal) < 0 && census.IndexOf("MarkCabinets()", StringComparison.Ordinal) > 0, "catalog built inside the census (scene load), no per-frame method");
        var items = System.IO.File.ReadAllText(System.IO.Path.Combine(plugin, "WorldItemSync.cs"));
        Expect(items.IndexOf("loot.AllowPick(pick)", StringComparison.Ordinal) > 0, "one PickUp prefix, world loot routed from it (no second competing prefix)");
    }

    static byte[] Wrap(byte ver, Action<NetDataWriter> rest)
    {
        var w = new NetDataWriter(); w.Put((uint)0x32504D54); w.Put(ver); rest(w);
        var body = w.CopyData(); uint h = 2166136261; foreach (var x in body) { h ^= x; h *= 16777619; }
        var o = new NetDataWriter(); o.Put(body.Length); o.Put(body); o.Put(h); return o.CopyData();
    }
}
