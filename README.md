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
- **Car storage** — the original CarBox UI with host-managed, single-player-at-a-time access.
- **Doors, locks and windows** (Madison Street) — shared doors, normal lockpicking, windows and vehicle cameras were confirmed by the user in a two-PC internet run (0.14.2); some edge cases remain under QA.
- **Host-owned campaign** — Player 2 always loads the host's world in an isolated save sandbox; an optional isolated **multiplayer test campaign** (all skills, test money) protects real saves during testing.

Implemented but **still pending in-game verification**: the main-menu-only session start of 0.14.3, Player 2's persistent inventory and item transfers, shared dropped items and pre-placed loot, travelling together in the same car, and synchronized sleep presentation.

### Still being worked on

- The remaining vehicle presentation issue (car seen floating on the host while Player 2 drives; diagnostics added in 0.14.3).
- Drawers and other containers, the Hideout garage, the cat, the drone, police and missions (documented for later research, not started).
- Broader world and mission synchronization; remaining audio and gameplay edge cases.

The project is being developed and tested incrementally; a feature isn't considered finished just because it compiles or passes offline tests.

## How it works

StolenPlayer is a **BepInEx 5 / Harmony** mod for the Unity Mono version of Thief Simulator 2. Networking uses **LiteNetLib**, with a host coordinating shared state and vehicle ownership.

Players install a drag-and-drop ZIP into the game folder and start the game normally. **F8 works only in the main menu** (in a loaded world it does nothing):

- **Player 1** chooses *Hosten vorbereiten* (or *Testkampagne hosten*) in the main menu, which opens UDP 27960 with a per-session join code, then loads the save through the original *Continue*. Multiplayer becomes active automatically; Player 2 is admitted only once the host's world is fully initialized.
- **Player 2** joins from the main menu (address, port, code) and plays in an isolated save sandbox.
- Returning to the main menu ends the session for either player.

Direct-IP internet play between two PCs has been used by the user since 0.14.1 (doors, lockpicking, windows and vehicle cameras confirmed in 0.14.2).

**Tested game target:** Thief Simulator 2 v1.27c  
**Current development build:** 0.14.3 · protocol 26 (pending manual two-PC QA, `docs/TESTING.md` §32)

Both instances need the same mod version and protocol.

## Building and testing

`tools/build.ps1 -Package` produces the release `build/release/TS2-Multiplayer-<version>.zip` (BepInEx 5.4.23.5 bootstrap + core, the plugin, LiteNetLib, licenses, German README and setup guide `docs/DIRECT_CONNECT_SETUP_DE.md`); `tools/build.ps1 -Test` builds everything and runs all offline suites. Every release is a pre-release until its manual two-PC QA in `docs/TESTING.md` has passed. `TS2-Multiplayer-Launcher.exe` is a developer tool for local two-instance tests only.

The project includes:

```text
src/Plugin/     Game integration and runtime hooks
src/Shared/     Protocol and shared game-state logic
tests/          Automated regression tests
tools/          Build tooling
docs/           Project notes, progress, and QA checklists
```

The build script is at `tools/build.ps1`. Building and running the mod requires a compatible local game installation and the project's development dependencies. See `docs/TESTING.md` for the manual test plans and `docs/PROGRESS.md` for the status of each milestone.

**Back up your saves before testing.** Inventory and save synchronization are still under active development.

## Project direction

The end goal is straightforward: play through Thief Simulator 2 with another person, using the original game's mechanics wherever possible. Both players should be able to drive, interact, carry items, travel, and complete objectives without either player feeling like a spectator.

There's still a lot to finish, but the foundation is taking shape.

## Disclaimer

StolenPlayer is an unofficial fan-made project and is not affiliated with the developers or publisher of Thief Simulator 2. A legitimate copy of the game is required. Game assets and binaries are not included in this repository.

No open-source license has been assigned to this repository yet.
