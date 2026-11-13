# LOCKPICKING_SYSTEM — original locks and lockpicking (W4.2B investigation)

Date: 2026-11-13. Method and legend as in `docs/research/DOOR_SYSTEM.md`: **[V]** verified IL, **[M]** verified mod
source, **[A]** assumption, **[Q]** needs an in-game test.

---

## 1. Classes, fields, methods

### `Lockpick : MonoBehaviour` — the lock of a door set [V]
Fields: `unlockDoor : Door[]` (doors this lock gates), `LockpickDifficulty` (0 simple / 1 medium / 2 advanced),
`LockType` (passed to `PlayerScript.chooseType`), `isPadlock`, `PadlockDestroy`, `SpawnDust`, `is_tutorial`,
`difficulty` (0.85 − upgrade), `feathering_range` (0.25 + upgrade), `Disable_This_Collider`, `on_unlock_advance_story`,
`story_name`, `hasAlarm`, `lockName`, `reqEq`, `highlightWork`/`highlightt`/`highlightOff`, private `wasOpenedOnce`,
`ActivateUI`, `lockpickScript : MadGoatLockpickMaster`, `PS`, `CI`.

Methods: `Start` (reads SPrefs `bltUpgrade` → difficulty/feathering; lock name texts), `Update` (highlight fade +
**alarm check**), `StartLockpicking`, `DoorUnlock`, `DoorUnlockKey`, `RelockDoor`, `BreakPadlock`, `CheckInventory`.

Census: Madison `Lockpick=76`, Hideout 3, MainScene 1 [M].

### Minigame classes [V]
| Difficulty | Tool (`reqEq`) | Entry (from `StartLockpicking`) | Minigame | Success callback |
|---|---|---|---|---|
| 0 `simplelock` | `blt` (bump/BLT tool) | `PS.chooseType = LockType`, `PS.LockpickEnable()`, `LockpickSounds.NewLockpickScript(this)`, `MadGoatLockpickable.Difficulty/FeatheringRange`, `MadGoatLockpickable.Interact()` | `MadGoatLockpickSystem.MadGoatLockpickMaster` / `MadGoatLockpickable` | `MadGoatLockpickMaster.WinIt` → `MadGoatLockpickable.OnUnlocked` + UnityEvent `onUnlock` → **`LockpickSounds.UnlockDoors()`** → `Lockpick.DoorUnlock()` |
| 1 `mediumlock` | `lockpick` | `PS.LockpickMediumEnable()`, `MediumLock.BeginLockpicking(unlockDoor, is_tutorial, this)` → `Medium_Lock_Spawner.Spawn_New` (instantiates a `MediumLockMaster`, sets `assignedLockpickScript`) | `MediumLockMaster` (pins) | `MediumLockMaster.Unlocked()` → story check → `Lockpick.DoorUnlock()` (not in tutorial) → `QuitWin` |
| 2 `advancedlock` | `automatic_lockpick` | `PS.AutoPickEnable(gameObject)` → `AutoLockpick_Manager.Begin(door)` | `AutoLockpick_Manager` (timed) | `Win_Timed` coroutine → `Lockpick.DoorUnlock()` **once per entry of `unlockDoor[]`** (IL_007f–IL_0095) → `PS.AutoPickDisable()` |

`LockpickSounds.UnlockDoors` has no IL caller → it is wired as a serialized UnityEvent (`onUnlock`) [A, strongly
indicated]. `StartLockpicking` refuses when `unlockDoor[0].npcUsing` (non-tutorial).

Other unlock paths: `PlayerScript.Update` with `CodenameAI.openLocksMode` (companion) → `CodenameAI.UnlockedSomething()`
+ `Lockpick.DoorUnlock()` directly (IL_5b1a–IL_5b30); crowbar break-in (`PlayerScript.CrowbarBreakin`) →
`Lockpick.DoorUnlock()` + `unlockDoor[0].OpenDestroyed()`; key → `Lockpick.DoorUnlockKey()`; padlock crowbar hit →
`Lockpick.BreakPadlock()`.

Related lock types (not `Lockpick`): `ElectricLock` (`doorsToUnlock`, `locked`, `DisableLock` ← `PlayerScript.Update`,
`Hacking_Complete`, `SecurityPanel.PanelDeactivate`; tags its doors `Door`), `IntercomGate.UnlockDoor` →
`DoorUnlockKey`, `MediumLockBMaster.Won` (tags `Door`), `SafeCrack.UnlockSafe`, `WeldDoor`, `LockedCarDoor`,
`Destructible.doorsUnlock`. Out of the W4.2B core; listed for scope.

