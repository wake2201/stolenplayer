# Scene continuity, shared vehicle and passenger acceptance

**INCOMPLETE. Mod 0.6.5 / protocol 8. Milestone F has not started.**

Functional passenger, native scene following and shared sleep are implemented, with
bounded native evidence. Earlier Client-driven native braking failed, a final mission rerun passed, and full
exception/variant acceptance remains open. Automated or managed-peer results do not
establish two native machines or human gameplay acceptance.

The historical tables and bounded rerun below record the 0.6.3 baseline. The separate
0.6.4 follow-up section records current source, additional tests and native attempts;
earlier results are not retroactively relabelled as current native acceptance.

The current 0.6.5 P0 report is appended below. Earlier unrelated-game and restoration
blockers describe their historical attempt; the new run uses a fresh pre-task backup,
owned-process checks and guarded changed-only restoration. Current frame-spike acceptance
and independent-profile/exceptional recovery verification remain open.

## Native findings and evidence boundaries

Targeted static/runtime reconnaissance found a single local driver path in
CarController, native EVP physics/input, and authored associateSitPos passenger anchors.
The anchor is not an existing functional local passenger controller. The extension
therefore suspends the original local motor/collisions, retains PlayerScript, inventory,
camera and listener, follows the authored seat and restores captured state on exit.
Remote occupants use sanitized game-owned seated resources under NetworkAvatar.

Evidence collected on 2026-10-08 uses one native process at a time with backed-up saves,
PlayerPrefs and deployed plugins/configurations. The other actor is a managed localhost
peer. It mirrors the native fixture manifest; this cannot prove compatibility between
independent native profiles. Raw diagnostics, original-camera preview, logs and
proprietary decompilations remain ignored locally.

The passenger fixture completed **20 assertions**, including original controller,
inventory/camera/listener identities, 60-frame moving attachment, near-clip restoration
and destroyed-seat recovery. Its controlled transform movement suspends EVP and is not
driving evidence. The separate EVP fixture uses actual wheel/body physics and validated
inputs through two transports. Host-driven acceleration/braking and Client-driven
acceleration with a real Host passenger passed. A Client-driver braking phase failed:
initial signed speed magnitude 3.435 m/s, minimum 3.435, final 5.344 after four seconds.
It logged 21 passing assertions before failing; these are partial results, not a
completed driving fixture. The final bounded diagnostic rerun is recorded below.

The native continuity fixture completed **29 assertions**: production Host passenger
entry, canonical native sleep, Host return to hideout, native Client automatic MadisonSt
join, native Client passenger entry/exit, Client driver entry and original Rewired input
delivery to managed Host authority, Client EVP suppression, automatic return, fresh
player/epoch and direct-hideout reconnect without stale occupancy. Zero Rewired axes in
that Client-driver segment prove delivery/authority plumbing, not human steering.

An earlier failed driver fixture timed out with the CharacterController still disabled.
Nested IEnumerator failures did not reliably unwind the outer Unity fixture scope.
GuardedRoutine now owns/disposes the iterator stack; automated tests verify nested
failure and cancellation cleanup. Native fallback paths still require explicit acceptance;
prior successful normal exits do not establish exceptional exit correctness.

## Requested 33-item report

