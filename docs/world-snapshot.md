# Initial world snapshot and semantic delta stream

Protocol 4 requires exact version and mod compatibility; protocols 1–3 are
explicitly rejected. C introduced the join/readiness contract at protocol 3.
PlayerState/NetworkAvatar wire
semantics are retained. World controls use LiteNetLib ReliableOrdered channel 0;
player poses continue Sequenced channel 1 and remain independently renderable.

## Join sequence

Transport -> protocol/build/avatar handshake -> session-local actor IDs -> matching
loaded scene keys -> SnapshotBegin -> SnapshotChunk(s) -> validate/stage -> main-thread
baseline apply -> SnapshotAck -> bounded post-baseline deltas -> WorldReady marker.
WorldSyncState is separate from MultiplayerSession.Connected. Future gameplay action
adapters must gate on WorldReady. C does not suppress original singleplayer inputs
or make existing avatar presentation wait on a world adapter that is not installed.
An empty registry still completes the generic world handshake during normal play.
In D, hosted real Door/Window bindings participate in the same baseline and backlog;
their Unity runtime acceptance passes with two-game validation deferred. Client action requests require
WorldReady. No historical interaction replay is needed for late joining.
Synthetic entities require explicit Development.WorldFixture on the game and `world`
mode in TransportHarness; they are managed records, not game mechanics.

## Wire layout

All integer fields are little endian. Every world message has type uint8, SessionId
uint64 and SceneEpoch uint32. Types: 20 SnapshotBegin, 21 SnapshotChunk, 22 SnapshotAck,
23 EntityChange, 24 WorldReady, 25 ResyncRequest. Snapshot metadata contains SnapshotId
uint64, BaseRevision uint64, TotalBytes int32, ChunkCount int32, SHA256[32]. Each chunk
adds Index int32, PayloadLength uint16 and bytes. SnapshotBegin declares the only
permitted current assembly; arbitrary chunk IDs cannot create additional assemblies.
Largest packet is 459 bytes including the 75-byte chunk header, below the retained
512-byte application bound. Transport itself may combine/fragment datagrams.

Snapshot payload schema uint16=1, entity count int32, then sorted entity records:
scope uint8, ID uint64, generation uint32, type uint16, entity revision uint64,
authority uint8, ownership kind uint8, persistent owner GUID[16] (.NET Guid.ToByteArray
layout), input player uint8, tombstone uint8, UTF-8 descriptor length uint16/bytes,
payload length uint16/bytes. Dynamic descriptors are empty. Static descriptors must
match the local manifest. Payloads have adapter-owned explicit versions; TestEntity
schema 1 is byte version, byte bool, int32 Value and float32 PositionLikeValue (10 bytes).
No arbitrary graphs, reflection serialization, BinaryFormatter or binary byte diffs.

EntityChange adds operation byte: Spawn=1, State=2, Despawn=3 and the same record
without descriptor. Static deltas resolve their descriptor from the already validated
baseline/local binding. Spawn is dynamic-only; an unknown state update requests resync.
SnapshotAck and WorldReady each carry SnapshotId uint64 and applied/base or final
revision uint64. Ack must match session, epoch, current snapshot ID and baseline,
and is accepted only once all chunks were sent. Delivery alone never marks world ready.

## Bounds

| Limit | Value / behavior |
| --- | --- |
| Registry/snapshot entity count | 4096 including tombstones; observed DOMY capture had 4035 visited objects, 300 relevant components (truncated), not a proven whole-game adapter count |
| Entity semantic payload | 256 bytes; larger future state needs an explicit schema/bounds change |
| Canonical static descriptor | 2048 UTF-8 bytes |
| Snapshot total | 4 MiB; capture refuses larger serialized baselines |
| Chunk payload | 384 bytes, exact expected length per index |
| Chunk count | 10923, additionally must equal ceil(total/384) |
| Assemblies | One active assembly per client, no growing ID dictionary |
| Reassembly lifetime | 30 seconds fixed from SnapshotBegin |
| Synchronization timeout | 40 seconds since host send progress / start of catch-up; client has assembly deadline plus sync timeout |
| Delta backlog | 1024 records and 512 KiB accounted bytes; both checked |
| Send work | Four chunks or semantic updates per poll; reliable queue backpressure at 128 queued packets, plus LiteNetLib's fixed in-flight window |
| Restart attempts | Three consecutive snapshots per synchronization failure; success resets the consecutive count |

