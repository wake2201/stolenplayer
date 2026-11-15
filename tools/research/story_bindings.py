"""Find every serialized reference to a story stage key across all decoded scenes.

Usage: python -I story_bindings.py <scenes_dir> <story_raw.json> <out.json>
A binding = a decoded script instance with a string field equal to a StoryManager stage key
(story_description). Flags with 'advance'/'story' in their name are reported alongside.
"""
import glob
import json
import os
import re
import sys


def walk(v, prefix=""):
    if isinstance(v, dict):
        for k, x in v.items():
            yield from walk(x, prefix + "." + k if prefix else k)
    elif isinstance(v, list) and v and not (len(v) == 2 and all(isinstance(i, int) for i in v)):
        for i, x in enumerate(v):
            yield from walk(x, "%s[%d]" % (prefix, i))
    else:
        yield prefix, v


def main():
    scenes, storyp, outp = sys.argv[1:4]
    stages = json.load(open(storyp, encoding="utf-8"))["stages"]
    key_index = {s["key"]: s["index"] for s in stages}
    out = []
    for f in sorted(glob.glob(os.path.join(scenes, "level*.jsonl")), key=lambda p: int(re.findall(r"\d+", os.path.basename(p))[0])):
        lv = os.path.basename(f)[:-6]
        for line in open(f, encoding="utf-8"):
            r = json.loads(line)
            if "fields" not in r:
                continue
            flat = list(walk(r["fields"]))
            hits = [(p, v) for p, v in flat if isinstance(v, str) and v in key_index]
            nums = [(p, v) for p, v in flat if re.search(r"(^|\.)(storyNr|story_nr|storyNumber|requiredStory|story_req)$", p) and isinstance(v, int)]
            if not hits and not nums:
                continue
            flags = {p: v for p, v in flat if isinstance(v, bool) and v and re.search(r"advance|story|Story", p)}
            for p, v in hits:
                out.append({"scene": lv, "class": r["class"], "path": r["path"], "active": r["active"], "field": p,
                            "key": v, "stage": key_index[v], "flags": flags})
            for p, v in nums:
                out.append({"scene": lv, "class": r["class"], "path": r["path"], "active": r["active"], "field": p,
                            "story_number": v, "flags": flags})
    json.dump(out, open(outp, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(len(out))


if __name__ == "__main__":
    main()
