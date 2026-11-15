# 08 — Vehicles, physics, heavy items, travel

Authoritative detail lives in `docs/CODEBASE_MAP.md` §3.7 (vehicles), §3.9 (scenes/travel) and the M4.x/M5.0 entries in `docs/PROGRESS.md`. This document is the gameplay-level summary plus the heavy-item lifecycle. **No vehicle code is changed or refactored by this milestone.**

## 1. Vehicle inventory (VERIFIED_IL + VERIFIED_ASSET)
| Vehicle | Object | Identity | Ownership / unlock | Notes |
|---|---|---|---|---|
| Ranger (player car 1) | `MainScene:PLAYER CAR` | `carNr` 0, `VehiclesManager.firstCar`, `CurrentCar` 0 | always | 4 doors + hatch, heavy cargo (`VanHeavyItems`), `CarBox`, medkit |
| Shadowhawk GT (player car 2) | `MainScene:MUSTANG` | `carNr` 1, `VehiclesManager.van` | `Tutorial.BuyVan` −25,000, `VanBought` | coupe, no heavy cargo |
| Stealable region cars | Madison `CAR 112`, `CAR 106`, `thunderdrive`; Hillville `BENC RS`, `VIXEN`, `PROSPECT`; Forestside `GOLF CART RED` | `car_name`, item ids `isCar` (7 types, e.g. 227 `mecenas_benc`, 233 prospect) | steal via `LockedCarDoor` + pick gun / `DrillingCarDoor` | delivered → hideout trophy (`StolenCarHideout`) + `CMS_Car` disassembly; sold at car service; story stages 46, 58, 59 |
| Police | `CopCar` (spawned), `HelicopterAI` | dynamic | – | – |
| Traffic | `TurnTheGameOn.SimpleTrafficSystem.AITrafficCar` (firstpass) | pooled | – | cosmetic |

## 2. Simulation and interaction (VERIFIED_IL)
- **Physics:** Edy's Vehicle Physics (`EVP.VehicleController`: Rigidbody + 4 `WheelCollider`s); `CarController` is the game logic.
- **Entry:** ray tag `SitCar` → `EnterCar.EnterVehicle` → `CarController.EnterVehicle` (no lock check). Exit: `ExitVehicle` / `ExitCar` / `ExitRunningCar`.
- **Engine:** `RunCar` → `Set_TPP(true)` (third-person camera `TPP_Camera_Controller`, driver model `tpp_character`). The travel menu opens on engine start in some regions.
- **Doors:** `Door` `isCarDoor`. `enter_car_object` is active only while a door is open.
- **Damage:** `OnCollisionEnter` (only `is_sitting_inside`): repair cost `min(250, v×3)` deducted immediately; deformation `ImpactDeformable`; NPC hits → `FallAsleep`/ragdoll; police car crash → taser.
- **Fuel:** `SaveGame.gasTankLeft` (`GasTankLeftCar`), auto-refill 60 for $70 ($200 survival). `GasAmount` is the sleeping-gas tool, not fuel.
- **Storage:** one global car storage (`Storage_Item_<id>`, 20 kg + 10 upgrade) shared by both cars.
- **Upgrades / paint:** `Whiplist` (engine, traction, brakes, paint, prefs `PlayerCar1/2`), `ColorButton.BuyColor` (story stage 56).
- **Persistence:** `CurrentCar`, `XCar/YCar/ZCar(+Rot)` (one saved car position), `TrunkItem`, `HeavyVan_*`, upgrade prefs.

## 3. Travel and region transitions (VERIFIED_IL)
- **Travel:** `PlayerScript.ShowTownSelection` → `GameManager.Select<Map>` (writes `CurrentLocation`, `RandomVan`, `Saved_Pos = 0`, `SaveGameNewSystem`) → MainScene reload + additive region scene. The car always comes along, and the player is seated (`CarController.IsStartingCar`).
- **Openers:** car engine start, `MapBorder` triggers (on foot or driving), jail bail door, code paths (`GotCaught`, `HeistManager.StartHeist`, renovations, stolen-car delivery).
- **Blocks:** carrying a heavy item (`travelUnavailable`), active police follow (`escapeCopsFirst`), story `block_*` flags.
- **Fast travel to the car:** phone, same region only, paid before teleport.

