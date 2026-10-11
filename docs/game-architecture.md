# Game architecture

These earlier static notes are supplemented by the later
[comprehensive runtime evidence](full-runtime-recon.md), including complete player,
NPC visual, pickup and scene-recreation inspection. Historical visual/160-node limits
below do not describe the new diagnostic experiment; production peer acceptance
remains separate.

Scope: the Assembly-CSharp SHA-256 recorded in [runtime-analysis.md](runtime-analysis.md). These are our interoperability notes, not reconstructed source. **Verified** below means implementation inspected unless explicitly marked runtime. Local decompilations and complete signatures remain ignored.

## Player

**Verified:** `PlayerScript` owns broad gameplay state and input-driven interactions. `Start()` binds `ReInput.players.GetPlayer(0)`, gathers `ItemID` components beneath `itemIDsHolder`, resolves the GameManager tag, initializes local hand animations, obtains its own CharacterController, changes collision/input/UI state, and starts `OnAwakeDelay()`.

`PlayerMotorBehavior` requires CharacterController, PlayerCameraBehavior, and AudioSource. Its Start binds Rewired slot 0; Update calls `CalculateMovement()` and `RefreshStates()`. Movement reads named axes `left_right`/`forward_back`, run/jump/crouch actions, derives a direction using player/camera transforms, and calls CharacterController.Move. It implements gravity, crouch height, slopes, footsteps and movement flags (`isMoving`, `isRunning`, `isCrouching`). The player's owning transform is the component GameObject transform; use world position/world yaw for packets.

`PlayerCameraBehavior.Start()` also binds slot 0 and obtains motor/controller/PlayerScript on its GameObject. `HandleMouseLook()` reads Rewired look axes and writes root yaw and camera-local pitch; head-bobbing/landing motion use `headTransform` and `playerCamera`.

GameManager exposes `Player` and `PS` references. Many objects cache a single `FindGameObjectWithTag("Player")` result. There is no observed collection of network-capable players. **Verified gameplay:** the local root is `IMPORTANT OBJECTS[0]/First-Person Character[19]` in MainScene. CC, PMB and PCB fields all resolve to that object; GameManager.Player also points to it. See [the exact observed hierarchy](live-player-hierarchy.md) for root components, every immediate child and relevant camera/hand/body branches. The old capture was capped at 160 nodes; deep bones remain incomplete.

**Verified gameplay visuals:** first-person hand legacy Animation branches live beneath Tilt/Head/HandsCam. `LEGS/Main Character Legs` contains Animator/GrounderIK, active Bottoms/Shoes and inactive full-body Body/Tops/Gloves/Eyes/Eyelashes/Hats SkinnedMeshRenderers. A hidden full body exists. CarController.tpp_character models were also observed with Animation and CheckItemEnable; the latter reads the local inventory and is unsafe to copy. Phase 1 bakes selected local body geometry into fresh MeshFilter/MeshRenderer objects, with original generated capsule fallback. In-world baking/render visibility is still unverified.

`PlayerController` is a ProceduralWorlds ivy-casting demo. `SimplePlayerController` and other third-party/demo controllers must not be mistaken for the playable thief. Static files do not prove what is attached in a save.

**Verified gameplay ownership:** a MainScene Single reload replaced PlayerScript instance 316802 with 583394 and GameManager 308524 with 575116. Neither observed player nor GameManager survived that reload. Disabling only camera/motor on a clone does not prevent startup side effects. The mod owns a persistent session object and destroys its proxy/disconnects at scene transitions.

**Final transform sampling:** own LateUpdate with execution order 10000 reads root world position and Euler Y. Runtime positions changed during walking and PMB/PCB/CC share the root. Inspected movement and mouse look run in Update; camera position contains child headbob/lean/pitch and is unsuitable as body position. Pitch is not synchronized. Sampling execution in the final plugin remains pending gameplay acceptance; no game movement method is patched.

## Interaction flow and identity

**Verified:** PlayerScript.Update raycasts from `cam` with layer masks and action/state gates. Different paths use approximately 1 m, 2.25 m and 2.75 m ranges. It dispatches by Transform tag and concrete component, using the local Rewired `interact` action. There is no universal gameplay Interactable interface established by this inspection.

Doors: `Door.Start()` caches the unique PlayerScript and local transforms/audio. `Use()` checks debounce/availability/NPC use, flips `isOpen`, starts animation, modifies the player's camera sensitivity/movement/noise, and plays audio. `UseFully()` and NPC-specific paths can also change door state. Slow-door input first sets `assignDoor.load_slow`; Door.Update performs the ongoing action. A single Use hook will not cover all door changes.

