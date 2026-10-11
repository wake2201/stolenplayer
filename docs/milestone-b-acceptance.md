# Milestone B acceptance — production NetworkAvatar

Mod **0.3.0**, protocol **2**. Finalization of implementation commit `e598f2ee27ee534bdfa85796a8403b00098b4154`, tested 7 October 2026. **ACCEPTED — TWO-GAME VALIDATION DEFERRED.** No Milestone C implementation is included.

**TWO-GAME ACCEPTANCE: DEFERRED.** The user explicitly separated that later gate from accepting B for continued development. [Future procedure](two-game-acceptance-plan.md): first attempt two normal legitimate local processes, A Host / B Client over localhost with RAM/private bytes/system commit/CPU/GPU monitoring; use separate legitimate PCs if unsupported or unstable. No ownership, DRM or Steam authentication bypass. Harness evidence does not replace two-game acceptance.

## Permanent architecture gate

**THE LOCALLY CONTROLLED PLAYER ALWAYS USES THE ORIGINAL THIEF SIMULATOR 2 FIRST-PERSON GAMEPLAY STACK.** Production observes PlayerScript, PlayerMotorBehavior, PlayerCameraBehavior and CharacterController without replacing them, the original camera/listener, hands/arms, functional tools, interactions, inventory, animations or input. NetworkAvatar is presentation for OTHER peers. This gate remains mandatory for Offline play and future milestones.

The diagnostics-only, default-disabled development fixture supplies bounded input at existing Rewired getters for the genuine original player. The original motor/camera/gameplay logic still executes. It never writes transforms, camera pitch, motor flags or inventory. PID bounds, pause/readiness checks, expiry, scene teardown and unpatching constrain it. This is **DEVELOPMENT INPUT FIXTURE** evidence, not human-driven input. Normal mouse wheel, click, crouch, inventory, phone and flashlight actions are separately identified as **NORMAL GAME INPUT**.

## Sustained movement and characterization

Six-second forward walk and sprint requests exercised the actual local controller/motor. Stable analysis uses the continuous interval 1.0–4.5 seconds after request start: acceleration settles first, and the later terrain/curb correction samples are reported separately. Population standard deviation; positions and velocities in game world metres / seconds. This is characterizing the observed gameplay state, not a new speed threshold.

| Stable state | UTC window | Samples / span | Planar mean / min / max / standard deviation (m/s) |
| --- | --- | --- | --- |
| Walk | 07:05:22.990–07:05:26.436 | 35 / 3.446 s | **2.998743 / 2.952474 / 3.001203 / 0.007984** |
| Sprint | 07:05:37.109–07:05:40.521 | 34 / 3.412 s | **7.053953 / 6.770698 / 7.337980 / 0.168607** |
| Idle / stopped | Repeated several-second windows before/after fixture expiry | 10 Hz observations | **0 / 0 / 0 / 0** |

Walk: isMoving=true, isRunning=false, isGrounded=true, isCrouching=false; body yaw 358.4006°, look pitch -6°. Vertical velocity mean -0.000588, range -0.025151..0.000457 m/s. Typical world controller velocity approximately (-0.0805, 0.0005, 2.9981).

Sprint: isMoving=true, isRunning=true, isGrounded=true, isCrouching=false; same independent yaw/pitch. Vertical mean 0.382350, range 0.260203..0.543722 m/s on the rising road. Typical velocity initially (-0.2051, 0.4129, 7.3351). Exhaustion decreased normally; this was a sustained semantic sprint, not a constant-speed promise. Configured moveSpeed=3 and runSpeed=7.5. Static original motor inspection confirms sprint target uses runSpeed multiplied by exhaustedSpeed (and carrying multiplier), consistent with the measured reduction.

Wider 1.0–5.75-second windows: walk 47 samples / 4.664 s, mean 2.935072, min 0, max 3.001203, deviation 0.432808; sprint 46 samples / 4.649 s, mean 6.806343, min 0, max 7.337980, deviation 1.038968. Occasional controller correction samples are zero even while genuine motor movement flags remain active. Earlier grass sprint also crossed terrain with grounded changes. These samples are retained rather than discarded as evidence of constant speed.

The outgoing real stream observed Idle → Walk → Run → Idle and intermediate Walk states. Production continues motor-flag semantics with 120 ms stop hysteresis; no velocity-threshold redesign. Stopped observations return moving/running=false, grounded=true, crouching=false and zero planar/vertical velocity.

Connected follow-up: NORMAL GAME INPUT crouch produced CrouchIdle; a six-second
DEVELOPMENT INPUT FIXTURE produced CrouchMove then CrouchIdle. Separate three-second
backward and right-strafe fixtures preserved yaw and the expected signs of the
derived body-relative velocities: mean body-forward -2.9998 m/s backward and
mean body-right +1.7649 m/s during geometry-constrained strafe, with yaw 112.4006°.
NORMAL GAME INPUT Space produced outgoing
Airborne (grounded=false) followed by landing. These verify semantics; they do not
accept additional remote animation clips.

## Turning and camera pitch

