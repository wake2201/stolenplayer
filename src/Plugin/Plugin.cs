// BepInEx 5 plugin entry.
// N1.2: a normally started game (Steam) runs in F8 mode: no patch and no network until the player opens the F8 menu in the
// main menu and prepares a host (W4.2B.3: before the save loads) or joins; see MultiplayerMenu.cs. The developer launcher
// (-ts2mp-role argument) keeps the fixed-role mode used for local two-instance tests.
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
            // N1: in launcher mode the join code arrives in this process's environment only (never on the command line).
            // Take it and remove it immediately so no child process inherits it; only the derived HMAC key stays in memory.
            string joinCodeRaw = Environment.GetEnvironmentVariable(JoinAuth.EnvVar);
            if (joinCodeRaw != null) { try { Environment.SetEnvironmentVariable(JoinAuth.EnvVar, null); } catch { } }
            bool legacy = LaunchArgs.TryParse(Environment.GetCommandLineArgs(), out args, out parseError);

            // Read-only per-machine tuning (BepInEx/config/com.ts2mp.multiplayer.cfg, created with defaults on first run).
            var settings = new SessionSettings();
            settings.Sync = new SyncSettings
            {
                SendRateHz = Mathf.Clamp(Config.Bind("Sync", "SendRateHz", 20, "Movement snapshots per second (10-30).").Value, 10, 30),
                InterpolationDelayMs = Mathf.Clamp(Config.Bind("Sync", "InterpolationDelayMs", 100, "Remote rendering delay used for interpolation (50-300 ms).").Value, 50, 300),
                YawOffset = Config.Bind("Avatar", "YawOffsetDegrees", 0f, "Rotation correction if the remote model faces the wrong way.").Value,
                ShowNameTag = Config.Bind("Avatar", "ShowNameTag", true, "Show a label above the remote player (diagnostics; only with Debug.ShowOverlay = true).").Value
            };
            settings.World = new WorldSettings
            {
                ClockRefreshSeconds = Mathf.Clamp(Config.Bind("World", "ClockRefreshSeconds", 10f, "Max seconds between region clock re-anchors while the clock runs normally (5-60). Changes are sent immediately.").Value, 5f, 60f),
                IdentityCensus = Config.Bind("World", "IdentityCensus", true, "M5 W1: once per scene load, record stable ids of world objects (diagnostic log WORLD_CENSUS; no gameplay effect). Off only for performance comparison.").Value,
                CensusExport = Config.Bind("World", "CensusExport", false, "Diagnostics: write every census identity (id, kind, category, hierarchy path) to census-*.tsv in the instance log folder.").Value,
                KeyMirror = Config.Bind("World", "KeyMirror", true, "M5 W2: the host's world keys (loot respawn days, daily events, hot item, day count) are mirrored to the client. Must be the same on both instances; off only for performance comparison.").Value,
                SharedLoot = Config.Bind("World", "SharedLoot", true, "M5 W4.1: pre-placed world loot (Madison Street subset) is host-authoritative and shared. Must be the same on both instances; needs World.IdentityCensus.").Value,
                SharedDoors = Config.Bind("World", "SharedDoors", true, "W4.2B: ordinary doors, locks (lockpicking) and windows in Madison Street are host-authoritative and shared. Must be the same on both instances; needs World.IdentityCensus.").Value
            };
            KeyCode travelKey, menuKey;
            var keyName = Config.Bind("Vehicles", "TravelMenuKey", "F7", "Opens the original travel menu on foot in the hideout / Auto Shop when you have no car there (another player simulates it or it is parked elsewhere).").Value;
            try { travelKey = (KeyCode)Enum.Parse(typeof(KeyCode), keyName, true); } catch { travelKey = KeyCode.F7; }
            var seatAdjust = Config.Bind("Vehicles", "DriverSeatAdjust", "", "Manual correction of the remote driver's seat in car-local metres, format carNr:dx,dy,dz;... (0 = Ranger, 1 = Shadowhawk), each value within +-0.5. Empty = automatic anchor from the car's own seat transforms.").Value;
            var passengerAdjust = Config.Bind("Vehicles", "PassengerSeatAdjust", "", "Manual correction of YOUR first-person passenger seat (player root, car-local metres), format carNr:dx,dy,dz;... (0 = Ranger, 1 = Shadowhawk), each within +-0.5. Empty = derived from the car's original companion seat (associateSitPos).").Value;
            settings.Vehicles = new VehicleSettings { TravelMenuKey = travelKey, DriverSeatAdjust = seatAdjust ?? "", PassengerSeatAdjust = passengerAdjust ?? "", InteractDebug = Config.Bind("Vehicles", "InteractDebug", false, "Diagnostics: on each E press near another player's car (max 1/s) log what the original interaction ray hits.").Value,
                GroundDiagnostics = Config.Bind("Vehicles", "GroundDiagnostics", true, "Diagnostics (W4.2B.3 QA): VEHICLE_GROUND lines in plugin.log comparing body clearance and wheel contact of a driven car on the driving and the observing PC (max 1 line per car per 5 s while it moves, 240 per session).").Value };
            var menuKeyName = Config.Bind("Network", "MenuKey", "F8", "Opens / closes the multiplayer menu in the main menu (prepare Host before loading the save, or Join). Does nothing in a loaded world.").Value;
            try { menuKey = (KeyCode)Enum.Parse(typeof(KeyCode), menuKeyName, true); } catch { menuKey = KeyCode.F8; }
            settings.MenuKey = menuKey;
            int hostPort;
            settings.HostPort = NetConfig.TryParsePort(Config.Bind("Network", "HostPort", NetConfig.DefaultPort, "Default UDP port for Host Game (forward exactly this UDP port in the router). Can also be changed in the F8 menu.").Value.ToString(), out hostPort) ? hostPort : NetConfig.DefaultPort;
            settings.DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TS2MP");

            var go = new GameObject("TS2MP_Runner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<NetRunner>();
            // M4.3.2a permanent rule: no mod text in the gameplay UI by default. The status lines and the name tag above the other
            // player are diagnostics, shown only when this is switched on (BepInEx log has the same information). The F8 menu
            // (N1.2) is drawn only while the player has opened it.
            runner.PerfStats = Config.Bind("Debug", "PerfStats", false, "Diagnostics: log frame-time statistics (avg FPS, P50/P95/P99, 1% low), mod main-thread cost, GC and network rates as PERF lines in plugin.log.").Value;
            runner.PerfIntervalSeconds = Mathf.Clamp(Config.Bind("Debug", "PerfIntervalSeconds", 30, "Seconds per PERF report window (10-300).").Value, 10, 300);
            runner.ShowDebugOverlay = Config.Bind("Debug", "ShowOverlay", false, "Diagnostics only: draw the TS2MP status lines and the name tag above the other player (Avatar.ShowNameTag) on screen. Off = the game looks unmodified; everything is in the BepInEx log.").Value;

            if (!legacy)
            {
                string logDir = Path.Combine(settings.DataDir, "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_game_" + System.Diagnostics.Process.GetCurrentProcess().Id);
                var flog = NewLog(logDir, "GAME", "-", "-");
                flog.Info("PLUGIN_INIT mode=F8 mod=" + ProtocolInfo.ModVersion + " protocol=" + ProtocolInfo.ProtocolVersion + " game=" + Application.version
                    + " unity=" + Application.unityVersion + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                    + " bepinex=" + typeof(BaseUnityPlugin).Assembly.GetName().Version + " logDir=" + logDir + " (no patch, no network until F8 Host/Join in the main menu)");
                settings.LogDir = logDir;
                runner.InitMenu(flog, settings, new Harmony(Guid + ".saveguard"));
                return;
            }

            var log = NewLog(args.LogDir, args.Role.ToString().ToUpperInvariant(), args.InstanceId, args.SessionId);
            log.Info("PLUGIN_INIT mode=launcher mod=" + ProtocolInfo.ModVersion + " protocol=" + ProtocolInfo.ProtocolVersion + " game=" + Application.version
                + " unity=" + Application.unityVersion + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id
                + " bepinex=" + typeof(BaseUnityPlugin).Assembly.GetName().Version + " port=" + args.Port + " logDir=" + args.LogDir);
            if (joinCodeRaw != null)
            {
                string code = JoinAuth.Normalize(joinCodeRaw);
                if (code != null) { args.JoinKey = JoinAuth.DeriveKey(code); log.Info("JOIN_CODE received from the launcher (join-code authentication on)"); }
                else log.Error("JOIN_CODE_INVALID: the join code passed by the launcher is malformed; authentication cannot be used");
            }
            else log.Info("JOIN_CODE none (only a loopback host may run without one)");

            // W4.2B.1 developer test mode: loopback Local Test only; every TS2MP file goes to <data>\devtest and the host plays
            // an isolated copy of its save (see DevTestMode.cs). Refused (normal Local Test) for any network session.
            if (args.DevTest)
            {
                if (!DevTestMode.LoopbackOnly(args)) { args.DevTest = false; log.Error("DEVTEST refused: only for the loopback Local Test without a join code (bind " + args.BindAddress + ", connect " + args.ConnectAddress + ")"); }
                else
                {
                    args.DataDir = DevTestMode.IsolatedDataDir(args.DataDir);
                    try { Directory.CreateDirectory(args.DataDir); } catch (Exception ex) { log.Error("DEVTEST data folder not created", ex); }
                    log.Warn("DEVTEST isolated data folder " + args.DataDir + " (Player-2 record, spawn, fleet, sandboxes; production files are not used)");
                }
            }
            bool saveSafe = true;
            if (args.Role == Role.Client) saveSafe = SaveGuard.Install(new Harmony(Guid + ".saveguard"), args, log);
            else if (args.DevTest) saveSafe = SaveGuard.InstallDevHost(new Harmony(Guid + ".saveguard"), args.DataDir, log);
            else log.Info("SAVE_OWNER: host keeps normal game persistence (real save folder). No save patches applied.");
            settings.DataDir = args.DataDir; settings.LogDir = args.LogDir;
            runner.Init(args, log, saveSafe, settings);
        }

        static InstanceLog NewLog(string dir, string role, string inst, string session)
        {
            var log = new InstanceLog(Path.Combine(dir, "plugin.log"), role, inst, session);
            log.Mirror = delegate(string level, string msg)
            {
                if (level == "ERROR") Log.LogError(msg); else if (level == "WARN") Log.LogWarning(msg); else Log.LogInfo(msg);
            };
            return log;
        }
    }

    public sealed class SessionSettings
    {
        public SyncSettings Sync; public WorldSettings World; public VehicleSettings Vehicles;
        public KeyCode MenuKey = KeyCode.F8; public int HostPort = NetConfig.DefaultPort;
        public string DataDir, LogDir;
    }

    public sealed class LaunchArgs
    {
        public Role Role;
        public string SessionId, InstanceId, LogDir, DataDir;
        public string BindAddress = "127.0.0.1", ConnectAddress = "127.0.0.1";
        public int Port;
        public byte[] JoinKey;   // N1: from the environment, never from argv
        public bool DevTest;     // W4.2B.1: -ts2mp-devtest 1 (developer launcher "devtest" command only)

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
                else if (k == "-ts2mp-devtest") a.DevTest = v == "1";
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
        SessionSettings _settings;
        SessionNode _node;
        PlayerSync _sync;
        WorldSync _world;
        VehicleSync _vehicles;
        CarBoxSync _carbox;
        WorldItemSync _items;
        Player2Sync _p2;
        WorldCensus _census;
        WorldKeyMirror _mirror;
        WorldLootSync _loot;
        CampaignBootstrap _campaign;
        WorldObjectSync _doors;   // W4.2B doors / locks / windows
        DevTestMode _devTest;     // W4.2B.1 developer launcher "devtest" only
        MultiplayerMenu _menu;
        bool _quitRequested, _shutdown;
        // M5 performance instrumentation (Debug.PerfStats; off = one null check per frame)
        public bool PerfStats; public int PerfIntervalSeconds = 30;
        FrameStats _frameStats; readonly CostMeter _modCost = new CostMeter(); double _nextPerf;
        int _gc0, _gc1, _gc2; long _netOut0, _netIn0; long _frameTicks;
        GUIStyle _style, _tagStyle;

        // W4.2B.2 F8 multiplayer test campaign: every TS2MP record of this process (Player-2 record, spawn, fleet, client
        // pending file) moves to <data>\testcampaign, and the test conveniences start. Called by the F8 menu after the save
        // isolation is armed and BEFORE the session's systems are built; process-sticky.
        internal void EnableTestCampaign(string testDataDir, string who)
        {
            _settings.DataDir = testDataDir;
            if (_devTest == null)
            {
                try { _devTest = new DevTestMode(_log, "F8 multiplayer test campaign, " + who); }
                catch (Exception ex) { _log.Error("DEVTEST init failed (test conveniences off; isolation stays active)", ex); _devTest = null; }
            }
            _testCampaign = true;
            _log.Warn("TEST_CAMPAIGN active (" + who + "): records in " + testDataDir + "; genuine saves and normal multiplayer records are not used by this process");
        }
        bool _testCampaign;
        internal bool TestCampaignActive { get { return _testCampaign; } }

        internal SessionNode Node { get { return _node; } }
        internal WorldSync World { get { return _world; } }
        internal VehicleSync Vehicles { get { return _vehicles; } }   // W4.2B.3 host readiness (fleet bound)
        internal Player2Sync P2 { get { return _p2; } }
        internal CampaignBootstrap Campaign { get { return _campaign; } }
        internal InstanceLog Log { get { return _log; } }

        // Launcher developer mode: role fixed at startup (M1-N1 behaviour).
        public void Init(LaunchArgs args, InstanceLog log, bool saveSafe, SessionSettings settings)
        {
            _args = args;
            _log = log;
            _settings = settings;
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
            var node = new SessionNode(args.Role, args.SessionId, args.InstanceId, Application.version, args.Port, log);
            node.BindAddress = args.BindAddress;
            node.ConnectAddress = args.ConnectAddress;
            node.JoinKey = args.JoinKey;
            _node = node;
            log.StateProvider = delegate { return _node != null ? _node.StateText : "state -"; };
            if (node.Start()) BuildSystems(node);
            if (args.DevTest) { try { _devTest = new DevTestMode(log); } catch (Exception ex) { log.Error("DEVTEST init failed (test conveniences off; isolation stays active)", ex); _devTest = null; } }
            _menu = new MultiplayerMenu(this, log, settings, null, true);
            StartPerf();
        }

        // N1.2 F8 mode: nothing runs until the player hosts or joins from the menu.
        public void InitMenu(InstanceLog log, SessionSettings settings, Harmony saveGuardHarmony)
        {
            _log = log;
            _settings = settings;
            log.StateProvider = delegate { return _node != null ? _node.StateText : "state -"; };
            _menu = new MultiplayerMenu(this, log, settings, saveGuardHarmony, false);
            StartPerf();
        }

        void StartPerf()
        {
            if (!PerfStats) return;
            _frameStats = new FrameStats(8192);
            _nextPerf = Time.realtimeSinceStartup + PerfIntervalSeconds;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _log.Info("PERF stats ON: window " + PerfIntervalSeconds + " s; 1% low = mean of the slowest 1% frame times; mod cost = TS2MP Update+LateUpdate+FixedUpdate main-thread time only (not game/GPU time)");
        }

        // N1.2: attach a started session node and construct every sync system for its role. False = core systems missing.
        internal bool AttachSession(SessionNode node)
        {
            if (_node != null && _node != node) { _log.Error("SESSION attach refused: another session is active"); return false; }
            _node = node;
            BuildSystems(node);
            return _sync != null && _world != null;
        }

        void BuildSystems(SessionNode node)
        {
            var log = _log; var world = _settings.World;
            try { _sync = new PlayerSync(node, log, _settings.Sync); }
            catch (Exception ex) { log.Error("SYNC init failed", ex); _sync = null; }
            try { _world = new WorldSync(node, log, world); }
            catch (Exception ex) { log.Error("WORLD init failed", ex); _world = null; }
            if (_world == null) return;
            try { _vehicles = new VehicleSync(node, log, _world, _sync, _settings.DataDir, _settings.Vehicles); }
            catch (Exception ex) { log.Error("VEHICLE init failed (vehicle sync disabled)", ex); _vehicles = null; }
            try { _p2 = new Player2Sync(node, log, _world, _settings.DataDir); }
            catch (Exception ex) { log.Error("P2INV init failed (Player-2 persistence disabled)", ex); _p2 = null; }
            try { _carbox = new CarBoxSync(node, log, _world); }
            catch (Exception ex) { log.Error("CARBOX init failed (shared car storage disabled)", ex); _carbox = null; }
            try { _items = new WorldItemSync(node, log, _world); }
            catch (Exception ex) { log.Error("WITEM init failed (shared dropped items disabled)", ex); _items = null; }
            if (world.KeyMirror)
            {
                try { _mirror = new WorldKeyMirror(node, log, _world); if (!_mirror.Ready) { _mirror.Dispose(); _mirror = null; } }
                catch (Exception ex) { log.Error("MIRROR init failed (world-key mirror disabled)", ex); _mirror = null; }
            }
            else log.Info("MIRROR disabled by config (World.KeyMirror = false)");
            if (world.IdentityCensus)
            {
                try { _census = new WorldCensus(log, _settings.LogDir, world.CensusExport); }
                catch (Exception ex) { log.Error("CENSUS init failed (identity census disabled)", ex); _census = null; }
            }
            else log.Info("CENSUS disabled by config (World.IdentityCensus = false)");
            if (_census != null && world.SharedLoot)
            {
                try { _loot = new WorldLootSync(node, log, _world); }
                catch (Exception ex) { log.Error("WLOOT init failed (shared world loot disabled)", ex); _loot = null; }
            }
            else log.Info("WLOOT disabled (" + (!world.SharedLoot ? "World.SharedLoot = false" : "no identity census") + "): original local loot pickups");
            // W4.2: host campaign bootstrap + Player-2 spawn (needs Player2Sync for the record / campaign id, VehicleSync for checked placement)
            try { _campaign = new CampaignBootstrap(node, log, _world, _settings.DataDir, _args != null); _campaign.Vehicles = _vehicles; if (!_campaign.Ready) { _campaign.Dispose(); _campaign = null; } }
            catch (Exception ex) { log.Error("CAMPAIGN init failed (Player 2 cannot load a host campaign; joins time out safely)", ex); _campaign = null; }
            if (_census != null && world.SharedDoors)
            {
                try { _doors = new WorldObjectSync(node, log, _world); }
                catch (Exception ex) { log.Error("WOBJ init failed (shared doors / locks / windows disabled)", ex); _doors = null; }
            }
            else log.Info("WOBJ disabled (" + (!world.SharedDoors ? "World.SharedDoors = false" : "no identity census") + "): original local doors / locks / windows");
        }

        // N1.2: end the session: final writes (fleet, Player-2 record) happen in the systems' Dispose, then the network stops.
        internal void DetachSession(string why)
        {
            if (_node == null && _world == null) return;
            _log.Info("SESSION_END (" + why + ")");
            DisposeSystems();
            if (_node != null) { _node.Dispose(); _node = null; }
        }

        // Order unchanged. W4.2B.3: a failing Dispose no longer disappears silently - the final writes (fleet, Player-2 record,
        // CarBox release, door/loot state) live in these Dispose methods, so every failure is logged once with the system
        // name (bounded: at most one line per system per session end) and the remaining systems are still disposed.
        void DisposeSystems()
        {
            Release(ref _doors, "WOBJ"); Release(ref _campaign, "CAMPAIGN"); Release(ref _loot, "WLOOT"); Release(ref _mirror, "MIRROR");
            Release(ref _census, "CENSUS"); Release(ref _items, "WITEM"); Release(ref _carbox, "CARBOX"); Release(ref _p2, "P2INV");
            Release(ref _vehicles, "VEHICLE"); Release(ref _world, "WORLD"); Release(ref _sync, "SYNC");
        }

        void Release<T>(ref T system, string name) where T : class, IDisposable
        {
            if (system == null) return;
            try { system.Dispose(); }
            catch (Exception ex) { if (_log != null) _log.Error("SESSION_CLEANUP " + name + " dispose failed (final writes of this system may be incomplete)", ex); }
            system = null;
        }

        internal void RequestQuit(string why) { _log.Error(why); _quitRequested = true; }

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
            if (_campaign != null) _log.Info("PERF " + _campaign.Stats);
            if (_doors != null) _log.Info("PERF " + _doors.Stats);
            _modCost.Reset(); _gc0 = g0; _gc1 = g1; _gc2 = g2; _netOut0 = outB; _netIn0 = inB;
        }

        void UpdateBody()
        {
            if (_quitRequested) { _quitRequested = false; Application.Quit(); return; }
            if (_menu != null) _menu.Update();
            if (SaveGuard.Armed && Time.unscaledTime >= _nextSaveCheck)
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
            if (_loot != null && _world != null)
            {
                try { _loot.Update(); }
                catch (Exception ex) { _log.Error("WLOOT update exception (shared world loot disabled)", ex); _loot.Dispose(); _loot = null; }
            }
            if (_campaign != null && _world != null)
            {
                try { _campaign.Update(); }
                catch (Exception ex) { _log.Error("CAMPAIGN update exception (bootstrap disabled)", ex); _campaign.Dispose(); _campaign = null; }
            }
            if (_doors != null && _world != null)
            {
                try { _doors.Update(); }
                catch (Exception ex) { _log.Error("WOBJ update exception (shared doors / locks / windows disabled)", ex); _doors.Dispose(); _doors = null; }
            }
            if (_devTest != null)
            {
                try { _devTest.Update(); }
                catch (Exception ex) { _log.Error("DEVTEST update exception (test conveniences off)", ex); _devTest.Dispose(); _devTest = null; }
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
            if (_menu != null) _menu.OnGUI();
            if (_node == null || !ShowDebugOverlay) return;
            if (_style == null) { _style = new GUIStyle(GUI.skin.label); _style.fontSize = 12; }
            string s = "TS2MP " + _node.Role.ToString().ToUpperInvariant() + " " + _node.InstanceId + " | "
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
            if (_menu != null) { try { _menu.Dispose(); } catch (Exception ex) { _log.Error("SESSION_CLEANUP menu dispose failed", ex); } }
            DisposeSystems();
            if (_node != null) { _node.Dispose(); _node = null; }
            _log.Dispose();
        }
    }
}
