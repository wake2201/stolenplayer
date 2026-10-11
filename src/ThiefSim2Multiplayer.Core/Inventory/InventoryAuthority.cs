using System;
using System.Collections.Generic;
using System.Linq;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Inventory
{
    public sealed class InventoryTransaction
    {
        public ulong Id { get; internal set; }
        public InventoryRequest Request { get; internal set; }
        public PersistentCoopPlayerId Owner { get; internal set; }
        public InventoryItemState Item { get; internal set; }
        public double Deadline { get; internal set; }
        public int MoneyDelta {get;internal set;}
    }
    public sealed partial class InventoryAuthority
    {
        private readonly NetworkEntityRegistry registry;
        private readonly Dictionary<ushort, ItemDefinitionRule> rules = new Dictionary<ushort, ItemDefinitionRule>();
        private readonly Dictionary<PersistentCoopPlayerId, InventoryState> inventories = new Dictionary<PersistentCoopPlayerId, InventoryState>();
        private readonly Dictionary<ulong, InventoryTransaction> pending = new Dictionary<ulong, InventoryTransaction>();
        private readonly Dictionary<string, InventoryTransactionResult> history = new Dictionary<string, InventoryTransactionResult>();
        private readonly Queue<string> order = new Queue<string>();
        private readonly Dictionary<string, ulong> highestRequest = new Dictionary<string, ulong>();
        private ulong nextTransaction;
        private readonly Dictionary<PersistentCoopPlayerId, double> rateWindow = new Dictionary<PersistentCoopPlayerId, double>();
        private readonly Dictionary<PersistentCoopPlayerId, int> rateCount = new Dictionary<PersistentCoopPlayerId, int>();
        public int PendingCount => pending.Count;
        public long Committed { get; private set; }
        public long Rejected { get; private set; }
        public long Duplicates { get; private set; }
        public long AdapterFailures { get; private set; }
        public event Action<InventoryState> Changed;
        public event Action<InventoryTransaction, InventoryTransactionResult> Settled;
        public InventoryAuthority(NetworkEntityRegistry world) { registry = world; }
        public void Configure(IEnumerable<ItemDefinitionRule> definitions)
        { registry.AssertMainThread(); rules.Clear(); foreach (var rule in definitions.Take(InventoryLimits.MaxDefinitions + 1)) { if (rules.Count >= InventoryLimits.MaxDefinitions || rules.ContainsKey(rule.Id)) throw new ArgumentException("Definition catalog overflow/collision."); rules.Add(rule.Id, rule); } }
        public InventoryState Get(PersistentCoopPlayerId owner) { InventoryState state; return inventories.TryGetValue(owner, out state) ? state : null; }
        public void Initialize(InventoryState state)
        {
            registry.AssertMainThread(); if (inventories.ContainsKey(state.Owner) || inventories.Count >= 2 || state.Items.Any(i => !rules.ContainsKey(i.Definition)) || state.Items.Any(i => inventories.Values.Any(v => v.Find(i.Instance) != null) || Economy!=null && (Economy.Find(0,i.Instance)!=null || Economy.Find(1,i.Instance)!=null))) throw new ArgumentException("Invalid personal seed.");
            inventories.Add(state.Owner, state);
        }
        public InventoryTransaction[] Pending => pending.Values.ToArray();
        public InventoryTransaction Find(ulong id) { InventoryTransaction t; return pending.TryGetValue(id, out t) ? t : null; }
        public bool Reserved(EntityRef entity) => pending.Values.Any(t => t.Request.Action == InventoryAction.Pickup && t.Request.Entity.Equals(entity));
        private static string Key(PersistentCoopPlayerId owner, ulong session, ulong request) => owner.Value.ToString("N") + ":" + session + ":" + request;
        private InventoryTransactionResult Result(InventoryRequest r, InventoryResultCode code, ulong tx, PersistentCoopPlayerId owner)
        { var state = Get(owner); return new InventoryTransactionResult { SessionId = r.SessionId, SceneEpoch = r.SceneEpoch, RequestId = r.RequestId, TransactionId = tx, Code = code, InventoryRevision = state == null ? 0 : state.Revision, WorldRevision = registry.WorldRevision, EconomyRevision=Economy?.Revision ?? 0, MoneyDelta=Find(tx)?.MoneyDelta ?? 0 }; }
        private void Remember(string key, InventoryTransactionResult result)
        { if (!history.ContainsKey(key)) { order.Enqueue(key); if (order.Count > InventoryLimits.History) history.Remove(order.Dequeue()); } history[key] = result; }
        public InventoryTransactionResult Begin(InventoryRequest r, ActorRef actor, uint epoch, bool ready, double now, Func<InventoryRequest, InventoryResultCode> validateTarget)
        {
            registry.AssertMainThread();
            if (!InventoryProtocol.Valid(r)) throw new ArgumentException("Malformed request must be rejected by codec.");
            InventoryResultCode code = InventoryResultCode.Reserved;
            if (r.SessionId != actor.SessionId) code = InventoryResultCode.WrongSession;
            else if (r.SceneEpoch != epoch || epoch != registry.SceneEpoch) code = InventoryResultCode.WrongEpoch;
            else if (r.ActorId != actor.NetworkPlayerId) code = InventoryResultCode.WrongActor;
            else if (!ready) code = InventoryResultCode.NotReady;
            if (code != InventoryResultCode.Reserved) { Rejected++; return Result(r, code, 0, actor.PersistentId); }
            string key = Key(actor.PersistentId, r.SessionId, r.RequestId); InventoryTransactionResult previous;
            if (history.TryGetValue(key, out previous)) { Duplicates++; return previous; }
            double window; if (!rateWindow.TryGetValue(actor.PersistentId, out window) || now - window >= 1) { rateWindow[actor.PersistentId] = now; rateCount[actor.PersistentId] = 0; }
            if (++rateCount[actor.PersistentId] > 32) { Rejected++; return Result(r, InventoryResultCode.RateLimited, 0, actor.PersistentId); }
            string actorKey = actor.PersistentId.Value.ToString("N") + ":" + r.SessionId;
            if (highestRequest.Count >= 4 && !highestRequest.ContainsKey(actorKey)) highestRequest.Clear();
            ulong highest; if (highestRequest.TryGetValue(actorKey, out highest) && r.RequestId <= highest) { Duplicates++; return Result(r, InventoryResultCode.Duplicate, 0, actor.PersistentId); }
            highestRequest[actorKey] = r.RequestId;
            InventoryState inventory = Get(actor.PersistentId); InventoryItemState item = null; int moneyDelta=0;
            if (inventory == null) code = InventoryResultCode.NotReady;
            else if (inventory.Revision != r.ExpectedInventoryRevision || inventory.Revision == ulong.MaxValue) code = InventoryResultCode.StaleRevision;
            else if (pending.Count >= InventoryLimits.MaxPending) code = InventoryResultCode.RateLimited;
            else if (pending.Values.Any(t => t.MoneyDelta > 0 && t.Request.Action == InventoryAction.Pickup)) code = InventoryResultCode.InvalidState;
            else if (r.Action >= InventoryAction.Buy) code=PrepareEconomy(r,actor.PersistentId,inventory,out item,out moneyDelta);
            else if(pending.Values.Any(t=>t.Owner.Equals(actor.PersistentId) && t.Request.Action>=InventoryAction.Buy)) code=InventoryResultCode.InvalidState;
            else if (r.Action == InventoryAction.Pickup)
            {
                var entry = registry.Lookup(r.Entity); LootState loot;
                if (entry == null || entry.State.Tombstone || Reserved(r.Entity)) code = InventoryResultCode.EntityUnavailable;
                else if (entry.State.Type != EntityTypeId.Pickup || !(registry.GetBinding(r.Entity) is ILootAdapter) || !LootState.TryDecode(entry.State.Payload, out loot)) code = InventoryResultCode.UnsupportedItem;
                else if (entry.State.Revision != r.ExpectedEntityRevision) code = InventoryResultCode.StaleRevision;
                else
                {
                    item = loot.Item; ItemDefinitionRule rule;
                    if (!loot.Available) code = InventoryResultCode.EntityUnavailable;
                    else if (loot.MoneyValue > 0) {
                        if(Economy == null || pending.Count != 0 || Economy.Revision == ulong.MaxValue || (long)Economy.Money + loot.MoneyValue > int.MaxValue) code = InventoryResultCode.InvalidState;
                        else moneyDelta = loot.MoneyValue;
                    }
                    else if (!rules.TryGetValue(item.Definition, out rule)) code = InventoryResultCode.UnsupportedItem;
                    else if (inventories.Values.Any(v => v.Find(item.Instance) != null)) code = InventoryResultCode.InvalidState;
                    else
                    {
                        var reserved = pending.Values.Where(t => t.Owner.Equals(actor.PersistentId) && t.Request.Action == InventoryAction.Pickup).ToArray();
                        float weight = inventory.Items.Sum(i => rules[i.Definition].Weight) + reserved.Sum(t => rules[t.Item.Definition].Weight);
                        if (inventory.Items.Length + reserved.Length >= InventoryLimits.MaxItems || weight + rule.Weight > inventory.Capacity + 5f + .0001f || (rule.MaxAmount > 0 && inventory.Count(item.Definition) + reserved.Count(t => t.Item.Definition == item.Definition) + 1 > rule.MaxAmount)) code = InventoryResultCode.InventoryFull;
                    }
                }
            }
            else
            {
                item = r.ItemInstance == Guid.Empty ? null : inventory.Find(new NetworkItemInstanceId(r.ItemInstance));
                if (r.Action != InventoryAction.Unequip && item == null) code = InventoryResultCode.NotOwner;
                else if (item != null && pending.Values.Any(t => t.Item.Instance.Equals(item.Instance))) code = InventoryResultCode.InvalidState;
                else if (r.Action == InventoryAction.Drop && (!rules[item.Definition].Droppable || registry.DynamicCount >= InventoryLimits.MaxDynamicDrops || registry.Count >= WorldLimits.MaxEntities)) code = InventoryResultCode.UnsupportedItem;
            }
            if (code == InventoryResultCode.Reserved && validateTarget != null) code = validateTarget(r);
            if (code != InventoryResultCode.Reserved) { Rejected++; var rejection = Result(r, code, 0, actor.PersistentId); Remember(key, rejection); return rejection; }
            if (nextTransaction == ulong.MaxValue) throw new InvalidOperationException("Transaction counter exhausted.");
            var transaction = new InventoryTransaction { Id = ++nextTransaction, Request = r, Owner = actor.PersistentId, Item = item, MoneyDelta=moneyDelta, Deadline = now + InventoryLimits.ReservationSeconds };
            pending.Add(transaction.Id, transaction); var result = Result(r, InventoryResultCode.Reserved, transaction.Id, actor.PersistentId); Remember(key, result);
            if (r.Action == InventoryAction.Equip || r.Action == InventoryAction.Unequip)
            { Update(inventory, inventory.Items, item == null ? (NetworkItemInstanceId?)null : item.Instance); return Finish(transaction, InventoryResultCode.Success); }
            if (r.Action == InventoryAction.NativeRemoval)
            { Update(inventory, inventory.Items.Where(i => !i.Instance.Equals(item.Instance)), inventory.Equipped.HasValue && inventory.Equipped.Value.Equals(item.Instance) ? null : inventory.Equipped); return Finish(transaction, InventoryResultCode.Success); }
            return result;
        }
        public InventoryTransactionResult Complete(InventoryCompletion completion, ActorRef actor, double now, Func<InventoryTransaction, ILootAdapter> createDrop = null)
        {
            registry.AssertMainThread(); InventoryTransaction transaction = Find(completion.TransactionId);
            if (transaction == null)
            { InventoryTransactionResult old; if (history.TryGetValue(Key(actor.PersistentId, completion.SessionId, completion.RequestId), out old) && old.TransactionId == completion.TransactionId) { Duplicates++; return old; } return new InventoryTransactionResult { SessionId = completion.SessionId, SceneEpoch = completion.SceneEpoch, RequestId = completion.RequestId, TransactionId = completion.TransactionId, Code = InventoryResultCode.InvalidState, WorldRevision = registry.WorldRevision, EconomyRevision=Economy?.Revision ?? 0 }; }
            var r = transaction.Request;
            if (!transaction.Owner.Equals(actor.PersistentId) || actor.NetworkPlayerId != r.ActorId || actor.SessionId != r.SessionId || completion.SessionId != r.SessionId || completion.SceneEpoch != registry.SceneEpoch || completion.SceneEpoch != r.SceneEpoch || completion.RequestId != r.RequestId) return Result(r, InventoryResultCode.WrongActor, transaction.Id, actor.PersistentId);
            if (!completion.Observed || now >= transaction.Deadline) return Finish(transaction, InventoryResultCode.Aborted);
            var inventory = Get(transaction.Owner);
            if (inventory.Revision == ulong.MaxValue) return Finish(transaction, InventoryResultCode.Aborted);
            if(r.Action>=InventoryAction.Buy) return CompleteEconomy(transaction,inventory);
            if (r.Action == InventoryAction.Pickup)
            {
                var entry = registry.Lookup(r.Entity);
                if (entry == null || entry.State.Tombstone || entry.State.Revision != r.ExpectedEntityRevision || inventories.Values.Any(v => v.Find(transaction.Item.Instance) != null)) return Finish(transaction, InventoryResultCode.Aborted);
                // All validations precede either mutation. Registry tombstone is streamed through C.
                LootState loot;
                if (!LootState.TryDecode(entry.State.Payload, out loot)) return Finish(transaction, InventoryResultCode.Aborted);
                var next = new InventoryState(inventory.Owner, inventory.Revision + 1, inventory.Capacity, loot.MoneyValue > 0 ? inventory.Items : inventory.Items.Concat(new[] { transaction.Item }), inventory.Equipped);
                EconomyState cash = null;
                if (loot.MoneyValue > 0) {
                    if (Economy == null || transaction.MoneyDelta != loot.MoneyValue || Economy.Revision == ulong.MaxValue || (long)Economy.Money + loot.MoneyValue > int.MaxValue) return Finish(transaction, InventoryResultCode.Aborted);
                    cash = new EconomyState(Economy.Revision+1, Economy.Money+loot.MoneyValue, Economy.StorageCapacity, Economy.LockerCapacity, Economy.Items(0), Economy.Items(1));
                }
                try { if (!registry.Despawn(r.Entity)) return Finish(transaction, InventoryResultCode.Aborted); }
                catch
                {
                    AdapterFailures++;
                    // A notification may fail after the canonical tombstone already committed.
                    // Settle ownership against the actual registry, never replay the transfer.
                    if (registry.Lookup(r.Entity)?.State.Tombstone != true) return Finish(transaction, InventoryResultCode.Aborted);
                }
                if(cash != null) Economy = cash;
                inventories[inventory.Owner] = next; Publish(next);
                if(cash != null) PublishEconomy();
            }
            else if (r.Action == InventoryAction.Drop)
            {
                if (inventory.Find(transaction.Item.Instance) == null || createDrop == null) return Finish(transaction, InventoryResultCode.Aborted);
                var next = new InventoryState(inventory.Owner, inventory.Revision + 1, inventory.Capacity, inventory.Items.Where(i => !i.Instance.Equals(transaction.Item.Instance)), inventory.Equipped.HasValue && inventory.Equipped.Value.Equals(transaction.Item.Instance) ? null : inventory.Equipped);
                ILootAdapter adapter = null;
                try
                {
                    adapter = createDrop(transaction); LootState loot;
                    if (adapter == null || !LootState.TryDecode(adapter.CaptureState(), out loot) || !loot.Item.Instance.Equals(transaction.Item.Instance) || loot.Item.Definition != transaction.Item.Definition) throw new ArgumentException("Drop adapter mismatch.");
                    registry.Spawn(adapter, new EntityOwnership(OwnershipKind.System));
                }
                catch
                {
                    AdapterFailures++;
                    if (adapter == null || !registry.FindBinding(adapter).HasValue)
                    {
                        // A created but unregistered native drop must not survive an abort.
                        try { (adapter as IDespawnPresentation)?.ApplyDespawn(); } catch { AdapterFailures++; }
                        try { adapter?.Release(); } catch { AdapterFailures++; }
                        return Finish(transaction, InventoryResultCode.Aborted);
                    }
                    // Spawn notification failed after registration: ownership must still leave inventory.
                }
                inventories[inventory.Owner] = next; Publish(next);
            }
            else return Finish(transaction, InventoryResultCode.Aborted);
            return Finish(transaction, InventoryResultCode.Success);
        }
        private void Update(InventoryState old, IEnumerable<InventoryItemState> items, NetworkItemInstanceId? equipped)
        { var next = new InventoryState(old.Owner, old.Revision + 1, old.Capacity, items, equipped); inventories[old.Owner] = next; Publish(next); }
        private void Publish(InventoryState state)
        {
            if (Changed == null) return;
            foreach (Action<InventoryState> subscriber in Changed.GetInvocationList())
                try { subscriber(state); } catch { AdapterFailures++; }
        }
        private InventoryTransactionResult Finish(InventoryTransaction t, InventoryResultCode code)
        {
            pending.Remove(t.Id); if (code == InventoryResultCode.Success) Committed++; else Rejected++;
            var result = Result(t.Request, code, t.Id, t.Owner); result.MoneyDelta=t.MoneyDelta; Remember(Key(t.Owner, t.Request.SessionId, t.Request.RequestId), result);
            if (Settled != null) foreach (Action<InventoryTransaction, InventoryTransactionResult> subscriber in Settled.GetInvocationList())
                try { subscriber(t, result); } catch { AdapterFailures++; }
            return result;
        }
        public void Tick(double now) { registry.AssertMainThread(); foreach (var t in pending.Values.Where(t => now >= t.Deadline).ToArray()) Finish(t, InventoryResultCode.Aborted); }
        public void Cancel(PersistentCoopPlayerId? owner = null)
        { registry.AssertMainThread(); foreach (var t in pending.Values.Where(t => !owner.HasValue || t.Owner.Equals(owner.Value)).ToArray()) Finish(t, InventoryResultCode.Aborted); }
        public int InvariantViolations()
        {
            registry.AssertMainThread(); var owned = inventories.Values.SelectMany(v => v.Items).Select(i => i.Instance).ToArray(); int violations = owned.Length - owned.Distinct().Count();
            if(Economy!=null) {var stored=Economy.Items(0).Concat(Economy.Items(1)).Select(i=>i.Instance).ToArray(); violations+=stored.Length-stored.Distinct().Count()+stored.Count(id=>owned.Contains(id)); owned=owned.Concat(stored).ToArray();}
            var world = new List<NetworkItemInstanceId>();
            foreach (var record in registry.Capture()) { LootState loot; if (!record.Tombstone && record.Type == EntityTypeId.Pickup && LootState.TryDecode(record.Payload, out loot)) world.Add(loot.Item.Instance); }
            return violations + world.Count - world.Distinct().Count() + world.Count(id => owned.Contains(id));
        }
    }
}
