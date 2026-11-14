# CAT_SYSTEM — hideout cat, petting reward (W4.2C Phase A investigation)

Date: 2026-11-14. Method: read-only Mono.Cecil IL dump + read-only UnityPy read of `level4` (PlayerHideout). Legend as
in `FURNITURE_SYSTEM.md`.

## 1. Object and spawning [V]
- One `CatAI` in the hideout scene: GameObject `AMAZING CAT` (active at load), `RichAI` on the same object, `Animator`
  on child `AMAZING CAT/AMAZING CAT`, four walk points `CAT WALK 1 … (3)`. Not spawned at runtime; it is part of the
  `PlayerHideout` scene (region namespace 0). No `CatAI` in MadisonSt, Hillville or MainScene.
- Serialized `CatPref = "catty1"` (read from the asset); `addXp` is not serialized in the asset and is set in `Start`.

## 2. Movement, animation, AI [V `CatAI.Start/Update/BeginIdle/FindRandomPoint`]
- `Start`: `GM` via tag `GameManager`; meow timer `Random.Range(10,30)`; `addXp = SPrefs.GetInt("CatStuff") == 1 ? 100 : 25`;
  tag `Cat`; `FindRandomPoint()` (random walk point → A* `RichAI` destination).
- `Update`: meow every `Random.Range(30,120)` s (not while `GM.car_lights_on`); walking → Animator bools `walking`
  true / `sleeping`, `sitting` false, `maxSpeed 0.75`; on arrival `BeginIdle`; idle → `maxSpeed 0`, after
  `Random.Range(5,30)` s next point; idle pose `sleeping` when `GM.car_lights_on`, else `sitting`.
- Fully local and random on each instance (A* path, random points, random timers) → the two cats differ.

## 3. Petting and reward [V]
- Input: `PlayerScript.Update` IL_4b0d–IL_4b78: ray hit tag `Cat` → universal prompt + `IndependentHighlight`;
  Rewired `interact` down → `CatAI.TouchCat()`.
- `TouchCat` IL_0000–IL_00b8: reward only if `SPrefs.GetInt(CatPref) != GM.currentWeek + 1` **and** `!catTouched`:
  `SPrefs.SetInt(CatPref, currentWeek + 1)`; `catTouched = true`; `PS.currentXPPoints += addXp`; `PS.RefreshXP()`;
  level-up sound; on-screen `+<addXp>XP` (original warning text). Always: `ultraMeow` plays if not playing.
- **Frequency:** once per in-game week per save (`catty1` stores week + 1), and at most once per scene instance
  (`catTouched`). 25 XP, or 100 XP when `CatStuff == 1`.
- `CatStuff`: read in `CatAI.Start` only; **no writer in Assembly-CSharp IL** (no `ldstr "CatStuff"` elsewhere) —
  probably written through a key built at runtime (shop / DLC item pref) [A] — [Q].

## 4. Persistence
- `catty1` (int, week + 1) is written by `SPrefs.SetInt` immediately; it reaches disk with the next save (FBPP, no
  autosave) [V `InitializePrefs.Reinitialize` AutoSaveData = false].
- XP: `PS.currentXPPoints` → saved as `XP` (with `Lvl`, `XPPoints`, `MaxXP`, `SpecialPoints`, `ThiefScore`,
  `ExtraPoints`) by `SaveGame.Save_XP_And_Level` [V].
- Mod: `CampaignPolicy.Classify` puts `catty1`, `CatStuff` and all XP keys in class `World` (not player-owned) [M].
  Player 2's sandbox is overwritten from the host snapshot at every join; Player 2's XP is not part of the Player-2
  record (`P2Inventory`: money, bank, tool wear, backpack items) [M].

## 5. Current multiplayer behaviour (derived, [Q] to confirm)
- Host petting: original reward, saved in the host save. Correct.
- Player 2 petting: `catty1` and XP change only in Player 2's sandbox; neither reaches the host; at the next join the
  host snapshot restores `catty1` → Player 2 can collect again after every rejoin (repeatable), and the XP itself is
  discarded with the sandbox (lost). Duplicate-but-worthless today; becomes a real duplication once Player-2 XP is
  persisted.
- Two cats walk differently on both screens (cosmetic).

## 6. Future implementation (not implemented) — preventing duplicate rewards
Decision needed: **per player** (original semantics per character: each player once per week) vs **shared** (one
reward per week for the pair).
- Per player (recommended, matches "player-specific progression"): host-authoritative transaction `catPet` with the
  week number; host keeps `TS2MP_CatWeekP2` (ModHost key) for Player 2 and the original `catty1` for itself; grants
  only if the stored week differs; Player 2's XP must then be persisted in the Player-2 record (prerequisite).
- Shared: the host commits `catty1` (World key) and both instances apply it; the actor's instance grants XP only after
  the host commit.
- Either way: Harmony **prefix** on `CatAI.TouchCat` on Player 2 decides locally from the host-mirrored week (no
  network round-trip before the meow/prompt), sends the transaction, and the XP is added only on host acceptance —
  never twice for one week. Event-driven; zero per-frame cost.
- Cat movement: leave local (presentation); optionally host-driven walk-point index (1 byte on change) if the visual
  difference matters.

## 7. Classification
| Aspect | Class |
|---|---|
| Walk/idle/meow/animation | local visual/animation state |
| `catTouched` | temporary region state (per scene instance, per player) |
| `catty1` weekly flag | persistent campaign state — player-specific or shared (decision) |
| XP grant | player-specific; host-authoritative transaction once Player-2 XP is persisted |
