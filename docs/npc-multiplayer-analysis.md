# NPC, police and perception architecture

## Live ownership and AI

VERIFIED_RUNTIME: complete R8 `NPCs[0]` capture includes ordinary male/female,
special citizens and dogs, including inactive inhabitants. AI holds one PlayerScript,
one player Transform and GameManager references; RichAI/Seeker provide pathfinding.
Live fields expose habit/persistence names, sex/dog flags, AI state, destinations,
animation references and door/sound relationships. A sampled RichAI destination was
(572.7,55.8,303.3), velocity zero, isStopped=false, pathPending=false and
remainingDistance=Infinity. Do not serialize Infinity as a finite gameplay pose.

VERIFIED_STATIC: AI.Start resolves the tagged PlayerScript and caches references.
NPCHabits advances schedule/habit actions using GameManager time and residence
preferences; GameManager scans/registers tagged NPCs; SidewalkSpawner creates
random sidewalk sources and SidewalkAI operates both pedestrians and traffic.
Door.Start, Sensor_Range.Start, GuardAI, PolicemanAI and many interactable/device
scripts also cache one player. Adding a second Player-tag object would not make
these systems select multiple targets and could change which player gets cached.

The first-person player, pause inventory, NPC scripts and GameManager form a graph
of single-player references. Known adaptation boundaries:

| Boundary | Existing assumption | Future treatment (design) |
| --- | --- | --- |
| AI.Start / player cache | One tagged PS and player Transform | Host multi-player target service; do not instantiate PS for remote visuals |
| Sensor_Range.Start / ray tests | One player target, game layers/tags | Host evaluates both validated actors; explicit target ID |
| AI range/seen state | Distance and sight of cached player | Per-actor visibility/trespass state, selected target and threat |
| AI.HeardSoundAction(Vector3,bool,float) | Event location plus global/context flags | Host validated noise with responsible actor ID |
| NPCHabits / GameManager | One clock, residence arrays, save preferences | Host world schedule, reliable schedule/interaction outcomes |
| Door.NPCUse | Shared door and cached single local PS side effects | Host interaction result; client presentation avoids original Use replay |
| SidewalkSpawner / SidewalkAI | Local random spawn order and navigation | Host spawn IDs, pedestrian/traffic snapshots |
| GuardAI | Cached player and NavMeshAgent, legacy animation | Separate guard adapter; runtime rig/navigation acceptance still needed |
| PolicemanAI / Police_Manager | Cached PS, global pursuit/last location | Explicit multi-player chase targets; host-only simulation |

This identifies observed/static single-player boundaries, not every occurrence in
the entire proprietary source tree. Complete loaded NPC inspection does not establish
behavior in unloaded Bank/Warehouse/other areas. GuardAI was not found live in that
root or the bounded global capture. See minimum fixtures below.

## Police

VERIFIED_RUNTIME: Police_Manager in MainScene has current_stars=0, a cached PS,
last-known location near the genuine player, five spawn references, empty spawned
policeman/car lists and null NPCWhoCalled. Inactive Policeman/Policewoman visual
templates exist with eight skins and legacy animation. They are source templates,
not an observed live chase. Active spawning, escalation, pursuit, arrest and cleanup
were not provoked during safe reconnaissance.

VERIFIED_STATIC: Activate_Chase/spawn and PolicemanAI paths drive navigation,
player detection, search/catch and cleanup. Update_Last_Location and other pursuit
calculations read the cached player Transform. A future single host simulator is
LIKELY feasible, but replacing target resolution is required; suppressing only Update
on a client leaves Start, coroutines, sensors, triggers and save callbacks running.
Clients need clean police/NPC presentation, never a second uncontrolled authority.

Recommended per-actor illegal-action/wanted contribution ledger feeds the host's
world response, with selected target actor IDs, last-known positions, search areas,
AI revision and spawn/despawn outcomes. Whether the game supports per-player stars
without broader modifications is UNKNOWN. Do not promise multi-target chase by merely
changing one player reference.

## Perception, audio and illegal actions

VERIFIED_STATIC: PlayerScript.MakeNoise is a float accumulator, not an action
method. Its value and movement/weight modifiers produce
gameplay hearing inputs; AI's sound action updates reactions/navigation. Sight/range
logic includes distinct thresholds (such as 0.7/0.4 in examined branches), sensors,
trespassing/seen state and police activation. These values are branch-specific,
not a universal hearing radius. Ordinary AudioSource playback alone cannot represent
illegal-action perception. `Soundwave` in the examined source animates a material's
UVs and is not the authoritative hearing service despite its name.

Proposed GameplayNoiseEvent: actor ID, epoch, action/transaction ID, validated world
position, noise category and effective radius/loudness. Host derives intensity from
its known movement/tool/state, evaluates witnesses and emits reactions once.
Replicated audio/particles are presentation and must not call hearing again.
Vision events similarly contain observer entity and actor ID, validated on host.

UNKNOWN runtime: a deliberate theft witness, forcing a door, hearing event, chase
transition and arrest were not exercised. Exact targeting and reward side effects
need backed-up scripted fixtures before implementing adapters.

## Minimum additional fixtures

- Load an existing legitimate save in a guard area and stand beside a stationary
  guard; capture `ai` and avatar metadata before any detection.
- On a disposable backed-up save, stand in visible sight of one citizen and perform
  one controlled illegal action; snapshot MakeNoise and trace HeardSound/alert/wanted changes.
- On that disposable save, provoke one short police response, then collect spawn,
  selected target, search/catch and cleanup transitions without progressing the main save.

These are navigation/setup actions only. Diagnostic scripts perform inspection and
log collection afterward. Current UI automation could operate menus/crouch/drop,
but sustained movement and locked-cursor aiming were unreliable; all other accessible
inspection was completed first.
