# Vanilla gameplay preservation audit — 0.6.13 / protocol 13 regression follow-up

The earlier inventory and closure review below are historical evidence through
0.6.10 / protocol 12. The 0.6.12 vehicle-door and rear-entry interventions are
classified in the [vehicle door preservation audit](vehicle-door-sync-preservation.md),
which now also records the 0.6.13 regression fixes and their verification limits. This
follow-up does not claim new broad vanilla-equivalence evidence.

The [0.6.9 vanilla-first vehicle storage audit](vanilla-first-vehicle-storage.md)
supersedes historical storage eligibility and rear-right seat assumptions. Its native
opening/ray context, engine replication and transaction revalidation are scoped to
cooperation; source-backed rear-center geometry remains presentation only.

The subsequent [0.6.7 stabilization audit](gameplay-stabilization-067.md) records
the narrowed Client crowbar boundary, slow-door motion, cash/key pickup, travel-seat
continuity and doorbell exceptions. The [0.6.8 health audit](multiplayer-health-068.md)
records subsequent restoration and loading safety. Historical limitations below
describe 0.6.6. The user withdrew the Host menu black-screen report as unrelated
to the mod; no camera/menu change is required.

The permanent rule is in [root AGENTS.md](../AGENTS.md). This audit reviews runtime
interventions, not just assembly fingerprints. The inventory below retains prior
evidence; the current review and closeout status are recorded after it. A =
multiplayer essential; B = essential but too invasive; C = unnecessary; D = effect
or safe replacement needs further investigation. No full vanilla equivalence is
claimed.

## Reviewed interventions and exceptions

