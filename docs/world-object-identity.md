# World-object identity and replication keys

Milestone C implementation is in [network-entity-registry.md](network-entity-registry.md).
It implements the canonical descriptor fallback and fail-closed collision policy
below, without registering real doors/loot. Entity ID scope/value is separate from
EntityRef generation and the message session/epoch envelope; earlier combined-ID
wording is conceptual only. Cross-peer whole-world stability remains unverified.

VERIFIED_RUNTIME: duplicate names and roots are normal. R8 `DOMY[0]` scope matched
roots in both MainScene and MadisonSt. ItemID definitions 10 and 19 share
`automatic_lockpick`; dropped objects are named `dropped`. Unity instance IDs
identify live engine objects only. The same initial allocation sequence repeated
some IDs across process starts; this does not make them persistent.

| Candidate key | Evidence | Scope / risk |
| --- | --- | --- |
| Unity GetInstanceID | VERIFIED_RUNTIME on all captured components | Diagnostic local lookup only; recreation invalidates references |
| Object name | VERIFIED_RUNTIME duplicates | Insufficient |
| Scene + sibling-index hierarchy | VERIFIED_RUNTIME exact captured paths | Useful authored fallback; runtime insertions/order and build changes require validation |
| ItemID.ID / PlayerScript.item_IDs index | VERIFIED_RUNTIME 452 definitions | Definition identity, not physical instance identity |
| Pickup name / item-transform name | VERIFIED_STATIC used by Item_Picked/save logic | Repeated drops and renamed/cloned objects can collide |
| Door_ID | VERIFIED_STATIC empty component in this build | Provides no persistent identifier |
| Pickupable.prefName / parcel_pref | VERIFIED_STATIC specialized preference keys | Optional, system-specific; uniqueness must be tested per map/type |
| Lockpick/LockedWindow | VERIFIED_STATIC state/tag/component mutation | No universal persistent key established on these components |
| parcel_pref / mission prefs / residence ID | VERIFIED_STATIC; live SelectResidence/Mission_Item fields | Semantic keys for that system only; not universal GUIDs |
| Serialized list index | VERIFIED_STATIC vehicles/storage/habits | Can be stable within an authored list; runtime append ordering differs |
| Explicit universal GUID | UNKNOWN | None established; absence from bounded capture is not proof of absence |

Proposed canonical authored key: build fingerprint + exact scene asset identifier +
escaped indexed hierarchy + concrete adapter type + same-type component ordinal.
Prefer an explicit validated save/semantic key when present, retaining the hierarchy
as diagnostic evidence. Do not include transform position in identity: doors move,
loot drops and vehicles drive. Position/bounds are sanity checks only.

Register authored objects after the complete scene set is loaded. Canonical keys
are sorted and hashed for compatibility; detect duplicate keys and reject ambiguous
bindings. Exchange the canonical string or a collision-checked compact registry index;
never silently resolve hash collisions. Compare both peers' relevant registry manifests
before gameplay replication. Keys are versioned per game build and adapter schema.

Dynamic entities need host-assigned IDs, spawn type/source descriptor and generation.
Logical removal uses a tombstone, retained for initial snapshots/reconnects. Destroyed
Unity IDs must never alias new logical entities. Separate persistent coop save identity
from session identity; translate explicitly on resume.

Proposed NetworkEntityId contains scene epoch, compact registry/dynamic entity ID and
generation. NetworkPlayerId and transaction IDs are separate domains. Reliable action
requests contain expected object revision and actor ID; host validates authority,
distance, state and ownership, then increments revision once. Duplicate requests reuse
the cached result, not the gameplay method.

## Stability tests and limits

VERIFIED_RUNTIME: repeated process loads recreate MainScene + additive MadisonSt,
with the same representative actor hierarchy and selected NPC source path. R9 returned
to MainMenuNew and continued again to observe a genuine unload/reload, rather than
the artificial adapter blackout. See [scene-sync-analysis.md](scene-sync-analysis.md)
for actual before/after IDs and event order.

UNKNOWN: deterministic mapping of every authored object on two legitimate peers,
all additive maps, user save reloads, differing progress and random spawns. Phase 1
two-game acceptance is blocked by the unavailable second legitimate installation.
Next registry fixture compares only relevant object types, records collision counts,
checks source keys across same-process reload and restart, then compares two peers.
Name-only or globally concatenated path-only identity is not acceptable.

Capture caveat: R1–R9 `state_field` records carry path/type but not scene or
component ID. Associate those with their preceding component record and snapshot,
not a global path dictionary across scenes. Scoped captures can match identical
root paths in multiple scenes. Bone/renderer metadata inherits its owning candidate's
scene. This caveat matters for offline correlation as well as future registry code.
The final R10 schema adds scene/component ID to each field record; its menu-only
fixture verified the new identifiers.