## 4. Heavy items: full lifecycle (VERIFIED_IL, PROGRESS W4.2D §B)
| Step | Original method | State | Persistence |
|---|---|---|---|
| Pickup | `PlayerScript.Update` IL_7cfb → `Pickupable.PickUpHeavy(float)` (only caller) | `PlayerScript.CarryItem` / `isCarrying`; tag `HeavyCarried`; object held in front (carry pose `heavy_carry_array`) | none, no reward |
| Carry | `PlayerScript` carry update; `Pickupable.IsObstructed/NotObstructed` | carry animation (remote clip `CarryRun` exists on the avatar rig) | – |
| Drop | `Pickupable.DropHeavy` (callers `StopCarryingItem`, `GetTazed`) | physics object at drop pose | none (lost at reload) |
| Load into car | `Place_Heavy_Trigger.OnTriggerEnter` → `PlayerScript.HeavyItemPlacedInCar` → `Pickupable.PlacedInTrunk` / `WasPlacedInCar` (story `advance_story_if_placed_car`) | Ranger cargo `heavy_item_place[]`, `CarController.heavyItem` | `TrunkItem` (player cars), Ranger `VanHeavyItems.SaveItems` (`PosX_item<i>`…, `HeavyVan_`); `VehiclesManager.SaveAllCarsTrunks` saves only `firstCar` |
| Stolen-count | `VanHeavyItems` / `Inventory.Sell_Item_NewSystem` → `stolenBigItemsNr++` | counter | `StolenBigItems` (story stage 61: 10 heavy items) |
| Hideout storage | `HeavyItemsShed` (`HeavyItemShed*` keys) | shed slots | keys |
| Sale | `HeavyShopTrigger`, `Inventory.FindHeavyInShopAndDestroy` | money | – |
| Mission use | cardboard box (stage 19 `CardboardBox_FlybyLiteDrone`), forklift (stage 39, `forklift` pref) | – | prefs |
| Associate | `CodenameAI.PickHeavy` (`heavyCarryMode`) | associate carries | – |

**Current StolenPlayer state:**
- Player 1 carrying is visible to Player 2 (W3 held/carried object presentation).
- Player 2 is **refused** at `Pickupable.PickUpHeavy` (client prefix, v0.16.1) by design: no heavy transaction family exists. This is not a bug in loot sync. It is a missing feature.
- **H1 (mod 0.18.0, PENDING MANUAL QA):** host-authoritative lease per census heavy object (`wheavy` / `heavyTxn`, `src/Shared/HeavyItems.cs`, `src/Plugin/HeavySync.cs`): both players pick up / carry / drop with the original methods; observers hide the scene copy while it is carried (avatar shows that object) and show it kinematic at the holder's committed rest pose. Steps 1-3 of the architecture below are implemented; deposits (4) and story hooks (5) are H2. PROGRESS H1.
- **H2 (mod 0.19.0, PENDING MANUAL QA):** the Ranger cargo bed (`VanHeavyItems`) is shared: Load / Unload as host transactions (`hcargo` list per car, cargo objects outside the car = H1 records with a synthetic id), presentation = original `drop_object` instances as `LoadItems` creates them, host save in the original `HeavyVan_` keys. Bed loading has no story / `StolenBigItems` effect in the original (`SaveXPForItems` uncalled; the counter grows only at the heavy-shop sale). Trunk slot, shop and shed stay unshared. PROGRESS H2.

**Minimal reusable architecture** (detailed in [11](11_ARCHITECTURE_BLUEPRINT.md) §C):
1. **Ownership lease** per heavy census id. The host grants a lease to one player at `PickUpHeavy`, and the other instance hides or attaches the object to the remote avatar.
2. **Carried presentation:** the existing W3 carried-object path.
3. **Drop:** the lease holder reports the final pose. The host commits a RegionLife relocation, and observers move their copy (kinematic). Physics settles on the holder only.
4. **Deposit** (trunk/van/shed/shop) as host storage transactions on the original car object (CarBoxSync analogue). The counter `StolenBigItems` increments once on the host.
5. **Story hooks** (`WasPlacedInCar`) go through the host story transaction.

## 5. Player state machine relevant to sync
See [07](07_ABILITIES_AND_EFFECTS.md) §7.
- **Synced:** walk/run/crouch/in-vehicle/seat (M2, M4.3).
- **Partly synced:** carrying (presentation), hiding (avatar position only).
- **Missing:** climbing animation (window climb is a local lerp), falling/injured, minigame postures, tool use, drone piloting, phone calls, cutscenes/scripted movement (intro `IntroMap`, ending `EndingFade`).
