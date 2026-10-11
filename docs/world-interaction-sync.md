# Authoritative world interactions (Milestone D)

Mod 0.5.0 / protocol 4. **ACCEPTED — TWO-GAME VALIDATION DEFERRED**; see
[milestone-d-acceptance.md](milestone-d-acceptance.md). Two-game acceptance remains
DEFERRED. No Milestone E or later mechanic is implemented.

## Evidence and scope

Installed-build decompilation was repeated for Door, PlayerScript, WindowUse,
LockedWindow and Lockpick. Proprietary output stays in ignored local storage.
VERIFIED_STATIC describes inspected implementation; VERIFIED_RUNTIME requires a
live observation or executed action, not a field name or a successful synthetic test.

| Boundary | Evidence | Meaning |
| --- | --- | --- |
| Door.Use / SlowFailed / TimeAdd / UseFully | VERIFIED_STATIC | Use changes target immediately, starts original Update motion, plays authored audio and adds local PS noise. Slow input first writes load_slow, then target changes after 0.15s; release uses SlowFailed. |
| Door.Update / Resett | VERIFIED_STATIC | Authored rotateOpen, useVectorRotation or moveOpen targets; original interpolation and completion; Resett restores usability and window traversal helper availability. |
| Door.ResetLock / Unlock_Key / DelayKey / OpenDestroyed | VERIFIED_STATIC | Relock, key unlock with delayed tag restoration, and broken hinge. |
| Lockpick.StartLockpicking / DoorUnlock / RelockDoor / BreakPadlock | VERIFIED_STATIC | Separate minigame, arrays of Door leaves, tags/enabled flags/collider capability; story, statistics, alarms, dust and rigidbody effects. |
| LockedWindow.BeginCut / UnlockWindow / UnlockWindowNoAlarm / BrokeWindow | VERIFIED_STATIC | Separate local cutting game; pane/enterable/collider changes, alarms/missions and component destruction. Inside unlock restores visible pane after UnlockWindow. |
| WindowUse.UseWindow / GetIn | VERIFIED_RUNTIME (normal traversal); VERIFIED_STATIC (implementation) | Normal host E traversed the window and recovered original hands/camera/motor. Never executed as replicated world state. |
| Representative Door and Lockpick fields in prior R7/R8 captures | VERIFIED_RUNTIME (observation only) | Real components and authored movement configuration exist; prior captures did not exercise these actions. |
| D host/client Door.Use / UseFully / Update / Resett | VERIFIED_RUNTIME | Real exterior door open/close, original motion completion, real client E-key intent, and authored client audio observed; see acceptance evidence. |
| Door-backed sliding Window and host forced gate outcome | VERIFIED_RUNTIME | Real host/client sliding-window open/close and normal client E; original crowbar R changes locked gate to unlocked/broken hinge. Remote tool completion remains LIMITED. |
| Durable per-object save GUID | UNKNOWN | Empty Door_ID and inspected lock/window classes establish no universal durable identity. |

## Semantic states

Door schema 1 is three bytes: schema, five boolean flag bits, interaction-tag enum.
Flags are Open (target isOpen), Enabled, RequiresKey, RelockAvailable (was_locked)
and BrokenHinge (brokeHinge). Tags are Door, Lock1, ElectricDoor, Untagged and Window.
The populated-scene run verified Window as a live locked-window capability; it can
appear after initialization and must remain representable during observation.
RelockAvailable is an observed capability, **not proof that a lockpick was used**:
DoorUnlock also participates in forced entry. No generic Locked/Picked enum is invented.
Locked capability comes from the authored tag, enabled/key flags and original adapter
eligibility. Unknown tags are explicitly unresolved. Static key/tool configuration
remains authored; clients cannot send an unlock or tool-completion operation.