| # | Item | Result and limits |
| --- | --- | --- |
| 1 | Final status | INCOMPLETE; F unstarted. |
| 2 | Versions | Historical baseline: mod 0.6.3 / protocol 8; incompatible protocol peers rejected. |
| 3 | Scene improvements | Native dependency readiness, Host vehicle variant/availability, outside-car Client spawn, phase deadlines, fresh time baseline, former-Host epoch cleanup. |
| 4 | Hideout join | Native automatic follow and reconnect passed with managed Host. Initialized playable local profile required. |
| 5 | Mission late join | Native Client loaded MadisonSt automatically, rebuilt original gameplay and reached GameplayReady. Different-profile parity unverified. |
| 6 | Mission follow | Authoritative descriptor invokes original MainScene/GameManager initialization; Client manual map selection unnecessary. Other maps remain native gates. |
| 7 | Return | Native Host and native Client paths separately passed mission-to-hideout loads with same transport/session and new baseline. |
| 8 | Host driver | Original native entry/engine, validated input, actual EVP acceleration and service stopping passed. Full native Client passenger pairing deferred. |
| 9 | Client driver | Managed Client input moves native Host EVP with real Host passenger. Native Client entry/Rewired input/kinematic suppression also passed. Stopping failed in an earlier fixture; the final MadisonSt fixture passed both roles. Repeatability remains an acceptance risk. |
| 10 | Vehicle authority | Host sole EVP physics; 20-Hz sequenced input and independent 86-byte motion packets. Client kinematic interpolation. |
| 11 | Passenger motion | Native Host passenger follows Client-input EVP movement; controlled frame fixture also passed. WAN/two-native comfort not accepted. |
| 12 | First-person camera | Same native camera/listener; bounded seat-relative look, temporary near clip and exact restoration. Rendered interior preview inspected. Other variant/angle combinations unverified. |
| 13 | Remote seats | Native sanitized driver and passenger rig construction passed; full outside/entry/seated/exit visual sequence on two machines deferred. |
| 14 | Entry/exit | Host reservations, local native attachment, confirm, exiting and native detach/ExitConfirm; real passenger native enter/exit and driver enter observed. Full input combinations pending. |
| 15 | Exceptional exits | Destroyed passenger seat recovery passed; fixture iterator failure guard added. All transport/lifetime/camera/native-exception combinations not yet accepted. |
| 16 | Variants | Active VehiclesManager.firstCar/van with verified authored contracts only. Primary car tested; second variant runtime behavior unverified. No stolen/unknown-car claim. |
| 17 | Host travel | Native selection guarded before save/load; Client global intents rejected. Solo/offline bypass preserved. |
| 18 | Two-seat rule | Same live vehicle, exactly confirmed actors 1/2 as Driver/Passenger. Outside, pending, exiting, separate cars and stale records denied before epoch/load. |
| 19 | Host sleep | Original Host PassTime runs after automatic Client ACK; mission sleep requires both seats. Hideout/solo do not invent a passenger requirement. |
| 20 | Shared time | Host Enviro/native counters replicated with begin/wake/ACK/resume; Client hourly mutation suppressed while lighting/UI remain native. NPC schedules/progression are outside this slice. |
| 21 | Epoch/baseline | Fresh registry/clock/player bindings after load; old seat/input/motion cursors invalidated. Former Host can join lower-epoch new Host. |
| 22 | Performance | Cached anchors, Rewired/EVP delegates and reflection metadata; bounded 20-Hz channels; motion avoids reliable world revisions/full-frame hashes. No new sustained benchmark acceptance. |
| 23 | Crash findings | Earlier WER native UnityPlayer failures and high system commit remain evidence; memory pressure is plausible, not proven causation. Real fixture integration exceptions were diagnosed separately. No system/pagefile changes. |
| 24 | Automated checks | 684 passed, retaining historical coverage; assertion count, not independent gameplay scenarios. |
| 25 | Harness | Real UDP both-role occupancy/input/motion, delayed load, time/sleep, epochs/reconnect and malformed packets. Impaired vehicle link: 40-ms delay, 100-ms jitter, 20% loss and duplication. Four independent-process scenarios also passed. |
| 26 | Native checks | Completed passenger 20, EVP 24 and continuity 29. Earlier EVP partial 21/failure retained. Final bounded rerun details below. Counts are per fixture and overlap. |
| 27 | First-person regression | Normal passenger and continuity restoration passed. Earlier on-foot fixture measured about 3 m/s. Later driver-fixture abort exposed disabled controller. Final normal driving/passenger restoration and bounded Walk reached 2.5232 m/s; exceptional combinations remain open. |
| 28 | Restoration | Original save file sets, PlayerPrefs value names/types/raw data, loader and deployed plugins/configuration restored and hash-verified after fixtures. Test-created data preserved only ignored locally. |
| 29 | Original binaries | All 183 original DLL/EXE fingerprints unchanged. No proprietary assembly, asset, save, dump or machine path committed. |
| 30 | Blockers | Earlier intermittent native Client-driven stopping requires repeatability validation; complete exceptional driver/player/camera lifecycle recovery; second variant and different native-profile compatibility; broader native map paths. |
| 31 | Two-machine gate | Two legitimate native games: both role assignments, sustained steering/brake/reverse, input entry/exit, remote visibility, occupied late join/disconnect, shared sleep and connected transitions under impairment. Deferred separately; no user testing requested now. |
| 32 | Commit | New scoped implementation commit; exact hash is provided in the task final report. No earlier commit amended. |
| 33 | Implementation complete? | NO. Passing harness/native subsets do not waive the functional blockers. |

## Required automated/native matrix

A = automated authority/transport assertions; N = controlled native fixture;
D = remaining native/two-machine gate. These labels describe evidence, not 50 independent tests.

