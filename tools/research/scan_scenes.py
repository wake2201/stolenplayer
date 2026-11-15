"""Decode every game-script MonoBehaviour in every scene (level0..level16) into JSON-lines dumps.

Usage: python -I scan_scenes.py <data_dir> <decomp_dir> <out_dir>
Output (keep outside the repo; it is derived game data):
  <out_dir>/<level>.jsonl : {"pid","path","active","enabled","class","fields"|"error"}
  <out_dir>/summary.json  : per scene/class instance counts and decode success
Only classes declared in the global namespace of Assembly-CSharp (game code) are decoded.
"""
import json
import os
import sys
import time
sys.path.insert(0, os.path.dirname(__file__))
from unity_reader import Library, Scene
from layout import Types, decode_behaviour

SCENES = ["MainMenuNew", "MainScene", "HackingPuzzle", "MadisonSt", "PlayerHideout", "Auto Shop", "IntroHouse",
          "Warehouse", "Hillville", "Jail", "BankScene", "EndStory", "Forestside Resort", "HauntedMansion", "Museum",
          "SwatRange", "DrugFarm"]


def main():
    data, decomp, out = sys.argv[1:4]
    os.makedirs(out, exist_ok=True)
    ty = Types(decomp)
    game = {n for n, d in ty.decls.items()}
    lib = Library(data)
    summary = {}
    for i, name in enumerate(SCENES):
        t0 = time.time()
        lv = "level%d" % i
        sc = Scene(lib, lv)
        per = {}
        with open(os.path.join(out, lv + ".jsonl"), "w", encoding="utf-8") as fh:
            for b in sc.behaviours:
                cls = b[3]
                if not cls or "." in cls or cls not in game:
                    continue
                rec = {"pid": b[0], "go": b[1], "path": sc.path(b[1]), "active": sc.active_in_hierarchy(b[1]),
                       "enabled": b[2], "class": cls}
                st = per.setdefault(cls, {"count": 0, "ok": 0, "active": 0})
                st["count"] += 1
                st["active"] += 1 if rec["active"] else 0
                try:
                    rec["fields"] = decode_behaviour(sc.sf, ty, b)
                    st["ok"] += 1
                except Exception as e:  # layout unknown: keep the instance, record the reason
                    rec["error"] = str(e)[:160]
                fh.write(json.dumps(rec, ensure_ascii=False) + "\n")
        summary[lv] = {"scene": name, "classes": dict(sorted(per.items())), "seconds": round(time.time() - t0, 1)}
        print(lv, name, sum(v["count"] for v in per.values()), sum(v["ok"] for v in per.values()), summary[lv]["seconds"], flush=True)
    json.dump(summary, open(os.path.join(out, "summary.json"), "w"), indent=1)


if __name__ == "__main__":
    main()
