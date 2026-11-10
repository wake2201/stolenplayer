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
                ShowNameTag = Config.Bind("Avatar", "ShowNameTag", true, "Show a label above the remote player.").Value
            };

            var go = new GameObject("TS2MP_Runner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            var runner = go.AddComponent<NetRunner>();
            runner.Init(args, log, saveSafe, sync);
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
        bool _quitRequested, _shutdown;
        GUIStyle _style, _tagStyle;

        public void Init(LaunchArgs args, InstanceLog log, bool saveSafe, SyncSettings sync)
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
            if (_node.Start()) _sync = new PlayerSync(_node, log, sync);
        }

        float _nextSaveCheck;

        void Update()
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
        }

        void OnGUI()
        {
            if (_args == null) return;
            if (_style == null) { _style = new GUIStyle(GUI.skin.label); _style.fontSize = 12; }
            string s = "TS2MP " + _args.Role.ToString().ToUpperInvariant() + " " + _args.InstanceId + " | "
                + (_node != null ? _node.State.ToString() + (_node.RemoteInstanceId != null ? " <-> " + _node.RemoteInstanceId : "") : "network off")
                + (_sync != null ? " | " + _sync.StatusLine : "");
            GUI.Label(new Rect(6, 2, 900, 20), s, _style);

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
            if (_sync != null) { try { _sync.Dispose(); } catch { } _sync = null; }
            if (_node != null) { _node.Dispose(); _node = null; }
            _log.Dispose();
        }
    }
}
