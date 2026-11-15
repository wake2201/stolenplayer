"""Build the machine-readable game-atlas catalogs (docs/game-atlas/data/*.json).

Usage: python -I build_atlas_data.py <scratch_dir>
  <scratch_dir> must contain: decomp/ (ilspycmd -p output), scenes/ (scan_scenes.py), story_raw.json
  (dump_story.py), story_bind.json (story_bindings.py), loc_en.json (English localization table).
Generated values come from byte-exact scene decodes (VERIFIED_ASSET) or the decompiled IL (VERIFIED_IL).
Curated rows (interactions, coverage) carry their own evidence tag and source. Unknown values are null.
"""
import collections
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "docs", "game-atlas", "data")
SCENES = ["MainMenuNew", "MainScene", "HackingPuzzle", "MadisonSt", "PlayerHideout", "Auto Shop", "IntroHouse",
          "Warehouse", "Hillville", "Jail", "BankScene", "EndStory", "Forestside Resort", "HauntedMansion", "Museum",
          "SwatRange", "DrugFarm"]
# Verified CurrentLocation / namespace table (src/Shared/WorldState.cs Regions, PROGRESS W4.2D table)
REGION_ID = {"MadisonSt": 99, "Hillville": 97, "PlayerHideout": 0, "MainScene": -1000, "IntroHouse": -1, "Warehouse": 91,
             "Jail": 93, "BankScene": 92, "Forestside Resort": 94, "HauntedMansion": 95, "Museum": 96, "SwatRange": 98,
             "DrugFarm": 90, "Auto Shop": 3}
SCHEMA = "game-atlas/1"
EVIDENCE = ["VERIFIED_IL", "VERIFIED_ASSET", "VERIFIED_RUNTIME", "INFERRED", "UNKNOWN"]


def meta(name, desc, extra=None):
    m = {"schema": SCHEMA, "catalog": name, "description": desc, "game_version": "1.27c (Unity 2020.3.33f1, Mono)",
         "generated_by": "tools/research/build_atlas_data.py", "evidence_levels": EVIDENCE}
    if extra:
        m.update(extra)
    return m


def is_ref(v):
    return isinstance(v, list) and len(v) == 2 and all(isinstance(i, int) for i in v)


def scalars(d):
    return {k: v for k, v in d.items() if not is_ref(v) and not isinstance(v, (dict, list)) and v is not None}


def load_scene(scratch, i):
    p = os.path.join(scratch, "scenes", "level%d.jsonl" % i)
    return [json.loads(l) for l in open(p, encoding="utf-8")]


