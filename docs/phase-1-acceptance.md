> S1 update: S1 supersedes the historical disconnect/reconnect scene policy below. Implementation is READY FOR MANUAL ACCEPTANCE, not ACCEPTED. Pure/UDP scene tests do not prove native automatic client loading or visual grounding/driver visibility; measured F8 game performance and one human prepare/capture/collect cycle remain required. Milestone F is BLOCKED. See playability-stabilization-s1.md.

# Phase 1 acceptance — updated 7 October 2026

**TWO-GAME ACCEPTANCE: DEFERRED.** This is an explicit separate project gate.
**Milestone B: ACCEPTED — TWO-GAME VALIDATION DEFERRED.**
Milestone B adds mod 0.3.0/protocol 2 and a production civilian presentation;
its separate game+harness acceptance and limitations are documented in
[milestone-b-acceptance.md](milestone-b-acceptance.md) and [network-avatar.md](network-avatar.md).
Both game-host and game-client semantic sequences have runtime evidence; current
finalization adds sustained original-motor fixture input, actual native tool use,
outgoing real equipment, ten-cycle stress and medium soak. This updates earlier
game/proxy UNKNOWN entries for those fixtures, without accepting two real games.
Current automation is 145 checks; all prior 129 checks are retained; sixteen focused regressions were added. Original binary
fingerprints remain unchanged in the historical B acceptance.
Milestone C adds generic world infrastructure in mod 0.4.0 / protocol 3 while
retaining the accepted avatar. Its current checks and bounded game regression are
recorded in [milestone-c-acceptance.md](milestone-c-acceptance.md). No real game
system, save or travel synchronization is claimed; the two-game gate remains deferred.

The future [two-game procedure](two-game-acceptance-plan.md) first attempts two
normal legitimate local processes over localhost with memory/commit monitoring,
then separate legitimate PCs if unsupported or unstable. It never bypasses DRM,
ownership or Steam authentication. Historical unverified entries below describe
earlier fixtures; they do not override the current DEFERRED classification.

## Historical Phase 1 evidence (mod 0.2.1 / protocol 1)

**Historical decision: NOT ACCEPTED at that stage.** The user confirmed
that no second legitimate installation is available. Networking checks pass,
but they cannot substitute for two-game movement/visual acceptance. No Phase 2
feature was implemented or recommended for immediate work.

## Environment and evidence

One local Windows x64 installation, Unity 2020.3.33f1 Mono CLR 4.0.30319.17020,
BepInEx 5.4.23.5. Multiplayer mod 0.2.1, protocol 1; game fingerprint remains
`3786E09344BD2CC72E599EBC2468F10C04BAEC58A9279A43B70FB56F79A9EA50`.
No second machine/account was used. Planned game joining is LAN IPv4; actual
transport runs used loopback IPv4, ephemeral UDP ports and the same 0.2.1 core
on host/client desktop harnesses. These are synthetic peers without game assets.

Later runtime update: ten launch sessions tested vanilla/Bep-only/diagnostics/
multiplayer/both, including a final menu-only metadata check. Six later launches
reached gameplay and closed under control.
F8 UI and current adapter acquisition are VERIFIED_RUNTIME; R9 actual menu/reload
logs show player availability false then reacquisition true, with PS and GM recreated.
The offline NPC visual/ground/idle audit passed. It does not accept the production
remote proxy or two-game movement. Startup pressure/crash details and new capture
keys are in [full-runtime-recon.md](full-runtime-recon.md). Earlier failure evidence
and impairment measurements below remain historical, not the latest startup verdict.

Ignored evidence: `.local/evidence/acceptance/automated-tests.log`,
`game-startup-20261006.log`, `game-playerlog-oom-20261006.log`,
and `.local/evidence/transport/*`. Raw logs and machine paths are not committed.

