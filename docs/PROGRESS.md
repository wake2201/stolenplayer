# PROGRESS

## W4.2B.1 — Gameplay regression fixes + developer test mode — **BUILT — PENDING MANUAL GAMEPLAY QA**, 2026-11-13
Mod **0.14.1, protocol 25** (the host commits a door / window-sash "slow mark dropped" transition). Input: the user's two-instance run 2026-11-13 17:34 with 0.14.0 (TESTING §30 header). No game instance was started by Claude. Not committed.

### A. Root causes (code / IL / log evidence)
| Regression | Root cause | Since |
|---|---|---|
| Driver stays in first person (Player 2 drove; Player 1 as passenger followed) | `CarController.RunCar` IL_021b reads `SPrefs.GetBool("TPP")` = `FBPP.GetBool(key, false)`; vanilla writes `TPP = true` in `MainMenu.StartNewGame` IL_003c / `StartFreeMode` IL_002f. Since W4.2 the client sandbox holds only the host snapshot, which drops `TPP` (LocalPreference, correctly not Player 1's value); `SaveGuard.CaptureLocalPreferences` returns silently while FBPP is not initialised - always the case in launcher mode (armed at startup), and in F8 mode whenever the client's own save has no `TPP` → key absent → first person. The passenger follows the driver's mode (`VehicleFlags.Tpp`, unchanged). The `VehicleSync` camera code is unchanged since v0.12.0 (`git diff 773292e` only adds the spawn helper). | **0.13.0** (predates 0.14.0); v0.12.0 seeded the sandbox with the client's own save incl. `TPP` |
| Early client accepted in the host's main menu (`CAMPAIGN snapshot NOT sent … not bound`) | `SessionNode.HostGate` was only installed by the F8 host (`MultiplayerMenu.StartHost`); the launcher's fixed-role host listens from startup without a gate. The client connected 4 s after the host process started, received Welcome + registry, never a campaign. | 0.13.0 |
| Relock → unlock: door stuck open | `Reconcile` applies kinds in the order Lockpick, LockedWindow, Door. A relock (`Door.ResetLock`: close + `Lockpick.RelockDoor`) commits lock 0 + door closed together; the replica relocked first (tag `Lock1`), then `WorldDoorRules.Decide` returned `SkipLocked` for the CLOSE ("not usable → wait"), so the door stayed open until somebody unlocked it again. In the user's run the door also diverged because its crowbar break was never shared (next row). | 0.14.0 |
| Crowbar door: lock open remotely, leaf not swinging | User log: crowbar on lock `43FDDB54` (0.86 s = the crowbar's 0.2 + 0.6 s waits) produced `Set Lockpick -> 1` but **no** door proposal; the only silent exit of `WorldObjectSync.LocalDoor` is the type filter `Ordinary` (gates were excluded together with furniture). The leaf's exact type cannot be recovered from these logs (it is logged now). Also: `Break` on a disabled `Door` (lock-gated leaf) set the fields without a swing until the lock replica enabled it. | 0.14.0 |
| Hold-E doors not visible / wrong | No door proposal at all in either log (same silent filter for the doors used); in addition the replica swing ran at a constant 45 x mult (original: ramp to 17, later 70) and could not follow an early release (`Door.SlowFailed` → 150 x mult; the host answered such a change `Unchanged`). | 0.14.0 |

### B. Changes
- `src/Shared/Campaign.cs`: `CampaignPolicy.LocalBoolDefaults` (`TPP = true`, the original new-campaign value); `ApplyLocalPreferences` fills a missing local preference with it (never the host's value).
- `src/Plugin/SaveGuard.cs`: `RefreshLocalPreferencesFromSandbox` (the in-session camera choice survives a menu-return snapshot); `InstallDevHost` + `SeedDevHostSave` (developer host plays a copy of the real save in `<data>\devtest\host-save` with the same redirect + guards; copied only while the copy has no save; the real folder is only read).
- `src/Plugin/CampaignBootstrap.cs`: refresh before apply; a snapshot that cannot be built (record not bound) is deferred and sent once the record is bound (defense in depth).
- `src/Plugin/MultiplayerMenu.cs`: the launcher host installs `HostGate` = `HostReadiness.Check` (4 Hz, transitions logged as `HOST_READINESS`); intentional waiting for a loading host unchanged (client retries `HostNotReady` every 5 s).
- `src/Shared/RegionState.cs`: `Decide` - closing never waits for the lock tag (opening still does); `ObjectStateRules.SlowFinish` (host commits slow → fast for doors and window sashes); `WorldDoorRules.FastFinish` (replica rule).
- `src/Plugin/WorldObjectBridge.cs`: gates shared (car doors + furniture stay local); `Break` enables the Door behaviour first (as `Lockpick.DoorUnlock` IL_006d does before `OpenDestroyed` in `CrowbarBreakin`); slow replica = the original `Door.Update` speed profile driven through `openSpeed` (`SlowTick`; `openingSlowly` stays false on the replica so the local HUD fill / mouse / movement are never touched), original squeak; `FinishFast` mirrors `SlowFailed` IL_0065..IL_016a minus the actor's effects.
- `src/Plugin/WorldObjectSync.cs`: the actor's watch tracks the slow phase and proposes the fast finish; the replica applies it; non-shared door types are logged once; `WOBJ_STATS` written every 30 s when changed (it was never logged).
- `src/Plugin/DevTestMode.cs` (new), `src/Plugin/Plugin.cs` (`-ts2mp-devtest 1`, loopback + no join code only, data folder forced to `<data>\devtest`, host save redirected), `src/Launcher/Launcher.cs` (command `devtest`, menu entry 4).
- `src/Shared/SaveIsolation.cs`: `DevTestDataDir`. `src/Shared/Protocol.cs`: 25 / 0.14.1. Tests + version pins; `release/TS2-Multiplayer-LIESMICH.txt` (gates / hold-E wording).

### C. Developer test mode (manual QA only)
Launcher `devtest` / menu 4 → `-ts2mp-devtest 1`. The plugin accepts it only in launcher mode with loopback bind/connect and no join code; F8 mode never parses it and the player release contains no launcher. Isolation: every TS2MP file lives in `%LOCALAPPDATA%\TS2MP\devtest\` (Player-2 record, spawn, fleet, client sandbox, logs, session state); host save = copy in `devtest\host-save` (redirect + FBPP / File / PlayerPrefs / achievement guards, runtime check every 2 s → quit on failure). Conveniences run only while the save target is verified isolated: all skills via the original `Skill_Manager.Learn_Skill` (`Skill_Manager.Start` postfix + a once-per-load fallback), extra skills at `maxUpgrades`; money topped up to 1,000,000 whenever below 250,000 (Player 2's money reaches its isolated record through the normal report path). No story, mission, XP, level or item is touched; tools are bought by the tester.

### D. Evidence
- `tools\build.ps1 -Test`: 26 csc builds, 0 errors (pre-existing CS0414 in Launcher.cs); **ALL 26 SUITES PASSED**; W42BTests **22/22** (17 previous + 5 new: relock → unlock replica sequence, crowbar + gates, hold-E slow / fast finish incl. window sash, driving-camera default, launcher readiness + deferred snapshot + developer isolation / source scans); version pins 25 / 0.14.1 in CarBox / Handoff / Passenger / W42 / W42B / package tests.
- IL (Cecil, read-only): `RunCar`, `SPrefs.GetBool`, `MainMenu.StartNewGame/StartFreeMode`, `Door.Update/Use/SlowFailed/OpenDestroyed/OpenOnStartt/Resett/ResetLock/Start`, `Lockpick.DoorUnlock/RelockDoor`, `PlayerScript.CrowbarBreakin` coroutine, `LockedWindow.UnlockWindow`, `Skill_Manager.*`, `SkillPage.LearnSkill*`, `FBPP.SetInt/SaveSaveFile`.
- Release: `build\release\TS2-Multiplayer-0.14.1.zip` (hashes in §F). NOT verified: anything in game.

### E. Performance (no in-game measurement)
Added per frame, Madison only: one `SlowCount` compare (+ ≤ 8 `openSpeed` writes while a replica hold-E swing runs), one `openingSlowly` read per watched slow door, one time compare for the 30 s stats. Launcher host: readiness check at 4 Hz. Developer mode: one time compare per frame, work every 2 s. Network: + one 19 B transaction (+ ≤ 804 B page delta) per hold-E swing released early. GC: nothing per frame (stats string every 30 s only when changed). FPS impact expected negligible - unmeasured.

### F. Release / deployment
`build\release\TS2-Multiplayer-0.14.1.zip` (27 files, manifest verified inside the ZIP, SHA-256 `6f4b8f155f39d07c6b9d15e4731fc6005a16c7eb3f1d6d2531c7f29b7b17be21`), plugin `TS2MP.Plugin.dll` `ee292be788477d87632cec88aed181b13ca7c1b74e4150a298bdbbd6413803cf`; package-test 14/14. The same plugin is deployed in the local game folder (`tools\build.ps1 -Test -Deploy`); the previous 0.14.0 plugin (`117b7e11…`) + LiteNetLib + manifest are backed up in `%LOCALAPPDATA%\TS2MP\backups\pre-0.14.1-20261113-184428`. Developer launcher rebuilt: `build\TS2-Multiplayer-Launcher.exe` (command `devtest` / menu 4). No game instance was started; the real save folder was not touched by this work.

## W4.2B — Shared doors, locks (lockpicking) and windows in Madison Street — **BUILT — PENDING TWO-PC MANUAL QA**, 2026-11-13
Mod **0.14.0, protocol 24** (`objTxn` v2 19 B with op + region epoch; new proposals `objScene`, `objInit`; registry `wrgn` + paged `wobj<kind>[.<page>]`). Research: `docs/research/*_SYSTEM.md`. Not committed.

### A. Architecture (src/Shared/RegionState.cs, src/Plugin/WorldObjectSync.cs, WorldObjectBridge.cs, WorldCensus.cs)
- **Region epochs + ownership:** a player's loaded scene instance (Unity scene handle) is reported once its Start ran (`objScene`, with the census object digest). The first reporter starts the epoch and is the **initializer**; members that change region keep membership for 4 s (brief probe gaps / reordered notifications do not reset); a member reporting a new scene instance while alone starts a new epoch (= vanilla reload); disconnect removes at once; no member left = every state cleared (vanilla reset). Simulation owner = lowest present member (host when present). Phase A's immediate `Objects.ClearRegion` on region change is replaced.
- **Canonical initial state:** the initializer sends every supported object not in its default state (open doors incl. the per-load `randomlyOpen` roll, broken hinges, unlocked locks, opened windows) in `objInit` chunks (160 entries, ≤ 20 chunks); the host commits them into paged lists (12 pages x 100 = 1200 per kind; Madison has 1142 doors) and marks the epoch initialized; every other scene reconciles silently: committed state, absent = default. Transitions are Busy until then; local changes made meanwhile are queued.
- **Transitions:** versioned per object (first committed wins), per-kind state/transition rules (broken hinge, padlock and opened window are terminal), rate 8/s, capacity 1200. **Reservations** (locks, windows): the original minigame start (`Lockpick.StartLockpicking`, `PlayerScript.BreakinCrowbar`, `LockedWindow.BeginCut`) is held in a prefix until the host reserves the object (host in place, client by proposal; refused → the press does nothing); the original then runs locally; the result hook commits (reservation consumed); the minigame ending without a result (`PlayerScript.isLockpicking` falls) releases; 120 s expiry; region change / disconnect release. Instant original actions (key, padlock, break glass, unlock from inside) are never blocked and cancel another player's reservation. Repeated `DoorUnlock` callbacks (advanced lockpick calls it once per door) are deduplicated locally and Unchanged on the host.
- **Windows:** one logical id per physical window = the census id of its `LockedWindow`; its sash `Door` (same GameObject; all 184 in Madison) maps to that id; the id is captured in the `UnlockWindow`/`BrokeWindow` prefix before the original destroys the component; sash open/slow/broken live in bits 2-4 of the window state.
- **Replica side effects:** the bridge only writes state/presentation (Door fields + `OpenOnStartt`/`Resett`/`OpenDestroyed`, lock tags / `was_locked` / colliders / `RelockDoor`, window glass objects / layer / tag / `Destroy(LockedWindow)`); `hasAlarm` is cleared before any retag (else `Lockpick.Update` would start a police chase on the replica). No DoorUnlock/BreakPadlock/UnlockWindow/BrokeWindow/CheckAlarm/noise/NPC alert/story/mission/stats/tool wear on a replica (source-scanned). The actor's original consequences (alarm, noise, stats, tool wear via the existing Player-2 inventory) run exactly once on the actor.
- **Player 2 story/mission rule:** prefixes on every progression entry (`StartLockpicking`, `BreakinCrowbar`, `DoorUnlock`, `DoorUnlockKey`, `BreakPadlock`, `BeginCut`, `UnlockWindowNoAlarm`, `UnlockWindow`, `BrokeWindow`) skip the original for Player 2 when the object is a story lock (`on_unlock_advance_story`, non-empty `story_name`, tutorial) or a mission window (`complete_mission`, tutorial, `Mission_Item`) — fail closed when unreadable, in every region; the host also refuses such ids from Player 2 when it has a census (`Restricted`). Player 1 unchanged. Postfixes run only when the original really ran (Harmony runs postfixes after a skipped original).
- **NPCs:** `Door.ResetLock` from `AI.OnTriggerEnter` (the only NPC caller) is published as `NpcSet` by the region owner and suppressed on the non-owner instance; other NPC door use (`OpenDoorNPC`, `CloseAfter`, `NPCUse`) stays local (unsupported until NPC sync).
- **Validation:** identity (census id; host catalog strict when it censused the region, else accepted as unverified and pruned when the host censuses it later), scene digest pinned per region (late census = no pin), epoch, present membership, kind/op/state/transition, reservation, initializer rules, duplicates/stale (proposal gate + versions), rate, capacity. Minigame success is trusted (stated boundary).
- **Phase 1 fix (host already in a region at F8):** `WorldCensus.CensusLoadedScenes` censuses the loaded scene once for the three object kinds only (no loot catalog, digest 0); the host initializes the epoch from its current scene, including doors opened / locks picked before hosting (read from `isOpen` / `brokeHinge` / `was_locked` / fallen padlock).

### B. Evidence
- `tools\build.ps1 -Test`: 26 csc builds, 0 errors (pre-existing CS0414 in Launcher.cs); **ALL 26 SUITES PASSED**: previous suites unchanged in count except W42Tests 13/13 (Phase A door cases 10-13 moved), version pins 24 / 0.14.0, **W42BTests 17/17 (new)** — host already in Madison, canonical random doors, 487-object initial state in 4 chunks / 5 door pages + 1200-door init, doors incl. broken, three lockpicking modes + key/padlock/crowbar/relock, single commit + duplicate auto-lockpick, rejection/cancel/expiry, same lock, windows (one id, inside/cut/broken/sash), side-effect source scans, P2 alone, host enters later, ownership transfer + NPC relock, lifetime (gap / grace / alone reload), reconnect during a reservation + initializer hand-over, validation, codecs/fuzz/scans.
- **Real game, one PC, developer Local Test (2026-11-13, plugin from the final source except a README change):** host auto-continued its save at the hideout; Player 2 restored into MadisonSt (seeded rejoin record from the previous Local Test). Client census `MadisonSt shared=1402 digest=FF97911A windowSashes=184 storyOrMission=4`; `scene report → Accepted`; host `epoch 1 started (initializer P2)`; client `initializer … 11 non-default object(s) (doors 11)` (previous load: 9 → per-load roll confirmed); host `chunk 1/1: 11 state(s), 11 unverified -> initialized`; client `ready: epoch 1 … owner=P2`. Dev hook `TS2MP_DEV_WOBJ` drove the ORIGINAL methods: `Door.Use` → `proposed Set Door … -> 1` → host Accepted; `Lockpick.DoorUnlock` (lock on `Door_0_Mesh`) → `Set Lockpick -> 1` → Accepted; `LockedWindow.UnlockWindowNoAlarm` (`O-Window-2_L`) → `Set LockedWindow -> 5` → Accepted; reservation through the real gate → host `reserved for P2` → client released it (target out of reach) → host `released`. No ERROR in either log. A first run found a dev-hook defect (it picked an inactive cabinet lock whose `Start` never ran → NRE inside the original `DoorUnlock`; hook now picks only active objects).
- NOT exercised in game: keyboard interaction, both players in Madison (replica swings/glass/locks visually), host entering an active region, late census in game, NPC relock, reconnect, two PCs.
- Saves: real `saveData.thief` / `saveInfo0.txt` byte-identical after the runs; the host instance's original load rotated its own `backups/saveData_backup3*` files and Unity analytics (vanilla, as in Phase A); the client wrote only its sandbox. Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.14.0-20261113-170216` (99 files, save hashes verified). The Local Test's temporary `p2spawn-0.dat` was removed again (none existed before).
- Release: `build\release\TS2-Multiplayer-0.14.0.zip` (27 files, SHA-256 `73a58485d564db8182c950c979725ae00b2d6dd77f6e39c0e6743cad18d82156`), plugin `TS2MP.Plugin.dll` `117b7e11c8e0951bbf161c789f96b76ccae7d2267a1fa22f15708bba907a7dc6` (same file deployed in the local game folder); package-test 14/14.

### C. Performance (no in-game FPS measured; `Debug.PerfStats` was off)
Per frame outside Madison: one region compare. In Madison: one `HeadRev` compare, `assignDoor` read + ≤ 4 watched doors, one scene-handle check per second, `isLockpicking` read only while a reservation exists. On a registry change: ≤ 36 page lookups + dictionary diff (allocates; registry changes are event-driven). Once per region visit: one pass over the 1402 census objects for the initial state and one silent full reconcile. Hooks: static field checks; `AI.OnTriggerEnter` prefix/finalizer = one int increment/decrement per NPC trigger. Network: scene report 14 B, init 10 + 6 B per non-default object once per epoch (Madison test: 76 B), transitions 19 B + one ≤ 804 B page delta, reservation 2 x 19 B.

### D. Status
| Subsystem | Status |
|---|---|
| Doors (quick / slow / broken hinge / relock close), canonical random doors | IMPLEMENTED; Local Test commit PASS in logs — replica visuals PENDING TWO-PC MANUAL QA |
| Locks: reservation, three minigames, key, padlock, crowbar, relock, duplicates | IMPLEMENTED; unlock + reservation round trip PASS in logs; minigames by keyboard NOT TESTED |
| Windows: inside / cut / broken, sash, one logical id | IMPLEMENTED; unlock-from-inside commit PASS in logs; replica NOT TESTED |
| Region epochs, grace, ownership, init hand-over, reconnect | IMPLEMENTED (offline tests); in game only P2-alone initialization PASS |
| Host F8 inside an already-loaded region (late census) | IMPLEMENTED BUT UNVERIFIED in game; DESIGN COMPROMISE: windows unlocked before hosting are not in the host's late census (component destroyed) and stay local; late-census ids could differ from fresh ones if destroyed loot shifted sibling indices (fail closed: host refuses unknown ids) |
| Player-2 story locks / mission windows | blocked (fail closed) — intended until shared missions |
| NPC relock | owner-replicated / non-owner suppressed — IMPLEMENTED BUT UNVERIFIED; other NPC door use local (unsupported) |
| Alarms triggered by the actor | actor only; police/NPC alarm sync is a future dependency (W8/W9) |
| Drawers, cabinets, cupboards, gates, electric locks, safes, other regions | not in scope (W4.2C / later) |
| Cabinet/container locks (`Lockpick` on furniture) | lock state shared; the furniture door itself stays local (W4.2C) |

## W4.2B — Technical investigation: doors, lockpicking, windows — **INVESTIGATION DONE, NO CODE CHANGED**, 2026-11-13
Read-only Cecil IL inspection of `Door`, `Lockpick`, MadGoat/Medium/Auto lockpick minigames, `LockedWindow`, `WindowOpenMaster`, `WindowUse`, `PlayerScript.Update` tag branches (`Door`, `Lock1`, `Window`, `WindowEnter`), `CrowbarHit`/`CrowbarBreakin`, `AI.OnTriggerEnter`. Results: `docs/research/DOOR_SYSTEM.md`, `docs/research/LOCKPICKING_SYSTEM.md`, `docs/research/WINDOW_SYSTEM.md`.
Key facts: none of the three systems writes a save key (all RegionLife; the roadmap's "durable broken windows" does not exist in vanilla); every lock outcome converges on `Lockpick.DoorUnlock` (story advance for story locks, `openedLocks` stat, tags/`was_locked`); replicas must not run the originals (alarm → police chase on the replica, story, mission, noise). Open decisions: story/mission locks and windows until W6, NPC relock ownership, `randomlyOpen` alignment vs. the 100-per-region capacity.

## W4.2 Phase A — Host-owned campaign bootstrap, persistent Player-2 spawn, region-object registry, door slice — **BUILT — PENDING TWO-PC MANUAL QA**, 2026-11-13
Mod **0.13.0, protocol 23** (new messages `CampaignSnapshot` 16 host→client, `CampaignRequest` 17 client→host; proposals `objTxn`, `p2pos`; registry key `wobj<kind>`). Baseline v0.12.0 (the rolled-back attempt in `stash@{0}` / `backup-codex-*` was not used). Not committed.

### A. Root cause (code-verified)
`SaveGuard.SeedSandbox` copied the client's own real save folder into the sandbox and the client loaded it with the original `MainMenu.Continue` → Player 2 entered with its solo position, story and world. The P2 record (inventory) was already host-owned; everything else was the client's own data.

### B. Bootstrap architecture (`src/Shared/Campaign.cs`, `src/Plugin/CampaignBootstrap.cs`, `SaveGuard.cs`)
- Verified IL (FileBasedPrefs.dll): `FBPP._latestData` = live model (`StringItem[]/IntItem[]/FloatItem[]/BoolItem[]`, each only ctor `(string, value)`); `GetSaveFileAsJson` = `JsonUtility.ToJson(GetSaveFile())`; `OverwriteLocalSaveFile(json)` = `WriteToSaveFile` + `_latestData = null` + reload. `GetSaveFile` creates an empty file when none exists.
- **Arm (client, main menu):** local preference `TPP` read from the real in-memory save (read-only) → old sandbox save files **moved** to `%LOCALAPPDATA%\TS2MP\client-sandbox-retired\<stamp>\` (never deleted) → seed copies only non-save files (`SaveIsolation.IsSaveData`: `saveData*`, `saveInfo*`, `backups\`) → armed → FBPP re-initialised (empty sandbox) → verified (unchanged N1.2 checks).
- **Host, per connection (after ClientReady):** live FBPP model → `CampaignSnapshot.Build` with `CampaignPolicy`: World keys copied; Player 1's personal keys (`Item_<id>`, `Money`, `BankBalance`, `BankIncrease`, `BatonLife`, `CrowbarLife`) replaced by the host-owned P2 record; host-executed storage/physical cargo (`Storage_Item_`, `Locker_Item_`, `HeavyVan_`, `HeavyItemShed`, `Pos*/Rot*_item`, `TrunkItem`) never copied (a local copy would duplicate real items); `TS2MP_*` never sent; position keys from the spawn plan (`CurrentLocation`, `Saved_Pos = 1`, `XPlayer..ZPlayer` for the original `SaveGame.LoadPlayerPosition`). Raw body (no 64 KB length prefix), reliable ordered, LiteNetLib fragments it.
- **Client:** applied only in the main menu, only while armed **and** `RuntimeCheck` passes → `FBPP.OverwriteLocalSaveFile` into the sandbox → verified (`CurrentLocation`, `Story`, target still inside the sandbox) → only then the original Continue may run. Harmony prefixes (client only): `MainMenu.Continue` blocked until a campaign was written in this connection; `StartNewGame/StartFreeMode/ClickedNewGame` always blocked for Player 2 (no second campaign). F8 flow continues automatically (Lifecycle `worldSynced` now = registry synced **and** campaign applied); back in the main menu while connected the client requests a fresh snapshot (host rate limit 1/5 s, served 0.5 s later so the exit position report is applied first).
- Separation: host world = host save (sent per join); Player-2 personal data = host files `p2inv-<profile>.dat` (existing) + `p2spawn-<profile>.dat` (new); local settings = PlayerPrefs/registry (blocked writes, unchanged) + `TPP`.

### C. Player-2 spawn (`SpawnRules`, host store, client placement)
- Campaign identity = host save profile + the P2 record's save-history id (`P2Book.HistoryId`); Player-2 identity = the campaign's Player-2 slot (same as the existing p2inv record — any authenticated join-code holder is Player 2; per-person identity is an open decision).
- Record `P2SpawnRecord` (46 B: history, region, feet, yaw, context flags, host story, seq, time) in `%LOCALAPPDATA%\TS2MP\p2spawn-<profile>.dat` (atomic replace + .bak, background writer). Updated from `p2pos` reports (client, every ≥ 3 s on a real change while grounded / in a vehicle, not lockpicking; plus one at `SaveGame.LoseGame` = exit to the menu); written ≤ every 5 s, at disconnect and at shutdown. Host validates slot 2, own registry region, ranges. Unusable file → quarantined (`.invalid-<stamp>`), other save history → `.stale-<stamp>`; never deleted.
- Decision: no/stale/invalid record → **NearHost** in the host's free-roam region (ring around Player 1's live feet), host in a mission scene / not placed → **OriginalSpawn** hideout; valid record → **Restore** (exact, yaw) or **RestoreNear** (last report in a vehicle / hiding spot); record in a mission scene (heists 90–98, jail, intro) → hideout original spawn; record with an unsafe story context → **SafePoint**.
- Placement after the original load (0.6 s after the region is confirmed): candidates (exact / VehicleSync ring 2–4.5 m / nearest original `GameManager.leavePoints` / original spawn) checked with VehicleSync's arrival test (ground normal ≥ 0.7, not on a car, ±4 m height, free capsule, ≥ 1.2 m from the other player, no traffic within 6 m); none → original position kept (fall guard active).

### D. Story-mission safety
Context flags come from original data at the time of the report: `StoryManager.currentStoryScript.block_saving` (the original forbids saving), `.block_all_buttons` (scripted step, travel blocked), `PlayerScript.isPoliceChase`, `caughtUp`; mission-only scenes by region. Any of them → never the exact spot: nearest original leave point of that region (Madison/Hillville) or its original spawn (hideout/Auto Shop), mission scenes → hideout. **Limitation:** the game has no per-mission "safe area" metadata; a story step without these flags (e.g. inside a mission house during a normal step) restores exactly. Not every mission was checked.

### E. Region-object registry (`src/Shared/RegionState.cs`, `WorldHost.Objects`)
Per (kind, region) list of `{id hash, state, version u16, committed-by}` in Region-scope registry entries `wobj<kind>` (≤ 100 per region, 804 B). Identity = W1 census id (namespace = region, FNV of scene + hierarchy path + kind; ambiguous never resolves). Host decides every transition: kind/region supported, requester in that region, valid state, **base version = current version** (first committed wins, stale/duplicate/reordered requests cannot reverse a newer state; ProposalGate dedups ids), rate ≤ 8/s per player, capacity; census catalog check when the host censused that region in this process (`WorldCensus.DoorKnown`), else accepted as **unverified** (logged; the host cannot simulate scenes it does not run — stated trust boundary). Lifetime per kind: RegionLife (cleared when the region empties = vanilla reload) / Campaign (serialiser for later kinds, not wired to a file yet — no Campaign kind exists).

### F. Door slice (`src/Plugin/WorldDoorSync.cs`)
Verified IL: player use only through `PlayerScript.assignDoor` (tag `Door`, `canUse`, `!brokeHinge`), `Door.Update` slow toggle / `SlowFailed`→`Use` quick toggle; Door has no save key, `randomlyOpen` = `Random.Range` per load; locked doors carry other tags. Only doors the local player touches are watched (≤ 4); a change → host commits / client proposes with the expected version; committed lists are applied on registry change and silently at region load (0.5 s after load) to ordinary doors (not car/drawer/window/gate/cupboard/fridge/cabinet/container) that are idle, not held locally, not pending, and unlocked here. Apply = `Door.Use` minus its local-player effects (as M5.0 car doors). Scope: Madison Street only.

### G. Evidence
- `tools\build.ps1 -Test`: 25 csc builds, 0 errors (1 pre-existing warning CS0414 in `src/Launcher/Launcher.cs`, untouched); **ALL 25 SUITES PASSED**: all previous suites unchanged in count (version pins 23 / 0.13.0 in CarBox/Handoff/Passenger/package tests) + **W42Tests 17/17 (new)**: first join, rejoin, other campaign/history, different region than P1, story contexts, fallback, invalid/stale records + fuzz, solo-save rules + source scans of the sandbox gate, P2 inventory base, door decisions with the host elsewhere, duplicate/out-of-order/conflict/rate/capacity, reload/reset/late-join/snapshot, replica rules, codecs, a ~70 KB snapshot over real LiteNetLib UDP, dispatch regressions, perf/UI scans.
- **Real game, one PC, developer Local Test (2026-11-13; plugin from the identical source, before the last non-code rebuild):** run 1 found a real defect (FBPP item types have no default constructor) — the client refused safely (nothing written, Continue blocked, stayed in the menu); fixed. Run 2: host `CAMPAIGN snapshot … keys 69/96 … 1249 B, 4.9 ms`; client `SEED retired … / 51 save file(s) NOT copied`, `written to the sandbox … 71 ms and verified`, original Continue loaded the host's Hideout, `P2SPAWN NearHost … placed … near Player 1` (2 m away); host `P2SPAWN report #1`, `p2spawn-0.dat` written (46 B), written again at disconnect. Run 3 (client restarted): plan `Restore/Rejoin`, placed exactly at the last position. Run 4 (record seeded: Madison + `block_saving`, host at the hideout): client alone loaded MadisonSt (census 2511 objects, 1142 doors, 0 ambiguous), `SafePoint … placed at nearest original leave point`, host `REGION_OWNERS Hideout:P1,MadisonSt:P2`, report #3 accepted. 0 ERROR lines. Real save folder: `saveData.thief` byte-identical; only the host's own original writes changed (`saveInfo0.txt`, the game's rotating `backups\`, timestamps = host Continue/map load); the client's loads produced no real-folder write. The test record was set aside as `p2spawn-0.localtest-20261113-042512.dat`.
- NOT exercised in game: doors (needs player input), F8 UI path (dev hooks used), two PCs, story saves, menu-return rejoin.
- Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.13.0-20261113-041118` (96 files hash-verified: saves, p2inv/fleet/sandbox, previous plugin).
- Release: `build\release\TS2-Multiplayer-0.13.0.zip` (27 files, SHA-256 `139efe2ebf8b7694fdb0d535bde5d185f97f17433047942c93379f5f11be68cc`, plugin `8c0ca889c06a636dc69ec2a6e9854a11146a995abd72ecf4cd1286af207353c1`, package-test 14/14); the same plugin is deployed in the local game folder. German README updated (host world, spawn, door scope).
- Observation (not caused by this milestone): the real `saveData.thief` on this PC was already a 568-byte Free Mode save at 03:22 before this session (the 59 KB `saveDataBackup.thief` from 02:43 holds the previous full save).

### H. Performance (hot paths; no in-game FPS measured)
Per frame: bootstrap = 1 bool + scene checks (`GetSceneByName` ×2) while in the menu, otherwise one region/time compare (reports ≤ 1 check/s, one 24 B proposal per ≥ 3 s on movement); doors = one region compare outside Madison, in Madison one field read (`assignDoor`) + ≤ 4 watched doors + a `HeadRev` compare; reconcile only on registry change (≤ 100 entries, census dictionary lookups). Once per join: host snapshot build 0.4–4.9 ms (96-key save; a 1,763-key save ≈ 38 KB / 2,500 extra keys ≈ 70 KB offline), client write ≈ 70 ms (main menu). Network: snapshot once per join; door change 16 B proposal + ≤ 804 B delta. No per-frame allocation in the new frame code paths except the rare reconcile.

### Status
| Subsystem | Status |
|---|---|
| Host campaign → client sandbox, Continue gate, no new game for P2 | IMPLEMENTED; one-PC Local Test PASS in logs — PENDING TWO-PC MANUAL QA |
| P2 first join near host / rejoin restore / region-independent load | IMPLEMENTED; one-PC Local Test PASS in logs — PENDING TWO-PC MANUAL QA |
| Story-safe fallback | IMPLEMENTED; seeded-record Local Test PASS; real story cases NOT TESTED; DESIGN COMPROMISE (necessary): only original flags/scene types, no per-mission safe areas |
| Menu-return rejoin (CampaignRequest) | IMPLEMENTED BUT UNVERIFIED |
| Exit report at `LoseGame` | menu exit IMPLEMENTED BUT UNVERIFIED; window close: not sent (runner disposed first) — periodic report covers it |
| Region-object registry | IMPLEMENTED (offline tests); unverified host ids accepted by design (trust boundary) |
| Door slice (Madison ordinary doors) | IMPLEMENTED BUT UNVERIFIED in game — PENDING MANUAL QA |
| Randomly-open doors (`randomlyOpen`) differ per instance until someone uses them | INCOMPLETE (known) |
| Host's doors in the region where it pressed Host (no census of the starting scene, existing N1.2 compromise) | stay local until the host re-enters that region â€” DESIGN COMPROMISE (documented, as W4.1 loot) |
| Locked doors, windows, drawers, NPC door use | not in scope (W4.2B/C, AI1) |
| XP/skills/survival stats of P2 | host's values at join (no P2 record fields) — DESIGN COMPROMISE until E1/M1 |
| P2 identity = campaign slot, not a person | open decision |
| Developer hook `TS2MP_DEV_AUTOCONTINUE` (launcher mode, host loads its save once) | test-only, like `TS2MP_DEV_AUTOJOIN` |

## N1.2 — Native F8 multiplayer menu + drag-and-drop release — **N1.2 BUILT — PENDING TWO-PC MANUAL QA**, 2026-11-12
Mod **0.12.0, protocol 22** (wire change: `ClientReady` message after Welcome, reject code `HostNotReady` = 13; `ConnState.Preparing`). Supersedes the N1 end-user launcher/installer: players copy the ZIP into the game folder, start the game normally (Steam) and use F8. The launcher stays as a **developer tool** (Local Test, fixed-role host/join); its N1 installer and the N1 launcher package were removed. Gameplay sync code unchanged (systems are now constructed/disposed at runtime; their code paths are the same). **No two-PC connection, no F8 gameplay flow and no save isolation on a second PC was tested** — see D.

### A. Investigation (save lifecycle, verified)
- Main menu scene `MainMenuNew`; gameplay `MainScene` (+ additive region scene). `GameManager.LoadMenu` = `SaveGame.LoseGame` (empty) + `LoadScene("MainMenuNew")` → returning to the menu saves nothing. `MainMenu.Continue` = `SPrefs.Save` (FBPP disk write) + `LoadNewSceneAsync("MainScene")`.
- FBPP is initialised by `InitializePrefs.Awake` → `Reinitialize()` (IL above): profile from PlayerPrefs `Profile` → file name; `FBPPConfig.SaveFilePath = Application.persistentDataPath` (**cached**), then `FBPP.Start(config)` (`_config`, `_latestData = GetSaveFile()`). `FBPPConfig.GetSaveFilePath()` returns the cached `SaveFilePath` when set (IL_0000–0013). `Reinitialize` never reads `this` (IL). Consequence: in the main menu FBPP already points at the **real** folder with the real profile in memory; switching a flag would not redirect it → the arming must re-run the original `Reinitialize` after the redirect is active, and verify.
- Player-input API used by the menu (IL): `PlayerScript.PMB` (`PlayerMotorBehavior`), `PCB` (`PlayerCameraBehavior`), `cursorLock` (read by `PlayerScript.Update` IL_0568 to set `Cursor.lockState/visible`), `rewiredPlayer` (`Rewired.Player.isPlaying`, public setter), `isLockpicking`; the original minigame locks (`Lock_Player_Minigame_Not_Illegal` / `Unlock_Player`) toggle exactly PMB/PCB/cursorLock (plus blur/controls, not used). `GameManager.loadedMap`, `GameManager.PS` = readiness facts. 174 game methods read legacy `UnityEngine.Input` directly → no text input in the world.

### B. Lifecycle (`src/Shared/MultiplayerLifecycle.cs`, glue `src/Plugin/MultiplayerMenu.cs`)
- States: Offline, HostStarting, HostReady, HostWaiting (joins paused), ClientConnecting, ClientPreparing, ClientSynchronizing, Connected, Disconnecting, Error. Context from real scenes: MainMenu (`MainMenuNew` loaded, no `MainScene`), InWorld (`MainScene` + `GameManager.loadedMap` + live `PS`), else Loading.
- **Host:** F8 → Host only `InWorld` (menu: "Hosten ist erst möglich, wenn dein Einzelspieler-Spielstand vollständig geladen ist."). Start: `SessionNode(Host, 0.0.0.0:<port>, new 25-char code)` + all sync systems built on the running world. **Readiness gate** (`HostReadiness.Check`, evaluated every frame, used by `SessionNode.HostGate` at the connection request and again right before Welcome): InWorld, loadedMap, PS alive, valid profile (PlayerPrefs `Profile` 0–4 and FBPP initialised), ≥ 3 s since the last scene load, systems constructed, region resolved (`WorldSync.RegionKnown`), Player-2 book bound (`Player2Sync.HostBookReady`). Not ready → `HostNotReady` (client retries every 5 s, up to 180 s). A connected client stays connected through the host's region transitions (existing systems). Host back in the main menu → session ends (`DetachSession`: systems' Dispose does the final fleet / p2inv writes, network stops). Listening socket or a successful authentication is never treated as readiness.
- **Client:** F8 → Join only in the main menu; in a loaded world the option is disabled with "Um einem Multiplayer-Spiel beizutreten, musst du zuerst zum Hauptmenü zurückkehren." No live migration, no automatic save/discard, no restart to switch roles. Flow: connect + join-code auth (unchanged N1 HMAC) → host gate → Welcome → `ConnState.Preparing` (no gameplay message accepted on either side) → **SaveGuard.Arm** (main menu only) → client systems built → `ClientReady` → host raises RemoteConnected → host world snapshot (`WorldSync.Synced`) → the original `MainMenu.Continue()` loads the sandboxed save (no sandbox save → the menu asks to start a new game from the original main menu, which writes only the sandbox) → Connected. Auth failure / timeout / host not ready: nothing armed, no scene change, client stays in the menu with a German error.
- **Sticky client mode:** once armed, the isolation stays armed for the process; Host is disabled ("…bitte das Spiel neu starten"); joining again is allowed. DESIGN COMPROMISE (necessary): returning the same process to solo play would require reverting FBPP/PlayerPrefs state mid-process; a restart is the safe path.
- Launcher developer mode (`-ts2mp-role`): unchanged fixed roles; F8 shows status only; HostGate off; the client sends ClientReady automatically.

### C. Save isolation (`src/Plugin/SaveGuard.cs`, rules `src/Shared/SaveIsolation.cs`)
- Normal start (F8 mode): **no patch is applied** (only a read-only precheck of folder layout + original API). Solo and host keep the original persistence untouched.
- `Arm()` (client only, main menu, after Welcome): install the 13 `persistentDataPath` redirects + guards → seed the sandbox (read-only copy of the real folder) → `Armed = true` (redirect → sandbox, guards active) → re-run the original `InitializePrefs.Reinitialize` (FBPP reloads from the sandbox) → verify `new FBPPConfig().GetSaveFilePath()` and the active `FBPP._config` resolve inside the sandbox; RuntimeCheck every 2 s afterwards. Failure before arming → join aborted, nothing changed; failure after arming → game quits (fail closed, as in M1).
- Guards while armed (unchanged rules): FBPP writes outside the sandbox, `File.*` writes into the real save folder or the game folder (except `BepInEx\`), PlayerPrefs Set*, graphics ini, achievements blocked. Host never needs the client's files and vice versa.

### D. Evidence
- Build: 23 csc builds, 0 errors / 0 warnings. **All 24 suites pass** on the deployed/packaged binary (no recompilation after the in-game runs): ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 34/34, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16 (case 15 now also restricts own IMGUI drawing to `MultiplayerMenu.cs` + the opt-in overlay), CarBoxTests 12/12, Player2Tests 19/19, Player2LifecycleTests 18/18, JointTravelTests 33/33, WorldIdentityTests 16/16, WorldMirrorTests 26/26, W3Tests 35/35, DurabilityTests 11/11, WorldLootTests 23/23, NetAuthTests 16/16, **LifecycleTests 17/17 (new)**, RedirectTest 3/3, launcher-test 21/21, **launcher-dev-test 18/18** (N1 host/join cases kept), **package-test 14/14 (new)**, n1-smoke 3/3. Version pins: protocol 22 / mod 0.12.0.
- LifecycleTests cover the 12 required cases (host from menu denied; host from a loaded world accepted, readiness only with every fact; join from the menu accepted; join from a solo world denied with the exact German text; join before host readiness → `HostNotReady`, retried, no preparation until ready (UDP); host region change while connected (UDP); host to menu ends the session; reconnect = one ClientReady per connection, automatic after the first preparation (UDP); auth failure → no preparation / no isolation request (UDP); timeout → error in the menu, nothing armed; host crash → retry, fatal ends the session; solo: no session and a source scan proving the F8 startup path applies no patch and opens no network) plus the ClientReady protocol (no gameplay before ready, ready timeout, abort = fatal), the full client flow, isolation failure, path rules, menu matrix and a scan that no log line contains a join-code variable.
- **Real game, this PC (2026-11-12):**
  - Normal start (F8 mode, no arguments): `PLUGIN_INIT mode=F8 … (no patch, no network until F8 Host/Join)`, `SAVE_GUARD precheck ok`, `MENU ready key=F8 isolationAvailable=True`; no errors in plugin.log or BepInEx `LogOutput.log`.
  - Developer Local Test (launcher, two instances): host `WELCOME sent … waiting for ClientReady` → `HANDSHAKE_OK host side`; client (launcher mode) `REDIRECT patched 13/13`, `SAVE_GUARD_ACTIVE … verified`, `HANDSHAKE_OK client side: ClientReady sent`.
  - **End-to-end F8 client path without UI input** (developer hook `TS2MP_DEV_AUTOJOIN` in the process environment of a normally started game; host = launcher fixed-role host on 127.0.0.1 with a join code, in its main menu): auth → `WELCOME … (host proved the join code)` → `Preparing` → redirect 13/13 + guards installed → `SEED copied 50 file(s)` → `SAVE_GUARD_ARMED` → `SAVE_GUARD FBPP re-initialised from the sandbox (InitializePrefs.Reinitialize)` → `VERIFY … (inside sandbox)` → `SAVE_GUARD_ACTIVE` → systems → `HANDSHAKE_OK client side: ClientReady sent` (host `HANDSHAKE_OK host side … is ready`) → `STATE_SNAPSHOT` + `MIRROR_SNAPSHOT` → `JOIN host world synchronized: loading the save through the original MainMenu.Continue()` → `SCENE local=MainScene`, census, arrival on-foot placement in MadisonSt; 0 ERROR lines; PlayerPrefs writes blocked (`BLOCKED_WRITE PlayerPrefs.SetInt …`). Both closed through the window.
  - **Real save folder hashed before/after that run: all 61 save/backup files byte-identical**; the only differences are Unity engine files the engine writes on every start (`Player.log`, `Player-prev.log`, `Unity\…\Analytics\…`; not game saves, not redirectable — same as documented since M1).
  - NOT exercised in game: the F8 window itself (drawing, cursor/input lock + restore), Host Game from a loaded world (needs a loaded save), the readiness gate in game, two PCs.
- Performance: per frame in F8 mode only `Input.GetKeyDown` + one bool check (no session). While hosting: readiness facts each frame (field reads, one `PlayerPrefs.GetInt`, no allocation; `FindObjectOfType` at most 2×/s and only while loading). Arming (once, main menu): IL scan of the game assemblies + 30 patches, ~1–3 s inside the "Speicherschutz wird aktiviert" step. Network: +1 B `ClientReady` per connection. No in-game FPS measured.

### E. Release (`tools/package.ps1`)
`build\release\TS2-Multiplayer-0.12.0.zip`, SHA-256 `d63dc894330d2cce9ca876a167fcfc7b95e5bfa351731db4c76f295757f7beb7` (27 files, no wrapping folder): `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `BepInEx\core\*.dll` (19, unmodified BepInEx 5.4.23.5), `BepInEx\plugins\TS2MP\` (`TS2MP.Plugin.dll` SHA-256 `50209c25fbee6d841bac885ee0b44b6fea43e6d5fd33987bb3b0f6a9213ee48f`, `LiteNetLib.dll`, `manifest.sha256`, `THIRD-PARTY-NOTICES.txt`, `DIRECT_CONNECT_SETUP_DE.md`, `licenses\` ×6), `TS2-Multiplayer-LIESMICH.txt` (German). No launcher, scripts, configs, saves, logs or secrets. Verified by `package.ps1` (manifest inside the ZIP, required layout, plugin = build) and `package-test.ps1` (extraction into a game folder with another mod + a BepInEx config: only additions, nothing overwritten; reproducible ZIP bytes). **Deployed locally**: game folder binaries = release (bootstrap, core, plugin `50209c25…`), stale N1 `install-record.txt` removed; backup before deployment `%LOCALAPPDATA%\TS2MP\backups\pre-0.12.0-20261112-204926` (128 files incl. the previous plugin, hash-verified). The N1 0.11.1 launcher ZIP in `build\release\` is obsolete.

### Status
| Subsystem | Status |
|---|---|
| F8 menu (open/close, cursor/input lock + restore, German texts) | IMPLEMENTED BUT UNVERIFIED in game — PENDING MANUAL QA |
| Host from a loaded world, readiness gate, menu → session end | IMPLEMENTED BUT UNVERIFIED in game (offline lifecycle + UDP gate tests pass) |
| Client join from the main menu, late-armed save isolation, auto Continue | late arming + FBPP re-init + verify + ClientReady + Continue VERIFIED on one PC via the developer auto-join hook (real saves byte-identical); the F8 UI path and two PCs PENDING MANUAL QA — HIGH-RISK until then |
| Systems started mid-world on the host (census of the starting region missing → W4.1 loot there inactive until re-entry; vehicle arrival hooks for the starting scene skipped, cars bound by the existing poll) | IMPLEMENTED BUT UNVERIFIED — DESIGN COMPROMISE (documented) |
| Sticky client mode (restart for solo/host after being Player 2) | DESIGN COMPROMISE (necessary) |
| Drag-and-drop ZIP | BUILT, contents + hashes verified |
| Developer hook `TS2MP_DEV_AUTOJOIN` (env var, one join from the main menu, code never logged) | test-only; not documented for players |
| Two-PC internet play | NOT TESTED |

Unchanged known limits: W4.1 loot not QA'd; a P2 pickup in a region the host never loaded this process is refused; shared XP and story not done (the client's world base is a copy of its own save); some unsaved P1 acquisitions keep the W3.4 durability limitation; the W3.4 unsaved-exit case is untested; transport not encrypted.

## N1 — Direct-IP internet multiplayer, finished launcher, release ZIP — **N1 BUILT — PENDING EXTERNAL TWO-PC MANUAL QA**, 2026-11-12 (end-user launcher, installer and launcher ZIP superseded by N1.2 the same day; networking/auth kept)
Mod **0.11.1, protocol 21** (wire change: Hello client nonce, `AuthChallenge`/`AuthResponse`, Welcome host proof, reject codes 9–12). Gameplay sync code is unchanged from 0.11.0 (only `SessionNode`, `Protocol`, `Plugin.Awake`/`LaunchArgs` touched). Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.11.1-20261112-195532` (125 files, SHA-256 verified: saves, p2inv/fleet/.bak/.stale, session.state, client sandbox). **WAN connectivity between two PCs was NOT tested** — everything below ran on one PC (loopback / 0.0.0.0).

### A. Networking (`src/Shared/SessionNode.cs`, `src/Shared/JoinAuth.cs`, `src/Shared/Protocol.cs`)
- Host listens on UDP **27960** (configurable 1024–65535) on a configurable IPv4 bind address (Host Game default `0.0.0.0`). Client target = IPv4 literal or DNS name + port; names are resolved **asynchronously** (`Dns.BeginGetHostAddresses`, IPv4 only, 5 min cache, retried on failure) so the Unity main thread never blocks. Exactly one remote Player 2 (`MaxClients = 1`); a peer still in the challenge occupies the slot (others get `SessionFull`, retryable). LiteNetLib architecture, channels and all gameplay messages unchanged.
- **Join code:** 25 Crockford-base32 chars (23 random from the OS CSPRNG = 115 bits, 2 typo-check chars), shown as `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX`, new per Host Game. Key = SHA-256("TS2MP-N1-join-key:" + code) (high-entropy input, no stretching needed).
- **Handshake:** client nonce (16 B) in Hello → host accepts the transport, sends a fresh single-use server nonce → client answers `HMAC-SHA256(key, "client" ‖ protocol ‖ game version ‖ client id ‖ client nonce ‖ server nonce)` → host verifies (constant-time) and only then sends Welcome with `HMAC(key, "host" ‖ … ‖ host id ‖ …)`; the client verifies the host proof before `Connected` (mutual). Before authentication any other message disconnects the peer and no gameplay message is delivered; the host relay never reaches a pending peer.
- **Replay / binding:** the challenge is stored per `NetPeer` (endpoint) and consumed by the first message; a captured proof fails against any new challenge; a reused client nonce (512-entry cache) is refused before the challenge (`AuthFailed`, logged `REPLAY`).
- **Limits (`AuthLimiter`):** ≤ 20 connection requests / 10 s per IP; 5 failed proofs (wrong code, timeout, replay, oversized request) within 60 s block that IP for 120 s (`RateLimited`, the client retries after 30 s); 30 failures from all IPs within 10 min lock new attempts for 60 s; tracked IPs bounded (1024). Connection request > 256 B → `Oversized` before parsing. Auth timeout 5 s.
- **Fail-closed:** a host bound beyond loopback refuses to start without a join code (`NET_START_REFUSED`); Local Test (127.0.0.1) runs without code (unreachable from the network). Fatal client outcomes (no retry): `AuthFailed`, `AUTH_REQUIRED` (host wants a code, client has none), `HOST_NOT_AUTHENTICATED` (client has a code, host proof missing/wrong), version mismatches. Host loss stays retryable; a restarted host has a new code, so the old client stops with `AuthFailed`.
- **Secrets:** the code reaches the game only via the child-process environment variable `TS2MP_JOIN_CODE` (set by the launcher, read and cleared in `Plugin.Awake` before anything else, only the derived key kept). Never on the command line, never in `launcher.log`, `plugin.log`, `session.state` or the ini (tests scan all of them). Per-process environment → host and client on one PC never share a file.
- **Transport is NOT encrypted** (documented in the launcher, quick start and setup guide): the code keeps strangers out; a packet observer can read gameplay state.
- Session id: a remote client cannot know the host's random session id; with a join code the host skips the session-id check (`Validation.Check(..., checkSession: false)`) and the client adopts `Welcome.SessionId`. Local Test keeps the session-id check.

### B. Launcher (`src/Launcher/Launcher.cs`, `src/Launcher/Installer.cs`)
- No arguments → console menu: Host Game, Join Game, Install/Update, Repair, Uninstall, Settings, Local Test (developer). CLI: `host [--port --bind]`, `join [--host --port --code-stdin]`, `local` (and the legacy no-command form used by `launcher-test.ps1`), `install|update|repair|uninstall [--yes]`, `status`.
- **Host Game:** checks the installed plugin equals the package plugin, no other managed/unmanaged instance, the UDP port is free on the bind address (exit 11 otherwise), verified save backup, starts the game, shows port, join code (`C` copies it via `clip.exe` stdin), this PC's LAN IPv4s, forwarding hint and the unencrypted-transport note. It does not query any external service for the public IP.
- **Join Game:** address (IPv4/DNS, validated, name resolved with 5 s timeout), port, code (typo check, up to 5 tries interactively). Separate PC: own saves backed up first; the client always runs with `SaveGuard` (role client → sandbox in `<DataDir>\client-sandbox`), so the joiner's real saves are never written. Same PC as a Host Game is allowed (local smoke). Remembers address/port in the ini, never the code.
- **Local Test:** the M1 auto-role two-instance flow, loopback only, no code; refused while a Host/Join session runs and vice versa. The M2 `Role/HostAddress/SessionKey` LAN keys are refused with a pointer to Host/Join (the unauthenticated SessionKey LAN mode is removed).
- **Settings:** game folder (auto-detect: exe folder, its parent, Steam libraries; or manual), host port, bind address, last host address/port; ini rewritten atomically.
- **Installer:** verifies every package file against `manifest.sha256` (and refuses unlisted payload files) **before** touching the game; plans, backs up every file it replaces (`<DataDir>\backups\install-<stamp>\game\…`, hash-checked), copies via temp file + hash check + atomic replace; records ownership in `BepInEx\plugins\TS2MP\install-record.txt`. A different BepInEx build is never overwritten (exit 12); an identical pre-existing BepInEx is recorded as pre-existing and kept on uninstall. Uninstall removes only owned files whose hash still matches (user-edited ones are kept and listed), plus the plugin config and — only for a launcher-installed BepInEx — its runtime log/cache/config, all backed up first; never game files or saves. Install/uninstall refuse while the game runs.

### C. Release (`tools/package.ps1`, called by `build.ps1 -Package` and `-Test`)
`build\release\TS2-Multiplayer-0.11.1-Windows.zip` (+ `.zip.sha256`): launcher, `payload\plugin\` (plugin, LiteNetLib), `payload\bepinex\` (unmodified BepInEx 5.4.23.5 win x64 files), `licenses\` (BepInEx/HarmonyX/MonoMod/Mono.Cecil MIT, UnityDoorstop LGPL-2.1, LiteNetLib MIT), `THIRD-PARTY-NOTICES.txt`, `SCHNELLSTART.txt` (German quick start), `DIRECT_CONNECT_SETUP_DE.md`, `manifest.sha256` (sha256sum format). Sorted entries, fixed timestamps: the same build always yields the same ZIP bytes (tested). Limitation: the .NET Framework csc (C# 5) has no `/deterministic`, so recompiling changes the hashes of our two binaries; third-party files are byte-identical in every build.

### D. Evidence (this PC only)
- `tools\build.ps1 -Test`: 23 csc builds, **0 errors / 0 warnings**; **ALL 22 SUITES PASSED** — ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 34/34, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16, CarBoxTests 12/12, Player2Tests 19/19, Player2LifecycleTests 18/18, JointTravelTests 33/33, WorldIdentityTests 16/16, WorldMirrorTests 26/26, W3Tests 35/35, DurabilityTests 11/11, WorldLootTests 23/23, **NetAuthTests 16/16 (new)**, RedirectTest 3/3, launcher-test 21/21 (M2 LAN cases replaced by Local-Test/legacy-key cases), **installer-test 49/49 (new)**, **n1-smoke 3/3 (new)**. Version pins updated to protocol 21 / mod 0.11.1 (CarBox/Handoff/Passenger tests, package + installer tests).
- NetAuthTests cover: config validation (ports, bind, IPv4/DNS targets), join codes (2000 unique, typo detection, key/proof binding), limiter (window, block, expiry, global lock, bounded table), binding (network bind without code refused; 0.0.0.0 reachable), loopback auth success with mutual proof and no code in logs, wrong code (fatal, no retry, host keeps listening), no-code client, unauthenticated host, replay (captured proof, reused nonce), bounded attempts (`RateLimited` after 5), oversized request, version mismatch with auth, full session (pending peer holds the slot, pre-auth message refused and never delivered, third peer refused, silent peer times out), port conflict on 0.0.0.0, reconnect (client restart; host restart with new code refuses the old one), DNS `localhost` + unresolvable name retried.
- installer-test covers: ZIP = manifest (34 files + manifest, hashes), required files, payload = fresh plugin + official BepInEx, version pins, German quick start content, **reproducible ZIP bytes**, install/status/no-op reinstall, repair (missing + damaged, backup), update (backup), tampered package and unlisted payload file refused with the game folder unchanged, foreign BepInEx refused, identical pre-existing BepInEx kept, install refused while the game runs, Host Game (args, code only in child env — verified via SHA-256 recorded by FakeGame — not in argv/launcher.log/session.state/ini, backup), second host / Local Test during host refused, Join (invalid code/address/unresolvable name refused, same-PC join, env code equals host code, ini keeps address not code), port conflict (exit 11), separate-PC join backs up own saves, test saves byte-identical, uninstall (cancel without `--yes`, removes only owned files, keeps other mods + user-edited file + game files, backs everything up).
- n1-smoke: two separate processes (host + client, code via process environment) authenticate and exchange a message; a client process with a wrong code is refused; no code in either log.
- **Release:** `build\release\TS2-Multiplayer-0.11.1-Windows.zip`, SHA-256 `0217badc5a567dd29f4a5009c1ef66dec7fc59d87a81da157f906c22a24b6d74`, 35 files, verified against its manifest inside the ZIP by `package.ps1` and independently by installer-test.
- **Deployed locally through the package's own installer** (`launcher install`): plugin SHA-256 `0fbe1c5faacae22dcb54bc214e6c16d17ae036e07c25c8dcb4af1f49b38266e4` = package payload; previous plugin (eb58ceaf…6db333) backed up in `backups\install-20261112-195812`; BepInEx recognised as identical pre-existing 5.4.23.5 (left untouched); `status` → all 24 files verified. Any later `build.ps1` run recompiles and changes this hash (see C).
- **Real-game smoke on this PC (no gameplay):** `launcher host --bind 127.0.0.1 --port 27960` + `launcher join --host 127.0.0.1 --code-stdin` with the real game: host `JOIN_CODE received … NET_STARTED … auth=join-code` → `ACCEPT … challenge pending` → `AUTH_CHALLENGE` → `AUTH_OK` → `HANDSHAKE_OK host side … join code verified`; client `SAVE_GUARD_ACTIVE` → `AUTH_RESPONSE sent` → `HANDSHAKE_OK client side … (host proved the join code)`; both closed via the window (`PLUGIN_SHUTDOWN`), no ERROR lines; the code was in none of plugin.log/unity.log (both), launcher.log, session.state, ini, BepInEx LogOutput.log, nor in either game's command line. Host bound to 127.0.0.1 deliberately (no firewall prompt); 0.0.0.0 binding is covered by the offline tests only.

### E. Performance
Per frame: unchanged (auth state is only touched in LiteNetLib callbacks and one `_pending.Count` check per `Tick`). Per connection: two extra reliable messages (17 B challenge, 33 B response) and +34 B in Hello/Welcome; 3 HMAC-SHA256 + 1 SHA-256 per attempt (µs range). DNS resolution off the main thread. No new allocations in `Update` paths. No in-game FPS measured.

### Status
| Subsystem | Status |
|---|---|
| Join-code auth, replay protection, limits, version checks (offline UDP tests) | IMPLEMENTED BUT UNVERIFIED in game — offline tests pass |
| Authenticated handshake inside the real game (one PC, 127.0.0.1) | VERIFIED by a local game smoke (handshake only, no gameplay) |
| Direct connection between two PCs over the internet (port forwarding, NAT, firewall) | NOT TESTED — PENDING EXTERNAL TWO-PC MANUAL QA |
| Launcher Host/Join/Local/Settings | IMPLEMENTED BUT UNVERIFIED by the user (fake-game tests pass) |
| Installer Install/Update/Repair/Uninstall | IMPLEMENTED BUT UNVERIFIED on a second PC (fake-game tests pass) |
| Release ZIP + manifest | BUILT, contents and hashes verified |
| Transport encryption | not provided (DESIGN COMPROMISE, documented; necessary for this milestone's scope) |
| IPv6 / CGNAT / DS-Lite hosting | not supported (documented); no UPnP/DMZ/VPN/relay by design |

Unchanged known limits: W4.1 loot not QA'd; a P2 pickup in a region the host never loaded this process is refused; shared XP and story not done; some unsaved P1 acquisitions keep the W3.4 durability limitation; the W3.4 unsaved-exit case is untested.

## M5 W4.1 Phase B — Shared pre-placed world loot (Madison Street subset) — **PENDING MANUAL QA**, 2026-11-12
Mod **0.11.0, protocol 20** (new proposal `lootTxn`, registry keys `wloot0..3`), p2inv file **v4** (written on the next host write; v1–v3 read; the real `p2inv-0.dat` + `.bak` (v3, record rev 35, txn 23, host seq 12, journal 1) were checked read-only on copies: v3 → v4 round trip identical). Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.11.0-20261112-174224` (saves, p2inv/.bak/.stale, fleet, session, client sandbox; p2inv hashes identical to the live files). Deployed DLL SHA-256 `eb58ceaf480a11f70de6fb6f88561a237fdc6930135de9c355cf2df1e46db333` (build = game folder). Gate: W3.4 development gate **waived by the user** (positive partial QA, see W3.4 below); Phase A investigation reused, not repeated.

### A. Supported / excluded
- **Supported:** census Pickupable objects of **MadisonSt** (namespace 99; census 869 loot objects, digest `50D2F273` identical on host and client in every logged run) with no exclusion flag and an ordinary item type: positive `item_value`, `add_XP` rounding to ≥ 1 (so the original `SetDayRespawn` always writes `ItemDay_<name>`), not heavy/car/drone/story/mission/tool/special. The in-game count of eligible objects is logged at scene load (`WORLD_LOOT_FLAGS MadisonSt … flagFree=N`) — **not measured yet**.
- **Excluded (original local pickup, unsynchronized as before, logged `WLOOT '<name>' not shared: <reason>` once per item type):** heavy (W4.2), money (economy), `destroy_if_was_picked` (unique items), `deleteIfPref`, parcel, story (`advance_story_if_*`, `story_name`), contract, `never_destroy`/`dontDestroyDays`, `add_any_XP` false, `cant_pick_reason`, dropped items (W3 path), **RandomFileCabinet contents** (random per instance), **shared-name families** (12 names / ~31 objects in Madison, `Coffee_Machine_Drop` ×2 in MainScene), numeric names (`Item_<name>` would hit a backpack-count key), ambiguous ids, MainScene loot and every other region. Excluded objects keep the pre-W4 DESIGN COMPROMISE (P2's pick is local + reported; both players can take them).

### B. Original methods reused (no new assets/UI/animation)
`PlayerScript.Update` pickup branch and checks (unchanged), `Pickupable.PickUp` → `<PickingItem>d__72` (presentation, sound, popup, XP, `SetDayRespawn`, `ItemID.Item_Picked`, `Destroy`), `Pickupable.DestroyMarker` + `Object.Destroy` for observer removal (= the original's own end), original keys `ItemDay_<name>` / `Item_<name>`, original `Pickupable.<Start>` key-based removal on scene load, W2 mirror for `ItemDay_`.

### C. Transaction paths
- **Identity:** W1 census id (namespace + FNV-1a of scene/path/kind), bound at `sceneLoaded` into a `LootCatalog` (object instance id → id, serialized flags, name, item id). Never the name. Catalog metadata is kept after unload (host validation in a region it visited in this process); live references released.
- **Player 1 (host):** `WorldItemSync.PickPrefix` (the single existing `Pickupable.PickUp` prefix) → `WorldLootSync.AllowPick`: taken → refused + object removed; else `LootBook.Claim` (**pending**) and the original runs unchanged. New postfix `Pickupable.SetDayRespawn` (the coroutine's irreversible step, IL_0353, for that very instance) → commit **taken** → registry. A pending claim whose object disappears (scene unload) or stays deactivated > 2 s before that step is **released** (nothing was granted). No timeout ever commits. P1's own pickup stays non-durable (original rollback of P1 + world together).
- **Player 2 (client):** `AllowPick` refuses the original, sends `lootTxn {txn = AppliedTxn+1, ns, hash, item, census digest}` (20 B). Host `LootDecision.DecideP2` in this order: retransmission → `Unchanged`; out of order → `Stale`; requester not in that region / host has no catalog of it / unsupported region → `NotAuthorized`; digest mismatch, unknown id, excluded flags, item mismatch, excluded item type → `Invalid`; taken → `Stale`; P1 pickup pending → `Busy`; registry chunk full → `Busy`; `max_amount` → `Invalid`. Accepted → `Player2Sync.HostLootTake`: record +1 (existing exactly-once txn) + world take in **one atomic p2inv write**, then `ItemDay_<name>` (never moved backwards) / `Item_<name>` / `TS2MP_HostTxn` in the save memory (+ mirror delta), then the registry commit. Only after the result does the client resume the **original** coroutine; the txn stays pending until the original counted the item; if the object vanished in the same scene the original `Item_Picked` is called directly; after a scene change the record restore carries it (never counted twice). Weight is checked by the client's original pickup branch (the host does not know P2's capacity).
- **Simultaneous:** first committed wins. P1 pending → P2 `Busy`; committed → P2 `Stale`; P2 committed first → P1 refused, object removed on the host. A pending P1 claim reserves its registry slot, so its commit can never be refused.
- **Region / reconnect:** taken set = Region-scope registry (arg = region, 4 hash chunks, ≤ 250 each, 1000 per region) → late joiners/reconnects get it with the snapshot; observers remove listed objects when the registry head changes (no scan). Region empties → set cleared (the original scene reload re-creates the objects; `ItemDay_` keeps them gone until their respawn day; original respawn untouched). **Host absent (P2 alone in a region the host never loaded in this process): P2 pickups of supported loot are refused (`NotAuthorized`, nothing happens) — INCOMPLETE; no client metadata is trusted.**

### D. Durability (host save + p2inv v4)
- P2 pickup + unsaved host exit: `HostBind` replays world takes above `TS2MP_HostTxn` (`ItemDay_` = max, `Item_<name>` = 1) → P2 keeps the item and the loot stays taken; idempotent; normal save → watermark covers it, compacted.
- **P1 → P2 unsaved-acquisition exploit (closed):** `LootProvenance` (host memory) tracks P1's unsaved world pickups (from `SetDayRespawn`, any loot that wrote a key) and P1's **recoverable** count per item type (saved key captured lazily before the mod changes it + journaled host deltas). On every durable P1 loss — P2 picks up P1's drop (`HostP2ItemTxn`, only when the drop is newer than the last saved host watermark) or withdraws P1's unsaved storage deposit (W3.4 prerequisite) — saved stock is attributed first; only if the recoverable count cannot cover the unit is the latest unsaved world pickup of that type journaled as a world take **in the same atomic write**. Reset at every original save disk write and at load. Unattributed unsaved units (shop purchase, pickpocket, money) keep the documented W3.4 limitation and are logged `P2INV … not a tracked world pickup`.
- Failed write → book + provenance restored, `Busy`, nothing changed. Profile/history isolation unchanged (`SameHistory`, profile in the file).

### E. Shared-name families (strategy for Phase C, not implemented)
Instance identity stays per object. The original's own semantic for a shared key is "one `ItemDay_<name>` for the whole family": in single player, taking one hides all of them after a reload. Phase C proposal: support them with per-object live state (as now) and keep the original key semantic for persistence (writing the family key on a take, exactly as the original does), documented as original behaviour; never merge their live state, never rename objects or invent new keys. Needs a user decision because it makes untouched family members vanish after a reload, as in single player.

### F. Performance (offline, .NET Framework, not Unity; **no in-game FPS measured**)
Catalog seal for 869 objects 0.87 ms (inside the existing census, scene load only; census itself was 180–200 ms MainScene / per-load before — unchanged work plus one flag read per Pickupable). Registry commit 10.8 µs per taken object; observer parse of 4 full chunks 11.7 µs, only when the registry head changes; `DecideP2` 0.049 µs, 0 gen-0 GCs over 100 k calls. Network per P2 pickup: proposal entry 40 B + result + chunk delta (25 B for the first object, ≤ ~1 KB when a chunk is full) + p2inv record delta; per P1 pickup: one chunk delta. Per frame: a few comparisons; pending claims checked 2×/s; no `Find*`, no scene scan (source-scan test). One extra synchronous p2inv write per P2 pickup (same as a CarBox transfer). In game: `WLOOT_STATS` (pickMax / decideMax / observeMax ms) every 30 s when active.

### Status
| Subsystem | Status |
|---|---|
| Census loot flags + catalog, RandomFileCabinet marking | IMPLEMENTED BUT UNVERIFIED (count not seen in game yet) |
| P1 pickup claim/commit/release, observer removal | IMPLEMENTED BUT UNVERIFIED — PENDING MANUAL QA |
| P2 pickup transaction, original coroutine resume, exactly-once count | IMPLEMENTED BUT UNVERIFIED — PENDING MANUAL QA |
| Durable world takes + replay, P1 → P2 provenance | IMPLEMENTED BUT UNVERIFIED (offline model incl. reproduction of the old duplicate) |
| P2 pickup in a region the host never loaded this process | INCOMPLETE (refused, nothing happens) |
| Excluded loot classes, shared-name families, other regions | INCOMPLETE (original local behaviour, unsynchronized as before) — Phase C |
| P2 XP from loot (client sandbox only, not host-owned) | INCOMPLETE (shared XP milestone) |
| Unattributed unsaved P1 units given to P2 before an unsaved host exit | DESIGN COMPROMISE (documented W3.4 limitation, logged) |

### Evidence
Build 0 errors / 0 warnings. All suites pass: Protocol 19, Sync 18, World 51, Vehicle 14, Travel 34, Handoff 37, Passenger 19, Interaction 16, CarBox 12, Player2 19, Player2Lifecycle 18, JointTravel 33, WorldIdentity 16, WorldMirror 26, W3 35, Durability 11, **WorldLoot 23 (new: P1/P2 pickup, simultaneous, duplicates, inventory full, interrupted pickup, late join, region return + respawn, normal save, unsaved exit, P1→P2 via drop (incl. reproduction) and via CarBox, saved-vs-unsaved quantities, same item type in different objects, shared names, exclusions, v3→v4 migration, history isolation, codecs + fuzz, failed write, WorldHost dispatch, dropped-item regression, source scan)**, launcher 21, redirect test. Version pins moved to 20 / 0.11.0; W3 pin relaxed to ≥ 19; Durability migration now expects v4. Offline tests do not exercise Unity, the census in a real scene, the original coroutine or the real save file. No Codex review.

## M5 W3.4 — CarBox & locker durability (unsaved host deposits) — **PARTIAL MANUAL QA (development gate waived by the user)**, 2026-11-12
**QA status (user report 2026-11-12, before W4.1):** positive in-game evidence for shared storage, real CarBox access, Player-2 transactions and journal recovery after a host restart. **Not conclusively tested end-to-end:** P1 deposit → P2 withdrawal → host exit WITHOUT saving (TESTING §24 step 5) — this recovery edge case stays **IMPLEMENTED BUT UNVERIFIED**. The user authorized W4.1 Phase B despite this gap.

Mod **0.10.4, protocol 19 (wire unchanged)**, p2inv file **v3** (written on the next host write; v1/v2 read). Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.4-20261112-041838` (saves, p2inv/.bak/.stale, fleet, client sandbox; p2inv + .bak hashes identical to the live files). Deployed DLL SHA-256 `5bb807c769d2eb6c31cf0c4a5757fc3938b391d54730e1ef7db47e8cc9b7e6fa` (build = game folder).

### A. Cause (code-verified)
P1's deposit exists only in live fields; P2's withdrawal is durable (record +1, journal Car −1, `Storage_Item_` key = live − 1, watermark). After a host quit without the original save the disk still has P1's unit in `Item_` and no unit in `Storage_Item_`; `HostBind` replayed Car −1 with `Math.Max(0, …)` (`ApplyStorageDeltas`) → absorbed → P1 and P2 both own the item. Same for the locker. Two further defects found: (1) Player-2 entries were replayed before all host entries (a host-side prerequisite could never precede the withdrawal it supports); (2) the transfer wrote the storage key from the LIVE count (may contain unsaved P1 deposits), so a stray FBPP write could persist inconsistent counts.

### B. Verified original save lifecycle (Cecil IL)
`Inventory.MoveItemToStorage` → `ItemID.Move_To_Storage(int)` / `Move_Out_Of_Storage(int)`: fields only (`item_amount`, `item_amount_storage`/`item_amount_locker`, weight, capacity). Keys `Item_`/`Storage_Item_`/`Locker_Item_` are written only by `ItemID.Save_Item_Amount`, whose only caller is `SaveGame.SaveGameNewSystem` IL_04a7 (all items), always followed by `FBPP.Save` IL_058a / `FBPP.SaveToMainProfile` IL_0590 → `FBPP.SaveSaveFile(true)` (`WriteToSaveFile` + `BackupSaves`). `ItemID.Start` reads the keys. Other live storage writers: `ItemID.Remove_Item_Blackbay` (removes), `ItemID.MoveToStorageOnDrop` (Hillville conversion inside `SaveGameNewSystem`, saved at once), `CMS_Part.AddPartToLocker` (adds, not from the backpack).

### C. Invariant
Between original saves a storage key = disk value + journaled multiplayer deltas = the count a reload recovers (the mod now writes storage keys only with a journal entry, as recoverable values). A confirmed P2 withdrawal never makes that recoverable count negative: if it would, the missing unit must be a tracked unsaved P1 deposit, whose effect (P1 backpack −1, storage +1) is journaled immediately before the withdrawal entry in the same atomic p2inv write; otherwise the withdrawal is refused (nothing changes) until the host saves. Saved stock is consumed first; unconsumed P1 deposits keep the original rollback.

### D–E. Implementation
- `HostDepositLedger` (host memory, per storage kind + item type, cap 1024): `Move_To_Storage` postfix +1, `Move_Out_Of_Storage` postfix −1 (floor 0); cleared by the FBPP write inside `SaveGameNewSystem` (prefix + finalizer depth flag; a postfix = write returned) and on every `HostBind` (new GameManager). A stray FBPP write (no `Save_Item_Amount`) does not clear it.
- `P2Book.HostTransfer` (pure): duplicate/order/capacity checks first (a retransmission never adds a prerequisite) → `StorageDurability.Decide(withdraw, recoverable = key, ledger)` → `Transfer` → prerequisite pair inserted before the withdrawal entry: `HostItem −1` + `HostCar +1` (new kind 4) / `HostLocker +1` (existing kind 3), one host sequence. Plan: storage key = recoverable ± (prereq) − 1, `Item_` key = key − 1 (only the prerequisite effect, never unrelated live changes), `TS2MP_HostTxn` = seq, `TS2MP_StorageTxn` = txn.
- Durability: one `FleetStore.WriteAtomic` contains record + prerequisite + withdrawal; the result is sent after it. Write failure → `P2Book.Restore(mark)` + keys, live field, ledger, watermarks restored → `Busy`, nothing changed.
- `ReplayAll`: one pass in journal (= commit) order, P2 entries above `TS2MP_StorageTxn`, host entries above `TS2MP_HostTxn`; both watermarks advanced to the replayed maxima. `SaveCompleted` compaction unchanged.
- `HostJournal(HostLocker)` (client-requested Hillville conversion) now writes the locker key as recoverable + 1 instead of the live count.

### F–I. Recovery behaviour
Unsaved quit: P1 −1 (clamped at the disk value), storage +1 −1, P2 keeps the item → exactly one unit. Normal save: `Save_Item_Amount` writes the live counts, watermarks cover every entry, journal compacted, no replay. CarBox → `Storage_Item_` (Car/HostCar), locker → `Locker_Item_` (Locker/HostLocker): never cross-replayed; leases unchanged. Migration: v1/v2 files load unchanged (record, money, bank, tools, history id/confirmed, watermarks, host sequence, journal order); written as v3; a v2-labelled file carrying kind 4 is rejected as corrupt (fallback `.bak`, existing behaviour). **Real records checked read-only** (copies of `p2inv-0.dat` and `.bak`, v2: record rev 27, txn 15, 2 item types, host seq 10, journal 13) → v3 round trip identical. Rollback to ≤ 0.10.3 needs the backup (an old reader rejects v3 and would start Player 2 fresh).

### K–M. Conservation, crash, performance
Never deletes an item to resolve a conflict; never more P1 deductions than confirmed withdrawals needed. Crash before the write = original rollback; during = old or new file (atomic replace); after the write before the ack = the grant is reproduced and the client resolves it from the record; retransmission = `Unchanged`. Performance: two O(1) dictionary updates per P1 storage move (UI action), one dictionary lookup per P2 transfer, ≤ 2 extra 13-byte journal entries per dependent withdrawal (same single write as before), ledger clear per save. No per-frame work, no scans, no extra disk writes. **No FPS measured** (not expected to change).

### Status
| Subsystem | Status |
|---|---|
| CarBox/locker duplication after unsaved host quit | IMPLEMENTED BUT UNVERIFIED — PENDING MANUAL QA (offline model reproduces the old duplicate and passes A–J) |
| Unattributed unsaved unit (e.g. `CMS_Part.AddPartToLocker`) withdrawn by P2 | refused until the host saves — DESIGN COMPROMISE (necessary: otherwise unrecoverable) |
| P1 unit acquired after the last save, deposited, withdrawn by P2, unsaved quit | P2 keeps it, P1's acquisition rolls back (money of an unsaved purchase returns) — one item exists, documented limitation |
| A P1 deposit consumed by P2 becomes durable even if P2 later returns the unit | by design (the dependent transfer was confirmed) |

### Evidence
Build 0 errors / 0 warnings. All suites pass: Protocol 19, Sync 18, World 51, Vehicle 14, Travel 34, Handoff 37, Passenger 19, Interaction 16, CarBox 12, Player2 19, Player2Lifecycle 18, JointTravel 33, WorldIdentity 16, WorldMirror 26, W3 35, **Durability 11 (new: reproduction + A–J)**, launcher 21, redirect test. The model mirrors `HostTransfer`/`HostBind`/save hooks on the same pure classes; Unity, Harmony and the real save file are not exercised. No Codex review.

## M5 W3.3 — Remote flashlight visuals removed — **PENDING MANUAL QA**, 2026-11-12
Mod **0.10.3, protocol 19 (unchanged wire format)**. Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.3-20261112-035156` (p2inv hash identical to the live file). Deployed DLL SHA-256 `0fa074c6a140797ab75b5e44305ef14e86fdfe3033f0f993f8492950e5c82bc1` (build = game folder).

**User QA of 0.10.2:** P1↔P2 drops, cross pickups, settling on the ground, pickup while the dropper's inventory is open, shared locker and Player-2 restart: working (user-confirmed). Remote chest lamp: **DEFECTIVE** — a bright magenta triangle (W3.1: white triangle). Product decision: no remote flashlight visual at all; no replacement.

**Removed (`src/Plugin/RemoteGear.cs`):** `ShowLamp`, `BuildLamp`, `SetLit`, `PoliceLamp` (+ the once-per-process `Resources.FindObjectsOfTypeAll(PolicemanAI)` search), `FlashlightItemId`, `FindChild`, fields `_lamp/_lens/_lampLit/_lampShown/_lampFailed/_nextLampTry/_nextLampSearch/_policeLampCache/_policeSearched`, `_policeT/_policeLight`, `LampToggles`, the per-sample lamp call in `Apply` and in `Seated`. `GearRules.Lamp/LampLength/IsLensMaterial` removed (`PlayerState.cs`), replaced by `GearRules.RemoteFlashlightVisual = false` (documentation constant). No remote flashlight GameObject, material instance, Light or shadow is created any more; nothing flashlight-related runs per frame on the observer. Remote spotlight code was already gone since W3.1.

**Kept:** the `Torch`/`HasLight` bits in the existing gear byte (protocol 19 unchanged, zero extra bytes; the sender still reads `PlayerScript.torch` and `item_IDs2[3]` once per snapshot, 20 Hz, compiled getters). Local flashlight: untouched — the mod never writes `torch`/`LightObj` except the pre-existing passenger-seat switch-off in `VehicleSync` (mirrors `CarController.EnterVehicle`), unchanged. Crowbar grip/swing, carry, dropped items, locker: unchanged code.

**Status:** remote flashlight visual removed — IMPLEMENTED BUT UNVERIFIED (PENDING MANUAL QA). Remote flashlight indication — INCOMPLETE by product decision (none shown).

**Evidence:** build 0 errors / 0 warnings; all suites pass (Protocol 19, Sync 18, World 51, Vehicle 14, Travel 34, Handoff 37, Passenger 19, Interaction 16, CarBox 12, Player2 19, Player2Lifecycle 18, JointTravel 33, WorldIdentity 16, WorldMirror 26, **W3Tests 35** — new case 35: flashlight bits round-trip without affecting the presentation, source scan of `RemoteGear.cs` for any lamp/material/light path, no plugin file creates a Light or writes the local torch outside `VehicleSync`; cases 26/34 lamp assertions replaced), launcher 21, redirect test. Offline tests do not show the rendered avatar.

### CarBox duplication (read-only review for the next milestone; NOT implemented)
- **Mechanism (code-verified):** P1 deposit lives only in the host save memory (`Item_<id>` −1, `Storage_Item_<id>` +1). P2's withdraw is durable (p2inv record +1, Car journal −1 above `TS2MP_StorageTxn`). Host quits without the original save → disk has the pre-deposit counts. On reload `Player2Sync.ApplyStorageDeltas` replays Car −1 with `Math.Max(0, …)`: the disk storage never contained that unit, so the decrement is absorbed (or eats an older unit), while P1's disk backpack still holds the item → duplicate. Same shape for the locker (`Locker_Item_`).
- **Recommendation (host-authoritative, no change to the original save schedule):** when the host commits a P2 withdrawal of item X, check a host-memory counter `unsavedHostDeposits[kind][X]` (incremented by the host's own `ItemID.Move_To_Storage(kind)` while P2 is connected, reset when the original save writes `TS2MP_HostTxn`/`TS2MP_StorageTxn`, i.e. the existing watermark hook). If > 0, write in the same durable p2inv write, before acknowledging P2, a host-journal pair with one host sequence: `HostItem X −1` (P1 backpack) and a new kind `HostStorage(kind) X +1` (the deposit), and decrement the counter. Replay rule unchanged: above `TS2MP_HostTxn` only → after a no-save quit the deposit is reconstructed (P1 −1, storage +1, then Car −1 = net: P1 lost it, P2 has it); after a normal save the watermark covers it and nothing replays. Never journal host moves P2 did not consume (single-player rollback semantics stay original). Needs: one new `JournalKind`, p2inv file v3 (or a reserved kind value), tests for save/no-save × car/locker × repeated withdraws, and a check that replay ordering applies the deposit pair before the Car −1 (ascending host sequence before Player-2 txn, or a clamp-free combined apply).

## M5 W3.2 — Dropped-item settling, inventory-open drops, police chest lamp, crowbar grip — **PENDING MANUAL QA**, 2026-11-12
Mod **0.10.2, protocol 19** (both instances; one install folder). Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.2-20261112-031056` (saves + p2inv/fleet records + client sandbox; p2inv hash identical to the live file). Deployed DLL SHA-256 `dfd5b8f74d49fbb845bc184b2cee530f58e7b6647230d022f2bb508ba4026ff6` (build = game folder).

### A. Evidence (user QA of 0.10.1, logs `20261112-023722_host_9c7e43af`, `…023730_client_10cda049`)
Transfers both ways committed exactly once (`WITEM host dropped item 0 as #1`, `P2INV item txn #2/#3/#4/#5`, `WITEM host picked up #3`). Host item #1 was dropped at 01:39:31 and picked up by P2 at 01:39:47 while the host's `WITEM_STATS` still showed `settles=0`: P2 saw it at the registry pose = the drop point. Client drops #3/#5 produced one settle between them.

### B. Root cause of floating remote items (verified IL)
- Every backpack drop happens inside the pause menu: `Inventory.DropItemNewSystem` is reached only from the inventory UI and `PlayerScript.EnableInventory` IL_00b0 sets `Time.timeScale = 0` (`DisableInventory` sets 1). The original object is instantiated at once at `Inventory.dropPos` (in front of the player, in the air) with a dynamic Rigidbody (IL_0082–IL_0139) but cannot fall until the menu closes.
- The registry recorded that drop point; observers spawned the copy there, kinematic, with its original solid colliders → a solid object in the air until a settle arrived.
- W3.1 settle detection (`velocity < 0.02 || IsSleeping || isKinematic`, then "moved ≥ 3 cm") could only fire after the dropper closed the menu and the object moved; a pickup or a long menu stay before that left observers with the floating copy. Nothing marked an item as "not settled yet", so a joiner also got the airborne pose.
- Original rigidbody lifecycle (new finding): `Pickupable.<Start>d__68` sets `thisRigid` + `deleteRigidInTime` (non-heavy); `Pickupable.Update` IL_0181–IL_01be **destroys the Rigidbody** once `IsSleeping()` or `isKinematic`. Every resting dropped item is therefore a static collider object; the observer's kinematic copy loses its Rigidbody the same way (no competing simulation).

### C. Fix (authoritative settle + observer presentation)
- Registry lifecycle bit `WorldItem.Settled` (bit 7 of the Sim byte; record stays 34 B, 30 items still fit): drop = Falling, first settle report = Settled. Protocol 18 → 19 (wire semantics changed).
- Owner (the dropper's instance, P1 or P2; the host never simulates P2's item): `SettleRules.OwnerShouldReport` — never while paused; first report when the original removed the Rigidbody, or it is asleep/slow after ≥ 0.2 s of running physics; always sent once (marks Settled), afterwards only after ≥ 3 cm / 5° movement. Checks 5×/s while unreported, 1×/s after; no streaming.
- Observer: a Falling item is shown on the surface straight below the drop point (one `RaycastNonAlloc` per registry event against the layers the item's own layer collides with per the original physics matrix; skips itself, triggers, players/avatars, moving bodies; lift capped at 0.25 m, no hit → reported pose). A Settled item is placed exactly at the reported rest pose (tables, shelves, slopes). Joiners use the same rule from the current registry value. Original colliders are kept (solid on the surface, like an original resting item).
- Stale settles after pickup / region clear / from the non-simulating player stay refused (`Stale` / `NotAuthorized`), unchanged ownership/transaction code.
- Diagnostics `WITEM_DIAG` for the first 8 items of a session: drop (id, item, dropper, region, position, menu state/timeScale, rigidbody, colliders), owner settle (pose, reason, fall height, seconds after drop / unpaused, result), observer show/update (reported vs. shown pose, surface hit, rigidbody, colliders). `WITEM_STATS` adds `settleApplied`, `surfaceShown`, `surfaceMiss`.

### D. Drop while the inventory stays open
Cause: original-game mechanic, not the multiplayer transaction. The original drop runs immediately (inventory −1, object instantiated) on both roles (host: at once; client: after the host commit, ~25–40 ms in the logs); the object cannot fall because the inventory is the pause menu (timeScale 0). No deferred/queued action on close exists in `DropItemNewSystem`. **Result:** the observing player now sees the item promptly on the surface below the drop point even while the dropper keeps the menu open; the dropper's own object keeps the original behaviour (falls when the menu closes). Not changed on purpose: `Time.timeScale` and the pause (a manual `Physics.Simulate` would step the whole scene, cars included). Whether the world behind the menu is visible at all is NOT verified (QA C).

### E. Chest lamp (verified asset hierarchy, UnityPy read of level1 / level3 / sharedassets1)
- All five police instances (MainScene `NPCs/116/NPC (3)/(4)`, `MANAGERS/POLICE MANAGER/POLICE/Policeman`, `…/Policewoman/femalePolice1`, `SIDEWALK NPCs/Sidewalk_Police`, `Policeman Ragdoll2`) carry `PolicemanAI.flashlight` = `flashlight_clean` **in `mixamorig:RightHand`** (local scale 0.7, inactive by default): `flashlight_body` (mesh extents 0.034×0.034×0.144, materials `flashlight_glass_clean` + `flashlight_clean 1`, Standard) with child `GameObject` (spot `Light`, 72°, range 10, intensity 2.5) + `flashlight_focus_ring` (z +0.092, material `flashlight_clean`). **No police model has a chest-mounted lamp**; the requested "original chest equipment" does not exist — the chest placement remains the agreed DESIGN COMPROMISE using the original hand flashlight assembly.
- Triangle root cause: W3.1 copied the right object but made **all 4 material slots** HDR-white emissive (×1.6, material instances) → the whole torch rendered as one glowing white shape (with bloom), regardless of view angle.
- Now: whole assembly copied (Light/scripts stripped, original shared materials untouched), verified by its `flashlight_body` child, uniformly scaled to 0.14 m (proportions kept), left chest, back end on the shirt, lens forward ~10° down (lens end = focus-ring end). Only the lens slot gets one material instance (copy of `flashlight_glass_clean`), emission on/off on change; off = no emission. No Light, no shadows, no AI interaction. Lens instance destroyed on avatar rebind; copies die with the avatar (disconnect / scene unload); hidden when seated.

### F. Crowbar
Simple attachment per user decision: long axis across the palm (pinky → index knuckle), mesh's second axis along the fingers, **shaft middle at the palm centre** (half-way wrist → middle knuckle), parented to `mixamorig:RightHand`. W3.1's grip 15 % from the end (85 % hanging below, 60° down) and the −12° upper-arm lift are removed. Swing (`GearRules.SwingCurve`, Attack() counter) unchanged. DESIGN COMPROMISE (open hand), PENDING visual QA. Log `AVATAR_GEAR held tool … shaft-to-palm <m>`.

### G. Status
| Subsystem | Status |
|---|---|
| Item ownership / transactions / locker | unchanged code, user-confirmed in 0.10.1 |
| Falling/settled lifecycle + observer surface placement | IMPLEMENTED BUT UNVERIFIED — PENDING MANUAL QA |
| Drop with inventory open | observer: IMPLEMENTED BUT UNVERIFIED; dropper: original pause behaviour (limitation, documented) |
| Chest lamp (police assembly, lens-only glow) | IMPLEMENTED BUT UNVERIFIED; chest mount = DESIGN COMPROMISE (no original chest lamp exists) |
| Crowbar grip | DESIGN COMPROMISE, PENDING visual QA |
| Crowbar swing | unchanged (user-accepted in 0.10.1) |
| CarBox P1-deposit → P2-withdraw → host quit without save | **HIGH-RISK, open, blocker before W4** (plan in W3.1 B, not implemented here) |

### H. Performance
Added: per frame one loop over this region's bound items (≤ 30, owner items only accumulate unpaused time); owner checks 5×/s until reported, then 1×/s; per registry event and item: one `Physics.SyncTransforms` + one non-allocating raycast (observer, falling items only) — no per-frame searches, no streams. Network: unchanged message sizes (bit in the existing byte); settle = one 37 B proposal per item (plus one per real later movement). Lamp: one material instance instead of four. **No FPS measured** (enable `Debug.PerfStats` for QA G).

### Evidence
Build 0 errors / 0 warnings. All suites pass: Protocol 19, Sync 18, World 51, Vehicle 14, Travel 34, Handoff 37, Passenger 19, Interaction 16, CarBox 12, Player2 19, Player2Lifecycle 18, JointTravel 33, WorldIdentity 16, WorldMirror 26, **W3Tests 34 (8 new W3.2 cases)**, launcher 21, redirect test. Offline tests cover registry lifecycle, settle rules, surface-offset rule, codec, gear constants — not Unity physics, raycasts, rendering or poses. No Codex review.

## M5 W3.1 — Dropped-item repair, remote crowbar grip/swing, chest lamp — **PENDING MANUAL QA**, 2026-11-12
Mod **0.10.1, protocol 18** (both instances). Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.1-20261112-023459`. Deployed DLL SHA-256 `ff97006b…a017e53a` (build = game folder).

### User QA of 0.10.0 (logs `20261112-020657_host_1d1ee760`, `…020706_client_50a156f8`)
Locker deposit/withdraw (both players), held-tool visibility and flashlight on/off: working. Drops: host log `WITEM dropped object of item 0 not found after the original drop: not bound` ×2; Player 2 had an empty backpack after its locker deposit (record `items 0`), so only host drops were attempted. The two host drops stayed as local objects on the host (inventory −1 by the original, item on the ground, not lost).

### A. Root cause (verified IL)
`Inventory.DropItemNewSystem` instantiates `ItemID.drop_object` untagged (Pickupable on the root, `GetComponent<Pickupable>` IL_0089…). The `"Pickupable"` tag is assigned only by `Pickupable.<Start>d__68` (IL_00b9 `set_tag`), which `Pickupable.OnEnable` starts and which first waits `WaitForSecondsRealtime(0.05)` (IL_002a). W3's postfix searched `GameObject.FindGameObjectsWithTag("Pickupable")` immediately after the original returned → the new object was never in the result. Same code path on host and client (the client runs it after the host's commit), so client drops would have failed identically. Item 0 is a real item id.

### B. Repair
- **Capture:** a narrow transpiler on `Inventory.DropItemNewSystem` passes the result of its only `Object.Instantiate<GameObject>` call through `WorldItemSync.OnDropInstantiated` (same object returned). `DropCapture` (pure) is armed in the prefix, records the first instantiation, and the postfix binds it after the original set `item_ID`/`wasDropped`. No tag, no scene search. If the transpiler does not find exactly one call site, shared drops are switched off (original drops, logged) instead of half-working.
- **Refusal before mutation (`DropGate`, pure):** region unknown, region limit (30) reached, another Player-2 transaction in flight, Player-2 inventory not synchronized → the prefix returns false: the original never runs, the inventory is unchanged, no untracked object (log `WITEM drop refused before the original ran`, rate-limited). Host checks the book before its original drop; the client checks its registry copy and the host refuses again (`Busy`). Special items (heavy/car/drone/story/mission) keep the original local drop, logged.
- **Ownership chain (unchanged from W3, now reachable):** P1: original drop (backpack −1, host sequence + watermark in save memory) → registry item → claimer. P2: host record −1 written durably → registry item → original drop runs locally. Pickup: host claim (one winner) → claimer's record/backpack +1 (P2 durable; P1 journaled when the item came from P2) → registry removal. A capture failure after the host original (should not happen) leaves a local object on the host, never a loss.
- **Still open (not changed, HIGH-RISK, blocker candidate for W4):** P1 deposits into the CarBox → P2 withdraws (durable) → host returns to the menu / quits without saving → P1's save still has the item: one duplicate. Narrow repair plan: journal the host's own storage moves while P2 is connected (HostItem −1 with a host sequence written next to `Storage_Item_`/`Locker_Item_`, replayed only for items later withdrawn by P2), same pattern as the W3 drop journal.

### C. Remote crowbar grip and swing
- Investigation: the associate model has only Idle/Walk/Run/Sneaking/Kneel/Sitting/CarryRun; no NPC melee strike clip exists in code (`AI` has only an `armedAnimation` threat pose + `armed_weapon`, residence scenes only, `AI.Update` IL_2984–IL_29af; police/guards/mob: shoot clips). No reusable original third-person tool pose.
- Grip (DESIGN COMPROMISE, needs visual QA): the tool copy is placed once from the avatar's own pose, convention-free: long mesh axis pointing forward-down (~60°) from the palm (between `mixamorig:RightHand` and `RightHandMiddle1`), gripped 15 % from the upper end, then parented to the hand (follows walk/run/crouch); while held the right upper arm is lifted 12° forward in LateUpdate.
- Swing: `PlayerScript.Attack()` postfix (one call per original attack, `PlayerScript.Update` IL_3994) → 4-bit counter in the snapshot gear byte → one 0.55 s procedural right-arm swing (raise −150°, strike −40°, recover) for crowbar/baton in LateUpdate after the legacy Animation (one bone rotation per frame; locomotion untouched; no replay on reconnect; duplicates ignored; presentation only, no damage).

### D. Chest lamp (replaces the remote spotlight)
Mesh source: the police NPC's own `PolicemanAI.flashlight` object (switched on by `PolicemanAI.OnEnable` IL_0118 at night), found once per process in the loaded police assets; fallback: the flashlight item's `drop_object` (`item_IDs2[3]`). Meshes only (Light/scripts stripped, cone/glow meshes dropped), ≤ 16 cm, on the left chest of `mixamorig:Spine2`, lens forward. Shown while the remote player owns a flashlight (new gear bit), emissive (one material-instance set per lamp, emission keyword/colour toggled only on change) while on, dark when off, hidden when seated. No light source, no shadows → the old remote spotlight and its cost are gone; `Avatar.RemoteFlashlightShadows` removed.

### E. Status
| Subsystem | Status |
|---|---|
| Drop capture + binding (original Instantiate) | IMPLEMENTED BUT UNVERIFIED — PENDING MANUAL QA |
| Refusal before inventory change (gate) | IMPLEMENTED BUT UNVERIFIED (W3Tests 22, 24) |
| Locker (W3A) | user-confirmed working in 0.10.0, code unchanged |
| Crowbar grip | DESIGN COMPROMISE, PENDING visual QA |
| Crowbar/baton swing | IMPLEMENTED BUT UNVERIFIED (procedural, no original clip exists) |
| Chest lamp | IMPLEMENTED BUT UNVERIFIED (asset source logged as `AVATAR_GEAR chest lamp from …`) |
| CarBox P1-deposit → P2-withdraw → host quit without save | HIGH-RISK, open (plan in B) |

### F. Performance
Removed: one remote spot light per visible avatar. Added: one bone rotation per frame while a remote tool is held/swung; one ownership read per snapshot (20 Hz, compiled accessor); lamp built once per avatar (police search once per process, retry ≤ 1/5 s only while no source is loaded). Drops: one extra static call per original drop, the tag scan is gone. Network: unchanged size (34 B snapshot; gear byte now carries owns-light + swing counter). **No FPS measured yet.**

### Evidence
Build 0 errors / 0 warnings. All suites pass: Protocol 19, Sync 18, World 51, Vehicle 14, Travel 34, Handoff 37, Passenger 19, Interaction 16, CarBox 12, Player2 19, Player2Lifecycle 18, JointTravel 33, WorldIdentity 16, WorldMirror 26, **W3Tests 26 (5 new W3.1 cases)**, RedirectTest pass, launcher 21. Offline tests cover the gate, capture ordering, registry and gear rules — not Unity instantiation, rendering, poses or FPS. No Codex review.

## M5 W3 — Shared hideout locker, shared dropped items, held/carried objects, flashlight — **PENDING MANUAL QA**, 2026-11-12
Mod **0.10.0, protocol 17** (both instances must run 0.10.0; one install folder serves both). Pre-deployment backup: `%LOCALAPPDATA%\TS2MP\backups\pre-0.10.0-20261112-002336` (saves, p2inv/fleet records, client sandbox, PlayerPrefs). The p2inv file is now format v2 (0.9.x cannot read it; restore the backup to roll back).

### A. Verified original mechanics (Cecil IL, Assembly-CSharp 1.27c, read-only, this milestone)
- **Locker:** the hideout locker = `CarBox` with `storage_type 1` → `PlayerScript.OpenStorage(1)` → `Inventory.OpenStorage` (`storage_Type`, `isStorage`); moves only via `Inventory.MoveItemToStorage` → `ItemID.Move_To_Storage(1)` / `Move_Out_Of_Storage(1)` (`item_amount_locker`, capacity `current_locker_capacity` / `max_locker_capacity`, same weight/max_amount/story checks as the car storage); saved by `ItemID.Save_Item_Amount` as `Locker_Item_<id>`. One per-type counter, no per-item metadata.
- **Drop:** `Inventory.DropItemNewSystem` (UI only, no code callers): `Instantiate(ItemID.drop_object, dropPos.position, drop_object.rotation)`, `Pickupable.item_ID`, `wasDropped = true`, `x10XP` if `x10xp_amount_of > 0` else `add_any_XP = false` + name `"dropped"`, `destroy_if_was_picked = false`, `SetActive(true)`, dynamic Rigidbody (continuous), then `ItemID.Drop_Item` (−1, weight; drone unassign / stolen-car sale side effects).
- **Pickup:** `PlayerScript.Update` IL_7bcd–IL_7c6c (weight / max_amount / `cant_pick_reason` checks) → `Pickupable.PickUp` → `<PickingItem>d__72` → `ItemID.Item_Picked(name, xp, wasDropped, special)` (no XP for dropped items) → `Destroy`. Only caller of `Pickupable.PickUp` is that branch.
- **Dropped items are never saved**; a MainScene reload destroys them. **Correction of earlier docs** (PROGRESS M4.3.3 A, CODEBASE_MAP): the dropped-item → locker conversion `GameManager.TryToMoveDroppedItemsToStorage` (every `Pickupable`-tagged `wasDropped && !is_heavy_item` → `ItemID.MoveToStorageOnDrop`: locker +1 unless `max_amount > 0`; the object stays) runs from `SaveGame.SaveGameNewSystem` IL_042a–IL_0448 only when `GameManager.current_location == 1` — which `GameManager.Start` sets for **Hillville** (IL_0494; Madison 0, hideout/pawn/garage/heists −1). It is not a hideout mechanism.
- **Flashlight:** owned = `item_IDs2[3].item_amount > 0`; on = `PlayerScript.torch`, toggled by Rewired `flashlight` (IL_379c–IL_3883, blocked while hacking / in a vehicle / on a call / lockpicking / on PC / hiding), visual = `PlayerScript.LightObj` (SetActive); forced off when `Inventory.hasFlashlight` is false, by `HidePlayer` and `CarController.EnterVehicle`. `AI.Update` reads the local `torch`. Lit-player detection = `ShadowDetector` (`FindObjectsOfType<Light>` filtered by `lightLayers`) → `isHitByLight`.
- **Held tools:** `PlayerScript.currentItem` (Select*: 0 hand, 1 crowbar, 2 binoculars, 3 PDA, 4 hacking laptop, 5 baton, 6 blowtorch, 7 gas tank, 8 key fob, 9 meat, 10 tranquilizer, 11 radio, 12 cloak, 99 phone, 102 brick) → `ActivateWep` activates first-person objects (`CrowbarObject`, `batonObj`, …) on the FP arm rig (legacy `L_Hand`/`R_Hand` clips). **No third-person tool attachment or holding animation exists in the game.**
- **Heavy carry:** `Pickupable.PickUpHeavy/DropHeavy`, `PlayerScript.isCarrying` + `CarryItem` (a world object moved to `carryPos`), heavy `ItemID`s never enter `item_amount`. The associate model has the original `CarryRun` clip.

### B. W3A shared locker (`CarBoxSync`, `Player2Sync`, `WorldHost.StorageRequest`)
The existing CarBox architecture serves both storages: lease `carbox` / contents `carboxItems` / request `carboxReq` with registry arg = storage kind (0 car, 1 locker); a second `CarBoxLease` (`WorldHost.Locker`), same exclusivity, session ids, renew/expiry (20 s), release on close / region change / disconnect. Client may open the locker only in the hideout region. Client moves = `storageTxn` with a kind byte (14 B; 13 B = car, M4.3.3 format kept): host validates stock, original locker capacity and backpack `max_amount`, applies record + journal (kind Locker) + host `item_amount_locker` + `Locker_Item_` key + watermark, writes p2inv **before** acknowledging; client runs the original move afterwards. Locker and car journals share the Player-2 watermark `TS2MP_StorageTxn` (both counts live in the same save memory) but replay into their own key families. Blackbay host guard extended (no host sale from the locker while P2 holds it). `Locker_Item_*` reclassified `MultiplayerOwned` in `KeyPolicy` (never mirrored).

### C. W3B shared dropped items (`src/Shared/WorldItems.cs`, `src/Plugin/WorldItemSync.cs`)
- Identity: host-assigned id per registry epoch; registry key `witems` (Region scope, arg = region) = all items of that region {id, item type, origin player, origin txn, rest pose (pos + 16-bit quaternion), simulating player} — 34 B/item, ≤ 30 items/region (1 KB value). Tracked by the host without a loaded scene.
- **Drop:** host: original `DropItemNewSystem` runs, its instantiated object is found (one `Pickupable` tag lookup per drop) and registered in place (host sequence D + watermark `TS2MP_HostTxn` + `Item_<id>` written into the save memory). Client: the original is refused, `itemTxn Drop {txn = AppliedTxn+1, item, region, pose}`; host: book check (region, limit) → Player-2 record −1 → p2inv durable → registry; on Accepted the original drop runs (same item selected) or `ItemID.Drop_Item` (counters only, object presented from the registry).
- **Pickup:** host-authoritative claim, exactly one winner (the first committed; host clicks are applied in place). Client: `itemTxn Pickup` → record +1 durable → registry removal → the original pickup coroutine runs on the client; the transaction stays pending (reports suspended) until `Item_Picked` actually counted it (fallback: original `Item_Picked` called directly after 4 s). Loser: Stale, nothing gained, object removed by the registry.
- **Cross-player crash safety (host journal kinds HostItem / HostLocker, p2inv v2):** P2 picks up P1's drop → journal "P1 backpack −1 at host seq D"; P1 picks up P2's drop → journal "+1" with the watermark written after the original counted it; P2 items converted into the host locker → "locker +1". Replayed after a host load only when the save predates them (save watermark < seq) → a host reload / quit without saving neither duplicates nor loses a transferred item.
- **Presentation:** each instance shows the items of its own region with the original `drop_object` prefab configured like `DropItemNewSystem` (kinematic rigidbody). The dropper's original object keeps its physics; its rest pose is reported once settled (checked 2×/s, own items only, re-reported only after ≥ 3 cm / 5°), committed by the host. No transform streaming.
- **Lifetime (original semantics):** items stay while anyone is in the region; when the region empties (both left, reload, disconnect with the host elsewhere) they are cleared — the original scene reload destroys drops. A committed Player-2 drop that is cleared this way is lost with the world, never duplicated (as in single player).
- **Hillville conversion:** host save in Hillville → registry items of that region converted on the host locker with the original `ItemID.MoveToStorageOnDrop` (max_amount items stay), removed from the registry exactly once; unregistered local drops keep the original loop. Client save in Hillville → `itemTxn Convert` → the host does the same; the client's sandbox locker is never used → the M4.3.3 data-loss path (P2 drop converted into the throw-away client locker) is closed.
- Not shared (original local behaviour, logged `WITEM … not shared`): heavy items, stolen cars, drones, story/mission items, items without `drop_object`.

### D. W3C held / carried objects, W3D flashlight (`PlayerState.cs`, `PlayerSync`, `RemoteGear.cs`, `RemoteAvatar`)
- Player snapshot 29 → **34 B**: camera pitch, gear flags (torch, carrying), `currentItem`, carried item id+1. State rides the existing 20 Hz sequenced stream → correct after loss, reconnect, scene reload; no separate event protocol (+5 B × 20 Hz = 100 B/s per direction).
- Held tool: the observer's own original first-person tool object (MeshRenderers only, FP arms dropped) on the avatar's `mixamorig:RightHand`, pooled per tool (no allocation on re-equip), hidden while seated. **DESIGN COMPROMISE (necessary):** the game has no third-person tool attachment/animation — placement is bounds-centred in the hand, the avatar keeps its walk/idle clips; logged as `AVATAR_GEAR held tool …` for visual QA.
- Heavy carry: original `CarryRun` clip (paused = holding pose when standing) + the carried item's original `drop_object` mesh in front of the chest. **DESIGN COMPROMISE:** the carried world object itself still stands at its original place in the observer's world (shared heavy-loot ownership = W4). Placing heavy loot in the van/trunk is not synchronized (W4).
- Flashlight: copy of the observer's original `LightObj` (light + beam mesh) on a head pivot oriented by remote yaw + pitch; off = disabled, one copy per avatar (no duplicates on repeated states), destroyed with the avatar (disconnect / other region). Remote shadows off by default (`Avatar.RemoteFlashlightShadows`, original setting logged). The copy's GameObject layer is outside every `ShadowDetector.lightLayers` → the remote beam can never make the local player "lit" for NPC AI. NPCs still detect only the local player (W8).

### E. Status
| Subsystem | Status |
|---|---|
| W3A locker lease, transfers, journal, isolation (pure) | IMPLEMENTED BUT UNVERIFIED (W3Tests 01–06) |
| W3A original locker UI on the client, contents, moves | IMPLEMENTED BUT UNVERIFIED — PENDING MANUAL QA |
| W3B registry, claims, exactly-once record, host journal, lifetime, conversion (pure) | IMPLEMENTED BUT UNVERIFIED (W3Tests 07–19) |
| W3B original drop/pickup hooks, object capture, presentation, settle | IMPLEMENTED BUT UNVERIFIED — PENDING MANUAL QA |
| W3C held tools | IMPLEMENTED BUT UNVERIFIED; placement DESIGN COMPROMISE |
| W3C heavy carry | IMPLEMENTED BUT UNVERIFIED (animation + visual only); world object/trunk = INCOMPLETE (W4) |
| W3D flashlight | IMPLEMENTED BUT UNVERIFIED; GPU cost not measured |
| Item limit 30 per region (31st drop does nothing) | DESIGN COMPROMISE (registry value size) |
| Host returns to the menu without saving after giving P1's item to P2 via CarBox (pre-existing M4.3.3 path, not the new drop path) | HIGH-RISK, unchanged: CarBox deposits of P1 are not host-journaled |
| Shared cash/bank | not started (W7, user decision recorded) |

### F. Performance (design + offline; **no in-game measurement yet**)
- Per frame: `WorldItemSync.Update` a few comparisons; the registry is read only when its head revision / region / scene changed; settle check 2×/s for own dropped items; no allocation in steady state (timestamps instead of Stopwatch objects). `RemoteGear` per visible frame: early-outs, one pivot pose while the light is on. Snapshot capture: 4 compiled field reads at the send rate; carried item resolved once per carried object.
- Events only: drop = 1 proposal (37 B) + region list delta (≤ 1 KB) + record delta + result; pickup the same; settle once per drop; locker = same messages as the CarBox. Object spawn = one `Instantiate` per remote item (per region entry), one tag lookup per own drop.
- GPU: one extra spot light per visible remote flashlight, shadows off by default. **Not measured.** Use `Debug.PerfStats = true` (PERF lines) on both instances for TESTING §20 H.

### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 34/34, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16, CarBoxTests 12/12, Player2Tests 19/19, Player2LifecycleTests 18/18, JointTravelTests 33/33, WorldIdentityTests 16/16, WorldMirrorTests 26/26, **W3Tests 21/21 (new)**, RedirectTest pass, launcher 21/21. Version-pin assertions moved to 17 / 0.10.0; `KeyPolicy` locker assertion updated.
- Deployed 2026-11-12; `TS2MP.Plugin.dll` SHA-256 `30c1708d…5c01b93` identical in `build\plugin` and the game's `BepInEx\plugins\TS2MP`.
- Offline tests prove host logic only, not Unity behaviour, original UI, physics, rendering or FPS.
- Codex review: **not performed** (last attempt failed with HTTP 401; not retried, per instructions).

## M5 hotfix 0.9.1 — Player 2 below the map after the caught-screen Load — **PENDING MANUAL QA**, 2026-11-11
Mod **0.9.1**, protocol 16 unchanged. Partial hotfix: the arrest/load spawn defect only (the hotfix prompt was received truncated at "7. Player 2 money"; money and performance sections not yet addressed).
- **Evidence (logs 2026-11-11 19:20–19:36 UTC, client `20261111-201955_client_d77c5b44`):** P2 drove the Ranger in Madison, then a MainScene reload without any travel request; `ARRIVAL MadisonSt: activating Ranger (registry)` + `ARRIVAL driver seating skipped (no travel grant)`; nothing placed the player. Menu → Continue repeated it (`activating no car (registry)`). Host: `SNAPSHOT invalid values pos=-10,01,-50026,28,145,58` (x/z = MainScene's default player position, not a Madison spawn; first invalid snapshot logged only once y passed −50 000).
- **Original mechanics (Cecil IL):** `GameManager.GotCaught(int)` (non-hardcore) shows the caught screen; its Load button = `GameManager.LoadCheckpoint` → `LoadCheckpointIE` (0.5 s) → `Application.LoadLevel("MainScene")` — **no save**; `InitializePrefs.Awake` → `FBPP.Start` re-reads the instance's save file (client: sandbox; host world untouched). `GameManager.Start` Madison IL_03f4–043a / Hillville IL_049b–04d1: with `Story > 3`, `Saved_Pos == 0` → `CarController.IsStartingCar()` is the **only** placement of the player, else `SaveGame.LoadPlayerPosition()`. `SaveGameNewSystem` writes `Saved_Pos = 1` only at save points (IL_00dc) and 0 on travel (IL_0156) → after any travel save, Load/Continue in Madison/Hillville always goes through `IsStartingCar`.
- **Root cause (mod, since M4.2):** `VehicleSync.AllowStartingCar` refuses the seating without a Carry travel grant (correct: the car may be the other player's), but `TryPlace` treated "no travel context" as "original position kept" → the player stayed at MainScene's default position, with no ground there in these maps → endless fall. Not a W1/W2 regression; latent in 0.8.0 for any load/continue into Madison/Hillville without one's own car seating.
- **Fix:** when the seating is refused without a travel grant, a recovery arrival context (Join placement) is created → the existing checked on-foot placement (near the other player / the region car / the original leave point `GameManager.leavePoints[MadisonPoint|HillvillePoint]`, ground-ray verified, not on a car) runs once the region is confirmed. Fall speed (`PlayerMotorBehavior.velocityY`) reset after every placement. `ARRIVAL_DIAG` logs reason (caught Load / menu Continue / restart), `Saved_Pos`, `Story`, player position, CharacterController state, car. Log-only postfix/prefix on `GotCaught`, `LoadCheckpoint`, `LoadMenu` label the reason.
- **Fall guard (both roles, on foot, fleet regions):** checked every 0.5 s (no per-frame work beyond a time compare); last grounded position per region; lost = airborne ≥ 3 s and > 60 m below the last ground (or below −500 without one) (`FallRules`); recovery to the last grounded position, else the original leave point, else the other player — max 3 per region visit, ≥ 10 s apart, then `FALL_RECOVERY gave up` (no loop). Never moves a player in a car or a seated passenger; never touches the other instance.
- **Load semantics in co-op (unchanged, documented):** P2's Load reloads only its sandbox checkpoint (host save and host world untouched); P2 inventory/money are re-applied from the host record in the new scene (log: `P2INV … restored`); world keys re-mirrored at the MainScene load; the driving lease was released at scene exit (`driver release sent`), region change ends any passenger ride / handoff.
- Status: IMPLEMENTED BUT UNVERIFIED. Tests: TravelTests 34/34 (new case 34 fall rule), all other suites unchanged and passing; build 0 warnings; deployed (hash verified).

## M5 W1 + W2 — World identity census & host-authoritative world-key mirror — **PENDING MANUAL QA**, 2026-11-11
Mod **0.9.0, protocol 16** (both instances must run 0.9.0; one install folder serves both). Design evidence: `docs/WORLD_SYNC_AUDIT.md` (§R addendum for what this milestone verified/corrected).

### A. W1 — world object identity (`src/Shared/WorldIdentity.cs`, `src/Plugin/WorldCensus.cs`)
- Identity of a fixed scene object = FNV-1a 32 over `<scene>/<hierarchy path>#<kind byte>`, namespaced by region (`IdentityNamespace`: region scenes → region id, `MainScene` → own namespace −1000, menu/puzzle → no census). Path segment = GameObject name, plus `[siblingIndex]` only where siblings share the name; `@k` for the k-th component of the same kind on one object. Wire id `WorldObjectId` = namespace i32 + hash u32 (8 B) — designed for W3+, **no W1 network traffic**.
- Census point: `SceneManager.sceneLoaded` (after Awake/OnEnable, **before any Start()** of that scene) → the hierarchy is the serialized asset on every instance; nothing has been destroyed yet (`Pickupable.<Start>d__68` destroys picked loot only after a 0.05 s realtime wait, VO). One run per loaded scene instance (`CensusGate`), never per frame; read-only (no enable/move/rename/destroy).
- Kinds (25, all verified type names): Pickupable, Door, Lockpick, LockedWindow, SafeMaster, SecurityCamera, SecurityPanel, LightSwitch, FuseBoxSwitch, Alarm, SelectResidence, AI, GuardAI, DogAI, PolicemanAI, SidewalkAI, StoryObject, ActivateObjectStory, Medkit, HiddenItem, MailBox, ParcelLocker, ItemSpawner, RandomFileCabinet, CopCar. Categories: Static (doors, locks, windows, safes, cameras, panels, switches, alarms, mailboxes), Persistent (loot keyed by name, residences, medkit, hidden item), Dynamic (dropped loot `"dropped"`/`wasDropped`, police, cop cars), TimeDependent (residents/guards/dogs, story objects), Unsupported (pedestrian pool, random spawners/cabinets, every ambiguous id).
- Registry: per namespace a list of (hash, kind, category, component ref) + hash index; duplicate paths and hash collisions are detected, diagnosed (≤ 32 lines) and marked ambiguous — **an ambiguous id never resolves**. Paths exist only during the census (and in the optional export) → steady state ≈ 24–50 B per object. Tables dropped on single scene load / scene unload; destroyed objects are released on lookup. Digest (order-independent) per namespace logged; `stable=yes/NO` compares with the previous load in the same process; compare host vs client logs for cross-instance stability.
- Loot names: the census counts loot objects whose names are shared (= one shared `ItemDay_<name>` key in the original) — answers audit question P1 in game.
- `DynamicIdAllocator` (owner slot << 24 | counter) defined for future runtime entities; not used for sync.

### B. W2 — ownership matrix (`KeyPolicy` in `src/Shared/WorldMirror.cs`; evidence = Cecil scan of all 801 `SPrefs`/`FBPP` call sites)
| Owner | Families (first match wins) | W2 |
|---|---|---|
| Host world, **mirrored** | `ItemDay_<objectName>` (loot respawn day; only writer `Pickupable.SetDayRespawn`; reader `Pickupable.<Start>`), `DailyEvent<0-999>`, `EventDay`, `GeneratedEvents` (`DailyEventManager`), `CurrentHotItem`, `CurrentDayHotItem` (`HotItemManager`) | snapshot + delta |
| Host world, live value | `Inventory.daysInGame` (key `DaysInGame` only written by `SaveStats`) | in every snapshot/delta header; client field + key aligned |
| Host world, deferred | `Story`, `StoryBackup` (W6: `InitiateStory` side effects), `Debt`, `RentTimer` (W7) | not mirrored |
| Player own | `Item_<numeric id>`, `Money`, `BankBalance`, `BankIncrease`, `BatonLife`, `CrowbarLife`, `Item_Discovered_*`, `Item_Value_*`, `TotalHoursPassed` | never touched (P2 via `Player2Sync`) |
| Multiplayer-owned | `Storage_Item_*` (CarBox), `CurrentCar`, `GasTankLeftCar`, `VanBought`, `TrunkItem` (fleet), `GameTime`, `Day`, `Week` (clocks/calendar) | never touched |
| Reserved | `Locker_Item_*` (storage milestone), `Item_<objectName>` (=1 on **every** pick, same prefix as backpack counts → W3) | never touched |
| Instance-local | `CurrentLocation`, `Saved_Pos`, `MadisonPoint`, `HillvillePoint`, `RandomVan`, `NewDay`, `Medkit` | never touched |
| Unknown | everything else | never mirrored (both sides validate every key) |

### C. W2 — protocol and rules (`WorldKeyMirror` plugin, `HostMirror`/`ClientMirror` pure)
- Messages on channel 0 ReliableOrdered: `WorldKeySnapshot` (10), `WorldKeyDelta` (11), `WorldKeyResync` (12, client → host, 14 B). Header: version 1, epoch u32 (random per host process), profile i32 (PlayerPrefs `Profile`), generation u32 (host save load counter), rev u32, days i32. Entry: family u8 + suffix string + i32 (delta: + op set/delete). Limits 8192 entries, 256 KiB, suffix ≤ 120 chars; any non-allowlisted, duplicate or malformed entry rejects the whole message; decoders never throw (fuzz-tested).
- Host: `HostMirror` = live SPrefs memory of the allowlist (unsaved values included). Changes via postfixes on the verified writers (`Pickupable.SetDayRespawn`, `DailyEventManager.SetNextDay/GenerateMonthEvents/SetTodayAsBurglarDay`, `HotItemManager.NewRandomItem`), coalesced into **one delta per frame at most**; a 60 s audit rescan (one pass over `FBPPFileModel.IntData`) catches unhooked writers (logged `MIRROR_AUDIT`). `FBPP._latestData` replaced (only `FBPP.Start` = `InitializePrefs.Awake`, `DeleteAll`, `OverwriteLocalSaveFile`, lazy load — VO) → new generation + full snapshot. Snapshot on connect / resync request. Expired loot markers (`value <= daysInGame`) are pruned on both sides with the same day count (no traffic).
- Client: incremental writes for deltas into its **sandbox** SPrefs memory; full reconcile at every `MainScene` single load (`sceneLoaded`, before the original objects read keys in `Start`), after a snapshot and right after its own game rewrote a daily/hot family (client rollover → host values re-asserted). Rule per key: host has it → host value; else the client's own loot pick (`SetDayRespawn` on the client marks it local, ≤ 4096) is kept; else (seed/old mirror value) deleted. Gap / other history → `WorldKeyResync` (≤ 1 per 2 s); duplicate → ignored; stale snapshot → ignored.
- Save semantics: the host save is never written by the mirror; the client sandbox is never copied to the host (SaveGuard) and is re-seeded at client start → a mirrored but unsaved host change cannot become durable through the client. Host returns to the menu without saving → FBPP reload → new generation → client follows the rolled-back state. Player-2 inventory keeps its own (non-rollback) host record.

### D. Hourly / daily effects (`HourlyEffects` table)
Day count → host live value; daily NPC events + hot item → host-authoritative keys (client values re-asserted); weekday/week/hour → existing M3.1 calendar/region clocks. **Unchanged, W7:** rent timer + rent charge (still runs on both instances, client charges Player 2's money), bank interest (per player, possible double tick), fuel refill charge. Ability cooldowns / total hours = per player.

### E. Status
| Subsystem | Status |
|---|---|
| W1 identity rules, registry, collision/ambiguity handling, gate (pure) | IMPLEMENTED BUT UNVERIFIED (16/16 offline tests) |
| W1 census in Unity (cost, counts, cross-instance/reload stability) | IMPLEMENTED BUT UNVERIFIED — **no in-game census measured yet** |
| W2 ownership policy + codec + host/client logic (pure) | IMPLEMENTED BUT UNVERIFIED (26/26 offline tests incl. 20 000-message fuzz) |
| W2 FBPP access, writer hooks, scene-load reconcile in Unity | IMPLEMENTED BUT UNVERIFIED |
| Live refresh of already-started objects (current NPC daily event, hot-item shop flag, loot already destroyed/spawned in the open scene) | INCOMPLETE by design: values take effect at the next scene load; live object sync = W3/W5/W8 |
| Client loot picks never reach the host (host world unchanged; client keeps its own marker) | DESIGN COMPROMISE (necessary until W3 shared loot) |
| `Item_<objectName>` unique-loot flags not mirrored | DESIGN COMPROMISE (necessary: written on every pick, prefix shared with backpack counts → W3) |
| Rent / bank interest / fuel charge on the client | HIGH-RISK, unchanged (W7) |
| Whether `InitializePrefs` also exists in MainScene (would make every travel a host reload → full snapshot instead of no traffic) | UNKNOWN — visible as `MIRROR_BASE … (poll)` per travel in the host log; correct either way |
| Debug.PerfStats frame statistics | IMPLEMENTED BUT UNVERIFIED |

### F. Performance design and measurements
- Hot paths added: `NetRunner.Update` (one null check when PerfStats is off), `WorldKeyMirror.Update` (a few comparisons + 1 Hz field poll; no allocation, no reflection in steady state; host scan only on writer events and every 60 s), writer postfixes (loot pick / day rollover only), census (scene load only). Network: nothing in steady state; one snapshot per connect/host save load; one small delta per loot pick or day rollover.
- Offline (.NET Framework, not Unity): 100 k identity lookups 3.0 ms, 0 GC; client reconcile plan for 1 530 world keys 0.10 ms; host audit 0.16 ms; key classification 0.03 µs/key; snapshot of 2 000 loot markers 48.5 KB (24 B/key); gen-0 GCs in the steady-state loops: 0.
- In game: `WORLD_CENSUS … time=… heapDelta~… gc0+…`, `MIRROR_APPLY … time=…`, `MIRROR_STATS … frame/scan/apply avg/max us`, and with `Debug.PerfStats = true`: `PERF frames= avgFps= p50/p95/p99 low1% low0.1% max | mod cost | gc | heap | net B/s | focus vsync targetFps res`. **No in-game FPS measured; no v0.8.0 baseline exists** (0.8.0 had no frame statistics) → A/B via `World.IdentityCensus=false` + `World.KeyMirror=false` on 0.9.0 (≈ 0.8.0 behaviour for these features).

### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16 (no-UI-text scan passes), CarBoxTests 12/12, Player2Tests 19/19, Player2LifecycleTests 18/18, JointTravelTests 33/33, **WorldIdentityTests 16/16 (new)**, **WorldMirrorTests 26/26 (new)**, RedirectTest pass, launcher 21/21. The fuzz test found that LiteNetLib throws `DecoderFallbackException` on invalid UTF-8 → mirror decoders made exception-safe. Deployed 2026-11-11, plugin hash verified. Backup before deployment: `%LOCALAPPDATA%\TS2MP\backups\pre-0.9.0-20261111-200632` (saves + records).
- Offline tests do not prove Unity scene reconstruction, census stability or FPS.
- Codex read-only review: attempted once, failed (HTTP 401 `workspace routing discovery unauthorized`, Codex read no files); not retried. Claude self-review only (not independent).

## M4.3 Phase B — Joint two-player vehicle travel (driver + passenger) — **PENDING MANUAL QA**, 2026-11-11
Mod **0.8.0, protocol 15** (both instances must run 0.8.0; one install folder serves both instances). Replaces the Phase A refusal "passenger aboard: joint travel comes in the next stage".

### A. Verified original travel path (Cecil IL, read-only, this milestone)
- Menu openers: `MapBorder.OnTriggerEnter` IL_0000–IL_003c calls `PlayerScript.OpenTownSelectionNoDelay()` for any collider tagged `Player` or `PlayerMapTrigger` (unless a drone is active); `OpenTownSelectionNoDelay` with `current_vehicle == null` starts `ShowTownSelection(0)` (IL_0100) → a seated mod passenger's own `Trigger Map` child could open the menu on the passenger's instance.
- `GameManager.SelectTestMap` (Madison) / `SelectHideout`: `timeScale 1`, `TownSelectObj` off, `RandomVan`, `Saved_Pos 0`, `CurrentLocation`, `SaveGame.SaveGameNewSystem(false,-1)`, `StartCoroutine(LoadWait())` — no dependency on the menu being open. `LoadWait` (`<LoadWait>d__331.MoveNext`): `PauseCar` only if `PS.is_in_vehicle`, `PCB` off, `LoadPanel.Play("ShowLoad")`, random tip, `WaitForSecondsRealtime(0.35)`, `timeScale 0`, `LoadMain()` → `LoadYourAsyncScene`.
- Arrival (unchanged, CODEBASE_MAP M4.1A audit): `GameManager.Start` → `InitiateMainCar` → Madison/Hillville `IsStartingCar` seats the driver; Hideout/Auto Shop/Verwood spawn the driver on foot at the garage.

### B. Root cause of the previous restriction
Phase A deliberately refused (`WorldHost.SeatTravelBlock`, plugin `SeatTravelBlock`): the M4.2 travel desk reserves the car for the driver only; the passenger's instance never left its scene, `SetPlayerRegion(None)` → `FleetBook.ReleaseAll` dropped its seat, and the car record moving to the destination made the passenger's car "not presented" → forced exit. Nothing carried the passenger.

### C. Design (host-coordinated logical transaction, `src/Shared/Travel.cs`)
- `JointTrip` registry entry `jointTrip` (Session, arg 0, 18 B, host-committed only): trip id (host counter per registry epoch), car, driver, passenger, origin, destination, phase, progress bits. Phases `Authorized → Travelling → Arrived → Committed`; terminal `Cancelled` (before the passenger departed) / `Failed` (after). Terminal phases never reopen; ids only grow.
- Decision: the driver's normal travel click (client proposal `travel` / host in place). `WorldHost.TravelCheck`: a passenger never chooses a destination; a driver with a **confirmed** passenger travels jointly only when the TravelDesk would `Carry` that car (`TravelDesk.WouldCarry` mirrors `Decide`), the passenger is in the car's region and neither has the CarBox open; otherwise `Busy`, nothing committed, the original menu stays open (no message). The trip entry is committed **before** the car record moves (a split delivery never shows a moved car without its trip). The driver's own path is the unchanged M4.2 `Carry` (grant, resume, `IsStartingCar`, lease claim).
- Passenger participation: the host marks `DriverLeft` when the driver's region becomes None/destination → `Travelling`; only then does the passenger's instance run **its own original `GameManager.Select<Dest>()`** (once per trip id, via the patched method and the existing one-shot continuation guard) → original save, loading screen and MainScene reload. A driver travel cancelled before departure therefore never moves the passenger.
- Seat retention: `FleetBook.ReleaseAll(slot, outbox, keepPassengerOf)` keeps the trip car's passenger seat while that passenger loads (region None); at the destination the car region already matches. Every terminal path except a seated commit releases the seat (`JointTravel.End`).
- Arrival readiness (`JointRules.ReadyToSeat`, independent per participant, no fixed delays): destination scene, car record at the destination, not reserved, holder = driver = trip driver (driver arrived, seated by the original `IsStartingCar`, lease claimed), passenger seat still this player, local original car object observed with its seat anchor, and a driver snapshot received after this instance arrived. Then the established `EnterSeat` path runs (seat anchor, first-person seat camera, `RideFrame` switches to the driver's current camera mode from the record, confirm `vehicleRide` op 2 → host `Unchanged` → `PassengerSeated` → `Committed`).
- Host failure handling: passenger disconnect → `Failed`/`Cancelled`, seat released, driver continues alone; driver disconnect → TravelDesk rollback (car back in the origin if not yet committed, no driver, holder by the normal rule — nobody is promoted to driver) → trip ends; reservation rollback/cancel → trip ends (bound to the driver's request id); wrong region of either → `Failed`; passenger quits before the driver left → `Cancelled`; timeouts `TripTimeout` 150 s, `ArrivedTimeout` 30 s. Passenger side (`JointRules.Passenger`): `Hold` / `Depart` / `Wait` / `Seat` / `Recover`; local timeouts 40 s after arrival, 20 s if the scene never changed; disconnect → forward recovery on foot (checked placement near the car, `TryPlace` Join context).
- Freeze during the trip: passenger exit input ignored while held, CarBox `Open` refused for both participants (host), no new travel/second trip (`Busy`), car `Reserved` refuses seat requests and handoffs, new narrow `PlayerScript.OpenTownSelection*` prefix blocks the menu on a seated passenger's instance (the original companion seat never travels by itself).
- Inventory/save: unchanged paths. The passenger's `Select` runs `SaveGameNewSystem` (client: sandbox; host passenger: its own save, as when it travels alone) → existing Player-2 exit capture + restore in the next scene; the trip never writes `p2inv`/CarBox records; no extra saves or snapshots.
- Latent fix: the host's own travel decision now uses the WorldSync clock (`_world.Clock`), the clock `WorldHost.Tick` expires reservations/trips with (it used VehicleSync's separate stopwatch).

### D. Status
| Subsystem | Status |
|---|---|
| Host trip state machine, validation, seat retention, recovery (`JointTravel`, `WorldHost`) | IMPLEMENTED BUT UNVERIFIED (offline tests pass) |
| Passenger departure through its own original `Select<Dest>` | IMPLEMENTED BUT UNVERIFIED (IL shows no menu dependency; not run in Unity) |
| Passenger re-seat at the destination (both cars, both roles) | IMPLEMENTED BUT UNVERIFIED |
| Menu guard for a seated passenger | IMPLEMENTED BUT UNVERIFIED (whether `MapBorder` fires for the passenger's `Trigger Map` is unknown; the guard is harmless either way) |
| Arrival at Hideout / Auto Shop / Verwood: both on foot | DESIGN COMPROMISE (necessary): the original arrival puts the driver on foot there; the passenger follows the same rule |
| Passenger sees its frozen on-foot spawn until the car is live (≤ the driver's remaining load time) | DESIGN COMPROMISE (necessary): the original scene load always creates an on-foot player; it is frozen (no movement/interaction) and shown to the other player as seated |
| Refused joint travel (occupied destination, special destination, storage open, seat still entering) | the click does nothing, like other refused travel (no mod text) — HIGH-RISK for confusion, logged |
| Driver avatar disappears on the passenger's screen during the ≤ 1 s hold (car record already names the destination) | known cosmetic effect |

### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19 (case 15 rewritten to the Phase B rule), InteractionTests 16/16 (no-UI-text source scan passes), CarBoxTests 12/12, Player2Tests 19/19, Player2LifecycleTests 18/18, **JointTravelTests 33/33 (new)**, RedirectTest pass, launcher 21/21. Deployed 2026-11-11 (plugin hash verified in `BepInEx\plugins\TS2MP`). Offline tests do not prove Unity scene transitions.
- Cost (offline, .NET Framework, not Unity): per-frame passenger decision ≈ 5.6 ns, 0 GC; `JointFrame` returns after one field check when this player is not the passenger of an active trip. Network: 6 `jointTrip` entries × 18 B = 108 B per trip; no change to driving traffic or snapshot size. No scene searches and no per-frame reflection (the Select `MethodInfo` is resolved once at hook install). **No in-game FPS measured.**
- Diagnostics: host `TRAVEL JOINT …` phase lines with durations; passenger `JOINT …` lines (hold, departure, load time, readiness time, re-seat, recovery); `VEHICLE_STATS … joint holds/departures/departFailed/seated/onFoot/recovered/menuBlocked loadMax readyMax` and host `trips started/committed/cancelled/failed/refused lastTrip`.
- Codex read-only review: attempted once, failed (HTTP 401 `token_revoked`); not retried. Claude self-review found and fixed: (1) trip entry committed after the car record (a split delivery could force the passenger out) → trip first; (2) a superseded older reservation could end a new trip → trip bound to the driver's request id; (3) the menu guard also blocked on-foot players with a seat request in flight → seated passengers only. Not an independent review.

## M4.3.3 hotfix — Player-2 inventory restoration, reliable host persistence, CarBox recovery — **PENDING MANUAL QA**, 2026-11-11
Mod **0.7.1, protocol 14** (both instances must run 0.7.1). Trigger: 0.7.0 **FAILED manual QA** (logs `LogOutput.log` host / `LogOutput.log.1` client, plugin logs `20261111-052947_host_e459b88f` / `…052954_client_59752907`). No new features; Phase B not started; the M4.3.3 sleep presentation is unchanged and still **unverified**.

### A. Root cause of `restored False` (verified)
- Host: created the record (`P2INV new Player-2 inventory for profile 0`), committed it to the registry; the client received the snapshot and every delta (`WORLD_STATS … entries=10 … gaps=0`), so transport, registration, identity and ordering were fine.
- `P2Inventory.TryRead` required `AvailableBytes >= 31`, but the v1 encoding of a record with an **empty backpack** is exactly 27 bytes (1 version + 6×4 fields + 2 count). Every new Player 2 (empty backpack by design) was rejected by `TryParse` → `Player2Sync.TryRecord` returned false every frame → `Restore` never ran → silent (no log). The offline tests only round-tripped records with items.
- Same defect on the host: after a host restart its own `p2inv-<profile>.dat` would have been "unusable" → Player 2 reset to a new game at every host start.
### B. Why the host received zero reports
Reports were gated on `_restored` (client) — never true, so `ReportIfChanged` never sent anything. A report with an empty backpack would also have been refused by the host (`Invalid`, same parser).
### C. Why the flashlight disappeared (verified IL + code)
1. The client was never restored, so it played on the sandbox copy of Player 1's save; the purchase was never reported → the host record stayed `money 200 items 0`.
2. Original semantics: `Shop_Manager.Buy_Item` only changes fields (`ItemID.item_amount`, `PlayerScript.moneyAm`; no SPrefs, no disk); `GameManager.LoadMenu` IL_0011 calls `SaveGame.LoseGame` (empty body) and loads `MainMenuNew` — nothing is saved. In single-player a purchase without a later save/travel is lost on "main menu"; the rejoin re-read the sandbox prefs (without the flashlight).
3. Latent second defect (would have hit even with the parser fixed): 0.7.0 restored **once per process**. After a menu return the scene re-reads the older prefs; the client would then have reported that older state back and the host would have accepted it → rollback. A report was also fire-and-forget (only in `Dispose`), and nothing ran on a menu exit.
### D. Persistence behaviour chosen (user decision: the host saves the session)
- Host world save: unchanged original checkpoints (FBPP writes by the host's own save/travel actions); never forced by Player-2 activity.
- Player 2's record: host-owned file `p2inv-<profile>.dat`. A client report is now **written durably (atomic replace) before the host acknowledges it** (failed write → record undone, `Busy`). An acknowledgment therefore means "persisted on the host"; Player 2 never needs its own save action.
- Deliberate difference to single-player: Player 2's legitimate changes persist without a checkpoint (the user requires that Player 2 never saves). Player 1 keeps the original checkpoint semantics.
### E. Synchronization lifecycle (`src/Shared/Player2Client.cs`, plugin `Player2Sync`)
- States logged on transition: `Offline → WaitingRecord → WaitingLocal → Acknowledging → Ready` (`Failed` = visible error `P2INV_FAIL`). CarBox and reports only in `Ready`.
- **Restore in every scene** (new GameManager: first join, travel, main menu → continue, reconnect), then an acknowledgment report; the host logs `P2INV client synchronized`.
- Records carry an **id** (= host save history id) and a revision; a report names the record it is based on. The host accepts only a report based on exactly its current record (same id, revision, transfer id); equal content = `Unchanged`; anything older = `Stale` (never overwrites). One report in flight at a time.
- **Exit synchronization:** Harmony prefix on `SaveGame.LoseGame` (main menu, quit button, application quit — verified callers `GameManager.LoadMenu` IL_0011, `Inventory.QuitGame` IL_001f, `SaveGame.OnApplicationQuit` IL_000b) and `SaveGame.SaveGameNewSystem(bool,float)` (travel, quicksave): the live Player-2 state is captured while the scene exists, sent, and kept as **pending** (memory + `%LOCALAPPDATA%\TS2MP\p2pending-client.dat`, atomic) until committed. Before the next scene is restored, a pending state is committed first — only if the host record is still the one it is based on (or the one its in-flight predecessor produces); otherwise the newer host state wins and the pending is discarded (`superseded`, logged). The main thread never waits.
- Recovery: missing record → at most 3 resync requests (8 s apart) per connection; unanswered report → retry after 5 s; `Busy` → retry after 2 s; at most 5 attempts, then `Failed` until the next scene/reconnect. No infinite loop.
### F. CarBox
Guard kept (`Restored` = state `Ready`). Refusal log rate-limited (one line per 10 s with a counter). A transfer first sends the newest Player-2 state on the same ordered channel and waits if a report is still out; `Rebase()` removed (the client's comparison base is now always the host record from the registry, so an unreported change can no longer be hidden by a transfer).
### G. Save/profile isolation
Unchanged: client sandbox redirect (SaveGuard), host-only p2inv files per profile, history id / watermark checks, journal replay. New client file `p2pending-client.dat` lives in the mod data dir, never in a game save; it is applied only to a record with the same id (a host new game discards it).
### Status
| Subsystem | Status |
|---|---|
| Empty-record codec, v2 record id, report base revision | IMPLEMENTED BUT UNVERIFIED (offline tests pass) |
| Restore per scene + acknowledgment, state machine, bounded recovery | IMPLEMENTED BUT UNVERIFIED |
| Durable host write before report acknowledgment | IMPLEMENTED BUT UNVERIFIED |
| Exit synchronization (LoseGame / SaveGameNewSystem) + pending file | IMPLEMENTED BUT UNVERIFIED |
| CarBox access/transfers for Player 2 | IMPLEMENTED BUT UNVERIFIED (blocked in 0.7.0 by A) |
| Sleep presentation (M4.3.3) | unchanged, IMPLEMENTED BUT UNVERIFIED |
| Actions before the first restore of a scene (normally < 1 frame after `loadedMap`) | DESIGN COMPROMISE (necessary): overwritten by the restore — never duplicated, possibly lost |
| Client crash (not a normal exit) | changes of the last ≤ 2 s before the periodic report can be lost, never duplicated |
### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16, CarBoxTests 12/12, Player2Tests 19/19, **Player2LifecycleTests 18/18 (new: real message sequencing through WorldHost + client registry + state machine + simulated original game)**, RedirectTest pass, launcher 21/21. Mutation check: restoring the old 31-byte minimum makes test 01 fail. Deployed 2026-11-11. Offline tests do not prove Unity scene timing.
- Cost: one purchase = report + record delta + result = 3 reliable messages, 133 B; no traffic while unchanged; `P2ClientSync.Tick` 0.003 µs/frame offline, 0 GC; registry record parsed once per registry revision (cached); capture only every 2 s / at exits (max time in `P2INV_STATS captureMax`); host = one synchronous p2inv write per committed report (`syncWrites … max …ms`); client file write only at exits with unconfirmed state (`pendingWrites … max`). **No in-game FPS measured.**

## M4.3.3 — Durable Player-2 inventory, client CarBox transfers, Blackbay/locker guards, co-op native sleep presentation — **0.7.0 FAILED MANUAL QA 2026-11-11 (Player 2 never restored, purchases lost) → corrected in the hotfix above**, 2026-11-11
Mod **0.7.0, protocol 13** (both instances must run 0.7.0). 0.6.3 confirmed by the user (vehicles, passengers, prompt, shared CarBox view/lock). Phase B (joint travel) not started.

### A. Verified ownership (Cecil IL, read-only)
- Player-owned save data: SPrefs `Item_<id>` (backpack, `ItemID.item_amount`, read in `ItemID.Start`, written by `Save_Item_Amount` for `PlayerScript.item_IDs` in `SaveGame.SaveGameNewSystem`), `Money` (`moneyAm`, `SaveGame.LoadGame`), `BankBalance` (SPrefs only: Darkbank deposit/withdraw, daily interest in `DayNightTime.Update`), tool wear `BatonLife` / `CrowbarLife`. Items have no other metadata.
- World-owned: `Storage_Item_<id>` (shared CarBox), `Locker_Item_<id>` (hideout locker; dropped items are moved there by `GameManager.TryToMoveDroppedItemsToStorage` → `ItemID.MoveToStorageOnDrop`), `Debt` (story debt, new game 500000), rent timer, missions, world objects.
- Disk writes: `FBPP.Save` (via `SPrefs.Save`, `GameManager.SaveAll`, `InitializePrefs.RestoreSaveData`, `SaveGameNewSystem`, credits) and `FBPP.SaveToMainProfile`.
- Before 0.7.0 the client's Player 2 started every session as a copy of Player 1 (`SaveGuard.SeedSandbox` overwrites the sandbox) → items/money of Player 1 duplicated, Player 2's own progress lost at every restart.
- Blackbay (`BlackBay_Object.SellIt`) pays first, then removes each required item from backpack → locker → car storage (`ItemID.Remove_Item_Blackbay`): on the client the local car-storage/locker copies could be sold (bypass of the shared CarBox).
- About 40 game methods write `moneyAm` (shops, Blackbay, missions, payphone, contracts, Darkbank, car upgrades, renovations, rent in `SaveGame.Update`, …).

### B. Architecture (chosen) and rejected alternatives
- **Player 2 = a host-owned record per host save profile** (`%LOCALAPPDATA%\TS2MP\p2inv-<profile>.dat`, atomic temp+replace+`.bak`, FNV checksum): backpack counts, Money, BankBalance, BatonLife, CrowbarLife, `AppliedTxn`. Registry `p2inv` delivers it to the client. The client keeps playing with the original game code; after its save is loaded it **restores** the record into its sandbox (SPrefs + live `ItemID`/`PlayerScript` fields, once per process) and **reports** its Player-2 state when it changed (2 s check + before every transfer). The host refuses any report whose `AppliedTxn` differs from the record (a report can never overwrite a transfer).
- **CarBox transfers of Player 2 are host transactions** (`storageTxn`, inside the client's own lease session, next transfer id only): the host validates against its real counters (stock, original car capacity), applies record + journal + host storage, writes the p2inv file **synchronously before the acknowledgment** (a failed write undoes everything). The client moves nothing locally before the acknowledgment; then the original `Inventory.MoveItemToStorage` runs (same item still selected → original move, UI refresh, sound) or the original unit move `ItemID.Move_To_Storage(0)` / `Move_Out_Of_Storage(0)`.
- **Crash consistency across the two host files:** the host save carries a watermark `TS2MP_StorageTxn` (last Player-2 transfer contained in its `Storage_Item_` counts; written in memory together with the counts, so the original FBPP write stores both atomically). After any load the journal entries **above** the loaded watermark are replayed; after an FBPP write the entries up to it are dropped. A save history id `TS2MP_P2History` (written into the host save, confirmed after the first FBPP write) plus the watermark detect another history (new game / free mode in the slot, older backup) → the old record is quarantined, Player 2 starts fresh. (Two extra SPrefs keys in the host save; the save format itself is unchanged.)
- Rejected: the client persisting its own inventory (no atomic transfer across two processes; the host could not verify anything); hooking every mutation path (≈ 40 money writers + many item paths – any miss = loss/duplicate); synchronizing the whole save (most of it is world state).
- **Initial state** (first join for a profile, or other history): original new-game player state — empty backpack (the game's own `debug_setToOne` items still appear), tool wear 100, money 500 (`MainMenu.StartNewGame`) / 200 (`StartFreeMode`). Never a copy of Player 1. Existing temporary client sandboxes are **not** merged (they were copies of Player 1).

### C/D. Supported Player-2 operations
| Operation | Path | Status |
|---|---|---|
| Pickup, use/consume, drop (out of the backpack), shop purchase, shop/Blackbay sale, Darkbank deposit/withdraw, tool wear | original game code on the client → report (2 s / before a transfer / at shutdown) → host record | IMPLEMENTED BUT UNVERIFIED |
| CarBox deposit / withdrawal | host transaction (above) | IMPLEMENTED BUT UNVERIFIED |
| Blackbay sale | allowed only from Player 2's own backpack (client); refused while the CarBox is open on the client; host sales that would take car-storage items while Player 2 uses the CarBox are refused | IMPLEMENTED BUT UNVERIFIED |
| Hideout locker (type 1) | host world storage, not present in the client (client copy cleared, client locker moves refused) | **INCOMPLETE** — shared locker = future milestone |
| Dropped items moved to the locker by the game (`TryToMoveDroppedItemsToStorage`) on the client | land in the client's local locker copy → gone at the next start | **INCOMPLETE** (world-object / shared locker dependency) |
| World loot authority | the same world object exists in both instances' worlds (pre-existing) | dependency: world-object authority milestone |
| Economy paths acting on Player 2's money in the client's world (rent in `SaveGame.Update`, car upgrades, renovations, Shadowhawk purchase, mission/contract rewards) | run on the client's world copy and now persist in Player 2's money | **HIGH-RISK / dependency**: shared economy milestone (e.g. rent may be charged to both players) |

### E/F. CarBox (kept from 0.6.3, extended)
Exclusive host lease unchanged (sessions, stale/duplicate rejection, release on close/disconnect/region change/expiry). A client transfer counts as a lease renewal (a working session is never revoked). Client opens only after its Player-2 inventory was restored. The host's contents written into the client for a session are cleared again when the session ends.

### H. Duplication/loss guarantees (and limits)
- Retransmission / duplicate transfer → answered `Unchanged`, never applied twice; out-of-order id → refused. Lost acknowledgment → the host record (`AppliedTxn`) decides after reconnect (committed → applied locally; not committed → dropped).
- Host crash after a transfer's p2inv write, before the game save → replay restores the storage; record already has the item → conserved. Host crash before the write → no acknowledgment, nothing changed anywhere (client resyncs if needed).
- Client crash after an acknowledgment → the next restore brings the committed state. Client changes not yet reported (≤ 2 s, or offline play without the host) are lost like unsaved single-player progress, never duplicated.

### I/J. Sleep — root cause and implementation
- **Root cause (logs + code):** M3.1.2 never showed the original sleep presentation on the non-sleeping player; it only sped up the day cycle (`GameTime.cycleLengthInMinutes`). That substitute refused unless the day length was exactly the nominal 28 min and the time mode 1/2 — it ran in Madison (ids 26/28) and refused in Hillville (ids 54/56: "time-lapse unavailable: catch-up after completion"). Hosts that sleep themselves were never shown on the client at all.
- **Fix (`src/Plugin/SleepPresenter.cs`, `WorldSync` "co-op sleep presentation event"):** the host commits `sleepShow` per region (event id, sleeper, target hour, active) for its own sleep and for the client's accepted sleep. Every other player in that region runs the **original PassTime presentation** (`Begin` with the sleep camera at the camera the player was looking through, `SetSleepValue` = target, `PassStart`; `PassTime.Update` runs the original fast-forward and filters to the target; early end → original `StopIt`; restore = `QuitIt`'s steps without its bed/car-driver dereference). Host observer: its PassTime fast-forward is the region clock (watched, no second advance; stops at the sleeper's end when the sleeper aborted). Client observer: follower paused while presenting, `DayNightTime` hourly tick suppressed (prefix), clock re-adopted afterwards. Driver observer: EVP input disabled during the presentation (no driving under the sleep camera), seats/camera mode untouched; passenger: interaction stays off, camera switches deferred; on foot: movement frozen. Not possible (paused/UI, own sleep, moving car it drives, survival rule) → logged, previous behaviour kept (day-speed time-lapse / catch-up). `SleepHooks.Observer` keeps the presentation from being treated as a local sleep (no extra sleep proposal).

### L/M. Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16, CarBoxTests 12/12, **Player2Tests 19/19 (new)**, RedirectTest pass, launcher 21/21. Deployed 2026-11-11. Offline only — no Unity UI, FBPP, PassTime or physics.
- Offline cost: transfer decision 0.12 µs (one record clone), p2inv parse ~20 µs (1000-entry journal, worst case). Runtime: client dirty check every 2 s (one pass over `item_IDs`, one small allocation), report ≤ 931 B only on change; transfer = 13 B proposal + ≤ 931 B record delta; host transfer = one synchronous p2inv write (measured in `P2INV_STATS syncWrites … max …ms`), reports written in the background (≤ 1/s). Sleep: one 7 B event per sleep start/end, no per-frame traffic. **No in-game FPS measured.**
- Codex review: **not performed** (the token refresh was revoked at the last attempt; not retried per instructions).

## M4.3.2b — Native passenger entry prompt, shared CarBox lease with host-authoritative contents — **verified by the user 2026-11-11 (prompt, lock, contents); client moves completed in M4.3.3**, 2026-11-11
Mod **0.6.3, protocol 12** (both instances must run 0.6.3). 0.6.2 confirmed by the user: reliable E entry, E exit, Ranger left-rear exit, no front-door animation, driving/handoff/occupancy, doors.

### A. Missing entry prompt — root cause (verified)
0.6.2 client log: every proximity entry `ray None`. When the original interaction ray hits nothing, `PlayerScript.Update` IL_7f5a fades the whole prompt canvas `alphaInteractPrompts` to 0, so the `CarEnterInfo` object the mod activated was invisible (it only showed when the ray happened to hit some non-interactive collider).
**Fix:** while passenger entry is valid, LateUpdate keeps `alphaInteractPrompts` at 1 and switches off the original target prompts that only the ray-hit branch resets (IL_479b–IL_7f5a: `universalInteractPrompt`, `UsePC`, `InteractInfo`, `DoorOpenInfo`, lockpick/keyfob/weld/… prompts — no original target is focused, otherwise the passenger hint is false), and shows the original `CarEnterInfo` object (the game's own text and binding display, no new UI). The prompt = `FootAction.RequestPassenger` of the same probe the E press uses (prompt and action cannot disagree), re-evaluated every frame while shown (10 Hz otherwise), 0.1 m hysteresis used by both prompt and E, cleared whenever the foot probe does not run (seated, request pending, scene load, travel, other region, disconnect). Original door/tailgate/CarBox/medkit targets keep priority (their own prompts).

### C. CarBox data model (verified IL)
`CarBox` (tag `CarBox`, `storage_type` 0) → `PlayerScript.OpenStorage(0)` → `Inventory.OpenStorage` (pause, `isStorage`) → UI built by `Inventory.LoadInventory` → `ItemID.Load_Item` from the in-memory counters of `PlayerScript.item_IDs2`. Storage = per item TYPE counter `ItemID.item_amount_storage` (global per save, both cars; locker = `item_amount_locker`, type 1). **No per-item metadata/condition exists.** A move = `Inventory.MoveItemToStorage` (checks car capacity `max_storage_capacity`, backpack `maxWeight + 5`, `max_amount`, story items) → `ItemID.Move_To_Storage(0)` / `Move_Out_Of_Storage(0)` = exactly ±1 unit of one type on `item_amount` and `item_amount_storage` + weights; the only other storage writer is `ItemID.Remove_Item_Blackbay` (Blackbay sale) and `ItemID.Start` (load from SPrefs `Storage_Item_<id>`). Persistence: `ItemID.Save_Item_Amount` in the original save lifecycle (`SaveGameNewSystem`).

### Blocker for client item moves (hard limitation, evidence)
The client's save is a sandbox that `SaveGuard.SeedSandbox` overwrites from the host's real save at **every client start** (`File.Copy(..., true)`). The client's backpack (`item_amount`) therefore has no durable owner: it starts each session as a copy of the HOST's backpack and every client change is discarded at the next start. Consequences for a client transfer, whatever the network protocol: **client withdrawal** = the item leaves the host's real storage (persistent) and lands in a backpack that is wiped at the next session → item lost; **client insert** = adds a copy of a host-owned item (the client's backpack items are copies of the host's) to the real storage → item duplicated. A correct client transfer needs a durable, host-owned Player-2 inventory (all client inventory changes — pickups, sales, drops, purchases — recorded by the host and restored at client start), which is a separate milestone (shared inventory/economy). Per the milestone rule, client moves are therefore **blocked** (no item can be duplicated or lost); this gate is not complete.

### Changes
- **Host-owned exclusive lease** (`src/Shared/CarBox.cs` `CarBoxLease`, `WorldHost.CarBoxRequest`, keys `carbox` / `carboxItems` / proposal `carboxReq`): owner none/P1/P2, session id per grant, generation by session id; open → Accepted / Busy (other owner) / Unchanged (own); close/renew must name the current session (older or duplicated packets → Stale, never end a newer session); release on client disconnect and on any region change (both players); a client owner renews every 5 s and expires after 20 s of silence (crash without disconnect); the host owner is released by its own UI watchdog, never by time. One lease for the single global storage (Ranger and Shadowhawk). Client open needs a player car in its region.
- **Host-authoritative contents:** a client grant commits the host's real counters (`carboxItems`, ≤ 160 types, 6 B each) **before** the lease; the client writes them into its own `item_amount_storage` and only then opens the original UI (continuation of the hooked `PlayerScript.OpenStorage(0)`), so both players see the same, real storage in the original interface. Missing/oversized/ambiguous contents → no lease, nothing opens.
- **Plugin `CarBoxSync`** (Harmony prefix `PlayerScript.OpenStorage(int)` type 0 only, prefix/postfix `Inventory.MoveItemToStorage`): every car-storage opening — original CarBox branch on foot, driver, or the passenger-seat bridge — needs the lease while both players are connected; a refused/pending open does nothing (no text). Watchdog per frame only while a session is held (`isStorage` read): closing by any path releases it. Host moves only inside its current lease session (re-checked against the host lease); **client moves blocked** (logged, rate-limited). Locker (type 1) untouched. Solo host: original behaviour.
- **Access from every role:** the CarBox collider stays enabled on observed cars (the car is physically there; van door / heavy cargo stay blocked; hideout travel proxies keep the CarBox off), so the client reaches it on foot at the host-driven car and vice versa; the passenger-seat bridge is allowed for both roles (`SeatStorageAllowed(isHost, sharedLease)`).

### Status
| Item | Status |
|---|---|
| Native entry prompt | IMPLEMENTED BUT UNVERIFIED |
| Exclusive host lease (open/close/stale/disconnect/region/expiry) | IMPLEMENTED BUT UNVERIFIED (offline tested) |
| Same contents shown on both instances, original UI | IMPLEMENTED BUT UNVERIFIED |
| Storage reachable: host/client × driver/passenger/on foot | IMPLEMENTED BUT UNVERIFIED |
| Host item insert/withdraw (real storage + real backpack, original code) | VERIFIED original behaviour, now lease-gated, UNVERIFIED in MP |
| **Client item insert/withdraw** | **BLOCKED** — needs durable host-owned Player-2 inventory (separate milestone) |
| Blackbay sale of storage items on the client (`Remove_Item_Blackbay`) | HIGH-RISK pre-existing: client economy is sandbox-only; not changed |
| Codex review | NOT DONE: Codex CLI reported "logged in" but token refresh was revoked (401); not retried |

### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19, InteractionTests 16/16 (new: prompt/action consistency + hysteresis), **CarBoxTests 12/12 (new)**, RedirectTest pass, launcher 21/21. Deployed 2026-11-11. Offline only.
- Cost: lease decisions are event-driven (per open/close/renew), no polling of inventories; client renew 1 × 10 B proposal per 5 s only while its UI is open; contents snapshot once per client open (6 + 6·n B, typically < 200 B); per frame only one bool read while a session is held. Prompt: per-frame probe (1 raycast + 1 multi-hit line) only while the prompt is shown, else 10 Hz near a driven car. No in-game FPS measured; `CARBOX_STATS` (every 30 s when used) and `VEHICLE_STATS … entryPromptShown` added.

## M4.3.2a — Passenger UX: no mod text in game, reliable E entry, exit beside the seat, seated interaction lock — **verified by the user 2026-11-11 (entry, exit side, no door animation); prompt fixed in M4.3.2b**, 2026-11-11
Mod **0.6.2, protocol 11** (wire format unchanged from 0.6.1; both instances must still run the same mod version). Next mandatory gates: **M4.3.2b shared CarBox**, then **M4.3 Phase B joint travel** (PROJECT_PLAN "M4.3.2 gates").

### User QA of 0.6.1 + log review (`LogOutput.log` host, `LogOutput.log.1` client, plugin logs `20261111-032552_host` / `…032601_client`)
- Confirmed by the user: slow car-door sync, repeated entry/exit, driving together, handoff, cameras, collision, wheels. Rejected: entry needs particular angles, exit through the front-right door, medkit usable from the seat, client cannot use the CarBox, on-screen mod messages.
- Host: 7 seat requests accepted/entered, 7 exits, 6 proximity entries (`ray None` ×4, `ray CarBody` ×2), 1 storage opened from the seat. Client log: `TRAVEL … refused locally: passenger aboard` = the Phase A travel guard (expected; joint travel is the Phase B gate, not a regression).
- **Entry root cause 1 (verified in the logs):** every successful entry logged `feet -0.56…-0.60 m` against the limit -0.6 m: the height was measured from the bottom of the car's collider box, which sits ~0.58 m above the ground (ground clearance) → slightly lower ground or a kerb failed. **Root cause 2 (code):** 0.6.1 additionally required the camera ray to hit the car body or the camera to face the car, and treated any other hit (ground, a wall behind the player) as an obstacle. **Root cause 3:** the car box was the world AABB of each collider converted to car space, which grows with the car's yaw (up to ~1 m at 45°).
- **Medkit root cause (verified IL):** `PlayerScript.DisableInventory` (closing the inventory, storage or pause UI) calls `InteractDelayed` → its coroutine sets `canInteract = true` while the passenger still sits in the car → the original E branch (medkit, doors via the slow-door path, pickups) worked from the seat after any UI had been opened, on whichever instance did that (the host log only counted refusals of the mod's own seat bridge, which is why it looked correct there).
- **Performance:** host `VEHICLE_STATS update avg` rose from ~60 to 150–190 µs while the host rode as passenger (client ~50 µs): the 0.6.1 seat/foot probes cast rays every frame.

### Changes
- **No mod text in game (permanent rule, AGENTS.md rule 4 / CLAUDE.md):** all 25 `ShowText("Multiplayer: …")` calls removed → `Quiet()` writes `FEEDBACK (log only …)` to the BepInEx log (de-duplicated 2 s); the free-text spawner accessor is gone; the phone block while seated uses the game's own `cant_fast_travel` warning; the OnGUI status lines and the name tag above the other player are drawn only with the new config `Debug.ShowOverlay` (default **off**). Enforced by a source-scan test.
- **Entry** (`PassengerInteraction.OnFoot`, `VehicleSync.FootFrame/ClearLineToCar`): the camera ray decides only whether an ORIGINAL interaction owns the press (door, tailgate, CarBox, medkit, SitCar, any original tag → the game handles it); otherwise E next to a car the other player drives requests the seat when the feet are ≤ 1.2 m from the car's exact collider box, between -0.5 and +0.6 m of the car's own ground level (lowest wheel contact from its WheelColliders), and a straight line from the eyes to the nearest point of the car hits nothing but the car (walls/fences/objects block; triggers ignored). No aiming, no facing test, no trigger volume, no global range change. Car must be stopped and the seat free (host reservation unchanged).
- **No forced door animation:** a proximity entry opens no door (the original game has no human rear-passenger entry animation; the companion simply takes `associateSitPos`); the original seat lerp is the transition. Entry through a door the player opened (SitCar trigger) still closes that door after 0.25 s like `L_/R_Door_Close_Delay`.
- **Exit** (`PassengerInteraction.ExitSpots`, `VehicleSync.TryExitRide/FindExitSpot`): beside the passenger seat at the seat's z, 0.45 m outside the car's collider box, from the car's current transform: Ranger rear bench (x ≈ 0) → driver side (sign of `car_door_L`), i.e. left, behind the driver door; Shadowhawk front passenger seat (x = +0.44) → right side. Mirrored side at the same z only if blocked; ground required at the car's ground level ±0.5/0.6 m; free 0.35 m capsule (walls, car body, other cars, traffic). Car moving or no free spot → stays seated (log only). **No door is opened or closed by the exit**; doors set by the other player are untouched. Camera clip `GetOutCar`, controls/collision back as before.
- **Seated interaction lock:** `canInteract` is kept off for the whole ride (LateUpdate, after coroutines), so no original interaction runs from the passenger seat on either instance; E is resolved only by the seat logic: storage box (host only until M4.3.2b) → original `OpenStorage`; medkit → nothing; anything else → exit. UI open/just closed (0.35 s) → nothing.
- **Probe cost:** foot/seat probes at 10 Hz for the prompt/highlight, fresh evaluation on the frame E goes down; `VEHICLE_STATS` now logs `seat+foot avg`, `exitBlocked`, `canInteractKept`, `logOnlyFeedback`.

### Status
| Item | Status |
|---|---|
| Slow car-door sync | VERIFIED by the user (0.6.1) |
| No mod text in game | IMPLEMENTED BUT UNVERIFIED |
| Entry from any accessible side | IMPLEMENTED BUT UNVERIFIED |
| Exit beside the seat, no door animation | IMPLEMENTED BUT UNVERIFIED |
| Medkit / original interactions from the seat blocked (both instances) | IMPLEMENTED BUT UNVERIFIED |
| Own body in third person | IMPLEMENTED BUT UNVERIFIED (not yet checked by the user; lifecycle reviewed: shown only Seated + third person, hidden on every exit path, destroyed at shutdown) |
| Client CarBox / shared storage | **INCOMPLETE** — gate M4.3.2b (client presses E on the box: nothing happens) |
| Joint travel | **INCOMPLETE** — gate Phase B (travel with a passenger aboard is refused silently) |
| Remote engine audio, fleet persistence weaknesses | tracked follow-ups |

### Evidence
- Build 0 errors / 0 warnings; ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37, PassengerTests 19/19, **InteractionTests 15/15** (entry from every ray outcome with a clear line, ground-relative height window incl. the 0.6.1 failure values, roof/floors/walls rejected, exit spots Ranger/Shadowhawk, source scan for UI text), RedirectTest pass, launcher 21/21. Deployed 2026-11-11. Offline only — no Unity physics/rendering.
- Performance: offline decision cost unchanged (0.021 µs/frame, 0 GC). In-game cost not measured yet; compare `seat+foot avg` / `update avg` with the 0.6.1 host values (150–190 µs). No FPS measured.
- Codex review: not run in this gate (no high-risk data path changed; it is planned for M4.3.2b/Phase B).

### Risks
- Wheel-collider ground level is an estimate (centre − radius − half suspension travel); the window ±0.5/0.6 m covers it, the logged `feet … above the car's ground` values will confirm.
- The line-of-sight test uses all default raycast layers: an invisible solid blocker between player and car blocks entry (as it would block walking there).
- Exit beside the rear bench places the player next to a closed rear door of the Ranger (not openable in the original asset): requested behaviour, no animation exists.

## M4.3.1 — Passenger interaction refinement, own body in third person, storage from the seat, slow-door sync — **user-tested 2026-11-11: slow doors OK; entry/exit/medkit/notices reworked in M4.3.2a**, 2026-11-11
Mod **0.6.1, protocol 11** (both instances must run 0.6.1). Builds on M4.3 Phase A (user-tested in game 2026-11-11: both roles drive/ride, camera modes, doors, collision, wheels, seat authority, handoff, travel guard — all kept). Phase B (joint travel) **not started**.

### Verified findings (Cecil IL, read-only; details CODEBASE_MAP §3.7 "M4.3.1 verified IL")
- **Interaction pipeline:** one ray per frame (cam, forward, 2.25 m, `PlayerScript.mask`), ~15 gates incl. `canInteract` and `PCB.enabled`; the hit transform's tag picks the branch. The Phase A adapter sets `canInteract = false` while seated (like the driver) → **root cause** that the passenger could not use anything from the seat.
- **Shoe-box storage = `CarBox`** (tag `CarBox`, front-passenger area of the Ranger, behind the seats in the Shadowhawk): `PlayerScript.OpenStorage(storage_type)` → `Inventory.OpenStorage` (pauses the game, `Time.timeScale = 0`). Contents = per item type `ItemID.item_amount_storage` in the instance's **own save** (`Storage_Item_*`): one global car storage per save, shared by both cars, **not a per-vehicle container and not networked**. The client plays on a sandbox copy seeded at start → only the host's storage is real. M5.0 deliberately disables the CarBox collider on observed cars ("no storage on a car simulated elsewhere") → **second root cause** for the passenger (always on an observed car).
- **Medkit:** single-use heal object (tag `Medkit` only while `car_door_R` is open or the local driver sits with the engine off; use = `Heal`, destroy, `SPrefs Medkit = 1`); never networked.
- **Slow doors:** hold-E toggles `isOpen` at the start and swings at ≤ 17°/s with a quiet squeak, finishing at 70°/s; releasing early (`SlowFailed`) finishes the same movement at 150°/s with the loud clip. No partial resting angle. Phase A `vdoor` carried open/closed only → **a slow open was replayed as a normal fast, loud swing (DEFECTIVE)**.
- **Own body in third person:** the local player has only first-person arms/legs; the driver's third-person body is the car's own `tpp_character` (driver seat only, shown by `Set_TPP`). No passenger body exists in the car prefab → nothing could be shown (**root cause**).
- **Passenger exit by E** already worked in the 0.6.0 logs (host `getting out of Ranger` 3×); kept, now with priorities.

### Changes
- **Interaction resolver** (`PassengerInteraction` in `Vehicles.cs`, pure; `VehicleSync.FootFrame/SeatInteraction`): per E press exactly one action in the order (1) original target (door, tailgate `VanDoor`, `CarBox`, `Medkit`, `SitCar`, any of the ~80 original tags, or the medkit/storage components) → the original code runs, the mod steps back; (2) seated passenger input; (3) passenger request; (4) nothing. **Entry:** standing next to a car the other player drives (feet ≤ 1.2 m from its box at ground level — not on the roof, not below), the original ray hits the car's body anywhere, or passes through glass while the camera faces the car with a clear line of sight → E requests the seat through the existing host reservation. A wall/other object in front, out of reach, original gates closed, UI open → nothing. The original `CarEnterInfo` prompt is shown in that case (LateUpdate). No trigger volume, no global range change; ray only while such a car is ≤ 5 m.
- **Entry door:** entering without opening a door first opens the passenger-side door through the original `Door` (synchronized like any door change), the player gets in once it has swung open (≤ 0.8 s), the existing 0.25 s close follows. Opening the door manually + SitCar trigger still works as before.
- **Exit / seated E:** E without a special target exits (unchanged exit code: safe spot, door, camera clip, controls); E on the CarBox opens the original storage instead (first person, like the original driver; never both in one press — 0.35 s guard after any storage/pause UI closes, camera-mode switches deferred while the UI is open); E on the medkit → "out of reach from the passenger seat" (neither heal nor exit).
- **Storage from the seat:** seat-specific bridge that aims the original ray plus the recorded CarBox box (collider stays disabled on observed cars) and calls the original `PlayerScript.OpenStorage(storage_type)` with the original `!isShop` condition, with the original highlight. **Allowed only on the host** (`SeatStorageAllowed`): the host's save holds the real car storage. **Client passenger: refused with a message** — its storage is an unshared sandbox copy; opening it would show stale contents and let items exist twice. **Shared car storage is INCOMPLETE** (see below).
- **Own body in third person:** local-only clone of the original associate model (`RemoteAvatar`: Transform/Animation/renderers only, no scripts/colliders/tags) at the same anchor the other player sees (`associateSitPos`, Codename root, original `Sitting` clip), shown only while seated in third person, hidden in first person and on every exit path (exit, forced exit, region change, shutdown destroys it). DESIGN COMPROMISE (necessary): the original game has no passenger body; the clone is the same model the other player sees for this passenger.
- **Slow door sync (protocol 11):** proposal {door, open, mode}; the committed byte keeps a per-door slow mark; observers replay the slow movement (squeak at 0.3, ≤ 17°/s ramp, 70°/s after the original 0.8 threshold) without setting `openingSlowly` (would drive the observer's HUD fill and restore its `canMove`); an early release commits FastFinish → observers finish fast with the loud clip. Late join / new scene objects swing quickly to the committed state. One proposal per change, no per-frame door packets.

### Status per subsystem
| Subsystem | Status |
|---|---|
| Phase A seat registry, camera mode, handoff, travel guard | VERIFIED CORRECT (user 2026-11-11), unchanged |
| E entry resolver / prompt / entry door | IMPLEMENTED BUT UNVERIFIED (PENDING MANUAL QA) |
| E exit priorities, medkit out of reach | IMPLEMENTED BUT UNVERIFIED |
| Storage from the seat (host) | IMPLEMENTED BUT UNVERIFIED |
| Storage for the client passenger / shared car storage | **INCOMPLETE** — needs a host-authoritative storage transaction layer (separate milestone with its own QA boundary) |
| Client storage access as driver / on foot on its own car (pre-existing) | **HIGH-RISK (pre-existing)**: the client can open its sandbox copy of the car storage when the car is simulated on the client → items can exist on both instances. Not changed in this milestone (decision for the user) |
| Own body in third person | IMPLEMENTED BUT UNVERIFIED; DESIGN COMPROMISE (necessary) |
| Slow car-door sync | IMPLEMENTED BUT UNVERIFIED (was DEFECTIVE) |
| Building/world doors | not synchronized at all (pre-existing, out of scope) |
| Ranger tailgate (`VanDoor`) | original interaction kept; its state is **not** synchronized (pre-existing) |
| Medkit consumption | not synchronized (pre-existing; per-save single use) |

### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37 (door tests moved to the 3-byte proposal, codec accepts slow bits, protocol-10 proposal rejected), PassengerTests 19/19, **InteractionTests 13/13 (new)**, RedirectTest pass, launcher 21/21. Deployed 2026-11-11.
- Offline decision cost (foot + seat + door rules): 0.021 µs/frame, 0 GCs / 1 M frames (.NET Framework, not Unity). New Unity work: one `Physics.Raycast` per frame only while on foot within 5 m of a car the other player drives, or while seated in first person (+ one linecast when the ray misses); one transform update for the own body while seated in third person; slow-door replay touches only doors being replayed. Network: door proposal 3 B (was 2 B), one extra door delta only for a slow open released early. **No in-game FPS measured** — the existing `VEHICLE_STATS … update avg/max us` line now also logs `footEntries`, `storage open/refused`, `medkitRefused`, `selfBodyShown`, `slowDoors rep/applied/fastFinish` for the user's run.
- Codex review: not run (Phase A attempt failed with authentication 401; not retried per instructions).

### Remaining risks (PENDING MANUAL QA)
- The car-body hit depends on the car's colliders being on `PlayerScript.mask`; if they are not, entry falls back to the facing + line-of-sight path (still bounded by the 1.2 m box reach).
- CarBox targeting from the rear bench uses the recorded box geometry; whether the rear eye position reaches it within 2.25 m and whether front-seat geometry should block it is an in-game check.
- The own-body clone is the associate model, not the main character's own third-person model (none exists for a passenger seat).
- Slow replay acceleration differs from the original by at most one frame (Door.Update's own acceleration follows the mod's speed cap); a fast-finish that arrives after the observer's replay already reached its state is ignored (moot).
- Ranger passenger still uses the front-right door for the rear bench (rear side doors are not openable in the original asset).

## M4.3 Phase A — Two-player vehicle occupancy (passenger seat, passenger camera mode) — **tested in game by the user 2026-11-11 (substantially working; refinements in M4.3.1)**, 2026-11-11
Mod **0.6.0, protocol 10** (both instances must run 0.6.0). 0.5.0 was confirmed by the user in game (doors, entry, solid remote cars, wheels, handoff, travel). Phase B (joint travel) and remote engine audio are **not implemented** (mandatory follow-ups).

### Verified original behaviour (Cecil IL / scene data, read-only)
- **Companion seat:** `CodenameAI.Update`: while `isInCar` the Codename **root** is set to `PS.current_vehicle.associateSitPos` position + rotation and `thisAnim.CrossFade(sitAnim, 0.2)`; `EnterCar()`/`ExitCar()` only toggle `isInCar`, following, tag, `richAI` (entered when the player sits in the car with no heavy item and `associateSitPos != null`). No door or entry animation is involved. Seats: Ranger `Female Sit Position` (-0.01, -0.40, -0.23) = rear bench centre; Shadowhawk `Associate Sit` (0.44, -0.78, 0.09) = front passenger (car-local, `SEAT_PROBE`).
- **Driver seat:** `CarController.Update` lerps the player root to `sit_transform` (dt × 3) while `is_sitting_inside`; the `RunCar` coroutine parents the player to `sit_transform` (mouse look then car-relative). `EnterVehicle` = unparent car, `current_vehicle`, door close coroutines, tutorial/icon, `ActivateControls(6)` (= HUD hint panel `ControlsObjects[6]`), crouch off + default height, `PMB.canMove = false`, `is_in_vehicle`, `HideWeapons`, CC + CapsuleCollider off, `is_sitting_inside`, torch off.
- **Camera:** `Set_TPP(true)`: `main_camera` off, `PCB` + `HandCamera` off, `tpp_camera.SetNewCar(car)` (`TPP_Camera_Controller` only needs the car transform, `CarController.TPP_distance/TPP_height` and its Rigidbody), `tpp_character` on, minimap → TPP camera; `Set_TPP(false)` the reverse + `DisableTPP`. Toggle: `change_view` while `engine_running` (`is_TPP`, SPrefs `TPP`).
- **Doors:** Ranger `Door` components: front L, front R, tailgate; rear side doors are meshes without Door/collider (not openable). Shadowhawk: `Door` only on the **left** door; the right door is decorative. → Ranger passengers use the front-right door, Shadowhawk passengers the left door.
- `PlayerScript.Update` interaction branches are gated by `canInteract` (also used by `ExitCar`).

### 0.5.0 log review (`20261111-013550_host`, `…013559_client`)
- `vdoor -> Busy` (7×): all while the host sat in the driver seat (lease 00:37:29, refusals 00:37:37–44). Cause: the door rule used `VehicleMotion.Settled` (no movement **and** no driver). Correct for the handoff, too strict for doors → **fixed**: doors use the new `Stopped` (no movement) — needed for passenger entry.
- `pushOuts=10` on the client: right after the host started driving while the client stood at the car → expected collision recovery, no defect.

### Changes
- **Host seat registry** (`FleetBook.ReservePassenger/ConfirmPassenger/ReleasePassenger/ExpirePassengers/ReleasePassengerOutside`, `WorldHost.Ride`, key `vehicleRide` 1 = reserve, 2 = seated, 0 = leave): reservation sets `Passenger` + `PassengerPending` (10 s entry timeout); conditions: unlocked, same fleet region, not travelling, the other player is the seated driver, seat free, requester has no seat, requester is not the holder, no handoff lock, car stopped. Disconnect, region change of the passenger, timeout free the seat. Passenger can never claim/take the car (existing `Claim`/`TakeOver` passenger checks). Driver exit leaves the passenger seated; holder unchanged.
- **Camera mode** `VehicleFlags.Tpp` in the record (host commits from its own car or the client's snapshot flag `VehicleStateFlags.Tpp`); a new passenger reads the current mode from the record (initial state); changes go out in the next 50 ms snapshot slot, nothing per frame.
- **Passenger adapter** (`VehicleSync` "M4.3 passenger seat"): E on the original door → SitCar prompt → E → seat request (`EnterAction.RequestPassenger` when the other player drives and the car stands). Entry mirrors the non-driver part of `EnterVehicle` (weapons hidden, crouch off, `canMove` off, CC + capsule off, `canInteract` off, torch off) and parents the player to a seat anchor (child of the car) like `RunCar`, with the original dt × 3 seat lerp; the used door closes after 0.25 s (close-delay semantics). Never set: `current_vehicle`, `is_in_vehicle`, `is_sitting_inside`, controls, engine, lease, holder. Passenger camera follows the driver's mode with the camera part of `Set_TPP` (original TPP vehicle camera on the same car / own first-person seat view). Exit with the original `interact` action: free exit spot from `leave_car_transform` (door side, other side, behind; ground + free capsule) else "no room", passenger-side door opens, unparent, `CarExitRot`, `GetOutCar` camera clip, controls back after 0.6 s + `InteractDelayed(0.8)`. Forced exit (car becomes simulated here / disappears, seat cleared, region change, disconnect, shutdown) restores a complete on-foot player first, before any presentation change.
- **Remote occupants:** passenger avatar at the original `associateSitPos` (Codename root, original `Sitting` clip) on both observed and simulated cars; `SeatRules.Occupant` seats driver and passenger. `PlayerSync.LocalSeated` reports `InVehicle` for the passenger.
- **Phase A travel guard:** a seated passenger or a driver with a passenger aboard cannot travel (local refusal with message + host `SeatTravelBlock` → `Busy`); phone fast travel blocked while seated.

### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests 33/33, HandoffTests 37/37 (3 assertions updated to the new behaviour), **PassengerTests 19/19 (new)**, RedirectTest 3/3, launcher 21/21. Deployed 2026-11-11 02:07 (243 712 B).
- Offline decision cost: seat + camera-mode 0.035 µs/frame, 0 GCs / 1 M frames (.NET Framework, not Unity). New Unity per-frame work only while seated: one transform lerp, one record lookup, one input read. Network: ~20 B proposal + ~20 B record delta per request/confirm/leave and per camera-mode change. **No in-game FPS measured.**
- Codex read-only review of the seat state machine attempted once: failed (Codex authentication 401). No independent review took place; Claude re-traced the invariants locally (forced exit before any presentation change, seat release on every exit path, no parenting in Requested/Granted).

### Not verified / risks (PENDING MANUAL QA)
- Local first-person passenger root is **derived** (associate seat raised by the learned 0.52 m) — Ranger rear-bench eye height / roof clearance needs calibration (`Vehicles.PassengerSeatAdjust`). Remote passenger anchor is the original companion seat (verified semantics, visual check pending).
- TPP camera on an observed (kinematic) car, PlayerMotorBehavior with CC disabled while parented to a kinematic body, `GetOutCar` clip, exit-spot quality in tight streets: in-game only.
- The Ranger passenger enters through the front-right door although the seat is the rear bench (rear side doors are not openable in the original asset); the Shadowhawk passenger uses the only openable (driver-side) door.
- Remote engine audio still paused on observers (tracked, not started).

## M5.0 — Engineering audit, verified vanilla vehicle pipeline, original-object remote vehicles — **verified in game by the user (2026-11-11)**, 2026-11-11
Mod **0.5.0, protocol 9** (both instances must run 0.5.0). Passenger occupancy (driver + passenger, cameras, joint travel) is **mandatory and NOT implemented yet**: it builds on this foundation after the user's QA (testing boundary).

### Verified vanilla vehicle entry/exit (Cecil IL of Assembly-CSharp 1.27c, read-only)
1. Car doors are ordinary `Door` components (`isCarDoor`), tagged `Door`, referenced by `CarController.car_door_L` / `car_door_R`. **Opening is a separate action**: E (`interact`) on the door in `PlayerScript.Update` (tag `Door`, `canUse`, not `brokeHinge`) starts the original slow/quick door interaction (`assignDoor`, `Door.load_slow` → `Door.Update`: after 0.15 s held `openingSlowly`, else quick); while in a vehicle the same E on a door calls `CarController.ExitVehicle`. The door swing is `Door.Update` rotating `localRotation` towards `OpenRot`/`openRotation`/`SaveRot` (no Animator); `Door.Resett` ends it.
2. `CarController.Update` (IL 0x0f7f–0x0fe2) activates `enter_car_object` (the `SitCar` trigger, `EnterCar`) **only while a front door is open and `!is_sitting_inside`** → the "Enter car" prompt exists only with an open door.
3. E on the trigger → `EnterCar.EnterVehicle` → `CarController.EnterVehicle`: unparents the car, `PS.current_vehicle`, starts `L_/R_Door_Close_Delay` (door untagged, `Resett` if animating, wait 0.25 s, `Use()` if open → **the door closes by itself**, tag `Door` again), `ActivateControls(6)`, CC + `CapsuleCollider` off, `is_in_vehicle`, `is_sitting_inside`; `CarController.Update` lerps the player to `sit_transform`. No lock check. Engine/TPP: `RunCar` → `Set_TPP(true)` (CODEBASE_MAP §3.7).
4. Exit: `ExitCar` coroutine calls `car_door_L.Use()` (**toggle** → opens the closed driver door), `GetOutCar` camera clip, re-enables CC/capsule after 0.5 + 0.6 s; the door stays open until someone closes it.
5. `CarController.OnCollisionEnter` returns immediately unless `is_sitting_inside` → a car nobody sits in locally never charges damage/repair nor knocks NPCs. `LoadTrunkItem` (from `Update` after enable) instantiates the instance's global `TrunkItem` into the car.
6. Same code for the Ranger and the Shadowhawk (one `CarController`/`Door` class); per-car differences are only data (doors, seats). **In-game confirmation per car is still a user baseline test** (TESTING §10, test 0).

### Audit of the implemented mod (classification per AGENTS rule 3)
| # | Subsystem (file · method) | Class | Evidence / finding | Action |
|---|---|---|---|---|
| 1 | Launcher + isolation (`Launcher.cs`, `SaveGuard`, `PathRedirect`) | VERIFIED CORRECT | M1 accepted; client sandbox verified in game; RedirectTest 3/3 | kept |
| 2 | Transport/protocol (`SessionNode`, `Protocol.cs`, `StateRegistry`) | VERIFIED CORRECT | M1–M3 in game; reliable ordered state, sequenced snapshots | kept (protocol 9 for new key/field) |
| 3 | Remote player movement/animation (`PlayerSync`, `RemoteAvatar`) | VERIFIED CORRECT (walk/run/crouch); DESIGN COMPROMISE (necessary): associate model, no carry/tool/jump clips exist on it | M2 accepted | kept |
| 4 | Region authority + scene transitions (`WorldSync`, `Regions`) | VERIFIED CORRECT | M3/M4.2.3 in game | kept |
| 5 | Region clocks + sleep (`WorldTime.cs`) | VERIFIED CORRECT (M3.1.1/M3.1.2 accepted); client-side hour ticks on corrections run against the sandbox (documented, shared economy later) | | kept |
| 6 | Vehicle identity/fleet (`FleetBook`, `VehicleRecord`) | VERIFIED CORRECT | one record per car through all tests/logs | kept |
| 7 | Driver lease + holder handoff (`FleetBook.TakeOver`, `RequestTake`, `EnterFrame`) | VERIFIED CORRECT (M4.2.2/4.2.3 in game both ways) | | kept; now reached through the original door + trigger everywhere |
| 8 | Travel authorization (`TravelDesk`, `TravelHooks`) | VERIFIED CORRECT (M4.2.3) | | kept |
| 9 | Hideout travel proxy (`AwayProxy`) | VERIFIED CORRECT, intentional design | separate from the real car | kept |
| 10 | Remote vehicle visual outside the hideout (stripped clone, `CreateProxy/Strip`) | **INCOMPLETE / DESIGN COMPROMISE (unacceptable)** | no `Door`/`CarController`: doors never opened, prompt appeared without door (M4.2.4 removed door colliders to make the ray reach the trigger), no wheel visuals, no passenger seats possible | **replaced** by the original object (observed mode) |
| 11 | Remote car collision (ghost while driven/moving) | **DESIGN COMPROMISE (unacceptable)** | `PresentationRules.Collision` returned Ghost; players walked through moving cars | **replaced**: solid in every state |
| 12 | Remote wheels | INCOMPLETE | clone had no wheel logic | **added** visual wheels (no physics) |
| 13 | Hideout ParkedOther copy | IMPLEMENTED (worked in game) but HIGH-RISK detail: FreezeAll dynamic body with EVP + wheel colliders still running, transform-teleported each frame | | **unified** into observed mode (kinematic, EVP paused) |
| 14 | Persistence/fuel (`FleetStore`, `FuelFrame`) | IMPLEMENTED BUT UNVERIFIED in game across restarts; see finding below | | not changed this stage (testing boundary) |
| 15 | Safe spawn (`TryPlace`) | VERIFIED CORRECT in observed runs; cost one-time (≤ 0.2 ms after the M4.2.2 lookup fix) | | kept; observed bodies excluded as ground |
| 16 | Disconnect/reconnect | IMPLEMENTED BUT UNVERIFIED in game for vehicles | registry resync + holder reassignment tested offline | kept |
| 17 | Remote seated driver (`SeatFrame`, `SeatRules`) | IMPLEMENTED, Ranger height still hypothesis (`TppCalibrated`) | | kept, now anchored to the original object everywhere |
| 18 | Passenger seat / camera / joint travel | **INCOMPLETE (mandatory)** | not implemented | next stage (M5.1) |

### Fleet persistence finding (`FLEET record ... save story 0 < recorded 80`)
- Evidence (read-only decode of the host save `saveData.thief`, FBPP XOR secret from `InitializePrefs.Reinitialize`): the save has **`FreeMode = 1`**. `MainMenu.StartFreeMode` (IL) does `SPrefs.DeleteAll`, writes `CurrentLocation = 0`, `GasTankLeftCar = 60` and **no `Story`**; `StoryManager.InitiateStory` sets `story = 80` in memory for free mode; `SaveGame.SaveGameNewSystem` writes `Story` on the next travel save. Every logged rejection shows exactly the fresh-free-mode values at load (story 0, location 0, fuel 60) and the host arrived in the hideout → **those sessions were genuinely new free-mode playthroughs; the rejection was correct**.
- Latent defect (DEFECTIVE, medium, not fixed in this stage): the fleet file is written from in-memory state between game save points, while the game persists only at `SaveGameNewSystem` (travel) / quit. After quitting without a save point (e.g. free mode before its first travel, or story advanced in memory), *Continue* can reject a legitimate fleet record or keep car positions newer than the save. Recommended fix (next persistence stage): commit the fleet file from a postfix on `SaveGame.SaveGameNewSystem` (same moment as the game's own car position), keep the background writer, and compare a save fingerprint (Story, Day/Week, Playtime) instead of story alone. No user save was modified.

### Changes (M5.0)
- **Observed presentation on the original object** (`VehicleSync.EnterObserved/ExitObserved/FixedStep`): the car simulated by the other instance is the local original scene object, `Rigidbody` kinematic (Interpolate, ContinuousSpeculative) moved with `MovePosition/MoveRotation` in `FixedUpdate` (teleport for > 5 m jumps); every enabled `EVP.*` component (controller, input, audio, tire effects) and all `WheelCollider`s paused; storage/cargo colliders off (as before); `load_trunk_items` off while observed (no duplicate trunk item); body + door colliders stay → **solid while parked, occupied, starting and driving**. Used for `ParkedOther` (hideout) and `RemoteVisual` (elsewhere). The stripped-clone code (`CreateProxy`, `Template`, `Strip`, `ShellRules`) was removed.
- **Original door interaction for both players**: doors are the original `Door` components; E on the door, the prompt after the door opens, `EnterCar` → `CarController.EnterVehicle` prefix → host handoff → resumed original entry (closes the door itself). Door state is host-committed (`vdoor`, `WorldHost.SetDoor`): holder always; others while the car is settled; driver door not toggled by others while a driver sits (`ExitCar` toggles it); reset when the car changes region. Clients use `DoorReplica` (no echo, no stale re-open). Remote changes are applied by writing `Door` fields exactly like `Door.Use()` without its local-player side effects (`canMove`, mouse sensitivity, noise); `Door.Update` animates, door sounds play.
- **Player push-out guard** (`PushOutFrame`): CharacterControllers are not pushed by kinematic bodies; only while the local player is inside an observed car's bounds, `Physics.ComputePenetration` (player `CapsuleCollider`, else CC) and a horizontal `CharacterController.Move` (respects walls, ≤ 2 m/frame).
- **Remote wheels** (`BindWheels/WheelFrame`): original `EVP.Wheel.wheelTransform`s, spin from the car's own motion / rolling radius, steer from the holder's `EVP.VehicleController.steerAngle` (new snapshot byte, 39 B).
- `PresentationRules.Collision` → Solid for all presentations (appearing around the player: solid once they step out).

### Evidence
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14 (39 B snapshot, steer round trip/clamp), TravelTests 33/33, HandoffTests 37/37 (new 36 door sync, 37 door rules; collision cases updated), RedirectTest 3/3, launcher 21/21. Deployed to `BepInEx\plugins\TS2MP` (225 280 B, 2026-11-11 01:25).
- Offline per-frame decision cost 0.032 µs, 0 GCs / 1 M frames (.NET Framework, not Unity). New per-frame Unity work: 2 door bool reads per present car, ≤ 4 quaternion updates per observed wheel set, one bounds test per observed car; overlap/penetration queries only while the player is inside a car's bounds; no allocations in these paths. Network: +1 B per vehicle snapshot; door changes ~20 B proposal + ~20 B delta per human door action. **No in-game FPS measured.**
- Codex read-only review was attempted once and did not run (Codex authentication 401); Claude performed the review questions itself (restore paths for every presentation transition, teleport vs. MovePosition, door races on reconnect/scene reload, hot-path allocations) — no defect found, not independent.

### Not verified / risks (PENDING MANUAL QA)
- Unity behaviour that offline tests cannot cover: CharacterController vs. moving kinematic car (blocking, push-out quality, trapping between car and wall), high speed (tunnelling is prevented for dynamic bodies by ContinuousSpeculative; the push-out is minimal-translation and could put a player on the far side at very high speed), wheel axes/orientation on both models, EVP restart after handoff (re-enabled components), door sound volume.
- The observed car is silent (EVP audio paused) except door sounds — DESIGN COMPROMISE (avoidable, later: drive `VehicleAudio` from snapshots).
- Traffic/NPC cars hitting an observed car bounce off an immovable kinematic body on the observer (each instance has its own traffic).
- `LoadTrunkItem` on a car that was already active before it became observed (hideout start) can still have run — pre-existing.

## M4.2.4 — Vanilla door interaction on remote cars — **PENDING MANUAL QA**, 2026-11-10
Mod 0.4.6, protocol 8. M4.2.3 user test: car travel/ownership, remote handoff both ways and the original entry work; E on a remote car outside the hideout appeared only from odd angles (jumping, walking around). Logs `20261110-232652_host_22d751f5` / `…232701_client_b178387e`: `INTERACT_PROBE … mask=0x1E00A811 original enter trigger layer=0 ownBody=True inMask=True shell layers=0(in mask),13(in mask),21`, `shell colliders=10 enterTriggers=1`.

### Root cause (verified IL + proxy code)
- `CarController.Update` (IL 0x0f7f–0x0fe2): `enter_car_object` (the `SitCar` trigger) is `SetActive(true)` only while `car_door_L` or `car_door_R` (`Door`, `isCarDoor`) `isOpen` and `!is_sitting_inside`; otherwise inactive. So the original trigger is only ever reachable with the door swung open.
- The proxy has no `CarController` or `Door` script: its trigger was always active, while the M4.2.2 shell kept the **closed car-door colliders** (non-trigger; layers in `PlayerScript.mask`) right in front of it. The E ray (`Physics.Raycast(cam, forward, 2.25 m, mask)`, first hit) hit the door collider, which resolves to the untagged proxy root, from every normal standing position; only rays slipping past the closed door (from above/odd angles) reached the trigger. The hideout copy is the real object with working `Door`s, so it behaves normally.
- Which shell collider exactly blocked the ray is an inference from geometry (the door sits between the player and the driver-side trigger); the new template log line `car-door colliders dropped=N` and the optional `INTERACT_RAY` probe make it checkable.

### Fix (smallest)
- Shell (`ShellRules.KeepCollider`): colliders of car `Door` objects are no longer cloned into the shell; body colliders stay (parked car stays solid), the original trigger stays unchanged (size, place, layer 0, own kinematic body).
- Trigger gating (`ShellRules.EnterTriggerActive`, mirrors `CarController.Update`): proxy trigger active only while the driver seat is empty, the car is unlocked and not travelling → no prompt while the remote drives, the original prompt (`CarEnterInfo`) as soon as the seat is free.
- Optional `Vehicles.InteractDebug` (default off): on an E press near a proxy (≤ 1/s) `INTERACT_RAY` logs all hits of the original ray in order with the transform the game reads.
- Unchanged: original input (`interact`), prompt, `EnterCar.EnterVehicle` → `CarController.EnterVehicle` prefix → host handoff → one resumed original entry. No new key handler, range, collider size or teleport.

### Limitations
- The proxy's doors don't open and their area is not solid (walking into the door plane is possible; body colliders still block). Vanilla shows door opening before entry; on a remote car the prompt appears directly.
- Unity raycast/prompt behaviour is not covered by offline tests.

## M4.2.3 — Vehicle travel ownership, remote-region entry, seat calibration — **verified in game except odd-angle E (→ M4.2.4)**, 2026-11-10
Mod 0.4.5, protocol 8 (wire format unchanged; host decision rules changed, so both must run 0.4.5). M4.2.2 user test (logs `20261110-225231_host_c9259146`, `20261110-225237_client_3fea319d`): hideout handoff both ways, travel + follow + spawn, remote movement, seated driver, parked solidity PASS; bugs below FAIL. Not accepted.

### Root causes (proven)
- **A — car left behind:** host log 21:56:26.990 `driver lease P1 -> Accepted`, no release before the 21:56:35.100 travel click → P1 **was driving**. `TravelDesk.Carriable` returned false for every car in a one-car region while the other player was there ("the region car stays with the player who stays", M4.2), so the outcome was `None` and `HandOverStaying` gave the car to P2. Intent was not the problem; the rule was.
- **B — E on a proxy did nothing:** `PlayerScript.Update` (IL) casts one `Physics.Raycast(cam, forward, 2.25 m, PlayerScript.mask)` and checks the tag `SitCar` / `EnterCar` of `RaycastHit.transform`, which Unity resolves to the **attached rigidbody's** transform. The proxy stripped every Rigidbody and added one kinematic body at its root, so a hit on the enter trigger resolved to the untagged proxy root. (The original trigger therefore needs its own body; the frozen hideout copy keeps it, which is why the hideout worked.) The log line `enterTriggers=1` showed the trigger existed, but nothing about how the hit resolved.
- **C — head through the roof:** the Ranger anchor came from `TPP Character` (y −0.15), a model reference, not a Codename seat. The verified Codename seat (Shadowhawk `Associate Sit`) sits 0.52 m below `Sit Position`.

### Fix
- Rule A–E (PROJECT_PLAN "M4.2.3 travel ownership rules"): `Carriable` accepts the confirmed driver (`Driver == traveller`) regardless of who stays; reservation moves the record to the destination at decision time, so the old region refuses leases (`Claim` region check) and handoffs (`TakeOver`: reserved/region), and drops late snapshots (`SenderSimulates` region check) at once; rollback (cancel, disconnect, load elsewhere, timeout) restores the origin and the holder is reassigned to whoever is there. Decision notes now show `(driving)` / `(on foot)`.
- Proxy enter trigger gets its own kinematic Rigidbody (moves nothing); shell colliders keep their original layers (same raycast blocking as the real car); `INTERACT_PROBE` logs `PlayerScript.mask`, the original trigger's layer/body and the shell layers once per car. The existing handoff path (same request/grant/resume code as the hideout) is now reachable outside the hideout.
- Seat: `SeatSource.TppCalibrated` = (tpp.x, sit.y − 0.52, tpp.z), Codename-root semantics (Ranger −0.15 → −0.33 m); manual per-car `Vehicles.DriverSeatAdjust`. Still a hypothesis until seen in game.

### Measurements (0.4.4 logs, real game)
- `VEHICLE_STATS` max frame 8.5 ms (host) / 13.2 ms (client), no `VEHICLE_SLOW_FRAME` (> 20 ms) logged; 0.4.3 had 117.7 / 114.1 ms → the startup type lookup fix removed the spikes (measured). Averages 19–27 µs (vehicle), 41–48 µs (world). Vehicle traffic over the session: host sent 445 snapshots / 16.9 KB, client 656 / 24.9 KB. Handoff 1/1/0/1 each way; solid switches 17 (host) / 6 (client). No FPS measured.

### Limitations
- Moving remote cars are still ghosts (interim policy); no car-to-car physics.
- Driving into a one-car region that already holds the other car leaves your car behind (region rule).
- Proxy E and seat height need in-game confirmation; offline tests cannot exercise the raycast or animation.

## M4.2.2 — Driver handoff, seated remote driver, remote car collision, travel visuals — **partly verified in game, superseded by M4.2.3**, 2026-11-10
Mod 0.4.4, **protocol 8** (both instances must run 0.4.4). M4.2.1 was tested in game (logs `20261110-215833_host_f4988fa9`, `20261110-215841_client_a7c2379d`): travel proxy, following into a region, safe arrival, return and remote movement worked; four bugs below failed. M4.1, M4.2, M4.2.1 are **not** accepted.

### Root causes (proven from logs + code)
- **Free car not takeable:** host log `driver lease P1 -> Accepted` / `driver released -> Accepted` while the client logged `ParkedOther (… holder=P1)` and `TRAVEL menu opened from the hideout travel proxy of Ranger (ParkedOther)` repeatedly. Release only cleared `Driver`; the simulation `Holder` stayed P1 (`FleetBook.ReassignHolders` keeps a present holder), `FleetBook.Claim` required `Holder == slot` ("no driver handoff in M4.2"), and `PresentationRules`/`AllowEnterVehicle` sent every E on `ParkedOther` to the travel menu. Outside the hideout the other player's car was a stripped visual without any enter trigger. The vehicle id was never lost.
- **Driver invisible:** `RemoteVisibility.Decide` → `InVehicle` hid the avatar (`REMOTE_VIEW Visible -> InVehicle`) by M2 design.
- **No physical presence:** the M4.1 visual proxy strips all colliders (`Strip` kept only meshes).
- **Floating car at map exits:** when the holder travelled and the car stayed (other player present → outcome `None`), the holder only changed after the traveller's scene unloaded (host log: grant 21:05:22.9, takeover 21:05:25.3); meanwhile the observer's visual held the last streamed pose (possibly mid-motion at the exit) and then the real car appeared at an older (≤ 2 s throttled) parking pose.

### Fix
- **Seat ≠ authority:** `FleetBook.TakeOver` + `WorldHost.TakeOver` + proposal `vehicleTake`; host-side `VehicleMotion` settle check (1 s), 5 s handoff lock, freshest pose/fuel committed before the holder change (`LivePark`: host physics or last client snapshot). The requester's E is intercepted in the existing `CarController.EnterVehicle` prefix, one request at a time (6 s timeout), and the original `EnterVehicle` is resumed exactly once (3 s window, player on foot within 6 m) after the car became Real locally; the lease is then claimed by the normal `StreamFrame` path. Denials show a short in-game text. Symmetric for host and client; the host gets no extra rights. The old holder stops streaming in the same frame its view changes (receivers drop its packets by holder check).
- **E priority:** `PresentationRules.Enter`: Real → original; AwayProxy → travel menu; ParkedOther/RemoteVisual → request (free + settled) / "seat taken" (occupied; future passenger) / "wait until stopped" / unavailable (travelling). F7 unchanged. No global key hook.
- **Seated driver:** `SeatRules` + `VehicleSync.SeatFrame` pose the existing Codename avatar with the original `Sitting` clip at a car-specific anchor from the car's own transforms, every frame after the car moved; walking resumes on exit. `PlayerSync` keeps the avatar visible in `InVehicle` only while posed.
- **Collision:** hideout copy (original object, FreezeAll) toggles its non-trigger, non-wheel body colliders; visual proxy keeps cloned body colliders on one kinematic, gravity-free rigidbody (untagged, default layer) plus the original `EnterCar` trigger re-pointed at the original `CarController`. Solid only when parked + settled; ghost otherwise; never closed around the local player. Arrival placement never stands on a proxy shell.
- **Travel visuals:** `TravelDesk.HandOverStaying` hands a staying car to the other player present at decision time; the new holder activates at the proxy's last streamed pose (else the reported parking pose) and removes the proxy; holder changes reset send/park/motion history.
- **Performance:** all `AccessTools.TypeByName` lookups moved to startup (CarBox/VanDoorTrigger/Place_Heavy_Trigger, AITrafficCar, AstarPath types); the AstarPath instance is re-read per scene (was cached forever). New `VEHICLE_SLOW_FRAME` log (> 20 ms, ≤ 1/5 s) with a measured phase breakdown.

### Measurements
- Logs (M4.2.1, real game): `VEHICLE_STATS` max 117.7 ms (host) / 114.1 ms (client) in the windows of the first arrival placements (`placed … after 1 candidate(s) in 115,01 ms` and `17 candidate(s) in 112,77 ms`); a later hideout placement took 0.22 ms → one-time cost, not candidate count. Client 45.0 ms in the window of the first hideout proxy (lazy `TypeByName` in `EnterProxy`). Typical avg 13–47 µs/frame. Attribution of the 113–115 ms to `TypeByName` is an **inference** (the lookups were the only one-time work inside the measured block); the next logs (`VEHICLE_SLOW_FRAME`) will confirm or refute it. 38.9 ms host / 12–14 ms windows not attributed (possibly first-call JIT, activation `StartVoid`).
- Offline (.NET Framework, not Unity): seat + collision + motion-latch decisions 0.031 µs/frame, 0 gen-0 GCs over 1 M frames. Network: one `vehicleTake` proposal (~21 B payload) + its result per handoff; record/park deltas as before; `Moving` flag changes are hysteresis-limited. No FPS claims.

### Not implemented / limitations
- Moving remote cars are ghosts (player and NPCs pass through); full car-to-car / car-to-player collision physics is **not** solved. Traffic hitting a solid parked shell bounces off an immovable body (each instance has its own traffic anyway).
- Seat anchors are **hypotheses until seen in game** (`SEAT_PROBE` logs the source per car). Passenger seating, passenger camera, travelling together, per-car storage: not implemented.
- Handoff only for settled cars (≈ 1–1.5 s after the driver exits). A race where the holder enters at the same moment the other player's request is granted kicks the holder out again (original exit) — rare, logged.
- The frozen hideout copy keeps its EVP/wheel components enabled under FreezeAll (unchanged from M4.2.1); outside the hideout no original physics runs on the observer.
- Offline tests do not exercise Unity physics, animation or rendering.

## M4.2.1 — Hideout vehicle availability, travel proxy, state restoration — **tested in game: partly working, superseded by M4.2.2**, 2026-11-10
Mod 0.4.3 (protocol 7 unchanged; both instances must run 0.4.3). M4.2 **failed** the user's manual QA (bugs A–C below) and is not accepted; M4.1 is not accepted.

### Root causes (from the M4.2 code + verified IL)
- **Bug A (Ranger vanishes from the client's hideout):** with both players home the host was the holder of both garage cars, so the client's original car objects were switched off (`SetActive(false)`) and replaced by stripped visuals. When the host left with the Ranger the record changed to "away"; the M4.2 "frozen travel access point" branch only froze a copy that was still *active*, so the already-inactive copy stayed off and the stripped visual was removed (car no longer in the region) → nothing left to interact with.
- **Bug B (returned car not visible):** each scene load re-created the sender's per-car state, so its snapshot sequence restarted at 1, while the client's filter still held the high sequence of the earlier trip (it records sequences even for cars in other regions). Every snapshot of the returned car was dropped as `Stale` → no visual until a restart.
- **Bug C (client walks through the car, cannot enter):** confirmed: the client saw the M4.1 visual-only proxy (scripts, physics, colliders, interactions stripped by design) because the host simulated the car; the client's own original object was disabled.

### Fix
- **Presentation state machine** (`PresentationRules`, derived only from the host's records + local region, never from the local `CurrentCar`): `Real` (this instance simulates it) / `ParkedOther` (hideout, car home, other player simulates it) / `AwayProxy` (hideout, car in another region) / `RemoteVisual` (other regions) / `Hidden` (elsewhere, or not bought on the host — an unbought GT is never shown as owned, even if the client's sandbox has `VanBought`).
- **Hideout travel proxy = the original scene object** (no new models): activated if the original left it off (`StartVoid` as the original), `Rigidbody.constraints = FreezeAll` (no movement, no impact costs), colliders of its `CarBox`, `VanDoorTrigger` and `Place_Heavy_Trigger` disabled (no storage, van doors or heavy cargo), and the original E interaction is redirected: Harmony prefix on `CarController.EnterVehicle` (only caller `EnterCar.EnterVehicle` from `PlayerScript.Update`, tag `SitCar`) skips entering and opens the original travel menu like `OpenTownSelectionNoDelay` (`openedTownSelection`/`caughtUp` guards, `StartCoroutine(ShowTownSelection(0))`: story buttons, carrying and police restrictions unchanged; the stolen-car delivery branch is not used). The selection goes through the M4.2 host-authorized travel; a proxy car is never carried (it is not simulated by the traveller / not in its region). Engine, fuel and driving are unreachable because the player never sits in a proxy.
- `ParkedOther` copies follow the holder's pose (parking record, then interpolated snapshots, no physics); `AwayProxy` copies keep their hideout spot, moved to the free original spot (`GameManager.SpawnHideoutCar` / `VehiclesManager.secondCarHideoutPos`) only if they would overlap another car. No stripped proxies in the hideout (no overlapping geometry). Becoming `Real` restores constraints and colliders exactly.
- **Return/handoff (Bug B):** snapshot sequences are kept per vehicle id across scene reloads; receivers forget a vehicle's sequence and follow buffer whenever its region/holder changes (`FleetChangeTracker`).
- **Fail-safe hooks:** an exception while handling a *menu* travel click now holds the click (message, retry possible) instead of running an unauthorized original travel; seating on arrival fails to "on foot"; scripted transitions keep the original behaviour. F7 stays as recovery.
- Symmetric: whoever is present first simulates the garage cars; the other instance gets `ParkedOther` proxies; a car taken away by either player leaves an `AwayProxy` on the other's hideout; a returning car becomes `ParkedOther`/`Real` on both.

### Limitations (not implemented, not to be reported as working)
- **Client cannot drive or enter a car the host simulates** (and vice versa): no driver handoff yet; while both are home the first present player simulates both garage cars, the other uses E for travel. Passenger seats, passenger camera, travelling together: not implemented.
- **No storage on proxies**; the real car's CarBox works only for its holder (global, unsynchronized original storage — shared per-car inventory is a later milestone).
- The original "enter car" prompt still appears when looking at a proxy (E opens the travel menu instead).
- Outside the hideout the other player's car is still a collider-less visual (M4.1 proxy).

### Verified offline
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14, TravelTests **33/33** (9 new: hideout leave/return, proxy travel without carry both directions, fresh hideout, two away proxies, proxy restrictions, sequence restart, many transitions + reconnect, invalid/refused state), RedirectTest 3/3, launcher 21/21. Deployed `TS2MP.Plugin.dll` only. Offline tests do not show that the E redirect, colliders, freezing or the menu work inside Unity.

### Performance
- Event-driven: presentation changes only on registry changes; proxy interaction colliders cached per car object once; follow of `ParkedOther` = one buffer sample per frame per followed car (same cost as the M4.1 proxy, no extra renderer — the stripped clone is no longer created in the hideout). No new network messages. No FPS measured.

## M4.2 — Host-authoritative vehicle travel, region assignment, safe spawning — **FAILED manual QA (hideout bugs A–C) → fixed in M4.2.1, re-test pending**, 2026-11-10
Mod 0.4.2, protocol 7 (0.4.0 instances are refused). M4.1 stays PENDING MANUAL QA (not accepted by this milestone).

### Integration points (verified IL, CODEBASE_MAP §3.9 "M4.2 verified")
- Harmony prefix on the 14 menu `GameManager.Select*()` methods (`SelectJail` untouched). Only clicks while the travel menu (`PlayerScript.TownSelection` / `GameManager.TownSelectObj`) is open are coordinated; code-driven calls (arrest, heists, stolen-car delivery, hideout repairs, cheat) run unchanged and never carry a car.
- Prefix + postfix on `VehiclesManager.InitiateMainCar` (sets SPrefs `CurrentCar` to the car this instance may simulate, deactivates the other original car outside the hideout), prefix on `CarController.IsStartingCar` (seats only a traveller who brought that car), prefix on `PlayerScript.SpawnAtCar` (phone guard). No other game method patched; no game file changed.

### Host-authoritative travel (B, C)
- Client click → `travel` proposal {dest, candidate car, its final pose+fuel}; the original `Select*` is **held** (returns false; menu stays open, nothing saved or loaded). The host queues client proposals and applies them in `LateUpdate` (one serialized path); the decision commits the vehicle records + a `travelGrant` entry before the proposal result. On the result the client checks the grant id, the same `GameManager` and the open menu, arms a one-shot guard and invokes the same `Select*` once (the prefix lets exactly that call through). Denied → menu stays open, retry possible. Menu closed / scene changed / disconnect / 15 s failsafe → click abandoned, a late grant is cancelled (`travelCancel` → rollback).
- Host click → decided in place (no wait), the original continues.
- Rules (`TravelDesk`): existing destination car wins (Join if held/reserved by the other player, Parked if nobody is there → reserved for the traveller); empty destination → Carry if the traveller's car is carriable (in its region, unlocked, not reserved, not held/driven by the other, and the other player is not staying in that one-car region), else None (on foot). Hideout garage may store both cars. Heist/mission destinations → Special (original behaviour, fleet untouched).
- Simultaneous requests: host click and client request in the same frame → the host's is decided first; any committed reservation (client or host) is final — a later request only joins. A newer request from the same player supersedes (rolls back) its unfinished one.
- Reservation = Reserved flag + holder = traveller; committed when the traveller's region becomes the destination; rolled back on cancel, disconnect, arrival elsewhere after departure (scene load failure), superseding request, or the 180 s safety timeout. Duplicate/retransmitted requests get the cached result (ProposalGate); older ids are Stale.

### Duplicates and driving (D, E)
- Each car has one region and one holder (the instance with the original car active). `ReassignHolders` after every membership/reservation change: present holder kept, else lowest present slot (host first), else 0 (parked, logical only). Only the holder's local original car is active; the other instance deactivates its copy (forcing `ExitCarImmidiately` first if its player sits inside) and renders a stripped proxy from the holder's snapshots. Original car objects are only activated/deactivated, never destroyed or spawned.
- Both roles stream their held cars (20 Hz moving / 2 s parked, 38 B incl. fuel). The host validates sender = holder and snapshot region = record region = sender's region, and stores pose/fuel of client-held cars in `vpark` even when it is elsewhere. The client sends `vehicleClaim` when it sits in its held car; claims require holder = sender (no driver handoff yet).
- Hideout: copies of cars parked elsewhere stay as **frozen travel access points** (`Rigidbody.constraints = FreezeAll`, restored when held/shut down): entering + engine start opens the original menu, the car itself is not carried. A player without a car of its own in the hideout/Auto Shop/Verwood can open the original menu on foot with **F7** (`[Vehicles] TravelMenuKey`, calls `PlayerScript.OpenTownSelectionNoDelay`; not offered when the last vehicle was a stolen car).

### Spawning (F)
- After `loadedMap` (same coroutine step as the leave-point move), once per menu travel: Carry into Madison/Hillville → original seating kept. Parked → the car is put back to its parking pose (the original moved it to the leave point). Otherwise the player is placed near (1) the other player if on foot and < 3 m/s, (2) the region car, (3) the original spawn (hideout/Auto Shop/Verwood, kept if clear) / the leave point. Candidates: exact spawn/leave point + rings 2/3/4.5 m × 8, snapped to a walkable node of the game's A* graph when within 1.5 m. Checks: downward ray from +3 m finds ground (normal ≥ 0.7, not a vehicle body, within ±4 m of the anchor), capsule 0.35×1.8 m overlaps nothing but the player, ≥ 1.2 m from the other player, ≥ 3.2 m from a proxy car, no traffic car (`AITrafficCar`) within 6 m. Fallback: leave point with ground check, else original position (logged). The other player is never moved; no repeated corrections.

### Persistence and fuel (G)
- Host file `%LOCALAPPDATA%\TS2MP\fleet-<profile>.dat` (profile = PlayerPrefs `Profile`, verified mapping to `saveData*.thief`): version, profile, Story, day index, per car {id, kind, unlocked, committed region, pose, fuel}, FNV-1a checksum. Written on a worker thread (temp + flush + `File.Replace` keeping `.bak`), ≤ 1 s after a car record change, ≤ 10 s after parking-pose changes, synchronously at shutdown. Reserved cars are stored with their pre-reservation region (no partial transaction persisted). Load: corrupt main file → `.bak`; unusable or stale (save Story or day counter lower than recorded, i.e. another history in that slot) → renamed `fleet-<p>.<tag>-<utc>.dat`, start from the save (current car in the save's region, other car in the hideout, fuel = `GasTankLeftCar`). The client never writes it; client sandbox unchanged; the original save format is not modified (the game itself still writes its single `CurrentCar`/`XCar`/`GasTankLeftCar`, which the mod overrides on arrival).
- Fuel: the game's single `SaveGame.gasTankLeft` always belongs to the car this instance drives/holds; when that car changes the old value is kept per car and the new car's value loaded. Host parking records hold per-car fuel (from the host's own car and validated client snapshots/requests). Original consumption and the empty-tank refill are unchanged.
- Crash recovery: at most ~10 s of parking movement or ~1 s of assignment changes can be lost; a crash between the original save and the fleet write can leave them one travel apart (the fleet file wins for car regions). **Not tested.**

### Phone (§10)
- `SpawnAtCar` prefix: in fleet regions (Hideout, Verwood, Auto Shop, Hillville, Madison) the original runs only if `GameManager.Car` is the active car this player simulates; otherwise the original `cant_fast_travel` warning is shown and **no money is charged**. Heist/mission regions keep the original behaviour.

### Special transitions (§11, limitations)
- Arrest/jail, heist start, delivery, repairs: original behaviour; no car is carried; on arrival in a fleet region the registry decides activation (no unsynchronized second copy outside mission regions). Jail itself deactivates the car (original).
- Heist/mission regions: the original activates the current car at `Heist.carSpawn` locally; it is **not** registered, streamed or persisted (mission transport exempt). Two players in the same heist can each see their own local copy. Leaving a heist by menu goes through the normal gate (the heist copy is never carried: its car is assigned elsewhere).

### Known limitations
- No driver handoff: only the holder can drive. While both players are in one region the first present player (host first) keeps simulating its car; in the hideout the host simulates both garage cars while it is there, so the client drives a garage car only after the host left (F7 menu for travel meanwhile).
- While the client is disconnected its local held car stays active and unsynchronized; the host may take over the same car in a shared region → two copies until the client reconnects (then the client's copy is released).
- A placement after skipped seating starts from the MainScene default player position for ~0.5 s of the loading screen (Madison/Hillville), then the checked placement runs once.

### Not implemented (deferred, still mandatory)
Driver handoff between players, passenger seats/camera, travelling together, per-car storage/inventory, purchase/unlock synchronization (the host's `VanBought` decides), car-to-car collisions, damage economy, jail co-op, heist car synchronization, NPC/police.

### Verified offline
- Build 0 errors / 0 warnings. ProtocolTests 19/19, SyncTests 18/18, WorldTests 51/51, VehicleTests 14/14 (updated to holders/9 B records/38 B snapshots), **TravelTests 24/24 (new)**, RedirectTest 3/3, launcher 21/21. Deployed `TS2MP.Plugin.dll` only (LiteNetLib unchanged). These tests do not run Unity: no proof of seating, activation, placement, physics or file behaviour inside the game.

### Performance (offline probe, .NET Framework, not Unity)
- Host travel decision 5.5 µs; decision + arrival incl. setup 26 µs per trip. Per-frame held-car park + send policy 0.025 µs, 0 GCs per 1M frames. Fleet file 109 B, serialize 0.5 µs on the main thread, written on a worker thread.
- Network per trip: request 35 B + result 10 B + 2–4 deltas (record 9 B / park 29 B / grant 11 B, each ~+20 B framing); driving: snapshots 38 B at 20 Hz (≈ 0.76 KB/s payload) plus ≤ 0.5 park deltas/s from the host; parked: 1 snapshot / 2 s.
- Placement: one-off per arrival (≤ 76 candidates, each 1 ray + 1 capsule + 1 sphere query), duration logged (`ARRIVAL … in x ms`). In-game cost logged every 30 s in `VEHICLE_STATS … update avg/max us`. **No FPS measured.**

## M4.1A — Vehicle travel, region reservation & spawn audit — **completed (analysis only, no code)**, 2026-11-10
M4.1 (mod 0.4.0, protocol 6) stays **PENDING MANUAL QA**. New requirement recorded: at most one permanent player car per region (PROJECT_PLAN "Region vehicle rule"); the multi-car-per-region fleet rule is SUPERSEDED. Verified facts: CODEBASE_MAP §3.7, §3.9.

### Findings (V = verified in game code, I = inferred, R = requires manual test)
- V: travel needs no car — the menu opens on foot at any `MapBorder` (player child `Trigger Map`), via `ExitMapDoor` interactables, after bail, or by engine start where enabled; heists/jail/hideout repairs travel by code.
- V: on arrival in Madison/Hillville after travel the current car is moved to the nearest `LeavePoint` and the player is seated in it with the engine running (`IsStartingCar`), even after walking. Hideout/pawn shop/Auto Shop use fixed spawns; heists use `Heist` spawns (some without a car).
- V: the car is a scene object re-activated per load (`InitiateMainCar`); the save keeps one `CurrentCar`, one car position (`XCar…`), one gas value, global car storage.
- V: `SpawnAtCar` (phone) charges money before checking that the car is active → must be guarded when a player has no car in the region. I/R: otherwise money is lost without a teleport.
- V: `Select*` prefix runs before save and reload → a travel request can be issued before the original reload. R: whether deferring the button is acceptable in UX terms (not needed by the design below).

### Proposed design (P = proposed, not implemented)
- **State (host registry, existing epochs/revisions):** per car `{region, state Stored|Parked|Active|Reserved(dest, player, requestId)}`; per region the assigned car = the car whose record names it (derived, no second source of truth). Reservations live only in the host's memory + registry; a reservation that is not confirmed by arrival within 60 s, by disconnect or by a failed load rolls back to the car's previous region.
- **Travel request (P):** Harmony prefix on `GameManager.Select<Map>` sends one reliable proposal `travel {requestId, destRegion, carry = local current car id or 0}` and lets the original travel continue at once (no waiting). The host is single-threaded: proposals are applied in arrival order; within one host step the host's own pending request is applied before network proposals (tie rule). Decision per request: destination already has a car (Active/Parked/Reserved) → traveller gets that car, carry ignored; destination empty and carried car is free to move (not Active in a region where the other player stays) → Reserved(dest) for that car; otherwise no car. Results come back as an ordered proposal result + registry deltas (deduplicated by the existing ProposalGate; stale ids are refused).
- **Car leaves with its player only if nobody else stays in its region** (else it stays, Example B). Forced travel (jail, caught, hideout repair) never carries a car.
- **Arrival gating (P):** postfix on `VehiclesManager.InitiateMainCar` deactivates the local car unless the registry says it is this region's car (pending decision = keep it inactive, the player arrives on foot); prefix on `CarController.IsStartingCar` skips seating when the car is not ours; `GameManager.Car` is left untouched for save compatibility. Arrival confirmation = the existing `region` proposal (Reserved → Active). If the decision arrives after the scene is loaded and grants the car, the car is activated at the leave point it was moved to.
- **Joining an occupied region (P):** the region car is shown as the M4.1 proxy (driving handoff M4.3); the joining player is placed once, after `GameManager.loadedMap` becomes true: candidates on a ring 2–4 m around the other player (if on foot), else around the region car's `leave_car_transform`, else the original `LeavePoint`/spawn; each candidate snapped to the A* walkable graph (`AstarPath.active.GetNearest`, used by the game's NPCs) and accepted only if a downward raycast finds ground and `Physics.CheckCapsule` (non-trigger colliders) is clear and it is outside any car bounds. The other player is never moved.
- **Two-phase commit is not required:** one atomic host decision + an arrival confirmation with timeout rollback is enough (two players, one authority, ordered reliable channel).
- **Both cars elsewhere / hideout:** no car is created; the player travels and arrives on foot (legitimate: the menu works on foot), can rejoin a car's region, or travel with the other player later (passenger milestone). Hideout car objects for cars not stored at the hideout: kept visible as travel access points (menu via engine start) but never carry or relocate the real car; they are made non-drivable (P, R).
- **Persistence (P):** one small host-owned file per host save profile (`%LOCALAPPDATA%\TS2MP\fleet-<profile>.dat`: per car region, position/rotation, later inventory), written atomically (temp + rename) only on commit events and at most every 10 s; the host's original save keys stay untouched apart from what the game itself writes; client sandbox unchanged.
- **Performance (estimate):** event-driven; one reliable proposal (~20 B) + 1–3 registry deltas (~30 B each) per trip; one placement search per arrival (≤ 24 candidates, a few raycasts/overlaps, < 1 ms estimated); no per-frame work beyond the existing M4.1 snapshot path; no simulation of unloaded regions.

### Open questions for the user
1. Hideout objects of a car parked in another (empty) region: travel access only (proposed), or may they recall the car to the destination?
2. Heist trips that spawn a car (`Heist.carSpawn`): same region rule (proposed), or always on foot?
3. Fuel: keep the game's single `GasAmount` for whichever car is driven, or per-car fuel?
4. Jail/caught: the car stays where it was (proposed) — the original resets the car position to the hideout.


## M4.1 — Vehicle audit + shared-fleet foundation + host car replication — **PENDING MANUAL QA**, 2026-11-10
M1–M3.1.2 accepted in-game by the user.

### Audit results (V, read-only; details CODEBASE_MAP §3.7)
- **Two** persistent player cars: Ranger (`PLAYER CAR`, `carNr` 0, always owned) and Shadowhawk GT (`MUSTANG`, `carNr` 1, bought for $25,000 in the hideout purchase UI). No third owned car; no player van (the field `VehiclesManager.van` holds the Shadowhawk). Seven stealable region cars and hideout trophies are not fleet vehicles.
- Garage/travel: scene objects re-activated by `VehiclesManager.InitiateMainCar`; only the current car (`CurrentCar`) is spawned per region, the other only at the hideout; switching = entering the other car at the hideout before travelling (`SaveCurrentCar`). One saved car position and gas value.
- Inventory: one global car storage (`ItemID.item_amount_storage`, 20/30 kg) shared by both cars; heavy items via global `TrunkItem` and Ranger's `VanHeavyItems` (`HeavyVan_*`).
- Damage: repair cost charged immediately in `CarController.OnCollisionEnter` (≤ $250, ×2 vs traffic), only with the driver inside.
- Seats/camera: Ranger rear bench (`Female Sit Position` candidate centre-rear), Shadowhawk front passenger `Associate Sit`; enter = first person, engine start = shared `TPP_Camera_Controller` + driver model (`Set_TPP`).

### Implemented
- `Shared/Vehicles.cs`: stable ids (`carNr`+1), `VehicleRecord`, `FleetBook` (host registry in the existing StateRegistry: register/identity conflict/bounded, placement, driving leases, release-all), `FleetView`, `VehicleSnapshot` (36 B), `VehicleSnapshotFilter`, `VehiclePoseBuffer`, `VehicleSendPolicy`. `WorldHost`: `vehicleClaim` proposals, leases released on disconnect. Protocol 6 / mod 0.4.0, channel 2 for vehicle snapshots.
- `Plugin/VehicleSync.cs`: host binds the cars of its `VehiclesManager` on every scene load, registers them (Shadowhawk unlocked only with `VanBought`), tracks spawned/stored placement, engine and its own driver lease, streams movement of each spawned car in its region. Client shows each host car in its own region as a visual-only proxy cloned from its own copy of the original car (scripts, physics, colliders, audio, lights stripped), interpolated; proxies removed on region change, timeout (10 s), record change or disconnect; no change to the client's own cars, saves or inputs.

### Not implemented (deferred, still mandatory)
Client driving/leases from the client, driver handoff, client car registration and duplicate resolution, multiple fleet cars per region beyond what the host spawns, car-to-car collisions, damage economy changes, per-vehicle storage, passengers, passenger camera, shared travel, fleet persistence, safe spawning, unlock/purchase sync, police/missions.

### Verified offline
- Build 0 errors / 0 warnings. `ProtocolTests` 19/19, `SyncTests` 18/18, `WorldTests` 51/51, `VehicleTests` 14/14 (new), `RedirectTest` passed, `launcher-test.ps1` 21/21. Deployed `TS2MP.Plugin.dll` only.

### Performance
- Offline probe (.NET Framework, not Unity): owner send decision + encode + receiver filter/buffer/sample 0.042 µs per frame, 0 GCs per 1M frames. In-game cost logged every 30 s (`VEHICLE_STATS … update avg/max us`). Traffic per streamed car: ≈ 720 B/s payload (≈ 1.36 KB/s with UDP headers) while moving, ≈ 34 B/s parked; 28 B per fleet record change. One-time template clone per car kind on the client (time logged). No FPS measured.

### Known limitations / risks
- The client still has its **own** active car (from its sandbox save); if it is the same car as the host's (e.g. both Ranger) both copies are visible in the same region (logged `WARNING … unsynchronized duplicate`). Resolution needs client fleet reporting (next vehicle milestone).
- Proxies have no colliders: the client can drive/walk through the host's car (deliberate Option C, no economy effects).
- Host's driver avatar is not shown inside the proxy (the M2 avatar is hidden in vehicles).
- `Female Sit Position` / `Associate Sit` usability as passenger seats is unverified in game.

## M3.1.2 — Shared sleep time-lapse + shared vehicle fleet requirement — **PENDING MANUAL QA**, 2026-11-10
User-confirmed (M3.1.1 run): client sleep transaction, no rollback, region clocks, host sleep with client time-lapse, travel, movement. Remaining: a client sleeping in the host's region gave the host only the final time (jump), no sky fast-forward.

### Root cause (V: code + game IL)
- The host learned of a client sleep only from the completion request (sent after the client's fast-forward ended) and then caught up with `Step`: one write past each hour boundary per frame → a few frames, i.e. a jump. No signal existed at sleep start.
- Original visual mechanism (IL): the sky time-lapse is only Enviro's `GameTime.cycleLengthInMinutes = 0.2` (set by `PassTime.Update` while `passingTime`; `EnviroCore.UpdateTime` advances by `deltaTime / cycle`; no `timeScale` change). Hourly/daily effects come from `DayNightTime.Update` crossing hours (one per hour); `DayNightTime` only speeds its light lerp while cycle < 5. `PassStart` additionally sets local-only state (`PS.canInteract = false`, mixer pitch, parking camera, blur, cloud wind, UI) → **not** callable on the observer.

### Fix
- Protocol 5 / mod 0.3.3: new client proposal `sleepStart` {from, target hour = `PassTime.currentHr`} (5 B, reliable, deduplicated by the existing proposal gate) sent at `PassStart`; new `ProposalCode.Busy`.
- Host (`WorldHost`): validates the start (sender in region, from within 1 h of the region clock), allows **one** remote sleep at a time, refuses (`Busy`) while another runs or while the host itself sleeps in that region. A completion (`sleep`) is accepted only for the accepted start (same slot, region, start time); without it → `Invalid` (never advances).
- Host in the same region: `WorldSync.StartLapse` sets its own Enviro day length to 0.2 min (the game's own time-lapse, no UI, no input change) until the game's rule (hour index == target hour). The host clock is the region clock (host owner), published by its sampler as during the host's own sleep. On completion the host does **not** apply from+advance: it settles **forward only** to the sleeper's end time (Esc → elapsed time), then restores the day length (only if still 0.2) and republishes at normal rate. The client (non-owner) follows the host afterwards with tick-suppressed corrections.
- Host elsewhere / time-lapse unavailable / host left mid-sleep: completion commits from+advance (existing path, now always normal rate) and the host catches up hour by hour only if it is in that region.
- Stops: sleeper disconnect or travel (transaction dropped, region rate normalized), host travel, host's own sleep start (PassTime takes over), game changing the day length (slowdown/ending), 40 s expiry, shutdown.
- Client: completion only after an accepted (or still pending) start; rejected/unsent start or disconnect → no request, re-adopt the region clock.

### Duplicate / side-effect protection
- Host advances only forward through Enviro → exactly one natural `DayNightTime` tick per crossed hour (same as its own sleep); no backward write, no second advance at completion. Duplicate start/end ids → cached answer; reordered end before start → `Invalid`, then stale. Client corrections stay tick-suppressed.

### Verified offline
- Build 0 errors / 0 warnings. `ProtocolTests` 18/18 (+ v4 client rejected; UDP world test now start + end), `SyncTests` 18/18, `WorldTests` 51/51 (+10 time-lapse/transaction tests), `RedirectTest` passed, `launcher-test.ps1` 21/21. Deployed `TS2MP.Plugin.dll` only. Offline tests do **not** verify Enviro rendering.

### Performance
- Idle cost per frame: one bool, one null check, one static read. While a time-lapse runs: two compiled field reads + comparisons per frame on the host. No allocations, no per-frame logging, no per-frame network messages (one 5 B start + one 8 B end per sleep; host clock deltas as during host sleep). Estimate, not measured in-game; no FPS claim.

### Vehicle requirement (documentation only)
- `PROJECT_PLAN.md`: "one shared car per region" marked SUPERSEDED; replaced by the session-wide shared player fleet (unique vehicle ids, no duplicates, several cars per region, inventory owned by the vehicle, unlocks respected, fleet size unverified), shared driver/passenger usage, travel/spawn rules and open questions; M5 row updated. Nothing implemented.

### Remaining limitations
- Global calendar/economy unchanged: day/week, rent, bank and daily events advance only from the host's clock; a client sleep in a region without the host advances only that region's time.
- Host and client time-lapses start ≈ one network delay apart; the end settles on the host's clock (client follows within the blend/snap rules).
- If the host is already running its own sleep/slowdown when the client's start arrives, no time-lapse is shown (start refused or catch-up path).

## M3.1.1 — Client sleep synchronization fix + permanent rules — **PENDING MANUAL QA**, 2026-11-10
User-confirmed in-game before this fix: region clocks, independent travel, host sleep, movement/animations. Client sleep broken (time rolled back after sleeping).

### Root cause (V: game IL + client log pattern)
- `PassTime.Update` finishes a sleep that reaches its target hour **without calling `StopIt`** (CODEBASE_MAP §3.12). The mod detected the sleep end only through a `StopIt` postfix → for a normally completed sleep no `SLEEP end` and **no sleep request** was sent.
- After the player closed the sleep UI (`QuitIt` clears `passingTimeOn`), the client resumed as region owner and published its post-sleep clock as an ordinary report. The host model still had the pre-sleep time → report rejected `Invalid` (> 0.1 h off) → client re-adopted the old region time = rollback.
- Matches the log: `SLEEP start Hillville at 04:27` with no `SLEEP end`, then `PROPOSAL rejected by host id=152 Invalid` and `TIME adopt Hillville clock 06:02 -> 04:27` (06:02 = sleep target reached). The working case (`SLEEP end 23:52 -> 04:25`, ack id=151) ended off the hour boundary, consistent with an Esc abort, which does call `StopIt`.
- Not provable with the unpaired logs: which proposal id=152 was (a clock report is the only proposal type that logged no "sent" line). The new logs label it.

### Fix
- `SleepHooks`: remembers the running `PassTime` instance; `Poll()` (called once per frame, field read only while a sleep runs) fires the end on the `passingTime` edge; `StopIt` fires it on abort; once per sleep; repeated `PassStart` ignored.
- `SleepTransaction` (pure, tested): start/end/region check, pending id, result matching, 15 s timeout, cancel on region change, resolve on disconnect.
- `WorldSync`: client sync paused while sleeping or the sleep UI is open; sampler restarted after the pause; sleep end logs `completed`/`stopped`; request log includes from/advance/rev; rejected proposals logged with detail (host: request/report vs. model; client: clock report label). Host sleep path unchanged (it now also logs `SLEEP end` for completed sleeps).
- Mod version 0.3.2 (wire format unchanged; 0.3.1 builds are refused by the handshake).

### Verified offline
- Build 0 errors / 0 warnings. `ProtocolTests` 17/17, `SyncTests` 18/18, `WorldTests` 41/41 (+11 sleep-transaction tests), `RedirectTest` passed, `launcher-test.ps1` 21/21. Deployed `TS2MP.Plugin.dll` only.

### Performance
- New per-frame work: one static null check (`SleepHooks.Poll`), plus one Unity null check + compiled field read only during a running sleep; no allocations, no logging per frame. Not measured in-game; `WORLD_STATS update avg/max us` still reports it. Network: unchanged (one 31 B sleep proposal per client sleep). No FPS claim.

### Remaining limitations
- Global calendar/economy unchanged: day/week, rent, bank interest and daily events advance only from the host's clock; a client sleep in a region without the host advances only that region's time. The client's own hourly ticks during sleep still run on its sandbox copy (not shared). Global calendar is **not** complete.
- A sleep of exactly 24 h would measure 0 h and not be sent (not observed; slider limits unverified).

## M3.1 — Region-based time, performance, binding requirements — **PENDING MANUAL QA** (implemented; in-game NOT TESTED), 2026-11-10

### Verified findings (IL; CODEBASE_MAP §3.12, §7)
- Sleep is `PassTime`: `PassStart` (sets `passingTime`), cycle 0.2 min/day while sleeping, `StopIt` restores 28 min/day; hourly ticks run naturally during sleep. `passingTimeOn` = sleep UI open.
- Hourly/daily side effects (rent countdown, hours passed, abilities, day/week, ATM reset, bank interest, daily events) all hang off `DayNightTime.Update`'s hour-index comparison → the mod can neutralize its own writes by aligning `GameManager.currentHour`/`DayNightTime.prevHour`, and can produce exactly one legitimate tick per hour by stepping hour boundaries.

### Decisions
- Binding long-term requirements (shared progression, one shared vehicle per region [superseded in M3.1.2 by the shared fleet], spawn rules, passengers, police, connectivity, performance) recorded in `PROJECT_PLAN.md`; none implemented here.
- Global `world.time` replaced by one host-owned `clock` per region (+ `calendar`); rules in CODEBASE_MAP §7. Protocol 4 / mod 0.3.1 (M3 builds are refused).
- Client sleep is a validated request (`sleep` proposal, deduplicated by id); host catch-up with natural hourly ticks when it shares the region.
- Performance: compiled accessors, event-driven sleep hooks, cached lookups, throttled overlay, configurable `World.ClockRefreshSeconds` (default 10 s), in-game cost/bandwidth counters in `WORLD_STATS`.

### Verified offline
- Build 0 errors / 0 warnings. `ProtocolTests` 17/17, `SyncTests` 18/18, `WorldTests` 30/30, `RedirectTest` 3/3, `launcher-test.ps1` 21/21.
- Offline probe (not Unity): clock math 0.064 µs/frame, 0 GCs; 33 B per clock delta, one per 10 s per occupied region.

### Not verified (TESTING §7)
- Everything in-game: region detection, adopt on arrival, client sleep request + host catch-up, isolation between regions, reconnect restore, logged update cost.

### Known limitations / blockers
- **Global calendar vs. regional clocks (design decision needed):** day/week, rent, bank interest and daily events advance only from the **host's** clock (its own region's hours, its own sleep, or a client sleep in the host's region). Hours that pass or are slept in a region the host is not in do not advance shared progression. Changing this needs an explicit rule (e.g. which region's midnight advances the shared calendar) and touches unverified economy code → not forced in M3.1.
- The client's own hourly ticks still run locally on its sandbox copy (not persisted, not shared).
- A region with time locked by the game (`ProgressTime=None`, e.g. Haunted Mansion) publishes rate 0; entering players still adopt its hour.
- Catch-up needs `DayNightTime` to tick; otherwise fallback direct write (ticks suppressed, `stepFallbacks` counted).
- Registry (all region clocks) lives in memory for the host session; host quit → region times restart from the host's save time.

## M3 — Shared world foundation & independent regions — **tested in-game by the user (networking + global time OK); superseded by M3.1 for time**, 2026-11-10

### Verified findings (IL, read-only Cecil inspection; details CODEBASE_MAP §3.9, §3.12)
- Travel = `GameManager.Select<Map>` → `CurrentLocation` + full save → reload `MainScene` (Single) → `GameManager.Start` loads the region scene additively and spawns the player. The local player is destroyed and recreated each trip; no original travel code was changed.
- `CurrentLocation`/`GameManager.currentLoc` is a reliable gameplay region id when cross-checked with the loaded additive scene (0 Hideout/`PlayerHideout`, 3 AutoShop/`Auto Shop`, 99 MadisonSt/`MadisonSt`, …). The M2 scene key stays the avatar-visibility rule.
- Time of day lives in Enviro (`internalHour`); the game itself restores it with `SetInternalTimeOfDay` on load. Hour changes drive per-hour/per-day gameplay ticks in `DayNightTime` → backward corrections must suppress one tick.

### Decisions
- Design requirements recorded in `PROJECT_PLAN.md` ("Confirmed multiplayer design").
- Minimal state layer (CODEBASE_MAP §7): host-only commits with revisions and an epoch per host start, snapshot on (re)connect, ordered deltas, gap/epoch → resync, client proposals validated + acknowledged by the host with per-instance duplicate protection. Player-specific (`region`), region-specific (`lastVisit`, retained while empty) and session (`world.time`) scopes.
- First real property: **time of day** (+ day/week). Host authoritative; client follows through the game's own `SetInternalTimeOfDay`, blending small offsets, snapping large ones, never applying while the host clock is unavailable/locked or while the client sleeps.
- No new persistence: the registry is in memory; the host's normal game save stays the only persistent owner (time already saved by the game). Client sandbox unchanged.
- Region simulation ownership model defined (host if present, else first client present, else frozen) and logged; not used for simulation yet.

### Verified offline
- Build 0 errors / 0 warnings (plugin, launcher, 5 test programs).
- `ProtocolTests` 16/16 (incl. M2 client rejected, world state over real UDP: join snapshot, proposal ack, duplicate, rejection, reconnect resync); `SyncTests` 18/18; `WorldTests` 18/18; `RedirectTest` 3/3; `launcher-test.ps1` 21/21.

### Not verified (needs the user's in-game run, TESTING §6)
- Region detection and proposals in the real game; independent travel with the connection kept; time sync smoothness, day rollover and sleep behaviour; reconnect restore.

### Known limitations / risks
- Client sleeping does not advance shared time (client clock returns to the host's). Host sleep is followed.
- While the client's clock is corrected, its own `DayNightTime` hour ticks (rent timer, daily events) still run locally against its sandbox state; they are not shared yet (shared money/progress is a later milestone).
- A large forward snap counts as one hour tick on the client (same as the game's own load). Haunted Mansion and other `ProgressTime=None` states keep a local clock.
- The registry is lost when the host quits (no session file by design in M3).

### Next (recommended)
User runs TESTING §6. Do not start M4 without approval.

## M2 — Remote player rendering & movement sync — **accepted by user (in-game, 2026-11-10)**

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
