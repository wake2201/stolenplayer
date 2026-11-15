# 03 — Side missions and independent progression systems

Machine data: `data/mission_catalog.json` → `residences`, `residence_missions`, `side_systems`. Every system below has **its own keys**. Only the links listed under "story link" feed `StoryManager`. They must not be merged into the story index.

## 1. System overview (VERIFIED_IL unless noted)
| System | Classes | Progress keys | Start | Completion / reward | Story link |
|---|---|---|---|---|---|
| Residence ("HellNeighbour") missions | `SelectResidence.residence_missions` (`Mission_Inventory`), `Mission_Item` | `<house>_mission_<i>`: 0 none, 1 accepted, 2 done (key built in `SelectResidence.Start`); counter `Inventory.missionsCompleted` → `MissionsCompleted` | phone/app accept (`Inventory.ResidenceClicked` UI) or `StoryScript.accept_job_on_start` | `Mission_Item.Mission_Complete` (10 callers: photo, plant, destroy, knock-out, feed-dog paths) → after `required_number_complete` steps → `MissionComplete1`: `moneyAm += pay`, `AddMoneyMade`, key = 2, XP, `GameManager.PlayMissionComplete` (`missionsCompleted++`), optional `AdvanceStory` | 5 missions advance stages; stages 30/69 count missions |
| Residence completion | `SelectResidence.RefreshIfComplete` | derived: all `Item_<loot_prefs>` = 1 and all missions = 2 | – | `isComplete` | stages 32, 62, 64 |
| Residence alert | `SelectResidence.WasAlerted/RemoveAlert` | `<residence object name>_alert` (queued in `GameManager.parcel_strings_to_set`, written at checkpoint) | NPC alert | fails an active payphone mission | – |
| Contracts | `ContractManager`, `Contract`, `ContractUIObj` | `Contract`, `ContractActive`, `ContractStatus`, `ContractLevel`, `CurrentRep`, `NextLevelRep`, `<contractPref>_complete`, infinite: `contract_infinite*`, `GeneratedInfinite`, `InfiniteMultiplier`, `ValueMinus`; counter `ContractsComplete` | `ActivateContract(pref)` | `ContractUIObj.ClaimReward` (money/XP/rep, `moneyMade +=`) → `ContractComplete` (`contractsComplete++`, level-up per 1000 rep, new infinite job) | stage 48 (5 contracts) |
| Job offers (Hire!Me) | `JobOffer`, `JobManager`, `Job_Object` | `job_pref` (0/1/2/3), `Jobs_Complete`, `itemXP_pref` | `AcceptJob` (`job_pref += 1`) | `JobManager.JobSuccess` sets prefs 2; `ClaimRewards` pay/XP/rep, `job_pref = 3`, `SaveMoneyAmount` | none |
| Payphone missions | `Payphone`, `PayphoneMission` | `PayphoneMadison`, `PayphoneHillville` (index), live: `PayphoneActive/Minutes/Seconds/Name` (saved by `SaveGameNewSystem`) | `TryActivate` (needs `item_name_required` in backpack; spawns a copy of `itemsToSteal[0]`) | `Success`: pay, XP, index++; `Failure` on any police chase or residence alert | none |
| Heists | `Heist`, `HeistManager`, `HeistButton` | `Story` gate (`requiredStory`), loot prefs, hint prefs, habit prefs (`<habit>_<k>`) | `HeistManager.StartHeist` → `Select<scene>` travel | loot sold normally; `extraProfit` | stages 34/38/72 by direct `story++` |
| BlackBay sales | `BlackBay_Object`, `BlackBay_Item` | `window_pref`, `BlackbayLists` stat; `GameManager.SetItemsBlackBay` keys per object name | phone app | `SellIt`: money, XP, removes items | 7 objects advance the story |
| Pickpocketing | `Pickpocket_Manager`, `Pickpocket_Object` (85 targets) | `<target>_week`, `PickpocketedPeople` | minigame | `Success`: money/items | stage 36 |
| ATM hacking | `ATM_Object`, `ATM_Minigame_*` | `prefs_to_set` (reset weekly in `DayNightTime`) | minigame | `SpawnMoney` | stage 60 |
| Tips (RobTips) | `BuyTip` (220) | `TipPref`, `unlock_cameras[i]` | phone purchase (`BuyTipp`) | habit/camera knowledge | stages 17, 25 |
| Parcels | `ParcelLocker`, `ParcelMinigame`, `ParcelContainer`, `ParcelStorage` | `parcel_code_pref` keys, `ParcelsOpened`; parcel loot uses `mission_strings_to_set`/`parcel_strings_to_set` | code from phone | locker opens, `Pickupable` parcels | none |
| Hot item of the day | `HotItemManager` | `CurrentHotItem`, `CurrentDayHotItem` | daily | sell bonus | none |
| Daily events | `DailyEventManager` | `DailyEvent<n>`, `EventDay`, `GeneratedEvents` | month generation / next day | NPC behaviour (`ApplyEventToAI`) | – |
| Tutorials | `Tutorial`, `TutorialTrigger` | `Tutorial_*`, per-tutorial prefs | trigger/story | UI | stage data `unlock_tutorial` |
| Cat | `CatAI.TouchCat` | `catty1` (week) | touch | 25/100 XP weekly (docs/research/CAT_SYSTEM.md) | – |
| Vehicle theft / delivery | `StolenCarsManager`, `StolenCarHideout`, `CMS_Car`, `Whiplist` | `Item_<car_name>`, `CarCompleted_<name>`, car-part keys | steal car (`LockedCarDoor`, pick gun) | deliver at hideout (`PlayerScript.OpenTownSelectionNoDelay` path), sell at car service | stages 46, 58, 59 |
| Associate | `CodenameAI`, `InviteThief` | `AssociateUnlocked` | story stage 29 | helper modes | – |

