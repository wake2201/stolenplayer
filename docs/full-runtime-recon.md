# Comprehensive runtime reconnaissance — 2026-10-06

The offline NPC-derived visual and asynchronous dropped-tool pickup are verified.
Ten actual game launches isolated startup failures and supplied targeted live
evidence. No broad gameplay synchronization was implemented. Production mod 0.2.1,
protocol 1 and LAN/direct-IP transport remain unchanged. Two-game Phase 1 acceptance
is **blocked: a second legitimate installation is unavailable**.

## Evidence convention and scope

`VERIFIED_RUNTIME`: directly observed live metadata, traces, screenshots or process
measurements. `VERIFIED_STATIC`: inspected installed metadata/targeted implementation.
`LIKELY`: reasoned hypothesis/design feasibility. `UNKNOWN`: not established.
Design proposals below are recommendations, not implemented behavior. Runtime wins
over naming assumptions. Truncated captures cannot prove absence.

Unity 2020.3.33f1, Mono CLR 4.0.30319.17020, BepInEx 5.4.23.5, Harmony 2.9;
SupportsSRE=false. Assembly-CSharp SHA256:
`3786E09344BD2CC72E599EBC2468F10C04BAEC58A9279A43B70FB56F79A9EA50`.
183 original DLL/EXE fingerprints verified unchanged after the task. Original assets
were not modified. Save backups were taken before each launch and the initial
backed-up save files restored byte-for-byte after the final game stopped. Post-test
files remain preserved locally; no manual save profiles were created. Registry
preferences are outside the file-backup restoration mechanism.

### Startup isolation

All measurements below are VERIFIED_RUNTIME, sampled at 1 Hz; values are bytes,
not virtual address-space estimates. “Samples” are alive observations, not exact
gameplay duration. System commit headroom includes other processes. Mode isolation
temporarily masks only our recorded loader/plugin additions.

| Run / UTC session key | Mode / result | Samples | Peak private | Peak working set | Minimum available RAM | Minimum commit headroom |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| R1 / 204315 | Closest safe vanilla; managed OOM in Astar graph deserialize/load | 70 | 9728684032 | 5116317696 | 2837413888 | 6344704 |
| R2 / 204547 | BepInEx only, zero plugins; managed OOM in GrassFlow culling | 60 | 10137571328 | 5296021504 | 2968215552 | 55435264 |
| R3 / 204657 | Diagnostics only; native Mesh.SetNormals/HighlightEffect crash | 73 | 10173534208 | 5323997184 | 3032702976 | 5390336 |
| R4 / 204907 | Multiplayer only; gameplay/F8 reached, controlled stop | 229 | 10230210560 | 5407535104 | 2807717888 | 415993856 |
| R5 / 205304 | Both; gameplay stable, capture cap diagnosed, controlled stop | 166 | 10305224704 | 5456248832 | 2712358912 | 431415296 |
| R6 / 205557 | Both; corrected targeted captures/crouch, controlled stop | 458 | 10289340416 | 5460615168 | 2758930432 | 302559232 |
| R7 / 210344 | Both; visible NPC visual, tools/vehicles/player, controlled stop | 693 | 10260643840 | 5444788224 | 3353997312 | 208482304 |
| R8 / 211540 | Both; pickup transaction and scoped NPC/time, controlled stop | 296 | 10247413760 | 5425287168 | 3521650688 | 300855296 |
| R9 / 212047 | Both; real preference write and same-process reload, controlled stop | 845 | 10472595456 | 5436379136 | 3762876416 | 184803328 |
| R10 / 214409 | Both; final menu-only metadata check, controlled stop | 41 | 2465140736 | 1373257728 | 7445217280 | 5905149952 |

Full session directory prefix is `20261006-`; suffix is the named mode. Player.log,
BepInEx logs, recent crash text, memory NDJSON, exact process identity and save
backups are ignored under `.local/runtime-sessions`. Captures are ignored under
`.local/runtime-dumps`. No original binary was overwritten to isolate modes.

