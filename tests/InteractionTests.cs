// M4.3.1 offline tests: passenger E resolver (original interaction first, passenger request only next to the car with no
// original target, obstacles), seated E (storage bridge / medkit out of reach / exit), seat authority regressions,
// local passenger body visibility rule, car storage authority rule, slow car-door state machine (protocol 11).
// Pure logic only: no Unity physics, raycasts, cameras, rendering, animation or inventory UI is exercised here - the
// plugin feeds these rules with the original PlayerScript ray and the car geometry in game (PENDING MANUAL QA).
using System;
using TS2MP.Shared;

static class InteractionTests
{
    static int _passed, _failed;
    static uint _pid;
    const int Hideout = 0, Madison = 99;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);

    static int Main()
    {
        Run("01 passenger entry by E next to the car when no original target is selected", EntryNoNativeTarget);
        Run("02 original door / tailgate / storage / medkit / SitCar targets take priority", NativePriority);
        Run("03 out-of-range, roof/underground and obstacle cases are rejected", RejectedEntries);
        Run("04 entry gates and car eligibility", GatesAndEligibility);
        Run("05 ray classification (tag list from PlayerScript.Update IL, components first)", Classification);
        Run("06 seated E: exit, storage bridge, medkit out of reach, UI/settle gates", SeatedRouting);
        Run("07 seat authority regressions: reservation, occupied seat, host/client symmetry", SeatAuthority);
        Run("08 local passenger body: third person only, never in first person / after exit", SelfBody);
        Run("09 car storage from a passenger seat: host save only (client sandbox copy is not shared)", StorageAuthority);
        Run("10 slow door: committed slow mark, fast finish, quick toggles", SlowDoorCommit);
        Run("11 door replica: slow replay, interruption (fast finish), late join, own reports", SlowDoorReplica);
        Run("12 door proposals: codec, host validation unchanged (moving car, region, retransmission)", DoorProposals);
        Run("13 per-press decision cost (offline) and zero allocations", Cost);
        Run("14 passenger exit spots: Ranger rear bench -> driver side behind the driver door, Shadowhawk front seat -> its side", ExitSpots);
        Run("15 no mod text in the gameplay UI (source scan of src/Plugin)", NoGameplayNotices);
        Run("16 entry prompt: shown exactly when E enters, hysteresis without false promises", PromptConsistency);
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

    // standing next to an eligible car on its ground level, clear line from the eyes to the car
    static FootProbe Near(InteractTarget ray)
    {
        return new FootProbe { Gates = true, Ray = ray, CarEligible = true, Reach = 0.4f, FeetHeight = 0.02f, LineOfSight = true };
    }

    static void EntryNoNativeTarget()
    {
        foreach (var t in new[] { InteractTarget.CarBody, InteractTarget.None, InteractTarget.Obstacle })
            Expect(PassengerInteraction.OnFoot(Near(t)) == FootAction.RequestPassenger, t + ": next to the car with a clear line, no original target -> passenger request (no aiming)");
        // M4.3.2a: the 0.6.1 failure cases - looking at the ground / the wall behind / away from the car
        var p = Near(InteractTarget.Obstacle);
        Expect(PassengerInteraction.OnFoot(p) == FootAction.RequestPassenger, "camera ray hits the ground beside the car -> still a request (was refused in 0.6.1)");
        p = Near(InteractTarget.CarBody); p.Reach = PassengerInteraction.EntryReach;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.RequestPassenger, "edge of the reach still valid");
        // 0.6.1 logs: feet measured -0.56..-0.60 m against the body box bottom; against the car's ground they are ~0
        foreach (var f in new[] { -0.45f, 0f, 0.3f, 0.55f })
        { p = Near(InteractTarget.None); p.FeetHeight = f; Expect(PassengerInteraction.OnFoot(p) == FootAction.RequestPassenger, "ground level / kerb / slope " + f); }
        Expect(PassengerInteraction.NearCar(0.5f, -0.5f) && !PassengerInteraction.NearCar(0.5f, -0.51f) && PassengerInteraction.NearCar(0.5f, 0.6f) && !PassengerInteraction.NearCar(0.5f, 0.61f), "height window edges");
    }

    static void NativePriority()
    {
        foreach (var t in new[] { InteractTarget.Native, InteractTarget.CarBox, InteractTarget.Medkit })
            Expect(PassengerInteraction.OnFoot(Near(t)) == FootAction.Original, t + ": the original branch runs, no passenger request");
        // the tags the original pipeline uses for these objects
        Expect(PassengerInteraction.Classify(true, "Door", false, false, true) == InteractTarget.Native, "car door (tag Door) -> original door (open/close, slow open)");
        Expect(PassengerInteraction.Classify(true, "VanDoor", false, false, true) == InteractTarget.Native, "Ranger tailgate (VanDoor) -> original");
        Expect(PassengerInteraction.Classify(true, "SitCar", false, false, true) == InteractTarget.Native, "open door's SitCar trigger -> original entry path (existing M4.3 routing)");
        Expect(PassengerInteraction.Classify(true, "CarBox", true, false, true) == InteractTarget.CarBox, "storage box -> original storage");
        Expect(PassengerInteraction.Classify(true, "Untagged", false, true, true) == InteractTarget.Medkit, "untagged medkit (door closed) still never triggers entry");
        Expect(PassengerInteraction.Classify(true, "Medkit", false, true, true) == InteractTarget.Medkit, "tagged medkit");
        // even when the car would accept a passenger, an original target wins
        var p = Near(InteractTarget.Native); p.CarEligible = true;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.Original, "native target beats an eligible car");
    }

    static void RejectedEntries()
    {
        var p = Near(InteractTarget.CarBody); p.Reach = PassengerInteraction.EntryReach + 0.01f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "beyond 1.2 m from the car's box -> nothing (no global range increase)");
        p = Near(InteractTarget.None); p.Reach = 3f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "far away -> nothing");
        p = Near(InteractTarget.CarBody); p.FeetHeight = 1.5f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "standing on the roof -> nothing");
        p = Near(InteractTarget.CarBody); p.FeetHeight = -2.8f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "a floor below the car -> nothing");
        p = Near(InteractTarget.CarBody); p.FeetHeight = 3f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "a floor above the car (balcony / ramp level) -> nothing");
        foreach (var t in new[] { InteractTarget.None, InteractTarget.CarBody, InteractTarget.Obstacle })
        { p = Near(t); p.LineOfSight = false; Expect(PassengerInteraction.OnFoot(p) == FootAction.None, t + ": wall / fence / solid object between the eyes and the car -> nothing"); }
        Expect(PassengerInteraction.Classify(true, "Untagged", false, false, false) == InteractTarget.Obstacle, "an untagged collider that is not part of the car is classified as obstacle (no original target)");
    }

    static void GatesAndEligibility()
    {
        var p = Near(InteractTarget.CarBody); p.Gates = false;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "original gates closed (canInteract off, carrying, hiding, running, UI open) -> nothing");
        p = Near(InteractTarget.CarBody); p.CarEligible = false;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "car not eligible (seat taken, moving, nobody driving) -> nothing");
        p = Near(InteractTarget.Native); p.Gates = false;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "closed gates: the resolver does not even claim the original branch");
        // eligibility comes from the existing M4.3 routing
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        VehicleRecord r = R(wh, Ranger);
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, false, true) == EnterAction.RequestPassenger, "other player drives, car stopped -> passenger request");
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, false, false) == EnterAction.Wait, "car moving -> wait");
        string why; wh.Motion.Report(Ranger, false, true, 1);
        Expect(wh.Ride(2, Ranger, 1, 2, out why) == ProposalCode.Accepted, "client reserves");
        r = R(wh, Ranger);
        Expect(PresentationRules.Enter(CarPresentation.RemoteVisual, r, 2, false, true) != EnterAction.RequestPassenger, "seat already claimed -> no second request");
    }

    static void Classification()
    {
        Expect(PassengerInteraction.Classify(false, null, false, false, false) == InteractTarget.None, "no hit");
        Expect(PassengerInteraction.Classify(true, "Untagged", false, false, true) == InteractTarget.CarBody, "untagged car collider = body");
        Expect(PassengerInteraction.Classify(true, "Player", false, false, true) == InteractTarget.CarBody, "a non-interaction tag on the car root = body");
        foreach (var tag in new[] { "Pickupable", "Door", "VanDoor", "CarBox", "Medkit", "SitCar", "GetOutCar", "HidingSpot", "Window", "NPC" })
            Expect(PassengerInteraction.IsNativeTag(tag), tag + " is an original interaction tag");
        Expect(!PassengerInteraction.IsNativeTag("Untagged") && !PassengerInteraction.IsNativeTag(null) && !PassengerInteraction.IsNativeTag("door"), "case-sensitive like String.op_Equality");
        Expect(PassengerInteraction.Classify(true, "Pickupable", false, false, false) == InteractTarget.Native, "an item lying next to the car -> original pickup");
    }

    static void SeatedRouting()
    {
        var s = new SeatProbe { Gates = true, FirstPerson = true, Ray = InteractTarget.None };
        Expect(PassengerInteraction.Seated(s) == SeatAction.Exit, "E without a special target -> exit (no aiming at a point)");
        s.Ray = InteractTarget.CarBody; Expect(PassengerInteraction.Seated(s) == SeatAction.Exit, "looking at the interior -> exit");
        s.Ray = InteractTarget.Native; Expect(PassengerInteraction.Seated(s) == SeatAction.Exit, "looking at the door -> exit (original: E on a car door while seated = ExitVehicle)");
        s.Ray = InteractTarget.CarBox; Expect(PassengerInteraction.Seated(s) == SeatAction.OpenStorage, "storage box -> storage, NOT exit");
        s.Ray = InteractTarget.Medkit; Expect(PassengerInteraction.Seated(s) == SeatAction.OutOfReach, "medkit -> out of reach, neither heal nor exit");
        s.FirstPerson = false; s.Ray = InteractTarget.CarBox;
        Expect(PassengerInteraction.Seated(s) == SeatAction.Exit, "third person: no interaction ray (original driver neither) -> exit");
        s = new SeatProbe { Gates = false, FirstPerson = true, Ray = InteractTarget.None };
        Expect(PassengerInteraction.Seated(s) == SeatAction.None, "storage/pause UI open or just closed, or < 1 s seated -> E does nothing (no exit + open in one press)");
    }

    static void SeatAuthority()
    {
        // host drives, client rides
        var wh = Both(Madison);
        wh.Fleet.Claim(Ranger, 1, Madison, wh.Outbox);
        wh.Motion.Report(Ranger, false, true, 1);
        string why;
        Expect(wh.Ride(2, Ranger, 1, 2, out why) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 2, "client reserves the seat");
        Expect(wh.Ride(2, Ranger, 2, 2.3, out why) == ProposalCode.Accepted && !R(wh, Ranger).PassengerPending, "confirmed seated");
        Expect(wh.Ride(1, Ranger, 1, 2.4, out why) != ProposalCode.Accepted, "the driver cannot also claim the passenger seat");
        Expect(R(wh, Ranger).Driver == 1 && R(wh, Ranger).Holder == 1, "driver/holder untouched by the passenger");
        Expect(wh.Ride(2, Ranger, 0, 5, out why) == ProposalCode.Accepted && R(wh, Ranger).Passenger == 0, "exit releases through the host only");
        // reverse roles: client drives, host rides
        var wh2 = Both(Madison);
        Expect(Prop(wh2, 2, WorldKeys.VehicleTake, Ranger, 1, 1) == ProposalCode.Accepted && Prop(wh2, 2, WorldKeys.VehicleClaim, Ranger, 1, 1.1) == ProposalCode.Accepted, "client drives");
        wh2.Motion.Report(Ranger, false, true, 2);
        Expect(wh2.Ride(1, Ranger, 1, 3, out why) == ProposalCode.Accepted && R(wh2, Ranger).Passenger == 1, "host rides with the client driver");
        Expect(Prop(wh2, 2, WorldKeys.VehicleRide, Ranger, 1, 3.1) != ProposalCode.Accepted, "occupied seat cannot be claimed again");
    }

    static void SelfBody()
    {
        Expect(PassengerInteraction.SelfBodyVisible(true, true, true), "seated + third person -> own body visible");
        Expect(!PassengerInteraction.SelfBodyVisible(true, false, true), "first person -> hidden (no head in the camera, no duplicate)");
        Expect(!PassengerInteraction.SelfBodyVisible(false, true, true), "not seated (requested, exiting, on foot) -> hidden");
        Expect(!PassengerInteraction.SelfBodyVisible(true, true, false), "no seat anchor -> hidden");
        // FP -> TPP -> FP sequence
        bool[] modes = { false, true, false, true, true, false };
        bool[] want = { false, true, false, true, true, false };
        for (int i = 0; i < modes.Length; i++) Expect(PassengerInteraction.SelfBodyVisible(true, modes[i], true) == want[i], "transition step " + i);
    }

    static void StorageAuthority()
    {
        Expect(PassengerInteraction.SeatStorageAllowed(true, false), "host passenger: its own save holds the real car storage -> original storage opens");
        Expect(!PassengerInteraction.SeatStorageAllowed(false, false), "client passenger without the shared lease: refused (sandbox copy)");
    }

    static void SlowDoorCommit()
    {
        byte m = 0;
        m = VehicleDoors.Next(m, 1, true, DoorMode.Slow);
        Expect(VehicleDoors.IsOpen(m, 1) && VehicleDoors.IsSlow(m, 1) && m == 0x0A, "slow open of R: open + slow mark");
        Expect(VehicleDoors.Next(m, 1, true, DoorMode.Slow) == m, "duplicate slow report: no new revision");
        byte f = VehicleDoors.Next(m, 1, true, DoorMode.FastFinish);
        Expect(f == 0x02, "E released early: slow mark cleared, still open");
        Expect(VehicleDoors.Next(f, 1, true, DoorMode.FastFinish) == f, "second fast finish: no change");
        Expect(VehicleDoors.Next(0, 0, false, DoorMode.FastFinish) == 0, "fast finish of a door that never moved: no change");
        byte q = VehicleDoors.Next(m, 1, false, DoorMode.Quick);
        Expect(q == 0, "quick close after a slow open: closed, slow mark gone");
        byte both = VehicleDoors.Next(VehicleDoors.Next(0, 0, true, DoorMode.Quick), 1, false, DoorMode.Slow);
        Expect(both == 0x01, "slow 'close' of a closed door: unchanged; L stays quick-open");
        byte sc = VehicleDoors.Next(0x01, 0, false, DoorMode.Slow);
        Expect(sc == 0x04, "slow close of L: closed + slow mark (quiet close replayed)");
    }

    static void SlowDoorReplica()
    {
        // observer replays a slow open
        var o = new DoorReplica(); o.Bind(0); o.OnCommitted(0); o.Applied();   // scene bound, first state applied
        byte want;
        o.OnCommitted(0x0A);
        Expect(o.Wanted(out want) && o.Plan(1) == DoorStep.OpenSlow && o.Plan(0) == DoorStep.None, "remote slow open -> slow swing (not a normal-speed swing)");
        o.Applied();
        Expect(!o.Wanted(out want) && VehicleDoors.IsSlow(o.AppliedSlow, 1), "slow replay in progress, nothing more to apply");
        // interruption: remote player releases E early
        o.OnCommitted(0x02);
        Expect(o.Wanted(out want) && o.Plan(1) == DoorStep.FinishFast, "fast finish while the slow replay runs");
        o.Applied();
        Expect(!o.Wanted(out want) && o.AppliedSlow == 0, "finished fast once, no repeat");
        // fast finish arriving after the local replay already reached its state: nothing
        var o2 = new DoorReplica(); o2.Bind(0); o2.OnCommitted(0); o2.Applied();
        o2.OnCommitted(0x0A); o2.Applied(); o2.SlowEnded(1);
        o2.OnCommitted(0x02);
        Expect(!o2.Wanted(out want), "replay already finished: a late fast finish is moot");
        // rapid toggling: slow open then quick close before the replay ends
        var o3 = new DoorReplica(); o3.Bind(0); o3.OnCommitted(0); o3.Applied();
        o3.OnCommitted(0x0A); o3.Applied();
        o3.OnCommitted(0x00);
        Expect(o3.Wanted(out want) && o3.Plan(1) == DoorStep.CloseQuick, "quick close interrupts the slow replay");
        o3.Applied();
        Expect(o3.AppliedSlow == 0 && !o3.Wanted(out want), "converged closed");
        // late join / fresh scene objects: committed slow mark is NOT replayed slowly
        var late = new DoorReplica(); late.OnCommitted(0x0A); late.Bind(0);
        Expect(late.Wanted(out want) && late.Plan(1) == DoorStep.OpenQuick, "late joiner: door just swings to its state");
        late.Applied();
        Expect(!late.Wanted(out want) && late.AppliedSlow == 0, "late joiner does not wait for a fast finish");
        late.OnCommitted(0x02);
        Expect(!late.Wanted(out want), "a later fast finish of that old slow open is moot");
        // the reporter itself (local slow open) never replays its own change
        var me = new DoorReplica(); me.Bind(0); me.OnCommitted(0); me.Applied();
        Expect(me.TakeLocalChanges(0x02) == 0x02, "local slow open detected (isOpen toggles at the start)");
        me.OnCommitted(0x0A);                                                  // host commits in place
        Expect(!me.Wanted(out want), "own slow change: no echo");
        me.OnCommitted(0x02);
        Expect(!me.Wanted(out want), "own fast finish: no echo");
        // client reporter with reports in flight
        var cl = new DoorReplica(); cl.Bind(0); cl.OnCommitted(0); cl.Applied();
        cl.TakeLocalChanges(0x02); cl.Sent();
        cl.OnCommitted(0x0A);
        Expect(!cl.Wanted(out want), "in flight: nothing applied");
        cl.Result();
        Expect(!cl.Wanted(out want), "after the result: converged");
    }

    static void DoorProposals()
    {
        int d; bool open; DoorMode mode;
        Expect(VehicleDoors.TryParseProposal(VehicleDoors.Proposal(1, true, DoorMode.Slow), out d, out open, out mode) && d == 1 && open && mode == DoorMode.Slow, "codec slow");
        Expect(VehicleDoors.TryParseProposal(VehicleDoors.Proposal(0, false, DoorMode.FastFinish), out d, out open, out mode) && d == 0 && !open && mode == DoorMode.FastFinish, "codec fast finish");
        Expect(!VehicleDoors.TryParseProposal(new byte[] { 0, 1, 3 }, out d, out open, out mode), "unknown mode rejected");
        Expect(!VehicleDoors.TryParseProposal(new byte[] { 0, 1 }, out d, out open, out mode), "protocol-10 proposal rejected");
        var wh = Both(Madison);
        string why;
        Expect(DoorP(wh, 2, Ranger, 1, true, 1, DoorMode.Slow) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0x0A, "client slow open committed with slow mark");
        Expect(DoorP(wh, 2, Ranger, 1, true, 1.5, DoorMode.FastFinish) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0x02, "client fast finish committed");
        Expect(DoorP(wh, 2, Ranger, 1, true, 1.6, DoorMode.FastFinish) == ProposalCode.Unchanged, "duplicate fast finish: unchanged");
        uint pid = 4242;
        Expect(DoorP(wh, 2, Ranger, 0, true, 2, DoorMode.Slow, pid) == ProposalCode.Accepted, "slow open L");
        wh.SetDoor(1, Ranger, 0, false, DoorMode.Quick, 2.1, out why);
        Expect(DoorP(wh, 2, Ranger, 0, true, 2.2, DoorMode.Slow, pid) == ProposalCode.Accepted && !VehicleDoors.IsOpen(wh.DoorMask(Ranger), 0), "retransmitted slow open answered from the cache, not re-applied");
        _pid = 5000;                                                          // proposal ids increase per client instance
        wh.Motion.Report(Ranger, true, true, 3);
        Expect(DoorP(wh, 2, Ranger, 0, true, 3.1, DoorMode.Slow) == ProposalCode.Busy, "moving car: slow open refused like a quick one");
        Expect(wh.SetDoor(1, Ranger, 1, false, DoorMode.Slow, 3.2, out why) == ProposalCode.Accepted && wh.DoorMask(Ranger) == 0x08, "holder's slow close while driving");
    }

    static void Cost()
    {
        const int N = 1000000;
        var fp = Near(InteractTarget.None);
        var sp = new SeatProbe { Gates = true, FirstPerson = true, Ray = InteractTarget.CarBox };
        var rep = new DoorReplica(); rep.Bind(0); rep.OnCommitted(0x0A);
        int gc0 = GC.CollectionCount(0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int acc = 0;
        for (int i = 0; i < N; i++)
        {
            fp.Reach = (i & 3) * 0.5f;
            if (PassengerInteraction.OnFoot(fp) == FootAction.RequestPassenger) acc++;
            if (PassengerInteraction.Classify(true, (i & 1) == 0 ? "Untagged" : "Door", false, false, true) == InteractTarget.Native) acc++;
            if (PassengerInteraction.Seated(sp) == SeatAction.OpenStorage) acc++;
            if (rep.Plan(i & 1) == DoorStep.OpenSlow) acc++;
        }
        sw.Stop();
        int gcs = GC.CollectionCount(0) - gc0;
        Console.WriteLine("      offline (.NET Framework, not Unity): foot + seat + door decisions " + (sw.Elapsed.TotalMilliseconds * 1000.0 / N).ToString("0.000") + " us/frame, gen0 GCs over 1M frames: " + gcs + " (acc " + acc + ")");
        Expect(gcs == 0, "no allocations");
    }

    static void ExitSpots()
    {
        Vec3 a, b;
        // Ranger (SEAT_PROBE): rear bench associateSitPos (-0.01, -0.40, -0.23), driver door on the left (negative x)
        PassengerInteraction.ExitSpots(new Vec3(-0.01f, -0.40f, -0.23f), -0.75f, -0.95f, 0.95f, out a, out b);
        Expect(Math.Abs(a.X - (-0.95f - PassengerInteraction.ExitClearance)) < 1e-4f && Math.Abs(a.Z - (-0.23f)) < 1e-4f, "Ranger: left side, beside the rear bench (behind the driver door): " + a);
        Expect(Math.Abs(b.X - (0.95f + PassengerInteraction.ExitClearance)) < 1e-4f && Math.Abs(b.Z - (-0.23f)) < 1e-4f, "Ranger: mirrored spot = right rear side, never the front door area: " + b);
        Expect(a.Z < 0.0f, "the exit z is the rear bench, not the front passenger door (front row z > 0)");
        // Shadowhawk: front passenger associateSitPos (0.44, -0.78, 0.09) on the right; its only Door is the driver door
        PassengerInteraction.ExitSpots(new Vec3(0.44f, -0.78f, 0.09f), -0.7f, -0.9f, 0.9f, out a, out b);
        Expect(a.X > 0.9f && Math.Abs(a.Z - 0.09f) < 1e-4f, "Shadowhawk: right side beside the front passenger seat (not the Ranger's left-rear rule): " + a);
        Expect(b.X < -0.9f, "Shadowhawk mirrored: driver side");
        // a centre seat in a car with the driver on the right follows the driver side
        PassengerInteraction.ExitSpots(new Vec3(0f, 0f, -0.3f), 0.7f, -0.9f, 0.9f, out a, out b);
        Expect(a.X > 0.9f, "centre seat follows the driver side");
        Expect(PassengerInteraction.ExitClearance >= 0.35f + 0.05f, "clearance keeps the 0.35 m player capsule outside the car body");
    }

    // Permanent rule (AGENTS.md / CLAUDE.md, M4.3.2a): the mod never writes its own text into the game UI. The only allowed
    // UI call is the game's own warning spawner with an original text key.
    static void NoGameplayNotices()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        string plugin = null;
        for (int i = 0; i < 6 && root != null; i++)
        {
            var c = System.IO.Path.Combine(root, "src", "Plugin");
            if (System.IO.Directory.Exists(c)) { plugin = c; break; }
            root = System.IO.Path.GetDirectoryName(root.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        }
        Expect(plugin != null, "src/Plugin found from the test folder");
        string[] allowedKeys = { "cant_fast_travel" };
        int files = 0;
        foreach (var f in System.IO.Directory.GetFiles(plugin, "*.cs"))
        {
            files++;
            var lines = System.IO.File.ReadAllLines(f);
            for (int n = 0; n < lines.Length; n++)
            {
                var l = lines[n];
                string where = System.IO.Path.GetFileName(f) + ":" + (n + 1);
                Expect(l.IndexOf("SpawnNormalText", StringComparison.Ordinal) < 0, where + " uses the game's free-text spawner");
                Expect(l.IndexOf("ShowText(", StringComparison.Ordinal) < 0, where + " still calls ShowText");
                int w = l.IndexOf("_warn(", StringComparison.Ordinal);
                if (w >= 0 && l.IndexOf("Fast.Call", StringComparison.Ordinal) < 0)
                {
                    bool ok = false;
                    foreach (var k in allowedKeys) if (l.IndexOf("\"" + k + "\"", w, StringComparison.Ordinal) > 0) ok = true;
                    Expect(ok, where + " shows a warning that is not an original text key");
                }
            }
        }
        Expect(files >= 5, "plugin sources scanned");
    }

    // M4.3.2b: the prompt is FootAction.RequestPassenger of the same probe the E press uses, so prompt == action by
    // construction; these cases pin the hysteresis and the invalidating conditions.
    static void PromptConsistency()
    {
        var p = Near(InteractTarget.None); p.Reach = PassengerInteraction.EntryReach + 0.05f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "approaching: not shown beyond 1.2 m");
        p.WasShown = true;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.RequestPassenger, "already shown: stays until 1.3 m - and E there still enters (same rule)");
        p.Reach = PassengerInteraction.EntryReach + PassengerInteraction.Hysteresis + 0.01f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "beyond the hysteresis: hidden");
        foreach (var t in new[] { InteractTarget.Native, InteractTarget.CarBox, InteractTarget.Medkit })
        { p = Near(t); p.WasShown = true; Expect(PassengerInteraction.OnFoot(p) == FootAction.Original, t + ": the original prompt replaces the passenger prompt at once"); }
        p = Near(InteractTarget.None); p.WasShown = true; p.CarEligible = false;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "seat taken / car starts moving / driver leaves: hidden at once (no hysteresis on eligibility)");
        p = Near(InteractTarget.None); p.WasShown = true; p.Gates = false;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "a UI opens / interaction disabled: hidden");
        p = Near(InteractTarget.None); p.WasShown = true; p.LineOfSight = false;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "stepped behind a wall: hidden");
        p = Near(InteractTarget.None); p.WasShown = true; p.FeetHeight = PassengerInteraction.GroundAbove + PassengerInteraction.Hysteresis + 0.01f;
        Expect(PassengerInteraction.OnFoot(p) == FootAction.None, "climbed up: hidden");
    }

    // ---- helpers (same setup as PassengerTests) ----
    static WorldHost Both(int region)
    {
        var wh = new WorldHost(7) { FallbackHours = 12f };
        wh.SetPlayerRegion(1, region, 0);
        wh.SetPlayerRegion(2, region, 0);
        wh.Fleet.Register(Ranger, 0, true, wh.Outbox);
        wh.Fleet.Register(GT, 1, false, wh.Outbox);
        wh.Fleet.Place(Ranger, region, wh.Outbox);
        wh.Fleet.Place(GT, Hideout, wh.Outbox);
        wh.Fleet.ReassignHolders(s => RegionDirectory.RegionOf(wh.Reg, s), wh.Outbox);
        wh.Outbox.Clear();
        return wh;
    }

    static VehicleRecord R(WorldHost wh, ushort id) { VehicleRecord r; Expect(wh.Fleet.TryGet(id, out r), "record " + id); return r; }

    static ProposalCode Prop(WorldHost wh, byte slot, string key, ushort id, byte v, double now)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = id, Key = key, Value = new[] { v } };
        return wh.HandleProposal("cli", slot, ++_pid, p, now, out rev, out sr, out sh);
    }

    static ProposalCode DoorP(WorldHost wh, byte slot, ushort id, int door, bool open, double now, DoorMode mode, uint pid = 0)
    {
        uint rev; int sr; float sh;
        var p = new StateEntry { Scope = StateScope.Session, ScopeArg = id, Key = WorldKeys.VehicleDoor, Value = VehicleDoors.Proposal(door, open, mode) };
        return wh.HandleProposal("cli", slot, pid != 0 ? pid : ++_pid, p, now, out rev, out sr, out sh);
    }
}
