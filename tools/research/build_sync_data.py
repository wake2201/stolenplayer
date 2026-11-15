"""Build docs/game-atlas/data/interaction_catalog.json and sync_coverage.json (curated, evidence-tagged).

Each row: one gameplay action family. Original mechanics: VERIFIED_IL / VERIFIED_ASSET from the atlas research.
StolenPlayer status: from docs/PROGRESS.md (VERIFIED_RUNTIME only where the user confirmed it in PROGRESS).
Usage: python -I tools/research/build_sync_data.py
"""
import json
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs", "game-atlas", "data")
STATUSES = ["COMPLETE / VERIFIED", "IMPLEMENTED / NEEDS MANUAL QA", "PARTIAL", "MISSING", "BLOCKED BY DEPENDENCY", "UNSAFE", "UNKNOWN"]

# id, action, original entry (class.method), trigger, state, persistence, evidence
I = [
    ("move", "walk/run/crouch/jump", "PlayerMotorBehavior.CalculateMovement", "input", "pose, move flags", "XPlayer.. at save", "VERIFIED_IL"),
    ("carry_heavy", "pick up / carry / drop heavy object", "Pickupable.PickUpHeavy / DropHeavy", "ray tag Pickupable (heavy) + E", "PlayerScript.isCarrying, object pose", "trunk/van/shed keys", "VERIFIED_IL"),
    ("loot_pick", "pick up ordinary loot / money / unique", "Pickupable.PickUp -> <PickingItem>", "ray tag Pickupable + E", "object removed, backpack +1 / money", "ItemDay_<name>, Item_<name>, Item_<id>", "VERIFIED_IL"),
    ("loot_story", "pick up story / drone / special item", "Pickupable.<PickingItem> (advance_story_if_picked, DronePickedUp, specialAdd)", "ray + E", "story index, drone equip", "Story, Item_<id>", "VERIFIED_IL"),
    ("drop_item", "drop inventory item", "Inventory.DropItemNewSystem", "inventory UI", "runtime object 'dropped'", "locker at hideout checkpoint", "VERIFIED_IL"),
    ("storage", "car storage / hideout locker", "Inventory.OpenStorage / MoveItemToStorage", "ray tag CarBox", "Storage_/Locker_ counts", "Storage_Item_<id>, Locker_Item_<id>", "VERIFIED_IL"),
    ("door", "open/close door, furniture, sliding window", "Door.Use / Door.Update (slow)", "ray tag Door + E (hold = slow)", "isOpen, motion", "none", "VERIFIED_IL"),
    ("lockpick", "pick / key / padlock / crowbar break-in", "Lockpick.StartLockpicking / DoorUnlock / BreakPadlock; PlayerScript.BreakinCrowbar", "ray + minigame", "lock state, alarm", "none", "VERIFIED_IL"),
    ("window_glass", "cut / break / unlock window", "LockedWindow.BeginCut / BrokeWindow / UnlockWindow(NoAlarm)", "ray + tool", "glass, Door enabled, alarm", "none", "VERIFIED_IL"),
    ("window_climb", "climb through window", "WindowUse.UseWindow", "ray tag WindowEnter", "local player lerp", "none", "VERIFIED_IL"),
    ("breakable", "smash breakable / planks", "DestroyObject", "crowbar hit", "destroyed", "none (Mission_Item)", "VERIFIED_IL"),
    ("blinds_lights", "blinds, light switch, fuse box", "Window_Blinds / LightSwitch / FuseBoxSwitch", "ray + E", "toggle", "none", "VERIFIED_IL (class existence); method detail INFERRED"),
    ("camera_disable", "disable security camera / panel", "SecurityCamera.DisableCamera / SecurityPanel.PanelDeactivate", "drone / minigame", "isDisabled", "discovered_camera_prefs", "VERIFIED_IL"),
    ("alarm", "trigger alarm (window, lock, camera, panel, car)", "LockedWindow.CheckAlarm, CameraTrigger, Security_Panel_Minigame.Failure, car alarms", "event", "police chase", "location alert", "VERIFIED_IL"),
    ("safe", "crack safe", "SafeCrack / PlayerScript.SafeEnable", "minigame", "open", "SafesOpened", "VERIFIED_IL (existence)"),
    ("hide", "hide in spot", "HidingSpot.GetIn / Unhide", "ray tag HidingSpot", "PS.isHiding", "none", "VERIFIED_IL"),
    ("read_note", "read note", "ReadNote.BeginRead", "ray", "XP once, story", "<name>=1", "VERIFIED_IL"),
    ("dig", "dig hole", "DigHole", "shovel", "hole, item", "DugHoles", "VERIFIED_IL (existence)"),
    ("parcel", "open parcel locker", "ParcelMinigame / ParcelLocker", "code", "open", "parcel_code_pref", "VERIFIED_IL"),
    ("mission_accept", "accept residence mission", "Inventory.ResidenceClicked / StoryScript.accept_job_on_start", "phone UI / story", "<house>_mission_<i>=1", "key", "VERIFIED_IL"),
    ("mission_complete", "complete residence mission", "Mission_Item.Mission_Complete -> MissionComplete1", "photo/plant/destroy/knock-out/feed", "key=2, money, XP", "key, MissionsCompleted", "VERIFIED_IL"),
    ("story_advance", "advance story stage", "StoryManager.AdvanceStory / direct story++", "29 callers + polls", "Story index", "Story", "VERIFIED_IL+VERIFIED_ASSET"),
    ("contract", "accept / claim contract", "ContractManager.ActivateContract / ContractUIObj.ClaimReward", "phone", "contract state", "Contract*, <pref>_complete", "VERIFIED_IL"),
    ("job", "accept / claim job offer", "JobOffer.AcceptJob / ClaimRewards", "phone", "job_pref", "job_pref, Jobs_Complete", "VERIFIED_IL"),
    ("payphone", "payphone mission", "Payphone.TryActivate / Success / Failure", "payphone", "timer, spawned item", "Payphone<Region>, PayphoneActive..", "VERIFIED_IL"),
    ("heist", "start heist", "HeistManager.StartHeist", "heist board", "story++, travel", "Story", "VERIFIED_IL"),
    ("blackbay", "sell on BlackBay", "BlackBay_Object.SellIt", "phone", "money, story", "BlackbayLists", "VERIFIED_IL"),
    ("pickpocket", "pickpocket NPC", "Pickpocket_Manager.Begin_Pickpocket / Success", "minigame", "money/items", "<target>_week", "VERIFIED_IL"),
    ("atm", "hack ATM", "ATM_Object.Begin_Minigame / SpawnMoney", "minigame", "money spawned", "prefs_to_set", "VERIFIED_IL"),
    ("shop_buy", "buy in shop / phone", "Shop_Manager.Buy_Item", "UI", "item, money", "at checkpoint", "VERIFIED_IL"),
    ("sell", "sell at pawn / car service", "Inventory.Sell_Item_NewSystem", "UI", "money, stolen counters", "MoneyMade, StolenItems", "VERIFIED_IL"),
    ("upgrade_buy", "garage, van, car/drone/hideout upgrades, tips, bank, stocks", "Inventory.BuyGarage, Tutorial.BuyVan, Whiplist.*, DroneUpgradePanel.BuyUpgrade, HouseRenovator.*, BuyTip.BuyTipp, Darkbank, StockManager", "UI", "unlock flags, money", "various", "VERIFIED_IL"),
    ("skill_learn", "learn skill / ability", "Skill_Manager.Learn_Skill / AbilitiesManager", "skill UI", "skill flags, timers", "skill prefs, points", "VERIFIED_IL"),
    ("xp", "gain XP / level up", "GameManager.AddXP / PlayerScript.RefreshXP", "rewards", "XP, level", "XP keys", "VERIFIED_IL"),
    ("gas", "use sleeping gas", "SleepingGas.Release / SleepGasCloud", "tool", "particles, clouds, NPC sleep", "GasAmount", "VERIFIED_IL"),
    ("knockout", "knock out NPC (crowbar, baton, tranquilizer)", "AI.FallAsleep", "melee / dart", "NPC unconscious", "KnockedOutPeople", "VERIFIED_IL"),
    ("drone", "equip / fly drone", "DroneManager.AssignDrone / ActivateDrone", "inventory / key", "drone object, camera", "Item_<id>, upgrades", "VERIFIED_IL"),
    ("npc_routine", "NPC schedules / movement", "AI.<Startt> / AI.Update", "game hour", "NPC pose/behaviour", "none (daily events keyed)", "VERIFIED_IL"),
    ("npc_perception", "NPC sees / hears player", "SensorToolkit + AI.HeardSound / Player_Is_Seen", "sensors", "suspicion, flee, call police", "none", "VERIFIED_IL (wiring INFERRED)"),
    ("police", "police chase / stars / arrest", "PlayerScript.StartPoliceChase -> Police_Manager.Activate_Chase / End_Chase; GameManager.GotCaught", "crimes", "chase, units, stars", "location alert, residence alert", "VERIFIED_IL"),
    ("vehicle_drive", "enter / drive / exit player car", "CarController.EnterVehicle / RunCar / ExitCar", "ray tag SitCar", "car pose, seat", "CurrentCar, XCar..", "VERIFIED_IL"),
    ("vehicle_passenger", "ride as passenger", "(mod) VehicleSync passenger adapter", "E on car", "seat", "none", "mod"),
    ("vehicle_steal", "steal / deliver region car", "LockedCarDoor / DrillingCarDoor / OpenTownSelectionNoDelay delivery", "minigame / travel", "car item", "Item_<car_name>, CarCompleted_", "VERIFIED_IL"),
    ("travel", "travel between regions", "PlayerScript.ShowTownSelection -> GameManager.Select<Map>", "menu / border", "scene, CurrentLocation", "full save", "VERIFIED_IL"),
    ("sleep", "sleep / pass time", "PassTime.Begin/PassStart (Parking.Activate)", "bed / car", "time", "time keys at checkpoint", "VERIFIED_IL"),
    ("save", "save game", "SaveGame.SaveGameNewSystem", "travel/quicksave/autosave/ending", "disk", "all", "VERIFIED_IL"),
    ("associate", "associate companion", "CodenameAI", "story stage 29", "follow/modes", "AssociateUnlocked", "VERIFIED_IL"),
    ("cat", "pet cat", "CatAI.TouchCat", "E", "XP", "catty1", "VERIFIED_IL"),
]

