# Milestone D acceptance evidence

Date: 2026-10-07. Mod 0.5.0 / protocol 4.

**ACCEPTED — TWO-GAME VALIDATION DEFERRED.**
**TWO-GAME ACCEPTANCE = DEFERRED.**
Accepted predecessor: Milestone C, `a918754204c013806b3f2692d0667144901e7af3`.
No Milestone E or later system was implemented.

## Implementation and automated evidence

[World interaction synchronization](world-interaction-sync.md) describes the fixed
original-game contracts, separate semantic codecs, authority, request/result bounds,
original execution, main-thread presentation and limitations. D uses the C registry,
baseline, deltas, generations, epochs, tombstones, ordering and state hashes.

**367 automated checks pass**, retaining all accepted B/C coverage. Added cases cover
Door/Window/request/result codecs, malformed packets, actor/context/type/generation,
stale and future revisions, distance abstraction, duplicate and evicted IDs, rate
limits, partial execution failure, guarded application, host observation, concurrent
baseline/delta, window hash, disconnect/reconnect, scene cleanup, bounded recovery and
real UDP requests. Impaired UDP uses 60ms latency, 180ms jitter, 10% loss and 20%
duplication. All projects build with zero warnings/errors.

Five independent-process regressions pass: clean transforms, clean world stream,
impaired world stream, abrupt host exit and abrupt client exit. Both world runs
converge at revision 38, six chunks and one tombstone. Synthetic tests are distinct
from the real Unity evidence below. Final validators/fingerprints pass as recorded
below. Raw assemblies, assets, saves, dumps and screenshots stay in
ignored local evidence; no machine-specific paths are committed.

## Populated scene and original contracts

Runtime discovery registers **950 doors and 184 windows: 1,134 static entities**.
There are **zero collisions** and **24 explicitly excluded/unresolved candidates**.
Two complete manifests from independent protected launches contain the same 1,134
ID/type/authored-path tuples with zero differences. IDs never use InstanceID.

Door Use/UseFully, original Update movement, Resett, original audio and the narrow
PlayerScript intent boundary are VERIFIED_RUNTIME. Static inspection also verifies
SlowFailed, TimeAdd, ResetLock, OpenDestroyed, NPC entry points and key unlock.
Unsecured sliding windows use a real Door-backed authored Window object; their
original open/close movement and WindowUse traversal are VERIFIED_RUNTIME.
LockedWindow tool/minigame completion remains VERIFIED_STATIC/LIMITED.

| Real sampled object | Entity ID | Evidence |
| --- | --- | --- |
| Soda Store exterior door | BF78407DB64E4D2E | Original host and client open/close; 90/0 degree endpoints, original audio |
| House 102 sliding window | FB41DA9F52E1B718 | Host requests, normal E, original traversal, client baseline/deltas and normal E |
| House 102 bathroom interior door | 6264996AD902D1F4 | Normal host E close/open and remote request open/close |
| House 102 locked wooden gate | FD33F22A0909DFC8 | Original crowbar R forced entry; canonical lock/tag/broken-hinge outcome |

## Real game host with harness client

Protected session 20261007-160810-Both provides most final runtime evidence; the
preceding protected session provides the first exterior-door request cycle.
All harness actors use the genuine sampled player location and production target
validation. No teleport, collider override or range bypass is used.

- 20261007-160304-client: exterior Open Accepted at 2960, duplicate Duplicate,
  Close Accepted at 2961. Host/client hash:
  `10C7E4C47DFDADA10C787A1DCE429B178AC46BD52273601281C6B2E34AD8E1A2`.
- 20261007-161349-client: baseline starts at 1273; original host open commits 1274
  during the 841-chunk transfer. WorldReady observes 1274; semantic Open is an
  accepted no-op, duplicate does not execute, Close commits 1275, stale Open rejects
  and a fresh baseline reaches 1275. Matching hash:
  `7AD835104172CFF37CA7EB6B1D56956510408758B2EB572A697E37AE1663861A`.
- 20261007-165028-client: real window Open 1612, duplicate, Close 1614, stale
  rejection and fresh WorldReady. Matching hash:
  `CA86F996A56A8E8AB5BF69855FC8896FB77F82AC1C23E7150FF3C89CB3724927`.
- 20261007-165629-client: interior Open 1634, duplicate, Close 1635, stale reject
  and fresh WorldReady. Matching hash:
  `CCB739A42EE5DEEA1395C4D953004C3DA82B07CE207FF39B56877FE1D4C5D786`.

Normal host E opens the window at 16:51:28 UTC (revision 1617); another E invokes
original WindowUse traversal and recovers original motor/camera controls. Normal
interior E closes at 16:53:30 (1631) and opens at 16:53:46 (1632).
Normal crowbar R at 16:41:02 commits gate revision 1450, state `01-1B-00`:
open, enabled, relock available, broken hinge, unlocked Door tag. This verifies the
host forced outcome, not a remote tool transaction or completed lockpick minigame.

## Harness host with real game client

Synthetic host adapters have no Unity geometry/gameplay callbacks. These runs verify
real client binding and presentation, separately from real-host execution above.
The captured manifest contains 1,134 records / 841 chunks / 322,639 snapshot bytes.