| # | Requirement | Evidence |
| --- | --- | --- |
| 1 | Host hideout | A; N Host return/load |
| 2 | Client hideout | A; N direct reconnect/follow |
| 3 | Active mission late join | A; N MadisonSt |
| 4 | Hideout-to-mission | A descriptor; N Client automatic follow; broader native Host map selection D |
| 5 | Mission-to-hideout | A; N both native roles separately |
| 6 | Slow Host load | A staged readiness; native stalled-load recovery D |
| 7 | Slow Client load | A delayed readiness; native stalled-load recovery D |
| 8 | Transition timeout | Phase timeout values asserted; actual elapsed deadline/native timeout cleanup D |
| 9 | Retry/recovery | A epoch/session reset; N reconnect |
| 10 | Stale epoch | A rejected |
| 11 | Duplicate transition | A idempotent |
| 12 | Readiness ordering | A phase/epoch checks |
| 13 | Baseline at boundary | A; N fresh native registry/clock |
| 14 | Join during loading | A phase barrier; native two-process D |
| 15 | Loading disconnect | A transport teardown; native D |
| 16 | Failed-load reconnect | A reset; N stable reconnect, native failed-load D |
| 17 | Host driver | A; N original entry/EVP |
| 18 | Client driver | A; N acceleration/input plumbing; final mission braking PASS, earlier failure retained |
| 19 | Host passenger | A; N production entry and EVP attachment |
| 20 | Client passenger | A; N native Client production entry/exit |
| 21 | Passenger entry | A reserve/confirm; N same original actor |
| 22 | Passenger exit | A ExitConfirm; N restored motor/camera |
| 23 | Driver exit | A; N normal native exit; aborted recovery D |
| 24 | Seated disconnect | A clears seats/input; all native roles D |
| 25 | Entry disconnect | A clears reservations; native interrupted attachment D |
| 26 | Exit disconnect | Authority clears actor state; native in-flight exit D |
| 27 | Vehicle destruction | A live-binding guards; N destroyed passenger anchor; full car destruction D |
| 28 | Client input validation | A actor/token/epoch/bounds/deadman; N delivery |
| 29 | Authoritative motion | A separate channel; N actual EVP acceleration |
| 30 | Moving passenger | A semantic pose; N frame attachment/EVP |
| 31 | Visible occupant | A avatar regression; N both seated rig creation; full visual lifecycle D |
| 32 | Camera/listener restore | N original identities/clip/normal exit; destruction/recreation D |
| 33 | Taken seat | A conflict rejected |
| 34 | Duplicate request | A request/repeat idempotence |
| 35 | Reservation timeout | A expiry preserves remaining actor; native D |
| 36 | Outside travel | A denial leaves epoch/connection unchanged; N empty-seat denial |
| 37 | Entering travel | A pending denial |
| 38 | Different vehicles | A denied |
| 39 | Both confirmed | A allowed; N production Host transition |
| 40 | Host passenger travel | A; N Host passenger native return |
| 41 | Client global travel | A denied; native selection prefix |
| 42 | Host sleep | A Host-only barrier; N original PassTime |
| 43 | Shared wake time | A midnight/day/week/counters; N native sleep |
| 44 | Duplicate sleep | A phase/sequence/token rejection before native application |
| 45 | Seat epoch cleanup | A; N scene reload original player/occupancy |
| 46 | WorldReady gate | A world/time/native readiness; N automatic join |
| 47 | Original restoration | N normal passenger/scene exits; exceptional driver FAIL/D |
| 48 | NetworkAvatar regression | Historical automated checks retained; N seated rig construction |
| 49 | Door/window regression | Historical automated/native acceptance retained; new native Client baseline applied |
| 50 | Loot/inventory regression | Historical automated checks retained; N original inventory identity/quantities |

Malformed/truncated/oversized/nonfinite/stale seat, motion and clock messages are bounded;
10,000 random motion/time envelopes cannot throw. Packet-loss assertions drain delayed
last packets before measuring the 350-ms deadman, rather than confusing valid arrivals
with an unexpired input leak.

## Validation and final bounded rerun

All four Release projects build with zero errors. Cached NuGet restores emit NU1900
because online vulnerability metadata is unavailable. The full automated runner passes
684 checks. Independent-process normal, abrupt Host, abrupt Client and world replication
(60-ms delay, 180-ms jitter, 10% loss) passed. Multiplayer and reconnaissance validators,
git diff --check and all 183 original-binary fingerprints passed before the accompanying commit.

