# 01 — Source index

How to find any original class, method, field, save key or scene object without decompiling again. All machine data lives in `data/`. This file is the human entry point. Evidence legend: see [00_README](00_README.md).

## 1. Sources examined
| Source | Path (game root `D:\Thief-Simulator-2-AnkerGames\Thief Simulator 2`) | Method | Coverage |
|---|---|---|---|
| Game logic | `Thief Simulator 2_Data/Managed/Assembly-CSharp.dll` | `ilspycmd -p` once into the scratchpad. The index keeps names/signatures/edges only | 3,336 classes listed by ILSpy; 1,258 game-namespace types indexed (third-party namespaces/prefixes excluded) |
| Save backend | `Managed/FileBasedPrefs.dll` | earlier Cecil reads (CODEBASE_MAP §3.10) | `FBPP`, `FBPPConfig` |
| Scenes | `Thief Simulator 2_Data/level0..level16` | `tools/research/unity_reader.py` + `layout.py` (byte-exact) | 17/17 scenes; 20,758 game-script instances, about 99% decoded |
| Shared assets | `globalgamemanagers.assets` (MonoScripts), `resources.assets` (localization JSON) | PPtr resolution; JSON regex | MonoScript names; 2,377 English strings |
| Prefab-only content | `sharedassets*.assets` | not decoded | UNKNOWN for prefab-internal fields (e.g. `Pickupable` on drop prefabs, police prefabs) |

Not available or not examined: `Assembly-CSharp-firstpass.dll` (traffic system), Rewired, A* internals, SensorToolkit/uGUI UnityEvent wiring, audio/particle assets. No runtime instrumentation was done (the game was not launched).

## 2. Scene table (build order = `levelN`)
| level | Scene | Region id (`CurrentLocation`) | Game scripts | Decoded |
|---|---|---|---|---|
| 0 | MainMenuNew | – | 640 | 630 |
| 1 | MainScene (persistent managers, player, cars, UI) | −1000 (mod namespace) | 5,464 | 5,402 |
| 2 | HackingPuzzle | – | 1,446 | 1,424 |
| 3 | MadisonSt | 99 | 4,793 | 4,732 |
| 4 | PlayerHideout | 0 | 1,424 | 1,384 |
| 5 | Auto Shop | 3 | 22 | 22 |
| 6 | IntroHouse | −1 | 221 | 220 |
| 7 | Warehouse | 91 | 402 | 401 |
| 8 | Hillville | 97 | 3,077 | 3,039 |
| 9 | Jail | 93 | 322 | 322 |
| 10 | BankScene | 92 | 558 | 555 |
| 11 | EndStory (credits) | – | 2 | 2 |
| 12 | Forestside Resort | 94 | 957 | 951 |
| 13 | HauntedMansion | 95 | 826 | 825 |
| 14 | Museum | 96 | 271 | 268 |
| 15 | SwatRange (gun range / office) | 98 | 124 | 122 |
| 16 | DrugFarm | 90 | 209 | 207 |

Verwood (1) has no additive scene; PawnShop (2) has no scene in 1.27c (CODEBASE_MAP §3.9).

## 3. Lookup recipes
| Question | Where |
|---|---|
| Fields / methods / line of a class | `data/source_index.json` → `types[].fields/methods` (`file`, `line`, `end` point into the scratch decompilation) |
| Body of one method | `python -I tools/research/show.py <scratch>/decomp Type.Method` |
| Who calls X | `types[].methods[].calls` (resolved `Type.Method` edges: direct type, local declarations, `GetComponent<T>()`, unique field types). For story/money/XP/save choke points use `data/call_index.json` |
| Which methods read/write save key K | `data/save_keys.json` (raw key expressions, e.g. `"Item_" + item_ID`) |
| Where object class C exists, and how many | `data/world_object_catalog.json` `instances_per_scene`; instance rows in `data/instances/<scene>.json` |
| Serialized values of one scene object | re-run `scan_scenes.py` (scratch `scenes/levelN.jsonl`, full decoded fields) |
| Which objects reference a story stage | `data/mission_catalog.json` `story[].completion.triggers` and `gating_objects` |

Index limits (heuristic parser): call edges through deeply chained expressions, delegates, `SendMessage`, UnityEvents and coroutines started by string are not resolved. `calls` is therefore a lower bound. Always confirm a caller with `show.py` before patching.

## 4. Choke points (VERIFIED_IL; counts from `call_index.json`)
| Method | Callers | Notes |
|---|---|---|
| `StoryManager.AdvanceStory()` | 29 call sites | `story++` + `InitiateStory()`. No-op in free mode. See [02](02_STORY_CAMPAIGN.md) |
| `StoryManager.story` direct writes | `HeistManager.StartHeist`, `PlayerScript` (intro travel menu), `Inventory.EndCheatEdit`, `Credits.Start` (key `Story`) | bypass `AdvanceStory` |
| `SaveGame.SaveGameNewSystem` | 19 | the one full campaign writer, see [09](09_SAVE_AND_CHECKPOINTS.md) |
| `Mission_Item.Mission_Complete` | 10 | residence missions |
| `PlayerScript.moneyAm` stfld | 55 sites / 40 methods | classified in `src/Shared/Wallet.cs` `WalletPolicy` (W4.1B) |
| `PlayerScript.StartPoliceChase(int, AI)` | 19 | police entry, see [06](06_NPC_AND_POLICE.md) |
| `Pickupable.PickUp` / `PickUpHeavy` | 1 each (`PlayerScript.Update`) | interaction ray |

