# 11 — Architecture blueprint

Recommendation for reaching a cooperative original campaign **by extending the existing StolenPlayer architecture**. This is design (INFERRED from verified mechanics). Nothing here is implemented by this milestone. Existing pieces referenced: `SessionNode` (LiteNetLib, host-authoritative registry), `StateRegistry` (host commit / client snapshot+delta), `ProposalGate`, `RegionStateBook` (region epochs, reservations), `WorldIdentity` census + `SceneCatalog` (host-side scene files), `LootBook`, `CarBoxSync`, `P2Inventory` + wallet journal, `RegionClockBook` (region owner rule), `HostMirror`/`ClientMirror` (W2 key families), `SaveGuard` (client sandbox).

## 0. Can the current architecture carry full co-op without a rewrite?
**Yes for A, B, C, F and G below**:
- Region object transactions, the loot book, host storage transactions, the wallet journal and host scene catalogs are already the right shapes.
- They need new *kinds* and one new **campaign transaction** layer, not new transports.

**E (NPC/police) is the genuine architectural limit.** The original AI binds one `PlayerScript`, and no instance can observe a region it has not loaded. Shared NPCs therefore need:
1. region-owner simulation;
2. an observer presentation mode for NPCs on the non-owner;
3. a perception proxy plus reaction redirect on the owner.

That is new engineering on top of the existing region-owner rule, with the highest regression risk. It should come after campaign progression is shared.

## 1. Cross-cutting rules
| Rule | Reason (evidence) |
|---|---|
| **Host owns the campaign**: story index, residence/contract/job/payphone keys, unlocks, location/residence alerts, durable world keys, wallet, P2 record | single save writer (SaveGameNewSystem), PROJECT_PLAN |
| **Region owner simulates live region state** that needs a loaded scene (NPC AI, police units, physics settle of dropped/heavy objects); host commits durable consequences | the host cannot load regions it is not in (independent regions, M3) |
| **Transactions for exactly-once effects**: txn id, idempotent reply, first committed wins, debit/credit once, journaled before applying | wallet journal + loot book patterns (W4.1B, W4.2D) |
| **Snapshots for continuous state** (poses, NPC transforms, drone): unreliable, interpolated (`SnapshotBuffer`) | M2 |
| **Original code on the original objects**: replicas call original methods (`OpenOnStartt`, `OpenDestroyed`, `RelockDoor`, `AdvanceStory` on the host) or mirror only the visual part; documented per bridge | CLAUDE.md permanent rules |
| **Client never writes campaign keys durably**; the sandbox may hold mirrored host values | SaveGuard |
| **Identity**: census id for scene objects (never by GameObject name alone), `DynamicIdAllocator` for runtime entities, atlas ids for objectives | 01 §6 |
| **No mod text in gameplay UI**; invalid requests silently do nothing + log | AGENTS rule 4 |

## 2. Patterns
### A. Static world objects (doors, windows, furniture, destructibles, switches, locks)
- **Existing:** `WorldObjectSync` kinds Door/Lockpick/LockedWindow, pages `wobj<kind>[.p]`, `objTxn` (Set/Reserve/Release/NpcSet + epoch), region init chunks, host digest check.
- **Extend:**
  - new census kinds + state encodings for `DestroyObject` (stage), `Window_Blinds`, `LightSwitch`, `FuseBoxSwitch`, `SecurityCamera` (disabled), `SecurityPanel`, `SafeCrack`, `Vent`, `DigHole`, `WeldDoor`, `ElectricLock`, `ParcelLocker`, `Garage_Switch`/`CarGate`;
  - one small bridge per kind, each mirroring the documented original method;
  - enable the remaining namespaces (Warehouse, Bank, Forestside, Museum, …): whitelist + digest + QA, no new architecture (DRONE_SYSTEM §7).
- **Fix first:** the sliding-window enable ordering (05 §6 H1), once logs confirm it.
- **Lifetime:** RegionLife (cleared when the region empties, vanilla reset semantics). Keyed exceptions (notes, camera discovery, medkit) are campaign transactions (F).

### B. Inventory and acquisition
- **Existing:** `LootBook` + host catalogs (all scenes), wallet, P2 record, `CarBoxSync`, shared drops.
- **Extend:**
  - per-family rules for story pickups (→ F story txn);
  - drone pickups (→ C ownership);
  - special/mission items: trace `specialAdd` / `isMissionItem` first (UNKNOWN today);
  - hidden items, ATM money, pickpocket loot as loot-book families with a host decision;
  - payphone target items as runtime entities.
- **Exactly once:** the host decides, then journals, then applies. The client resumes the original coroutine (the existing W4.1 pattern).

### C. Physical entities (heavy objects, drones, carried objects, vehicles)
- **Vehicles:** keep M5.0 (original car object observed, kinematic, solid) and the region vehicle rule. No change.
- **Heavy items:**
  - `heavyLease` (request → grant/deny by census id, holder, expiry on disconnect);
  - the holder runs the original `PickUpHeavy`/carry;
  - observers hide the scene copy and show it attached to the avatar (existing W3 carried presentation);
  - `DropHeavy` → holder reports pose → host commits RegionLife relocation → observers place kinematic;
  - deposits (`HeavyItemPlacedInCar`, shed, shop) are host storage transactions on the original car (`CarBoxSync` analogue);
  - `StolenBigItems`, sale money and story `WasPlacedInCar` are applied once on the host.
