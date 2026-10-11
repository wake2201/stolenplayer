using System;
using System.Collections.Generic;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Core.World
{
    public interface IInteractionAdapter : INetworkEntityAdapter
    {
        bool Supports(InteractionKind kind);
        bool Permits(InteractionKind kind);
        void Execute(InteractionKind kind);
    }
    public interface IActorInteractionAdapter : IInteractionAdapter
    {
        bool CanContinue(byte actor, InteractionKind kind);
        bool Permits(byte actor, InteractionKind kind);
        void Execute(byte actor, InteractionKind kind);
    }
    public sealed class InteractionTelemetry
    {
        public long Sent, Accepted, Rejected, Duplicates, Stale, DeltasApplied, ApplyFailures;
        public int Doors, Windows, Unresolved;
    }
    public sealed class InteractionAuthority
    {
        private sealed class ActorRequests
        {
            public ulong High;
            public readonly Dictionary<ulong, InteractionResult> Recent = new Dictionary<ulong, InteractionResult>();
            public readonly Queue<ulong> Order = new Queue<ulong>();
            public double Window;
            public int Count;
        }
        private readonly Dictionary<byte, ActorRequests> actors = new Dictionary<byte, ActorRequests>();
        private ulong session;
        private uint epoch;
        public const int RecentLimit = 128;
        public readonly InteractionTelemetry Telemetry;
        public InteractionAuthority(InteractionTelemetry telemetry) { Telemetry = telemetry; }
        public InteractionResult Handle(NetworkEntityRegistry registry, InteractionRequest r, ulong sessionId, byte authenticatedActor, bool ready, double now, Func<byte, EntityRef, InteractionResult> target)
        {
            registry.AssertMainThread();
            if (!InteractionProtocol.Valid(r)) return Reject(InteractionResult.Unsupported);
            if (r.SessionId != sessionId || sessionId == 0) return Reject(InteractionResult.WrongSession);
            if (r.SceneEpoch != registry.SceneEpoch) return Reject(InteractionResult.WrongEpoch);
            if (!registry.IsHost || !ready) return Reject(InteractionResult.NotReady);
            if (r.ActorId != authenticatedActor) return Reject(InteractionResult.WrongActor);
            if (session != sessionId || epoch != registry.SceneEpoch) { actors.Clear(); session = sessionId; epoch = registry.SceneEpoch; }
            ActorRequests history;
            if (!actors.TryGetValue(r.ActorId, out history)) actors.Add(r.ActorId, history = new ActorRequests());
            if (r.RequestId <= history.High) { Telemetry.Duplicates++; return InteractionResult.Duplicate; }
            // Burn IDs before validation/execution; even failed actions cannot later execute on retry.
            history.High = r.RequestId;
            InteractionResult result;
            if (now - history.Window >= 1) { history.Window = now; history.Count = 0; }
            if (++history.Count > 20) result = InteractionResult.RateLimited;
            else
            {
                try { result = ValidateExecute(registry, r, target); }
                catch { result = InteractionResult.ExecutionFailed; }
            }
            history.Recent.Add(r.RequestId, result); history.Order.Enqueue(r.RequestId);
            if (history.Order.Count > RecentLimit) history.Recent.Remove(history.Order.Dequeue());
            if (result == InteractionResult.Accepted) Telemetry.Accepted++; else Reject(result);
            return result;
        }
        private InteractionResult Reject(InteractionResult result) { Telemetry.Rejected++; if (result == InteractionResult.StaleRevision) Telemetry.Stale++; return result; }
        private static InteractionResult ValidateExecute(NetworkEntityRegistry registry, InteractionRequest r, Func<byte, EntityRef, InteractionResult> target)
        {
            RegistryEntry entry = registry.Lookup(r.Entity);
            if (entry == null)
            {
                foreach (var s in registry.Capture()) if (s.Entity.Id.Equals(r.Entity.Id)) return InteractionResult.WrongGeneration;
                return InteractionResult.UnknownEntity;
            }
            var adapter = registry.GetBinding(r.Entity) as IInteractionAdapter;
            if (entry.State.Tombstone || adapter == null) return InteractionResult.UnknownEntity;
            if ((entry.State.Type != EntityTypeId.Door && entry.State.Type != EntityTypeId.Window && entry.State.Type != EntityTypeId.Doorbell) || !adapter.Supports(r.Kind)) return InteractionResult.Unsupported;
            // Observe any host change made since the last frame before comparing a client revision.
            if (!StableHash.Equal(entry.State.Payload, adapter.CaptureState())) registry.CommitState(r.Entity);
            var actorAdapter = adapter as IActorInteractionAdapter;
            if (r.ExpectedRevision < entry.State.Revision && !(actorAdapter?.CanContinue(r.ActorId,r.Kind) ?? false)) return InteractionResult.StaleRevision;
            if (r.ExpectedRevision > entry.State.Revision) return InteractionResult.FutureRevision;
            InteractionResult eligibility = target == null ? InteractionResult.InvalidTarget : target(r.ActorId, r.Entity);
            if (eligibility != InteractionResult.Accepted) return eligibility;
            if (actorAdapter != null ? !actorAdapter.Permits(r.ActorId,r.Kind) : !adapter.Permits(r.Kind)) return InteractionResult.NotPermitted;
            try
            {
                if(actorAdapter != null) actorAdapter.Execute(r.ActorId,r.Kind); else adapter.Execute(r.Kind);
                if (!StableHash.Equal(entry.State.Payload, adapter.CaptureState())) registry.CommitState(r.Entity);
                return InteractionResult.Accepted;
            }
            catch
            {
                // Original methods may fail after a partial mutation. Preserve the observed host outcome.
                try { if (!StableHash.Equal(entry.State.Payload, adapter.CaptureState())) registry.CommitState(r.Entity); } catch { }
                return InteractionResult.ExecutionFailed;
            }
        }
        public static bool InRange(float squaredDistance, float originalDistance, float tolerance) => Networking.Protocol.Finite(squaredDistance) && Networking.Protocol.Finite(originalDistance) && Networking.Protocol.Finite(tolerance) && squaredDistance >= 0 && originalDistance > 0 && tolerance >= 0 && squaredDistance <= (originalDistance + tolerance) * (originalDistance + tolerance);
    }
}
