# Game Atlas — Thief Simulator 2 reverse-engineering package (StolenPlayer)

**Status:** research package complete as a static audit (2026-11-15). Every document and catalog listed below exists, and `tools/research/validate_atlas.py` reports 0 errors.

**Not verified at runtime.** Every mechanic is VERIFIED_IL, VERIFIED_ASSET or INFERRED. VERIFIED_RUNTIME appears only where it is quoted from `docs/PROGRESS.md`. Open questions are in [13](13_UNVERIFIED_AND_RUNTIME_TESTS.md).

No gameplay code, game file or save file was changed. The game was not launched. Nothing was committed.

## Game and tools
- **Game:** `D:\Thief-Simulator-2-AnkerGames\Thief Simulator 2`, version 1.27c, Unity 2020.3.33f1, Mono.
- **Assembly:** `D:\Thief-Simulator-2-AnkerGames\Thief Simulator 2\Thief Simulator 2_Data\Managed\Assembly-CSharp.dll` (5,792,768 B).
- **ILSpy CLI** 11.2.0.10050 at `C:\Users\wake\.dotnet\tools\ilspycmd.cmd`. It is not on the PATH of sessions started before its install. Commands that succeeded:
  - `ilspycmd --version` → `ilspycmd: 11.2.0.10050`.
  - `ilspycmd -l c "<assembly>"` → 3,336 classes, including `StoryManager`, `Pickupable`, `Door`, `GameManager`, `PlayerScript`, `AI`, `PolicemanAI`, `Police_Manager`, `SidewalkAI`, `CodenameAI`.
  - `ilspycmd -p -o <scratch>\decomp "<assembly>"`: a one-time project decompile into the session scratchpad. It is **never** in the repo (proprietary code). Targeted reads go through `tools/research/show.py`.
- **Scene / prefab data:** `tools/research/unity_reader.py` + `layout.py`, dependency-free readers for SerializedFile v22.
  - Each MonoBehaviour must consume exactly its stored byte size, so a successful decode counts as VERIFIED_ASSET.
  - Coverage: 20,758 game-script instances in 17 scenes, about 99% byte-exact.
  - Failures: camera/sky/third-party scripts, `CarController` ×10, `Inventory`, `PlayerScript`, `Tutorial`.
  - Namespaced components (SensorToolkit, uGUI) and `sharedassets` prefabs are not decoded.

## Reproduce (read-only on the game)
```
python -I tools/research/atlas_index.py <scratch>/decomp docs/game-atlas/data
python -I tools/research/scan_scenes.py "<game>/Thief Simulator 2_Data" <scratch>/decomp <scratch>/scenes
python -I tools/research/dump_story.py "<game>/Thief Simulator 2_Data" <scratch>/decomp <scratch>/loc_en.json <scratch>/story_raw.json
python -I tools/research/story_bindings.py <scratch>/scenes <scratch>/story_raw.json <scratch>/story_bind.json
python -I tools/research/build_atlas_data.py <scratch>
python -I tools/research/build_sync_data.py
python -I tools/research/validate_atlas.py
python -I tools/research/render_tables.py story|residence_missions|side|items_summary|wotypes|npcs
python -I tools/research/show.py <scratch>/decomp Type.Method
```
`loc_en.json`: English localization (language 10), extracted from `resources.assets` (JSON entries `{"ID","enumID","translations"}`).

## Evidence legend
| Level | Meaning |
|---|---|
| VERIFIED_IL | decompiled original method |
| VERIFIED_ASSET | byte-exact scene decode |
| VERIFIED_RUNTIME | user QA / logs recorded in PROGRESS |
| INFERRED | supported by indirect evidence only |
| UNKNOWN | not established |

## Coverage summary
| Area | Coverage |
|---|---|
| Story | **81 stages** (79 playable + final + free mode). Every playable stage has a mapped completion path; stage 2 is INFERRED. 0 unresolved in the catalog |
| Side systems | 33 residences, 51 residence missions, 25 contracts, 2 payphone chains (Madison/Hillville), 7 heists, 4 job offers, 64 BlackBay objects, 85 pickpocket targets, 6 ATMs |
| Items | **452 item types** (ids 0–451, unique) with world placement counts per scene |
| World objects | 56 types, **8,665 instances** across 13 scenes |
| NPCs | 15 classes; police state machine; gas response |
| Abilities | 45 skills (incl. 3 timed abilities); 38 tool items; 3 drones |
| Multiplayer | 48 action families × 15 questions: 3 complete, 10 implemented / needs QA, 7 partial, 19 missing, 2 blocked, 6 unsafe, 1 unknown |

