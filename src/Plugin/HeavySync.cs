// H1 heavy world objects on the ORIGINAL carry system (design + verified original flow: src/Shared/HeavyItems.cs header).
//  - Pickup: WorldLootSync's Pickupable.PickUpHeavy(float) prefix asks AllowPickUpHeavy. Host (Player 1): the lease is decided
//    in place inside the original call (first committed wins) and the original runs at once. Client (Player 2): the original
//    is refused, a "heavyTxn" Lease proposal is sent; on the host's grant the ORIGINAL PickUpHeavy(distance) is invoked exactly
//    once (re-checked against the original interact preconditions; otherwise the lease is released at once).
//  - Carry: the original PlayerScript carry update on the holder; the other instance hides its scene copy (renderers +
//    colliders off, kinematic) and RemoteGear shows a script-free copy of that very object on the remote avatar.
//  - Drop (H1.1): Pickupable.DropHeavy postfix on the holder = the verified release point (DropHeavy only switches the object
//    to dynamic physics; the caller's throw force follows after it returns) -> the release pose is committed AT ONCE (Drop:
//    Placed + Moving). Observers reveal the scene object there in the same frame the record arrives and hide the avatar copy
//    in that frame (CarryShown: the lease record, not the 100 ms-delayed player snapshot, drives the carried copy). While the
//    holder's ORIGINAL physics moves it, bounded HeavyMotion snapshots (<= 20/s, only after a real change, unreliable) are
//    interpolated on the observer's kinematic object (VehiclePoseBuffer); at rest the holder sends the final snapshot +
//    commits the exact pose (Settle) and stops. A later real movement starts a new Drop -> motion -> Settle cycle. No instance
//    simulates an object it does not hold. If the simulator leaves while the object still moves, the host hands it to the
//    other player when that player is in the region (Adopt: its instance continues the original physics from its shown pose).
//    Ordinary TAB drops (WorldItemSync) are a different original operation and keep their own presentation (paused physics).
//  - End of a carry without a drop (scene unloaded / reloaded, object destroyed): the lease is released -> previous rest
//    pose or the original scene pose. Region change / disconnect: the host releases the slot's leases (HeavyBook.ReleaseSlot);
//    an emptied region loses its records (original reload).
//  - Deposits: H2 shares the Ranger cargo bed (HeavyCargoSync): an object of this instance that comes to rest in the original
//    cargo sensor commits its rest pose and asks the host to Load it into the car (both players); a cargo object picked up
//    again is leased under its synthetic id (HeavyCargo.Synthetic) and follows every H1 rule. Player 2's drop next to the
//    Ranger is the original drop; next to any other car it stays refused. Trunk / shop triggers (single trunk slot, heavy
//    shop) stay H1: blocked for Player 2, Player 1's original deposit leaves shared tracking (Removed). The associate's heavy
//    carry (CodenameAI.PickHeavy) is refused for supported objects in a session (it would move one instance's copy only).
// Harmony (this class): Pickupable.DropHeavy (post), Pickupable.PlacedInTrunk (post, host), Place_Heavy_Trigger.OnTriggerEnter /
// Shop_Heavy_Trigger.OnTriggerEnter (pre, client), PlayerScript.StopCarryingItem(float) (pre, client), CodenameAI.PickHeavy (pre).
// Per frame: field reads only; the registry is parsed only when its head revision / region / scene changes; held objects are
// checked 5x/s while falling and 2x/s at rest; moving held objects: one rate/epsilon check per frame (VehicleSendPolicy, no
// allocation); followed objects: one buffer sample per frame. No scene-wide searches.
using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class HeavySync : IDisposable
    {
        public static HeavySync Instance;

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        Func<object, object> _gmPs, _psCarryItem, _psPmb; Func<object, bool> _psCarrying, _psInVehicle, _psDropBlock;
        Func<object, int> _pickId; Action<object, float> _pickUpHeavy, _stopCarry;
        Type _pickT, _carT, _enterCarT;

        // presentation of the census objects of the local region that have (or had) a record
        sealed class Obj
        {
            public uint Hash; public int ItemId; public Component Pick; public GameObject Go; public Rigidbody Rb;
            public bool Captured; public Transform OrigParent; public Vector3 OrigLocalPos; public Quaternion OrigLocalRot; public Vector3 OrigLocalScale; public bool OrigKin, OrigGrav;
            public readonly List<Renderer> HiddenRenderers = new List<Renderer>(); public readonly List<Collider> HiddenColliders = new List<Collider>();
            public HeavyRules.Show Shown = HeavyRules.Show.ScenePose; public Vector3 ShownPos; public Quaternion ShownRot;
            public bool InterpOff; public RigidbodyInterpolation OrigInterp;   // H1.1: interpolation off while followed (transform-driven kinematic)
            public bool Synthetic;   // H2: a cargo object outside its car (no scene pose: without a record it is hidden, HeavyCargoSync owns it)
        }
        readonly Dictionary<uint, Obj> _objs = new Dictionary<uint, Obj>();
        int _objNs = Regions.None;

        // objects this instance holds a lease for (carrying, or simulating after its own drop). Falling = its original physics
        // moves it (record Moving, snapshots streamed); Resting = settled (record Settle committed).
        enum Phase { Carrying, Falling, Resting }
        sealed class Sim
        {
            public int Ns; public uint Hash; public Component Pick; public Rigidbody Rb; public ushort Lease; public Phase Phase;
            public bool Reported; public Vector3 LastPos; public Quaternion LastRot; public double DropAt, LastTick, UnpausedFor, NextCheck; public string Name;
            public readonly VehicleSendPolicy Policy = new VehicleSendPolicy { RateHz = MotionHz, MoveEpsilon = 0.01f, AngleEpsilon = 1f, HeartbeatSeconds = double.MaxValue };
            public int Sent;
            public bool LoadTried;   // H2: one Load request per rest (a refused Load keeps the H1 rest state)
            public bool Cargo;       // H2: a cargo object outside its car (synthetic id, requests carry the cargo flag)
        }
        readonly Dictionary<uint, Sim> _sims = new Dictionary<uint, Sim>();
        readonly List<uint> _scratch = new List<uint>();
        readonly List<Sim> _iter = new List<Sim>();

        // H1.1 observer: HeavyMotion snapshots of objects the other player's original physics moves (lease-checked)
        const double MotionHz = 20;
        sealed class Motion
        {
            public uint Hash; public ushort Lease; public byte Sender; public Obj Obj; public int Samples; public double Start;
            public readonly VehiclePoseBuffer Buf = new VehiclePoseBuffer();
        }
        readonly Dictionary<uint, Motion> _motions = new Dictionary<uint, Motion>();
        readonly List<uint> _ended = new List<uint>();
        readonly HeavyMotionFilter _filter = new HeavyMotionFilter();
        readonly Dictionary<uint, HeavyRecord> _records = new Dictionary<uint, HeavyRecord>();   // local region, as last reconciled
        readonly NetDataWriter _mw = new NetDataWriter();
        uint _motionSeq;
        long _motionSent, _motionSentBytes, _motionRecv, _motionRecvBytes, _motionMalformed, _motionUnknown, _followed, _adopted;
        double _correctionMax;

        // client: one pending Lease request; other proposals are fire-and-forget (results logged)
        sealed class Req { public uint Id; public int Ns; public uint Hash; public Component Pick; public float Dist; public double At; public string Name; public bool Cargo; }
        Req _req;
        readonly Dictionary<uint, HeavyTxnRequest> _sent = new Dictionary<uint, HeavyTxnRequest>();
        readonly Dictionary<uint, Req> _abandoned = new Dictionary<uint, Req>();
        Component _resume;

        uint _seenHead = uint.MaxValue, _seenEpoch, _shownRev = uint.MaxValue; object _seenGm; bool _dirty = true; int _refreshFrame = -1;
        GameObject _remoteCarryGo; int _remoteCarryItem = -1; uint _remoteCarryHash; int _suppressItem = -1;
        readonly HashSet<int> _logged = new HashSet<int>();

        long _p1Leases, _p2Requests, _p2Granted, _p2Refused, _grantAborted, _drops, _settles, _moves, _releases, _removes, _hidden, _placed, _restored, _dropRefused, _depositBlocked, _associateBlocked, _forcedEnds, _lost;
        long _loadsSent, _loadsDone, _loadsRefused, _cargoPicks;
        double _costMaxMs, _nextStats;
        static double Now { get { return Time.realtimeSinceStartup; } }
        bool Live { get { return _node.IsConnected && _world.Synced; } }
        byte Me { get { return _world.LocalSlot; } }
        byte Other { get { return _world.RemoteSlot; } }

        public HeavySync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); _pickT = AccessTools.TypeByName("Pickupable");
                _carT = AccessTools.TypeByName("CarController"); _enterCarT = AccessTools.TypeByName("EnterCar");
                _gmPs = Fast.Getter<object>(gm, "PS"); _psCarryItem = Fast.Getter<object>(ps, "CarryItem"); _psPmb = Fast.Getter<object>(ps, "PMB");
                _psCarrying = Fast.Getter<bool>(ps, "isCarrying"); _psInVehicle = Fast.Getter<bool>(ps, "is_in_vehicle"); _psDropBlock = Fast.Getter<bool>(ps, "dropBigItemBlock");
                _pickId = Fast.Getter<int>(_pickT, "item_ID"); _pickUpHeavy = Fast.Call<float>(_pickT, "PickUpHeavy"); _stopCarry = Fast.Call<float>(ps, "StopCarryingItem");
                var drop = AccessTools.Method(_pickT, "DropHeavy", Type.EmptyTypes); var trunk = AccessTools.Method(_pickT, "PlacedInTrunk", Type.EmptyTypes);
                var stop = AccessTools.Method(ps, "StopCarryingItem", new[] { typeof(float) });
                if (_gmPs == null || _psCarryItem == null || _psPmb == null || _psCarrying == null || _psInVehicle == null || _psDropBlock == null || _pickId == null || _pickUpHeavy == null || _stopCarry == null
                    || drop == null || trunk == null || stop == null || _carT == null || world.LocalSlot == 0)
                { _log.Warn("HEAVY game API incomplete: heavy-object sync disabled (Player 2 heavy pickups stay refused)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.heavy");
                _h.Patch(drop, null, new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "DropPostfix")));
                if (_host) _h.Patch(trunk, null, new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "TrunkPostfix")));
                else
                {
                    _h.Patch(stop, new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "StopPrefix")));
                    foreach (var tn in new[] { "Place_Heavy_Trigger", "Shop_Heavy_Trigger" })
                    {
                        var t = AccessTools.TypeByName(tn); var m = t != null ? AccessTools.Method(t, "OnTriggerEnter", new[] { typeof(Collider) }) : null;
                        if (m != null) _h.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "DepositPrefix")));
                        else _log.Warn("HEAVY " + tn + ".OnTriggerEnter not found: that deposit is not blocked for Player 2");
                    }
                }
                var cod = AccessTools.TypeByName("CodenameAI"); var pickHeavy = cod != null ? AccessTools.Method(cod, "PickHeavy", new[] { typeof(GameObject) }) : null;
                if (pickHeavy != null) _h.Patch(pickHeavy, new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "AssociatePrefix")));
                else _log.Warn("HEAVY CodenameAI.PickHeavy not found: the associate's heavy carry is not guarded");
                if (_host && world.Host != null) world.Host.HeavyCatalog = HostCatalog;
                if (!_host) world.ProposalResultHook8 = OnResult;
                _node.MessageReceived += OnMessage;
                _ok = true; Instance = this;
                _log.Info("HEAVY heavy-object sync ready (" + (_host ? "host: leases decided here" : "client: pickups as host leases") + "; region census heavy objects, MainScene excluded)");
            }
            catch (Exception ex) { _log.Error("HEAVY init failed: heavy-object sync disabled", ex); _ok = false; }
        }

        object Ps() { var gm = _world.GameManagerObject; return gm != null ? _gmPs(gm) : null; }

        LootCatalog LiveCatalog(int ns)
        {
            var c = WorldCensus.Instance != null ? WorldCensus.Instance.Loot(ns) : null;
            return c != null && c.Live ? c : null;
        }

        // census identity of a heavy object of the local region (null = not supported: MainScene, runtime object, ambiguous)
        LootEntry Resolve(Component pick, out int ns)
        {
            ns = IdentityNamespace.ForScene(pick.gameObject.scene.name);
            if (ns != _world.LocalRegion) return null;
            var cat = LiveCatalog(ns); LootEntry e;
            if (cat == null || !cat.TryByInstance(pick.GetInstanceID(), out e) || !HeavyRules.Supported(ns, e.Flags)) return null;
            return e;
        }

        bool TryRecord(int ns, uint hash, out HeavyRecord r)
        {
            r = default(HeavyRecord);
            var e = _world.Registry.Get(StateScope.Region, ns, WorldKeys.Heavy);
            List<HeavyRecord> l;
            if (e == null || !HeavyList.TryParse(e.Value, out l)) return false;
            foreach (var x in l) if (x.Hash == hash) { r = x; return true; }
            return false;
        }

        void LogOnce(string key, string msg) { if (_logged.Count < 256 && _logged.Add(key.GetHashCode())) _log.Info(msg); }

        // ======================================================================== pickup (from WorldLootSync.HeavyPrefix)
        // true = the original PickUpHeavy runs now; false = nothing happens (a refused original interaction).
        public bool AllowPickUpHeavy(Component pick, float dist)
        {
            if (!_ok || pick == null) return true;
            if (ReferenceEquals(_resume, pick)) { _resume = null; return true; }   // our confirmed Player-2 pickup
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                int ns; var e = Resolve(pick, out ns);
                string name = e != null ? e.Name : pick.transform.name;
                // H2: a cargo object (in the car, or out of it as a synthetic record) is leased under its synthetic id
                uint hash = e != null ? e.Hash : 0u; bool cargo = false;
                if (e == null && HeavyCargoSync.Instance != null && HeavyCargoSync.Instance.TryCargoObject(pick, out hash))
                {
                    cargo = true; ns = _world.LocalRegion;
                    if (ns == Regions.None || ns == IdentityNamespace.MainScene) { LogOnce("cns:" + name, "HEAVY pickup of cargo object '" + name + "' refused: no region"); return false; }
                }
                if (_host)
                {
                    if ((e == null && !cargo) || _world.Host == null) return true;          // not shared: original local carry (Player 2 cannot pick it)
                    string why; ushort lease;
                    var code = _world.Host.HeavyTransaction(1, new HeavyTxnRequest { Op = HeavyOp.Lease, Ns = ns, Hash = hash, Cargo = cargo }, _world.Clock, out lease, out why);
                    _world.FlushOutbox();
                    if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) { _log.Info("HEAVY Player-1 pickup of '" + name + "' (" + hash.ToString("X8") + ") refused: " + why); return false; }
                    CaptureBeforePickup(ns, hash, cargo);
                    AddSim(ns, hash, pick, lease, name, cargo);
                    _p1Leases++; _dirty = true; if (cargo) _cargoPicks++;
                    _log.Info("HEAVY Player 1 carries '" + name + "' (" + IdentityNamespace.Name(ns) + ":" + hash.ToString("X8") + ", lease " + lease + (cargo ? ", " + why : "") + "): original PickUpHeavy");
                    return true;
                }
                if (!Live) { LogOnce("off:" + name, "HEAVY Player-2 heavy pickup of '" + name + "' refused: not connected (a local carry could never be reconciled)"); return false; }
                if (e == null && !cargo)
                {
                    _p2Refused++;
                    LogOnce("ns:" + name, "HEAVY Player-2 heavy pickup of '" + name + "' refused: not a verified heavy object of the current region (MainScene / runtime / ambiguous object) - a local carry would duplicate it");
                    return false;
                }
                if (_req != null) return false;                                // one request at a time (the click is simply ignored)
                HeavyRecord r;
                if (TryRecord(ns, hash, out r) && (r.State == HeavyState.Removed || (r.State == HeavyState.Carried && r.Actor != Me))) return false;
                var cat = cargo ? null : LiveCatalog(ns);
                var req = new HeavyTxnRequest { Op = HeavyOp.Lease, Ns = ns, Hash = hash, Digest = cat != null ? cat.Digest : 0u, Rot = new Quat16 { W = 32767 }, Cargo = cargo };
                _req = new Req { Ns = ns, Hash = hash, Pick = pick, Dist = dist, At = Now, Name = name, Cargo = cargo };
                _req.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = req.Encode() });
                _p2Requests++; if (cargo) _cargoPicks++;
                _log.Info("HEAVY pickup of " + (cargo ? "cargo object " : "") + "'" + name + "' (" + IdentityNamespace.Name(ns) + ":" + hash.ToString("X8") + ") requested from the host (proposal " + _req.Id + ")");
                return false;
            }
            finally { double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _costMaxMs) _costMaxMs = ms; }
        }

        // Scene pose of an object before the original pickup changes it (PickUpHeavy sets parent null): needed to restore it
        // exactly when the lease ends without any rest pose (host deletes the record = original scene pose everywhere).
        void CaptureBeforePickup(int ns, uint hash, bool cargo)
        {
            var cat = LiveCatalog(ns); if (cat == null || ns != _world.LocalRegion) return;
            if (ns != _objNs) { RestoreAll(); _objs.Clear(); _objNs = ns; }
            var o = Get(cat, hash, cargo); if (o != null) Capture(o);
        }

        void AddSim(int ns, uint hash, Component pick, ushort lease, string name, bool cargo)
        {
            _sims[hash] = new Sim { Ns = ns, Hash = hash, Pick = pick, Rb = pick.GetComponent<Rigidbody>(), Lease = lease, Phase = Phase.Carrying, LastTick = Now, Name = name, Cargo = cargo };
            Obj o; if (_objs.TryGetValue(hash, out o)) o.Shown = HeavyRules.Show.Own;   // the original owns its state now
        }

        // ======================================================================== client results
        bool OnResult(uint id, ProposalCode code)
        {
            if (_req != null && id == _req.Id) { var q = _req; _req = null; OnLeaseResult(q, code); return true; }
            Req ab;
            if (_abandoned.TryGetValue(id, out ab))
            {
                _abandoned.Remove(id);
                if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged) { HeavyRecord r; if (TryRecord(ab.Ns, ab.Hash, out r) && r.CarriedBy(Me)) SendTxn(HeavyOp.Release, ab.Ns, ab.Hash, r.Lease, default(Vector3), Quaternion.identity, 0u, r.Cargo, "late grant of an abandoned request"); }
                return true;
            }
            HeavyTxnRequest t;
            if (!_sent.TryGetValue(id, out t)) return false;
            _sent.Remove(id);
            if (t.Op == HeavyOp.Load) { OnLoadResult(t, code); return true; }
            if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged)
            {
                _log.Info("HEAVY " + t + " -> " + code + ": the host record decides (presentation follows the registry)");
                Sim s; if ((t.Op == HeavyOp.Drop || t.Op == HeavyOp.Settle) && _sims.TryGetValue(t.Hash, out s) && s.Lease == t.Lease) { _sims.Remove(t.Hash); _dirty = true; }
            }
            return true;
        }

        void OnLeaseResult(Req q, ProposalCode code)
        {
            if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) { _p2Refused++; _log.Info("HEAVY pickup of '" + q.Name + "' -> " + code + ": nothing happens (another player holds it, or the host refused)"); _dirty = true; return; }
            HeavyRecord r;
            if (!TryRecord(q.Ns, q.Hash, out r) || !r.CarriedBy(Me)) { _log.Warn("HEAVY pickup of '" + q.Name + "' acknowledged but the registry shows no lease for Player " + Me + " - nothing happens"); _dirty = true; return; }
            var ps = Ps(); bool alive = q.Pick != null && q.Pick.gameObject.activeInHierarchy && q.Ns == _world.LocalRegion;
            float dist = float.MaxValue; bool controls = false;
            if (alive && ps != null)
            {
                var me = (ps as Component).transform.position; dist = Vector3.Distance(me, q.Pick.transform.position);
                var pmb = _psPmb(ps) as Behaviour; controls = pmb != null && pmb.enabled;
            }
            string block = ps == null ? "no player" : HeavyRules.GrantBlock(alive, _psCarrying(ps), _psInVehicle(ps), controls, Time.timeScale <= 0f, dist);
            if (block != null)
            {
                _grantAborted++;
                SendTxn(HeavyOp.Release, q.Ns, q.Hash, r.Lease, default(Vector3), Quaternion.identity, 0u, r.Cargo, "grant no longer usable: " + block);
                _log.Info("HEAVY pickup of '" + q.Name + "' granted (lease " + r.Lease + ") but " + block + ": released, nothing happens");
                return;
            }
            CaptureBeforePickup(q.Ns, q.Hash, r.Cargo);
            _resume = q.Pick;
            try { _pickUpHeavy(q.Pick, q.Dist); }
            catch (Exception ex) { _log.Error("HEAVY original PickUpHeavy after the grant failed", ex); }
            finally { _resume = null; }
            if (!_psCarrying(ps) || !ReferenceEquals(_psCarryItem(ps), q.Pick.transform))
            {
                _grantAborted++;
                SendTxn(HeavyOp.Release, q.Ns, q.Hash, r.Lease, default(Vector3), Quaternion.identity, 0u, r.Cargo, "original pickup did not start");
                _log.Warn("HEAVY pickup of '" + q.Name + "': the original PickUpHeavy did not start a carry - lease released");
                return;
            }
            _p2Granted++;
            AddSim(q.Ns, q.Hash, q.Pick, r.Lease, q.Name, r.Cargo);
            _log.Info("HEAVY Player 2 carries '" + q.Name + "' (lease " + r.Lease + ", " + ((Now - q.At) * 1000).ToString("0") + " ms after the click): original PickUpHeavy ran once");
        }

        // one lease operation: host in place, client proposal
        void SendTxn(HeavyOp op, int ns, uint hash, ushort lease, Vector3 p, Quaternion q, string why)
        {
            SendTxn(op, ns, hash, lease, p, q, 0u, IsCargo(hash), why);
        }
        // H2: whether this id is a cargo object outside its car (held here, or listed so in the last reconciled records)
        bool IsCargo(uint hash)
        {
            Sim s; if (_sims.TryGetValue(hash, out s)) return s.Cargo;
            HeavyRecord r; return _records.TryGetValue(hash, out r) && r.Cargo;
        }
        void SendTxn(HeavyOp op, int ns, uint hash, ushort lease, Vector3 p, Quaternion q, uint digest, bool cargo, string why)
        {
            var t = new HeavyTxnRequest { Op = op, Ns = ns, Hash = hash, Digest = digest, Lease = lease, Pos = new Vec3(p.x, p.y, p.z), Rot = Quat16.From(q.x, q.y, q.z, q.w), Cargo = cargo };
            if (op == HeavyOp.Release) _releases++; else if (op == HeavyOp.Remove) _removes++;
            if (_host)
            {
                if (_world.Host == null) return;
                string w; ushort l; var code = _world.Host.HeavyTransaction(1, t, _world.Clock, out l, out w); _world.FlushOutbox();
                bool place = op == HeavyOp.Drop || op == HeavyOp.Settle;
                if (op == HeavyOp.Load) { OnLoadResult(t, code, w); return; }
                if (!place || (code != ProposalCode.Accepted && code != ProposalCode.Unchanged)) _log.Info("HEAVY " + t + " (" + why + ") -> " + code + (w != null ? " (" + w + ")" : ""));
                Sim hs; if (place && code == ProposalCode.Stale && _sims.TryGetValue(hash, out hs) && hs.Lease == lease) { _sims.Remove(hash); _dirty = true; }
                return;
            }
            if (!Live) { _log.Info("HEAVY " + t + " (" + why + ") not sent: offline (the host releases the leases of a disconnected player)"); return; }
            uint id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = t.Encode() });
            if (_sent.Count < 64) _sent[id] = t;
            if (op != HeavyOp.Drop && op != HeavyOp.Settle) _log.Info("HEAVY " + t + " (" + why + ") sent (proposal " + id + ")");
        }

        // H2: the host's decision on a Load (host in place, client from its proposal result). Accepted: the object is the car's
        // cargo now (the registry hides / replaces it); refused: nothing changes, the object keeps its committed rest pose here.
        void OnLoadResult(HeavyTxnRequest t, ProposalCode code, string why = null)
        {
            Sim s; bool mine = _sims.TryGetValue(t.Hash, out s) && s.Lease == t.Lease;
            if (code == ProposalCode.Accepted)
            {
                _loadsDone++;
                if (mine) { _sims.Remove(t.Hash); _dirty = true; }
                _log.Info("HEAVY " + (mine ? "'" + s.Name + "' " : "") + "loaded into vehicle " + t.Digest + " (" + t.Hash.ToString("X8") + ", lease " + t.Lease + (why != null ? ", " + why : "") + "): left the shared world once, now committed cargo");
                return;
            }
            _loadsRefused++;
            _log.Info("HEAVY " + t + " -> " + code + (why != null ? " (" + why + ")" : "") + ": not loaded - the object stays where it rests (H1 rest pose)");
        }

        // H1.1: one movement snapshot of an object this instance simulates (final = the exact rest pose, sent with the Settle).
        // Sent only to a connected peer in the same region, at most MotionHz and only after a real change (VehicleSendPolicy).
        void Stream(Sim s, Transform t, double now, bool final)
        {
            var p = t.position; var q = t.rotation;
            var vp = new Vec3(p.x, p.y, p.z);
            if (!s.Policy.ShouldSend(now, vp, new Quat(q.x, q.y, q.z, q.w), VehicleStateFlags.None, s.Ns) && !final) return;
            var m = new HeavyMotion { Ns = s.Ns, Hash = s.Hash, Lease = s.Lease, Seq = ++_motionSeq, TimeMs = (uint)(now * 1000.0), Pos = vp, Rot = Quat16.From(q.x, q.y, q.z, q.w), Final = final };
            _mw.Reset(); m.Write(_mw);
            _node.Send(_mw, DeliveryMethod.Unreliable, SessionNode.ChannelState);
            _motionSent++; _motionSentBytes += _mw.Length; s.Sent++;
        }

        // The original physics moves a held object (after DropHeavy, after a carry that ended without it, or again after a
        // rest): commit the current pose as Moving at once, then stream until it rests.
        void StartMoving(Sim s, double now, string why)
        {
            var t = s.Pick.transform;
            s.Phase = Phase.Falling; s.DropAt = now; s.LastTick = now; s.UnpausedFor = 0; s.NextCheck = now + 0.2; s.Sent = 0; s.LoadTried = false;
            s.Policy.Reset();
            SendTxn(HeavyOp.Drop, s.Ns, s.Hash, s.Lease, t.position, t.rotation, why);
            if (s.Reported) _moves++;
            _log.Info("HEAVY '" + s.Name + "' " + why + " at " + V3(t.position) + " (lease " + s.Lease + "): release pose committed now, original physics on this PC only, movement streamed until it rests");
        }

        // ======================================================================== original hooks
        static void DropPostfix(object __instance)
        {
            var i = Instance; var c = __instance as Component;
            if (i == null || c == null) return;
            try { i.OnDropped(c); } catch (Exception ex) { i._log.Error("HEAVY drop hook failed", ex); }
        }
        // Postfix of the ORIGINAL DropHeavy (callers StopCarryingItem / GetTazed): the object is dynamic now and still at its
        // carry pose; the caller's AddForce follows after this returns. That pose is the release pose -> committed at once.
        void OnDropped(Component pick)
        {
            Sim hit = null;
            foreach (var s in _sims.Values) if (ReferenceEquals(s.Pick, pick)) { hit = s; break; }
            if (hit == null || hit.Phase != Phase.Carrying) return;
            hit.Reported = false;
            _drops++;
            StartMoving(hit, Now, "dropped (original DropHeavy)");
        }

        // host: Player 1's original deposit (trunk / shop: PlayerScript.HeavyItemPlacedInCar -> PlacedInTrunk) - the object
        // leaves shared tracking; Player 2 never sees a second copy (deposit sync is a later milestone).
        static void TrunkPostfix(object __instance)
        {
            var i = Instance; var c = __instance as Component;
            if (i == null || c == null || HeavyCargoSync.Applying) return;   // H2: our own cargo presentation, not a deposit
            try { i.OnDeposit(c, "original deposit (PlacedInTrunk: car trunk / shop)"); } catch (Exception ex) { i._log.Error("HEAVY deposit hook failed", ex); }
        }
        void OnDeposit(Component pick, string why)
        {
            foreach (var s in _sims.Values)
            {
                if (!ReferenceEquals(s.Pick, pick)) continue;
                _sims.Remove(s.Hash);
                SendTxn(HeavyOp.Remove, s.Ns, s.Hash, s.Lease, default(Vector3), Quaternion.identity, why);
                _log.Info("HEAVY '" + s.Name + "' left the shared world: " + why + " - host-local now, hidden for Player 2");
                _dirty = true;
                return;
            }
        }

        bool CarriesLeased(out Sim sim)
        {
            sim = null;
            var ps = Ps(); if (ps == null || !_psCarrying(ps)) return false;
            var t = _psCarryItem(ps) as Transform; if (t == null) return false;
            foreach (var s in _sims.Values) if (s.Phase == Phase.Carrying && s.Pick != null && ReferenceEquals(s.Pick.transform, t)) { sim = s; return true; }
            return false;
        }

        // client: no trunk / shop deposit of a leased object in H1 (it would vanish into Player 2's unsaved local storage)
        static bool DepositPrefix(Collider other)
        {
            var i = Instance; if (i == null || other == null || other.tag != "Player") return true;
            try
            {
                Sim s; if (!i.CarriesLeased(out s)) return true;
                i._depositBlocked++;
                i.LogOnce("dep:" + s.Name, "HEAVY Player-2 deposit of '" + s.Name + "' (car trunk / shop trigger) blocked: heavy deposits are not shared yet (later milestone) - keeps carrying");
                return false;
            }
            catch (Exception ex) { i._log.Error("HEAVY deposit guard failed (deposit blocked)", ex); return false; }
        }

        // client: the original drop is refused (keeps carrying, like an obstructed drop) when the object would land on or in a
        // vehicle without a shared cargo bed. H2: next to the Ranger (shared cargo bed) the original drop runs; at rest in its bed
        // the object is loaded through the host, elsewhere on the car it is fixed at its rest pose (H1 rule).
        static readonly Collider[] _overlap = new Collider[32];
        static bool StopPrefix(object __instance)
        {
            var i = Instance; if (i == null) return true;
            try
            {
                Sim s; if (!i.CarriesLeased(out s) || s.Pick == null) return true;
                var car = i.VehicleNear(s.Pick.gameObject, 0.3f, true);
                if (car == null) return true;
                if (HeavyCargoSync.Instance != null && HeavyCargoSync.Instance.IsCargoCar(car)) return true;   // H2: shared cargo bed
                i._dropRefused++;
                i.LogOnce("drop:" + car.name, "HEAVY Player-2 drop of '" + s.Name + "' refused: it would land on / in the vehicle '" + car.name + "' (no shared cargo bed) - keeps carrying");
                return false;
            }
            catch (Exception ex) { i._log.Error("HEAVY drop guard failed (original drop allowed)", ex); return true; }
        }

        // associate carry (CodenameAI.PickHeavy) of a supported object during a session: refused (one instance's copy only)
        static bool AssociatePrefix(GameObject obj)
        {
            var i = Instance; if (i == null || obj == null || i._pickT == null) return true;
            try
            {
                var p = obj.GetComponent(i._pickT); if (p == null) return true;
                int ns; var e = i.Resolve(p, out ns); if (e == null) return true;
                i._associateBlocked++;
                i.LogOnce("cod:" + e.Name, "HEAVY associate heavy carry of '" + e.Name + "' refused during a multiplayer session (not shared yet)");
                return false;
            }
            catch (Exception ex) { i._log.Error("HEAVY associate guard failed (refused)", ex); return false; }
        }

        // A vehicle collider within 'margin' of the object's collider bounds (EnterCar door triggers ignored); its CarController or null.
        Component VehicleNear(GameObject go, float margin, bool triggers)
        {
            Bounds b; if (!Bounds(go, out b)) return null;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + new Vector3(margin, margin, margin), _overlap, Quaternion.identity, ~0, triggers ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);
            var self = go.transform;
            for (int k = 0; k < n; k++)
            {
                var c = _overlap[k];
                if (c == null || c.transform.IsChildOf(self)) continue;
                var car = c.GetComponentInParent(_carT) as Component;
                if (car == null) continue;
                if (c.isTrigger && _enterCarT != null && c.GetComponent(_enterCarT) != null) continue;
                return car;
            }
            return null;
        }

        // the object rests on a vehicle (or was parented into one by the original cargo code)
        static readonly RaycastHit[] _hits = new RaycastHit[16];
        string RestsOnVehicle(GameObject go)
        {
            var pc = go.transform.parent != null ? go.transform.parent.GetComponentInParent(_carT) as Component : null;
            if (pc != null) return pc.name;
            Bounds b; if (!Bounds(go, out b)) return null;
            int n = Physics.RaycastNonAlloc(b.center, Vector3.down, _hits, b.extents.y + 0.6f, ~0, QueryTriggerInteraction.Ignore);
            var self = go.transform;
            for (int k = 0; k < n; k++)
            {
                var c = _hits[k].collider;
                if (c == null || c.transform.IsChildOf(self)) continue;
                var car = c.GetComponentInParent(_carT) as Component;
                if (car != null) return car.name;
            }
            return null;
        }

        static bool Bounds(GameObject go, out Bounds b)
        {
            bool any = false; b = new Bounds();
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) { if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds); }
            if (any) return true;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            return any;
        }

        // ======================================================================== per frame
        public void Update()
        {
            if (!_ok) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = Now;
            if (now >= _nextStats) { _nextStats = now + 30; if (_p1Leases + _p2Requests + _drops + _hidden + _placed + _motionRecv > 0) _log.Info(Stats); }
            if (_req != null && (!Live || now - _req.At > 15.0))
            {
                _lost++; _log.Warn("HEAVY pickup request of '" + _req.Name + "' unanswered" + (Live ? " after 15 s" : " (disconnected)") + ": abandoned (a late grant is released)");
                if (_abandoned.Count < 16) _abandoned[_req.Id] = _req;
                _req = null;
            }
            Refresh();
            if (!_host && !Live) return;                                        // presentation frozen while offline; Dispose restores
            int region = _world.LocalRegion;
            if (_motions.Count > 0) MotionFrame(now);
            if (_sims.Count > 0) SimFrame(now, region);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _costMaxMs) _costMaxMs = ms;
        }

        // Scene / registry check, at most once per frame: from Update, or earlier in the frame from RemoteGear (CarryShown), so
        // the revealed scene object and the avatar's carried copy always switch in the same frame.
        void Refresh()
        {
            int f = Time.frameCount;
            if (f == _refreshFrame) return;
            _refreshFrame = f;
            var gm = _world.GameManagerObject;
            if (!ReferenceEquals(gm, _seenGm)) { _seenGm = gm; SceneChanged(); }
            if (!_host && !Live) return;
            int region = _world.LocalRegion;
            var reg = _world.Registry;
            if (_dirty || region != _objNs || reg.HeadRev != _seenHead || reg.Epoch != _seenEpoch)
            {
                if (reg.Epoch != _seenEpoch) { _dirty = true; _filter.Clear(); }   // new host registry (resync): revisions / leases restart
                _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch;
                // only this region's "wheavy" entry matters (the head revision also moves for clocks, doors, loot ...)
                var he = region != Regions.None ? reg.Get(StateScope.Region, region, WorldKeys.Heavy) : null;
                uint rev = he != null ? he.Rev : 0u;
                if (_dirty || region != _objNs || rev != _shownRev) { if (Reconcile(region)) { _dirty = false; _shownRev = rev; } }
            }
        }

        // A new GameManager = a new scene instance: every original object of the old one is gone. Leases still held for it are
        // released (a same-region reload would otherwise keep them; after a region change the host already released them).
        void SceneChanged()
        {
            foreach (var s in _sims.Values)
                SendTxn(HeavyOp.Release, s.Ns, s.Hash, s.Lease, default(Vector3), Quaternion.identity, "scene reloaded (object gone with it)");
            _sims.Clear(); _objs.Clear(); _objNs = Regions.None; _remoteCarryGo = null; _remoteCarryItem = -1; _remoteCarryHash = 0; _dirty = true;
            _motions.Clear(); _records.Clear(); _filter.Clear();
        }

        // Present every record of the local region; objects that lost their record go back to their scene pose. false = retry.
        bool Reconcile(int region)
        {
            if (region != _objNs) { EndAllMotions(); RestoreAll(); _objs.Clear(); _objNs = region; _filter.Clear(); }
            bool hadCarry = _remoteCarryGo != null; int prevItem = _remoteCarryItem; uint prevHash = _remoteCarryHash;
            _remoteCarryGo = null; _remoteCarryItem = -1; _remoteCarryHash = 0;
            _records.Clear();
            if (region == Regions.None || region == IdentityNamespace.MainScene) { if (hadCarry) _suppressItem = prevItem; return true; }
            List<HeavyRecord> l = null;
            var e = _world.Registry.Get(StateScope.Region, region, WorldKeys.Heavy);
            if (e != null && !HeavyList.TryParse(e.Value, out l)) { _log.Warn("HEAVY registry list of " + Regions.Name(region) + " unreadable"); return true; }
            if (l == null) l = new List<HeavyRecord>();
            var cat = LiveCatalog(region);
            if (cat == null) return l.Count == 0;                               // census of the new scene not ready yet: retry
            var keep = new HashSet<uint>();
            foreach (var r in l)
            {
                keep.Add(r.Hash);
                _records[r.Hash] = r;
                Sim s; bool sim = _sims.TryGetValue(r.Hash, out s);
                if (sim && !(r.Actor == Me && r.Lease == s.Lease && r.State != HeavyState.Removed)) { EndSim(s, r); sim = false; }
                Obj o = Get(cat, r.Hash, r.Cargo);
                if (!sim && r.MovingBy(Me))
                {
                    // the host handed this moving object to us (its simulator left): continue the original physics here
                    if (o != null) { Adopt(o, r, region); sim = true; }
                    else SendTxn(HeavyOp.Release, region, r.Hash, r.Lease, default(Vector3), Quaternion.identity, 0u, r.Cargo, "handed over but not in this census");
                }
                if (o == null) continue;
                var show = HeavyRules.Present(true, r, Me, sim);
                if (r.State == HeavyState.Carried && r.Actor == Other) { _remoteCarryGo = o.Go; _remoteCarryItem = o.ItemId; _remoteCarryHash = r.Hash; }
                Apply(o, show, r);
            }
            // the other player's carry of a leased object ended (drop / deposit / release) in THIS frame: its avatar copy goes now,
            // and the player snapshot (rendered ~100 ms later) may not bring it back until it shows the carry ended too
            if (hadCarry && (_remoteCarryGo == null || _remoteCarryHash != prevHash)) _suppressItem = prevItem;
            // a lease this instance still runs but the host no longer lists (released / region reset): the registry decides
            _scratch.Clear();
            foreach (var h in _sims.Keys) if (!keep.Contains(h)) _scratch.Add(h);
            foreach (var h in _scratch) { Sim s; if (_sims.TryGetValue(h, out s)) EndSim(s, default(HeavyRecord)); }
            foreach (var kv in _objs) if (!keep.Contains(kv.Key) && !_sims.ContainsKey(kv.Key)) Apply(kv.Value, HeavyRules.Show.ScenePose, default(HeavyRecord));
            return true;
        }

        // The registry no longer gives this instance the object (another player leased it after our drop, or the host reverted
        // the lease): stop simulating; a carry the original still runs is ended with the original StopCarryingItem.
        void EndSim(Sim s, HeavyRecord r)
        {
            _sims.Remove(s.Hash);
            var ps = Ps();
            if (s.Phase == Phase.Carrying && ps != null && _psCarrying(ps) && s.Pick != null && ReferenceEquals(_psCarryItem(ps), s.Pick.transform) && !_psDropBlock(ps))
            {
                try { _stopCarry(ps, 0f); _forcedEnds++; } catch (Exception ex) { _log.Error("HEAVY original StopCarryingItem failed", ex); }
            }
            _log.Info("HEAVY '" + s.Name + "' no longer held here (registry: " + r + "): presented from the registry");
        }

        Obj Get(LootCatalog cat, uint hash, bool cargo)
        {
            Obj o;
            if (_objs.TryGetValue(hash, out o)) { if (o.Go != null && o.Synthetic == cargo) return o; _objs.Remove(hash); }
            if (cargo)
            {
                // H2: a cargo object outside its car - this instance's object for it (HeavyCargoSync creates one if needed)
                var sp = HeavyCargoSync.Instance != null ? HeavyCargoSync.Instance.SyntheticObject(hash) : null;
                if (sp == null) return null;
                o = new Obj { Hash = hash, ItemId = HeavyCargo.ItemOf(hash), Pick = sp, Go = sp.gameObject, Rb = sp.GetComponent<Rigidbody>(), Synthetic = true };
                _objs[hash] = o;
                return o;
            }
            if (cat == null) return null;
            LootEntry le; if (!cat.TryGet(hash, out le)) return null;
            var pick = le.Target as Component; if (pick == null) return null;
            o = new Obj { Hash = hash, ItemId = le.ItemId, Pick = pick, Go = pick.gameObject, Rb = pick.GetComponent<Rigidbody>() };
            _objs[hash] = o;
            return o;
        }

        void Capture(Obj o)
        {
            if (o.Captured) return;
            var t = o.Go.transform;
            o.OrigParent = t.parent; o.OrigLocalPos = t.localPosition; o.OrigLocalRot = t.localRotation; o.OrigLocalScale = t.localScale;
            if (o.Rb != null) { o.OrigKin = o.Rb.isKinematic; o.OrigGrav = o.Rb.useGravity; }
            o.Captured = true;
        }

        void Apply(Obj o, HeavyRules.Show show, HeavyRecord r)
        {
            if (o.Go == null) return;
            var p = new Vector3(r.Pos.X, r.Pos.Y, r.Pos.Z); var q = new Quaternion(r.Rot.Fx, r.Rot.Fy, r.Rot.Fz, r.Rot.Fw);
            if (show == o.Shown && (show != HeavyRules.Show.AtPose || ((p - o.ShownPos).sqrMagnitude < 1e-6f && Quaternion.Angle(q, o.ShownRot) < 0.5f))) return;
            var t = o.Go.transform;
            switch (show)
            {
                case HeavyRules.Show.Own:
                    Unhide(o);                                                  // the original drives it on this instance
                    break;
                case HeavyRules.Show.Hidden:
                    Capture(o);
                    if (t.parent != null) t.SetParent(null, true);
                    Freeze(o);
                    Hide(o); _hidden++;
                    break;
                case HeavyRules.Show.AtPose:
                {
                    Capture(o);
                    if (t.parent != null) t.SetParent(null, true);
                    bool reveal = o.Shown != HeavyRules.Show.AtPose;
                    Unhide(o); Freeze(o);
                    // H1.1: a followed object keeps its interpolated pose (MotionFrame ends exactly at the committed rest pose);
                    // a reveal always starts at the committed pose (the release pose of a drop)
                    if (reveal || !Following(o.Hash, r)) t.SetPositionAndRotation(p, q);
                    o.ShownPos = p; o.ShownRot = q; _placed++;
                    break;
                }
                default:
                    if (o.Shown == HeavyRules.Show.ScenePose) return;
                    Restore(o); _restored++;
                    break;
            }
            o.Shown = show;
        }

        void Freeze(Obj o) { if (o.Rb != null) { if (!o.Rb.isKinematic) { o.Rb.velocity = Vector3.zero; o.Rb.angularVelocity = Vector3.zero; } o.Rb.isKinematic = true; o.Rb.useGravity = false; } }

        // ======================================================================== H1.1 movement (observer + handover)
        bool Following(uint hash, HeavyRecord r)
        {
            Motion m; return _motions.TryGetValue(hash, out m) && m.Lease == r.Lease && m.Sender == r.Actor && m.Samples > 0;
        }

        // The host handed us an object that is still moving (its simulator left the region / disconnected): this instance shows
        // it kinematic at the last followed pose -> the original post-DropHeavy physics state (dynamic + gravity) from there,
        // settled and committed by this instance like its own drop.
        void Adopt(Obj o, HeavyRecord r, int region)
        {
            EndMotion(r.Hash);
            Unhide(o);
            if (o.Rb != null) { o.Rb.isKinematic = false; o.Rb.useGravity = true; }
            double now = Now;
            var s = new Sim { Ns = region, Hash = r.Hash, Pick = o.Pick, Rb = o.Rb, Lease = r.Lease, Phase = Phase.Falling, DropAt = now, LastTick = now, NextCheck = now + 0.2, Name = o.Go.name, Reported = true, Cargo = r.Cargo };
            s.LastPos = o.Go.transform.position; s.LastRot = o.Go.transform.rotation;
            _sims[r.Hash] = s;
            o.Shown = HeavyRules.Show.Own; _adopted++;
            _log.Info("HEAVY '" + s.Name + "' (" + r.Hash.ToString("X8") + ") handed over while still moving (lease " + r.Lease + "): the original physics continues on this PC from " + V3(s.LastPos));
        }

        void EndMotion(uint hash)
        {
            Motion m; if (!_motions.TryGetValue(hash, out m)) return;
            _motions.Remove(hash);
            RestoreInterp(m.Obj);
        }
        void EndAllMotions() { foreach (var m in _motions.Values) RestoreInterp(m.Obj); _motions.Clear(); }
        static void RestoreInterp(Obj o) { if (o != null && o.InterpOff) { if (o.Rb != null) o.Rb.interpolation = o.OrigInterp; o.InterpOff = false; } }

        // HeavyMotion from the other player (LiteNetLib events are polled on the main thread).
        void OnMessage(NetPeer peer, byte type, NetPacketReader r)
        {
            if (type != (byte)MsgType.HeavyMotion || !_ok) return;
            _motionRecv++; _motionRecvBytes += r.AvailableBytes + 1;
            HeavyMotion m;
            if (!HeavyMotion.TryRead(r, out m)) { _motionMalformed++; return; }
            if (!_host && !Live) return;
            HeavyRecord rec = default(HeavyRecord);
            bool has = _objNs == _world.LocalRegion && _records.TryGetValue(m.Hash, out rec);
            var v = _filter.Check(m, has, rec, Other, _world.LocalRegion);
            if (v != HeavyMotionVerdict.Buffer && v != HeavyMotionVerdict.Follow) return;
            Motion mo;
            if (!_motions.TryGetValue(m.Hash, out mo) || mo.Lease != m.Lease || mo.Sender != Other)
            {
                var cat = LiveCatalog(_objNs); var o = cat != null ? Get(cat, m.Hash, has && rec.Cargo) : null;
                if (o == null || (_motions.Count >= 16 && !_motions.ContainsKey(m.Hash))) { _motionUnknown++; return; }
                EndMotion(m.Hash);
                mo = new Motion { Hash = m.Hash, Lease = m.Lease, Sender = Other, Obj = o, Start = Now };
                if (o.Rb != null && o.Rb.interpolation != RigidbodyInterpolation.None) { o.OrigInterp = o.Rb.interpolation; o.Rb.interpolation = RigidbodyInterpolation.None; o.InterpOff = true; }
                _motions[m.Hash] = mo;
            }
            mo.Buf.Add(m.TimeMs, m.Pos, m.RotQ, Now);
            mo.Samples++;
        }

        // Followed objects: the kinematic scene object takes the interpolated pose of the holder's original physics; once the
        // record is settled and the buffer has played out, the exact committed rest pose. A record that no longer belongs to
        // that holder / lease (pickup, storage, release, reset) ends the motion at once - Reconcile already presents it.
        void MotionFrame(double now)
        {
            _ended.Clear();
            foreach (var m in _motions.Values)
            {
                HeavyRecord r;
                if (!_records.TryGetValue(m.Hash, out r) || r.Actor != m.Sender || r.Lease != m.Lease || r.State == HeavyState.Removed) { _ended.Add(m.Hash); continue; }
                if (r.State == HeavyState.Carried) continue;                    // Drop commit still in flight: buffered only
                var o = m.Obj;
                if (o == null || o.Go == null) { _ended.Add(m.Hash); continue; }
                if (o.Shown != HeavyRules.Show.AtPose) continue;
                var t = o.Go.transform;
                VehiclePose p;
                if (m.Buf.Sample(now, out p)) t.SetPositionAndRotation(new Vector3(p.Pos.X, p.Pos.Y, p.Pos.Z), new Quaternion(p.Rot.X, p.Rot.Y, p.Rot.Z, p.Rot.W));
                if (r.Moving || !m.Buf.Drained(now)) continue;
                var rp = new Vector3(r.Pos.X, r.Pos.Y, r.Pos.Z); var rq = new Quaternion(r.Rot.Fx, r.Rot.Fy, r.Rot.Fz, r.Rot.Fw);
                float corr = Vector3.Distance(t.position, rp);
                t.SetPositionAndRotation(rp, rq); o.ShownPos = rp; o.ShownRot = rq;
                if (corr > _correctionMax) _correctionMax = corr;
                _followed++;
                _log.Info("HEAVY " + m.Hash.ToString("X8") + " followed " + m.Samples + " snapshot(s) over " + (now - m.Start).ToString("0.0") + " s from P" + m.Sender + ": exact rest pose " + V3(rp) + " (final correction " + corr.ToString("0.000") + " m)");
                _ended.Add(m.Hash);
            }
            foreach (var h in _ended) EndMotion(h);
        }

        // RemoteGear (once per frame, before Update): which carried item the remote avatar shows. A leased object the other
        // player carries is shown from the lease record (scene = that very object); otherwise the snapshot's item type (prefab
        // fallback), except the item whose leased carry just ended until the snapshot shows that end too (no duplicate copy).
        public int CarryShown(int snapshotItem, out GameObject scene)
        {
            scene = null;
            if (!_ok) return snapshotItem;
            Refresh();
            if (_remoteCarryGo != null) { scene = _remoteCarryGo; return _remoteCarryItem; }
            if (snapshotItem < 0) { _suppressItem = -1; return -1; }
            return snapshotItem == _suppressItem ? -1 : snapshotItem;
        }

        void Hide(Obj o)
        {
            foreach (var r in o.Go.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; o.HiddenRenderers.Add(r); }
            foreach (var c in o.Go.GetComponentsInChildren<Collider>(true)) if (c.enabled) { c.enabled = false; o.HiddenColliders.Add(c); }
        }
        void Unhide(Obj o)
        {
            foreach (var r in o.HiddenRenderers) if (r != null) r.enabled = true;
            foreach (var c in o.HiddenColliders) if (c != null) c.enabled = true;
            o.HiddenRenderers.Clear(); o.HiddenColliders.Clear();
        }

        // original scene pose and physics state (the original reload would show exactly this)
        void Restore(Obj o)
        {
            if (o.Go == null) return;
            if (o.Synthetic)
            {
                // no scene pose: a cargo object without a record is not in this world (HeavyCargoSync puts it back into its car
                // when the host list says so, or removes it)
                if (o.Go.transform.parent != null) o.Go.transform.SetParent(null, true);
                Freeze(o);
                if (o.HiddenRenderers.Count == 0 && o.HiddenColliders.Count == 0) Hide(o);
                return;
            }
            Unhide(o);
            if (!o.Captured) return;
            var t = o.Go.transform;
            if (t.parent != o.OrigParent) t.SetParent(o.OrigParent, false);
            t.localPosition = o.OrigLocalPos; t.localRotation = o.OrigLocalRot; t.localScale = o.OrigLocalScale;
            if (o.Rb != null) { o.Rb.isKinematic = o.OrigKin; o.Rb.useGravity = o.OrigGrav; }
        }

        void RestoreAll()
        {
            foreach (var o in _objs.Values) if (o.Shown != HeavyRules.Show.ScenePose && o.Shown != HeavyRules.Show.Own) Restore(o);
        }

        // Held objects: carry watch; while the original physics moves an object: bounded snapshots every frame (rate/epsilon
        // policy) + the W3 rest rule on the ORIGINAL rigidbody 5x/s; at rest: movement check 2x/s. Host in place, client proposals.
        void SimFrame(double now, int region)
        {
            bool paused = Time.timeScale <= 0f;
            var ps = Ps();
            bool stream = Live && RegionDirectory.RegionOf(_world.Registry, Other) == region;   // nobody to show it to otherwise
            _scratch.Clear();
            _iter.Clear(); foreach (var kv in _sims) _iter.Add(kv.Value);      // SendTxn may drop a stale lease from _sims
            foreach (var s in _iter)
            {
                if (!_sims.ContainsKey(s.Hash)) continue;
                if (!paused) s.UnpausedFor += now - s.LastTick;
                s.LastTick = now;
                if (s.Pick == null || s.Ns != region) { _scratch.Add(s.Hash); continue; }
                if (s.Phase == Phase.Carrying)
                {
                    bool carrying = ps != null && _psCarrying(ps) && ReferenceEquals(_psCarryItem(ps), s.Pick.transform);
                    // the carry ended without our DropHeavy postfix (e.g. a teleport / scripted end): treat as released here
                    if (!carrying && ps != null && !_psCarrying(ps)) { s.Reported = false; StartMoving(s, now, "carry ended without a drop call"); }
                    continue;
                }
                var t = s.Pick.transform; var rb = s.Rb;
                if (s.Phase == Phase.Falling && stream && !paused) Stream(s, t, now, false);
                if (now < s.NextCheck) continue;
                s.NextCheck = now + (s.Phase == Phase.Resting ? 0.5 : 0.2);
                bool gone = rb == null; string why;
                float speed = gone ? 0f : rb.velocity.sqrMagnitude, moved = (t.position - s.LastPos).sqrMagnitude, turned = Quaternion.Angle(t.rotation, s.LastRot);
                bool resting = SettleRules.OwnerShouldReport(false, paused, s.UnpausedFor, gone, !gone && rb.IsSleeping(), !gone && rb.isKinematic, speed, moved, turned, out why);
                if (s.Phase == Phase.Resting)
                {
                    if (paused || (moved < SettleRules.MoveEpsSqr && turned < SettleRules.TurnEpsDeg)) continue;
                    if (!resting) { StartMoving(s, now, "moving again"); continue; }
                    // moved and already at rest again between two checks: the new exact pose
                    s.LastPos = t.position; s.LastRot = t.rotation; _moves++;
                    SendTxn(HeavyOp.Settle, s.Ns, s.Hash, s.Lease, t.position, t.rotation, "moved");
                    TryLoad(s, t);
                    continue;
                }
                if (!resting && !paused && now - s.DropAt > 8.0) { resting = true; why = "still moving after 8 s"; }   // bounded stream; a later real movement restarts it
                if (!resting) continue;
                bool bed = InCargoBed(s);
                string car = bed ? null : RestsOnVehicle(s.Pick.gameObject);
                if (car != null)
                {
                    if (_host) { _scratch.Add(s.Hash); SendTxn(HeavyOp.Remove, s.Ns, s.Hash, s.Lease, default(Vector3), Quaternion.identity, "rests on / in the vehicle '" + car + "' (host-local cargo)"); _dirty = true; continue; }
                    // client: fixed where it lies (never carried along by a car simulated here), simulated by nobody
                    if (stream) Stream(s, t, now, true);
                    SendTxn(HeavyOp.Settle, s.Ns, s.Hash, s.Lease, t.position, t.rotation, "rest pose");
                    SendTxn(HeavyOp.Release, s.Ns, s.Hash, s.Lease, default(Vector3), Quaternion.identity, "rests on the vehicle '" + car + "': fixed at this pose");
                    if (rb != null) { rb.velocity = Vector3.zero; rb.isKinematic = true; rb.useGravity = false; }
                    _scratch.Add(s.Hash); continue;
                }
                bool first = !s.Reported;
                s.LastPos = t.position; s.LastRot = t.rotation; s.Reported = true; s.Phase = Phase.Resting;
                if (stream) Stream(s, t, now, true);                               // final snapshot = the exact rest pose
                SendTxn(HeavyOp.Settle, s.Ns, s.Hash, s.Lease, t.position, t.rotation, first ? "rest pose" : "rest pose after a movement");
                if (first) _settles++;
                _log.Info("HEAVY '" + s.Name + "' at rest after " + (now - s.DropAt).ToString("0.0") + " s (" + why + ") at " + V3(t.position) + ": exact pose committed (lease " + s.Lease + ", " + s.Sent + " movement snapshot(s) sent)" + (bed ? " - in the cargo bed" : ""));
                if (bed) TryLoad(s, t);
            }
            foreach (var h in _scratch)
            {
                Sim s; if (!_sims.TryGetValue(h, out s)) continue;
                _sims.Remove(h);
                if (s.Pick == null || s.Ns != region) SendTxn(HeavyOp.Release, s.Ns, s.Hash, s.Lease, default(Vector3), Quaternion.identity, s.Pick == null ? "object destroyed" : "region left");
                _dirty = true;
            }
        }

        static string V3(Vector3 v) { return v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00"); }

        // ======================================================================== H2 cargo bed
        bool InCargoBed(Sim s)
        {
            ushort vid; Vector3 lp; Quaternion lq;
            return HeavyCargoSync.Instance != null && s.Pick != null && HeavyCargoSync.Instance.InBed(s.Pick.gameObject, out vid, out lp, out lq);
        }
        // The held object rests in the original cargo bed (its rest pose is already committed): ask the host to load it, once per
        // rest. Host: decided in place; client: proposal (result in OnLoadResult). Same request for both players.
        void TryLoad(Sim s, Transform t)
        {
            if (s.LoadTried || HeavyCargoSync.Instance == null) return;
            ushort vid; Vector3 lp; Quaternion lq;
            if (!HeavyCargoSync.Instance.InBed(s.Pick.gameObject, out vid, out lp, out lq)) return;
            s.LoadTried = true; _loadsSent++;
            SendTxn(HeavyOp.Load, s.Ns, s.Hash, s.Lease, lp, lq, vid, s.Cargo, "rests in the cargo bed of vehicle " + vid);
        }

        // H2 API for HeavyCargoSync
        // This instance runs the original carry / physics of that object (never removed by the cargo presenter).
        public bool HoldsLocally(Component pick)
        {
            if (pick == null) return false;
            foreach (var s in _sims.Values) if (ReferenceEquals(s.Pick, pick)) return true;
            return false;
        }
        // The object has (or had) shared heavy state here (host save: such objects are saved through their record, not the sensor).
        public bool Knows(Component pick)
        {
            if (pick == null) return false;
            if (HoldsLocally(pick)) return true;
            foreach (var o in _objs.Values) if (ReferenceEquals(o.Pick, pick)) return true;
            return false;
        }
        // The cargo presenter takes this synthetic object back into its car: stop presenting / simulating it here.
        public void Forget(uint hash)
        {
            if (!_ok) return;
            EndMotion(hash);
            _sims.Remove(hash); _records.Remove(hash);
            Obj o; if (_objs.TryGetValue(hash, out o)) { Unhide(o); RestoreInterp(o); _objs.Remove(hash); }
        }

        // ======================================================================== host catalog check (WorldHost.HeavyCatalog)
        ProposalCode HostCatalog(int ns, uint hash, uint digest, out string why)
        {
            why = null;
            var cat = HostLootCatalogs.Get(ns);
            if (cat == null) { if (HostLootCatalogs.Pending(ns)) { why = "host catalog of " + IdentityNamespace.Name(ns) + " still building"; return ProposalCode.Busy; } why = "no host catalog of " + IdentityNamespace.Name(ns); return ProposalCode.NotAuthorized; }
            if (cat.Digest != digest) { why = "census digest " + digest.ToString("X8") + " differs from the host scene file " + cat.Digest.ToString("X8"); return ProposalCode.NotAuthorized; }
            LootEntry e;
            if (!cat.TryGet(hash, out e) || !HeavyRules.Supported(ns, e.Flags)) { why = "not a supported heavy object of " + IdentityNamespace.Name(ns); return ProposalCode.NotAuthorized; }
            return ProposalCode.Accepted;
        }

        public string Stats
        {
            get
            {
                var b = _host && _world.Host != null ? _world.Host.Heavy : null;
                return "HEAVY_STATS " + (_host ? "p1Leases " + _p1Leases : "requests " + _p2Requests + " granted " + _p2Granted + " refused " + _p2Refused + " grantAborted " + _grantAborted + " lost " + _lost + " dropRefused " + _dropRefused + " depositBlocked " + _depositBlocked)
                    + " held " + _sims.Count + " drops " + _drops + " settles " + _settles + " moves " + _moves + " releases " + _releases + " removes " + _removes + " forcedEnds " + _forcedEnds + " associateBlocked " + _associateBlocked
                    + " shown hidden/placed/restored " + _hidden + "/" + _placed + "/" + _restored + " tracked " + _objs.Count
                    + " | motion sent " + _motionSent + " (" + _motionSentBytes + " B) recv " + _motionRecv + " (" + _motionRecvBytes + " B) stale " + _filter.Stale + " rejected " + _filter.Rejected + " malformed " + _motionMalformed + " unknown " + _motionUnknown
                    + " following " + _motions.Count + " followed " + _followed + " maxCorrection " + _correctionMax.ToString("0.000") + "m adopted " + _adopted
                    + " | cargo loads sent " + _loadsSent + " done " + _loadsDone + " refused " + _loadsRefused + " picks " + _cargoPicks
                    + (b != null ? " | book total " + b.Total + " leases " + b.Leases + " drops " + b.Drops + " settles " + b.Settles + " releases " + b.Releases + " removes " + b.Removes + " refused " + b.Refused + " cleared " + b.Cleared + " slotReleases " + b.SlotReleases + " handovers " + b.Handovers + " stowed " + b.Stowed : "")
                    + " frameMax " + _costMaxMs.ToString("0.00") + "ms";
            }
        }

        // Session end: every object this instance changed returns to an original single-player state (hidden -> scene pose;
        // placed -> stays there, original physics again). Held objects keep running the original locally.
        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            _node.MessageReceived -= OnMessage;
            try
            {
                EndAllMotions();
                foreach (var o in _objs.Values)
                {
                    if (o.Go == null) continue;
                    if (o.Shown == HeavyRules.Show.Hidden && !o.Synthetic) Restore(o);
                    else if (o.Shown == HeavyRules.Show.AtPose) { Unhide(o); if (o.Rb != null) { o.Rb.isKinematic = false; o.Rb.useGravity = true; } }
                }
                _objs.Clear(); _sims.Clear();
            }
            catch (Exception ex) { _log.Error("HEAVY restore at session end failed", ex); }
            if (_host && _world.Host != null && _world.Host.HeavyCatalog == (WorldHost.HeavyCatalogFn)HostCatalog) _world.Host.HeavyCatalog = null;
            if (!_host && _world.ProposalResultHook8 == (Func<uint, ProposalCode, bool>)OnResult) _world.ProposalResultHook8 = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