## 2. Residences and missions
33 `SelectResidence` objects in MainScene (`IMPORTANT OBJECTS/Canvases/PAUSE MENU CANVAS/RESIDENCES SCRIPTS/*`). Houses 100–116 Madison (116 = Jail), 200–210 Hillville/Museum/SwatRange, 309 Forestside, 401 Warehouse, 402 DrugFarm, 406 Bank, 666 HauntedMansion. 51 residence missions (table below). `required_count` > 0 means a step counter (`current_number`) that is **not persisted** (VERIFIED_IL: no SPrefs write). Partial photo/smash progress is lost on a scene reload.

Multiplayer requirements:
- Accepting and completing must be **host transactions** keyed by `<house>_mission_<i>`.
- The step counter needs live replication while both players work on it.
- Reward (pay, XP, `missionsCompleted`) applies exactly once on the host.
- Client `Mission_Item.MissionComplete1` money is already excluded on the client (W4.1B `WalletPolicy`), but the client still writes its sandbox key and counter today (UNSAFE drift).

| Id / save key | Scene | Objective (EN) | Pay | XP | Lvl | Count | Plant | Advances story | Target objects |
|---|---|---|---|---|---|---|---|---|---|
| `111_mission_0` | MadisonSt | Destroy the washing machine. | 500 | 75 | 9 | 0 |  |  | 1 |
| `111_mission_1` | MadisonSt | Plant a package, unspotted | 750 | 75 | 12 | 0 |  |  | 1 |
| `200_mission_0` | Hillville | Plant a package, unspotted | 2000 | 100 | 9 | 0 | yes |  | 1 |
| `112_mission_0` | MadisonSt | Take a photo of the bedroom. | 400 | 50 | 9 | 0 |  |  | 1 |
| `112_mission_1` | MadisonSt | Plant a note unspotted | 600 | 50 | 10 | 0 | yes |  | 1 |
| `110_mission_0` | MadisonSt | Plant a suspicious package inside the home, unspotted | 250 | 50 | 3 | 0 | yes | story17 | 1 |
| `110_mission_1` | MadisonSt | Photograph the kitchen. | 150 | 50 | 5 | 0 |  |  | 1 |
| `201_mission_0` | Hillville | Destroy the golden deer. | 2000 | 150 | 12 | 0 |  |  | 1 |
| `402_mission_0` | DrugFarm | Photograph several rooms | 1500 | 100 | 9 | 2 |  |  | 2 |
| `402_mission_1` | DrugFarm | Plant a package, unspotted | 1500 | 150 | 16 | 0 | yes |  | 1 |
| `205_mission_0` | Hillville | Knock out the muscular guy. | 2500 | 100 | 16 | 0 |  |  | 1 |
| `208_mission_0` | Hillville | Plant bombs unspotted | 2500 | 50 | 13 | 2 | yes | story49 | 2 |
| `210_mission_0` | SwatRange | Photograph several rooms | 2500 | 100 | 9 | 3 |  |  | 3 |
| `210_mission_1` | SwatRange | Knock out Briggy | 4500 | 150 | 16 | 0 |  |  | 1 |
| `101_mission_0` | MadisonSt | Destroy the AC unit. | 400 | 30 | 5 | 0 |  |  | 1 |
| `101_mission_1` | MadisonSt | Plant a suspicious package inside the home, unspotted | 600 | 50 | 5 | 0 | yes |  | 1 |
| `114_mission_0` | MadisonSt | Knock out the guard. | 1500 | 150 | 16 | 0 |  |  | 1 |
| `114_mission_1` | MadisonSt | Take a photo of a room | 1000 | 150 | 16 | 0 |  |  | 1 |
| `114_mission_2` | MadisonSt | Plant a package, unspotted | 1250 | 150 | 18 | 0 | yes |  | 1 |
| `114_mission_3` | MadisonSt | Destroy the wooden chair | 1250 | 125 | 16 | 0 |  |  | 1 |
| `203_mission_0` | Hillville | Plant a note unspotted | 1000 | 100 | 12 | 0 | yes |  | 1 |
| `203_mission_1` | Hillville | Photograph several rooms | 2000 | 100 | 15 | 3 |  |  | 3 |
| `115_mission_0` | MadisonSt | Photograph inside of the container | 500 | 50 | 13 | 0 |  |  | 1 |
| `108_mission_0` | MadisonSt | Smash a few things | 400 | 100 | 5 | 2 |  |  | 2 |
| `108_mission_1` | MadisonSt | Plant a package, unspotted | 500 | 100 | 8 | 0 |  |  | 1 |
| `204_mission_0` | Hillville | Plant a note unspotted | 2000 | 100 | 0 | 0 | yes | story44 | 1 |
| `401_mission_0` | Warehouse | Plant a package, unspotted | 2000 | 100 | 15 | 0 | yes |  | 1 |
| `109_mission_0` | MadisonSt | Photograph the shed | 250 | 50 | 6 | 0 |  |  | 1 |
| `109_mission_1` | MadisonSt | Smash plates | 500 | 100 | 7 | 0 |  |  | 1 |
| `406_mission_0` | BankScene | Photograph the vault door | 2500 | 150 | 16 | 0 |  |  | 1 |
| `104_mission_0` | MadisonSt | Photograph several rooms | 500 | 50 | 5 | 2 |  |  | 2 |
| `103_mission_0` | MadisonSt | Destroy the garden gnome. | 75 | 25 | 3 | 0 |  |  | 1 |
| `202_mission_0` | Hillville | Plant a package, unspotted | 2000 | 150 | 16 | 0 | yes |  | 1 |
| `202_mission_1` | Hillville | Knock out guard in the suit | 2500 | 150 | 16 | 0 |  |  | 1 |
| `202_mission_2` | Hillville | Destroy the golden deer. | 2000 | 150 | 18 | 0 |  |  | 1 |
| `102_mission_0` | MadisonSt | Destroy the window. | 75 | 50 | 1 | 0 |  |  | 2 |
| `102_mission_1` | MadisonSt | Smash a few things | 250 | 150 | 1 | 3 |  | story7 | 3 |
| `100_mission_0` | MadisonSt | Knock out the guard. | 500 | 100 | 12 | 0 |  |  | 11 |
| `100_mission_1` | MadisonSt | Photograph the security booth interior. | 150 | 50 | 5 | 0 |  |  | 1 |
| `207_mission_0` | Hillville | Feed the dog some meat. | 1000 | 50 | 12 | 0 |  |  | 1 |
| `106_mission_0` | MadisonSt | Take a few photos | 1000 | 100 | 9 | 3 |  | story38 | 3 |
| `106_mission_1` | MadisonSt | Destroy the window. | 1000 | 150 | 9 | 0 |  |  | 1 |
| `309_mission_0` | Forestside Resort | Photograph several rooms | 2000 | 200 | 12 | 3 |  |  | 3 |
| `113_mission_0` | MadisonSt | Take a few photos | 500 | 250 | 0 | 3 |  | story61 | 3 |
| `113_mission_1` | MadisonSt | Destroy the vase | 750 | 100 | 12 | 0 |  |  | 1 |
| `206_mission_0` | Hillville | Destroy the statue. | 1500 | 150 | 12 | 0 |  |  | 1 |
| `206_mission_1` | Hillville | Photograph several rooms | 1500 | 200 | 16 | 2 |  |  | 2 |
| `105_mission_0` | MadisonSt | Photograph the kitchen. | 250 | 50 | 4 | 0 |  | story18 | 1 |
| `105_mission_1` | MadisonSt | Plant a package, unspotted | 250 | 50 | 6 | 0 |  |  | 1 |
| `107_mission_0` | MadisonSt | Destroy the chair. | 500 | 50 | 7 | 0 |  |  | 1 |
| `107_mission_1` | MadisonSt | Feed the dog some meat. | 500 | 50 | 10 | 0 |  |  | 1 |

