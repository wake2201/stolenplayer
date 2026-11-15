# 06 — NPCs, police and AI

Machine data: `data/npc_catalog.json`. Foundation: `docs/WORLD_SYNC_AUDIT.md` §E (NPC architecture, AI field names, perception, authority options) and §F (police). Those findings remain valid. This document adds verified scene counts, the police state machine, the gas response, and the multiplayer scenario analysis.

## 1. NPC classes and instances (generated; VERIFIED_ASSET counts, roles from IL)
| Class | Role | Instances | Spawn | Perception | Reactions |
|---|---|---|---|---|---|
| `AI` | resident/tenant | 100 | fixed scene object bound to SelectResidence.AIs[] | RaySensor sight + RangeSensor hearing + light (ShadowDetector) | flee home + call police (EscapeHomeAndCallCops/Delayed_Police_Call), catch, pepper spray, armed |
| `GuardAI` | patrol guard | 0 in scenes (prefab-only or spawned) | no decoded scene instance (prefab or spawned; UNKNOWN) | RayCast look + HeardNoise | chase/shoot (StartChase), HitSleep |
| `PolicemanAI` | police officer | 3 | spawned by Police_Manager.SpawnNewPoliceCar/SpawnPoliceSimple (dynamic) | Player_Is_Seen/Player_In_Hear_Range sensor events | taser/shoot -> PlayerScript.GetTazed -> GotCaught |
| `CopCar` | police car | 1 | spawned dynamic | PlayerIsSeen | deliver officers |
| `SidewalkAI` | pedestrian | 10 | SidewalkSpawner pool (random) | SpotMe/IsSeen | surprised/escape/catch -> StartPoliceChase(starsAmount) |
| `DogAI` | dog | 0 in scenes (prefab-only or spawned) | no decoded scene instance (prefab or spawned; UNKNOWN) | SpotDog | bark -> alert |
| `CatAI` | cat | 2 | scene object | n/a | TouchCat weekly XP reward (catty1 key) |
| `CodenameAI` | associate (companion) | 1 | scene object (MainScene/Jail) | n/a | helps player; CallOffPolice |
| `MobGuyAI` | special NPC | 3 | scene object | UNKNOWN | UNKNOWN |
| `PriestAI` | special NPC | 1 | scene object | UNKNOWN | UNKNOWN |
| `MannequinAI` | special NPC (mannequin) | 4 | scene object | UNKNOWN | UNKNOWN |
| `DemonAI` | scripted/special | 1 | scene object | UNKNOWN | UNKNOWN |
| `ImpAI` | scripted/special | 1 | scene object | UNKNOWN | UNKNOWN |
| `HelicopterAI` | police helicopter | 1 | spawned by Police_Manager (helicopter field) | UNKNOWN | spotlight |
| `Ambulance` | ambulance | 0 in scenes (prefab-only or spawned) | AmbulanceManager | n/a | n/a |

Residents (`AI`) are fixed scene objects listed in `SelectResidence.AIs[]`. Police officers, police cars, the helicopter and pedestrians are **dynamic** (spawned or pooled) and have no scene identity. Per-scene counts are in the JSON.

