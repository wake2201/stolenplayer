// Offline check of the client save redirect against the real game IL, outside Unity.
// .NET Framework cannot JIT methods that call Unity internal calls ("ECall methods must be packaged into a system
// module"), so this test does NOT apply the Harmony patch. It verifies the portable parts:
//   1. the IL scan finds exactly the expected callers of Application.persistentDataPath;
//   2. reading each method's original IL through Harmony and running the plugin's Rewrite() leaves no call to the
//      getter and inserts the replacement.
// Applying the patch under Unity's Mono runtime is verified in-game (plugin log REDIRECT/VERIFY lines, fail-closed).
// Usage: TS2MP.RedirectTest.exe <game Managed folder>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TS2MP.Plugin;
using TS2MP.Shared;

static class RedirectTest
{
    public static string Sandbox() { return "sandbox"; }

    static int Main(string[] args)
    {
        string managed = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        string logPath = Path.Combine(Path.GetTempPath(), "ts2mp-redirect-test.log");
        if (File.Exists(logPath)) File.Delete(logPath);
        var log = new InstanceLog(logPath, "TEST", "redir", "-");

        var core = Assembly.LoadFrom(Path.Combine(managed, "UnityEngine.CoreModule.dll"));
        var getter = core.GetType("UnityEngine.Application").GetProperty("persistentDataPath").GetGetMethod();
        var replacement = typeof(RedirectTest).GetMethod("Sandbox");
        var asms = new[] { Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll")), Assembly.LoadFrom(Path.Combine(managed, "FileBasedPrefs.dll")) };

        var found = asms.SelectMany(a => PathRedirect.FindCallers(a, getter)).ToList();
        var keys = found.Select(PathRedirect.Key).ToList();
        bool scanOk = keys.Count == PathRedirect.Expected.Length && PathRedirect.Expected.All(keys.Contains);
        Console.WriteLine((scanOk ? "PASS" : "FAIL") + "  IL scan finds exactly the " + PathRedirect.Expected.Length + " expected callers (found " + keys.Count + ": " + string.Join(", ", keys) + ")");

        int rewriteFail = 0;
        var rewritten = new List<string>();
        foreach (var m in found)
        {
            int n;
            var result = PathRedirect.Rewrite(PatchProcessor.GetOriginalInstructions(m), getter, replacement, out n);
            bool remains = result.Any(ci => PathRedirect.IsCallTo(ci, getter));
            bool inserted = result.Count(ci => PathRedirect.IsCallTo(ci, replacement)) == n;
            if (n == 0 || remains || !inserted) { rewriteFail++; Console.WriteLine("   rewrite problem in " + PathRedirect.Key(m) + " n=" + n + " remains=" + remains); }
            else rewritten.Add(PathRedirect.Key(m));
            log.Info("REWRITE " + PathRedirect.Key(m) + " replaced=" + n);
        }
        Console.WriteLine((rewriteFail == 0 ? "PASS" : "FAIL") + "  rewrite leaves no persistentDataPath call in any of " + found.Count + " methods");
        bool evalOk = PathRedirect.Evaluate(rewritten, log) != null;
        Console.WriteLine((evalOk ? "PASS" : "FAIL") + "  critical methods (FBPPConfig.GetSaveFilePath, FBPP.BackupSaves, InitializePrefs.Reinitialize) covered");
        log.Dispose();
        return scanOk && rewriteFail == 0 && evalOk ? 0 : 1;
    }
}
