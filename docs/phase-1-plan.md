# Phase 1 plan: Host/Join and a remote visual

**Original plan; implementation now exists.** Start gates passed and the source/automated transport are verified. The current [implementation](phase-1-implementation.md) is authoritative for protocol and status; final gameplay acceptance remains pending. Scope is exactly two players and a pose-only proof; no AI/police, inventory/loot, vehicles, missions, saving/progression, synchronized travel, host migration or full animation synchronization.

## Entry gates

1. Obtain one automatic gameplay capture: actual PlayerScript component/hierarchy, motor, CharacterController, camera ownership and world transform. Confirm finite poses and local adapter readiness after loading.
2. Observe one gameplay scene transition and whether the player is recreated. Determine world scene identity separately from additive minigames.
3. Confirm a visual-only proxy design and rendering layer/scale. A mod-owned capsule with a facing indicator is sufficient; reuse of game art is not a prerequisite.
4. Compare game assembly fingerprints on both computers and require compatible mod/protocol versions. Native Steam compatibility is not a gate for a LAN LiteNetLib prototype.
5. Source-build the pinned LiteNetLib 1.3.5 Unity-compatible target and run a separate game load/API check. Decide plugin/core target framework from this evidence.
6. If any integration patch is required, verify one observational Harmony postfix on a nonmutating finite method and inspect backend/errors before adding others. Event subscriptions and pose sampling may avoid patches entirely in Phase 1.

## Implementation sequence and acceptance

| Step | Work | Acceptance criterion |
|---|---|---|
| 1 | Create MultiplayerPlugin with explicit version/fingerprint checks | BepInEx loads it cleanly alongside diagnostics; no reference DLLs are copied into the release |
| 2 | Add small Host/Join/Disconnect debug UI | UI visible in a gameplay scene, reports session state and errors, and releases input focus when closed |
| 3 | Implement host listener behind NetworkTransport | Player A hosts a configured LAN port; one remote slot; bind failures are displayed |
| 4 | Implement client connection and bounded timeouts | Player B connects by address; failed connection returns to Offline |
| 5 | Implement reliable protocol handshake | Hello carries magic, protocol/mod version, assembly fingerprint and scene identity; host returns SessionId, assigned ID and accepted scene epoch |
| 6 | Create host/client membership events | Host detects one client and rejects a second; no duplicate member events |
| 7 | Establish mutual peer awareness | Client learns host identity; both report handshake success before any proxy spawn |
| 8 | Assign stable session player IDs | Host=1 and accepted client=2; repeated messages cannot change membership ID |
| 9 | Bind the verified local adapter and spawn one RemotePlayer per peer | One mod-owned noncolliding visual; no extra camera, AudioListener, Rewired player, PlayerScript, save state or game controller |
| 10 | Send position and yaw (optionally pitch field reserved) | Each peer sees the other's position/facing; samples include player ID, epoch, sequence and timestamp |
| 11 | Buffer/interpolate accepted remote poses | At normal movement rates visual motion is smooth; dropped/out-of-order packets and yaw wrap are handled; stale samples/teleports are bounded |
| 12 | Handle disconnect and scene invalidation | Clean disconnect or timeout removes remote visual, clears buffers and returns to Offline; reconnect does not duplicate objects |

Host validates client pose messages before acceptance. No server-side movement re-simulation is claimed. Keep control/handshake messages ReliableOrdered and transient poses Sequenced/unreliable; cap packet size, input rates and messages processed per frame. SessionEpoch/SceneEpoch and membership checks prevent stale packets from previous loads/disconnects from spawning visuals.

## Validation matrix

Run unit tests on pure protocol parsing/state transitions with malformed lengths, incompatible versions, duplicate handshakes, extra-client rejection, invalid IDs, nonfinite values, sequence wrap and stale epochs. Test interpolation with yaw 359->1 degrees, jitter/reordering, teleport and bounded extrapolation. These verify independent logic rather than mirroring game methods.

First end-to-end tests require two game processes on two computers with the same analyzed build (or a deliberately validated newer one), same gameplay scene and local adapters ready. A standalone network harness can precede this but does not substitute for two in-game visual captures.

Exercise host/join, move/rotate, crouch pose height, pause/UI focus, one clean disconnect, network timeout, reconnect, wrong build/scene, and second-client rejection. Use simulated packet loss/latency to verify interpolation. Scene travel either disconnects cleanly or uses a later explicitly implemented ready barrier; it must not silently bind stale transforms.

Inspect loader/plugin errors and new original-binary hash changes. Check that cameras/input stay with the local player and no remote visual enters saves or AI collision/perception unexpectedly. Ordinary base-game state remains independent between peers; this acceptance test proves remote visuals, not synchronized cooperative world gameplay.

## Exit criteria

All twelve requested prototype behaviors pass with evidence from both peers. No proprietary binaries/assets/decompilations are tracked or shipped. Known world-state limitations are documented. Only then propose a separate phase for host-owned interactions/registry, followed by inventory, AI, vehicles and shared progression/save policy.