The final native rerun in **MadisonSt|MainScene** completed the 24-assertion EVP fixture:
Host driver moved/stopped from 5.858 m/s to zero; managed Client driver moved 1.086 m,
reached 4.818 m/s and stopped from signed speed 3.011 m/s to zero. Four grounded wheels,
validated input and resulting native throttle/brake/handbrake were sampled. Original
controller/inventory/camera restoration passed for both roles. The setup now positions
the native driver at the authored seat and uses verified native door-bypass exit. This
successful fixture does not prove the precise cause of earlier stopping failure; retain
repeatability/exception gates rather than discarding that evidence.

The passenger fixture passed all 20 assertions again on the final native build, including
destroyed-seat recovery. Audit recorded six original cameras, one listener, identities
unchanged, PlayerScript enabled and CharacterController enabled. Bounded original Rewired
on-foot input produced Walk reaching 2.5232 m/s. This is movement fixture evidence, not a
new campaign/equipment/long-soak acceptance.

Eight hidden launches were used for this extension, one at a time, including a menu-only
attempt whose diagnostic opt-in had been reset by deployment. No new native process
crash was observed. Real managed/native fixture exceptions are retained above. Native
private memory was about 9.8 GB with system commit near 20.9/21.4 GB; this does not isolate
mod memory or prove the root cause of historical UnityPlayer crashes.

The final duplicate-readiness idempotence correction and its six automated assertions
were added after native collection. They are automatically verified, not retroactively
counted as native-tested paths. Broader map selection, second variant, differing profiles,
all exceptional recovery combinations and two-machine gameplay remain unaccepted.

Complete save sets were byte-verified after each launch. PlayerPrefs was restored with
all names/types/raw values verified; seven prior deployment/configuration files were
restored with equal hashes. Loader restoration and all 183 binary fingerprints passed.

## 0.6.4 passenger/driver follow-up

Current automated verification is **696 checks**. Twelve new checks cover available-seat
selection and concurrent seat revision races. All four Release builds have zero errors;
cached NuGet audit metadata still produces NU1900. Multiplayer/recon validators and four
independent-process scenarios passed. Protocol remains 8 because wire layouts did not change.

Production changes select the remaining available seat, reject an entry-frame passenger
exit pulse, scope native driver lifetime outside the car hierarchy and capture restoration
state before entry. A three-second stalled-exit recovery restores the surviving original
controller, camera, control UI, colliders and equipment selection. Already-seated startup
uses verified native exit defaults, including the hand camera; unknown pre-entry selection
cannot be reconstructed. Client input retries only revision conflicts for at most two
seconds, with fresh Host validation. Other denials stay final. Scene/disconnect/readiness
changes cancel the intent. Dead bindings close readiness and cannot authorize travel.

Remote driving mirrors native alarm Rigidbody removal/recreation by occupancy without
running native RunCar on the Host passenger. Safe-exit probes ignore the verified car's
own colliders, since native exit opens its door, but continue rejecting other actors,
world obstacles, unsuitable ground and bounded-buffer overflow. No camera/controller clone
is created. Seated NetworkAvatar resources cache their verified anchors and are released
when their original resources disappear.

Additional bounded native evidence on 2026-10-08:

- Driver recovery completed **12 assertions** for stalled exit and destroyed seat anchor,
  retaining the original player, inventory and camera/listener identities. This was before
  the later already-seated hand-camera default correction; that branch remains a native gate.
- Two earlier 0.6.4 EVP runs completed **24 assertions** across both roles. Their second role
  reset the vehicle transform; a stricter ground-contact fixture subsequently failed for
  Client-driver setup. Earlier success does not waive that failure. The final source now
  tests ordinary stopped-car role handover without that second teleport, and requires ground
  contact before acceleration. Its final native completion is still pending.
- The revision-aware continuity fixture completed **29 assertions**: Host passenger sleep,
  mission return with the connection retained, automatic native Client mission late-join,
  Client passenger exit, Client driver input delivery, fresh epoch/player baseline, automatic
  hideout return and direct hideout reconnect. Its other peer mirrors the native manifest.
- Input fixtures observed functional original passenger entry/exit, original driver entry,
  both remote seated presentations and original camera/inventory identities. Earlier runs
  failed on stale fixture revisions, an unrelated on-foot raycast during injected input,
  the car's own closed door in the capsule probe, and fixture placement of the on-foot Host
  in the other seat's exit. These are partial runs, not a completed interaction-cycle claim.
  Final source isolates only the injected on-foot frame, preserves the opposite-side exit,
  holds the handbrake, waits for original eligibility and checks the stopped vehicle.

