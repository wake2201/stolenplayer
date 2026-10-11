# Milestone C entity registry

Accepted C implementation: mod 0.4.0, protocol 3, generic foundation and opt-in
synthetic test adapters. D adds Door/Window adapters in mod 0.5.0 / protocol 4;
their real runtime acceptance [passes](milestone-d-acceptance.md), with two-game
validation deferred.
Game saves remain outside the implemented replication contract.
Permanent authority/ownership policy is [coop-gameplay-rules.md](coop-gameplay-rules.md).

## Identity

NetworkEntityId is scope byte (Static=1, Dynamic=2) plus nonzero uint64 value.
EntityRef adds nonzero uint32 Generation. Session and scene context are outside
the ID, in every world message envelope. Dictionary GetHashCode is only an in-process
lookup; it is never network identity.

StaticEntityDescriptor schema 1 uses UTF-8 byte-length-prefixed canonical fields:
build fingerprint, exact scene asset path, identity mode, identity key, component
contract and same-type ordinal. The fallback key is root-to-leaf escaped names
with sibling indices including the root. UnityNetworkEntityAdapter.Describe builds
that path from a loaded legitimate installed scene; it refuses unloaded/pathless
scenes. Ordinal and adapter contract are supplied explicitly by the future adapter.
Position and Unity InstanceID are excluded. Semantic mode replaces the identity
hierarchy with a proven unique save/semantic key while keeping the path for diagnostics.
No real game's semantic field is currently approved as a universal GUID.

SHA-256 of the canonical UTF-8 descriptor supplies the first eight digest bytes,
interpreted little endian; zero maps to one. Equal keys and different-key hash
collisions both fail closed. Diagnostics include ID, descriptors A/B, entity paths
A/B and collision count. No insertion-order-dependent collision repair occurs.
Refusing ambiguous bindings is the collision policy. Client baselines require the
same ID, generation, descriptor and type as each registered static binding, and
reject missing authored entries. Future adapters must validate cross-peer stability
before enabling real state mutation. See [world-object-identity.md](world-object-identity.md)
and the full runtime recon: names/DOMY roots repeat across MainScene and MadisonSt;
Door_ID is empty, ItemID IDs identify definitions, not physical instances.

Host dynamic allocation is a separate uint64 monotonic counter. No ID reuse occurs,
even after a scene reset within that registry instance. Generation starts at one;
references/wire codecs support higher generations. An established generation rejects
both older and unannounced newer generations; a new baseline can establish a higher
generation. There is no automatic same-ID resurrection/reuse API in C.

## Ordering and authority

WorldRevision is a non-wrapping uint64 counter within SceneEpoch. Every committed
host spawn/state/despawn advances it exactly once. EntityRevision is the global
revision of that entity's last change, not a second counter. Client deltas require
the next contiguous world revision, matching epoch/generation and a known entity;
older/duplicate revisions cannot mutate state. Gaps request resnapshot. Snapshot
records have revision <= the captured base revision. Player movement keeps the
accepted separate sequenced transform stream and does not advance WorldRevision.

Authority has one supported world value: Host. NetworkAvatar is outside this
registry and remains presentation. EntityOwnership distinguishes System, Session
and Player (nonempty persistent GUID), with optional current input NetworkPlayerId
0/1/2; future vehicle input can belong to player B while authority remains Host.
ICoopIdentityProvider/PersistentCoopPlayerId/ActorRef supply concepts only; no player
identity is saved or authenticated by C. ActorRef includes the runtime SessionId,
network actor and persistent logical player.

SessionId preserves the accepted random nonzero runtime connection/session token,
renewed on each accepted join. It differs from persistent identity and distinguishes
reconnections, including the same still-running host. It is zero without a peer.
Every world packet is checked against it. SceneEpoch is a separate host-authoritative
nonzero uint32 world generation. Scene callbacks cancel sessions and clear registry
bindings/tombstones/snapshot work; epoch increments without wrapping. Player-pose
local readiness epochs remain separate, because adapter availability may change
without world travel. Phase 1 still disconnects on actual scene transitions.

