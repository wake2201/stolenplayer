# PROGRESS

## M2 — Remote player rendering & movement sync — **PENDING MANUAL QA** (implemented; in-game NOT TESTED), 2026-11-10

### M1 in-game evidence (user run 2026-11-09, logs `20261110-001135_host_ffb81b59`, `20261110-001200_client_6fe338e5`)
Both instances connected (`HANDSHAKE_OK` both sides), client `REDIRECT patched 13/13`, `VERIFY … (inside sandbox)`, `SAVE_GUARD_ACTIVE`, 100+ `BLOCKED_WRITE PlayerPrefs.*` (settings keys), clean `RemoteConnectionClose` on exit, no exceptions in client `unity.log`. M1 accepted by the user.

### Decisions
- **Model:** the game's associate thief `MainScene:Codename/anim` (legacy `Animation`: Idle/Walk/Run/Sneaking; 7 skinned meshes, Mixamo rig fully inside the subtree). Verified offline with UnityPy (read-only, scratchpad venv; no assets copied). Found at runtime via `Resources.FindObjectsOfTypeAll<Animation>()` by clip set; cloned under an inactive holder (no `Awake`/`OnEnable`), stripped to Transform/Animation/renderers, untagged, `DontDestroyOnLoad`. Fallback (only if no model found for 15 s): Unity built-in capsule without collider, logged as `AVATAR_FALLBACK`.
- **No second PlayerScript.** Local player read-only via reflection.
- **Protocol 2** (see CODEBASE_MAP §6): 29-byte `PlayerState` on a Sequenced channel at 20 Hz; reliable `SceneInfo`; M1 handshake unchanged except version numbers (0.2.0 / 2), so M1 and M2 builds refuse each other.
- **Scene gating:** scene key = sorted names of loaded scenes; avatar shown only when both keys match; otherwise `REMOTE_VIEW OtherScene` in log/overlay.
- **Animation:** Idle (< 0.2 m/s), Walk, Run (running flag or speed between walk/run reference), Sneaking (crouch; frozen pose when not moving); clip speed scaled by speed / the game's own `moveSpeed/runSpeed/crouchSpeed`. Avatar hidden while the remote is in a vehicle or hiding spot (vehicle sync is M5).
- **LAN (opt-in, untested):** ini `Role`, `BindAddress`, `HostAddress`, `SessionKey`; non-loopback addresses refused without a key. Default remains loopback-only. No NAT traversal/relay/public server.

### Verified offline
- Build 0 errors / 0 warnings (plugin, launcher, 4 test programs).
- `TS2MP.ProtocolTests` 14/14 (12 M1 regression + PlayerState/SceneInfo exchange over real UDP + v1 client rejected).
- `TS2MP.SyncTests` 18/18 (codec, yaw quantization, validation, stale/duplicate/wrap, interpolation midpoint, shortest yaw, hold, bounded extrapolation, teleport/gap snap, sender restart, frame-rate independence, jitter monotonicity, visibility matrix).
- `TS2MP.RedirectTest` 3/3, `launcher-test.ps1` 21/21 (15 M1 + 6 LAN-config cases).

### Not verified (needs the user's in-game run)
- Template is found at runtime and renders correctly (materials, scale, facing — `Avatar.YawOffsetDegrees` config exists in case the model faces backwards).
- Feet-position offset (avatar may float/sink if `CC.bounds` differs from the visual ground contact).
- Scene key equality when both players are in the same map; behaviour across map changes.
- Actual traffic (logged every 30 s in `LATENCY … traffic out/in`), smoothness, CPU cost. LAN on two PCs.

### Known limitations
- Remote player invisible while driving or hiding (no vehicle/hiding replication yet). No carry/tool/hand animations; no jump/climb/lean animation (no matching clips on the model).
- The associate model is the accomplice character, not the protagonist (protagonist model only exists in menu/intro scenes without run/crouch clips).
- If the remote is in another scene, nothing is shown (by design for M2).

