# Scene continuity, automatic travel and late join

Mod 0.6.5 / protocol 8 retains LAN transport across supported MainScene + map changes.
Mandatory shared seating, movement and shared sleep belong to this milestone.
Milestone F has not started. [Acceptance](scene-continuity-acceptance.md) separates
implementation, controlled native evidence and eventual human acceptance.

## Transition barrier

1. Host validates authority/readiness before original selection/save writes. Connected
   mission-to-hideout return requires confirmed Driver and Passenger actors 1 and 2 in
   the SAME live Vehicle. Pending/exiting seats, outside/disconnected actors and separate
   cars cannot authorize return. Denial starts no load and changes no epoch.
2. A newer TransitionId/SceneEpoch announces the Host descriptor: map/location, native
   CurrentCar variant (0/1) and VanBought availability.
3. Quiescence closes gameplay requests, cancels inventory reservations, clears input/
   motion cursors, restores scoped passenger changes and releases scene bindings.
4. Client loads MainScene with original GameManager initialization. Scoped native reads
   select Host CurrentLocation/CurrentCar/VanBought and a fresh Saved_Pos branch without
   overwriting Client preferences. Even identical-map joins reinitialize.
5. Readiness checks actual GameManager.loadedMap, canonical original PlayerScript,
   SaveGame.loadedGame, Rewired, inventory/item array, camera, motor/look, selected
   vehicle manager and unpaused gameplay. Two consistent observations stabilize identity;
   SceneManager completion alone cannot open the barrier.
6. Client initial driver auto-entry is suppressed. Mission joins use a grounded authored
   exit anchor for outside spawn even before vanilla's story-gated initial seating. No arriving actor is
   automatically assigned an occupied seat.
7. Each peer reacquires native objects, rebuilds its registry and sends loaded state.
   A fresh world snapshot/backlog applies; owner inventory is reapplied to rebuilt native
   bindings. A fresh canonical time baseline gates native GameplayReady as well.
8. Gameplay resumes through the same connection/session. No previous seat Transform
   survives the epoch. Seats require fresh explicit entry requests in the new world.

Load/announce phases allow 120 seconds, waiting for the other loaded peer 60 seconds,
baseline phases 45 seconds. Phase changes reset deadlines. No timer declares native
initialization successful. Failure closes readiness and reports its phase while retaining
transport. Returning to the native menu safely terminates the session.

Stopping a session clears its former Host descriptor, epoch and sealed registry. Losing
a Client while continuing to host preserves the Host world. This distinction prevents
a former Host from rejecting a new Host's lower-epoch announcement. Duplicate loaded
acknowledgements preserve a completed WorldReady phase instead of reopening its baseline.

## Shared sleep and clock

Only Host initiates global sleep. Applicable mission sleep requires the same confirmed
two-seat condition; hideout sleep and solo/offline behavior do not invent a passenger
requirement. Host passenger Y opens original PassTime UI; T invokes guarded return.

Start → freeze transactions → Client acknowledgement → original Host PassTime.PassStart
→ native accelerated hour/day effects → authoritative wake result → Client apply and
acknowledgement → resume. Original Host effects execute once. Client Enviro progression
is suspended while connected; the verified DayNightTime hourly mutation block is skipped
while original lighting/UI refresh remains. Disconnect restores native clock progression.
Start/wake acknowledgement failures stay frozen and close readiness after 15 seconds.

State includes fractional internal Enviro hour/year/day, native weekday/week, total
hours, rent hours and days played. Sequence/session/epoch/phase validation precedes native
application. Full NPC schedules, missions/progression, economy and cooperative disk saves
remain later work; this clock slice is not their acceptance.

Controlled fixtures exercise native Host sleep/return, native Client automatic mission
loading, passenger entry, automatic return and direct hideout reconnect. Their other
peer is managed and mirrors a native fixture manifest. They cannot establish compatibility
between different native profiles or replace real two-machine acceptance.

Seat references are also invalidated when their original actor or verified anchor is
lost. The surviving native driver is detached from its plain seat reference and restored
before registry release. Canonical occupancy is cleared/released locally without failing
an otherwise healthy scene. Ordinary native exit temporarily disables CharacterController
after clearing the vehicle flag; this is still the same live original actor. Vehicle
existence and occupancy are independent, and stable manager-slot identity survives actor
reparenting. Failed scenes cannot reattach from old seat records. Normal epoch changes rebuild from
fresh native context. Missing original camera/whole-vehicle reinitialization outside a
legitimate scene boundary remains unresolved; no controller or camera clone is invented.
Controlled active van coverage now uses the descriptor's existing variant/availability
fields and scoped native initialization reads. It does not persist a local vehicle choice
or unlock; the native selected car is used for passenger/cycle/driving checks.
