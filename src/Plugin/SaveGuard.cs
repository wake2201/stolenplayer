// Client-only persistence protection.
// Primary: every game method that reads Application.persistentDataPath is transpiled to use a client sandbox folder,
//          so the client's saves, backups and "new game" wipes land in the sandbox, never in the real save folder.
// Defense in depth: guarded FBPP writes, File.* writes into the real save folder / game folder are blocked,
//          PlayerPrefs (registry) writes, graphics ini writes and Steam achievement writes are blocked.
// Failure to verify => caller quits the client (fail closed).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal static class SaveGuard
    {
        internal static string SandboxDir, RealDir, GameDir;
        static InstanceLog _log;
        static readonly HashSet<string> _logged = new HashSet<string>();
        static readonly Dictionary<MethodBase, int[]> _filePathArgs = new Dictionary<MethodBase, int[]>();
        static int _blockedCount;

        public static string SandboxPath() { return SandboxDir; } // replacement for Application.persistentDataPath

        public static bool Install(Harmony harmony, LaunchArgs args, InstanceLog log)
        {
            _log = log;
            try
            {
                RealDir = Norm(Application.persistentDataPath); // read before patching
                GameDir = Norm(Path.GetDirectoryName(Application.dataPath));
                SandboxDir = Norm(Path.Combine(args.DataDir, "client-sandbox"));
                log.Info("SAVE_GUARD real=" + RealDir + " sandbox=" + SandboxDir + " game=" + GameDir);
                if (IsUnder(SandboxDir, RealDir) || IsUnder(SandboxDir, GameDir) || IsUnder(RealDir, SandboxDir))
                {
                    log.Error("SAVE_GUARD sandbox overlaps real save folder or game folder");
                    return false;
                }
                Directory.CreateDirectory(SandboxDir);
                SeedSandbox();

                if (!PatchRedirects(harmony)) return false;
                if (!VerifyRedirect()) return false;
                InstallGuards(harmony);
                log.Info("SAVE_GUARD_ACTIVE client persistence redirected to sandbox; guards installed; blocked so far=" + _blockedCount);
                return true;
            }
            catch (Exception ex)
            {
                log.Error("SAVE_GUARD install exception", ex);
                return false;
            }
        }

        // Copies the host's current save files into the sandbox (read-only access to the real folder).
        static void SeedSandbox()
        {
            if (!Directory.Exists(RealDir)) { _log.Info("SEED real save folder does not exist yet; client sandbox starts empty"); return; }
            int n = 0;
            foreach (var src in Directory.GetFiles(RealDir, "*", SearchOption.AllDirectories))
            {
                string rel = src.Substring(RealDir.Length).TrimStart('\\', '/');
                if (rel.StartsWith("Unity" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(SandboxDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                try { File.Copy(src, dst, true); n++; }
                catch (Exception ex) { _log.Warn("SEED could not copy " + rel + ": " + ex.Message); }
            }
            _log.Info("SEED copied " + n + " file(s) from real save folder into sandbox");
        }

        static bool PatchRedirects(Harmony harmony)
        {
            var patched = PathRedirect.Apply(harmony,
                new[] { Assembly.Load("Assembly-CSharp"), Assembly.Load("FileBasedPrefs") },
                AccessTools.PropertyGetter(typeof(Application), "persistentDataPath"),
                AccessTools.Method(typeof(SaveGuard), "SandboxPath"), _log);
            return patched != null;
        }

        static bool VerifyRedirect()
        {
            var cfgType = AccessTools.TypeByName("FBPPConfig");
            var cfg = Activator.CreateInstance(cfgType);
            string p = (string)AccessTools.Method(cfgType, "GetSaveFilePath").Invoke(cfg, null);
            bool ok = p != null && IsUnder(Norm(p), SandboxDir);
            if (ok) _log.Info("VERIFY FBPPConfig.GetSaveFilePath() -> " + p + " (inside sandbox)");
            else _log.Error("VERIFY FAILED FBPPConfig.GetSaveFilePath() -> " + p + " (not inside sandbox)");
            return ok && RuntimeCheck();
        }

        // Detects the case where the game initialised FBPP with the real path before our patches existed.
        // Called at install and periodically by NetRunner. Returns false if the active save target is outside the sandbox.
        public static bool RuntimeCheck()
        {
            try
            {
                var fbpp = AccessTools.TypeByName("FBPP");
                var cfg = AccessTools.Field(fbpp, "_config").GetValue(null);
                if (cfg == null) return true; // not initialised yet; initialisation will go through the patched Reinitialize
                string p = (string)AccessTools.Method(cfg.GetType(), "GetSaveFilePath").Invoke(cfg, null);
                if (p != null && IsUnder(Norm(p), SandboxDir)) return true;
                _log.Error("RUNTIME_CHECK FBPP active save file is outside the sandbox: " + p);
                return false;
            }
            catch (Exception ex) { _log.Error("RUNTIME_CHECK exception", ex); return false; }
        }

        static void InstallGuards(Harmony h)
        {
            var block = new HarmonyMethod(AccessTools.Method(typeof(SaveGuard), "BlockCall"));
            TryPrefix(h, AccessTools.Method(AccessTools.TypeByName("FBPP"), "WriteToSaveFile"), new HarmonyMethod(AccessTools.Method(typeof(SaveGuard), "GuardFbppWrite")), "FBPP.WriteToSaveFile guard");

            var prefsBlock = new HarmonyMethod(AccessTools.Method(typeof(SaveGuard), "BlockPrefs"));
            TryPrefix(h, AccessTools.Method(typeof(PlayerPrefs), "SetInt", new[] { typeof(string), typeof(int) }), prefsBlock, "PlayerPrefs.SetInt");
            TryPrefix(h, AccessTools.Method(typeof(PlayerPrefs), "SetFloat", new[] { typeof(string), typeof(float) }), prefsBlock, "PlayerPrefs.SetFloat");
            TryPrefix(h, AccessTools.Method(typeof(PlayerPrefs), "SetString", new[] { typeof(string), typeof(string) }), prefsBlock, "PlayerPrefs.SetString");

            foreach (var name in new[] { "SPrefs:SecureDeleteKey", "GameMenuController:DeletePlayerprefs", "GraphicsSettingsMenu:SaveMenuVariables",
                                         "URP_GraphicsSettingsMenu:SaveMenuVariables", "AchievementManager:SetAchievement" })
            {
                var parts = name.Split(':');
                var t = AccessTools.TypeByName(parts[0]);
                TryPrefix(h, t == null ? null : AccessTools.Method(t, parts[1]), block, name.Replace(':', '.'));
            }

            var fileGuard = new HarmonyMethod(AccessTools.Method(typeof(SaveGuard), "GuardFile"));
            var F = typeof(File);
            var s = typeof(string);
            AddFileGuard(h, AccessTools.Method(F, "WriteAllText", new[] { s, s }), fileGuard, 0);
            AddFileGuard(h, AccessTools.Method(F, "WriteAllText", new[] { s, s, typeof(System.Text.Encoding) }), fileGuard, 0);
            AddFileGuard(h, AccessTools.Method(F, "WriteAllBytes", new[] { s, typeof(byte[]) }), fileGuard, 0);
            AddFileGuard(h, AccessTools.Method(F, "WriteAllLines", new[] { s, typeof(string[]) }), fileGuard, 0);
            AddFileGuard(h, AccessTools.Method(F, "AppendAllText", new[] { s, s }), fileGuard, 0);
            AddFileGuard(h, AccessTools.Method(F, "Copy", new[] { s, s }), fileGuard, 1);
            AddFileGuard(h, AccessTools.Method(F, "Copy", new[] { s, s, typeof(bool) }), fileGuard, 1);
            AddFileGuard(h, AccessTools.Method(F, "Delete", new[] { s }), fileGuard, 0);
            AddFileGuard(h, AccessTools.Method(F, "Move", new[] { s, s }), fileGuard, 0, 1);
        }

        static void TryPrefix(Harmony h, MethodBase target, HarmonyMethod prefix, string label)
        {
            if (target == null) { _log.Warn("GUARD target not found: " + label); return; }
            try { h.Patch(target, prefix); _log.Info("GUARD installed: " + label); }
            catch (Exception ex) { _log.Warn("GUARD failed: " + label + " (" + ex.Message + ")"); }
        }

        static void AddFileGuard(Harmony h, MethodBase target, HarmonyMethod prefix, params int[] pathArgs)
        {
            if (target == null) { _log.Warn("FILE_GUARD target not found"); return; }
            _filePathArgs[target] = pathArgs;
            TryPrefix(h, target, prefix, "File." + target.Name + "(" + target.GetParameters().Length + ")");
        }

        // ---- prefixes (return false = skip original) ----
        static bool BlockCall(MethodBase __originalMethod)
        {
            Blocked(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name, null);
            return false;
        }

        static bool BlockPrefs(MethodBase __originalMethod, string key)
        {
            Blocked("PlayerPrefs." + __originalMethod.Name, key);
            return false;
        }

        static bool GuardFbppWrite()
        {
            string p = null;
            try
            {
                var fbpp = AccessTools.TypeByName("FBPP");
                p = (string)AccessTools.Method(fbpp, "GetSaveFilePath").Invoke(null, null);
            }
            catch (Exception ex) { _log.Error("FBPP guard could not resolve target path; blocking", ex); }
            if (p != null && IsUnder(Norm(p), SandboxDir)) return true;
            Blocked("FBPP.WriteToSaveFile", p);
            return false;
        }

        static bool GuardFile(MethodBase __originalMethod, object[] __args)
        {
            int[] idx;
            if (!_filePathArgs.TryGetValue(__originalMethod, out idx)) return true;
            foreach (var i in idx)
            {
                var p = i < __args.Length ? __args[i] as string : null;
                if (p == null) continue;
                string full;
                try { full = Norm(p); } catch { continue; }
                if (IsUnder(full, RealDir) || (IsUnder(full, GameDir) && !IsUnder(full, Path.Combine(GameDir, "BepInEx"))))
                {
                    Blocked("File." + __originalMethod.Name, full);
                    return false;
                }
            }
            return true;
        }

        static void Blocked(string what, string detail)
        {
            _blockedCount++;
            string key = what + "|" + detail;
            bool first;
            lock (_logged) first = _logged.Add(key);
            if (first || _blockedCount % 100 == 0)
                _log.Warn("BLOCKED_WRITE " + what + (detail != null ? " target=" + detail : "") + " (total blocked=" + _blockedCount + ")");
        }

        static string Norm(string p) { return Path.GetFullPath(p).TrimEnd('\\', '/'); }

        static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            return path.Equals(root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
