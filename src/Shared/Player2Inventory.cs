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

    // Client proposal "storageTxn": one unit of one item type between Player 2's backpack and the shared CarBox.
    public struct StorageTxnRequest
    {
        public uint Session, Txn; public int ItemId; public bool Withdraw;
        public byte[] Encode() { var w = new NetDataWriter(); w.Put(Session); w.Put(Txn); w.Put(ItemId); w.Put(Withdraw ? (byte)1 : (byte)0); return w.CopyData(); }
        public static bool TryParse(byte[] v, out StorageTxnRequest t)
        {
            t = default(StorageTxnRequest);
            if (v == null || v.Length != 13 || v[12] > 1) return false;
            t.Session = BitConverter.ToUInt32(v, 0); t.Txn = BitConverter.ToUInt32(v, 4); t.ItemId = BitConverter.ToInt32(v, 8); t.Withdraw = v[12] == 1;
            return true;
        }
    }

    public struct JournalEntry { public uint Txn; public int ItemId; public int StorageDelta; }

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
        public long Reports, ReportsStale, Transfers, TransfersRefused, Replayed;

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
        {
            storageDelta = 0;
            if (txn != 0 && txn <= Rec.AppliedTxn) return ProposalCode.Unchanged;
            if (txn != Rec.AppliedTxn + 1) { TransfersRefused++; return ProposalCode.Stale; }
            if (Journal.Count >= MaxJournal) { TransfersRefused++; return ProposalCode.Busy; }   // host never saved for very long: refuse instead of growing
            if (withdraw)
            {
                if (storageCount < 1) { TransfersRefused++; return ProposalCode.Invalid; }
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
            Journal.Add(new JournalEntry { Txn = txn, ItemId = itemId, StorageDelta = storageDelta });
            Transfers++;
            return ProposalCode.Accepted;
        }

        // Entries above the watermark of the storage counts in memory are not contained in them yet.
        public List<JournalEntry> ReplayFor(uint watermark)
        {
            var l = new List<JournalEntry>();
            foreach (var e in Journal) if (e.Txn > watermark) l.Add(e);
            return l;
        }

        // After an FBPP disk write with this watermark: every entry up to it is contained in the save on disk.
        public void SaveCompleted(uint savedTxn)
        {
            if (savedTxn > LastSavedTxn) LastSavedTxn = savedTxn;
            Journal.RemoveAll(e => e.Txn <= savedTxn);
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
            var w = new NetDataWriter();
            w.Put((uint)0x32504D54); w.Put((byte)1); w.Put(profile); w.Put(LastSavedTxn); w.Put(HistoryId); w.Put(HistoryConfirmed);
            Rec.Write(w);
            w.Put(Journal.Count);
            foreach (var e in Journal) { w.Put(e.Txn); w.Put(e.ItemId); w.Put(e.StorageDelta); }
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
                if (b.GetUInt() != 0x32504D54 || b.GetByte() != 1) { err = "not a p2inv v1 file"; return false; }
                if (b.GetInt() != profile) { err = "other profile"; return false; }
                uint seen = b.GetUInt(); int hist = b.GetInt(); bool conf = b.GetBool();
                P2Inventory rec;
                if (!P2Inventory.TryRead(b, out rec)) { err = "record"; return false; }
                int n = b.GetInt();
                if (n < 0 || n > MaxJournal) { err = "journal size"; return false; }
                rec.Id = hist;                               // v1 records (0.7.0 files) carry no id: the book's history id is the record id
                var bk = new P2Book(rec, seen) { HistoryId = hist, HistoryConfirmed = conf };
                for (int i = 0; i < n; i++)
                {
                    var e = new JournalEntry { Txn = b.GetUInt(), ItemId = b.GetInt(), StorageDelta = b.GetInt() };
                    if (e.StorageDelta != 1 && e.StorageDelta != -1) { err = "journal entry"; return false; }
                    bk.Journal.Add(e);
                }
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
    }
}
