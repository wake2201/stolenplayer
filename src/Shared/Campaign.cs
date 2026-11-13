// W4.2 Phase A: host-owned campaign bootstrap + persistent Player-2 spawn. Unity-free (unit-testable); the Unity side is
// src/Plugin/CampaignBootstrap.cs.
//
// Problem (v0.12.0, first two-PC test): the client sandbox was seeded from the client's OWN real save and loaded with the
// original Continue -> Player 2 entered the multiplayer world with its solo story, world progress and position.
// Design: the host sends ONE campaign snapshot per connection, built from its live FBPP model (FBPP.GetSaveFileAsJson -
// verified IL: JsonUtility.ToJson(GetSaveFile()) = the in-memory model incl. unsaved values), filtered by CampaignPolicy:
//   World           host value (story, missions, loot markers, daily events, unlocks, stats, ...)
//   PlayerOwnP1     Player 1's personal keys - never sent; replaced by the host-owned Player-2 record (P2Inventory)
//   HostStorage     host-executed shared storage / physical cargo (CarBox, locker, van + shed heavy items, trunk item):
//                   never copied - a local copy on the client would duplicate real items (shared via CarBoxSync instead)
//   Position        CurrentLocation / Saved_Pos / XPlayer..ZPlayer: from the host's spawn plan for Player 2
//   ModHost         TS2MP_* host bookkeeping keys - never sent
//   LocalPreference purely local player preference kept from the client's own data (TPP camera choice)
// The client writes the result into its SANDBOX save with the original FBPP.OverwriteLocalSaveFile (verified IL:
// WriteToSaveFile + reload of _latestData) while still in the main menu, then the original MainMenu.Continue loads it.
//
// Player-2 spawn (SpawnRules): the host keeps Player 2's last valid multiplayer position per host campaign (save profile +
// the Player-2 record's save-history id) in its own data folder, updated from periodic client reports and at disconnect.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public enum CampaignKeyClass : byte { World = 0, PlayerOwnP1 = 1, HostStorage = 2, Position = 3, ModHost = 4, LocalPreference = 5 }

    public static class CampaignPolicy
    {
        // Verified writers: SaveGame.SaveGameNewSystem / LoadGame (Money, BatonLife, CrowbarLife), Darkbank (BankBalance,
        // BankIncrease), ItemID.Save_Item_Amount (Item_<id>). The Player-2 record (P2Inventory) owns exactly these for Player 2.
        static readonly HashSet<string> P1Exact = new HashSet<string>(StringComparer.Ordinal) { "Money", "BankBalance", "BankIncrease", "BatonLife", "CrowbarLife" };
        // SaveGame.LoadPlayerPosition reads XPlayer/YPlayer/ZPlayer (IL_000b..001f); GameManager.Start reads CurrentLocation / Saved_Pos.
        static readonly HashSet<string> PositionExact = new HashSet<string>(StringComparer.Ordinal) { "CurrentLocation", "Saved_Pos", "XPlayer", "YPlayer", "ZPlayer" };
        // ItemID.Save_Item_Amount (Storage_Item_/Locker_Item_), VanHeavyItems.SaveItems (HeavyVan_, Pos*/Rot*_item),
        // HeavyItemsShed.SaveItems (HeavyItemShed), CarController.LoadTrunkItem (TrunkItem).
        static readonly string[] StoragePrefixes = { "Storage_Item_", "Locker_Item_", "HeavyVan_", "HeavyItemShed", "PosX_item", "PosY_item", "PosZ_item", "RotX_item", "RotY_item", "RotZ_item" };
        // CarController.Update / RunCar / GameManager.Update read "TPP" (the player's own car camera choice).
        static readonly HashSet<string> LocalExact = new HashSet<string>(StringComparer.Ordinal) { "TPP" };

        public const string ModPrefix = "TS2MP_";

        public static bool IsBackpackCount(string k) { return k.Length > 5 && k.StartsWith("Item_", StringComparison.Ordinal) && AllDigits(k, 5); }

        static bool AllDigits(string k, int from)
        {
            if (k.Length - from < 1 || k.Length - from > 9) return false;
            for (int i = from; i < k.Length; i++) if (k[i] < '0' || k[i] > '9') return false;
            return true;
        }

        public static CampaignKeyClass Classify(string key)
        {
            if (string.IsNullOrEmpty(key)) return CampaignKeyClass.World;
            if (key.StartsWith(ModPrefix, StringComparison.Ordinal)) return CampaignKeyClass.ModHost;
            if (P1Exact.Contains(key) || IsBackpackCount(key)) return CampaignKeyClass.PlayerOwnP1;
            if (key == "TrunkItem") return CampaignKeyClass.HostStorage;
            for (int i = 0; i < StoragePrefixes.Length; i++) if (key.StartsWith(StoragePrefixes[i], StringComparison.Ordinal)) return CampaignKeyClass.HostStorage;
            if (PositionExact.Contains(key)) return CampaignKeyClass.Position;
            if (LocalExact.Contains(key)) return CampaignKeyClass.LocalPreference;
            return CampaignKeyClass.World;
        }
    }

    // FBPP model content (FBPPFileModel: StringData / IntData / FloatData / BoolData, key -> value).
    public sealed class CampaignData
    {
        public readonly Dictionary<string, int> Ints = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, float> Floats = new Dictionary<string, float>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, bool> Bools = new Dictionary<string, bool>(StringComparer.Ordinal);
        public int Count { get { return Ints.Count + Floats.Count + Strings.Count + Bools.Count; } }

        public bool TryInt(string k, out int v) { return Ints.TryGetValue(k, out v); }
        public bool Has(string k) { return Ints.ContainsKey(k) || Floats.ContainsKey(k) || Strings.ContainsKey(k) || Bools.ContainsKey(k); }
        public void RemoveKey(string k) { Ints.Remove(k); Floats.Remove(k); Strings.Remove(k); Bools.Remove(k); }
    }

    // ---- spawn ----
    [Flags]
    public enum SpawnContext : byte
    {
        None = 0,
        InVehicle = 1,       // PlayerScript.is_in_vehicle at the report: restore beside the spot, never into the seat
        BlockSaving = 2,     // StoryManager.currentStoryScript.block_saving: the original forbids saving in this story step
        BlockTravel = 4,     // StoryScript.block_all_buttons: scripted step (travel menu blocked by the original)
        PoliceChase = 8,     // PlayerScript.isPoliceChase
        Caught = 16,         // PlayerScript.caughtUp
        Hiding = 32,         // PlayerScript.isHiding (inside a hiding spot)
    }

    public static class SpawnRegions
    {
        // Free-roam regions whose original arrival is a normal map spawn (TravelRules.IsFleetRegion: hideout, Verwood, Auto
        // Shop, Hillville, Madison). Everything else (heists 90-98, jail 93, intro -1) is a mission / scripted scene.
        public static bool IsFreeRoam(int region) { return TravelRules.IsFleetRegion(region); }
        public const int Fallback = TravelRules.Hideout;   // verified safe fallback: the hideout's original spawn (GameManager.SpawnHideoutPlayer)
    }

    public static class SpawnContextRules
    {
        public const SpawnContext Unsafe = SpawnContext.BlockSaving | SpawnContext.BlockTravel | SpawnContext.PoliceChase | SpawnContext.Caught;
        public static bool IsUnsafe(SpawnContext c) { return (c & Unsafe) != 0; }
    }

    // Last valid multiplayer position of Player 2 (host file p2spawn-<profile>.dat). 46 B.
    public sealed class P2SpawnRecord
    {
        public const byte Version = 1;
        public const int Bytes = 46;
        public int HistoryId;         // the Player-2 record's save-history id (P2Book.HistoryId) = campaign identity on the host
        public int Region;
        public Vec3 Pos;
        public float Yaw;
        public SpawnContext Context;
        public int Story;             // host StoryManager story value when the report was accepted (diagnostics + mission checks)
        public uint Seq;              // accepted reports so far (monotonic per record)
        public long UtcTicks;
        public int Profile;

        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put(Version); w.Put(HistoryId); w.Put(Region); w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); w.Put(Yaw); w.Put((byte)Context);
            w.Put(Story); w.Put(Seq); w.Put(UtcTicks); w.Put(Profile);
            return w.CopyData();
        }

        public static bool TryParse(byte[] b, out P2SpawnRecord r, out string err)
        {
            r = null; err = null;
            if (b == null || b.Length != Bytes) { err = "size " + (b == null ? -1 : b.Length); return false; }
            var rd = new NetDataReader(b);
            if (rd.GetByte() != Version) { err = "version"; return false; }
            var x = new P2SpawnRecord { HistoryId = rd.GetInt(), Region = rd.GetInt(), Pos = new Vec3(rd.GetFloat(), rd.GetFloat(), rd.GetFloat()), Yaw = rd.GetFloat(), Context = (SpawnContext)rd.GetByte(),
                Story = rd.GetInt(), Seq = rd.GetUInt(), UtcTicks = rd.GetLong(), Profile = rd.GetInt() };
            if (!SpawnValidation.PositionOk(x.Pos) || !SpawnValidation.Finite(x.Yaw)) { err = "position out of range"; return false; }
            if (!Regions.IsKnown(x.Region)) { err = "unknown region " + x.Region; return false; }
            if (x.HistoryId == 0) { err = "no campaign id"; return false; }
            r = x; return true;
        }
    }

    public static class SpawnValidation
    {
        public const float MaxAbsXZ = 20000f, MinY = -500f, MaxY = 3000f;
        public static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v); }
        public static bool PositionOk(Vec3 p) { return Finite(p.X) && Finite(p.Y) && Finite(p.Z) && Math.Abs(p.X) <= MaxAbsXZ && Math.Abs(p.Z) <= MaxAbsXZ && p.Y >= MinY && p.Y <= MaxY; }
    }

    // Client -> host proposal "p2pos" (Player scope, arg 2), never stored in the registry. 24 B.
    public struct P2PosReport
    {
        public const int Bytes = 24;
        public int Region; public Vec3 Pos; public float Yaw; public SpawnContext Context; public byte Reserved; public ushort Reserved2;
        public byte[] ToBytes()
        {
            var w = new NetDataWriter();
            w.Put(Region); w.Put(Pos.X); w.Put(Pos.Y); w.Put(Pos.Z); w.Put(Yaw); w.Put((byte)Context); w.Put(Reserved); w.Put(Reserved2);
            return w.CopyData();
        }
        public static bool TryParse(byte[] b, out P2PosReport r)
        {
            r = default(P2PosReport);
            if (b == null || b.Length != Bytes) return false;
            var rd = new NetDataReader(b);
            r.Region = rd.GetInt(); r.Pos = new Vec3(rd.GetFloat(), rd.GetFloat(), rd.GetFloat()); r.Yaw = rd.GetFloat(); r.Context = (SpawnContext)rd.GetByte(); r.Reserved = rd.GetByte(); r.Reserved2 = rd.GetUShort();
            return Regions.IsKnown(r.Region) && SpawnValidation.PositionOk(r.Pos) && SpawnValidation.Finite(r.Yaw);
        }
    }

    public enum SpawnMode : byte
    {
        NearHost = 1,        // first join: near Player 1 in the host's free-roam region (checked ring placement, never on top)
        Restore = 2,         // rejoin: exact last valid position (checked; ring fallback)
        RestoreNear = 3,     // rejoin, last report was in a vehicle / hiding spot: checked ring around it, never the seat/spot
        SafePoint = 4,       // rejoin after an unsafe story context: nearest original leave point of that region / its original spawn
        OriginalSpawn = 5,   // the region's original spawn (hideout fallback, host in a mission scene, no usable data)
    }

    public enum SpawnReason : byte { FirstJoin = 1, Rejoin = 2, StaleRecord = 3, InvalidRecord = 4, StoryContext = 5, MissionScene = 6, HostInMissionScene = 7, VehicleOrHiding = 8 }

    public struct SpawnPlan
    {
        public const int Bytes = 23;
        public SpawnMode Mode; public SpawnReason Reason; public int Region; public bool HasTarget; public Vec3 Target; public float Yaw;
        public void Write(NetDataWriter w) { w.Put((byte)Mode); w.Put((byte)Reason); w.Put(Region); w.Put(HasTarget); w.Put(Target.X); w.Put(Target.Y); w.Put(Target.Z); w.Put(Yaw); }
        public static bool TryRead(NetDataReader r, out SpawnPlan p)
        {
            p = default(SpawnPlan);
            byte m, why; int reg; bool has; float x, y, z, yaw;
            if (!r.TryGetByte(out m) || !r.TryGetByte(out why) || !r.TryGetInt(out reg) || !r.TryGetBool(out has) || !r.TryGetFloat(out x) || !r.TryGetFloat(out y) || !r.TryGetFloat(out z) || !r.TryGetFloat(out yaw)) return false;
            if (m < 1 || m > 5 || why < 1 || why > 8 || !Regions.IsKnown(reg)) return false;
            p = new SpawnPlan { Mode = (SpawnMode)m, Reason = (SpawnReason)why, Region = reg, HasTarget = has, Target = new Vec3(x, y, z), Yaw = yaw };
            if ((has && !SpawnValidation.PositionOk(p.Target)) || !SpawnValidation.Finite(yaw)) return false;
            return true;
        }
        public override string ToString()
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return Mode + "/" + Reason + " in " + Regions.Name(Region) + (HasTarget ? " at (" + Target.X.ToString("0.0", c) + " " + Target.Y.ToString("0.0", c) + " " + Target.Z.ToString("0.0", c) + ")" : "");
        }
    }

    public static class SpawnRules
    {
        // record: the host's stored record (null = none); historyId: the current Player-2 record's save history id.
        // recordError: non-null when a stored record exists but could not be parsed.
        public static SpawnPlan Decide(P2SpawnRecord record, string recordError, int historyId, int hostRegion, Vec3? hostFeet, out string note)
        {
            if (record == null)
                return First(recordError != null ? SpawnReason.InvalidRecord : SpawnReason.FirstJoin, hostRegion, hostFeet, recordError != null ? "stored record unusable (" + recordError + ")" : "no record for this campaign", out note);
            if (record.HistoryId != historyId)
                return First(SpawnReason.StaleRecord, hostRegion, hostFeet, "record belongs to another save history of this profile (record " + record.HistoryId + ", campaign " + historyId + ")", out note);
            if (!SpawnRegions.IsFreeRoam(record.Region))
            {
                note = "last position was in the mission scene " + Regions.Name(record.Region) + ": verified fallback = hideout original spawn";
                return new SpawnPlan { Mode = SpawnMode.OriginalSpawn, Reason = SpawnReason.MissionScene, Region = SpawnRegions.Fallback };
            }
            if (SpawnContextRules.IsUnsafe(record.Context))
            {
                note = "last position was in an unsafe story context (" + record.Context + "): nearest original safe point of " + Regions.Name(record.Region);
                return new SpawnPlan { Mode = SpawnMode.SafePoint, Reason = SpawnReason.StoryContext, Region = record.Region, HasTarget = true, Target = record.Pos };
            }
            if ((record.Context & (SpawnContext.InVehicle | SpawnContext.Hiding)) != 0)
            {
                note = "last position was " + ((record.Context & SpawnContext.InVehicle) != 0 ? "in a vehicle" : "in a hiding spot") + ": checked placement beside it";
                return new SpawnPlan { Mode = SpawnMode.RestoreNear, Reason = SpawnReason.VehicleOrHiding, Region = record.Region, HasTarget = true, Target = record.Pos, Yaw = record.Yaw };
            }
            note = "rejoin: last valid position (report #" + record.Seq + ")";
            return new SpawnPlan { Mode = SpawnMode.Restore, Reason = SpawnReason.Rejoin, Region = record.Region, HasTarget = true, Target = record.Pos, Yaw = record.Yaw };
        }

        static SpawnPlan First(SpawnReason why, int hostRegion, Vec3? hostFeet, string what, out string note)
        {
            if (SpawnRegions.IsFreeRoam(hostRegion) && hostFeet.HasValue && SpawnValidation.PositionOk(hostFeet.Value))
            {
                note = what + ": first spawn near Player 1 in " + Regions.Name(hostRegion);
                return new SpawnPlan { Mode = SpawnMode.NearHost, Reason = why, Region = hostRegion, HasTarget = true, Target = hostFeet.Value };
            }
            note = what + ": Player 1 is " + (SpawnRegions.IsFreeRoam(hostRegion) ? "not placed yet" : "in " + Regions.Name(hostRegion)) + " -> hideout original spawn";
            return new SpawnPlan { Mode = SpawnMode.OriginalSpawn, Reason = why == SpawnReason.FirstJoin ? SpawnReason.HostInMissionScene : why, Region = SpawnRegions.Fallback };
        }

        // Host acceptance of a client position report (validation boundary: the host cannot see the client's scene; it checks
        // identity, the registry region and plausibility only).
        public static string CheckReport(byte slot, int scopeArg, int registryRegion, P2PosReport r)
        {
            if (slot != 2 || scopeArg != 2) return "only Player 2 reports its own position";
            if (r.Region != registryRegion) return "report region " + Regions.Name(r.Region) + " differs from the registry region " + Regions.Name(registryRegion);
            return null;
        }
    }

    // ---- the campaign snapshot (host -> client, MsgType.CampaignSnapshot, reliable ordered, once per connection) ----
    public sealed class CampaignSnapshot
    {
        public const byte Version = 1;
        public const int MaxEntries = 20000, MaxKeyChars = 200, MaxStringChars = 8192, MaxBytes = 1 << 20;

        public int Profile;          // host save profile
        public int HistoryId;        // Player-2 record history id (campaign identity)
        public int HostStory;        // host Story key (verification after the client wrote its sandbox)
        public uint Id;              // random per snapshot (log correlation)
        public SpawnPlan Plan;
        public readonly CampaignData Data = new CampaignData();

        // Host: the live FBPP model of Player 1's save -> the Player-2 campaign base (see file header).
        public static CampaignSnapshot Build(CampaignData host, P2Inventory p2, SpawnPlan plan, int profile, int historyId, uint id, out int[] removedByClass)
        {
            removedByClass = new int[6];
            var s = new CampaignSnapshot { Profile = profile, HistoryId = historyId, Plan = plan, Id = id };
            int story; s.HostStory = host.TryInt("Story", out story) ? story : 0;
            foreach (var kv in host.Ints) if (Keep(kv.Key, removedByClass)) s.Data.Ints[kv.Key] = kv.Value;
            foreach (var kv in host.Floats) if (Keep(kv.Key, removedByClass)) s.Data.Floats[kv.Key] = kv.Value;
            foreach (var kv in host.Strings) if (Keep(kv.Key, removedByClass)) s.Data.Strings[kv.Key] = kv.Value ?? "";
            foreach (var kv in host.Bools) if (Keep(kv.Key, removedByClass)) s.Data.Bools[kv.Key] = kv.Value;
            // Player 2's own record (host-owned, M4.3.3) in place of Player 1's personal keys.
            if (p2 != null)
            {
                foreach (var kv in p2.Items) s.Data.Ints["Item_" + kv.Key] = kv.Value;
                s.Data.Ints["Money"] = p2.Money; s.Data.Ints["BankBalance"] = p2.Bank;
                s.Data.Ints["BatonLife"] = p2.BatonLife; s.Data.Ints["CrowbarLife"] = p2.CrowbarLife;
            }
            // Position keys for the original load path: CurrentLocation (GameManager.Start), Saved_Pos = 1 + XPlayer..ZPlayer
            // (SaveGame.LoadPlayerPosition in Madison / Hillville). Regions with a fixed original spawn ignore the coordinates.
            s.Data.Ints["CurrentLocation"] = plan.Region;
            s.Data.Ints["Saved_Pos"] = 1;
            var t = plan.HasTarget ? plan.Target : new Vec3(0, 0, 0);
            s.Data.Floats["XPlayer"] = t.X; s.Data.Floats["YPlayer"] = t.Y; s.Data.Floats["ZPlayer"] = t.Z;
            return s;
        }

        static bool Keep(string key, int[] removed)
        {
            var c = CampaignPolicy.Classify(key);
            if (c == CampaignKeyClass.World) return true;
            removed[(int)c]++;
            return false;
        }

        // Client: add its own purely local preferences (never progression).
        public void ApplyLocalPreferences(CampaignData local)
        {
            if (local == null) return;
            foreach (var kv in local.Bools) if (CampaignPolicy.Classify(kv.Key) == CampaignKeyClass.LocalPreference) Data.Bools[kv.Key] = kv.Value;
            foreach (var kv in local.Ints) if (CampaignPolicy.Classify(kv.Key) == CampaignKeyClass.LocalPreference) Data.Ints[kv.Key] = kv.Value;
        }

        public byte[] Encode()
        {
            var w = new NetDataWriter();
            w.Put(Version); w.Put(Profile); w.Put(HistoryId); w.Put(HostStory); w.Put(Id);
            Plan.Write(w);
            w.Put(Data.Ints.Count); foreach (var kv in Data.Ints) { w.Put(kv.Key); w.Put(kv.Value); }
            w.Put(Data.Floats.Count); foreach (var kv in Data.Floats) { w.Put(kv.Key); w.Put(kv.Value); }
            w.Put(Data.Strings.Count); foreach (var kv in Data.Strings) { w.Put(kv.Key); w.Put(kv.Value ?? ""); }
            w.Put(Data.Bools.Count); foreach (var kv in Data.Bools) { w.Put(kv.Key); w.Put(kv.Value); }
            return w.CopyData();
        }

        public static bool TryDecode(NetDataReader r, out CampaignSnapshot s, out string err)
        {
            s = null; err = null;
            try
            {
                if (r.AvailableBytes > MaxBytes) { err = "oversized (" + r.AvailableBytes + " B)"; return false; }
                byte v;
                if (!r.TryGetByte(out v) || v != Version) { err = "version"; return false; }
                var x = new CampaignSnapshot();
                if (!r.TryGetInt(out x.Profile) || !r.TryGetInt(out x.HistoryId) || !r.TryGetInt(out x.HostStory) || !r.TryGetUInt(out x.Id)) { err = "header"; return false; }
                if (!SpawnPlan.TryRead(r, out x.Plan)) { err = "spawn plan"; return false; }
                int total = 0, n;
                if (!Count(r, ref total, out n, out err)) return false;
                for (int i = 0; i < n; i++) { string k; int val; if (!Key(r, out k, out err) || !r.TryGetInt(out val)) { err = err ?? "int"; return false; } x.Data.Ints[k] = val; }
                if (!Count(r, ref total, out n, out err)) return false;
                for (int i = 0; i < n; i++) { string k; float val; if (!Key(r, out k, out err) || !r.TryGetFloat(out val)) { err = err ?? "float"; return false; } x.Data.Floats[k] = val; }
                if (!Count(r, ref total, out n, out err)) return false;
                for (int i = 0; i < n; i++) { string k, val; if (!Key(r, out k, out err) || !r.TryGetString(out val) || val.Length > MaxStringChars) { err = err ?? "string"; return false; } x.Data.Strings[k] = val; }
                if (!Count(r, ref total, out n, out err)) return false;
                for (int i = 0; i < n; i++) { string k; bool val; if (!Key(r, out k, out err) || !r.TryGetBool(out val)) { err = err ?? "bool"; return false; } x.Data.Bools[k] = val; }
                if (r.AvailableBytes != 0) { err = "trailing bytes"; return false; }
                if (x.HistoryId == 0) { err = "no campaign id"; return false; }
                int loc; if (!x.Data.TryInt("CurrentLocation", out loc) || loc != x.Plan.Region) { err = "position keys do not match the spawn plan"; return false; }
                s = x; return true;
            }
            catch (Exception ex) { err = ex.GetType().Name; return false; }
        }

        static bool Count(NetDataReader r, ref int total, out int n, out string err)
        {
            err = null;
            if (!r.TryGetInt(out n) || n < 0 || n > MaxEntries || (total += n) > MaxEntries) { err = "entry count"; return false; }
            return true;
        }

        static bool Key(NetDataReader r, out string k, out string err)
        {
            err = null;
            if (!r.TryGetString(out k) || string.IsNullOrEmpty(k) || k.Length > MaxKeyChars) { err = "key"; return false; }
            return true;
        }
    }
}
