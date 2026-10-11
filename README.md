# Thief Simulator 2 — cooperative multiplayer project

Current build: **0.6.13 / protocol 13** — vehicle door state uses stable vehicle/native-door identities and the existing authoritative world snapshots and transition replication. Rear passenger entry requires a real, validated rear-side native door. The confirmed driver still runs original EVP locally on either peer, with sequenced motion and Host validation. See [vehicle door and entry preservation](docs/vehicle-door-sync-preservation.md) and [driver ownership](docs/driver-owned-vehicle-physics.md). Native vehicle storage rules, Client camera-ray selection, Host door identity/range checks and rear-center passenger calibration remain documented in the
[vanilla-first storage findings and scope](docs/vanilla-first-vehicle-storage.md).
The previous [health audit](docs/multiplayer-health-068.md) covers loading lifecycle
safety, narrower native vehicle hooks and multiplayer work reductions. Historical milestone
counts and versions below describe their accepted slices; real two-machine
acceptance remains deferred. No NPC/security-camera milestone starts here.

Two-player LAN/direct-IPv4 prototype for the analyzed Unity 2020.3 Mono build.
Host owns session membership (host ID 1, client ID 2); each peer sends its own
position/yaw plus semantic player state and displays one visual-only civilian avatar.
Milestone D adds authoritative Door/Window adapters and Open/Close requests.
Milestone E adds authoritative supported loot, personal inventory transactions and drops.
Milestone F adds a shared Host wallet, original buying/selling and cooperative storage.
AI, advanced vehicles, cooperative saves,
progression, Steam transport and chat are outside the implemented synchronization.

The mandatory scene/travel passenger extension is **0.6.6 / protocol 9**, with
731 automated checks and separate controlled native vehicle/scene/time fixtures.
[Shared vehicle contracts](docs/vehicle-passenger-sync.md) implement two-phase
Host occupancy, a real scoped local passenger, driver-only input and minimum Host
EVP motion on a separate sequenced channel. Both confirmed seats in the same car
are mandatory for connected mission-to-hideout travel. Host shared sleep uses
original native time advancement and acknowledged canonical clock/day counters.
Remaining acceptance gates include the historical vehicle-disappearance report,
unisolated frame spikes and independent two-machine gameplay. These are documented
in the [scene acceptance report](docs/scene-continuity-acceptance.md), which
distinguishes native fixtures, transport evidence and the deferred two-machine gate.
Milestone F is implemented for supported native unit-count items. See [current evidence and limits](docs/milestone-f-acceptance.md).

The final goal is high-quality, full-sync, two-player cooperative multiplayer.
Milestone B now supplies a production avatar factory, sanitized civilian
Idle/Walk/Run, semantic protocol and renderer-only crowbar equipment. Its accepted baseline
contained 145 automated checks, retaining all prior 129. The final game+harness decision, sustained
original-motor fixture measurements, normal functional crowbar regression and
reconnect/soak evidence are in the [Milestone B acceptance report](docs/milestone-b-acceptance.md).
**TWO-GAME ACCEPTANCE: DEFERRED** as a separate gate. The [future test plan](docs/two-game-acceptance-plan.md)
first attempts two normal legitimate local processes; no DRM/authentication bypass.
The locally controlled player permanently retains the original first-person stack.
Milestone C now supplies the generic entity registry, revision model, bounded world
snapshot/acknowledgement, ordered backlog and tombstones in mod 0.4.0 / protocol 3.
**290 automated checks pass**, including the original 145. Game-host and game-client
synthetic world tests, avatar regressions and populated scene cleanup/reload pass.
**Milestone C: ACCEPTED — TWO-GAME VALIDATION DEFERRED.**
Milestone D is accepted at mod 0.5.0 / protocol 4 with **367 automated checks**,
real game/harness interactions, matching hashes and scene reload/rebinding. See the
[Milestone C acceptance report](docs/milestone-c-acceptance.md).
[Milestone D acceptance status](docs/milestone-d-acceptance.md) and
[world interaction contracts](docs/world-interaction-sync.md) distinguish automated
verification from real Unity action/presentation evidence and LIMITED remote tool
completion. **Milestone D: ACCEPTED — TWO-GAME VALIDATION DEFERRED.**
Milestone E uses mod **0.6.0 / protocol 5**, with **476 automated checks** including
all previous 367. See [loot/inventory contracts](docs/loot-inventory-sync.md) and
[Milestone E acceptance evidence](docs/milestone-e-acceptance.md) for the tested
subset, synthetic versus native evidence and remaining limitations. The historical E slice preceded the shared wallet implemented in 0.6.6.
**Milestone B: ACCEPTED — TWO-GAME VALIDATION DEFERRED.**
Startup failures remain evidence; no universal startup fix is claimed.