Multiplayer loaded at **14:01:25.240836 UTC**, reporting main thread **1**.
Chainloader startup completed; the normal game menu was visually observed.
No TS2MP integration exception was logged. The game exited at approximately
14:02:11 UTC. Its Player.log reports **System out of memory**, attempting a
**70,715,376-byte FMODSample** allocation. No player was acquired and no peer or
proxy existed in this run. Attribution to a specific root cause is not proven;
there was no control run with the plugin removed. Later OS readings showed
about 4.0 GiB free physical memory and 2.6 GiB free commit capacity, which are
post-crash readings, not measurements at the failing allocation.

A subsequent normal startup retry was stopped through physical Escape by the
user. Game automation stopped immediately; no later app input or launch occurred.
No synthetic peer was connected to a running game.
Startup verification applies to the first 0.2.1 candidate. The final rebuilt
and redeployed binaries have not received a separate game smoke test.

## Acceptance matrix

| Requirement | Status | Evidence / limit |
|---|---|---|
| Plugin startup | VERIFIED | Local game chainloader, mod 0.2.1, thread 1 |
| F8 UI / gameplay Host startup | UI VERIFIED_RUNTIME; Host UNKNOWN | R4 F8 opens/closes; no actual peer/game Host acceptance |
| Genuine player acquisition | VERIFIED_RUNTIME for current adapter | R4 and R9 acquisition logs; R9 real reload reacquires |
| Client joins / handshake | VERIFIED in harness; UNVERIFIED in game | Reliable compatibility checks and one-client cap |
| Host/client IDs | VERIFIED in harness | Host 1, client 2, fresh session per reconnect |
| Exactly one proxy / component audit | UNVERIFIED in game | Renderer-only code allowlist and explicit runtime audit added |
| Runtime visual strategy actually used | UNVERIFIED | Preferred baked body + capsule fallback; neither spawned in game |
| Bidirectional movement / yaw | UNVERIFIED in game | Synthetic wire poses and shortest-arc yaw verified separately |
| Feet/origin/model-axis alignment | UNVERIFIED | NetworkRoot/VisualRoot separation and controller-based capsule added |
| Stable game interpolation / drift | UNVERIFIED | Desktop linear timeline did not rewind under impairment |
| Snapshot arrival statistics | VERIFIED for harness only | Measurements below; no gameplay arrivals |
| Normal client/host disconnect | VERIFIED in harness; UNVERIFIED in game | Membership/buffer cleanup; dropped close resolves by 5 s timeout |
| Abrupt client exit | VERIFIED in process harness | Host logs Timeout and returns Hosting |
| Abrupt host exit | VERIFIED in process harness | Client logs Timeout and returns Offline |
| Reconnect | VERIFIED in harness; UNVERIFIED in game | Three cycles, fresh session IDs, no stale buffer |
| Temporary local-player reacquisition | VERIFIED_RUNTIME offline reload and bounded connected adapter fixture | R9 availability false then true; Milestone C client returns to WorldReady after the two-second adapter fixture |
| Scene transition | VERIFIED_RUNTIME offline callbacks and populated connected teardown | Milestone C real menu/reload removes the peer/proxy and clears the host registry; synchronized travel remains UNKNOWN and unimplemented |
| Thread safety | VERIFIED by review and desktop guard test | Poll dispatch, all unsynced flags false, owner-thread enforcement |
| Codec allocations | VERIFIED on .NET 9 | 10,000 reused transform encode/decode pairs allocated zero managed bytes |
| Whole game allocations / long session soak | UNVERIFIED | No Mono profiler or sustained gameplay run |
| Local gameplay smoke run | Six later runs VERIFIED_RUNTIME | Earlier FMOD failure and vanilla/Bep allocation/native failures remain reliability evidence |
| Two real game installations | DEFERRED | Separate current project gate; no second installation used in these historical fixtures |

## Arrival and impairment measurements

Independent host/client processes used a third loopback UDP relay. One-way delay
is sampled uniformly around the configured latency; jitter is the peak-to-peak
span (20 ms means +/-10 ms), not measured standard deviation. A seeded 5%/10%
drop decision affects datagrams in both directions, including handshake and
reliable control traffic. Delivery loss is tolerated by LiteNetLib sequencing
and retransmission. The test relay pins one client, caps datagrams at 4 KiB,
queues at 512 and processes at most 128 receives/sends per loop. Its thread
handles only bytes/endpoints and is never deployed into Unity.

