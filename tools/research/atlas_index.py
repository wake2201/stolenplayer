"""Game-atlas source indexer (research only, read-only on the game).

Input : an ILSpy project decompilation of Assembly-CSharp.dll, produced with
        ilspycmd -p -o <decomp_dir> "<game>/Thief Simulator 2_Data/Managed/Assembly-CSharp.dll"
        (keep that directory OUTSIDE the repository; it is proprietary code).
Output: <out_dir>/source_index.json   - per game type: base, fields, methods, calls, save keys, category
        <out_dir>/save_keys.json      - SPrefs key expressions -> readers / writers
        <out_dir>/call_index.json     - choke-point callers (story, money, XP, police, save)

Only names, signatures, string-literal save keys and call relationships are emitted;
no method bodies are copied.  Brace tracking is heuristic (strings/chars stripped).

Usage: python -I tools/research/atlas_index.py <decomp_dir> <out_dir>
"""
import json
import os
import re
import sys

THIRD_PARTY_PREFIX = (
    "AG", "Amplify", "Battlehub", "Beautify", "BuildingMaker", "CTI", "CalmWater", "CloudSkybox",
    "CompassNavigator", "Cooldhands", "DynamicFog", "ECM", "EVP", "Gley", "Kino", "Lean", "MK",
    "NGS", "Obi", "Pathfinding", "Rewired", "SC_", "Steamworks", "TMPro", "TurnTheGameOn", "UnityStandardAssets",
    "VolumetricFog", "AlmostEngine", "BasicTools", "DroneController",
)

CATEGORIES = [
    ("story", r"Story|Narrat|Cutscene|Intro|EndStory|Ending|Tutorial|Note|Phone|Call"),
    ("mission", r"Mission|Contract|Job|Heist|Payphone|Parcel|Habit|Residence|Task|Objective|Wishlist|HotItem|Burglar|DailyEvent"),
    ("police", r"Police|Cop|Swat|Jail|Wanted|Arrest|Chase|Siren"),
    ("npc", r"\bAI\b|AI$|^AI|NPC|Sidewalk|Codename|Resident|Guard|Dog|Cat|Animal|Pet|Civil|Ped|Sleep|Hear|Vision|Detect|Suspic"),
    ("vehicle", r"Car|Vehicle|Van|Truck|Drive|Garage|Wheel|Trunk|Fuel|Whiplist|Traffic|Mechanic"),
    ("item", r"Item|Loot|Pickup|Pickable|Inventory|Shop|Sell|Storage|Box|Valuable|Money|Wallet|Pawn|Fence|Market|Buy"),
    ("world_object", r"Door|Window|Lock|Safe|Drawer|Cabinet|Fridge|Furniture|Switch|Fuse|Alarm|Camera|Keypad|Computer|Laptop|Mailbox|Gate|Fence|Glass|Blind|Curtain|Destruct|Break|Hiding|Bed|Workbench|Light|Container|Interact|Trigger|Terminal"),
    ("ability", r"Skill|Abilit|Gas|Drone|Hack|Crowbar|Drill|Cutter|Flash|Distract|Tool|Gadget|Lockpick|Taser|Smoke|Noise|Upgrade"),
    ("save", r"Save|Load|Prefs|Checkpoint|Persist"),
    ("player", r"Player|Motor|Camera|Legs|Hand|Crouch|Carry|Heavy"),
    ("world", r"Region|Town|Scene|Map|Area|Time|Day|Weather|Sun|Clock|Travel"),
]

