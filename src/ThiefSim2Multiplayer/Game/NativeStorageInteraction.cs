using System;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class CooperativeEconomyController
    {
        private const float StorageReach=2.25f;
        private static readonly Type carBoxType=Type.GetType("CarBox, Assembly-CSharp",true);
        private static readonly string[] storageBlocks={"attacking","isCarrying","closeUp","hideHands","callIncoming","opening_slowly","pickingUp","isHiding"};
        private Component storageBox;
        private Vector3 storageOrigin,storageAim;
        private sealed class StorageOpeningState
        {
            internal Component Box;
            internal Vector3 Origin,Aim;
        }
        private static void StorageRemovalStarting(Component __instance,out int[] __state)
        {
            __state=null; var c=current;
            if(c==null || !c.Cooperative || !c.network.Session.IsHost || !c.native.Ready || c.executing) return;
            int id=(int)GameAccess.Get(__instance,"item_ID");
            if(id<0 || id>=InventoryLimits.MaxDefinitions || !c.native.Definitions.TryGetValue((ushort)id,out var definition) || definition!=__instance) return;
            __state=new[] {(int)GameAccess.Get(__instance,"item_amount_storage"),(int)GameAccess.Get(__instance,"item_amount_locker")};
        }
        private static void StorageRemoved(Component __instance,int[] __state)
        {
            var c=current; if(__state==null || c==null || !c.Cooperative || !c.network.Session.IsHost) return;
            ushort id=(ushort)(int)GameAccess.Get(__instance,"item_ID");
            for(byte container=0;container<2;container++) {
                int removed=__state[container]-(int)GameAccess.Get(__instance,container==0 ? "item_amount_storage" : "item_amount_locker");
                if(removed>0) c.network.Inventories.Authority.ObserveNativeStorageRemoval(container,id,removed);
            }
        }
        private static Component RayBox(Component player,Vector3 origin,Vector3 aim)
        {
            SharedVehicleController.RefreshStorageTags();
            if(!player || !Physics.Raycast(origin,aim,out var hit,StorageReach,(LayerMask)GameAccess.Get(player,"mask"))) return null;
            var box=hit.transform.GetComponent(carBoxType);
            return box && box.gameObject.activeInHierarchy && box.CompareTag("CarBox") && SharedVehicleController.StorageBoxAvailable(box) ? box : null;
        }
        private static Component NativeStorageTarget(Component player)
        {
            if(!player || Time.timeScale<=0) return null;
            var inv=GameAccess.Get(player,"invScript") as Component;
            var motor=GameAccess.Get(player,"PMB") as Behaviour; var look=GameAccess.Get(player,"PCB") as Behaviour;
            var cam=GameAccess.Get(player,"cam") as Transform;
            bool passenger=SharedVehicleController.Current?.PassengerStorageControls(player)==true;
            if(!inv || !motor || !look || !cam || (!passenger && (!motor.enabled || !look.enabled || !GameAccess.Bool(player,"canInteract"))) ||
                GameAccess.Bool(motor,"isRunning") || NativeBooleanMember.Read(GameAccess.Get(player,"radial_menu"),"RadialMenuActive") ||
                GameAccess.Bool(inv,"isShop") || (GameAccess.Get(inv,"PauseObj") as GameObject)?.activeInHierarchy==true ||
                GameAccess.Bool(GameAccess.Get(player,"pickpocket_Manager"),"isOn")) return null;
            foreach(string field in storageBlocks) if(GameAccess.Bool(player,field)) return null;
            return RayBox(player,cam.position,cam.forward);
        }
        private static bool StorageOpening(Component __instance,int __0,out StorageOpeningState __state)
        {
            __state=null; var c=current;
            if(c==null || !c.Cooperative) return true;
            var box=NativeStorageTarget(__instance);
            if(!box || (int)GameAccess.Get(box,"storage_type")!=__0) return false;
            var cam=(Transform)GameAccess.Get(__instance,"cam");
            __state=new StorageOpeningState {Box=box,Origin=cam.position,Aim=cam.forward};
            return true;
        }
        private static void StorageOpened(StorageOpeningState __state)
        {
            var c=current;
            if(__state==null || c==null || !c.native.Refresh() || !GameAccess.Bool(c.native.Inventory,"isStorage")) return;
            c.storageBox=__state.Box; c.storageOrigin=__state.Origin; c.storageAim=__state.Aim;
        }
        internal static bool PassengerStorage(Component player,bool open)
        {
            var c=current;
            if(c==null || !c.Cooperative || SharedVehicleController.Current?.PassengerStorageControls(player)!=true) return false;
            var box=NativeStorageTarget(player); if(!box) return false;
            if(open) player.GetType().GetMethod("OpenStorage").Invoke(player,new object[] {(int)GameAccess.Get(box,"storage_type")});
            return true;
        }
        private bool StorageContextValid(byte container) => storageBox && storageBox.gameObject.activeInHierarchy &&
            (int)GameAccess.Get(storageBox,"storage_type")==container && native.Ready && GameAccess.Bool(native.Inventory,"isStorage") &&
            native.Inventory.gameObject.activeInHierarchy && SharedVehicleController.StorageBoxAvailable(storageBox);
        private void ObserveStorageContext()
        {
            if(!storageBox) return;
            if(!GameAccess.Bool(native.Inventory,"isStorage") || !native.Inventory.gameObject.activeInHierarchy) {storageBox=null; return;}
            // Vanilla pauses this UI. A second actor can start the engine in co-op;
            // retire the original UI before permitting any further storage action.
            if(!SharedVehicleController.StorageBoxAvailable(storageBox) && pending==null) {
                GameAccess.Call(native.Player,"DisableInventory"); storageBox=null;
            }
        }
        private void CloseOwnedStorage()
        {
            if(storageBox && native.Ready && GameAccess.Bool(native.Inventory,"isStorage")) GameAccess.Call(native.Player,"DisableInventory");
            storageBox=null;
        }
        private EconomyQuote QuoteStorage(InventoryRequest r,Vector3 actorPosition)
        {
            var origin=new Vector3(r.X,r.Y,r.Z); var aim=new Vector3(r.AimX,r.AimY,r.AimZ);
            // Bounds the claimed camera to the authenticated native actor. The
            // separate 2.25 m ray remains the actual interaction-distance check.
            if((origin-actorPosition).sqrMagnitude>6.25f) return Reject(InventoryResultCode.OutOfRange);
            var box=RayBox(native.Player,origin,aim);
            if(!box || (int)GameAccess.Get(box,"storage_type")!=r.Container) return Reject(InventoryResultCode.OutOfRange);
            return new EconomyQuote();
        }
    }
}
