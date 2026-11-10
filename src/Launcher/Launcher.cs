// TS2-Multiplayer-Launcher: first run starts the Host instance, second run starts the Client instance.
// Starts the installed game exe normally (same as a double-click) and only adds command-line arguments.
// No Steam/DRM/account handling. C# 5, .NET Framework 4.x, no dependencies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TS2MP.Launcher
{
    static class Program
    {
        const string MutexName = @"Local\TS2MP.Launcher";
        const string IniName = "TS2-Multiplayer-Launcher.ini";
        const string PluginRel = @"BepInEx\plugins\TS2MP\TS2MP.Plugin.dll";

        enum Exit { Ok = 0, Config = 2, Unmanaged = 3, Full = 4, Backup = 5, StartFailed = 6, EarlyExit = 7, Locked = 8, Inconsistent = 9, Internal = 10 }

        static string _dataDir;
        static bool _noPause;

        static int Main(string[] argv)
        {
            int code;
            try { code = (int)Run(argv); }
            catch (Exception ex) { Fail("Unexpected error: " + ex); code = (int)Exit.Internal; }
            if (!_noPause)
            {
                if (code != 0) { Console.WriteLine(); Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
                else Thread.Sleep(4000);
            }
            return code;
        }

        static Exit Run(string[] argv)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string iniPath = Path.Combine(exeDir, IniName);
            int ci = Array.IndexOf(argv, "--config");
            if (ci >= 0 && ci + 1 < argv.Length) iniPath = Path.GetFullPath(argv[ci + 1]);
            var cfg = Config.Load(iniPath);
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == "--no-pause") _noPause = true;
                else if (argv[i] == "--game" && i + 1 < argv.Length) cfg.GamePath = argv[++i];
                else if (argv[i] == "--data-dir" && i + 1 < argv.Length) cfg.DataDir = argv[++i];
            }
            _dataDir = cfg.DataDir;
            Directory.CreateDirectory(_dataDir);
            Log("---- launcher start pid=" + Process.GetCurrentProcess().Id + " exe=" + Path.Combine(exeDir, "TS2-Multiplayer-Launcher.exe"));

            // 1. Validate installation
            string gameExe = Path.Combine(cfg.GamePath ?? "", "Thief Simulator 2.exe");
            if (string.IsNullOrEmpty(cfg.GamePath) || !File.Exists(gameExe))
                return Fail(Exit.Config, "Game not found. Set GamePath in " + Path.Combine(exeDir, IniName) + " to the folder containing 'Thief Simulator 2.exe'. Current: '" + cfg.GamePath + "'");
            if (!File.Exists(Path.Combine(cfg.GamePath, "winhttp.dll")) || !File.Exists(Path.Combine(cfg.GamePath, @"BepInEx\core\BepInEx.dll")))
                return Fail(Exit.Config, "BepInEx is not installed in " + cfg.GamePath + ". Run tools\\bepinex.ps1 -Install from the TS2-Multiplayer repo.");
            if (!File.Exists(Path.Combine(cfg.GamePath, PluginRel)))
                return Fail(Exit.Config, "Multiplayer plugin missing: " + Path.Combine(cfg.GamePath, PluginRel) + ". Run tools\\build.ps1 -Deploy.");
            string procName = Path.GetFileNameWithoutExtension(gameExe);

            // 2. Serialize launcher runs (rapid double-clicks)
            using (var mutex = new Mutex(false, MutexName))
            {
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
                catch (AbandonedMutexException) { owned = true; Log("previous launcher crashed while holding the lock; continuing"); }
                if (!owned) return Fail(Exit.Locked, "Another launcher is still busy (waited 60 s). Try again.");
                try { return LaunchLocked(cfg, gameExe, procName); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        static Exit LaunchLocked(Config cfg, string gameExe, string procName)
        {
            string statePath = Path.Combine(_dataDir, "session.state");
            var st = SessionState.Load(statePath);
            bool hostAlive = IsAlive(st.HostPid, st.HostStart, procName);
            bool clientAlive = IsAlive(st.ClientPid, st.ClientStart, procName);

            // 3. Unmanaged instances would write the real saves concurrently
            var known = new HashSet<int>();
            if (hostAlive) known.Add(st.HostPid);
            if (clientAlive) known.Add(st.ClientPid);
            var foreign = Process.GetProcessesByName(procName).Where(p => !known.Contains(p.Id)).Select(p => p.Id).ToList();
            if (foreign.Count > 0)
                return Fail(Exit.Unmanaged, "Thief Simulator 2 is already running without the launcher (PID " + string.Join(", ", foreign) + "). "
                    + "Close it first: an instance without the launcher writes the real save files unprotected.");

            IPAddress tmp;
            if (cfg.Role != "auto" && cfg.Role != "host" && cfg.Role != "client")
                return Fail(Exit.Config, "Invalid Role '" + cfg.Role + "' in the ini (use auto, host or client).");
            if (!IPAddress.TryParse(cfg.BindAddress, out tmp) || !IPAddress.TryParse(cfg.HostAddress, out tmp))
                return Fail(Exit.Config, "BindAddress/HostAddress must be IPv4 addresses (got '" + cfg.BindAddress + "', '" + cfg.HostAddress + "').");

            // LAN mode (M2, opt-in): a non-loopback address requires a shared SessionKey on both PCs.
            bool lan = !IsLoopback(cfg.BindAddress) || !IsLoopback(cfg.HostAddress);
            if (lan && string.IsNullOrEmpty(cfg.SessionKey))
                return Fail(Exit.Config, "LAN addresses are configured (BindAddress=" + cfg.BindAddress + ", HostAddress=" + cfg.HostAddress + ") but SessionKey is empty. Set the same SessionKey on both PCs.");
            string keyedSession = string.IsNullOrEmpty(cfg.SessionKey) ? null : SessionIdFromKey(cfg.SessionKey);

            Role role;
            if (cfg.Role == "client" && !hostAlive)
            {
                // Forced client (other PC hosts): there is no local host process to check.
                if (clientAlive) return Fail(Exit.Full, "A client instance (PID " + st.ClientPid + ") is already running on this PC.");
                if (keyedSession == null) return Fail(Exit.Config, "Role=client without a local host needs SessionKey and HostAddress in the ini.");
                st = new SessionState { SessionId = keyedSession, Port = cfg.BasePort };
                role = Role.Client;
            }
            else
            {
                if (!hostAlive && clientAlive)
                    return Fail(Exit.Inconsistent, "The host instance is gone but the client (PID " + st.ClientPid + ") is still running. Close the client, then start the launcher again.");
                if (hostAlive && clientAlive)
                    return Fail(Exit.Full, "Session full: host (PID " + st.HostPid + ") and client (PID " + st.ClientPid + ") are already running. Only two players are supported.");
                if (cfg.Role == "host" && hostAlive)
                    return Fail(Exit.Full, "Role=host is set but a host instance (PID " + st.HostPid + ") is already running on this PC.");
                role = hostAlive ? Role.Client : Role.Host;
            }

            string instanceId = RandomHex(4);
            if (role == Role.Host)
            {
                // LAN: the remote client must know the port, so use BasePort exactly; local: first free port.
                int port = lan ? (PortFree(cfg.BindAddress, cfg.BasePort) ? cfg.BasePort : 0) : FindFreeUdpPort(cfg.BasePort);
                st = new SessionState { SessionId = keyedSession ?? RandomHex(6), Port = port };
                if (st.Port == 0) return Fail(Exit.StartFailed, lan ? "UDP port " + cfg.BasePort + " is in use on " + cfg.BindAddress + "." : "No free UDP port on 127.0.0.1 between " + cfg.BasePort + " and " + (cfg.BasePort + 20) + ".");
                Exit b = BackupSaves(cfg.GamePath);
                if (b != Exit.Ok) return b;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string roleName = role == Role.Host ? "host" : "client";
            string logDir = Path.Combine(_dataDir, "logs", stamp + "_" + roleName + "_" + instanceId);
            Directory.CreateDirectory(logDir);

            var psi = new ProcessStartInfo(gameExe)
            {
                WorkingDirectory = cfg.GamePath,
                UseShellExecute = false,
                Arguments = "-logFile " + Q(Path.Combine(logDir, "unity.log"))
                    + " -ts2mp-role " + roleName + " -ts2mp-session " + st.SessionId + " -ts2mp-instance " + instanceId
                    + " -ts2mp-port " + st.Port + " -ts2mp-logdir " + Q(logDir) + " -ts2mp-data " + Q(_dataDir)
                    + " -ts2mp-bind " + cfg.BindAddress + " -ts2mp-connect " + cfg.HostAddress
            };
            Log("starting " + roleName + ": " + gameExe + " " + psi.Arguments);
            Process proc;
            try { proc = Process.Start(psi); }
            catch (Exception ex) { return Fail(Exit.StartFailed, "Could not start the game: " + ex.Message); }
            if (proc == null) return Fail(Exit.StartFailed, "Process.Start returned no process.");

            long startTicks = proc.StartTime.ToUniversalTime().Ticks;
            if (role == Role.Host) { st.HostPid = proc.Id; st.HostStart = startTicks; st.HostInstance = instanceId; st.ClientPid = 0; }
            else { st.ClientPid = proc.Id; st.ClientStart = startTicks; st.ClientInstance = instanceId; }
            st.Save(statePath);

            Console.WriteLine("Thief Simulator 2 Multiplayer launcher");
            Console.WriteLine("  Role     : " + roleName.ToUpperInvariant() + (role == Role.Host ? " (waiting for Player 2)" : " (connecting to host " + cfg.HostAddress + ")"));
            Console.WriteLine("  Session  : " + st.SessionId + "   Port: " + (role == Role.Host ? cfg.BindAddress : cfg.HostAddress) + ":" + st.Port + (lan ? "   [LAN mode]" : ""));
            Console.WriteLine("  Instance : " + instanceId + "   PID: " + proc.Id);
            Console.WriteLine("  Logs     : " + logDir);
            Log("started " + roleName + " pid=" + proc.Id + " instance=" + instanceId + " session=" + st.SessionId + " port=" + st.Port + " logDir=" + logDir);

            // Detect an immediate exit (crash, or a relaunch through Steam that would drop our arguments).
            if (proc.WaitForExit(5000))
                return Fail(Exit.EarlyExit, "The game exited within 5 s (exit code " + proc.ExitCode + "). If it restarted through Steam, the multiplayer arguments were lost. See " + logDir);
            return Exit.Ok;
        }

        // ---- save backup (host only, before launch) ----
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
                return Fail(Exit.Backup, "Save backup failed, host not started: " + ex.Message + " (partial backup left in " + dest + ")");
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

        static bool IsLoopback(string addr)
        {
            IPAddress ip;
            return IPAddress.TryParse(addr, out ip) && IPAddress.IsLoopback(ip);
        }

        static bool PortFree(string addr, int port)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(addr, out ip)) return false;
            try { using (new UdpClient(new IPEndPoint(ip, port))) return true; }
            catch (SocketException) { return false; }
        }

        // Both PCs derive the same session id from the shared key; the handshake rejects any other session.
        static string SessionIdFromKey(string key)
        {
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes("TS2MP:" + key))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        static int FindFreeUdpPort(int basePort)
        {
            for (int port = basePort; port <= basePort + 20; port++)
            {
                try { using (new UdpClient(new IPEndPoint(IPAddress.Loopback, port))) return port; }
                catch (SocketException) { }
            }
            return 0;
        }

        static string Sha256(string f)
        {
            using (var s = File.OpenRead(f)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s));
        }

        static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        static string Q(string s) { return "\"" + s + "\""; }

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

    enum Role { Host, Client }

    sealed class SessionState
    {
        public string SessionId = "", HostInstance = "", ClientInstance = "";
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

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, "SessionId=" + SessionId + "\r\nPort=" + Port + "\r\nHostPid=" + HostPid + "\r\nHostStart=" + HostStart
                + "\r\nHostInstance=" + HostInstance + "\r\nClientPid=" + ClientPid + "\r\nClientStart=" + ClientStart + "\r\nClientInstance=" + ClientInstance + "\r\n");
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }
    }

    sealed class Config
    {
        public string GamePath;
        public string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TS2MP");
        public int BasePort = 27960;
        public string Role = "auto";               // auto | host | client
        public string BindAddress = "127.0.0.1";   // host listen address (LAN: this PC's LAN IP or 0.0.0.0)
        public string HostAddress = "127.0.0.1";   // client target (LAN: the host PC's IP)
        public string SessionKey = "";             // required for LAN; same value on both PCs

        public static Config Load(string iniPath)
        {
            var c = new Config();
            if (!File.Exists(iniPath))
            {
                c.GamePath = AutoDetect(AppDomain.CurrentDomain.BaseDirectory);
                File.WriteAllText(iniPath, "; TS2 Multiplayer launcher settings\r\n; Folder that contains 'Thief Simulator 2.exe'\r\nGamePath=" + (c.GamePath ?? "")
                    + "\r\n; Where logs, backups, session state and the client save sandbox are stored\r\nDataDir=" + c.DataDir
                    + "\r\n; First UDP port tried on 127.0.0.1 (next free one up to +20 is used)\r\nBasePort=" + c.BasePort
                    + "\r\n; LAN (optional): Role=auto|host|client, BindAddress (host), HostAddress (client), SessionKey (same on both PCs)\r\nRole=auto\r\nBindAddress=127.0.0.1\r\nHostAddress=127.0.0.1\r\nSessionKey=\r\n");
                return c;
            }
            foreach (var raw in File.ReadAllLines(iniPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim().Trim('"');
                if (k.Equals("GamePath", StringComparison.OrdinalIgnoreCase)) c.GamePath = v;
                else if (k.Equals("DataDir", StringComparison.OrdinalIgnoreCase) && v.Length > 0) c.DataDir = Environment.ExpandEnvironmentVariables(v);
                else if (k.Equals("BasePort", StringComparison.OrdinalIgnoreCase)) int.TryParse(v, out c.BasePort);
                else if (k.Equals("Role", StringComparison.OrdinalIgnoreCase) && v.Length > 0) c.Role = v.ToLowerInvariant();
                else if (k.Equals("BindAddress", StringComparison.OrdinalIgnoreCase) && v.Length > 0) c.BindAddress = v;
                else if (k.Equals("HostAddress", StringComparison.OrdinalIgnoreCase) && v.Length > 0) c.HostAddress = v;
                else if (k.Equals("SessionKey", StringComparison.OrdinalIgnoreCase)) c.SessionKey = v;
            }
            if (string.IsNullOrEmpty(c.GamePath)) c.GamePath = AutoDetect(AppDomain.CurrentDomain.BaseDirectory);
            if (c.BasePort < 1024 || c.BasePort > 65000) c.BasePort = 27960;
            return c;
        }

        static string AutoDetect(string exeDir)
        {
            foreach (var d in new[] { exeDir, Path.GetDirectoryName(exeDir.TrimEnd('\\')) })
                if (d != null && File.Exists(Path.Combine(d, "Thief Simulator 2.exe"))) return d;
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
                        if (File.Exists(Path.Combine(d, "Thief Simulator 2.exe"))) return d;
                    }
                }
            }
            catch { }
            return null;
        }
    }
}