| Class | Hook / affected vanilla behavior | Multiplayer reason and activation | Preservation / restoration |
| --- | --- | --- | --- |
| A | LocalPlayerAdapter / PlayerLocator read the original player, motor, controller, camera, equipment and vehicle fields | Replicate the real local actor; observation only | No replacement local player or movement/physics settings; original cameras, listeners, hands and tools stay functional |
| A | RemotePlayerProxy, avatar/animation/equipment factories create and sanitize presentation objects | Display the second actor during a session | Mutations apply to mod-owned clones and visuals; no functional NPC/player/tool clone; Dispose removes them |
| A | CarController.EnterVehicle/ExitVehicle prefixes; native driver reference and recovery | Authoritative seat reservation, surviving actor lifetime and asynchronous exit; connected cooperative cars only | Native driver entry/exit/camera routines remain; recovery snapshots restore surviving original actor after exceptional loss; offline prefixes pass through |
| C → restored | SharedVehicleController replaced EVP input for the Host and used a simplified forward/reverse calculation | Host replacement was unnecessary; remote input forwarding is necessary | Local Host retains native VehicleStandardInput.Update/FixedUpdate, including reset and authored input rules; remote input executes the actual native FixedUpdate method |
| A | NativeDrivingReads changes only four GetAxis reads and the single-player can_drive_car gate while injecting an authenticated driver's input | Host passenger is not the driver; remote axes must reach native EVP | Synchronous scope with finally cleanup, no permanent player flag writes; normal calls execute original IL; pending/exit/world barriers temporarily neutralize driving with handbrake |
| A | EVP.VehicleController.FixedUpdate suppression and Client motion follower | Prevent two conflicting vehicle simulations | Client only; Host EVP physics, parameters and collisions remain native; adapter Release restores the settings actually changed by replication |
| B → narrowed | VehicleNetworkAdapter.Restore previously wrote engine, braking and Rigidbody state for every captured Host car | Cleanup is necessary only for replica/remote-control mutations | Read-only Host bindings now leave native vehicle state alone; replica and remote-control flags scope restoration, including registry release |
| A | Remote engine flag and alarm child Rigidbody lifetime | Native RunCar would take over the Host passenger; remote motion needs the verified vehicle-side lifetime | Scoped to remote driving; native alarm body restored; no handling/acceleration/physics-parameter replacement |
| B → narrowed in 0.6.8 | CarController.Update suppressed for occupied remote-driven vehicles | Avoid single-player input/actor callbacks against the Host passenger | Host now executes original fuel/gauges/audio/repair/lootbox lifecycle; authenticated remote driving filters only local button reads, visual steering and Host passenger isDrifting writes. Client replica boundary remains; offline/local Host reads and writes pass through |
| A | LocalPassengerState / PlayerScript.Update suspension, colliders/renderers, motor/look, camera near clip and rear pose | Vanilla has no second seat; rear passenger must follow the shared car | Occupied passenger only; captured original fields, cameras, collisions, renderers and control states restore on exit/loss; original camera/listener is reused; front lootbox is untouched |
| A | RearPassengerSeat creates authored-mesh-verified seat/eye reference transforms | Add rear seating without using front storage | Mod-owned reference geometry only; no native collider, seat, storage or vehicle physics modification |
| A | GameManager.LoadWait/LoadMain, Select*; Start/LoadGame/InitiateMainCar preference reads; IsStartingCar | Automatic Host-directed scene continuity and shared-vehicle travel preconditions | Session only; Client scene selection/initial seating is suppressed, Host descriptors substitute read values without writing preferences; offline reads/loading remain original |
| A | PassTime.Begin/PassStart and Bed.Sleep/SleepB/LayDown; DayNightTime hourly transpiler; Enviro clock and temporary controls | One Host clock and coordinated sleep instead of duplicate Client hourly mutations | Session/Client-only clock suppression; native lighting retained; native Host sleep executes; disconnect now unfreezes controls as well as restoring clock progression |
| C → restored | Time teardown restored captured Host progression and could stop an unrelated later native sleep | Neither write is necessary when the mod did not own/suppress it | Clock restoration now requires actual Client suppression; native sleep cleanup requires a sleep started by the cooperative barrier |
| A | Door Use/UseFully/SlowFailed/TimeAdd/ResetLock/OpenDestroyed/NPCUse/OpenDoorNPC/CloseDoorNPC/Unlock_Key; PlayerScript.load_slow writes | Host door state and bounded Client intents; prevent duplicate state/noise callbacks | Offline calls/slow writes pass through; Host executes native interaction; receiver applies state/presentation without duplicating actor rewards/noise |
| B → narrowed | Receiver Door/Window adapters suppress Lockpick/LockedWindow.hasAlarm | Avoid duplicate Client alarms while applying authoritative state | Suppression now captures and restores the original alarm flags on adapter Release; accepted world state remains as designed |
| B | Lockpick.StartLockpicking/DoorUnlock/DoorUnlockKey/RelockDoor/BreakPadlock; LockedWindow.BeginCut/BrokeWindow/UnlockWindow/UnlockWindowNoAlarm; PlayerScript.BreakinCrowbar | Host authority is needed for native lock, alarm, mission/story and reward outcomes | Offline and Hosting-alone calls pass through. Client DoorUnlockKey and crowbar completion are supported through Host validation/native completion. Client lockpick minigame and ordinary completion, relocking, padlock breaking and locked-window cutting/completion remain unsupported. A safe transaction bridge for their native UI, restrictions, alarm/noise and progression outcomes remains a co-op blocker; removing guards would diverge state |
| A | Pickupable.PickUp and PickingItem.MoveNext destruction deferral; Inventory.DropItemNewSystem; PlayerScript.SelectCrowbar | Personal ownership, reservation, rollback and delayed native completion | Session only; native pickup/drop routines execute once; deferred destruction concerns only tracked transactions; offline destruction remains original |
| A | PlayerInventoryAdapter supported ItemID quantities, buttons, weight and flashlight/equipment reconciliation | Apply canonical personal inventory without replaying rewards/save callbacks | Original Inventory/tools remain; unsupported quantities retained; limits/weights read from native definitions; native equipment methods reused |
| B → narrowed | LootNetworkAdapter Client Rigidbody kinematic/gravity suppression | Host-authoritative loose loot physics | Original body flags now captured before suppression and restored on Release; only mod-owned dynamic Client replicas are destroyed |
| A | Shop_Manager.Buy_Item; Inventory.Sell_Item_NewSystem/MoveItemToStorage; sale valuation injection | Shared Host wallet with personal item delivery and storage reservations | Connected cooperation only; actual nearby website, native unlocks and Host prices/bonuses feed original execution; temporary purchase price restores in finally; original sale-chain flag retained; Offline/Hosting alone pass through |
| A | Cooperative locker snapshot refresh and delayed native selection validation | A counterpart can change storage while the original UI is open | Original ClearInventory retires obsolete buttons before OpenStorage and its native reload coroutine rebuild lists; delayed sale/transfer reservations require the same live selected button, direction and personal quantity; stale execution aborts without moving canonical ownership; connected cooperation only |
| A | New Client join invalidates the previous inventory/wallet view | Inventory revisions belong to the selected Host; a former Host revision must not reject the new baseline | Clears only replication views/assemblers when joining; retains previous local authority data and native offline profile; no native rules or wire changes |
| A | SaveGame.SaveGameNewSystem, PlayerScript.SaveMoneyAmount, ItemID.Save_Item_Amount, selected SPrefs.SetInt keys | Protect Client profile from Host wallet/inventory persistence; prevent saving half-completed transactions | Offline saves/preferences pass through; Host native fields/save retained; captured Client wallet/storage/inventory restored on disconnect |
| B → narrowed | GameManager.TryToMoveDroppedItemsToStorage and its original ItemID.MoveToStorageOnDrop call | Preserve native Host save vacuum while avoiding duplicate canonical ownership | Offline/Hosting-alone run the original vacuum; connected Host tracked supported drops invoke the original item method once and transfer the same instance into the native locker; Client duplicates are suppressed and reserved/bounded transfers defer. Native cargo acceptance remains an incomplete co-op path; the no-peer native path is source-verified |
| D → investigated; A read-only baseline observation | Hosting-alone inventory/economy reconciliation | Keep Host-alone play native and seed a later peer from observed supported inventory/economy state | No-peer native actions and rewards pass through; observer reads native wallet, supported quantities, capacities and equipment and updates only the managed baseline. It does not write native fields. Unsupported progression/reward state remains outside co-op synchronization; evidence and scope are reviewed below |
| A | LocalPlayerAdapter recognizes original UseComputer sit/stand transition | Native controller disable during stand-up must not trigger a cooperative world/time freeze | Availability observation only, tied to the original actor/computer; native controller, motor and animation execution unchanged; confirmed by native Host purchase followed by locker deposit |
| C → restored | Vehicle/global-action denial labels could remain after disconnect | No offline gameplay reason for stale notices | Draw is session-scoped; offline scene tick clears global notice |
| A | Explicit development fixtures, temporary Harmony/Rewired input patches, diagnostic captures | Bounded acceptance evidence only; configured opt-in and fixed commands | Not production controls; owned process/save backup required by tooling; temporary input hooks clean up; diagnostics/configs restored after testing |

