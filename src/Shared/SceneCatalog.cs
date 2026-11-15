// W4.2D Stage A: trusted host loot catalogs WITHOUT loading a scene. The host reads its OWN installed scene files (read-only,
// Unity SerializedFile "levelN") and rebuilds exactly what the in-game census (src/Plugin/WorldCensus.cs) produces at
// SceneManager.sceneLoaded: the same hierarchy paths (sibling index only for shared names, '@k' occurrences), the same
// FNV-1a identities per kind (ObjectKinds.All, subclasses included like GetComponentsInChildren), the same ambiguity rules,
// the same identity digest, and the same LootCatalog (name, item_ID, loot flags, add_money). The host therefore validates a
// Player-2 pickup in a region it never loaded, against data it derived itself - never against client-supplied metadata.
// Self-check at runtime: a client request carries its own census digest; it is accepted only when it equals the digest the
// host computed from its files (a wrong reader or a modified scene refuses, never grants).
//
// Unity-free: the serialized MonoBehaviour layout is derived by reflection from the game's own script types (Unity 2020.3
// rules: public or [SerializeField], not static/readonly/[NonSerialized]; primitives < 4 bytes aligned; strings and arrays
// aligned; UnityEngine.Object = PPtr 12 B; built-in structs; [Serializable] classes inline). Every parsed MonoBehaviour must
// consume EXACTLY its serialized byte size, otherwise the scene is rejected (fail closed).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace TS2MP.Shared
{
    public struct PPtr { public int FileId; public long PathId; public bool IsNull { get { return FileId == 0 && PathId == 0; } } }

    // Minimal reader for Unity SerializedFile versions 17..22 (2020.3 = 21/22), type trees stripped or present.
    public sealed class SerializedAssetFile : IDisposable
    {
        public struct ObjInfo { public long PathId; public long Start; public uint Size; public int ClassId; public int ScriptIndex; }
        public readonly string Path;
        public uint Version; public bool BigEndianData; public long DataOffset; public string UnityVersion;
        public readonly List<ObjInfo> Objects = new List<ObjInfo>();
        public readonly Dictionary<long, int> ByPath = new Dictionary<long, int>();
        public readonly List<string> Externals = new List<string>();
        readonly FileStream _fs; readonly BinaryReader _r;

        public SerializedAssetFile(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _r = new BinaryReader(_fs, Encoding.UTF8);
            ReadMetadata();
        }

        uint BE32() { var b = _r.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        long BE64() { long hi = BE32(); long lo = BE32(); return hi << 32 | lo; }
        void Align4() { long p = _fs.Position; long a = (p + 3) & ~3L; if (a != p) _fs.Position = a; }
        string CStr() { var sb = new List<byte>(); int c; while ((c = _fs.ReadByte()) > 0) sb.Add((byte)c); return Encoding.UTF8.GetString(sb.ToArray()); }

        void ReadMetadata()
        {
            BE32(); BE32(); Version = BE32(); DataOffset = BE32();
            if (Version < 17 || Version > 22) throw new InvalidDataException("unsupported SerializedFile version " + Version);
            int endian = _fs.ReadByte(); _fs.Position += 3;
            if (Version >= 22) { BE32(); BE64(); DataOffset = BE64(); BE64(); }
            if (endian != 0) throw new InvalidDataException("big-endian metadata not supported");
            UnityVersion = CStr();
            _r.ReadInt32();                         // target platform
            bool typeTree = _r.ReadByte() != 0;
            int types = _r.ReadInt32();
            var classOf = new int[types]; var scriptOf = new int[types];
            for (int i = 0; i < types; i++)
            {
                classOf[i] = _r.ReadInt32();
                _r.ReadByte();                          // stripped
                scriptOf[i] = _r.ReadInt16();
                if (classOf[i] == 114) _fs.Position += 16;   // script id
                _fs.Position += 16;                     // old type hash
                if (typeTree) SkipTypeTree();
            }
            int objects = _r.ReadInt32();
            for (int i = 0; i < objects; i++)
            {
                Align4();
                long pid = _r.ReadInt64();
                long start = Version >= 22 ? _r.ReadInt64() : _r.ReadUInt32();
                uint size = _r.ReadUInt32(); int t = _r.ReadInt32();
                ByPath[pid] = Objects.Count;
                Objects.Add(new ObjInfo { PathId = pid, Start = start, Size = size, ClassId = classOf[t], ScriptIndex = scriptOf[t] });
            }
            int scripts = _r.ReadInt32();
            for (int i = 0; i < scripts; i++) { _r.ReadInt32(); Align4(); _r.ReadInt64(); }
            int ext = _r.ReadInt32();
            for (int i = 0; i < ext; i++) { CStr(); _fs.Position += 20; Externals.Add(CStr()); }
        }

        void SkipTypeTree()
        {
            int nodes = _r.ReadInt32(), strings = _r.ReadInt32();
            _fs.Position += nodes * (Version >= 19 ? 32 : 24) + strings;
            if (Version >= 21) { int deps = _r.ReadInt32(); _fs.Position += deps * 4; }
        }

        // Positions the reader at an object's data (little-endian) and returns it.
        public BinaryReader At(ObjInfo o) { _fs.Position = DataOffset + o.Start; return _r; }
        public long Position { get { return _fs.Position; } }
        public void Dispose() { _r.Close(); }
    }

    public static class AssetRead
    {
        public static void Align4(BinaryReader r) { var s = r.BaseStream; long p = s.Position; long a = (p + 3) & ~3L; if (a != p) s.Position = a; }
        public static PPtr Ptr(BinaryReader r) { return new PPtr { FileId = r.ReadInt32(), PathId = r.ReadInt64() }; }
        public static string Str(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 1 << 20) throw new InvalidDataException("string length " + n);
            var s = Encoding.UTF8.GetString(r.ReadBytes(n)); Align4(r); return s;
        }
    }

    // Serialized layout of a script type (Unity 2020.3 rules, see file header). Reads named fields of interest, skips the rest.
    public sealed class ScriptLayout
    {
        sealed class F { public string Name; public Type T; }
        readonly List<F> _fields = new List<F>();
        public readonly Type Type;
        static readonly Dictionary<Type, ScriptLayout> _cache = new Dictionary<Type, ScriptLayout>();

        public static ScriptLayout For(Type t) { lock (_cache) { ScriptLayout l; if (!_cache.TryGetValue(t, out l)) _cache[t] = l = new ScriptLayout(t); return l; } }

        ScriptLayout(Type t)
        {
            Type = t;
            var chain = new List<Type>();
            for (var x = t; x != null && !IsUnityBase(x); x = x.BaseType) chain.Insert(0, x);
            foreach (var c in chain)
                foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (Serialized(f)) _fields.Add(new F { Name = f.Name, T = f.FieldType });
        }

        static bool IsUnityBase(Type x)
        {
            string n = x.FullName;
            return n == "UnityEngine.MonoBehaviour" || n == "UnityEngine.ScriptableObject" || n == "UnityEngine.Behaviour" || n == "UnityEngine.Component" || n == "UnityEngine.Object" || n == "System.Object";
        }

        static bool HasAttr(MemberInfo m, string name)
        {
            foreach (var a in m.GetCustomAttributesData()) if (a.AttributeType.Name == name) return true;
            return false;
        }

        static bool Serialized(FieldInfo f)
        {
            if (f.IsStatic || f.IsInitOnly || f.IsLiteral || f.IsNotSerialized) return false;
            if (!f.IsPublic && !HasAttr(f, "SerializeField")) return false;
            if (HasAttr(f, "SerializeReference")) throw new NotSupportedException("SerializeReference field " + f.DeclaringType.Name + "." + f.Name);
            return Serializable(f.FieldType, 0);
        }

        public static bool IsUnityObject(Type t) { for (var x = t; x != null; x = x.BaseType) if (x.FullName == "UnityEngine.Object") return true; return false; }

        static readonly Dictionary<string, int> Builtin = new Dictionary<string, int>
        {
            { "UnityEngine.Vector2", 8 }, { "UnityEngine.Vector3", 12 }, { "UnityEngine.Vector4", 16 }, { "UnityEngine.Quaternion", 16 },
            { "UnityEngine.Color", 16 }, { "UnityEngine.Color32", 4 }, { "UnityEngine.Rect", 16 }, { "UnityEngine.Bounds", 24 },
            { "UnityEngine.LayerMask", 4 }, { "UnityEngine.Matrix4x4", 64 }, { "UnityEngine.Vector2Int", 8 }, { "UnityEngine.Vector3Int", 12 },
            { "UnityEngine.RectInt", 16 }, { "UnityEngine.BoundsInt", 24 }, { "UnityEngine.RectOffset", 16 }
        };

        static Type Element(Type t)
        {
            if (t.IsArray) return t.GetArrayRank() == 1 ? t.GetElementType() : null;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return t.GetGenericArguments()[0];
            return null;
        }

        static bool Serializable(Type t, int depth)
        {
            if (depth > 7) return false;
            if (t.IsPrimitive) return t != typeof(IntPtr) && t != typeof(UIntPtr);
            if (t == typeof(string) || t.IsEnum) return true;
            if (IsUnityObject(t)) return true;
            if (t.FullName != null && (Builtin.ContainsKey(t.FullName) || t.FullName == "UnityEngine.AnimationCurve" || t.FullName == "UnityEngine.Gradient")) return true;
            var el = Element(t);
            if (el != null) return Element(el) == null && Serializable(el, depth + 1);
            if (t.IsArray || t.IsInterface || t.IsAbstract || t.IsPointer || typeof(Delegate).IsAssignableFrom(t)) return false;
            if (t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal)) return false;
            if (t.IsGenericTypeDefinition) return false;
            if ((t.Attributes & TypeAttributes.Serializable) == 0) return false;
            if (t.FullName != null && (t.FullName.StartsWith("UnityEngine.", StringComparison.Ordinal) && t.FullName.Contains("GUIStyle"))) throw new NotSupportedException("GUIStyle field");
            return true;
        }

        // Reads every field; values of the requested names (primitives, strings, PPtrs, PPtr lists) go into 'into'.
        public void Read(BinaryReader r, ICollection<string> want, Dictionary<string, object> into)
        {
            foreach (var f in _fields) Value(r, f.T, want != null && want.Contains(f.Name) ? f.Name : null, into, 0);
        }

        static void Value(BinaryReader r, Type t, string keep, Dictionary<string, object> into, int depth)
        {
            if (t.IsEnum) t = Enum.GetUnderlyingType(t);
            if (t.IsPrimitive) { object v = Prim(r, t); if (keep != null) into[keep] = v; if (Size(t) < 4) AssetRead.Align4(r); return; }
            if (t == typeof(string)) { var s = AssetRead.Str(r); if (keep != null) into[keep] = s; return; }
            if (IsUnityObject(t)) { var p = AssetRead.Ptr(r); if (keep != null) into[keep] = p; return; }
            string fn = t.FullName ?? "";
            int bs;
            if (Builtin.TryGetValue(fn, out bs)) { r.BaseStream.Position += bs; return; }
            if (fn == "UnityEngine.AnimationCurve")
            {
                int n = r.ReadInt32(); if (n < 0 || n > 100000) throw new InvalidDataException("curve keys " + n);
                r.BaseStream.Position += n * 28L + 12; return;
            }
            if (fn == "UnityEngine.Gradient") { r.BaseStream.Position += 168; return; }
            var el = Element(t);
            if (el != null)
            {
                int n = r.ReadInt32(); if (n < 0 || n > 1 << 20) throw new InvalidDataException("array length " + n);
                var e2 = el.IsEnum ? Enum.GetUnderlyingType(el) : el;
                List<PPtr> ptrs = keep != null && IsUnityObject(el) ? new List<PPtr>(n) : null;
                for (int i = 0; i < n; i++)
                {
                    if (e2.IsPrimitive) Prim(r, e2);                         // packed elements
                    else if (ptrs != null) ptrs.Add(AssetRead.Ptr(r));
                    else Value(r, el, null, null, depth + 1);
                }
                AssetRead.Align4(r);
                if (ptrs != null) into[keep] = ptrs;
                return;
            }
            // [Serializable] class / struct: its own serialized fields, same rules
            var inner = For(t);
            foreach (var f in inner._fields) Value(r, f.T, null, null, depth + 1);
        }

        static int Size(Type t)
        {
            if (t == typeof(bool) || t == typeof(byte) || t == typeof(sbyte)) return 1;
            if (t == typeof(short) || t == typeof(ushort) || t == typeof(char)) return 2;
            if (t == typeof(long) || t == typeof(ulong) || t == typeof(double)) return 8;
            return 4;
        }

        static object Prim(BinaryReader r, Type t)
        {
            switch (Size(t))
            {
                case 1: { byte b = r.ReadByte(); return t == typeof(bool) ? (object)(b != 0) : b; }
                case 2: return r.ReadInt16();
                case 8: return r.ReadInt64();
                default: return t == typeof(float) ? (object)r.ReadSingle() : r.ReadInt32();
            }
        }
    }

    public sealed class SceneCatalogResult
    {
        public string Scene; public int Namespace;
        public IdentityTable Table; public LootCatalog Loot;
        public int Transforms, Behaviours, Parsed, MissingScripts, LootSharedNames, LootNames;
        public string Error;   // non-null = unusable (fail closed)
        public uint Digest { get { return Table != null ? Table.Digest : 0; } }
    }

    // Builds the census-equivalent identity table + loot catalog of one scene file.
    public static class SceneCatalogBuilder
    {
        // Scene build order from the game's own BuildSettings (globalgamemanagers, class 141, first field m_Scenes =
        // vector<string> of "Assets/.../<Scene>.unity"): scene i is stored in "level<i>". Returns plain scene names.
        public static List<string> BuildScenes(string dataDir)
        {
            using (var f = new SerializedAssetFile(Path.Combine(dataDir, "globalgamemanagers")))
                foreach (var o in f.Objects)
                {
                    if (o.ClassId != 141) continue;
                    var r = f.At(o); int n = r.ReadInt32();
                    if (n <= 0 || n > 512) throw new InvalidDataException("BuildSettings scene count " + n);
                    var l = new List<string>(n);
                    for (int i = 0; i < n; i++)
                    {
                        string s = AssetRead.Str(r);
                        if (!s.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("BuildSettings entry '" + s + "'");
                        l.Add(System.IO.Path.GetFileNameWithoutExtension(s));
                    }
                    return l;
                }
            throw new InvalidDataException("no BuildSettings in globalgamemanagers");
        }

        // Every scene of the build that has a census namespace (region scenes + MainScene), in build order.
        public static List<SceneCatalogResult> BuildAll(string dataDir, Func<string, Type> typeByName, Action<SceneCatalogResult> done)
        {
            var all = new List<SceneCatalogResult>();
            var scenes = BuildScenes(dataDir);
            for (int i = 0; i < scenes.Count; i++)
            {
                int ns = IdentityNamespace.ForScene(scenes[i]);
                if (ns == IdentityNamespace.None) continue;
                string file = System.IO.Path.Combine(dataDir, "level" + i);
                var r = File.Exists(file) ? Build(file, scenes[i], ns, typeByName, dataDir) : new SceneCatalogResult { Scene = scenes[i], Namespace = ns, Error = "level" + i + " missing" };
                all.Add(r);
                if (done != null) done(r);
            }
            return all;
        }
        // Every ObjectKinds.All kind type, resolved by the caller (plugin: AccessTools.TypeByName; tests: reflection).
        public static SceneCatalogResult Build(string levelPath, string sceneName, int ns, Func<string, Type> typeByName, string dataDir)
        {
            var res = new SceneCatalogResult { Scene = sceneName, Namespace = ns };
            try { BuildCore(levelPath, sceneName, ns, typeByName, dataDir, res); }
            catch (Exception ex) { res.Error = ex.GetType().Name + ": " + ex.Message; res.Table = null; res.Loot = null; }
            return res;
        }

        sealed class Go { public string Name; public List<PPtr> Components; }
        sealed class Tr { public long Go; public List<long> Children = new List<long>(); public long Father; public int Sibling; public string PathCache; }

        static readonly string[] PickWant = { "item_ID", "is_heavy_item", "is_money", "add_money", "destroy_if_was_picked", "deleteIfPref", "lock_parcel_after_pickup",
            "advance_story_if_picked", "advance_story_if_placed_car", "isContractItem", "never_destroy", "dontDestroyDays", "cant_pick_reason", "story_name", "add_any_XP", "wasDropped", "parcel_pref" };

        static void BuildCore(string levelPath, string sceneName, int ns, Func<string, Type> typeByName, string dataDir, SceneCatalogResult res)
        {
            var kindTypes = new Type[ObjectKinds.All.Length];
            for (int i = 0; i < kindTypes.Length; i++) kindTypes[i] = typeByName(ObjectKinds.All[i].TypeName);
            var pickT = typeByName("Pickupable"); var cabT = typeByName("RandomFileCabinet");
            if (pickT == null) throw new InvalidDataException("Pickupable type not found");
            var scriptFiles = new Dictionary<string, Dictionary<long, Type>>(StringComparer.OrdinalIgnoreCase);
            using (var f = new SerializedAssetFile(levelPath))
            {
                var gos = new Dictionary<long, Go>(); var trs = new Dictionary<long, Tr>(); var trOfGo = new Dictionary<long, long>();
                var behaviours = new List<SerializedAssetFile.ObjInfo>();
                foreach (var o in f.Objects)
                {
                    if (o.ClassId == 1)
                    {
                        var r = f.At(o); int n = r.ReadInt32(); if (n < 0 || n > 4096) throw new InvalidDataException("component count");
                        var comps = new List<PPtr>(n); for (int i = 0; i < n; i++) comps.Add(AssetRead.Ptr(r));
                        r.ReadUInt32(); gos[o.PathId] = new Go { Name = AssetRead.Str(r), Components = comps };
                    }
                    else if (o.ClassId == 4 || o.ClassId == 224)
                    {
                        var r = f.At(o); var t = new Tr(); var go = AssetRead.Ptr(r); t.Go = go.PathId;
                        r.BaseStream.Position += 40; int n = r.ReadInt32(); if (n < 0 || n > 1 << 16) throw new InvalidDataException("child count");
                        for (int i = 0; i < n; i++) t.Children.Add(AssetRead.Ptr(r).PathId);
                        t.Father = AssetRead.Ptr(r).PathId;
                        trs[o.PathId] = t; trOfGo[t.Go] = o.PathId;
                    }
                    else if (o.ClassId == 114) behaviours.Add(o);
                }
                res.Transforms = trs.Count; res.Behaviours = behaviours.Count;
                foreach (var kv in trs) for (int i = 0; i < kv.Value.Children.Count; i++) { Tr c; if (trs.TryGetValue(kv.Value.Children[i], out c)) c.Sibling = i; }
                var roots = new List<long>();
                foreach (var o in f.Objects) if ((o.ClassId == 4 || o.ClassId == 224) && trs[o.PathId].Father == 0) roots.Add(o.PathId);
                for (int i = 0; i < roots.Count; i++) trs[roots[i]].Sibling = i;

                // MonoBehaviour -> script type
                var compType = new Dictionary<long, Type>(); var compInfo = new Dictionary<long, SerializedAssetFile.ObjInfo>();
                foreach (var o in behaviours)
                {
                    var r = f.At(o); AssetRead.Ptr(r); r.ReadByte(); AssetRead.Align4(r); var sp = AssetRead.Ptr(r);
                    var st = ScriptType(f, sp, scriptFiles, dataDir, typeByName);
                    if (st == null) { res.MissingScripts++; continue; }
                    compType[o.PathId] = st; compInfo[o.PathId] = o;
                }

                // sibling-name sets
                Func<long, string> nameOf = tp => { Go g; return gos.TryGetValue(trs[tp].Go, out g) ? g.Name : ""; };
                var rootNames = new SiblingNames(); foreach (var rt in roots) rootNames.Add(nameOf(rt));
                var sibSets = new Dictionary<long, SiblingNames>();
                Func<long, string> pathOf = null;
                pathOf = tp =>
                {
                    var t = trs[tp]; if (t.PathCache != null) return t.PathCache;
                    string name = nameOf(tp);
                    if (t.Father == 0) t.PathCache = IdentityPath.Segment(name, t.Sibling, rootNames.Shared(name));
                    else
                    {
                        SiblingNames s;
                        if (!sibSets.TryGetValue(t.Father, out s)) { s = new SiblingNames(); foreach (var c in trs[t.Father].Children) if (trs.ContainsKey(c)) s.Add(nameOf(c)); sibSets[t.Father] = s; }
                        t.PathCache = IdentityPath.Join(pathOf(t.Father), IdentityPath.Segment(name, t.Sibling, s.Shared(name)));
                    }
                    return t.PathCache;
                };

                // RandomFileCabinet marks (its own transform + every referenced GameObject)
                var cabinet = new HashSet<long>();
                var cabSlot = new Dictionary<long, KeyValuePair<uint, int>>();   // transform -> (slot group, outcome)
                var cabWant = new HashSet<string>();
                if (cabT != null) foreach (var fi in cabT.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) cabWant.Add(fi.Name);
                var table = new IdentityTable(ns); var loot = new NameCensus(); var cat = new LootCatalog(ns);
                var occ = new Dictionary<long, int[]>();   // transform -> per-kind occurrence counters (census: instance id << 8 | kind)
                var pickVals = new Dictionary<long, Dictionary<string, object>>();
                if (cabT != null)
                    foreach (var kv in compType)
                    {
                        if (!(kv.Value == cabT || kv.Value.IsSubclassOf(cabT))) continue;
                        var vals = ParseBehaviour(f, compInfo[kv.Key], kv.Value, cabWant); res.Parsed++;
                        long cabTr = Mark(cabinet, trOfGo, gos, compInfo[kv.Key], f);
                        if (cabTr != 0) Slots(cabSlot, trOfGo, vals, pathOf(cabTr));
                        foreach (var v in vals.Values)
                        {
                            if (v is PPtr) MarkGo(cabinet, trOfGo, (PPtr)v);
                            var l = v as List<PPtr>; if (l != null) foreach (var p in l) MarkGo(cabinet, trOfGo, p);
                        }
                    }

                // census order: per kind, per root, pre-order DFS, component order
                for (int k = 0; k < kindTypes.Length; k++)
                {
                    var kt = kindTypes[k]; if (kt == null) continue; var info = ObjectKinds.All[k];
                    foreach (var root in roots)
                    {
                        var stack = new Stack<long>(); stack.Push(root);
                        while (stack.Count > 0)
                        {
                            long tp = stack.Pop(); var t = trs[tp]; Go g;
                            if (gos.TryGetValue(t.Go, out g))
                                foreach (var cp in g.Components)
                                {
                                    Type ct; if (cp.FileId != 0 || !compType.TryGetValue(cp.PathId, out ct) || !(ct == kt || ct.IsSubclassOf(kt))) continue;
                                    string path = pathOf(tp);
                                    int[] oc; if (!occ.TryGetValue(tp, out oc)) occ[tp] = oc = new int[32]; int n = oc[(int)info.Kind & 31]++;
                                    path = IdentityPath.WithOccurrence(path, n);
                                    var catg = info.Category; Dictionary<string, object> pv = null;
                                    if (info.Kind == ObjectKind.Pickupable)
                                    {
                                        pv = ParseBehaviour(f, compInfo[cp.PathId], ct, new HashSet<string>(PickWant)); res.Parsed++;
                                        bool dropped = Bool(pv, "wasDropped");
                                        catg = ObjectKinds.PickupableCategory(g.Name, dropped);
                                        if (catg == IdentityCategory.Persistent) loot.Add(g.Name);
                                    }
                                    uint hash = IdentityHash.ForObject(sceneName, path, info.Kind);
                                    table.Add(hash, path, info.Kind, catg, null);
                                    if (pv != null)
                                    {
                                        var fl = LootFlags.None;
                                        if (Bool(pv, "is_heavy_item")) fl |= LootFlags.Heavy; if (Bool(pv, "is_money")) fl |= LootFlags.Money; if (Bool(pv, "destroy_if_was_picked")) fl |= LootFlags.DestroyIfPicked;
                                        if (Bool(pv, "deleteIfPref")) fl |= LootFlags.DeleteIfPref; if (Bool(pv, "lock_parcel_after_pickup")) fl |= LootFlags.Parcel; if (Bool(pv, "advance_story_if_picked")) fl |= LootFlags.StoryPick;
                                        if (Bool(pv, "advance_story_if_placed_car")) fl |= LootFlags.StoryCar; if (Bool(pv, "isContractItem")) fl |= LootFlags.Contract; if (Bool(pv, "never_destroy")) fl |= LootFlags.NeverDestroy;
                                        if (Bool(pv, "dontDestroyDays")) fl |= LootFlags.NoDays; if (Bool(pv, "cant_pick_reason")) fl |= LootFlags.CantPick;
                                        object sn; if (pv.TryGetValue("story_name", out sn) && !string.IsNullOrEmpty(sn as string)) fl |= LootFlags.StoryName;
                                        if (!Bool(pv, "add_any_XP")) fl |= LootFlags.NoXp;
                                        if (catg == IdentityCategory.Dynamic) fl |= LootFlags.Dropped;
                                        for (long x = tp; x != 0; x = trs[x].Father) if (cabinet.Contains(x)) { fl |= LootFlags.RandomCabinet; break; }
                                        cat.Add(hash, g.Name, Int(pv, "item_ID"), fl, null, 0, Int(pv, "add_money"));
                                        LootEntry le; if (cat.TryGet(hash, out le))
                                        {
                                            object pp; le.Pref = pv.TryGetValue("parcel_pref", out pp) ? pp as string : null;
                                            for (long x = tp; x != 0; x = trs[x].Father) { KeyValuePair<uint, int> sl; if (cabSlot.TryGetValue(x, out sl)) { le.Group = sl.Key; le.Outcome = sl.Value; break; } }
                                        }
                                    }
                                }
                            for (int i = t.Children.Count - 1; i >= 0; i--) if (trs.ContainsKey(t.Children[i])) stack.Push(t.Children[i]);
                        }
                    }
                }
                table.Seal();
                IdentityCategory ic;
                cat.Seal(h => !table.TryCategory(h, out ic) || ic == IdentityCategory.Unsupported);
                cat.Digest = table.Digest;
                res.Table = table; res.Loot = cat; res.LootNames = loot.Names; res.LootSharedNames = loot.SharedNames;
            }
        }

        static long Mark(HashSet<long> set, Dictionary<long, long> trOfGo, Dictionary<long, Go> gos, SerializedAssetFile.ObjInfo comp, SerializedAssetFile f)
        {
            var r = f.At(comp); var go = AssetRead.Ptr(r); long tp; if (trOfGo.TryGetValue(go.PathId, out tp)) { set.Add(tp); return tp; }
            return 0;
        }

        // RandomFileCabinet slots (verified fields): first/sec/th + "CabinetDisable" (GameObject, outcome 0) and "CabinetRandom"
        // (List<GameObject>, element k = outcome k + 1). Slot group id = FNV of the cabinet's census path + slot name.
        static readonly string[] SlotNames = { "first", "sec", "th" };
        static void Slots(Dictionary<long, KeyValuePair<uint, int>> map, Dictionary<long, long> trOfGo, Dictionary<string, object> vals, string cabinetPath)
        {
            foreach (var s in SlotNames)
            {
                uint g = IdentityHash.Fnv1a32(cabinetPath + "#" + s); if (g == 0) g = 1;
                object v; long tp;
                if (vals.TryGetValue(s + "CabinetDisable", out v) && v is PPtr && ((PPtr)v).FileId == 0 && trOfGo.TryGetValue(((PPtr)v).PathId, out tp)) map[tp] = new KeyValuePair<uint, int>(g, 0);
                var l = vals.TryGetValue(s + "CabinetRandom", out v) ? v as List<PPtr> : null;
                if (l != null) for (int k = 0; k < l.Count; k++) if (l[k].FileId == 0 && trOfGo.TryGetValue(l[k].PathId, out tp)) map[tp] = new KeyValuePair<uint, int>(g, k + 1);
            }
        }
        static void MarkGo(HashSet<long> set, Dictionary<long, long> trOfGo, PPtr p) { long tp; if (p.FileId == 0 && trOfGo.TryGetValue(p.PathId, out tp)) set.Add(tp); }

        static bool Bool(Dictionary<string, object> v, string k) { object o; return v.TryGetValue(k, out o) && o is bool && (bool)o; }
        static int Int(Dictionary<string, object> v, string k) { object o; return v.TryGetValue(k, out o) && o is int ? (int)o : 0; }

        static Dictionary<string, object> ParseBehaviour(SerializedAssetFile f, SerializedAssetFile.ObjInfo o, Type t, ICollection<string> want)
        {
            var r = f.At(o); long start = f.Position;
            AssetRead.Ptr(r); r.ReadByte(); AssetRead.Align4(r); AssetRead.Ptr(r); AssetRead.Str(r);
            var vals = new Dictionary<string, object>();
            ScriptLayout.For(t).Read(r, want, vals);
            long used = f.Position - start;
            if (used != o.Size) throw new InvalidDataException(t.Name + " layout mismatch at path id " + o.PathId + ": read " + used + " of " + o.Size + " bytes");
            return vals;
        }

        static Type ScriptType(SerializedAssetFile f, PPtr sp, Dictionary<string, Dictionary<long, Type>> files, string dataDir, Func<string, Type> typeByName)
        {
            if (sp.IsNull) return null;
            string file = sp.FileId == 0 ? f.Path : (sp.FileId - 1 < f.Externals.Count ? Path.Combine(dataDir, Path.GetFileName(f.Externals[sp.FileId - 1])) : null);
            if (file == null) return null;
            Dictionary<long, Type> map;
            if (!files.TryGetValue(file, out map))
            {
                files[file] = map = new Dictionary<long, Type>();
                if (File.Exists(file))
                    using (var sf = new SerializedAssetFile(file))
                        foreach (var o in sf.Objects)
                        {
                            if (o.ClassId != 115) continue;
                            var r = sf.At(o); AssetRead.Str(r); r.ReadInt32(); r.BaseStream.Position += 16;
                            string cls = AssetRead.Str(r), nsp = AssetRead.Str(r), asm = AssetRead.Str(r);
                            if (asm != "Assembly-CSharp.dll" && asm != "Assembly-CSharp") continue;
                            var t = typeByName(string.IsNullOrEmpty(nsp) ? cls : nsp + "." + cls);
                            if (t != null) map[o.PathId] = t;
                        }
            }
            Type ty; return map.TryGetValue(sp.PathId, out ty) ? ty : null;
        }
    }
}