### Next (recommended)
User runs `docs/TESTING.md` §5 (M2 checklist) and sends logs + screenshots. Then M3 (first world-interaction slice). Do not start M3 without approval.

## M1 — Launcher, BepInEx bootstrap, local handshake, save protection — **accepted by user (in-game run 2026-11-09)**, 2026-11-09

### Decisions
- Separate repo `D:\TS2-Multiplayer`. Docs moved out of the game folder; `PROJECT_PLAN.md` now at `docs/PROJECT_PLAN.md` (matches AGENTS/CLAUDE references). Game folder keeps a pointer `CLAUDE.md`.
- Toolchain: Windows' built-in .NET Framework `csc` (C# 5). No .NET SDK, no ILSpy installed (not needed for M1).
- Loader: BepInEx **5.4.23.5** win x64 (Unity Mono), pinned + SHA-256 in `tools/common.ps1`. Installed into the game folder.
- Transport: **LiteNetLib 1.3.1 `net471`** (2.x only ships netstandard2.1/net8, unusable on Unity 2020.3 Mono). UDP bound to `127.0.0.1` only.
- Role assignment: launcher-side, under a named mutex `Local\TS2MP.Launcher`, using `session.state` (PID + process start time to defeat PID reuse). The game receives its role via command-line arguments; the plugin is inert without them (normal single-player unaffected apart from BepInEx loading).
- Save ownership: **host** = real save folder, unchanged behaviour. **Client** = all 13 `persistentDataPath` callers transpiled to `<data>\client-sandbox` (seeded read-only from the real folder at client start) + guards. Fail-closed: client quits if the redirect cannot be verified.
- Handshake: client sends `Hello` (magic, protocol, mod version, game version = `Application.version`, session, instance) as connection data; host validates and replies `Welcome` (ReliableOrdered). Mismatches are fatal (no retry); `SessionFull` and transport failures retry every 3 s.

### Verified (offline, no game started)
- Build: plugin, launcher, tests compile with 0 warnings / 0 errors.
- `TS2MP.ProtocolTests.exe`: 12/12 PASS (handshake, 6 reject codes, session full, client-before-host, host restart + auto-reconnect, client restart, port in use).
- `TS2MP.RedirectTest.exe`: 3/3 PASS on the real game IL (13/13 callers found; rewrite removes every `persistentDataPath` call; critical methods covered).
- `tests/launcher-test.ps1` (fake game exe): 15/15 PASS (host/client/full, restart rules, unmanaged instance refused, simultaneous launches → one host + one client, config errors, verified backup, test saves untouched).

### Not verified (needs the user's in-game run)
- BepInEx 5.4.23.5 actually loads in this build; plugin `Awake` runs before the game's first `InitializePrefs.Reinitialize` (runtime check covers the opposite case by quitting).
- Harmony applies the transpilers and File/PlayerPrefs prefixes under Unity Mono (cannot be tested outside Unity: .NET Framework refuses to JIT methods calling Unity internal calls).
- Two real processes connect and handshake; Steam emulator tolerates two processes; BepInEx `LogOutput.log` sharing between two instances.

### Remaining risks
- Client PlayerPrefs `DeleteKey/DeleteAll/Save` and Unity's native screen-setting writes are internal calls → cannot be blocked; client may still change shared registry settings (resolution/window mode). Not save data.
- Client settings changes (graphics/keybinds/profile choice) are not persisted and not visible to the client itself (Set* is blocked, Get*(key,default) not shadowed).
- If the game ever relaunches itself through Steam, the arguments are lost; launcher reports "exited within 5 s".

### Next (recommended)
User runs `docs/TESTING.md` §4 (M1 manual checklist). If PASS → M2 (remote player presence). Do not start M2 without approval.

## M0 — Reconnaissance & codebase mapping — **completed (research only)**, 2026-11-09
M0 blockers 1–4 were resolved by the M1 authorization (use existing install; separate repo; doc path fixed; tools as needed).