| Link conditions | Receiver | Accepted | Mean interval ms | Min | Max | Jitter stddev ms | Sequence gaps |
|---|---|---:|---:|---:|---:|---:|---:|
| 50 ms / 20 ms span / 5% loss | Host | 108 | 54.1 | 29.0 | 127.7 | 15.3 | 3 |
| 50 ms / 20 ms span / 5% loss | Client | 108 | 53.1 | 29.0 | 116.9 | 12.5 | 1 |
| 100 ms / 40 ms span / 10% loss | Host | 101 | 56.4 | 5.9 | 186.1 | 26.8 | 8 |
| 100 ms / 40 ms span / 10% loss | Client | 95 | 58.1 | 11.9 | 141.1 | 25.1 | 10 |

The relay recorded 256 datagrams/5 dropped and 262/20 dropped respectively,
zero queue overflow and no relay error. Configured probabilities do not imply
an exact percentage in a short run. Sequence gaps describe missing accepted
application sequence numbers, not an exact UDP loss rate. Statistics use the
last 128 accepted arrival intervals, population standard deviation, and local
monotonic arrival time. Unity main-thread scheduling is not represented here.

The 100 ms interpolation delay remains unchanged. It covers ordinary cadence
in these fixtures; longer loss gaps cause holding at the last pose, then stale
hiding after one second. No prediction/extrapolation is added. Deterministic
linear-movement tests checked rendered position did not regress. These short
desktop measurements do not establish visual smoothness or optimal delay in-game.

LiteNetLib's built-in combined latency/loss simulation was reviewed and rejected
for this acceptance setup: incoming packets are queued for latency before its
loss flag is set, so queued packets still arrive. Earlier built-in measurements
are superseded by the relay results above. Upstream source is unchanged.

## Hardening and validation

- Bounded F8 telemetry and explicit proxy audit; UTC/instance-labelled logs.
  Detailed transforms default off and are capped at one summary per second.
- Fixed 30-byte transform codec writes into reusable storage and reads the
  pooled packet slice. No per-transform stream/reader/Message/copy allocations.
  LiteNetLib pooling can still allocate when pools grow; no zero-GC game claim.
- Configurable 10 m jump snap, immediate first/spawn pose, preserved sequence
  guard, shortest-path yaw. Explicit N/N+1/N/N-1 and wrap cases pass.
- VisualRoot offsets preserve true network coordinates/yaw. Capsule origin uses
  controller centre/height. Baked meshes validate materials and own cleanup.
- Adapter discovery retries unavailable cached players/controllers at most
  twice per second, including an inactive old player that has not yet been
  destroyed. It ignores disabled controllers; R9 genuine offline scene recreation
  now verifies reacquisition. Connected peer/proxy recovery remains unverified.
  The old cache only retried when the cached player was destroyed.
- Explicit main-thread transport contract; callback tests pass even under loss.
- **93 automated checks pass** (35 more than baseline 58), including 20,000
  malformed packets/slices, pooled offsets, handshake-state/identity/channel
  rejection, reconnects, snaps, thread guard and impaired interpolation.
- All projects build with zero warnings/errors. Independent graceful/abrupt
  process runs pass. Validators check original hashes, provenance, license,
  forbidden tracking, machine paths and whitespace before commit.

## Remaining acceptance procedure

Resolve the local game's startup/memory failure and complete a stable local
smoke test. Capture F8 acquisition/Host, then test two legitimate installations
in the same area. Stand for 3 s, walk about 5 m, stop, rotate 90 degrees, walk
another direction, stop; repeat with the other peer moving. Audit one proxy
per side, root/visual coordinates, feet and model yaw, camera/input/audio, arrival
statistics, disconnect/timeout, at least two reconnects, real player loss and
scene transition/manual reconnect. Record both timestamped instance-labelled
logs. The optional adapter blackout is a fixture, not proof of actual scene
recreation. Do not start Phase 2 until these critical in-game checks pass.
