# 05 — World objects and interactions

Machine data: `data/world_object_catalog.json` (56 types: interaction, state, save behaviour, StolenPlayer status, per-scene counts, methods, save keys) and `data/instances/<scene>.json` (8,665 instances with key serialized flags). Deep dives that stay authoritative: `docs/research/DOOR_SYSTEM.md`, `LOCKPICKING_SYSTEM.md`, `WINDOW_SYSTEM.md`, `FURNITURE_SYSTEM.md`, `DESTRUCTIBLES_SYSTEM.md`, `HIDEOUT_SYSTEM.md`.

## 1. Interaction pipeline (VERIFIED_IL, CODEBASE_MAP §3.7 M4.3.1)
- **One interaction ray per frame:** `PlayerScript.Update` casts `Physics.Raycast(cam, forward, 2.25, mask)`. The **tag** of the hit transform selects the branch (about 80 tags: `Door`, `Pickupable`, `SitCar`, `CarBox`, `WindowEnter`, `Medkit`, `HidingSpot`, …), together with Rewired `interact` (E).
- **Guards:** the branch runs only when the player is free: `PMB.enabled`, not running, not carrying, `canInteract`, not hiding, …
- **Consequence for multiplayer:** every original interaction starts on the **local** player's instance and reads the local `PlayerScript`. Remote-player interactions must be replayed on the other instance as state changes (the replica bridge pattern), never by faking input.

## 2. Same look, different script
| Looks like | Actual component(s) | Motion / state | Note |
|---|---|---|---|
| Door | `Door` (plain) rotateOpen 215 / useVectorRotation 234 | `localRotation` towards open | + `Lockpick`/`MediumLock`/`ElectricLock`/`WeldDoor` for locks |
| Drawer | `Door` `isDrawer` moveOpen 841 | `localPosition` MoveTowards `OpenMove` | same code as doors |
| Cupboard / wardrobe | `Door` `isCupboard` (404) | rotation | `HidingSpot` reads `Door.isOpen` |
| Fridge / file cabinet / glass cabinet / container | `Door` with the type flag | rotation or move | flags read only in `Door.Start` (sounds, layer) |
| Car door | `Door` `isCarDoor` (34) | rotation | synced separately (`vdoor`, M5.0) |
| **Window, sliding (up or sideways)** | `Door` `isWindow` moveOpen (326) **plus** `LockedWindow` on the same GameObject (Madison: 178 of 181) | `localPosition` MoveTowards `OpenMove` (vertical `_single_vinyl_openable`: y; sideways `_double_vinyl_openable_A/B`: x ±0.49) | the `Door` is enabled by `LockedWindow` when unlocked (`LockedWindow.cs` lines ~123/~164: `GetComponent<Door>().enabled = true`) |
| Window glass / lock | `LockedWindow` (439) + `WindowOpenMaster` | cut, broken, unlocked; alarm | glass instantiation (`instantiateGlass`), noise, `starsAmount` |
| Climb through a window | `WindowUse` (768, GameObject `WINDOW_ENTER`) | moves the local player only | not a world state |
| Blinds | `Window_Blinds` (299) | open/closed | MISSING in sync |
| Scripted window blast | `WindowExplosion` (88, Warehouse 84) | exploded | mission script |
| Kitchen cabinets in some houses | `SimpleOpenClose` (Animator) | `objectOpen` | asset-store helper. Only `ObjectClicked` (no verified game caller): UNKNOWN whether interactive |
| Fence plank (102) | `DestroyObject` stages + blocking collider | destroyed | DESTRUCTIBLES_SYSTEM.md |

## 3. Type catalog (generated)
Counts are scene instances in the asset (`active_in_asset_per_scene` in the JSON separates inactive templates, e.g. interiors that `HouseDisableOnDistance` enables). "Instances" includes objects that stay inactive in some campaign states. A serialized object does not prove it is active in every state (StoryObject gating).

