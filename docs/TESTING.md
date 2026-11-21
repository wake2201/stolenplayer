# TESTING

Results use PASS / FAIL / NOT TESTED. Only the user's actual in-game results can mark PASS.

## 1. M0 environment checks (static, by Claude)
| Check | Result | Evidence |
|---|---|---|
| Game exe + Unity version | PASS (static) | `Thief Simulator 2.exe` FileVersion 2020.3.33.9525882 |
| Runtime is Mono | PASS (static) | `MonoBleedingEdge/`, `Managed/*.dll`, no `GameAssembly.dll` |
| Mod loader installed | M0: none → M1: BepInEx 5.4.23.5 installed | `tools/bepinex.ps1 -Install` (hash-verified zip) |
| Save folder exists | none yet | `%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2\` missing |
| PlayerPrefs registry key | none yet | `HKCU\Software\CookieDev` missing |
| Two instances run concurrently | NOT TESTED | user-reported only |
| BepInEx loads in this build | NOT TESTED | needs in-game run (§4 test 4) |

## 1b. M1 offline tests (by Claude, `tools/build.ps1 -Test`)
| Suite | Result | What it proves |
|---|---|---|
| Build (csc, C# 5) | 0 errors, 0 warnings | Plugin compiles against the game's Mono libs + BepInEx/Harmony; launcher on .NET Framework 4.x |
| `TS2MP.ProtocolTests.exe` | 12/12 PASS | Handshake, reject codes, retry/reconnect, restart, port conflict over real UDP loopback — not inside Unity |
| `TS2MP.RedirectTest.exe` | 3/3 PASS | IL scan + rewrite of the 13 save-path methods on the real game IL — does not prove Harmony applies under Mono |
| `tests/launcher-test.ps1` | 15/15 PASS | Role assignment, refusals, rapid double launch, backup verification — with a fake game exe |

**Current totals (N1.2 0.12.0, 2026-11-12):** 23 builds 0 errors / 0 warnings; 24 suites all pass: the N1 list below with launcher-dev-test 18/18 (replaces installer-test), plus **LifecycleTests 17/17** and **package-test 14/14** (`tools\build.ps1 -Test` builds and verifies the drag-and-drop ZIP). Previous **(N1 0.11.1, 2026-11-12):** 23 builds 0 errors / 0 warnings; 22 suites all pass: ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 34/34, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16, CarBoxTests 12/12, Player2Tests 19/19, Player2LifecycleTests 18/18, JointTravelTests 33/33, WorldIdentityTests 16/16, WorldMirrorTests 26/26, W3Tests 35/35, DurabilityTests 11/11, WorldLootTests 23/23, **NetAuthTests 16/16**, RedirectTest 3/3, launcher-test 21/21, **installer-test 49/49**, **n1-smoke 3/3** (`tools\build.ps1 -Test` also builds and verifies the release ZIP). Previous (M4.3 Phase B 0.8.0, 2026-11-11): build 0/0; ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19 (case 15 now the Phase B rule), InteractionTests 16/16, CarBoxTests 12/12, Player2Tests 19/19, Player2LifecycleTests 18/18, **JointTravelTests 33/33** (new: host trip state machine, both driver roles, both cars, async arrival orders, duplicates/stale ids, CarBox/inventory, disconnects, timeouts, wrong scene, reconnect, solo/on-foot/different-region regressions, clocks/sleep, replication, cost), RedirectTest pass, launcher 21/21. Previous (M4.3.3 hotfix 0.7.1, 2026-11-11): build 0/0; same as below plus **Player2LifecycleTests 18/18** (integration: message sequencing, restore/ack, menu exit, rejoin, restarts, pending recovery, CarBox, stale reports, new game, write failure). Previous totals (M4.3.3, 2026-11-11): build 0/0; ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16, CarBoxTests 12/12, **Player2Tests 19/19**, RedirectTest 3/3, launcher 21/21. Previous (M4.3.2b): no Player2Tests. Previous (M4.3.2a): InteractionTests 15/15, no CarBoxTests. Previous (M4.3.1): InteractionTests 13/13, rest identical. Previous (M4.3 Phase A, 2026-11-11): build 0/0; ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19, RedirectTest 3/3, launcher 21/21. Previous (M5.0, 2026-11-11): build 0/0; ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37 (36/37 = M5.0 door sync/rules, replacing the M4.2.4 shell cases), RedirectTest 3/3, launcher 21/21. Previous (M4.2.3, 2026-11-10): build 0 errors / 0 warnings; ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, **HandoffTests 37/37** (M4.2.4: shell selection + trigger gating; M4.2.3: 9 cases, case 22 on-foot variant), RedirectTest pass, launcher 21/21. M4.2.2: HandoffTests 26/26. M4.2.1: same without HandoffTests. Earlier (M3.1): ProtocolTests 17/17, SyncTests 18/18, **WorldTests 30/30** (registry, revisions, duplicates, late join, reconnect, proposals, regions, per-region clocks, sleep requests, hourly-tick accounting), RedirectTest 3/3, launcher 21/21. None of these run inside Unity.

## 2. Save-collision risks (two instances, one Windows account)
Both processes resolve the same `persistentDataPath` and the same registry key:
- `saveData*.thief`, `saveInfo0.txt`, `saveDataBackup.thief`, `backups/` — written by `FBPP.Save/SaveToMainProfile/BackupSaves`, `MainMenu.BackupSave`, `GameManager.LoadProfile` (delete + copy). Two writers → last-writer-wins or a torn file.
- Map change (`GameManager.Select<Map>`), quicksave and ending all call `SaveGame.SaveGameNewSystem` → both instances will save during normal play.
- Registry (PlayerPrefs): `Profile`, keybindings (`KeyBinding.SaveKeyCode`), graphics, and Unity's own screen-resolution keys are shared.
- `Player.log` in the same folder: both Unity players write it (H: second instance may fail to open it or overwrite it).
- Steam Cloud: the current install uses an emulator, so no real cloud sync. On a legitimate Steam install, Steam Auto-Cloud could upload a corrupted save to all devices (U: game's Auto-Cloud config). Disable Steam Cloud for the game during development (Steam → game Properties → General).

## 3. Save protection (M1 implementation)
Implemented safeguards (code: `src/Plugin/SaveGuard.cs`, `src/Plugin/PathRedirect.cs`, `src/Launcher/Launcher.cs`):
- **Launcher, before every Host start:** copies the real save folder to `%LOCALAPPDATA%\TS2MP\backups\<stamp>\saves\`, verifies every file by SHA-256, exports the PlayerPrefs registry key to `playerprefs.reg`. If the backup fails, the host is not started. Backups are never overwritten or deleted.
- **Launcher refuses** to start when a game instance not started by the launcher is running (it would write the real saves unprotected), when the session is full, or when the host died while the client still runs.
- **Client plugin:** all 13 `persistentDataPath` callers are redirected to `%LOCALAPPDATA%\TS2MP\client-sandbox\` (seeded from the real folder, read-only). Verification: `FBPPConfig.GetSaveFilePath()` must resolve inside the sandbox, and every 2 s the active FBPP save target is re-checked. If either fails, the client quits (fail closed).
- **Client guards (defense in depth):** `FBPP.WriteToSaveFile` blocked unless target is in sandbox; `File.WriteAllText/WriteAllBytes/WriteAllLines/AppendAllText/Copy(dest)/Delete/Move` blocked for paths in the real save folder or game folder (except `BepInEx\`); `PlayerPrefs.SetInt/SetFloat/SetString`, `SPrefs.SecureDeleteKey`, `GameMenuController.DeletePlayerprefs`, `GraphicsSettingsMenu.SaveMenuVariables` (+URP), `AchievementManager.SetAchievement` blocked. Each block is logged as `BLOCKED_WRITE`.
- **Host:** unchanged game persistence (authoritative owner of the real saves).
- Remaining unblockable on the client: PlayerPrefs `DeleteKey/DeleteAll/Save` (internal calls; only managed callers patched) and Unity's native screen-resolution registry writes.

Manual precautions (user, once before the first M1 run):
1. Start the game **once normally** (double-click, single instance), reach the main menu, quit. The plugin stays inactive in a normal start. This creates the save folder.
2. Copy `%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2\` to a dated folder elsewhere (independent of the launcher backup).
3. `reg export "HKCU\Software\CookieDev\Thief Simulator 2" "%USERPROFILE%\Desktop\ts2_prefs_backup.reg"`
4. Use a throwaway profile slot for multiplayer tests.

Hash snapshot of the real save folder (run before and after a test, compare):
```powershell
Get-ChildItem "$env:USERPROFILE\AppData\LocalLow\CookieDev\Thief Simulator 2" -Recurse -File | Get-FileHash | Select-Object Hash, Path | Out-File "$env:USERPROFILE\Desktop\ts2_hashes_$(Get-Date -f HHmmss).txt"
```

## 4. M1 manual test checklist (user, two real instances)
Launcher: `D:\TS2-Multiplayer\build\TS2-Multiplayer-Launcher.exe` (settings in `TS2-Multiplayer-Launcher.ini` next to it). Logs: `%LOCALAPPDATA%\TS2MP\logs\`. Do **not** load into gameplay with two instances beyond the main menu / a throwaway profile until test 10 passes.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 Host start | Take hash snapshot. Double-click the launcher. | Console: `Role: HOST`, session, port, `Backup: …`. Game window opens; top-left overlay `TS2MP HOST <id> \| Listening`. New folder `logs\<stamp>_host_<id>\` with `plugin.log` containing `PLUGIN_INIT`, `SAVE_OWNER`, `NET_STARTED`, `STATE Stopped -> Listening`. | NOT TESTED |
| 2 Client start | Double-click the launcher again. | Console: `Role: CLIENT`, same session + port. Second window, overlay `TS2MP CLIENT <id> \| …`. Separate `logs\<stamp>_client_<id>\plugin.log`. | NOT TESTED |
| 3 Two processes | Task Manager → Details. | Two `Thief Simulator 2.exe` processes, PIDs match the launcher consoles / `launcher.log`. | NOT TESTED |
| 4 BepInEx init | Open `<game>\BepInEx\LogOutput.log` (and `LogOutput.log.1` if present). | `Chainloader started`, `Loading [TS2 Multiplayer 0.1.0]`. Client `plugin.log`: `SAVE_GUARD_ACTIVE`, `REDIRECT patched 13/13`, `VERIFY … (inside sandbox)`, `GUARD installed` lines. Report any `GUARD failed` / `FILE_GUARD` warnings. | NOT TESTED |
| 5 Auto connect | No input needed. | Client log `CONNECT attempt 1`, then `STATE … -> Connected`; host log `ACCEPT … client instance=<client id>`. Overlays show `Connected <-> <other id>`. | NOT TESTED |
| 6 Handshake | Compare both logs. | Host `HANDSHAKE_OK host side`, client `HANDSHAKE_OK client side: host=<host id> slot=2 mod=0.1.0 game=<version>`; same session id in both. `LATENCY` line every 30 s. | NOT TESTED |
| 7 Client restart | Close the client window normally. Wait 5 s. Start the launcher again. | Host log: `DISCONNECT … reason=RemoteConnectionClose` (or `Timeout` if killed), `STATE Connected -> Listening`. New client gets role CLIENT, same session, connects again (`HANDSHAKE_OK`). | NOT TESTED |
| 8 Disconnect handling | With both connected, close the **host** window. | Client log: `DISCONNECT … reason=RemoteConnectionClose`, `STATE -> WaitingRetry`, periodic `CONNECT attempt n`. No crash. Then close the client. | NOT TESTED |
| 9 Launcher errors | a) With host+client running, start the launcher a 3rd time. b) With only a client running (host closed), start the launcher. c) Start the game by double-click on the exe, then start the launcher. d) Set an invalid `GamePath` in the ini. | a) `Session full` (exit 4), no 3rd window. b) `host instance is gone but the client … still running`. c) `already running without the launcher`. d) `Game not found`. Each message also in `logs\launcher.log`. | NOT TESTED |
| 10 Save safety | After tests 1–8: take a new hash snapshot; inspect client `plugin.log` for `BLOCKED_WRITE` and confirm `client-sandbox\` received the client's files. Optionally in the client: main menu → New Game on a throwaway slot, then quit. | Real save-folder hashes changed only by the host (if the host saved); no file written by the client; client sandbox contains the client's writes; `backups\<stamp>\` exists with `saves\` (+ `playerprefs.reg` once the key exists). | NOT TESTED |

**M1 outcome (2026-11-09):** user reported both instances launched, connected and entered gameplay; M1 accepted. Log evidence received for tests 1, 2, 4 (client `REDIRECT patched 13/13`, `VERIFY … inside sandbox`), 5, 6 and the client side of 10 (`BLOCKED_WRITE` entries). Tests 3, 7, 8, 9 and the hash comparison of 10: no evidence received — remain NOT TESTED.

Report back: the console text, both `plugin.log` files, `launcher.log`, `BepInEx\LogOutput.log*`, and the two hash snapshots.

## 5. M2 manual test checklist (remote player + movement)
Setup: launcher as in M1 (`D:\TS2-Multiplayer\build\TS2-Multiplayer-Launcher.exe`, twice). Both instances need the **new** plugin (0.2.0); an old/new mix is rejected with `ProtocolMismatch`. Run the windows side by side (windowed mode in the game's options if needed). Use a throwaway profile.
Overlay (top-left): `TS2MP <ROLE> <id> | Connected <-> <other id> | remote <View> [clip=…]`. Name tag above the remote character.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Start Host via launcher | Overlay `HOST … Listening`; `plugin.log`: `SYNC_INIT slot=1 sendRate=20Hz` | NOT TESTED |
| 2 | Start Client via launcher | Overlay `CLIENT …`; `SYNC_INIT slot=2` | NOT TESTED |
| 3 | Wait for connection | Both logs `HANDSHAKE_OK`, overlay `Connected <-> <id>` | NOT TESTED |
| 4 | Load both players into the **same map** (e.g. both Continue/load the same town) | Both logs `SCENE local=<key>` and `SCENE remote=<same key> … (same as local)`; overlay `remote Visible` | NOT TESTED |
| 5 | Host looks for Client | Host sees the associate-thief character (name tag `Player 2 <id>`) at the client's position. Log `AVATAR_TEMPLATE source=MainScene:Codename/anim clips=…`, `AVATAR_SPAWNED … idle=Idle walk=Walk run=Run crouch=Sneaking`. Screenshot. | NOT TESTED |
| 6 | Client looks for Host | Same on the client (name tag `Host <id>`). Screenshot. | NOT TESTED |
| 7 | Move Host (walk a circle) | Client sees smooth movement, no stutter/teleports; overlay clip `Walk` | NOT TESTED |
| 8 | Move Client | Host sees smooth movement | NOT TESTED |
| 9 | Walk, turn on the spot, stop | Remote faces the walking direction/camera yaw; stops cleanly (`Idle`); no sliding after stopping (≤ ~0.1 s delay is expected) | NOT TESTED |
| 10 | Sprint, crouch, crouch-walk | `Run` while sprinting, `Sneaking` while crouched (frozen pose when crouch-idle). Note: feet on the ground? Model facing forward? (If backwards: set `Avatar.YawOffsetDegrees = 180` in `BepInEx\config\com.ts2mp.multiplayer.cfg`.) | NOT TESTED |
| 11 | Independence | Each window's mouse/keyboard only moves its own player; cameras unchanged; no extra audio; NPCs do not react to the remote character | NOT TESTED |
| 12 | Temporary disconnect: close Client, restart it via launcher, load the same map | Host: `AVATAR_DESTROYED (remote disconnected …)`, avatar gone; after reconnect `HANDSHAKE_OK`, avatar back, no duplicate character | NOT TESTED |
| 13 | Cleanup: one player changes map / returns to menu / enters a car | Other side: `REMOTE_VIEW Visible -> OtherScene` (or `NoLocalPlayer`, `InVehicle`); character disappears; returns when both are in the same map again | NOT TESTED |
| 14 | Logs | No `ERROR`/exceptions in `plugin.log`/`unity.log`; `SYNC_STATS` stale/invalid low; `LATENCY … traffic out=… in=…` (expected order of magnitude: ~1–3 KB/s each way) | NOT TESTED |
| 15 | Save protection regression | Client `plugin.log`: `REDIRECT patched 13/13`, `VERIFY … inside sandbox`, `SAVE_GUARD_ACTIVE`; real save folder unchanged by the client (hash snapshot as §3) | NOT TESTED |

Send back: screenshots for 5/6/10, both `plugin.log`, both `unity.log` (only if errors), `launcher.log`.
LAN (two PCs) is implemented but outside this checklist: NOT TESTED.

M2 outcome (user, 2026-11-10): **accepted** — both players see each other, walk/run/crouch animations, movement sync both ways, avatar hidden in different scenes and back in compatible ones. Rows not reported individually keep their state above.

## 6. M3 manual test checklist (shared world foundation, independent travel, shared time of day)
Setup: as §5, both instances need plugin **0.3.0** (M2 + M3 builds refuse each other: `ProtocolMismatch`). Use a throwaway profile; leave the hideout through the car's town selection as in normal play (Madison Street = `MadisonSt`, Auto Shop = `AutoShop`).
Overlay line 2: `TS2MP world | region <local> / remote <other> | time <local HH:MM> host <HH:MM> in sync|Blend|Snap | rev <n>` (host shows `(authority)`).

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Start Host, then Client via launcher | Both `plugin.log`: `WORLD_INIT … timeApi=ok` (host also `epoch=…`) | NOT TESTED |
| 2 | Connect | `HANDSHAKE_OK`; host `STATE_SNAPSHOT sent (remote connected …)`, client `STATE_SNAPSHOT received epoch=…` | NOT TESTED |
| 3 | Both load into the hideout | Both `REGION local none -> Hideout`; client `PROPOSAL sent … region=Hideout` then `PROPOSAL ack … Accepted`; overlay `region Hideout / remote Hideout`; host `REGION_OWNERS Hideout:P1` | NOT TESTED |
| 4 | Movement/animations regression | As §5 rows 5–10 (remote visible, Walk/Run/Sneaking) | NOT TESTED |
| 5 | Host (P1) drives to Madison Street; Client stays home | Host `REGION local Hideout -> none -> MadisonSt`; client overlay `remote MadisonSt`; client avatar of host disappears (`REMOTE_VIEW … OtherScene`); client is **not** moved | NOT TESTED |
| 6 | Connection during/after loading | No `RemoteDisconnected`/`HANDSHAKE` lines during the loading screen; overlay stays `Connected` | NOT TESTED |
| 7 | Client (P2) travels to Madison Street on its own | Client `REGION … -> MadisonSt`, `PROPOSAL ack`; host `REGION_OWNERS MadisonSt:P1` | NOT TESTED |
| 8 | Both in Madison Street | Each sees the other again; exactly one remote character each | NOT TESTED |
| 9 | Host returns to the hideout; Client stays in Madison | Host `REGION … -> Hideout`; host `REGION_OWNERS Hideout:P1,MadisonSt:P2` (order may vary); avatars hidden on both | NOT TESTED |
| 10 | Independent travel again (e.g. Client to Auto Shop, back) | Region lines on both; never a forced teleport; no session reset (client `STATE_SNAPSHOT received` only after a reconnect, rev keeps rising) | NOT TESTED |
| 11 | **Shared time of day** | Compare the time on both (overlay, phone/computer clock, sky). Client overlay `… in sync` within a few seconds of connecting (first sync may log one `TIME snap …`). Host sleeps in the hideout bed: client sky/time fast-forwards with it; `TIME day …` on the client after midnight | NOT TESTED |
| 12 | Time across regions | After each travel in rows 5–10: the client's time matches the host's again within a few seconds; host overlay never shows a jump backwards | NOT TESTED |
| 13 | Client reconnect | Close the Client, restart via launcher, load any region: host `STATE_SNAPSHOT sent (remote connected …)`, client gets the current time and the host's region immediately; host log `STATE_COMMIT … P2 in none` on disconnect | NOT TESTED |
| 14 | Logs / side effects | No `ERROR`/exceptions; `WORLD_STATS` every 30 s with `gaps=0`, `rejected=0`; no duplicate avatars; host money/story unchanged by the client's presence | NOT TESTED |
| 15 | Save protection regression | Client: `REDIRECT patched 13/13`, `VERIFY … inside sandbox`, `SAVE_GUARD_ACTIVE`; the client's travel saves go to the sandbox only; real save folder changed only by the host | NOT TESTED |

Known/expected in M3: the client's own sleep (bed) does not advance shared time — the client clock returns to the host's time afterwards (`TIME snap … (hour tick suppressed)`). Haunted Mansion locks time (mode 0): the client keeps its own clock there.
Send back: both `plugin.log`, screenshots for 8 and 11.

M3 outcome (user, 2026-11-10): networking and global time synchronization worked in-game; the design was changed to per-region time (M3.1). §6 rows not reported individually keep their state.

## 7. M3.1 manual test checklist (per-region time, client sleep)
Setup: both instances need plugin **0.3.1** (older builds are refused with `ProtocolMismatch`). Throwaway profile. Travel through the car's town selection (Madison Street = `MadisonSt`, Hillville = `Hillville`). Sleep = the hideout bed (or any bed the game offers). Compare times with the overlay line 2 (`time <local> region <HH:MM> in sync` / `(region authority)` / `(region owner, host validates)`) and the in-game phone clock/sky.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Launch Host, then Client | Both `WORLD_INIT … api=ok sleepHooks=ok clockRefresh=10s` | NOT TESTED |
| 2 | Both load into the hideout | Host `REGION_CLOCK Hideout initialized …`; client `TIME region clock Hideout …`, possibly `TIME adopt …` once | NOT TESTED |
| 3 | Shared hideout time | Same clock on both (± 1 game minute); client overlay `in sync` | NOT TESTED |
| 4 | Host → Madison Street | Host `REGION_CLOCK MadisonSt initialized from P1 previous region Hideout` (first visit); client unaffected | NOT TESTED |
| 5 | Client → Hillville | Host log `REGION_CLOCK Hideout frozen; Hillville initialized from P2 previous region Hideout`; client overlay `(region owner, host validates)` | NOT TESTED |
| 6 | Independent clocks | Both clocks keep running; note both times | NOT TESTED |
| 7 | Client sleeps (bed) in Hillville… | Client `SLEEP start` / `SLEEP end … (+N h)` / `SLEEP request sent` / `SLEEP ack`; host `SLEEP accepted … Hillville -> HH:MM` | NOT TESTED |
| 8 | …Host's Madison clock | Unchanged by the client's sleep (still running normally) | NOT TESTED |
| 9 | Host travels to Hillville | Host `TIME adopt Hillville clock A -> B (hour tick suppressed)` | NOT TESTED |
| 10 | Host adopted the client's time | Both show Hillville's time (the post-sleep time + elapsed) | NOT TESTED |
| 11 | Host sleeps while both are in Hillville | Host sleeps normally; client sky/time fast-forwards along (`TIME snap … (hour tick suppressed)` lines allowed) | NOT TESTED |
| 12 | Both clocks advanced | Same time on both after the sleep | NOT TESTED |
| 12b | (optional) Client sleeps while both are in the same region | Host `SLEEP accepted … (host in region: catching up hour by hour)` then `TIME_STEP done … hourly ticks=N` (N ≈ hours slept); no `FALLBACK` | NOT TESTED |
| 13 | Both leave Hillville (e.g. both to the hideout); wait a few minutes; one returns | Host `REGION_CLOCK Hillville frozen` when the last player left, later `Hillville resumed at …` | NOT TESTED |
| 14 | Preserved time | Returning player adopts Hillville's frozen time (not their previous region's time) | NOT TESTED |
| 15 | Restart the Client, load any region | Host `STATE_SNAPSHOT sent`; client adopts the correct time of the region it loads into | NOT TESTED |
| 16 | Remote player visibility | As M2: visible on foot in the same region, hidden in vehicles/other regions, animations unchanged | NOT TESTED |
| 17 | Logs | No `ERROR`/exceptions; `WORLD_STATS` shows `rejected=0` (or explained), `gaps=0`, `stepFallbacks=0`; on the **host**: rent/day/bank do not jump when it adopts a region time (only legitimate sleep/elapsed hours); note `update avg/max us` and `bytes out/in` | NOT TESTED |

Known/expected: shared calendar, rent and bank interest advance only from the host's clock (see PROGRESS "Known limitations"). Send back both `plugin.log` files and the times you noted in rows 6, 10, 12, 14.

## 59. W6C.1 checklist — death / arrest without world rollback (release `TS2-Multiplayer-0.31.0.zip` on BOTH PCs, protocol 41)
Grep tags: `CAUGHT P`, `DEATH P`, `DEATH_SCREEN`, `RESPAWN begin`, `RESPAWN complete`, `RESPAWN failure`, `JAIL begin`, `JAIL complete`, `CHECKPOINT_LOAD`, `STORY host scene load`, `STORY host story back at`, `DEATH_STATS`.
Each `before:` / `after:` line carries `story <committed>`, `worldRev`, `heavy …` and `cargo …` identities.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | story15: **P2** takes the box at House 104 and carries it. **P1** gets caught by police | P1 caught screen: button text "Continue" (game text) instead of "Load checkpoint", "Go to jail", "Quit to menu". P2 keeps playing (moves, carries). P1 log `CAUGHT P1 GotCaught(0) …`, `DEATH_SCREEN P1 shown: options Respawn …` | NOT TESTED |
| 2 | P1 clicks "Continue" (Respawn) | Original loading screen, P1 appears in the hideout with full control (move, look, interact, inventory). P1 log `DEATH_SCREEN P1 choice Respawn`, `GameManager.LoadCheckpoint NOT called`, `RESPAWN begin`, then `RESPAWN complete … region Hideout`; the story in `before:` and `after:` is the same; P2 log has NO `MIRROR … new host history` with fewer keys and NO `STORY followed host story 18` | NOT TESTED |
| 3 | After row 2: P1 travels to Madison | House 104 box NOT there on P1; P2 still holds it where it is; one minimap marker, on P2 | NOT TESTED |
| 4 | Box into the Ranger, drive to the hideout, unpack (either player) | Unpacked once; exactly ONE package (item 91), claimed once; never a second box or `CARGO #2` | NOT TESTED |
| 5 | Repeat rows 1-4 with roles swapped: **P2** caught, P1 continues | P2: same caught screen + respawn in the hideout; P1 unaffected; P2 log `CAUGHT P2 …`, `RESPAWN complete` | NOT TESTED |
| 6 | Caught by police, choose "Go to jail" (P1, then later P2) | Original jail scene + bail exit to the hideout; story / box / cargo / money unchanged except the original jail effects (crowbar 0, alerts cleared, bail cost). Log `JAIL begin` / `JAIL complete … region Jail`; the other player continues | NOT TESTED |
| 7 | Die from bleeding (shot) or gas | Caught screen with "Continue" (no jail button); respawn as row 2 | NOT TESTED |
| 8 | During P1's caught screen: P2 picks up / drops world items, loads cargo, answers a story call | All committed by the host while P1 waits (P2 logs `-> Accepted`); P1 sees the results after respawning | NOT TESTED |
| 9 | Both caught at about the same time; both respawn (or one jail) | Independent screens, one transition each; no duplicates | NOT TESTED |
| 10 | Double-click "Continue" / click "Continue" then "Go to jail" quickly | One transition only (`repeated choice ignored`) | NOT TESTED |
| 11 | Host: pause menu "Load" (save slot) and "Load save?" -> Yes during the session | Nothing happens (menu stays), log `CHECKPOINT_LOAD … refused during the multiplayer session` | NOT TESTED |
| 12 | Disconnect P2 while P1 is on the caught screen; respawn; P2 reconnects | Respawn works offline; after reconnect P2 sees the same story / box / cargo | NOT TESTED |
| 13 | Travel races: host travels right after a story advance (e.g. box into the car) | No `STORY shared story` going down on P2; host log may show `STORY host scene load: local story … held` then `back at …` | NOT TESTED |
| 14 | End the session (F8), single-player: get caught | Original "Load checkpoint" text and behaviour (reload), no `DEATH_SCREEN` log | NOT TESTED |
| 15 | Regression: W6B.4 calls + markers, drone, cameras, vehicles, wallet, inventories, save isolation | Unchanged | NOT TESTED |