No production NPC/police simulation hook, mission rewrite, movement-speed tuning,
graphics optimization or vanilla assembly rewrite was found in this scope. NPC
door calls share the current Client door-authority guard; this does not establish
full NPC/police synchronization or behavioral equivalence.

## Current review and closure status — 0.6.10 / protocol 12

The root [AGENTS.md](../AGENTS.md) is the policy authority. This document is the
authoritative intervention inventory (also identified as such by
[multiplayer architecture](multiplayer-architecture.md)). It previously had no
explicit closure checklist. The following criteria translate the existing policy;
they do not waive feature or preservation requirements.

### Hosting-alone `D` investigation

**Finding: investigated; no native gameplay regression identified in this path.**
The code installs controller hooks at startup, so the no-peer guards matter. In
`CooperativeEconomyController`, `Bypass` calls original buy/sell/transfer methods
when `!Cooperative`; `Save` allows the original no-peer/offline save path and
blocks connected Client saves;
`SaveItem` and `SavePref` likewise allow native Host/offline writes. `AutoStorage`
allows the original vacuum when not cooperative. The `NativeCargo` wrapper calls
the original `ItemID.MoveToStorageOnDrop` unless it is handling a bound connected
Host transaction. `LootInventoryController` lets original pickup, drop and
equipment actions run when not cooperative. These no-peer guards and wrappers are
in [CooperativeEconomyController.cs](../src/ThiefSim2Multiplayer/Game/CooperativeEconomyController.cs)
and [LootInventoryController.cs](../src/ThiefSim2Multiplayer/Game/LootInventoryController.cs).