Window schema 1 is four bytes: schema, Open/Enabled/Locked/PaneVisible flags,
Intact/Cut/Broken outcome, and interaction-tag enum. LockedWindow and its pane/helper
references are retained by the adapter when first bound. A fixed postfix records
host cut/break outcome before the original component is destroyed. PaneVisible is
separate because inside unlock leaves the pane visible. Door-backed windows are
registered as Window, with their own codec and adapter. WindowUse is not an entity.
No-Door windows, prefab-instantiated broken-glass cosmetics and uncommon devices
remain LIMITED/unresolved. A late-created adapter cannot reconstruct every destroyed
LockedWindow reference; host bootstrap precedes hosted interactions where practical.
Reconstructing uncommon completed offline cut/break outcomes before hosting remains LIMITED.

Animation progress, speed, audio playback and transforms are presentation. They
are absent from canonical hashes; the semantic target and revision are hashed.

## Identity and lifecycle

Use the C registry's build fingerprint + authored scene path + escaped indexed
hierarchy + fixed door-v1/window-v1 contract + component ordinal. Doors include
drawers/container leaves using Door's verified movement contract. Car doors,
descendants of the fixed vehicle-root component contracts, the verified unused
MainScene vehicle template with no controller marker, and
objects without supported movement are excluded and counted. Component pointers
are local bindings only, never wire identity. Duplicate keys or hash collisions
fail closed with both descriptors. Unknown capability tags log an unresolved record.
Registration sorts descriptors for reproducible bootstrap ordering. Actual populated
manifest contains 950 doors and 184 windows, with 24 explicitly excluded/unresolved
objects and zero collisions. Two populated loads produced identical sets of all
1,134 entity IDs, types and authored paths.

All registry work, observation, original execution and application run on the Unity
main thread. Host observation samples semantic changes at 10 Hz, immediately before
validation and after a development fixture action. Changed state commits a C entity
revision and enters the existing ordered backlog. No independent door transport or
save database exists. A destroyed host binding becomes a retained C tombstone.
Clients correct semantic drift to the canonical cached state, request a resnapshot
on application failure, and stop visibly after C's bounded restart limits.

Disconnect retains host canonical state. Client cancellation releases bindings;
reconnect adopts the host epoch and rebuilds the authored manifest before applying
a fresh baseline. Scene callbacks retain the accepted disconnect policy, clear
bindings/revisions/tombstones, and advance SceneEpoch. Real connected menu unload
cleared the registry/proxy; same-process reload created epoch 9 with fresh bindings
and a matching current-state baseline, including the newly loaded intact locked gate.

## Request and authority flow

ReliableOrdered channel 0 carries a 44-byte request (all integers little endian):

| Field | Type |
| --- | --- |
| Message type = 26 | uint8 |
| SessionId | uint64 |
| SceneEpoch | uint32 |
| RequestId | uint64 |
| ActorId (authenticated NetworkPlayerId) | uint8 |
| Entity scope / ID / generation | uint8 / uint64 / uint32 |
| InteractionKind: Open=1, Close=2 | uint8 |
| ExpectedEntityRevision | uint64 |

There are no parameters, method names, reflection paths or arbitrary operations on
the wire. A 30-byte reliable result (type 27) contains SessionId, SceneEpoch,
RequestId, bounded result enum and current revision. Canonical payloads still travel
through C EntityChange (type 23), or a fresh baseline for stale/future requests.

The local player's original PlayerScript raycast and UI determine intent. Range
validation and development probes reuse B's MainScene PlayerLocator; a global
Player tag is ambiguous across the simultaneously loaded authored areas. One
transpiler replaces exactly the three verified load_slow field writes in
PlayerScript.Update with a guarded fixed setter; it does not rewrite Update. Host
and offline calls retain the original field write. Client hold/release keeps the
original UI/control path; release reaches the narrow SlowFailed prefix and sends
an explicit Open or Close based on the canonical record. Client opening mutation
is suppressed until host confirmation. Per-entity pending intent is bounded to two
seconds and cleared by a result. Original host interaction stays latency free.
Client quiet-opening presentation/input timing is LIMITED; no unsafe prediction.
Before WorldReady intent is ignored, without an unbounded gameplay queue.
The guarded setter arms one component per original hold gesture. SlowFailed consumes
that intent once, including rejected requests; later original cleanup writes cannot
emit a second toggle. A real E-key regression verified one accepted Open and no
subsequent cleanup Close.

