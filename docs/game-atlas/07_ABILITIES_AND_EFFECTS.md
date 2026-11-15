# 07 — Player abilities, tools and effects

Evidence:
- Skill and ability tables are VERIFIED_ASSET (`MainScene` `MANAGERS/SKILL MANAGER` `Skill_Manager.skills`, `MANAGERS/ABILITIES MANAGER`).
- Mechanics are VERIFIED_IL.
- Item ids come from `data/item_catalog.json`.
- Drone deep dive: `docs/research/DRONE_SYSTEM.md`. Lockpicking: `docs/research/LOCKPICKING_SYSTEM.md`.

## 1. Separation used for every effect
1. Player action event.
2. Visual/audio presentation.
3. Physical world object (spawned or moved).
4. Gameplay effect (state change in world/NPC).
5. NPC reaction.
6. Duration and cleanup.
7. Persistence.

A synchronization strategy is chosen per layer, never for the effect as a whole ([11](11_ARCHITECTURE_BLUEPRINT.md)).

## 2. Skills (45 entries in `Skill_Manager.skills`)
| # | Skill | Req. level | Points | Kind |
|---|---|---|---|---|
| 0–3 | Lockpicking L1–L4 | 3, 5, 9, 2 | 1 | main |
| 4, 20, 33 | Perception L1–L3 | 6, 10, 18 | 1/1/2 | main |
| 5 | Pickpocket | 4 | 1 | main |
| 6 | Blowtorch use | 5 | 1 | main |
| 7, 10, 11 | Backpack L1–L3 | 6, 15, 23 | 1/1/2 | main |
| 8, 12, 13 | Electronics L1–L3 | 12, 19, 10 | 1/2/2 | main |
| 9, 21, 22 | Agility L1–L3 | 7, 11, 17 | 1 | main |
| 14–16 | Specialization L1–L3 | 13, 18, 22 | 1/1/2 | main |
| 17–19 | Car theft L1–L3 | 10, 15, 20 | 1/2/2 | main |
| 23–26 | Negotiator, Stealth master, Doughnut time, Unconsciousness | 15, 22, 19, 20 | special | special |
| 27 | Marking | 2 | 1 | main |
| 28 | Safe cracking | 8 | 1 | main |
| 29–30 | Hearing L1–L2 | 7, 16 | special | special |
| 31–32 | Runner L1–L2 | 6, 13 | special | special |
| 34–36 | Item highlight, Backpack enlargement, Zero noise | 5, 10, 15 | – | **timed abilities** |
| 37–44 | A man of strength, Capitalist, Not so cautious, Cautious opener, I'm a hedge, Weightless tools, Weightless car parts, Unstoppable | rating 2–5 + extra points 5,000–50,000 | – | extra skills (`maxUpgrades` 1–5) |

`Skill_Manager.Learn_Skill(int)`, `Check_If_Skill_Learned(string)`. Persistence: skill prefs (`skill_pref`, assigned at runtime, empty in the asset), `SpecialSkillsLearned`, `ExtraPoints`, `SpecialPoints`, `XPPoints` (`SaveGame.Save_XP_And_Level`). Story stages 5, 13, 31, 50, 53, 57 wait for specific skills.

**Timed abilities** (`AbilitiesManager` / `Ability`):
- ability1 = highlight: 5 min active, 10 min cooldown.
- ability2 = backpack: 10 / 20 min.
- ability3 = zero noise: 5 / 20 min.

Ticks come from `AbilitiesManager.HourPassed` (game time, `DayNightTime`). Persistence: `AbilitiesManager.SaveAbilities/LoadAbilities` (in `SaveGameNewSystem`).

## 3. Tools and gadgets (ItemTypeIDs VERIFIED_ASSET)
Tool items:
- 0 crowbar, 1 blt (basic lockpick tool), 2 lockpick, 3 flashlight, 4 binoculars, 5 glasscutter, 6 stethoscope, 7 hackpda, 8 baton, 9 climbing_gloves
- 10 and 19 automatic_lockpick, 11 hacking_laptop, 15 night_goggles, 16 jewellery_tools, 21 atm_hacking_tool, 23 car_lock_pickgun, 24 blowtorch
- 26 sleeping_gas, 27 respirator, 29 keyfob, 49 remote_camera, 50 dog_meat, 51 micro_camera
- 95 tranquilizer_gun, 96 tranq_dart, 98 car_assembly_toolbox
- 330 special_shoes, 338 noisy_talkie, 339 ski_mask, 347 item_plant, 355 plan_tut, 377 shovel, 397 car_storage, 436 cloak_device