The observer in [NativeHostEconomy.cs](../src/ThiefSim2Multiplayer/Game/NativeHostEconomy.cs)
reads the native wallet, supported inventory/storage quantities, capacity and
equipment. Its only write is `InventoryReplication.ObserveSoloHost`, a managed
baseline update; it does not set native `PlayerScript`, `ItemID`, preference or
reward fields. Solo drop observation retains the native drop and item ID. Save
continues through the original Host save path when there is no pending cooperative
transaction.

The outstanding vanilla reward paths were checked against the available
decompilation of the installed game source. `Pickupable.PickUp` routes cash through
native `moneyAm`, ordinary items through `ItemID.Item_Picked`, and retains native
drone, contract, parcel and story callbacks. `Item_Picked` owns discovery, quantity
and weight, item/parcel preferences, XP/skill points, special-item awards, stats
and achievements. `Shop_Manager.Buy_Item` retains native tutorial/achievement,
tool-repair, story, item-count/weight, sound and price effects. `Inventory.Sell_Item_NewSystem`
retains its chain-sale gate, story advancement, venue multiplier, hardcore and
negotiator/hot-item/day/upgrade modifiers, wallet and `MoneyMade` rewards, thief
score, item drop, achievement, UI and story callbacks. Their source is in the
ignored `.local/decompiled/Assembly-CSharp.dll` inspection outputs; those are source
inspection evidence, not a fresh runtime test in this review.

Original seller variants were traced from `PlayerScript.Update`: pawn uses
`Pawn_Shop_Activate()`/profit 1; junkyard uses `Junkyard_Shop_Activate()`/profit 1;
house buyer uses `Pawn_Shop_Activate(0.9f)`; the mechanic invokes the distinct
`Whiplist.InitiateMechanic()` service. The available decompilation includes the
mechanic entry point and upgrade/persistence field index, but not the method body;
its detailed native purchase effects were not independently verified here. No
production Harmony patch targets `Whiplist`, and the no-peer `SPrefs.SetInt` hook
passes original Host/offline writes. Cooperative commerce binds buys to the nearby
native computer website and supports pawn/junkyard/house sale valuation. It does
not establish multiplayer mechanic-service parity or every reward/progression
field; these are documented multiplayer limitations, not a claim that mechanic
behavior was tested. They do not alter the Host-alone native variants: the existing
Milestone F native integration record reports original Host/offline bypass, and
one native game with a managed counterpart verified the supported buy/sell/locker
paths; it does not claim every reward path or independent-profile two-player
acceptance.

**Evidence-based disposition:** the prior `D` was uncertainty about whether
Host-alone reconciliation changed original play. The guards, direct original
calls, read-only observer and inspected native reward/shop paths resolve that
uncertainty for the audited no-peer scope. The baseline observer is classified A
because it is needed to seed a later cooperative peer, while unshared reward and
service fields stay explicitly out of the feature claim. This is an investigation
result, not a waiver of missing multiplayer parity.

### Lockpicking, forced entry and cargo boundaries

