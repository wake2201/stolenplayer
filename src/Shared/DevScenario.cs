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

    // W6A.4: one verified story-test prerequisite = the ORIGINAL persisted state an authentic save has after the step that
    // produces it. Applied only to a fresh story seed (before the scene loads, through the original load path) and to Player 2's
    // fresh host-owned record - exactly once per scenario, never to genuine saves.
    public sealed class ScenarioPrereq
    {
        public readonly string Id, Evidence; public readonly int FromIndex;
        public readonly string[] SeedKeys; public readonly int[] SeedValues;   // SPrefs ints written into the seed
        public readonly int P2ItemId, P2ItemCount;                            // Player 2's own copy (host-owned record), 0 = none
        public readonly int EquippedDroneId;                                  // world check: DroneManager.assigned_drone_script.item_ID
        public ScenarioPrereq(string id, int fromIndex, string[] keys, int[] values, int p2Item, int p2Count, int drone, string evidence)
        { Id = id; FromIndex = fromIndex; SeedKeys = keys; SeedValues = values; P2ItemId = p2Item; P2ItemCount = p2Count; EquippedDroneId = drone; Evidence = evidence; }
    }

    public static class ScenarioPrereqs
    {
        public const int FlybyDrone = 89;   // ITEM IDs/89_flyby_drone ("Flyby Lite' Drone", isDrone + isTool)

        public static readonly ScenarioPrereq[] All =
        {
            // story11 (index 15) "Break into 103 and steal the drone": STEAL DRONE 103 MISSION/enable/flyby drone pickup (item 89,
            // advance_story_if_picked). Persisted by ItemID.Save_Item_Amount (Item_89) and SaveGame.SaveGameNewSystem IL_03c8..IL_03fa
            // (Drone = assigned_drone_script.item_ID); restored by ItemID.Start + DroneManager.<FindDrone> IL_003e..IL_0095
            // (SPrefs Drone -> PS.item_IDs entry with item_amount > 0 -> original AssignDrone).
            new ScenarioPrereq("flyby_drone", 16, new[] { "Item_89", "Drone" }, new[] { 1, FlybyDrone }, FlybyDrone, 1, FlybyDrone,
                "index 15 story11 pickup; Item_89 + Drone keys; DroneManager.<FindDrone> -> AssignDrone"),
            // story12 (index 16) "buy a 'Camera disable module' on Dronetech": MainScene DroneShopScript upgrade flyby_upgrade3 =
            // camera_module (camera_upgrade true, advanceStory story12; decoded from level1). DroneUpgradePanel.BuyUpgrade writes
            // SPrefs flyby_upgrade3 = 1; DroneManager.ApplyUpgrades IL_0013..IL_002f applies it in AssignDrone. World key: Player 2
            // receives it with the host campaign (CampaignPolicy World).
            new ScenarioPrereq("drone_camera_module", 17, new[] { "flyby_upgrade3" }, new[] { 1 }, 0, 0, 0,
                "index 16 story12 purchase; flyby_upgrade3 (camera_module); DroneManager.ApplyUpgrades"),
        };

        public static ScenarioPrereq[] For(int index)
        {
            var l = new System.Collections.Generic.List<ScenarioPrereq>();
            if (index < 0) return l.ToArray();
            foreach (var p in All) if (index >= p.FromIndex) l.Add(p);
            return l.ToArray();
        }

        // W6A.5: Player 2's developer grants for a FRESH isolated record (Player2Sync.LoadBook, new-record branch only). Pure rule:
        // only a developer story test (scenario >= 0, isolated developer host), only items the record does not yet hold in the
        // required count, applied with Set (absolute, never Add). A reconnect / late join loads the existing record and never
        // reaches this rule; a second evaluation of the same record yields nothing -> no duplicate drone.
        public static System.Collections.Generic.List<ScenarioPrereq> P2Grants(int scenario, bool devHost, Func<int, int> countOf)
        {
            var l = new System.Collections.Generic.List<ScenarioPrereq>();
            if (scenario < 0 || !devHost || countOf == null) return l;
            foreach (var p in For(scenario)) if (p.P2ItemId != 0 && countOf(p.P2ItemId) < p.P2ItemCount) l.Add(p);
            return l;
        }
    }

    // W6A.5: an original action that completes a scenario's story step. DIAGNOSTIC ONLY - never part of the session-readiness
    // gate (a missing optional alternative must not block joins). Primary = the step's live objective; StaticUnavailable = the
    // original asset itself never lets the action appear (scene evidence), so it is reported as unavailable, never as testable.
    public sealed class ScenarioAction
    {
        public readonly string Id, FindPath, StaticUnavailable, Evidence; public readonly bool Primary;
        public ScenarioAction(string id, bool primary, string findPath, string staticUnavailable, string evidence)
        { Id = id; Primary = primary; FindPath = findPath; StaticUnavailable = staticUnavailable; Evidence = evidence; }
    }

    public static class ScenarioActions
    {
        // MadisonSt (level3) asset reads 2026-11-17 (tools/research/unity_reader.py, game 1.27c):
        //   DOMY/Dom 103/Interior_103                          activeSelf False (AssignInteriors / OnPossesion: active only while the
        //                                                      local player stands in House 103's info trigger, on foot)
        //   .../Interior_103/story object 103 notka            activeSelf False - the ONLY story-keyed StoryObject serialized
        //                                                      inactive; its obj_to_activate is the child pPlane3 (the ReadNote), not
        //                                                      itself, and no MonoBehaviour / game code references or finds it -> its
        //                                                      StoryObject never runs and the note never appears in the original game.
        //   DISABLE CAMERA 104 MISSION (StoryObject story14) -> enable (pid 6010), scene root, not distance-culled.
        //   Junkyard/SECURITY CAM (1)/Security Camera: SecurityCamera canBeDisabledDrone, onDisableAdvanceStory, storyName story14,
        //                                              discovered_camera_prefs Camera_104_1.
        public static ScenarioAction[] For(int index)
        {
            var s = StoryCatalog.Get(index);
            if (s != null && s.Key == "story14") return new[]
            {
                new ScenarioAction("camera_104_drone", true, "Junkyard/SECURITY CAM (1)/Security Camera", null,
                    "SecurityCamera onDisableAdvanceStory story14 (drone camera disable); marker DISABLE CAMERA 104 MISSION/enable"),
                new ScenarioAction("note_103", false, null,
                    "im Originalspiel 1.27c nicht verfügbar: 'story object 103 notka' ist im Szenen-Asset deaktiviert und wird von nichts aktiviert",
                    "ReadNote advanceStory story14 under an inactive, unreferenced StoryObject holder"),
            };
            return new ScenarioAction[0];
        }

        // One status line per action (F10 + log). active: FindPath -> GameObject active in hierarchy (Unity side).
        public static string Describe(ScenarioAction a, bool active)
        {
            if (a.StaticUnavailable != null) return a.Id + ": " + a.StaticUnavailable;
            return a.Id + (a.Primary ? " (Ziel)" : "") + ": " + (active ? "verfügbar" : "Objekt nicht aktiv (" + a.FindPath + ")");
        }
    }

    // W6A.5: the ONE host readiness state. Network join decisions, HOST_READINESS log, F8 chip and F10 status all read it.
    //   world  = HostReadiness.Check (loaded world, persistence bound); null = ready
    //   scenario state (developer story test only): None / Waiting / Checking / Valid / Invalid
    public enum ScenarioState : byte { None, Waiting, Checking, Valid, Invalid }

    public static class JoinGate
    {
        // null = Player 2 may join now; else the reason joins are refused.
        public static string Reason(string world, ScenarioState s, string scenarioDetail)
        {
            if (s == ScenarioState.Invalid) return "Story-Test ungültig: " + scenarioDetail;   // permanent, even while the world reloads
            if (world != null) return world;
            if (s == ScenarioState.Waiting) return "Story-Test wartet auf die geladene Welt";
            if (s == ScenarioState.Checking) return "Story-Test wird geprüft" + (scenarioDetail != null ? ": " + scenarioDetail : "");
            return null;
        }

        // Permanent refusal (the client stops retrying): only an invalid scenario. Everything else is retryable HostNotReady.
        public static bool Fatal(ScenarioState s) { return s == ScenarioState.Invalid; }

        public static RejectCode? Reject(string reason, bool fatal) { return reason == null ? (RejectCode?)null : fatal ? RejectCode.ScenarioInvalid : RejectCode.HostNotReady; }

        public static string LogLine(string reason, bool fatal) { return reason == null ? "ready: Player 2 may join" : (fatal ? "closed permanently: " : "not ready: ") + reason; }
    }

    // Live facts the host gathers for the scenario check (Unity side: MultiplayerMenu.Dev.cs).
    public sealed class ScenarioFacts
    {
        public int Expected, LiveIndex, Region; public string LiveKey; public bool FreeMode;
        public int EquippedDrone; public System.Collections.Generic.Dictionary<string, int> Prefs = new System.Collections.Generic.Dictionary<string, int>();
        public System.Collections.Generic.Dictionary<int, int> P2Items = new System.Collections.Generic.Dictionary<int, int>(); public bool P2RecordKnown;
        public bool SpawnOk; public string SpawnNote;
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
        // W6A.4: seed prefs incl. prerequisites (get = SPrefs.GetInt of the seed).
        public static string VerifySeed(int expectedStory, Func<string, int> get)
        {
            string bad = VerifySeedPrefs(expectedStory, get("Story"), get("StoryBackup"), get("FreeMode"), get("CurrentLocation"));
            if (bad != null) return bad;
            foreach (var p in ScenarioPrereqs.For(expectedStory))
                for (int i = 0; i < p.SeedKeys.Length; i++)
                    if (get(p.SeedKeys[i]) != p.SeedValues[i]) return "Voraussetzung " + p.Id + ": " + p.SeedKeys[i] + " = " + get(p.SeedKeys[i]) + " (erwartet " + p.SeedValues[i] + ").";
            return null;
        }

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

        // Seed keys of the scenario (story index + prerequisites), applied after the original StartNewGame, before the load.
        public static void SeedKeys(int index, System.Collections.Generic.List<string> keys, System.Collections.Generic.List<int> values)
        {
            keys.Clear(); values.Clear();
            keys.Add("Story"); values.Add(index); keys.Add("StoryBackup"); values.Add(index);
            foreach (var p in ScenarioPrereqs.For(index)) for (int i = 0; i < p.SeedKeys.Length; i++) { keys.Add(p.SeedKeys[i]); values.Add(p.SeedValues[i]); }
        }

        // W6A.4 full scenario check (host world). null = valid; else the first failing requirement. Pending (retry) vs final is the
        // caller's timeout decision: a check is never reported OK while any line fails.
        public static string Check(ScenarioFacts f)
        {
            string bad = VerifyLoaded(f.Expected, f.LiveIndex, f.LiveKey, f.FreeMode);
            if (bad != null) return bad;
            if (f.Region != SeedLocation) return "Host ist in " + Regions.Name(f.Region) + ", Testregion ist " + Regions.Name(SeedLocation) + ".";
            foreach (var p in ScenarioPrereqs.For(f.Expected))
            {
                for (int i = 0; i < p.SeedKeys.Length; i++)
                {
                    if (p.SeedKeys[i] == "Drone") continue;   // persisted only by the next save; the live assignment is checked below
                    int v; if (!f.Prefs.TryGetValue(p.SeedKeys[i], out v) || v != p.SeedValues[i]) return "Voraussetzung " + p.Id + ": " + p.SeedKeys[i] + " = " + (f.Prefs.ContainsKey(p.SeedKeys[i]) ? v.ToString() : "?") + " (erwartet " + p.SeedValues[i] + ").";
                }
                if (p.EquippedDroneId != 0 && f.EquippedDrone != p.EquippedDroneId) return "Voraussetzung " + p.Id + ": Drohne " + p.EquippedDroneId + " nicht ausgerüstet (DroneManager: " + f.EquippedDrone + ").";
                if (p.P2ItemId != 0)
                {
                    if (!f.P2RecordKnown) return "Spieler-2-Datensatz noch nicht gebunden.";
                    int c; if (!f.P2Items.TryGetValue(p.P2ItemId, out c) || c < p.P2ItemCount) return "Voraussetzung " + p.Id + ": Spieler 2 hat Gegenstand " + p.P2ItemId + " nicht.";
                }
            }
            // W6A.5: story objects / actions are NOT checked here (ScenarioActions = diagnostics). The W6A.4 'story object 103
            // notka' requirement blocked every join: that holder is inactive in the original asset (and inside a distance-culled
            // interior), so the check could never pass on a valid index-18 world.
            if (!f.SpawnOk) return "Spawn für Spieler 2 ungültig: " + f.SpawnNote;
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
