# Shared vehicle and local passenger synchronization

Current **0.6.10 / protocol 12** implements [driver-owned native physics](driver-owned-vehicle-physics.md).
The historical Host-only motion/input architecture below is superseded.
The retained **0.6.9 / protocol 11** work replaces the historical rear-right calibration below
with verified rear-center references and preserves native storage access. See the
[current source findings, exceptions and verification](vanilla-first-vehicle-storage.md).
Earlier native render/seat-cycle evidence below does not validate the new center pose.

Mod 0.6.5 / protocol 8. Required within Scene Continuity, Automatic Travel and Late-Join;
Milestone F remains unstarted. [Acceptance](scene-continuity-acceptance.md) records native
coverage and unresolved gates.

## Native findings and bindings

Local reconnaissance inspected CarController, EVP.VehicleController/VehicleStandardInput,
VehiclesManager, EnterCar, PlayerScript, motor/look, GameManager, SaveGame, PassTime, Bed,
EnviroSky/Core and DayNightTime. Proprietary outputs stay ignored. CarController has one
local driver path; native entry/camera/engine/asynchronous exit cannot simply run twice
for a passenger. Authored associateSitPos is not a functional local passenger controller
and is no longer the cooperative seat: runtime geometry showed a centered, low reference
in the main car. The front passenger lootbox remains native and untouched. EVP speed is
a property; its verified getter is cached as a delegate.

Bindings are limited to active VehiclesManager.firstCar and van, with native body/EVP/input,
descendant driver/exit anchors and a distinct named rear-right passenger anchor. Passenger separation must
be 0.25–3 metres, with forward directions within 60 degrees. Unknown/missing contracts
fail closed. No universal world-space seat offset exists. Host variant/availability are
part of the descriptor and scoped native vehicle-manager initialization. Both variants
now have controlled native seat/motion evidence; sustained two-machine behavior remains
a separate gate, and anchor guards alone are insufficient.

RearPassengerSeat verifies the Car04/Car05 interior mesh contract before creating one
plain TS2MP_RearRightPassengerSeat and one TS2MP_RearPassengerEyes child. Rebinding reuses
these references; scene unloading owns their destruction. No physics/input component is
added. Readable interior triangles, native driver reference and sampled driving_front
hips/head establish the vehicle-local calibration. Car04 rear cushion centre is
(.526,.156,-.491), front cushion height .191; Car05 rear cushion centre is
(.345,-.092,-.747), front cushion height -.235. Reference height transfers the native
hips/cushion clearance. Car05 also compensates the sampled .1046 forward hips offset.
The main-car rear camera uses local Y=1.05; the second model uses Y=.57, derived from
sampled head pivots and cushion displacement. These are model contracts, never world
offsets. Original-camera renders of both active models are inspected. Missing contracts disable passenger capability rather than falling
back to the front lootbox or associate reference.

## Occupancy and interaction

VehicleState is a 55-byte reliable version-2 payload: independent OccupancyRevision,
Driver/Passenger, reservations, exiting flags, capability, baseline pose and linear
velocity. Authenticated session/SceneEpoch/EntityRef generation scope the actors. The wire
contains no Unity references.

Enter → Host reservation → native local attachment → Confirm → seated.
Exit → Host marks exiting → native detach/controller restoration → ExitConfirm → outside.
An exiting actor retains the seat until confirmation or its five-second timeout. Pending
or exiting occupants cannot authorize travel or driving. Duplicate requests and fresh
repeat exits create no extra occupancy. Host-local mutation executes even without result
listeners. Host validates live capability, proximity, eligibility, actor uniqueness,
readiness, revision and epoch. Remote confirmation requires a recent authenticated pose
and expected seat identity. There is no human approval for ordinary Client seat requests.

Disconnect removes the departing actor/input; remaining reservations keep bounded expiry.
A 350-ms input deadman brakes. Scene changes reset every occupancy/input/native reference.
An arriving actor never inherits a seat, even if Host occupancy appears in its baseline.

Natural entry uses original Rewired interact near an available verified seat and an
availability prompt. Either peer can take either role. Passenger interact exits after
Host validates low speed and clear grounded capsule space. Its exit mirrors the authored
driver exit across the centreline. Native driver entry/exit remain in use. The native
exit follows the Host safe-exit check. ExitCarPolice bypasses its first door check, but
ExitVehicle still checks obstruction; a three-second recovery bound prevents a stalled
native exit from permanently disabling the original first-person controller.
Ground and capsule probes ignore only the verified vehicle's own colliders, including
the door native exit opens. Other actors/world obstacles, steep/nonexistent ground and
probe-buffer overflow deny exit. Probe buffers are cleared after use and retain no old
scene references.

When only one seat is available, interaction selects that role regardless of which
anchor is nearer. Reserved, exiting and unavailable seats are excluded. A passenger
cannot reuse the entry frame's Interact pulse as an exit request. Seat reservations
still pass canonical Host validation; local availability is only interaction guidance.

Client input retains one entry/exit intent for at most two seconds. Only a StaleRevision
response schedules a fresh validated request after 250 ms. Scene/readiness loss,
disconnect, dead bindings, timeout or any other result clears it. Seat conflicts and
eligibility denials never become a hidden reservation. Confirm/ExitConfirm already use
their bounded reconciliation loop. This closes the race between a single Interact press
and the peer receiving the newest occupancy delta.

Native driver entry captures the surviving original actor, motor/look fields, cameras,
colliders, control UI, equipment selection and door tags. After native RunCar parents
the actor, a plain Transform outside the car's destruction hierarchy follows the same
authored seat. Native look retains its seat-relative frame. Normal exit keeps its native
sequence; a failed/stalled exit restores the snapshot. Scene invalidation restores before
releasing bindings. Already-seated Host startup uses verified native exit defaults where
no pre-entry snapshot exists. It cannot reconstruct unknown pre-entry equipment state.