All sustained turning/pitch trials use **DEVELOPMENT INPUT FIXTURE** through original camera input. Small turn: 0.1006° → 5.8010° (+5.7004°). Approximately 90°: 5.8010° → 101.0017° (+95.2007°). Approximately 180°: 101.0017° → 291.4030° (+190.4013°). A separate wrap crossed 270° → 0.1006° (+90.1006° shortest signed change).

Original actor root yaw, LocalPlayerAdapter BodyYaw and harness-received yaw agree within log precision. Positive horizontal look increases body yaw; model facing remains corrected only by VisualRoot metadata, with no 180° network inversion. Pure interpolation checks include 359°/1° both directions. Synthetic remote yaw transitions and front/side presentation are inspected separately from genuine local turns.

Original private pitch reached **-75° look up**, **0° neutral**, **+75° look down**. Camera local X was respectively 285°, 0°, 75°. Harness PlayerState received these signed values, and yaw remained independent. Protocol ±80° remains unchanged. Remote head/spine LookPitch application is **LIMITED**, disabled until axes, weighted distribution and animation reset are visually accepted; no speculative bone rotation or NetworkRoot pitch is applied.

## Per-feature classification

| Feature | Classification | Evidence / bounded limit |
| --- | --- | --- |
| Sustained local Walk / Sprint / Stop | VERIFIED_RUNTIME | Original player, bounded labelled fixture; flags, velocity, pitch/yaw and outgoing semantics |
| Original local crowbar | VERIFIED_RUNTIME | NORMAL GAME INPUT: original visual equip, swing and original CrowbarHit entry/return; offline and connected; no replacement or interception by production |
| Real slot mapping | VERIFIED_RUNTIME + VERIFIED_STATIC | Original currentItem: 0 none → 1 crowbar → 0 none; native phone opened slot 99 while outgoing item stayed None; six pure regressions. Other slots UNKNOWN |
| Additional tool | LIMITED | Normal flashlight operation inspected; no additional equipment-slot-to-ItemID mapping proven |
| Outgoing equipment | VERIFIED_RUNTIME | At 07:01:15–18, normal wheel/click/wheel yielded real harness item -1 → 0 → -1 through adapter/codec/LiteNetLib |
| Male AvatarId 0 | VERIFIED_RUNTIME | Seven skins, clean whitelist, bindings/materials, skeleton, feet, yaw and Idle/Walk/Run; repeated teardown |
| Female AvatarId 1 | LIMITED, non-blocking | Final runtime: 89 transforms, eight skins, unit scale, compatibleGait=false; safe capsule fallback. Female feet/gait/socket not accepted; female equipment disabled until separate calibration |
| Crouch visual | LIMITED | Real crouch semantics and original crouch retained; remote Idle/Walk approximation |
| Backward visual | LIMITED | Body-relative velocity remains correct; no accepted compatible backward clip |
| Strafe visual | LIMITED | Left/right velocity retained; no accepted dedicated clips |
| Airborne visual | LIMITED | Native original jump produced real outgoing Airborne; generic player jump does not establish civilian clip compatibility |
| Head / upper-body look | LIMITED | LookPitch sampling/replication verified; visual application disabled |
| Animation transitions | VERIFIED_RUNTIME within coverage | Repeated Idle → Walk → Run → Walk → Idle; 150 ms crossfade retained. No obvious popping/restart/root drift. Exact foot locking and speed matching are not implemented |
| NetworkRoot invariant | VERIFIED_RUNTIME + VERIFIED_STATIC | 262,952 soak checks, then final exact-equality guard runtime check reaching 10,612 assertions; no reported mutation. Incoming pitch stays below the yaw-only root; source-local motion zero |
| Scene/source reacquisition | VERIFIED_RUNTIME + VERIFIED_STATIC | Actual gameplay → menu → gameplay reload twice; old actor/PS/cameras changed, new male sources spawned; cache Reset clears references; no MissingReferenceException loop |
| Offline singleplayer | VERIFIED_RUNTIME | Original movement/sprint/crouch, inventory, phone, flashlight and crowbar use; zero remote roots/assets before hosting |
| Connected first-person | VERIFIED_RUNTIME | Original PS/actor/controller/motor/cameraBehavior, six cameras, listener, hands and crowbar IDs stable within each scene before/during/after connection |
| Ten reconnect cycles | VERIFIED_RUNTIME | 60 connected audits: 1 root / 1 avatar / 1 animation / 3 private clips; ten disconnected audits: all zero; equipped visual one |
| Abrupt loss while equipped | VERIFIED_RUNTIME | Final build: owned harness killed at 07:42:50.176 UTC while equipped; Timeout at 07:42:55.181; global root/avatar/animation/equipment/privateClips=0 at 07:42:58.179; fresh reconnect and local play passed |
| Medium soak / memory | VERIFIED_RUNTIME | 30 minutes connected, about 30 min 46 s overall; NO OBVIOUS MONOTONIC LEAK; game-level drift and limitations below |
| Builds / validators / fingerprints | VERIFIED_STATIC | All projects build without warnings/errors; 145 checks, five process cases, validators; all 183 originals unchanged; all four deployed DLL hashes match |
| True two-game cooperation | DEFERRED | Separate later gate; no test attempted in this task |

