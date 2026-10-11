# Runtime avatar candidates

Evidence: R7 global avatar capture (124 candidate records, global traversal
truncated), R8 complete `NPCs[0]` capture (13,017 objects, 101 component records),
R7 offline visual experiment and complete player hierarchy. Session keys and
capture names are in [full-runtime-recon.md](full-runtime-recon.md). A candidate
record is an observed source, not a verified prefab identifier. Original prefab
GUIDs and asset paths remain UNKNOWN. [avatar-catalog.md](avatar-catalog.md)
contains concise per-source rig, mesh, material and clip metadata.

## Selection

| Candidate / runtime source | Full body / rig / hands | Animation | Dependencies | Scale/origin/forward | Suitability / confidence |
| --- | --- | --- | --- | --- | --- |
| Ordinary man, `NPCs[0]/105[6]/NPC[7]`, AI, `NPC_Man105` | 7 skins, complete Mixamorig skeleton, both hands/fingers/feet; 89 transforms | Legacy `Animation`, 21 clips; no Animator/controller/Unity Humanoid Avatar | AI, RichAI, Seeker, HighlightEffect, colliders/Rigidbody, sensors, pickpocket/audio descendants must be excluded | Unit root scale, local +Z forward; feet need VisualRoot offset from player centre | Preferred; VERIFIED_RUNTIME rendering/idle test |
| Ordinary woman, `NPCs[0]/112[13]/NPC[5]`, AI, `NPC_Woman112` | 8 skins; body 50 bones, hands/head/feet and spine chain present | Legacy NPC clips; no Animator/controller | Same single-player AI/navigation dependencies | Unit scale; independent origin calibration needed | Best female alternative; VERIFIED_RUNTIME inspection, visual spawn UNKNOWN |
| Other female, `NPCs[0]/112[13]/NPC (1)[3]` and `109/NPC[2]` | 7 skins in observed sources | Legacy animation | AI/navigation/persistence | Unit scale in captures | Alternatives, VERIFIED_RUNTIME metadata; no spawn acceptance |
| Special people, e.g. `108/NPC[1]` (`NPC_Weirdo`), `101/NPC[2]` (`NPC_Harry`), `101/NPC[3]` (`NPC_Julia`) | 9 / 7 / 7 skins; human anatomy | Legacy clips; variant-specific coverage in catalog | Story/habit/AI dependencies and distinctive appearance | Capture-specific root scale/origin | Technically plausible, ordinary citizens preferred; LIKELY suitability |
| Police `MANAGERS[0]/POLICE MANAGER[7]/POLICE[0]/Policeman[0]`, `Policewoman[4]` | 8 skins each; full human skeleton/hands | Legacy police animation, no Animator | PolicemanAI, perception, weapon/arrest/chase and manager relationships | Root scale 1.075; inactive templates | VERIFIED_RUNTIME templates; rendering and pursuit UNKNOWN; unnecessary visual specialization |
| GuardAI | Type and legacy animation/NavMeshAgent verified statically | Guard state/clip usage static only | Cached player, navigation/detection | UNKNOWN live rig/origin | UNKNOWN runtime candidate; absent from complete loaded NPC root, other areas not exhausted |
| Sidewalk_Pink / sidewalk people | Observed human sources with 8 skins | Legacy animation | SidewalkAI, spawner, audio/navigation | Example scale 1.05, inactive | Possible fallback; VERIFIED_RUNTIME metadata |
| SidewalkAI on `MyCar*Clone` | Vehicle plus driver geometry, 7–9 skins | Vehicle/passenger context | Traffic/navigation/physics | Example scale 1.2 | Do not select by AI class alone; poor extraction boundary |
| Local `LEGS[36]/Main Character Legs[0]` and hidden body branches | Game-owned skinned body geometry, hand bones in hierarchy | Generic Animator `man` | Original player scripts/presentation | Legs scale 1.111 | VERIFIED_RUNTIME; a controller on this rig does not prove NPC retargeting |
| Pause previews `Main Character Final[13]`, `new main guy[15]` | 8 skins, Animator | Preview controller | UI hierarchy/canvas | Scale 1.459, canvas coordinates near (960,540) | Avoid world-space cloning; VERIFIED_RUNTIME UI dependency |
| `CarController.tpp_character` in five cars | 8 skins per inspected TPP body | Legacy `driving_front`, `driving_left`, `driving_right` | CheckItemEnable/vehicle scripts; seat context | Player car 0.84; Mustang nonuniform (0.98,0.92,0.92) | Driving presentation source only, VERIFIED_RUNTIME metadata |

