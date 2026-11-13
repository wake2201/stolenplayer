// W4.2 Phase A vertical slice: ordinary doors (Madison Street) on the host-owned region-object registry
// (design + authority/validation boundaries: src/Shared/RegionState.cs).
//
// Verified IL (Cecil, read-only, Assembly-CSharp 1.27c):
//   - Player door use: PlayerScript.Update tag "Door" + Door.canUse + !brokeHinge (IL_47f2..4852), E down -> assignDoor =
//     the door, Door.load_slow = true (IL_4947..4953); released -> Door.SlowFailed() (IL_4994) and assignDoor = null
//     (IL_499b / IL_459f). Door.Update toggles isOpen for the slow (hold-E) movement (IL_00b5..00bd), Door.SlowFailed ->
//     Door.Use() toggles it for the quick press. Every player change of isOpen therefore happens while assignDoor points
//     at that door or right after (same frame) -> only doors the LOCAL player touches are watched (no scene scan).
//   - NPCs use Door.OpenDoorNPC / CloseDoorNPC / NPCUse (local NPC simulation, not shared in this milestone - AI1).
//   - Door has no save key (Door.Start: no SPrefs access; OpenOnStart / randomlyOpen = Random.Range per load) -> lifetime
//     RegionLife (the host clears the region's states when it empties = vanilla reload).
//   - Locked doors carry another tag (Lock1 / ElectricDoor, Door.set_tag_after_unlock) -> a committed open state is not
//     forced onto a door that is locked on this instance (locks are W4.2B).
// Remote apply = Door.Use() minus its local-player side effects (PlayerMotorBehavior.canMove, mouse sensitivity, noise):
// isOpen, openedByPlayer, opening, openSpeed = 150 x speedMultiplier, canUse = false + open/close clip; the original
// Door.Update swings it and Door.Resett re-enables it (same bridge as the M5.0 car doors, VehicleSync.SwingDoor). Doors
// restored at region load swing silently.
using System;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class WorldDoorSync : IDisposable
    {
        public static WorldDoorSync Instance;
        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        bool _ok;
        static readonly string Key = ObjectStateRules.RegistryKey(ObjectKind.Door);

        Func<object, object> _gmPs, _psAssign; Func<object, bool> _gmLoaded;
        Func<object, bool> _open, _opening, _canUse, _slowly;
        readonly List<Func<object, bool>> _special = new List<Func<object, bool>>();   // isCarDoor, isDrawer, ... (not ordinary doors)
        Action<object, bool> _setOpen, _setByPlayer, _setOpening, _setCanUse; Action<object, float> _setSpeed;
        Func<object, float> _speedMul, _maxVolume; Func<object, AudioSource> _sound; Func<object, AudioClip> _clipOpen, _clipClose;

        sealed class Watch { public Component Door; public bool Open; public double Since; public WorldObjectId Id; }
        readonly List<Watch> _watch = new List<Watch>(4);
        readonly Dictionary<uint, uint> _pendingByTxn = new Dictionary<uint, uint>();   // client proposal id -> door hash
        readonly Dictionary<uint, ushort> _expect = new Dictionary<uint, ushort>();      // door hash -> version after our in-flight change(s)
        ObjectStateList _list = new ObjectStateList(); uint _seenHead = uint.MaxValue; uint _listRev = uint.MaxValue;
        int _region = Regions.None; double _regionAt, _retryAt = -1; bool _restored; uint _txn;
        long _local, _proposed, _committed, _refused, _applied, _silent, _skipLocked, _skipBusy, _unidentified;
        readonly HashSet<uint> _loggedSkip = new HashSet<uint>();

        public WorldDoorSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var d = AccessTools.TypeByName("Door");
            _gmPs = Fast.Getter<object>(gm, "PS"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap"); _psAssign = Fast.Getter<object>(ps, "assignDoor");
            _open = Fast.Getter<bool>(d, "isOpen"); _opening = Fast.Getter<bool>(d, "opening"); _canUse = Fast.Getter<bool>(d, "canUse"); _slowly = Fast.Getter<bool>(d, "openingSlowly");
            foreach (var f in new[] { "isCarDoor", "isDrawer", "isWindow", "isGate", "isCupboard", "isFridge", "isFileCabinet", "isGlassCabinet", "isContainer" })
            {
                var g = Fast.Getter<bool>(d, f);
                if (g == null) { _log.Error("WDOOR Door." + f + " not found: shared doors disabled"); return; }
                _special.Add(g);
            }
            _setOpen = Fast.Setter<bool>(d, "isOpen"); _setByPlayer = Fast.Setter<bool>(d, "openedByPlayer"); _setOpening = Fast.Setter<bool>(d, "opening"); _setCanUse = Fast.Setter<bool>(d, "canUse");
            _setSpeed = Fast.Setter<float>(d, "openSpeed"); _speedMul = Fast.Getter<float>(d, "speedMultiplier"); _maxVolume = Fast.Getter<float>(d, "maxVolume");
            _sound = Fast.Getter<AudioSource>(d, "DoorSound"); _clipOpen = Fast.Getter<AudioClip>(d, "DoorOpen"); _clipClose = Fast.Getter<AudioClip>(d, "DoorClose");
            _ok = _gmPs != null && _gmLoaded != null && _psAssign != null && _open != null && _opening != null && _canUse != null && _setOpen != null && _setByPlayer != null
                && _setOpening != null && _setCanUse != null && _setSpeed != null && _speedMul != null;
            if (!_ok) { _log.Error("WDOOR original Door / PlayerScript fields not found: shared doors disabled"); return; }
            if (_host) world.Host.ObjectCatalog = Catalog;
            else world.ProposalResultHook7 = OnResult;
            Instance = this;
            _log.Info("WDOOR ready (" + (_host ? "host: commits its own door changes, decides Player 2's" : "client: proposes its door changes") + "; slice: ordinary doors in Madison Street; locks/windows/NPC doors not shared yet)");
        }

        static bool? Catalog(ObjectKind k, int ns, uint hash)
        {
            var c = WorldCensus.Instance;
            return k == ObjectKind.Door && c != null ? c.DoorKnown(ns, hash) : (bool?)null;
        }

        static double Now { get { return Time.realtimeSinceStartup; } }

        bool Ordinary(Component door)
        {
            for (int i = 0; i < _special.Count; i++) if (_special[i](door)) return false;
            return true;
        }

        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            int region = _world.LocalRegion;
            if (region != _region)
            {
                _region = region; _regionAt = now; _restored = false; _watch.Clear(); _expect.Clear(); _seenHead = uint.MaxValue; _loggedSkip.Clear();
            }
            if (region == Regions.None || !ObjectStateRules.SupportedNamespace(ObjectKind.Door, region)) return;   // no work elsewhere
            var gm = _world.GameManagerObject; var census = WorldCensus.Instance;
            if (gm == null || census == null || !_gmLoaded(gm)) return;
            if (!_restored)
            {
                if (now - _regionAt < 0.5) return;   // every Door.Start of the region ran (SaveRot, OpenOnStart)
                _restored = true; _seenHead = _world.Registry.HeadRev;
                Reconcile(census, true, "region load");
            }
            else if (_world.Registry.HeadRev != _seenHead || (_retryAt > 0 && now >= _retryAt)) { _seenHead = _world.Registry.HeadRev; _retryAt = -1; Reconcile(census, false, "update"); }
            Detect(gm, census, now);
        }

        // ---- local player changes ----
        void Detect(UnityEngine.Object gm, WorldCensus census, double now)
        {
            var ps = _gmPs(gm);
            var assigned = ps != null ? _psAssign(ps) as Component : null;
            if (assigned != null && Find(assigned) < 0 && _watch.Count < 4)
            {
                WorldObjectId id;
                if (!Ordinary(assigned)) { }
                else if (!census.TryDoorId(assigned, out id) || id.Namespace != _region) { _unidentified++; if (_loggedSkip.Add((uint)assigned.GetInstanceID())) _log.Info("WDOOR door '" + assigned.name + "' has no unambiguous census id: stays local"); }
                else _watch.Add(new Watch { Door = assigned, Open = _open(assigned), Since = now, Id = id });
            }
            for (int i = _watch.Count - 1; i >= 0; i--)
            {
                var w = _watch[i];
                if (w.Door == null) { _watch.RemoveAt(i); continue; }
                bool open = _open(w.Door);
                if (open != w.Open)
                {
                    w.Open = open; w.Since = now;
                    bool slow = _slowly != null && _slowly(w.Door);
                    LocalChange(w.Id, (byte)((open ? ObjectStateRules.DoorOpen : 0) | (slow ? ObjectStateRules.DoorSlow : 0)), now);
                }
                else if (!ReferenceEquals(w.Door, assigned) && !_opening(w.Door) && now - w.Since > 0.3) _watch.RemoveAt(i);
            }
        }

        int Find(Component door) { for (int i = 0; i < _watch.Count; i++) if (ReferenceEquals(_watch[i].Door, door)) return i; return -1; }

        ushort BaseVersion(uint hash)
        {
            ushort v; if (_expect.TryGetValue(hash, out v)) return v;
            ObjectState s; return _list.TryGet(hash, out s) ? s.Ver : (ushort)0;
        }

        void LocalChange(WorldObjectId id, byte state, double now)
        {
            _local++;
            RefreshList();
            var req = new ObjectTxnRequest { Txn = ++_txn, Kind = (byte)ObjectKind.Door, Ns = id.Namespace, Hash = id.Hash, State = state, BaseVer = BaseVersion(id.Hash) };
            if (_host)
            {
                string why;
                var code = _world.Host.ObjectTransaction(_world.LocalSlot, req, now, out why);
                _world.FlushOutbox();
                if (code == ProposalCode.Accepted) _committed++; else _refused++;
                _log.Info("WDOOR host " + req + " -> " + code + (why != null ? " (" + why + ")" : ""));
                if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) _retryAt = now + 0.25;   // re-assert the committed state
                return;
            }
            if (!_node.IsConnected || !_world.Synced) { _log.Info("WDOOR " + req + " not proposed (offline): the door stays local until the next reconcile"); return; }
            uint pid = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ObjectTxn, Value = req.ToBytes() });
            _pendingByTxn[pid] = id.Hash;
            _expect[id.Hash] = (ushort)(req.BaseVer == ushort.MaxValue ? 1 : req.BaseVer + 1);
            _proposed++;
            _log.Info("WDOOR proposed #" + pid + " " + req);
        }

        bool OnResult(uint pid, ProposalCode code)
        {
            uint hash;
            if (!_pendingByTxn.TryGetValue(pid, out hash)) return false;
            _pendingByTxn.Remove(pid);
            bool stillPending = false; foreach (var v in _pendingByTxn.Values) if (v == hash) { stillPending = true; break; }
            if (code == ProposalCode.Accepted) { _committed++; if (!stillPending) _expect.Remove(hash); }
            else
            {
                _refused++; _expect.Remove(hash);
                _log.Info("WDOOR proposal #" + pid + " " + IdentityNamespace.Name(_region) + ":" + hash.ToString("X8") + " -> " + code + (code == ProposalCode.Unchanged ? "" : ": the committed state is restored"));
                _retryAt = Now + 0.1;   // reconcile (committed state wins)
            }
            return true;
        }

        bool Pending(uint hash) { foreach (var v in _pendingByTxn.Values) if (v == hash) return true; return false; }

        // ---- committed state -> local doors ----
        void RefreshList()
        {
            var e = _world.Registry.Get(StateScope.Region, _region, Key);
            uint rev = e != null ? e.Rev : 0;
            if (rev == _listRev) return;
            _listRev = rev;
            ObjectStateList l;
            _list = e != null && ObjectStateList.TryParse(e.Value, out l) ? l : new ObjectStateList();
        }

        void Reconcile(WorldCensus census, bool silent, string why)
        {
            RefreshList();
            int swung = 0;
            var ps = _world.GameManagerObject != null ? _gmPs(_world.GameManagerObject) : null;
            var assigned = ps != null ? _psAssign(ps) as Component : null;
            foreach (var s in _list.Items)
            {
                Component door;
                if (!census.TryResolve(new WorldObjectId(_region, s.Hash), out door) || door == null || !Ordinary(door)) continue;
                var a = WorldDoorRules.Decide(s.State, _open(door), _opening(door), ReferenceEquals(door, assigned), door.CompareTag("Door"), Pending(s.Hash));
                switch (a)
                {
                    case DoorApply.Swing: Swing(door, (s.State & ObjectStateRules.DoorOpen) != 0, silent); swung++; break;
                    case DoorApply.SkipLocked: _skipLocked++; if (_loggedSkip.Add(s.Hash)) _log.Info("WDOOR " + IdentityNamespace.Name(_region) + ":" + s.Hash.ToString("X8") + " committed open but locked here (tag " + door.tag + "): not forced (locks = W4.2B)"); break;
                    case DoorApply.SkipBusy: _skipBusy++; _retryAt = Now + 0.25; break;   // retry once the swing / local use ended
                }
            }
            if (swung > 0) _log.Info("WDOOR " + why + ": " + swung + " door(s) of " + Regions.Name(_region) + " set to the committed state" + (silent ? " (silently, region load)" : ""));
        }

        void Swing(Component door, bool open, bool silent)
        {
            _setOpen(door, open); _setByPlayer(door, open); _setOpening(door, true);
            _setSpeed(door, 150f * _speedMul(door)); _setCanUse(door, false);
            int w = Find(door); if (w >= 0) _watch[w].Open = open;   // never echoed back as a local change
            if (silent) { _silent++; return; }
            _applied++;
            var src = _sound != null ? _sound(door) : null;
            var clip = open ? (_clipOpen != null ? _clipOpen(door) : null) : (_clipClose != null ? _clipClose(door) : null);
            if (src != null && clip != null) { if (_maxVolume != null) src.volume = _maxVolume(door); src.pitch = 1f; src.PlayOneShot(clip); }
        }

        public string Stats
        {
            get
            {
                return "WDOOR_STATS local=" + _local + " proposed=" + _proposed + " committed=" + _committed + " refused=" + _refused + " applied=" + _applied + " restoredSilently=" + _silent
                    + " skippedLocked=" + _skipLocked + " skippedBusy=" + _skipBusy + " unidentified=" + _unidentified + (_host && _world.Host != null ? " | book " + _world.Host.Objects.Stats : "");
            }
        }

        public void Dispose()
        {
            if (_host && _world.Host != null && _world.Host.ObjectCatalog == (Func<ObjectKind, int, uint, bool?>)Catalog) _world.Host.ObjectCatalog = null;
            if (!_host && _world.ProposalResultHook7 == (Func<uint, ProposalCode, bool>)OnResult) _world.ProposalResultHook7 = null;
            if (Instance == this) Instance = null;
        }
    }
}