# id -> (p1, p2_now, other_sees, other_consequences, saved, authority, msgs, reliable, dup_risk, rollback, reconnect, diff_regions, simultaneous, prereq, status, mod_refs)
S = {
    "move": ("yes", "yes", "yes", "n/a", "position at checkpoint only", "each player (host relays)", "player snapshot 29 B 20 Hz", "unreliable", "none", "n/a", "resume from host live/checkpoint spawn", "avatar hidden outside region", "n/a", "-", "COMPLETE / VERIFIED", "PlayerSync, RemoteAvatar (M2 accepted)"),
    "carry_heavy": ("yes", "no (refused by design)", "P1 carry visible to P2", "no", "trunk/van/shed only", "host lease per census id", "heavyLease req/grant, drop pose commit, deposit txn", "reliable", "high without lease", "lease release on disconnect", "object returns to scene state", "region-scoped", "first lease wins", "region objects, CarBoxSync pattern", "BLOCKED BY DEPENDENCY", "WorldLootSync HeavyPrefix (refusal), W3 carried presentation"),
    "loot_pick": ("yes", "yes (all census scenes)", "yes (object removed)", "yes (wallet/inventory)", "ItemDay_/Item_ via host", "host", "lootTxn + wloot chunks", "reliable", "rejected by LootBook", "Stale -> restore", "book reseeded from host keys", "host scene catalogs", "first committed wins", "census, catalogs", "IMPLEMENTED / NEEDS MANUAL QA", "WorldLootSync, HostLootCatalogs (W4.1/W4.1B/W4.2D)"),
    "loot_story": ("yes", "no (refused)", "P1 pickup removes P2 copy (shadow claim)", "no story effect on P2", "via host story", "host", "story txn + drone ownership", "reliable", "double story advance", "stage check", "index from host", "-", "stage check", "story sync, drone sync", "BLOCKED BY DEPENDENCY", "WorldLootSync ClientPolicy"),
    "drop_item": ("yes", "yes", "yes", "yes", "locker at hideout", "host entity table", "drop/pick txns", "reliable", "entity id", "lost like vanilla at reload", "n/a", "region-scoped", "first wins", "-", "IMPLEMENTED / NEEDS MANUAL QA", "WorldItemSync (W3)"),
    "storage": ("yes", "yes (car box), locker yes", "contents shared", "yes", "host save", "host", "storageTxn, carbox lease", "reliable", "txn ids", "journal", "host record", "car box per car region", "lease", "-", "IMPLEMENTED / NEEDS MANUAL QA", "CarBoxSync (car box user-verified M4.3.2b; locker W3 pending QA)"),
    "door": ("yes", "yes (Madison/Hillville/hideout)", "yes", "noise to NPCs local only", "none", "host commit per region epoch", "objTxn + wobj pages", "reliable", "version check", "n/a", "region init chunks", "per region", "reservation/version", "census", "IMPLEMENTED / NEEDS MANUAL QA", "WorldObjectSync/Bridge (user PASS on 0.16.0 for doors/drawers/furniture, PROGRESS v0.16.1 §0)"),
    "lockpick": ("yes", "yes (same regions)", "yes", "story/alarm effects not shared", "none", "host + reservation", "objTxn Reserve/Set", "reliable", "reservation", "release on cancel", "init chunks", "per region", "reservation", "census", "IMPLEMENTED / NEEDS MANUAL QA", "WorldObjectSync"),
    "window_glass": ("yes", "yes", "yes (glass mirror)", "alarm/police local", "none", "host", "objTxn", "reliable", "version", "n/a", "init", "per region", "version", "police authority for alarm", "PARTIAL", "WorldObjectSync glass mirrors; sliding-window report open (05 §6)"),
    "window_climb": ("yes", "yes", "avatar moves (no climb anim)", "n/a", "none", "local", "player snapshot", "unreliable", "none", "n/a", "n/a", "n/a", "n/a", "-", "PARTIAL", "PlayerSync (no climb clip)"),
    "breakable": ("yes", "yes (local)", "no", "no", "none", "host", "objTxn (new kind)", "reliable", "version", "n/a", "init", "per region", "first wins", "census kind", "MISSING", "-"),
    "blinds_lights": ("yes", "yes (local)", "no", "light affects NPC sight locally", "none", "host", "objTxn (new kinds)", "reliable", "version", "n/a", "init", "per region", "version", "census kinds", "MISSING", "-"),
    "camera_disable": ("yes", "yes (local)", "no", "no", "discovery only", "region owner/host", "objTxn", "reliable", "version", "n/a", "init", "per region", "first wins", "drone presentation", "MISSING", "-"),
    "alarm": ("yes", "yes (local police)", "no", "no", "location alert (sandbox on P2)", "region simulation owner + host for alert", "crime event (offender id)", "reliable", "event id", "n/a", "chase ends on reload (vanilla)", "per region", "owner serializes", "NPC/police authority", "MISSING", "-"),
    "safe": ("yes", "yes (local)", "no", "loot inside shared", "stat", "host", "objTxn", "reliable", "version", "n/a", "init", "per region", "reservation", "census kind", "MISSING", "-"),
    "hide": ("yes", "yes", "avatar position only", "NPC perception local", "none", "local + presentation", "snapshot flag", "unreliable", "-", "-", "-", "-", "spot occupancy UNKNOWN", "NPC perception", "PARTIAL", "PlayerSync"),
    "read_note": ("yes", "yes (sandbox key, XP local)", "no", "no", "<name> key", "host", "campaign txn", "reliable", "key", "n/a", "host keys", "-", "key", "campaign txn", "MISSING", "-"),
    "dig": ("yes", "yes (local)", "no", "no", "stat", "host", "objTxn + loot", "reliable", "version", "n/a", "init", "per region", "first wins", "census kind", "MISSING", "-"),
    "parcel": ("yes", "yes", "parcel loot shared", "parcel strings once on host", "parcel keys", "host", "lootTxn side effect", "reliable", "book", "-", "-", "-", "first wins", "-", "PARTIAL", "WorldLootSync parcel path (W4.2D); locker door state MISSING"),
    "mission_accept": ("yes", "yes (sandbox)", "no", "no", "key", "host", "campaign txn", "reliable", "key value", "n/a", "host keys", "global", "first wins", "campaign txn", "MISSING", "-"),
    "mission_complete": ("yes", "yes (sandbox; money excluded)", "no", "no", "key, counter", "host", "campaign txn + step counter state", "reliable", "double reward", "n/a", "counter not persisted (vanilla)", "global", "first wins", "story sync", "UNSAFE", "WalletPolicy excludes reward money"),
    "story_advance": ("yes", "yes (sandbox only)", "no", "no", "Story", "host", "storyTxn(stage key, trigger id) + story index replication", "reliable", "stage check", "n/a", "index from host", "global", "stage check first wins", "campaign txn layer", "MISSING", "-"),
    "contract": ("yes", "yes (sandbox; money excluded)", "no", "no", "keys", "host", "campaign txn", "reliable", "double reward", "-", "-", "global", "first wins", "campaign txn", "UNSAFE", "WalletPolicy"),
    "job": ("yes", "yes (sandbox; money excluded)", "no", "no", "keys", "host", "campaign txn", "reliable", "double reward", "-", "-", "global", "first wins", "campaign txn", "UNSAFE", "WalletPolicy"),
    "payphone": ("yes", "yes (sandbox; money excluded)", "no", "no", "keys", "host", "campaign txn + timer + runtime target entity", "reliable", "double reward", "-", "-", "region of target", "first wins", "campaign txn, dynamic entities", "UNSAFE", "WalletPolicy"),
    "heist": ("yes", "travel only", "travel shared", "story not shared", "Story", "host", "heist txn + joint travel", "reliable", "story++ twice", "-", "-", "both travel", "host decides", "story sync", "MISSING", "TravelHooks (Select*), JointTravel"),
    "blackbay": ("yes", "yes (money counted into wallet)", "no", "money yes, story no", "-", "host", "campaign txn for story objects", "reliable", "story double", "-", "-", "-", "-", "story sync", "PARTIAL", "WalletSync"),
    "pickpocket": ("yes", "yes (money excluded)", "no", "no", "weekly key", "region owner (NPC) + host", "NPC request", "reliable", "double", "-", "-", "-", "owner", "NPC authority", "UNSAFE", "WalletPolicy"),
    "atm": ("yes", "yes (local)", "no", "no", "weekly prefs", "host", "objTxn + loot", "reliable", "double money", "-", "-", "-", "first wins", "census kind", "MISSING", "-"),
    "shop_buy": ("yes", "yes", "-", "shared wallet", "item at checkpoint", "host wallet", "p2report Spend", "reliable", "journal seq", "Stale -> restore", "journal replay", "-", "host wins", "-", "IMPLEMENTED / NEEDS MANUAL QA", "WalletSync (user PASS wallet 0.16.0, PROGRESS v0.16.1 §0)"),
    "sell": ("yes", "yes", "-", "shared wallet", "money", "host wallet", "p2report", "reliable", "journal", "-", "-", "-", "-", "-", "IMPLEMENTED / NEEDS MANUAL QA", "WalletSync"),
    "upgrade_buy": ("yes", "refused on client", "-", "-", "unlock keys", "host campaign txn", "campaign txn", "reliable", "double purchase", "debit-once", "-", "global", "first wins", "campaign txn", "MISSING", "WalletPolicy Refuse"),
    "skill_learn": ("yes", "yes (sandbox)", "no", "no", "skill prefs (P2 not persistent)", "per player (decision)", "player record extension", "reliable", "-", "-", "host record", "-", "-", "decision: per-player progression", "PARTIAL", "DevTestMode only learns skills in test"),
    "xp": ("yes", "yes (local, not persistent)", "no", "no", "XP keys (P1 only)", "per player (decision)", "player record extension", "reliable", "double rewards via unsynced missions", "-", "-", "-", "-", "decision", "PARTIAL", "-"),
    "gas": ("yes", "yes (local)", "no", "no", "GasAmount (player)", "holder event + region NPC owner effects", "gas on/off event, cloud spawns", "reliable events, unreliable pose", "double NPC effect", "n/a", "-", "-", "owner", "NPC authority", "MISSING", "-"),
    "knockout": ("yes", "yes (local)", "no", "no", "stat", "region NPC owner", "NPC request", "reliable", "-", "-", "-", "-", "owner", "NPC authority", "MISSING", "-"),
    "drone": ("yes", "yes (local)", "no (nothing sent)", "no", "item, upgrades", "owner pilot + presentation", "drone pose snapshot, ownership", "unreliable pose / reliable ownership", "-", "-", "-", "6 regions", "-", "presentation layer", "MISSING", "DRONE_SYSTEM.md"),
    "npc_routine": ("yes", "yes (own copy)", "different NPC positions", "no", "none", "region simulation owner", "NPC snapshots", "unreliable", "-", "-", "restart from hour", "per region", "-", "region owner rule (exists for clocks)", "MISSING", "-"),
    "npc_perception": ("yes", "yes (own copy, own player only)", "no", "no", "none", "region simulation owner seeing both", "proxy + reaction redirect", "-", "-", "-", "-", "-", "-", "NPC replication", "MISSING", "-"),
    "police": ("yes", "yes (own copy)", "no", "no", "alerts (sandbox on P2)", "region owner + per-player chase flag + host alerts", "crime event, police snapshots, chase state", "reliable + unreliable", "-", "-", "chase ends on reload", "per region", "owner", "NPC replication", "MISSING", "-"),
    "vehicle_drive": ("yes", "yes", "yes (original car object observed)", "collisions solid", "fleet file", "host fleet + holder", "vehicle/vpark/vdoor", "unreliable pose, reliable claims", "holder rule", "-", "fleet", "region vehicle rule", "handoff lock", "-", "COMPLETE / VERIFIED", "VehicleSync (M5.0 verified in game)"),
    "vehicle_passenger": ("yes", "yes", "yes", "yes", "-", "host", "vehicleRide", "reliable", "-", "-", "-", "-", "-", "-", "IMPLEMENTED / NEEDS MANUAL QA", "VehicleSync M4.3 (user-tested, refinements pending QA)"),
    "vehicle_steal": ("yes", "yes (local car)", "no", "no", "car keys", "host", "region car state + campaign txn", "reliable", "double car", "-", "-", "-", "first wins", "region vehicle objects", "MISSING", "-"),
    "travel": ("yes", "yes", "yes", "fleet cars move", "full save (P1)", "host reservations", "travel*, jointTrip", "reliable", "reservation", "rollback", "fleet", "independent regions", "first committed wins", "-", "IMPLEMENTED / NEEDS MANUAL QA", "TravelHooks, VehicleSync (ordinary travel user PASS 0.16.0)"),
    "sleep": ("yes", "yes (coordinated)", "yes", "yes", "time at checkpoint", "host", "sleepStart/sleep/sleepShow", "reliable", "-", "-", "-", "region clocks", "-", "-", "COMPLETE / VERIFIED", "WorldSync, SleepPresenter (M3.1.2 accepted)"),
    "save": ("yes", "sandbox only", "-", "-", "host file only", "host", "-", "-", "-", "-", "client snapshot on join", "-", "-", "-", "IMPLEMENTED / NEEDS MANUAL QA", "SaveGuard, CampaignBootstrap"),
    "associate": ("yes", "own copy", "no", "no", "AssociateUnlocked", "host decision", "NPC replication", "-", "-", "-", "-", "-", "-", "NPC authority + design decision", "UNKNOWN", "-"),
    "cat": ("yes", "yes (repeatable per join)", "no", "XP discarded", "catty1", "decision", "campaign txn", "reliable", "repeat reward", "-", "-", "-", "-", "decision", "UNSAFE", "CAT_SYSTEM.md (DEFECTIVE outcome)"),
}

