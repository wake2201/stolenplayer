# Playability Stabilization Gate S1

Historical S1 slice. The mandatory passenger extension supersedes its next-step
status; see [current incomplete scene/travel acceptance](scene-continuity-acceptance.md).
No manual user testing is requested during this development phase. Milestone F
remains unstarted.

Implementation status: **READY FOR MANUAL ACCEPTANCE**. **S1 is not ACCEPTED. Milestone F is blocked and has not started.** Baseline commit is `58df20e6eba190f0fad8c792abe046333ae668c1` (E, 0.6.0/protocol 5). Stabilization uses mod **0.6.1 / protocol 6** because the semantic player packet changes. Both peers/harnesses must update.

## Scope and root causes

Scene continuity: Plugin's loaded/unloaded/active-scene callbacks explicitly called `Disconnect("Scene changed; reconnect after loading.")`; World.InvalidateScene also cancelled its stream/session. Vanilla GameManager is recreated with MainScene Single, then Start selects one additive map using CurrentLocation. That Start branch also selects player/car/heist spawn helpers and location/environment flags. Calling Unity's additive map load alone would omit these dependencies.

Scene descriptor carries a bounded allowlisted map identity with MainScene and the verified CurrentLocation mapping. The supported shape is exactly MainScene + one of PlayerHideout, PawnShop, Auto Shop, MadisonSt, Hillville, DrugFarm, Warehouse, BankScene, Forestside Resort, HauntedMansion, Museum, SwatRange, Jail or IntroHouse. These are statically verified mappings, not a claim that all areas have passed live acceptance. Blank CurrentLocation=1, menus as authoritative worlds, multiple maps and HackingPuzzle are outside S1's descriptor. Failures explicitly block gameplay readiness while preserving transport.

Host LoadWait/LoadMain request the transition before loading. Client travel selection is blocked before its preference/save writes. SceneTransitionId and host SceneEpoch establish quiescence; new interactions/loot are blocked, pending reservations/native operations are cancelled/restored, presentation and Unity bindings are invalidated, and reliable transport continues. One epoch is allocated per announced reload rather than per Unity callback. Boundary callbacks only release presentation and scene bindings. Unexpected host boundaries can recover by announcing a verified stable loaded scene; unsupported sets fail visibly.

The client learns the descriptor immediately after handshake, including when starting in a different initialized safe scene. It automatically loads MainScene Single. A narrowly scoped transpiler replaces SPrefs.GetInt calls **only in GameManager.Start**: CurrentLocation reads the host location; Saved_Pos reads zero so that the map branch chooses its original spawn. It does not write either preference. Other game reads and mission objectives/rewards/story are unchanged. An initialized MainMenuNew with enabled ContinueButton, or existing gameplay, is required; absence of a playable local profile is an explicit native dependency. No automatic New Game or Continue/save method is invoked. This bridge still requires native manual validation, especially differing profiles/story and map initialization; structural incompatibility must fail through the existing manifest checks.

After loadedMap, a genuine actor and a settled complete scene set exist, the local peer signals SceneLoaded. The host waits for the matching transition/epoch/descriptor acknowledgement. The barrier then allows Milestone C's normal registry, chunked snapshot/hash validation, application, ack and catch-up stream. Unchanged inventory revisions are resent/reapplied to recreated native adapters in the new epoch. Transport/session ID remains stable in real managed UDP tests. Timeout (120 seconds) and explicit failure retain the connection with readiness blocked; they are not a successful travel or an automatic mission fallback. Host can request a newer supported transition to recover. Remote roots are recreated cleanly after compatibility/pose readiness; no replicated root is parented to scene-owned objects.

F8: previous RefreshTelemetry ran from OnGUI at a 0.25-second cadence, not every frame. It sorted/captured the entire registry twice (loot enumeration + ownership audit), decoded every pickup for ownership checks, cloned inventories, recalculated the canonical inventory SHA and rendered 25 GUILayout labels. Ownership overlap uses world-item × owned-item membership checks. **No continuously computed full world hash was found in the production frame path.** Full world summary was already an explicit audit. The 89-FPS claim is user-observed; its dominant runtime contributor must be determined by the new measurements, not assumed from source.

Current telemetry refresh runs in Update only when visible, four Hz, and renders one cached text block. It uses existing counts/revisions/item count and no capture/hash/ownership audit. F8 OFF does no display formatting. One-shot diagnostics retain heavy audits. The bounded profiler records DebugUI, RegistryTelemetry, WorldHash, InventoryHash, NetworkProcessing and AvatarPresentation plus separate GUI Layout/Repaint calls; ten-second windows report FPS and frame-time percentiles. Precise Mono allocations/frame is unavailable. Synthetic net9 timings are supporting CPU/allocation evidence, not game FPS.

