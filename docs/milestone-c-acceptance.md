# Milestone C acceptance evidence

Date: 2026-10-07. Mod 0.4.0 / protocol 3.

**ACCEPTED — TWO-GAME VALIDATION DEFERRED.** Automated validation, bounded game-host
and game-client validation, and populated scene cleanup/reload pass. This acceptance
covers the generic foundation and synthetic adapters; real world adapters remain future work.
Milestone B remains accepted with its existing two-game acceptance gate deferred.
Milestone D has not begun.

## Delivered scope

The foundation implements stable static descriptors, separate monotonic dynamic IDs,
generations, host authority, independent ownership and persistent player identity
concepts, a main-thread registry, immutable canonical state, global/entity revisions,
semantic spawn/state/despawn, retained tombstones, bounded snapshot codecs and assembly,
explicit applied-baseline acknowledgement, concurrent-change backlog, resnapshot,
and separate WorldReady state. See [network-entity-registry.md](network-entity-registry.md)
and [world-snapshot.md](world-snapshot.md) for the exact contracts and limits.

The fixed future gameplay rules are in [coop-gameplay-rules.md](coop-gameplay-rules.md).
No persistent identity provider, cooperative save format, game state extraction,
actual world adapter or gameplay synchronization is enabled. Original game assets,
assemblies, saves, runtime evidence and local installation paths are excluded from Git.
The package still contains only our assemblies, permitted LiteNetLib and its license.

## Automated verification

- All projects build, including net46 plugin/diagnostics and net9 test/harness outputs,
  with zero warnings and errors.
- **290 checks pass**, retaining the original 145 checks. Only the existing protocol
  version assertion changes from 2 to 3; its compatibility rejection intent is retained.
- Coverage includes canonical SHA-256 static identity vectors, semantic/fallback keys,
  forced collisions, dynamic nonreuse, generations, host authority, ordering, stale
  sessions/epochs/revisions, explicit cleanup, snapshot limits/checksum/timeout,
  reordered and duplicate/conflicting chunks, adapter validation, lifecycle operations,
  acknowledgement/readiness and overflow/resnapshot.
- The fixture captures A=10/B=20/C=30 plus 32 filler entities at revision 35. During
  transfer the host commits A=11, B's tombstone and D=40 at revisions 36/37/38.
  The client applies the immutable baseline, acknowledges it and catches up to the
  complete canonical host registry: 35 live entries, one tombstone, revision 38.
- Actual UDP tests pass on clean loopback and with 60 ms latency, 180 ms jitter span,
  10% loss and 20% duplication. Jitter permits datagram reordering. The impaired test
  confirms chunks traverse dropped/duplicated/reordered packets with no link overflow.
- Bounded fuzz coverage comprises 16,000 targeted packet mutations, 20,000 random
  malformed world slices and 5,000 checksum-valid malformed snapshot payloads, plus
  exhaustive packet truncations and targeted invalid length/count/type/ID fields.
- Independent processes pass normal, abrupt-host, abrupt-client and both original
  impaired-avatar cases, plus a world-mode 60/180/10 case. The process world case
  uses loss/jitter; duplication is exercised by the actual-UDP test-suite case.
- Multiplayer and reconnaissance validators, staged/unstaged whitespace checks and
  all **183 original DLL/EXE fingerprints** pass. No forbidden material is tracked.

## Legitimate game runtime

Protected-save sessions and evidence are local and ignored. Configuration was backed
up before development controls were enabled; the original plugin/diagnostics
configuration was restored. Every session's backed-up save files were restored
byte-for-byte after collecting logs. The final tested mod binaries were deployed.
The final isolated multiplayer startup session `20261007-113317-Multiplayer` loaded
0.4.0 / protocol 3 on Unity main thread 1 with restored normal configuration and no
multiplayer managed exception in its collected startup log. It was closed and its
save backup restored after the bounded smoke check.

The successful gameplay session `20261007-111738-Both` reached MadisonSt/MainScene.
Game host plus independent harness client completed the synthetic world fixture:
epoch 7, revision 38, six chunks, 2001 bytes, 35 live entries, one tombstone and zero
remaining backlog. Both reported fixture PASS. The development canonical registry
hash was `5B22E92F2A12BF8658FA7993EB9077F46343C3CE9ED3678F26F9191E87F57AB4`.
F8 world registry/snapshot/backlog telemetry was observed and its audit was logged.

