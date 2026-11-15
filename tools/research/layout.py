"""Unity script-field layout decoder for game-atlas research.

Serialized layouts are derived from the decompiled class declarations (ILSpy project output):
field order = declaration order (base class first); public or [SerializeField] instance fields of
serializable types only. decode_behaviour() raises LayoutError when the bytes consumed differ from
the object size, so every successful decode is byte-exact (VERIFIED_ASSET).
"""
import os
import re

PRIM = {"bool": ("b", 1), "byte": ("b", 1), "sbyte": ("b", 1), "short": ("h", 2), "ushort": ("h", 2), "char": ("h", 2),
        "int": ("i", 4), "uint": ("i", 4), "float": ("f", 4), "long": ("q", 8), "ulong": ("q", 8), "double": ("d", 8)}
STRUCTS = {"Vector2": 2, "Vector3": 3, "Vector4": 4, "Quaternion": 4, "Color": 4, "Rect": 4, "Bounds": 6,
           "Vector2Int": 2, "Vector3Int": 3, "Matrix4x4": 16}
NOT_SERIALIZED = {"Dictionary", "HashSet", "Queue", "Stack", "Action", "Func", "IEnumerator", "Coroutine", "object",
                  "Delegate", "Hashtable", "ArrayList", "Thread", "Mesh[,]", "Type", "Task", "decimal", "DateTime", "TimeSpan",
                  "Tuple", "WaitForSeconds", "Ray", "RaycastHit", "NavMeshPath", "Collision"}
UNITY_SERIALIZABLE_CLASSES = {"AnimationCurve", "Gradient", "LayerMask", "Color32", "UnityEvent", "RectOffset", "GUIStyle"}
DECL = re.compile(r'^(\s*)(?:public|internal|private|protected)?\s*(?:static\s+|sealed\s+|abstract\s+|partial\s+)*(class|struct|enum)\s+(\w+)(?:<[^>]*>)?\s*(?::\s*([^{]+))?$')
FIELD = re.compile(r'^\s*((?:public|private|protected|internal|static|readonly|const|new|volatile)\s+)+([\w<>\[\],\.\s]+?)\s+(\w+)\s*(?:=.*)?;\s*$')


class LayoutError(Exception):
    pass


class Types:
    def __init__(self, decomp_dir):
        self.decls = {}
        for root, _, files in os.walk(decomp_dir):
            for f in files:
                if f.endswith(".cs"):
                    self._parse(os.path.join(root, f))

    def _parse(self, path):
        lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
        stack, depth, attrs, pending = [], 0, [], None
        for line in lines:
            code = re.sub(r'"(?:\\.|[^"\\])*"', '""', line)
            st = code.strip()
            if st.startswith("[") and st.endswith("]"):
                attrs.append(st)
                continue
            m = DECL.match(code) if pending is None else None
            if m:
                bases = [b.strip() for b in (m.group(4) or "").split(",") if b.strip()]
                pending = {"name": m.group(3), "kind": m.group(2), "base": bases[0] if bases else None,
                           "serializable": any("Serializable" in a for a in attrs), "fields": [], "enum_size": 4}
                if m.group(2) == "enum" and bases and bases[0] in ("byte", "sbyte"):
                    pending["enum_size"] = 4  # Unity serializes enums as int-sized
                attrs = []
            elif stack and depth == stack[-1][1] and pending is None:
                fm = FIELD.match(code)
                if fm and "(" not in st.split("=")[0]:
                    mods = fm.group(1)
                    stack[-1][0]["fields"].append({"name": fm.group(3), "type": " ".join(fm.group(2).split()),
                                                   "public": "public" in mods, "static": "static" in mods or "const" in mods,
                                                   "readonly": "readonly" in mods,
                                                   "serialize_field": any("SerializeField" in a for a in attrs),
                                                   "non_serialized": any("NonSerialized" in a for a in attrs)})
                attrs = [] if st and not st.startswith("[") else attrs
            elif st:
                attrs = []
            o, c = code.count("{"), code.count("}")
            if pending is not None and o:
                depth += o - c
                stack.append((pending, depth))
                self.decls.setdefault(pending["name"], pending)
                pending = None
                continue
            depth += o - c
            while stack and depth < stack[-1][1]:
                stack.pop()

    def is_object(self, name):
        """UnityEngine.Object-derived (serialized as PPtr)."""
        seen = 0
        n = name
        while n and seen < 20:
            if n in ("MonoBehaviour", "ScriptableObject", "Object", "UnityEngine.Object", "Component", "Behaviour", "NetworkBehaviour"):
                return True
            d = self.decls.get(n)
            if d is None:
                # Unknown non-game type: UnityEngine component/asset types are Objects; treat as Object
                # unless it is a known struct/serializable class.
                return n not in STRUCTS and n not in UNITY_SERIALIZABLE_CLASSES and n not in PRIM and n != "string"
            if d["kind"] != "class":
                return False
            n = d["base"]
            seen += 1
        return False

    def fields(self, name):
        d = self.decls.get(name)
        if d is None:
            raise LayoutError("unknown type " + name)
        out = []
        if d["base"] and d["base"] in self.decls and self.decls[d["base"]]["kind"] == "class":
            out.extend(self.fields(d["base"]))
        for f in d["fields"]:
            if f["static"] or f["readonly"] or f["non_serialized"]:
                continue
            if not (f["public"] or f["serialize_field"]):
                continue
            if not self.serializable_type(f["type"]):
                continue
            out.append(f)
        return out

    def serializable_type(self, t):
        t = t.replace("UnityEngine.", "").replace("System.Collections.Generic.", "")
        base = re.sub(r"<.*", "", t).replace("[]", "")
        if base in NOT_SERIALIZED or "[,]" in t or t.count("[]") > 1:
            return False
        if t.startswith("List<"):
            inner = t[5:-1]
            return "[]" not in inner and not inner.startswith("List<") and self.serializable_type(inner)
        if t.endswith("[]"):
            return not t[:-2].startswith("List<") and self.serializable_type(t[:-2])
        if base in PRIM or base == "string" or base in STRUCTS or base in UNITY_SERIALIZABLE_CLASSES:
            return True
        if base.startswith("UnityEvent"):
            return True
        d = self.decls.get(base)
        if d is not None:
            if d["kind"] == "enum":
                return True
            if self.is_object(base):
                return True
            return d["serializable"] and "<" not in t
        return self.is_object(base)


