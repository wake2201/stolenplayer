# WINDOW_SYSTEM — original windows (W4.2B investigation)

Date: 2026-11-13. Method and legend as in `docs/research/DOOR_SYSTEM.md`: **[V]** verified IL, **[M]** verified mod
source, **[A]** assumption, **[Q]** needs an in-game test.

---

## 1. Classes, fields, methods

### `LockedWindow : MonoBehaviour` — a closed, locked window [V]
Fields: `Windows : GameObject[]` (`[0]` intact glass, `[1]` broken-glass prefab/object), `brokeGlass` (private),
`canBreak` (default true), `isWindow`, `isTutorial`, `hasAlarm`, `starsAmount` (default 2), `alertNPCs` (default true),
`addNoise` + `noiseAmount`, `instantiateGlass` (default true), `unlockFromInside` (default true), `noDoor`,
`enterablesEnable`, `unconvexCollider`, `complete_mission`, `assigned_residence`, `WOM : WindowOpenMaster`, `PS`,
`highlightt`, `highlightOff`.
Methods: `Start`, `Update` (empty), `BeginCut`, `UnlockWindow`, `UnlockWindowNoAlarm`, `BrokeWindow`, `CheckAlarm`.
Census: Madison `LockedWindow=184`, Hideout 1, MainScene 3 [M]. Observed (0.14.0 census, 2026-11-13): all 184 Madison LockedWindows carry their sash `Door` on the same GameObject. After the original `UnlockWindowNoAlarm` on `O-Window-2_L` the sash `Door.isOpen` read true (committed window state 5 = inside + sash open) [observed, one window].

### Related [V]
- `Door` with `isWindow = true` — the openable sash once unlocked (layer 13, clips `window open`/`doorclose`;
  `Door.WindowUse` GameObject active only while open, set in `Door.Resett`; `Door.Update` hides it while moving).
- `WindowOpenMaster` — glass-cutting minigame (`BeginCutting(LockedWindow, bool)`, `Update`, `QuitCut`, `DelayWin`,
  `DisableIt`; fields `LW`, `rotated_count`, `won`, `isTut`, …), found by tag `WindowOpenMaster`.
- `WindowUse` — climb through an open window (`UseWindow` → coroutine `GetIn`; moves the **local** player along
  `Move1`/`Move2`, crouch, camera; sounds `MovementSound`).
- `Brick` (`OnTriggerEnter`, tags `Window`/`PossesionTrigger`), `PlayerScript.CrowbarHit`, `PlayerScript.CrowbarBreakin`.
- `GetAllWindows.EnableAlarmsOnAllWindows` (sets `hasAlarm` on all windows; no IL caller → UnityEvent/story [A]),
  `SecurityPanel.PanelDeactivate` (clears `hasAlarm`), `WindowExplosion`, `Window_Blinds` (cosmetic), `OnPossesion`
  (tag `Window`).

---

## 2. Interaction flow (input → state)

### 2.1 Ray branch `tag == "Window"` [V] (`PlayerScript.Update` IL_7dc4–IL_7f31)
1. `rayHitWindow = true`. If `ceilingDetect` (player is inside, under a ceiling [A from name]) and
   `unlockFromInside` → **`UnlockWindowNoAlarm()`** immediately (no key press needed).
2. `WindowCutInfo` prompt; crowbar equipped (`currentItem == 1`) and not tutorial → crowbar prompt; Rewired
   `relock_door` → `BreakinCrowbar(hit)`.
3. `interact` with both `Prompt_Color` checks met (glass cutter etc.) → `LockedWindow.BeginCut()` (or companion
   `openLocksMode` → `CodenameAI.UnlockedSomething()` + `UnlockWindow()`).

### 2.2 Glass cutting [V]
`BeginCut` → `PlayerScript.WindowCutEnable()` (input lock, `SlowdownTime` with `reward_time`, like lockpicking) +
`WindowOpenMaster.BeginCutting(this, isTutorial)`. `WindowOpenMaster.Update` counts rotations; on success
`DelayWin` → win sound → (not tutorial) **`LW.UnlockWindow()`** → `DisableIt`. `QuitCut` cancels. Everything except
`UnlockWindow` is local UI.

