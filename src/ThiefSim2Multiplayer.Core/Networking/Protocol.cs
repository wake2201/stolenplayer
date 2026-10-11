using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public static partial class Protocol
    {
        public const ushort Version = 13;
        public const string ModVersion = "0.6.13";
        public const int MaxPacketBytes = 512;
        public const int TransformBytes = 30;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public uint Bits;
        }

        // Fixed wire layout, little endian on every host. Caller owns/reuses the buffer.
        public static bool WriteTransform(PlayerTransform pose, byte[] bytes, int offset = 0)
        {
            if (!ValidPose(pose) || bytes == null || offset < 0 || offset > bytes.Length - TransformBytes) return false;
            bytes[offset] = (byte)MessageType.PlayerTransform; bytes[offset + 1] = pose.PlayerId;
            WriteUInt32(bytes, offset + 2, pose.SceneEpoch); WriteUInt32(bytes, offset + 6, pose.Sequence);
            WriteUInt32(bytes, offset + 10, pose.Tick);
            WriteFloat(bytes, offset + 14, pose.X); WriteFloat(bytes, offset + 18, pose.Y);
            WriteFloat(bytes, offset + 22, pose.Z); WriteFloat(bytes, offset + 26, pose.Yaw); return true;
        }
        public static bool TryReadTransform(byte[] bytes, int offset, int count, out PlayerTransform pose)
        {
            pose = default(PlayerTransform);
            if (bytes == null || count != TransformBytes || offset < 0 || offset > bytes.Length - count || bytes[offset] != (byte)MessageType.PlayerTransform) return false;
            pose = new PlayerTransform { PlayerId = bytes[offset + 1], SceneEpoch = ReadUInt32(bytes, offset + 2),
                Sequence = ReadUInt32(bytes, offset + 6), Tick = ReadUInt32(bytes, offset + 10),
                X = ReadFloat(bytes, offset + 14), Y = ReadFloat(bytes, offset + 18), Z = ReadFloat(bytes, offset + 22), Yaw = ReadFloat(bytes, offset + 26) };
            if (ValidPose(pose)) return true;
            pose = default(PlayerTransform); return false;
        }
        private static void WriteUInt32(byte[] bytes, int offset, uint value)
        { bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); bytes[offset + 2] = (byte)(value >> 16); bytes[offset + 3] = (byte)(value >> 24); }
        private static uint ReadUInt32(byte[] bytes, int offset)
        { return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        internal static void WriteFloat(byte[] bytes, int offset, float value) { WriteUInt32(bytes, offset, new FloatBits { Value = value }.Bits); }
        private static float ReadFloat(byte[] bytes, int offset) { return new FloatBits { Bits = ReadUInt32(bytes, offset) }.Value; }

        public static byte[] Encode(Message message)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write((byte)message.Type);
                switch (message.Type)
                {
                    case MessageType.HandshakeRequest:
                        writer.Write(message.ProtocolVersion);
                        WriteString(writer, message.ModVersion, 32);
                        WriteString(writer, message.PlayerName, 48);
                        WriteString(writer, message.BuildFingerprint, 64);
                        if (message.ProtocolVersion >= 2) writer.Write(message.AvatarId);
                        break;
                    case MessageType.HandshakeAccepted:
                        writer.Write(message.ProtocolVersion);
                        WriteString(writer, message.ModVersion, 32);
                        WriteString(writer, message.PlayerName, 48);
                        WriteString(writer, message.BuildFingerprint, 64);
                        if (message.ProtocolVersion >= 2) writer.Write(message.AvatarId);
                        writer.Write(message.LocalPlayerId); writer.Write(message.RemotePlayerId); writer.Write(message.SessionId);
                        break;
                    case MessageType.HandshakeRejected:
                    case MessageType.PlayerLeft:
                        WriteString(writer, message.Reason, 120); break;
                    case MessageType.SceneState:
                        writer.Write(message.Ready); writer.Write(message.SceneEpoch);
                        WriteString(writer, message.SceneKey, 160); break;
                    case MessageType.PlayerTransform:
                        PlayerTransform pose = message.Transform;
                        if (!ValidPose(pose)) throw new ArgumentException("Invalid transform.");
                        writer.Write(pose.PlayerId); writer.Write(pose.SceneEpoch); writer.Write(pose.Sequence); writer.Write(pose.Tick);
                        writer.Write(pose.X); writer.Write(pose.Y); writer.Write(pose.Z); writer.Write(pose.Yaw); break;
                    case MessageType.PlayerState:
                        var stateBytes = new byte[StateBytes];
                        if (!WriteState(message.Transform, stateBytes)) throw new ArgumentException("Invalid semantic player state.");
                        writer.Write(stateBytes, 1, StateBytes - 1); break;
                    default: throw new ArgumentException("Unknown message type.");
                }
                return stream.ToArray();
            }
        }

        public static bool TryDecode(byte[] bytes, out Message message)
        { return TryDecode(bytes, 0, bytes == null ? 0 : bytes.Length, out message); }

        public static bool TryDecode(byte[] bytes, int offset, int count, out Message message)
        {
            message = null;
            if (bytes == null || count < 1 || count > MaxPacketBytes || offset < 0 || offset > bytes.Length - count) return false;
            try
            {
                using (var stream = new MemoryStream(bytes, offset, count, false))
                using (var reader = new BinaryReader(stream, Utf8))
                {
                    var result = new Message { Type = (MessageType)reader.ReadByte() };
                    switch (result.Type)
                    {
                        case MessageType.HandshakeRequest:
                        case MessageType.HandshakeAccepted:
                            result.ProtocolVersion = reader.ReadUInt16();
                            result.ModVersion = ReadString(reader, 32);
                            result.PlayerName = ReadString(reader, 48);
                            result.BuildFingerprint = ReadString(reader, 64);
                            if (result.ProtocolVersion >= 2) { result.AvatarId = reader.ReadByte(); if (!AvatarCatalog.IsSupported(result.AvatarId)) return false; }
                            if (result.ModVersion.Length == 0 || result.PlayerName.Length == 0) return false;
                            if (result.Type == MessageType.HandshakeAccepted)
                            {
                                result.LocalPlayerId = reader.ReadByte(); result.RemotePlayerId = reader.ReadByte(); result.SessionId = reader.ReadUInt64();
                                if (result.LocalPlayerId != 2 || result.RemotePlayerId != 1 || result.SessionId == 0) return false;
                            }
                            break;
                        case MessageType.HandshakeRejected:
                        case MessageType.PlayerLeft:
                            result.Reason = ReadString(reader, 120); break;
                        case MessageType.SceneState:
                            byte ready = reader.ReadByte(); if (ready > 1) return false;
                            result.Ready = ready == 1; result.SceneEpoch = reader.ReadUInt32();
                            result.SceneKey = ReadString(reader, 160);
                            if (result.SceneEpoch == 0 || (result.Ready && result.SceneKey.Length == 0)) return false;
                            break;
                        case MessageType.PlayerTransform:
                            result.Transform = new PlayerTransform {
                                PlayerId = reader.ReadByte(), SceneEpoch = reader.ReadUInt32(), Sequence = reader.ReadUInt32(), Tick = reader.ReadUInt32(),
                                X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), Yaw = reader.ReadSingle()
                            };
                            if (!ValidPose(result.Transform)) return false;
                            break;
                        case MessageType.PlayerState:
                            if (!TryReadState(bytes, offset, count, out result.Transform)) return false;
                            stream.Position = stream.Length; break;
                        default: return false;
                    }
                    if (stream.Position != stream.Length) return false;
                    message = result; return true;
                }
            }
            catch (InvalidDataException) { return false; }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        public static bool ValidPose(PlayerTransform pose)
        {
            return (pose.PlayerId == 1 || pose.PlayerId == 2) && pose.SceneEpoch != 0 &&
                Finite(pose.X) && Finite(pose.Y) && Finite(pose.Z) && Finite(pose.Yaw) &&
                Math.Abs(pose.X) <= 100000 && Math.Abs(pose.Y) <= 100000 && Math.Abs(pose.Z) <= 100000 && pose.Yaw >= 0 && pose.Yaw < 360;
        }
        public static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        public static bool IsNewer(uint value, uint previous) { return unchecked((int)(value - previous)) > 0; }
        private static void WriteString(BinaryWriter writer, string value, int limit)
        {
            byte[] bytes = Utf8.GetBytes(value ?? "");
            if (bytes.Length > limit || HasControls(value)) throw new ArgumentException("Invalid string.");
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static string ReadString(BinaryReader reader, int limit)
        {
            int length = reader.ReadUInt16();
            if (length > limit || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException();
            string text = Utf8.GetString(reader.ReadBytes(length));
            if (HasControls(text)) throw new InvalidDataException();
            return text;
        }
        private static bool HasControls(string value)
        {
            if (value == null) return false;
            foreach (char character in value) if (char.IsControl(character)) return true;
            return false;
        }
    }
}
