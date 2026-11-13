// M5 W1: world object identity census (design + rules: src/Shared/WorldIdentity.cs). Read-only observer of the original scene:
// it never enables, moves, renames or destroys anything. Runs once per loaded scene instance inside
// SceneManager.sceneLoaded (after Awake/OnEnable, before any Start of that scene, so the hierarchy is the pristine serialized
// asset on every instance). Work: one GetComponentsInChildren(type, includeInactive) per census kind and scene root, one
// name read per visited transform (memoized), no per-frame code, no network traffic. The registry keeps 32-bit ids and
// component references per namespace; tables are dropped on scene unload / single scene load; references to destroyed
// objects are released on lookup. Optional full export (World.CensusExport) is written on a worker thread.
// W4.1: for every Pickupable the census also reads its serialized loot flags (before any Start) into a LootCatalog per
// namespace (src/Shared/WorldLoot.cs); RandomFileCabinet contents are marked (random per instance). Catalog metadata is kept
// after the scene unloads (the host validates a Player-2 pickup against it); live component references are released.
// W4.2B: object maps for the shared kinds Door / Lockpick / LockedWindow (component -> id, per-namespace enumeration), the
// window -> sash link (LockedWindow and its Door on the same GameObject = ONE logical window id), story/mission metadata
// read from the serialized fields before any Start (Lockpick.on_unlock_advance_story / story_name / is_tutorial,
// LockedWindow.complete_mission / isTutorial / Mission_Item), and an object digest (sorted ids of the three kinds) used as
// scene metadata. LATE census: scenes that were already loaded when the census was created (the host pressed F8 inside a
// region) are censused once for the three object kinds only (no loot catalog: their loot state is no longer pristine).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TS2MP.Plugin
{
    internal sealed class WorldCensus : IDisposable
    {
        public static WorldCensus Instance;
        readonly InstanceLog _log; readonly bool _export; readonly string _logDir;
        readonly IdentityRegistry _registry = new IdentityRegistry();
        readonly CensusGate _gate = new CensusGate();
        readonly Type[] _types = new Type[ObjectKinds.All.Length];
        Func<object, bool> _wasDropped;
        // W4.1 loot flags (serialized Pickupable fields)
        Func<object, int> _pItem; Func<object, string> _pStory;
        readonly List<KeyValuePair<LootFlags, Func<object, bool>>> _pFlags = new List<KeyValuePair<LootFlags, Func<object, bool>>>();
        Func<object, bool> _pAnyXp; Type _cabinetT;
        readonly Dictionary<int, LootCatalog> _loot = new Dictionary<int, LootCatalog>();
        readonly HashSet<Transform> _cabinet = new HashSet<Transform>();
        readonly Func<object, bool> _alive = o => (o as UnityEngine.Object) != null;

        // census-scoped scratch (cleared after each run)
        readonly List<GameObject> _roots = new List<GameObject>();
        readonly Dictionary<Transform, string> _paths = new Dictionary<Transform, string>();
        readonly Dictionary<Transform, SiblingNames> _siblings = new Dictionary<Transform, SiblingNames>();
        readonly Dictionary<long, int> _occurrence = new Dictionary<long, int>();
        SiblingNames _rootNames;
        long _runs; double _totalMs, _maxMs;

        public IdentityRegistry Registry { get { return _registry; } }

        public WorldCensus(InstanceLog log, string logDir, bool export)
        {
            _log = log; _logDir = logDir; _export = export;
            int found = 0;
            for (int i = 0; i < ObjectKinds.All.Length; i++)
            {
                _types[i] = AccessTools.TypeByName(ObjectKinds.All[i].TypeName);
                if (_types[i] != null) found++; else _log.Warn("CENSUS type not found: " + ObjectKinds.All[i].TypeName + " (kind skipped)");
            }
            var pick = AccessTools.TypeByName("Pickupable");
            _wasDropped = pick != null ? Fast.Getter<bool>(pick, "wasDropped") : null;
            if (pick != null) InitLootFlags(pick);
            InitObjectMeta();
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Instance = this;
            _log.Info("CENSUS ready: " + found + "/" + ObjectKinds.All.Length + " kinds, once per scene load (no per-frame work)" + (_export ? ", export ON" : ""));
        }

        void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            try
            {
                if (mode == LoadSceneMode.Single) { _registry.Clear(); foreach (var c in _loot.Values) c.ReleaseTargets(); _objIds.Clear(); _objByNs.Clear(); _windowDoor.Clear(); }   // every other scene was unloaded
                int ns = IdentityNamespace.ForScene(s.name);
                if (!_gate.TryBegin(s.handle, ns)) return;
                Run(s, ns, false);
            }
            catch (Exception ex) { _log.Error("CENSUS failed for scene " + s.name + " (no gameplay effect)", ex); }
            finally { _roots.Clear(); _paths.Clear(); _siblings.Clear(); _occurrence.Clear(); _rootNames = null; _cabinet.Clear(); }
        }

        void OnSceneUnloaded(Scene s)
        {
            _gate.Unloaded(s.handle);
            int ns = IdentityNamespace.ForScene(s.name);
            if (ns != IdentityNamespace.None) { _registry.Remove(ns); LootCatalog c; if (_loot.TryGetValue(ns, out c)) c.ReleaseTargets(); ForgetObjects(ns); }
        }

        // ---- W4.2/W4.2B shared objects: component -> id (live scene only) + retained catalogs per namespace ----
        public struct ObjectRef { public WorldObjectId Id; public ObjectKind Kind; public Component Comp; }
        readonly Dictionary<int, ObjectRef> _objIds = new Dictionary<int, ObjectRef>();             // component instance id -> ref
        readonly Dictionary<int, List<ObjectRef>> _objByNs = new Dictionary<int, List<ObjectRef>>();
        readonly Dictionary<int, WorldObjectId> _windowDoor = new Dictionary<int, WorldObjectId>(); // sash Door instance id -> window id
        readonly Dictionary<long, HashSet<uint>> _catalog = new Dictionary<long, HashSet<uint>>();  // (kind, ns) -> unambiguous ids
        readonly Dictionary<int, HashSet<uint>> _restricted = new Dictionary<int, HashSet<uint>>(); // ns -> story locks / mission windows
        readonly Dictionary<int, uint> _objDigest = new Dictionary<int, uint>();
        readonly Dictionary<int, int> _objCount = new Dictionary<int, int>();
        readonly HashSet<int> _late = new HashSet<int>();
        Func<object, bool> _lpStory, _lpTut, _lwMission, _lwTut; Func<object, string> _lpStoryName; Type _missionItemT;
        public event Action<int, bool> ObjectsCensused;   // (namespace, late)

        static bool Shared(ObjectKind k) { return k == ObjectKind.Door || k == ObjectKind.Lockpick || k == ObjectKind.LockedWindow; }
        static long CK(ObjectKind k, int ns) { return ((long)(byte)k << 32) | (uint)ns; }

        void ForgetObjects(int ns)
        {
            List<ObjectRef> l;
            if (!_objByNs.TryGetValue(ns, out l)) return;
            foreach (var r in l) { if ((object)r.Comp == null) continue; int iid = r.Comp.GetInstanceID(); _objIds.Remove(iid); _windowDoor.Remove(iid); }
            _objByNs.Remove(ns);
        }

        // A shared object of a loaded scene -> its census id; false for ambiguous / unknown objects (never shared).
        public bool TryObjectId(Component c, ObjectKind kind, out WorldObjectId id)
        {
            id = default(WorldObjectId); ObjectRef r;
            if ((object)c == null || !_objIds.TryGetValue(c.GetInstanceID(), out r) || r.Kind != kind) return false;
            id = r.Id; return true;
        }
        public bool TryDoorId(Component door, out WorldObjectId id) { return TryObjectId(door, ObjectKind.Door, out id); }
        // The sash Door of a LockedWindow (same GameObject) -> the window's logical id.
        public bool TryWindowOfDoor(Component door, out WorldObjectId id) { id = default(WorldObjectId); return (object)door != null && _windowDoor.TryGetValue(door.GetInstanceID(), out id); }
        public List<ObjectRef> Objects(int ns) { List<ObjectRef> l; return _objByNs.TryGetValue(ns, out l) ? l : null; }
        public bool IsLate(int ns) { return _late.Contains(ns); }
        public bool TryDigest(int ns, out uint digest, out int count) { count = 0; _objCount.TryGetValue(ns, out count); return _objDigest.TryGetValue(ns, out digest); }

        // Host validation: null = this process never censused the namespace (unverified), else known/unknown.
        public bool? ObjectKnown(ObjectKind k, int ns, uint hash) { HashSet<uint> s; return _catalog.TryGetValue(CK(k, ns), out s) ? s.Contains(hash) : (bool?)null; }
        public bool? DoorKnown(int ns, uint hash) { return ObjectKnown(ObjectKind.Door, ns, hash); }
        public bool ObjectRestricted(ObjectKind k, int ns, uint hash) { HashSet<uint> s; return k != ObjectKind.Door && _restricted.TryGetValue(ns, out s) && s.Contains(hash); }

        void InitObjectMeta()
        {
            var lp = AccessTools.TypeByName("Lockpick"); var lw = AccessTools.TypeByName("LockedWindow");
            if (lp != null) { _lpStory = Fast.Getter<bool>(lp, "on_unlock_advance_story"); _lpStoryName = Fast.Getter<string>(lp, "story_name"); _lpTut = Fast.Getter<bool>(lp, "is_tutorial"); }
            if (lw != null) { _lwMission = Fast.Getter<bool>(lw, "complete_mission"); _lwTut = Fast.Getter<bool>(lw, "isTutorial"); }
            _missionItemT = AccessTools.TypeByName("Mission_Item");
        }

        // Fail closed: when the metadata cannot be read the object counts as story-critical.
        bool StoryCritical(ObjectKind k, Component c)
        {
            if (k == ObjectKind.Lockpick)
                return _lpStory == null || _lpStoryName == null || _lpTut == null || _lpStory(c) || !string.IsNullOrEmpty(_lpStoryName(c)) || _lpTut(c);
            if (k == ObjectKind.LockedWindow)
                return _lwMission == null || _lwTut == null || _lwMission(c) || _lwTut(c) || (_missionItemT != null && c.GetComponent(_missionItemT) != null);
            return false;
        }

        // Scenes already loaded when the census was created (F8 host inside a region): object kinds only, once.
        public void CensusLoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                int ns = IdentityNamespace.ForScene(s.name);
                if (ns == IdentityNamespace.None || ns == IdentityNamespace.MainScene || !_gate.TryBegin(s.handle, ns)) continue;
                try { Run(s, ns, true); }
                catch (Exception ex) { _log.Error("CENSUS late run failed for scene " + s.name + " (no gameplay effect)", ex); }
                finally { _roots.Clear(); _paths.Clear(); _siblings.Clear(); _occurrence.Clear(); _rootNames = null; _cabinet.Clear(); }
            }
        }

        void InitLootFlags(Type pick)
        {
            _pItem = Fast.Getter<int>(pick, "item_ID"); _pStory = Fast.Getter<string>(pick, "story_name"); _pAnyXp = Fast.Getter<bool>(pick, "add_any_XP");
            Flag(pick, "is_heavy_item", LootFlags.Heavy); Flag(pick, "is_money", LootFlags.Money); Flag(pick, "destroy_if_was_picked", LootFlags.DestroyIfPicked);
            Flag(pick, "deleteIfPref", LootFlags.DeleteIfPref); Flag(pick, "lock_parcel_after_pickup", LootFlags.Parcel); Flag(pick, "advance_story_if_picked", LootFlags.StoryPick);
            Flag(pick, "advance_story_if_placed_car", LootFlags.StoryCar); Flag(pick, "isContractItem", LootFlags.Contract); Flag(pick, "never_destroy", LootFlags.NeverDestroy);
            Flag(pick, "dontDestroyDays", LootFlags.NoDays); Flag(pick, "cant_pick_reason", LootFlags.CantPick);
            _cabinetT = AccessTools.TypeByName("RandomFileCabinet");
            if (_pItem == null || _pStory == null || _pAnyXp == null || _pFlags.Count != 11) { _log.Warn("CENSUS loot flag fields incomplete: shared world loot disabled (no catalog)"); _pItem = null; }
        }
        void Flag(Type t, string field, LootFlags f) { var g = Fast.Getter<bool>(t, field); if (g != null) _pFlags.Add(new KeyValuePair<LootFlags, Func<object, bool>>(f, g)); }

        // W4.1: retained loot catalog of a namespace (null = never censused in this process or flags unavailable).
        public LootCatalog Loot(int ns) { LootCatalog c; return _loot.TryGetValue(ns, out c) ? c : null; }

        LootFlags FlagsOf(Component comp, bool dropped)
        {
            var f = LootFlags.None;
            for (int i = 0; i < _pFlags.Count; i++) if (_pFlags[i].Value(comp)) f |= _pFlags[i].Key;
            if (!string.IsNullOrEmpty(_pStory(comp))) f |= LootFlags.StoryName;
            if (!_pAnyXp(comp)) f |= LootFlags.NoXp;
            if (dropped) f |= LootFlags.Dropped;
            for (var t = comp.transform; t != null; t = t.parent) if (_cabinet.Contains(t)) { f |= LootFlags.RandomCabinet; break; }
            return f;
        }

        // RandomFileCabinet.Start activates random child objects per instance: its own transform and every GameObject it
        // references (firstCabinetRandom, ...) are marked; loot below them is not the same on both instances.
        void MarkCabinets()
        {
            _cabinet.Clear();
            if (_cabinetT == null) return;
            var fields = _cabinetT.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            for (int r = 0; r < _roots.Count; r++)
                foreach (var c in _roots[r].GetComponentsInChildren(_cabinetT, true))
                {
                    _cabinet.Add(c.transform);
                    foreach (var f in fields)
                    {
                        object v = f.GetValue(c);
                        var go = v as GameObject; if (go != null) { _cabinet.Add(go.transform); continue; }
                        var list = v as System.Collections.IEnumerable;
                        if (list != null && !(v is string)) foreach (var o in list) { var g = o as GameObject; if (g != null) _cabinet.Add(g.transform); }
                    }
                }
        }

        void Run(Scene scene, int ns, bool late)
        {
            long t0 = Stopwatch.GetTimestamp();
            int gc0 = GC.CollectionCount(0); long mem0 = GC.GetTotalMemory(false);
            var table = _registry.Begin(ns);
            ForgetObjects(ns);
            var objs = new List<ObjectRef>(); var windowsByTransform = new Dictionary<Transform, WorldObjectId>(); var restricted = new HashSet<uint>();
            scene.GetRootGameObjects(_roots);
            _rootNames = new SiblingNames();
            for (int i = 0; i < _roots.Count; i++) _rootNames.Add(_roots[i].name);
            var loot = new NameCensus();
            LootCatalog catalog = _pItem != null && !late ? new LootCatalog(ns) : null;
            if (catalog != null) MarkCabinets();
            List<string> lines = _export ? new List<string>(4096) : null;
            int visitedComponents = 0;

            for (int k = 0; k < _types.Length; k++)
            {
                var type = _types[k]; if (type == null) continue;
                var info = ObjectKinds.All[k];
                if (late && !Shared(info.Kind)) continue;
                for (int r = 0; r < _roots.Count; r++)
                {
                    var comps = _roots[r].GetComponentsInChildren(type, true);
                    for (int c = 0; c < comps.Length; c++)
                    {
                        var comp = comps[c]; if (comp == null) continue;
                        visitedComponents++;
                        var t = comp.transform;
                        string path = PathOf(t);
                        long occKey = ((long)t.GetInstanceID() << 8) | (byte)info.Kind;
                        int occ; _occurrence.TryGetValue(occKey, out occ); _occurrence[occKey] = occ + 1;
                        path = IdentityPath.WithOccurrence(path, occ);
                        var cat = info.Category;
                        if (info.Kind == ObjectKind.Pickupable)
                        {
                            string name = t.name;
                            bool dropped = _wasDropped != null && _wasDropped(comp);
                            cat = ObjectKinds.PickupableCategory(name, dropped);
                            if (cat == IdentityCategory.Persistent) loot.Add(name);
                        }
                        uint hash = IdentityHash.ForObject(scene.name, path, info.Kind);
                        var res = table.Add(hash, path, info.Kind, cat, comp);
                        if (Shared(info.Kind) && res == AddResult.Added)
                        {
                            var oref = new ObjectRef { Id = new WorldObjectId(ns, hash), Kind = info.Kind, Comp = comp };
                            objs.Add(oref);
                            if (info.Kind == ObjectKind.LockedWindow) windowsByTransform[t] = oref.Id;
                            if (StoryCritical(info.Kind, comp)) restricted.Add(hash);
                        }
                        if (info.Kind == ObjectKind.Pickupable && catalog != null)
                        {
                            string lname = t.name;
                            catalog.Add(hash, lname, _pItem(comp), FlagsOf(comp, cat == IdentityCategory.Dynamic), comp, comp.GetInstanceID());
                        }
                        if (lines != null) lines.Add(hash.ToString("X8") + "\t" + info.Kind + "\t" + cat + "\t" + res + "\t" + path);
                    }
                }
            }
            var stable = late ? null : _registry.Finish(table);
            if (late) table.Seal();
            FinishObjects(ns, table, objs, windowsByTransform, restricted, late);
            if (catalog != null)
            {
                IdentityCategory ic;
                catalog.Seal(h => !table.TryCategory(h, out ic) || ic == IdentityCategory.Unsupported);
                catalog.Digest = table.Digest;
                _loot[ns] = catalog;
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            long alloc = GC.GetTotalMemory(false) - mem0; int gcs = GC.CollectionCount(0) - gc0;
            _runs++; _totalMs += ms; if (ms > _maxMs) _maxMs = ms;

            var sb = new StringBuilder();
            sb.Append(late ? "WORLD_CENSUS(late: objects only) " : "WORLD_CENSUS ").Append(IdentityNamespace.Name(ns)).Append(" scene='").Append(scene.name).Append("' ").Append(table.Summary())
              .Append(" components=").Append(visitedComponents).Append(" transforms=").Append(_paths.Count)
              .Append(" loot=").Append(table.CountOf(ObjectKind.Pickupable)).Append(" lootSharedNames=").Append(loot.SharedNames).Append("/").Append(loot.Names)
              .Append(" (").Append(loot.ObjectsWithSharedNames).Append(" objects share an ItemDay_ key)")
              .Append(" stable=").Append(stable == null ? "first" : stable.Value ? "yes" : "NO")
              .Append(" time=").Append(ms.ToString("0.0")).Append("ms heapDelta~").Append(alloc / 1024).Append("KB gc0+").Append(gcs);
            _log.Info(sb.ToString());
            var perKind = new StringBuilder("WORLD_CENSUS_KINDS ").Append(IdentityNamespace.Name(ns));
            foreach (var info in ObjectKinds.All) { int n = table.CountOf(info.Kind); if (n > 0) perKind.Append(' ').Append(info.Kind).Append('=').Append(n); }
            _log.Info(perKind.ToString());
            foreach (var d in table.Diagnostics) _log.Info("WORLD_CENSUS_DIAG " + IdentityNamespace.Name(ns) + " " + d);
            if (loot.SharedNames > 0) _log.Info("WORLD_CENSUS_LOOT_NAMES " + IdentityNamespace.Name(ns) + " examples: " + string.Join(", ", loot.Examples(8).ToArray()));
            if (catalog != null && catalog.Count > 0) LogLoot(catalog);
            if (stable == false) _log.Warn("WORLD_CENSUS " + IdentityNamespace.Name(ns) + " digest differs from the previous load of this namespace in this process");
            if (lines != null) Export(ns, scene.name, lines);
            uint od; int oc; TryDigest(ns, out od, out oc);
            _log.Info("WORLD_CENSUS_OBJECTS " + IdentityNamespace.Name(ns) + " shared=" + oc + " digest=" + od.ToString("X8") + " windowSashes=" + _windowDoorCount + " storyOrMission=" + restricted.Count + (late ? " (late census)" : ""));
            var handler = ObjectsCensused; if (handler != null) handler(ns, late);
        }

        int _windowDoorCount;
        void FinishObjects(int ns, IdentityTable table, List<ObjectRef> objs, Dictionary<Transform, WorldObjectId> windows, HashSet<uint> restricted, bool late)
        {
            var keep = new List<ObjectRef>(objs.Count); IdentityCategory cat;
            var cats = new Dictionary<long, HashSet<uint>>();
            var sorted = new List<ulong>(objs.Count);
            _windowDoorCount = 0;
            foreach (var r in objs)
            {
                if (!table.TryCategory(r.Id.Hash, out cat) || cat == IdentityCategory.Unsupported) continue;   // ambiguous after sealing
                keep.Add(r);
                _objIds[r.Comp.GetInstanceID()] = r;
                HashSet<uint> s; long key = CK(r.Kind, ns);
                if (!cats.TryGetValue(key, out s)) cats[key] = s = new HashSet<uint>();
                s.Add(r.Id.Hash);
                sorted.Add(((ulong)(byte)r.Kind << 32) | r.Id.Hash);
                WorldObjectId wid;
                if (r.Kind == ObjectKind.Door && windows.TryGetValue(r.Comp.transform, out wid)) { _windowDoor[r.Comp.GetInstanceID()] = wid; _windowDoorCount++; }
            }
            _objByNs[ns] = keep;
            foreach (var kv in cats) _catalog[kv.Key] = kv.Value;
            foreach (var k in new[] { ObjectKind.Door, ObjectKind.Lockpick, ObjectKind.LockedWindow }) if (!cats.ContainsKey(CK(k, ns))) _catalog[CK(k, ns)] = new HashSet<uint>();
            _restricted[ns] = restricted;
            sorted.Sort();
            uint d = IdentityHash.Fnv1a32("objects");
            foreach (var v in sorted) for (int b = 0; b < 5; b++) d = IdentityHash.Append(d, (byte)(v >> (8 * b)));
            _objDigest[ns] = d; _objCount[ns] = sorted.Count;
            if (late) _late.Add(ns); else _late.Remove(ns);
        }

        // W4.1: one line per census with the flag distribution (which loot is in the supported subset and why the rest is not).
        void LogLoot(LootCatalog c)
        {
            var counts = new Dictionary<LootFlags, int>();
            foreach (var e in c.Entries)
                for (int b = 0; b < 32; b++) { var x = (LootFlags)(1u << b); if ((e.Flags & x) != 0) { int n; counts.TryGetValue(x, out n); counts[x] = n + 1; } }
            var sb = new StringBuilder("WORLD_LOOT_FLAGS ").Append(IdentityNamespace.Name(c.Namespace)).Append(" objects=").Append(c.Count)
                .Append(" flagFree=").Append(c.Eligible).Append(" supportedRegion=").Append(LootRules.SupportedNamespace(c.Namespace) ? "yes" : "no");
            foreach (var kv in counts) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            _log.Info(sb.ToString());
        }

        // Scene-relative hierarchy path; a sibling index only where siblings share the name. Memoized per transform.
        string PathOf(Transform t)
        {
            string p;
            if (_paths.TryGetValue(t, out p)) return p;
            var parent = t.parent;
            string name = t.name;
            if (parent == null) p = IdentityPath.Segment(name, t.GetSiblingIndex(), _rootNames.Shared(name));
            else
            {
                SiblingNames sib;
                if (!_siblings.TryGetValue(parent, out sib))
                {
                    sib = new SiblingNames();
                    for (int i = 0; i < parent.childCount; i++) sib.Add(parent.GetChild(i).name);
                    _siblings[parent] = sib;
                }
                p = IdentityPath.Join(PathOf(parent), IdentityPath.Segment(name, t.GetSiblingIndex(), sib.Shared(name)));
            }
            _paths[t] = p;
            return p;
        }

        void Export(int ns, string scene, List<string> lines)
        {
            string file = Path.Combine(_logDir, "census-" + IdentityNamespace.Name(ns) + "-" + _runs + ".tsv");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { File.WriteAllLines(file, lines.ToArray()); }
                catch (Exception ex) { _log.Warn("CENSUS export failed: " + ex.Message); }
            });
            _log.Info("WORLD_CENSUS export " + lines.Count + " rows -> " + file);
        }

        // Future network events (W3+): resolve an id without any scene search.
        public bool TryResolve(WorldObjectId id, out Component c)
        {
            object o; c = null;
            if (!_registry.TryResolve(id, _alive, out o)) return false;
            c = o as Component;
            return c != null;
        }

        public string Stats
        {
            get
            {
                return "CENSUS_STATS runs=" + _runs + " refused=" + _gate.Refused + " avg=" + (_runs > 0 ? (_totalMs / _runs).ToString("0.0") : "0") + "ms max=" + _maxMs.ToString("0.0")
                    + "ms namespaces=" + _registry.Count + " mem~" + (_registry.EstimatedBytes / 1024) + "KB";
            }
        }

        public void Dispose()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            _registry.Clear();
            if (Instance == this) Instance = null;
        }
    }
}