LIKELY: Windows system commit pressure contributes to startup allocation failures:
R1/R3 approached 5–6 MB headroom despite approximately 3 GB available RAM. Failure
without either plugin proves our plugin is not a necessary cause. R3 contains a
native highlight/mesh stack, not an explicit managed OOM, so its root cause is UNKNOWN.
FMOD is not established as the common root cause. Background commit and scene/save
differences prevent measuring a clean mod memory delta from this sequence. Six later
launches reached gameplay and closed under control; startup is usable for bounded
reconnaissance but not universally fixed. No Windows memory/pagefile settings changed.

### Main capture ledger

| Evidence | Capture basename / scope | Completion |
| --- | --- | --- |
| R7 candidates | `recon-20261006-211018-3ec78da8-1-avatars` | 124 candidate records; global traversal truncated |
| R7 definitions | `recon-20261006-211048-3ec78da8-2-inventory` | 452 ItemID definitions, 462 components; global traversal truncated |
| R7 vehicles | `recon-20261006-211213-3ec78da8-4-vehicles` | Five cars plus enter/cargo/manager records; global traversal truncated |
| R7 player | `recon-20261006-211405-3ec78da8-6-player` | Complete 1,186-node actor hierarchy; surrounding global traversal truncated |
| R8 NPCs | `recon-20261006-211755-4aa1db8b-1-avatars`, `NPCs[0]` | Complete: 13,017 objects / 101 components |
| R8 time | `recon-20261006-211806-4aa1db8b-2-managers`, Enviro root | Complete: 12 objects / 2 components |
| R8 world | `recon-20261006-211833-4aa1db8b-3-interactions`, DOMY roots | 4,035 visits / 300 components; record-limited, truncated |
| R8 pickup | `recon-20261006-211843-4aa1db8b-4-inventory` | Explicit transaction before/0.1s/0.7s + Item_Picked trace |
| R9 reload | `recon-20261006-212802-703a21c1-1-managers` | Scene callbacks and before/after GameManager/PS; manager traversal bounded |
| R9 police | `recon-20261006-213440-703a21c1-3-ai`, MANAGERS root | Complete: 968 objects / 3 relevant components, inactive police templates |
| R10 schema | Final menu-only manager capture | 2,823 objects / 2 components; generic enabled/active, transform and field scene/ID verified |

Basenames denote ignored `.ndjson` files. R7 `avatar-visible.jpg` is the screenshot
of the full-body test. UTC/session identifiers allow correlation without committing
raw dumps, save contents or machine-specific paths. Float fields are invariant;
some Unity metadata uses local decimal commas. Normalize numbers when analyzing.

## 1. Player architecture

VERIFIED_RUNTIME: genuine root `IMPORTANT OBJECTS[0]/First-Person Character[19]`,
PlayerScript + motor/camera behaviors + CharacterController and Rigidbody. Complete
hierarchy has 1,186 nodes. Controller standing height 1.6, radius 0.35, centre zero;
runtime crouch height approximately 0.6000308. Root is simulation position/body yaw;
feet origin needs visual correction. Camera local pitch mapping is VERIFIED_STATIC,
while new automated mouse-look movement acceptance remains UNKNOWN.

## 2. First-person rig

VERIFIED_RUNTIME: Tilt/Head/CamAnim/Camera, separate HandsCam, hand/arm/wrist/tool
branches, local body/legs and camera/effect animations. Phone rendered in the original
hands. Local motor, camera, original hands and functional tools remain intact.
Remote visual is separate. [Detailed rig/equipment inventory](player-animation-inventory.md).

## 3. Avatar candidates

VERIFIED_RUNTIME: ordinary male/female citizens, special citizens, inactive police,
sidewalk people/traffic, hidden player/previews and five TPP car bodies. Guards are
VERIFIED_STATIC only, not found in the complete loaded NPC scope. Preferred ordinary
male `NPCs[0]/105[6]/NPC[7]`; female alternative `112/NPC[5]`.
[Selection and extraction](avatar-candidates.md), [per-source catalog](avatar-catalog.md).

## 4. Skeletons

VERIFIED_RUNTIME: preferred male has 89 transforms, seven skins, body 49 bindings,
Mixamorig spine/head/arms/hands/fingers/legs/feet. Female body has 50 bindings.
Human anatomy is not a Unity Humanoid Avatar: ordinary citizens use legacy Animation.
Fresh bone remapping produced normal full-body skinning with ground-corrected feet.

