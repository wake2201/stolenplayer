using System;
using System.IO;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    // Pure managed personal authority. No host-side second Inventory or PlayerScript.
    public sealed class InventoryReplication
    {
        private readonly NetworkManager network;
        private readonly Action<byte[]> send;
        private readonly Action<string> log;
        private readonly InventorySnapshotAssembler assembler = new InventorySnapshotAssembler();
        private readonly InventorySnapshotAssembler economyAssembler = new InventorySnapshotAssembler();
        private byte[][] economyOutgoing; private int economyChunk; private ulong lastEconomySent;
        public EconomyState SharedEconomy {get;private set;}
        public bool EconomyLocalPending {get;set;}
        public event Action<EconomyState> EconomyReceived;
        private Guid remoteIdentity, allowedRemoteIdentity;
        private ulong nextRequest;
        private ulong lastSentRevision;
        private byte[][] outgoing;
        private int nextChunk;
        private bool sceneRebind;
        private readonly ulong hostRuntimeToken = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) | 1UL;
        private ulong ContextSession => network.Session.SessionId == 0 ? hostRuntimeToken : network.Session.SessionId;
        public PersistentCoopPlayerId LocalIdentity { get; set; } = new PersistentCoopPlayerId(Guid.NewGuid());
        public PersistentCoopPlayerId? RemoteIdentity => remoteIdentity == Guid.Empty ? (PersistentCoopPlayerId?)null : new PersistentCoopPlayerId(remoteIdentity);
        public InventoryAuthority Authority { get; }
        public InventoryState LocalState { get; private set; }
        public ulong OwnerSnapshotWorldRevision { get; private set; }
        public Func<PersistentCoopPlayerId, InventoryState> CreateRemoteSeed { get; set; }
        public Func<InventoryRequest, InventoryResultCode> ValidateTarget { get; set; }
        public Func<InventoryTransaction, ILootAdapter> CreateDrop { get; set; }
        public event Action<InventoryTransactionResult> ResultReceived;
        public event Action<InventoryState> StateReceived;
        public long RejectedPackets { get; private set; }
        public InventoryReplication(NetworkManager manager, Action<byte[]> sender, Action<string> logger)
        {
            network = manager; send = sender; log = logger;
            Authority = new InventoryAuthority(network.World.Registry);
            Authority.Changed += state => { if (state.Owner.Equals(LocalIdentity)) { LocalState = state; StateReceived?.Invoke(state); } else SendSnapshot(state); };
            Authority.Settled += (transaction, result) => { Trace(transaction, result); if (transaction.Request.ActorId == 2) { send(InventoryProtocol.EncodeResult(result)); SendSnapshot(Authority.Get(transaction.Owner)); } else ResultReceived?.Invoke(result); };
            Authority.EconomyChanged += state => { SharedEconomy=state; EconomyReceived?.Invoke(state); SendEconomy(); };
            network.World.WorldBecameReady += Ready;
        }
        private void SendEconomy()
        {
            var state=Authority.Economy; if(state==null || !network.Session.IsHost || !network.ScenesCompatible || !RemoteIdentity.HasValue || state.Revision==lastEconomySent) return;
            lastEconomySent=state.Revision; economyOutgoing=InventoryProtocol.EconomySnapshot(network.Session.SessionId,network.World.Registry.SceneEpoch,state); economyChunk=0;
        }
        private void Ready()
        {
            if (network.Session.IsHost) { if (RemoteIdentity.HasValue) { SendSnapshot(Authority.Get(RemoteIdentity.Value)); SendEconomy(); } }
            else send(InventoryProtocol.Write(w => { w.Write(InventoryProtocol.IdentityType); w.Write(network.Session.SessionId); w.Write(LocalIdentity.Value.ToByteArray()); }));
        }
        private void SendSnapshot(InventoryState state)
        { if (state == null || !network.Session.IsHost || !network.ScenesCompatible || !RemoteIdentity.HasValue || !state.Owner.Equals(RemoteIdentity.Value) || state.Revision == lastSentRevision) return; lastSentRevision = state.Revision; outgoing = InventoryProtocol.Snapshot(network.Session.SessionId, network.World.Registry.SceneEpoch, state, network.World.Registry.WorldRevision); nextChunk = 0; }
        public void Tick()
        {
            if (network.Session.IsHost) { Authority.Tick(network.Now); LocalState = Authority.Get(LocalIdentity); }
            int budget=4;
            while(budget-- > 0 && network.World.CanSend()) {
                if(outgoing!=null) {send(outgoing[nextChunk++]); if(nextChunk==outgoing.Length) outgoing=null;}
                else if(economyOutgoing!=null) {send(economyOutgoing[economyChunk++]); if(economyChunk==economyOutgoing.Length) economyOutgoing=null;}
                else break;
            }
        }
        public void Disconnected()
        {
            Authority.Cancel(RemoteIdentity); remoteIdentity = Guid.Empty; assembler.Clear(); economyAssembler.Clear(); economyOutgoing=null; economyChunk=0; lastEconomySent=0;
            lastSentRevision = 0; outgoing = null; nextChunk = 0;
            if (!network.Session.IsHost) {LocalState = null; SharedEconomy=null;}
        }
        internal void BeginClient()
        {
            // Revisions belong to the selected Host. A former local Host view
            // must not reject the new Host's lower-revision personal baseline.
            LocalState=null; SharedEconomy=null; OwnerSnapshotWorldRevision=0;
            assembler.Clear(); economyAssembler.Clear(); sceneRebind=false;
        }
        public void SceneInvalidating() { Authority.Cancel(); assembler.Clear(); economyAssembler.Clear(); economyOutgoing=null; economyChunk=0; lastEconomySent=0; outgoing = null; nextChunk = 0; lastSentRevision = 0; sceneRebind = true; }
        public void Handle(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 1) { RejectedPackets++; return; }
            ulong session = network.Session.SessionId; uint epoch = network.World.Registry.SceneEpoch;
            if (bytes[0] == InventoryProtocol.IdentityType && network.Session.IsHost)
            {
                if (bytes.Length != 25 || BitConverter.ToUInt64(bytes, 1) != session || network.World.State != WorldSyncState.WorldReady) { RejectedPackets++; return; }
                byte[] guidBytes = new byte[16]; Buffer.BlockCopy(bytes, 9, guidBytes, 0, 16); var identity = new Guid(guidBytes);
                if (identity == Guid.Empty || identity == LocalIdentity.Value || (allowedRemoteIdentity != Guid.Empty && identity != allowedRemoteIdentity) || (remoteIdentity != Guid.Empty && identity != remoteIdentity)) { RejectedPackets++; return; }
                var owner = new PersistentCoopPlayerId(identity);
                if (Authority.Get(owner) == null)
                { var seed = CreateRemoteSeed?.Invoke(owner); if (seed == null || !seed.Owner.Equals(owner)) { RejectedPackets++; return; } Authority.Initialize(seed); }
                allowedRemoteIdentity = remoteIdentity = identity; SendSnapshot(Authority.Get(owner)); SendEconomy(); return;
            }
            if(bytes[0]==InventoryProtocol.EconomySnapshotType && !network.Session.IsHost) {
                byte[] payload; ulong revision; EconomyState state;
                if(!economyAssembler.AcceptPayload(bytes,session,epoch,network.Now,InventoryProtocol.EconomySnapshotType,25,EconomyCodec.MaxBytes,out payload,out revision)) {RejectedPackets++; return;}
                if(payload!=null) {if(!EconomyCodec.Decode(payload,out state) || state.Revision!=revision) {RejectedPackets++; return;} if(SharedEconomy==null || revision>=SharedEconomy.Revision) {SharedEconomy=state; EconomyReceived?.Invoke(state);}}
                return;
            }
            if (bytes[0] == InventoryProtocol.SnapshotType && !network.Session.IsHost)
            {
                InventoryState state;
                if (!assembler.Accept(bytes, session, epoch, network.Now, out state)) { RejectedPackets++; return; }
                if (state != null)
                { if (!state.Owner.Equals(LocalIdentity)) { RejectedPackets++; return; } if (state.NewerThan(LocalState) || sceneRebind && LocalState != null && state.Revision == LocalState.Revision && state.Hash == LocalState.Hash) { sceneRebind = false; LocalState = state; OwnerSnapshotWorldRevision = assembler.CompletedWorldRevision; StateReceived?.Invoke(state); } }
                return;
            }
            if (bytes[0] == InventoryProtocol.ResultType && !network.Session.IsHost)
            {
                InventoryTransactionResult result;
                if (!InventoryProtocol.TryDecodeResult(bytes, out result) || result.SessionId != session || result.SceneEpoch != epoch || result.RequestId > nextRequest) { RejectedPackets++; return; }
                ResultReceived?.Invoke(result); return;
            }
            if (!network.Session.IsHost || !RemoteIdentity.HasValue) { RejectedPackets++; return; }
            var actor = new ActorRef(session, network.Session.RemotePlayerId, RemoteIdentity.Value);
            if (bytes[0] == InventoryProtocol.RequestType)
            {
                InventoryRequest request;
                if (!InventoryProtocol.TryDecode(bytes, out request)) { RejectedPackets++; return; }
                var result = Authority.Begin(request, actor, epoch, network.GameplayReady, network.Now, ValidateTarget);
                send(InventoryProtocol.EncodeResult(result)); if (result.Code != InventoryResultCode.Reserved) SendSnapshot(Authority.Get(actor.PersistentId));
                log("[TS2MP] Inventory request=" + request.RequestId + " actor=" + actor.NetworkPlayerId + " persistent=" + actor.PersistentId.Value + " entity=" + request.Entity + " instance=" + request.ItemInstance + " expected=" + request.ExpectedEntityRevision + " transaction=" + result.TransactionId + " result=" + result.Code);
            }
            else if (bytes[0] == InventoryProtocol.CompletionType)
            {
                InventoryCompletion completion;
                if (!InventoryProtocol.TryDecodeCompletion(bytes, out completion)) { RejectedPackets++; return; }
                var result = Authority.Complete(completion, actor, network.Now, CreateDrop); send(InventoryProtocol.EncodeResult(result)); SendSnapshot(Authority.Get(actor.PersistentId));
            }
            else RejectedPackets++;
        }
        public InventoryRequest Request(InventoryAction action, EntityRef entity = default(EntityRef), Guid instance = default(Guid), float x = 0, float y = 0, float z = 0, ushort definition = 0, byte container = 0, float aimX=0, float aimY=0, float aimZ=1)
        {
            if (action>=InventoryAction.Buy && SharedEconomy==null) return null;
            if (LocalState == null || nextRequest == ulong.MaxValue) return null;
            var entry = action == InventoryAction.Pickup ? network.World.Registry.Lookup(entity) : null;
            var request = new InventoryRequest { SessionId = ContextSession, SceneEpoch = network.World.Registry.SceneEpoch, RequestId = ++nextRequest, ActorId = network.Session.LocalPlayerId, Action = action, Entity = entity, ItemInstance = instance, ExpectedInventoryRevision = LocalState.Revision, ExpectedEntityRevision = entry == null ? 0 : entry.State.Revision, X = x, Y = y, Z = z, Definition=definition, Container=container, ExpectedEconomyRevision=action>=InventoryAction.Buy ? SharedEconomy.Revision : 0 };
            if(action>=InventoryAction.Deposit) {request.AimX=aimX; request.AimY=aimY; request.AimZ=aimZ;}
            return InventoryProtocol.Valid(request) ? request : null;
        }
        public InventoryTransactionResult HostBegin(InventoryRequest request, Func<InventoryRequest, InventoryResultCode> validation)
        { var result = Authority.Begin(request, new ActorRef(ContextSession, 1, LocalIdentity), network.World.Registry.SceneEpoch, network.TransactionsAllowed, network.Now, validation); return result; }
        public void ObserveSoloHost(InventoryState inventory,EconomyState economy)
        {
            if(!network.Session.IsHost || network.PeerConnected || inventory==null || !inventory.Owner.Equals(LocalIdentity)) throw new InvalidOperationException("Solo Host observation required.");
            Authority.ObserveNativeHost(inventory,economy);
        }
        public bool SendRequest(InventoryRequest request)
        {
            if (network.Session.IsHost || !network.ScenesCompatible || !network.GameplayReady || !InventoryProtocol.Valid(request)) return false;
            nextRequest = Math.Max(nextRequest, request.RequestId); send(InventoryProtocol.Encode(request)); return true;
        }
        public InventoryTransactionResult Complete(InventoryRequest request, ulong transactionId, bool observed, Func<InventoryTransaction, ILootAdapter> hostDrop = null)
        {
            var completion = new InventoryCompletion { SessionId = request.SessionId, SceneEpoch = request.SceneEpoch, RequestId = request.RequestId, TransactionId = transactionId, Observed = observed };
            if (network.Session.IsHost) return Authority.Complete(completion, new ActorRef(ContextSession, 1, LocalIdentity), network.Now, hostDrop);
            if (network.ScenesCompatible) send(InventoryProtocol.EncodeCompletion(completion)); return null;
        }
        private void Trace(InventoryTransaction t, InventoryTransactionResult result)
        { log("[TS2MP] Inventory settled request=" + t.Request.RequestId + " transaction=" + t.Id + " actor=" + t.Request.ActorId + " persistent=" + t.Owner.Value + " entity=" + t.Request.Entity + " instance=" + t.Item?.Instance + " definition=" + t.Item?.Definition + " result=" + result.Code + " inventoryRevision=" + result.InventoryRevision + " worldRevision=" + result.WorldRevision); }
        public PlayerReplicationState ValidateEquipment(PlayerReplicationState state, bool remote)
        {
            if (CreateRemoteSeed == null && LocalState == null) return state; // Asset-free B/C harnesses do not configure inventory gameplay.
            InventoryState inventory = remote ? (RemoteIdentity.HasValue ? Authority.Get(RemoteIdentity.Value) : null) : LocalState;
            if ((state.Known & SampledFields.Equipment) != 0 && state.EquippedItemId >= 0)
            {
                var item = inventory != null && inventory.Equipped.HasValue ? inventory.Find(inventory.Equipped.Value) : null;
                if (item == null || item.Definition != state.EquippedItemId) state.EquippedItemId = -1;
            }
            return state;
        }
    }
}
