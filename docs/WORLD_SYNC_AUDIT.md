# WORLD_SYNC_AUDIT — Thief Simulator 2 world, NPC, gameplay and persistence audit (M5 preparation)

Read-only investigation, 2026-11-11. Baseline: mod 0.8.0 / protocol 15 (working tree; M4.3 Phase B joint travel recorded as PENDING MANUAL QA in `docs/PROGRESS.md`; the user reports 0.8.0 as the tested baseline). **No code, test, protocol, build, deploy or save was changed by this audit.**

Method: static IL inspection of the installed game assemblies (`Thief Simulator 2_Data/Managed/Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`) with a read-only Mono.Cecil analyzer (type/field inventory, every `SPrefs`/`PlayerPrefs`/`FBPP` call with its key literal, callers, field readers/writers); the existing verified notes in `docs/CODEBASE_MAP.md` / `docs/PROGRESS.md`; the mod sources in `src/`. Scene asset contents (which objects exist in which map, their names and counts) were **not** readable statically and are listed as UNKNOWN with an in-game experiment.

Evidence tags: **VO** = verified in original IL · **VM** = verified in mod source · **VT** = verified by tests (offline) or user gameplay reports recorded in PROGRESS · **INF** = inferred · **UNK** = unknown.

---

## A. Executive summary

