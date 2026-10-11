using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Collections.Generic;
using LiteNetLib;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Core.Networking
{
    // All callbacks are dispatched by Poll on the calling thread. No Unity dependencies.
    public sealed class NetworkManager : IDisposable
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Action<string> log;
        private readonly string buildFingerprint;
        private readonly ushort protocolVersion;
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private readonly byte[] transformBytes = new byte[Protocol.StateBytes];
        private long finalSent, finalReceived;
        private NetManager transport;
        private NetworkPeer peer;
        private string localName;
        private double connectStarted;
        private uint sequence, localEpoch = 1;
        private bool localReady;
        private string localScene = "";
        public MultiplayerSession Session { get; } = new MultiplayerSession();
        public TransformBuffer RemoteTransforms { get; } = new TransformBuffer();
        public SnapshotTelemetry Telemetry { get; } = new SnapshotTelemetry();
        public WorldReplication World { get; }
        public Diagnostics.FrameStabilityProfiler FrameProfile {get;} = new Diagnostics.FrameStabilityProfiler();
        public int LastProcessedPackets {get;private set;}
        private long processedPackets;
        public int PendingNetworkEvents => transport?.PendingEventCount ?? 0;
        public int PendingReliablePackets => peer?.Transport?.GetPacketsCountInReliableQueue(0,true) ?? 0;
        public double NetworkBudgetMilliseconds {get;set;}=2;
        public SceneContinuity Scenes { get; } = new SceneContinuity();
        public VehicleAuthority Vehicles { get; } = new VehicleAuthority();
        public Func<byte, EntityRef, SeatRole, SeatAction, EntityRef, bool> ValidateSeat { get; set; }
        public event Action<ulong, SeatResult> SeatCompleted;
        private ulong nextSeatRequest;
        private uint driverSequence;
        private uint motionSequence;
        private uint impactSequence, receivedImpactSequence;
        private double nextMotionAck;
        public Func<VehicleImpact,bool> ApplyVehicleImpact;
        private readonly byte[] motionBytes=new byte[VehicleMotionProtocol.Bytes];
        private readonly Dictionary<EntityRef,uint> motionWatermarks=new Dictionary<EntityRef,uint>();
        public long VehicleMotionSent { get; private set; }
        public long VehicleMotionReceived { get; private set; }
        public bool GameplayReady => !Sleep.Frozen && (!RequireTimeBaseline || TimeReady) && World.State == WorldSyncState.WorldReady && (!Scenes.Active || Scenes.State == SceneTransitionState.WorldReady);
        public bool TransactionsAllowed => Session.State==SessionState.Offline || Session.IsHost && !PeerConnected || GameplayReady;
        public bool RequireTimeBaseline {get;set;}
        public bool TimeReady {get;private set;}
        public CooperativeSleep Sleep {get;}=new CooperativeSleep();
        public WorldTimeState LastWorldTime {get;private set;}
        public Func<WorldTimeState,bool> ApplyWorldTime {get;set;}
        public event Action SleepStarted, SleepEnded;
        private uint timeSequence, receivedTimeSequence, sleepAckSequence;
        private double sleepBoundaryAt;
        public event Action SceneQuiescing;
        public Func<NetworkSceneDescriptor, NetworkSceneDescriptor> CaptureTravelOccupants;
        public event Action<NetworkSceneDescriptor> HostSceneRequested;
        private double transitionStarted;
        private SceneTransitionState timeoutPhase;
        public InteractionTelemetry Interactions { get; } = new InteractionTelemetry();
        public InventoryReplication Inventories { get; }
        private readonly InteractionAuthority interactionAuthority;
        private ulong nextRequest;
        public ulong LastInteractionRequestId => nextRequest;
        public Func<byte, EntityRef, InteractionResult> ValidateInteractionTarget { get; set; }
        public event Action<ulong, InteractionResult, ulong> InteractionCompleted;
        public string HandshakeState { get; private set; } = "None";
        public string LastDisconnectReason { get; private set; } = "None";
        public string RemoteModVersion { get { return peer == null ? "" : peer.ModVersion; } }
        public bool PeerConnected { get { return peer != null && peer.Transport != null && peer.Transport.ConnectionState == ConnectionState.Connected; } }
        public bool RemoteSceneReady { get { return peer != null && peer.Ready; } }
        public string RemoteSceneKey { get { return peer == null ? "" : peer.SceneKey; } }
        public double LastReceiveAge { get { return peer == null || peer.Transport == null ? -1 : peer.Transport.TimeSinceLastPacket / 1000.0; } }
        public long PacketsSent { get { return transport == null ? finalSent : transport.Statistics.PacketsSent; } }
        public long PacketsReceived { get { return transport == null ? finalReceived : transport.Statistics.PacketsReceived; } }
        public string LastError { get; private set; } = "";
        public string BuildFingerprintForDiagnostics { get { return buildFingerprint; } }
        public string RemoteName { get { return peer == null ? "" : peer.Name; } }
        public byte LocalAvatarId { get; set; }
        public byte RemoteAvatarId { get { return peer == null ? (byte)0 : peer.AvatarId; } }
        public int Ping { get { return peer == null || peer.Transport == null ? 0 : peer.Transport.Ping; } }
        public int Port { get { return transport == null ? 0 : transport.LocalPort; } }
        public double Now { get { return clock.Elapsed.TotalSeconds; } }
        public bool ScenesCompatible { get { return Session.State == SessionState.Connected && peer != null && peer.Ready && localReady && peer.SceneKey == localScene && Scenes.BarrierOpen; } }
        public event Action RemoteRemoved;
        public event Action Connected;

        public NetworkManager(string fingerprint, Action<string> logger = null, ushort version = Protocol.Version)
        {
            buildFingerprint = fingerprint ?? ""; log = logger ?? (_ => { }); protocolVersion = version;
            World = new WorldReplication(SendWorld);
            World.FrameProfile=FrameProfile;
            Inventories = new InventoryReplication(this, SendInventory, log);
            World.WorldBecameReady += () => Scenes.Ready();
            interactionAuthority = new InteractionAuthority(Interactions);
            World.CanSend = () => peer != null && peer.Transport != null && peer.Transport.GetPacketsCountInReliableQueue(0, true) < 128;
        }
        private void AssertOwnerThread()
        { if (Thread.CurrentThread.ManagedThreadId != ownerThread) throw new InvalidOperationException("NetworkManager must be used on its creating/main thread."); }

        public bool Host(int port, string name)
        {
            AssertOwnerThread();
            using var phase=FrameProfile.Measure(Diagnostics.RuntimePhase.JoinHandshake);
            if (port < 1 || port > 65535) return Fail("Port must be 1..65535.");
            if (!Session.BeginHost()) return Fail("Session already active.");
            World.ConfigureRole(true);
            try
            {
                localName = ValidateName(name, "Host"); LastError = "";
                CreateTransport();
                if (!transport.Start(port)) throw new InvalidOperationException("UDP port could not be opened.");
                Session.HostStarted(); HandshakeState = "Listening"; log("[TS2MP] Host started on port " + Port); return true;
            }
            catch (Exception error) { Shutdown(); return Fail("Host failed: " + error.Message); }
        }

        public bool Join(string address, int port, string name)
        {
            AssertOwnerThread();
            using var phase=FrameProfile.Measure(Diagnostics.RuntimePhase.JoinHandshake);
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return Fail("Enter a valid IPv4 address.");
            if (port < 1 || port > 65535) return Fail("Port must be 1..65535.");
            if (!Session.BeginConnect()) return Fail("Session already active.");
            Inventories.BeginClient();
            World.ConfigureRole(false);
            try
            {
                localName = ValidateName(name, "Client"); LastError = "";
                CreateTransport();
                if (!transport.Start()) throw new InvalidOperationException("Client socket could not be opened.");
                var request = new Message { Type = MessageType.HandshakeRequest, ProtocolVersion = protocolVersion,
                    ModVersion = Protocol.ModVersion, PlayerName = localName, BuildFingerprint = buildFingerprint, AvatarId = LocalAvatarId };
                peer = new NetworkPeer { Transport = transport.Connect(address, port, LiteNetLib.Utils.NetDataWriter.FromBytes(Protocol.Encode(request), false)) };
                connectStarted = Now; HandshakeState = "Connecting"; log("[TS2MP] Client connecting to " + address + ":" + port); return true;
            }
            catch (Exception error) { Shutdown(); return Fail("Join failed: " + error.Message); }
        }

        private void CreateTransport()
        {
            Telemetry.Reset(); finalSent = finalReceived = 0; LastDisconnectReason = "None";
            var listener = new EventBasedNetListener();
            transport = new NetManager(listener) { IPv6Enabled = false, UnconnectedMessagesEnabled = false,
                BroadcastReceiveEnabled = false, AutoRecycle = false, DisconnectTimeout = 5000, ChannelsCount = 4,
                UpdateTime = 15, MaxConnectAttempts = 10, ReconnectDelay = 500, UseNativeSockets = false,
                EnableStatistics = true, UnsyncedEvents = false, UnsyncedReceiveEvent = false, UnsyncedDeliveryEvent = false };
            listener.ConnectionRequestEvent += OnRequest;
            listener.PeerConnectedEvent += OnTransportConnected;
            listener.PeerDisconnectedEvent += OnDisconnected;
            listener.NetworkReceiveEvent += (sender, reader, channel, delivery) => {
                processedPackets++;
                try
                {
                    if (peer == null || sender != peer.Transport || reader.AvailableBytes < 1 || reader.AvailableBytes > Protocol.MaxPacketBytes) return;
                    byte packetType = reader.RawData[reader.Position];
                    if(packetType==VehicleImpactProtocol.Type) {
                        var bytes=new byte[reader.AvailableBytes]; Buffer.BlockCopy(reader.RawData,reader.Position,bytes,0,bytes.Length);
                        if(!Session.IsHost || channel!=0 || delivery!=DeliveryMethod.ReliableOrdered || !GameplayReady || !ScenesCompatible ||
                            !VehicleImpactProtocol.Read(bytes,out var impact) || impact.SessionId!=Session.SessionId || impact.Epoch!=World.Registry.SceneEpoch || impact.Actor!=Session.RemotePlayerId ||
                            !Protocol.IsNewer(impact.Sequence,receivedImpactSequence) || !Vehicles.RecentMotion(World.Registry,impact.Vehicle,impact.Actor,impact.AuthorityGeneration,Now) || ApplyVehicleImpact==null) Telemetry.Rejected++;
                        else {receivedImpactSequence=impact.Sequence; if(!ApplyVehicleImpact(impact)) Telemetry.Rejected++;}
                        return;
                    }
                    if(packetType>=CooperativeTimeProtocol.Baseline && packetType<=CooperativeTimeProtocol.Resume) {
                        var bytes=new byte[reader.AvailableBytes]; Buffer.BlockCopy(reader.RawData,reader.Position,bytes,0,bytes.Length);
                        WorldTimeState time; byte type;
                        if(channel!=0 || delivery!=DeliveryMethod.ReliableOrdered || !ScenesCompatible || !CooperativeTimeProtocol.Decode(bytes,out time,out type) || !HandleTime(time,type)) Telemetry.Rejected++;
                        return;
                    }
                    if (packetType == VehicleMotionProtocol.Type)
                    {
                        VehicleMotion motion;
                        if (channel!=3 || delivery!=DeliveryMethod.Sequenced || !ScenesCompatible || !GameplayReady ||
                            !VehicleMotionProtocol.Read(reader.RawData,reader.Position,reader.AvailableBytes,out motion) || !ReceiveVehicleMotion(motion)) Telemetry.Rejected++;
                        return;
                    }
                    if (packetType >= VehicleProtocol.Request && packetType <= VehicleProtocol.Input)
                    {
                        var bytes = new byte[reader.AvailableBytes]; Buffer.BlockCopy(reader.RawData, reader.Position, bytes, 0, bytes.Length);
                        if (!ScenesCompatible || !GameplayReady) { Telemetry.Rejected++; return; }
                        if (packetType == VehicleProtocol.Request && Session.IsHost && channel == 0 && delivery == DeliveryMethod.ReliableOrdered)
                        {
                            SeatRequest request;
                            if (!VehicleProtocol.Decode(bytes, out request)) { Telemetry.Rejected++; return; }
                            var result = Vehicles.Handle(World.Registry, request, Session.SessionId, Session.RemotePlayerId, GameplayReady, Now, ValidateSeat);
                            peer.Transport.Send(VehicleProtocol.EncodeResult(Session.SessionId, World.Registry.SceneEpoch, request.RequestId, result), 0, DeliveryMethod.ReliableOrdered);
                        }
                        else if (packetType == VehicleProtocol.Result && !Session.IsHost && channel == 0 && delivery == DeliveryMethod.ReliableOrdered)
                        {
                            ulong token, id; uint epoch; SeatResult result;
                            if (!VehicleProtocol.DecodeResult(bytes, out token, out epoch, out id, out result) || token != Session.SessionId || epoch != World.Registry.SceneEpoch || id > nextSeatRequest) { Telemetry.Rejected++; return; }
                            SeatCompleted?.Invoke(id, result);
                        }
                        else if (packetType == VehicleProtocol.Input && Session.IsHost && channel == 2 && delivery == DeliveryMethod.Sequenced)
                        {
                            if (Now - peer.RateWindow >= 1) { peer.RateWindow = Now; peer.ReceivedInWindow = 0; }
                            if (++peer.ReceivedInWindow > 100) { Telemetry.Rejected++; return; }
                            DriverInput input;
                            if (!VehicleProtocol.Decode(bytes, out input) || !Vehicles.AcceptInput(World.Registry, input, Session.SessionId, Session.RemotePlayerId, GameplayReady, Now)) Telemetry.Rejected++;
                        }
                        else Telemetry.Rejected++;
                        return;
                    }
                    if (packetType >= SceneProtocol.Announce && packetType <= SceneProtocol.Failed)
                    {
                        var bytes = new byte[reader.AvailableBytes]; Buffer.BlockCopy(reader.RawData, reader.Position, bytes, 0, bytes.Length);
                        SceneMessage scene;
                        if (channel == 0 && delivery == DeliveryMethod.ReliableOrdered && Session.State == SessionState.Connected && SceneProtocol.TryDecode(bytes, out scene)) HandleScene(scene);
                        else Telemetry.Rejected++;
                        return;
                    }
                    if (packetType >= InventoryProtocol.RequestType && packetType <= InventoryProtocol.EconomySnapshotType)
                    {
                        if (channel != 0 || delivery != DeliveryMethod.ReliableOrdered || !ScenesCompatible) { Telemetry.Rejected++; return; }
                        var bytes = new byte[reader.AvailableBytes]; Buffer.BlockCopy(reader.RawData, reader.Position, bytes, 0, bytes.Length); Inventories.Handle(bytes); return;
                    }
                    if (packetType == InteractionProtocol.RequestType || packetType == InteractionProtocol.ResultType)
                    {
                        if (channel != 0 || delivery != DeliveryMethod.ReliableOrdered || !ScenesCompatible) { Telemetry.Rejected++; return; }
                        if (packetType == InteractionProtocol.RequestType && Session.IsHost)
                        {
                            InteractionRequest request;
                            if (!InteractionProtocol.TryDecode(reader.RawData, reader.Position, reader.AvailableBytes, out request)) { Telemetry.Rejected++; Interactions.Rejected++; return; }
                            var result = interactionAuthority.Handle(World.Registry, request, Session.SessionId, Session.RemotePlayerId, GameplayReady, Now, ValidateInteractionTarget);
                            var entry = World.Registry.Lookup(request.Entity); ulong revision = entry == null ? 0 : entry.State.Revision;
                            log("[TS2MP] Interaction actor=" + request.ActorId + " request=" + request.RequestId + " entity=" + request.Entity + " kind=" + request.Kind + " expected=" + request.ExpectedRevision + " result=" + result + " revision=" + revision);
                            peer.Transport.Send(InteractionProtocol.EncodeResult(Session.SessionId, World.Registry.SceneEpoch, request.RequestId, result, revision), 0, DeliveryMethod.ReliableOrdered);
                            // A rejection must converge through the existing ordered stream, never an out-of-order record.
                            if (result == InteractionResult.StaleRevision || result == InteractionResult.FutureRevision) World.Resnapshot();
                        }
                        else if (packetType == InteractionProtocol.ResultType && !Session.IsHost)
                        {
                            ulong token, id, revision; uint epoch; InteractionResult result;
                            if (!InteractionProtocol.TryDecodeResult(reader.RawData, reader.Position, reader.AvailableBytes, out token, out epoch, out id, out result, out revision) || token != Session.SessionId || epoch != World.Registry.SceneEpoch || id > nextRequest) { Telemetry.Rejected++; return; }
                            if (result == InteractionResult.Accepted) Interactions.Accepted++; else if (result == InteractionResult.Duplicate) Interactions.Duplicates++; else { Interactions.Rejected++; if (result == InteractionResult.StaleRevision) Interactions.Stale++; }
                            InteractionCompleted?.Invoke(id, result, revision);
                        }
                        else Telemetry.Rejected++;
                        return;
                    }
                    if (reader.RawData[reader.Position] >= (byte)WorldMessageType.SnapshotBegin)
                    {
                        WorldMessage worldMessage;
                        if (channel == 0 && delivery == DeliveryMethod.ReliableOrdered && Session.State == SessionState.Connected && ScenesCompatible &&
                            WorldProtocol.TryDecode(reader.RawData, reader.Position, reader.AvailableBytes, out worldMessage)) World.Handle(worldMessage, Now);
                        else Telemetry.Rejected++;
                        return;
                    }
                    if (reader.RawData[reader.Position] == (byte)MessageType.PlayerState)
                    {
                        PlayerTransform pose;
                        if (Protocol.TryReadState(reader.RawData, reader.Position, reader.AvailableBytes, out pose)) HandleTransform(pose, channel, delivery);
                        else Telemetry.Rejected++;
                        return;
                    }
                    Message message;
                    if (!Protocol.TryDecode(reader.RawData, reader.Position, reader.AvailableBytes, out message)) { Telemetry.Rejected++; return; }
                    Handle(message, channel, delivery);
                }
                finally { reader.Recycle(); }
            };
            listener.NetworkErrorEvent += (endpoint, error) => { LastError = "Socket error: " + error; };
        }

        private void OnRequest(ConnectionRequest request)
        {
            using var phase=FrameProfile.Measure(Diagnostics.RuntimePhase.JoinHandshake);
            if (!Session.IsHost) { request.Reject(); return; }
            Message message = null;
            string rejection = "";
            if (request.Data.AvailableBytes < 1 || request.Data.AvailableBytes > Protocol.MaxPacketBytes || !Protocol.TryDecode(request.Data.GetRemainingBytes(), out message) || message.Type != MessageType.HandshakeRequest)
                rejection = "Malformed handshake.";
            else rejection = ValidateHandshake(message, protocolVersion, buildFingerprint, peer != null || Session.State != SessionState.Hosting);
            if (rejection.Length > 0)
            {
                request.Reject(Protocol.Encode(new Message { Type = MessageType.HandshakeRejected, Reason = rejection }));
                log("[TS2MP] Handshake rejected: " + rejection); return;
            }
            peer = new NetworkPeer { Transport = request.Accept(), Name = message.PlayerName, ModVersion = message.ModVersion, AvatarId = message.AvatarId };
        }

        public static string ValidateHandshake(Message message, ushort expectedVersion, string expectedFingerprint, bool full)
        {
            if (expectedVersion != Protocol.Version || message.ProtocolVersion != expectedVersion) return "Incompatible protocol version.";
            if (message.ModVersion != Protocol.ModVersion) return "Incompatible mod version.";
            if (!AvatarCatalog.IsSupported(message.AvatarId)) return "Unsupported avatar definition.";
            if (message.BuildFingerprint != expectedFingerprint) return "Different game build.";
            return full ? "Session full or duplicate connection." : "";
        }

        private void OnTransportConnected(NetPeer sender)
        {
            using var phase=FrameProfile.Measure(Diagnostics.RuntimePhase.JoinHandshake);
            if (peer == null || sender != peer.Transport) { sender.Disconnect(); return; }
            log("[TS2MP] Peer connected");
            if (!Session.IsHost) { HandshakeState = "Waiting for acceptance"; return; }
            ulong token = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
            if (token == 0) token = 1;
            Session.Accept(token); RemoteTransforms.Clear(); Telemetry.Reset(); sequence = 0; HandshakeState = "Accepted";
            World.BeginConnection(true, token, Now);
            sender.Send(Protocol.Encode(new Message { Type = MessageType.HandshakeAccepted, ProtocolVersion = protocolVersion,
                ModVersion = Protocol.ModVersion, PlayerName = localName, BuildFingerprint = buildFingerprint,
                LocalPlayerId = 2, RemotePlayerId = 1, SessionId = token, AvatarId = LocalAvatarId }), 0, DeliveryMethod.ReliableOrdered);
            SendScene(); if (Scenes.Active) { Scenes.Begin(Scenes.TransitionId + 1, Scenes.Epoch, Scenes.Target); Scenes.LocalReady(localScene); transitionStarted = Now; SendTransition(SceneProtocol.Announce); SendScene(); }
            log("[TS2MP] Handshake accepted; host=1 client=2"); Connected?.Invoke();
        }

        private void Handle(Message message, byte channel, DeliveryMethod delivery)
        {
            if (channel != 0 || delivery != DeliveryMethod.ReliableOrdered) return;
            if (message.Type == MessageType.HandshakeAccepted && !Session.IsHost && Session.State == SessionState.Connecting)
            {
                string rejection = ValidateHandshake(message, protocolVersion, buildFingerprint, false);
                if (rejection.Length != 0) { LastError = rejection; Shutdown(); return; }
                peer.Name = message.PlayerName; peer.ModVersion = message.ModVersion; peer.AvatarId = message.AvatarId; Session.Accept(message.SessionId); sequence = 0;
                RemoteTransforms.Clear(); Telemetry.Reset(); HandshakeState = "Accepted";
                World.BeginConnection(false, message.SessionId, Now);
                SendScene(); log("[TS2MP] Handshake accepted; local=2 remote=1"); Connected?.Invoke();
            }
            else if (message.Type == MessageType.SceneState && Session.State == SessionState.Connected)
            {
                if (peer.SceneEpoch != 0 && message.SceneEpoch != peer.SceneEpoch && !Protocol.IsNewer(message.SceneEpoch, peer.SceneEpoch)) return;
                if (message.SceneEpoch != peer.SceneEpoch || message.Ready != peer.Ready || message.SceneKey != peer.SceneKey) { RemoteTransforms.Clear(); Telemetry.ResetTimeline(); }
                peer.Ready = message.Ready; peer.SceneEpoch = message.SceneEpoch; peer.SceneKey = message.SceneKey;
            }
        }

        private void HandleTransform(PlayerTransform pose, byte channel, DeliveryMethod delivery)
        {
            if (channel != 1 || delivery != DeliveryMethod.Sequenced || !ScenesCompatible || pose.PlayerId != Session.RemotePlayerId || pose.SceneEpoch != peer.SceneEpoch) { Telemetry.Rejected++; return; }
            if (Session.IsHost) pose.State = Inventories.ValidateEquipment(pose.State, true);
            double now = Now;
            if (now - peer.RateWindow >= 1) { peer.RateWindow = now; peer.ReceivedInWindow = 0; }
            if (++peer.ReceivedInWindow > 100 || !RemoteTransforms.Add(pose, now)) { Telemetry.Rejected++; return; }
            Telemetry.Record(pose, now);
        }

        public void SetLocalScene(bool ready, string key)
        {
            AssertOwnerThread();
            key = key ?? "";
            if (ready == localReady && key == localScene) return;
            localReady = ready; localScene = key; localEpoch++; if (localEpoch == 0) localEpoch = 1;
            RemoteTransforms.Clear(); Telemetry.ResetTimeline(); SendScene();
        }
        public void BeginSceneTransition(NetworkSceneDescriptor target)
        {
            AssertOwnerThread(); if (!Session.IsHost || target == null) throw new InvalidOperationException("Only host may select a supported world.");
            uint epoch = World.Registry.SceneEpoch; if (epoch == uint.MaxValue) throw new InvalidOperationException("Scene epoch exhausted.");
            target = CaptureTravelOccupants?.Invoke(target) ?? target;
            Scenes.Begin(Scenes.TransitionId + 1, epoch + 1, target); QuiesceScene(); SendTransition(SceneProtocol.Announce);
            log("[TS2MP] SCENE announced id=" + Scenes.TransitionId + " epoch=" + Scenes.Epoch + " target=" + target.SceneKey);
        }
        public void PublishStableScene(NetworkSceneDescriptor target)
        {
            AssertOwnerThread(); if (!Session.IsHost || target == null || Scenes.Active) return;
            Scenes.Begin(1, World.Registry.SceneEpoch, target); Scenes.LocalReady(localScene); transitionStarted = Now; SendTransition(SceneProtocol.Announce);
        }
        private void QuiesceScene()
        {
            ResetTime();
            motionWatermarks.Clear(); motionSequence=0; impactSequence=receivedImpactSequence=0; nextMotionAck=0;
            transitionStarted = Now; SetLocalScene(false, ""); if (peer != null) peer.Ready = false;
            SceneQuiescing?.Invoke(); Vehicles.Reset(); Inventories.SceneInvalidating(); World.TransitionScene(Scenes.Epoch); RemoteTransforms.Clear(); Scenes.Loading();
        }
        private void SendTransition(byte type)
        {
            if (Session.State != SessionState.Connected || peer == null || !Scenes.Active) return;
            peer.Transport.Send(SceneProtocol.Encode(new SceneMessage { Type = type, SessionId = Session.SessionId, TransitionId = Scenes.TransitionId, Epoch = Scenes.Epoch, Target = Scenes.Target }), 0, DeliveryMethod.ReliableOrdered);
        }
        private void HandleScene(SceneMessage m)
        {
            if (m.SessionId != Session.SessionId) { Telemetry.Rejected++; return; }
            if (m.Type == SceneProtocol.Announce && !Session.IsHost)
            {
                if ((Scenes.Active && m.TransitionId <= Scenes.TransitionId) || m.Epoch < World.Registry.SceneEpoch) { Telemetry.Rejected++; return; }
                Scenes.Begin(m.TransitionId, m.Epoch, m.Target); QuiesceScene(); HostSceneRequested?.Invoke(m.Target);
            }
            else if (m.Type == SceneProtocol.Loaded && Session.IsHost)
            { if (!Scenes.PeerReady(m.TransitionId, m.Epoch, m.Target.SceneKey)) Telemetry.Rejected++; }
            else if (m.Type == SceneProtocol.Failed && m.TransitionId == Scenes.TransitionId && m.Epoch == Scenes.Epoch)
            { Scenes.Fail("Peer could not load the authoritative world."); SetLocalScene(false, ""); log("[TS2MP] SCENE failed; transport retained"); }
            else Telemetry.Rejected++;
        }
        public bool MarkSceneLoaded(string key)
        {
            AssertOwnerThread();
            using var phase=FrameProfile.Measure(Diagnostics.RuntimePhase.WorldReadyTransition);
            if (!Scenes.Active) return false;
            if (Scenes.LocalLoaded) return key == Scenes.Target.SceneKey;
            if (!Scenes.LocalReady(key)) return false;
            SetLocalScene(true, key);
            if (!Session.IsHost) { Scenes.PeerReady(Scenes.TransitionId, Scenes.Epoch, key); SendTransition(SceneProtocol.Loaded); }
            log("[TS2MP] SCENE local loaded id=" + Scenes.TransitionId + " epoch=" + Scenes.Epoch + " target=" + key); return true;
        }
        public void FailSceneTransition(string reason)
        { AssertOwnerThread(); Scenes.Fail(reason); SetLocalScene(false, ""); SendTransition(SceneProtocol.Failed); log("[TS2MP] SCENE failed: " + reason + "; transport retained"); }
        private void SendScene()
        {
            if (Session.State != SessionState.Connected || peer == null) return;
            peer.Transport.Send(Protocol.Encode(new Message { Type = MessageType.SceneState, Ready = localReady, SceneKey = localScene, SceneEpoch = localEpoch }), 0, DeliveryMethod.ReliableOrdered);
        }
        public bool SendTransform(float x, float y, float z, float yaw)
        { return SendState(new PlayerTransform { X = x, Y = y, Z = z, Yaw = yaw }); }

        public bool CanGlobalAction(bool requiresSharedVehicle, out string reason)
        {
            AssertOwnerThread(); reason = "";
            if (Session.State == SessionState.Offline) return true;
            if (!Session.IsHost) { reason = "Only the Host may initiate travel or sleep."; return false; }
            if (!PeerConnected) return true;
            if (!ScenesCompatible || !GameplayReady) { reason = "The cooperative world is not ready."; return false; }
            if (requiresSharedVehicle && !VehicleAuthority.BothConfirmed(World.Registry)) { reason = "Both players must be inside the vehicle."; return false; }
            return true;
        }
        public bool PublishTime(WorldTimeState time)
        {
            AssertOwnerThread(); if(!Session.IsHost || !ScenesCompatible || World.State!=WorldSyncState.WorldReady) return false;
            time.SessionId=Session.SessionId; time.Epoch=World.Registry.SceneEpoch; time.Sequence=++timeSequence; time.SleepId=Sleep.Frozen ? Sleep.Id : 0;
            if(!time.Valid) return false;
            LastWorldTime=time; TimeReady=true; SendTime(time,CooperativeTimeProtocol.Baseline); return true;
        }
        public bool BeginSleep(bool requiresSharedVehicle)
        {
            AssertOwnerThread(); string reason;
            if(!PeerConnected || !CanGlobalAction(requiresSharedVehicle,out reason) || !TimeReady || Sleep.Id==ulong.MaxValue) return false;
            Sleep.Begin(Sleep.Id+1); sleepBoundaryAt=Now;
            var time=LastWorldTime; time.SleepId=Sleep.Id; time.Sequence=++timeSequence; sleepAckSequence=time.Sequence;
            SendTime(time,CooperativeTimeProtocol.Begin); return true;
        }
        public bool WakeSleep(WorldTimeState time)
        {
            AssertOwnerThread(); if(!Session.IsHost || !Sleep.Wake(Sleep.Id)) return false;
            sleepBoundaryAt=Now; time.SessionId=Session.SessionId; time.Epoch=World.Registry.SceneEpoch; time.SleepId=Sleep.Id; time.Sequence=++timeSequence;
            if(!time.Valid) { Sleep.Fail(); return false; }
            LastWorldTime=time; sleepAckSequence=time.Sequence; SendTime(time,CooperativeTimeProtocol.Wake); return true;
        }
        private void SendTime(WorldTimeState time,byte type) { if(peer!=null && Session.State==SessionState.Connected) peer.Transport.Send(CooperativeTimeProtocol.Encode(time,type),0,DeliveryMethod.ReliableOrdered); }
        private bool HandleTime(WorldTimeState time,byte type)
        {
            if(time.SessionId!=Session.SessionId || time.Epoch!=World.Registry.SceneEpoch) return false;
            if(Session.IsHost) {
                if(type!=CooperativeTimeProtocol.Ack || time.Sequence!=sleepAckSequence || !Sleep.Acknowledge(time.SleepId)) return false;
                if(Sleep.Phase==CooperativeSleepPhase.Running) SleepStarted?.Invoke();
                else { var final=LastWorldTime; final.Sequence=++timeSequence; SendTime(final,CooperativeTimeProtocol.Resume); SleepEnded?.Invoke(); }
                return true;
            }
            if(type==CooperativeTimeProtocol.Ack || !Protocol.IsNewer(time.Sequence,receivedTimeSequence)) return false;
            if(type==CooperativeTimeProtocol.Begin && (Sleep.Frozen || time.SleepId<=Sleep.Id) ||
                type==CooperativeTimeProtocol.Wake && (Sleep.Phase!=CooperativeSleepPhase.Running || time.SleepId!=Sleep.Id) ||
                type==CooperativeTimeProtocol.Resume && (Sleep.Phase!=CooperativeSleepPhase.Waking || time.SleepId!=Sleep.Id) ||
                type==CooperativeTimeProtocol.Baseline && (Sleep.Frozen ? time.SleepId!=Sleep.Id : time.SleepId!=0)) return false;
            if(ApplyWorldTime!=null && !ApplyWorldTime(time)) return false;
            if(type==CooperativeTimeProtocol.Begin) { if(Sleep.Frozen || time.SleepId<=Sleep.Id) return false; Sleep.Begin(time.SleepId); Sleep.Acknowledge(time.SleepId); SendTime(time,CooperativeTimeProtocol.Ack); }
            else if(type==CooperativeTimeProtocol.Wake) { if(!Sleep.Wake(time.SleepId)) return false; SendTime(time,CooperativeTimeProtocol.Ack); }
            else if(type==CooperativeTimeProtocol.Resume) { if(!Sleep.Acknowledge(time.SleepId)) return false; SleepEnded?.Invoke(); }
            else if(Sleep.Frozen && time.SleepId!=Sleep.Id) return false;
            LastWorldTime=time; receivedTimeSequence=time.Sequence; TimeReady=true; return true;
        }
        private void ResetTime() { TimeReady=false; timeSequence=receivedTimeSequence=sleepAckSequence=0; Sleep.Clear(); LastWorldTime=default(WorldTimeState); }
        public ulong RequestSeat(EntityRef vehicle, SeatRole seat, SeatAction action, EntityRef door = default(EntityRef))
        {
            AssertOwnerThread();
            if (!GameplayReady || Session.State != SessionState.Connected || !ScenesCompatible || nextSeatRequest == ulong.MaxValue) return 0;
            var a = World.Registry.GetBinding(vehicle) as IVehicleAdapter; if (a == null) return 0;
            var q = new SeatRequest { SessionId = Session.SessionId, Epoch = World.Registry.SceneEpoch, RequestId = ++nextSeatRequest, Actor = Session.LocalPlayerId, Vehicle = vehicle, Door=door, Seat = seat, Action = action, OccupancyRevision = a.State.OccupancyRevision };
            if(!VehicleProtocol.Valid(q)) return 0;
            if (Session.IsHost) { var result=Vehicles.Handle(World.Registry,q,Session.SessionId,Session.LocalPlayerId,GameplayReady,Now,ValidateSeat); SeatCompleted?.Invoke(q.RequestId,result); }
            else peer.Transport.Send(VehicleProtocol.Encode(q), 0, DeliveryMethod.ReliableOrdered);
            return q.RequestId;
        }
        public bool SendDriverInput(EntityRef vehicle, float steer, float forward, float handbrake, bool engineRunning = true)
        {
            AssertOwnerThread(); if (!GameplayReady || !ScenesCompatible) return false;
            var a = World.Registry.GetBinding(vehicle) as IVehicleAdapter; if (a == null || a.State.Driver != Session.LocalPlayerId) return false;
            var q = new DriverInput { SessionId = Session.SessionId, Epoch = World.Registry.SceneEpoch, Sequence = ++driverSequence, Actor = Session.LocalPlayerId, Vehicle = vehicle, OccupancyRevision = a.State.OccupancyRevision, Steer = steer, Forward = forward, Handbrake = handbrake, EngineRunning=engineRunning };
            if (!VehicleProtocol.Valid(q)) return false;
            if (Session.IsHost) return Vehicles.AcceptInput(World.Registry, q, Session.SessionId, Session.LocalPlayerId, GameplayReady, Now);
            peer.Transport.Send(VehicleProtocol.Encode(q), 2, DeliveryMethod.Sequenced); return true;
        }
        public bool SendVehicleMotion(VehicleMotion motion)
        {
            AssertOwnerThread();
            if (!GameplayReady || !ScenesCompatible || peer==null || !(World.Registry.GetBinding(motion.Vehicle) is IVehicleAdapter a) ||
                a.State.Driver!=Session.LocalPlayerId && !(Session.IsHost && a.State.Driver==0)) return false;
            motion.Actor=a.State.Driver; motion.AuthorityGeneration=a.State.AuthorityGeneration;
            return SendMotionPacket(motion);
        }
        private bool SendMotionPacket(VehicleMotion motion)
        {
            motion.SessionId=Session.SessionId; motion.Epoch=World.Registry.SceneEpoch; motion.Sequence=++motionSequence;
            if (motion.Sequence==0) motion.Sequence=++motionSequence;
            motion.Tick=unchecked((uint)clock.ElapsedMilliseconds);
            if(!VehicleMotionProtocol.Write(motion,motionBytes)) return false;
            peer.Transport.Send(motionBytes,3,DeliveryMethod.Sequenced); VehicleMotionSent++; return true;
        }
        public bool SendVehicleImpact(VehicleImpact impact)
        {
            AssertOwnerThread();
            var a=World.Registry.GetBinding(impact.Vehicle) as IVehicleAdapter;
            if(Session.IsHost || !GameplayReady || !ScenesCompatible || peer==null || a==null || a.State.Driver!=Session.LocalPlayerId) return false;
            impact.SessionId=Session.SessionId; impact.Epoch=World.Registry.SceneEpoch; impact.Actor=Session.LocalPlayerId; impact.AuthorityGeneration=a.State.AuthorityGeneration;
            impact.Sequence=++impactSequence; if(impact.Sequence==0) impact.Sequence=++impactSequence;
            if(!impact.Valid) return false;
            peer.Transport.Send(VehicleImpactProtocol.Write(impact),0,DeliveryMethod.ReliableOrdered); return true;
        }
        private bool ReceiveVehicleMotion(VehicleMotion motion)
        {
            if (motion.SessionId!=Session.SessionId || motion.Epoch!=World.Registry.SceneEpoch) return false;
            var a=World.Registry.GetBinding(motion.Vehicle) as IVehicleAdapter;
            if(a==null || !a.IsAlive) return false;
            if(Session.IsHost) {
                if(!Vehicles.AcceptMotion(World.Registry,motion,Session.SessionId,Session.RemotePlayerId,GameplayReady,Now,out bool severe)) {
                    if(severe) Vehicles.RecoverMotion(World.Registry,motion.Vehicle,Now);
                    return false;
                }
                (a as IVehicleMotionAdapter)?.ApplyMotion(motion,Now); VehicleMotionReceived++;
                // A low-rate canonical acknowledgement bounds partition recovery;
                // its pose is never applied by the active driver.
                if(Now>=nextMotionAck) {nextMotionAck=Now+.25; motion.Fuel=a.State.Fuel; SendMotionPacket(motion);}
                return true;
            }
            if(motion.Actor!=a.State.Driver || motion.AuthorityGeneration!=a.State.AuthorityGeneration) return false;
            uint previous; if(motionWatermarks.TryGetValue(motion.Vehicle,out previous) && !Protocol.IsNewer(motion.Sequence,previous)) return false;
            motionWatermarks[motion.Vehicle]=motion.Sequence;
            // The confirmed local driver never consumes echoed/old Host poses.
            if(a.State.Driver==Session.LocalPlayerId) return true;
            motion.Apply(a.State); (a as IVehicleMotionAdapter)?.ApplyMotion(motion,Now); VehicleMotionReceived++; return true;
        }

        public bool SendState(PlayerTransform pose)
        {
            AssertOwnerThread();
            if (!ScenesCompatible || Scenes.Active && World.State != WorldSyncState.WorldReady) return false;
            pose.PlayerId = Session.LocalPlayerId; pose.SceneEpoch = localEpoch; pose.Sequence = ++sequence; pose.Tick = unchecked((uint)clock.ElapsedMilliseconds);
            pose.State = Inventories.ValidateEquipment(pose.State, false);
            if (!Protocol.WriteState(pose, transformBytes)) return false;
            // LiteNetLib copies synchronously into its pooled packet before returning.
            peer.Transport.Send(transformBytes, 1, DeliveryMethod.Sequenced); Telemetry.Sent++; return true;
        }
        public void Poll()
        {
            AssertOwnerThread();
            using var phase=FrameProfile.Measure(Diagnostics.RuntimePhase.RuntimeNetworkProcessing);
            long before=processedPackets;
            LastProcessedPackets=0;
            Vehicles.Tick(World.Registry, Now);
            Inventories.Tick();
            if (transport == null) { World.Tick(Now, false); return; }
            try
            {
                // Do not drop authoritative traffic under pressure. Abort explicitly at
                // an abusive backlog, before processing, rather than pretending convergence.
                if(transport.PendingEventCount>8192) throw new InvalidOperationException("Inbound event backlog exceeded 8192; explicit session failure.");
                transport.PollEvents(128,Math.Max(.1,Math.Min(10,NetworkBudgetMilliseconds)));
                LastProcessedPackets=(int)(processedPackets-before);
                if((Sleep.Phase==CooperativeSleepPhase.Starting || Sleep.Phase==CooperativeSleepPhase.Waking) && Now-sleepBoundaryAt>15 && Session.IsHost) { Sleep.Fail(); SleepEnded?.Invoke(); FailSceneTransition("Cooperative sleep acknowledgement timed out; world remains frozen."); }
                if(timeoutPhase!=Scenes.State) { timeoutPhase=Scenes.State; transitionStarted=Now; }
                if (Scenes.Active && Scenes.State != SceneTransitionState.WorldReady && Scenes.State != SceneTransitionState.Failed && Now - transitionStarted > Scenes.TimeoutSeconds) FailSceneTransition("Scene phase "+Scenes.State+" timed out after "+Scenes.TimeoutSeconds+" seconds.");
                World.Tick(Now, ScenesCompatible);
                if (Session.State == SessionState.Connecting && Now - connectStarted > 10)
                { LastError = "Connection/handshake timed out."; HandshakeState = "Timed out"; LastDisconnectReason = "Handshake timeout"; log("[TS2MP] " + LastError); Shutdown(); }
                if (Session.State == SessionState.Offline && transport != null) Shutdown();
            }
            catch (Exception error) { LastError = "Network failure: " + error.Message; log("[TS2MP] " + LastError); Shutdown(); }
        }
        private void OnDisconnected(NetPeer sender, DisconnectInfo info)
        {
            if (peer == null || sender != peer.Transport) return;
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0 && info.AdditionalData.AvailableBytes <= Protocol.MaxPacketBytes)
            {
                Message message;
                if (Protocol.TryDecode(info.AdditionalData.GetRemainingBytes(), out message) &&
                    (message.Type == MessageType.HandshakeRejected || message.Type == MessageType.PlayerLeft)) LastError = message.Reason;
            }
            if (LastError.Length == 0 && info.Reason == DisconnectReason.Timeout) LastError = "Peer timed out.";
            LastDisconnectReason = info.Reason.ToString();
            HandshakeState = info.Reason == DisconnectReason.ConnectionRejected ? "Rejected" : info.Reason == DisconnectReason.Timeout ? "Timed out" : "Disconnected";
            ClearPeer(); Session.PeerLost(); log("[TS2MP] Peer disconnected: " + info.Reason);
        }
        public void Disconnect(string reason = "Disconnected.")
        {
            AssertOwnerThread();
            if (Session.State == SessionState.Offline) return;
            Session.BeginDisconnect();
            if (peer != null && peer.Transport != null) peer.Transport.Disconnect(Protocol.Encode(new Message { Type = MessageType.PlayerLeft, Reason = reason }));
            LastDisconnectReason = reason; HandshakeState = "Disconnected"; Shutdown(); log("[TS2MP] Session stopped: " + reason);
        }
        private void SendWorld(WorldMessage message)
        { if (Session.State == SessionState.Connected && peer != null) peer.Transport.Send(WorldProtocol.Encode(message), 0, DeliveryMethod.ReliableOrdered); }
        private void SendInventory(byte[] bytes)
        { if (Session.State == SessionState.Connected && peer != null) peer.Transport.Send(bytes, 0, DeliveryMethod.ReliableOrdered); }
        public bool SendInteraction(EntityRef entity, InteractionKind kind)
        {
            AssertOwnerThread();
            if (NetworkApplyScope.Active || Session.IsHost || !ScenesCompatible || !GameplayReady || nextRequest == ulong.MaxValue) return false;
            var entry = World.Registry.Lookup(entity);
            if (entry == null || entry.State.Tombstone || !(World.Registry.GetBinding(entity) is IInteractionAdapter)) return false;
            return SendInteractionRequest(new InteractionRequest { SessionId = Session.SessionId, SceneEpoch = World.Registry.SceneEpoch, RequestId = ++nextRequest, ActorId = Session.LocalPlayerId, Entity = entity, Kind = kind, ExpectedRevision = entry.State.Revision });
        }
        // Semantic bounded request; exposed for the transport harness's duplicate/stale cases.
        public bool SendInteractionRequest(InteractionRequest request)
        {
            AssertOwnerThread();
            if (NetworkApplyScope.Active || Session.IsHost || !ScenesCompatible || !GameplayReady || !InteractionProtocol.Valid(request)) return false;
            if (request.RequestId > nextRequest) nextRequest = request.RequestId;
            peer.Transport.Send(InteractionProtocol.Encode(request), 0, DeliveryMethod.ReliableOrdered); Interactions.Sent++; return true;
        }
        public void InvalidateWorldScene() { AssertOwnerThread(); Inventories.SceneInvalidating(); World.InvalidateScene(); }
        private void ClearPeer() { ResetTime(); SleepEnded?.Invoke(); motionWatermarks.Clear(); motionSequence=0; impactSequence=receivedImpactSequence=0; nextMotionAck=0; Vehicles.RemoveActor(World.Registry, Session.RemotePlayerId); Vehicles.Reset(); nextSeatRequest = 0; driverSequence = 0; Inventories.Disconnected(); peer = null; World.Cancel(); RemoteTransforms.Clear(); RemoteRemoved?.Invoke(); if (!Session.IsHost) Scenes.Clear(); }
        private void Shutdown()
        {
            NetManager old = transport; transport = null;
            if (old != null) { finalSent = old.Statistics.PacketsSent; finalReceived = old.Statistics.PacketsReceived; }
            ClearPeer(); Session.Stop(); sequence = 0;
            // A stopped session cannot retain the former Host's epoch/transition or sealed registry.
            // Peer loss while continuing to host intentionally uses ClearPeer without this reset.
            Scenes.Clear(); SceneQuiescing?.Invoke(); World.Registry.Reset(false,1);
            if (old != null) old.Stop();
        }
        private bool Fail(string error) { LastError = error; log("[TS2MP] " + error); return false; }
        private static string ValidateName(string value, string fallback)
        {
            value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            Protocol.Encode(new Message { Type = MessageType.HandshakeRequest, ProtocolVersion = Protocol.Version, ModVersion = Protocol.ModVersion, PlayerName = value });
            return value;
        }
        public void Dispose() { AssertOwnerThread(); try { if (Session.State == SessionState.Offline) Shutdown(); else Disconnect(); } finally { World.Registry.ReleaseBindings(); } }
    }
}
