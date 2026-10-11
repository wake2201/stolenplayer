> Current extension: 0.6.4/protocol 8 separates reliable occupancy from sequenced Host motion, adds asynchronous exit confirmation, original-player passenger lifetime safety, stronger native readiness/variant selection, and Host shared sleep with canonical time barriers. Native fixtures and remaining gates are in [scene acceptance](scene-continuity-acceptance.md). Milestone F has not started.

The current follow-up selects an available seat, prevents same-frame passenger entry/exit,
captures native driver recovery state and follows the authored driver reference outside
vehicle destruction ownership. Remote driving mirrors the verified alarm Rigidbody
lifetime without invoking RunCar on the Host passenger. Dead seat/player bindings stop
readiness, release canonical local occupancy and discard stale seated presentation.
Normal seat/scene flow continues to use original players and Host-owned occupancy.

# Two-player cooperative replication architecture

The permanent [Vanilla Gameplay Preservation — Non-Negotiable](../AGENTS.md)
rule applies to every runtime hook and future milestone. Multiplayer extends the
original game; it must preserve solo behavior and original first-person/EVP
gameplay, scope required adaptations, and optimize only our mod. Original binary
hashes are a safety check, not proof of runtime equivalence. The current
[gameplay intervention audit](vanilla-gameplay-audit.md) records classifications,
corrections and remaining evidence limits.

This updates the earlier transport proposal using the 2026-10-06 live investigation.
Evidence labels and limits are in [full-runtime-recon.md](full-runtime-recon.md).
Milestone C registry/snapshot infrastructure is accepted at mod 0.4.0 / protocol 3.
Milestone D implements Door/Window state and request adapters in mod 0.5.0 / protocol 4,
with real runtime acceptance complete and two-game validation deferred. Milestone E
adds authoritative supported loot, personal inventories and drop transactions in
mod 0.6.0 / protocol 5; see [loot/inventory synchronization](loot-inventory-sync.md)
and [acceptance evidence](milestone-e-acceptance.md). Later game-system adapters below remain **design only**. The accepted avatar remains intact,
with semantic player state, production male NetworkAvatar, bounded interpolation,
session membership and persistent scene-transition transport. LAN/direct-IP LiteNetLib 1.3.5 is implemented and tested. Broad interaction,
AI, advanced vehicle, economy, mission and cooperative disk-save synchronization is not implemented.

## Mandatory shared vehicle and continuity extension

VehicleState is an explicit Vehicle registry payload. Its driver/passenger actor IDs
are session/epoch scoped and occupancy changes carry an independent revision.
The Host reserves seats, validates local application confirmation, and alone commits
the result. Actor uniqueness covers confirmed and pending seats across every car.
Reservations, driver inputs and native references are invalidated at scene quiescence.
Baseline transfer includes occupied vehicles but never assigns an arriving actor a
seat. Disconnect removes the departing actor before a future baseline.

Seat requests/results use reliable ordered channel 0. Sequenced driver inputs use
channel 2; player state remains channel 1 and sequenced motion uses channel 3.
The fixed motion codec includes linear/angular velocity and independent sequence/tick;
it does not generate reliable registry revisions. Only the Host executes EVP
physics for bound vehicles. Client physics/input are suppressed and a kinematic
follower interpolates cached Host motion. Native passenger state is scoped
around the original local player and camera; NetworkAvatar remains presentation.

Mission-to-hideout requires BOTH confirmed actors in the SAME live vehicle, Driver
and Passenger. The Host checks before native selection save writes. Denial does
not begin a load or advance an epoch. Accepted transitions freeze transactions,
announce, automatically load on each peer, rebuild native/player/registry bindings,
transfer a fresh world and personal inventory baseline, then open WorldReady.
The connection persists. Host-only sleep checks the same mission precondition and
freezes gameplay until start acknowledgement, native Host advancement, canonical
wake application/acknowledgement and resume. Client independent Enviro progression
and the verified native hourly mutation block are suspended; lighting/UI remain
native. Hideout sleep does not require seats. Full NPC schedules are future work.
Native driving, seat visuals and scene loading have controlled fixture evidence,
with remaining native coverage and real two-machine gates explicitly separate.

