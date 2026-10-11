using LiteNetLib;

namespace ThiefSim2Multiplayer.Core.Networking
{
    internal sealed class NetworkPeer
    {
        internal NetPeer Transport;
        internal string Name;
        internal string ModVersion = "";
        internal byte AvatarId;
        internal bool Ready;
        internal string SceneKey = "";
        internal uint SceneEpoch;
        internal double RateWindow;
        internal int ReceivedInWindow;
    }
}
