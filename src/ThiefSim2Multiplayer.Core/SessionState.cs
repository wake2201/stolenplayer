namespace ThiefSim2Multiplayer.Core
{
    public enum SessionState { Offline, StartingHost, Hosting, Connecting, Connected, Disconnecting }

    public sealed class MultiplayerSession
    {
        public SessionState State { get; private set; }
        public bool IsHost { get; private set; }
        public byte LocalPlayerId { get; private set; }
        public byte RemotePlayerId { get; private set; }
        public ulong SessionId { get; private set; }

        public bool BeginHost()
        {
            if (State != SessionState.Offline) return false;
            IsHost = true; State = SessionState.StartingHost; return true;
        }
        public void HostStarted()
        {
            if (State != SessionState.StartingHost) throw new System.InvalidOperationException();
            LocalPlayerId = 1; State = SessionState.Hosting;
        }
        public bool BeginConnect()
        {
            if (State != SessionState.Offline) return false;
            IsHost = false; State = SessionState.Connecting; return true;
        }
        public void Accept(ulong sessionId)
        {
            if (sessionId == 0 || !(IsHost ? State == SessionState.Hosting : State == SessionState.Connecting))
                throw new System.InvalidOperationException();
            SessionId = sessionId; LocalPlayerId = IsHost ? (byte)1 : (byte)2;
            RemotePlayerId = IsHost ? (byte)2 : (byte)1; State = SessionState.Connected;
        }
        public void PeerLost()
        {
            RemotePlayerId = 0; SessionId = 0;
            if (IsHost && (State == SessionState.Hosting || State == SessionState.Connected)) State = SessionState.Hosting;
            else Stop();
        }
        public void BeginDisconnect() { if (State != SessionState.Offline) State = SessionState.Disconnecting; }
        public void Stop()
        {
            State = SessionState.Offline; LocalPlayerId = RemotePlayerId = 0; SessionId = 0; IsHost = false;
        }
    }
}