## Runtime constraints that shape the design

VERIFIED_RUNTIME: PlayerScript and core managers are recreated with MainScene,
MadisonSt loads additively, NPCs have legacy Animation, and the clean seven-skin
civilian visual renders correctly. A delayed pickup changes inventory after its
ordinary PickUp method returns. Duplicate roots/names and changing Unity instance
IDs cannot provide universal identity. VERIFIED_STATIC: many systems cache one PS,
AI target or input/camera; preference mutations and durable save writes are separate.

## Permanent local-player invariant

**THE LOCALLY CONTROLLED PLAYER ALWAYS USES THE ORIGINAL THIEF SIMULATOR 2
FIRST-PERSON GAMEPLAY STACK.** Preserve PlayerScript, PlayerMotorBehavior,
PlayerCameraBehavior, CharacterController, original camera/listener, hands/arms,
functional tools, interaction logic, inventory behavior, animations and input.
Production multiplayer observes and replicates local state; it never replaces
local gameplay. This invariant applies Offline, connected, during teardown and
through every future milestone. The opt-in, expiring diagnostics input fixture
feeds the existing Rewired read path only for development acceptance and is not
production player control or human-input evidence.

Remote players use a mod-owned NetworkRoot → VisualRoot → fresh remapped skeleton,
shared game-owned render assets and sanitized animation. Never create a second
functional PlayerScript, NPC or held tool merely to display another player.

NetworkRoot position/rotation belongs exclusively to the remote interpolation
controller. Animation, model-axis, feet, equipment and any future head/spine look
correction belong below VisualRoot. Development assertions compare the root
before/after presentation. BodyYaw is never altered to compensate for model facing;
LookPitch is independent and never rotates NetworkRoot pitch.

Milestone B acceptance is tracked in [milestone-b-acceptance.md](milestone-b-acceptance.md).
**TWO-GAME ACCEPTANCE: DEFERRED**, under the explicit project decision; see the
[future legitimate two-process-first procedure](two-game-acceptance-plan.md).

## Authority policy

| System | Authority | Receiver treatment |
| --- | --- | --- |
| Membership, epoch, registry, logical IDs/revisions | HOST-AUTHORITATIVE | Apply accepted host identity/lifecycle only |
| Raw movement/input and immediate local camera/hands | LOCAL-ONLY input/presentation; OWNER movement WITH HOST VALIDATION | Remote pose interpolation; no copied input/controller |
| Player animation/look/equipment | Owner semantic claims WITH HOST VALIDATION; PRESENTATION-ONLY rendering | Rig-specific adapter, renderer-only tools |
| Door/window/lock/switch state | HOST-AUTHORITATIVE | Explicit state/presentation adapter, no local-player-bound Use replay |
| Loot reservation/claim/drop/cargo | HOST-AUTHORITATIVE transactions | Reliable idempotent commit, tombstones, owner/location changes |
| Personal inventory/equipment/durability | HOST-AUTHORITATIVE personal records | PersistentCoopPlayerId ownership; no duplicate grants |
| Shared wallet, XP, level, skill points and unlocked skills | HOST-AUTHORITATIVE global Host Save | Automatic request validation, shared UI balance/progression |
| NPC/guard/police perception and schedules | HOST-AUTHORITATIVE | Clean replicated visual/state, no client AI/nav/coroutines |
| Vehicle physics and collision outcomes | HOST-AUTHORITATIVE; driver input OWNER WITH HOST VALIDATION | Pose/velocity/wheel presentation interpolation; seat tokens |
| Mission stage/world unlocks/time | HOST-AUTHORITATIVE | Global objective state with actor-specific contribution/reward policy |
| Coop save/checkpoint/travel | HOST-AUTHORITATIVE | HostGameSave plus coop metadata; host offline remains playable |
| Camera bob/FOV, menus, tutorials, local phone UI | LOCAL-ONLY | Keep original behavior where safe; bridge replicated outcomes |
| Cosmetic sound/particles/debris | PRESENTATION-ONLY | Local effects; gameplay noise is a distinct host event |

Current movement authority validates replicated claims; it does not resimulate
CharacterController input or claim anti-cheat. Future host validation should add
plausible displacement/action/seat constraints based on measured gameplay.
Host AI/vehicle authority is LIKELY feasible from structure, not runtime-accepted.

