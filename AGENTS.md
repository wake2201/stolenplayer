# AGENTS.md — Thief Simulator 2 Multiplayer

## Purpose and precedence
This file defines the shared engineering rules for this repository. It is especially relevant when OpenAI Codex is asked to work on the project. Claude Code has additional instructions in `CLAUDE.md`. Read `docs/PROJECT_PLAN.md` for scope, milestone gates, and open decisions.

If a task conflicts with these rules or lacks necessary evidence, stop and explain the conflict. Never invent game APIs, verified results, file paths, or successful tests.

## Project target
Build a **two-player cooperative mod for Thief Simulator 2** on Windows. Player 2 must eventually be able to perform genuine in-game actions and advance the original missions; the host and client must agree on mission progress and relevant world state. Vehicles, NPCs, police, items, economy, scenes, and persistence must be handled as validated milestones—not assumed to work.

Favor original game systems and animation/assets rather than recreations. Do not modify or distribute proprietary game binaries/assets. Verify licenses before copying third-party code.

## Responsibility and editing discipline
- **Claude Code is the primary implementer and project coordinator.**
- **Codex is an optional, independent specialist** for bounded review, diagnosis, or a specifically delegated coding task. Do not invoke it automatically or after every change.
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
