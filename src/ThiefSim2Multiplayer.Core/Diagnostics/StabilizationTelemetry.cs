using System;
using System.Diagnostics;
using System.Globalization;

namespace ThiefSim2Multiplayer.Core.Diagnostics
{
    public sealed class TelemetryCadence
    {
        private double next;
        public bool Due(bool visible, double now) { if (!visible || now < next) return false; next = now + 0.25; return true; }
        public void Invalidate() { next = 0; }
    }
    // Fixed storage; no per-frame logging, sorting, allocation or reflection.
    public sealed class TimingWindow
    {
        private readonly double[] values = new double[8192];
        private int count, next;
        private double sum;
        public int Count => count;
        public void Add(double milliseconds) { if (milliseconds < 0 || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds)) return; if(count==values.Length) sum-=values[next]; else count++; values[next]=milliseconds; next=(next+1)%values.Length; sum += milliseconds; }
        public void Clear() { count = next = 0; sum = 0; }
        public string Summary()
        { if (count == 0) return "count=0"; var sorted = new double[count]; Array.Copy(values, sorted, count); Array.Sort(sorted); return string.Format(CultureInfo.InvariantCulture, "count={0}; avgMs={1:F4}; medianMs={2:F4}; p95Ms={3:F4}; p99Ms={4:F4}; maxMs={5:F4}", count, sum / count, sorted[(count-1)/2], sorted[(int)Math.Ceiling(count*0.95)-1], sorted[(int)Math.Ceiling(count*0.99)-1], sorted[count-1]); }
        public double Average => count == 0 ? 0 : sum / count;
    }
    public sealed class DevelopmentProfiler
    {
        public readonly TimingWindow[] Windows = { new TimingWindow(), new TimingWindow(), new TimingWindow(), new TimingWindow(), new TimingWindow(), new TimingWindow(), new TimingWindow(), new TimingWindow() };
        public static readonly string[] Names = { "DebugUI", "RegistryTelemetry", "WorldHash", "InventoryHash", "NetworkProcessing", "AvatarPresentation", "DebugUILayout", "DebugUIRepaint" };
        public bool Enabled;
        public long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;
        public void End(int subsystem, long start) { if (start != 0) Windows[subsystem].Add((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency); }
    }
    public static class ManualMarker
    {
        public static string Format(DateTime utc, string session, string action, string scene, string network, string player)
        { return "MANUAL_CHECKPOINT utc=" + utc.ToUniversalTime().ToString("O") + " testSession=" + Clean(session) + " action=" + Clean(action) + " scene=" + Clean(scene) + " network=" + Clean(network) + " player=" + Clean(player); }
        private static string Clean(string s) { s = s ?? ""; return s.Replace("\r", "_").Replace("\n", "_").Replace(" ", "_").Substring(0, Math.Min(160, s.Length)); }
    }
}
