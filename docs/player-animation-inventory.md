# Animation and equipment evidence

VERIFIED_RUNTIME: R7 complete 1,186-node player hierarchy, R8 complete NPC root and
R7 five vehicle visual sources. Clip names establish available assets, not acceptance
of every action on a remote rig. State-machine path names beyond the currently
sampled layer/hash are UNKNOWN; controller clip lists do not expose transition graphs.

## Player and camera separation

The genuine actor is `IMPORTANT OBJECTS[0]/First-Person Character[19]`, with
PlayerScript, PlayerMotorBehavior, PlayerCameraBehavior, CharacterController,
Rigidbody and colliders. Standing controller height 1.6, centre zero, radius 0.35,
skin width 0.01, step offset 0.05, slope limit 90; crouching changes height to
approximately 0.6000308. Root position is the simulation reference, not foot origin.

Camera path: `Tilt[3]/Head[0]/CamAnim[0]/Camera[1]`. HandsCam is the Head child
`HandsCam[1]`, culling mask 256 (layer 8). Main camera mask was -567104841, so do
not model it as merely “all except hands.” The auxiliary GotShot camera mask was 0.
Head tilt, bob, lean, FOV and CamAnim motion are first-person presentation.
VERIFIED_STATIC: HandleMouseLook rotates actor Y for yaw and camera local X for
pitch. Fresh automated pitch/yaw movement was not accepted in these sessions;
mouse dragging did not reliably drive locked-cursor gameplay. Root/camera mappings
are runtime-inspected, movement implementation is static plus earlier task evidence.

Hands hierarchy includes `HANDS/Nowe Łapy/ChangingItemL/LEWAREKA` and right
`ChangingItemR/PRAWAREKA`, `arms@throwing`, `L_wrist` and `R_wrist`. Exact suffixes
are in the ignored hierarchy; names here describe branches, not persistent IDs.
The original phone rendered in those hands during gameplay. Keep all local branches.

## Rig and clip coverage

| Semantic need | Actual source / clip names | Runtime interpretation and remaining gap |
| --- | --- | --- |
| Idle | Male NPC `BreatheIdle`, `Bored`, `Baseball_Idle`; player `idle` | BreatheIdle rendered/played on clean NPC rig; other clips inventoried |
| Forward walking | NPC `Walk`, `Walk2`; player `walking` | NPC live Walk state enabled/weight 1; sustained mod-controlled walking UNKNOWN |
| Run | NPC `Running`; player `run` | Compatible NPC source likely; remote transition/blending not tested |
| Backward walking | Player `walkBack` | Generic player rig only; NPC binding/retarget UNKNOWN |
| Strafe/turn | No proven dedicated NPC clips | UNKNOWN; procedural turn/blend candidate, needs visual test |
| Crouch idle/move | First-person `crouch_idle_Lhand`, `crouch_idle_Rhand`, `CrouchWalkCam` | These are hands/camera clips, not full-body crouch; full-body coverage UNKNOWN |
| Jump | Player `jump` | Generic player rig; NPC compatibility UNKNOWN |
| Fall/land | NPC `Tripping`, `KnockedOut`, `StandUp`; player falling is gameplay | Names do not establish jump landing coverage; UNKNOWN |
| Pickup | Hands `pickup_Lhand`, camera `pickUpCam` | Original pickup delays verified; full-body pickup UNKNOWN |
| Door/window | NPC `DoorOpen`; camera `ClimbWindow`, `GetOutCar`; player `Kick` | DoorOpen inventoried; window traversal/control is separate gameplay; remote full-body sequence UNKNOWN |
| Tool/carry | Hands crowbar/baton/blowtorch/binocular/laptop/gas/brick clips | First-person assets only; no validated generic NPC carry/lockpick/flashlight stance |
| Driving | Vehicle TPP `driving_front`, `driving_left`, `driving_right` | Eight-skin bodies, seated rig; enter/exit animation compatibility UNKNOWN |
| Phone/device | `phone_idle_Rhand`, `Idle_Rhand_laptop`, `hacker_laptop_circle` | Local presentation only; body action synthesis UNKNOWN |
| NPC context | WorkOn, Surprise, Sleep, Sitting2, SitUp, Sit, Scared, Looking, FallAsleep, Dance1, Aim_Spray | Inventoried legacy states; no claim all actions useful for player avatar |

Exact preferred NPC inventory (21): `WorkOn`, `Walk2`, `Walk`, `Tripping`,
`Surprise`, `StandUp`, `Sleep`, `Sitting2`, `SitUp`, `Sit`, `Scared`, `Running`,
`Looking`, `KnockedOut`, `FallAsleep`, `DoorOpen`, `Dance1`, `BreatheIdle`, `Bored`,
`Baseball_Idle`, `Aim_Spray`. The catalog preserves metadata per source, including
clip durations, legacy flag and human-motion flag where emitted.

