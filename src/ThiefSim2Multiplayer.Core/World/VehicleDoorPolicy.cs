using System;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Core.World
{
    public static class VehicleDoorPolicy
    {
        public const float MinimumSideOffset = 0.35f;
        public const float MaximumSideOffset = 2.25f;
        public const float MaximumRearRowOffset = 1.5f;
        public const float MaximumVerticalOffset = 1.25f;
        public const float MaximumSeatClearance = 2.25f;
        public const float NativeInteractionReach = 2.25f;

        public static bool WithinNativeInteractionReach(float squaredDistance)
            => Protocol.Finite(squaredDistance) && squaredDistance >= 0 && squaredDistance <= NativeInteractionReach * NativeInteractionReach;

        public static string StableIdentity(string vehicleSlot, string nativeDoorPath, int componentOrdinal)
        {
            if ((vehicleSlot != "VehiclesManager.firstCar" && vehicleSlot != "VehiclesManager.van") || string.IsNullOrWhiteSpace(nativeDoorPath) || componentOrdinal < 0)
                throw new ArgumentException("A manager vehicle slot and native relative door path are required.");
            return "vehicle-door-v1:" + vehicleSlot.Length + ":" + vehicleSlot + nativeDoorPath.Length + ":" + nativeDoorPath + ":" + componentOrdinal;
        }

        public static bool IsRearPassengerDoor(bool supportedVehicle, bool nativeFrontEntryDoor, bool hasEnabledCollider,
            float sideOffset, float rearRowOffset, float verticalOffset, float colliderSeatClearance)
        {
            if (!supportedVehicle || nativeFrontEntryDoor || !hasEnabledCollider ||
                !Protocol.Finite(sideOffset) || !Protocol.Finite(rearRowOffset) || !Protocol.Finite(verticalOffset) || !Protocol.Finite(colliderSeatClearance))
                return false;
            return sideOffset >= MinimumSideOffset && sideOffset <= MaximumSideOffset &&
                rearRowOffset >= 0 && rearRowOffset <= MaximumRearRowOffset &&
                verticalOffset >= 0 && verticalOffset <= MaximumVerticalOffset &&
                colliderSeatClearance >= 0 && colliderSeatClearance <= MaximumSeatClearance;
        }

        public static bool RequiresDoorTarget(SeatRole role, SeatAction action) => role == SeatRole.Passenger && action == SeatAction.Enter;

        public static bool MatchesVehicleTarget(EntityRef requestedVehicle, EntityRef doorOwnerVehicle, bool registeredRearPassengerDoor)
            => requestedVehicle.Id.IsValid && requestedVehicle.Generation != 0 && doorOwnerVehicle.Id.IsValid &&
                requestedVehicle.Equals(doorOwnerVehicle) && registeredRearPassengerDoor;
    }
}
