using System;
using System.Linq;
using System.Collections.Generic;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

internal static class InventoryTests
{
    private sealed class FaultLoot : ILootAdapter, IDespawnPresentation
    {
        private readonly ManagedLootAdapter inner;
        internal bool Despawned, Released, ThrowDespawn;
        internal FaultLoot(InventoryItemState item) { inner = new ManagedLootAdapter(new LootState { Item = item }); }
        public EntityTypeId EntityType => EntityTypeId.Pickup;
        public bool IsAlive => inner.IsAlive;
        public byte[] CaptureState() => inner.CaptureState();
        public bool ValidateState(byte[] bytes) => inner.ValidateState(bytes);
        public void ApplyState(byte[] bytes) => inner.ApplyState(bytes);
        public void ApplyDespawn() { Despawned = true; if (ThrowDespawn) throw new InvalidOperationException("Presentation failure fixture"); }
        public void Release() { Released = true; inner.Release(); }
    }
    private sealed class Fixture
    {
        internal readonly NetworkEntityRegistry World = new NetworkEntityRegistry(true, 1);
        internal readonly PersistentCoopPlayerId A = new PersistentCoopPlayerId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        internal readonly PersistentCoopPlayerId B = new PersistentCoopPlayerId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        internal readonly InventoryAuthority Authority;
        internal EntityRef Loot, Other;
        internal Fixture(float capacity = 15, int max = 0)
        {
            Authority = new InventoryAuthority(World); Authority.Configure(new[] { new ItemDefinitionRule(0, .1f, max, true), new ItemDefinitionRule(12, 2, 0, true), new ItemDefinitionRule(3, .1f, 0, false) });
            Authority.Initialize(new InventoryState(A, 1, capacity, new InventoryItemState[0])); Authority.Initialize(new InventoryState(B, 1, capacity, new InventoryItemState[0]));
            Loot = World.RegisterStatic(new StaticEntityDescriptor("build", "scene", "loot[0]", "pickup-v1", 0), Adapter(0), new EntityOwnership(OwnershipKind.System));
            Other = World.RegisterStatic(new StaticEntityDescriptor("build", "scene", "loot[1]", "pickup-v1", 0), Adapter(0), new EntityOwnership(OwnershipKind.System));
        }
        internal ManagedLootAdapter Adapter(ushort definition) => new ManagedLootAdapter(new LootState { Item = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), definition), X = 1, Y = 1, Z = 1 });
        internal ActorRef Actor(byte id, ulong session = 10) => new ActorRef(session, id, id == 1 ? A : B);
        internal InventoryRequest Pickup(byte actor = 1, ulong request = 1, EntityRef? entity = null)
        { var target = entity ?? Loot; return new InventoryRequest { SessionId = 10, SceneEpoch = 1, RequestId = request, ActorId = actor, Action = InventoryAction.Pickup, Entity = target, ExpectedEntityRevision = World.Lookup(target).State.Revision, ExpectedInventoryRevision = Authority.Get(actor == 1 ? A : B).Revision }; }
        internal InventoryTransactionResult Begin(InventoryRequest r, double now = 0) => Authority.Begin(r, Actor(r.ActorId), 1, true, now, _ => InventoryResultCode.Reserved);
        internal InventoryTransactionResult Complete(InventoryTransactionResult result, byte actor = 1, bool observed = true, double now = .7, Func<InventoryTransaction, ILootAdapter> spawn = null)
        { return Authority.Complete(new InventoryCompletion { SessionId = 10, SceneEpoch = 1, RequestId = result.RequestId, TransactionId = result.TransactionId, Observed = observed }, Actor(actor), now, spawn); }
        internal InventoryRequest ItemAction(InventoryAction action, byte actor, ulong request, Guid? instance = null)
        { var inventory = Authority.Get(actor == 1 ? A : B); return new InventoryRequest { SessionId = 10, SceneEpoch = 1, ActorId = actor, RequestId = request, Action = action, ItemInstance = instance ?? inventory.Items.First().Instance.Value, ExpectedInventoryRevision = inventory.Revision }; }
    }
    internal static void Economy(Action<bool,string> check)
    {
        NativeHostBridges(check);
        var f=new Fixture();
        f.Authority.InitializeEconomy(new EconomyState(1,1000,2,20,new InventoryItemState[0],new InventoryItemState[0]));
        f.Authority.QuoteEconomy=(r,i,o)=>new EconomyQuote {MoneyDelta=r.Action==InventoryAction.Buy ? -500 : r.Action==InventoryAction.Sell ? 800 : 0};
        ulong a=0,b=0;
        InventoryRequest Request(InventoryAction action,byte actor,Guid instance=default(Guid),byte container=0)=>new InventoryRequest {SessionId=10,SceneEpoch=1,RequestId=actor==1 ? ++a : ++b,ActorId=actor,Action=action,ExpectedInventoryRevision=f.Authority.Get(actor==1 ? f.A : f.B).Revision,ExpectedEconomyRevision=f.Authority.Economy.Revision,Definition=12,ItemInstance=instance,Container=container,AimZ=action>=InventoryAction.Deposit ? 1 : 0};
        var buy=Request(InventoryAction.Buy,2); var reserve=f.Begin(buy);
        check(InventoryProtocol.TryDecode(InventoryProtocol.Encode(buy),out var parsed) && parsed.Definition==12 && parsed.ExpectedEconomyRevision==1 && reserve.MoneyDelta==-500 && f.Complete(reserve,2).Code==InventoryResultCode.Success && f.Authority.Economy.Money==500 && f.Authority.Get(f.B).Count(12)==1 && f.Authority.Get(f.A).Count(12)==0,"Client purchase commits Host quote once into buyer inventory only");
        f.Begin(buy); f.Complete(reserve,2);
        check(f.Authority.Economy.Money==500 && f.Authority.Get(f.B).Count(12)==1,"duplicate purchase request/completion cannot double charge or grant");
        var owned=f.Authority.Get(f.B).Items.Single(); var selling=f.Begin(Request(InventoryAction.Sell,2,owned.Instance.Value)); f.Complete(selling,2); f.Complete(selling,2);
        check(f.Authority.Economy.Money==1300 && f.Authority.Get(f.B).ItemCount==0 && f.Begin(Request(InventoryAction.Sell,1,owned.Instance.Value)).Code==InventoryResultCode.NotOwner,"sale pays canonical wallet once and rejects another player's item");
        var purchased=f.Begin(Request(InventoryAction.Buy,1)); f.Complete(purchased); owned=f.Authority.Get(f.A).Items.Single();
        var deposit=f.Begin(Request(InventoryAction.Deposit,1,owned.Instance.Value)); f.Complete(deposit); var withdrawal=Request(InventoryAction.Withdraw,2,owned.Instance.Value); var take=f.Begin(withdrawal); var race=f.Begin(Request(InventoryAction.Withdraw,1,owned.Instance.Value)); f.Complete(take,2);
        check(race.Code==InventoryResultCode.InvalidState && f.Authority.Get(f.B).Find(owned.Instance)!=null && f.Authority.Get(f.A).ItemCount==0 && f.Authority.Economy.Items(0).Length==0 && f.Authority.InvariantViolations()==0,"shared storage cross-player transfer preserves instance and rejects simultaneous withdrawal");
        var abort=f.Begin(Request(InventoryAction.Deposit,2,owned.Instance.Value)); f.Complete(abort,2,false);
        check(f.Authority.Get(f.B).Find(owned.Instance)!=null && f.Authority.Economy.Items(0).Length==0 && f.Authority.Economy.Money==800,"failed native transfer retains original ownership and money");
        var pending=f.Begin(Request(InventoryAction.Buy,1)); var insufficient=f.Begin(Request(InventoryAction.Buy,2)); f.Complete(pending);
        check(insufficient.Code==InventoryResultCode.InsufficientFunds && f.Authority.Economy.Money==300,"concurrent spending reserves funds and never permits a negative wallet");
        var cap=f.Begin(Request(InventoryAction.Deposit,2,owned.Instance.Value)); f.Complete(cap,2); var other=f.Authority.Get(f.A).Items.Single();
        check(f.Begin(Request(InventoryAction.Deposit,1,other.Instance.Value)).Code==InventoryResultCode.InventoryFull,"native storage weight capacity includes committed and reserved units");
        var assembler=new InventorySnapshotAssembler(); EconomyState decoded=null;
        foreach(var packet in InventoryProtocol.EconomySnapshot(10,1,f.Authority.Economy)) {if(!assembler.AcceptPayload(packet,10,1,0,InventoryProtocol.EconomySnapshotType,25,EconomyCodec.MaxBytes,out var payload,out var revision)) throw new Exception("economy chunk"); if(payload!=null) {EconomyCodec.Decode(payload,out decoded); if(decoded.Revision!=revision) throw new Exception("economy revision");}}
        check(decoded!=null && decoded.Money==300 && decoded.Items(0).Single().Instance.Equals(owned.Instance) && !assembler.AcceptPayload(InventoryProtocol.EconomySnapshot(10,1,f.Authority.Economy)[0],11,1,0,InventoryProtocol.EconomySnapshotType,25,EconomyCodec.MaxBytes,out _,out _) && f.Authority.InvariantViolations()==0,"bounded reconnect/scene economy baseline preserves shared IDs and rejects wrong session");
        var ray=Request(InventoryAction.Withdraw,1,owned.Instance.Value); ray.X=2; ray.Y=1.5f; ray.AimY=-.6f; ray.AimZ=.8f;
        check(InventoryProtocol.TryDecode(InventoryProtocol.Encode(ray),out parsed) && parsed.X==2 && parsed.Y==1.5f && parsed.AimY==-.6f && parsed.AimZ==.8f,"storage transaction carries the original camera ray without replacing item identity");
        ray.AimZ=0; check(!InventoryProtocol.Valid(ray),"storage transaction rejects a non-unit camera direction"); ray.AimZ=.8f;
        f.Authority.QuoteEconomy=(r,i,o)=>new EconomyQuote(); var engineChanged=f.Begin(ray);
        f.Authority.QuoteEconomy=(r,i,o)=>new EconomyQuote {Code=InventoryResultCode.OutOfRange};
        check(engineChanged.Code==InventoryResultCode.Reserved && f.Complete(engineChanged).Code==InventoryResultCode.Aborted && f.Authority.Economy.Find(0,owned.Instance)!=null && f.Authority.Get(f.A).Find(owned.Instance)==null && f.Authority.InvariantViolations()==0,"storage eligibility lost before completion aborts without changing canonical ownership");
        f.Authority.QuoteEconomy=(r,i,o)=>new EconomyQuote();
        var consumed=f.Begin(Request(InventoryAction.Withdraw,1,owned.Instance.Value));
        bool removed=f.Authority.ObserveNativeStorageRemoval(0,12,1);
        check(removed && consumed.Code==InventoryResultCode.Reserved && f.Complete(consumed).Code==InventoryResultCode.Aborted && f.Authority.Economy.Count(0,12)==0 && f.Authority.Get(f.A).Find(owned.Instance)==null && f.Authority.Get(f.B).Find(owned.Instance)==null && f.Authority.Economy.Money==300 && f.Authority.InvariantViolations()==0,"native storage consumption wins a reserved withdrawal without resurrecting the unit or changing personal ownership/wallet");
        ulong consumedRevision=f.Authority.Economy.Revision;
        check(!f.Authority.ObserveNativeStorageRemoval(0,12,1) && !f.Authority.ObserveNativeStorageRemoval(1,12,-1) && !f.Authority.ObserveNativeStorageRemoval(2,12,1) && f.Authority.Economy.Revision==consumedRevision,"repeated or invalid native consumption cannot mint storage units or advance state");
    }
    internal static void EconomyTransport(Action<bool,string> check) => Transport(check,false);
    private static void NativeHostBridges(Action<bool,string> check)
    {
        var f=new Fixture(); var item=new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()),12);
        f.Authority.InitializeEconomy(new EconomyState(1,1000,2,20,Array.Empty<InventoryItemState>(),new[] {item}));
        f.Authority.ObserveNativeHost(new InventoryState(f.A,2,15,new[] {item}),new EconomyState(2,900,2,20,Array.Empty<InventoryItemState>(),Array.Empty<InventoryItemState>()));
        check(f.Authority.Get(f.A).Find(item.Instance)!=null && f.Authority.Get(f.B).Revision==1 && f.Authority.Economy.Items(1).Length==0 && f.Authority.Economy.Money==900 && f.Authority.InvariantViolations()==0,"solo native Host storage/wallet changes retain instance and absent player's inventory");
        bool rejected=false; var reservation=f.Begin(f.Pickup(2));
        try {f.Authority.ObserveNativeHost(new InventoryState(f.A,3,15,new[] {item}),new EconomyState(3,800,2,20,Array.Empty<InventoryItemState>(),Array.Empty<InventoryItemState>()));} catch(InvalidOperationException) {rejected=true;}
        check(rejected && f.Authority.Economy.Money==900,"native Host observation cannot overwrite a pending cooperative transaction");
        f.Authority.Cancel(); var loot=((ManagedLootAdapter)f.World.GetBinding(f.Loot)).State.Item; int nativeCalls=0;
        bool stored=f.Authority.StoreNativeDrop(f.Loot,()=> {nativeCalls++; return true;}); bool duplicate=f.Authority.StoreNativeDrop(f.Loot,()=> {nativeCalls++; return true;});
        check(stored && !duplicate && nativeCalls==1 && f.Authority.Economy.Find(1,loot.Instance)!=null && f.World.Lookup(f.Loot).State.Tombstone && f.Authority.InvariantViolations()==0,"native loose-item vacuum moves the same instance exactly once to original locker storage");
    }
    internal static void Run(Action<bool, string> check)
    {
        Economy(check);
        var f = new Fixture();
        var id = NetworkItemInstanceId.FromEntity(10, 1, f.Loot);
        check(id.Equals(NetworkItemInstanceId.FromEntity(10, 1, f.Loot)), "physical identity derives deterministically from world origin/entity");
        check(!id.Equals(NetworkItemInstanceId.FromEntity(10, 1, f.Other)), "same definition distinct physical entities have distinct identity");
        check(!id.Equals(NetworkItemInstanceId.FromEntity(10, 2, f.Loot)) && !id.Equals(NetworkItemInstanceId.FromEntity(11, 1, f.Loot)), "item identity namespaces scene epoch and runtime origin");
        check(!id.Equals(NetworkItemInstanceId.FromEntity(10, 1, new EntityRef(f.Loot.Id, 2))), "generation distinguishes physical origins");
        var item = new InventoryItemState(id, 0);
        var state = new InventoryState(f.A, 4, 15, new[] { item }, id);
        check(InventoryCodec.TryDecode(InventoryCodec.Encode(state), out var decoded) && decoded.Owner.Equals(f.A) && decoded.Find(id).Definition == 0 && decoded.Equipped.Value.Equals(id), "inventory codec preserves physical ownership/equipment");
        check(InventoryCodec.Encode(state).Length == InventoryCodec.HeaderBytes + InventoryCodec.ItemBytes, "inventory snapshot explicit bounded semantic record");
        check(!state.NewerThan(state) && state.NewerThan(new InventoryState(f.A, 3, 15, new[] { item })) && !state.NewerThan(new InventoryState(f.B, 3, 15, new[] { item })), "inventory revision rejects old/equal/wrong owner state");
        check(state.Hash == decoded.Hash, "canonical inventory hash codec roundtrip");
        var second = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0);
        check(new InventoryState(f.A, 4, 15, new[] { item, second }).Hash == new InventoryState(f.A, 4, 15, new[] { second, item }).Hash, "canonical inventory hash independent of insertion order");
        CheckThrows(() => new InventoryState(f.A, 1, 15, new[] { item, item }), check, "duplicate physical instance in snapshot rejected");
        CheckThrows(() => new InventoryState(f.A, 1, 15, new[] { item }, second.Instance), check, "equipment cannot reference unowned instance");
        CheckThrows(() => new InventoryItemState(id, 452), check, "invalid definition ID rejected");
        CheckThrows(() => new NetworkItemInstanceId(Guid.Empty), check, "empty physical identity rejected");
        CheckThrows(() => new InventoryState(f.A, 1, float.NaN, new[] { item }), check, "NaN capacity rejected");
        CheckThrows(() => new InventoryState(f.A, 1, 15, Enumerable.Range(0, 129).Select(_ => new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0))), check, "inventory item hard bound");
        var request = f.Pickup(); byte[] wire = InventoryProtocol.Encode(request);
        check(wire.Length == InventoryProtocol.RequestBytes && InventoryProtocol.TryDecode(wire, out var parsed) && parsed.Entity.Equals(f.Loot), "pickup request codec");
        var reserved = f.Begin(request);
        check(reserved.Code == InventoryResultCode.Reserved && f.Authority.PendingCount == 1 && f.Authority.Reserved(f.Loot), "valid pickup reserves one physical item without granting inventory");
        check(f.Authority.Get(f.A).Items.Length == 0 && !f.World.Lookup(f.Loot).State.Tombstone, "method-return phase is pending world plus no inventory addition");
        check(f.Begin(request).TransactionId == reserved.TransactionId && f.Authority.PendingCount == 1, "duplicate request reuses reservation");
        check(f.Begin(f.Pickup(2)).Code == InventoryResultCode.EntityUnavailable, "two actors same-item race exactly one reservation");
        var committed = f.Complete(reserved);
        check(committed.Code == InventoryResultCode.Success && f.Authority.Get(f.A).Count(0) == 1 && f.Authority.Get(f.B).Count(0) == 0, "successful pickup transfers ownership into personal inventory only");
        check(f.World.Lookup(f.Loot).State.Tombstone && f.World.Lookup(f.Loot).State.Revision == committed.WorldRevision, "pickup commit produces C registry world tombstone");
        check(f.Authority.Get(f.A).Revision == 2 && f.Authority.PendingCount == 0, "settled pickup advances inventory revision and releases reservation");
        check(f.Begin(request).Code == InventoryResultCode.Success && f.Complete(reserved).Code == InventoryResultCode.Success && f.Authority.Get(f.A).Count(0) == 1 && f.World.TombstoneCount == 1, "duplicate request and duplicate completion grant exactly once");
        check(f.Authority.InvariantViolations() == 0, "world/inventory physical ownership invariant after pickup");
        check(InventoryProtocol.TryDecodeResult(InventoryProtocol.EncodeResult(committed), out var resultRead) && resultRead.TransactionId == committed.TransactionId && resultRead.InventoryRevision == 2, "pickup result codec preserves transaction/revisions");
        var completion = new InventoryCompletion { SessionId = 10, SceneEpoch = 1, RequestId = 1, TransactionId = reserved.TransactionId, Observed = true };
        check(InventoryProtocol.TryDecodeCompletion(InventoryProtocol.EncodeCompletion(completion), out var evidence) && evidence.Observed, "asynchronous completion codec");
        var drop = f.ItemAction(InventoryAction.Drop, 1, 2); check(InventoryProtocol.TryDecode(InventoryProtocol.Encode(drop), out parsed) && parsed.ItemInstance == drop.ItemInstance, "drop request identifies owned instance rather than definition");
        var dropping = f.Begin(drop);
        check(dropping.Code == InventoryResultCode.Reserved && f.Authority.Get(f.A).Count(0) == 1, "drop reservation retains ownership before execution evidence");
        var dropped = f.Complete(dropping, spawn: t => new ManagedLootAdapter(new LootState { Item = t.Item, X = 1, Y = 1, Z = 1 }));
        var dynamic = f.World.Capture().Single(r => r.Entity.Id.Scope == EntityScope.Dynamic);
        check(dropped.Code == InventoryResultCode.Success && f.Authority.Get(f.A).Count(0) == 0 && dynamic.Type == EntityTypeId.Pickup, "drop removes one owned instance and spawns one authoritative dynamic item");
        LootState.TryDecode(dynamic.Payload, out var loose);
        check(loose.Item.Instance.Value == drop.ItemInstance && dynamic.Entity.Generation == 1 && f.Authority.Get(f.A).Revision == 3, "drop preserves physical identity with C dynamic allocation/generation and new revision");
        check(f.Begin(drop).Code == InventoryResultCode.Success && f.Complete(dropping).Code == InventoryResultCode.Success && f.World.DynamicCount == 1, "duplicate drop packet/completion cannot spawn second world copy");
        var transfer = f.Begin(f.Pickup(2, 2, dynamic.Entity));
        check(f.Complete(transfer, 2).Code == InventoryResultCode.Success && f.Authority.Get(f.B).Find(loose.Item.Instance) != null && f.Authority.Get(f.A).Items.Length == 0, "drop then other actor pickup transfers original physical unit");
        check(f.Authority.InvariantViolations() == 0 && f.World.DynamicCount == 0, "transfer leaves no available world copy or duplicate ownership");
        check(f.Begin(f.ItemAction(InventoryAction.Drop, 1, 3, loose.Item.Instance.Value)).Code == InventoryResultCode.NotOwner, "fabricated/wrong-owner drop rejected");
        var equipped = f.Begin(f.ItemAction(InventoryAction.Equip, 2, 3));
        check(equipped.Code == InventoryResultCode.Success && f.Authority.Get(f.B).Equipped.Value.Equals(loose.Item.Instance), "equipment authority accepts personally owned instance");
        check(f.Begin(f.ItemAction(InventoryAction.Equip, 1, 4, loose.Item.Instance.Value)).Code == InventoryResultCode.NotOwner, "equipment authority rejects another actor's tool");
        var redrop = f.Begin(f.ItemAction(InventoryAction.Drop, 2, 4));
        f.Complete(redrop, 2, spawn: t => new ManagedLootAdapter(new LootState { Item = t.Item }));
        check(!f.Authority.Get(f.B).Equipped.HasValue, "dropping equipped item clears stale equipment reference");
        var g = new Fixture(); var ar = g.Begin(g.Pickup()); var br = g.Begin(g.Pickup(2, entity: g.Other));
        check(ar.Code == InventoryResultCode.Reserved && br.Code == InventoryResultCode.Reserved && g.Authority.PendingCount == 2, "different-item concurrency has independent item reservations");
        g.Complete(ar); g.Complete(br, 2);
        check(g.Authority.Get(g.A).Count(0) == 1 && g.Authority.Get(g.B).Count(0) == 1 && g.Authority.InvariantViolations() == 0, "different actors can independently commit different physical units");
        g = new Fixture(); ar = g.Begin(g.Pickup()); g.Authority.Tick(5);
        check(g.Authority.PendingCount == 0 && !g.World.Lookup(g.Loot).State.Tombstone && g.Authority.Get(g.A).Items.Length == 0, "reservation timeout aborts and releases availability");
        check(g.Begin(g.Pickup(2)).Code == InventoryResultCode.Reserved, "timed-out item can be claimed by another actor");
        g.Authority.Cancel(g.B);
        check(g.Authority.PendingCount == 0 && !g.World.Lookup(g.Loot).State.Tombstone, "disconnect during reservation deterministically aborts");
        check(g.Complete(ar).Code == InventoryResultCode.Aborted && g.Authority.Get(g.A).Items.Length == 0, "late completion cannot revive an aborted transaction");
        g = new Fixture(); ar = g.Begin(g.Pickup()); check(g.Complete(ar, observed: false).Code == InventoryResultCode.Aborted && !g.World.Lookup(g.Loot).State.Tombstone, "native pickup rejection aborts without ownership grant");
        g = new Fixture(); ar = g.Begin(g.Pickup()); check(g.Complete(ar, 2).Code == InventoryResultCode.WrongActor && g.Authority.PendingCount == 1, "wrong-actor completion cannot consume another reservation");
        var collision = new InventoryCompletion { SessionId = 10, SceneEpoch = 1, RequestId = 2, TransactionId = ar.TransactionId, Observed = true };
        check(g.Authority.Complete(collision, g.Actor(1), 1).Code == InventoryResultCode.WrongActor, "transaction ID collision with another request rejected");
        g.Authority.Cancel(); check(g.Authority.PendingCount == 0, "scene cleanup releases all pending managed transactions");
        g = new Fixture(); request = g.Pickup(); request.ExpectedEntityRevision++;
        check(g.Begin(request).Code == InventoryResultCode.StaleRevision, "stale/future entity revision rejected");
        request = g.Pickup(request: 2); request.ExpectedInventoryRevision++;
        check(g.Begin(request).Code == InventoryResultCode.StaleRevision, "stale/future inventory revision rejected");
        request = g.Pickup(request: 3); request.Entity = new EntityRef(request.Entity.Id, 2);
        check(g.Begin(request).Code == InventoryResultCode.EntityUnavailable, "wrong generation rejected");
        request = g.Pickup(request: 4); request.SceneEpoch = 2;
        check(g.Begin(request).Code == InventoryResultCode.WrongEpoch, "wrong epoch rejected");
        request = g.Pickup(request: 5); request.SessionId = 11;
        check(g.Begin(request).Code == InventoryResultCode.WrongSession, "wrong session rejected");
        request = g.Pickup(request: 6); check(g.Authority.Begin(request, g.Actor(2), 1, true, 0, null).Code == InventoryResultCode.WrongActor, "authenticated connection actor overrides packet actor");
        request = g.Pickup(request: 7); check(g.Authority.Begin(request, g.Actor(1), 1, false, 0, null).Code == InventoryResultCode.NotReady, "WorldReady required for remote action");
        request = g.Pickup(request: 8); check(g.Authority.Begin(request, g.Actor(1), 1, true, 0, _ => InventoryResultCode.OutOfRange).Code == InventoryResultCode.OutOfRange, "target range rejection happens before reservation");
        g = new Fixture(0); var heavy = g.World.Spawn(new ManagedLootAdapter(new LootState { Item = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 12) }), new EntityOwnership(OwnershipKind.System));
        ar = g.Begin(g.Pickup(entity: heavy)); g.Complete(ar);
        var heavy2 = g.World.Spawn(g.Adapter(12), new EntityOwnership(OwnershipKind.System)); ar = g.Begin(g.Pickup(request: 2, entity: heavy2)); g.Complete(ar);
        var heavy3 = g.World.Spawn(g.Adapter(12), new EntityOwnership(OwnershipKind.System));
        check(g.Begin(g.Pickup(request: 3, entity: heavy3)).Code == InventoryResultCode.InventoryFull, "verified maxWeight plus five capacity rule rejects overweight item");
        g = new Fixture(max: 1); ar = g.Begin(g.Pickup());
        check(g.Begin(g.Pickup(request: 2, entity: g.Other)).Code == InventoryResultCode.InventoryFull, "max_amount accounts for outstanding reservations");
        g = new Fixture(); ar = g.Begin(g.Pickup()); g.Complete(ar); ar = g.Begin(g.Pickup(request: 2, entity: g.Other)); g.Complete(ar);
        check(g.Authority.Get(g.A).Count(0) == 2 && g.Authority.Get(g.A).Items.Select(i => i.Instance).Distinct().Count() == 2, "native aggregate quantity two retains two distinct physical instances");
        var nondrop = g.World.Spawn(g.Adapter(3), new EntityOwnership(OwnershipKind.System)); ar = g.Begin(g.Pickup(request: 3, entity: nondrop)); g.Complete(ar);
        check(g.Begin(g.ItemAction(InventoryAction.Drop, 1, 4, g.Authority.Get(g.A).Items.Single(i => i.Definition == 3).Instance.Value)).Code == InventoryResultCode.UnsupportedItem, "definition without supported native drop capability rejected");
        var unknown = g.World.Spawn(g.Adapter(1), new EntityOwnership(OwnershipKind.System));
        check(g.Begin(g.Pickup(request: 5, entity: unknown)).Code == InventoryResultCode.UnsupportedItem, "unsupported item definition rejected");
        var abortDrop = g.Begin(g.ItemAction(InventoryAction.Drop, 1, 6, g.Authority.Get(g.A).Items.First(i => i.Definition == 0).Instance.Value));
        check(g.Complete(abortDrop, spawn: _ => throw new InvalidOperationException()).Code == InventoryResultCode.Aborted && g.Authority.Get(g.A).Count(0) == 2, "drop spawn exception preserves personal ownership");
        var badDrop = g.Begin(g.ItemAction(InventoryAction.Drop, 1, 7, g.Authority.Get(g.A).Items.First(i => i.Definition == 0).Instance.Value));
        check(g.Complete(badDrop, spawn: _ => g.Adapter(0)).Code == InventoryResultCode.Aborted && g.Authority.Get(g.A).Count(0) == 2, "drop adapter wrong physical identity cannot commit");
        var remove = g.ItemAction(InventoryAction.NativeRemoval, 1, 8, g.Authority.Get(g.A).Items.First(i => i.Definition == 0).Instance.Value);
        check(g.Begin(remove).Code == InventoryResultCode.Success && g.Authority.Get(g.A).Count(0) == 1, "observed legitimate native consumption removes ownership without world spawn");
        SnapshotChecks(check);
        CodecChecks(check, state, wire, committed, completion);
        Stress(check);
        Bounds(check);
        FailureChecks(check);
        Transport(check, false); Transport(check, true);
    }
    private static void FailureChecks(Action<bool, string> check)
    {
        var f = new Fixture();
        var bad = new FaultLoot(new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0)) { ThrowDespawn = true };
        var target = f.World.RegisterStatic(new StaticEntityDescriptor("build", "scene", "fault[0]", "pickup-v1", 0), bad, new EntityOwnership(OwnershipKind.System));
        var started = f.Begin(f.Pickup(entity: target));
        check(f.Complete(started).Code == InventoryResultCode.Aborted && f.Authority.Get(f.A).Items.Length == 0 && f.Authority.PendingCount == 0 && !f.World.Lookup(target).State.Tombstone, "pickup presentation failure aborts without granting ownership");
        check(f.Authority.AdapterFailures == 1 && f.Authority.InvariantViolations() == 0, "pickup adapter failure remains bounded and observable");
        f = new Fixture(); started = f.Begin(f.Pickup()); f.Complete(started);
        var drop = f.Begin(f.ItemAction(InventoryAction.Drop, 1, 2));
        bad = new FaultLoot(new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0)) { ThrowDespawn = true };
        check(f.Complete(drop, spawn: _ => bad).Code == InventoryResultCode.Aborted && bad.Despawned && bad.Released && f.Authority.Get(f.A).Count(0) == 1 && f.World.DynamicCount == 0 && f.Authority.PendingCount == 0, "failed unregistered native drop discards provisional presentation even when cleanup throws");
        f = new Fixture(); started = f.Begin(f.Pickup());
        f.World.Changed += _ => throw new InvalidOperationException("Post-commit notification fixture");
        check(f.Complete(started).Code == InventoryResultCode.Success && f.World.Lookup(f.Loot).State.Tombstone && f.Authority.Get(f.A).Count(0) == 1 && f.Authority.InvariantViolations() == 0, "post-tombstone notification failure still settles exactly one owner");
        f = new Fixture(); started = f.Begin(f.Pickup()); f.Complete(started);
        drop = f.Begin(f.ItemAction(InventoryAction.Drop, 1, 2));
        f.World.Changed += _ => throw new InvalidOperationException("Post-spawn notification fixture");
        check(f.Complete(drop, spawn: t => new ManagedLootAdapter(new LootState { Item = t.Item })).Code == InventoryResultCode.Success && f.World.DynamicCount == 1 && f.Authority.Get(f.A).Items.Length == 0 && f.Authority.InvariantViolations() == 0, "post-spawn notification failure cannot leave inventory and world copies");
        check(f.Complete(drop).Code == InventoryResultCode.Success && f.World.DynamicCount == 1 && f.Authority.PendingCount == 0, "post-spawn failure duplicate completion stays idempotent");
        f = new Fixture(); started = f.Begin(f.Pickup()); int changes = 0, settlements = 0;
        f.Authority.Changed += _ => throw new InvalidOperationException("Inventory observer fixture");
        f.Authority.Changed += _ => changes++;
        f.Authority.Settled += (_, __) => throw new InvalidOperationException("Result observer fixture");
        f.Authority.Settled += (_, __) => settlements++;
        check(f.Complete(started).Code == InventoryResultCode.Success && changes == 1 && settlements == 1 && f.Authority.AdapterFailures == 2 && f.Authority.PendingCount == 0 && f.Authority.InvariantViolations() == 0, "inventory/result observer exceptions cannot strand a committed reservation or suppress remaining observers");
        check(f.Complete(started).Code == InventoryResultCode.Success && changes == 1 && settlements == 1 && f.Authority.Get(f.A).Items.Length == 1, "observer failure duplicate completion returns cached success without another inventory mutation");
    }
    private static void SnapshotChecks(Action<bool, string> check)
    {
        var f = new Fixture(); var seed = f.World.Capture(); ulong baseRevision = f.World.WorldRevision;
        var replica = new NetworkEntityRegistry(false, 1);
        foreach (var record in seed) replica.RegisterStatic(StaticEntityDescriptor.ParseCanonical(record.Descriptor), new ManagedLootAdapter(LootState.TryDecode(record.Payload, out var loot) ? loot : null), record.Ownership);
        var snapshot = new InitialSnapshot(10, 1, 1, baseRevision, seed);
        var backlog = new DeltaBacklog(); backlog.Begin(baseRevision); f.World.Changed += change => backlog.Add(change);
        var result = f.Begin(f.Pickup(2)); f.Complete(result, 2);
        check(replica.ApplyBaseline(snapshot.Entities, snapshot.BaseRevision, _ => null), "world baseline captured before pickup remains valid");
        foreach (var change in backlog.Drain(10)) replica.ApplyChange(1, change, _ => null);
        check(replica.Lookup(f.Loot).State.Tombstone && f.Authority.Get(f.B).Count(0) == 1 && replica.WorldRevision == f.World.WorldRevision, "baseline plus concurrent pickup backlog ends with absent world item and personal ownership");
        var inventory = f.Authority.Get(f.B); var chunks = InventoryProtocol.Snapshot(10, 1, inventory, f.World.WorldRevision); var assembler = new InventorySnapshotAssembler(); InventoryState received = null;
        foreach (var chunk in chunks) check(assembler.Accept(chunk, 10, 1, 0, out received), "owner inventory snapshot chunk accepted");
        check(received.Hash == inventory.Hash && assembler.CompletedWorldRevision == f.World.WorldRevision, "owner snapshot retains canonical hash and world revision dependency");
        f.Authority.Cancel(f.B);
        check(f.Authority.Get(f.B).Hash == inventory.Hash && InventoryCodec.TryDecode(InventoryCodec.Encode(f.Authority.Get(f.B)), out var reconnect) && reconnect.Hash == inventory.Hash, "reconnect restores retained persistent personal runtime state without pickup replay");
        var large = new InventoryState(f.A, 9, 15, Enumerable.Range(0, 128).Select(_ => new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0)));
        chunks = InventoryProtocol.Snapshot(10, 1, large); assembler = new InventorySnapshotAssembler();
        bool all = chunks.All(c => c.Length <= Protocol.MaxPacketBytes); foreach (var chunk in chunks) all &= assembler.Accept(chunk, 10, 1, 0, out received);
        check(all && received.Hash == large.Hash, "maximum inventory uses bounded reliable packets and exact reconstruction");
        assembler = new InventorySnapshotAssembler(); check(!assembler.Accept(chunks[1], 10, 1, 0, out _), "inventory snapshot rejects missing first chunk");
        check(!assembler.Accept(chunks[0], 11, 1, 0, out _), "inventory snapshot rejects wrong session");
        check(!assembler.Accept(chunks[0], 10, 2, 0, out _), "inventory snapshot rejects wrong epoch");
        assembler.Accept(chunks[0], 10, 1, 0, out _); check(!assembler.Accept(chunks[1], 10, 1, 6, out _), "bounded inventory snapshot assembly timeout");
    }
    private static void CodecChecks(Action<bool, string> check, InventoryState state, byte[] request, InventoryTransactionResult result, InventoryCompletion completion)
    {
        var inventory = InventoryCodec.Encode(state); var loot = new LootState { Item = state.Items[0] }.Encode();
        check(LootState.TryDecode(loot, out var loose) && loose.Item.Instance.Equals(state.Items[0].Instance), "loot physical identity codec");
        foreach (var payload in new[] { request, InventoryProtocol.EncodeResult(result), InventoryProtocol.EncodeCompletion(completion), inventory, loot })
        {
            bool rejected = true;
            for (int length = 0; length < payload.Length; length++) rejected &= !Decode(payload.Take(length).ToArray(), payload);
            rejected &= !Decode(payload.Concat(new byte[] { 0 }).ToArray(), payload);
            check(rejected, "all truncations and extra payload rejected for schema " + payload[0] + " length " + payload.Length);
        }
        var malformed = (byte[])inventory.Clone(); Array.Clear(malformed, 47, 16); check(!InventoryCodec.TryDecode(malformed, out _), "malformed empty inventory item identity rejected");
        malformed = (byte[])inventory.Clone(); BitConverter.GetBytes(-1).CopyTo(malformed, 65); check(!InventoryCodec.TryDecode(malformed, out _), "negative quantity rejected");
        malformed = (byte[])inventory.Clone(); BitConverter.GetBytes(int.MaxValue).CopyTo(malformed, 65); check(!InventoryCodec.TryDecode(malformed, out _), "giant quantity rejected");
        malformed = (byte[])request.Clone(); malformed[22] = 255; check(!InventoryProtocol.TryDecode(malformed, out _), "unknown inventory action enum rejected");
        malformed = (byte[])request.Clone(); malformed[23] = 255; check(!InventoryProtocol.TryDecode(malformed, out _), "invalid entity identity scope rejected");
        malformed = (byte[])request.Clone(); BitConverter.GetBytes(float.NaN).CopyTo(malformed, 68); check(!InventoryProtocol.TryDecode(malformed, out _), "NaN drop/request coordinates rejected");
        var random = new Random(5917); var assembler = new InventorySnapshotAssembler();
        for (int i = 0; i < 10000; i++)
        {
            var bytes = new byte[random.Next(0, 600)]; random.NextBytes(bytes);
            InventoryProtocol.TryDecode(bytes, out _); InventoryProtocol.TryDecodeResult(bytes, out _); InventoryProtocol.TryDecodeCompletion(bytes, out _); InventoryCodec.TryDecode(bytes, out _); LootState.TryDecode(bytes, out _); assembler.Accept(bytes, 10, 1, i * .001, out _);
        }
        check(true, "10,000 malformed/fuzzed inventory packets across all parsers cannot throw or allocate from network counts");
    }
    private static bool Decode(byte[] bytes, byte[] reference)
    {
        if (reference[0] == InventoryProtocol.RequestType) return InventoryProtocol.TryDecode(bytes, out _);
        if (reference[0] == InventoryProtocol.ResultType) return InventoryProtocol.TryDecodeResult(bytes, out _);
        if (reference[0] == InventoryProtocol.CompletionType) return InventoryProtocol.TryDecodeCompletion(bytes, out _);
        return reference.Length == 52 ? LootState.TryDecode(bytes, out _) : InventoryCodec.TryDecode(bytes, out _);
    }
    private static void Stress(Action<bool, string> check)
    {
        var f = new Fixture(); var initial = f.Begin(f.Pickup());
        for (int i = 0; i < 200; i++) { f.Begin(f.Pickup()); f.Begin(f.Pickup(2)); }
        f.Complete(initial);
        for (int i = 0; i < 200; i++) f.Complete(initial);
        check(f.Authority.Get(f.A).Items.Length == 1 && f.Authority.Get(f.B).Items.Length == 0 && f.World.TombstoneCount == 1 && f.Authority.PendingCount == 0 && f.Authority.InvariantViolations() == 0, "400 duplicate/racing requests plus 200 delayed duplicate completions prove exactly-one ownership");
        ulong a = 1, b = 1; byte owner = 1; bool clean = true;
        for (int cycle = 0; cycle < 32; cycle++)
        {
            var drop = f.Begin(f.ItemAction(InventoryAction.Drop, owner, owner == 1 ? ++a : ++b), cycle + 1);
            var dropped = f.Complete(drop, owner, now: cycle + 1.1, spawn: t => new ManagedLootAdapter(new LootState { Item = t.Item }));
            var entity = f.World.Capture().Single(r => !r.Tombstone && r.Entity.Id.Scope == EntityScope.Dynamic).Entity;
            owner = owner == 1 ? (byte)2 : (byte)1;
            var pickup = f.Begin(f.Pickup(owner, owner == 1 ? ++a : ++b, entity), cycle + 1.2);
            var got = f.Complete(pickup, owner, now: cycle + 1.9);
            clean &= dropped.Code == InventoryResultCode.Success && got.Code == InventoryResultCode.Success && f.Authority.InvariantViolations() == 0 && f.World.DynamicCount == 0 && f.Authority.PendingCount == 0 && f.Authority.Get(f.A).Items.Length + f.Authority.Get(f.B).Items.Length == 1;
        }
        check(clean && f.World.TombstoneCount == 33, "32 alternating pickup/drop transfers preserve identity, quantities, invariants and bounded pending state");
    }
    private static void CheckThrows(Action action, Action<bool, string> check, string name)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } check(rejected, name); }
    private static void Bounds(Action<bool, string> check)
    {
        var f = new Fixture();
        var adapter = (ManagedLootAdapter)f.World.GetBinding(f.Loot); adapter.State.Available = false; f.World.CommitState(f.Loot);
        check(f.Begin(f.Pickup()).Code == InventoryResultCode.EntityUnavailable, "inactive authored loot is unavailable to authoritative pickup");
        var malformed = adapter.CaptureState(); malformed[51] = 2;
        check(!LootState.TryDecode(malformed, out _), "noncanonical loot availability boolean rejected");
        f = new Fixture(); bool bounded = true;
        for (ulong i = 1; i <= 33; i++)
        { var request = f.Pickup(request: i); request.ExpectedInventoryRevision = 100; var result = f.Begin(request); bounded &= result.Code == (i <= 32 ? InventoryResultCode.StaleRevision : InventoryResultCode.RateLimited); }
        check(bounded && f.Authority.PendingCount == 0, "new request rate is bounded without accumulating rejected transactions");
        f = new Fixture(); var completed = f.Begin(f.Pickup()); f.Complete(completed);
        for (ulong i = 2; i < 280; i++) f.Begin(f.Pickup(request: i), i);
        check(f.Begin(f.Pickup(request: 1), 300).Code == InventoryResultCode.Duplicate && f.Authority.Get(f.A).Items.Length == 1, "evicted history retains monotonic request floor and cannot reacquire a physical item");
        f = new Fixture(); var pending = f.Begin(f.Pickup()); f.Authority.Cancel(); f.World.Reset(true, 2);
        check(f.Authority.PendingCount == 0 && f.Complete(pending).Code != InventoryResultCode.Success && f.Authority.Get(f.A).Items.Length == 0, "scene invalidation cancels reservations before registry destruction");
    }
    private static void Transport(Action<bool, string> check, bool impaired)
    {
        using var socket = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)); int port = ((System.Net.IPEndPoint)socket.Client.LocalEndPoint).Port; socket.Close();
        using var host = new NetworkManager("build"); using var client = new NetworkManager("build");
        // Reuse a former Host with a higher cached revision. The new Host's
        // revision-one baseline must replace that view before any request.
        client.Inventories.Authority.Initialize(new InventoryState(client.Inventories.LocalIdentity,9,15,new InventoryItemState[0]));
        client.Inventories.Authority.InitializeEconomy(new EconomyState(9,77,20,99,new InventoryItemState[0],new InventoryItemState[0]));
        var rule = new ItemDefinitionRule(0, .1f, 0, true);
        host.Host(port, "Host"); host.Inventories.Authority.Configure(new[] { rule });
        host.Inventories.Authority.Initialize(new InventoryState(host.Inventories.LocalIdentity, 1, 15, new InventoryItemState[0]));
        host.Inventories.Authority.InitializeEconomy(new EconomyState(1,1000,20,99,new InventoryItemState[0],new InventoryItemState[0]));
        host.Inventories.Authority.QuoteEconomy=(r,i,o)=>new EconomyQuote {MoneyDelta=r.Action==InventoryAction.Buy ? -100 : r.Action==InventoryAction.Sell ? 50 : 0};
        host.Inventories.CreateRemoteSeed = owner => new InventoryState(owner, 1, 15, new InventoryItemState[0]);
        host.Inventories.ValidateTarget = _ => InventoryResultCode.Reserved;
        host.Inventories.CreateDrop = t => new ManagedLootAdapter(new LootState { Item = t.Item });
        var descriptor = new StaticEntityDescriptor("build", "scene", "loot[0]", "pickup-v1", 0);
        var item = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0);
        var entity = host.World.Registry.RegisterStatic(descriptor, new ManagedLootAdapter(new LootState { Item = item }), new EntityOwnership(OwnershipKind.System));
        client.World.PrepareRegistry = () => { if (client.World.Registry.Count == 0) client.World.Registry.RegisterStatic(descriptor, new ManagedLootAdapter(new LootState { Item = item }), new EntityOwnership(OwnershipKind.System)); };
        client.World.AdapterFactory = _ => new ManagedLootAdapter(new LootState { Item = item });
        using var link = impaired ? new ThiefSim2Multiplayer.TransportHarness.UdpConditionLink(0, port, 60, 180, 10, 20) : null;
        host.SetLocalScene(true, "scene"); client.SetLocalScene(true, "scene"); client.Join("127.0.0.1", link == null ? port : link.Port, "Client");
        void Pump(Func<bool> done) { var timer = System.Diagnostics.Stopwatch.StartNew(); while (!done() && timer.Elapsed.TotalSeconds < 10) { host.Poll(); client.Poll(); System.Threading.Thread.Sleep(5); } if (!done()) throw new Exception("E UDP timeout: " + host.LastError + "/" + client.World.LastError); }
        Pump(() => client.Inventories.LocalState != null && client.Inventories.SharedEconomy!=null && host.World.State == WorldSyncState.WorldReady);
        check(client.Inventories.LocalState.Owner.Equals(client.Inventories.LocalIdentity), "E UDP owner-only inventory baseline bound to persistent identity");
        check(client.Inventories.LocalState.Revision==1 && client.Inventories.SharedEconomy.Money==1000 && client.Inventories.Authority.Get(client.Inventories.LocalIdentity).Revision==9,"joining a new Host accepts its lower inventory baseline without deleting former Host authority");
        InventoryRequest request = client.Inventories.Request(InventoryAction.Pickup, entity); ulong transaction = 0;
        client.Inventories.ResultReceived += r => { if (r.RequestId == request.RequestId && r.Code == InventoryResultCode.Reserved) transaction = r.TransactionId; };
        for (int i = 0; i < 20; i++) client.Inventories.SendRequest(request);
        Pump(() => transaction != 0); client.Inventories.Complete(request, transaction, true); client.Inventories.Complete(request, transaction, true);
        Pump(() => client.Inventories.LocalState.Count(0) == 1 && client.World.Registry.Lookup(entity).State.Tombstone && host.Inventories.Authority.PendingCount == 0);
        check(host.Inventories.Authority.Get(host.Inventories.LocalIdentity).Items.Length == 0 && host.Inventories.Authority.Get(client.Inventories.LocalIdentity).Items.Length == 1 && host.Inventories.Authority.InvariantViolations() == 0, "E reliable UDP duplicates settle exactly once with separate personal inventories");
        request=client.Inventories.Request(InventoryAction.Buy,definition:0); transaction=0; client.Inventories.SendRequest(request); Pump(()=>transaction!=0); client.Inventories.Complete(request,transaction,true);
        Pump(()=>client.Inventories.SharedEconomy.Money==900 && client.Inventories.LocalState.Count(0)==2);
        check(host.Inventories.Authority.Economy.Money==900 && host.Inventories.Authority.Get(host.Inventories.LocalIdentity).ItemCount==0,"F UDP native quote charges shared wallet and routes item only to Client");
        var purchased=client.Inventories.LocalState.Items.Single(i=>!i.Instance.Equals(item.Instance));
        request=client.Inventories.Request(InventoryAction.Deposit,instance:purchased.Instance.Value,definition:0,container:1); transaction=0; client.Inventories.SendRequest(request); Pump(()=>transaction!=0); client.Inventories.Complete(request,transaction,true);
        Pump(()=>client.Inventories.SharedEconomy.Find(1,purchased.Instance)!=null && client.Inventories.LocalState.Count(0)==1);
        var withdraw=host.Inventories.Request(InventoryAction.Withdraw,instance:purchased.Instance.Value,definition:0,container:1); var reserved=host.Inventories.HostBegin(withdraw,_=>InventoryResultCode.Reserved); host.Inventories.Complete(withdraw,reserved.TransactionId,true);
        Pump(()=>client.Inventories.SharedEconomy.Items(1).Length==0);
        check(host.Inventories.LocalState.Find(purchased.Instance)!=null && client.Inventories.LocalState.Find(purchased.Instance)==null && host.Inventories.Authority.InvariantViolations()==0,"F UDP Client deposit and Host withdrawal retain exactly one canonical instance");
        var expected = client.Inventories.LocalState.Hash;
        client.Disconnect(); Pump(() => host.Session.State == ThiefSim2Multiplayer.Core.SessionState.Hosting);
        using var reconnectLink = impaired ? new ThiefSim2Multiplayer.TransportHarness.UdpConditionLink(0, port, 60, 180, 10, 20) : null;
        client.Join("127.0.0.1", reconnectLink == null ? port : reconnectLink.Port, "Again");
        Pump(() => client.Inventories.LocalState != null && client.Inventories.SharedEconomy!=null && client.World.State == WorldSyncState.WorldReady);
        check(client.Inventories.SharedEconomy.Money==900 && client.Inventories.SharedEconomy.Items(1).Length==0 && host.Inventories.LocalState.Find(purchased.Instance)!=null && client.Inventories.LocalState.Hash == expected && client.World.Registry.Lookup(entity).State.Tombstone && host.World.Registry.DevelopmentSummary() == client.World.Registry.DevelopmentSummary(), "E UDP reconnect retains personal inventory and converges tombstone/world hashes");
        if (impaired) check(link.Dropped > 0 && link.Duplicated > 0 && link.QueueOverflow == 0, "E UDP inventory survives latency, jitter, loss and packet duplication");
    }
}