For offline and Hosting-alone play, `NetworkManager.TransactionsAllowed` allows
the native path, `WorldInteractionController.LimitedAction` passes original Host
calls unless the remote actor owns the object, and the Client-specific route is
not used. Available vanilla source confirms `StartLockpicking` launches the
original difficulty-specific minigame; `DoorUnlock` applies the original
story/lock/tag/opened-lock effects; `BreakPadlock` restores physics/collider/tags
and noise; locked-window methods retain their alarm, mission, noise and entry
effects. The co-op unsupported list above is therefore a feature boundary, not a
change to the no-peer original path. `gameplay-stabilization-067.md` documents the
supported Client key and crowbar paths and why the remaining native completion
actions need a safe transaction bridge.

The installed source for `GameManager.TryToMoveDroppedItemsToStorage` iterates
tagged dropped, non-heavy pickups and calls `ItemID.MoveToStorageOnDrop`; that
native method increments locker quantity/capacity only when `max_amount <= 0`.
The no-peer wrapper reaches that method once. The multiplayer Host wrapper keeps
the same original acceptance predicate and call, then updates canonical ownership
for its tracked item. Automatic cargo has existing source/managed coverage, but
the previous native acceptance record still lists automatic cargo and broader
two-peer acceptance as incomplete. No changed native acceptance rule was found;
full co-op cargo behavior is not claimed.

### Vehicle runtime cases not verified with two native games

The real two-player cases listed by
[driver-owned-vehicle-physics.md](driver-owned-vehicle-physics.md) remain
unverified: (1) real two-player driving; (2) sustained driver handoffs and network
partitions; (3) collision repair, police and NPC consequences; (4) remote audio
and wheel presentation; (5) simultaneous vehicles and fuel; and (6) passenger
storage/comfort under latency. Existing evidence is source inspection, managed
network checks and native fixtures with a managed observer, not human or two-game
acceptance.

The preservation boundary is source-verifiable: no-coop vehicle hooks pass the
original methods through; the confirmed local driver runs the original EVP and
input method; replica physics/contact simulation is suppressed only for the
cooperative non-owner; the Host's remote-driver `CarController.Update` keeps its
native lifecycle while narrowly filtering local passenger input/actor writes and
replica fuel consumption. The Client driver runs its original collision callback;
Host replays the original consequence method for the cooperative world. The
documented unique-target/contact validation can reject independently moved or
spawned NPCs, which remains a multiplayer outcome gap.
Existing rear-passenger fixtures cover original camera/listener identity and
restoration, but do not prove comfort under real latency. None of these records
proves full two-player behavior or complete vanilla equivalence. No source path
was found that changes offline or Hosting-alone driving, native handling, fuel,
collision rules or storage eligibility; the unverified cases remain explicitly
cooperative acceptance gaps rather than evidence that those gaps are correct.

### Audit closeout checklist

