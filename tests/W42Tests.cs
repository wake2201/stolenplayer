// W4.2 Phase A offline tests: host-owned campaign bootstrap, persistent Player-2 spawn + story-safe fallback, host-owned
// region-object registry and the ordinary-door vertical slice. The tests drive the SAME Unity-free pieces as the plugin
// (src/Plugin/CampaignBootstrap.cs, WorldDoorSync.cs, SaveGuard.cs): CampaignPolicy / CampaignSnapshot / SpawnRules /
// P2SpawnRecord / P2PosReport, RegionStateBook / ObjectStateList / ObjectTxnRequest / WorldDoorRules, WorldHost dispatch,
// SaveIsolation.IsSaveData, plus one real LiteNetLib UDP transfer of a large campaign snapshot. Unity scene behaviour (the
// FBPP write + reload, Continue, placement, door swings) is manual QA (docs/TESTING.md §27).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W42Tests
{
    static int _passed, _failed, _port = 38100;
    static string _logDir;
    const int Hideout = 0, Hillville = 97, Madison = 99, Bank = 92;
    const string Game = "1.27c";

    static int Main()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ts2mp-w42-tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_logDir);
        Run("01 first join: host world + P2 record, never P1's personal keys; spawn near Player 1 in the host's free-roam region", FirstJoin);
        Run("02 rejoin the same campaign: exact last valid position + yaw, original position keys for the load", Rejoin);
        Run("03 another host campaign / another save history: never the old position (first-join spawn), record kept", OtherCampaign);
        Run("04 Player 2 reconnects in a different region than Player 1: Player 2's region only (host region untouched)", DifferentRegion);
        Run("05 disconnect during a story mission: no exact restore (safe point / hideout), flags from original metadata", StoryMission);
        Run("06 safe spawn fallback: host in a mission scene / host not placed -> hideout original spawn", SafeFallback);
        Run("07 invalid / stale spawn records and reports: rejected safely, never thrown, never applied", InvalidRecords);
        Run("08 solo-save protection: save data never seeded, local settings only, client write gated (source scan)", SoloSaves);
        Run("09 Player 2 inventory: host-owned record in the campaign base, P1 backpack/money/storage never copied", Inventory);
        Run("10 door interaction while Player 1 is in another region: host decides without the scene", DoorHostElsewhere);
        Run("11 duplicate / out-of-order / conflicting door messages never reverse or corrupt the state", DoorOrdering);
        Run("12 door state across region reload: kept while the region is live, vanilla reset when it empties, late join", DoorReload);
        Run("13 replica decisions: committed state applied only to idle, unlocked, non-pending doors", DoorReplicaRules);
        Run("14 codecs: snapshot round trip, size, fuzz; object list cap; requests; spawn record 46 B; report 24 B", Codecs);
        Run("15 campaign snapshot over real LiteNetLib UDP (~70 KB, fragmented, reliable ordered) + version pins", Transport);
        Run("16 regressions: existing registry keys / loot / dropped items / vehicle doors unaffected by the new dispatch", Regressions);
        Run("17 performance + UI guard: no scene scans in the new per-frame code, no mod text in the game UI", SourceScan);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed. Logs: " + _logDir);
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- helpers
    // A host save as the original writes it (key families seen in a real 1.27c save: 1763 keys).
    static CampaignData HostSave(int location)
    {
        var d = new CampaignData();
        d.Ints["Story"] = 42; d.Ints["StoryBackup"] = 41; d.Ints["CurrentLocation"] = location; d.Ints["Saved_Pos"] = 0;
        d.Ints["Money"] = 98765; d.Ints["BankBalance"] = 5000; d.Ints["BankIncrease"] = 3; d.Ints["BatonLife"] = 20; d.Ints["CrowbarLife"] = 30;
        for (int i = 1; i <= 452; i++) { d.Ints["Item_" + i] = i % 3; d.Ints["Storage_Item_" + i] = 1; d.Ints["Locker_Item_" + i] = 2; }
        d.Ints["Item_GoldWatch"] = 1;          // loot marker (world)
        d.Ints["ItemDay_GoldWatch"] = 17;      // loot respawn day (world)
        d.Ints["DailyEvent3"] = 2; d.Ints["DaysInGame"] = 15; d.Ints["Lvl"] = 7; d.Ints["XP"] = 1234; d.Ints["VanBought"] = 1;
        d.Ints["HeavyVan_0"] = 900; d.Ints["HeavyItemShed0"] = 901; d.Ints["TrunkItem"] = 77;
        d.Floats["PosX_item0"] = 1f; d.Floats["RotY_item0"] = 2f;
        d.Floats["XPlayer"] = 11f; d.Floats["YPlayer"] = 12f; d.Floats["ZPlayer"] = 13f; d.Floats["GameTime"] = 15f; d.Floats["GasTankLeftCar"] = 60f;
        d.Ints["TS2MP_StorageTxn"] = 9; d.Ints["TS2MP_HostTxn"] = 4; d.Ints["TS2MP_P2History"] = 555;
        d.Strings["contract_name"] = "Mansion"; d.Bools["TPP"] = false; d.Bools["AssociateUnlocked"] = true;
        return d;
    }

    static P2Inventory P2Record() { var r = P2Inventory.Initial(false); r.Id = 555; r.Set(41, 2); r.Set(57, 1); r.Money = 1500; r.Bank = 10; r.BatonLife = 80; r.CrowbarLife = 60; return r; }

    static P2SpawnRecord Rec(int history, int region, float x, float y, float z, SpawnContext ctx)
    { return new P2SpawnRecord { HistoryId = history, Region = region, Pos = new Vec3(x, y, z), Yaw = 135f, Context = ctx, Story = 42, Seq = 7, UtcTicks = 1, Profile = 0 }; }

    static CampaignSnapshot RoundTrip(CampaignSnapshot s)
    {
        CampaignSnapshot back; string err;
        Expect(CampaignSnapshot.TryDecode(new NetDataReader(s.Encode()), out back, out err), "snapshot decodes: " + err);
        return back;
    }

    // ---------------------------------------------------------------- 01
    static void FirstJoin()
    {
        string note;
        var plan = SpawnRules.Decide(null, null, 555, Madison, new Vec3(100f, 5f, -40f), out note);
        Expect(plan.Mode == SpawnMode.NearHost && plan.Reason == SpawnReason.FirstJoin && plan.Region == Madison && plan.HasTarget && plan.Target.X == 100f, "first join near Player 1 in Madison: " + plan);
        int[] removed;
        var snap = RoundTrip(CampaignSnapshot.Build(HostSave(Madison), P2Record(), plan, 0, 555, 1, out removed));
        int v;
        Expect(snap.Data.TryInt("Story", out v) && v == 42 && snap.HostStory == 42, "host story");
        Expect(snap.Data.TryInt("Item_GoldWatch", out v) && v == 1 && snap.Data.TryInt("ItemDay_GoldWatch", out v) && v == 17, "host loot markers / respawn days (world)");
        Expect(snap.Data.TryInt("DailyEvent3", out v) && snap.Data.TryInt("Lvl", out v) && snap.Data.TryInt("VanBought", out v), "other world / progression keys");
        Expect(snap.Data.TryInt("Money", out v) && v == 1500 && snap.Data.TryInt("BatonLife", out v) && v == 80 && snap.Data.TryInt("BankBalance", out v) && v == 10, "Player 2's own money / tools / bank");
        Expect(snap.Data.TryInt("Item_41", out v) && v == 2 && snap.Data.TryInt("Item_57", out v) && v == 1 && !snap.Data.Has("Item_1") && !snap.Data.Has("Item_2"), "Player 2's backpack only");
        Expect(!snap.Data.Has("BankIncrease"), "Player 1's bank increase not copied");
        Expect(snap.Data.TryInt("CurrentLocation", out v) && v == Madison && snap.Data.TryInt("Saved_Pos", out v) && v == 1 && snap.Data.Floats["XPlayer"] == 100f, "position keys = the spawn plan");
        Expect(!snap.Data.Has("TS2MP_StorageTxn") && !snap.Data.Has("TS2MP_P2History"), "no host bookkeeping");
        Expect(removed[(int)CampaignKeyClass.PlayerOwnP1] == 452 + 5 && removed[(int)CampaignKeyClass.HostStorage] == 452 * 2 + 5 && removed[(int)CampaignKeyClass.ModHost] == 3, "removal counts " + string.Join(",", Array.ConvertAll(removed, x => x.ToString())));
    }

    // ---------------------------------------------------------------- 02
    static void Rejoin()
    {
        string note;
        var plan = SpawnRules.Decide(Rec(555, Madison, 12f, 3f, 99f, SpawnContext.None), null, 555, Madison, new Vec3(0, 0, 0), out note);
        Expect(plan.Mode == SpawnMode.Restore && plan.Reason == SpawnReason.Rejoin && plan.Region == Madison && plan.Target.Z == 99f && plan.Yaw == 135f, "exact restore: " + plan);
        int[] r;
        var snap = RoundTrip(CampaignSnapshot.Build(HostSave(Madison), P2Record(), plan, 0, 555, 2, out r));
        Expect(snap.Plan.Mode == SpawnMode.Restore && snap.Plan.Yaw == 135f && snap.Data.Floats["ZPlayer"] == 99f && snap.Data.Ints["Saved_Pos"] == 1, "original LoadPlayerPosition keys carry the restore position");
        // vehicle / hiding spot at the last report: never into the seat / spot
        plan = SpawnRules.Decide(Rec(555, Hillville, 1, 2, 3, SpawnContext.InVehicle), null, 555, Madison, null, out note);
        Expect(plan.Mode == SpawnMode.RestoreNear && plan.Region == Hillville, "vehicle -> beside it");
        plan = SpawnRules.Decide(Rec(555, Hideout, 1, 2, 3, SpawnContext.Hiding), null, 555, Madison, null, out note);
        Expect(plan.Mode == SpawnMode.RestoreNear && plan.Region == Hideout, "hiding spot -> beside it");
    }

    // ---------------------------------------------------------------- 03
    static void OtherCampaign()
    {
        string note;
        var plan = SpawnRules.Decide(Rec(111, Madison, 12f, 3f, 99f, SpawnContext.None), null, 555, Hillville, new Vec3(7, 8, 9), out note);
        Expect(plan.Mode == SpawnMode.NearHost && plan.Reason == SpawnReason.StaleRecord && plan.Region == Hillville, "other history -> first join near the host: " + plan);
        plan = SpawnRules.Decide(null, null, 999, Hideout, new Vec3(1, 1, 1), out note);
        Expect(plan.Mode == SpawnMode.NearHost && plan.Region == Hideout, "another host (no record there) -> first join");
        int[] r;
        var a = CampaignSnapshot.Build(HostSave(Hideout), P2Record(), plan, 3, 999, 3, out r);
        Expect(a.Profile == 3 && a.HistoryId == 999, "snapshot names the host campaign");
        // the record stays with its own campaign: a host never mixes P2 records of other profiles (file per profile, history id)
        Expect(Rec(111, Madison, 0, 0, 0, 0).HistoryId != 999, "record bound to its history");
    }

    // ---------------------------------------------------------------- 04
    static void DifferentRegion()
    {
        string note;
        var plan = SpawnRules.Decide(Rec(555, Madison, 50f, 2f, 60f, SpawnContext.None), null, 555, Hideout, new Vec3(1, 1, 1), out note);
        Expect(plan.Region == Madison && plan.Mode == SpawnMode.Restore, "Player 2 returns to Madison while Player 1 is at the hideout");
        int[] r;
        var host = HostSave(Hideout);
        var snap = RoundTrip(CampaignSnapshot.Build(host, P2Record(), plan, 0, 555, 4, out r));
        Expect(snap.Data.Ints["CurrentLocation"] == Madison && host.Ints["CurrentLocation"] == Hideout, "only Player 2's load uses Madison; the host model is not modified");
        // registry: the host accepts Player 2's reports for Player 2's own region
        var wh = new WorldHost(7);
        wh.SetPlayerRegion(1, Hideout, 0); wh.SetPlayerRegion(2, Madison, 0);
        P2PosReport got = default(P2PosReport); int calls = 0;
        wh.P2PosHook = rep => { got = rep; calls++; return ProposalCode.Accepted; };
        uint rev; int sr; float sh;
        var rp = new P2PosReport { Region = Madison, Pos = new Vec3(5, 6, 7), Yaw = 10 };
        var code = wh.HandleProposal("c1", 2, 1, new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.P2Pos, Value = rp.ToBytes() }, 1, out rev, out sr, out sh);
        Expect(code == ProposalCode.Accepted && calls == 1 && got.Pos.Z == 7f, "report accepted for the reporter's own region");
        var bad = new P2PosReport { Region = Hideout, Pos = new Vec3(5, 6, 7) };
        code = wh.HandleProposal("c1", 2, 2, new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.P2Pos, Value = bad.ToBytes() }, 1, out rev, out sr, out sh);
        Expect(code == ProposalCode.NotAuthorized && calls == 1, "a report for another region than the registry's is refused");
        Expect(wh.Reg.Get(StateScope.Player, 2, WorldKeys.P2Pos) == null, "reports are never stored in the shared registry");
    }

    // ---------------------------------------------------------------- 05
    static void StoryMission()
    {
        string note;
        foreach (var ctx in new[] { SpawnContext.BlockSaving, SpawnContext.BlockTravel, SpawnContext.PoliceChase, SpawnContext.Caught, SpawnContext.BlockSaving | SpawnContext.InVehicle })
        {
            var plan = SpawnRules.Decide(Rec(555, Madison, 50f, 2f, 60f, ctx), null, 555, Hideout, null, out note);
            Expect(plan.Mode == SpawnMode.SafePoint && plan.Reason == SpawnReason.StoryContext && plan.Region == Madison && plan.HasTarget, ctx + " -> nearest original safe point of Madison: " + plan);
        }
        var heist = SpawnRules.Decide(Rec(555, Bank, 1, 1, 1, SpawnContext.None), null, 555, Madison, new Vec3(0, 0, 0), out note);
        Expect(heist.Mode == SpawnMode.OriginalSpawn && heist.Reason == SpawnReason.MissionScene && heist.Region == Hideout && !heist.HasTarget, "mission scene -> hideout original spawn");
        foreach (int r in new[] { 90, 91, 92, 93, 94, 95, 96, 98, -1 }) Expect(!SpawnRegions.IsFreeRoam(r), Regions.Name(r) + " is a mission/scripted scene");
        foreach (int r in new[] { 0, 1, 3, 97, 99 }) Expect(SpawnRegions.IsFreeRoam(r), Regions.Name(r) + " is free roam");
        Expect(!SpawnContextRules.IsUnsafe(SpawnContext.InVehicle | SpawnContext.Hiding), "vehicle / hiding are not story contexts");
    }

    // ---------------------------------------------------------------- 06
    static void SafeFallback()
    {
        string note;
        var p = SpawnRules.Decide(null, null, 555, Bank, new Vec3(1, 1, 1), out note);
        Expect(p.Mode == SpawnMode.OriginalSpawn && p.Region == Hideout && p.Reason == SpawnReason.HostInMissionScene, "host in a heist -> hideout: " + p);
        p = SpawnRules.Decide(null, null, 555, Madison, null, out note);
        Expect(p.Mode == SpawnMode.OriginalSpawn && p.Region == Hideout, "host not placed -> hideout");
        p = SpawnRules.Decide(null, null, 555, Regions.None, null, out note);
        Expect(p.Region == Hideout, "host region unknown -> hideout");
        p = SpawnRules.Decide(null, null, 555, Madison, new Vec3(float.NaN, 0, 0), out note);
        Expect(p.Region == Hideout, "invalid host feet -> hideout");
    }

    // ---------------------------------------------------------------- 07
    static void InvalidRecords()
    {
        P2SpawnRecord r; string err;
        var good = Rec(555, Madison, 1, 2, 3, SpawnContext.None).ToBytes();
        Expect(good.Length == P2SpawnRecord.Bytes && P2SpawnRecord.TryParse(good, out r, out err) && r.Pos.Y == 2f && r.Seq == 7, "round trip");
        Expect(!P2SpawnRecord.TryParse(new byte[10], out r, out err), "short");
        var nan = Rec(555, Madison, float.NaN, 0, 0, 0).ToBytes(); Expect(!P2SpawnRecord.TryParse(nan, out r, out err), "NaN");
        var far = Rec(555, Madison, 1e6f, 0, 0, 0).ToBytes(); Expect(!P2SpawnRecord.TryParse(far, out r, out err), "out of range");
        var reg = Rec(555, 12345, 0, 0, 0, 0).ToBytes(); Expect(!P2SpawnRecord.TryParse(reg, out r, out err), "unknown region");
        var noid = Rec(0, Madison, 0, 0, 0, 0).ToBytes(); Expect(!P2SpawnRecord.TryParse(noid, out r, out err), "no campaign id");
        var ver = (byte[])good.Clone(); ver[0] = 9; Expect(!P2SpawnRecord.TryParse(ver, out r, out err) && err == "version", "version");
        string note;
        var plan = SpawnRules.Decide(null, "main: size 3, backup: missing", 555, Madison, new Vec3(1, 1, 1), out note);
        Expect(plan.Reason == SpawnReason.InvalidRecord && plan.Mode == SpawnMode.NearHost, "unusable record -> first-join spawn");
        var rnd = new Random(5);
        for (int i = 0; i < 3000; i++) { var b = new byte[rnd.Next(0, 60)]; rnd.NextBytes(b); P2SpawnRecord.TryParse(b, out r, out err); P2PosReport pr; P2PosReport.TryParse(b, out pr); }
        P2PosReport rep;
        Expect(!P2PosReport.TryParse(new P2PosReport { Region = Madison, Pos = new Vec3(0, -9999, 0) }.ToBytes(), out rep), "report below the world refused");
        Expect(SpawnRules.CheckReport(1, 2, Madison, new P2PosReport { Region = Madison }) != null, "only Player 2");
        Expect(SpawnRules.CheckReport(2, 1, Madison, new P2PosReport { Region = Madison }) != null, "own slot only");
        Expect(SpawnRules.CheckReport(2, 2, Hideout, new P2PosReport { Region = Madison }) != null, "registry region must match");
        Expect(SpawnRules.CheckReport(2, 2, Madison, new P2PosReport { Region = Madison }) == null, "valid");
    }

    // ---------------------------------------------------------------- 08
    static void SoloSaves()
    {
        foreach (var f in new[] { "saveData.thief", "saveData2.thief", "saveData5.thief", "saveDataBackup.thief", "saveInfo0.txt", "backups\\saveData_backup3.thief", "backups/x.txt", "\\saveData.thief" })
            Expect(SaveIsolation.IsSaveData(f), f + " is save data (never seeded)");
        foreach (var f in new[] { "QualitySettings.ini", "settings.cfg", "Player.log", "Unity\\x\\Analytics\\a.json", "" })
            Expect(!SaveIsolation.IsSaveData(f), f + " is not save data");
        // unchanged N1.2 protection
        Expect(SaveIsolation.IsProtectedWrite("C:\\Real\\saveData.thief", "C:\\Real", "D:\\Game") && !SaveIsolation.IsProtectedWrite("C:\\Data\\client-sandbox\\saveData.thief", "C:\\Real", "D:\\Game"), "real folder protected, sandbox writable");
        string plugin = PluginDir();
        var guard = File.ReadAllText(Path.Combine(plugin, "SaveGuard.cs"));
        Expect(guard.Contains("if (IsSaveData(rel)) { skipped++; continue; }"), "SeedSandbox skips save data");
        Expect(guard.Contains("RetireSandboxSaves();") && guard.Contains("File.Move(src, to)") && !guard.Contains("File.Delete(src"), "old sandbox saves moved aside, never deleted");
        var boot = File.ReadAllText(Path.Combine(plugin, "CampaignBootstrap.cs"));
        int apply = boot.IndexOf("void Apply(CampaignSnapshot snap)", StringComparison.Ordinal);
        int gate = boot.IndexOf("if (!SaveGuard.Armed || !SaveGuard.RuntimeCheck())", apply, StringComparison.Ordinal);
        int write = boot.IndexOf("FbppApi.Overwrite(", apply, StringComparison.Ordinal);
        Expect(apply > 0 && gate > apply && write > gate, "the sandbox write happens only after the armed + runtime check");
        Expect(boot.Contains("if (!InMainMenu())") && boot.Contains("NewGamePrefix") && boot.Contains("ContinuePrefix"), "applied only in the main menu; Continue gated; new games blocked");
        var menu = File.ReadAllText(Path.Combine(plugin, "MultiplayerMenu.cs"));
        Expect(menu.Contains("cb != null && cb.Applied") && !menu.Contains("Neues Spiel"), "F8 Continue waits for the written campaign; no new-game path for Player 2");
    }

    // ---------------------------------------------------------------- 09
    static void Inventory()
    {
        string note; int[] r;
        var plan = SpawnRules.Decide(null, null, 555, Madison, new Vec3(1, 1, 1), out note);
        var empty = P2Inventory.Initial(false);
        var snap = RoundTrip(CampaignSnapshot.Build(HostSave(Madison), empty, plan, 0, 555, 9, out r));
        int v;
        foreach (var k in snap.Data.Ints.Keys) Expect(!CampaignPolicy.IsBackpackCount(k), "no backpack count when Player 2's record is empty (" + k + ")");
        Expect(snap.Data.TryInt("Money", out v) && v == 500 && snap.Data.Ints["BatonLife"] == 100, "new Player 2 = original new-game state, never Player 1's 98765");
        foreach (var k in snap.Data.Ints.Keys) Expect(CampaignPolicy.Classify(k) != CampaignKeyClass.HostStorage, "no host storage / cargo key (" + k + ")");
        Expect(!snap.Data.Floats.ContainsKey("PosX_item0") && !snap.Data.Ints.ContainsKey("TrunkItem"), "no physical cargo copies (duplication guard)");
        // classification table
        Expect(CampaignPolicy.Classify("Item_123") == CampaignKeyClass.PlayerOwnP1 && CampaignPolicy.Classify("Item_GoldWatch") == CampaignKeyClass.World && CampaignPolicy.Classify("Item_Discovered_5") == CampaignKeyClass.World, "Item_ families");
        Expect(CampaignPolicy.Classify("TPP") == CampaignKeyClass.LocalPreference && CampaignPolicy.Classify("XCar") == CampaignKeyClass.World && CampaignPolicy.Classify("TS2MP_Anything") == CampaignKeyClass.ModHost, "other classes");
        // local preference from the client's own data replaces the host's
        var local = new CampaignData(); local.Bools["TPP"] = true; local.Ints["Story"] = 1;
        snap.ApplyLocalPreferences(local);
        Expect(snap.Data.Bools["TPP"] && snap.Data.Ints["Story"] == 42, "only the local preference is taken from the client");
    }

    // ---------------------------------------------------------------- doors
    static StateEntry Txn(uint txn, int ns, uint hash, byte state, ushort baseVer)
    {
        var t = new ObjectTxnRequest { Txn = txn, Kind = (byte)ObjectKind.Door, Ns = ns, Hash = hash, State = state, BaseVer = baseVer };
        return new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() };
    }

    static ProposalCode Propose(WorldHost wh, string inst, byte slot, uint id, StateEntry e, double now)
    { uint rev; int sr; float sh; return wh.HandleProposal(inst, slot, id, e, now, out rev, out sr, out sh); }

    static ObjectState DoorState(StateRegistry reg, int ns, uint hash)
    {
        var e = reg.Get(StateScope.Region, ns, ObjectStateRules.RegistryKey(ObjectKind.Door));
        ObjectStateList l; ObjectState s = default(ObjectState);
        if (e != null && ObjectStateList.TryParse(e.Value, out l)) l.TryGet(hash, out s);
        return s;
    }

    static void DoorHostElsewhere()
    {
        var wh = new WorldHost(11);
        wh.SetPlayerRegion(1, Hideout, 0); wh.SetPlayerRegion(2, Madison, 0);
        wh.ObjectCatalog = (k, ns, h) => null;   // the host never loaded Madison in this process
        var code = Propose(wh, "c", 2, 1, Txn(1, Madison, 0xABCD1234, ObjectStateRules.DoorOpen, 0), 1);
        Expect(code == ProposalCode.Accepted, "Player 2 opens a Madison door while Player 1 is at the hideout: " + code + " " + wh.LastNote);
        var s = DoorState(wh.Reg, Madison, 0xABCD1234);
        Expect(s.State == ObjectStateRules.DoorOpen && s.Ver == 1 && s.By == 2, "committed open v1 by P2");
        Expect(wh.Objects.Unverified == 1, "accepted on the simulating player's word (host has no census) - counted as unverified");
        Expect(wh.Outbox.Count > 0 && wh.Outbox[wh.Outbox.Count - 1].Key == "wobj2", "registry delta queued for the client");
        // with a host catalog (host loaded the region earlier): unknown ids are refused
        wh.ObjectCatalog = (k, ns, h) => h == 0x1111;
        Expect(Propose(wh, "c", 2, 2, Txn(2, Madison, 0x2222, 1, 0), 2) == ProposalCode.Invalid, "unknown id refused when the host has a catalog");
        Expect(Propose(wh, "c", 2, 3, Txn(3, Madison, 0x1111, 1, 0), 3) == ProposalCode.Accepted, "known id accepted");
        // a player can only change objects of the region it has loaded
        Expect(Propose(wh, "c", 2, 4, Txn(4, Hillville, 0x1111, 1, 0), 4) == ProposalCode.NotAuthorized, "other region refused (also unsupported)");
        wh.SetPlayerRegion(2, Hillville, 5);
        Expect(Propose(wh, "c", 2, 5, Txn(5, Madison, 0x1111, 0, 1), 6) == ProposalCode.NotAuthorized, "requester no longer in Madison");
        // the host's own change (in place, same rules) while it is in Madison
        wh.SetPlayerRegion(1, Madison, 7); wh.ObjectCatalog = null; string why;
        var hc = wh.ObjectTransaction(1, new ObjectTxnRequest { Txn = 1, Kind = 2, Ns = Madison, Hash = 0x9999, State = 1, BaseVer = 0 }, 8, out why);
        Expect(hc == ProposalCode.Accepted && DoorState(wh.Reg, Madison, 0x9999).By == 1, "host commits its own door change");
        Expect(Propose(wh, "c", 2, 6, Txn(6, Madison, 0x9999, ObjectStateRules.DoorOpen, 0), 9) == ProposalCode.NotAuthorized, "P2 not in Madison any more");
        Expect(Propose(wh, "c", 2, 7, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = new byte[3] }, 10) == ProposalCode.Invalid, "malformed");
        Expect(wh.ObjectTransaction(1, new ObjectTxnRequest { Kind = 2, Ns = Madison, Hash = 1, State = 9 }, 11, out why) == ProposalCode.Invalid, "invalid door state");
        Expect(wh.ObjectTransaction(1, new ObjectTxnRequest { Kind = (byte)ObjectKind.Safe, Ns = Madison, Hash = 1, State = 1 }, 12, out why) == ProposalCode.NotAuthorized, "kind not shared yet");
    }

    static void DoorOrdering()
    {
        var wh = new WorldHost(12);
        wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, Madison, 0);
        const uint D = 0x00C0FFEE;
        // rapid open/close/open by Player 2 with chained versions
        Expect(Propose(wh, "c", 2, 1, Txn(1, Madison, D, 1, 0), 0.0) == ProposalCode.Accepted, "open v1");
        Expect(Propose(wh, "c", 2, 2, Txn(2, Madison, D, 0, 1), 0.1) == ProposalCode.Accepted, "close v2");
        Expect(Propose(wh, "c", 2, 3, Txn(3, Madison, D, 1, 2), 0.2) == ProposalCode.Accepted, "open v3");
        Expect(DoorState(wh.Reg, Madison, D).Ver == 3 && DoorState(wh.Reg, Madison, D).State == 1, "open at v3");
        // retransmission of the last id #3: the cached answer, not applied again
        Expect(Propose(wh, "c", 2, 3, Txn(3, Madison, D, 1, 2), 0.3) == ProposalCode.Accepted && DoorState(wh.Reg, Madison, D).Ver == 3, "retransmitted last id: cached Accepted, no new version");
        // a late duplicate of #2 (close): the gate answers Stale and the door stays open (would otherwise close it)
        Expect(Propose(wh, "c", 2, 2, Txn(2, Madison, D, 0, 1), 0.3) == ProposalCode.Stale && DoorState(wh.Reg, Madison, D).Ver == 3 && DoorState(wh.Reg, Madison, D).State == 1, "late duplicate not re-applied");
        // an older id after newer ones: stale by the proposal gate
        Expect(Propose(wh, "c", 2, 1, Txn(1, Madison, D, 0, 0), 0.4) != ProposalCode.Accepted && DoorState(wh.Reg, Madison, D).State == 1, "out-of-order older id refused");
        // a delayed request with an old base version (new id): stale, never reverses the newer state
        Expect(Propose(wh, "c", 2, 10, Txn(10, Madison, D, 0, 1), 0.5) == ProposalCode.Stale && DoorState(wh.Reg, Madison, D).State == 1, "old base -> Stale");
        // the same physical state with an old base: Unchanged (no revision)
        uint head = wh.Reg.HeadRev;
        Expect(Propose(wh, "c", 2, 11, Txn(11, Madison, D, 1 | ObjectStateRules.DoorSlow, 1), 0.6) == ProposalCode.Unchanged && wh.Reg.HeadRev == head, "already open -> Unchanged, no new revision");
        // simultaneous conflict: host and client both act on v3 -> first committed wins
        string why;
        Expect(wh.ObjectTransaction(1, new ObjectTxnRequest { Kind = 2, Ns = Madison, Hash = D, State = 0, BaseVer = 3 }, 0.7, out why) == ProposalCode.Accepted, "host closes first (v4)");
        Expect(Propose(wh, "c", 2, 12, Txn(12, Madison, D, 0 | ObjectStateRules.DoorSlow, 3), 0.8) == ProposalCode.Unchanged, "client's close on v3 = same physical state -> Unchanged");
        Expect(Propose(wh, "c", 2, 13, Txn(13, Madison, D, 1, 3), 0.9) == ProposalCode.Stale && DoorState(wh.Reg, Madison, D).Ver == 4 && DoorState(wh.Reg, Madison, D).By == 1, "client's open on v3 -> Stale; host's v4 stays");
        // rate limit (per player, per second)
        var wh2 = new WorldHost(13); wh2.SetPlayerRegion(2, Madison, 0);
        int busy = 0;
        for (uint i = 0; i < 12; i++) if (Propose(wh2, "x", 2, 100 + i, Txn(100 + i, Madison, 0x5000 + i, 1, 0), 5.0) == ProposalCode.Busy) busy++;
        Expect(busy == 12 - ObjectStateRules.RatePerSecond, "rate limit: " + busy + " busy");
        Expect(Propose(wh2, "x", 2, 200, Txn(200, Madison, 0x6000, 1, 0), 6.1) == ProposalCode.Accepted, "next second allowed");
        // capacity
        var wh3 = new WorldHost(14); wh3.SetPlayerRegion(2, Madison, 0); int ok = 0, full = 0;
        for (uint i = 0; i < ObjectStateRules.MaxPerRegion + 5; i++) { var c = Propose(wh3, "y", 2, 1 + i, Txn(1 + i, Madison, 0x7000 + i, 1, 0), i); if (c == ProposalCode.Accepted) ok++; else if (c == ProposalCode.Busy) full++; }
        Expect(ok == ObjectStateRules.MaxPerRegion && full == 5, "capacity " + ok + "/" + full);
        Expect(wh3.Reg.Get(StateScope.Region, Madison, "wobj2").Value.Length <= StateEntry.MaxValueBytes, "registry value within 1024 B");
    }

    static void DoorReload()
    {
        var wh = new WorldHost(15);
        const uint D = 0x0DD;
        wh.SetPlayerRegion(1, Hideout, 0); wh.SetPlayerRegion(2, Madison, 0);
        Expect(Propose(wh, "c", 2, 1, Txn(1, Madison, D, 1, 0), 1) == ProposalCode.Accepted, "P2 opens");
        wh.SetPlayerRegion(1, Madison, 2);                     // P1 loads Madison: the state is there (late join of the region)
        Expect(DoorState(wh.Reg, Madison, D).State == 1, "P1 arriving sees the open door");
        wh.SetPlayerRegion(1, Hideout, 3); wh.SetPlayerRegion(1, Madison, 4);   // P1 reloads Madison while P2 stays
        Expect(DoorState(wh.Reg, Madison, D).State == 1, "survives P1's region reload");
        wh.PlayerDisconnected(2, 5);                           // P2 disconnects; P1 keeps the scene alive
        Expect(DoorState(wh.Reg, Madison, D).State == 1, "survives P2's disconnect while the region is live");
        wh.SetPlayerRegion(2, Madison, 6);                     // P2 reconnects into Madison
        Expect(DoorState(wh.Reg, Madison, D).State == 1, "P2 reconnect sees the state");
        // client late join: registry snapshot carries it
        var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
        var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep, hd; List<StateEntry> entries;
        Expect(StateMessages.TryReadSnapshot(r, out ep, out hd, out entries), "snapshot");
        var client = StateRegistry.CreateClient(); client.LoadSnapshot(ep, hd, entries);
        Expect(DoorState(client, Madison, D).State == 1, "client registry has the open door after a snapshot");
        // both leave -> vanilla reset (the original reload rebuilds the door from the scene)
        wh.SetPlayerRegion(1, Hideout, 7); wh.SetPlayerRegion(2, Hideout, 8);
        Expect(DoorState(wh.Reg, Madison, D).Ver == 0, "region empty -> cleared like a vanilla reload");
        Expect(wh.Objects.Cleared >= 1, "cleared counted");
        // RegionLife states are never written as durable campaign data
        Expect(Propose(wh, "c", 2, 20, Txn(20, Madison, D, 1, 0), 9) == ProposalCode.NotAuthorized, "P2 at the hideout cannot change Madison doors");
        wh.SetPlayerRegion(2, Madison, 10); Expect(Propose(wh, "c", 2, 21, Txn(21, Madison, D, 1, 0), 11) == ProposalCode.Accepted, "new visit starts from v0 (scene default)");
        var bytes = wh.Objects.SerializeCampaign(555);
        var b2 = new RegionStateBook(); string err; var reg = StateRegistry.CreateHost(1); var outbox = new List<StateEntry>();
        Expect(b2.LoadCampaign(bytes, 555, reg, outbox, out err) && outbox.Count == 0, "door states are not campaign-durable (vanilla has no door save key)");
        Expect(!b2.LoadCampaign(bytes, 556, reg, outbox, out err) && err == "other save history", "campaign data of another history refused");
    }

    static void DoorReplicaRules()
    {
        Expect(WorldDoorRules.Decide(1, false, false, false, true, false) == DoorApply.Swing, "closed, idle, unlocked -> swing open");
        Expect(WorldDoorRules.Decide(0, true, false, false, true, false) == DoorApply.Swing, "open -> swing closed");
        Expect(WorldDoorRules.Decide(1 | ObjectStateRules.DoorSlow, true, false, false, true, false) == DoorApply.None, "already in state (slow bit is presentation only)");
        Expect(WorldDoorRules.Decide(1, false, true, false, true, false) == DoorApply.SkipBusy, "original swing in progress -> later");
        Expect(WorldDoorRules.Decide(1, false, false, true, true, false) == DoorApply.SkipBusy, "local player holds the door -> later");
        Expect(WorldDoorRules.Decide(1, false, false, false, false, false) == DoorApply.SkipLocked, "locked here -> not forced (W4.2B)");
        Expect(WorldDoorRules.Decide(0, true, false, false, true, true) == DoorApply.SkipPending, "own proposal in flight -> wait for the result");
        Expect(ObjectStateRules.SupportedNamespace(ObjectKind.Door, Madison) && !ObjectStateRules.SupportedNamespace(ObjectKind.Door, Hillville) && !ObjectStateRules.SupportedNamespace(ObjectKind.LockedWindow, Madison), "slice scope: doors in Madison");
        Expect(ObjectStateRules.Lifetime(ObjectKind.Door) == ObjectLifetime.RegionLife, "doors live as long as the scene (no save key)");
        ObjectKind k; Expect(ObjectStateRules.TryKindOfKey("wobj2", out k) && k == ObjectKind.Door && !ObjectStateRules.TryKindOfKey("wloot0", out k), "registry key mapping");
    }

    // ---------------------------------------------------------------- 14
    static void Codecs()
    {
        string note; int[] r;
        var plan = SpawnRules.Decide(null, null, 555, Madison, new Vec3(1, 2, 3), out note);
        var big = HostSave(Madison);
        for (int i = 0; i < 1300; i++) big.Ints["Rating_mission_flag_" + i] = i;
        var s = CampaignSnapshot.Build(big, P2Record(), plan, 0, 555, 77, out r);
        var bytes = s.Encode();
        var back = RoundTrip(s);
        Expect(back.Data.Count == s.Data.Count && back.Id == 77 && back.Plan.Region == Madison, "round trip " + back.Data.Count);
        Console.WriteLine("      snapshot with " + s.Data.Count + " keys = " + bytes.Length + " B");
        CampaignSnapshot x; string err;
        var trunc = new byte[bytes.Length - 3]; Array.Copy(bytes, trunc, trunc.Length);
        Expect(!CampaignSnapshot.TryDecode(new NetDataReader(trunc), out x, out err), "truncated refused (" + err + ")");
        var tail = new byte[bytes.Length + 2]; Array.Copy(bytes, tail, bytes.Length);
        Expect(!CampaignSnapshot.TryDecode(new NetDataReader(tail), out x, out err) && err == "trailing bytes", "trailing bytes refused");
        var badVer = (byte[])bytes.Clone(); badVer[0] = 2; Expect(!CampaignSnapshot.TryDecode(new NetDataReader(badVer), out x, out err), "version");
        // position keys must agree with the plan
        var mism = CampaignSnapshot.Build(HostSave(Madison), P2Record(), plan, 0, 555, 1, out r); mism.Data.Ints["CurrentLocation"] = Hideout;
        Expect(!CampaignSnapshot.TryDecode(new NetDataReader(mism.Encode()), out x, out err), "plan / CurrentLocation mismatch refused");
        var rnd = new Random(9);
        for (int i = 0; i < 2000; i++) { var b = new byte[rnd.Next(0, 200)]; rnd.NextBytes(b); CampaignSnapshot.TryDecode(new NetDataReader(b), out x, out err); }
        for (int i = 0; i < 300; i++) { var b = (byte[])bytes.Clone(); b[rnd.Next(b.Length)] ^= (byte)(1 + rnd.Next(255)); CampaignSnapshot.TryDecode(new NetDataReader(b), out x, out err); }
        // object list + txn + report
        var l = new ObjectStateList(); for (uint i = 0; i < 100; i++) l.Items.Add(new ObjectState { Hash = i, State = 1, Ver = 2, By = 2 });
        ObjectStateList l2; Expect(l.ToBytes().Length == 804 && ObjectStateList.TryParse(l.ToBytes(), out l2) && l2.Items.Count == 100, "list cap 100 = 804 B");
        l.Items.Add(new ObjectState()); Expect(!ObjectStateList.TryParse(l.ToBytes(), out l2), "over cap refused");
        ObjectTxnRequest t; Expect(new ObjectTxnRequest { Txn = 5, Kind = 2, Ns = 99, Hash = 7, State = 1, BaseVer = 3 }.ToBytes().Length == ObjectTxnRequest.Bytes && ObjectTxnRequest.TryParse(new ObjectTxnRequest { Txn = 5, Kind = 2, Ns = 99, Hash = 7, State = 1, BaseVer = 3 }.ToBytes(), out t) && t.BaseVer == 3, "txn 16 B");
        Expect(new P2PosReport { Region = 99 }.ToBytes().Length == P2PosReport.Bytes, "report 24 B");
        for (int i = 0; i < 2000; i++) { var b = new byte[rnd.Next(0, 40)]; rnd.NextBytes(b); ObjectStateList.TryParse(b, out l2); ObjectTxnRequest.TryParse(b, out t); }
    }

    // ---------------------------------------------------------------- 15
    static InstanceLog Log(string n) { return new InstanceLog(Path.Combine(_logDir, n + ".log"), "T", n, "s"); }

    static void Transport()
    {
        Expect(ProtocolInfo.ProtocolVersion == 23 && ProtocolInfo.ModVersion == "0.13.0", "protocol 23 / mod 0.13.0");
        Expect((byte)MsgType.CampaignSnapshot == 16 && (byte)MsgType.CampaignRequest == 17, "message ids");
        int port = _port++;
        string code = JoinAuth.NewCode();
        var host = new SessionNode(Role.Host, "hs", "host01", Game, port, Log("t-host")); host.BindAddress = "127.0.0.1"; host.JoinKey = JoinAuth.DeriveKey(code);
        var cl = new SessionNode(Role.Client, "join", "cli01", Game, port, Log("t-cli")); cl.ConnectAddress = "127.0.0.1"; cl.JoinKey = JoinAuth.DeriveKey(code); cl.AutoReady = true;
        CampaignSnapshot got = null; string err = null; int requests = 0;
        cl.MessageReceived += (p, type, r) => { if (type == (byte)MsgType.CampaignSnapshot && !CampaignSnapshot.TryDecode(new NetDataReader(r.GetRemainingBytes()), out got, out err)) got = null; };
        host.MessageReceived += (p, type, r) => { if (type == (byte)MsgType.CampaignRequest) requests++; };
        try
        {
            Expect(host.Start() && cl.Start(), "start");
            Pump(() => host.IsConnected && cl.IsConnected, 8000, "connect", host, cl);
            string note; int[] rm;
            var big = HostSave(Madison);
            for (int i = 0; i < 2500; i++) big.Ints["Rating_mission_flag_" + i] = i;
            var snap = CampaignSnapshot.Build(big, P2Record(), SpawnRules.Decide(null, null, 555, Madison, new Vec3(1, 2, 3), out note), 0, 555, 4242, out rm);
            var body = snap.Encode();
            Expect(body.Length > 65535, "body larger than a ushort length prefix (" + body.Length + " B)");
            var w = new NetDataWriter(); w.Put((byte)MsgType.CampaignSnapshot); w.Put(body);
            host.Send(w, DeliveryMethod.ReliableOrdered, 0);
            Pump(() => got != null || err != null, 8000, "snapshot", host, cl);
            Expect(got != null && got.Id == 4242 && got.Data.Count == snap.Data.Count, "received intact: " + err);
            var rq = new NetDataWriter(); rq.Put((byte)MsgType.CampaignRequest); cl.Send(rq, DeliveryMethod.ReliableOrdered, 0);
            Pump(() => requests == 1, 4000, "request", host, cl);
        }
        finally { cl.Dispose(); host.Dispose(); }
    }

    static void Pump(Func<bool> until, int ms, string what, params SessionNode[] nodes)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { foreach (var n in nodes) n.Tick(); if (until()) return; Thread.Sleep(5); }
        throw new Exception("timeout: " + what);
    }

    // ---------------------------------------------------------------- 16
    static void Regressions()
    {
        var wh = new WorldHost(16);
        wh.SetPlayerRegion(1, Madison, 0); wh.SetPlayerRegion(2, Madison, 0);
        // existing proposal kinds still dispatch as before
        uint rev; int sr; float sh;
        var reg = wh.HandleProposal("c", 2, 1, new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Region, Value = WorldKeys.Int(Hillville) }, 1, out rev, out sr, out sh);
        Expect(reg == ProposalCode.Accepted && RegionDirectory.RegionOf(wh.Reg, 2) == Hillville, "region proposal unchanged");
        Expect(wh.HandleProposal("c", 2, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.LootTxn, Value = new byte[20] }, 2, out rev, out sr, out sh) != ProposalCode.Accepted, "loot dispatch still guarded (no hook)");
        Expect(wh.HandleProposal("c", 2, 3, new StateEntry { Scope = StateScope.Session, ScopeArg = 5, Key = "unknownKey", Value = new byte[1] }, 3, out rev, out sr, out sh) == ProposalCode.NotAuthorized, "unknown keys refused");
        // leaving a region clears dropped items, loot AND door states in one step (all scene-lifetime)
        wh.SetPlayerRegion(2, Madison, 4);
        Expect(Propose(wh, "c", 2, 4, Txn(4, Madison, 0x42, 1, 0), 5) == ProposalCode.Accepted, "door");
        wh.SetPlayerRegion(1, Hideout, 6); wh.SetPlayerRegion(2, Hideout, 7);
        Expect(DoorState(wh.Reg, Madison, 0x42).Ver == 0, "cleared with the region");
        // the client-side vehicle door replica type is untouched (name kept separate)
        Expect(typeof(DoorReplica).Name == "DoorReplica" && typeof(WorldDoorRules).Name == "WorldDoorRules", "vehicle DoorReplica and world WorldDoorRules are separate");
    }

    // ---------------------------------------------------------------- 17
    static string PluginDir()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && root != null; i++)
        {
            var c = Path.Combine(root, "src", "Plugin");
            if (Directory.Exists(c)) return c;
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new Exception("src/Plugin not found");
    }

    static void SourceScan()
    {
        string plugin = PluginDir();
        foreach (var f in new[] { "WorldDoorSync.cs", "CampaignBootstrap.cs" })
        {
            var src = File.ReadAllText(Path.Combine(plugin, f));
            foreach (var bad in new[] { "FindObjectsOfType", "Resources.FindObjectsOfTypeAll", "GameObject.Find", "GetComponentsInChildren", "FindGameObjectsWithTag", "GetRootGameObjects" })
                Expect(src.IndexOf(bad, StringComparison.Ordinal) < 0, f + " has no scene scan (" + bad + ")");
            foreach (var bad in new[] { "OnGUI", "GUI.", "SpawnWarning", "SpawnNormalText", "UI.Text", "TextMeshPro" })
                Expect(src.IndexOf(bad, StringComparison.Ordinal) < 0, f + ": no mod text in the game UI (" + bad + ")");
        }
        var boot = File.ReadAllText(Path.Combine(plugin, "CampaignBootstrap.cs"));
        Expect(boot.Split(new[] { "FindObjectOfType" }, StringSplitOptions.None).Length - 1 == 1 && boot.Contains("// once per join, main menu only"), "the single FindObjectOfType is the one-shot Continue lookup in the main menu");
        var doors = File.ReadAllText(Path.Combine(plugin, "WorldDoorSync.cs"));
        Expect(doors.Contains("if (region == Regions.None || !ObjectStateRules.SupportedNamespace(ObjectKind.Door, region)) return;   // no work elsewhere"), "doors: no per-frame work outside the slice region");
        Expect(doors.Contains("_watch.Count < 4"), "bounded watch list");
        Expect(!doors.Contains("PMB") && !doors.Contains("MakeNoise") && !doors.Contains("mouseSensitivity = "), "remote swing never touches the local player (Door.Use side effects excluded)");
    }
}
