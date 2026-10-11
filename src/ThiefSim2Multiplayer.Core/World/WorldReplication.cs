using System;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Core.World
{
    public enum WorldSyncState { Disconnected, Connected, SynchronizingWorld, ApplyingBaseline, CatchingUp, WorldReady, Failed }
    // Two-player host stream. Poll/application and all registry bindings share the creating thread.
    public sealed class WorldReplication
    {
        private readonly Action<WorldMessage> send;
        private SnapshotChunk[] outgoing;
        private int nextChunk;
        private SnapshotAssembler assembler;
        private InitialSnapshot pending;
        private ulong session, nextSnapshot = 1, appliedSnapshot, latestSnapshot;
        private bool host;
        private double started, now;
        private double lastResyncRequest = -100;
        private double nextBindingSweep;
        private int attempts;
        private int recoveryRequests;
        public NetworkEntityRegistry Registry { get; private set; } = new NetworkEntityRegistry(false, 1);
        public DeltaBacklog Backlog { get; } = new DeltaBacklog();
        public WorldSyncState State { get; private set; } = WorldSyncState.Disconnected;
        public ulong SnapshotId { get; private set; }
        public int SnapshotChunks { get; private set; }
        public int SnapshotProgress { get; private set; }
        public int SnapshotBytes { get; private set; }
        public int ResnapshotCount { get; private set; }
        public int Rejected { get; private set; }
        public string LastError { get; private set; } = "";
        public bool TestEntitiesEnabled { get; set; }
        public Func<EntityTypeId, INetworkEntityAdapter> AdapterFactory { get; set; }
        public Func<bool> CanSend { get; set; } = () => true;
        public event Action SnapshotCaptured;
        public event Action WorldBecameReady;
        public Action PrepareRegistry { get; set; }
        public Action<InitialSnapshot> PrepareBaseline { get; set; }
        public Diagnostics.FrameStabilityProfiler FrameProfile {get;set;}
        public long DeltasApplied { get; private set; }
        public long AdapterApplyFailures { get; private set; }
        public WorldReplication(Action<WorldMessage> sender) { send = sender; Registry.Changed += Changed; }
        public void ConfigureRole(bool isHost)
        { Registry.AssertMainThread(); if (Registry.IsHost != isHost) Registry.Reset(isHost, Registry.SceneEpoch); host = isHost; }
        public void BeginConnection(bool isHost, ulong sessionId, double time)
        {
            Registry.AssertMainThread(); Cancel(); ConfigureRole(isHost); session = sessionId; now = time; State = WorldSyncState.Connected; attempts = 0; latestSnapshot = 0; LastError = "";
            if (!host) Registry.Reset(false, 1); // A new session adopts the host epoch; local scene callback counts are not authority.
        }
        public void Cancel()
        {
            Registry.AssertMainThread(); outgoing = null; assembler = null; pending = null; session = 0; appliedSnapshot = 0; State = WorldSyncState.Disconnected;
            SnapshotId = 0; SnapshotChunks = SnapshotProgress = SnapshotBytes = 0; Backlog.Begin(0);
            recoveryRequests = 0;
            if (!host) Registry.Reset(false, Registry.SceneEpoch);
        }
        public void InvalidateScene()
        {
            Cancel(); if (Registry.SceneEpoch == uint.MaxValue) throw new InvalidOperationException("Scene epoch exhausted.");
            Registry.Reset(host, Registry.SceneEpoch + 1);
        }
        // Preserve the existing stream/session, release Unity bindings and restart Milestone C.
        public void TransitionScene(uint epoch)
        {
            Registry.AssertMainThread(); if (epoch == 0 || epoch < Registry.SceneEpoch) throw new ArgumentException("Invalid authoritative epoch.");
            ulong token = session; Cancel(); Registry.Reset(host, epoch); session = token;
            attempts = 0; latestSnapshot = 0; LastError = ""; State = token == 0 ? WorldSyncState.Disconnected : WorldSyncState.Connected;
        }
        private WorldMessage Envelope(WorldMessageType type)
        { return new WorldMessage { Type = type, SessionId = session, SceneEpoch = Registry.SceneEpoch }; }
        private INetworkEntityAdapter Factory(EntityTypeId type)
        { return AdapterFactory != null ? AdapterFactory(type) : TestEntitiesEnabled && type == EntityTypeId.TestEntity ? new TestEntityAdapter() : null; }
        private void BeginSnapshot()
        {
            if (++attempts > 3) { Fail("Repeated snapshot restart limit."); return; }
            if (nextSnapshot == ulong.MaxValue) { Fail("Snapshot ID exhausted."); return; }
            if (SnapshotId != 0) ResnapshotCount++;
            ulong revision = Registry.WorldRevision;
            Registry.SealStaticRegistration();
            EntityStateRecord[] records;
            using(FrameProfile?.Measure(Diagnostics.RuntimePhase.BaselineCapture) ?? default(Diagnostics.FrameStabilityProfiler.Scope)) records=Registry.Capture();
            using(FrameProfile?.Measure(Diagnostics.RuntimePhase.BaselineSerialization) ?? default(Diagnostics.FrameStabilityProfiler.Scope)) outgoing = SnapshotCodec.Chunk(new InitialSnapshot(session, Registry.SceneEpoch, nextSnapshot++, revision, records));
            SnapshotId = outgoing[0].Metadata.SnapshotId; SnapshotChunks = outgoing.Length; SnapshotBytes = outgoing[0].Metadata.TotalBytes; SnapshotProgress = nextChunk = 0;
            Backlog.Begin(revision); State = WorldSyncState.SynchronizingWorld; started = now;
            var message = Envelope(WorldMessageType.SnapshotBegin); message.Metadata = outgoing[0].Metadata; send(message);
            SnapshotCaptured?.Invoke(); // Development fixture mutates A/B/D after the immutable capture.
        }
        public void Tick(double time, bool scenesCompatible)
        {
            Registry.AssertMainThread(); now = time;
            if (now >= nextBindingSweep) { Registry.SweepDestroyedBindings(); nextBindingSweep = now + 1; }
            if (session == 0 || State == WorldSyncState.Failed) return;
            if (!scenesCompatible)
            {
                if (State != WorldSyncState.Connected) { outgoing = null; assembler = null; pending = null; appliedSnapshot = 0; State = WorldSyncState.Connected; Backlog.Begin(Registry.WorldRevision); }
                return;
            }
            using(FrameProfile?.Measure(Diagnostics.RuntimePhase.RegistryRebuild) ?? default(Diagnostics.FrameStabilityProfiler.Scope)) PrepareRegistry?.Invoke();
            if (host && (State == WorldSyncState.Connected || Backlog.Overflowed)) BeginSnapshot();
            if (State == WorldSyncState.Failed) return;
            if (host && outgoing != null)
            {
                using var transfer=FrameProfile?.Measure(Diagnostics.RuntimePhase.BaselineTransfer) ?? default(Diagnostics.FrameStabilityProfiler.Scope);
                // Bounded work/send burst per poll; allow the host game to continue between chunks.
                int previousChunk = nextChunk;
                for (int budget = 0; budget < 4 && nextChunk < outgoing.Length && CanSend(); budget++)
                { var message = Envelope(WorldMessageType.SnapshotChunk); message.Chunk = outgoing[nextChunk++]; send(message); SnapshotProgress = nextChunk; }
                if (nextChunk != previousChunk) started = now;
            }
            if (host && (State == WorldSyncState.WorldReady || State == WorldSyncState.CatchingUp))
            {
                for (int budget = 0; budget < 4 && Backlog.Count > 0 && CanSend(); budget++)
                    foreach (var change in Backlog.Drain(1)) SendChange(change);
                if (State == WorldSyncState.CatchingUp && Backlog.Count == 0 && CanSend())
                {
                    var ready = Envelope(WorldMessageType.WorldReady); ready.SnapshotId = SnapshotId; ready.Revision = Registry.WorldRevision; send(ready);
                    State = WorldSyncState.WorldReady; attempts = recoveryRequests = 0; LastError = ""; WorldBecameReady?.Invoke();
                }
            }
            if (!host && pending != null)
            {
                using var apply=FrameProfile?.Measure(Diagnostics.RuntimePhase.BaselineApply) ?? default(Diagnostics.FrameStabilityProfiler.Scope);
                var baseline = pending; pending = null;
                try
                {
                    PrepareBaseline?.Invoke(baseline);
                    Registry.ProfileBaseline = FrameProfile?.Enabled == true;
                    if (!Registry.ApplyBaseline(baseline.Entities, baseline.BaseRevision, Factory)) { RequestResync("Baseline adapter/manifest validation failed: " + Registry.LastBaselineError); return; }
                    appliedSnapshot = baseline.SnapshotId; State = WorldSyncState.CatchingUp;
                    var ack = Envelope(WorldMessageType.SnapshotAck); ack.SnapshotId = baseline.SnapshotId; ack.Revision = baseline.BaseRevision; send(ack);
                }
                catch (Exception error) { AdapterApplyFailures++; Registry.Reset(false, Registry.SceneEpoch); RequestResync("Adapter baseline application failed: " + error.GetType().Name); }
            }
            if (State != WorldSyncState.WorldReady && State != WorldSyncState.Connected && now - started >= WorldLimits.SyncSeconds)
            { if (host) BeginSnapshot(); else RequestResync("World synchronization timed out."); }
            else if (!host && assembler != null && assembler.Expired(now)) RequestResync("Snapshot assembly timed out.");
        }
        public void Handle(WorldMessage message, double time)
        {
            Registry.AssertMainThread(); now = time;
            if (message == null || session == 0 || message.SessionId != session || State == WorldSyncState.Failed) { Rejected++; return; }
            if (host)
            {
                if (message.SceneEpoch != Registry.SceneEpoch) { Rejected++; return; }
                if (message.Type == WorldMessageType.SnapshotAck)
                {
                    if (outgoing == null || Backlog.Overflowed || nextChunk != outgoing.Length || message.SnapshotId != SnapshotId || message.Revision != outgoing[0].Metadata.BaseRevision || State != WorldSyncState.SynchronizingWorld) { Rejected++; return; }
                    outgoing = null;
                    State = WorldSyncState.CatchingUp; started = now;
                }
                else if (message.Type == WorldMessageType.ResyncRequest && now - lastResyncRequest >= 0.25 && CanSend())
                { lastResyncRequest = now; BeginSnapshot(); }
                else Rejected++;
                return;
            }
            if (message.Type == WorldMessageType.SnapshotBegin)
            {
                if (message.Metadata.SnapshotId <= latestSnapshot || message.SceneEpoch < Registry.SceneEpoch ||
                    (message.SceneEpoch == Registry.SceneEpoch && message.Metadata.BaseRevision < Registry.WorldRevision)) { Rejected++; return; }
                latestSnapshot = message.Metadata.SnapshotId; if (++attempts > 3) { Fail("Repeated inbound snapshot limit."); return; }
                if (message.SceneEpoch != Registry.SceneEpoch) Registry.Reset(false, message.SceneEpoch);
                assembler = new SnapshotAssembler(message.Metadata, now); pending = null; appliedSnapshot = 0;
                SnapshotId = message.Metadata.SnapshotId; SnapshotChunks = message.Metadata.ChunkCount; SnapshotBytes = message.Metadata.TotalBytes; SnapshotProgress = 0;
                started = now; State = WorldSyncState.SynchronizingWorld; return;
            }
            if (message.SceneEpoch != Registry.SceneEpoch) { Rejected++; return; }
            if (message.Type == WorldMessageType.SnapshotChunk)
            {
                if (assembler == null || State != WorldSyncState.SynchronizingWorld) { Rejected++; return; }
                var result = assembler.Add(message.Chunk, now); SnapshotProgress = assembler.ReceivedChunks;
                if (result == AssemblyResult.Complete)
                {
                    if (!assembler.TryFinish(now, out pending)) { RequestResync("Snapshot payload rejected."); return; }
                    assembler = null; State = WorldSyncState.ApplyingBaseline;
                }
                else if (result == AssemblyResult.Conflict || result == AssemblyResult.InvalidHash || result == AssemblyResult.Expired) RequestResync("Snapshot chunk integrity failed.");
                else if (result == AssemblyResult.Rejected) Rejected++;
            }
            else if (message.Type == WorldMessageType.EntityChange)
            {
                if (State != WorldSyncState.CatchingUp && State != WorldSyncState.WorldReady) { Rejected++; RequestResync("Delta before applied baseline."); return; }
                try
                {
                    var result = Registry.ApplyChange(message.SceneEpoch, message.Change, Factory);
                    if (result == ApplyResult.Applied) DeltasApplied++;
                    if (result != ApplyResult.Applied && result != ApplyResult.Stale) { Rejected++; RequestResync("Entity stream rejected: " + result); }
                }
                catch (Exception error) { AdapterApplyFailures++; Registry.Reset(false, Registry.SceneEpoch); RequestResync("Adapter delta application failed: " + error.GetType().Name); }
            }
            else if (message.Type == WorldMessageType.WorldReady)
            {
                if (State != WorldSyncState.CatchingUp || message.SnapshotId != appliedSnapshot || message.Revision != Registry.WorldRevision) { Rejected++; return; }
                State = WorldSyncState.WorldReady; attempts = recoveryRequests = 0; LastError = ""; WorldBecameReady?.Invoke();
            }
            else Rejected++;
        }
        private void Changed(EntityChange change)
        {
            if (session == 0 || !host) return;
            if (State == WorldSyncState.SynchronizingWorld || State == WorldSyncState.CatchingUp) Backlog.Add(change);
            else if (State == WorldSyncState.WorldReady) Backlog.Add(change);
        }
        public void Resnapshot()
        {
            Registry.AssertMainThread(); if (session == 0 || State == WorldSyncState.Failed) return;
            if (host) { State = WorldSyncState.Connected; outgoing = null; Backlog.Begin(Registry.WorldRevision); }
            else RequestResync("Development/application resync requested.");
        }
        private void SendChange(EntityChange change) { var message = Envelope(WorldMessageType.EntityChange); message.Change = change; send(message); }
        private void RequestResync(string reason)
        {
            LastError = reason; assembler = null; pending = null; appliedSnapshot = 0;
            if (State == WorldSyncState.Connected && now - lastResyncRequest < 0.25) return;
            if (++recoveryRequests > 3) { Fail("Repeated application/resync recovery limit: " + reason); return; }
            State = WorldSyncState.Connected;
            lastResyncRequest = now;
            var request = Envelope(WorldMessageType.ResyncRequest); request.SnapshotId = SnapshotId; request.Revision = Registry.WorldRevision; send(request);
        }
        private void Fail(string reason) { LastError = reason; outgoing = null; assembler = null; pending = null; Backlog.Begin(0); State = WorldSyncState.Failed; }
    }
    public sealed class WorldSyncFixture
    {
        private readonly WorldReplication world;
        private TestEntityAdapter a;
        private EntityRef aRef, bRef, cRef, dRef;
        private bool changed;
        public WorldSyncFixture(WorldReplication replication) { world = replication; world.TestEntitiesEnabled = true; world.SnapshotCaptured += ConcurrentChanges; }
        public void InitializeHost()
        {
            world.ConfigureRole(true);
            if (world.Registry.Count != 0) return; // Reconnect must preserve canonical state/revision in the same host epoch.
            changed = false;
            a = new TestEntityAdapter { Value = 10 }; aRef = world.Registry.Spawn(a, new EntityOwnership(OwnershipKind.Session));
            bRef = world.Registry.Spawn(new TestEntityAdapter { Value = 20 }, new EntityOwnership(OwnershipKind.Session));
            cRef = world.Registry.Spawn(new TestEntityAdapter { Value = 30 }, new EntityOwnership(OwnershipKind.Session));
            // Extra synthetic entities force multiple chunks through the real reliable transport.
            for (int i = 0; i < 32; i++) world.Registry.Spawn(new TestEntityAdapter { Value = 100 + i }, new EntityOwnership(OwnershipKind.System));
        }
        private void ConcurrentChanges()
        {
            if (changed || a == null) return; changed = true;
            a.Value = 11; world.Registry.CommitState(aRef); world.Registry.Despawn(bRef);
            dRef = world.Registry.Spawn(new TestEntityAdapter { Value = 40 }, new EntityOwnership(OwnershipKind.Session));
        }
        public static bool ValidateClient(WorldReplication world)
        {
            if (world.State != WorldSyncState.WorldReady) return false;
            var entries = world.Registry.Capture();
            return entries.Length == 36 && world.Registry.TombstoneCount == 1 && world.Registry.WorldRevision == 38 &&
                Array.Exists(entries, e => !e.Tombstone && BitConverter.ToInt32(e.Payload, 2) == 11) &&
                Array.Exists(entries, e => e.Tombstone) &&
                Array.Exists(entries, e => !e.Tombstone && BitConverter.ToInt32(e.Payload, 2) == 30) &&
                Array.Exists(entries, e => !e.Tombstone && BitConverter.ToInt32(e.Payload, 2) == 40);
        }
    }
}
