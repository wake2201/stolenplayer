// M5 W2 offline tests: world-key ownership policy, snapshot/delta codec, host mirror (rebase, live changes, coalescing,
// day pruning), client mirror (validation, ordering, history isolation, local loot markers, reconcile plan), isolation of
// Player-2 / CarBox / fleet / locker keys, limits, corrupt payloads, steady-state cost. A simulated client SPrefs store
// stands in for FBPP; Unity lifecycle (sceneLoaded before Start) and the Harmony writer hooks run only in game.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class WorldMirrorTests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("01 classification: allowlisted families only; unknown and player/storage/fleet keys never mirrored", Classification);
        Run("02 initial snapshot: host scan -> snapshot -> client store equals the host's world keys", InitialSnapshot);
        Run("03 snapshot validation: version, epoch 0, truncation, trailing bytes, injected key", SnapshotValidation);
        Run("04 save profile isolation: another profile is a new history, its deltas are refused", ProfileIsolation);
        Run("05 session/epoch isolation: deltas of another host process are refused", EpochIsolation);
        Run("06 revision ordering: duplicate and gap detection", Ordering);
        Run("07 duplicate messages change nothing", Duplicates);
        Run("08 stale snapshot of the same history is ignored", StaleSnapshot);
        Run("09 missing update recovery: gap -> resync snapshot -> consistent", GapRecovery);
        Run("10 returning to the menu / Continue from the sandbox: reconcile restores host values", MenuReturn);
        Run("11 rejoining the same host: snapshot of the same history accepted", Rejoin);
        Run("12 individual / joint travel: every scene load reconciles idempotently (no redundant writes)", TravelReloads);
        Run("13 unsaved host change, host reloads without saving: client follows the reloaded state", UnsavedHostChange);
        Run("14 host restart (new epoch): old host keys removed, new state applied", HostRestart);
        Run("15 host original save untouched: mirror only reads host values; client keeps no write path to the host", HostSaveUntouched);
        Run("16 Player-2 inventory, money, bank, tool wear never touched by snapshots or reconcile", P2Isolation);
        Run("17 CarBox / locker / vehicle fleet keys never touched", StorageFleetIsolation);
        Run("18 unknown keys refused by host mirror and codec", UnknownKeys);
        Run("19 local client loot marker kept until the host has a value; host delete keeps it", LocalMarkers);
        Run("20 day count: expired loot markers pruned identically; days-only delta", Days);
        Run("21 coalescing: many changes of one key within a frame = one change", Coalescing);
        Run("22 family reconcile on the host: changed + vanished keys become sets + deletes", HostReconcile);
        Run("23 snapshot size limits", SizeLimits);
        Run("24 corrupt payload fuzz: never throws, never accepted with foreign keys", Fuzz);
        Run("25 hourly effect ownership table consistent with the policy", HourlyOwnership);
        Run("26 steady state: unchanged state = 0 writes, 0 pending; cost measured", SteadyState);
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

    // ---------------- helpers ----------------
    static Dictionary<string, int> HostSave()
    {
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "ItemDay_Laptop", 14 }, { "ItemDay_TV (1)", 12 }, { "ItemDay_Old", 3 },   // Old expired at day 10
            { "DailyEvent0", 2 }, { "DailyEvent1", 0 }, { "EventDay", 4 }, { "GeneratedEvents", 1 },
            { "CurrentHotItem", 17 }, { "CurrentDayHotItem", 3 },
            { "Item_12", 5 }, { "Item_Laptop", 1 }, { "Money", 4200 }, { "BankBalance", 900 }, { "Storage_Item_7", 2 }, { "Locker_Item_3", 4 },
            { "CurrentCar", 1 }, { "GasTankLeftCar", 30 }, { "VanBought", 1 }, { "Story", 22 }, { "Debt", 400000 }, { "DaysInGame", 10 },
        };
    }

    static HostMirror NewHost(uint epoch, int profile, Dictionary<string, int> save, int days)
    {
        var h = new HostMirror(epoch); h.Rebase(profile, days, save); return h;
    }

    static byte[] Snap(HostMirror h) { var w = new NetDataWriter(); Expect(h.WriteSnapshot(w), "snapshot written"); return w.CopyData(); }
    static byte[] Delta(HostMirror h) { var w = new NetDataWriter(); int n; Expect(h.TakeDelta(w, out n), "delta written"); return w.CopyData(); }

    static NetDataReader Body(byte[] msg, MsgType expect) { var r = new NetDataReader(msg); Expect(r.GetByte() == (byte)expect, "msg type"); return r; }

    static MirrorApply ApplySnap(ClientMirror c, byte[] msg)
    {
        MirrorHeader h; Dictionary<string, int> v; bool nh;
        Expect(MirrorCodec.TryReadSnapshot(Body(msg, MsgType.WorldKeySnapshot), out h, out v), "snapshot decodes");
        return c.ApplySnapshot(h, v, out nh);
    }

    static MirrorApply ApplyDelta(ClientMirror c, byte[] msg, List<KeyWrite> writes)
    {
        MirrorHeader h; List<KeyChange> ch;
        Expect(MirrorCodec.TryReadDelta(Body(msg, MsgType.WorldKeyDelta), out h, out ch), "delta decodes");
        return c.ApplyDelta(h, ch, writes);
    }

    // Simulated client SPrefs store (FBPP memory of the sandbox).
    static void Execute(Dictionary<string, int> store, List<KeyWrite> writes)
    {
        foreach (var w in writes) { if (w.Delete) store.Remove(w.Key); else store[w.Key] = w.Value; }
    }

    static Dictionary<string, int> Family(Dictionary<string, int> store)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in store) if (KeyPolicy.MirroredFamily(kv.Key) != 0) d[kv.Key] = kv.Value;
        return d;
    }

    static int Reconcile(ClientMirror c, Dictionary<string, int> store)
    {
        var writes = new List<KeyWrite>();
        c.Plan(Family(store), 0, writes);
        Execute(store, writes);
        return writes.Count;
    }

    // ---------------- tests ----------------
    static void Classification()
    {
        Expect(KeyPolicy.MirroredFamily("ItemDay_Laptop") == KeyPolicy.FamItemDay, "loot respawn day");
        Expect(KeyPolicy.MirroredFamily("ItemDay_dropped") == 0, "dropped items never have a key");
        Expect(KeyPolicy.MirroredFamily("ItemDay_") == 0, "empty name");
        Expect(KeyPolicy.MirroredFamily("DailyEvent12") == KeyPolicy.FamDailyEvent && KeyPolicy.MirroredFamily("DailyEventX") == 0 && KeyPolicy.MirroredFamily("DailyEvent1234") == 0, "daily event index");
        Expect(KeyPolicy.MirroredFamily("EventDay") == KeyPolicy.FamEventDay && KeyPolicy.MirroredFamily("EventDay2") == 0, "exact keys");
        Expect(KeyPolicy.MirroredFamily("GeneratedEvents") != 0 && KeyPolicy.MirroredFamily("CurrentHotItem") != 0 && KeyPolicy.MirroredFamily("CurrentDayHotItem") != 0, "event/hot keys");
        foreach (var k in new[] { "Item_12", "Item_Laptop", "Money", "BankBalance", "BankIncrease", "BatonLife", "CrowbarLife", "Storage_Item_7", "Locker_Item_3",
                                  "CurrentCar", "GasTankLeftCar", "VanBought", "Story", "Debt", "RentTimer", "DaysInGame", "GameTime", "Day", "Week", "CurrentLocation", "Totally_Unknown", "", null })
            Expect(KeyPolicy.MirroredFamily(k) == 0, "not mirrored: " + k);
        Expect(KeyPolicy.OwnerOf("Item_12") == KeyOwner.PlayerOwn, "backpack count = player");
        Expect(KeyPolicy.OwnerOf("Item_Laptop") == KeyOwner.ReservedFuture, "unique loot flag reserved (W3)");
        Expect(KeyPolicy.OwnerOf("Locker_Item_3") == KeyOwner.MultiplayerOwned, "locker owned by the shared-locker subsystem (W3), never mirrored");
        Expect(KeyPolicy.OwnerOf("Storage_Item_7") == KeyOwner.MultiplayerOwned && KeyPolicy.OwnerOf("CurrentCar") == KeyOwner.MultiplayerOwned, "mod-owned");
        Expect(KeyPolicy.OwnerOf("Totally_Unknown") == KeyOwner.Unknown, "unknown");
        Expect(KeyPolicy.OwnerOf("ItemDay_Laptop") == KeyOwner.HostWorld, "world");
        int mirrored = 0; foreach (var f in KeyPolicy.Families) { if (f.Mirrored) { mirrored++; Expect(f.Id != 0 && f.Owner == KeyOwner.HostWorld && !string.IsNullOrEmpty(f.Evidence), "mirrored family " + f.Prefix + " documented"); } }
        Expect(mirrored == 6, "six mirrored families");
        string key; Expect(KeyPolicy.TryCompose(KeyPolicy.FamItemDay, "Vase", out key) && key == "ItemDay_Vase", "compose");
        Expect(!KeyPolicy.TryCompose(KeyPolicy.FamEventDay, "x", out key) && !KeyPolicy.TryCompose(99, "x", out key), "compose refuses bad suffix / family");
    }

    static void InitialSnapshot()
    {
        var host = NewHost(0xA1, 0, HostSave(), 10);
        Expect(host.Count == 8, "8 live world keys (expired ItemDay_Old and all non-world keys excluded), got " + host.Count);
        var client = new ClientMirror();
        Expect(ApplySnap(client, Snap(host)) == MirrorApply.Applied, "applied");
        var store = new Dictionary<string, int>(StringComparer.Ordinal) { { "Money", 50 }, { "Item_12", 1 }, { "ItemDay_Laptop", 9 }, { "DailyEvent0", 5 } };
        int n = Reconcile(client, store);
        Expect(store["ItemDay_Laptop"] == 14 && store["ItemDay_TV (1)"] == 12 && store["DailyEvent0"] == 2 && store["EventDay"] == 4 && store["CurrentHotItem"] == 17, "host values applied");
        Expect(store["Money"] == 50 && store["Item_12"] == 1, "player keys untouched");
        Expect(n == 8, "8 writes (" + n + ")");
        Expect(client.Header.Days == 10, "day count carried");
    }

    static void SnapshotValidation()
    {
        var host = NewHost(0xA1, 0, HostSave(), 10);
        var good = Snap(host);
        MirrorHeader h; Dictionary<string, int> v;
        // truncated
        var cut = new byte[good.Length - 3]; Array.Copy(good, cut, cut.Length);
        Expect(!MirrorCodec.TryReadSnapshot(Body(cut, MsgType.WorldKeySnapshot), out h, out v), "truncated rejected");
        // trailing bytes
        var longer = new byte[good.Length + 2]; Array.Copy(good, longer, good.Length);
        Expect(!MirrorCodec.TryReadSnapshot(Body(longer, MsgType.WorldKeySnapshot), out h, out v), "trailing bytes rejected");
        // wrong version
        var ver = (byte[])good.Clone(); ver[1] = 9;
        Expect(!MirrorCodec.TryReadSnapshot(Body(ver, MsgType.WorldKeySnapshot), out h, out v), "version rejected");
        // epoch 0 / generation 0
        var w = new NetDataWriter(); w.Put((byte)MsgType.WorldKeySnapshot); new MirrorHeader { Epoch = 0, Generation = 1 }.Write(w); w.Put((ushort)0);
        Expect(!MirrorCodec.TryReadSnapshot(Body(w.CopyData(), MsgType.WorldKeySnapshot), out h, out v), "epoch 0 rejected");
        // injected non-allowlisted key: family 1 with a suffix is fine, but a forged family id is not
        w = new NetDataWriter(); w.Put((byte)MsgType.WorldKeySnapshot); new MirrorHeader { Epoch = 5, Generation = 1, Rev = 1 }.Write(w); w.Put((ushort)1);
        w.Put((byte)77); w.Put("Money"); w.Put(1000000);
        Expect(!MirrorCodec.TryReadSnapshot(Body(w.CopyData(), MsgType.WorldKeySnapshot), out h, out v), "forged family rejected");
        w = new NetDataWriter(); w.Put((byte)MsgType.WorldKeySnapshot); new MirrorHeader { Epoch = 5, Generation = 1, Rev = 1 }.Write(w); w.Put((ushort)1);
        w.Put(KeyPolicy.FamItemDay); w.Put("dropped"); w.Put(99);
        Expect(!MirrorCodec.TryReadSnapshot(Body(w.CopyData(), MsgType.WorldKeySnapshot), out h, out v), "ItemDay_dropped rejected");
        w = new NetDataWriter(); w.Put((byte)MsgType.WorldKeySnapshot); new MirrorHeader { Epoch = 5, Generation = 1, Rev = 1 }.Write(w); w.Put((ushort)2);
        w.Put(KeyPolicy.FamItemDay); w.Put("A"); w.Put(20); w.Put(KeyPolicy.FamItemDay); w.Put("A"); w.Put(21);
        Expect(!MirrorCodec.TryReadSnapshot(Body(w.CopyData(), MsgType.WorldKeySnapshot), out h, out v), "duplicate key rejected");
        Expect(MirrorCodec.TryReadSnapshot(Body(good, MsgType.WorldKeySnapshot), out h, out v) && v.Count == 8, "intact snapshot accepted");
    }

    static void ProfileIsolation()
    {
        var p0 = NewHost(0xB0, 0, HostSave(), 10);
        var client = new ClientMirror();
        ApplySnap(client, Snap(p0));
        var other = HostSave(); other["ItemDay_Laptop"] = 99; other.Remove("ItemDay_TV (1)");
        var p1 = new HostMirror(0xB0); p1.Rebase(0, 10, HostSave()); p1.Rebase(1, 10, other);   // same epoch, profile 1, generation 2
        p1.Set("ItemDay_New", 30);
        var writes = new List<KeyWrite>();
        Expect(ApplyDelta(client, Delta(p1), writes) == MirrorApply.WrongHistory && writes.Count == 0, "delta of another profile refused");
        MirrorHeader h; Dictionary<string, int> v; bool nh;
        MirrorCodec.TryReadSnapshot(Body(Snap(p1), MsgType.WorldKeySnapshot), out h, out v);
        Expect(client.ApplySnapshot(h, v, out nh) == MirrorApply.Applied && nh && client.Header.Profile == 1, "snapshot of the other profile = new history");
        var store = new Dictionary<string, int>(StringComparer.Ordinal) { { "ItemDay_TV (1)", 12 }, { "Item_12", 4 } };
        Reconcile(client, store);
        Expect(!store.ContainsKey("ItemDay_TV (1)") && store["ItemDay_Laptop"] == 99 && store["ItemDay_New"] == 30 && store["Item_12"] == 4, "profile 0 world keys replaced, player keys untouched");
    }

    static void EpochIsolation()
    {
        var a = NewHost(0x111, 0, HostSave(), 10);
        var b = NewHost(0x222, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(a));
        b.Set("ItemDay_Laptop", 50);
        Expect(ApplyDelta(client, Delta(b), new List<KeyWrite>()) == MirrorApply.WrongHistory, "other epoch refused");
        int v; Expect(client.TryGet("ItemDay_Laptop", out v) && v == 14, "state unchanged");
    }

    static void Ordering()
    {
        var host = NewHost(0xC1, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        host.Set("ItemDay_A", 20); var d1 = Delta(host);
        host.Set("ItemDay_B", 21); var d2 = Delta(host);
        var writes = new List<KeyWrite>();
        Expect(ApplyDelta(client, d2, writes) == MirrorApply.Gap && writes.Count == 0, "rev+2 = gap");
        Expect(ApplyDelta(client, d1, writes) == MirrorApply.Applied && writes.Count == 1, "in order");
        Expect(ApplyDelta(client, d2, writes) == MirrorApply.Applied && writes.Count == 2, "next in order");
        Expect(new ClientMirror().ApplyDelta(host.Header, new List<KeyChange>(), null) == MirrorApply.NoSnapshot, "delta before snapshot");
    }

    static void Duplicates()
    {
        var host = NewHost(0xC2, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        host.Set("ItemDay_A", 20); var d1 = Delta(host);
        var writes = new List<KeyWrite>();
        ApplyDelta(client, d1, writes);
        writes.Clear();
        Expect(ApplyDelta(client, d1, writes) == MirrorApply.Duplicate && writes.Count == 0, "duplicate ignored");
        Expect(ApplySnap(client, Snap(host)) == MirrorApply.Applied, "repeated snapshot of the same rev accepted (idempotent)");
    }

    static void StaleSnapshot()
    {
        var host = NewHost(0xC3, 0, HostSave(), 10);
        var client = new ClientMirror();
        var old = Snap(host);
        host.Set("ItemDay_A", 20); ApplySnap(client, Snap(host));
        Expect(ApplySnap(client, old) == MirrorApply.Stale, "older snapshot of the same history ignored");
        int v; Expect(client.TryGet("ItemDay_A", out v) && v == 20, "newer state kept");
    }

    static void GapRecovery()
    {
        var host = NewHost(0xC4, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        host.Set("ItemDay_A", 20); Delta(host);                // lost
        host.Set("ItemDay_B", 21); var d2 = Delta(host);
        Expect(ApplyDelta(client, d2, new List<KeyWrite>()) == MirrorApply.Gap, "gap detected -> plugin sends WorldKeyResync");
        var rw = new NetDataWriter(); MirrorCodec.WriteResync(rw, client.Header, "gap");
        uint e, g, r; Expect(MirrorCodec.TryReadResync(Body(rw.CopyData(), MsgType.WorldKeyResync), out e, out g, out r) && e == 0xC4 && r == client.Header.Rev, "resync codec");
        Expect(ApplySnap(client, Snap(host)) == MirrorApply.Applied, "snapshot answers");
        int v; Expect(client.TryGet("ItemDay_A", out v) && v == 20 && client.TryGet("ItemDay_B", out v) && v == 21, "missed change recovered");
        host.Set("ItemDay_C", 22);
        Expect(ApplyDelta(client, Delta(host), new List<KeyWrite>()) == MirrorApply.Applied, "deltas continue after the snapshot");
    }

    static void MenuReturn()
    {
        var host = NewHost(0xD1, 0, HostSave(), 10);
        host.Set("ItemDay_Fresh", 15);   // host live change after the client's sandbox seed
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var seed = HostSave();          // sandbox file = host disk save at client start
        var store = new Dictionary<string, int>(seed, StringComparer.Ordinal);
        Reconcile(client, store);
        Expect(store["ItemDay_Fresh"] == 15, "live host value applied");
        store = new Dictionary<string, int>(seed, StringComparer.Ordinal);   // menu + Continue re-reads the sandbox file
        Expect(!store.ContainsKey("ItemDay_Fresh"), "sandbox reload lost the mirrored value");
        Reconcile(client, store);   // MainScene sceneLoaded
        Expect(store["ItemDay_Fresh"] == 15 && store["Money"] == 4200, "restored before Start; player keys of the sandbox untouched");
    }

    static void Rejoin()
    {
        var host = NewHost(0xD2, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        host.Set("ItemDay_A", 20); Delta(host);   // client disconnected, missed it
        MirrorHeader h; Dictionary<string, int> v; bool nh;
        MirrorCodec.TryReadSnapshot(Body(Snap(host), MsgType.WorldKeySnapshot), out h, out v);
        Expect(client.ApplySnapshot(h, v, out nh) == MirrorApply.Applied && !nh, "same history, newer rev");
        int x; Expect(client.TryGet("ItemDay_A", out x) && x == 20, "caught up");
    }

    static void TravelReloads()
    {
        var host = NewHost(0xD3, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var store = new Dictionary<string, int>(HostSave(), StringComparer.Ordinal);
        Reconcile(client, store);
        for (int trip = 0; trip < 20; trip++) Expect(Reconcile(client, store) == 0, "scene load " + trip + ": nothing to write");
        host.Set("ItemDay_Picked", 13);
        var writes = new List<KeyWrite>(); ApplyDelta(client, Delta(host), writes); Execute(store, writes);
        Expect(store["ItemDay_Picked"] == 13 && Reconcile(client, store) == 0, "delta applied live, next travel reconcile empty");
    }

    static void UnsavedHostChange()
    {
        var disk = HostSave();
        var host = NewHost(0xE1, 0, disk, 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var store = new Dictionary<string, int>(disk, StringComparer.Ordinal); Reconcile(client, store);
        host.Set("ItemDay_Vase", 13);   // host picks a vase, no checkpoint yet
        var writes = new List<KeyWrite>(); ApplyDelta(client, Delta(host), writes); Execute(store, writes);
        Expect(store["ItemDay_Vase"] == 13, "client sees the live (unsaved) host change");
        uint gen = host.Header.Generation;
        host.Rebase(0, 10, disk);       // host -> main menu without saving -> Continue: FBPP reloaded from disk
        Expect(host.Header.Generation == gen + 1, "new generation");
        Expect(ApplySnap(client, Snap(host)) == MirrorApply.Applied, "snapshot of the reloaded state");
        Reconcile(client, store);
        Expect(!store.ContainsKey("ItemDay_Vase"), "unsaved change rolled back on the client too (vase back)");
    }

    static void HostRestart()
    {
        var host = NewHost(0xE2, 0, HostSave(), 10);
        host.Set("ItemDay_Session", 18);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var store = new Dictionary<string, int>(HostSave(), StringComparer.Ordinal); Reconcile(client, store);
        var restarted = NewHost(0xE3, 0, HostSave(), 10);   // the change was never saved
        bool nh; MirrorHeader h; Dictionary<string, int> v;
        MirrorCodec.TryReadSnapshot(Body(Snap(restarted), MsgType.WorldKeySnapshot), out h, out v);
        Expect(client.ApplySnapshot(h, v, out nh) == MirrorApply.Applied && nh, "new host history");
        Reconcile(client, store);
        Expect(!store.ContainsKey("ItemDay_Session") && store["ItemDay_Laptop"] == 14, "follows the restarted host");
    }

    static void HostSaveUntouched()
    {
        var disk = HostSave(); var copy = new Dictionary<string, int>(disk);
        var host = NewHost(0xE4, 0, disk, 10);
        host.Set("ItemDay_X", 30); host.Remove("ItemDay_Laptop"); Delta(host); Snap(host);
        Expect(disk.Count == copy.Count, "host scan input never modified by the mirror");
        foreach (var kv in copy) Expect(disk[kv.Key] == kv.Value, "host value unchanged: " + kv.Key);
        // the only client message to the host is a resync request (no values)
        var w = new NetDataWriter(); MirrorCodec.WriteResync(w, host.Header, "x");
        Expect(w.Length == 14, "resync carries ids only (" + w.Length + " B)");
    }

    static void P2Isolation()
    {
        var host = NewHost(0xF1, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var p2 = new Dictionary<string, int>(StringComparer.Ordinal) { { "Item_12", 0 }, { "Item_5", 3 }, { "Money", 77 }, { "BankBalance", 5 }, { "BatonLife", 40 }, { "CrowbarLife", 60 }, { "Item_Laptop", 0 } };
        var store = new Dictionary<string, int>(p2, StringComparer.Ordinal);
        Reconcile(client, store);
        foreach (var kv in p2) Expect(store[kv.Key] == kv.Value, "P2 key untouched: " + kv.Key);
        var w = new List<KeyWrite>(); client.Plan(Family(store), 0, w);
        foreach (var x in w) Expect(KeyPolicy.MirroredFamily(x.Key) != 0, "plan writes world keys only");
        foreach (var kv in host.Values) Expect(KeyPolicy.OwnerOf(kv.Key) == KeyOwner.HostWorld, "snapshot carries world keys only: " + kv.Key);
    }

    static void StorageFleetIsolation()
    {
        var host = NewHost(0xF2, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var keep = new Dictionary<string, int>(StringComparer.Ordinal) { { "Storage_Item_7", 9 }, { "Locker_Item_3", 0 }, { "CurrentCar", 0 }, { "GasTankLeftCar", 55 }, { "VanBought", 0 }, { "Story", 3 }, { "Debt", 1 } };
        var store = new Dictionary<string, int>(keep, StringComparer.Ordinal);
        Reconcile(client, store);
        foreach (var kv in keep) Expect(store[kv.Key] == kv.Value, "untouched: " + kv.Key);
    }

    static void UnknownKeys()
    {
        var host = NewHost(0xF3, 0, HostSave(), 10);
        Expect(!host.Set("Money", 1) && !host.Set("Item_12", 1) && !host.Set("Locker_Item_3", 1) && !host.Set("Whatever", 1), "host mirror refuses");
        Expect(!host.HasPending, "nothing queued");
        var w = new NetDataWriter();
        Expect(!MirrorCodec.WriteDelta(w, host.Header, new List<KeyChange> { new KeyChange("Money", KeyOp.Set, 5) }), "codec refuses to encode a foreign key");
        var bad = new Dictionary<string, int> { { "Money", 5 } };
        Expect(!MirrorCodec.WriteSnapshot(new NetDataWriter(), host.Header, bad), "snapshot encoder refuses a foreign key");
        var client = new ClientMirror();
        Expect(!client.MarkLocal("Money"), "client never tracks foreign keys");
    }

    static void LocalMarkers()
    {
        var host = NewHost(0xA2, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var store = new Dictionary<string, int>(HostSave(), StringComparer.Ordinal); Reconcile(client, store);
        store["ItemDay_ClientPick"] = 13; Expect(client.MarkLocal("ItemDay_ClientPick"), "client pick marked");   // Pickupable.SetDayRespawn on the client
        store["ItemDay_Sneaky"] = 50;   // a stale/unknown client value that the client did not legitimately write
        Reconcile(client, store);
        Expect(store["ItemDay_ClientPick"] == 13, "own pick kept (no duplication by re-picking after travel)");
        Expect(!store.ContainsKey("ItemDay_Sneaky"), "unmarked client value removed (host authoritative)");
        host.Set("ItemDay_ClientPick", 16);
        var writes = new List<KeyWrite>(); ApplyDelta(client, Delta(host), writes); Execute(store, writes);
        Expect(store["ItemDay_ClientPick"] == 16, "host value wins once it exists");
        host.Remove("ItemDay_ClientPick");
        writes.Clear(); ApplyDelta(client, Delta(host), writes); Execute(store, writes);
        Expect(store.ContainsKey("ItemDay_ClientPick"), "host delete does not erase the client's own marker");
        store["ItemDay_Laptop"] = 3;   // client tries to bring back a host-hidden object
        Reconcile(client, store);
        Expect(store["ItemDay_Laptop"] == 14, "stale client value cannot overwrite the host value");
        var c2 = new ClientMirror();
        for (int i = 0; i < ClientMirror.MaxLocal + 10; i++) c2.MarkLocal("ItemDay_x" + i);
        Expect(c2.LocalCount == ClientMirror.MaxLocal && c2.LocalDropped == 10, "local marker set bounded");
    }

    static void Days()
    {
        Expect(KeyPolicy.IsLive(KeyPolicy.FamItemDay, 11, 10) && !KeyPolicy.IsLive(KeyPolicy.FamItemDay, 10, 10) && KeyPolicy.IsLive(KeyPolicy.FamDailyEvent, 0, 10), "liveness = Pickupable condition daysInGame < value");
        var host = NewHost(0xA3, 0, HostSave(), 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        int hostBefore = host.Count, clientBefore = client.Count;
        host.SetDays(12);   // ItemDay_TV (1) = 12 expires
        Expect(host.HasPending && host.PendingCount == 0, "days-only change pending");
        var writes = new List<KeyWrite>();
        Expect(ApplyDelta(client, Delta(host), writes) == MirrorApply.Applied && writes.Count == 0, "days-only delta applied without key writes");
        Expect(client.Header.Days == 12 && host.Count == hostBefore - 1 && client.Count == clientBefore - 1, "both pruned the expired marker");
        int v; Expect(!client.TryGet("ItemDay_TV (1)", out v), "expired marker gone on the client");
        Expect(!host.Set("ItemDay_Past", 12), "a marker that is already expired is not mirrored");
    }

    static void Coalescing()
    {
        var host = NewHost(0xA4, 0, HostSave(), 10);
        for (int i = 0; i < 100; i++) host.Set("ItemDay_Busy", 20 + (i % 3));
        Expect(host.PendingCount == 1, "one pending change");
        var w = new NetDataWriter(); int n; host.TakeDelta(w, out n);
        Expect(n == 1 && !host.HasPending, "flushed as one change");
        Expect(!host.Set("ItemDay_Busy", 20 + (99 % 3)), "same value again: no change");
    }

    static void HostReconcile()
    {
        var save = HostSave();
        var host = NewHost(0xA5, 0, save, 10);
        var scan = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in save) if (KeyPolicy.MirroredFamily(kv.Key) != 0) scan[kv.Key] = kv.Value;
        Expect(host.Reconcile(0, scan) == 0 && !host.HasPending, "unchanged scan: nothing");
        scan["DailyEvent0"] = 3; scan.Remove("DailyEvent1"); scan["DailyEvent2"] = 1; scan["ItemDay_Laptop"] = 99;
        int n = host.Reconcile(KeyPolicy.FamDailyEvent, scan);
        Expect(n == 3, "daily family: 2 sets + 1 delete (" + n + ")");
        Expect(host.Values.Contains(new KeyValuePair<string, int>("ItemDay_Laptop", 14)), "ItemDay change outside the reconciled family not taken yet");
        Expect(host.Reconcile(0, scan) == 1, "full audit takes the remaining change");
    }

    static void SizeLimits()
    {
        var big = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < MirrorCodec.MaxEntries + 1; i++) big["ItemDay_obj" + i] = 100;
        var host = new HostMirror(1); host.Rebase(0, 10, big);
        Expect(!host.WriteSnapshot(new NetDataWriter()), "entry limit");
        var huge = new Dictionary<string, int>(StringComparer.Ordinal);
        string longName = new string('x', KeyPolicy.MaxSuffixChars - 6);
        for (int i = 0; i < 3000; i++) huge["ItemDay_" + longName + i.ToString("00000")] = 100;
        var h2 = new HostMirror(1); h2.Rebase(0, 10, huge);
        Expect(!h2.WriteSnapshot(new NetDataWriter()), "payload limit (" + MirrorCodec.MaxPayloadBytes + " B)");
        Expect(KeyPolicy.MirroredFamily("ItemDay_" + new string('y', KeyPolicy.MaxSuffixChars + 1)) == 0, "suffix length limit");
        var normal = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < 2000; i++) normal["ItemDay_House " + (i / 40) + "/Loot " + i] = 100;
        var h3 = new HostMirror(1); h3.Rebase(0, 10, normal);
        var w = new NetDataWriter(); Expect(h3.WriteSnapshot(w), "2000 markers fit");
        Console.WriteLine("      snapshot 2000 loot markers: " + w.Length + " B (" + (w.Length / 2000.0).ToString("0.0") + " B/key)");
    }

    static void Fuzz()
    {
        var rnd = new Random(1234);
        var host = NewHost(0xA6, 0, HostSave(), 10);
        var snap = Snap(host); host.Set("ItemDay_Z", 40); var delta = Delta(host);
        int accepted = 0;
        for (int i = 0; i < 20000; i++)
        {
            var src = (i & 1) == 0 ? snap : delta;
            var b = (byte[])src.Clone();
            int flips = 1 + rnd.Next(4);
            for (int f = 0; f < flips; f++) b[1 + rnd.Next(b.Length - 1)] = (byte)rnd.Next(256);
            if (rnd.Next(5) == 0) { var cut = new byte[1 + rnd.Next(b.Length)]; Array.Copy(b, cut, cut.Length); b = cut; }
            try
            {
                var r = new NetDataReader(b); r.GetByte();
                MirrorHeader h; Dictionary<string, int> v = null; List<KeyChange> ch = null;
                bool ok = (i & 1) == 0 ? MirrorCodec.TryReadSnapshot(r, out h, out v) : MirrorCodec.TryReadDelta(r, out h, out ch);
                if (ok)
                {
                    accepted++;
                    if (v != null) foreach (var k in v.Keys) Expect(KeyPolicy.MirroredFamily(k) != 0, "accepted foreign key " + k);
                }
            }
            catch (Exception ex) { throw new Exception("decoder threw: " + ex.GetType().Name); }
        }
        Console.WriteLine("      20000 mutated messages: " + accepted + " decodable (all keys allowlisted), 0 exceptions");
    }

    static void HourlyOwnership()
    {
        foreach (var e in HourlyEffects.All) Expect(e.Owner != KeyOwner.Unknown && !string.IsNullOrEmpty(e.Original), "owner for " + e.Name);
        int mirrored = 0, w7 = 0;
        foreach (var e in HourlyEffects.All) { if (e.Handling == EffectHandling.MirroredHostAuthoritative) mirrored++; if (e.Handling == EffectHandling.UnchangedW7) w7++; }
        Expect(mirrored == 2 && w7 == 3, "daily events + hot item mirrored; rent, bank interest, fuel charge left to W7");
        Expect(KeyPolicy.MirroredFamily("EventDay") != 0 && KeyPolicy.MirroredFamily("CurrentHotItem") != 0, "mirrored effects have mirrored keys");
        Expect(KeyPolicy.MirroredFamily("RentTimer") == 0 && KeyPolicy.MirroredFamily("BankBalance") == 0, "economy keys not mirrored (W7)");
    }

    static void SteadyState()
    {
        var save = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < 1500; i++) save["ItemDay_Loot" + i] = 20;
        for (int i = 0; i < 30; i++) save["DailyEvent" + i] = i % 3;
        for (int i = 0; i < 1500; i++) save["Item_" + i] = 1;   // player keys mixed in
        var host = NewHost(0xA7, 0, save, 10);
        var client = new ClientMirror(); ApplySnap(client, Snap(host));
        var store = new Dictionary<string, int>(save, StringComparer.Ordinal);
        Reconcile(client, store);
        var fam = Family(store);
        var writes = new List<KeyWrite>(4);
        var scan = new Dictionary<string, int>(fam, StringComparer.Ordinal);
        int gc = GC.CollectionCount(0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) { writes.Clear(); client.Plan(fam, 0, writes); Expect(writes.Count == 0, "no writes"); }
        double planMs = sw.Elapsed.TotalMilliseconds / 100;
        sw.Restart();
        for (int i = 0; i < 100; i++) Expect(host.Reconcile(0, scan) == 0 && !host.HasPending, "no pending");
        double recMs = sw.Elapsed.TotalMilliseconds / 100;
        sw.Restart();
        int matches = 0;
        for (int i = 0; i < 100; i++) foreach (var k in store.Keys) if (KeyPolicy.MirroredFamily(k) != 0) matches++;
        double classifyUs = sw.Elapsed.TotalMilliseconds * 1000 / (100.0 * store.Count);
        Console.WriteLine("      1530 world keys: client plan " + planMs.ToString("0.000") + " ms, host audit " + recMs.ToString("0.000") + " ms, classify " + classifyUs.ToString("0.000") + " us/key, gen0 GCs during loops: " + (GC.CollectionCount(0) - gc) + " (.NET Framework, not Unity Mono)");
        Expect(planMs < 20 && recMs < 20, "bounded cost");
    }
}