VERIFIED_RUNTIME: NPC clips have legacy animation and humanMotion=false. The asset
`isLooping` flag was false, but actual `AnimationState.wrapMode` was Loop with
Animation.wrapMode Default. Use runtime state wrapping when reproducing NPC playback.
The clean test overrides its private Idle state to Loop. No Animator state machine
or root-motion flag exists on a legacy Animation component.

VERIFIED_RUNTIME: player legs Animator controller `man`, null Avatar, isHuman=false,
initialized=true, AlwaysAnimate, Normal update, applyRootMotion=false and
hasRootMotion=false. Parameters: isWalking, walkBack, isRunning, isJumping, Kick
(Bool), walkSpeed (Float, sample 1.1). Do not put that controller on a differently
bound NPC skeleton just because both look human. The flame Animator's root-motion
setting belongs to an effect, not the player-body movement policy.

## First-person clip inventory

Observed camera/FOV names include pickUpCam, WalkCam, TazedCamera, StopHide,
StartHide, RunCam, LeanRight, LeanLeft, JumpCam, IdleCam, GetOutCar, CrowbarCam,
CrouchWalkCam, CrashCam, ClimbWindow, WalkFOV, IdleFOV, EnterWindowFOV,
CrouchWalkFOV. Equipment/hand states include respirator equip/unequip,
drink_alcohol, crowbarBreakin, usingShovel, ItemSwitch/ItemSwitchB,
pickup_Lhand, sprint_Lhand, crouch_idle_Lhand, Lhand_none,
gastank_idle/sprint/turnoff_Lhand, tranquilizer_walk/sprint/shoot/idle_Rhand,
sprint_Rhand, phone_idle_Rhand, keyfob_sprint/idle_Rhand,
gastankturnoff/sprint/idle_Rhand, crowbarwalk/sprint/idle/attack_Rhand,
crouch_idle_Rhand, brickwalk/throw/sprint/idle_Rhand,
blowtorch_sprint/idle/blowing_Rhand, binoculars_walk/sprint/idle_Rhand,
Sprint_RHand_Laptop, Idle_Rhand_laptop, baton_expand and hacker_laptop_circle.
Grouped slash notation denotes separate captured names, not a new clip identifier.

## Replicated semantics and root-motion policy (design, not implemented)

PlayerReplicationState carries position, body yaw, optional look pitch, velocity,
planar speed, vertical velocity, grounded, crouched, sprinting, locomotion enum,
action enum plus action sequence/start tick, equipped definition ID, carried entity
ID and seat/vehicle state. Quantize and validate finite values. Never use raw Animator
hashes as the primary wire contract. They can remain optional debug metadata.

PlayerAvatarController maps this to a rig-specific legacy CrossFade adapter first,
or a mod-owned Animator only after verified binding/retargeting. NetworkRoot owns
movement. VisualRoot owns feet offset, scale and appearance; any animated root curves
remain below it and must be compensated or excluded. Runtime Idle left NetworkRoot
stationary, but sustained walk/run displacement and child-root drift are UNKNOWN.
Disable Animator.applyRootMotion on any future Animator backend; do not infer that
this alone neutralizes legacy transform curves. Remove clip events before playback.

## Definition IDs and equipment sockets

VERIFIED_RUNTIME: 452 ItemID definition references exist on PlayerScript. Examples
captured from live ItemID fields (names are the actual itemName, not localized UI):

| ID | itemName | ID | itemName |
| ---: | --- | ---: | --- |
| 0 | crowbar | 1 | blt |
| 2 | lockpick | 3 | flashlight |
| 4 | binoculars | 5 | glasscutter |
| 6 | stethoscope | 7 | hackpda |
| 8 | baton | 9 | climbing_gloves |
| 10 | automatic_lockpick | 11 | hacking_laptop |
| 15 | night_goggles | 16 | jewellery_tools |
| 19 | automatic_lockpick | 21 | atm_hacking_tool |
| 23 | car_lock_pickgun | 24 | blowtorch |
| 25 | cardboard_box | 26 | sleeping_gas |
| 27 | respirator | 29 | keyfob |

IDs 10 and 19 deliberately share an observed name; name-only lookup would collide.
Do not expand `blt` into an unverified tool name. Switching uses PlayerScript.currentItem,
CheckItemEnable and functional tool scripts with hand animations/effects. Durability
such as crowbarLife/batonLife/bltHealth resides partly on the player, not a generic
per-instance item record. Crowbar and flashlight weigh 0.1 each in the sampled inventory.

RemoteEquipmentVisualController maps HeldItemId to fresh Transform plus shared
mesh/material renderer(s), with only a sanitized animation backend if needed. It
must exclude functional item scripts, input, raycasts, cameras, damage/noise and save
logic. Attach to `mixamorig:RightHand` or LeftHand on the chosen NPC rig. Record
position/rotation/scale offsets per rig/item in mod configuration, separate from
replicated state. Socket offsets and visible held-tool tests remain UNKNOWN; local
R_wrist offsets are not portable to the NPC skeleton.
