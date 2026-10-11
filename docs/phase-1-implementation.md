# Phase 1 implementation — 6 October 2026

Status: **implementation built and automated transport verified; gameplay acceptance pending**.
Current hardening release: **0.2.1**, protocol **1**. Current evidence and
VERIFIED/FAILED/UNVERIFIED/BLOCKED distinctions are in
[Phase 1 acceptance](phase-1-acceptance.md). Phase 1 is **not accepted**.
Scope is exactly two peers exchanging position/yaw and creating a visual-only
representation. No world state, interactions, inventory, AI, vehicles, saves,
progression, chat, Steam networking or host migration is synchronized.

## Architecture and gates

- `ThiefSim2Multiplayer` is the separate net46 BepInEx plugin. Its only game
  adapter resolves the real PlayerScript by exact assembly/type in MainScene,
  requires the motor/camera/controller on the same object and refuses ambiguity.
- `ThiefSim2Multiplayer.Core` contains transport, binary protocol, membership,
  state transitions and interpolation without any Unity/game reference.
- `third_party/LiteNetLib` is pinned 1.3.5 source with two small net46 adaptations
  described in [provenance](../third_party/LiteNetLib/PROVENANCE.md).
- Diagnostics remains a separate plugin. F9 captures diagnostics; F8 opens
  multiplayer. The opt-in detour verification unpatches itself immediately.
- net9.0 tests and TransportHarness use the same core sources and upstream
  transport sources without Unity platform flags; the game uses net46 with
  Unity flags. Passing desktop tests alone does not prove Unity integration.

The Phase 1 start gates passed: runtime PlayerScript/CC/motor/camera binding,
root movement, a safe renderer-only representation with generated fallback,
stable BepInEx diagnostics loading, and an in-game LiteNetLib reliable loopback
packet. Harmony Prefix/Postfix also passed, although Phase 1 needs no detours.

## Transport compatibility and redistribution

LiteNetLib 1.3.5's upstream binaries target net471/netstandard2.0 or newer. Its
source initially failed net46 compilation on RuntimeInformation and a tuple
swap. Microsoft compatibility packages compiled, but in-game platform detection
failed on a missing System.Resources.ResourceManager facade. Version 1.2.0 was
inspected and has the same platform API requirement; 0.9.5.2 has a net35 target.

The selected 1.3.5 build uses a net46-only Environment.OSVersion platform helper
and an equivalent temporary-variable queue swap. The supported game target is
Windows x64; cross-platform certification is outside this prototype. Upstream
Unity socket pause handling remains enabled. Native sockets are disabled in
our session adapter. The resulting library exchanged reliable packets inside
Unity 2020.3.33f1/BepInEx 5.4.23.5/Mono CLR 4.0.30319.17020. There are **no runtime
NuGet/transitive dependencies**; framework reference packages are build-only.

LiteNetLib is MIT licensed. Source and deployed packages retain the complete
Ruslan Pyrch copyright/license. No game DLL, model, material, save, or decompiled
output is redistributed. Game/Unity/BepInEx assemblies are Private=false build
references. Deployment contains only our two multiplayer DLLs, LiteNetLib and
its license; diagnostics is deployed separately.

## Protocol version 1

Application packets are little-endian binary, bounded to 512 bytes. Strings use
a UInt16 byte length and strict UTF-8; controls, invalid encodings, truncation,
trailing bytes, unknown types and oversize values are rejected. There is no
object deserialization, JSON or BinaryFormatter.

| Type | Fields | Delivery |
|---|---|---|
| 1 HandshakeRequest | UInt16 protocol, mod version (32 bytes), name (48), Assembly-CSharp SHA-256 (64) | LiteNetLib connection data, retransmitted until answered |
| 2 HandshakeAccepted | Same compatibility/name fields, client ID 2, host ID 1, nonzero UInt64 session token | Channel 0 ReliableOrdered |
| 3 HandshakeRejected | Bounded reason (120 bytes) | LiteNetLib rejection data |
| 4 PlayerTransform | Byte player ID; UInt32 scene epoch, sequence, sender monotonic millisecond tick; float32 X/Y/Z/yaw | Channel 1 Sequenced, 30 bytes |
| 5 SceneState | Boolean ready, UInt32 scene epoch, sorted loaded-scene key (160 bytes) | Channel 0 ReliableOrdered |
| 6 PlayerLeft | Bounded reason | LiteNetLib disconnect data |

Host rejects mismatched protocol, mod version or game build, duplicate/full
sessions and malformed connection data before accepting the transport peer.
HandshakeAccepted assigns both IDs and host session token; both sides then
exchange readiness. Transport connection identity isolates previous sessions.
The pose scene epoch and sequence reject stale state within that connection.

