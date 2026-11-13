// M3: host-authoritative shared world state. Unity-free (unit-testable).
// One registry per instance. Only the host commits; the client mirrors it via snapshot + ordered deltas and
// proposes changes (StateProposal) that the host validates, commits and acknowledges (ProposalResult).
using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    // ---- regions (verified from GameManager.Select*/Start IL, see CODEBASE_MAP §3.9) ----
    public static class Regions
    {
        public const int None = int.MinValue;   // menu, loading, or region not yet confirmed
        // CurrentLocation value -> (name, additive scene loaded by GameManager.Start; null = MainScene only)
        static readonly Dictionary<int, string[]> Table = new Dictionary<int, string[]>
        {
            { 0,  new[] { "Hideout",        "PlayerHideout" } },
            { 1,  new[] { "Verwood",        null } },
            { 2,  new[] { "PawnShop",       "PawnShop" } },
            { 3,  new[] { "AutoShop",       "Auto Shop" } },
            { 90, new[] { "DrugFarm",       "DrugFarm" } },
            { 91, new[] { "Warehouse",      "Warehouse" } },
            { 92, new[] { "Bank",           "BankScene" } },
            { 93, new[] { "Jail",           "Jail" } },
            { 94, new[] { "Forestside",     "Forestside Resort" } },
            { 95, new[] { "HauntedMansion", "HauntedMansion" } },
            { 96, new[] { "Museum",         "Museum" } },
            { 97, new[] { "Hillville",      "Hillville" } },
            { 98, new[] { "SwatRange",      "SwatRange" } },
            { 99, new[] { "MadisonSt",      "MadisonSt" } },
            { -1, new[] { "IntroHouse",     "IntroHouse" } },
        };

        public static bool IsKnown(int id) { return Table.ContainsKey(id); }
        public static string Name(int id)
        {
            if (id == None) return "none";
            string[] e; return Table.TryGetValue(id, out e) ? e[0] : "loc" + id;
        }
        public static string ExpectedScene(int id) { string[] e; return Table.TryGetValue(id, out e) ? e[1] : null; }

        // A region is confirmed only when MainScene is loaded, the player exists, and the additive scene that
        // GameManager.Start loads for this CurrentLocation is present (currentLoc is 0 before Start runs).
        public static int Resolve(int currentLoc, bool playerPresent, ICollection<string> loadedScenes)
        {
            if (!playerPresent || loadedScenes == null || !loadedScenes.Contains("MainScene") || !IsKnown(currentLoc)) return None;
            var scene = ExpectedScene(currentLoc);
            if (scene != null && !loadedScenes.Contains(scene)) return None;
            if (scene == null && loadedScenes.Any(s => s != "MainScene" && Table.Values.Any(v => v[1] == s))) return None;
            return currentLoc;
        }
    }

    // ---- entries ----
    public enum StateScope : byte { Session = 1, Region = 2, Player = 3 }

    public sealed class StateEntry
    {
        public const int MaxValueBytes = 1024, MaxKeyChars = 64;
        public StateScope Scope;
        public int ScopeArg;          // region id (Region) or player slot (Player); 0 for Session
        public string Key;
        public uint Rev;              // host revision that last changed this entry
        public byte[] Value;

        public string Id { get { return Compose(Scope, ScopeArg, Key); } }
        public static string Compose(StateScope s, int arg, string key) { return (byte)s + ":" + arg + ":" + key; }

        public void Write(NetDataWriter w)
        {
            w.Put((byte)Scope); w.Put(ScopeArg); w.Put(Key ?? ""); w.Put(Rev);
            w.Put((ushort)Value.Length); w.Put(Value);
        }

        public static bool TryRead(NetDataReader r, out StateEntry e)
        {
            e = null;
            byte scope; int arg; string key; uint rev; ushort len;
            if (!r.TryGetByte(out scope) || !r.TryGetInt(out arg) || !r.TryGetString(out key) || !r.TryGetUInt(out rev) || !r.TryGetUShort(out len)) return false;
            if (scope < 1 || scope > 3 || string.IsNullOrEmpty(key) || key.Length > MaxKeyChars || len > MaxValueBytes || r.AvailableBytes < len) return false;
            var v = new byte[len]; r.GetBytes(v, len);
            e = new StateEntry { Scope = (StateScope)scope, ScopeArg = arg, Key = key, Rev = rev, Value = v };
            return true;
        }

        public StateEntry Clone() { return new StateEntry { Scope = Scope, ScopeArg = ScopeArg, Key = Key, Rev = Rev, Value = (byte[])Value.Clone() }; }
    }

    public enum DeltaResult { Applied, Duplicate, Gap, WrongEpoch, NoSnapshot }

    // ---- registry ----
    public sealed class StateRegistry
    {
        readonly Dictionary<string, StateEntry> _entries = new Dictionary<string, StateEntry>();
        public uint Epoch { get; private set; }       // identifies one authoritative host history (new on host start)
        public uint HeadRev { get; private set; }
        public bool HasSnapshot { get; private set; } // client: true once a snapshot has been loaded
        public int Count { get { return _entries.Count; } }
        public IEnumerable<StateEntry> Entries { get { return _entries.Values; } }

        public static StateRegistry CreateHost(uint epoch)
        {
            if (epoch == 0) epoch = 1;
            return new StateRegistry { Epoch = epoch, HasSnapshot = true };
        }
        public static StateRegistry CreateClient() { return new StateRegistry(); }

        public StateEntry Get(StateScope s, int arg, string key)
        {
            StateEntry e; return _entries.TryGetValue(StateEntry.Compose(s, arg, key), out e) ? e : null;
        }

        // Host only. Returns the committed entry, or null when the value is unchanged (idempotent: no new revision).
        public StateEntry Commit(StateScope s, int arg, string key, byte[] value)
        {
            if (value == null || value.Length > StateEntry.MaxValueBytes) throw new ArgumentException("value");
            var cur = Get(s, arg, key);
            if (cur != null && cur.Value.SequenceEqual(value)) return null;
            HeadRev++;
            var e = new StateEntry { Scope = s, ScopeArg = arg, Key = key, Rev = HeadRev, Value = (byte[])value.Clone() };
            _entries[e.Id] = e;
            return e;
        }

        // Client: replace everything with the host's snapshot.
        public void LoadSnapshot(uint epoch, uint headRev, IEnumerable<StateEntry> entries)
        {
            _entries.Clear();
            foreach (var e in entries) _entries[e.Id] = e.Clone();
            Epoch = epoch; HeadRev = headRev; HasSnapshot = true;
        }

        // Client: apply one ordered delta. Gap means a missed revision -> request a resync snapshot.
        public DeltaResult ApplyDelta(uint epoch, StateEntry e)
        {
            if (!HasSnapshot) return DeltaResult.NoSnapshot;
            if (epoch != Epoch) return DeltaResult.WrongEpoch;
            if (e.Rev <= HeadRev) return DeltaResult.Duplicate;
            if (e.Rev != HeadRev + 1) return DeltaResult.Gap;
            _entries[e.Id] = e.Clone();
            HeadRev = e.Rev;
            return DeltaResult.Applied;
        }

        public void Reset() { _entries.Clear(); Epoch = 0; HeadRev = 0; HasSnapshot = false; }
    }

    // ---- proposals (client -> host) ----
    public enum ProposalCode : byte { Accepted = 0, Unchanged = 1, NotAuthorized = 2, Invalid = 3, Stale = 4, Busy = 5 }   // Busy: protocol 5 (another sleep running)

    // Host-side duplicate protection per client instance: ids must increase; a repeated id gets the cached answer.
    public sealed class ProposalGate
    {
        sealed class Last { public uint Id; public ProposalCode Code; public uint Rev; }
        readonly Dictionary<string, Last> _last = new Dictionary<string, Last>();

        public bool TryGetCached(string instance, uint id, out ProposalCode code, out uint rev)
        {
            code = ProposalCode.Stale; rev = 0;
            Last l;
            if (!_last.TryGetValue(instance ?? "", out l) || id > l.Id) return false;
            if (id == l.Id) { code = l.Code; rev = l.Rev; }
            return true;   // id < last: stale (already superseded)
        }

        public void Record(string instance, uint id, ProposalCode code, uint rev)
        {
            _last[instance ?? ""] = new Last { Id = id, Code = code, Rev = rev };
        }
    }

    // ---- wire messages (all on the reliable ordered channel) ----
    public static class StateMessages
    {
        public const int MaxSnapshotEntries = 512;

        public static void WriteSnapshot(NetDataWriter w, StateRegistry reg)
        {
            w.Put((byte)MsgType.StateSnapshot); w.Put(reg.Epoch); w.Put(reg.HeadRev);
            var list = reg.Entries.OrderBy(e => e.Rev).ToList();
            w.Put((ushort)list.Count);
            foreach (var e in list) e.Write(w);
        }

        public static bool TryReadSnapshot(NetDataReader r, out uint epoch, out uint head, out List<StateEntry> entries)
        {
            entries = null; head = 0;
            ushort n;
            if (!r.TryGetUInt(out epoch) || !r.TryGetUInt(out head) || !r.TryGetUShort(out n) || n > MaxSnapshotEntries) return false;
            entries = new List<StateEntry>(n);
            for (int i = 0; i < n; i++)
            {
                StateEntry e;
                if (!StateEntry.TryRead(r, out e) || e.Rev > head) return false;
                entries.Add(e);
            }
            return epoch != 0;
        }

        public static void WriteDelta(NetDataWriter w, uint epoch, StateEntry e) { w.Put((byte)MsgType.StateDelta); w.Put(epoch); e.Write(w); }
        public static bool TryReadDelta(NetDataReader r, out uint epoch, out StateEntry e)
        {
            e = null;
            return r.TryGetUInt(out epoch) && StateEntry.TryRead(r, out e);
        }

        public static void WriteResync(NetDataWriter w, uint epoch, uint head) { w.Put((byte)MsgType.StateResyncRequest); w.Put(epoch); w.Put(head); }
        public static bool TryReadResync(NetDataReader r, out uint epoch, out uint head) { head = 0; return r.TryGetUInt(out epoch) && r.TryGetUInt(out head); }

        public static void WriteProposal(NetDataWriter w, uint id, StateEntry e) { w.Put((byte)MsgType.StateProposal); w.Put(id); e.Write(w); }
        public static bool TryReadProposal(NetDataReader r, out uint id, out StateEntry e) { e = null; return r.TryGetUInt(out id) && StateEntry.TryRead(r, out e); }

        public static void WriteResult(NetDataWriter w, uint id, ProposalCode code, uint rev) { w.Put((byte)MsgType.ProposalResult); w.Put(id); w.Put((byte)code); w.Put(rev); }
        public static bool TryReadResult(NetDataReader r, out uint id, out ProposalCode code, out uint rev)
        {
            code = ProposalCode.Invalid; rev = 0; byte c;
            if (!r.TryGetUInt(out id) || !r.TryGetByte(out c) || !r.TryGetUInt(out rev) || c > (byte)ProposalCode.Busy) return false;
            code = (ProposalCode)c; return true;
        }
    }

    // ---- keys and typed values used in M3 ----
    public static class WorldKeys
    {
        public const string Clock = "clock";            // Region scope (arg = region id): RegionClock, host-committed; owner reports
        public const string Sleep = "sleep";            // Region scope: SleepRequest proposal (client -> host), never stored
        public const string Vehicle = "vehicle";        // Session scope (arg = vehicle id): VehicleRecord, host-committed (M4.1)
        public const string VehicleClaim = "vehicleClaim"; // Session scope (arg = vehicle id): 1 byte 1 = claim driver seat, 0 = release (client proposal)
        public const string VehicleTake = "vehicleTake";   // Session scope (arg = vehicle id): 1 byte 1 = request free driver seat + simulation (M4.2.2, protocol 8)
        public const string VehicleRide = "vehicleRide"; // Session scope (arg = vehicle id): 1 byte 1 = reserve passenger seat, 2 = seated (confirm), 0 = leave (M4.3, protocol 10)
        public const string P2Inv = "p2inv";             // Session scope (arg 2): committed Player-2 inventory (P2Inventory), host-owned (M4.3.3, protocol 13)
        public const string P2Report = "p2report";       // Session scope (arg 2): client proposal, Player-2 inventory after local changes (base = AppliedTxn)
        public const string StorageTxn = "storageTxn";   // Session scope (arg 0): client proposal, one storage transfer (StorageTxnRequest; 14 B = with storage kind, W3)
        public const string SleepShow = "sleepShow";     // Region scope (arg = region): committed co-op sleep presentation event (SleepShowState)
        public const string CarBox = "carbox";           // Session scope (arg = storage kind: 0 car storage, 1 hideout locker W3): committed CarBoxLeaseState {owner, session} (M4.3.2b, protocol 12)
        public const string CarBoxItems = "carboxItems"; // Session scope (arg = storage kind): committed host storage contents (CarBoxItems)
        public const string CarBoxReq = "carboxReq";     // Session scope (arg = storage kind): client proposal {op, session}
        public const string WorldItems = "witems";       // Region scope (arg = region): committed dropped items of that region (WorldItemList, W3, protocol 17)
        public const string LootTxn = "lootTxn";         // Session scope (arg 0): client proposal, Player 2 picks up one verified world object (LootTxnRequest, W4.1, protocol 20)
        // W4.1: taken world loot = Region scope (arg = namespace = region id), keys LootBook.Keys "wloot0".."wloot3" (hash chunks)
        public const string ObjectTxn = "objTxn";       // Session scope (arg 0): client proposal, one world-object transition (ObjectTxnRequest, W4.2, protocol 23)
        // W4.2: committed object states = Region scope (arg = namespace = region id), key ObjectStateRules.RegistryKey(kind) ("wobj2" = doors)
        public const string P2Pos = "p2pos";             // Player scope (arg 2): client proposal, Player 2's last valid position (P2PosReport, W4.2), never stored
        public const string ItemTxn = "itemTxn";         // Session scope (arg 0): client proposal, one dropped-item transaction (ItemTxnRequest, W3)
        public const string VehicleDoor = "vdoor";      // Session scope (arg = vehicle id): committed 1-byte door mask (+ slow bits, M4.3.1); client proposal {door, open, mode} (M5.0 protocol 9, mode protocol 11)
        public const string VehiclePark = "vpark";      // Session scope (arg = vehicle id): ParkRecord, host-committed (M4.2)
        public const string Travel = "travel";          // Player scope (arg = sender slot): TravelRequest proposal, never stored (M4.2)
        public const string TravelCancel = "travelCancel"; // Player scope: u32 request id proposal, never stored (M4.2)
        public const string TravelGrant = "travelGrant";   // Player scope (arg = slot): TravelGrant, host-committed (M4.2)
        public const string JointTrip = "jointTrip";       // Session scope (arg 0): JointTrip 18 B, host-committed only (M4.3 Phase B, protocol 15)
        public const string SleepStart = "sleepStart";  // Region scope: SleepStart proposal (client -> host, protocol 5), never stored
        public const string Calendar = "calendar";      // Session scope, host-only (Calendar: day/week)
        public const string Region = "region";          // Player scope (arg = slot): int region id, proposed by its owner
        public const string LastVisit = "lastVisit";    // Region scope (arg = region id): last slot that entered + session rev

        public static byte[] Int(int v) { var w = new NetDataWriter(); w.Put(v); return w.CopyData(); }
        public static bool TryInt(byte[] b, out int v)
        {
            v = 0; if (b == null || b.Length != 4) return false;
            v = BitConverter.ToInt32(b, 0); return true;
        }
    }

    // Host-side validation of client proposals (pure; the host passes the sender's slot from the connection).
    public static class ProposalRules
    {
        public static ProposalCode Validate(StateEntry p, byte senderSlot)
        {
            if (p.Scope == StateScope.Player && p.Key == WorldKeys.Region)
            {
                if (p.ScopeArg != senderSlot) return ProposalCode.NotAuthorized;   // a player only reports its own region
                int region;
                if (!WorldKeys.TryInt(p.Value, out region) || (region != Regions.None && !Regions.IsKnown(region))) return ProposalCode.Invalid;
                return ProposalCode.Accepted;
            }
            return ProposalCode.NotAuthorized;   // other keys are handled by WorldHost (clock/sleep) or host-only
        }
    }

    // Region ownership model (M3: computed and logged only; no distributed simulation yet).
    // Owner = host if the host is in the region, else the lowest-slot client in it, else 0 (frozen: state retained in
    // the registry, nobody simulates it). Host always validates and commits, whoever owns the region.
    public static class RegionDirectory
    {
        public static Dictionary<int, List<byte>> Occupants(StateRegistry reg)
        {
            var map = new Dictionary<int, List<byte>>();
            foreach (var e in reg.Entries.Where(x => x.Scope == StateScope.Player && x.Key == WorldKeys.Region))
            {
                int region;
                if (!WorldKeys.TryInt(e.Value, out region) || region == Regions.None) continue;
                List<byte> l;
                if (!map.TryGetValue(region, out l)) map[region] = l = new List<byte>();
                l.Add((byte)e.ScopeArg);
            }
            foreach (var l in map.Values) l.Sort();
            return map;
        }

        public static byte OwnerOf(StateRegistry reg, int region)
        {
            List<byte> l;
            return Occupants(reg).TryGetValue(region, out l) && l.Count > 0 ? l[0] : (byte)0;
        }

        public static int RegionOf(StateRegistry reg, byte slot)
        {
            var e = reg.Get(StateScope.Player, slot, WorldKeys.Region);
            int region; return e != null && WorldKeys.TryInt(e.Value, out region) ? region : Regions.None;
        }
    }
}
