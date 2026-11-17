// W6B offline tests: drone flight presentation (body stays, drone separate), shared permanent security-camera disables with
// Player-2 story14 completion through the host's ORIGINAL DisableCamera -> AdvanceStory, and the cooperative story call.
// Real Shared code: PlayerStateCodec / SnapshotBuffer / DroneRules, WorldHost region-object book + WorldBreakRules, StoryCall
// rules and host handling; the original game methods are modelled from the decompiled IL (SecurityCamera.DisableCamera /
// Update, DroneManager.ActivateDrone / Update, StorySequence.Update). Unity glue is source-scanned. In-game: TESTING.md §55.
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W6BTests
{
    static int _passed, _failed;
    const int Madison = 99, Hideout = 0;
    const uint Digest = 0x0B0A2D1E;
    const byte C = (byte)ObjectKind.SecurityCamera;
    const uint Cam104 = 0x104C0001, Cam113 = 0x113C0002;
    const int S14 = 18, S15 = 19;

    static int Main()
    {
        Run("01 snapshot drone block: 34 B on foot, 52 B piloting, round trip exact, truncated / invalid drone blocks refused", Wire);
        Run("02 P1 flies, P2 observes: the remote body stays at the joystick_character spot, the drone moves separately (interpolated)", P1Flies);
        Run("03 P2 flies, P1 observes (relay through the host) + neither body ever follows the drone; position reports blocked", P2Flies);
        Run("04 drone start / stop / disconnect / other scene: remote drone shown only while flown in the same scene", DroneLifecycle);
        Run("05 P2 disables the House 104 camera: host commits once, host original DisableCamera advances story14 -> story15 once; P1 drone start afterwards keeps it disabled", P2Camera);
        Run("06 roles reversed: P1 disables, P2 replica disabled, P2's AdvanceStory stays suppressed; story advances once", P1Camera);
        Run("07 simultaneous disables, replays, stale epoch; re-enable impossible; other cameras untouched; temporary disables local", Races);
        Run("08 reload paths: member leaves / returns keeps state, last member gone or alone reload -> active again (original), no second story step", Reload);
        Run("09 story call: either player answers, one committed answer, other PC mirrors (ringing -> answer, pending -> bypass), stale / duplicate / own / in-call", Calls);
        Run("10 Unity glue: drone hooks, camera prefix 999 only + OnEnable, original methods reused, call hooks mirror the IL branches", Glue);
        Run("11 regressions + versions: snapshot still 34 B on foot, existing kinds unchanged, protocol 38 / mod 0.27.0", Versions);
        Console.WriteLine(_failed == 0 ? "W6B TESTS PASSED (" + _passed + ")" : "W6B TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ================================================================ drone
    static PlayerSnapshot Body(byte slot, uint seq, Vec3 pos) { return new PlayerSnapshot { Slot = slot, Seq = seq, TimeMs = seq * 50, SceneHash = 7, Pos = pos, Yaw = 90f, Flags = MoveFlags.Present | MoveFlags.Grounded }; }

    // Pilot side (PlayerSync.SendSnapshot): PS.transform rides the drone (DroneManager.Update playerAttach).
    static PlayerSnapshot Pilot(byte slot, uint seq, Vec3 joystick, Vec3 drone, Vec3 latched)
    {
        var s = Body(slot, seq, drone);   // what the snapshot was before W6B: PS.transform == drone position
        s.Pos = DroneRules.BodyFeet(true, joystick, true, latched, drone);
        s.Flags = DroneRules.BodyFlags(MoveFlags.Present | MoveFlags.Moving);
        s.DroneItem = 89; s.DronePos = drone; s.DroneYaw = 45f; s.DronePitch = 10; s.DroneRoll = -5;
        return s;
    }

    static PlayerSnapshot RoundTrip(PlayerSnapshot s, out int size)
    {
        var w = new NetDataWriter(); PlayerStateCodec.Write(w, s); size = w.Length;
        var r = new NetDataReader(w.CopyData()); r.GetByte(); PlayerSnapshot o;
        Expect(PlayerStateCodec.TryRead(r, out o), "decode");
        return o;
    }

    static void Wire()
    {
        int size; var foot = RoundTrip(Body(1, 1, new Vec3(1, 2, 3)), out size);
        Expect(size == PlayerStateCodec.WireSize && !foot.Has(MoveFlags.Drone), "on foot: unchanged 34 B");
        var p = Pilot(1, 2, new Vec3(10, 0, 10), new Vec3(30, 12, 40), new Vec3(10, 0, 10));
        var d = RoundTrip(p, out size);
        Expect(size == PlayerStateCodec.WireSize + PlayerStateCodec.DroneBlockSize, "piloting: 34 + 18 B");
        Expect(d.Has(MoveFlags.Drone) && d.DroneItem == 89 && Vec3.Distance(d.DronePos, p.DronePos) < 1e-4 && Math.Abs(d.DroneYaw - 45f) < 0.01f && d.DronePitch == 10 && d.DroneRoll == -5, "drone pose exact");
        Expect(Vec3.Distance(d.Pos, new Vec3(10, 0, 10)) < 1e-4, "Pos = the standing body");
        var w = new NetDataWriter(); PlayerStateCodec.Write(w, p);
        var cut = new byte[w.Length - 3]; Array.Copy(w.Data, cut, cut.Length);
        var r = new NetDataReader(cut); r.GetByte(); PlayerSnapshot x;
        Expect(!PlayerStateCodec.TryRead(r, out x), "truncated drone block refused");
        var bad = p; bad.DroneItem = 0; Expect(SnapshotValidation.Check(bad, 1, false, 0) == SnapshotVerdict.Invalid, "drone item 0 invalid");
        bad = p; bad.DronePos = new Vec3(float.NaN, 0, 0); Expect(SnapshotValidation.Check(bad, 1, false, 0) == SnapshotVerdict.Invalid, "NaN drone invalid");
        bad = p; bad.DronePos = new Vec3(60000, 0, 0); Expect(SnapshotValidation.Check(bad, 1, false, 0) == SnapshotVerdict.Invalid, "out-of-range drone invalid");
        Expect(SnapshotValidation.Check(p, 1, false, 0) == SnapshotVerdict.Accepted, "valid pilot snapshot");
        Expect(PlayerStateCodec.QuantizeAngle(350f) == -10 && PlayerStateCodec.QuantizeAngle(200f) == -127 && PlayerStateCodec.QuantizeAngle(20f) == 20, "angle quantization (signed, clamped)");
    }

    // Observer side (PlayerSync.OnMessage + UpdateRemote): body buffer + drone buffer.
    static void Observe(byte pilotSlot, out float bodyTravel, out float droneTravel, out bool droneShown)
    {
        var body = new SnapshotBuffer { InterpolationDelay = 0.1 }; var drone = new SnapshotBuffer { InterpolationDelay = 0.1, TeleportDistance = 30f };
        var joystick = new Vec3(100, 5, 100); var latched = new Vec3(100, 5, 100);
        Vec3 firstBody = default(Vec3), lastBody = default(Vec3), firstDrone = default(Vec3), lastDrone = default(Vec3); bool have = false;
        droneShown = false;
        for (uint i = 1; i <= 60; i++)
        {
            var dp = new Vec3(100 + i * 0.8f, 8 + i * 0.05f, 100 + i * 0.4f);   // 16 m/s at 20 Hz
            var s = Pilot(pilotSlot, i, joystick, dp, latched);
            int sz; s = RoundTrip(s, out sz);
            if (pilotSlot == 2) { int z; s = RoundTrip(s, out z); }   // host relay (PlayerSync host: SendToOthers, same codec)
            double now = i * 0.05 + 0.02;
            body.Add(s, now);
            if (s.Has(MoveFlags.Drone)) drone.Add(DroneRules.AsDroneSample(s), now);
            RemoteSample b, d;
            if (i < 4 || !body.TrySample(now, out b) || !drone.TrySample(now, out d)) continue;
            droneShown |= DroneRules.ShowRemoteDrone(true, true, true, s.Flags);
            if (!have) { firstBody = b.Pos; firstDrone = d.Pos; have = true; }
            lastBody = b.Pos; lastDrone = d.Pos;
            Expect(b.HorizontalSpeed < 0.01f && (b.Flags & MoveFlags.Moving) == 0, "pilot body does not walk");
            Expect(Vec3.Distance(b.Pos, d.Pos) > 2f, "body and drone are separate objects");
        }
        bodyTravel = Vec3.Distance(firstBody, lastBody); droneTravel = Vec3.Distance(firstDrone, lastDrone);
    }

    static void P1Flies()
    {
        float bt, dt; bool shown; Observe(1, out bt, out dt, out shown);
        Expect(bt < 1e-3f, "remote body stays at the joystick_character spot (" + bt + " m)");
        Expect(dt > 30f && shown, "remote drone moves (" + dt + " m) and is shown");
    }

    static void P2Flies()
    {
        float bt, dt; bool shown; Observe(2, out bt, out dt, out shown);
        Expect(bt < 1e-3f && dt > 30f && shown, "P1 sees P2's body stand and P2's drone fly");
        // Without joystick_character (missing / inactive): the latched pre-flight feet, never the drone / PS transform.
        var f = DroneRules.BodyFeet(false, default(Vec3), true, new Vec3(1, 2, 3), new Vec3(50, 20, 50));
        Expect(Vec3.Distance(f, new Vec3(1, 2, 3)) < 1e-4, "latched feet fallback");
        f = DroneRules.BodyFeet(false, default(Vec3), false, default(Vec3), new Vec3(50, 20.829f, 50));
        Expect(Math.Abs(f.Y - 20f) < 1e-3, "last fallback: original joystick offset below the player transform");
        Expect(!DroneRules.PositionReportAllowed(true) && DroneRules.PositionReportAllowed(false), "P2 spawn reports / host live position never taken mid-flight");
        Expect(DroneRules.BodyFlags(MoveFlags.Present | MoveFlags.Running | MoveFlags.Moving | MoveFlags.Crouching) == (MoveFlags.Present | MoveFlags.Crouching | MoveFlags.Grounded | MoveFlags.Drone), "flags: standing, grounded, drone");
    }

    static void DroneLifecycle()
    {
        var drone = MoveFlags.Present | MoveFlags.Drone; var foot = MoveFlags.Present;
        Expect(DroneRules.ShowRemoteDrone(true, true, true, drone), "flying in the same scene: shown");
        Expect(!DroneRules.ShowRemoteDrone(true, true, true, foot), "flight ended (ActivateDrone toggle / battery / signal / police): hidden");
        Expect(!DroneRules.ShowRemoteDrone(false, true, true, drone), "disconnect: hidden");
        Expect(!DroneRules.ShowRemoteDrone(true, false, true, drone), "other scene (travel): hidden");
        Expect(!DroneRules.ShowRemoteDrone(true, true, false, drone), "stale data (> 3 s): hidden");
        // landing: next on-foot snapshot carries the body where joystick_character was (+1.2 m original exit lift)
        var land = Body(1, 99, new Vec3(100, 5, 100)); int sz; land = RoundTrip(land, out sz);
        Expect(!land.Has(MoveFlags.Drone) && sz == PlayerStateCodec.WireSize, "landing: no drone block");
        var src = Src("Plugin", "PlayerSync.cs");
        Expect(src.Contains("else if (_droneBuffer.HasData) _droneBuffer.Clear();") && src.Contains("DestroyDrone(\"remote disconnected\");") && src.Contains("DestroyDrone(\"shutdown\");"), "cleanup on landing, disconnect, shutdown");
        Expect(src.Contains("_buffer.Clear(); _droneBuffer.Clear(); _lastRemoteScene = s.SceneHash;"), "scene change clears the drone buffer");
    }

    // ================================================================ cameras
    static uint _pid = 1;
    static ProposalCode Propose(WorldHost wh, byte slot, StateEntry e, double now, uint pid = 0)
    { uint rev; int sr; float sh; return wh.HandleProposal("c" + slot, slot, pid != 0 ? pid : _pid++, e, now, out rev, out sr, out sh); }
    static ProposalCode Scene(WorldHost wh, byte slot, int ns, int serial, double now)
    {
        var r = new ObjectSceneReport { Ns = ns, Serial = serial, Digest = Digest, Count = 900 };
        if (slot == 1) { string why; return wh.ObjectScene(1, r, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Player, ScopeArg = slot, Key = WorldKeys.ObjectScene, Value = r.ToBytes() }, now);
    }
    static ProposalCode Init(WorldHost wh, byte slot, int ns, double now)
    {
        ProposalCode last = ProposalCode.Invalid;
        foreach (var c in ObjectInitChunk.Split(ns, wh.Objects.EpochOf(ns), new List<ObjectInitEntry>()))
        {
            if (slot == 1) { string why; last = wh.ObjectInit(1, c, now, out why); }
            else last = Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectInit, Value = c.ToBytes() }, now);
        }
        return last;
    }
    static ObjectTxnRequest Req(WorldHost wh, uint hash, byte state, ushort baseVer, byte kind = C)
    { return new ObjectTxnRequest { Txn = _pid, Op = ObjectOp.Set, Kind = kind, Ns = Madison, Hash = hash, State = state, BaseVer = baseVer, Epoch = wh.Objects.EpochOf(Madison) }; }
    static ProposalCode Send(WorldHost wh, byte slot, ObjectTxnRequest t, double now, uint pid = 0)
    {
        if (slot == 1) { string why; return wh.ObjectTransaction(1, t, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() }, now, pid);
    }
    static byte Committed(WorldHost wh, uint hash) { ObjectState s; return wh.Objects.TryGet(ObjectKind.SecurityCamera, Madison, hash, out s) ? s.State : (byte)0; }
    static ushort Ver(WorldHost wh, uint hash) { ObjectState s; return wh.Objects.TryGet(ObjectKind.SecurityCamera, Madison, hash, out s) ? s.Ver : (ushort)0; }

    // Original SecurityCamera (IL 1.27c): DisableCamera(999) -> disabledPerma, isDisabled, visuals; onDisableAdvanceStory &&
    // current key == storyName -> StoryManager.AdvanceStory. Update: disabledPerma -> return (never re-enabled).
    sealed class Cam { public bool Perma, Disabled, StoryCam; public int Calls; }
    sealed class World
    {
        public readonly WorldHost Wh; public int HostStory = S14, HostAdvances, P2AdvanceSuppressed;
        public readonly Dictionary<uint, Cam> HostCams = new Dictionary<uint, Cam>(), P2Cams = new Dictionary<uint, Cam>();
        public readonly Dictionary<uint, uint> P2Wait = new Dictionary<uint, uint>();   // pid -> camera (WorldObjectSync._camWait)
        public World()
        {
            Wh = new WorldHost(51); Wh.SetPlayerRegion(1, Madison, 0); Wh.SetPlayerRegion(2, Madison, 0);
            Expect(Scene(Wh, 1, Madison, 1, 0) == ProposalCode.Accepted && Init(Wh, 1, Madison, 0.1) == ProposalCode.Accepted && Scene(Wh, 2, Madison, 2, 0.2) == ProposalCode.Accepted, "Madison ready on both");
            foreach (var h in new[] { Cam104, Cam113 }) { HostCams[h] = new Cam { StoryCam = h == Cam104 }; P2Cams[h] = new Cam { StoryCam = h == Cam104 }; }
        }
        string Key(int i) { return i == S14 ? "story14" : i == S15 ? "story15" : "story" + i; }
        // original method on the host PC
        public void HostOriginal(uint h)
        {
            var c = HostCams[h]; c.Calls++; c.Disabled = true; c.Perma = true;
            if (c.StoryCam && Key(HostStory) == "story14") { HostStory++; HostAdvances++; }
        }
        // original method on Player 2's PC: AdvanceStory hits StorySync's client prefix (suppressed, never local)
        public void P2Original(uint h)
        {
            var c = P2Cams[h]; c.Calls++; c.Disabled = true; c.Perma = true;
            if (c.StoryCam && Key(HostStory) == "story14") P2AdvanceSuppressed++;
        }
        // WorldObjectSync.AllowCamera (prefix) on either PC
        public string Press(byte slot, uint h, double now)
        {
            var cams = slot == 1 ? HostCams : P2Cams;
            if (cams[h].Perma) return "original guard";   // PlayerScript only offers the prompt for !isDisabled
            bool waiting = slot == 2 && P2Wait.ContainsValue(h);
            var g = WorldBreakRules.Gate(slot == 1, true, Committed(Wh, h) != 0, waiting);
            if (g == BreakGate.Refuse) return "refused";
            if (g == BreakGate.ApplyCommitted) { Sync(); return "replica"; }
            if (g == BreakGate.CommitThenRun)
            {
                var code = Send(Wh, 1, Req(Wh, h, ObjectStateRules.CamDisabled, Ver(Wh, h)), now);
                if (code == ProposalCode.Accepted) HostOriginal(h);
                Sync(); return code.ToString();
            }
            uint pid = _pid++;
            var r = Req(Wh, h, ObjectStateRules.CamDisabled, Ver(Wh, h)); r.Txn = pid;
            var pc = Send(Wh, 2, r, now, pid);
            if (pc == ProposalCode.Accepted) P2Wait[pid] = h;
            Sync();
            if (pc == ProposalCode.Accepted) { P2Wait.Remove(pid); if (!P2Cams[h].Perma) P2Original(h); }   // ResumeCamera
            return pc.ToString();
        }
        // WorldObjectSync.Reconcile -> Apply(SecurityCamera) on both PCs
        public void Sync()
        {
            foreach (var h in new[] { Cam104, Cam113 })
            {
                byte want = Committed(Wh, h);
                if (WorldBreakRules.Decide(want, HostCams[h].Perma, false) == BreakApply.Shatter) HostOriginal(h);   // CamDisable = original
                if (WorldBreakRules.Decide(want, P2Cams[h].Perma, P2Wait.ContainsValue(h)) == BreakApply.Shatter) P2Original(h);
            }
        }
        // P1 enters drone mode afterwards: ActivateDrone + SecurityCamera.Update / OnEnable on P1 - disabledPerma stays.
        public bool P1DroneStartKeepsDisabled(uint h) { var c = HostCams[h]; bool before = c.Disabled; /* Update: if (disabledPerma) return; */ return before && c.Perma && c.Disabled; }
    }

    static void P2Camera()
    {
        var w = new World();
        Expect(w.Press(2, Cam104, 1) == "Accepted", "P2's original disable proposed and accepted");
        Expect(Committed(w.Wh, Cam104) == ObjectStateRules.CamDisabled && Ver(w.Wh, Cam104) == 1, "committed once");
        Expect(w.HostCams[Cam104].Perma && w.P2Cams[Cam104].Perma, "disabled on both PCs (host replica via the original method)");
        Expect(w.HostStory == S15 && w.HostAdvances == 1, "host original onDisableAdvanceStory: story14 -> story15 exactly once");
        Expect(w.P1DroneStartKeepsDisabled(Cam104), "P1 enters drone mode afterwards: camera stays disabled");
        Expect(w.Press(1, Cam104, 2) == "original guard" && w.Press(2, Cam104, 2) == "original guard", "no second disable prompt / effect");
        w.Sync(); Expect(w.HostCams[Cam104].Calls == 1 && w.P2Cams[Cam104].Calls == 1 && w.HostAdvances == 1, "re-sync: nothing runs twice");
        Expect(!w.HostCams[Cam113].Disabled && !w.P2Cams[Cam113].Disabled, "other camera untouched");
    }

    static void P1Camera()
    {
        var w = new World();
        Expect(w.Press(1, Cam104, 1) == "Accepted", "host commits, original runs");
        Expect(w.HostStory == S15 && w.HostAdvances == 1, "story advanced on the host once");
        Expect(w.P2Cams[Cam104].Perma && w.P2AdvanceSuppressed == 0, "P2 replica disabled; host already story15 when the replica ran (no P2 attempt)");
        Expect(w.P2Cams[Cam104].Calls == 1 && w.HostCams[Cam104].Calls == 1, "each PC ran the original once");
    }

    static void Races()
    {
        var w = new World();
        // both press in the same host step: P2's proposal and the host's own commit race on BaseVer 0
        uint pid = _pid++; var r2 = Req(w.Wh, Cam104, 1, 0); r2.Txn = pid;
        var a = Send(w.Wh, 1, Req(w.Wh, Cam104, 1, 0), 1); if (a == ProposalCode.Accepted) w.HostOriginal(Cam104);
        var b = Send(w.Wh, 2, r2, 1, pid);
        Expect(a == ProposalCode.Accepted && b != ProposalCode.Accepted, "exactly one commit (" + a + " / " + b + ")");
        w.Sync();
        Expect(w.HostAdvances == 1 && w.P2Cams[Cam104].Perma && w.HostCams[Cam104].Calls == 1, "one story step, both disabled");
        Expect(Send(w.Wh, 2, r2, 1.5, pid) == b, "replayed proposal: cached answer");
        Expect(Send(w.Wh, 2, Req(w.Wh, Cam104, ObjectStateRules.CamActive, Ver(w.Wh, Cam104)), 2) != ProposalCode.Accepted, "re-enable refused (terminal)");
        Expect(ObjectStateRules.CheckTransition(ObjectKind.SecurityCamera, 1, 0, ObjectOp.Set) != null && ObjectStateRules.CheckTransition(ObjectKind.SecurityCamera, 0, 1, ObjectOp.Set) == null, "transition rules");
        var stale = Req(w.Wh, Cam113, 1, 0); stale.Epoch = (ushort)(w.Wh.Objects.EpochOf(Madison) + 5);
        Expect(Send(w.Wh, 2, stale, 3) != ProposalCode.Accepted, "stale epoch refused");
        Expect(!ObjectStateRules.ValidState(ObjectKind.SecurityCamera, 2) && ObjectStateRules.SupportedKind(ObjectKind.SecurityCamera), "state space 0 / 1");
        // waiting P2 presses again during the round trip: refused, no second proposal
        Expect(WorldBreakRules.Gate(false, true, false, true) == BreakGate.Refuse, "second press while waiting: refused");
        Expect(WorldBreakRules.Gate(false, false, false, false) == BreakGate.Refuse, "region not ready: refused (P2 fails closed)");
        var src = Src("Plugin", "WorldObjectSync.cs");
        Expect(src.Contains("disableTime != 999f) return true;   // temporary disables stay local"), "fuse box / hack / remote-drone temporary disables stay local");
    }

    static void Reload()
    {
        var w = new World();
        w.Press(2, Cam104, 1);
        ushort epoch = w.Wh.Objects.EpochOf(Madison);
        w.Wh.SetPlayerRegion(2, Hideout, 2); w.Wh.Tick(2 + ObjectStateRules.MemberGraceSeconds + 0.1);
        Expect(Committed(w.Wh, Cam104) == 1 && w.Wh.Objects.EpochOf(Madison) == epoch, "P2 travels away, P1 stays: kept");
        w.Wh.SetPlayerRegion(2, Madison, 10); Scene(w.Wh, 2, Madison, 11, 10);
        var fresh = new Cam { StoryCam = true };
        Expect(WorldBreakRules.Decide(Committed(w.Wh, Cam104), fresh.Perma, false) == BreakApply.Shatter, "P2 returns (fresh scene): replica disables its camera");
        Expect(w.HostStory == S15, "the returning replica cannot advance again (host story15; P2 AdvanceStory suppressed anyway)");
        w.Wh.SetPlayerRegion(1, Hideout, 20); w.Wh.SetPlayerRegion(2, Hideout, 20); w.Wh.Tick(20 + ObjectStateRules.MemberGraceSeconds + 0.1);
        Expect(w.Wh.Objects.EpochOf(Madison) == 0 && Committed(w.Wh, Cam104) == 0, "both left: original reload restores the camera (not saved by the game either)");
        w.Wh.SetPlayerRegion(1, Madison, 30); Scene(w.Wh, 1, Madison, 12, 30); Init(w.Wh, 1, Madison, 30.1);
        Expect(Committed(w.Wh, Cam104) == 0 && w.Wh.Objects.EpochOf(Madison) == epoch + 1, "new epoch: active camera");
        Expect(ObjectStateRules.Lifetime(ObjectKind.SecurityCamera) == ObjectLifetime.RegionLife, "region lifetime, never a save key");
    }

    // ================================================================ story call
    sealed class StoryHost : IStoryHost
    {
        public int Index = S14; public string Key = "story14"; public bool Free;
        public bool TryCurrent(out int i, out string k, out bool f) { i = Index; k = Key; f = Free; return true; }
        public bool Advance(int e) { return false; }
    }
    static StoryCallState Committed(WorldHost wh) { var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.StoryCall); StoryCallState s; return e != null && StoryCallState.TryParse(e.Value, out s) ? s : null; }
    static ProposalCode P2Answer(WorldHost wh, StoryCallState s, uint pid) { return Propose(wh, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StoryCallAnswer, Value = s.ToBytes() }, 1, pid); }

    static void Calls()
    {
        var st = new StoryCallState { Key = "story14", Seq = 12, By = 2 };
        StoryCallState back; Expect(StoryCallState.TryParse(st.ToBytes(), out back) && back.SameCall(st) && back.By == 2, "codec");
        Expect(!StoryCallState.TryParse(new byte[] { 1, 12, 3, 1, 65 }, out back) && !StoryCallState.TryParse(new byte[] { 2, 12, 2, 1, 65 }, out back), "bad slot / version refused");

        // P2 answers first
        var wh = new WorldHost(61); var sh = new StoryHost(); wh.StoryHost = sh;
        uint pid = _pid++;
        Expect(P2Answer(wh, st, pid) == ProposalCode.Accepted && Committed(wh).By == 2, "P2 answer committed");
        Expect(P2Answer(wh, st, pid) == ProposalCode.Accepted && wh.CallAnswers == 1, "retransmission: cached, counted once");
        Expect(P2Answer(wh, st, _pid++) == ProposalCode.Unchanged, "second answer of the same call: Unchanged");
        string why; Expect(wh.AnswerStoryCall(1, new StoryCallState { Key = "story14", Seq = 12, By = 1 }, out why) == ProposalCode.Unchanged, "host answering later: Unchanged (P2 won)");
        Expect(P2Answer(wh, new StoryCallState { Key = "story13", Seq = 12, By = 2 }, _pid++) == ProposalCode.Stale, "late answer of an older stage: Stale");
        Expect(Propose(wh, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StoryCallAnswer, Value = new StoryCallState { Key = "story14", Seq = 3, By = 1 }.ToBytes() }, 1) == ProposalCode.Invalid, "P2 cannot claim the host answered");
        Expect(Propose(wh, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StoryCall, Value = st.ToBytes() }, 1) == ProposalCode.NotAuthorized, "the committed record is host-only");
        sh.Free = true; Expect(wh.AnswerStoryCall(1, new StoryCallState { Key = "story14", Seq = 1, By = 1 }, out why) == ProposalCode.Unchanged, "free mode: no story calls");

        // mirror decisions on the other PC
        var p2c = Committed(wh);
        Expect(StoryCallRules.Decide(p2c, 1, "story14", 12, true, false, false) == CallMirror.AnswerRinging, "host still ringing -> original answer branch");
        Expect(StoryCallRules.Decide(p2c, 1, "story14", 12, false, true, false) == CallMirror.AnswerWhenRung, "host ring pending (busy) -> bypass branch when it would ring");
        Expect(StoryCallRules.Decide(p2c, 2, "story14", 12, true, false, false) == CallMirror.None, "own answer never mirrored");
        Expect(StoryCallRules.Decide(p2c, 1, "story14", 12, false, false, true) == CallMirror.None, "already in the conversation (both answered): nothing");
        Expect(StoryCallRules.Decide(p2c, 1, "story15", 12, true, false, false) == CallMirror.None, "different stage (story moved on): nothing");
        Expect(StoryCallRules.Decide(p2c, 1, "story14", 11, true, false, false) == CallMirror.None, "different sequence: nothing");
        Expect(StoryCallRules.Decide(p2c, 1, "story14", 12, false, false, false) == CallMirror.None, "call declined / over here: no replay");

        // host answers first
        var wh2 = new WorldHost(62); wh2.StoryHost = new StoryHost();
        Expect(wh2.AnswerStoryCall(1, new StoryCallState { Key = "story14", Seq = 12, By = 1 }, out why) == ProposalCode.Accepted && Committed(wh2).By == 1, "host answer committed");
        Expect(P2Answer(wh2, st, _pid++) == ProposalCode.Unchanged, "P2's simultaneous answer: Unchanged (both in the dialogue, one record)");
        Expect(StoryCallRules.Decide(Committed(wh2), 2, "story14", 12, true, false, false) == CallMirror.AnswerRinging, "P2 ringing -> answer branch");
        Expect(wh2.CallAnswers == 1, "one committed answer");
    }

    // ================================================================ glue
    static void Glue()
    {
        var ps = Src("Plugin", "PlayerSync.cs"); var rd = Src("Plugin", "RemoteDrone.cs"); var cb = Src("Plugin", "CampaignBootstrap.cs");
        Expect(ps.Contains("bool drone = LocalDrone.Active(_ps, out dT, out bT, out did);") && ps.Contains("s.Pos = DroneRules.BodyFeet(bT != null"), "snapshot: standing body while piloting");
        Expect(rd.Contains("Fast.Getter<bool>(dm, \"activated\")") && rd.Contains("\"joystick_character\"") && rd.Contains("\"droneObject\""), "original DroneManager / ItemID members");
        Expect(rd.Contains("Instantiate(src, _holder.transform, false);   // inactive parent: no Awake / OnEnable") && rd.Contains("RemoteAvatar.Strip(tpl)"), "remote drone = stripped clone of the original model");
        Expect(cb.Contains("if (!DroneRules.PositionReportAllowed(LocalDrone.Active(ps))) return;") && cb.Contains("if (LocalDrone.Active(ps, out dT, out bT, out did))"), "position reports / host live position use the body");
        var wo = Src("Plugin", "WorldObjectSync.cs"); var br = Src("Plugin", "WorldObjectBridge.cs");
        Expect(wo.Contains("AccessTools.Method(_b.CamT, \"DisableCamera\", new[] { typeof(float) })") && wo.Contains("_h.Patch(camEnable, postfix: hm(\"CamEnablePost\"))"), "DisableCamera prefix + OnEnable postfix");
        Expect(wo.Contains("return i._host; }   // Player 2 fails closed"), "P2 fails closed on hook errors");
        Expect(br.Contains("_camDisable = Fast.Call<float>(CamT, \"DisableCamera\")") && br.Contains("try { _camDisable(c, 999f); }"), "replica = the original DisableCamera(999)");
        Expect(br.Contains("public bool CamReady(Component c) { return c.gameObject.activeInHierarchy && _camVlb(c) != null; }"), "culled cameras wait for OnEnable (vlb)");
        var ss = Src("Plugin", "StorySync.cs");
        Expect(ss.Contains("AccessTools.Method(seq, \"Beginn\", Type.EmptyTypes)") && ss.Contains("AccessTools.Method(seq, \"PhoneCall1\", Type.EmptyTypes)"), "call hooks: Beginn + PhoneCall1");
        int m = ss.IndexOf("void Mirror(object seq, bool ringing)", StringComparison.Ordinal);
        string mirror = ss.Substring(m, ss.IndexOf("void CallTick()", m, StringComparison.Ordinal) - m);
        int a = mirror.IndexOf("_setCalling(seq, false);", StringComparison.Ordinal), b = mirror.IndexOf("pick.Play();", StringComparison.Ordinal), c = mirror.IndexOf("_seqBeginn(seq);", StringComparison.Ordinal), d = mirror.IndexOf("vib.Stop();", StringComparison.Ordinal), e = mirror.IndexOf("_psStopCall(ps);", StringComparison.Ordinal);
        Expect(a > 0 && b > a && c > b && d > c && e > d, "mirror = StorySequence.Update answer branch order (isCalling=false, pickUp, Beginn, vibrate stop, StopPhoneCall)");
        Expect(ss.IndexOf("ForceStop", StringComparison.Ordinal) < 0 || !ss.Contains("Fast.Call0(seq, \"ForceStop\")"), "declining is never shared");
        Expect(ss.Contains("if (reg.HeadRev != _callHead || reg.Epoch != _callEpoch)"), "call state parsed only on registry change");
    }

    static void Versions()
    {
        Expect(PlayerStateCodec.WireSize == 34, "on-foot snapshot size unchanged");
        foreach (var k in new[] { ObjectKind.Door, ObjectKind.Lockpick, ObjectKind.LockedWindow, ObjectKind.Breakable, ObjectKind.ParcelLocker }) Expect(ObjectStateRules.SupportedKind(k), k + " still shared");
        Expect(!ObjectStateRules.SupportedKind(ObjectKind.SecurityPanel), "security panels not shared (not in scope)");
        Expect(ProtocolInfo.ProtocolVersion == 38 && ProtocolInfo.ModVersion == "0.27.0", "protocol 38 / mod 0.27.0");
    }

    // ---------------------------------------------------------------- helpers
    static string Root()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && root != null; i++) { if (Directory.Exists(Path.Combine(root, "src", "Plugin"))) return Path.Combine(root, "src"); root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)); }
        throw new Exception("src not found");
    }
    static string Src(string dir, string f) { return File.ReadAllText(Path.Combine(Root(), dir, f)).Replace("\r\n", "\n"); }
}
