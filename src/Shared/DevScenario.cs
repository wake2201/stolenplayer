// W6A.3: developer story-test scenario (F10) - planning and verification rules (pure, Unity-free). Unity side:
// src/Plugin/MultiplayerMenu.Dev.cs (StartDevCampaign).
//
// Root causes of the failed W6A.1 / W6A.2 story test (in-game logs 2026-11-17):
//   1. The story step was written into a COPY of the genuine save. The user's genuine save is a free-mode save (FreeMode = 1):
//      StoryManager.Start sets isFreeMode, InitiateStory forces index 80 and AdvanceStory does nothing (IL, STORY_SYSTEM §1), so
//      no story step can be tested on it. The refusal then released the copy (redirect back to the genuine folder) and left the
//      original main menu: the user's next click loaded / started a genuine campaign at the first mission.
//   2. The host listener is created only after the preparation succeeded; the refusal came first, so Player 2 got
//      ConnectionFailed (nothing listening on the port).
//
// Story seed (mode StorySeed, every supported index): a dedicated isolated folder <data>\testcampaign\story (records) with the
// save sandbox <...>\story\host-save. It is never seeded from the genuine save. A previous story folder is moved aside
// (<story>.old-<time>, never deleted) so every start is clean. After the redirect is armed and verified and the host session
// listens, the ORIGINAL MainMenu.StartNewGame runs with skipIntro = true inside the sandbox (IL 1.27c): SPrefs.DeleteAll;
// CurrentLocation = 99; Story = 3; Week = 1; TPP; Money; GasAmount; Debt = 500000; GasTankLeftCar; RentTimer; tool life;
// GameTime; NextLevelRep; HardMode (PlayerPrefs, blocked by the armed guard); FadeLoad; MainDifficulty; SPrefs.Save;
// LoadNewSceneAsync("MainScene"). Before the scene's StoryManager.BeginStory reads it, Story / StoryBackup are set to the
// selected index (FreeMode stays 0: DeleteAll) - the step's own start effects then run once through InitiateStory, exactly as
// when the original loads a save at that index. This is a NEW-GAME BASELINE at that index, not an authentic checkpoint:
// results of earlier steps (items, completed tasks, debt deductions, counters) are absent (StoryCatalog states it per step).
// Mode SaveCopy (no index selected): the W4.2B.2 copy of the genuine save, loaded through the original Continue.
using System;
using System.IO;

namespace TS2MP.Shared
{
    public enum DevMode : byte { StorySeed, SaveCopy }

    public sealed class DevPlan
    {
        public DevMode Mode; public int Story = -1; public string DataDir, MoveAsideTo; public bool SeedFromReal;
    }

    public static class DevScenario
    {
        public const string StoryFolder = "story";
        public const int SeedLocation = 99;   // MainMenu.StartNewGame IL_001a..IL_0021 (skip intro: Madison Street)

        public static string StoryDataDir(string dataDir)
        {
            var t = SaveIsolation.TestCampaignDataDir(dataDir);
            return t != null ? Path.Combine(t, StoryFolder) : null;
        }

        // null = plan ok; else the German reason shown in F10 (nothing has been changed yet).
        public static string Plan(int story, string dataDir, bool storyDirExists, string realSaveDir, string gameDir, string stamp, out DevPlan plan)
        {
            plan = null;
            if (string.IsNullOrEmpty(dataDir)) return "Kein Datenordner.";
            if (story < 0)
            {
                plan = new DevPlan { Mode = DevMode.SaveCopy, DataDir = SaveIsolation.TestCampaignDataDir(dataDir), SeedFromReal = true };
                return null;
            }
            string r = StoryCatalog.Refusal(story);
            if (r != null) return r;
            var p = new DevPlan { Mode = DevMode.StorySeed, Story = story, DataDir = StoryDataDir(dataDir), SeedFromReal = false };
            if (storyDirExists)
            {
                string why;
                p.MoveAsideTo = AsideTarget(p.DataDir, realSaveDir, gameDir, stamp, out why);
                if (p.MoveAsideTo == null) return "Alter Story-Testordner kann nicht beiseitegelegt werden: " + why;
            }
            plan = p; return null;
        }

        // <data>\testcampaign\story -> <data>\testcampaign\story.old-<stamp>; only that folder, never inside real / game folders.
        public static string AsideTarget(string storyDir, string realSaveDir, string gameDir, string stamp, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(storyDir) || string.IsNullOrEmpty(stamp)) { why = "kein Ordner"; return null; }
            string s = SaveIsolation.Norm(storyDir);
            if (!string.Equals(Path.GetFileName(s), StoryFolder, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(Path.GetDirectoryName(s)), SaveIsolation.TestCampaignFolder, StringComparison.OrdinalIgnoreCase)) { why = "nicht der Story-Testordner: " + s; return null; }
            if ((!string.IsNullOrEmpty(realSaveDir) && (SaveIsolation.IsUnder(s, SaveIsolation.Norm(realSaveDir)) || SaveIsolation.IsUnder(SaveIsolation.Norm(realSaveDir), s)))
                || (!string.IsNullOrEmpty(gameDir) && (SaveIsolation.IsUnder(s, SaveIsolation.Norm(gameDir)) || SaveIsolation.IsUnder(SaveIsolation.Norm(gameDir), s)))) { why = "überschneidet echten Spielstand- oder Spielordner"; return null; }
            for (int i = 0; i < stamp.Length; i++) if (!char.IsLetterOrDigit(stamp[i]) && stamp[i] != '-') { why = "ungültiger Zeitstempel"; return null; }
            return s + ".old-" + stamp;
        }

        // Prefs of the seed right before the scene loads (after the original StartNewGame and the index write).
        public static string VerifySeedPrefs(int expectedStory, int story, int storyBackup, int freeMode, int currentLocation)
        {
            if (freeMode != 0) return "FreeMode = " + freeMode + " im Story-Testspielstand (erwartet 0).";
            if (story != expectedStory || storyBackup != expectedStory) return "Story = " + story + " / StoryBackup = " + storyBackup + " (erwartet " + expectedStory + ").";
            if (currentLocation != SeedLocation) return "CurrentLocation = " + currentLocation + " (erwartet " + SeedLocation + ", Madison Street).";
            return null;
        }

        // Host world after the load: the live StoryManager must be on the selected index, not in free mode.
        public static string VerifyLoaded(int expected, int liveIndex, string liveKey, bool liveFreeMode)
        {
            var s = StoryCatalog.Get(expected);
            if (liveFreeMode) return "Die geladene Welt ist im Freien Modus (Schritt 80) – Story-Test ungültig.";
            if (liveIndex != expected) return "Die geladene Welt steht auf Index " + liveIndex + " ('" + liveKey + "'), erwartet " + expected + ".";
            if (s != null && !string.Equals(liveKey, s.Key, StringComparison.Ordinal)) return "Stufe '" + liveKey + "' passt nicht zu Index " + expected + " ('" + s.Key + "').";
            return null;
        }

        // Index and original objective together: "Index 18 · story14 · Disable the 104 entrance camera with your drone".
        public static string Label(int index)
        {
            var s = StoryCatalog.Get(index);
            return s == null ? "Index " + index : "Index " + index + " · " + s.Key + " · " + s.Text;
        }
    }
}