STRIP_STR = re.compile(r'@"(?:[^"]|"")*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])\'')
TYPE_DECL = re.compile(r'^(\s*)(?:\[[^\]]*\]\s*)*(?:public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+)*(class|struct|enum|interface)\s+(\w+)(?:<[^>]*>)?\s*(?::\s*([^{]+))?$')
METHOD = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*((?:public|private|protected|internal|static|virtual|override|abstract|sealed|extern|new|unsafe|async)\s+)*([\w<>\[\],\.\s]+?)\s+(\w+)\s*\(([^;]*)\)\s*(?:where [^{]*)?$')
CTOR = re.compile(r'^\s*(?:public|private|protected|internal|static)\s+(\w+)\s*\(([^;]*)\)\s*(?::\s*(?:base|this)\(.*\))?$')
FIELD = re.compile(r'^\s*(?:\[[^\]]*\]\s*)*((?:public|private|protected|internal|static|readonly|const|new)\s+)+([\w<>\[\],\.\s]+?)\s+(\w+)\s*(?:=[^;]*)?;$')
PROP = re.compile(r'^\s*((?:public|private|protected|internal|static|virtual|override)\s+)+([\w<>\[\],\.]+)\s+(\w+)\s*$')
CALL = re.compile(r'\b([A-Za-z_]\w*)\s*\.\s*([A-Za-z_]\w*)\s*\(')
BARECALL = re.compile(r'(?<![\.\w])([A-Za-z_]\w*)\s*\(')
SPREF = re.compile(r'\b(SPrefs|PlayerPrefs|FBPP)\s*\.\s*(Get\w*|Set\w*|HasKey|DeleteKey|DeleteAll|Save)\s*\(')
GENERIC_CALL = re.compile(r'(?:GetComponent|GetComponentInChildren|GetComponentInParent|FindObjectOfType)\s*<\s*(\w+)\s*>\s*\(\s*\)\s*\.\s*(\w+)\s*\(')
LOCAL_DECL = re.compile(r'([A-Z]\w*)\s+(\w+)\s*=')
KEYWORDS = {"if", "for", "foreach", "while", "switch", "return", "new", "typeof", "sizeof", "catch", "using", "lock", "nameof", "default", "base", "this", "checked"}

CHOKE = {
    "StoryManager.AdvanceStory", "StoryManager.SetStory", "StoryManager.SaveStory", "StoryManager.InitiateStory",
    "GameManager.AddXP", "GameManager.PlayMissionComplete", "Inventory.AddMoneyMade", "GameManager.SaveGameNewSystem",
    "SaveGame.SaveGameNewSystem", "SaveGame.LoadGame", "Police_Manager.CallPolice", "Police_Manager.StartChase",
    "PlayerScript.Arrested", "Mission_Item.MissionComplete1", "Mission_Item.Mission_Complete", "Skill_Manager.Learn_Skill",
    "Pickupable.PickUp", "Pickupable.PickUpHeavy", "Pickupable.DropHeavy", "ItemID.Item_Picked",
}


def extract_call_args(text, start):
    """Return the raw first argument text of a call whose '(' is at text[start-1]."""
    depth, i, buf = 1, start, []
    while i < len(text) and depth > 0:
        c = text[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if depth == 0:
                break
        elif c == "," and depth == 1:
            break
        buf.append(c)
        i += 1
    return "".join(buf).strip()


def categorize(name):
    cats = [c for c, rx in CATEGORIES if re.search(rx, name)]
    return cats or ["other"]


def is_third_party(rel, name):
    if os.sep in rel or "/" in rel:
        return True  # namespaced folder = library code (game code is global namespace)
    return name.startswith(THIRD_PARTY_PREFIX)


def parse_file(path, rel):
    raw = open(path, encoding="utf-8", errors="replace").read().splitlines()
    types, stack = [], []  # stack of (type_dict, brace_depth_at_body)
    depth = 0
    cur_method = None  # (type, method_dict, depth)
    pending_type = None
    for ln, line in enumerate(raw, 1):
        code = STRIP_STR.sub('""', line)
        code = re.sub(r"//.*$", "", code)
        stripped = code.strip()
        if pending_type is None and cur_method is None:
            m = TYPE_DECL.match(code)
            if m:
                bases = [b.strip() for b in (m.group(4) or "").split(",") if b.strip()]
                outer = stack[-1][0]["name"] + "." if stack else ""
                pending_type = {"name": outer + m.group(3), "kind": m.group(2), "base": bases[0] if bases else None,
                                "interfaces": bases[1:], "file": rel, "line": ln, "fields": [], "methods": []}
        if cur_method is None and stack and pending_type is None and depth == stack[-1][1]:
            t = stack[-1][0]
            fm = FIELD.match(code)
            if fm and "(" not in stripped.split("=")[0]:
                t["fields"].append({"name": fm.group(3), "type": " ".join(fm.group(2).split()),
                                    "static": "static" in fm.group(1) or "const" in fm.group(1),
                                    "public": "public" in fm.group(1), "line": ln})
            else:
                mm = METHOD.match(code) if not stripped.endswith(";") else None
                cm = CTOR.match(code) if not stripped.endswith(";") else None
                pm = PROP.match(code) if not mm and not cm else None
                if mm and mm.group(3) not in KEYWORDS and (mm.group(2).split() or [""])[-1] not in KEYWORDS:
                    meth = {"name": mm.group(3), "ret": " ".join(mm.group(2).split()), "params": " ".join(mm.group(4).split()),
                            "line": ln, "end": None, "calls": set(), "keys": [], "strings": set()}
                    t["methods"].append(meth)
                    cur_method = (t, meth, depth)
                elif cm and cm.group(1) == t["name"].split(".")[-1]:
                    meth = {"name": ".ctor", "ret": "", "params": " ".join(cm.group(2).split()), "line": ln, "end": None,
                            "calls": set(), "keys": [], "strings": set()}
                    t["methods"].append(meth)
                    cur_method = (t, meth, depth)
                elif pm:
                    t["fields"].append({"name": pm.group(3), "type": pm.group(2), "property": True, "static": "static" in pm.group(1),
                                        "public": "public" in pm.group(1), "line": ln})
        if cur_method is not None:
            meth = cur_method[1]
            for a, b in CALL.findall(code):
                meth["calls"].add(a + "." + b)
            for a, b in GENERIC_CALL.findall(code):
                meth["calls"].add(a + "." + b)
            for a, b in LOCAL_DECL.findall(code):
                meth.setdefault("locals", {})[b] = a
            for b in BARECALL.findall(code):
                if b not in KEYWORDS:
                    meth["calls"].add("." + b)
            for sm in SPREF.finditer(line):
                arg = extract_call_args(line, sm.end())
                meth["keys"].append({"api": sm.group(1) + "." + sm.group(2), "key": arg, "line": ln})
            for s in re.findall(r'"((?:\\.|[^"\\]){2,60})"', line):
                meth["strings"].add(s)
        opens, closes = code.count("{"), code.count("}")
        if pending_type is not None and opens:
            depth += opens - closes
            stack.append((pending_type, depth))
            types.append(pending_type)
            pending_type = None
            continue
        depth += opens - closes
        if cur_method is not None and depth <= cur_method[2] and (closes or stripped.endswith(";")) and ln > cur_method[1]["line"]:
            cur_method[1]["end"] = ln
            cur_method = None
        while stack and depth < stack[-1][1]:
            stack.pop()
    return types


def main():
    src, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    all_types = []
    for root, _, files in os.walk(src):
        for f in files:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(root, f)
            rel = os.path.relpath(p, src)
            name0 = os.path.splitext(f)[0]
            if is_third_party(rel, name0):
                continue
            all_types.extend(parse_file(p, rel))
    known = {t["name"] for t in all_types}
    fieldtype = {}
    for t in all_types:
        for fl in t["fields"]:
            fieldtype[(t["name"], fl["name"])] = re.sub(r"[\[\]<>].*", "", fl["type"].split(".")[-1])

    byname = {}
    for (tn, fn), ft in fieldtype.items():
        byname.setdefault(fn, set()).add(ft)

    def resolve(tname, call, local):
        a, b = call.split(".", 1)
        if a == "":
            return tname + "." + b
        if a in known:
            return a + "." + b
        if a in local and local[a] in known:
            return local[a] + "." + b
        ft = fieldtype.get((tname, a))
        if ft in known:
            return ft + "." + b
        cands = byname.get(a, set()) & known
        if len(cands) == 1:
            return next(iter(cands)) + "." + b
        return None

    keys, choke = {}, {}
    index = []
    for t in all_types:
        meths = []
        for m in t["methods"]:
            resolved = sorted({r for r in (resolve(t["name"], c, m.get("locals", {})) for c in m["calls"]) if r and r.split(".")[0] in known})
            for r in resolved:
                if r in CHOKE:
                    choke.setdefault(r, []).append(f'{t["name"]}.{m["name"]} ({t["file"]}:{m["line"]})')
            for k in m["keys"]:
                rw = "write" if ".Set" in k["api"] or "Delete" in k["api"] else ("read" if ".Get" in k["api"] or "HasKey" in k["api"] else "other")
                e = keys.setdefault(k["key"], {"read": [], "write": [], "other": []})
                e[rw].append(f'{t["name"]}.{m["name"]} ({t["file"]}:{k["line"]})')
            meths.append({"name": m["name"], "ret": m["ret"], "params": m["params"], "line": m["line"], "end": m["end"],
                          "calls": resolved, "save_keys": sorted({k["key"] for k in m["keys"]})})
        index.append({"type": t["name"], "kind": t["kind"], "base": t["base"], "interfaces": t["interfaces"],
                      "file": t["file"], "line": t["line"], "category": categorize(t["name"]),
                      "fields": t["fields"], "methods": meths})
    index.sort(key=lambda x: x["type"])
    meta = {"generator": "tools/research/atlas_index.py", "source": "ilspycmd 11.2.0.10050 -p of Assembly-CSharp.dll (game 1.27c)",
            "evidence": "VERIFIED_IL (decompiled source; brace parsing heuristic)", "type_count": len(index),
            "method_count": sum(len(t["methods"]) for t in index)}
    json.dump({"meta": meta, "types": index}, open(os.path.join(out, "source_index.json"), "w", encoding="utf-8"), indent=0)
    json.dump({"meta": meta, "keys": dict(sorted(keys.items()))}, open(os.path.join(out, "save_keys.json"), "w", encoding="utf-8"), indent=1)
    json.dump({"meta": meta, "choke_callers": {k: sorted(v) for k, v in sorted(choke.items())}},
              open(os.path.join(out, "call_index.json"), "w", encoding="utf-8"), indent=1)
    print(json.dumps(meta))


if __name__ == "__main__":
    main()
