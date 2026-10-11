using System;

namespace ThiefSim2Multiplayer.Core
{
    public enum LocomotionState : byte { Idle, Walk, Run, CrouchIdle, CrouchMove, Airborne }
    public enum PlayerActionState : byte { Unknown }
    [Flags]
    public enum SampledFields : byte { None = 0, Velocity = 1, Grounded = 2, Crouched = 4, Sprinting = 8, Equipment = 16, LookPitch = 32, GroundFeet = 64, Lean = 128 }
    public enum SeatRole : byte { None, Driver, Passenger }
    public enum SeatEligibility : byte { Unknown, Eligible, Ineligible }
    public struct VehicleOccupancyState
    {
        public SeatRole Seat;
        public ulong PresentationKey; // scene-local authored CarController identity, never physics authority.
        public bool IsInVehicle => Seat == SeatRole.Driver || Seat == SeatRole.Passenger;
    }
    public static class GroundingPresentation
    {
        public static float Offset(float standingFeet, PlayerReplicationState state)
        { return !state.Vehicle.IsInVehicle && (state.Known & SampledFields.GroundFeet) != 0 ? state.GroundFeet - standingFeet : 0; }
    }

    // Position and BodyYaw live in the enclosing sequenced PlayerTransform envelope.
    // A missing SampledFields bit means unknown, never a guessed gameplay value.
    public struct PlayerReplicationState
    {
        public float VelocityX, VelocityY, VelocityZ, LookPitch;
        public SampledFields Known;
        public bool Grounded, Crouched, Sprinting;
        public LocomotionState Locomotion;
        public PlayerActionState Action;
        public short EquippedItemId; // -1 = verified none, 0 = crowbar; ignored unless Equipment is known.
        public float GroundFeet;
        public float LeanOffset, LeanRoll;
        public VehicleOccupancyState Vehicle;
        public SeatEligibility SeatEligibility;
        public float PlanarSpeed { get { return (float)Math.Sqrt(VelocityX * VelocityX + VelocityZ * VelocityZ); } }
        public float VerticalVelocity { get { return VelocityY; } }
        public float ForwardVelocity(float bodyYaw) { double a = bodyYaw * Math.PI / 180; return (float)(VelocityX * Math.Sin(a) + VelocityZ * Math.Cos(a)); }
        public float RightVelocity(float bodyYaw) { double a = bodyYaw * Math.PI / 180; return (float)(VelocityX * Math.Cos(a) - VelocityZ * Math.Sin(a)); }
    }

    public sealed class LocomotionSelector
    {
        private bool moving, running;
        private readonly float idleEnter, idleExit, runEnter, runExit;
        public LocomotionSelector(float stopSpeed, float startSpeed, float startRunSpeed, float stopRunSpeed)
        {
            if (stopSpeed < 0 || startSpeed <= stopSpeed || stopRunSpeed <= startSpeed || startRunSpeed <= stopRunSpeed ||
                !Networking.Protocol.Finite(stopSpeed + startSpeed + startRunSpeed + stopRunSpeed)) throw new ArgumentException("Invalid calibrated locomotion thresholds.");
            idleEnter = stopSpeed; idleExit = startSpeed; runEnter = startRunSpeed; runExit = stopRunSpeed;
        }
        public LocomotionState Select(PlayerReplicationState state)
        {
            float speed = state.PlanarSpeed;
            if (!Networking.Protocol.Finite(speed)) { Reset(); return LocomotionState.Idle; }
            moving = speed > (moving ? idleEnter : idleExit);
            running = speed > (running ? runExit : runEnter);
            if ((state.Known & SampledFields.Grounded) != 0 && !state.Grounded) return LocomotionState.Airborne;
            if ((state.Known & SampledFields.Crouched) != 0 && state.Crouched) return moving ? LocomotionState.CrouchMove : LocomotionState.CrouchIdle;
            if (!moving) return LocomotionState.Idle;
            // Actual motor state takes precedence over speed (exhaustion/carrying can slow a sprint).
            if ((state.Known & SampledFields.Sprinting) != 0) return state.Sprinting ? LocomotionState.Run : LocomotionState.Walk;
            return running ? LocomotionState.Run : LocomotionState.Walk;
        }
        public void Reset() { moving = running = false; }
    }

    // Use verified motor booleans when available; velocity thresholds need actual calibration.
    // Hold a disappearing movement flag briefly so a one-frame pause cannot restart a crossfade.
    public sealed class MotorLocomotionSelector
    {
        private double lastMoving = double.NegativeInfinity;
        public LocomotionState Select(PlayerReplicationState state, bool moving, double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) { Reset(); return LocomotionState.Idle; }
            if (now < lastMoving) Reset();
            if (moving) lastMoving = now;
            bool heldMoving = moving || now - lastMoving < 0.12;
            if ((state.Known & SampledFields.Grounded) != 0 && !state.Grounded) return LocomotionState.Airborne;
            if ((state.Known & SampledFields.Crouched) != 0 && state.Crouched) return heldMoving ? LocomotionState.CrouchMove : LocomotionState.CrouchIdle;
            return !heldMoving ? LocomotionState.Idle : (state.Known & SampledFields.Sprinting) != 0 && state.Sprinting ? LocomotionState.Run : LocomotionState.Walk;
        }
        public void Reset() { lastMoving = double.NegativeInfinity; }
    }

    public static class AvatarCatalog
    {
        public static bool IsSupported(byte avatarId) { return avatarId <= 1; }
    }

    // Shared lifecycle decision boundary, independent of Unity assets.
    public sealed class EquipmentPresentationState
    {
        public short Item { get; private set; } = -1;
        public bool Change(PlayerReplicationState state)
        {
            short next = (state.Known & SampledFields.Equipment) != 0 && state.EquippedItemId == 0 ? (short)0 : (short)-1;
            if (next == Item) return false;
            Item = next; return true;
        }
        public void Reset() { Item = -1; }
    }
}
