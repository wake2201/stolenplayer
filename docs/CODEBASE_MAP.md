# CODEBASE_MAP — Thief Simulator 2 Multiplayer

Navigation index. Created M0 (2026-11-09). **V** = verified locally, **H** = hypothesis, **U** = unknown.
Verify an entry against the current binaries before patching it. Method names/signatures below were read with .NET reflection; method *bodies* were inspected only through IL call/field-reference scans, not full decompilation.

## 1. Project repository — `D:\TS2-Multiplayer` (git, separate from the game folder since M1)
| Path | Content |
|---|---|
| `AGENTS.md`, `CLAUDE.md`, `docs/PROJECT_PLAN.md` | Rules and roadmap (moved here from the game folder in M1; paths now consistent) |
| `docs/` | `CODEBASE_MAP.md`, `PROGRESS.md`, `TESTING.md` |
| `src/Shared/Protocol.cs` | Protocol constants (`ProtocolVersion=1`, `ModVersion=0.1.0`), `Hello`, `Welcome`, `RejectCode`, `Validation` |
| `src/Shared/SessionNode.cs` | LiteNetLib host/client state machine (`ConnState`), handshake, reconnect, shutdown |
| `src/Shared/PlayerState.cs` | M2: `PlayerSnapshot` + `PlayerStateCodec` (29 B), `MoveFlags`, `SceneInfo`, `SceneKeys` (FNV-1a), `SnapshotValidation`, `RemoteVisibility` |
| `src/Shared/SnapshotBuffer.cs` | M2: interpolation buffer (clock-offset estimate, delay, teleport/gap snap, bounded extrapolation) |
| `src/Shared/InstanceLog.cs` | Per-instance log file (UTC, level, role+instance, session, state, message) |
| `src/Plugin/Plugin.cs` | BepInEx entry `com.ts2mp.multiplayer`; `LaunchArgs` (`-ts2mp-*`); `NetRunner` MonoBehaviour (tick, overlay, shutdown) |
| `src/Plugin/PlayerSync.cs` | M2: reads local player (reflection, read-only), scene key, 20 Hz send, validation, avatar visibility |
| `src/Plugin/RemoteAvatar.cs` | M2: clones the game's associate model as a script-free visual, legacy-Animation clip selection |
| `src/Plugin/SaveGuard.cs` | Client-only save protection (sandbox redirect + guards), fail-closed |
| `src/Plugin/PathRedirect.cs` | Unity-free IL scan + transpiler for `Application.persistentDataPath` callers |
| `src/Launcher/Launcher.cs` | `TS2-Multiplayer-Launcher.exe` (role assignment, backup, process start, session state) |
| `tests/` | `ProtocolTests.cs` (UDP loopback), `SyncTests.cs` (M2 codec/interpolation), `RedirectTest.cs` (real game IL), `launcher-test.ps1` + `FakeGame.cs` |
| `tools/build.ps1` | csc build (.NET Framework csc, C# 5; no SDK); `-Deploy`, `-Test` |
| `tools/bepinex.ps1` | Pinned BepInEx 5.4.23.5 x64 install/uninstall (SHA-256 checked, never overwrites) |
| `lib/LiteNetLib.dll` | LiteNetLib 1.3.1 `net471` build (MIT, `lib/LICENSE-LiteNetLib.txt`) |
| `game-path.txt` (gitignored) | Game folder used by build scripts |

### M1 runtime layout
- Data dir (default `%LOCALAPPDATA%\TS2MP`): `session.state`, `logs\launcher.log`, `logs\<stamp>_<role>_<instance>\{plugin.log,unity.log}`, `backups\<stamp>\{saves\,playerprefs.reg}`, `client-sandbox\`.
- Game folder additions: `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`, `BepInEx\` (incl. `plugins\TS2MP\TS2MP.Plugin.dll`, `LiteNetLib.dll`).
- Launcher → game arguments: `-logFile <dir>\unity.log -ts2mp-role host|client -ts2mp-session <hex12> -ts2mp-instance <hex8> -ts2mp-port <n> -ts2mp-logdir <dir> -ts2mp-data <dataDir> -ts2mp-bind <ip> -ts2mp-connect <ip>` (bind/connect added in M2). Without `-ts2mp-role` the plugin is inert.

## 2. Game installation (V)
- Root: `D:\Thief-Simulator-2-AnkerGames\Thief Simulator 2\` (the repo root is the game folder itself)
- `Thief Simulator 2.exe`, `UnityPlayer.dll` FileVersion `2020.3.33.9525882` → **Unity 2020.3.33f1** (also in `globalgamemanagers`)
- Game version string next to the player settings in `globalgamemanagers`: `1.27c` (H: bundleVersion; no other version source found)
- `app.info`: company `CookieDev`, product `Thief Simulator 2`
- Runtime: **Mono** (`MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll`, `Managed/*.dll` present, no `GameAssembly.dll`/`il2cpp_data`)
- API level: `mscorlib 4.0.0.0`, `netstandard 2.0.0.0` → target **net46–net472 / netstandard2.0** for plugins (H: net472 is the safe choice)
- Distribution: **AnkerGames repack**, not a Steam install: `Plugins/x86_64/steam_emu.ini` + `steam_api64.rne` (Steam emulator, AppId 1332720). See PROGRESS "Blockers".
- Steamworks.NET 20.0.0 / SDK 1.52 (`Plugins/Steamworks.NET.txt`)
- Mod loaders present: **none** (no BepInEx, `winhttp.dll`, `doorstop_config.ini`, MelonLoader)
- Scenes (build order, from `globalgamemanagers`): MainMenuNew, MainScene, HackingPuzzle, MadisonSt, PlayerHideout, Auto Shop, IntroHouse, Warehouse, Hillville, Jail, BankScene, EndStory, Forestside Resort, HauntedMansion, Museum, SwatRange, DrugFarm

### Assemblies of interest (`Thief Simulator 2_Data/Managed/`)
| Assembly | Role |
|---|---|
| `Assembly-CSharp.dll` (5.8 MB, 4101 types) | All game logic; game classes are mostly in the **global namespace** |
| `FileBasedPrefs.dll` | Save backend (`FBPP`, `FBPPConfig`) |
| `Rewired_Core.dll` | Input (`Rewired.Player`) |
| `AstarPathfindingProject.dll` | NPC navigation (`RichAI`, `Seeker`) |
| `com.rlabrecque.steamworks.net.dll` | Steam API |
| `UnityEngine.UNETModule.dll` | Present but deprecated; not a recommended transport |

## 3. Game systems

### 3.1 Player, movement, input, camera (V)
- `PlayerScript` (MonoBehaviour, ~730 members): **god object** for the player. Fields: `CharacterController CC`, `PlayerMotorBehavior PMB`, `PlayerCameraBehavior PCB`, `Rewired.Player rewiredPlayer`, `Rigidbody myRigid`, `Inventory invScript`, `GameManager GM`, `Police_Manager PoliceManager`, `CarController current_vehicle`, `int moneyAm`, `float currentXPPoints`, `bool isPoliceChase`.
- `PlayerMotorBehavior`: `CalculateMovement()`, `UpdateGravity(float,float,float)`, `RefreshStates()`, `OnControllerColliderHit(...)`; fields `moveSpeed, runSpeed, crouchSpeed, currentSpeed, targetSpeed, velocityY, inputVector`.
- `PlayerCameraBehavior`: `HandleMouseLook()`, `SetYaw(float)`, `CarEnterRot(float)`, `CarExitRot(float)`.
- `LegsController` (`Animator animator`, `Vector2 inputDir`): first-person legs. `InverseKinematicsController.OnAnimatorIK()`.
- Input is read through Rewired in `PlayerScript`, `PlayerMotorBehavior`, `CarController`, `GameManager`.
- Movement/state fields used by M2 (V, reflection): `PlayerMotorBehavior.isGrounded/isMoving/isRunning/isCrouching`, `moveSpeed/runSpeed/crouchSpeed`; `PlayerScript.CC`, `PMB`, `MainCam`, `is_in_vehicle`, `isHiding`. M2 reads them only; feet = `CC.bounds` (center x/z, min y), yaw = `MainCam` yaw. The remote avatar is **not** a second `PlayerScript`.

### 3.2 Models / animation (V unless noted)
- NPCs use **legacy `UnityEngine.Animation`**, not Animator: `AI.NPCAnim`, `PolicemanAI.anim`, `SidewalkAI.anim` (+ clip-name strings such as `IdleAnim`, `idleNames`).
- `CarController.tpp_character_anim` (legacy `Animation`): a third-person driver model exists inside the car prefab (H: candidate Player-2 avatar source).
- `MainMenu.ActivateCharacterSelect()` exists (U: what it selects).
- **M2 verified (UnityPy read of `level1` = MainScene):** `Codename/anim` (root `Codename` inactive, has Rigidbody+BoxCollider; owner script presumably `CodenameAI`, U) holds a legacy `Animation` with clips **Idle, Walk, Run, Sneaking, Kneel, Sitting, CarryRun** (all `legacy=True`, default `Idle`) and 7 `SkinnedMeshRenderer`s (Body, Bottoms, Eyelashes, Eyes, Hats, Shoes, Tops) whose bones (Mixamo `mixamorig:*`) are all inside `Codename/anim`. Same rig/clips in `level9` (Jail) `GIRL ACTIVATE/GIRL/anim`. **Used as the remote-player model.**
- Main character models exist only in menus/intro: `level0` `new main guy` (Idle_1, Sitting), `level3` `INTRO STORY CANVAS/.../Main Character Final` (Idle3, Lifting, Walk) — no run/crouch, UI-parented.
- Pause-menu preview `IMPORTANT OBJECTS/Canvases/PAUSE MENU CANVAS/ITEMS SHOWCASE/associate prev` (layer 23) has the same clips — excluded (UI).
- NPC civilians use `Anim` objects with Baseball_Idle/Running/… (no plain Walk) — not chosen.
- Player body: FP legs use `Animator` (`LegsController`). U: whether a full third-person player mesh exists.

### 3.3 Interactions, pickup, locks, doors (V)
- `Pickupable`: `PickUp()`, `PickUpHeavy(float)`, `DropHeavy()`, `PlacedInTrunk()`, `WasPlacedInCar()`; fields `int item_ID`, `ItemID itemScript`, `advance_story_if_picked`, `story_name`.
- `ItemID`: `Item_Picked(string,float,bool,bool)`, `Drop_Item()`, `Move_To_Storage(int)`, `Load_Item()`, `Save_Item_Amount()`; fields `int item_ID`, `string item_name`, `float item_value`, `List<> pickedNames`. `item_ID` is a **type id**, not a per-instance id (H, from naming + `PlayerScript.AddItem(int)`).
- `Door`: `Use()`, `UseFully()`, `NPCUse(bool)`, `OpenDoorNPC()`, `CloseDoorNPC()`, `Unlock_Key()`, `ResetLock()`; fields `bool isOpen`, `int key_item_ID`, `bool was_locked`, `bool openedByPlayer`. `Door_ID` has no members (marker component).
- `Lockpick`: `StartLockpicking()`, `DoorUnlock()` (→ `StoryManager.AdvanceStory`), `RelockDoor()`, `BreakPadlock()`. Also `MediumLockMaster.Unlocked()`, `AutoLockpick`, `ElectricLock`, `LockedWindow`, `HidingSpot.GetIn(bool)/Unhide()`.
- Minigame mode switches live on `PlayerScript`: `Lock_Player_Minigame(int)`, `LockpickEnable()`, `DrillEnable()`, `SafeEnable()`, `HackingPuzzleEnable()` …

### 3.4 Inventory / items (V)
- `Inventory` (~490 members; also hosts settings/options UI): `PickedItemNewSystem(ItemID,Item_Button)`, `DropItemNewSystem()`, `Sell_Item_NewSystem(bool)`, `MoveItemToStorage()`, `LoadInventory()`, `ClearInventory()`, `AddMoneyMade(int)`, `OpenShop(bool,float)`, `OpenStorage(int)`.
- `PlayerScript.AddItem(int)`, `RemoveItem(int)`, `ScrollItem(bool)`, `Select<Tool>()` methods.
- Data classes: `PickedItemScript{item_name, real_name, add_xp, item_id, …}`, `Loot_Item_Inventory{loot_prefs, …}`.

### 3.5 Missions / story (V)
- `StoryManager`: `int story`, `StoryScript currentStoryScript`, `List story_scripts`; `AdvanceStory()` (no args), `SetStory(string)`, `SaveStory()`, `InitiateStory()`, `CheckPoliceEscaped()`.
- **`StoryManager.AdvanceStory()` is the main story choke point**: ~30 callers (triggers, `Lockpick.DoorUnlock`, `Pickupable.WasPlacedInCar`, `Mission_Item.MissionComplete1`, `Shop_Manager.Buy_Item`, `ReadNote.BeginRead`, `Inventory.Sell_Item_NewSystem`, `AI.DiscoverHabit`, …).
- `Mission_Inventory` (serializable data): `mission_prefs`, `mission_pay`, `mission_add_XP`, `required_number_complete`, `current_number`, `on_complete_advance_story`, `display_as_complete`.
- `Mission_Item`: `int mission_int`, `SelectResidence assigned_residence`; `Mission_Complete()` writes `current_number`; `MissionComplete1()` writes money, XP, `display_as_complete` and calls `GameManager.PlayMissionComplete`, `Inventory.AddMoneyMade`, `StoryManager.AdvanceStory`.
- `Mission_Manager.Load_Mission_Info(SelectResidence)` (UI). Others: `ContractManager.ContractComplete()`, `JobManager.JobSuccess()`, `JobOffer.ClaimRewards()`, `Payphone.Success()`, `PayphoneMission`, `HeistManager`.
- Progress is persisted as **SPrefs keys** (`mission_prefs`, `story_name`, `prefsHabitName` strings) (V for key fields; H for exact key format).

### 3.6 Money, XP, skills (V)
- Money = `PlayerScript.moneyAm` (int field). **Written directly from ~40 methods** (shops, sales, missions, Darkbank, crashes, `GotCaught`, `SaveGame.LoadGame`…), **no setter**.
- XP = `PlayerScript.currentXPPoints`; `GameManager.AddXP(int)`, `Level_Up_Check()`, `AddSkillPoint()`; `PlayerScript.RefreshXP()`, `CheckLevelUp()`.
- Skills: `Skill_Manager.Learn_Skill(int)`, `Check_If_Skill_Learned(string)`; `Skill_Instance{skill_pref, isLearned, …}`; `AbilitiesManager.SaveAbilities()/LoadAbilities()`.
- `PlayerScript.IncreaseReputation(int)`.

### 3.7 Vehicles (V)
- `CarController`: `EnterVehicle()`, `ExitVehicle()`, `ExitCar()`, `RunEngine(bool)`, `RunCar(bool)`, `ExitCarPolice()`, `TriggerAlarm()`; fields `VehicleController drivetrain`, `Rigidbody thisRigid`, `bool engine_running`, `is_sitting_inside`, `is_player_car`, `PlayerScript PS`, `Rewired.Player rewiredPlayer`, `TPP_Camera_Controller tpp_camera`. Sets `PlayerScript.current_vehicle`.
- `VehicleController`: 4 `WheelCollider`s + `Rigidbody rb`, `torqueForceWheel` (Unity WheelCollider physics).
- `EnterCar.EnterVehicle()`, `ExitCar.ExitVehicle()`, `VehiclesManager.InitiateMainCar()/SaveAllCarsTrunks()`, `StolenCarsManager`, `OtherCarController`, `CopCar`, `AICar`, traffic: `TurnTheGameOn.SimpleTrafficSystem.*`.

### 3.8 NPC, police, detection (V)
- `AI` (homeowners): `RichAI agent`, `GameObject player`, `PlayerScript PS`, `sees_player`, `Player_Is_Seen()`, `Player_Lost()`, `HeardSoundAction(Vector3,bool,float)`, `EscapeHomeAndCallCops()`, `ApplyEvent(string)`.
- `PolicemanAI`, `SidewalkAI`, `GuardAI`, `DogAI`, `CatAI`: same pattern — **one `PS`/`player` reference each**.
- Chase: `PlayerScript.StartPoliceChase(int,AI)` → `Police_Manager.Activate_Chase(int,AI,bool)`; end via `PlayerScript.StopPoliceChase()` → `Police_Manager.End_Chase()`. Callers of `StartPoliceChase`: AI, Alarm, CameraTrigger, CarController, Lockpick, SidewalkAI, Security_Panel_Minigame, … Detection helpers: `ShadowDetector`, `SensorToolkit` (`RaySensor`), `DailyEventManager`, `NPCHabits`.
- Caught: `GameManager.GotCaught(int)`.

### 3.9 Scenes / loading (V)
- `GameManager.LoadNewSceneAsync(string)`, `LoadYourAsyncScene()`, `LoadMain()`, `LoadMenu()`, `LoadCheckpoint()`; map selection `GameManager.Select<Map>()` (each calls `SaveGame.SaveGameNewSystem` first). `MainMenu.LoadNewSceneAsync(string)`, `StartNewGame()`, `Continue()`.
- `GameManager` holds spawn transforms (`SpawnHideoutPlayer`, `SpawnMadisonCar`, …) and `GameObject Player`, `PlayerScript PS`.

### 3.10 Save / load (V)
- Path: `Application.persistentDataPath` = `%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2\` (H: derived from `app.info`; folder does **not exist yet** on this account).
- Files: `saveData.thief` … `saveData5.thief` (5 profiles, `InitializePrefs.Reinitialize`), `saveInfo0.txt`, `saveDataBackup.thief` (`MainMenu.BackupSave`), `backups/saveData_backup*.thief|.txt` (`FBPP.BackupSaves`), graphics settings file (`GraphicsSettingsMenu.SaveMenuVariables`).
- Backend: `SPrefs` (salted wrapper) → `FBPP` (FileBasedPrefs). Config in `InitializePrefs.Reinitialize()`: `AutoSaveData = false`, `ScrambleSaveData = true` (IL constants, V).
- Disk write triggers: `FBPP.Save()`, `SaveToMainProfile()`, any `FBPP.Delete*`, `DeleteAll()`. `Set*` only writes in memory while AutoSave is off.
- High-level save: `SaveGame.SaveGameNewSystem(bool,float)` ← `PlayerScript.Quicksave(bool)`, `GameManager.Select<Map>()`, `GameManager.EndingFade()`, `SaveGame.SaveGame1()`. Also `GameManager.SaveAll()`, `SaveToProfile(int)`, `LoadProfile(int)` (does `File.Delete`/`File.Copy`).
- Profile selection uses Unity **PlayerPrefs** key `Profile` (`MainMenu.SetProfile(int)`) → Windows registry `HKCU\Software\CookieDev\Thief Simulator 2` (H: Unity default; key not present yet).
- M1 additions (V, IL scan of all `Managed/*.dll`): exactly **13** methods call `Application.persistentDataPath`: `FBPPConfig.GetSaveFilePath`, `FBPP.BackupSaves`, `InitializePrefs.Reinitialize`, `GameManager.LoadProfile`, `GameManager.SaveFileInfo`, `MainMenu.BackupSave/ClickedNewGame/LoadContinueTxt`, `SaveButtonInfo.OnEnable`, `DemonFile.SaveFileInfo`, `GetAudioRadio.Awake`, `GraphicsSettingsMenu.Start`, `URP_GraphicsSettingsMenu.Start`.
- `MainMenu.StartNewGame()` / `StartFreeMode()` call `SPrefs.DeleteAll()` → `FBPP.DeleteAll()` → writes an emptied save file to disk.
- `GraphicsSettingsMenu.SaveMenuVariables()` writes `QualitySettings.ini` (path built in `Start()` from `dataPath`/`persistentDataPath`) and PlayerPrefs.
- PlayerPrefs patchability (V): `SetInt/SetFloat/SetString(key,value)` and `GetX(key)` are managed wrappers (patchable); `GetX(key,default)`, `HasKey`, `DeleteKey`, `DeleteAll`, `Save` are internal calls (**not patchable** with Harmony). Managed PlayerPrefs deleters: `SPrefs.SecureDeleteKey`, `GameMenuController.DeletePlayerprefs`.

### 3.11 Steam
- `SteamManager.Awake()` calls `SteamAPI.RestartAppIfNecessary`, `SteamAPI.Init`; `AchievementManager.SetAchievement` → `SteamUserStats.StoreStats`. No `SteamRemoteStorage` calls in game code (V) → Steam Cloud (on a real Steam install) would be configured as Auto-Cloud, outside the code (U).
- No `Environment.GetCommandLineArgs` usage in game code (V): host/client role must come from our plugin.

## 4. Single-player assumptions (V counts)
- 326 call sites of `GameObject.Find*/FindWithTag/FindObjectOfType`; 227 `"Player"` string literals in Assembly-CSharp.
- Each NPC/door/car/pickup caches **one** `PlayerScript PS`.
- No static singletons on `GameManager`/`PlayerScript`; lookups go through Find/tag.

## 5. Re-running the inspection
M0 used an ad-hoc reflection helper (C# compiled with PowerShell `Add-Type`, loading `Managed/` with an AssemblyResolve hook) with four modes: type list, members of named types, string literals in method bodies, call/field-reference scans. It was kept in the session scratchpad, not the repo. No .NET SDK/ILSpy is installed (only the `dotnet` host, no SDKs); full method decompilation will need `ilspycmd` or the ILSpy GUI (user approval to install).

## 6. M2 multiplayer protocol (protocol 2, mod 0.2.0)
| Msg | Channel / delivery | Content |
|---|---|---|
| `Welcome` (1) | 0 ReliableOrdered | as M1 |
| `PlayerState` (2) | 1 **Sequenced** (unreliable, drops older) | slot u8, seq u32, senderTimeMs u32, sceneHash i32, feet pos 3×f32, yaw u16 (quantized), MoveFlags u8 = 29 B |
| `SceneInfo` (3) | 0 ReliableOrdered | slot, sceneHash, sceneKey (sorted loaded scene names joined `+`), present |
- Rate: 20 Hz while the local player exists (BepInEx config `Sync.SendRateHz`, 10–30), 2 Hz "not present" otherwise. ≈ 29 B payload + UDP/LiteNetLib headers per snapshot.
- Authority: each peer owns its own player. Receiver validates slot (host expects 2, client expects 1), finite/in-range values, newer seq (wrap-aware). Host relays only validated states (`SendToOthers`; no-op with one client).
- Interpolation: `SnapshotBuffer`, render time = now − clock offset (min-filtered) − 100 ms (`Sync.InterpolationDelayMs`); lerp position, shortest-path yaw; snap on jumps > 8 m or gaps > 1 s; hold last state (no extrapolation by default); speed→0 when data is > 250 ms late; avatar hidden after 3 s without data.
- Visibility (`RemoteVisibility`): connected + local player present + remote present + same scene hash + not in vehicle + not hiding + fresh data.
- Addressing: `SessionNode.BindAddress/ConnectAddress` (default 127.0.0.1). Launcher ini `Role`, `BindAddress`, `HostAddress`, `SessionKey` (LAN requires a key; session id = SHA-256("TS2MP:"+key)[0..16]).