## 3. Side-system data (generated)

### Contracts (`ContractManager.contracts`, MainScene)

| Pref | House | Any house | Infinite | Rep level | Money | XP | Rep |
|---|---|---|---|---|---|---|---|
| `contract1` | 110 | False | False | 1 | 250 | 50 | 250 |
| `contract2` | 105 | False | False | 1 | 250 | 50 | 250 |
| `contract3` | 104 | False | False | 1 | 250 | 50 | 250 |
| `contract4` | 109 | False | False | 2 | 500 | 100 | 250 |
| `contract5` | 108 | False | False | 2 | 750 | 100 | 250 |
| `contract6` | 107 | False | False | 2 | 600 | 100 | 250 |
| `contract7` | 106 | False | False | 2 | 750 | 100 | 250 |
| `contract8` | 111 | False | False | 3 | 1000 | 100 | 250 |
| `contract9` | 101 | False | False | 3 | 1000 | 100 | 250 |
| `contract10` | 112 | False | False | 3 | 1250 | 100 | 250 |
| `contract11` | 115 | False | False | 3 | 1250 | 100 | 250 |
| `contract12` | 113 | False | False | 4 | 1500 | 150 | 250 |
| `contract13` | 116 | False | False | 4 | 1500 | 150 | 250 |
| `contract14` | 204 | False | False | 4 | 1500 | 150 | 250 |
| `contract15` | 201 | False | False | 4 | 1500 | 150 | 250 |
| `contract16` | 207 | False | False | 5 | 2500 | 200 | 500 |
| `contract17` | 205 | False | False | 5 | 2500 | 200 | 500 |
| `contract18` | 114 | False | False | 6 | 5000 | 200 | 500 |
| `contract19` | 202 | False | False | 6 | 6500 | 200 | 500 |
| `contract20` | 114 | False | False | 5 | 4500 | 250 | 500 |
| `contract21` | 401 | False | False | 5 | 5000 | 250 | 500 |
| `contract22` | 206 | False | False | 5 | 3500 | 200 | 500 |
| `contract23` | 666 | False | False | 6 | 3500 | 200 | 500 |
| `contract24` | 309 | False | False | 6 | 3500 | 200 | 500 |
| `contract_infinite` | 0 | True | True | 1 | 500 | 25 | 50 |

