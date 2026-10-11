namespace ThiefSim2Multiplayer.Core.Networking
{
    public enum MessageType : byte
    {
        HandshakeRequest = 1, HandshakeAccepted = 2, HandshakeRejected = 3,
        PlayerTransform = 4, SceneState = 5, PlayerLeft = 6, PlayerState = 7
    }

    public sealed class Message
    {
        public MessageType Type;
        public ushort ProtocolVersion;
        public string ModVersion = "", PlayerName = "", BuildFingerprint = "", SceneKey = "", Reason = "";
        public byte LocalPlayerId, RemotePlayerId;
        public byte AvatarId;
        public ulong SessionId;
        public bool Ready;
        public uint SceneEpoch;
        public PlayerTransform Transform;
    }

    public struct PlayerTransform
    {
        public byte PlayerId;
        public uint Sequence, Tick, SceneEpoch;
        public float X, Y, Z, Yaw;
        public PlayerReplicationState State;
    }
}
