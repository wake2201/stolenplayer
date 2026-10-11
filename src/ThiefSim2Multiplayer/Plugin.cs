using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Game;
using ThiefSim2Multiplayer.UI;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer
{
    [BepInPlugin(Id, "ThiefSim2Multiplayer", Protocol.ModVersion)]
    [DefaultExecutionOrder(10000)]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.thiefsim2.multiplayer";
        private NetworkManager network;
        private MultiplayerConfig settings;
        private MultiplayerDebugUI ui;
        private readonly LocalPlayerAdapter local = new LocalPlayerAdapter();
        private readonly RemotePlayerProxy remote = new RemotePlayerProxy();
        private double nextSend;
        private bool integrationFailed;
        private bool wasAvailable;
        private string instanceLabel = "Local";
        private double nextVerbose;
        private WorldSyncFixture worldFixture;
        private WorldSyncState lastWorldState;
        private WorldInteractionController interactions;
        private LootInventoryController loot;
        private CooperativeEconomyController economy;
        private SceneContinuityController scenes;
        private SharedVehicleController vehicles;
        private ManualRuntimeAcceptance acceptance;
        private readonly Core.Diagnostics.DevelopmentProfiler profiler = new Core.Diagnostics.DevelopmentProfiler();

        private void Awake()
        {
            try
            {
                string assembly = Type.GetType("PlayerScript, Assembly-CSharp", true).Assembly.Location;
                string hash;
                using (var input = File.OpenRead(assembly))
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                settings = new MultiplayerConfig(Config);
                char[] label = (settings.InstanceName.Value ?? "Local").Trim().ToCharArray();
                for (int i = 0; i < label.Length; i++) if (char.IsControl(label[i])) label[i] = '_';
                instanceLabel = new string(label, 0, Math.Min(label.Length, 32));
                network = new NetworkManager(hash, Trace);
                network.FrameProfile.Enabled=settings.FrameProfiling.Value;
                network.FrameProfile.SpikeMilliseconds=Mathf.Clamp(settings.FrameSpikeMilliseconds.Value,10,5000);
                network.NetworkBudgetMilliseconds=Mathf.Clamp(settings.NetworkBudgetMilliseconds.Value,.1f,10f);
                Guid identity;
                if (!Guid.TryParse(settings.PersistentPlayerId.Value, out identity) || identity == Guid.Empty) throw new InvalidOperationException("Invalid configured persistent player identity.");
                network.Inventories.LocalIdentity = new PersistentCoopPlayerId(identity);
                interactions = new WorldInteractionController(network, Trace);
                loot = new LootInventoryController(network, Trace);
                economy = new CooperativeEconomyController(network,Trace);
                scenes = new SceneContinuityController(network, Trace);
                vehicles = new SharedVehicleController(network, Trace); vehicles.OpenSleep=scenes.OpenPassengerSleep;
                network.SceneQuiescing += Quiesce;
                network.LocalAvatarId = (byte)Mathf.Clamp(settings.AvatarId.Value, 0, 1);
                network.World.TestEntitiesEnabled = settings.DevelopmentWorldFixture.Value;
                if (settings.DevelopmentWorldFixture.Value) worldFixture = new WorldSyncFixture(network.World);
                network.Connected += () => { if (network.Session.IsHost && worldFixture != null) worldFixture.InitializeHost(); };
                network.RemoteRemoved += RemoveRemote;
                ui = new MultiplayerDebugUI(network, settings, local, remote, loot, Trace, profiler);
                acceptance = new ManualRuntimeAcceptance(network, local, remote, ui, settings.ManualSessionId.Value, Trace, profiler);
                DontDestroyOnLoad(gameObject);
                SceneManager.sceneLoaded += SceneLoaded; SceneManager.sceneUnloaded += SceneUnloaded; SceneManager.activeSceneChanged += SceneChanged;
                Trace("[TS2MP] Loaded " + Protocol.ModVersion + "; protocol=" + Protocol.Version + "; game=" + hash + "; mainThread=" + System.Threading.Thread.CurrentThread.ManagedThreadId + "; F8 opens multiplayer");
            }
            catch (Exception error) { FailIntegration(error); }
        }
        private void Update()
        {
            if (integrationFailed || network == null) return;
            try
            {
                RefreshNeutralFixtureInput();
                network.FrameProfile.Frame(Time.unscaledDeltaTime*1000.0,(int)network.Session.State,network.World.Registry.SceneEpoch,(int)network.World.State,network.LastProcessedPackets,network.PendingNetworkEvents);
                long started = profiler.Begin(); economy.Observe(); loot.Observe(); interactions.Observe(); network.Poll(); profiler.End(4, started); ui.Update(); acceptance.Update();
                if (network.World.State != lastWorldState)
                {
                    lastWorldState = network.World.State;
                    Trace("[TS2MP] World sync: " + lastWorldState + "; epoch=" + network.World.Registry.SceneEpoch + "; revision=" + network.World.Registry.WorldRevision + "; static=" + network.World.Registry.StaticCount + "; dynamic=" + network.World.Registry.DynamicCount + "; tombstones=" + network.World.Registry.TombstoneCount + "; snapshot=" + network.World.SnapshotId + "; chunks=" + network.World.SnapshotProgress + "/" + network.World.SnapshotChunks + "; bytes=" + network.World.SnapshotBytes + "; backlog=" + network.World.Backlog.Count + "; error=" + network.World.LastError);
                    if (lastWorldState == WorldSyncState.WorldReady && worldFixture != null)
                        Trace("[TS2MP] WORLD FIXTURE: " + (WorldSyncFixture.ValidateClient(network.World) ? "PASS" : "FAIL") + "; host=" + network.Session.IsHost);
                }
            }
            catch (Exception error) { FailIntegration(error); }
        }
        private void LateUpdate()
        {
            if (integrationFailed || network == null) return;
            try
            {
                if (local.Refresh())
                {
                    Trace("[TS2MP] Genuine local player acquired in " + local.SceneName + "; root=" + local.Position.ToString("F2") + "; yaw=" + local.Yaw.ToString("F1") + "; controllerCenter=" + local.ControllerCenter.ToString("F2") + "; controllerHeight=" + local.ControllerHeight.ToString("F2"));
                    if (worldFixture != null)
                    {
                        var actor = GameObject.FindGameObjectWithTag("Player");
                        var component = actor ? actor.GetComponent(Type.GetType("PlayerScript, Assembly-CSharp", true)) : null;
                        if (component)
                        {
                            var descriptor = UnityNetworkEntityAdapter.Describe(component, network.BuildFingerprintForDiagnostics, "player-identity-probe-v1", 0);
                            Trace("[TS2MP] Static identity probe (read only, unregistered): " + descriptor.DeriveId() + "; descriptor=" + descriptor.Canonical);
                        }
                    }
                }
                local.Observe(Trace, settings.DevelopmentControls.Value);
                bool available = scenes.Tick(local.IsAvailable && local.SceneKey.Length > 0, local.SceneKey);
                if (available) vehicles.Prepare();
                vehicles.Tick();
                if (available && network.Session.IsHost) { interactions.Prepare(); loot.Prepare(); economy.Prepare(); interactions.Observe(); }
                if (available != wasAvailable) { Trace("[TS2MP] Local player availability=" + available); wasAvailable = available; }
                network.SetLocalScene(available, available ? local.SceneKey : "");
                network.RemoteTransforms.SnapDistance = Protocol.Finite(settings.SnapDistance.Value) ? Mathf.Clamp(settings.SnapDistance.Value, 1, 1000) : 10;
                if (network.Session.State == SessionState.Connected && available)
                {
                    if (network.Now >= nextSend)
                    {
                        nextSend = network.Now + 1.0 / Mathf.Clamp(settings.SendRate.Value, 1, 60);
                        network.SendState(local.Sample());
                    }
                    PlayerTransform pose;
                    if (network.ScenesCompatible && (!network.Scenes.Active || network.World.State == WorldSyncState.WorldReady) && network.RemoteTransforms.TrySample(network.Now, Mathf.Clamp(settings.InterpolationMilliseconds.Value, 0, 500) / 1000.0, out pose))
                    {
                        pose.State.Vehicle = vehicles.Occupancy(network.Session.RemotePlayerId);
                        using(network.FrameProfile.Measure(Core.Diagnostics.RuntimePhase.NetworkAvatarConstruction)) remote.Create(network.Session.RemotePlayerId, local.VisualSource, Trace, local.ControllerCenter, local.ControllerHeight, settings.VisualHeightOffset.Value, settings.VisualYawOffset.Value, network.RemoteAvatarId, local.CrowbarSource);
                        long started = profiler.Begin(); remote.SetPose(pose, true); profiler.End(5, started);
                    }
                    else remote.Hide();
                }
                else remote.Hide();
                if (settings.VerboseTransforms.Value && network.Now >= nextVerbose)
                {
                    nextVerbose = network.Now + 1;
                    ArrivalStatistics stats = network.Telemetry.Statistics;
                    PlayerTransform latest = network.Telemetry.Latest;
                    Trace("[TS2MP] Acceptance: state=" + network.Session.State + "; local=" + local.Position.ToString("F2") +
                        "; localYaw=" + local.Yaw.ToString("F1") + "; target=(" + latest.X.ToString("F2") + "," + latest.Y.ToString("F2") + "," + latest.Z.ToString("F2") +
                        "); remoteYaw=" + latest.Yaw.ToString("F1") + "; rendered=" + remote.Position.ToString("F2") + "; visible=" + remote.Visible +
                        "; sent=" + network.Telemetry.Sent + "; accepted=" + network.Telemetry.Accepted + "; seq=" + latest.Sequence + "; gaps=" + network.Telemetry.SequenceGaps +
                        "; intervalMs=" + stats.MeanMilliseconds.ToString("F1") + "; min=" + stats.MinimumMilliseconds.ToString("F1") + "; max=" + stats.MaximumMilliseconds.ToString("F1") + "; jitter=" + stats.JitterMilliseconds.ToString("F1"));
                }
                ui.LateUpdate();
            }
            catch (Exception error) { FailIntegration(error); }
        }
        private void OnGUI() { if (!integrationFailed && ui != null) { ui.Draw(); vehicles?.Draw(); scenes?.DrawNotice(); } }
        // Fixed development fixture entry, called by PID/save-bound diagnostics only.
        public string DevelopmentWorldInteraction(string action)
        {
            if (settings == null || !settings.DevelopmentControls.Value || (action != "probe" && action != "audit" && action != "open" && action != "close")) throw new InvalidOperationException("World development control disabled/invalid.");
            return interactions.DevelopmentProbe(action);
        }
        public string DevelopmentLootInventory(string action)
        {
            if (settings == null || !settings.DevelopmentControls.Value || !new[] { "probe", "audit", "pickup", "drop", "catalog" }.Contains(action)) throw new InvalidOperationException("Loot development control disabled/invalid.");
            return loot.Development(action);
        }
        public string DevelopmentSharedVehicle(string action)
        {
            if (settings == null || !settings.DevelopmentControls.Value) throw new InvalidOperationException("Vehicle development control disabled.");
            if(action=="profile-reset") { network.FrameProfile.Reset(); return "Bounded frame profile reset."; }
            if(action=="profile-summary") { Trace(network.FrameProfile.Summary()); return "Bounded frame profile exported."; }
            if(action=="menu-cycle") {StartCoroutine(GuardedRoutine.Run(NativeMenuObservation(),e=>Trace("[TS2MP] MENU OBSERVATION failed after cleanup: "+e)));return "Bounded native Host menu/camera observation started with managed peer.";}
            if(action=="geometry") return VehicleGeometryAudit();
            if (action == "bootstrap") {
                if (SceneManager.GetSceneByName("MainScene").isLoaded) return "Already in native gameplay.";
                var type = Type.GetType("MainMenu, Assembly-CSharp", true);
                var menu = UnityEngine.Object.FindObjectOfType(type) as Component;
                var button = menu ? GameAccess.Get(menu, "ContinueButton") : null;
                if (button == null || !(bool)button.GetType().GetProperty("interactable").GetValue(button,null)) throw new InvalidOperationException("No initialized Continue profile.");
                GameAccess.Call(menu,"Continue"); return "Native Continue invoked on backed-up profile.";
            }
            if (action == "drive-cycle") { StartCoroutine(GuardedRoutine.Run(vehicles.NativeDrivingFixture(),e=>Trace("[TS2MP] NATIVE EVP FIXTURE failed after cleanup: "+e))); return "Controlled native EVP/UDP fixture started."; }
            if(action=="economy-cycle") {StartCoroutine(GuardedRoutine.Run(economy.NativeFixture(),e=>Trace("[TS2MP] NATIVE ECONOMY failed after cleanup: "+e))); return "Bounded original shop/sale/storage fixture started.";}
            if(action=="hideout") {if(network.Session.State!=SessionState.Offline) throw new InvalidOperationException("Offline native fixture required."); var actor=new PlayerLocator().Find(); GameAccess.Call(GameAccess.Get(actor,"GM"),"SelectHideout"); return "Original hideout travel requested on backed-up profile.";}
            if (action == "interaction-cycle") { StartCoroutine(GuardedRoutine.Run(NativeSeatInteractionFixture(2),e=>Trace("[TS2MP] NATIVE SEAT INPUT failed after cleanup: "+e))); return "Bounded original Rewired/native seat interaction fixture started; managed peer, both role assignments."; }
            if (action == "recovery-cycle") { StartCoroutine(GuardedRoutine.Run(vehicles.NativeDriverRecoveryFixture(),e=>Trace("[TS2MP] NATIVE DRIVER RECOVERY failed after cleanup: "+e))); return "Bounded native driver seat-loss/exit-timeout fixture started."; }
            if (action == "coop-cycle") { StartCoroutine(GuardedRoutine.Run(NativeContinuityFixture(),e=>Trace("[TS2MP] NATIVE CONTINUITY failed after cleanup: "+e))); return "Controlled native sleep/scene-follow fixture started; second peer is managed."; }
            if (action == "mission-prepare") { StartCoroutine(GuardedRoutine.Run(NativeMissionPreparationFixture(),e=>Trace("[TS2MP] NATIVE MISSION PREPARATION failed after cleanup: "+e))); return "Controlled native mission preparation started via production automatic follow; managed source."; }
            if (action == "passenger-cycle") { StartCoroutine(GuardedRoutine.Run(vehicles.NativePassengerFixture(),e=>Trace("[TS2MP] NATIVE PASSENGER FIXTURE failed after cleanup: "+e))); return "Controlled native passenger fixture started."; }
            if (action == "native-exit") {
                if (network.Session.State != SessionState.Offline) throw new InvalidOperationException("Native setup requires offline disposable session.");
                var p = new PlayerLocator().Find(); if (!p) throw new InvalidOperationException("Native actor unavailable.");
                var car = GameAccess.Get(p,"current_vehicle") as Component;
                p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] { car != null });
                if (car) GameAccess.Call(car,"ExitCarImmidiately");
                return "Original native exit requested; scene="+local.SceneKey;
            }
            if (action == "audit") { local.Audit(Trace); scenes.Audit(); VehicleStorageAudit(); return "Original player, scene readiness and native storage bindings recorded (read-only)."; }
            if (action == "vanilla-audit") return vehicles.NativeInputAudit();
            throw new ArgumentException("Unknown fixed vehicle fixture action.");
        }
        public string DevelopmentPassengerPreview(string directory)
        {
            if(settings==null || !settings.DevelopmentControls.Value || !Directory.Exists(directory)) throw new InvalidOperationException("Native preview fixture disabled/invalid output directory.");
            StartCoroutine(GuardedRoutine.Run(vehicles.NativePassengerFixture(camera=> {
                var target=new RenderTexture(640,360,24); var texture=new Texture2D(640,360,TextureFormat.RGB24,false);
                var original=camera.targetTexture; var active=RenderTexture.active;
                try { camera.targetTexture=target; camera.Render(); RenderTexture.active=target; texture.ReadPixels(new Rect(0,0,640,360),0,0); texture.Apply(); File.WriteAllBytes(Path.Combine(directory,"passenger-native-preview.png"),texture.EncodeToPNG()); }
                finally { camera.targetTexture=original; RenderTexture.active=active; target.Release(); Destroy(target); Destroy(texture); }
            }),e=>Trace("[TS2MP] NATIVE PASSENGER PREVIEW failed after cleanup: "+e)));
            return "Original-camera passenger preview fixture started; output remains local.";
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode) { InvalidateScene(); }
        private void SceneUnloaded(Scene scene) { InvalidateScene(); }
        private void SceneChanged(Scene oldScene, Scene nextScene) { InvalidateScene(); }
        private void InvalidateScene()
        {
            if (network != null) Trace("[TS2MP] Scene boundary: state=" + network.Session.State + "; transport retained");
            Quiesce(); network?.SetLocalScene(false, ""); scenes?.Boundary();
        }
        private void Quiesce() { vehicles?.SceneInvalidating(); RemoveRemote(); remote.ResetSources(); local.Reset(); nextSend = 0; economy?.SceneInvalidating(); loot?.SceneInvalidating(); interactions?.SceneInvalidating(); }
        private void Trace(string message) { Logger.LogInfo(DateTime.UtcNow.ToString("O") + " [instance=" + instanceLabel + "] " + message); }
        private void RemoveRemote() { if (remote.Exists) Trace("[TS2MP] Remote visual removed"); remote.Dispose(); }
        private void FailIntegration(Exception error)
        {
            integrationFailed = true; Logger.LogError("[TS2MP] Integration stopped safely: " + error);
            vehicles?.Dispose(); scenes?.Dispose(); economy?.Dispose(); loot?.Dispose(); interactions?.Dispose(); network?.Dispose(); remote.Dispose(); ui?.Dispose();
        }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded; SceneManager.sceneUnloaded -= SceneUnloaded; SceneManager.activeSceneChanged -= SceneChanged;
            vehicles?.Dispose(); scenes?.Dispose(); economy?.Dispose(); loot?.Dispose(); interactions?.Dispose(); network?.Dispose(); remote.Dispose(); ui?.Dispose();
        }
        private void OnApplicationQuit() { network?.Disconnect("Host/client exited."); }
    }
}
