// Stand-in for "Thief Simulator 2.exe" used only by tests/launcher-test.ps1. Records its arguments, then idles until killed.
using System;
using System.IO;
using System.Threading;

static class FakeGame
{
    static void Main(string[] args)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        File.AppendAllText(Path.Combine(dir, "fakegame-args.txt"), System.Diagnostics.Process.GetCurrentProcess().Id + " " + string.Join(" ", args) + Environment.NewLine);
        Thread.Sleep(Timeout.Infinite);
    }
}