Performance to note: `DEATH_STATS`, `STORY_STATS heldLoads`; FPS before/after with both PCs in Madison (no new per-frame work expected).

## 58. W6B.4 checklist — shared story calls + mission minimap markers (release `TS2-Multiplayer-0.30.0.zip` on BOTH PCs, protocol 41)
Grep tags: `STORY call ringing here`, `STORY shared call`, `answered the call`, `finished the call`, `conversation over here`, `ring skipped`, `original decline branch`, `original answer branch`, `original bypass branch`, `STORY_STATS` (`calls answered/mirrored/bypassed/silenced/finished`), `BLIP '`, `BLIP_STATS`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | F10 story15 (or any stage with a call): both PCs on foot when it rings; **P1** answers | P2 stops ringing at once and hears the same dialogue + subtitles; P1 log `STORY host answered the call 'story15' … -> Accepted`; P2 log `STORY shared call …`, `ringing here -> original answer branch`. Later both logs `conversation over here`, one `finished the call … -> Accepted` | NOT TESTED |
| 2 | Next call: **P2** answers | Same as row 1 with roles swapped (P2 `proposed #N`, `-> Accepted`) | NOT TESTED |
| 3 | Both press accept at the same moment | One dialogue per PC (not two overlapping); one log `-> Accepted`, the other `-> Unchanged` or `answered here too` | NOT TESTED |
| 4 | P2 sits in a car while P1 answers; P2 leaves the car during / after the call | P2 never rings; when on foot it gets the dialogue once (`ring pending here -> original bypass branch`) | NOT TESTED |
| 5 | After a finished call: P2 disconnects and rejoins (or reloads) at the same stage | No ring / no dialogue replay on P2 (`ring skipped` or `original decline branch (no replay)`) | NOT TESTED |
| 6 | Decline (cancel) on one PC while the call rings | Only that PC stops ringing; the other can still answer (decline is personal) | NOT TESTED |
| 7 | story15 box marker: P1 picks the box in House 104 | Both minimaps: ONE story marker, on P1 (P2 log `BLIP 'CardboardBox_FlybyLiteDrone' … follow …`); no marker left in the house | NOT TESTED |
| 8 | P1 drops it, P2 picks it up | One marker, now on P2, on both PCs | NOT TESTED |
| 9 | Box into the Ranger cargo, drive to the hideout | One marker on the car on both PCs, moving with it; nothing left at 104 (logs `BLIP … hidden (removed: in a car …)`) | NOT TESTED |
| 10 | Unload at the hideout, carry (either player), place on the table | Marker follows the carrier, then on the table; no marker at the parked car | NOT TESTED |
| 11 | Open the box (either player) | Box marker gone on both; only the package's original marker (if any) at the table; pick it up -> no marker left | NOT TESTED |
| 12 | Travel Madison <-> hideout, P2 rejoin, bed-save + reload during rows 9-11 | Never two markers, never a marker at an old place | NOT TESTED |
| 13 | Regression: drone + camera story14, cardboard unpack once, package claim once, story16-18 tasks + one $250 payout each, heavy cargo, P2 inventory, save isolation, F8 join, F10 developer session, payphone | Unchanged (§55–57) | NOT TESTED |

Performance to note: `STORY_STATS` / `HEAVY_STATS` lines (no per-frame BLIP lines); FPS before/after with both PCs in the hideout.

## 57. W6B.3 checklist — shared box contents, story16-18 by either player, checkpoint (release `TS2-Multiplayer-0.29.0.zip` on BOTH PCs)
Grep tags: `HEAVY mission-box contents`, `HEAVY unpack`, `WITEM`, `STORY task`, `STORY Player-2 mission`, `STORY host completed`, `STORY shared missions`, `STORY_STATS` (`missions …`), `WALLET`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | story15/16: box on the hideout table, P1 opens it | Both PCs: box gone, ONE package on the table at the same spot. P1 log `HEAVY mission-box contents '…' item 91 (original SpawnItem) shared as world item #N`; P2 shows it (no `contents not created` duplicate) | NOT TESTED |
| 2 | Fresh: P2 opens it (P1 in the hideout) | Same as row 1 (P2 gets fade + cut sound). P1 log `opened by Player 2` + `shared as world item` | NOT TESTED |
| 3 | After row 1 or 2: P2 picks the package | P2 inventory +1 black package; it vanishes on P1. P1 log `taken by Player 2: host original object removed`, `ItemCardboard = -1` | NOT TESTED |
| 4 | Fresh: P1 picks it instead | P1 inventory +1; vanishes on P2; P2 cannot pick anything there. Also check: after P2 opened, does P1 ever still see the closed box? (report the host log around `opened by Player 2`) | NOT TESTED |
| 5 | Both press interact on the package at once | One inventory gets it; the other's pickup does nothing (`WITEM … -> Stale`) | NOT TESTED |
| 6 | Package on the table, P1 bed-saves, both quit, reload, P2 rejoins | One package on both PCs (P1 log `original SpawnItemOnStart (ItemCardboard 91) shared`) | NOT TESTED |
| 7 | Package on the table, both travel to Madison and back | Package still there once on both (host log may show `host check: … not in the registry` re-share) | NOT TESTED |
| 8 | story16: P2 (holding the package) sells it on Blackbay | Both HUDs story17 once; shared money +250 once. P2 log `STORY Player-2 story 20 'story16' via BlackbaySale … proposed`, `-> Accepted` | NOT TESTED |
| 9 | story17: P2 plants the package at 110 | P2: original mission-complete UI; both HUDs story18 once; money +250 once. P2 log `STORY Player-2 mission 110[0] Plant … proposed`, `committed by the host`; P1 log `STORY host completed 110[0]`. P1 at 110 sees the planted object | NOT TESTED |
| 10 | Fresh story17: P1 plants instead | Original on P1; P2 log `STORY task 110[0] … key 1 -> 2 (host campaign), completion shown`; P2's marker gone, P2 cannot plant again | NOT TESTED |
| 11 | story18: P2 photographs the 105 kitchen (photo mode, aim at the kitchen target) | Both story19 once; +250 once; P2 log `105[0] Photo … proposed`, host `completed 105[0]` | NOT TESTED |
| 12 | Fresh story18: P1 photographs instead | P2 follows (`key 1 -> 2`), no second payout | NOT TESTED |
| 13 | Invalid: P2 photographs another kitchen / 110's photo target at story18; P2 plants while P1 already did | Nothing happens on either PC; P2 log `refused before the host` or `-> Stale`, plant objects restored | NOT TESTED |
| 14 | After row 9: P1 gets caught or loads the last save (checkpoint) | Both back to the saved story (e.g. 21) and money; P2 log `STORY task 110[0] key 2 -> 1 … host checkpoint restored the task`; the task can be done once more | NOT TESTED |
| 15 | Regression: drone flights, 104 camera + story14 by P2, cargo, heavy sale, F8 join, F10 scenarios, story calls | Unchanged (§55, §56) | NOT TESTED |

