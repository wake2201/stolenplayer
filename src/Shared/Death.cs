// W6C.1: multiplayer-safe death / arrest. Unity-free rules (unit-tested); the Unity side is src/Plugin/DeathSync.cs.
//
// Verified original paths (Mono.Cecil IL + MainScene `level1` button bindings, Assembly-CSharp 1.27c):
//   Health 0:   PlayerScript.Update IL_0ce3..IL_0d67 bloodSprite.fillAmount <= 0 (bleeding) -> GameManager.GotCaught(5).
//   Gas:        PlayerScript.CollapseFromGas -> caughtUp = true, GotCaught(2).
//   Tazed:      PlayerScript.GetTazed (police / guards / pedestrians) drops a carried heavy object (DropHeavy), caughtUp = true,
//               PMB / PCB off; PlayerScript.Update IL_9089..IL_911e -> after taze_max_time GotCaught(caught_reason).
//   GotCaught(r) IL_0000..IL_026c: once per GameManager (`caught`, never cleared - only a scene load resets it).
//               Hardcore: r 0 -> SelectJail, r 6 -> half money + SelectHideout (no screen). Otherwise Time.timeScale = 0,
//               CaughtObject on, MainCanvas / StoryObject alpha 0, PlayerMotorBehavior / PlayerCameraBehavior off, cursor
//               free, AudioListener 0.4, associate sent off; r 0 outside BankScene -> policeCaughtButtons, else
//               normalCaughtButtons; caughtTxt from the reason key below.
//   Caught screen buttons (persistent UnityEvent calls in level1):
//               CAUGHT /Buttons/Buttons/LOAD CHECKPOINT        -> GameManager.LoadCheckpoint
//               CAUGHT /Buttons Jail/Buttons/LOAD CHECKPOINT   -> GameManager.LoadCheckpoint
//               CAUGHT /Buttons Jail/Buttons/Go To Jail        -> GameManager.SelectJail
//               CAUGHT /Buttons*/Buttons/Quit To Menu          -> GameManager.LoadMenu
//   Other LoadCheckpoint callers: PAUSE MENU/Load save?/Yes (button) and GameManager.LoadProfile IL_009f (pause-menu save
//               slot "Load", after File.Copy of saveDataN.thief over saveData.thief).
//   LoadCheckpoint: timeScale 1, CaughtObject off, LoadCheckpointIE -> 0.5 s -> Application.LoadLevel("MainScene") with NO
//               save first: FBPP.Start reads the disk file again, so every in-memory change since the last save is dropped
//               (the shared world rolls back under a connected Player 2 - the W6C.1 defect).
//   SelectJail: CaughtObject off, crowbar 0, Reset{Player,Car}Position, CurrentLocation 93, timesCaught++, RemoveAllAlerts,
//               ItemJail0..29 = 0 (hardcore: confiscation), achievement, SaveGameNewSystem(false) FIRST, LoadWait -> LoadMain.
//   SelectHideout: Reset{Player,Car}Position, Medkit 0, CurrentLocation 0, SaveGameNewSystem(false) FIRST, timeScale 1,
//               LoadWait -> LoadMain; GameManager.Start then places the player at SpawnHideoutPlayer. The original uses it as
//               its own respawn (hardcore GotCaught r 6) and as the jail exit (PlayerScript.Update IL_54a8, bail).
// Multiplayer rule: a death / arrest never reloads the last checkpoint while a session is active. The caught screen's
// "Load checkpoint" button becomes Respawn (original SelectHideout transition: live state saved, no rollback); Go to Jail
// stays the original (it saves before loading); every other checkpoint / slot load is refused during the session.
using System;

namespace TS2MP.Shared
{
    public enum LoadSource : byte { CaughtScreen = 0, PauseMenu = 1, SaveSlot = 2 }
    public enum LoadDecision : byte { Original = 0, Respawn = 1, Refuse = 2 }
    public enum DeathChoice : byte { Respawn = 0, Jail = 1, QuitToMenu = 2 }

