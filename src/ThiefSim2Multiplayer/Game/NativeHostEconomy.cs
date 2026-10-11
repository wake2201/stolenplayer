using System;
using System.Collections.Generic;
using System.Linq;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Inventory;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class CooperativeEconomyController
    {
        private void ObserveSoloHost()
        {
            var inventory=network.Inventories.LocalState; var economy=network.Inventories.SharedEconomy;
            if(inventory==null || economy==null || network.Inventories.Authority.PendingCount!=0) return;
            float storage=(int)GameAccess.Get(native.Inventory,"max_storage_capacity"),locker=(int)GameAccess.Get(native.Inventory,"max_locker_capacity");
            foreach(UnityEngine.Component item in (Array)GameAccess.Get(native.Player,"item_IDs2")) if(!native.Definitions.ContainsKey((ushort)(int)GameAccess.Get(item,"item_ID"))) {
                float weight=(float)GameAccess.Get(item,"item_weight"); storage-=(int)GameAccess.Get(item,"item_amount_storage")*weight; locker-=(int)GameAccess.Get(item,"item_amount_locker")*weight;
            }
            storage=Math.Max(0,storage); locker=Math.Max(0,locker);
            short selected; bool knownEquipment=EquipmentSlotMap.TryGetItem((int)GameAccess.Get(native.Player,"currentItem"),out selected);
            if(knownEquipment && selected>=0 && !native.Definitions.ContainsKey((ushort)selected)) selected=-1;
            var priorEquipped=inventory.Equipped.HasValue ? inventory.Find(inventory.Equipped.Value) : null;
            bool changed=(int)GameAccess.Get(native.Player,"moneyAm")!=economy.Money || native.Capacity!=inventory.Capacity || storage!=economy.StorageCapacity || locker!=economy.LockerCapacity || knownEquipment && (priorEquipped==null ? -1 : priorEquipped.Definition)!=selected;
            foreach(var pair in native.Definitions) changed|=native.Amount(pair.Key)!=inventory.Count(pair.Key) || (int)GameAccess.Get(pair.Value,"item_amount_storage")!=economy.Count(0,pair.Key) || (int)GameAccess.Get(pair.Value,"item_amount_locker")!=economy.Count(1,pair.Key);
            if(!changed) return;
            var previous=new[] {inventory.Items,economy.Items(0),economy.Items(1)};
            var wanted=new int[3,InventoryLimits.MaxDefinitions]; var next=new List<InventoryItemState>[] {new List<InventoryItemState>(),new List<InventoryItemState>(),new List<InventoryItemState>()};
            foreach(var pair in native.Definitions) {wanted[0,pair.Key]=native.Amount(pair.Key); wanted[1,pair.Key]=(int)GameAccess.Get(pair.Value,"item_amount_storage"); wanted[2,pair.Key]=(int)GameAccess.Get(pair.Value,"item_amount_locker");}
            var used=new HashSet<NetworkItemInstanceId>();
            for(int location=0;location<3;location++) foreach(var group in previous[location].GroupBy(i=>i.Definition)) foreach(var item in group.Take(wanted[location,group.Key])) {next[location].Add(item); used.Add(item.Instance);}
            var available=previous.SelectMany(s=>s).Where(i=>!used.Contains(i.Instance)).GroupBy(i=>i.Definition).ToDictionary(g=>g.Key,g=>new Queue<InventoryItemState>(g));
            for(int location=0;location<3;location++) foreach(var id in native.Definitions.Keys) {
                int needed=wanted[location,id]-next[location].Count(i=>i.Definition==id);
                if(wanted[location,id]<0 || wanted[location,id]>InventoryLimits.MaxItems) throw new InvalidOperationException("Native Host quantities exceed cooperative bounds.");
                while(needed-->0) {Queue<InventoryItemState> pool; next[location].Add(available.TryGetValue(id,out pool) && pool.Count>0 ? pool.Dequeue() : new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()),id));}
            }
            var equipped=inventory.Equipped.HasValue && next[0].Any(i=>i.Instance.Equals(inventory.Equipped.Value)) ? inventory.Equipped : null;
            if(knownEquipment) {var item=selected<0 ? null : next[0].FirstOrDefault(i=>i.Definition==selected); equipped=item==null ? (NetworkItemInstanceId?)null : item.Instance;}
            network.Inventories.ObserveSoloHost(new InventoryState(inventory.Owner,inventory.Revision+1,native.Capacity,next[0],equipped),new EconomyState(economy.Revision+1,(int)GameAccess.Get(native.Player,"moneyAm"),storage,locker,next[1],next[2]));
        }
    }
}