def main():
    scratch = sys.argv[1]
    os.makedirs(OUT, exist_ok=True)
    loc = json.load(open(os.path.join(scratch, "loc_en.json"), encoding="utf-8"))
    story = json.load(open(os.path.join(scratch, "story_raw.json"), encoding="utf-8"))
    binds = json.load(open(os.path.join(scratch, "story_bind.json"), encoding="utf-8"))
    summary = json.load(open(os.path.join(scratch, "scenes", "summary.json")))
    index = json.load(open(os.path.join(OUT, "source_index.json")))
    T = {t["type"]: t for t in index["types"]}
    scenes = {SCENES[i]: load_scene(scratch, i) for i in range(len(SCENES))}
    main_scene = scenes["MainScene"]

    # ------------------------------------------------------------------ missions
    AUTO = {  # StoryScript flag -> (checker, evidence source)
        "advance_if_level_up": "PlayerScript.RefreshXP (level-up branch) -> AdvanceStory",
        "advance_if_sell_anything": "Inventory.Sell_Item_NewSystem -> AdvanceStory",
        "advance_if_item_sold": "Inventory.Sell_Item_NewSystem when item_sell_ID == sold item_ID",
        "if_learned_skill_advance": "StoryManager.Update 1 s poll Skill_Manager.Check_If_Skill_Learned(skill_name)",
        "advanceIfInPlanningMode": "StoryManager.Update 3 s poll PS.planScript.planningActive",
        "advance_if_home": "StoryManager.InitiateStory: skipped when SPrefs CurrentLocation == 0 at stage start/reload",
        "advance_if_jail": "StoryManager.InitiateStory: skipped when SPrefs CurrentLocation == 93",
        "advance_if_has_item": "StoryManager.Update 1 s poll item.item_amount >= item_amount_req",
        "advance_if_tips_bought": "StoryManager.Update 1 s poll every BuyTip (TipPref key or tipBought)",
        "advance_if_item_pref": "StoryManager.Update 1 s poll SPrefs Item_<item_name> (or <item_name> if dont_add_prefix) == 1",
        "advance_if_residence_complete": "StoryManager.Update 1 s poll residence_script.isComplete (SelectResidence.RefreshIfComplete)",
        "advance_if_garage_bought": "StoryManager.Update 1 s poll GameManager.boughtGarage",
        "complete_missions_amount": "StoryManager.Update 1 s poll Inventory.missionsCompleted (key MissionsCompleted)",
        "complete_contracts_amount": "StoryManager.Update 1 s poll Inventory.contractsComplete (key ContractsComplete)",
        "complete_money_amount": "StoryManager.Update 1 s poll Inventory.moneyMade (key MoneyMade)",
        "steal_heavy_items": "StoryManager.Update 1 s poll Inventory.stolenBigItemsNr (key StolenBigItems)",
        "advance_if_slept": "Bed.Stand -> AdvanceStory",
        "on_repaint_advance_story": "ColorButton.BuyColor -> AdvanceStory",
        "advance_on_police_escape": "Police_Manager.End_Chase -> StoryManager.CheckPoliceEscaped",
        "display_amount_money_required": "StoryManager.Update 1 s poll required_money_item amount*value >= required_money_amount_item",
    }
    START = ["first_mission", "police_chase_on_start", "deduct_debt", "accept_job_on_start", "unlock_hillville",
             "unlock_associate", "unlock_quickBuy", "unlock_tutorial", "unlock_achievement", "unlock_achievement_hardcore",
             "save_story_on_start", "rain_on_start", "thunder_on_start", "block_saving", "block_jail", "block_item_drop",
             "block_all_buttons", "block_home_button", "block_pawn_shop_button", "block_habit_learning", "cant_be_heard",
             "if_detected_respawn", "on_story_begin_start_call", "call_only_once", "call_only_at_home", "try_show_wishlist",
             "block_heavy_from_car", "blockAutoPause", "custom_music"]
    ADV_FLAG = {"CheckCameras": "advanceStoryOnUse", "Lockpick": "on_unlock_advance_story", "SecurityCamera": "onDisableAdvanceStory",
                "Pickupable": ("advance_story_if_picked", "advance_story_if_placed_car"), "BlackBay_Object": "on_sell_advance_story",
                "Pickpocket_Object": "advance_story_if_pickpocketed", "ATM_Object": "advance_story_on_hack", "SecurityPanel": "advance_story",
                "Shop_Button": "advance_story_on_buy", "ReadNote": "advanceStory", "Heist": "advanceStoryIfStoryName"}
    ALWAYS_ADV = {"AdvanceTrigger": "AdvanceTrigger.OnTriggerEnter (player + condition)", "AdvanceStoryTrigger": "AdvanceStoryTrigger.OnTriggerEnter (tag Player)",
                  "ActivateAssociateJail": "ActivateAssociateJail.Update (cell door opened)", "AI": "AI.DiscoverHabit (marked habit; runtime marked_habits)"}
    stage_bind = collections.defaultdict(list)
    for b in binds:
        if "stage" not in b or b["class"] in ("StoryManager",):
            continue
        cls, fl = b["class"], b["flags"]
        role = "gate"
        if cls in ALWAYS_ADV:
            role = "advance"
        elif cls in ADV_FLAG:
            names = ADV_FLAG[cls] if isinstance(ADV_FLAG[cls], tuple) else (ADV_FLAG[cls],)
            if any(fl.get(n) for n in names) and not (cls == "Heist" and b["field"] != "storyName"):
                role = "advance"
        elif cls == "SelectResidence" and ".story_desc" in b["field"]:
            role = "residence_mission_link"
        elif cls == "DroneTech":
            role = "advance_if_upgrade_flag"
        stage_bind[b["stage"]].append({"role": role, "class": cls, "field": b["field"], "scene": SCENES[int(b["scene"][5:])],
                                       "path": b["path"], "active_in_asset": b["active"],
                                       "flags": sorted(k for k, v in fl.items() if v and "." not in k and "[" not in k),
                                       "evidence": "VERIFIED_ASSET"})
    # residence missions that advance the story
    res_adv = {}
    residences, res_missions = [], []
    for r in main_scene:
        if r["class"] != "SelectResidence":
            continue
        f = r["fields"]
        house = f["houseNumber"]
        residences.append({"id": "residence:%d" % house, "house": house, "scene": f["scene_name"], "region_id": REGION_ID.get(f["scene_name"]),
                           "selector_path": r["path"], "interior_object": f["interior_object_name"], "loot_targets": len(f["loot_items"]),
                           "loot_prefs": [x.get("loot_prefs") for x in f["loot_items"]],
                           "missions": len(f["residence_missions"]), "discovered_key": f["discoveredPref"] or None,
                           "alert_key": r["path"].split("/")[-1] + "_alert", "event_type": f["residenceEventType"],
                           "has_sound_security": f["hasSoundSecurity"], "kill_npcs_after_story": f["storyName"] if f["killNPCs_afterStory"] else None,
                           "complete_rule": "SelectResidence.RefreshIfComplete: every Item_<loot_prefs>==1 and every <house>_mission_<i>==2",
                           "evidence": "VERIFIED_ASSET+VERIFIED_IL"})
        for i, m in enumerate(f["residence_missions"]):
            mid = "resmission:%d:%d" % (house, i)
            row = {"id": mid, "house": house, "index": i, "scene": f["scene_name"], "save_key": "%d_mission_%d" % (house, i),
                   "save_values": {"0": "not offered", "1": "accepted (marker/objects shown)", "2": "complete"},
                   "text_key": m["mission_description"], "text_en": loc.get(m["mission_description"]), "pay": m["mission_pay"],
                   "xp": m["mission_add_XP"], "required_level": m["required_level"], "required_count": m["required_number_complete"],
                   "plant_mission": m["isPlantMission"], "advances_story_key": m["story_desc"] if m["on_complete_advance_story"] else None,
                   "counter_persisted": False, "evidence": "VERIFIED_ASSET (values) + VERIFIED_IL (SelectResidence.Start key, Mission_Item.MissionComplete1)"}
            res_missions.append(row)
            if row["advances_story_key"]:
                res_adv[row["advances_story_key"]] = mid
    # mission item placements
    mitems = collections.defaultdict(list)
    for sc, rows in scenes.items():
        for r in rows:
            if r["class"] == "Mission_Item" and "fields" in r:
                f = r["fields"]
                mitems["resmission:%d:%d" % (f["house_number"], f["mission_int"])].append({"scene": sc, "path": r["path"], "active_in_asset": r["active"]})
    for m in res_missions:
        m["mission_item_objects"] = mitems.get(m["id"], [])
    DIRECT = {0: None}
    direct_inc = {"intro": "PlayerScript.ShowTownSelection-path (PlayerScript.cs ~1764): story++ when the travel menu opens while CurrentLocation == -1",
                  "heist": "HeistManager.StartHeist: story++ when heist.advanceStoryIfStoryName and storyName == current key, then Select<heist scene>"}
    stages = []
    for s in story["stages"]:
        i = s["index"]
        auto = [{"flag": k, "checker": AUTO[k]} for k in AUTO if s.get(k)]
        start = [k for k in START if s.get(k)]
        params = {k: s[k] for k in ("deduct_amount", "item_amount_req", "required_mission_amount", "requiredContracts", "required_money_made",
                                    "heavy_items_amount", "item_sell_ID", "required_money_amount_item", "skill_name", "item_name",
                                    "tutorial_name", "achievement_name", "setPrefsTo1", "job_nr", "second_job_nr") if k in s}
        for k in ("item", "required_money_item", "house_script", "residence_script"):
            if isinstance(s.get(k), dict):
                params[k] = s[k].get("path")
        bl = stage_bind.get(i, [])
        triggers = [b for b in bl if b["role"] in ("advance", "advance_if_upgrade_flag")]
        if s["key"] in res_adv:
            triggers.append({"role": "advance", "class": "Mission_Item", "residence_mission": res_adv[s["key"]],
                             "method": "Mission_Item.MissionComplete1", "evidence": "VERIFIED_ASSET+VERIFIED_IL"})
        if s["key"] == "story6":
            triggers.append({"role": "advance", "class": "Tutorial_PC", "method": "Tutorial_PC.Update (hard-coded key story6)", "evidence": "VERIFIED_IL"})
        if s["key"] in ("story30b", "story33", "story66"):
            triggers.append({"role": "advance", "class": "HeistManager", "method": direct_inc["heist"], "evidence": "VERIFIED_IL+VERIFIED_ASSET"})
        if s["key"] == "story71":
            triggers.append({"role": "advance", "class": "ActivateEnd", "scene": "MadisonSt", "path": "Ending Trigger/STORY MARKER/1/end trigger",
                             "method": "ActivateEnd.OnTriggerEnter (no police chase) -> GameManager.EndingFade -> SaveGameNewSystem -> EndStory scene; Credits.Start: Story+1, CurrentLocation=0, FBPP.Save",
                             "evidence": "VERIFIED_IL+VERIFIED_ASSET"})
        if s["key"] in ("story73_end", "story73_freemode"):
            triggers.append({"role": "terminal", "method": "final stage (story73_end) / free mode forced index 80 by StoryManager.InitiateStory", "evidence": "VERIFIED_IL"})
        if s["key"] == "story2_escapeB":
            triggers.append({"role": "advance", "class": "IntroMap", "scene": "IntroHouse", "path": "INTRO SCRIPT",
                             "method": "IntroMap.Update: 1 s poll, pickedStuffHolder.childCount == 0 -> AdvanceStory", "evidence": "VERIFIED_IL+VERIFIED_ASSET"})
        if i <= 2:
            triggers.append({"role": "skip", "method": "MainMenu.StartNewGame with skipIntro: Story=3, CurrentLocation=99 (stages 0-2 skipped)", "evidence": "VERIFIED_IL"})
            triggers.append({"role": "candidate", "class": "PlayerScript", "method": direct_inc["intro"], "evidence": "VERIFIED_IL (stage mapping INFERRED)"})
        relevant = [k for k in ("item_name", "skill_name", "tutorial_name") if k in params]
        resolved = bool(auto or triggers)
        stages.append({"id": "story:%d" % i, "index": i, "key": s["key"], "text_en": s["text_en"],
                       "scenes_with_bindings": sorted({b["scene"] for b in bl}),
                       "start_effects": start, "parameters": params,
                       "note_parameters": "item_name/skill_name/tutorial_name are copied between stages by the designers; they matter only where the matching flag is set",
                       "completion": {"auto": auto, "triggers": triggers, "resolved": resolved},
                       "gating_objects": [b for b in bl if b["role"] == "gate"][:40],
                       "save": {"key": "Story", "written_by": "SaveGame.SaveGameNewSystem (StoryBackup/Story, only if higher) / StoryManager.SaveStory"},
                       "evidence": "VERIFIED_ASSET (StoryManager.story_scripts[%d], byte-exact) + VERIFIED_IL (StoryManager)" % i})
    side = {}

    def collect(cls, scene=None, fn=None):
        out = []
        for sc, rows in scenes.items():
            if scene and sc != scene:
                continue
            for r in rows:
                if r["class"] == cls and "fields" in r:
                    out.append((sc, r))
        return out
    cm = collect("ContractManager")[0][1]["fields"]
    side["contracts"] = [dict(scalars(c), id="contract:" + c["contractPref"], completion_key=c["contractPref"] + "_complete",
                              required_item=None if not c.get("requiredItem") else "ItemID ref", evidence="VERIFIED_ASSET") for c in cm["contracts"]]
    side["payphones"] = []
    for sc, r in collect("Payphone"):
        f = r["fields"]
        side["payphones"].append({"id": "payphone:" + f["payphonePref"], "scene": sc, "progress_key": f["payphonePref"],
                                  "missions": [dict(scalars(m), items_to_steal=len(m.get("itemsToSteal") or []), item_houses=m.get("itemHouseNumbers")) for m in f["missions"]],
                                  "achievement": f.get("achievementCompleted"), "evidence": "VERIFIED_ASSET"})
    side["heists"] = []
    for sc, r in collect("Heist"):
        f = r["fields"]
        side["heists"].append({"id": "heist:" + f["heistName"], "name_en": loc.get(f["heistName"]), "location_number": f["locationNumber"],
                               "required_story": f.get("requiredStory") or None, "advances_story_if_key": f["storyName"] if f.get("advanceStoryIfStoryName") else None,
                               "required_item_refs": len(f.get("requiredItemIDs") or []), "required_skills": f.get("requiredSkills"),
                               "garage_required": f.get("garageRequired", False), "extra_profit": f.get("extraProfit"),
                               "loot_prefs": [x.get("loot_prefs") for x in f.get("loot") or []], "hint_keys": f.get("hints"),
                               "npc_refs": len(f.get("npcs") or []), "evidence": "VERIFIED_ASSET"})
    side["job_offers"] = [dict(scalars(r["fields"]), id="job:" + (r["fields"].get("job_pref") or r["path"].split("/")[-1]), path=r["path"],
                               evidence="VERIFIED_ASSET") for sc, r in collect("JobOffer")]
    side["blackbay_objects"] = [{"id": "blackbay:" + r["path"].split("/")[-1], "path": r["path"], "value": r["fields"]["value"], "xp": r["fields"]["xp_add"],
                                 "advances_story_key": r["fields"]["story_name"] if r["fields"]["on_sell_advance_story"] else None,
                                 "story_key_ref": r["fields"]["story_name"] or None, "evidence": "VERIFIED_ASSET"} for sc, r in collect("BlackBay_Object")]
    side["pickpocket_targets"] = len(collect("Pickpocket_Object"))
    side["atm_objects"] = [{"scene": sc, "path": r["path"], **{k: v for k, v in scalars(r["fields"]).items() if k in ("advance_story_on_hack", "story_name", "prefs_to_set")}} for sc, r in collect("ATM_Object")]
    json.dump({"meta": meta("mission_catalog", "Story stages, residence missions, residences and independent side-progression systems",
                            {"story_stage_count": len(stages), "story_list_source": story["source"]}),
               "story": stages, "residences": residences, "residence_missions": res_missions, "side_systems": side},
              open(os.path.join(OUT, "mission_catalog.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)

    # ------------------------------------------------------------------ items
    pick_fields = ["is_money", "add_money", "is_heavy_item", "destroy_if_was_picked", "never_destroy", "dontDestroyDays", "deleteIfPref",
                   "advance_story_if_picked", "advance_story_if_placed_car", "isContractItem", "lock_parcel_after_pickup", "randomName", "isPainting"]
    placements = collections.defaultdict(lambda: collections.Counter())
    flagcount = collections.defaultdict(lambda: collections.Counter())
    for sc, rows in scenes.items():
        for r in rows:
            if r["class"] == "Pickupable" and "fields" in r:
                f = r["fields"]
                placements[f["item_ID"]][sc] += 1
                for k in pick_fields:
                    if f.get(k):
                        flagcount[f["item_ID"]][k] += 1
    items = []
    for r in main_scene:
        if r["class"] != "ItemID" or "fields" not in r:
            continue
        f = r["fields"]
        iid = f["item_ID"]
        flags = sorted(k for k in ("is_heavy", "heavy_in_shop", "is_cardboard", "is_hackable", "is_special", "isTool", "isLoot", "isKey", "isDrone",
                                   "car_part", "isCar", "isStoryItem", "isMissionItem", "wanted_on_Blackbay", "cant_sell", "isHotItem",
                                   "carPrefRequiredToSell", "ableToDiscover", "isNotStoryAfterStoryName") if f.get(k))
        items.append({"item_type_id": iid, "internal_name": f["item_name"], "definition_object": r["path"],
                      "display_name_en": loc.get(f["item_name"]), "value": f["item_value"], "weight": f["item_weight"], "add_xp": f["add_XP"],
                      "max_amount": f["max_amount"], "flags": flags, "story_key": f["storyName"] or None,
                      "hacked_parcel_pref": f["hacked_parcel_pref"] or None, "type_string": f["item_type_string"] or None,
                      "world_placements": dict(placements.get(iid, {})), "world_placement_flags": dict(flagcount.get(iid, {})),
                      "save_keys": ["Item_%d" % iid, "Storage_Item_%d" % iid, "Locker_Item_%d" % iid, "Item_Discovered_%d" % iid, "Item_Value_%d" % iid],
                      "evidence": "VERIFIED_ASSET (MainScene ITEM IDs/*, byte-exact) + VERIFIED_IL (ItemID.Save_Item_Amount/Start keys)"})
    items.sort(key=lambda x: x["item_type_id"])
    unknown_ids = sorted(set(placements) - {i["item_type_id"] for i in items})
    json.dump({"meta": meta("item_catalog", "Item TYPE definitions (ItemID) with world placement counts; world instances are in instances/*.json",
                            {"item_type_count": len(items), "identity_rules": {
                                "ItemTypeID": "ItemID.item_ID / Pickupable.item_ID (type, shared by many objects)",
                                "WorldObjectID": "census id = namespace + FNV-1a(scene/path#kind) with [siblingIndex] for duplicate names (src/Shared/WorldIdentity.cs)",
                                "SaveKey_world": "ItemDay_<GameObject name> (respawn day), Item_<GameObject name> (unique/money) - keyed by NAME, not path",
                                "SaveKey_inventory": "Item_<id>, Storage_Item_<id>, Locker_Item_<id>",
                                "RuntimeEntityID": "dropped objects are renamed 'dropped' by the game and have no scene identity"},
                             "placement_item_ids_without_definition": unknown_ids}),
               "items": items}, open(os.path.join(OUT, "item_catalog.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)

    # ------------------------------------------------------------------ world objects (types + instances)
    WO = {  # class -> (category, interaction entry, original state, save behaviour, mod status)
        "Door": ("door/furniture/window leaf", "PlayerScript ray tag Door -> Door.Use / hold -> slow; NPC Door.NPCUse", "isOpen, opening, openedByPlayer, broken (OpenDestroyed); type flags isDrawer/isCupboard/isFridge/isFileCabinet/isGlassCabinet/isContainer/isWindow/isCarDoor; motion rotateOpen/useVectorRotation/moveOpen", "none (reset on scene load)", "IMPLEMENTED / NEEDS MANUAL QA (Madison, Hillville, hideout; car doors via vdoor)"),
        "Lockpick": ("lock", "Lockpick.StartLockpicking minigame -> DoorUnlock / DoorUnlockKey / BreakPadlock; crowbar break-in", "unlocked, padlock broken, hasAlarm", "none; NPCs relock via Door.ResetLock -> RelockDoor", "IMPLEMENTED / NEEDS MANUAL QA (same regions)"),
        "MediumLock": ("lock (medium lock minigame)", "MediumLockMaster.Unlocked", "unlocked", "none", "PARTIAL (rides Lockpick where assigned; standalone UNKNOWN)"),
        "ElectricLock": ("electric lock", "electronics minigame", "unlocked", "none", "MISSING"),
        "WeldDoor": ("welded door (blowtorch)", "Blowtorch", "welded/cut", "none", "MISSING"),
        "LockedWindow": ("window lock/glass", "LockedWindow.BeginCut / UnlockWindowNoAlarm / UnlockWindow / BrokeWindow", "locked, cut, broken glass, alarm", "none", "IMPLEMENTED / NEEDS MANUAL QA"),
        "WindowUse": ("window climb-through", "ray tag WindowEnter -> WindowUse.UseWindow (moves local player)", "none (transient)", "none", "LOCAL ONLY (player movement, replicated through avatar)"),
        "Window_Blinds": ("blinds", "interaction toggle", "open/closed", "none", "MISSING"),
        "WindowExplosion": ("scripted window explosion", "mission script", "exploded", "none", "MISSING"),
        "DestroyObject": ("breakable (crowbar)", "crowbar hit -> DestroyObject", "destroyed stage", "none (mission completion via Mission_Item)", "MISSING (local)"),
        "TimedDestroy": ("timed cleanup helper", "auto", "lifetime", "none", "LOCAL (no gameplay state)"),
        "LightSwitch": ("light switch", "interact toggle", "on/off", "none", "MISSING"),
        "Light_Trigger": ("light area trigger", "player trigger", "lit state", "none", "MISSING"),
        "FuseBoxSwitch": ("fuse box", "interact", "power off", "none", "MISSING"),
        "SecurityCamera": ("security camera", "drone disable / panel / SpotCamera", "isDisabled, spotted, discovered", "discovered_camera_prefs key only", "MISSING"),
        "CameraTrigger": ("camera view trigger", "player in view -> StartPoliceChase", "triggered", "none", "MISSING"),
        "SecurityPanel": ("security panel minigame", "Security_Panel_Minigame -> PanelDeactivate", "deactivated", "none", "MISSING"),
        "Alarm": ("house alarm", "Alarm.TriggerAlarm (AIs HeardSound, police)", "triggered", "none", "MISSING (no scene instance in any level: prefab-only or unused, UNKNOWN)"),
        "GasDetector": ("gas detector", "SleepingGas/SleepGasCloud sensor -> TriggerAlarm", "triggered", "none", "MISSING"),
        "SafeCrack": ("safe", "SafeEnable minigame", "open", "SafesOpened stat", "MISSING"),
        "HidingSpot": ("hiding place", "HidingSpot.GetIn / Unhide", "occupied (player isHiding)", "none", "PARTIAL (avatar shows hiding? UNKNOWN; NPC perception local)"),
        "Vent": ("vent", "crowbar/screwdriver open", "opened", "none", "MISSING"),
        "ClimbIvy": ("climbable", "agility skill climb", "none", "none", "LOCAL (movement)"),
        "DigHole": ("dig spot", "shovel", "dug, item spawned", "DugHoles stat", "MISSING"),
        "StashTrigger": ("stash/hidden spot trigger", "player trigger", "found", "UNKNOWN", "MISSING"),
        "HiddenItem": ("hidden item (+100$)", "HiddenItem.PickIt", "found", "HiddenItems stat; key UNKNOWN", "UNSAFE for P2 (money excluded on client, W4.1B)"),
        "MailBox": ("mailbox", "interact", "opened/looted", "UNKNOWN", "MISSING"),
        "ParcelLocker": ("parcel locker", "ParcelMinigame code entry", "opened", "parcel_code_pref keys", "PARTIAL (parcel loot shared W4.2D; locker state MISSING)"),
        "Garage_Switch": ("garage door switch", "interact", "open", "none", "MISSING"),
        "CarGate": ("car gate", "switch/trigger", "open", "none", "MISSING"),
        "GarageOpenNPC": ("NPC garage opener", "NPC schedule", "open", "none", "MISSING (NPC)"),
        "Doorbell": ("doorbell", "interact -> NPC reaction", "rang", "none", "MISSING"),
        "ReadNote": ("note", "ReadNote.BeginRead (XP once via key <name>, may advance story)", "read", "SPrefs <transform name> = 1", "MISSING"),
        "TutorialTrigger": ("tutorial trigger", "player trigger", "shown", "Tutorial_* keys", "LOCAL (UI)"),
        "ATM_Object": ("ATM", "ATM_Minigame -> SpawnMoney (may advance story55)", "hacked", "prefs_to_set", "MISSING"),
        "Payphone": ("payphone mission giver", "Payphone.TryActivate", "current_mission", "Payphone<Region> + PayphoneActive/Minutes/Seconds/Name", "MISSING"),
        "Pickupable": ("world loot", "PlayerScript ray tag Pickupable -> Pickupable.PickUp / PickUpHeavy", "present/taken", "ItemDay_<name>, Item_<name>", "IMPLEMENTED / NEEDS MANUAL QA (ordinary/money/unique all scenes; heavy/story/drone/special BLOCKED)"),
        "RandomFileCabinet": ("random cabinet", "Start roll", "rolled variant", "none", "IMPLEMENTED / NEEDS MANUAL QA (seeded roll, DESIGN COMPROMISE)"),
        "CarBox": ("car / locker storage", "Inventory.OpenStorage", "contents", "Storage_Item_<id>/Locker_Item_<id>", "COMPLETE / VERIFIED (car box); locker IMPLEMENTED / NEEDS MANUAL QA"),
        "Place_Heavy_Trigger": ("heavy placement", "carry into trigger", "placed", "trunk/shed keys", "BLOCKED BY DEPENDENCY (heavy ownership)"),
        "ShopTrigger": ("shop entry", "trigger -> Inventory.OpenShop", "open", "none", "IMPLEMENTED (wallet purchases) / NEEDS MANUAL QA"),
        "PawnGuyTrigger": ("pawn shop seller", "trigger -> sell UI", "open", "none", "IMPLEMENTED (sales counted via wallet) / NEEDS MANUAL QA"),
        "Bed": ("bed / sleep", "Bed -> PassTime", "sleeping", "none (time keys at checkpoints)", "COMPLETE / VERIFIED (co-op sleep, M3.1.2)"),
        "OnPossesion": ("residence robbery zone", "trigger -> PlayerScript.isOnPossesion", "inside", "none", "LOCAL (player state)"),
        "LockedCarDoor": ("stealable car door lock", "car lock pick gun minigame", "unlocked", "none", "MISSING"),
        "CarAlarm": ("car alarm", "alarm trigger -> police", "ringing", "none", "MISSING"),
        "Lockpick_Pin": ("hideout lockpick practice", "minigame", "n/a", "none", "LOCAL"),
        "CheckCameras": ("camera view PC", "PlayerScript ray -> CheckCameras (story)", "used", "none", "BLOCKED BY DEPENDENCY (story)"),
        "StoryObject": ("story-gated object", "Start/Update enable by Story index/key", "active", "derived from Story", "BLOCKED BY DEPENDENCY (story sync)"),
        "AdvanceTrigger": ("story trigger", "OnTriggerEnter", "fired", "Story", "BLOCKED BY DEPENDENCY (story)"),
        "AdvanceStoryTrigger": ("story trigger", "OnTriggerEnter", "fired", "Story", "BLOCKED BY DEPENDENCY (story)"),
        "Mission_Item": ("residence mission target", "Mission_Item.Mission_Complete callers (photo, plant, destroy, knock-out, feed dog)", "progress", "<house>_mission_<i>", "MISSING"),
        "PlantEvidence": ("plant-mission target", "plant interaction", "planted", "via Mission_Item", "MISSING"),
        "ContractItem": ("contract target item", "pickup", "found", "contract keys", "PARTIAL (loot shared; contract completion host-only)"),
        "Pickpocket_Object": ("pickpocket target", "Pickpocket_Manager minigame", "pickpocketed this week", "<name>_week", "MISSING"),
        "SimpleOpenClose": ("animator open/close helper (asset-store)", "ObjectClicked (no verified game caller)", "objectOpen", "none", "UNKNOWN (no interaction path verified)"),
    }
    type_rows, inst_by_scene = [], collections.defaultdict(list)
    for cls, (cat, inter, state, save, mod) in WO.items():
        counts = {v["scene"]: v["classes"][cls]["count"] for v in summary.values() if cls in v["classes"]}
        active = {v["scene"]: v["classes"][cls]["active"] for v in summary.values() if cls in v["classes"]}
        t = T.get(cls, {})
        type_rows.append({"id": "wotype:" + cls, "class": cls, "category": cat, "interaction": inter, "state": state,
                          "save_behaviour": save, "mod_status": mod, "instances_per_scene": counts, "active_in_asset_per_scene": active,
                          "methods": [m["name"] for m in t.get("methods", [])], "save_keys_in_code": sorted({k for m in t.get("methods", []) for k in m["save_keys"]}),
                          "source": t.get("file"), "evidence": "VERIFIED_ASSET (counts) + VERIFIED_IL (methods/keys); interaction/state text from IL reads and docs/research"})
    keep = {"Door": ["isOpen", "isDrawer", "isCupboard", "isFridge", "isFileCabinet", "isGlassCabinet", "isContainer", "isWindow", "isCarDoor", "rotateOpen", "useVectorRotation", "moveOpen", "key_item_ID"],
            "Pickupable": ["item_ID", "is_money", "add_money", "is_heavy_item", "destroy_if_was_picked", "never_destroy", "dontDestroyDays", "advance_story_if_picked", "advance_story_if_placed_car", "story_name", "isContractItem", "lock_parcel_after_pickup", "parcel_pref"],
            "LockedWindow": ["isWindow", "canBreak", "hasAlarm", "starsAmount", "noDoor", "complete_mission", "unlockFromInside"],
            "Lockpick": ["LockType", "isPadlock", "hasAlarm", "LockpickDifficulty", "reqEq", "on_unlock_advance_story", "story_name"],
            "SecurityCamera": ["canBeDisabledDrone", "discovered_camera_prefs", "onDisableAdvanceStory", "storyName"],
            "Alarm": ["triggerPolice"]}
    for sc, rows in scenes.items():
        names = collections.Counter(r["path"] for r in rows)
        for r in rows:
            if r["class"] not in WO:
                continue
            f = r.get("fields") or {}
            row = {"class": r["class"], "path": r["path"], "active_in_asset": r["active"], "path_unique_in_dump": names[r["path"]] == 1}
            for k in keep.get(r["class"], []):
                if k in f and f[k] not in (False, None, "", 0):
                    row[k] = f[k]
            if "error" in r:
                row["decode_error"] = r["error"][:80]
            inst_by_scene[sc].append(row)
    os.makedirs(os.path.join(OUT, "instances"), exist_ok=True)
    inst_files = {}
    for sc, rows in inst_by_scene.items():
        fn = "instances/%s.json" % re.sub(r"\W+", "_", sc)
        json.dump({"meta": meta("world_object_instances", "Scene instances of world-object classes in " + sc,
                                {"scene": sc, "region_id": REGION_ID.get(sc), "count": len(rows),
                                 "path_note": "display paths without [siblingIndex]; census identity disambiguates duplicates (WorldIdentity.IdentityPath)"}),
                   "instances": rows}, open(os.path.join(OUT, fn), "w", encoding="utf-8"), ensure_ascii=False, indent=0)
        inst_files[sc] = {"file": fn, "count": len(rows)}
    json.dump({"meta": meta("world_object_catalog", "World-object TYPES; instances per scene in instances/<scene>.json",
                            {"instance_files": inst_files, "decode": {lv: {"scene": v["scene"], "game_script_instances": sum(c["count"] for c in v["classes"].values()),
                                                                            "decoded_byte_exact": sum(c["ok"] for c in v["classes"].values())} for lv, v in summary.items()}}),
               "types": type_rows}, open(os.path.join(OUT, "world_object_catalog.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)

    # ------------------------------------------------------------------ NPCs
    NPC = {
        "AI": ("resident/tenant", "fixed scene object bound to SelectResidence.AIs[]", "hour schedule BehaviourNumber[hour] + DailyEventManager events", "RaySensor sight + RangeSensor hearing + light (ShadowDetector)", "flee home + call police (EscapeHomeAndCallCops/Delayed_Police_Call), catch, pepper spray, armed"),
        "GuardAI": ("patrol guard", "no decoded scene instance (prefab or spawned; UNKNOWN)", "waypoint patrol pointsWalk", "RayCast look + HeardNoise", "chase/shoot (StartChase), HitSleep"),
        "PolicemanAI": ("police officer", "spawned by Police_Manager.SpawnNewPoliceCar/SpawnPoliceSimple (dynamic)", "chase/check-out last_known_location", "Player_Is_Seen/Player_In_Hear_Range sensor events", "taser/shoot -> PlayerScript.GetTazed -> GotCaught"),
        "CopCar": ("police car", "spawned dynamic", "drive to scene / at player / away", "PlayerIsSeen", "deliver officers"),
        "SidewalkAI": ("pedestrian", "SidewalkSpawner pool (random)", "walk sidewalk points by time of day", "SpotMe/IsSeen", "surprised/escape/catch -> StartPoliceChase(starsAmount)"),
        "DogAI": ("dog", "no decoded scene instance (prefab or spawned; UNKNOWN)", "behaviours", "SpotDog", "bark -> alert"),
        "CatAI": ("cat", "scene object", "idle", "n/a", "TouchCat weekly XP reward (catty1 key)"),
        "CodenameAI": ("associate (companion)", "scene object (MainScene/Jail)", "follows player; modes openLocks/heavyCarry/mark", "n/a", "helps player; CallOffPolice"),
        "MobGuyAI": ("special NPC", "scene object", "UNKNOWN", "UNKNOWN", "UNKNOWN"),
        "PriestAI": ("special NPC", "scene object", "UNKNOWN", "UNKNOWN", "UNKNOWN"),
        "MannequinAI": ("special NPC (mannequin)", "scene object", "UNKNOWN", "UNKNOWN", "UNKNOWN"),
        "DemonAI": ("scripted/special", "scene object", "UNKNOWN", "UNKNOWN", "UNKNOWN"),
        "ImpAI": ("scripted/special", "scene object", "UNKNOWN", "UNKNOWN", "UNKNOWN"),
        "HelicopterAI": ("police helicopter", "spawned by Police_Manager (helicopter field)", "follow", "UNKNOWN", "spotlight"),
        "Ambulance": ("ambulance", "AmbulanceManager", "drive", "n/a", "n/a"),
    }
    npc_rows = []
    for cls, (role, spawn, sched, perc, react) in NPC.items():
        counts = {v["scene"]: v["classes"][cls]["count"] for v in summary.values() if cls in v["classes"]}
        t = T.get(cls, {})
        npc_rows.append({"id": "npc:" + cls, "class": cls, "role": role, "spawn": spawn, "schedule": sched, "perception": perc,
                         "reactions": react, "instances_per_scene": counts, "field_count": len(t.get("fields", [])),
                         "methods": [m["name"] for m in t.get("methods", [])], "persistence": "none (positions/AI state reset on scene load)",
                         "player_binding": "single local PlayerScript via FindGameObjectWithTag(\"Player\") (VERIFIED_IL, WORLD_SYNC_AUDIT E.5)",
                         "mod_status": "MISSING (each instance simulates its own NPCs against its own player)",
                         "evidence": "VERIFIED_ASSET (counts) + VERIFIED_IL (methods); role/reaction text from IL and WORLD_SYNC_AUDIT E; UNKNOWN where marked"})
    json.dump({"meta": meta("npc_catalog", "NPC / police / animal classes", {"police": {
        "stars_cap": "Police_Manager.AddNewStar increments only while current_stars < 2 (VERIFIED_IL)",
        "chase_flag": "PlayerScript.isPoliceChase (per player instance)",
        "persistent": "Location_Manager.location_string alert level, <residence>_alert (queued parcel string)",
        "end": "Police_Manager.End_Chase: DailyEventManager.SetTodayAsBurglarDay, StoryManager.CheckPoliceEscaped, PS.tryToSave (autosave)",
        "steam_remote_play": "GameManager.steamRemotePlay + otherPlayerDrone (PlayerRemoteDrone): original second-player drone mode (VERIFIED_IL existence; runtime use UNKNOWN)"}}),
        "npcs": npc_rows}, open(os.path.join(OUT, "npc_catalog.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("missions", len(stages), "resmissions", len(res_missions), "items", len(items), "wotypes", len(type_rows),
          "instances", sum(len(v) for v in inst_by_scene.values()), "npcs", len(npc_rows), "unknown placement ids", unknown_ids[:10])


if __name__ == "__main__":
    main()
