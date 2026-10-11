using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace ThiefSim2Multiplayer.Core.World
{
    public enum EntityScope : byte { Static = 1, Dynamic = 2 }
    public struct NetworkEntityId : IEquatable<NetworkEntityId>, IComparable<NetworkEntityId>
    {
        public readonly EntityScope Scope;
        public readonly ulong Value;
        public NetworkEntityId(EntityScope scope, ulong value)
        { if ((scope != EntityScope.Static && scope != EntityScope.Dynamic) || value == 0) throw new ArgumentException("Invalid entity ID."); Scope = scope; Value = value; }
        public bool IsValid { get { return Value != 0 && (Scope == EntityScope.Static || Scope == EntityScope.Dynamic); } }
        public bool Equals(NetworkEntityId other) { return Scope == other.Scope && Value == other.Value; }
        public override bool Equals(object other) { return other is NetworkEntityId && Equals((NetworkEntityId)other); }
        public override int GetHashCode() { return ((int)Scope * 397) ^ Value.GetHashCode(); } // Dictionary only, never wire identity.
        public int CompareTo(NetworkEntityId other) { int scope = Scope.CompareTo(other.Scope); return scope != 0 ? scope : Value.CompareTo(other.Value); }
        public override string ToString() { return Scope + ":" + Value.ToString("X16"); }
    }
    public struct EntityRef : IEquatable<EntityRef>
    {
        public readonly NetworkEntityId Id;
        public readonly uint Generation;
        public EntityRef(NetworkEntityId id, uint generation)
        { if (!id.IsValid || generation == 0) throw new ArgumentException("Invalid entity reference."); Id = id; Generation = generation; }
        public bool Equals(EntityRef other) { return Id.Equals(other.Id) && Generation == other.Generation; }
        public override bool Equals(object other) { return other is EntityRef && Equals((EntityRef)other); }
        public override int GetHashCode() { return Id.GetHashCode() ^ (int)Generation; }
        public override string ToString() { return Id + "/" + Generation; }
    }
    public struct PersistentCoopPlayerId : IEquatable<PersistentCoopPlayerId>
    {
        public readonly Guid Value;
        public PersistentCoopPlayerId(Guid value) { if (value == Guid.Empty) throw new ArgumentException("Empty persistent player identity."); Value = value; }
        public bool Equals(PersistentCoopPlayerId other) { return Value.Equals(other.Value); }
        public override bool Equals(object other) { return other is PersistentCoopPlayerId && Equals((PersistentCoopPlayerId)other); }
        public override int GetHashCode() { return Value.GetHashCode(); }
    }
    public interface ICoopIdentityProvider { PersistentCoopPlayerId GetLocalIdentity(); }
    public struct ActorRef
    {
        public readonly ulong SessionId;
        public readonly byte NetworkPlayerId;
        public readonly PersistentCoopPlayerId PersistentId;
        public ActorRef(ulong sessionId, byte player, PersistentCoopPlayerId persistentId)
        { if (sessionId == 0 || (player != 1 && player != 2) || persistentId.Value == Guid.Empty) throw new ArgumentException("Invalid actor."); SessionId = sessionId; NetworkPlayerId = player; PersistentId = persistentId; }
    }
    public enum EntityAuthority : byte { Host = 1 }
    public enum OwnershipKind : byte { System = 0, Session = 1, Player = 2 }
    public struct EntityOwnership
    {
        public readonly OwnershipKind Kind;
        public readonly Guid PersistentPlayer;
        public readonly byte InputPlayerId;
        public EntityOwnership(OwnershipKind kind, Guid persistentPlayer = default(Guid), byte inputPlayerId = 0)
        {
            if ((byte)kind > 2 || inputPlayerId > 2 || (kind == OwnershipKind.Player ? persistentPlayer == Guid.Empty : persistentPlayer != Guid.Empty)) throw new ArgumentException("Invalid ownership.");
            Kind = kind; PersistentPlayer = persistentPlayer; InputPlayerId = inputPlayerId;
        }
    }
    // Mapping schema 1. Door, Window, Pickup and Vehicle have explicit adapters; other mechanics remain reserved.
    public enum EntityTypeId : ushort { TestEntity = 1, Door = 10, Window = 11, Pickup = 12, Container = 13, Npc = 14, Police = 15, Vehicle = 16, MissionObject = 17, Doorbell = 18 }
    public sealed class StaticEntityDescriptor
    {
        public const ushort Schema = 1;
        public string Canonical { get; private set; }
        public StaticEntityDescriptor(string build, string sceneKey, string indexedHierarchy, string componentType, int componentOrdinal, string validatedSemanticKey = "")
        {
            if (string.IsNullOrEmpty(build) || string.IsNullOrEmpty(sceneKey) || string.IsNullOrEmpty(indexedHierarchy) || string.IsNullOrEmpty(componentType) || componentOrdinal < 0) throw new ArgumentException("Incomplete static descriptor.");
            // Length-prefixed fields defeat delimiter ambiguity. Semantic mode requires a caller-validated unique key.
            string mode = string.IsNullOrEmpty(validatedSemanticKey) ? "hierarchy" : "semantic";
            string identity = mode == "hierarchy" ? indexedHierarchy : validatedSemanticKey;
            Canonical = "static-v1" + Field(build) + Field(sceneKey) + Field(mode) + Field(identity) + Field(componentType) + Field(componentOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Encoding.UTF8.GetByteCount(Canonical) > WorldLimits.MaxDescriptorBytes) throw new ArgumentException("Descriptor too long.");
            DiagnosticPath = indexedHierarchy;
        }
        public string DiagnosticPath { get; private set; }
        private static string Field(string value) { return Encoding.UTF8.GetByteCount(value).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value; }
        public NetworkEntityId DeriveId() { return new NetworkEntityId(EntityScope.Static, StableHash.Id(Encoding.UTF8.GetBytes(Canonical))); }
        public static StaticEntityDescriptor ParseCanonical(string canonical)
        {
            if (canonical == null || !canonical.StartsWith("static-v1", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(canonical) > WorldLimits.MaxDescriptorBytes) throw new ArgumentException("Invalid descriptor.");
            byte[] bytes = Encoding.UTF8.GetBytes(canonical); int offset = 9;
            string[] fields = new string[6];
            for (int f = 0; f < fields.Length; f++)
            {
                int start = offset; while (offset < bytes.Length && bytes[offset] >= 48 && bytes[offset] <= 57) offset++;
                int length; if (offset == start || offset >= bytes.Length || bytes[offset++] != 58 || !int.TryParse(Encoding.ASCII.GetString(bytes, start, offset - start - 1), out length) || length < 0 || length > bytes.Length - offset) throw new ArgumentException("Invalid descriptor field.");
                fields[f] = new UTF8Encoding(false, true).GetString(bytes, offset, length); offset += length;
            }
            int ordinal; if (offset != bytes.Length || !int.TryParse(fields[5], out ordinal) || (fields[2] != "hierarchy" && fields[2] != "semantic")) throw new ArgumentException("Invalid descriptor contract.");
            var descriptor = new StaticEntityDescriptor(fields[0], fields[1], fields[2] == "hierarchy" ? fields[3] : "harness", fields[4], ordinal, fields[2] == "semantic" ? fields[3] : "");
            if (descriptor.Canonical != canonical) throw new ArgumentException("Noncanonical descriptor."); return descriptor;
        }
    }
    public static class StableHash
    {
        // SHA-256 truncated to the first 8 digest bytes interpreted little endian. Zero maps to one; collisions fail closed.
        public static ulong Id(byte[] bytes)
        { byte[] hash = Digest(bytes); ulong value = 0; for (int i = 0; i < 8; i++) value |= (ulong)hash[i] << (8 * i); return value == 0 ? 1 : value; }
        public static byte[] Digest(byte[] bytes) { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes); }
        public static bool Equal(byte[] a, byte[] b)
        { if (a == null || b == null || a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
    }
    public static class WorldLimits
    {
        public const int MaxEntities = 4096, MaxEntityPayload = 256, MaxDescriptorBytes = 2048;
        public const int MaxSnapshotBytes = 4 * 1024 * 1024, ChunkPayload = 384;
        public const int MaxChunks = (MaxSnapshotBytes + ChunkPayload - 1) / ChunkPayload;
        public const int MaxBacklog = 1024, MaxBacklogBytes = 512 * 1024;
        public const double AssemblySeconds = 30, SyncSeconds = 40;
        public static bool ValidType(EntityTypeId type) { return type == EntityTypeId.TestEntity || ((ushort)type >= 10 && (ushort)type <= 18); }
    }
}
