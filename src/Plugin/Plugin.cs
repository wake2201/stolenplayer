// BepInEx 5 plugin entry. Inert unless the game was started by TS2-Multiplayer-Launcher (-ts2mp-role argument).
using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    [BepInPlugin(Guid, "TS2 Multiplayer", ProtocolInfo.ModVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.ts2mp.multiplayer";
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            LaunchArgs args;
            string parseError;
            if (!LaunchArgs.TryParse(Environment.GetCommandLineArgs(), out args, out parseError))
            {
                Log.LogInfo("TS2MP inactive: game not started by the multiplayer launcher (" + parseError + "). No patches applied.");
                return;
            }

            var log = new InstanceLog(Path.Combine(args.LogDir, "plugin.log"), args.Role.ToString().ToUpperInvariant(), args.InstanceId, args.SessionId);
            log.Mirror = delegate(string level, string msg)
            {
                if (level == "ERROR") Log.LogError(msg); else if (level == "WARN") Log.LogWarning(msg); else Log.LogInfo(msg);
            };
            log.Info("PLUGIN_INIT mod=" + ProtocolInfo.ModVersion + " protocol=" + ProtocolInfo.ProtocolVersion + " game=" + Application.version
                + " unity=" + Application.unityVersion + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                + " bepinex=" + typeof(BaseUnityPlugin).Assembly.GetName().Version + " port=" + args.Port + " logDir=" + args.LogDir);

            bool saveSafe = true;
            if (args.Role == Role.Client)
            {
                var harmony = new Harmony(Guid + ".saveguard");
                saveSafe = SaveGuard.Install(harmony, args, log);
            }
            else
            {
                log.Info("SAVE_OWNER: host keeps normal game persistence (real save folder). No save patches applied.");
            }

            // Read-only per-machine tuning (BepInEx/config/com.ts2mp.multiplayer.cfg, created with defaults on first run).
            var sync = new SyncSettings
            {
                SendRateHz = Mathf.Clamp(Config.Bind("Sync", "SendRateHz", 20, "Movement snapshots per second (10-30).").Value, 10, 30),
                InterpolationDelayMs = Mathf.Clamp(Config.Bind("Sync", "InterpolationDelayMs", 100, "Remote rendering delay used for interpolation (50-300 ms).").Value, 50, 300),
                YawOffset = Config.Bind("Avatar", "YawOffsetDegrees", 0f, "Rotation correction if the remote model faces the wrong way.").Value,
                ShowNameTag = Config.Bind("Avatar", "ShowNameTag", true, "Show a label above the remote player (diagnostics; only with Debug.ShowOverlay = true).").Value
            };
            var world = new WorldSettings
            {
                ClockRefreshSeconds = Mathf.Clamp(Config.Bind("World", "ClockRefreshSeconds", 10f, "Max seconds between region clock re-anchors while the clock runs normally (5-60). Changes are sent immediately.").Value, 5f, 60f),
                IdentityCensus = Config.Bind("World", "IdentityCensus", true, "M5 W1: once per scene load, record stable ids of world objects (diagnostic log WORLD_CENSUS; no gameplay effect). Off only for performance comparison.").Value,
                CensusExport = Config.Bind("World", "CensusExport", false, "Diagnostics: write every census identity (id, kind, category, hierarchy path) to census-*.tsv in the instance log folder.").Value,
                KeyMirror = Config.Bind("World", "KeyMirror", true, "M5 W2: the host's world keys (loot respawn days, daily events, hot item, day count) are mirrored to the client. Must be the same on both instances; off only for performance comparison.").Value
            };

            var go = new GameObject("TS2MP_Runner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<NetRunner>();
            KeyCode travelKey;
            var keyName = Config.Bind("Vehicles", "TravelMenuKey", "F7", "Opens the original travel menu on foot in the hideout / Auto Shop when you have no car there (another player simulates it or it is parked elsewhere).").Value;
            try { travelKey = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true); } catch { travelKey = KeyCode.F7; }
            var seatAdjust = Config.Bind("Vehicles", "DriverSeatAdjust", "", "Manual correction of the remote driver's seat in car-local metres, format carNr:dx,dy,dz;... (0 = Ranger, 1 = Shadowhawk), each value within +-0.5. Empty = automatic anchor from the car's own seat transforms.").Value;
            var passengerAdjust = Config.Bind("Vehicles", "PassengerSeatAdjust", "", "Manual correction of YOUR first-person passenger seat (player root, car-local metres), format carNr:dx,dy,dz;... (0 = Ranger, 1 = Shadowhawk), each within +-0.5. Empty = derived from the car's original companion seat (associateSitPos).").Value;
            var vehicles = new VehicleSettings { TravelMenuKey = travelKey, DriverSeatAdjust = seatAdjust ?? "", PassengerSeatAdjust = passengerAdjust ?? "", InteractDebug = Config.Bind("Vehicles", "InteractDebug", false, "Diagnostics: on each E press near another player's car (max 1/s) log what the original interaction ray hits.").Value };
            // M4.3.2a permanent rule: no mod text in the gameplay UI by default. The status lines and the name tag above the other
            // player are diagnostics, shown only when this is switched on (BepInEx log has the same information).
            runner.PerfStats = Config.Bind("Debug", "PerfStats", false, "Diagnostics: log frame-time statistics (avg FPS, P50/P95/P99, 1% low), mod main-thread cost, GC and network rates as PERF lines in plugin.log.").Value;
            runner.PerfIntervalSeconds = Mathf.Clamp(Config.Bind("Debug", "PerfIntervalSeconds", 30, "Seconds per PERF report window (10-300).").Value, 10, 300);
            runner.ShowDebugOverlay = Config.Bind("Debug", "ShowOverlay", false, "Diagnostics only: draw the TS2MP status lines and the name tag above the other player (Avatar.ShowNameTag) on screen. Off = the game looks unmodified; everything is in the BepInEx log.").Value;
            runner.Init(args, log, saveSafe, sync, world, vehicles);
        }
    }

    public sealed class LaunchArgs
    {
        public Role Role;
        public string SessionId, InstanceId, LogDir, DataDir;
        public string BindAddress = "127.0.0.1", ConnectAddress = "127.0.0.1";
        public int Port;

        public static bool TryParse(string[] argv, out LaunchArgs a, out string error)
        {
            a = new LaunchArgs();
            error = null;
            string role = null, port = null;
            for (int i = 0; i < argv.Length - 1; i++)
            {
                string k = argv[i], v = argv[i + 1];
                if (k == "-ts2mp-role") role = v;
                else if (k == "-ts2mp-session") a.SessionId = v;
                else if (k == "-ts2mp-instance") a.InstanceId = v;
                else if (k == "-ts2mp-port") port = v;
                else if (k == "-ts2mp-logdir") a.LogDir = v;
                else if (k == "-ts2mp-data") a.DataDir = v;
                else if (k == "-ts2mp-bind") a.BindAddress = v;
                else if (k == "-ts2mp-connect") a.ConnectAddress = v;
            }
            if (role == null) { error = "no -ts2mp-role argument"; return false; }
            if (role == "host") a.Role = Role.Host;
            else if (role == "client") a.Role = Role.Client;
            else { error = "invalid role '" + role + "'"; return false; }
            if (string.IsNullOrEmpty(a.SessionId) || string.IsNullOrEmpty(a.InstanceId) || string.IsNullOrEmpty(a.LogDir) || string.IsNullOrEmpty(a.DataDir))
            { error = "missing -ts2mp-session/-instance/-logdir/-data"; return RoleOnlyFallback(a); }
            if (!int.TryParse(port, out a.Port) || a.Port < 1 || a.Port > 65535)
            { error = "invalid -ts2mp-port '" + port + "'"; return RoleOnlyFallback(a); }
            return true;
        }

        // A client with broken arguments must still get save protection, so keep it active with a fallback log dir.
        static bool RoleOnlyFallback(LaunchArgs a)
        {
            if (a.Role != Role.Client) return false;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TS2MP");
            if (string.IsNullOrEmpty(a.DataDir)) a.DataDir = root;
            if (string.IsNullOrEmpty(a.LogDir)) a.LogDir = Path.Combine(root, "logs", "orphan-client-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            if (string.IsNullOrEmpty(a.SessionId)) a.SessionId = "invalid";
            if (string.IsNullOrEmpty(a.InstanceId)) a.InstanceId = "c" + System.Diagnostics.Process.GetCurrentProcess().Id;
            a.Port = 0; // NetRunner will not start networking
            return true;
        }
    }

    public sealed class NetRunner : MonoBehaviour
    {
        LaunchArgs _args;
        InstanceLog _log;
        SessionNode _node;
        PlayerSync _sync;
        WorldSync _world;
        VehicleSync _vehicles;
        CarBoxSync _carbox;
        WorldItemSync _items;
        Player2Sync _p2;
        WorldCensus _census;
        WorldKeyMirror _mirror;
        bool _quitRequested, _shutdown;
        // M5 performance instrumentation (Debug.PerfStats; off = one null check per frame)
        public bool PerfStats; public int PerfIntervalSeconds = 30;
        FrameStats _frameStats; readonly CostMeter _modCost = new CostMeter(); double _nextPerf;
        int _gc0, _gc1, _gc2; long _netOut0, _netIn0; long _frameTicks;
        GUIStyle _style, _tagStyle;

        public void Init(LaunchArgs args, InstanceLog log, bool saveSafe, SyncSettings sync, WorldSettings world, VehicleSettings vehicles)
        {
            _args = args;
            _log = log;
            if (!saveSafe)
            {
                _log.Error("SAVE_GUARD_FAILED: client save protection could not be verified. Quitting this instance to protect the real saves.");
                _quitRequested = true;
                return;
            }
            if (args.Port == 0)
            {
                _log.Error("ARGS_INVALID: networking disabled (save protection is active). Start the game via TS2-Multiplayer-Launcher.exe.");
                return;
            }
            _node = new SessionNode(args.Role, args.SessionId, args.InstanceId, Application.version, args.Port, log);
            _node.BindAddress = args.BindAddress;
            _node.ConnectAddress = args.ConnectAddress;
            log.StateProvider = delegate { return _node.StateText; };
            if (_node.Start())
            {
                _sync = new PlayerSync(_node, log, sync);
                _world = new WorldSync(_node, log, world);
                try { _vehicles = new VehicleSync(_node, log, _world, _sync, args.DataDir, vehicles); }
                catch (Exception ex) { log.Error("VEHICLE init failed (vehicle sync disabled)", ex); _vehicles = null; }
                try { _p2 = new Player2Sync(_node, log, _world, args.DataDir); }
                catch (Exception ex) { log.Error("P2INV init failed (Player-2 persistence disabled)", ex); _p2 = null; }
                try { _carbox = new CarBoxSync(_node, log, _world); }
                catch (Exception ex) { log.Error("CARBOX init failed (shared car storage disabled)", ex); _carbox = null; }
                try { _items = new WorldItemSync(_node, log, _world); }
                catch (Exception ex) { log.Error("WITEM init failed (shared dropped items disabled)", ex); _items = null; }
                if (world.KeyMirror)
                {
                    try { _mirror = new WorldKeyMirror(_node, log, _world); if (!_mirror.Ready) { _mirror.Dispose(); _mirror = null; } }
                    catch (Exception ex) { log.Error("MIRROR init failed (world-key mirror disabled)", ex); _mirror = null; }
                }
                else log.Info("MIRROR disabled by config (World.KeyMirror = false)");
            }
            if (world.IdentityCensus)
            {
                try { _census = new WorldCensus(log, args.LogDir, world.CensusExport); }
                catch (Exception ex) { log.Error("CENSUS init failed (identity census disabled)", ex); _census = null; }
            }
            else log.Info("CENSUS disabled by config (World.IdentityCensus = false)");
            if (PerfStats)
            {
                _frameStats = new FrameStats(8192);
                _nextPerf = Time.realtimeSinceStartup + PerfIntervalSeconds;
                _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
                log.Info("PERF stats ON: window " + PerfIntervalSeconds + " s; 1% low = mean of the slowest 1% frame times; mod cost = TS2MP Update+LateUpdate+FixedUpdate main-thread time only (not game/GPU time)");
            }
        }

        float _nextSaveCheck;

        void Update()
        {
            if (_frameStats == null) { UpdateBody(); return; }
            _frameStats.Add(Time.unscaledDeltaTime);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateBody();
            _frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            _modCost.Add(_frameTicks); _frameTicks = 0;   // per frame: this Update + the previous frame's LateUpdate/FixedUpdate
            if (Time.realtimeSinceStartup >= _nextPerf) PerfReport();
        }

        void PerfReport()
        {
            float now = Time.realtimeSinceStartup;
            _nextPerf = now + PerfIntervalSeconds;
            var r = _frameStats.Compute(); _frameStats.Reset();
            long f = System.Diagnostics.Stopwatch.Frequency;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long outB = _node != null ? _node.BytesSent : 0, inB = _node != null ? _node.BytesReceived : 0;
            double secs = Math.Max(1, r.Seconds);
            _log.Info("PERF " + r + " | mod " + _modCost.Text(f) + " | gc0+" + (g0 - _gc0) + " gc1+" + (g1 - _gc1) + " gc2+" + (g2 - _gc2)
                + " heap=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MB | net out=" + ((outB - _netOut0) / secs).ToString("0") + "B/s in=" + ((inB - _netIn0) / secs).ToString("0")
                + "B/s | focus=" + Application.isFocused + " vsync=" + QualitySettings.vSyncCount + " targetFps=" + Application.targetFrameRate + " res=" + Screen.width + "x" + Screen.height
                + " scenes=" + UnityEngine.SceneManagement.SceneManager.sceneCount);
            if (_census != null) _log.Info("PERF " + _census.Stats);
            if (_mirror != null) _log.Info("PERF " + _mirror.Stats);
            _modCost.Reset(); _gc0 = g0; _gc1 = g1; _gc2 = g2; _netOut0 = outB; _netIn0 = inB;
        }

        void UpdateBody()
        {
            if (_quitRequested) { _quitRequested = false; Application.Quit(); return; }
            if (_args.Role == Role.Client && Time.unscaledTime >= _nextSaveCheck)
            {
                _nextSaveCheck = Time.unscaledTime + 2f;
                if (!SaveGuard.RuntimeCheck())
                {
                    _log.Error("SAVE_GUARD_FAILED at runtime: quitting client to protect the real saves.");
                    _quitRequested = true;
                    return;
                }
            }
            if (_node != null) _node.Tick();
            if (_sync != null)
            {
                try { _sync.Update(); }
                catch (Exception ex) { _log.Error("SYNC update exception (sync disabled)", ex); _sync.Dispose(); _sync = null; }
            }
            if (_world != null)
            {
                try { _world.Update(Time.unscaledDeltaTime); }
                catch (Exception ex) { _log.Error("WORLD update exception (world sync disabled)", ex); _world.Dispose(); _world = null; }
            }
            if (_vehicles != null)
            {
                if (_world == null) { _vehicles.Dispose(); _vehicles = null; }
                else
                {
                    try { _vehicles.Update(); }
                    catch (Exception ex) { _log.Error("VEHICLE update exception (vehicle sync disabled)", ex); _vehicles.Dispose(); _vehicles = null; }
                }
            }
            if (_p2 != null && _world != null)
            {
                try { _p2.Update(); }
                catch (Exception ex) { _log.Error("P2INV update exception (Player-2 persistence disabled)", ex); _p2.Dispose(); _p2 = null; }
            }
            if (_carbox != null && _world != null)
            {
                try { _carbox.Update(); }
                catch (Exception ex) { _log.Error("CARBOX update exception (shared car storage disabled)", ex); _carbox.Dispose(); _carbox = null; }
            }
            if (_items != null && _world != null)
            {
                try { _items.Update(); }
                catch (Exception ex) { _log.Error("WITEM update exception (shared dropped items disabled)", ex); _items.Dispose(); _items = null; }
            }
            if (_mirror != null && _world != null)
            {
                try { _mirror.Update(); }
                catch (Exception ex) { _log.Error("MIRROR update exception (world-key mirror disabled)", ex); _mirror.Dispose(); _mirror = null; }
            }
        }

        // M5.0: observed cars are kinematic bodies moved in the physics step.
        void FixedUpdate()
        {
            if (_vehicles == null) return;
            long t0 = _frameStats != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { _vehicles.FixedStep(); }
            catch (Exception ex) { _log.Error("VEHICLE fixed step exception (vehicle sync disabled)", ex); _vehicles.Dispose(); _vehicles = null; }
            if (_frameStats != null) _frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        // M4.2: client proposals are applied here, after this frame's UI events (host's own travel click wins a same-frame tie).
        void LateUpdate()
        {
            if (_world == null) return;
            long t0 = _frameStats != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            LateBody();
            if (_frameStats != null) _frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        void LateBody()
        {
            try { _world.LateUpdate(); }
            catch (Exception ex) { _log.Error("WORLD late update exception", ex); }
            if (_vehicles != null)
            {
                try { _vehicles.LateUpdate(); }
                catch (Exception ex) { _log.Error("VEHICLE late update exception (vehicle sync disabled)", ex); _vehicles.Dispose(); _vehicles = null; }
            }
            if (_sync != null)
            {
                try { _sync.LateUpdate(); }                         // W3.1: remote arm pose (after the legacy Animation posed the avatar)
                catch (Exception ex) { _log.Error("SYNC late update exception", ex); }
            }
        }

        public bool ShowDebugOverlay;

        void OnGUI()
        {
            if (_args == null || !ShowDebugOverlay) return;
            if (_style == null) { _style = new GUIStyle(GUI.skin.label); _style.fontSize = 12; }
            string s = "TS2MP " + _args.Role.ToString().ToUpperInvariant() + " " + _args.InstanceId + " | "
                + (_node != null ? _node.State.ToString() + (_node.RemoteInstanceId != null ? " <-> " + _node.RemoteInstanceId : "") : "network off")
                + (_sync != null ? " | " + _sync.StatusLine : "");
            GUI.Label(new Rect(6, 2, 1400, 20), s, _style);
            if (_world != null) GUI.Label(new Rect(6, 18, 1400, 20), "TS2MP world | " + _world.StatusLine, _style);
            if (_vehicles != null) GUI.Label(new Rect(6, 34, 1400, 20), "TS2MP vehicles | " + _vehicles.StatusLine, _style);

            Vector3 world; string label;
            var cam = Camera.main;
            if (_sync != null && cam != null && _sync.TryGetNameTag(out world, out label))
            {
                var sp = cam.WorldToScreenPoint(world);
                if (sp.z > 0)
                {
                    if (_tagStyle == null) { _tagStyle = new GUIStyle(GUI.skin.label); _tagStyle.alignment = TextAnchor.MiddleCenter; _tagStyle.fontSize = 13; }
                    GUI.Label(new Rect(sp.x - 100, Screen.height - sp.y - 10, 200, 20), label, _tagStyle);
                }
            }
        }

        void OnApplicationQuit() { Shutdown("application quit"); }
        void OnDestroy() { Shutdown("runner destroyed"); }

        void Shutdown(string why)
        {
            if (_shutdown) return;
            _shutdown = true;
            if (_log == null) return;
            _log.Info("PLUGIN_SHUTDOWN (" + why + ")");
            if (_mirror != null) { try { _mirror.Dispose(); } catch { } _mirror = null; }
            if (_census != null) { try { _census.Dispose(); } catch { } _census = null; }
            if (_items != null) { try { _items.Dispose(); } catch { } _items = null; }
            if (_carbox != null) { try { _carbox.Dispose(); } catch { } _carbox = null; }
            if (_p2 != null) { try { _p2.Dispose(); } catch { } _p2 = null; }
            if (_vehicles != null) { try { _vehicles.Dispose(); } catch { } _vehicles = null; }
            if (_world != null) { try { _world.Dispose(); } catch { } _world = null; }
            if (_sync != null) { try { _sync.Dispose(); } catch { } _sync = null; }
            if (_node != null) { _node.Dispose(); _node = null; }
            _log.Dispose();
        }
    }
}
