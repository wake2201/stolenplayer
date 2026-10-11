> S1 update: S1 is a prerequisite stabilization gate between E and F. Milestone F is BLOCKED until scene continuity, F8 measured performance, driver visibility, crouch grounding and human-assisted workflow acceptance pass. Do not begin F. Minimal session continuity is brought forward from K; full travel/checkpoint/mission/economy/vehicle synchronization remains deferred. See playability-stabilization-s1.md and manual-runtime-testing.md.

# Production coop roadmap

This is a dependency roadmap, not broad synchronization implemented during recon.
Each milestone has explicit acceptance gates. Two legitimate game instances
are required for eventual cooperative peer acceptance. That gate is explicitly
DEFERRED and does not block accepting Milestone B for continued development.
Evidence and remaining fixtures are in
[full-runtime-recon.md](full-runtime-recon.md).

Milestone B finalization (7 October): production male civilian factory,
protocol 2/AvatarId, semantic Idle/Walk/Run and renderer-only crowbar are implemented.
Sustained original-motor movement and yaw/pitch use a labelled development input
fixture; actual normal crowbar switching/use and real outgoing equipment have
runtime evidence. Final reconnect/soak results and acceptance are in
[milestone-b-acceptance.md](milestone-b-acceptance.md), with bounded presentation
limits in [network-avatar.md](network-avatar.md). **TWO-GAME ACCEPTANCE: DEFERRED**.
The [future plan](two-game-acceptance-plan.md) first tries two legitimate normal
local processes. Full-sync coop remains the goal; Milestone C now supplies the generic foundation (mod 0.4.0, protocol 3).

**Milestone B: ACCEPTED — TWO-GAME VALIDATION DEFERRED.** Ten reconnect cycles,
30 minutes of connected soak, abrupt equipped timeout/recovery, 145 checks and
final distribution/binary validation pass. C may be the next separately requested
implementation slice; presentation limitations stay explicit.

| Milestone | Deliverable | Acceptance gate / dependency |
| --- | --- | --- |
| A — Stable Phase 1 runtime acceptance | Startup/isolation and transport evidence; eventual two-game pose/lifetime acceptance | Runtime game+harness evidence exists. True two-game cooperative gate DEFERRED; must pass before cooperative acceptance is claimed |
| B — Production NetworkAvatar | Seven-skin male rig, sanitized Idle/Walk/Run, sole/root correction, protocol 2/AvatarId, semantic state and visual crowbar | Final decision in milestone-b-acceptance.md. Original first-person invariant, functional crowbar, sustained movement/yaw/pitch, reconnect/soak and build validation required. Crouch/backward/strafe/head/airborne/female limits explicitly non-blocking |
| C — Entity registry and synchronization foundation | Canonical authored keys, host dynamic IDs, authority/ownership, epoch/generation/revision, spawn/tombstones, chunked baseline | ACCEPTED generic infrastructure; see milestone-c-acceptance.md. Game-host/client and populated scene cleanup/reload pass. Real subsystem registry comparisons and deferred two-game gate stay visible. Compare relevant registries across restart/reload/additive load/duplicate names; collision rejection; malformed/oversize baseline bounds; late-join state cut; opt-in hashes |
| D — Simple world interactions | Door/window semantic state, automatic host validation/execution and C replication | ACCEPTED — TWO-GAME VALIDATION DEFERRED. Exterior/interior doors, sliding window, host locked/forced gate outcome, concurrent snapshot/race, hashes, reconnect and reload pass. Remote tool/minigame completion and unverified devices remain LIMITED; see milestone-d-acceptance.md |
| E — Loot and personal inventory transactions | Host reservations, asynchronous native completion, physical instance IDs, personal revisions, quantity/weight, equipment ownership and dynamic drops | Implemented at mod 0.6.0 / protocol 5; 476 checks retain all 367 accepted checks. See milestone-e-acceptance.md for native game/harness evidence and LIMITED categories. Money, heavy carry, specialized condition and mission/container semantics are excluded |
| F — Containers/storage/economy | Shared storage, lockers/cargo, sale/buy and single Host Save wallet and explicit debt handling | Depends on E. Atomic transfers with revisions, competing withdrawal, reward/sale once, weight/storage capacity, reconnect and baseline |
| G — NPCs, security cameras and permanent teammate tracking | Host-native multi-actor NPC/camera perception, schedules/spawns/nav/door/animations/alerts/alarms, clean Client presentation; permanent world indicators, minimap markers and native full-map support | Depends on C/D and B. Suppress competing Client NPC/camera authority within the connected session; both actors seen/heard independently; ordinary/female/special/guard coverage; schedule rollover and outcome replication. Symmetric teammate tracking through obstacles and during vehicle travel, correct native map conversion, SceneEpoch binding and cleanup are required in this same milestone. See [Milestone G implementation prompt](milestone-g-prompt.md) |
| H — Police/wanted authority | Actor-specific illegal contributions/target IDs, spawn/search/chase/catch/cleanup | Depends on G. Two targets, loss/search, scene cleanup, wanted policy, no duplicate police; arrest consequences/reconnect validated |
| I — Advanced vehicles | Cargo/gas/damage/upgrades, broader vehicle variants and higher-quality corrections | Basic real passenger, validated driver input and minimum Host motion are mandatory NOW in scene/travel. I retains advanced scope only; it cannot defer cooperative seating |
| J — Missions/story/progression | Global objective/unlock state, shared XP/level/skill points/unlocks, participation and reward ledger | Depends on D/E/G/H/I for relevant triggers. Area/action objectives accept either actor by policy, story UI local, rewards once, unlock/save keys audited, resume stage consistent |
| K — Persistent travel/session continuity | Quiescence/checkpoint/epoch/load/readiness/baseline barrier | Depends on C and adopted subsystem adapters. MainScene/additive street plus second area, genuine PS/GM recreation, missing-client timeout, interrupted travel and reconnect; minigame scenes classified |
| L — Authoritative coop save model | HostGameSave plus versioned coop metadata/checkpoints, logical keys/tombstones/inventories/world/time/progression | Agree schema before E; implement durability after K. Save-enabled write/reload fixtures, atomic/backup checkpoint, crash recovery/migration/version rejection, client single-player profiles isolated |
| M — Optional Internet/Steam transport | Optional ITransport sockets adapter, discovery/invitations if verified; independent LAN | Can be evaluated separately after A; not a prerequisite for B–L. Legitimate SDK/Spacewar + owned build, auth/native interface/callback tests, relay/lobby connection, service loss; no AppID/DRM workaround |
| N — Desync/stress/polish | Regression matrix, canonical state hashes/correction metrics, stress/long play, settings/performance | Starts with C diagnostics and continues throughout. Combined two-player campaign fixtures, loss/jitter/reorder/reconnect/save/travel, sustained memory/CPU budgets, no orphan entities/rewards; release acceptance includes all adopted systems |

