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
// original (name-based) meaning.
// v0.16.1 shared-name families (LootFlags.SharedName, e.g. Madison 'Pot', 'Wine1': 31 objects / 12 names): shared by census
// id like every other object. Vanilla semantics are kept exactly: picking ANY member writes the family's ItemDay_<name> /
// Item_<name> (LootKeys.Apply never moves a day backwards) and the next scene load destroys EVERY member (<Start>d__68), while
// the other members stay pickable until that reload. Hence a family member's "already taken" is decided by the host's live
// object (PersistentlyTaken overload) while the host has the scene loaded, and by the key only across a reload.
// Player 2 (v0.16.1): whatever is NOT shared (unsupported region, MainScene/runtime object, excluded class) is refused on the
// client (P2PickPolicy) - a vanilla local pickup there would hand out a second copy of the reward.
//
// W4.1B: LootRules.Classify adds unique valuables (destroy_if_was_picked, Item_<name> = permanent removal) and world money
// (is_money + add_money, credited to the shared balance, src/Shared/Wallet.cs); LootRules.PersistentlyTaken refuses a take
// the host save already removed (respawn day in the future / unique item picked).
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
        public bool Known, Heavy, Car, Drone, Story, Mission, Tool, Special, Key;
        public float Value, AddXp; public int MaxAmount;
    }

    public sealed class LootEntry
    {
        public uint Hash; public string Name; public int ItemId; public LootFlags Flags;
        public int Money;       // W4.1B: Pickupable.add_money (serialized, read by the census; only meaningful with LootFlags.Money)
        public object Target;   // live Pickupable component (null after the scene unloaded; metadata stays)
        // W4.2D host catalog (SceneCatalogBuilder) only: Pickupable.parcel_pref, and the RandomFileCabinet slot this object
        // belongs to (Group 0 = none; Outcome 0 = the slot's default contents "<x>CabinetDisable", k + 1 = list element k).
        public string Pref; public uint Group; public int Outcome;
    }

    // Shared-loot classes (LootRules.Classify).
    //   Valuable       ordinary item whose original pickup writes a respawn key (SetDayRespawn: value > 0, xpAdd >= 1)
    //   UniqueValuable destroy_if_was_picked (Item_<name> = 1 removes it for good; keys included since W4.2D)
    //   Money          is_money with add_money > 0 (shared balance, W4.1B)
    //   LiveOnly       W4.2D: ordinary item whose original pickup writes NO respawn key (no XP / no value / tool /
    //                  never_destroy / dontDestroyDays): vanilla re-creates it at the next scene load, so it is shared for the
    //                  current region occupancy only (book); no ItemDay_ key is written for it.
    public enum LootClass : byte { Excluded, Valuable, UniqueValuable, Money, LiveOnly }

    public enum P2PickPolicy : byte { Shared, LocalOwnDrop, Refuse }

    public static class LootRules
    {
        public const int MaxNameChars = 120;
        // W4.2D: every census namespace (each original scene with a region id, plus MainScene). The host validates against its
        // own scene-file catalog (SceneCatalogBuilder), so it no longer needs to have visited the region in this process.
        public static bool SupportedNamespace(int ns) { return ns != IdentityNamespace.None && (ns == IdentityNamespace.MainScene || Regions.IsKnown(ns)); }

        // Flags that change nothing about sharing an object by census id (see Classify). Everything else excludes it.
        public const LootFlags Neutral = LootFlags.SharedName | LootFlags.DestroyIfPicked | LootFlags.NoXp | LootFlags.NoDays | LootFlags.NeverDestroy
                                       | LootFlags.CantPick | LootFlags.DeleteIfPref | LootFlags.Contract | LootFlags.Parcel | LootFlags.RandomCabinet;
        // Still excluded (W4.2D, precise reasons in docs/PROGRESS.md): heavy items (carry / trunk / van / shed / shop transaction
        // family), story pickups (StoryManager.AdvanceStory needs story sync), dropped (W3 path), ids without a stable identity.
        public const LootFlags Blocking = LootFlags.Heavy | LootFlags.StoryPick | LootFlags.StoryCar | LootFlags.StoryName | LootFlags.Dropped
                                        | LootFlags.NumericName | LootFlags.Ambiguous | LootFlags.LongName;

        public static bool IsNumeric(string s) { int v; return !string.IsNullOrEmpty(s) && int.TryParse(s, out v); }

        // Reason the object is outside the supported subset, or null.
        public static string Exclusion(LootFlags f)
        {
            if (f == LootFlags.None) return null;
            for (int b = 0; b < 32; b++) { var x = (LootFlags)(1u << b); if ((f & x) != 0) return x.ToString(); }
            return f.ToString();
        }

        // W4.2D class rules. Neutral flags are verified to need nothing beyond the shared take:
        //   SharedName   shared by census id, the family key keeps its per-name meaning (v0.16.1)
        //   CantPick     the original refuses before Pickupable.PickUp (PlayerScript.Update IL_7ca6) - never reaches the hook
        //   DeleteIfPref presence decided by a campaign pref at Start (the client loads the host's campaign keys)
        //   Contract     the pickup only shows "contract_item_found" (<PickingItem> final step); the contract itself is completed
        //                by the host's original ClaimReward with the item (Player 2 hands it over via shared storage / drop)
        //   Parcel       the pickup moves parcel_pref from GameManager.parcel_strings_to_set to mission_strings_to_set - the host
        //                applies exactly that to ITS GameManager when it accepts Player 2's take (WorldLootSync.HostParcel)
        //   RandomCabinet slot contents are rolled per instance: both instances seed the original roll identically and the host
        //                allows only one outcome per slot and occupancy (CabinetBook)
        public static LootClass Classify(LootEntry e, ItemFacts it, out string why)
        {
            why = null;
            if (e == null) { why = "unknown object"; return LootClass.Excluded; }
            var block = e.Flags & Blocking;
            if (block != LootFlags.None) { why = Exclusion(block); return LootClass.Excluded; }
            if ((e.Flags & LootFlags.Money) != 0)
            {
                if (e.Money <= 0 || e.Money > WalletRules.MaxLootMoney) { why = "money amount " + e.Money; return LootClass.Excluded; }
                return LootClass.Money;   // SetDayRespawn writes the money key regardless of XP (IL_0000-0061)
            }
            why = Exclusion(it);
            if (why != null) return LootClass.Excluded;
            if ((e.Flags & LootFlags.DestroyIfPicked) != 0) return LootClass.UniqueValuable;   // removal by Item_<name>, independent of XP / value
            if ((e.Flags & (LootFlags.NoXp | LootFlags.NoDays | LootFlags.NeverDestroy)) != 0 || it.Tool || RespawnDays(it.Value, false) <= 0 || XpAdd(it.AddXp) < 1)
                return LootClass.LiveOnly;
            return LootClass.Valuable;
        }

        // ItemDay_<name> the host writes for an accepted take: the original SetDayRespawn tier; 0 (= nothing written, LootKeys.Apply
        // never lowers a day) for LiveOnly and for objects without a respawn tier (the original writes no future day either).
        public static int TakeDay(LootClass c, int today, float value)
        {
            if (c == LootClass.Money) return today + RespawnDays(0, true);
            if (c == LootClass.LiveOnly) return 0;
            int d = RespawnDays(value, false);
            return d > 0 ? today + d : 0;
        }

        // W4.1B: the host's save already says this object is gone: ItemDay_<name> lies in the future (original <Start>d__68
        // destroys it) or, for a unique valuable, Item_<name> = 1 (destroy_if_was_picked). LiveOnly has no key: book only.
        public static bool PersistentlyTaken(LootClass c, int itemDayKey, int pickedKey, int today)
        {
            if (c == LootClass.Excluded || c == LootClass.LiveOnly) return false;
            if (itemDayKey > today) return true;
            return c == LootClass.UniqueValuable && pickedKey == 1;
        }

        // W4.2D host decision while the region is occupied: the region book was seeded with the original reload rule when the
        // occupancy began (LootBook.IsSeeded). A shared-name family member is then decided by the book alone (a sibling's key
        // written later does not remove it - vanilla lets the player pick it until the next reload); every other object also
        // checks its own key (written by a pickup the book already knows, or by an unsaved-exit replay).
        public static bool TakenByKeys(LootClass c, bool sharedName, int itemDayKey, int pickedKey, int today)
        {
            if (sharedName) return false;
            return PersistentlyTaken(c, itemDayKey, pickedKey, today);
        }

        // v0.16.1 overload (family rule with a live host scene), kept for diagnostics / tests.
        public static bool PersistentlyTaken(LootClass c, bool sharedName, bool hostSceneLive, bool hostObjectAlive, int itemDayKey, int pickedKey, int today)
        {
            if (c == LootClass.Excluded) return false;
            if (sharedName && hostSceneLive) return !hostObjectAlive;
            return PersistentlyTaken(c, itemDayKey, pickedKey, today);
        }

        // v0.16.1 Player-2 pickup policy (client). Shared = host transaction; LocalOwnDrop = Player 2's own unshared local drop
        // (exists only in Player 2's world, picking it back is no duplicate); Refuse = nothing happens (like any refused
        // original interaction): no host transaction can cover the object, and a vanilla local pickup would let both players
        // collect the same world object.
        public static P2PickPolicy ClientPolicy(bool supportedRegion, bool inCatalog, LootClass cls, bool droppedObject)
        {
            if (droppedObject && !inCatalog) return P2PickPolicy.LocalOwnDrop;
            if (!supportedRegion || !inCatalog || cls == LootClass.Excluded) return P2PickPolicy.Refuse;
            return P2PickPolicy.Shared;
        }

        // W4.2D: tools and keys are shared inventory units (unlocking stays the W4.2B lock sync).
        public static string Exclusion(ItemFacts it)
        {
            if (!it.Known) return "unknown item type";
            if (it.Heavy) return "heavy item type";
            if (it.Car) return "car item";
            if (it.Drone) return "drone (DroneManager.DronePickedUp equips the picker's drone)";
            if (it.Story) return "story item";
            if (it.Mission) return "mission item";
            if (it.Special) return "special item (special-skill reward)";
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

        public bool Add(uint hash, string name, int itemId, LootFlags flags, object target, int instanceId) { return Add(hash, name, itemId, flags, target, instanceId, 0); }
        public bool Add(uint hash, string name, int itemId, LootFlags flags, object target, int instanceId, int money)
        {
            if (_byHash.Count >= MaxEntries) return false;
            if (_byHash.ContainsKey(hash)) { _byHash[hash].Flags |= LootFlags.Ambiguous; return false; }
            if (name == null || name.Length > LootRules.MaxNameChars) flags |= LootFlags.LongName;
            if (LootRules.IsNumeric(name)) flags |= LootFlags.NumericName;
            _byHash[hash] = new LootEntry { Hash = hash, Name = name ?? "", ItemId = itemId, Flags = flags, Target = target, Money = money };
            if (target != null) { _byInstance[instanceId] = hash; Live = true; }
            return true;
        }

        // After all entries were added: names shared within the namespace (shared original keys) and census ambiguity.
        public void Seal(Func<uint, bool> ambiguous)
        {
            var n = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in _byHash.Values) { int c; n.TryGetValue(e.Name, out c); n[e.Name] = c + 1; }
            Eligible = 0; EligibleMoney = 0; EligibleUnique = 0;
            foreach (var e in _byHash.Values)
            {
                if (n[e.Name] > 1) e.Flags |= LootFlags.SharedName;
                if (ambiguous != null && ambiguous(e.Hash)) e.Flags |= LootFlags.Ambiguous;
                var f = e.Flags & ~LootFlags.SharedName;   // v0.16.1: shared-name family members are eligible
                if (f == LootFlags.None) Eligible++;
                else if (f == LootFlags.DestroyIfPicked) EligibleUnique++;
                else if ((f & ~(LootFlags.Money | LootFlags.NoXp)) == LootFlags.None && (f & LootFlags.Money) != 0 && e.Money > 0) EligibleMoney++;
            }
        }
        // W4.1B flag-level counts (item facts are checked per pickup): unique valuables, money objects.
        public int EligibleMoney { get; private set; }
        public int EligibleUnique { get; private set; }

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

        // W4.2D: a namespace's book is "seeded" once the host listed, at the start of the current occupancy, every object the
        // original reload removes (WorldLootSync.SeedRegion). Player-2 takes wait for it (Busy). Cleared with the occupancy.
        readonly HashSet<int> _seeded = new HashSet<int>();
        public bool IsSeeded(int ns) { return _seeded.Contains(ns); }
        public void MarkSeeded(int ns) { _seeded.Add(ns); }
        // Cabinet slot outcomes of the current occupancy (one roll per slot is shared; see CabinetBook).
        public readonly CabinetBook Cabinets = new CabinetBook();
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
            _seeded.Remove(ns); Cabinets.ClearRegion(ns);
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

    // W4.2D RandomFileCabinet slots (verified IL, RandomFileCabinet.Start): per slot (first / sec / th) one roll
    // Random.Range(0, 20) > 17 hides "<slot>CabinetDisable" (the default contents) and activates ONE random element of
    // "<slot>CabinetRandom". A slot therefore shows exactly one outcome per scene load: 0 = default contents, k + 1 = element k.
    // Both instances seed that original roll identically (WorldLootSync.CabinetPrefix), and the host lets the first taken
    // object of a slot fix its outcome for the occupancy: objects of another outcome of the same slot are refused (a different
    // local roll can never yield a second reward from one slot). Several objects of the SAME outcome stay pickable.
    public sealed class CabinetBook
    {
        readonly Dictionary<long, int> _outcome = new Dictionary<long, int>();
        static long K(int ns, uint g) { return ((long)ns << 32) | g; }
        public long Refused;
        public bool Allowed(int ns, uint group, int outcome) { int o; return group == 0 || !_outcome.TryGetValue(K(ns, group), out o) || o == outcome; }
        public void Take(int ns, uint group, int outcome) { if (group != 0 && !_outcome.ContainsKey(K(ns, group))) _outcome[K(ns, group)] = outcome; }
        public int OutcomeCount { get { return _outcome.Count; } }
        public void ClearRegion(int ns)
        {
            var drop = new List<long>();
            foreach (var k in _outcome.Keys) if ((int)(k >> 32) == ns) drop.Add(k);
            foreach (var k in drop) _outcome.Remove(k);
        }
        // Seed of the original roll: same cabinet identity + same campaign day on both instances -> same Random sequence.
        public static int Seed(uint cabinetHash, int day) { return (int)IdentityHash.Append(IdentityHash.Append(IdentityHash.Fnv1a32("cabinet"), cabinetHash.ToString("X8")), day.ToString()); }
    }

    // Host decision for a Player-2 pickup, in this order (a retransmission is answered before anything else is looked at).
    public static class LootDecision
    {
        public static ProposalCode DecideP2(uint txn, uint appliedTxn, bool inRegion, LootCatalog cat, uint clientDigest, uint hash, int itemId,
                                            ItemFacts facts, LootState state, bool room, int recordCount, out string why)
        {
            LootClass cls;
            return DecideP2(txn, appliedTxn, inRegion, cat, clientDigest, hash, itemId, facts, state, room, recordCount, false, out cls, out why);
        }

        // W4.1B: class-aware (ordinary valuable, unique valuable, money). persistTaken = LootRules.PersistentlyTaken on the
        // host's own keys (an object the host save already removed is never handed out again, e.g. after the live taken list
        // of an emptied region was cleared). Money has no backpack unit (no max_amount / count check).
        public static ProposalCode DecideP2(uint txn, uint appliedTxn, bool inRegion, LootCatalog cat, uint clientDigest, uint hash, int itemId,
                                            ItemFacts facts, LootState state, bool room, int recordCount, bool persistTaken, out LootClass cls, out string why)
        {
            why = null; cls = LootClass.Excluded;
            if (txn != 0 && txn <= appliedTxn) return ProposalCode.Unchanged;
            if (txn != appliedTxn + 1) { why = "out of order"; return ProposalCode.Stale; }
            if (!inRegion) { why = "not in that region"; return ProposalCode.NotAuthorized; }
            if (cat == null) { why = "host has no verified catalog of that region (not visited by the host in this process)"; return ProposalCode.NotAuthorized; }
            if (!LootRules.SupportedNamespace(cat.Namespace)) { why = "region not supported"; return ProposalCode.NotAuthorized; }
            if (cat.Digest != clientDigest) { why = "scene data differs between the instances (census digest)"; return ProposalCode.Invalid; }
            LootEntry e;
            if (!cat.TryGet(hash, out e)) { why = "unknown object"; return ProposalCode.Invalid; }
            if (e.ItemId != itemId) { why = "item type mismatch"; return ProposalCode.Invalid; }
            string x;
            cls = LootRules.Classify(e, facts, out x);
            if (cls == LootClass.Excluded) { why = "excluded: " + x; return ProposalCode.Invalid; }
            if (state == LootState.Taken) { why = "already taken"; return ProposalCode.Stale; }
            if (state == LootState.Pending) { why = "Player 1's pickup of it is in progress"; return ProposalCode.Busy; }
            if (persistTaken) { why = "already taken (host save keys)"; return ProposalCode.Stale; }
            if (!room) { why = "region loot list full"; return ProposalCode.Busy; }
            if (cls == LootClass.Money) return ProposalCode.Accepted;
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
