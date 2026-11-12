# AGENTS.md — Thief Simulator 2 Multiplayer

## Purpose and precedence
This file defines the shared engineering rules for this repository. It is especially relevant when OpenAI Codex is asked to work on the project. Claude Code has additional instructions in `CLAUDE.md`. Read `docs/PROJECT_PLAN.md` for scope, milestone gates, and open decisions.

If a task conflicts with these rules or lacks necessary evidence, stop and explain the conflict. Never invent game APIs, verified results, file paths, or successful tests.

## Project target
Build a **two-player cooperative mod for Thief Simulator 2** on Windows. Player 2 must eventually be able to perform genuine in-game actions and advance the original missions; the host and client must agree on mission progress and relevant world state. Vehicles, NPCs, police, items, economy, scenes, and persistence must be handled as validated milestones—not assumed to work.

Favor original game systems and animation/assets rather than recreations. Do not modify or distribute proprietary game binaries/assets. Verify licenses before copying third-party code.

## Permanent rules (every milestone)
**1. Preserve original game code.** Never modify the original game's source, binaries, assemblies, or assets unless a specific technical necessity is established **and** the user explicitly approves it. Prefer, in order: BepInEx plugins, narrowly scoped Harmony runtime patches (only when technically necessary), the game's own public/internal APIs, existing game assets loaded at runtime, small compatibility adapters inside the mod. Do not rewrite or replace original systems (movement, animation, physics, AI, missions), patch assemblies on disk, make destructive changes to the installation, or change game behaviour unrelated to the current milestone. If on-disk modification becomes unavoidable: explain why and **stop for user approval**.

**2. Mod performance.** Minimize the overhead *our* mod adds (CPU, frame time, allocations/GC, bandwidth, memory, main-thread work, Harmony patch cost, logging, scene transitions). Do not spend time optimizing or refactoring the original game's code.
- No `GameObject.Find`/`FindObjectOfType`/scene-wide searches in per-frame paths; cache Unity references and invalidate them on scene changes.
- No allocating LINQ, string formatting, or logging in `Update`/`FixedUpdate` hot paths; logging conditional and rate-limited.
- Cached accessors instead of repeated reflection; no expensive per-frame work without justification; bounded buffers/queues.
- Fixed send rates (not per rendered frame); never transmit unchanged shared state; interpolate frequently updated remote objects; no work for unloaded regions; no blocking file I/O or networking on the main thread.
- **Verification per milestone:** identify the performance-sensitive paths added/changed, measure them where practical, record network traffic and relevant memory/GC behaviour, compare with the previous mod version when meaningful, and report potential FPS impact. **Never claim FPS improvements without real in-game FPS measurements.**

**3. Correct implementation over shortcuts (permanent, 2026-11-11).** Enforceable rules:
- **Original systems first.** Reuse the game's own objects, components, colliders, animations, interaction paths and methods. A replacement (clone, substitute animation, custom key handler, teleport-into-seat) needs a written reason why the original cannot be used **and** user approval.
- **No workaround presented as a feature.** An approximation (e.g. a non-solid remote car, a door that never opens, an instant seat teleport) is recorded as `DESIGN COMPROMISE` or `INCOMPLETE` in `docs/PROGRESS.md` and never marked done. A required feature is never removed or downgraded to make a test or milestone pass.
- **Verify, don't recall.** Original behaviour is established from IL/scene data (cite method + IL offsets or asset path) or a user baseline test in unmodified single-player. Unverifiable facts stay `unknown` with a proposed in-game test.
- **Multiplayer may differ internally** (authoritative simulation + local representation) as long as gameplay behaviour and the invariants hold: exactly one simulating instance per car, host-committed shared state, no duplicated rewards/costs/items.
- **Status vocabulary for every subsystem in reports:** `VERIFIED CORRECT` (in-game evidence + code review), `IMPLEMENTED BUT UNVERIFIED`, `INCOMPLETE`, `DEFECTIVE`, `DESIGN COMPROMISE` (state whether necessary/avoidable/unacceptable), `HIGH-RISK`. New code ships as `PENDING MANUAL QA` until the user confirms it in game.
- **Done means:** specified behaviour exists, offline tests pass **and** the user verified it in game. A compiling intermediate architecture is not a completed milestone.
- **Testing boundaries:** do not combine independently risky changes (e.g. collision rewrite + new passenger system + persistence changes) in one untested deployment; stop for user QA when a foundation changes.
- **No unnecessary rewrites:** keep working, verified systems; change them only with evidence of a defect or a requirement they cannot meet. Simple is preferred when it is genuinely correct; fewer lines is not a goal.
- **Never** modify installed game assemblies/assets, never delete or overwrite user saves, never commit without user approval.

