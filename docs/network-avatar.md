# Production network player avatar — Milestone B

Mod **0.3.0**, protocol **2**, 7 October 2026. This milestone implements remote player presentation. Full-sync cooperation remains the goal; Milestone C has not been implemented. The final evidence and decision are in [milestone-b-acceptance.md](milestone-b-acceptance.md).

**Milestone B: ACCEPTED — TWO-GAME VALIDATION DEFERRED.**

**TWO-GAME ACCEPTANCE: DEFERRED**, a separate gate under the project decision. The [future test plan](two-game-acceptance-plan.md) first attempts two legitimate normal local processes, then separate legitimate PCs if unsupported or unstable. Harness validation does not replace that test.

## Permanent local-player invariant

**THE LOCALLY CONTROLLED PLAYER ALWAYS USES THE ORIGINAL THIEF SIMULATOR 2 FIRST-PERSON GAMEPLAY STACK.** This includes PlayerScript, PlayerMotorBehavior, PlayerCameraBehavior, CharacterController, original camera/listener, hands, arms, functional tools, interaction logic, inventory, animations and input handling. Production multiplayer observes and replicates this stack. RemotePlayer / NetworkAvatar presents OTHER peers only. Offline play must retain this invariant.

## Production boundary

RemotePlayerProxy owns the transform-only `TS2MP_RemotePlayer_<NetworkPlayerId>` NetworkRoot. VisualRoot owns model facing/height corrections. Beneath it, PlayerAvatarController owns AvatarVisual, AvatarRig, animation and equipment. The factory constructs a fresh hierarchy rather than cloning a functional actor. Diagnostics fixtures are not referenced by production.

The whitelist is Transform, SkinnedMeshRenderer, Animation, MeshFilter, MeshRenderer, Untagged/layer 0. Validation precedes activation, including tools. AI, nav/sensors, police, PlayerScript, Rewired, Camera, AudioListener, inventory, save/mission/interaction logic and functional tools are excluded. Cosmetic failure logs its reason and uses a capsule without interrupting networking.

Only the remote interpolation controller writes NetworkRoot world position and yaw. Feet, model-axis, equipment and future look rotations belong below VisualRoot. Development assertions compare root position/rotation around presentation updates; unexpected mutation is restored by the controller and fails the cosmetic presentation. BodyYaw is never adjusted for model facing. LookPitch remains independent and never rotates NetworkRoot pitch.

## Identity and game-owned resources

| AvatarId | Source metadata | Current acceptance |
| --- | --- | --- |
| 0 | NPC_Man105, group 105, object NPC, seven skins | VERIFIED_RUNTIME: preferred male; clean rig, binding/materials, feet, facing, Idle/Walk/Run and cleanup |
| 1 | NPC_Woman112, group 112, object NPC, expected eight skins | LIMITED: real production request failed rig signature validation and fell back; female playback is not accepted and is non-blocking |

Habit keys alone are ambiguous; discovery also checks group/object identity. Only AvatarId is transmitted; scene paths and Unity instance IDs remain internal metadata. Discovery caches reset at scene boundaries. Destroyed objects and changed/ambiguous signatures fail safely.

Meshes, remapped bone/rootBone bindings, blend weights and shared materials come from the installed game. The accepted male's BreatheIdle, Walk and Running legacy clips become private runtime Idle/Walk/Run copies with every AnimationEvent removed. Only private copies and mod-owned temporary bake/fallback resources are destroyed. Shared assets retain game ownership. No game asset or assembly is distributed; build references use Private=false.

## Protocol and local observation

Protocol 2 carries AvatarId in the handshake and message 7's fixed **52-byte** PlayerState packet. Protocol 1 codecs/tests remain for explicit rejection; legacy transforms cannot enter the protocol-2 presentation buffer.

PlayerTransform carries position, BodyYaw, player/scene identity, sequence and tick. PlayerReplicationState adds world velocity, grounded/crouched/sprinting flags, semantic locomotion, LookPitch, equipped definition ID and ActionState Unknown. Planar/vertical/body-relative velocities are derived. SampledFields marks reliable fields; absent bits mean unknown. Validation covers finite values, enums/flags, pitch ±80°, velocity components ±100 m/s and equipment IDs -1..451. Gait/equipment follows the same delayed timeline as transform interpolation.

LocalPlayerAdapter reads original controller velocity and verified motor isMoving/isRunning/isGrounded/isCrouching flags. Semantic selection prioritizes those flags with **120 ms stop hysteresis**, below the **150 ms crossfade**. Measured speed does not replace game semantics. Original camera pitch reaches **-75° up / +75° down**; neutral is zero. Existing ±80° validation is sufficient. Body yaw and camera pitch are independent.

