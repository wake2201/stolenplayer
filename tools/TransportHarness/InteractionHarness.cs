using System;
using System.IO;
using System.Linq;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.Inventory;

namespace ThiefSim2Multiplayer.TransportHarness
{
    // Explicitly synthetic presentation stand-ins, never evidence of Unity animation or audio.
    public sealed class HarnessInteractionAdapter : IInteractionAdapter
    {
        private byte[] state;
        public EntityTypeId EntityType { get; }
        public bool IsAlive { get; private set; } = true;
        public HarnessInteractionAdapter(EntityTypeId type, byte[] initial) { EntityType = type; if (!ValidateState(initial)) throw new ArgumentException("Invalid synthetic state."); state = (byte[])initial.Clone(); }
        public byte[] CaptureState() => (byte[])state.Clone();
        public bool ValidateState(byte[] payload) { DoorState door; WindowState window; return EntityType == EntityTypeId.Door ? DoorState.TryDecode(payload, out door) : EntityType == EntityTypeId.Window && WindowState.TryDecode(payload, out window); }
        public void ApplyState(byte[] payload) { if (!ValidateState(payload)) throw new ArgumentException("Invalid state."); using (new NetworkApplyScope()) state = (byte[])payload.Clone(); }
        public bool Supports(InteractionKind kind) => kind == InteractionKind.Open || kind == InteractionKind.Close;
        public bool Permits(InteractionKind kind) => true;
        public void Execute(InteractionKind kind)
        {
            if (EntityType == EntityTypeId.Door) { DoorState d; DoorState.TryDecode(state, out d); d.Open = kind == InteractionKind.Open; state = d.Encode(); }
            else { WindowState w; WindowState.TryDecode(state, out w); w.Open = kind == InteractionKind.Open; state = w.Encode(); }
        }
        public void Release() { IsAlive = false; }
    }
    public sealed class InteractionHarness
    {
        public static void BindPresentationBaseline(NetworkManager manager, InitialSnapshot baseline)
        {
            foreach (var record in baseline.Entities.Where(e => e.Entity.Id.Scope == EntityScope.Static))
            {
                if (manager.World.Registry.Lookup(record.Entity) != null) continue;
                var descriptor = StaticEntityDescriptor.ParseCanonical(record.Descriptor);
                if (!descriptor.DeriveId().Equals(record.Entity.Id)) throw new ArgumentException("Manifest identity mismatch.");
                manager.World.Registry.RegisterStatic(descriptor, CreateAdapter(record.Type, record.Payload, record.Tombstone), record.Ownership);
            }
            manager.World.AdapterFactory = type => type == EntityTypeId.Pickup ? new ManagedLootAdapter(new LootState { Item = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0) }) : null;
        }
        internal static INetworkEntityAdapter CreateAdapter(EntityTypeId type, byte[] payload, bool tombstone = false)
        {
            if (type == EntityTypeId.Vehicle) { VehicleState state; if (tombstone) state=new VehicleState(); else if (!VehicleState.TryDecode(payload,out state)) throw new ArgumentException("Malformed captured vehicle."); return new ManagedVehicleAdapter { State=state }; }
            if (type == EntityTypeId.Pickup)
            { LootState state; if (tombstone) state = new LootState { Item = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0) }; else if (!LootState.TryDecode(payload, out state)) throw new ArgumentException("Malformed captured loot."); return new ManagedLootAdapter(state); }
            return new HarnessInteractionAdapter(type, tombstone ? (type == EntityTypeId.Door ? new DoorState().Encode() : new WindowState().Encode()) : payload);
        }
        private readonly NetworkManager network;
        private readonly string manifestPath, targetPath;
        private EntityRef target;
        private ulong startRevision, requestId;
        private bool requested, duplicated, closed, openAccepted, staleSent, staleRejected;
        private int readyCount;
        private double readyAt;
        public bool Passed { get; private set; }
        public InteractionHarness(NetworkManager manager, bool host, string manifest, string targetFile)
        {
            network = manager; manifestPath = manifest; targetPath = targetFile;
            if (host)
            {
                network.World.ConfigureRole(true);
                if (!File.Exists(manifest)) throw new ArgumentException("Synthetic host needs a captured local manifest.");
                foreach (string line in File.ReadAllLines(manifest))
                {
                    var fields = line.Split('\t'); var descriptor = StaticEntityDescriptor.ParseCanonical(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(fields[0])));
                    var type = (EntityTypeId)ushort.Parse(fields[1]);
                    network.World.Registry.RegisterStatic(descriptor, CreateAdapter(type, Convert.FromBase64String(fields[2])), new EntityOwnership(OwnershipKind.System));
                }
                network.ValidateInteractionTarget = (_, _) => InteractionResult.Accepted; // Synthetic host only, explicitly lacks Unity target validation.
            }
            else network.World.PrepareBaseline = baseline =>
            {
                BindPresentationBaseline(network, baseline);
                File.WriteAllLines(manifest, baseline.Entities.Where(e => e.Entity.Id.Scope == EntityScope.Static && !e.Tombstone).Select(e => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(e.Descriptor)) + "\t" + (ushort)e.Type + "\t" + Convert.ToBase64String(e.Payload)));
                Console.WriteLine("REAL HOST MANIFEST: records=" + baseline.Entities.Length + " epoch=" + baseline.SceneEpoch + " base=" + baseline.BaseRevision);
            };
            network.World.WorldBecameReady += () => { readyAt = network.Now; readyCount++; Console.WriteLine("INTERACTION WORLD READY: " + network.World.Registry.DevelopmentSummary()); };
            network.InteractionCompleted += (id, result, revision) => {
                if (id == 1 && result == InteractionResult.Accepted) openAccepted = true;
                if (id == 3 && result == InteractionResult.StaleRevision) staleRejected = true;
                Console.WriteLine("INTERACTION RESULT: request=" + id + " result=" + result + " revision=" + revision);
            };
        }
        public void Tick(bool host)
        {
            if (network.World.State != WorldSyncState.WorldReady || !network.Telemetry.HasPose || network.Now - readyAt < 1) return;
            if (host)
            {
                // Mutate a known captured real entity through the generic commit/delta path. Presentation test only.
                if (!requested && File.Exists(targetPath))
                {
                    target = ReadTarget(); var adapter = network.World.Registry.GetBinding(target) as HarnessInteractionAdapter;
                    if (adapter == null) throw new ArgumentException("Target not in synthetic manifest.");
                    adapter.Execute(InteractionKind.Open); network.World.Registry.CommitState(target); requested = true;
                    Console.WriteLine("SYNTHETIC HOST OPEN DELTA: entity=" + target + " revision=" + network.World.Registry.WorldRevision);
                }
                if (requested && !closed && network.Now - readyAt > 4)
                { ((HarnessInteractionAdapter)network.World.Registry.GetBinding(target)).Execute(InteractionKind.Close); network.World.Registry.CommitState(target); closed = true; Passed = true; Console.WriteLine("SYNTHETIC HOST CLOSE DELTA: " + network.World.Registry.DevelopmentSummary()); }
                return;
            }
            if (!requested && File.Exists(targetPath))
            {
                target = ReadTarget(); var entry = network.World.Registry.Lookup(target); if (entry == null) throw new ArgumentException("Target unknown.");
                startRevision = entry.State.Revision;
                // Caller must choose an actually nearby object. Actor follows observed genuine host position.
                if (network.SendInteraction(target, InteractionKind.Open)) { requested = true; requestId = 1; Console.WriteLine("REAL HOST OPEN REQUEST: entity=" + target + " expected=" + startRevision); }
            }
            if (requested && !duplicated && network.Now - readyAt > 2)
            {
                network.SendInteractionRequest(new InteractionRequest { SessionId = network.Session.SessionId, SceneEpoch = network.World.Registry.SceneEpoch, RequestId = requestId, ActorId = network.Session.LocalPlayerId, Entity = target, Kind = InteractionKind.Open, ExpectedRevision = startRevision }); duplicated = true;
            }
            if (requested && !closed && openAccepted && network.Now - readyAt > 4)
            { network.SendInteraction(target, InteractionKind.Close); closed = true; }
            if (closed && !staleSent && network.Interactions.Accepted >= 2 && network.Now - readyAt > 5)
            {
                network.SendInteractionRequest(new InteractionRequest { SessionId = network.Session.SessionId, SceneEpoch = network.World.Registry.SceneEpoch, RequestId = 3, ActorId = network.Session.LocalPlayerId, Entity = target, Kind = InteractionKind.Open, ExpectedRevision = startRevision }); staleSent = true;
            }
            if (closed && staleRejected && readyCount >= 2 && network.Interactions.Accepted >= 2 && network.World.DeltasApplied >= 2) { Passed = true; Console.WriteLine("INTERACTION HARNESS PASS: stale request rejected; fresh baseline converged; " + network.World.Registry.DevelopmentSummary()); closed = false; requested = false; targetPathConsumed = true; }
        }
        private bool targetPathConsumed;
        private EntityRef ReadTarget()
        { return new EntityRef(new NetworkEntityId(EntityScope.Static, ulong.Parse(File.ReadAllText(targetPath).Trim(), System.Globalization.NumberStyles.HexNumber)), 1); }
        public bool Completed => targetPathConsumed;
    }
}
