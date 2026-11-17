# STORY_SYSTEM — story campaign, objectives, triggers, rewards (W6A mission-system map)

Date: 2026-11-17. Game build 1.27c. Method: read-only Mono.Cecil IL dump of `Assembly-CSharp.dll` (StoryManager, ReadNote,
StoryObject, callers of `AdvanceStory` / `InitiateStory` / `BeginStory`) + the existing byte-exact stage decode
(`docs/game-atlas/02_STORY_CAMPAIGN.md`, `docs/game-atlas/data/mission_catalog.json`). Nothing here was observed at runtime.
Legend: **[V]** verified IL / asset, **[M]** mod source, **[A]** assumption, **[Q]** needs an in-game test.

## 1. Core [V]
| Piece | Behaviour |
|---|---|
| `StoryManager` (MainScene `MANAGERS/STORY MANAGER`, `GameManager.story_manager`) | `int story` indexes `List<StoryScript> story_scripts` (81 stages, 0..80). `currentStoryScript`, `isFreeMode`, `calledInitiate`. One linear index; no branches. |
| `AdvanceStory()` IL_0000..IL_001d | `if (isFreeMode) return; story++; InitiateStory();` — the only shared choke point. 29 call sites (table §3). |
| `InitiateStory()` | `GM.CheckStory` (HUD animation); `currentStoryScript = story_scripts[story]` (free mode: 80); `advance_if_home` + `CurrentLocation == 0` / `advance_if_jail` + `== 93` → `story++` **in place** (IL_0066 / IL_00a6, not via AdvanceStory); `RefreshText`; jail / associate / Hillville buttons (`AssociateUnlocked` key); tutorials; `police_chase_on_start` → `PlayerScript.StartPoliceChase`; `setPrefsTo1[]` keys; `first_mission` → player + main car teleport; music; `blockDrop`; `deduct_debt` once (`<key>_deduct`, `PlayerScript.debtAm`); phone call (`call_sequence`, once via `<key>_Call` when `call_only_once`, `call_only_at_home`); `accept_job_on_start` → `<house>_mission_<job>` = 1 + side info; `unlock_quickBuy` → `QuickBuyEnabled`; rain / thunder; `StartCoroutine(InitiateOff)`. |
| `BeginStory()` (scene load, `GameManager.Update` IL_04b8 / `ContinueXP`) | `story = SPrefs "Story"` (clamped to the count), `InitiateStory()`. |
| `SaveStory()` | `SPrefs "Story" = story` (memory; disk only via `FBPP.Save`). |
| `SaveGame.SaveGameNewSystem` | writes `StoryBackup` / `Story` only when the live index is higher. No mission checkpoints exist. |
| `StoryManager.Start` | free mode: every `accept_job_on_start` job `mission_prefs = 2`, `display_as_complete`. |
| `StoryManager.Update` | 1–3 s polls that call `AdvanceStory` (13 sites): level-up, sell, skill, item, item pref, tips, missions, contracts, money, heavy, residence complete, planning mode … |
| `StoryObject.UpdateObject` (runs when `calledInitiate`) | `story_desc_to_activate == current story_description` → object active, else inactive (`dont_disable_if_story_changed` keeps it once activated); `enable_after_story` / `disableAfterStory` compare `Story` / `story` with the stage index of a key. Pure presentation of the index. |

## 2. Objectives, prerequisites, rewards [V]
- One stage = one HUD objective (`story_txt` ← localized `story_description`). Multi-part objectives are counters polled by `StoryManager.Update`.
- Prerequisites are the trigger's own condition: external triggers compare their key with `currentStoryScript.story_description`; polls read player counters / keys. Gating objects (`StoryObject`, `Diamond_Marker`, shop / heist / phone checks) read the index.
- Rewards of a **story step**: none paid by `AdvanceStory` itself. Side effects come from the **next stage's start** (`InitiateStory`: debt deduction, job acceptance, unlocks, calls) or from the trigger's own code before it calls `AdvanceStory` (e.g. `Mission_Item.MissionComplete1`: money, XP, `display_as_complete`, `missionsCompleted`; `ReadNote.BeginRead`: XP via `addPoint`, parcel code, achievement).

