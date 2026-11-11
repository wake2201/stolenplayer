// Host/client session over LiteNetLib. Main-thread driven: call Tick() regularly (PollEvents).
// LiteNetLib's own logic thread keeps the connection alive during main-thread stalls (scene loads).
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace TS2MP.Shared
{
    public sealed class SessionNode : IDisposable
    {
        public readonly Role Role;
        public readonly string SessionId, InstanceId, GameVersion;
        public readonly int Port;
        readonly InstanceLog _log;

        public int ReconnectDelayMs = 3000;
        public int HandshakeTimeoutMs = 10000;
        public int DisconnectTimeoutMs = 15000;
        public int LatencyLogIntervalMs = 30000;

        public ConnState State { get; private set; }
        public RejectCode? LastReject { get; private set; }
        public DisconnectReason? LastDisconnect { get; private set; }
        public string RemoteInstanceId { get; private set; }
        public int ConnectAttempts { get; private set; }
        public int HandshakesCompleted { get; private set; }

        // Test hook: lets tests send a deliberately wrong hello.
        public Hello HelloOverride;

        // Addressing (M2): host bind address and client target address. Defaults keep M1 behaviour (loopback only).
        public string BindAddress = "127.0.0.1";
        public string ConnectAddress = "127.0.0.1";

        // Channels: 0 = reliable lifecycle (Welcome, SceneInfo), 1 = sequenced movement snapshots.
        public const byte ChannelReliable = 0, ChannelState = 1, ChannelVehicle = 2;   // 2 = M4.1 vehicle snapshots (own sequence)

        // Post-handshake messages other than Welcome. Raised on the thread calling Tick() (Unity main thread in game).
        public event Action<NetPeer, byte, NetPacketReader> MessageReceived;
        public event Action<string> RemoteConnected;    // remote instance id
        public event Action<string> RemoteDisconnected; // reason

        public long BytesSent { get { return _net != null && _net.Statistics != null ? _net.Statistics.BytesSent : 0; } }
        public long BytesReceived { get { return _net != null && _net.Statistics != null ? _net.Statistics.BytesReceived : 0; } }
        long _lastBytesSent, _lastBytesRecv;

        NetManager _net;
        EventBasedNetListener _listener;
        NetPeer _peer;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        long _stateSince, _retryAt, _lastLatencyLog;
        int _lastLatency = -1;
        bool _disposed;

        public SessionNode(Role role, string sessionId, string instanceId, string gameVersion, int port, InstanceLog log)
        {
            Role = role; SessionId = sessionId; InstanceId = instanceId; GameVersion = gameVersion ?? ""; Port = port; _log = log;
            State = ConnState.Stopped;
        }

        long Now { get { return _clock.ElapsedMilliseconds; } }

        void SetState(ConnState s, string why)
        {
            if (State == s) return;
            var old = State;
            State = s;
            _stateSince = Now;
            _log.Info("STATE " + old + " -> " + s + (why != null ? " (" + why + ")" : ""));
        }

        public string StateText { get { return "state " + State; } }

        public bool Start()
        {
            _listener = new EventBasedNetListener();
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
            _listener.NetworkLatencyUpdateEvent += delegate(NetPeer p, int latency) { _lastLatency = latency; };

            _net = new NetManager(_listener);
            _net.IPv6Enabled = false;
            _net.DisconnectTimeout = DisconnectTimeoutMs;
            _net.AutoRecycle = true;
            _net.UnconnectedMessagesEnabled = false;
            _net.MaxConnectAttempts = 5;
            _net.ChannelsCount = 3;
            _net.EnableStatistics = true;

            bool ok;
            IPAddress bind;
            if (!IPAddress.TryParse(Role == Role.Host ? BindAddress : "0.0.0.0", out bind)) bind = IPAddress.Loopback;
            if (Role == Role.Client)
            {
                // Client socket: loopback when the host is local, any-interface when connecting to another PC.
                IPAddress target;
                bind = IPAddress.TryParse(ConnectAddress, out target) && IPAddress.IsLoopback(target) ? IPAddress.Loopback : IPAddress.Any;
            }
            try
            {
                ok = _net.Start(bind, IPAddress.IPv6None, Role == Role.Host ? Port : 0);
            }
            catch (Exception ex) { _log.Error("NetManager.Start threw", ex); ok = false; }

            if (!ok)
            {
                _log.Error("NET_START_FAILED: could not bind UDP " + (Role == Role.Host ? bind + ":" + Port : "client socket") + " (port in use or blocked)");
                SetState(ConnState.Failed, "bind failed");
                return false;
            }
            _log.Info("NET_STARTED role=" + Role + " bind=" + bind + " localPort=" + _net.LocalPort + (Role == Role.Client ? " target=" + ConnectAddress + ":" + Port : "") + " protocol=" + ProtocolInfo.ProtocolVersion + " mod=" + ProtocolInfo.ModVersion + " game=" + GameVersion);
            if (Role == Role.Host) SetState(ConnState.Listening, "waiting for Player 2");
            else BeginConnect();
            return true;
        }

        void BeginConnect()
        {
            ConnectAttempts++;
            var w = new NetDataWriter();
            var hello = HelloOverride ?? new Hello { GameVersion = GameVersion, SessionId = SessionId, InstanceId = InstanceId };
            hello.Write(w);
            if (ConnectAttempts <= 5 || ConnectAttempts % 20 == 0)
                _log.Info("CONNECT attempt " + ConnectAttempts + " -> " + ConnectAddress + ":" + Port);
            _peer = _net.Connect(ConnectAddress, Port, w);
            SetState(ConnState.Connecting, null);
        }

        public void Tick()
        {
            if (_net == null || _disposed) return;
            try { _net.PollEvents(); }
            catch (Exception ex) { _log.Error("PollEvents threw", ex); }

            long now = Now;
            if (Role == Role.Client)
            {
                if (State == ConnState.WaitingRetry && now >= _retryAt) BeginConnect();
                if (State == ConnState.Handshaking && now - _stateSince > HandshakeTimeoutMs)
                {
                    _log.Error("HANDSHAKE_TIMEOUT: connected but no Welcome within " + HandshakeTimeoutMs + " ms");
                    if (_peer != null) _peer.Disconnect();
                }
            }
            if (State == ConnState.Connected && _lastLatency >= 0 && now - _lastLatencyLog >= LatencyLogIntervalMs)
            {
                double secs = _lastBytesSent == 0 && _lastBytesRecv == 0 ? (now - _stateSince) / 1000.0 : LatencyLogIntervalMs / 1000.0;
                long sent = BytesSent, recv = BytesReceived;
                if (secs < 1) secs = 1;
                _log.Info("LATENCY " + _lastLatency + " ms remote=" + RemoteInstanceId + " traffic out=" + (long)((sent - _lastBytesSent) / secs) + " B/s in=" + (long)((recv - _lastBytesRecv) / secs) + " B/s");
                _lastLatencyLog = now; _lastBytesSent = sent; _lastBytesRecv = recv;
            }
        }

        public bool IsConnected { get { return State == ConnState.Connected && _peer != null; } }

        // Sends to the connected remote (client: the host; host: the client).
        public void Send(NetDataWriter w, DeliveryMethod method, byte channel)
        {
            if (!IsConnected) return;
            _peer.Send(w, channel, method);
        }

        // Host relay: send to every connected client except `except` (no-op with MaxClients = 1).
        public void SendToOthers(NetDataWriter w, DeliveryMethod method, byte channel, NetPeer except)
        {
            if (_net == null || Role != Role.Host) return;
            _net.SendToAll(w, channel, method, except);
        }

        // ---- host side ----
        void OnConnectionRequest(ConnectionRequest req)
        {
            Hello hello;
            RejectCode? rej = Hello.TryRead(req.Data, out hello)
                ? Validation.Check(hello, SessionId, GameVersion, InstanceId)
                : RejectCode.Malformed;
            if (rej == null && _net.ConnectedPeersCount >= ProtocolInfo.MaxClients) rej = RejectCode.SessionFull;

            if (rej != null)
            {
                _log.Warn("REJECT " + req.RemoteEndPoint + " code=" + rej.Value + " (" + (byte)rej.Value + ")"
                    + (hello != null ? " client protocol=" + hello.ProtocolVersion + " mod=" + hello.ModVersion + " game=" + hello.GameVersion + " session=" + hello.SessionId + " instance=" + hello.InstanceId : ""));
                req.Reject(new byte[] { (byte)rej.Value });
                return;
            }
            var peer = req.Accept();
            peer.Tag = hello.InstanceId;
            _log.Info("ACCEPT " + req.RemoteEndPoint + " client instance=" + hello.InstanceId + " peerId=" + peer.Id);
        }

        // ---- both ----
        void OnPeerConnected(NetPeer peer)
        {
            if (Role == Role.Host)
            {
                _peer = peer;
                RemoteInstanceId = peer.Tag as string;
                var w = new NetDataWriter();
                new Welcome { SessionId = SessionId, HostInstanceId = InstanceId, GameVersion = GameVersion, PlayerSlot = 2 }.Write(w);
                peer.Send(w, ChannelReliable, DeliveryMethod.ReliableOrdered);
                HandshakesCompleted++;
                SetState(ConnState.Connected, "client " + RemoteInstanceId);
                _log.Info("HANDSHAKE_OK host side: Welcome sent to " + RemoteInstanceId + " (slot 2)");
                if (RemoteConnected != null) RemoteConnected(RemoteInstanceId);
            }
            else
            {
                SetState(ConnState.Handshaking, "transport connected, awaiting Welcome");
            }
        }

        void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            byte type;
            if (!reader.TryGetByte(out type)) { _log.Warn("Empty packet from " + peer); return; }
            if (Role == Role.Client && type == (byte)MsgType.Welcome)
            {
                Welcome wel;
                if (!Welcome.TryRead(reader, out wel)) { _log.Error("Malformed Welcome"); peer.Disconnect(); return; }
                if (wel.SessionId != SessionId || wel.GameVersion != GameVersion || wel.HostModVersion != ProtocolInfo.ModVersion)
                {
                    _log.Error("WELCOME_MISMATCH host session=" + wel.SessionId + " game=" + wel.GameVersion + " mod=" + wel.HostModVersion);
                    peer.Disconnect();
                    return;
                }
                RemoteInstanceId = wel.HostInstanceId;
                HandshakesCompleted++;
                ConnectAttempts = 0;
                SetState(ConnState.Connected, "host " + wel.HostInstanceId);
                _log.Info("HANDSHAKE_OK client side: host=" + wel.HostInstanceId + " slot=" + wel.PlayerSlot + " mod=" + wel.HostModVersion + " game=" + wel.GameVersion);
                if (RemoteConnected != null) RemoteConnected(RemoteInstanceId);
                return;
            }
            if (State == ConnState.Connected && peer == _peer && MessageReceived != null && type != (byte)MsgType.Welcome)
            {
                MessageReceived(peer, type, reader);
                return;
            }
            _log.Warn("Unexpected message type " + type + " from " + peer + " in state " + State);
        }

        void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            LastDisconnect = info.Reason;
            string extra = "";
            if (info.Reason == DisconnectReason.ConnectionRejected && info.AdditionalData != null && info.AdditionalData.AvailableBytes >= 1)
            {
                LastReject = (RejectCode)info.AdditionalData.GetByte();
                extra = " rejectCode=" + LastReject.Value + " (" + (byte)LastReject.Value + ")";
            }
            if (info.SocketErrorCode != SocketError.Success) extra += " socketError=" + info.SocketErrorCode + " (" + (int)info.SocketErrorCode + ")";

            bool quietRetry = Role == Role.Client && info.Reason == DisconnectReason.ConnectionFailed && ConnectAttempts > 5 && ConnectAttempts % 20 != 0;
            if (!quietRetry) _log.Warn("DISCONNECT peer=" + peer + " reason=" + info.Reason + extra);
            if (State == ConnState.Connected && peer == _peer && RemoteDisconnected != null) RemoteDisconnected(info.Reason.ToString());

            if (Role == Role.Host)
            {
                if (peer == _peer) { _peer = null; RemoteInstanceId = null; }
                if (_net != null && _net.ConnectedPeersCount == 0) SetState(ConnState.Listening, "waiting for Player 2");
                return;
            }

            _peer = null;
            if (_disposed) return;
            if (LastReject.HasValue && info.Reason == DisconnectReason.ConnectionRejected && Validation.IsFatal(LastReject.Value))
            {
                _log.Error("CONNECT_FAILED_FATAL: host rejected handshake with " + LastReject.Value + "; not retrying. Restart both instances via the launcher.");
                SetState(ConnState.Failed, "rejected " + LastReject.Value);
                return;
            }
            _retryAt = Now + ReconnectDelayMs;
            SetState(ConnState.WaitingRetry, "retry in " + ReconnectDelayMs + " ms");
        }

        void OnNetworkError(IPEndPoint ep, SocketError err)
        {
            _log.Error("NETWORK_ERROR endpoint=" + ep + " socketError=" + err + " (" + (int)err + ")");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_net != null)
            {
                try
                {
                    _log.Info("SHUTDOWN: disconnecting " + _net.ConnectedPeersCount + " peer(s), stopping network");
                    _net.DisconnectAll();
                    _net.Stop(true);
                }
                catch (Exception ex) { _log.Error("Shutdown error", ex); }
                _net = null;
            }
            State = ConnState.Stopped;
            _log.Info("STATE -> Stopped (disposed)");
        }
    }
}
