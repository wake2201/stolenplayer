// TS2-Multiplayer-Launcher - DEVELOPER TOOL ONLY (N1.2). Players use the in-game F8 menu of the drag-and-drop release;
// this launcher is not part of it. It starts the installed game exe normally and only adds command-line arguments
// (fixed-role launcher mode of the plugin); a join code goes to the started game through its process environment only.
// No Steam/DRM/account handling. C# 5, .NET Framework 4.x, no dependencies.
//
//   (no arguments)                 interactive menu
//   local                          Local Test: 1st run = host, 2nd run = client, loopback only (default)
//   devtest                        W4.2B.1 Developer Test: Local Test with isolated test saves in <data>\devtest, all skills, money top-up
//   host  [--port N] [--bind A]    fixed-role host with a new join code (developer two-PC tests)
//   join  [--host H] [--port N] [--code-stdin]   fixed-role client
// Common: --game <folder> --data-dir <folder> --config <ini> --no-pause
// Legacy: only common options and no command = local.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using TS2MP.Shared;

namespace TS2MP.Launcher
{
    static class Program
    {
        const string MutexName = @"Local\TS2MP.Launcher";
        const string IniName = "TS2-Multiplayer-Launcher.ini";
        internal const string GameExe = "Thief Simulator 2.exe";
        const string PluginRel = @"BepInEx\plugins\TS2MP\TS2MP.Plugin.dll";
        const string Version = ProtocolText.ModVersion;

        enum Exit { Ok = 0, Config = 2, Unmanaged = 3, Full = 4, Backup = 5, StartFailed = 6, EarlyExit = 7, Locked = 8, Inconsistent = 9, Internal = 10, PortInUse = 11, Install = 12, Cancelled = 13 }

        static string _dataDir, _iniPath;
        static bool _devTest;   // W4.2B.1 "devtest": Local Test with isolated test saves + developer conveniences
        static bool _noPause, _interactive, _yes, _codeStdin;
        static Config _cfg;
        static string _optHost, _optBind; static int _optPort;

        static int Main(string[] argv)
        {
            int code;
            try { code = (int)Run(argv); }
            catch (Exception ex) { Fail("Unexpected error: " + ex); code = (int)Exit.Internal; }
            if (!_noPause && !_interactive)
            {
                if (code != 0) { Console.WriteLine(); Console.WriteLine("Press Enter to close."); SafeReadLine(); }
                else Thread.Sleep(4000);
            }
            return code;
        }

        static Exit Run(string[] argv)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            _iniPath = Path.Combine(exeDir, IniName);
            string command = null, gameOpt = null, dataOpt = null;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string next = i + 1 < argv.Length ? argv[i + 1] : null;
                switch (a)
                {
                    case "--no-pause": _noPause = true; break;
                    case "--yes": _yes = true; break;
                    case "--code-stdin": _codeStdin = true; break;
                    case "--config": if (next == null) return Fail(Exit.Config, "--config needs a file"); _iniPath = Path.GetFullPath(next); i++; break;
                    case "--game": if (next == null) return Fail(Exit.Config, "--game needs a folder"); gameOpt = next; i++; break;
                    case "--data-dir": if (next == null) return Fail(Exit.Config, "--data-dir needs a folder"); dataOpt = next; i++; break;
                    case "--host": if (next == null) return Fail(Exit.Config, "--host needs an address"); _optHost = next.Trim(); i++; break;
                    case "--bind": if (next == null) return Fail(Exit.Config, "--bind needs an address"); _optBind = next.Trim(); i++; break;
                    case "--port":
                        if (!NetConfig.TryParsePort(next, out _optPort)) return Fail(Exit.Config, "--port must be a number between 1024 and 65535 (got '" + next + "')");
                        i++; break;
                    default:
                        if (a.StartsWith("--")) return Fail(Exit.Config, "Unknown option " + a);
                        if (command != null) return Fail(Exit.Config, "Only one command allowed (got '" + command + "' and '" + a + "')");
                        command = a.ToLowerInvariant();
                        break;
                }
            }
            _cfg = Config.Load(_iniPath);
            if (gameOpt != null) _cfg.GamePath = gameOpt;
            if (dataOpt != null) _cfg.DataDir = dataOpt;
            _dataDir = _cfg.DataDir;
            Directory.CreateDirectory(_dataDir);
            Log("---- launcher " + Version + " start pid=" + Process.GetCurrentProcess().Id + " command=" + (command ?? (argv.Length == 0 ? "menu" : "local")));