## 3. Trigger families (callers of `AdvanceStory`) [V]
| Family | Call sites | Multiplayer note |
|---|---|---|
| Notes | `ReadNote.BeginRead` IL_0195 (`advanceStory`, `storyDesc`) | **W6A supported** for note_103 / story14 |
| Pickups / car | `Pickupable.<PickingItem>` IL_0464, `Pickupable.WasPlacedInCar` | item ownership rule needed (physical story items) |
| Locks / panels | `Lockpick.DoorUnlock`, `MediumLockMaster.Unlocked`, `SecurityPanel.PanelDeactivate`, `SecurityCamera.DisableCamera` IL_0101 | camera / lock state not shared yet |
| Areas | `AdvanceTrigger.OnTriggerEnter`, `AdvanceStoryTrigger.OnTriggerEnter`, `ActivateAssociateJail.Update`, `IntroMap.Update`, `CarController.<RunCar>` | location: which player counts = decision |
| Shops / PC | `Shop_Manager.Buy_Item`, `DroneUpgradePanel.BuyUpgrade`, `ColorButton.BuyColor`, `Tutorial_PC.Update`, `BlackBay_Object.SellIt`, `Inventory.Sell_Item_NewSystem` ×2, `ATM_Object.SpawnMoney` | economy (W7) |
| NPC / skills | `AI.DiscoverHabit`, `Pickpocket_Manager.Success`, `PlayerScript.RefreshXP`, `PlayerScript.Update` IL_5e7a, `Bed.Stand` | personal counters vs shared = decision |
| Missions | `Mission_Item.MissionComplete1` IL_0182 | residence mission transaction (money / XP / keys) |
| Polls | `StoryManager.Update` ×13, `CheckPoliceEscaped`, `<AdvanceItem>` | host counters decide |
| Direct `story++` (no AdvanceStory) | `InitiateStory` home / jail skip, `HeistManager.StartHeist`, intro, `Credits.Start` | heists / ending = later W6 slices |

## 4. Vertical slice: note_103 → story14 [V]
- Stage 18 `story14` ("Disable the 104 entrance camera with your drone"): start effects `unlock_quickBuy`, `unlock_tutorial` (drone), phone call once. Completion triggers: `ReadNote` `DOMY/Dom 103/Interior_103/story object 103 notka/pPlane3` (`advanceStory`, `storyDesc = story14`) **or** `SecurityCamera` `Junkyard/SECURITY CAM (1)/Security Camera` (`onDisableAdvanceStory`).
- The note's parent `story object 103 notka` is a `StoryObject` with `story_desc_to_activate = story14`: the note exists only while story14 is current.
- `ReadNote.BeginRead`: opens the text, discovers `parcel1_madison` (W2B), XP / achievement if set, then `AdvanceStory()` when the current key is `story14`. `advanceStory` is never cleared.
- `ReadNote.Start` IL_0062..IL_007f: hides the note when `addParcelCode` and `SPrefs(parcel1_madison) >= 2` (parcel taken). In single-player the parcel cannot be taken before the note is read during story14; in multiplayer it can (shared code / shared loot), which would block the step.
- Next stage 19 `story15` ("Steal the cardboard box from 104 …"): start effects tutorial + call; completes by `advance_if_item_pref` poll.

## 5. Multiplayer design (W6A, 0.26.0) [M]
- **Ownership:** the host's `StoryManager.story` is the campaign index; durable only through the host's original saves. Record `story` (Session scope) = host index + stage key + last advance by.
- **Player 2 triggers:** `AdvanceStory` never runs on Player 2's copy (Harmony prefix). A call from a listed trigger (`StoryTriggers`, matched by stage key + kind + object path suffix) whose original condition just passed is proposed as `storyAdvance` (from index, key, trigger). The host decides with `StoryRules.Decide` (ready / listed / free mode / from index == live index / key == live key) and runs its own original `AdvanceStory()` once. Retransmission → proposal cache; replay / concurrent / late → `Stale`.
- **Following:** Player 2 runs the original scene-load path (`story = host index; InitiateStory(); SaveStory()`) for the presentation (HUD, `StoryObject` gates, tutorials, calls, job markers). Cleared for that one call and restored: `advance_if_home`, `advance_if_jail`, `police_chase_on_start`, `first_mission`, `deduct_debt` (actor / money effects of the host's step). Rate-limited to one follow per second; re-run after Player 2's scene loads.
- **Note visibility:** a code note hidden by `ReadNote.Start` (parcel taken) is re-shown while its own `advanceStory` step is current.
- **Unlisted triggers / polls on Player 2:** do nothing (logged once per stage key + trigger). The host's own triggers and polls run unchanged and publish.
- **Personal vs shared:** the story index and the host's stage-start effects are shared (once, on the host). Note XP, inventory, tool use and the reader's phone UI stay on the reader's PC. Parcel codes: W2B.

## 6. Extending (next W6 slices)
Add a `StoryTrigger` entry only after verifying (a) the trigger's own condition in IL, (b) any physical item / object it consumes (ownership + consumption rule, e.g. `Pickupable` story items need a host take like W4.2D loot), (c) rewards paid by the trigger before `AdvanceStory` (host transaction, exactly once). Polls need a decision on whose counters count; location skips need a decision on whose location counts; heists / ending need joint travel.

## 7. Open
- [Q] Player 2 HUD / phone call / StoryObject presentation after a follow (expected identical to the scene-load path).
- [Q] `SecurityCamera` alternative for story14 on Player 2 (not listed: camera state not shared).
- [A] `story_scripts` identical on both PCs (same game build) — follow refuses an index whose key differs.