Gender classifications use live `isMan`/`isWoman` fields, not names. Dogs in the
catalog are excluded from humanoid selection. No loaded-source enumeration proves
coverage of every unlockable map, costume or prefab in the entire game.

## Preferred rig

VERIFIED_RUNTIME: the preferred male has `Anim/mixamorig:Hips`, spine → Spine1 →
Spine2 → Neck → Head; both Shoulder → Arm → ForeArm → Hand chains; finger chains;
both UpLeg → Leg → Foot → ToeBase chains. The body skin has 49 bindings. Counts
below are per renderer, not unique skeleton totals.

| Mesh | Bone count | Root bone | Material/shader |
| --- | ---: | --- | --- |
| Body | 49 | Spine2 | man_Body_diffuse / Standard |
| Bottoms | 11 | Hips | man_Bottom_diffuse / Standard |
| Eyelashes | 1 | Head | Rzesy / Standard |
| Eyes | 1 | Head | Captured game material; see catalog |
| Hair | 3 | Spine2 | man_Hair_diffuse / Standard |
| Shoes | 7 | LeftUpLeg | man_Shoes_diffuse / Standard |
| Tops | 18 | Hips | man_Top_diffuse / Standard |

Both hands are real skeleton attachment candidates. Neither a Mixamorig name nor
human anatomy establishes Unity Humanoid compatibility: these NPCs have no
Animator/Avatar. Full bone names and per-source meshes are metadata in the catalog;
no vertices, bind-pose arrays, animation curves or textures are redistributed.

## Safe construction and lifecycle

VERIFIED_STATIC: original NPC roots contain gameplay MonoBehaviours whose
Awake/OnEnable/Start and tag-based manager lookup are unsuitable for a remote
presentation. Instantiating an inactive NPC still risks Awake. Stripping it after
instantiation is therefore not the selected strategy.

VERIFIED_RUNTIME: `AvatarExperiment` creates fresh GameObjects/Transforms, remaps
all skeleton references, and adds only SkinnedMeshRenderer plus one legacy
Animation component. Shared meshes/materials remain game-owned references inside
the running process. A private copy of `BreatheIdle` has all AnimationEvents
removed and explicit Loop wrapping. Every object is Untagged, layer 0; no original
NPC root is instantiated. Failures destroy the partially constructed visual.

The source's seven renderers were all disabled by distance culling. Copying that
flag produced an invisible first experiment in R6. The successful R7 test explicitly
enables its own renderers and updates skins when off screen. This fixes a concrete
extraction problem without altering source NPC culling.

At 21:10:13.482 UTC the single visual spawned 3 m ahead of the genuine player,
facing the camera (player yaw + 180 degrees). At 21:10:15.484 its audit reported
99 components, seven visible skins, playing Idle, and only Transform,
SkinnedMeshRenderer and Animation. Feet minimum Y 45.881 versus ground 45.880.
Screenshot inspection verified full body, normal skinning, materials, head direction
and plausible scale. NetworkRoot remained stationary. Original first-person hands
were observed before the test and remain in the subsequent complete player capture.

No AI/navigation/camera/audio/input/collider/save/inventory component was created.
Untagged objects cannot enter the observed tag-based NPC/player registration path.
This is VERIFIED_RUNTIME structural isolation; an exhaustive audit of every private
manager collection is UNKNOWN. Production locomotion, crouch, equipment, networked
spawn and two-peer visibility are also UNKNOWN. The experiment does not change the
production Phase 1 proxy.

## Production boundary

Use NetworkRoot for replicated position/yaw and a separate VisualRoot for scale,
feet offset and appearance. Preserve the original local player, cameras, arms and
functional tools. Remote equipment and animation must be presentation adapters
owned by the mod. Validate sustained locomotion/root-curve drift and event removal
for every copied clip before enabling it. See [animation inventory](player-animation-inventory.md).
