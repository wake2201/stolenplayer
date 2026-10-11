> S1 update: S1 replaces lengthy autonomous gameplay navigation with start-manual-acceptance.ps1 and collect-manual-acceptance.ps1. Ctrl+F10 marks, Ctrl+F7 snapshots, Ctrl+F6 profiles ten seconds. Every test is AUTOMATED, HARNESS or MANUAL; missing human gameplay evidence is MANUAL_REQUIRED. Read manual-runtime-testing.md for batching, capture and verified save/config/PlayerPrefs restoration.

# Controlled runtime reconnaissance

Milestone D adds protected `world-probe`, `world-audit`, `world-open` and
`world-close` development fixtures plus `start-interaction-harness.ps1`. Their
original-range selection, manifest capture, role limitations and cleanup workflow
are documented in [world-interaction-sync.md](world-interaction-sync.md). These are
explicit development tools; accepted evidence is recorded in
[milestone-d-acceptance.md](milestone-d-acceptance.md).

Milestone E adds protected `loot-probe`, `loot-audit`, `loot-pickup`, `loot-drop`
and `loot-catalog` fixtures and `start-inventory-harness.ps1`. Pickup invokes the
original path only for a supported item within original range and line of sight.
Harness roles use real registry/catalog descriptors and production transactions;
synthetic canonical completion is distinguished from native Unity execution.
An exact live process identity, existing save backup and development permit are
required. Harness duration is bounded to 10–900 seconds. See
[loot-inventory-sync.md](loot-inventory-sync.md) and
[milestone-e-acceptance.md](milestone-e-acceptance.md) for contracts and evidence.

These tools are development diagnostics. They implement no world synchronization.
Game paths, complete captures, screenshots, saves, decompilations and crash reports
remain under ignored local storage. Read [the evidence report](full-runtime-recon.md)
before interpreting a capture as proof of behavior.

## Session lifecycle

```powershell
# Close an earlier session with stop-game before selecting another mode.
$session = ./tools/runtime/launch-game.ps1 -Mode Both -MonitorSeconds 900
# Other isolation modes: Vanilla, BepOnly, Diagnostics, Multiplayer.
./tools/runtime/snapshot-scene.ps1 -Target player
./tools/runtime/snapshot-avatar-candidates.ps1
./tools/runtime/snapshot-scene.ps1 -Target avatars -Scope 'NPCs[0]'
./tools/runtime/snapshot-scene.ps1 -Target inventory
./tools/runtime/snapshot-scene.ps1 -Target interactions
./tools/runtime/snapshot-scene.ps1 -Target vehicles
./tools/runtime/snapshot-scene.ps1 -Target managers
./tools/runtime/read-capture.ps1 -Events snapshot_end
./tools/runtime/stop-game.ps1 -Session $session
# Only for a hung verified process: stop-game -Session $session -Force
./tools/runtime/restore-save.ps1 -Session $session
# Optional: also preserve newly created test saves locally and restore the file set.
./tools/runtime/restore-save.ps1 -Session $session -RestoreFileSet
```

Launch verifies original binary hashes, backs up every existing `.thief` and
`saveInfo*.txt`, and records exact executable/PID/start time. Vanilla temporarily
disables our recorded Doorstop addition. Plugin isolation renames only our own
plugin entry DLLs; stop restores them and the previous loader configuration.
The hidden helper samples available RAM, commit usage/limit, private bytes and
working set once per second for the requested duration. It does not change memory
configuration or stop unrelated applications. A process identity mismatch aborts.

Stop collects Player/BepInEx logs, recent crash text and this session's NDJSON,
then verifies the original binary baseline. Save restoration requires a closed
game, preserves post-test files, restores backed-up files and compares hashes.
By default it leaves later files in place. Optional `-RestoreFileSet` moves newly
created `.thief`/`saveInfo*.txt` files into this session's ignored preservation folder,
after checking paths, creation time and hashes. It refuses extras that predate the
session. No file is deleted. It does not revert Windows registry preferences.
Keep experiments to existing backed-up profiles; audit any new profile separately.

## Bounds and interpretation

Snapshots traverse loaded scenes, including inactive objects, with explicit object,
record, hierarchy and byte limits. Manual traversals visit at most 50,000 objects;
inventory permits 1,000 component records, other manual targets use the configured
limit (300 in this deployment). Automatic manager snapshots use ObjectLimit
(12,000 here). `-Scope` accepts an exact observed scene-root hierarchy path and
restricts that capture to matching roots; roots with the same path in different
scenes can both match. Always retain the scene identity from the component record.

