# Scene, recreation and save ordering

## Observed scenes and recreation

VERIFIED_RUNTIME: gameplay loaded MainScene Single then MadisonSt Additive while
active scene remained MainScene. NPC roots and core managers live in MainScene;
street/world interactables include MadisonSt. Loading callbacks precede complete
initialization, so the diagnostic schedules both early and late snapshots.

R9 same-process UI no-save return and Continue produced this ordering (UTC):

| Time | Event |
| --- | --- |
| 21:29:12.541 | MainScene unloaded |
| 21:29:12.576 | MadisonSt unloaded |
| 21:29:12.849 | MainMenuNew loaded Single |
| 21:30:15.565 | MainMenuNew unloaded |
| 21:30:15.847 | MainScene loaded Single |
| 21:30:18.849 | Early manager snapshot, timeScale 0 |
| 21:30:20.988 | MadisonSt loaded Additive |
| 21:30:23.998 | Manager snapshot, timeScale 1, active MainScene |
| 21:30:35.852 / 40.995 | Later initialization snapshots |

VERIFIED_RUNTIME: GameManager.PS changed from instance 316802 to 953900 at the
same `IMPORTANT OBJECTS[0]/First-Person Character[19]` hierarchy. The original
GameManager component changed from 308524 to 945622; it is recreated with MainScene, not a persistent
world authority. Instance IDs are not network or save IDs. MainMenu's InitializePrefs
is separate from MainScene's instance. Original hands/phone render again on the
recreated player. This is a real recreation, unlike the earlier adapter-blackout fixture.

UNKNOWN: all inter-area destinations, sleep/travel variants, individual interior
load/unload behavior, both-peer scene ordering and every persistent object. The
observed world uses additive street loading; this does not prove every house is a
separate additive scene. HackingPuzzle additive UI scenes have distinct semantics.

## Save boundaries

VERIFIED_STATIC: InitializePrefs configures FBPP profiles, scrambled typed JSON and
AutoSave=false. SPrefs delegates to FBPP. Game SaveGameNewSystem builds preference
state (inventory amounts, money/debt/XP, time, vehicles, missions/parcels, upgrades
and other systems), then FBPP.Save or SaveToMainProfile flushes according to mode.
SaveGame1 checks saving/chase/trespass conditions and calls SaveGameNewSystem(true).
PlayerScript.Quicksave dispatches based on GameManager.currentLoc and suppresses
duplicate saves while delaySaving is set. Inventory.QuitGameIE calls SaveOnTheRun
→ Quicksave, waits 0.15 realtime seconds, then quits or loads MainMenuNew.

VERIFIED_RUNTIME: R9 main-menu/Continue trace recorded FBPP.Save →
SaveSaveFile(true) → WriteToSaveFile(payload_length=57886). It proves execution of
the real write boundary without copying the JSON into tracked diagnostics. Numerous
SaveSaveFile(false) calls represent preference updates; with AutoSave=false they
do not establish a disk write. Earlier R8 also observed FBPP.Save on Continue.

UNKNOWN runtime: manual slot/return-with-save completion, timed autosave, travel
quicksave and mission/vehicle save branches. R8 automatic-slot save and R9 Save
confirmation clicks did not produce the expected SaveGame trace; the no-save button
did return to menu. The precise disabled-button cause was not proven. No manual
profile was created. Do not label those attempts successful saves.

VERIFIED_STATIC correction: SaveGame.LoseGame is empty. OnApplicationQuit calls it,
so closing the game window must not be described as an unconditional quicksave.
The optional trace observed OnApplicationQuit entry/return on controlled stops,
not SaveGameNewSystem completion. Disk durability is separate from inventory mutation.

## Future travel barrier (design only)

Host prepares target scene set, session epoch and checkpoint; both actors acknowledge
quiescence. Stop interactions/physics replication, discard old-epoch messages and
pending incomplete actions, load the agreed scene set, then await local actors and
relevant manager/registry readiness rather than one scene callback. Compare registry
hashes, apply host world snapshot, acknowledge its revision, resume ticked replication.

Timeout/disconnect before checkpoint commit aborts or returns to a defined host-owned
safe state. Reconnect receives the current epoch/snapshot, never resumes obsolete
coroutines against destroyed references. Persistent transport/session state must live
in mod-owned objects, separated from disposable GameManager/PS references.

Production Phase 1 still deliberately disconnects on scene changes. This task designs
future continuity and does not change that behavior. Save ownership must eventually
be host-owned coop state; clients must not overwrite independent single-player saves.
