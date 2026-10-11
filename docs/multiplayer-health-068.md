# Multiplayer health 0.6.8 / protocol 10

This health update follows 0.6.7 (baseline commit `387fcd5`). It fixes confirmed
loading/presentation defects, restores overly suppressed native vehicle behavior
and reduces multiplayer work. It does **not** establish that the reported native
loading crash is eliminated. No milestone G, graphics tuning or new gameplay starts.

## Crash evidence and limits

Existing logs were preserved before launching. Two native crash traces appear
during the October 9 Hideout-to-Madison loading sequence at 16:44:22–23 UTC. The
shared Player.log mixes two processes. Windows event 1000 confirms a UnityPlayer
access violation (`0xc0000005`, PID `0x5674`); its process start matches the Client
startup, rather than assuming that role from the report. The other trace cannot
be uniquely assigned from this mixed log. The second trace includes repeated
D3D11 2048×2048 RenderTexture allocation failures (`0x8007000E`,
[E_OUTOFMEMORY](https://learn.microsoft.com/en-us/windows/win32/seccrypto/common-hresult-values)),
followed by native rendering/Impostors callbacks. No associated managed mod
exception or usable crash dump was found. Allocation pressure is evidenced;
its source, physical/GPU/commit constraint and causal connection to each crash
remain uncertain. No native graphics or Impostors code was changed.

Confirmed mod defects are corrected independently: acquiring a player before its
controller was enabled could permanently retain an empty scene key; destination
attachments occurred before a stable initialized-object observation; failed
restoration could continue into loading acknowledgement; repeated presentation
failures could retain another fallback mesh/material every frame. These are code
and runtime findings, not proof that they caused the reported native crash.

## Targeted crash-source diagnosis (2026-10-10)

**Classification: C — inconclusive.** The preserved evidence identifies a native
rendering failure during a Client travel session, but does not establish whether
mod-owned state contributed to the resource pressure or access violation.

The Windows event's process start FILETIME (`0x1DD580B2117C055`) converts to
`2026-10-09T16:27:51.407Z`. It aligns with the second BepInEx startup at
16:27:55.618Z, which logs `Client connecting`, `local=2 remote=1`, and automatic
follow to `MainScene|PlayerHideout`; the earlier 16:26:19.113Z instance logs
`Host started`. Event 1000 therefore assigns one `UnityPlayer` access violation
to the Client process. Its logged offset `0x588436` matches the UnityPlayer
address in the captured native trace. The event is recorded at 16:44:26.478Z.
The Client log records its local passenger application and Madison load at
16:44:19.036Z and 16:44:19.232Z. The Unity log has two crash sections and the
evidence folder has reports named `Crash_2026-10-09_164422926` and
`Crash_2026-10-09_164423529`; both report folders contain a `Player.log` with the
same SHA-256 as the shared root log. Thus the second trace cannot be assigned to
a PID. No `.dmp`, `.mdmp` or `.hdmp` is present in the preserved incident folder.

Immediately around the crash sections, the shared Unity log records repeated
2048×2048 D3D11 RenderTexture creation failures (`0x8007000E`) and a managed
stack through `Impostors.Managers.CameraImpostorsManager.UpdateImpostorTextures`
and `OnPreCullCallback`; Windows records an access violation in `UnityPlayer.dll`.
These establish allocation failure and the native rendering callback context,
not the cause of the allocation failure or the relationship between the two
crash sections. No managed multiplayer exception was logged.

The source review found no production scene/presentation call to create a
RenderTexture. The `Plugin` passenger-preview, `NativeStabilityFixture`, and
`VehicleGeometryAudit` RenderTexture calls are explicit diagnostic commands;
they restore the camera target and active target, release the target, and destroy
the temporary texture in `finally`. No such fixture start appears in the
preserved peer logs. The normal `RemotePlayerProxy` path creates presentation
meshes/materials and its scene invalidation calls `Dispose`; the Client log records
remote-visual removal before the Madison load. In 0.6.7, a persistent
`SetPose` presentation exception could retry fallback mesh/material creation on
later updates. The 0.6.8 change in commit `6c31e6a` retires that presentation once.
Neither peer log contains the corresponding `Avatar=Fallback` error, so this
source-level risk is not evidence that the retry path ran in the captured session.
The subsequent controller reacquisition, stable scene readiness, and failure
barrier changes also address confirmed loading defects independently; they do
not prove a causal fix for this native crash.

The Client was also logged as a passenger during destination initialization. The
0.6.7 `LocalPassengerState` path retains the original `MainCam` but, while seated,
moves its transform to the authored passenger eye point and sets near clipping to
at most `0.03`; exit restores the captured local pose and clip distance. The
crashing callback is a camera pre-cull callback, so this cooperative camera pose
is a concrete mod-touched input to native rendering even though no mod target
texture is assigned there. Separately, the available vanilla `EnviroSky`
decompilation allocates a 2048×2048 `cloudShadowMap` during initialization and a
screen-sized satellite render target. It contains no explicit release for the
satellite target in the available source; this alone does not establish a leak or
that either allocation produced the recorded D3D failures. The `Impostors`
implementation itself is absent from the available decompilation. These native
and mod-side paths leave indirect pressure or camera-state effects plausible,
without identifying a cause.

The Client's `SceneContinuityController` path uses native `LoadSceneAsync` and
session-scoped preference-read substitutions, then waits for initialized native
actor/manager/car references. The preserved Client log reaches `SCENE local
loaded` before the rendering errors. `Plugin` subscribes once to the three
`SceneManager` events and removes those handlers in `OnDestroy`; the Host scene
request handler likewise has a matching removal in `Dispose`. The production
scene path contains no coroutine. This source review found no duplicate mod
subscription or unbounded production scene coroutine, and no direct mod-owned
RenderTexture leak that explains the trace. It cannot rule out an indirect mod
contribution to Unity or driver resource pressure. Earlier Windows UnityPlayer
failures in the evidence set are not a vanilla control because their mod/process
state is not established.

**Next diagnostic needed:** one bounded A/B capture protocol: (A) repeat the
Client passenger Hideout-to-Madison transition with the current mod; (B) with the
mod disabled, keep two game processes under the same graphics settings while
each natively loads Madison. Capture separate per-process Unity logs, camera
pose/clip and active target identities, a full user-mode dump on crash, and
GPU/commit memory counters through scene load. This is not run automatically: it
requires two game instances in each arm. The comparison is the missing evidence
needed to distinguish the cooperative camera/scene path from native or system
rendering-memory pressure. No safe source change is supported by the current
causal evidence.

### Final diagnostic preflight (2026-10-10)

**The A/B run was not started.** The read-only process check found no running
Thief Simulator 2 or Unity process. Before any launch, `nvidia-smi` reported the
RTX 5060 at 100% utilization, 7,802/8,151 MiB allocated, 77 C and 144/145 W,
with no process attribution. This is severe, unexplained GPU pressure; adding
two game processes would violate the diagnostic stop condition. GPU performance
counter sets were unavailable, and the operating-system CIM memory query was
inaccessible. `nvidia-smi` could report GPU totals only.

The repository's `tools/runtime/launch-game.ps1` cannot safely run the requested
comparison on the shared installation: it changes the shared Doorstop setting
and plugin files, launches one game process, and does not pass unique log paths.
`tools/runtime/collect-runtime.ps1` copies the common LocalLow `Player.log` and
the installation-root `BepInEx/LogOutput.log`, so two same-install processes
would not have attributable logs. The health record's last verified deployment
is 0.6.8; the incident affected 0.6.7, while the current repository source is
0.6.10. This preflight did not re-read the installation outside the workspace,
so its present mod version is unconfirmed. No ProcDump/WinDbg command was
available on PATH or in the repository's local tools; no GPU counter set was
available. No dump capture, WER setting, registry change, or system-wide trace
was attempted.

There are no A/B observations, crash outcomes, or process-specific logs from
this attempt. The crash remains **C — inconclusive**, and the audit remains open.
The smallest safe next diagnostic is an isolated staging setup with the
verified original game build and affected 0.6.7 mod build, separate game roots
for concurrent processes, isolated or verified-restorable per-instance saves,
PlayerPrefs and configs, and distinct Unity/BepInEx log destinations. Retry only
after GPU load and allocated memory have returned to an explainable safe state;
keep the existing five-minute-per-launch and ten-minute-total limits. A dump
tool is optional only if an already available capture mechanism can be used
without system-wide configuration changes.

## Targeted preservation and lifecycle audit

Read alongside [the intervention inventory](vanilla-gameplay-audit.md) and
[0.6.7 boundaries](gameplay-stabilization-067.md). Those documents retain historical
results; this table describes the current changes and exceptions.

| Class | Native behavior / multiplayer reason | Scope and preservation |
| --- | --- | --- |
| B → narrowed | Entire occupied remote-driven CarController.Update was skipped to avoid Host passenger input/actor callbacks | A connected Host now runs original fuel use, gauges, audio activation, repairs, alarm/trunk/lootbox timers. The verified transpiler filters six local button reads, four visual steering reads and two Host passenger isDrifting writes only while an authenticated remote driver owns that car. Original local/offline reads and writes pass through; Host EVP remains the physics authority. Client replica suppression remains necessary. |
| A | Original driver/rear occupancy must be restored before loading acknowledgement | Stable scene key, manager, actor and canonical manager-slot car are observed on consecutive frames before attachment. Active car PS references must match the actor. Inactive cars on vanilla on-foot maps do not prevent readiness; occupied travel requires an active compatible car. No original car is cloned. |
| A | A confirmed traveling driver otherwise waited for EnterVehicle's seat lerp while Update waited for WorldReady | After native initialization, call original IsStartingCar only for that confirmed destination driver, inside the existing native-apply scope. Its original authored placement, engine/camera sequence executes. Initial-seat suppression admits only this owned restoration. Ordinary interaction still uses EnterVehicle. Driver snapshots and passenger state restore on failure/exit/invalidation. |
| A | Loading failure and destroyed references must not silently acknowledge a world | Restore surviving actor state, clear failed local attachment, report transition failure and keep readiness closed until a new transition/session. Boundary/Dispose clear candidate references; main-thread polling, epochs and existing authoritative baseline application remain. |
| A | Remote avatars are presentation objects | Retire a failing presentation once, reuse its existing fallback and clear source caches on Dispose. Seated avatars skip irrelevant sole mesh baking; ordinary civilian calibration remains. No original NPC, player, camera or tools are modified. |
| A | Bounded multiplayer observation/serialization | Vehicle binding uses only the already-supported VehiclesManager firstCar/van references; per-frame vehicle validation/nearest lookup avoids capturing LINQ/sorting. Player interpolation uses a 32-entry ring; wire-compatible door/window encoders allocate only their final arrays. No native scene loading, input or physics optimization. |
| A | Explicit development fixture | Menu interference is closed through original menu methods only during the opt-in native seat fixture. Temporary input patches still unpatch in finally; production menu behavior is unchanged. |

The remaining interaction/inventory/economy/time hooks were inspected against the
existing inventory: session authority, personal inventory/shared wallet, reliable
idempotent transactions, native slow doors/breaching, Client profile protection,
sleep ownership and reversible teardown remain. No new unsupported reward,
NPC/police/security-camera synchronization or UI feature is added. The withdrawn
menu black-screen report requires no speculative camera change.

## Verified original vehicle entry

The deployed native EnterVehicle contains no visible entry animation. It changes
controls, hides equipment, marks seating and disables the original controller;
native Update lerps the actor toward sit_transform. RunCar later handles native
camera/engine behavior. IsStartingCar directly places the actor at the authored
seat for native initialization. ExitCar retains its original GetOutCar animation.
The apparently instant entry is consistent with this native implementation; no
animation or camera transition was invented. Rear seating is the scoped required
cooperative extension; the front lootbox remains native.

## Performance and synchronization evidence

A temporary local comparison used baseline/current core code, 200,000 operations,
five rounds and median timings with tiered compilation disabled:

| Component | 0.6.7 | 0.6.8 | Result |
| --- | --- | --- | --- |
| Full-buffer movement receive | 14.538 ms, 0 allocated bytes | 6.976 ms, 0 bytes | About 52% less processing time |
| Gradual door encoding | 36.163 ms, 448 bytes/capture | 8.800 ms, 64 bytes/capture | About 76% less time and 86% fewer allocated bytes |

These are component microbenchmarks, not game FPS measurements. Sender-timed
interpolation, stale/snap/semantic behavior, 20-Hz vehicle motion, changed-state
suppression and reliable authoritative interactions retain their contracts.
WorldReady retires completed travel metadata once instead of repeatedly creating
descriptors; failed/delayed callbacks cannot reopen a failed scene.

The existing native profiler observed 1,427 fixture frames: median 5.078 ms,
p99 25.119 ms, maximum 337.112 ms, with three spikes. Network processing averaged
0.0667 ms; vehicle binding averaged 0.0015 ms. Registry/inventory initialization
still peaked at 79.739/58.931 ms. This short window includes startup/cleanup and
diagnostics, lacks a matched pre-update run and does not prove freezes eliminated.

## Validation, installation and remaining acceptance

- Eight meaningful new checks; **756 checks passed** in the one completed full
  suite. A sandboxed attempt stopped at blocked localhost UDP before the successful
  permitted run; no assertions were weakened. Final Unity-side adjustments were
  rebuilt and the eight focused core checks rerun; they are not Unity emulation.
- Final controlled native run: **49 seat checks plus five input checks**, both Host
  roles, managed peer, production WorldReady, original actor/camera/inventory,
  physics identities, single remote visual, exit/cleanup and front lootbox. Earlier
  bounded runs found the acquisition race and a paused-fixture timeout. Five owned
  launches total, bounded below **6 minutes 33 seconds**, each below five minutes;
  collection time is included. No two full games or unrelated process termination.
- The final confirmed-travel IsStartingCar/inactive-map changes received native
  source inspection and Release build validation **after** that native run. Actual
  occupant travel in both role permutations, native Client join and two-human
  driving/interaction acceptance remain required; no universal equivalence claim.
- Four Release targets build with zero errors. The existing NU1900 package audit
  feed warning remains. Existing validators and whitespace checks pass.
- **0.6.8 remains directly installed**, protocol **10** unchanged. All three installed
  mod/dependency DLL SHA-256 values match Release outputs; duplicate/stale copies
  are checked. Original **183 DLL/EXE fingerprints** remain unchanged.
- Fresh pre-test **35 save files, 64 PlayerPrefs values, three configurations and
  loader settings** are restored and verified. Original diagnostics and crash/test
  logs are preserved locally. Proprietary files, saves and evidence are ignored.

The native rendering crash remains unconfirmed as fixed. Current remote driving
still uses Host-side drunk effects and does not forward Client reset/modifier keys;
other lockpicking/window/padlock limitations remain as documented in 0.6.7.