---

## 2. Interaction flow (input → state)

### 2.1 Prompt and start [V] (`PlayerScript.Update` IL_5804–IL_5b65, tag `Lock1`, `!isLockpicking`)
1. `LockpickActivate` prompt fades in; lock highlight on.
2. Reads `Door` on the hit object (try/catch): if `requires_key` → key prompt (`required_key_prompt_2.required_item_ID
   = key_item_ID`).
3. Crowbar prompt when `currentItem == 1` (crowbar), not tutorial, not `only_with_key`, and (`LockpickDifficulty < 2`
   or `crowbarLevel3`). Rewired `relock_door` while that prompt is shown → `PlayerScript.BreakinCrowbar(hit)`.
4. Tool prompts per difficulty (`lockpick_blt/lockpick/automatic_prompts`).
5. `interact` down: no key prompt → all `Prompt_Color.can_use` (tool owned) → `Lockpick.StartLockpicking()` (or
   companion path). Key prompt and key owned → `Lockpick.DoorUnlockKey()`.

### 2.2 During the minigame (local only) [V]
`LockpickEnable` / `LockpickMediumEnable` / `AutoPickEnable`: disable PMB/PCB, `cursorLock`, UI, `isLockpicking = true`,
`ActivateControls`, and **`SlowdownTime()`**: only when `PlayerScript.rewardTime == 1` (SPrefs `reward_time`) sets
`EnviroTime.cycleLengthInMinutes = 48` (normal 28). `LockpickDisable` / `MedLockDisable` / `AutoPickDisable` →
`isLockpicking = false`, coroutine `LPDisable` (calls `ReturnSkytime` → 28).

Tool wear (local player inventory): MadGoat `OnBegins` sets `PS.bltHealth`; `BreakNew` → `bltHealth`, BLT
`ItemID.item_amount--`, `currentWeight`, break animation, `QuitIt`; `MediumLockMaster.ResetAllLocks` decrements the
lockpick `ItemID.item_amount`; MediumLockMaster reads SPrefs `lpUpgrade`. Cancel: MadGoat `QuitIt` → `OnCancelation`
+ `onCancel`; failure attempt: `OnTryFailed` → `OnFailed` + `onTryFail`.

### 2.3 Success — `Lockpick.DoorUnlock()` [V]
1. If `on_unlock_advance_story` and `currentStoryScript.story_description == story_name` →
   **`StoryManager.AdvanceStory()`** (persistent story progress). `MediumLockMaster.Unlocked` does the same check itself
   before calling `DoorUnlock` [V] (a second advance is prevented only because the story description changed — [A]).
2. `is_tutorial` → return (nothing else changes).
3. Own transform tag → `Untagged` (the lock no longer offers the `Lock1` prompt).
4. For each `unlockDoor[i]`: `enabled = true`, `was_locked = true`, tag `Door`, `requires_key = false`.
5. `Disable_This_Collider` → own `BoxCollider.enabled = false`.
6. First time only (`wasOpenedOnce`): `Inventory.openedLocks++` (statistic saved by `Inventory.SaveStats`).

The door is **unlocked but stays closed**; the player then opens it through the normal door branch.

### 2.4 Alarm [V] (`Lockpick.Update`)
While `hasAlarm` and the Lockpick's **own** tag is `Door` (the Lockpick sits on a door that is in its own
`unlockDoor[]`, so step 4 retags it): `hasAlarm = false`, `PS.isSeenByNPC = true`, `Police_Manager.Update_Last_Location`,
warning `door_alarm_detected`, `PS.StartPoliceChase(2, residence AI[0] | null)`. `SecurityPanel.PanelDeactivate` clears
`hasAlarm` (alarm panel, W4.2C).

### 2.5 Key, padlock, crowbar, relock [V]
- `DoorUnlockKey()` → every `unlockDoor[i].Unlock_Key()` (tag `Untagged`, 0.5 s later `set_tag_after_unlock`,
  `requires_key = false`, unlock sound). Does **not** set `was_locked` (NPCs will not relock or detect it).
- `BreakPadlock()` (crowbar hit, `PlayerScript.CrowbarHit` IL_0a3c–IL_0a92, `isPadlock`): padlock `Rigidbody`
  non-kinematic (falls), `BoxCollider` off, dust prefab instantiated, own tag `Untagged`, doors tagged `Door`,
  `MakeNoise += 5`. No `was_locked`, no story, no stat.