Permanent policy is now fixed in [coop-gameplay-rules.md](coop-gameplay-rules.md):
shared wallet and XP/skills, personal inventory/equipment, host mission selection,
either-actor objective completion, shared busted outcome and host travel barriers.
The host must remain able to play the same save offline with remote personal records
inactive. Earlier personal-wallet/progression proposals are superseded.

## Small replication layer

Reuse the current transport/session/protocol boundaries. Add a small registry and
system adapters, not a general replacement game engine.

| Concept | Required semantics |
| --- | --- |
| NetworkEntityId | Scope + uint64 value; EntityRef adds generation, envelope adds session/epoch; players are a separate domain |
| NetworkEntityRegistry | Collision-checked canonical authored keys, host dynamic IDs, local binding/readiness and teardown |
| Adapter | Bounded CaptureState, ValidateRequest, ApplyAuthoritativeState, CreatePresentation, Dispose hooks per system |
| Authority / ownership | Host revision source; actor/seat/claim tokens; transfers explicitly revisioned |
| StateSnapshot / Delta | Explicit schema/payload, global last-change entity revision and bounds; reject stale generation/epoch |
| ActionRequest / Result | Reliable actor/request/entity/revision payload, host eligibility, cached idempotent result |
| Spawn / Despawn | Host type/source descriptor + initial state; retain tombstones/revision to reject late resurrection |
| SceneEpoch | Monotonically changes for a world set; gate messages on ready registry and completed baseline |
| Initial / reconnect sync | Verified baseline + changes newer than snapshot cut; resume token and membership rules separate |

See [world-object-identity.md](world-object-identity.md): prioritize validated semantic
save keys, otherwise exact scene identifier + escaped sibling-index hierarchy +
concrete type/ordinal. Keys must be compared across peers and reloads. Instance IDs
are local diagnostics, not packets. Full relevant-registry hashes catch content or
progress mismatches before actions. Dynamic source descriptions are mod-approved
selectors, never arbitrary method/path injection from the wire.

## Semantic player and equipment state

PlayerReplicationState: position/yaw, optional look pitch, velocity/speed/vertical
velocity, grounded/crouched/sprinting, locomotion/action enum with sequence/start
tick, equipped definition ID, carried entity and seat/vehicle ID. Validate each field
and send changes at the rate needed by presentation. Animator hashes/clip identifiers
remain adapter/debug details, not the stable primary protocol.

Civilian rigs currently support legacy Idle/Walk/Running; full-body crouch/backward/
strafe/tool binding needs acceptance. NetworkRoot owns motion; animated root curves
stay below VisualRoot and require compensation. RemoteEquipmentVisualController
looks up only visual render data and per-rig hand offsets; no functional tool script
or camera is copied. Local camera pitch effects are not blindly applied to NPC heads.

## Actions and coroutine transactions

Never “RPC the original method on both games” as a default. Door.Use changes cached
local-player state/noise; WindowUse can disable the local motor/camera and move the
actor; Item_Picked can award XP and update save bookkeeping. Host adapters apply the
original authority path once, observing actual delayed completion. Receiver adapters
apply state and presentation without executing award/input/save callbacks again.

Pickup: host reservation, validated branch, asynchronous completion, one inventory/
reward/world-removal commit. Include result revision/transaction ID; record committed
reward ownership. A disconnect before commit releases only still-pending reservations;
after commit the reconnect snapshot restores the result. Door/lock animation progress
can interpolate from host start tick while logical open/broken/unlocked state remains
revisioned. Gameplay noise emits once with actor ID and validated location/intensity.

## Implemented Milestone C foundation

[network-entity-registry.md](network-entity-registry.md) defines the implemented scoped
uint64 ID, separate EntityRef generation/context, SHA256 static descriptors, collision
policy, host-only canonical revisions, authority/ownership and adapter lifetime.
[world-snapshot.md](world-snapshot.md) defines the C messages retained in protocol 4, the four-MiB baseline,
384-byte chunks, bounded assembly/backlog, application acknowledgement and WorldReady.
Development TestEntities retain their isolated fixture. D adds authored Door/Window
adapters with [accepted runtime evidence](milestone-d-acceptance.md). NetworkAvatar/first-person gameplay
is independent. Snapshot capture freezes immutable committed records; later changes
queue without pausing host gameplay. No loot/AI/save/travel adapter exists.

