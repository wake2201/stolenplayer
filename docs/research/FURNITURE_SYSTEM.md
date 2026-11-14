# FURNITURE_SYSTEM — drawers, cupboards, cabinets, breakable furniture (W4.2C Phase A investigation)

Date: 2026-11-14. Game build 1.27c, `Assembly-CSharp.dll`. Method: read-only Mono.Cecil IL dump (scratch tool) and
read-only UnityPy reads of `level3` (MadisonSt), `level4` (PlayerHideout), `level8` (Hillville). No game started.
Legend: **[V]** verified in IL / scene data, **[M]** verified in the mod source, **[A]** assumption, **[Q]** needs an
in-game test. Builds on `DOOR_SYSTEM.md` (the same `Door` class) — read that first.

## 1. Classes

| Class | Role | Evidence |
|---|---|---|
| `Door` | Every drawer / cupboard / fridge / file cabinet / glass cabinet / container leaf. Type flags `isDrawer`, `isCupboard`, `isFridge`, `isFileCabinet`, `isGlassCabinet`, `isContainer`; motion `moveOpen`+`OpenMove` (drawers slide) or `rotateOpen` (doors swing). Same `Use` / slow-open / `SlowFailed` path as house doors. | [V] DOOR_SYSTEM §1–2 |
| `DestroyObject` | Breakable furniture and props (chairs, toilets, plate stacks, washing machine, crates, the House 102 planks). | [V] IL below; Madison 64, Hillville 9, Hideout 7 instances |
| `RandomFileCabinet` | Picks a random content variant per cabinet row at load. | [V] `Start` IL_0000–IL_00b4; Madison 2, Hillville 1 |
| `Pickupable` | Loot inside furniture is ordinary pre-placed loot; `Pickupable.Start` is empty (no random roll). | [V] |
| `Destructible` | Alarm / door-unlocking breakable (`doorsUnlock` → tag `Door`). **0 instances** in Madison, Hillville and Hideout; no IL caller of `DestroyObject()` (would be a UnityEvent/animation event). | [V] scene counts, refs scan |
| `LootBox`, `ItemSpawner` | `LootBox` has empty methods. `ItemSpawner` is a UI spawner (`ItemSpawnUI.AssignAll`), not world loot — the census note "random loot spawner" (`ObjectKinds`) is inaccurate. | [V] |

## 2. Opening / closing (drawers, cupboards, cabinets)
Identical to house doors [V, DOOR_SYSTEM §2]: `PlayerScript.Update` tag `Door` branch → `assignDoor` → `Door.Update`
slow open (≥ 0.15 s) or `SlowFailed` → `Use()`. Motion is procedural in `Door.Update` (no Animator). Audio clip per type
from `GameManager.doorSounds` (`drawer open`, …). Drawers/cupboards/cabinets with `disableCollisions` move to layer 11
in `Door.Start`. `randomlyOpen` applies only to `isWoodenDoor`, so furniture always starts closed [V `Door.Start`].

**Locking:** furniture has no own lock logic in `Door`; a locked cabinet is a `Lockpick` gating the leaf exactly like a
house door (tag `Lock1` until unlocked) [V DOOR_SYSTEM §2, LOCKPICKING]. W4.2B already shares `Lockpick` states in
Madison — so a shared unlock of a cabinet lock is replicated while the cabinet leaf itself is NOT (gap, §6).

**NPCs:** `Door.NPCUse/OpenDoorNPC/CloseDoorNPC` exist for every `Door`; whether residents ever open furniture is not
visible from IL [A: navigation links target house doors only] — [Q].

## 3. Contents and item spawning
- Pre-placed `Pickupable`s inside drawers/cupboards. Persistence = the original loot keys (`ItemDay_<name>` respawn day,
  `Item_<name>` for unique items) [V WORLD_SYNC_AUDIT §3]. Already shared host-authoritatively in Madison by W4.1
  (`WorldLootSync`, 869 loot objects) [M]. A remote player can pick loot out of a drawer that is closed on its own
  screen (the drawer state is not shared) — physically odd but no duplication.
- `RandomFileCabinet.Start`: three rows, each with `Random.Range(0,20) > 17` → disable the default object and enable one
  random variant from the list [V]. Rolled **independently on each instance** → the two worlds can show different
  cabinet contents. If a variant contains a `Pickupable`, the loot identity exists on one instance only [A — variants'
  contents not inspected; census marks the kind Unsupported].
- Breakable furniture: `DestroyObject.Destroy_Thing` (§4) may enable a `destroyed` variant; no item spawn of its own.

## 4. Breakable furniture — `DestroyObject` [V]
- Trigger: crowbar only — `PlayerScript.CrowbarHit` IL_0636–IL_068c: target tag `Destructible` (or `DrinkBottle`) →
  `GetComponent<DestroyObject>().Destroy_Thing()`, then crowbar impact sound, camera shake, `MakeNoise += 0.75`,
  `brokeLoud = true` (actor only).
- `Start`: sets tag `Destructible` unless `dontSetTag`.
- `Destroy_Thing` (one shot, `destroyed` guard): `FindGameObjectWithTag("Player").GetComponent<PlayerScript>()`;
  `Destroy(BoxCollider)` and `Destroy(MeshCollider)` on itself; `PS.AlertNPCs_DistanceBased(1)` (inside try/catch);
  every `destroyed_objects[i].SetActive(true)` (+ `parent = null` if `detach_destroyed`); if
  `activate_mission_completion` → `GetComponent<Mission_Item>().Mission_Complete()`; every `normal_objects[i].SetActive(false)`.
