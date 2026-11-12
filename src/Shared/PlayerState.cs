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

    // W3C/W3D presentation state carried in every movement snapshot (self-healing after loss, reconnect and scene reloads;
    // no separate event protocol). Verified IL: flashlight on = PlayerScript.torch (toggled in PlayerScript.Update IL_379c-
    // IL_3883 only with item_IDs2[3].item_amount > 0, forced off when Inventory.hasFlashlight is false, by HidePlayer and by
    // CarController.EnterVehicle); visual = PlayerScript.LightObj. Held tool = PlayerScript.currentItem (Select* methods:
    // 0 hand, 1 crowbar, 2 binoculars, 3 PDA, 4 hacking laptop, 5 baton, 6 blowtorch, 7 gas tank, 8 key fob, 9 meat,
    // 10 tranquilizer, 11 radio, 12 cloak, 99 phone, 102 brick). Heavy carry = PlayerScript.isCarrying + CarryItem
    // (Pickupable.PickUpHeavy / DropHeavy / StopCarryingItem).
    // W3.1: HasLight = owns a flashlight (item_IDs2[3].item_amount > 0) -> chest lamp shown; bits 4-7 of the wire byte carry
    // a 4-bit counter of original attacks (PlayerScript.Attack) -> one remote swing per change (protocol 18).
    [Flags]
    public enum GearFlags : byte { None = 0, Torch = 1, Carrying = 2, HasLight = 4 }

    // W3.1 presentation rules for the remote avatar's equipment (pure; RemoteGear applies them to Unity objects).
    public static class GearRules
    {
        // tool in hand: none while carrying a heavy object or seated (the original puts tools away in cars)
        public static int ToolShown(byte held, GearFlags g, bool seated) { return seated || (g & GearFlags.Carrying) != 0 ? 0 : held; }
        // W3.3: the other player's flashlight is never presented (no remote mesh / light); the HasLight/Torch bits stay on the
        // wire (protocol 19 unchanged) for diagnostics only. The local original flashlight is untouched.
        public const bool RemoteFlashlightVisual = false;
        public static bool IsMelee(int tool) { return tool == 1 || tool == 5; }   // crowbar, baton
        // one swing per counter change; never on the first sample after (re)binding (no replay after reconnect / reload)
        public const float SwingSeconds = 0.55f;
        // degrees about the avatar's right axis (negative = arm forward/up): raise overhead, strike down-forward, recover
        public static float SwingCurve(float t)
        {
            if (t <= 0f || t >= SwingSeconds) return 0f;
            if (t < 0.15f) return -150f * t / 0.15f;
            if (t < 0.32f) return -150f + 110f * (t - 0.15f) / 0.17f;
            return -40f + 40f * (t - 0.32f) / (SwingSeconds - 0.32f);
        }
        public static bool SwingStarts(int lastSeen, byte current, int tool) { return lastSeen >= 0 && lastSeen != current && IsMelee(tool); }

        // W3.2 held-tool attachment (user decision: simple attachment, no grip animation, no IK, open hand accepted):
        // the tool's long axis runs across the palm (little-finger knuckle -> index knuckle, i.e. through the closed-hand grip
        // line), the MIDDLE of the shaft sits in the palm centre (half-way between the wrist bone and the middle-finger knuckle).
        // W3.1 hung 85 % of the tool below the hand (grip 15 % from the end, 60 deg down) and lifted the upper arm 12 deg.
        public const float GripAlongShaft = 0.5f;   // 0 = one end, 1 = the other end
        public const float PalmCentre = 0.5f;       // wrist (0) -> middle-finger knuckle (1)
        public const float HoldArmLiftDeg = 0f;     // W3.1 used -12; removed (unnatural with the corrected grip)
        // Grip point in the tool's local long-axis coordinate: centre + axis * (2 * GripAlongShaft - 1) (axis = half extent).
        public static float GripOffset(float halfLength) { return halfLength * (2f * GripAlongShaft - 1f); }
    }

    // One movement snapshot. Wire size: 1 type + 1 slot + 4 seq + 4 time + 4 scene + 12 pos + 2 yaw + 1 flags
    // + 1 pitch + 1 gear + 1 held + 2 carry item = 34 bytes (W3; 29 bytes before protocol 17).
    public struct PlayerSnapshot
    {
        public byte Slot;        // 1 = host, 2 = client
        public uint Seq;         // per-sender, increments by 1 per snapshot
        public uint TimeMs;      // sender monotonic clock (ms)
        public int SceneHash;    // SceneKeys.Hash of the sender's loaded-scene key
        public Vec3 Pos;         // feet position, world space
        public float Yaw;        // degrees 0..360
        public MoveFlags Flags;
        public sbyte Pitch;      // W3: camera pitch in degrees (-90..90, positive = looking down, Unity euler x)
        public GearFlags Gear;   // W3: flashlight on, carrying a heavy object; W3.1 owns a flashlight
        public byte Swing;       // W3.1: attack counter 0..15 (shares the gear byte on the wire)
        public byte Held;        // W3: PlayerScript.currentItem (0 = empty hands)
        public ushort CarryItem; // W3: item_ID + 1 of the carried heavy object (0 = none / unknown)

        public bool Has(MoveFlags f) { return (Flags & f) != 0; }
    }

    public static class PlayerStateCodec
    {
        public const int WireSize = 34;

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
            w.Put(s.Pitch); w.Put((byte)(((byte)s.Gear & 0x0F) | ((s.Swing & 0x0F) << 4))); w.Put(s.Held); w.Put(s.CarryItem);
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
            s.Pitch = r.GetSByte(); byte g = r.GetByte(); s.Gear = (GearFlags)(g & 7); s.Swing = (byte)(g >> 4); s.Held = r.GetByte(); s.CarryItem = r.GetUShort();
            return true;
        }

        public static sbyte QuantizePitch(float eulerX)
        {
            float p = eulerX > 180f ? eulerX - 360f : eulerX;
            return (sbyte)Math.Max(-90, Math.Min(90, (int)Math.Round(p)));
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