Window traversal: `WindowUse.UseWindow()` disables player motor/camera, starts `GetIn()`, and Update moves the cached player's transform. `LockedWindow` handles breakage; `Lockpick`/other minigames handle lock state. Network playback of traversal cannot blindly call a method bound to the receiver's local player.

Pickups: PlayerScript verifies capacity/weight and calls `Pickupable.PickUp()`. This starts `PickingItem()`, which delays, disables colliders, creates UI/audio effects, modifies money or delegates `ItemID.Item_Picked`, advances some mission/story states, and destroys the world object. Completion occurs in the coroutine, not at the PickUp return boundary.

**Verified identity distinctions:** `ItemID.item_ID` identifies a catalog definition, not a unique world instance. Pickup persistence uses `"Item_" + transform.name` and respawn days use `"ItemDay_" + transform.name`. Optional `prefName`/`parcel_pref` provide further save relations. `Inventory.DropItemNewSystem()` can name spawned objects `dropped`, directly disproving name-only uniqueness for dynamic loot. `Door_ID` is an empty MonoBehaviour marker; its name does not establish an identifier. A universal GUID/persistent-ID contract was not found in the inspected interactables or identity search candidates.

**Proposed, not verified:** For authored objects, combine scene path + full hierarchy with sibling indices + concrete component identity/ordinal, validated against a host registry and scene/build fingerprint. Hierarchies are diagnostic locators, not automatically deterministic IDs: activation, loading order, instantiation and rename operations can diverge. Host-assigned spawn IDs are required for dynamic objects; do not use Unity GetInstanceID across peers. Duplicate/collision checks and two-process scene comparisons remain outstanding.

`LootBox` and `Loot_Manager` are misleading as generic inventory hooks: LootBox.Start/Update are empty; Loot_Manager is a display-info helper. Search is based on implementation, not naming.

## Inventory and progression

**Verified:** `Inventory` coordinates UI, equipment selection, item lists and dropping. `PlayerScript.item_IDs`/`item_IDs2` reference catalog `ItemID` objects. ItemID fields include item ID/name/description, weight/value, item/storage/locker amounts, flags and drop prefab. `Item_Picked(string,float,bool,bool)` increments non-heavy quantity/weight, marks the world object's save key, and may award XP/special points/achievements. `Drop_Item()` decrements quantity/weight and has specialized drone/car behavior.

Inventory.DropItemNewSystem instantiates `drop_object` at `dropPos`, sets Pickupable's ID/wasDropped, activates Rigidbody physics and calls ItemID.Drop_Item. Storage moves and selling are separate ItemID mutation paths. Saving quantities uses Item_ + catalog ID, Storage_Item_ + ID and Locker_Item_ + ID. This is scattered mutable state, not a single central inventory transaction API.

Money, debt, level, XP and skill points live on PlayerScript (`moneyAm`, `debtAm`, `currentLvl`, `currentXPPoints`, `skillXP`, others). SaveGame writes Money/Debt/Lvl/XP/XPPoints etc.; GameManager.AddXP and level-up logic are additional routes. `Skill_Manager.Start` assigns Skill_ + index keys; Learn_Skill sets the preference and isLearned. Extra upgrades can use skill-name keys.

## NPCs and police

**Verified:** `AI` is the citizen component, obtains `Pathfinding.RichAI` and caches the tagged PlayerScript in Start/OnEnable. SensorToolkit RaySensor/RangeSensor and related Sensor_Range objects drive detection/hearing; `Player_Is_Seen`, `Player_Lost`, `Player_In_Hear_Range`, `HeardSoundAction` and Update modify state and RichAI.destination. Habits are hour-indexed and coupled to GameManager time; `ReloadHabits()`/`ChangeBehaviour()` control schedules. NPCHabits is mainly player-facing habit display/control, not a universal NPC simulation owner.

GameManager gathers objects tagged NPC; SidewalkSpawner.Start randomly places scene-provided NPC objects among spawnTransforms. Police_Manager gathers PoliceSpawn tags, owns stars/chase/spawn/last-known-location logic, and instantiates police cars/helicopters. `PolicemanAI` implements police behavior with its own single-player references. GuardAI, SidewalkAI, dog/cat and other variants exist and were indexed; not every variant was fully decompiled.