- Debris: the destroyed variants are pre-built fragment hierarchies (`*_sh_N` with `MeshCollider` + `Rigidbody`,
  layer 11) [V scene] — local physics.
- Persistence: no SPrefs access → restored on every scene load [V]. Exception: `activate_mission_completion` objects
  call `Mission_Complete` (persistent mission progress) — e.g. `DOMY/Dom 101/AC Mission/AC` [V scene path; field value
  per object not decoded].

## 5. Save / reload semantics
| State | Saved? | Reset |
|---|---|---|
| Drawer/cupboard open/closed | no (`Door` has no SPrefs) | every scene load |
| Cabinet lock | no | every scene load; NPC relock (LOCKPICKING) |
| Loot inside | yes, loot keys (`ItemDay_`/`Item_`) | respawn day / never for unique items |
| `RandomFileCabinet` variant | no | re-rolled every load |
| Broken furniture | no | every scene load (mission flag excepted) |

## 6. Existing mod coverage and gaps
- Identity: `Door` (incl. furniture) is in the W1 census (`ObjectKind.Door`, FNV scene + path + kind) [M]. Madison
  1142 / Hillville 605 / Hideout 43 `Door` components [V scene counts]. `DestroyObject` and `RandomFileCabinet`
  identity: `RandomFileCabinet` census kind = Unsupported; `DestroyObject` not in the census at all [M].
- Sync: furniture leaves are filtered out by exactly one predicate, `WorldObjectBridge.Ordinary`
  (`SpecialNames = isCarDoor, isDrawer, isCupboard, isFridge, isFileCabinet, isGlassCabinet, isContainer`) at
  `WorldObjectSync.cs:312/429/535` [M]. Everything else (registry kind `Door`, state encoding open/slow/broken,
  reservations, replica swing via `Door.Use` minus local effects, slow replay) already applies to them.
- Capacity: `ObjectStateRules.MaxPerRegion` = 12 pages × 100 = 1200 per kind per region ≥ Madison's 1142 doors [M].

## 7. How to synchronise (recommendation, not implemented)
1. **Furniture leaves = Door kind, same transitions.** Replace the blanket `Ordinary` exclusion by an explicit
   supported-type predicate (drawer, cupboard, fridge, file/glass cabinet, container); keep `isCarDoor` excluded (`vdoor`).
   No new message, no new state bits, no extra per-frame work (capture is the existing `assignDoor` watch + hooks).
   Risk: higher commit volume (drawer spam) — the existing 8/s proposal rate limit applies [M]; [Q] measure.
2. **Breakables = new census kind + RegionLife state** `Destroyed` (terminal). Detection: Harmony postfix on
   `DestroyObject.Destroy_Thing` (fires once per object; guarded by a "applying remote" flag). Replica bridge mirrors the
   IL **without** the actor-only effects: set `destroyed = true`, destroy the two colliders, toggle
   `destroyed_objects` / `normal_objects` (+ detach). Do NOT call `Destroy_Thing` on the replica: it would run
   `AlertNPCs_DistanceBased` around the *local* player and could call `Mission_Complete` a second time.
   `activate_mission_completion` objects: host-only / restricted for Player 2 until shared missions exist (same rule as
   story locks).
3. **`RandomFileCabinet`**: align once per region epoch (first loader publishes its three row choices, later loaders
   apply them before loot reconciliation) — same pattern as the still-open `randomlyOpen` alignment. Low priority.

## 8. Classification
| Aspect | Class |
|---|---|
| Drawer/cupboard open state, broken furniture | temporary region state (RegionLife), host-authoritative transition |
| Loot inside | persistent shared campaign state (existing W4.1) |
| Mission-completing breakables | persistent shared campaign state, host-authoritative transaction (restricted) |
| Swing/slide motion, sounds, debris physics, camera shake, noise | local visual/animation state (each instance) |
| NPC reactions (`AlertNPCs_DistanceBased`) | region-simulation-dependent (actor's instance only today) |

## 9. Risks / uncertainties
- [Q] Share of furniture among Madison's 1142 doors (decides commit volume) — census export `World.CensusExport`.
- [Q] Whether NPCs open or close furniture.
- [A] `RandomFileCabinet` variants may contain loot that exists on one instance only.
- Field order note: some scene assets serialise fewer fields than the current class (e.g. `DestroyObject` assets with 3
  bools) — the bridge must read live component fields, never assume asset layouts.

## 10. Status after W4.2C Phase B (0.15.0, 2026-11-14) [M]
- Implemented as recommended in §7.1: furniture leaves share the Door kind (`DoorTypes.Shared` = not `isCarDoor`), in
  Madison, Hillville and the hideout. No new message / key / state bit. PENDING MANUAL QA (TESTING §34).
- §3 / §9 `RandomFileCabinet` resolved [V UnityPy]: the random variants contain only loot `Pickupable`s (wallet,
  projector, $50 cash, scanner, keyboard, tablet, hard drive), never a `Door`; the three drawers are fixed siblings. Their
  loot is census-flagged `RandomCabinet` → outside shared loot (no double grant); visible contents may differ per PC.
- Breakables (§7.2) and file-cabinet alignment (§7.3) remain open.
