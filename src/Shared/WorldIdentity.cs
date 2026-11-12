// M5 W1: world object identity foundation. Unity-free (unit-testable); the Unity side is src/Plugin/WorldCensus.cs.
//
// Identity of a fixed scene object = FNV-1a 32 over "<scene>/<hierarchy path>#<kind>", namespaced by the region the scene
// belongs to (IdentityNamespace). The path is taken at SceneManager.sceneLoaded, i.e. after Awake/OnEnable and BEFORE any
// Start() of that scene: nothing has been destroyed or re-parented yet (Pickupable.<Start>d__68 destroys picked loot only
// after a 0.05 s realtime wait, VO), so sibling indices are the serialized asset order and identical on every instance.
// A sibling index is added to a path segment only where siblings share the name. Duplicate paths and hash collisions are
// detected and those ids are marked ambiguous: they never resolve (no false stable identity). Full paths exist only during
// the census (and in an explicit export); steady state keeps 32-bit hashes + component references per region.
// Runtime-spawned objects (dropped items, police units, pedestrians) are not in the census; DynamicIdAllocator defines
// their future network ids (owner slot + counter), never derived from Unity instance ids. No W1 network traffic.
using System;
using System.Collections.Generic;
using System.Text;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum IdentityCategory : byte
    {
        Static = 1,         // A: fixed scene object, identity = scene hierarchy path
        Persistent = 2,     // B: identity also represented by an original save key (loot ItemDay_<name>, residences)
        Dynamic = 3,        // C: runtime-spawned, needs a runtime id (dropped items, police, spawned mission objects)
        TimeDependent = 4,  // D: fixed object whose state/activation follows game time or story (NPC residents, story objects)
        Unsupported = 5     // E: no reliable identity yet (random pools, ambiguous paths)
    }

    // Values are diagnostic ids (part of the hash input); do not renumber.
    public enum ObjectKind : byte
    {
        Pickupable = 1, Door = 2, Lockpick = 3, LockedWindow = 4, Safe = 5, SecurityCamera = 6, SecurityPanel = 7,
        LightSwitch = 8, FuseBox = 9, Alarm = 10, Residence = 11, Resident = 12, Guard = 13, Dog = 14, Police = 15,
        Pedestrian = 16, StoryObject = 17, StoryActivator = 18, Medkit = 19, HiddenItem = 20, MailBox = 21,
        ParcelLocker = 22, ItemSpawner = 23, FileCabinet = 24, CopCar = 25
    }

    public sealed class KindInfo
    {
        public readonly ObjectKind Kind; public readonly string TypeName; public readonly IdentityCategory Category; public readonly string Note;
        public KindInfo(ObjectKind k, string type, IdentityCategory c, string note) { Kind = k; TypeName = type; Category = c; Note = note; }
    }

    // Census scope: original component types (all verified to exist in Assembly-CSharp 1.27c, Cecil type scan 2026-11-11).
    public static class ObjectKinds
    {
        public static readonly KindInfo[] All =
        {
            new KindInfo(ObjectKind.Pickupable,     "Pickupable",          IdentityCategory.Persistent,    "loot; original key ItemDay_<name>/Item_<name>; 'dropped' = dynamic"),
            new KindInfo(ObjectKind.Door,           "Door",                IdentityCategory.Static,        "doors, drawers, cupboards, containers; no save key"),
            new KindInfo(ObjectKind.Lockpick,       "Lockpick",            IdentityCategory.Static,        "lock on a door set; NPCs relock (Door.ResetLock)"),
            new KindInfo(ObjectKind.LockedWindow,   "LockedWindow",        IdentityCategory.Static,        "window glass/alarm"),
            new KindInfo(ObjectKind.Safe,           "SafeMaster",          IdentityCategory.Static,        "safe minigame"),
            new KindInfo(ObjectKind.SecurityCamera, "SecurityCamera",      IdentityCategory.Static,        "camera; discovery marker key only"),
            new KindInfo(ObjectKind.SecurityPanel,  "SecurityPanel",       IdentityCategory.Static,        "panel deactivation"),
            new KindInfo(ObjectKind.LightSwitch,    "LightSwitch",         IdentityCategory.Static,        "lights"),
            new KindInfo(ObjectKind.FuseBox,        "FuseBoxSwitch",       IdentityCategory.Static,        "residence power"),
            new KindInfo(ObjectKind.Alarm,          "Alarm",               IdentityCategory.Static,        "alarm trigger"),
            new KindInfo(ObjectKind.Residence,      "SelectResidence",     IdentityCategory.Persistent,    "residence; <house>_alert / mission keys"),
            new KindInfo(ObjectKind.Resident,       "AI",                  IdentityCategory.TimeDependent, "resident NPC; behaviour from GameManager.currentHour"),
            new KindInfo(ObjectKind.Guard,          "GuardAI",             IdentityCategory.TimeDependent, "patrol guard"),
            new KindInfo(ObjectKind.Dog,            "DogAI",               IdentityCategory.TimeDependent, "dog"),
            new KindInfo(ObjectKind.Police,         "PolicemanAI",         IdentityCategory.Dynamic,       "police are spawned by Police_Manager; scene copies listed for diagnosis"),
            new KindInfo(ObjectKind.Pedestrian,     "SidewalkAI",          IdentityCategory.Unsupported,   "pedestrian pool, random spawns"),
            new KindInfo(ObjectKind.StoryObject,    "StoryObject",         IdentityCategory.TimeDependent, "enabled by Story index"),
            new KindInfo(ObjectKind.StoryActivator, "ActivateObjectStory", IdentityCategory.TimeDependent, "enabled by Story index"),
            new KindInfo(ObjectKind.Medkit,         "Medkit",              IdentityCategory.Persistent,    "car medkit; key Medkit"),
            new KindInfo(ObjectKind.HiddenItem,     "HiddenItem",          IdentityCategory.Persistent,    "hidden item; key HiddenItem"),
            new KindInfo(ObjectKind.MailBox,        "MailBox",             IdentityCategory.Static,        "mailbox / camera placement"),
            new KindInfo(ObjectKind.ParcelLocker,   "ParcelLocker",        IdentityCategory.Static,        "parcel codes (parcel_strings_to_set)"),
            new KindInfo(ObjectKind.ItemSpawner,    "ItemSpawner",         IdentityCategory.Unsupported,   "random loot spawner (spawned items are dynamic)"),
            new KindInfo(ObjectKind.FileCabinet,    "RandomFileCabinet",   IdentityCategory.Unsupported,   "random contents"),
            new KindInfo(ObjectKind.CopCar,         "CopCar",              IdentityCategory.Dynamic,       "police car, spawned"),
        };

        public static KindInfo Get(ObjectKind k) { foreach (var i in All) if (i.Kind == k) return i; return null; }

        // Pickupable: the original renames dropped items to "dropped" (Inventory.DropItemNewSystem IL_00e0, flag wasDropped);
        // they have no key and no scene path -> dynamic. Every other loot object is keyed by its GameObject name.
        public static IdentityCategory PickupableCategory(string name, bool wasDropped)
        {
            return wasDropped || name == "dropped" ? IdentityCategory.Dynamic : IdentityCategory.Persistent;
        }
    }

    // Region namespace of a scene. MainScene is loaded for every region with identical content -> its own namespace.
    public static class IdentityNamespace
    {
        public const int MainScene = -1000;
        public const int None = int.MinValue;
        static readonly Dictionary<string, int> SceneToRegion = new Dictionary<string, int>
        {
            { "MainScene", MainScene }, { "PlayerHideout", 0 }, { "PawnShop", 2 }, { "Auto Shop", 3 }, { "DrugFarm", 90 },
            { "Warehouse", 91 }, { "BankScene", 92 }, { "Jail", 93 }, { "Forestside Resort", 94 }, { "HauntedMansion", 95 },
            { "Museum", 96 }, { "Hillville", 97 }, { "SwatRange", 98 }, { "MadisonSt", 99 }, { "IntroHouse", -1 }
        };
        // None for the main menu, the hacking puzzle and unknown scenes (no census).
        public static int ForScene(string scene) { int r; return scene != null && SceneToRegion.TryGetValue(scene, out r) ? r : None; }
        public static string Name(int ns) { return ns == MainScene ? "MainScene" : Regions.Name(ns); }
    }

    public static class IdentityHash
    {
        public const uint Offset = 2166136261u, Prime = 16777619u;
        public static uint Fnv1a32(string s) { return Append(Offset, s); }
        public static uint Append(uint h, string s)
        {
            if (s == null) return h;
            for (int i = 0; i < s.Length; i++) { char c = s[i]; h = (h ^ (byte)c) * Prime; h = (h ^ (byte)(c >> 8)) * Prime; }
            return h;
        }
        public static uint Append(uint h, byte b) { return (h ^ b) * Prime; }
        // Identity input: scene name, '/', path, '#', kind byte.
        public static uint ForObject(string scene, string path, ObjectKind kind)
        {
            uint h = Append(Offset, scene);
            h = Append(h, "/"); h = Append(h, path); h = Append(h, "#");
            return Append(h, (byte)kind);
        }
    }

    public static class IdentityPath
    {
        // A sibling index only where another sibling has the same name (otherwise the plain name stays readable and stable
        // even if unrelated siblings are added in a game update).
        public static string Segment(string name, int siblingIndex, bool sharedName)
        {
            return sharedName ? name + "[" + siblingIndex + "]" : name;
        }
        public static string Join(string parentPath, string segment) { return string.IsNullOrEmpty(parentPath) ? segment : parentPath + "/" + segment; }
        // Several components of the same kind on one GameObject: '@k' for the k-th (k >= 1), in component order.
        public static string WithOccurrence(string path, int occurrence) { return occurrence <= 0 ? path : path + "@" + occurrence; }
    }

    // Counts names per parent (or among scene roots) once, so duplicate-name checks don't re-read Unity names.
    public sealed class SiblingNames
    {
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.Ordinal);
        public void Add(string name) { int n; _counts.TryGetValue(name ?? "", out n); _counts[name ?? ""] = n + 1; }
        public bool Shared(string name) { int n; return _counts.TryGetValue(name ?? "", out n) && n > 1; }
        public int Distinct { get { return _counts.Count; } }
    }

    public struct WorldObjectId : IEquatable<WorldObjectId>
    {
        public const int WireBytes = 8;
        public int Namespace; public uint Hash;
        public WorldObjectId(int ns, uint hash) { Namespace = ns; Hash = hash; }
        public bool Equals(WorldObjectId o) { return o.Namespace == Namespace && o.Hash == Hash; }
        public override bool Equals(object o) { return o is WorldObjectId && Equals((WorldObjectId)o); }
        public override int GetHashCode() { return (int)Hash ^ (Namespace * 397); }
        public override string ToString() { return IdentityNamespace.Name(Namespace) + ":" + Hash.ToString("X8"); }
        public void Write(NetDataWriter w) { w.Put(Namespace); w.Put(Hash); }
        public static bool TryRead(NetDataReader r, out WorldObjectId id) { id = default(WorldObjectId); int ns; uint h; if (!r.TryGetInt(out ns) || !r.TryGetUInt(out h)) return false; id = new WorldObjectId(ns, h); return true; }
    }

    public enum AddResult { Added, DuplicatePath, HashCollision, Rejected }

    // One namespace (region or MainScene) of the current scene load.
    public sealed class IdentityTable
    {
        public const int MaxEntries = 20000, MaxDiagnostics = 32;
        struct Entry { public uint Hash; public byte Kind; public byte Category; public object Target; }

        public readonly int Namespace;
        readonly List<Entry> _entries = new List<Entry>();
        readonly Dictionary<uint, int> _index = new Dictionary<uint, int>();
        readonly HashSet<uint> _ambiguous = new HashSet<uint>();
        Dictionary<uint, string> _paths = new Dictionary<uint, string>();   // census only; dropped by Seal()
        readonly List<string> _diag = new List<string>();
        readonly int[] _byKind = new int[32], _byCategory = new int[8];
        public int DuplicatePaths { get; private set; }
        public int HashCollisions { get; private set; }
        public int Rejected { get; private set; }
        public int DeadRemoved { get; private set; }
        public bool Sealed { get; private set; }
        public uint Digest { get; private set; }

        public IdentityTable(int ns) { Namespace = ns; }
        public int Count { get { return _entries.Count; } }
        public int Distinct { get { return _index.Count - _ambiguous.Count; } }
        public int Ambiguous { get { return _ambiguous.Count; } }
        public IList<string> Diagnostics { get { return _diag; } }
        public int CountOf(ObjectKind k) { return _byKind[(int)k & 31]; }
        public int CountOf(IdentityCategory c) { return _byCategory[(int)c & 7]; }
        // Steady-state estimate: entry list + index + ambiguous set (managed overhead approximated).
        public long EstimatedBytes { get { return _entries.Capacity * 24L + _index.Count * 28L + _ambiguous.Count * 24L; } }

        public AddResult Add(uint hash, string path, ObjectKind kind, IdentityCategory cat, object target)
        {
            if (Sealed) throw new InvalidOperationException("table sealed");
            if (_entries.Count >= MaxEntries) { Rejected++; return AddResult.Rejected; }
            _byKind[(int)kind & 31]++; _byCategory[(int)cat & 7]++;
            int at;
            if (_index.TryGetValue(hash, out at))
            {
                string first; _paths.TryGetValue(hash, out first);
                bool samePath = first == path;
                if (samePath) DuplicatePaths++; else HashCollisions++;
                _ambiguous.Add(hash);
                if (_diag.Count < MaxDiagnostics)
                    _diag.Add((samePath ? "DUPLICATE_PATH " : "HASH_COLLISION ") + hash.ToString("X8") + " " + kind + " '" + path + "'" + (samePath ? "" : " vs '" + first + "'"));
                _entries.Add(new Entry { Hash = hash, Kind = (byte)kind, Category = (byte)IdentityCategory.Unsupported, Target = null });
                return samePath ? AddResult.DuplicatePath : AddResult.HashCollision;
            }
            _index[hash] = _entries.Count;
            _paths[hash] = path;
            _entries.Add(new Entry { Hash = hash, Kind = (byte)kind, Category = (byte)cat, Target = target });
            return AddResult.Added;
        }

        // Ends the census: drops the path strings, computes the order-independent digest of the unambiguous ids.
        public void Seal()
        {
            if (Sealed) return;
            Sealed = true; _paths = null;
            var ids = new List<ulong>(_index.Count);
            foreach (var kv in _index) if (!_ambiguous.Contains(kv.Key)) ids.Add(((ulong)kv.Key << 8) | _entries[kv.Value].Kind);
            ids.Sort();
            uint h = IdentityHash.Offset;
            foreach (var x in ids) for (int i = 0; i < 5; i++) h = IdentityHash.Append(h, (byte)(x >> (8 * i)));
            Digest = h;
        }

        // Never resolves an ambiguous id; drops references to destroyed objects (alive = Unity null check).
        public bool TryResolve(uint hash, Func<object, bool> alive, out object target)
        {
            target = null;
            int at;
            if (_ambiguous.Contains(hash) || !_index.TryGetValue(hash, out at)) return false;
            var e = _entries[at];
            if (e.Target == null) return false;
            if (alive != null && !alive(e.Target)) { e.Target = null; _entries[at] = e; DeadRemoved++; return false; }
            target = e.Target;
            return true;
        }

        public bool TryCategory(uint hash, out IdentityCategory cat)
        {
            cat = IdentityCategory.Unsupported; int at;
            if (!_index.TryGetValue(hash, out at)) return false;
            cat = _ambiguous.Contains(hash) ? IdentityCategory.Unsupported : (IdentityCategory)_entries[at].Category;
            return true;
        }

        public void ReleaseTargets()
        {
            for (int i = 0; i < _entries.Count; i++) { var e = _entries[i]; e.Target = null; _entries[i] = e; }
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append("objects=").Append(Count).Append(" distinct=").Append(Distinct).Append(" dupPaths=").Append(DuplicatePaths)
              .Append(" hashCollisions=").Append(HashCollisions).Append(" ambiguous=").Append(Ambiguous).Append(" rejected=").Append(Rejected)
              .Append(" static=").Append(CountOf(IdentityCategory.Static)).Append(" persistent=").Append(CountOf(IdentityCategory.Persistent))
              .Append(" dynamic=").Append(CountOf(IdentityCategory.Dynamic)).Append(" time=").Append(CountOf(IdentityCategory.TimeDependent))
              .Append(" unsupported=").Append(CountOf(IdentityCategory.Unsupported)).Append(" digest=").Append(Digest.ToString("X8"))
              .Append(" mem~").Append(EstimatedBytes / 1024).Append("KB");
            return sb.ToString();
        }
    }

    // All namespaces of the currently loaded scenes. Tables are replaced on reload and removed on unload.
    public sealed class IdentityRegistry
    {
        public const int MaxNamespaces = 8;
        readonly Dictionary<int, IdentityTable> _tables = new Dictionary<int, IdentityTable>();
        readonly Dictionary<int, uint> _lastDigest = new Dictionary<int, uint>();   // consistency across reloads (this process)

        public int Count { get { return _tables.Count; } }
        public IdentityTable Get(int ns) { IdentityTable t; return _tables.TryGetValue(ns, out t) ? t : null; }

        public IdentityTable Begin(int ns)
        {
            Remove(ns);
            if (_tables.Count >= MaxNamespaces) throw new InvalidOperationException("too many identity namespaces");
            var t = new IdentityTable(ns);
            _tables[ns] = t;
            return t;
        }

        // Seals the table; returns null on the first census of this namespace, else whether the digest matches the previous one.
        public bool? Finish(IdentityTable t)
        {
            t.Seal();
            uint prev; bool had = _lastDigest.TryGetValue(t.Namespace, out prev);
            _lastDigest[t.Namespace] = t.Digest;
            return had ? (bool?)(prev == t.Digest) : null;
        }

        public void Remove(int ns)
        {
            IdentityTable t;
            if (_tables.TryGetValue(ns, out t)) { t.ReleaseTargets(); _tables.Remove(ns); }
        }

        public void Clear() { foreach (var t in _tables.Values) t.ReleaseTargets(); _tables.Clear(); }

        public bool TryResolve(WorldObjectId id, Func<object, bool> alive, out object target)
        {
            target = null;
            var t = Get(id.Namespace);
            return t != null && t.Sealed && t.TryResolve(id.Hash, alive, out target);
        }

        public long EstimatedBytes { get { long b = 0; foreach (var t in _tables.Values) b += t.EstimatedBytes; return b; } }
    }

    // At most one census per loaded scene instance (scene handle); never per frame.
    public sealed class CensusGate
    {
        readonly HashSet<int> _done = new HashSet<int>();
        public long Runs { get; private set; }
        public long Refused { get; private set; }
        public bool TryBegin(int sceneHandle, int ns)
        {
            if (ns == IdentityNamespace.None || !_done.Add(sceneHandle)) { Refused++; return false; }
            Runs++;
            return true;
        }
        public void Unloaded(int sceneHandle) { _done.Remove(sceneHandle); }
        public int Tracked { get { return _done.Count; } }
    }

    // Duplicate GameObject names among loot of one namespace: the original game shares one ItemDay_<name> key between them.
    public sealed class NameCensus
    {
        readonly Dictionary<string, int> _n = new Dictionary<string, int>(StringComparer.Ordinal);
        public void Add(string name) { int c; _n.TryGetValue(name, out c); _n[name] = c + 1; }
        public int Names { get { return _n.Count; } }
        public int SharedNames { get { int s = 0; foreach (var v in _n.Values) if (v > 1) s++; return s; } }
        public int ObjectsWithSharedNames { get { int s = 0; foreach (var v in _n.Values) if (v > 1) s += v; return s; } }
        public List<string> Examples(int max)
        {
            var l = new List<string>();
            foreach (var kv in _n) { if (kv.Value > 1 && l.Count < max) l.Add(kv.Key + " x" + kv.Value); }
            return l;
        }
    }

    // Design for future runtime entities (W3+): id = owner slot (high byte) + per-owner counter. Not used for sync in W1.
    public sealed class DynamicIdAllocator
    {
        public readonly byte Slot; uint _next = 1;
        public DynamicIdAllocator(byte slot) { if (slot == 0) throw new ArgumentException("slot"); Slot = slot; }
        public uint Next()
        {
            if (_next > 0x00FFFFFFu) throw new InvalidOperationException("dynamic id space exhausted");
            return ((uint)Slot << 24) | _next++;
        }
        public static byte OwnerOf(uint id) { return (byte)(id >> 24); }
    }
}
