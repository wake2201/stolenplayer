using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Inventory
{
    public sealed class EconomyState
    {
        private readonly InventoryItemState[][] stored;
        public ulong Revision {get;}
        public int Money {get;}
        public float StorageCapacity {get;}
        public float LockerCapacity {get;}
        public EconomyState(ulong revision,int money,float storageCapacity,float lockerCapacity,IEnumerable<InventoryItemState> storage,IEnumerable<InventoryItemState> locker)
        {
            if(storage==null || locker==null || revision==0 || money<0 || !Protocol.Finite(storageCapacity) || !Protocol.Finite(lockerCapacity) || storageCapacity<0 || lockerCapacity<0 || storageCapacity>100000 || lockerCapacity>100000) throw new ArgumentException("Invalid shared economy.");
            stored=new[] {storage.Take(InventoryLimits.MaxItems+1).ToArray(),locker.Take(InventoryLimits.MaxItems+1).ToArray()};
            if(stored.Any(s=>s.Length>InventoryLimits.MaxItems || s.Any(i=>i==null)) || stored.SelectMany(s=>s).Select(i=>i.Instance).Distinct().Count()!=stored.Sum(s=>s.Length)) throw new ArgumentException("Duplicate/overflowing storage.");
            stored=stored.Select(s=>s.OrderBy(i=>i.Instance.ToString(),StringComparer.Ordinal).ToArray()).ToArray();
            Revision=revision; Money=money; StorageCapacity=storageCapacity; LockerCapacity=lockerCapacity;
        }
        public InventoryItemState[] Items(byte container) => (InventoryItemState[])stored[container].Clone();
        public InventoryItemState Find(byte container,NetworkItemInstanceId id) => stored[container].FirstOrDefault(i=>i.Instance.Equals(id));
        public int Count(byte container,ushort definition) => stored[container].Count(i=>i.Definition==definition);
        public float Capacity(byte container) => container==0 ? StorageCapacity : LockerCapacity;
    }
    public sealed class EconomyQuote
    {
        public InventoryResultCode Code=InventoryResultCode.Reserved;
        public int MoneyDelta;
    }
    public static class EconomyCodec
    {
        public const int MaxBytes=25+2*InventoryLimits.MaxItems*18;
        public static byte[] Encode(EconomyState state) => InventoryProtocol.Write(w=> {
            w.Write((byte)1); w.Write(state.Revision); w.Write(state.Money); w.Write(state.StorageCapacity); w.Write(state.LockerCapacity);
            for(byte container=0;container<2;container++) {var items=state.Items(container); w.Write((ushort)items.Length); foreach(var item in items) {w.Write(item.Instance.Value.ToByteArray()); w.Write(item.Definition);}}
        });
        public static bool Decode(byte[] bytes,out EconomyState state)
        {
            state=null; if(bytes==null || bytes.Length<25 || bytes.Length>MaxBytes) return false;
            try {using(var r=new BinaryReader(new MemoryStream(bytes,false))) {
                if(r.ReadByte()!=1) return false;
                ulong revision=r.ReadUInt64(); int money=r.ReadInt32(); float capacity=r.ReadSingle(),locker=r.ReadSingle();
                var items=new List<InventoryItemState>[2];
                for(int c=0;c<2;c++) {int count=r.ReadUInt16(); if(count>InventoryLimits.MaxItems) return false; items[c]=new List<InventoryItemState>(count); for(int i=0;i<count;i++) items[c].Add(new InventoryItemState(new NetworkItemInstanceId(new Guid(r.ReadBytes(16))),r.ReadUInt16()));}
                if(r.BaseStream.Position!=bytes.Length) return false;
                state=new EconomyState(revision,money,capacity,locker,items[0],items[1]); return true;
            }} catch(ArgumentException) {return false;} catch(IOException) {return false;}
        }
    }
    public sealed partial class InventoryAuthority
    {
        public EconomyState Economy {get;private set;}
        public Func<InventoryRequest,InventoryItemState,PersistentCoopPlayerId,EconomyQuote> QuoteEconomy;
        public event Action<EconomyState> EconomyChanged;
        public void InitializeEconomy(EconomyState state)
        {
            registry.AssertMainThread(); if(Economy!=null || state==null || state.Items(0).Concat(state.Items(1)).Any(i=>!rules.ContainsKey(i.Definition) || inventories.Values.Any(v=>v.Find(i.Instance)!=null))) throw new ArgumentException("Invalid shared seed.");
            Economy=state; PublishEconomy();
        }
        public void ObserveHostMoney(int money)
        {
            registry.AssertMainThread(); if(Economy==null || money<0 || money==Economy.Money || pending.Count!=0 || Economy.Revision==ulong.MaxValue) return;
            Economy=new EconomyState(Economy.Revision+1,money,Economy.StorageCapacity,Economy.LockerCapacity,Economy.Items(0),Economy.Items(1)); PublishEconomy();
        }
        public bool ObserveNativeStorageRemoval(byte container,ushort definition,int amount)
        {
            registry.AssertMainThread();
            if(!registry.IsHost || Economy==null || Economy.Revision==ulong.MaxValue || container>1 || amount<=0) return false;
            var stored=Economy.Items(container);
            var removed=new HashSet<NetworkItemInstanceId>(stored.Where(i=>i.Definition==definition).Take(amount).Select(i=>i.Instance));
            if(removed.Count==0) return false;
            var remaining=stored.Where(i=>!removed.Contains(i.Instance));
            // Mirror only native consumption, never mint units or change the wallet.
            // A withdrawal of a consumed instance will abort at completion.
            Economy=new EconomyState(Economy.Revision+1,Economy.Money,Economy.StorageCapacity,Economy.LockerCapacity,container==0 ? remaining : Economy.Items(0),container==1 ? remaining : Economy.Items(1));
            PublishEconomy(); return true;
        }
        // The native Host remains free to play while alone. Adopt its observed state
        // before a peer's next baseline; never change the absent player's records.
        public void ObserveNativeHost(InventoryState inventory,EconomyState economy)
        {
            registry.AssertMainThread(); var previous=inventory==null ? null : Get(inventory.Owner);
            if(!registry.IsHost || pending.Count!=0 || previous==null || Economy==null || economy==null || previous.Revision==ulong.MaxValue || Economy.Revision==ulong.MaxValue || inventory.Revision!=previous.Revision+1 || economy.Revision!=Economy.Revision+1) throw new InvalidOperationException("Unsafe native Host observation.");
            var items=inventory.Items.Concat(economy.Items(0)).Concat(economy.Items(1)).ToArray();
            if(items.Any(i=>!rules.ContainsKey(i.Definition)) || items.Select(i=>i.Instance).Distinct().Count()!=items.Length || items.Any(i=>inventories.Values.Any(v=>!v.Owner.Equals(inventory.Owner) && v.Find(i.Instance)!=null))) throw new ArgumentException("Native Host ownership collision.");
            inventories[inventory.Owner]=inventory; Economy=economy; Publish(inventory); PublishEconomy();
        }
        public bool StoreNativeDrop(EntityRef entity,Func<bool> nativeMove)
        {
            registry.AssertMainThread(); var entry=registry.Lookup(entity); LootState loot;
            if(!registry.IsHost || Economy==null || Economy.Revision==ulong.MaxValue || pending.Count!=0 || entry==null || entry.State.Tombstone || entry.State.Type!=EntityTypeId.Pickup || !LootState.TryDecode(entry.State.Payload,out loot) || !loot.Available) return false;
            ItemDefinitionRule rule; var stored=Economy.Items(1);
            if(!rules.TryGetValue(loot.Item.Definition,out rule) || rule.MaxAmount>0 || stored.Length>=InventoryLimits.MaxItems || inventories.Values.Any(v=>v.Find(loot.Item.Instance)!=null) || Economy.Find(0,loot.Item.Instance)!=null || Economy.Find(1,loot.Item.Instance)!=null) return false;
            // The original native save-time vacuum has no capacity check. Retain that
            // rule; move this same physical instance instead of minting another unit.
            var next=new EconomyState(Economy.Revision+1,Economy.Money,Economy.StorageCapacity,Economy.LockerCapacity,Economy.Items(0),stored.Concat(new[] {loot.Item}));
            if(nativeMove==null || !nativeMove()) return false;
            Economy=next; registry.Despawn(entity); PublishEconomy(); return true;
        }
        private InventoryResultCode PrepareEconomy(InventoryRequest r,PersistentCoopPlayerId owner,InventoryState inventory,out InventoryItemState item,out int delta)
        {
            item=null; delta=0;
            if(Economy==null || QuoteEconomy==null) return InventoryResultCode.NotReady;
            if(r.ExpectedEconomyRevision!=Economy.Revision || Economy.Revision==ulong.MaxValue) return InventoryResultCode.StaleRevision;
            if(pending.Values.Any(t=>t.Owner.Equals(owner))) return InventoryResultCode.InvalidState;
            if(r.Action==InventoryAction.Buy) item=new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()),r.Definition);
            else if(r.Action==InventoryAction.Withdraw) item=Economy.Find(r.Container,new NetworkItemInstanceId(r.ItemInstance));
            else item=inventory.Find(new NetworkItemInstanceId(r.ItemInstance));
            ItemDefinitionRule rule;
            if(item==null || item.Definition!=r.Definition) return InventoryResultCode.NotOwner;
            if(!rules.TryGetValue(item.Definition,out rule)) return InventoryResultCode.UnsupportedItem;
            var candidate=item;
            if(pending.Values.Any(t=>t.Item!=null && t.Item.Instance.Equals(candidate.Instance))) return InventoryResultCode.InvalidState;
            if(r.Action==InventoryAction.Buy || r.Action==InventoryAction.Withdraw) {
                var incoming=pending.Values.Where(t=>t.Owner.Equals(owner) && (t.Request.Action==InventoryAction.Pickup || t.Request.Action==InventoryAction.Buy || t.Request.Action==InventoryAction.Withdraw)).ToArray();
                float weight=inventory.Items.Sum(i=>rules[i.Definition].Weight)+incoming.Sum(t=>rules[t.Item.Definition].Weight);
                float allowance=r.Action==InventoryAction.Buy ? 0 : 5;
                if(inventory.ItemCount+incoming.Length>=InventoryLimits.MaxItems || weight+rule.Weight>inventory.Capacity+allowance+.0001f || rule.MaxAmount>0 && inventory.Count(item.Definition)+incoming.Count(t=>t.Item.Definition==candidate.Definition)>=rule.MaxAmount) return InventoryResultCode.InventoryFull;
            }
            if(r.Action==InventoryAction.Deposit) {
                var stored=Economy.Items(r.Container); var incoming=pending.Values.Where(t=>t.Request.Action==InventoryAction.Deposit && t.Request.Container==r.Container).ToArray();
                if(stored.Length+incoming.Length>=InventoryLimits.MaxItems || stored.Sum(i=>rules[i.Definition].Weight)+incoming.Sum(t=>rules[t.Item.Definition].Weight)+rule.Weight>Economy.Capacity(r.Container)+.0001f) return InventoryResultCode.InventoryFull;
            }
            var quote=QuoteEconomy(r,item,owner); if(quote==null) return InventoryResultCode.UnsupportedItem;
            if(quote.Code!=InventoryResultCode.Reserved) return quote.Code;
            delta=quote.MoneyDelta;
            if(r.Action==InventoryAction.Buy && delta>0 || r.Action==InventoryAction.Sell && delta<0 || r.Action>=InventoryAction.Deposit && delta!=0) return InventoryResultCode.InvalidState;
            long reserved=pending.Values.Where(t=>t.MoneyDelta<0).Sum(t=>-(long)t.MoneyDelta);
            if(delta<0 && (long)Economy.Money-reserved+delta<0 || (long)Economy.Money+delta>int.MaxValue) return InventoryResultCode.InsufficientFunds;
            return InventoryResultCode.Reserved;
        }
        private InventoryTransactionResult CompleteEconomy(InventoryTransaction t,InventoryState inventory)
        {
            var r=t.Request;
            if(Economy==null || Economy.Revision==ulong.MaxValue || inventory.Revision!=r.ExpectedInventoryRevision || (long)Economy.Money+t.MoneyDelta<0 || (long)Economy.Money+t.MoneyDelta>int.MaxValue) return Finish(t,InventoryResultCode.Aborted);
            if(r.Action>=InventoryAction.Deposit && QuoteEconomy?.Invoke(r,t.Item,t.Owner)?.Code!=InventoryResultCode.Reserved) return Finish(t,InventoryResultCode.Aborted);
            var owned=inventory.Items; var stored=Economy.Items(r.Container);
            bool receiving=r.Action==InventoryAction.Buy || r.Action==InventoryAction.Withdraw;
            if(receiving) {if(inventories.Values.Any(v=>v.Find(t.Item.Instance)!=null)) return Finish(t,InventoryResultCode.Aborted);}
            else if(inventory.Find(t.Item.Instance)==null) return Finish(t,InventoryResultCode.Aborted);
            if(r.Action==InventoryAction.Withdraw && Economy.Find(r.Container,t.Item.Instance)==null) return Finish(t,InventoryResultCode.Aborted);
            var next=new InventoryState(inventory.Owner,inventory.Revision+1,inventory.Capacity,receiving ? owned.Concat(new[] {t.Item}) : owned.Where(i=>!i.Instance.Equals(t.Item.Instance)),!receiving && inventory.Equipped.HasValue && inventory.Equipped.Value.Equals(t.Item.Instance) ? null : inventory.Equipped);
            var nextStored=r.Action==InventoryAction.Deposit ? stored.Concat(new[] {t.Item}) : r.Action==InventoryAction.Withdraw ? stored.Where(i=>!i.Instance.Equals(t.Item.Instance)) : stored;
            var economy=new EconomyState(Economy.Revision+1,Economy.Money+t.MoneyDelta,Economy.StorageCapacity,Economy.LockerCapacity,r.Container==0 ? nextStored : Economy.Items(0),r.Container==1 ? nextStored : Economy.Items(1));
            // Both locations and the wallet commit before any native/network subscriber.
            inventories[inventory.Owner]=next; Economy=economy; Publish(next); PublishEconomy();
            return Finish(t,InventoryResultCode.Success);
        }
        private void PublishEconomy()
        {
            if(EconomyChanged==null) return;
            foreach(Action<EconomyState> subscriber in EconomyChanged.GetInvocationList()) try {subscriber(Economy);} catch {AdapterFailures++;}
        }
    }
}
