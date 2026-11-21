# 09 — Save, checkpoints and persistence

Foundation: `docs/WORLD_SYNC_AUDIT.md` §B–C (save system, persistence matrix), `docs/CODEBASE_MAP.md` §3.10, `src/Shared/Campaign.cs` (verified save rules, key classes) and `src/Shared/WorldMirror.cs` (`KeyPolicy` families). Machine data: `data/save_keys.json` (every SPrefs/PlayerPrefs/FBPP key expression → reader/writer methods).

## 1. Mechanisms (VERIFIED_IL)
| Mechanism | Trigger | What happens | Reaches disk |
|---|---|---|---|
| In-memory write | any `SPrefs.Set*` | FBPP dictionary only (`AutoSaveData = false`) | no |
| Deferred keys | `GameManager.mission_strings_to_set`, `parcel_strings_to_set` (residence alerts, parcels, `VanBought`, loot parcels) | set to 1 inside `SaveGameNewSystem` | at the next checkpoint |
| Travel save | `GameManager.Select<Map>` | `CurrentLocation`, `Saved_Pos = 0`, `SaveGameNewSystem(false)` | yes |
| Manual / quicksave | key `quicksave` → `PlayerScript.Quicksave` (`CheckIfCanSave`) | `SaveGame1` → `SaveGameNewSystem(true)` in Madison/Hillville/SwatRange, else `(false)` | yes |
| Autosave (option `AutoSave`) | `PlayerScript.tryToSave` from `CarController.ExitRunningCar`, `Police_Manager.End_Chase` | `Quicksave` after 1 s if allowed | yes |
| Save-and-quit | `Inventory.QuitGame(true)` → `SaveOnTheRun` → `Quicksave` | – | yes |
| Quit / menu without save | `GameManager.LoadMenu`, `QuitGame(false)` → `SaveGame.LoseGame` (empty) | memory discarded | no |
| Profile slots | `GameManager.SaveToProfile/LoadProfile` (`File.Copy`/`Delete`) | – | yes |
| Ending | `GameManager.EndingFade` → `SaveGameNewSystem`; `Credits.Start` → `Story+1`, `CurrentLocation=0`, `FBPP.Save` | – | yes (twice) |
| Hideout summary | `GameManager.SaveAll` (no static caller; UI event INFERRED) | `Item_` counts of the outing, `StealthRating`, `FBPP.Save` | yes |
| Caught / checkpoint reload | `GameManager.LoadCheckpoint` | MainScene reload WITHOUT a save: FBPP reads the **disk** save again (unsaved changes dropped; W6C.1 evidence). Refused / replaced by Respawn during a multiplayer session | no |
| New game / free mode | `MainMenu.StartNewGame` / `StartFreeMode` (`DeleteAll`, initial keys, `SPrefs.Save`) | – | yes |

`CheckIfCanSave` refuses in a vehicle with a blocked door ray, during a police chase, when seen by an NPC, inside a possession zone, while sleeping, while bleeding, with a drone active, on a stage with `block_saving`, or while hiding.

**There are no mission checkpoints.** "Checkpoint" in this game means the last disk save (any mechanism above). Story progress is monotonic through saves (`Story`/`StoryBackup` written only when higher).

## 2. `SaveGameNewSystem` write order (VERIFIED_IL; WORLD_SYNC_AUDIT B.3)
1. `Playtime`, `StoryBackup`/`Story`, `ThiefRating` (`Rating1..6`).
2. Position (`XPlayer..ZPlayer`, `XCar..`, `Saved_Pos`) only with `savePos`.
3. Payphone state, `Inventory.SaveStats` (statistics), notepad.
4. `GameTime`, `Week`, `BLTHealth`, `DrunkLevel`, `GasAmount`, `Day`, `GasTankLeftCar`, `RentTimer`, `BatonLife`, `CrowbarLife`.
5. Cloak, survival, abilities, contracts, upgrade shop, car paint/upgrades, CMS parts, `CurrentCar`, `StealthRating`, `Drone`, `TotalHoursPassed`, trunk item.
6. Dropped items → locker (hideout only), cardboard item.
7. `Money` (after travel-damage deduction and the negative clamp), `Debt`.
8. Every `Item_<id>`/`Storage_Item_<id>`/`Locker_Item_<id>`.
9. The deferred string lists → 1.
10. XP/level.
11. `FBPP.Save` / `SaveToMainProfile`.

