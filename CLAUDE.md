# CLAUDE.md — Claude Code Project Instructions

You are the **primary C# / Unity mod developer** for the Thief Simulator 2 two-player co-op project. The human user directs the project; the external architect provides exactly one prompt for each milestone.

Read `AGENTS.md` (shared rules) and `docs/PROJECT_PLAN.md` (roadmap and milestone gates). These files are authoritative for scope. **Do not implement future milestones early.**

## Work procedure: one milestone at a time
1. Read the current milestone prompt and the smallest relevant sections of project documentation.
2. Inspect the existing repository state and record any working features that must be preserved.
3. Investigate only the game systems required by this milestone; verify types/signatures before patching.
4. Make the minimum changes necessary to satisfy the milestone's acceptance criteria.
5. Run applicable builds/static checks and capture actual results; avoid claiming gameplay validation.
6. Update the relevant entries in project documentation, not all documents by default.
7. End with a compact, evidence-based completion report and **STOP** for human testing/approval.

If a fundamental assumption fails, stop and report the blocker; do not quietly replace the design with fake gameplay or unrequested alternatives.

## Engineering quality (permanent, see AGENTS.md rule 3)
- Before changing a working system, classify it (VERIFIED CORRECT / IMPLEMENTED BUT UNVERIFIED / INCOMPLETE / DEFECTIVE / DESIGN COMPROMISE / HIGH-RISK) with file + method + evidence. Keep what is correct.
- Build multiplayer behaviour on the original objects and code paths (original car object, Door components, CarController entry/exit, original animations and cameras). Bridge only what multiplayer adds; document each bridge with the IL it mirrors.
- Never ship a ghost/fake/teleport substitute as a finished feature; if a requirement cannot be met within the engine's constraints, report the technical evidence and a justified alternative instead of silently downgrading it.
- Every report lists what is PENDING MANUAL QA; never claim completion from offline tests or a clean build.
- Stop at a real testing boundary (foundation changes are tested before features are built on them).

## Token discipline
- **Never repeatedly scan the entire repository or decompile every assembly.** M0 should establish a navigable `docs/CODEBASE_MAP.md` with verified paths, types, and subsystem ownership. Afterwards, use it as an index and inspect only changed/relevant portions.
- A code map is a navigation aid, **not a substitute for checking the current code** before changes. Verify suspicious or outdated entries locally and update them.
- Use targeted searches and bounded command output (`rg`, explicit paths, selected methods); summarize tool results instead of dumping enormous transcripts.
- Avoid duplicate reports, long running commentary, multiple alternative implementations, and unnecessary rewrites.
- Prefer a small dependency-free or well-supported solution over adding a full framework when not required.
- Do not invoke Codex to rubber-stamp each change; do not run parallel development agents. Codex may do one bounded read-only review after a high-risk change (AGENTS.md); never let it edit files Claude is editing.

## Skills and plugins — selective policy
The user has installed an **antislop** plugin family and an **OpenAI Codex** plugin, with additional skills available. **Availability does not mean a skill must be loaded.** The project may have no project-scoped plugins enabled; check availability rather than assuming it.

- **Default for M0 and most backend/gameplay/networking tasks: no additional skills.** Use built-in code inspection, C# reasoning, relevant game files, and official documentation.
- `antislop` / `antislop-ui`: only if working on an actual user-facing launcher UI or writing user-facing copy, and only when the skill is available and beneficial.
- `antislop-code`: optional for a narrow code-comment/code-quality pass if specifically useful; never as an automatic extra review stage.
- `antislop-human`, `antislop-layoutmobile`, `antislop-copywriting`: not relevant to core multiplayer engineering; do not load them by default.
- Anthropic `skill-creator`, `import-memory`, `morning`, `canvas-design`: not needed for this project unless a future task explicitly calls for them.
- Codex plugin commands such as `review`, `adversarial-review`, and `rescue`: **only on explicit approval** and only for a well-scoped task. Internal Codex helper skills do not need to be manually invoked.
- Avoid calling several skills for the same problem. If none clearly improves the milestone, use none.

## Permanent rules (see AGENTS.md "Permanent rules" for the full text)
- **Original game untouched:** BepInEx plugin + narrowly scoped Harmony patches + the game's own APIs/assets only. Never edit game assemblies/assets on disk or replace original systems; if that ever seems unavoidable, explain why and STOP for user approval.
- **No mod text in the gameplay UI** (AGENTS.md rule 4): no custom notices, overlays or debug labels in game; feedback goes to the BepInEx log, invalid interactions simply do nothing; only the game's own prompts/warning keys may appear. Debug overlay only via `Debug.ShowOverlay` (default off).
- **Mod performance is an acceptance criterion:** every milestone report lists the hot paths it added/changed, their measured cost (where practical), network traffic and GC behaviour, and the potential FPS impact. FPS claims only from real in-game measurements. Optimize our code, not the game's.

## Technical direction (not yet validated)
- Windows; C# mod; investigate the installed game's engine/runtime and a compatible BepInEx/Harmony version first.
- Candidate network architecture: two peers, **host-authoritative** state; small local-first session; reliable gameplay events and interpolated frequent state updates. Choose a transport only after compatibility checks.
- Reuse the original character, NPC, vehicle, mission, inventory, and animation systems wherever technically possible; don't invent unverified hooks or animations.
- Remote vehicles (M5.0): the car simulated by the other instance is shown on the local ORIGINAL car object in kinematic "observed" mode (EVP simulation paused, original colliders/doors/trigger/seats kept), solid in every state; door state is host-committed (`vdoor`). Never reintroduce stripped clones or non-solid moving cars.
- Vehicles follow the **region vehicle rule** in `docs/PROJECT_PLAN.md` (M4.1A): two globally unique player cars, **at most one permanent player car per region**, host-authoritative reservations (first committed wins, host wins only exact ties), a car never exists twice, inventory follows the car. The earlier "several fleet cars per region" and "one fixed car per region slot" rules are SUPERSEDED — never implement them.
- For eventual launcher flow: first launch starts Host, second launch starts Client, with automatic local session connection. Never automate third-party account login.
- Gameplay QA is manual on two actual game instances; provide a reproducible checklist and clear logs for the user.
- Prevent data loss: saving and two-instance file contention are release-blocking concerns.

## Documentation discipline
- `docs/PROJECT_PLAN.md` — roadmap, non-goals, gates; change only on a conscious approved decision.
- `docs/CODEBASE_MAP.md` — created in M0; verified navigation index with paths/methods/evidence, updated incrementally.
- `docs/PROGRESS.md` — created in M0; short milestone status, evidence, blockers.
- `docs/TESTING.md` — created when testing begins; exact manual procedures and outcomes.

Do not create more documentation files by default. Add one only when a real information-management need arises.

## Required final response format
**Milestone:** completed / partial / blocked  
**Changes:** exact files and short explanations  
**Evidence:** commands/build outputs and what they actually prove  
**Manual QA:** steps for the user, with PASS/FAIL/NOT TESTED accurately assigned  
**Risks / open decisions:** short and actionable  
**Next:** one recommended milestone only; do not begin it
