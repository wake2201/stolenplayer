// M5 W4.1 shared pre-placed world loot (pure logic, offline-tested; Unity side: src/Plugin/WorldLootSync.cs).
//
// Verified original lifecycle (Cecil IL, Assembly-CSharp 1.27c, W4.1 Phase A):
//  - PlayerScript.Update IL_799f (tag "Pickupable") -> original weight / max_amount / cant_pick_reason checks (IL_7be4-7c4d) ->
//    Pickupable.PickUp() IL_7c5a (its only caller) -> coroutine <PickingItem>d__72 (~0.475 s, three waits). Only its LAST
//    step is irreversible: money (IL_0333), SetDayRespawn (IL_0353), ItemID.Item_Picked (IL_037f: backpack +1, weight, XP,
//    "Item_<objectName>" = 1), drone/contract/parcel/story effects, Destroy (IL_046f) - all in one MoveNext step.
//  - SetDayRespawn writes "ItemDay_<objectName>" = Inventory.daysInGame + 5/4/3/2 for item_value > 300/200/100/0 (money 5),
//    only for objects not named "dropped" with xpAdd > 0 or money. <Start>d__68 destroys the object while
//    daysInGame < ItemDay_<name> (unless dontDestroyDays / never_destroy). The key is per NAME: objects sharing a name share it.
//  - SelectResidence.RefreshIfComplete reads "Item_<loot_prefs>" -> the Item_<objectName> flag is world state too.
//
// Identity = the W1 census id (namespace + FNV-1a of scene/hierarchy path/kind), never the name. The original keys keep their
// original (name-based) meaning; a shared-name family is excluded from the Phase B subset (LootFlags.SharedName) because
// one key serves several physical objects (strategy for Phase C: docs/PROGRESS.md "W4.1 Phase B", shared-name families).
//
// Authority: host. Player 1 = the original pickup on the host; a claim is pending from Pickupable.PickUp until the original
// reached SetDayRespawn for THAT instance (= the irreversible step), then the object is "taken". Player 2 = a lootTxn
// proposal; the host validates against ITS OWN census catalog, commits the Player-2 record +1 and a durable world-take
// entry in one atomic p2inv write, writes the original keys, and only then is the client's original coroutine resumed.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    // Serialized Pickupable fields read by the census at sceneLoaded (before any Start). One bit per exclusion reason.
    [Flags]
    public enum LootFlags : uint
    {
        None = 0,
        Heavy = 1 << 0,            // is_heavy_item: PickUpHeavy flow (W4.2)
        Money = 1 << 1,            // is_money: money, not an item (economy milestone)
        DestroyIfPicked = 1 << 2,  // destroy_if_was_picked: unique item (Item_<name> = permanent removal)
        DeleteIfPref = 1 << 3,     // deleteIfPref: removed by a mission/story pref
        Parcel = 1 << 4,           // lock_parcel_after_pickup: parcel mission
        StoryPick = 1 << 5,        // advance_story_if_picked
        StoryCar = 1 << 6,         // advance_story_if_placed_car
        StoryName = 1 << 7,        // story_name set
        Contract = 1 << 8,         // isContractItem
        NeverDestroy = 1 << 9,     // never_destroy: no day-based removal
        NoDays = 1 << 10,          // dontDestroyDays: no day-based removal
        NoXp = 1 << 11,            // add_any_XP false: SetDayRespawn writes no key (original respawn on every reload)
        CantPick = 1 << 12,        // cant_pick_reason
        Dropped = 1 << 13,         // a dropped item ("dropped" / wasDropped): W3 path
        RandomCabinet = 1 << 14,   // inside RandomFileCabinet contents: activated at random per instance
        SharedName = 1 << 15,      // another loot object of the namespace has the same name: shared original keys
        NumericName = 1 << 16,     // Item_<name> would collide with a backpack count key Item_<id>
        Ambiguous = 1 << 17,       // census id ambiguous (duplicate path / hash collision)
        LongName = 1 << 18,        // name too long for the durable entry
    }

    // ItemID facts of the item type (player's ItemID list; same on both instances, read by the deciding instance itself).
    public struct ItemFacts
    {
        public bool Known, Heavy, Car, Drone, Story, Mission, Tool, Special;
        public float Value, AddXp; public int MaxAmount;
    }

    public sealed class LootEntry
    {
        public uint Hash; public string Name; public int ItemId; public LootFlags Flags;
        public object Target;   // live Pickupable component (null after the scene unloaded; metadata stays)
    }

    public static class LootRules
    {
        public const int MaxNameChars = 120;
        // Phase B vertical slice: Madison Street (region scene "MadisonSt", census: 869 loot objects, digest identical on
        // both instances). MainScene loot (namespace -1000, present in every region) and other regions: Phase C.
        static readonly HashSet<int> Supported = new HashSet<int> { 99 };
        public static bool SupportedNamespace(int ns) { return Supported.Contains(ns); }

        public static bool IsNumeric(string s) { int v; return !string.IsNullOrEmpty(s) && int.TryParse(s, out v); }

        // Reason the object is outside the supported subset, or null.
        public static string Exclusion(LootFlags f)
        {
            if (f == LootFlags.None) return null;
            for (int b = 0; b < 32; b++) { var x = (LootFlags)(1u << b); if ((f & x) != 0) return x.ToString(); }
            return f.ToString();
        }

        public static string Exclusion(ItemFacts it)
        {
            if (!it.Known) return "unknown item type";
            if (it.Heavy) return "heavy item type";
            if (it.Car) return "car item";
            if (it.Drone) return "drone";
            if (it.Story) return "story item";
            if (it.Mission) return "mission item";
            if (it.Tool) return "tool (no XP, no respawn key)";
            if (it.Special) return "special item (special-skill reward)";
            if (RespawnDays(it.Value, false) <= 0) return "no value (no respawn key)";
            if (XpAdd(it.AddXp) < 1) return "no XP (no respawn key)";
            return null;
        }

        // Pickupable.SetDayRespawn tiers (IL_0000-0061).
        public static int RespawnDays(float value, bool money)
        {
            if (money) return 5;
            if (value > 300f) return 5; if (value > 200f) return 4; if (value > 100f) return 3; if (value > 0f) return 2;
            return 0;
        }

        // Pickupable.<Start> xpAdd = Mathf.RoundToInt(add_XP [x10]) (banker's rounding like Mathf.RoundToInt); the original
        // writes the respawn key only for xpAdd > 0. Requiring >= 1 without the x10 bonus makes both cases write it.
        public static int XpAdd(float addXp) { return (int)Math.Round(addXp, MidpointRounding.ToEven); }
    }

    // One namespace of verified loot, built by the census at sceneLoaded. Metadata is kept after the scene unloads (the host
    // validates a Player-2 pickup in a region it visited before in this process); live targets are released.
    public sealed class LootCatalog
    {
        public const int MaxEntries = 4096;
        public readonly int Namespace; public uint Digest;
        readonly Dictionary<uint, LootEntry> _byHash = new Dictionary<uint, LootEntry>();
        readonly Dictionary<int, uint> _byInstance = new Dictionary<int, uint>();
        public int Eligible { get; private set; }
        public LootCatalog(int ns) { Namespace = ns; }
        public int Count { get { return _byHash.Count; } }
        public bool Live { get; private set; }

        public bool Add(uint hash, string name, int itemId, LootFlags flags, object target, int instanceId)
        {
            if (_byHash.Count >= MaxEntries) return false;
            if (_byHash.ContainsKey(hash)) { _byHash[hash].Flags |= LootFlags.Ambiguous; return false; }
            if (name == null || name.Length > LootRules.MaxNameChars) flags |= LootFlags.LongName;
            if (LootRules.IsNumeric(name)) flags |= LootFlags.NumericName;
            _byHash[hash] = new LootEntry { Hash = hash, Name = name ?? "", ItemId = itemId, Flags = flags, Target = target };
            if (target != null) { _byInstance[instanceId] = hash; Live = true; }
            return true;
        }

        // After all entries were added: names shared within the namespace (shared original keys) and census ambiguity.
        public void Seal(Func<uint, bool> ambiguous)
        {
            var n = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in _byHash.Values) { int c; n.TryGetValue(e.Name, out c); n[e.Name] = c + 1; }
            Eligible = 0;
            foreach (var e in _byHash.Values)
            {
                if (n[e.Name] > 1) e.Flags |= LootFlags.SharedName;
                if (ambiguous != null && ambiguous(e.Hash)) e.Flags |= LootFlags.Ambiguous;
                if (e.Flags == LootFlags.None) Eligible++;
            }
        }

        public bool TryGet(uint hash, out LootEntry e) { return _byHash.TryGetValue(hash, out e); }
        public bool TryByInstance(int instanceId, out LootEntry e) { uint h; e = null; return _byInstance.TryGetValue(instanceId, out h) && _byHash.TryGetValue(h, out e); }
        public IEnumerable<LootEntry> Entries { get { return _byHash.Values; } }
        public void ReleaseTargets() { foreach (var e in _byHash.Values) e.Target = null; _byInstance.Clear(); Live = false; }
        public int SharedNameObjects { get { int s = 0; foreach (var e in _byHash.Values) if ((e.Flags & LootFlags.SharedName) != 0) s++; return s; } }
    }

    // Client proposal "lootTxn" (Session scope, arg 0): Player 2 picks up one verified world object. 20 bytes.
    public struct LootTxnRequest
    {
        public const int WireSize = 20;
        public uint Txn; public int Ns; public uint Hash; public int ItemId; public uint Digest;
        public byte[] Encode() { var w = new NetDataWriter(); w.Put(Txn); w.Put(Ns); w.Put(Hash); w.Put(ItemId); w.Put(Digest); return w.CopyData(); }
        public static bool TryParse(byte[] v, out LootTxnRequest t)
        {
            t = default(LootTxnRequest);
            if (v == null || v.Length != WireSize) return false;
            t.Txn = BitConverter.ToUInt32(v, 0); t.Ns = BitConverter.ToInt32(v, 4); t.Hash = BitConverter.ToUInt32(v, 8); t.ItemId = BitConverter.ToInt32(v, 12); t.Digest = BitConverter.ToUInt32(v, 16);
            return t.Txn != 0;
        }
    }

    // A durable world acquisition: the original keys the host save must contain for it (ItemDay_<name> = Day, Item_<name> = 1).
    public struct LootTake
    {
        public int Ns; public uint Hash; public int ItemId; public int Day; public bool ItemFlag; public string Name;
        public void Write(NetDataWriter w) { w.Put(Ns); w.Put(Hash); w.Put(ItemId); w.Put(Day); w.Put(ItemFlag ? (byte)1 : (byte)0); w.Put(Name ?? ""); }
        public static bool TryRead(NetDataReader r, out LootTake t)
        {
            t = default(LootTake);
            try
            {
                t.Ns = r.GetInt(); t.Hash = r.GetUInt(); t.ItemId = r.GetInt(); t.Day = r.GetInt(); byte f = r.GetByte(); t.Name = r.GetString(LootRules.MaxNameChars + 8);
                if (f > 1 || string.IsNullOrEmpty(t.Name) || t.Name.Length > LootRules.MaxNameChars || LootRules.IsNumeric(t.Name) || t.Name == "dropped") return false;
                t.ItemFlag = f == 1; return true;
            }
            catch { return false; }
        }
        public override string ToString() { return "'" + Name + "' (" + IdentityNamespace.Name(Ns) + ":" + Hash.ToString("X8") + ", item " + ItemId + ", ItemDay " + Day + ")"; }
    }

    public struct WorldTakeEntry { public uint Seq; public LootTake Take; }

    // Original-key effects of a world take (host SPrefs, or a dictionary in tests).
    public interface IKeyStore { int Get(string key); void Set(string key, int value); }

    public static class LootKeys
    {
        public static string Day(string name) { return "ItemDay_" + name; }
        public static string Picked(string name) { return "Item_" + name; }
        // Never moves a respawn day backwards (a later original pick of a same-named object may already be newer).
        public static void Apply(IKeyStore k, LootTake t)
        {
            string d = Day(t.Name);
            if (k.Get(d) < t.Day) k.Set(d, t.Day);
            if (t.ItemFlag && k.Get(Picked(t.Name)) != 1) k.Set(Picked(t.Name), 1);
        }
    }

    public enum LootState : byte { Available, Pending, Taken }

    // Host-only: taken objects per namespace (registry, Region scope, arg = namespace = region id, 4 chunk keys) + Player-1
    // claims pending until the original reached its irreversible step. The original scene reload re-creates every object, so
    // the live set is cleared when nobody is in that region any more; persistence after that is the original ItemDay_ key.
    public sealed class LootBook
    {
        public const int Chunks = 4, MaxPerChunk = 250;   // 2 + 4 x 250 = 1002 B <= StateEntry.MaxValueBytes
        public static readonly string[] Keys = { "wloot0", "wloot1", "wloot2", "wloot3" };
        public static int ChunkOf(uint hash) { return (int)(hash & (Chunks - 1)); }

        readonly Dictionary<int, HashSet<uint>> _taken = new Dictionary<int, HashSet<uint>>();
        readonly Dictionary<long, int> _pending = new Dictionary<long, int>();   // (ns, hash) -> claim token (Pickupable instance id)
        public long Claims, Commits, Releases, Refused, Cleared, Full;
        static long K(int ns, uint h) { return ((long)ns << 32) | h; }

        public LootState StateOf(int ns, uint hash)
        {
            HashSet<uint> s;
            if (_taken.TryGetValue(ns, out s) && s.Contains(hash)) return LootState.Taken;
            return _pending.ContainsKey(K(ns, hash)) ? LootState.Pending : LootState.Available;
        }
        public int TakenCount(int ns) { HashSet<uint> s; return _taken.TryGetValue(ns, out s) ? s.Count : 0; }
        public int PendingCount { get { return _pending.Count; } }

        // Player 1: the original pickup starts. False = refused (taken / pending / chunk full): nothing happens.
        public bool Claim(int ns, uint hash, int token, out string why)
        {
            why = null;
            var st = StateOf(ns, hash);
            if (st != LootState.Available) { Refused++; why = st == LootState.Taken ? "already taken" : "pickup already in progress"; return false; }
            if (ChunkCount(ns, ChunkOf(hash)) >= MaxPerChunk) { Full++; Refused++; why = "region loot list full"; return false; }
            _pending[K(ns, hash)] = token; Claims++;
            return true;
        }
        public bool HasClaim(int ns, uint hash, int token) { int t; return _pending.TryGetValue(K(ns, hash), out t) && t == token; }
        // The original pickup was interrupted before its irreversible step: the object is available again.
        public bool Release(int ns, uint hash, int token)
        {
            int t; long k = K(ns, hash);
            if (!_pending.TryGetValue(k, out t) || t != token) return false;
            _pending.Remove(k); Releases++; return true;
        }

        // Player 2 (validated + durable) or a pending Player-1 claim whose original reached SetDayRespawn.
        public ProposalCode Commit(int ns, uint hash, StateRegistry reg, List<StateEntry> outbox)
        {
            _pending.Remove(K(ns, hash));
            HashSet<uint> s;
            if (!_taken.TryGetValue(ns, out s)) _taken[ns] = s = new HashSet<uint>();
            if (s.Contains(hash)) return ProposalCode.Unchanged;
            int c = ChunkOf(hash);
            if (ChunkCount(ns, c) >= MaxPerChunk) { Full++; return ProposalCode.Busy; }
            s.Add(hash); Commits++;
            CommitChunk(ns, c, reg, outbox);
            return ProposalCode.Accepted;
        }

        // Room for one more in that chunk (checked BEFORE a Player-2 durable write, so a commit can never fail after it).
        public bool HasRoom(int ns, uint hash) { return ChunkCount(ns, ChunkOf(hash)) < MaxPerChunk; }

        public int ClearRegion(int ns, StateRegistry reg, List<StateEntry> outbox)
        {
            var drop = new List<long>();
            foreach (var kv in _pending) if ((int)(kv.Key >> 32) == ns) drop.Add(kv.Key);
            foreach (var k in drop) _pending.Remove(k);
            HashSet<uint> s;
            if (!_taken.TryGetValue(ns, out s) || s.Count == 0) return 0;
            int n = s.Count; s.Clear(); Cleared += n;
            for (int c = 0; c < Chunks; c++) CommitChunk(ns, c, reg, outbox);
            return n;
        }

        // Taken + pending of one chunk: a pending Player-1 claim reserves its slot, so its commit can never be refused.
        int ChunkCount(int ns, int chunk)
        {
            HashSet<uint> s; int n = 0;
            if (_taken.TryGetValue(ns, out s)) foreach (var h in s) if (ChunkOf(h) == chunk) n++;
            foreach (var k in _pending.Keys) if ((int)(k >> 32) == ns && ChunkOf((uint)k) == chunk) n++;
            return n;
        }

        void CommitChunk(int ns, int chunk, StateRegistry reg, List<StateEntry> outbox)
        {
            var l = new List<uint>(); HashSet<uint> s;
            if (_taken.TryGetValue(ns, out s)) foreach (var h in s) if (ChunkOf(h) == chunk) l.Add(h);
            l.Sort();
            var e = reg.Commit(StateScope.Region, ns, Keys[chunk], Encode(l));
            if (e != null) outbox.Add(e);
        }

        public static byte[] Encode(List<uint> hashes)
        {
            var w = new NetDataWriter(); w.Put((ushort)hashes.Count);
            foreach (var h in hashes) w.Put(h);
            return w.CopyData();
        }
        public static bool TryParse(byte[] v, int chunk, HashSet<uint> into)
        {
            if (v == null || v.Length < 2) return false;
            int n = BitConverter.ToUInt16(v, 0);
            if (n > MaxPerChunk || v.Length != 2 + 4 * n) return false;
            for (int i = 0; i < n; i++) { uint h = BitConverter.ToUInt32(v, 2 + 4 * i); if (ChunkOf(h) != chunk) return false; into.Add(h); }
            return true;
        }
    }

    // Host decision for a Player-2 pickup, in this order (a retransmission is answered before anything else is looked at).
    public static class LootDecision
    {
        public static ProposalCode DecideP2(uint txn, uint appliedTxn, bool inRegion, LootCatalog cat, uint clientDigest, uint hash, int itemId,
                                            ItemFacts facts, LootState state, bool room, int recordCount, out string why)
        {
            why = null;
            if (txn != 0 && txn <= appliedTxn) return ProposalCode.Unchanged;
            if (txn != appliedTxn + 1) { why = "out of order"; return ProposalCode.Stale; }
            if (!inRegion) { why = "not in that region"; return ProposalCode.NotAuthorized; }
            if (cat == null) { why = "host has no verified catalog of that region (not visited by the host in this process)"; return ProposalCode.NotAuthorized; }
            if (!LootRules.SupportedNamespace(cat.Namespace)) { why = "region not supported"; return ProposalCode.NotAuthorized; }
            if (cat.Digest != clientDigest) { why = "scene data differs between the instances (census digest)"; return ProposalCode.Invalid; }
            LootEntry e;
            if (!cat.TryGet(hash, out e)) { why = "unknown object"; return ProposalCode.Invalid; }
            if (e.Flags != LootFlags.None) { why = "excluded: " + LootRules.Exclusion(e.Flags); return ProposalCode.Invalid; }
            if (e.ItemId != itemId) { why = "item type mismatch"; return ProposalCode.Invalid; }
            var x = LootRules.Exclusion(facts);
            if (x != null) { why = "excluded: " + x; return ProposalCode.Invalid; }
            if (state == LootState.Taken) { why = "already taken"; return ProposalCode.Stale; }
            if (state == LootState.Pending) { why = "Player 1's pickup of it is in progress"; return ProposalCode.Busy; }
            if (!room) { why = "region loot list full"; return ProposalCode.Busy; }
            if (facts.MaxAmount > 0 && recordCount + 1 > facts.MaxAmount) { why = "max_amount"; return ProposalCode.Invalid; }
            if (recordCount >= ushort.MaxValue) { why = "count"; return ProposalCode.Invalid; }
            return ProposalCode.Accepted;
        }
    }

    // Host memory: where a unit of Player 1 that Player 2 now durably owns came from.
    // A durable Player-1 loss (journal HostItem -1: Player 2 picked up Player 1's drop, or withdrew Player 1's unsaved storage
    // deposit) is replayed after an unsaved quit as "Item_<id> - 1, clamped at 0". If Player 1's RECOVERABLE count of that type
    // (the count a reload of the save gives back = saved key + journaled host deltas) is >= 1, a saved unit is consumed and
    // every unsaved acquisition rolls back together with its world state: conserved. If it is <= 0 the clamp absorbs the
    // loss: the unit is an unsaved acquisition. If it was an unsaved world-loot pickup of Player 1 (tracked here from the
    // original SetDayRespawn), its world take is journaled durably in the same write, so the loot cannot respawn while
    // Player 2 keeps the item. Saved stock is attributed first (never a new pickup for a unit the save already contains).
    // Bases are read lazily (first use per item type after a save/load boundary), always before the mod writes that key.
    public enum Attribution { SavedUnit, WorldTake, Unattributed }

    public sealed class LootProvenance
    {
        public const int MaxTakes = 4096;
        readonly Dictionary<int, int> _base = new Dictionary<int, int>(), _delta = new Dictionary<int, int>();
        readonly Dictionary<int, List<LootTake>> _unsaved = new Dictionary<int, List<LootTake>>();
        int _takes;
        public long Resets, Picks, Saved, Attributed, Unattributed, Dropped;
        public int Tracked { get { return _takes; } }

        // Original save disk write / load (HostBind after replay): every earlier effect is in the save.
        public void Reset() { _base.Clear(); _delta.Clear(); _unsaved.Clear(); _takes = 0; Resets++; }

        public void EnsureBase(int item, int key) { if (!_base.ContainsKey(item)) _base[item] = Math.Max(0, key); }
        public int Recoverable(int item, int key) { EnsureBase(item, key); int d; _delta.TryGetValue(item, out d); return _base[item] + d; }
        public int UnsavedPicks(int item) { List<LootTake> l; return _unsaved.TryGetValue(item, out l) ? l.Count : 0; }

        public void P1Picked(LootTake t)
        {
            if (_takes >= MaxTakes) { Dropped++; return; }
            List<LootTake> l; if (!_unsaved.TryGetValue(t.ItemId, out l)) _unsaved[t.ItemId] = l = new List<LootTake>();
            l.Add(t); _takes++; Picks++;
        }

        // A durable Player-1 gain (Player 1 picked up Player 2's drop: journal HostItem +1).
        public void P1Gained(int item, int key) { EnsureBase(item, key); int d; _delta.TryGetValue(item, out d); _delta[item] = d + 1; }

        // A durable Player-1 loss (journal HostItem -1). key = Player 1's Item_<id> key before the mod changes it.
        public Attribution P1Lost(int item, int key, out LootTake take)
        {
            take = default(LootTake);
            int r = Recoverable(item, key);
            int d; _delta.TryGetValue(item, out d); _delta[item] = d - 1;
            if (r >= 1) { Saved++; return Attribution.SavedUnit; }
            List<LootTake> l;
            if (_unsaved.TryGetValue(item, out l) && l.Count > 0) { take = l[l.Count - 1]; l.RemoveAt(l.Count - 1); _takes--; Attributed++; return Attribution.WorldTake; }
            Unattributed++; return Attribution.Unattributed;
        }

        // Undo of P1Lost when the durable write failed.
        public void UndoLost(int item, Attribution a, LootTake take)
        {
            int d; _delta.TryGetValue(item, out d); _delta[item] = d + 1;
            if (a == Attribution.SavedUnit) Saved--;
            else if (a == Attribution.WorldTake) { List<LootTake> l; if (!_unsaved.TryGetValue(item, out l)) _unsaved[item] = l = new List<LootTake>(); l.Add(take); _takes++; Attributed--; }
            else Unattributed--;
        }
        public void UndoGained(int item) { int d; _delta.TryGetValue(item, out d); _delta[item] = d - 1; }
    }
}
