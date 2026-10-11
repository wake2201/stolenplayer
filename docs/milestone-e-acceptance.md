# Milestone E acceptance evidence

Test date: 7–8 October 2026 (local time). Mod 0.6.0 / protocol 5.
Accepted predecessor: `1054083e64aa3c6be633be94bb0407242567200c`.

**ACCEPTED — TWO-GAME VALIDATION DEFERRED.**
**TWO-GAME ACCEPTANCE = DEFERRED.**

The supported adapter uses the accepted B/C/D architecture. Economy, shared-wallet
mutation, storage synchronization, missions, AI/police, vehicles, travel, cooperative
disk saves, Steam transport and release packaging were not implemented.
Contracts, evidence classifications and exclusions are in
[loot-inventory-sync.md](loot-inventory-sync.md).

## Static contracts and physical ownership

VERIFIED_STATIC: `Pickupable.PickUp` starts `PickingItem`; `ItemID.Item_Picked`
increments one native aggregate unit/weight; `PlayerScript.Update` uses definition
maximum and `maxWeight + 5`; `Inventory.DropItemNewSystem`, `ItemID.Drop_Item`,
definition `drop_object` and Transform `dropPos` create/remove one unit and update
the original backpack. Original Item_Button paths permit minimal reconciliation.
PlayerScript equipment uses original SelectCrowbar/HideWeapons. Definition 3's
independent torch checks quantity and Inventory.hasFlashlight. Unknown slots remain
UNKNOWN. A universal vanilla physical save GUID is UNKNOWN.

VERIFIED_RUNTIME: 452 native definitions; crowbar and flashlight each weigh 0.1;
native starter inventory weighs 0.2. Pickup invocation/return precede quantity and
world removal. Offline dropped crowbar reconnaissance measured approximately
0.474 seconds to quantity/weight change and 0.708 seconds to disappearance.
Production completion uses quantity, weight and observed final removal together,
never method return. Connected client completion measured approximately 0.55 s
from reservation; host original execution measured approximately 0.52 s.

NetworkItemInstanceId is a nonempty Guid. Static origin hashes runtime host token,
epoch and EntityRef/generation; host state supplies the client's physical identity.
Starting native units receive independent host IDs. Dropping preserves that ID
while the C registry allocates a new dynamic EntityRef. ItemDefinitionId identifies
the ItemID definition; it cannot identify a physical copy. No Unity InstanceID,
memory address or object name is a network identity.

Inventories are bounded managed records keyed by PersistentCoopPlayerId, with
uint64 monotonic revision and owned equipment. ActorRef binds that ID to an
authenticated current connection. The host needs no second Unity player. Owner-only
explicit snapshots have a required world revision; peers get equipment presentation.
The first partner identity is pinned for the host runtime. A new partner gets fresh
instances of the verified starter subset, not copies of host-acquired loot.
Existing canonical runtime inventory always wins on reconnect. L will handle disk
restoration/import. No cooperative inventory save file was added.

## Automated acceptance

**476 checks pass: all original 367 plus 109 Milestone E checks.** All projects
build. Cached NuGet audit data produces NU1900 because the package audit endpoint
is unavailable; compilation has zero errors. Tests require localhost UDP access;
sandboxed runs without that access failed the existing raw handshake fixture and
are not counted as passing runs.

Tests cover codecs/physical identities, sorted canonical hashes, old/equal/wrong
owner revisions, bounded snapshots, capacity plus five/max_amount, reserved weight,
successful/duplicate pickup and completion, same-item and independent-item races,
five-second timeout, disconnect/scene cancellation, world tombstones, malformed
actor/session/epoch/generation/identity/revision, wrong ownership, unsupported
definitions, dynamic drop/duplicate/transfer, equipment ownership/removal, legitimate
native removal, baseline plus concurrent pickup, reconnect and owner privacy.

Failure tests include presentation exceptions, invalid spawn identity, unregistered
drop cleanup exceptions, notification failures after canonical tombstone/spawn, and
inventory/result observer exceptions. Committed ownership still settles exactly
once and no pending reservation survives an observer exception.

Stress tests hammer 400 duplicate/racing requests plus 200 duplicate completions;
exactly one inventory owns the unit, with one tombstone and no pending transaction.
Thirty-two alternating drop/pickup transfers preserve identity and total ownership.
10,000 fuzz inputs across new parsers reject safely. Real UDP tests additionally use
60 ms latency, 180 ms jitter, 10% loss and 20% duplication, then reconnect the same
persistent identity and compare inventory/world hashes.

Five independent-process B/C transport regressions pass: clean transforms, clean
world snapshots, impaired world snapshots, abrupt host and abrupt client loss.
World runs converge at revision 38 with six chunks and one tombstone.

