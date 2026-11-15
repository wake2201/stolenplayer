# 10 — Multiplayer synchronization matrix

Machine data:
- `data/sync_coverage.json`: per action, the 15 questions, status and mod references.
- `data/interaction_catalog.json`: original entry point, trigger, state, persistence.

Baseline: mod 0.17.0 working tree (W4.2D, uncommitted pieces included), `docs/PROGRESS.md` of 2026-11-15. Question 7 ("which original methods execute") is the `original_entry` column of the interaction catalog.

## 1. Status counts (48 action families)
| Status | Count | Meaning |
|---|---|---|
| COMPLETE / VERIFIED | 3 | movement/avatar (M2), player-car driving (M5.0), co-op sleep (M3.1.2): confirmed in game by the user |
| IMPLEMENTED / NEEDS MANUAL QA | 10 | loot, drops, storage, doors/furniture, locks, passenger, shop, sell, travel, save isolation |
| PARTIAL | 7 | window glass/sliding window (open report), window climb, hiding, parcels, BlackBay, skills, XP |
| MISSING | 19 | breakables, lights, cameras, alarms, safes, notes, digging, missions, story, heists, ATM, upgrades, gas, knock-out, drone, NPC routine/perception, police, car theft |
| BLOCKED BY DEPENDENCY | 2 | heavy items (lease family), story/drone/special pickups (story + drone sync) |
| UNSAFE | 6 | residence missions, contracts, jobs, payphone, pickpocket (client runs them on its sandbox, reward money excluded: progress drifts), cat reward |
| UNKNOWN | 1 | associate ownership in co-op |

Some IMPLEMENTED rows already have positive user QA on 0.16.0 for part of their scope (PROGRESS v0.16.1 §0: wallet, doors/drawers/furniture, connectivity, ordinary travel PASS). They stay "NEEDS MANUAL QA" because later versions (0.16.1, 0.17.0) are untested and the sideways-window report is open.

**No system is complete merely because its animation is replicated:**
- Carrying is visible, but heavy ownership is missing.
- The remote flashlight was removed.
- Window climbing moves the avatar without the climb clip.

