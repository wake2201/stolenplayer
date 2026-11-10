// Interpolation buffer for remote player snapshots. Unity-free; time is passed in by the caller (seconds, monotonic).
// Render time = local now - estimated clock offset - interpolation delay. Samples between the two bracketing
// snapshots; holds the newest one when data runs out (extrapolation is bounded by MaxExtrapolation, default 0).
using System;
using System.Collections.Generic;

namespace TS2MP.Shared
{
    public struct RemoteSample
    {
        public Vec3 Pos;
        public float Yaw;
        public float HorizontalSpeed; // m/s, derived from the bracketing snapshots
        public MoveFlags Flags;
        public bool Snapped;          // true when a teleport/gap was crossed (no interpolation)
    }

    public sealed class SnapshotBuffer
    {
        public double InterpolationDelay = 0.1;   // seconds
        public int Capacity = 32;
        public double MaxExtrapolation = 0.0;     // seconds; 0 = hold last state
        public float TeleportDistance = 8f;       // metres between consecutive snapshots
        public double MaxGap = 1.0;               // seconds between consecutive snapshots before snapping

        struct Entry { public double T; public PlayerSnapshot S; }
        readonly List<Entry> _entries = new List<Entry>();
        double _offset; bool _hasOffset;
        public uint LastSeq { get; private set; }
        public bool HasData { get { return _entries.Count > 0; } }
        public int Count { get { return _entries.Count; } }
        public int StaleDropped { get; private set; }

        public void Clear() { _entries.Clear(); _hasOffset = false; }

        // Returns false if the snapshot is stale (not newer than the newest buffered one).
        public bool Add(PlayerSnapshot s, double localNow)
        {
            if (_entries.Count > 0 && !SnapshotValidation.IsNewer(s.Seq, LastSeq)) { StaleDropped++; return false; }
            double t = s.TimeMs / 1000.0;
            if (_entries.Count > 0 && t <= _entries[_entries.Count - 1].T)
            {
                // Sender clock went backwards (sender restarted): start over.
                _entries.Clear(); _hasOffset = false;
            }
            double sample = localNow - t;
            if (!_hasOffset) { _offset = sample; _hasOffset = true; }
            else if (sample < _offset) _offset = sample;                // faster packet: lower bound of latency
            else _offset += (sample - _offset) * 0.02;                  // drift slowly upwards (clock drift, latency rise)
            _entries.Add(new Entry { T = t, S = s });
            LastSeq = s.Seq;
            while (_entries.Count > Capacity) _entries.RemoveAt(0);
            return true;
        }

        public double RenderTime(double localNow) { return localNow - _offset - InterpolationDelay; }

        public bool TrySample(double localNow, out RemoteSample result)
        {
            result = new RemoteSample();
            int n = _entries.Count;
            if (n == 0) return false;
            double rt = RenderTime(localNow);

            if (n == 1 || rt <= _entries[0].T)
            {
                var e = rt <= _entries[0].T ? _entries[0] : _entries[n - 1];
                Fill(ref result, e.S, e.S.Pos, e.S.Yaw, 0f, false);
                return true;
            }

            var newest = _entries[n - 1];
            if (rt >= newest.T)
            {
                var prev = _entries[n - 2];
                float speed = Speed(prev, newest);
                Vec3 pos = newest.S.Pos;
                double over = Math.Min(rt - newest.T, MaxExtrapolation);
                if (over > 0 && !IsJump(prev, newest) && speed > 0)
                {
                    double dt = newest.T - prev.T;
                    float k = (float)(over / dt);
                    pos = new Vec3(newest.S.Pos.X + (newest.S.Pos.X - prev.S.Pos.X) * k, newest.S.Pos.Y + (newest.S.Pos.Y - prev.S.Pos.Y) * k, newest.S.Pos.Z + (newest.S.Pos.Z - prev.S.Pos.Z) * k);
                }
                // Data is late: report speed only while the newest sample is recent, so the avatar stops animating.
                if (rt - newest.T > 0.25) speed = 0f;
                Fill(ref result, newest.S, pos, newest.S.Yaw, speed, false);
                return true;
            }

            for (int i = n - 2; i >= 0; i--)
            {
                var a = _entries[i];
                if (a.T > rt) continue;
                var b = _entries[i + 1];
                if (IsJump(a, b)) { Fill(ref result, b.S, b.S.Pos, b.S.Yaw, 0f, true); return true; }
                float t = (float)((rt - a.T) / (b.T - a.T));
                Fill(ref result, t < 0.5f ? a.S : b.S, Vec3.Lerp(a.S.Pos, b.S.Pos, t), LerpAngle(a.S.Yaw, b.S.Yaw, t), Speed(a, b), false);
                return true;
            }
            return false;
        }

        bool IsJump(Entry a, Entry b)
        {
            return (b.T - a.T) > MaxGap || Vec3.Distance(a.S.Pos, b.S.Pos) > TeleportDistance;
        }

        static float Speed(Entry a, Entry b)
        {
            double dt = b.T - a.T;
            return dt > 1e-4 ? (float)(Vec3.HorizontalDistance(a.S.Pos, b.S.Pos) / dt) : 0f;
        }

        static void Fill(ref RemoteSample r, PlayerSnapshot flagsFrom, Vec3 pos, float yaw, float speed, bool snapped)
        {
            r.Pos = pos; r.Yaw = yaw; r.HorizontalSpeed = speed; r.Flags = flagsFrom.Flags; r.Snapped = snapped;
        }

        // Shortest-path angle interpolation in degrees, result in [0,360).
        public static float LerpAngle(float a, float b, float t)
        {
            float d = ((b - a) % 360f + 540f) % 360f - 180f;
            float r = (a + d * t) % 360f;
            return r < 0 ? r + 360f : r;
        }
    }
}
