using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ThiefSim2Multiplayer.Core.World
{
    public interface INetworkEntityAdapter
    {
        EntityTypeId EntityType { get; }
        bool IsAlive { get; } // Unity adapters must use Unity's destroyed-object comparison here.
        byte[] CaptureState();
        bool ValidateState(byte[] payload);
        void ApplyState(byte[] payload); // Only called on the registry's creating/main thread.
        void Release(); // Clear Unity references; does not destroy original gameplay objects.
    }
    public enum EntityOperation : byte { Spawn = 1, State = 2, Despawn = 3 }
    public sealed class EntityStateRecord
    {
        public EntityRef Entity { get; private set; }
        public EntityTypeId Type { get; private set; }
        public ulong Revision { get; private set; }
        public EntityAuthority Authority { get; private set; }
        public EntityOwnership Ownership { get; private set; }
        public bool Tombstone { get; private set; }
        public string Descriptor { get; private set; }
        private readonly byte[] payload;
        public byte[] Payload { get { return (byte[])payload.Clone(); } }
        public int PayloadLength { get { return payload.Length; } }
        public EntityStateRecord(EntityRef entity, EntityTypeId type, ulong revision, EntityOwnership ownership, byte[] state, bool tombstone = false, string descriptor = "")
        {
            if (!entity.Id.IsValid || entity.Generation == 0 || !WorldLimits.ValidType(type) || revision == 0 || state == null || state.Length > WorldLimits.MaxEntityPayload || (tombstone && state.Length != 0) || (byte)ownership.Kind > 2 || ownership.InputPlayerId > 2 || (ownership.Kind == OwnershipKind.Player ? ownership.PersistentPlayer == Guid.Empty : ownership.PersistentPlayer != Guid.Empty)) throw new ArgumentException("Invalid entity state record.");
            descriptor = descriptor ?? "";
            if (System.Text.Encoding.UTF8.GetByteCount(descriptor) > WorldLimits.MaxDescriptorBytes || (entity.Id.Scope == EntityScope.Dynamic && descriptor.Length != 0)) throw new ArgumentException("Invalid descriptor scope.");
            Entity = entity; Type = type; Revision = revision; Ownership = ownership; Authority = EntityAuthority.Host; payload = (byte[])state.Clone(); Tombstone = tombstone; Descriptor = descriptor;
        }
    }
    public sealed class EntityChange
    {
        public EntityOperation Operation { get; private set; }
        public EntityStateRecord Record { get; private set; }
        public EntityChange(EntityOperation operation, EntityStateRecord record)
        {
            if (record == null || (byte)operation < 1 || (byte)operation > 3 || (operation == EntityOperation.Despawn) != record.Tombstone || (operation == EntityOperation.Spawn && record.Entity.Id.Scope != EntityScope.Dynamic)) throw new ArgumentException("Invalid entity change.");
            Operation = operation; Record = record;
        }
    }
    public sealed class RegistryEntry
    {
        public EntityStateRecord State { get; internal set; }
        public string DiagnosticLabel { get; internal set; } = "";
        internal INetworkEntityAdapter Adapter;
        internal RegistryEntry(EntityStateRecord state, INetworkEntityAdapter adapter) { State = state; Adapter = adapter; }
    }
    public enum ApplyResult { Applied, Stale, WrongGeneration, UnknownEntity, InvalidState, Gap, WrongEpoch }
    public sealed class NetworkEntityRegistry
    {
        private readonly Dictionary<NetworkEntityId, RegistryEntry> entries = new Dictionary<NetworkEntityId, RegistryEntry>();
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly Func<StaticEntityDescriptor, NetworkEntityId> derive;
        private ulong nextDynamic = 1;
        private bool capturing;
        private bool staticRegistrationSealed;
        public bool IsHost { get; private set; }
        public uint SceneEpoch { get; private set; }
        public ulong WorldRevision { get; private set; }
        public int CollisionCount { get; private set; }
        public string LastCollision { get; private set; } = "";
        public string LastBaselineError { get; private set; } = "";
        public bool ProfileBaseline;
        public double BaselineValidationMilliseconds { get; private set; }
        public readonly double[] BaselineAdapterMilliseconds = new double[19];
        public readonly int[] BaselineAdapterCounts = new int[19];
        public int StaticCount { get { return entries.Values.Count(e => !e.State.Tombstone && e.State.Entity.Id.Scope == EntityScope.Static); } }
        public int DynamicCount { get { return entries.Values.Count(e => !e.State.Tombstone && e.State.Entity.Id.Scope == EntityScope.Dynamic); } }
        public int TombstoneCount { get { return entries.Values.Count(e => e.State.Tombstone); } }
        public int Count { get { return entries.Count; } }
        public event Action<EntityChange> Changed;
        public NetworkEntityRegistry(bool host, uint epoch, Func<StaticEntityDescriptor, NetworkEntityId> identityDeriver = null)
        { if (epoch == 0) throw new ArgumentException("Zero epoch."); IsHost = host; SceneEpoch = epoch; derive = identityDeriver ?? (d => d.DeriveId()); }
        public void AssertMainThread() { if (Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("Registry/application must run on the creating/main thread."); }
        private void HostMutation()
        { AssertMainThread(); if (!IsHost || capturing) throw new InvalidOperationException("Only host may mutate; capture cannot reenter mutations."); }
        private ulong Advance() { if (WorldRevision == ulong.MaxValue) throw new InvalidOperationException("Revision exhausted."); return ++WorldRevision; }
        private void Capacity() { if (entries.Count >= WorldLimits.MaxEntities) throw new InvalidOperationException("Registry including tombstones is full; new epoch required."); }
        public EntityRef RegisterStatic(StaticEntityDescriptor descriptor, INetworkEntityAdapter adapter, EntityOwnership ownership)
        {
            AssertMainThread(); if (staticRegistrationSealed) throw new InvalidOperationException("Static registration is bootstrap-only; rebuild at the next epoch.");
            if (capturing || descriptor == null || adapter == null || !adapter.IsAlive || !WorldLimits.ValidType(adapter.EntityType)) throw new ArgumentException("Invalid static binding.");
            var id = derive(descriptor);
            if (id.Scope != EntityScope.Static) throw new ArgumentException("Invalid derived ID scope.");
            RegistryEntry previous;
            if (entries.TryGetValue(id, out previous))
            {
                CollisionCount++; LastCollision = "Collision " + id + " A=" + previous.State.Descriptor + " B=" + descriptor.Canonical + " A-entity=" + previous.DiagnosticLabel + " B-entity=" + descriptor.DiagnosticPath;
                throw new InvalidOperationException(LastCollision); // Duplicate canonical keys are just as unsafe as different-key hash collisions.
            }
            Capacity(); byte[] state = adapter.CaptureState(); ValidatePayload(adapter, state);
            var entity = new EntityRef(id, 1);
            entries.Add(id, new RegistryEntry(new EntityStateRecord(entity, adapter.EntityType, IsHost ? Advance() : 1, ownership, state, false, descriptor.Canonical), adapter) { DiagnosticLabel = descriptor.DiagnosticPath });
            return entity;
        }
        public EntityRef Spawn(INetworkEntityAdapter adapter, EntityOwnership ownership)
        {
            HostMutation(); Capacity(); if (adapter == null || !adapter.IsAlive || nextDynamic == ulong.MaxValue) throw new InvalidOperationException("Invalid dynamic allocation.");
            byte[] state = adapter.CaptureState(); ValidatePayload(adapter, state);
            var entity = new EntityRef(new NetworkEntityId(EntityScope.Dynamic, nextDynamic++), 1);
            var record = new EntityStateRecord(entity, adapter.EntityType, Advance(), ownership, state);
            entries.Add(entity.Id, new RegistryEntry(record, adapter)); Changed?.Invoke(new EntityChange(EntityOperation.Spawn, record)); return entity;
        }
        public bool CommitState(EntityRef entity)
        {
            HostMutation(); var adapter = GetBinding(entity); if (adapter == null) return false;
            var previous = entries[entity.Id].State; byte[] state = adapter.CaptureState(); ValidatePayload(adapter, state);
            var record = new EntityStateRecord(entity, previous.Type, Advance(), previous.Ownership, state, false, previous.Descriptor);
            entries[entity.Id].State = record; Changed?.Invoke(new EntityChange(EntityOperation.State, record)); return true;
        }
        public bool Despawn(EntityRef entity)
        {
            HostMutation(); RegistryEntry entry;
            if (!entries.TryGetValue(entity.Id, out entry) || !entry.State.Entity.Equals(entity) || entry.State.Tombstone) return false;
            var previous = entry.State; DespawnPresentation(entry); Release(entry);
            entry.State = new EntityStateRecord(entity, previous.Type, Advance(), previous.Ownership, new byte[0], true, previous.Descriptor);
            Changed?.Invoke(new EntityChange(EntityOperation.Despawn, entry.State)); return true;
        }
        public RegistryEntry Lookup(EntityRef entity)
        { AssertMainThread(); RegistryEntry entry; return entries.TryGetValue(entity.Id, out entry) && entry.State.Entity.Equals(entity) ? entry : null; }
        public INetworkEntityAdapter GetBinding(EntityRef entity)
        {
            var entry = Lookup(entity); if (entry == null || entry.State.Tombstone) return null;
            if (entry.Adapter != null && !entry.Adapter.IsAlive) Release(entry);
            return entry.Adapter;
        }
        public void SweepDestroyedBindings()
        { AssertMainThread(); foreach (var entry in entries.Values) if (entry.Adapter != null && !entry.Adapter.IsAlive) Release(entry); }
        public void ReleaseBindings() { AssertMainThread(); foreach (var entry in entries.Values) Release(entry); }
        private static void Release(RegistryEntry entry) { var adapter = entry.Adapter; entry.Adapter = null; if (adapter != null) adapter.Release(); }
        private static void DespawnPresentation(RegistryEntry entry)
        { (entry.Adapter as Inventory.IDespawnPresentation)?.ApplyDespawn(); }
        private static void ValidatePayload(INetworkEntityAdapter adapter, byte[] payload)
        { if (payload == null || payload.Length > WorldLimits.MaxEntityPayload || !WorldLimits.ValidType(adapter.EntityType) || !adapter.ValidateState(payload)) throw new ArgumentException("Adapter payload invalid."); }
        public EntityStateRecord[] Capture()
        {
            AssertMainThread(); if (capturing) throw new InvalidOperationException("Capture reentry."); capturing = true;
            try
            {
                // Capture cached authoritative records, committed explicitly through host mutation APIs.
                // Adapters cannot silently change canonical state during serialization.
                return entries.OrderBy(p => p.Key).Select(p => p.Value.State).ToArray();
            }
            finally { capturing = false; }
        }
        public void SealStaticRegistration() { AssertMainThread(); staticRegistrationSealed = true; }
        // Vehicle transactions need only immutable vehicle records, not a sorted
        // snapshot of every door, window and pickup in the current mission.
        public EntityStateRecord[] Capture(EntityTypeId type)
        {
            AssertMainThread();
            return entries.Values.Where(e => e.State.Type == type).Select(e => e.State).ToArray();
        }
        public EntityRef? FindBinding(INetworkEntityAdapter adapter)
        {
            AssertMainThread(); if (adapter == null || !adapter.IsAlive) return null;
            foreach (var entry in entries.Values) if (!entry.State.Tombstone && ReferenceEquals(entry.Adapter, adapter)) return entry.State.Entity;
            return null;
        }
        public string DevelopmentSummary()
        {
            AssertMainThread(); var records = Capture();
            foreach (var record in records) if (record.Revision > WorldRevision)
                return "entities=" + Count + "; revision=" + WorldRevision + "; SHA256=PENDING BASELINE";
            byte[] canonical = SnapshotCodec.Encode(new InitialSnapshot(1, SceneEpoch, 1, WorldRevision, records));
            return "entities=" + Count + "; revision=" + WorldRevision + "; SHA256=" + BitConverter.ToString(StableHash.Digest(canonical)).Replace("-", "");
        }
        public bool ApplyBaseline(EntityStateRecord[] records, ulong baseRevision, Func<EntityTypeId, INetworkEntityAdapter> factory)
        {
            AssertMainThread(); LastBaselineError = "";
            Array.Clear(BaselineAdapterMilliseconds, 0, BaselineAdapterMilliseconds.Length);
            Array.Clear(BaselineAdapterCounts, 0, BaselineAdapterCounts.Length);
            BaselineValidationMilliseconds = 0;
            long validationStart = ProfileBaseline ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (IsHost || records == null || records.Length > WorldLimits.MaxEntities) return BaselineRejected("Invalid baseline bounds/role");
            var staged = new Dictionary<NetworkEntityId, RegistryEntry>(); var created = new List<INetworkEntityAdapter>();
            try
            {
                foreach (var record in records)
                {
                    if (record.Revision > baseRevision || staged.ContainsKey(record.Entity.Id)) return BaselineRejected("Revision/duplicate " + record.Entity);
                    INetworkEntityAdapter adapter = null;
                    if (record.Entity.Id.Scope == EntityScope.Static)
                    {
                        RegistryEntry local;
                        if (!entries.TryGetValue(record.Entity.Id, out local) || local.State.Descriptor != record.Descriptor || local.State.Type != record.Type || !local.State.Entity.Equals(record.Entity)) return BaselineRejected("Static manifest " + record.Entity);
                        adapter = local.Adapter;
                    }
                    else if (!record.Tombstone) { adapter = factory(record.Type); if (adapter != null) created.Add(adapter); }
                    if (!record.Tombstone && (adapter == null || !adapter.IsAlive || adapter.EntityType != record.Type || !adapter.ValidateState(record.Payload))) return BaselineRejected("Adapter/payload " + record.Entity);
                    staged.Add(record.Entity.Id, new RegistryEntry(record, record.Tombstone ? null : adapter));
                }
                // A missing live authored entity is a manifest mismatch, not permission to ignore it.
                foreach (var local in entries.Values) if (local.State.Entity.Id.Scope == EntityScope.Static && !staged.ContainsKey(local.State.Entity.Id)) return BaselineRejected("Missing static " + local.State.Entity);
                if (ProfileBaseline) BaselineValidationMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - validationStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                using (new NetworkApplyScope(true))
                    foreach (var entry in staged.Values) if (!entry.State.Tombstone)
                    {
                        long start = ProfileBaseline ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        entry.Adapter.ApplyState(entry.State.Payload);
                        if (ProfileBaseline) { int type = (int)entry.State.Type; BaselineAdapterCounts[type]++; BaselineAdapterMilliseconds[type] += (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
                    }
                foreach (var old in entries.Values) if (!staged.ContainsKey(old.State.Entity.Id) || !ReferenceEquals(staged[old.State.Entity.Id].Adapter, old.Adapter))
                { if (staged.ContainsKey(old.State.Entity.Id) && staged[old.State.Entity.Id].State.Tombstone) DespawnPresentation(old); Release(old); }
                entries.Clear(); foreach (var entry in staged) entries.Add(entry.Key, entry.Value);
                WorldRevision = baseRevision; created.Clear(); return true;
            }
            finally { foreach (var adapter in created) adapter.Release(); }
        }
        private bool BaselineRejected(string reason) { LastBaselineError = reason; return false; }
        public ApplyResult ApplyChange(uint epoch, EntityChange change, Func<EntityTypeId, INetworkEntityAdapter> factory)
        {
            AssertMainThread(); if (IsHost || epoch != SceneEpoch) return ApplyResult.WrongEpoch;
            var state = change.Record;
            if (state.Revision <= WorldRevision) return ApplyResult.Stale;
            if (WorldRevision == ulong.MaxValue || state.Revision != WorldRevision + 1) return ApplyResult.Gap;
            RegistryEntry existing; entries.TryGetValue(state.Entity.Id, out existing);
            if (existing != null && !existing.State.Entity.Equals(state.Entity)) return ApplyResult.WrongGeneration;
            if (existing != null && (existing.State.Type != state.Type || (state.Descriptor.Length != 0 && existing.State.Descriptor != state.Descriptor))) return ApplyResult.InvalidState;
            if (existing != null && state.Entity.Id.Scope == EntityScope.Static)
                state = new EntityStateRecord(state.Entity, state.Type, state.Revision, state.Ownership, state.Payload, state.Tombstone, existing.State.Descriptor);
            if (change.Operation == EntityOperation.Spawn)
            {
                if (existing != null) return ApplyResult.InvalidState; // Never revive a tombstone in the same generation.
                if (entries.Count >= WorldLimits.MaxEntities) return ApplyResult.InvalidState;
                var adapter = factory(state.Type);
                if (adapter == null) return ApplyResult.InvalidState;
                try { ValidatePayload(adapter, state.Payload); adapter.ApplyState(state.Payload); entries.Add(state.Entity.Id, new RegistryEntry(state, adapter)); }
                catch { adapter.Release(); throw; }
            }
            else
            {
                if (existing == null) return ApplyResult.UnknownEntity;
                if (existing.State.Tombstone) return ApplyResult.Stale;
                if (change.Operation == EntityOperation.Despawn) { DespawnPresentation(existing); Release(existing); existing.State = state; }
                else
                {
                    var adapter = GetBinding(state.Entity); if (adapter == null || !adapter.ValidateState(state.Payload)) return ApplyResult.InvalidState;
                    adapter.ApplyState(state.Payload); existing.State = state;
                }
            }
            WorldRevision = state.Revision; return ApplyResult.Applied;
        }
        public void Reset(bool host, uint epoch)
        {
            AssertMainThread(); if (epoch == 0) throw new ArgumentException("Zero epoch.");
            if (IsHost && host && WorldRevision != 0 && epoch <= SceneEpoch) throw new InvalidOperationException("Canonical host revision cannot reset within the same/older epoch.");
            foreach (var entry in entries.Values) Release(entry); entries.Clear(); IsHost = host; SceneEpoch = epoch; WorldRevision = 0; CollisionCount = 0; LastCollision = ""; staticRegistrationSealed = false;
            // Dynamic IDs never reuse even across epochs within this registry lifetime.
        }
    }
    // No Unity objects, save state, or game mechanics. Schema byte + bool + int + float.
    public sealed class TestEntityAdapter : INetworkEntityAdapter
    {
        public bool Active = true;
        public int Value;
        public float PositionLikeValue;
        public EntityTypeId EntityType { get { return EntityTypeId.TestEntity; } }
        public bool IsAlive { get; private set; } = true;
        public byte[] CaptureState()
        {
            using (var stream = new System.IO.MemoryStream()) using (var writer = new System.IO.BinaryWriter(stream))
            { writer.Write((byte)1); writer.Write(Active); writer.Write(Value); writer.Write(PositionLikeValue); return stream.ToArray(); }
        }
        public bool ValidateState(byte[] payload)
        { return payload != null && payload.Length == 10 && payload[0] == 1 && payload[1] <= 1 && Networking.Protocol.Finite(BitConverter.ToSingle(payload, 6)); }
        public void ApplyState(byte[] payload)
        {
            if (!ValidateState(payload)) throw new ArgumentException("Invalid test state.");
            using (var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(payload))) { reader.ReadByte(); Active = reader.ReadBoolean(); Value = reader.ReadInt32(); PositionLikeValue = reader.ReadSingle(); }
        }
        public void Release() { IsAlive = false; }
    }
}
