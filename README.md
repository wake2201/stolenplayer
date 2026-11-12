# StolenPlayer

**Two-player co-op for Thief Simulator 2.**

Thief Simulator 2 was built as a single-player game. StolenPlayer is an ongoing attempt to make it work with a friend while keeping as much of the original game intact as possible.

The idea isn't to rebuild the game around multiplayer. It's to connect the systems that are already there: players, vehicles, interactions, time, inventory, and eventually the jobs themselves.

> **Work in progress.** This is an experimental project, not a finished mod or a public release. Expect bugs, incomplete features, and breaking changes between builds.

## Current progress

The core two-player session is running, and several systems have been tested in-game:

- **Player synchronization** — movement, basic animations, and visibility across both instances.
- **Vehicles** — shared car movement, driver handoff, passengers, wheel movement, collisions, and door interactions.
- **Passenger experience** — entry and exit, seat placement, camera modes, and native interaction prompts.
- **World state** — region-aware time synchronization and parts of the original world interactions.
- **Car storage** — the original CarBox UI with host-managed, single-player-at-a-time access; the latest inventory integration is still under QA.

Some newer systems are implemented but **still need in-game verification**, particularly Player 2's persistent inventory, item transfers, and synchronized sleep presentation.

### Still being worked on

- Travelling between regions **together in the same car**.
- Reliable inventory persistence and transfers across both players.
- Shared hideout storage, item drops, and economy behavior.
- Broader world and mission synchronization.
- Remaining audio and gameplay edge cases.

The project is being developed and tested incrementally; a feature isn't considered finished just because it compiles or passes offline tests.

## How it works

StolenPlayer is a **BepInEx 5 / Harmony** mod for the Unity Mono version of Thief Simulator 2. Networking uses **LiteNetLib**, with a host coordinating shared state and vehicle ownership.

Players install a drag-and-drop ZIP into the game folder, start the game normally and press **F8**: Player 1 hosts from a loaded single-player world (direct IP, UDP 27960, per-session join code), Player 2 joins from the main menu and plays in an isolated save sandbox. Internet play between two PCs has not been validated yet.

**Tested game target:** Thief Simulator 2 v1.27c  
**Current development build:** 0.12.0 · protocol 22 (pending two-PC manual QA)

Both instances need the same mod version and protocol.

## Building and testing

`tools/build.ps1 -Package` produces the release `build/release/TS2-Multiplayer-<version>.zip` (BepInEx 5.4.23.5 bootstrap + core, the plugin, LiteNetLib, licenses, German README and setup guide `docs/DIRECT_CONNECT_SETUP_DE.md`). It is a pre-release until the two-PC QA in `docs/TESTING.md` §27 has passed. `TS2-Multiplayer-Launcher.exe` is a developer tool for local two-instance tests only.

The project includes:

```text
src/Plugin/     Game integration and runtime hooks
src/Shared/     Protocol and shared game-state logic
tests/          Automated regression tests
tools/          Build tooling
docs/           Project notes, progress, and QA checklists
```

The build script is at `tools/build.ps1`. Building and running the mod requires a compatible local game installation and the project's development dependencies. See `docs/TESTING.md` for the current manual test plan.

**Back up your saves before testing.** Inventory and save synchronization are still under active development.

## Project direction

The end goal is straightforward: play through Thief Simulator 2 with another person, using the original game's mechanics wherever possible. Both players should be able to drive, interact, carry items, travel, and complete objectives without either player feeling like a spectator.

There's still a lot to finish, but the foundation is taking shape.

## Disclaimer

StolenPlayer is an unofficial fan-made project and is not affiliated with the developers or publisher of Thief Simulator 2. A legitimate copy of the game is required. Game assets and binaries are not included in this repository.

No open-source license has been assigned to this repository yet.
