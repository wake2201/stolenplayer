# PROGRESS

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