## 56. W6B.2 checklist — mission box unpacking (release `TS2-Multiplayer-0.28.0.zip` on BOTH PCs, story15 or later)
Grep tags: `HEAVY unpack`, `HEAVY_STATS` (`unpack host / req / granted / hostRuns / refused / spawnBlocked`), `CARGO`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Bring the 104 box home (P2 carries, Ranger cargo, travel), P2 puts it on the hideout table; P1 opens it (knife) | P1: fade, sound, package on the table. P2: box disappears, no package. P1 log `HEAVY unpack of '…' by Player 1 (Unpack Hideout:cargo 805A0001 …): box record consumed once` | NOT TESTED |
| 2 | After row 1: P2 aims at the table | Nothing to open; no second package anywhere | NOT TESTED |
| 3 | Fresh run of row 1 but P2 opens it (P1 in the hideout) | P2: fade + sound, box disappears; P1: box disappears, package on P1's table. P2 log `requested (proposal N)` then `granted`; P1 log `opened by Player 2: host original SpawnItem created the contents once` | NOT TESTED |
| 4 | P2 opens while P1 is NOT in the hideout | Nothing happens; P2 log `-> Busy`, P1 log `refused: host is not in …`; box still there; repeat with P1 present -> row 3 | NOT TESTED |
| 5 | Both press the knife at the same moment | Exactly one package (on P1's table); the second open logs Stale | NOT TESTED |
| 6 | P1 saves (bed) with the package still on the table, both quit, reload, P2 rejoins | Package on P1's table only; P2 log `contents not created here` once; no box | NOT TESTED |
| 7 | P1 picks the package | Normal pickup, story continues (story16 send via Blackbay by P1) | NOT TESTED |
| 8 | Regression: heavy sale at the junkyard, other heavy cargo, parcel lockers, drone camera story14 | Unchanged | NOT TESTED |

## 55. W6B checklist — drone flight, House 104 camera, story14 by Player 2, story call (release `TS2-Multiplayer-0.27.1.zip` on BOTH PCs, F10 developer menu, index 18)
Grep tags: `DRONE`, `WOBJ camera`, `STORY`, `WOBJ_STATS`, `SYNC_STATS`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | P1 launches its drone near P2 and flies around | P2 sees P1's body STANDING at the launch spot and a separate drone flying smoothly. P1 log `DRONE local flight started`; P2 log `DRONE remote flight shown (item 89)` | NOT TESTED |
| 2 | P1 lands (drone key) | Drone disappears on P2, body unchanged; P2 log `DRONE remote flight hidden` | NOT TESTED |
| 3 | Rows 1-2 with roles swapped (P2 flies) | Same on P1 | NOT TESTED |
| 4 | P2 flies, P2 quits to the main menu mid-flight | P1: drone and avatar vanish, no leftover drone | NOT TESTED |
| 5 | Fresh index-18 session. P2 disables the 104 entrance camera (Junkyard) with its drone | Both HUDs switch to story15 once; host log `WOBJ camera … disabled from the shared state … host story step checked by the original`, `STORY host published story 19 'story15'`; P2 log `disable proposed #`, `accepted: original DisablePermamently runs` | NOT TESTED |
| 6 | After row 5: P1 launches its drone and flies to the camera | Camera stays dark / disabled on P1 (no light, no detection) | NOT TESTED |
| 7 | Fresh session: P1 disables the camera instead | story15 once on both; P2's camera dark too | NOT TESTED |
| 8 | Both leave Madison (travel to hideout) and come back | Camera active again (original reload), story stays story15 | NOT TESTED |
| 9 | Story call (story15 call after row 5/7): P2 answers | P1 stops ringing at once and hears the same dialogue; log `STORY … answered by P2`, P1 `ringing here -> original answer branch` | NOT TESTED |
| 10 | Next call: P1 answers (or P2 busy e.g. in a car while P1 answers) | P2 stops ringing / gets the dialogue without ringing (`bypass branch`) | NOT TESTED |
| 11 | Regression: F8 normal host + join, doors, loot, cars | Unchanged | NOT TESTED |
| 12 | (0.27.1) P2 flies, then quits to the main menu mid-flight; rejoins | Host log `P2INV report committed … items 1` during the flight (no `items 0`), P2 still owns the drone after rejoin | NOT TESTED |

## 54. W6A.5 checklist — story-test join deadlock, readiness state, drones (release `TS2-Multiplayer-0.26.5.zip` on BOTH PCs, developer menu enabled on both)
Grep tags: `HOST_READINESS`, `TEST_STORY`, `TEST_SCENARIO`, `JOIN_DEFERRED`, `JOIN_REFUSED`, `REJECT`, `P2INV`, `STORY`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | P2 F10 *Testkampagne beitreten* FIRST, then P1 F10 → Index 18 → *Testkampagne starten* | P2 waits (`HostNotReady`, F10 "Host ist nicht bereit"), no failure. P1 log: `HOST_READINESS not ready: …` while loading / checking, NEVER "may join" before `TEST_STORY scenario check OK`; then `HOST_READINESS ready: Player 2 may join` + `TEST_CAMPAIGN HOST READY` | NOT TESTED |
| 2 | Wait (no clicks on P2) | P2 joins by itself within ~5 s of HOST READY; spawns in Madison beside P1 | NOT TESTED |
| 3 | P1 log after OK | `TEST_STORY story actions (diagnostic, not a join condition): camera_104_drone (Ziel): verfügbar; note_103: im Originalspiel 1.27c nicht verfügbar …` | NOT TESTED |
| 4 | P1 F10 in the world | "HOST BEREIT …", "Story: Index 18 · story14 … geprüft OK", "Aktionen: …" as in row 3 | NOT TESTED |
| 5 | Both: own drone | P1 drone 89 equipped; P2 log `TEST_SCENARIO Player 2 check OK: own drone 89 equipped`; host log one `P2INV story-test grant … item 89` | NOT TESTED |
| 6 | P1 flies its drone to the 104 entrance camera (Junkyard) and disables it | Story advances once to story15 on BOTH HUDs (`STORY host published story 19 'story15'`) | NOT TESTED |
| 7 | P2 leaves to the main menu and rejoins via F10 | Joins again; NO second `P2INV story-test grant`; P2 still exactly one drone | NOT TESTED |
| 8 | House 103 (optional): walk in | The note is not there (original 1.27c asset); nothing breaks | NOT TESTED |
| 9 | Normal F8 host + code join (fresh start) | Join before load waits, then joins; chip "BEREIT – SPIELER 2 KANN BEITRETEN" only when the join works; no developer grant in any log | NOT TESTED |

Not reproducible on demand: the permanent-failure path (`TEST_STORY scenario check FAILED`, P1 F10 "BEITRITT GESPERRT – …", P2 "Story-Test des Hosts ist ungültig … kein weiterer Versuch", host log `JOIN_REFUSED … permanent`). Report it if it ever appears. Send back both `LogOutput.log` files.

## 53. W6A.4 checklist — story14 prerequisites, Player-2 spawn, scenario check (release `TS2-Multiplayer-0.26.4.zip` on BOTH PCs, developer menu enabled on both)
| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Host F10 → Index 18 → *Testkampagne starten* | Log `TEST_SCENARIO Index 18 · story14 …: prerequisites flyby_drone, drone_camera_module`, `TEST_STORY seed: … Item_89 = 1, Drone = 89, flyby_upgrade3 = 1`, then `TEST_STORY scenario check pending …` (may appear) and `TEST_STORY scenario check OK … drone 89 …`, then `TEST_CAMPAIGN HOST READY` — never READY before OK | NOT TESTED |
| 2 | Host: inventory / drone key | The Flyby drone is in the inventory and launches with the original drone key in Madison; the camera disable works on the 104 entrance camera | NOT TESTED |
| 3 | Client F10 join (before and after host ready) | Before OK: waits (`HostNotReady`, reason "Story-Test …"); after: P2 spawns in Madison beside P1 (log `P2SPAWN RestoreNear (FirstJoin) in MadisonSt`, host `spawn … story test: first join beside the host`), not in the hideout or inside a car | NOT TESTED |
| 4 | Client: own drone | `P2INV story-test grant … item 89` on the host; P2 log `TEST_SCENARIO Player 2 check OK: own drone 89 equipped`; P2 launches its own drone independently of P1 | NOT TESTED |
| 5 | Story14 completion via note_103 (§49 row 2) or the drone camera (P1) | story15 on both. W6A.5: note_103 does not exist in the original 1.27c asset, only the drone camera path is testable (§54 row 6) | NOT TESTED |
| 6 | P2 leaves to the main menu and rejoins | Spawns at its own last position (Restore), still exactly one drone | NOT TESTED |
| 7 | Restart both, start index 18 again | Fresh seed, both players get exactly one drone again | NOT TESTED |
| 8 | Host F10 in the world | Status shows "geprüft OK (Region, Voraussetzungen, Story-Objekte, Spawn)" | NOT TESTED |

## 52. W6A.3 checklist — isolated story-test startup (release `TS2-Multiplayer-0.26.3.zip` on BOTH PCs, `[Developer] EnableDeveloperMenu = true` on both)
Note the genuine save's file timestamps first (`%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2`). Grep tags: `TEST_CAMPAIGN`, `TEST_STORY`, `HOST_READINESS`, `STORY`, `JOIN`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Host (genuine save in free mode is fine): F10 → keep "Index 18 · story14 · Disable the 104 entrance camera…" → *Testkampagne starten* | No click in the original menu; the world loads by itself in Madison Street. Log: `TEST_CAMPAIGN prepared: story seed Index 18 …`, `TEST_STORY seed: original MainMenu.StartNewGame …`, `TEST_STORY world check OK … note_103 …`, `TEST_CAMPAIGN HOST READY`. HUD objective: "Disable the 104 entrance camera with your drone" | NOT TESTED |
| 2 | Host in the world: press F10 | Read-only status box: "HOST BEREIT …", "Story: Index 18 · story14 … geprüft OK"; F10 again hides it | NOT TESTED |
| 3 | Client: F10 → host address + port → *Testkampagne beitreten* (also try it BEFORE the host is ready) | Joins without a code; before readiness it waits (log `HostNotReady`), then loads the host world on story14 | NOT TESTED |
| 4 | Either player reads the note in House 103 (§49 row 2) | Both HUDs switch to story15 ("Steal the cardboard box from 104 …") once | NOT TESTED |
| 5 | Unsupported index (e.g. 20) → *Testkampagne starten* | F10 shows "!! TESTKAMPAGNE NICHT GESTARTET – …"; nothing loads; F8 / original menu unchanged | NOT TESTED |
| 6 | Restart, start index 18 again | Fresh seed (`story.old-<time>` folder appears), same result as row 1 | NOT TESTED |
| 7 | After all rows: compare the genuine save timestamps | Unchanged | NOT TESTED |
| 8 | Normal F8 host / join with code (fresh game start) | Unchanged behaviour | NOT TESTED |

## 51. W6A.2 checklist — menu pointer, developer join without code, story list (release `TS2-Multiplayer-0.26.2.zip` on BOTH PCs, protocol 37)
| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Main menu, F8: move the mouse over tabs, buttons, fields; click a field and type; close and reopen | A light arrow pointer with a dark outline is visible and follows the mouse; hover highlights exactly under its tip; no second pointer, not the game's cursor | NOT TESTED |
| 2 | Repeat row 1 at 1280x720, 1920x1080 and 2560x1440 (or the highest available) | Pointer tip and hit positions match at every resolution; pointer size grows with the resolution | NOT TESTED |
| 3 | Close F8 (key, *Schließen*), move the mouse | The game's own menu cursor is back, continues near the last pointer position, the original menu buttons react again; no frozen extra cursor | NOT TESTED |
| 4 | F10 (developer menu enabled): pointer, scroll the story list with the wheel, select rows, click buttons | Same pointer; the list shows one selectable row per step (no checkboxes); the selected row is highlighted | NOT TESTED |
| 5 | P1 F10 *Test-Host vorbereiten* (any supported step), *Fortsetzen*; P2 F10 enters only address + port, *Testkampagne beitreten* | No code anywhere in F10; P2 joins the test campaign (log `JOIN started from the developer menu`) | NOT TESTED |
| 6 | P1 hosts normally with F8; P2 tries F10 *Testkampagne beitreten* to it | Refused: "Unter dieser Adresse läuft kein Entwickler-Test-Host …" (log `AuthFailed`); P1's F8 session unaffected; a normal F8 join with the code still works | NOT TESTED |
| 7 | During row 5: P2 disconnects (network off / *Verbindung trennen*) and rejoins via F10 | Rejoin works without a code (restart P2's game if it asks for it) | NOT TESTED |
| 8 | Start *Fortsetzen* with F8 open (host prepared) | World loads; in the world the game's cursor behaviour is unchanged | NOT TESTED |

## 50. W6A.1 checklist — F8 public menu + F10 developer menu (release `TS2-Multiplayer-0.26.1.zip` on BOTH PCs, protocol 37)
Both PCs in the original main menu. Developer rows need `BepInEx\config\com.ts2mp.multiplayer.cfg` → `[Developer] EnableDeveloperMenu = true` on BOTH PCs (restart the game after editing). Grep tags: `MENU`, `HOST`, `JOIN`, `TEST_CAMPAIGN`, `TEST_STORY`, `TEST_RESET`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Default config: press F8, F10 | F8 opens the new panel (dark, amber accent, game font); F10 does nothing. Log `MENU ready ... developer menu off`, `MENU look: game font ...` | NOT TESTED |
| 2 | F8 panel: hover buttons, switch tabs, type in fields, click outside the panel / on the game's menu behind it | Hover states, tabs switch; the original main menu does not react to clicks while the panel is open; it works again after closing (F8 / *Schließen*) | NOT TESTED |
| 3 | Normal session: P1 *Spiel hosten → Sitzung vorbereiten*, *Fortsetzen*; P2 *Spiel beitreten* with address / port / code | Same behaviour as 0.26.0 (join loading screen, P2 loads the host world, W6A / W2B work). Hosting view shows code, *Kopieren*, status chip | NOT TESTED |
| 4 | Errors: wrong code, wrong port, cancel during join, host cancel | Error banner in the panel with the original messages; main menu usable afterwards | NOT TESTED |
| 5 | Enable the developer menu on both PCs. F10, then F8, then F8 again; while typing in an F10 field press F8 | Only one window at a time; F8 while typing in F10 is ignored; same key closes | NOT TESTED |
| 6 | P1 F10: select step 18 (story14), *Test-Host vorbereiten mit Schritt 18*, *Fortsetzen* | Log `TEST_CAMPAIGN prepared`, `TEST_STORY isolated copy: Story n -> 18 'story14'`; HUD shows "Disable the 104 entrance camera ..."; real save untouched (check its timestamp) | NOT TESTED |
| 7 | P2 F10 → *Testkampagne beitreten* | P2 joins, test sandbox (`JOIN ... (multiplayer test campaign)`), same objective as P1; W6A note_103 test (§49 row 2) works | NOT TESTED |
| 8 | P2 tries F8 *Spiel beitreten* to the test host (restart P2 first) | Refused: "Der Host spielt eine Entwickler-Testkampagne ..."; P2 loads nothing | NOT TESTED |
| 9 | Normal host (no test) + P2 F10 *Testkampagne beitreten* | Refused: "Der Host spielt keine Testkampagne ..." | NOT TESTED |
| 10 | F10 select an unsupported step (e.g. 20, 35) | Marked X with the reason and the step to select instead; *Test-Host vorbereiten* refuses, main menu stays normal (copy released) | NOT TESTED |
| 11 | After a test run: restart, F10 *Testszenario zurücksetzen*, then Dev Host again | Log `TEST_RESET ... moved aside`; new copy of the real save; `testcampaign.reset-<time>` folder exists | NOT TESTED |
| 12 | In a loaded world press F8 / F10 | Nothing opens (log `MENU ... ignored outside the main menu`) | NOT TESTED |

Send back both `LogOutput.log` files.

 — shared story progression (release `TS2-Multiplayer-0.26.0.zip` on BOTH PCs, protocol 37)
F8 test campaign at story step "Disable the 104 entrance camera with your drone" (stage 18 `story14`; reached after the RobTips step). Both players in Madison Street. Back up the host save first. Grep tag: `STORY`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Logs after loading on both PCs | `STORY shared story ready` (host / client); host `STORY host published story 18 'story14'`; client `STORY shared story 18 'story14'`. Same HUD objective on both PCs | NOT TESTED |
| 2 | P2 enters House 103 and reads the note (`story object 103 notka`) | P2: original note text; parcel code on both phones (W2B). Story: BOTH HUDs switch to "Steal the cardboard box from 104 ..." (story15); the original story15 phone call plays on both PCs once. P2 log `STORY Player-2 story 18 'story14' via ReadNote ... proposed #n`, `-> Accepted`, `STORY followed host story 19 'story15'`; host log `STORY host AdvanceStory for Player 2: 18 -> 19` | NOT TESTED |
| 3 | P2 reads the note again (if still shown) / P1 reads it | Nothing more happens to the story (no second call, no skipped objective) | NOT TESTED |
| 4 | Repeat from a backup at story14, roles swapped: P1 reads the note | Original step on the host; P2's HUD follows to story15 (`STORY followed host`) | NOT TESTED |
| 5 | Repeat from the backup: both read the note at the same moment | One step (story15), one call per PC; one P2 log `-> Stale` allowed | NOT TESTED |
| 6 | Parcel first: from the backup, open Madison locker 1 with 16161 (code known) and take the parcel BEFORE anyone reads the note; then leave Madison and return; read the note | The note is still there (`STORY note ... kept visible`) and advances the story once | NOT TESTED |
| 7 | After row 2: host checkpoint (travel to the hideout), host reload of the save, P2 disconnect + rejoin, P2 travel | Both PCs stay on story15 after every step; no repeated calls | NOT TESTED |
| 8 | Host quits WITHOUT saving after row 2 and reloads | Original rule: back to story14 on both PCs; the note works again | NOT TESTED |
| 9 | P2 does something that would advance the story in single player but is not supported (e.g. disables the 104 camera with the drone) | P2's objective does not change on its own; P2 log `STORY Player-2 story step ... not a listed shared trigger` (once). P1 doing it: original step on both | NOT TESTED |
| 10 | Regression: W2B parcel codes / lockers / parcel loot, doors, loot, wallet, sleep | Unchanged | NOT TESTED |

Note: §48 row 4 (P2's read leaves the story unchanged) is superseded by W6A (row 2 here). Send back both `LogOutput.log` files (lines `STORY`, `PERF STORY_STATS`, `PARCEL`).

## 48. W2B checklist — shared parcel codes, story clues, parcel lockers (release `TS2-Multiplayer-0.25.0.zip` on BOTH PCs, protocol 36)
F8 test campaign (fresh enough that the codes below are not yet known), both players in Madison Street. Phone = the original phone's parcel-code page.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Logs after loading on both PCs | `PARCEL shared parcel codes ready`; `WOBJ hooks: ... ParcelCode.OpenLocker (W2B)`; host `PARCEL host codes published: 0 code(s)` (or the campaign's known codes) | NOT TESTED |
| 2 | P2 reads the note in House 103 (`note_103`, first story code note) | P2: original note + "new code" animation; host: "new code" animation too, the code is on BOTH phones. P2 log `PARCEL Player-2 Note 'parcel1_madison' ... proposed`, `-> Accepted`, `story note ... stays with the host`; host log `P2 discovered code 'parcel1_madison': applied to the host campaign` | NOT TESTED |
| 3 | P1 enters 16161 at the Madison parcel station | Locker 1 opens on BOTH PCs (animation + sound on both). P2 can take the parcel; it is gone for P1 | NOT TESTED |
| 4 | Story: after row 2, check the host's current story objective | Unchanged by P2's read. P1 then reads the same note: original story step happens once on the host | NOT TESTED |
| 5 | P1 reads another code note (e.g. House 106 / 110 / park bench) | P2 gets the code with the animation; P2 opens that locker with its code | NOT TESTED |
| 6 | Both players read the same new code note at the same moment | Code known on both, one animation per PC, no error | NOT TESTED |
| 7 | Hillville: P2 alone in Hillville reads `note_201`, P1 stays in Madison | Code on both phones; later P2 (or P1 after travel) opens Hillville locker 1 with 69999 | NOT TESTED |
| 8 | Persistence: host saves (checkpoint), both travel to the hideout and back; then P2 disconnects + rejoins; then host quits to menu and reloads the save | All discovered codes still usable on both PCs after each step | NOT TESTED |
| 9 | Parcel once: P1 and P2 try to take the same parcel item from an opened locker at the same time | Exactly one gets it (in their own inventory); selling it later adds money once to the shared balance | NOT TESTED |
| 10 | Locker reset: both leave Madison and return | Opened lockers closed again (original); taken parcels stay taken; codes stay known | NOT TESTED |
| 11 | Regression: loot, wallet, doors, breakables (W2A), heavy objects | Unchanged | NOT TESTED |

Send back both `LogOutput.log` files (lines `PARCEL`, `PARCEL_STATS`, `WOBJ ... parcel locker`).

## 47. W2A checklist — shared destructible world objects (release `TS2-Multiplayer-0.24.0.zip` on BOTH PCs, protocol 35) — **user-verified (reported 2026-11-17, as a whole; row 9 region-reload reset NOT TESTED)**
F8 test campaign, both players in Madison Street near House 102 (the wooden barrier: plywood sheet in front, three nailed planks behind), both with a crowbar. "Hit" = original crowbar swing at the object.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Logs after loading Madison on both PCs | `WORLD_CENSUS_KINDS MadisonSt ... Breakable=64`; `WORLD_CENSUS ... MadisonSt` digest `711D4CF3` / 2575 objects on both; `WOBJ hooks: DestroyObject.Destroy_Thing (W2A), ...`; `WOBJ MadisonSt ready ... broken objects 0` | NOT TESTED |
| 2 | P2 hits the plywood (stage 1) | Plywood breaks on BOTH PCs (fragments + sound on both); planks still intact and blocking. P2 log `WOBJ proposed #n Set Breakable MadisonSt:<hash> -> 1` then `breakable ... accepted: original destruction runs`; host log `WOBJ update: 1 object(s)` | NOT TESTED |
| 3 | P1 hits the planks (stage 2) | Planks break on BOTH PCs; the opening is passable for BOTH players (walk through on each PC). Host log `WOBJ host Set Breakable MadisonSt:<hash> -> 1 ... -> Accepted` | NOT TESTED |
| 4 | Reverse roles after a region reload (row 9): P1 stage 1, P2 stage 2 | Same as rows 2-3 with roles swapped | NOT TESTED |
| 5 | Both hit the same intact stage at the same moment | Breaks once; one set of fragments per PC; no error. One log `-> Unchanged` on the loser is fine | NOT TESTED |
| 6 | P2 hits a stage repeatedly / very fast | Breaks once; extra swings only make the crowbar sound (log `swing ignored: already waiting for the host` allowed) | NOT TESTED |
| 7 | NPC reaction: with a resident inside House 102, P2 breaks a stage | Only NPCs near P2 react on P2's PC (original alert); the host PC shows the break but does not replay an alert around P1 | NOT TESTED |
| 8 | Late join: P1 breaks stage 1 while P2 is in the hideout; P2 then travels to Madison | P2 arrives with stage 1 already broken (no break sound on arrival), stage 2 intact; P2 can then break stage 2 | NOT TESTED |
| 9 | Region reset: both players leave Madison (e.g. both to the hideout), wait 5 s, return | Both stages intact again on both PCs (original reload). Host stays while P2 leaves and returns: stages stay broken | NOT TESTED |
| 10 | Furniture / toilet: each player breaks one breakable chair / toilet / plate stack in a Madison house; one in Hillville | Broken on both PCs; no money / XP / item change | NOT TESTED |
| 11 | Mission object (House 101 AC, if available): P2 hits it | Nothing breaks (crowbar sound only); P2 log `is a story/mission object`. P1 hitting it: original behaviour, P2 sees it broken | NOT TESTED |
| 12 | Regression: doors, drawers, locks, windows, loot, heavy objects in Madison | Unchanged | NOT TESTED |

Send back both `LogOutput.log` files (lines `WOBJ`, `WOBJ_STATS ... breakables local/applied/waiting`).

## 46. H3 checklist — heavy loot sale integrity + Ranger rear hatch (release `TS2-Multiplayer-0.23.0.zip` on BOTH PCs, protocol 34)
F8 test campaign, both players in the hideout, Ranger in the hideout with at least two heavy objects in its cargo bed (e.g. two TVs + one other item; load them in Madison first). "Heavy shop" = the hideout's original heavy-item sale area + sale screen used in the 0.22.1 test. Grep tags: `HEAVY Player 1 sells`, `HEAVY Player-2 sale`, `HEAVY Player 2 sold`, `refused`, `Unsell`, `HEAVY_STATS ... sold / saleRefused / unsold`, `WALLET Player-2 money change`, `DOOR ... rear hatch`, `vdoor`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | P1 takes TV 1 out of the Ranger, puts it in the heavy shop area, opens the sale screen, sells TV 1 | Money +value once on BOTH PCs; TV 1 gone on BOTH PCs and does not come back (wait 5 s, walk around). Host log `HEAVY Player 1 sells 'TV...' ... consumed once`; no `Release ... (object destroyed)`; CARGO `spawned` does not grow | NOT TESTED |
| 2 | Right after row 1, P1 and P2 each try to sell TV 1 again (sale screen still open / reopened) | Nothing happens, no money. Logs `refused` / `object is no longer at the heavy shop` | NOT TESTED |
| 3 | P2 takes TV 2 out, puts it in the area, sells it | Money +value once on BOTH PCs within ~1 s; TV 2 gone on both. P2 log `HEAVY Player 2 sold ... original sale ran once`, host log `WALLET Player-2 money change ... +value` once | NOT TESTED |
| 4 | Concurrent: one object in the area, both players open the sale screen and click Sell at the same time | Exactly one sale, one money increase; the other click does nothing | NOT TESTED |
| 5 | P2 clicks Sell and immediately closes the sale screen (Esc) | Either sold once (money once) or not sold (object back, log `Unsell`); never money without the object leaving, never the object gone without money | NOT TESTED |
| 6 | Legit flows: sell a different heavy object of another type; load an unsold object back into the Ranger and out again; drop / carry / hand over in the hideout | Unchanged H1 / H2 behaviour; the other object sells once | NOT TESTED |
| 7 | Reload / reconnect: after rows 1-3, P2 disconnects and rejoins; then host saves, quits to menu and reloads | Sold objects stay gone, money unchanged by the reload / rejoin; unsold cargo still in the Ranger | NOT TESTED |
| 8 | Rear hatch: P1 opens the Ranger's small rear hatch (E), then closes it; then P2 does the same | Hatch opens / closes on BOTH PCs with the original animation and sound. Logs `DOOR Ranger: rear hatch 'Car04_Door_Back_LOD0' ... synchronized as door 2`, `vdoor` commits | NOT TESTED |
| 9 | Rear hatch slow open (hold E) by P2, release early once | Observer replays the slow swing, then finishes fast like the side doors | NOT TESTED |
| 10 | Hatch + doors: open the hatch, then both front doors, enter as driver, close; drive off | Side doors close on entry as before; the hatch stays as set; a moving car refuses the other player's hatch open (log `car is moving`) | NOT TESTED |
| 11 | Hatch + cargo: with the hatch open and closed, load and unload a heavy object | Cargo load / unload unchanged | NOT TESTED |
| 12 | Regression: ordinary (non-heavy) item sales by both players, Shadowhawk door, medkit, CarBox, travel | Unchanged | NOT TESTED |

Send back both `LogOutput.log` files.

## 45. S1 checklist — shared sleep control (release `TS2-Multiplayer-0.22.1.zip` on BOTH PCs, protocol 33) — **user-verified (reported 2026-11-17, as a whole; rows not reported individually)**
F8 test campaign, normal mode (not survival). Sleep = hideout bed or the car (driver, `Parking`). Grep tags: `SLEEP_CONTROL` (`installed`, `observer EscKey/StopIt/QuitIt refused`, `own sleep refused`), `SLEEP_SHOW` (`presenting`, `not presented yet (...)`, `holding`, `ended (...)`), `SLEEP_START`, `SLEEP accepted`, `SLEEP end`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Both in the hideout. P1 sleeps in the bed until a later hour | P2 sees the original clock / fast-forward UI from its own view; P2 log `SLEEP_SHOW ... presenting`; both end at the same hour | NOT TESTED |
| 2 | Row 1 with P2 sleeping (bed), P1 watching | P1 sees the original UI; P1 log `SLEEP_START accepted ... host shows the original sleep presentation`; no double advance (P1 day/rent tick once per hour) | NOT TESTED |
| 3 | During rows 1 / 2 the OBSERVER presses Esc several times and clicks the screen | Nothing happens; observer log `SLEEP_CONTROL observer EscKey refused` once; sleep continues on both | NOT TESTED |
| 4 | The SLEEPER presses Esc mid-sleep (P1 sleeper, then P2 sleeper) | Both presentations end at the same time; observer log `SLEEP_SHOW ended (... sleeper ...)`; the sleeper's picker / Esc back to the game as original; same clock on both | NOT TESTED |
| 5 | Sleeper presses Esc immediately after Start (P2 sleeper) | P1 presentation ends at once; P1 clock not advanced to the target; no 40 s `expired` warning | NOT TESTED |
| 6 | While P1 sleeps, P2 opens its own bed / car sleep picker and presses Start | Original warning "you can't sleep"; picker stays; Esc closes it; P2 log `SLEEP_CONTROL own sleep refused`; then P2 sees the running sleep (`presenting` after its picker closed) | NOT TESTED |
| 7 | Row 6 reversed (P2 sleeps, P1 tries) | Same | NOT TESTED |
| 8 | Different regions: P1 in the hideout, P2 in Madison; each sleeps | No presentation on the other PC; each region clock advances only in its own region | NOT TESTED |
| 9 | Observer busy: P2 in the pause menu / inventory when P1 starts sleeping, closes it after 2 s | P2 log `not presented yet (game paused / UI open), retrying`, then `presenting` while the sleep still runs | NOT TESTED |
| 10 | Observer driving: P2 drives a moving car when P1 sleeps; stops the car while the sleep runs | Driving never interrupted; presentation starts after the car stopped (`driving a moving car` then `presenting`), EVP input restored after the sleep | NOT TESTED |
| 11 | Car sleep: P1 sleeps in the car as driver, P2 passenger in the same car | P2 sees the original UI; after the end the seat / camera are as before | NOT TESTED |
| 12 | P2 (observer) disconnects during P1's sleep and reconnects while it still runs | Presentation stops at the disconnect; after the reconnect P2 adopts the region clock (presents the rest if still running) | NOT TESTED |
| 13 | P2 (sleeper) disconnects mid-sleep | P1 presentation stops (`transaction ended without completion`); P1 clock continues normally | NOT TESTED |
| 14 | Regression: solo sleep without a session (bed + car), M3.1 region clocks, hourly rent/day, travel after sleeping | Unchanged | NOT TESTED |

Send back both `LogOutput.log` files.

## 44. V1.4.1 checklist — first-arrival main spot + Player-2 medkit (release `TS2-Multiplayer-0.22.0.zip` on BOTH PCs, protocol 33) — **user-verified 2026-11-16 (Ranger rear hatch NOT TESTED)**
F8 test campaign with the garage, BOTH cars owned and the medkit NOT used yet (fresh test campaign copy). Grep tags: `VEHICLE_HIDEOUT_LAYOUT`, `MEDKIT` (`requested`, `accepted`, `refused`, `committed state`, `removed`), `MEDKIT_STATS`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Both cars away (Ranger at the Auto Shop, Shadowhawk in Madison), both players in the hideout | Two travel proxies, separated, same spots on both PCs; E on each opens the travel menu | PASS (user) |
| 2 | P2 drives the Shadowhawk home first | Shadowhawk at the main spot, Ranger proxy moves to the second spot, on both PCs | PASS (user) |
| 3 | Row 2 reversed: P1 drives the Ranger home first, Shadowhawk away | Ranger main, Shadowhawk proxy second, on both PCs | PASS (user) |
| 4 | After row 2, P1 brings the Ranger home | Ranger takes the second spot; the parked Shadowhawk does not move | PASS (user) |
| 5 | Drive a car off / past a proxy and park elsewhere | No proxy jump while driving; once parked the proxy goes to a free original spot on both PCs; the driven car never jumps | PASS (user) |
| 6 | P2 gets shot (bleeding), opens the right front door of a car in the same region, E on the medkit | Original heal (blood UI gone), sound, medkit disappears on BOTH PCs. P2 log `MEDKIT ... accepted`, host log `medkit car#.. by P2 -> Accepted` | PASS (user) |
| 7 | After row 6: other car's medkit, and reload / reconnect / host restart | No medkit in either car on either PC (single use per save) | PASS (user) |
| 8 | Fresh copy: P2 as passenger, first person, engine OFF, bleeding, look at the medkit, E | Heal, medkit gone on both PCs | PASS (user) |
| 9 | P2 passenger, engine running, right door closed, E on the medkit | Nothing happens (no exit, no heal) | PASS (user) |
| 10 | E on the medkit while NOT bleeding (foot and seat) | Original warning "not bleeding", medkit stays | PASS (user) |
| 11 | Medkit on a travel proxy (car elsewhere) | No prompt, E does nothing, nothing consumed | PASS (user) |
| 12 | P1 uses the medkit (original) | Medkit gone on both PCs; a later P2 attempt does nothing | PASS (user) |
| 13 | Regression: CarBox from the seat, passenger entry/exit, handoff, Auto Shop proxy, cargo + tailgate, fuel, W1 purchases | Unchanged | PASS (user), except the Ranger rear hatch (tailgate): NOT TESTED |

Send back both `LogOutput.log` files.

## 43. V1.4 checklist — hideout parking layout of both cars (release `TS2-Multiplayer-0.21.3.zip` on BOTH PCs, protocol 32)
F8 test campaign with the garage and BOTH cars owned. Grep tag: `VEHICLE_HIDEOUT_LAYOUT` (one line per car on scene bind / change: region + holder, presentation, root, renderers on/total, pos, yaw, spot, park, overlap; `corrected … -> MainSpot/SecondSpot/Pose (reason)` on every move), `VEHICLE_STATS … hideoutLayout passes/moves`.
Expected layout whenever no car has a parked hideout pose: **Ranger at the main spot (SpawnHideoutCar), Shadowhawk at the second spot (secondCarHideoutPos)** on both PCs; a car someone parked in the hideout stays where it was parked.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Reported sequence: P1 leaves the Ranger at the Auto Shop and returns to the hideout; P2 drives the Shadowhawk to Madison and back to the hideout | Both PCs: Ranger (travel proxy) and Shadowhawk visible, separated, at the same spots on both screens. P1 log: Shadowhawk `ParkedOther`, Ranger `AwayProxy`, `overlap no`, renderers n/n | NOT TESTED |
| 2 | Row 1 with reversed roles (P1 carries the Shadowhawk, P2 leaves the Ranger elsewhere) | Same result | NOT TESTED |
| 3 | Both arrive with their own car (P1 Ranger, P2 Shadowhawk), in either order | Ranger main, Shadowhawk second on both PCs; no car on top of another | NOT TESTED |
| 4 | P1 home first (both cars elsewhere → two proxies), then P2 arrives with a car | Proxies separated; when P2 arrives the proxy/observed cars end up at the same spots as on P2 | NOT TESTED |
| 5 | Travel interaction: E on the proxy | Original travel menu; no driving, no storage, no cargo on the proxy | NOT TESTED |
| 6 | Park a car anywhere in the hideout, then: renovation reload (W1.1), reconnect P2, host restart | The parked car comes back at its parked place on both PCs | NOT TESTED |
| 7 | Drive a car around the hideout and park it on the other car's proxy spot | The driven car never jumps; once parked the proxy moves to the free original spot (log `corrected … overlapped another car`) on both PCs | NOT TESTED |
| 7b | Sit in a car in the hideout (stopped) next to a proxy, then drive it to a new place in the hideout, park and get out | The car is never moved while seated nor after getting out; a proxy never stands inside the seated car; both PCs show the same spots | NOT TESTED |
| 8 | Handoff / passenger in the hideout (take over the other player's car, ride as passenger) | Car stays in place, no relocation, entry / seat work as before | NOT TESTED |
| 9 | Regression: Auto Shop proxy, Shadowhawk purchase, CarBox, fuel, Ranger cargo + tailgate, H1/H2, W1 purchases, joint travel | Unchanged | NOT TESTED |

Send back both `LogOutput.log` files.

## 42. W1.1 checklist — Player-2-initiated renovation reload on Player 1 (release `TS2-Multiplayer-0.21.1.zip` on BOTH PCs, protocol 32)
F8 test campaign copy with the garage, WITHOUT renovations, shared money >= 20,000. Grep tags on the PC that did NOT buy: `HIDEOUT_REBUILD` (`invoke` = SelectHideout called, `native loading screen presented` = LoadWait ran, `scene reloaded and built with ... renovation applied (scene: AudioUpgrade shown ...)` = the new scene contains it, `waits (...)` = deferred, `MISSING` / `did not start` = failure), `HIDEOUT_STATS ... rebuilds req/started/notStarted/applied/missing/waits`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Both in the hideout, P1 on foot. P2 buys **AudioUpgrade** on the hideout computer | P2: original loading screen, radio present. P1: within ~1 s the original loading screen, rebuilt hideout with the radio. P1 log: `invoke` → `native loading screen presented` → `scene reloaded ... renovation applied (scene: AudioUpgrade shown)`. One charge | NOT TESTED |
| 2 | Repeat row 1 for **AntiTheft**, **CatStuff**, **LoreBooks** and **house repairs** (one per reload) | Same for each; `HIDEOUT_STATS` on P1: `started` = `applied` = number of purchases, `missing 0`, `notStarted 0` | NOT TESTED |
| 3 | P1 sits in a car (or carries a heavy object / has the travel menu open / is seated as passenger) while P2 buys an addition | P1 not reloaded while busy; log `waits (in a car)` (once per reason). P1 gets out → loading screen within ~1 s **without any further action** → renovation visible | NOT TESTED |
| 4 | P1 in Madison while P2 buys an addition; then P1 travels home | P1 never interrupted, no `HIDEOUT_REBUILD` line while in Madison; normal travel to the hideout shows the renovation | NOT TESTED |
| 5 | Reverse direction: P1 buys an addition while P2 is in the hideout | Unchanged from W1: P2 one loading screen + renovation (P2 log shows the same `HIDEOUT_REBUILD` sequence) | NOT TESTED |
| 6 | P2 changes a wallpaper first, then P1 buys repairs (both in the hideout) | P2 still gets the repairs rebuild (W1 suppressed it after a wallpaper purchase) | NOT TESTED |
| 7 | P1 buys the garage (fresh copy) while P2 is in the hideout | P1 log has NO `garage bought by the other player` line (own purchase not re-presented); P2 sees the swap | NOT TESTED |
| 8 | P2 buys an addition exactly while P1 is travelling / in the loading screen | P2: refused (`Busy`, nothing charged; click again after P1 arrived) or accepted after P1's scene is loaded — never charged without the renovation appearing | NOT TESTED |
| 9 | After rows 1–2: P1 travels away and back; P2 disconnects and rejoins; host saves (travel), quits, restarts | Renovations stay on both PCs, money unchanged, no reload loop (no repeated `invoke` lines) | NOT TESTED |
| 10 | Regression: W1 rows 1–13, Shadowhawk, CarBox, heavy cargo, passengers, travel | Unchanged | NOT TESTED |

Send back both `LogOutput.log` files. Note: the supplied host log of the W1 report announced `TS2 Multiplayer 0.16.0` in its BepInEx header; please confirm that both PCs show `0.21.1` this time.

## 41. W1 checklist — shared garage + hideout renovations (release `TS2-Multiplayer-0.21.0.zip` on BOTH PCs, protocol 32)
F8 test campaign copy WITHOUT the garage, without renovations, shared money ≥ 20,000. Grep tags: `HIDEOUT`, `hideout buy`, `HIDEOUT_STATS`, `original hideout rebuild`, `sandbox keys follow`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Both in the hideout. P2 buys the garage at the garage trigger | P2: original window, buy animation, sound; garage opens. P1 sees the garage swap at the same moment (walls/gate gone, brick fence, lamps, laptop; can walk into the garage). Money −8000 once on both. Host `hideout buy Garage by P2 -> Accepted`, client `HIDEOUT purchase … accepted`, host `garage queued for the next host checkpoint` | NOT TESTED |
| 2 | Fresh copy: P1 buys the garage while P2 is in Madison | P1 original purchase; P2 not interrupted. P2 travels to the hideout → garage built. P2 at the Auto Shop sees the Shadowhawk sign | NOT TESTED |
| 3 | Both click Buy at the same time / P2 double-clicks | One charge only; the later click logs `refused … Stale` / `already owned` | NOT TESTED |
| 4 | With < 8000 / < 1000 | Original: no garage button; renovator shows its own "too poor" page; nothing charged | NOT TESTED |
| 5 | Both in the hideout. P2 buys an addition (e.g. audio system) on the hideout computer | P2: original loading screen + rebuilt hideout with the radio. P1: original loading screen right after (`original hideout rebuild`), same upgrade, both at the hideout spawn; inventories, money, cars unchanged; session stays connected | NOT TESTED |
| 6 | P1 in the hideout sits in a car / carries a heavy object while P2 buys house repairs | P1's rebuild waits until P1 is out of the car / dropped the object (`rebuild … waits`), then the original loading screen once | NOT TESTED |
| 7 | P1 in Madison, P2 in the hideout buys repairs | P2 reloads; P1 is not interrupted (no loading screen). P1 travels home → repaired hideout | NOT TESTED |
| 8 | P1 (host) in Madison: P2 buys an addition + wallpaper | Accepted although the host is elsewhere; host travel home → upgraded hideout | NOT TESTED |
| 9 | Wallpaper / floor: P2 changes the bedroom wallpaper while P1 watches | P1 sees the new material live (no reload); −50 once | NOT TESTED |
| 10 | P1 buys an addition (original reload) while P2 is in the hideout | P2 gets the original rebuild once; no repeated reloads | NOT TESTED |
| 11 | P2 disconnects right after a purchase (during the loading screen), rejoins | Purchase kept once, no second charge; P2 sees the upgraded hideout after the rejoin | NOT TESTED |
| 12 | Host saves (travel), quits, restarts, hosts; P2 joins | Garage + renovations on both PCs, money unchanged. Fresh copy: P2 buys repairs, P1 gets caught and loads the checkpoint without saving → repairs AND money rolled back together | NOT TESTED |
| 13 | Regression: Shadowhawk purchase / unlock, travel proxies, CarBox, driving, passenger seats, heavy cargo, tailgate, F7 | Unchanged | NOT TESTED |

## 40. V1.2 checklist — Shadowhawk purchase / unlock + Auto Shop travel proxy (release `TS2-Multiplayer-0.20.0.zip` on BOTH PCs, protocol 31)
Both PCs need 0.20.0. Use the F8 test campaign with the hideout garage bought (`garage`) and the Shadowhawk NOT bought; shared money ≥ 50,000. Grep tags: `VEHICLE purchase`, `buy car#`, `unlocked in the shared fleet`, `placed at the original hideout spot`, `Auto Shop travel proxy`, `travel proxy presentation`, `Auto Shop purchase sign`, `VEHICLE_STATS … buy req`.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | P2 goes to the Auto Shop, presses E on the Shadowhawk sign, clicks Buy | Original window; after the click: original `bought_van` message + sound, window closes. Client `VEHICLE purchase … requested #N`, `accepted`; host `VEHICLE buy car#2 by P2 -> Accepted (charged 25000 of …)`. Money on BOTH PCs −25,000 exactly once | NOT TESTED |
| 2 | P1 (host) at the Auto Shop during row 1 | Host's sign disappears (`Auto Shop purchase sign switched off`); E/Buy no longer possible | NOT TESTED |
| 3 | P2 clicks Buy repeatedly / both players click at the same time (fresh campaign copy) | One charge only; the later click logs `already owned` / `refused … Stale`; no money lost | NOT TESTED |
| 4 | With < 25,000: open the window | No buy button (original); nothing charged | NOT TESTED |
| 5 | Fresh copy: P1 (host) buys at the Auto Shop | Original purchase on P1; host `unlocked in the shared fleet (host purchase …)`; P2's sign disappears if P2 is there | NOT TESTED |
| 6 | After the purchase: both travel to the hideout (separately, then together) | Shadowhawk at the original second hideout spot on BOTH PCs, same place; one player simulates it, the other sees it parked (no second copy, no car inside a player) | NOT TESTED |
| 7 | One player is already in the hideout while the other buys | The Shadowhawk appears in the hideout during the session (`placed at the original hideout spot`), Ranger unaffected | NOT TESTED |
| 8 | Drive the Shadowhawk: driver handoff, passenger seat, first/third person, doors; then the Ranger (doors + slow doors + tailgate) | Unchanged behaviour (M4.2–M5.0) | NOT TESTED |
| 9 | Host saves (travel), quits, restarts, hosts; P2 rejoins | Shadowhawk still owned on both PCs, money stays deducted once. Fresh copy: buy, then caught-screen Load without travelling → car locked again AND money back (original checkpoint rule) | NOT TESTED |
| 10 | Auto Shop: P2 goes there first (on foot), then P1 arrives with the Ranger | P2 first sees a frozen Ranger at the Auto Shop car spot (`Auto Shop travel proxy … original Auto Shop car spawn`); when P1 arrives it becomes the real Ranger in place (no second car) | NOT TESTED |
| 11 | P1 starts the engine and travels away with the Ranger | On P2 the Ranger stays where it stood as a frozen car (`travel proxy presentation … AutoShop`); P2 can walk around it / is never pushed | NOT TESTED |
| 12 | P2 opens the proxy door and presses E | Original travel menu; P2 travels on foot; P1 keeps driving the real Ranger (no teleport, no seat, no storage, no cargo on the proxy) | NOT TESTED |
| 13 | Roles reversed (P2 drives away, P1 stays); reconnect P2 while P1 stands at the proxy | Same proxy behaviour on the host; after the rejoin still exactly one real Ranger | NOT TESTED |
| 14 | Ranger cargo (H2) + CarBox while it is a proxy, then back as a real car | Proxy shows no cargo and no storage; the real car shows its cargo again; H1/H2 rows 1–6 unchanged; F7 still works | NOT TESTED |

## 39. H2 checklist — shared heavy cargo in the Ranger (release `TS2-Multiplayer-0.19.0.zip` on BOTH PCs, protocol 30)
Both PCs need 0.19.0 (0.18.x is refused). Use the F8 test campaign (isolated host save copy). Grep tags: host + client `CARGO`, `HEAVY … loaded into vehicle`, `HEAVY Load`, `CARGO_STATS`, `HEAVY_STATS … cargo loads`. Shared cargo = the Ranger's open cargo bed (drop the object into it); the separate trunk trigger / heavy shop are not part of H2.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Both in Madison, Ranger parked there. P1 picks up a TV and drops it into the Ranger's cargo bed | P1: original drop, the TV settles in the bed. Host: `HEAVY 'tv…' at rest … - in the cargo bed`, `HEAVY 'tv…' loaded into vehicle 1 (… cargo #N item …)`, `CARGO #N (item …) in PLAYER CAR at local …`. Exactly one TV visible on P1 (no second copy) | NOT TESTED |
| 2 | P2 looks at the Ranger | P2 sees the TV in the bed at the same place/orientation (same model); the TV is gone from the house. Client `CARGO #N … in PLAYER CAR` | NOT TESTED |
| 3 | P2 picks up a second heavy object (printer / painting) and drops it into the bed | P2: original drop (no refusal next to the Ranger). Client `HEAVY Load … sent`, then `loaded into vehicle 1`; host `heavyTxn Load … by P2 -> Accepted` | NOT TESTED |
| 4 | P1 looks at the bed | Both objects in the bed on both PCs at the same poses; no duplicate anywhere | NOT TESTED |
| 5 | P2 takes the TV out of the bed (E) and carries it; P1 tries to take the same TV at the same moment | One carries it, the other's E does nothing. P1 sees the TV on P2's avatar (CarryRun), not in the bed. Client `HEAVY Player 2 carries … cargo #N taken out` | NOT TESTED |
| 6 | P2 drops the TV on the ground next to the car, then puts it back into the bed | Ground: both PCs show the fall/settle (H1.1 path). Back in the bed: `loaded into vehicle 1` again with the same cargo # | NOT TESTED |
| 7 | Repeat loading/unloading several times by both players; then fill the bed (if enough heavy objects are around) | Every load appears once on both PCs. At 24 objects the next one stays where it lies: `HEAVY Load … -> Busy (cargo bed full (24))`, object still pickable | NOT TESTED |
| 8 | P1 drives the Ranger with cargo (P2 as passenger, then P2 standing outside watching) | Cargo moves with the car on both PCs, never falls through or stays behind; after P1 exits, the cargo stays in place (`CARGO_STATS … refrozen`) | NOT TESTED |
| 9 | Travel with the cargo to another region (joint trip, then P1 alone) and back | Cargo still in the bed at the destination on both PCs (`CARGO #N … in PLAYER CAR` after the scene load); a P2 still in Madison cannot take cargo out of a car that is elsewhere | NOT TESTED |
| 10 | P2 takes an object out and, while carrying it, disconnects; P2 reconnects | P1: the object goes back into the bed (host `restores 1` in `CARGO_STATS`). After the rejoin P2 sees exactly the host's cargo, no extra objects | NOT TESTED |
| 11 | P1 saves (sleep / save point), quits, restarts and hosts again; P2 joins | Host `CARGO host save: vehicle 1 cargo written … (N shared)`; after the restart `CARGO vehicle 1 seeded from the host save: N heavy object(s)` and the same objects on both PCs. P2's sandbox never logs a cargo save (`SaveItems skipped`) | NOT TESTED |
| 12 | Story / counters: note `StolenBigItems` (stats screen) and the current story objective before loading; load 2 objects | No counter, money, XP or story change from loading (original bed loading has none); the heavy-shop sale and the trunk trigger are not part of H2 | NOT TESTED |
| 13 | H1.1 regression: pick up / carry / drop / throw / settle heavy objects away from the car (§38 rows 1–6) | Unchanged behaviour; `PERF` lines: mod cost and FPS comparable to 0.18.0 (report the numbers) | NOT TESTED |
| 14 | P2 drops a heavy object next to the Shadowhawk (if unlocked) or onto the Ranger's roof | Shadowhawk: P2 keeps carrying (`refused … no shared cargo bed`). Ranger roof: the object stays where it lies on both PCs (fixed, H1 rule), not loaded | NOT TESTED |

## 38. H1 / H1.1 checklist — heavy objects: pickup / carry / drop for both players (release `TS2-Multiplayer-0.18.0.zip` built with H1.1 on BOTH PCs, protocol 29)
Both PCs need 0.18.0 (0.17.x is refused: protocol / mod mismatch). **H1.1: both PCs must run the same H1.1 package** — an earlier H1 build has the same version number and is not refused, but cannot read the v2 `wheavy` list (`HEAVY registry list … unreadable`). Back up the host save. Run in Madison Street (48 heavy objects: TVs, printers, computers, paintings). Keep both `LogOutput.log` files. Log tag `HEAVY` (+ `HEAVY_STATS` every 30 s while active, `AVATAR_GEAR carried item …`).

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | P1 picks up a TV (E) | P1: original carry (pose, slow walk, drop prompt). P2: the TV disappears from its spot and appears in front of P1's avatar with the CarryRun animation; same model. Host `HEAVY Player 1 carries '<name>' (MadisonSt:<hash>, lease N)`; client `AVATAR_GEAR carried item <id> from the leased scene object …` | NOT TESTED |
| 2 | P1 drops it (E) on the floor, then throws another one (throw key) | P1: original drop / throw physics. **H1.1:** P2 sees the TV leave the avatar's arms and appear at the release point in the same moment (no invisible interval, never two TVs), then fall / slide smoothly and stop at exactly P1's pose (compare position / tilt). Host `HEAVY '<name>' dropped (original DropHeavy) at … release pose committed now`, then `at rest after … s … exact pose committed (lease N, K movement snapshot(s) sent)`; client `HEAVY <hash> followed K snapshot(s) over … s from P1: exact rest pose … (final correction 0.0xx m)` | NOT TESTED |
| 3 | P2 walks into / against the TV P1 placed; then P1 moves it without picking it up (push it / hit it with the car, if the original allows) | Solid on P2's PC and it does not move there (kinematic on the observer). If the original physics moves it on P1's PC, P1's log shows `moving again` within 0.5 s and P2's copy follows the movement and stops at the new rest pose | NOT TESTED |
| 4 | P2 picks up the same TV | Client `HEAVY pickup … requested`, then `HEAVY Player 2 carries '<name>' (lease N, … ms after the click): original PickUpHeavy ran once`; P2: original carry; P1: the TV vanishes from the floor and is carried by P2's avatar | NOT TESTED |
| 5 | P2 drops it on a table / slope | P1 sees it appear at P2's release point at once, follow the fall, and both PCs show the same settled pose; host log `HEAVY heavyTxn Drop … by P2 -> Accepted`, then `heavyTxn Settle … -> Accepted`; host `HEAVY … followed K snapshot(s)` | NOT TESTED |
| 6 | Both press E on the same free TV at the same moment (count down by voice) | Exactly one carries it; the other's E does nothing (no carry, no second TV). Client log either `… carries` or `-> Stale … nothing happens`; host `heavyTxn Lease … -> Stale (carried by P1)` or P1 `refused: carried by P2` | NOT TESTED |
| 7 | P2 carries a TV; P2 disconnects (quit to menu / pull the network) | P1: the TV reappears at its scene pose (never placed) or at its last rest pose; no second TV anywhere. Host `HEAVY_STATS … slotReleases` ≥ 1. P2 rejoins: the TV is where P1 sees it | NOT TESTED |
| 8 | P1 carries a TV while P2 disconnects and rejoins | P1 keeps carrying; after the rejoin P2 sees it carried by P1's avatar | NOT TESTED |
| 9 | P2 drops a TV, then leaves Madison (travels away) while P1 stays | P1 still sees the TV at P2's rest pose; when P2 returns it sees it there too | NOT TESTED |
| 10 | Both leave Madison (nobody there), then return | Every moved heavy object is back at its original scene pose (vanilla reload rule) | NOT TESTED |
| 11 | P2 carries a TV to the Ranger trunk / cargo area and tries to drop it on / in a car; also walks into the car's heavy trigger | Nothing happens: P2 keeps carrying (`HEAVY Player-2 drop … refused: it would land on / in the vehicle` / `deposit … blocked`). Vehicle loading is H2 | NOT TESTED |
| 12 | P1 loads a TV into the Ranger (original trunk trigger / cargo) | P1: original deposit unchanged. P2: the TV stays hidden (no second copy); host `HEAVY '<name>' left the shared world: …` | NOT TESTED |
| 13 | P2 tries a heavy object in MainScene or a shed / van item spawned from the save | Nothing happens (`not a verified heavy object of the current region`) | NOT TESTED |
| 14 | Regression: ordinary loot pickups (both), money, dropped items, inventory, CarBox/locker, doors, driving / passenger, travel | Unchanged (§35–§37 rows) | NOT TESTED |
| 15 | Performance | Note FPS in Madison with and without a carry; `HEAVY_STATS … frameMax` < 1 ms | NOT TESTED |
| 16 | H1.1 — TV, printer and a painting: each dropped by P1 (host) and by P2 (client) onto the floor | Each: visible on the other PC immediately at the release point, visible fall, same rest pose; no frame with the object both in the avatar's arms and on the floor, no frame with neither | NOT TESTED |
| 17 | Drop onto an elevated surface (table / shelf / bed) and off an edge so it falls further (stairs / table edge) | Observer follows the fall onto / off the surface; rest pose identical on both PCs (`final correction` ≤ ~0.05 m) | NOT TESTED |
| 18 | Throw (throw key) so the object slides / tumbles across the floor | Visible slide on the observer; streaming stops at rest (`HEAVY_STATS motion sent` stops growing while nothing moves) | NOT TESTED |
| 19 | Immediate re-pickup: drop, then pick the same object up again while it still falls (same player, then the other player) | Pickup works; observer switches straight to the carry; no second object; late snapshots ignored (`HEAVY_STATS … rejected/stale` may grow) | NOT TESTED |
| 20 | P2 drops a TV and quits to the menu (or pulls the network) while it is still falling; P1 in the region | P1: the TV keeps falling with original physics on P1's PC and comes to rest (host `HEAVY '<name>' … handed over while still moving … original physics continues on this PC`); never frozen in the air; P2 rejoins and sees it at P1's pose | NOT TESTED |
| 21 | P1 (host) drops a TV and travels away while it still falls; P2 stays | P2: `handed over while still moving`, the TV comes to rest on P2's PC; P1 returns and sees it there | NOT TESTED |
| 22 | Normal inventory item: host drops an item from the TAB inventory and keeps TAB open; then the client does the same | The other PC shows the item right after the drop (surface position) while the dropper's TAB stays open; the dropper's item does not fall until TAB closes, then falls normally; the other PC moves it to the exact rest pose; inventory counts correct, no duplicate (`WITEM … dropped …`, `WITEM_DIAG … settled`) | NOT TESTED |
| 23 | Regression: vehicles (drive, passenger, doors), doors / locks / furniture, loot, Spind (locker), CarBox | Unchanged | NOT TESTED |
| 24 | FPS: host and client, 60 s each in Madison idle, carrying, and dropping a TV repeatedly (with `Debug.PerfStats = true`) | `PERF … avgFps / low1%` per instance, compare with the H1 / earlier values; `HEAVY_STATS frameMax`; `motion sent` bytes ≈ ≤ 0.8 kB per second of movement | NOT TESTED |

## 37. W4.2D checklist — cross-region loot (release `TS2-Multiplayer-0.17.0.zip` on BOTH PCs, protocol 28)
Both PCs need 0.17.0 (0.16.x is refused: protocol / mod mismatch). Back up the host save. Run §36 first (v0.16.1 is not yet tested in game; its loading-screen and spawn rows still apply). Keep both `LogOutput.log` files.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Host: F8 → Host in the main menu, wait ~20 s before Continue | Host log `LOOT_CATALOG <scene> … digest=… loot=…` for 14 scenes, then `LOOT_CATALOG ready: 14 scene(s) in N ms` (note N); MadisonSt digest `50D2F273`, PlayerHideout `C14C64A1`, MainScene `4A364B6F` | NOT TESTED |
| 2 | P1 in Madison, P2 travels alone to Hillville; P2 picks ordinary items and money | Host `WLOOT Hillville occupancy seeded …`, `WLOOT Player-2 pickup #N … (Hillville:…) ACCEPTED … host copy not loaded here`; P2's money appears once in the shared wallet on both PCs | NOT TESTED |
| 3 | P1 later travels to Hillville | The objects P2 took are gone for P1; P1 can still pick the rest | NOT TESTED |
| 4 | Both in Hillville: P1 picks an item, P2 picks another; then each tries the other's | P1's object disappears on P2's PC and vice versa; second attempts do nothing | NOT TESTED |
| 5 | Hideout: P2 picks the hideout loot objects (2) | Accepted once; P1 cannot pick them afterwards | NOT TESTED |
| 6 | MainScene loot (an object that exists in every region) while P1 and P2 are in different regions | Taken by one player = unavailable for the other; host log `(MainScene:…)` | NOT TESTED |
| 7 | Same-name objects (e.g. Madison `Pot`, Hillville `Wallet brown`): P2 takes one, the other stays pickable until both leave and return | After leaving the region with nobody left there and returning: every member gone until its respawn day | NOT TESTED |
| 8 | A key (e.g. Madison `Key_102_Porch`) picked by P2 | Accepted as UniqueValuable; P1 cannot pick it; P2 can unlock the matching door (lock state shared, W4.2B) | NOT TESTED |
| 9 | File cabinet with random contents (Madison house 114, BankScene): note what both PCs show; one player takes an item | Normally both see the same contents (`cabinetSeeds` > 0 in `WLOOT_STATS`); after one item is taken, the other alternative of the same slot is refused (`… cabinet slot already gave another outcome`) | NOT TESTED |
| 10 | A parcel in Madison picked by P2 | Host `… parcel '<pref>' locked in the host campaign`; P1 cannot pick it; after a host save + reload the parcel stays locked | NOT TESTED |
| 11 | A tool / no-XP object (e.g. a crowbar on the ground) picked by P2 | `LiveOnly` accepted, no ItemDay written; after the region is reloaded by both it is back (vanilla respawn) | NOT TESTED |
| 12 | P2 tries a story item (heavy items: superseded by §38, H1) | Nothing happens; client `… excluded: StoryPick` | NOT TESTED |
| 13 | P2 disconnects during a pickup (pull the network / quit) and rejoins | Item either granted once (record) or not at all; object state matches on both | NOT TESTED |
| 14 | Host saves, quits without saving after a P2 pickup in Hillville, restarts, P2 rejoins | P2 keeps the item; the object stays gone in Hillville (durable world take replayed) | NOT TESTED |
| 15 | Single player without F8 | No `LOOT_CATALOG` / `WLOOT` lines; cabinets roll normally | NOT TESTED |
| 16 | Performance | Note FPS in the host main menu while `LOOT_CATALOG` builds and in Madison/Hillville; `WLOOT_STATS … decideMax/observeMax` < 5 ms | NOT TESTED |

## 36. v0.16.1 checklist — two-PC QA fixes: loot duplicates, join loading screen, Player-2 spawn (release `TS2-Multiplayer-0.16.1.zip` on BOTH PCs, protocol 27)
Both PCs need 0.16.1 (0.16.0 is refused: mod version mismatch). Back up the host save first. Keep both `BepInEx\LogOutput.log` files. Re-check §35 rows for wallet, doors/drawers and travel once as a regression (they passed on 0.16.0).

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Madison: P1 picks an ordinary item / money / unique valuable | P2's copy disappears; host `WLOOT Player 1 took '…' (…, Valuable/Money/UniqueValuable) -> Accepted`; P1 credited once | NOT TESTED |
| 2 | Madison: P2 picks the same three classes | Host `WLOOT Player-2 pickup #N of '…' ACCEPTED by the host: item … +1 / $… credited once`, host copy removed; P1 cannot pick it; wallet rises once on both | NOT TESTED |
| 3 | Madison: a shared-name object (`Pot`, `Wine1`) — P2 picks one, then P1 tries the same one | Host `… shared-name family … ACCEPTED`; object gone on P1's PC; the other `Pot` stays pickable for both (vanilla until reload) | NOT TESTED |
| 4 | Madison: P2 tries an excluded object (contract/parcel/story item, file-cabinet content) and a heavy item | Nothing happens on P2's PC; client `WLOOT Player-2 pickup of '…' refused: excluded: …` / `heavy pickup … refused`; P1 can still pick it normally | NOT TESTED |
| 5 | Hillville or hideout: P2 tries any loot | Nothing happens; client `… refused: region Hillville not shared yet`. P1 picks it normally (P2 still sees P1's picked object there — known, see PROGRESS) | NOT TESTED |
| 6 | P2 drops an unshareable own item (if any) and picks it back up | Works locally (own drop) | NOT TESTED |
| 7 | Both press E on the same Madison object at the same moment | Exactly one gets it; the other's press does nothing; no double money/item | NOT TESTED |
| 8 | P1 saves, leaves Madison with P2, both return (or host restarts and P2 rejoins) | Host `WLOOT MadisonSt loaded: N shared object(s) already removed by the host save listed as taken`; nothing taken before respawn day reappears for P2 | NOT TESTED |
| 9 | Single player (no F8): main menu Continue / New Game | Unchanged original loading screen; no `JOIN_LOADING` lines | NOT TESTED |
| 10 | P1: F8 → Host, menu stays usable; then Continue | Play/Settings clickable while hosting is prepared; normal original loading screen | NOT TESTED |
| 11 | P2: F8 → Join (host already in world) | Main menu switches to the original loading screen at once, Play/Settings do not react; `JOIN_LOADING original loading screen shown …`; world loads; `JOIN_LOADING handed over …`; camera/cursor/controls normal in world | NOT TESTED |
| 12 | P2: Join while the host is still in its main menu, then press F8 → Abbrechen | Loading screen while retrying; after cancel the original main menu is back and clickable (`JOIN_LOADING main menu restored`) | NOT TESTED |
| 13 | P2: Join with a wrong code / unreachable address | Error in the F8 window, main menu restored and clickable | NOT TESTED |
| 14 | Full restart: P1 saves in Madison (quicksave or manual), both quit, P1 Continues, P2 joins | P1 at its saved spot; P2 beside that saved spot (host `spawn RestoreNear/FirstJoin … host checkpoint MadisonSt Saved_Pos 1`) — NOT at P2's last pre-restart position | NOT TESTED |
| 15 | Full restart after a travel into Madison/Hillville without a later save | P1 seated in the car at the leave point; P2 beside that leave point (`SafePoint/FirstJoin … travel arrival (Saved_Pos 0)`) | NOT TESTED |
| 16 | Full restart with the host checkpoint in the hideout / Auto Shop | Both at the original fixed spawn (P2 beside P1, `OriginalSpawn/FirstJoin`) | NOT TESTED |
| 17 | P2 walks away (P1 does not save), P2 quits to menu, rejoins with F8 while P1 keeps playing | P2 back at its last position (`Restore/Rejoin … live rejoin (host world still running)`) | NOT TESTED |
| 18 | P2 moves, nobody saves, P1 returns to menu (session ends), P1 Continues, P2 joins | P2 at the host checkpoint, not at its unsaved position | NOT TESTED |
| 19 | Host data folder | `p2spawn-<profile>.dat` (if present from older builds) unchanged (same date/size); no new p2spawn files written | NOT TESTED |
| 20 | Logs / FPS | No `ERROR`; `WLOOT_STATS` shows `familyTakes`, `p2Refused(local)`, `heavyRefused`; seeding time in `WLOOT MadisonSt loaded: … ms` < 20 ms; note FPS in Madison before/after a pickup | NOT TESTED |

## 35. W4.1B checklist (integrated) — shared money & duplicate-free world loot (release `TS2-Multiplayer-0.16.0.zip` on BOTH PCs, protocol 27)
Both PCs need 0.16.0 (0.15.0 and older are refused: protocol / mod mismatch). Run §34 (furniture) once more on 0.16.0 as a regression check. **Use a real campaign or a backup copy, NOT the F8 test campaign** for rows 1–9: the test campaign tops the host balance up to 2,000,000 whenever it falls below 1,000,000, which hides the arithmetic. Note the host's money before each row. Send BOTH `plugin.log` files (`%LOCALAPPDATA%\TS2MP\logs\<stamp>_game_<pid>\`). Look for `WALLET`, `WLOOT`, `P2INV`, `WALLET_STATS`, `WLOOT_STATS`.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Host loads the save, client joins | Client money = host money (not Player 2's old personal money, not the sum). Client log `WALLET mirror active … (legacy personal money X / bank Y kept untouched, not added)`; host log `WALLET bound: shared balance N` | NOT TESTED |
| 2 | Example A: host buys something for a known price in an original shop | Both show balance − price within ~1 s; client log `WALLET money A -> B (host balance …)` | NOT TESTED |
| 3 | Example C: client buys an item (original shop) | Price deducted ONCE on both; item in the client backpack; host log `WALLET Player-2 money change (report, spend +price): -price -> shared balance …` exactly once | NOT TESTED |
| 4 | Client sells an own item (original sale / Blackbay) | Both balances + the original sale price; item gone from the client | NOT TESTED |
| 5 | Example B (Madison Street): client picks up a world money note (e.g. `15$…`) | +amount once on both; note disappears on the host too; host log `WALLET Player 2 collected world money '…'` + `P2INV`/`WLOOT`; host cannot pick it up | NOT TESTED |
| 6 | Host picks up a different money note | +amount once on both; note disappears on the client | NOT TESTED |
| 7 | Both press E on the same note at the same moment | Exactly one gets it; money +amount once; the other: nothing happens | NOT TESTED |
| 8 | Unique valuable (e.g. `vase_102_2`, `Toaster`, `alarm_102`) picked by the client, then by nobody else | Gone on both; host cannot take it; after both leave and return (even days later) it stays gone on both | NOT TESTED |
| 9 | Simultaneous spend: bring the balance to just above one item price, both buy at the same moment | Balance never below 0; if the host's purchase landed first, the client's item disappears again and its money returns (log `WALLET Player-2 report -> Stale: insufficient shared funds`) | NOT TESTED |
| 10 | Client tries garage / car upgrade (Whiplist) / hideout renovation / drone upgrade / Darkbank / stocks | Nothing happens, money unchanged; client log `WALLET <Type>.<Method> refused on the client` once | NOT TESTED |
| 11 | Client completes a job / contract / payphone mission or pickpockets an NPC | Original flow runs; money NOT added to the shared balance (client log `WALLET … not credited to the shared balance … reverted`) | NOT TESTED |
| 12 | Client: crash the car (repair cost), refuel, buy a drink | Costs deducted once from the shared balance | NOT TESTED |
| 13 | Rent day passes (sleep) with both connected | Rent charged ONCE (host); client log `WALLET rent … reverted` | NOT TESTED |
| 14 | Client travels; host travels; both reconnect (client leaves via menu and joins again) | Balance identical on both after every step; no jump | NOT TESTED |
| 15 | Checkpoint: host saves, host buys X, client buys Y, host quits to menu WITHOUT saving and continues | Host's purchase rolled back (+X), client's purchase and its money stay (balance = saved − Y); client keeps the item; host log `WALLET replay of 1 Player-2 money change(s)` | NOT TESTED |
| 16 | Host saves normally, quits the game, restarts and hosts again | Same balance; host log `WALLET bound … journal 0` | NOT TESTED |
| 17 | Client picks up money in Hillville or the hideout (furniture there is shared since 0.15.0) | Nothing happens (client log `WLOOT world money '…' refused for Player 2`) — documented limitation | NOT TESTED |
| 18 | Performance | No visible hitch; `WALLET_STATS mirrorMax` < 0.1 ms; `WLOOT_STATS decideMax` similar to 0.15.0 | NOT TESTED |

## 34. W4.2C Phase B checklist — shared furniture, Hillville + hideout objects (plugin 0.15.0 / protocol 26; deployed by Claude in the local game folder, release `TS2-Multiplayer-0.15.0.zip` for the second PC)
Both PCs need 0.15.0 (a 0.14.x peer is refused: mod version mismatch). Logs: `%LOCALAPPDATA%\TS2MP\logs\<stamp>_game_<pid>\plugin.log` on BOTH PCs. Useful lines: `WOBJ <region> ready: … furniture leaves in the scene N …`, `WOBJ proposed …` / `WOBJ host … -> Accepted`, `WOBJ_STATS … furniture local/applied=a/b inactiveSnaps=… pagesParsed=…`, `WORLD_CENSUS_OBJECTS <region> … digest=…` (must be equal on both PCs per region).

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Both in Madison, same house. Player 2 opens a kitchen drawer (quick E), then closes it | Host sees the drawer slide open and closed with the original drawer sound; positions match; P2's log `WOBJ proposed … Door …`, host `Accepted` | NOT TESTED |
| 2 | Host opens a cupboard, a fridge and a wardrobe door (quick E) | Player 2 sees each open with its own original sound (cupboard / fridge / door clips), no snapping | NOT TESTED |
| 3 | Hold E on a cupboard / file-cabinet drawer (slow open), release halfway | The other player sees the slow (quiet) motion, then the fast finish at the same moment | NOT TESTED |
| 4 | Both press E on the same closed drawer at the same time | It ends in ONE state on both screens (the losing press snaps back to the committed state); no flip-flop | NOT TESTED |
| 5 | Locked cabinet / glass cabinet / container with a lock: Player 2 picks the lock, opens the leaf | Host: lock opens, then the leaf opens; before the lock is shared the host's leaf never opens on its own | NOT TESTED |
| 6 | Loot inside: Player 2 opens a drawer and takes an item; host looks into the same drawer | Drawer open on both; the item is gone for the host (shared Madison loot, as before); opening alone gives nobody an item, money or XP | NOT TESTED |
| 7 | File cabinet in the villa (`DOMY/Villa/Interior_114/FileCabinetFull`, Madison) or House 207 (Hillville): both open the same drawer | Drawer state shared; the CONTENTS may differ between PCs (random per load, original) — each player can take only what their own screen shows; no item appears twice in one inventory | NOT TESTED |
| 8 | Hillville: both players in the same house — doors, drawers, a lock, a window | Shared like Madison. Host log `WOBJ Hillville ready`; if instead `scene metadata differs (census digest …)` appears, send both logs (Hillville stays local — safe) | NOT TESTED |
| 9 | Hideout: Player 2 opens/closes doors and a cupboard while the host watches | Shared; nothing else in the hideout changes (garage, cat, renovations unchanged) | NOT TESTED |
| 10 | Player 2 alone in Hillville (host stays in the hideout) opens furniture; host then drives to Hillville | Host arrives and sees P2's open drawers / doors; nothing reverts | NOT TESTED |
| 11 | Region reload: both leave Madison (≥ 5 s), come back | All furniture closed again (original reload); random doors rolled anew | NOT TESTED |
| 12 | Car doors: open the doors of your car and a parked car | Unchanged (vehicle door sync for the player cars; parked cars local) | NOT TESTED |
| 13 | Regression: house doors, crowbar break-in, lockpicking, windows, vehicles + driver handoff, P2 inventory / CarBox, loot + drops, travel, reconnect | As confirmed with 0.14.x | NOT TESTED |
| 14 | Performance: open/close ~10 drawers quickly in a kitchen while the other player watches; note FPS before/after | No stutter; log `WOBJ_STATS` refused stays small (rate limit 8/s per player: a 9th press within a second snaps back) | NOT TESTED |

## 33. W4.2C Phase A checklist — cancelling a prepared host (plugin 0.14.4 / protocol 26; deployed by Claude in the local game folder, release `TS2-Multiplayer-0.14.4.zip` for the second PC)
Both PCs need 0.14.4 for a session (mod version check). Items 1–6 need only ONE PC (no Player 2) except where a join target is named. Log: `%LOCALAPPDATA%\TS2MP\logs\<stamp>_game_<pid>\plugin.log`. Do not restart the game between items 1–5.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Main menu → F8 → "Testkampagne hosten" → "Hosten abbrechen" (do NOT load a save) | Menu shows the Host section ("Hosten vorbereiten", not "Testkampagne hosten"), the Test-campaign section AND the Join fields again; no "TESTKAMPAGNE AKTIV" line; the game's own main-menu save info shows your REAL save again. Log: `SESSION_END (cancelled by the player in the main menu)`, `SAVE_GUARD test campaign RELEASED …`, `TEST_CAMPAIGN disabled …`, `TEST_CAMPAIGN released …`, `HOST cancelled in the main menu: state Offline, test campaign not prepared, save isolation off` | NOT TESTED |
| 2 | Continue: "Hosten vorbereiten" (normal) → "Hosten abbrechen" → "Hosten vorbereiten" again on the SAME port | Second prepare works (no "UDP-Port … belegt"); cancel returns to the full menu each time | NOT TESTED |
| 3 | Continue: "Testkampagne hosten" again → close F8 → "Fortsetzen" | Loads the TEST copy (test money / skills, menu showed "TESTKAMPAGNE AKTIV"); the earlier release did not delete the copy (progress from an earlier test session still there) | NOT TESTED |
| 4 | Restart. "Hosten vorbereiten" → cancel → "Verbinden" to a running 0.14.4 host on the other PC → play briefly → "Verbindung trennen" / return to main menu | Join works without a restart; after the disconnect "Verbinden" is offered again; hosting stays disabled with the restart hint (unchanged Player-2 rule) | NOT TESTED |
| 5 | Restart. "Testkampagne hosten" → cancel → "Verbinden" to a NORMAL host | Joins as Player 2 of the normal campaign (log `JOIN save isolation armed …` without "test campaign"); no "Andere Kampagnenart" error | NOT TESTED |
| 6 | Single player after item 1 (restart not needed): cancel the test host, then "Fortsetzen" without F8 | Your REAL save loads; progress you make is saved to the real save (check after a restart) — the test copy is unchanged | NOT TESTED |
| 7 | Regression: normal host session with Player 2 (doors, lockpicking, vehicles, inventory) | As confirmed with 0.14.3 | NOT TESTED |

## 32. W4.2B.3 checklist — main-menu-only multiplayer lifecycle, hosting with a car, floating-car diagnostics (release `TS2-Multiplayer-0.14.3.zip` on BOTH PCs, protocol 26)
Both PCs need 0.14.3 (a 0.14.2 peer is refused: mod version mismatch). Logs: `%LOCALAPPDATA%\TS2MP\logs\<stamp>_game_<pid>\plugin.log` on each PC — please send BOTH (the host log is needed for items 9–11).

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Start normally (Steam), play single player without pressing F8 | Unchanged single player; log only `PLUGIN_INIT mode=F8 … (no patch, no network until F8 Host/Join in the main menu)` and `MENU ready` | NOT TESTED |
| 2 | In a loaded world press F8 several times (on foot, then seated as driver) | Nothing happens: no window, camera / mouse / movement / car control unchanged; log at most one `MENU F8 ignored outside the main menu` per 30 s | NOT TESTED |
| 3 | Host: main menu → F8 → port → "Hosten vorbereiten" | Code shown; status "Host vorbereitet – lade jetzt deinen Spielstand …"; log `HOST prepared in the main menu: UDP 0.0.0.0:<port> listening, session systems attached before the save loads`, `HOST_READINESS Hauptmenü – lade zuerst deinen Spielstand` | NOT TESTED |
| 4 | Player 2 tries to join while the host is still in the main menu | Player 2 waits ("Host ist noch nicht bereit … neuer Versuch"); host log `JOIN_DEFERRED … host not gameplay-ready (Hauptmenü …)`; never `WELCOME` before item 5 finishes | NOT TESTED |
| 5 | Host closes F8 and clicks the game's "Fortsetzen" | World loads normally; host log `HOST world loading started`, `VEHICLE_BIND`, `REGION local none -> …`, the own car `presentation Hidden -> Real`, then `HOST_READINESS ready: Player 2 may join`; Player 2 is admitted automatically afterwards and loads the host's world | NOT TESTED |
| 6 | Host: save that was last saved with the player seated in / next to the car (if the game restores the seat: load it seated) | Host stays seated / the car stays where it is; no `must be released … leaving it first`, no `local copy deactivated` for the host's own car in the host log; readiness waits for `Fahrzeuge` if needed | NOT TESTED |
| 7 | Both play a few minutes, then the HOST returns to the main menu | Host log `SESSION_END (host returned to the main menu)`; Player 2 log shows the disconnect; no `SESSION_CLEANUP … failed` lines; host save + fleet written (`FLEET final write`) | NOT TESTED |
| 8 | Host: F8 in the main menu again → "Hosten vorbereiten" → Continue; Player 2 rejoins from its main menu | New code works; Player 2's inventory / position kept from the first session | NOT TESTED |
| 9 | Player 2 returns to the main menu while connected | ~1.5 s later Player 2 log `SESSION_END (player 2 returned to the main menu)`; host log shows Player 2 left; F8 in Player 2's main menu offers "Verbinden" again; rejoin works with the same code | NOT TESTED |
| 10 | Test campaign: restart host, main menu → F8 → "Testkampagne hosten" → Continue; Player 2 joins | Same as items 3–5 plus `TEST_CAMPAIGN prepared …`, `HOST prepared … MULTIPLAYER TEST CAMPAIGN`; Player 2 `TEST_CAMPAIGN active (Player 2)`; skills / test money as in §31 3–4; real saves untouched (§31 5) | NOT TESTED |
| 11 | Floating car: Player 2 drives (Madison Street, also hideout), host watches from outside, then as passenger; then swap (host drives, Player 2 watches) | Report what each PC shows. Both logs contain `VEHICLE_GROUND <car> role=driver …` on the driving PC and `role=observer …` on the watching PC (≤ 1 per 5 s while moving). Compare `clearance` (body above the road) and `wheelGap` (wheel bottom above the road) between the two PCs; `ownSkipped` > 0 means the old wheel ray hit the car itself | NOT TESTED |
| 12 | Regression spot checks (normal campaign): doors / lockpicking / windows, vehicle cameras, CarBox, shared drops, travel together | As confirmed with 0.14.2 | NOT TESTED |
| 13 | Optional, if possible: `Debug.PerfStats = true` on both, 5 minutes of driving | `PERF` lines: mod cost and GC comparable to 0.14.2 (VEHICLE_GROUND adds ≤ 1 string per car per 5 s) | NOT TESTED |

## 31. W4.2B.2 checklist — broken doors, passenger legs, observed car height, F8 multiplayer test campaign (release `TS2-Multiplayer-0.14.2.zip` on BOTH PCs, protocol 26)
Input: two-PC internet run 2026-11-13 19:23 with 0.14.1 (this PC = Player 2, log `20261113-192322_game_20044`): crowbar door now shared (`Set Door -> 5`), hold-E proposals `-> 3` present; user findings: broken doors cannot be closed, passenger legs visible in first person, car slightly above the ground on the passenger's PC.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Host: start through Steam, stay in the main menu, F8 → "Testkampagne vorbereiten", then the game's "Fortsetzen" | Log `TEST_CAMPAIGN prepared`, `DEVTEST host save copy created/kept`, `DEVTEST MODE ACTIVE (F8 multiplayer test campaign, F8 host)`; the world of the COPY loads; F8 shows "TESTKAMPAGNE AKTIV" | NOT TESTED |
| 2 | Host in the world → F8 → Hosten; Player 2 joins normally from the main menu | Host log `HOST started … MULTIPLAYER TEST CAMPAIGN`; client log `WELCOME … MULTIPLAYER TEST CAMPAIGN`, `SAVE_GUARD multiplayer test campaign (Player 2): sandbox …\testcampaign\client-sandbox`, `TEST_CAMPAIGN active (Player 2)` | NOT TESTED |
| 3 | Both open the skill tree | All skills learned, extra skills at max (`DEVTEST skills …`); story / missions unchanged | NOT TESTED |
| 4 | Both buy tools in shops until money < 1,000,000 | Money returns to 2,000,000 within 2 s (`DEVTEST money`); Player 2's own inventory keeps the tools after a rejoin; no duplicated money | NOT TESTED |
| 5 | After the session: compare timestamps of the real save folder, `%LOCALAPPDATA%\TS2MP\p2inv-*.dat`, `p2spawn-*`, `fleet-*` | Unchanged; test records only in `%LOCALAPPDATA%\TS2MP\testcampaign\` | NOT TESTED |
| 6 | Restart, normal F8 host / join (no test campaign) | Normal campaign, normal records, no `DEVTEST` lines | NOT TESTED |
| 7 | A player crowbars a door, both try to close it | Neither can use it (vanilla: a broken hinge makes the door unusable until the region is reloaded); same pose on both PCs. Compare once in single player if in doubt | NOT TESTED |
| 8 | Player 2 as passenger (Ranger rear bench, Shadowhawk front seat), first person; then swap roles (Player 1 passenger) | No own legs in the first-person view; third person still shows the own seated body; the driver still sees the passenger | NOT TESTED |
| 9 | On the passenger's PC look at the car from outside (third person / stand next to it) while parked and while driving | Wheels touch the road (no visible gap); body height like on the driver's PC; doors, entry/exit, travel, collisions unchanged | NOT TESTED |
| 10 | Optional `Debug.PerfStats = true` while driving with a passenger | Mod cost unchanged within noise (≤ 8 extra raycasts per frame for observed cars) | NOT TESTED |

## 30. W4.2B.1 checklist — regression fixes + developer test mode (plugin 0.14.1 / protocol 25 on BOTH; deployed by Claude in the local game folder)
**User run 2026-11-13 17:34 (0.14.0, developer launcher, one PC; logs `20261113-173429_host_0c25492c`, `-173434_client_5c23c038`, `-173621_client_c8200056`):** driving camera FAIL (Player 2 drove in first person, Player 1 as passenger followed it), crowbar door FAIL (lock open on the remote, leaf did not swing), relock → unlock FAIL (one instance kept a permanently open door), slow hold-E doors FAIL (not visible remotely), first client accepted in the host's main menu FAIL (`CAMPAIGN snapshot NOT sent … Player-2 record not bound`, the client had to be restarted). Root causes: PROGRESS "W4.2B.1".

Start: `build\TS2-Multiplayer-Launcher.exe` → **4 Developer Test** twice (host first), or `TS2-Multiplayer-Launcher.exe devtest` twice. The developer test plays an isolated COPY of your save (`%LOCALAPPDATA%\TS2MP\devtest\host-save`) and a separate Player-2 record (`…\devtest\p2inv-*.dat`); your real save and the normal multiplayer records are not used. Logs: `%LOCALAPPDATA%\TS2MP\devtest\logs`. For steps 1-2 the normal **1 Local Test** works as well.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Start the host, then IMMEDIATELY the client (host still in the main menu / loading) | Client waits (host log `JOIN_DEFERRED … host not gameplay-ready`, `HOST_READINESS …` transitions); once the host stands in its world the client joins by itself, loads the host campaign (client `CAMPAIGN host campaign … written`), no `CAMPAIGN snapshot NOT sent` | NOT TESTED |
| 2 | Player 2 drives a car (start the engine) | Third-person driving camera (as in single player); `V`/change-view toggles like vanilla; Player 1 entering as passenger sees the same mode (`PASSENGER camera -> third person`); toggling by the driver switches the passenger; stationary car / engine off / driver change / passenger exit / joint travel keep working | NOT TESTED |
| 3 | Player 1 drives with its own camera choice | Unchanged single-player behaviour (its own TPP setting) | NOT TESTED |
| 4 | Developer test: open the skill tree on both instances | All skills learned, extra skills at max (log `DEVTEST skills … learned`); no story/mission advanced | NOT TESTED |
| 5 | Developer test: buy tools in a shop on both instances | Money stays ≥ 250,000 (log `DEVTEST money … -> 1000000`); purchases work; after the session your real save and `%LOCALAPPDATA%\TS2MP\p2inv-*.dat` are unchanged (timestamps) | NOT TESTED |
| 6 | Madison: one player opens a door by holding E (slow), lets it finish | The other sees the slow, squeaking swing in the same direction, reaching the same final pose (log `slowReplicas` in `WOBJ_STATS`) | NOT TESTED |
| 7 | Hold E and release early (door then finishes fast) | The other's swing speeds up at the same moment (log `fastFinish sent/applied`), same final pose | NOT TESTED |
| 8 | Crowbar a locked house door (and a locked gate if you find one) | The remote sees the lock open AND the leaf swing open with the broken hinge; neither player can use it any more; lock + door identical on both | NOT TESTED |
| 9 | Player A opens a picked door, closes it, relocks it (R); Player B picks it again and opens it; repeat with roles swapped | Both instances end with the same state each time; never a door that stays open on one PC | NOT TESTED |
| 10 | A door that stays local (drawer / cupboard / car door) | Works locally as before; log once `WOBJ door '…' (isDrawer…) stays local` (W4.2C) | NOT TESTED |
| 11 | Normal single player (game started without the launcher, no F8) | Unchanged: no `DEVTEST`, no isolation, normal save | NOT TESTED |
| 12 | Optional: `Debug.PerfStats = true`, 2 min in Madison with door use | PERF mod cost unchanged vs 0.14.0 within noise; no GC spikes from the door code | NOT TESTED |

## 29. W4.2B checklist — doors, locks, windows (release `TS2-Multiplayer-0.14.0.zip` on BOTH, protocol 24)
Setup as §27/§28 (F8, two PCs). Optional `Debug.PerfStats = true` for step 20. Log tags: `WOBJ` (both), host `WOBJ objScene/objInit/objTxn … -> code`, `WOBJ_STATS` (PERF), `WORLD_CENSUS_OBJECTS`. Steps 2-15 in Madison Street with both players there unless stated.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Host loads its save IN Madison, opens one door and picks one simple lock, THEN presses F8 → Host; client joins into Madison | Host log `WORLD_CENSUS(late: objects only)`, `WOBJ host scene … epoch 1 started`, `initializer … N non-default`; client arrives with that door open and that lock unlocked | NOT TESTED |
| 2 | Both load Madison fresh (travel there one after the other); look at houses with randomly open doors | Both PCs show the SAME open doors; second loader log `ready: … committed doors N` | NOT TESTED |
| 3 | Client opens / closes a door (quick press), then hold-E slowly | Host sees the swing (slow = quieter, slower); logs `proposed Set Door` / host `Accepted` | NOT TESTED |
| 4 | Both press E on the same door at the same moment | Same final state on both, no flicker loop (loser `Stale`/`Unchanged`) | NOT TESTED |
| 5 | Client picks a SIMPLE lock (BLT minigame), succeeds | Client: minigame opens after the host's reservation (no visible delay on LAN); host sees the door unlocked (prompt "Door" instead of lock); host log `Reserve … reserved for P2`, `Set Lockpick … -> 1 … Accepted`; no police/alarm on the host | NOT TESTED |
| 6 | Same with a MEDIUM (pins) and an ADVANCED (automatic) lock | As 5; advanced: exactly one `Set Lockpick` (host log), later duplicates `Unchanged` | NOT TESTED |
| 7 | Client starts picking, host tries to pick the same lock | Host's E on the lock does nothing (`reservation … -> Busy`) | NOT TESTED |
| 8 | Client cancels the minigame (Esc) / breaks the tool | Lock stays locked on both; client log `interaction ended without a result … released`; tool wear only in Player 2's inventory | NOT TESTED |
| 9 | Key door (key in inventory), padlock (crowbar hit), crowbar break-in on a lock | Both PCs: unlocked; padlock falls on both (dust only on the replica when it was live); break-in door forced open and unusable on both | NOT TESTED |
| 10 | Player relock (relock button on a closed, previously unlocked door) | Locked again on both PCs | NOT TESTED |
| 11 | Window from inside (look at it from inside a house), glass cutting, brick throw / crowbar hit, crowbar break-in | Both PCs show the same result (glass stays / gone / shards), the window opens/closes on both afterwards; alarm and noise only for the actor | NOT TESTED |
| 12 | Host at the hideout, client alone in Madison does 3, 5, 11 | Host log `Accepted (… unverified id …)`; then host travels to Madison: arrives with every change applied, no sound burst | Local Test (dev hook, no keyboard): scene report, P2 initializer, door/lock/window commits and a reservation round trip Accepted — keyboard + host arrival NOT TESTED |
| 13 | Both leave Madison, one returns after > 5 s | Vanilla reset: original scene state (new epoch in the log) | NOT TESTED |
| 14 | One leaves, the other stays; the first returns | Changes still there (same epoch) | NOT TESTED |
| 15 | Client disconnects (quit) during a lockpicking minigame; rejoins | Host can pick that lock meanwhile; rejoined client sees the committed state | NOT TESTED |
| 16 | Client tries a story lock / mission window (needs a story save at such a step) | Nothing happens for Player 2 (log `story/mission object`); host can still use it | NOT TESTED |
| 17 | NPC tenant returns home through a door the players unlocked (both in Madison) | Relock happens on both PCs (owner = host publishes it), never twice | NOT TESTED |
| 18 | Regression: §28 steps 2-8, §27, vehicles/passenger, P2 inventory, CarBox/locker, drops, W4.1 loot | As before | NOT TESTED |
| 19 | Client's real saves after the session | Hash list identical (§3) | NOT TESTED (Local Test: client wrote only its sandbox; host's `saveData.thief` byte-identical, its own backup rotation ran) |
| 20 | Logs + performance in Madison | No `ERROR`; `WOBJ_STATS diverged=0`; note FPS with PerfStats | NOT TESTED |

## 28. W4.2 Phase A checklist — host campaign bootstrap, Player-2 spawn, doors (release `TS2-Multiplayer-0.13.0.zip` on BOTH, protocol 23)
Setup as §27 (F8 flow, two PCs). The client player saves a hash list of their real saves before the test (TESTING §3 command). Host log tags: `CAMPAIGN snapshot`, `P2SPAWN store/report/left`, `WDOOR host`, `PROPOSAL … objTxn`. Client log tags: `SEED`, `CAMPAIGN host campaign … written … verified`, `P2SPAWN <mode>`, `WDOOR proposed/region load/update`. Spawn record on the host PC: `%LOCALAPPDATA%\TS2MP\p2spawn-<profile>.dat`.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Client: before joining, note your own solo save's location/story (or load it once and quit) | — | — |
| 2 | Host loads its save, stands somewhere in Madison or the hideout, F8 → Host. Client joins from the main menu (first join for this host campaign) | Client log `SEED … save file(s) NOT copied`, `CAMPAIGN host campaign #… written … verified; spawn plan NearHost/FirstJoin`; the client loads the **host's** region, story state and world (not its own solo position/story); Player 2 stands beside Player 1 (not inside, not on a car) | Local Test (one PC, launcher, 2026-11-13): PASS for steps 2/4/6 in logs (see PROGRESS W4.2 G) — **two PCs NOT TESTED** |
| 3 | Client inventory/money after the first join | Original new-game values (money 500 / 200 free mode, empty backpack) or Player 2's existing host record — never the client's solo inventory, never Player 1's | NOT TESTED |
| 4 | Client walks somewhere else (same region), waits 5 s, quits to the main menu | Host log `P2SPAWN report …`, `P2SPAWN Player 2 left … written`; client log `P2SPAWN final position report (exit to the main menu …)` | NOT TESTED (two PCs) |
| 5 | Client clicks the original Continue while still connected (or rejoins with F8 after a restart) | Client log `CAMPAIGN back in the main menu … fresh host campaign requested` (menu return) / new join; plan `Restore/Rejoin`; Player 2 is at the spot from step 4, facing the same way | NOT TESTED |
| 6 | Host goes to the hideout. Client travels to Madison, walks a bit, quits the game (window close), restarts and joins again | Plan `Restore/Rejoin in MadisonSt`; only the client loads Madison; the host stays at the hideout; client log `P2SPAWN Restore … placed at last valid position` | Local Test variant PASS (SafePoint into Madison while host at hideout, see PROGRESS W4.2 G); **two PCs NOT TESTED** |
| 7 | Story mission (needs a story save): client disconnects (quit) during a step where saving is blocked / a police chase is active / inside a heist scene | Host log `P2SPAWN report … ctx=BlockSaving/PoliceChase…` before the quit; on rejoin plan `SafePoint/StoryContext` (free-roam region: nearest original leave point) or `OriginalSpawn/MissionScene` (heist scene: hideout spawn); never inside the mission spot | NOT TESTED |
| 8 | Client tries the original Continue / New Game in the main menu WITHOUT joining (after a session ended, same process) | Nothing happens (client log `CAMPAIGN blocked MainMenu.Continue …` / `… StartNewGame …`); F8 → Join still works | NOT TESTED |
| 9 | Doors, both in Madison: client opens an ordinary house door (quick press and hold-E) | Host sees the door swing open; host log `WDOOR … objTxn … Accepted`; client log `WDOOR proposed #…`; closing it converges again | NOT TESTED |
| 10 | Doors: host opens a door the client is looking at | Client sees it swing (client log `WDOOR update: 1 door(s) …`) | NOT TESTED |
| 11 | Doors while the host is in ANOTHER region: host at the hideout, client opens a Madison door | Host log `WDOOR`/`PROPOSAL` shows `Accepted`, `unverified id (host has no census of MadisonSt)` when the host never loaded Madison in this session; the host does not load Madison | NOT TESTED |
| 12 | Door reload: with the client still in Madison and a door open, the host travels to Madison | The host arrives with that door already open (silent restore, host log `WDOOR region load: 1 door(s) …`) | NOT TESTED |
| 13 | Door reset like vanilla: both leave Madison, one returns | The door is in its original scene state again (vanilla reload behaviour) | NOT TESTED |
| 14 | Door spam: both players press E on the same door at the same moment / rapidly | Both PCs end with the same state; no flicker loop; host log shows at most `Stale`/`Unchanged` for the loser | NOT TESTED |
| 15 | Locked door (not yet unlocked on one PC) opened on the other PC | Not forced open on the PC where it is locked (log `committed open but locked here … W4.2B`) — expected limitation | NOT TESTED |
| 16 | Regression: F8 host/join, movement, avatars, vehicles/passenger, P2 inventory, CarBox/locker, dropped items, W4.1 loot | As §27 / earlier checklists | NOT TESTED |
| 17 | Client's real saves after the session | Hash list identical to step 1's list (§3) | NOT TESTED (Local Test: the sandboxed client wrote nothing into the real folder; the host's own Continue rewrote its active save byte-identically) |
| 18 | Logs + performance | No `ERROR`; with `Debug.PerfStats = true` note `PERF … CAMPAIGN_STATS / WDOOR_STATS` and FPS in Madison | NOT TESTED |

## 27. N1.2 checklist — F8 menu, two PCs (release `TS2-Multiplayer-0.12.0.zip` on BOTH) — all NOT TESTED
Each PC: own legitimate game copy (same version). Copy the ZIP contents into the game folder, start the game normally. Host PC: router forwarding of **UDP 27960 only** (`BepInEx\plugins\TS2MP\DIRECT_CONNECT_SETUP_DE.md`). Before the test the client player saves a hash list of their real saves (TESTING §3 command). Logs: `%LOCALAPPDATA%\TS2MP\logs\<stamp>_game_<pid>\plugin.log`.
| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Both: start normally, stay in the main menu | `plugin.log`: `PLUGIN_INIT mode=F8 … (no patch, no network until F8 Host/Join)`, `SAVE_GUARD precheck ok`, `MENU ready` | NOT TESTED |
| 2 | Host in the main menu: F8 | menu opens; "Hosten starten" disabled with "Hosten ist erst möglich, wenn …" | NOT TESTED |
| 3 | Host loads a save, stands in the world, F8 | menu opens, mouse free, the player does not move/look while it is open; Join disabled with "Um einem Multiplayer-Spiel beizutreten, musst du zuerst zum Hauptmenü zurückkehren." | NOT TESTED |
| 4 | Host: "Hosten starten" (port 27960) | join code shown, "Code kopieren" works; status "Bereit – warte auf Spieler 2" after ~3 s; firewall prompt → allow (private) | NOT TESTED |
| 5 | Host: F8 again | menu closes; movement, camera and cursor exactly as before | NOT TESTED |
| 6 | Client in the main menu: F8 → public IPv4 / DynDNS, 27960, code → Verbinden | status Verbinde → Speicherschutz → Weltdaten …; log `SAVE_GUARD_ARMED`, `SAVE_GUARD_ACTIVE … verified`, `HANDSHAKE_OK client side: ClientReady sent`, `JOIN host world synchronized: loading the save through the original MainMenu.Continue()`; the game loads automatically | NOT TESTED |
| 7 | Both in the world | Player 2 visible; P2 inventory restored from the host (`P2INV` lines); host status "Spieler 2 ist verbunden" | NOT TESTED |
| 8 | Client types a wrong code (fresh game start) | error "Beitrittscode falsch …", stays in the main menu, no `SAVE_GUARD_ARMED` in the log | NOT TESTED |
| 9 | Client joins while the host is still loading its save | status "Host ist noch nicht bereit (lädt) – neuer Versuch …", joins automatically once the host stands in the world | NOT TESTED |
| 10 | Host travels to another region while P2 is connected | P2 stays connected; a join attempt during the loading screen is deferred (`JOIN_DEFERRED`) | NOT TESTED |
| 11 | Client: F8 → "Verbindung trennen", then F8 → join again (same code) | reconnects without re-arming (`isolation already armed`); P2 inventory exactly once | NOT TESTED |
| 12 | Host returns to the main menu | client logs a disconnect and keeps retrying; host log `SESSION_END (host returned to the main menu)`; host can start a new session from a loaded world (new code) | NOT TESTED |
| 13 | Kill the host game (Task Manager) while P2 plays | P2 keeps playing offline, status "Verbindung unterbrochen – neuer Versuch …"; no crash | NOT TESTED |
| 14 | Client: in a loaded world, F8 | Host disabled ("Dieses Spiel lief bereits als Spieler 2 …"), Join disabled | NOT TESTED |
| 15 | Client quits; compares the real save hash list | byte-identical (P2 used `%LOCALAPPDATA%\TS2MP\client-sandbox`) | NOT TESTED |
| 16 | Client restarts the game normally and plays solo | own save loads as before, unaffected | NOT TESTED |
| 17 | Search logs for the join code | not found | NOT TESTED |
| 18 | Solo regression (no F8): load, play, save, quit | identical to unmodded behaviour; `plugin.log` has only the init lines | NOT TESTED |

Report: rows with PASS/FAIL, both `plugin.log` files, connection type (LAN / internet IPv4 / DynDNS).

## 26. N1 checklist — direct connect between two PCs (release ZIP 0.11.1 / protocol 21 on BOTH) — SUPERSEDED by §27 (launcher flow replaced by F8), all NOT TESTED
Needs two PCs, each with its own legitimate copy of the game (same version). Host PC: router forwarding of **UDP 27960 only** to the host PC (see `docs/DIRECT_CONNECT_SETUP_DE.md`; no UPnP, no DMZ, no VPN). Use `build\release\TS2-Multiplayer-0.11.1-Windows.zip` on both PCs; extract to a normal folder. Logs: `%LOCALAPPDATA%\TS2MP\logs\` on each PC (`launcher.log`, `<stamp>_<role>_<id>\plugin.log`). The join code must not appear in any log — check row 12.
| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Both PCs: launcher → 3 Install | "Install: N file(s) added … all hash-verified"; menu line "Mod: installed, version 0.11.1, all 24 files verified" | NOT TESTED |
| 2 | Host PC: launcher → 1 Host Game | game starts; window shows port 27960 and a join code; Windows firewall prompt → allow for private networks; host `plugin.log`: `NET_STARTED role=Host bind=0.0.0.0 localPort=27960 auth=join-code` | NOT TESTED |
| 3 | Same LAN first (optional): client PC → 2 Join Game with the host's **LAN** address | client `plugin.log`: `AUTH_RESPONSE sent` → `HANDSHAKE_OK client side … (host proved the join code)`; host: `AUTH_OK`, `HANDSHAKE_OK host side … join code verified`; Player 2 visible in game | NOT TESTED |
| 4 | Internet: client PC (other network, e.g. phone hotspot) → Join Game with the host's **public IPv4** (or DynDNS name) | same as row 3 | NOT TESTED |
| 5 | Play a few minutes (walk, drive, one loot pickup in Madison) | behaves as in the local tests; `LATENCY … ms` lines every 30 s in both logs (note the values) | NOT TESTED |
| 6 | Client PC: Join with a wrong code (change one character → launcher refuses the typo; use a code from an old host session instead) | client `CONNECT_FAILED_FATAL: host rejected the connection with AuthFailed`, no retry; host `AUTH_FAILED`; host session unaffected | NOT TESTED |
| 7 | Third PC (or a second client instance elsewhere) tries to join while Player 2 is connected | refused `SessionFull`; Player 2 stays connected | NOT TESTED |
| 8 | Client PC: close the game, Join again with the same code | reconnects (`AUTH_OK` again); Player-2 inventory restored as before | NOT TESTED |
| 9 | Pull the client's network for ~30 s, then restore | `DISCONNECT … Timeout`, then automatic reconnect while the host runs | NOT TESTED |
| 10 | Host closes and starts Host Game again (new code); client keeps running | client stops with `AuthFailed` (old code); Join Game with the new code works | NOT TESTED |
| 11 | Client PC: its own single-player saves (`%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2`) | byte-identical after the session (client used `%LOCALAPPDATA%\TS2MP\client-sandbox`); a verified backup was made before Join | NOT TESTED |
| 12 | Search both PCs' `logs` folders and `TS2-Multiplayer-Launcher.ini` for the join code | not found | NOT TESTED |
| 13 | Both PCs: launcher → 5 Uninstall | only mod files removed (backup listed); game starts normally in single player afterwards | NOT TESTED |
| 14 (if hosting fails from outside) | Compare the router's WAN IPv4 with a "what is my IP" page | different / 100.64.x.x / DS-Lite → CGNAT, see setup guide §4 (not a mod defect) | NOT TESTED |

Report: rows with PASS/FAIL, `LATENCY` values, both `plugin.log` files, `launcher.log`, router model and whether the connection type was LAN, internet IPv4 or DynDNS.

## 25. M5 W4.1 Phase B checklist — shared world loot (plugin 0.11.0 / protocol 20 on BOTH) — all NOT TESTED
Backup: `%LOCALAPPDATA%\TS2MP\backups\pre-0.11.0-20261112-174224`. Region: **Madison Street** only. Supported = ordinary small valuables (not heavy, not money, not story/contract/parcel items, not inside random file cabinets, not objects that share their name with another object). Unsupported loot behaves as before (unsynchronized) — host log `WLOOT '<name>' (item N) not shared: <reason>` tells which. Host log at Madison load: `WORLD_LOOT_FLAGS MadisonSt objects=… flagFree=…`.
| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Both players enter the same Madison house | both see the same loot | NOT TESTED |
| 2 | P1 takes one supported item (log `WLOOT Player 1 took '<name>'`) | original pickup for P1 (+1); on P2's screen it disappears within a moment (client log `WLOOT removed '<name>' … taken`) | NOT TESTED |
| 3 | P2 takes a different supported item (client `WLOOT pickup #N … requested` → `original pickup running`; host `P2INV world pickup #N committed`) | original pickup animation/sound/popup for P2 (+1, after ~one round trip); it disappears on P1's screen | NOT TESTED |
| 4 | Both open the inventory | each has only their own item, exactly once; nothing duplicated | NOT TESTED |
| 5 | Both leave Madison and come back once | both taken items stay gone for both | NOT TESTED |
| 6 | P2 quits to the menu and reconnects into Madison | the two items stay gone; P2 still has its item exactly once | NOT TESTED |
| 7 (optional, disposable profile only) | After a fresh normal save: P2 takes a supported item, host quits to the main menu WITHOUT saving, reload the same save, both go to that house | P2 still has the item; the object is NOT there again (host log `P2INV replay of 1 world take(s)`) | NOT TESTED |

## 24. M5 W3.4 checklist — storage durability (plugin 0.10.4 / protocol 19 on BOTH) — all NOT TESTED
**Status (user report 2026-11-12):** positive partial evidence (shared storage, real CarBox, Player-2 transactions, journal recovery after a host restart); step 5 (unsaved-exit recovery) not conclusively tested; development gate waived by the user.
Backup: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.4-20261112-041838`. **Steps 4–5 only on a disposable profile:** in the main menu pick an unused profile slot, start a new game, play a few minutes, make one normal save. Use cheap items (e.g. a watch). **Never quit without saving on your real profile.**
Counting: for the test item X, total = P1 backpack + CarBox + locker (host: open each) + P2 backpack (client). Note the total before each step; it must be the same after. Host log: `P2INV transfer #…` lines name `recoverable` counts and `unsaved Player-1 deposit journaled first`; `P2INV_STATS … prerequisites N`.
| # | Step | Expected | Result |
|---|---|---|---|
| 1 | P1 deposits X into the CarBox, P2 withdraws it, P2 deposits it back, P1 withdraws it | each move once, totals unchanged | NOT TESTED |
| 2 | Same with the hideout locker | as 1 | NOT TESTED |
| 3 | P2 quits to the menu and reconnects | P2 backpack restored exactly | NOT TESTED |
| 4 | (disposable) P1 deposits X, P2 withdraws it, host saves normally (sleep / travel / save), host quits, reload | P1 0, storage 0, P2 has X; host log: no `replay` of these entries | NOT TESTED |
| 5 | (disposable) after a fresh normal save: P1 deposits X, P2 withdraws it (log `unsaved Player-1 deposit journaled first`), host quits to the main menu WITHOUT saving, reload the same save | exactly one X, in P2's backpack; P1 backpack without it; storage without it. For the strongest test, close the host game completely (not only the menu) before reloading; host log then shows `replay of N journal entr(ies) in commit order` | NOT TESTED |
| 6 | One dropped-item pickup each way, one car travel together | unchanged behaviour | NOT TESTED |

## 23. M5 W3.3 checklist (plugin 0.10.3 / protocol 19 on BOTH) — all NOT TESTED
Backup: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.3-20261112-035156`. W3.2 results (user): A, B, C, D, locker, P2 restart = PASS; E (chest lamp) = FAIL (magenta triangle) → removed.
| # | Step | Expected | Result |
|---|---|---|---|
| A | Each player owns a flashlight, toggles it ON/OFF several times; look at the other player from front and side | no flashlight/triangle/glow on the remote character in any state | NOT TESTED |
| B | Each player's own flashlight | lights the own surroundings exactly as before | NOT TESTED |
| C | Remote crowbar: hold, walk, swing once | same as 0.10.2 | NOT TESTED |
| D | One drop + pickup each way, one locker transfer, one region change / car entry | unchanged; no leftover objects on the remote character after the transition | NOT TESTED |

## 22. M5 W3.2 checklist (plugin 0.10.2 / protocol 19 on BOTH) — all NOT TESTED
Backup: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.2-20261112-031056`. For G set `Debug.PerfStats = true` in `BepInEx\config\com.ts2mp.multiplayer.cfg`. Logs to send: both `plugin.log` (`WITEM_DIAG`, `WITEM_STATS`, `AVATAR_GEAR`, `PERF`).
| # | Step | Expected | Result |
|---|---|---|---|
| A | P1 drops an item, keeps the menu open ~5 s, closes it | P2 sees it at once on the floor below the drop point; after P1 closes, both see it at the same rest spot (`WITEM_DIAG … settled`, client `updated (settled…)`) | NOT TESTED |
| B | P2 drops an item (same), P1 picks it up | as A in the other direction; P1's backpack +1 | NOT TESTED |
| C | Press Drop and stay in the inventory | backpack −1 at once; the other player sees the item at once; the dropper's own item falls when the menu closes (original pause) | NOT TESTED |
| D | Walk around / over a dropped item on both screens; drop one onto a table | never hanging in the air, no invisible obstacle; on the table both see it on the table | NOT TESTED |
| E | Remote flashlight ON / OFF, front and side | small police torch on the left chest, lens forward; ON = only the lens glows; OFF = dark lens; no white triangle | NOT TESTED |
| F | Remote crowbar: idle, walk, run, crouch, front/side/back; swing once | shaft runs through the palm with its middle at the hand, not hanging below; swing as in 0.10.1 | NOT TESTED |
| G | One locker transfer, one region change, P2 inventory check; compare frame times | unchanged behaviour; `PERF` lines on host and client | NOT TESTED |

## 21. M5 W3.1 hotfix checklist (plugin 0.10.1 / protocol 18 on BOTH) — all NOT TESTED
Backup: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.1-20261112-023459`. Use cheap items. Set `Debug.PerfStats = true` for G. Log lines: `WITEM host dropped item … as #…`, `WITEM drop #… requested`, `WITEM pickup …`, `WITEM drop refused before the original ran …`, `AVATAR_GEAR held tool … gripped`, `AVATAR_GEAR chest lamp from …`; any `WITEM original drop … produced no capturable object` = FAIL.

| # | Test | Expected | Result |
|---|---|---|---|
| A | P1 drops a cheap item | lands on the ground for both; P2 picks it up with E: P2 +1, P1 −1 once, gone for both | NOT TESTED |
| B | P2 drops (take an item out of the locker first), P1 picks up | same in reverse | NOT TESTED |
| C | P1 holds the crowbar: look from front, side, behind; walk, crouch; swing (left click) | crowbar held in the hand pointing forward-down, follows the arm; one arm swing per click; nothing else happens on P2's side | NOT TESTED |
| D | Both toggle the flashlight | the other player sees a small lamp on the chest, glowing when on, dark when off; no extra light on walls/floor | NOT TESTED |
| E | P2 deposits one item into the locker and withdraws it | as in 0.10.0 | NOT TESTED |
| F | Restart P2, rejoin | inventory and locker unchanged; no leftover crowbar/lamp/dropped-item ghosts | NOT TESTED |
| G | Both walking, crowbar held, both lamps on | PERF lines of host and client (avg FPS, 1 % low, P95/P99, GC) | NOT TESTED |

## 20. M5 W3 manual checklist — shared locker, dropped items, held/carried objects, flashlight (plugin 0.10.0 / protocol 17 on BOTH) — all NOT TESTED
Backup made before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.0-20261112-002336`. For H set `Debug.PerfStats = true` in `BepInEx\config\com.ts2mp.multiplayer.cfg`. Both logs must show `CARBOX shared car storage + hideout locker ready` and `WITEM shared dropped items ready`. Useful log lines: `CARBOX … locker …`, `P2INV transfer #… (locker)`, `WITEM drop/pickup …`, `P2INV item txn #…`, `P2INV host journal seq …`, `AVATAR_GEAR …`, `WITEM_STATS`, `CARBOX_STATS`, `SYNC_STATS … gear …`.

| # | Test | Expected | Result |
|---|---|---|---|
| A1 | Both in the hideout. P1 puts an item into the locker, closes it. P2 opens the locker | P2 sees the item with the same count; takes it out; P2 inventory +1, locker −1 | NOT TESTED |
| A2 | Reverse: P2 deposits, P1 withdraws | counts match on both; nobody keeps a copy | NOT TESTED |
| A3 | P1 has the locker open, P2 presses E on it (and reverse) | nothing opens for the second player (no text); opens after the first closes | NOT TESTED |
| A4 | P2 deposits, P2 restarts its game and rejoins, opens the locker | item still in the locker, not back in P2's backpack | NOT TESTED |
| B1 | Same region (e.g. Madison). P1 drops an item from the inventory | P2 sees it at the same spot (after it lands); P2 picks it up with E: P2 +1, P1 does not have it, gone for both | NOT TESTED |
| B2 | Reverse: P2 drops, P1 picks up | same | NOT TESTED |
| B3 | Each player picks up their own drop | works, count back | NOT TESTED |
| B4 | Both press E on the same dropped item at the same moment | exactly one gets it, the other gets nothing | NOT TESTED |
| B5 | P2 drops an item, P1 leaves the region and comes back | item still there for both; then both leave → item gone (original: drops are not saved) | NOT TESTED |
| B6 | Hillville: P2 drops an item, P2 travels away (save) | the item is in the HOST locker at the hideout (original Hillville conversion), not lost, not duplicated | NOT TESTED |
| C1 | P1 holds crowbar / baton / phone / brick (switch between them) | P2 sees the matching original model in P1's right hand; previous model disappears | NOT TESTED |
| C2 | Reverse (P2 holds tools) | same on P1 | NOT TESTED |
| C3 | Walk, run, crouch while holding; enter a car as driver / passenger | tool stays in the hand while on foot; hidden while seated; back after exit | NOT TESTED |
| D1 | Carry a heavy object (e.g. TV), walk and stand | other player sees the CarryRun pose and the object in front of the carrier; known: the observer's own copy of that world object stays at its place (W4) | NOT TESTED |
| E1 | P1 toggles the flashlight on/off | P2 sees P1's beam on/off, pointing where P1 looks (also up/down) | NOT TESTED |
| E2 | Reverse (P2) | same on P1 | NOT TESTED |
| E3 | Both flashlights on, walk/crouch; one player enters a car | both beams correct; the beam turns off in the car (original rule) | NOT TESTED |
| E4 | NPC near the local player while the other player's beam shines on them | no extra detection caused by the remote beam (NPCs still react only to the local player) | NOT TESTED |
| F1 | Normal travel, return to the hideout, check locker + both inventories; restart P2 and rejoin | confirmed items in the right inventory/storage, nothing duplicated | NOT TESTED |
| G1 | Regression: driving, passenger seat, joint travel, arrest → Load, CarBox, sleep | unchanged | NOT TESTED |
| H1 | PerfStats on both: walking, both flashlights on, several dropped items, held tools, locker use, travel | note avg FPS / 1 % low / P95 / P99 / GC per instance from the PERF lines; compare flashlight off vs both on | NOT TESTED |

Send back both `plugin.log` files (host + client) and the PERF lines of H1.

## 19. M5 hotfix 0.9.1 — caught-screen Load / Continue spawn (plugin 0.9.1 on BOTH) — all NOT TESTED
Back up saves first. Client log lines: `CAUGHT …`, `LOAD GameManager.LoadCheckpoint …`, `ARRIVAL_DIAG load without travel grant (…)`, `ARRIVAL MadisonSt (load without travel): placed near …`; any `FALL_RECOVERY` means the guard had to rescue (note it); `FALL_RECOVERY gave up` = FAIL.

| # | Test | Expected | Result |
|---|---|---|---|
| L1 | P2 drives the Ranger in Madison, gets caught by the police, chooses Load | loading screen, P2 stands on the street near the leave point / the car / P1 — never falls; P1 unaffected | NOT TESTED |
| L2 | Same with P2 on foot (P1 holds the car) | same | NOT TESTED |
| L3 | After L1: P2 to main menu → Continue | P2 placed on foot in Madison, no fall | NOT TESTED |
| L4 | Close P2's game and restart it (host stays) | P2 loads playable (hideout or placed on foot in Madison/Hillville) | NOT TESTED |
| L5 | After L1–L4: P2 inventory/money, CarBox, driving the Ranger again, passenger seat, travel | as before the arrest; no stuck seat / lease | NOT TESTED |
| L6 | Hillville: repeat L1 | same as Madison | NOT TESTED |
| L7 | Normal play 10 min incl. jumping off a roof | no `FALL_RECOVERY` lines | NOT TESTED |

## 18. M5 W1 + W2 manual checklist — world identity census + world-key mirror (plugin 0.9.0 / protocol 16 on BOTH) — all NOT TESTED
**Before testing:** back up `%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2` and `%LOCALAPPDATA%\TS2MP` (a pre-deployment copy exists: `%LOCALAPPDATA%\TS2MP\backups\pre-0.9.0-20261111-200632`). For section H set `Debug.PerfStats = true` in `BepInEx\config\com.ts2mp.multiplayer.cfg` (one file serves both instances; the default is false). Both logs must show `MIRROR ready (host…` / `(client…` and `CENSUS ready: 25/25 kinds`. Client log on connect: `MIRROR_SNAPSHOT received … keys=N`; host: `MIRROR_SNAPSHOT sent … bytes=…`. Any `ERROR`, `MIRROR_SNAPSHOT rejected`, `MIRROR_AUDIT` (missing writer hook) or `stable=NO` = note it.

| # | Test | Expected | Result |
|---|---|---|---|
| A1 | Start host + client with the launcher, load the game on both | both load and connect; movement, camera and the other player's avatar as in 0.8.0 | NOT TESTED |
| A2 | Walk around 1 min each | no new stutter; logs show no per-frame `MIRROR`/`CENSUS` lines | NOT TESTED |
| B1 | Load Hideout, Madison Street, Hillville (on both instances) | per scene one `WORLD_CENSUS <region> … time=…ms` line per instance; note `objects`, `dupPaths`, `hashCollisions`, `lootSharedNames`, `time`, `digest`; no visible hitch beyond the normal loading | NOT TESTED |
| B2 | Compare the `digest=` of the same region in the host log and the client log | identical digest for the same region scene (and for `MainScene`) | NOT TESTED |
| B3 | Travel away and back to the same region | second census of that region logs `stable=yes` | NOT TESTED |
| B4 | Check `WORLD_CENSUS_DIAG` lines | list of duplicate paths / collisions (if any); these ids are ambiguous by design — report the count | NOT TESTED |
| C1 | Host and client in Madison. **Host** picks a cheap loose loot item (not a story/unique item); note its name | host log: `MIRROR_DELTA sent … changes=1`; client log `MIRROR_DELTA applied`. The item may still be visible on the client right now (live object sync = W3) | NOT TESTED |
| C2 | Client travels away and back into Madison (individually) | client log `MIRROR_APPLY scene load …`; the item the host picked is **gone** on the client | NOT TESTED |
| C3 | Host returns to the main menu **without saving**, presses Continue, goes to Madison; client re-enters Madison | host log `MIRROR_BASE … gen=N+1`, client `MIRROR_SNAPSHOT received … (new host history)`; the item is back on **both** (original: unsaved change lost) | NOT TESTED |
| C4 | Repeat C1, then the host saves (travel or quicksave), restart both games, enter Madison | item stays gone on both until its respawn day | NOT TESTED |
| C5 | **Client** picks a cheap loot item, travels away and back | client log `MIRROR local loot marker …`; item stays gone for the client (no re-pick); the host still sees it (expected until W3) | NOT TESTED |
| C6 | Sleep through midnight (or wait) in the host's region | host `MIRROR_DELTA` with the new day count; client `MIRROR day count a -> b (host)`; no repeated jumps | NOT TESTED |
| D1 | Give P2 a disposable item and some money; do C1–C3 and travel between regions | P2 keeps its own items and money (never P1's backpack); P1 inventory unchanged | NOT TESTED |
| E1 | Both use the CarBox (exclusive lock); transfer a disposable item; restart both | storage and inventories correct after restart; mirror never logs `Storage_`/`Locker_` keys | NOT TESTED |
| F1 | Host drives, client passenger; client drives, host passenger; joint travel; individual travel; driver handoff | as in 0.8.0; cameras correct for driver and passenger | NOT TESTED |
| G1 | Players in different regions: clocks independent; same region: native sleep time-lapse | as in 0.8.0; no extra time jumps; `CALENDAR` lines as before | NOT TESTED |
| H1 | With `Debug.PerfStats = true`: 60 s each — standing in the Hideout, walking Madison, driving the Ranger, both in one car, players in different regions, joint travel, returning to a visited region. Note which window had focus | `PERF frames= avgFps= p95= low1%=` per 30 s window on each instance; `mod n= avg= max=` (TS2MP main-thread time only); `gc0+`, `heap`, `net out/in` (steady state should be ≈ 0.8.0 rates) | NOT TESTED |
| H2 | A/B: set `World.IdentityCensus = false` and `World.KeyMirror = false`, repeat two H1 scenes with the same window focus/graphics | compare avg FPS / P95: investigate if > ~5 % FPS or > ~10 % P95 worse with the features on | NOT TESTED |

Unfocused windows are throttled by the game/OS: compare host-with-host and client-with-client under the same focus (`focus=` is logged in every PERF line). Send back both `plugin.log` files (`%LOCALAPPDATA%\TS2MP\logs\<stamp>_host|client_*`).

## 17. M4.3 Phase B manual checklist — joint vehicle travel (plugin 0.8.0 / protocol 15 on BOTH) — all NOT TESTED
**Before testing:** back up `%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2` and `%LOCALAPPDATA%\TS2MP`; use a throwaway host profile. Both instances must show `VEHICLE_INIT … jointTravel=5 destinations, passengerMenuGuard=on` (a 0.7.1 instance is refused at the handshake). Host log lines: `TRAVEL JOINT trip#N … authorized`, `… Authorized -> Travelling`, `… Travelling -> Arrived`, `… Committed after X s`. Passenger instance: `JOINT … holds`, `JOINT … starting the original Select…`, `JOINT … passenger arrived … s after departure`, `JOINT … passenger re-seated … s after arrival`. Any `JOINT … forward recovery` in a normal run = FAIL (note what happened). Madison/Hillville seat the driver on arrival (original); Hideout/Auto Shop/Verwood put the driver on foot at the garage (original), so there the passenger arrives on foot too.

| # | Test | Expected | Result |
|---|---|---|---|
| A1 | P1 drives the Ranger, P2 on the rear bench; P1 drives to a map border and picks Hillville | P2 does not get a travel menu; both games show the original loading screen; nobody must press anything on P2 | NOT TESTED |
| A2 | Watch both windows until both finished loading | one Ranger at the Hillville arrival point; P1 at the wheel (original arrival), P2 back on the rear bench in first person (or TPP if P1 is in TPP); P2 never walks around, never under/through the car, no duplicate car or character | NOT TESTED |
| A3 | Drive off at once, brake, steer; then exit and re-enter (P1 driver, P2 passenger) | normal driving on P1, smooth car on P2, passenger exit beside the seat, re-entry with E works | NOT TESTED |
| A4 | Continue travelling together at once (Hillville → Madison) | second joint journey works the same | NOT TESTED |
| B1 | Reverse roles: P2 drives, P1 rides; P2 picks the destination | as A1–A2 with roles swapped; host log trip `P2 drives, P1 rides`; car simulated by P2 (`VEHICLE … now simulated here` on the client only) | NOT TESTED |
| B2 | After arrival: P2 drives, brakes, exits | P2 remains the physics authority (P1 sees the car follow smoothly, no jump to the host), P1 can exit beside the seat | NOT TESTED |
| C | Routes: Hideout → Madison, Madison → Hillville, Hillville → Hideout, reverse where the original menu offers them | each arrives together; at the Hideout both arrive on foot by the garage (original driver behaviour) and the car is in the garage | NOT TESTED |
| C2 | Shadowhawk GT (if bought): P2 at `Associate Sit`, travel Hideout → Madison | P2 re-seated in the front passenger seat | NOT TESTED |
| D | Switch FPS/TPP before travelling, check after arrival; switch again | passenger camera follows the driver's mode after arrival; correct seats, no duplicate model, no roof clipping; switching keeps working | NOT TESTED |
| E | Give P2 a disposable item, travel jointly, check P2's inventory; open the CarBox after arrival (P1 and P2 one after the other); repeat after returning to the Hideout | item still with P2 (not reset, not P1's items); CarBox contents unchanged and exclusive | NOT TESTED |
| E2 | P2 keeps the CarBox open while P1 picks a destination | nothing happens (menu stays open, no message); after P2 closes it, the click works | NOT TESTED |
| F | Regression: driver alone with the car; on-foot travel; players in different regions travelling independently; hideout travel proxy (E on a car that is elsewhere) | all as in 0.7.1 | NOT TESTED |
| G1 | (backed-up save) Close the passenger's game while it is on the loading screen | driver arrives and drives normally, no stuck seat; host log `JOINT … Failed … disconnected`; passenger reconnects later on foot | NOT TESTED |
| G2 | (backed-up save) Close the driver's game (P2 driving) during loading | host passenger arrives on foot at the destination after a short wait, controls work; the car stays at the origin (or at the destination if P2 had already arrived), nobody drives it; P2 reconnects normally | NOT TESTED |
| H | General regression: doors (incl. slow), wheels, collision, handoff, passenger entry prompt, avatars, camera modes, P2 inventory persistence, CarBox transfers, sleep time-lapse, region clocks, launcher, FPS | as in 0.7.1 | NOT TESTED |

Collect `BepInEx\LogOutput.log` of both instances and the TS2MP plugin logs (`%LOCALAPPDATA%\TS2MP\logs`). `VEHICLE_STATS` shows `joint holds/departures/…/seated/onFoot/recovered`, `loadMax`, `readyMax` and (host) `trips started/committed/cancelled/failed/refused`.

## 16. M4.3.3 hotfix manual checklist (plugin 0.7.1 / protocol 14 on BOTH) — all NOT TESTED
**Before testing:** back up `%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2` and `%LOCALAPPDATA%\TS2MP`; disposable items only. Do **not** use a separate save on the client. Log lines to look for: client `P2INV state … -> Ready (restore acknowledged …)`, host `P2INV client synchronized`, `P2INV report committed`, `P2INV_STATS` (client: `state Ready … committed N`; host: `reports N … syncWrites N`). Any `P2INV_FAIL` = FAIL.

| # | Test | Expected | Result |
|---|---|---|---|
| A | Start host + client, join | client log: `P2INV Player-2 inventory restored`, `state Acknowledging -> Ready`; host: `P2INV client synchronized`; first time: P2 has an empty backpack and the start money (host record) | NOT TESTED |
| B1 | P1 sits at the wheel; P2 opens the blue CarBox on foot | original storage UI opens for P2; P1 cannot open meanwhile; closes → lock free | NOT TESTED |
| B2 | Same as rear passenger (and as driver through the original interaction) | as B1 | NOT TESTED |
| C | P2 buys a flashlight, immediately returns to the main menu (no save), rejoins the running host | flashlight still with P2, money reduced once, P1's inventory unchanged; host log `report committed` with the item | NOT TESTED |
| D | Close both games normally, restart, rejoin | P2 keeps the flashlight and the money | NOT TESTED |
| E | Deposit one disposable item as P2, close the UI, restart both; then withdraw it, restart again | item exactly once: in the CarBox after the deposit, with P2 after the withdrawal | NOT TESTED |
| F | P1 saves with an original save action (travel / quicksave) | P1's world progress saved as before; P2's committed inventory unchanged after restart | NOT TESTED |
| G | Regression: passenger entry/exit, native prompt, both camera modes, doors, driving + handoff, exclusive CarBox, region time, sleep (M4.3.3, still unverified), save isolation, FPS + network stability | as in 0.7.0 / 0.6.3 | NOT TESTED |

## 15. M4.3.3 manual checklist (plugin 0.7.0 / protocol 13 on BOTH) — user run 2026-11-11: FAILED (P2 never restored, CarBox blocked, purchase lost) → §16
**Before testing:** back up the host save folder (`%USERPROFILE%\AppData\LocalLow\CookieDev\Thief Simulator 2`) and `%LOCALAPPDATA%\TS2MP` (fleet + p2inv files); use cheap disposable items. Logs: `P2INV …`, `P2INV_STATS`, `CARBOX transfer …`, `CARBOX_STATS`, `SLEEP_SHOW …`.
First connection: Player 2 starts with an **empty backpack and $500** (free mode $200) — expected (no copy of Player 1 any more).

| # | Test | Expected | Result |
|---|---|---|---|
| A1 | P2 picks up / buys a cheap item; wait 3 s; quit both normally; restart both, reconnect | P2 still has it; P1 did not receive it; P2 money as before the quit | NOT TESTED |
| A2 | P2 uses a consumable, sells something at a shop, deposits money at Darkbank; restart | counts, money and bank balance as left | NOT TESTED |
| A3 | P1 starts a NEW game in the same profile, P2 reconnects | P2 starts fresh ($500, empty) — old P2 file kept as `p2inv-<p>.stale-…` | NOT TESTED |
| B1 | P2 opens the CarBox (on foot / rear seat / as driver), deposits a disposable item, closes; P1 opens | item exactly once in the CarBox; gone from P2 | NOT TESTED |
| C1 | P1 deposits an item; P2 opens and withdraws it; restart both | P2 has it once; CarBox lost it once; persists after restart | NOT TESTED |
| C2 | P2 withdraws, then the host quits WITHOUT saving (task kill) right after; restart | the item is either back in the CarBox and not with P2, or with P2 and not in the CarBox — never both, never neither | NOT TESTED |
| D1 | P1 opens CarBox; P2 tries; P1 closes; P2 opens (and reverse) | one user at a time, no text | NOT TESTED |
| D2 | P2 moves several items quickly in the open CarBox | each move once; UI correct after each | NOT TESTED |
| E1 | P2 sells its own items via Blackbay | money +, items − on P2; persists | NOT TESTED |
| E2 | P1 stores an item in the CarBox; P2 tries to sell that item type via Blackbay without having it | sale refused (button does nothing) | NOT TESTED |
| E3 | P2 opens the hideout locker | empty for P2, nothing can be stored there (host storage, not shared yet) | NOT TESTED |
| F1 | P1 sleeps (bed or car) with P2 in the same region (on foot / in the car) | P2 sees the original sleep presentation (sleep camera, fast sky, ticking UI) and ends at the same time | NOT TESTED |
| G1 | P1 drives the Ranger (stopped), P2 on the rear bench sleeps | P1 sees the original sleep presentation; both end at the same time; seats, car and camera mode unchanged; P1 cannot drive during it | NOT TESTED |
| G2 | Roles reversed (P2 drives, P1 rear bench sleeps) | identical | NOT TESTED |
| G3 | The sleeper stops early (Esc) | the other presentation stops at the same time | NOT TESTED |
| H1 | Players in different regions; one sleeps | the other sees nothing; independent region clocks as before | NOT TESTED |
| I1 | Regression: launcher, movement, handoff, passenger entry/exit/prompt, third-person body, doors + slow doors, wheels, collision, fuel, travel, clocks, CarBox lock; FPS on both | as before | NOT TESTED |

## 14. M4.3.2b manual checklist (plugin 0.6.3 / protocol 12 on BOTH) — all NOT TESTED
Back up the host save folder first; use disposable items. Logs: `CARBOX …` lines and `CARBOX_STATS`, `VEHICLE_STATS … entryPromptShown`.

| # | Test | Expected | Result |
|---|---|---|---|
| A1 | P1 drives the Ranger (stopped), P2 walks around it (any side, looking anywhere) | the original "enter car" prompt is visible the whole time E would enter; leaving ~1.3 m / walking behind a wall / car starting to move hides it at once; no stale door/lockpick prompt appears with it | NOT TESTED |
| A2 | With the prompt visible press E once | seated as passenger; prompt gone | NOT TESTED |
| A3 | Look at the open door / tailgate / CarBox / medkit near the car | their own original prompt, never the passenger prompt; E does only that action | NOT TESTED |
| A4 | Roles reversed | identical | NOT TESTED |
| C1 | Host drives; client opens the CarBox on foot from its usual spot | original storage UI with the host's contents | NOT TESTED |
| C2 | Client drives; host opens the CarBox on foot | original UI, host's contents | NOT TESTED |
| D1 | Host drives, client on the rear bench: look at the CarBox, E | storage opens, client stays seated; closing does not exit | NOT TESTED |
| D2 | Client drives, host on the rear bench: same | same | NOT TESTED |
| E1 | P1 has the CarBox open; P2 tries (on foot / seated) | nothing opens on P2, no text | NOT TESTED |
| E2 | P1 closes; P2 opens | opens | NOT TESTED |
| E3 | P1 at the Ranger, P2 at the Shadowhawk (hideout) at the same time | only one storage UI at a time | NOT TESTED |
| F1 | Host moves a disposable item into / out of the CarBox; client opens it afterwards | client sees exactly the host's new counts | NOT TESTED |
| F2 | Client tries to move an item in either direction | nothing moves (blocked by design until the Player-2 inventory milestone); counts unchanged on both | NOT TESTED |
| G1 | Client has the CarBox open, then disconnects / quits; host opens | host can open immediately | NOT TESTED |
| G2 | Host saves, restarts both instances; reopen | host's storage counts persisted by the original save; client shows them | NOT TESTED |
| H1 | Regression: driver entry, passenger entry/exit (left rear), doors + slow doors, tailgate, camera modes, own body in third person, handoff, collision, travel, clocks, FPS | as before | NOT TESTED |

## 13. M4.3.2a manual checklist (plugin 0.6.2 / protocol 11 on BOTH) — all NOT TESTED
Both instances on 0.6.2. Logs: `PASSENGER entry by E` (reach, feet above the car's ground), `PASSENGER getting out … (beside the seat …, no door animated)`, `FEEDBACK (log only …)`, `PASSENGER the game re-enabled interaction while seated`, `VEHICLE_STATS … exitBlocked canInteractKept logOnlyFeedback seat+foot avg`.

| # | Test | Expected | Result |
|---|---|---|---|
| U1 | Play normally on both instances (driving, travel attempts, refused seats, handoff) | no mod text anywhere on screen (no status lines, no name tag, no "Multiplayer: …"); original prompts/warnings only | NOT TESTED |
| A1 | Host drives the Ranger and stops. Client walks around the car and presses E from the left side, right side, rear, front corners, looking at the car, at the ground, at the sky, at a wall behind | every position next to the car: seated on the rear bench; no door opens | NOT TESTED |
| A2 | E from ~2 m away, through a wall/fence, from the roof, from a floor above/below | nothing, no text | NOT TESTED |
| A3 | E while aiming at the driver door, an open door, the tailgate, the storage box, the medkit | original action only, never a seat | NOT TESTED |
| A4 | Car rolling / seat taken | nothing, no text | NOT TESTED |
| A5 | Roles reversed (client drives, host rides) | identical | NOT TESTED |
| B1 | Ranger rear passenger, car stopped: E (any view direction) | stands upright on the LEFT side beside the rear bench, behind the driver door; NO door opens or closes; first-person controls back | NOT TESTED |
| B2 | Left rear side blocked (wall / other car) | right rear side; both blocked → stays seated, no text | NOT TESTED |
| B3 | E while the car is moving | stays seated, no text | NOT TESTED |
| B4 | A door opened by the other player before the exit | stays as it is | NOT TESTED |
| B5 | Shadowhawk passenger (front right seat): E | stands on the right side beside the front passenger seat; no door animated | NOT TESTED |
| B6 | Roles reversed | identical | NOT TESTED |
| M1 | Seated passenger (host and client): open TAB inventory / pause / (host) storage, close it, then aim at the medkit, a door, items and press E | E only gets out (or opens storage on the host when aiming at the box); medkit never heals, doors never move | NOT TESTED |
| M2 | Closing storage / pause with E | stays seated | NOT TESTED |
| C1 | Seated, driver toggles third person repeatedly | own character visible on the rear bench in third person, hidden in first person, no duplicate, gone after exit | NOT TESTED |
| R1 | Regression: driver entry/exit, handoff, doors + slow doors, tailgate, wheels, collision, fuel, clocks, sleep, launcher, save isolation | as before | NOT TESTED |
| P1 | `VEHICLE_STATS … update avg / seat+foot avg` while riding and standing next to the car (compare 0.6.1: 150–190 µs avg on the host passenger) | lower; FPS noted | NOT TESTED |

## 12. M4.3.1 manual checklist (plugin 0.6.1 / protocol 11 on BOTH) — all NOT TESTED
Preparation: back up the host save folder; use disposable test items for storage tests; both instances on 0.6.1; collect both `plugin.log` files afterwards (`%LOCALAPPDATA%\TS2MP\logs\<time>_host_*` and `_client_*`). Look for `PASSENGER entry by E`, `PASSENGER opened the car storage`, `PASSENGER own body shown/hidden`, `VEHICLE_STATS … footEntries … slowDoors …`.

| # | Test | Expected | Result |
|---|---|---|---|
| A1 | Host drives the Ranger and stops. Client walks to the car (left side, right side, rear, front corner), looks at the car body, presses E — without opening a door | "Enter car" prompt while looking at the body; E: front-right door opens, client sits on the rear bench, door closes | NOT TESTED |
| A2 | Same, standing ~2 m away / on a wall's other side / on the roof (if reachable) / looking away | nothing happens | NOT TESTED |
| A3 | Client looks at the driver door, the open right door, the tailgate, presses E | original door / tailgate action only, no seat request | NOT TESTED |
| A4 | Right door open (medkit tagged): client aims at the medkit and presses E (bleeding and not bleeding) | original medkit behaviour, no seat request | NOT TESTED |
| A5 | Open the door manually and use the "Enter car" trigger as in 0.6.0 | still works | NOT TESTED |
| A6 | Car rolling / both seats taken: E at the body | "wait until the car has stopped" / "both seats are taken" | NOT TESTED |
| A7 | Repeat A1–A3 with roles reversed (client drives, host rides) | identical | NOT TESTED |
| B1 | Seated passenger, 1 s after sitting down, E while looking anywhere (window, roof, floor) | gets out on a free side, camera/controls normal, seat freed on both | NOT TESTED |
| B2 | Exit next to a wall / another car | other side or behind; "no room" only if all are blocked | NOT TESTED |
| B3 | Repeat with roles reversed | identical | NOT TESTED |
| C1 | Both seated, driver toggles third person repeatedly | passenger sees its OWN character on the rear bench (Sitting) + the driver; first person: no own body in the view, no clipping | NOT TESTED |
| C2 | Passenger gets out while in third person; driver gets out while the passenger stays; leave the region | own body never stays visible after exit; no duplicate characters | NOT TESTED |
| D1 | **Host as passenger** (client drives), first person: look at the shoe box (front passenger area), press E | box highlights; original storage opens with the host's real contents; closing does not exit; host still seated | NOT TESTED |
| D2 | Host passenger moves a disposable item into / out of the storage, closes, gets out, checks the storage on foot at its own car later | contents consistent with what was done, nothing duplicated | NOT TESTED |
| D3 | **Client as passenger**: look at the shoe box, press E | message "car storage belongs to the host's save … later stage", no storage UI, still seated | NOT TESTED |
| D4 | Driver toggles the camera while the host passenger has the storage open | camera follows only after the storage is closed; no stuck camera | NOT TESTED |
| E1 | Seated passenger aims at the medkit and presses E | "medkit is out of reach from the passenger seat"; no heal, no exit | NOT TESTED |
| E2 | On foot through the open right door / as driver with engine off | original medkit works as before | NOT TESTED |
| F1 | Host holds E on the Ranger's right door until it is open; client watches (and reverse) | other player sees the slow, quiet movement (squeak), not a fast loud swing | NOT TESTED |
| F2 | Hold E and release after ~0.3 s | both see the door finish fast with the normal loud sound | NOT TESTED |
| F3 | Quick tap E (open / close) | normal speed on both, as in 0.6.0 | NOT TESTED |
| F4 | Slow-open a door, then the other player joins the region / travels in | joiner sees the door open (normal swing), no endless squeak | NOT TESTED |
| G1 | Regression: original driver entry, handoff both ways, driving, wheels, collision, fuel, travel refusals with passenger, regional clocks + sleep, launcher | as in 0.6.0 | NOT TESTED |
| G2 | Performance: note FPS on both instances while driving with a passenger and while standing next to the car; `VEHICLE_STATS … update avg/max us` | no visible drop; numbers recorded | NOT TESTED |

## 11. M4.3 Phase A manual checklist (plugin 0.6.0 / protocol 10 on BOTH) — user run 2026-11-11: two-player occupancy, both roles, camera modes, doors, collision, wheels, handoff/travel guard working (see PROGRESS)
Logs: `PASSENGER`, `SEAT_PROBE ... passenger`, `REMOTE_SEAT`, `DOOR`, `TRAVEL ... refused`, `VEHICLE_STATS ... passenger req/granted/denied/seated/exits/forced= camSwitches=`.
| # | Test | Expected | Result |
|---|---|---|---|
| 1 | Host drives the Ranger (engine on, standing). Client walks to the **front-right** door, E opens it, E on the prompt | client seated in the rear bench centre (first person), door closes by itself, both see it | NOT TESTED |
| 2 | Same with roles swapped (client drives, host rides) | symmetric | NOT TESTED |
| 3 | Both look at each other | driver in the driver seat, passenger on the rear bench (Sitting clip), no floating, no head through the roof, facing forward | NOT TESTED |
| 4 | Driver drives off, turns, brakes | passenger moves with the car without jitter, sees it from the own seat | NOT TESTED |
| 5 | Passenger presses throttle/steer/brake/horn/change_view keys | nothing happens to the car | NOT TESTED |
| 6 | Driver toggles first/third person (change_view) several times | passenger switches to the original third-person car camera / back to own first-person seat view each time | NOT TESTED |
| 7 | Passenger presses E (car standing) | gets out beside the car (door opens, short camera clip), walks normally, can interact again after ~1 s | NOT TESTED |
| 8 | Driver exits while the passenger stays; then passenger exits | driver seat empty, passenger stays seated and keeps the camera; passenger can get out; nobody becomes driver automatically | NOT TESTED |
| 9 | Repeat entry/exit 5× each role, also re-entering the driver seat with the passenger aboard | no stuck controls/camera, no duplicate avatars or cars | NOT TESTED |
| 10 | Try to get in while the car moves / when both seats are taken | "wait until the car has stopped" / "both seats are taken" | NOT TESTED |
| 11 | Walk into the moving and standing car with nobody inside / while someone rides | car solid; seated passenger never pushed out | NOT TESTED |
| 12 | Driver with passenger drives to a map border / opens the travel menu and picks a destination; passenger tries the same | travel refused with a message, menu stays open, nobody detached | NOT TESTED |
| 13 | Driver disconnects / quits while the passenger sits | passenger is put outside on foot with full controls | NOT TESTED |
| 14 | Shadowhawk (only if bought on the host): passenger uses the **left** door, front passenger seat | as 1–8 | NOT TESTED |
| 15 | Regression: driver handoff without passenger, doors, travel, region clocks, sleep, player sync | as 0.5.0 | NOT TESTED |
| 16 | FPS on both instances with both players in the car | no visible drop; `VEHICLE_STATS update avg/max` | NOT TESTED |
If the first-person passenger view is too high/low: `BepInEx\config\com.ts2mp.multiplayer.cfg` → `Vehicles.PassengerSeatAdjust = 0:0,-0.05,0` (Ranger) and send the value that looks right.

## 10. M5.0 manual checklist (plugin 0.5.0 / protocol 9 on BOTH instances) — all NOT TESTED
Log lines to send: `OBSERVED`, `VEHICLE ... presentation`, `DOOR`, `VEHICLE_STATS` (`observed=`, `pushOuts=`, `doors rep/applied/refused=`), `VEHICLE_SLOW_FRAME`, `SEAT_PROBE`, `REMOTE_SEAT`.
| # | Test | Expected | Result |
|---|---|---|---|
| 0 | **Vanilla baseline** (BepInEx off: `tools\bepinex.ps1 -Uninstall` or launch the game without the launcher, single player): Ranger and Shadowhawk — walk to the driver door, look at it, E | door swings open (hold E = slow quiet open); only then "Enter car" prompt on the opening; E seats you; door closes ~0.25 s later by itself; engine start → third person; exit opens the driver door, it stays open | NOT TESTED |
| 1 | P1 holds the parked Ranger (Madison, then Hillville, then Hideout); P2 looks at the driver door, E | door opens on P2 **and** P1 (sound on both); P2 then sees the original Enter prompt; no prompt before the door is open | NOT TESTED |
| 2 | Continue test 1: P2 presses E on the prompt | handoff granted, P2 seated via the original entry, door closes by itself on both | NOT TESTED |
| 3 | Same with roles swapped (P2 holds, P1 enters) | symmetric | NOT TESTED |
| 4 | Shadowhawk tests 1–3 (bought on the host) | symmetric | NOT TESTED |
| 5 | Empty parked remote car: walk into body, doors, bumpers | solid, no jitter, no push of the car | NOT TESTED |
| 6 | Remote driver seated, engine on, stationary: walk into it | solid | NOT TESTED |
| 7 | Remote car driving slowly towards / past the standing player | car does not pass through; player is pushed aside (`pushOuts` increases), never trapped inside | NOT TESTED |
| 8 | Remote car at normal speed passes close / hits the player | no pass-through, no teleport across the car, no money/damage change on either side | NOT TESTED |
| 9 | Player stands in the swing of a door that opens remotely | no stuck player | NOT TESTED |
| 10 | Remote wheels while driving straight / turning / reversing | wheels roll in the driving direction, front wheels steer, no mirrored or wobbling wheels | NOT TESTED |
| 11 | Observer opens a door of a moving remote car | door closes again, "the car is moving" | NOT TESTED |
| 12 | Driver exits (original exit opens the driver door) | both see the door open; P2 may close it with E | NOT TESTED |
| 13 | Door left open, car driven to another region and back | doors closed at the destination; no stale re-open | NOT TESTED |
| 14 | Late join: open a door, then start/connect the client | client shows the door open | NOT TESTED |
| 15 | Regression: launcher, connection, player replication, region travel with/without car, sleep, region clocks, fuel per car, hideout travel proxy (E → travel menu, F7), safe spawn near the car, one car only | as before (M4.2.3 results) | NOT TESTED |
| 16 | Repeated handoff P1→P2→P1 in Hideout, Madison, Hillville | each time solid, doors coherent, car keeps identity/pose/fuel | NOT TESTED |
| 17 | Performance: note FPS on both instances before/after a remote car drives nearby; check `VEHICLE_STATS update avg/max` | no visible FPS drop; avg in the tens of µs | NOT TESTED |

## 9e. M4.2.4 manual checklist (plugin 0.4.6 on both) — all NOT TESTED
Log tags: `VEHICLE_TEMPLATE … car-door colliders dropped=N`, `driver handoff requested/granted`, `entered after the handoff`. If E still fails: set `Vehicles.InteractDebug = true` in `BepInEx/config/com.ts2mp.multiplayer.cfg`, press E at the door, send the `INTERACT_RAY` lines.

| # | Test | Expected | Result |
|---|---|---|---|
| A | Host drives Ranger to Madison, client follows; host parks + exits; client walks normally to the driver door | original E prompt from a normal standing position (no jumping); one E → client drives | NOT TESTED |
| B | Reverse (client exits, host walks to the door) | same | NOT TESTED |
| C | ≥ 5 handoffs without region reload | no stuck/missing prompt, parked car stays solid | NOT TESTED |
| D | Driver inside / car rolling / hideout away proxy | no prompt while occupied; "wait until stopped" while rolling; hideout proxy opens travel | NOT TESTED |
| E | Regression: remote driver render, enter/exit animation, no teleport, travel/region rules, one car, clocks, movement, FPS | unchanged | NOT TESTED |

## 9d. M4.2.3 manual checklist (plugin 0.4.5 / protocol 8 on both) — all NOT TESTED
Log tags: `TRAVEL P1 #n -> … (driving|on foot): …`, `stays in … now simulated by P…`, `driver handoff requested/granted`, `entered after the handoff`, `INTERACT_PROBE` (E raycast evidence), `SEAT_PROBE … TppCalibrated`, `VEHICLE_SLOW_FRAME`.

| # | Test | Steps | Expected | Result |
|---|---|---|---|---|
| A | Free car in Madison | host drives Ranger to Madison, client follows; host parks, exits; wait ~2 s; client presses E on the car | client enters with one E and drives; host sees it move | NOT TESTED |
| B | Reverse | client exits; host presses E | host drives; repeat a few times | NOT TESTED |
| C | Drive away, other stays | both in Madison; host sits in the Ranger and travels to the hideout (driving, not on foot) | log `(driving)` + `Carry`; Ranger gone from Madison for the client at once; client cannot enter a stale Ranger; host arrives with it, usable; going back creates no duplicate | NOT TESTED |
| D | On foot | both in Madison; host exits the Ranger and leaves on foot | log `(on foot)` + `None`; Ranger stays parked for the client, client can take it (E); host sees only the hideout travel proxy | NOT TESTED |
| E | Reverse C | client drives away, host stays | same as C | NOT TESTED |
| F | Driver rendering | watch the other player drive the Ranger (and Shadowhawk if bought), both roles; enter/exit | head inside the cabin, seated, follows the car, walks again after exit. If off: set `Vehicles.DriverSeatAdjust` in `BepInEx/config/com.ts2mp.multiplayer.cfg`, e.g. `0:0,-0.05,0` | NOT TESTED |
| G | Collision/regression | parked remote car solid, moving one ghost, no stuck player after entry/exit; hideout proxy + E travel; one car per region; safe spawn; fuel; clocks; sleep; FPS | unchanged | NOT TESTED |

## 9c. M4.2.2 manual checklist (plugin 0.4.4 / protocol 8 on both) — user run 2026-11-10: hideout handoff, travel/follow, seated driver, parked solidity PASS; Madison E handoff FAIL; drive-away with staying player FAIL; Ranger head clipping
Throwaway host profile. Log tags: `driver handoff requested/granted/ended`, `entered after the handoff`, `take car#… -> …` (host), `REMOTE_SEAT`, `SEAT_PROBE`, `proxy created … shell colliders=… enterTriggers=…`, `stays in … now simulated by P…`, `VEHICLE_SLOW_FRAME`, `VEHICLE_STATS … handoff req/granted/denied/entered`.

| # | Test | Steps | Expected | Result |
|---|---|---|---|---|
| A | Host exits, client drives | both at the hideout; host enters Ranger, exits; wait ~2 s; client presses E on the Ranger | client enters with one E press and can drive; host sees it move; one Ranger only | NOT TESTED |
| B | Reverse | client exits; host presses E | host drives the same Ranger; both see the same car | NOT TESTED |
| B2 | Outside the hideout | repeat A/B in Madison or Hillville | same; E on the other player's parked car works | NOT TESTED |
| B3 | Occupied / moving | press E while the other player sits inside / while it still rolls | short text ("seat taken" / "wait until stopped"), no travel menu, no entry | NOT TESTED |
| C | Driver visible | host drives; client watches (then swap roles) | remote player sits in the driver seat, turns with the car, no roof/wheel clipping; standing/walking again after exit; no second model | NOT TESTED |
| D | Remote collider | other player's car parked; walk into it; then let it drive slowly past | parked: cannot walk through; moving: passes through (ghost) without pushing/damage; no money lost | NOT TESTED |
| E | Border | both in Madison; one drives to the map exit and travels; the other watches | car stays at the exit, settles normally (no floating/jump back), stays visible; travelling with the car while alone still carries it | NOT TESTED |
| F | Regression | hideout away proxy + E travel, F7, one car per region, safe spawn, return trip, region clocks, sleep, reconnect, FPS | unchanged from M4.2.1 | NOT TESTED |

Send both `plugin.log` files and note any `SEAT_PROBE` line plus how the seated avatar looked in each car (Ranger, Shadowhawk if bought).

## 9b. M4.2.1 manual checklist (hideout travel proxy; plugin 0.4.3 on both)
Throwaway host profile. Overlay line 3 shows each car's region and holder; log tags `VEHICLE … presentation`, `TRAVEL menu opened from the hideout travel proxy`, `TRAVEL grant`. Record client driving / passenger access separately — **not implemented** (expected: not possible).

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Both load into the hideout. Host takes the Ranger to Madison. Client stays | Client: Ranger stays visible (`presentation ParkedOther -> AwayProxy`); walking into it collides; E opens the original travel menu; looking at the CarBox/trunk: no storage opens; car does not move, no engine | NOT TESTED |
| 2 | Client: E on the Ranger proxy → Madison Street | Client `TRAVEL request …car=-` → `grant … Join car#1`; arrives on foot near the host/Ranger; host sees one Ranger; client hideout had no car brought along | NOT TESTED |
| 3 | Host (with client in hideout again, or staying) returns to the hideout **with** the Ranger | Client: `presentation AwayProxy -> ParkedOther`, Ranger moves to where the host parked it, no second Ranger; host: Ranger `Real`, drivable | NOT TESTED |
| 4 | Reverse: host leaves the hideout first (client alone simulates the garage cars: `now simulated here`), host comes back later; client drives the Ranger to Hillville | Host's hideout keeps a Ranger travel proxy; host E → Hillville → `Join car#1`, on foot near the client; no second Ranger | NOT TESTED |
| 5 | Fresh: both load into the hideout | Holder (first present) sees normal cars; the other sees the cars at the same positions, solid, E = travel menu (not driving); GT only if bought on the host | NOT TESTED |
| 5b | Client tries to drive / sit as passenger in a host-held car | Not possible (expected: driver handoff/passengers not implemented) | NOT TESTED |
| 6 | After a car trip, restart the client and load into the hideout again; also travel hideout → Madison → hideout | Same presentation as before the restart; no duplicate; E available | NOT TESTED |
| 7 | Regression: remote player sync, region clocks, both sleep directions, host driving, proxy motion outside the hideout, logs | Unchanged; no exceptions; `VEHICLE_STATS` (`proxyMenus`, `stale` ≈ 0 after a return, `update avg/max us`) | NOT TESTED |

## 9. M4.2 manual checklist (travel, region cars, spawn, fleet file, phone; run with plugin 0.4.3; user run 2026-11-10 found hideout bugs A–C → §9b)
Throwaway host profile (note its slot number = `fleet-<slot>.dat`). Watch overlay line 3 (`TS2MP vehicles | cars Ranger@MadisonSt(here) Shadowhawk@Hillville(P2) | travel …`). Log tags: `TRAVEL`, `ARRIVAL`, `VEHICLE`, `FUEL`, `FLEET`, `PHONE`, `VEHICLE_STATS`. Before starting, back up `%LOCALAPPDATA%\TS2MP\fleet-*.dat` if present. Hideout/Auto Shop without a car of your own: **F7** opens the original travel menu. While the host is in the hideout it simulates both garage cars; the client can drive a garage car only after the host left (no driver handoff in M4.2).

**Shadowhawk not bought?** Scenarios A–C need two cars. Without GT use the single-car variants marked *(1 car)*; do not edit saves or bypass the purchase. Buying the GT legitimately ($25,000 in the hideout purchase UI) on the **host** unlocks it for both.

| # | Scenario / steps | Expected evidence | Result |
|---|---|---|---|
| A1 | Both in the hideout. Host enters Ranger, drives out (engine → menu), Madison | Host `TRAVEL grant #n -> MadisonSt Carry car#1 (decided in place)`, arrives seated in the Ranger | NOT TESTED |
| A2 | Client (its GT copy becomes active once the host left: `VEHICLE Shadowhawk now simulated here`) drives the GT out, Hillville | Client `TRAVEL request #n … (original travel held)` then `TRAVEL grant … Carry car#2 (host decision; resuming …)`; host `TRAVEL P2 … committed` | NOT TESTED |
| A3 | Both drive around in their own regions | Both cars handle normally; host log `VEHICLE_STATS` shows `recv` growing (client GT ingested, `wrongRegion` counts while apart); no proxies on either side | NOT TESTED |
| A *(1 car)* | Host drives the Ranger to Madison. Client (hideout, no car: overlay shows `F7: travel menu`) presses F7 → Hillville | Host `Carry car#1`; client `TRAVEL grant … Hillville None`, arrives on foot at the leave point; only the host has a car | NOT TESTED |
| B1 | Continue from A: host walks to a Madison border (or drives) → Hillville | Host `TRAVEL grant … Hillville Join car#2`; host arrives **on foot** (`ARRIVAL driver seating skipped`, `placed near other player …` or `region car`) | NOT TESTED |
| B2 | Check positions | Host not inside a wall/car/the client, not on the road; client was not moved; host sees exactly one GT (proxy, moves when the client drives); overlay `Ranger@MadisonSt(parked)` | NOT TESTED |
| B *(1 car)* | After A *(1 car)*: client walks to a Hillville border → Madison (`Join car#1`, placed near the host). Then the host walks to a Madison border → Hillville (`None`: the Ranger stays with the client, client log `Ranger now simulated here`, car stays where it was). Host travels back to Madison | Host `Join car#1`, placed next to the client/Ranger; one Ranger visible on both | NOT TESTED |
| C1 | Both in different regions with their cars (A). Both open the menu and pick the **same empty** region (e.g. Auto Shop or Verwood if unlocked) as simultaneously as possible | Host log `TRAVEL P1 … Carry` and `TRAVEL P2 … Join` (or the reverse if the client was first); exactly one `Carry` | NOT TESTED |
| C2 | Arrival | One usable car in the destination; the loser arrives on foot; the loser's car stays where it was (overlay) | NOT TESTED |
| C *(1 car)* | Both walk from Madison to Hillville (Ranger stays parked in Madison, overlay `(parked)`). Both pick Madison at the same moment | Host log one `Parked car#1` and one `Join car#1`; one Ranger at its parking spot; both on foot next to it | NOT TESTED |
| D1 | Holder of a car leaves its region on foot while nobody else is there; later returns by menu | On return `TRAVEL grant … Parked car#n`, `ARRIVAL <car> restored to its parking pose`, player placed next to it; no second car | NOT TESTED |
| D2 | Leave the car somewhere memorable (not at a leave point), travel away and back | Car exactly where it was left (log pose), fuel unchanged (`FUEL … (x.x)`) | NOT TESTED |
| E1 | Client quits (Alt+F4) while in Hillville with the GT; restart client via launcher | Host `TRAVEL … rolled back` only if a travel was pending; after reconnect client log `VEHICLE Shadowhawk now simulated here` once; no extra GT | NOT TESTED |
| E2 | Client quits **during** a travel load | Host `reservation #n rolled back (P2 disconnected)`; car back in its origin; client after restart arrives without a duplicate | NOT TESTED |
| F1 | Note both cars' regions/fuel; host saves (travel or quicksave) and quits; restart both | Host `FLEET_LOAD profile=… Ranger@… Shadowhawk@…` with the same regions/fuel; file `%LOCALAPPDATA%\TS2MP\fleet-<slot>.dat` (+ `.bak`) | NOT TESTED |
| F2 | Load a **different** host profile slot | `FLEET_INIT … (no fleet record …)` or that slot's own record; the first slot's cars do not appear | NOT TESTED |
| F3 | Start a New Game in a throwaway slot that already has a fleet file | `FLEET record … ignored: save story … (kept as …stale-….dat)` | NOT TESTED |
| G1 | Travel on foot to a region with no car of yours (e.g. join B). Note money. Phone → "Fast travel to car" | Original "can't fast travel" message, `PHONE fast travel to car blocked … (no money charged)`, money unchanged | NOT TESTED |
| G2 | In a region where you hold your car, use it | Original behaviour: money charged once, teleported to the car | NOT TESTED |
| H1 | Remote player walk/run/crouch animation and visibility in a shared region | Unchanged from M2 | NOT TESTED |
| H2 | Region clocks: travel into the other player's region adopts its time; both sleep directions (TESTING §7c) | Unchanged | NOT TESTED |
| H3 | Driving feel of the original cars; proxy interpolation (smooth, ≈120 ms behind) | Unchanged / smooth | NOT TESTED |
| H4 | Hideout with a car parked elsewhere: enter its frozen copy, start the engine | Menu opens, car does not move; choosing the region where that car is → `Parked`/`Join`, never a second copy | NOT TESTED |
| H5 | Scripted transitions: get arrested; start a heist | Original behaviour, `TRAVEL … (scripted transition …)`; heist car local only (expected limitation) | NOT TESTED |
| H6 | Logs | No exceptions/`ERROR`; note `VEHICLE_STATS update avg/max us`, `sent/recv` bytes, `ARRIVAL … in x ms`, `fleetWrites`, `decisions/commits/rollbacks`; `notOwner`/`malformed` = 0 | NOT TESTED |

Send both `plugin.log` files and the `fleet-*.dat` names.

## 8. M4.1 manual checklist (host car replication, plugin 0.4.0 on both; protocol 6)
Throwaway profile. What exists: the **host's** cars are registered and the host's spawned cars are shown on the client as moving visual proxies. Nothing else (client driving, passengers, storage, collisions) is implemented.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Launch Host, then Client | Both `VEHICLE_INIT … api=ok carFields=firstCar,van`; host `VEHICLE_BIND firstCar:PLAYER CAR#1 van:MUSTANG#2(locked or not)…` | NOT TESTED |
| 2 | Both travel to Madison Street; both get out of their cars | Host `VEHICLE #1 PLAYER CAR spawned in MadisonSt`; client `VEHICLE_TEMPLATE kind=0 … ms`, `VEHICLE proxy created #1 kind=0 in MadisonSt` (+ duplicate WARNING if the client also drives the Ranger) | NOT TESTED |
| 3 | Identify the synchronized car | Client sees the host's car where the host parked it (a second Ranger if the client's own car is also a Ranger) | NOT TESTED |
| 4 | Host drives around | Host `VEHICLE #1 driver lease P1 -> Accepted`; client sees the proxy move and turn smoothly (≈ 120 ms behind), no jitter/teleporting | NOT TESTED |
| 5 | Host parks and exits | Host `driver released`; proxy stops at the same spot on the client | NOT TESTED |
| 6 | Client walks into the proxy | Passes through (no collider, by design) | NOT TESTED |
| 7 | Host travels to Hillville and back; client stays | Client `VEHICLE proxy removed #1 (… another region …)`, then `proxy created` again when the host returns; never two host proxies | NOT TESTED |
| 8 | Client travels away and back | `VEHICLE proxies cleared (local region …)` then recreated; no accumulated `TS2MP_RemoteVehicle_*` objects | NOT TESTED |
| 9 | Hideout (if Shadowhawk bought on the host) | Both host cars spawned → two proxies on a client in the hideout | NOT TESTED |
| 10 | Regression | Remote player movement/animation, region clocks, sleep sync unchanged | NOT TESTED |
| 11 | Diagnostics | `VEHICLE_STATS`: `malformed=0`, `notOwner=0`, `unknown` ≈ 0, `update avg/max us`, `sent/recv` bytes; no exceptions | NOT TESTED |

Send both `plugin.log` files.

## 7c. M3.1.2 manual checklist (shared sleep time-lapse, plugin 0.3.3 on both instances)
Protocol 5: a 0.3.2 instance is refused. Throwaway profile. Watch the sky and the overlay line 2 (host shows `time-lapse (P2 sleeping)` while it runs).

| # | Test | Steps | Expected evidence | Result |
|---|---|---|---|---|
| 1 | Client sleeping together | Both in Hillville, both out of cars; client sleeps from night to morning (let it run to the target) | Host sky fast-forwards **while** the client sleeps; host log `SLEEP_START accepted … -> host time-lapse started`, `TIMELAPSE stop (target hour N reached …)`, `SLEEP accepted … (host watched … no extra advance)`; client `SLEEP_START sent/ack`, `SLEEP ack`; both clocks end on the same time (± a few game minutes); host keeps normal control during it | NOT TESTED |
| 2 | Host sleeping together | Stay in Hillville; host sleeps | Client sky fast-forwards as before; same end time | NOT TESTED |
| 3 | Client sleeping separately | Host → Madison Street; client stays in Hillville and sleeps | Host log `SLEEP_START accepted … (host elsewhere: no time-lapse)`; Madison sky/clock unaffected; Hillville advanced (check by host travelling there afterwards) | NOT TESTED |
| 4 | Interrupted sleep | Together in Hillville; client starts sleeping and presses Esc midway | Client `SLEEP end … (stopped)`; host `TIMELAPSE stop (reached sleeper's end time …)` shortly after; no fast-forward remains on either side; same time on both | NOT TESTED |
| 5 | Regression / performance | Walk/run/crouch together; travel independently; read both logs | No `PROPOSAL … -> Invalid/Busy` unless explained, no `TIME adopt` back to an older time, no exceptions; note `WORLD_STATS … timelapses=… update avg/max us` and `bytes out/in`; FPS only if measured in comparable scenes | NOT TESTED |

Also note money/day before and after each sleep (expected: hourly effects once per slept hour on the host; known calendar/economy limitation unchanged).

## 7b. M3.1.1 manual checklist (client sleep fix, plugin 0.3.2 on both instances)
Let each sleep **run to its target hour** (do not press Esc during the fast-forward); one extra run with Esc during the fast-forward covers the abort path. Note money/day before and after each sleep.

| # | Steps | Expected evidence | Result |
|---|---|---|---|
| 1 | Launch Host and Client with the launcher | Both `WORLD_INIT … sleepHooks=ok`; handshake OK (mod 0.3.2) | NOT TESTED |
| 2 | Host → Madison Street, Client → Hillville | Client overlay `(region owner, host validates)` | NOT TESTED |
| 3 | Client sleeps in Hillville (run to target) | Client: `SLEEP start` → `SLEEP end … (+N h, completed)` → `SLEEP request sent id=…` → `SLEEP ack`; host: `SLEEP accepted id=… Hillville -> HH:MM` | NOT TESTED |
| 4 | After closing the sleep UI | Client keeps the slept time; no `TIME adopt … ->` back to the old time; no `PROPOSAL rejected`; Madison time on the host unchanged | NOT TESTED |
| 5 | Host travels to Hillville | Host `TIME adopt Hillville …` to the slept time + elapsed | NOT TESTED |
| 6 | Client sleeps again (both in Hillville) | Host `SLEEP accepted … (host in region: catching up hour by hour)` + `TIME_STEP done … hourly ticks=N` (N ≈ hours slept, no FALLBACK); both show the same new time | NOT TESTED |
| 7 | Host sleeps (both in Hillville) | Unchanged behaviour: client follows the host's fast-forward; same time afterwards | NOT TESTED |
| 8 | (optional) Client sleeps and presses Esc mid-sleep | `SLEEP end … (stopped)`, request accepted, no rollback | NOT TESTED |
| 9 | Money / calendar | No unexpected jumps; day/rent change only from the host's own elapsed or slept hours (known limitation) | NOT TESTED |
| 10 | Logs | No exceptions; any `PROPOSAL … -> Invalid` line on the host now includes `from/advance/model` or `report/model` — send both logs if one appears | NOT TESTED |
