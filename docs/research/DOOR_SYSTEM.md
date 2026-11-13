# DOOR_SYSTEM — original door system (W4.2B investigation)

Date: 2026-11-13. Game: Thief Simulator 2, `Assembly-CSharp.dll` (4101 types). Method: read-only Mono.Cecil IL dump of the
named methods (scratch tool, not in the repo); census counts from the mod's own `WORLD_CENSUS_KINDS` log lines (0.13.0).
Legend: **[V]** verified in IL (offset given where useful), **[M]** verified in the mod source, **[A]** assumption /
inference, not verified, **[Q]** needs an in-game test.

Related: `docs/research/LOCKPICKING_SYSTEM.md` (locks that gate doors), `docs/research/WINDOW_SYSTEM.md` (windows become
`Door` objects after unlocking). Existing slice: `src/Plugin/WorldDoorSync.cs`, `src/Shared/RegionState.cs`.

---

## 1. Classes, fields, methods

### `Door : MonoBehaviour` (60 fields, 21 methods) [V]
One component per movable leaf: house doors, gates, drawers, cupboards, fridges, cabinets, containers, car doors and
windows (type flags below). The same class is used everywhere; `Door_ID` is an empty marker component (purpose unknown).

| Group | Fields |
|---|---|
| Motion | `rotateOpen` + `OpenRot` (euler in a Quaternion), `useVectorRotation` + `openRotation`, `moveOpen` + `OpenMove` (local position); saved closed pose `SaveRot`, `SaveRotVector`, `SavePos` (private, captured in `Start`) |
| State | `isOpen`, `opening` (a swing is running), `canUse`, `openingSlowly`, `load_slow`, `useTim`, `slowMoving`, `openSpeed`, `openSpeed2`, `openingResetTimer`, `playedSlow`, `npcUsing`, `closeAfter`, `closeAfterTimer` |
| Lock / damage | `requires_key`, `only_with_key`, `key_item_ID`, `hadKey`, `was_locked`, `breakin_discovered`, `brokeHinge`, `set_tag_after_unlock`, `lockpickScript` (never written in IL), `unlockSound` |
| Start options | `OpenOnStart`, `randomlyOpen` |
| Type flags | `isWoodenDoor`, `isCarDoor`, `isDrawer`, `isWindow`, `isGate`, `isCupboard`, `isFridge`, `isFileCabinet`, `isGlassCabinet`, `isContainer`, `disableCollisions` |
| Presentation | `DoorSound` (AudioSource), `DoorOpen`/`DoorSqueak`/`DoorClose` clips, `maxVolume`, `speedMultiplier`, `slowOpenSpeedMulti`, `DoorOpenButton` (UI Image, the slow-open fill), `OpenInfo`, `WindowUse` (GameObject, climb trigger of windows), `objName`, `openedByPlayer`, `PS` (the single local `PlayerScript`) |

Methods: `Start`, `Update`, `Use`, `UseFully`, `SlowFailed`, `TimeAdd`, `Resett`, `OpenOnStartt`, `OpenDestroyed`,
`ResetLock`, `Unlock_Key`, `NPCUse(bool)`, `OpenDoorNPC`, `CloseDoorNPC`, `CloseAfter`, `getDoorClip(string)`,
`SetChildren`, coroutines `SetSlowOpenSpeed`, `DelayKey`, `CloseAfterNPC(bool)`.

### Census (Madison Street, 0.13.0 log) [M]
`Door=1142`, `Lockpick=76`, `LockedWindow=184`, 0 ambiguous ids. Hideout `Door=43`, MainScene `Door=16`. How many of the
1142 are windows/drawers/randomly-open is **not** counted yet [Q: census export `World.CensusExport`]. Observed (0.14.0 Local Test, two loads of MadisonSt): 9 and 11 supported doors were open after `Start` (randomlyOpen roll differs per load).

---

## 2. Interaction flow (player input → state)

### 2.1 Interaction ray and gate [V]
`PlayerScript.Update` casts one ray (2.25 m) and branches on the hit transform's **tag** (CODEBASE_MAP §M4.3.1).
Branch `tag == "Door"` (IL_47f2–IL_49bf) requires `GetComponent<Door>()`, `Door.canUse`, `!Door.brokeHinge`. Doors that
are locked carry a different tag (`Lock1`, `ElectricDoor`, `LockedDoorNPC`, `FakeDoor`, `Window`), so the door branch
never sees them.

Inside the door branch:
- Prompt `PlayerScript.DoorOpenInfo` on; if `!isOpen && was_locked` the relock prompt shows, and Rewired button
  `relock_door` (GetButtonUp) → **`Door.ResetLock()`** + `PlayerScript.relockSound.Play()` (IL_48b1–IL_48e5).
