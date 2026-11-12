// M4.2: narrowly scoped Harmony patches for travel, arrival car activation and phone fast travel. Verified IL:
//   GameManager.Select<Map>()  (no args)  : set CurrentLocation, SaveGameNewSystem, reload MainScene (CODEBASE_MAP §3.9)
//   VehiclesManager.InitiateMainCar()     : from GameManager.Start, activates the car named by SPrefs "CurrentCar"
//   CarController.IsStartingCar()         : seats the player in GameManager.Car after travel to Madison/Hillville
//   PlayerScript.SpawnAtCar()             : phone "fast travel to car", charges money before checking the car
//   CarController.EnterVehicle()          : M4.2.1, called by EnterCar.EnterVehicle from PlayerScript.Update (tag SitCar + E)
//   PlayerScript.OpenTownSelectionNoDelay() / OpenTownSelection() : M4.3 Phase B, the travel menu openers (MapBorder.OnTriggerEnter
//                                           IL_003c calls OpenTownSelectionNoDelay for any collider tagged Player/PlayerMapTrigger,
//                                           i.e. also for a seated mod passenger's own player child "Trigger Map")
// Hooks fall back to the original behaviour if the handler throws, except menu travel clicks (held) and driver seating
// on arrival (skipped). SelectJail is never patched (arrest path).
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TS2MP.Plugin
{
    internal interface ITravelHandler
    {
        bool OnSelect(object gm, MethodBase method, int dest);   // false = hold the original travel
        void BeforeInitiate(object vm);
        void AfterInitiate(object vm);
        bool AllowStartingCar(object car);
        bool AllowSpawnAtCar(object ps);
        bool AllowEnterVehicle(object car);                      // false = hideout travel proxy (menu opened instead)
        bool AllowTownSelection(object ps);                      // M4.3 Phase B: false = seated passenger (only the driver travels)
        bool SelectFailSafe(object gm);                           // result when OnSelect threw
        void HookError(string where, Exception ex);
    }

    internal static class TravelHooks
    {
        // Verified destinations (GameManager.Select* IL; SelectOffice and SelectGunRange both write 98).
        public static readonly Dictionary<string, int> Destinations = new Dictionary<string, int>
        {
            { "SelectHideout", 0 }, { "SelectVerwood", 1 }, { "SelectParking", 2 }, { "SelectAutoShop", 3 },
            { "SelectDrugFarm", 90 }, { "SelectWarehouse", 91 }, { "SelectBank", 92 }, { "SelectForestside", 94 },
            { "SelectHauntedMansion", 95 }, { "SelectMuseum", 96 }, { "SelectHillville", 97 }, { "SelectOffice", 98 },
            { "SelectGunRange", 98 }, { "SelectTestMap", 99 },
        };

        public static ITravelHandler Handler;
        static Harmony _h;
        public static int Patched;
        public static bool TownSelectionPatched;
        // M4.3 Phase B: the exact patched Select* method per fleet destination (a joint passenger resumes its own original travel)
        public static readonly Dictionary<int, MethodInfo> SelectByDest = new Dictionary<int, MethodInfo>();

        public static string Install(string id)
        {
            var gm = AccessTools.TypeByName("GameManager");
            var vm = AccessTools.TypeByName("VehiclesManager");
            var car = AccessTools.TypeByName("CarController");
            var ps = AccessTools.TypeByName("PlayerScript");
            if (gm == null || vm == null || car == null || ps == null) return "game types not found";
            var init = AccessTools.Method(vm, "InitiateMainCar", Type.EmptyTypes);
            var start = AccessTools.Method(car, "IsStartingCar", Type.EmptyTypes);
            var spawn = AccessTools.Method(ps, "SpawnAtCar", Type.EmptyTypes);
            var enter = AccessTools.Method(car, "EnterVehicle", Type.EmptyTypes);
            if (init == null || start == null || spawn == null || enter == null) return "InitiateMainCar/IsStartingCar/SpawnAtCar/EnterVehicle not found";
            var selects = new List<MethodInfo>();
            foreach (var kv in Destinations)
            {
                var m = AccessTools.Method(gm, kv.Key, Type.EmptyTypes);
                if (m == null || m.ReturnType != typeof(void)) return "GameManager." + kv.Key + "() not found";
                selects.Add(m);
                if (!SelectByDest.ContainsKey(kv.Value)) SelectByDest[kv.Value] = m;
            }
            var townA = AccessTools.Method(ps, "OpenTownSelectionNoDelay", Type.EmptyTypes);
            var townB = AccessTools.Method(ps, "OpenTownSelection", Type.EmptyTypes);
            _h = new Harmony(id);
            var selectPrefix = new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "SelectPrefix"));
            foreach (var m in selects) { _h.Patch(m, selectPrefix); Patched++; }
            _h.Patch(init, new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "InitPrefix")), new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "InitPostfix")));
            _h.Patch(start, new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "StartingPrefix")));
            _h.Patch(spawn, new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "SpawnAtCarPrefix")));
            _h.Patch(enter, new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "EnterPrefix")));
            Patched += 4;
            // M5 hotfix: log-only labels for the next arrival (verified IL: GotCaught shows the caught screen whose Load button
            // calls GameManager.LoadCheckpoint -> LoadCheckpointIE -> Application.LoadLevel("MainScene"), no save).
            var caught = AccessTools.Method(gm, "GotCaught", new[] { typeof(int) });
            var loadCp = AccessTools.Method(gm, "LoadCheckpoint", Type.EmptyTypes);
            var loadMenu = AccessTools.Method(gm, "LoadMenu", Type.EmptyTypes);
            if (caught != null) { _h.Patch(caught, null, new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "CaughtPostfix"))); Patched++; }
            if (loadCp != null) { _h.Patch(loadCp, new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "LoadCheckpointPrefix"))); Patched++; }
            if (loadMenu != null) { _h.Patch(loadMenu, new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "LoadMenuPrefix"))); Patched++; }
            if (townA != null && townB != null)   // optional: without them a seated passenger may see the menu (its clicks stay refused)
            {
                var townPrefix = new HarmonyMethod(AccessTools.Method(typeof(TravelHooks), "TownPrefix"));
                _h.Patch(townA, townPrefix); _h.Patch(townB, townPrefix);
                Patched += 2; TownSelectionPatched = true;
            }
            return null;
        }

        public static string LastLoadReason;   // consumed by the next arrival diagnostic (VehicleSync.AllowStartingCar)
        public static Action<string> Log;

        static void CaughtPostfix(int __0) { try { LastLoadReason = "caught (GotCaught " + __0 + ")"; if (Log != null) Log("CAUGHT GameManager.GotCaught(" + __0 + "): original caught screen / jail path"); } catch { } }
        static void LoadCheckpointPrefix() { try { LastLoadReason = "caught screen Load (GameManager.LoadCheckpoint: MainScene reload, last checkpoint of this instance's save)"; if (Log != null) Log("LOAD GameManager.LoadCheckpoint: original reload of the last checkpoint"); } catch { } }
        static void LoadMenuPrefix() { try { LastLoadReason = "main menu + Continue"; } catch { } }

        public static void Uninstall() { if (_h != null) { _h.UnpatchSelf(); _h = null; } Handler = null; Log = null; LastLoadReason = null; Patched = 0; TownSelectionPatched = false; SelectByDest.Clear(); }

        static bool SelectPrefix(object __instance, MethodBase __originalMethod)
        {
            var h = Handler;
            if (h == null) return true;
            try
            {
                int dest;
                return !Destinations.TryGetValue(__originalMethod.Name, out dest) || h.OnSelect(__instance, __originalMethod, dest);
            }
            catch (Exception ex) { h.HookError("Select", ex); return h.SelectFailSafe(__instance); }   // M4.2.1: menu clicks fail closed
        }

        static void InitPrefix(object __instance)
        {
            var h = Handler; if (h == null) return;
            try { h.BeforeInitiate(__instance); } catch (Exception ex) { h.HookError("InitiateMainCar prefix", ex); }
        }

        static void InitPostfix(object __instance)
        {
            var h = Handler; if (h == null) return;
            try { h.AfterInitiate(__instance); } catch (Exception ex) { h.HookError("InitiateMainCar postfix", ex); }
        }

        static bool StartingPrefix(object __instance)
        {
            var h = Handler; if (h == null) return true;
            try { return h.AllowStartingCar(__instance); } catch (Exception ex) { h.HookError("IsStartingCar", ex); return false; }   // fail safe: arrive on foot
        }

        static bool EnterPrefix(object __instance)
        {
            var h = Handler; if (h == null) return true;
            try { return h.AllowEnterVehicle(__instance); } catch (Exception ex) { h.HookError("EnterVehicle", ex); return true; }
        }

        static bool TownPrefix(object __instance)
        {
            var h = Handler; if (h == null) return true;
            try { return h.AllowTownSelection(__instance); } catch (Exception ex) { h.HookError("OpenTownSelection", ex); return true; }   // fail open: original menu
        }

        static bool SpawnAtCarPrefix(object __instance)
        {
            var h = Handler; if (h == null) return true;
            try { return h.AllowSpawnAtCar(__instance); } catch (Exception ex) { h.HookError("SpawnAtCar", ex); return true; }
        }
    }
}
