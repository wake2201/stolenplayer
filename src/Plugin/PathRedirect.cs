// Unity-free core of the client save redirect (also exercised offline by tests/RedirectTest.cs).
// Finds every method in the given assemblies that calls `getter` and transpiles that call to `replacement`.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TS2MP.Shared;

namespace TS2MP.Plugin
{
    public static class PathRedirect
    {
        // Callers of Application.persistentDataPath, verified in M1 by IL scan of all Managed assemblies (docs/CODEBASE_MAP.md §3.10).
        public static readonly string[] Expected =
        {
            "FBPPConfig::GetSaveFilePath", "FBPP::BackupSaves", "InitializePrefs::Reinitialize",
            "GameManager::LoadProfile", "GameManager::SaveFileInfo", "MainMenu::BackupSave", "MainMenu::ClickedNewGame",
            "MainMenu::LoadContinueTxt", "SaveButtonInfo::OnEnable", "DemonFile::SaveFileInfo", "GetAudioRadio::Awake",
            "GraphicsSettingsMenu::Start", "URP_GraphicsSettingsMenu::Start"
        };
        public static readonly string[] Critical = { "FBPPConfig::GetSaveFilePath", "FBPP::BackupSaves", "InitializePrefs::Reinitialize" };

        static MethodInfo _getter, _replacement;
        static int _replaced;

        public static string Key(MethodBase m) { return m.DeclaringType.Name + "::" + m.Name; }

        // Returns the list of patched method keys, or null if a critical method could not be patched.
        public static List<string> Apply(Harmony harmony, IEnumerable<Assembly> assemblies, MethodInfo getter, MethodInfo replacement, InstanceLog log)
        {
            _getter = getter;
            _replacement = replacement;
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(PathRedirect), "Transpiler"));
            var patched = new List<string>();
            foreach (var asm in assemblies)
            {
                foreach (var m in FindCallers(asm, getter))
                {
                    string key = Key(m);
                    int before = _replaced;
                    try { harmony.Patch(m, null, null, transpiler); }
                    catch (Exception ex) { log.Error("REDIRECT patch failed for " + key, ex); continue; }
                    int count = _replaced - before;
                    log.Info("REDIRECT " + asm.GetName().Name + " " + key + " replaced=" + count);
                    if (count > 0) patched.Add(key);
                }
            }
            return Evaluate(patched, log);
        }

        public static List<string> Evaluate(List<string> patched, InstanceLog log)
        {
            foreach (var e in Expected)
                if (!patched.Contains(e)) log.Warn("REDIRECT expected method not patched: " + e);
            foreach (var p in patched)
                if (!Expected.Contains(p)) log.Warn("REDIRECT unexpected extra method patched: " + p);
            var missing = Critical.Where(c => !patched.Contains(c)).ToArray();
            if (missing.Length > 0)
            {
                log.Error("REDIRECT critical methods missing: " + string.Join(", ", missing));
                return null;
            }
            log.Info("REDIRECT patched " + patched.Count + "/" + Expected.Length + " expected methods");
            return patched;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int n;
            var result = Rewrite(instructions, _getter, _replacement, out n);
            _replaced += n;
            return result;
        }

        public static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodInfo getter, MethodInfo replacement, out int count)
        {
            count = 0;
            var list = instructions.ToList();
            foreach (var ci in list)
            {
                if (IsCallTo(ci, getter))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                    count++;
                }
            }
            return list;
        }

        public static bool IsCallTo(CodeInstruction ci, MethodInfo target)
        {
            var mi = ci.operand as MethodInfo;
            return (ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && mi != null && mi.Name == target.Name
                && mi.DeclaringType != null && mi.DeclaringType.FullName == target.DeclaringType.FullName;
        }

        // IL byte scan for call/callvirt to target (same technique as the M0 inspection).
        public static IEnumerable<MethodBase> FindCallers(Assembly asm, MethodInfo target)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                if (t.IsGenericTypeDefinition) continue;
                MethodBase[] ms;
                try { ms = t.GetMethods(BF).Cast<MethodBase>().Concat(t.GetConstructors(BF).Cast<MethodBase>()).ToArray(); }
                catch { continue; }
                foreach (var m in ms)
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    byte[] il;
                    try { var body = m.GetMethodBody(); if (body == null) continue; il = body.GetILAsByteArray(); }
                    catch { continue; }
                    if (CallsTarget(m, il, target)) yield return m;
                }
            }
        }

        static bool CallsTarget(MethodBase m, byte[] il, MethodInfo target)
        {
            for (int i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                int tok = BitConverter.ToInt32(il, i + 1);
                int tt = (tok >> 24) & 0xFF;
                if (tt != 0x0A && tt != 0x06) continue;
                MethodBase r;
                try { r = m.Module.ResolveMethod(tok); } catch { continue; }
                if (r.Name == target.Name && r.DeclaringType != null && r.DeclaringType.FullName == target.DeclaringType.FullName) return true;
            }
            return false;
        }
    }
}