## 3. Persistent systems: key, writer, reader, owner
| System | Keys | Writer (original) | Reader | Written when | Reset | MP owner (required) | Mod today |
|---|---|---|---|---|---|---|---|
| Story | `Story`, `StoryBackup`, `<key>_deduct`, `<key>_Call` | `SaveGameNewSystem`, `StoryManager.SaveStory`, `Credits.Start`, `InitiateStory` | `StoryManager.BeginStory`, gates | checkpoint | new game | host | not mirrored (W6 planned); client sandbox copy |
| Residence missions | `<house>_mission_<i>` | `SelectResidence`/`InitiateStory`/`Mission_Item` (immediate `SPrefs`) | `Mission_Item.LoadAll`, `RefreshIfComplete` | memory → checkpoint | – | host | sandbox |
| Residence alert | `<residence>_alert` | deferred string list | `SelectResidence` | checkpoint | `RemoveAlert` | host | sandbox |
| Location police alert | `Location_Manager.location_string` | `SetAlert`/`RemoveOnePoster` | `CopCar`, `PolicemanAI`, `Location_Check` | memory → checkpoint | posters removed | host | sandbox |
| Loot respawn / uniques | `ItemDay_<name>`, `Item_<name>` | `Pickupable.SetDayRespawn`, `<PickingItem>` | `Pickupable.<Start>` | memory → checkpoint | day passes | host | `ItemDay_` mirrored (W2); takes host-committed (W4.1/W4.2D) |
| Inventory (P1) | `Item_<id>` | `ItemID.Save_Item_Amount` | `ItemID.Start` | checkpoint | – | host (P1) | host save |
| Inventory (P2) | host record `p2inv` file | mod | mod | per change (durable write) | – | host | IMPLEMENTED (M4.3.3) |
| Car storage / locker | `Storage_Item_<id>`, `Locker_Item_<id>` | `Save_Item_Amount` | `ItemID.Start` | checkpoint | – | host | host-authoritative (CarBoxSync) |
| Wallet | `Money` (+ mod `TS2MP_WalletTxn`) | `SaveGameNewSystem` | `SaveGame.LoadGame` | checkpoint | – | host (shared balance) | IMPLEMENTED (W4.1B) |
| Bank | `BankBalance`, `BankIncrease` | `Darkbank`, `DayNightTime` interest | Darkbank | immediate `SPrefs` → checkpoint | – | decision (household) | P2 refused (W4.1B) |
| Debt / rent | `Debt`, `RentTimer` | story deduct, `SaveGame.Update` rent | `LoadGame` | checkpoint | – | host household | host only; client rent excluded |
| XP / level / skills | `XP`, `XPPoints`, `Lvl`, `MaxXP`, `ExtraPoints`, `SpecialPoints`, `ThiefScore`, skill prefs | `Save_XP_And_Level`, `Learn_Skill` | `LoadGame`, `Skill_Manager` | checkpoint | – | per player (decision) | P2 XP not persistent (open decision W4.2C) |
| Abilities | ability timers | `AbilitiesManager.SaveAbilities` | `LoadAbilities` | checkpoint | – | per player | – |
| Time | `GameTime`, `Day`, `Week`, `TotalHoursPassed`, `NewDay` | `SaveGameNewSystem`, `DayNightTime` | `LoadGame` | checkpoint | – | host (region clocks) | IMPLEMENTED (M3.1) |
| Daily events / hot item | `DailyEvent*`, `EventDay`, `GeneratedEvents`, `CurrentHotItem`, `CurrentDayHotItem` | `DailyEventManager`, `HotItemManager` | same | immediate | monthly/daily | host | mirrored (W2) |
| Vehicles | `CurrentCar`, `XCar..`, `GasTankLeftCar`, `VanBought`, `TrunkItem`, `HeavyVan_*`, `PlayerCar1/2` | `SaveGameNewSystem`, `VehiclesManager`, `Whiplist` | `InitiateMainCar`, `LoadPlayerPosition` | checkpoint | – | host fleet | fleet file (M4.2) |
| Heavy storage | `PosX_item<i>`…, `HeavyItemShed*` | `VanHeavyItems.SaveItems`, `HeavyItemsShed` | `LoadItems` | checkpoint | – | host | not synced |
| Contracts / jobs / payphones | see [03](03_SIDE_MISSIONS.md) | managers | managers | immediate / checkpoint | – | host | sandbox |
| Unlocks | `AssociateUnlocked`, `QuickBuyEnabled`, `garage`, `VanBought`, tips `TipPref`, drone upgrade prefs, renovations | various | various | immediate / deferred | – | host | sandbox (garage/drone purchases refused for P2) |
| Statistics | `Inventory.SaveStats` keys | `SaveStats` | `ReloadStats` | checkpoint | – | host (or per player) | sandbox |
| Position | `CurrentLocation`, `Saved_Pos`, `XPlayer..`, `MadisonPoint`, `HillvillePoint` | `Select*`, `SaveGameNewSystem` | `GameManager.Start` | checkpoint / menu open | – | per instance (P2 from host spawn plan) | IMPLEMENTED (v0.16.1) |
| Preferences | PlayerPrefs (`Profile`, graphics, `AutoSave`, `TPP` in SPrefs) | options UI | – | immediate | – | local | LocalPreference class |

## 4. Live state vs checkpoint state (rules)
- The host's live FBPP model = last checkpoint + in-memory changes. A client snapshot taken from it can contain unsaved progress (W4.2 Phase A bootstrap copies the live model). After a host crash the client must not keep unsaved host progress as durable.
- Deferred keys exist only in `GameManager` lists until a checkpoint. Mirroring them early would persist state that the original would drop on quit.
- Player 2 never writes the campaign file: the client plays on a sandbox (`SaveGuard`), and campaign changes must reach the host as transactions. The host save carries mod bookkeeping only under the `TS2MP_` prefix.

## 5. Contradictions found between existing documents (to resolve in the next doc pass)
| Item | Document A | Document B | Evidence-based resolution |
|---|---|---|---|
| Money ownership | `src/Shared/WorldMirror.cs` `KeyPolicy`: `Money` = `PlayerOwn` (M4.3.3) | W4.1B (`Wallet.cs`, PROGRESS): host-owned shared balance | W4.1B is newer and implemented; `KeyPolicy` comment/owner is stale (documentation drift, no behaviour claim verified) |
| Scene contents readable | WORLD_SYNC_AUDIT §Method: "not readable statically" | W4.2C research used UnityPy; W4.2D `SceneCatalog.cs`; this atlas | readable (VERIFIED_ASSET) |
| Mission count | CODEBASE_MAP §3.5 lists `StoryManager` only (no count) | this atlas: 81 stages | 81 (byte-exact decode) |
| `ItemID.item_ID` type vs instance | CODEBASE_MAP §3.3: "H: type id" | this atlas: 452 unique ids under `ITEM IDs/` | type id (VERIFIED_ASSET) |
| CODEBASE_MAP §5 | "No ILSpy installed" | ILSpy 11.2 now installed | update CODEBASE_MAP §5 |