Six native gameplay launches preceded a seventh menu-only attempt. During that last attempt,
two unrelated game instances were launched from Windows Explorer. The recorded fixture
process was stopped; no unrelated instance was terminated without permission. Save/PlayerPrefs
restoration for the first six launches passed. Final restoration is pending until unrelated
game instances close. This also prevents safe native request routing, because diagnostics
and save/configuration files are shared. No system-memory settings were changed.

### Requested 25-item report

| # | Item | Current result |
| --- | --- | --- |
| 1 | Native passenger findings | Driver-only controller; verified associateSitPos anchor is reusable but not a local passenger system. |
| 2 | Passenger implementation | Original actor/motor/look/camera retained; scoped collision/input suspension and reversible seat-relative view. |
| 3 | Host Driver / Client Passenger | Authority/harness passed; native Client passenger entry/exit and native Host driving separately observed. Two-native pairing deferred. |
| 4 | Client Driver / Host Passenger | Validated input moves native Host EVP and real passenger in completed earlier fixtures; strict grounded role-handover acceptance remains open. |
| 5 | First-person camera | Original camera/listener identities preserved; passenger clip/look restored. Already-seated hand-camera fallback needs native coverage. |
| 6 | Motion / attachment | Host EVP only; sequenced Client input/Host motion; frame attachment implemented. Final grounded role handover pending. |
| 7 | Remote visibility | Sanitized native seated rigs for both roles; outside presentation restored in completed partial cycle segments. Full final cycle pending. |
| 8 | Entry / exit | Original Interact path and reversible passenger exit observed; scoped driver safe-exit fix awaits completed final input cycle. |
| 9 | Occupancy conflicts | Host validation, independent revision, duplicate idempotence, reservations/exit confirmation, generation/epoch/session guards; revision retry bounded. |
| 10 | Host-only travel | Client requests denied before load/epoch changes; Host ordinary validation requires no human approval. |
| 11 | Host-only sleep | Host native PassTime with automatic barrier/time replication; Client cannot initiate global sleep. |
| 12 | Two-seat precondition | One confirmed Driver and one confirmed Passenger in one live shared vehicle; pending/exiting/outside/separate cars denied. Solo exempt. |
| 13 | Scene integration | Freeze, announce, original load, fresh player/registry/baseline/time/inventory, readiness; no old seat references carried. |
| 14 | Hideout join | Native follower/reconnect observed with managed Host. |
| 15 | Mission late-join | Automatic native MadisonSt join observed; initialized profile required. Independent-profile parity remains open. |
| 16 | Mission return | Production Host and Client paths separately completed with retained connection and fresh baseline. |
| 17 | Disconnect / reconnect | Automated actor/seat/input cleanup passed; native hideout reconnect passed. All moving seated disconnect combinations remain native gates. |
| 18 | Automated / harness count | 696 automated checks; four independent-process scenarios. Native counts are per fixture, overlap and are not human acceptance. |
| 19 | Native evidence limits | One native process plus managed actor/manifest; completed 12-recovery and 29-continuity fixtures plus earlier 24-EVP runs; strict/partial failures retained. |
| 20 | Original gameplay | Surviving original controller/camera/inventory restoration observed. Final on-foot audit/movement and broader equipment/offline combinations pending. |
| 21 | Build / validators | Four Release builds, multiplayer validator, recon validator and diff whitespace pass; NU1900 metadata warning remains. |
| 22 | Binary preservation | All 183 original DLL/EXE fingerprints unchanged; proprietary evidence remains ignored. Final deployment/config restoration pending. |
| 23 | Commit | Scoped follow-up commit; exact resulting hash provided in the task final response. |
| 24 | Blockers | Final native interaction/grounded handover; destroyed original camera/whole-car reinitialization outside scene boundaries; second variant/different profiles/broader maps; unrelated running games block final native routing/restoration. |
| 25 | Complete / final gate | INCOMPLETE. No Milestone F. Two legitimate native machines, both roles, travel/sleep, occupied late-join/disconnect and sustained driving under impairment remain a separate deferred gate. |

## 0.6.5 P0 vehicle lifecycle and frame-stability report

The user observed passenger body/camera misplacement, remote/vehicle disappearance after
mission exit, failed re-entry and intermittent stalls. Working menu join, automatic
mission following and retained transport remain protected. This is stabilization inside
the scene milestone; no storage/economy/NPC or other Milestone F work was started.

