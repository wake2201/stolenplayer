# HIDEOUT_SYSTEM — hideout doors, garage purchase, garage effects (W4.2C Phase A investigation)

Date: 2026-11-14. Method: read-only Mono.Cecil IL dump + read-only UnityPy read of `level4` (PlayerHideout). Legend as
in `FURNITURE_SYSTEM.md`. Shared cash is NOT implemented (out of scope).

## 1. Hideout scene contents (`PlayerHideout`, region namespace 0) [V scene counts]
`Door` 43, `CheckRenovations` 67, `StolenCarHideout` 7 (`STOLEN CARS/MECENAS`, `VIXEN`, `MECENAS RS`, `THUNDERDRIVE`,
`PROSPECT`, `ROVER`, `GOLF CART`), `Garage` 1 (`GARAGE SCRIPT`), `CatAI` 1, `PaintingHideout` 4, `HideoutRadio` 2,
`DestroyObject` 7 (chairs, toilet, plates, a beer bottle), many car-mechanic (`CMS_*`) parts.
No `GarageDoor`, `GarageDoorOpen`, `Garage_Switch` or `GarageLocker` in the hideout (those are residence/heist garages).

## 2. Hideout doors
- The 43 hideout doors are ordinary `Door` components (same class/flow as DOOR_SYSTEM) [V].
- Mod: W4.2B shares doors only in namespace 99 (`ObjectStateRules.SupportedNamespace(ns) => ns == 99`) [M] → hideout
  doors are **local** on each instance today.
- The garage area has no moving door script; the garage is a static enable/disable swap (§3).

## 3. Garage purchase [V]
1. World trigger `GARAGE TO DISABLE/Buy garage trigger` (tag `BuyGarage`, BoxCollider + Rigidbody).
2. `PlayerScript.Update` IL_49c1–IL_4a18: ray hit tag `BuyGarage` → prompt; `interact` → `Inventory.tutorial_script.OpenBuyGarage()`.
3. `Tutorial.OpenBuyGarage`: shows the purchase panel; the buy button is enabled when `PS.moneyAm >= 8000`.
4. UI button (UnityEvent, no IL caller) → **`Inventory.BuyGarage()`**: `PS.moneyAm -= 8000` (no second check in the
   method), PMB/PCB disabled, camera off, `FindObjectOfType<Garage>().BoughtSeq()`, buy sound, achievement `BUY_GARAGE`.
5. `Garage.BoughtSeq`: `buyAnimObj` (`GARAGE PURCHASED ANIM`, legacy Animation) on, `garageDisable` off,
   `garageEnable` on, `GM.mission_strings_to_set.Add("garage")`, `GM.boughtGarage = true`,
   `DayNightTime.RefreshLightObjects()`, coroutine `GarageAnim` (4 s → anim object off, PMB/PCB/camera back).
6. Commit: `SaveGame.SaveGameNewSystem` IL_051e–IL_0575 writes every `mission_strings_to_set` entry as
   `SPrefs.SetInt(<key>, 2)` → **`garage = 2`** at the next save. Money is saved separately (`Money`).
7. Load: `Garage.Start`: `SPrefs.GetInt("garage")` → `garageDisable` off / `garageEnable` on; `GameManager.Start`
   IL_0118: `garage >= 1` → `boughtGarage = true`.

Scene swap: `GARAGE TO DISABLE` (old wall colliders, gate, trees, the buy trigger) vs `GARAGE TO ENABLE` (brick fence,
gate, porch, lamps, laptop, colliders) [V].

## 4. What the garage affects [V]
| Reader | Effect |
|---|---|
| `PlayerScript.OpenTownSelectionNoDelay` IL_0067–IL_00f5 | Without `boughtGarage` a stolen car (unless `canBeStolenWithoutGarage`) cannot be delivered ("you_need_garage"); with it the car name goes to `parcel_strings_to_set` → saved as 1 |
| `StolenCarHideout` (7 in the hideout) | `SPrefs.GetInt(car_name)` → shows the delivered stolen car in the garage |
| `StoryManager.Update` IL_0362–IL_0370 | a story step advances when `boughtGarage` (story progression) |
| `SkillConnection.CheckIf` IL_003f | skill-tree nodes gated by the garage |
| `HeistManager.SelectHeist` IL_027e | heist UI text |
| `SecurityPanel.Start` | unrelated name string ("garage" panels with `GarageSwitches`) |

**Campaign-wide persistent state:** yes — `garage` is one save key read at every load, plus derived story/skill/stolen-
car state. It is not per region and not per player.

## 5. Current multiplayer behaviour (derived)
- `garage` and the stolen-car keys are `World` keys (`CampaignPolicy.Classify`) [M]: Player 2 gets the host's value in
  the campaign snapshot and sees the host's garage state at load.
- Player 2 buying: deducts 8000 from Player 2's money (persisted via the Player-2 record), sets `boughtGarage` and the
  swap only in Player 2's instance; `garage = 2` lands only in Player 2's sandbox and is overwritten at the next join →
  **money lost, garage not bought for the campaign** (DEFECTIVE outcome today). Host buying: correct for the host;
  Player 2 sees it only after the next join (no live swap).
- `Inventory.BuyGarage` uses `FindObjectOfType<Garage>()` — callable only inside the hideout scene [V].

## 6. Future implementation (not implemented)
- One host-authoritative **campaign transaction** `buyGarage` (txn id, requester, expected price 8000): host checks
  `garage < 1` and the payer's funds, debits exactly once, commits `garage = 2` on the host side (writes the key and
  `boughtGarage`), replies; both instances apply the swap through `Garage.BoughtSeq` semantics (presentation part only on
  the non-actor: enable/disable objects, `boughtGarage`, `RefreshLightObjects`; no money, no camera lock, no
  achievement on the observer). Duplicate request with the same txn id → same answer (idempotent).
- Who pays: needs the economy decision (shared cash is a later milestone). Until then: **refuse Player 2's purchase**
  (Harmony prefix on `Inventory.BuyGarage` returns false on Player 2 — the original UI then simply does nothing) is the
  safe interim; the host buys as in vanilla.
- Persisting immediately vs at the next save: the original commits at save time (`mission_strings_to_set`). The host
  transaction should follow the host's own save (keep vanilla timing) and publish the committed flag to Player 2 through
  the existing world-key mirror.
- Hideout doors: extend `ObjectStateRules.SupportedNamespace` to namespace 0 after a census digest check (see
  DRONE_SYSTEM §7 / PROGRESS "Other regions").

## 7. Classification
| Aspect | Class |
|---|---|
| Hideout door open/closed | temporary region state (RegionLife), host-authoritative transition |
| `garage`, stolen cars in the garage, renovations | persistent shared campaign state |
| Purchase (8000 debit + flag) | host-authoritative transaction, exactly once |
| Buy animation, camera/input lock | local visual state of the actor |
| Story advance on `boughtGarage` | persistent shared campaign state (host story owner) |
