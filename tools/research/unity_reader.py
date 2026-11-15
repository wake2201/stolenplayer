"""Minimal read-only Unity SerializedFile reader (Unity 2020.3, format v22) for game-atlas research.

No third-party packages. Reads object tables, GameObject names, Transform hierarchy paths and the
MonoScript class of every MonoBehaviour. Field decoding of script data is done by layout.py with
layouts derived from the decompiled class declarations; every decode is checked against the
object's exact byte size, so a wrong layout is detected instead of silently producing garbage.

Never writes to the game folder.
"""
import os
import struct


class Reader:
    def __init__(self, data, pos=0, little=True):
        self.b, self.p, self.e = data, pos, "<" if little else ">"

    def u8(self):
        v = self.b[self.p]; self.p += 1; return v

    def boolean(self):
        return self.u8() != 0

    def i16(self):
        v = struct.unpack_from(self.e + "h", self.b, self.p)[0]; self.p += 2; return v

    def i32(self):
        v = struct.unpack_from(self.e + "i", self.b, self.p)[0]; self.p += 4; return v

    def u32(self):
        v = struct.unpack_from(self.e + "I", self.b, self.p)[0]; self.p += 4; return v

    def i64(self):
        v = struct.unpack_from(self.e + "q", self.b, self.p)[0]; self.p += 8; return v

    def f32(self):
        v = struct.unpack_from(self.e + "f", self.b, self.p)[0]; self.p += 4; return v

    def raw(self, n):
        v = self.b[self.p:self.p + n]; self.p += n; return v

    def cstr(self):
        z = self.b.index(b"\0", self.p)
        v = self.b[self.p:z].decode("utf-8", "replace"); self.p = z + 1; return v

    def align(self):
        self.p = (self.p + 3) & ~3

    def string(self):
        n = self.i32()
        if n < 0 or n > 1 << 24:
            raise ValueError("bad string length %d at %d" % (n, self.p - 4))
        v = self.b[self.p:self.p + n].decode("utf-8", "replace"); self.p += n; self.align(); return v

    def pptr(self):
        return (self.i32(), self.i64())


class SerializedFile:
    def __init__(self, path):
        self.path = path
        self.name = os.path.basename(path)
        self.b = open(path, "rb").read()
        h = Reader(self.b, 0, little=False)
        h.u32(); h.u32(); self.version = h.u32(); h.u32()
        if self.version < 22:
            raise ValueError("only format v22 supported (got %d)" % self.version)
        endian = h.u8(); h.raw(3)
        h.u32(); h.i64(); self.data_offset = h.i64(); h.i64()
        r = Reader(self.b, h.p, little=(endian == 0))
        self.unity_version = r.cstr()
        self.platform = r.i32()
        self.type_tree = r.boolean()
        self.types = []
        for _ in range(r.i32()):
            cid = r.i32(); r.boolean(); sidx = r.i16()
            if cid == 114:
                r.raw(16)
            r.raw(16)
            if self.type_tree:
                raise ValueError("type trees present; not supported by this minimal reader")
            self.types.append((cid, sidx))
        self.objects = {}
        for _ in range(r.i32()):
            r.align(); pid = r.i64(); start = r.i64() + self.data_offset; size = r.u32(); tid = r.i32()
            self.objects[pid] = (self.types[tid][0], start, size)
        for _ in range(r.i32()):
            r.i32(); r.align(); r.i64()
        self.externals = []
        for _ in range(r.i32()):
            r.cstr(); r.raw(16); r.i32(); self.externals.append(r.cstr())

    def reader(self, pid):
        cid, start, size = self.objects[pid]
        return Reader(self.b, start), start, size

    def of_class(self, cid):
        return [p for p, o in self.objects.items() if o[0] == cid]


class Library:
    """Opens files lazily from the game's _Data folder and resolves PPtrs across files."""

    def __init__(self, data_dir):
        self.dir = data_dir
        self.files = {}
        self.script_cache = {}

    def open(self, name):
        key = os.path.basename(name).lower()
        if key not in self.files:
            p = os.path.join(self.dir, os.path.basename(name))
            self.files[key] = SerializedFile(p) if os.path.exists(p) else None
        return self.files[key]

    def resolve(self, sf, pptr):
        fid, pid = pptr
        if pid == 0:
            return None, None
        target = sf if fid == 0 else self.open(sf.externals[fid - 1])
        if target is None or pid not in target.objects:
            return target, None
        return target, pid

    def script_name(self, sf, pptr):
        tf, pid = self.resolve(sf, pptr)
        if pid is None:
            return None
        k = (tf.name, pid)
        if k not in self.script_cache:
            r, _, _ = tf.reader(pid)
            name = r.string(); r.i32(); r.raw(16); cls = r.string(); ns = r.string()
            self.script_cache[k] = (ns + "." + cls) if ns else cls
        return self.script_cache[k]


class Scene:
    """GameObjects, transform paths and MonoBehaviours (with script class) of one serialized file."""

    def __init__(self, lib, name):
        self.lib, self.sf = lib, lib.open(name)
        sf = self.sf
        self.go_name, self.go_active, self.go_comps = {}, {}, {}
        for pid in sf.of_class(1):
            r, _, _ = sf.reader(pid)
            comps = [r.pptr() for _ in range(r.i32())]
            r.i32()  # layer
            self.go_name[pid] = r.string()
            r.i16()  # tag
            self.go_active[pid] = r.boolean()
            self.go_comps[pid] = comps
        self.tr_go, self.tr_father, self.go_tr = {}, {}, {}
        for cid in (4, 224):  # Transform, RectTransform
            for pid in sf.of_class(cid):
                r, _, _ = sf.reader(pid)
                go = r.pptr()[1]
                r.raw(16 + 12 + 12)
                kids = [r.pptr() for _ in range(r.i32())]
                father = r.pptr()[1]
                self.tr_go[pid], self.tr_father[pid], self.go_tr[go] = go, father, pid
        self.behaviours = []  # (pid, go_pid, enabled, script, start, size)
        for pid in sf.of_class(114):
            r, start, size = sf.reader(pid)
            go = r.pptr()[1]; en = r.u8(); r.align(); scr = r.pptr()
            r.string()  # m_Name
            self.behaviours.append((pid, go, en != 0, lib.script_name(sf, scr), r.p, start + size))

    def path(self, go):
        parts, tr, guard = [], self.go_tr.get(go), 0
        while tr and guard < 64:
            parts.append(self.go_name.get(self.tr_go.get(tr), "?"))
            tr = self.tr_father.get(tr); guard += 1
        return "/".join(reversed(parts))

    def active_in_hierarchy(self, go):
        tr, guard = self.go_tr.get(go), 0
        while tr and guard < 64:
            if not self.go_active.get(self.tr_go.get(tr), True):
                return False
            tr = self.tr_father.get(tr); guard += 1
        return True

    def find(self, script):
        return [b for b in self.behaviours if b[3] == script]