## 5. Core classes by system
| System | Classes (global namespace unless noted) |
|---|---|
| Player | `PlayerScript` (7.9k lines, god object), `PlayerMotorBehavior`, `PlayerCameraBehavior`, `LegsController`, `Inventory` (also settings UI) |
| Story / missions | `StoryManager`, `StoryScript` (data), `StorySequence` (phone calls), `StoryObject`, `AdvanceTrigger`, `AdvanceStoryTrigger`, `IntroMap`, `ActivateEnd`, `Credits`, `SelectResidence`, `Mission_Inventory` (data), `Mission_Item`, `ContractManager`/`Contract`/`ContractUIObj`, `JobOffer`/`JobManager`/`Job_Object`, `Payphone`/`PayphoneMission`, `Heist`/`HeistManager`, `BlackBay_Object`, `Pickpocket_Manager`/`Pickpocket_Object`, `ATM_Object`, `BuyTip`, `Tutorial`, `DailyEventManager`, `HotItemManager` |
| Items | `ItemID` (type definition, 452 under `MainScene:ITEM IDs`), `Pickupable` (world instance), `Shop_Manager`/`Shop_Button`, `CarBox`, `VanHeavyItems`, `HeavyItemsShed`, `RandomFileCabinet`, `HiddenItem` |
| World objects | `Door`, `Lockpick`, `MediumLockMaster`/`MediumLock`, `ElectricLock`, `LockedWindow`, `WindowOpenMaster`, `WindowUse`, `Window_Blinds`, `DestroyObject`, `SecurityCamera`/`CameraTrigger`/`SecurityPanel`, `Alarm`, `GasDetector`, `SafeCrack`, `HidingSpot`, `LightSwitch`, `FuseBoxSwitch`, `Vent`, `DigHole`, `ParcelLocker`, `MailBox`, `ReadNote`, `Bed`/`PassTime` |
| NPC / police | `AI`, `GuardAI`, `PolicemanAI`, `CopCar`, `Police_Manager`, `Location_Manager`, `SidewalkAI`/`SidewalkSpawner`, `DogAI`, `CatAI`, `CodenameAI` (associate), `HelicopterAI` |
| Abilities / tools | `Skill_Manager`/`Skill_Instance`, `AbilitiesManager`/`Ability`, `SleepingGas`/`SleepGasCloud`, `DroneManager`/`DroneTech`/`DroneUpgradePanel`, `PlayerRemoteDrone`, `Blowtorch`, `HackingLaptop`, `CloakDevice`, `Medkit`, `PepperSpray`, `PolicemanTaser` |
| Vehicles | `CarController`, `EVP.VehicleController`, `VehiclesManager`, `EnterCar`, `Whiplist`, `CarPaint`/`ColorButton`, `StolenCarsManager`, `StolenCarHideout`, `CMS_Car`, `CopCar`, `AICar` |
| Save | `SaveGame`, `SPrefs`, `FBPP`, `InitializePrefs`, `GameManager.SaveAll/SaveToProfile/LoadProfile/LoadCheckpoint` |

## 6. Identity vocabulary used throughout the atlas
| Id | Definition | Example |
|---|---|---|
| ItemTypeID | `ItemID.item_ID` = `Pickupable.item_ID` (type, not instance) | 26 = sleeping gas |
| WorldObjectID | mod census id: namespace + FNV-1a(`scene/path#kind`), `[siblingIndex]` only for duplicate sibling names, `@k` for repeated components (`src/Shared/WorldIdentity.cs`) | `MadisonSt:50D2…` |
| RegionID | `SPrefs CurrentLocation` value / mod namespace | 99 Madison |
| SceneObjectPath | Unity hierarchy path. Atlas instance files show display paths without sibling indices | `DOMY/Dom 108 Old/…` |
| RuntimeEntityID | network id for dynamic objects (`DynamicIdAllocator`). The game renames drops to `"dropped"` | – |
| MissionObjectiveID | atlas ids `story:<index>`, `resmission:<house>:<i>`, `contract:<pref>`, `payphone:<pref>`, `heist:<name>` | `story:15` |
| SaveKey | original SPrefs key | `ItemDay_<name>`, `Story`, `108_mission_0` |

The game's own world-object persistence keys use the **GameObject name** (`ItemDay_<name>`, `Item_<name>`, `<name>_alert`, ReadNote `<name>`), not the path. Same-name objects therefore share keys in the original game (PROGRESS v0.16.1 §A).