| Existing policy obligation | Current evidence and status |
| --- | --- |
| Classify production native hooks/runtime mutations A/B/C/D | **Complete for the reviewed source scope.** `Plugin.Awake` creates `WorldInteractionController`, `LootInventoryController`, `CooperativeEconomyController`, `SceneContinuityController` (which owns `CooperativeTimeController`) and `SharedVehicleController`; their Harmony patch families map to the intervention rows above. Explicit development fixtures are separated in the final row. No NPC/police simulation hook or native assembly rewrite was found. |
| Investigate uncertain D findings before changing them | **Hosting-alone D investigated and evidence-dispositioned above.** The 0.6.7 menu report is recorded as withdrawn. |
| Minimize B and restore unnecessary C changes | **Documented in the inventory and linked follow-ups:** vehicle Host state restore narrowed; occupied-car Update filtering narrowed; original slow-door speed restored; time teardown narrowed; overbroad vehicle restoration and proximity rules restored/narrowed. The 0.6.13 driver-motion timeout removal is recorded as C → restored in the vehicle-door follow-up. |
| Document every exception/known limitation, evidence, scope, activation and restoration | **Documented for the intervention inventory.** The lockpick/cargo limits and the six unverified vehicle cases are stated above with their source/acceptance boundaries. This is scoped evidence, not full-equivalence proof. |
| No unresolved potential vanilla regression is silently accepted | **OPEN / BLOCKED.** The [0.6.8 incident follow-up](multiplayer-health-068.md#targeted-crash-source-diagnosis-2026-10-10) correlates one UnityPlayer access violation to the Client and the travel/rendering timeline. The Client's required rear-passenger adaptation moves the original camera before the native Impostors pre-cull callback; vanilla `EnviroSky` also allocates render targets. The two crash traces share one Unity log, no usable dump exists, and no matched native control was captured. Direct mod RenderTexture allocation and fallback retry in that session were not evidenced, but an indirect camera/resource contribution remains possible. The final diagnostic preflight did not launch games because it found severe unexplained GPU load (100%, 7,802/8,151 MiB, no process attribution), and the shared-install launcher/log paths cannot isolate concurrent processes; see the preflight evidence and staging requirements in the health record. The evidence remains insufficient to rule out a vanilla regression. |

**Audit status: not closed. Milestone G must not start.** The exact remaining
question is whether mod-owned Client scene/presentation state contributed to the
allocation pressure or UnityPlayer access violation, versus a native/system
rendering-memory failure. The minimum next diagnostic is the bounded matched
co-op/mod-disabled two-process Madison capture described in the incident
follow-up. Missing causal evidence is not proof that the mod preserved that
runtime behavior.

## Verification and limits

The deployed game implementation of VehicleStandardInput.FixedUpdate was inspected:
four Rewired GetAxis calls, one can_drive_car gate, its actual continuous-reverse
branch and 0.1 input dead zone. NativeDrivingReads fails closed if that contract
changes. It executes the original method rather than a reconstructed calculator.
Remote driving still uses Host-side actor fields (including drunk effects); reset
and modifier-key forwarding for Client drivers are not represented by protocol 9.
These are remaining input-fidelity gaps; original local Host behavior is preserved.

The existing suite passed **731 checks** with local UDP access. The initial sandboxed
attempt failed the first raw-handshake network check; it was rerun without that
restriction, with no test weakened. Four Release projects built with zero errors;
the existing NU1900 package-audit-feed warning remains.

One owned native game session lasted **56 seconds**, with native Continue followed
by **five focused checks**: read-only Host vehicle teardown and native axes/dead-zone
behavior at forward inputs +0.09, +0.11, -0.09 and -0.11. It performed no driving
sequence or multiplayer lifecycle marathon. Native 0.6.6/protocol 9 startup and
the original local controller/camera stack were observed. Alarm-release and time-ownership cleanup
was added after this run and receives build/source validation only. Existing
multiplayer protocol/transaction/vehicle coverage remains; no full two-machine
acceptance or complete vanilla equivalence is claimed.

No original-game performance work was changed. Existing callback budgets, bounded
snapshots/telemetry, cached multiplayer bindings and avatar work stay in the mod.
No new gameplay feature or milestone was added.

Final installation and safety evidence is recorded locally under the ignored
vanilla-audit protection/session artifacts: all three deployed DLLs match the
corrected 0.6.6 build, original diagnostics are restored, and **35 save files,
63 PlayerPrefs values and three configurations** match the fresh task baselines.
All **183 original DLL/EXE fingerprints** remain unchanged. Validators passed for
scripts, output allowlist, tracked material, machine paths, provenance and whitespace.
Historical saves are never used to overwrite newer user progress.

The subsequent Milestone F native integration follow-up is recorded in
[Milestone F acceptance](milestone-f-acceptance.md). It resolves Hosting-alone
interception and narrows save vacuuming as above. Client forced entry remains
blocked because its native reward/alarm/lock actions have no safe authority bridge.
At that follow-up, remote occupied-car Update still skipped gauges/fuel/repair
side effects; [0.6.8](multiplayer-health-068.md) now restores this Host lifecycle
with narrower input/actor filtering. Remote input still uses Host drunk effects
and lacks Client reset/modifier forwarding. The other Client interaction gaps
are described by the subsequent 0.6.7 boundaries rather than this historical slice.
