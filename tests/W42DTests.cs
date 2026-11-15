// W4.2D offline tests: cross-region loot synchronization.
//   Stage A  trusted host catalogs rebuilt from the REAL installed scene files (SceneCatalogBuilder) - identical to the in-game
//            census (digests / counts recorded from real game logs) - so the host validates regions it never loaded;
//            occupancy seeding, MainScene objects, Player 1 and Player 2 in different regions.
//   Stage B  random file cabinets: seeded original roll + one outcome per slot (CabinetBook); heavy loot stays refused.
//   Stage C  keys (unique), contract and parcel items (original side effects), LiveOnly objects; story / drone / special
//            stay excluded with their dependency.
// Usage: TS2MP.W42DTests.exe <game Managed folder>  (the real-file cases are skipped with a FAIL when the folder is missing).
// In-game behaviour (pickup presentation, coroutine, registry delivery between two PCs) is manual QA (docs/TESTING.md §37).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TS2MP.Shared;

static class W42DTests
{
    static int _passed, _failed;
    static string _managed, _data; static Func<string, Type> _types;
    static readonly Dictionary<string, SceneCatalogResult> _real = new Dictionary<string, SceneCatalogResult>();
    const int Madison = 99, Hillville = 97, Hideout = 0, MainNs = IdentityNamespace.MainScene;

