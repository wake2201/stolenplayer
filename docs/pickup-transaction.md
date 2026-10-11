# Pickup, inventory and transaction boundaries

Evidence: R8 UI drop and opt-in backed-up dropped-crowbar fixture, live ItemID
prefix/postfix observation, delayed world-object/quantity samples; targeted static
Pickupable/ItemID/PlayerScript/SaveGame inspection. No original loot or mission
reward was artificially granted. The fixture called the original PickUp method
after bounded harness validation; original aiming/raycast eligibility was not tested.

## Verified transaction timeline

| UTC / relative time | Observed result | Classification |
| --- | --- | --- |
| UI drop before fixture | Crowbar definition 0 quantity became 0; world `dropped`, wasDropped=true; inventory retained flashlight, weight 0.1 | VERIFIED_RUNTIME |
| 21:18:57.594 / start | PickUp prefix/postfix in same frame 32837; quantity 0, weight 0.1, money/XP 0 | VERIFIED_RUNTIME |
| 21:18:57.701 / 0.107 s | World object still exists, quantity 0, weight 0.1; pickedItems count 1 | VERIFIED_RUNTIME |
| 21:18:58.069 / 0.475 s | Item_Picked args `dropped, 0, true, false`, frame 32894; quantity 0→1, total weight 0.1→0.2 | VERIFIED_RUNTIME |
| 21:18:58.302 / 0.708 s | World object destroyed, quantity 1, weight 0.2; inventory UI shows crowbar + flashlight | VERIFIED_RUNTIME |

No money, XP, skill/extra/special points changed in this branch; no mission, save or
AI/noise method from the finite traced allowlist appeared. This is not evidence
that reward-bearing theft has no such effects. No double-claim network race was tested.

VERIFIED_STATIC: PickUp starts a coroutine. Delays around 0.05 + 0.2 + 0.225 seconds
explain why its ordinary method return precedes Item_Picked. Intermediate operations
affect colliders/Rigidbody, pickup layer/animation/UI/audio; Item_Picked updates
definition quantity, weight and pickup/save bookkeeping; the coroutine removes the
world item afterward. Money, heavy objects, dropped tools, normal theft, parcels,
contracts, daily respawn and story branches have different mutation paths.

The transaction boundary spans eligibility/reservation → asynchronous completion →
inventory/reward/persistence mutation → world removal. A Harmony postfix on PickUp
alone is not a successful-claim event. Item_Picked is a useful inventory mutation
boundary, but does not cover every branch (money and heavy carrying in particular).

## Inventory model

VERIFIED_RUNTIME: PlayerScript owns 452 ItemID references, weight/capacity,
money/debt/XP, current item and references to inventory UI. Starter capacity 15 kg;
crowbar + flashlight 0.2 kg; money 0 and debt 500,000. ItemID combines definition
metadata with mutable quantity/storage/locker fields. ID/index identifies the
definition; no universal persistent physical-item GUID was established.

VERIFIED_STATIC: Item_Picked performs item-name/save-key and parcel/mission
bookkeeping; non-dropped eligible branches can award XP (including scaled extra
reward), special points/achievements. Drop creates a world representation and
changes quantities/weight. Heavy pickup instead parents to the player's carried
root and adjusts physics; VanHeavyItems stores cargo using slots/local transforms.
Selling/buying involves UI-selected definitions, quantity and money. Tool durability
is partly player fields (`crowbarLife`, `batonLife`, `bltHealth`), requiring specialized
state rather than a fictitious universal ItemInstance class.

Backpack/skill upgrades affect capacity and behavior; maximum upgraded capacities,
all tool-slot rules, locker operations, heavy transfers, sell rewards and two-player
economic races remain UNKNOWN in runtime. Definition metadata can be shared by build;
physical loot/cargo needs a separate host-created instance ID and owner/location.

## Proposed host transaction (not implemented)

1. Client requests claim with actor/entity/epoch/revision and unique request ID.
2. Host validates range, interaction eligibility, capacity, action state and availability;
   atomically reserves the entity. Competing player gets a definitive rejection.
3. Host drives the appropriate original branch once and observes its actual completion.
   Local hands/animation can respond immediately but grants remain pending.
4. Commit quantity/weight/durability, reward ledger, mission contribution, persistence
   tombstone and entity removal as one logical result. Send reliable idempotent commit.
5. On timeout/disconnect, reconcile from authoritative revision; do not undo after a
   committed reward or replay a completion method. Reservations expire only while pending.

Do not invoke receiver PickUp/Item_Picked to apply replicated state: they target the
cached local player and can award/save again. Dedicated adapters apply presentation
and authoritative state. Save flush is a separate durable checkpoint; item preference
mutation does not itself prove disk persistence. Initial snapshots include removed loot
and pending/committed transaction revisions to prevent late-join duplication.

Minimum remaining fixtures: one backed-up authored non-tool theft with eligibility,
XP/noise/mission/save observation; one money pickup; one heavy pickup → cargo → drop;
two simultaneous claim requests; disconnect during reservation and after commit.
