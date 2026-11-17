// W6A.1: original story steps (StoryManager.story_scripts, 81 entries, 1.27c) for the developer menu's test-step selector.
// GENERATED from docs/game-atlas/data/mission_catalog.json (VERIFIED_ASSET byte-exact decode of MainScene
// MANAGERS/STORY MANAGER) by a one-off script; the support classification and reasons are hand-checked against the
// completion triggers / start effects in the same file (docs/research/STORY_SYSTEM.md §8).
//   Supported   - the step's own completion can be reached from the W6A.3 story seed loaded at this step (original
//                 StartNewGame baseline, then BeginStory -> InitiateStory runs the step's start effects once).
//   Limited     - loads like Supported, but the listed state is the new-game baseline or the step has a special start.
//   Unsupported - needs an item / scene produced by an earlier step (or is not a story step): refused by the selector.
// Earlier steps are never replayed: items, completed tasks, debt deductions and counters before the selected step are
// absent (new-game baseline, src/Shared/DevScenario.cs).
namespace TS2MP.Shared
{
    public enum StepSupport : byte { Supported, Limited, Unsupported }

    public sealed class StoryStep
    {
        public readonly int Index; public readonly string Key, Text, Reason; public readonly StepSupport Support;
        public StoryStep(int index, string key, string text, StepSupport support, string reason) { Index = index; Key = key; Text = text; Support = support; Reason = reason; }
    }