**4. No mod text in the gameplay UI (permanent, user preference 2026-11-11).** The game must feel unmodified. The mod never shows its own notices, toasts, popups, status overlays or debug labels in game (no "Multiplayer: ...", seat/handoff/travel/authority status, implementation-stage hints). Routine multiplayer feedback goes to the BepInEx log only. An invalid or refused interaction does nothing, like the original game; the game's own UI, its original prompts and its original warning texts (e.g. `cant_fast_travel` where the game itself would refuse) stay untouched. Never perform an unsafe operation to avoid an error. A critical unrecoverable failure is logged and handled safely (e.g. fail-closed quit for save protection), never by corrupting data. Diagnostics overlays exist only behind an explicit config switch that is off by default (`Debug.ShowOverlay`). **Exception (N1.2, requested by the user, 2026-11-12):** the multiplayer menu that the player opens with F8 (`src/Plugin/MultiplayerMenu.cs`; Host/Join, connection status and errors in German) — drawn only while the player has it open, never as an unsolicited notice. Enforced by `tests/InteractionTests.cs` case 15 (source scan: own IMGUI drawing only in `MultiplayerMenu.cs` and the opt-in overlay in `Plugin.cs`).

## Responsibility and editing discipline
- **Claude Code is the primary implementer and project coordinator.**
- **Codex is an optional, independent specialist** for bounded review, diagnosis, or a specifically delegated coding task. Do not invoke it automatically or after every change. Good uses: one focused read-only review after a high-risk implementation (moving-vehicle collision, seat/handoff races, travel transactions, persistence, complex hooks), with named files, constraints and questions. Claude evaluates its findings critically and implements accepted changes itself, sequentially. No review loops without new evidence.
- Only **one AI may edit project files at a time**. Never have Claude and Codex edit concurrently; complete, report, and hand off explicitly.
- For Codex review requests: default to **read-only review** with concrete findings, severity, affected files/lines, evidence, and minimal proposed fixes. Never rewrite code during review unless explicitly authorized.
- Do not initiate background Codex jobs unless the user explicitly authorizes them. Wait for completion before any overlapping edits.
- The user/architect approves each new milestone. **One milestone = one implementation prompt = one final report.** Do not start the next milestone without approval.

## Evidence-first development
- Identify the **actual installed game build**, runtime (Mono/IL2CPP), assembly versions, mod loader, save paths, and compatibility before designing hooks. Earlier internet examples are not proof of compatibility.
- Prefer targeted inspection of existing code/assemblies to repetitive full-project scans. Create or refresh `docs/CODEBASE_MAP.md` during M0; consult it on later tasks and inspect only relevant files or methods unless there is a documented reason to broaden the search.
- Distinguish **verified**, **hypothesis**, **unknown**, and **blocked** in technical notes.
- Do not hardcode game class or method names before verifying them.
- Use stable object/event identities, clear authority ownership, and replay-safe/idempotent events when implementing networking.
- Keep implementation minimal: no speculative architecture, invented gameplay substitutes, unnecessary dependencies, or giant refactors.

## Safety, launch, and persistence
- The launcher must invoke the legitimate installed game normally. No DRM/Steam bypasses, account impersonation, credential storage, or automated Steam sign-in.
- “Automatic login” means **automatic host/client connection to our own multiplayer session**, not login to a platform account.
- The user reports that two game instances can already be started manually. Preserve that mechanism until its behavior is verified.
- Do not alter real save files or run potentially destructive multi-instance tests until backup, save-path discovery, and collision risks are addressed. If save isolation is not proven, block risky in-game testing and document it.
- Preserve user files and changes; inspect before editing; do not delete or overwrite without authorization.

## Verification and reporting
- Builds, static checks, isolated unit tests, and log analysis are encouraged. **They do not replace manual in-game testing** by the user on two instances.
- Never mark an in-game test PASS without actual user results or corresponding game-session evidence. Keep untested cases as `NOT TESTED`.
- Prefer concise, structured, per-instance diagnostics; do not emit noisy per-frame logs by default.
- Finish every requested milestone with: changed files, evidence/build commands and results, actual manual tests vs. pending tests, known risks/blockers, and the smallest proposed next step.

## Codex-specific operating mode
Unless an explicitly approved task says otherwise:
1. Start from the current requested scope and existing documentation, not from a full repository reread.
2. Review **only changed or implicated files** and relevant game integration points.
3. Report actionable defects first, ordered by severity; do not write generic praise or perform unsolicited refactors.
4. Do not install tools, invoke other agents, or modify code as part of a read-only review.
5. Keep token use proportional to the task and end once the bounded objective is satisfied.