## 2. Matrix
| Action | 1 P1 | 2 P2 now | 3 Other sees | 4 Consequences | 5 Saved | 6 Authority | 8 Messages | 9 Delivery | 10 Duplicates | 11 Rollback | 12 Reconnect | 13 Regions | 14 Simultaneous | 15 Prerequisites | Status | Mod refs |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| walk/run/crouch/jump | yes | yes | yes | n/a | position at checkpoint only | each player (host relays) | player snapshot 29 B 20 Hz | unreliable | none | n/a | resume from host live/checkpoint spawn | avatar hidden outside region | n/a | - | **COMPLETE / VERIFIED** | PlayerSync, RemoteAvatar (M2 accepted) |
| pick up / carry / drop heavy object | yes | no (refused by design) | P1 carry visible to P2 | no | trunk/van/shed only | host lease per census id | heavyLease req/grant, drop pose commit, deposit txn | reliable | high without lease | lease release on disconnect | object returns to scene state | region-scoped | first lease wins | region objects, CarBoxSync pattern | **BLOCKED BY DEPENDENCY** | WorldLootSync HeavyPrefix (refusal), W3 carried presentation |
| pick up ordinary loot / money / unique | yes | yes (all census scenes) | yes (object removed) | yes (wallet/inventory) | ItemDay_/Item_ via host | host | lootTxn + wloot chunks | reliable | rejected by LootBook | Stale -> restore | book reseeded from host keys | host scene catalogs | first committed wins | census, catalogs | **IMPLEMENTED / NEEDS MANUAL QA** | WorldLootSync, HostLootCatalogs (W4.1/W4.1B/W4.2D) |
| pick up story / drone / special item | yes | no (refused) | P1 pickup removes P2 copy (shadow claim) | no story effect on P2 | via host story | host | story txn + drone ownership | reliable | double story advance | stage check | index from host | - | stage check | story sync, drone sync | **BLOCKED BY DEPENDENCY** | WorldLootSync ClientPolicy |
| drop inventory item | yes | yes | yes | yes | locker at hideout | host entity table | drop/pick txns | reliable | entity id | lost like vanilla at reload | n/a | region-scoped | first wins | - | **IMPLEMENTED / NEEDS MANUAL QA** | WorldItemSync (W3) |
| car storage / hideout locker | yes | yes (car box), locker yes | contents shared | yes | host save | host | storageTxn, carbox lease | reliable | txn ids | journal | host record | car box per car region | lease | - | **IMPLEMENTED / NEEDS MANUAL QA** | CarBoxSync (car box user-verified M4.3.2b; locker W3 pending QA) |
| open/close door, furniture, sliding window | yes | yes (Madison/Hillville/hideout) | yes | noise to NPCs local only | none | host commit per region epoch | objTxn + wobj pages | reliable | version check | n/a | region init chunks | per region | reservation/version | census | **IMPLEMENTED / NEEDS MANUAL QA** | WorldObjectSync/Bridge (user PASS on 0.16.0 for doors/drawers/furniture, PROGRESS v0.16.1 §0) |
| pick / key / padlock / crowbar break-in | yes | yes (same regions) | yes | story/alarm effects not shared | none | host + reservation | objTxn Reserve/Set | reliable | reservation | release on cancel | init chunks | per region | reservation | census | **IMPLEMENTED / NEEDS MANUAL QA** | WorldObjectSync |
| cut / break / unlock window | yes | yes | yes (glass mirror) | alarm/police local | none | host | objTxn | reliable | version | n/a | init | per region | version | police authority for alarm | **PARTIAL** | WorldObjectSync glass mirrors; sliding-window report open (05 §6) |
| climb through window | yes | yes | avatar moves (no climb anim) | n/a | none | local | player snapshot | unreliable | none | n/a | n/a | n/a | n/a | - | **PARTIAL** | PlayerSync (no climb clip) |
| smash breakable / planks | yes | yes (local) | no | no | none | host | objTxn (new kind) | reliable | version | n/a | init | per region | first wins | census kind | **MISSING** | - |
| blinds, light switch, fuse box | yes | yes (local) | no | light affects NPC sight locally | none | host | objTxn (new kinds) | reliable | version | n/a | init | per region | version | census kinds | **MISSING** | - |
| disable security camera / panel | yes | yes (local) | no | no | discovery only | region owner/host | objTxn | reliable | version | n/a | init | per region | first wins | drone presentation | **MISSING** | - |
| trigger alarm (window, lock, camera, panel, car) | yes | yes (local police) | no | no | location alert (sandbox on P2) | region simulation owner + host for alert | crime event (offender id) | reliable | event id | n/a | chase ends on reload (vanilla) | per region | owner serializes | NPC/police authority | **MISSING** | - |
| crack safe | yes | yes (local) | no | loot inside shared | stat | host | objTxn | reliable | version | n/a | init | per region | reservation | census kind | **MISSING** | - |
| hide in spot | yes | yes | avatar position only | NPC perception local | none | local + presentation | snapshot flag | unreliable | - | - | - | - | spot occupancy UNKNOWN | NPC perception | **PARTIAL** | PlayerSync |
| read note | yes | yes (sandbox key, XP local) | no | no | <name> key | host | campaign txn | reliable | key | n/a | host keys | - | key | campaign txn | **MISSING** | - |
| dig hole | yes | yes (local) | no | no | stat | host | objTxn + loot | reliable | version | n/a | init | per region | first wins | census kind | **MISSING** | - |
| open parcel locker | yes | yes | parcel loot shared | parcel strings once on host | parcel keys | host | lootTxn side effect | reliable | book | - | - | - | first wins | - | **PARTIAL** | WorldLootSync parcel path (W4.2D); locker door state MISSING |
| accept residence mission | yes | yes (sandbox) | no | no | key | host | campaign txn | reliable | key value | n/a | host keys | global | first wins | campaign txn | **MISSING** | - |
| complete residence mission | yes | yes (sandbox; money excluded) | no | no | key, counter | host | campaign txn + step counter state | reliable | double reward | n/a | counter not persisted (vanilla) | global | first wins | story sync | **UNSAFE** | WalletPolicy excludes reward money |
| advance story stage | yes | yes (sandbox only) | no | no | Story | host | storyTxn(stage key, trigger id) + story index replication | reliable | stage check | n/a | index from host | global | stage check first wins | campaign txn layer | **MISSING** | - |
| accept / claim contract | yes | yes (sandbox; money excluded) | no | no | keys | host | campaign txn | reliable | double reward | - | - | global | first wins | campaign txn | **UNSAFE** | WalletPolicy |
| accept / claim job offer | yes | yes (sandbox; money excluded) | no | no | keys | host | campaign txn | reliable | double reward | - | - | global | first wins | campaign txn | **UNSAFE** | WalletPolicy |
| payphone mission | yes | yes (sandbox; money excluded) | no | no | keys | host | campaign txn + timer + runtime target entity | reliable | double reward | - | - | region of target | first wins | campaign txn, dynamic entities | **UNSAFE** | WalletPolicy |
| start heist | yes | travel only | travel shared | story not shared | Story | host | heist txn + joint travel | reliable | story++ twice | - | - | both travel | host decides | story sync | **MISSING** | TravelHooks (Select*), JointTravel |
| sell on BlackBay | yes | yes (money counted into wallet) | no | money yes, story no | - | host | campaign txn for story objects | reliable | story double | - | - | - | - | story sync | **PARTIAL** | WalletSync |
| pickpocket NPC | yes | yes (money excluded) | no | no | weekly key | region owner (NPC) + host | NPC request | reliable | double | - | - | - | owner | NPC authority | **UNSAFE** | WalletPolicy |
| hack ATM | yes | yes (local) | no | no | weekly prefs | host | objTxn + loot | reliable | double money | - | - | - | first wins | census kind | **MISSING** | - |
| buy in shop / phone | yes | yes | - | shared wallet | item at checkpoint | host wallet | p2report Spend | reliable | journal seq | Stale -> restore | journal replay | - | host wins | - | **IMPLEMENTED / NEEDS MANUAL QA** | WalletSync (user PASS wallet 0.16.0, PROGRESS v0.16.1 §0) |
| sell at pawn / car service | yes | yes | - | shared wallet | money | host wallet | p2report | reliable | journal | - | - | - | - | - | **IMPLEMENTED / NEEDS MANUAL QA** | WalletSync |
| garage, van, car/drone/hideout upgrades, tips, bank, stocks | yes | refused on client | - | - | unlock keys | host campaign txn | campaign txn | reliable | double purchase | debit-once | - | global | first wins | campaign txn | **MISSING** | WalletPolicy Refuse |
| learn skill / ability | yes | yes (sandbox) | no | no | skill prefs (P2 not persistent) | per player (decision) | player record extension | reliable | - | - | host record | - | - | decision: per-player progression | **PARTIAL** | DevTestMode only learns skills in test |
| gain XP / level up | yes | yes (local, not persistent) | no | no | XP keys (P1 only) | per player (decision) | player record extension | reliable | double rewards via unsynced missions | - | - | - | - | decision | **PARTIAL** | - |
| use sleeping gas | yes | yes (local) | no | no | GasAmount (player) | holder event + region NPC owner effects | gas on/off event, cloud spawns | reliable events, unreliable pose | double NPC effect | n/a | - | - | owner | NPC authority | **MISSING** | - |
| knock out NPC (crowbar, baton, tranquilizer) | yes | yes (local) | no | no | stat | region NPC owner | NPC request | reliable | - | - | - | - | owner | NPC authority | **MISSING** | - |
| equip / fly drone | yes | yes (local) | no (nothing sent) | no | item, upgrades | owner pilot + presentation | drone pose snapshot, ownership | unreliable pose / reliable ownership | - | - | - | 6 regions | - | presentation layer | **MISSING** | DRONE_SYSTEM.md |
| NPC schedules / movement | yes | yes (own copy) | different NPC positions | no | none | region simulation owner | NPC snapshots | unreliable | - | - | restart from hour | per region | - | region owner rule (exists for clocks) | **MISSING** | - |
| NPC sees / hears player | yes | yes (own copy, own player only) | no | no | none | region simulation owner seeing both | proxy + reaction redirect | - | - | - | - | - | - | NPC replication | **MISSING** | - |
| police chase / stars / arrest | yes | yes (own copy) | no | no | alerts (sandbox on P2) | region owner + per-player chase flag + host alerts | crime event, police snapshots, chase state | reliable + unreliable | - | - | chase ends on reload | per region | owner | NPC replication | **MISSING** | - |
| enter / drive / exit player car | yes | yes | yes (original car object observed) | collisions solid | fleet file | host fleet + holder | vehicle/vpark/vdoor | unreliable pose, reliable claims | holder rule | - | fleet | region vehicle rule | handoff lock | - | **COMPLETE / VERIFIED** | VehicleSync (M5.0 verified in game) |
| ride as passenger | yes | yes | yes | yes | - | host | vehicleRide | reliable | - | - | - | - | - | - | **IMPLEMENTED / NEEDS MANUAL QA** | VehicleSync M4.3 (user-tested, refinements pending QA) |
| steal / deliver region car | yes | yes (local car) | no | no | car keys | host | region car state + campaign txn | reliable | double car | - | - | - | first wins | region vehicle objects | **MISSING** | - |
| travel between regions | yes | yes | yes | fleet cars move | full save (P1) | host reservations | travel*, jointTrip | reliable | reservation | rollback | fleet | independent regions | first committed wins | - | **IMPLEMENTED / NEEDS MANUAL QA** | TravelHooks, VehicleSync (ordinary travel user PASS 0.16.0) |
| sleep / pass time | yes | yes (coordinated) | yes | yes | time at checkpoint | host | sleepStart/sleep/sleepShow | reliable | - | - | - | region clocks | - | - | **COMPLETE / VERIFIED** | WorldSync, SleepPresenter (M3.1.2 accepted) |
| save game | yes | sandbox only | - | - | host file only | host | - | - | - | - | client snapshot on join | - | - | - | **IMPLEMENTED / NEEDS MANUAL QA** | SaveGuard, CampaignBootstrap |
| associate companion | yes | own copy | no | no | AssociateUnlocked | host decision | NPC replication | - | - | - | - | - | - | NPC authority + design decision | **UNKNOWN** | - |
| pet cat | yes | yes (repeatable per join) | no | XP discarded | catty1 | decision | campaign txn | reliable | repeat reward | - | - | - | - | decision | **UNSAFE** | CAT_SYSTEM.md (DEFECTIVE outcome) |