The civilian's inspected 21 legacy clips contain no proven compatible full-body crouch, directional or jump set. First-person crouch hands/camera and generic-player walkBack/jump do not share accepted binding/rest/orientation evidence. No unsafe retargeting or new proprietary assets were introduced. ActionState stays Unknown. Relaxed hand fingers and attached crowbar are not an accepted attack/grip pose.

## Identity, lifecycle and memory evidence

Raw evidence remains local and ignored: runtime session `20261007-064623-Both`, diagnostics `recon-20261007-064626-23c2d559.ndjson`, real peer logs `mb-real-local-2.log` / `mb-female.log`, and `mb-soak-cycle-1` through `10`. Summary numbers are committed; runtime dumps, screenshots, game assemblies/assets and machine paths are not.

PlayerScript instances changed across real reloads (316802 → 953900 → 1563934), proving actual recreation rather than a synthetic SceneManager signal. In the last scene actor=1371950, controller=1460296, motor=1563942, cameraBehavior=1563940; cameras=1439376/1439380/1439394/1439374/1439388/1439360, listener=1455556. Original hands/crowbar references remained stable during connection/reconnect audits. IDs are evidence only, never network identity.

Normal crowbar visibility in public triggered original NPC/police consequences twice. The caught state disabled the original controller; the development fixture correctly rejected further movement. Safe menu reload restored gameplay. These are ordinary game consequences, not avatar or startup allocation failures. Save backups are restored after the test session.

No startup allocation failure occurred in this finalization session. High system commit pressure is recorded separately from avatar lifetime counts. Earlier base-game/vanilla allocation failures remain in historical reconnaissance. No pagefile setting was changed. Process memory and GC.GetTotalMemory observations are coarse game-level evidence, not a Unity allocation profiler and not proof of zero leaks.

The bounded soak ran 07:05:55–07:36:41 UTC: ten 180-second connections,
34,156 synthetic incoming states, 32,001 real game states received by the harness,
zero sequence gaps. Sixty connected and ten disconnected audits had only accepted
lifecycle counts. All original local instance-ID sets had cardinality one during
the soak. No root assertion failure or MissingReferenceException loop was logged.

| Soak memory metric (MiB) | First / last | Minimum / maximum |
| --- | --- | --- |
| Working set | 5274.81 / 4130.80 | 4130.58 / 5274.81 |
| Private bytes | 10065.38 / 10011.71 | 9967.19 / 10066.66 |
| System commit | 21003.30 / 20923.93 | 20783.25 / 21272.23 |
| Commit limit | 21493.11 / 22524.12 | Windows-managed expansion observed |
| Available physical memory | 4188.52 / 4990.27 | 4105.86 / 5013.14 |
| Managed memory (audit GC.GetTotalMemory(false)) | Fluctuating | 369.24 / 463.54 |

At the consistent 175-second cycle endpoints, private bytes went 9974.68 →
10011.71 MiB, a 37.03 MiB drift (about 0.37%), with decreases in cycles 3 and 8.
The initial warmup high-water mark exceeded the final value. Working set was
trimmed markedly in later cycles while private bytes stayed similar. Classification:
**NO OBVIOUS MONOTONIC LEAK** in this bounded avatar lifecycle fixture. Ordinary
game allocations and diagnostic overhead are included; smaller/longer leaks remain
possible and should be monitored in future combined gameplay soaks.

## Final validation and decision

All four root projects and their referenced core/LiteNetLib targets build with
zero warnings/errors. **145 checks pass**, retaining all previous 129 plus six
equipment mapping and ten pure development-input boundary regressions. The suite
keeps its zero-allocation assertion; tiered JIT is disabled only in the test
process to avoid JIT work contaminating that measurement. Five independent
process cases pass: normal, abrupt Host, abrupt Client, 50 ms/20 ms/5% impairment
and 100 ms/40 ms/10% impairment.

Multiplayer and reconnaissance validators pass: PowerShell parsing, distribution
allowlist, pinned source/license provenance, all **183 original DLL/EXE hashes**,
and tracked material/path protections. No proprietary asset, game assembly,
screenshot, runtime dump or machine-specific path is tracked. All four deployed
DLL SHA256 values match final build outputs. Git whitespace validation passes.

Final candidate runtime session `20261007-073849-Both` rechecked Offline zero
remote assets, female guarded fallback, male equipment/root assertions, abrupt
equipped timeout cleanup, fresh reconnect, original input-fixture expiry and
native crowbar equip/swing/CrowbarHit/unequip. Original identities stayed unchanged
before/during/after; global disconnected counts were all zero. Neither finalization
launch failed startup allocation. Both sessions closed under control, saves were
restored byte-for-byte and MilestoneB/trace/development controls disabled.

**Milestone B is ACCEPTED — TWO-GAME VALIDATION DEFERRED for continued development.**
The bounded crouch/backward/strafe/airborne/head/female presentation limits are
non-blocking. Future action animations expand with their gameplay systems.
Milestone C may now begin as a separately authorized task; it was not started here.