Vehicle: vanilla EnterVehicle/IsStartingCar set is_in_vehicle/current_vehicle, disable CharacterController/capsule, reposition to sit_transform and later parent the player there in RunCar. Adapter availability and locator both previously required the CharacterController enabled, suppressing snapshots/readiness on entry. Static decompilation and prior R7 seated-source captures support that path; actual entry/exit state measurements remain MANUAL_REQUIRED.

Availability now accepts the verified vehicle flag even with disabled controller and samples the actual native world root. The 65-byte semantic state adds Driver occupancy, optional scene-local authored CarController presentation key and measured relative feet height. Passenger semantics are intentionally rejected because support is unverified. Remote presentation resolves a cached local vehicle/seat source, constructs only fresh transforms/skins/legacy Animation using shared game-owned driving_front assets, strips events and gameplay components, and disables normal civilian locomotion while seated. The sanitized driver stays beneath VisualRoot at the replicated actor pose with the source's seat-relative visual offset. This avoids attaching NetworkRoot to an independently moving unsynchronized vehicle. It neither moves nor simulates the local vehicle. Unresolved/incompatible seats keep a visible civilian fallback and report LIMITED seating; this is not sufficient to accept an appropriate seated presentation without human evidence. Exit disposes the driver and resumes the existing civilian root. Prior R7 proves driving_front source availability, not S1 runtime visibility.

Crouch: the motor changes controller height from the observed standing 1.6 m toward ~0.6 m and affects the root through controller movement/stand-up correction. The remote factory baked a single standing sole offset and reused it during crouch. Now the sender transmits controller.center.y - controller.height/2 as GroundFeet; VisualRoot adds the difference from its standing feet calibration. The authoritative packet position and NetworkRoot remain unchanged. This continuous geometry correction also follows crouch transitions instead of stepping a guessed constant. Runtime standing/crouching root, controller and foot measurements are captured by markers; sole grounding still requires visual acceptance. Full-body crouch animation remains **LIMITED**: no newly verified compatible game-owned clip; hands/camera crouch clips are not applied to the civilian skeleton. Idle/Walk remains the safe pose fallback with corrected grounding.

## Acceptance and evidence

| Gate | Classification | Status / required evidence |
| --- | --- | --- |
| A scene continuity + join | AUTOMATED + HARNESS + MANUAL | Managed real UDP transition/join, barrier, cancellation, epoch rejection and matching post-load hashes covered. Native client loading, actual host travel, inventory coherence and recreated avatar: MANUAL_REQUIRED |
| B F8 performance | AUTOMATED + MANUAL | Cadence/profiler bounds and synthetic legacy-operation profiling covered. Equivalent native before/after OFF/ON average FPS, median/p95 frame times: MANUAL_REQUIRED. Remaining overhead unknown |
| C driver entry/exit | AUTOMATED + MANUAL | Codec validation and pose/presentation separation covered. Native entry/exit and observer visibility/uniqueness: MANUAL_REQUIRED. Passenger unsupported |
| D crouch | AUTOMATED + MANUAL | Controller-feet correction, seated exclusion, restore and pure NetworkRoot invariance covered. Stand → CrouchIdle → CrouchMove → Stand/Walk visible grounding: MANUAL_REQUIRED |
| E workflow | AUTOMATED/script validation + MANUAL | Launcher, manifest, markers, snapshot, profiler, collector/restoration created. At least one human action/capture/collection cycle: MANUAL_REQUIRED |

Original 476 checks are retained, with the version-specific protocol assertion updated for v6; no coverage removed. S1 adds pure/state and actual localhost UDP checks. Short separate-process normal and impaired world harnesses remain technical evidence. Game startup smoke is recorded separately below. No autonomous map navigation, mission arrival or driving acceptance occurs.

### Validation record — 8 October 2026 (Europe/Berlin)

