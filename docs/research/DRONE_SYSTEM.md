# DRONE_SYSTEM — drone purchase, ownership, deployment, remote visibility (W4.2C Phase A investigation)

Date: 2026-11-14. Method: read-only Mono.Cecil IL dump + read-only UnityPy class counts (`level1` MainScene). Legend
as in `FURNITURE_SYSTEM.md`. Also covers the "other regions" audit (§7).

## 1. Classes [V]
| Class | Role |
|---|---|
| `DroneManager` | One per MainScene (`DRONE CONTROLLER`, active). Assignment, activation, battery, signal range, camera switch, upgrades. |
| `ItemID` (`isDrone`, `droneObject`) | The drone is an inventory item; its world GameObject is `ItemID.droneObject`. |
| `DroneID` | Marker + `wallSensor` on the drone object. |
| `DroneMovement` / `DronePropelers` / `DroneCamera` / `DroneCollision` | Third-party drone physics/camera package (`DroneController.*`). |
| `DroneTech` / `DroneShopScript` / `DroneUpgrade(Panel)` | Drone shop UI: select, restore, upgrades. |
| `PlayerRemoteDrone` | Remote-controlled ground drone variant (noise maker, camera disable). |

## 2. The six layers
| Layer | Original | Evidence |
|---|---|---|
| Purchase / restore | `DroneTech.RestoreDrone`: `moneyAm -= restore_cost`, `item_amount` set, `SPrefs Item_<id>`, `SaveMoneyAmount`. Upgrades `DroneUpgradePanel.BuyUpgrade`: `moneyAm -= upgrade_cost`, `SPrefs <drone_upgrade_pref>`, `SaveMoneyAmount`, optional `StoryManager.AdvanceStory` | [V IL] |
| Ownership | Inventory item (`Item_<id>` backpack count) — player-owned | [V] + `CampaignPolicy` PlayerOwnP1 for `Item_<digits>` [M] |
| Equip | `Inventory.EquipDrone` → `DroneManager.AssignDrone(ItemID)` (also from `Pickupable.<PickingItem>` → `DronePickedUp`); unassigned by `ItemID.Drop_Item`, `Move_Out_Of_Storage`, `Inventory.MoveItemToStorage` | [V refs] |
| Deploy | `DroneManager.Update` → `ActivateDrone`: allowed only when the loaded region is `MadisonSt`, `Warehouse`, `Hillville`, `BankScene`, `Forestside Resort` or `Museum` (else warning `cant_launch_drone_location`); hides weapons, `PS.ActivateControls`, places `droneObj` in front of the player, activates it, switches camera/controls to the drone | [V IL_0012–IL_0361] |
| World object | `ItemID.droneObject` — a local GameObject driven by `DroneMovement` (rigidbody) on the owner's instance | [V AssignDrone IL_0009] |
| Gameplay effects | security-camera disabling (`camerasDisableAmount`), loot info, NPC spotting of the drone (`JoystickCharacterSeen`), battery/range; while active `LegsController` hides the legs and saving is refused (`CheckIfCanSave`: drone active) | [V fields; WORLD_SYNC_AUDIT] |

## 3. What the mod excludes today and why [M]
- `WorldItemSync` (dropped items) and `WorldLootSync` (pre-placed loot) exclude `isDrone` items: a drone has a bound
  world object and assignment side effects (`ItemID.Drop_Item` → `UnassignDrone`) that a shared drop/pickup cannot
  reproduce safely; the drop/pickup stays local (logged).
- `PlayerSync` sends only the player body pose (34 B snapshot, 20 Hz). The drone object is never sent → **the other
  player sees nothing** of a flying drone; the avatar stands where the pilot's body is.
- Purchases/upgrades: `Item_<id>` is player-owned (Player-2 record for Player 2); upgrade prefs are `World` keys → a
  Player-2 upgrade is paid from Player 2's money but its pref lives only in the sandbox and is reset at the next join
  (DEFECTIVE outcome, same pattern as the garage).
- Security cameras disabled by a drone: local to the pilot's instance (no camera state sync yet).

## 4. Remote visibility — proposed design (not implemented)
- Presentation only, owner-simulated: extend the player snapshot (or a separate 2 Hz-on-change + 10 Hz-while-active
  message on the unreliable channel) with `droneActive` + drone position/yaw (≈ 16 B). Observer: a script-free clone of
  the original drone model (same pattern as `RemoteAvatar`: original asset, no physics/colliders), interpolated by the
  existing `SnapshotBuffer`. No drone physics on the observer, no second `DroneManager` activation.
- The pilot's body: stays visible standing (vanilla: the player stands still while piloting) [A: confirm the body
  remains at its spot — Q].
- Gameplay effects (camera disabling, NPC spotting) need their own host-authoritative transitions later (cameras are
  region objects; NPC perception belongs to NPC replication).

## 5. Classification
| Aspect | Class |
|---|---|
| Drone item / ownership | player-specific state (Player-2 record) |
| Restore cost, upgrade purchase | host-authoritative transaction (economy) + player-specific or shared upgrade flags (decision) |
| Upgrade prefs | persistent campaign state — today `World`; must become per player or shared explicitly |
| Drone flight | local simulation on the owner + remote presentation (snapshot) |
| Disabled cameras, NPC spotting | temporary region state / region-simulation-dependent |

