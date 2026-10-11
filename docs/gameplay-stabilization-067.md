# Gameplay stabilization 0.6.7 / protocol 10

Subsequent clarification: the user withdrew the menu black-screen report as
unrelated to the mod. Its historical observations below required no camera fix.
Current health work is recorded in [0.6.8](multiplayer-health-068.md).

This update implemented eight reported interaction/continuity changes. At release,
the bounded menu observation had not established a rendering root cause; the user
later withdrew that report as unrelated to the mod. No camera fix is pending for
it. Real two-game acceptance remains limited. No milestone G or cooperative persistence starts here.

## Changes and native boundaries

| Area | Implementation and preservation classification |
| --- | --- |
| Travel occupants | **A:** capture only confirmed driver/rear occupants before quiescing; announce actor IDs against the original VehiclesManager first-car/van slots. Restore new-epoch native driver entry or scoped rear seating before LocalSceneLoaded/WorldReady. No old Unity references or cloned cars cross the boundary. Exit-pending occupants are excluded; unseated actors are not assigned a role. Completed travel metadata is cleared for late join. Both-confirmed travel rules remain. |
| Performance | **A:** remote interpolation uses sender tick spacing when packets arrive together, with monotonic presentation and existing stale/snap protection. Vehicle motion retains its 20-Hz schedule, suppresses byte-equivalent motion fields, and sends a one-second idle keepalive. Loot adapters reuse encoded unchanged state. Door work drains at most 32 changed/active bindings per 100 ms through a deduplicated FIFO, rather than recapturing the whole manifest. No native graphics, physics, movement or simulation parameters are optimized. |
| Lean | **A:** read the original HeadTilt resulting head offset/roll, excluding damage poses and seated actors. Replicate/interpolate these values into the remote visual root only; release native observation references on scene invalidation. Original input, clearance tests, camera and lean speed remain native. |
| Slow doors, both directions | **A/B:** Client sends reliable slow begin/release intents; Host uses the original load_slow timer and SlowFailed behavior after validation. A scoped snapshot restores Host motor/look/UI writes caused by the remote gesture. Actual local hinge position/rotation, native progress, direction and capability flags replicate. Registered Client doors follow those samples and suppress their competing Door.Update simulation (**B**, necessary authority boundary; its timers/NPC lifecycle remain on Host). **C restored:** ordinary receiver motion no longer substitutes UseFully's faster opening speed. Hooks pass through offline and native Host door motion stays original. Cancellation releases the owned gesture. |
| Crowbar doors | **A/B:** Host validates native door restrictions, distance/line of sight, the actor's personal crowbar and equipped state; reserves the action. Acceptance invokes the owner's original coroutine, retaining cost, timing, hands, creaks, dust and control restoration. Its DoorUnlock completion is a reliable owner-only continuation with minimum native elapsed time; Host executes native lock/broken-hinge outcomes once. Duplicate IDs cannot repeat completion. Client's possession-effect branch is suppressed only for this owned coroutine. The Host synchronously borrows native possession context for original noise/security callbacks (**B**); three native sound position reads resolve the authenticated actor origin only inside that scope. The local actor/controller/camera transform never moves. Original possession/NPC guard flags restore in finally. Scene/disconnect cleanup stops only the tracked coroutine and restores its captured controls; native tool cost is retained. Other lockpicking/window/padlock functionality remains limited by the existing authority layer. |
| Cash and keys | **A:** remove blanket cash/key exclusion. Cash metadata carries native add_money; a reservation prevents overlapping wallet mutations, original pickup runs, observed completion tombstones the pickup and credits the shared wallet once without granting a physical inventory item. Abort restores canonical cash and pickup presentation. Key ItemIDs retain personal ownership, native weight/quantity rules and canonical pickup removal. Remote key access verifies the key in that actor's personal inventory and invokes native DoorUnlockKey; delayed native tag changes replicate. Same-object Door/Lockpick tags preserve the authoritative door tag. Client ItemDay save writes are scoped with the existing profile-protection guard. Story/heavy/mission pickup exclusions remain. |
| Menu black flash | **D → withdrawn:** no speculative camera/render fix. Native EnableInventory/DisableInventory and inventory/game tabs were observed with a connected managed peer. Camera identity, enabled/active state, clear flags, culling mask, near clip and target stayed unchanged. This does not test every rendered frame, computer/map UI or vehicle/travel combinations. The later user clarification closes this report as unrelated to the mod. |
| Doorbells | **A:** register authored bells in the existing registry and route Ring through Host interaction authority. Host calls original Ring, preserving native consequences; receivers play the existing local AudioSource clip for new reliable activations. Silent baselines never replay history, and duplicate requests/deltas do not retrigger sound. No audio assets or listener/mixer changes are introduced. The loaded hideout had no bell sources, so audible two-peer acceptance remains unproven. |

Protocol 10 is required by the new semantic state and scene occupancy wire layout.
Protocol 9 peers are explicitly rejected. Existing state identity, revisions,
reservations, LiteNetLib transport and world readiness remain the authority path.
Hooks are removed by their owning controller's Dispose; session-owned gestures,
native alarm suppression and replica physics retain their existing restoration.
The original front passenger lootbox is untouched.

## Focused evidence and limits

- Release build: all four existing build targets passed, zero errors. NuGet audit
  feed warning NU1900 remained because the package feed was unavailable.
- Existing complete suite: **748 checks passed**, including **12 new checks**.
  The first sandboxed run stopped at blocked localhost UDP; the completed run used
  localhost access. Final scoped cleanup/tag changes were rebuilt and the 12 focused
  checks rerun; the full suite was not repeatedly executed.
- One native gameplay session observed production Host readiness with a managed
  peer, inventory/pause camera lifecycle, 22 supported native key definitions and
  clean integration startup. A brief second startup verified the final hook set.
  These are controlled observations, not two-human interaction acceptance.
  The start-to-restoration records bound total native runtime below 5 minutes
  13 seconds across two sessions (each below five minutes); actual process runtime
  is slightly shorter because those records include collection/restoration.
- The short menu profile retained 340 frames: p99 9.7160 ms, maximum 39.6849 ms;
  RuntimeNetworkProcessing average 0.0149 ms, maximum 0.0356 ms. There is no matched
  pre-update sample. These numbers cannot establish a global FPS improvement or
  elimination of freezes; scene loading was outside that reset window.
- Save files and PlayerPrefs were freshly backed up for each owned session and
  restored with post-test change guards. Configurations and loader settings were
  restored byte-for-byte; diagnostics and collected evidence were preserved.
  Original 183 binary fingerprints were verified before/after deployment/testing.

Remaining real two-game acceptance: both occupant role permutations through travel,
visible lean/gradual doors, crowbar timing and native sound-security effects, both
cash/key pickup paths, personal key access, live bell audibility and duplicate/late
join behavior. The withdrawn Host black flash adds no pending camera work.
