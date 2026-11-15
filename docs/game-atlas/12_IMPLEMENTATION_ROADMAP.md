# 12 — Implementation roadmap (dependency-aware)

Proposal for the architect. `docs/PROJECT_PLAN.md` remains authoritative and changes only on an approved decision. Each milestone ends at a manual two-PC QA gate (CLAUDE.md). Order principle: close the open QA debt, then **campaign progression** (the central co-op goal and the dependency of story pickups, missions and heists), then physical entities, then AI and police (highest risk).

| # | Milestone | Scope | Depends on | Testable acceptance (manual, two PCs) |
|---|---|---|---|---|
| R0 | QA closure 0.16.1 / 0.17.0 + window diagnostic | run TESTING §36/§37; collect logs for the 108 sliding window (05 §6) | – | loot all regions, wallet, spawn semantics PASS; window hypothesis confirmed or refuted from logs |
| R1 | Sliding-window fix + remaining region enablement | fix the `LockedWindow`↔`Door` enable ordering (only if R0 confirms H1); enable object sync for Warehouse/Bank/Forestside/Museum/Jail/Haunted/Swat/DrugFarm/Intro | R0 | windows at 108 open/close on both; doors/locks in a heist region |
| R2 | **Campaign transaction layer + shared story index** | `CampaignTxnSync`; registry `story`; client `InitiateStory` side-effect gate + poll gate; host-side `storyAdvance` for all external trigger families (02 §5); decisions on counters, skill and location ownership recorded in PROJECT_PLAN | R0 | stages advanced by either player appear on both; no double debt/job/teleport; host save holds the index; client restart shows the same stage |
| R3 | Story pickups and story objects | enable `advance_story_if_picked` / `placed_car`, camera-view PC, ReadNote, ATM/SecurityPanel/SecurityCamera story hooks through R2 | R2 | stage 15 drone pickup by P2 advances the story once (drone item → R6) |
| R4 | Residence missions, contracts, jobs, payphones, BlackBay story sales | host transactions; step counter replication; mirrored campaign key pages; remove the UNSAFE sandbox drift (client completions become proposals) | R2 | P2 completes "Smash a few things" at 102: one reward, key = 2 on host, marker gone on both; contract claim once |
| R5 | Heists | `HeistManager.StartHeist` as a host transaction + joint travel to heist scenes (fleet/joint travel exists) | R2, R1 | Forestside heist started by either player: story++ once, both arrive |
| R6 | Drone ownership + presentation | per-player drone item/equip, pose snapshots, camera-disable transitions, upgrades as campaign txns | R2, R1 | P2 collects the 103 drone (story once), flies; P1 sees it; camera disabled for both |
| R7 | Heavy items | `heavyLease`, drop relocation, trunk/van/shed/shop deposits on the original car, `StolenBigItems` once | R1 (region objects), CarBoxSync | P2 carries a TV to the Ranger; P1 sees carry, load, sale; stage 61 counter |
| R8 | Static world object kinds II | breakables, blinds, lights, fuse boxes, safes, vents, dig spots, welded/electric locks, parcel lockers, gates | R1 | each kind replicated, reset on region empty |
| R9 | Gameplay events + gas presentation | `gev` channel; gas stream/clouds presentation; noise events; alarm presentation | R8 | P1 sees P2's gas; no NPC effect yet on the observer copy |
| R10 | **NPC observer mode** (region owner simulation) | owner snapshots of residents; observer disables `AI`; dynamic pedestrians optional | R9 | both see the same resident walk/sit/sleep; owner change on leave works |
| R11 | NPC perception of both players + reaction redirect | proxy for the remote player; redirect `StartPoliceChase`/catch/taser to the right player; gas/knock-out/pickpocket requests to the owner | R10 | NPC spots P2 while P1 is hidden: only P2 is chased; gas by P2 sleeps the NPC on both |
| R12 | Police | one region `Police_Manager` on the owner; units replicated; per-player stars/chase; host alerts once | R11 | the 04-scenario: P2 breaks an alarmed window → one police response, P2 wanted, P1 not |
| R13 | Progression ownership | per-player XP/skills/abilities persistence (record), shared vs per-player upgrades per decision | R2 | P2 levels and keeps skills across restarts |
| R14 | Ending, jail, intro and full-campaign pass | ending sequence for both, jail stages 26–28 for two, intro (stages 0–2) with two players, full playthrough checklist | all | campaign completable start to end with two players |

Parallelizable after R2: R6/R7/R8 (different files and systems). R10–R12 are strictly sequential.

Per milestone (AGENTS/CLAUDE rules): hot paths, network traffic, GC and FPS impact measured in game; exact QA steps in `docs/TESTING.md`; PENDING MANUAL QA listed.
