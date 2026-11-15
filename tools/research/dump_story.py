"""Dump StoryManager.story_scripts (MainScene) to JSON: index, key, English objective, non-default
fields and resolved references. Usage: python -I dump_story.py <data_dir> <decomp_dir> <loc_en.json> <out.json>"""
import json
import sys
import os
sys.path.insert(0, os.path.dirname(__file__))
from unity_reader import Library, Scene
from layout import Types, decode_behaviour

DEFAULTS = {"item_amount_req": 1, "can_use_PC": True, "second_job_nr": None}


def main():
    data, decomp, locp, outp = sys.argv[1:5]
    loc = json.load(open(locp, encoding="utf-8"))
    ty = Types(decomp)
    lib = Library(data)
    sc = Scene(lib, "level1")
    beh_by_pid = {b[0]: b for b in sc.behaviours}

    def ref(p):
        if not p:
            return None
        fid, pid = p
        if fid != 0:
            return {"file": sc.sf.externals[fid - 1], "pid": pid}
        b = beh_by_pid.get(pid)
        if b:
            return {"script": b[3], "path": sc.path(b[1])}
        if pid in sc.go_name:
            return {"gameobject": sc.path(pid)}
        if pid in sc.tr_go:
            return {"transform": sc.path(sc.tr_go[pid])}
        return {"pid": pid}

    sm = sc.find("StoryManager")[0]
    d = decode_behaviour(sc.sf, ty, sm)
    fields = {f["name"]: f["type"] for f in ty.fields("StoryScript")}
    stages = []
    for i, s in enumerate(d["story_scripts"]):
        e = {"index": i, "key": s["story_description"], "text_en": loc.get(s["story_description"])}
        for k, v in s.items():
            if k == "story_description":
                continue
            t = fields[k]
            if t == "bool" and v:
                e[k] = True
            elif t in ("int", "float") and v not in (0, DEFAULTS.get(k)) and not (k == "item_amount_req" and v == 1):
                e[k] = v
            elif t == "string" and v:
                e[k] = v
            elif t == "string[]" and v:
                e[k] = v
            elif t == "BuyTip[]" and v:
                e[k] = [ref(x) for x in v]
            elif isinstance(v, list) and len(v) == 2 and isinstance(v[1], int) and t not in ("string[]",):
                r = ref(v)
                if r:
                    e[k] = r
        if s.get("second_job_nr", -1) != -1 and s.get("accept_job_on_start"):
            e["second_job_nr"] = s["second_job_nr"]
        stages.append(e)
    # call sequences: resolve text for the first call line
    out = {"source": "level1 (MainScene) MANAGERS/STORY MANAGER StoryManager.story_scripts, byte-exact decode",
           "count": len(stages), "stages": stages}
    json.dump(out, open(outp, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(len(stages))


if __name__ == "__main__":
    main()