The host owns membership/capacity. Each peer reports its own transform; there
is no authoritative movement simulation or anti-cheat claim. Incoming poses
must identify the sole accepted remote player, match scene epoch and delivery
channel/mode, contain finite coordinates within +/-100,000 and yaw in [0,360).
Callbacks are polled on Unity's main thread, capped at 128 events per frame.
All unsynchronized LiteNetLib callback flags are explicitly disabled. The
NetworkManager records its creating thread and rejects mutating calls/Poll
from another thread before any callback. The desktop relay's worker handles
only UDP bytes; it never references Unity objects.
Accepted transform processing is capped at 100 per second. Name/build matching
is compatibility validation, not authentication; use a trusted LAN for testing.

## Session state and cleanup

`Offline -> StartingHost -> Hosting -> Connected` for host;
`Offline -> Connecting -> Connected` for client. Explicit disconnect passes
through Disconnecting to Offline. Host returns to Hosting when only its client
leaves. Client returns to Offline when host leaves or times out. Invalid host,
join and mode-change actions are blocked. Connection/handshake deadline is 10
seconds; LiteNetLib peer timeout is 5 seconds. Membership and interpolation
buffers clear on disconnect. The Unity proxy subscribes to the cleanup event.

F8 debug UI offers Host, IPv4/port Join, Disconnect, name, state, IDs, ping,
scene readiness and the last error. Pause with Esc first to avoid game input
while operating the panel. It restores cursor state when closed; it does not
modify original menu assets or game input maps.
It now adds handshake/remote-mod state, actual transport packet counters,
last transport receive age and five-second timeout context, adapter/proxy
availability, scene keys, buffer/sequence/gaps, latest target/rendered pose and
128-interval arrival statistics. Text is cached at 4 Hz. InstanceName is a
sanitized local log label only; logs have UTC timestamps. Pose summaries are
opt-in and capped at 1 Hz. A dropped close datagram may require the existing
five-second peer timeout; cleanup remains bounded.

## Local pose and remote visuals

Sample world position and world yaw from the actual PlayerScript transform in
our LateUpdate (`DefaultExecutionOrder(10000)`). Runtime CC/PMB/PCB references
all point to that object. Movement and mouse look run in game Update; camera
headbob/lean/pitch belong to children. Pitch is not sent.

Preferred strategy A: bake Body, Bottoms, Tops, Gloves, Shoes, Eyes and Eyelashes
from the observed `LEGS/Main Character Legs` SkinnedMeshRenderers into fresh
Meshes. Convert vertices into the local player root's coordinate space. New
objects contain only Transform/MeshFilter/MeshRenderer; materials are referenced
in-process without changing assets. No active/inactive game hierarchy is cloned.
There is no Animator, Animation, IK, game controller, camera, audio, collider,
input, inventory, interaction, save logic or AI in the proxy. It is Untagged on
the Default world rendering layer and named `TS2MP_RemotePlayer_1/2`.

If expected meshes are absent or cannot be baked, create original capsule
geometry with the same renderer-only contract. An original colored forward
marker identifies yaw. Exactly one proxy is created lazily after a valid pose
and matching readiness. Remote body is a static sampled pose: animation,
equipment and crouch state are not synchronized. Runtime material/scale/camera
visibility of the final proxy remains unverified. Shared game assets never enter
the repository or build output.
NetworkRoot owns replicated position/yaw. A child VisualRoot owns configurable
height/yaw corrections, default zero; these never alter protocol values. Capsule
feet are `controller.center.y - controller.height/2`. Baking validates mesh and
material references, and owns all baked meshes before renderer creation so a
partial failure can clean them up. Spawn and explicit UI audits report scene,
renderer/mesh/material counts, forbidden-component count and local bounds.
Disposed roots are immediately hidden and renamed before Unity's deferred
Destroy, preventing a rapid reconnect audit from counting a retired visual.

Default send cadence is 20 Hz, configurable/clamped to 1..60. A frame sends at
most one pose and never catches up in a burst. No sends occur before local
availability and mutual matching scene readiness. Incoming samples use local
arrival times, a 32-entry buffer and default 100 ms delay (configurable 0..500).
Position interpolates linearly, yaw uses the shortest arc. There is no prediction
or extrapolation. A pose older than one second is hidden. Sender tick is carried
for diagnostics/future timing; clocks are not assumed synchronized.
First/spawn snapshots snap immediately. A received positional discontinuity
greater than the configurable 10 m threshold clears interpolation history and
snaps immediately while retaining the sequence guard. This avoids smoothing
major relocations through the intervening world. No prediction is added.
The transform send path reuses one 30-byte buffer. Receive decoding reads the
pooled packet slice before recycling; no byte-array copy, Message, stream or
reader is allocated for transforms. Control messages remain bounded and use
the general codec. The .NET test measured zero managed allocations for 10,000
transform codec pairs; whole-game/Mono allocation profiling remains unverified.

## Scenes and loading

The supplied gameplay capture observed MainScene Single + IntroHouse Additive,
then both unloading and recreating the player and GameManager. Our plugin is
DontDestroyOnLoad. Any scene load/unload/active-scene change destroys the proxy,
invalidates the adapter and disconnects an active session. Reconnect manually
after both peers load the same area. No synchronized travel is implemented.