FIELDS = ["p1_can_perform", "p2_can_perform_now", "other_player_sees", "other_player_gets_consequences", "saved", "authority",
          "network_messages", "delivery", "duplication_risk", "rollback", "reconnect", "different_regions", "simultaneous",
          "prerequisites", "status", "mod_references"]


def main():
    meta = {"schema": "game-atlas/1", "game_version": "1.27c", "generated_by": "tools/research/build_sync_data.py"}
    inter = [{"id": "act:" + i, "action": a, "original_entry": e, "trigger": t, "state": s, "persistence": p, "evidence": ev}
             for i, a, e, t, s, p, ev in I]
    json.dump({"meta": dict(meta, catalog="interaction_catalog", description="Gameplay action families with original entry points"), "interactions": inter},
              open(os.path.join(OUT, "interaction_catalog.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    ids = [x[0] for x in I]
    assert set(ids) == set(S), (set(ids) ^ set(S))
    rows = []
    for i in ids:
        r = dict(zip(FIELDS, S[i]))
        assert r["status"] in STATUSES, r["status"]
        r["id"] = "act:" + i
        r["status_evidence"] = "VERIFIED_RUNTIME" if r["status"] == "COMPLETE / VERIFIED" else "docs/PROGRESS.md (implementation) / atlas research"
        rows.append(r)
    counts = {s: sum(1 for r in rows if r["status"] == s) for s in STATUSES}
    json.dump({"meta": dict(meta, catalog="sync_coverage", description="Per action: the 15 multiplayer questions + StolenPlayer status",
                            statuses=STATUSES, counts=counts, baseline="mod 0.17.0 working tree (W4.2D), PROGRESS 2026-11-15"),
               "coverage": rows}, open(os.path.join(OUT, "sync_coverage.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(len(inter), counts)


if __name__ == "__main__":
    main()
