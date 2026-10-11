using System;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Game;
using ThiefSim2Multiplayer.Core.Diagnostics;

namespace ThiefSim2Multiplayer.UI
{
    internal sealed class MultiplayerDebugUI
    {
        private readonly NetworkManager network;
        private readonly MultiplayerConfig config;
        private readonly Action<string> log;
        private readonly LocalPlayerAdapter local;
        private readonly RemotePlayerProxy remote;
        private readonly LootInventoryController lootController;
        private readonly string[] telemetryRows = new string[25];
        private readonly TelemetryCadence cadence = new TelemetryCadence();
        private readonly DevelopmentProfiler profiler;
        private string telemetryText = "";
        internal bool Visible => visible;
        internal Action SnapshotRequested;
        private Vector2 scroll;
        private Rect bounds = new Rect(20, 20, 580, 650);
        private string address, port, name, error = "";
        private bool visible;
        private bool oldCursorVisible;
        private CursorLockMode oldCursorLock;
        internal MultiplayerDebugUI(NetworkManager network, MultiplayerConfig config, LocalPlayerAdapter local, RemotePlayerProxy remote, LootInventoryController loot, Action<string> logger, DevelopmentProfiler profiler)
        {
            this.network = network; this.config = config; log = logger;
            this.local = local; this.remote = remote;
            lootController = loot;
            this.profiler = profiler;
            address = config.Address.Value; port = config.Port.Value.ToString(); name = config.Name.Value;
        }
        internal void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (!visible) { oldCursorVisible = Cursor.visible; oldCursorLock = Cursor.lockState; }
                visible = !visible;
                cadence.Invalidate();
                if (!visible) RestoreCursor();
                log("[TS2MP] Debug UI " + (visible ? "opened" : "closed"));
            }
            if (cadence.Due(visible, network.Now)) { long started = profiler.Begin(); RefreshTelemetry(); telemetryText = string.Join("\n", telemetryRows); profiler.End(1, started); }
        }
        internal void LateUpdate() { if (visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } }
        internal void Draw()
        {
            if (!visible) return;
            long started = profiler.Begin(); bounds = GUI.Window(0x545332, bounds, Window, "Thief Simulator 2 Multiplayer — F8"); profiler.End(0, started);
            if (Event.current.type == EventType.Layout) profiler.End(6, started); else if (Event.current.type == EventType.Repaint) profiler.End(7, started);
        }
        private void Window(int id)
        {
            SessionState state = network.Session.State;
            GUI.enabled = state == SessionState.Offline;
            GUILayout.BeginHorizontal(); GUILayout.Label("Name", GUILayout.Width(45)); name = GUILayout.TextField(name, 48); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("IP", GUILayout.Width(45)); address = GUILayout.TextField(address, 45); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("Port", GUILayout.Width(45)); port = GUILayout.TextField(port, 5); GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host")) Act(true);
            if (GUILayout.Button("Join")) Act(false);
            GUILayout.EndHorizontal();
            GUI.enabled = state != SessionState.Offline;
            if (GUILayout.Button("Disconnect")) network.Disconnect();
            GUI.enabled = true;
            GUILayout.Label("Error: " + (error.Length > 0 ? error : network.LastError));
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Clamp(Screen.height - 300, 160, 400)));
            // One cached text block; no enumerations/hashes/formatting in any OnGUI event.
            GUILayout.Label(telemetryText);
            GUILayout.EndScrollView();
            if (GUILayout.Button("Log acceptance snapshot"))
            {
                SnapshotRequested?.Invoke();
            }
            if (config.DevelopmentControls.Value && GUILayout.Button("Simulate adapter unavailable for 2 seconds"))
            { local.SuspendForAcceptanceTest(); log("[TS2MP] Development adapter blackout started (game actor unchanged)"); }
            if (config.DevelopmentControls.Value && GUILayout.Button("Resynchronize world")) network.World.Resnapshot();
            GUILayout.Label("Pause the game with Esc before using this panel.");
            GUI.DragWindow(new Rect(0, 0, 580, 20));
        }
        private void RefreshTelemetry()
        {
            ArrivalStatistics stats = network.Telemetry.Statistics;
            PlayerTransform latest = network.Telemetry.Latest;
            string role = network.Session.State == SessionState.Offline ? "none" : network.Session.IsHost ? "host" : "client";
            telemetryRows[0] = "State: " + network.Session.State + " | Role: " + role + " | IDs: " + network.Session.LocalPlayerId + " / " + network.Session.RemotePlayerId;
            telemetryRows[1] = "Transport peer: " + network.PeerConnected + " | Ping: " + network.Ping + " ms | UDP sent/received: " + network.PacketsSent + "/" + network.PacketsReceived;
            telemetryRows[2] = "Last transport receive: " + (network.LastReceiveAge < 0 ? "none" : network.LastReceiveAge.ToString("F1") + " s") + " | Timeout: 5 s | Last end: " + network.LastDisconnectReason;
            telemetryRows[3] = "Protocol: " + Protocol.Version + " | Mod: " + Protocol.ModVersion + " | Handshake: " + network.HandshakeState + " | Remote mod: " + network.RemoteModVersion;
            telemetryRows[4] = "Local player acquired: " + local.IsAvailable + " | Local scene key: " + local.SceneKey;
            telemetryRows[5] = "Remote ready: " + network.RemoteSceneReady + " | Matching scenes: " + network.ScenesCompatible + " | " + network.RemoteSceneKey;
            telemetryRows[6] = "Proxy: " + remote.Exists + " | Visible: " + remote.Visible + " | " + (remote.Strategy ?? "none");
            telemetryRows[7] = "Buffer: " + network.RemoteTransforms.Count + "/32 | Latest sequence: " + latest.Sequence + " | Sequence gaps: " + network.Telemetry.SequenceGaps;
            telemetryRows[8] = "Render target age: " + config.InterpolationMilliseconds.Value + " ms | Snapshot age: " + network.Telemetry.ReceiveAge(network.Now).ToString("F2") + " s | Snaps: " + network.RemoteTransforms.SnapCount;
            telemetryRows[9] = "Local sampled: " + local.Position.ToString("F2") + " yaw " + local.Yaw.ToString("F1");
            telemetryRows[10] = "Last remote target: (" + latest.X.ToString("F2") + ", " + latest.Y.ToString("F2") + ", " + latest.Z.ToString("F2") + ") yaw " + latest.Yaw.ToString("F1") + " | Rendered: " + remote.Position.ToString("F2");
            telemetryRows[11] = "Arrival ms mean/min/max/jitter: " + stats.MeanMilliseconds.ToString("F1") + "/" + stats.MinimumMilliseconds.ToString("F1") + "/" + stats.MaximumMilliseconds.ToString("F1") + "/" + stats.JitterMilliseconds.ToString("F1") + " (" + stats.Samples + "/128)";
            telemetryRows[12] = "Transforms sent/accepted/rejected: " + network.Telemetry.Sent + "/" + network.Telemetry.Accepted + "/" + network.Telemetry.Rejected;
            var world = network.World; var registry = world.Registry;
            telemetryRows[13] = "World: " + world.State + " | Epoch: " + registry.SceneEpoch + " | Revision: " + registry.WorldRevision;
            telemetryRows[14] = "Registry static/dynamic/tombstone: " + registry.StaticCount + "/" + registry.DynamicCount + "/" + registry.TombstoneCount + " | Collisions: " + registry.CollisionCount;
            telemetryRows[15] = "Snapshot: " + world.SnapshotId + " | Chunks: " + world.SnapshotProgress + "/" + world.SnapshotChunks + " | Bytes: " + world.SnapshotBytes + " | Backlog: " + world.Backlog.Count + " | Restarts: " + world.ResnapshotCount;
            var interactions = network.Interactions;
            telemetryRows[16] = "Doors / Windows / Unresolved: " + interactions.Doors + "/" + interactions.Windows + "/" + interactions.Unresolved;
            telemetryRows[17] = "Interaction sent/accepted/rejected: " + interactions.Sent + "/" + interactions.Accepted + "/" + interactions.Rejected + " | Duplicate/stale: " + interactions.Duplicates + "/" + interactions.Stale;
            telemetryRows[18] = "World deltas applied: " + world.DeltasApplied + " | Adapter apply failures: " + world.AdapterApplyFailures;
            var inventory = network.Inventories; var authority = inventory.Authority; var owner = inventory.LocalState;
            var other = inventory.RemoteIdentity.HasValue ? authority.Get(inventory.RemoteIdentity.Value) : null;
            telemetryRows[19] = "Inventory transactions pending / reservations: " + (network.Session.IsHost ? authority.PendingCount : lootController.NativePendingCount) + "/" + (network.Session.IsHost ? authority.PendingCount.ToString() : "host-only");
            telemetryRows[20] = "Committed / rejected / duplicate requests: " + authority.Committed + "/" + authority.Rejected + "/" + authority.Duplicates + " | Packet rejects / adapter failures: " + inventory.RejectedPackets + "/" + authority.AdapterFailures;
            telemetryRows[21] = "Bound static loot: " + lootController.LootCount + " | Transition: " + network.Scenes.State + " / " + network.Scenes.TransitionId;
            telemetryRows[22] = "Player1 / Player2 inventory revisions: " + (network.Session.IsHost ? owner?.Revision.ToString() ?? "none" : "private") + "/" + (network.Session.IsHost ? other?.Revision.ToString() ?? "none" : owner?.Revision.ToString() ?? "none");
            telemetryRows[23] = "Owner inventory count: " + (owner == null ? "none" : owner.ItemCount.ToString()) + " | Heavy audits: one-shot Ctrl+F7";
            telemetryRows[24] = "Scene error: " + network.Scenes.Error + " | Mark Ctrl+F10 | 10s profile Ctrl+F6";
        }
        private void Act(bool host)
        {
            int number;
            if (!int.TryParse(port, out number) || number < 1 || number > 65535) { error = "Port must be 1..65535."; return; }
            error = "";
            bool started = host ? network.Host(number, name) : network.Join(address, number, name);
            if (started) { config.Port.Value = number; config.Address.Value = address; config.Name.Value = name; }
        }
        private void RestoreCursor() { Cursor.lockState = oldCursorLock; Cursor.visible = oldCursorVisible; }
        internal void Dispose() { if (visible) RestoreCursor(); visible = false; }
    }
}