Temporary player/controller disappearance stops sends, advertises not-ready,
clears old poses and hides the existing proxy. The adapter reacquires a missing
player at most twice per second. Availability recovery resumes readiness;
the proxy is reused without repeated instantiation. A differing loaded-scene
key suppresses pose traffic and visibility.

## Validation performed

- **AUTOMATED VERIFIED:** all projects build with zero warnings/errors.
- **AUTOMATED VERIFIED:** 93 checks covering all message roundtrips, every
  truncation, invalid types/IDs/epochs/floats, 10,000 random packets, compatibility,
  state transitions, sequence wrap, yaw interpolation, buffer limits/staleness,
  real UDP bidirectional poses, empty wire handshake rejection, capacity, scene readiness/mismatch, rejection,
  disconnect cleanup, three reconnect cycles, handshake cancellation, thread
  affinity, pooled slices, first/jump snaps, zero-allocation codec and host exit.
- **AUTOMATED VERIFIED:** two independent .NET transport processes completed
  a handshake and each sent 113/sampled 111 synthetic updates in the recorded run.
  This is a transport test, not two running games.
- **AUTOMATED VERIFIED:** a separate harness run killed its own host process;
  the client detected the 5-second timeout and returned safely to Offline.
- **AUTOMATED VERIFIED:** abrupt harness client loss returns host to Hosting.
- **AUTOMATED VERIFIED:** real bidirectional datagram impairment at 50 ms
  latency/20 ms jitter span/5% loss and 100 ms/40 ms/10% loss, including control
  traffic. Sequence/interpolation did not rewind in the deterministic movement
  fixture. See acceptance for measured interval statistics and limits.
- **DEPLOYMENT VERIFIED:** rebuilt multiplayer/core/transport DLLs and the MIT
  license were installed under BepInEx/plugins with the game closed. Installed
  DLL hashes match build outputs; original game binary hashes remain unchanged.
  Updated diagnostics was deployed with runtime verification disabled.
- **SINGLE-INSTANCE VERIFIED:** diagnostics plugin, harmless Harmony detour
  (then unpatched), and pinned Unity/net46 LiteNetLib reliable loopback.
- **SINGLE-INSTANCE VERIFIED:** multiplayer 0.2.1 plugin loaded on main thread 1
  at 14:01:25 UTC. **FAILED:** the smoke run crashed before F8/gameplay checks
  with an out-of-memory FMODSample allocation in the game log. A retry was
  stopped by physical Escape; no further Computer Use was performed.
- **TWO-GAME ACCEPTANCE BLOCKED:** user confirmed no second installation.
  F8, gameplay hosting/joining, actual proxy rendering, movement/yaw,
  reacquisition and scene transition remain UNVERIFIED in the game.

The original gameplay hierarchy capture has a 160-node breadth-first cap; all
root children and important cameras/body were observed, but deep hand bones
were not exhaustively captured. Diagnostics now supports 4,096 nodes with an
explicit truncation record. A complete expanded follow-up remains pending.

## Development and next acceptance test

```powershell
./tools/build-multiplayer.ps1
./tools/test-multiplayer.ps1
./tools/test-transport-processes.ps1
./tools/test-transport-processes.ps1 -AbruptHostExit
./tools/test-transport-processes.ps1 -AbruptClientExit
./tools/test-transport-processes.ps1 -LatencyMilliseconds 50 -JitterMilliseconds 20 -LossPercent 5
./tools/test-transport-processes.ps1 -LatencyMilliseconds 100 -JitterMilliseconds 40 -LossPercent 10
./tools/reverse/validate-recon.ps1
./tools/validate-multiplayer.ps1
./tools/deploy-multiplayer.ps1
```

The existing ignored discovery/tooling supplies ManagedDir and SDK. Network
access is needed on first restore; constrained execution may need authorization
for localhost UDP and game-folder deployment. `-NoRestore` builds from cached
references. Deployment refuses while the game is running and verifies original
binary hashes before/after copying only our BepInEx plugin files.

Use two legitimately obtained installations with matching game/mod builds.
Load the same area, press Esc then F8. Host chooses UDP port; client enters the
host's LAN IPv4/port and Join. Resume gameplay and walk/turn in each instance;
confirm the other visual follows position/yaw. Confirm one proxy, input/audio
unaffected, graceful and abrupt disconnect, timeout, reconnect, rejection of
a third peer, wrong protocol/build, and safe disconnect on scene reload. Capture
logs and verify visibility on both sides. For transport-only checks, run:

```powershell
# Separate terminals, synthetic scene identity, no game assets needed:
./.local/dotnet/dotnet.exe tools/TransportHarness/bin/Release/net9.0/TransportHarness.dll host 9050 test-build 30
./.local/dotnet/dotnet.exe tools/TransportHarness/bin/Release/net9.0/TransportHarness.dll client 127.0.0.1 9050 test-build 25
```

No Steam/single-instance restriction was bypassed and no second game launch
was attempted for a concurrent pair. Another legitimate LAN installation is
the recommended route. **Next phase: finish Phase 1 gameplay acceptance on two
legitimate installations before beginning world/interaction synchronization.**
