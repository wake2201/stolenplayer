using ThiefSim2Multiplayer.Core;

namespace ThiefSim2Multiplayer.Game
{
    internal static class VehicleMultiplayerPolicy
    {
        internal static bool PassThroughNativeEntry(bool cooperative, bool authorizedApply) => !cooperative || authorizedApply;
        internal static SeatRole NativeEntryRole => SeatRole.Driver;
        internal static bool UsesPassengerPresentation(SeatRole role) => role == SeatRole.Passenger;
        internal static bool ShowPassengerPrompt(SeatRole availableRole, bool vanillaPromptVisible, bool nativeEntryThisFrame) =>
            UsesPassengerPresentation(availableRole) && !vanillaPromptVisible && !nativeEntryThisFrame;
        internal static bool ShouldConsumePassengerEntryInput(bool requestQueued, bool sameRequestPending) => requestQueued || sameRequestPending;
        internal static bool MeetsSeatSpeedGate(SeatRole role, float speed) => role == SeatRole.Driver || speed < 5f;
        internal static bool LocalPeerSimulates(bool host, byte localActor, byte confirmedDriver) =>
            host && confirmedDriver == 0 || confirmedDriver == localActor;
    }
}
