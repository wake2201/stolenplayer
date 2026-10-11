# Reconnaissance and Phase 1 report — 6 October 2026

Historical first-stage report. The later nine-launch investigation supersedes its
startup, 160-node hierarchy and avatar feasibility limits. See
[comprehensive runtime reconnaissance](full-runtime-recon.md). Production two-game
acceptance remains blocked; the successful civilian visual is an offline diagnostic
experiment, not a changed production network proxy.

**Result:** gameplay evidence closes the player/ownership/visual-source gaps;
Harmony detours and Unity/net46 LiteNetLib loading are verified. Phase 1 source
is implemented, built and automated transport verified. Final plugin gameplay
smoke/visual acceptance and a real two-game test remain unverified.

## Gameplay findings

The supplied capture `recon-20261006-112054-5e8ec02f.ndjson` contains eight real
PlayerScript snapshots over two MainScene lifetimes. Root is
`IMPORTANT OBJECTS[0]/First-Person Character[19]`; PlayerScript, motor, camera
behaviour and CharacterController all reside there. [Exact observed hierarchy](live-player-hierarchy.md)
records the root's components, all 47 immediate children and relevant cameras,
AudioListener, audio/physics/sensors, hands and hidden-body branches. The old
capture is capped at 160 nodes, so deep hand/animation bones remain incomplete.
The diagnostics limit is now 4,096 with explicit truncation status.

Runtime CC/PMB/PCB bindings resolve to that root; GameManager.Player points to
it. Walking changed position from (-218.345,85.196,101.553) through
(-219.701,85.203,101.675); the later player moved to (-229.841,84.679,88.639).
Inspected motor/look Update methods write root movement/yaw and camera-local
pitch. Final sampling uses our LateUpdate at execution order 10000; only world
position/Euler Y is sent. Numeric camera/yaw sampling in the upgraded diagnostics
and execution of the final adapter remain pending.

A complete hidden body source exists beneath `LEGS/Main Character Legs`:
Body/Tops/Gloves/Eyes/Eyelashes/Hats inactive, Bottoms/Shoes active. Animator,
GrounderIK/LimbIK and LegsController belong to the local game actor. Vehicle
TPP Character models also exist with Animation/CheckItemEnable; the latter
reads the local inventory. Preferred remote strategy is new baked meshes and
MeshFilter/MeshRenderer objects, with original generated capsule fallback.
No original hierarchy is cloned or redistributed. Final baking/rendering is
not yet runtime-verified.

MainScene loaded Single at 11:22:17 UTC; IntroHouse loaded Additive at 11:22:20
UTC, with MainScene active. Both unloaded at 11:23:24 UTC and reloaded. PlayerScript
instance 316802 became 583394; GameManager 308524 became 575116. Both are scene
owned for this observed reload. The reload trigger was not recorded. Mod session
uses DontDestroyOnLoad and disconnects/destroys proxies at any scene transition;
world travel synchronization is excluded.

## Runtime compatibility

BepInEx 5.4.23.5 x64, Unity 2020.3.33f1 Mono CLR 4.0.30319.17020 are unchanged.
The harmless temporary patch against inspected empty LootBox.Start executed
Prefix/Postfix once each, then UnpatchSelf removed it. Evidence includes
harmony_detour_verified and harmony_verification_removed in the ignored
125807/130053 verification logs. This proves detouring despite Supports SRE=False.
Normal diagnostics disable this optional verification; Phase 1 needs no patches.

Pinned LiteNetLib 1.3.5 initially needed unavailable newer platform/tuple APIs.
Microsoft compatibility packages failed at runtime on a missing BCL facade;
1.2.0 has the same platform requirement and 0.9.5.2 has net35 support. A minimal
net46-only platform helper and equivalent temporary-variable tuple swap retain
1.3.5 without runtime packages. A reliable loopback packet was received inside
the actual Unity/BepInEx process. Source provenance and full MIT attribution
are retained in third_party. No custom UDP implementation was written.

## Phase 1 implementation

Separate BepInEx plugin + pure core + pinned transport source + automated tests
and a transport harness. Protocol v1 has compact bounded binary request/accept/
reject, scene readiness, 30-byte position/yaw/sequence/tick/epoch, and disconnect
data. Host controls capacity/IDs 1 and 2; client waits for validated application
acceptance. Explicit Offline/StartingHost/Hosting/Connecting/Connected/
Disconnecting states prevent invalid actions. Both peers send at configurable
20 Hz and interpolate a bounded 32-sample buffer at 100 ms delay using shortest
arc yaw. Stale/unready/mismatched-scene visuals are hidden; disconnect clears
membership/buffer/proxy, returning host to Hosting or client to Offline.

F8 Host/IP/Port/Join/Disconnect UI displays IDs/state/ping/error. F9 diagnostics
avoids a hotkey collision. New source is in src/ThiefSim2Multiplayer and
src/ThiefSim2Multiplayer.Core, tests in tests/ThiefSim2Multiplayer.Tests, and
standalone harness in tools/TransportHarness. Build/deploy/test/validation
scripts and four recon documents, README, hierarchy and implementation notes
were updated. No future-phase features were added.

## Validation and practical limits

- AUTOMATED VERIFIED: all projects built, zero warnings/errors; 58 checks passed,
  including 10,000 malformed packets and real UDP handshake/poses/rejection/
  readiness/disconnect/reconnect/host exit.
- AUTOMATED VERIFIED: separate host/client process pair exchanged synthetic
  transforms (113 sent, 111 sampled on each side in the recorded run).
- AUTOMATED VERIFIED: abrupt termination of the harness host produced a client
  peer timeout and safe Offline cleanup, without disconnect data.
- SINGLE-INSTANCE VERIFIED: diagnostics loading, empty-method Harmony detour
  and pinned Unity/net46 transport loopback.
- FINAL MULTIPLAYER PLUGIN SINGLE-INSTANCE UNVERIFIED: final startup, F8 GUI,
  Host button and in-world visual geometry have not been observed.
- TWO-INSTANCE UNVERIFIED: no real pair of games was tested; process harness
  results are not gameplay acceptance.

Computer Use was stopped with physical Escape during follow-up capture; no
further game control was performed. No DRM/authentication/single-instance bypass
or Steam binary changes were attempted. Use another legitimate LAN installation
for the next acceptance test. The local discovery remains a non-manifest Steam
fallback; this does not certify official Steam networking.

All 183 original DLL/EXE baseline hashes remain unchanged. Machine-specific
paths, proprietary DLLs, decompiled output, raw logs and build artifacts stay
ignored; original game files are not overwritten. Repeatable validators check
script parsing, forbidden tracking, dependency/output allowlists, source
provenance, reports and hashes. A Phase 1 commit is permitted only after final
validation and git diff --check pass.

**Next recommended phase:** finish Phase 1 gameplay acceptance on two legitimate
installations, including UI, baked/fallback visibility, walk/yaw, abrupt timeout,
reconnect and scene safety. Do not begin world/interaction synchronization yet.
Detailed procedure: [Phase 1 implementation](phase-1-implementation.md).
