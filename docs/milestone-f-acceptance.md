# 0.6.6: Client driving and shared economy

Protocol 9 rejects older peers; both machines need 0.6.6.

Client motion previously restarted interpolation at the previous packet target,
including after velocity extrapolation. Native Rigidbody interpolation was also
active while networking wrote the Transform. A bounded 32-sample sender timeline
now rejects old sequence/timestamp samples, renders 100 ms behind the sender,
holds the newest position during loss, and writes the kinematic Rigidbody alone.
The detached driver reference follows after vehicle presentation. Host native EVP
physics, the 20 Hz / 86-byte stream, rear seat and original controller remain.

Milestone F extends InventoryAuthority and its reliable channel, reservations,
completion receipts and bounded snapshots. Host save money seeds one wallet.
The Host derives original shop prices/unlocks, native sale bonuses and venue
eligibility. A commit updates the wallet and personal/storage ownership together.
Duplicates, item races, concurrent overspending, stale revisions, failed native
operations and scene cancellation cannot grant a second canonical item/payment.
Personal inventory identities and stored item IDs survive reconnect and scenes
within the same running Host. Disk persistence of separate cooperative personal
inventories remains outside this slice.

Original Shop_Manager.Buy_Item, Inventory.Sell_Item_NewSystem and
Inventory.MoveItemToStorage execute after reservations. Supported ordinary
ItemID units include loot and tools; heavy, special, story/mission, cardboard,
hackable, keys, drones, cars and car parts remain excluded. Native hideout locker
and car-box storage use their original capacity/quantity rules. Original physical
interfaces remain. Client money/item/storage save writes are protected, and its
original local quantities/wallet/storage are restored on leaving cooperation.
Host native save keys retain canonical wallet/storage. Host save-time vacuuming
now executes the original native call for tracked supported loose items, moving
their existing instance into the original locker and retiring the world entity.
Client duplicate vacuuming remains suppressed. Existing car-box and locker
identities retain their native meanings; no unrelated lootbox is repurposed.

Native integration follow-up (2026-10-09): purchasing binds the actual nearby
computer website instead of an arbitrary global catalog button. The Host derives
native price/unlocks; the quoted price is installed only during original purchase
execution and restored in finally. Selling retains the original chain-sale flag.
Offline and Hosting alone bypass economy/pickup/drop/equipment interception;
Host native changes are observed for the next baseline without overwriting the
absent player's personal inventory. Solo native drops retain their instance and
destroyed native pickups retire their world records. Computer sit/stand transitions
remain available to networking while the original controller is disabled, avoiding
a readiness/time freeze without changing the native animation or controller.

The shopping/storage completion follow-up clears obsolete native locker buttons
before rebuilding an already-open interface after a counterpart transfer. Delayed
sale/storage execution also requires the same live selected button, direction and
personal quantity, so a refreshed selection cannot execute a stale reservation.
A reproduced Host-to-Client switch retained the old Host inventory revision and
rejected the new Host's lower baseline. Joining now clears only the cached Client
view/assemblers; the former Host authority and original native profile remain.
No wire fields, valuation rules, native capacity rules or vehicle behavior changed.

Validation: **734 existing checks passed in one full run**. After the late
Host-switch fix, six focused real-UDP checks passed, including one new baseline
regression and the existing purchase, cross-player locker and reconnect flows.
The earlier 11 focused economy checks passed as well. Four Release projects build
with zero errors; the existing NU1900 audit-feed warning remains. Repository
validation and whitespace checks pass. The full suite was not repeated after the
late fix, in accordance with the requested test limit.

Current task native evidence: **six owned sessions, approximately 14 minutes 40
seconds total**, every session below five minutes. Early runs exposed fixture
timing/selection errors and the production Host-switch baseline bug; failures were
retained explicitly. The final bounded run passed eight Host and seven Client
checks using the original shop, seller and hideout locker interfaces. Both roles
purchased one exclusive personal item with an exact wallet charge, sold an owned
item into the shared wallet, and deposited/withdrew the same locker instance.
Managed counterparts withdrew and redeposited that instance while each native
role's locker UI remained open; canonical ownership invariants stayed clean.
The Client flow used reliable UDP transactions with a managed Host that quoted
through the production native catalog/valuation code. It used the one available
native profile, so it does not establish independent Host/Client unlock/bonus rules.

This is native acceptance in one game process with a managed counterpart,
**not real two-machine acceptance or full vanilla equivalence**. Two human games
with independent profiles, supported shop/seller variants, full native economy
reconnect/scene UI cycles and automatic cargo still require acceptance. The existing
managed reconnect/scene coverage passed; separate cooperative personal inventories
still do not persist across a Host process restart. No Milestone G work was started.

Direct BepInEx deployment retains **0.6.6 / protocol 9** and verifies all three DLLs
against the final build. Fresh-task saves, PlayerPrefs, configurations and the
installed diagnostics are preserved/restored; all 183 original DLL/EXE fingerprints
remain unchanged. Local ignored session/protection artifacts contain the evidence.
