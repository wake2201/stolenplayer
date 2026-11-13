// N1.2: Unity-free path rules of the client save isolation (used by src/Plugin/SaveGuard.cs, tested offline).
using System;
using System.IO;

namespace TS2MP.Shared
{
    public static class SaveIsolation
    {
        public static string Norm(string p) { return Path.GetFullPath(p).TrimEnd('\\', '/'); }

        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            return path.Equals(root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        // The sandbox must be a separate folder: not inside the real save folder or the game folder, and not containing them.
        public static string LayoutError(string sandbox, string real, string game)
        {
            if (string.IsNullOrEmpty(sandbox) || string.IsNullOrEmpty(real) || string.IsNullOrEmpty(game)) return "path missing";
            if (IsUnder(sandbox, real) || IsUnder(real, sandbox)) return "sandbox overlaps the real save folder";
            if (IsUnder(sandbox, game) || IsUnder(game, sandbox)) return "sandbox overlaps the game folder";
            return null;
        }

        // Guarded file write while armed: the real save folder and the game folder (except BepInEx\) are protected.
        public static bool IsProtectedWrite(string fullPath, string real, string game)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            if (IsUnder(fullPath, real)) return true;
            return IsUnder(fullPath, game) && !IsUnder(fullPath, Path.Combine(game, "BepInEx"));
        }

        // W4.2: save data in a save folder (relative path): FBPP profiles saveData*.thief, MainMenu.BackupSave saveDataBackup.thief,
        // GameManager.SaveFileInfo saveInfo*.txt, FBPP.BackupSaves backups\. Never seeded into the client sandbox: Player 2's world
        // comes from the host campaign; only local files (settings) are copied.
        public static bool IsSaveData(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return false;
            string r = rel.Replace('/', '\\').TrimStart('\\');
            if (r.StartsWith("backups\\", StringComparison.OrdinalIgnoreCase)) return true;
            string name = Path.GetFileName(r);
            return name.StartsWith("saveData", StringComparison.OrdinalIgnoreCase) || name.StartsWith("saveInfo", StringComparison.OrdinalIgnoreCase);
        }

        // W4.2B.1 developer test mode: every TS2MP data file of a developer test lives in a dedicated "devtest" sub-folder of
        // the given data folder (idempotent), so production records (p2inv / p2spawn / fleet / client sandbox) are unreachable.
        public const string DevTestFolder = "devtest";
        public static string DevTestDataDir(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) return null;
            string trimmed = dataDir.TrimEnd('\\', '/');
            return string.Equals(Path.GetFileName(trimmed), DevTestFolder, StringComparison.OrdinalIgnoreCase) ? trimmed : Path.Combine(trimmed, DevTestFolder);
        }

        // W4.2B.2 F8 multiplayer TEST campaign: host save copy, test Player-2 records and the client's test sandbox live in a
        // dedicated "testcampaign" sub-folder (separate from normal multiplayer records and from the launcher's "devtest").
        public const string TestCampaignFolder = "testcampaign";
        public static string TestCampaignDataDir(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) return null;
            string trimmed = dataDir.TrimEnd('\\', '/');
            return string.Equals(Path.GetFileName(trimmed), TestCampaignFolder, StringComparison.OrdinalIgnoreCase) ? trimmed : Path.Combine(trimmed, TestCampaignFolder);
        }

        // Path the game's persistentDataPath callers see: the original while disarmed (solo / host), the sandbox while armed.
        public static string Effective(bool armed, string sandbox, Func<string> original) { return armed ? sandbox : original(); }
    }
}
