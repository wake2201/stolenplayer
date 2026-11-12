// N1 installer: Install / Update / Repair / Uninstall of BepInEx 5.4.23.5 + the TS2MP plugin from the release package.
// Rules: every payload file is hash-verified against manifest.sha256 before anything in the game folder changes; only
// mod-owned files are written or removed (ownership is recorded in BepInEx\plugins\TS2MP\install-record.txt); every
// file that is replaced or removed is first copied (and hash-checked) into <DataDir>\backups\install-<stamp>\.
// Original game files and saves are never touched. C# 5, no dependencies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TS2MP.Launcher
{
    sealed class PayloadFile
    {
        public string PackageRel;   // path inside the package (payload\...)
        public string GameRel;      // destination relative to the game folder
        public string Sha256;       // upper-case hex
        public bool IsBepInEx;      // false = plugin file
    }

    sealed class Payload
    {
        public const string ManifestName = "manifest.sha256";
        public string Root, Version;
        public readonly List<PayloadFile> Files = new List<PayloadFile>();
        public readonly Dictionary<string, string> AllHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // every manifest entry

        public PayloadFile Plugin { get { return Files.FirstOrDefault(f => f.GameRel.Equals(Installer.PluginRel, StringComparison.OrdinalIgnoreCase)); } }

        // Null + error when the package is missing or damaged.
        public static Payload Load(string root, out string error)
        {
            error = null;
            string man = Path.Combine(root, ManifestName);
            if (!File.Exists(man)) { error = "no release package next to the launcher (" + ManifestName + " missing in " + root + ")"; return null; }
            var p = new Payload { Root = root };
            foreach (var raw in File.ReadAllLines(man))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#"))
                {
                    int v = line.IndexOf("version=", StringComparison.Ordinal);
                    if (v >= 0) p.Version = line.Substring(v + 8).Split(' ')[0];
                    continue;
                }
                // sha256sum format: "<64 hex>  <relative path with forward slashes>"
                if (line.Length < 67 || line[64] != ' ') { error = "malformed manifest line: " + line; return null; }
                string hash = line.Substring(0, 64).ToUpperInvariant(), rel = line.Substring(66).Replace('/', '\\');
                if (rel.Contains("..") || Path.IsPathRooted(rel)) { error = "unsafe path in manifest: " + rel; return null; }
                p.AllHashes[rel] = hash;
                if (rel.StartsWith(@"payload\bepinex\", StringComparison.OrdinalIgnoreCase))
                    p.Files.Add(new PayloadFile { PackageRel = rel, GameRel = rel.Substring(16), Sha256 = hash, IsBepInEx = true });
                else if (rel.StartsWith(@"payload\plugin\", StringComparison.OrdinalIgnoreCase))
                    p.Files.Add(new PayloadFile { PackageRel = rel, GameRel = Path.Combine(Installer.PluginDir, rel.Substring(15)), Sha256 = hash, IsBepInEx = false });
            }
            if (p.Plugin == null || !p.Files.Any(f => f.GameRel.Equals(@"BepInEx\core\BepInEx.dll", StringComparison.OrdinalIgnoreCase)) || !p.Files.Any(f => f.GameRel.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase)))
            { error = "manifest lacks the plugin or BepInEx core files"; return null; }
            return p;
        }

        // Integrity check of the package itself: every manifest entry exists with the recorded hash, and no extra
        // unlisted files sit in payload\. Must pass before any game file is changed.
        public bool Verify(out string error)
        {
            foreach (var kv in AllHashes)
            {
                string f = Path.Combine(Root, kv.Key);
                if (!File.Exists(f)) { error = "package file missing: " + kv.Key; return false; }
                if (Installer.Sha256(f) != kv.Value) { error = "package file damaged (hash mismatch): " + kv.Key; return false; }
            }
            string pd = Path.Combine(Root, "payload");
            if (Directory.Exists(pd))
                foreach (var f in Directory.GetFiles(pd, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(Root.TrimEnd('\\').Length + 1);
                    if (!AllHashes.ContainsKey(rel)) { error = "unlisted file in the package payload: " + rel; return false; }
                }
            error = null;
            return true;
        }
    }

    enum InstallState { NotInstalled, Installed, NeedsUpdate, Damaged, ForeignBepInEx }

    sealed class InstallRecord
    {
        public string Version = "";
        public bool BepInExOwned;
        public readonly Dictionary<string, string> Owned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // game-relative -> sha

        public static InstallRecord Load(string game)
        {
            string f = Path.Combine(game, Installer.RecordRel);
            if (!File.Exists(f)) return null;
            var r = new InstallRecord();
            foreach (var raw in File.ReadAllLines(f))
            {
                string line = raw.Trim();
                if (line.StartsWith("version=")) r.Version = line.Substring(8);
                else if (line == "bepinex=owned") r.BepInExOwned = true;
                else if (line.StartsWith("owned "))
                {
                    var parts = line.Split(new[] { ' ' }, 3);
                    if (parts.Length == 3 && parts[1].Length == 64 && !parts[2].Contains("..") && !Path.IsPathRooted(parts[2])) r.Owned[parts[2]] = parts[1].ToUpperInvariant();
                }
            }
            return r;
        }

        public void Save(string game)
        {
            string f = Path.Combine(game, Installer.RecordRel);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            var lines = new List<string> { "# TS2-Multiplayer install record: files listed as 'owned' were written by the launcher and are removed by Uninstall.", "version=" + Version, "bepinex=" + (BepInExOwned ? "owned" : "preexisting") };
            foreach (var kv in Owned.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) lines.Add("owned " + kv.Value + " " + kv.Key);
            string tmp = f + ".tmp";
            File.WriteAllLines(tmp, lines);
            if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f);
        }
    }

    static class Installer
    {
        public const string PluginDir = @"BepInEx\plugins\TS2MP";
        public const string PluginRel = @"BepInEx\plugins\TS2MP\TS2MP.Plugin.dll";
        public const string RecordRel = @"BepInEx\plugins\TS2MP\install-record.txt";
        public const string GameExe = "Thief Simulator 2.exe";
        // Created at runtime by BepInEx / the plugin; removed with an owned BepInEx (configs are backed up first).
        static readonly string[] BepInExRuntimeFiles = { @"BepInEx\LogOutput.log", @"BepInEx\config\BepInEx.cfg" };
        const string PluginConfigRel = @"BepInEx\config\com.ts2mp.multiplayer.cfg";

        public static Action<string> Out = Console.WriteLine;
        public static Action<string> Log = delegate { };

        public static string Sha256(string f)
        {
            using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
        }

        public static bool GameRunning(string game)
        {
            return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe)).Length > 0;
        }

        public static InstallState Status(string game, Payload p, out string detail)
        {
            var rec = InstallRecord.Load(game);
            int missing = 0, differ = 0, pluginDiffer = 0, bepDiffer = 0;
            foreach (var f in p.Files)
            {
                string d = Path.Combine(game, f.GameRel);
                if (!File.Exists(d)) { missing++; continue; }
                if (Sha256(d) != f.Sha256)
                {
                    differ++;
                    if (f.IsBepInEx) bepDiffer++; else pluginDiffer++;
                }
            }
            bool bepPresent = File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.dll"));
            bool pluginPresent = File.Exists(Path.Combine(game, PluginRel));
            if (!bepPresent && !pluginPresent) { detail = "not installed"; return InstallState.NotInstalled; }
            if (bepDiffer > 0 && (rec == null || !rec.BepInExOwned)) { detail = "a different BepInEx build is installed (" + bepDiffer + " file(s) differ from BepInEx 5.4.23.5)"; return InstallState.ForeignBepInEx; }
            if (missing == 0 && differ == 0) { detail = "installed, version " + (p.Version ?? "?") + ", all " + p.Files.Count + " files verified"; return InstallState.Installed; }
            if (missing == 0 && differ == pluginDiffer && pluginPresent) { detail = "plugin differs from this package (update available or local build)"; return InstallState.NeedsUpdate; }
            detail = missing + " file(s) missing, " + differ + " file(s) changed";
            return InstallState.Damaged;
        }

        // Install, Update and Repair all bring the mod-owned files to the package state; the verb only changes wording.
        public static bool Apply(string game, Payload p, string backupRoot, string verb, out string error)
        {
            error = null;
            if (!File.Exists(Path.Combine(game, GameExe))) { error = "game not found in " + game; return false; }
            if (GameRunning(game)) { error = "Thief Simulator 2 is running. Close every game instance first."; return false; }
            if (!p.Verify(out error)) { error = "release package check failed, nothing changed: " + error; return false; }

            var rec = InstallRecord.Load(game) ?? new InstallRecord();
            bool bepPresent = File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.dll"));
            if (!bepPresent && !rec.BepInExOwned) rec.BepInExOwned = true;   // fresh BepInEx: ours

            // Plan before touching anything.
            var copy = new List<PayloadFile>(); var replace = new List<PayloadFile>();
            foreach (var f in p.Files)
            {
                string d = Path.Combine(game, f.GameRel);
                if (!File.Exists(d)) { copy.Add(f); continue; }
                if (Sha256(d) == f.Sha256)
                {
                    // Unchanged. Keep earlier ownership; an identical plugin file is ours by location.
                    if (!f.IsBepInEx || rec.BepInExOwned) rec.Owned[f.GameRel] = f.Sha256;
                    continue;
                }
                bool owned = !f.IsBepInEx || rec.BepInExOwned || rec.Owned.ContainsKey(f.GameRel);
                if (!owned)
                {
                    error = "a different BepInEx build is installed (" + f.GameRel + " differs from BepInEx 5.4.23.5). Nothing was changed. "
                        + "Remove that BepInEx (or ask its mod author) and run Install again.";
                    return false;
                }
                replace.Add(f);
            }
            if (copy.Count == 0 && replace.Count == 0 && File.Exists(Path.Combine(game, RecordRel)))
            {
                Out("  " + verb + ": all " + p.Files.Count + " files already match version " + p.Version + ". Nothing to do.");
                rec.Version = p.Version ?? ""; rec.Save(game);
                return true;
            }

            string backupDir = null;
            try
            {
                if (replace.Count > 0)
                {
                    backupDir = NewBackupDir(backupRoot, "install");
                    foreach (var f in replace) Backup(game, f.GameRel, backupDir);
                    Out("  Backup   : " + replace.Count + " file(s) to be replaced -> " + backupDir);
                }
                foreach (var f in copy.Concat(replace))
                {
                    PlaceVerified(Path.Combine(p.Root, f.PackageRel), Path.Combine(game, f.GameRel), f.Sha256);
                    rec.Owned[f.GameRel] = f.Sha256;
                    Log("install: " + f.GameRel + " " + f.Sha256);
                }
                Directory.CreateDirectory(Path.Combine(game, @"BepInEx\plugins"));
                rec.Version = p.Version ?? "";
                rec.Save(game);
            }
            catch (Exception ex)
            {
                error = verb + " failed: " + ex.Message + (backupDir != null ? " (replaced files are backed up in " + backupDir + ")" : "") + ". Run Repair after fixing the cause.";
                return false;
            }
            Out("  " + verb + ": " + copy.Count + " file(s) added, " + replace.Count + " replaced, all hash-verified. BepInEx " + (rec.BepInExOwned ? "installed by the launcher" : "pre-existing (left as it was)") + ".");
            return true;
        }

        public static bool Uninstall(string game, Payload p, string backupRoot, out string error)
        {
            error = null;
            if (GameRunning(game)) { error = "Thief Simulator 2 is running. Close every game instance first."; return false; }
            var rec = InstallRecord.Load(game);
            var remove = new List<string>(); var keptModified = new List<string>();
            if (rec != null)
            {
                foreach (var kv in rec.Owned)
                {
                    string d = Path.Combine(game, kv.Key);
                    if (!File.Exists(d)) continue;
                    if (Sha256(d) == kv.Value) remove.Add(kv.Key); else keptModified.Add(kv.Key);
                }
            }
            else
            {
                // No record (developer deploy): only the plugin folder files that match a known payload file are ours.
                if (p != null)
                    foreach (var f in p.Files.Where(x => !x.IsBepInEx))
                        if (File.Exists(Path.Combine(game, f.GameRel))) remove.Add(f.GameRel);
                if (p == null)
                    foreach (var n in new[] { "TS2MP.Plugin.dll", "LiteNetLib.dll" })
                        if (File.Exists(Path.Combine(game, PluginDir, n))) remove.Add(Path.Combine(PluginDir, n));
            }
            bool bepOwned = rec != null && rec.BepInExOwned;
            var extra = new List<string>();
            if (File.Exists(Path.Combine(game, PluginConfigRel))) extra.Add(PluginConfigRel);
            if (bepOwned) foreach (var f in BepInExRuntimeFiles) if (File.Exists(Path.Combine(game, f))) extra.Add(f);
            if (bepOwned && Directory.Exists(Path.Combine(game, @"BepInEx\cache")))
                foreach (var f in Directory.GetFiles(Path.Combine(game, @"BepInEx\cache"), "*", SearchOption.AllDirectories)) extra.Add(f.Substring(game.TrimEnd('\\').Length + 1));

            if (remove.Count == 0 && extra.Count == 0 && rec == null) { Out("  Uninstall: nothing of TS2-Multiplayer is installed in " + game + "."); return true; }
            string backupDir;
            try
            {
                backupDir = NewBackupDir(backupRoot, "uninstall");
                foreach (var rel in remove.Concat(extra)) Backup(game, rel, backupDir);
                if (rec != null) Backup(game, RecordRel, backupDir);
                foreach (var rel in remove.Concat(extra)) { File.Delete(Path.Combine(game, rel)); Log("uninstall: removed " + rel); }
                if (rec != null) File.Delete(Path.Combine(game, RecordRel));
                RemoveEmptyDirs(Path.Combine(game, PluginDir));
                if (bepOwned) RemoveEmptyDirs(Path.Combine(game, "BepInEx"));
            }
            catch (Exception ex) { error = "uninstall failed: " + ex.Message; return false; }
            Out("  Uninstall: " + (remove.Count + extra.Count) + " file(s) removed (backup: " + backupDir + ").");
            if (keptModified.Count > 0) Out("  Kept " + keptModified.Count + " file(s) changed since installation: " + string.Join(", ", keptModified));
            if (!bepOwned && File.Exists(Path.Combine(game, @"BepInEx\core\BepInEx.dll")))
                Out("  BepInEx was already there before the launcher installed the mod, so it was left in place.");
            return true;
        }

        static string NewBackupDir(string root, string kind)
        {
            string d = Path.Combine(root, kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            for (int i = 2; Directory.Exists(d); i++) d = Path.Combine(root, kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + i);
            Directory.CreateDirectory(d);
            return d;
        }

        static void Backup(string game, string rel, string backupDir)
        {
            string src = Path.Combine(game, rel), dst = Path.Combine(backupDir, "game", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, false);
            if (Sha256(src) != Sha256(dst)) throw new IOException("backup hash mismatch: " + rel);
        }

        // Copy to a temporary name, verify, then swap in: a failed copy never leaves a half-written file in place.
        static void PlaceVerified(string src, string dst, string sha)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            string tmp = dst + ".ts2mp-new";
            if (File.Exists(tmp)) File.Delete(tmp);
            File.Copy(src, tmp, false);
            if (Sha256(tmp) != sha) { File.Delete(tmp); throw new IOException("hash mismatch after copying " + Path.GetFileName(dst)); }
            if (File.Exists(dst)) File.Replace(tmp, dst, null); else File.Move(tmp, dst);
        }

        static void RemoveEmptyDirs(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var sub in Directory.GetDirectories(dir)) RemoveEmptyDirs(sub);
            if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
        }
    }
}
