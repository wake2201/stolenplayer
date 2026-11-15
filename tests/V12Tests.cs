// V1.2 offline tests: shared Shadowhawk purchase / unlock (VehicleShop + WorldHost.BuyVehicle / SetUnlocked), Shadowhawk
// presentation in the hideout after the purchase, and the Auto Shop travel-only proxy (PresentationRules.AutoShopProxyCar),
// plus source scans of the Unity glue (TravelHooks.BuyVan, VehicleShopSync, WalletSync policy, HeavyCargoSync proxy rule).
// In-game behaviour (original purchase window, sign, car placement, proxy interaction, travel menu) is manual two-PC QA
// (docs/TESTING.md §40).
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class V12Tests
{
    static int _passed, _failed;
    const int Hideout = 0, AutoShop = 3, Hillville = 97, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);
    static uint _pid;

    sealed class FakeShop : IVehicleShopHost
    {
        public int Money = 30000; public bool Garage = true, Ready = true; public int Charges; public readonly List<string> Parcel = new List<string>();
        public bool TryBalance(out int b) { b = Money; return Ready; }
        public bool GarageOwned { get { return Garage; } }
        public bool Charge(ushort v, int price) { Money -= price; Charges++; if (!Parcel.Contains("VanBought")) Parcel.Add("VanBought"); return true; }
    }

    static int Main()
    {
        Run("01 Player 1 buys the Shadowhawk: original purchase -> host unlock state -> fleet unlocked at once, second host click refused", P1Buys);
        Run("02 Player 2 buys the Shadowhawk: vbuy at the Auto Shop -> host charges the shared balance once, queues VanBought, unlocks", P2Buys);
        Run("03 simultaneous / repeated purchases: retransmission, second request, host click + client request -> exactly one charge", Simultaneous);
        Run("04 refused purchases: too little money, wrong region, no garage, host loading, wrong car, malformed -> no charge, still locked", Invalid);
        Run("05 unlock consistency + persistence: idempotent, checkpoint rollback re-locks, fleet file + registry carry the flag", Persistence);
        Run("06 Shadowhawk in the hideout after the purchase: Hidden -> Real for the present holder, ParkedOther / AwayProxy for the other", HideoutAfterBuy);
        Run("07 reconnect / host restart: registry snapshot keeps the unlock; restart derives it from the saved key (unsaved purchase lost with its money)", Restart);
        Run("08 Auto Shop proxy appears when the real car leaves: one owned car elsewhere -> AwayProxy (travel menu only, solid, no storage)", ProxyAppears);
        Run("09 the remaining player travels through the proxy: on-foot travel request, the real car keeps its region / holder / reservation", ProxyTravel);
        Run("10 roles reversed: the same rule for host and client, whoever stays; host leaving with the car leaves a proxy for Player 2", Roles);
        Run("11 a returning real car replaces the proxy: same car in place, another car -> proxy hidden; preference for the car last seen here", Returning);
        Run("12 never a second real car / duplicate cargo / double charge: at most one Real per car, proxy carries no cargo", NoDuplicates);
        Run("13 regressions: hideout / other-region presentations unchanged, doors + tailgate sync skips proxies, wallet fallback, glue scans", Regressions);
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
    static WorldHost Host(int p1, int p2, bool gtOwned, out FakeShop shop, int rangerRegion = AutoShop)
    {
        var wh = new WorldHost(11) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, p1, 0);
        if (p2 != Regions.None) wh.SetPlayerRegion(2, p2, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);
        wh.Fleet.Register(GT, 1, gtOwned, wh.Outbox);
        wh.Fleet.Place(Ranger, rangerRegion, wh.Outbox);
        wh.Fleet.Place(GT, Hideout, wh.Outbox);   // EnsureHostFleet FLEET_INIT: a car without fleet record (locked GT) is placed in the hideout
        wh.FleetInitialized(0);
        shop = new FakeShop(); wh.Shop = shop;
        wh.Outbox.Clear();
        return wh;
    }
    static VehicleRecord R(WorldHost wh, ushort id) { VehicleRecord r; Expect(wh.Fleet.TryGet(id, out r), "record " + id); return r; }
    static ProposalCode Buy(WorldHost wh, uint pid = 0, ushort vid = 0, byte[] value = null)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = vid == 0 ? GT : vid, Key = WorldKeys.VehicleBuy, Value = value ?? new byte[] { 1 } };
        return wh.HandleProposal("cli", 2, pid != 0 ? pid : ++_pid, p, 1, out rev, out sr, out sh);
    }
    // Host plugin SyncHostUnlock: unlocked = pref VanBought >= 1 || VanBought queued in parcel_strings_to_set
    static bool HostSync(WorldHost wh, int pref, List<string> parcel) { return wh.SetUnlocked(GT, pref >= 1 || parcel.Contains("VanBought")); }
    static IEnumerable<KeyValuePair<ushort, VehicleRecord>> All(WorldHost wh) { foreach (var id in wh.Fleet.Ids()) yield return new KeyValuePair<ushort, VehicleRecord>(id, R(wh, id)); }
    static CarPresentation Pres(WorldHost wh, ushort id, int localRegion, byte slot, ushort preferred = 0)
    {
        ushort proxy = PresentationRules.AutoShopProxyCar(All(wh), localRegion, preferred);
        return PresentationRules.Decide(R(wh, id), localRegion, slot, id == proxy);
    }
    static bool VehicleEntryChanged(WorldHost wh, ushort id) { foreach (var e in wh.Outbox) if (e.Key == WorldKeys.Vehicle && e.ScopeArg == id) return true; return false; }

    // ------------------------------------------------------------------------------------------------ tests
    static void P1Buys()
    {
        FakeShop shop; var wh = Host(AutoShop, Hideout, false, out shop);
        Expect(!R(wh, GT).Unlocked, "GT starts locked");
        var parcel = new List<string>();
        // the ORIGINAL Tutorial.BuyVan ran on the host: moneyAm -= 25000, VanBought queued; the postfix syncs the unlock state
        shop.Money -= VehicleShop.Price; parcel.Add("VanBought");
        Expect(HostSync(wh, 0, parcel) && R(wh, GT).Unlocked, "fleet unlocked at once from the queued key (no save needed)");
        Expect(VehicleEntryChanged(wh, GT), "registry delta for both players");
        Expect(R(wh, GT).Holder == 2, "holder re-established at once: Player 2 is in the hideout -> simulates the new car there");
        Expect(!HostSync(wh, 0, parcel), "idempotent");
        int bal; string why;
        Expect(VehicleShop.Decide(wh.Fleet, GT, AutoShop, shop, out bal, out why) == ProposalCode.Stale && why == "already owned", "a second host purchase is refused by the host prefix (already owned, no charge)");
        Expect(shop.Money == 5000 && shop.Charges == 0, "one charge (the original's own)");
    }

    static void P2Buys()
    {
        FakeShop shop; var wh = Host(Madison, AutoShop, false, out shop, Madison);
        Expect(Buy(wh) == ProposalCode.Accepted, "Player 2 at the Auto Shop with 30000: accepted");
        Expect(shop.Charges == 1 && shop.Money == 30000 - VehicleShop.Price, "charged once on the host (shared balance)");
        Expect(shop.Parcel.Contains("VanBought"), "the original unlock key is queued on the HOST (written by its next checkpoint)");
        Expect(R(wh, GT).Unlocked && VehicleEntryChanged(wh, GT), "fleet unlocked and published");
        Expect(wh.Purchases == 1 && wh.LastNote.Contains("buy car#" + GT) && wh.LastNote.Contains("Accepted"), "logged decision: " + wh.LastNote);
        Expect(R(wh, GT).Region == Hideout && R(wh, GT).Holder == 0, "the car stays in the hideout (original: shown at secondCarHideoutPos); nobody there -> no holder yet");
        Expect(VehicleShop.Price == 25000 && VehicleShop.ShopRegion == 3 && VehicleShop.Vehicle == GT, "original price / Auto Shop / Shadowhawk");
    }

    static void Simultaneous()
    {
        FakeShop shop; var wh = Host(AutoShop, AutoShop, false, out shop);
        uint id = ++_pid;
        Expect(Buy(wh, id) == ProposalCode.Accepted, "first request");
        Expect(Buy(wh, id) == ProposalCode.Accepted && shop.Charges == 1, "retransmission: cached answer, not applied again");
        Expect(Buy(wh) == ProposalCode.Stale && shop.Charges == 1, "second request: already owned (Stale, never Unchanged = no false success)");
        // host click in the same frame: client proposals are applied in LateUpdate -> the host's original runs first
        FakeShop s2; var w2 = Host(AutoShop, AutoShop, false, out s2);
        var parcel = new List<string>(); s2.Money -= VehicleShop.Price; parcel.Add("VanBought"); HostSync(w2, 0, parcel);
        Expect(Buy(w2) == ProposalCode.Stale && s2.Charges == 0 && s2.Money == 5000, "host bought first: the client request is refused, one charge in total");
        // client first, then the host's sign click: the host prefix refuses (record already unlocked)
        int bal; string why;
        FakeShop s3; var w3 = Host(AutoShop, AutoShop, false, out s3);
        Expect(Buy(w3) == ProposalCode.Accepted && VehicleShop.Decide(w3.Fleet, GT, AutoShop, s3, out bal, out why) == ProposalCode.Stale && s3.Charges == 1, "client first: the host's original purchase is skipped");
    }

    static void Invalid()
    {
        FakeShop shop; WorldHost wh;
        wh = Host(Madison, AutoShop, false, out shop, Madison); shop.Money = 24999;
        Expect(Buy(wh) == ProposalCode.Stale && shop.Charges == 0 && !R(wh, GT).Unlocked, "24999 < 25000: refused, nothing charged, still locked");
        wh = Host(AutoShop, Madison, false, out shop);
        Expect(Buy(wh) == ProposalCode.NotAuthorized && shop.Charges == 0, "Player 2 not at the Auto Shop (the sign exists only there)");
        wh = Host(Madison, AutoShop, false, out shop, Madison); shop.Garage = false;
        Expect(Buy(wh) == ProposalCode.NotAuthorized && shop.Charges == 0, "no garage: the original sign is destroyed (DestroyIfPref garage == 0)");
        wh = Host(Madison, AutoShop, false, out shop, Madison); shop.Ready = false;
        Expect(Buy(wh) == ProposalCode.Busy && shop.Charges == 0 && !R(wh, GT).Unlocked, "host scene loading (no live balance): Busy, retry possible");
        wh = Host(Madison, AutoShop, false, out shop, Madison); wh.Shop = null;
        Expect(Buy(wh) == ProposalCode.Busy, "host plugin shop not wired: Busy");
        wh = Host(Madison, AutoShop, false, out shop, Madison);
        Expect(Buy(wh, 0, Ranger) == ProposalCode.NotAuthorized && shop.Charges == 0, "the Ranger is not for sale");
        Expect(Buy(wh, 0, 77) == ProposalCode.Invalid, "unknown vehicle");
        Expect(Buy(wh, 0, 0, new byte[] { 1, 1 }) == ProposalCode.Invalid && Buy(wh, 0, 0, new byte[] { 0 }) == ProposalCode.Invalid, "malformed value");
        Expect(shop.Charges == 0 && !R(wh, GT).Unlocked && wh.PurchaseRefusals >= 2, "nothing changed by refused requests");
    }

    static void Persistence()
    {
        FakeShop shop; var wh = Host(AutoShop, Hideout, false, out shop);
        Expect(Buy(wh) == ProposalCode.NotAuthorized, "(Player 2 in the hideout: refused)");
        wh.SetPlayerRegion(2, AutoShop, 1);
        Expect(Buy(wh) == ProposalCode.Accepted, "bought");
        Expect(!wh.SetUnlocked(GT, true), "SetUnlocked idempotent");
        // host plugin at the next scene bind: key not yet saved, parcel list of the new GameManager empty -> caught-screen
        // checkpoint reload: the purchase (money + key) is gone from the host's memory -> the fleet follows
        Expect(HostSync(wh, 0, new List<string>()) && !R(wh, GT).Unlocked, "checkpoint reload without a save: unlock rolled back with the money");
        Expect(HostSync(wh, 1, new List<string>()) && R(wh, GT).Unlocked, "travel saved first (VanBought = 1): unlocked at the next bind");
        var fs = new FleetSave { Profile = 1, Story = 5, DayIndex = 3, SavedUtcTicks = 1 };
        fs.Cars.Add(new FleetSaveCar { Id = GT, Kind = 1, Unlocked = true, Region = Hideout, Park = new ParkRecord { Region = Hideout, Fuel = -1f } });
        FleetSave back; string err;
        Expect(FleetSave.TryParse(fs.Serialize(), out back, out err) && back.Cars[0].Unlocked, "fleet file keeps the flag (" + err + ")");
        var e = wh.Reg.Get(StateScope.Session, GT, WorldKeys.Vehicle);
        VehicleRecord rr; Expect(e != null && VehicleRecord.TryParse(e.Value, out rr) && rr.Unlocked, "registry entry (snapshot for a reconnecting client) carries the unlock");
        var vs = Src("Plugin", "VehicleShopSync.cs");
        Expect(Body(vs, "void SyncHostUnlock(string why)").Contains("bool unlocked = Pref(pref) >= 1 || queued;"), "host unlock state = saved key or queued original purchase");
        var v = Src("Plugin", "VehicleSync.cs");
        Expect(v.Contains("if (_host) { EnsureHostFleet(vm); SyncHostUnlock(\"scene bound\"); }"), "re-derived at every scene bind (not only once per profile)");
    }

    static void HideoutAfterBuy()
    {
        FakeShop shop; var wh = Host(AutoShop, Hideout, false, out shop);
        Expect(Pres(wh, GT, Hideout, 2) == CarPresentation.Hidden, "before: locked car hidden in the hideout (never shown as owned)");
        wh.SetPlayerRegion(2, AutoShop, 1); Buy(wh); wh.SetPlayerRegion(2, Hideout, 2);
        Expect(R(wh, GT).Holder == 2, "Player 2 (alone in the hideout) holds the new car");
        Expect(Pres(wh, GT, Hideout, 2) == CarPresentation.Real, "Player 2 sees + drives the Shadowhawk in the hideout during this session");
        wh.SetPlayerRegion(1, Hideout, 3);
        Expect(Pres(wh, GT, Hideout, 1) == CarPresentation.ParkedOther, "Player 1 arriving: observed copy of the same car (one simulation)");
        Expect(Pres(wh, Ranger, Hideout, 1) == CarPresentation.AwayProxy && Pres(wh, Ranger, Hideout, 2) == CarPresentation.AwayProxy, "Ranger still at the Auto Shop: hideout travel proxy (unchanged M4.2.1)");
        // host bought, host returns alone to the hideout (Ranger elsewhere)
        FakeShop s2; var w2 = Host(AutoShop, Madison, false, out s2);
        var parcel = new List<string> { "VanBought" }; HostSync(w2, 0, parcel); w2.SetPlayerRegion(1, Hideout, 2);
        Expect(R(w2, GT).Holder == 1 && Pres(w2, GT, Hideout, 1) == CarPresentation.Real && Pres(w2, GT, Madison, 2) == CarPresentation.Hidden, "host: Real in the hideout; Player 2 elsewhere sees nothing");
        var v = Src("Plugin", "VehicleSync.cs");
        Expect(Body(v, "void Activate(UnityEngine.Object gm, LocalCar lc, int region)").Contains("else if (!lc.Placed && region == TravelRules.Hideout) PlaceNewHideoutCar(gm, lc);"), "a car the original did not place goes to the original hideout spot");
        Expect(Body(v, "void Activate(UnityEngine.Object gm, LocalCar lc, int region)").Contains("if (gm != null && !keepCurrent)"), "GameManager.Car is not stolen from this instance's own car (like InitiateMainCar's second car)");
        Expect(Body(v, "void ActivateProxyObject(UnityEngine.Object gm, LocalCar lc, int region)").Contains("PlaceNewHideoutCar(gm, lc)"), "observed / proxy copies of an unplaced car too");
        var vs = Src("Plugin", "VehicleShopSync.cs");
        var place = Body(vs, "void PlaceNewHideoutCar(object gm, LocalCar lc)");
        Expect(place.Contains("_vmSecondPos(vm)") && place.Contains("_gmHideoutCar(gm)") && place.Contains("Overlaps(lc, sp.position)") && place.Contains("< 3.5f"), "original spots (secondCarHideoutPos, SpawnHideoutCar), clear of cars and the player");
    }

    static void Restart()
    {
        // host restart: EnsureHostFleet registers from the saved key; FleetSave.Unlocked is not trusted over the save
        var a = new WorldHost(12); a.Fleet.Register(GT, 1, true, a.Outbox);
        Expect(R(a, GT).Unlocked, "saved VanBought = 1 -> unlocked after the restart");
        var b = new WorldHost(13); b.Fleet.Register(GT, 1, false, b.Outbox);
        Expect(!R(b, GT).Unlocked, "purchase never saved by the host -> locked (the money was not saved either)");
        var v = Src("Plugin", "VehicleSync.cs");
        Expect(v.Contains("bool unlocked = kv.Key != \"van\" || (vanPref != null && Pref(vanPref) == 1);") && v.Contains("fleet.Register(id, (byte)nr, unlocked, outbox)"), "restart registration from the host save key (unchanged)");
        // reconnect: the client view is the host registry; the client sandbox key is never consulted for presentation
        FakeShop shop; var wh = Host(AutoShop, AutoShop, false, out shop); Buy(wh);
        var snap = wh.Reg.Get(StateScope.Session, GT, WorldKeys.Vehicle); VehicleRecord r;
        Expect(snap != null && VehicleRecord.TryParse(snap.Value, out r) && r.Unlocked, "rejoining client receives the unlock in the registry snapshot");
        var vs = Src("Plugin", "VehicleShopSync.cs");
        Expect(Body(vs, "bool BuyResult(uint id, ProposalCode code)").Contains("if (!had && parcel != null) parcel.Remove(pref);"), "the client sandbox never keeps a purchase key");
    }

    static void ProxyAppears()
    {
        FakeShop shop; var wh = Host(AutoShop, AutoShop, true, out shop);
        wh.Fleet.Claim(Ranger, 1, AutoShop, wh.Outbox);
        Expect(PresentationRules.AutoShopProxyCar(All(wh), AutoShop, 0) == VehicleIds.None, "the real Ranger is here: no proxy");
        Expect(Pres(wh, Ranger, AutoShop, 2) == CarPresentation.RemoteVisual, "Player 2 sees the real car driven by Player 1");
        var g = wh.HostTravel(++_pid, new TravelRequest { Dest = Hillville, Car = Ranger, Pose = new ParkRecord { Region = AutoShop, Fuel = -1 } }, 2);
        Expect(g.Outcome == TravelOutcome.Carry, "Player 1 takes the real Ranger with the original travel: " + g.Outcome);
        wh.SetPlayerRegion(1, Hillville, 3);
        Expect(R(wh, Ranger).Region == Hillville, "the car is in Hillville now");
        ushort px = PresentationRules.AutoShopProxyCar(All(wh), AutoShop, Ranger);
        Expect(px == Ranger, "the Ranger (last seen here) becomes the Auto Shop proxy, not the Shadowhawk in the hideout");
        var pres = Pres(wh, Ranger, AutoShop, 2, Ranger);
        Expect(pres == CarPresentation.AwayProxy, "Player 2: travel-only proxy, was Hidden before V1.2: " + pres);
        Expect(Pres(wh, GT, AutoShop, 2, Ranger) == CarPresentation.Hidden, "only ONE proxy");
        Expect(PresentationRules.Enter(pres, R(wh, Ranger), 2, true) == EnterAction.TravelMenu && PresentationRules.EnterOpensTravelMenu(pres), "E opens the original travel menu");
        Expect(!PresentationRules.AllowsDriving(pres) && !PresentationRules.AllowsStorage(pres) && !PresentationRules.IsObserved(pres), "no driving, storage, seats or snapshots");
        Expect(PresentationRules.Collision(pres, R(wh, Ranger), true) == CollisionMode.Solid, "solid, stationary (EnterProxy: FreezeAll; ghost while the player stands inside)");
        Expect(Pres(wh, Ranger, Hillville, 1, Ranger) == CarPresentation.Real, "Player 1 drives the one real Ranger in Hillville");
        // Player 2 alone at the Auto Shop without any car (arrived on foot): proxy at the original car spawn
        FakeShop s2; var w2 = Host(Madison, AutoShop, false, out s2, Madison);
        Expect(PresentationRules.AutoShopProxyCar(All(w2), AutoShop, 0) == Ranger && Pres(w2, Ranger, AutoShop, 2) == CarPresentation.AwayProxy, "no car seen here: lowest owned car (locked GT never)");
        var vs = Src("Plugin", "VehicleShopSync.cs");
        var spot = Body(vs, "void PlaceShopProxy(object gm, LocalCar lc)");
        Expect(spot.Contains("_gmGarageCar(gm)") && spot.Contains("bool keep = lc.Id == _shopSeen"), "spot: last presented pose here, else GameManager.SpawnGarageCar (original Auto Shop car spawn)");
    }

    static void ProxyTravel()
    {
        FakeShop shop; var wh = Host(Hillville, AutoShop, true, out shop, Hillville);
        wh.Fleet.Claim(Ranger, 1, Hillville, wh.Outbox);
        Expect(Pres(wh, Ranger, AutoShop, 2) == CarPresentation.AwayProxy, "proxy for Player 2");
        var before = R(wh, Ranger);
        uint rev; int sr; float sh;
        // VehicleSync.Candidate: the proxy is not Mine / not sat in -> the request carries no car (on foot)
        var p = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = Hideout, Car = VehicleIds.None, Pose = new ParkRecord { Region = AutoShop, Fuel = -1 } }.ToBytes() };
        Expect(wh.HandleProposal("cli", 2, ++_pid, p, 4, out rev, out sr, out sh) == ProposalCode.Accepted, "independent travel accepted");
        TravelGrant g; var ge = wh.Reg.Get(StateScope.Player, 2, WorldKeys.TravelGrant);
        Expect(ge != null && TravelGrant.TryParse(ge.Value, out g) && g.Outcome != TravelOutcome.Carry && g.Car != Ranger, "on foot: the real Ranger is never brought along");
        var after = R(wh, Ranger);
        Expect(after.Region == before.Region && after.Holder == before.Holder && after.Driver == before.Driver && !after.Reserved, "the real car's region / holder / driver untouched by the proxy");
        // a request that names the proxied car anyway (forged) cannot carry it: not in the traveller's region
        FakeShop s2; var w2 = Host(Hillville, AutoShop, true, out s2, Hillville);
        var q = new StateEntry { Scope = StateScope.Player, ScopeArg = 2, Key = WorldKeys.Travel, Value = new TravelRequest { Dest = Madison, Car = Ranger, Pose = new ParkRecord { Region = AutoShop, Fuel = -1 } }.ToBytes() };
        w2.HandleProposal("cli", 2, ++_pid, q, 4, out rev, out sr, out sh);
        Expect(R(w2, Ranger).Region == Hillville && !R(w2, Ranger).Reserved, "forged car in the request: the car elsewhere is not moved");
        var v = Src("Plugin", "VehicleSync.cs");
        Expect(Body(v, "void OpenTravelMenu(LocalCar lc)").Contains("_showTown(ps, 0f)") && Body(v, "public bool AllowEnterVehicle(object car)").Contains("case EnterAction.TravelMenu: OpenTravelMenu(lc); return false;"), "proxy E -> original ShowTownSelection (same path as the hideout proxy)");
        Expect(Body(v, "void Candidate(object gm, int origin, out ushort car, out ParkRecord pose)").Contains("lc.Mine && lc.Active"), "a proxy (not Mine) is never the travel candidate");
    }

    static void Roles()
    {
        // client drove the car away, host stays
        FakeShop shop; var wh = Host(Madison, AutoShop, true, out shop);   // Player 2 alone with the Ranger -> holder 2
        wh.SetPlayerRegion(1, AutoShop, 0.5);                               // host joins: the present holder is kept
        Expect(R(wh, Ranger).Holder == 2 && Pres(wh, Ranger, AutoShop, 1) == CarPresentation.RemoteVisual && Pres(wh, Ranger, AutoShop, 2) == CarPresentation.Real, "client simulates the Ranger: " + R(wh, Ranger));
        wh.SetPlayerRegion(2, Madison, 1); wh.Fleet.Place(Ranger, Madison, wh.Outbox); wh.FleetInitialized(1);
        Expect(Pres(wh, Ranger, AutoShop, 1, Ranger) == CarPresentation.AwayProxy, "host stays: proxy on the host");
        Expect(Pres(wh, Ranger, Madison, 2, Ranger) == CarPresentation.Real, "client keeps the real car");
        // host drove away, client stays (the original scenario)
        FakeShop s2; var w2 = Host(AutoShop, AutoShop, true, out s2);
        w2.SetPlayerRegion(1, Hillville, 1); w2.Fleet.Place(Ranger, Hillville, w2.Outbox); w2.FleetInitialized(1);
        Expect(Pres(w2, Ranger, AutoShop, 2, Ranger) == CarPresentation.AwayProxy && Pres(w2, Ranger, Hillville, 1, Ranger) == CarPresentation.Real, "client stays: proxy on the client");
        Expect(PresentationRules.AutoShopProxyCar(All(w2), AutoShop, Ranger) == PresentationRules.AutoShopProxyCar(All(w2), AutoShop, Ranger), "pure, slot-independent choice");
    }

    static void Returning()
    {
        FakeShop shop; var wh = Host(Hillville, AutoShop, true, out shop, Hillville);
        Expect(Pres(wh, Ranger, AutoShop, 2) == CarPresentation.AwayProxy, "proxy while the Ranger is in Hillville");
        // the same car comes back with Player 1
        wh.SetPlayerRegion(1, AutoShop, 2); wh.Fleet.Place(Ranger, AutoShop, wh.Outbox); wh.FleetInitialized(2);
        Expect(PresentationRules.AutoShopProxyCar(All(wh), AutoShop, Ranger) == VehicleIds.None, "real car here: no proxy");
        var p2 = Pres(wh, Ranger, AutoShop, 2); var p1 = Pres(wh, Ranger, AutoShop, 1);
        Expect((p1 == CarPresentation.Real) != (p2 == CarPresentation.Real) && (p1 == CarPresentation.RemoteVisual || p2 == CarPresentation.RemoteVisual), "proxy replaced by the real car in place (one Real, one observed): " + p1 + "/" + p2);
        // another car arrives while the Ranger proxy is shown
        FakeShop s2; var w2 = Host(Hillville, AutoShop, true, out s2, Hillville);
        Expect(Pres(w2, Ranger, AutoShop, 2) == CarPresentation.AwayProxy, "Ranger proxy");
        w2.SetPlayerRegion(1, AutoShop, 2); w2.Fleet.Place(GT, AutoShop, w2.Outbox); w2.FleetInitialized(2);
        Expect(Pres(w2, Ranger, AutoShop, 2) == CarPresentation.Hidden && Pres(w2, GT, AutoShop, 2) != CarPresentation.AwayProxy, "Shadowhawk arrives: Ranger proxy removed, the real Shadowhawk shown");
        // preference: the car last seen here stays the proxy; unknown preference -> lowest id
        FakeShop s3; var w3 = Host(Hillville, AutoShop, true, out s3, Hillville);
        w3.Fleet.Place(GT, Madison, w3.Outbox);
        Expect(PresentationRules.AutoShopProxyCar(All(w3), AutoShop, GT) == GT && PresentationRules.AutoShopProxyCar(All(w3), AutoShop, 0) == Ranger, "preferred car kept, else the lowest id");
        Expect(PresentationRules.AutoShopProxyCar(All(w3), Hideout, GT) == VehicleIds.None && PresentationRules.AutoShopProxyCar(All(w3), Madison, GT) == VehicleIds.None, "only the Auto Shop (car-only travel region)");
        var v = Src("Plugin", "VehicleSync.cs");
        Expect(Body(v, "void Reconcile(UnityEngine.Object gm, UnityEngine.Object ps, int region)").Contains("if (lc.IsProxy && !lc.Observed) ExitProxy(lc);"), "proxy -> observed in place (unchanged M5.0 path)");
    }

    static void NoDuplicates()
    {
        int[] regions = { Hideout, 1, AutoShop, Hillville, Madison };
        FakeShop shop; var wh = Host(Hillville, AutoShop, true, out shop, Hillville);
        wh.Fleet.Place(GT, Madison, wh.Outbox); wh.FleetInitialized(1);
        foreach (var id in new[] { Ranger, GT })
        {
            int real = 0;
            foreach (var reg in regions) for (byte s = 1; s <= 2; s++) if (Pres(wh, id, reg, s, Ranger) == CarPresentation.Real) real++;
            Expect(real <= 1, "car " + id + " has at most one Real presentation over all regions/slots (" + real + ")");
        }
        Expect(Pres(wh, Ranger, AutoShop, 2, Ranger) == CarPresentation.AwayProxy && R(wh, Ranger).Holder == 1, "the proxy never changes the holder (Player 1 keeps simulating it)");
        FakeShop s2; var w2 = Host(AutoShop, AutoShop, false, out s2);
        Buy(w2); Buy(w2); Buy(w2);
        Expect(s2.Charges == 1 && s2.Money == 5000, "three clicks, one charge");
        var c = Src("Plugin", "HeavyCargoSync.cs");
        Expect(c.Contains("bool show = !_seenProxy;") && c.Contains("if (show) foreach (var e in _entries) _refs.Add(e.Id);") && c.Contains("if (show) foreach (var e in _entries)"), "no cargo copies on a travel proxy");
        var vs = Src("Plugin", "VehicleShopSync.cs");
        Expect(Body(vs, "internal bool IsTravelProxy(ushort id)").Contains("lc.IsProxy && !lc.Observed"), "travel proxy = AwayProxy only (observed cars keep their cargo)");
        Expect(Body(Src("Plugin", "VehicleSync.cs"), "void EnterProxy(LocalCar lc, UnityEngine.Object ps)").Contains("foreach (var col in lc.Blocked) if (col != null) col.enabled = false;"), "proxy storage / van door / cargo triggers off (unchanged)");
    }

    static void Regressions()
    {
        // hideout + other regions: Decide without the proxy flag is exactly the pre-V1.2 rule
        var recs = new List<VehicleRecord>();
        foreach (int reg in new[] { Hideout, AutoShop, Madison, Regions.None })
            foreach (byte h in new byte[] { 0, 1, 2 })
                foreach (bool u in new[] { true, false })
                    recs.Add(new VehicleRecord { Kind = 0, Region = reg, Holder = h, Flags = u ? VehicleFlags.Unlocked : VehicleFlags.None });
        foreach (var r in recs)
            foreach (int loc in new[] { Hideout, 1, AutoShop, Hillville, Madison, 92, Regions.None })
                foreach (byte s in new byte[] { 1, 2 })
                {
                    Expect(PresentationRules.Decide(r, loc, s) == PresentationRules.Decide(r, loc, s, false), "old overload = no proxy flag");
                    if (loc != AutoShop) Expect(PresentationRules.Decide(r, loc, s, true) == PresentationRules.Decide(r, loc, s, false), "the proxy flag only matters in the Auto Shop (" + loc + ")");
                    if (r.Region == loc) Expect(PresentationRules.Decide(r, loc, s, true) == PresentationRules.Decide(r, loc, s, false), "a car in this region is never a proxy");
                    if (!r.Unlocked) Expect(PresentationRules.Decide(r, loc, s, true) != CarPresentation.AwayProxy || loc == Hideout && r.Unlocked, "a locked car is never a proxy");
                }
        var v = Src("Plugin", "VehicleSync.cs");
        Expect(Body(v, "void DoorFrame(int region)").Contains("if (!lc.Active || !(lc.Observed || (lc.Mine && !lc.IsProxy))) continue;"), "door / tailgate sync (vdoor, original Door components) still skips travel proxies");
        Expect(Body(v, "void Reconcile(UnityEngine.Object gm, UnityEngine.Object ps, int region)").Contains("if (!was && region == TravelRules.Hideout) KeepClearOfOtherCars(gm, lc);"), "hideout proxy spot logic unchanged");
        var w = Src("Plugin", "WalletSync.cs");
        Expect(w.Contains("if (w.Policy == MoneyPolicy.HostTxn && HostTxnHooks.Claims(w.Type + \".\" + w.Method)) { n++; continue; }") && Src("Plugin", "VehicleSync.cs").Contains("if (TravelHooks.BuyVanPatched) HostTxnHooks.Claim(\"Tutorial.BuyVan\");"), "Tutorial.BuyVan left to the vbuy hook only when it is installed (claim); otherwise refused like before");
        bool found = false; foreach (var m in WalletPolicy.Writers) if (m.Type == "Tutorial" && m.Method == "BuyVan") { found = true; Expect(m.Policy == MoneyPolicy.HostTxn, "BuyVan = host transaction"); }
        Expect(found, "BuyVan classified");
        var t = Src("Plugin", "TravelHooks.cs");
        Expect(Body(t, "static bool BuyVanPrefix(object __instance)").Contains("return false; }"), "BuyVan hook fails closed (an exception never charges without the host)");
        var vs = Src("Plugin", "VehicleShopSync.cs");
        Expect(Body(vs, "public void AfterBuyVan(object tutorial)").Contains("if (!_buyRan) return;"), "postfix acts only when the original really ran");
        var br = Body(vs, "bool BuyResult(uint id, ProposalCode code)");
        Expect(br.Contains("finally") && br.Contains("if (_psMoney(ps) != money) _setPsMoney(ps, money);"), "client: the original's local money change is undone even if the original throws");
        foreach (var bad in new[] { "OnGUI", "GUI.", "Time.timeScale =", "FindObjectsOfType", "SpawnMessage(" })
            Expect(!vs.Contains(bad), "VehicleShopSync contains no '" + bad + "' (no mod UI text, no scene-wide search)");
        Expect(Count(vs, "GameObject.Find(") == 1 && Body(vs, "void HideBuySign(object gm, bool once)").Contains("GameObject.Find(\"BUY VAN OBJ\")"), "the only lookup = the original sign name (Tutorial.BuyVan uses the same)");
        Expect(ProtocolInfo.ProtocolVersion == 32 && ProtocolInfo.ModVersion == "0.21.0", "protocol 32 / mod 0.21.0");
        Expect(WorldKeys.VehicleBuy == "vbuy", "key");
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
    static int Count(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }
    static string Body(string src, string signature)
    {
        int a = src.IndexOf(signature, StringComparison.Ordinal); if (a < 0) throw new Exception("missing " + signature);
        int open = src.IndexOf('{', a), depth = 0;
        for (int i = open; i < src.Length; i++) { if (src[i] == '{') depth++; else if (src[i] == '}' && --depth == 0) return src.Substring(a, i - a + 1); }
        throw new Exception("unbalanced " + signature);
    }
}
