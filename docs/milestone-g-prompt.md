# Milestone G — NPCs, security cameras and permanent teammate tracking

This implementation prompt extends the existing
[Milestone G roadmap scope](full-sync-roadmap.md). Permanent teammate tracking must
be implemented and accepted within the same NPC and security-camera milestone;
it is not an optional polish item or an unrelated future feature.

Status: requirements only. Milestone G remains unstarted. This document does not
claim implemented tracking, verified native map support or completed acceptance.
Updating these requirements does not start a feature milestone during the
gameplay-preservation audit.

## Vanilla-first implementation gate

Read and obey [AGENTS.md](../AGENTS.md) and the
[permanent cooperative gameplay rules](coop-gameplay-rules.md).
Understand vanilla, preserve vanilla, enable Player 2, then synchronize both players.
Before modifying any native mechanic, inspect its original implementation,
interaction conditions, dependencies, state transitions, animation and persistence.
Use a short controlled vanilla observation only when source inspection cannot
resolve a specific question.

First inspect the original minimap and full-map behavior. Identify the actual UI
and rendering components, world-to-map conversion, original player icon placement,
map rotation, scene-specific bounds, scaling and icon lifetime. Check whether the
full map supports dynamic position markers and how it selects the displayed map.
Record the inspected native methods and evidence before selecting an integration.
Reuse native conversion and icon infrastructure where technically possible; do
not assume that minimap and full-map conversion are interchangeable.

Extend only what is necessary for cooperative teammate tracking. Preserve the
native map presentation and local-player marker. Do not replace or redesign the
minimap, invent a full-map interface or add unrelated map functionality. Preserve
original gameplay and the local first-person player stack, including movement,
input, camera, hands, tools and interactions. Preserve native vehicle handling,
driver camera, entry/exit and the front passenger lootbox.

Classify native hooks and runtime mutations as A/B/C/D under AGENTS.md; investigate
D before changing it, minimize B and remove C. Document each necessary exception's
native behavior, evidence, multiplayer justification, scope, activation and
restoration. Hooks must be session scoped and reversible, with normal solo/offline
behavior preserved. Never overwrite original game assemblies.

## Primary NPC and security-camera scope

The original Milestone G requirements remain primary:

- Host-authoritative native NPC simulation, including original schedules, spawns,
  navigation and door interactions, with clean Client presentation.
- NPCs correctly perceive both players independently through the native sight,
  hearing and action conditions. Adapt the original Player 1 dependency minimally;
  do not change native detection thresholds, occlusion, stealth or restrictions.
- Shared NPC movement and animations, suspicion and alert states, with legitimate
  outcomes replicated once and no competing Client simulation, authority callbacks,
  coroutines or sensors for these synchronized actors.
- Security cameras detect both players under their verified original conditions.
- Native alarm activation and synchronization, preserving original NPC and security
  camera gameplay and avoiding duplicate alarm activation from presentation replay.

Retain the existing C/D/B dependencies, relevant ordinary/female/special/guard
coverage, schedule rollover and outcome-replication gates. Use the
[NPC analysis](npc-multiplayer-analysis.md) as prior evidence, then inspect the
actual native paths relevant to this implementation. Permanent teammate markers
are an additional cooperative UI feature within G. Do not expand G into full police
synchronization; that remains Milestone H.

## Permanent world-space teammate indicator

Whenever both players are in the same ready gameplay scene, Host must always be
able to locate Client and Client must always be able to locate Host.

- Use a lightweight teammate icon, waypoint or name marker tied to the actual
  authoritative/networked teammate position.
- Keep only this indicator visible through walls, buildings and terrain. Do not
  render the entire remote character through obstacles or globally disable native
  occlusion, depth testing or rendering systems.
- Do not hide the marker because the teammate is far away, the avatar is culled,
  or the remote character is not directly visible. Its lifetime and position must
  not depend on remote-avatar renderer visibility.
- Keep the marker readable without obstructing the view. Use sensible screen-edge
  indicators when the teammate is outside the camera view, including behind it.
- Follow walking, crouching, leaning, driving and passenger travel correctly on
  both peers, including when Host is passenger and Client is driver.

## Native minimap teammate markers

Extend the existing minimap where technically possible using the inspected native
conversion, rotation, bounds, scale and icon lifetime. A teammate marker on the
original minimap is required; a world indicator alone does not satisfy G.

- Host sees Client's continuously updated position; Client sees Host's.
- Use a clearly distinguishable teammate marker and leave the original local-player
  marker unchanged.
