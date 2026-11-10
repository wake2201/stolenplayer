using System;
using System.IO;
using System.Text;

namespace TS2MP.Shared
{
    // One file per game instance (path is unique per launch, so instances never share a file).
    // Line format: UTC timestamp | level | role instance | session | state | message
    public sealed class InstanceLog : IDisposable
    {
        readonly object _lock = new object();
        StreamWriter _writer;
        readonly string _role, _instance, _session;
        public Func<string> StateProvider;
        public Action<string, string> Mirror; // (level, message), e.g. BepInEx logger
        public string FilePath { get; private set; }

        public InstanceLog(string path, string role, string instanceId, string sessionId)
        {
            _role = role; _instance = instanceId; _session = sessionId;
            FilePath = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(fs, new UTF8Encoding(false));
                _writer.AutoFlush = true;
            }
            catch (Exception ex)
            {
                _writer = null;
                FilePath = null;
                if (Mirror != null) Mirror("ERROR", "Cannot open instance log " + path + ": " + ex.Message);
            }
        }

        public void Info(string msg) { Write("INFO", msg); }
        public void Warn(string msg) { Write("WARN", msg); }
        public void Error(string msg) { Write("ERROR", msg); }
        public void Error(string msg, Exception ex) { Write("ERROR", msg + " | " + ex.ToString()); }

        public void Write(string level, string msg)
        {
            string state = "-";
            try { if (StateProvider != null) state = StateProvider(); } catch { }
            string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") + " | " + level.PadRight(5) + " | " + _role + " " + _instance
                + " | session " + _session + " | " + state + " | " + msg;
            lock (_lock)
            {
                if (_writer != null)
                {
                    try { _writer.WriteLine(line); } catch { }
                }
            }
            if (Mirror != null) { try { Mirror(level, msg); } catch { } }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_writer != null) { try { _writer.Flush(); _writer.Dispose(); } catch { } _writer = null; }
            }
        }
    }
}