- Crowbar break-in (`CrowbarBreakin` coroutine): `crowbarLife` wear, local animation/sounds/shake → `DoorUnlock()` +
  `unlockDoor[0].OpenDestroyed()` (door forced open, `brokeHinge`); noise → `AlertNPCs_DistanceBased`, residence with
  `hasSoundSecurity` → `StartPoliceChase` + `sound_detected`; crowbar may break (`ItemID.item_amount`).
- Relock: player `relock_door` on a closed `was_locked` door → `Door.ResetLock()` → `Lockpick.RelockDoor()`: each door
  `enabled = true`, `was_locked = false`, tag `Lock1`; own tag `Lock1`; `Disable_This_Collider` → collider on. NPC
  tenants with `return_home_reset_locks` call the same `ResetLock` (`AI.OnTriggerEnter` IL_0150).

## 3. Animation and audio [V]
The minigames are first-person UI/camera scenes on the actor's PC (`LockpickRenderCamera`, pin animations
`Pin_Win_Rotate`, `LockUnlock`, sounds on `LockpickSounds`/`MadGoatLockpickMaster.TurnSound`/`AutoLockpick_Manager`).
The original has **no third-person lockpicking animation**; the mod's remote avatar has only Idle/Walk/Run/Sneaking
clips [M `RemoteAvatar.cs`]. Audible world effects: `Door.unlockSound` (key), padlock fall + dust, crowbar sounds on
the actor's `PlayerScript` (local AudioSources).

## 4. Persistence and vanilla reset [V]
- `Lockpick`, `MadGoat*`, `MediumLock*`, `AutoLockpick_Manager` write **no SPrefs** (only read `bltUpgrade`/`lpUpgrade`).
  Lock state = scene tags + `Door` fields → reset on every scene load. Within a visit, tenants may relock.
- Persistent side effects of a successful unlock: story advance (story locks only), `Inventory.openedLocks` statistic,
  tool wear (`ItemID.item_amount`, `bltHealth`, `crowbarLife` — saved through the inventory/stat save).

## 5. Dependencies
Scene (per region), story (`StoryManager.currentStoryScript.story_description`), inventory (tools: BLT, lockpick,
automatic lockpick, crowbar; door keys `key_item_ID`), upgrades (`bltUpgrade`, `lpUpgrade`, `crowbarLevel3`), reward
`reward_time`, companion `CodenameAI.openLocksMode`, residence (`currentPossesionScript`, AIs) and police for alarms.

## 6. Local vs. host-authorized

| Operation | Local (actor) | Host transition |
|---|---|---|
| Prompts, minigame UI/camera/input, time slowdown, success/failure/cancel | yes | — |
| Tool wear / break (P2 backpack) | yes; the change flows through the existing Player-2 inventory reports (`Player2Sync`, host-owned P2 record) [M] | via P2 record (existing) |
| Unlock (`DoorUnlock` effects 3–5) | — | **yes**: lock state `Unlocked` |
| Key unlock (`DoorUnlockKey`) | — | **yes**: `KeyUnlocked` (no `was_locked`) |
| Padlock broken | — | **yes**: `PadlockBroken` (terminal) |
| Crowbar break-in | noise/police on actor | **yes**: lock `Unlocked` + door `DoorBroken` |
| Player relock | — | **yes**: lock back to `Locked` (version bump) |
| NPC relock | local today | decision (see R3) |
| Story advance (`on_unlock_advance_story`) | runs on the actor's instance only | **not covered** — shared story is W6 |
| `Inventory.openedLocks`, achievements | actor only | never replicated |
| Door alarm → police chase | actor's instance | police is W9; the replica must **not** fire it (R2) |

## 7. Different regions [M + design]
Same mechanism as doors: the actor's instance runs the minigame; the transition is proposed with the expected version;
the host commits into `wobj3` (Lockpick kind, Region scope) even if it does not run that scene; whoever loads the region
later applies the committed lock states silently before door states. Region empty → RegionLife cleared = vanilla.

