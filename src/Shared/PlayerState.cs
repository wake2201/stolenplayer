// M2 player-state protocol. Unity-free (shared with offline tests). C# 5.
using System;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    [Flags]
    public enum MoveFlags : byte
    {
        None = 0,
        Present = 1,     // sender has a local player in a gameplay scene
        Grounded = 2,
        Moving = 4,
        Running = 8,
        Crouching = 16,
        InVehicle = 32,
        Hidden = 64      // in a hiding spot
    }

    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) { return new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t); }
        public static float Distance(Vec3 a, Vec3 b) { float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z; return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); }
        public static float HorizontalDistance(Vec3 a, Vec3 b) { float dx = a.X - b.X, dz = a.Z - b.Z; return (float)Math.Sqrt(dx * dx + dz * dz); }
        public override string ToString() { return X.ToString("0.00") + "," + Y.ToString("0.00") + "," + Z.ToString("0.00"); }
    }

    // One movement snapshot. Wire size: 1 type + 1 slot + 4 seq + 4 time + 4 scene + 12 pos + 2 yaw + 1 flags = 29 bytes.
    public struct PlayerSnapshot
    {
        public byte Slot;        // 1 = host, 2 = client
        public uint Seq;         // per-sender, increments by 1 per snapshot
        public uint TimeMs;      // sender monotonic clock (ms)
        public int SceneHash;    // SceneKeys.Hash of the sender's loaded-scene key
        public Vec3 Pos;         // feet position, world space
        public float Yaw;        // degrees 0..360
        public MoveFlags Flags;

        public bool Has(MoveFlags f) { return (Flags & f) != 0; }
    }

    public static class PlayerStateCodec
    {
        public const int WireSize = 29;

        public static void Write(NetDataWriter w, PlayerSnapshot s)
        {
            w.Put((byte)MsgType.PlayerState);
            w.Put(s.Slot);
            w.Put(s.Seq);
            w.Put(s.TimeMs);
            w.Put(s.SceneHash);
            w.Put(s.Pos.X); w.Put(s.Pos.Y); w.Put(s.Pos.Z);
            w.Put(QuantizeYaw(s.Yaw));
            w.Put((byte)s.Flags);
        }

        // Reader positioned after the MsgType byte.
        public static bool TryRead(NetDataReader r, out PlayerSnapshot s)
        {
            s = new PlayerSnapshot();
            if (r.AvailableBytes < WireSize - 1) return false;
            s.Slot = r.GetByte();
            s.Seq = r.GetUInt();
            s.TimeMs = r.GetUInt();
            s.SceneHash = r.GetInt();
            s.Pos = new Vec3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            s.Yaw = DequantizeYaw(r.GetUShort());
            s.Flags = (MoveFlags)r.GetByte();
            return true;
        }

        public static ushort QuantizeYaw(float deg)
        {
            float d = deg % 360f; if (d < 0) d += 360f;
            return (ushort)Math.Round(d / 360f * 65535f);
        }

        public static float DequantizeYaw(ushort q) { return q / 65535f * 360f; }
    }

    // Reliable: sent on connect and whenever the sender's scene key or presence changes (for logs/overlay).
    public sealed class SceneInfo
    {
        public byte Slot;
        public int SceneHash;
        public string SceneKey = "";
        public bool Present;

        public void Write(NetDataWriter w)
        {
            w.Put((byte)MsgType.SceneInfo);
            w.Put(Slot); w.Put(SceneHash); w.Put(SceneKey ?? ""); w.Put(Present);
        }

        public static bool TryRead(NetDataReader r, out SceneInfo s)
        {
            s = new SceneInfo();
            byte slot; int hash; string key; bool present;
            if (!r.TryGetByte(out slot) || !r.TryGetInt(out hash) || !r.TryGetString(out key) || !r.TryGetBool(out present)) return false;
            s.Slot = slot; s.SceneHash = hash; s.SceneKey = key; s.Present = present;
            return true;
        }
    }

    public static class SceneKeys
    {
        // Deterministic FNV-1a (string.GetHashCode is not stable across runtimes/processes).
        public static int Hash(string key)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in key ?? "") { h ^= c; h *= 16777619; }
                return (int)h;
            }
        }
    }

    public enum SnapshotVerdict { Accepted, Stale, WrongSlot, Invalid }

    public static class SnapshotValidation
    {
        public const float MaxCoordinate = 50000f;

        // Wrap-aware "a is newer than b" for sequence numbers.
        public static bool IsNewer(uint a, uint b) { return (int)(a - b) > 0; }

        public static SnapshotVerdict Check(PlayerSnapshot s, byte expectedSlot, bool hasLast, uint lastSeq)
        {
            if (s.Slot != expectedSlot) return SnapshotVerdict.WrongSlot;
            if (!Finite(s.Pos.X) || !Finite(s.Pos.Y) || !Finite(s.Pos.Z) || !Finite(s.Yaw)) return SnapshotVerdict.Invalid;
            if (Math.Abs(s.Pos.X) > MaxCoordinate || Math.Abs(s.Pos.Y) > MaxCoordinate || Math.Abs(s.Pos.Z) > MaxCoordinate) return SnapshotVerdict.Invalid;
            if (hasLast && !IsNewer(s.Seq, lastSeq)) return SnapshotVerdict.Stale;
            return SnapshotVerdict.Accepted;
        }

        static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
    }

    public enum RemoteView { NotConnected, NoLocalPlayer, RemoteNotPresent, OtherScene, InVehicle, Hiding, WaitingForData, Visible }

    // Visibility decision for the remote avatar (pure, testable).
    public static class RemoteVisibility
    {
        public static RemoteView Decide(bool connected, bool localPresent, bool remotePresent, int localScene, int remoteScene, MoveFlags remoteFlags, bool hasSamples)
        {
            if (!connected) return RemoteView.NotConnected;
            if (!localPresent) return RemoteView.NoLocalPlayer;
            if (!remotePresent || (remoteFlags & MoveFlags.Present) == 0) return RemoteView.RemoteNotPresent;
            if (localScene != remoteScene) return RemoteView.OtherScene;
            if ((remoteFlags & MoveFlags.InVehicle) != 0) return RemoteView.InVehicle;
            if ((remoteFlags & MoveFlags.Hidden) != 0) return RemoteView.Hiding;
            if (!hasSamples) return RemoteView.WaitingForData;
            return RemoteView.Visible;
        }
    }
}
