# Thief Simulator 2 Multiplayer — Project Plan

Status: **Planning / Milestone 0 not yet started**  
Scope: **Windows, two-player cooperative modification of the original game**  
Development style: **one approved milestone, one Claude Code prompt, one verified report at a time**

## Vision and acceptance philosophy
Two human players should eventually inhabit the same world and progress through the original game together. Either player may perform valid interactions and mission actions. Completed objectives and the associated world/progression consequences must be reflected correctly for both players. The mod should use original game controllers, animations, UI, NPCs, and missions where possible rather than approximating the original game with replacements.

Success requires actual in-game manual tests, not only successful compilation or plausible architecture. Some mechanics may be infeasible or need game-specific workarounds; report and prove limitations rather than promising full coverage early.

## Fixed decisions
- Two players initially; Windows PC; local testing with **two concurrently running game instances**.
- User reports that manual dual-instance launching already works; verify before changing that mechanism.
- Eventually deliver a small `TS2-Multiplayer-Launcher.exe`: **first invocation Host; second invocation Client**, automatically connecting to a local multiplayer session.
- The launcher handles the **mod session connection**, not Steam/third-party account authentication.
- Proposed authority model: **Host-authoritative**, subject to technical validation.
- Original systems first: gameplay hooks, character/NPC models, animation controllers, inventory/mission/vehicle systems if compatible.
- User manually tests every gameplay milestone. Logs, builds, static checks, and unit tests supplement—not replace—manual tests.
- Claude Code develops; Codex is called only for a targeted task explicitly approved by the user. No simultaneous editing.
- Token efficiency is mandatory: persistent navigational code map, targeted inspection, minimal dependencies, no automatic multi-agent review loop.

## Working networking principles (to validate)
- Stable IDs for replicated objects, players, missions, and events.
- Host validates and applies authoritative changes; clients request actions and render confirmed state.
- Reliable, replay-safe messages for inventory, mission, money, and world-state changes.
- High-frequency state (movement/vehicles) uses appropriate snapshots, smoothing/interpolation, and ownership handling.
- Prevent duplicate rewards and duplicated pickups; plan late join/reconnect and scene transition explicitly.
- **Do not assume the economy design:** shared mission/world progress is required; shared vs. separate player wallets/inventories remains a design decision after inspecting original mechanics.
- **Do not assume all original missions are replicated safely:** validate one real end-to-end mission path early.

## Milestones and gates

| ID | Target | Exit gate / human evidence |
|---|---|---|
| **M0** | **Read-only game/repository reconnaissance and architecture discovery**. Identify installed game executable/runtime, modding approach, save risks, existing launch method, and relevant assemblies. Create verified `CODEBASE_MAP.md`, `PROGRESS.md`, and initial `TESTING.md`. **No game mod/launcher feature implementation yet.** | Technical report distinguishing verified facts, unknowns, risks, and feasible M1 design. User approves M1. |
| **M1** | Minimal legitimate Windows launcher and compatible mod bootstrap; first invocation Host, second Client; local handshake, session/version identity, separate instance logs, clean shutdown. | User starts both actual instances manually via launcher and verifies handshake/logs. Protect saves; block unsafe gameplay tests if isolation is unproven. |
| **M2** | Two player presences: verified original render model/animations, movement and facing synchronization, controlled camera/input separation. | Both instances can see a moving second player without destructive side effects. |
| **M3** | First world-interaction vertical slice: a real, stable world object and one original interaction, with authoritative state and duplicate-action prevention. | An interaction by either player is visibly consistent on both instances. |
| **M4** | **Early mission vertical slice**: one genuine original mission step/action performed by Player 2, propagated through the original progression mechanics. | Same confirmed mission objective/progress on both sides; no duplicate reward. This validates architecture before broader systems. |
| **M5** | Vehicle synchronization: one vehicle, one authoritative driver/physics source, enter/exit and smooth remote observation; extend only after validated. | Reproducible two-instance driving test with tolerable sync and consistent entry/exit state. |
| **M6** | NPC/police and world-system synchronization: discovery, suspicion/chase states, scene transitions and relevant interactions. | Both instances show consistent response to a representative player-triggered event. |
| **M7** | Broader missions, progression, inventory/economy, persistence, reconnection and late joining. | Repeatable playthrough sample with Player 2 completing objectives and correctly restored shared state. |
| **M8** | Reliability, edge cases, packaging, documentation, regression/performance tests. | Manually verified two-player test matrix and clearly documented limitations. |

Each milestone is separate. Do **not** batch M0+M1 or implement later functionality without written approval. If an earlier gate reveals a major blocker, redesign the next milestone before writing its prompt.

## M0 research priorities (not yet verified)
1. Installed game location and version; Unity/Mono/IL2CPP determination by local evidence.
2. Real mod loader/plugin compatibility; runtime target framework and assembly references.
3. Existing dual-instance launch process; process IDs, game command line, and legitimate launcher constraints.
4. Original save paths, Steam Cloud behavior if applicable, backups, concurrent writes/collision risks.
5. Targeted class/assembly map: player, missions, inventory, objects, vehicles, NPC AI, saves, scenes, animation.
6. Transport compatibility options; do not select final library without runtime evidence.
7. License/readme review of any external reference or reusable code.

## Milestone execution contract
- One prompt given by the external architect for one specific milestone.
- Claude works inside the authorized repo, performs evidence-based minimal implementation, and documents findings.
- Mandatory concise final report: changed files, real build results, verified facts, blockers, manual test checklist and outcomes.
- Human user tests where necessary and reports results/logs; architect checks and authorizes next milestone.
- No next milestone is started automatically.

## Risks to track
- Original single-player logic might assume one player, one camera, one save, one active mission owner.
- Shared local save directory could create conflicts when two processes run.
- Native Unity physics and animation are not automatically network deterministic.
- Existing mods can be outdated or have code/license restrictions.
- Two game instances on one PC may affect performance; remote multiplayer needs additional testing later.
- Automatic multiplayer connection must not be confused with Steam credential automation.

## Open decisions (defer until supported by evidence)
- Game runtime and final mod loader configuration.
- Transport library and host discovery details.
- Shared wallet versus separate wallets; inventory ownership and reward distribution.
- Host save ownership and client-side save suppression/isolation strategy.
- Reconnection policy and handling of mission scenes/teleports.

## Reference entry points (unverified compatibility)
- BepInEx documentation: https://docs.bepinex.dev/
- Thief Simulator 2 mod listings: https://www.nexusmods.com/thiefsimulator2
- Open-source TS2 plugin example: https://github.com/xiaodong2008/BepInEx_ThiefSimulator2
- LiteNetLib: https://github.com/RevenantX/LiteNetLib
- Nitrox: https://github.com/SubnauticaNitrox/Nitrox

References are not validated against the user's game build. Check compatibility and reuse licenses before adopting code.