### 2.3 `UnlockWindow()` [V]
`isTutorial` → return. `CheckAlarm()`; `Windows[0].SetActive(false)` (glass removed); if `!noDoor` and a `Door` exists
on the object: `Door.enabled = true`, layer 13, tag **`Door`**; else `enterablesEnable.SetActive(true)`;
`unconvexCollider` → `MeshCollider.convex = false`; `complete_mission` → `Mission_Item.Mission_Complete()`;
**`Destroy(this)`** (the `LockedWindow` component disappears; from now on the window is an ordinary `Door` with
`isWindow`).
`UnlockWindowNoAlarm()`: `hasAlarm = false`, `UnlockWindow()`, then `Windows[0].SetActive(true)` (glass stays visible
— it is opened from inside, not cut), `complete_mission` → `Mission_Complete()` again [V: called twice in that path].

### 2.4 `BrokeWindow()` [V] (brick, crowbar hit)
`isTutorial` → return; once (`brokeGlass`): `Windows[0].SetActive(false)`, `CheckAlarm()`, broken glass:
`instantiateGlass` → `Instantiate(Windows[1], pos, rot)` scaled like the glass, else `Windows[1].SetActive(true)`;
door → enabled + tag `Door` (or `enterablesEnable`); possession + `alertNPCs` → `PS.AlertNPCsOnPossesion(1, null)`;
`addNoise` → `PS.MakeNoise += noiseAmount`; `unconvexCollider`; `Destroy(Windows[0])`; `complete_mission` →
`Mission_Complete()`; `Destroy(this)`.
Callers: `Brick.OnTriggerEnter` (thrown brick hits a `Window`), `PlayerScript.CrowbarHit` (tag `Window` with `canBreak`,
IL_01c6–IL_026c, plus `StartPoliceChase`/`sound_detected` in a residence with sound security; and tag `Door` with
`Door.isWindow` → `GetComponent<LockedWindow>().BrokeWindow()`, IL_0986–IL_09dc).
Crowbar break-in on a `Window` (`CrowbarBreakin`): `LockedWindow.UnlockWindow()` + `Door.OpenDestroyed()` (sash forced
open, `brokeHinge`).

### 2.5 `CheckAlarm()` [V]
If `hasAlarm`: clear it, `PS.isSeenByNPC = true`, `Police_Manager.Update_Last_Location`, warning
`window_alarm_detected`, `StartPoliceChase(starsAmount, AI[0] | null)`, `AlertNPCsOnPossesion`. Runs on the actor only
because it is inside `UnlockWindow`/`BrokeWindow`.

### 2.6 After unlocking: open, close, climb [V]
The sash is a normal `Door` (isWindow): same `Door` branch as doors (quick/slow, `Use`, sounds). `Door.Resett`
activates `WindowUse` when open; ray tag `WindowEnter` + `interact` → `WindowUse.UseWindow()` (local player movement
through the window). NPCs never open windows (`AI.OnTriggerEnter` excludes `isWindow`).

## 3. Animation and audio [V]
No Animator: the sash moves via `Door.Update`; glass removal = `SetActive`/`Destroy`; broken glass = prefab instance
or pre-placed object (any break sound/particles inside that prefab are not visible in IL [A]). Minigame sounds
(`WindowOpenMaster.winSound`) are local UI. Climb sounds on `WindowUse` (local).

## 4. Persistence and vanilla reset [V]
`LockedWindow`, `WindowOpenMaster`, `WindowUse`, `Brick` write **no SPrefs**. Cut/broken/unlocked windows return to
intact on every scene load. **The original does not persist broken windows** — the W4.2B roadmap line "durable ones as
Campaign lifetime where the original persists them" and the `RegionState.cs` header example ("broken windows") do not
apply: windows are **RegionLife**. Persistent side effects: `Mission_Item.Mission_Complete()` (mission progress, via the
mission system) and police/alert consequences.

## 5. Dependencies
Scene (per region; Madison 184), `WindowOpenMaster` object (tag), inventory tools (glass cutter via `Prompt_Color`,
crowbar, brick object), `reward_time`, residence (`assigned_residence`, `currentPossesionScript`, AIs), police, mission
items (`complete_mission`), security panels (`hasAlarm`), companion `openLocksMode`.

