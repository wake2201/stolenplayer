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
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Instance = this;
            _log.Info("CENSUS ready: " + found + "/" + ObjectKinds.All.Length + " kinds, once per scene load (no per-frame work)" + (_export ? ", export ON" : ""));
        }

        void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            try
            {
                if (mode == LoadSceneMode.Single) { _registry.Clear(); foreach (var c in _loot.Values) c.ReleaseTargets(); _doorIds.Clear(); _doorIdsByNs.Clear(); }   // every other scene was unloaded
                int ns = IdentityNamespace.ForScene(s.name);
                if (!_gate.TryBegin(s.handle, ns)) return;
                Run(s, ns);
            }
            catch (Exception ex) { _log.Error("CENSUS failed for scene " + s.name + " (no gameplay effect)", ex); }
            finally { _roots.Clear(); _paths.Clear(); _siblings.Clear(); _occurrence.Clear(); _rootNames = null; _cabinet.Clear(); }
        }

        void OnSceneUnloaded(Scene s)
        {
            _gate.Unloaded(s.handle);
            int ns = IdentityNamespace.ForScene(s.name);
            if (ns != IdentityNamespace.None) { _registry.Remove(ns); LootCatalog c; if (_loot.TryGetValue(ns, out c)) c.ReleaseTargets(); ForgetDoors(ns); }
        }

        // ---- W4.2 doors: component -> id (live scene only) + retained catalog of unambiguous door ids per namespace ----
        readonly Dictionary<int, WorldObjectId> _doorIds = new Dictionary<int, WorldObjectId>();
        readonly Dictionary<int, List<int>> _doorIdsByNs = new Dictionary<int, List<int>>();
        readonly Dictionary<int, HashSet<uint>> _doorCatalog = new Dictionary<int, HashSet<uint>>();

        void ForgetDoors(int ns)
        {
            List<int> l;
            if (!_doorIdsByNs.TryGetValue(ns, out l)) return;
            foreach (var iid in l) _doorIds.Remove(iid);
            _doorIdsByNs.Remove(ns);
        }

        // A Door of a loaded scene -> its census id; false for ambiguous / unknown doors (never shared).
        public bool TryDoorId(Component door, out WorldObjectId id)
        {
            id = default(WorldObjectId);
            if (door == null || !_doorIds.TryGetValue(door.GetInstanceID(), out id)) return false;
            var t = _registry.Get(id.Namespace); IdentityCategory cat;
            return t != null && t.TryCategory(id.Hash, out cat) && cat != IdentityCategory.Unsupported;
        }

        // Host validation of a door id: null = this process never censused the namespace (unverified), else known/unknown.
        public bool? DoorKnown(int ns, uint hash) { HashSet<uint> s; return _doorCatalog.TryGetValue(ns, out s) ? s.Contains(hash) : (bool?)null; }

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

        void Run(Scene scene, int ns)
        {
            long t0 = Stopwatch.GetTimestamp();
            int gc0 = GC.CollectionCount(0); long mem0 = GC.GetTotalMemory(false);
            var table = _registry.Begin(ns);
            ForgetDoors(ns);
            var doors = new List<int>(); var doorHashes = new List<uint>();
            scene.GetRootGameObjects(_roots);
            _rootNames = new SiblingNames();
            for (int i = 0; i < _roots.Count; i++) _rootNames.Add(_roots[i].name);
            var loot = new NameCensus();
            LootCatalog catalog = _pItem != null ? new LootCatalog(ns) : null;
            if (catalog != null) MarkCabinets();
            List<string> lines = _export ? new List<string>(4096) : null;
            int visitedComponents = 0;

            for (int k = 0; k < _types.Length; k++)
            {
                var type = _types[k]; if (type == null) continue;
                var info = ObjectKinds.All[k];
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
                        if (info.Kind == ObjectKind.Door && res == AddResult.Added) { int iid = comp.GetInstanceID(); _doorIds[iid] = new WorldObjectId(ns, hash); doors.Add(iid); doorHashes.Add(hash); }
                        if (info.Kind == ObjectKind.Pickupable && catalog != null)
                        {
                            string lname = t.name;
                            catalog.Add(hash, lname, _pItem(comp), FlagsOf(comp, cat == IdentityCategory.Dynamic), comp, comp.GetInstanceID());
                        }
                        if (lines != null) lines.Add(hash.ToString("X8") + "\t" + info.Kind + "\t" + cat + "\t" + res + "\t" + path);
                    }
                }
            }
            var stable = _registry.Finish(table);
            _doorIdsByNs[ns] = doors;
            var dcat = new HashSet<uint>(); IdentityCategory dc;
            foreach (var h in doorHashes) if (table.TryCategory(h, out dc) && dc != IdentityCategory.Unsupported) dcat.Add(h);
            _doorCatalog[ns] = dcat;
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
            sb.Append("WORLD_CENSUS ").Append(IdentityNamespace.Name(ns)).Append(" scene='").Append(scene.name).Append("' ").Append(table.Summary())
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