Remote driver physics mirrors native RunCar's removal of the alarm child Rigidbody and
native ExitCar's kinematic recreation when the driver leaves. This lifetime follows
occupancy, so an engine pause or input deadman does not reintroduce a child physics body
while the driver remains seated. The Host passenger never runs native RunCar. This does
not synchronize vehicle damage, upgrades, alarm gameplay or other future vehicle systems.

Destroyed native actors/cameras are not cloned. Lost seat/control bindings restore the
surviving occupant and clear or request release of occupancy without failing the whole
scene. Native driver exit deliberately leaves CharacterController disabled after
is_in_vehicle becomes false; that bounded native exit state preserves actor availability.
Vehicle existence depends on native destruction/scene lifetime, separately from active
state, occupancy and operational anchors. Only verified native destruction creates a
vehicle tombstone. Stable identity uses the verified VehiclesManager.firstCar/van slot,
so native actor reparenting cannot change the vehicle key through root sibling indices.
Reinitializing a destroyed original camera or whole
vehicle outside a legitimate scene boundary remains an explicit implementation blocker.

## Original passenger first-person stack

LocalPassengerState preserves original PlayerScript, CharacterController, motor/look,
inventory/equipment, camera hierarchy, hands and AudioListeners. It snapshots flags,
enabled states, colliders/renderers, parent/rotation, camera pose/near clip and internal
look state before scoped suspension. Collisions/motor/look are disabled and active
passenger PlayerScript.Update is suppressed; passenger driving remains disabled. Original
inventory UI remains available through the passenger handler.

Each LateUpdate sets seat-relative actor and original-camera poses with bounded look.
The eye reference is independent of the on-foot eye offset and vehicle scale, preventing
the old standing-offset camera from being embedded at the low associate reference.
Attachment still updates without gameplay input while a sleep/readiness barrier is closed.
The player retains its original lifetime parent: destroying a seat/car cannot implicitly
destroy PlayerScript as its child. Near clip is temporarily at most 0.03 m and restores
exactly. Exit restores original state and a safe position, with entry origin as exceptional
fallback. No functional player, inventory, camera or listener is cloned. Missing essential
native references close readiness and restore still-live objects safely.

An original-camera rendered preview shows the rear-right passenger interior. Native checks
cover bounded placement, original identities and restoration. All variants/view angles,
equipment/pause combinations and real passenger comfort remain separate acceptance gates.

## Motion and remote occupants

| Channel | Delivery | State |
| --- | --- | --- |
| 0 | Reliable ordered | Scene/seat lifecycle, world baseline/deltas, shared time |
| 1 | Sequenced | Player state |
| 2 | Sequenced | Bounded active driver input |
| 3 | Sequenced | Host vehicle motion at 20 Hz |

The fixed 86-byte motion packet includes session/epoch/entity/generation, sequence/tick,
pose, linear and angular velocity. Motion does not increment reliable world revisions.
Only Host runs native EVP physics. Cooperative native input writers are suppressed; only
the confirmed driver's validated input reaches steering/throttle, opposite-direction
service brake and handbrake. Host passenger input cannot steer. Remote driving makes the
Host body dynamic without running native RunCar on its passenger. Client is kinematic,
EVP physics is suppressed, and cached samples interpolate with bounded extrapolation and
large-divergence correction. Passenger follows the resulting seat. Damage/upgrades/cargo/
fuel/economy remain outside this slice. Original Rewired getter delegates are cached.

NetworkAvatar selects the sanitized game-owned driving rig at canonical driver/passenger
anchors. It has no functional controllers/scripts/cameras. Outside/exiting actors return
to normal presentation. No standing avatar substitutes for a local functional passenger.
Native fixtures construct both seated rigs; full visual clipping/animation quality is
separate from functional authority.

Native driving fixtures use real EVP with localhost transports. Continuity fixtures use
production seat validation/entry/exit and original sleep/scene methods. Their second
actor/manifest is managed; neither is a second native game or human driving acceptance.

Historical 0.6.3 MadisonSt EVP fixtures passed 24 assertions for both roles, including stopping,
Host passenger attachment and original controller/inventory/camera restoration. Earlier
Client stopping failed; final setup uses authored driver placement and native door-bypass
exit. The exact earlier failure cause remains unresolved; repeatability and all exceptional
native combinations remain acceptance gates. Passenger and continuity fixtures separately
completed 20 and 29 assertions. Full evidence is in the acceptance report.
The 0.6.4 follow-up has 696 automated checks, native driver-recovery and revision-aware
continuity evidence. Stricter wheel-contact and final original-input cycle acceptance
remain open; see the separate follow-up section rather than treating earlier counts as
final native acceptance of the current source.

The 0.6.5 follow-up runs the original van through authoritative native initialization
(variant 1/available) without writing the saved vehicle choice or cloning player/vehicle
controllers. Main car and van each pass twenty alternating seat cycles (414 checks),
passenger attachment/restoration (20 checks) and open-street EVP driving/braking (28 checks).
Van measurements: rear actor/eye (.3974,-.119,-.9546)/(.3974,.57,-.9546), remote hips
(.3450,.0301,-.8500), head (.3418,.5271,-.9363). Its front lootbox, original cameras/listeners,
inventory and body/entity identity remain intact in the completed cycles. Native van
Host/Client-input driving travelled 7.614/7.880 metres and stopped. One earlier hideout
handover displacement failure is retained; its environmental cause is not proven.
These are one-native-process/managed-peer fixtures, not human or two-machine acceptance.