Drones: 89 flyby_drone, 185 skyeye_drone, 346 neonbot_drone.

| Tool | Activation (original) | World effect | NPC / police effect | Persistence | Sync layers needed |
|---|---|---|---|---|---|
| Lockpick / auto lockpick / pick gun | `Lockpick.StartLockpicking` minigame (`PlayerScript.Lock_Player_Minigame`) | lock state | alarm locks → police | none; tool wear `BLTHealth` | lock transition (exists), reservation (exists) |
| Crowbar / baton | melee (`PlayerScript` crowbar branch, `crowbar_attack_sensor`) | breakables, windows (`isWindow` → `BrokeWindow`), break-in | `AI.FallAsleep` (knock-out) | `CrowbarLife`, `BatonLife` | swing presentation (W3.1 exists), break transitions, NPC knock-out request |
| Glass cutter | `LockedWindow.BeginCut` | window cut | none (no alarm path) | none | exists (W4.2B) |
| Blowtorch | `Blowtorch` + `WeldDoor` | weld cut | – | none | MISSING |
| Hacking laptop / PDA / ATM tool | `HackingLaptop` panels, `HackingPuzzle` scene, ATM minigame | panels off, ATM money | cameras/alarms off | `HackingDifficulty`, ATM prefs | MISSING |
| Sleeping gas (26) + respirator (27) | held tank, `SleepingGas.Release` toggle (`PlayerScript` lines ~2697/~3580) | particle stream + fog clouds | NPC sleep, police masks, gas detector alarm | `GasAmount` (`PS.gasLeft`) | see §4 |
| Tranquilizer gun (95/96) | shoot | dart | `AI.FallAsleep` | dart count | MISSING |
| Flashlight / night goggles | toggle | light (`PS.torch`) | NPC sight multiplier | none | remote flashlight visual removed in W3.3 (presentation only, by user decision) |
| Remote / micro camera | place camera | camera object | – | UNKNOWN | MISSING |
| Noisy talkie (338) | place / activate | noise source | NPC `HeardSound` (distraction) | UNKNOWN | MISSING |
| Dog meat (50) | throw / feed | – | `DogAI` calm; residence mission "Feed the dog" | – | MISSING |
| Cloak device (436) | `CloakDevice.ActivateCloak` | hand meshes / visibility | NPC sight | `SaveCloak/LoadCloak` | MISSING |
| Shovel (377) | `DigHole` | hole, spawned item | – | `DugHoles` | MISSING |
| Ski mask / special shoes | worn | – | identification (`CheckForMasks`), footsteps (`shoes_itemID`) | item | local (perception owner must read the right player's gear) |
| Medkit (car) | `Medkit` | heals bleeding | – | `Medkit` | local (M4.3.1) |
| Drones (89/185/346) | `Inventory.EquipDrone` → `DroneManager.AssignDrone`; `ActivateDrone` (6 regions only) | drone body (`ItemID.droneObject`, rigidbody) | camera disable (`SecurityCamera.canBeDisabledDrone`), NPC spotting (`JoystickCharacterSeen`) | `Item_<id>`, upgrade prefs, `Drone` | see §5 |

## 4. Sleeping gas (VERIFIED_IL: `SleepingGas`, `SleepGasCloud`, `AI`)
1. **Action:** `SleepingGas.Release()` toggles `is_released`. It plays the valve/air sounds and instantiates `Gas_Prefab` (a particle system) at `gas_position`, then keeps it on the nozzle every frame. A second `Release` stops the particles. Running out (`PS.gasLeft <= 0`) stops them.
2. **Presentation:** particle stream (spawned object, follows the holder), `gas_sound` volume ramp, fog clouds.
3. **Physical object:** a runtime particle object, plus `gas_fog_prefab` instances spawned at the player position every ~8 s while indoors (timer resets to −5 after each spawn). Each cloud carries a `SleepGasCloud` `RangeSensor`.
4. **Gameplay effect:**
   - The nozzle `RangeSensor` (range grows to 15 indoors, shrinks outdoors) sets `AI.is_inhaling_gas = true` for objects tagged `NPC` and calls `GasDetector.TriggerAlarm` for `Gas_Detector`.
   - Indoors the user inhales (`PS.inhaled_gas += 2/s`) unless `respirator_on`.
   - Clouds affect `NPC` (inhale), `Player` (`inhaled_gas += 3/s`), `Policeman` (`WearMask`) and gas detectors.
5. **NPC reaction:** `AI.Update`: `gas_inhaled` rises to 20 (2/s) while inhaling and decays at 1/3 per s. `sleeping_timer` rises at `2*(gas_inhaled+1)` per s, up to 254. The collapse uses `gasCollapseAnim` and `FallAsleep`. The player collapses at high `inhaled_gas`, leading to `GotCaught`.
6. **Duration / cleanup:** clouds and particles are runtime objects (cleanup by their own lifetime; `TimedDestroy` likely: INFERRED). The NPC sleep timer declines over time.
7. **Persistence:** only the fuel (`GasAmount`). NPC sleep is RegionLife.

**Multiplayer requirement:**
- (1) action event `gas on/off` with holder id;
- (2) presentation on the observer: original `Gas_Prefab` attached to the remote avatar's hand, and cloud prefabs at the reported positions (presentation only, sensors disabled);
- (4)/(5) effects computed **only by the region NPC owner**, which must run sensors for the remote holder's nozzle and clouds, or receive "NPC x inhaling" from the holder;
- (6) cleanup is local per instance;
- (7) fuel stays player-owned.

Never let both instances apply `is_inhaling_gas` to their own NPC copies. That reproduces today's divergence.

## 5. Drones (VERIFIED_IL + VERIFIED_ASSET; details DRONE_SYSTEM.md)
- **Items:** 89 flyby (story stage 15 pickup `flyby drone pickup` in house 103, Madison), 185 skyeye (`skyeye pickup`, Warehouse), 346 neonbot.
- **Pickup:** `Pickupable.<PickingItem>` → `ItemID.Item_Picked` + `DroneManager.DronePickedUp` (equip). The story advances at stage 15 (`advance_story_if_picked`, key `story11`).
- **Deploy:** `DroneManager.ActivateDrone`, allowed only in MadisonSt, Warehouse, Hillville, BankScene, Forestside Resort and Museum. It places `droneObj`, switches camera and controls, and blocks saving.
- **Effects:** battery and range; camera disabling (`camerasDisableAmount`, `SecurityCamera.DisableCamera`, story stage 18); NPC spotting.
- **Upgrades:** `DroneUpgradePanel.BuyUpgrade` (money, upgrade pref, optional `AdvanceStory`, stage 16 `DroneTech`).
- **Player 2 cannot collect the house 103 drone because** StolenPlayer blocks both the drone class and story pickups for Player 2 (PROGRESS W4.2D §D). Requirements:
  - host story transaction for stage 15;
  - drone ownership per player (equip on the picker only);
  - drone presentation to the other player (snapshot of the drone pose);
  - camera-disable effects as region object transitions.

## 6. Associate (companion) and special equipment
`CodenameAI` (unlocked at stage 29, `AssociateUnlocked`): follows the player, with modes `openLocksMode`, `heavyCarryMode` (carries a heavy item), `markMode`, and `CallOffPolice`. It binds to the local player and the local car seat (`associateSitPos`). In co-op the associate is campaign content. Whose companion it is when two players exist is an open design decision (UNKNOWN; likely host-owned NPC simulated by the region owner).

## 7. Player states relevant to sync (VERIFIED_IL fields)
`PlayerMotorBehavior` `isGrounded/isMoving/isRunning/isCrouching`; `PlayerScript.isCarrying`/`CarryItem` (heavy), `isHiding`, `is_in_vehicle`/`current_vehicle`, `isPoliceChase`, `isBleeding`, `inhaled_gas`, `passingTimeOn` (sleep), `isInJail`, `having_phone_convo`, minigame locks (`Lock_Player_Minigame`), `hideHands`, `straightenCamera` (window climb), drone active.
- **Already in the M2 snapshot (29 B):** move flags, crouch/run, in-vehicle. Carry and hiding presentation use W3 held/carried objects.
- **Missing:** minigame posture, gas holding, drone flight, injured/bleeding, phone call.
