using System;
using System.IO;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public enum WorldMessageType : byte { SnapshotBegin = 20, SnapshotChunk = 21, SnapshotAck = 22, EntityChange = 23, WorldReady = 24, ResyncRequest = 25 }
    public sealed class WorldMessage
    {
        public WorldMessageType Type;
        public ulong SessionId, SnapshotId, Revision;
        public uint SceneEpoch;
        public SnapshotMetadata Metadata;
        public SnapshotChunk Chunk;
        public EntityChange Change;
    }
    public static class WorldProtocol
    {
        public static byte[] Encode(WorldMessage message)
        {
            if (message == null || message.SessionId == 0 || message.SceneEpoch == 0) throw new ArgumentException("Invalid world envelope.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)message.Type); writer.Write(message.SessionId); writer.Write(message.SceneEpoch);
                switch (message.Type)
                {
                    case WorldMessageType.SnapshotBegin:
                    case WorldMessageType.SnapshotChunk:
                        var meta = message.Type == WorldMessageType.SnapshotBegin ? message.Metadata : message.Chunk.Metadata;
                        if (meta.SessionId != message.SessionId || meta.SceneEpoch != message.SceneEpoch) throw new ArgumentException("Metadata envelope mismatch.");
                        writer.Write(meta.SnapshotId); writer.Write(meta.BaseRevision); writer.Write(meta.TotalBytes); writer.Write(meta.ChunkCount); writer.Write(meta.Hash);
                        if (message.Type == WorldMessageType.SnapshotChunk) { writer.Write(message.Chunk.Index); byte[] bytes = message.Chunk.Payload; writer.Write((ushort)bytes.Length); writer.Write(bytes); }
                        break;
                    case WorldMessageType.SnapshotAck:
                    case WorldMessageType.WorldReady:
                    case WorldMessageType.ResyncRequest:
                        if (message.SnapshotId == 0 && message.Type != WorldMessageType.ResyncRequest) throw new ArgumentException("Zero snapshot ID.");
                        writer.Write(message.SnapshotId); writer.Write(message.Revision); break;
                    case WorldMessageType.EntityChange:
                        if (message.Change == null) throw new ArgumentException("Missing entity change.");
                        writer.Write((byte)message.Change.Operation); SnapshotCodec.WriteRecord(writer, message.Change.Record, false); break;
                    default: throw new ArgumentException("Invalid world message.");
                }
                if (stream.Length > Protocol.MaxPacketBytes) throw new ArgumentException("Oversize world packet."); return stream.ToArray();
            }
        }
        public static bool TryDecode(byte[] bytes, int offset, int count, out WorldMessage message)
        {
            message = null;
            if (bytes == null || count < 13 || count > Protocol.MaxPacketBytes || offset < 0 || offset > bytes.Length - count) return false;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes, offset, count, false)))
                {
                    var result = new WorldMessage { Type = (WorldMessageType)reader.ReadByte(), SessionId = reader.ReadUInt64(), SceneEpoch = reader.ReadUInt32() };
                    if (result.SessionId == 0 || result.SceneEpoch == 0) return false;
                    switch (result.Type)
                    {
                        case WorldMessageType.SnapshotBegin:
                        case WorldMessageType.SnapshotChunk:
                            var metadata = new SnapshotMetadata(result.SessionId, result.SceneEpoch, reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), SnapshotCodec.ReadExact(reader, 32));
                            result.Metadata = metadata;
                            if (result.Type == WorldMessageType.SnapshotChunk) result.Chunk = new SnapshotChunk(metadata, reader.ReadInt32(), SnapshotCodec.ReadBounded(reader, WorldLimits.ChunkPayload));
                            break;
                        case WorldMessageType.SnapshotAck:
                        case WorldMessageType.WorldReady:
                        case WorldMessageType.ResyncRequest:
                            result.SnapshotId = reader.ReadUInt64(); result.Revision = reader.ReadUInt64();
                            if (result.SnapshotId == 0 && result.Type != WorldMessageType.ResyncRequest) return false; break;
                        case WorldMessageType.EntityChange:
                            result.Change = new EntityChange((EntityOperation)reader.ReadByte(), SnapshotCodec.ReadRecord(reader, false)); break;
                        default: return false;
                    }
                    if (reader.BaseStream.Position != reader.BaseStream.Length) return false; message = result; return true;
                }
            }
            catch (InvalidDataException) { return false; } catch (IOException) { return false; } catch (ArgumentException) { return false; }
        }
    }
}