- AUTOMATED: **526 checks passed** (original 476 retained, 50 added). Source: ignored `.local/s1-tests.log`. Includes real localhost UDP join/scene transitions, cancellation, old epoch rejection, new baseline/hash equality, inventory reservation cancellation and unchanged revision rebinding; codec bounds, cache cadence, marker sanitization, bounded profiler, smooth feet interpolation and clean vehicle enter/exit timelines.
- HARNESS: two separate processes exchanged 113 sent / 112 accepted client poses on the normal run. Impaired world run (60 ms latency, 180 ms jitter span, 10% configured loss) completed revision 38, six chunks, one tombstone; client accepted 80 poses. These are synthetic peers, not native multiplayer acceptance. Abrupt host and abrupt client process-loss checks also pass; host-loss client reached Offline with Peer timed out. Sources: `.local/s1-harness-normal.log`, `.local/s1-harness-world.log`, `.local/s1-harness-abrupt-host.log`, `.local/s1-harness-abrupt-client.log` and existing ignored transport evidence.
- HARNESS/startup: **one game launch**, using the new manual launcher. BepInEx loaded 0.6.1/protocol 6 on main thread 1 and completed chainloader startup; scene callbacks ran without integration-stop errors. No navigation/gameplay performed. Session `s1-20261007-231634-2fe228ca` was collected and stopped. This startup proves hook installation, not GameManager.Start native travel or visual gates. The final locator cache/audit additions were built afterward; no second smoke is claimed for them.
- Restoration: backed-up save file set restored byte-for-byte (zero extra test-created save files); loader config restored; multiplayer config restored with hash comparison. Game PlayerPrefs restored and all value names/types/data verified. Unity's eight-byte values tagged REG_DWORD require preserving raw width/type rather than a normal Int32 SetValue; tooling handles this observed case.
- Build: all four projects succeed with zero errors. Cached NuGet vulnerability-feed warning NU1900 remains because the feed is inaccessible; no new package dependency or restore required. Validator, distribution allowlist, tracking/path check, script parser, provenance and `git diff --check` pass. All **183 original DLL/EXE fingerprints unchanged**. No proprietary assets, local paths, saves or raw logs tracked.
- Final implementation commit uses `fix: stabilize multiplayer playability`, body **S1 READY FOR MANUAL ACCEPTANCE**. No previous milestone amended and no ACCEPTED commit created.

Synthetic managed profiling (net9, 1,500 pickup records / 128 owned items, 50 warmed samples) measured:

| Operation | Average ms / refresh | Managed bytes / refresh |
| --- | ---: | ---: |
| Legacy sorted registry capture | 0.2225 | 648,568 |
| Legacy ownership audit | 0.5673 | 1,270,560 |
| Legacy inventory hash | 0.0069 | 18,960 |
| Current fast counters | 0.0476 | 120 |

Ownership auditing dominates these measured synthetic operations. This does **not** identify the dominant Unity/OnGUI source of the observed 89-FPS loss. F8 OFF FPS/frame times: **MANUAL_REQUIRED**. F8 ON FPS/frame times: **MANUAL_REQUIRED**. Remaining measured native overhead and before-build comparison: **UNKNOWN / MANUAL_REQUIRED**. Heavy work is absent from live overlay refresh; no performance gate accepted by inference.

Manual cases: S1-SCENE-01, S1-JOIN-01, S1-PERF-01, S1-VEHICLE-01 and S1-CROUCH-01. All remain **MANUAL_REQUIRED**. No human action was performed in this turn, so workflow gate E remains open too. One new launcher session can batch performance/crouch/vehicle; use a second native-peer scene/join session only if needed. The actual game-launch count during this implementation is **one**. Old-build measurement needs a further separate launch. Raw machine-local logs stay under `.local`. No final ACCEPTED commit may exist until all required non-manual checks and manual gates pass.

## Remaining limits and freeze

### P0 vehicle lifecycle and frame stability, mod 0.6.5

The earlier S1 counts and driver-only limitations above are historical. Current automatic
scene following and genuine passenger control remain within the scene milestone; F stays
unstarted. Real user reports of exit disappearance and intermittent stalls triggered
targeted native tracing rather than synthetic acceptance.

Native driver ExitCar clears current_vehicle, then clears is_in_vehicle while keeping
CharacterController disabled for another part of the exit animation. The previous locator
and adapter marked that live original actor unavailable; SharedVehicleController failed
the scene and hid its remote visual. The old-build native reproduction recorded
SceneFailed with remoteVisible=false, while the native car stayed alive and its registry
had zero tombstones. Thus remote disappearance is reproduced; actual vehicle disappearance
is not attributed to that same cause without evidence. The fix retains structural actor
identity through exit, scopes exit availability and separates operational seat failure
from vehicle destruction. Verified manager-slot identity also survives root index changes.

Empty hideout loot manifests previously failed the initialized check and rescanned every
Update, LateUpdate and world-preparation call. Before: RegistryRebuild average 10.2864 ms,
maximum 15.0610 ms across 98 samples; about 303 manifest trace records. Caching even an empty
completed manifest produced average .0081 ms, maximum .2788 ms across 817 samples in the
first post-fix native run. This isolates repeated mod scanning, not total game FPS.