| Class | Category | Instances (total) | Scenes | Save behaviour | StolenPlayer status |
|---|---|---|---|---|---|
| `Door` | door/furniture/window leaf | 2430 | 14 | none (reset on scene load) | IMPLEMENTED / NEEDS MANUAL QA (Madison, Hillville, hideout; car doors via vdoor) |
| `Pickupable` | world loot | 2395 | 13 | ItemDay_<name>, Item_<name> | IMPLEMENTED / NEEDS MANUAL QA (ordinary/money/unique all scenes; heavy/story/drone/special BLOCKED) |
| `WindowUse` | window climb-through | 768 | 8 | none | LOCAL ONLY (player movement, replicated through avatar) |
| `LockedWindow` | window lock/glass | 439 | 8 | none | IMPLEMENTED / NEEDS MANUAL QA |
| `Window_Blinds` | blinds | 299 | 4 | none | MISSING |
| `LightSwitch` | light switch | 220 | 6 | none | MISSING |
| `Lockpick` | lock | 196 | 14 | none; NPCs relock via Door.ResetLock -> RelockDoor | IMPLEMENTED / NEEDS MANUAL QA (same regions) |
| `TimedDestroy` | timed cleanup helper | 196 | 8 | none | LOCAL (no gameplay state) |
| `Light_Trigger` | light area trigger | 176 | 3 | none | MISSING |
| `DestroyObject` | breakable (crowbar) | 120 | 9 | none (mission completion via Mission_Item) | MISSING (local) |
| `ClimbIvy` | climbable | 108 | 7 | none | LOCAL (movement) |
| `OnPossesion` | residence robbery zone | 89 | 1 | none | LOCAL (player state) |
| `WindowExplosion` | scripted window explosion | 88 | 2 | none | MISSING |
| `SecurityCamera` | security camera | 88 | 11 | discovered_camera_prefs key only | MISSING |
| `CameraTrigger` | camera view trigger | 87 | 10 | none | MISSING |
| `Pickpocket_Object` | pickpocket target | 85 | 4 | <name>_week | MISSING |
| `StoryObject` | story-gated object | 82 | 9 | derived from Story | BLOCKED BY DEPENDENCY (story sync) |
| `Mission_Item` | residence mission target | 79 | 8 | <house>_mission_<i> | MISSING |
| `SecurityPanel` | security panel minigame | 74 | 8 | none | MISSING |
| `MediumLock` | lock (medium lock minigame) | 72 | 12 | none | PARTIAL (rides Lockpick where assigned; standalone UNKNOWN) |
| `HidingSpot` | hiding place | 66 | 8 | none | PARTIAL (avatar shows hiding? UNKNOWN; NPC perception local) |
| `ReadNote` | note | 58 | 7 | SPrefs <transform name> = 1 | MISSING |
| `StashTrigger` | stash/hidden spot trigger | 36 | 6 | UNKNOWN | MISSING |
| `WeldDoor` | welded door (blowtorch) | 32 | 4 | none | MISSING |
| `SimpleOpenClose` | animator open/close helper (asset-store) | 31 | 4 | none | UNKNOWN (no interaction path verified) |
| `Vent` | vent | 30 | 4 | none | MISSING |
| `RandomFileCabinet` | random cabinet | 30 | 3 | none | IMPLEMENTED / NEEDS MANUAL QA (seeded roll, DESIGN COMPROMISE) |
| `SafeCrack` | safe | 28 | 10 | SafesOpened stat | MISSING |
| `DigHole` | dig spot | 27 | 6 | DugHoles stat | MISSING |
| `TutorialTrigger` | tutorial trigger | 26 | 6 | Tutorial_* keys | LOCAL (UI) |
| `ContractItem` | contract target item | 24 | 6 | contract keys | PARTIAL (loot shared; contract completion host-only) |
| `HiddenItem` | hidden item (+100$) | 21 | 3 | HiddenItems stat; key UNKNOWN | UNSAFE for P2 (money excluded on client, W4.1B) |
| `MailBox` | mailbox | 20 | 3 | UNKNOWN | MISSING |
| `ElectricLock` | electric lock | 15 | 3 | none | MISSING |
| `ParcelLocker` | parcel locker | 15 | 2 | parcel_code_pref keys | PARTIAL (parcel loot shared W4.2D; locker state MISSING) |
| `Garage_Switch` | garage door switch | 15 | 3 | none | MISSING |
| `PlantEvidence` | plant-mission target | 15 | 4 | via Mission_Item | MISSING |
| `CarGate` | car gate | 10 | 3 | none | MISSING |
| `CarBox` | car / locker storage | 9 | 5 | Storage_Item_<id>/Locker_Item_<id> | COMPLETE / VERIFIED (car box); locker IMPLEMENTED / NEEDS MANUAL QA |
| `GasDetector` | gas detector | 8 | 2 | none | MISSING |
| `LockedCarDoor` | stealable car door lock | 7 | 3 | none | MISSING |
| `FuseBoxSwitch` | fuse box | 6 | 3 | none | MISSING |
| `ATM_Object` | ATM | 6 | 3 | prefs_to_set | MISSING |
| `Doorbell` | doorbell | 5 | 1 | none | MISSING |
| `Place_Heavy_Trigger` | heavy placement | 5 | 4 | trunk/shed keys | BLOCKED BY DEPENDENCY (heavy ownership) |
| `ShopTrigger` | shop entry | 5 | 4 | none | IMPLEMENTED (wallet purchases) / NEEDS MANUAL QA |
| `Lockpick_Pin` | hideout lockpick practice | 5 | 1 | none | LOCAL |
| `CheckCameras` | camera view PC | 5 | 4 | none | BLOCKED BY DEPENDENCY (story) |
| `GarageOpenNPC` | NPC garage opener | 4 | 1 | none | MISSING (NPC) |
| `CarAlarm` | car alarm | 3 | 2 | none | MISSING |
| `Payphone` | payphone mission giver | 2 | 2 | Payphone<Region> + PayphoneActive/Minutes/Seconds/Name | MISSING |
| `PawnGuyTrigger` | pawn shop seller | 2 | 2 | none | IMPLEMENTED (sales counted via wallet) / NEEDS MANUAL QA |
| `Bed` | bed / sleep | 1 | 1 | none (time keys at checkpoints) | COMPLETE / VERIFIED (co-op sleep, M3.1.2) |
| `AdvanceTrigger` | story trigger | 1 | 1 | Story | BLOCKED BY DEPENDENCY (story) |
| `AdvanceStoryTrigger` | story trigger | 1 | 1 | Story | BLOCKED BY DEPENDENCY (story) |
| `Alarm` | house alarm | 0 | 0 | none | MISSING (no scene instance in any level: prefab-only or unused, UNKNOWN) |