No game files, saves, or settings were modified. Nothing was installed. No mod code was written.

### Verified
- Unity **2020.3.33f1**, **Mono** runtime, .NET 4.x API (`mscorlib 4.0`) → BepInEx **5.x (x64, Mono)** + HarmonyX fits this runtime class (compatibility not yet proven *in game*).
- No mod loader installed. No mod source code present. The repository root is the game folder; not a git repo.
- Game logic is in `Assembly-CSharp.dll`, global namespace. Main nodes: `PlayerScript`, `GameManager`, `Inventory`, `StoryManager`, `SaveGame`, `CarController`, `AI`/`PolicemanAI`/`Police_Manager` (see `CODEBASE_MAP.md`).
- Choke points that fit host authority: `StoryManager.AdvanceStory()`, `PlayerScript.StartPoliceChase(int,AI)`/`StopPoliceChase()`, `CarController.EnterVehicle()/ExitVehicle()`, `Door.Use()`, `Pickupable.PickUp()`, `SaveGame.SaveGameNewSystem(bool,float)`, `FBPP.Save()`.
- Strong single-player assumptions: one `PlayerScript PS` cached per NPC/door/car/pickup; 326 `Find*` call sites; money is a raw field (`PlayerScript.moneyAm`) written from ~40 methods.
- NPCs use A* `RichAI` + legacy `Animation`; vehicles use `Rigidbody` + 4 `WheelCollider`s.
- Saves: FileBasedPrefs under `persistentDataPath`, 5 profile files, automatic backup copies, `AutoSaveData=false`. Profile index is stored in PlayerPrefs (registry).
- No game save folder, Player.log, or PlayerPrefs registry key exists for this Windows user yet → **the game has not been run under this account**, or it writes elsewhere (U).

### Hypotheses (to validate in M1/M2)
- BepInEx 5 loads and Harmony patches apply on this build.
- Two Unity processes run concurrently (user-reported; not observed by me).
- Player 2 can be shown with the driver model in `CarController.tpp_character_anim` or an NPC model with legacy clips.
- Hooking `AdvanceStory` on the client and forwarding it to the host can replicate story progress without duplicate rewards (needs per-call idempotency IDs).

### Unknowns
- Exact meaning of `1.27c` (likely bundleVersion).
- Whether `Force Single Instance` is set (the user's report suggests not).
- Whether a full third-person player mesh exists; what `MainMenu.ActivateCharacterSelect` selects.
- How unique world objects can be identified: scene objects have no persistent id component (`Door_ID` is empty; `ItemID.item_ID` is a type id). Candidate: scene name + hierarchy path + spawn position.
- Exact SPrefs key layout (needs decompilation of `SaveGame`/`StoryManager`).

### Blockers / decisions for the user
1. **Install provenance (blocks M1 per AGENTS.md).** The folder is an AnkerGames repack with a Steam emulator (`steam_emu.ini`, `steam_api64.rne`). AGENTS.md requires that the launcher "invoke the legitimate installed game normally" with "no DRM/Steam bypasses". I did not touch the emulator. Development and testing should move to a legitimate Steam install of App 1332720 before M1. Paths, save locations, and `SteamAPI.RestartAppIfNecessary` behavior (it relaunches through Steam when the exe is started directly) will then need re-verification.
2. `PROJECT_PLAN.md` sits in the repo root, while `AGENTS.md`/`CLAUDE.md` reference `docs/PROJECT_PLAN.md`. Approve moving it, or fixing the references.
3. Tooling: full decompilation needs ILSpy/`ilspycmd` (requires a .NET SDK). Approval needed before installing.
4. The repo root is the game folder. Recommend a separate source repo (e.g. `D:\TS2MP\`) so mod code is never mixed with game binaries, with git initialized there.

### Next (recommended)
M1 — minimal launcher plus a BepInEx bootstrap plugin (host/client role via a lock file or named mutex, local UDP handshake, per-instance logs), after blocker 1 is resolved and saves are backed up per `TESTING.md`.