## 5. Animation inventory

VERIFIED_RUNTIME: 21 preferred NPC legacy clips including BreatheIdle/Walk/Walk2/
Running/DoorOpen. Actual legacy states wrap Loop even when clip asset isLooping=false.
Player legs Generic controller `man`, null Avatar, root motion disabled; clips
walking/idle/walkBack/run/jump/Kick and six parameters. Full-body crouch/strafe/tool
actions, sustained remote locomotion and retargeting remain UNKNOWN. Network transforms
must own movement; semantic states drive a rig-specific animation adapter.

## 6. Tools/equipment

VERIFIED_RUNTIME: 452 definitions; crowbar, lockpick, flashlight, hacking_laptop,
keyfob, blowtorch and other actual IDs/names recorded. Definition names can repeat.
Local wrist presentation includes functional scripts and camera-specific animation.
Design: remote HeldItemId creates renderer-only equipment attached to NPC hand bones,
with per-rig/item offsets outside the wire state. Visual socket acceptance UNKNOWN.

## 7. Interaction architecture

VERIFIED_STATIC: PlayerScript uses raycast/tag routing and branch-specific reach/gates;
there is no established universal interactable service. Door, WindowUse/LockedWindow,
Lockpick, Pickupable, LightSwitch, Garage_Switch, Mission_Item, devices/shop and
EnterCar have specialized state and cached player side effects. VERIFIED_RUNTIME:
representative world components and state captured, including container doors,
stash lock, house switch, authored loot and mission object. Device/shop transaction
execution remains UNKNOWN. [Representative state/method/side-effect inventory](world-interactions.md).

## 8. World identity

VERIFIED_RUNTIME: duplicated names, duplicate DOMY roots across scenes and repeated
dropped names defeat name-only identity. PS instance ID changes on reload at the same
hierarchy. Proposed registry uses validated semantic/save key or scene + indexed
hierarchy + component ordinal, collision checks, epoch/generation and host IDs for
dynamic spawns. Cross-peer stability is UNKNOWN. [Identity analysis](world-object-identity.md).

## 9. Loot/pickup transactions

VERIFIED_RUNTIME: dropped crowbar PickUp returns immediately; ~0.475s later
Item_Picked increments quantity 0→1 and weight 0.1→0.2; by ~0.708s world object is
gone. No XP/money change in that branch. Original aim eligibility, authored theft,
money/heavy rewards and race/disconnect semantics remain UNKNOWN. Reserve and commit
on host around actual completion, not PickUp postfix. [Transaction evidence](pickup-transaction.md).

## 10. Inventory

VERIFIED_RUNTIME: definition metadata + mutable quantity/storage fields share ItemID
components; capacity 15kg and starter weight 0.2kg. No universal physical-item GUID
established. VERIFIED_STATIC: durability partly player-level, heavy/cargo/locker/drop/
sell paths specialized. Design distinguishes definition ID from host physical instance,
personal inventory and shared storage ownership; receivers must not replay award methods.

## 11. NPC AI

VERIFIED_RUNTIME: ordinary AI with RichAI, Seeker, legacy animation, habit/pref state
and single cached player/GM. VERIFIED_STATIC: schedules, doors/sensors/vision/sound
target single-player references. Host authority is LIKELY feasible with explicit actor
target service; disabling client Update alone is inadequate.
[AI/perception adaptation](npc-multiplayer-analysis.md).

## 12. Police

VERIFIED_RUNTIME: manager stars 0, five spawn refs, empty spawned lists, cached PS,
last-known position, inactive eight-skin police templates. VERIFIED_STATIC: chase,
search, catch and cleanup read one player. Active multi-target pursuit/arrest is UNKNOWN.
Design one host simulator with actor IDs/wanted contribution, clean client visuals.

## 13. Vehicles

VERIFIED_RUNTIME: five CarController roots, ~1,000kg Rigidbody + EVP controller/input,
four wheels, enter triggers, driver TPP and starter cargo manager. VERIFIED_STATIC:
enter/exit changes local PS/input/camera/parent; storage uses slots/local poses.
Moving physics/damage/passengers/cargo transactions remain UNKNOWN. Recommend host EVP
physics, validated driver inputs, seat tokens and interpolated state.
[Vehicle analysis](vehicle-multiplayer-analysis.md).