## 2. NPC lifecycle (VERIFIED_IL unless noted)
| Aspect | Mechanism |
|---|---|
| Spawn | residents: scene objects; pedestrians: `SidewalkSpawner` (pool, random); police: `Police_Manager.SpawnNewPoliceCar` / `SpawnPoliceSimple` (lists `spawned_policemen`, `spawned_cars`) |
| Despawn | scene unload; police `SomePoliceLeaving` / `One_Police_Less` / car `DriveAway` after a chase |
| Schedule | `AI.<Startt>`: `BehaviourNumber[GameManager.currentHour]`, then `ChangeBehaviour` (walk / sit / bed `Nr3Bed` / out `OutPos` / work); hourly changes in `AI.Update`; daily variations from `DailyEventManager` (`isBurglarDay`, `isChillDay`, `isSickDay`, `isPoliceDay`) |
| Navigation | A* `RichAI agent` (`AstarPathfindingProject.dll`) |
| Animation | legacy `UnityEngine.Animation` with clip-name fields (`WalkAnim`, `RunAnim`, `gasCollapseAnim`, `scaredAnim` …) |
| Sight | SensorToolkit `RaySensor` aimed by `LookAt`; light via `PlayerScript.isHitByLight` (`ShadowDetector`), masks, torch, night factor |
| Hearing | `RangeSensor hearingSensor`; `PlayerScript.MakeNoise` / `AlertNPCs_DistanceBased` → `AI.HeardSound`; footsteps, doors (`Door.Use` noise), glass (`LockedWindow.addNoise`) |
| Suspicion | `peace_amount`, `concernMultiplier`/`addConcern`, `seen_amount_of_times`, `HeardSomething`/`checkItOut`/`explore_home` |
| Missing items / open doors | no verified "notice missing item" logic in `AI` (UNKNOWN). Doors: `AI.OnTriggerEnter` relocks (`Door.ResetLock`), and the NPC context is hooked by StolenPlayer (W4.2B) |
| Break-in reaction | alarm/camera/window → `PlayerScript.StartPoliceChase`; residents flee home and call (`EscapeHomeAndCallCops` → `Delayed_Police_Call` → `Police_Manager.NewHouseCalled`) |
| Incapacitation | gas (`is_inhaling_gas` → `gas_inhaled` → `sleeping_timer` → collapse), crowbar/baton/car hit (`AI.FallAsleep(time, callCops)`), `HitPersonTrigger`; wake up → `callCopsAfterWakingUp` |
| Attack / arrest | armed residents, pepper spray, guards shoot (`GuardAI`), police taser/shoot → `PlayerScript.GetTazed` → `GameManager.GotCaught(reason)` |
| Habits | `NPCHabits`, `AI.DiscoverHabit` (marking skill; stage 7 advances here), keys `<prefsHabitName>_<k>` |
| Reload restore | none: positions and AI state reset; residence `_alert` and location alert level survive (checkpoint) |
| Kill after story | `SelectResidence.killNPCs_afterStory` (house 108 at `story72_end`): `BehaviourNumber[] = 998` |

**Player binding:** every NPC caches exactly one `PlayerScript` (`FindGameObjectWithTag("Player")`) and reads that player's flags. The StolenPlayer remote avatar is script-free and untagged (VERIFIED in mod source). Today NPCs on each instance perceive **only the local player**.

## 3. Police state machine (VERIFIED_IL)
```
idle --StartPoliceChase(stars, ai)--> [if stage.if_detected_respawn: RespawnOnDetect, no chase]
     --Activate_Chase (only if !PS.isPoliceChase)--> chase:
          Location_Manager.SetAlert (current_location >= 0, not easy)   [persistent alert level]
          PS.isPoliceChase = true, UI, siren, SpawnPoliceSimple(1, stars)
chase --StartPoliceChase again--> NewHouseCalled(ai): new house/location -> AddNewStar (max 2) + SpawnNewPoliceCar
chase --officer catches/tasers--> PlayerScript.GetTazed -> GameManager.GotCaught(reason)
          hardcore: reason 0 -> SelectJail ; reason 6 -> lose half money, SelectHideout
          normal: caught screen (CaughtObject) -> checkpoint reload / jail (CODEBASE_MAP 3.9)
chase --escape (Detection_Hide timers)--> End_Chase: isPoliceChase=false, DailyEventManager.SetTodayAsBurglarDay,
          StoryManager.CheckPoliceEscaped (stage 12), achievements, PS.tryToSave (autosave)
```
- **Per player:** `PS.isPoliceChase`, `PS.isSeenByNPC`, `last_known_location` (`Update_Last_Location` from the local player).
- **Per region (scene):** `Police_Manager` instance, spawned units, `current_stars`.
- **Per campaign:** `Location_Manager.location_string` alert/poster level, `<residence>_alert`, burglar day event, stats (`timesPoliceCalled`, `SpottedPeople`).
- Stars: `AddNewStar` caps at 2. The effect of a caller's `stars` argument (e.g. `LockedWindow.starsAmount`) beyond `SpawnPoliceSimple`'s spawn count is UNKNOWN.
- Trigger sources (19 `StartPoliceChase` sites): NPC calls, `RoofAlarm`, `LockedWindow.CheckAlarm`, `Lockpick.Update` (alarm lock), `Security_Panel_Minigame.Failure`, `CameraTrigger`, car alarms, pedestrians, crowbar break-ins, pickpocket caught, story `police_chase_on_start`. `Alarm` has no scene instance in any level (prefab-only or unused).
- Mission-specific: stage 12 `advance_on_police_escape` + `block_jail`; stage 2 `if_detected_respawn`; payphone missions fail on any chase.
- Original co-op hint: `StartPoliceChase` lifts `GM.otherPlayerDrone` when `GM.steamRemotePlay`. The game has a Steam Remote Play helper drone (`PlayerRemoteDrone`). It is not a second thief.

