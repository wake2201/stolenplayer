# 04 — Items and loot

Machine data:
- `data/item_catalog.json`: 452 item types, with world placement counts and flag counts per type.
- `data/instances/<scene>.json`: every `Pickupable` instance, with item id and flags.
- `src/Shared/SceneCatalog.cs` (mod): builds the same loot catalog at runtime from the scene files.

## 1. Item definitions (VERIFIED_ASSET + VERIFIED_IL)
- **Where they live:** `MainScene:ITEM IDs/<id>_<name>` holds 452 `ItemID` components, one per item type. Ids run 0..451, all unique; this is the **ItemTypeID**.
- **Fields:**
  - identity and economy: `item_ID`, `item_name` (internal + localization key), `item_value`, `item_weight`, `add_XP`, `max_amount`;
  - counts: `item_amount` (backpack), `item_amount_storage` (car storage, shared by both player cars), `item_amount_locker` (hideout locker);
  - flags: `isTool`, `isLoot`, `isKey`, `isDrone` (+ `droneObject`), `car_part`, `isCar`, `is_heavy`, `heavy_in_shop`, `is_cardboard` (+ `cardboard_spawn_item_ID`), `is_hackable` (+ `unhacked_item_ID`, `hacked_parcel_pref`), `is_special`, `isStoryItem` (+ `storyName`, `isNotStoryAfterStoryName`), `isMissionItem`, `wanted_on_Blackbay`, `cant_sell`, `isHotItem`, `ableToDiscover`/`discovered`;
  - assets: `drop_object` (prefab in `sharedassets1.assets`), `inventory_image`, `inventory_showcase_object`.
- **Save keys per type** (`ItemID.Start` / `Save_Item_Amount`):
  - counts: `Item_<id>`, `Storage_Item_<id>`, `Locker_Item_<id>`;
  - discovery: `Item_Discovered_<id>`, `Item_Value_<id>`;
  - cars: `CarCompleted_<name>`;
  - global modifiers: `tools_weight`, `car_parts_weight`, `reward_xp`.
- Flag totals: `isLoot` 336, `ableToDiscover` 315, `cant_sell` 73, `car_part` 49, `is_heavy` 43, `isTool` 38, `isKey` 22, `isStoryItem` 19, `is_special` 10, `is_hackable` 8, `isCar` 7, `is_cardboard` 4, `isDrone` 3, `isMissionItem` 1.
- **Caveat:** `Pickupable.item_ID` defaults to 0 (`crowbar`). 431 of the 434 "item 0" world placements are world money (`is_money`, `add_money`), not crowbars. Use `is_money` to tell them apart.

## 2. Separated identities
| Id | Meaning | Never confuse with |
|---|---|---|
| ItemTypeID (`item_ID`) | what kind of thing | an object: two TVs share it |
| WorldObjectID (census) | one pre-placed scene object (`ns:hash`, `[i]` for duplicate sibling names) | the GameObject name (which the original keys use) |
| SaveKey `ItemDay_<name>` / `Item_<name>` | original per-**name** respawn / unique flag | per-object identity (same-name families share it) |
| RuntimeEntityID | dropped / spawned objects (`"dropped"`, payphone targets, gas clouds, police) | a scene identity: a dropped object must not get one |

