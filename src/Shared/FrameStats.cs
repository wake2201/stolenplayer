// M5 W1/W2 performance instrumentation (pure). Fed with Time.unscaledDeltaTime once per rendered frame by NetRunner when
// Debug.PerfStats is on (one float store per frame, no allocation). Report over the frames of the last window:
//   avg FPS   = frames / sum(frame times)
//   P50/P95/P99 = frame-time percentiles (nearest rank on the sorted window)
//   1% low    = 1000 / mean(frame time of the slowest 1% of frames, at least 1 frame)   ("average of the worst 1%")
//   0.1% low  = same with the slowest 0.1%
// The window is bounded (Capacity frames); at very high FPS it covers less than the reporting interval (frames= says how many).
using System;

namespace TS2MP.Shared
{
    public struct FrameReport
    {
        public int Frames; public double Seconds, AvgFps, P50Ms, P95Ms, P99Ms, Low1Fps, Low01Fps, MaxMs;
        public override string ToString()
        {
            return "frames=" + Frames + " avgFps=" + AvgFps.ToString("0.0") + " p50=" + P50Ms.ToString("0.00") + "ms p95=" + P95Ms.ToString("0.00")
                + "ms p99=" + P99Ms.ToString("0.00") + "ms low1%=" + Low1Fps.ToString("0.0") + "fps low0.1%=" + Low01Fps.ToString("0.0") + "fps max=" + MaxMs.ToString("0.0") + "ms";
        }
    }

    public sealed class FrameStats
    {
        public readonly int Capacity;
        readonly float[] _ring, _sort;
        int _n, _i;

        public FrameStats(int capacity) { Capacity = Math.Max(16, capacity); _ring = new float[Capacity]; _sort = new float[Capacity]; }
        public int Count { get { return _n; } }

        public void Add(float seconds)
        {
            if (!(seconds > 0) || seconds > 10f) return;   // ignore pauses/loading stalls > 10 s and invalid values
            _ring[_i] = seconds; _i = (_i + 1) % Capacity; if (_n < Capacity) _n++;
        }

        public void Reset() { _n = 0; _i = 0; }

        public FrameReport Compute()
        {
            var r = new FrameReport { Frames = _n };
            if (_n == 0) return r;
            Array.Copy(_ring, _sort, _n);
            Array.Sort(_sort, 0, _n);
            double sum = 0; for (int i = 0; i < _n; i++) sum += _sort[i];
            r.Seconds = sum;
            r.AvgFps = _n / sum;
            r.P50Ms = Pct(0.50) * 1000; r.P95Ms = Pct(0.95) * 1000; r.P99Ms = Pct(0.99) * 1000;
            r.MaxMs = _sort[_n - 1] * 1000;
            r.Low1Fps = LowFps(0.01); r.Low01Fps = LowFps(0.001);
            return r;
        }

        double Pct(double p) { int k = (int)Math.Ceiling(p * _n) - 1; if (k < 0) k = 0; if (k >= _n) k = _n - 1; return _sort[k]; }

        double LowFps(double share)
        {
            int k = Math.Max(1, (int)Math.Floor(_n * share));
            double s = 0; for (int i = _n - k; i < _n; i++) s += _sort[i];
            return k / s;
        }
    }

    // Main-thread cost of one mod subsystem: count, total, max (Stopwatch ticks supplied by the caller).
    public sealed class CostMeter
    {
        public long Count, Ticks, MaxTicks;
        public void Add(long ticks) { Count++; Ticks += ticks; if (ticks > MaxTicks) MaxTicks = ticks; }
        public double AvgUs(long freq) { return Count == 0 ? 0 : Ticks * 1e6 / freq / Count; }
        public double MaxUs(long freq) { return MaxTicks * 1e6 / freq; }
        public void Reset() { Count = 0; Ticks = 0; MaxTicks = 0; }
        public string Text(long freq) { return "n=" + Count + " avg=" + AvgUs(freq).ToString("0.0") + "us max=" + MaxUs(freq).ToString("0") + "us"; }
    }
}