Host-owned AI is a sensible future design, **not proven** by disabling AI.Update alone. Navigation, sensors, coroutines, spawning, damage, audio and police escalation need authority gates. Detection currently depends on one cached PlayerScript, so two targets require an explicit perception adapter.

## Vehicles

**Verified:** Game-specific CarController wraps `EVP.VehicleController` and Rigidbody. StartVoid binds Rewired slot 0/cached PlayerScript and camera references. EVP.VehicleStandardInput.FixedUpdate reads the player's car axes and writes steering/throttle/brake/handbrake. EVP.VehicleController.FixedUpdate runs wheel/physics simulation. This establishes separate input and physics responsibilities; it does not prove network ownership.

CarController.EnterVehicle/ExitVehicle change current_vehicle, player control maps, collider/motor/camera state and seating; RunCar reparents the player beneath a seat transform. VehiclesManager.InitiateMainCar selects/activates firstCar or van from save prefs and invokes StartVoid. VanHeavyItems serializes item indices and local poses, instantiates catalog drop prefabs, parents them to the vehicle and changes physics. Stolen-car systems add further specialized behavior. Passenger support is unknown.

## Missions and saves

**Verified:** StoryManager maintains story index, a list of StoryScript configuration components, currentStoryScript, phone call/UI logic and polling objectives. BeginStory reads Story; AdvanceStory increments/initiates the next stage; SaveStory writes Story. Some transitions are coupled to money, items, police and scene location.

Mission_Item relates house_number/mission_int to SelectResidence.residence_missions, uses mission_prefs, and completes a mission through its guarded Mission_Complete path. Observed convention: 1 means active and 2 means completed. Mission_Manager mainly builds display information rather than owning all progression.

SaveGame.Start loads player state; SaveGameNewSystem(bool,float) writes extensive player/world/progression values and flushes via FBPP.Save or SaveToMainProfile. SaveGame1 is a guarded quick-save path. GameManager.SaveAll is another aggregate route. SaveGame.OnApplicationQuit calls LoseGame, but LoseGame is empty in this build: window close does not establish a quicksave. Inventory.QuitGameIE(save path) calls SaveOnTheRun → PlayerScript.Quicksave before quitting/loading menu. Later read-only observers verified FBPP.Save/write on Continue, while save-confirmation completion remains unknown; see [scene/save trace](scene-sync-analysis.md).

SPrefs forwards common one-argument getters and setters to FBPP; some overloads retain older secure-PlayerPrefs behavior. InitializePrefs.Reinitialize selects profile save filenames `saveData.thief` through `saveData5.thief`, disables AutoSaveData, enables scrambling, and chooses Application.persistentDataPath. FileBasedPrefs FBPPFileModel contains typed StringData/IntData/FloatData/BoolData key-value arrays. FBPP serializes with Unity JsonUtility and applies repeated-key character XOR in DataScrambler, with backup/profile routines. Save format is scrambled JSON, not a network protocol or a relational world-object registry. No save files were decoded or edited.

## Scenes

**Verified static:** MainMenu.LoadNewSceneAsync loads MainScene in Single mode. GameManager then loads a selected world scene Additively in LoadNewSceneAsync, stores loaded_map_name and waits for completion. Known string targets include PlayerHideout, PawnShop, Auto Shop, MadisonSt, Hillville, DrugFarm, Warehouse, BankScene, Forestside Resort, HauntedMansion, Museum, SwatRange, Jail and IntroHouse. HackingPuzzle is also additive; MainMenuNew is the menu.

Travel paths update save/location/spawn state and reload MainScene in Single mode via LoadWait/LoadYourAsyncScene. Some player movement can bypass the motor (vehicles, window traversal, scripts), so future transform sampling should occur after all game updates, not solely after motor.CalculateMovement.

**Verified runtime:** MainMenuNew unloaded; MainScene loaded Single at 11:22:17 UTC and IntroHouse loaded Additive at 11:22:20 UTC. MainScene remained active. At 11:23:24 UTC both gameplay scenes unloaded; MainScene Single and IntroHouse Additive loaded again with new player/manager instance IDs. The trigger was not captured, so it is called a reload rather than proven travel. Other locations/puzzles remain unobserved. SteamManager persistence is static evidence only. Phase 1 disconnects on any scene lifecycle change, reacquires the local player afterward and requires manual reconnect; temporary player absence pauses sends/hides the proxy.