Files rotate per manual target and stop emitting at 32 MiB. Hierarchies are capped
at 4,096 player nodes or 512 candidate nodes; skins at 256 bones, clips at 160,
fields at 240 and collection previews at 24. String values are capped at 512
characters. Metadata describes game-owned assets; vertices, textures, curves and
audio are not exported. A 4 ms traversal timer yields between objects; inspection
of one complex actor can exceed that budget. This is not a strict frame-time cap.

`snapshot_end.truncated`, hierarchy completion and byte-limit warnings must be
checked. Missing records in a truncated capture do not prove absence. Fields are
read without arbitrary getters; only a fixed navigation-property allowlist and
Unity engine metadata are inspected. Lazy singleton getters are never invoked.
An existing `SteamManager` object can be found without initializing Steam.
Field records now include scene and component ID. Older R1–R9 captures need
owning-component correlation. Generic Behaviour metadata distinguishes enabled
from active-and-enabled; engine Transform, camera and AudioSource metadata is
bounded and uses only known Unity getters. Raw audio data is never exported.

## Explicit experiments

```powershell
./tools/runtime/snapshot-scene.ps1 -Target trace-on
# Enable prefix/postfix observation for a finite list of actions/save boundaries.
./tools/runtime/snapshot-scene.ps1 -Target trace-off
```

Tracing is off by default, stops at 1,000 observer events per explicit enable window,
reports the limit, never changes arguments
or results, and removes only its own Harmony owner on unload. Save payloads are
represented by length, not their contents. Entry/return of a coroutine-starting
method does not establish coroutine completion.

For the offline avatar experiment, put an exact previously inspected NPC hierarchy
path in ignored `avatar-source.txt`, then request `avatar-test`. It constructs one
fresh visual hierarchy, remaps bones, shares meshes/materials in-process, and copies
one idle clip privately with events removed. `avatar-remove` removes only that
visual and its private clip. Original NPC objects are never instantiated.

`pickup-test -Session $session` is an opt-in mutation experiment requiring the
matching process and an existing save backup. A five-minute, PID-specific permit
is consumed once. Only one active, already dropped starter crowbar (definition 0,
`wasDropped=true`, non-heavy/non-money, within 5 m, adequate capacity) qualifies.
It invokes the original `PickUp()` after harness validation and records the delayed
transaction. It does not test the original player's aiming/raycast eligibility.
Do not interpret this fixture as theft, heavy carrying or mission-reward acceptance.

## Milestone B bounded input and soak

`[MilestoneB] Enabled` is false by default. Explicitly enabling it permits the
PID-bound `milestone-b-fixture.ps1` requests: audit, host, disconnect, input and
input-stop. Input lasts at most 60 seconds, requires the existing backed-up session
and genuine enabled/unpaused player, and feeds only that player's original Rewired
getters. Movement axes are ±1, look axes ±100; nonfinite values, arbitrary buttons,
invalid booleans and other process identities are rejected. It writes no player
transform, camera field, motor flag, inventory or tool. Expiry, pause, scene events
and unload remove its own patches. Evidence must say DEVELOPMENT INPUT FIXTURE,
never human-driven input. Trace observations of original tool use are separate.

`run-avatar-soak.ps1 -Session <local-session>` runs ten 180-second localhost harness
connections. Incoming presentation is synthetic; outgoing local observation is
from the game. Audit connected/disconnected counts and private clips separately
from working set/private bytes/managed memory. The script only terminates its own
hidden harness helper on failure, never the game. The diagnostics output retains
the 32 MiB cap; ensure the bounded soak fits it or rotate captures between trials.

Disable MilestoneB, trace and production DevelopmentControls after acceptance.
The production plugin has no reference to this input fixture. Remove the diagnostics entry
DLL or disable the loader for normal play if reconnaissance is no longer needed.

## Milestone C synthetic world fixture

Enable production `[Development] WorldFixture = true` only for explicit development
acceptance. It initializes 35 managed TestEntities on the host's first connection,
captures revision 35, then commits A update/B tombstone/D spawn through revision 38.
Reconnect preserves that canonical host state. Scene reset releases it. It never
creates doors, pickups, NPCs, tools, game inventories or save records. The read-only
player descriptor probe is unregistered and supplies one runtime identity comparison.
F8 includes world readiness, epoch/revision, counts/collisions, chunks/bytes/backlog.
With Diagnostics.DevelopmentControls, its audit button hashes canonical registry
records only on demand.