- Rewired `interact` down: on foot (`current_vehicle == null`, `canUseDoorsSlowly`) → `PlayerScript.assignDoor = door`,
  `door.load_slow = true`, `opening_slowly = true`, `slowOpenFix` (IL_4935–IL_496c); seated in a car →
  `CarController.ExitVehicle()`.
- Ray leaves the door / E released (`IL_4980`, also IL_4538, IL_7fa5) → `assignDoor.SlowFailed()`, `assignDoor = null`.

### 2.2 Quick vs. slow open [V]
- `Door.Update` while `load_slow`: `useTim += dt`; at ≥ 0.15 s and not yet slow → `load_slow = false`, stop sound,
  `openSpeed = 17 × speedMultiplier`, `openSpeed2 = 0`, `openingSlowly = opening = true`, **`isOpen = !isOpen`**,
  `openedByPlayer = isOpen` (IL_0047–IL_00c9). → slow (quiet) movement.
- `SlowFailed()`: if not slow yet → **`Use()`** (quick toggle). If slow: finish fast (`openSpeed = 150 × mult`, loud
  `DoorOpen`/`DoorClose`, `PS.MakeNoise += 1` wooden / `0.5`, restores `PCB.mouseSensitivity`, `PMB.canMove = true`,
  `DoorOpenButton.fillAmount = 0`).
- `Use()`: requires `useTim < 0.15`, `canUse`, `!npcUsing` → `isOpen = !isOpen`, `openedByPlayer = isOpen`,
  `opening = true`, `openSpeed = 150 × mult`, `canUse = false`, volume `maxVolume`, pitch 1; touches the **local player**
  (`PCB.mouseSensitivity`, `PMB.canMove = true`); unless `PS.noNoise`: `MakeNoise += 1.65` (wooden) / `0.75` and
  `PlayOneShot(DoorOpen | DoorClose)`.
- `UseFully()` (only `VanDoor.UseDoors`): toggle at 175 × mult, no sound, no player effects.
- `TimeAdd()` — no caller in IL [V]; legacy.

### 2.3 Motion and completion [V]
`Door.Update` (IL_0210–IL_05e5): while `opening`, `openSpeed2` ramps toward `openSpeed` (slow: `+dt × 45 ×
slowOpenSpeedMulti`; fast: +250/s below 70 then +100/s); rotates/moves toward the open pose (`OpenRot` / `openRotation` /
`OpenMove`) or back to the saved closed pose with `RotateTowards`/`MoveTowards`; on exact arrival → `Resett()`. Safety:
`openingResetTimer > 3 s` → `Resett()`. Slow branch drives the local UI fill (`DoorOpenButton.fillAmount`) and above
`slowMoving > 0.8` re-enables the local player's movement. **There is no resting partial angle**: every movement ends at
the open or the closed pose.

`Resett()`: `opening = openingSlowly = npcUsing = playedSlow = false`, `canUse = true`, timers 0; windows:
`WindowUse.SetActive(isOpen)` (climb trigger only while open).

### 2.4 NPC use [V]
- `AI.OnTriggerEnter` (IL_0001–IL_0150) for colliders tagged `Lock1`, `Door`, `FakeDoor`, `LockedDoorNPC`, `ElectricDoor`
  with a `Door`: not a window, closed, not moving, wooden or gate → `OpenDoorNPC()` (`npcUsing`, open at 125 × mult,
  `DoorOpen` clip, `closeAfter` with timer −1). **NPCs open locked (`Lock1`) doors** without unlocking them.
  If `was_locked && discoverBreakins && !isChillDay`: `return_home_reset_locks` → **`Door.ResetLock()`** (relock), else
  first time → `breakin_discovered = true` + `AI.Discover_Breakin()` (warning `tenant_discovered_breakin`,
  `Police_Manager.Update_Last_Location_2`, `Delayed_Police_Call`, `NewHouseCalled`).
- `AI.Update` calls `CloseAfter()` (7 sites); `Door.Update` closes after 0.7 s via `CloseDoorNPC()` (125 × mult,
  `DoorClose` clip).
- `PolicemanAI`/`PriestAI.OnTriggerEnter` → `OpenDoorNPC`; `CodenameAI`/`MobGuyAI` → `NPCUse(bool)` (coroutine
  `CloseAfterNPC`: open, wait `maxVolume` s, close, wait 0.75 s, release).

### 2.5 Other writers [V]
- `Door.OpenDestroyed()` ← `PlayerScript.CrowbarBreakin` (crowbar break-in): `isOpen = true`, 170 × mult,
  `canUse = false`, **`brokeHinge = true`** (door unusable for the rest of the visit, the door branch requires
  `!brokeHinge`), `DoorOpen` clip.