## 4. Persistence and reset (VERIFIED_IL; WORLD_SYNC_AUDIT B.5/C)
- **No save keys** for doors, locks, windows, blinds, furniture, breakables, lights, fuse boxes, cameras, panels, alarms, safes, vents or hiding spots. They reset on every scene load. NPCs relock doors (`Door.ResetLock` → `Lockpick.RelockDoor`).
- **Keyed exceptions:**
  - `SecurityCamera.discovered_camera_prefs` (discovery marker only);
  - `ReadNote` `<object name>` = 1 (XP once);
  - parcel locker codes;
  - `Medkit`;
  - residence `_alert` (queued to the next checkpoint);
  - stats (`SafesOpened`, `LocksOpened`, `DugHoles`, `HiddenItems`).
- Stable identity requirement: region-scoped census id (exists, `WorldIdentity`). These states are **RegionLife** (live while a region is occupied), not campaign state, except the keyed exceptions.

## 5. Synchronization requirements per family
| Family | Live event | State lifetime | Authority | Side effects that must run once | Status |
|---|---|---|---|---|---|
| Doors / furniture / sliding windows | open/close/slow/break | RegionLife | host commit, region epoch | noise → NPC (owner) | IMPLEMENTED / NEEDS MANUAL QA (Madison, Hillville, hideout); other regions not enabled |
| Locks (pick, key, padlock, crowbar) | unlock/relock | RegionLife | host + reservation (minigame) | story advance (`on_unlock_advance_story`), alarm → police | IMPLEMENTED / NEEDS MANUAL QA; story effect BLOCKED |
| Window locks / glass | cut, break, unlock | RegionLife | host | alarm, noise, police stars, `complete_mission` | IMPLEMENTED / NEEDS MANUAL QA (alarm effects local) |
| Breakables, blinds, lights, fuse boxes, vents, dig spots, safes, welded doors, electric locks | per type | RegionLife | host | mission completion (Mission_Item), stats | MISSING |
| Cameras, panels, alarms, gas detectors | disable / trigger | RegionLife | region owner → police event | police chase (who is the suspect), story | MISSING |
| Hiding spots | occupy | live | local player + presentation | NPC perception | PARTIAL |
| Story objects / triggers | enable by stage | derived from `Story` | host story | – | BLOCKED BY DEPENDENCY (story sync) |