Native old-build tracing reproduced the original actor becoming unavailable during
ExitCar's controller-disabled tail, which incorrectly failed the scene and hid the remote
avatar. The car remained alive with zero vehicle tombstones in that reproduction; actual
vehicle disappearance is a distinct unproven symptom, not retroactively attributed to it.
Vehicle liveness now reflects native existence/loaded scene, operational reference loss
releases occupants without a global scene failure, and only verified native destruction
despawns the canonical car. Verified manager-slot keys survive native root reparenting.
Idle native CarController updates resume its interaction/door/lootbox lifecycle.

The cooperative seat is now explicitly rear-right, with separate body and eye references.
Car04/Car05 mesh triangles and sampled native driving_front hips/head calibrate vehicle-local
positions. Main-car measurements: rear actor/reference (.556,.157,-.491), remote hips
(.5489,.4403,-.4970), head (.5455,1.0076,-.5956), original passenger camera (.556,1.05,-.491).
NetworkRoot and VisualRoot stay at the semantic reference; the seated rig applies the
verified native source offset once. The front lootbox is unchanged in every completed cycle.
Actual original-camera rear renders remain ignored locally. The later active second-model
fixture uses authoritative variant initialization rather than accepting its earlier
inactive render. It completed passenger attachment (20 checks), twenty alternating
seat cycles (414 checks), original-camera rear render and native EVP driving (28 checks).
Van Host/Client-input travel was 7.614/7.880 metres, with stopping and original first-person
restoration. Remote van hips were (.3450,.0301,-.8500), head (.3418,.5271,-.9363), original
passenger eye (.3974,.5700,-.9546). Neither saved CurrentCar nor VanBought was changed.

Three earlier native repeatability runs completed twenty alternating roles, 413 checks
each. The final calibrated source additionally verifies rear-side hips and completes
414 checks for twenty cycles in both its initial world and MadisonSt mission. Consolidated
runs also completed 32 continuity checks: Host passenger sleep/return, native Client
mission follow, passenger exit, driver exit/re-entry, automatic return and hideout reconnect.
A later repeat exposed a one-shot fixture request arriving before the existing .4-second
interaction debounce; it was rejected without closing world readiness. The fixture now
pumps for .45 seconds before the new role entry. Its previous failure is retained.
A prior mission attempt stopped before seat testing because native timeScale was zero. Background physical input had affected
that owned fixture. The consolidated fixture neutralizes that input, uses original UI
close routines, suppresses unrelated on-foot raycasts and injects only the explicit
entry/exit pulse. This is a controlled native boundary test, never human approach acceptance.

Current automated verification is 712 assertions, including rolling/profile/allocation
bounds, semantic identity under root-index change and 300 ordered real-UDP callbacks
across count/time-budgeted polls. Four independent-process scenarios passed, including
60-ms delay / 180-ms jitter / 10% loss baseline convergence at revision 38. The first
concurrent abrupt-Client attempt failed with a Host that received no packets; its isolated
rerun passed. The failed run is retained and its cause is not claimed proven.

Measured scan-loop fix, rolling frame distributions, retained cold startup/cleanup spikes
and profiling limits are in [S1 P0 evidence](playability-stabilization-s1.md). Reliable
callbacks are preserved when bounded polling yields; atomic transactions remain intact.
The no-avoidable->100-ms performance gate is not accepted from the current fixture alone.
No matched vanilla/real-peer production run isolates all remaining spikes.