- **Drones:**
  - ownership per player (item in the player's inventory record);
  - the pilot simulates;
  - pose snapshot (≈16 B, 10 Hz while active) presented with a script-free clone of the original drone model;
  - effects (camera disable) as A transitions;
  - upgrades as F campaign transactions (decision: per player or shared).

### D. Gameplay events (abilities, sounds, gas, alarms, tool use)
- **New `GameplayEvent` channel** (reliable, region-scoped, small): `{eventId, actor, kind, census/dynamic target, params}`. Examples: gas on/off, cloud spawned (pos), alarm triggered (object, offender), tool swing, noise (pos, radius), knock-out request, pickpocket request.
- **Presentation** (particles, sounds, the original prefabs) runs on observers with sensors disabled.
- **Gameplay effects** run only on the region simulation owner (NPC effects) or the host (campaign effects).
- **Crime events** carry the offender id so police target only that player.

### E. AI entities (NPCs, police, animals)
1. **Region simulation owner** = the existing `RegionClockBook` owner (host if present, else the client in that region).
2. **Observer mode** on the non-owner:
   - disable `AI`/`PolicemanAI` decision and steering (`enabled = false`, `RichAI` stopped);
   - drive transform + legacy `Animation` clip from owner snapshots (`AI` 100 residents + dynamic police; ~10 Hz, delta-compressed, interest-limited to occupied buildings);
   - dynamic police/pedestrian entities get `DynamicIdAllocator` ids and spawn/despawn events.
3. **Perception of the remote player on the owner:**
   - a proxy object (collider + tag/layer the SensorToolkit sensors detect) following the remote avatar, carrying the remote player's relevant flags (hiding, crouch, light, mask, torch, in-vehicle);
   - **reaction redirect:** Harmony prefixes on the reaction entry points that read `PS` (`StartPoliceChase`, `catchedPlayer` paths, `GetTazed`) route to the targeted player — local call, or event to the remote player's instance. Inventory must be done per method with IL evidence; this is the HIGH-RISK part.
4. **Owner change** (owner leaves): the new owner restarts from scene + hour (vanilla reload semantics). An optional hand-over snapshot of alerted NPCs and active chase.
5. **Police:** one `Police_Manager` per region on the owner; per-player chase flag; location alert committed by the host once.

### F. Campaign progression
- **New `CampaignTxnSync` component** (proposed in W4.2C Phase A, not built). Host-side handlers that run the **original** methods once:
  - `storyAdvance(stageKey, triggerId)` → host checks `currentStoryScript.story_description == stageKey` → original `AdvanceStory()`;
  - `missionAccept/Complete(house, i, step)` → original `Mission_Item`/`MissionComplete1` on the host (or an equivalent call sequence documented with IL when the host lacks the scene object, e.g. the host is in another region: then apply the reward/key part only — DESIGN COMPROMISE requiring approval);
  - contracts, jobs, payphone, heist start, BlackBay story sale, upgrades/garage/van/tips, ReadNote XP, cat.
- **Story replication:** registry value `story` (index + key + stage-start counter). The client applies the index to its `StoryManager` with presentation only:
  - a Harmony gate suppresses the client-side effects of `InitiateStory` (debt, job acceptance, `setPrefsTo1`, police chase on start, teleport, money) and of the poll flags (`StoryManager.Update` auto-advances on the client);
  - the client keeps `RefreshText`, buttons and `StoryObject` gating.
- **Poll conditions** evaluated only on the host. Design decisions needed:
  - which player's counters count (missions/contracts/money are campaign stats → host stats);
  - whose skill/level/location;
  - joint presence for home/jail skips.
- **Mirrored campaign key families** (extend W2): residence mission keys, `_alert`, location alert, contract/job/payphone keys, unlock flags, tips. Mirrored at scene load (vanilla reconstruction from keys) + live deltas.

### G. Persistence
- The host save stays the only campaign file. Player 2 data stays in the host-owned `p2inv` record (+ wallet journal).
- Every client-originated campaign change becomes a host transaction, journaled with a mod key or record and applied before the host's next checkpoint. That preserves original checkpoint semantics: the host's quit without saving rolls back both players, consistent with vanilla.
- Reconnect: client re-mirrors from the host live model + registry; transactions are idempotent by txn id; leases expire on disconnect.
- Late join: the existing campaign snapshot, then the live registry.
- Deferred keys (`mission_strings_to_set` …) stay deferred on the host. They are never mirrored as durable before the host checkpoint.

## 3. Message sketch (new)
| Message | Dir | Delivery | Size (est.) | Notes |
|---|---|---|---|---|
| `campaignTxn` {txnId, kind, args} | C→H | reliable | 16–40 B | idempotent |
| `campaignResult` {txnId, outcome, revision} | H→C | reliable | 12 B | |
| registry `story` | H→C | reliable (registry) | 8 B | index + start counter |
| registry `cmpN` campaign key pages | H→C | reliable | ≤ 800 B/page | like `wobj` pages |
| `heavyLease` / `heavyDrop` | both | reliable | 20–40 B | census id + pose |
| `gev` gameplay event | both | reliable | 12–32 B | region-scoped |
| `npcSnap` | owner→other | unreliable 10 Hz | ~12 B/NPC delta | interest-limited |
| `npcReq` | other→owner | reliable | 16 B | knock-out, gas, pickpocket |
| `droneSnap` | pilot→other | unreliable 10 Hz | 16 B | only while active |

## 4. Performance considerations (to measure in game, none measured here)
- Hot paths to add: NPC snapshot packing on the owner (≤ 100 residents per region, only active and near players); observer apply (transform + `Animation.CrossFade` on clip change only); gameplay events are sparse.
- Avoid per-frame reflection: use the existing `Fast` accessors.
- Host catalogs are already built off-thread (W4.2D).
- Bandwidth target: ≤ 10 KB/s per peer in a busy region (owner NPC snapshots dominate).
