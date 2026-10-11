# Driver-owned native vehicle physics

Current implementation: **0.6.10 / protocol 12**. Milestone G remains unstarted.

## Finding and vanilla evidence

The inspected 0.6.9 implementation did **not** simulate Client driving locally:
`PhysicsStep` always called `ClientPhysics` on Client, `InputStep` admitted only
Host input, and `ApplyDriving` fed received Client input to Host EVP. Client
`ApplyState/Follow` made the body kinematic; `SendVehicleMotion` allowed only Host.

Inspection covered original `CarController`, `EVP.VehicleController`,
`EVP.VehicleStandardInput`, `SaveGame` and Unity `Collision/ContactPoint`.
Proprietary outputs stay ignored under `.local`. Native RunCar/exit own the dynamic
body and alarm child body lifetime. Native input owns drunk effects, reset/modifier
controls, forward/reverse dead zones and braking. EVP owns wheels, forces and
contacts. CarController owns fuel, audio, gauges, impact cooldowns, repair charges
and NPC reactions. No original assembly or handling/fuel/collision formula changed.

## Ownership and transport

`VehicleNetworkAdapter.Simulates/SyncAuthority` selects the confirmed local driver
on either peer; Host owns an unoccupied vehicle. `AuthorityGeneration` changes on
driver assignment/release or severe recovery. Passenger changes advance only
`OccupancyRevision`. Transfer seeds accepted pose and both velocities once and
restores original input/interpolation. A used vehicle stays dynamic after return
to Host, matching native post-exit behavior.

`SharedVehicleController.NativeSimulation/InputStep/PhysicsContact` suppress
replica EVP, input and contact processing. The local driver executes original
EVP/input instructions without network input substitution. Fixtures and existing
readiness neutralization retain their explicit scoped input source.

`Follow` keeps the fixed sender timeline/100-ms interpolation delay and never runs
on the active owner. Ordinary reliable seat baselines cannot move/disable that
owner; `ApplyMotion` ignores its echoed poses. Native replica speed/control fields
feed presentation, not forces. Host exit/travel checks use accepted motion speed
instead of a kinematic body's velocity.

`Tick` sends owner's native motion on sequenced channel 3 at maximum **20 Hz**,
with a one-second parked heartbeat. Production input packets are removed; motion
includes native control outputs for audio/gauges. Motion is **120 bytes** and the
reliable vehicle baseline is **79 bytes/version 3**: protocol 11 is incompatible.

`VehicleAuthority.AcceptMotion` validates authenticated actor, session, SceneEpoch,
live vehicle/entity generation, confirmed driver, authority generation, newer
sequence/tick, finite bounded pose/velocities/controls/fuel and broad displacement
and fuel-change envelopes. Valid motion normally becomes Host canonical state,
without reliable world revisions or replacement by delayed rendered positions.
This is lightweight validation, not rollback or a new anti-cheat system.

## Necessary runtime exceptions

All interventions are class **A: essential**, scoped to cooperative ownership.

| Intervention | Justification and restoration |
| --- | --- |
| Replica kinematic body and input/EVP/contact suppression | One simulator. Local ownership restores original input/interpolation; teardown restores captured native state. |
| Replica native engine/speed/control presentation | Original audio/gauges/storage lifecycle consumes accepted state without applying forces or passenger input. Owner computations remain native. |
| Replica fuel-store suppression and cumulative native consumption | Prevents duplicate consumption. Original Update runs on the driver; its observed before/after delta is applied once on Host despite packet loss, while retaining Host's own consumption. Other vehicle packets cannot overwrite an active Client tank. Existing Host saving remains in use; Client's original fuel restores on teardown. |
| Reliable Client collision consequences | Client captures the contact identity before native ragdoll callbacks. Host uses a bounded contact-local collider query and validates recent motion, authority/sequence, unique live target, proximity and plausible relative speed, including the preceding accepted speed after an abrupt stop. Original OnCollisionEnter executes with verified Unity contact fields, retaining native cooldowns, repair charge and NPC methods. Temporary seated state restores in finally. Host passenger tazing is skipped only during replay because it already ran on Client's driver. Contact physics still runs only on driver. |
| Severe recovery | Host advances generation and reliably sends last accepted pose with stopped velocities. Client reseeds once. Old-generation packets cannot cause repeated corrections; ordinary differences within the broad envelope are accepted. |
| Motion lease | Host sends canonical acknowledgements at most 4 Hz; owner ignores their poses. Client suspends after two seconds without acknowledgements. At 2.5 seconds of silence Host revokes the generation and requests exit, retaining replica physics until native detach confirms or the existing five-second exit reservation expires. Loading/sleep barriers refresh lease time. New authority clears suspension. |

Existing reserve/attach/confirm and detach/confirm paths remain. Exiting retains
the driver generation until release; passenger reservations do not suspend driver
physics. Disconnect removes the actor and advances generation. Reconnect inherits
no seat. Scene quiescing restores actors and clears bindings, leases and sequences.
The contact query retains no target cache. Disposal removes hooks/callbacks;
offline calls follow vanilla.

Rear-center geometry, original passenger camera/controller restoration, front
lootbox, native seated/door/engine access, 2.25-m storage ray, capacity/persistence
and synchronized inventory transactions retain the 0.6.9 work. Pre-existing
uncommitted changes were preserved. No new milestone or gameplay convenience added.

## Verification limits

The complete existing suite passed **770 checks** once near completion. The final
vehicle-focused run passed **186 checks**, including the subsequent bounded exit
revocation and impact-speed regressions. Release build and repository validation
passed. NuGet vulnerability metadata was unavailable (NU1900); cached compilation
succeeded with no errors.

Existing native driving fixtures passed **33 checks**, and rear passenger fixtures
passed **20 checks**. Native storage binding inspection retained the front box,
capacity 20 and original engine/door tag conditions. Four sequential native launches
used at most **5 minutes 17 seconds total**, each under five minutes; initial runs
exposed fixture setup issues that were corrected. No two native games were launched.
Each session restored saves, PlayerPrefs and configurations; all **183 original
game binary hashes** remained unchanged. Installed multiplayer DLL hashes were
checked against the final build. Collision relay and the last lease refinements
received source/focused verification, without another native launch.

Focused managed checks cover driver authorization/generation, passenger revision
independence, duplicates, severe recovery, silence, impact codec, both UDP driving
directions, scene/reconnect lifecycle and impaired transport. Existing native
fixtures put the genuine vehicle/player on the driving peer in each role, with a
managed observer and explicitly synthetic local axes; physics uses production
NativeSimulation. This does not constitute two native games or human acceptance.

Real two-player driving, sustained handoffs/partitions, collision repair/police/NPC
consequences, remote audio/wheels, simultaneous vehicles/fuel, and passenger/storage
comfort under latency remain unverified. Collision targets must still resolve
uniquely and be near contact on Host; independently moved/spawned NPCs can fail
validation. Immediate collision relay does not implement a new NPC/security
milestone. No FPS/high-ping or full vanilla-equivalence claim follows from this work.
