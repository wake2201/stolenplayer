// Host/client session over LiteNetLib. Main-thread driven: call Tick() regularly (PollEvents).
// LiteNetLib's own logic thread keeps the connection alive during main-thread stalls (scene loads).
// N1: optional join-code authentication (JoinAuth). A host bound beyond loopback refuses to start without a join key;
// with a key every client must pass the HMAC challenge-response before any gameplay message is accepted, and the
// client verifies the host's proof before it treats the session as connected.
using System;
using System.Collections.Generic;
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
        public int RateLimitedDelayMs = 30000;
        public int HandshakeTimeoutMs = 10000;
        public int AuthTimeoutMs = 5000;        // host: an accepted peer must answer the challenge within this time
        public int ReadyTimeoutMs = 90000;      // host (N1.2): a welcomed client must send ClientReady within this time
        public int HostNotReadyDelayMs = 5000;  // client (N1.2): retry delay after HostNotReady
        public int DisconnectTimeoutMs = 15000;
        public int LatencyLogIntervalMs = 30000;

        public ConnState State { get; private set; }
        public RejectCode? LastReject { get; private set; }
        public DisconnectReason? LastDisconnect { get; private set; }
        public string RemoteInstanceId { get; private set; }
        public string RemoteSessionId { get; private set; }   // client: the host's session id from Welcome
        public string FatalReason { get; private set; }       // client: why retries stopped
        public int ConnectAttempts { get; private set; }
        public int HandshakesCompleted { get; private set; }
        public int AuthFailures { get; private set; }          // host: rejected / missing join-code proofs
        public int AuthSuccesses { get; private set; }

        // Test hooks: a deliberately wrong hello, and observation/replacement of the proof a client sends.
        public Hello HelloOverride;
        public Func<byte[], byte[]> AuthResponseHook;

        // Addressing (M2/N1): host bind address (IPv4 literal) and client target (IPv4 literal or DNS name).
        public string BindAddress = "127.0.0.1";
        public string ConnectAddress = "127.0.0.1";

        // N1: HMAC key derived from the join code (JoinAuth.DeriveKey). Null = no authentication (loopback-only host).
        public byte[] JoinKey;
        public readonly AuthLimiter Limiter;

        // N1.2 host: null = gameplay-ready; otherwise the reason new joins are refused (RejectCode.HostNotReady, retryable).
        // Checked at the connection request and again right before Welcome. Null delegate = always ready (launcher dev mode).
        public Func<string> HostGate;
        // W4.2B.2: host = this session plays the isolated multiplayer test campaign (sent in Welcome); client = the host said so.
        public bool TestCampaign;
        public bool HostTestCampaign { get; private set; }
        // N1.2 client: true = send ClientReady right after a valid Welcome (launcher dev mode, reconnects). False = stop in
        // ConnState.Preparing and raise Welcomed; the owner arms save isolation and then calls ConfirmReady() or AbortPreparation().
        public bool AutoReady = true;
        public event Action Welcomed;
        public string HostWaitReason { get; private set; }   // client: last HostNotReady (host still loading); null otherwise

        // Channels: 0 = reliable lifecycle (Welcome, SceneInfo), 1 = sequenced movement snapshots.
        public const byte ChannelReliable = 0, ChannelState = 1, ChannelVehicle = 2;   // 2 = M4.1 vehicle snapshots (own sequence)

        // Post-handshake messages other than Welcome. Raised on the thread calling Tick() (Unity main thread in game).
        public event Action<NetPeer, byte, NetPacketReader> MessageReceived;
        public event Action<string> RemoteConnected;    // remote instance id
        public event Action<string> RemoteDisconnected; // reason

        public long BytesSent { get { return _net != null && _net.Statistics != null ? _net.Statistics.BytesSent : 0; } }
        public long BytesReceived { get { return _net != null && _net.Statistics != null ? _net.Statistics.BytesReceived : 0; } }
        public int LocalPort { get { return _net != null ? _net.LocalPort : 0; } }
        long _lastBytesSent, _lastBytesRecv;

        NetManager _net;
        EventBasedNetListener _listener;
        NetPeer _peer;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        long _stateSince, _retryAt, _lastLatencyLog;
        int _lastLatency = -1;
        bool _disposed;

        // host: accepted peers that have not proven the join code yet (at most one, see OnConnectionRequest)
        sealed class Pending { public NetPeer Peer; public string Ip, ClientInstance; public byte[] ClientNonce, ServerNonce; public long Since; }
        readonly Dictionary<NetPeer, Pending> _pending = new Dictionary<NetPeer, Pending>();
        readonly NonceCache _seenClientNonces = new NonceCache(512);
        // host: authenticated + welcomed peer that has not sent ClientReady yet (occupies the only slot)
        NetPeer _joining; string _joiningInstance; long _joiningSince;
        // client: per-attempt handshake state
        byte[] _clientNonce, _serverNonce;
        bool _challenged;
        // client: asynchronous DNS resolution of a host name (never blocks the main thread)
        IAsyncResult _dns;
        IPEndPoint _target;
        long _dnsResolvedAt;
        const long DnsCacheMs = 300000;

        public SessionNode(Role role, string sessionId, string instanceId, string gameVersion, int port, InstanceLog log)
        {
            Role = role; SessionId = sessionId; InstanceId = instanceId; GameVersion = gameVersion ?? ""; Port = port; _log = log;
            State = ConnState.Stopped;
            Limiter = new AuthLimiter(() => _clock.ElapsedMilliseconds);
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
        public bool AuthEnabled { get { return JoinKey != null; } }

        // Transcript of the client proof. The client learns the host instance id only from Welcome, so the client proof
        // binds the two fresh nonces and the client id; the host proof (sent in Welcome) additionally binds the host id.
        byte[] ClientProof(string clientInstance, byte[] clientNonce, byte[] serverNonce)
        {
            return JoinAuth.Proof(JoinKey, false, ProtocolInfo.ProtocolVersion, GameVersion, "", clientInstance, clientNonce, serverNonce);
        }

        byte[] HostProof(string hostInstance, string clientInstance, byte[] clientNonce, byte[] serverNonce)
        {
            return JoinAuth.Proof(JoinKey, true, ProtocolInfo.ProtocolVersion, GameVersion, hostInstance, clientInstance, clientNonce, serverNonce);
        }

        public bool Start()
        {
            IPAddress bind;
            if (Role == Role.Host)
            {
                if (!NetConfig.ValidBindAddress(BindAddress) || !IPAddress.TryParse(BindAddress, out bind))
                {
                    _log.Error("NET_START_REFUSED: invalid bind address '" + BindAddress + "' (IPv4 literal required)");
                    SetState(ConnState.Failed, "invalid bind address");
                    return false;
                }
                if (!IPAddress.IsLoopback(bind) && JoinKey == null)
                {
                    _log.Error("NET_START_REFUSED: bind " + BindAddress + " is reachable from the network but no join code was supplied. Start Host Game from the launcher.");
                    SetState(ConnState.Failed, "no join code for a network bind");
                    return false;
                }
            }
            else
            {
                if (!NetConfig.ValidConnectAddress(ConnectAddress))
                {
                    _log.Error("NET_START_REFUSED: invalid host address '" + ConnectAddress + "'");
                    SetState(ConnState.Failed, "invalid host address");
                    return false;
                }
                // Client socket: loopback when the host is local, any-interface when connecting to another PC.
                bind = NetConfig.IsLoopback(ConnectAddress) ? IPAddress.Loopback : IPAddress.Any;
            }

            _listener = new EventBasedNetListener();
            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnReceive;
            _listener.NetworkErrorEvent += OnNetworkError;
            _listener.NetworkLatencyUpdateEvent += delegate(NetPeer p, int latency) { if (p == _peer) _lastLatency = latency; };

            _net = new NetManager(_listener);
            _net.IPv6Enabled = false;
            _net.DisconnectTimeout = DisconnectTimeoutMs;
            _net.AutoRecycle = true;
            _net.UnconnectedMessagesEnabled = false;
            _net.MaxConnectAttempts = 5;
            _net.ChannelsCount = 3;
            _net.EnableStatistics = true;

            bool ok;
            try { ok = _net.Start(bind, IPAddress.IPv6None, Role == Role.Host ? Port : 0); }
            catch (Exception ex) { _log.Error("NetManager.Start threw", ex); ok = false; }

            if (!ok)
            {
                _log.Error("NET_START_FAILED: could not bind UDP " + (Role == Role.Host ? bind + ":" + Port : "client socket") + " (port in use or blocked)");
                SetState(ConnState.Failed, "bind failed");
                try { _net.Stop(); } catch { }
                _net = null;
                return false;
            }
            _log.Info("NET_STARTED role=" + Role + " bind=" + bind + " localPort=" + _net.LocalPort + (Role == Role.Client ? " target=" + ConnectAddress + ":" + Port : "")
                + " auth=" + (JoinKey != null ? "join-code" : "none (loopback)") + " protocol=" + ProtocolInfo.ProtocolVersion + " mod=" + ProtocolInfo.ModVersion + " game=" + GameVersion);
            if (Role == Role.Host) SetState(ConnState.Listening, "waiting for Player 2");
            else BeginConnect();
            return true;
        }

        void BeginConnect()
        {
            ConnectAttempts++;
            if (ConnectAttempts <= 5 || ConnectAttempts % 20 == 0)
                _log.Info("CONNECT attempt " + ConnectAttempts + " -> " + ConnectAddress + ":" + Port);
            SetState(ConnState.Connecting, null);

            IPAddress ip;
            if (NetConfig.IsIPv4Literal(ConnectAddress) && IPAddress.TryParse(ConnectAddress, out ip)) { ConnectTo(new IPEndPoint(ip, Port)); return; }
            if (_target != null && Now - _dnsResolvedAt < DnsCacheMs) { ConnectTo(_target); return; }
            try { _dns = Dns.BeginGetHostAddresses(ConnectAddress, null, null); }
            catch (Exception ex) { DnsFailed(ex.Message); }
        }

        void PollDns()
        {
            if (_dns == null || !_dns.IsCompleted) return;
            var ar = _dns; _dns = null;
            IPAddress[] all;
            try { all = Dns.EndGetHostAddresses(ar); }
            catch (Exception ex) { DnsFailed(ex.Message); return; }
            IPAddress v4 = null;
            if (all != null) foreach (var a in all) if (a.AddressFamily == AddressFamily.InterNetwork) { v4 = a; break; }
            if (v4 == null) { DnsFailed("no IPv4 address"); return; }
            if (_target == null || !_target.Address.Equals(v4)) _log.Info("DNS " + ConnectAddress + " -> " + v4);
            _target = new IPEndPoint(v4, Port);
            _dnsResolvedAt = Now;
            if (State == ConnState.Connecting) ConnectTo(_target);
        }

        void DnsFailed(string why)
        {
            _target = null;
            if (ConnectAttempts <= 5 || ConnectAttempts % 20 == 0) _log.Warn("DNS_FAILED " + ConnectAddress + ": " + why);
            _retryAt = Now + ReconnectDelayMs;
            SetState(ConnState.WaitingRetry, "name not resolved, retry in " + ReconnectDelayMs + " ms");
        }

        void ConnectTo(IPEndPoint ep)
        {
            var w = new NetDataWriter();
            var hello = HelloOverride ?? new Hello { GameVersion = GameVersion, SessionId = SessionId, InstanceId = InstanceId };
            if (HelloOverride != null && HelloOverride.ClientNonce != null && !IsZero(HelloOverride.ClientNonce)) _clientNonce = HelloOverride.ClientNonce;
            else { _clientNonce = JoinAuth.NewNonce(); hello.ClientNonce = _clientNonce; }
            _challenged = false; _serverNonce = null;
            hello.Write(w);
            _peer = _net.Connect(ep, w);
        }

        static bool IsZero(byte[] b) { foreach (var x in b) if (x != 0) return false; return true; }

        public void Tick()
        {
            if (_net == null || _disposed) return;
            try { _net.PollEvents(); }
            catch (Exception ex) { _log.Error("PollEvents threw", ex); }
            if (_net == null) return;

            long now = Now;
            if (Role == Role.Client)
            {
                PollDns();
                if (State == ConnState.WaitingRetry && now >= _retryAt) BeginConnect();
                if (State == ConnState.Handshaking && now - _stateSince > HandshakeTimeoutMs)
                {
                    _log.Error("HANDSHAKE_TIMEOUT: connected but no Welcome within " + HandshakeTimeoutMs + " ms");
                    if (_peer != null) _peer.Disconnect();
                }
            }
            else if (_pending.Count > 0)
            {
                Pending late = null;
                foreach (var p in _pending.Values) if (now - p.Since > AuthTimeoutMs) { late = p; break; }
                if (late != null)
                {
                    _pending.Remove(late.Peer);
                    Limiter.Failure(late.Ip);
                    AuthFailures++;
                    _log.Warn("AUTH_TIMEOUT " + late.Peer + " instance=" + late.ClientInstance + ": no answer to the challenge within " + AuthTimeoutMs + " ms");
                    late.Peer.Disconnect(new[] { (byte)RejectCode.AuthFailed });
                }
            }
            if (Role == Role.Host && _joining != null && now - _joiningSince > ReadyTimeoutMs)
            {
                var j = _joining; _joining = null;
                _log.Warn("READY_TIMEOUT " + j + " instance=" + _joiningInstance + ": no ClientReady within " + ReadyTimeoutMs + " ms");
                j.Disconnect();
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

        // Sends to the connected remote (client: the host; host: the authenticated client).
        public void Send(NetDataWriter w, DeliveryMethod method, byte channel)
        {
            if (!IsConnected) return;
            _peer.Send(w, channel, method);
        }

        // Host relay: send to the authenticated client unless it is `except` (MaxClients = 1). Never reaches a peer that
        // is still in the join-code challenge.
        public void SendToOthers(NetDataWriter w, DeliveryMethod method, byte channel, NetPeer except)
        {
            if (_net == null || Role != Role.Host || !IsConnected || _peer == except) return;
            _peer.Send(w, channel, method);
        }

        // ---- host side ----
        void OnConnectionRequest(ConnectionRequest req)
        {
            string ip = req.RemoteEndPoint.Address.ToString();
            string why;
            if (!Limiter.AllowRequest(ip, out why))
            {
                _log.Warn("REJECT " + req.RemoteEndPoint + " code=" + RejectCode.RateLimited + " (" + why + ")");
                req.Reject(new[] { (byte)RejectCode.RateLimited });
                return;
            }
            if (req.Data.AvailableBytes > Hello.MaxBytes)
            {
                _log.Warn("REJECT " + req.RemoteEndPoint + " code=" + RejectCode.Oversized + " (" + req.Data.AvailableBytes + " bytes)");
                Limiter.Failure(ip);
                req.Reject(new[] { (byte)RejectCode.Oversized });
                return;
            }
            Hello hello;
            RejectCode? rej = Hello.TryRead(req.Data, out hello)
                ? Validation.Check(hello, SessionId, GameVersion, InstanceId, JoinKey == null)
                : RejectCode.Malformed;
            if (rej == null && JoinKey != null && !_seenClientNonces.Add(hello.ClientNonce))
            {
                _log.Warn("REPLAY " + req.RemoteEndPoint + ": client nonce already used");
                Limiter.Failure(ip);
                rej = RejectCode.AuthFailed;
            }
            // One remote player: an authenticated client, a welcomed client preparing, or a peer in the challenge occupies the only slot.
            if (rej == null && (_net.ConnectedPeersCount >= ProtocolInfo.MaxClients || _peer != null || _joining != null || _pending.Count > 0)) rej = RejectCode.SessionFull;
            string notReady = rej == null ? GateReason() : null;
            if (notReady != null) { rej = RejectCode.HostNotReady; _log.Info("JOIN_DEFERRED " + req.RemoteEndPoint + ": host not gameplay-ready (" + notReady + ")"); }

            if (rej != null)
            {
                _log.Warn("REJECT " + req.RemoteEndPoint + " code=" + rej.Value + " (" + (byte)rej.Value + ")"
                    + (hello != null ? " client protocol=" + hello.ProtocolVersion + " mod=" + Trim(hello.ModVersion) + " game=" + Trim(hello.GameVersion) + " instance=" + Trim(hello.InstanceId) : ""));
                req.Reject(new[] { (byte)rej.Value });
                return;
            }
            var peer = req.Accept();
            peer.Tag = hello.InstanceId;
            if (JoinKey != null)
                _pending[peer] = new Pending { Peer = peer, Ip = ip, ClientInstance = hello.InstanceId, ClientNonce = hello.ClientNonce, ServerNonce = JoinAuth.NewNonce(), Since = Now };
            _log.Info("ACCEPT " + req.RemoteEndPoint + " client instance=" + hello.InstanceId + " peerId=" + peer.Id + (JoinKey != null ? " (join-code challenge pending)" : ""));
        }

        static string Trim(string s) { return s == null ? "" : s.Length > 40 ? s.Substring(0, 40) + "..." : s; }

        string GateReason()
        {
            if (HostGate == null) return null;
            try { return HostGate(); } catch (Exception ex) { _log.Error("HostGate threw (treated as not ready)", ex); return "gate error"; }
        }

        // Authenticated (or loopback) client: re-check gameplay readiness, send Welcome, then wait for ClientReady.
        void SendWelcome(NetPeer peer, string clientInstance, byte[] hostProof)
        {
            string notReady = GateReason();
            if (notReady != null)
            {
                _log.Info("JOIN_DEFERRED " + peer + " instance=" + clientInstance + ": host not gameplay-ready (" + notReady + ")");
                peer.Disconnect(new[] { (byte)RejectCode.HostNotReady });
                return;
            }
            _joining = peer; _joiningInstance = clientInstance; _joiningSince = Now;
            var w = new NetDataWriter();
            new Welcome { SessionId = SessionId, HostInstanceId = InstanceId, GameVersion = GameVersion, PlayerSlot = 2, HostProof = hostProof ?? new byte[0], TestCampaign = TestCampaign }.Write(w);
            peer.Send(w, ChannelReliable, DeliveryMethod.ReliableOrdered);
            _log.Info("WELCOME sent to " + clientInstance + " (slot 2" + (hostProof != null ? ", join code verified" : "") + "), waiting for ClientReady");
        }

        void CompleteHostHandshake(NetPeer peer)
        {
            string clientInstance = _joiningInstance;
            _joining = null;
            _peer = peer;
            RemoteInstanceId = clientInstance;
            HandshakesCompleted++;
            SetState(ConnState.Connected, "client " + RemoteInstanceId);
            _log.Info("HANDSHAKE_OK host side: " + RemoteInstanceId + " is ready (slot 2)");
            if (RemoteConnected != null) RemoteConnected(RemoteInstanceId);
        }

        // ---- both ----
        void OnPeerConnected(NetPeer peer)
        {
            if (Role == Role.Host)
            {
                Pending p;
                if (_pending.TryGetValue(peer, out p))
                {
                    var w = new NetDataWriter();
                    AuthMessages.WriteChallenge(w, p.ServerNonce);
                    peer.Send(w, ChannelReliable, DeliveryMethod.ReliableOrdered);
                    p.Since = Now;
                    _log.Info("AUTH_CHALLENGE sent to " + peer + " instance=" + p.ClientInstance);
                    return;
                }
                if (JoinKey != null) { _log.Warn("Unexpected connected peer " + peer + " without challenge state; disconnecting"); peer.Disconnect(); return; }
                SendWelcome(peer, peer.Tag as string, null);
            }
            else
            {
                SetState(ConnState.Handshaking, JoinKey != null ? "transport connected, awaiting join-code challenge" : "transport connected, awaiting Welcome");
            }
        }

        void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            byte type;
            if (!reader.TryGetByte(out type)) { _log.Warn("Empty packet from " + peer); return; }

            if (Role == Role.Host)
            {
                Pending p;
                if (_pending.TryGetValue(peer, out p))
                {
                    // Single use: the challenge is consumed by the first message, right or wrong.
                    _pending.Remove(peer);
                    byte[] proof;
                    bool ok = type == (byte)MsgType.AuthResponse && AuthMessages.TryReadResponse(reader, out proof)
                        && JoinAuth.FixedTimeEquals(proof, ClientProof(p.ClientInstance, p.ClientNonce, p.ServerNonce));
                    if (!ok)
                    {
                        AuthFailures++;
                        Limiter.Failure(p.Ip);
                        _log.Warn("AUTH_FAILED " + peer + " instance=" + p.ClientInstance + (type == (byte)MsgType.AuthResponse ? ": wrong join code" : ": message type " + type + " before authentication"));
                        peer.Disconnect(new[] { (byte)RejectCode.AuthFailed });
                        return;
                    }
                    AuthSuccesses++;
                    Limiter.Success(p.Ip);
                    _log.Info("AUTH_OK " + peer + " instance=" + p.ClientInstance);
                    SendWelcome(peer, p.ClientInstance, HostProof(InstanceId, p.ClientInstance, p.ClientNonce, p.ServerNonce));
                    return;
                }
                if (peer == _joining)
                {
                    if (type == (byte)MsgType.ClientReady && reader.AvailableBytes == 0) { CompleteHostHandshake(peer); return; }
                    _log.Warn("Message type " + type + " from " + peer + " before ClientReady; disconnecting");
                    _joining = null;
                    peer.Disconnect();
                    return;
                }
                if (peer != _peer) { _log.Warn("Message type " + type + " from unknown peer " + peer + "; disconnecting"); peer.Disconnect(); return; }
            }
            else
            {
                if (type == (byte)MsgType.AuthChallenge) { OnChallenge(peer, reader); return; }
                if (type == (byte)MsgType.Welcome) { OnWelcome(peer, reader); return; }
            }

            if (State == ConnState.Connected && peer == _peer && MessageReceived != null && type != (byte)MsgType.Welcome
                && type != (byte)MsgType.AuthChallenge && type != (byte)MsgType.AuthResponse && type != (byte)MsgType.ClientReady)
            {
                MessageReceived(peer, type, reader);
                return;
            }
            _log.Warn("Unexpected message type " + type + " from " + peer + " in state " + State);
        }

        void OnChallenge(NetPeer peer, NetPacketReader reader)
        {
            byte[] serverNonce;
            if (State != ConnState.Handshaking || _challenged || !AuthMessages.TryReadChallenge(reader, out serverNonce))
            {
                _log.Error("AUTH_PROTOCOL_ERROR: unexpected or malformed challenge in state " + State);
                peer.Disconnect();
                return;
            }
            _challenged = true;
            if (JoinKey == null)
            {
                Fatal("AUTH_REQUIRED: the host requires a join code; use Join Game (F8 menu) with the code shown on the host PC");
                peer.Disconnect();
                return;
            }
            _serverNonce = serverNonce;
            var proof = ClientProof(InstanceId, _clientNonce, serverNonce);
            if (AuthResponseHook != null) proof = AuthResponseHook(proof);
            var w = new NetDataWriter();
            AuthMessages.WriteResponse(w, proof);
            peer.Send(w, ChannelReliable, DeliveryMethod.ReliableOrdered);
            _log.Info("AUTH_RESPONSE sent");
        }

        void OnWelcome(NetPeer peer, NetPacketReader reader)
        {
            Welcome wel;
            if (State != ConnState.Handshaking || !Welcome.TryRead(reader, out wel)) { _log.Error("Malformed or unexpected Welcome in state " + State); peer.Disconnect(); return; }
            if (wel.GameVersion != GameVersion || wel.HostModVersion != ProtocolInfo.ModVersion || (JoinKey == null && wel.SessionId != SessionId))
            {
                _log.Error("WELCOME_MISMATCH host game=" + Trim(wel.GameVersion) + " mod=" + Trim(wel.HostModVersion) + (JoinKey == null ? " session=" + Trim(wel.SessionId) : ""));
                Fatal("host version or session differs");
                peer.Disconnect();
                return;
            }
            if (JoinKey != null)
            {
                bool ok = _challenged && _serverNonce != null && wel.HostProof.Length == JoinAuth.ProofBytes
                    && JoinAuth.FixedTimeEquals(wel.HostProof, HostProof(wel.HostInstanceId, InstanceId, _clientNonce, _serverNonce));
                if (!ok)
                {
                    Fatal("HOST_NOT_AUTHENTICATED: the host did not prove the join code (wrong host address, or a host started without this code)");
                    peer.Disconnect();
                    return;
                }
            }
            _serverNonce = null;
            RemoteInstanceId = wel.HostInstanceId;
            RemoteSessionId = wel.SessionId;
            HostTestCampaign = wel.TestCampaign;
            HostWaitReason = null;
            ConnectAttempts = 0;
            _log.Info("WELCOME client side: host=" + wel.HostInstanceId + " slot=" + wel.PlayerSlot + " mod=" + wel.HostModVersion + " game=" + wel.GameVersion + (wel.TestCampaign ? " MULTIPLAYER TEST CAMPAIGN" : "") + (JoinKey != null ? " (host proved the join code)" : ""));
            if (AutoReady) { SendReady(); return; }
            SetState(ConnState.Preparing, "host " + wel.HostInstanceId + " welcomed us; preparing before ClientReady");
            if (Welcomed != null) Welcomed();
        }

        void SendReady()
        {
            var w = new NetDataWriter();
            w.Put((byte)MsgType.ClientReady);
            _peer.Send(w, ChannelReliable, DeliveryMethod.ReliableOrdered);
            HandshakesCompleted++;
            SetState(ConnState.Connected, "host " + RemoteInstanceId);
            _log.Info("HANDSHAKE_OK client side: ClientReady sent to host=" + RemoteInstanceId);
            if (RemoteConnected != null) RemoteConnected(RemoteInstanceId);
        }

        // N1.2: the owner finished its preparation (save isolation armed and verified, client systems built).
        // Later reconnects of this node send ClientReady automatically.
        public bool ConfirmReady()
        {
            if (Role != Role.Client || State != ConnState.Preparing || _peer == null) return false;
            AutoReady = true;
            SendReady();
            return true;
        }

        // N1.2: preparation failed (e.g. save isolation not verifiable): disconnect without retry.
        public void AbortPreparation(string why)
        {
            if (Role != Role.Client) return;
            Fatal("PREPARATION_FAILED: " + why);
            if (_peer != null) _peer.Disconnect(); else SetState(ConnState.Failed, why);
        }

        void Fatal(string why)
        {
            FatalReason = why;
            _log.Error("CONNECT_FAILED_FATAL: " + why + "; not retrying.");
        }

        void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            string extra = "";
            RejectCode? code = null;
            if ((info.Reason == DisconnectReason.ConnectionRejected || info.Reason == DisconnectReason.RemoteConnectionClose)
                && info.AdditionalData != null && info.AdditionalData.AvailableBytes == 1)
            {
                code = (RejectCode)info.AdditionalData.GetByte();
                extra = " rejectCode=" + code.Value + " (" + (byte)code.Value + ")";
            }
            if (info.SocketErrorCode != SocketError.Success) extra += " socketError=" + info.SocketErrorCode + " (" + (int)info.SocketErrorCode + ")";

            if (Role == Role.Host)
            {
                _pending.Remove(peer);
                if (peer == _joining) _joining = null;
                _log.Warn("DISCONNECT peer=" + peer + " reason=" + info.Reason + extra);
                if (peer == _peer)
                {
                    if (State == ConnState.Connected && RemoteDisconnected != null) RemoteDisconnected(info.Reason.ToString());
                    _peer = null; RemoteInstanceId = null;
                }
                if (_net != null && _peer == null) SetState(ConnState.Listening, "waiting for Player 2");
                return;
            }

            LastDisconnect = info.Reason;
            if (code.HasValue) LastReject = code;
            bool quietRetry = info.Reason == DisconnectReason.ConnectionFailed && ConnectAttempts > 5 && ConnectAttempts % 20 != 0;
            if (!quietRetry) _log.Warn("DISCONNECT peer=" + peer + " reason=" + info.Reason + extra);
            if (State == ConnState.Connected && peer == _peer && RemoteDisconnected != null) RemoteDisconnected(info.Reason.ToString());

            _peer = null;
            _serverNonce = null;
            if (_disposed) return;
            if (FatalReason != null) { SetState(ConnState.Failed, FatalReason); return; }
            if (code.HasValue && Validation.IsFatal(code.Value))
            {
                FatalReason = "host rejected the connection with " + code.Value;
                _log.Error("CONNECT_FAILED_FATAL: " + FatalReason + "; not retrying." + (code.Value == RejectCode.AuthFailed
                    ? " Check the join code (a new code is generated each time the host starts)." : " Restart both instances via the launcher."));
                SetState(ConnState.Failed, "rejected " + code.Value);
                return;
            }
            HostWaitReason = code == RejectCode.HostNotReady ? "host not gameplay-ready" : null;
            int delay = code == RejectCode.RateLimited ? RateLimitedDelayMs : code == RejectCode.HostNotReady ? HostNotReadyDelayMs : ReconnectDelayMs;
            _retryAt = Now + delay;
            SetState(ConnState.WaitingRetry, "retry in " + delay + " ms");
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
            _pending.Clear();
            _joining = null;
            State = ConnState.Stopped;
            _log.Info("STATE -> Stopped (disposed)");
        }
    }
}