- [Production network avatar and runtime acceptance](docs/network-avatar.md)
- [Permanent cooperative gameplay rules](docs/coop-gameplay-rules.md)
- [Network entity registry](docs/network-entity-registry.md)
- [Initial world snapshot](docs/world-snapshot.md)

- [Comprehensive runtime evidence and remaining fixtures](docs/full-runtime-recon.md)
- [NPC avatar selection and safe visual experiment](docs/avatar-candidates.md)
- [Animation, first-person rig and equipment](docs/player-animation-inventory.md)
- [Authority and replication architecture](docs/multiplayer-architecture.md)
- [Full synchronization roadmap](docs/full-sync-roadmap.md)
- [Milestone G prompt: NPCs, security cameras and permanent teammate tracking](docs/milestone-g-prompt.md)
- [Controlled launch/capture/restore tooling](docs/runtime-tooling.md)

- [Current reconnaissance report](docs/recon-report.md)
- [Exact observed player hierarchy](docs/live-player-hierarchy.md)
- [Game architecture](docs/game-architecture.md)
- [Hook candidates and Harmony result](docs/hook-candidates.md)
- [Feasibility and remaining gaps](docs/multiplayer-feasibility.md)
- [Runtime/build fingerprint](docs/runtime-analysis.md)
- [Steam transport research](docs/steam-transport-feasibility.md)
- [LiteNetLib provenance and net46 adaptations](third_party/LiteNetLib/PROVENANCE.md)

## Build, test, deploy (PowerShell 7)

```powershell
# Existing ignored discovery/SDK tools; only needed on a fresh machine:
./tools/reverse/find-game.ps1
./tools/reverse/bootstrap-tools.ps1

./tools/build-multiplayer.ps1
./tools/test-multiplayer.ps1
./tools/test-transport-processes.ps1
./tools/test-transport-processes.ps1 -AbruptHostExit
./tools/test-transport-processes.ps1 -AbruptClientExit
./tools/test-transport-processes.ps1 -LatencyMilliseconds 50 -JitterMilliseconds 20 -LossPercent 5
./tools/test-transport-processes.ps1 -LatencyMilliseconds 100 -JitterMilliseconds 40 -LossPercent 10
./tools/test-transport-processes.ps1 -Mode world -LatencyMilliseconds 60 -JitterMilliseconds 180 -LossPercent 10
./tools/reverse/validate-recon.ps1
./tools/validate-multiplayer.ps1
# Close the game first. Adds only our files beneath BepInEx/plugins:
./tools/deploy-multiplayer.ps1
# Optional upgraded diagnostics (F9). No verification patch in normal operation:
./tools/reverse/deploy-diagnostics.ps1
```

The tests are a dependency-free executable test runner using real localhost UDP
in addition to pure protocol/state/interpolation checks. The process harness
uses synthetic poses and no game assets. Network sandbox authorization may be
needed for restore and localhost UDP; deployment needs game-folder write access.
`build-multiplayer.ps1 -NoRestore` uses cached build references.

## Use on two legitimate installations

