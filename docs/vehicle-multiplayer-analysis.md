# Vehicle runtime and authority analysis

Evidence: R7 targeted vehicle capture, avatar/TPP capture, PlayerScript/GameManager
fields; static CarController, EVP.VehicleController/VehicleStandardInput, EnterCar,
VehiclesManager and VanHeavyItems. Bounded global vehicle traversal was truncated.
Five CarController roots were inspected; this is not proof that every unlockable
vehicle type is covered.

| Live root | Scene / active | Physics / driver model | Findings |
| --- | --- | --- | --- |
| PLAYER CAR | MainScene / true | Rigidbody ~1,000 kg, EVP four wheels, eight-skin TPP | Near player (528.605,46.676,166.623); PS bound; VanHeavyItems present |
| MUSTANG | MainScene / false | Same observed EVP/Rigidbody family | Inactive source; TPP nonuniform scale (0.98,0.92,0.92) |
| DOMY / Dom_106 / CAR 106 | MadisonSt / false | EVP/Rigidbody, EnterCar, TPP | Story activation branch |
| DOMY / Villa / Interior_114 / thunderdrive | MadisonSt / false | EVP/Rigidbody, EnterCar, TPP | Vehicle-specific hierarchy/seat calibration |
| DOMY / 3 Domy / Interior_112 / CAR 112 | MadisonSt / false | EVP/Rigidbody, EnterCar, TPP | Vehicle-specific hierarchy/seat calibration |

VERIFIED_RUNTIME: roots are nonkinematic gravity-enabled Rigidbody, constraints None,
zero velocity at capture; trigger/enter helpers have separate kinematic 1 kg bodies
with gravity disabled. Object-layout records show EVP.VehicleController and
EVP.VehicleStandardInput; wheel references and throttle/brake/steer/gear fields are
inspectable. Resting captures do not verify moving wheel slip, suspension response
or steering control. Driver animation sources have driving_front/left/right.

VERIFIED_STATIC: EnterVehicle/ExitVehicle modify the cached local PS, movement/
CharacterController, cameras/input, active vehicle, player parenting and seat
presentation. VehiclesManager coordinates vehicle selection. Unactivated car
references can be null until initialization, so copying field values from inactive
templates cannot prove operational behavior. VanHeavyItems manages heavy cargo
slots and saved local poses; car unlock/paint/upgrades and gas/damage state are
additional specialized state, not just Rigidbody position.

UNKNOWN runtime: safe enter/exit, driving throttle/brake/steering, passenger capacity,
collisions/damage, trunk transfer and vehicle save/reload. The nearby starter car
was located in captures, but UI automation could not reliably aim/move to its
interaction collider. No original control methods were invoked artificially.

## Proposed authority

Use host-authoritative EVP physics at the game's FixedUpdate cadence. Driver owns
raw input commands, host validates seat token, actor, sequence and allowable input.
One authoritative driver at a time; passenger seats require measured transforms and
explicit occupancy state. Client original input/physics/coroutines must not run a
second uncontrolled simulation.

Replicate pose, linear/angular velocity, steering/wheel presentation, engine/gear,
gas/damage, occupied seats, cargo revisions and spawn/active state. Interpolate
remote physics bodies; corrective snaps use validated host state. Do not send each
decorative subpart's transform every frame. Seat-relative avatar transforms and
driving animations remain presentation below the replicated vehicle root.

Owner prediction is optional later: non-deterministic EVP collisions and contact
state require measured correction quality before choosing it. Host physics is a
design supported by runtime structure, not verified two-player vehicle authority.

The next fixture needs only: on a disposable backed-up save stand beside PLAYER
CAR, enter, drive slowly for a few seconds, stop, exit and place one heavy item in
storage. Scripts collect before/after vehicle/seat/input/cargo/save fields. Repeat
with both actors once a second legitimate installation is available.
