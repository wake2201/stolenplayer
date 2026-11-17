// V1.4 offline tests: hideout parking layout of the two original cars (HideoutLayout.Plan, src/Shared/Travel.cs) driven by a
// two-PC model of the original hideout load (VehiclesManager.InitiateMainCar + GameManager.Start: this save's CurrentCar at
// SpawnHideoutCar, the other car at secondCarHideoutPos), the host's fleet records (PresentationRules.Decide) and the glue
// rules of VehicleHideoutLayout.cs (roles Fixed / Arrange / Proxy), plus source scans of the Unity glue.
// In-game behaviour (visible cars, renderers, travel interaction, no relocation while driving) is manual two-PC QA
// (docs/TESTING.md §43).
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class V14Tests
{
    static int _passed, _failed;
    const int Hideout = 0, AutoShop = 3, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);
    // scene scan level1 (MainScene MANAGERS/GAME MANAGER): SpawnCarHideout / SecondSpot Car Hideout Position
    static readonly Vec3 MainSpot = new Vec3(-97.72f, 76.43f, -169.62f), SecondSpot = new Vec3(-109.80f, 76.41f, -168.81f);
    static readonly Vec3 SceneDefault = new Vec3(44.28f, 0f, 249.17f);   // where an object InitiateMainCar left inactive stands

    static int Main()
    {
        Run("01 reported sequence: Ranger parked at the Auto Shop, Player 2 brings the Shadowhawk home -> separated, same spots on both PCs", Reported);
        Run("02 root cause reproduced without the layout: per-PC CurrentCar puts the proxy under the observed car on the host", RootCause);
        Run("03 roles reversed: the host brings the Shadowhawk home, Player 2's Ranger is the proxy", Reversed);
        Run("04 both cars real: each PC's own car at its main spot originally -> Ranger main, Shadowhawk second on both PCs", BothReal);
        Run("05 arrival orders: Player 1 first / Player 2 first / both together give the same final layout", ArrivalOrders);
        Run("06 transitions: Real -> AwayProxy, AwayProxy -> Real, AwayProxy -> ParkedOther, Real -> ParkedOther (handoff in place)", Transitions);
        Run("07 committed hideout poses are preserved across a reload (renovation / reconnect / restart); conflicting ones fall back to a spot", Persistence);
        Run("08 a driven or moving car is never repositioned; a proxy reacts only to settled cars", NeverWhileDriven);
        Run("09 determinism: input order, dictionary order and which PC loads first do not change the layout", Determinism);
        Run("10 eligibility: locked / hidden cars are not presented; only one presentation per identity; proxy never Real", Eligibility);
        Run("11 glue: layout after every reconcile, no KeepClear leftovers, no park / record writes for proxies, diagnostics", Glue);
        Run("12 regressions: Auto Shop proxy, other regions, presentation rules unchanged; version pins", Regressions);
        Run("13 a car the driver sits in (stopped) is an obstacle on both PCs and is never rearranged after parking", SeatedStopped);
        Run("14 V1.4.1: first real car home takes the main spot (Ranger or Shadowhawk); the other proxy yields; later arrival second", FirstArrivalMain);
        Run("15 V1.4.1 medkit: Player-2 use is a host transaction, applied exactly once (retransmission cached, second use Stale), state committed", MedkitOnce);
        Run("16 V1.4.1 medkit: travel proxy / other region refused, unknown car / malformed Invalid, host loading Busy, already used Stale", MedkitRefusals);
        Run("17 V1.4.1 medkit: passenger seat reachability (right door open or engine off), seat actions, state codec", MedkitSeat);
        Run("18 V1.4.1 medkit glue: original sequence kept, client proposal, proxy guard + prompt, removal on commit only, no per-frame search", MedkitGlue);
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

    // ------------------------------------------------------------------------------------------------ fleet (host records)
    sealed class Fleet
    {
        public readonly Dictionary<ushort, VehicleRecord> Rec = new Dictionary<ushort, VehicleRecord>();
        public readonly Dictionary<ushort, ParkRecord> Park = new Dictionary<ushort, ParkRecord>();
        public Fleet(bool gtUnlocked)
        {
            Rec[Ranger] = new VehicleRecord { Kind = 0, Flags = VehicleFlags.Unlocked, Region = Hideout };
            Rec[GT] = new VehicleRecord { Kind = 1, Flags = gtUnlocked ? VehicleFlags.Unlocked : VehicleFlags.None, Region = Hideout };
        }
        public void Set(ushort id, int region, byte holder) { var r = Rec[id]; r.Region = region; r.Holder = holder; Rec[id] = r; }
        public void ParkAt(ushort id, int region, Vec3 p) { Park[id] = new ParkRecord { Region = region, HasPose = true, Pos = p, Rot = new Quat(0, 0, 0, 1), Fuel = 50 }; }
    }

    // ------------------------------------------------------------------------------------------------ one PC's hideout scene
    sealed class Pc
    {
        public byte Slot; public int CurrentCar; public bool VanPref; public Fleet F;
        public readonly Dictionary<ushort, Vec3> Pos = new Dictionary<ushort, Vec3>();
        public readonly Dictionary<ushort, CarPresentation> Pres = new Dictionary<ushort, CarPresentation>();
        public readonly HashSet<ushort> Active = new HashSet<ushort>(), Arranged = new HashSet<ushort>(), Followed = new HashSet<ushort>(), Driven = new HashSet<ushort>(), Seated = new HashSet<ushort>();
        public bool Layout = true; public int Moves;

        // VehiclesManager.InitiateMainCar + GameManager.Start (hideout), then the mod's first reconcile + layout
        public void Load()
        {
            Pos.Clear(); Pres.Clear(); Active.Clear(); Arranged.Clear(); Followed.Clear(); Driven.Clear(); Seated.Clear();
            ushort cur = CurrentCar == 1 ? GT : Ranger, other = cur == Ranger ? GT : Ranger;
            Active.Add(cur); Pos[cur] = MainSpot;
            if (CurrentCar == 1 || VanPref) { Active.Add(other); Pos[other] = SecondSpot; }
            if (!Pos.ContainsKey(other)) Pos[other] = SceneDefault;
            Reconcile();
        }

        public void Reconcile()
        {
            foreach (var id in new[] { Ranger, GT })
            {
                var r = F.Rec[id];
                var was = Pres.ContainsKey(id) ? Pres[id] : CarPresentation.Hidden;
                var p = PresentationRules.Decide(r, Hideout, Slot);
                if (p == CarPresentation.Hidden) { Active.Remove(id); Pres[id] = p; continue; }
                if (!Active.Contains(id))
                {
                    Active.Add(id);
                    ParkRecord pk;
                    bool pose = F.Park.TryGetValue(id, out pk) && pk.HasPose && pk.Region == Hideout;
                    if (p == CarPresentation.Real && pose) { Pos[id] = pk.Pos; Arranged.Remove(id); }   // Activate at its parking pose
                    else PlaceNew(id);                                                                    // V1.2 PlaceNewHideoutCar
                }
                if (p == CarPresentation.Real && (was == CarPresentation.ParkedOther || was == CarPresentation.AwayProxy)) Arranged.Add(id);   // handoff in place
                if (p == CarPresentation.ParkedOther && was != CarPresentation.ParkedOther)
                {
                    ParkRecord pk;
                    if (F.Park.TryGetValue(id, out pk) && pk.HasPose && pk.Region == Hideout) Pos[id] = pk.Pos;   // EnterObserved -> ApplyParkPose
                    Followed.Remove(id);
                }
                Pres[id] = p;
            }
            if (Layout) RunLayout();
        }

        void PlaceNew(ushort id)
        {
            ushort o = id == Ranger ? GT : Ranger;
            bool busy = Active.Contains(o) && Vec3.HorizontalDistance(Pos[o], SecondSpot) < HideoutLayout.Clearance;
            Pos[id] = busy ? MainSpot : SecondSpot;
        }

        public void RunLayout()
        {
            var cars = new List<LayoutCar>(); bool hold = false;
            foreach (var id in new[] { Ranger, GT })
            {
                if (!Active.Contains(id)) continue;
                if (Driven.Contains(id) && (Pres[id] == CarPresentation.Real || Pres[id] == CarPresentation.ParkedOther)) hold = true;
                var p = Pres[id];
                ParkRecord pk; bool pose = F.Park.TryGetValue(id, out pk) && pk.HasPose && pk.Region == Hideout;
                LayoutRole role;
                if (p == CarPresentation.Real) { if (Driven.Contains(id) || Seated.Contains(id)) Arranged.Add(id); if (Driven.Contains(id)) continue; role = Arranged.Contains(id) ? LayoutRole.Fixed : LayoutRole.Arrange; }
                else if (p == CarPresentation.ParkedOther) { if (Driven.Contains(id)) continue; role = Arranged.Contains(id) || Followed.Contains(id) ? LayoutRole.Fixed : LayoutRole.Arrange; }
                else if (p == CarPresentation.AwayProxy) role = LayoutRole.Proxy;
                else continue;
                cars.Add(new LayoutCar { Id = id, Role = role, Pos = Pos[id], HasPose = pose, Pose = pose ? pk.Pos : default(Vec3) });
            }
            var plan = HideoutLayout.Plan(cars, MainSpot, SecondSpot, hold);
            for (int i = 0; i < plan.Length; i++)
            {
                if (cars[i].Role == LayoutRole.Fixed) continue;
                if (plan[i].Moves(cars[i].Pos)) { Pos[cars[i].Id] = plan[i].Target; Moves++; }
                if (cars[i].Role == LayoutRole.Arrange) Arranged.Add(cars[i].Id);
            }
        }

        // the holder's snapshots reach this PC (observed cars follow the real pose)
        public void Follow(Pc holder)
        {
            foreach (var id in new[] { Ranger, GT })
                if (Active.Contains(id) && Pres[id] == CarPresentation.ParkedOther && holder.Pres.ContainsKey(id) && holder.Pres[id] == CarPresentation.Real) { Pos[id] = holder.Pos[id]; Followed.Add(id); }
            if (Layout) RunLayout();
        }

        public bool Shows(ushort id) { return Active.Contains(id) && Pres[id] != CarPresentation.Hidden; }
    }

    // the host commits the hideout pose of every settled real car (VehicleSync ParkPolicy / IngestRemote -> "vpark")
    static void Commit(Pc pc) { foreach (var id in new[] { Ranger, GT }) if (pc.Shows(id) && pc.Pres[id] == CarPresentation.Real && !pc.Driven.Contains(id)) pc.F.ParkAt(id, Hideout, pc.Pos[id]); }
    static void Settle(Pc a, Pc b) { for (int k = 0; k < 3; k++) { a.Follow(b); b.Follow(a); } Commit(a); Commit(b); }

    static void Separated(Pc pc, string who)
    {
        Expect(pc.Shows(Ranger) && pc.Shows(GT), who + ": both cars presented (" + pc.Pres[Ranger] + ", " + pc.Pres[GT] + ")");
        float d = Vec3.HorizontalDistance(pc.Pos[Ranger], pc.Pos[GT]);
        Expect(d >= HideoutLayout.Clearance, who + ": cars separated (" + d.ToString("0.0") + " m)");
    }
    static void Same(Pc a, Pc b, string what)
    {
        foreach (var id in new[] { Ranger, GT })
            Expect(Vec3.HorizontalDistance(a.Pos[id], b.Pos[id]) < 0.01f, what + ": " + (id == Ranger ? "Ranger" : "Shadowhawk") + " at the same place on both PCs (" + a.Pos[id] + " / " + b.Pos[id] + ")");
    }
    static bool At(Pc pc, ushort id, Vec3 p) { return Vec3.HorizontalDistance(pc.Pos[id], p) < 0.01f; }

    // ------------------------------------------------------------------------------------------------ tests
    static void Reported()
    {
        var f = new Fleet(true);
        f.Set(Ranger, AutoShop, 0); f.ParkAt(Ranger, AutoShop, new Vec3(10, 0, 10));   // Ranger parked at the Auto Shop
        f.Set(GT, Hideout, 2);                                                           // Player 2 carried the Shadowhawk home (pose stays Madison's until reported)
        f.Park[GT] = new ParkRecord { Region = Madison, HasPose = true, Pos = new Vec3(470, 47, 189), Rot = new Quat(0, 0, 0, 1), Fuel = 40 };
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };    // host last drove the Ranger
        var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f };    // client sandbox: VanBought 0, CurrentCar 1 (client log: van (locked)(active))
        cli.Load(); host.Load(); Settle(host, cli);
        Expect(cli.Pres[GT] == CarPresentation.Real && cli.Pres[Ranger] == CarPresentation.AwayProxy, "Player 2: Shadowhawk Real, Ranger AwayProxy (client log)");
        Expect(host.Pres[GT] == CarPresentation.ParkedOther && host.Pres[Ranger] == CarPresentation.AwayProxy, "host: Shadowhawk ParkedOther, Ranger AwayProxy (host log)");
        Separated(host, "host"); Separated(cli, "Player 2"); Same(host, cli, "reported sequence");
        Expect(At(host, GT, MainSpot) && At(host, Ranger, SecondSpot) && At(cli, GT, MainSpot) && At(cli, Ranger, SecondSpot), "V1.4.1: the first real car home (Shadowhawk) takes SpawnHideoutCar, the Ranger proxy yields to secondCarHideoutPos on both PCs");
        Expect(f.Rec[Ranger].Region == AutoShop && f.Park[Ranger].Region == AutoShop, "the Ranger's region and park pose are untouched by its proxy");
    }

    static void RootCause()
    {
        var f = new Fleet(true);
        f.Set(Ranger, AutoShop, 0); f.Set(GT, Hideout, 2);
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f, Layout = false };
        var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f, Layout = false };
        cli.Load(); host.Load(); host.Follow(cli);
        Expect(Vec3.HorizontalDistance(host.Pos[Ranger], host.Pos[GT]) < HideoutLayout.Clearance, "W1/V1.2 behaviour: host shows the Shadowhawk on top of the Ranger proxy at the main spot");
        Expect(At(cli, Ranger, SecondSpot) && At(host, Ranger, MainSpot), "and the Ranger proxy stands at different spots on the two PCs (Player 2: second spot, never seen by Player 1)");
    }

    static void Reversed()
    {
        var f = new Fleet(true);
        f.Set(Ranger, Madison, 0); f.ParkAt(Ranger, Madison, new Vec3(470, 47, 189));
        f.Set(GT, Hideout, 1);
        var host = new Pc { Slot = 1, CurrentCar = 1, VanPref = true, F = f };
        var cli = new Pc { Slot = 2, CurrentCar = 0, VanPref = false, F = f };
        host.Load(); cli.Load(); Settle(host, cli);
        Expect(host.Pres[GT] == CarPresentation.Real && cli.Pres[GT] == CarPresentation.ParkedOther && cli.Pres[Ranger] == CarPresentation.AwayProxy, "roles reversed");
        Separated(host, "host"); Separated(cli, "Player 2"); Same(host, cli, "reversed");
    }

    static void BothReal()
    {
        var f = new Fleet(true);
        f.Set(Ranger, Hideout, 1); f.Set(GT, Hideout, 2);   // each player carried their own car home (no hideout poses)
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };
        var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f };
        host.Load(); cli.Load();
        Expect(At(host, Ranger, MainSpot) && At(cli, GT, SecondSpot), "each holder arranges its own car at its identity spot before any snapshot");
        Settle(host, cli);
        Separated(host, "host"); Separated(cli, "Player 2"); Same(host, cli, "two real cars");
        Expect(host.Pres[GT] == CarPresentation.ParkedOther && cli.Pres[Ranger] == CarPresentation.ParkedOther, "each sees the other's car observed");
    }

    static void ArrivalOrders()
    {
        Vec3 r1 = default(Vec3), g1 = default(Vec3);
        for (int order = 0; order < 3; order++)
        {
            var f = new Fleet(true);
            f.Set(Ranger, AutoShop, 0); f.Set(GT, Madison, 2);
            var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };
            var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f };
            if (order == 0)
            {
                host.Load();                                   // Player 1 home first: both cars elsewhere -> two proxies
                Expect(host.Pres[Ranger] == CarPresentation.AwayProxy && host.Pres[GT] == CarPresentation.AwayProxy, "two proxies");
                Separated(host, "host alone");
                f.Set(GT, Hideout, 2); host.Reconcile();     // Player 2 arrives with the Shadowhawk: AwayProxy -> ParkedOther
                cli.Load();
            }
            else if (order == 1) { f.Set(GT, Hideout, 2); cli.Load(); host.Load(); }
            else { f.Set(GT, Hideout, 2); host.Load(); cli.Load(); }
            Settle(host, cli);
            Separated(host, "order " + order + " host"); Separated(cli, "order " + order + " Player 2"); Same(host, cli, "order " + order);
            if (order == 0) { r1 = host.Pos[Ranger]; g1 = host.Pos[GT]; }
            else Expect(Vec3.HorizontalDistance(host.Pos[Ranger], r1) < 0.01f && Vec3.HorizontalDistance(host.Pos[GT], g1) < 0.01f, "order " + order + ": same final layout as order 0");
        }
    }

    static void Transitions()
    {
        // Real -> AwayProxy: Player 1 is home, Player 2 drives the Shadowhawk away (host keeps a proxy of it)
        var f = new Fleet(true);
        f.Set(Ranger, Hideout, 1); f.Set(GT, Hideout, 2);
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };
        var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f };
        host.Load(); cli.Load(); Settle(host, cli);
        host.Pos[Ranger] = new Vec3(-109.0f, 76.4f, -168.5f); f.ParkAt(Ranger, Hideout, host.Pos[Ranger]); host.Reconcile();   // P1 parked the Ranger on the second spot
        f.Set(GT, Madison, 2); host.Reconcile();
        Expect(host.Pres[GT] == CarPresentation.AwayProxy, "Shadowhawk now a proxy on the host");
        Expect(At(host, Ranger, new Vec3(-109.0f, 76.4f, -168.5f)), "the Real Ranger is never moved for a proxy");
        Expect(At(host, GT, MainSpot) && host.Pres[GT] == CarPresentation.AwayProxy, "proxy takes the free original spot (its identity spot is taken)");
        Separated(host, "Real + AwayProxy");
        // AwayProxy -> ParkedOther: the Shadowhawk comes home with Player 2 (no hideout pose yet)
        f.Set(GT, Hideout, 2); host.Reconcile(); cli.Load(); Settle(host, cli);
        Separated(host, "Real + ParkedOther"); Same(host, cli, "car came home");
        // Real -> ParkedOther (handoff in place): Player 2 takes the Ranger over where it stands
        var before = host.Pos[Ranger];
        f.Set(Ranger, Hideout, 2); host.Reconcile(); cli.Reconcile();
        Expect(host.Pres[Ranger] == CarPresentation.ParkedOther && At(host, Ranger, before), "handoff: host's copy observed in place");
        Expect(cli.Pres[Ranger] == CarPresentation.Real && cli.Arranged.Contains(Ranger), "handoff: new holder keeps the pose (never rearranged)");
        // AwayProxy -> Real in place (restart edge): kept where the proxy stood
        var f2 = new Fleet(true); f2.Set(Ranger, AutoShop, 0); f2.Set(GT, Madison, 0);
        var p1 = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f2 }; p1.Load();
        var spot = p1.Pos[GT]; f2.Set(GT, Hideout, 1); p1.Reconcile();
        Expect(p1.Pres[GT] == CarPresentation.Real && At(p1, GT, spot), "AwayProxy -> Real keeps the place");
    }

    static void Persistence()
    {
        var custom = new Vec3(-103.5f, 76.4f, -160.0f);
        var f = new Fleet(true);
        f.Set(Ranger, AutoShop, 0); f.Set(GT, Hideout, 2); f.ParkAt(GT, Hideout, custom);   // Player 2 parked the Shadowhawk in the hideout
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };
        var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f };
        cli.Load(); host.Load(); Settle(host, cli);
        Expect(At(cli, GT, custom) && At(host, GT, custom), "committed pose restored after the original reset it to the main spot (reload / reconnect / restart)");
        Separated(host, "host"); Same(host, cli, "persisted pose");
        // renovation reload of Player 1 only (W1.1): same result, nothing else moves
        int moves = cli.Moves; host.Load(); Settle(host, cli);
        Expect(cli.Moves == moves && Same2(host, cli), "the other PC's reload does not move anything on this PC");
        // a committed pose that collides with a fixed car falls back to a free original spot
        var f2 = new Fleet(true);
        f2.Set(Ranger, Hideout, 1); f2.ParkAt(Ranger, Hideout, SecondSpot);
        f2.Set(GT, Hideout, 2); f2.ParkAt(GT, Hideout, new Vec3(SecondSpot.X + 1, SecondSpot.Y, SecondSpot.Z));
        var a = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f2 }; var b = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f2 };
        a.Load(); b.Load(); Settle(a, b);
        Separated(a, "conflict host"); Separated(b, "conflict Player 2"); Same(a, b, "conflicting poses");
    }
    static bool Same2(Pc a, Pc b) { try { Same(a, b, "x"); return true; } catch { return false; } }

    static void NeverWhileDriven()
    {
        var f = new Fleet(true);
        f.Set(Ranger, AutoShop, 0); f.Set(GT, Hideout, 2);
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };
        var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f };
        cli.Load(); host.Load(); Settle(host, cli);
        Expect(At(cli, GT, MainSpot) && At(host, Ranger, SecondSpot), "start: Shadowhawk main, Ranger proxy second");
        // Player 2 drives the Shadowhawk onto the Ranger proxy's spot (second) and stops there
        cli.Driven.Add(GT); cli.Pos[GT] = SecondSpot; cli.RunLayout();
        Expect(At(cli, GT, SecondSpot), "a driven car is never moved");
        host.Driven.Add(GT); host.Follow(cli);
        Expect(At(host, Ranger, SecondSpot), "a moving observed car is not an obstacle: the proxy does not jump while it passes");
        cli.Driven.Remove(GT); host.Driven.Remove(GT); host.Follow(cli); cli.RunLayout();
        Expect(At(cli, GT, SecondSpot) && At(host, GT, SecondSpot), "the parked car stays where the driver left it");
        Expect(At(host, Ranger, MainSpot) && At(cli, Ranger, MainSpot), "the proxy moves to the free spot once the car settled, on both PCs");
        Separated(host, "after parking"); Same(host, cli, "after parking");
        int moves = host.Moves; for (int i = 0; i < 5; i++) host.RunLayout();
        Expect(host.Moves == moves, "no reposition loop");
    }

    // V1.4.1: both cars away -> two proxies; the first real car home takes the main spot whichever it is, the other car's
    // proxy yields to the second spot; the car arriving later takes the second spot (the first stays where it is).
    static void FirstArrivalMain()
    {
        foreach (var first in new[] { Ranger, GT })
        {
            ushort other = first == Ranger ? GT : Ranger;
            var f = new Fleet(true);
            f.Set(Ranger, AutoShop, 0); f.Set(GT, Madison, 0);
            var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };
            var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f };
            host.Load(); cli.Load();
            Expect(host.Pres[Ranger] == CarPresentation.AwayProxy && host.Pres[GT] == CarPresentation.AwayProxy && cli.Pres[Ranger] == CarPresentation.AwayProxy && cli.Pres[GT] == CarPresentation.AwayProxy, "both away: both cars keep travel proxies on both PCs");
            Separated(host, "two proxies host"); Same(host, cli, "two proxies");
            // the driver of 'first' comes home (its PC loads the hideout scene); the other player is already there
            byte holder = first == Ranger ? (byte)1 : (byte)2;
            Pc arriving = holder == 1 ? host : cli, staying = holder == 1 ? cli : host;
            f.Set(first, Hideout, holder); arriving.Load(); staying.Reconcile(); Settle(host, cli);
            Expect(At(host, first, MainSpot) && At(cli, first, MainSpot), (first == Ranger ? "Ranger" : "Shadowhawk") + " home first: main spot on both PCs");
            Expect(At(host, other, SecondSpot) && At(cli, other, SecondSpot), "the other car's proxy yields to the second spot");
            Expect(host.Pres[other] == CarPresentation.AwayProxy && cli.Pres[other] == CarPresentation.AwayProxy, "the away car keeps its proxy");
            Expect(f.Rec[other].Region != Hideout, "the proxy never changes its car's region");
            // the other car comes home later: second spot, the first car is not moved
            var before = host.Pos[first];
            byte h2 = holder == 1 ? (byte)2 : (byte)1;
            Pc arriving2 = h2 == 1 ? host : cli, staying2 = h2 == 1 ? cli : host;
            f.Set(other, Hideout, h2); arriving2.Load(); staying2.Reconcile(); Settle(host, cli);
            Expect(At(host, first, before) && At(cli, first, before), "settled first car never moved by the later arrival");
            Expect(At(host, other, SecondSpot) && At(cli, other, SecondSpot), "later arrival takes the second spot");
            Separated(host, "both home host"); Separated(cli, "both home Player 2"); Same(host, cli, "both home");
        }
    }

    static void SeatedStopped()
    {
        // Player 2's Shadowhawk is seated in on arrival (before its arrangement), stopped on the main spot; Ranger is a proxy
        var f = new Fleet(true);
        f.Set(Ranger, AutoShop, 0); f.Set(GT, Hideout, 2);
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f };
        var cli = new Pc { Slot = 2, CurrentCar = 1, VanPref = false, F = f, Layout = false };
        cli.Load(); cli.Layout = true;
        cli.Seated.Add(GT); cli.RunLayout();
        Expect(At(cli, GT, MainSpot), "a car with the driver seated is never moved");
        Expect(At(cli, Ranger, SecondSpot), "the stopped seated car is an obstacle: the proxy keeps clear of it");
        host.Load(); Settle(host, cli);
        Separated(host, "host"); Separated(cli, "Player 2"); Same(host, cli, "seated, stopped");
        // the driver parks somewhere else and gets out: the car stays where it was parked (never sent to its identity spot)
        var park = new Vec3(-103.5f, 76.4f, -160.0f);
        cli.Driven.Add(GT); cli.Pos[GT] = park; cli.RunLayout(); cli.Driven.Remove(GT); cli.Seated.Remove(GT);
        for (int i = 0; i < 3; i++) cli.RunLayout();
        Expect(At(cli, GT, park), "after parking and leaving the car it is never relocated");
        Settle(host, cli);
        Expect(At(cli, Ranger, MainSpot) && At(host, Ranger, MainSpot), "the proxy returns to its identity spot once it is free, on both PCs");
        Separated(host, "host after parking"); Same(host, cli, "after parking");
    }

    static void Determinism()
    {
        var a = new List<LayoutCar> { new LayoutCar { Id = GT, Role = LayoutRole.Proxy, Pos = SceneDefault }, new LayoutCar { Id = Ranger, Role = LayoutRole.Proxy, Pos = MainSpot } };
        var b = new List<LayoutCar> { a[1], a[0] };
        var pa = HideoutLayout.Plan(a, MainSpot, SecondSpot); var pb = HideoutLayout.Plan(b, MainSpot, SecondSpot);
        Expect(pa[0].Choice == pb[1].Choice && pa[1].Choice == pb[0].Choice, "input order does not matter");
        Expect(pa[1].Choice == LayoutChoice.MainSpot && pa[0].Choice == LayoutChoice.SecondSpot, "identity spots: Ranger main, Shadowhawk second");
        // fixed obstacle on the identity spot -> the other spot; both blocked -> stays
        var c = HideoutLayout.Plan(new List<LayoutCar> { new LayoutCar { Id = Ranger, Role = LayoutRole.Proxy, Pos = SceneDefault }, new LayoutCar { Id = GT, Role = LayoutRole.Fixed, Pos = MainSpot } }, MainSpot, SecondSpot);
        Expect(c[0].Choice == LayoutChoice.SecondSpot && c[0].Overlapped == false, "proxy avoids a fixed car");
        var d = HideoutLayout.Plan(new List<LayoutCar> { new LayoutCar { Id = Ranger, Role = LayoutRole.Arrange, Pos = new Vec3(0, 0, 0), HasPose = true, Pose = MainSpot }, new LayoutCar { Id = GT, Role = LayoutRole.Fixed, Pos = MainSpot } }, MainSpot, SecondSpot);
        Expect(d[0].Choice == LayoutChoice.SecondSpot, "a committed pose under a fixed car is not used");
        var e = HideoutLayout.Plan(new List<LayoutCar> { new LayoutCar { Id = GT, Role = LayoutRole.Proxy, Pos = MainSpot }, new LayoutCar { Id = Ranger, Role = LayoutRole.Fixed, Pos = MainSpot }, new LayoutCar { Id = 9, Role = LayoutRole.Fixed, Pos = SecondSpot } }, MainSpot, SecondSpot);
        Expect(e[0].Choice == LayoutChoice.Keep && e[0].Overlapped, "both original spots blocked: stays (reported as overlap), never an invented spot");
        // which PC loads first
        for (int first = 0; first < 2; first++)
        {
            var f = new Fleet(true); f.Set(Ranger, AutoShop, 0); f.Set(GT, Hideout, 2);
            var h = new Pc { Slot = 1, CurrentCar = 0, VanPref = true, F = f }; var k = new Pc { Slot = 2, CurrentCar = 1, F = f };
            if (first == 0) { h.Load(); k.Load(); } else { k.Load(); h.Load(); }
            Settle(h, k);
            Expect(At(h, GT, MainSpot) && At(h, Ranger, SecondSpot) && At(k, GT, MainSpot) && At(k, Ranger, SecondSpot), "load order " + first + ": same layout (real car main, proxy second)");
        }
        // CurrentCar combinations of the two PCs
        for (int hc = 0; hc < 2; hc++) for (int cc = 0; cc < 2; cc++)
        {
            var f = new Fleet(true); f.Set(Ranger, AutoShop, 0); f.Set(GT, Hideout, 2);
            var h = new Pc { Slot = 1, CurrentCar = hc, VanPref = true, F = f }; var k = new Pc { Slot = 2, CurrentCar = cc, VanPref = cc == 1, F = f };
            h.Load(); k.Load(); Settle(h, k);
            Separated(h, "CurrentCar " + hc + "/" + cc + " host"); Separated(k, "CurrentCar " + hc + "/" + cc + " Player 2"); Same(h, k, "CurrentCar " + hc + "/" + cc);
        }
    }

    static void Eligibility()
    {
        var f = new Fleet(false);   // Shadowhawk not bought
        f.Set(Ranger, Hideout, 1);
        var host = new Pc { Slot = 1, CurrentCar = 0, VanPref = false, F = f };
        host.Load();
        Expect(host.Pres[GT] == CarPresentation.Hidden && !host.Shows(GT), "a locked car is never presented");
        Expect(host.Pres[Ranger] == CarPresentation.Real && At(host, Ranger, MainSpot), "the only car keeps the main spot");
        foreach (var region in new[] { Hideout, AutoShop, Madison })
            foreach (byte slot in new byte[] { 1, 2 })
            {
                var r = new VehicleRecord { Flags = VehicleFlags.Unlocked, Region = Madison, Holder = 2 };
                var p = PresentationRules.Decide(r, region, slot);
                if (region == Hideout) Expect(p == CarPresentation.AwayProxy && !PresentationRules.AllowsDriving(p) && !PresentationRules.AllowsStorage(p), "hideout proxy: no driving, no storage");
                if (region == Madison && slot == 2) Expect(p == CarPresentation.Real, "the one real car is where its record says");
            }
    }

    static void Glue()
    {
        var v = Src("Plugin", "VehicleSync.cs"); var ly = Src("Plugin", "VehicleHideoutLayout.cs");
        Expect(v.Contains("Reconcile(gm, ps, region); MarkLayout(\"presentation reconcile\"); }") && v.Contains("LayoutFrame(gm, ps, region, now);"), "layout after every reconcile, then <= 1 Hz for settled poses");
        Expect(!v.Contains("KeepClearOfOtherCars"), "the one-shot W1 proxy check is gone (superseded by the shared layout)");
        Expect(v.Contains("lc.Arranged = true;   // V1.4: a handoff in place keeps the pose (never rearranged)"), "handoff in place is never rearranged");
        var lf = Body(ly, "void LayoutFrame(UnityEngine.Object gm, UnityEngine.Object ps, int region, double now)");
        Expect(lf.Contains("if (region != TravelRules.Hideout || gm == null) return;"), "hideout only");
        Expect(lf.Contains("pk.Region == TravelRules.Hideout"), "only hideout poses count (a carried car keeps its origin pose until arrival)");
        Expect(lf.Contains("if (seated || moving)") && lf.Contains("if (!lc.Arranged) { lc.Arranged = true;") && lf.Contains("if (moving) { anyMoving = true; continue; }") && lf.Contains("anyMoving = true; continue; }   // moving under its holder"), "driven cars are never moved (marked arranged); moving cars are not obstacles, stopped seated cars are");
        Expect(lf.Contains("HideoutLayout.Plan(cars, V(main.position), V(second.position), anyMoving)") && lf.Contains("_gmHideoutCar(gm)") && lf.Contains("_vmSecondPos(vm)"), "original spots GameManager.SpawnHideoutCar / VehiclesManager.secondCarHideoutPos");
        Expect(lf.Contains("_carRotYHideout(lc.Car)") && lf.Contains("second.rotation"), "original rotations (GameManager.Start rotY_hideout, InitiateMainCar)");
        Expect(!ly.Contains("SetPark") && !ly.Contains("SendProposal") && !ly.Contains("Commit(") && !ly.Contains("FlushFleet"), "no park pose / record / proposal written by the layout (proxy never changes its car)");
        Expect(!ly.Contains("FindObjectOfType") && !ly.Contains("FindObjectsOfType") && !ly.Contains("GameObject.Find"), "no scene search");
        var ds = Body(ly, "string Describe(LocalCar lc, string kind, LayoutCar c, LayoutResult r)");
        foreach (var w in new[] { "region ", "holder P", "presentation ", "root ", "renderers ", "pos ", "yaw ", "spot ", "park ", "overlap " }) Expect(ds.Contains(w), "diagnostics contain '" + w.Trim() + "'");
        Expect(lf.Contains("if (moved == null && sig == _layoutSig) return;"), "logged on scene bind / change / correction only, not every pass");
        Expect(lf.Contains("GhostIfAroundPlayer(lc, ps)"), "a proxy moved onto the player never closes around them");
        Expect(Src("Plugin", "VehicleShopSync.cs").Contains("hideoutLayout passes/moves"), "stats line");
    }

    static void Regressions()
    {
        var fleet = new Dictionary<ushort, VehicleRecord> { { Ranger, new VehicleRecord { Flags = VehicleFlags.Unlocked, Region = Madison, Holder = 1 } }, { GT, new VehicleRecord { Flags = VehicleFlags.Unlocked, Region = Hideout } } };
        Expect(PresentationRules.AutoShopProxyCar(fleet, AutoShop, 0) == Ranger, "Auto Shop proxy rule unchanged");
        Expect(PresentationRules.Decide(fleet[Ranger], Madison, 2) == CarPresentation.RemoteVisual && PresentationRules.Decide(fleet[GT], Madison, 1) == CarPresentation.Hidden, "other regions unchanged");
        var v = Src("Plugin", "VehicleSync.cs");
        Expect(Body(v, "void Activate(UnityEngine.Object gm, LocalCar lc, int region)").Contains("else if (!lc.Placed && region == TravelRules.Hideout) PlaceNewHideoutCar(gm, lc);"), "initial placement of an unplaced object kept (valid start pose before the layout)");
        Expect(ProtocolInfo.ProtocolVersion == 37 && ProtocolInfo.ModVersion == "0.26.0", "protocol 34 / mod 0.23.0");
    }

    // ------------------------------------------------------------------------------------------------ V1.4.1 medkit
    sealed class FakeMedkit : IMedkitHost
    {
        public bool Used, Loading; public int Applies; public byte LastSlot; public ushort LastCar;
        public bool TryUsed(out bool used) { used = Used; return !Loading; }
        public bool Apply(byte slot, ushort car) { Applies++; Used = true; LastSlot = slot; LastCar = car; return true; }
    }
    static uint _mpid;
    static WorldHost MedHost(int p1, int p2, int rangerRegion, int gtRegion, out FakeMedkit m)
    {
        var wh = new WorldHost(31) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, p1, 0); wh.SetPlayerRegion(2, p2, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox); wh.Fleet.Register(GT, 1, true, wh.Outbox);
        wh.Fleet.Place(Ranger, rangerRegion, wh.Outbox); wh.Fleet.Place(GT, gtRegion, wh.Outbox);
        wh.FleetInitialized(0);
        m = new FakeMedkit(); wh.MedkitHost = m; wh.Outbox.Clear();
        return wh;
    }
    static ProposalCode UseMed(WorldHost wh, ushort car, uint pid = 0, byte[] raw = null)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = car, Key = WorldKeys.MedkitUse, Value = raw ?? new byte[] { 1 } };
        return wh.HandleProposal("cli", 2, pid != 0 ? pid : ++_mpid, p, 1, out rev, out sr, out sh);
    }
    static bool MedState(WorldHost wh, out MedkitState s) { var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.Medkit); s = default(MedkitState); return e != null && MedkitState.TryParse(e.Value, out s); }

    static void MedkitOnce()
    {
        FakeMedkit m; var wh = MedHost(Hideout, Hideout, Hideout, Hideout, out m);
        uint pid = ++_mpid;
        Expect(UseMed(wh, Ranger, pid) == ProposalCode.Accepted && m.Applies == 1 && m.LastSlot == 2 && m.LastCar == Ranger, "Player 2's use applied once on the host (original key + removal)");
        MedkitState s; Expect(MedState(wh, out s) && s.Used && s.Car == Ranger && s.By == 2, "shared state committed: used, car, by P2");
        Expect(wh.Outbox.Count > 0, "state change sent to the client");
        Expect(UseMed(wh, Ranger, pid) == ProposalCode.Accepted && m.Applies == 1, "retransmitted proposal: cached answer, never applied twice");
        Expect(UseMed(wh, GT) == ProposalCode.Stale && m.Applies == 1, "second use (other car's medkit too): Stale - single use per save");
        FakeMedkit m2; var wh2 = MedHost(Hideout, Hideout, Hideout, Hideout, out m2);
        m2.Used = true; Expect(wh2.PublishMedkit(new MedkitState { Used = true, Car = GT, By = 1 }), "host's own use committed");
        Expect(!wh2.PublishMedkit(new MedkitState { Used = true, Car = GT, By = 1 }), "unchanged state not re-committed");
        Expect(UseMed(wh2, GT) == ProposalCode.Stale && m2.Applies == 0, "host used it first: Player 2 refused, nothing applied");
        Expect(wh2.PublishMedkit(new MedkitState()) && MedState(wh2, out s) && !s.Used, "host save state 'available' published at a scene bind (other save / new game)");
    }

    static void MedkitRefusals()
    {
        FakeMedkit m; var wh = MedHost(Hideout, Hideout, AutoShop, Hideout, out m);
        Expect(UseMed(wh, Ranger) == ProposalCode.NotAuthorized && m.Applies == 0, "Ranger is at the Auto Shop: its hideout object is a travel proxy -> refused");
        Expect(UseMed(wh, GT) == ProposalCode.Accepted && m.Applies == 1, "the real car in the requester's region works");
        FakeMedkit m2; var wh2 = MedHost(Hideout, Madison, Hideout, Hideout, out m2);
        Expect(UseMed(wh2, Ranger) == ProposalCode.NotAuthorized, "requester in another region than the car -> refused");
        Expect(UseMed(wh2, 77) == ProposalCode.Invalid, "unknown car -> Invalid");
        Expect(UseMed(wh2, Ranger, 0, new byte[] { 2 }) == ProposalCode.Invalid && UseMed(wh2, Ranger, 0, new byte[0]) == ProposalCode.Invalid && UseMed(wh2, 0) == ProposalCode.Invalid, "malformed -> Invalid");
        FakeMedkit m3; var wh3 = MedHost(Hideout, Hideout, Hideout, Hideout, out m3); m3.Loading = true;
        Expect(UseMed(wh3, Ranger) == ProposalCode.Busy && m3.Applies == 0, "host scene loading -> Busy, nothing consumed");
        m3.Loading = false; Expect(UseMed(wh3, Ranger) == ProposalCode.Accepted, "after the load it works");
        var wh4 = MedHost(Hideout, Hideout, Hideout, Hideout, out m3); wh4.MedkitHost = null;
        Expect(UseMed(wh4, Ranger) == ProposalCode.Busy, "no host medkit subsystem -> Busy (fail closed)");
        string why;
        Expect(MedkitRules.Decide(Ranger, true, Hideout, Regions.None, false, out why) == ProposalCode.NotAuthorized, "requester region unknown -> refused");
    }

    static void MedkitSeat()
    {
        Expect(MedkitRules.SeatReachable(true, true) && MedkitRules.SeatReachable(false, false) && MedkitRules.SeatReachable(true, false) && !MedkitRules.SeatReachable(false, true),
            "reachable from the seat: right door open (original tag) or engine off; not with door closed and engine running");
        var sp = new SeatProbe { Gates = true, FirstPerson = true, Ray = InteractTarget.Medkit, MedkitReachable = true };
        Expect(PassengerInteraction.Seated(sp) == SeatAction.UseMedkit, "first person, medkit reachable -> use");
        sp.MedkitReachable = false; Expect(PassengerInteraction.Seated(sp) == SeatAction.OutOfReach, "not reachable -> nothing happens");
        sp.FirstPerson = false; sp.MedkitReachable = true; Expect(PassengerInteraction.Seated(sp) == SeatAction.Exit, "third person has no original ray -> exit as before");
        sp.FirstPerson = true; sp.Gates = false; Expect(PassengerInteraction.Seated(sp) == SeatAction.None, "gates closed -> nothing");
        sp.Gates = true; sp.Ray = InteractTarget.CarBox; Expect(PassengerInteraction.Seated(sp) == SeatAction.OpenStorage, "CarBox unchanged");
        var st = new MedkitState { Used = true, Car = GT, By = 2 }; MedkitState back;
        Expect(MedkitState.TryParse(st.ToBytes(), out back) && back.Used && back.Car == GT && back.By == 2 && st.ToBytes().Length == 4, "4-byte state codec");
        Expect(!MedkitState.TryParse(new byte[] { 2, 0, 0, 0 }, out back) && !MedkitState.TryParse(new byte[] { 1, 0, 0, 3 }, out back) && !MedkitState.TryParse(new byte[3], out back) && !MedkitState.TryParse(null, out back), "malformed state rejected");
    }

    static void MedkitGlue()
    {
        var mk = Src("Plugin", "MedkitSync.cs"); var il = Src("Plugin", "MedkitIL.cs"); var vs = Src("Plugin", "VehicleSync.cs"); var pl = Src("Plugin", "Plugin.cs");
        var ro = Body(mk, "static void RunOriginal(object ps, GameObject go)");
        int a = ro.IndexOf("_heal(ps)"), b = ro.IndexOf("snd.Play()"), c = ro.IndexOf("UnityEngine.Object.Destroy(go)"), d = ro.IndexOf("_prefSet(MedkitRules.PrefKey, 1)");
        Expect(a > 0 && a < b && b < c && c < d, "the replaced original sequence runs unchanged and in order: Heal, healSound.Play, Destroy, SPrefs Medkit = 1");
        Expect(il.Contains("mi.Name != \"Heal\"") && il.Contains("(code[k - 2].operand as string) == \"Medkit\"") && il.Contains("m2.Name == \"SetInt\"") && il.Contains("if (count != 1 || hook == null) return code;"), "IL rewrite bound to the verified pattern, applied only once (real IL verified by RedirectTest)");
        Expect(mk.Contains("if (_sites != 1)") && mk.Contains("_h.UnpatchSelf()"), "pattern not found exactly once -> feature disabled, original untouched");
        var use = Body(mk, "void Use(object ps, GameObject go, bool seat)");
        Expect(use.IndexOf("if (proxy)") >= 0 && use.IndexOf("if (proxy)") < use.IndexOf("RunOriginal(ps, go)"), "travel proxy: nothing happens (checked before any use)");
        Expect(use.Contains("Key = WorldKeys.MedkitUse") && use.Contains("if (_pend != null) return;"), "client: one host proposal at a time, never a local heal before the answer");
        Expect(use.IndexOf("WorldKeys.MedkitUse") > use.IndexOf("if (_host)"), "only the host runs the original at once");
        var res = Body(mk, "internal bool OnProposalResult(uint id, ProposalCode code)");
        Expect(res.Contains("if (code == ProposalCode.Accepted)") && res.Contains("RunOriginal(p.Ps"), "client: original heal + consumption only after the host accepted");
        Expect(Body(mk, "internal void SeatUse(object ps, GameObject go)").Contains("\"not_bleeding\""), "seat: original bleeding requirement + original warning key");
        var tp = Body(mk, "static void TagPostfix(Component __instance)");
        Expect(tp.Contains("\"Untagged\"") && !tp.Contains("FindObject"), "proxy medkit prompt suppressed without a search");
        Expect(!Body(mk, "public void Update()").Contains("FindObject") && Body(mk, "void RemoveAll(string why)").Contains("FindObjectsOfType"), "FindObjectsOfType only when a committed change removes the medkits");
        Expect(vs.Contains("case SeatAction.UseMedkit:") && vs.Contains("mk.SeatUse(ps, _seatMedkit)") && vs.Contains("MedkitRules.SeatReachable(_seatMedkit.CompareTag(\"Medkit\"), EngineOn(t.Car.Id))"), "passenger seat wired to the shared use path");
        Expect(pl.Contains("_medkit = new MedkitSync(node, log, _world)") && pl.Contains("Release(ref _medkit, \"MEDKIT\")"), "subsystem created per session and released (offline game unpatched)");
        Expect(Src("Plugin", "WorldSync.cs").Contains("ProposalResultHook10(id, code)"), "proposal results routed");
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
