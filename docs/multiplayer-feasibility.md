# Multiplayer feasibility — updated 6 October 2026

Historical Phase 1 feasibility. Later runtime work verified F8, current adapter
acquisition/recreation, a complete player hierarchy and a clean civilian full-body
offline visual. See [current evidence](full-runtime-recon.md) and
[avatar selection](avatar-candidates.md). Two-game production visibility and world
sync feasibility gates remain open; the earlier stopped-control notice below is
historical and does not describe the authorized nine-session follow-up.

**Phase 1 start gates passed; implementation is built and automated transport
verified. Final gameplay acceptance is pending.** Static analysis from f1a2bc5
was reused. New inspection was limited to newly observed hierarchy scripts and
transport/runtime verification. No world synchronization has been implemented.

| Question | VERIFIED | LIKELY / implemented strategy | UNKNOWN / acceptance still required |
|---|---|---|---|
| 1. Can a visible remote player exist? | Plugin runs in Unity; original mesh parts exist; renderer-only construction compiles | One mod-owned proxy; baked body with generated capsule fallback | Final in-world visibility, dimensions/materials/layer and both game peers |
| 2. Is there a reusable body/model? | Runtime LEGS/Main Character Legs has Body/Tops/Gloves/eyes/hats hidden, Bottoms/Shoes active; vehicle TPP bodies also exist | Bake local body geometry into new meshes; no hierarchy clone | Runtime bake quality/pose and final visibility |
| 3. Can camera/input be disabled? | Motor/camera are separate; root/HeadTilt/LegsController read local input; cursor/global output side effects identified | Exclude every controller/camera/audio/collider/script from remote instead of copying them | Entire active player cloning remains unsafe and unused |
| 4. Can a player-like visual proxy be used? | Transform/MeshFilter/MeshRenderer-only code with component allowlist; generated capsule requires no game asset | Exactly one visual per accepted remote, Untagged, Default rendering layer | Actual visual spawn not yet exercised in final game plugin |
| 5. How to identify players? | Automated UDP acceptance assigns host=1/client=2, shared session token; capacity/reconnect checked | Host-authoritative membership separate from game/Steam identity | Real game pair acceptance |
| 6. Can interactions be intercepted? | Empty LootBox.Start Prefix and Postfix each executed once in runtime, then unpatched | Finite action adapters remain future work; Phase 1 uses no game patches | Action-specific safe authority boundaries and alternate/coroutine paths |
| 7. Can world objects be deterministically identified? | Catalog IDs are definitions, drops reuse names, Door_ID is empty | Future authored scene registry + host spawn IDs | Cross-process uniqueness, save-dependent population and streamed content |
| 8. Can AI be host-authoritative? | Single-player citizen/navigation/perception and police ownership code identified | Future dedicated host simulation and two-target adapters | Complete isolation and perception design; no Phase 1 AI sync |
| 9. Can scenes synchronize? | MainScene Single + IntroHouse Additive and reload observed; both PlayerScript and GameManager replaced | Phase 1 disconnects on scene events; readiness hides absent/mismatched players | Synchronized travel is not implemented; only one area observed |
| 10. Hardest multiplayer system? | Loot reaches inventory/XP/save/mission/destruction; AI caches one player | Shared world authority, transactional loot/persistence and two-target AI remain hardest | Cost/edge cases until future scoped investigation |

## Phase 1 gates

1. Genuine local player: VERIFIED in eight gameplay snapshots. PlayerScript,
   motor, camera behaviour and CharacterController share the same owning root.
2. Position/yaw source: root movement observed; runtime references plus inspected
   Update movement/look identify world root position/Euler Y. Own late sampling
   compiles; final plugin sampling execution is still unverified.
3. Safe representation possible: only fresh visual components are created;
   hidden body geometry is optional, original capsule is asset-independent.
4. BepInEx stability: diagnostics loaded and completed captures in the game.
   Final multiplayer plugin startup still needs its separate smoke test.
5. Transport compatibility: LiteNetLib 1.3.5 source with minimal documented net46
   adaptations exchanged a reliable loopback packet in Unity/BepInEx/Mono.

No fundamental implementation blocker was found. Do not declare the user-facing
“two players see each other move” acceptance complete until two legitimate game
installations verify it. Computer Use was stopped with physical Escape during
follow-up capture, so further in-game control was stopped.

## Remaining risks and limits

World IDs, single-player references, asynchronous state replay, AI/perception,
save/scene differences and official Steam-build differences remain future-phase
risks. The original capture's 160-node limit leaves deep hand bones incomplete;
the upgraded diagnostics has a 4,096-node limit and explicit truncation status.
The allowlist excludes all unaudited child scripts from remote visuals.

Transport compatibility uses local empirical evidence; generic unmodified
net46 support for upstream 1.3.5 is not claimed. MIT attribution and source
adaptations are documented in third_party. No Steam authentication, DRM,
single-instance behavior or original game files were modified.

Next work is Phase 1 single/two-instance gameplay acceptance, including abrupt
host timeout, UI focus, proxy rendering and scene-reload cleanup. Follow the
[implementation test procedure](phase-1-implementation.md) before any world or
interaction synchronization.
