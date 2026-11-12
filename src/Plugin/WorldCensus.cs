// M5 W1: world object identity census (design + rules: src/Shared/WorldIdentity.cs). Read-only observer of the original scene:
// it never enables, moves, renames or destroys anything. Runs once per loaded scene instance inside
// SceneManager.sceneLoaded (after Awake/OnEnable, before any Start of that scene, so the hierarchy is the pristine serialized
// asset on every instance). Work: one GetComponentsInChildren(type, includeInactive) per census kind and scene root, one
// name read per visited transform (memoized), no per-frame code, no network traffic. The registry keeps 32-bit ids and
// component references per namespace; tables are dropped on scene unload / single scene load; references to destroyed
// objects are released on lookup. Optional full export (World.CensusExport) is written on a worker thread.
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
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Instance = this;
            _log.Info("CENSUS ready: " + found + "/" + ObjectKinds.All.Length + " kinds, once per scene load (no per-frame work)" + (_export ? ", export ON" : ""));
        }

        void OnSceneLoaded(Scene s, LoadSceneMode mode)
        {
            try
            {
                if (mode == LoadSceneMode.Single) _registry.Clear();   // every other scene was unloaded
                int ns = IdentityNamespace.ForScene(s.name);
                if (!_gate.TryBegin(s.handle, ns)) return;
                Run(s, ns);
            }
            catch (Exception ex) { _log.Error("CENSUS failed for scene " + s.name + " (no gameplay effect)", ex); }
            finally { _roots.Clear(); _paths.Clear(); _siblings.Clear(); _occurrence.Clear(); _rootNames = null; }
        }

        void OnSceneUnloaded(Scene s)
        {
            _gate.Unloaded(s.handle);
            int ns = IdentityNamespace.ForScene(s.name);
            if (ns != IdentityNamespace.None) _registry.Remove(ns);
        }

        void Run(Scene scene, int ns)
        {
            long t0 = Stopwatch.GetTimestamp();
            int gc0 = GC.CollectionCount(0); long mem0 = GC.GetTotalMemory(false);
            var table = _registry.Begin(ns);
            scene.GetRootGameObjects(_roots);
            _rootNames = new SiblingNames();
            for (int i = 0; i < _roots.Count; i++) _rootNames.Add(_roots[i].name);
            var loot = new NameCensus();
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
                        if (lines != null) lines.Add(hash.ToString("X8") + "\t" + info.Kind + "\t" + cat + "\t" + res + "\t" + path);
                    }
                }
            }
            var stable = _registry.Finish(table);
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
            if (stable == false) _log.Warn("WORLD_CENSUS " + IdentityNamespace.Name(ns) + " digest differs from the previous load of this namespace in this process");
            if (lines != null) Export(ns, scene.name, lines);
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