## Travel, saves and lifetimes

Session/transport stays mod-owned across disposable game managers. Travel barrier:
prepare target set/checkpoint → both quiescent → epoch transition → load → await
actors/managers/registry → compare → host baseline → acknowledged resume. Old-epoch
packets, coroutines and Unity references are invalidated. Supported gameplay scene
changes retain transport; native menu return safely terminates the session. Stopping
hosting clears its old descriptor/epoch/registry before a subsequent Client join.

Host coop save stores schema/game build, stable logical keys, inventories/rewards,
tombstones, missions, time, vehicles/storage and checkpoint revision. Write to an
host-compatible coop metadata with backup/atomic checkpoint policy; the same vanilla world must remain playable offline. Existing single-player
FBPP preferences are integration details; client joins must not overwrite normal
profiles. SaveGameNewSystem and UI quicksave are specialized bridges requiring
save-enabled runtime acceptance, not presumed universal serialization hooks.

## Debug/desync design

Opt-in compact per-system canonical state hashes at a low frequency, entity counts,
revision/tick/generation/epoch counters, pending transaction counts, registry mismatch,
authoritative corrections and snapshot completion counters. Exclude interpolation/
cosmetics and unstable Unity IDs from hashes. Compare at the same host revision, not
different local frame times. Report first differing system, then enable bounded
targeted detail; expensive dumps remain disabled by default. Current telemetry and
synthetic impairment harness remain useful without claiming world-sync acceptance.

## Transport and milestone gates

The 0.6.5 stabilization preserves protocol 8 and the existing automatic scene follower.
Cooperative vehicles use semantic verified manager slots rather than mutable root indices.
Vehicle existence is independent of confirmed/reserved/exiting occupants and operational
seat references. Native driver exit keeps the original actor available throughout its
temporary controller-disabled stage. Loss of an occupant binding restores that occupant;
verified native destruction or a scene boundary alone removes the vehicle entity.
Passenger control uses the original player/camera with named rear-right body and eye
references; front storage is untouched. The confirmed same-vehicle two-seat requirement
gates Host mission return/sleep before loading, transactions or epoch advancement.

Synchronized LiteNetLib callbacks have a count limit of 128 and configurable 2-ms time
budget. Atomic callbacks finish, then remaining reliable events stay ordered in the
queue; a queue beyond 8192 explicitly fails the session rather than discarding canonical
traffic. Baseline transfer remains four chunks per poll, with atomic immutable capture
and apply. The empty loot manifest is cached even when it contains zero entities.
Thirteen bounded owner-thread phase/frame rings support explicit spike export. No Unity
object is accessed from background work, no live overlay hashes whole worlds, and no
per-frame diagnostic log is introduced. Native loading and diagnostic fixture setup are
separate acceptance limits; see [P0 evidence](scene-continuity-acceptance.md).

Scene preparation indexes existing Lockpick.unlockDoor links once. Door baseline/delta
application uses its own linked components, preserving shared-leaf lock semantics and
active-object checks without a whole-scene lookup per door. The index is owned by the
current adapters and rebuilt with the scene/role manifest. World and loot binding traces
show at most sixteen examples each plus complete aggregate counts; registry contents
and baseline validation are unchanged. Canonical baseline application remains atomic.

LiteNetLib 1.3.5 with documented net46 adaptations and MIT provenance remains the
implemented transport. Optional Steam sockets/discovery fits behind ITransport only
after legitimate initialization/interface/peer tests. No Steam identity change is
part of this design. [Steam research](steam-transport-feasibility.md).

Milestone C implements the accepted registry and snapshot foundation. Milestone D
is now implemented with [explicit semantic states and original game boundaries](world-interaction-sync.md),
and accepted real game/harness interaction, hash, reconnect and reload evidence.
True two-game acceptance stays DEFERRED as its own later gate. World adapters
for later milestones remain design only. [Full dependency roadmap](full-sync-roadmap.md).
