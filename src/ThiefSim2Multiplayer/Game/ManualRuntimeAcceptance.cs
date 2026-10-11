using System;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Diagnostics;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.UI;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class ManualRuntimeAcceptance
    {
        private readonly NetworkManager network;
        private readonly LocalPlayerAdapter local;
        private readonly RemotePlayerProxy remote;
        private readonly MultiplayerDebugUI ui;
        private readonly string session;
        private readonly Action<string> log;
        internal readonly DevelopmentProfiler Profiler;
        private readonly TimingWindow frames = new TimingWindow();
        private double end;
        private bool overlay;
        private bool snapshotRequested;
        private int collections;
        internal ManualRuntimeAcceptance(NetworkManager n, LocalPlayerAdapter l, RemotePlayerProxy r, MultiplayerDebugUI u, string id, Action<string> logger, DevelopmentProfiler p)
        { network = n; local = l; remote = r; ui = u; session = id; log = logger; Profiler = p; ui.SnapshotRequested = () => snapshotRequested = true; }
        internal void Update()
        {
            bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (control && Input.GetKeyDown(KeyCode.F10)) Mark("MARK");
            if (control && Input.GetKeyDown(KeyCode.F7)) snapshotRequested = true;
            if (snapshotRequested) { snapshotRequested = false; Snapshot(); }
            if (control && Input.GetKeyDown(KeyCode.F6) && !Profiler.Enabled)
            {
                frames.Clear(); foreach (var w in Profiler.Windows) w.Clear(); overlay = ui.Visible; end = network.Now + 10; collections = GC.CollectionCount(0);
                Mark("PROFILE_BEGIN"); Profiler.Enabled = true;
            }
            if (Profiler.Enabled)
            {
                if (overlay != ui.Visible) { Profiler.Enabled = false; log("[TS2MP] MANUAL_PROFILE INVALID overlay changed inside sampling window"); return; }
                frames.Add(Time.unscaledDeltaTime * 1000.0);
                if (network.Now >= end || frames.Count == 8192)
                {
                    Profiler.Enabled = false;
                    log("[TS2MP] MANUAL_PROFILE testSession=" + session + "; F8=" + (overlay ? "ON" : "OFF") + "; avgFPS=" + (frames.Average == 0 ? 0 : 1000/frames.Average).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "; " + frames.Summary() + "; gen0Collections=" + (GC.CollectionCount(0)-collections) + "; allocationsPerFrame=UNAVAILABLE_MONO");
                    for (int i = 0; i < Profiler.Windows.Length; i++) log("[TS2MP] MANUAL_SUBSYSTEM " + DevelopmentProfiler.Names[i] + " " + Profiler.Windows[i].Summary());
                    Mark("PROFILE_END");
                }
            }
        }
        private void Mark(string action)
        {
            var pose = local.Sample();
            log("[TS2MP] " + ManualMarker.Format(DateTime.UtcNow, session, action, local.SceneKey, network.Session.State.ToString(), pose.State.Locomotion.ToString()) +
                " epoch=" + network.World.Registry.SceneEpoch + " world=" + network.World.State + " transition=" + network.Scenes.TransitionId +
                " sessionId=" + network.Session.SessionId + " target=" + (network.Scenes.Target?.SceneKey ?? "none") + " peerConnected=" + network.PeerConnected +
                " root=" + local.Position.ToString("F4") + " controllerCenter=" + local.ControllerCenter.ToString("F4") + " controllerHeight=" + local.CurrentControllerHeight.ToString("F4") +
                " feetY=" + (pose.Y + pose.State.GroundFeet).ToString("F4") + " crouched=" + pose.State.Crouched + " vehicle=" + pose.State.Vehicle.Seat + " vehicleKey=" + pose.State.Vehicle.PresentationKey +
                " proxy=" + remote.Visible + " driverVisible=" + remote.DriverVisible + " F8=" + ui.Visible);
        }
        private void Snapshot()
        {
            Mark("SNAPSHOT"); local.Audit(log); remote.Audit(log);
            // Legacy F8 work measured explicitly once; absent from both Update and OnGUI telemetry refresh.
            var timer = System.Diagnostics.Stopwatch.StartNew(); int count = network.World.Registry.Capture().Length;
            log("[TS2MP] LEGACY_F8 RegistryCapture entities=" + count + " ms=" + timer.Elapsed.TotalMilliseconds.ToString("F4"));
            timer.Restart(); int invariant = network.Inventories.Authority.InvariantViolations();
            log("[TS2MP] LEGACY_F8 OwnershipAudit violations=" + invariant + " ms=" + timer.Elapsed.TotalMilliseconds.ToString("F4"));
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            log("[TS2MP] ONE_SHOT_WORLD " + network.World.Registry.DevelopmentSummary()); Profiler.End(2, started);
            started = System.Diagnostics.Stopwatch.GetTimestamp(); timer.Restart();
            string hash = network.Inventories.LocalState?.Hash ?? "none";
            log("[TS2MP] ONE_SHOT_INVENTORY hash=" + hash + " pending=" + network.Inventories.Authority.PendingCount + " ms=" + timer.Elapsed.TotalMilliseconds.ToString("F4")); Profiler.End(3, started);
        }
    }
}