`start-avatar-harness.ps1 -Sequence world -Role client|host` enables the matching
synthetic transport fixture. The existing PID/save-backup-bound diagnostics helper
also accepts `milestone-b-fixture.ps1 -Action join` to join localhost port 9050; it
uses the public network API and injects no gameplay input. `-Action audit`, `host`
and `disconnect` remain available. These network/audit requests are development
control, not DEVELOPMENT INPUT FIXTURE movement evidence. Disable WorldFixture,
MilestoneB and DevelopmentControls and restore original configs after testing.

## Native vehicle and continuity fixtures (0.6.3)

PID/save-bound requests include vehicle-bootstrap (original Continue), vehicle-native-exit,
vehicle-passenger-cycle, vehicle-passenger-preview, vehicle-drive-cycle, vehicle-coop-cycle
and vehicle-audit. DevelopmentControls and MilestoneB must both be explicitly enabled.
Original-camera preview is rendered to an ignored local PNG, with target texture restored.
Passenger checks restore camera/controller/inventory state and test destroyed-seat lifetime.
Drive-cycle uses one native EVP simulation and two localhost transports, bounded driver
inputs, acceleration, service braking, seat following and sanitized seated rigs. The
other actor is managed. Coop-cycle exercises production seat validation, original sleep,
native Host return, native Client automatic mission following, role reversal and hideout
reconnect. Its managed peer mirrors the native fixture manifest; different native profiles
and human/two-machine gameplay are not thereby accepted.

Save/config/plugin files and raw PlayerPrefs are backed up before testing. Stop the exact
recorded process, wait until closed, then restore its complete save file set, PlayerPrefs
and prior deployment/config hashes. Test-created save files remain ignored locally. One
native game is used at a time; monitor system commit as well as working set. No pagefile
or system setting is changed. Raw captures, previews and proprietary recon stay untracked.
Fixture coroutines own and dispose their nested iterator stack on failure/cancellation,
so Unity exceptions cannot silently skip outer restoration finally blocks. This harness
guard does not by itself establish all production exceptional driver-exit recovery.

### P0 additions, 0.6.5

vehicle-interaction-cycle executes twenty alternating native local driver/passenger cycles
with a managed opposite actor. It injects one original Rewired interact pulse at the native
entry/exit boundary, rather than claiming human approach/raycast acceptance. Fixture-only
axis suppression makes positioning deterministic. It verifies original camera/listener,
inventory and physics identities, one remote avatar, front lootbox preservation, persistent
entity/generation/epoch and open scene readiness. The alarm body's verified native
remove/recreate lifecycle is checked separately from the preserved original body set.
vehicle-mission-prepare uses production automatic Client following into MadisonSt with a
managed Host and scoped initialization reads; it avoids setting the local profile's map
preference. A subsequent lifecycle fixture can therefore run in the reported mission
context. vehicle-coop-cycle also checks native Client driver exit and re-entry before return.

vehicle-geometry records bounded native field/mesh/seat and sampled driving_front hips/head
coordinates. Bounded mesh CSV/triangles and temporary original-camera renders remain ignored
locally; their exports can be expensive and are excluded from stable-play frame acceptance.
Camera transform, target texture and near clip restore in finally; no new camera/listener
or native storage mutation occurs. vehicle-profile-reset / vehicle-profile-summary reset
or explicitly export fixed profiler rings. The normal overlay does not perform those exports.
The exact LiteNetLib adaptation is recorded in its provenance and checked patch descriptor.
stop-game now waits for the verified owned process to exit before collecting/restoring loader
files. Never restore an older save or full preference set over newer user progress; preserve
post-test data and check ownership/current values before any restoration.

vehicle-stability-cycle runs rear geometry/passenger capture, twenty initial-world
cycles, production automatic mission preparation, twenty mission cycles and native
sleep/return/Client exit/re-entry/reconnect. It neutralizes physical input only while
the explicit fixture is active and disposes those patches in finally. Wait for the
original Continue load and on-foot actor before requesting it. Native driving fixtures
close the delayed native town selector through DisableTownSelection and use frame
yields so their wall-clock deadlines remain observable if physics is paused.
vehicle-mission-drive-cycle reuses that scoped input isolation and production mission
initialization, then tests native EVP roles without teleporting between handovers.
vehicle-variant-cycle uses the same initializer with authoritative vehicle variant 1
and second-vehicle availability; no saved vehicle-choice preference is written by the
fixture. It tests the original manager-selected car, passenger camera, repeated seat
interaction and EVP driving. Native drive proof logs are bounded to half-second intervals
and record applied controls, engine, wheel ground and forward obstacle rays. These are
development diagnostics, not production per-frame logging or real two-machine acceptance.