## Registry and adapters

NetworkEntityRegistry contains at most 4096 live/tombstoned records together. Static
registration is bootstrap-only and is sealed when snapshotting starts; later static
additions require an epoch rebuild. Host-created dynamic entities use Spawn. Lookup
checks the complete EntityRef. FindBinding supports reverse adapter lookup. Capture
returns sorted immutable canonical records. DevelopmentSummary explicitly hashes
these records on demand; F8 audit enables it only under DevelopmentControls.

Adapters expose stable numeric type, IsAlive, bounded CaptureState, ValidateState,
ApplyState and Release. Mapping schema 1: TestEntity=1; Door=10, Window=11, Pickup=12,
Container=13, Npc=14, Police=15, Vehicle=16, MissionObject=17 are reserved. No .NET
implementation type is a protocol type ID. The registry understands no door/AI logic.
State changes become canonical only through explicit host CommitState, not by silently
resampling a changing Unity object halfway through a snapshot. Factory injection is
available for future dynamic presentation; only the opt-in TestEntity factory exists.

All registry accesses, capture and application assert the creating/main thread.
Network callbacks are polled on that thread; immutable baseline application is
staged for the next world Tick. Baselines validate every adapter/manifest before
applying any payload. Adapter exceptions fail synchronization and clear client
bindings; arbitrary adapter side effects cannot be rolled back by this generic layer.
Future Unity adapters must obey the explicit semantic contract and preserve originals.

UnityNetworkEntityAdapter uses Unity destroyed-object comparison at every binding
boundary. Access and a once-per-second main-thread sweep drop dead adapters, even
offline. Release clears engine references; scene reset releases all entries and
terminal network disposal explicitly releases remaining host bindings. Dead bindings may retain managed metadata pending
an explicit host tombstone; missing bindings reject application rather than looping
through MissingReferenceException. No persistent reference to an old scene survives.

Despawn is idempotent and retains an EntityRef/revision/type/ownership tombstone;
no payload remains. Same-generation late spawn/state cannot revive it. Tombstones
count toward the limit and are not pruned inside the epoch. On exhaustion the host
refuses new registrations rather than evicting safety metadata. Save translation,
tombstone compaction remain future work. E supplies supported dynamic Unity loot
spawning through this same allocator, as described below.

## Milestone D adapter integration

Door/Window adapters retain this exact static identity, generation, authority,
ownership and revision model. They register authored components after hosted
gameplay becomes available and on the client before baseline application.
PrepareRegistry rebuilds bindings after client session/epoch/reset events; sorted
descriptors, collisions and unresolved-capability diagnostics are explicit.
NetworkApplyScope wraps baseline application with silent presentation. A local
host semantic transition commits EntityChange; destroyed bindings become tombstones.
Canonical hashes already contain semantic payloads and revisions, excluding animation
progress. ParseCanonical supports the explicit synthetic harness manifest workflow;
production clients discover their own authored scene bindings, not wire-created doors.
See [world-interaction-sync.md](world-interaction-sync.md) for boundaries and limits.

## Milestone E loot integration

Supported authored Pickupable components register as Pickup type 12 using the same
descriptor/generation architecture. Physical NetworkItemInstanceId is distinct from
ItemID definition and lives in the semantic payload. Runtime spawner clones without
a verified origin and native drop prototypes are excluded from the static manifest.
Canonical pickups reserve one entity, observe delayed native completion, then use
Despawn to commit a revisioned tombstone. The optional despawn presentation boundary
hides the original object; Release alone remains binding cleanup.

Native drops preserve the physical item ID and use this registry's existing Spawn
allocator. A fresh dynamic EntityRef never reuses an older tombstone/generation.
Dynamic Unity presentation is now implemented for the supported E subset; generic
save translation and tombstone compaction remain later work. Missing baseline
bindings fail closed with a bounded entity-specific diagnostic. See
[loot/inventory synchronization](loot-inventory-sync.md).