## 6. Risks
- Drone in another region than the observer: nothing to show (region-scoped like the avatar).
- Upgrade/story coupling: `BuyUpgrade` may call `AdvanceStory` → Player 2 must not advance the story locally (refuse or
  host transaction) — HIGH-RISK if left as is once story sync exists.

## 7. Other regions — Madison-specific assumptions in the mod [M]
| Location | Assumption | Change needed |
|---|---|---|
| `src/Shared/RegionState.cs:60` `ObjectStateRules.SupportedNamespace(ns) => ns == 99` | doors/locks/windows only in Madison | data-driven set of supported namespaces (e.g. {99, 97, 0}); every other rule is already per namespace |
| `src/Shared/WorldLoot.cs:71` `LootRules.Supported = {99}` | shared loot only in Madison | same set (per-kind if needed) |
| `src/Plugin/Plugin.cs:47-48` config texts | wording | update text |
| `src/Plugin/WorldObjectSync.cs` / `WorldLootSync.cs` log texts "Madison Street" | wording | update text |
| Region table `Regions` / `IdentityNamespace` | already covers every scene (Hillville 97, Hideout 0, …) | none |
| `MaxPerRegion` 1200 per kind | sized for Madison (1142 doors) | Hillville 605 doors / 39 locks / 116 windows / 598 loot [V scene counts] fit |
| Census digest gate (both instances must report the same scene digest) | generic | none; needs a Hillville census log from both PCs before enabling |
| `TravelRules.SeatsOnArrival` (97, 99), leave points (`MadisonPoint`/`HillvillePoint`) | vehicles already handle Hillville | none |
No per-region copy of the architecture is needed: the registry, identities, reservations and bridges are namespace-
generic. Enabling a region = (1) census digest equality verified in logs, (2) story/mission restricted objects of that
region listed by the host census, (3) adding the namespace to the supported set, (4) a manual QA pass in that region.

## 8. Story-test prerequisites (W6A.4) [V]
- Story drone = item 89 `flyby_drone` (Flyby Lite), stolen at index 15 (story11, `STEAL DRONE 103 MISSION/enable/flyby drone pickup`, `advance_story_if_picked`).
- Persisted state: `Item_89` (ItemID.Save_Item_Amount), `Drone` = equipped item id (SaveGameNewSystem IL_03c8..IL_03fa); restored by `DroneManager.<FindDrone>` (0.5 s after start) -> `AssignDrone` -> `ApplyUpgrades`.
- Flyby upgrades (level1 `DroneShopScript`, decoded): `flyby_upgrade1` speed_module (250, lvl 2), `flyby_upgrade2` battery_module (300), `flyby_upgrade3` camera_module (300, camera_upgrade, advanceStory story12).
- Story-test seed for index >= 16 / 17 writes these keys (src/Shared/DevScenario.cs `ScenarioPrereqs`); Player 2 gets item 89 in its fresh record and the World keys with the host campaign.

## 9. Cooperative drone ownership — decision W6A.5 [M]
Product decision: after the shared campaign unlock, BOTH players own one personal, functional drone. No host-only drone, no drop / hand-over.
- **Unlock once:** the story index is shared (W6A). The drone step (story11 pickup at index 15) happens once in the campaign.
- **One drone per player:** the drone is an inventory item (`Item_<id>`, `CampaignPolicy` PlayerOwnP1). Player 1 keeps it in the host campaign; Player 2 in its host-owned record (`P2Inventory`). Equip with the original `Inventory.EquipDrone` -> `DroneManager.AssignDrone` on each PC.
- **No duplicates:** a grant is an absolute count (`Set`, never `Add`) on a record that does not yet hold the item; late join / reconnect load the existing record (same rule as `ScenarioPrereqs.P2Grants`).
- **No double charge:** upgrade keys (`flyby_upgradeN`, `Drone`) are `World` campaign keys, bought once on the shared balance; a purchase by either player must be a host transaction (W4.1B wallet) that writes the key once. `DroneTech.RestoreDrone` (repair) stays per player, paid by the player who repairs.
- **Developer tests:** F10 isolated seeds may provision the drone (seed keys + Player-2 fresh record). Normal campaigns never receive developer grants (`P2Grants` requires a scenario and the isolated developer host).
- **Deferred (not in W6A.5):** granting Player 2 its own drone from the shared story11 unlock in a normal campaign, and Player-2 `DroneShopScript` / `DroneUpgradePanel.BuyUpgrade` as host transactions (`BuyUpgrade` can call `AdvanceStory`, see §6). Needs its own inventory milestone.

## 10. Flight presentation + camera disable (W6B) [V IL]
- `ActivateDrone` (on): `PMB` / `PCB` / main camera off, `droneObj` 3 m above the player, `joystick_character` at `PS.y - 0.829` (the standing pilot), `playerAttach = true`. `Update`: while `playerAttach`, `PS.transform` = drone pose. Off: `PS.transform` = `joystick_character + 1.2 m`, drone + body hidden; out of range → drop object, drone lost.
- Mod: snapshots carry the body (`joystick_character`) + a separate drone pose; the observer shows a renderer-only clone of the original drone model. Position reports skip the flight.
- Drone camera disable: `PlayerScript` (drone active, ray on `SecurityCam`, `camerasDisable > 0`, `relock_door`): `droneManager.CameraDisabled()` (charge), sound, `SecurityCamera.DisablePermamently()`. Shared per region epoch since W6B (host commit; replicas run the original; host story check in the original).
