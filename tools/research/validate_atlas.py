"""Structural validation of docs/game-atlas/data catalogs. Exit code 1 on any error.
Usage: python -I tools/research/validate_atlas.py"""
import json
import os
import re
import sys

D = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "docs", "game-atlas", "data")
ATLAS = os.path.dirname(D)
errors, notes = [], []


def load(n):
    try:
        return json.load(open(os.path.join(D, n), encoding="utf-8"))
    except Exception as e:
        errors.append("%s: cannot load (%s)" % (n, e))
        return None


def need(cond, msg):
    if not cond:
        errors.append(msg)


def unique(rows, key, name):
    ids = [r.get(key) for r in rows]
    need(all(i is not None and i != "" for i in ids), "%s: missing %s" % (name, key))
    dup = {i for i in ids if ids.count(i) > 1}
    need(not dup, "%s: duplicate %s %s" % (name, key, sorted(dup)[:5]))


def main():
    cats = ["item_catalog.json", "world_object_catalog.json", "mission_catalog.json", "npc_catalog.json",
            "interaction_catalog.json", "sync_coverage.json"]
    data = {c: load(c) for c in cats}
    for c, d in data.items():
        if d is None:
            continue
        m = d.get("meta", {})
        need(m.get("schema") == "game-atlas/1", c + ": meta.schema")
        need(m.get("catalog") == c[:-5], c + ": meta.catalog name")
    it = data["item_catalog.json"]
    if it:
        unique(it["items"], "item_type_id", "items")
        need(len(it["items"]) == 452, "items: expected 452 types")
        for i in it["items"]:
            need(isinstance(i["item_type_id"], int) and i["internal_name"], "item row incomplete %s" % i.get("item_type_id"))
            need("evidence" in i, "item without evidence %s" % i["item_type_id"])
        need(not it["meta"]["placement_item_ids_without_definition"], "placements reference undefined item ids")
    wo = data["world_object_catalog.json"]
    if wo:
        unique(wo["types"], "id", "world types")
        for sc, f in wo["meta"]["instance_files"].items():
            p = os.path.join(D, f["file"])
            need(os.path.exists(p), "missing instance file " + f["file"])
            if os.path.exists(p):
                inst = json.load(open(p, encoding="utf-8"))
                need(len(inst["instances"]) == f["count"], "instance count mismatch " + f["file"])
                types = {t["class"] for t in wo["types"]}
                need(all(r["class"] in types for r in inst["instances"]), "instance of unknown type in " + f["file"])
    mc = data["mission_catalog.json"]
    if mc:
        unique(mc["story"], "id", "story")
        need([s["index"] for s in mc["story"]] == list(range(81)), "story indices not 0..80")
        unique(mc["residence_missions"], "id", "residence missions")
        unique(mc["residences"], "id", "residences")
        rm = {r["id"] for r in mc["residence_missions"]}
        keys = {s["key"] for s in mc["story"]}
        unresolved = [s["key"] for s in mc["story"] if not s["completion"]["resolved"]]
        need(not unresolved, "unresolved stages %s" % unresolved)
        for s in mc["story"]:
            for t in s["completion"]["triggers"]:
                if t.get("residence_mission"):
                    need(t["residence_mission"] in rm, "story %s references unknown %s" % (s["key"], t["residence_mission"]))
        for r in mc["residence_missions"]:
            if r["advances_story_key"]:
                need(r["advances_story_key"] in keys, "%s advances unknown key %s" % (r["id"], r["advances_story_key"]))
            need(re.fullmatch(r"\d+_mission_\d+", r["save_key"]) is not None, "bad save key " + r["save_key"])
        for h in mc["side_systems"]["heists"]:
            for k in ("required_story", "advances_story_if_key"):
                if h[k]:
                    need(h[k] in keys, "heist %s %s unknown" % (h["id"], h[k]))
    npc = data["npc_catalog.json"]
    if npc:
        unique(npc["npcs"], "id", "npcs")
    ic, sc = data["interaction_catalog.json"], data["sync_coverage.json"]
    if ic and sc:
        unique(ic["interactions"], "id", "interactions")
        unique(sc["coverage"], "id", "coverage")
        need({r["id"] for r in ic["interactions"]} == {r["id"] for r in sc["coverage"]}, "interaction/coverage id sets differ")
        st = set(sc["meta"]["statuses"])
        for r in sc["coverage"]:
            need(r["status"] in st, "bad status " + r["id"])
            for f in ("p1_can_perform", "p2_can_perform_now", "other_player_sees", "other_player_gets_consequences", "saved", "authority",
                      "network_messages", "delivery", "duplication_risk", "rollback", "reconnect", "different_regions", "simultaneous", "prerequisites"):
                need(f in r and r[f] not in (None, ""), "coverage %s missing %s" % (r["id"], f))
    ev = re.compile(r"VERIFIED_IL|VERIFIED_ASSET|VERIFIED_RUNTIME|INFERRED|UNKNOWN|mod")
    for c in ("interaction_catalog.json",):
        for r in (data[c] or {}).get("interactions", []):
            need(ev.search(r["evidence"]) is not None, "evidence tag missing " + r["id"])
    # docs cross-links
    for n in sorted(os.listdir(ATLAS)):
        if n.endswith(".md"):
            txt = open(os.path.join(ATLAS, n), encoding="utf-8").read()
            for link in re.findall(r"\]\(([0-9]{2}_[A-Z_]+\.md)\)", txt):
                need(os.path.exists(os.path.join(ATLAS, link)), "%s links missing %s" % (n, link))
    print("errors:", len(errors))
    for e in errors:
        print(" -", e)
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