- `Door.Unlock_Key()` ← `Lockpick.DoorUnlockKey`: tag `Untagged`, `DelayKey` (0.5 s) → tag `set_tag_after_unlock`,
  `requires_key = false`, `PlayOneShot(unlockSound)`.
- `Door.ResetLock()`: if open → close at 125 × mult with `DoorClose`; `breakin_discovered = false`; own
  `Lockpick.RelockDoor()` if the door carries a `Lockpick`; `hadKey = false`.
- `Lockpick.DoorUnlock/RelockDoor/BreakPadlock`, `LockedWindow.UnlockWindow/BrokeWindow`, `ElectricLock.DisableLock`,
  `Destructible.DestroyObject`, `MediumLockBMaster.Won`, `SafeCrack.UnlockSafe`, `WeldDoor.Update`,
  `LockedCarDoor.UnlockDoor`, `IntercomGate` set door **tags** (`Door`, `Lock1`, `Untagged`) and `enabled`.
- Car doors: `CarController` / `OtherCarController` call `Use()` (already covered by M5.0 `vdoor`).

### 2.6 Start [V]
`Door.Start`: removes `Rigidbody` and `HighlightEffect` unless `isCarDoor`; `slowOpenSpeedMulti = 0.75` (+0.15 ×
skill `slow_opening` after 0.5 s, `SetSlowOpenSpeed`); captures the closed pose; `canUse = true`; caches `PS`,
`PS.DoorOpenInfo`, `PS.SlowOpen`; picks clips per type from `GameManager.doorSounds`; spatial audio (min 2, max 25,
`maxVolume` 0.55 / car 0.4); windows → layer 13; drawers/cupboards/cabinets with `disableCollisions` → layer 11.
**Random open:** if tag is not `Lock1`/`ElectricDoor`, `isWoodenDoor && randomlyOpen` and `Random.Range(0,15) > 11`
→ `OpenOnStart = true`; `OpenOnStart` → `OpenOnStartt()` (snap to open pose, `openedByPlayer = false`, `Resett`).

---

## 3. Animation and audio [V]
- No Animator/Animation: motion is procedural in `Door.Update` (transform rotation/position). `LPDoor` (Animator,
  `ToggleDoor`) and `DoorOpen`/`SlideDoorOpen*`/`InteriorDoorOpen`/`GarageDoorOpen` are separate, unrelated scripts
  (not `Door`); not investigated further.
- Audio on the door's own `AudioSource` (3D): open/close one-shots, slow squeak loop (volume 0.3, pitch 0.9–1.1),
  unlock one-shot; clip names per type (`dooropen normal`, `dooropen quiet`, `doorclose`, `drawer open`, `window open`,
  `gate open`, `car door open`, …).
- Noise for NPC hearing goes only into the local `PlayerScript.MakeNoise` (no world-position noise event).

## 4. Persistence and vanilla reset [V]
- `Door` has **no SPrefs access** in any method; no other class writes a door key. Door state lives only in the loaded
  scene: a scene reload (leaving the region, loading a save, travelling) rebuilds every door from the scene asset, with
  `randomlyOpen` re-rolled per load.
- Within one visit: NPCs relock unlocked doors (`return_home_reset_locks`), close the doors they opened, and discover
  break-ins once (`breakin_discovered`).
- Indirectly persistent effects of door actions: none from `Door` itself (break-in discovery feeds police/alert systems,
  out of scope here).

## 5. Dependencies
- Scene: every region scene (Madison 1142 doors). `PlayerScript`, `GameManager.doorSounds`, Rewired input.
- Story/flags: none in `Door` (story locks are `Lockpick.on_unlock_advance_story`, see LOCKPICKING_SYSTEM).
- Inventory: `requires_key`/`only_with_key`/`key_item_ID` are read only by the `Lock1` branch prompt (key item check
  via `Prompt_Color`) — keys are inventory items.
- Skills: `slow_opening` (local player) changes `slowOpenSpeedMulti` per instance → slow swing speed may differ between
  instances [V]; this is presentation only.

## 6. Local vs. host-authorized

| Operation | Local only | Host-authorized transition |
|---|---|---|
| Ray, prompts, slow-open UI fill, mouse/move lock, noise (`MakeNoise`) | yes (actor) | — |
| Swing motion / audio | runs on each instance through the original `Door.Update` | — |
| Open / close by a player (`Use`, slow toggle, `SlowFailed`) | — | **yes** (already: `wobj2` Phase A, Madison only) |
| Broken hinge (`OpenDestroyed`, crowbar) | — | **yes** (new state bit; irreversible for the visit) |
| Relock by the player (`relock_door` → `ResetLock`) | — | **yes** (it is a lock transition, see LOCKPICKING) |
| NPC open/close (`OpenDoorNPC`, `CloseAfter`, `NPCUse`) | **stays local** until NPC replication (AI1); NPCs run independently on both instances today | later: NPC owner |
| NPC relock (`AI.OnTriggerEnter` → `ResetLock`) | local today (see risk R3) | decision needed |
| `randomlyOpen` at load | rolled per instance | **should be aligned once per region load** (Phase A INCOMPLETE) |
| Car doors | — | existing `vdoor` (M5.0), excluded from `wobj2` |
| Drawers, cupboards, fridges, cabinets, containers | — | W4.2C (same `Door` class, excluded by `WorldDoorSync.Ordinary`) |
| Windows (`isWindow`) | — | after the `LockedWindow` transition (WINDOW_SYSTEM) |

