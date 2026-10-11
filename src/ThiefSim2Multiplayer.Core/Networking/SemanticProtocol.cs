using System;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public static partial class Protocol
    {
        public const int StateBytes = 74;
        public static bool ValidState(PlayerReplicationState state)
        {
            return (byte)state.Locomotion <= (byte)LocomotionState.Airborne && state.Action == PlayerActionState.Unknown &&
                Finite(state.LeanOffset) && Math.Abs(state.LeanOffset) <= 1 && Finite(state.LeanRoll) && Math.Abs(state.LeanRoll) <= 12 &&
                ((state.Known & SampledFields.Lean) != 0 || state.LeanOffset == 0 && state.LeanRoll == 0) &&
                Finite(state.GroundFeet) && Math.Abs(state.GroundFeet) <= 4 && ((state.Known & SampledFields.GroundFeet) != 0 || state.GroundFeet == 0) &&
                (byte)state.SeatEligibility <= 2 && (byte)state.Vehicle.Seat <= 2 && (state.Vehicle.IsInVehicle ? state.Vehicle.PresentationKey != 0 : state.Vehicle.PresentationKey == 0) &&
                Finite(state.VelocityX) && Finite(state.VelocityY) && Finite(state.VelocityZ) && Math.Abs(state.VelocityX) <= 100 && Math.Abs(state.VelocityY) <= 100 && Math.Abs(state.VelocityZ) <= 100 &&
                Finite(state.LookPitch) && Math.Abs(state.LookPitch) <= 80 && state.EquippedItemId >= -1 && state.EquippedItemId <= 451 &&
                ((state.Known & SampledFields.Velocity) != 0 || (state.VelocityX == 0 && state.VelocityY == 0 && state.VelocityZ == 0)) &&
                ((state.Known & SampledFields.LookPitch) != 0 || state.LookPitch == 0) &&
                ((state.Known & SampledFields.Grounded) != 0 || !state.Grounded) &&
                ((state.Known & SampledFields.Crouched) != 0 || !state.Crouched) &&
                ((state.Known & SampledFields.Sprinting) != 0 || !state.Sprinting);
        }
        public static bool WriteState(PlayerTransform pose, byte[] bytes, int offset = 0)
        {
            if (!ValidPose(pose) || !ValidState(pose.State) || bytes == null || offset < 0 || offset > bytes.Length - StateBytes) return false;
            WriteTransform(pose, bytes, offset); bytes[offset] = (byte)MessageType.PlayerState;
            var s = pose.State;
            WriteFloat(bytes, offset + 30, s.VelocityX); WriteFloat(bytes, offset + 34, s.VelocityY); WriteFloat(bytes, offset + 38, s.VelocityZ);
            WriteFloat(bytes, offset + 42, s.LookPitch); bytes[offset + 46] = (byte)s.Known;
            bytes[offset + 47] = (byte)((s.Grounded ? 1 : 0) | (s.Crouched ? 2 : 0) | (s.Sprinting ? 4 : 0));
            bytes[offset + 48] = (byte)s.Locomotion; bytes[offset + 49] = (byte)s.Action;
            bytes[offset + 50] = (byte)s.EquippedItemId; bytes[offset + 51] = (byte)(s.EquippedItemId >> 8);
            WriteFloat(bytes, offset + 52, s.GroundFeet); bytes[offset + 56] = (byte)s.Vehicle.Seat;
            for (int i = 0; i < 8; i++) bytes[offset + 57 + i] = (byte)(s.Vehicle.PresentationKey >> (8*i)); bytes[offset + 65] = (byte)s.SeatEligibility;
            WriteFloat(bytes, offset + 66, s.LeanOffset); WriteFloat(bytes, offset + 70, s.LeanRoll); return true;
        }
        public static bool TryReadState(byte[] bytes, int offset, int count, out PlayerTransform pose)
        {
            pose = default(PlayerTransform);
            if (bytes == null || count != StateBytes || offset < 0 || offset > bytes.Length - count || bytes[offset] != (byte)MessageType.PlayerState || bytes[offset + 47] > 7) return false;
            pose = new PlayerTransform { PlayerId = bytes[offset + 1], SceneEpoch = ReadUInt32(bytes, offset + 2), Sequence = ReadUInt32(bytes, offset + 6), Tick = ReadUInt32(bytes, offset + 10),
                X = ReadFloat(bytes, offset + 14), Y = ReadFloat(bytes, offset + 18), Z = ReadFloat(bytes, offset + 22), Yaw = ReadFloat(bytes, offset + 26),
                State = new PlayerReplicationState { VelocityX = ReadFloat(bytes, offset + 30), VelocityY = ReadFloat(bytes, offset + 34), VelocityZ = ReadFloat(bytes, offset + 38), LookPitch = ReadFloat(bytes, offset + 42),
                    Known = (SampledFields)bytes[offset + 46], Grounded = (bytes[offset + 47] & 1) != 0, Crouched = (bytes[offset + 47] & 2) != 0, Sprinting = (bytes[offset + 47] & 4) != 0,
                    Locomotion = (LocomotionState)bytes[offset + 48], Action = (PlayerActionState)bytes[offset + 49], EquippedItemId = (short)(bytes[offset + 50] | bytes[offset + 51] << 8) } };
            pose.State.GroundFeet = ReadFloat(bytes, offset + 52); pose.State.Vehicle.Seat = (SeatRole)bytes[offset + 56];
            pose.State.SeatEligibility = (SeatEligibility)bytes[offset + 65];
            pose.State.LeanOffset = ReadFloat(bytes, offset + 66); pose.State.LeanRoll = ReadFloat(bytes, offset + 70);
            for (int i = 0; i < 8; i++) pose.State.Vehicle.PresentationKey |= (ulong)bytes[offset + 57 + i] << (8*i);
            if (ValidPose(pose) && ValidState(pose.State)) return true;
            pose = default(PlayerTransform); return false;
        }
    }
}
