// M4.3.2b shared car storage (CarBox). Verified original model (Cecil IL, CODEBASE_MAP §3.7 "M4.3.2b"): one car storage per
// save, shared by both cars = per item TYPE counter ItemID.item_amount_storage (no per-item metadata, no condition), changed
// one unit at a time only by ItemID.Move_To_Storage(0) / Move_Out_Of_Storage(0) from Inventory.MoveItemToStorage (plus the
// Blackbay sale path ItemID.Remove_Item_Blackbay). Opened only through PlayerScript.OpenStorage(type 0).
// Multiplayer: the HOST's counters are the only real storage (the client plays on a sandbox copy re-seeded from the host save
// at every client start). The host owns one exclusive lease: exactly one player has the original storage UI open at a time.
// Registry: "carbox" (Session, arg 0) = committed lease {owner u8, session u32}; "carboxItems" (Session, arg 0) = committed
// host contents {rev u32, n u16, n x (item_ID i32, count u16)}; client proposal "carboxReq" {op u8, session u32}.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum CarBoxOp : byte { Close = 0, Open = 1, Renew = 2 }

    public struct CarBoxLeaseState
    {
        public byte Owner;      // 0 none, 1 host, 2 client
        public uint Session;    // increasing per grant; 0 = never granted
        public byte[] ToBytes() { var w = new NetDataWriter(); w.Put(Owner); w.Put(Session); return w.CopyData(); }
        public static bool TryParse(byte[] v, out CarBoxLeaseState s)
        {
            s = default(CarBoxLeaseState);
            if (v == null || v.Length != 5 || v[0] > 2) return false;
            s.Owner = v[0]; s.Session = BitConverter.ToUInt32(v, 1); return true;
        }
        public override string ToString() { return Owner == 0 ? "free" : "P" + Owner + " session " + Session; }
    }

    public static class CarBoxRequest
    {
        public static byte[] Encode(CarBoxOp op, uint session) { var w = new NetDataWriter(); w.Put((byte)op); w.Put(session); return w.CopyData(); }
        public static bool TryParse(byte[] v, out CarBoxOp op, out uint session)
        {
            op = CarBoxOp.Close; session = 0;
            if (v == null || v.Length != 5 || v[0] > (byte)CarBoxOp.Renew) return false;
            op = (CarBoxOp)v[0]; session = BitConverter.ToUInt32(v, 1); return true;
        }
    }

    // Host contents snapshot: every item type with a non-zero car-storage count (absent = 0).
    public sealed class CarBoxItems
    {
        public const int MaxEntries = 160;   // registry value limit 1024 B: 6 + 6 x 160 = 966 B
        public uint Rev;
        public readonly List<KeyValuePair<int, int>> Counts = new List<KeyValuePair<int, int>>();

        public bool TryEncode(out byte[] data)
        {
            data = null;
            if (Counts.Count > MaxEntries) return false;
            var w = new NetDataWriter();
            w.Put(Rev); w.Put((ushort)Counts.Count);
            foreach (var kv in Counts)
            {
                if (kv.Value <= 0 || kv.Value > ushort.MaxValue) return false;
                w.Put(kv.Key); w.Put((ushort)kv.Value);
            }
            data = w.CopyData();
            return true;
        }

        public static bool TryParse(byte[] v, out CarBoxItems items)
        {
            items = null;
            if (v == null || v.Length < 6) return false;
            var r = new NetDataReader(v);
            var it = new CarBoxItems();
            it.Rev = r.GetUInt();
            int n = r.GetUShort();
            if (n > MaxEntries || v.Length != 6 + 6 * n) return false;
            var seen = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                int id = r.GetInt(); int c = r.GetUShort();
                if (c == 0 || !seen.Add(id)) return false;
                it.Counts.Add(new KeyValuePair<int, int>(id, c));
            }
            items = it; return true;
        }

        public int CountOf(int id) { foreach (var kv in Counts) if (kv.Key == id) return kv.Value; return 0; }
    }

    // Host-only exclusive lease. One owner or none; a grant creates a new session id; close/renew must name the current
    // session (a delayed or duplicated packet of an older session is Stale and changes nothing). A client owner that stops
    // renewing (crash without a clean disconnect) expires after LeaseSeconds; the host owner is watched by the host plugin
    // (released when its original UI closes). Disconnect and region change release at once.
    public sealed class CarBoxLease
    {
        public const double LeaseSeconds = 20.0, RenewSeconds = 5.0;
        public byte Owner { get; private set; }
        public uint Session { get; private set; }
        uint _next;
        double _seen;
        public long Grants, Busy, Stale, Expired;

        public CarBoxLeaseState State { get { return new CarBoxLeaseState { Owner = Owner, Session = Session }; } }

        public ProposalCode Request(byte slot, CarBoxOp op, uint session, double now)
        {
            if (slot == 0 || slot > 2) return ProposalCode.Invalid;
            switch (op)
            {
                case CarBoxOp.Open:
                    if (Owner == slot) { _seen = now; return ProposalCode.Unchanged; }   // repeated open of the own session
                    if (Owner != 0) { Busy++; return ProposalCode.Busy; }
                    Owner = slot; Session = ++_next; _seen = now; Grants++;
                    return ProposalCode.Accepted;
                case CarBoxOp.Renew:
                    if (Owner != slot || session != Session) { Stale++; return ProposalCode.Stale; }
                    _seen = now; return ProposalCode.Unchanged;
                default:
                    if (Owner == 0 || Owner != slot || session != Session) { if (Owner != 0 || session != 0) Stale++; return Owner == 0 ? ProposalCode.Unchanged : ProposalCode.Stale; }
                    Owner = 0; return ProposalCode.Accepted;
            }
        }

        public bool Release(byte slot)
        {
            if (Owner == 0 || Owner != slot) return false;
            Owner = 0; return true;
        }

        public bool Expire(double now)
        {
            if (Owner != 2 || now - _seen < LeaseSeconds) return false;
            Owner = 0; Expired++; return true;
        }
    }
}
