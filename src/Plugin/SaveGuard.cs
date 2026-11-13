// Player-2 (client) persistence protection.
// N1.2: a normally started game (solo / host) applies NO patch here. Only when the process becomes Player 2 - from the
// F8 menu after authentication + host readiness (main menu, before any client persistence or scene load), or at startup
// in the launcher's developer client mode - the redirect and guards are installed and ARMED. Once armed it stays armed.
// Arming: seed the sandbox (read-only copy of the real folder), switch the redirect, re-run the original
// InitializePrefs.Reinitialize so FBPP (which caches its folder in FBPPConfig.SaveFilePath) reloads from the sandbox,
// then verify that every save target resolves inside the sandbox. Any failure => caller fails closed.
// Primary: every game method that reads Application.persistentDataPath is transpiled to SandboxPath().
// Defense in depth (armed only): guarded FBPP writes, File.* writes into the real save folder / game folder blocked,
//          PlayerPrefs (registry) writes, graphics ini writes and Steam achievement writes blocked.
using System;
using System.Collections.Generic;
using System.IO;
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
        internal static volatile bool Armed;
        internal static bool Available { get; private set; }   // precheck passed (Join allowed)
        static bool _installed;
        static Harmony _harmony;
        static InstanceLog _log;
        static readonly HashSet<string> _logged = new HashSet<string>();
        static readonly Dictionary<MethodBase, int[]> _filePathArgs = new Dictionary<MethodBase, int[]>();
        static int _blockedCount;

        // Replacement for Application.persistentDataPath in the 13 redirected callers.
        public static string SandboxPath() { return Armed ? SandboxDir : Application.persistentDataPath; }

        // Launcher developer mode (client role fixed at startup): precheck + install + arm at once.
        public static bool Install(Harmony harmony, LaunchArgs args, InstanceLog log)
        {
            string why;
            if (!Precheck(harmony, args.DataDir, log)) return false;
            if (!Arm(out why)) { log.Error("SAVE_GUARD arm failed: " + why); return false; }
            return true;
        }

        // Read-only: folder layout and the original types/methods the protection relies on. No patch is applied.
        public static bool Precheck(Harmony harmony, string dataDir, InstanceLog log)
        {
            _log = log; _harmony = harmony;
            try
            {
                RealDir = SaveIsolation.Norm(Application.persistentDataPath);   // the original value, nothing is redirected yet
                GameDir = SaveIsolation.Norm(Path.GetDirectoryName(Application.dataPath));
                SandboxDir = SaveIsolation.Norm(Path.Combine(dataDir, "client-sandbox"));
                log.Info("SAVE_GUARD real=" + RealDir + " sandbox=" + SandboxDir + " game=" + GameDir);
                string layout = SaveIsolation.LayoutError(SandboxDir, RealDir, GameDir);
                if (layout != null) { log.Error("SAVE_GUARD " + layout); return false; }
                var fbpp = AccessTools.TypeByName("FBPP"); var cfg = AccessTools.TypeByName("FBPPConfig"); var ip = AccessTools.TypeByName("InitializePrefs");
                if (fbpp == null || cfg == null || ip == null || AccessTools.Field(fbpp, "_config") == null || AccessTools.Method(ip, "Reinitialize") == null
                    || AccessTools.Method(cfg, "GetSaveFilePath") == null || AccessTools.Method(fbpp, "WriteToSaveFile") == null)
                { log.Error("SAVE_GUARD original save API not found (FBPP / FBPPConfig / InitializePrefs)"); return false; }
                Available = true;
                log.Info("SAVE_GUARD precheck ok (no patch applied until this process becomes Player 2)");
                return true;
            }
            catch (Exception ex)
            {
                log.Error("SAVE_GUARD precheck exception", ex);
                return false;
            }
        }

        // Main thread only; outside a gameplay scene (main menu) or before any scene (developer mode).
        public static bool Arm(out string why)
        {
            why = null;
            if (Armed) { why = RuntimeCheck() ? null : "active save target outside the sandbox"; return why == null; }
            if (!Available) { why = "save protection precheck failed"; return false; }
            try
            {
                if (!_installed)
                {
                    if (!PatchRedirects(_harmony)) { why = "save-path redirect could not be applied"; return false; }
                    InstallGuards(_harmony);
                    _installed = true;
                    _log.Info("SAVE_GUARD_INSTALLED redirect + guards (inactive until armed)");
                }
                Directory.CreateDirectory(SandboxDir);
                CaptureLocalPreferences();
                RetireSandboxSaves();
                SeedSandbox();
                Armed = true;   // from here on every guard is active and the redirect points at the sandbox
                _log.Info("SAVE_GUARD_ARMED redirect -> sandbox, guards active");
                var fbpp = AccessTools.TypeByName("FBPP");
                bool fbppStarted = AccessTools.Field(fbpp, "_config").GetValue(null) != null;
                if (fbppStarted)
                {
                    // The original keeps the folder in FBPPConfig.SaveFilePath: re-run its own initialisation (now redirected).
                    if (!ReinitializePrefs(out why)) return false;
                    _log.Info("SAVE_GUARD FBPP re-initialised from the sandbox (InitializePrefs.Reinitialize)");
                }
                if (!VerifyRedirect(out why)) return false;
                _log.Info("SAVE_GUARD_ACTIVE client persistence redirected to sandbox; verified; blocked so far=" + _blockedCount);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("SAVE_GUARD arm exception", ex);
                why = ex.Message;
                return false;
            }
        }

        static bool ReinitializePrefs(out string why)
        {
            why = null;
            var t = AccessTools.TypeByName("InitializePrefs");
            var m = t != null ? AccessTools.Method(t, "Reinitialize") : null;
            if (m == null) { why = "InitializePrefs.Reinitialize not found"; return false; }
            var inst = UnityEngine.Object.FindObjectOfType(t);   // once, at arming time (main menu)
            if (inst != null) { m.Invoke(inst, null); return true; }
            // No instance in the scene: the method never reads 'this' (IL verified), call it with a null receiver.
            var dm = new DynamicMethod("TS2MP_Reinit", typeof(void), Type.EmptyTypes, typeof(SaveGuard), true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Call, m); il.Emit(OpCodes.Ret);
            ((Action)dm.CreateDelegate(typeof(Action)))();
            return true;
        }

        // W4.2: purely local preferences of this player (CampaignPolicy LocalPreference: TPP) read from the real save that FBPP
        // still holds in memory (main menu, before the re-initialisation). Read-only; never progression.
        internal static readonly CampaignData LocalPreferences = new CampaignData();
        static void CaptureLocalPreferences()
        {
            try
            {
                var fbpp = AccessTools.TypeByName("FBPP");
                if (AccessTools.Field(fbpp, "_config").GetValue(null) == null) return;
                var has = AccessTools.Method(fbpp, "HasKeyForBool", new[] { typeof(string) });
                var get = AccessTools.Method(fbpp, "GetBool", new[] { typeof(string), typeof(bool) });
                if (has == null || get == null) return;
                foreach (var k in new[] { "TPP" })
                    if ((bool)has.Invoke(null, new object[] { k })) LocalPreferences.Bools[k] = (bool)get.Invoke(null, new object[] { k, false });
                _log.Info("SAVE_GUARD local preferences kept from this player's own data: " + LocalPreferences.Count + " key(s) (no progression)");
            }
            catch (Exception ex) { _log.Warn("SAVE_GUARD local preferences not read (" + ex.Message + "); game defaults apply"); }
        }

        // W4.2: the client never plays its own solo save (classification: SaveIsolation.IsSaveData, tested offline).
        static bool IsSaveData(string rel) { return SaveIsolation.IsSaveData(rel); }

        // Save files in the sandbox (copies seeded by mod versions <= 0.12.0) are moved aside once per process - never deleted -
        // so FBPP starts without a world until the host's campaign snapshot is written (CampaignBootstrap).
        static bool _retired;
        static void RetireSandboxSaves()
        {
            if (_retired || !Directory.Exists(SandboxDir)) return;
            _retired = true;
            string dst = Path.Combine(Path.Combine(Path.GetDirectoryName(SandboxDir), "client-sandbox-retired"), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            int n = 0;
            foreach (var src in Directory.GetFiles(SandboxDir, "*", SearchOption.AllDirectories))
            {
                string rel = src.Substring(SandboxDir.Length).TrimStart('\\', '/');
                if (!IsSaveData(rel)) continue;
                try { string to = Path.Combine(dst, rel); Directory.CreateDirectory(Path.GetDirectoryName(to)); File.Move(src, to); n++; }
                catch (Exception ex) { _log.Warn("SEED could not retire sandbox file " + rel + ": " + ex.Message); }
            }
            if (n > 0) _log.Info("SEED retired " + n + " old sandbox save file(s) to " + dst + " (kept, not deleted): Player 2 never plays a local world");
        }

        // Copies this player's purely local files (settings) into the sandbox - never save data (W4.2: the world comes from the host).
        static void SeedSandbox()
        {
            if (!Directory.Exists(RealDir)) { _log.Info("SEED real save folder does not exist yet; client sandbox starts empty"); return; }
            int n = 0, skipped = 0;
            foreach (var src in Directory.GetFiles(RealDir, "*", SearchOption.AllDirectories))
            {
                string rel = src.Substring(RealDir.Length).TrimStart('\\', '/');
                if (rel.StartsWith("Unity" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (rel.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsSaveData(rel)) { skipped++; continue; }
                string dst = Path.Combine(SandboxDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                try { File.Copy(src, dst, true); n++; }
                catch (Exception ex) { _log.Warn("SEED could not copy " + rel + ": " + ex.Message); }
            }
            _log.Info("SEED copied " + n + " local file(s) into the sandbox; " + skipped + " save file(s) NOT copied (the multiplayer world comes from the host)");
        }

        static bool PatchRedirects(Harmony harmony)
        {
            var patched = PathRedirect.Apply(harmony,
                new[] { Assembly.Load("Assembly-CSharp"), Assembly.Load("FileBasedPrefs") },
                AccessTools.PropertyGetter(typeof(Application), "persistentDataPath"),
                AccessTools.Method(typeof(SaveGuard), "SandboxPath"), _log);
            return patched != null;
        }

        static bool VerifyRedirect(out string why)
        {
            why = null;
            var cfgType = AccessTools.TypeByName("FBPPConfig");
            var cfg = Activator.CreateInstance(cfgType);
            string p = (string)AccessTools.Method(cfgType, "GetSaveFilePath").Invoke(cfg, null);
            if (p == null || !SaveIsolation.IsUnder(SaveIsolation.Norm(p), SandboxDir)) { why = "FBPPConfig.GetSaveFilePath() -> " + p; _log.Error("VERIFY FAILED " + why + " (not inside sandbox)"); return false; }
            _log.Info("VERIFY FBPPConfig.GetSaveFilePath() -> " + p + " (inside sandbox)");
            if (!RuntimeCheck()) { why = "active FBPP save target outside the sandbox"; return false; }
            return true;
        }

        // Returns false if the active save target is outside the sandbox. Armed processes are checked every 2 s.
        public static bool RuntimeCheck()
        {
            try
            {
                var fbpp = AccessTools.TypeByName("FBPP");
                var cfg = AccessTools.Field(fbpp, "_config").GetValue(null);
                if (cfg == null) return true; // not initialised yet; initialisation will go through the redirected Reinitialize
                string p = (string)AccessTools.Method(cfg.GetType(), "GetSaveFilePath").Invoke(cfg, null);
                if (p != null && SaveIsolation.IsUnder(SaveIsolation.Norm(p), SandboxDir)) return true;
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

        // ---- prefixes (return false = skip original); all pass through while disarmed ----
        static bool BlockCall(MethodBase __originalMethod)
        {
            if (!Armed) return true;
            Blocked(__originalMethod.DeclaringType.Name + "." + __originalMethod.Name, null);
            return false;
        }

        static bool BlockPrefs(MethodBase __originalMethod, string key)
        {
            if (!Armed) return true;
            Blocked("PlayerPrefs." + __originalMethod.Name, key);
            return false;
        }

        static bool GuardFbppWrite()
        {
            if (!Armed) return true;
            string p = null;
            try
            {
                var fbpp = AccessTools.TypeByName("FBPP");
                p = (string)AccessTools.Method(fbpp, "GetSaveFilePath").Invoke(null, null);
            }
            catch (Exception ex) { _log.Error("FBPP guard could not resolve target path; blocking", ex); }
            if (p != null && SaveIsolation.IsUnder(SaveIsolation.Norm(p), SandboxDir)) return true;
            Blocked("FBPP.WriteToSaveFile", p);
            return false;
        }

        static bool GuardFile(MethodBase __originalMethod, object[] __args)
        {
            if (!Armed) return true;
            int[] idx;
            if (!_filePathArgs.TryGetValue(__originalMethod, out idx)) return true;
            foreach (var i in idx)
            {
                var p = i < __args.Length ? __args[i] as string : null;
                if (p == null) continue;
                string full;
                try { full = SaveIsolation.Norm(p); } catch { continue; }
                if (SaveIsolation.IsProtectedWrite(full, RealDir, GameDir))
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
    }
}