## 3. Loot sources and lifecycles
| Source | Create | Remove | Reset / respawn | Persist | Evidence |
|---|---|---|---|---|---|
| Pre-placed valuables (`Pickupable`, 2,395 instances in 13 scenes) | scene | `<PickingItem>`: `Item_Picked` → `SetDayRespawn` → Destroy | `<Start>` destroys while `daysInGame < ItemDay_<name>` (tiers 5/4/3/2 days by value, money 5) | `ItemDay_<name>` at checkpoint | IL (PROGRESS W4.2D §B) |
| World money (`is_money`) | scene | pickup adds `add_money` + `Item_<name>` | as above | same | IL |
| Unique (`destroy_if_was_picked`) | scene | pickup | destroyed when `Item_<name>` = 1 | `Item_<name>` | IL |
| Heavy (`is_heavy_item`, 43 types) | scene | `PickUpHeavy` = carry only (no key, no reward) | – | trunk `TrunkItem`, van `PosX_item…`/`HeavyVan_`, shed `HeavyItemShed*`, sale via `HeavyShopTrigger` | IL |
| Random cabinets (`RandomFileCabinet`, 30) | `Start` roll per slot (`Random.Range(0,20) > 17`) | as loot | per load | none | IL + asset |
| Story items (`advance_story_if_picked`, `advance_story_if_placed_car`) | `StoryObject` enables them by stage | pickup → `AdvanceStory` if key matches | gated by `Story` | via story | IL + asset |
| Contract items (`isContractItem`) | scene | pickup → message `contract_item_found` | as loot | contract keys | IL |
| Parcels (`lock_parcel_after_pickup`, `parcel_pref`) | locker | pickup → `mission_strings_to_set` + `parcel_strings_to_set` | parcel keys | checkpoint | IL |
| Cardboard boxes (`is_cardboard`) | heavy box | open at hideout → `cardboard_spawn_item_ID` | – | `CardboardBox_*` pref (story 19) | IL + asset |
| Drone pickups (`ItemID.isDrone`) | scene (`flyby drone pickup` 103, `skyeye pickup` Warehouse) | pickup → `DroneManager.DronePickedUp` | gated | `Item_<id>` | IL + asset |
| Keys (`isKey`, 22) | scene | pickup | `destroy_if_was_picked` | `Item_<name>` | asset |
| Hidden items (`HiddenItem`, 21) | scene | `PickIt` +100 $ | UNKNOWN key | `HiddenItems` stat | IL |
| Dropped items | `Inventory.DropItemNewSystem` instantiates `drop_object` named `"dropped"` | pickup | lost on scene load; at a hideout checkpoint moved to locker (`TryToMoveDroppedItemsToStorage`) | none / locker | IL |
| Car storage / locker | `CarBox` UI | move | – | `Storage_Item_<id>` / `Locker_Item_<id>` | IL |
| Shop purchases | `Shop_Manager.Buy_Item` (fields only, no key until checkpoint) | – | – | `Item_<id>` at checkpoint | IL |
| Payphone target | `Payphone.TryActivate` instantiates a copy of `itemsToSteal[0]` | pickup | – | payphone keys | IL |
| ATM money | `ATM_Object.SpawnMoney` | pickup | weekly | `prefs_to_set` | IL |
| Police/NPC loot | pickpocket items (`Pickpocket_Manager.Success`) | – | weekly `<target>_week` | – | IL |

## 4. Exactly-once transaction requirements per family
| Family | Collector | Host validation | Rewards | Keys changed | Duplicate rejection | Reconnect / reload | StolenPlayer today |
|---|---|---|---|---|---|---|---|
| Ordinary / money / unique / same-name families | either player | census id in the host scene catalog (all scenes, W4.2D) + taken book + host keys | item +1 or wallet + money | `ItemDay_<name>` (max), `Item_<name>` | book: first committed wins | book re-seeded from host keys at occupancy start | IMPLEMENTED / NEEDS MANUAL QA |
| Random cabinet | either | `CabinetBook` slot | as loot | as loot | slot | seeded roll (DESIGN COMPROMISE) | IMPLEMENTED / NEEDS MANUAL QA |
| Contract, parcel | either | as loot + parcel side effect on host | item | parcel strings | book | – | IMPLEMENTED / NEEDS MANUAL QA |
| Heavy | either | ownership lease per object | none at pickup; value at sale/storage | trunk/van/shed keys | lease | carried object must be dropped or stored | **BLOCKED** for P2 (refused) |
| Story pickups | either | host story transaction (key = current stage) | story advance | `Story` | stage check | – | **BLOCKED** |
| Drone pickups | either | drone ownership (per player) | `DronePickedUp` equips the picker | `Item_<id>` + drone assignment | – | – | **BLOCKED** |
| Special / mission / car items | either | per-item effect not traced (`specialAdd` reward, `isMissionItem`) | UNKNOWN | UNKNOWN | – | – | **BLOCKED** |
| Dropped items | owner of drop | dynamic entity id (W3 shared drops) | – | locker at hideout | host entity table | lost at reload like vanilla | IMPLEMENTED / NEEDS MANUAL QA (W3) |
| Shop purchase | buyer | wallet balance (W4.1B `Spend` check) | item | at checkpoint | report sequence | journal | IMPLEMENTED / NEEDS MANUAL QA |
| Hidden items, ATM, pickpocket | either | none yet | money | stat/weekly keys | none | – | MISSING / UNSAFE (client money excluded) |