### Payphone chains

- `PayphoneMadison` (MadisonSt), 11 missions, achievement `PAYPHONE_MADISON`:
  - 0: required item `blt`, 7 min, pay 250, XP 50, item houses [105]
  - 1: required item `blt`, 3 min, pay 500, XP 50, item houses [103]
  - 2: required item `lockpick`, 7 min, pay 500, XP 75, item houses [107]
  - 3: required item `lockpick`, 2 min, pay 750, XP 75, item houses [100]
  - 4: required item `lockpick`, 7 min, pay 750, XP 100, item houses [101]
  - 5: required item `lockpick`, 4 min, pay 750, XP 100, item houses [108]
  - 6: required item `automatic_lockpick`, 4 min, pay 1000, XP 100, item houses [109]
  - 7: required item `automatic_lockpick`, 5 min, pay 1000, XP 100, item houses [106]
  - 8: required item `blowtorch`, 3 min, pay 1250, XP 150, item houses [115]
  - 9: required item `automatic_lockpick`, 4 min, pay 1250, XP 150, item houses [112]
  - 10: required item `hackpda`, 4 min, pay 2000, XP 250, item houses [114]
- `PayphoneHillville` (Hillville), 6 missions, achievement `PAYPHONE_HILLVILLE`:
  - 0: required item `automatic_lockpick`, 3 min, pay 1500, XP 100, item houses [206]
  - 1: required item `lockpick`, 2 min, pay 1500, XP 150, item houses [204]
  - 2: required item `hackpda`, 5 min, pay 2000, XP 150, item houses [207]
  - 3: required item `automatic_lockpick`, 4 min, pay 2000, XP 250, item houses [201]
  - 4: required item `hackpda`, 4 min, pay 2000, XP 250, item houses [203]
  - 5: required item `hacking_laptop`, 5 min, pay 4500, XP 250, item houses [202]

