# World interaction adapters and side effects

R7 interaction capture contained 186 Pickupable, 34 Door, seven Lockpick, six
LightSwitch, 25 Mission_Item, 33 SelectResidence and individual device/parcel/window
master records, plus enter/garage/fuse helpers. Its 300-record limit truncated the
global traversal. R8 DOMY capture contained 264 Pickupable, 13 Door, two Lockpick,
three LightSwitch, 17 Mission_Item and one Garage_Switch; also record-limited.
Counts describe observed records, not all objects or unique prefabs in the game.

## Representative runtime state

| Example | Live component / state | Methods and side effects from static inspection |
| --- | --- | --- |
| MadisonSt courier container Door_A | Door; isOpen=false, opening=false, canUse=true, npcUsing=false, rotateOpen=true, isWindow=false; cached PS | Use/NPCUse/UseFully and slow Update paths differ; transform rotation/movement, sound, local-player noise and camera sensitivity |
| DOM 110 Stash/Lid | Lockpick; observed difficulty 0.85 | StartLockpicking, DoorUnlock/RelockDoor/BreakPadlock; changes Door tags/enabled/requires_key/was_locked, colliders, openedLocks counter, optional story/noise |
| Dom 102 Light_switch (8) | LightSwitch with light/material/GM refs and powerOn/setActive/wasFlipped state | FlipSwitch toggles lightObject/materials and plays optional AudioSource; PowerOut/NPC_Entered_Room/daylight affect state too |
| DOM 110 authored `30$_c12q0q$` | Pickupable | Money/loot branch depends on fields; do not infer ID, reward or noise from object name |
| Dropped starter tool | Pickupable ID 0, wasDropped=true, non-heavy/non-money | Original PickUp coroutine and Item_Picked transaction verified separately |
| Courier GameObject (1) | Mission_Item, assigned residence/mission index | Completion gates on active mission pref, updates money/XP/pref/progress and presentation |
| World computer | UseComputer + PS/GM/UI/sit/stand refs | UsePC/device menus alter local input/cursor/pause/position, purchase/economy paths specialized |
| WindowOpenMaster | Runtime cutting/minigame manager observed | BeginCutting references LockedWindow and local player/tool/UI; not one generic window state |
| Garage/fuse/parcel helpers | Garage_Switch/FuseBoxSwitch/ParcelContainer observed | Specialized interactions; complete causal mutation trace UNKNOWN |
| Starter vehicle enter helper | EnterCar under PLAYER CAR | CarController EnterVehicle/ExitVehicle alter original actor/camera/input/seat parent |

VERIFIED_RUNTIME: courier door SavePos=(-2,0.1,-4.9), closed local quaternion
(0,0,0,1), OpenRot captured as (0,90,0,0), useVectorRotation=false and key flags
false. Preserve the authored rotation convention and inspect code before interpreting
OpenRot as a normalized pose. Door sound references select container open/close/slow.
None of these door/switch/lock action methods was exercised in the current safe fixture;
their causal side effects are VERIFIED_STATIC, not runtime-accepted interactions.

## Door/window logical model

VERIFIED_STATIC: Door carries rotateOpen/moveOpen, OpenRot/OpenMove, closed SaveRot/
SavePos, isOpen/opening/canUse/openingSlowly/npcUsing, speed, requires_key/only_with_key/
key_item_ID/was_locked and subtype flags (drawer, cupboard, fridge, file cabinet,
glass cabinet, container, gate, car door, window). Locks also use tags and separate
Lockpick/MadGoat components. One invented `Door.locked` bit would lose important state.

Use changes the cached PS.MakeNoise accumulator in branch-specific amounts, then
animates/plays sound. Lockpick.BreakPadlock changes physics/collider/tags and adds
noise; DoorUnlock can advance story and increment opened-lock stats once. Treat
lock outcome, eligibility and resulting collider/tag capability as gameplay state.
Opening progress/audio can be presentation from host state/start tick. Residence/
mission linkage is part of validation, not visual hierarchy alone.

VERIFIED_STATIC: LockedWindow has brokeGlass, canBreak, hasAlarm/starsAmount,
addNoise/noiseAmount, alertNPCs, tutorial/mission and window/enterable references.
BeginCut delegates to WindowOpenMaster and local PS tool setup. UnlockWindow/
BrokeWindow can trigger alarm/chase, mission completion, glass effects, tag/layer/
collider changes, enable Door/enterables and destroy the original component/window.
No direct universal prefName exists on LockedWindow or Lockpick; Door_ID is empty.
Durable restoration for each subtype is a separate UNKNOWN integration requirement.

VERIFIED_STATIC: WindowUse.UseWindow hides hands, disables local motor/camera,
changes crouch, starts local camera/audio and the delayed GetIn traversal through
Move1/Move2. Do not replay it on a receiving peer's cached local player. Its actual
world execution and relevant LockedWindow live component inspection remain UNKNOWN;
only the master manager and source architecture were inspected in these captures.

## Host integration design

Actions contain actor/entity/epoch/revision and request ID. Host validates reach,
tool/key/skill, current transition, residence rules, target availability and ownership;
applies once and publishes explicit logical result/revision. Receivers use dedicated
state/presentation adapters, not original local-player-bound callbacks. Audio and
glass/dust effects are separate from one host gameplay-noise/illegal-action event.
No XP/save/mission/economy award should be duplicated by presentation replay.

Start with one unlocked rotating door, one sliding/container door, one switch and
one lock/window fixture on a backed-up disposable save. Observe before/after state,
all alternate NPC/slow/forced paths and late-join reconstruction before expanding.
Source field presence does not prove runtime save durability or universal coverage.