## 5. Special-item tables (generated)
Types with tool, drone, key, story, mission, special, heavy, car, hackable or cardboard flags. The full list is in the JSON.

| Id | Internal name | EN name | Value | Weight | Flags | World placements |
|---|---|---|---|---|---|---|
| 0 | `crowbar` | Crowbar | 50.0 | 0.1 | isTool | 434 |
| 1 | `blt` | Basic Lockpicking Tool | 50.0 | 0.1 | isTool | 1 |
| 2 | `lockpick` | Lockpick | 20.0 | 0.0 | isTool | 1 |
| 3 | `flashlight` | Flashlight | 25.0 | 0.1 | isTool | 2 |
| 4 | `binoculars` | Binoculars | 4000.0 | 0.1 | isTool | 1 |
| 5 | `glasscutter` | Glass Cutter | 1500.0 | 0.1 | isTool | 1 |
| 6 | `stethoscope` | Stethoscope | 1000.0 | 0.1 | isTool | 1 |
| 7 | `hackpda` | Hacking PDA | 5000.0 | 0.1 | isTool | 1 |
| 8 | `baton` | Baton | 1500.0 | 0.1 | isTool | 1 |
| 9 | `climbing_gloves` | Climbing gloves | 1100.0 | 0.1 | isTool | 1 |
| 10 | `automatic_lockpick` | Automatic lockpick | 3500.0 | 0.1 | isTool | 1 |
| 11 | `hacking_laptop` | Hacking Laptop | 15000.0 | 0.2 | isTool | 1 |
| 15 | `night_goggles` | Night vision goggles | 3000.0 | 0.1 | isTool | 1 |
| 16 | `jewellery_tools` | Jewelry tools | 1500.0 | 0.1 | isTool | 1 |
| 17 | `bracelet_gold` | Gold bracelet | 1600.0 | 0.1 | cant_sell, isLoot, isStoryItem | 2 |
| 18 | `bracelet_gold_parts` | Gold bracelet [Parts] | 1600.0 | 0.1 | isLoot, isStoryItem | 1 |
| 19 | `automatic_lockpick` | Automatic lockpick | 2500.0 | 0.1 | isTool | 1 |
| 21 | `atm_hacking_tool` | ATM Hacking Tool | 3000.0 | 0.1 | isTool | 2 |
| 23 | `car_lock_pickgun` | Carlock pick gun | 5000.0 | 0.1 | isTool | 0 |
| 24 | `blowtorch` | Blowtorch | 2000.0 | 0.1 | isTool | 1 |
| 25 | `cardboard_box` | Cardboard box | 100.0 | 10.0 | ableToDiscover, cant_sell, isLoot, is_cardboard, is_heavy | 1 |
| 26 | `sleeping_gas` | Sleeping gas | 10000.0 | 0.1 | isTool | 1 |
| 27 | `respirator` | Respirator | 1000.0 | 0.1 | isTool | 1 |
| 28 | `old_TV` | Old TV | 220.0 | 5.0 | ableToDiscover, isLoot, is_heavy | 3 |
| 29 | `keyfob` | Car signal hijacker | 10000.0 | 0.1 | isTool | 0 |
| 36 | `super_TV` | Super' TV | 350.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 4 |
| 47 | `key_102` | Key to 102 (front door) | 0.0 | 0.1 | isKey | 0 |
| 48 | `painting_triangles` | Painting 'Triangles' | 120.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 2 |
| 49 | `remote_camera` | Remote camera | 0.0 | 0.1 | isStoryItem, isTool | 0 |
| 50 | `dog_meat` | Dog meat | 25.0 | 0.5 | isTool | 0 |
| 51 | `micro_camera` | Micro Camera | 500.0 | 0.1 | isTool | 0 |
| 57 | `painting_bond` | Painting 'Bond' | 300.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 8 |
| 62 | `key_102_gate` | Key to 102 (front gate) | 0.0 | 0.1 | isKey | 1 |
| 71 | `key_103` | Key to 103 | 0.0 | 0.1 | isKey | 1 |
| 84 | `printer` | Printer | 350.0 | 7.0 | ableToDiscover, isLoot, is_heavy | 4 |
| 88 | `key_101` | 101 storage key | 0.0 | 0.1 | isKey | 0 |
| 89 | `flyby_drone` | Flyby Lite' Drone | 1500.0 | 0.1 | isDrone, isTool | 2 |
| 90 | `cardboard_box` | Cardboard box | 100.0 | 5.0 | ableToDiscover, cant_sell, isLoot, is_cardboard, is_heavy | 2 |
| 91 | `black_package` | Black package | 0.0 | 1.0 | cant_sell, isLoot, isNotStoryAfterStoryName, isStoryItem | 0 |
| 94 | `flyp_smartphone` | Flyp' smartphone | 100.0 | 0.5 | ableToDiscover, isLoot, is_hackable | 3 |
| 95 | `tranquilizer_gun` | Tranquilizer gun | 30000.0 | 0.1 | isTool | 0 |
| 96 | `tranq_dart` | Tranquilizer dart | 200.0 | 0.1 | isTool | 0 |
| 97 | `transmission_zp6` | Automatic transmission ZP6 | 700.0 | 2.0 | car_part, isNotStoryAfterStoryName, isStoryItem | 1 |
| 98 | `car_assembly_toolbox` | Car disassembly toolbox | 2500.0 | 0.1 | isTool | 0 |
| 124 | `vintage_typewriter` | Vintage typewriter | 500.0 | 2.0 | isLoot, is_special | 3 |
| 125 | `key_114` | 114 Key | 0.0 | 0.1 | isKey | 1 |
| 126 | `intro_gate_key` | Key to the back gate | 0.0 | 0.1 | isKey | 1 |
| 127 | `stash_key_110` | 110 Stash key | 0.0 | 0.0 | isKey, isNotStoryAfterStoryName, isStoryItem | 0 |
| 128 | `suspicious_briefcase` | Suspicious briefcase | 0.0 | 0.1 | cant_sell, isLoot, isStoryItem | 1 |
| 134 | `computer` | Computer | 500.0 | 8.0 | ableToDiscover, isLoot, is_heavy | 8 |
| 141 | `basement_key_108` | 108 basement key | 0.0 | 0.0 | isKey, isStoryItem | 1 |
| 143 | `rock_statue` | Rock statue | 200.0 | 1.0 | isLoot, is_special | 1 |
| 150 | `valley_painting` | Valley's painting | 300.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 4 |
| 154 | `super_TV` | Super' TV | 400.0 | 5.0 | ableToDiscover, isLoot, is_heavy | 8 |
| 155 | `key_109` | 109 gate key | 0.0 | 0.1 | isKey | 1 |
| 157 | `deer_painting` | Deer' painting | 300.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 8 |
| 160 | `key_101` | 101 storage key | 0.0 | 0.1 | isKey | 1 |
| 162 | `painting_lighthouse` | Painting 'Lighthouse' | 250.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 8 |
| 164 | `key_101_main` | 101 key | 0.0 | 0.1 | isKey | 2 |
| 171 | `key_107_cabinet` | 107 cabinet key | 0.0 | 0.1 | isKey, isStoryItem | 0 |
| 179 | `painting_sunset` | Painting 'Sunset' | 250.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 7 |
| 183 | `key_107` | 107 key | 0.0 | 0.1 | isKey | 2 |
| 184 | `warehouse_plan` | Warehouse plan | 0.0 | 0.1 | cant_sell, isLoot, isNotStoryAfterStoryName, isStoryItem | 1 |
| 185 | `skyeye_drone` | Skyeye' Drone | 3000.0 | 0.1 | isDrone, isTool | 1 |
| 186 | `forklift_key` | Forklift key | 0.0 | 0.0 | isKey, isNotStoryAfterStoryName, isStoryItem | 1 |
| 200 | `gaming_computer` | Gaming computer | 2500.0 | 8.0 | ableToDiscover, isLoot, is_heavy | 5 |
| 202 | `train_painting` | Train tracks' painting | 150.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 9 |
| 210 | `piano_painting` | Piano' painting | 250.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 4 |
| 211 | `spoons_painting` | Spoons' painting | 300.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 4 |
| 217 | `wall_tv_zoni` | Zoni's flat TV | 600.0 | 8.0 | ableToDiscover, isLoot, is_heavy | 6 |
| 221 | `catalytic` | Catalytic converter | 200.0 | 1.0 | car_part, isNotStoryAfterStoryName, isStoryItem | 4 |
| 227 | `mecenas_benc` | Mecenas-Benc | 1000.0 | 0.0 | cant_sell, carPrefRequiredToSell, isCar, isNotStoryAfterStoryName, isStoryItem | 0 |
| 229 | `conf_laptop_locked` | Confidential laptop | 1500.0 | 2.0 | isLoot, isStoryItem, is_hackable | 1 |
| 230 | `conf_laptop` | Confidential laptop | 1500.0 | 2.0 | cant_sell, isLoot, isStoryItem | 0 |
| 233 | `prospect_car` | Prospect' | 5000.0 | 0.0 | cant_sell, carPrefRequiredToSell, isCar | 0 |
| 234 | `bank_plan` | Bank construction plan | 0.0 | 0.0 | cant_sell, isLoot, isNotStoryAfterStoryName, isStoryItem | 1 |
| 239 | `gold_bar` | Gold bar | 50000.0 | 0.0 | isLoot, isNotStoryAfterStoryName, isStoryItem | 8 |
| 240 | `samlong_tablet` | Samlong's tablet | 175.0 | 2.0 | ableToDiscover, cant_sell, isLoot, is_hackable | 47 |
| 241 | `medi_smartphone` | Medi' smartphone | 150.0 | 0.5 | ableToDiscover, cant_sell, isLoot, is_hackable | 5 |
| 245 | `samlong_TV` | Samlong's TV | 1200.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 6 |
| 248 | `printer_lp` | Lp' printer | 500.0 | 7.0 | ableToDiscover, isLoot, is_heavy | 4 |
| 251 | `retro_computer_svm` | SVM' Retro computer | 2000.0 | 4.0 | isLoot, is_special | 1 |
| 256 | `cardboard_box` | Cardboard box | 100.0 | 5.0 | ableToDiscover, cant_sell, isLoot, is_cardboard, is_heavy | 0 |
| 257 | `printer_3d` | 3D Printer | 2500.0 | 3.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 259 | `female_sculpture_gold` | Golden female sculpture | 3000.0 | 5.0 | isLoot, is_special | 1 |
| 268 | `uphone2` | uPhone 2 | 600.0 | 0.5 | ableToDiscover, cant_sell, isLoot, is_hackable | 6 |
| 277 | `gold_head` | Golden head statue | 2500.0 | 2.0 | cant_sell, isLoot, is_special | 1 |
| 280 | `gaming_laptop` | Gaming laptop | 1000.0 | 2.0 | ableToDiscover, cant_sell, isLoot, is_hackable | 2 |
| 289 | `gold_cash_register` | Golden cash register | 1200.0 | 2.0 | isLoot, is_special | 1 |
| 294 | `computer_five` | Computer '5' | 700.0 | 8.0 | ableToDiscover, isLoot, is_heavy | 2 |
| 295 | `five_TV` | 5' Smart TV | 1400.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 2 |
| 296 | `wall_tv_five` | 5' Wall TV | 750.0 | 8.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 297 | `cabinet_key_203` | 203 cabinet key | 0.0 | 0.1 | isKey | 1 |
| 301 | `old_camcorder` | Old camcorder | 2500.0 | 5.0 | isLoot, is_special | 1 |
| 313 | `golden_dragon` | Golden dragon statue | 2000.0 | 3.0 | isLoot, is_special | 1 |
| 315 | `work_laptop` | Work laptop | 300.0 | 2.0 | ableToDiscover, cant_sell, isLoot, is_hackable | 0 |
| 321 | `rover_rider` | Rover Rider | 1500.0 | 0.0 | cant_sell, carPrefRequiredToSell, isCar | 0 |
| 325 | `thunderdrive` | Thunder drive | 10000.0 | 0.0 | cant_sell, carPrefRequiredToSell, isCar | 0 |
| 330 | `special_shoes` | Quiet shoes | 70000.0 | 1.0 | isTool | 0 |
| 331 | `mecenas_benc_rs` | Mecenas-Benc Sport | 1500.0 | 0.0 | cant_sell, carPrefRequiredToSell, isCar | 0 |
| 335 | `vintage_vixen` | Vintage Vixen GT | 5000.0 | 0.0 | cant_sell, carPrefRequiredToSell, isCar | 0 |
| 337 | `key_105_gate` | 105 gate key | 0.0 | 0.1 | isKey | 1 |
| 338 | `noisy_talkie` | Noisy walkie talkie | 200.0 | 0.1 | isTool | 1 |
| 339 | `ski_mask` | Ski mask | 2000.0 | 0.1 | isTool | 0 |
| 340 | `key_police_exit` | Police station exit key | 0.0 | 0.1 | isKey | 3 |
| 341 | `work_laptop` | Work laptop | 300.0 | 2.0 | ableToDiscover, cant_sell, isLoot, is_hackable | 16 |
| 346 | `neonbot_drone` | Neon Bot's drone | 7500.0 | 0.1 | isDrone, isTool | 0 |
| 347 | `item_plant` | Item to plant | 0.0 | 0.0 | isMissionItem, isStoryItem, isTool | 0 |
| 355 | `plan_tut` | Planning mode | 0.0 | 0.0 | isTool | 0 |
| 370 | `key_201_gate` | 201 gate key | 0.0 | 0.1 | isKey | 1 |
| 371 | `key_206` | 206 key | 0.0 | 0.1 | isKey | 1 |
| 375 | `five_TV_broken` | 5' Smart TV [Broken] | 300.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 376 | `key_309_gate` | 309 gate key | 0.0 | 0.1 | isKey | 1 |
| 377 | `shovel` | Shovel | 500.0 | 0.1 | isTool | 0 |
| 378 | `diamond_earrings` | Diamond earrings | 1000.0 | 1.0 | isLoot, isStoryItem, is_special | 1 |
| 379 | `key_2c_cabinet` | 2C cabinet key | 0.0 | 0.1 | isKey | 0 |
| 380 | `golf_cart` | Golf cart | 3000.0 | 0.0 | isCar | 0 |
| 382 | `key_666_chest` | 666 chest key | 0.0 | 0.1 | isKey | 1 |
| 383 | `cardboard_box` | Cardboard box | 0.0 | 5.0 | ableToDiscover, cant_sell, isLoot, is_cardboard, is_heavy | 1 |
| 388 | `big_grandfather_clock` | Big grandfather clock | 600.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 3 |
| 392 | `wood_coffin` | Wood coffin | 500.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 2 |
| 395 | `ouija_board` | Ouija board | 0.0 | 1.0 | ableToDiscover, cant_sell, isLoot, is_special | 1 |
| 397 | `car_storage` | Car storage | 0.0 | 0.0 | isTool | 0 |
| 398 | `painting_blue` | 'Abstract Blue' painting | 500.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 401 | `painting_blue` | 'Abstract Blue' painting | 400.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 402 | `painting_cockatoo` | 'Parrot' painting | 400.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 405 | `painting_window` | 'Breeze' painting | 400.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 408 | `painting_autumn_valley` | 'Autumn Valley' painting | 500.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 409 | `painting_artistic_sunset` | 'Artistic sunset' painting | 550.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 410 | `painting_purple_winter` | 'Purple winter' painting | 400.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 411 | `painting_volcano` | 'Sunset Volcano' painting | 350.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 414 | `painting_waterfall` | 'Autumn Waterfall' painting | 500.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 415 | `painting_messy` | 'Messy colors' painting | 300.0 | 2.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 416 | `ancient_stone` | Ancient stone (fake) | 2500.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 417 | `ancient_stone` | Ancient stone (fake) | 1000.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 418 | `god_statue` | Ancient God's statue | 2500.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 419 | `granite_statue` | Granite woman statue | 1000.0 | 10.0 | ableToDiscover, isLoot, is_heavy | 1 |
| 436 | `cloak_device` | Cloak device | 150000.0 | 1.0 | isTool | 0 |
