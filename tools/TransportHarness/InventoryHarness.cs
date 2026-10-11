using System;
using System.IO;
using System.Linq;
using System.Globalization;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.TransportHarness
{
    public sealed class InventoryHarness
    {
        public static readonly PersistentCoopPlayerId FixtureIdentity = new PersistentCoopPlayerId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        private readonly NetworkManager network;
        private readonly string targetFile;
        private InventoryRequest pickup, drop;
        private ulong transaction;
        private double approvedAt;
        private bool completionSent, pickupSuccess, dropSuccess, duplicatesSent, reported;
        private Guid acquired;
        private int initialCount = -1;
        public bool Passed { get; private set; }
        public InventoryHarness(NetworkManager manager, bool host, string manifest, string target, string catalog)
        {
            network = manager; targetFile = target;
            network.Inventories.LocalIdentity = FixtureIdentity;
            if (host)
            {
                network.World.ConfigureRole(true);
                var rules = File.ReadAllLines(catalog).Select(line => { var f = line.Split('\t'); return new ItemDefinitionRule(ushort.Parse(f[0]), float.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2]), bool.Parse(f[3])); }).ToArray();
                network.Inventories.Authority.Configure(rules);
                network.Inventories.Authority.Initialize(new InventoryState(FixtureIdentity, 1, 15, new InventoryItemState[0]));
                network.Inventories.CreateRemoteSeed = owner => new InventoryState(owner, 1, 15, new[] { new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0), new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 3) });
                foreach (string line in File.ReadAllLines(manifest))
                {
                    var f = line.Split('\t'); var descriptor = StaticEntityDescriptor.ParseCanonical(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(f[0]))); var type = (EntityTypeId)ushort.Parse(f[1]); bool dead = f.Length > 3 && bool.Parse(f[3]);
                    var entity = network.World.Registry.RegisterStatic(descriptor, InteractionHarness.CreateAdapter(type, Convert.FromBase64String(f[2]), dead), new EntityOwnership(OwnershipKind.System)); if (dead) network.World.Registry.Despawn(entity);
                }
                network.Inventories.ValidateTarget = request =>
                {
                    if (!network.Telemetry.HasPose || network.Telemetry.ReceiveAge(network.Now) > 1) return InventoryResultCode.InvalidState;
                    var pose = network.Telemetry.Latest;
                    float x = request.X, y = request.Y, z = request.Z;
                    if (request.Action == InventoryAction.Pickup) { LootState state; var entry = network.World.Registry.Lookup(request.Entity); if (entry == null || !LootState.TryDecode(entry.State.Payload, out state)) return InventoryResultCode.EntityUnavailable; x = state.X; y = state.Y; z = state.Z; }
                    else if (request.Action != InventoryAction.Drop) return InventoryResultCode.Reserved;
                    return (x - pose.X) * (x - pose.X) + (y - pose.Y) * (y - pose.Y) + (z - pose.Z) * (z - pose.Z) <= 7.5625f ? InventoryResultCode.Reserved : InventoryResultCode.OutOfRange;
                };
                network.Inventories.CreateDrop = t => new ManagedLootAdapter(new LootState { Item = t.Item, X = t.Request.X, Y = t.Request.Y, Z = t.Request.Z });
                network.Inventories.Authority.Settled += (t, r) => { Console.WriteLine("SYNTHETIC HOST INVENTORY: action=" + t.Request.Action + " result=" + r.Code + " owner=" + t.Owner.Value + " state=" + network.Inventories.Authority.Get(t.Owner).Hash + " world=" + network.World.Registry.DevelopmentSummary()); if (r.Code == InventoryResultCode.Success && t.Request.Action == InventoryAction.Pickup) Passed = true; };
            }
            else
            {
                network.World.PrepareBaseline = baseline =>
                {
                    InteractionHarness.BindPresentationBaseline(network, baseline);
                    File.WriteAllLines(manifest, baseline.Entities.Where(e => e.Entity.Id.Scope == EntityScope.Static).Select(e => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(e.Descriptor)) + "\t" + (ushort)e.Type + "\t" + Convert.ToBase64String(e.Payload) + "\t" + e.Tombstone));
                    Console.WriteLine("REAL HOST LOOT BASELINE: entities=" + baseline.Entities.Length + " pickups=" + baseline.Entities.Count(e => e.Type == EntityTypeId.Pickup) + " base=" + baseline.BaseRevision);
                };
                network.Inventories.ResultReceived += result =>
                {
                    Console.WriteLine("INVENTORY RESULT: request=" + result.RequestId + " transaction=" + result.TransactionId + " code=" + result.Code + " inventoryRevision=" + result.InventoryRevision + " worldRevision=" + result.WorldRevision);
                    if (pickup != null && result.RequestId == pickup.RequestId)
                    { if (result.Code == InventoryResultCode.Reserved && transaction == 0) { transaction = result.TransactionId; approvedAt = network.Now; } if (result.Code == InventoryResultCode.Success) pickupSuccess = true; }
                    if (drop != null && result.RequestId == drop.RequestId)
                    { if (result.Code == InventoryResultCode.Reserved) network.Inventories.Complete(drop, result.TransactionId, true); if (result.Code == InventoryResultCode.Success) dropSuccess = true; }
                };
                network.Inventories.StateReceived += state => Console.WriteLine("OWNER INVENTORY: revision=" + state.Revision + " count=" + state.Items.Length + " hash=" + state.Hash);
            }
        }
        public void Tick(bool host)
        {
            if (host) return;
            var state = network.Inventories.LocalState;
            if (network.World.State != WorldSyncState.WorldReady || state == null || !network.Telemetry.HasPose) return;
            if (initialCount < 0) initialCount = state.Items.Length;
            if (pickup == null && File.Exists(targetFile))
            {
                string[] parts = File.ReadAllText(targetFile).Trim().Split(':'); var entity = new EntityRef(new NetworkEntityId((EntityScope)byte.Parse(parts[0]), ulong.Parse(parts[1], NumberStyles.HexNumber)), 1);
                var record = network.World.Registry.Lookup(entity); if (record == null || record.State.Tombstone) return;
                if (!LootState.TryDecode(record.State.Payload, out var loot)) throw new InvalidOperationException("Target isn't supported loot."); acquired = loot.Item.Instance.Value;
                pickup = network.Inventories.Request(InventoryAction.Pickup, entity); network.Inventories.SendRequest(pickup);
                Console.WriteLine("HARNESS PICKUP: entity=" + entity + " instance=" + acquired + " definition=" + loot.Item.Definition + " evidence=SYNTHETIC CLIENT, NO NATIVE UNITY INVENTORY");
            }
            if (pickup != null && !duplicatesSent)
            { for (int i = 0; i < 20; i++) network.Inventories.SendRequest(pickup); duplicatesSent = true; }
            if (transaction != 0 && !completionSent && network.Now - approvedAt >= .7)
            { network.Inventories.Complete(pickup, transaction, true); completionSent = true; }
            if (pickupSuccess && drop == null && state.Find(new NetworkItemInstanceId(acquired)) != null)
            {
                if (state.Items.Length != initialCount + 1) throw new InvalidOperationException("Duplicate pickup changed owner quantity.");
                var pose = network.Telemetry.Latest;
                drop = network.Inventories.Request(InventoryAction.Drop, instance: acquired, x: pose.X, y: pose.Y + .1f, z: pose.Z + .5f); network.Inventories.SendRequest(drop);
            }
            if (dropSuccess && state.Find(new NetworkItemInstanceId(acquired)) == null && state.Items.Length == initialCount && !reported && network.World.Registry.WorldRevision >= network.Inventories.OwnerSnapshotWorldRevision)
            { reported = Passed = true; Console.WriteLine("INVENTORY HARNESS PASS: pickup duplicate anti-dupe and drop; owner=" + state.Hash + " world=" + network.World.Registry.DevelopmentSummary()); }
        }
    }
}