- 20261007-160356-host: door Open/Close deltas drive original motion/audio and
  finish closed. Matching revision 1136 hash:
  `0FD4B9398D27E02BF250F23E301E59C2E6081422C440F752037D3A7EB85A3654`.
- 20261007-161438-host: normal client E produces one Open request, accepted at
  1137, and the door reaches 90 degrees. No delayed cleanup Close request occurs.
  Disconnect leaves host Open; reconnect installs fresh Open baseline 1137.
  Matching hash:
  `F55FE3CFA0E991B9A7CD0DAF9A04D6C472C6DBF192BDA03961BB015C8A77BE98`.
- 20261007-170340-host: window Open 1135 and Close 1136 drive original presentation;
  the real client finishes usable/closed at local Y -0.201. Matching hash:
  `4CEE9ECC8A2C79B204E1FD63265D5494B0E6E3F288F4E152EC276C6B698B9DEA`.
- 20261007-170542-host: normal client E at 17:06:27 requests Open, accepted at
  1137. At 17:06:50 the real window is open, usable, no longer moving, local Y 0.400.
  One original gesture produces one semantic request.

Changed deltas allow original authored clips; baseline application is silent.
Canonical hashes contain semantic state/revision, never animation progress.

## B/C regression and cleanup

The original local player, controller, motor, camera behavior, camera/listener set,
hands and crowbar retain identical object identities before/during/after networking
within the loaded scene. Player/controller remain enabled. Normal movement, looking,
crowbar equip, interaction UI and WindowUse remain original.

The avatar harness was updated to bind captured D descriptors before baseline apply;
its earlier missing-binding run is not counted as WorldReady acceptance.
The corrected md-avatar-worldready fixture reaches WorldReady at revision 1641,
hash `FA23CF70A5FAD17C45DD29F8B1B6FB761FC835A24373718F6726DBAF1D212196`,
with 759 sent / 728 received / 726 sampled transforms and zero sequence gaps.
Runtime audits show original clip Run playing, visible skins, one avatar/animation,
private clips and crowbar equipment, no forbidden components, and unchanged local
identities. Equipped-disconnect audits show zero avatar roots, avatars, animations,
equipment and private clips.

Connected return to menu at 17:07:23 UTC follows the existing scene disconnect policy.
F8 in the menu shows Offline, no acquired player, no visible proxy, registry 0/0/0,
revision zero and epoch 5. Same-process gameplay reload passes: new host epoch 9, 950 doors/184
windows, no collisions; fresh baseline revision 1134 and matching hash
`9DABCBE0B987F42A2978BDD64175687340A0870FEC33097526C5C47E4C2E824F`. Reloaded gate is intact/locked
(`01-06-01`), window Open (`01-03-00-00`), exterior/interior doors Closed (`01-02-00`), matching
newly captured original state rather than old session revisions.
Final-build protected session 20261007-171618-Both passes WorldReady with 1,134 records, revision
1135, hash `5F356FFFDF7CB901D58083423F7F80BC02D101B4D3F75E3AF13370B9E39921B5`; error text is empty.
Full avatar phases complete: 759 sent, 711 received, 709 sampled, zero gaps. Final disconnect audit
records zero roots/avatars/animations/equipment/private clips and unchanged original identities.
Offline original phone, camera, jump, crouch, crowbar equip and swing respond; a separate fully
offline reload also passes sustained original movement (approximately 70 metres), camera and
original gate interaction UI. An NPC reports the visible crowbar and original police catch the test
actor before the optional final gate cycle; that cycle is not claimed as a pass. Real host gate
execution is verified above. No original motor/control writes or collision bypass were used to
finish navigation.

## Runtime findings and remaining limitations

Runtime evidence corrected transient rigidbody-based static filtering, a non-convex
collider target check, and an original slow-use cleanup double-toggle. The final
single-intent latch consumes one genuine gesture and ignores its later cleanup.
Telemetry avoids hashing an unapplied baseline and clears old errors on reconnection
and successful WorldReady. Development input now hooks original GetButtonUp too.

Earlier native world-load failures showed Direct3D allocation errors near 24.11GB
committed against 24.16GB; those failed runs are not acceptance evidence. Later
protected sessions loaded and completed all interaction samples at original Medium
texture quality. No other apps were closed and no OS memory setting was changed.

Remote lockpick/key/relock/tool-break completion is LIMITED: host outcomes synchronize,
but client minigame inputs, rewards, durability and AI ownership are deferred.
Tutorial/vehicle doors, unsupported windows and unverified binary devices are excluded.
Original host noise uses the game's local-player context; remote actor noise position
remains a limitation. Client presentation suppresses authoritative alarm consequences.
Cooperative persistence, loot, inventory, economy, AI, vehicles, missions, travel,
Steam networking and installer work remain outside D.

Final multiplayer and reconnaissance validators pass, including PowerShell parsing, distribution
allowlist, dependency provenance, all tracked and candidate untracked material/path checks and `git
diff --check`. All 183 original DLL/EXE fingerprints are unchanged. Both original plugin
configurations and the original save file set are restored byte-for-byte after the verified game
process closes. Original texture quality remains Medium. No proprietary assets, binaries, saves,
dumps or screenshots are staged. A single coherent D commit follows the completed acceptance checks.
Do not start E automatically; request separate authorization after accepting D.
