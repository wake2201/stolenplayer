using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.TransportHarness;

internal static class WorldTests
{
    private static Action<bool, string> check;
    private static EntityOwnership Ownership => new EntityOwnership(OwnershipKind.Session);
    private static INetworkEntityAdapter Factory(EntityTypeId type) => type == EntityTypeId.TestEntity ? new TestEntityAdapter() : null;
    private static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }
    private static EntityStateRecord Record(ulong id = 1, uint generation = 1, ulong revision = 1, int value = 10, bool tombstone = false) =>
        new EntityStateRecord(new EntityRef(new NetworkEntityId(EntityScope.Dynamic, id), generation), EntityTypeId.TestEntity, revision, Ownership, tombstone ? Array.Empty<byte>() : new TestEntityAdapter { Value = value }.CaptureState(), tombstone);
    private static EntityChange Change(EntityOperation operation, ulong revision, uint generation = 1, ulong id = 1, int value = 10) => new EntityChange(operation, Record(id, generation, revision, value, operation == EntityOperation.Despawn));
    private static StaticEntityDescriptor Descriptor(string path = "DOMY[0]/Door[1]", string scene = "MadisonSt", string type = "door-v1", int ordinal = 0, string semantic = "") => new StaticEntityDescriptor("build", scene, path, type, ordinal, semantic);
    public static void Run(Action<bool, string> assertion)
    { check = assertion; Identity(); Registry(); Snapshots(); Codecs(); Synchronization(); Transport(); }
    private static void Identity()
    {
        var descriptor = Descriptor(); var id = descriptor.DeriveId();
        check(id.Equals(Descriptor().DeriveId()) && descriptor.Canonical == Descriptor().Canonical, "static identity deterministic repeated derivation");
        check(StableHash.Id(Encoding.UTF8.GetBytes("abc")) == 0xEACF018FBF1678BAUL, "explicit SHA256 truncation known vector, little endian");
        check(descriptor.Canonical == "static-v15:build9:MadisonSt9:hierarchy15:DOMY[0]/Door[1]7:door-v11:0", "static descriptor canonical UTF8 length-prefixed schema vector");
        foreach (var other in new[] { Descriptor("DOMY[0]/Door[2]"), Descriptor(scene: "MainScene"), Descriptor(type: "window-v1"), Descriptor(ordinal: 1), new StaticEntityDescriptor("other", "MadisonSt", "DOMY[0]/Door[1]", "door-v1", 0) })
            check(!id.Equals(other.DeriveId()), "static identity distinguishes " + other.Canonical);
        check(Descriptor(semantic: "residence:102/door:1").DeriveId().Equals(Descriptor(path: "changed[9]", semantic: "residence:102/door:1").DeriveId()), "validated semantic key supersedes diagnostic hierarchy");
        check(!Descriptor(semantic: "a/b").DeriveId().Equals(Descriptor(semantic: "a").DeriveId()), "semantic descriptors retain unambiguous lengths");
        check(Throws(() => new NetworkEntityId(EntityScope.Dynamic, 0)) && Throws(() => new NetworkEntityId((EntityScope)255, 1)), "invalid entity ID scope and zero rejected");
        check(Throws(() => new EntityRef(id, 0)) && Throws(() => new EntityRef(default, 1)), "zero generation/default entity rejected");
        check(Throws(() => Descriptor(path: "")) && Throws(() => Descriptor(ordinal: -1)) && Throws(() => Descriptor(path: new string('x', 2049))), "static descriptor missing/negative/oversize rejected");
        var persistent = new PersistentCoopPlayerId(Guid.NewGuid()); var actor1 = new ActorRef(1, 2, persistent); var actor2 = new ActorRef(2, 1, persistent);
        check(actor1.PersistentId.Equals(actor2.PersistentId) && actor1.SessionId != actor2.SessionId, "persistent identity survives changed session-local actor assignment");
        check(Throws(() => new PersistentCoopPlayerId(Guid.Empty)) && Throws(() => new ActorRef(1, 3, persistent)), "invalid persistent/actor identities rejected");
        var owner = new EntityOwnership(OwnershipKind.Player, persistent.Value, 2);
        check(owner.Kind == OwnershipKind.Player && owner.InputPlayerId == 2 && EntityAuthority.Host == (EntityAuthority)1, "host authority independent of personal/input ownership");
        check(Throws(() => new EntityOwnership(OwnershipKind.Player)) && Throws(() => new EntityOwnership(OwnershipKind.Session, persistent.Value)) && Throws(() => new EntityOwnership(OwnershipKind.System, inputPlayerId: 3)), "invalid ownership metadata rejected");
    }
    private static void Registry()
    {
        var host = new NetworkEntityRegistry(true, 1); var descriptor = Descriptor(); var staticRef = host.RegisterStatic(descriptor, new TestEntityAdapter(), Ownership);
        check(staticRef.Id.Scope == EntityScope.Static && host.StaticCount == 1 && host.WorldRevision == 1, "static registry binding and first authoritative revision");
        check(Throws(() => host.RegisterStatic(descriptor, new TestEntityAdapter(), Ownership)) && host.CollisionCount == 1 && host.LastCollision.Contains("A=") && host.LastCollision.Contains("B="), "duplicate canonical ID rejected with collision diagnostics");
        var colliding = new NetworkEntityRegistry(true, 1, _ => new NetworkEntityId(EntityScope.Static, 99)); colliding.RegisterStatic(Descriptor(), new TestEntityAdapter(), Ownership);
        check(Throws(() => colliding.RegisterStatic(Descriptor(path: "other[1]"), new TestEntityAdapter(), Ownership)) && colliding.Count == 1, "forced different-descriptor hash collision fails closed");
        var adapter = new TestEntityAdapter { Value = 10 }; var a = host.Spawn(adapter, Ownership); var b = host.Spawn(new TestEntityAdapter(), Ownership);
        check(a.Id.Scope == EntityScope.Dynamic && a.Id.Value == 1 && b.Id.Value == 2 && a.Generation == 1, "host allocates monotonic nonreused dynamic IDs");
        var client = new NetworkEntityRegistry(false, 1); check(Throws(() => client.Spawn(new TestEntityAdapter(), Ownership)), "clients cannot allocate authoritative IDs");
        check(host.GetBinding(a) == adapter && host.Lookup(new EntityRef(a.Id, 2)) == null, "binding lookup verifies generation");
        check(host.FindBinding(adapter).Value.Equals(a), "reverse local adapter binding resolves full entity reference");
        ulong revision = host.WorldRevision; adapter.Value = 11; check(host.CommitState(a) && host.WorldRevision == revision + 1 && host.Lookup(a).State.Revision == host.WorldRevision, "global revision is per-entity last-change revision");
        check(host.Despawn(a) && host.GetBinding(a) == null && host.TombstoneCount == 1 && !adapter.IsAlive, "despawn retains metadata and releases binding");
        check(Throws(() => host.Reset(true, 1)), "host canonical revision cannot reset within an established epoch");
        revision = host.WorldRevision; check(!host.Despawn(a) && host.WorldRevision == revision && !host.CommitState(a), "duplicate despawn and state after tombstone idempotent on host");
        var life = (TestEntityAdapter)host.GetBinding(b); life.Release(); host.SweepDestroyedBindings(); check(host.GetBinding(b) == null && host.Lookup(b) != null, "destroyed binding cleared without retaining adapter");
        host.Reset(true, 2); var newRef = host.Spawn(new TestEntityAdapter(), Ownership);
        check(host.Count == 1 && host.SceneEpoch == 2 && newRef.Id.Value == 3 && host.Lookup(staticRef) == null, "epoch cleanup releases entries without reusing dynamic IDs");
        Exception wrongThread = null; var thread = new Thread(() => { try { host.Lookup(newRef); } catch (Exception error) { wrongThread = error; } }); thread.Start(); thread.Join();
        check(wrongThread is InvalidOperationException, "registry lookup/application asserts main thread");
        var replica = new NetworkEntityRegistry(false, 1);
        check(replica.ApplyBaseline(new[] { Record(generation: 4) }, 1, Factory), "baseline can establish generation four without local ID allocation");
        check(replica.ApplyChange(1, Change(EntityOperation.State, 2, generation: 3), Factory) == ApplyResult.WrongGeneration, "late generation three cannot mutate generation four");
        check(replica.ApplyChange(1, Change(EntityOperation.State, 2, generation: 5), Factory) == ApplyResult.WrongGeneration, "new generation requires explicit baseline/rebinding rather than aliasing");
        check(replica.ApplyChange(2, Change(EntityOperation.State, 2, generation: 4), Factory) == ApplyResult.WrongEpoch, "wrong scene epoch rejected");
        check(replica.ApplyChange(1, Change(EntityOperation.State, 3, generation: 4), Factory) == ApplyResult.Gap && replica.WorldRevision == 1, "revision gap cannot skip missing entity mutation");
        check(replica.ApplyChange(1, Change(EntityOperation.State, 2, generation: 4, value: 12), Factory) == ApplyResult.Applied, "new contiguous semantic state applied");
        check(replica.ApplyChange(1, Change(EntityOperation.State, 1, generation: 4, value: 11), Factory) == ApplyResult.Stale && ((TestEntityAdapter)replica.GetBinding(Record(generation: 4).Entity)).Value == 12, "older state cannot overwrite newer entity state");
        check(replica.ApplyChange(1, Change(EntityOperation.Despawn, 3, generation: 4), Factory) == ApplyResult.Applied && replica.TombstoneCount == 1, "client authoritative tombstone applied");
        check(replica.ApplyChange(1, Change(EntityOperation.Despawn, 3, generation: 4), Factory) == ApplyResult.Stale, "duplicate despawn safe on client");
        check(replica.ApplyChange(1, Change(EntityOperation.State, 2, generation: 4), Factory) == ApplyResult.Stale && replica.GetBinding(Record(generation: 4).Entity) == null, "late state after despawn remains absent");
        check(replica.ApplyChange(1, Change(EntityOperation.Spawn, 4, generation: 4), Factory) == ApplyResult.InvalidState, "same-generation spawn cannot revive a tombstone");
        check(replica.ApplyChange(1, Change(EntityOperation.State, 4, id: 99), Factory) == ApplyResult.UnknownEntity, "unknown dynamic state requires spawn/resync");
        check(replica.ApplyChange(1, Change(EntityOperation.Spawn, 4, id: 99, value: 40), Factory) == ApplyResult.Applied && replica.DynamicCount == 1, "explicit dynamic spawn creates adapter through factory");
        check(replica.ApplyChange(1, Change(EntityOperation.Spawn, 4, id: 99), Factory) == ApplyResult.Stale, "duplicate spawn idempotent at existing revision");
        var reuse = new NetworkEntityRegistry(false, 1); reuse.ApplyBaseline(new[] { Record(generation: 4, revision: 3, tombstone: true) }, 3, Factory);
        check(reuse.ApplyBaseline(new[] { Record(generation: 5, revision: 4) }, 4, Factory) && reuse.ApplyChange(1, Change(EntityOperation.State, 5, generation: 4), Factory) == ApplyResult.WrongGeneration, "generation five baseline after generation four tombstone rejects late generation four packet");
        var authoredHost = new NetworkEntityRegistry(true, 1); var authoredClient = new NetworkEntityRegistry(false, 1);
        var authored = authoredHost.RegisterStatic(Descriptor(), new TestEntityAdapter { Value = 42 }, Ownership); authoredClient.RegisterStatic(Descriptor(), new TestEntityAdapter(), Ownership);
        check(authoredClient.ApplyBaseline(authoredHost.Capture(), authoredHost.WorldRevision, Factory) && ((TestEntityAdapter)authoredClient.GetBinding(authored)).Value == 42, "static snapshot matches canonical local descriptor before apply");
        var mismatch = new NetworkEntityRegistry(false, 1); mismatch.RegisterStatic(Descriptor(path: "other[0]"), new TestEntityAdapter(), Ownership);
        check(!mismatch.ApplyBaseline(authoredHost.Capture(), authoredHost.WorldRevision, Factory), "static registry mismatch never applies state to another object");
        check(!authoredClient.ApplyBaseline(Array.Empty<EntityStateRecord>(), 0, Factory), "missing authored entity in snapshot rejected as manifest mismatch");
        var full = new NetworkEntityRegistry(true, 1); for (int i = 0; i < WorldLimits.MaxEntities; i++) full.Spawn(new TestEntityAdapter(), Ownership);
        check(Throws(() => full.Spawn(new TestEntityAdapter(), Ownership)) && full.Count == WorldLimits.MaxEntities, "registry hard bound includes all live entities");
        full.Despawn(full.Capture()[0].Entity); check(Throws(() => full.Spawn(new TestEntityAdapter(), Ownership)), "tombstone still consumes bounded registry capacity");
        check(Throws(() => Record(revision: 0)) && Throws(() => new EntityStateRecord(Record().Entity, (EntityTypeId)65535, 1, Ownership, new byte[0])), "zero revision/invalid entity type rejected");
        check(Throws(() => new EntityStateRecord(Record().Entity, EntityTypeId.TestEntity, 1, Ownership, new byte[257])), "entity payload length hard bound");
        var captured = Record(); var copied = captured.Payload; copied[2] = 99; check(captured.Payload[2] == 10, "canonical record payload immutable across caller mutation");
        var release = new NetworkEntityRegistry(true, 1); var releasedAdapter = new TestEntityAdapter(); var releasedRef = release.Spawn(releasedAdapter, Ownership); release.ReleaseBindings();
        check(!releasedAdapter.IsAlive && release.GetBinding(releasedRef) == null && release.Count == 1, "terminal registry disposal releases bindings while managed diagnostics may remain");
    }
    private static void Snapshots()
    {
        var snapshot = new InitialSnapshot(123, 7, 99, 10, Enumerable.Range(1, 40).Select(i => Record((ulong)i, revision: (ulong)(i % 10 + 1), value: i)).ToArray());
        var chunks = SnapshotCodec.Chunk(snapshot); var metadata = chunks[0].Metadata; byte[] bytes = SnapshotCodec.Encode(snapshot);
        check(chunks.Length > 1 && chunks.All(c => c.Payload.Length <= 384), "world snapshot chunked into bounded UDP-friendly payloads");
        check(SnapshotCodec.TryDecode(bytes, metadata, out var decoded) && decoded.Entities.Length == 40 && decoded.BaseRevision == 10, "versioned snapshot canonical encode/decode");
        var assembler = new SnapshotAssembler(metadata, 0); foreach (var chunk in chunks.Reverse()) assembler.Add(chunk, 1);
        check(assembler.TryFinish(1, out decoded) && assembler.ReceivedBytes == bytes.Length, "out-of-order chunks assemble complete snapshot");
        check(assembler.Add(chunks[0], 1) == AssemblyResult.Duplicate, "matching duplicate chunk safe after completion");
        var partial = new SnapshotAssembler(metadata, 0); partial.Add(chunks[0], 1);
        var corrupt = chunks[0].Payload; corrupt[0] ^= 1;
        check(partial.Add(new SnapshotChunk(metadata, 0, corrupt), 2) == AssemblyResult.Conflict && !partial.TryFinish(2, out _), "conflicting duplicate invalidates bounded assembly");
        var wrongId = new SnapshotMetadata(123, 7, 100, 10, bytes.Length, chunks.Length, metadata.Hash);
        check(new SnapshotAssembler(metadata, 0).Add(new SnapshotChunk(wrongId, 0, chunks[0].Payload), 1) == AssemblyResult.Rejected, "unknown snapshot ID chunk rejected without extra assembly");
        var wrongEpoch = new SnapshotMetadata(123, 8, 99, 10, bytes.Length, chunks.Length, metadata.Hash);
        check(new SnapshotAssembler(metadata, 0).Add(new SnapshotChunk(wrongEpoch, 0, chunks[0].Payload), 1) == AssemblyResult.Rejected, "wrong epoch chunk rejected");
        var expired = new SnapshotAssembler(metadata, 0);
        check(expired.Add(chunks[0], 30) == AssemblyResult.Expired && !expired.TryFinish(30, out _), "snapshot reassembly exact deadline enforced");
        var wrongHash = new SnapshotMetadata(123, 7, 99, 10, bytes.Length, chunks.Length, new byte[32]); var hashAssembly = new SnapshotAssembler(wrongHash, 0); AssemblyResult result = default;
        foreach (var chunk in chunks) result = hashAssembly.Add(new SnapshotChunk(wrongHash, chunk.Index, chunk.Payload), 1);
        check(result == AssemblyResult.InvalidHash && !hashAssembly.TryFinish(1, out _), "final SHA256 checksum mismatch rejected");
        check(!SnapshotCodec.TryDecode(bytes.Take(bytes.Length - 1).ToArray(), metadata, out _), "truncated snapshot length rejected");
        check(Throws(() => new SnapshotMetadata(1, 1, 1, 0, WorldLimits.MaxSnapshotBytes + 1, 1, new byte[32])), "claimed giant snapshot rejected before allocation");
        check(Throws(() => new SnapshotMetadata(1, 1, 1, 0, 6, WorldLimits.MaxChunks + 1, new byte[32])) && Throws(() => new SnapshotMetadata(1, 1, 1, 0, 6, 2, new byte[32])), "chunk count overflow and inconsistent total rejected");
        check(Throws(() => new SnapshotChunk(metadata, chunks.Length, Array.Empty<byte>())) && Throws(() => new SnapshotChunk(metadata, 0, new byte[385])), "chunk index/payload length validation");
        check(Throws(() => new InitialSnapshot(1, 1, 1, 1, new[] { Record(), Record() })), "duplicate snapshot entity IDs rejected");
        check(Throws(() => new InitialSnapshot(1, 1, 1, 0, new[] { Record() })), "entry revision after baseline cannot enter snapshot");
        check(Throws(() => new InitialSnapshot(1, 1, 1, 1, new EntityStateRecord[4097])), "snapshot entity count hard bound");
        var bogus = new byte[] { 1, 0, 255, 255, 255, 127 }; var bogusMeta = new SnapshotMetadata(1, 1, 1, 1, 6, 1, StableHash.Digest(bogus));
        check(!SnapshotCodec.TryDecode(bogus, bogusMeta, out _), "malicious entry count rejected before array allocation");
        var maximum = new SnapshotMetadata(1, 1, 1, 0, WorldLimits.MaxSnapshotBytes, WorldLimits.MaxChunks, new byte[32]);
        check(new SnapshotAssembler(maximum, 0).Metadata.TotalBytes == 4194304, "exact maximum assembly allocation remains bounded at four MiB");
        var backlog = new DeltaBacklog(); backlog.Begin(10); check(backlog.Add(Change(EntityOperation.State, 11)) && backlog.Add(Change(EntityOperation.Despawn, 12)), "post-baseline backlog maintains contiguous global revisions");
        check(backlog.Drain().Select(c => c.Record.Revision).SequenceEqual(new ulong[] { 11, 12 }) && backlog.Count == 0, "backlog drains ordered without losing tombstone");
        backlog.Begin(0); for (ulong i = 1; i <= 1024; i++) if (!backlog.Add(Change(EntityOperation.State, i))) throw new Exception("Unexpected backlog overflow");
        check(!backlog.Add(Change(EntityOperation.State, 1025)) && backlog.Overflowed && backlog.Count == 0 && backlog.Bytes == 0, "backlog overflow fails closed and requests fresh baseline");
        backlog.Begin(50); check(!backlog.Add(Change(EntityOperation.State, 52)) && backlog.Overflowed, "backlog missing revision cannot silently bridge snapshot");
    }
    private static void Codecs()
    {
        var chunk = SnapshotCodec.Chunk(new InitialSnapshot(1, 1, 1, 1, new[] { Record() }))[0];
        var messages = new[] {
            new WorldMessage { Type = WorldMessageType.SnapshotBegin, SessionId = 1, SceneEpoch = 1, Metadata = chunk.Metadata },
            new WorldMessage { Type = WorldMessageType.SnapshotChunk, SessionId = 1, SceneEpoch = 1, Chunk = chunk },
            new WorldMessage { Type = WorldMessageType.SnapshotAck, SessionId = 1, SceneEpoch = 1, SnapshotId = 1, Revision = 1 },
            new WorldMessage { Type = WorldMessageType.WorldReady, SessionId = 1, SceneEpoch = 1, SnapshotId = 1, Revision = 1 },
            new WorldMessage { Type = WorldMessageType.ResyncRequest, SessionId = 1, SceneEpoch = 1 },
            new WorldMessage { Type = WorldMessageType.EntityChange, SessionId = 1, SceneEpoch = 1, Change = Change(EntityOperation.Spawn, 1) },
            new WorldMessage { Type = WorldMessageType.EntityChange, SessionId = 1, SceneEpoch = 1, Change = Change(EntityOperation.State, 2) },
            new WorldMessage { Type = WorldMessageType.EntityChange, SessionId = 1, SceneEpoch = 1, Change = Change(EntityOperation.Despawn, 3) } };
        foreach (var message in messages)
        {
            var bytes = WorldProtocol.Encode(message);
            check(bytes.Length <= 512 && WorldProtocol.TryDecode(bytes, 0, bytes.Length, out var decoded) && decoded.Type == message.Type, "world codec roundtrip " + message.Type + " " + message.Change?.Operation);
            bool anyAccepted = false; for (int i = 0; i < bytes.Length; i++) anyAccepted |= WorldProtocol.TryDecode(bytes, 0, i, out _);
            check(!anyAccepted && !WorldProtocol.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), 0, bytes.Length + 1, out _), "world codec rejects all truncations/trailing byte " + message.Type + " " + message.Change?.Operation);
            var padded = new byte[bytes.Length + 12]; Array.Copy(bytes, 0, padded, 7, bytes.Length);
            check(WorldProtocol.TryDecode(padded, 7, bytes.Length, out _), "world codec pooled offset " + message.Type + " " + message.Change?.Operation);
        }
        var deltaBytes = WorldProtocol.Encode(messages[6]);
        void RejectMutation(int offset, byte[] replacement, string name) { var mutated = (byte[])deltaBytes.Clone(); Array.Copy(replacement, 0, mutated, offset, replacement.Length); check(!WorldProtocol.TryDecode(mutated, 0, mutated.Length, out _), name); }
        RejectMutation(14, new byte[] { 255 }, "wire invalid entity scope"); RejectMutation(15, new byte[8], "wire zero entity ID"); RejectMutation(23, new byte[4], "wire zero generation");
        RejectMutation(27, new byte[] { 255, 255 }, "wire invalid entity type"); RejectMutation(29, new byte[8], "wire zero revision"); RejectMutation(37, new byte[] { 2 }, "wire client authority forbidden");
        RejectMutation(56, new byte[] { 2 }, "wire invalid tombstone boolean"); RejectMutation(57, new byte[] { 1, 1 }, "wire oversized entity payload length");
        var begin = WorldProtocol.Encode(messages[0]); Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, begin, 29, 4);
        check(!WorldProtocol.TryDecode(begin, 0, begin.Length, out _), "wire huge claimed snapshot rejected");
        var ack = WorldProtocol.Encode(messages[2]); Array.Clear(ack, 1, 8); check(!WorldProtocol.TryDecode(ack, 0, ack.Length, out _), "wire zero SessionId rejected");
        check(!WorldProtocol.TryDecode(deltaBytes, int.MaxValue, int.MaxValue, out _) && !WorldProtocol.TryDecode(new byte[513], 0, 513, out _), "world parser slice/packet bound validation");
        var rng = new Random(3003);
        foreach (var valid in messages)
        {
            byte[] source = WorldProtocol.Encode(valid);
            for (int i = 0; i < 2000; i++) { var bytes = (byte[])source.Clone(); for (int j = 0; j < 4; j++) bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256); WorldProtocol.TryDecode(bytes, 0, bytes.Length, out _); }
        }
        check(true, "16,000 bounded valid-envelope parser mutations cannot throw");
        for (int i = 0; i < 20000; i++) { var bytes = new byte[rng.Next(0, 600)]; rng.NextBytes(bytes); WorldProtocol.TryDecode(bytes, rng.Next(-5, 605), rng.Next(-5, 605), out _); }
        check(true, "20,000 malformed world packets/slices cannot throw or allocate from claimed lengths");
        for (int i = 0; i < 5000; i++) { var bytes = new byte[rng.Next(6, 1000)]; rng.NextBytes(bytes); var metadata = new SnapshotMetadata(1, 1, 1, 1, bytes.Length, (bytes.Length + 383) / 384, StableHash.Digest(bytes)); SnapshotCodec.TryDecode(bytes, metadata, out _); }
        check(true, "5,000 bounded checksum-valid malformed snapshot payloads cannot throw");
        check(NetworkManager.ValidateHandshake(new Message { ProtocolVersion = 2 }, Protocol.Version, "", false).Contains("protocol"), "protocol two explicitly incompatible with world join protocol four");
    }
    private static void Synchronization()
    {
        var toClient = new List<WorldMessage>(); var toHost = new List<WorldMessage>();
        var host = new WorldReplication(m => toClient.Add(m)); var client = new WorldReplication(m => toHost.Add(m));
        var fixture = new WorldSyncFixture(host); fixture.InitializeHost(); client.TestEntitiesEnabled = true;
        host.BeginConnection(true, 123, 0); client.BeginConnection(false, 123, 0);
        check(host.State == WorldSyncState.Connected && client.State == WorldSyncState.Connected, "transport connected distinct from world readiness");
        host.Tick(1, true); var metadata = toClient[0].Metadata;
        check(metadata.BaseRevision == 35 && host.Registry.WorldRevision == 38 && host.Backlog.Count == 3, "consistent A/B/C baseline with concurrent A update B despawn D spawn");
        host.Handle(new WorldMessage { Type = WorldMessageType.SnapshotAck, SessionId = 123, SceneEpoch = 1, SnapshotId = metadata.SnapshotId, Revision = 35 }, 1);
        check(host.State == WorldSyncState.SynchronizingWorld && host.Rejected == 1, "ack before all chunks sent rejected");
        foreach (var message in toClient) client.Handle(message, 1); toClient.Clear(); host.Tick(2, true);
        foreach (var message in toClient) client.Handle(message, 2); toClient.Clear();
        check(client.State == WorldSyncState.ApplyingBaseline && client.Registry.Count == 0 && toHost.Count == 0, "network parsing stages immutable snapshot without adapter mutation or premature ack");
        client.Tick(2, true);
        check(client.State == WorldSyncState.CatchingUp && client.Registry.WorldRevision == 35 && toHost.Count == 1, "ack emitted only after successful main-thread baseline application");
        var ack = toHost.Single(); var staleAck = new WorldMessage { Type = ack.Type, SessionId = 123, SceneEpoch = 1, SnapshotId = ack.SnapshotId + 1, Revision = ack.Revision };
        host.Handle(staleAck, 2); check(host.State == WorldSyncState.SynchronizingWorld, "stale/wrong SnapshotAck identity cannot mark peer world ready");
        host.Handle(ack, 2);
        check(host.State == WorldSyncState.CatchingUp && toClient.Count == 0, "host acknowledgement stages bounded delta drain on next poll");
        host.Tick(2, true);
        check(host.State == WorldSyncState.WorldReady && toClient.Count == 4 && toClient.Last().Type == WorldMessageType.WorldReady, "host drains three deltas then sends world ready after matching ack");
        foreach (var message in toClient) client.Handle(message, 3);
        check(WorldSyncFixture.ValidateClient(client) && WorldSyncFixture.ValidateClient(host), "baseline + concurrent changes final A11 B tombstoned C30 D40 exactly matches host");
        check(host.Registry.DevelopmentSummary() == client.Registry.DevelopmentSummary(), "development summary hashes sorted canonical records and semantic state");
        check(Throws(() => host.Registry.RegisterStatic(Descriptor(), new TestEntityAdapter(), Ownership)), "static bootstrap sealed after baseline to prevent untracked registry additions");
        var rewind = SnapshotCodec.Chunk(new InitialSnapshot(123, 1, 999, 1, new[] { Record() }))[0];
        client.Handle(new WorldMessage { Type = WorldMessageType.SnapshotBegin, SessionId = 123, SceneEpoch = 1, Metadata = rewind.Metadata }, 3);
        check(client.Registry.WorldRevision == 38 && client.State == WorldSyncState.WorldReady, "new snapshot identity cannot rewind an already newer world revision");
        var before = client.Registry.WorldRevision; client.Handle(new WorldMessage { Type = WorldMessageType.EntityChange, SessionId = 999, SceneEpoch = 1, Change = Change(EntityOperation.State, 39) }, 3);
        check(client.Registry.WorldRevision == before && client.Rejected > 0, "old session cannot mutate current world after reconnect");
        client.Handle(new WorldMessage { Type = WorldMessageType.EntityChange, SessionId = 123, SceneEpoch = 2, Change = Change(EntityOperation.State, 39) }, 3);
        check(client.Registry.WorldRevision == before, "world envelope epoch mismatch rejected");
        client.Handle(new WorldMessage { Type = WorldMessageType.WorldReady, SessionId = 123, SceneEpoch = 1, SnapshotId = ack.SnapshotId, Revision = 999 }, 3);
        check(client.State == WorldSyncState.WorldReady && client.Registry.WorldRevision == before, "invalid readiness revision cannot rewind already ready client");
        client.InvalidateScene(); check(client.Registry.Count == 0 && client.Registry.SceneEpoch == 2 && client.State == WorldSyncState.Disconnected, "scene boundary cancels snapshot/backlog and releases test bindings");
        host.InvalidateScene(); check(host.Registry.Count == 0 && host.State == WorldSyncState.Disconnected, "host scene boundary registry/tombstone cleanup");
        var early = new WorldReplication(_ => { }); early.BeginConnection(false, 123, 0); early.Handle(new WorldMessage { Type = WorldMessageType.EntityChange, SessionId = 123, SceneEpoch = 1, Change = Change(EntityOperation.Spawn, 1) }, 1);
        check(early.Registry.Count == 0 && early.State != WorldSyncState.WorldReady, "delta before baseline cannot create arbitrary dynamic entity");
        var epochAdoption = new WorldReplication(_ => { }); epochAdoption.InvalidateScene(); epochAdoption.InvalidateScene(); epochAdoption.BeginConnection(false, 123, 0);
        var epochBaseline = SnapshotCodec.Chunk(new InitialSnapshot(123, 1, 1, 0, Array.Empty<EntityStateRecord>()))[0];
        epochAdoption.Handle(new WorldMessage { Type = WorldMessageType.SnapshotBegin, SessionId = 123, SceneEpoch = 1, Metadata = epochBaseline.Metadata }, 1);
        check(epochAdoption.State == WorldSyncState.SynchronizingWorld && epochAdoption.Registry.SceneEpoch == 1, "new client session adopts lower host epoch regardless of its own scene callback history");
        var overflowMessages = new List<WorldMessage>(); var overflow = new WorldReplication(m => overflowMessages.Add(m)); overflow.ConfigureRole(true);
        var adapter = new TestEntityAdapter(); var entity = overflow.Registry.Spawn(adapter, Ownership); overflow.BeginConnection(true, 555, 0); overflow.Tick(1, true); ulong oldSnapshot = overflow.SnapshotId;
        for (int i = 0; i <= 1024; i++) { adapter.Value = i; overflow.Registry.CommitState(entity); }
        check(overflow.Backlog.Overflowed, "live synchronization notices bounded backlog overflow");
        overflow.Tick(2, true); check(overflow.SnapshotId > oldSnapshot && overflow.ResnapshotCount == 1 && !overflow.Backlog.Overflowed && overflowMessages.Count(m => m.Type == WorldMessageType.SnapshotBegin) == 2, "backlog overflow automatically recaptures current baseline instead of losing changes");
        var invalid = new WorldReplication(_ => { }); invalid.BeginConnection(false, 1, 0);
        var chunks = SnapshotCodec.Chunk(new InitialSnapshot(1, 1, 1, 1, new[] { Record() }));
        invalid.Handle(new WorldMessage { Type = WorldMessageType.SnapshotBegin, SessionId = 1, SceneEpoch = 1, Metadata = chunks[0].Metadata }, 1);
        invalid.Handle(new WorldMessage { Type = WorldMessageType.SnapshotChunk, SessionId = 1, SceneEpoch = 1, Chunk = chunks[0] }, 1); invalid.Tick(1, true);
        check(invalid.State != WorldSyncState.WorldReady && invalid.Registry.Count == 0, "unsupported adapter baseline cannot be acknowledged or become ready");
    }
    private static int FreePort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint).Port; }
    private static void Transport()
    {
        foreach (bool impaired in new[] { false, true })
        {
            using var host = new NetworkManager("build"); using var client = new NetworkManager("build"); var fixture = new WorldSyncFixture(host.World); fixture.InitializeHost(); client.World.TestEntitiesEnabled = true;
            host.SetLocalScene(true, "MainScene"); client.SetLocalScene(true, "MainScene"); host.Host(FreePort(), "Host");
            using var link = impaired ? new UdpConditionLink(0, host.Port, 60, 180, 10, 20) : null;
            client.Join("127.0.0.1", impaired ? link.Port : host.Port, "Client");
            var timer = Stopwatch.StartNew(); while (timer.Elapsed.TotalSeconds < 12 && (host.World.State != WorldSyncState.WorldReady || client.World.State != WorldSyncState.WorldReady)) { host.Poll(); client.Poll(); Thread.Sleep(5); }
            check(WorldSyncFixture.ValidateClient(host.World) && WorldSyncFixture.ValidateClient(client.World), "actual UDP world snapshot+ack+concurrent backlog " + (impaired ? "60 ms latency / 180 ms jitter / 10% loss / 20% duplication" : "clean loopback"));
            if (impaired) check(link.Dropped > 0 && link.Duplicated > 0 && link.QueueOverflow == 0 && link.Error.Length == 0, "real world chunks cross lossy duplicated reordered datagrams without overflow");
            var hostStates = host.World.Registry.Capture(); var clientStates = client.World.Registry.Capture();
            check(hostStates.Zip(clientStates, (a, b) => a.Entity.Equals(b.Entity) && a.Revision == b.Revision && a.Tombstone == b.Tombstone && a.Payload.SequenceEqual(b.Payload)).All(equal => equal), "canonical complete host/client registry state equality " + impaired);
            client.Disconnect(); timer.Restart(); while (timer.Elapsed.TotalSeconds < 6 && host.Session.State != ThiefSim2Multiplayer.Core.SessionState.Hosting) { host.Poll(); Thread.Sleep(5); }
            check(host.World.State == WorldSyncState.Disconnected && host.World.Registry.Count == 36 && client.World.Registry.Count == 0, "disconnect preserves host canonical world and releases client bindings " + impaired);
        }
    }
}
