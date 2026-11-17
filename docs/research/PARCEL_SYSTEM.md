# PARCEL_SYSTEM — parcel lockers, parcel codes, code notes, story clues (W2B investigation)

Date: 2026-11-17. Game build 1.27c. Method: read-only Mono.Cecil IL dump of `Assembly-CSharp.dll` and a read-only scene-file
decoder built on `src/Shared/SceneCatalog.cs` (real `level*` files, every decoded MonoBehaviour consumed its exact byte size).
Legend: **[V]** verified IL / scene data, **[M]** mod source, **[A]** assumption, **[Q]** needs an in-game test.

## 1. Classes [V]
| Class | Role |
|---|---|
| `ParcelStorage` | One instance, MainScene `MANAGERS/PARCEL STORAGE` (= `GameManager.ParcelStorag`): `List<ParcelCode> parcelCodes` (14). Rebuilt with every MainScene load. |
| `ParcelCode` (serializable) | `parcel_code_pref`, `parcel_code` (5 digits), `location`, `lockerTname`, `lockerTransform`, `discoveredOnMap`, `wasOpened`. `OpenLocker()` = `wasOpened = true` on itself and its `ParcelLocker`. |
| `ParcelContainer` | One station per region (`Paczkomat/Trigger Paczkomat`, Madison + Hillville). `Start`: for codes of its `location`: `lockerTransform = GameObject.Find(lockerTname)`, `SPrefs(pref) == 1` -> `discoveredOnMap`. `openSound`. |
| `ParcelLocker` | Locker door: `wasOpened` -> `Update` lerps `localRotation` toward `openRot`. No close, no save. |
| `ParcelMinigame` | Keypad UI. `PressNumberButton` (5 digits): a code with equal digits, its locker not `wasOpened`, `discoveredOnMap` -> `Inventory.parcelsOpened++`, `ParcelCode.OpenLocker()`, `openSound`, quit; else `wrong_code`. Opened from `PlayerScript.Update` IL_56a4 (tag `ParcelLocker`, E). |
| `ReadNote` | Notes. `BeginRead`: `Tutorial.OpenNote(NoteText)`; `addParcelCode` (once per instance): the matching code, not `discoveredOnMap`, `SPrefs(pref) == 0` -> `discoveredOnMap = true`, `Inventory.AddNewParcelCode()` (phone animation flag), `parcel_strings_to_set.Add(pref)`; `setAchievement`; `addPoint` -> `AddXP(xpAdd)` + `SPrefs(<object name>) = 1`; `advanceStory` -> `StoryManager.AdvanceStory()` only when `currentStoryScript.story_description == storyDesc`. `Start`: hides the note when `SPrefs(parcel_pref) >= 2`; `addPoint` off when its name key is 1. |
| `ItemID.HackedElectronic` | `hacked_parcel_pref != ""` and `SPrefs(pref) < 1` -> `parcel_strings_to_set.Add(pref)` (no `discoveredOnMap`: usable after the next save + load). Caller `PlayerScript.Hacking_Complete`. |

## 2. Persistence [V]
`parcel_code_pref` key: 0 unknown, 1 discovered (`parcel_strings_to_set`, written as 1 by `SaveGameNewSystem` IL_04c5..),
2 parcel taken (`Pickupable.lock_parcel_after_pickup`: `mission_strings_to_set.Add` + `parcel_strings_to_set.Remove`, written
as 2). `GameManager.LoadParcels` (`SaveGame.LoadGame`) and `ParcelContainer.Start` set `discoveredOnMap` for value 1 only.
`ParcelCode.wasOpened` / `ParcelLocker.wasOpened` are runtime only (region reload closes the lockers). `Inventory.parcelsOpened`
is a personal statistic (`SaveStats` "ParcelsOpened", achievement `PARCELS`).

## 3. Real data [V scene files]
Codes (pref / digits / station / locker): parcel1_madison 16161 Madison locker1 · parcel_myst2 99517 Madison locker_myst ·
parcel2_madison 25252 locker2 · parcel3_madison 24252 locker3 · parcel4 59444 locker4 · parcel_jail 42318 locker8 ·
parcel106 77611 locker5 · parcel115 69695 locker6 · parcel114 45144 locker7 · parcel201 69999 Hillville locker1 · parcel205
54145 locker2 · parcel202 49637 locker3 · parcel_mysterious 53453 lockerMysterious · parcel_hillville 68611 locker4.

Code notes (`addParcelCode = true`): MadisonSt `DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3` (note_103,
parcel1_madison, **advanceStory story14** = the first story code note), `DOMY/Dom_106/Interior_106/pPlane3` (parcel106),
`bench (9)/pPlane3` (parcel4), `DOMY/Villa/Interior_114/1STFLOOR/pPlane3` (parcel114), `DOMY/DOM 110/Interior_110/pPlane3`
(parcel3_madison); Hillville note_201 (parcel201), note_hillville_park (parcel_hillville), note_hillville_myst
(parcel_mysterious), note_202 (parcel202), note_205 (parcel205); Jail jail_note (parcel_jail); Forestside note_forest_myst
(parcel_myst2). parcel2_madison / parcel115 have no note (hacked electronics `hacked_parcel_pref` / other sources [A]).
About 40 more notes only show text or point at a code without adding it; skill books / lore give XP (`addPoint`).
Parcel loot: Madison 10, Hillville 6 `Pickupable`s with `lock_parcel_after_pickup` + `parcel_pref` (W4.2D shared loot).

## 4. Multiplayer design (implemented in W2B, 0.25.0) [M]
- Codes = persistent campaign state on the host. Shared record `parcels` = the host's live `discoveredOnMap` set. Player 2's
  original discovery is proposed (`parcelCode`: Note / Hack + pref); the host applies the same original mutation once
  (`src/Plugin/ParcelSync.cs`). Every PC sets `discoveredOnMap = true` for shared codes after every change / scene load / join.
- Locker doors = region object kind `ParcelLocker` (22): `ParcelCode.OpenLocker` postfix, replica `WorldObjectBridge.OpenLocker`.
- Parcel loot = existing W4.2D shared loot (one take, taker's inventory, host parcel list mutation).
- Story clue: Player 2's read never runs `AdvanceStory` (no shared story yet, W6); the host's own read keeps it.

## 5. Open
- [Q] Hacked-code path in game (which items carry `hacked_parcel_pref`).
- Shared story steps from notes need W6 (shared missions).