Targeted native timing also exposed 8,040.7001 ms baseline application in the 1,727-entity
mission, and 7,520.399 ms on a second native Client mission join. DoorNetworkAdapter searched
the entire scene for all Lockpick objects for every door. Scene preparation now indexes
the authored links once; application retains the same active-lock and shared-leaf rules.
World/loot binding logs are capped at sixteen examples each with full aggregate counts.
The next native run measured mission baseline application at 131.0122 ms and hideout at
under 1 ms. A further per-type trace attributed 140.2997 ms of a 153.1320 ms mission apply
to 592 pickups, versus 7.0689 ms for 950 doors, 1.8005 ms for 184 windows and .6267 ms for
the vehicle; validation took 2.6565 ms. The mirrored managed Host had inherited default
Available=true for inactive native Client pickups. Initial bindings now capture actual
native availability, and equal native transforms/activation/physics flags avoid redundant
writes. This fixture correction must not be presented as proving different real profiles
never require expensive authoritative activation. The corrected consolidated run measured
mission baseline application at 13.0563 ms; its subsequent native mission join was
13.3261 ms. Across all four mission/hideout applies the mean was 6.8115 ms, median .4531 ms.
Per-type costs were validation 2.3915 ms, 950 doors 5.7760 ms, 184 windows 1.4398 ms,
592 pickups 2.4703 ms and one vehicle .4836 ms. The same run completed twenty initial-world
and twenty mission cycles (414 checks each), passenger attachment (20 checks), and the
32-check continuity sequence. Its real EVP Host-driver phase accelerated 6.691 metres
at maximum 5.363 m/s and stopped. The subsequent Client-input phase failed displacement
(.043 metres, maximum .103 m/s), despite four grounded wheels and unpaused physics.
That failure is retained as a separate native driving gate; it is not waived by the
successful lifecycle/continuity/baseline measurements. Instrumented open-street follow-up
separates environmental obstruction from native input/physics handover. That subsequent
open-street MadisonSt run completed 28 checks with unchanged assertions: both roles
accelerated, native wheels grounded, validated input/motion streams converged and service
braking stopped each role. Client input moved the native vehicle 7.859 metres at maximum
6.057 m/s while the original Host passenger followed and retained its camera/inventory.
Bounded proof recorded engine=true, throttle=.45, brake/handbrake=0, awake/enabled physics,
clear forward ray and terrain/road wheel contacts. The earlier hideout failure remains
recorded; its exact obstruction/collision cause is not proven by the successful mission run.

Fresh pre-task save/config/deployment/PlayerPrefs backups supersede the older backup that
predated later user gameplay. No unrelated game was killed. Before the final consolidated
run, exact guarded snapshots showed all main saves and gameplay/settings preferences
unchanged. Only four ancillary saveInfo/backup files and three backup/Unity session counters
were restored, preserving post-test data. After the final owned run, all game processes
were closed and a new exact post-test snapshot was archived. Eight changed save files
(including saveData.thief), four preference values (CurrentBackup, TipSave and two Unity
session fields) were restored with current-snapshot guards. All 35 pre-task save files,
file set and every preference name/type/value were verified. The seven pre-task plugin,
dependency/configuration files were restored byte-for-byte too; the installed pre-test
plugin is 0.6.4.0. The built 0.6.5 release remains in the workspace. Older historical
backups were not restored over newer user progress. Post-test data remains ignored locally.

### Requested P0 report