- Track the correct player while driving or riding as passenger. Never leave a
  marker stuck at an old walking, seat or vehicle position.
- Preserve native map orientation and scaling for the current scene/map.
- Maintain one teammate marker per peer, with no duplicated or ghost icons.
- Clean up marker registrations, UI objects and bindings on disconnect, scene
  transition or session teardown, using the native lifetime rules where applicable.

## Original full world map

Inspect the original full-map interface. If it supports dynamic position markers,
show the teammate there using the original world coordinates and that map's native
conversion. Preserve the native interface and presentation.

Never place a teammate at a misleading position on another scene or displayed map.
If separate-scene presence needs representation, use a minimal accurate
scene/location indicator rather than a fake world position. If native dynamic
markers are unsupported, document the inspected limitation; do not invent a new
map interface. This conditional full-map support does not relax the world-indicator
or minimap requirements.

## Synchronization and performance

Reuse existing player position replication, synchronized transforms and scene
identity. Bind to the accepted pose of the correct network player in the current
session and SceneEpoch, including existing driver/passenger position handling.
Do not introduce another high-frequency location stream, duplicate player-position
synchronization or unnecessary network traffic.

- Reuse existing player replication and UI infrastructure with cached bindings;
  no full-world searches per frame.
- Avoid expensive UI allocations per frame, excessive Unity Canvas rebuilding
  and frame-time spikes when markers appear.
- Update UI only when needed. Position, camera/view projection, map rotation,
  scale, bounds and visibility changes may require updates even if label content
  is unchanged; unchanged UI content must not be rebuilt unnecessarily.
- Use lightweight interpolation where appropriate while preserving the correct
  player, scene and authoritative/networked position.

Markers are purely cooperative UI. They must not affect NPC, police or security
camera detection, AI visibility, stealth rules, player collision or vanilla gameplay
balance. The explicit through-obstacle exception applies only to teammate indicators.

## Late join, scene travel and teardown

Tracking must recover automatically during normal gameplay, late join, automatic
scene following, mission transitions, hideout transitions, reconnect and vehicle
travel. Use existing scene/readiness and player lifecycle infrastructure.

- Bind markers to the correct player identity, current connection/session and
  SceneEpoch. Reject obsolete scene/connection data and remove old-scene markers
  cleanly before rebinding.
- Do not show an incorrect or stale world/map position while players are in different
  scenes/maps or temporarily in different loading states. An accurate minimal
  scene/location/loading indicator may represent that temporary absence.
- Restore position markers automatically when both players reach the appropriate
  ready scene and valid current player positions are available.
- Handle disconnect and reconnect without retaining ghost icons, subscriptions,
  native UI registrations or obsolete player/vehicle references.

## Focused verification and acceptance

Do not create a large new testing system or spend hours in native game launches.
Reuse the existing suite, player replication and UI infrastructure. Add only a few
focused checks, exercising both Host and Client views:

1. Correct teammate identity and current position on both peers during walking,
   crouching and leaning; correct automatic binding on late join/reconnect.
2. Readable world indicator through walls, buildings and terrain, at long distance,
   and as a screen-edge indicator outside the camera view, independent of avatar
   visibility and without rendering the character through obstacles.
3. Correct native minimap placement, rotation, scene bounds and scale with one
   distinct teammate icon and the unchanged local-player icon. Check full-map
   positioning where supported; otherwise record the native limitation.
4. Correct positions while driving and riding, with each peer taking each role;
   no frozen walking/seat position or duplicate location stream.
5. SceneEpoch invalidation, automatic scene following, mission/hideout/vehicle
   travel, loading-state suppression, disconnect cleanup and automatic restoration;
   no stale or ghost markers and no fake cross-map positions.

Prefer source inspection for native questions and keep required runtime observations
short. During implementation, run focused regressions, the existing suite once
near completion, then build and validate. Preserve saves, PlayerPrefs, configurations
and original binaries; do not overwrite in-use installation files or terminate
unrelated game processes. Report measured evidence and remaining limitations without
claiming full vanilla equivalence from limited checks.

Milestone G acceptance requires the primary NPC/security-camera behavior and
symmetric permanent teammate tracking together: both players can always locate
each other in the same ready gameplay scene; the indicator stays visible through
walls and buildings; teammate positions appear on the native minimap; the original
full map is supported where possible; existing position synchronization is reused;
vanilla gameplay and performance are preserved.