## 14. Missions

VERIFIED_RUNTIME: StoryManager/SelectResidence/Mission_Item layouts and references,
intro objective “break into 102” UI. VERIFIED_STATIC: Mission_Item completion writes
mission prefs, money/XP and can advance story; StoryManager conditions include actor
areas/actions, unlock prefs and local phone/UI. No reward-bearing objective was completed.
Recommend session-global objective stage/world unlocks, individual participation and
idempotent reward ledger; dialogue/tutorial presentation stays local.

## 15. Progression

VERIFIED_RUNTIME: money 0, debt 500,000, XP/points 0 in pickup fixture; no progression
mutation in that branch. VERIFIED_STATIC: PS money/XP/points, GameManager.AddXP,
mission rewards, store/sale/rent/gas deductions and Skill_Manager.Learn_Skill preferences
(`Skill_<index>` plus named extra upgrades). Save keys include Money, Debt, Lvl, XP,
XPPoints, SpecialPoints, ExtraPoints. Coop policy is a design decision: default individual
tools/skills/XP, explicit shared or individual wallet choice, global mission rewards
awarded once with documented split. Do not mirror all single-player prefs blindly.

## 16. Save system

VERIFIED_RUNTIME: FBPP Save→SaveSaveFile(true)→WriteToSaveFile on Continue; payload
length only logged. VERIFIED_STATIC: SPrefs/FBPP typed scrambled JSON, AutoSave=false,
SaveGame aggregation and UI quicksave path. SaveSaveFile(false) is not proof of disk
write. OnApplicationQuit→LoseGame invokes an empty method; save confirmation attempts
did not complete. Travel/autosave/mission flushes remain runtime UNKNOWN. Future host
coop checkpoints must be isolated from clients' single-player profiles.

## 17. Scenes

VERIFIED_RUNTIME: MainScene Single + MadisonSt Additive, active MainScene, menu
return unloads both then recreates PS/GM on Continue. Future travel needs quiescence,
epoch barrier, full scene readiness, registry comparison and initial world snapshot.
Production Phase 1 still disconnects on scene change.
[Exact callback order and save paths](scene-sync-analysis.md).

## 18. Managers

VERIFIED_RUNTIME: scene-owned GameManager/PS/SaveGame/Inventory/Police/Story/Vehicles/
Skills/Enviro, separate menu initialization. Steam DDOL lifecycle is static, initialized
runtime UNKNOWN. References must rebind after real recreation; mod transport/session
lifetime remains separate. [Manager catalog](runtime-managers.md).

## 19. Time

VERIFIED_RUNTIME: Enviro GameTime Simulated, calendar/time fields, observed 26-minute
cycle and day/night multipliers 1; GameManager derives hour/minute/day. VERIFIED_STATIC:
schedule/story/save and skip paths depend on it, some branches change cycle length.
Host owns world time and skip events; clients smooth presentation. Sleep rollover UNKNOWN.

## 20. Physics

VERIFIED_RUNTIME/STATIC: CharacterController actor, EVP cars, Rigidbody dropped/
carried props, transform/animation doors. Replicate collision-relevant entities and
ownership transfers; cosmetic debris/effects stay local. Full EVP prediction,
ragdoll transitions and heavy collision correction remain UNKNOWN.

## 21. Gameplay noise/events

VERIFIED_STATIC: the MakeNoise float accumulator, sensor/vision branches and AI.HeardSoundAction supply
hearing/detection inputs; AudioSource alone is insufficient. Soundwave is a visual
material animator in examined source. Host validates actor-position-action noise,
then replicates reactions and audio separately. Deliberate perception/noise runtime
acceptance remains UNKNOWN.

## 22. Steam transport feasibility

VERIFIED_STATIC: Steamworks.NET 20.0.0 / SDK 1.52 wrappers include sockets/messages/
utils/matchmaking; game source initializes original AppID 1332720. VERIFIED_RUNTIME:
assembly and log messages, not authenticated networking. Lobby/socket availability
is UNKNOWN. Optional legitimate SDK/Spacewar harness and owned game tests are gates;
LAN stays independent. [Primary sources and analysis](steam-transport-feasibility.md).

