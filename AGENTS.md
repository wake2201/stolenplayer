# Project instructions

## Vanilla-First Multiplayer Implementation — Highest Priority

Every multiplayer feature must be based on verified original Thief Simulator 2 behavior. Inspect vanilla implementation before modifying or extending any mechanic. The mod may enable Player 2 to perform original gameplay actions and synchronize their legitimate outcomes, but must not invent gameplay mechanics, remove native restrictions, change persistence rules or improve vanilla systems without explicit user authorization. Necessary multiplayer-only adaptations must remain minimal, scoped and reversible.

This rule applies to every future milestone and Codex task and supersedes conflicting
earlier feature requirements. The base game defines the mechanics; Player 2 receives
the same original gameplay capabilities as Player 1.

- Understand vanilla → preserve vanilla → enable Player 2 → synchronize both players.
- Inspect native interaction conditions, component dependencies, animations, state
  transitions and persistence before implementing. Use short controlled vanilla
  observations only when native code inspection cannot resolve a specific question.
- Identify the original Player 1 dependency, extend only the integration needed for
  Player 2, and replicate legitimate outcomes while retaining native restrictions.
- Apply this to vehicles, doors/locks/breaching, inventory/storage, buying/selling,
  keys/money, tools/equipment, sleep/travel, NPCs/security, police/alarms,
  missions/progression, animation/audio, world interactions and saving/loading.
- Vehicle storage access must follow verified native seated/on-foot, distance,
  position and movement conditions. Rear-center passenger seating is a separate
  multiplayer presentation adaptation and grants no storage privileges. Keep the
  native front passenger lootbox intact.
- Preserve native storage capacity, accepted items, UI, interaction locations,
  ownership, persistence and clearing. Use the existing authoritative inventory
  transactions; never invent a permanent shared vehicle inventory.
- Document necessary exceptions, their evidence, scope, activation and restoration.
  Keep verification focused; prefer source inspection over new test frameworks or
  lengthy runtime testing. Preserve normal offline behavior.

## Vanilla Gameplay Preservation — Non-Negotiable

This permanent project rule supersedes conflicting earlier project instructions.
The mod adds cooperative multiplayer; it does not redesign Thief Simulator 2.

- Never alter vanilla gameplay unless strictly required for functional multiplayer.
- Optimize our multiplayer code only, not unrelated vanilla systems or their physics.
- Preserve the original local first-person stack: PlayerScript, PlayerMotorBehavior,
  PlayerCameraBehavior, CharacterController, camera, hands, tools, equipment,
  inventory, animations, movement, input and native interactions.
- Preserve original vehicle physics, handling, steering, acceleration, braking,
  collisions, driver camera and native entry/exit. The confirmed driver must run
  original EVP locally on their peer; suppress competing replica physics. Keep the
  front passenger lootbox functional.
- Preserve vanilla economy, missions, NPCs, police, UI, settings, loading and saving,
  except for explicitly required cooperative extensions.
- Required extensions include Host authority, remote avatars, shared world states,
  rear passenger seating, shared wallet/progression, personal inventories,
  Host-directed travel and coordinated sleep/time. Preserve compatible native rules.
- Keep hooks minimal, session scoped and reversible with correct teardown. Temporary
  passenger state must restore the original actor, controller and camera on exit.
- Solo play with the mod installed must retain normal original behavior.
- Audit every future native hook and runtime mutation against these requirements.
  Classify interventions as A (essential), B (essential but too invasive), C
  (unnecessary), or D (uncertain). Investigate D before changing it; minimize B and
  restore C without breaking required multiplayer functionality.
- Document each exception's affected vanilla behavior, multiplayer justification,
  minimal scope, activation conditions and restoration/preservation behavior.
- Restore unnecessary historical modifications through our mod. Never overwrite
  original assemblies with decompiled or reconstructed versions.
- Unchanged original binary hashes do not prove unchanged runtime gameplay.

Keep verification focused. Do not add hundreds of checks or run unnecessary native
fixtures. Run focused regressions, the existing suite once near completion, build
and validate. Do not claim full vanilla equivalence from limited evidence.
Never overwrite in-use installation files or terminate unrelated game processes.
Preserve saves, PlayerPrefs, unrelated configurations and original game binaries.
Do not start a new feature milestone during the gameplay-preservation audit.
