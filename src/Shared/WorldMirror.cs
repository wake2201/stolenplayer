// M5 W2: host-authoritative world-key mirror. Unity-free (unit-testable); the Unity side is src/Plugin/WorldKeyMirror.cs.
//
// The original game rebuilds its persistent world in Start() from SPrefs keys (VO: Pickupable.<Start>d__68 reads
// ItemDay_<name> after a 0.05 s realtime wait; DailyEventManager.<Start>d__9 reads EventDay/GeneratedEvents/DailyEvent<i>;
// HotItemManager.InitializeThis reads CurrentDayHotItem/CurrentHotItem; Inventory.ReloadStats (from SaveGame.LoadGame)
// reads DaysInGame). The host mirrors an explicit allowlist of those key families (KeyPolicy) into the client's sandbox
// SPrefs memory: snapshot on connect / host save reload, ordered deltas afterwards, plus the host's live day count.
// Rules: unknown keys are never mirrored (both sides validate); the host's live (possibly unsaved) value wins; a key the
// client's own game wrote (its own loot pick) stays local until the host has a value; a client key that came from the
// host/seed and is no longer in the host's state is removed (host reloaded without saving, other profile, new game).
// The client never writes the host save (sandbox only, SaveGuard) - the mirror cannot make an unsaved host change durable.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum KeyOwner : byte
    {
        Unknown = 0,
        HostWorld = 1,          // shared world progression, host save is the only durable copy
        PlayerOwn = 2,          // each player's own value (P1: host save; P2: host-owned record via Player2Sync)
        MultiplayerOwned = 3,   // already owned by a mod subsystem (CarBox, fleet, region clocks, calendar)
        InstanceLocal = 4,      // per-instance position/session meta, never shared
        ReservedFuture = 5      // ownership reserved for a planned milestone (no generic copying)
    }

    public sealed class KeyFamily
    {
        public readonly byte Id;            // wire id for mirrored families (0 = not mirrored)
        public readonly string Prefix;      // key = Prefix + suffix
        public readonly bool Exact;         // suffix must be empty
        public readonly KeyOwner Owner;
        public readonly bool Mirrored;
        public readonly string Milestone;   // where the family is (or will be) handled
        public readonly string Evidence;    // writers -> readers (VO)
        readonly Func<string, int, bool> _suffixOk;   // (key, suffixStart) -> valid

        public KeyFamily(byte id, string prefix, bool exact, KeyOwner owner, bool mirrored, string milestone, string evidence, Func<string, int, bool> suffixOk)
        { Id = id; Prefix = prefix; Exact = exact; Owner = owner; Mirrored = mirrored; Milestone = milestone; Evidence = evidence; _suffixOk = suffixOk; }

        // Allocation-free match.
        public bool Matches(string key)
        {
            if (key == null || !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            if (Exact) return key.Length == Prefix.Length;
            return key.Length > Prefix.Length && (_suffixOk == null || _suffixOk(key, Prefix.Length));
        }
    }

    public static class KeyPolicy
    {
        public const int MaxSuffixChars = 120;
        public const byte FamItemDay = 1, FamDailyEvent = 2, FamEventDay = 3, FamGeneratedEvents = 4, FamHotItem = 5, FamHotItemDay = 6;

        static bool Digits(string k, int from, int max)
        {
            int n = k.Length - from; if (n < 1 || n > max) return false;
            for (int i = from; i < k.Length; i++) if (k[i] < '0' || k[i] > '9') return false;
            return true;
        }
        static bool ObjectName(string k, int from)
        {
            int n = k.Length - from; if (n < 1 || n > MaxSuffixChars) return false;
            if (n == 7 && string.CompareOrdinal(k, from, "dropped", 0, 7) == 0) return false;   // dropped items never get a key (SetDayRespawn IL_0062)
            for (int i = from; i < k.Length; i++) if (k[i] < ' ') return false;
            return true;
        }
        static bool NotDigits(string k, int from) { return !Digits(k, from, 9) && ObjectName(k, from); }

        // Order matters: the first matching family classifies a key. Mirrored families first.
        public static readonly KeyFamily[] Families =
        {
            // ---- mirrored in W2 (host -> client) ----
            new KeyFamily(FamItemDay, "ItemDay_", false, KeyOwner.HostWorld, true, "W2",
                "Pickupable.SetDayRespawn (only writer, = daysInGame+2..5) -> Pickupable.<Start>d__68 IL_0475..04c8 destroys while daysInGame < value", ObjectName),
            new KeyFamily(FamDailyEvent, "DailyEvent", false, KeyOwner.HostWorld, true, "W2",
                "DailyEventManager.GenerateMonthEvents (Random) / SetTodayAsBurglarDay -> DailyEventManager.LoadEvents (scene start)", (k, f) => Digits(k, f, 3)),
            new KeyFamily(FamEventDay, "EventDay", true, KeyOwner.HostWorld, true, "W2",
                "DailyEventManager.SetNextDay (day rollover) -> DailyEventManager.<Start>d__9", null),
            new KeyFamily(FamGeneratedEvents, "GeneratedEvents", true, KeyOwner.HostWorld, true, "W2",
                "DailyEventManager.<Start>d__9 (sets 1, then GenerateMonthEvents) -> same", null),
            new KeyFamily(FamHotItem, "CurrentHotItem", true, KeyOwner.HostWorld, true, "W2",
                "HotItemManager.NewRandomItem (Random) -> HotItemManager.InitializeThis", null),
            new KeyFamily(FamHotItemDay, "CurrentDayHotItem", true, KeyOwner.HostWorld, true, "W2",
                "HotItemManager.NewRandomItem -> HotItemManager.InitializeThis", null),

            // ---- classified, NOT mirrored (documented owner) ----
            new KeyFamily(0, "Storage_Item_", false, KeyOwner.MultiplayerOwned, false, "M4.3.2b CarBoxSync", "ItemID.Save_Item_Amount; shared CarBox is host-authoritative", null),
            new KeyFamily(0, "Locker_Item_", false, KeyOwner.MultiplayerOwned, false, "M5 W3 CarBoxSync (storage kind 1)", "ItemID.Save_Item_Amount; shared hideout locker is host-authoritative (lease + host transactions)", null),
            new KeyFamily(0, "Item_Discovered_", false, KeyOwner.PlayerOwn, false, "-", "ItemID.DiscoverItem (player knowledge)", null),
            new KeyFamily(0, "Item_Value_", false, KeyOwner.PlayerOwn, false, "-", "ItemID.DiscoverValue (player knowledge)", null),
            new KeyFamily(0, "Item_", false, KeyOwner.PlayerOwn, false, "M4.3.3 Player2Sync", "Item_<numeric id> backpack count: ItemID.Save_Item_Amount", (k, f) => Digits(k, f, 9)),
            new KeyFamily(0, "Item_", false, KeyOwner.ReservedFuture, false, "W3 shared loot", "Item_<objectName>=1 on EVERY pick (Pickupable.<PickingItem>/ItemID.Item_Picked); read for destroy_if_was_picked; same prefix as backpack counts", NotDigits),
            new KeyFamily(0, "Money", true, KeyOwner.PlayerOwn, false, "M4.3.3", "PlayerScript.moneyAm", null),
            new KeyFamily(0, "BankBalance", true, KeyOwner.PlayerOwn, false, "M4.3.3 / W7 interest", "Darkbank, DayNightTime.Update interest", null),
            new KeyFamily(0, "BankIncrease", true, KeyOwner.PlayerOwn, false, "W7", "Darkbank, DayNightTime.Update", null),
            new KeyFamily(0, "BatonLife", true, KeyOwner.PlayerOwn, false, "M4.3.3", "tool wear", null),
            new KeyFamily(0, "CrowbarLife", true, KeyOwner.PlayerOwn, false, "M4.3.3", "tool wear", null),
            new KeyFamily(0, "Story", true, KeyOwner.HostWorld, false, "W6 shared story", "StoryManager.story; InitiateStory side effects (calls, debt) need W6", null),
            new KeyFamily(0, "StoryBackup", true, KeyOwner.HostWorld, false, "W6", "SaveGameNewSystem", null),
            new KeyFamily(0, "Debt", true, KeyOwner.HostWorld, false, "W7 household economy", "PlayerScript.debtAm, story deduct", null),
            new KeyFamily(0, "RentTimer", true, KeyOwner.HostWorld, false, "W7", "SaveGame.timeTillRentPayment (DayNightTime hourly)", null),
            new KeyFamily(0, "DaysInGame", true, KeyOwner.HostWorld, false, "W2 live value (snapshot header)", "Inventory.daysInGame; key written only by Inventory.SaveStats", null),
            new KeyFamily(0, "GameTime", true, KeyOwner.MultiplayerOwned, false, "M3.1 region clocks", "Enviro universal time", null),
            new KeyFamily(0, "Day", true, KeyOwner.MultiplayerOwned, false, "M3.1 calendar", "GameManager.currentDayInt", null),
            new KeyFamily(0, "Week", true, KeyOwner.MultiplayerOwned, false, "M3.1 calendar", "GameManager.currentWeek", null),
            new KeyFamily(0, "TotalHoursPassed", true, KeyOwner.PlayerOwn, false, "-", "PlayerScript.totalHoursPassed", null),
            new KeyFamily(0, "NewDay", true, KeyOwner.InstanceLocal, false, "-", "DayNightTime once-per-midnight latch", null),
            new KeyFamily(0, "CurrentCar", true, KeyOwner.MultiplayerOwned, false, "M4.2 fleet", "PlayerScript.SaveCurrentCar", null),
            new KeyFamily(0, "GasTankLeftCar", true, KeyOwner.MultiplayerOwned, false, "M4.2 fleet (vpark fuel)", "SaveGame.gasTankLeft", null),
            new KeyFamily(0, "VanBought", true, KeyOwner.MultiplayerOwned, false, "M4.1 fleet unlock", "Tutorial.BuyVan (parcel string)", null),
            new KeyFamily(0, "TrunkItem", true, KeyOwner.MultiplayerOwned, false, "vehicle trunk (future)", "CarController.LoadTrunkItem", null),
            new KeyFamily(0, "CurrentLocation", true, KeyOwner.InstanceLocal, false, "-", "GameManager.Select*", null),
            new KeyFamily(0, "Saved_Pos", true, KeyOwner.InstanceLocal, false, "-", "travel vs save-point spawn", null),
            new KeyFamily(0, "MadisonPoint", true, KeyOwner.InstanceLocal, false, "-", "GameManager.SetClosestPointSpawn", null),
            new KeyFamily(0, "HillvillePoint", true, KeyOwner.InstanceLocal, false, "-", "GameManager.SetClosestPointSpawn", null),
            new KeyFamily(0, "RandomVan", true, KeyOwner.InstanceLocal, false, "-", "random per travel", null),
            new KeyFamily(0, "Medkit", true, KeyOwner.InstanceLocal, false, "vehicle (future)", "single-use car medkit", null),
        };

        public static KeyFamily Classify(string key)
        {
            for (int i = 0; i < Families.Length; i++) if (Families[i].Matches(key)) return Families[i];
            return null;
        }

        public static KeyOwner OwnerOf(string key) { var f = Classify(key); return f != null ? f.Owner : KeyOwner.Unknown; }

        // Mirrored family id of a key, 0 if the key must not be mirrored. Allocation-free.
        public static byte MirroredFamily(string key)
        {
            for (int i = 0; i < Families.Length; i++)
            {
                var f = Families[i];
                if (!f.Mirrored) break;   // mirrored families come first
                if (f.Matches(key)) return f.Id;
            }
            return 0;
        }

        public static KeyFamily Family(byte id)
        {
            for (int i = 0; i < Families.Length; i++) if (Families[i].Mirrored && Families[i].Id == id) return Families[i];
            return null;
        }

        public static bool TryCompose(byte fam, string suffix, out string key)
        {
            key = null;
            var f = Family(fam);
            if (f == null || suffix == null || suffix.Length > MaxSuffixChars) return false;
            key = f.Prefix + suffix;
            return f.Matches(key);
        }

        public static string Suffix(byte fam, string key) { var f = Family(fam); return f == null ? null : key.Substring(f.Prefix.Length); }

        // A loot marker is relevant only while it still hides the object (Pickupable: daysInGame < value). Expired markers are
        // dropped from the mirror on both sides with the same day count (no traffic); the object is back in both worlds.
        public static bool IsLive(byte fam, int value, int days) { return fam != FamItemDay || value > days; }
    }

    // ---- hourly / daily side effects of the original (DayNightTime.Update, SaveGame.Update), ownership after W2 ----
    public enum EffectHandling : byte { MirroredHostAuthoritative = 1, AlignedLiveValue = 2, LocalPresentation = 3, UnchangedW7 = 4, ExistingSubsystem = 5 }

    public sealed class HourlyEffect
    {
        public readonly string Name, Original; public readonly KeyOwner Owner; public readonly EffectHandling Handling; public readonly string Note;
        public HourlyEffect(string name, string original, KeyOwner owner, EffectHandling h, string note) { Name = name; Original = original; Owner = owner; Handling = h; Note = note; }
    }

    public static class HourlyEffects
    {
        public static readonly HourlyEffect[] All =
        {
            new HourlyEffect("day count (loot respawn)", "DayNightTime.Update IL_0201..0208 Inventory.daysInGame++", KeyOwner.HostWorld, EffectHandling.AlignedLiveValue, "client follows the host's live value (snapshot/delta header)"),
            new HourlyEffect("daily NPC events", "DayNightTime.Update -> DailyEventManager.SetNextDay/GenerateMonthEvents", KeyOwner.HostWorld, EffectHandling.MirroredHostAuthoritative, "client keys re-asserted from the host after its own rollover; the client's live NPCs keep their event until the next scene load (W8)"),
            new HourlyEffect("hot item of the day", "HotItemManager.NewRandomItem", KeyOwner.HostWorld, EffectHandling.MirroredHostAuthoritative, "keys re-asserted; live shop flag refreshes at the next scene load"),
            new HourlyEffect("weekday / week", "DayNightTime.Update currentDayInt/currentWeek", KeyOwner.MultiplayerOwned, EffectHandling.ExistingSubsystem, "M3.1 'calendar' (host value written to the client)"),
            new HourlyEffect("hour of day / NPC schedules / lights", "Enviro, AI.Update currentHour", KeyOwner.MultiplayerOwned, EffectHandling.ExistingSubsystem, "M3.1 region clocks; NPC simulation stays local until W8"),
            new HourlyEffect("rent timer + rent charge", "DayNightTime.Update timeTillRentPayment--, SaveGame.Update moneyAm -= rent", KeyOwner.HostWorld, EffectHandling.UnchangedW7, "still runs on both instances (client charges Player 2's money): W7"),
            new HourlyEffect("bank interest", "DayNightTime.Update (day rollover) BankBalance", KeyOwner.PlayerOwn, EffectHandling.UnchangedW7, "per player in practice; P2 balance in the host record; double-tick risk: W7"),
            new HourlyEffect("fuel refill charge", "SaveGame.Update gasTankLeft empty -> 60, moneyAm -= 70", KeyOwner.MultiplayerOwned, EffectHandling.UnchangedW7, "per car fuel already mirrored by the fleet; charge ownership: W7"),
            new HourlyEffect("ability cooldowns", "AbilitiesManager.HourPassed", KeyOwner.PlayerOwn, EffectHandling.LocalPresentation, "each player's own skills"),
            new HourlyEffect("total hours played", "PlayerScript.totalHoursPassed++", KeyOwner.PlayerOwn, EffectHandling.LocalPresentation, "statistic"),
        };
    }

    // ---- wire ----
    public struct MirrorHeader
    {
        public const byte Version = 1;
        public uint Epoch;        // host process history (random per host start)
        public int Profile;       // host save profile (PlayerPrefs "Profile")
        public uint Generation;   // host save load counter (new on every host save reload / profile change / new game)
        public uint Rev;          // revision within the generation
        public int Days;          // host Inventory.daysInGame (live)

        public void Write(NetDataWriter w) { w.Put(Version); w.Put(Epoch); w.Put(Profile); w.Put(Generation); w.Put(Rev); w.Put(Days); }
        public static bool TryRead(NetDataReader r, out MirrorHeader h)
        {
            h = default(MirrorHeader); byte v;
            if (!r.TryGetByte(out v) || v != Version) return false;
            return r.TryGetUInt(out h.Epoch) && r.TryGetInt(out h.Profile) && r.TryGetUInt(out h.Generation) && r.TryGetUInt(out h.Rev) && r.TryGetInt(out h.Days)
                && h.Epoch != 0 && h.Generation != 0 && h.Days >= 0;
        }
        public bool SameHistory(MirrorHeader o) { return o.Epoch == Epoch && o.Profile == Profile && o.Generation == Generation; }
        public override string ToString() { return "epoch=" + Epoch.ToString("X8") + " profile=" + Profile + " gen=" + Generation + " rev=" + Rev + " days=" + Days; }
    }

    public enum KeyOp : byte { Set = 1, Delete = 2 }

    public struct KeyChange
    {
        public string Key; public KeyOp Op; public int Value;
        public KeyChange(string key, KeyOp op, int value) { Key = key; Op = op; Value = value; }
        public override string ToString() { return (Op == KeyOp.Set ? "set " : "del ") + Key + (Op == KeyOp.Set ? "=" + Value : ""); }
    }

    public static class MirrorCodec
    {
        public const int MaxEntries = 8192, MaxPayloadBytes = 256 * 1024;

        public static bool WriteSnapshot(NetDataWriter w, MirrorHeader h, ICollection<KeyValuePair<string, int>> values)
        {
            if (values.Count > MaxEntries) return false;
            w.Put((byte)MsgType.WorldKeySnapshot); h.Write(w); w.Put((ushort)values.Count);
            foreach (var kv in values)
            {
                byte fam = KeyPolicy.MirroredFamily(kv.Key);
                if (fam == 0) return false;   // never serialize a non-allowlisted key
                w.Put(fam); w.Put(KeyPolicy.Suffix(fam, kv.Key)); w.Put(kv.Value);
                if (w.Length > MaxPayloadBytes) return false;
            }
            return true;
        }

        // Reader positioned after the MsgType byte. Any entry outside the allowlist or limits rejects the whole message.
        public static bool TryReadSnapshot(NetDataReader r, out MirrorHeader h, out Dictionary<string, int> values)
        {
            try { return ReadSnapshot(r, out h, out values); }
            catch (Exception) { h = default(MirrorHeader); values = null; return false; }   // e.g. invalid UTF-8 (DecoderFallbackException)
        }

        static bool ReadSnapshot(NetDataReader r, out MirrorHeader h, out Dictionary<string, int> values)
        {
            values = null; ushort n;
            if (r.AvailableBytes > MaxPayloadBytes || !MirrorHeader.TryRead(r, out h) || !r.TryGetUShort(out n) || n > MaxEntries) { h = default(MirrorHeader); return false; }
            var d = new Dictionary<string, int>(n, StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                byte fam; string suffix; int v; string key;
                if (!r.TryGetByte(out fam) || !r.TryGetString(out suffix) || !r.TryGetInt(out v) || !KeyPolicy.TryCompose(fam, suffix, out key) || d.ContainsKey(key)) return false;
                d[key] = v;
            }
            if (r.AvailableBytes != 0) return false;
            values = d;
            return true;
        }

        public static bool WriteDelta(NetDataWriter w, MirrorHeader h, IList<KeyChange> changes)
        {
            if (changes.Count > MaxEntries) return false;
            w.Put((byte)MsgType.WorldKeyDelta); h.Write(w); w.Put((ushort)changes.Count);
            for (int i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                byte fam = KeyPolicy.MirroredFamily(c.Key);
                if (fam == 0) return false;
                w.Put(fam); w.Put(KeyPolicy.Suffix(fam, c.Key)); w.Put((byte)c.Op); w.Put(c.Value);
                if (w.Length > MaxPayloadBytes) return false;
            }
            return true;
        }

        public static bool TryReadDelta(NetDataReader r, out MirrorHeader h, out List<KeyChange> changes)
        {
            try { return ReadDelta(r, out h, out changes); }
            catch (Exception) { h = default(MirrorHeader); changes = null; return false; }
        }

        static bool ReadDelta(NetDataReader r, out MirrorHeader h, out List<KeyChange> changes)
        {
            changes = null; ushort n;
            if (r.AvailableBytes > MaxPayloadBytes || !MirrorHeader.TryRead(r, out h) || !r.TryGetUShort(out n) || n > MaxEntries) { h = default(MirrorHeader); return false; }
            var l = new List<KeyChange>(n);
            for (int i = 0; i < n; i++)
            {
                byte fam, op; string suffix; int v; string key;
                if (!r.TryGetByte(out fam) || !r.TryGetString(out suffix) || !r.TryGetByte(out op) || !r.TryGetInt(out v) || (op != 1 && op != 2) || !KeyPolicy.TryCompose(fam, suffix, out key)) return false;
                l.Add(new KeyChange(key, (KeyOp)op, v));
            }
            if (r.AvailableBytes != 0) return false;
            changes = l;
            return true;
        }

        public static void WriteResync(NetDataWriter w, MirrorHeader known, string why)
        {
            w.Put((byte)MsgType.WorldKeyResync); w.Put(MirrorHeader.Version); w.Put(known.Epoch); w.Put(known.Generation); w.Put(known.Rev);
        }
        public static bool TryReadResync(NetDataReader r, out uint epoch, out uint gen, out uint rev)
        {
            epoch = gen = rev = 0; byte v;
            if (r.AvailableBytes != 13) return false;
            return r.TryGetByte(out v) && v == MirrorHeader.Version && r.TryGetUInt(out epoch) && r.TryGetUInt(out gen) && r.TryGetUInt(out rev);
        }
    }

    // ---- host ----
    public sealed class HostMirror
    {
        readonly Dictionary<string, int> _values = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly List<KeyChange> _pending = new List<KeyChange>();
        readonly List<string> _tmp = new List<string>();
        MirrorHeader _h;
        bool _headerDirty;

        public HostMirror(uint epoch) { _h.Epoch = epoch == 0 ? 1u : epoch; }
        public MirrorHeader Header { get { return _h; } }
        public bool Based { get { return _h.Generation != 0; } }
        public int Count { get { return _values.Count; } }
        public bool HasPending { get { return _pending.Count > 0 || _headerDirty; } }
        public int PendingCount { get { return _pending.Count; } }
        public ICollection<KeyValuePair<string, int>> Values { get { return _values; } }
        public long Rebases { get; private set; }

        // New base after a host save load/reload, profile change or new game: full state replaced, new generation.
        public void Rebase(int profile, int days, IEnumerable<KeyValuePair<string, int>> scanned)
        {
            _values.Clear(); _pending.Clear(); _headerDirty = false;
            _h.Profile = profile; _h.Days = Math.Max(0, days); _h.Generation++; _h.Rev = 1; Rebases++;
            foreach (var kv in scanned)
            {
                byte fam = KeyPolicy.MirroredFamily(kv.Key);
                if (fam != 0 && KeyPolicy.IsLive(fam, kv.Value, _h.Days)) _values[kv.Key] = kv.Value;
            }
        }

        // Live change of one key observed on the host (writer hook). Unknown keys are refused.
        public bool Set(string key, int value)
        {
            byte fam = KeyPolicy.MirroredFamily(key);
            if (fam == 0 || !Based) return false;
            int cur;
            bool has = _values.TryGetValue(key, out cur);
            if (!KeyPolicy.IsLive(fam, value, _h.Days)) { if (has) { _values.Remove(key); Queue(new KeyChange(key, KeyOp.Delete, 0)); return true; } return false; }
            if (has && cur == value) return false;
            _values[key] = value;
            Queue(new KeyChange(key, KeyOp.Set, value));
            return true;
        }

        public bool Remove(string key)
        {
            if (!Based || !_values.Remove(key)) return false;
            Queue(new KeyChange(key, KeyOp.Delete, 0));
            return true;
        }

        // Full comparison of one family (or all when fam == 0) with a scan of the host's SPrefs memory: sets + deletes.
        public int Reconcile(byte fam, IDictionary<string, int> scanned)
        {
            if (!Based) return 0;
            int n = 0;
            foreach (var kv in scanned)
            {
                byte f = KeyPolicy.MirroredFamily(kv.Key);
                if (f == 0 || (fam != 0 && f != fam)) continue;
                if (Set(kv.Key, kv.Value)) n++;
            }
            _tmp.Clear();
            foreach (var k in _values.Keys)
            {
                byte f = KeyPolicy.MirroredFamily(k);
                if ((fam == 0 || f == fam) && !scanned.ContainsKey(k)) _tmp.Add(k);
            }
            foreach (var k in _tmp) { Remove(k); n++; }
            return n;
        }

        // Day rollover: expired loot markers are pruned silently (the client prunes with the same day count).
        public void SetDays(int days)
        {
            days = Math.Max(0, days);
            if (!Based || days == _h.Days) return;
            _h.Days = days; _headerDirty = true;
            _tmp.Clear();
            foreach (var kv in _values) if (!KeyPolicy.IsLive(KeyPolicy.MirroredFamily(kv.Key), kv.Value, days)) _tmp.Add(kv.Key);
            foreach (var k in _tmp) _values.Remove(k);
        }

        void Queue(KeyChange c)
        {
            for (int i = 0; i < _pending.Count; i++) if (_pending[i].Key == c.Key) { _pending[i] = c; return; }   // coalesce within one flush
            _pending.Add(c);
        }

        // One revision per flush (all changes since the last flush, coalesced).
        public bool TakeDelta(NetDataWriter w, out int count)
        {
            count = _pending.Count;
            if (!HasPending) return false;
            _h.Rev++;
            bool ok = MirrorCodec.WriteDelta(w, _h, _pending);
            _pending.Clear(); _headerDirty = false;
            return ok;
        }

        public bool WriteSnapshot(NetDataWriter w)
        {
            // A snapshot carries everything; pending changes are part of it (applied above), the revision still advances.
            if (HasPending) { _h.Rev++; _pending.Clear(); _headerDirty = false; }
            return MirrorCodec.WriteSnapshot(w, _h, _values);
        }
    }

    // ---- client ----
    public enum MirrorApply { Applied, Duplicate, Gap, WrongHistory, NoSnapshot, Stale }

    public struct KeyWrite
    {
        public string Key; public bool Delete; public int Value;
        public override string ToString() { return Delete ? "del " + Key : "set " + Key + "=" + Value; }
    }

    public sealed class ClientMirror
    {
        public const int MaxLocal = 4096;
        readonly Dictionary<string, int> _values = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly HashSet<string> _local = new HashSet<string>(StringComparer.Ordinal);
        readonly List<string> _tmp = new List<string>();
        MirrorHeader _h; bool _has;

        public bool Has { get { return _has; } }
        public MirrorHeader Header { get { return _h; } }
        public int Count { get { return _values.Count; } }
        public int LocalCount { get { return _local.Count; } }
        public long LocalDropped { get; private set; }
        public bool TryGet(string key, out int v) { return _values.TryGetValue(key, out v); }
        public bool IsLocal(string key) { return _local.Contains(key); }

        // Full replacement. A snapshot of the same history older than what we have is stale.
        public MirrorApply ApplySnapshot(MirrorHeader h, IDictionary<string, int> values, out bool newHistory)
        {
            newHistory = !_has || !_h.SameHistory(h);
            if (_has && !newHistory && h.Rev < _h.Rev) return MirrorApply.Stale;
            _values.Clear();
            foreach (var kv in values)
            {
                byte fam = KeyPolicy.MirroredFamily(kv.Key);
                if (fam != 0 && KeyPolicy.IsLive(fam, kv.Value, h.Days)) _values[kv.Key] = kv.Value;
            }
            _h = h; _has = true;
            return MirrorApply.Applied;
        }

        // Ordered deltas of the same history only; a gap or another history needs a resync snapshot.
        public MirrorApply ApplyDelta(MirrorHeader h, IList<KeyChange> changes, List<KeyWrite> writes)
        {
            if (!_has) return MirrorApply.NoSnapshot;
            if (!_h.SameHistory(h)) return MirrorApply.WrongHistory;
            if (h.Rev <= _h.Rev) return MirrorApply.Duplicate;
            if (h.Rev != _h.Rev + 1) return MirrorApply.Gap;
            if (h.Days != _h.Days) PruneTo(h.Days);
            _h = h;
            for (int i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                byte fam = KeyPolicy.MirroredFamily(c.Key);
                if (fam == 0) continue;
                if (c.Op == KeyOp.Set && KeyPolicy.IsLive(fam, c.Value, h.Days))
                {
                    _values[c.Key] = c.Value;
                    if (writes != null) writes.Add(new KeyWrite { Key = c.Key, Value = c.Value });
                }
                else
                {
                    _values.Remove(c.Key);
                    if (writes != null && !_local.Contains(c.Key)) writes.Add(new KeyWrite { Key = c.Key, Delete = true });
                }
            }
            return MirrorApply.Applied;
        }

        void PruneTo(int days)
        {
            _tmp.Clear();
            foreach (var kv in _values) if (!KeyPolicy.IsLive(KeyPolicy.MirroredFamily(kv.Key), kv.Value, days)) _tmp.Add(kv.Key);
            foreach (var k in _tmp) _values.Remove(k);
        }

        // The client's own game wrote this key (its own loot pick): keep it until the host has a value for it.
        public bool MarkLocal(string key)
        {
            if (KeyPolicy.MirroredFamily(key) == 0) return false;
            if (_local.Count >= MaxLocal && !_local.Contains(key)) { LocalDropped++; return false; }
            return _local.Add(key);
        }

        // Writes that make the client's SPrefs family keys equal to the host state (restricted to one family, 0 = all).
        public void Plan(IDictionary<string, int> client, byte fam, List<KeyWrite> writes)
        {
            if (!_has) return;
            foreach (var kv in _values)
            {
                if (fam != 0 && KeyPolicy.MirroredFamily(kv.Key) != fam) continue;
                int cur;
                if (!client.TryGetValue(kv.Key, out cur) || cur != kv.Value) writes.Add(new KeyWrite { Key = kv.Key, Value = kv.Value });
            }
            foreach (var kv in client)
            {
                byte f = KeyPolicy.MirroredFamily(kv.Key);
                if (f == 0 || (fam != 0 && f != fam) || _values.ContainsKey(kv.Key) || _local.Contains(kv.Key)) continue;
                writes.Add(new KeyWrite { Key = kv.Key, Delete = true });
            }
        }

        public void Reset() { _values.Clear(); _has = false; _h = default(MirrorHeader); }   // local marks survive (same client process)
    }
}
