// M4.3.3 durable Player-2 inventory + shared CarBox transactions (pure logic, offline-tested).
// Verified original model (Cecil IL, CODEBASE_MAP Â§3.7 "M4.3.3"): the player-owned save data is SPrefs "Item_<id>"
// (ItemID.item_amount, read in ItemID.Start, written by ItemID.Save_Item_Amount for PlayerScript.item_IDs), "Money"
// (PlayerScript.moneyAm, SaveGame.LoadGame / SaveGameNewSystem), "BankBalance" (Darkbank, SPrefs only) and the tool wear
// "BatonLife" / "CrowbarLife" (PlayerScript.batonLife / crowbarLife). World-owned: "Storage_Item_<id>" (car storage, the
// shared CarBox), "Locker_Item_<id>" (hideout locker), "Debt" (story debt, new game 500000), everything else.
// Ownership: Player 1 = the host's own save (unchanged). Player 2 = one record per HOST save profile, owned and persisted by
// the host (file p2inv-<profile>.dat), restored into the client's sandbox after load, kept current by client reports.
// Shared CarBox = the host's real storage counters. A client transfer changes the P2 record and the host storage in ONE
// process: the record, the transfer journal and the new storage count are written to the p2inv file before the client is
// acknowledged; the storage count itself reaches the original save at the host's next save. The host save carries a
// watermark (SPrefs "TS2MP_StorageTxn" = id of the last Player-2 transfer contained in its Storage_Item_ counts, written in
// memory together with the counts on every transfer, so the original FBPP write stores both atomically). After any load the
// journal entries ABOVE the loaded watermark are replayed - a crash between the p2inv write and the game save, or reloading
// an older on-disk save in the same process, never duplicates or loses an item. A loaded watermark BELOW the one this file
// last saw means another save history (new game / older backup): the record is not applied.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public sealed class P2Inventory
    {
        public const int MaxItems = 150;                 // registry value limit 1024 B: v2 header 31 + 6 x 150 = 931 B
        public const int HeaderV1 = 27, HeaderV2 = 31;   // version byte + fields + item count (an EMPTY backpack is exactly the header)
        public int Id;                                   // record identity = the host book's save history id (0.7.1, v2); a new host book = new id
        public uint Rev;                                 // host revision of the record (a client report names the revision it is based on)
        public uint AppliedTxn;                          // last CarBox transfer contained in this inventory
        public int Money, Bank, BatonLife = 100, CrowbarLife = 100;
        public readonly SortedDictionary<int, int> Items = new SortedDictionary<int, int>();   // item_ID -> backpack count (> 0)

        public int CountOf(int id) { int c; return Items.TryGetValue(id, out c) ? c : 0; }
        public void Set(int id, int c) { if (c > 0) Items[id] = c; else Items.Remove(id); }

        // Initial Player 2 (first join for a host profile): an original new game's player state - empty backpack (the game's
        // own debug_setToOne items still appear through ItemID.Start), tool wear 100, start money of MainMenu.StartNewGame
        // (500) / StartFreeMode (200). Never a copy of Player 1 (that duplicated items and money before M4.3.3).
        public static P2Inventory Initial(bool freeMode) { return new P2Inventory { Money = freeMode ? 200 : 500, Bank = 0, BatonLife = 100, CrowbarLife = 100 }; }

        public P2Inventory Clone()
        {
            var c = new P2Inventory { Id = Id, Rev = Rev, AppliedTxn = AppliedTxn, Money = Money, Bank = Bank, BatonLife = BatonLife, CrowbarLife = CrowbarLife };
            foreach (var kv in Items) c.Items[kv.Key] = kv.Value;
            return c;
        }

        public bool SameContent(P2Inventory o)
        {
            if (o == null || Money != o.Money || Bank != o.Bank || BatonLife != o.BatonLife || CrowbarLife != o.CrowbarLife || Items.Count != o.Items.Count) return false;
            foreach (var kv in Items) if (o.CountOf(kv.Key) != kv.Value) return false;
            return true;
        }

        public void Write(NetDataWriter w)
        {
            w.Put((byte)2); w.Put(Id); w.Put(Rev); w.Put(AppliedTxn); w.Put(Money); w.Put(Bank); w.Put(BatonLife); w.Put(CrowbarLife);
            w.Put((ushort)Items.Count);
            foreach (var kv in Items) { w.Put(kv.Key); w.Put((ushort)Math.Min(kv.Value, ushort.MaxValue)); }
        }

        public bool TryEncode(out byte[] data)
        {
            data = null;
            if (Items.Count > MaxItems) return false;
            foreach (var kv in Items) if (kv.Value <= 0 || kv.Value > ushort.MaxValue) return false;
            var w = new NetDataWriter(); Write(w); data = w.CopyData(); return true;
        }

        // v2 (0.7.1): version, Id, Rev, AppliedTxn, Money, Bank, BatonLife, CrowbarLife, count, items. v1 (0.7.0 p2inv files):
        // the same without Id. 0.7.0 required 31 bytes for v1, whose empty-backpack form is 27 bytes -> every record of a new
        // Player 2 was rejected (client never restored, host file "unusable" after a restart). The minimum is now the header.
        public static bool TryRead(NetDataReader r, out P2Inventory inv)
        {
            inv = null;
            try
            {
                if (r.AvailableBytes < HeaderV1) return false;
                byte ver = r.GetByte();
                if (ver != 1 && ver != 2) return false;
                if (ver == 2 && r.AvailableBytes < HeaderV2 - 1) return false;
                var i = new P2Inventory();
                if (ver == 2) i.Id = r.GetInt();
                i.Rev = r.GetUInt(); i.AppliedTxn = r.GetUInt(); i.Money = r.GetInt(); i.Bank = r.GetInt(); i.BatonLife = r.GetInt(); i.CrowbarLife = r.GetInt();
                int n = r.GetUShort();
                if (n > MaxItems || r.AvailableBytes < 6 * n) return false;
                for (int k = 0; k < n; k++)
                {
                    int id = r.GetInt(), c = r.GetUShort();
                    if (c == 0 || i.Items.ContainsKey(id)) return false;
                    i.Items[id] = c;
                }
                inv = i; return true;
            }
            catch { return false; }
        }

        public static bool TryParse(byte[] v, out P2Inventory inv)
        {
            inv = null;
            if (v == null) return false;
            var r = new NetDataReader(v);
            return TryRead(r, out inv) && r.AvailableBytes == 0;
        }

        public override string ToString() { return "id " + Id + " rev " + Rev + " txn " + AppliedTxn + " money " + Money + " bank " + Bank + " tools " + BatonLife + "/" + CrowbarLife + " items " + Items.Count; }
    }

    // Original storage types (Inventory.storage_Type, verified IL Inventory.MoveItemToStorage): 0 = car storage (CarBox,
    // ItemID.item_amount_storage, "Storage_Item_"), 1 = hideout locker (ItemID.item_amount_locker, "Locker_Item_").
    public static class StorageKind
    {
        public const byte Car = 0, Locker = 1;
        public static bool Valid(int k) { return k == Car || k == Locker; }
        public static string Name(int k) { return k == Locker ? "locker" : "CarBox"; }
        public static string KeyPrefix(int k) { return k == Locker ? "Locker_Item_" : "Storage_Item_"; }
    }

    // Client proposal "storageTxn": one unit of one item type between Player 2's backpack and a shared storage. 13 bytes =
    // car storage (M4.3.3 format), 14 bytes = + storage kind (W3: hideout locker).
    public struct StorageTxnRequest
    {
        public uint Session, Txn; public int ItemId; public bool Withdraw; public byte Kind;
        public byte[] Encode()
        {
            var w = new NetDataWriter(); w.Put(Session); w.Put(Txn); w.Put(ItemId); w.Put(Withdraw ? (byte)1 : (byte)0);
            if (Kind != StorageKind.Car) w.Put(Kind);
            return w.CopyData();
        }
        public static bool TryParse(byte[] v, out StorageTxnRequest t)
        {
            t = default(StorageTxnRequest);
            if (v == null || (v.Length != 13 && v.Length != 14) || v[12] > 1) return false;
            if (v.Length == 14 && !StorageKind.Valid(v[13])) return false;
            t.Session = BitConverter.ToUInt32(v, 0); t.Txn = BitConverter.ToUInt32(v, 4); t.ItemId = BitConverter.ToInt32(v, 8); t.Withdraw = v[12] == 1;
            t.Kind = v.Length == 14 ? v[13] : StorageKind.Car;
            return true;
        }
    }

    // Journal kinds. Car/Locker: Player-2 transfers (txn = Player-2 transaction id, watermark "TS2MP_StorageTxn" stored with the
    // storage counts). HostItem/HostLocker (W3): host-side effects of cross-player item transfers (txn = host sequence,
    // watermark "TS2MP_HostTxn"): HostItem = Player 1's backpack count (Item_<id>), HostLocker = a Player-2 item converted
    // into the host locker by the original dropped-item conversion. Replayed after a load when the save predates them.
    // W3.4: HostCar = host-side car-storage effect (a Player-1 deposit that a confirmed Player-2 withdrawal depends on; the
    // locker counterpart reuses HostLocker, whose replay is the same "+delta on Locker_Item_"). p2inv file v3.
    public static class JournalKind
    {
        public const byte Car = 0, Locker = 1, HostItem = 2, HostLocker = 3, HostCar = 4;
        public const byte MaxV2 = HostLocker, Max = HostCar;
        public static bool IsHost(byte k) { return k == HostItem || k == HostLocker || k == HostCar; }
        public static string KeyPrefix(byte k) { return k == Locker || k == HostLocker ? "Locker_Item_" : k == HostItem ? "Item_" : "Storage_Item_"; }
        public static byte HostStorage(byte storageKind) { return storageKind == StorageKind.Locker ? HostLocker : HostCar; }
        public static bool IsLockerKey(byte k) { return k == Locker || k == HostLocker; }
        public static bool IsCarKey(byte k) { return k == Car || k == HostCar; }
    }

    public struct JournalEntry { public uint Txn; public int ItemId; public int StorageDelta; public byte Kind; }

    // W3.4 durability model (verified IL, Assembly-CSharp 1.27c):
    //  - Player 1's deposit/withdrawal = Inventory.MoveItemToStorage -> ItemID.Move_To_Storage(type) / Move_Out_Of_Storage(type):
    //    live fields only (item_amount, item_amount_storage / item_amount_locker, weights, capacity); no SPrefs key.
    //  - The keys Item_/Storage_Item_/Locker_Item_ are written ONLY by ItemID.Save_Item_Amount, called only by
    //    SaveGame.SaveGameNewSystem IL_04a7 (every item), which then always ends in FBPP.Save / FBPP.SaveToMainProfile
    //    (IL_058a / IL_0590) -> FBPP.SaveSaveFile(true): the disk write. ItemID.Start reads the keys at scene load.
    //  - Hence, between two original saves, a storage key = its value on disk + the journaled multiplayer deltas written by
    //    the mod (Player-2 transfers, replays, host-journal effects) = exactly what a reload of that save recovers
    //    ("recoverable count"), as long as the mod writes keys only together with a journal entry.
    // Invariant: a confirmed Player-2 withdrawal never makes the recoverable count of that storage negative. If it would, the
    // missing unit is an unsaved Player-1 deposit (ledger below); its effect (Player 1 backpack -1, storage +1) is journaled
    // as a prerequisite pair BEFORE the withdrawal entry, in the same durable p2inv write that commits the withdrawal.
    // Withdrawals are served from the recoverable (saved) stock first; unconsumed Player-1 deposits keep the original
    // unsaved-change rollback. A unit that is neither recoverable nor a tracked Player-1 deposit (e.g. an unsaved
    // CMS_Part.AddPartToLocker) is refused until the host saves (it could not be recovered without inventing an item).
    public enum PrereqDecision { None, Prerequisite, Refuse }

    public static class StorageDurability
    {
        // recoverable = the storage key before this transfer; unsavedDeposits = tracked Player-1 deposits of that item/storage
        public static PrereqDecision Decide(bool withdraw, int recoverable, int unsavedDeposits)
        {
            if (!withdraw || recoverable >= 1) return PrereqDecision.None;
            return unsavedDeposits > 0 ? PrereqDecision.Prerequisite : PrereqDecision.Refuse;
        }
    }

    // Host memory only: net Player-1 deposits per (storage, item type) made since the last ORIGINAL save (SaveGameNewSystem
    // -> FBPP write) or load. Event-driven (Move_To_Storage / Move_Out_Of_Storage postfixes), never scanned. A withdrawal by
    // Player 1 lowers it (never below 0: withdrawing a saved unit is rolled back by a reload anyway). Bounded by the number of
    // item types (cap 1024 entries; beyond that new types are not tracked -> such a withdrawal is refused, never duplicated).
    public sealed class HostDepositLedger
    {
        public const int MaxEntries = 1024;
        readonly Dictionary<long, int> _n = new Dictionary<long, int>();
        static long K(byte kind, int item) { return ((long)kind << 32) | (uint)item; }
        public int Count(byte kind, int item) { int v; return _n.TryGetValue(K(kind, item), out v) ? v : 0; }
        public void Deposit(byte kind, int item) { long k = K(kind, item); int v; _n.TryGetValue(k, out v); if (v == 0 && _n.Count >= MaxEntries) return; _n[k] = v + 1; }
        public void Withdraw(byte kind, int item) { long k = K(kind, item); int v; if (_n.TryGetValue(k, out v)) { if (v <= 1) _n.Remove(k); else _n[k] = v - 1; } }
        public void Consume(byte kind, int item) { Withdraw(kind, item); }
        public void Restore(byte kind, int item) { long k = K(kind, item); int v; _n.TryGetValue(k, out v); _n[k] = v + 1; }
        public void Clear() { _n.Clear(); }
        public int Entries { get { return _n.Count; } }
    }

    // Result of P2Book.HostTransfer: what the host plugin writes (keys = recoverable values, live fields = live + delta).
    public struct StoragePlan
    {
        public ProposalCode Code; public int StorageDelta;
        public bool Prerequisite; public uint PrereqSeq;   // host-journal pair HostItem -1 + HostCar/HostLocker +1 with this sequence
        public int NewStorageKey;                          // recoverable storage count after the transfer (key value to write)
        public int NewItemKey;                             // Player 1 "Item_" key after the prerequisite (only when Prerequisite)
        public string Why;
    }

    // Host-only book of Player 2: record + CarBox journal + save watermark + save history id. Pure; the plugin feeds the
    // host storage counts and SPrefs values.
    public sealed class P2Book
    {
        public const int MaxJournal = 4096;
        public P2Inventory Rec;
        public readonly List<JournalEntry> Journal = new List<JournalEntry>();
        public uint LastSavedTxn;  // watermark of the last host save known to have reached the disk
        public int HistoryId;      // random id written into the host save (SPrefs "TS2MP_P2History") when this record was created
        public bool HistoryConfirmed;   // that id is known to be in a save on disk (set after the first completed FBPP write)
        public uint HostSeq;       // W3: last host sequence number handed out (HostItem/HostLocker journal, "TS2MP_HostTxn")
        public uint LastSavedHost; // W3: host watermark of the last save known on disk
        public long Reports, ReportsStale, Transfers, TransfersRefused, Replayed, ItemTxns, ItemTxnsRefused;

        public P2Book(P2Inventory rec, uint savedTxn) { Rec = rec; LastSavedTxn = savedTxn; }

        // Client report of everything that changed outside CarBox transfers (pickups, sales, purchases, drops, use, bank).
        // 0.7.1: a report names the record it is based on (Id, Rev, AppliedTxn). Accepted only on exactly the current record:
        // another record (new game / other save history), a missing transfer or an older revision (duplicate, delayed or
        // recovered old report) is Stale and never overwrites newer data. A report equal to the record is Unchanged whatever
        // its revision (retransmission after a lost acknowledgment: already committed, nothing to do).
        public ProposalCode AcceptReport(P2Inventory rep)
        {
            if (rep == null) return ProposalCode.Invalid;
            if (rep.Id != Rec.Id || rep.AppliedTxn != Rec.AppliedTxn) { ReportsStale++; return ProposalCode.Stale; }
            if (rep.Money < -10000000 || rep.Bank < 0 || rep.BatonLife < 0 || rep.CrowbarLife < 0) return ProposalCode.Invalid;
            Reports++;
            if (rep.SameContent(Rec)) return ProposalCode.Unchanged;
            if (rep.Rev != Rec.Rev) { ReportsStale++; return ProposalCode.Stale; }
            var n = rep.Clone(); n.Id = Rec.Id; n.Rev = Rec.Rev + 1; n.AppliedTxn = Rec.AppliedTxn;
            Rec = n;
            return ProposalCode.Accepted;
        }

        // One CarBox transfer (exactly one unit). txn must be the next id; an already applied id is answered Unchanged
        // (retransmission after a lost acknowledgment never applies twice). storageCount = the host's current count of that
        // item type; depositFits = the original car-capacity rule holds on the host.
        public ProposalCode Transfer(uint txn, int itemId, bool withdraw, int storageCount, bool depositFits, out int storageDelta)
        { return Transfer(txn, StorageKind.Car, itemId, withdraw, storageCount, depositFits, true, out storageDelta); }

        // W3: the same exactly-once unit transfer for the car storage (kind 0) or the hideout locker (kind 1). withdrawFits =
        // the original max_amount rule for the backpack (checked again on the host). The journal entry names the storage.
        public ProposalCode Transfer(uint txn, byte kind, int itemId, bool withdraw, int storageCount, bool depositFits, bool withdrawFits, out int storageDelta)
        {
            storageDelta = 0;
            if (!StorageKind.Valid(kind)) return ProposalCode.Invalid;
            if (txn != 0 && txn <= Rec.AppliedTxn) return ProposalCode.Unchanged;
            if (txn != Rec.AppliedTxn + 1) { TransfersRefused++; return ProposalCode.Stale; }
            if (Journal.Count >= MaxJournal) { TransfersRefused++; return ProposalCode.Busy; }   // host never saved for very long: refuse instead of growing
            if (withdraw)
            {
                if (storageCount < 1 || !withdrawFits) { TransfersRefused++; return ProposalCode.Invalid; }
                if (Rec.CountOf(itemId) >= ushort.MaxValue) { TransfersRefused++; return ProposalCode.Invalid; }
                storageDelta = -1;
            }
            else
            {
                if (Rec.CountOf(itemId) < 1 || !depositFits) { TransfersRefused++; return ProposalCode.Invalid; }
                storageDelta = +1;
            }
            var n = Rec.Clone();
            n.Set(itemId, n.CountOf(itemId) - storageDelta);
            n.AppliedTxn = txn; n.Rev = Rec.Rev + 1;
            Rec = n;
            Journal.Add(new JournalEntry { Txn = txn, ItemId = itemId, StorageDelta = storageDelta, Kind = kind });
            Transfers++;
            return ProposalCode.Accepted;
        }

        // W3.4 host entry point for a Player-2 storage transfer. storageKey = recoverable count (the SPrefs key), p1ItemKey =
        // Player 1's "Item_" key, unsavedDeposits = HostDepositLedger.Count. Order: duplicates / ordering / capacity first (a
        // retransmission never adds a prerequisite), then the durability decision, then the transfer. A prerequisite pair is
        // inserted IMMEDIATELY BEFORE the withdrawal entry (journal order = replay order). Pure: the caller persists the book
        // (one atomic write) and, on failure, restores the BookMark taken before.
        public ProposalCode HostTransfer(uint txn, byte kind, int itemId, bool withdraw, int liveCount, int storageKey, int p1ItemKey, int unsavedDeposits,
                                         bool depositFits, bool withdrawFits, out StoragePlan plan)
        {
            plan = new StoragePlan();
            if (!StorageKind.Valid(kind)) return plan.Code = ProposalCode.Invalid;
            if (txn != 0 && txn <= Rec.AppliedTxn) return plan.Code = ProposalCode.Unchanged;
            if (txn != Rec.AppliedTxn + 1) { TransfersRefused++; return plan.Code = ProposalCode.Stale; }
            if (Journal.Count + 3 > MaxJournal) { TransfersRefused++; return plan.Code = ProposalCode.Busy; }
            int rec = Math.Max(0, storageKey);
            var d = withdraw && liveCount >= 1 && withdrawFits ? StorageDurability.Decide(true, rec, unsavedDeposits) : PrereqDecision.None;
            if (d == PrereqDecision.Refuse)
            {
                TransfersRefused++; plan.Why = "unit not in the host save and not a tracked Player-1 deposit (refused until the host saves)";
                return plan.Code = ProposalCode.Invalid;
            }
            int delta;
            var code = Transfer(txn, kind, itemId, withdraw, liveCount, depositFits, withdrawFits, out delta);
            plan.Code = code; plan.StorageDelta = delta;
            if (code != ProposalCode.Accepted) return code;
            if (d == PrereqDecision.Prerequisite)
            {
                uint seq = NextHostSeq();
                int at = Journal.Count - 1;                                // the withdrawal entry just added
                Journal.Insert(at, new JournalEntry { Txn = seq, ItemId = itemId, StorageDelta = +1, Kind = JournalKind.HostStorage(kind) });
                Journal.Insert(at, new JournalEntry { Txn = seq, ItemId = itemId, StorageDelta = -1, Kind = JournalKind.HostItem });
                plan.Prerequisite = true; plan.PrereqSeq = seq; plan.NewItemKey = Math.Max(0, p1ItemKey - 1); rec += 1;
                Prerequisites++;
            }
            plan.NewStorageKey = Math.Max(0, rec + delta);
            return code;
        }
        public long Prerequisites;

        // Undo point for a failed durable write (journal tail, record, host sequence, counters).
        public struct BookMark { public P2Inventory Rec; public int Journal; public uint HostSeq; public long Transfers, Prerequisites, ItemTxns; }
        public BookMark Mark() { return new BookMark { Rec = Rec, Journal = Journal.Count, HostSeq = HostSeq, Transfers = Transfers, Prerequisites = Prerequisites, ItemTxns = ItemTxns }; }
        public void Restore(BookMark m)
        {
            Rec = m.Rec; if (Journal.Count > m.Journal) Journal.RemoveRange(m.Journal, Journal.Count - m.Journal);
            HostSeq = m.HostSeq; Transfers = m.Transfers; Prerequisites = m.Prerequisites; ItemTxns = m.ItemTxns;
        }

        // W3.4: one replay pass in JOURNAL ORDER (= commit order) over every entry not yet contained in the loaded save:
        // Player-2 transfers above the storage watermark, host entries above the host watermark. W3 replayed all Player-2
        // entries before all host entries, so a prerequisite deposit would have come after the withdrawal it supports
        // (clamped at 0 -> wrong count). maxTxn / maxHost = the watermarks the replayed state now contains.
        public List<JournalEntry> ReplayAll(uint watermark, uint hostWatermark, out uint maxTxn, out uint maxHost)
        {
            var l = new List<JournalEntry>(); maxTxn = watermark; maxHost = hostWatermark;
            foreach (var e in Journal)
            {
                if (JournalKind.IsHost(e.Kind)) { if (e.Txn > hostWatermark) { l.Add(e); if (e.Txn > maxHost) maxHost = e.Txn; } }
                else if (e.Txn > watermark) { l.Add(e); if (e.Txn > maxTxn) maxTxn = e.Txn; }
            }
            return l;
        }

        // W3: Player 2's backpack <-> the shared world (dropped items). delta -1 = drop (the item leaves the record into the
        // host's world-item registry), +1 = pickup (claimed from the registry). Same exactly-once rules as a transfer: next
        // transaction id only, a retransmitted id is Unchanged, never applied twice. No storage journal entry (the dropped
        // item itself is world state with the original game's transience).
        public ProposalCode ItemTxn(uint txn, int itemId, int delta)
        {
            if (delta != 1 && delta != -1) return ProposalCode.Invalid;
            if (txn != 0 && txn <= Rec.AppliedTxn) return ProposalCode.Unchanged;
            if (txn != Rec.AppliedTxn + 1) { ItemTxnsRefused++; return ProposalCode.Stale; }
            int c = Rec.CountOf(itemId);
            if (delta < 0 && c < 1) { ItemTxnsRefused++; return ProposalCode.Invalid; }
            if (delta > 0 && c >= ushort.MaxValue) { ItemTxnsRefused++; return ProposalCode.Invalid; }
            var n = Rec.Clone();
            n.Set(itemId, c + delta);
            n.AppliedTxn = txn; n.Rev = Rec.Rev + 1;
            Rec = n;
            ItemTxns++;
            return ProposalCode.Accepted;
        }

        // W3 host-side journal (Player 1's backpack / Player-2 items converted into the host locker). seq = a number from
        // NextHostSeq; the plugin writes the host watermark "TS2MP_HostTxn" = seq into the save memory together with the
        // changed key, so a later FBPP write contains both.
        public uint NextHostSeq() { return ++HostSeq; }
        public bool AddHostEntry(uint seq, byte kind, int itemId, int delta)
        {
            if ((kind != JournalKind.HostItem && kind != JournalKind.HostLocker) || (delta != 1 && delta != -1) || seq == 0 || Journal.Count >= MaxJournal) return false;
            if (seq > HostSeq) HostSeq = seq;
            Journal.Add(new JournalEntry { Txn = seq, ItemId = itemId, StorageDelta = delta, Kind = kind });
            return true;
        }

        // Entries above the watermark of the storage counts in memory are not contained in them yet (Player-2 transfers).
        public List<JournalEntry> ReplayFor(uint watermark)
        {
            var l = new List<JournalEntry>();
            foreach (var e in Journal) if (!JournalKind.IsHost(e.Kind) && e.Txn > watermark) l.Add(e);
            return l;
        }

        // W3: host-side entries above the loaded host watermark, in sequence order.
        public List<JournalEntry> ReplayHost(uint hostWatermark)
        {
            var l = new List<JournalEntry>();
            foreach (var e in Journal) if (JournalKind.IsHost(e.Kind) && e.Txn > hostWatermark) l.Add(e);
            l.Sort((a, b) => a.Txn.CompareTo(b.Txn));
            return l;
        }

        // After an FBPP disk write with this watermark: every entry up to it is contained in the save on disk.
        public void SaveCompleted(uint savedTxn) { SaveCompleted(savedTxn, 0); }
        public void SaveCompleted(uint savedTxn, uint savedHost)
        {
            if (savedTxn > LastSavedTxn) LastSavedTxn = savedTxn;
            if (savedHost > LastSavedHost) LastSavedHost = savedHost;
            Journal.RemoveAll(e => JournalKind.IsHost(e.Kind) ? e.Txn <= savedHost : e.Txn <= savedTxn);
        }

        // A save whose watermark is LOWER than one this file already saw on disk belongs to another history (new game / free
        // mode in the same slot, restored older backup): the record must not be applied to it.
        public static bool IsStale(uint loadedTxn, uint lastSavedTxn) { return loadedTxn < lastSavedTxn; }

        // Does a loaded host save belong to this record? Same history id; or no id yet in the save while the record's id never
        // reached the disk (crash before the first save after creation). A new game (SPrefs.DeleteAll) has no id after the id
        // was confirmed -> other history; a restored older backup has a lower watermark -> other history.
        public static bool SameHistory(int saveHistoryId, uint saveTxn, P2Book file)
        {
            if (file == null) return false;
            if (IsStale(saveTxn, file.LastSavedTxn)) return false;
            if (saveHistoryId != 0) return saveHistoryId == file.HistoryId;
            return !file.HistoryConfirmed;
        }

        public byte[] Serialize(int profile)
        {
            // v2 (W3): + HostSeq, LastSavedHost, a kind byte per journal entry. v3 (W3.4): same layout, journal kind HostCar
            // allowed (a v2 reader would misread it, so the version says so). v1 / v2 files stay readable (migrated on write).
            var w = new NetDataWriter();
            w.Put((uint)0x32504D54); w.Put((byte)3); w.Put(profile); w.Put(LastSavedTxn); w.Put(HistoryId); w.Put(HistoryConfirmed);
            w.Put(HostSeq); w.Put(LastSavedHost);
            Rec.Write(w);
            w.Put(Journal.Count);
            foreach (var e in Journal) { w.Put(e.Txn); w.Put(e.ItemId); w.Put(e.StorageDelta); w.Put(e.Kind); }
            var body = w.CopyData();
            var o = new NetDataWriter(); o.Put(body.Length); o.Put(body); o.Put(Fnv(body));
            return o.CopyData();
        }

        public static bool TryDeserialize(byte[] data, int profile, out P2Book book, out string err)
        {
            book = null; err = null;
            try
            {
                var r = new NetDataReader(data);
                int len = r.GetInt();
                if (len <= 0 || len > data.Length) { err = "bad length"; return false; }
                var body = new byte[len]; r.GetBytes(body, len);
                if (r.GetUInt() != Fnv(body)) { err = "checksum"; return false; }
                var b = new NetDataReader(body);
                if (b.GetUInt() != 0x32504D54) { err = "not a p2inv file"; return false; }
                byte ver = b.GetByte();
                if (ver < 1 || ver > 3) { err = "unknown p2inv version " + ver; return false; }
                if (b.GetInt() != profile) { err = "other profile"; return false; }
                uint seen = b.GetUInt(); int hist = b.GetInt(); bool conf = b.GetBool();
                uint hostSeq = 0, hostSaved = 0;
                if (ver >= 2) { hostSeq = b.GetUInt(); hostSaved = b.GetUInt(); }
                byte maxKind = ver >= 3 ? JournalKind.Max : JournalKind.MaxV2;
                P2Inventory rec;
                if (!P2Inventory.TryRead(b, out rec)) { err = "record"; return false; }
                int n = b.GetInt();
                if (n < 0 || n > MaxJournal) { err = "journal size"; return false; }
                rec.Id = hist;                               // v1 records (0.7.0 files) carry no id: the book's history id is the record id
                var bk = new P2Book(rec, seen) { HistoryId = hist, HistoryConfirmed = conf, HostSeq = hostSeq, LastSavedHost = hostSaved };
                for (int i = 0; i < n; i++)
                {
                    var e = new JournalEntry { Txn = b.GetUInt(), ItemId = b.GetInt(), StorageDelta = b.GetInt() };
                    if (ver >= 2) e.Kind = b.GetByte();
                    if ((e.StorageDelta != 1 && e.StorageDelta != -1) || e.Kind > maxKind) { err = "journal entry"; return false; }
                    bk.Journal.Add(e);
                }
                if (b.AvailableBytes != 0) { err = "trailing data"; return false; }
                book = bk; return true;
            }
            catch (Exception ex) { err = ex.GetType().Name; return false; }
        }

        static uint Fnv(byte[] d) { uint h = 2166136261; foreach (var x in d) { h ^= x; h *= 16777619; } return h; }
    }

    // The original Inventory.MoveItemToStorage checks for car storage (type 0), verified IL: deposit needs
    // current_storage_capacity + weight <= max_storage_capacity; withdraw needs currentWeight + weight <= maxWeight + 5 and,
    // for items with max_amount > 0, item_amount + 1 <= max_amount; story items never move.
    public static class StorageRules
    {
        public static bool DepositFits(float storageWeight, float itemWeight, int maxStorage) { return maxStorage >= storageWeight + itemWeight; }
        public static bool WithdrawFits(float backpackWeight, float itemWeight, float maxWeight, int itemAmount, int maxAmount)
        {
            if (maxWeight + 5f < backpackWeight + itemWeight) return false;
            return maxAmount <= 0 || itemAmount + 1 <= maxAmount;
        }
    }

    // M4.3.3 co-op sleep presentation event (registry key "sleepShow", Region scope, arg = region), host-committed: which
    // player started the original sleep there, until which hour, and whether it still runs. Every OTHER player in that region
    // shows the original PassTime presentation; the region clock stays host-authoritative (unchanged clock model).
    public struct SleepShowState
    {
        public uint EventId; public byte Initiator; public byte TargetHour; public bool Active;
        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(EventId); w.Put(Initiator); w.Put(TargetHour); w.Put(Active ? (byte)1 : (byte)0); return w.CopyData(); }
        public static bool TryParse(byte[] v, out SleepShowState s)
        {
            s = default(SleepShowState);
            if (v == null || v.Length != 7 || v[4] == 0 || v[4] > 2 || v[5] > 23 || v[6] > 1) return false;
            s.EventId = BitConverter.ToUInt32(v, 0); s.Initiator = v[4]; s.TargetHour = v[5]; s.Active = v[6] == 1; return true;
        }

        // Should THIS instance (slot me, in region myRegion) present the event of 'region'?
        public static bool ShouldPresent(SleepShowState s, int region, byte me, int myRegion) { return s.Active && s.Initiator != me && region == myRegion && region != Regions.None; }
    }
}

namespace TS2MP.Shared
{
    // BlackBay_Object.SellIt guard decisions (Player2Sync.AllowSale; original removal order backpack -> locker -> car storage,
    // ItemID.Remove_Item_Blackbay). Client: only its own backpack is real, and never while the CarBox is open. Host: a sale
    // that could only be served from the car storage is refused while Player 2 holds the CarBox lease.
    public static class BlackbayRules
    {
        public static bool ClientSaleAllowed(int backpack, int need, bool carBoxOpen) { return backpack >= need && !carBoxOpen; }
        public static bool HostSaleBlocked(byte leaseOwner, int backpack, int locker, int storage, int need) { return leaseOwner == 2 && backpack < need && locker < need && storage >= need; }
        // W3: the original takes from the locker when the backpack lacks the items; refused while Player 2 holds the locker lease.
        public static bool HostLockerSaleBlocked(byte lockerOwner, int backpack, int locker, int need) { return lockerOwner == 2 && backpack < need && locker >= need; }
    }
}