Host checks envelope, membership, readiness, actor, full entity reference/type,
live binding, supported action, exact current revision, range/target and original
game capability. It captures unobserved host changes before comparing revision.
Expected revisions below or above canonical state reject; no stale toggle executes.
Request IDs increase per actor/session; 128 recent results are retained, with a
high-water mark keeping even evicted old IDs consumed. IDs burn before execution or
game validation, so duplicate/changed retries never repeat a partial action.
Validation is limited to 20 new requests per actor per second. No human approval.

## Range and execution trade-offs

VERIFIED_STATIC: ordinary PlayerScript Door detection uses a 2.25m camera ray and
the player's mask. Host uses that distance plus 0.5m latency tolerance, the latest
accepted actor root (at most one second old), authored standing/crouch capsule height,
and an eye-region offset of half-height minus 0.05m. Runtime capsule centre was zero;
this is an approximate remote eye, not frame-exact lean/headbob reproduction.
Bounds only select candidate rays. Collider.Raycast must hit actual target geometry
within 2.75m; a second ray using the original mask/global trigger semantics must hit
that same collider first. This supports non-convex mesh colliders without the
unsupported Collider.ClosestPoint call or a zero-distance acceptance shortcut.
The host never accepts a client-supplied position inside an interaction packet.
View-angle proof and full FPS hit validation are deliberately deferred.

Valid requests execute original Door.Use when a target change is needed. This
preserves original sound/noise/openedByPlayer and motion setup. Use also writes the
cached local player's mouse sensitivity and movement permission: those two values
are preserved/restored synchronously around remote execution. The cached host
PS noise accumulator remains original. Noise location is therefore host-player
based, not a new multi-actor AI stimulus model; AI integration is future work.
Original methods continue to own host save-backed behavior; cooperative persistence
and client-save isolation have no new implementation or acceptance in D.

Client adapters apply explicit capability flags/references, and use Door.UseFully
for a changed presentation target. Original Update performs motion. An equal target
with a lock-only update preserves animation already in flight. Client application
does not invoke Door.Use, Lockpick rewards/story/alarm callbacks, window alarms,
WindowUse or tool transactions. NetworkApplyScope suppresses request recursion.
Baseline scopes suppress audio; changed deltas use the original authored clips and
AudioSource once. No raw audio packets. Real-client closing audio was observed while
original motion was active, followed by completion at the authored closed rotation.

## Limited actions and testing

Remote lockpick start/completion, key unlock, relock, forced entry and cutting/breaking
are blocked at fixed evidence-backed method boundaries. Host outcomes are observed
where the real adapter can represent them. Padlock physics, tutorial windows,
mission-specific window callbacks, uncommon tags/devices and full remote minigames
remain LIMITED. No fabricated minigame success or inventory/tool authority.

TransportHarness interactions mode builds synthetic stand-ins from a captured C
static manifest. Its client follows the genuine observed host pose and requests a
previously probed target, preserving production range checks. Its host can replay
that manifest and commit open/close deltas to test real-client presentation; this is
explicitly synthetic-host validation, with no claim of host Unity gameplay or AI.
Manifests and target files remain ignored local evidence.

Protected workflow: enable both development control flags, launch with save backups,
host, move naturally near a usable door/window, run milestone-b-fixture -Action
world-probe, then start-interaction-harness -Role client. world-open/world-close
provide bounded original-method host fixtures; world-audit logs canonical hashes.
Capture animation completion, original controls and audio before/after; repeat for
interior/exterior/other-area/window and lock state; test reconnect and menu/reload.
Restore saves and configuration afterward. Development actions are fixtures, never
human-driven gameplay evidence. F8 exposes counts, readiness, requests/rejections,
duplicates/stale revisions, delta/application counters, hashes and resync.