### Heists (`MANAGERS/HEIST MANAGER/Heist*`)

| Heist | Location | Required story | Advances story at | Garage | Extra profit | Loot prefs |
|---|---|---|---|---|---|---|
| Shooting range (`gun_range`) | 98 | story30b |  | False | 5000 | 3 |
| Abandoned drug farm (`drug_farm`) | 90 |  |  | False | 5000 | 3 |
| Futuristic museum (`futuristic_museum`) | 96 | story30b |  | False | 15000 | 4 |
| Warehouse 401 (`warehouse_401`) | 91 | story31 | story33 | True | 20000 | 0 |
| Forestside Resort (`forestside_resort`) | 94 | story30b | story30b | False | 18000 | 0 |
| Devil's lair (`devils_lair`) | 95 | story30b |  | False | 5000 | 0 |
| Central Bank (`central_bank`) | 92 | story64 | story66 | False | 50000 | 0 |

### Job offers (Hire!Me)

- `mission_102_toilet`: rep 100, pay 300, XP 200, +rep 50, location Foggy Hills, 102
- `mission_105_window`: rep 500, pay 500, XP 500, +rep 75, location Foggy Hills, 105
- `info (UI template)`: rep 100, pay 200, XP 100, +rep 50, location Foggy Hills, 102
- `mission_container_1`: rep 300, pay 275, XP 300, +rep 50, location Foggy Hills

### BlackBay objects that advance the story

- `blackbay:BRACELET` value 1000, XP 100 → `story37`
- `blackbay:GOLD BARS` value 25000, XP 500 → `story69`
- `blackbay:STORY CAR` value 750, XP 100 → `story43`
- `blackbay:EARRINGS` value 2500, XP 100 → `story30c`
- `blackbay:PACKAGE` value 250, XP 50 → `story16`
- `blackbay:CONF LAPTOP` value 1000, XP 250 → `story47`
- `blackbay:BRIEFCASE` value 100, XP 20 → `story26`

BlackBay objects total: 64; pickpocket targets: 85; ATM objects: 6.

## 4. Multiplayer classification
| System | Today in StolenPlayer | Required authority | Exactly-once effects | Live state |
|---|---|---|---|---|
| Residence missions | MISSING (each instance independent; client reward money excluded) | host transaction | pay, XP, key 2, missionsCompleted, story | step counter, marker objects |
| Contracts | PARTIAL (contract items are shared loot; completion host-only `ClaimReward`, W4.2D) | host | money/XP/rep, contractsComplete | active contract, item possession |
| Job offers | MISSING | host | pay/XP/rep, Jobs_Complete | accepted state |
| Payphone | MISSING (client reward excluded) | host (timer host-owned) | pay/XP, index | timer, spawned target item (runtime entity) |
| Heists | MISSING (travel is synced, `StartHeist` is not) | host + joint travel | story++, habit learning | heist scene for both |
| BlackBay | PARTIAL (client sales counted into the shared wallet; story effect not synced) | host for story objects | money, story | – |
| Pickpocket / ATM / tips / parcels | MISSING (client money excluded or refused, W4.1B) | host | money, weekly keys | target NPC state |
| Hot item / daily events | IMPLEMENTED / NEEDS MANUAL QA (W2 key mirror: `DailyEventManager`, `HotItemManager` writers) | host | – | – |
