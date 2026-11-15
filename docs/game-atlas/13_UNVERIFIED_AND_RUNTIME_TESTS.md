# 13 — Unverified facts and targeted runtime tests

Everything below is **not established** by static evidence. Each item names the evidence needed and a minimal procedure. No item requires code changes unless noted; the mod logs and the user's two PCs suffice.

## 1. Open facts
| # | Question | Current status | Evidence needed | Procedure |
|---|---|---|---|---|
| U1 | Which window is the user's "sideways window near the containers at 108"; why it does not sync | candidates + hypotheses H1–H3 ([05](05_WORLD_OBJECTS.md) §6) | plugin logs of both PCs at the moment of opening | 05 §6 diagnostic (open locked vs already-unlocked window; grep `Window_double_vinyl_openable`, `localOnlyDoors`, `no unambiguous census id`, `SkipBusy`, `WOBJ_STATS`) |
| U2 | Stage 2 (`story3_escape`) completes via the intro travel-menu `story++` | INFERRED | log of `Story` before/after the intro gate | single player intro run; note the HUD objective when the travel menu opens at the back gate |
| U3 | Sensor → AI wiring (`Player_Is_Seen`, `Player_Lost`, `Player_In_Hear_Range`) | INFERRED (UnityEvents in SensorToolkit not decoded) | SensorToolkit UnityEvent decode (`SensorToolkit.*` layouts) or runtime log | extend `layout.py` to SensorToolkit (static) — no runtime needed |
| U4 | `GuardAI`, `DogAI`, `Alarm` have 0 scene instances | UNKNOWN (prefab-only, spawned, or unused) | decode of `sharedassets*.assets` prefabs or runtime `FindObjectsOfType` count | static prefab scan, or a read-only debug log line in a dev build |
| U5 | Item effects of `is_special` (`specialAdd`) and `isMissionItem` | UNKNOWN | targeted IL read of `<PickingItem>` specialAdd branch + `Inventory` users | static (show.py) |
| U6 | `HiddenItem` persistence key; `StashTrigger`, `MailBox` behaviour | UNKNOWN | IL read | static |
| U7 | Meaning of police `stars` > 2 from callers (`LockedWindow.starsAmount`) | UNKNOWN beyond spawn count | IL of `SpawnPoliceSimple` | static |
| U8 | Whether NPCs notice missing items or open doors | UNKNOWN (no logic found in `AI` names) | IL search of `AI` for `Pickupable`/loot reads | static |
| U9 | `SimpleOpenClose` is interactive | UNKNOWN (no caller of `ObjectClicked`) | `SendMessage("ObjectClicked")` search in IL / runtime test | static string search + in-game click on a 108 kitchen cabinet |
| U10 | Gas cloud lifetime / cleanup component | INFERRED (`TimedDestroy`) | prefab decode of `gas_fog_prefab` | static prefab scan |
| U11 | Hiding-spot occupancy with two players | UNKNOWN | runtime | both players try the same wardrobe |
| U12 | Steam Remote Play drone (`steamRemotePlay`, `PlayerRemoteDrone`) usable in this repack | UNKNOWN | runtime | not needed for the mod; note only |
| U13 | Associate ownership in co-op | design decision | architect | – |
| U14 | Counters for story polls (missions, contracts, money made, heavy items, level, skill, location) — host only or shared | design decision | architect | – |
| U15 | `GameManager.SaveAll` caller (hideout summary) | INFERRED UI event | uGUI event decode | static |
| U16 | `CarController` serialized layout (10 instances fail decode) incl. `advanceStory_on_engine_start` per car | decode error | fix layout for CarController (EVP types) | static |

## 2. Coverage limits of this atlas
- Decoded: all 17 scenes, game-namespace MonoBehaviours (≈99 % byte-exact). Not decoded: namespaced components (SensorToolkit, uGUI, Enviro, EVP), prefabs in `sharedassets*`, audio/particle assets, `Assembly-CSharp-firstpass`.
- The call index is a lower bound (no delegates/SendMessage/UnityEvents).
- No runtime observation was made in this milestone; every mechanic is VERIFIED_IL/VERIFIED_ASSET or INFERRED, never VERIFIED_RUNTIME unless quoted from PROGRESS.
- Inactive-in-asset objects (interiors, story objects) may become active in some campaign states; activity per campaign state is not modelled.

## 3. Regression tests the next implementer should keep green
- `tools/research/*.py` re-run reproduces the catalogs (deterministic given the same game build).
- Existing suites (`tools\build.ps1 -Test`, 30 suites at 0.17.0) — not run by this milestone (docs/research only).