    static int Main(string[] args)
    {
        _managed = args.Length > 0 ? args[0] : null;
        Run("A1 real scene files: build order from BuildSettings, every census scene parses with exact byte layouts", RealFilesParse);
        Run("A2 real scene files = in-game census: MadisonSt 50D2F273/2511, PlayerHideout C14C64A1/74, MainScene 4A364B6F/217 (real logs)", RealDigestsMatchLogs);
        Run("A3 real loot catalogs = in-game WORLD_LOOT_FLAGS (Madison 869 / MainScene 39 / Hideout 2; Hillville 598 = UnityPy count)", RealLootMatchesLogs);
        Run("A4 host never visited the region: P1 in Madison, P2 picks Hillville loot -> validated against the host's file catalog", HostNeverVisited);
        Run("A5 client metadata is never trusted: wrong digest / item / unknown id / region refused", NoClientTrust);
        Run("A6 occupancy seeding gate: Busy until seeded; region empties -> cleared; re-entry re-seeds by the original reload rule", OccupancySeeding);
        Run("A7 MainScene objects: pickable from any region, book reset at every region change (original MainScene reload)", MainSceneObjects);
        Run("A8 both directions in different regions at once + first valid pickup wins + duplicates/retries never grant twice", BothDirections);
        Run("A9 same-name families under occupancy: sibling key does not remove a live member; reload rule removes all", FamilyOccupancy);
        Run("A10 persistence: durable take keys, unsaved-exit replay, respawn day, host checkpoint rollback", Persistence);
        Run("B1 random cabinets: seeded original roll is deterministic per cabinet + day; one outcome per slot and occupancy", Cabinets);
        Run("B2 random cabinets from the real files: slot groups / outcomes extracted (Madison 6, Hillville 3, BankScene 81 slots)", RealCabinets);
        Run("B3 heavy loot stays refused for Player 2 (carry-only pickup; storage / sale transaction family missing)", HeavyRefused);
        Run("C1 keys = unique valuables; tools / no-XP / never_destroy = LiveOnly (no ItemDay_ key, vanilla respawn on reload)", KeysAndLiveOnly);
        Run("C2 contract + parcel: shared; parcel side effect applied once on the host (real parcel prefs extracted)", ContractParcel);
        Run("C3 story / drone / special / mission items stay excluded with their dependency", StoryDroneSpecial);
        Run("C4 Unity glue: catalogs on a background thread, seeding per occupancy, cabinet seed restores Random, MainScene in-region rule", SourceScan);
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

    // ------------------------------------------------------------------------------------------------ helpers
    static void LoadReal()
    {
        if (_real.Count > 0) return;
        Expect(_managed != null && File.Exists(Path.Combine(_managed, "Assembly-CSharp.dll")), "game Managed folder required (pass it as the first argument)");
        _data = Path.GetDirectoryName(_managed.TrimEnd('\\', '/'));
        string m = _managed;
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var p = Path.Combine(m, new AssemblyName(e.Name).Name + ".dll"); return File.Exists(p) ? Assembly.LoadFrom(p) : null; };
        var asm = Assembly.LoadFrom(Path.Combine(_managed, "Assembly-CSharp.dll"));
        _types = n => asm.GetType(n, false);
        foreach (var r in SceneCatalogBuilder.BuildAll(_data, _types, null)) _real[r.Scene] = r;
    }
    static SceneCatalogResult Real(string scene) { LoadReal(); SceneCatalogResult r; Expect(_real.TryGetValue(scene, out r), scene + " built"); Expect(r.Error == null, scene + ": " + r.Error); return r; }
    static Dictionary<LootFlags, int> FlagCounts(LootCatalog c)
    {
        var d = new Dictionary<LootFlags, int>();
        foreach (var e in c.Entries) for (int b = 0; b < 32; b++) { var x = (LootFlags)(1u << b); if ((e.Flags & x) != 0) { int n; d.TryGetValue(x, out n); d[x] = n + 1; } }
        return d;
    }
    static int F(Dictionary<LootFlags, int> d, LootFlags f) { int n; return d.TryGetValue(f, out n) ? n : 0; }
    static ItemFacts Item() { return new ItemFacts { Known = true, Value = 150, AddXp = 5 }; }
    static LootCatalog Cat(int ns, params object[] o)   // (hash, name, flags, money)
    {
        var c = new LootCatalog(ns);
        for (int i = 0; i < o.Length; i += 4) c.Add((uint)o[i], (string)o[i + 1], 9, (LootFlags)o[i + 2], null, 0, (int)o[i + 3]);
        c.Seal(null); c.Digest = 7; return c;
    }
    static ProposalCode Decide(LootCatalog c, uint txn, uint applied, bool inRegion, uint hash, LootBook book, ItemFacts f, out LootClass cls)
    {
        string why; return LootDecision.DecideP2(txn, applied, inRegion, c, 7, hash, 9, f, book.StateOf(c.Namespace, hash), book.HasRoom(c.Namespace, hash), 0, false, out cls, out why);
    }
    sealed class Keys : IKeyStore { public Dictionary<string, int> D = new Dictionary<string, int>(); public int Get(string k) { int v; return D.TryGetValue(k, out v) ? v : 0; } public void Set(string k, int v) { D[k] = v; } }
    static string Root()
    {
        var d = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && d != null; i++) { if (Directory.Exists(Path.Combine(d, "src", "Plugin"))) return d; d = Path.GetDirectoryName(d.TrimEnd('\\', '/')); }
        throw new Exception("repository root not found");
    }
    static string Src(string dir, string f) { return File.ReadAllText(Path.Combine(Root(), "src", dir, f)); }

    // ------------------------------------------------------------------------------------------------ A
    static void RealFilesParse()
    {
        LoadReal();
        var order = SceneCatalogBuilder.BuildScenes(_data);
        var expected = new[] { "MainMenuNew", "MainScene", "HackingPuzzle", "MadisonSt", "PlayerHideout", "Auto Shop", "IntroHouse", "Warehouse", "Hillville", "Jail", "BankScene", "EndStory", "Forestside Resort", "HauntedMansion", "Museum", "SwatRange", "DrugFarm" };
        Expect(order.SequenceEqual(expected), "build order = verified CODEBASE_MAP list: " + string.Join(",", order.ToArray()));
        Expect(_real.Count == 14, "14 census scenes (" + _real.Count + ")");
        foreach (var r in _real.Values) Expect(r.Error == null && r.Table.Rejected == 0 && r.Table.Ambiguous == 0, r.Scene + " exact parse: " + r.Error);
        Expect(!_real.ContainsKey("PawnShop"), "region 2 (PawnShop) has no scene in this build -> no catalog, its takes are refused");
    }

    static void RealDigestsMatchLogs()
    {
        // WORLD_CENSUS lines of the real 1.27c game (BepInEx LogOutput.log, 2026-11-14/15)
        var m = Real("MadisonSt"); Expect(m.Digest == 0x50D2F273 && m.Table.Count == 2511, "MadisonSt " + m.Digest.ToString("X8") + "/" + m.Table.Count);
        var h = Real("PlayerHideout"); Expect(h.Digest == 0xC14C64A1 && h.Table.Count == 74, "PlayerHideout " + h.Digest.ToString("X8") + "/" + h.Table.Count);
        var s = Real("MainScene"); Expect(s.Digest == 0x4A364B6F && s.Table.Count == 217, "MainScene " + s.Digest.ToString("X8") + "/" + s.Table.Count);
        Expect(m.Table.CountOf(IdentityCategory.Static) == 1591 && m.Table.CountOf(IdentityCategory.Persistent) == 879 && m.Table.CountOf(IdentityCategory.TimeDependent) == 33 && m.Table.CountOf(IdentityCategory.Unsupported) == 8, "Madison category split 1591/879/33/8");
        Expect(s.Table.CountOf(IdentityCategory.Dynamic) == 4 && s.Table.CountOf(IdentityCategory.TimeDependent) == 116, "MainScene dynamic 4 / time 116");
    }

    static void RealLootMatchesLogs()
    {
        var m = Real("MadisonSt"); var d = FlagCounts(m.Loot);
        Expect(m.Loot.Count == 869 && m.LootSharedNames == 12 && m.LootNames == 850, "Madison loot 869, shared names 12/850");
        Expect(F(d, LootFlags.DestroyIfPicked) == 341 && F(d, LootFlags.Money) == 176 && F(d, LootFlags.SharedName) == 31 && F(d, LootFlags.Heavy) == 48 && F(d, LootFlags.Contract) == 13
            && F(d, LootFlags.NoDays) == 3 && F(d, LootFlags.NeverDestroy) == 1 && F(d, LootFlags.RandomCabinet) == 12 && F(d, LootFlags.StoryPick) == 1 && F(d, LootFlags.StoryName) == 3
            && F(d, LootFlags.StoryCar) == 1 && F(d, LootFlags.Parcel) == 10, "Madison WORLD_LOOT_FLAGS identical");
        var s = Real("MainScene"); var ds = FlagCounts(s.Loot);
        Expect(s.Loot.Count == 39 && F(ds, LootFlags.StoryPick) == 1 && F(ds, LootFlags.StoryName) == 1 && F(ds, LootFlags.SharedName) == 2 && F(ds, LootFlags.DestroyIfPicked) == 1 && F(ds, LootFlags.NoXp) == 4 && F(ds, LootFlags.Heavy) == 2, "MainScene WORLD_LOOT_FLAGS identical");
        var h = Real("PlayerHideout"); Expect(h.Loot.Count == 2 && F(FlagCounts(h.Loot), LootFlags.DestroyIfPicked) == 1, "Hideout loot 2 (1 unique)");
        Expect(Real("Hillville").Loot.Count == 598, "Hillville loot 598 (UnityPy scene count, CODEBASE_MAP §15)");
        int moneyAmounts = m.Loot.Entries.Count(e => (e.Flags & LootFlags.Money) != 0 && e.Money > 0);
        Expect(moneyAmounts >= 165, "money amounts read from the scene file (host-side, never from the client): " + moneyAmounts);
    }

    static void HostNeverVisited()
    {
        var hill = Real("Hillville").Loot;
        var wh = new WorldHost(3); var reg = wh.Reg;
        wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, Hillville, 0);
        var target = hill.Entries.First(e => e.Flags == LootFlags.None && e.ItemId > 0);
        wh.Loot.MarkSeeded(Hillville);
        bool seenInRegion = false; string why;
        wh.LootTxnHook = (byte slot, LootTxnRequest t, bool inRegion, out string w) =>
        {
            seenInRegion = inRegion; LootClass cls;
            var code = LootDecision.DecideP2(t.Txn, 0, inRegion, hill, t.Digest, t.Hash, t.ItemId, Item(), wh.Loot.StateOf(t.Ns, t.Hash), wh.Loot.HasRoom(t.Ns, t.Hash), 0, false, out cls, out w);
            return code;
        };
        var req = new LootTxnRequest { Txn = 1, Ns = Hillville, Hash = target.Hash, ItemId = target.ItemId, Digest = hill.Digest };
        Expect(wh.LootTransaction(2, req, 1, out why) == ProposalCode.Accepted && seenInRegion, "accepted from the host's file catalog while the host is in Madison: " + why);
        Expect(wh.Loot.StateOf(Hillville, target.Hash) == LootState.Taken, "Hillville book lists it taken (P2's observer removes it; P1 cannot take it later)");
        var e2 = reg.Get(StateScope.Region, Hillville, LootBook.Keys[LootBook.ChunkOf(target.Hash)]);
        var set = new HashSet<uint>(); Expect(e2 != null && LootBook.TryParse(e2.Value, LootBook.ChunkOf(target.Hash), set) && set.Contains(target.Hash), "registry chunk carries it to both instances");
    }

    static void NoClientTrust()
    {
        var hill = Real("Hillville").Loot; var book = new LootBook(); LootClass cls; string why;
        var t = hill.Entries.First(e => e.Flags == LootFlags.None && e.ItemId > 0);
        Expect(LootDecision.DecideP2(1, 0, true, hill, hill.Digest ^ 1, t.Hash, t.ItemId, Item(), LootState.Available, true, 0, false, out cls, out why) == ProposalCode.Invalid, "client census digest differs from the host's file digest -> refused");
        Expect(LootDecision.DecideP2(1, 0, true, hill, hill.Digest, t.Hash, t.ItemId + 1, Item(), LootState.Available, true, 0, false, out cls, out why) == ProposalCode.Invalid, "claimed item type differs -> refused");
        Expect(LootDecision.DecideP2(1, 0, true, hill, hill.Digest, 0xDEADBEEF, t.ItemId, Item(), LootState.Available, true, 0, false, out cls, out why) == ProposalCode.Invalid, "unknown id -> refused");
        Expect(LootDecision.DecideP2(1, 0, false, hill, hill.Digest, t.Hash, t.ItemId, Item(), LootState.Available, true, 0, false, out cls, out why) == ProposalCode.NotAuthorized, "requester not in Hillville -> refused");
        var money = hill.Entries.First(e => (e.Flags & LootFlags.Money) != 0 && e.Money > 0 && (e.Flags & LootRules.Blocking) == 0);
        Expect(LootRules.Classify(money, new ItemFacts(), out why) == LootClass.Money && money.Money > 0, "money amount comes from the host's file catalog (the wire carries no amount)");
    }

    static void OccupancySeeding()
    {
        var wh = new WorldHost(5);
        wh.SetPlayerRegion(2, Hillville, 0);
        Expect(!wh.Loot.IsSeeded(Hillville), "a fresh occupancy is not seeded -> the plugin answers Busy (nothing granted)");
        wh.Loot.MarkSeeded(Hillville); wh.Loot.Commit(Hillville, 11, wh.Reg, wh.Outbox);
        wh.SetPlayerRegion(2, Madison, 1);   // Hillville empties
        Expect(!wh.Loot.IsSeeded(Hillville) && wh.Loot.StateOf(Hillville, 11) == LootState.Available, "region emptied: book + seeded mark cleared (original reload re-creates the loot)");
        // re-entry: the original reload rule on the host's keys decides what stays gone
        Expect(LootRules.PersistentlyTaken(LootClass.Valuable, 20, 1, 15) && !LootRules.PersistentlyTaken(LootClass.Valuable, 15, 1, 15) && !LootRules.PersistentlyTaken(LootClass.LiveOnly, 99, 1, 15), "seed rule: future ItemDay_ stays gone, reached day respawns, LiveOnly always respawns");
    }

    static void MainSceneObjects()
    {
        var wh = new WorldHost(9); bool inRegion = false; string why;
        wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, Hillville, 0);
        wh.LootTxnHook = (byte s, LootTxnRequest t, bool ir, out string w) => { inRegion = ir; w = null; return ProposalCode.Accepted; };
        Expect(wh.LootTransaction(2, new LootTxnRequest { Txn = 1, Ns = MainNs, Hash = 5 }, 1, out why) == ProposalCode.Accepted && inRegion, "MainScene object: in region from Hillville");
        Expect(wh.LootTransaction(2, new LootTxnRequest { Txn = 2, Ns = Madison, Hash = 6 }, 1, out why) == ProposalCode.Accepted && !inRegion, "Madison object while Player 2 is in Hillville: not in region");
        wh.Loot.MarkSeeded(MainNs);
        Expect(wh.Loot.StateOf(MainNs, 5) == LootState.Taken, "MainScene take listed");
        wh.SetPlayerRegion(1, Hideout, 2);   // Player 1 travels = its MainScene reloads
        Expect(!wh.Loot.IsSeeded(MainNs) && wh.Loot.StateOf(MainNs, 5) == LootState.Available, "region change -> MainScene book re-seeded from keys (taken objects with a respawn key come back as taken)");
        wh.SetPlayerRegion(2, Regions.None, 3);
        Expect(wh.LootTransaction(2, new LootTxnRequest { Txn = 3, Ns = MainNs, Hash = 7 }, 3, out why) == ProposalCode.Accepted && !inRegion, "not in the world -> not in region");
    }

    static void BothDirections()
    {
        var mad = Cat(Madison, 1u, "Lamp", LootFlags.None, 0); var hill = Cat(Hillville, 2u, "Vase", LootFlags.None, 0);
        var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>(); string why; LootClass cls;
        Expect(book.Claim(Madison, 1, 100, out why) && book.Commit(Madison, 1, reg, o) == ProposalCode.Accepted, "Player 1 takes Madison loot (original)");
        Expect(Decide(hill, 1, 0, true, 2, book, Item(), out cls) == ProposalCode.Accepted && book.Commit(Hillville, 2, reg, o) == ProposalCode.Accepted, "at the same time Player 2 takes Hillville loot");
        Expect(Decide(mad, 2, 1, true, 1, book, Item(), out cls) == ProposalCode.Stale, "Player 2 later in Madison: P1's object is gone");
        Expect(!book.Claim(Hillville, 2, 101, out why), "Player 1 later in Hillville: P2's object is gone");
        Expect(Decide(hill, 1, 1, true, 2, book, Item(), out cls) == ProposalCode.Unchanged, "duplicate packet -> Unchanged");
        Expect(Decide(hill, 5, 1, true, 2, book, Item(), out cls) == ProposalCode.Stale, "out-of-order -> Stale");
    }

    static void FamilyOccupancy()
    {
        Expect(!LootRules.TakenByKeys(LootClass.Valuable, true, 30, 1, 10), "family member: a sibling's key written during this occupancy does not remove it");
        Expect(LootRules.TakenByKeys(LootClass.Valuable, false, 30, 1, 10), "non-family: its own key removes it");
        Expect(LootRules.PersistentlyTaken(LootClass.Valuable, 30, 1, 10), "seeding at the next occupancy: the family key removes every member (original <Start>)");
        var real = Real("MadisonSt").Loot;
        var fam = real.Entries.Where(e => (e.Flags & LootFlags.SharedName) != 0).GroupBy(e => e.Name).First(g => g.Count() > 1).ToList();
        Expect(fam.Select(e => e.Hash).Distinct().Count() == fam.Count, "real family '" + fam[0].Name + "' x" + fam.Count + ": distinct ids, never merged");
    }

    static void Persistence()
    {
        var k = new Keys();
        LootKeys.Apply(k, new LootTake { Name = "Lamp", Day = LootRules.TakeDay(LootClass.Valuable, 10, 150), ItemFlag = true });
        Expect(k.Get("ItemDay_Lamp") == 13 && k.Get("Item_Lamp") == 1, "valuable 150 -> 3 days (SetDayRespawn tier)");
        LootKeys.Apply(k, new LootTake { Name = "15$_a", Day = LootRules.TakeDay(LootClass.Money, 10, 0), ItemFlag = true });
        Expect(k.Get("ItemDay_15$_a") == 15, "money -> 5 days");
        LootKeys.Apply(k, new LootTake { Name = "Wrench", Day = LootRules.TakeDay(LootClass.LiveOnly, 10, 50), ItemFlag = true });
        Expect(k.Get("ItemDay_Wrench") == 0 && k.Get("Item_Wrench") == 1, "LiveOnly writes no respawn day (vanilla re-creates it at the next load)");
        // durable take round trip (unsaved host exit replay uses the same keys)
        var w = new LiteNetLib.Utils.NetDataWriter(); var take = new LootTake { Ns = MainNs, Hash = 9, ItemId = 4, Day = 0, ItemFlag = true, Name = "Wrench" }; take.Write(w);
        LootTake back; Expect(LootTake.TryRead(new LiteNetLib.Utils.NetDataReader(w.CopyData()), out back) && back.Ns == MainNs && back.Day == 0, "MainScene / LiveOnly take survives the durable record");
        // host checkpoint rollback: keys and the book go back with the host world; a P2 durable take is replayed (no duplicate)
        Expect(!LootRules.PersistentlyTaken(LootClass.Valuable, 13, 1, 13), "respawn day reached -> available again");
    }

    // ------------------------------------------------------------------------------------------------ B
    static void Cabinets()
    {
        Expect(CabinetBook.Seed(0x1234, 10) == CabinetBook.Seed(0x1234, 10), "same cabinet + day -> same seed on both instances");
        Expect(CabinetBook.Seed(0x1234, 10) != CabinetBook.Seed(0x1234, 11) && CabinetBook.Seed(0x1234, 10) != CabinetBook.Seed(0x1235, 10), "a new day / another cabinet rolls anew");
        var b = new CabinetBook();
        Expect(b.Allowed(Madison, 7, 0) && b.Allowed(Madison, 7, 2), "untouched slot: any outcome");
        b.Take(Madison, 7, 2);
        Expect(b.Allowed(Madison, 7, 2) && !b.Allowed(Madison, 7, 0) && !b.Allowed(Madison, 7, 1), "slot fixed to outcome 2: other outcomes refused, same outcome stays pickable");
        Expect(b.Allowed(Madison, 8, 0) && b.Allowed(Hillville, 7, 0) && b.Allowed(Madison, 0, 5), "other slot / region / no group unaffected");
        var book = new LootBook(); var reg = StateRegistry.CreateHost(1); var o = new List<StateEntry>();
        book.Cabinets.Take(Madison, 7, 2); book.ClearRegion(Madison, reg, o);
        Expect(book.Cabinets.Allowed(Madison, 7, 0), "region emptied: the next load rolls again (original)");
    }

    static void RealCabinets()
    {
        Func<string, int> slots = s => Real(s).Loot.Entries.Where(e => e.Group != 0).Select(e => e.Group).Distinct().Count();
        Expect(slots("MadisonSt") == 6 && slots("Hillville") == 3 && slots("BankScene") == 81, "slot groups " + slots("MadisonSt") + "/" + slots("Hillville") + "/" + slots("BankScene"));
        var bank = Real("BankScene").Loot.Entries.Where(e => e.Group != 0).ToList();
        // real structure (1.27c): the default contents ("<slot>CabinetDisable") hold no loot; each slot has two alternative
        // elements with one loot object each -> exactly one of the two may be taken per occupancy
        Expect(bank.All(e => (e.Flags & LootFlags.RandomCabinet) != 0 && e.Outcome > 0), "grouped objects are cabinet loot of random list elements");
        Expect(bank.GroupBy(e => e.Group).All(g => g.Count() == 2 && g.Select(e => e.Outcome).Distinct().Count() == 2), "each slot: two mutually exclusive alternatives");
    }

    static void HeavyRefused()
    {
        string why;
        Expect(LootRules.Classify(new LootEntry { Name = "TV", Flags = LootFlags.Heavy }, Item(), out why) == LootClass.Excluded && LootRules.ClientPolicy(true, true, LootClass.Excluded, false) == P2PickPolicy.Refuse, "heavy -> refused");
        var s = Src("Plugin", "WorldLootSync.cs");
        Expect(s.Contains("static bool HeavyPrefix(") && s.Contains("transaction family missing"), "client PickUpHeavy guard stays");
    }

    // ------------------------------------------------------------------------------------------------ C
    static void KeysAndLiveOnly()
    {
        string why; var key = Item(); key.Key = true; key.Value = 0; key.AddXp = 0;
        Expect(LootRules.Classify(new LootEntry { Name = "Key_102_Porch", Flags = LootFlags.DestroyIfPicked }, key, out why) == LootClass.UniqueValuable, "unique key -> unique valuable (Item_<name> removal, no value needed)");
        var tool = Item(); tool.Tool = true;
        Expect(LootRules.Classify(new LootEntry { Name = "Crowbar" }, tool, out why) == LootClass.LiveOnly, "tool -> LiveOnly");
        foreach (var f in new[] { LootFlags.NoXp, LootFlags.NoDays, LootFlags.NeverDestroy })
            Expect(LootRules.Classify(new LootEntry { Name = "x", Flags = f }, Item(), out why) == LootClass.LiveOnly, f + " -> LiveOnly");
        Expect(!LootRules.PersistentlyTaken(LootClass.LiveOnly, 99, 1, 1) && LootRules.TakeDay(LootClass.LiveOnly, 10, 300) == 0, "LiveOnly: never key-taken, no day written");
    }

    static void ContractParcel()
    {
        string why;
        Expect(LootRules.Classify(new LootEntry { Name = "c", Flags = LootFlags.Contract }, Item(), out why) == LootClass.Valuable, "contract item shared (pickup side effect = message only)");
        Expect(LootRules.Classify(new LootEntry { Name = "p", Flags = LootFlags.Parcel }, Item(), out why) == LootClass.Valuable, "parcel shared");
        var parcels = Real("MadisonSt").Loot.Entries.Where(e => (e.Flags & LootFlags.Parcel) != 0).ToList();
        Expect(parcels.Count == 10 && parcels.All(e => !string.IsNullOrEmpty(e.Pref)), "all 10 Madison parcels carry their parcel_pref from the scene file");
        var s = Src("Plugin", "WorldLootSync.cs");
        Expect(s.Contains("mission.Add(e.Pref); parcels.Remove(e.Pref);") && s.Contains("string side = HostSideEffects(e);"), "host applies the original list mutation for the accepted take only");
        int dec = s.IndexOf("ProposalCode HostDecide(", StringComparison.Ordinal), side = s.IndexOf("string side = HostSideEffects(e);", StringComparison.Ordinal), take = s.IndexOf("p2.HostLootTake(t.Txn, take, out why)", StringComparison.Ordinal);
        Expect(dec > 0 && take > dec && side > take, "side effect after the durable take succeeded");
    }

    static void StoryDroneSpecial()
    {
        string why;
        foreach (var f in new[] { LootFlags.StoryPick, LootFlags.StoryCar, LootFlags.StoryName }) Expect(LootRules.Classify(new LootEntry { Name = "s", Flags = f }, Item(), out why) == LootClass.Excluded, f + " excluded (StoryManager.AdvanceStory)");
        var drone = Item(); drone.Drone = true; Expect(LootRules.Classify(new LootEntry { Name = "d" }, drone, out why) == LootClass.Excluded && why.Contains("DronePickedUp"), "drone excluded: " + why);
        var sp = Item(); sp.Special = true; Expect(LootRules.Classify(new LootEntry { Name = "x" }, sp, out why) == LootClass.Excluded, "special excluded");
        var mi = Item(); mi.Mission = true; Expect(LootRules.Classify(new LootEntry { Name = "x" }, mi, out why) == LootClass.Excluded, "mission item excluded");
    }

    static void SourceScan()
    {
        var c = Src("Plugin", "HostLootCatalogs.cs"); var s = Src("Plugin", "WorldLootSync.cs"); var w = Src("Shared", "WorldTime.cs"); var sc = Src("Shared", "SceneCatalog.cs");
        Expect(c.Contains("IsBackground = true") && c.Contains("ThreadPriority.BelowNormal") && c.Contains("if (_thread != null) return;   // once per process"), "catalogs: one background build per process");
        Expect(!sc.Contains("UnityEngine.") || sc.IndexOf("using UnityEngine", StringComparison.Ordinal) < 0, "scene reader is Unity-free (file access only)");
        Expect(s.Contains("void SeedOccupied(double now)") && s.Contains("book.MarkSeeded(ns)") && s.Contains("not initialized for this occupancy yet"), "seeding per occupancy + Busy gate");
        Expect(s.Contains("_savedRandom = i._rndState.GetValue(null, null);") && s.Contains("i._rndState.SetValue(null, _savedRandom, null);"), "cabinet seed restores the global Random state");
        Expect(w.Contains("t.Ns == IdentityNamespace.MainScene ? at != Regions.None : at == t.Ns") && w.Contains("if (prev != region) Loot.ClearRegion(IdentityNamespace.MainScene, Reg, Outbox);"), "WorldHost MainScene rules");
        Expect(!s.Contains("GUI.") && !c.Contains("GUI."), "no mod text in the game UI");
    }
}