    public static class DeathRules
    {
        // GotCaught reason -> the original caughtTxt key (IL_01ba..IL_0267); reason 6 has no own key (hardcore money loss path,
        // normal mode shows "got_caught").
        public static string ReasonKey(int reason)
        {
            switch (reason)
            {
                case 0: return "got_caught";
                case 1: return "ran_over";
                case 2: return "collapsed_from_gas";
                case 3: return "killed_person";
                case 4: return "only_guards";
                case 5: return "you_got_shot";
                case 6: return "got_caught";
                case 7: return "car_found_police";
                default: return "reason_" + reason;
            }
        }

        // Health / gas = DEATH; every arrest-type reason = CAUGHT (log classification only).
        public static bool IsDeath(int reason) { return reason == 2 || reason == 5; }

        public static string Kind(int reason) { return IsDeath(reason) ? "DEATH" : "CAUGHT"; }

        // Which buttons the original shows (GotCaught IL_015f..IL_01ba): police set (with Go to Jail) only for reason 0 outside
        // the bank heist; hardcore hides its Load button (buttonReloadSavePolice).
        public static bool JailOffered(int reason, string mapName) { return reason == 0 && !string.Equals(mapName, "BankScene", StringComparison.Ordinal); }

        public static LoadDecision Decide(bool sessionActive, LoadSource src)
        {
            if (!sessionActive) return LoadDecision.Original;
            return src == LoadSource.CaughtScreen ? LoadDecision.Respawn : LoadDecision.Refuse;
        }

        public static string SourceName(LoadSource s)
        {
            switch (s)
            {
                case LoadSource.CaughtScreen: return "caught screen Load checkpoint";
                case LoadSource.PauseMenu: return "pause menu Load save";
                default: return "pause menu save slot Load (GameManager.LoadProfile)";
            }
        }

        // The game's own localization key for the relabelled button (resources.assets: "Continue" -> "Continue"); the
        // original label key is restored outside a session.
        public const string RespawnLabelKey = "Continue";
        public const string OriginalLabelKey = "load_checkpoint";
    }

    // Host story publish right after one of ITS OWN scene loads (respawn, jail, travel). The original reads the saved `Story`
    // key; an advance that happened between the transition's save and the scene unload (LoadWait waits 0.35 s) is not in
    // that save, so the fresh StoryManager starts one step lower and the original usually re-advances a moment later
    // (0.30.0 client log 21:37:05-09: 20 -> 19 -> 20 during a host travel). The lower index is held back for a short window
    // instead of being published; it is published if it is still lower afterwards (the host's own state stays authoritative:
    // this is never a merge of two players' saves and never a max() rule).
    public enum StoryLoadDecision : byte { Publish = 0, Hold = 1 }

    public static class StoryLoadRule
    {
        public const double Window = 5.0;

        public static StoryLoadDecision Decide(int local, bool hasCommitted, int committed, double boundAt, double now)
        {
            if (!hasCommitted || local >= committed) return StoryLoadDecision.Publish;
            return now - boundAt < Window ? StoryLoadDecision.Hold : StoryLoadDecision.Publish;
        }
    }

    // One caught episode = one GameManager instance (`caught` is only reset by a scene load). Exactly one choice per episode;
    // a failed transition (the original did not start loading) re-opens the choice.
    public sealed class DeathEpisode
    {
        public int Id { get; private set; }
        public int Reason { get; private set; }
        public bool Open { get; private set; }
        public bool Chosen { get; private set; }
        public DeathChoice Choice { get; private set; }
        public double ChosenAt { get; private set; }
        public long Duplicates { get; private set; }

        // GotCaught entered with `caught` still false: a new episode. A second GotCaught of the same episode (the original
        // guards it anyway) is a duplicate.
        public bool Begin(int reason, bool alreadyCaught)
        {
            if (alreadyCaught || Open) { Duplicates++; return false; }
            Id++; Reason = reason; Open = true; Chosen = false; ChosenAt = 0;
            return true;
        }

        public bool TryChoose(DeathChoice c, double now)
        {
            if (!Open || Chosen) { Duplicates++; return false; }
            Chosen = true; Choice = c; ChosenAt = now;
            return true;
        }

        public void Failed() { Chosen = false; ChosenAt = 0; }

        // The scene the choice loaded is up (new GameManager): the episode is over.
        public void Complete() { Open = false; Chosen = false; }

        // A transition that never produced a scene load within the timeout is a failure (the choice re-opens).
        public bool TimedOut(double now, double timeout) { return Open && Chosen && now - ChosenAt > timeout; }
    }
}