The profiler retains the latest 8192 frame/phase samples, 128 phase events and 16 spike
contexts with their last 16 events. Export alone sorts/formats. Normal collection does
not allocate; explicit tests verify this on net9. GC generation counts and a spike-time
live-heap estimate are available; precise Mono allocation bytes/frame are unavailable.
Spikes retain session state, epoch, baseline stage, processed packet count and event queue.
Threshold defaults to 100 ms; diagnostic export is development-only. All thirteen requested
phases are instrumented. NativeSceneInitialization measures the mod's asynchronous load
initiation, not the full native loading CPU cost. It cannot prove all native loading stalls
avoidable. Frame/phase percentiles describe a rolling window; spike/GC counts are since reset.

Transport PollEvents now dequeues complete callbacks under a short queue lock, then invokes
them outside that lock. The upstream bounded poll detached the entire event chain and lost
the deferred tail at its count limit. A real UDP test queues 300 reliable ordered callbacks,
drains seven, yields after a slow atomic callback under a .1-ms budget, then delivers every
remaining callback once and in order. Production uses 128 callbacks / configurable 2 ms;
atomic transactions may exceed that budget. A backlog over 8192 causes explicit session
failure. Baselines transfer at most four chunks per poll; committed immutable capture and
baseline application remain atomic. No authoritative transaction is silently dropped.

After the rear-seat lifecycle run, a recent 8192-frame export measured average 2.1198 ms,
median 1.8281, p95 2.6282, p99 12.8823 and maximum 19.7242. RegistryRebuild maximum .0814 ms;
capture 2.9075 ms, serialization 1.5217 ms, JoinHandshake maximum 2.4288 ms. Two spikes
since reset remained: 367.854 ms at cold fixture startup and 423.553 ms at fixture cleanup
with one collection per GC generation. Setup constructs a second managed peer and cold
fixture code; its uninstrumented/JIT/native cost is not isolated by the short mod phase
durations. This is not a human multiplayer join benchmark. No matched vanilla/production
comparison establishes that all >100-ms stalls are eliminated. Performance acceptance
remains INCOMPLETE; do not substitute average FPS or the rolling max for the retained spikes.

The consolidated native mission sequence subsequently measured an 8,040.7001-ms
BaselineApply frame, repeated at 7,520.399 ms on another native Client mission join.
The concrete mod cause was a whole-scene Lockpick search for each door. An adapter-owned
index of authored unlockDoor links removed those repeated searches; native mission
baseline apply then measured 131.0122 ms, with hideout under 1 ms. Binding logs now cap
world/loot examples at sixteen each without limiting registry entries or canonical packets.
Bounded optional per-type baseline probes separated validation (2.6565 ms), 950 doors
(7.0689 ms), 184 windows (1.8005 ms), one vehicle (.6267 ms), and 592 pickups (140.2997 ms)
in a later 153.1320-ms mission apply. The managed source had copied default availability
from native Client pickup bindings, artificially activating inactive objects. Initial
bindings now capture actual native availability. Equal transform/activation/physics flags
avoid redundant native writes. Real differing native profiles can still require those
authoritative operations; corrected fixture timing does not waive that remaining gate.
Native scene-loading and cleanup spikes above 100 ms remain visible in retained contexts.
Current 712 automated checks cover opt-in baseline probes preserving the canonical hash.

The corrected native mission baseline was 13.0563 ms, then 13.3261 ms on the later mission
join (four mission/hideout applies averaged 6.8115 ms, median .4531 ms). Validation was
2.3915 ms, door application 5.7760 ms, windows 1.4398 ms, pickups 2.4703 ms and vehicle .4836 ms.
The latest 8192-frame continuity export still measured average 6.9603 ms, median 1.9149,
p95 19.0032, p99 32.5599 and maximum 1809.5632 ms, with 32 retained/since-reset spikes and
44 collections per GC generation. Spikes occur across native scene initialization and
fixture cleanup; a matched vanilla/production-peer run has not isolated all causes.
Thus the baseline search stall is resolved in the measured boundary; global frame-time
acceptance remains incomplete. Good averages and a fast baseline cannot erase those spikes.

Remote lockpick/forced-tool completion, head look and full crouch animation remain LIMITED.
The 0.6.3/protocol 8 extension implements real scoped passenger control, sequenced Host
EVP motion, canonical sleep/time and native scene following. Controlled native evidence
and unresolved gates are in [scene acceptance](scene-continuity-acceptance.md); native
two-game acceptance remains DEFERRED. Persistent cooperative save is NOT IMPLEMENTED.
Mission-specific profile/story differences are not synchronized by a descriptor.
Failed native initialization remains a blocker, never permission to require manual
matching scenes/rejoining as accepted UX.

S1 must pass A–E before F can begin. Shared wallet, buying, selling, storage, NPC/police authority, full vehicle physics, missions/progression, cooperative persistence and Steam transport remain frozen. [Manual workflow](manual-runtime-testing.md) is mandatory for future human gameplay gates.
