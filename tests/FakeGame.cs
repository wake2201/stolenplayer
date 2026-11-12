// Stand-in for "Thief Simulator 2.exe" used only by the launcher tests. Records its arguments (and, in a separate
// file, only a SHA-256 of the TS2MP_JOIN_CODE environment variable, never the value), then idles until killed.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

static class FakeGame
{
    static void Main(string[] args)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        File.AppendAllText(Path.Combine(dir, "fakegame-args.txt"), pid + " " + string.Join(" ", args) + Environment.NewLine);
        string code = Environment.GetEnvironmentVariable("TS2MP_JOIN_CODE");
        string h = "none";
        if (code != null) using (var s = SHA256.Create()) h = BitConverter.ToString(s.ComputeHash(Encoding.ASCII.GetBytes(code))).Replace("-", "").ToLowerInvariant();
        File.AppendAllText(Path.Combine(dir, "fakegame-env.txt"), pid + " " + h + Environment.NewLine);
        Thread.Sleep(Timeout.Infinite);
    }
}
