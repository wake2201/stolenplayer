# Vanilla-first vehicle storage — 0.6.9 / protocol 11

The [permanent root rule](../AGENTS.md) governs every future feature and task:
understand vanilla, preserve vanilla, enable Player 2, synchronize legitimate outcomes.
This is a correction to existing multiplayer storage/seating, not a new milestone.

## Verified original implementation

The installed original assembly was freshly decompiled for CarBox, CarController,
PlayerScript, Inventory, ItemID, SaveGame and VehiclesManager. All seven outputs
matched the previously inspected source. EnterCar and native motor/look dependencies
were also inspected. Proprietary source, mesh exports and runtime logs stay ignored.

| Question | Native finding and source |
| --- | --- |
| Driver seated access? | Yes in the first-person, engine-off state when the normal interaction gates permit it. CarController.Update tags the box when `!engine_running` and either `is_sitting_inside` or the right door is open. EnterVehicle disables CharacterController/capsule and movement, but does not disable PMB. ActivateControls switches control-prompt objects. The normal player interaction ray has no `!is_in_vehicle` condition for CarBox. Native menus/initialization may independently disable PMB/PCB; those restrictions remain. |
| Original passenger context? | EnterCar delegates to the single CarController/PlayerScript driver path. CarBox.Start binds the tagged original player and its Inventory. The inspected path provides no second local playable passenger. `associateSitPos` is an authored reference, not another player controller. Cooperative rear seating therefore needs a minimal interaction integration; it inherits the same box restrictions. |
| Must exit first? | No blanket exit requirement. A seated player must still physically target the box with the original camera ray. If the rear position cannot reach it or geometry blocks it, exit and use the native outside approach. |
| Outside approach? | The right-hand car door must be open, the engine off, and the camera must hit the actual box collider/tag. No invented interaction point or alternate container. |
| Stationary? | The inspected box path gates on the engine, not a speed threshold. Engine-running access is denied even at zero speed. No additional speed restriction is invented. The native UI pauses its local game; cooperative transfers recheck eligibility if another actor starts the engine. |
| Distance/position? | PlayerScript.Update uses the first Physics.Raycast hit from `cam.position` along `cam.forward`, length **2.25 m**, with the original `mask`. This is camera-to-hit distance, not a radius from the player's feet or box transform. |
| Other interaction gates? | PMB/PCB enabled; not running, radial menu, attacking, carrying, close-up, hiding hands, incoming call, active pickpocketing, slow opening, pickup or hiding; `canInteract`; positive time scale. The CarBox branch rejects an active shop; PlayerScript.OpenStorage also requires PauseObj inactive. |
| UI? | Original PlayerScript.OpenStorage → Inventory.OpenStorage, the existing two-list storage/backpack UI, cursor/pause/audio/camera handling, native selection, one-unit MoveItemToStorage and original close path. |
| Items? | Native Load_Item exposes non-heavy personal ItemID quantities (cars also require the appropriate native shop context). MoveItemToStorage refuses story items, checks weight, and applies `max_amount` on withdrawal. It does not impose a blanket tools/keys/loot-only rule. Heavy vehicle cargo is a separate mechanic. Multiplayer continues its existing supported unit-count subset; unsupported definitions remain native and are not silently granted network support. |
| Capacity? | Item weight, not item slots. CheckBackpack sets car storage to **20 + 10 × carStorageUpgrade**; the constructor default 15 is not the initialized gameplay limit. The current native value seeds authority. Withdrawal uses native backpack capacity +5 and the item's positive max_amount. Unsupported stored weight is deducted from cooperative capacity without changing native capacity. |
| Exit and scene changes? | Box quantities belong to PlayerScript's ItemID definitions, not a CarBox list or vehicle lifetime. The inspected exit routines do not clear them. Native travel calls SaveGameNewSystem; ItemID initialization reloads the same storage keys in the next scene. |
| Saving/restart? | SaveGameNewSystem calls ItemID.Save_Item_Amount, writing `Storage_Item_<id>` and separate `Locker_Item_<id>` keys. ItemID initialization reloads them. Transfers themselves do not invent an immediate save: a restart retains the quantities from the original successful save. ItemID.Remove_Item_Blackbay consumes from personal quantities first, then locker, then car storage when that location contains the required amount; it does not pool locations for a sale. |
| Main car versus van? | Both use the same CarController box eligibility code. The native binding read confirms **both authored boxes have storage_type=0**, the common item_amount_storage pool. VehiclesManager has no separate per-vehicle ordinary-box save or capacity. The second manager slot named `van` is actually instantiated as **MUSTANG / Car05** in this installed build; geometry support is specific to that observed model, not a claim about every van-shaped vehicle. Heavy cargo/trunk behavior is separate and is not generalized by this correction. |

## Multiplayer corrections and exceptions