## 6. Case: sideways-sliding window near the containers at house 108 (user report)
**Verified object candidates** (VERIFIED_ASSET, `MadisonSt`):
- `DOMY/Dom 108 Old/ABANDONEDHOUSE/WallOut_3x_windowdouble_vinyl/Window_double_vinyl/Window_double_vinyl_openable_A` (×2 siblings): `OpenMove` (0.495, 0.4, 0).
- `…/Window_double_vinyl_openable_B` (×2): `OpenMove` (−0.492, 0.4, 0).
- Each is `Door` (`isWindow`, `moveOpen`) + `LockedWindow` on the same GameObject, active in the asset.
- The containers next to them (`Industrial Scene/Container_Main*`, `DOMY/Siedziba kurierow/Container_Main*`) are `Door` `isContainer` rotateOpen. They are not windows.
- The only sideways-moving windows in Madison are these `_double_vinyl_openable_A/B` variants (65 of each in the scene). Whether the user's window is one of them is INFERRED from "sideways" + "near 108".

**What was ruled out (VERIFIED):**
1. **Motion math.** The bridge snaps `localPosition` to `SavePos` (closed) and uses `OpenOnStartt` (open → `OpenMove`), exactly like `Door.Update`/`OpenOnStartt` (`WorldObjectBridge.Snap`).
2. **A different component.** It is the ordinary `Door`.
3. **Duplicate-path ambiguity.** The census appends `[siblingIndex]` for same-name siblings (`IdentityPath.Segment`), so the two `_A` copies get distinct ids.

**Ranked hypotheses (UNKNOWN until logs):**
- **H1 (most likely):** the window `Door` is **disabled** until its `LockedWindow` is unlocked. The opener's own unlock enables it locally. On the observer the replica swing runs on a disabled `Door` (no `Door.Update` → no motion), unless the lock/unlock mirror enabled it first (`WorldObjectBridge` line ~304 enables it only in the glass/unlock mirrors).
  - Ordering issue: a door page applied before the lock page → `SkipBusy`/no motion.
  - Indirect support: 841 `moveOpen` drawers use the same motion path, and furniture was reported working in 0.16.0.
- **H2:** the opener's capture misses the transition because the open comes from the `LockedWindow`/`WindowOpenMaster` path rather than the `assignDoor` watch.
- **H3:** the census flags it non-shared (`localOnlyDoors`, `has no unambiguous census id`).

**Minimal diagnostic for the next manual test** (no code change needed):
1. Both players in Madison at house 108. Player 1 unlocks or opens one sideways window. Note the exact window (front/back, left/right sash).
2. Search both instance logs (`%LOCALAPPDATA%\TS2MP\logs\<stamp>_*\plugin.log`) for:
   - `Window_double_vinyl_openable`
   - `WOBJ '...' has no unambiguous census id`
   - `localOnlyDoors`
   - `SkipBusy`
   - `WOBJ_STATS` (applied / inactiveSnaps counters)
   - the `objTxn` line of that transition.
3. Repeat with an already-unlocked window (open → close → open). If only the first open fails, H1 is confirmed.
4. Optional: enable `Debug.ShowOverlay` only if the user agrees.
