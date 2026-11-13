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
