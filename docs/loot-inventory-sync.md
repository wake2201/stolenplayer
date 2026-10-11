# Authoritative loot and personal inventory

Milestone E, mod 0.6.0 / protocol 5. Acceptance evidence and explicit limitations
are in [milestone-e-acceptance.md](milestone-e-acceptance.md). B/C/D remain the
foundation. There is no separate loot transport, registry or dynamic allocator.
Economy, storage synchronization, missions and cooperative disk saves are excluded.

## Reconnaissance contracts

| Finding | Evidence | Consequence |
| --- | --- | --- |
| `Pickupable.PickUp()` starts `PickingItem()` and returns immediately | VERIFIED_STATIC + VERIFIED_RUNTIME | Method return is never a commit |
| `PickingItem` destroys its marker, then Rigidbody/disables colliders, calls `ItemID.Item_Picked`, finally destroys its GameObject | VERIFIED_STATIC + VERIFIED_RUNTIME | Observe quantity/weight and final removal together; defer only tracked final destruction |
| Dropped crowbar quantity/weight changed about 0.474 s after invocation; object disappeared about 0.708 s later | VERIFIED_RUNTIME, protected 7 October recon | Keep a bounded asynchronous transaction |
| `PlayerScript.Update` validates `canInteract`, carry/vehicle, `cant_pick_reason`, `max_amount` and `maxWeight + 5` | VERIFIED_STATIC | Both native eligibility and managed host reservation capacity use these verified rules |
| `ItemID.Item_Picked` adds one `item_amount` unit and its weight; preference/parcel/reward branches also exist | VERIFIED_STATIC + VERIFIED_RUNTIME for crowbar | Each supported physical Pickupable is one unit; native aggregate quantities retain separate canonical physical identities |
| `Inventory.DropItemNewSystem` instantiates definition `drop_object` at Transform `dropPos`, sets dropped fields, calls `ItemID.Drop_Item`, updates `Item_Button` UI | VERIFIED_STATIC + VERIFIED_RUNTIME | Reserve ownership before native drop; bind its unique new object or instantiate the verified native prefab remotely |
| `PlayerScript.item_IDs2` has 452 definitions; definition counts are aggregated | VERIFIED_RUNTIME + VERIFIED_STATIC | ItemID is a definition, never a physical GUID |
| `crowbarLife` lives on PlayerScript, starts at 100, decreases on forced entry and resets on break | VERIFIED_STATIC; default 100 VERIFIED_RUNTIME | Worn crowbar transfer is excluded until a condition adapter is proven |
| `Inventory.buttons`, `disabledButtonsDestroy`, `Item_Button.LoadAmountText`, `InstantiateNewItemX` and `ItemID.SetTags` support limited UI reconciliation | VERIFIED_STATIC | Apply minimal count/UI changes; never call ClearInventory or replay Item_Picked rewards |
| `PlayerScript.Update` checks definition 3 quantity before toggling `torch`/`LightObj`, and extinguishes it when `Inventory.hasFlashlight` is false | VERIFIED_STATIC | Mirror this ownership flag during connected reconciliation and extinguish an unowned light without inventing a handheld equipment slot |
| Universal per-instance save GUID, specialized metadata and all equipment-slot mappings | UNKNOWN | Do not fabricate fields or mappings |

Proprietary decompilation, dumps, manifests and trace evidence remain ignored under
`.local`. Scene-resident drop prototypes and Canvas previews are excluded even if
they carry Pickupable. Money, heavy carry, story/mission/contract, keys, drones,
cars/parts, cardboard/hackable/special items and container/cargo-owned Pickupables
are excluded from the general adapter. Only crowbar definition 0 and flashlight
definition 3 are enabled among tools; ordinary `isLoot` definitions pass the
verified negative capability filters. UNKNOWN slots stay UNKNOWN.

VERIFIED_RUNTIME: independently loaded scenes produced different untracked root
clones (acoustic guitar versus frying pan). These native procedural/spawner objects
have no verified authored identity and are excluded conservatively. They are never
assigned a static network identity from their clone name. Verified authoritative
drops use C's host-assigned dynamic IDs and remain supported.

## Physical identity and ownership

`NetworkItemInstanceId` is a nonempty Guid value independent of Unity. Static
physical origins hash a host runtime origin token, SceneEpoch, NetworkEntityId
scope/value and generation. Clients bind the host's physical ID from registry
state. The origin token persists through reconnect within this host runtime;
cross-process disk identity belongs to L. Native starting inventory units receive
host-assigned IDs. Dropping preserves the physical ID while C allocates a fresh
dynamic EntityRef. No raw InstanceID, address or name is a wire identity.

`InventoryState` is a pure managed record keyed by `PersistentCoopPlayerId`, with
monotonic uint64 revision, bounded physical item records, verified capacity and an
optional owned equipped instance. `InventoryItemState` contains instance ID,
definition ID and quantity one. Native `ItemID.item_amount` aggregates physical
records of the same definition. Two same-definition tools remain distinguishable.
No unproven durability or value metadata is serialized.

The host's original Inventory remains its gameplay source, observed at supported
native transaction boundaries. The client also retains its original Inventory,
UI, PlayerScript, hands, controller/camera and functional tools. Its canonical
personal state resides on the host without a second Unity player/inventory.
Only the owner receives its full inventory; peers receive validated equipment
presentation. The host exposes only compact counts/revisions/hashes normally.

The identity config supplies a stable player Guid; runtime ActorRef binds it to
the authenticated connection/session slot. The first partner identity is pinned
for this two-player host runtime; a different partner is rejected, preventing
unbounded inventory creation. This is session membership, not Steam authentication.
Existing runtime records always win on reconnect. A new partner receives fresh
physical instances of the verified starter crowbar/flashlight subset, with the
host's initial capacity. Host loot is never copied into the joining inventory.
Importing arbitrary client saves and final disk restoration are deferred to L.