def read_value(r, ty, t, lib=None, sf=None, depth=0):
    t = t.replace("UnityEngine.", "").replace("System.Collections.Generic.", "")
    if depth > 12:
        raise LayoutError("depth")
    if t.startswith("List<") or t.endswith("[]"):
        inner = t[5:-1] if t.startswith("List<") else t[:-2]
        n = r.i32()
        if n < 0 or n > 200000:
            raise LayoutError("bad array length %d (%s)" % (n, t))
        if inner in ("byte", "bool", "sbyte"):
            v = list(r.raw(n)); r.align(); return v
        v = [read_value(r, ty, inner, lib, sf, depth + 1) for _ in range(n)]
        if inner in ("short", "ushort", "char"):
            r.align()
        return v
    if t in PRIM:
        k, sz = PRIM[t]
        if sz == 1:
            v = r.u8(); r.align(); return bool(v) if t == "bool" else v
        if sz == 2:
            v = r.i16(); r.align(); return v
        if k == "f":
            return round(r.f32(), 4)
        if sz == 8:
            return r.i64()
        return r.i32()
    if t == "string":
        return r.string()
    if t in STRUCTS:
        return [round(r.f32(), 3) for _ in range(STRUCTS[t])]
    if t == "LayerMask" or t == "Color32":
        return r.u32()
    if t == "AnimationCurve":
        n = r.i32(); r.raw(28 * n); r.i32(); r.i32(); r.i32(); return {"keys": n}
    if t == "Gradient":
        r.raw(128 + 16 + 16 + 4 + 2); r.align(); return "gradient"
    if t == "RectOffset":
        return [r.i32() for _ in range(4)]
    if t.startswith("UnityEvent"):
        calls = []
        for _ in range(r.i32()):
            tgt = r.pptr(); asm = r.string(); meth = r.string(); mode = r.i32()
            oa = r.pptr(); oat = r.string(); ia = r.i32(); fa = round(r.f32(), 3); sa = r.string(); ba = r.u8(); r.align()
            state = r.i32()
            c = {"target": list(tgt), "type": asm.split(",")[0], "method": meth, "mode": mode}
            if mode == 2: c["arg_obj"] = list(oa)
            if mode == 3: c["arg_int"] = ia
            if mode == 4: c["arg_float"] = fa
            if mode == 5: c["arg_string"] = sa
            if mode == 6: c["arg_bool"] = bool(ba)
            calls.append(c)
        return {"calls": calls}
    base = re.sub(r"<.*", "", t)
    d = ty.decls.get(base)
    if d is not None and d["kind"] == "enum":
        return r.i32()
    if ty.is_object(base):
        p = r.pptr()
        return None if p[1] == 0 else list(p)
    if d is None:
        raise LayoutError("cannot decode type " + t)
    out = {}
    for f in ty.fields(base):
        out[f["name"]] = read_value(r, ty, f["type"], lib, sf, depth + 1)
    return out


def decode_behaviour(sf, ty, beh, script=None):
    """beh = Scene.behaviours tuple; returns dict of script fields (byte-exact) or raises LayoutError."""
    from unity_reader import Reader
    pid, go, en, scr, pos, end = beh
    r = Reader(sf.b, pos)
    out = {}
    for f in ty.fields(script or scr):
        out[f["name"]] = read_value(r, ty, f["type"])
    if r.p != end:
        raise LayoutError("size mismatch for %s: consumed %d, object ends %d (diff %d)" % (scr, r.p, end, end - r.p))
    return out