These conservative development bounds need real adapter scale/throughput measurement.
A valid maximum-size snapshot can still time out at low poll/network throughput; C
fails/restarts within bounded memory rather than promising unlimited-duration joins.

Length/count/type/boolean/authority/ID/generation/revision checks precede controlled
allocations. The parser rejects truncation, trailing bytes and invalid UTF-8. Metadata
constructors check total/count arithmetic before buffer allocation. SHA256 validates
the completed baseline; it detects corruption, not remote-peer authentication.
Assembler handles out-of-order and identical duplicate chunks; conflicting duplicates
invalidate it. Wrong session/epoch/snapshot never mutate the world. Expired/corrupt
assemblies request a new baseline; a duplicate/older begin cannot reset progress.

## Snapshot-to-delta bridge

On the main thread, capture sorted immutable committed records at BaseRevision R.
No host game pause occurs during transmission. Every host change R+1 onward is
queued in order, including tombstones and spawns. Ack means the client successfully
validated and applied R. Host then drains the backlog with the same bounded sender,
including changes arriving during catch-up, and emits a final revision WorldReady
marker on the same ordered channel. Client verifies its cursor equals that marker.
Later updates keep using the bounded queue. An overflow/gap discards the incomplete
backlog and recaptures current canonical state; it never declares a missing change
delivered. Repeated failure stops world synchronization visibly.

Per-entity revisions equal their most recent global revision. Client applies only
the next global revision with valid epoch, generation, type, adapter payload and
lifecycle. Duplicate/older updates are harmless. Reliable ordering is required by
the integration; the pure registry rejects gaps rather than maintaining another
unbounded reorder buffer. Tombstones are retained in baselines for late joins.

Baseline models and payloads copy caller data. Infrequent capture/chunking/assembly
allocate bounded arrays. Semantic update allocation includes immutable state records,
explicit payloads and serialization; no Unity reflection is involved. Existing
high-frequency avatar encoder/decoder retains its zero-allocation checks. C does
not claim zero-allocation future world adapters, which do not yet exist.

## Verification

The synthetic fixture captures A=10/B=20/C=30 plus 32 filler entities at revision 35.
Immediately after capture A becomes 11 (36), B despawns (37), D=40 spawns (38).
The baseline is unchanged. Ack drains all three mutations, and both registries end
at 38 with 35 live entities plus B's tombstone. It exercises multiple actual chunks,
main-thread staging and explicit readiness without altering real objects or saves.
Pure tests cover collision injection, generations, bounds, duplicates/conflicts,
timeout, checksum, version rejection, backlog overflow/resnapshot and malformed codecs.
Impairment uses real datagrams with latency 60 ms, jitter span 180 ms (reordering),
10% loss and 20% duplication. Runtime and final totals are recorded separately in
[milestone-c-acceptance.md](milestone-c-acceptance.md).

## Milestone D state and recovery

Door and window semantic payloads use the same baseline and ordered revision stream.
Baseline application runs inside a silent network-apply scope; later deltas use the
original door motion with guarded presentation. A stale interaction requests a new
snapshot through the existing C recovery path. Adapter application failure releases
old bindings and gates readiness while retrying. Repeated recovery is bounded to
three requests per sync attempt and then ends visibly in a failed state.

Synthetic tests verify these paths, including a concurrent door mutation and window
state/hash agreement. Real populated-scene baseline, concurrent mutation, presentation,
reconnect and scene reload pass; see [milestone-d-acceptance.md](milestone-d-acceptance.md).

## Milestone E world and owner ordering

Loot spawn, state and pickup tombstones use the same immutable baseline cut and
post-baseline delta backlog. Concurrent collection cannot revive a baseline's old
world copy. There is no pickup history replay on join. Full personal inventory goes
only to its owner as explicit bounded semantic records, not serialized Unity objects.
Owner chunks carry session, epoch, inventory revision and the required world revision;
inventory applies after that world revision and any native transaction have settled.
Old/equal revisions never overwrite newer owner state. Reconnect uses the retained
host runtime record for the bound PersistentCoopPlayerId.

Owner inventory assembly is bounded to eight chunks, 128 physical units and 2863
bytes with a five-second timeout. Sends share reliable-channel backpressure. A
transaction result may precede its queued owner snapshot; local mutation observation
waits for both resulting revisions. This prevents a legitimate pending drop decrement
from becoming a false consumption. Reconciliation changes minimal native counts/UI
through a guarded adapter and retries at most three times. See
[Milestone E implementation](loot-inventory-sync.md) and its acceptance report.
