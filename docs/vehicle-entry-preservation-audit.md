# Native vehicle entry preservation audit

**Date:** 2026-10-10  
**Scope:** Local driver entry, multiplayer seat authorization, passenger-only presentation, and simulation authority. No game instance was launched for this audit.

## Vanilla entry contract

The original `PlayerScript.Update` reaches vehicle entry through its camera ray (2.25 m), native eligibility gates, the `SitCar` tag, the `interact` button, and an `EnterCar` component. `EnterCar.EnterVehicle` calls `CarController.EnterVehicle`. The native method owns the driver state changes, controls, weapon hiding, door close sequence, and collider/controller changes. `CarController.Update` moves the player toward the authored `sit_transform`; the native `RunCar` sequence parents the player to that transform and applies the native camera-entry rotation when the engine starts. The inspected native entry path has no vehicle-speed gate.

With no connected cooperative session, the mod's entry prefix passes directly to the original method. In a cooperative session, the prefix reserves the **Driver** seat. A Host's local reservation is synchronous, so the original method runs in the same input frame. A Client waits for Host authorization; after the reservation arrives, `Reconcile` invokes the same original `CarController.EnterVehicle` method with the multiplayer recursion guard enabled. The only timing difference is the required remote authorization delay.

## Audit findings and corrections

| Operation | Classification | Result |
| --- | --- | --- |
| Native `PlayerScript.Update` ray and input | Original behavior preserved | The driver path remains inside the native ray, `SitCar`, and `interact` gates. |
| Cooperative seat request and Host validation | Necessary multiplayer authorization | Requests remain authenticated and position-checked. The validation is now seat-aware so it does not add a speed restriction to native driver entry. Passenger boarding retains its multiplayer safety speed limit. |
| Local driver transform and attachment | Unnecessary deviation corrected | Removed the `TS2MP_DriverReference` follow/parent operation. `LocalDriverState` now captures recovery state without changing the driver's transform, parent, camera, animation, controller, or input during normal entry. |
| Nearby seat prompt | Unnecessary driver bypass corrected | The proximity prompt can request only the Passenger role. It yields while the native `CarEnterInfo` prompt is visible or during a native entry frame. |
| Passenger position/camera handling | Passenger-only adaptation | `LocalPassengerState` runs only for `SeatRole.Passenger`; it anchors the existing actor and camera at the rear-center seat while suspending passenger movement/collision presentation. Its captured actor, camera, component, and player-state values are restored on exit or teardown. It does not run for a Driver. |
| Vehicle simulation authority | Necessary synchronization | Once occupancy is confirmed, `VehicleNetworkAdapter` enables local simulation only for the confirmed local driver. The other peer remains a kinematic replica. With no driver, only the Host retains the existing world simulation. |

The original front passenger lootbox and vehicle physics architecture were not changed. Emergency `LocalDriverState.Restore` remains limited to failed entry/exit, actor loss, or session/scene teardown; ordinary entry and successful native exit keep the game's own state transitions.

## Verification and limits

The focused vehicle policy tests cover offline native pass-through, authorization pass-through for Host and Client, driver-role selection, passenger-only prompt/presentation, driver versus passenger speed gates, and single-peer simulation ownership. The existing multiplayer harness checks seat reservation, confirmation, input authority, and driver-owned motion.

These are source and harness results. They do not confirm in-game animation, camera timing, or visual movement. A controlled Host-driver and Client-driver comparison in the game is still needed before claiming runtime equivalence.

The Host also applies a coarse authenticated actor-to-car-root distance check of less than 4 m. It cannot reconstruct the Client's camera ray from the replicated actor pose, so an in-game check at the edge of the native ray remains necessary to confirm this guard never rejects a legitimate `SitCar` hit.

At the time of this 0.6.11 entry audit, the installed plugin/Core hashes were the ones recorded above. The later 0.6.12 vehicle-door and rear-entry changes are reviewed separately in [vehicle door sync and passenger-entry preservation](vehicle-door-sync-preservation.md); no runtime equivalence claim is added here.
