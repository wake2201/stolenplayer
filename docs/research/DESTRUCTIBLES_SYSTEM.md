# DESTRUCTIBLES_SYSTEM — House 102 fence and other breakable world objects (W4.2C Phase A investigation)

Date: 2026-11-14. Method: read-only Mono.Cecil IL dump + read-only UnityPy reads of `level3` (MadisonSt). Legend as in
`FURNITURE_SYSTEM.md` ([V] verified, [A] assumption, [Q] in-game test). The mechanism (`DestroyObject`) is described
in `FURNITURE_SYSTEM.md` §4; this file covers the House 102 barrier and the multi-stage question.

## 1. Which script breaks the fence [V]
Not `Destructible` / `BreakableObject` (0 / unused in Madison). The crowbar path is
`PlayerScript.CrowbarHit` IL_0636–IL_068c → target tag `Destructible` → `DestroyObject.Destroy_Thing()`.
`DestroyObject.Start` gives the object the tag `Destructible` unless `dontSetTag`.

## 2. House 102 hierarchy (scene `MadisonSt`, read from `level3`) [V]
Two independent `DestroyObject` components, about 0.2 m apart at world ≈ (445.7–445.9, 47.1–47.6, 180.3–180.5):

**Stage 1 — plywood sheet** `DOMY/Dom 102/PLANK DESTROYABLE/plywood_001a`
(MeshRenderer, BoxCollider, Rigidbody, DestroyObject; position (445.69, 47.51, 180.31))
- `normal_objects` = [`plywood_001a`] (itself) · `destroyed_objects` = [`PLANK DESTROYABLE/plywood_001a_root`]
  (inactive at load; AudioSource + fragment hierarchy) · `detach_destroyed = 0`, `dontSetTag = 0`, `dust_spawn = null`.

**Stage 2 — three nailed planks** `DOMY/Dom 102/DESTROYABLE PLANKS/Destroy`
(MeshRenderer, BoxCollider, Rigidbody, DestroyObject; position (445.84, 47.34, 180.48))
- `normal_objects` = [`wood_plank_001a`, `wood_plank_001a (1)`, `wood_plank_001a (2)`, `Destroy` (itself),
  `Collider`] · `destroyed_objects` = [`DESTROYABLE PLANKS/Destroyed`] (inactive at load; AudioSource + a script +
  3 × 5 fragments `wood_plank_001a*_sh_1..5`, MeshCollider + Rigidbody, layer 11).
- `DESTROYABLE PLANKS/Collider` (MeshRenderer + BoxCollider) is the blocking volume of the opening; it is switched
  off by stage 2 (`normal_objects`).

## 3. State transitions [V IL + scene]
| Step | Original action | Physical result |
|---|---|---|
| S0 → S1 | crowbar hit on the plywood → `Destroy_Thing` | plywood colliders destroyed, plywood hidden, plywood fragments shown (physics); `destroyed = true` |
| S1 → S2 | crowbar hit on the plank bundle → `Destroy_Thing` | its own colliders destroyed; 3 planks + `Collider` hidden; plank fragments shown; **opening passable** |
| reset | scene reload | both back to S0 (no SPrefs) |

Each stage has its own one-shot guard (`DestroyObject.destroyed`), so each needs its own authoritative state. Order: both
objects are tagged `Destructible` from `Start`, and `CrowbarHit` acts on whatever the crowbar sensor detects — the IL
does not force plywood-before-planks; physically the plywood sits in front [A]. Whether the planks can be hit through
the plywood: [Q]. The user's "3. allow passage" is the outcome of S2 (no separate script) [V: no third component].
The fragments' `Rigidbody` debris and the stage's AudioSource (activated object plays on enable [A: playOnAwake not
decoded]) are presentation.

## 4. Side effects to keep / avoid
- Actor only (original): crowbar sound, shake, `MakeNoise += 0.75`, `brokeLoud`, `AlertNPCs_DistanceBased(1)` around
  the actor (wakes / alerts residents near the actor).
- Not used here: `activate_mission_completion` (that flag exists for mission objects such as House 101's AC).
- No inventory, money, XP or save effect for the fence [V].

## 5. Synchronisation design (not implemented)
- Identity: census kind `Breakable` for `DestroyObject` (Madison 64, Hillville 9, Hideout 7 instances); id = existing
  FNV scheme (scene + hierarchy path + kind). Both House 102 stages get distinct ids from their paths.
- State: one bit `Destroyed` per id, RegionLife scope, terminal (`S0 → Destroyed` only; anything else refused).
- Capture: Harmony **postfix** on `DestroyObject.Destroy_Thing`, only when `destroyed` flipped false → true during the
  call and not while applying a remote state. Event-driven, zero per-frame cost.
- Host decision: accept the first commit per id (idempotent; a second `Destroyed` = `Unchanged`). Player 2 is refused for
  `activate_mission_completion` objects (restricted) until shared missions exist.
- Replica bridge (mirrors `Destroy_Thing` IL_000b–IL_00d7 minus actor effects): `destroyed = true`; destroy own
  `BoxCollider`/`MeshCollider`; activate `destroyed_objects` (+ detach); deactivate `normal_objects`. Never call
  `AlertNPCs_DistanceBased` or `Mission_Complete` on the replica. Late joiners: silent apply after region load (fragments
  can be shown already settled — accepted presentation difference; or apply with fragments hidden [decision]).
- Ordering: the region reconcile applies `Breakable` before `Door` (a broken barrier can expose doors) — no dependency
  found for House 102 [V: no `Door` in `normal_objects`].

## 6. Classification
| Aspect | Class |
|---|---|
| Stage destroyed flags | temporary region state, host-authoritative transition |
| Colliders removed / passage | follows the flag on every instance (deterministic from the bridge) |
| Fragments, sounds, dust | local visual/animation state |
| NPC alert | region-simulation-dependent (actor instance) |
| Mission completion (other objects) | persistent shared campaign state, restricted host transaction |

## 7. Uncertainties
- [Q] Whether stage 2 can be broken while the plywood is intact (sensor reach through the sheet).
- [Q] Which Madison `DestroyObject`s set `activate_mission_completion` (asset field layouts vary; decode per object or
  read the live component at census time).
- [A] Fragment AudioSource plays on activation.
