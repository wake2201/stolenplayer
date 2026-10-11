using System;
using System.Linq;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

internal static class Gameplay067Tests
{
    internal static void Run(Action<bool,string> check)
    {
        var travel=new NetworkSceneDescriptor("MadisonSt",0,false,1,2);
        var message=new SceneMessage {Type=SceneProtocol.Announce,SessionId=10,TransitionId=2,Epoch=3,Target=travel};
        check(SceneProtocol.TryDecode(SceneProtocol.Encode(message),out var decoded) && decoded.Target.FirstDriver==1 && decoded.Target.FirstPassenger==2,"0.6.7 travel preserves confirmed driver/rear identities in destination descriptor");
        var continuity=new SceneContinuity(); continuity.Begin(2,3,travel); continuity.LocalReady(travel.SceneKey); continuity.PeerReady(2,3,travel.SceneKey); continuity.Ready();
        bool invalid=false; try {new NetworkSceneDescriptor("MadisonSt",0,false,2,2);} catch(ArgumentException) {invalid=true;}
        check(invalid && !continuity.Target.HasTravelOccupants && !continuity.PeerReady(1,2,travel.SceneKey),"0.6.7 travel rejects duplicate/stale seats and clears travel-only roles for late join");
        var left=new PlayerTransform {PlayerId=2,SceneEpoch=1,Sequence=1,Tick=1000,State=new PlayerReplicationState {Known=SampledFields.Lean,LeanOffset=-.5f,LeanRoll=8}};
        var right=left; right.Sequence=2; right.Tick=1100; right.X=2; right.State.LeanOffset=.5f;right.State.LeanRoll=-8;
        var bytes=new byte[Protocol.StateBytes]; Protocol.WriteState(left,bytes,0);
        check(Protocol.TryReadState(bytes,0,bytes.Length,out var pose) && pose.State.LeanOffset==-.5f && pose.State.LeanRoll==8,"0.6.7 lean codec retains native offset and roll without changing local input");
        var timeline=new TransformBuffer();timeline.Add(left,1);timeline.Add(right,1);
        check(timeline.TrySample(1.05,.1,out pose) && Math.Abs(pose.X-1)<.001 && Math.Abs(pose.State.LeanOffset)<.001 && Math.Abs(pose.State.LeanRoll)<.001,"0.6.7 coalesced packet arrivals retain smooth sender-spaced motion and lean");
        right.State.LeanRoll=float.NaN;
        check(!Protocol.ValidState(right.State) && NetworkManager.ValidateHandshake(new Message {ProtocolVersion=9},10,"",false).Contains("protocol"),"0.6.7 rejects invalid lean and incompatible protocol 9 peers");
        var motion=new DoorMotionState {Opening=true,Slow=true,Loading=true,Progress=.35f,Qw=1,Y=2};
        var door=new DoorState {Open=true,Enabled=true,Tag=InteractionTag.Door,HasMotion=true,Motion=motion};
        var window=new WindowState {Open=true,Enabled=true,Tag=InteractionTag.Door,HasMotion=true,Motion=motion};
        check(DoorState.TryDecode(door.Encode(),out var readDoor) && WindowState.TryDecode(window.Encode(),out var readWindow) && readDoor.Motion.Slow && readWindow.Motion.Progress==.35f,"0.6.7 door/window intermediate native movement survives state replication");
        var corrupt=door.Encode();Array.Copy(BitConverter.GetBytes(float.NaN),0,corrupt,4,4);
        check(!DoorState.TryDecode(corrupt,out _) && InteractionProtocol.Valid(new InteractionRequest {SessionId=10,SceneEpoch=1,RequestId=1,ActorId=2,Entity=new EntityRef(new NetworkEntityId(EntityScope.Static,1),1),ExpectedRevision=1,Kind=InteractionKind.BreachComplete}),"0.6.7 malformed door motion rejected and reliable crowbar completion supported");
        CashAndKeys(check);
    }
    private static void CashAndKeys(Action<bool,string> check)
    {
        var world=new NetworkEntityRegistry(true,1);var a=new PersistentCoopPlayerId(Guid.NewGuid());var b=new PersistentCoopPlayerId(Guid.NewGuid());
        var authority=new InventoryAuthority(world);authority.Configure(new[] {new ItemDefinitionRule(12,0,1,false)});
        authority.Initialize(new InventoryState(a,1,15,Array.Empty<InventoryItemState>()));authority.Initialize(new InventoryState(b,1,15,Array.Empty<InventoryItemState>()));
        authority.InitializeEconomy(new EconomyState(1,100,20,20,Array.Empty<InventoryItemState>(),Array.Empty<InventoryItemState>()));
        EntityRef Loot(string name,ushort id,int money)=>world.RegisterStatic(new StaticEntityDescriptor("build","scene",name,"pickup-v1",0),new ManagedLootAdapter(new LootState {Item=new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()),id),MoneyValue=money}),new EntityOwnership(OwnershipKind.System));
        var cash=Loot("cash",451,250);var key=Loot("key",12,0);var cash2=Loot("cash2",451,77);
        InventoryRequest Request(byte actor,ulong id,EntityRef entity)=>new InventoryRequest {SessionId=10,SceneEpoch=1,RequestId=id,ActorId=actor,Action=InventoryAction.Pickup,Entity=entity,ExpectedEntityRevision=world.Lookup(entity).State.Revision,ExpectedInventoryRevision=authority.Get(actor==1?a:b).Revision};
        ActorRef Actor(byte actor)=>new ActorRef(10,actor,actor==1?a:b);
        InventoryTransactionResult Begin(InventoryRequest r)=>authority.Begin(r,Actor(r.ActorId),1,true,0,_=>InventoryResultCode.Reserved);
        InventoryTransactionResult Complete(InventoryTransactionResult r,byte actor,bool observed=true)=>authority.Complete(new InventoryCompletion {SessionId=10,SceneEpoch=1,RequestId=r.RequestId,TransactionId=r.TransactionId,Observed=observed},Actor(actor),.7);
        var request=Request(2,1,cash);var reserved=Begin(request);
        check(reserved.Code==InventoryResultCode.Reserved && reserved.MoneyDelta==250 && Begin(Request(1,1,key)).Code==InventoryResultCode.InvalidState,"0.6.7 cash reserves exact native value and blocks overlapping wallet/item mutations safely");
        var completed=Complete(reserved,2);Begin(request);Complete(reserved,2);
        check(completed.Code==InventoryResultCode.Success && authority.Economy.Money==350 && authority.Get(b).ItemCount==0 && world.Lookup(cash).State.Tombstone,"0.6.7 Client cash grants shared wallet exactly once and never enters personal physical inventory");
        var keyReserved=Begin(Request(1,2,key));Complete(keyReserved,1);
        check(authority.Get(a).Count(12)==1 && authority.Get(b).Count(12)==0 && world.Lookup(key).State.Tombstone,"0.6.7 world key remains personal to its picker and is removed canonically");
        var abort=Begin(Request(1,3,cash2));Complete(abort,1,false);
        check(authority.Economy.Money==350 && !world.Lookup(cash2).State.Tombstone && authority.PendingCount==0,"0.6.7 failed native cash pickup retains wallet and world pickup");
        var host=Begin(Request(1,4,cash2));Complete(host,1);
        check(authority.Economy.Money==427 && authority.Get(a).Count(12)==1 && authority.InvariantViolations()==0,"0.6.7 Host cash commits exact amount while preserving existing personal keys");
    }
}
