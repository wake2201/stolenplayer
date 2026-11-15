"""Render Markdown tables from docs/game-atlas/data catalogs (keeps docs and JSON consistent).
Usage: python -I render_tables.py <table> > fragment.md
Tables: story, residence_missions, side, items_summary, wotypes, npcs"""
import collections
import json
import os
import sys

D = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs", "game-atlas", "data")


def j(n):
    return json.load(open(os.path.join(D, n), encoding="utf-8"))


def esc(s):
    return str(s if s is not None else "").replace("|", "/").replace("\n", " ")


def story():
    m = j("mission_catalog.json")
    print("| # | Key | Objective (EN) | Completion | Start effects | Binding scenes |")
    print("|---|---|---|---|---|---|")
    for s in m["story"]:
        comp = [a["flag"] for a in s["completion"]["auto"]]
        for t in s["completion"]["triggers"]:
            if t["role"] in ("advance", "advance_if_upgrade_flag", "terminal"):
                lab = t.get("class", "") + ("." + t["field"].split(".")[-1] if t.get("field") else "")
                if t.get("residence_mission"):
                    lab += "(" + t["residence_mission"] + ")"
                if t["role"] == "terminal":
                    lab = "terminal"
                comp.append(lab)
            elif t["role"] == "candidate":
                comp.append("intro story++ (INFERRED)")
        comp = sorted(set(comp))
        eff = [e for e in s["start_effects"] if e not in ("blockAutoPause", "on_story_begin_start_call", "call_only_once", "custom_music", "block_habit_learning", "block_home_button", "block_pawn_shop_button", "cant_be_heard", "rain_on_start", "thunder_on_start", "call_only_at_home")]
        p = s["parameters"]
        extra = []
        for k in ("deduct_amount",) if s["start_effects"] and "deduct_debt" in s["start_effects"] else ():
            extra.append("debt -%s" % p.get(k))
        if "setPrefsTo1" in p:
            extra.append("setPrefsTo1=" + ",".join(p["setPrefsTo1"]))
        print("| %d | `%s` | %s | %s | %s | %s |" % (s["index"], s["key"], esc(s["text_en"]), esc(", ".join(comp)),
                                                    esc(", ".join(eff + extra)), esc(", ".join(s["scenes_with_bindings"]))))


def residence_missions():
    m = j("mission_catalog.json")
    print("| Id / save key | Scene | Objective (EN) | Pay | XP | Lvl | Count | Plant | Advances story | Target objects |")
    print("|---|---|---|---|---|---|---|---|---|---|")
    for r in m["residence_missions"]:
        print("| `%s` | %s | %s | %d | %d | %d | %d | %s | %s | %d |" % (r["save_key"], r["scene"], esc(r["text_en"] or r["text_key"]), r["pay"], r["xp"],
                                                                 r["required_level"], r["required_count"], "yes" if r["plant_mission"] else "", r["advances_story_key"] or "",
                                                                 len(r["mission_item_objects"])))


def side():
    m = j("mission_catalog.json")["side_systems"]
    print("### Contracts (`ContractManager.contracts`, MainScene)\n")
    print("| Pref | House | Any house | Infinite | Rep level | Money | XP | Rep |")
    print("|---|---|---|---|---|---|---|---|")
    for c in m["contracts"]:
        print("| `%s` | %s | %s | %s | %s | %s | %s | %s |" % (c["contractPref"], c.get("houseNumber"), c.get("anyHouse"), c.get("isInfinite"),
                                                           c.get("requiredRepLevel"), c.get("addMoney"), c.get("addXP"), c.get("addRep")))
    print("\n### Payphone chains\n")
    for p in m["payphones"]:
        print("- `%s` (%s), %d missions, achievement `%s`:" % (p["progress_key"], p["scene"], len(p["missions"]), p["achievement"]))
        for i, x in enumerate(p["missions"]):
            print("  - %d: required item `%s`, %s min, pay %s, XP %s, item houses %s" % (i, x.get("item_name_required"), x.get("timeInMinutes"), x.get("pay"), x.get("xpAdd"), x.get("item_houses")))
    print("\n### Heists (`MANAGERS/HEIST MANAGER/Heist*`)\n")
    print("| Heist | Location | Required story | Advances story at | Garage | Extra profit | Loot prefs |")
    print("|---|---|---|---|---|---|---|")
    for h in m["heists"]:
        print("| %s (`%s`) | %s | %s | %s | %s | %s | %d |" % (esc(h["name_en"]), h["id"][6:], h["location_number"], h["required_story"] or "", h["advances_story_if_key"] or "",
                                                            h["garage_required"], h["extra_profit"], len(h["loot_prefs"])))
    print("\n### Job offers (Hire!Me)\n")
    for x in m["job_offers"]:
        print("- `%s`: rep %s, pay %s, XP %s, +rep %s, location %s" % (x.get("job_pref") or x["path"].split("/")[-1] + " (UI template)", x.get("required_reputation"), x.get("pay"), x.get("addXP"), x.get("addRep"), x.get("jobOfferLocation")))
    print("\n### BlackBay objects that advance the story\n")
    for b in m["blackbay_objects"]:
        if b["advances_story_key"]:
            print("- `%s` value %s, XP %s → `%s`" % (b["id"], b["value"], b["xp"], b["advances_story_key"]))
    print("\nBlackBay objects total: %d; pickpocket targets: %d; ATM objects: %d." % (len(m["blackbay_objects"]), m["pickpocket_targets"], len(m["atm_objects"])))


def items_summary():
    c = j("item_catalog.json")
    items = c["items"]
    fl = collections.Counter(f for i in items for f in i["flags"])
    print("Item types: **%d** (ids %d..%d, all unique). Flag counts: %s.\n" % (len(items), items[0]["item_type_id"], items[-1]["item_type_id"],
                                                                         ", ".join("`%s` %d" % kv for kv in fl.most_common())))
    print("| Id | Internal name | EN name | Value | Weight | Flags | World placements |")
    print("|---|---|---|---|---|---|---|")
    for i in items:
        if i["flags"] and set(i["flags"]) & {"isTool", "isDrone", "isKey", "isStoryItem", "isMissionItem", "is_special", "is_heavy", "isCar", "is_hackable", "is_cardboard"}:
            wp = sum(i["world_placements"].values())
            print("| %d | `%s` | %s | %s | %s | %s | %d |" % (i["item_type_id"], i["internal_name"], esc(i["display_name_en"]), i["value"], i["weight"], ", ".join(i["flags"]), wp))


def wotypes():
    c = j("world_object_catalog.json")
    print("| Class | Category | Instances (total) | Scenes | Save behaviour | StolenPlayer status |")
    print("|---|---|---|---|---|---|")
    for t in sorted(c["types"], key=lambda x: -sum(x["instances_per_scene"].values())):
        print("| `%s` | %s | %d | %d | %s | %s |" % (t["class"], esc(t["category"]), sum(t["instances_per_scene"].values()), len(t["instances_per_scene"]), esc(t["save_behaviour"]), esc(t["mod_status"])))


def npcs():
    c = j("npc_catalog.json")
    print("| Class | Role | Instances | Spawn | Perception | Reactions |")
    print("|---|---|---|---|---|---|")
    for n in c["npcs"]:
        print("| `%s` | %s | %s | %s | %s | %s |" % (n["class"], esc(n["role"]), sum(n["instances_per_scene"].values()) or "0 in scenes (prefab-only or spawned)", esc(n["spawn"]), esc(n["perception"]), esc(n["reactions"])))


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    globals()[sys.argv[1]]()