## 4. Essential scenario: Player 1 hidden, Player 2 breaks a window with an alarm
Original chain on the instance where the break happens (VERIFIED_IL):
1. The player's crowbar/glass branch → `LockedWindow.BrokeWindow`:
   - glass swapped or instantiated (`Windows[0]` off, `Windows[1]` on / spawned);
   - the `Door` is enabled and tagged `Door`;
   - optional `AlertNPCsOnPossesion`.
2. `CheckAlarm` (only if `hasAlarm`, which is then cleared):
   - `PS.isSeenByNPC = true`;
   - `Update_Last_Location`;
   - warning `window_alarm_detected`;
   - `StartPoliceChase(starsAmount, residence AIs[0])`;
   - `AlertNPCsOnPossesion`.
3. `Activate_Chase` sets the **location alert** (campaign state) and spawns units hunting the offender's `last_known_location`.

| Layer | Requirement | Authority | Status |
|---|---|---|---|
| Window state (broken glass, door enabled) | region object transition; observers run the glass mirror | host commit (W4.2B) | IMPLEMENTED / NEEDS MANUAL QA (`hasAlarm` cleared before any retag on replicas) |
| Alarm sound / warning | presentation on both; the vanilla warning key only for the offender | event | MISSING |
| NPC investigation / flight | the region's AI simulation runs once and is seen by both | region simulation owner | MISSING |
| Police response | one `Police_Manager` per region; units visible on both | region simulation owner | MISSING |
| Suspect / pursuit target | **Player 2 only** (offender, P2's `last_known_location`); Player 1 becomes wanted only if seen | per-player chase flag | MISSING. The original data is per player, so this is natural |
| Persistent consequence | location alert + residence alert applied once | host | MISSING (client writes its sandbox) |

Do **not** mark both players wanted for one alarm. The original `isPoliceChase` lives on the offender's `PlayerScript`.

## 5. AI authority design constraints (INFERRED from the verified structure)
- **Different regions** never share NPCs. Each region needs exactly one simulating instance: the existing region-owner rule (`RegionClockBook`: host if present, else the client there).
- **Same region:** one owner simulates `AI`/`PolicemanAI`. The observer disables decision and steering (`RichAI`, `AI.Update`) and plays owner snapshots (transform, clip name, state bits). Knock-out, gas, pickpocket and NPC door use become requests to the owner.
- **Perception of the second player:** the owner needs a detectable proxy for the remote player (collider + tag on a sensor layer) **and** a redirect for reactions that read `PS` or call `PS.StartPoliceChase`. This is the largest risk: AI.cs has 3,422 lines and reads dozens of `PlayerScript` fields.
- **Late entry / owner change:** NPC state is not saved in vanilla either. The new owner restarts from scene + hour (vanilla reload semantics). A hand-over snapshot is optional.
- **Simultaneous triggers on one NPC:** owner-serialized, first request wins (the region-object reservation pattern).
- Pedestrians, animals and traffic: low risk. Owner or local simulation are both acceptable (traffic stays local).