## 8. Reusable mod infrastructure [M]
- Census already registers `Lockpick` (kind 3) objects with stable ids (`WorldIdentity.cs` KindInfo "lock on a door
  set; NPCs relock") but `WorldCensus` builds an instance→id map only for doors (`_doorIds`, `TryDoorId`) → add the same
  for Lockpick/LockedWindow.
- `RegionStateBook`, `ObjectTxnRequest`, `objTxn` proposal path, `ProposalGate` dedup, rate limit, catalog check:
  kind-generic already; `ObjectStateRules.SupportedKind/ValidState/SamePhysical` are door-only and need per-kind rules.
- Player-2 tool consumption: `Player2Sync` host-owned inventory (no new work).
- `CampaignBootstrap` already treats `isLockpicking` as "not a safe position report" [M].

## 9. Risks, unsupported cases, uncertainties
- **R1 Story locks** (`on_unlock_advance_story`): a P2 unlock advances only the client's sandbox story; the host story
  does not move. Until W6 these locks must not be shared (or must be refused for P2) — **decision required**.
- **R2 Alarm on the replica:** applying the unlock by retagging a door that carries its own `Lockpick` with `hasAlarm`
  makes `Lockpick.Update` on the **replica** start a police chase for the replica's local player. The bridge must clear
  `hasAlarm` before retagging (the actor's original path already consumed it).
- **R3 NPC relock:** independent NPC simulation on both instances; see DOOR_SYSTEM R3.
- **R4 Concurrency:** both players pick the same lock: first committed wins; the second success becomes `Unchanged`.
  A remote unlock arriving while the local player is inside that lock's minigame must be deferred until the minigame
  ends (`isLockpicking`), otherwise the local completion would run `DoorUnlock` again locally (harmless for tags; may
  double-count `openedLocks`, story already handled by R1).
- **R5 Auto lockpick calls `DoorUnlock` once per door** in `unlockDoor[]` → the detection hook must be idempotent
  (dedupe per lock per frame / by committed state).
- **R6** `LockpickSounds.UnlockDoors` wiring via UnityEvent is inferred [A]; detection should hook `Lockpick.DoorUnlock`
  itself, which covers all minigames, the companion and the crowbar.
- **R7** [observed 2026-11-13, Local Test: a Madison `Lockpick` on `Door_0_Mesh`, i.e. on the door itself, exists] Whether a given `Lockpick` sits on the door object itself or on a separate lock object varies per prefab
  (`Door.ResetLock` uses `GetComponent<Lockpick>` on the door; padlocks are separate rigidbodies) [A]; and whether
  locked doors start with `Door.enabled = false` is not visible in IL (`DoorUnlock`/`RelockDoor` set `enabled = true`)
  [Q] census export of tags/enabled per region.
- **R8 Trust boundary:** the host cannot verify that the minigame was actually won (it may not even run the scene) —
  same stated boundary as Phase A.
- **R9 Time slowdown** with `reward_time`: `cycleLengthInMinutes = 48` on the actor's instance while picking; the mod's
  region clocks are host-authoritative and correct drift (`WorldTime.NominalRate` assumes 28) [M] — possible small clock
  corrections after long minigames [Q].
- Unsupported: `ElectricLock`, `IntercomGate`, `SafeCrack`, `WeldDoor`, `LockedCarDoor`, `MediumLockBMaster`,
  `Destructible.doorsUnlock` — W4.2C; tutorials (`is_tutorial`) — never shared.

## 10. Recommendation (locks part of W4.2B)
1. New kind rules for `ObjectKind.Lockpick` (RegionLife): state `0 Locked`, `1 Unlocked` (was_locked set),
   `2 KeyUnlocked`, `3 PadlockBroken`. Valid transitions: Locked→any unlocked state; Unlocked/KeyUnlocked→Locked
   (player relock only); PadlockBroken terminal.
2. Detection: Harmony postfixes on `Lockpick.DoorUnlock`, `Lockpick.DoorUnlockKey`, `Lockpick.BreakPadlock`,
   `Door.ResetLock` (the only caller of `Lockpick.RelockDoor`; it relocks only when the `Lockpick` sits on that door's
   own GameObject, otherwise it just closes the door [V]), each skipped while the mod applies a remote state; a prefix/
   postfix flag on `AI.OnTriggerEnter` marks NPC-caused relocks (not published). Skip `is_tutorial` and, until W6,
   `on_unlock_advance_story` locks.
3. Replica bridge = the state subset of the original methods only: tags, `Door.enabled`, `was_locked`,
   `requires_key`, collider, padlock rigidbody/collider (+ dust prefab, optional), `hasAlarm = false` first — no story,
   no stats, no noise, no police, no tool wear. Each bridge documented with the IL it mirrors (CLAUDE.md rule).
4. One `WorldLockSync` reconcile pass per registry change: locks → windows → doors (doors skipped as `SkipLocked`
   otherwise).