The same session passed the bounded existing avatar sequence: remote spawn,
Idle/Walk/Run, look, renderer-only crowbar attach/remove, teleport and disconnect
cleanup. Audits retained original local IDs, enabled original controller/player,
six cameras and one listener; the remote avatar retained no forbidden gameplay
components. The original first-person implementation was not modified. Offline
player acquisition and normal game presentation were observed before hosting;
this does not claim exhaustive singleplayer regression coverage.

A read-only descriptor probe of the genuine installed player binding repeated
the same static ID across successful acquisitions/processes. It registered or
mutated no real world entity. It does not establish whole-world or two-peer static
identity stability, which remains an adapter-specific prerequisite.

An initial gameplay load and returning from gameplay to the menu caused native
game crashes. The populated-registry scene-unload callback was not reached during
the menu-return attempt. Subsequent launches loaded the plugin but produced black
UI captures, preventing a reliable game-client gameplay test. No multiplayer managed
exception was identified in those logs; the native crash cause remains unresolved.
Pure registry/session tests verify scene cleanup, but are not a substitute for the
real-game reload check. These initially open client/reload gates were subsequently
completed in the recovery session below. Two legitimate game instances remain untested.

### Completed client and reload gates

Protected session `20261007-120319-Both` loaded the same deployed 0.4.0 / protocol 3
build without code changes. Normal game UI capture recovered on this run.

- Real game client plus independent harness host passed at epoch 1, revision 38,
  six chunks, 2001 bytes, 35 live entries and one tombstone. Its canonical registry
  hash exactly matched the earlier game-host hash above. The first-person audit
  again retained original IDs, enabled controller/player, six cameras and one listener.
- A click intended for the audit selected the adjacent existing two-second adapter
  fixture. It recovered without changing the game actor; the client applied a fresh
  revision-38 baseline (snapshot 2, 2048 bytes) and both peers reported PASS again.
- The independent host completed 120 seconds: 2245 synthetic poses sent, 2144 real
  peer poses received, zero sequence gaps and no transport error.
- The game then hosted a fresh synthetic world. After revision 38 / WorldReady,
  the normal menu-return action crossed the actual scene boundary while connected.
  The remote avatar was removed and the peer received the scene-change disconnect.
  Registry live entries, tombstones, revision, snapshot and backlog all reset to zero;
  epoch advanced from 1 to 5 across the game's callbacks. The menu loaded successfully.
- Gameplay reloaded within the same game process. Original player acquisition and
  the unchanged read-only static probe ID `Static:5D25F295E0570F96` were observed.
  F8 showed an empty registry at epoch 9 with no remote proxy. A new host/harness
  connection completed another six-chunk fixture at epoch 9, revision 38. The harness
  completed its bounded 30-second run without error or sequence gaps.
- No native crash or multiplayer managed exception occurred in this recovery run.
  Earlier crashes remain evidence. System commit headroom repeatedly approached zero;
  earlier crash logs show Direct3D texture allocation failures. The capture helper
  also explicitly failed to allocate an 8 MB image during recovery, then recovered.
  Memory pressure is a supported contributor; this run establishes no universal fix.
- An optional texture-arrow action was rejected by automatic approval review because
  it interpreted the arrow as increasing texture quality. No texture change occurred;
  the original preference was verified unchanged after the tests.
- Saves and configurations were restored. Two automatically created backup files were
  preserved under ignored local evidence using `restore-save.ps1 -RestoreFileSet`;
  the original save file set and bytes were verified after restoration. No TestEntity
  state was persisted. Original binary fingerprint checks still pass.

## Remaining gates and next milestone

Two-game validation remains open. Real static manifests must be proven stable per adapter; no real game
semantic field is yet approved as a universal object GUID. Scale/throughput limits
need representative world adapters. Generic adapter application cannot roll back
arbitrary external side effects after an adapter throws. Persistent identity/save
translation, authentication and tombstone compaction remain future work.

No door, window, loot, inventory, container, NPC, police, vehicle, mission, wallet,
XP, skills, progression, time or scene travel synchronization was implemented.
Recommend a separately requested Milestone D for one narrowly bounded static
interaction adapter, proving that adapter's static identity against legitimate peer
installations and retaining the deferred two-game acceptance gate. Do not start
Milestone D automatically.
