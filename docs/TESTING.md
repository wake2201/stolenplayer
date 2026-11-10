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
