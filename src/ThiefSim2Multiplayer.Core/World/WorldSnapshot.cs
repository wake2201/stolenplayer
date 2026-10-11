using System;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace ThiefSim2Multiplayer.Core.World
{
    public sealed class InitialSnapshot
    {
        public ulong SessionId { get; private set; }
        public uint SceneEpoch { get; private set; }
        public ulong SnapshotId { get; private set; }
        public ulong BaseRevision { get; private set; }
        private readonly EntityStateRecord[] entities;
        public EntityStateRecord[] Entities { get { return (EntityStateRecord[])entities.Clone(); } }
        public InitialSnapshot(ulong session, uint epoch, ulong snapshotId, ulong revision, EntityStateRecord[] records)
        {
            if (session == 0 || epoch == 0 || snapshotId == 0 || records == null || records.Length > WorldLimits.MaxEntities) throw new ArgumentException("Invalid snapshot.");
            var ids = new HashSet<NetworkEntityId>();
            foreach (var record in records) if (record == null || record.Revision > revision || !ids.Add(record.Entity.Id) || (record.Entity.Id.Scope == EntityScope.Static && record.Descriptor.Length == 0)) throw new ArgumentException("Invalid snapshot entry.");
            SessionId = session; SceneEpoch = epoch; SnapshotId = snapshotId; BaseRevision = revision; entities = (EntityStateRecord[])records.Clone();
        }
    }
    public sealed class SnapshotMetadata
    {
        public readonly ulong SessionId, SnapshotId, BaseRevision;
        public readonly uint SceneEpoch;
        public readonly int TotalBytes, ChunkCount;
        private readonly byte[] hash;
        public byte[] Hash { get { return (byte[])hash.Clone(); } }
        public SnapshotMetadata(ulong session, uint epoch, ulong id, ulong revision, int total, int chunks, byte[] checksum)
        {
            if (session == 0 || epoch == 0 || id == 0 || total < 6 || total > WorldLimits.MaxSnapshotBytes || chunks < 1 || chunks > WorldLimits.MaxChunks || chunks != (total + WorldLimits.ChunkPayload - 1) / WorldLimits.ChunkPayload || checksum == null || checksum.Length != 32) throw new ArgumentException("Invalid snapshot bounds.");
            SessionId = session; SceneEpoch = epoch; SnapshotId = id; BaseRevision = revision; TotalBytes = total; ChunkCount = chunks; hash = (byte[])checksum.Clone();
        }
        public bool Matches(SnapshotMetadata other)
        { return other != null && SessionId == other.SessionId && SceneEpoch == other.SceneEpoch && SnapshotId == other.SnapshotId && BaseRevision == other.BaseRevision && TotalBytes == other.TotalBytes && ChunkCount == other.ChunkCount && StableHash.Equal(hash, other.hash); }
    }
    public sealed class SnapshotChunk
    {
        public readonly SnapshotMetadata Metadata;
        public readonly int Index;
        private readonly byte[] bytes;
        public byte[] Payload { get { return (byte[])bytes.Clone(); } }
        public SnapshotChunk(SnapshotMetadata metadata, int index, byte[] payload)
        {
            if (metadata == null || index < 0 || index >= metadata.ChunkCount || payload == null || payload.Length != Math.Min(WorldLimits.ChunkPayload, metadata.TotalBytes - index * WorldLimits.ChunkPayload)) throw new ArgumentException("Invalid chunk bounds.");
            Metadata = metadata; Index = index; bytes = (byte[])payload.Clone();
        }
    }
    public enum AssemblyResult { Accepted, Duplicate, Complete, Rejected, Conflict, Expired, InvalidHash }
    public sealed class SnapshotAssembler
    {
        public readonly SnapshotMetadata Metadata;
        private readonly byte[] buffer;
        private readonly bool[] received;
        private readonly double deadline;
        private bool invalid;
        public int ReceivedChunks { get; private set; }
        public int ReceivedBytes { get; private set; }
        public SnapshotAssembler(SnapshotMetadata metadata, double now)
        { if (metadata == null || double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentException(); Metadata = metadata; buffer = new byte[metadata.TotalBytes]; received = new bool[metadata.ChunkCount]; deadline = now + WorldLimits.AssemblySeconds; }
        public bool Expired(double now) { return now >= deadline; }
        public AssemblyResult Add(SnapshotChunk chunk, double now)
        {
            if (Expired(now)) { invalid = true; return AssemblyResult.Expired; }
            if (invalid || chunk == null || !Metadata.Matches(chunk.Metadata)) return AssemblyResult.Rejected;
            int offset = chunk.Index * WorldLimits.ChunkPayload; byte[] bytes = chunk.Payload;
            if (received[chunk.Index])
            {
                for (int i = 0; i < bytes.Length; i++) if (buffer[offset + i] != bytes[i]) { invalid = true; return AssemblyResult.Conflict; }
                return AssemblyResult.Duplicate;
            }
            Array.Copy(bytes, 0, buffer, offset, bytes.Length); received[chunk.Index] = true; ReceivedChunks++; ReceivedBytes += bytes.Length;
            if (ReceivedChunks != Metadata.ChunkCount) return AssemblyResult.Accepted;
            if (!StableHash.Equal(StableHash.Digest(buffer), Metadata.Hash)) { invalid = true; return AssemblyResult.InvalidHash; }
            return AssemblyResult.Complete;
        }
        public bool TryFinish(double now, out InitialSnapshot snapshot)
        { snapshot = null; return !invalid && !Expired(now) && ReceivedChunks == Metadata.ChunkCount && SnapshotCodec.TryDecode(buffer, Metadata, out snapshot); }
    }
    public static class SnapshotCodec
    {
        internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        public static byte[] Encode(InitialSnapshot snapshot)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Utf8))
            {
                writer.Write((ushort)1); var entities = snapshot.Entities; writer.Write(entities.Length);
                foreach (var record in entities) { WriteRecord(writer, record, true); if (stream.Length > WorldLimits.MaxSnapshotBytes) throw new ArgumentException("Snapshot byte limit exceeded."); }
                return stream.ToArray();
            }
        }
        public static bool TryDecode(byte[] bytes, SnapshotMetadata metadata, out InitialSnapshot snapshot)
        {
            snapshot = null;
            if (metadata == null || bytes == null || bytes.Length != metadata.TotalBytes || bytes.Length > WorldLimits.MaxSnapshotBytes || !StableHash.Equal(StableHash.Digest(bytes), metadata.Hash)) return false;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes, false), Utf8))
                {
                    if (reader.ReadUInt16() != 1) return false;
                    int count = reader.ReadInt32(); if (count < 0 || count > WorldLimits.MaxEntities || count > (bytes.Length - 6) / 46) return false;
                    var records = new EntityStateRecord[count];
                    for (int i = 0; i < count; i++) records[i] = ReadRecord(reader, true);
                    if (reader.BaseStream.Position != reader.BaseStream.Length) return false;
                    snapshot = new InitialSnapshot(metadata.SessionId, metadata.SceneEpoch, metadata.SnapshotId, metadata.BaseRevision, records); return true;
                }
            }
            catch (InvalidDataException) { return false; } catch (IOException) { return false; } catch (ArgumentException) { return false; }
        }
        public static SnapshotChunk[] Chunk(InitialSnapshot snapshot)
        {
            byte[] bytes = Encode(snapshot); int count = (bytes.Length + WorldLimits.ChunkPayload - 1) / WorldLimits.ChunkPayload;
            var metadata = new SnapshotMetadata(snapshot.SessionId, snapshot.SceneEpoch, snapshot.SnapshotId, snapshot.BaseRevision, bytes.Length, count, StableHash.Digest(bytes));
            var chunks = new SnapshotChunk[count];
            for (int i = 0; i < count; i++) { var payload = new byte[Math.Min(WorldLimits.ChunkPayload, bytes.Length - i * WorldLimits.ChunkPayload)]; Array.Copy(bytes, i * WorldLimits.ChunkPayload, payload, 0, payload.Length); chunks[i] = new SnapshotChunk(metadata, i, payload); }
            return chunks;
        }
        internal static void WriteRecord(BinaryWriter writer, EntityStateRecord record, bool descriptor)
        {
            writer.Write((byte)record.Entity.Id.Scope); writer.Write(record.Entity.Id.Value); writer.Write(record.Entity.Generation); writer.Write((ushort)record.Type); writer.Write(record.Revision);
            writer.Write((byte)record.Authority); writer.Write((byte)record.Ownership.Kind); writer.Write(record.Ownership.PersistentPlayer.ToByteArray()); writer.Write(record.Ownership.InputPlayerId); writer.Write(record.Tombstone);
            if (descriptor) { byte[] text = Utf8.GetBytes(record.Descriptor); writer.Write((ushort)text.Length); writer.Write(text); }
            byte[] payload = record.Payload; writer.Write((ushort)payload.Length); writer.Write(payload);
        }
        internal static EntityStateRecord ReadRecord(BinaryReader reader, bool descriptor)
        {
            var entity = new EntityRef(new NetworkEntityId((EntityScope)reader.ReadByte(), reader.ReadUInt64()), reader.ReadUInt32());
            var type = (EntityTypeId)reader.ReadUInt16(); ulong revision = reader.ReadUInt64();
            if (reader.ReadByte() != (byte)EntityAuthority.Host) throw new InvalidDataException();
            var kind = (OwnershipKind)reader.ReadByte(); var guid = new Guid(ReadExact(reader, 16)); byte input = reader.ReadByte();
            var ownership = new EntityOwnership(kind, guid, input); byte tombstone = reader.ReadByte(); if (tombstone > 1) throw new InvalidDataException();
            string text = descriptor ? Utf8.GetString(ReadBounded(reader, WorldLimits.MaxDescriptorBytes)) : "";
            return new EntityStateRecord(entity, type, revision, ownership, ReadBounded(reader, WorldLimits.MaxEntityPayload), tombstone == 1, text);
        }
        internal static byte[] ReadBounded(BinaryReader reader, int max)
        { int length = reader.ReadUInt16(); if (length > max) throw new InvalidDataException(); return ReadExact(reader, length); }
        internal static byte[] ReadExact(BinaryReader reader, int length)
        { if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException(); return reader.ReadBytes(length); }
    }
    public sealed class DeltaBacklog
    {
        private readonly List<EntityChange> changes = new List<EntityChange>();
        public int Count { get { return changes.Count; } }
        public int Bytes { get; private set; }
        public bool Overflowed { get; private set; }
        private ulong lastRevision;
        public void Begin(ulong revision) { changes.Clear(); Bytes = 0; Overflowed = false; lastRevision = revision; }
        public bool Add(EntityChange change)
        {
            if (Overflowed) return false;
            int cost = 64 + change.Record.PayloadLength;
            if (lastRevision == ulong.MaxValue || change.Record.Revision != lastRevision + 1 || changes.Count >= WorldLimits.MaxBacklog || Bytes > WorldLimits.MaxBacklogBytes - cost) { Overflowed = true; changes.Clear(); Bytes = 0; return false; }
            changes.Add(change); Bytes += cost; lastRevision = change.Record.Revision; return true;
        }
        public EntityChange[] Drain(int maximum = WorldLimits.MaxBacklog)
        {
            int count = Math.Min(maximum, changes.Count); var result = changes.GetRange(0, count).ToArray();
            foreach (var change in result) Bytes -= 64 + change.Record.PayloadLength;
            changes.RemoveRange(0, count); return result;
        }
    }
}