## 23. Unknowns and minimum fixtures

| Gap | Minimum additional setup / test |
| --- | --- |
| Two-game Phase 1 visibility, movement, teardown/reconnect | Second legitimate installation; same area, Host/Join and walk/turn/scene reload |
| Production NPC locomotion/root drift/crouch/look/equipment | Clean visual fixture, sustained idle/walk/run, authored crouch or verified retarget, one socket item |
| Original aim eligibility and theft rewards | Backed-up disposable save, stand aimed at one ordinary non-tool pickup |
| Lock/window/device/shop effects | Stand beside unlocked door/window, one lock and device/sale point; perform one action per capture |
| Guard/police perception/chase/arrest | Existing legitimate guard-area save; separate backed-up short detection/chase fixture |
| Vehicle entry/drive/cargo/damage | Stand beside starter car; slow enter/drive/exit and one heavy cargo transfer |
| Mission reward/skill/economy ownership | Disposable save one step before objective completion and one skill/sale |
| Manual/autosave/travel persistence | Save-enabled disposable state and one explicit save/travel, verify write + reload |
| Every scene/canonical registry mapping | Relevant-registry comparison across restart/reload/map and both peers |
| Sleep/schedule rollover | Backed-up save immediately before sleep or schedule boundary |
| Steam authentication/sockets/lobbies | Owned Steam launch plus two authorized SDK/example test accounts |

Menu/crouch/drop navigation worked; sustained movement and locked-cursor aim could
not be controlled reliably through the available UI API. Read-only targeted capture
of inactive sources completed every other accessible investigation first. These gaps
do not require the user to inspect internals; only precise gameplay setup when
automation cannot reach a fixture. No unavailable behavior is marked passed.

## 24. Technical blockers and tooling limits

System commit pressure/startup failures remain a reliability risk. A second game is
unavailable. Legacy NPC rigs lack proven full-body crouch/tool coverage. World keys
need two-peer collision/stability acceptance. Single-player references, coroutine
transactions, save ownership and non-deterministic AI/physics need explicit adapters.

Diagnostics now rotate manual files, scope roots, bound traversal/records/strings/
bones/clips, yield between objects and use opt-in finite observation patches. A 4ms
yield budget is not a hard cap on one complex object inspection. R5's automatic full
captures hit the 32MiB emission limit; automatic captures now target managers and
manual captures rotate. Some global snapshots still truncate intentionally. Raw
R1–R9 state fields lack scene IDs and need owning-component correlation. R10's
updated field records include scene and component ID, and generic Behaviour records
distinguish enabled from active-and-enabled. Engine camera/audio metadata is supported;
these additional camera/audio records were build-checked, not exercised by the
menu-only manager fixture. No arbitrary
getter/lazy singleton invocation, full assets or frame-by-frame dump is used.
[Tooling runbook](runtime-tooling.md).

## 25. Synchronization ordering and validation

First finish stable Phase 1 two-game acceptance (A), then production avatar and
semantic animation/equipment (B). Registry/epoch/revisions and bounded join snapshot
(C) precede interactions (D), loot/inventory (E), storage/economy (F), NPCs (G), police
(H), vehicles (I), story/progression (J), travel (K), coop saves (L), optional Internet/
Steam transport (M), stress/desync/polish (N). Snapshot/diagnostic design begins in C
and expands per system. Save model must be agreed before any persistent mutation.
[Full milestone gates](full-sync-roadmap.md), [authority/replication design](multiplayer-architecture.md).

All four project builds completed with zero warnings/errors. All 93 automated checks
passed (protocol/fuzz/finite bounds, sessions/reconnect/epochs, interpolation,
allocation checks and real UDP with impairment). Five independent-process harness
scenarios passed: baseline, abrupt host exit, abrupt client exit, 50ms/20ms jitter/5%
loss and 100ms/40ms jitter/10% loss. These use synthetic poses, not two game players.
Multiplayer/recon validators cover script parsing, required reports, forbidden
tracking/machine paths, output allowlist, vendored provenance and binary hashes.
Local source/deployed plugin hashes match; original baseline remains unchanged.
No raw captures/assets/decompiled game source are committed.