Dependencies form a graph: registry/snapshot and save ownership are foundations;
avatar work is the highest-priority visible feature after A. AI target changes
require measured perception, vehicles require actual moving-physics tests, and
mission adapters depend on their underlying action systems. Internet transport must
not postpone LAN gameplay correctness.

## Exact next implementation slice

Milestone E is implemented at mod 0.6.0 / protocol 5 with 476 checks and
[bounded runtime evidence](milestone-e-acceptance.md). It preserves B/C/D and the
original local gameplay stack, uses personal runtime inventories keyed by persistent
player identity, and transfers physical ownership through reservations, native
completion, tombstones and dynamic drops. Specialized/mission/container objects,
unverified procedural spawns, worn crowbar transfer and unknown slots remain LIMITED.
The current mandatory slice is Scene Continuity, Automatic Travel and Late-Join,
including a real passenger, two confirmed seats, minimum shared motion and guarded
travel/sleep. Version 0.6.5/protocol 8 supplies independent sequenced motion,
asynchronous exit confirmation, canonical shared sleep/time, native variant selection
and verified native automatic mission/hideout loading with a managed peer.
[Acceptance and remaining native gates](scene-continuity-acceptance.md) distinguish
these fixtures from two native games. Complete remaining variant/exception/input and
intermittent braking repeatability coverage before claiming full implementation acceptance.
The 0.6.4 follow-up adds available-seat interaction, an entry-frame exit guard, surviving
native driver recovery and the verified remote alarm Rigidbody lifetime. Destroyed original
camera/whole-vehicle reinitialization outside scene loading, independent native profiles
and second-variant acceptance remain explicit gates. The detailed report separates final
completed fixtures from earlier failed/partial attempts. **Do not start Milestone F.**
The 0.6.5 P0 stabilization adds a named geometry-derived rear-right passenger/eye frame,
semantic persistent vehicle keys, normal native exit availability, empty-manifest caching
and ordered count/time-bounded transport polling. Per-door whole-scene searches are replaced
by scene-scoped native lock links; baseline cost probes separate validation and native types.
Repeated native lifecycle and measured
phase evidence are recorded separately from remaining frame-spike and two-native gates.
Both original active vehicle variants now pass controlled passenger, twenty-cycle seat
and EVP role/braking fixtures. Reported actual vehicle loss, native loading/GC spikes and
exceptional original-camera/whole-vehicle recovery remain tracked, not silently accepted.
F remains later work requiring a separate instruction. Its shared Host wallet and
buyer-owned physical items remain unchanged product requirements.
The original local first-person
stack and [coop gameplay rules](coop-gameplay-rules.md) are permanent requirements.

Carry presentation limits alongside the gameplay systems that need new actions.
Perfect animation coverage is not a prerequisite for C. Eventual true two-game
movement, lifecycle, scene and original first-person acceptance uses the separate
legitimate-process-first plan; a protocol harness cannot replace it.

Before each world milestone, close its UNKNOWN runtime fixtures on a backed-up
disposable save. Use reusable scoped diagnostics, not full proprietary dumps in Git.
No artificial “Phase 2 means every system” bundle is needed.