    public static class StoryCatalog
    {
        public const int Count = 81;
        public static readonly StoryStep[] Steps =
        {
            new StoryStep(0, "story1_escape", "Check the security camera view", StepSupport.Unsupported, "Intro (IntroHouse scene, CurrentLocation -1): only reachable through New Game, cannot be loaded from a save."),
            new StoryStep(1, "story2_escapeB", "Get your things from the shed, quietly", StepSupport.Unsupported, "Intro (IntroHouse scene, CurrentLocation -1): only reachable through New Game, cannot be loaded from a save."),
            new StoryStep(2, "story3_escape", "Escape through the back gate", StepSupport.Unsupported, "Intro (IntroHouse scene, CurrentLocation -1): only reachable through New Game, cannot be loaded from a save."),
            new StoryStep(3, "story4_breakin", "Break into 102 and steal enough to level up", StepSupport.Limited, "First story step after the intro: InitiateStory teleports player + main car (first_mission) and saving is blocked (block_saving)."),
            new StoryStep(4, "story5", "Drive to Joe's pawnshop and sell your loot", StepSupport.Supported, ""),
            new StoryStep(5, "story5b", "Learn 'Marking' skill", StepSupport.Supported, ""),
            new StoryStep(6, "story5c", "Go to a marked spot at 15:00", StepSupport.Supported, ""),
            new StoryStep(7, "story5d", "Mark the 103 tenant to learn his routine", StepSupport.Supported, ""),
            new StoryStep(8, "story5e", "Enter planning mode (look at the marked tenant and press [planning_mode])", StepSupport.Supported, ""),
            new StoryStep(9, "story6a", "Drive out of the area, and go to your hideout", StepSupport.Supported, ""),
            new StoryStep(10, "story6", "Use the computer", StepSupport.Supported, ""),
            new StoryStep(11, "story7", "Get to 102 and complete the task (smash a few things)", StepSupport.Supported, ""),
            new StoryStep(12, "story8", "Hide from the police", StepSupport.Limited, "Starts with a police chase (police_chase_on_start), as in the original."),
            new StoryStep(13, "story9", "Learn the 'Lockpicking (Level 1)' skill", StepSupport.Supported, ""),
            new StoryStep(14, "story10", "Buy a basic lockpicking tool on your phone {take_out_phone}", StepSupport.Supported, ""),
            new StoryStep(15, "story11", "Break into 103 and steal the drone [tenant is out 17-21]", StepSupport.Supported, ""),
            new StoryStep(16, "story12", "Go home and buy a 'Camera disable module' on Dronetech", StepSupport.Supported, ""),
            new StoryStep(17, "story13", "Buy all 104 info on RobTips", StepSupport.Supported, ""),
            new StoryStep(18, "story14", "Disable the 104 entrance camera with your drone", StepSupport.Supported, ""),
            new StoryStep(19, "story15", "Steal the cardboard box from 104 and bring it home", StepSupport.Supported, ""),
            new StoryStep(20, "story16", "Open the cardboard box from 104 and send the contents via Blackbay", StepSupport.Unsupported, "Needs the cardboard box from 104 (Item_CardboardBox_FlybyLiteDrone, step 19). Select step 19 instead."),
            new StoryStep(21, "story17", "Complete a task at 110 (plant a package)", StepSupport.Supported, ""),
            new StoryStep(22, "story18", "Complete the task at 105 (photograph kitchen)", StepSupport.Supported, ""),
            new StoryStep(23, "story19", "Learn 'Lockpicking (Level 2)' and buy 3 lockpicks", StepSupport.Supported, ""),
            new StoryStep(24, "story20", "Try a test lockpick at your hideout", StepSupport.Supported, ""),
            new StoryStep(25, "story21", "Buy all the info about 116 on Robtips", StepSupport.Supported, ""),
            new StoryStep(26, "story22", "Get caught by police and go to jail", StepSupport.Supported, ""),
            new StoryStep(27, "story23", "Break out the associate from her jail cell", StepSupport.Unsupported, "Needs the arrest / Jail scene of step 26 (CurrentLocation 93). Select step 26 and get caught."),
            new StoryStep(28, "story24", "Escape jail together (find the exit key)", StepSupport.Unsupported, "Needs the Jail scene and the associate freed in step 27. Select step 26 and get caught."),
            new StoryStep(29, "story26", "Steal a briefcase from 108 and send it via Blackbay", StepSupport.Supported, ""),
            new StoryStep(30, "story27", "Complete 7 missions from HellNeighbour", StepSupport.Limited, "Counter step: the completed-missions counter starts at the new-game baseline (0)."),
            new StoryStep(31, "story28", "Learn 'Agility (Level 1)'", StepSupport.Supported, ""),
            new StoryStep(32, "story29", "Complete 109 residences and go home (all loot, missions)", StepSupport.Limited, "Residence step: loot / mission state of 109 starts at the new-game baseline (0)."),
            new StoryStep(33, "story30", "Get to the Heist board at your hideout", StepSupport.Supported, ""),
            new StoryStep(34, "story30b", "Start the 'Forestside Resort' heist in your hideout", StepSupport.Supported, ""),
            new StoryStep(35, "story30c", "Steal diamond earrings from Forestside Resort and send them via Blackbay", StepSupport.Unsupported, "Needs the diamond earrings from the Forestside heist (started in step 34). Select step 34."),
            new StoryStep(36, "story31", "Pickpocket a guy with pink hair (Madison St.)", StepSupport.Supported, ""),
            new StoryStep(37, "story32", "Break into 107, steal the warehouse plan and go home", StepSupport.Supported, ""),
            new StoryStep(38, "story33", "Start the warehouse heist at your hideout", StepSupport.Supported, ""),
            new StoryStep(39, "story34", "Steal the forklift from Warehouse 401", StepSupport.Unsupported, "Takes place inside the Warehouse 401 heist (started in step 38). Select step 38."),
            new StoryStep(40, "story35", "Learn 'Lockpicking (Level 3)' and buy an automatic lockpick", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(41, "story36", "Steal a bracelet from a safe in 111 and go home", StepSupport.Supported, ""),
            new StoryStep(42, "story37", "Prepare the bracelet and sell it on Blackbay", StepSupport.Unsupported, "Needs the bracelet from the safe in 111 (step 41). Select step 41."),
            new StoryStep(43, "story38", "Take a few photos in 106", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(44, "story39", "Buy a garage at your hideout", StepSupport.Supported, ""),
            new StoryStep(45, "story40", "Learn 'Car theft (Level 1)' and buy a car lock pick gun", StepSupport.Supported, ""),
            new StoryStep(46, "story41", "Steal the red car from 106 and drive home", StepSupport.Supported, ""),
            new StoryStep(47, "story43", "Take off the Transmission and catalytic converter and send them via BlackBay", StepSupport.Unsupported, "Needs the parts of the red car stolen in step 46. Select step 46."),
            new StoryStep(48, "story43b", "Complete 5 contracts", StepSupport.Limited, "Counter step: the completed-contracts counter starts at the new-game baseline (0)."),
            new StoryStep(49, "story44", "Go to 204 and leave a note at garage 2A (unspotted)", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(50, "story45", "Learn 'Electronics (Level 1)'", StepSupport.Supported, ""),
            new StoryStep(51, "story46", "Steal a laptop with confidential data from 207 and go home", StepSupport.Supported, ""),
            new StoryStep(52, "story47", "Hack the laptop at home and send it via BlackBay", StepSupport.Unsupported, "Needs the laptop with confidential data from 207 (step 51). Select step 51."),
            new StoryStep(53, "story48", "Learn 'Specialization (Level 1)'", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(54, "story49", "Go to 208 and plant bombs in containers, unspotted (blowtorch needed)", StepSupport.Supported, ""),
            new StoryStep(55, "story50", "Escape home", StepSupport.Supported, ""),
            new StoryStep(56, "story51", "Repaint your car at the Car Service", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(57, "story52", "Learn 'Car Theft (Level 2)'", StepSupport.Supported, ""),
            new StoryStep(58, "story53", "Steal a car from 206 and take it home", StepSupport.Supported, ""),
            new StoryStep(59, "story54", "Sell 'Prospect' at Car Service", StepSupport.Unsupported, "Needs the 'Prospect' car stolen in step 58. Select step 58."),
            new StoryStep(60, "story55", "Buy an ATM hacking tool and hack the ATM in Madison St.", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(61, "story55_2", "Steal 10 heavy items", StepSupport.Limited, "Counter step: the stolen-heavy-items counter starts at the new-game baseline (0)."),
            new StoryStep(62, "story55_3", "Complete 201 residence (all loot, missions)", StepSupport.Limited, "Residence step: loot / mission state of 201 starts at the new-game baseline (0)."),
            new StoryStep(63, "story56", "Learn 'Specialization (Level 2)' and buy sleeping gas", StepSupport.Supported, ""),
            new StoryStep(64, "story57", "Complete the 203 residence (all loot, missions)", StepSupport.Limited, "Residence step: loot / mission state of 203 starts at the new-game baseline (0)."),
            new StoryStep(65, "story60", "Get to 113 and disable a camera security panel", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(66, "story61", "Take a few photos at 113", StepSupport.Supported, ""),
            new StoryStep(67, "story62", "Get home", StepSupport.Supported, ""),
            new StoryStep(68, "story63", "Go to sleep", StepSupport.Supported, ""),
            new StoryStep(69, "story63_2", "Complete 25 HellNeighbour missions", StepSupport.Limited, "Counter step: the completed-missions counter starts at the new-game baseline (0)."),
            new StoryStep(70, "story64", "Learn 'Electronics (Level 2)' and buy a hacking laptop", StepSupport.Supported, ""),
            new StoryStep(71, "story65", "Steal the bank construction plan from 202 and go home", StepSupport.Supported, ""),
            new StoryStep(72, "story66", "Start the 'Central Bank' heist", StepSupport.Supported, ""),
            new StoryStep(73, "story67", "Steal 350k$ worth of gold", StepSupport.Unsupported, "Takes place inside the Central Bank heist (started in step 72). Select step 72."),
            new StoryStep(74, "story68", "You got the gold, escape home!", StepSupport.Unsupported, "Takes place inside the Central Bank heist (started in step 72). Select step 72."),
            new StoryStep(75, "story69", "Send the gold bars via Blackbay", StepSupport.Unsupported, "Needs the gold bars from the Central Bank heist (step 72). Select step 72."),
            new StoryStep(76, "story70", "Get some sleep", StepSupport.Supported, "Starts with the original debt deduction of this step (once)."),
            new StoryStep(77, "story71", "Meet your friend in a secluded place on Madison St.", StepSupport.Limited, "Ending: the trigger starts the credits (Credits.Start writes Story + 1 into the isolated test save)."),
            new StoryStep(78, "story72_end", "Keep making money", StepSupport.Limited, "Money counter (MoneyMade) starts at the new-game baseline (0)."),
            new StoryStep(79, "story73_end", "Do whatever you want", StepSupport.Limited, "Final step: no objective to complete."),
            new StoryStep(80, "story73_freemode", "Free mode", StepSupport.Unsupported, "Free-mode stage (FreeMode = 1 only): StoryManager forces it, never a story step."),
        };

        public static StoryStep Get(int index) { return index >= 0 && index < Steps.Length ? Steps[index] : null; }

        // Developer test step: null = allowed, else the reason it is refused.
        public static string Refusal(int index)
        {
            var s = Get(index);
            if (s == null) return "No such story step (0-" + (Count - 1) + ").";
            return s.Support == StepSupport.Unsupported ? "Step " + index + " (" + s.Key + ") is not supported: " + s.Reason : null;
        }
    }
}
