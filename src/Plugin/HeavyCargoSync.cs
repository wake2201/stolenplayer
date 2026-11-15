// H2 shared heavy vehicle cargo on the ORIGINAL Ranger cargo bed (design + verified original flow: src/Shared/HeavyCargo.cs).
//  - Presentation: every entry of the host list "hcargo" (arg = vehicle id) is ONE original drop_object instance of its item,
//    parented to this instance's own copy of the car (VanHeavyItems transform = car root) at the committed car-local pose,
//    kinematic, PlacedInTrunk - exactly the original VanHeavyItems.LoadItems representation of saved cargo
//    (blockHeavysOnStart branch). The car object itself is VehicleSync's (driven here, or observed and moved kinematically), so
//    the cargo follows it on both instances; no physics is simulated or streamed for stored cargo.
//  - Load detection (HeavySync.SimFrame, holder of the lease): the object came to rest and the ORIGINAL cargo sensor
//    (VanHeavyItems.triggerSensor = the criterion of the original SaveItems) lists it, or it lies inside that sensor's volume.
//  - Unload: a cargo object is picked up with the original PickUpHeavy -> HeavySync asks the host for a lease on its synthetic
//    id (HeavyCargo.Synthetic); from then on it is an H1 heavy record (drop, motion, settle, handover, re-load).
//  - Persistence: host - VehiclesManager.SaveAllCarsTrunks postfix writes the committed list in the original SaveItems key
//    format (also when the host's car object is inactive: the original skips SaveItems then); the first time the host sees its
//    car in a session the list is seeded from those keys. Player 2 never runs SaveItems / LoadItems during a session (its
//    sandbox never holds or restores cargo). A cargo object outside any host-committed state is never kept on Player 2.
// Harmony (this class): VanHeavyItems.LoadItems (pre, both: the registry owns the cargo during a session),
// VanHeavyItems.SaveItems (pre, both: skipped - it would save only the sensor view), VehiclesManager.SaveAllCarsTrunks (post,
// host: seeds first if needed, then the original keys from the committed list),
// VanHeavyItems.UnblockHeavy (post, both: committed cargo stays at its committed bed pose - see DESIGN COMPROMISE in PROGRESS H2).
// Per frame: two reference compares + the registry head revision; lists are parsed only when their entry revision changes.
// Event-only: spawning (one Instantiate per new entry), save, bed check at a rest report. No scene-wide search per frame
// (VehiclesManager is looked up once per scene instance).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class HeavyCargoSync : IDisposable
    {
        public static HeavyCargoSync Instance;
        public static bool Applying;   // our own PlacedInTrunk calls (HeavySync's deposit hook ignores them)

        readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        Type _vanT, _vmT, _pickT, _carT;
        Func<object, object> _gmPs, _psItems2, _vmFirst, _carVan, _vanSensor, _itemDrop;
        Func<object, int> _carNr, _pickId, _itemId; Func<object, bool> _vanLoaded, _pickHeavy;
        Action<object, bool> _setX10, _setAnyXp; Action<object> _placedInTrunk;
        PropertyInfo _detected;
        Func<string, int> _getInt; Action<string, int> _setInt; Func<string, float> _getFloat; Action<string, float> _setFloat;

        // the supported car of the current scene instance (VehiclesManager.firstCar with its VanHeavyItems)
        object _boundGm; Component _car, _van; Transform _root; ushort _vid; Component _sensor; readonly List<Collider> _sensorCols = new List<Collider>();
        bool _adoptChecked;

        sealed class CObj
        {
            public ushort Id; public ushort ItemId; public GameObject Go; public Component Pick; public Rigidbody Rb;
            public bool InCar; public Vector3 LocalPos; public Quaternion LocalRot; public double UnrefSince = -1;
        }
        readonly Dictionary<ushort, CObj> _objs = new Dictionary<ushort, CObj>();
        readonly Dictionary<int, CObj> _byInstance = new Dictionary<int, CObj>();
        readonly List<CargoEntry> _entries = new List<CargoEntry>();
        readonly HashSet<ushort> _refs = new HashSet<ushort>();
        readonly List<ushort> _drop = new List<ushort>();
        uint _seenHead = uint.MaxValue, _seenEpoch, _cargoRev = uint.MaxValue, _heavyRev = uint.MaxValue; int _seenRegion = int.MinValue; bool _dirty = true, _seenProxy;
        readonly HashSet<string> _logged = new HashSet<string>();
        long _spawned, _placed, _destroyed, _adopted, _saves, _seeded, _loadSkipped, _saveSkipped, _refrozen;
        double _nextStats, _costMaxMs;
        const double DestroyGrace = 3.0;   // an object unreferenced this long (no entry, no record) is gone everywhere
        static double Now { get { return Time.realtimeSinceStartup; } }

        public HeavyCargoSync(InstanceLog log, WorldSync world, bool host)
        {
            _log = log; _world = world; _host = host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var it = AccessTools.TypeByName("ItemID");
                _vanT = AccessTools.TypeByName("VanHeavyItems"); _vmT = AccessTools.TypeByName("VehiclesManager"); _pickT = AccessTools.TypeByName("Pickupable"); _carT = AccessTools.TypeByName("CarController");
                var sp = AccessTools.TypeByName("SPrefs");
                _gmPs = Fast.Getter<object>(gm, "PS"); _psItems2 = Fast.Getter<object>(ps, "item_IDs2");
                _vmFirst = Fast.Getter<object>(_vmT, "firstCar"); _carVan = Fast.Getter<object>(_carT, "van_heavy"); _carNr = Fast.Getter<int>(_carT, "carNr");
                _vanSensor = Fast.Getter<object>(_vanT, "triggerSensor"); _vanLoaded = Fast.Getter<bool>(_vanT, "loaded");
                _pickId = Fast.Getter<int>(_pickT, "item_ID"); _pickHeavy = Fast.Getter<bool>(_pickT, "is_heavy_item");
                _setX10 = Fast.Setter<bool>(_pickT, "x10XP"); _setAnyXp = Fast.Setter<bool>(_pickT, "add_any_XP"); _placedInTrunk = Fast.Call0(_pickT, "PlacedInTrunk");
                _itemId = Fast.Getter<int>(it, "item_ID"); _itemDrop = Fast.Getter<object>(it, "drop_object");
                var gi = AccessTools.Method(sp, "GetInt", new[] { typeof(string) }); var si = AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) });
                var gf = AccessTools.Method(sp, "GetFloat", new[] { typeof(string) }); var sf = AccessTools.Method(sp, "SetFloat", new[] { typeof(string), typeof(float) });
                if (gi != null) _getInt = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                if (si != null) _setInt = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), si);
                if (gf != null) _getFloat = (Func<string, float>)Delegate.CreateDelegate(typeof(Func<string, float>), gf);
                if (sf != null) _setFloat = (Action<string, float>)Delegate.CreateDelegate(typeof(Action<string, float>), sf);
                var load = AccessTools.Method(_vanT, "LoadItems", Type.EmptyTypes); var save = AccessTools.Method(_vanT, "SaveItems", Type.EmptyTypes);
                var unblock = AccessTools.Method(_vanT, "UnblockHeavy", Type.EmptyTypes); var saveAll = AccessTools.Method(_vmT, "SaveAllCarsTrunks", Type.EmptyTypes);
                if (_gmPs == null || _psItems2 == null || _vmFirst == null || _carVan == null || _carNr == null || _vanSensor == null || _vanLoaded == null || _pickId == null || _pickHeavy == null
                    || _setX10 == null || _setAnyXp == null || _placedInTrunk == null || _itemId == null || _itemDrop == null || _getInt == null || _setInt == null || _getFloat == null || _setFloat == null
                    || load == null || save == null || unblock == null || saveAll == null || world.LocalSlot == 0)
                { _log.Warn("CARGO game API incomplete: shared heavy cargo disabled (heavy deposits stay as in H1)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.cargo");
                _h.Patch(load, new HarmonyMethod(AccessTools.Method(typeof(HeavyCargoSync), "LoadPrefix")));
                _h.Patch(unblock, null, new HarmonyMethod(AccessTools.Method(typeof(HeavyCargoSync), "UnblockPostfix")));
                // the original SaveItems writes only what the sensor sees (nothing of the shared list, which LoadItems no longer
                // spawns from the keys): skipped on both instances; the host writes the committed list in the same format instead
                _h.Patch(save, new HarmonyMethod(AccessTools.Method(typeof(HeavyCargoSync), "SavePrefix")));
                if (_host) _h.Patch(saveAll, null, new HarmonyMethod(AccessTools.Method(typeof(HeavyCargoSync), "SaveAllPostfix")));
                if (_host && world.Host != null) world.Host.HeavyItemId = HostItemId;
                _ok = true; Instance = this;
                _log.Info("CARGO shared heavy cargo ready (" + (_host ? "host: cargo list committed + saved here" : "client: cargo shown from the host list, never saved here") + "; Ranger VanHeavyItems bed, capacity " + HeavyCargo.Capacity + ")");
            }
            catch (Exception ex) { _log.Error("CARGO init failed: shared heavy cargo disabled", ex); _ok = false; }
        }

        public bool Ready { get { return _ok; } }
        void LogOnce(string key, string msg) { if (_logged.Count < 128 && _logged.Add(key)) _log.Info(msg); }

        // ======================================================================== API for HeavySync
        // The cargo object (entry or synthetic record) this Pickupable is, as its synthetic heavy id.
        public bool TryCargoObject(Component pick, out uint hash)
        {
            hash = 0; CObj o;
            if (!_ok || pick == null || !_byInstance.TryGetValue(pick.GetInstanceID(), out o) || o.Go == null) return false;
            hash = HeavyCargo.Synthetic(o.ItemId, o.Id);
            return true;
        }

        // The local object of a synthetic record (created from the item's original drop_object when this instance has none yet,
        // e.g. after its scene reloaded). HeavySync presents it from the record.
        public Component SyntheticObject(uint hash)
        {
            if (!_ok || !HeavyCargo.IsSynthetic(hash)) return null;
            var o = Ensure(HeavyCargo.IdOf(hash), (ushort)HeavyCargo.ItemOf(hash), Vector3.zero, Quaternion.identity, false);
            return o != null ? o.Pick : null;
        }

        public bool IsCargoCar(Component car) { return _ok && car != null && _car != null && ReferenceEquals(car, _car) && SupportedHere; }
        bool SupportedHere { get { return _van != null && _root != null && _vid != VehicleIds.None; } }

        // The object lies in the ORIGINAL cargo bed of the supported car: listed by the original cargo sensor (what SaveItems saves)
        // or inside the sensor's trigger volume. Out: vehicle id and the car-local pose (VanHeavyItems transform space).
        public bool InBed(GameObject go, out ushort vid, out Vector3 localPos, out Quaternion localRot)
        {
            vid = VehicleIds.None; localPos = Vector3.zero; localRot = Quaternion.identity;
            if (!_ok || go == null) return false;
            Bind();
            if (!SupportedHere || !_root.gameObject.activeInHierarchy) return false;
            bool listed = false;
            try { var l = _sensor != null && _detected != null ? _detected.GetValue(_sensor, null) as List<GameObject> : null; listed = l != null && l.Contains(go); }
            catch { listed = false; }
            if (!listed)
            {
                var c = Center(go);
                foreach (var col in _sensorCols) if (col != null && col.enabled && col.bounds.Contains(c)) { listed = true; break; }
            }
            if (!listed) return false;
            localPos = _root.InverseTransformPoint(go.transform.position); localRot = Quaternion.Inverse(_root.rotation) * go.transform.rotation;
            if (!HeavyCargo.LocalPoseOk(new Vec3(localPos.x, localPos.y, localPos.z))) return false;
            vid = _vid;
            return true;
        }

        static Vector3 Center(GameObject go)
        {
            bool any = false; var b = new Bounds();
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) { if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds); }
            return any ? b.center : go.transform.position;
        }

        // ======================================================================== car binding (once per scene instance)
        void Bind()
        {
            var gm = _world.GameManagerObject;
            if (ReferenceEquals(gm, _boundGm) && (gm == null || _car != null)) return;
            if (!ReferenceEquals(gm, _boundGm)) SceneChanged();
            _boundGm = gm;
            if (gm == null) return;
            var vm = UnityEngine.Object.FindObjectOfType(_vmT) as Component;   // once per scene instance (the original TrySaveTrunkItem does the same)
            var car = vm != null ? _vmFirst(vm) as Component : null;
            if (car == null) { LogOnce("nocar", "CARGO VehiclesManager.firstCar not found: no shared cargo in this scene"); return; }
            _car = car;
            var van = _carVan(car) as Component;
            _vid = VehicleIds.FromCarNr(_carNr(car));
            if (van == null) { LogOnce("novan", "CARGO " + car.name + " has no VanHeavyItems: no cargo bed (nothing shared)"); _van = null; return; }
            _van = van; _root = van.transform;
            _sensor = _vanSensor(van) as Component;
            _sensorCols.Clear();
            if (_sensor != null)
            {
                foreach (var c in _sensor.GetComponents<Collider>()) if (c.isTrigger) _sensorCols.Add(c);
                if (_detected == null) _detected = AccessTools.Property(_sensor.GetType(), "DetectedObjects");
            }
            if (_host && _world.Host != null) _world.Host.CargoVehicles.Add(_vid);
            LogOnce("bind:" + _vid, "CARGO bound " + car.name + " (vehicle " + _vid + "): original VanHeavyItems bed, sensor " + (_sensor != null ? _sensor.name + " (" + _sensorCols.Count + " trigger collider(s))" : "missing") + ", loaded " + _vanLoaded(van));
            _dirty = true;
        }

        void SceneChanged()
        {
            // every object of the old scene instance is gone with it (children of its car / its scene)
            _objs.Clear(); _byInstance.Clear(); _entries.Clear();
            _car = null; _van = null; _root = null; _sensor = null; _sensorCols.Clear(); _vid = VehicleIds.None;
            _cargoRev = uint.MaxValue; _heavyRev = uint.MaxValue; _dirty = true;
        }

        // ======================================================================== per frame
        public void Update()
        {
            if (!_ok) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = Now;
            if (now >= _nextStats) { _nextStats = now + 30; if (_spawned + _placed + _saves + _seeded > 0) _log.Info(Stats); }
            Bind();
            if (_boundGm == null || !SupportedHere) return;
            if (_host && _world.Host != null && !_world.Host.Cargo.Seeded(_vid)) Seed();
            var reg = _world.Registry;
            bool proxy = OnTravelProxy();
            if (!_dirty && reg.HeadRev == _seenHead && reg.Epoch == _seenEpoch && _world.LocalRegion == _seenRegion && proxy == _seenProxy && _drop.Count == 0 && !AnyPending()) return;
            if (proxy != _seenProxy) _dirty = true;
            _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch; _seenRegion = _world.LocalRegion; _seenProxy = proxy;
            var ce = reg.Get(StateScope.Session, _vid, WorldKeys.Cargo);
            var he = _world.LocalRegion != Regions.None ? reg.Get(StateScope.Region, _world.LocalRegion, WorldKeys.Heavy) : null;
            uint crev = ce != null ? ce.Rev : 0u, hrev = he != null ? he.Rev : 0u;
            if (_dirty || crev != _cargoRev || hrev != _heavyRev || AnyPending()) Reconcile(ce, he, now);
            _cargoRev = crev; _heavyRev = hrev; _dirty = false;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _costMaxMs) _costMaxMs = ms;
        }

        bool OnTravelProxy() { var v = VehicleSync.Current; return v != null && v.IsTravelProxy(_vid); }

        bool AnyPending() { foreach (var o in _objs.Values) if (o.UnrefSince >= 0) return true; return false; }

        void Reconcile(StateEntry ce, StateEntry he, double now)
        {
            _entries.Clear();
            List<CargoEntry> l;
            if (ce != null)
            {
                if (HeavyCargo.TryParse(ce.Value, out l)) _entries.AddRange(l);
                else LogOnce("bad:" + ce.Rev, "CARGO registry list of vehicle " + _vid + " unreadable (both PCs need mod " + ProtocolInfo.ModVersion + ")");
            }
            _refs.Clear();
            // V1.2: a car that is only a local travel proxy here (hideout / Auto Shop AwayProxy) carries no cargo copies
            bool show = !_seenProxy;
            if (show) foreach (var e in _entries) _refs.Add(e.Id);
            // cargo objects out in the world are synthetic heavy records of this region (HeavySync presents them)
            List<HeavyRecord> hl;
            if (he != null && HeavyList.TryParse(he.Value, out hl)) foreach (var r in hl) if (r.Cargo) _refs.Add(HeavyCargo.IdOf(r.Hash));
            if (!_adoptChecked) { _adoptChecked = true; if (_vanLoaded(_van)) AdoptLoaded(); }
            if (show) foreach (var e in _entries)
            {
                var o = Ensure(e.Id, e.ItemId, ToV(e.Pos), ToQ(e.Rot), true);
                if (o == null) continue;
                PlaceInCar(o, ToV(e.Pos), ToQ(e.Rot), false);
            }
            // objects that no committed state references any more: destroyed after a short grace (the two registry entries of one
            // host step may arrive in different frames; HeavySync already hides a synthetic object whose record ended)
            _drop.Clear();
            foreach (var kv in _objs)
            {
                var o = kv.Value;
                if (o.Go == null) { _drop.Add(kv.Key); continue; }
                bool inList = false; foreach (var e in _entries) if (e.Id == o.Id) { inList = true; break; }
                if (!inList) o.InCar = false;
                if (_refs.Contains(o.Id)) { o.UnrefSince = -1; continue; }
                if (HeavySync.Instance != null && HeavySync.Instance.HoldsLocally(o.Pick)) continue;   // our own carry: the original owns it
                if (o.UnrefSince < 0) { o.UnrefSince = now; continue; }
                if (now - o.UnrefSince >= DestroyGrace) _drop.Add(kv.Key);
            }
            foreach (var id in _drop)
            {
                CObj o; if (!_objs.TryGetValue(id, out o)) continue;
                _objs.Remove(id); if (o.Pick != null) _byInstance.Remove(o.Pick.GetInstanceID());
                if (o.Go != null) { UnityEngine.Object.Destroy(o.Go); _destroyed++; _log.Info("CARGO object #" + id + " (item " + o.ItemId + ") no longer in any car or in this region: removed here"); }
            }
            _drop.Clear();
        }

        static Vector3 ToV(Vec3 v) { return new Vector3(v.X, v.Y, v.Z); }
        static Quaternion ToQ(Quat16 q) { return new Quaternion(q.Fx, q.Fy, q.Fz, q.Fw); }

        // One object per cargo id on this instance: the existing one, else a new instance of the item's ORIGINAL drop_object with
        // the original LoadItems settings (x10XP / add_any_XP off: no reward from a cargo copy).
        CObj Ensure(ushort id, ushort itemId, Vector3 lp, Quaternion lq, bool inCar)
        {
            CObj o;
            if (_objs.TryGetValue(id, out o))
            {
                if (o.Go != null) return o;
                _objs.Remove(id); if (o.Pick != null) _byInstance.Remove(o.Pick.GetInstanceID());
            }
            var prefab = DropObject(itemId);
            if (prefab == null) { LogOnce("noitem:" + itemId, "CARGO item " + itemId + " has no drop_object: cargo object #" + id + " cannot be shown here"); return null; }
            GameObject go;
            Applying = true;
            try
            {
                Vector3 wp = inCar && _root != null ? _root.TransformPoint(lp) : lp;
                go = UnityEngine.Object.Instantiate(prefab, wp, Quaternion.identity);
                go.SetActive(true);
                var pick = go.GetComponent(_pickT);
                if (pick == null) { UnityEngine.Object.Destroy(go); LogOnce("nopick:" + itemId, "CARGO drop_object of item " + itemId + " has no Pickupable"); return null; }
                _setX10(pick, false); _setAnyXp(pick, false);
                o = new CObj { Id = id, ItemId = itemId, Go = go, Pick = pick, Rb = go.GetComponent<Rigidbody>() };
                if (o.Rb != null) o.Rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
            finally { Applying = false; }
            _objs[id] = o; _byInstance[o.Pick.GetInstanceID()] = o; _spawned++;
            return o;
        }

        GameObject DropObject(int itemId)
        {
            var ps = _world.GameManagerObject != null ? _gmPs(_world.GameManagerObject) : null;
            var arr = ps != null ? _psItems2(ps) as Array : null;
            if (arr == null) return null;
            object item = null;
            if (itemId >= 0 && itemId < arr.Length) { var x = arr.GetValue(itemId); if (x != null && _itemId(x) == itemId) item = x; }
            if (item == null) foreach (var x in arr) if (x != null && _itemId(x) == itemId) { item = x; break; }
            return item != null ? _itemDrop(item) as GameObject : null;
        }

        // The original saved-cargo state (LoadItems, blockHeavysOnStart branch): child of the car at the local pose, kinematic,
        // no gravity, PlacedInTrunk (trigger colliders, can_flash / add_any_XP off). HeavySync gives up its presentation first.
        void PlaceInCar(CObj o, Vector3 lp, Quaternion lq, bool force)
        {
            var t = o.Go.transform;
            bool same = o.InCar && t.parent == _root && (t.localPosition - lp).sqrMagnitude < 1e-6f && Quaternion.Angle(t.localRotation, lq) < 0.5f;
            if (same && !force) return;
            if (HeavySync.Instance != null) HeavySync.Instance.Forget(HeavyCargo.Synthetic(o.ItemId, o.Id));
            o.UnrefSince = -1;
            Applying = true;
            try
            {
                if (!o.Go.activeSelf) o.Go.SetActive(true);
                t.SetParent(_root, false);
                t.localPosition = lp; t.localRotation = lq;
                if (o.Rb != null) { if (!o.Rb.isKinematic) { o.Rb.velocity = Vector3.zero; o.Rb.angularVelocity = Vector3.zero; } o.Rb.useGravity = false; o.Rb.isKinematic = true; }
                _placedInTrunk(o.Pick);
            }
            catch (Exception ex) { _log.Error("CARGO placing #" + o.Id + " failed", ex); }
            finally { Applying = false; }
            bool first = !o.InCar;
            o.InCar = true; o.LocalPos = lp; o.LocalRot = lq;
            if (first) { _placed++; _log.Info("CARGO #" + o.Id + " (item " + o.ItemId + ", '" + o.Go.name + "') in " + (_car != null ? _car.name : "car") + " at local " + V3(lp)); }
        }

        // ======================================================================== host: seed + save
        // The host's own saved keys (original SaveItems format) become the session's cargo list, once per host session.
        void Seed()
        {
            var items = new int[HeavyCargo.SaveSlots]; var pos = new Vec3[HeavyCargo.SaveSlots]; var eul = new Vec3[HeavyCargo.SaveSlots];
            for (int i = 0; i < HeavyCargo.SaveSlots; i++)
            {
                items[i] = _getInt("HeavyVan_" + i);
                if (items[i] <= 0) break;
                pos[i] = new Vec3(_getFloat("PosX_item" + i), _getFloat("PosY_item" + i), _getFloat("PosZ_item" + i));
                eul[i] = new Vec3(_getFloat("RotX_item" + i), _getFloat("RotY_item" + i), _getFloat("RotZ_item" + i));
            }
            var l = HeavyCargo.FromSaveSlots(items, pos, eul, e => { var q = Quaternion.Euler(e.X, e.Y, e.Z); return Quat16.From(q.x, q.y, q.z, q.w); });
            if (_world.Host.CargoSeed(_vid, l)) { _seeded++; _world.FlushOutbox(); _log.Info("CARGO vehicle " + _vid + " seeded from the host save: " + l.Count + " heavy object(s) (HeavyVan_ keys)"); }
            _dirty = true;
        }

        // The original LoadItems already ran before this session's hook (host started hosting from a loaded world): its objects
        // are the seeded entries - adopted one-to-one by item id instead of a second copy.
        void AdoptLoaded()
        {
            if (!_host) return;
            var free = new List<Component>();
            // only LoadItems' own instances ("(Clone)"): a census object the original parented into the car is never adopted
            foreach (Transform c in _root) { var p = c.GetComponent(_pickT); if (p != null && _pickHeavy(p) && c.name.EndsWith("(Clone)", StringComparison.Ordinal) && !_byInstance.ContainsKey(p.GetInstanceID()) && (HeavySync.Instance == null || !HeavySync.Instance.Knows(p))) free.Add(p); }
            foreach (var e in _entries)
            {
                if (_objs.ContainsKey(e.Id)) continue;
                for (int i = 0; i < free.Count; i++)
                {
                    if (_pickId(free[i]) != e.ItemId) continue;
                    var o = new CObj { Id = e.Id, ItemId = e.ItemId, Go = free[i].gameObject, Pick = free[i], Rb = free[i].GetComponent<Rigidbody>() };
                    _objs[e.Id] = o; _byInstance[o.Pick.GetInstanceID()] = o; free.RemoveAt(i); _adopted++;
                    break;
                }
            }
            if (_adopted > 0) _log.Info("CARGO adopted " + _adopted + " cargo object(s) the original LoadItems created before the session hook");
        }

        // Host save: the committed list in the original SaveItems key format (30 slots cleared, <= 24 written) + heavy objects the
        // original sensor sees in the bed that are not shared at all (e.g. MainScene heavy objects: host-local as before H2,
        // the original SaveItems would have saved them).
        void WriteSave()
        {
            Bind();
            if (_world.Host == null || _vid == VehicleIds.None || !_world.Host.CargoVehicles.Contains(_vid) || !_world.Host.Cargo.Seeded(_vid)) return;
            var l = _world.Host.Cargo.Of(_vid);
            var extra = new List<KeyValuePair<int, Transform>>();
            try
            {
                var det = _sensor != null && _detected != null && _root != null && _root.gameObject.activeInHierarchy ? _detected.GetValue(_sensor, null) as List<GameObject> : null;
                if (det != null)
                    foreach (var g in det)
                    {
                        if (g == null || g.tag == "HeavyCarried") continue;
                        var p = g.GetComponent(_pickT);
                        if (p == null || !_pickHeavy(p) || _byInstance.ContainsKey(p.GetInstanceID())) continue;
                        if (HeavySync.Instance != null && HeavySync.Instance.Knows(p)) continue;   // shared object: its record decides
                        extra.Add(new KeyValuePair<int, Transform>(_pickId(p), g.transform));
                    }
            }
            catch (Exception ex) { _log.Error("CARGO sensor read at save failed (only the shared cargo is saved)", ex); }
            for (int i = 0; i < HeavyCargo.SaveSlots; i++)
            {
                _setInt("HeavyVan_" + i, -1);
                _setFloat("PosX_item" + i, 0f); _setFloat("PosY_item" + i, 0f); _setFloat("PosZ_item" + i, 0f);
                _setFloat("RotX_item" + i, 0f); _setFloat("RotY_item" + i, 0f); _setFloat("RotZ_item" + i, 0f);
            }
            int n = 0;
            foreach (var e in l)
            {
                if (n >= HeavyCargo.Capacity) break;
                var eul = ToQ(e.Rot).eulerAngles;
                Slot(n++, e.ItemId, ToV(e.Pos), eul);
            }
            int extras = 0;
            foreach (var x in extra)
            {
                if (n >= HeavyCargo.Capacity || _root == null) break;
                Slot(n++, x.Key, _root.InverseTransformPoint(x.Value.position), (Quaternion.Inverse(_root.rotation) * x.Value.rotation).eulerAngles);
                extras++;
            }
            _saves++;
            _log.Info("CARGO host save: vehicle " + _vid + " cargo written to the original HeavyVan_ keys (" + Math.Min(l.Count, HeavyCargo.Capacity) + " shared" + (extras > 0 ? " + " + extras + " host-local" : "") + (l.Count > HeavyCargo.Capacity ? ", " + (l.Count - HeavyCargo.Capacity) + " beyond the original 24-slot save" : "") + ")");
        }
        void Slot(int i, int item, Vector3 p, Vector3 e)
        {
            _setInt("HeavyVan_" + i, item);
            _setFloat("PosX_item" + i, p.x); _setFloat("PosY_item" + i, p.y); _setFloat("PosZ_item" + i, p.z);
            _setFloat("RotX_item" + i, e.x); _setFloat("RotY_item" + i, e.y); _setFloat("RotZ_item" + i, e.z);
        }

        int HostItemId(int ns, uint hash)
        {
            LootEntry e;
            var cat = HostLootCatalogs.Get(ns);
            if (cat != null && cat.TryGet(hash, out e)) return e.ItemId;
            var live = WorldCensus.Instance != null ? WorldCensus.Instance.Loot(ns) : null;
            return live != null && live.TryGet(hash, out e) ? e.ItemId : 0;
        }

        // ======================================================================== original hooks
        static IEnumerator Empty() { yield break; }
        // VanHeavyItems.LoadItems (coroutine, started 0.5 s after every OnEnable): during a session the host list owns the cargo -
        // the original would add a second copy from this instance's keys (host) or restore sandbox keys (client).
        static bool LoadPrefix(object __instance, ref IEnumerator __result)
        {
            var i = Instance; if (i == null || !i._ok) return true;
            __result = Empty(); i._loadSkipped++;
            i.LogOnce("loadskip", "CARGO original VanHeavyItems.LoadItems skipped during the session: the host cargo list is shown instead");
            return false;
        }
        // Original SaveItems during a session: Player 2's sandbox never saves the shared cargo (Player 1's save is the only
        // canonical cargo state); on the host it would overwrite the keys with the sensor view only - SaveAllPostfix writes them.
        static bool SavePrefix()
        {
            var i = Instance; if (i == null || !i._ok) return true;
            i._saveSkipped++;
            i.LogOnce("saveskip", "CARGO original VanHeavyItems.SaveItems skipped during the session: " + (i._host ? "the committed cargo list is written instead" : "the host saves the shared cargo"));
            return false;
        }
        static void SaveAllPostfix()
        {
            var i = Instance; if (i == null || !i._ok) return;
            try
            {
                i.Bind();
                // a save before the first seed (keys still intact: SaveItems was skipped) seeds now, so the save never drops cargo
                if (i.SupportedHere && i._world.Host != null && !i._world.Host.Cargo.Seeded(i._vid)) i.Seed();
                i.WriteSave();
            }
            catch (Exception ex) { i._log.Error("CARGO host cargo save failed", ex); }
        }
        // Original CarController exit -> UnblockHeavy makes every sensed object dynamic and unparented. Committed cargo stays the
        // car's: back to its committed bed pose (kinematic, parented) so both instances keep showing the same state.
        static void UnblockPostfix(object __instance)
        {
            var i = Instance; if (i == null || !i._ok || !ReferenceEquals(__instance, i._van)) return;
            try { foreach (var o in i._objs.Values) if (o.InCar && o.Go != null) { i.PlaceInCar(o, o.LocalPos, o.LocalRot, true); i._refrozen++; } }
            catch (Exception ex) { i._log.Error("CARGO re-placing after UnblockHeavy failed", ex); }
        }

        static string V3(Vector3 v) { return v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00"); }

        public string Stats
        {
            get
            {
                var b = _host && _world.Host != null ? _world.Host.Cargo : null;
                return "CARGO_STATS vehicle " + _vid + " entries " + _entries.Count + " objects " + _objs.Count + " spawned " + _spawned + " placed " + _placed + " destroyed " + _destroyed + " adopted " + _adopted
                    + " refrozen " + _refrozen + " loadSkipped " + _loadSkipped + " saveSkipped " + _saveSkipped + " saves " + _saves + " seeded " + _seeded
                    + (b != null ? " | book loads " + b.Loads + " unloads " + b.Unloads + " restores " + b.Restores + " refused " + b.Refused : "") + " frameMax " + _costMaxMs.ToString("0.00") + "ms";
            }
        }

        // Session end. Host: the cargo objects stay in its car (the original sensor / SaveItems own them again). Player 2: its
        // copies of host cargo are removed (its sandbox world must not keep host items), except the object it carries.
        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            try
            {
                if (!_host)
                    foreach (var o in _objs.Values)
                        if (o.Go != null && (HeavySync.Instance == null || !HeavySync.Instance.HoldsLocally(o.Pick))) UnityEngine.Object.Destroy(o.Go);
                _objs.Clear(); _byInstance.Clear();
            }
            catch (Exception ex) { _log.Error("CARGO cleanup at session end failed", ex); }
            if (_host && _world.Host != null && _world.Host.HeavyItemId == (Func<int, uint, int>)HostItemId) _world.Host.HeavyItemId = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