## Native world registration

MadisonSt/MainScene registers **589 supported authored loot entities**, alongside
the unchanged 950 doors and 184 windows: **1,723 static entities**, zero collisions.
There are 320 excluded Pickupable candidates, including previews/prototypes and
objects without verified supported identity/capability. These counts describe this
tested scene/save/build, not universal game coverage.

Independent native loads exposed root procedural clones with different definitions
(acoustic guitar versus frying pan). These unverified spawn origins are excluded
from static registration. This corrected a detected baseline definition mismatch;
the adapter fails closed instead of silently binding the wrong item. Native drop
prefabs/prototypes are also excluded from authored loose-loot registration.

Supported ordinary isLoot definitions pass verified negative filters. Money, heavy
carry, mission/story/contract, parcel preference side effects, keys, drones, cars,
parts, hackable/cardboard/special items and container/cargo-owned items are excluded.
Tools are limited to crowbar 0 and flashlight 3. Worn crowbar drop is blocked:
crowbarLife is player-global, not a proven transferable instance condition field.

## Real game host with harness client

Protected session `20261007-211614-Both` and harness `20261007-211910-client`
provide native host drop, automatic remote claim, duplicate requests, remote drop
and native host repickup of the **same physical crowbar**.

- Normal original host backpack drop creates dynamic entity 1, inventory revision 2.
- The harness sends a valid in-range claim plus 20 duplicate requests. One
  reservation/commit gives its personal canonical inventory one additional unit;
  the host inventory remains unchanged. This harness has no native Unity inventory.
- Harness drop creates dynamic entity 2 preserving the physical ID. Its inventory
  loses that unit exactly once. Final owner hash:
  `86AE01BA40215BC2870ACF030E1C60A524FCD23AAA1E6EEC30192E6389DF7251`.
- Host invokes original pickup through the labelled, range/LOS-validated development
  fixture; delayed native completion returns crowbar quantity to one and weight to
  0.2. Dynamic loot count becomes zero; two tombstones and zero invariant violations.
  Host owner hash:
  `AF2EB6B35797BF0BBF0D635BDA58109F1F12C9D46BE300D1FEBAB16DC8B72FFE`.

The harness actor uses sampled native location and production host range/LOS
validation. No teleport, range bypass, fake second PlayerScript or manual approval
is used. Synthetic completion evidence is clearly separated from native execution.

## Harness host with real game client

Protected `20261007-213347-Both` completes two original crowbar drop/pickup cycles.
Drops use the normal original backpack UI. Pickups use the labelled original-path
fixture, with genuine native coroutine/count/weight/removal observations. Final
inventory revision 5/count 2/weight 0.2; no pending transaction or invariant error.
Owner hash:
`28A2A005EDEC086CEA054DC8919E1A572FF77DFF3AD61BC2071D70D125D7E21B`.
World revision 1727/hash:
`205628B59097C733852968EB95D24A23D3AD75DBD5CB1C37ACAE759AA1EBE233`.

Protected `20261007-215747-Both`, harness `20261007-220024-host`, additionally
verifies **normal native E pickup** of the flashlight after normal backpack drop.
Bounded original-camera input aligns the native raycast; E is actual UI input.
The original taking prompt appears; one intent produces one request, reservation,
native coroutine and commit at 22:03:54 UTC. Physical ID
`ed3195cd29ba47b2adb8972156ad1a0d` persists through drop/repickup.
Native weight goes 0.2 -> 0.1 -> 0.2. Dropping its lit instance produces
flashlight=0, torch=false, light=false; reacquisition restores ownership without
forcing the light on. Offline normal F previously produced torch=true/light=true.

After reacquisition inventory revision 3/count 2/hash:
`733714CFFD5B8689994545D8B9163DC24A48B51EC88C103F040785628EACAEB4`.
World revision 1725/hash:
`FF106642C64BDA45AC7C70342D191189DC87E98FED1156C8085C426DE8E3DD2E`.
Both hashes match the synthetic host's authoritative state.

Normal mousewheel equips the owned original crowbar, advancing revision 4.
Normal left click invokes original CrowbarHit at 22:04:30.786 UTC. Disconnect,
local unequip, and reconnect to the **same still-running host** restore equipped
crowbar through original SelectCrowbar, revision 4/count 2/hash:
`DCEE141F8723C5D7900751FD98A7F9AC7132402E5EE12340109CA44021CF6D8E`.
Fresh WorldReady at 22:04:58.585 retains world revision 1725 and one tombstone.
No historical pickup replay is needed. Inventory/native weight remains 0.2.

