using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    // Native UI intents use the existing personal inventory reservation/completion channel.
    internal sealed partial class CooperativeEconomyController : IDisposable
    {
        private static CooperativeEconomyController current;
        private readonly NetworkManager network;
        private readonly Action<string> log;
        private readonly PlayerInventoryAdapter native=new PlayerInventoryAdapter();
        private readonly Harmony harmony=new Harmony(Plugin.Id+".economy");
        private readonly Dictionary<ushort,Component> catalog=new Dictionary<ushort,Component>();
        private Component[] shopButtons=Array.Empty<Component>();
        private Component[] computers=Array.Empty<Component>(), sellers=Array.Empty<Component>(), boxes=Array.Empty<Component>();
        private Component preparedPlayer;
        private EconomyState deferred;
        private Pending pending;
        private bool executing, restoring;
        private ulong requiredInventory,requiredEconomy;
        private double nextObserve;
        private int originalMoney;
        private readonly Dictionary<ushort,int[]> originalStored=new Dictionary<ushort,int[]>();
        private bool capturedClient;
        private bool Active=>network.Session.State!=SessionState.Offline;
        private bool Client=>Active && !network.Session.IsHost;
        private bool Cooperative=>Active && (!network.Session.IsHost || network.PeerConnected);
        internal static bool NativeComputerTransition(Component player)
        {
            var c=current;
            return c!=null && player && c.computers.Any(pc=>pc && pc.gameObject.activeInHierarchy && GameAccess.Get(pc,"PS") as Component==player && (GameAccess.Bool(pc,"isUsing") || GameAccess.Bool(pc,"sitDown") || GameAccess.Bool(pc,"standUp")));
        }
        private sealed class Pending
        {
            internal InventoryRequest Request;
            internal Component UI,Selection,Button;
            internal ulong Transaction;
            internal int Before,Delta;
            internal bool Started;
            internal bool ChainSell;
            internal double Deadline;
        }
        internal CooperativeEconomyController(NetworkManager manager,Action<string> logger)
        {
            network=manager; log=logger; current=this;
            network.Inventories.Authority.QuoteEconomy=Quote;
            network.Inventories.ResultReceived+=Result;
            network.Inventories.EconomyReceived+=Received;
            network.RemoteRemoved+=Disconnected;
            Patch("Shop_Manager","Buy_Item",nameof(Buy));
            Patch("Inventory","Sell_Item_NewSystem",nameof(Sell));
            Patch("Inventory","MoveItemToStorage",nameof(Transfer));
            harmony.Patch(AccessTools.Method(Type.GetType("PlayerScript, Assembly-CSharp",true),"OpenStorage"),prefix:new HarmonyMethod(typeof(CooperativeEconomyController),nameof(StorageOpening)),postfix:new HarmonyMethod(typeof(CooperativeEconomyController),nameof(StorageOpened)));
            Patch("SaveGame","SaveGameNewSystem",nameof(Save));
            Patch("PlayerScript","SaveMoneyAmount",nameof(Save));
            Patch("ItemID","Save_Item_Amount",nameof(SaveItem));
            harmony.Patch(AccessTools.Method(Type.GetType("ItemID, Assembly-CSharp",true),"Remove_Item_Blackbay"),prefix:new HarmonyMethod(typeof(CooperativeEconomyController),nameof(StorageRemovalStarting)),postfix:new HarmonyMethod(typeof(CooperativeEconomyController),nameof(StorageRemoved)));
            Patch("GameManager","TryToMoveDroppedItemsToStorage",nameof(AutoStorage));
            harmony.Patch(AccessTools.Method(Type.GetType("GameManager, Assembly-CSharp",true),"TryToMoveDroppedItemsToStorage"),transpiler:new HarmonyMethod(typeof(CooperativeEconomyController),nameof(NativeCargoCalls)));
            harmony.Patch(AccessTools.Method(Type.GetType("SPrefs, Assembly-CSharp",true),"SetInt"),new HarmonyMethod(typeof(CooperativeEconomyController),nameof(SavePref)));
            harmony.Patch(AccessTools.Method(Type.GetType("Inventory, Assembly-CSharp",true),"Sell_Item_NewSystem"),transpiler:new HarmonyMethod(typeof(CooperativeEconomyController),nameof(SalePrice)));
        }
        private void Patch(string type,string method,string prefix)=>harmony.Patch(AccessTools.Method(Type.GetType(type+", Assembly-CSharp",true),method),new HarmonyMethod(typeof(CooperativeEconomyController),prefix));
        private static object Invoke(object obj,string method,params object[] args)=>AccessTools.Method(obj.GetType(),method).Invoke(obj,args);
        private static Component[] SceneComponents(string type)
        {
            var found=new List<Component>(); var contract=Type.GetType(type+", Assembly-CSharp",true);
            for(int i=0;i<SceneManager.sceneCount;i++) {var scene=SceneManager.GetSceneAt(i); if(!scene.isLoaded) continue; foreach(var root in scene.GetRootGameObjects()) found.AddRange(root.GetComponentsInChildren(contract,true).Cast<Component>());}
            return found.ToArray();
        }
        private bool Hideout=>SceneManager.GetSceneByName("PlayerHideout").isLoaded;
        internal void Prepare()
        {
            if(!Active || network.World.TestEntitiesEnabled || network.Scenes.Active && !network.Scenes.LocalLoaded || !native.Refresh()) return;
            if(network.Session.IsHost && network.Inventories.Authority.Get(network.Inventories.LocalIdentity)==null) return;
            if(preparedPlayer==native.Player) return;
            preparedPlayer=native.Player; catalog.Clear();
            shopButtons=SceneComponents("Shop_Button");
            foreach(var button in shopButtons) {int id=(int)GameAccess.Get(button,"shop_item_ID"); if(id>=0 && id<InventoryLimits.MaxDefinitions && native.Definitions.ContainsKey((ushort)id) && !catalog.ContainsKey((ushort)id)) catalog.Add((ushort)id,button);}
            computers=SceneComponents("UseComputer"); sellers=SceneComponents("ShopTrigger"); boxes=SceneComponents("CarBox");
            if(Client && !capturedClient) {originalMoney=(int)GameAccess.Get(native.Player,"moneyAm"); foreach(var pair in native.Definitions) originalStored[pair.Key]=new[] {(int)GameAccess.Get(pair.Value,"item_amount_storage"),(int)GameAccess.Get(pair.Value,"item_amount_locker"),native.Amount(pair.Key)}; capturedClient=true;}
            if(network.Session.IsHost && network.Inventories.Authority.Economy==null) {
                var stored=new List<InventoryItemState>[2] {new List<InventoryItemState>(),new List<InventoryItemState>()};
                float[] unsupported=new float[2];
                foreach(Component item in (Array)GameAccess.Get(native.Player,"item_IDs2")) {
                    int id=(int)GameAccess.Get(item,"item_ID");
                    for(byte c=0;c<2;c++) {int amount=(int)GameAccess.Get(item,c==0 ? "item_amount_storage" : "item_amount_locker");
                        if(!native.Definitions.ContainsKey((ushort)id)) {unsupported[c]+=amount*(float)GameAccess.Get(item,"item_weight"); continue;}
                        if(amount<0 || amount>InventoryLimits.MaxItems-stored[c].Count) throw new InvalidOperationException("Native storage exceeds cooperative bounds.");
                        for(int n=0;n<amount;n++) stored[c].Add(new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()),(ushort)id));
                    }
                }
                network.Inventories.Authority.InitializeEconomy(new EconomyState(1,(int)GameAccess.Get(native.Player,"moneyAm"),Math.Max(0,(int)GameAccess.Get(native.Inventory,"max_storage_capacity")-unsupported[0]),Math.Max(0,(int)GameAccess.Get(native.Inventory,"max_locker_capacity")-unsupported[1]),stored[0],stored[1]));
                log("[TS2MP] Shared wallet initialized from Host save; money="+network.Inventories.SharedEconomy.Money+" storage="+stored[0].Count+" locker="+stored[1].Count);
            }
            deferred=network.Inventories.SharedEconomy;
        }
        private EconomyQuote Reject(InventoryResultCode code)=>new EconomyQuote {Code=code};
        private static bool OnWebsite(Component computer,Component button)
        {
            var shop=GameAccess.Get(button,"shopManagerScript") as Component;
            return ((GameObject[])GameAccess.Get(computer,"Websites")).Any(w=>w && (button.transform==w.transform || button.transform.IsChildOf(w.transform) || shop && (shop.transform==w.transform || shop.transform.IsChildOf(w.transform))));
        }
        private Component ShopButton(ushort id,Vector3 position)
        {
            var computer=computers.Where(c=>c && c.gameObject.activeInHierarchy && (c.transform.position-position).sqrMagnitude<=16).OrderBy(c=>(c.transform.position-position).sqrMagnitude).FirstOrDefault();
            return computer ? shopButtons.FirstOrDefault(b=>b && (int)GameAccess.Get(b,"shop_item_ID")==id && OnWebsite(computer,b)) : null;
        }
        private EconomyQuote Quote(InventoryRequest r,InventoryItemState item,PersistentCoopPlayerId owner)
        {
            if(!native.Ready || !native.Definitions.ContainsKey(item.Definition)) return Reject(InventoryResultCode.UnsupportedItem);
            Vector3 position;
            if(r.ActorId==1) position=native.Player.transform.position;
            else {if(!network.Telemetry.HasPose || network.Telemetry.ReceiveAge(network.Now)>1) return Reject(InventoryResultCode.NotReady); var pose=network.Telemetry.Latest; position=new Vector3(pose.X,pose.Y,pose.Z);}
            return QuoteAt(r,item,owner,network.Inventories.Authority,position);
        }
        private EconomyQuote QuoteAt(InventoryRequest r,InventoryItemState item,PersistentCoopPlayerId owner,InventoryAuthority authority,Vector3 position)
        {
            var definition=native.Definitions[item.Definition]; var gm=GameAccess.Get(native.Player,"GM"); var skills=GameAccess.Get(native.Player,"skill_manager");
            if(r.Action==InventoryAction.Buy) {
                var button=ShopButton(item.Definition,position);
                if(!button) return Reject(InventoryResultCode.OutOfRange);
                int skill=(int)GameAccess.Get(button,"required_skill_int");
                if(skill>=0 && !(bool)Invoke(skills,"Check_If_Skill_Learned_Array",skill)) return Reject(InventoryResultCode.InvalidState);
                if(GameAccess.Bool(button,"story_required")) {var story=GameAccess.Get(gm,"story_manager"); var scripts=(IList)GameAccess.Get(story,"story_scripts"); int required=0; for(int i=0;i<scripts.Count;i++) if((string)GameAccess.Get(scripts[i],"story_description")== (string)GameAccess.Get(button,"story_name_req")) {required=i; break;} if((int)GameAccess.Get(story,"story")<required && (int)AccessTools.Method(Type.GetType("SPrefs, Assembly-CSharp",true),"GetInt",new[] {typeof(string)}).Invoke(null,new object[] {"Story"})<required) return Reject(InventoryResultCode.InvalidState);}
                int count=authority.Get(owner).Count(item.Definition);
                var economy=authority.Economy;
                if(GameAccess.Bool(button,"allow_only_one") && count+economy.Count(0,item.Definition)+economy.Count(1,item.Definition)>0 || GameAccess.Bool(button,"allow_a_few") && count>=(int)GameAccess.Get(button,"max_amount")) return Reject(InventoryResultCode.InventoryFull);
                float price=(float)GameAccess.Get(definition,"item_value"); if(GameAccess.Bool(gm,"lowStealgearPrices")) price=Mathf.RoundToInt(price*.7f);
                if(!Protocol.Finite(price) || price<0 || price>int.MaxValue-1024f) return Reject(InventoryResultCode.UnsupportedItem);
                return new EconomyQuote {MoneyDelta=-Mathf.RoundToInt(price)};
            }
            if(r.Action==InventoryAction.Sell) {
                var venue=sellers.Where(c=>c && c.gameObject.activeInHierarchy && (GameAccess.Bool(c,"pawnGuy") || GameAccess.Bool(c,"junkyardGuy") || GameAccess.Bool(c,"houseThing")) && (c.transform.position-position).sqrMagnitude<=16).OrderBy(c=>(c.transform.position-position).sqrMagnitude).FirstOrDefault();
                if(!venue) return Reject(InventoryResultCode.OutOfRange);
                if(GameAccess.Bool(definition,"cant_sell")) return Reject(InventoryResultCode.UnsupportedItem);
                float value=(float)GameAccess.Get(definition,"item_value")*(GameAccess.Bool(venue,"houseThing") ? .9f : 1f);
                if(GameAccess.Bool(gm,"isHardcore")) value=Mathf.RoundToInt(value*1.25f);
                if((bool)Invoke(skills,"Check_If_Skill_Learned","negotiator_skill")) value=Mathf.RoundToInt(value*1.25f);
                if(GameAccess.Bool(definition,"isHotItem")) value=Mathf.RoundToInt(value*3f);
                if(GameAccess.Bool(gm,"sellerDay")) value=Mathf.RoundToInt(value*1.2f);
                if(GameAccess.Bool(gm,"restockDay")) value=Mathf.RoundToInt(value*.9f);
                float upgrade=(int)Invoke(skills,"CheckSkillUpgrade","sellvalue_extra"); if(upgrade>0) value*=1+upgrade/25f;
                if(!Protocol.Finite(value) || value<0 || value>int.MaxValue-1024f) return Reject(InventoryResultCode.UnsupportedItem);
                return new EconomyQuote {MoneyDelta=Mathf.RoundToInt(value)};
            }
            if(GameAccess.Bool(definition,"isStoryItem")) return Reject(InventoryResultCode.UnsupportedItem);
            return QuoteStorage(r,position);
        }
        private bool Start(InventoryAction action,Component ui,Component selection,ushort definition,byte container=0,bool chainSell=false)
        {
            Prepare(); var state=network.Inventories.LocalState; var economy=network.Inventories.SharedEconomy;
            if(!network.TransactionsAllowed || state==null || economy==null || pending!=null || !native.Definitions.ContainsKey(definition)) return false;
            if(!ui || !ui.gameObject.activeInHierarchy || !selection || action==InventoryAction.Buy && (!(GameAccess.Get(ui,"Buy_Button") as GameObject)?.activeInHierarchy ?? true) || action==InventoryAction.Sell && (!GameAccess.Bool(ui,"isShop") || GameAccess.Bool(selection,"cant_sell") || GameAccess.Bool(ui,"sellingItems") && !chainSell) || action>=InventoryAction.Deposit && !GameAccess.Bool(ui,"isStorage")) return false;
            if(action>=InventoryAction.Deposit && (!StorageContextValid(container) || GameAccess.Bool(selection,"isStoryItem"))) return false;
            var item=action==InventoryAction.Buy ? null : action==InventoryAction.Withdraw ? economy.Items(container).FirstOrDefault(i=>i.Definition==definition) : state.Items.FirstOrDefault(i=>i.Definition==definition);
            if(action!=InventoryAction.Buy && item==null) return false;
            var origin=action>=InventoryAction.Deposit ? storageOrigin : Vector3.zero;
            var aim=action>=InventoryAction.Deposit ? storageAim : Vector3.zero;
            var request=network.Inventories.Request(action,instance:item==null ? Guid.Empty : item.Instance.Value,definition:definition,container:container,x:origin.x,y:origin.y,z:origin.z,aimX:aim.x,aimY:aim.y,aimZ:aim.z); if(request==null) return false;
            pending=new Pending {Request=request,UI=ui,Selection=selection,Button=action==InventoryAction.Buy ? null : GameAccess.Get(ui,"current_item_button") as Component,Before=native.Amount(definition),ChainSell=chainSell,Deadline=network.Now+InventoryLimits.ReservationSeconds+1}; network.Inventories.EconomyLocalPending=true;
            if(network.Session.IsHost) Result(network.Inventories.HostBegin(request,_=>InventoryResultCode.Reserved));
            else if(!network.Inventories.SendRequest(request)) Finish();
            return false;
        }
        private void Result(InventoryTransactionResult result)
        {
            var t=pending; if(t==null || t.Request.RequestId!=result.RequestId) return;
            if(result.Code==InventoryResultCode.Reserved) {
                if(t.Started) return;
                t.Transaction=result.TransactionId; t.Delta=result.MoneyDelta;
                bool valid=native.Ready && t.UI && t.UI.gameObject.activeInHierarchy && t.Selection && network.Now<t.Deadline;
                if(t.Request.Action==InventoryAction.Buy) valid&=(GameAccess.Get(t.UI,"button_script_save") as Component)==t.Selection;
                else valid&=t.Button && t.Button.gameObject.activeInHierarchy && (GameAccess.Get(t.UI,"current_item_button") as Component)==t.Button && (GameAccess.Get(t.UI,"current_item_script") as Component)==t.Selection && GameAccess.Bool(t.Button,"is_in_storage")== (t.Request.Action==InventoryAction.Withdraw) && native.Amount(t.Request.Definition)==t.Before && (t.Request.Action==InventoryAction.Sell ? GameAccess.Bool(t.UI,"isShop") : GameAccess.Bool(t.UI,"isStorage") && (int)GameAccess.Get(t.UI,"storage_Type")==t.Request.Container);
                if(t.Request.Action>=InventoryAction.Deposit) valid&=StorageContextValid(t.Request.Container) && !GameAccess.Bool(t.Selection,"isStoryItem");
                if(!valid) {network.Inventories.Complete(t.Request,t.Transaction,false); return;}
                t.Started=true; executing=true;
                try {using(new NetworkApplyScope()) {
                    if(t.Request.Action==InventoryAction.Buy) {var price=GameAccess.Get(t.Selection,"value"); try {GameAccess.Set(t.Selection,"value",(float)-t.Delta); GameAccess.Call(t.UI,"Buy_Item");} finally {GameAccess.Set(t.Selection,"value",price);}}
                    else if(t.Request.Action==InventoryAction.Sell) Invoke(t.UI,"Sell_Item_NewSystem",t.ChainSell);
                    else GameAccess.Call(t.UI,"MoveItemToStorage");
                }
                bool receiving=t.Request.Action==InventoryAction.Buy || t.Request.Action==InventoryAction.Withdraw;
                network.Inventories.Complete(t.Request,t.Transaction,native.Amount(t.Request.Definition)==t.Before+(receiving ? 1 : -1));
                } catch(Exception error) {log("[TS2MP] Native economy execution failed: "+error.GetBaseException()); network.Inventories.Complete(t.Request,t.Transaction,false);}
                finally {executing=false;}
            } else {
                if(result.Code==InventoryResultCode.Success) {requiredInventory=Math.Max(requiredInventory,result.InventoryRevision); requiredEconomy=Math.Max(requiredEconomy,result.EconomyRevision);}
                Finish();
            }
        }
        private void Finish() {pending=null; network.Inventories.EconomyLocalPending=false; deferred=network.Inventories.SharedEconomy;}
        private void Received(EconomyState state)
        {
            deferred=state;
            // Remote commits must reach the original Host save fields in the
            // same main-thread operation, before any native save callback.
            if(Cooperative && network.Session.IsHost && native.Ready && pending==null) {Apply(state); deferred=null;}
        }
        internal void Observe()
        {
            if(!Active) {if(capturedClient) RestoreClient(); return;}
            Prepare(); if(!native.Ready || network.Scenes.Active && !network.Scenes.LocalLoaded) return;
            ObserveStorageContext();
            if(!Cooperative) { deferred=null; if(network.Now>=nextObserve) {nextObserve=network.Now+.1; ObserveSoloHost();} return; }
            if(pending!=null && network.Now>=pending.Deadline) {if(pending.Transaction!=0) network.Inventories.Complete(pending.Request,pending.Transaction,false); Finish();}
            if(pending!=null || LootInventoryController.NativePickupPending || network.Inventories.LocalState==null || network.Inventories.LocalState.Revision<requiredInventory || network.Inventories.SharedEconomy==null || network.Inventories.SharedEconomy.Revision<requiredEconomy) return;
            if(deferred!=null) {native.Apply(network.Inventories.LocalState,log); Apply(deferred); deferred=null;}
            if(network.Now<nextObserve) return; nextObserve=network.Now+.1;
            int money=(int)GameAccess.Get(native.Player,"moneyAm");
            if(network.Session.IsHost) network.Inventories.Authority.ObserveHostMoney(money);
            else if(money!=network.Inventories.SharedEconomy.Money) GameAccess.Set(native.Player,"moneyAm",network.Inventories.SharedEconomy.Money);
        }
        private void Apply(EconomyState state)
        {
            GameAccess.Set(native.Player,"moneyAm",state.Money); bool changed=false;
            foreach(var pair in native.Definitions) for(byte c=0;c<2;c++) {string field=c==0 ? "item_amount_storage" : "item_amount_locker"; int count=state.Count(c,pair.Key); if((int)GameAccess.Get(pair.Value,field)!=count) {GameAccess.Set(pair.Value,field,count); changed=true;}}
            if(changed && native.Inventory.gameObject.activeInHierarchy && GameAccess.Bool(native.Inventory,"isStorage")) {
                // Native OpenStorage instantiates both lists. Retire the previous
                // buttons before rebuilding an already open cooperative interface.
                foreach(string list in new[] {"buttons","buttons_storage"}) foreach(Component button in ((IList)GameAccess.Get(native.Inventory,list)).Cast<Component>().ToArray()) if(button) button.gameObject.SetActive(false);
                GameAccess.Call(native.Inventory,"ClearInventory");
                Invoke(native.Inventory,"OpenStorage",(int)GameAccess.Get(native.Inventory,"storage_Type"));
                ((MonoBehaviour)native.Inventory).StartCoroutine((IEnumerator)Invoke(native.Inventory,"Reload_Button_List_Storage"));
            }
        }
        private void RestoreClient()
        {
            if(!native.Refresh()) return;
            restoring=true;
            try {if(native.Refresh()) {GameAccess.Set(native.Player,"moneyAm",originalMoney); foreach(var pair in originalStored) if(native.Definitions.ContainsKey(pair.Key)) {GameAccess.Set(native.Definitions[pair.Key],"item_amount_storage",pair.Value[0]); GameAccess.Set(native.Definitions[pair.Key],"item_amount_locker",pair.Value[1]); GameAccess.Set(native.Definitions[pair.Key],"item_amount",pair.Value[2]);}} float weight=0; foreach(Component item in (Array)GameAccess.Get(native.Player,"item_IDs2")) if(item && !GameAccess.Bool(item,"is_heavy")) weight+=(int)GameAccess.Get(item,"item_amount")*(float)GameAccess.Get(item,"item_weight"); GameAccess.Set(native.Player,"currentWeight",weight); native.ReconcileFlashlightOwnership();}
            finally {capturedClient=false; originalStored.Clear(); restoring=false;}
        }
        private void Disconnected() {Finish(); CloseOwnedStorage(); requiredInventory=requiredEconomy=0; if(capturedClient) RestoreClient();}
        internal void SceneInvalidating() {if(pending!=null) Finish(); CloseOwnedStorage(); preparedPlayer=null; native.Reset(); catalog.Clear(); shopButtons=computers=sellers=boxes=Array.Empty<Component>(); deferred=null;}
        private static bool Bypass=>current==null || !current.Cooperative || current.executing;
        private static bool Buy(Component __instance)
        {
            if(Bypass) return true; var selected=GameAccess.Get(__instance,"button_script_save") as Component; if(!selected) return false;
            int id=(int)GameAccess.Get(selected,"shop_item_ID"); return current.Start(InventoryAction.Buy,__instance,selected,(ushort)id);
        }
        private static bool Sell(Component __instance,bool __0)
        {
            if(Bypass) return true; var selected=GameAccess.Get(__instance,"current_item_script") as Component;
            return selected && current.Start(InventoryAction.Sell,__instance,selected,(ushort)(int)GameAccess.Get(selected,"item_ID"),chainSell:__0);
        }
        private static bool Transfer(Component __instance)
        {
            if(Bypass) return true; var selected=GameAccess.Get(__instance,"current_item_script") as Component; var button=GameAccess.Get(__instance,"current_item_button") as Component; int container=(int)GameAccess.Get(__instance,"storage_Type");
            return selected && button && container>=0 && container<=1 && current.Start(GameAccess.Bool(button,"is_in_storage") ? InventoryAction.Withdraw : InventoryAction.Deposit,__instance,selected,(ushort)(int)GameAccess.Get(selected,"item_ID"),(byte)container);
        }
        private static bool Save() {if(current==null || !current.Cooperative || current.restoring) return true; if(current.Client) return false; current.Observe(); return current.pending==null && current.network.Inventories.Authority.PendingCount==0 && !LootInventoryController.NativePickupPending;}
        private static bool SaveItem()=>current==null || !current.Client || current.restoring;
        // Only the Host executes native save-time vacuuming; its supported loose
        // entities move through the original native call and canonical ownership.
        private static bool AutoStorage()=>current==null || !current.Cooperative || !current.Client;
        private static void NativeCargo(object item,GameObject drop)
        {
            var c=current;
            if(c!=null && c.Cooperative && c.network.Session.IsHost && drop) {
                var pickup=drop.GetComponent(Type.GetType("Pickupable, Assembly-CSharp",true));
                if(LootInventoryController.StoreNativeDrop(pickup,()=> {int before=(int)GameAccess.Get(item,"item_amount_locker"); GameAccess.Call(item,"MoveToStorageOnDrop"); return (int)GameAccess.Get(item,"item_amount_locker")==before+1;})) return;
            }
            GameAccess.Call(item,"MoveToStorageOnDrop");
        }
        private static IEnumerable<CodeInstruction> NativeCargoCalls(IEnumerable<CodeInstruction> instructions)
        {
            int matches=0;
            foreach(var instruction in instructions) {
                if(instruction.opcode==OpCodes.Callvirt && instruction.operand is MethodInfo method && method.Name=="MoveToStorageOnDrop") {
                    var drop=new CodeInstruction(OpCodes.Ldloc_2); drop.labels.AddRange(instruction.labels); instruction.labels.Clear(); yield return drop;
                    instruction.opcode=OpCodes.Call; instruction.operand=AccessTools.Method(typeof(CooperativeEconomyController),nameof(NativeCargo)); matches++;
                }
                yield return instruction;
            }
            if(matches!=1) throw new InvalidOperationException("Native cargo call contract changed.");
        }
        private static bool SavePref(string __0)=>current==null || !current.Client || current.restoring || !(__0=="Money" || __0.StartsWith("Item_",StringComparison.Ordinal) || __0.StartsWith("ItemDay_",StringComparison.Ordinal) || __0.StartsWith("Storage_Item_",StringComparison.Ordinal) || __0.StartsWith("Locker_Item_",StringComparison.Ordinal));
        private static float AuthoritativeSaleValue(float value)=>current!=null && current.executing && current.pending!=null && current.pending.Request.Action==InventoryAction.Sell ? current.pending.Delta : value;
        private static IEnumerable<CodeInstruction> SalePrice(IEnumerable<CodeInstruction> instructions)
        {
            int matches=0;
            foreach(var instruction in instructions) {
                if(instruction.opcode==OpCodes.Ldfld && instruction.operand is FieldInfo field && field.Name=="moneyAm") {yield return new CodeInstruction(OpCodes.Ldloc_0); yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(CooperativeEconomyController),nameof(AuthoritativeSaleValue))); yield return new CodeInstruction(OpCodes.Stloc_0); matches++;}
                yield return instruction;
            }
            if(matches!=1) throw new InvalidOperationException("Native sale valuation contract changed: "+matches);
        }
        public void Dispose() {Disconnected(); harmony.UnpatchSelf(); network.Inventories.Authority.QuoteEconomy=null; network.Inventories.ResultReceived-=Result; network.Inventories.EconomyReceived-=Received; network.RemoteRemoved-=Disconnected; if(current==this) current=null;}
    }
}