1. **The original game is a single-player state machine built around one `PlayerScript`.** 195 game types hold a `PlayerScript` field; 215 methods resolve it with `GameObject.FindGameObjectWithTag("Player")` (VO). NPCs (`AI`, `PolicemanAI`, `SidewalkAI`, `GuardAI`, `DogAI`, …) bind `PS`/`player` once in `Start`/`OnEnable` and read that one player's flags (`isHiding`, `isMoving`, `torch`, `isHitByLight`, `illegal`, `is_in_vehicle`, …). Nothing in the original code can treat a second player as a perception target. Our remote avatar is deliberately untagged and script-free (VM `src/Plugin/RemoteAvatar.cs`), so **NPCs on each instance only ever perceive the local player**.
2. **Persistence is one flat key/value store (`SPrefs` → `FBPP` file-based prefs) written only at checkpoints.** The disk write happens in `SaveGame.SaveGameNewSystem` (travel `GameManager.Select*`, quicksave key, autosave after leaving a running car or after a police chase ends, ending) and `GameManager.SaveAll` (VO). **Sleeping, closing the game, returning to the main menu and force-quitting do not save** (`SaveGame.LoseGame` is empty; VO + PROGRESS M4.3.3). Many world changes are buffered in memory (`SPrefs` values, `GameManager.mission_strings_to_set`, `parcel_strings_to_set`) and become durable only at the next checkpoint.
3. **Most world objects are not saved at all; the scene is rebuilt on every MainScene load.** Doors, locks, windows, safes, containers, lights, alarms, NPC positions/AI states, police, traffic and pedestrians have **no save keys** (VO: none of their classes writes `SPrefs`, except named exceptions below). Their state resets on every region change. What survives is derived: loot respawn days (`ItemDay_<objectName>`), unique-item flags (`Item_<name>`), story index (`Story`), residence alert/mission flags, per-location police alert level, daily NPC events (`DailyEvent*`), habit knowledge, stolen-car sales, renovations, upgrades.
4. **NPC schedules are recomputed from the game hour, not stored.** `AI.Startt` reads `GameManager.currentHour` and picks `BehaviourNumber[hour]` with its destinations (VO). After any scene load an NPC starts at its scene position and walks into its hourly behaviour. Daily variations (`isBurglarDay`, `isChillDay`, `isSickDay`, `isPoliceDay`) come from `DailyEventManager` keys that are saved (VO).
5. **World loot identity is the GameObject name.** A picked world item writes `ItemDay_<transform.name> = daysInGame + N` (`Pickupable.SetDayRespawn`, N = 3–5 by value, money 5); on scene start `Pickupable.<Start>` destroys the object while `daysInGame < ItemDay_<name>` (VO). Dropped items are renamed `"dropped"` and never get a respawn key; at a hideout checkpoint dropped pickups are converted into hideout-locker counts (`GameManager.TryToMoveDroppedItemsToStorage`, VO). There is no other stable world-object ID in the game.
6. **What the mod synchronizes today** (VM, details §K): connection/handshake, remote avatars, region membership, host-authoritative per-region clocks, sleep coordination + presentation, the two permanent player cars (fleet registry, driving, doors, passenger seat, travel, joint travel), the shared CarBox (lease + host-authoritative Player-2 transfers), and the host-owned Player-2 inventory (backpack, money, bank, tool wear). **Everything else in the world runs independently on both instances**: loot, doors/locks/windows/safes, NPCs, police, alarms/cameras, missions/story, shops/economy events, rent/bank ticks, traffic, pedestrians, hideout locker for Player 2.
7. **Main architectural gap:** there is no shared world-object layer. The client plays on a sandbox copy of the host save that is re-seeded at client start (VM `SaveGuard.SeedSandbox`), so its persistent world keys drift from the host's until the next restart, and its live scene objects are never told what the host's player did (and vice versa).
8. **Most reusable original mechanism:** almost every persistent world state is reconstructed by the objects themselves from `SPrefs` keys in `Start()`. A host-authoritative **world-key mirror** (replicating a whitelist of world key families into the client's sandbox before/at scene load) makes both scene loads produce the same world with zero changes to the original reconstruction code. Live changes then need a small **event layer** keyed by a region-scoped object identity.
9. **Recommended order:** world identity census → persistent world-key mirror (and stop client-side duplicate day/rent side effects) → shared loose loot pickups → Player-2 hideout locker + dropped items → doors/locks/windows/containers (live, non-persistent) → shared story/mission progression → economy ownership → NPC replication (observe) → NPC perception of both players → police/alarms. NPC AI is the largest and riskiest block and should come last, on top of identity, region ownership and event infrastructure that the earlier milestones establish.

---

## B. Original save system

### B.1 Storage and format (VO + CODEBASE_MAP "Save facts")
- API: `SPrefs` (game wrapper) → `FBPP` (FileBasedPrefs.dll) in-memory dictionary; `SPrefs.Get*/Set*` only touch memory. Disk write only via `FBPP.Save()` / `FBPP.SaveToMainProfile()`.
- File: `%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2\saveData.thief` (profile 0) / `saveData2..5.thief` (profiles 1–4), selected by Unity `PlayerPrefs "Profile"` in `InitializePrefs.Reinitialize`. JSON XOR `FBPPConfig.EncryptionSecret`, UTF-8 BOM.
- Unity `PlayerPrefs` (registry) holds only settings and meta keys (`Profile`, `ProfileSave`, `SaveExtra`, graphics/audio/input, `HardMode`, `Random_BG`, Enviro's own `Time_*`/`currentWeather`) — not world state (VO, key table §Q).
- No save version key exists (VO: no version key is read/written); the game tolerates missing keys through defaults.

### B.2 Writers of the disk file (VO)
| Trigger | Path |
|---|---|
| Travel (any `GameManager.Select*`, incl. jail and every special destination) | `Select*` → `SaveGame.SaveGameNewSystem(false,-1)` → `FBPP.Save` + `FBPP.SaveToMainProfile` |
| Quicksave key (`rewiredPlayer "quicksave"`) | `PlayerScript.Update` IL_0d6d → `Quicksave` → `SaveGame1` + `SaveGameNewSystem`; refused with original warning `cant_save_now` when `CheckIfCanSave` fails |
| Autosave | `PlayerScript.tryToSave` set by `CarController.ExitRunningCar` and `Police_Manager.End_Chase`; after 1 s, if `CheckIfCanSave()` and the `AutoSave` option toggle → `Quicksave` |
| Save-to-profile slot | `GameManager.SaveToProfile(int)` → `Quicksave` |
| Quit from the pause menu ("save on the run") | `Inventory.<QuitGameIE>` → `Inventory.SaveOnTheRun` → `Quicksave` |
| Ending | `ActivateEnd.OnTriggerEnter` → `GameManager.EndingFade` → `SaveGameNewSystem` |
| Hideout "picked items" summary | `GameManager.SaveAll` (writes `Item_` counts of the outing list, `StealthRating`) → `FBPP.Save` (no static caller: invoked from a UI/scene event — INF) |
| New game / free mode / continue | `MainMenu.StartNewGame` / `StartFreeMode` (`SPrefs.DeleteAll`, initial values, `SPrefs.Save`) / `MainMenu.Continue` |
| Misc | `InitializePrefs.RestoreSaveData`, one tutorial coroutine |

`CheckIfCanSave` refuses while: in a vehicle with a blocked door ray, police chase, seen by an NPC, inside a "possession" (house-robbery zone), sleeping/passing time, bleeding UI, drone active, current story step `block_saving`, hiding (VO).

### B.3 What `SaveGameNewSystem` writes (VO, in order)
`Playtime`, `StoryBackup`/`Story` (from `StoryManager.story`), `ThiefRating.SaveThings` (`Rating1..6`), player/car position + `Saved_Pos` (only on save points, not on travel), payphone mission state, `Inventory.SaveStats` (statistics) + notepad, `GameTime` (Enviro universal time), `Week`, `BLTHealth`, `DrunkLevel`, `GasAmount`, `Day`, `GasTankLeftCar`, `RentTimer`, `BatonLife`, `CrowbarLife`, cloak/survival/abilities/contract managers, upgrade shop, car paint, car upgrades, CMS car parts, `CurrentCar`, `StealthRating`, `Drone`, `TotalHoursPassed`, trunk item, **dropped items → locker (only when `GameManager.current_location == 1`, a hideout branch of `GameManager.Start`)**, cardboard item, `Money`, `Debt`, every backpack/storage/locker counter (`ItemID.Save_Item_Amount` → `Item_<id>`, `Storage_Item_<id>`, `Locker_Item_<id>`), **all queued `parcel_strings_to_set` and `mission_strings_to_set` keys set to 1**, XP/level, then the disk write.

### B.4 Loading
- Process start / main menu → `InitializePrefs.Reinitialize` loads the profile file into FBPP.
- Scene load (`MainScene` + map scene): `SaveGame.Start` → `SaveGame.LoadGame` restores player values; every world object reconstructs itself in its own `Start()` from keys (loot, story objects, residences, events, cars …).
- Returning to the main menu: `GameManager.LoadMenu` → `SaveGame.LoseGame` (empty) → `MainMenuNew`; "Continue" re-reads the file → **unsaved memory changes are lost** (VO + PROGRESS M4.3.3 C).

### B.5 State classes
| Class | Examples |
|---|---|
| 1 Persistently saved (at checkpoints) | story index, money, debt, items/storage/locker counts, loot respawn days, residence/mission flags, location alert level, daily events, habits, rent timer, game time/day/week, upgrades, renovations |
| 2 Memory only until scene unload | door/lock/window/safe/container state, NPC positions/AI/alertness, police units and stars, alarms, cameras disabled for the visit, traffic, pedestrians, dropped items outside the hideout |
| 3 Reconstructed from mission progression | `StoryObject`/`ActivateObjectStory`/`ActivateStory` enable objects by `Story`; story-gated menu destinations; `Heist.CheckStory` |
| 4 Reconstructed from game time | NPC behaviour per hour, daily events, loot respawn (`daysInGame`), rent, bank interest, ability cooldowns, hot item of the day |
| 5 Reset on scene load | everything in class 2 |
| 6 Random at scene creation | `RandomVan` (random 0..12 written on travel), traffic pool spawns, pedestrian spawns, random file cabinets/loot spawners (`RandomFileCabinet`, `ItemSpawner` — INF), random NPC sounds, AI random checkouts |

---

## C. Persistence matrix (original game, single player)

Legend: **K** kept · **R** reset/reconstructed · **D** durable only if a checkpoint follows · **—** not applicable.

| State | Close/reopen menu | Leave/re-enter building (same region) | Change region | Sleep | Main menu without save | Checkpoint (travel/quicksave/autosave) | Normal close | Force close | Restart from save | New game |
|---|---|---|---|---|---|---|---|---|---|---|
| Door open/closed, unlocked lock, broken padlock | K | K (same scene; INF) | R | K | R | R (no key) | R | R | R | R |
| Window cut/broken | K | K | R | K | R | R | R | R | R | R |
| Picked world loot (removed object) | K | K | R (object destroyed again on load if `ItemDay_` set) | K | **R — reappears** (key not on disk) | K until respawn day | lost if no checkpoint | lost | K until respawn day | R |
| Dropped item (outside hideout) | K | K | lost (no key) | K | lost | lost | lost | lost | lost | — |
| Dropped item in hideout | K | K | → locker count at the travel checkpoint | K | lost | → locker | lost | lost | in locker | — |
| Heavy items in car trunk / hideout shed | K | K | K (`PosX_item…`, `HeavyItemShed*`) D | K | D | K | D | D | K | R |
| NPC position / AI state / alertness | K | K | R (hour behaviour) | K/R (time jump) | R | R | R | R | R | R |
| Residence alerted flag (`<house>_alert`) | K | K | D (queued string) | K | lost | K | lost | lost | K | R |
| Residence mission complete | K | K | D (queued string) | K | lost | K | lost | lost | K | R |
| Location police alert level (posters) | K | K | K (immediate `SPrefs`) D | K | lost | K | lost | lost | K | R |
| Police chase / stars | K | K | ends (new scene) | blocked | R | autosave after chase end | R | R | R | R |
| Story index | K | K | D | K | lost | K | lost | lost | K | R |
| Money / items (Player 1) | K | K | D | K | lost | K | lost | lost | K | R |
| Bank balance | immediate `SPrefs` D | — | D | K | lost | K | lost | lost | K | R |
| Rent timer / hours / day | K | K | D | advances | lost | K | lost | lost | K | R |
| Daily events | `SPrefs` on day rollover D | — | D | may roll | lost | K | lost | lost | K | R |

### C.2 Comparison matrix (original vs multiplayer)
| System / object | Original controller | Original persistence | Scene reload | Restart after saved game | Current MP sync | Recommended authority | Risk | Evidence |
|---|---|---|---|---|---|---|---|---|
| Static world objects | scene content | none needed | identical | identical | both load same scene | none (static) | low | VO no loads besides travel |
| Loose loot | `Pickupable`, `ItemID.Item_Picked` | `ItemDay_<name>` respawn day, `Item_<name>` unique | destroyed while key active | same (if checkpoint) | independent (duplication) | host (durable) | high | VO `SetDayRespawn`, `<Start>d__68` |
| Dropped loot | `Inventory.DropItemNewSystem` | none; hideout → locker at checkpoint | lost (hideout: locker) | locker only | independent; P2 loss | host | high | VO `TryToMoveDroppedItemsToStorage` |
| Heavy loot | `Pickupable.is_heavy_item`, `VanHeavyItems`, `HeavyItemsShed` | trunk/shed positions | restored from keys | restored | not synced | host | medium | VO `SaveItems` keys |
| Doors | `Door` | none | reset | reset | independent | region owner/host (live) | medium | VO no writers |
| Locks | `Lockpick`, `MediumLockMaster`, `ElectricLock` | none (NPCs relock) | reset | reset | independent | region owner/host | medium | VO `RelockDoor` ← `Door.ResetLock` |
| Windows | `LockedWindow`, `WindowOpenMaster` | none | reset | reset | independent | region owner/host | medium | VO |
| Containers | `Door` (isContainer/Drawer/…), `RandomFileCabinet`, `LootBox` | none (loot inside via `Pickupable`) | reset | reset | independent | region owner | medium | VO |
| Safes | `SafeMaster`, `SafeCrack`, `LPSafe` | none (stat only) | reset | reset | independent | region owner | medium | VO |
| Security cameras | `SecurityCamera`, `SecurityPanel` | discovery marker only | re-enabled | re-enabled | independent | region owner | medium | VO `SpotCamera` |
| Alarms | `Alarm`, `RoofAlarm`, `LockedWindow.CheckAlarm` | none | reset | reset | independent | region owner → police event | high | VO |
| NPC positions | `AI` + `RichAI` | none | scene start + hourly behaviour | same | independent | region owner | high | VO `<Startt>` |
| NPC schedules | `AI.BehaviourNumber`, `DailyEventManager` | daily events saved | recomputed from hour | recomputed | independent (client generates own events) | host (events) + clock | medium | VO |
| NPC detection | SensorToolkit on `AI` | none | reset | reset | each instance: local player only | region owner, both players | very high | VO `AI.player` via tag |
| NPC chasing | `AI`, `PolicemanAI` | none | reset | reset | independent | region owner | very high | VO |
| NPC incapacitation | `AI.FallAsleep`, gas | none | reset | reset | independent | region owner | high | VO |
| Police | `Police_Manager`, `PolicemanAI`, `CopCar` | location alert level | units gone | alert kept | independent | region owner + host (alert) | very high | VO |
| Mission progress | `StoryManager.story`, `StoryScript` | `Story` + mission strings | reconstructed | kept | independent (client sandbox) | host | very high | VO `AdvanceStory` |
| Mission rewards | `Mission_Item`, `JobOffer`, `Reward` | via money/keys | — | kept | independent (double pay) | host grants once | high | VO |
| Shops | `Shop_Manager` | via checkpoint | — | — | independent | per player purchase, host stock | medium | VO |
| Player money | `PlayerScript.moneyAm` | `Money` | kept (memory) | checkpoint | P1 host save, P2 host record | per player (host) | medium | VM `P2Inventory` |
| Bank | `Darkbank`, `DayNightTime` interest | `BankBalance` | — | kept | P2 in host record; interest on both | per player (host tick) | medium | VO/VM |
| Rent | `DayNightTime` + `SaveGame.Update` | `RentTimer` | — | kept | runs on both | host household | high | VO |
| Debt | `PlayerScript.debtAm`, story | `Debt` | — | kept | host only | host household | medium | VO |
| Hideout locker | `CarBox` type 1, `Inventory.OpenStorage` | `Locker_Item_<id>` | reload from keys | kept | host only; P2 blocked | host (CarBox pattern) | high | VO/VM `CarBoxSync` |
| CarBox | `CarBox` type 0 | `Storage_Item_<id>` | reload | kept | shared, host-authoritative | host | low | VM tested |
| Vehicles (player cars) | `VehiclesManager`, `CarController` | `CurrentCar`, `XCar…` | reload | kept | synchronized (fleet) | host + holder | low | VM tested |
| AI traffic | Simple Traffic System pool | none | random | random | independent | keep local | low | VO firstpass |
| Time | Enviro, `DayNightTime` | `GameTime`, `Day`, `Week` | kept | checkpoint | host region clocks | host | low | VM |
| Sleep | `Bed`, `PassTime` | no save | — | — | coordinated | host | low | VM |
| Respawns | `ItemDay_`, `daysInGame` | keys | key-based | kept | per instance | host day | medium | VO |
| Upgrades | `UpgradeIt`, car/drone upgrades, skills | keys | — | kept | independent | decision: per player vs household | medium | VO |
| Persistent world unlocks | story flags, `unlock_hillville`, `garage`, renovations | keys | reconstructed | kept | independent (client sandbox) | host | high | VO |
| Weather | Enviro | `currentWeather` (PlayerPrefs) | — | — | independent | keep local or host later | low | VO |

Behaviour inside a region when entering/leaving a building: interiors are part of the loaded region scene (`SelectResidence.interior_holder`, distance-based renderer enabling in `HouseDisableOnDistance`; the only game scene loads are travel, menu and the hacking puzzle — VO), so building entry is not a scene transition and keeps all memory state (INF from the absence of loads; verify §P).

---

## D. World object catalogue

### D.1 Buildings and interiors
- Regions = one `MainScene` + a map scene (existing mod table `Regions`, VM `src/Shared/WorldState.cs`). Residences are `SelectResidence` components (fields: `houseNumber`, `AIs[]`, `loot_items`, `residence_missions`, `interior_holder`, `NPCs_Alerted`, `wasSpotted`, `isComplete`, `discoveredPref`, `FuseBoxSwitch FBS`, `residenceEventType`) (VO).
- Persistent per residence: alert flag `<name>_alert` queued in `parcel_strings_to_set` by `SelectResidence.WasAlerted` (written at checkpoint), removed by level-up popup `SelectResidence.RemoveAlert`; completion through mission/loot keys; habits discovered (`<prefsHabitName>_<n>`, `_spot`) by `AI.DiscoverHabit` (VO).
- Hideout: garage purchase (`garage`), renovations (`HouseRepairs`, `WallpaperBedroom`, `FloorBedroom`, …, `HouseRenovator`), radio, painting, locker, heavy-item shed (VO keys).
- Multiplayer: interiors exist identically in both instances (static scene content). Only their dynamic state needs sync.

### D.2 Doors, windows, locks (VO)
- `Door` (60 fields): `isOpen`, `opening`, `canUse`, `requires_key`, `only_with_key`, `key_item_ID`, `was_locked`, `breakin_discovered`, `brokeHinge`, `openedByPlayer`, `npcUsing`, type flags (`isWindow`, `isGate`, `isDrawer`, `isCupboard`, `isFridge`, `isFileCabinet`, `isGlassCabinet`, `isContainer`, `isCarDoor`). Used by players (`Use`, `UseFully`, slow `SlowFailed`) and NPCs (`NPCUse`, `OpenDoorNPC`, `CloseDoorNPC`). **No save keys.**
- `Lockpick` (lock on a door set): `unlockDoor[]`, `LockType`, `isPadlock`, `hasAlarm`, `on_unlock_advance_story`/`story_name`; `DoorUnlock` → story advance possible; `BreakPadlock`; `RelockDoor` ← `Door.ResetLock` ← `AI.OnTriggerEnter` / `PlayerScript.Update` → **NPCs relock doors** (`AI.return_home_reset_locks`). No save keys.
- `LockedWindow` (`brokeGlass`, `hasAlarm`, `alertNPCs`, `addNoise`, `complete_mission`, `assigned_residence`); `BrokeWindow` ← `Brick.OnTriggerEnter`, `PlayerScript.CrowbarHit`; `CheckAlarm` → police. `WindowOpenMaster` (glass-cutting minigame), `WindowUse` (climb through), `WindowExplosion`, `GetAllWindows.EnableAlarmsOnAllWindows`. No save keys.
- Other locks: `MediumLockMaster` (lock minigame, `Unlocked` → story advance), `ElectricLock`, `WeldDoor`, `IntercomGate`, `Gate`/`GateSwitch`, `GarageDoor` (static state), `LockedCarDoor`, `KeyScript`, `Doorbell`, `Vent`, `HidingSpot`.
- Decision "does a lock stay unlocked after reload?": **no** — nothing persists lock state (VO, absence of writers); verify in game §P.
- Duplicate-simulation risk: HIGH for gameplay consistency (P1 sees a closed locked door the client already opened), LOW for data corruption (nothing is saved).

### D.3 Interactables (VO)
| Object | Effect | Persistent? |
|---|---|---|
| `LightSwitch`, `SwitchLight` | lights (NPCs react: `NPC_Entered_Room`) | no |
| `FuseBoxSwitch.PowerOff` | residence power cut (lights, cameras) | no |
| `SecurityPanel` / `Security_Panel_Minigame` | `PanelDeactivate` → cameras `DisablePermamently`, may advance story; failure → police | no (except tutorial flag `SecurityPanelTut` in PlayerPrefs) |
| `SecurityCamera` | spots player → police; `SpotCamera` writes `discovered_camera_prefs` (map marker knowledge) | only the discovery marker |
| `SecurityLaser`, `Alarm`, `RoofAlarm`, `GalleryAlarm`, `CarAlarm` | `TriggerAlarm` → `PlayerScript.StartPoliceChase` / NPC hearing | no |
| `SafeMaster`/`SafeCrack`/`LPSafe` | safe minigame, opens container doors | no (statistic `SafesOpened`) |
| `RandomFileCabinet`, `LootBox`, `HiddenItem` (`HiddenItem` key), `MailBox` (camera placement, `SideAnim`), `ParcelLocker`/`ParcelStorage` (parcel codes, `parcel_strings_to_set`) | containers | parcels/hidden item: yes; cabinets: no |
| `Bed` / `PassTime` | sleep (no save; `advance_if_slept` story) | time only |
| `UseComputer` | websites, jobs, contracts | contracts saved |
| `ATM_Object`, `Pickpocket_Object`, `Payphone`, `CardboardMinigame`, `HackingLaptop`, `Jewelery_*` | minigames with rewards | partly (`_week`, payphone, hacking) |

---

## E. NPC architecture

### E.1 Categories (VO classes)
`AI` (residents/tenants, 252 fields — the core), `GuardAI` (patrol guards), `PolicemanAI` + `CopCar` + `Police_Manager` (police), `SidewalkAI` + `SidewalkSpawner` + `SidewalkNPCDetect` (pedestrians), `DogAI`, `CatAI` (animals), `MobGuyAI`, `PriestAI`, `MannequinAI`, `DemonAI`/`ImpAI`/`HelicopterAI`/`Obunga` (special/easter-egg/scripted), `CodenameAI` (the companion "associate"), `Ambulance`/`AmbulanceManager`, AI traffic (`TurnTheGameOn.SimpleTrafficSystem.AITrafficController`, job-system car pool, firstpass DLL).

### E.2 Creation and identity
- Residents are scene objects bound to residences (`SelectResidence.AIs[]`) — fixed, not spawned (INF from `AIs[]` arrays and `startPosition` fields). Identity candidates: residence + index, `TenantName`/`prefsHabitName` (names generated by `GenerateNPCNames`/`GiveNPCNames` — UNK whether deterministic).
- Pedestrians: `SidewalkSpawner` + `SidewalkAI` (pool/random, UNK details); traffic: pooled, random spawn points.
- Police units: instantiated by `Police_Manager.SpawnNewPoliceCar` / `SpawnPoliceSimple` (`spawned_policemen`, `spawned_cars`) — dynamic, no identity.
- No NPC is persistent across scene loads; there is no NPC save key except habits/events/statistics (VO).

### E.3 Routines (VO)
- `AI.BehaviourNumber[]` per hour, `walkDestinations[]`, `SitPoints[]`, `sitNrs[]`, `BedPoint`, `OutPos` (out of house), `Nr3Bed` sleeping, `Nr4SitDown`, `isWorking`.
- `AI.<Startt>` (scene start) reads `GameManager.currentHour` → `hourBhv` → `ChangeBehaviour` (walk/sit/bed/out). `AI.Update` copies `GameManager.currentHour` every frame and changes behaviour on hour change (with random variations `randomChangeChance`, `doRandomCheckouts`).
- Daily events: `DailyEventManager.GenerateMonthEvents/SetNextDay/SetTodayAsBurglarDay` (`DailyEvent*`, `EventDay`, `GeneratedEvents` keys) → `ApplyEventToAI` → `AI.ApplyEvent` (`isBurglarDay`, `isChillDay`, `isSickDay`, `isPoliceDay`, `blockSleepyDay`, `currentMood`).
- **Positions are never saved; after a load NPCs start from scene positions and resume the behaviour of the current hour** (VO for the code path; visual result to confirm §P).

### E.4 AI state (actual field names, VO)
Normal: `walkingTowards`, `isSitting`/`sitting`, `isSleeping`/`lyingDown`, `OutOfHouse`, `isWorking`. Alert: `HeardSomething`/`heardPos`/`checkItOut`, `explore_home`, `surprise_moment`, `sees_player`, `spotted`, `peace_amount` (calm meter), `concernMultiplier`/`addConcern`, `seen_amount_of_times`. Reaction: `Escape_Home` + `escape_location` → `EscapeHomeAndCallCops` → `Delayed_Police_Call` → `Police_Manager.NewHouseCalled`; `catchedPlayer`; armed (`isArmed`), pepper spray (`hasPepperSpray`). Incapacitated: gas (`is_inhaling_gas`, `gas_inhaled`), `feel_asleep`/`FallAsleep` (crowbar, tranquilizer, car hit, `HitPersonTrigger`), `waking_after_knockdown`, `callCopsAfterWakingUp`, `secondKnockdown`. Discovery: `Discover_Breakin` (open doors via `doorSensor`/`foundOpenDoor`, `careAboutOpenDoors`), `foundMissingItem`/`missingItemsFound`, `foundMissingCar`. Dogs: `dog_alerted`, `dog_sense`, meat (`eat_meat`, `got_fed_meat`).

### E.5 Perception (VO)
- Sight: SensorToolkit `RaySensor ray_sensor` aimed at the player by `LookAt ray_look_at`, `IsObstructed`, `DetectedObjects`, plus `Sensor_Range`; light level via `PlayerScript.isHitByLight` (`ShadowDetector`), night multiplier, masks (`CheckForMasks`), torch.
- Hearing: `RangeSensor hearingSensor`, `PlayerScript.MakeNoise`/`AlertNPCs_DistanceBased` → `AI.HeardSound`; footsteps (`shoes_itemID`, `makeFootsteps`, `isMoving`), alarms (`Alarm.TriggerAlarm` → `HeardSound`).
- Target choice: **always `AI.player` / `AI.PS`, set once in `AI.Start`/`OnEnable` via `FindGameObjectWithTag("Player")`** (VO, writers of `AI.player`). `Player_Is_Seen`/`Player_Lost`/`Player_In_Hear_Range` have no IL callers — they are sensor UnityEvents wired in the scene/prefab (INF); the sensors detect colliders by layer/tag.
- Two players: impossible without adaptation. The remote avatar has no collider/tag (VM), so it is invisible to sensors; even with a tagged collider, every reaction reads the local `PS` fields and calls local `PS.StartPoliceChase` (VO).

### E.6 Reactions and combat
Call police (`EscapeHomeAndCallCops`, `Delayed_Police_Call`), chase/attack (`catchedPlayer`, armed residents, `HurtPlayer`, `PepperSpray`), police taser/shooting (`PolicemanTaser`, `PoliceShoot` → `PlayerScript.GetTazed`), NPC-to-NPC: `AI.GuardAlert` + `alertGuardAI`, `SpotNPC` (associate/drone/planning mode), dogs barking.

### E.7 Persistence
Chasing, remembered burglary, unconscious state, called police, modified patrols, NPC room position: **none survive a scene load** (no keys; VO). Surviving: the residence alert flag / location alert level (checkpoint), habits learned, daily events, statistics (`KnockedOutPeople`, `SpottedPeople`).

### E.8 Multiplayer NPC authority — evaluation
| Option | Pros | Cons |
|---|---|---|
| Host simulates all NPCs | single truth | host has no scene for a region it is not in (independent regions); would need the host to load other regions — impossible with the original single-scene design |
| **Region-owner simulation** (the existing region-clock owner rule: host if present, else the client there) | matches region clocks already in place (VM `RegionClockBook`), each region simulated exactly once, original AI runs unchanged on the owner, the other instance only presents | owner change when players move; observers need a replicated NPC presentation (kinematic, original animations) and their interactions must be proposals to the owner |
| Both simulate, reconcile | no work | two different worlds (current state) |
Recommendation: **region-owner authoritative AI with host-committed durable effects** (Option C below). Non-owner instance: original `AI` components disabled for steering/decision (A* `RichAI` stopped), transforms + animation state driven by owner snapshots at a bounded rate, interaction requests (knock out, pickpocket, gas) sent to the owner. Second-player perception needs an adapter on the owner: a lightweight proxy "player" per remote participant that the sensors can detect, plus a reaction redirect so `StartPoliceChase` etc. target the right player. This is the hardest part and needs its own milestones.

NPCs in regions nobody occupies: not simulated by the original game either (no scene) — nothing to do; the region resumes from time-based reconstruction when someone enters (INF, matches original).

---

## F. Police and security (VO)

- Trigger sources of `PlayerScript.StartPoliceChase` (19 call sites): NPC calls (`AI.Update`, `Delayed_Police_Call`), alarms (`Alarm`, `RoofAlarm`, `LockedWindow.CheckAlarm`, `Lockpick.Update`, `Security_Panel_Minigame.Failure`, `CameraTrigger`), car alarms (`OtherCarController.SetOffAlarm`, `CarController.Update`, `CourierCar`), pedestrians (`SidewalkAI`, `SidewalkNPCDetect`), crowbar break-ins, pickpocket caught, story steps.
- `Police_Manager` (per scene): `current_stars`, `last_known_location` (from `PS`), `spawned_policemen`/`spawned_cars`, helicopter, `Activate_Chase`, `AddNewStar` (via `NewHouseCalled`), `End_Chase` (→ autosave request), `Detection`/`Detection_Hide`. Chase flag lives on the player (`PlayerScript.isPoliceChase`).
- Persistent: per-location alert level (`Location_Manager.location_string` key; `SetAlert` +2 up to a cap from `Activate_Chase`/`SpawnNewPoliceCar`; `RemoveOnePoster`/`RemoveAllAlerts` clear) read by `CopCar.GoToPoint`, `PolicemanAI.OnEnable`, `Location_Check` (police presence UI), residence `_alert` flags, statistics. Arrest: `GameManager.GotCaught` (← gas collapse, `PlayerScript.Update`) → `SelectJail` (jail travel, item confiscation `ItemJail*`, `SpawnWarningHardcore`).
- Ownership: chase/stars = player-specific in the original structure (on `PS`), police units = region world objects, alert level/posters = durable world state.
- Two independent responses today: each instance spawns its own police for its own player's crimes; the other instance sees none (INF from per-instance `Police_Manager`; nothing synchronized, VM). Multiplayer design: one region `Police_Manager` on the region owner; crimes reported as events with the offending player; stars per player (or shared per region — design decision for the architect).

---

## G. Loot and item lifecycle (VO)

- Item definitions: `ItemID` components (one per item type on the player; `item_ID`, `item_amount` backpack, `item_amount_storage` CarBox, `item_amount_locker` hideout locker, `max_amount`, `item_value`, `item_weight`, `is_heavy`, `isStoryItem`). Keys `Item_<id>`, `Storage_Item_<id>`, `Locker_Item_<id>`, `Item_Discovered_<id>`, `Item_Value_<id>`.
- World loot: `Pickupable` on scene objects (`item_ID`, `is_money`/`add_money`, `is_heavy_item`, `destroy_if_was_picked`, `deleteIfPref`/`prefName`, `never_destroy`, `dontDestroyDays`, `randomName`/`generatedHash`, `advance_story_if_picked`, `story_name`, `isContractItem`, `wasDropped`, `isPainting`).
- Pickup: `PlayerScript` ray (tag `Pickupable`) → `Pickupable.<PickingItem>` → `ItemID.Item_Picked` (backpack +, `StolenItems` stat, parcel string) → `Pickupable.SetDayRespawn` (`ItemDay_<name>` = `daysInGame` + 3/4/5, only for non-`dropped` objects with XP or money) → optional `mission_strings_to_set`/`parcel_strings_to_set` additions, contract item check, `StoryManager.AdvanceStory` → object `Destroy`. Adds to `Picked_Items_List` (DontDestroyOnLoad outing list).
- Scene start: `Pickupable.<Start>`: destroyed if `deleteIfPref` key set, if `destroy_if_was_picked` and `Item_<name>` already owned, or if `daysInGame < ItemDay_<name>`.
- Respawn: day-based (`Inventory.daysInGame`, incremented in `DayNightTime.Update` on day rollover). A picked object is gone for 3–5 in-game days once a checkpoint saved the key.
- Drop: `Inventory.DropItemNewSystem` instantiates a pickup named `"dropped"` (`wasDropped`), `ItemID.Drop_Item` decrements the backpack. No position save. At a hideout checkpoint (`current_location == 1`) `TryToMoveDroppedItemsToStorage` converts dropped non-heavy pickups into `item_amount_locker` (respecting `max_amount`).
- Heavy loot: carried objects, car trunk positions (`VanHeavyItems.SaveItems`: `PosX_item*`…), hideout shed (`HeavyItemsShed.SaveItems`), shop heavy triggers (`Shop_Heavy_Trigger`, `HeavyItemsShed`).
- **Player-2 drop loss (confirmed mechanism, VM + VO):** a client drop creates the pickup only in the client scene; the backpack decrement is reported to the host as a Player-2 inventory change (VM `Player2Sync`); the host never sees the object; in the hideout the client's checkpoint moves it into the client's locker copy, which the mod clears (VM `src/Plugin/Player2Sync.cs` `_set("Locker_Item_" + id, 0)`, client locker moves refused in `CarBoxSync`) → the item is gone for Player 2. Outside the hideout it is lost at the next scene load (same as single player).
- Stable identities: **only the GameObject name** (used by the game itself for `ItemDay_` and `Item_<name>`). Uniqueness per region is UNK (duplicate names would share a respawn key in the original too). No instance IDs/GUIDs are used by the game.
- Shared-loot requirements (design, not implemented): world object id = (region, object name, [hierarchy path or rounded scene position for disambiguation]); host-committed "taken" events per id plus the original `ItemDay_<name>` key in the host save; pickup = proposal → host decides first-wins → both instances destroy the object; the item goes to the picker (Player 2 through the existing `P2Inventory` record path). Respawn stays original (day keys mirrored to the client).

---

## H. Mission architecture (VO)

- Story: linear `StoryManager.story` (int, key `Story`, backup `StoryBackup`) indexing `story_scripts : List<StoryScript>`. `StoryScript` is a data record (77 fields) of objective conditions and effects: `advance_if_item_sold`, `advance_if_residence_complete`, `advance_if_slept`, `advance_if_has_item`, `advance_if_home`, `advance_if_jail`, `advance_on_police_escape`, `advance_if_garage_bought`, `advance_if_level_up`, `advance_if_tips_bought`, `complete_missions_amount`, `complete_contracts_amount`, `complete_money_amount`, `steal_heavy_items`, `if_learned_skill_advance`, `deduct_debt`/`deduct_amount`, `setPrefsTo1[]`, `unlock_hillville`, `unlock_associate`, `unlock_quickBuy`, `unlock_tutorial`, UI/button blocks, `block_saving`, `police_chase_on_start`, `call_sequence` (phone call `StorySequence`), `save_story_on_start`.
- `StoryManager.AdvanceStory()` = `story++` + `InitiateStory()` (free mode: no-op). 25 call sites: pickups, selling (`BlackBay_Object.SellIt`, `Inventory.Sell_Item_NewSystem`), lock/panel/camera completion (`Lockpick.DoorUnlock`, `MediumLockMaster.Unlocked`, `SecurityPanel.PanelDeactivate`, `SecurityCamera.DisableCamera`), `Bed.Stand`, `AI.DiscoverHabit`, shop purchases, triggers (`AdvanceStoryTrigger`, `AdvanceTrigger`), engine start (`<RunCar>`), level-up, police escape, notes, intro.
- Residence missions: `SelectResidence.residence_missions` (`Mission_Inventory`: pay, image), `Mission_Item.MissionComplete1` pays `PlayerScript.moneyAm += mission_pay` directly + `MoneyMade` stat + queued mission string (checkpoint). Jobs/contracts: `JobOffer` (`Reputation1`, `Jobs_Complete`), `ContractManager` (`Contract*` keys), `Reward.Claim`, heists (`HeistManager.SelectHeist`), pickpocket weeks, payphone missions, stolen cars (`StolenCarsManager`).
- Story-reconstructed world: `StoryObject`, `ActivateObjectStory`, `ActivateStory`, `Heist.CheckStory`, `StoryManager.InitiateStory` (`AssociateUnlocked`, `QuickBuyEnabled`, `_Call`, `_deduct` keys).
- Single-player assumptions to adapt: advance calls fire on whichever instance's player performs the action; rewards go to the local `PS.moneyAm`; story steps read the local inventory/home/jail state; `setPrefsTo1` writes into the local save.
- Difficult sequences (VO types present): `IntroSequence`/`IntroMap`, `EndingSequence`/`EndingGuy`, `StorySequence` phone calls, heists with spawned objects (`HeistManager.DeleteSpawnedObjects`, `LearnAllHabits`), jail (`JailCellTrigger`, `JailItems`), `PlanningMode`, drone missions, scripted transitions via `Select*` (heist start, arrest, delivery).

---

## I. Economy and storage (VO + VM)

| Value | Original owner/key | Nature | Mod today |
|---|---|---|---|
| Cash | `PlayerScript.moneyAm` / `Money` (~40 writers) | player | P1 host save; P2 host-owned record (VM `P2Inventory`) |
| Bank | `BankBalance` (Darkbank deposit/withdraw, daily interest in `DayNightTime.Update` with `reward_bank`) | player in practice | P2 record field `Bank` |
| Debt | `PlayerScript.debtAm` / `Debt` (story `deduct_debt`) | story/household | host only; client sandbox copy diverges |
| Rent | `SaveGame.timeTillRentPayment` (`RentTimer`) decremented hourly in `DayNightTime.Update`, charged in `SaveGame.Update` (`moneyAm -=`, message `rent_paid`), gas refill charge there too | household | **runs on both instances** (client charges P2's money) — duplication risk |
| Shops | `Shop_Manager.Buy_Item` (fields only, no save until checkpoint) | player purchase, world stock (`heavyAmountInShop`) | independent |
| Blackbay | `BlackBay_Object.SellIt` (pays first, removes items backpack→locker→car storage) | player | VM guard (client Blackbay limited to own backpack) |
| Stocks | `StockManager` (`RefreshStock`, shares keys) | player | independent |
| Upgrades/tools | `UpgradeIt` (`_cost`), drone upgrades, car upgrades, skills (`SpecialPoints`) | player or household (ambiguous) | independent |
| Renovations | `HouseRenovator` (`HouseRepairs`, wallpaper/floor keys) | household | independent (client sandbox) |
| Mission pay | `Mission_Item`, `JobOffer.ClaimRewards`, `ContractUIObj.ClaimReward`, `Reward.Claim` | player who completes (original) | independent |
| CarBox | `Storage_Item_<id>` | world (car) | shared, host-authoritative (VM M4.3.2b/M4.3.3) |
| Hideout locker | `Locker_Item_<id>`, same `CarBox` component with `storage_type` 1 opened by `PlayerScript.OpenStorage(CarBox.storage_type)` → `Inventory.OpenStorage(type)` | world (hideout) | host only; client moves refused (VM `CarBoxSync`) |
| Heavy shed / trunk | `HeavyItemShed*`, `PosX_item*` | world | not synchronized |

Duplication risks (code paths exist; magnitudes UNK): rent and gas refill charged on the client from Player 2's money each period; daily bank interest on both (per player — may be acceptable); story debt deductions only on the host (client copy keeps stale debt display); mission rewards paid twice if both instances complete the same objective in their own scenes; shop stock per instance; daily events/hot item generated independently on the client.

Recommended ownership (to be decided by the architect): per-player = cash, bank, backpack, tool wear, skills/XP (as now for P2); household (host) = debt, rent, renovations, garage, hideout locker, heavy shed, CarBox; world (host) = story, residences, loot respawn, alerts, daily events; one-time rewards = granted once by the host to the completing player.

---

## J. Time and world events (VO + VM)

- Clock: Enviro `GameTime`; `GameManager.currentHour`; `DayNightTime.Update` detects hour changes (`prevHour`) and runs: `PlayerScript.totalHoursPassed++`, `SaveGame.timeTillRentPayment--`, `AbilitiesManager.HourPassed`, day rollover (`NewDay` key, `currentDayInt`, `Inventory.daysInGame++`, `DailyEventManager.SetNextDay`, `currentWeek`), daily bank interest (`BankBalance`, `BankIncrease`, `reward_bank`), `RefreshDay`.
- Stored: absolute `GameTime`, `Day`, `Week`, `TotalHoursPassed`, `RentTimer` (remaining hours), ability cooldowns (remaining), loot respawn (absolute day), daily events (absolute days), hot item of the day (`CurrentDayHotItem`).
- Recomputed: NPC behaviour (hour), lights (`DayNightTime.SetLights2`), shops (UNK opening hours), police patrol (UNK).
- Sleep: `Bed.LayDown`/`Parking.Activate` → `PassTime.Begin` (fast-forward; no save).
- Mod (VM): host-authoritative region clocks (`RegionClockBook`, `WorldSync`), clock writes made by the mod suppress the hour tick (`WriteClock` sets `currentHour`/`prevHour`), sleep coordination and presentation (`SleepPresenter`, the client's fast-forward produces no local tick). **Natural hour passing still runs `DayNightTime.Update` on both instances**, so rent, abilities, day rollover, daily-event generation and bank interest execute on the client too (INF from code; verify by log/experiment §P).

---

## K. Current multiplayer coverage (VM; manual-test status from PROGRESS/TESTING)

| Subsystem | Mod files / keys | Status |
|---|---|---|
| Session, handshake, launcher | `SessionNode.cs`, `Protocol.cs`, `Launcher.cs` | fully synchronized, manually tested |
| Save isolation (client sandbox) | `SaveGuard.cs`, `PathRedirect.cs` | implemented, manually tested (M1) |
| Remote avatar + movement | `PlayerSync.cs`, `RemoteAvatar.cs`, `PlayerState.cs` | tested |
| Region membership | registry `region`, `lastVisit` | tested |
| Region clocks | `clock`, `WorldTime.cs`, `WorldSync.cs` | partly confirmed in game (M3.1) |
| Sleep | `sleepStart`, `sleep`, `sleepShow`, `SleepPresenter.cs` | M3.1.2 accepted; M4.3.3 presentation reported working by the user |
| Player cars (fleet, driving, doors, handoff, passenger, travel) | `vehicle`, `vpark`, `vdoor`, `vehicleClaim/Take/Ride`, `travel*`, `VehicleSync.cs` | tested (0.7.1 baseline) |
| Joint travel | `jointTrip` | implemented 0.8.0, PENDING MANUAL QA in PROGRESS (user reports tested) |
| CarBox | `carbox`, `carboxItems`, `carboxReq`, `storageTxn`, `CarBoxSync.cs` | tested |
| Player-2 inventory | `p2inv`, `p2report`, `Player2Sync.cs` | tested (0.7.1) |
| Hideout locker for P2 | — | intentionally blocked (client moves refused) |
| World loot, dropped items | — | independently simulated by both |
| Doors/locks/windows/safes/containers | — | independent |
| NPCs, pedestrians, animals | — | independent (each instance: own NPCs vs own player) |
| Police, alarms, cameras | — | independent |
| Story/missions/contracts/jobs | — | independent (client on sandbox copy; reseeded at client start) |
| Economy events (rent, bank interest, shops, stocks) | — | independent; P2 money/bank in the host record |
| AI traffic | — | independent (cosmetic; collisions only local) |
| Weather | — | independent (Enviro) |

Conflict scenarios (current behaviour → required model):
| Scenario | Today (INF from code) | Required |
|---|---|---|
| Both pick the same object | both get the item (duplicated), each instance hides it locally | host first-wins on object id; loser's object vanishes |
| Both open one door | two independent door states | region-scoped door state, owner/host committed |
| P1 breaks a window, P2 watches | P2 sees an intact window | event + region state |
| P2 triggers an alarm | police only on P2's instance | one region police response |
| NPC sees P1, not P2 | each instance's NPC only knows its local player | owner AI evaluates both players |
| NPC attacks P2 | only on P2's instance; P1 sees the NPC elsewhere | owner simulation + replication |
| P1 stuns an NPC | P2's NPC copy stays awake | owner applies, replicated |
| P2 completes an objective | only P2's sandbox story advances; host save unchanged | proposal → host story |
| Both claim a reward | both paid | host grants once |
| Item respawns after sleep | per-instance `daysInGame` | host day + mirrored keys |
| Both buy one unique upgrade | both bought (two saves) | host household purchase |
| One player changes region | already handled for clocks/cars | world keys per region on entry |
| Both return to an earlier region | each rebuilds from its own keys (client keys stale) | mirrored host keys |
| Host saves and restarts | host world restored; client keeps its sandbox until it restarts | client re-mirror on join |
| Client rejoins with a fresh sandbox | client world = host save at seed time (VM) | live key mirror keeps it current |

---

## L. Shared world authority proposal

- **Option A — host simulates everything:** incompatible with independent regions (the host has no scene for a region it is not in); would force both players together or require a second scene load on the host. Rejected for NPCs; valid for durable state.
- **Option B — region-owner simulation of everything:** good for live simulation but leaves durable state (story, economy, loot keys) split across two save files.
- **Option C — hybrid (recommended):**
  1. **Host owns every durable world mutation** (the host save is the only world save — already the project rule): world keys, story, residences, alerts, loot taken/respawn days, household economy, storage. Clients send proposals; host validates/commits in the registry; the host's original `SPrefs` holds the result, persisted by the original checkpoints.
  2. **Region owner owns live simulation** of region objects that the original keeps only in memory: NPC AI, police units, door/window physics where needed. Owner rule = the existing region-clock owner (host if present, else the client there), so exactly one instance simulates each occupied region.
  3. **Non-owners present** replicated state on the original objects (as already done for cars: kinematic original object, original animations) and route their player's interactions as proposals.
  4. **Both instances reconstruct** the static and time-derived world from the same (mirrored) keys and the same region clock, so nothing static needs network traffic.
- One logical world is enforced by: a single committer per state class (host for durable, owner for live), stable object ids, idempotent events with transaction ids (pattern already used by travel/CarBox/joint trip), and snapshots on region entry.

---

## M. Object identity and replication strategy

- Unity instance IDs: process-local, unusable across instances/restarts.
- Game's own identity: GameObject names (`ItemDay_<name>`, `Item_<name>`), residence/house numbers, `story_name`, location strings, camera discovery prefs, `Door_ID` marker component (no fields; purpose UNK).
- Proposed id (fixed scene objects): `region` + **hierarchy path** (`root/child/...` names) + sibling index where names repeat; hash to 32 bits for the wire, keep the full path in a host-side census table for collision detection. Validate by a one-time census per region (log duplicates of path and of plain name; the original `ItemDay_<name>` behaviour shows which items already share keys).
- Dynamic objects: dropped items, police, spawned heist objects — host/owner assigns a transaction-scoped id at creation (`owner slot + counter`), announces spawn with position; never derived from Unity names.
- Persistent state encoding: reuse the original key where one exists (`ItemDay_<name>`, `<house>_alert`, `Story`, …) so the host's original save stays the source of truth; registry entries only for live/region state and for the mirror.
- Scene lifecycle: ids resolve after the region scene finished loading (bind once per `GameManager` like `VehicleSync.Bind`), cached dictionary id → component; invalidated on scene change.

---

## N. Risks and unknowns

Confirmed (code evidence): single-player `PlayerScript` binding in 195 types; no persistence for doors/locks/windows/NPC/police; loot identity = object name; dropped-item locker conversion only at a hideout checkpoint; unsaved changes lost on menu/quit; Player-2 drop loss mechanism; hourly economy side effects run on every instance.
Risks (not yet observed): duplicate object names sharing loot keys; client daily-event generation diverging NPC behaviour between instances; rent charged to Player 2; double mission rewards; story advancing only in the client sandbox and being re-seeded away; client autosave/quicksave writing only its sandbox (harmless for the host, confusing for P2).
Unknown: exact scene contents per region (counts of `Pickupable`, `AI`, `Door`), pedestrian/traffic determinism, NPC name generation determinism, shop opening hours, sensor UnityEvent wiring, `Door_ID` purpose, `GameManager.SaveAll` trigger, residence completion criteria details.

---

## O. Recommended milestone roadmap (dependency order)

Foundations implemented once and reused: **(F1) region object identity + census**, **(F2) host world-key mirror**, **(F3) generic world event/proposal channel with transaction ids and region snapshots** (generalizes the travel/CarBox/joint-trip patterns), **(F4) region-owner live simulation hand-off** (generalizes the region-clock owner).

| # | Milestone | Objective | Reuse | Extend | Prereq | Challenges | Persistence | Authority | Automated tests | Manual QA | Regression risk | Done when |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| W1 | Identity census (diagnostic) | per-region id table, duplicate report, no behaviour change | scene objects, names | Bind-per-scene pattern | — | path stability, duplicates | none | — | id hashing/collision tests | run each region on both instances, compare census logs | none | identical census on both instances, duplicates listed |
| W2 | World-key mirror + day/rent ownership | client world reconstructs from host keys; no client-side rent/day/event generation | original `Start()` reconstruction, `SPrefs` | registry snapshot/delta, P2-style key filter | W1 (for key families) | key whitelist (world vs player), timing before scene `Start`, sandbox writes | host save only | host | key classification, mirror ordering, stale/epoch tests | pick loot as host, client travels in: item gone; daily events identical; P2 not charged rent | medium (save/restore paths) | both instances load identical persistent world |
| W3 | Shared loose loot | one pickup = one item, both scenes update | `Pickupable`, `Item_Picked`, `SetDayRespawn`, P2 record | F3 events, P2 transactions | W1, W2 | first-wins races, money pickups, story items, unique items | host save (`ItemDay_`) | host | simultaneous pickup, retransmit, scene reload, restart | both pick same item; P2 picks while P1 watches; reload/restart | medium | no duplicate/lost item in all cases |
| W4 | P2 hideout locker + drops | P2 uses locker; drops never lost | CarBox UI type 1, `Locker_Item_`, lease/txn | `CarBoxSync`, `Player2Sync` | W3 (drop events) | locker capacity, drop→locker conversion | host save | host | lease/txn tests type 1, drop conversion | deposit/withdraw/drop/restart | medium (inventory) | P2 locker round-trip and drops persist |
| W5 | Doors/locks/windows/containers | shared live state per region | `Door.Use/UseFully`, `Lockpick.DoorUnlock`, `LockedWindow.BrokeWindow` | F3, region snapshot on entry | W1 | NPC door use, slow doors, relocking | none (like original) | region owner/host | event ordering, snapshot | open/lock/break with both watching | low–medium | identical door/window state for both |
| W6 | Shared story/missions | one story index, one reward | `StoryManager.AdvanceStory`, `Mission_Item`, contracts | proposals, W2 mirror | W2, W3 | conditions read local state, rewards, `setPrefsTo1` | host save | host | advance idempotency, reward once | P2 completes objective; both claim | high (progression) | story identical; rewards once |
| W7 | Economy ownership | household vs player money rules | Shop/Blackbay/renovation code | P2 record, host household | W2, W6 | ~40 money writers | host save | host | double-charge tests | rent, purchases, upgrades | medium | no double charge/reward |
| W8a | NPC replication (observe) | owner simulates, other presents | `AI`, `RichAI`, animations | F4 owner hand-off, snapshot stream | W1, W5 | owner changes, animation state, bandwidth | none | region owner | snapshot codec, owner switch | NPCs move identically on both | medium | one NPC simulation per region |
| W8b | NPC perception of both players | NPCs detect/react to either player | sensors, `PlayerScript` reactions | remote-player proxy on owner, reaction redirect | W8a | 195 single-player bindings, sensor wiring | none | owner | target selection tests | NPC sees P2 on host-owned region | high | both players detectable |
| W9 | Police/alarms/cameras | one police response per region | `Police_Manager`, alarms | events, owner | W8b | stars per player, arrest/jail | alert level (host) | owner + host | one response per event | trigger alarms from both | high | single coherent police response |
Postpone: AI traffic and pedestrians (cosmetic, random pools — keep local unless a later milestone needs them), weather, special/easter-egg AIs, survival mode, heists and scripted sequences until W6/W8 exist.

---

## P. Questions requiring real gameplay tests
1. Duplicate names: in each region, do several `Pickupable` objects share a name (shared `ItemDay_` key)? (W1 census.)
2. Pick an item, return to the main menu without saving, continue: does it reappear? (Expected yes.)
3. Unlock a door / break a window, travel away and back: reset? (Expected reset.)
4. Leave and re-enter a house without travelling: door/NPC state kept?
5. NPC positions after a reload at the same hour: same behaviour target, scene start positions?
6. Do the client's own rent payments and daily-event generation happen (client log / P2 money drop at rent time)?
7. Does `GameManager.SaveAll` run on entering the hideout / a specific UI?
8. Are pedestrian names/positions/traffic identical between instances (determinism)?
9. Does a dropped item by P2 in the hideout vanish after a P2 travel (confirm the loss mechanism)?
10. Location alert posters: what removes them in normal play (`RemoveOnePoster` trigger)?
11. Shop opening hours / stock behaviour per day.
12. Does the passenger's `Trigger Map` ever fire `MapBorder` (menu guard log in 0.8.0)?

---

## Q. Technical evidence appendix

### Q.1 Original classes/methods referenced
Save: `SPrefs`, `FBPP.Save/SaveToMainProfile`, `InitializePrefs.Reinitialize/RestoreSaveData`, `SaveGame.SaveGameNewSystem/SaveGame1/LoadGame/LoseGame/OnApplicationQuit/Update`, `PlayerScript.Quicksave/CheckIfCanSave/tryToSave/saveTimer`, `GameManager.SaveAll/SaveToProfile/LoadProfile/LoadMenu/EndingFade/Select*/SelectJail/TryToMoveDroppedItemsToStorage/mission_strings_to_set/parcel_strings_to_set/current_location`, `MainMenu.StartNewGame/StartFreeMode/Continue`, `Inventory.SaveOnTheRun/SaveStats`.
Loot: `Pickupable` (`<Start>d__68`, `<PickingItem>d__72`, `SetDayRespawn`, `WasPlacedInCar`), `ItemID` (`Item_Picked`, `Drop_Item`, `MoveToStorageOnDrop`, `Save_Item_Amount`, `Move_To_Storage`, `Move_Out_Of_Storage`, `Remove_Item_Blackbay`), `Inventory.DropItemNewSystem/OpenStorage`, `Picked_Items_List`, `VanHeavyItems.SaveItems`, `HeavyItemsShed.SaveItems`, `Loot_Manager`, `HiddenItem`.
World: `Door`, `Lockpick` (`DoorUnlock`, `RelockDoor`), `Door.ResetLock`, `LockedWindow` (`BrokeWindow`, `CheckAlarm`), `WindowOpenMaster`, `MediumLockMaster`, `SafeMaster`, `SecurityPanel`, `SecurityCamera` (`SpotCamera`, `DisablePermamently`), `FuseBoxSwitch`, `LightSwitch`, `Alarm`/`RoofAlarm`/`GalleryAlarm`/`SecurityLaser`, `SelectResidence` (`WasAlerted`, `RemoveAlert`, `SetInterior`), `HouseDisableOnDistance`, `Location_Manager` (`SetAlert`, `RemoveAlert`, `CheckIfPoliceAlerted`), `GameManager.RemoveOnePoster/RemoveAllAlerts`.
NPC: `AI` (`Start`, `<Startt>d__268`, `<ChangeBehaviour>d__271`, `Update`, `HeardSound`, `Discover_Breakin`, `Delayed_Police_Call`, `EscapeHomeAndCallCops`, `FallAsleep`, `ApplyEvent`, `DiscoverHabit`), `DailyEventManager`, `GuardAI`, `PolicemanAI`, `SidewalkAI`, `DogAI`, `CatAI`, `CodenameAI`, SensorToolkit `RaySensor`/`RangeSensor`, A* `RichAI`, `TurnTheGameOn.SimpleTrafficSystem.*` (firstpass).
Police: `Police_Manager` (`Activate_Chase`, `AddNewStar`, `NewHouseCalled`, `End_Chase`, `SpawnNewPoliceCar`), `PlayerScript.StartPoliceChase/StopPoliceChase/isPoliceChase`, `GameManager.GotCaught`.
Story/economy/time: `StoryManager` (`story`, `AdvanceStory`, `InitiateStory`, `SaveStory`), `StoryScript`, `Mission_Item.MissionComplete1`, `JobOffer`, `ContractManager`, `Reward`, `HeistManager`, `Shop_Manager.Buy_Item`, `BlackBay_Object.SellIt`, `Darkbank`, `StockManager`, `HouseRenovator`, `UpgradeIt`, `DayNightTime.Update`, `PassTime.Begin`, `Bed`, `AbilitiesManager.HourPassed`.

### Q.2 Save keys (SPrefs) by family (VO; writer → reader)
- Player: `Money`, `Debt`, `Item_<id>`, `XP`, `Lvl`, `MaxXP`, `XPPoints`, `SpecialPoints`, `ExtraPoints`, `ThiefScore`, `BatonLife`, `CrowbarLife`, `BLTHealth`, `DrunkLevel`, `GasAmount`, `Reputation*`, `BankBalance`, `BankIncrease`, `Playtime`, position (`XPlayer…`, `Saved_Pos`), statistics (`StolenItems`, `LocksOpened`, `SafesOpened`, `KnockedOutPeople`, `SpottedPeople`, `TimesCaught`, `MoneyMade`, …), `notepad`, skills, abilities (`Ability*`), cloak, survival (`Hunger`, `Thirst`, `Energy`).
- World/progression: `Story`, `StoryBackup`, `Storage_Item_<id>`, `Locker_Item_<id>`, `ItemDay_<objectName>`, `Item_<objectName>` (unique items), `Item_Discovered_*`, `Item_Value_*`, `<house>_alert` (via queued strings), mission strings, parcel strings, `<location_string>` (alert level), `DailyEvent*`, `EventDay`, `GeneratedEvents`, `NewDay`, `<habitName>_*`, `_spot`, camera discovery prefs, `CurrentHotItem`, `CurrentDayHotItem`, stolen car names, `HeavyItemShed*`, `PosX/Y/Z_item*`, `RotX/Y/Z_item*`, `TrunkItem`, `CurrentCar`, `XCar…`, `GasTankLeftCar`, `CarCompleted_*`, `carpart_*`, upgrades (`_cost`, `lpUpgrade`, `stetUpgrade`, `meatUpgrade`, `planningUpgrade`, `carStorageUpgrade`, `backpack_extra`), renovations (`HouseRepairs`, `Wallpaper*`, `Floor*`), `garage`, `AssociateUnlocked`, `QuickBuyEnabled`, `Contract*`, `Jobs_Complete`, `_complete`, `_week`, payphone (`Payphone*`), `HiddenItem`, `Medkit`, `CameToHome`, `RandomVan`.
- Time: `GameTime`, `Day`, `Week`, `TotalHoursPassed`, `RentTimer`, `Hours`.
- Meta: `CurrentLocation`, `MadisonPoint`, `HillvillePoint`, `LeavePoint`, `FreeMode`, `MainDifficulty`, `TPP`, `Tutorial_*`, `FadeLoad`, `SpawnWarningHardcore`.

### Q.3 Mod sources referenced
`src/Shared/WorldState.cs` (`Regions`, `StateRegistry`, `WorldKeys`, `RegionDirectory`), `src/Shared/WorldTime.cs` (`WorldHost`, `RegionClockBook`, sleep), `src/Shared/Vehicles.cs`, `src/Shared/Travel.cs`, `src/Shared/CarBox.cs`, `src/Shared/Player2Inventory.cs`, `src/Shared/Player2Client.cs`, `src/Plugin/WorldSync.cs`, `VehicleSync.cs`, `CarBoxSync.cs`, `Player2Sync.cs`, `SleepPresenter.cs`, `PlayerSync.cs`, `RemoteAvatar.cs`, `SaveGuard.cs`, `PathRedirect.cs`, `TravelHooks.cs`.

---

## R. W1/W2 verification addendum (2026-11-11, mod 0.9.0)
Corrections / confirmed extensions from the W1/W2 implementation (IL details: CODEBASE_MAP §8):
1. **Correction:** `Item_<objectName>` is not a "unique item" key — the original writes it (= 1) on **every** pick; only objects with `destroy_if_was_picked` read it. Same prefix as the backpack counts `Item_<id>` → reserved for W3, not mirrored.
2. Confirmed: `Pickupable.SetDayRespawn` is the only `ItemDay_` writer; expiry condition `daysInGame < value`; the read happens 0.05 s (realtime) after `Start`.
3. Confirmed: loot names are not randomized at runtime (`Pickupable.RandomName` only fills `generatedHash`); only drops are renamed (`"dropped"`).
4. New: `FBPP.Start` (from `InitializePrefs.Awake`) always re-reads the save file. Which scenes contain `InitializePrefs` is still UNK (static scene data not read); the mirror treats every reload as a new host generation.
5. Duplicate loot names per region (§P question 1) are now counted in game by the census (`lootSharedNames`).
6. §J "hourly ticks run on both instances": day count, daily events and hot item are now host-authoritative on the client (W2); rent, bank interest and fuel charge remain on both (W7).