            if (command == null && argv.Length == 0) { _interactive = true; return Menu(); }
            switch (command ?? "local")
            {
                case "local": return Locked(() => LaunchLocal());
                case "devtest": UseDevTest(true); return Locked(() => LaunchLocal());
                case "host": return Locked(() => LaunchHost());
                case "join": return Locked(() => LaunchJoin());
                default: return Fail(Exit.Config, "Unknown command '" + command + "'. Commands: local, devtest, host, join. (Players use the in-game F8 menu.)");
            }
        }

        // W4.2B.1 Developer Test: the same loopback Local Test, but every file (session state, logs, Player-2 record, sandboxes,
        // the host's isolated save copy) lives in <data>\devtest and the plugin enables its developer conveniences
        // (-ts2mp-devtest 1). The real save folder is still backed up before the host starts (BackupSaves) and is never written.
        static string _baseDataDir;
        static void UseDevTest(bool on)
        {
            if (_baseDataDir == null) _baseDataDir = _dataDir;
            _devTest = on;
            _dataDir = on ? Path.Combine(_baseDataDir, "devtest") : _baseDataDir;
            Directory.CreateDirectory(_dataDir);
            if (on) Log("devtest: data folder " + _dataDir + " (isolated test saves, all skills, money top-up)");
        }

        // ================= interactive menu =================
        static Exit Menu()
        {
            Exit last = Exit.Ok;
            while (true)
            {
                Clear();
                Console.WriteLine("TS2-Multiplayer " + Version + " - DEVELOPER launcher (players use the in-game F8 menu)");
                Console.WriteLine("Game folder: " + (GameOk() ? _cfg.GamePath : "NOT FOUND - open Settings"));
                Console.WriteLine();
                Console.WriteLine("  1  Local Test         (two game instances on this PC: run twice)");
                Console.WriteLine("  2  Host (fixed role)  (developer two-PC test)");
                Console.WriteLine("  3  Join (fixed role)  (developer two-PC test)");
                Console.WriteLine("  4  Developer Test     (Local Test with ISOLATED test saves, all skills, unlimited money: run twice)");
                Console.WriteLine("  6  Settings");
                Console.WriteLine("  0  Exit");
                Console.WriteLine();
                string choice = Ask("Choice");
                if (choice == null) return last;
                Console.WriteLine();
                switch (choice.Trim())
                {
                    case "1": UseDevTest(false); last = Locked(() => LaunchLocal()); break;
                    case "2": UseDevTest(false); last = Locked(() => LaunchHost()); break;
                    case "3": UseDevTest(false); last = Locked(() => LaunchJoin()); break;
                    case "4": UseDevTest(true); last = Locked(() => LaunchLocal()); break;
                    case "6": SettingsMenu(); continue;
                    case "0": return last;
                    default: continue;
                }
                Console.WriteLine();
                Console.WriteLine("Press Enter to return to the menu.");
                if (SafeReadLine() == null) return last;
            }
        }

        static void SettingsMenu()
        {
            while (true)
            {
                Clear();
                Console.WriteLine("Settings (" + _iniPath + ")");
                Console.WriteLine();
                Console.WriteLine("  1  Game folder        : " + (_cfg.GamePath ?? "(not set)"));
                Console.WriteLine("  2  Host port (UDP)    : " + _cfg.HostPort);
                Console.WriteLine("  3  Host listen address: " + _cfg.BindAddress + "   (0.0.0.0 = all network adapters)");
                Console.WriteLine("  4  Last host address  : " + (_cfg.JoinAddress.Length > 0 ? _cfg.JoinAddress : "(none)") + "   port " + _cfg.JoinPort);
                Console.WriteLine("  5  Search game folder automatically");
                Console.WriteLine("     Data folder        : " + _cfg.DataDir + "   (logs, backups, Player-2 sandbox)");
                Console.WriteLine("  0  Back");
                Console.WriteLine();
                string c = Ask("Choice");
                if (c == null || c.Trim() == "0") return;
                switch (c.Trim())
                {
                    case "1":
                        string g = Ask("Folder that contains 'Thief Simulator 2.exe'");
                        if (g == null) return;
                        g = g.Trim().Trim('"');
                        if (g.Length > 0 && File.Exists(Path.Combine(g, GameExe))) { _cfg.GamePath = Path.GetFullPath(g); _cfg.Save(_iniPath); }
                        else Pause("'Thief Simulator 2.exe' was not found in that folder.");
                        break;
                    case "2":
                        int port;
                        if (NetConfig.TryParsePort(Ask("UDP port (1024-65535, default 27960)"), out port)) { _cfg.HostPort = port; _cfg.Save(_iniPath); }
                        else Pause("Invalid port.");
                        break;
                    case "3":
                        string b = (Ask("Listen address (IPv4, 0.0.0.0 = all adapters)") ?? "").Trim();
                        if (NetConfig.ValidBindAddress(b)) { _cfg.BindAddress = b; _cfg.Save(_iniPath); }
                        else Pause("Invalid address. Use 0.0.0.0 or one of this PC's IPv4 addresses.");
                        break;
                    case "4":
                        string h = (Ask("Host address (IPv4 or DNS name)") ?? "").Trim();
                        int jp;
                        if (!NetConfig.ValidConnectAddress(h)) { Pause("Invalid address."); break; }
                        if (!NetConfig.TryParsePort(Ask("Host port (default 27960)"), out jp)) { Pause("Invalid port."); break; }
                        _cfg.JoinAddress = h; _cfg.JoinPort = jp; _cfg.Save(_iniPath);
                        break;
                    case "5":
                        string found = Config.AutoDetect(AppDomain.CurrentDomain.BaseDirectory);
                        if (found != null) { _cfg.GamePath = found; _cfg.Save(_iniPath); Pause("Found: " + found); }
                        else Pause("Not found automatically. Enter the folder with option 1.");
                        break;
                }
            }
        }

        static Exit CheckInstalledForPlay() { return CheckModFiles(); }

        static Exit CheckModFiles()
        {
            if (!GameOk()) return Fail(Exit.Config, GameMissingText());
            if (!File.Exists(Path.Combine(_cfg.GamePath, "winhttp.dll")) || !File.Exists(Path.Combine(_cfg.GamePath, @"BepInEx\core\BepInEx.dll")))
                return Fail(Exit.Config, "BepInEx is not installed in " + _cfg.GamePath + ". Extract the release ZIP into the game folder (or tools\\bepinex.ps1 -Install).");
            if (!File.Exists(Path.Combine(_cfg.GamePath, PluginRel)))
                return Fail(Exit.Config, "Multiplayer plugin missing: " + Path.Combine(_cfg.GamePath, PluginRel) + ". Run tools\\build.ps1 -Deploy or extract the release ZIP.");
            return Exit.Ok;
        }

        // ================= launching =================
        static Exit Locked(Func<Exit> body)
        {
            using (var mutex = new Mutex(false, MutexName))
            {
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
                catch (AbandonedMutexException) { owned = true; Log("previous launcher crashed while holding the lock; continuing"); }
                if (!owned) return Fail(Exit.Locked, "Another launcher is still busy (waited 60 s). Try again.");
                try { return body(); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        sealed class Instances { public SessionState St; public bool HostAlive, ClientAlive; public string StatePath; }

        static Exit CheckInstances(out Instances inst)
        {
            string procName = Path.GetFileNameWithoutExtension(GameExe);
            inst = new Instances { StatePath = Path.Combine(_dataDir, "session.state") };
            inst.St = SessionState.Load(inst.StatePath);
            inst.HostAlive = IsAlive(inst.St.HostPid, inst.St.HostStart, procName);
            inst.ClientAlive = IsAlive(inst.St.ClientPid, inst.St.ClientStart, procName);
            var known = new HashSet<int>();
            if (inst.HostAlive) known.Add(inst.St.HostPid);
            if (inst.ClientAlive) known.Add(inst.St.ClientPid);
            var foreign = Process.GetProcessesByName(procName).Where(p => !known.Contains(p.Id)).Select(p => p.Id).ToList();
            if (foreign.Count > 0)
                return Fail(Exit.Unmanaged, "Thief Simulator 2 is already running without the launcher (PID " + string.Join(", ", foreign) + "). "
                    + "Close it first: an instance without the launcher writes the real save files unprotected.");
            return Exit.Ok;
        }

        static Exit LaunchHost()
        {
            Exit e = CheckInstalledForPlay();
            if (e != Exit.Ok) return e;
            int port = _optPort > 0 ? _optPort : _cfg.HostPort;
            string bind = _optBind ?? _cfg.BindAddress;
            if (!NetConfig.ValidPort(port)) return Fail(Exit.Config, "Invalid host port " + port + " (1024-65535).");
            if (!NetConfig.ValidBindAddress(bind)) return Fail(Exit.Config, "Invalid listen address '" + bind + "' (IPv4 such as 0.0.0.0).");
            Instances inst;
            if ((e = CheckInstances(out inst)) != Exit.Ok) return e;
            if (inst.HostAlive || inst.ClientAlive)
                return Fail(Exit.Full, "A multiplayer game started by this launcher is still running (PID " + (inst.HostAlive ? inst.St.HostPid : inst.St.ClientPid) + "). Close it first.");
            if (!PortFree(bind, port))
                return Fail(Exit.PortInUse, "UDP port " + port + " is already in use on " + bind + " (another program or game). Close it or choose another port in Settings (and forward that port instead).");

            string code = JoinAuth.NewCode();
            string sessionId = RandomHex(6), instanceId = RandomHex(4);
            Exit b = BackupSaves(_cfg.GamePath);
            if (b != Exit.Ok) return b;
            var st = new SessionState { SessionId = sessionId, Port = port, Mode = "host" };
            Process proc;
            string logDir;
            e = StartGame("host", sessionId, instanceId, port, bind, "127.0.0.1", code, out proc, out logDir);
            if (e != Exit.Ok) return e;
            st.HostPid = proc.Id; st.HostStart = proc.StartTime.ToUniversalTime().Ticks; st.HostInstance = instanceId;
            st.Save(inst.StatePath);

            Console.WriteLine();
            Console.WriteLine("  HOST GAME started (PID " + proc.Id + "). Logs: " + logDir);
            Console.WriteLine();
            Console.WriteLine("  Send your friend these three things (chat, phone):");
            Console.WriteLine("    Address  : your PUBLIC IPv4 address (router status page) or your DynDNS name");
            Console.WriteLine("    Port     : " + port + " (UDP)");
            Console.WriteLine("    Join code: " + JoinAuth.Format(code));
            Console.WriteLine();
            var lan = LocalIPv4();
            Console.WriteLine("  This PC's LAN address" + (lan.Count == 1 ? "" : "es") + ": " + (lan.Count > 0 ? string.Join(", ", lan) : "none found"));
            Console.WriteLine("  The router must forward UDP " + port + " to that address. Same network? Your friend can use the LAN address directly.");
            Console.WriteLine("  The code is new for every Host Game and works only for this session. Only one friend can join.");
            Console.WriteLine("  Note: the join code keeps strangers out, but the game traffic itself is not encrypted.");
            Log("host started pid=" + proc.Id + " instance=" + instanceId + " session=" + sessionId + " bind=" + bind + " port=" + port + " logDir=" + logDir + " (join code shown on screen only)");

            if (proc.WaitForExit(5000)) return EarlyExit(proc, logDir);
            if (_interactive || (!_noPause && !Console.IsInputRedirected))
            {
                Console.WriteLine();
                Console.WriteLine("  [C] copy the join code to the clipboard   [Enter] continue (the game keeps running)");
                while (true)
                {
                    var k = SafeReadLine();
                    if (k != null && k.Trim().Equals("c", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(CopyToClipboard(JoinAuth.Format(code)) ? "  Copied." : "  Copy failed; type the code manually."); continue; }
                    break;
                }
                _noPause = true;
            }
            return Exit.Ok;
        }

        static Exit LaunchJoin()
        {
            Exit e = CheckInstalledForPlay();
            if (e != Exit.Ok) return e;
            bool ask = _interactive || (!Console.IsInputRedirected && !_codeStdin && _optHost == null);
            string host = _optHost;
            if (host == null && ask)
            {
                string def = _cfg.JoinAddress;
                host = Ask("Host address (IPv4 or DNS name" + (def.Length > 0 ? ", Enter = " + def : "") + ")");
                if (host == null) return Exit.Cancelled;
                host = host.Trim();
                if (host.Length == 0) host = def;
            }
            if (string.IsNullOrEmpty(host)) host = _cfg.JoinAddress;
            if (!NetConfig.ValidConnectAddress(host)) return Fail(Exit.Config, "Invalid host address '" + host + "'. Use an IPv4 address such as 203.0.113.7 or a DNS name, without the port.");
            int port = _optPort;
            if (port == 0 && ask)
            {
                string ps = Ask("Port (Enter = " + _cfg.JoinPort + ")");
                if (ps == null) return Exit.Cancelled;
                if (ps.Trim().Length == 0) port = _cfg.JoinPort;
                else if (!NetConfig.TryParsePort(ps, out port)) return Fail(Exit.Config, "Invalid port '" + ps + "' (1024-65535).");
            }
            if (port == 0) port = _cfg.JoinPort;

            string code = null;
            for (int tries = 0; code == null; tries++)
            {
                string raw = _codeStdin ? SafeReadLine() : Ask("Join code (shown on the host PC)");
                if (raw == null) return _codeStdin ? Fail(Exit.Config, "No join code on standard input.") : Exit.Cancelled;
                code = JoinAuth.Normalize(raw);
                if (code == null)
                {
                    if (_codeStdin || !ask || tries >= 4) return Fail(Exit.Config, "That is not a valid join code (25 characters, e.g. ABCDE-FGH12-...). Check for typos.");
                    Console.WriteLine("  Not a valid join code - check for typos and try again.");
                }
            }

            if (!NetConfig.IsIPv4Literal(host))
            {
                string resolved;
                if (!Resolve(host, out resolved))
                {
                    if (!ask) return Fail(Exit.Config, "Host name '" + host + "' cannot be resolved (check the spelling or your internet connection).");
                    if (!Confirm("  '" + host + "' cannot be resolved right now. Start anyway (the game keeps retrying)?")) return Exit.Cancelled;
                }
                else Console.WriteLine("  " + host + " -> " + resolved);
            }

            Instances inst;
            if ((e = CheckInstances(out inst)) != Exit.Ok) return e;
            if (inst.ClientAlive) return Fail(Exit.Full, "A Player-2 instance (PID " + inst.St.ClientPid + ") is already running on this PC.");
            if (inst.HostAlive && inst.St.Mode != "host")
                return Fail(Exit.Full, "A Local Test session is running on this PC. Close it before joining another game.");

            _cfg.JoinAddress = host; _cfg.JoinPort = port;
            try { _cfg.Save(_iniPath); } catch (Exception ex) { Log("could not save ini: " + ex.Message); }

            if (!inst.HostAlive)
            {
                // Joining from a separate PC: back up this PC's own saves first (the client never writes them; see SaveGuard).
                Exit b = BackupSaves(_cfg.GamePath);
                if (b != Exit.Ok) return b;
            }
            string instanceId = RandomHex(4);
            Process proc; string logDir;
            e = StartGame("client", "join", instanceId, port, "127.0.0.1", host, code, out proc, out logDir);
            if (e != Exit.Ok) return e;
            var st = inst.HostAlive ? inst.St : new SessionState { SessionId = "join", Port = port, Mode = "join" };
            st.ClientPid = proc.Id; st.ClientStart = proc.StartTime.ToUniversalTime().Ticks; st.ClientInstance = instanceId;
            st.Save(inst.StatePath);
            Console.WriteLine();
            Console.WriteLine("  JOIN GAME started (PID " + proc.Id + "), connecting to " + host + ":" + port + " (UDP). Logs: " + logDir);
            Console.WriteLine("  You play as Player 2. Your own saves on this PC are not used or changed.");
            Log("join started pid=" + proc.Id + " instance=" + instanceId + " target=" + host + ":" + port + " logDir=" + logDir);
            if (proc.WaitForExit(5000)) return EarlyExit(proc, logDir);
            return Exit.Ok;
        }

        // Local Test / Developer mode (M1 behaviour): first run = host, second run = client, loopback only, no join code
        // (a 127.0.0.1 host cannot be reached from the network; the plugin refuses a network bind without a code).
        static Exit LaunchLocal()
        {
            if (!_cfg.LegacyLanKeys.All(string.IsNullOrEmpty))
                return Fail(Exit.Config, "Role/HostAddress/SessionKey in " + _iniPath + " are no longer used. Remove them: play over a network with Host Game / Join Game (join code).");
            Exit e = CheckModFiles();
            if (e != Exit.Ok) return e;
            Instances inst;
            if ((e = CheckInstances(out inst)) != Exit.Ok) return e;
            var st = inst.St;
            if ((inst.HostAlive || inst.ClientAlive) && st.Mode != "local" && st.Mode != "")
                return Fail(Exit.Full, "A Host Game / Join Game session started by this launcher is running. Close it before a Local Test.");
            if (!inst.HostAlive && inst.ClientAlive)
                return Fail(Exit.Inconsistent, "The host instance is gone but the client (PID " + st.ClientPid + ") is still running. Close the client, then start the launcher again.");
            if (inst.HostAlive && inst.ClientAlive)
                return Fail(Exit.Full, "Session full: host (PID " + st.HostPid + ") and client (PID " + st.ClientPid + ") are already running. Only two players are supported.");
            bool asHost = !inst.HostAlive;
            string instanceId = RandomHex(4);
            if (asHost)
            {
                int port = FindFreeUdpPort(_cfg.BasePort);
                if (port == 0) return Fail(Exit.PortInUse, "No free UDP port on 127.0.0.1 between " + _cfg.BasePort + " and " + (_cfg.BasePort + 20) + ".");
                st = new SessionState { SessionId = RandomHex(6), Port = port, Mode = "local" };
                Exit b = BackupSaves(_cfg.GamePath);
                if (b != Exit.Ok) return b;
            }
            string role = asHost ? "host" : "client";
            Process proc; string logDir;
            e = StartGame(role, st.SessionId, instanceId, st.Port, "127.0.0.1", "127.0.0.1", null, out proc, out logDir);
            if (e != Exit.Ok) return e;
            long startTicks = proc.StartTime.ToUniversalTime().Ticks;
            if (asHost) { st.HostPid = proc.Id; st.HostStart = startTicks; st.HostInstance = instanceId; st.ClientPid = 0; }
            else { st.ClientPid = proc.Id; st.ClientStart = startTicks; st.ClientInstance = instanceId; }
            st.Save(inst.StatePath);
            Console.WriteLine("Thief Simulator 2 Multiplayer launcher - " + (_devTest ? "DEVELOPER TEST (isolated test saves, all skills, unlimited money)" : "Local Test"));
            Console.WriteLine("  Role     : " + role.ToUpperInvariant() + (asHost ? " (start the launcher again for Player 2)" : " (connecting to the local host)"));
            Console.WriteLine("  Session  : " + st.SessionId + "   Port: 127.0.0.1:" + st.Port);
            Console.WriteLine("  Instance : " + instanceId + "   PID: " + proc.Id);
            Console.WriteLine("  Logs     : " + logDir);
            Log("started local " + role + " pid=" + proc.Id + " instance=" + instanceId + " session=" + st.SessionId + " port=" + st.Port + " logDir=" + logDir);
            if (proc.WaitForExit(5000)) return EarlyExit(proc, logDir);
            return Exit.Ok;
        }

        static Exit StartGame(string role, string sessionId, string instanceId, int port, string bind, string connect, string joinCode, out Process proc, out string logDir)
        {
            proc = null;
            string gameExe = Path.Combine(_cfg.GamePath, GameExe);
            logDir = Path.Combine(_dataDir, "logs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + role + "_" + instanceId);
            Directory.CreateDirectory(logDir);
            var psi = new ProcessStartInfo(gameExe)
            {
                WorkingDirectory = _cfg.GamePath,
                UseShellExecute = false,
                Arguments = "-logFile " + Q(Path.Combine(logDir, "unity.log"))
                    + " -ts2mp-role " + role + " -ts2mp-session " + sessionId + " -ts2mp-instance " + instanceId
                    + " -ts2mp-port " + port + " -ts2mp-logdir " + Q(logDir) + " -ts2mp-data " + Q(_dataDir)
                    + " -ts2mp-bind " + bind + " -ts2mp-connect " + connect + (_devTest ? " -ts2mp-devtest 1" : "")
            };
            // The join code never appears in the arguments, the launcher log or a file: only in this child's environment.
            psi.EnvironmentVariables.Remove(JoinAuth.EnvVar);
            if (joinCode != null) psi.EnvironmentVariables[JoinAuth.EnvVar] = joinCode;
            Log("starting " + role + ": " + gameExe + " " + psi.Arguments + (joinCode != null ? " [join code in child environment]" : ""));
            try { proc = Process.Start(psi); }
            catch (Exception ex) { return Fail(Exit.StartFailed, "Could not start the game: " + ex.Message); }
            if (proc == null) return Fail(Exit.StartFailed, "Process.Start returned no process.");
            return Exit.Ok;
        }

        static Exit EarlyExit(Process proc, string logDir)
        {
            return Fail(Exit.EarlyExit, "The game exited within 5 s (exit code " + proc.ExitCode + "). If it restarted through Steam, the multiplayer arguments were lost. See " + logDir);
        }

        // ---- save backup (before every host or separate-PC client launch) ----
        static Exit BackupSaves(string gamePath)
        {
            string company, product;
            if (!ReadAppInfo(gamePath, out company, out product))
                return Fail(Exit.Backup, "Cannot read app.info to locate the save folder; refusing to start without a backup.");
            string saveDir = Path.Combine(GetLocalLow(), company, product);
            if (!Directory.Exists(saveDir))
            {
                Log("backup: no save folder yet (" + saveDir + "); nothing to back up");
                Console.WriteLine("  Backup   : no save folder yet");
                return Exit.Ok;
            }
            string dest = Path.Combine(_dataDir, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            if (Directory.Exists(dest)) dest += "_" + RandomHex(2);
            try
            {
                int n = 0;
                foreach (var src in Directory.GetFiles(saveDir, "*", SearchOption.AllDirectories))
                {
                    string rel = src.Substring(saveDir.Length).TrimStart('\\');
                    string dst = Path.Combine(dest, "saves", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, false);
                    if (Sha256(src) != Sha256(dst)) throw new IOException("hash mismatch after copy: " + rel + " (file may be in use)");
                    n++;
                }
                string regKey = @"HKCU\Software\" + company + @"\" + product;
                string regNote = ExportRegistry(regKey, Path.Combine(dest, "playerprefs.reg"));
                Log("backup: " + n + " file(s) verified -> " + dest + "; registry: " + regNote);
                Console.WriteLine("  Backup   : " + n + " save file(s) verified -> " + dest);
                return Exit.Ok;
            }
            catch (Exception ex)
            {
                return Fail(Exit.Backup, "Save backup failed, game not started: " + ex.Message + " (partial backup left in " + dest + ")");
            }
        }

        static string ExportRegistry(string key, string file)
        {
            var psi = new ProcessStartInfo("reg.exe", "export " + Q(key) + " " + Q(file) + " /y") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(15000);
                return p.ExitCode == 0 ? "exported" : "key not present (reg exit " + p.ExitCode + ")";
            }
        }

        static bool ReadAppInfo(string gamePath, out string company, out string product)
        {
            company = product = null;
            string f = Path.Combine(gamePath, "Thief Simulator 2_Data", "app.info");
            if (!File.Exists(f)) return false;
            var lines = File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (lines.Length < 2) return false;
            company = lines[0]; product = lines[1];
            return company.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && product.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        static string GetLocalLow()
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid("A520A1A4-1780-4FF6-BD18-167343C5AF16"), 0, IntPtr.Zero, out p) != 0)
                return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low";
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }

        // ---- helpers ----
        static bool GameOk() { return !string.IsNullOrEmpty(_cfg.GamePath) && File.Exists(Path.Combine(_cfg.GamePath, GameExe)); }
        public static string Sha256(string f)
        {
            using (var st = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(st)).Replace("-", "");
        }

        static string GameMissingText()
        {
            return "Game not found. Set the folder containing 'Thief Simulator 2.exe' in Settings (or GamePath in " + _iniPath + "). Current: '" + _cfg.GamePath + "'";
        }

        static bool IsAlive(int pid, long startTicks, string procName)
        {
            if (pid <= 0) return false;
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited || !p.ProcessName.Equals(procName, StringComparison.OrdinalIgnoreCase)) return false;
                return Math.Abs(p.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.TicksPerSecond; // guards against PID reuse
            }
            catch { return false; }
        }

        static bool PortFree(string addr, int port)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip)) return false;
            try
            {
                using (var u = new UdpClient(AddressFamily.InterNetwork))
                {
                    u.ExclusiveAddressUse = true;
                    u.Client.Bind(new IPEndPoint(ip, port));
                    return true;
                }
            }
            catch (SocketException) { return false; }
        }

        static int FindFreeUdpPort(int basePort)
        {
            for (int port = basePort; port <= basePort + 20; port++)
                if (PortFree("127.0.0.1", port)) return port;
            return 0;
        }

        static List<string> LocalIPv4()
        {
            var r = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address) && !ua.Address.ToString().StartsWith("169.254."))
                            r.Add(ua.Address.ToString());
                }
            }
            catch { }
            return r;
        }

        static bool Resolve(string host, out string ipv4)
        {
            ipv4 = null;
            try
            {
                var ar = Dns.BeginGetHostAddresses(host, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(5000)) return false;
                foreach (var a in Dns.EndGetHostAddresses(ar)) if (a.AddressFamily == AddressFamily.InterNetwork) { ipv4 = a.ToString(); return true; }
            }
            catch { }
            return false;
        }

        // clip.exe reads the text from stdin, so the code is not placed on any command line.
        static bool CopyToClipboard(string text)
        {
            try
            {
                var psi = new ProcessStartInfo("clip.exe") { UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true };
                using (var p = Process.Start(psi)) { p.StandardInput.Write(text); p.StandardInput.Close(); p.WaitForExit(5000); return p.ExitCode == 0; }
            }
            catch { return false; }
        }

        static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var r = System.Security.Cryptography.RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        static string Q(string s) { return "\"" + s + "\""; }

        static void Clear() { try { Console.Clear(); } catch (IOException) { } }
        static string SafeReadLine() { try { return Console.ReadLine(); } catch { return null; } }
        static string Ask(string prompt) { Console.Write(prompt + ": "); return SafeReadLine(); }
        static bool Confirm(string q) { var a = Ask(q + " [y/N]"); return a != null && (a.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || a.Trim().Equals("j", StringComparison.OrdinalIgnoreCase)); }
        static void Pause(string msg) { Console.WriteLine(msg); Console.WriteLine("Press Enter."); SafeReadLine(); }

        static Exit Fail(Exit code, string msg) { Fail(msg); return code; }

        static void Fail(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: " + msg);
            Console.ResetColor();
            Log("ERROR " + msg);
        }

        static void Log(string msg)
        {
            if (_dataDir == null) return;
            try
            {
                string dir = Path.Combine(_dataDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "launcher.log"), DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " [launcher " + Process.GetCurrentProcess().Id + "] " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // The launcher does not link the LiteNetLib-dependent Protocol.cs; tests/installer-test.ps1 checks that this
    // matches ProtocolInfo.ModVersion.
    static class ProtocolText { public const string ModVersion = "0.12.0"; }

    sealed class SessionState
    {
        public string SessionId = "", HostInstance = "", ClientInstance = "", Mode = "";
        public int Port, HostPid, ClientPid;
        public long HostStart, ClientStart;

        public static SessionState Load(string path)
        {
            var s = new SessionState();
            if (!File.Exists(path)) return s;
            foreach (var line in File.ReadAllLines(path))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i), v = line.Substring(i + 1);
                switch (k)
                {
                    case "SessionId": s.SessionId = v; break;
                    case "Mode": s.Mode = v; break;
                    case "Port": int.TryParse(v, out s.Port); break;
                    case "HostPid": int.TryParse(v, out s.HostPid); break;
                    case "HostStart": long.TryParse(v, out s.HostStart); break;
                    case "HostInstance": s.HostInstance = v; break;
                    case "ClientPid": int.TryParse(v, out s.ClientPid); break;
                    case "ClientStart": long.TryParse(v, out s.ClientStart); break;
                    case "ClientInstance": s.ClientInstance = v; break;
                }
            }
            return s;
        }

        // No secrets in here: the join code is never persisted.
        public void Save(string path)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, "SessionId=" + SessionId + "\r\nMode=" + Mode + "\r\nPort=" + Port + "\r\nHostPid=" + HostPid + "\r\nHostStart=" + HostStart
                + "\r\nHostInstance=" + HostInstance + "\r\nClientPid=" + ClientPid + "\r\nClientStart=" + ClientStart + "\r\nClientInstance=" + ClientInstance + "\r\n");
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }

    sealed class Config
    {
        public string GamePath;
        public string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TS2MP");
        public int BasePort = 27960;                 // Local Test: first loopback port tried
        public int HostPort = NetConfig.DefaultPort; // Host Game: UDP port (forward this one)
        public string BindAddress = "0.0.0.0";       // Host Game: listen address
        public string JoinAddress = "";              // Join Game: last host address (no code is ever stored)
        public int JoinPort = NetConfig.DefaultPort;
        public string[] LegacyLanKeys = new string[3]; // M2 Role / HostAddress / SessionKey (replaced by Host/Join)
        string _dataDirRaw = @"%LOCALAPPDATA%\TS2MP";

        public static Config Load(string iniPath)
        {
            var c = new Config();
            if (File.Exists(iniPath))
            {
                foreach (var raw in File.ReadAllLines(iniPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim().Trim('"');
                    int n;
                    if (Is(k, "GamePath")) c.GamePath = v.Length > 0 ? v : null;
                    else if (Is(k, "DataDir") && v.Length > 0) { c._dataDirRaw = v; c.DataDir = Environment.ExpandEnvironmentVariables(v); }
                    else if (Is(k, "BasePort")) { if (int.TryParse(v, out n) && n >= 1024 && n <= 65000) c.BasePort = n; }
                    else if (Is(k, "HostPort")) { if (NetConfig.TryParsePort(v, out n)) c.HostPort = n; }
                    else if (Is(k, "BindAddress")) { if (NetConfig.ValidBindAddress(v)) c.BindAddress = v; }
                    else if (Is(k, "JoinAddress")) { if (v.Length == 0 || NetConfig.ValidConnectAddress(v)) c.JoinAddress = v; }
                    else if (Is(k, "JoinPort")) { if (NetConfig.TryParsePort(v, out n)) c.JoinPort = n; }
                    else if (Is(k, "Role")) { if (!v.Equals("auto", StringComparison.OrdinalIgnoreCase)) c.LegacyLanKeys[0] = v; }
                    else if (Is(k, "HostAddress")) { if (v.Length > 0 && v != "127.0.0.1") c.LegacyLanKeys[1] = v; }
                    else if (Is(k, "SessionKey")) c.LegacyLanKeys[2] = v;
                }
            }
            if (string.IsNullOrEmpty(c.GamePath) || !File.Exists(Path.Combine(c.GamePath, Program.GameExe)))
            {
                string found = AutoDetect(AppDomain.CurrentDomain.BaseDirectory);
                if (found != null) c.GamePath = found;
            }
            if (!File.Exists(iniPath)) { try { c.Save(iniPath); } catch { } }
            return c;
        }

        static bool Is(string k, string name) { return k.Equals(name, StringComparison.OrdinalIgnoreCase); }

        public void Save(string iniPath)
        {
            string text = "; TS2 Multiplayer launcher settings (no join code is ever stored here)\r\n"
                + "; Folder that contains 'Thief Simulator 2.exe'\r\nGamePath=" + (GamePath ?? "") + "\r\n"
                + "; Logs, backups, session state and the Player-2 save sandbox\r\nDataDir=" + _dataDirRaw + "\r\n"
                + "; Host Game: UDP port to forward in the router, and the listen address (0.0.0.0 = all adapters)\r\nHostPort=" + HostPort + "\r\nBindAddress=" + BindAddress + "\r\n"
                + "; Join Game: last used host address and port\r\nJoinAddress=" + JoinAddress + "\r\nJoinPort=" + JoinPort + "\r\n"
                + "; Local Test (developer): first UDP port tried on 127.0.0.1\r\nBasePort=" + BasePort + "\r\n";
            string tmp = iniPath + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(iniPath)) File.Replace(tmp, iniPath, null); else File.Move(tmp, iniPath);
        }

        public static string AutoDetect(string exeDir)
        {
            foreach (var d in new[] { exeDir, Path.GetDirectoryName(exeDir.TrimEnd('\\')) })
                if (d != null && File.Exists(Path.Combine(d, Program.GameExe))) return d;
            try
            {
                var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (steam != null)
                {
                    var libs = new List<string> { steam };
                    string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (var l in File.ReadAllLines(vdf))
                        {
                            var t = l.Trim();
                            if (t.StartsWith("\"path\"")) libs.Add(t.Substring(6).Trim().Trim('"').Replace(@"\\", @"\"));
                        }
                    foreach (var lib in libs)
                    {
                        string d = Path.Combine(lib, @"steamapps\common\Thief Simulator 2");
                        if (File.Exists(Path.Combine(d, Program.GameExe))) return d;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
