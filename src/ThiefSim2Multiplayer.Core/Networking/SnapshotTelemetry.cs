using System;

namespace ThiefSim2Multiplayer.Core.Networking
{
    public struct ArrivalStatistics
    {
        public int Samples;
        public double MeanMilliseconds, MinimumMilliseconds, MaximumMilliseconds, JitterMilliseconds;
    }

    // Fixed rolling window; records accepted snapshots, never packet payloads or log strings.
    public sealed class SnapshotTelemetry
    {
        private readonly double[] intervals = new double[128];
        private int count, next;
        private double lastArrival;
        private bool hasPrevious;
        public long Sent { get; internal set; }
        public long Accepted { get; private set; }
        public long Rejected { get; internal set; }
        public long SequenceGaps { get; private set; }
        public bool HasPose { get; private set; }
        public PlayerTransform Latest { get; private set; }
        public bool Record(PlayerTransform pose, double arrival)
        {
            if (!Protocol.ValidPose(pose) || double.IsNaN(arrival) || double.IsInfinity(arrival) || arrival < 0 ||
                (hasPrevious && (arrival < lastArrival || !Protocol.IsNewer(pose.Sequence, Latest.Sequence)))) return false;
            if (hasPrevious)
            {
                double interval = arrival - lastArrival;
                intervals[next] = interval; next = (next + 1) % intervals.Length;
                if (count < intervals.Length) count++;
                uint delta = unchecked(pose.Sequence - Latest.Sequence);
                if (delta > 1 && delta < 0x80000000u) SequenceGaps += delta - 1;
            }
            hasPrevious = HasPose = true; Latest = pose; lastArrival = arrival; Accepted++; return true;
        }
        public ArrivalStatistics Statistics
        {
            get
            {
                var result = new ArrivalStatistics { Samples = count };
                if (count == 0) return result;
                double sum = 0, min = double.MaxValue, max = 0;
                for (int i = 0; i < count; i++) { double value = intervals[i]; sum += value; min = Math.Min(min, value); max = Math.Max(max, value); }
                double mean = sum / count, variance = 0;
                for (int i = 0; i < count; i++) { double delta = intervals[i] - mean; variance += delta * delta; }
                result.MeanMilliseconds = mean * 1000; result.MinimumMilliseconds = min * 1000; result.MaximumMilliseconds = max * 1000;
                result.JitterMilliseconds = Math.Sqrt(variance / count) * 1000; return result;
            }
        }
        public double ReceiveAge(double now) { return HasPose ? Math.Max(0, now - lastArrival) : -1; }
        internal void ResetTimeline() { count = next = 0; hasPrevious = HasPose = false; }
        internal void Reset() { ResetTimeline(); Sent = Accepted = Rejected = SequenceGaps = 0; Latest = default(PlayerTransform); }
    }

}
