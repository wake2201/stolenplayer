using BepInEx.Configuration;

namespace ThiefSim2Multiplayer
{
    internal sealed class MultiplayerConfig
    {
        internal readonly ConfigEntry<int> Port, SendRate, InterpolationMilliseconds, AvatarId;
        internal readonly ConfigEntry<string> Address, Name;
        internal readonly ConfigEntry<string> InstanceName;
        internal readonly ConfigEntry<string> PersistentPlayerId;
        internal readonly ConfigEntry<string> ManualSessionId;
        internal readonly ConfigEntry<bool> VerboseTransforms, DevelopmentControls, DevelopmentWorldFixture;
        internal readonly ConfigEntry<float> SnapDistance, VisualHeightOffset, VisualYawOffset;
        internal readonly ConfigEntry<bool> FrameProfiling;
        internal readonly ConfigEntry<float> FrameSpikeMilliseconds, NetworkBudgetMilliseconds;
        internal MultiplayerConfig(ConfigFile config)
        {
            PersistentPlayerId = config.Bind("Identity", "PersistentCoopPlayerId", System.Guid.NewGuid().ToString("D"), "Personal identity used across reconnects; inventory persistence is not implemented.");
            ManualSessionId = config.Bind("Diagnostics", "ManualSessionId", "", "Local manual acceptance session ID; Ctrl+F10 mark, Ctrl+F7 snapshot, Ctrl+F6 bounded profiler.");
            DevelopmentWorldFixture = config.Bind("Development", "WorldFixture", false, "Managed synthetic world entities only; never touches game objects or saves.");
            Port = config.Bind("Network", "Port", 9050, "UDP port, 1..65535.");
            Address = config.Bind("Network", "Address", "127.0.0.1", "Host IPv4 address for direct joining.");
            Name = config.Bind("Network", "PlayerName", "Player", "Name (at most 48 UTF-8 bytes).");
            AvatarId = config.Bind("Visuals", "AvatarId", 0, "Civilian definition: 0 male, 1 female. Applied before connecting.");
            SendRate = config.Bind("Transforms", "SendRate", 20, "Packets per second (clamped 1..60).");
            InterpolationMilliseconds = config.Bind("Transforms", "InterpolationMilliseconds", 100, "Interpolation delay (clamped 0..500 ms).");
            SnapDistance = config.Bind("Transforms", "SnapDistance", 10f, "Snap on a received positional jump larger than this many metres (clamped 1..1000).");
            VisualHeightOffset = config.Bind("Visuals", "HeightOffset", 0f, "Local visual correction in metres; replicated root position is unchanged.");
            VisualYawOffset = config.Bind("Visuals", "YawOffset", 0f, "Local model yaw correction in degrees; protocol yaw is unchanged.");
            InstanceName = config.Bind("Diagnostics", "InstanceName", "Local", "Local development label in logs only, never network identity.");
            VerboseTransforms = config.Bind("Diagnostics", "VerboseTransforms", false, "Opt-in position/yaw and arrival summary, at most once per second.");
            DevelopmentControls = config.Bind("Diagnostics", "DevelopmentControls", false, "Show controlled two-second adapter-unavailability test in F8.");
            FrameProfiling=config.Bind("Diagnostics","FrameProfiling",true,"Bounded in-memory frame/phase rings; export explicitly, no per-frame logs.");
            FrameSpikeMilliseconds=config.Bind("Diagnostics","FrameSpikeMilliseconds",100f,"Retain recent phase timeline above this unscaled frame duration.");
            NetworkBudgetMilliseconds=config.Bind("Network","ProcessingBudgetMilliseconds",2f,"Owner-thread callback budget, 0.1..10 ms; atomic callbacks complete and all deferred reliable events remain queued.");
        }
    }
}