## Transaction lifecycle

ReliableOrdered channel 0 carries semantic Pickup, Drop, Equip, Unequip and a
bounded NativeRemoval sink. Movement remains Sequenced channel 1. Messages 28–32
are explicit requests, results, completion evidence, identity and owner snapshot
chunks. Exact protocol/build/mod matching rejects older peers.

Pickup request: SessionId, SceneEpoch, RequestId, connection ActorId, EntityRef,
expected entity revision and expected inventory revision. Drop/equipment requests
reference an owned physical ID, never an arbitrary item definition. Drop also
contains finite bounded placement coordinates within 2.5 m of the actor. Fixed
80-byte request, 46-byte result and 30-byte completion codecs reject trailing data,
all truncations, unsupported actions/identities and invalid coordinates.

Requested -> validated -> Reserved -> native ExecutionPending -> CommitObserved
-> Committed, or Aborted. TransactionId is separate from RequestId. Reservations
are item-scoped; different actors/items can proceed independently. Capacity and
definition maximum include outstanding reservations. Host ordering selects one
winner for a same-item race. The request cache returns the existing reservation
or settled result; the monotonic request floor still rejects IDs evicted from the
bounded cache. Duplicate completion never grants another unit or spawns another
drop. No manual host permission UI exists.

Host validation checks authenticated actor, session/epoch, WorldReady, physical
entity/generation/type/capability/availability, tombstone/reservation, revision,
definition, ownership, capacity/weight and target. Production pickup uses fresh
peer pose, native collider geometry and first-hit occlusion within native 2.25 m
reach plus the existing 0.5 m network tolerance. Local intent uses native 2.25 m.
Harness-host checks are explicitly synthetic distance validation, not Unity LOS.

Host pickup reserves synchronously and immediately allows the original coroutine.
Client intent is suppressed until automatic host reservation; then the exact
original PickUp runs. A narrow iterator transpiler preserves original marker and
Rigidbody destruction and defers only the tracked final GameObject removal. The
observer requires native quantity increment, verified weight increment and final
removal observation before reporting completion. Bounds use real network time,
not a paused game coroutine. Native rejection never becomes canonical ownership.
Completion evidence cannot select a different item/definition/owner; the host
commits only the reserved record. This is cooperative client execution evidence,
not attestation of an unmodified hostile executable.

On commit C creates the tombstone and the owner's inventory revision advances.
Snapshots/backlog reject late world state. On rejection, timeout, disconnect or
scene invalidation, stop the tracked coroutine, restore the same original object's
layer/colliders/basic Rigidbody flags and reconcile counts. Recovery never clones
loot. A final destruction deferral also retains static bindings for reconnect.
Dynamic tombstones release/destroy their presentation. Obsolete Unity references
and pending bindings are released at scene boundaries.

Drop reserves an owned physical unit before the original UI call. The adapter
observes exactly one new dropped object and a one-unit native decrement. Host
native drops bind that object; remote drops use the original definition prefab and
the bounded approved placement. The client's provisional native object is hidden
until the C spawn arrives, then destroyed after success. On abort it is destroyed
and ownership/counts are restored. A failed host spawn preserves canonical
inventory. Equipment is cleared when its physical item leaves inventory.

## Reconciliation, ordering and diagnostics

Apply authoritative counts through a guarded minimal adapter: preserve unsupported
native definitions, original tool references and UI objects; update/add/remove only
affected backpack rows and recompute actual native weight. Do not clear storage,
replay pickup rewards, or reconstruct PlayerScript. Restore only the proven crowbar
slot through original SelectCrowbar/HideWeapons; unknown slots remain conservative.
Flashlight ownership updates the original `hasFlashlight` flag and clears the
original torch/light only when its native quantity reaches zero. Its independent
toggle remains original; this does not infer a network handheld slot.
Native negative changes settle through an ownership-only removal sink, so consumed
supported units are not resurrected. Unsolicited positive changes never grant
canonical ownership. Full consumable/special-tool behavior is LIMITED.

Owner snapshots are explicit records, max 128 items / 2863 bytes, with 384-byte
chunks and a required world revision. Apply inventory only once that world revision
has arrived and local execution has settled. Strict ordered assembly, 5 s timeout,
exact lengths/owner/session/epoch and monotonically newer inventory revisions
prevent stale overwrites. Reconciliation retries at most three times per revision;
failure is logged and ownership-changing observation is gated until recovery.

Hard bounds: two players, 452 definitions, 128 physical inventory units, quantity
one, 16 pending transactions, 5 s reservations, 256 cached results, 32 new requests
per actor per second, 128 live dynamic drops, and C's scene entity/tombstone bounds.
Reliable owner snapshot sends share C's queue backpressure and a four-packet budget.
Adapter bindings cache EntityRefs rather than scanning the entire registry per item.

Canonical inventory SHA-256 hashes sorted semantic records, revision, capacity and
equipment. Development ownership checks detect duplicate physical IDs within/across
inventories, live world duplicates and world+inventory overlap after settlement.
F8 reports pending/reservations, committed/rejected/duplicate transactions, world
loot/drops, player revisions, owner hash and invariant violations. Transaction logs
correlate request/transaction, ActorRef/persistent ID, EntityRef, physical/definition
ID, expected and resulting revisions. No per-frame full inventory logging.

F can consume these owned physical records for future purchase/sale transactions
against the one shared Host wallet. J can subscribe to settled semantic events.
Neither subsystem is implemented by E. Scene travel remains D's disconnect policy;
runtime personal identity is ready for L serialization, with no cooperative save file.