The scene/travel and supported economy extension uses mod 0.6.6 / protocol 9; independent two-machine acceptance remains deferred. See [current acceptance](docs/scene-continuity-acceptance.md) and [scene flow](docs/scene-continuity.md). The historical [S1 report](docs/playability-stabilization-s1.md) remains evidence for its earlier slice. Host owns the supported MainScene + map descriptor; an initialized Client follows automatically. Native managed-peer follow/return fixtures passed; remaining gates are explicit. Press Esc to pause, then F8. Choose **Host** and
a UDP port on the first machine. On the second, enter the host's LAN IPv4/port
and **Join**. Resume and walk/turn. The other player's proxy should follow.
The panel displays session/handshake, IDs, transport counters/timeout, local and
remote readiness, proxy/buffer status, arrival statistics and sampled/rendered
poses. Telemetry text refreshes four times per second. **Disconnect**
clears the proxy. Supported scene transitions retain transport, wait for the scene barrier and rebuild the existing world baseline. Unsupported scene sets fail visibly.

Configuration is generated in BepInEx/config/local.thiefsim2.multiplayer.cfg.
Transform rate defaults to 20 Hz (1..60); interpolation delay is 100 ms (0..500).
Received jumps larger than 10 metres snap immediately; `Transforms.SnapDistance`
is configurable. `Visuals.HeightOffset` and `Visuals.YawOffset` affect only the
child VisualRoot, preserving replicated coordinates/yaw. The generated capsule
uses the local CharacterController's centre and height.
A fresh civilian hierarchy reuses the installed game's shared skins/materials
and privately sanitized runtime clips. The generated capsule remains a safe
fallback. No game controller, camera, audio, collider, inventory, save or AI
component is copied. `Visuals.AvatarId` selects the male (0) or female (1) identity
before connection; this task's visual acceptance uses the preferred male.
Crouch/directional/look semantics are retained with documented visual limitations.

LiteNetLib 1.3.5 is vendored under its MIT license, retained with source and
installed packages. Two documented net46 compatibility changes avoid runtime
NuGet dependencies. Game/Unity/BepInEx references are read-only and Private=false.
The independent test/harness target uses the pinned local .NET 9 SDK.
Current mod version is 0.6.13; both peers must match. Protocol is version 13, with explicit
version-1 rejection and preserved legacy codec semantics.

## Diagnostics and evidence

```powershell
./tools/reverse/collect-runtime.ps1
# Explicit optional harmless verification; closes/removes the detour immediately:
./tools/reverse/deploy-diagnostics.ps1 -VerifyRuntime
```

Diagnostics records bounded targeted NDJSON in ignored local storage. F9 requests
a capture; runtime scripts request player/avatar/interaction/inventory/AI/vehicle/
manager snapshots, optional exact root scopes and finite action/save observation.
Manual files rotate and stop emitting at 32 MiB; completion/truncation is explicit.
The complete actor capture contains 1,186 nodes. The complete NPC-root capture visits
13,017 objects. Normal tracing is off; opt-in traces never change method results.
An explicit avatar experiment constructs only fresh presentation components. The
backed-up dropped-crowbar fixture is an opt-in mutation test, separate from normal
capture. See the tooling runbook for launch isolation, collection and save restore.

`Diagnostics.InstanceName` labels multiplayer logs only. Detailed pose/arrival
logging is opt-in with `VerboseTransforms=true`, capped at one summary per
second; normal operation defaults to false. `DevelopmentControls=true` exposes
a labelled two-second adapter blackout without changing the genuine game actor.
It also enables bounded real-player semantic observations. That fixture does not
prove real scene/player destruction. Use the F8 audit
button to record proxy ownership, uniqueness, mesh/material validity and the
component allowlist and original local camera/listener identities. Disable
development controls after testing. See the avatar report for deterministic
game-host and game-client harness commands.

Machine paths, game binaries/assets/saves, raw logs, metadata indexes, targeted
decompilations, downloaded SDK/tools and build artifacts remain ignored. The
183 original DLL/EXE baseline hashes are unchanged. Never redistribute game
files or decompiled code. The local installation is not manifest-verified Steam;
no DRM/authentication/single-instance mechanism was bypassed.
