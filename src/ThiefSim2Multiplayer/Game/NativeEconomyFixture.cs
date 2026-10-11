using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class CooperativeEconomyController
    {
        internal IEnumerator NativeFixture()
        {
            bool failed=false;
            yield return GuardedRoutine.Run(NativeFixtureRole(false),e=> {failed=true; log("[TS2MP] NATIVE ECONOMY ROLE FAILED Host: "+e);});
            yield return new WaitForSecondsRealtime(.4f);
            yield return GuardedRoutine.Run(NativeFixtureRole(true),e=> {failed=true; log("[TS2MP] NATIVE ECONOMY ROLE FAILED Client: "+e);});
            if(failed) throw new InvalidOperationException("Native economy acceptance incomplete; see explicit role failures.");
        }
        private WorldTimeState fixtureTime;
        private IEnumerator NativeFixtureRole(bool clientRole)
        {
            if(Active) throw new InvalidOperationException("Economy fixture requires disposable offline native gameplay.");
            if(!native.Refresh()) throw new InvalidOperationException("Original native inventory unavailable.");
            var position=native.Player.transform.position; var fixtureCamera=(Transform)GameAccess.Get(native.Player,"cam"); var cameraRotation=fixtureCamera.rotation; int checks=0; Component computer=null;
            NetworkManager peer=null;
            void Check(bool ok,string label) {if(!ok) throw new InvalidOperationException("Native economy FAIL: "+label); checks++; log("[TS2MP] NATIVE ECONOMY PASS: "+label);}
            IEnumerator Await(Func<bool> done,string label,double seconds=8)
            {
                double end=network.Now+seconds;
                while(!done()) {peer?.Poll(); network.Poll(); if(clientRole && peer!=null) peer.PublishTime(fixtureTime); if(network.Now>end) throw new InvalidOperationException("Native economy timeout: "+label+"; "+network.LastError+" / "+peer?.LastError); yield return null;}
            }
            try {
                using(var socket=new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0))) {int port=((System.Net.IPEndPoint)socket.Client.LocalEndPoint).Port; socket.Close(); if(!network.Host(port,"NativeEconomy")) throw new InvalidOperationException("Fixture Host failed.");}
                double until=network.Now+5;
                while(network.Inventories.LocalState==null || network.Inventories.SharedEconomy==null) {if(network.Now>until) throw new InvalidOperationException("Native economy initialization timed out."); yield return null;}
                if(clientRole) {
                    yield return Await(()=>network.Scenes.Active && network.Scenes.LocalLoaded,"seed Host scene descriptor");
                    var seed=network.Inventories.LocalState; var economy=network.Inventories.SharedEconomy; var rules=native.Rules.ToArray(); var target=network.Scenes.Target;
                    network.Disconnect("Native economy switches to Client role."); yield return null;
                    peer=new NetworkManager(network.BuildFingerprintForDiagnostics,log);
                    peer.Host(Plugin.FixturePort(),"ManagedEconomyHost");
                    peer.Inventories.Authority.Configure(rules); peer.Inventories.Authority.Initialize(new InventoryState(peer.Inventories.LocalIdentity,1,seed.Capacity,Array.Empty<InventoryItemState>()));
                    peer.Inventories.Authority.InitializeEconomy(economy);
                    peer.Inventories.CreateRemoteSeed=owner=>new InventoryState(owner,1,seed.Capacity,seed.Items);
                    peer.Inventories.ValidateTarget=_=>InventoryResultCode.Reserved;
                    peer.Inventories.Authority.QuoteEconomy=(r,i,o)=> {Prepare(); if(!native.Ready || !native.Definitions.ContainsKey(i.Definition) || !peer.Telemetry.HasPose || peer.Telemetry.ReceiveAge(peer.Now)>1) return Reject(InventoryResultCode.NotReady); var pose=peer.Telemetry.Latest; return QuoteAt(r,i,o,peer.Inventories.Authority,new Vector3(pose.X,pose.Y,pose.Z));};
                    peer.World.PrepareRegistry=()=> {network.World.PrepareRegistry?.Invoke(); Plugin.MirrorFixture(peer.World.Registry,network.World.Registry.Capture());};
                    peer.SetLocalScene(true,target.SceneKey); peer.PublishStableScene(target);
                    network.Join("127.0.0.1",peer.Port,"NativeEconomyClient");
                } else {
                    // Exercise ordinary native UI while Hosting alone, then join one
                    // managed peer so production cooperative hooks actually activate.
                    Check(!Cooperative && Bypass,"Hosting alone bypasses native economy interception");
                    peer=new NetworkManager(network.BuildFingerprintForDiagnostics,log);
                    peer.RequireTimeBaseline=true; peer.ApplyWorldTime=_=>true;
                    peer.World.PrepareBaseline=s=>Plugin.MirrorFixture(peer.World.Registry,s.Entities); peer.HostSceneRequested+=s=>peer.MarkSceneLoaded(s.SceneKey);
                    peer.Join("127.0.0.1",network.Port,"ManagedEconomyClient");
                }
                yield return Await(()=>network.GameplayReady && peer.GameplayReady && network.Inventories.LocalState!=null && network.Inventories.SharedEconomy!=null && (clientRole ? peer.Inventories.RemoteIdentity.HasValue : network.Inventories.RemoteIdentity.HasValue),"native economy role readiness",90);
                if(!clientRole) fixtureTime=network.LastWorldTime;
                Prepare();
                computer=computers.FirstOrDefault(c=>c && c.gameObject.activeInHierarchy && shopButtons.Any(b=>b && OnWebsite(c,b))); if(!computer) throw new InvalidOperationException("No genuine shop computer in this scene.");
                native.Player.transform.position=computer.transform.position;
                yield return Await(()=>!clientRole || peer.Telemetry.HasPose && Vector3.Distance(new Vector3(peer.Telemetry.Latest.X,peer.Telemetry.Latest.Y,peer.Telemetry.Latest.Z),computer.transform.position)<.5f,"native buyer pose");
                Component button=null; int expectedCost=0;
                foreach(var candidate in shopButtons.Where(b=>b && OnWebsite(computer,b)).OrderBy(b=>native.Definitions.TryGetValue((ushort)(int)GameAccess.Get(b,"shop_item_ID"),out var d) && !GameAccess.Bool(d,"cant_sell") ? 0 : 1)) {
                    var idCandidate=(ushort)(int)GameAccess.Get(candidate,"shop_item_ID"); if(!native.Definitions.ContainsKey(idCandidate)) continue;
                    var pair=new System.Collections.Generic.KeyValuePair<ushort,Component>(idCandidate,candidate);
                    var r=new InventoryRequest {ActorId=1,Action=InventoryAction.Buy,Definition=pair.Key};
                    var q=QuoteAt(r,new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()),pair.Key),network.Inventories.LocalIdentity,clientRole ? peer.Inventories.Authority : network.Inventories.Authority,native.Player.transform.position);
                    var item=native.Definitions[pair.Key];
                    if(q.Code==InventoryResultCode.Reserved && q.MoneyDelta<0 && -q.MoneyDelta<=network.Inventories.SharedEconomy.Money && native.Weight+(float)GameAccess.Get(item,"item_weight")<=native.Capacity && ((int)GameAccess.Get(item,"max_amount")<=0 || native.Amount(pair.Key)<(int)GameAccess.Get(item,"max_amount"))) {button=pair.Value; expectedCost=-q.MoneyDelta; break;}
                }
                if(!button) throw new InvalidOperationException("No safe affordable supported native purchase.");
                GameAccess.Call(computer,"UsePC"); yield return new WaitForSecondsRealtime(1.2f);
                var shop=GameAccess.Get(button,"shopManagerScript") as Component;
                var websites=(GameObject[])GameAccess.Get(computer,"Websites"); int website=Array.FindIndex(websites,w=>w && (button.transform==w.transform || button.transform.IsChildOf(w.transform) || shop && (shop.transform==w.transform || shop.transform.IsChildOf(w.transform))));
                if(website<0) throw new InvalidOperationException("Selected shop button is not bound to this original computer website; native UI acceptance deferred.");
                Invoke(computer,"ActivateWebsite",website); shop.gameObject.SetActive(true); button.gameObject.SetActive(true); yield return null;
                Invoke(shop,"SelectedNew",button); ushort id=(ushort)(int)GameAccess.Get(button,"shop_item_ID");
                var authority=clientRole ? peer.Inventories.Authority : network.Inventories.Authority;
                int before=native.Amount(id),money=network.Inventories.SharedEconomy.Money; long committed=authority.Committed;
                int otherCount=authority.Get(peer.Inventories.LocalIdentity).Count(id);
                GameAccess.Call(shop,"Buy_Item"); yield return Await(()=>pending==null && network.Inventories.LocalState.Count(id)==before+1 && network.Inventories.SharedEconomy.Revision==authority.Economy.Revision && (int)GameAccess.Get(native.Player,"moneyAm")==authority.Economy.Money,"native purchase"); Observe();
                Check(native.Amount(id)==before+1 && network.Inventories.LocalState.Count(id)==before+1 && network.Inventories.SharedEconomy.Money==money-expectedCost && authority.Get(peer.Inventories.LocalIdentity).Count(id)==otherCount && authority.Committed==committed+1,"original shop Buy_Item commits one buyer-owned item and exact shared wallet charge role="+(clientRole ? "Client" : "Host"));
                shop.gameObject.SetActive(false);
                GameAccess.Call(computer,"StopUsing");
                yield return Await(()=>!GameAccess.Bool(computer,"standUp") && native.Player.GetComponent<CharacterController>().enabled && network.GameplayReady,"original computer stand-up completion");
                var box=boxes.FirstOrDefault(c=>c && c.gameObject.activeInHierarchy && (int)GameAccess.Get(c,"storage_type")==1);
                if(Hideout && box) {
                    var collider=box.GetComponent<Collider>(); if(!collider) throw new InvalidOperationException("Native locker raycast collider unavailable.");
                    var target=collider.bounds.center; var origin=target+box.transform.forward*1.5f; var aim=(target-origin).normalized;
                    native.Player.transform.position+=origin-fixtureCamera.position; var interactionPosition=native.Player.transform.position;
                    native.Inventory.gameObject.SetActive(true);
                    yield return Await(()=>!clientRole || peer.Telemetry.HasPose && peer.Telemetry.ReceiveAge(peer.Now)<1 && Vector3.Distance(new Vector3(peer.Telemetry.Latest.X,peer.Telemetry.Latest.Y,peer.Telemetry.Latest.Z),native.Player.transform.position)<.5f,"native storage pose");
                    fixtureCamera.rotation=Quaternion.LookRotation(aim); Invoke(native.Player,"OpenStorage",1); yield return null;
                    if(!storageBox) throw new InvalidOperationException("Native locker opening did not satisfy the original camera-ray gates.");
                    Component Select(bool stored) {var list=(IList)GameAccess.Get(native.Inventory,stored ? "buttons_storage" : "buttons"); return list.Cast<Component>().First(c=>c && (int)GameAccess.Get(c,"item_ID")==id && GameAccess.Bool(c,"is_in_storage")==stored);}
                    var owned=network.Inventories.LocalState.Items.First(i=>i.Definition==id);
                    Invoke(native.Inventory,"PickedItemNewSystem",native.Definitions[id],Select(false)); GameAccess.Call(native.Inventory,"MoveItemToStorage"); yield return Await(()=>pending==null && network.Inventories.SharedEconomy.Find(1,owned.Instance)!=null,"native deposit"); Observe();
                    Check(network.Inventories.SharedEconomy.Find(1,owned.Instance)!=null && native.Amount(id)==before && authority.InvariantViolations()==0,"original hideout locker deposit retains canonical instance without a second owner");
                    IEnumerator CounterpartTransfer(InventoryAction action) {
                        yield return Await(()=>peer.Inventories.SharedEconomy.Revision==authority.Economy.Revision && peer.Inventories.LocalState!=null,"counterpart storage revision");
                        peer.SendState(new PlayerTransform {X=interactionPosition.x,Y=interactionPosition.y,Z=interactionPosition.z});
                        yield return Await(()=>clientRole || network.Telemetry.HasPose && network.Telemetry.ReceiveAge(network.Now)<1 && Vector3.Distance(new Vector3(network.Telemetry.Latest.X,network.Telemetry.Latest.Y,network.Telemetry.Latest.Z),interactionPosition)<.5f,"counterpart storage pose arrival");
                        var request=peer.Inventories.Request(action,instance:owned.Instance.Value,definition:id,container:1,x:storageOrigin.x,y:storageOrigin.y,z:storageOrigin.z,aimX:storageAim.x,aimY:storageAim.y,aimZ:storageAim.z);
                        if(request==null) throw new InvalidOperationException("Counterpart storage request unavailable.");
                        bool finished=false; InventoryResultCode code=InventoryResultCode.NotReady;
                        void Complete(InventoryTransactionResult result) {if(result.RequestId!=request.RequestId) return; if(result.Code==InventoryResultCode.Reserved) {var done=peer.Inventories.Complete(request,result.TransactionId,true); if(done!=null) Complete(done);} else {code=result.Code; finished=true;}}
                        peer.Inventories.ResultReceived+=Complete;
                        try {if(peer.Session.IsHost) Complete(peer.Inventories.HostBegin(request,_=>InventoryResultCode.Reserved)); else if(!peer.Inventories.SendRequest(request)) throw new InvalidOperationException("Counterpart storage send rejected."); yield return Await(()=>finished && network.Inventories.SharedEconomy.Revision==authority.Economy.Revision,"cross-player "+action); Check(code==InventoryResultCode.Success && authority.InvariantViolations()==0,"managed counterpart "+action+" transfers the same instance while native "+(clientRole ? "Client" : "Host")+" storage UI is open");}
                        finally {peer.Inventories.ResultReceived-=Complete;}
                    }
                    yield return CounterpartTransfer(InventoryAction.Withdraw);
                    Check(authority.Get(peer.Inventories.LocalIdentity).Find(owned.Instance)!=null && authority.Get(network.Inventories.LocalIdentity).Find(owned.Instance)==null && authority.Economy.Find(1,owned.Instance)==null,"cross-player withdrawal has exactly one canonical owner");
                    yield return CounterpartTransfer(InventoryAction.Deposit);
                    Invoke(native.Inventory,"OpenStorage",1); yield return null;
                    Invoke(native.Inventory,"PickedItemNewSystem",native.Definitions[id],Select(true)); GameAccess.Call(native.Inventory,"MoveItemToStorage"); yield return Await(()=>pending==null && network.Inventories.SharedEconomy.Find(1,owned.Instance)==null,"native withdrawal"); Observe();
                    Check(network.Inventories.SharedEconomy.Find(1,owned.Instance)==null && native.Amount(id)==before+1 && authority.InvariantViolations()==0,"original hideout locker withdrawal restores exactly one personal unit");
                    GameAccess.Call(native.Inventory,"CloseStorage"); GameAccess.Call(native.Player,"DisableInventory"); yield return new WaitForSecondsRealtime(.4f);
                } else log("[TS2MP] NATIVE ECONOMY STORAGE DEFERRED: native hideout locker unavailable in current scene.");
                var seller=sellers.FirstOrDefault(c=>c && c.gameObject.activeInHierarchy && (GameAccess.Bool(c,"pawnGuy") || GameAccess.Bool(c,"houseThing")));
                var sellItem=native.Definitions.Values.FirstOrDefault(i=>!GameAccess.Bool(i,"cant_sell") && native.Amount((ushort)(int)GameAccess.Get(i,"item_ID"))>0);
                if(seller && sellItem) {
                    native.Player.transform.position=seller.transform.position;
                    yield return Await(()=>!clientRole || peer.Telemetry.HasPose && Vector3.Distance(new Vector3(peer.Telemetry.Latest.X,peer.Telemetry.Latest.Y,peer.Telemetry.Latest.Z),seller.transform.position)<.5f,"native seller pose");
                    Invoke(native.Player,"Pawn_Shop_Activate",GameAccess.Bool(seller,"houseThing") ? .9f : 1f); yield return null;
                    ushort soldId=(ushort)(int)GameAccess.Get(sellItem,"item_ID"); var itemButton=((IList)GameAccess.Get(native.Inventory,"buttons")).Cast<Component>().First(c=>c && (int)GameAccess.Get(c,"item_ID")==soldId);
                    Invoke(native.Inventory,"PickedItemNewSystem",sellItem,itemButton); int amount=native.Amount(soldId); money=network.Inventories.SharedEconomy.Money;
                    Invoke(native.Inventory,"Sell_Item_NewSystem",false); yield return Await(()=>pending==null && network.Inventories.LocalState.Count(soldId)==amount-1,"native sale"); Observe();
                    Check(native.Amount(soldId)==amount-1 && network.Inventories.LocalState.Count(soldId)==amount-1 && network.Inventories.SharedEconomy.Money>money && authority.InvariantViolations()==0,"original sell UI removes owned item and credits the shared wallet once");
                    GameAccess.Call(native.Player,"DisableInventory"); yield return null;
                } else log("[TS2MP] NATIVE ECONOMY SALE DEFERRED: no native sale venue/owned supported sale item.");
                if(!clientRole) fixtureTime=network.LastWorldTime;
                log("[TS2MP] NATIVE ECONOMY COMPLETE role="+(clientRole ? "Client" : "Host")+" checks="+checks+"; genuine native UI with one managed counterpart; independent Host game/profile validation deferred");
            } finally {if(computer && GameAccess.Bool(computer,"PC_on")) GameAccess.Call(computer,"StopUsing"); if(native.Refresh()) {if(GameAccess.Bool(native.Inventory,"isStorage") || GameAccess.Bool(native.Inventory,"isShop")) GameAccess.Call(native.Player,"DisableInventory"); native.Player.transform.position=position;} if(fixtureCamera) fixtureCamera.rotation=cameraRotation; network.Disconnect("Native economy fixture cleanup."); peer?.Dispose();}
        }
    }
}
