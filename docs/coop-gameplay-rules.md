# Permanent cooperative gameplay requirements

The highest-priority [Vanilla-First Multiplayer Implementation rule](../AGENTS.md)
governs every requirement below and all future milestones/tasks. Inspect the original
mechanic first; enable Player 2 to perform its legitimate actions, then synchronize
the outcomes. Preserve native restrictions, UI and persistence. Rear-center passenger
seating grants no storage privilege; [vehicle storage findings](vanilla-first-vehicle-storage.md)
define the verified access rules.

These are authoritative product requirements for all future work. They supersede
earlier recon proposals about personal wallets, XP, skills or reward splits.

1. The host owns the canonical world, vanilla save, shared economy/progression,
   missions, wanted/police state, vehicles, world time, travel, sleep, NPC/police
   simulation and persistence for both cooperative players. Clients never own
   canonical persistent state. Host authority means automatic technical validation;
   it never means a manual approval prompt for ordinary client actions.
2. There is exactly one wallet: the Host Save wallet. Either player's earnings,
   sales, spending and rewards modify it once on the host. Both see its balance.
   A physical purchase goes only to its buyer: wallet 1000, player B buys for 100,
   wallet becomes 900 and only B receives the item.
3. XP, level, skill points and unlocked/purchased skills are session-global. Either
   player may buy through their own skill UI. Host validates points, prerequisites
   and absence of the unlock, deducts once and unlocks for both in the Host Save.
4. Inventories, tools/items, equipped items and equipment-specific upgrades are
   personal. Loot claimed by B belongs to B until a future explicit sale/transfer;
   selling credits the shared wallet. Personal records live in host-owned coop data.
5. PersistentCoopPlayerId identifies personal save records. NetworkPlayerId (1/2)
   identifies only the current connection actor. A returning friend restores their
   record; another friend gets another record. Identity providers may be locally
   generated or Steam-backed later; no Steam identity is hardcoded into save semantics.
6. The same host world must remain playable in normal offline singleplayer: same
   progression, wallet, XP and skills, original first-person stack, no remote spawn.
   Friends' personal records remain stored and inactive until their owners return.
7. Only the host selects/starts missions. Either player may independently complete
   objectives; accepted contributions advance the same global mission for both.
8. Vehicles are shared world state. Either player may drive. Driver/input ownership
   may be temporary; canonical simulation/world state remains host-authoritative.
9. Only the Host initiates sleep, return to hideout or equivalent global transitions.
   With a connected Client, mission-to-hideout and applicable mission sleep require
   BOTH actors authoritatively confirmed in the SAME vehicle: exactly one Driver
   and one Passenger. Either peer may occupy either role. Separate vehicles,
   standing outside, an empty passenger seat or an unfinished entry are denied.
   Proximity and an animated avatar do not authorize travel. Denial starts no load,
   changes no epoch and interrupts no synchronization. The Client never manually
   approves Host travel. Solo hosting preserves normal singleplayer travel/sleep.
10. NPCs/police may detect/chase either actor independently. If either is caught or
    arrested, SessionBusted triggers the normal failure/arrest outcome for both.
11. Both players must always be able to locate their teammate in the same gameplay
    scene through a permanent world indicator visible through walls, buildings and
    terrain, with screen-edge guidance outside the camera view, plus a distinct
    teammate marker on the native minimap. Support the original full map where it
    supports dynamic position markers. Reuse existing player poses and scene identity;
    bind markers to the correct player and SceneEpoch, remove obsolete markers and
    never display a fake position during loading or in another scene/map. These are
    cooperative UI only: preserve native local-player markers, map presentation,
    rendering, AI detection, stealth, collisions and gameplay. Implement this permanent
    requirement within [Milestone G](milestone-g-prompt.md), alongside NPC and security
    camera synchronization, rather than deferring it to a separate milestone.

The local player permanently uses original PlayerScript, motor, camera behavior,
CharacterController, cameras, hands, functional tools, input and interactions.
NetworkAvatar is remote presentation only.

Seat entry is confirmed only after the original local actor attaches and Host accepts
the confirmation. Pending/exiting seats and records with dead bindings never satisfy
the shared-car travel/sleep condition. The original Interact action selects the available
role and exits the passenger; it requires no manual Host permission. Loss recovery must
retain surviving original controllers and stop readiness if native context cannot be
rebuilt safely. A visual seated avatar alone never grants occupancy.

The real passenger seat and minimum shared motion are mandatory in the current
scene/travel milestone. They cannot be deferred to Milestone I or F. See
[vehicle contracts](vehicle-passenger-sync.md) and [current incomplete acceptance](scene-continuity-acceptance.md).
Connected sleep now uses a Host start/acknowledgement/wake/resume barrier and the
original Host time advancement. Clients apply canonical clock/day/hour counters
and suspend independent hourly mutation. Applicable mission sleep checks both
confirmed seats; ordinary hideout sleep does not. Full NPC schedule/economy/story
replication remains future work. Controlled native evidence is recorded separately
from real two-machine acceptance. The subsequent user-authorized 0.6.6 slice implements supported shared economy/storage; see milestone-f-acceptance.md.

Milestone E implements supported loose loot pickup, personal runtime inventory,
drop/repickup and ownership-checked equipment. Player B's pickup changes only B's
inventory. A physical drop lets A acquire that same instance; direct trading is
not implemented. Automatic host reservation and asynchronous completion provide
technical authority without approval UI. Original native inventories and functional
tools remain local. Runtime records use PersistentCoopPlayerId, and reconnect gets
the host's current record. E does not synchronize economy, storage, money, rewards,
missions or disk saves. The single shared Host-wallet rule above remains F's policy.

Milestone D observes original host Door/Window state and validates semantic Open/Close
requests automatically. Technical host authority never asks a human to approve a
door action. Door capabilities belong to the canonical host world. Disconnect
retains accepted host world state; reconnect receives its current baseline.
Remote lockpick, forced-entry, tool/inventory validation and persistence remain
limited/deferred; D changes none of the fixed shared/personal rules above.

Milestone C supplies metadata and replication infrastructure only. None of the
economy, inventory, mission, vehicle, AI, travel or cooperative persistence mechanics
above is implemented by its synthetic entities.

## Permanent shared vehicle and travel condition

Permanent vehicle layout: one original front driver and one cooperative rear-right
passenger. The original front passenger lootbox/storage remains untouched. Either actor
may take either role; the Host remains world/vehicle authority. Mission-to-hideout return
and applicable mission sleep require both connected actors confirmed in those two seats
of the same vehicle. Pending entry, exiting, empty seats, separate cars and disconnected
records cannot authorize a transition. Denial starts no load or epoch change. Only the
Host initiates global travel/sleep, with no manual Client approval. Solo/offline native
behavior is exempt from the absent-passenger condition.

## Future persistence boundary (design only)

HostGameSave remains the canonical vanilla world/progression. CoopMetadata will
have its own schema version and PersistentCoopPlayer records for inventories and
equipment, plus necessary coop data that cannot safely live in vanilla save keys.
SessionId, NetworkPlayerId, SceneEpoch, dynamic IDs and TestEntities are runtime
data and must not masquerade as persistent personal identity. No save-format change,
identity-provider persistence, Steam integration or TestEntity persistence occurs in C.