| # | Item | Current evidence / limit |
| --- | --- | --- |
| 1 | Status | INCOMPLETE: remaining frame-spike, reported actual vehicle loss and exceptional recovery gates; F unstarted. |
| 2 | Version | Mod 0.6.5 / protocol 8; both peers must use matching mod. Semantic key/seat contracts changed, wire layouts did not. |
| 3 | Native vehicle findings | Single native local driver path; associate reference alone does not implement passenger control. Exit clears vehicle flags before enabling CharacterController. |
| 4 | Rear anchor | Named rear-right reference, verified interior mesh and cushion/driver-rig calibration; no arbitrary world offset. |
| 5 | Front lootbox | Native position, active state and object retained throughout completed cycles. |
| 6 | Passenger body | Main-car right/rear hips/head measured; sanitized driving_front source offset applied once beneath NetworkAvatar. |
| 7 | Passenger camera | Original camera, independent rear eye at local Y=1.05, scoped near clip/look; original settings/identities restore. |
| 8 | Driver exit | Ordinary native sequence retained; original actor stays available through controller-disabled exit. Twenty initial-world cycles passed. |
| 9 | Passenger exit | Safe grounded opposite-side exit, captured controls/collisions/camera restored, authoritative confirmation releases seat. |
| 10 | Vehicle persistence | Native object/body identities, entity/generation/epoch unchanged in completed cycles; operation loss does not imply destruction. Actual reported disappearance not independently reproduced. |
| 11 | Re-entry | Twenty alternating roles in both initial world and mission; original components and one native alarm body retained. Native Client driver exit/re-entry also passed in completed continuity runs. |
| 12 | Remote visibility | Both seated roles, one visible remote character and restored on-foot presentation checked each cycle. |
| 13 | Repeat cycles | Three earlier 20-cycle/413-check runs; consolidated initial-world and mission runs each 20 cycles/414 checks. Counts overlap and are not independent human scenarios. |
| 14 | Scene continuity | Existing descriptor/load/baseline barrier retained; old seats invalidated before new epoch; completed 32-check native regression sequences. |
| 15 | Hideout join | Existing controlled native automatic follower/reconnect evidence retained; independent native profiles remain deferred. |
| 16 | Mission late join | New native automatic MadisonSt preparation completed, 1,727 records / 503,666 bytes / 1,312 chunks in the observed larger baseline. |
| 17 | Mission return | Confirmed same-car Driver+Passenger required for Host return and applicable sleep. Native Host sleep/return and Client follower/reconnect passed in completed sequences. |
| 18 | Frame-stall causes | Empty-manifest repeated scans and per-door whole-scene lock searches confirmed and fixed. Pickup activation skew corrected in mirrored native fixture. Native load/GC costs and actual vehicle disappearance remain separate limits. |
| 19 | Network queues | 128 callbacks / configurable 2-ms budget; deferred reliable events retained in order; >8192 events explicitly fails session. No silent canonical drops. |
| 20 | Frame budgets | Four baseline chunks per poll; immutable capture/apply and seat/inventory transactions remain atomic; repeated empty scans removed; no unsafe Unity worker access. |
| 21 | Before/after performance | Registry rebuild average 10.2864→.0081 ms; mission baseline 8040.7001→131.0122 ms after lock indexing, then 13.0563 ms with accurate native availability. No real-profile/whole-frame freeze acceptance claimed. |
| 22 | Automated count | 712 checks passed, retaining previous coverage; native fixture assertions reported separately. |
| 23 | Harness count | Four independent-process scenarios passed; original failed abrupt-Client attempt retained, isolated rerun passed. |
| 24 | Native limitations | Both active vehicle variants pass controlled seat and EVP fixtures. One native game/managed peers; human approach and two independent profiles/machines are not established. |
| 25 | Original first person | Original player/controller, motor/look, inventory, cameras/listeners and collision snapshots retained/restored. Final scoped Rewired on-foot input after van cleanup observed native movement and changing look yaw, then ended cleanly; no human singleplayer playthrough claimed. |
| 26 | Build/validators | Four Release projects build with zero errors; NU1900 audit-feed warning. Exact vendored queue adaptation/provenance, scripts, tracking, paths and whitespace validators pass. |
| 27 | Binary fingerprints | All 183 original DLL/EXE fingerprints unchanged; no proprietary binaries/assets/captures tracked. |
| 28 | Commit | Scoped stabilization commit; exact hash provided in the task final response. |
| 29 | Blockers | Instrumented mission driving passed 28 checks for each vehicle variant; exact cause of the earlier hideout handover failure and actual reported vehicle disappearance remain unproven. Unisolated >100-ms cold/GC/native costs, exceptional original-camera/whole-vehicle reinitialization and independent profiles/maps remain open. |
| 30 | Deferred real two-player gate | Two legitimate native machines, both role assignments, human input entry/exit, sustained turn/brake/reverse, rear view/body, occupied joins/disconnects, same-car travel/sleep and uninterrupted scene/reconnect under impairment. |

### Final 0.6.5 installation and targeted follow-up

Remote driving now clears the mirrored native engine flag after driver release;
previously this could keep original idle vehicle interactions suppressed. Emergency
driver recovery restores the alarm Rigidbody removed by native RunCar when ExitCar
is cancelled. Passenger teardown clears its captured state in a finally block.
Vehicle authority queries select vehicle records without sorting the entire world;
the automatic post-join whole-scene avatar audit is removed (manual audit retained).
Rear-right geometry, original camera/listener and protocol 8 are unchanged.

Validation: 716 automated checks (four new vehicle-query assertions), 174 focused
vehicle/travel checks, one impaired world transport scenario, Release build and
both validators passed. Existing native fixtures passed 49 seat/lifecycle checks,
20 passenger checks, 14 recovery checks and 32 scene-continuity checks. Six added
native assertions cover the running remote engine and recovered alarm body; the
brief entry/exit action uses two cycles with unchanged assertions. Two owned game
launches totalled approximately 268 seconds; each stayed below five minutes.
The first menu-only launch rejected the disabled development fixture; restoring
the backed-up diagnostics setting enabled the second launch. Saves, PlayerPrefs
and configs were restored from fresh backups. Version 0.6.5 is directly installed;
all three DLL hashes match the validated build and 183 original binaries remain
unchanged. The van's existing geometry/fixture evidence is retained; this follow-up
ran native lifecycle checks on the main car, without repeating the van driving run.
The historical disappearance symptom and all reported frame stalls are not claimed
fully reproduced or eliminated. Independent two-machine acceptance remains deferred.
Milestone F has not started.
