// W1 offline tests: shared hideout ownership (src/Shared/Hideout.cs + WorldHost.BuyHideout / PublishHideout) and source
// scans of the Unity glue (HideoutSync: original purchase hooks, host apply, client presentation, coordinated original
// hideout rebuild, sandbox keys, snapshot overlay; wallet policy; plugin wiring).
// In-game behaviour (original windows, garage animation / swap, rebuild loading screen, positions) is manual two-PC QA
// (docs/TESTING.md §41).
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class W1Tests
{
    static int _passed, _failed;
    const int Hideout = 0, AutoShop = 3, Madison = 99;
    static uint _pid;

    // The host plugin's view: keys + queues of the host campaign; Apply mutates them like HideoutSync.Apply.
    sealed class FakeHost : IHideoutHost
    {
        public int Money = 20000; public bool Loading; public HideoutState S; public int Applies;
        public int[] Costs = (int[])HideoutShop.AdditionCosts.Clone(); public int[] Counts = { 24, 14, 24, 14, 8 };
        public bool TryBalance(out int b) { b = Money; return !Loading; }
        public bool TryState(out HideoutState s) { s = S; return !Loading; }
        public int AdditionCost(int i) { return Costs[i]; }
        public int WallpaperCount(int i) { return Counts[i]; }
        public bool Apply(HideoutBuyRequest r, int price)
        {
            Money -= price; Applies++;
            if (r.Item == HideoutItem.Garage) S.Garage = true;
            else if (r.Item == HideoutItem.Repairs) S.Repairs = true;
            else if (r.Item == HideoutItem.Addition) S.Additions |= (byte)(1 << r.Index);
            else S.SetWall(r.Index, r.Value);
            return true;
        }
    }

    static int Main()
    {
        Run("01 Player 1 (host) buys the garage: original purchase, shared state committed, second purchase refused", HostGarage);
        Run("02 Player 2 buys the garage: hbuy in the hideout -> host charges 8000 once and owns the garage", ClientGarage);
        Run("03 simultaneous / duplicate requests: retransmission cached, second request Stale, host first -> client Stale", Duplicates);
        Run("04 funds + original prerequisites: too little money, not in the hideout, host loading, malformed / out of range", Refusals);
        Run("05 renovations: additions (live costs), repairs 2500, wallpapers 50 per change; each owned item once", Renovations);
        Run("06 live state: 8-byte codec, rebuild only for additions / repairs, garage + wallpapers applied live", LiveState);
        Run("07 save / restart: host keys follow the original timing (mission / parcel queues, wallpaper at once), money in the same checkpoint", Persistence);
        Run("08 reconnect / join after a renovation: registry snapshot + campaign snapshot overlay, sandbox keys mirror the host", Reconnect);
        Run("09 cross-region: a player outside the hideout is never reloaded; the host decides without the hideout scene", CrossRegion);
        Run("10 coordinated rebuild: once per scene, deferred in a car / carrying / caught / menu, never during an own pending purchase", CoordinatedReload);
        Run("11 disconnect during a purchase / transition: nothing charged twice, host state unchanged, pending dropped", Disconnect);
        Run("12 wallet + wiring: four writers are host transactions, client hook fails closed, claims released, plugin order", Wiring);
        Run("13 regressions: Shadowhawk garage prerequisite, V1.2 hooks, travel scripted path, no mod UI text, no per-frame search", Regressions);
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

    static WorldHost Host(int p1, int p2, out FakeHost h)
    {
        var wh = new WorldHost(21) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, p1, 0);
        if (p2 != Regions.None) wh.SetPlayerRegion(2, p2, 0);
        h = new FakeHost(); wh.HideoutHost = h;
        wh.Outbox.Clear();
        return wh;
    }
    static HideoutBuyRequest Garage() { return new HideoutBuyRequest { Item = HideoutItem.Garage }; }
    static HideoutBuyRequest Repairs() { return new HideoutBuyRequest { Item = HideoutItem.Repairs }; }
    static HideoutBuyRequest Add(int i) { return new HideoutBuyRequest { Item = HideoutItem.Addition, Index = (byte)i }; }
    static HideoutBuyRequest Wall(int i, int v) { return new HideoutBuyRequest { Item = HideoutItem.Wallpaper, Index = (byte)i, Value = (byte)v }; }
    static ProposalCode Buy(WorldHost wh, HideoutBuyRequest r, uint pid = 0, byte[] raw = null, int arg = 0)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = arg, Key = WorldKeys.HideoutBuy, Value = raw ?? r.ToBytes() };
        return wh.HandleProposal("cli", 2, pid != 0 ? pid : ++_pid, p, 1, out rev, out sr, out sh);
    }
    static HideoutState Committed(WorldHost wh)
    {
        var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Hideout); HideoutState s;
        Expect(e != null && HideoutState.TryParse(e.Value, out s), "committed hideout state"); HideoutState.TryParse(e.Value, out s); return s;
    }

    // ------------------------------------------------------------------------------------------------ tests
    static void HostGarage()
    {
        FakeHost h; var wh = Host(Hideout, Madison, out h);
        // the original Inventory.BuyGarage ran on the host (moneyAm -= 8000, BoughtSeq queues "garage"); postfix -> PublishHideout
        h.Money -= HideoutShop.GaragePrice; h.S.Garage = true;
        Expect(wh.PublishHideout() && Committed(wh).Garage, "shared state committed (registry delta for Player 2)");
        Expect(!wh.PublishHideout(), "unchanged state: no new revision");
        int price; string why;
        Expect(HideoutShop.Decide(Garage(), Hideout, h, out price, out why) == ProposalCode.Stale && why == "garage already owned", "a second purchase is refused by the host prefix");
        var hs = Src("Plugin", "HideoutSync.cs");
        Expect(Body(hs, "bool OnBuy(object inst, MethodBase m)").Contains("if (_host)") && Body(hs, "static void BuyPostfix(object __instance, MethodBase __originalMethod)").Contains("PublishHideout()"), "host: the original runs, the postfix publishes");
    }

    static void ClientGarage()
    {
        FakeHost h; var wh = Host(Madison, Hideout, out h);
        Expect(Buy(wh, Garage()) == ProposalCode.Accepted, "Player 2 in the hideout with 20000: accepted");
        Expect(h.Applies == 1 && h.Money == 12000, "charged once on the host (shared balance)");
        Expect(Committed(wh).Garage && wh.HideoutPurchases == 1, "garage owned in the shared hideout, committed in the same host step");
        Expect(wh.LastNote.Contains("hideout buy Garage by P2 -> Accepted"), "logged: " + wh.LastNote);
        Expect(HideoutShop.GaragePrice == 8000, "original price (Inventory.BuyGarage IL_001d)");
        var hs = Src("Plugin", "HideoutSync.cs");
        var ap = Body(hs, "public bool Apply(HideoutBuyRequest r, int price)");
        Expect(ap.Contains("m.Add(HideoutShop.GarageKey)") && ap.Contains("_setGmBought(gm, true);"), "host applies Garage.BoughtSeq's campaign part (mission_strings 'garage' -> saved as 2, boughtGarage)");
        Expect(!ap.Contains("buyAnimObj") && !ap.Contains("SetAchievement") && !ap.Contains(".Invoke("), "no buy animation / camera lock / achievement on the host for Player 2's purchase");
    }

    static void Duplicates()
    {
        FakeHost h; var wh = Host(Hideout, Hideout, out h);
        uint id = ++_pid;
        Expect(Buy(wh, Garage(), id) == ProposalCode.Accepted && Buy(wh, Garage(), id) == ProposalCode.Accepted && h.Applies == 1, "retransmission: cached answer, applied once");
        Expect(Buy(wh, Garage()) == ProposalCode.Stale && h.Applies == 1 && h.Money == 12000, "second request: Stale (never Unchanged = no false success), one charge");
        FakeHost h2; var w2 = Host(Hideout, Hideout, out h2);
        h2.Money -= 8000; h2.S.Garage = true; w2.PublishHideout();   // host's original ran first (client proposals are applied in LateUpdate)
        Expect(Buy(w2, Garage()) == ProposalCode.Stale && h2.Applies == 0 && h2.Money == 12000, "host first: client refused, one charge");
        FakeHost h3; var w3 = Host(Hideout, Hideout, out h3);
        Expect(Buy(w3, Add(0)) == ProposalCode.Accepted && Buy(w3, Add(0)) == ProposalCode.Stale && Buy(w3, Add(1)) == ProposalCode.Accepted, "same addition twice: one charge; another addition independent");
        Expect(h3.Money == 20000 - 1000 - 1500, "1000 + 1500 charged");
    }

    static void Refusals()
    {
        FakeHost h; var wh = Host(Madison, Hideout, out h);
        h.Money = 7999;
        Expect(Buy(wh, Garage()) == ProposalCode.Stale && h.Applies == 0, "7999 < 8000: refused, nothing charged");
        h.Money = 20000; wh.SetPlayerRegion(2, AutoShop, 1);
        Expect(Buy(wh, Garage()) == ProposalCode.NotAuthorized && h.Applies == 0, "not in the hideout (trigger / computer exist only there)");
        wh.SetPlayerRegion(2, Hideout, 2); h.Loading = true;
        Expect(Buy(wh, Garage()) == ProposalCode.Busy && h.Applies == 0, "host scene loading: Busy, retry possible");
        h.Loading = false; wh.HideoutHost = null;
        Expect(Buy(wh, Garage()) == ProposalCode.Busy, "host hideout glue missing: Busy");
        wh.HideoutHost = h;
        Expect(Buy(wh, Garage(), 0, new byte[] { 1, 0 }) == ProposalCode.Invalid && Buy(wh, Garage(), 0, new byte[] { 9, 0, 0 }) == ProposalCode.Invalid, "malformed");
        Expect(Buy(wh, Garage(), 0, new byte[] { 3, 4, 0 }) == ProposalCode.Invalid && Buy(wh, Garage(), 0, new byte[] { 4, 5, 0 }) == ProposalCode.Invalid, "addition / setting index out of range");
        Expect(Buy(wh, Garage(), 0, new byte[] { 1, 1, 0 }) == ProposalCode.Invalid && Buy(wh, Garage(), 0, null, 7) == ProposalCode.Invalid, "garage with an index / wrong scope arg");
        Expect(Buy(wh, Wall(4, 8)) == ProposalCode.Invalid && Buy(wh, Wall(0, 0)) == ProposalCode.Stale, "material beyond the table / already set");
        h.Costs[2] = 0;
        Expect(Buy(wh, Add(2)) == ProposalCode.Busy, "live addition table does not match the verified one: no guessed price");
        Expect(h.Applies == 0 && h.Money == 20000 && wh.HideoutRefusals >= 6, "nothing changed by refused requests");
    }

    static void Renovations()
    {
        FakeHost h; var wh = Host(Hideout, Hideout, out h); h.Money = 100000;
        for (int i = 0; i < 4; i++) Expect(Buy(wh, Add(i)) == ProposalCode.Accepted, "addition " + HideoutShop.AdditionPrefs[i]);
        Expect(h.Money == 100000 - 5500, "1000 + 1500 + 1500 + 1500 (scene data) charged: " + h.Money);
        Expect(Buy(wh, Repairs()) == ProposalCode.Accepted && h.Money == 100000 - 5500 - 2500 && Buy(wh, Repairs()) == ProposalCode.Stale, "repairs 2500 once");
        Expect(Buy(wh, Wall(0, 5)) == ProposalCode.Accepted && Buy(wh, Wall(0, 5)) == ProposalCode.Stale && Buy(wh, Wall(0, 6)) == ProposalCode.Accepted && Buy(wh, Wall(4, 7)) == ProposalCode.Accepted, "wallpapers: 50 per change, repeatable with another material");
        Expect(h.Money == 100000 - 5500 - 2500 - 150, "wallpaper charges");
        var s = Committed(wh);
        Expect(s.Additions == 0x0F && s.Repairs && s.W0 == 6 && s.W4 == 7 && !s.Garage, "committed: " + s);
        Expect(HideoutShop.AdditionPrefs[0] == "AudioUpgrade" && HideoutShop.AdditionPrefs[1] == "AntiTheft" && HideoutShop.AdditionPrefs[2] == "CatStuff" && HideoutShop.AdditionPrefs[3] == "LoreBooks"
            && HideoutShop.WallPrefs[0] == "WallpaperBedroom" && HideoutShop.WallPrefs[4] == "FloorStorage" && HideoutShop.RepairsPrice == 2500 && HideoutShop.WallpaperPrice == 50, "verified original keys / prices");
    }

    static void LiveState()
    {
        var s = new HideoutState { Garage = true, Repairs = true, Additions = 0x0A, W0 = 1, W1 = 2, W2 = 3, W3 = 4, W4 = 5 };
        HideoutState b; Expect(HideoutState.TryParse(s.ToBytes(), out b) && b.Same(s) && s.ToBytes().Length == 8, "round trip 8 B");
        Expect(!HideoutState.TryParse(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }, out b) && !HideoutState.TryParse(new byte[] { 1, 4, 0, 0, 0, 0, 0, 0 }, out b) && !HideoutState.TryParse(new byte[] { 1, 0, 0x10, 0, 0, 0, 0, 0 }, out b) && !HideoutState.TryParse(new byte[7], out b), "malformed refused");
        var built = new HideoutState();
        Expect(new HideoutState { Garage = true }.NeedsRebuild(built) == false && new HideoutState { W2 = 3 }.NeedsRebuild(built) == false, "garage / wallpapers never force a reload (the original applies them live)");
        Expect(new HideoutState { Repairs = true }.NeedsRebuild(built) && new HideoutState { Additions = 4 }.NeedsRebuild(built), "additions / repairs need the original rebuild (read only in Start)");
        Expect(!new HideoutState { Additions = 4 }.NeedsRebuild(new HideoutState { Additions = 5 }) && new HideoutState { W1 = 1 }.WallsDiffer(built), "already built: no reload; wall diff detected");
        var hs = Src("Plugin", "HideoutSync.cs");
        var pr = Body(hs, "void Present(object gm, double now)");
        Expect(pr.Contains("_garageDisable(g)") && pr.Contains("_garageEnable(g)") && pr.Contains("_setGmBought(gm, true)") && pr.Contains("_refreshLights.Invoke(dn, null)"), "garage: BoughtSeq scene swap (objects incl. colliders / entrance, boughtGarage, lights)");
        Expect(pr.Contains("FindObjectsOfType(_checkT)") && pr.Contains("_checkAll.Invoke(c, null)"), "wallpapers: CheckRenovations.CheckAll like SetWallpaperBedroom");
        Expect(pr.Contains("_selectHideout.Invoke(gm, null)") && !pr.Contains("buyAnimObj"), "rebuild = original GameManager.SelectHideout; no animation copy");
    }

    static void Persistence()
    {
        var hs = Src("Plugin", "HideoutSync.cs");
        var ap = Body(hs, "public bool Apply(HideoutBuyRequest r, int price)");
        Expect(ap.Contains("_setMoney(ps, before - price);"), "host moneyAm (shared balance) charged in memory -> saved with the next checkpoint");
        Expect(ap.Contains("var p = _gmParcel(gm); if (p != null && !Has(p, key)) p.Add(key);"), "additions / repairs queued in parcel_strings_to_set (written as 1 by the next SaveGameNewSystem, same checkpoint as Money)");
        Expect(ap.Contains("_prefSet(HideoutShop.WallPrefs[r.Index], r.Value);"), "wallpapers written at once (original SetWallpaperBedroom timing)");
        var ts = Body(hs, "public bool TryState(out HideoutState s)");
        Expect(ts.Contains("FromPrefs()") && ts.Contains("Has(mission, HideoutShop.GarageKey) || _gmBought(gm)") && ts.Contains("Has(parcel, HideoutShop.RepairsKey)"), "host state = keys + queued purchases (a checkpoint reload without a save drops both together)");
        var fp = Body(hs, "HideoutState FromPrefs()");
        Expect(fp.Contains("Pref(HideoutShop.GarageKey) >= 1") && fp.Contains("Pref(HideoutShop.RepairsKey) == 1"), "load rules: Garage.Start garage >= 1, CheckRenovations HouseRepairs == 1");
        // restart: a new WorldHost knows nothing until the host plugin publishes from the loaded keys
        FakeHost h; var wh = Host(Hideout, Regions.None, out h); h.S.Repairs = true; h.S.Garage = true;
        Expect(wh.PublishHideout() && Committed(wh).Repairs && Committed(wh).Garage, "after a restart the saved keys rebuild the shared state (published at scene bind)");
        Expect(Body(hs, "public void Update()").Contains("_world.Host.PublishHideout()"), "published at every host scene bind");
    }

    static void Reconnect()
    {
        FakeHost h; var wh = Host(Hideout, Hideout, out h);
        Buy(wh, Add(3)); Buy(wh, Wall(2, 9));
        var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Hideout);
        Expect(e != null && e.Value.Length == 8, "the registry snapshot of a rejoining client carries the hideout state (no replay, no payment)");
        var hs = Src("Plugin", "HideoutSync.cs");
        var ov = Body(hs, "internal void OverlaySnapshot(CampaignData model)");
        Expect(ov.Contains("model.Ints[HideoutShop.RepairsKey] = 1") && ov.Contains("model.Ints[HideoutShop.AdditionPrefs[i]] = 1") && ov.Contains("model.Ints[HideoutShop.GarageKey] = 2"), "campaign snapshot includes purchases still queued on the host");
        Expect(Src("Plugin", "CampaignBootstrap.cs").Contains("var hs = HideoutSync.Instance; if (hs != null) hs.OverlaySnapshot(model);"), "overlay applied right before CampaignSnapshot.Build");
        var sb = Body(hs, "void ApplySandbox()");
        Expect(sb.Contains("if (_host || !_hasCommitted) return;") && sb.Contains("_prefSet(HideoutShop.GarageKey, 2)") && sb.Contains("SetIf(HideoutShop.WallPrefs[i], s.Wall(i))"), "client sandbox keys follow the committed state exactly; the host never takes them from the registry");
        var c = new CampaignData(); c.Ints["garage"] = 0;
        Expect(CampaignPolicy.Classify("garage") == CampaignKeyClass.World && CampaignPolicy.Classify("HouseRepairs") == CampaignKeyClass.World && CampaignPolicy.Classify("AudioUpgrade") == CampaignKeyClass.World, "hideout keys are World keys of the campaign snapshot (sent to Player 2)");
    }

    static void CrossRegion()
    {
        var hs = Src("Plugin", "HideoutSync.cs");
        var up = Body(hs, "public void Update()");
        Expect(up.Contains("if (region != HideoutShop.Region || !_hasBuilt || _pend != null) return;   // elsewhere: the next original hideout load builds it"), "a player outside the hideout: sandbox keys only, never a reload / teleport / loading screen");
        Expect(up.IndexOf("if (changed) ApplySandbox();", StringComparison.Ordinal) < up.IndexOf("if (region != HideoutShop.Region", StringComparison.Ordinal), "keys are written in every region (the next original load reads them)");
        foreach (var sig in new[] { "public bool Apply(HideoutBuyRequest r, int price)", "public bool TryState(out HideoutState s)", "public int AdditionCost(int index)", "public int WallpaperCount(int setting)" })
            Expect(!Body(hs, sig).Contains("FindObjectOfType"), sig + ": host decides / applies without the hideout scene (MainScene HouseRenovator + GameManager lists)");
        Expect(Body(hs, "public int AdditionCost(int index)").Contains("_addPref(add) != HideoutShop.AdditionPrefs[index]"), "live table checked against the verified keys");
        // the host outside the hideout decides a Player-2 purchase
        FakeHost h; var wh = Host(Madison, Hideout, out h);
        Expect(Buy(wh, Repairs()) == ProposalCode.Accepted && Committed(wh).Repairs, "Scenario D: host in Madison, Player 2 renovates");
        Expect(Body(Src("Plugin", "VehicleSync.cs"), "public bool OnSelect(object gm, MethodBase method, int dest)").Contains("Code-driven travel (arrest, heist start, stolen-car delivery, hideout repairs): original behaviour"), "the rebuild is the original scripted SelectHideout (no travel request, no car carried)");
    }

    static void CoordinatedReload()
    {
        var hs = Src("Plugin", "HideoutSync.cs");
        var pr = Body(hs, "void Present(object gm, double now)");
        Expect(pr.Contains("s.NeedsRebuild(_built) && !ReferenceEquals(_reloadGm, gm)") && pr.Contains("_reloadGm = gm; _reloads++;"), "at most one rebuild per scene instance");
        foreach (var w in new[] { "_psInVehicle(ps)", "_psCarrying(ps)", "_psCaught(ps)", "_psOpened(ps)" }) Expect(pr.Contains(w), "deferred while " + w);
        Expect(pr.Contains("_retry = true"), "deferred rebuild retried at 1 Hz");
        var ob = Body(hs, "bool OnBuy(object inst, MethodBase m)");
        Expect(ob.Contains("if (r.Item == HideoutItem.Addition || r.Item == HideoutItem.Repairs) _reloadGm = gm;"), "host's own renovation: the original reloads, no second SelectHideout");
        var res = Body(hs, "internal bool OnProposalResult(uint id, ProposalCode code)");
        Expect(res.Contains("_resume = true; _reloadGm = gm;") && res.IndexOf("ApplySandbox();", StringComparison.Ordinal) < res.IndexOf("p.Method.Invoke(p.Instance, null);", StringComparison.Ordinal), "client actor: keys first, then the original (its own SelectHideout), no extra reload");
        Expect(Body(hs, "public void Update()").Contains("_pend != null) return;"), "no observer rebuild while an own purchase waits for its result (the delta precedes the result)");
        Expect(Body(hs, "public void Update()").Contains("if (!_gmLoaded(gm)) return;   // the original Start methods have not read the keys yet"), "built state taken after the original Start methods (loadedMap)");
    }

    static void Disconnect()
    {
        FakeHost h; var wh = Host(Hideout, Hideout, out h);
        Expect(Buy(wh, Add(1)) == ProposalCode.Accepted, "accepted before the disconnect");
        wh.SetPlayerRegion(2, Regions.None, 5);   // Player 2 gone mid-transition
        Expect(Committed(wh).Has(1) && h.Applies == 1 && h.Money == 18500, "host state and charge unchanged by the disconnect (applied once)");
        uint rev; int sr; float sh;
        Expect(wh.HandleProposal("cli", 2, 1, new StateEntry { Scope = StateScope.Session, Key = WorldKeys.HideoutBuy, Value = Add(1).ToBytes() }, 6, out rev, out sr, out sh) != ProposalCode.Accepted || h.Applies == 1, "a stale retransmission after the rejoin never charges again");
        var hs = Src("Plugin", "HideoutSync.cs");
        Expect(Body(hs, "void OnDisconnected(string reason)").Contains("_pend = null"), "client pending purchase dropped (the shared state shows the host's decision)");
        var res = Body(hs, "internal bool OnProposalResult(uint id, ProposalCode code)");
        Expect(res.Contains("finally { _resume = false; _ran = false; if (_money(ps) != before) _setMoney(ps, before); }"), "client money restored even if the original throws");
    }

    static void Wiring()
    {
        foreach (var m in new[] { "Inventory.BuyGarage", "HouseRenovator.BuyAddition", "HouseRenovator.BuyRepairs", "HouseRenovator.SetWallpaperBedroom" })
        {
            bool found = false;
            foreach (var w in WalletPolicy.Writers) if (w.Type + "." + w.Method == m) { found = true; Expect(w.Policy == MoneyPolicy.HostTxn, m + " = host transaction"); }
            Expect(found, m + " classified");
        }
        var hs = Src("Plugin", "HideoutSync.cs");
        Expect(Body(hs, "static bool BuyPrefix(object __instance, MethodBase __originalMethod)").Contains("return h._host; }   // client fails closed"), "client hook fails closed (no local charge on an exception)");
        Expect(Body(hs, "public void Dispose()").Contains("HostTxnHooks.Release(k)") && hs.Contains("HostTxnHooks.Claim(kv.Key);"), "claims follow the installed hooks (else WalletSync refuses them like before)");
        Expect(Body(hs, "bool OnBuy(object inst, MethodBase m)").Contains("_money(ps) < price) return true;"), "client below the price: the original 'too poor' page (no money change)");
        var p = Src("Plugin", "Plugin.cs");
        int hi = p.IndexOf("_hideout = new HideoutSync(node, log, _world)", StringComparison.Ordinal), wi = p.IndexOf("_wallet = new WalletSync(node, log, _world)", StringComparison.Ordinal);
        Expect(hi > 0 && wi > hi, "HideoutSync built before WalletSync (claims exist when the wallet policy installs)");
        Expect(Src("Plugin", "WorldSync.cs").Contains("ProposalResultHook9 != null && ProposalResultHook9(id, code)"), "proposal results routed");
        Expect(WorldKeys.HideoutBuy == "hbuy" && WorldKeys.Hideout == "hideout" && ProtocolInfo.ProtocolVersion == 32 && ProtocolInfo.ModVersion == "0.21.0", "keys / protocol 32 / mod 0.21.0");
    }

    static void Regressions()
    {
        var vs = Src("Plugin", "VehicleShopSync.cs");
        Expect(vs.Contains("Pref(\"garage\") >= 1 || (gm != null && _gmMission != null && ListHas(_gmMission(gm), \"garage\"))"), "Shadowhawk prerequisite accepts the queued garage (Player 2's garage purchase unlocks the Shadowhawk sign condition on the host)");
        var hs = Src("Plugin", "HideoutSync.cs");
        Expect(Body(hs, "void ApplySandbox()").Contains("_prefSet(HideoutShop.GarageKey, 2)"), "client sandbox garage key -> the Auto Shop sign (DestroyIfPref garage == 0) appears for Player 2 too");
        foreach (var bad in new[] { "OnGUI", "GUI.", "Time.timeScale =", "SpawnMessage(", "LoadLevel", "LoadScene" })
            Expect(!hs.Contains(bad), "HideoutSync contains no '" + bad + "' (no mod UI text, no own scene loading)");
        var up = Body(hs, "public void Update()");
        Expect(!up.Contains("FindObjectOfType") && !up.Contains("FindObjectsOfType"), "no scene search in the per-frame path (only in Present after a state change)");
        Expect(Src("Plugin", "TravelHooks.cs").Contains("HostTxnHooks") == false && Src("Plugin", "VehicleSync.cs").Contains("HostTxnHooks.Release(\"Tutorial.BuyVan\");"), "V1.2 BuyVan claim lives with VehicleSync");
    }

    // ------------------------------------------------------------------------------------------------ source helpers
    static string Src(params string[] p)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var f = Path.Combine(dir, "src", Path.Combine(p));
            if (File.Exists(f)) return File.ReadAllText(f);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new Exception("source not found: " + string.Join("/", p));
    }
    static string Body(string src, string signature)
    {
        int a = src.IndexOf(signature, StringComparison.Ordinal); if (a < 0) throw new Exception("missing " + signature);
        int open = src.IndexOf('{', a), depth = 0;
        for (int i = open; i < src.Length; i++) { if (src[i] == '{') depth++; else if (src[i] == '}' && --depth == 0) return src.Substring(a, i - a + 1); }
        throw new Exception("unbalanced " + signature);
    }
}
