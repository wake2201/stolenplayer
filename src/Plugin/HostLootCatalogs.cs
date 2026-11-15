// W4.2D Stage A: the host's trusted loot catalogs for EVERY census namespace, built once per process on a background thread
// from the host's own installed scene files (src/Shared/SceneCatalog.cs; read-only file access, no scene is loaded, no Unity
// API is touched off the main thread). Started when the host session is attached in the main menu, so it normally completes
// before Player 2 can join (joins wait for the loaded host world). Until a namespace is ready, Player-2 takes there are
// answered Busy (retry, nothing granted). A scene whose file cannot be read exactly is unavailable: its pickups are refused.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal static class HostLootCatalogs
    {
        static readonly object _lock = new object();
        static readonly Dictionary<int, SceneCatalogResult> _byNs = new Dictionary<int, SceneCatalogResult>();
        static Thread _thread; static volatile bool _done; static string _fatal; static double _ms;
        static InstanceLog _log;

        public static bool Done { get { return _done; } }
        public static string Fatal { get { return _fatal; } }

        public static void Start(InstanceLog log)
        {
            _log = log;
            if (_thread != null) return;   // once per process
            var pick = AccessTools.TypeByName("Pickupable");
            if (pick == null) { _fatal = "Pickupable type not found"; _done = true; log.Warn("LOOT_CATALOG unavailable: " + _fatal); return; }
            Assembly asm = pick.Assembly; string dataDir = Application.dataPath;
            _thread = new Thread(() => Run(asm, dataDir)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "TS2MP loot catalogs" };
            _thread.Start();
            log.Info("LOOT_CATALOG building the host's trusted loot catalogs from its own scene files (background, once per process): " + dataDir);
        }

        static void Run(Assembly asm, string dataDir)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                SceneCatalogBuilder.BuildAll(dataDir, n => asm.GetType(n, false), r =>
                {
                    lock (_lock) _byNs[r.Namespace] = r;
                    var l = _log;
                    if (l == null) return;
                    if (r.Error != null) l.Warn("LOOT_CATALOG " + r.Scene + " (" + IdentityNamespace.Name(r.Namespace) + ") UNAVAILABLE: " + r.Error + " - Player-2 pickups there are refused");
                    else l.Info("LOOT_CATALOG " + r.Scene + " (" + IdentityNamespace.Name(r.Namespace) + ") digest=" + r.Digest.ToString("X8") + " objects=" + r.Table.Count + " loot=" + r.Loot.Count + " parsedScripts=" + r.Parsed + " transforms=" + r.Transforms);
                });
            }
            catch (Exception ex) { _fatal = ex.GetType().Name + ": " + ex.Message; }
            _ms = sw.Elapsed.TotalMilliseconds;
            _done = true;
            var lg = _log;
            if (lg != null) { if (_fatal != null) lg.Error("LOOT_CATALOG build failed (" + _fatal + "): Player-2 world pickups are refused (no duplicate possible)"); else lg.Info("LOOT_CATALOG ready: " + _byNs.Count + " scene(s) in " + _ms.ToString("0") + " ms"); }
        }

        // null = not built (yet) or unusable.
        public static LootCatalog Get(int ns)
        {
            lock (_lock) { SceneCatalogResult r; return _byNs.TryGetValue(ns, out r) && r.Error == null ? r.Loot : null; }
        }
        public static bool Pending(int ns) { if (_done) return false; lock (_lock) return !_byNs.ContainsKey(ns); }
        public static string Summary { get { lock (_lock) return "catalogs " + _byNs.Count + (_done ? " done in " + _ms.ToString("0") + " ms" : " building") + (_fatal != null ? " FAILED " + _fatal : ""); } }
    }
}