## 7. Different regions (host and client apart) [M]
Phase A design, reused unchanged: the player who has the region loaded simulates it; the host stores the committed
list in `wobj<kind>` (Region scope, arg = region) even when it does not run that scene (unverified ids accepted when the
host has no census of that region — stated trust boundary). A player entering later gets the list silently applied
0.5 s after load (`WorldDoorSync.Reconcile(..., silent)`). When the region empties, `RegionStateBook.ClearRegion`
drops RegionLife states = vanilla reload. Locked doors with a committed "open" are skipped (`SkipLocked`) until their
lock state is applied — W4.2B must reconcile locks **before** doors in the same pass.

## 8. Reusable mod infrastructure [M]
- Identity: `WorldCensus.TryDoorId` / `TryResolve` / `DoorKnown` (W1 census; FNV of scene + hierarchy path + kind).
- Registry + host decisions: `RegionStateBook.Change` (kind/namespace/state/base-version/rate/capacity/catalog),
  `ObjectTxnRequest` (`objTxn`, 16 B), `ObjectStateList` (≤ 100 per kind per region, 804 B), `WorldDoorRules.Decide`.
- Apply bridge: `WorldDoorSync.Swing` = `Door.Use` minus local-player effects (M5.0 pattern, `VehicleSync.SwingDoor`);
  slow replay exists for car doors (`VehicleSync.SwingSlow/FinishFast`, `DoorReplica`) but not for world doors.
- Detection: `PlayerScript.assignDoor` watch (≤ 4 doors) — covers quick/slow use only; `OpenDestroyed` and `ResetLock`
  do not go through `assignDoor` and need their own hook (Harmony postfix, see §10).

## 9. Risks, unsupported cases, uncertainties
- **R1 Capacity:** `MaxPerRegion = 100` per kind; Madison has 1142 doors. A long visit that touches > 100 doors (plus
  randomly-open alignment) is refused (`capacity`) → later doors stay local. [Q] measure typical counts.
- **R2 `randomlyOpen` divergence:** each instance rolls its own; until someone uses the door the two worlds differ
  (known Phase A INCOMPLETE). Count of randomly-open doors per region unknown [Q].
- **R3 NPC relock / NPC doors:** NPC AI runs on both instances independently (no AI replication). A tenant relocking a
  shared-unlocked door happens only on the instance whose NPC walked through → divergence; publishing NPC transitions
  from both instances would double-commit. Needs an owner rule (region owner = host if present) or stays local
  until AI1 — **decision required**.
- **R4 Busy doors:** `Decide` skips doors that are moving or held; NPC `npcUsing` blocks `Use()` on that instance only.
- **R5 `slow_opening` skill and `speedMultiplier`** differ per instance → swing durations differ (cosmetic).
- **R6 `brokeHinge` door** is open and unusable on the actor; a replica must set `brokeHinge` too, otherwise the other
  player could close a door that "should" be broken.
- **R7 Door_ID** marker purpose unknown; not used by any IL reader [V: no fields/methods].
- Unsupported (not `Door`): `LPDoor`, `DoorOpen*`, `SlideDoorOpen*`, `GarageDoor(Open)`, `VanDoor`, `WH_Doors`,
  `NOTLonely_Door.DoorScript`, `RetroCaffe.RC_DoorScript` — scene usage not checked [Q].

## 10. Recommendation (doors part of W4.2B)
1. Extend door state bits: `DoorOpen = 1`, `DoorSlow = 2`, **`DoorBroken = 4`** (brokeHinge; implies open; terminal
   for RegionLife). Replica bridge for broken = `OpenDestroyed` minus nothing (it has no local-player effects) — call the
   original method.
2. Detect `OpenDestroyed` with a Harmony postfix guarded by an "applying remote" flag (no per-frame cost).
3. Keep NPC door use local; do not publish NPC-caused changes (filter by call context, see LOCKPICKING §10).
4. Align `randomlyOpen` per region load: the first loader of a region publishes its rolled open set once (RegionLife),
   later loaders apply it silently — or defer to W4.2C; needs approval because of capacity (R1).
5. Expand the supported namespace beyond Madison only after a census per region (Hillville, Hideout).