| Class | Affected native behavior / reason | Minimal scope and preservation |
| --- | --- | --- |
| A | PlayerScript.OpenStorage opening context must identify the legitimate box for authoritative transfers | Cooperative prefix verifies the original camera ray and gates, then the original method opens its own UI. Postfix remembers that successful opening. Offline and Hosting alone pass through. Closing, disconnect and scene invalidation retire owned context through the original close method. |
| A | Suspended passenger PlayerScript.Update cannot process the original storage branch | Only the captured PMB-enabled, PCB-enabled and canInteract gates suspended by the seat adaptation are used for that attached passenger. All other original gates, camera ray, collider, door/engine conditions and original OpenStorage remain required. Storage-target interact opens the box; otherwise interact exits as before. The native TryOpenInventory method handles backpack/pause restrictions and storage closing. No reach/geometry bypass. |
| A | Native is_sitting_inside refers to one local driver; Client CarController.Update is suppressed at the simulation boundary | Confirmed cooperative occupants extend only the original seated operand of the box-tag rule. Actual native door state and Host engine state retain the other operands. Only the box tag is mirrored; no collider, storage object, UI or capacity mutation. Teardown restores the tag from the live original engine/seated/door rule. |
| A | Engine status must reach the replica independently of throttle permission | VehicleState uses an existing flag bit for actual engine state. DriverInput reports the real engine; paused/blocked controls send neutral input and handbrake through original Host EVP. A deadman neutralizes input while retaining the last verified engine flag until engine-off or driver removal, so packet loss does not unlock storage. Local Host driving remains native. |
| C → restored | Previous four-metre box-transform proximity allowed broader access | Storage requests now carry the verified opening camera origin/direction. Host checks a normalized ray, the authenticated actor's camera-origin bound and the actual first native-mask hit within 2.25 m. Both directions require the original box type/tag and current eligibility. No generic nearby-box permission remains. |
| A | Engine/door changes can invalidate a delayed cooperative transaction | Existing item-instance reservation/completion infrastructure remains. Local execution and Host completion recheck storage eligibility; lost eligibility aborts and reconciliation restores authoritative ownership. Host saves defer while any authoritative transaction is pending. No second inventory, extra item or new persistence key is created. |
| A | Native Host Blackbay consumption must not be undone by the next storage snapshot | A session-scoped prefix/postfix captures only decreases caused by the original ItemID.Remove_Item_Blackbay on supported Host definitions. Authority retires that many existing instances from the affected original pool and publishes the result. A reserved withdrawal of a consumed instance aborts. The hook never blocks the native call, changes its location selection, grants rewards, mints items or changes capacity/persistence. Offline and Hosting alone use the original path; hook teardown follows the economy controller. Client Blackbay workflow is not newly synchronized here. |
| A | Required rear-center passenger presentation | Mesh intersections at centered hips verify Car04 cushion/roof Y=.15361/1.48279 and Car05 Y=-.06259/.65199. Native driving_front hips X offsets (-.0071/-.0524) calibrate actor references X=.0071/.0384, centering the body. Preserve front-to-rear cushion clearance and prior head-to-eye clearance. Eye Y=1.04761/.59941, behind the driver. Existing original controller/camera capture and restoration remain; front lootbox and all native vehicle physics are untouched. Unknown mesh contracts fail closed. |

Protocol **11** rejects older peers: inventory requests are now 103 bytes with the
original camera direction appended; vehicle baseline stays 55 bytes with a new flag.
Both peers must use **0.6.9**. Item identities, personal ownership, locker/car-box
meaning, native Host save quantities and Client profile protection remain.

Native storage counts persist through the Host's original saves. Cooperative
instance IDs are reconstructed from those counts on a fresh Host session; separate
cooperative personal inventories still do not persist across Host process restarts.
Unsupported native item mechanisms are not claimed as synchronized by this audit.

## Focused verification

Verification uses native source/previous readable mesh exports, a single bounded
native binding read, focused existing economy checks and the existing suite once.
No new test framework, gameplay marathon, NPC/security work or original assembly
rewrite. Build/native binding details and final verification are recorded below.

One owned native session lasted **32.82 seconds**, including startup/Continue and
one read-only binding capture. Native fields showed initialized car capacity **20**,
used weight 0, authored locker capacity **999**, firstCar box type 0 (active PLAYER
CAR, engine running, tag Untagged), and van-slot box type 0 (inactive MUSTANG,
BoxCollider). Main box local position (.5412,.2218,.5375); second box position
(-.0045,-.1298,-.3825). The inactive second box's authored tag is not evidence of
live eligibility; its active native Update rule is established by source inspection.
The capture occurred during native menu/initialization with PMB/PCB disabled; it
does not constitute a successful storage interaction or driving observation.
No item transaction or gameplay fixture ran. The installed 0.6.9 Harmony startup
completed, all original binary fingerprints were unchanged, and this run's save
file set and PlayerPrefs were restored and verified. Configurations/loader settings
were restored to their fresh task copies. No unrelated process was terminated.

All four Release projects build with zero errors. The existing NuGet vulnerability
feed warning (`NU1900`, unreachable feed) remains; it is not a code-build failure.
Repository validation passes, including script parsing, output allowlist, tracking,
license/provenance, whitespace and all **183** original binary fingerprints.
The existing full suite passed **760 checks** after updating its explicit protocol
assertion from 10 to 11. After the final native-consumption correction, the focused
economy run passed **16 checks**, including consumption versus a reserved withdrawal.
The full suite was not repeated for that small correction. The earlier sandboxed
vehicle transport run failed to connect; the subsequent full suite's transport checks
ran successfully with loopback permission. Failed attempts remain in ignored logs.
No native transaction fixture or second native session was run for the final hook.
The final 0.6.9 build is deployed only while the game is closed; its installed DLL
hashes match the built outputs. Both peers require the matching protocol-11 build.
After deployment, a read-only comparison again confirmed the save file set/bytes,
PlayerPrefs names/types/data and all four configuration/loader copies match this
task's fresh pre-run baselines.

Current native code establishes the engine-off seated branch; it does not prove
every human camera pose can hit the box. New rear-center calibration is based on
mesh/body measurements, not completed two-human view/animation acceptance. Real
two-machine car-box transfers, independent profiles, supported vehicle/scene/save
cycles and human clipping/comfort remain acceptance gates. Limited checks do not
establish full vanilla equivalence.
