// W2A offline tests: shared destructible world objects (original DestroyObject: the two House 102 barrier stages, breakable
// furniture, toilets) on the host-owned region-object registry (kind Breakable = 26). Drives the SAME Unity-free pieces as
// the plugin: ObjectStateRules / WorldBreakRules (state, transition, prefix gate and apply decisions), RegionStateBook through
// WorldHost ("objScene" / "objInit" / "objTxn"), the registry snapshot + delta wire format to client registries and
// ObjectPageCache. Each PC is a simulated scene instance that runs the plugin's rule: the original Destroy_Thing (actor
// effects: NPC alert, fragments, sound) only after the host committed it; every other PC applies the replica (fragments,
// sound unless silent, never an NPC alert or mission completion). Unity behaviour (crowbar hit, fragments, colliders,
// passage) is manual two-PC QA (docs/TESTING.md §47); the Unity glue is source-scanned (test 11).
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class W2ATests
{
    static int _passed, _failed;
    const int Madison = 99, Hillville = 97, Hideout = 0;
    const uint Digest = 0x0B0A2D1E;
    const byte B = (byte)ObjectKind.Breakable, D = (byte)ObjectKind.Door;
    // House 102 (docs/research/DESTRUCTIBLES_SYSTEM.md §2): two independent DestroyObject components
    const uint Plywood = 0x10200001, Planks = 0x10200002, Toilet = 0x0000700E, Chair = 0x0000C4A1, AcMission = 0x101AC000;

    static int Main()
    {
        Run("01 rules + wire: kind Breakable = 26 (DestroyObject), state 0/1 terminal, registry wobj26, gate / apply tables, protocol 35 / mod 0.24.0", Rules);
        Run("02 House 102: stage 1 (plywood) and stage 2 (planks) are separate objects; P2 breaks one, the host the other; passage open on both PCs", House102);
        Run("03 either player breaks each stage (both orders); ordinary furniture / toilet use the same path", EitherPlayer);
        Run("04 exactly once: repeated swings while waiting, retransmitted / duplicated proposals, second hit on a broken object - one commit, one actor effect", ExactlyOnce);
        Run("05 concurrent hits on the same stage (host first / P2 first / reordered duplicate): one winner, loser shows the replica without an NPC alert", Concurrent);
        Run("06 stale + invalid events: old epoch, wrong region, non-member, bad state, revert to intact, NPC op - all refused, state unchanged", Stale);
        Run("07 late joiner: broken stages applied silently on region ready (no sound, no alert); intact objects untouched", LateJoin);
        Run("08 region reload: last player leaves / alone reload / disconnect = original reset (intact again), never persisted", Reload);
        Run("09 mission-completing breakable: Player 2 refused (host + local prefix), host's original commit replicated without mission effect", Mission);
        Run("10 initializer + capacity: pre-broken object in the initial state, Madison count fits, rate limit, independent regions", InitAndCapacity);
        Run("11 Unity glue: Destroy_Thing prefix gates both roles, Player 2 fails closed, replica mirrors the IL minus alert / mission, no scans", Glue);
        Console.WriteLine(_failed == 0 ? "W2A TESTS PASSED (" + _passed + ")" : "W2A TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ---------------------------------------------------------------- host harness
    static uint _pid = 1;
    static ProposalCode Propose(WorldHost wh, byte slot, StateEntry e, double now, uint pid = 0)
    { uint rev; int sr; float sh; return wh.HandleProposal("c" + slot, slot, pid != 0 ? pid : _pid++, e, now, out rev, out sr, out sh); }

    static ProposalCode Scene(WorldHost wh, byte slot, int ns, int serial, double now)
    {
        var r = new ObjectSceneReport { Ns = ns, Serial = serial, Digest = Digest, Count = 900 };
        if (slot == 1) { string why; return wh.ObjectScene(1, r, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Player, ScopeArg = slot, Key = WorldKeys.ObjectScene, Value = r.ToBytes() }, now);
    }

    static ProposalCode Init(WorldHost wh, byte slot, int ns, List<ObjectInitEntry> all, double now)
    {
        ProposalCode last = ProposalCode.Invalid;
        foreach (var c in ObjectInitChunk.Split(ns, wh.Objects.EpochOf(ns), all))
        {
            if (slot == 1) { string why; last = wh.ObjectInit(1, c, now, out why); }
            else last = Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectInit, Value = c.ToBytes() }, now);
            if (last != ProposalCode.Accepted) return last;
        }
        return last;
    }

    static ObjectTxnRequest Req(WorldHost wh, uint hash, byte state, ushort baseVer, int ns = Madison, ObjectOp op = ObjectOp.Set, int epoch = -1, byte kind = B)
    { return new ObjectTxnRequest { Txn = _pid, Op = op, Kind = kind, Ns = ns, Hash = hash, State = state, BaseVer = baseVer, Epoch = epoch >= 0 ? (ushort)epoch : wh.Objects.EpochOf(ns) }; }

    static ProposalCode Send(WorldHost wh, byte slot, ObjectTxnRequest t, double now, uint pid = 0)
    {
        if (slot == 1) { string why; return wh.ObjectTransaction(1, t, now, out why); }
        return Propose(wh, slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() }, now, pid);
    }

    static WorldHost Host(int hostRegion, int p2Region)
    {
        var wh = new WorldHost(47);
        wh.SetPlayerRegion(1, hostRegion, 0); wh.SetPlayerRegion(2, p2Region, 0);
        return wh;
    }

    static byte Committed(WorldHost wh, uint hash, int ns = Madison) { ObjectState s; return wh.Objects.TryGet(ObjectKind.Breakable, ns, hash, out s) ? s.State : (byte)0; }
    static ushort Ver(WorldHost wh, uint hash, int ns = Madison) { ObjectState s; return wh.Objects.TryGet(ObjectKind.Breakable, ns, hash, out s) ? s.Ver : (ushort)0; }

    // ---------------------------------------------------------------- simulated PC (one scene instance of one region)
    // Mirrors WorldObjectSync.AllowBreak / ResumeBreak / Apply(Breakable) and WorldObjectBridge.Shatter on plain values.
    sealed class Obj { public bool Destroyed; public bool Passable; public int Fragments, Sounds, Alerts, Missions; public bool Mission; }

    sealed class Pc
    {
        public readonly byte Slot; public readonly StateRegistry Reg; public readonly int Ns;
        readonly WorldHost _wh; int _sent;
        public readonly ObjectPageCache Com = new ObjectPageCache();
        public readonly Dictionary<uint, Obj> Objs = new Dictionary<uint, Obj>();
        public bool Ready = true;
        // client: proposals in flight (pid -> request) and the objects waiting for the host's answer
        public readonly Dictionary<uint, ObjectTxnRequest> InFlight = new Dictionary<uint, ObjectTxnRequest>();
        public readonly Dictionary<uint, uint> Waiting = new Dictionary<uint, uint>();   // pid -> hash
        public int Refused, Proposals;
        readonly string[] _keys = new string[ObjectStateRules.MaxPages];

        public Pc(WorldHost wh, byte slot, int ns, params uint[] objects)
        {
            _wh = wh; Slot = slot; Ns = ns;
            for (int p = 0; p < _keys.Length; p++) _keys[p] = ObjectStateRules.RegistryKey(ObjectKind.Breakable, p);
            foreach (var h in objects) Objs[h] = new Obj { Mission = h == AcMission };
            var w = new NetDataWriter(); StateMessages.WriteSnapshot(w, wh.Reg);
            var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep, hd; List<StateEntry> entries;
            Expect(StateMessages.TryReadSnapshot(r, out ep, out hd, out entries), "snapshot");
            Reg = StateRegistry.CreateClient(); Reg.LoadSnapshot(ep, hd, entries);
            _sent = wh.Outbox.Count;
        }

        bool PendingFor(uint hash) { foreach (var t in InFlight.Values) if (t.Hash == hash) return true; return false; }
        byte Com_(uint h) { ObjectState s; return Com.TryGet(h, out s) ? s.State : (byte)0; }
        ushort ComVer(uint h) { ObjectState s; return Com.TryGet(h, out s) ? s.Ver : (ushort)0; }

        // original Destroy_Thing (actor): one-shot guard, fragments + sound, NPC alert around THIS player, mission if flagged
        void Original(Obj o) { if (o.Destroyed) return; o.Destroyed = true; o.Passable = true; o.Fragments++; o.Sounds++; o.Alerts++; if (o.Mission) o.Missions++; }
        // WorldObjectBridge.Shatter: same physical result, no alert, no mission; silent = sound stopped
        void Shatter(Obj o, bool silent) { if (o.Destroyed) return; o.Destroyed = true; o.Passable = true; o.Fragments++; if (!silent) o.Sounds++; }

        // A crowbar hit reaching DestroyObject.Destroy_Thing on this PC. Returns what happened.
        public string Hit(uint h, double now)
        {
            var o = Objs[h];
            if (o.Destroyed) return "already";
            if (Slot != 1 && o.Mission) { Refused++; return "story"; }   // StoryBlocked (client prefix)
            bool waiting = false; foreach (var x in Waiting.Values) if (x == h) waiting = true;
            var g = WorldBreakRules.Gate(Slot == 1, Ready, Com_(h) != 0, waiting || PendingFor(h));
            switch (g)
            {
                case BreakGate.Refuse: Refused++; return "refused";
                case BreakGate.ApplyCommitted: Refused++; Apply(h, false); return "replica";
                case BreakGate.CommitThenRun:
                {
                    string why; var code = _wh.ObjectTransaction(1, new ObjectTxnRequest { Txn = _pid++, Op = ObjectOp.Set, Kind = B, Ns = Ns, Hash = h, State = 1, BaseVer = ComVer(h), Epoch = _wh.Objects.EpochOf(Ns) }, now, out why);
                    if (code != ProposalCode.Accepted) { Refused++; return "host " + code; }
                    Original(o); return "broken";
                }
                default:
                {
                    var t = new ObjectTxnRequest { Txn = _pid, Op = ObjectOp.Set, Kind = B, Ns = Ns, Hash = h, State = 1, BaseVer = ComVer(h), Epoch = _wh.Objects.EpochOf(Ns) };
                    uint pid = _pid++;
                    InFlight[pid] = t; Waiting[pid] = h; Proposals++;
                    return "proposed";
                }
            }
        }

        // delivers one in-flight proposal to the host and handles the result like WorldObjectSync.OnResult
        public ProposalCode Deliver(uint pid, double now, bool resend = false)
        {
            var t = InFlight[pid];
            var code = Propose(_wh, Slot, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = t.ToBytes() }, now, pid);
            if (resend) return code;
            Resolve(pid, code);
            return code;
        }
        // the host's answer to one of our proposals reaches this PC
        public void Resolve(uint pid, ProposalCode code)
        {
            InFlight.Remove(pid);
            uint h; bool wait = Waiting.TryGetValue(pid, out h); Waiting.Remove(pid);
            if (wait && code == ProposalCode.Accepted) Original(Objs[h]);   // ResumeBreak
            else if (wait) { Refused++; Sync(); Apply(h, false); }           // refused -> committed state restored by the reconcile
        }
        public ProposalCode DeliverAll(double now) { var last = ProposalCode.Invalid; foreach (var pid in new List<uint>(InFlight.Keys)) last = Deliver(pid, now); return last; }

        public int Sync(bool silent = false)
        {
            for (; _sent < _wh.Outbox.Count; _sent++)
            {
                var w = new NetDataWriter(); StateMessages.WriteDelta(w, _wh.Reg.Epoch, _wh.Outbox[_sent]);
                var r = new NetDataReader(w.CopyData()); r.GetByte(); uint ep; StateEntry e;
                Expect(StateMessages.TryReadDelta(r, out ep, out e), "delta");
                Reg.ApplyDelta(ep, e);
            }
            var changed = Com.Refresh(p => Reg.Get(StateScope.Region, Ns, _keys[p]));
            int n = 0;
            if (changed != null) foreach (var h in changed) if (Objs.ContainsKey(h)) n += Apply(h, silent) ? 1 : 0;
            return n;
        }
        // full reconcile on region ready (silent)
        public int ReadyFull() { int n = Sync(true); foreach (var h in Objs.Keys) n += Apply(h, true) ? 1 : 0; return n; }

        public bool Apply(uint h, bool silent)
        {
            var o = Objs[h];
            var a = WorldBreakRules.Decide(Com_(h), o.Destroyed, PendingFor(h));
            if (a == BreakApply.Shatter) { Shatter(o, silent); return true; }
            if (a == BreakApply.Diverged) Diverged++;
            return false;
        }
        public int Diverged;
        public int Alerts { get { int n = 0; foreach (var o in Objs.Values) n += o.Alerts; return n; } }
        public uint LastPid { get { uint m = 0; foreach (var k in InFlight.Keys) if (k > m) m = k; return m; } }
    }

    static WorldHost ReadyMadison(out Pc host, out Pc p2, params uint[] objects)
    {
        var wh = Host(Madison, Madison);
        Expect(Scene(wh, 1, Madison, 1, 0) == ProposalCode.Accepted && Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0.1) == ProposalCode.Accepted, "host initializes Madison");
        Expect(Scene(wh, 2, Madison, 2, 0.2) == ProposalCode.Accepted, "P2 joins the epoch");
        host = new Pc(wh, 1, Madison, objects); p2 = new Pc(wh, 2, Madison, objects);
        return wh;
    }

    // ---------------------------------------------------------------- 01
    static void Rules()
    {
        Expect((byte)ObjectKind.Breakable == 26 && ObjectKinds.Get(ObjectKind.Breakable) != null && ObjectKinds.Get(ObjectKind.Breakable).TypeName == "DestroyObject"
            && ObjectKinds.Get(ObjectKind.Breakable).Category == IdentityCategory.Static, "census kind Breakable = original DestroyObject, static identity");
        Expect(ObjectStateRules.SupportedKind(ObjectKind.Breakable) && ObjectStateRules.Lifetime(ObjectKind.Breakable) == ObjectLifetime.RegionLife, "shared, region lifetime (no save key)");
        Expect(ObjectStateRules.SupportedNamespace(ObjectKind.Breakable, Madison) && ObjectStateRules.SupportedNamespace(ObjectKind.Breakable, Hillville) && ObjectStateRules.SupportedNamespace(ObjectKind.Breakable, Hideout)
            && !ObjectStateRules.SupportedNamespace(ObjectKind.Breakable, 92), "same regions as doors (Madison, Hillville, hideout)");
        Expect(ObjectStateRules.ValidState(ObjectKind.Breakable, 0) && ObjectStateRules.ValidState(ObjectKind.Breakable, 1) && !ObjectStateRules.ValidState(ObjectKind.Breakable, 2) && !ObjectStateRules.ValidState(ObjectKind.Breakable, 255), "one bit");
        Expect(ObjectStateRules.CheckTransition(ObjectKind.Breakable, 0, 1, ObjectOp.Set) == null && ObjectStateRules.CheckTransition(ObjectKind.Breakable, 1, 0, ObjectOp.Set) != null, "intact -> destroyed only (terminal)");
        Expect(!ObjectStateRules.Reservable(ObjectKind.Breakable) && !ObjectStateRules.Instant(ObjectKind.Breakable, 0, 1), "no reservation, never cancels one");
        ObjectKind k; int pg;
        Expect(ObjectStateRules.RegistryKey(ObjectKind.Breakable) == "wobj26" && ObjectStateRules.RegistryKey(ObjectKind.Breakable, 3) == "wobj26.3"
            && ObjectStateRules.TryKindOfKey("wobj26.3", out k, out pg) && k == ObjectKind.Breakable && pg == 3, "registry keys wobj26[.page]");
        Expect(IdentityHash.ForObject("MadisonSt", "DOMY/Dom 102/PLANK DESTROYABLE/plywood_001a", ObjectKind.Breakable) != IdentityHash.ForObject("MadisonSt", "DOMY/Dom 102/DESTROYABLE PLANKS/Destroy", ObjectKind.Breakable)
            && IdentityHash.ForObject("MadisonSt", "DOMY/Dom 102/DESTROYABLE PLANKS/Destroy", ObjectKind.Breakable) != IdentityHash.ForObject("MadisonSt", "DOMY/Dom 102/DESTROYABLE PLANKS/Destroy", ObjectKind.Door), "the two House 102 stages have distinct ids");
        // apply decision
        Expect(WorldBreakRules.Decide(1, false, false) == BreakApply.Shatter && WorldBreakRules.Decide(1, false, true) == BreakApply.SkipPending && WorldBreakRules.Decide(1, true, false) == BreakApply.None
            && WorldBreakRules.Decide(0, false, false) == BreakApply.None && WorldBreakRules.Decide(0, true, false) == BreakApply.Diverged, "apply table");
        // prefix gate
        Expect(WorldBreakRules.Gate(true, false, false, false) == BreakGate.Refuse && WorldBreakRules.Gate(false, false, false, false) == BreakGate.Refuse, "not ready: nothing happens (both roles)");
        Expect(WorldBreakRules.Gate(true, true, true, false) == BreakGate.ApplyCommitted && WorldBreakRules.Gate(false, true, true, true) == BreakGate.ApplyCommitted, "committed: replica, never the original again");
        Expect(WorldBreakRules.Gate(true, true, false, false) == BreakGate.CommitThenRun && WorldBreakRules.Gate(false, true, false, false) == BreakGate.ProposeThenRun && WorldBreakRules.Gate(false, true, false, true) == BreakGate.Refuse, "host commits in place, P2 proposes once");
        // wire round trip
        var t = new ObjectTxnRequest { Txn = 9, Op = ObjectOp.Set, Kind = B, Ns = Madison, Hash = Planks, State = 1, BaseVer = 0, Epoch = 3 };
        ObjectTxnRequest back; Expect(ObjectTxnRequest.TryParse(t.ToBytes(), out back) && back.Kind == B && back.Hash == Planks && back.State == 1 && back.Epoch == 3, "objTxn carries the new kind unchanged");
        Expect(ProtocolInfo.ProtocolVersion == 39 && ProtocolInfo.ModVersion == "0.28.0", "protocol 35 / mod 0.24.0 (new kind: both PCs need 0.24.0)");
    }

    // ---------------------------------------------------------------- 02
    static void House102()
    {
        Pc host, p2; var wh = ReadyMadison(out host, out p2, Plywood, Planks);
        // stage 1 by Player 2: nothing breaks before the host answered
        Expect(p2.Hit(Plywood, 1) == "proposed" && !p2.Objs[Plywood].Destroyed, "P2 swing: no irreversible local break before authorization");
        Expect(p2.DeliverAll(1.05) == ProposalCode.Accepted && p2.Objs[Plywood].Destroyed && p2.Objs[Plywood].Alerts == 1, "accepted: original on P2 (actor alert once)");
        host.Sync(); p2.Sync();
        Expect(host.Objs[Plywood].Destroyed && host.Objs[Plywood].Alerts == 0 && host.Objs[Plywood].Sounds == 1, "host: replica with the original sound, no NPC alert");
        Expect(!host.Objs[Planks].Destroyed && !p2.Objs[Planks].Destroyed, "stage 2 is a separate object: still intact on both");
        // stage 2 by the host
        Expect(host.Hit(Planks, 2) == "broken" && host.Objs[Planks].Alerts == 1, "host breaks the planks in place (actor alert)");
        p2.Sync(); host.Sync();
        Expect(p2.Objs[Planks].Destroyed && p2.Objs[Planks].Passable && host.Objs[Planks].Passable && p2.Objs[Planks].Alerts == 0, "opening passable on both PCs, no replayed alert");
        Expect(Ver(wh, Plywood) == 1 && Ver(wh, Planks) == 1 && Committed(wh, Plywood) == 1 && Committed(wh, Planks) == 1, "each stage committed exactly once");
        Expect(host.Alerts + p2.Alerts == 2 && host.Diverged == 0 && p2.Diverged == 0, "two actor alerts in total (one per stage), no divergence");
    }

    // ---------------------------------------------------------------- 03
    static void EitherPlayer()
    {
        Pc host, p2; var wh = ReadyMadison(out host, out p2, Plywood, Planks, Toilet, Chair);
        Expect(p2.Hit(Plywood, 1) == "proposed" && p2.DeliverAll(1) == ProposalCode.Accepted, "P2 stage 1");
        Expect(p2.Hit(Planks, 1.5) == "proposed" && p2.DeliverAll(1.5) == ProposalCode.Accepted, "P2 stage 2");
        host.Sync();
        Expect(host.Objs[Plywood].Destroyed && host.Objs[Planks].Destroyed && host.Alerts == 0 && p2.Alerts == 2, "P2 breaks both stages; host shows both replicas");
        Pc h2, q2; var wh2 = ReadyMadison(out h2, out q2, Plywood, Planks);
        Expect(h2.Hit(Planks, 1) == "broken" && h2.Hit(Plywood, 1.2) == "broken", "host breaks both (planks first: the IL forces no order, [Q] reach through the sheet)");
        q2.Sync();
        Expect(q2.Objs[Plywood].Destroyed && q2.Objs[Planks].Destroyed && q2.Alerts == 0, "P2 shows both replicas");
        // ordinary furniture / toilet: same kind, same path
        Expect(host.Hit(Toilet, 3) == "broken" && p2.Hit(Chair, 3) == "proposed" && p2.DeliverAll(3) == ProposalCode.Accepted, "toilet by host, chair by P2");
        host.Sync(); p2.Sync();
        Expect(p2.Objs[Toilet].Destroyed && host.Objs[Chair].Destroyed && Committed(wh, Toilet) == 1 && Committed(wh, Chair) == 1, "furniture shared both ways");
    }

    // ---------------------------------------------------------------- 04
    static void ExactlyOnce()
    {
        Pc host, p2; var wh = ReadyMadison(out host, out p2, Plywood, Toilet);
        Expect(p2.Hit(Plywood, 1) == "proposed", "first swing proposes");
        uint pid = p2.LastPid;
        Expect(p2.Hit(Plywood, 1.01) == "refused" && p2.Hit(Plywood, 1.02) == "refused" && p2.Proposals == 1, "further swings during the round trip: no second proposal");
        var copy = p2.InFlight[pid];
        Expect(p2.Deliver(pid, 1.1, true) == ProposalCode.Accepted, "delivered once");
        // the same pid again (retransmission / duplicated packet) is answered from the cache: no second commit
        Expect(Propose(wh, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = copy.ToBytes() }, 1.2, pid) == ProposalCode.Accepted && Ver(wh, Plywood) == 1, "retransmitted proposal: cached answer, version stays 1");
        // a NEW proposal for the same object with the old base version: Unchanged (already destroyed), never a new version
        Expect(Send(wh, 2, Req(wh, Plywood, 1, 0), 1.3) == ProposalCode.Unchanged && Ver(wh, Plywood) == 1, "duplicated event: Unchanged");
        Expect(Send(wh, 1, Req(wh, Plywood, 1, 1), 1.4) == ProposalCode.Unchanged && Ver(wh, Plywood) == 1, "host duplicate: Unchanged");
        p2.Resolve(pid, ProposalCode.Accepted);   // the (single) answer reaches Player 2
        Expect(p2.Objs[Plywood].Alerts == 1 && p2.Objs[Plywood].Fragments == 1, "P2 ran the original once");
        host.Sync(); p2.Sync(); host.Sync();
        Expect(host.Objs[Plywood].Fragments == 1 && host.Objs[Plywood].Alerts == 0, "host replica once");
        // a later hit on the broken object: the original's own guard / no effects
        Expect(host.Hit(Plywood, 2) == "already" && p2.Hit(Plywood, 2) == "already" && host.Objs[Plywood].Fragments == 1 && p2.Objs[Plywood].Fragments == 1, "hits on broken objects do nothing");
        // host double hit (two detected objects in one swing reach the same component twice)
        Expect(host.Hit(Toilet, 3) == "broken" && host.Hit(Toilet, 3) == "already" && Ver(wh, Toilet) == 1 && host.Objs[Toilet].Alerts == 1, "host: one commit, one alert");
    }

    // ---------------------------------------------------------------- 05
    static void Concurrent()
    {
        // (a) both swing in the same moment; the host's in-place commit is decided first
        Pc host, p2; var wh = ReadyMadison(out host, out p2, Planks);
        Expect(p2.Hit(Planks, 1) == "proposed", "P2 proposes");
        Expect(host.Hit(Planks, 1.001) == "broken", "host commits first");
        Expect(p2.DeliverAll(1.02) == ProposalCode.Unchanged, "P2 loses: Unchanged");
        Expect(p2.Objs[Planks].Destroyed && p2.Objs[Planks].Alerts == 0 && host.Objs[Planks].Alerts == 1 && Ver(wh, Planks) == 1, "loser shows the replica without its own alert; exactly one commit");
        // (b) P2's proposal arrives first; the host's swing then sees the committed state
        Pc h2, q2; var wh2 = ReadyMadison(out h2, out q2, Planks);
        Expect(q2.Hit(Planks, 1) == "proposed" && q2.DeliverAll(1.01) == ProposalCode.Accepted, "P2 wins");
        // the host's prefix still sees intact (its reconcile did not run yet): the in-place commit is Unchanged -> no original
        Expect(h2.Hit(Planks, 1.011) == "host Unchanged" && !h2.Objs[Planks].Destroyed && h2.Objs[Planks].Alerts == 0 && Ver(wh2, Planks) == 1, "host swing during the race: no original, no second commit");
        h2.Sync();
        Expect(h2.Objs[Planks].Destroyed && h2.Objs[Planks].Fragments == 1 && h2.Objs[Planks].Alerts == 0, "host replica once (no alert)");
        // (c) after its sync the host's swing finds the broken object; P2's alert stays the only one
        Expect(h2.Hit(Planks, 1.5) == "already" && q2.Alerts == 1 && h2.Alerts == 0, "one actor alert in total");
        // (d) reordered: a P2 proposal with a base version that is not current is never applied twice
        Expect(Send(wh2, 2, Req(wh2, Planks, 1, 7), 1.6) == ProposalCode.Unchanged && Ver(wh2, Planks) == 1, "reordered duplicate: Unchanged");
        // (e) committed in this PC's view but not drawn yet (apply pending): the prefix takes the replica path
        Expect(WorldBreakRules.Gate(false, true, true, false) == BreakGate.ApplyCommitted && WorldBreakRules.Gate(true, true, true, false) == BreakGate.ApplyCommitted, "committed view: replica, never the original");
    }

    // ---------------------------------------------------------------- 06
    static void Stale()
    {
        Pc host, p2; var wh = ReadyMadison(out host, out p2, Plywood);
        ushort epoch = wh.Objects.EpochOf(Madison);
        Expect(Send(wh, 2, Req(wh, Plywood, 1, 0, Madison, ObjectOp.Set, epoch + 5), 1) == ProposalCode.Stale, "wrong / old epoch: Stale");
        Expect(Send(wh, 2, Req(wh, Plywood, 1, 0, Hillville), 1) == ProposalCode.NotAuthorized, "wrong region: refused");
        Expect(Send(wh, 2, Req(wh, Plywood, 2, 0), 1) == ProposalCode.Invalid, "invalid state 2");
        Expect(Send(wh, 2, Req(wh, Plywood, 1, 0, Madison, ObjectOp.NpcSet), 1) != ProposalCode.Accepted, "NPC op never breaks an object");
        Expect(Send(wh, 2, Req(wh, Plywood, 1, 0, Madison, ObjectOp.Reserve), 1) == ProposalCode.Invalid, "not reservable");
        Expect(Committed(wh, Plywood) == 0, "nothing committed by refused events");
        Expect(Send(wh, 2, Req(wh, Plywood, 1, 0), 2) == ProposalCode.Accepted, "valid break");
        Expect(Send(wh, 1, Req(wh, Plywood, 0, 1), 2.5) == ProposalCode.Invalid && Committed(wh, Plywood) == 1, "revert to intact refused (host too): terminal for the epoch");
        // an event of the previous epoch after a reload never reaches the new epoch
        wh.SetPlayerRegion(1, Hillville, 3); wh.SetPlayerRegion(2, Hillville, 3);
        wh.Tick(3 + ObjectStateRules.MemberGraceSeconds + 0.1);
        wh.SetPlayerRegion(2, Madison, 10); Scene(wh, 2, Madison, 9, 10);
        Expect(Send(wh, 2, Req(wh, Plywood, 1, 0, Madison, ObjectOp.Set, epoch), 10.1) == ProposalCode.Stale, "old-epoch break after the reload: Stale");
        // non-member (P1 not in Madison any more)
        Expect(Send(wh, 1, Req(wh, Plywood, 1, 0), 10.2) == ProposalCode.NotAuthorized, "non-member refused");
    }

    // ---------------------------------------------------------------- 07
    static void LateJoin()
    {
        var wh = Host(Madison, Hideout);
        Scene(wh, 1, Madison, 1, 0); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 0.1);
        var host = new Pc(wh, 1, Madison, Plywood, Planks, Toilet);
        Expect(host.Hit(Plywood, 1) == "broken" && host.Hit(Toilet, 1.2) == "broken", "host breaks stage 1 + a toilet alone");
        // Player 2 arrives later: snapshot + full silent reconcile
        wh.SetPlayerRegion(2, Madison, 5);
        Expect(Scene(wh, 2, Madison, 7, 5) == ProposalCode.Accepted, "P2 joins the running epoch");
        var p2 = new Pc(wh, 2, Madison, Plywood, Planks, Toilet);
        Expect(p2.ReadyFull() == 2, "two objects applied on region ready");
        Expect(p2.Objs[Plywood].Destroyed && p2.Objs[Toilet].Destroyed && !p2.Objs[Planks].Destroyed, "late joiner sees exactly the region state (stage 2 still intact)");
        Expect(p2.Objs[Plywood].Sounds == 0 && p2.Objs[Toilet].Sounds == 0 && p2.Alerts == 0, "silent: no sound, no NPC alert");
        Expect(p2.Hit(Planks, 6) == "proposed" && p2.DeliverAll(6) == ProposalCode.Accepted, "late joiner breaks stage 2");
        host.Sync();
        Expect(host.Objs[Planks].Destroyed && host.Objs[Planks].Sounds == 1, "host replica audible (live event)");
    }

    // ---------------------------------------------------------------- 08
    static void Reload()
    {
        Pc host, p2; var wh = ReadyMadison(out host, out p2, Plywood, Planks);
        host.Hit(Plywood, 1); p2.Hit(Planks, 1); p2.DeliverAll(1);
        ushort epoch = wh.Objects.EpochOf(Madison);
        Expect(Committed(wh, Plywood) == 1 && Committed(wh, Planks) == 1, "both stages broken");
        // one player leaves: the region keeps its state for the remaining player (that instance did not reload)
        wh.SetPlayerRegion(2, Hideout, 2); wh.Tick(2 + ObjectStateRules.MemberGraceSeconds + 0.1);
        Expect(Committed(wh, Plywood) == 1 && wh.Objects.EpochOf(Madison) == epoch, "host still there: state kept");
        // P2 comes back (fresh scene load on P2): reconciles to the committed state
        wh.SetPlayerRegion(2, Madison, 10);
        Expect(Scene(wh, 2, Madison, 11, 10) == ProposalCode.Accepted && wh.Objects.EpochOf(Madison) == epoch, "same epoch");
        var p2b = new Pc(wh, 2, Madison, Plywood, Planks);
        Expect(p2b.ReadyFull() == 2 && p2b.Objs[Planks].Passable && p2b.Alerts == 0, "returning P2 sees both stages broken (passage open), silently");
        // both leave: the original reload resets the barrier
        wh.SetPlayerRegion(1, Hideout, 20); wh.SetPlayerRegion(2, Hideout, 20); wh.Tick(20 + ObjectStateRules.MemberGraceSeconds + 0.1);
        Expect(wh.Objects.EpochOf(Madison) == 0 && Committed(wh, Plywood) == 0 && Committed(wh, Planks) == 0, "last member gone: intact again (vanilla reload)");
        wh.SetPlayerRegion(1, Madison, 30); Scene(wh, 1, Madison, 12, 30); Init(wh, 1, Madison, new List<ObjectInitEntry>(), 30.1);
        Expect(Committed(wh, Plywood) == 0 && wh.Objects.EpochOf(Madison) == epoch + 1, "new epoch starts intact");
        Expect(Send(wh, 1, Req(wh, Plywood, 1, 0), 31) == ProposalCode.Accepted, "breakable again after the reset");
        // alone reload of a running region resets as well
        Expect(Scene(wh, 1, Madison, 13, 32) == ProposalCode.Accepted && wh.Objects.EpochOf(Madison) == epoch + 2 && Committed(wh, Plywood) == 0, "alone reload = new epoch, intact");
        // disconnect of the last member
        wh.SetPlayerRegion(2, Madison, 40); Scene(wh, 2, Madison, 14, 40); wh.SetPlayerRegion(1, Hideout, 40); wh.Tick(40 + ObjectStateRules.MemberGraceSeconds + 0.1);
        wh.PlayerDisconnected(2, 50);
        Expect(wh.Objects.EpochOf(Madison) == 0, "P2 disconnects alone in Madison: epoch ends");
        // never persisted: the session registry is the only store; no save-key path for the kind
        Expect(ObjectStateRules.Lifetime(ObjectKind.Breakable) == ObjectLifetime.RegionLife, "region lifetime");
    }

    // ---------------------------------------------------------------- 09
    static void Mission()
    {
        Pc host, p2; var wh = ReadyMadison(out host, out p2, AcMission, Toilet);
        wh.Objects.Restricted = (k, ns, h) => k == ObjectKind.Breakable && h == AcMission;
        Expect(p2.Hit(AcMission, 1) == "story" && p2.Proposals == 0 && !p2.Objs[AcMission].Destroyed, "P2 prefix refuses the mission object locally (nothing breaks, nothing sent)");
        Expect(Send(wh, 2, Req(wh, AcMission, 1, 0), 1.1) == ProposalCode.NotAuthorized && Committed(wh, AcMission) == 0, "host refuses Player 2 for a restricted breakable");
        Expect(host.Hit(AcMission, 2) == "broken" && host.Objs[AcMission].Missions == 1, "host keeps the original (its own campaign mission completion, single-player rule)");
        p2.Sync();
        Expect(p2.Objs[AcMission].Destroyed && p2.Objs[AcMission].Missions == 0 && p2.Objs[AcMission].Alerts == 0, "P2 replica: broken, no mission completion, no alert");
        Expect(p2.Hit(Toilet, 3) == "proposed" && p2.DeliverAll(3) == ProposalCode.Accepted, "ordinary breakables stay open for Player 2");
    }

    // ---------------------------------------------------------------- 10
    static void InitAndCapacity()
    {
        // P2 initializes a region whose scene already has a broken object (broken before the session / while offline)
        var wh = Host(Hideout, Hillville);
        Expect(Scene(wh, 2, Hillville, 3, 0) == ProposalCode.Accepted, "P2 alone in Hillville");
        var all = new List<ObjectInitEntry> { new ObjectInitEntry { Kind = B, Hash = Chair, State = 1 }, new ObjectInitEntry { Kind = D, Hash = 0x55, State = 1 } };
        Expect(Init(wh, 2, Hillville, all, 0.1) == ProposalCode.Accepted && Committed(wh, Chair, Hillville) == 1, "pre-broken chair committed by the initializer");
        wh.SetPlayerRegion(1, Hillville, 1); Scene(wh, 1, Hillville, 4, 1);
        var host = new Pc(wh, 1, Hillville, Chair);
        Expect(host.ReadyFull() == 1 && host.Objs[Chair].Destroyed && host.Alerts == 0, "host arriving later: silent replica");
        // an initial state entry that is invalid for the kind is skipped
        var wh2 = Host(Madison, Madison); Scene(wh2, 1, Madison, 1, 0);
        Expect(Init(wh2, 1, Madison, new List<ObjectInitEntry> { new ObjectInitEntry { Kind = B, Hash = Toilet, State = 9 } }, 0.1) == ProposalCode.Accepted && Committed(wh2, Toilet) == 0, "invalid init state skipped");
        // Madison has 64 DestroyObject instances: far below the 1200 per kind
        Pc h, p; var wh3 = ReadyMadison(out h, out p);
        int ok = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
        for (uint i = 0; i < 64; i++) if (Send(wh3, 1, Req(wh3, 0x9000 + i, 1, 0), 1 + i * 0.2) == ProposalCode.Accepted) ok++;
        sw.Stop();
        Console.WriteLine("      host break decision incl. registry page commit: " + (sw.Elapsed.TotalMilliseconds * 1000.0 / 64).ToString("0.00") + " us per object (offline, 64 objects); page " + wh3.Reg.Get(StateScope.Region, Madison, ObjectStateRules.RegistryKey(ObjectKind.Breakable)).Value.Length + " B");
        Expect(ok == 64, "all 64 Madison breakables committable (rate limit respected at 5/s)");
        int burst = 0;
        for (uint i = 0; i < 12; i++) if (Send(wh3, 2, Req(wh3, 0xA000 + i, 1, 0), 100) == ProposalCode.Accepted) burst++;
        Expect(burst == ObjectStateRules.RatePerSecond, "per-player rate limit 8/s: a 12-object burst commits 8 (the rest: nothing breaks, Busy)");
        // regions are independent
        Expect(Committed(wh3, 0x9000, Hillville) == 0, "Madison commits do not touch Hillville");
    }

    // ---------------------------------------------------------------- 11
    static void Glue()
    {
        var sync = Src("WorldObjectSync.cs"); var code = StripComments(sync);
        var bridge = Src("WorldObjectBridge.cs"); var bcode = StripComments(bridge);
        var census = StripComments(Src("WorldCensus.cs"));
        Expect(code.Contains("AccessTools.Method(_b.BreakT, \"Destroy_Thing\")") && code.Contains("_h.Patch(_destroyThing, prefix: hm(\"DestroyPre\"))"), "prefix on the original DestroyObject.Destroy_Thing");
        Expect(code.Contains("k == ObjectKind.Breakable ? 2 : k == ObjectKind.ParcelLocker ? 3 : k == ObjectKind.SecurityCamera ? 5 : 4") && code.Contains("ObjectKind.Breakable, ObjectKind.ParcelLocker, ObjectKind.Door, ObjectKind.SecurityCamera };"), "apply order locks -> windows -> breakables -> parcel lockers (W2B) -> doors -> cameras (W6B); KI = Kinds index");
        Expect(code.Contains("WorldBreakRules.Gate(_host, _phase == Phase.Ready,") && code.Contains("return _lastHostCode == ProposalCode.Accepted;"), "host: original only when the in-place commit was accepted");
        Expect(code.Contains("if (pid != 0) _breakWait[pid] = o;") && sync.Contains("return false;   // ResumeBreak runs the original once the host accepted"), "Player 2: no local break before the host accepted");
        Expect(code.Contains("if (_lastHostCode == ProposalCode.Unchanged) { _dirty.Add(key); _retryAt = Now; }"), "host lost the race: replica via the reconcile, not the original");
        Expect(code.Contains("if (breakWait && code == ProposalCode.Accepted) ResumeBreak(breakObj, key);") && sync.Contains("_bypass = true;\n            try { _destroyThing.Invoke(o, null); }".Replace("\n", Nl(sync))), "accepted: the original runs once through the bypass");
        Expect(code.Contains("return i._host; }"), "hook exception: Player 2 fails closed (never an unauthorized irreversible break)");
        Expect(code.Contains("if (StoryBlocked(ObjectKind.Breakable, o, \"breakable\")) return false;"), "mission breakables blocked for Player 2 in the prefix");
        Expect(code.Contains("var a = WorldBreakRules.Decide(want, _b.Destroyed(c), pending);") && code.Contains("_b.Shatter(c, silent);"), "committed state applied only through the bridge");
        Expect(code.Contains("case ObjectKind.Breakable: return _b.BreakOk && _b.Destroyed(c) ? ObjectStateRules.BreakDestroyed : ObjectStateRules.BreakIntact;"), "initializer reports locally broken objects");
        Expect(code.Contains("_breakWait.Clear();"), "waiting breaks dropped on region change (stale results never break a new scene)");
        foreach (var bad in new[] { "AlertNPC", "Mission_Complete", "MakeNoise", "SetActive(", "Destroy(" })
            Expect(code.IndexOf(bad, StringComparison.Ordinal) < 0, "sync never calls " + bad);
        // bridge mirrors Destroy_Thing IL_000b..IL_00d7 minus PlayerScript lookup, alert and mission completion
        int sh = bcode.IndexOf("public void Shatter(", StringComparison.Ordinal);
        Expect(sh > 0, "Shatter present");
        string body = bcode.Substring(sh, bcode.IndexOf("void OpenSash(", sh, StringComparison.Ordinal) - sh);
        foreach (var x in new[] { "if (_doDestroyed(o)) return;", "_doSetDestroyed(o, true);", "GetComponent<BoxCollider>()", "GetComponent<MeshCollider>()", "g.SetActive(true);", "if (detach) g.transform.parent = null;", "if (g != null) g.SetActive(false);", "if (silent) { var a = g.GetComponent<AudioSource>(); if (a != null) a.Stop(); }", "Applying = true;", "finally { Applying = false; }" })
            Expect(body.Contains(x), "Shatter mirrors the original: " + x);
        foreach (var bad in new[] { "AlertNPC", "Mission_Complete", "FindGameObjectWithTag", "PlayerScript", "MakeNoise", "Invoke(" })
            Expect(body.IndexOf(bad, StringComparison.Ordinal) < 0, "replica never: " + bad);
        Expect(bcode.Contains("\"activate_mission_completion\"") && bcode.Contains("if (k == ObjectKind.Breakable) return !BreakOk || _doMission(c)"), "mission flag read live, fail closed");
        Expect(bcode.Contains("BreakOk = BreakT != null &&") && code.Contains("_destroyThing = _b.BreakOk ?"), "breakables optional: doors / locks / windows unaffected when DestroyObject members are missing");
        Expect(census.Contains("static bool Shared(ObjectKind k) { return ObjectStateRules.SupportedKind(k); }") && census.Contains("ObjectKind.LockedWindow, ObjectKind.Breakable, ObjectKind.ParcelLocker, ObjectKind.SecurityCamera })")
            && census.Contains("if (k == ObjectKind.Breakable)") && census.Contains("_doMission == null || _doMission(c)"), "census: breakables shared, strict catalog, mission objects restricted (fail closed)");
        // no new per-frame work: the only entry points are the Harmony prefix and the existing reconcile
        foreach (var f in new[] { code, bcode })
            foreach (var x in new[] { "FindObjectsOfType", "FindObjectOfType", "GameObject.Find", "GetComponentsInChildren", "FindGameObjectsWithTag", "OnGUI", "GUI." })
                Expect(f.IndexOf(x, StringComparison.Ordinal) < 0, "no scene scan / UI: " + x);
    }

    // ---------------------------------------------------------------- source helpers
    static string Nl(string s) { return s.Contains("\r\n") ? "\r\n" : "\n"; }
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
    static string Src(string f) { return File.ReadAllText(Path.Combine(PluginDir(), f)); }
    static string StripComments(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in s.Split('\n')) { int i = line.IndexOf("//", StringComparison.Ordinal); sb.Append(i >= 0 ? line.Substring(0, i) : line).Append('\n'); }
        return sb.ToString();
    }
}
