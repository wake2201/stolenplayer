"""Print selected method bodies from the scratch decompilation using source_index.json line ranges.
Usage: python -I show.py <decomp_dir> Type.Method [Type.Method ...] [--max N]
Blank lines are dropped; each body is capped at N lines (default 60)."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
INDEX = os.path.join(HERE, "..", "..", "docs", "game-atlas", "data", "source_index.json")


def main():
    args = sys.argv[1:]
    mx = 60
    if "--max" in args:
        i = args.index("--max"); mx = int(args[i + 1]); del args[i:i + 2]
    decomp, targets = args[0], args[1:]
    idx = {t["type"]: t for t in json.load(open(INDEX))["types"]}
    for tg in targets:
        tn, mn = tg.rsplit(".", 1)
        t = idx.get(tn)
        if not t:
            print("## no type", tn); continue
        lines = open(os.path.join(decomp, t["file"]), encoding="utf-8", errors="replace").read().splitlines()
        for m in t["methods"]:
            if m["name"] == mn:
                body = [l.rstrip() for l in lines[m["line"] - 1:(m["end"] or m["line"] + mx)] if l.strip()]
                print("## %s.%s  (%s:%d)" % (tn, mn, t["file"], m["line"]))
                for l in body[:mx]:
                    print(l.replace("\t", " "))
                if len(body) > mx:
                    print("   ... (%d more lines)" % (len(body) - mx))


if __name__ == "__main__":
    main()
