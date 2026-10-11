# Manual runtime acceptance

Every check is AUTOMATED (managed unit/integration tests), HARNESS (synthetic transport peer/fixture), or MANUAL (human gameplay/navigation and visible observations). MANUAL_REQUIRED is a stopping point for runtime automation, not a PASS. Never fabricate input to replace missing human playability evidence.

Prepare a single batch with the game closed:

```powershell
./tools/runtime/start-manual-acceptance.ps1
# Already built and validated during development:
./tools/runtime/start-manual-acceptance.ps1 -SkipBuild
# Deployment and instructions only, without changing configuration or launching:
./tools/runtime/start-manual-acceptance.ps1 -SkipBuild -PrepareOnly
```

The launcher validates original binaries, deploys our mod, creates an ignored unique test directory under `.local/manual-acceptance`, records configuration, backs up game PlayerPrefs and saves, labels diagnostic events, and launches exactly once. It never navigates or invokes gameplay actions. Existing process checks prevent a second accidental launch. There is no Steam/DRM bypass. Use an initialized ordinary profile; no automatic New Game or Continue/save invocation.

It writes `INSTRUCTIONS.txt`, a copy of [manual-tests-s1.json](../tools/runtime/manual-tests-s1.json), `observations.json`, and `acceptance.json`. Use the emitted directory in subsequent commands. Paths, saves, binaries and raw evidence remain ignored.

After loading a gameplay scene, host through F8. Close F8, then start the optional synthetic client from a terminal:

```powershell
./tools/runtime/start-avatar-harness.ps1 -Sequence s1 -Seconds 900 -Label s1-manual
```

This client consumes the actual host's Door/Window/Pickup baseline, automatically acknowledges scene descriptors, and echoes native player semantics as a visual test actor. On foot it stays three metres ahead of the local actor; seated it echoes the actual seat world position. It is a synthetic echo, not another native client. It can test native host transport/registry rebuilding and observer presentation. Overlap with the vanilla local TPP body can occur in the same seat: compare component/root audits and explicitly report visibility uncertainty. It cannot establish native client automatic loading or two-player seat observation. Those cases require a legitimate second game or a suitably captured-manifest harness host; retain MANUAL_REQUIRED if unavailable.

Controls (left or right Ctrl):

| Control | Work |
| --- | --- |
| Ctrl+F10 | MARK: one bounded line with UTC, session label, scene, network/world/transition state, native root/controller feet geometry, crouch/occupancy and proxy state |
| Ctrl+F7 | SNAPSHOT: explicit one-shot local/remote audits, registry summary/hash, inventory hash, pending count and timed legacy F8 capture/ownership audit |
| Ctrl+F6 | Start a ten-second profiler window; average FPS, average/median/p95/max frame time, Gen0 collections, aggregate subsystem/event timings |

The marker never calls the F8 refresh, hashes, resource scans or manifest audits. Snapshot work is deliberately heavy; run it outside frame-time windows. Dedicated F9 diagnostics remains independent. These Ctrl combinations are not used by the existing mod; confirm game/custom keyboard bindings before testing.

For performance, hold scene, camera, graphics, pause state, peer/load and profiler window length constant. Record OFF, ON, OFF, ON; opening F8 changes cursor behavior, so document the actual pause state. Changing F8 during a window invalidates it. Frame samples use unscaled delta time. The bounded buffer holds up to 8192 frames, so a very high frame rate can end it earlier. .NET 4.6 Mono lacks a verified accurate per-thread allocation counter here: allocations/frame is explicitly UNAVAILABLE, not zero. Gen0 collection count is supporting evidence only. Separate Layout/Repaint counters establish OnGUI invocation cost. The one-shot legacy operations and managed synthetic benchmark do not establish the old build's FPS; capture a separate equivalent old-build run or leave before-build comparison open.

Batch S1-PERF-01, S1-CROUCH-01 and S1-VEHICLE-01 in one launch. S1-SCENE-01 and S1-JOIN-01 can use the same launch if both peers/safe states are ready; otherwise use one further session. Baseline-build frame measurement requires a separate launch. Do not restart for every small check. A bounded startup smoke is a separate HARNESS/technical check, not human acceptance.

Write human results into `observations.json`: tester, each case's `Id`, visible result, steps actually performed, scene/camera/pause context, timestamps, any failure and whether the observer was native or synthetic. Do not paste proprietary saves/assets into tracked documents. Report abbreviated source-backed results afterward.

Close the game and collect:

```powershell
./tools/runtime/collect-manual-acceptance.ps1 -AcceptanceSession '<emitted local directory>' -Restore
```

The collector gathers runtime logs, filters checkpoints/profile/scene/failure evidence, writes `evidence.txt` and per-case `results.json`, restores the original mod config, loader config, save file set and game PlayerPrefs with verification, and rechecks original binary fingerprints. Post-test saves are preserved in ignored storage before restoration. It never accepts cases automatically, including when human notes claim PASS. Review timeline/visual observations/performance context against the manifest first. Repeated collection cannot silently manufacture acceptance.

After evidence exists, Codex parses logs, associates each marker with the human action, checks session/epoch/baseline/uniqueness and measured frame windows, and updates the report. Missing or contradictory evidence remains MANUAL_REQUIRED/FAIL. Gate E requires at least one actual prepare → human action → capture → collection cycle. A startup smoke cannot satisfy it.
