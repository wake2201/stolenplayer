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
        // V1.4.1: the medkit use sequence of PlayerScript.Update is found exactly once and replaced by one hook call; the tag
        // compare ("Medkit"), the bleeding check and the original "not_bleeding" warning stay.
        bool medOk = false;
        try
        {
            var psT = asms[0].GetType("PlayerScript");
            var upd = psT.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var orig = PatchProcessor.GetOriginalInstructions(upd);
            var ldstr = System.Reflection.Emit.OpCodes.Ldstr;
            int before = orig.Count(ci => ci.opcode == ldstr && (ci.operand as string) == "Medkit");
            int sites;
            var res = MedkitIL.Rewrite(new List<CodeInstruction>(orig), replacement, out sites);
            int hookAt = res.FindIndex(ci => ci.operand as MethodInfo == replacement);
            medOk = sites == 1 && res.Count(ci => ci.operand as MethodInfo == replacement) == 1
                && !res.Any(ci => ci.operand is MethodInfo && ((MethodInfo)ci.operand).Name == "Heal")
                && res.Count(ci => ci.opcode == ldstr && (ci.operand as string) == "Medkit") == before - 1
                && res.Any(ci => ci.opcode == ldstr && (ci.operand as string) == "not_bleeding")
                && res.Any(ci => ci.opcode == System.Reflection.Emit.OpCodes.Ldfld && ci.operand is FieldInfo && ((FieldInfo)ci.operand).Name == "isBleeding")
                && hookAt >= 2 && res[hookAt - 2].opcode == System.Reflection.Emit.OpCodes.Ldarg_0
                && (res[hookAt - 1].opcode == System.Reflection.Emit.OpCodes.Ldloca_S || res[hookAt - 1].opcode == System.Reflection.Emit.OpCodes.Ldloca)
                && res.Count == orig.Count - 15 + 3;
            Console.WriteLine((medOk ? "PASS" : "FAIL") + "  medkit: PlayerScript.Update use sequence (Heal .. SPrefs.SetInt Medkit 1) found " + sites + "x, replaced by one hook call (" + orig.Count + " -> " + res.Count + " instructions); tag, bleeding check, not_bleeding warning kept");
        }
        catch (Exception ex) { Console.WriteLine("FAIL  medkit IL check: " + ex.Message); }
        // S1: PassTime.Update has exactly one Esc read and one (survival) StopIt call; both are routed through the observer
        // gates, nothing else changes (same instruction count, the "you_cant_sleep" warning and QuitIt call stay).
        bool sleepOk = false;
        try
        {
            var ptT = asms[0].GetType("PassTime");
            var upd = ptT.GetMethod("Update", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            var orig = PatchProcessor.GetOriginalInstructions(upd);
            var keyGate = typeof(RedirectTest).GetMethod("KeyGateStub", BindingFlags.Static | BindingFlags.NonPublic);
            var ruleGate = typeof(RedirectTest).GetMethod("RuleGateStub", BindingFlags.Static | BindingFlags.NonPublic);
            int esc, rule;
            var res = SleepControlIL.Rewrite(new List<CodeInstruction>(orig), keyGate, ruleGate, out esc, out rule);
            Func<CodeInstruction, string> name = ci => ci.operand is MethodInfo ? ((MethodInfo)ci.operand).Name : null;
            sleepOk = esc == 1 && rule == 1 && res.Count == orig.Count
                && res.Count(ci => ci.operand as MethodInfo == keyGate) == 1 && res.Count(ci => ci.operand as MethodInfo == ruleGate) == 1
                && !res.Any(ci => name(ci) == "GetKeyDown") && res.Count(ci => name(ci) == "StopIt") == orig.Count(ci => name(ci) == "StopIt") - 1
                && res.FindIndex(ci => ci.operand as MethodInfo == ruleGate) < res.FindIndex(ci => ci.operand as MethodInfo == keyGate)
                && res.Count(ci => name(ci) == "QuitIt") == 1
                && res.Any(ci => ci.opcode == System.Reflection.Emit.OpCodes.Ldstr && (ci.operand as string) == "you_cant_sleep");
            Console.WriteLine((sleepOk ? "PASS" : "FAIL") + "  S1 sleep control: PassTime.Update Esc read found " + esc + "x, survival StopIt (before the key read) " + rule + "x, both routed through the observer gates (" + orig.Count + " -> " + res.Count + " instructions); Esc-branch StopIt / QuitIt calls and you_cant_sleep warning kept");
        }
        catch (Exception ex) { Console.WriteLine("FAIL  S1 sleep control IL check: " + ex.Message); }
        log.Dispose();
        return scanOk && rewriteFail == 0 && evalOk && medOk && sleepOk ? 0 : 1;
    }
    static bool KeyGateStub(int key) { return false; }
    static void RuleGateStub(object pt) { }
}
