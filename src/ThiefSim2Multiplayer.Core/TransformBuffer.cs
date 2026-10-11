using System;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Core
{
    public sealed class TransformBuffer
    {
        private struct Sample { public PlayerTransform Pose; public double Arrival; }
        private readonly Sample[] samples = new Sample[32];
        private int start, count;
        private Sample At(int index) => samples[(start+index)%samples.Length];
        public int Count { get { return count; } }
        public double LastArrival { get; private set; }
        private uint sequence;
        private bool hasSequence;
        private uint lastTick;
        private double senderTime, offset, rendered;
        public uint LastSequence { get { return sequence; } }
        public float SnapDistance { get; set; } = 10;
        public int SnapCount { get; private set; }

        public bool Add(PlayerTransform pose, double arrival)
        {
            if (!Protocol.ValidPose(pose) || double.IsNaN(arrival) || double.IsInfinity(arrival) || arrival < 0 || (hasSequence && !Protocol.IsNewer(pose.Sequence, sequence))) return false;
            if (count > 0 && arrival < LastArrival) return false;
            if (count > 0)
            {
                PlayerTransform previous = At(count - 1).Pose;
                double x = pose.X - previous.X, y = pose.Y - previous.Y, z = pose.Z - previous.Z;
                double threshold = Protocol.Finite(SnapDistance) && SnapDistance > 0 ? SnapDistance : 10;
                if (x * x + y * y + z * z > threshold * threshold || previous.State.Vehicle.Seat != pose.State.Vehicle.Seat || previous.State.Vehicle.PresentationKey != pose.State.Vehicle.PresentationKey) { start=count=0; SnapCount++; }
            }
            if(count==0) {senderTime=0;offset=arrival;rendered=0;}
            else {
                int elapsed=unchecked((int)(pose.Tick-lastTick));
                // Sender spacing survives packets delivered together. Equal legacy ticks
                // retain arrival spacing; epoch/snap resets discard the old clock anchor.
                senderTime=elapsed>0 && elapsed<2000 ? senderTime+elapsed*.001 : Math.Max(senderTime,arrival-offset);
                offset=Math.Min(offset,arrival-senderTime);
            }
            lastTick=pose.Tick;
            hasSequence = true; sequence = pose.Sequence; LastArrival = arrival;
            if (count == samples.Length) { start=(start+1)%samples.Length; count--; }
            samples[(start+count++)%samples.Length]=new Sample { Pose = pose, Arrival = senderTime }; return true;
        }
        public bool TrySample(double now, double delay, out PlayerTransform pose)
        {
            pose = default(PlayerTransform);
            if (count == 0 || double.IsNaN(now) || double.IsInfinity(now) || double.IsNaN(delay) || double.IsInfinity(delay) || now < LastArrival || now - LastArrival > 1) return false;
            double target = now - offset - Math.Max(0, delay);
            rendered=Math.Max(rendered,Math.Min(senderTime,target)); target=rendered;
            while (count > 2 && At(1).Arrival <= target) { start=(start+1)%samples.Length; count--; }
            Sample first = At(0); pose = first.Pose;
            if (count == 1 || target <= first.Arrival) return true;
            Sample next = At(1);
            float amount = next.Arrival == first.Arrival ? 1 : (float)Math.Max(0, Math.Min(1, (target - first.Arrival) / (next.Arrival - first.Arrival)));
            pose.X = first.Pose.X + (next.Pose.X - first.Pose.X) * amount;
            pose.Y = first.Pose.Y + (next.Pose.Y - first.Pose.Y) * amount;
            pose.Z = first.Pose.Z + (next.Pose.Z - first.Pose.Z) * amount;
            pose.Yaw = InterpolateYaw(first.Pose.Yaw, next.Pose.Yaw, amount);
            // Discrete equipment/locomotion belongs to the same delayed snapshot timeline.
            // At the endpoint, do not retain an old semantic state while holding the newest position.
            if (amount >= 1) pose.State = next.Pose.State;
            else if ((first.Pose.State.Known & next.Pose.State.Known & SampledFields.GroundFeet) != 0)
                pose.State.GroundFeet = first.Pose.State.GroundFeet + (next.Pose.State.GroundFeet - first.Pose.State.GroundFeet) * amount;
            if ((first.Pose.State.Known & next.Pose.State.Known & SampledFields.Lean) != 0) {
                pose.State.LeanOffset = first.Pose.State.LeanOffset + (next.Pose.State.LeanOffset - first.Pose.State.LeanOffset) * amount;
                pose.State.LeanRoll = first.Pose.State.LeanRoll + (next.Pose.State.LeanRoll - first.Pose.State.LeanRoll) * amount;
            }
            return true;
        }
        public void Clear() { start=count=0; hasSequence = false; sequence = lastTick = 0; LastArrival = senderTime = offset = rendered = 0; SnapCount = 0; }
        public static float InterpolateYaw(float from, float to, float amount)
        {
            float delta = ((to - from + 540) % 360) - 180;
            return ((from + delta * Math.Max(0, Math.Min(1, amount))) % 360 + 360) % 360;
        }
    }
}