Cold full-scene baseline first exceeds C's existing 30-second assembly window,
then bounded recovery reaches WorldReady (1,309 chunks, approximately 502 KiB).
Warm reconnect completes in approximately eight seconds. No inventory transaction
is admitted before both readiness and the owner baseline. Production adapter
exceptions are bounded/logged; reconciliation retries three times per revision.

## Local, avatar and lifecycle regressions

Final protected host session `20261007-220728-Both` uses the final 476-check
artifact. Native House 102 pickups successfully acquire damaged uPhone definition
55, teapot 45 and wall clock 46. They commit independently at 22:20:41.215,
22:20:50.035 and 22:20:56.814 UTC, with distinct physical IDs and C tombstones.
The original wall-clock PickUp returns at 22:20:56.189; Item_Picked runs at
22:20:56.771 and the observer commits at 22:20:56.814. This ordinary-item trace
again proves that method return is not completion. The range/LOS-validated
development pickup fixture is explicitly labelled; no interaction range was
bypassed.

Final audit at 22:21:06.404 reports inventory revision 6/count 5, native weight
4.7, crowbar quantity 1/life 85, zero native/canonical pending transactions and
zero ownership invariant violations. Owner hash:
`F433EB6DC0010CE77109680D141CB32A9A28B001C633ABEC7793BD85CFFB0E75`.
World revision 2071/hash:
`2BC17C872ABCD9150CFEEE31D9258443A4E8145D8792908C5D9C8A57D75D5B60`.
The owned original crowbar forces House 102's locked gate at 22:12:46 UTC;
D commits its unlocked/broken-hinge state. Subsequent native door interactions
continue to produce D revisions. Final local audit retains all original object
identities, six original cameras, one listener, enabled PlayerScript/controller.

The original local PlayerScript, Inventory, motor, camera behavior, controller,
hands and tools retain native object identities. Audits in the native host/client
sessions show original identities unchanged, original cameras/listener retained,
and functional local crowbar equip/swing/CrowbarHit/unequip/drop/reacquisition.
NetworkAvatar remains presentation only: one avatar/animation, seven valid visible
renderers/materials, no forbidden functional components. The harness host's
presentation stands three metres ahead to avoid placing its visual inside the
local camera; the real actor position used for validation is unchanged.

Native police may catch a public crowbar sighting or house trespass during fixtures.
Police are intentionally untouched and this does not constitute police sync.
Return to menu at 22:05:53 follows D's scene disconnect policy. F8 reports Offline,
epoch 5, world revision zero, registry 0/0/0, zero loot/drops/native pending;
audit reports zero avatar roots/animations/equipment. Pending scene cancellation
and reconnect-during-reservation are separately proven in managed/UDP fixtures.

Offline original inventory/drop/pickup/crowbar, incoming phone interaction and
flashlight toggle remain native. Connected original inventory UI and first-person
gameplay are retained. All D interaction tests remain passing; earlier accepted D
native door/window evidence remains applicable. No accepted local or avatar stack
was replaced.

## Explicit remaining limits

- True simultaneous two-game pickup/drop/UI/LOS/capacity acceptance is DEFERRED.
- Ordinary authored loot has native pickup evidence for damaged phone, teapot and
  wall clock. Other supported definitions still need category-specific runtime
  trials; registration does not establish universal item support.
- Capacity/weight/full and aggregate quantity-two semantics have strong automated
  plus static evidence; a naturally full native backpack fixture remains LIMITED.
  No invented variable-quantity Pickupable or generic stacking rule was added.
- Specialized durability/condition, mission/container/cargo identity, consumable
  side effects and unknown equipment slots remain LIMITED/excluded.
- Scene travel disconnects; disk persistence/import is L. New partner initialization
  supports only the verified starter subset, not arbitrary client save inventories.
- Dynamic loot physics presentation is host authoritative; native Rigidbody details
  restored on abort are limited to the verified basic collider/gravity flags.
- Long native stress, native disconnect at a precisely controlled coroutine point,
  and genuine two-peer simultaneous races remain additional runtime fixtures.

## Final validation

All projects build; 476 checks pass. The final artifact also passes all five
independent-process regressions, including impaired delivery and both abrupt
disconnect cases. Multiplayer and reconnaissance validators pass, as does
`git diff --check`. All 183 original DLL/EXE fingerprints are unchanged.
Every protected test session's save backup is restored byte-for-byte. The final
session restores the original file set with zero extra test-created files.
Both multiplayer/diagnostics configurations and the diagnostic scope are restored
byte-for-byte, and the recorded loader restoration is complete. The coherent
Milestone E commit is identifiable by `feat: synchronize authoritative loot and
inventories`; no predecessor commit is amended.
Raw saves/config backups, manifests, native logs and screenshots stay ignored.
No proprietary game assemblies/assets or machine-specific paths belong in Git.
Milestone F is the next recommendation after E acceptance; it has not started.