## 6. Local vs. host-authorized

| Operation | Local (actor) | Host transition |
|---|---|---|
| Prompts, cutting minigame, climb (`WindowUse`), input locks | yes | — |
| Unlock from inside (glass stays) | — | **yes**: `OpenedInside` |
| Cut (glass removed) | — | **yes**: `Cut` |
| Broken (brick/crowbar; broken glass shown) | noise/alert/police on actor | **yes**: `Broken` (terminal) |
| Crowbar break-in | actor effects | **yes**: `Cut` + sash door `DoorBroken` |
| Open/close sash after unlock | — | door kind (`wobj2`) once `isWindow` doors are allowed |
| Alarm, NPC alert, noise | actor only | police/NPC = W8/W9 |
| `Mission_Complete` | actor only | mission/story = W6 (**not covered**) |

## 7. Different regions
Same as doors/locks: actor proposes, host commits into `wobj4` (LockedWindow kind, Region scope) without running the
scene; a later loader applies the window state silently, then sash door states. RegionLife → cleared when empty.

## 8. Reusable mod infrastructure [M]
Census kind `LockedWindow` (4) exists; needs an instance→id map like doors. Registry/proposal path as for locks.
`WorldDoorSync.Ordinary` excludes `isWindow` doors today — allow them after the window transition is applied. Door swing
bridge `WorldDoorSync.Swing` works for sashes unchanged (`Door.Update` original).

## 9. Risks, unsupported cases, uncertainties
- **R1 Component destroyed:** `UnlockWindow`/`BrokeWindow` end with `Destroy(this)`; the `LockedWindow` id no longer
  resolves afterwards (census `TryResolve` checks liveness). Detection must read the id in a prefix/postfix of the same
  call (Destroy is deferred to end of frame [Unity semantics]); applying a committed state on a replica is one-shot.
  The sash `Door` keeps its own Door-kind id.
- **R2 Replica must not run the originals:** they fire `CheckAlarm` (police for the replica's player), noise, NPC
  alerts and `Mission_Complete`. Bridge = state subset only: glass `SetActive`/`Destroy`, broken-glass prefab, door
  enable/layer/tag, `enterablesEnable`, collider convexity, `Destroy(LockedWindow)`.
- **R3 Unlock from inside is automatic** (looking at the window from inside with `ceilingDetect`); it will produce a
  proposal whenever a player inside a house looks at such a window — rate limit (8/s) is enough, but the state is shared
  immediately [Q] whether this is noticeable to the other player.
- **R4 Missions:** `complete_mission` windows advance mission progress only on the actor's instance; P2 on the client
  completes it only in the sandbox → must be excluded (or host-forwarded) until W6 — **decision required**.
- **R5 Broken-glass physics/particles** are instance-local (non-deterministic) — acceptable as presentation.
- **R6 Capacity:** 184 windows in Madison vs. 100 states per kind per region (only changed windows are stored).
- **R7** `CrowbarHit` on a `Door` with `isWindow` calls `GetComponent<LockedWindow>().BrokeWindow()` with **no null check**
  [V IL_09b0–IL_09dc] — on a window whose `LockedWindow` was already destroyed this throws in vanilla (not ours); windows whose sash `Door`
  is tagged `Door` from the start while still carrying a `LockedWindow` may exist [A].
- Unsupported here: `Window_Blinds`, `WindowExplosion`, `GetAllWindows`, alarm panels (W4.2C).

## 10. Recommendation (windows part of W4.2B)
1. Kind rules for `ObjectKind.LockedWindow` (RegionLife): `0 Intact`, `1 OpenedInside`, `2 Cut`, `3 Broken`;
   transitions only away from `Intact` (all terminal for the visit; `Broken` also from `OpenedInside`/`Cut` is not
   reachable in the original because the component is destroyed).
2. Detection: Harmony prefix on `LockedWindow.UnlockWindow`, `UnlockWindowNoAlarm`, `BrokeWindow` (read id before
   `Destroy`), skipped while applying; skip `isTutorial`; until W6 skip `complete_mission` windows.
3. Replica bridge per §9 R2, then allow `isWindow` doors in the door slice so sash open/close is shared.
4. Lifetime RegionLife (no Campaign kind needed for W4.2B).