`currentItem` is a first-person slot, not ItemID. EquipmentSlotMap maps verified slot 1 to crowbar definition 0, slots 0/99 to none, and every other slot to UNKNOWN. Normal mouse-wheel switching and original CrowbarHit execution were observed. The real outgoing stream carried None → Crowbar → None.

## Animation, equipment and limitations

An initialization-only sanitized idle bake measures sole minimum against standing controller centre minus height/2. The male correction is approximately **-0.7284 m** under VisualRoot. Renderer culling bounds are not sole measurements. Cached ancestor/root transforms and hip X/Z translation reset after legacy animation; cyclic hip Y remains. Idle/Walk/Run crossfade without restarting the same clip every packet. Crossfade stays 150 ms; no evidence justified retuning.

| Feature | Presentation status |
| --- | --- |
| Idle / Walk / Run | VERIFIED_RUNTIME: compatible civilian clips and sustained transitions |
| Crouch | LIMITED: semantic CrouchIdle/CrouchMove retained, visual Idle/Walk |
| Backward | LIMITED: correct world/body-relative velocity, forward walking clip |
| Strafe | LIMITED: correct velocity; no accepted dedicated left/right clips |
| Airborne | LIMITED: semantic support, idle visual; player jump compatibility unverified |
| Remote LookPitch | LIMITED: replication verified; head/spine rotation disabled pending rig-axis/reset acceptance |
| ActionState | UNKNOWN: no invented tool attack/use state |
| Female | LIMITED: guarded fallback; gait/socket calibration not accepted |

The accepted civilian's 21 legacy clips contain no proven full-body crouch, backward, strafe or jump coverage. [Player animation inventory](player-animation-inventory.md) and [source catalog](avatar-catalog.md) distinguish these from first-person hand/camera and generic player clips. Bone names alone do not prove hierarchy, bind/rest pose, scale, orientation and legacy binding compatibility. No unsafe retargeting, new proprietary clips or speculative head-axis rotation is enabled. These are presentation limits rather than changes to local gameplay.

Crowbar presentation rebuilds only original CrowbarObject MeshFilter/MeshRenderer assets and source-relative child matrices under mixamorig:RightHand. ItemMaterialChange and gameplay scripts are excluded; shared materials avoid instantiation. Preserve native scale **0.385**, then accepted male rig/item offsets. Female equipment is hidden until its own calibration passes; male offsets are not applied to another rig.

None/unknown removes equipment. Disconnect destroys equipment, avatar, three private clips and hierarchy. Scene boundaries reset source discovery and player acquisition. No remote avatar asset construction occurs Offline.

## Development acceptance tools

Raw evidence remains ignored under `.local/`. Runtime sessions back up saves, collect logs/memory and restore saves byte-for-byte after controlled close. Original DLL/EXE fingerprints are separate from deployed mod hashes. Base-game allocation failures and system commit pressure remain startup evidence, not automatic attribution to avatar code. Pagefile settings are unchanged.

Diagnostics-only `[MilestoneB] Enabled=true` accepts PID-bound requests with finite bounded axes, at most 60 seconds, original-player readiness and pause checks. Harmony feeds existing Rewired getters only for the original player. It does not write transforms, motor flags, camera fields or inventory. Expiry, pause, scene boundaries and unload remove its patches. Evidence is labelled **DEVELOPMENT INPUT FIXTURE**, never human-driven. Native tool switching/clicks are labelled **NORMAL GAME INPUT** separately.

```powershell
./tools/runtime/milestone-b-fixture.ps1 -Session <local-session> -Action input -Seconds 6 -Forward 1
./tools/runtime/milestone-b-fixture.ps1 -Session <local-session> -Action input -Seconds 6 -Forward 1 -Run
./tools/runtime/run-avatar-soak.ps1 -Session <local-session>
```

The soak uses ten 180-second connections with synthetic incoming gait/yaw/pitch/equipment, real local observation and connected/disconnected audits. It is lifecycle evidence, not two-game input acceptance.

```powershell
./tools/runtime/start-avatar-harness.ps1 -Role client -Sequence avatar -Seconds 120 -PhaseSeconds 8
./tools/runtime/start-avatar-harness.ps1 -Role host -Sequence avatar -Seconds 150 -PhaseSeconds 8
```

`-Sequence equipment -PhaseSeconds 30 -Seconds 100` starts equipped for cleanup; `-AvatarId 1` exercises the female candidate. Fixtures adopt the real scene and anchor bounded synthetic motion ahead of the player. Disable development controls, MilestoneB input and traces after QA. Production updates use cached references, with no per-frame catalog scan or mesh baking.

Recommend Milestone C only after the final decision. Do not automatically begin registry/world/gameplay synchronization.
