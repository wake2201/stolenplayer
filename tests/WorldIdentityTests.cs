// M5 W1 offline tests: world object identity (hashing, path rules, namespaces, collisions, registry lifecycle, limits,
// census gate, dynamic ids). Pure logic only: the scene traversal itself (WorldCensus) runs only in game.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class WorldIdentityTests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("01 stable static identity: deterministic FNV-1a over scene/path/kind (reference implementation)", Deterministic);
        Run("02 namespaces: same path in different regions are different ids; MainScene has its own namespace", Namespaces);
        Run("03 identical sibling names get a sibling index, unique names stay plain", SiblingRule);
        Run("04 duplicate hierarchy paths are ambiguous: never resolved, diagnosed", DuplicatePaths);
        Run("05 hash collisions are detected (different paths, same hash), both ambiguous", HashCollision);
        Run("06 kind and multi-component occurrence are part of the identity", KindAndOccurrence);
        Run("07 scene unload removes the namespace; single load clears all", Unload);
        Run("08 destroyed objects: lookup releases the reference and fails", Destroyed);
        Run("09 dynamic classification: dropped loot, police, pedestrians; dynamic ids are owner+counter", Dynamic);
        Run("10 registry reconstruction: digest stable across reloads, order independent, change detected", Reconstruction);
        Run("11 limits: entries, namespaces, diagnostics; paths dropped after seal", Limits);
        Run("12 census gate: once per scene instance, never repeated, unknown scenes skipped", Gate);
        Run("13 wire id: 8 bytes round trip", Wire);
        Run("14 namespace table covers every region scene; kinds table complete", Tables);
        Run("15 loot name census: names shared between objects (shared ItemDay_ key) are counted", LootNames);
        Run("16 steady state: resolve is a dictionary lookup (100k lookups, no scene search)", ResolveCost);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    sealed class Obj { public bool Alive = true; public string Name; public Obj(string n) { Name = n; } }
    static readonly Func<object, bool> Alive = o => o is Obj && ((Obj)o).Alive;

    static uint RefFnv(byte[] bytes) { uint h = 2166136261; foreach (var b in bytes) { h ^= b; h *= 16777619; } return h; }
    static byte[] Utf16(string s) { return System.Text.Encoding.Unicode.GetBytes(s); }

    static void Deterministic()
    {
        string scene = "MadisonSt", path = "HOUSES/House 12/Interior/TV";
        var bytes = new List<byte>();
        bytes.AddRange(Utf16(scene)); bytes.AddRange(Utf16("/")); bytes.AddRange(Utf16(path)); bytes.AddRange(Utf16("#")); bytes.Add((byte)ObjectKind.Pickupable);
        uint expect = RefFnv(bytes.ToArray());
        uint a = IdentityHash.ForObject(scene, path, ObjectKind.Pickupable);
        uint b = IdentityHash.ForObject(scene, path, ObjectKind.Pickupable);
        Expect(a == expect && a == b, "hash matches the reference FNV-1a and repeats (" + a.ToString("X8") + " vs " + expect.ToString("X8") + ")");
        Expect(IdentityHash.ForObject(scene, path + " ", ObjectKind.Pickupable) != a, "different path -> different hash");
        Expect(IdentityHash.Fnv1a32("") == IdentityHash.Offset, "empty string = offset basis");
    }

    static void Namespaces()
    {
        uint h = IdentityHash.ForObject("X", "A/B", ObjectKind.Door);
        var a = new WorldObjectId(99, h); var b = new WorldObjectId(97, h);
        Expect(!a.Equals(b) && a.GetHashCode() != b.GetHashCode(), "region is part of the id");
        var reg = new IdentityRegistry();
        var o1 = new Obj("door madison"); var o2 = new Obj("door hillville");
        var t1 = reg.Begin(99); t1.Add(h, "A/B", ObjectKind.Door, IdentityCategory.Static, o1); reg.Finish(t1);
        var t2 = reg.Begin(97); t2.Add(h, "A/B", ObjectKind.Door, IdentityCategory.Static, o2); reg.Finish(t2);
        object r;
        Expect(reg.TryResolve(a, Alive, out r) && r == o1, "madison resolves its object");
        Expect(reg.TryResolve(b, Alive, out r) && r == o2, "hillville resolves its object");
        Expect(IdentityNamespace.ForScene("MainScene") == IdentityNamespace.MainScene && IdentityNamespace.ForScene("MadisonSt") == 99
            && IdentityNamespace.ForScene("PlayerHideout") == 0 && IdentityNamespace.ForScene("Hillville") == 97 && IdentityNamespace.ForScene("Auto Shop") == 3, "scene -> namespace");
        Expect(IdentityNamespace.ForScene("MainMenuNew") == IdentityNamespace.None && IdentityNamespace.ForScene("HackingPuzzle") == IdentityNamespace.None, "menu/puzzle: no census");
    }

    static void SiblingRule()
    {
        var sib = new SiblingNames(); sib.Add("Vase"); sib.Add("Vase"); sib.Add("Lamp");
        Expect(sib.Shared("Vase") && !sib.Shared("Lamp") && !sib.Shared("Missing"), "shared detection");
        string v0 = IdentityPath.Join("Room", IdentityPath.Segment("Vase", 0, sib.Shared("Vase")));
        string v1 = IdentityPath.Join("Room", IdentityPath.Segment("Vase", 1, sib.Shared("Vase")));
        string lamp = IdentityPath.Join("Room", IdentityPath.Segment("Lamp", 2, sib.Shared("Lamp")));
        Expect(v0 == "Room/Vase[0]" && v1 == "Room/Vase[1]" && lamp == "Room/Lamp", "paths: " + v0 + ", " + v1 + ", " + lamp);
        Expect(IdentityHash.ForObject("S", v0, ObjectKind.Pickupable) != IdentityHash.ForObject("S", v1, ObjectKind.Pickupable), "same-name siblings -> distinct ids");
        Expect(IdentityPath.Join("", "Root") == "Root", "root segment");
    }

    static void DuplicatePaths()
    {
        var t = new IdentityTable(99);
        uint h = IdentityHash.ForObject("S", "A/Thing", ObjectKind.Door);
        Expect(t.Add(h, "A/Thing", ObjectKind.Door, IdentityCategory.Static, new Obj("1")) == AddResult.Added, "first added");
        Expect(t.Add(h, "A/Thing", ObjectKind.Door, IdentityCategory.Static, new Obj("2")) == AddResult.DuplicatePath, "second = duplicate path");
        t.Seal();
        object r;
        Expect(!t.TryResolve(h, Alive, out r) && t.Ambiguous == 1 && t.DuplicatePaths == 1, "ambiguous id never resolves");
        IdentityCategory c; Expect(t.TryCategory(h, out c) && c == IdentityCategory.Unsupported, "category reported as unsupported");
        Expect(t.Diagnostics.Count == 1 && t.Diagnostics[0].StartsWith("DUPLICATE_PATH"), "diagnosed");
    }

    static void HashCollision()
    {
        var t = new IdentityTable(97);
        Expect(t.Add(0xDEADBEEF, "A/One", ObjectKind.Door, IdentityCategory.Static, new Obj("1")) == AddResult.Added, "first");
        Expect(t.Add(0xDEADBEEF, "B/Two", ObjectKind.Door, IdentityCategory.Static, new Obj("2")) == AddResult.HashCollision, "collision");
        t.Seal();
        object r;
        Expect(!t.TryResolve(0xDEADBEEF, Alive, out r) && t.HashCollisions == 1, "collided id not resolvable");
        Expect(t.Diagnostics[0].Contains("A/One") && t.Diagnostics[0].Contains("B/Two"), "both paths kept in the diagnostic");
        Expect(t.Distinct == 0, "no distinct ids left");
    }

    static void KindAndOccurrence()
    {
        Expect(IdentityHash.ForObject("S", "A", ObjectKind.Door) != IdentityHash.ForObject("S", "A", ObjectKind.Lockpick), "kind in hash");
        Expect(IdentityPath.WithOccurrence("A/B", 0) == "A/B" && IdentityPath.WithOccurrence("A/B", 1) == "A/B@1", "occurrence suffix");
        var t = new IdentityTable(0);
        Expect(t.Add(IdentityHash.ForObject("S", "A/B", ObjectKind.Door), "A/B", ObjectKind.Door, IdentityCategory.Static, new Obj("d1")) == AddResult.Added
            && t.Add(IdentityHash.ForObject("S", "A/B@1", ObjectKind.Door), "A/B@1", ObjectKind.Door, IdentityCategory.Static, new Obj("d2")) == AddResult.Added, "two doors on one object are distinct");
    }

    static void Unload()
    {
        var reg = new IdentityRegistry();
        var o = new Obj("x"); uint h = 5;
        var t = reg.Begin(0); t.Add(h, "p", ObjectKind.Door, IdentityCategory.Static, o); reg.Finish(t);
        object r;
        Expect(reg.TryResolve(new WorldObjectId(0, h), Alive, out r), "resolves while loaded");
        reg.Remove(0);
        Expect(!reg.TryResolve(new WorldObjectId(0, h), Alive, out r) && reg.Count == 0, "gone after unload");
        var t2 = reg.Begin(0); t2.Add(h, "p", ObjectKind.Door, IdentityCategory.Static, o); reg.Finish(t2);
        var t3 = reg.Begin(IdentityNamespace.MainScene); reg.Finish(t3);
        reg.Clear();
        Expect(reg.Count == 0 && !reg.TryResolve(new WorldObjectId(0, h), Alive, out r), "single scene load clears everything");
        var unsealed = reg.Begin(1); unsealed.Add(9, "q", ObjectKind.Door, IdentityCategory.Static, o);
        Expect(!reg.TryResolve(new WorldObjectId(1, 9), Alive, out r), "unsealed table (census running) does not resolve");
    }

    static void Destroyed()
    {
        var t = new IdentityTable(99); var o = new Obj("tv");
        t.Add(1, "tv", ObjectKind.Pickupable, IdentityCategory.Persistent, o); t.Seal();
        object r;
        Expect(t.TryResolve(1, Alive, out r) && r == o, "alive resolves");
        o.Alive = false;
        Expect(!t.TryResolve(1, Alive, out r) && t.DeadRemoved == 1, "destroyed -> released");
        o.Alive = true;
        Expect(!t.TryResolve(1, Alive, out r) && t.DeadRemoved == 1, "reference stays released (no resurrection)");
    }

    static void Dynamic()
    {
        Expect(ObjectKinds.PickupableCategory("dropped", false) == IdentityCategory.Dynamic, "renamed drop");
        Expect(ObjectKinds.PickupableCategory("Laptop", true) == IdentityCategory.Dynamic, "wasDropped flag");
        Expect(ObjectKinds.PickupableCategory("Laptop", false) == IdentityCategory.Persistent, "scene loot = persistent (name key)");
        Expect(ObjectKinds.Get(ObjectKind.Police).Category == IdentityCategory.Dynamic && ObjectKinds.Get(ObjectKind.CopCar).Category == IdentityCategory.Dynamic, "police dynamic");
        Expect(ObjectKinds.Get(ObjectKind.Pedestrian).Category == IdentityCategory.Unsupported, "pedestrian pool unsupported");
        Expect(ObjectKinds.Get(ObjectKind.Resident).Category == IdentityCategory.TimeDependent && ObjectKinds.Get(ObjectKind.StoryObject).Category == IdentityCategory.TimeDependent, "time-dependent");
        var host = new DynamicIdAllocator(1); var client = new DynamicIdAllocator(2);
        var seen = new HashSet<uint>();
        for (int i = 0; i < 1000; i++) { Expect(seen.Add(host.Next()) && seen.Add(client.Next()), "unique"); }
        uint id = client.Next();
        Expect(DynamicIdAllocator.OwnerOf(id) == 2 && DynamicIdAllocator.OwnerOf(host.Next()) == 1, "owner encoded");
        bool threw = false; try { new DynamicIdAllocator(0); } catch (ArgumentException) { threw = true; }
        Expect(threw, "slot 0 refused");
    }

    static IdentityTable Fill(IdentityRegistry reg, int ns, string[] paths, bool reverse)
    {
        var t = reg.Begin(ns);
        var list = new List<string>(paths); if (reverse) list.Reverse();
        foreach (var p in list) t.Add(IdentityHash.ForObject("MadisonSt", p, ObjectKind.Door), p, ObjectKind.Door, IdentityCategory.Static, new Obj(p));
        return t;
    }

    static void Reconstruction()
    {
        var reg = new IdentityRegistry();
        var paths = new[] { "H1/Door", "H1/Window", "H2/Door[0]", "H2/Door[1]" };
        var first = Fill(reg, 99, paths, false);
        Expect(reg.Finish(first) == null, "first census: no comparison");
        uint d1 = first.Digest;
        var again = Fill(reg, 99, paths, true);   // other enumeration order
        Expect(reg.Finish(again) == true && again.Digest == d1, "reload: same digest regardless of order");
        var changed = Fill(reg, 99, new[] { "H1/Door", "H1/Window", "H2/Door[0]" }, false);
        Expect(reg.Finish(changed) == false, "different content detected");
        object r;
        Expect(reg.TryResolve(new WorldObjectId(99, IdentityHash.ForObject("MadisonSt", "H1/Door", ObjectKind.Door)), Alive, out r), "rebuilt table resolves");
    }

    static void Limits()
    {
        var t = new IdentityTable(0);
        for (int i = 0; i < IdentityTable.MaxEntries; i++) t.Add((uint)i, null, ObjectKind.Door, IdentityCategory.Static, null);
        Expect(t.Add(0xFFFFFFF0, "x", ObjectKind.Door, IdentityCategory.Static, null) == AddResult.Rejected && t.Rejected == 1, "entry limit");
        var d = new IdentityTable(1);
        for (int i = 0; i < 100; i++) { d.Add(7, "same", ObjectKind.Door, IdentityCategory.Static, null); }
        Expect(d.Diagnostics.Count == IdentityTable.MaxDiagnostics, "diagnostics bounded at " + IdentityTable.MaxDiagnostics);
        var reg = new IdentityRegistry();
        for (int i = 0; i < IdentityRegistry.MaxNamespaces; i++) reg.Begin(i);
        bool threw = false; try { reg.Begin(100); } catch (InvalidOperationException) { threw = true; }
        Expect(threw, "namespace limit");
        var s = new IdentityTable(2);
        for (int i = 0; i < 1000; i++) s.Add((uint)(i * 7919), "SomeRoot/Some House/Interior/Object number " + i, ObjectKind.Pickupable, IdentityCategory.Persistent, null);
        s.Seal();
        Expect(s.EstimatedBytes < 100 * 1024, "steady state ~" + s.EstimatedBytes / 1024 + " KB for 1000 objects (no path strings)");
        threw = false; try { s.Add(1, "x", ObjectKind.Door, IdentityCategory.Static, null); } catch (InvalidOperationException) { threw = true; }
        Expect(threw, "sealed table refuses additions");
    }

    static void Gate()
    {
        var g = new CensusGate();
        Expect(g.TryBegin(101, 99), "first load runs");
        for (int frame = 0; frame < 1000; frame++) Expect(!g.TryBegin(101, 99), "no repeat for the same scene instance");
        Expect(g.Runs == 1 && g.Refused == 1000, "one run, repeats refused");
        Expect(!g.TryBegin(102, IdentityNamespace.None), "menu/unknown scene skipped");
        g.Unloaded(101);
        Expect(g.TryBegin(101, 99) && g.Runs == 2, "reload after unload runs again");
        Expect(g.TryBegin(103, IdentityNamespace.MainScene) && g.Tracked == 2, "bounded tracking (loaded scenes only)");
    }

    static void Wire()
    {
        var id = new WorldObjectId(-1000, 0xCAFEBABE);
        var w = new NetDataWriter(); id.Write(w);
        Expect(w.Length == WorldObjectId.WireBytes, "8 bytes");
        WorldObjectId back; Expect(WorldObjectId.TryRead(new NetDataReader(w.CopyData()), out back) && back.Equals(id), "round trip");
        Expect(!WorldObjectId.TryRead(new NetDataReader(new byte[3]), out back), "truncated rejected");
        Expect(id.ToString() == "MainScene:CAFEBABE", "readable: " + id);
    }

    static void Tables()
    {
        foreach (var region in new[] { 0, 2, 3, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, -1 })
        {
            string scene = Regions.ExpectedScene(region);
            Expect(scene != null && IdentityNamespace.ForScene(scene) == region, "region " + region + " scene " + scene);
        }
        var kinds = new HashSet<ObjectKind>();
        foreach (var k in ObjectKinds.All) { Expect(kinds.Add(k.Kind) && !string.IsNullOrEmpty(k.TypeName), "unique kind " + k.Kind); }
        Expect(kinds.Count == Enum.GetValues(typeof(ObjectKind)).Length, "every kind has a census entry");
    }

    static void LootNames()
    {
        var n = new NameCensus();
        foreach (var s in new[] { "Vase", "Vase", "Vase", "Laptop", "TV", "TV" }) n.Add(s);
        Expect(n.Names == 3 && n.SharedNames == 2 && n.ObjectsWithSharedNames == 5, "shared names counted");
        Expect(n.Examples(1).Count == 1, "bounded examples");
    }

    static void ResolveCost()
    {
        var reg = new IdentityRegistry();
        var t = reg.Begin(99);
        var objs = new Obj[2000];
        for (int i = 0; i < objs.Length; i++) { objs[i] = new Obj("o" + i); t.Add(IdentityHash.ForObject("MadisonSt", "o" + i, ObjectKind.Door), "o" + i, ObjectKind.Door, IdentityCategory.Static, objs[i]); }
        reg.Finish(t);
        var ids = new WorldObjectId[objs.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = new WorldObjectId(99, IdentityHash.ForObject("MadisonSt", "o" + i, ObjectKind.Door));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int gc = GC.CollectionCount(0);
        object r; int hits = 0;
        for (int i = 0; i < 100000; i++) if (reg.TryResolve(ids[i % ids.Length], Alive, out r)) hits++;
        sw.Stop();
        Expect(hits == 100000, "all resolved");
        Console.WriteLine("      100k resolves: " + sw.Elapsed.TotalMilliseconds.ToString("0.0") + " ms, gen0 GCs: " + (GC.CollectionCount(0) - gc));
        Expect(sw.Elapsed.TotalMilliseconds < 1000, "fast lookup");
    }
}