## Documents
| File | Content |
|---|---|
| [01_SOURCE_INDEX](01_SOURCE_INDEX.md) | sources, scene table, lookup recipes, choke points, class map, identity vocabulary |
| [02_STORY_CAMPAIGN](02_STORY_CAMPAIGN.md) | story architecture, all 81 stages, dependency graph, checkpoints, multiplayer rules |
| [03_SIDE_MISSIONS](03_SIDE_MISSIONS.md) | residences/missions, contracts, jobs, payphones, heists, BlackBay, pickpocket, ATM, tips, parcels, events |
| [04_ITEMS_AND_LOOT](04_ITEMS_AND_LOOT.md) | item definitions, identity separation, loot sources, exactly-once requirements |
| [05_WORLD_OBJECTS](05_WORLD_OBJECTS.md) | interaction pipeline, look-alike scripts, type catalog, persistence, **sliding-window case** |
| [06_NPC_AND_POLICE](06_NPC_AND_POLICE.md) | NPC lifecycle, police state machine, alarm scenario, AI authority constraints |
| [07_ABILITIES_AND_EFFECTS](07_ABILITIES_AND_EFFECTS.md) | skills, tools, **gas**, **drones**, associate, player states |
| [08_VEHICLES_AND_PHYSICS](08_VEHICLES_AND_PHYSICS.md) | vehicles, travel, **heavy-item lifecycle** |
| [09_SAVE_AND_CHECKPOINTS](09_SAVE_AND_CHECKPOINTS.md) | save mechanisms, key ownership table, live vs checkpoint, document contradictions |
| [10_MULTIPLAYER_SYNC_MATRIX](10_MULTIPLAYER_SYNC_MATRIX.md) | 48-row coverage matrix |
| [11_ARCHITECTURE_BLUEPRINT](11_ARCHITECTURE_BLUEPRINT.md) | authority rules, patterns A–G, message sketch |
| [12_IMPLEMENTATION_ROADMAP](12_IMPLEMENTATION_ROADMAP.md) | R0–R14 milestones with acceptance tests |
| [13_UNVERIFIED_AND_RUNTIME_TESTS](13_UNVERIFIED_AND_RUNTIME_TESTS.md) | open facts and procedures |

## Data files
| File | Content |
|---|---|
| `data/source_index.json` | 1,258 game types, 5,527 methods; fields, resolved call edges, save keys per method (names only) |
| `data/save_keys.json` | key expression → readers/writers |
| `data/call_index.json` | callers of story/money/XP/save choke points |
| `data/mission_catalog.json` | story stages, residences, residence missions, side systems |
| `data/item_catalog.json` | item types + placements + identity rules |
| `data/world_object_catalog.json` + `data/instances/<scene>.json` | types / per-scene instances |
| `data/npc_catalog.json` | NPC classes, police facts |
| `data/interaction_catalog.json` | 48 action families with original entry points |
| `data/sync_coverage.json` | per action: 15 multiplayer questions + status |

All catalogs use the `game-atlas/1` schema: a `meta` block plus rows with stable `id`s and evidence. Unknown values are `null` or marked UNKNOWN.

## Headline findings
1. **Campaign = 81 stages** in `StoryManager.story_scripts` (key `Story`), strictly linear, with no mission checkpoints. Completion comes from polled flags, 29 `AdvanceStory` call sites keyed by serialized stage keys, and direct `story++` writers (heist start, intro travel menu, credits).
2. **Side systems are independent** (own keys). Only 5 residence missions, 7 BlackBay objects, 3 heists and counters (missions, contracts, heavy items, money) feed the story.
3. **The house 103 drone** is the stage-15 story pickup with `isDrone`. StolenPlayer refuses both classes for Player 2 by design, so it needs story transactions plus drone ownership (R3 + R6).
4. **Heavy items** are carry-only (no key). Player 2 is refused by design and needs a lease family (R7).
5. **Sliding window at 108:** `Door` (`moveOpen`, `isWindow`) + `LockedWindow` on one GameObject. Math and identity are ruled out; the lead hypothesis is the `Door` enable ordering. A log diagnostic is defined (05 §6).
6. **Gas:** the held tank's particle stream + `RangeSensor`, plus timed fog clouds, set `AI.is_inhaling_gas`. NPC effects must run only on the region NPC owner.
7. **Police:** the chase flag is per player and stars cap at 2. One alarm must make only the offender wanted.
8. **Architecture:** the existing design extends to objects, loot, physical entities, campaign transactions and persistence without a rewrite. **NPC/police authority is the genuine new architecture** and the highest risk.
