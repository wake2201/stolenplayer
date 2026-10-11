# Runtime manager catalog

Evidence: R7/R9 manager captures, R8 focused Enviro and inventory captures, R9
complete 968-object MANAGERS-root AI capture and same-process scene reload.
Global manager snapshots are bounded/truncated; the table covers observed managers
and explicit static integration points, not every Manager-named type in the game.

| System / component | Discovery and runtime location | Lifetime | Player/save dependencies / confidence |
| --- | --- | --- | --- |
| PlayerScript / motor / camera | Player-tag genuine root under IMPORTANT OBJECTS | MainScene actor; recreated on reload | Original local input/presentation and inventory, cached by many systems; VERIFIED_RUNTIME |
| GameManager | GameManager tag, MANAGERS/GAME MANAGER | MainScene; ID 308524→945622 on R9 reload | One Player/PS, Enviro, inventory/story/vehicles/police; global preference bookkeeping; VERIFIED_RUNTIME |
| SaveGame | Component beside GameManager | Recreated with MainScene, R9 308526→945624 | Cached PS/inventory and system managers; SPrefs/FBPP flush; runtime component + static save branches |
| Inventory | Inventory-tag pause canvas under IMPORTANT OBJECTS/Canvases | MainScene UI/actor relationship | Cached PS, quantity/weight/economy display, save/travel buttons; VERIFIED_RUNTIME |
| ItemID definitions | PlayerScript.item_IDs, inventory item-definition hierarchy | MainScene, 452 references | Definition + mutable quantities/storage/locker; VERIFIED_RUNTIME |
| Police_Manager | MANAGERS/POLICE MANAGER | MainScene, 313472→950570 | One PS, stars/location, spawned lists and templates; VERIFIED_RUNTIME |
| PolicemanAI templates | Police manager POLICE child, inactive male/female | MainScene templates; spawned lifecycle not exercised | AI/detection/navigation/player reference; VERIFIED_RUNTIME templates, static pursuit |
| Skill_Manager | MANAGERS/SKILL MANAGER | MainScene | Skill list/index and preference names; static Learn writes; runtime component |
| StoryManager | MANAGERS/STORY MANAGER | MainScene | One story stage, current StoryScript, PS/camera/phone/UI, saved Story; VERIFIED_RUNTIME + static mutation |
| StoryScript / StoryObject | Referenced scripts/trigger/presentation objects | Scene-dependent | Player conditions/area progression and UI; static behavior, live referenced state |
| Mission_Manager / Mission_Item / SelectResidence | Pause residence scripts and world objective objects | Per scene/UI; menu has its own inactive Mission_Manager | Residence ID, mission preference/index, player reward; VERIFIED_RUNTIME layouts + static reward paths |
| VehiclesManager | MANAGERS/VEHICLES MANAGER | MainScene | Vehicle selection/sources and cached gameplay references; VERIFIED_RUNTIME |
| CarController / EVP / VanHeavyItems | PLAYER CAR and four inspected alternatives | Scene cars, some inactive sources | Local PS/driver/input/camera, physics, cargo/save; VERIFIED_RUNTIME layouts |
| DayNightTime / EnviroSky | Enviro Sky Manager and EnviroSky Standard | MainScene in observed loads | Player/camera and GameManager time; GameTime structure and saved time; VERIFIED_RUNTIME |
| AI / NPCHabits | NPCs residence branches | Scene citizens, some inactive | One PS/player/GM, habits/time, door/noise and pref names; VERIFIED_RUNTIME + static |
| SidewalkSpawner / SidewalkAI | MadisonSt SIDEWALK NPCs | Additive street/spawn lifecycle | Random pedestrian/traffic spawn, cached player/nav; runtime sources + static lifecycle |
| Audio/mixers | GameManager references, actor and scene AudioSources | Scene/presentation-dependent | Music/pause/game sound distinct from hearing events; VERIFIED_RUNTIME references |
| InitializePrefs | INITIALIZE in MainMenuNew; INITIALIZE PREFS in MainScene | Separate scene instances | FBPP config/profile and persistent path; VERIFIED_RUNTIME instances + static setup |
| SPrefs / FBPP | Static wrapper / FileBasedPrefs assembly | Managed static state, process/profile-dependent | Typed preference state and write boundary; VERIFIED_STATIC, runtime Save/write calls |
| SteamManager | Static s_instance/lazy Instance, DDOL by source | Source requests DontDestroyOnLoad | Existing-object lookup found no SteamManager by its expected name; runtime initialized/service state UNKNOWN |

VERIFIED_RUNTIME: ordinary GameManager, PS, Police_Manager and SaveGame are not
mod-owned persistent session services. Their same-process IDs changed after genuine
unload/reload. Menu and game inventory/initialization objects must not be confused
by similar names. Component records include scene; interpret state-field records
within their owning component/snapshot rather than joining path-only globally.
That correlation applies to the older R1–R9 fields; R10 adds scene/component ID
to each field record.

VERIFIED_STATIC: SteamManager.Initialized invokes a lazy getter that can create a
GameObject and initialize Steam, so reconnaissance never calls it. Static fields
on types with a type initializer are skipped to avoid triggering initialization.
GameObject.Find of the source's known object name inspects an existing object only.
Consequently missing Steam singleton evidence is a deliberate limitation, not a
claim SteamAPI is unavailable. Accessing networking APIs to find out is separate work.

## Time and schedules

VERIFIED_RUNTIME: R8 complete Enviro root captured GameTime.ProgressTime=Simulated,
Hours=6, Minutes=59, Seconds=43, Days=1, Years=2021, DaysInYear=365,
cycleLengthInMinutes=26, day/night multipliers=1 and utcOffset=0. GameManager
publishes derived current hour/minute/day state (Monday index 0 in the observed UI).
The phone clock advances in gameplay; pause UI can stop progression via TimeProgressMode.

VERIFIED_STATIC: GameManager derives its clock from Enviro; NPC habit and story
conditions read time, SaveGame stores GameTime/Day/Week/TotalHoursPassed; sleeping
and time-skip paths alter it. Player branches can change cycle length (examined
values 28/48), so the observed 26-minute cycle is not a universal fixed rule.

Future design: host owns monotonically versioned world time and calendar/skip
events. Clients smooth display time between host anchors; schedule/objective
evaluation occurs once on host. Sleeping/time skip requires a cooperative barrier
or explicit policy for the other actor. Runtime sleep/skip and schedule rollover
acceptance remain UNKNOWN.

## Randomness and physics

VERIFIED_STATIC: random outcomes occur in loot/world markers, sidewalk spawn choice,
NPC/door branches, GameManager.RandomVan and audio pitch. UnityEngine.Random call
order differs when each peer executes local AI/input/effects. No deterministic world
simulation was established. Replicate host outcomes with logical IDs; seeded RNG can
remain useful inside one isolated host subsystem, not as a global synchronization promise.

VERIFIED_RUNTIME/STATIC: player uses CharacterController/motor transform movement;
cars use Rigidbody + EVP; carried/dropped loot has changing Rigidbody/parenting;
doors/windows include transform/animation movement. Classify player/cars/claimable
heavy props, collision-relevant obstacles and gameplay ragdoll transitions as
relevant. Ambient particles, noninteractive debris, foliage and audio variations
are presentation/cosmetic unless a specific mechanic proves otherwise. Do not
replicate every decorative Rigidbody indiscriminately.
