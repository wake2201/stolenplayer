using System;
using System.Diagnostics;
using System.Threading;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Diagnostics;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Core.Inventory;

internal static class StabilizationTests
{
    internal static void Run(Action<bool, string> check)
    {
        var map = new NetworkSceneDescriptor("Warehouse");
        check(map.Location == 91 && map.SceneKey == "MainScene|Warehouse", "S1 semantic map includes MainScene and verified vanilla location");
        check(NetworkSceneDescriptor.FromLocation(99).Map == "MadisonSt" && NetworkSceneDescriptor.FromLocation(1) == null, "S1 unsupported blank canonical world fails explicitly");
        check(!NetworkSceneDescriptor.TryFromKey("MainScene|Warehouse|HackingPuzzle", out _), "S1 auxiliary/puzzle scene sets are not silently treated as canonical travel");
        foreach (byte type in new[] { SceneProtocol.Announce, SceneProtocol.Loaded, SceneProtocol.Failed }) {
            var bytes = SceneProtocol.Encode(new SceneMessage { Type = type, SessionId = 42, Epoch = 3, TransitionId = 7, Target = map });
            check(SceneProtocol.TryDecode(bytes, out var read) && read.Target.Location == 91 && read.Epoch == 3 && read.TransitionId == 7, "S1 scene codec roundtrip " + type);
            for (int n = 0; n < bytes.Length; n++) { var cut = new byte[n]; Array.Copy(bytes, cut, n); if (SceneProtocol.TryDecode(cut, out _)) throw new Exception("S1 truncated scene packet accepted"); }
            check(true, "S1 all scene packet truncations rejected " + type);
            var trailing = new byte[bytes.Length+1]; Array.Copy(bytes, trailing, bytes.Length); check(!SceneProtocol.TryDecode(trailing, out _), "S1 trailing scene bytes rejected " + type);
        }
        var scene = new SceneContinuity(); scene.Begin(1, 2, map);
        check(!scene.BarrierOpen && scene.State == SceneTransitionState.TransitionAnnounced, "S1 transition quiesces barrier"); scene.Loading();
        check(!scene.LocalReady("MainScene|MadisonSt") && !scene.LocalLoaded, "S1 wrong loaded world cannot open barrier");
        check(scene.LocalReady(map.SceneKey) && !scene.BarrierOpen && scene.State == SceneTransitionState.WaitingForPeers, "S1 local ready waits for peer");
        check(!scene.PeerReady(1, 1, map.SceneKey) && !scene.PeerReady(2, 2, map.SceneKey), "S1 old epoch and wrong transition acknowledgements rejected");
        check(scene.PeerReady(1, 2, map.SceneKey) && scene.BarrierOpen && scene.State == SceneTransitionState.BaselineSynchronizing, "S1 matching acknowledgement opens baseline barrier"); scene.Ready();
        check(scene.State == SceneTransitionState.WorldReady, "S1 baseline completion closes transition"); scene.Begin(2, 3, map); scene.Fail("cancelled");
        check(!scene.BarrierOpen && !scene.LocalReady(map.SceneKey) && !scene.PeerReady(2, 3, map.SceneKey), "S1 cancellation keeps requests blocked");
        scene.Begin(3, 4, map); scene.Clear(); check(!scene.Active && scene.BarrierOpen, "S1 teardown removes old transition state");
        var cadence = new TelemetryCadence(); check(!cadence.Due(false, 100), "S1 F8 off performs no display refresh");
        check(cadence.Due(true, 100) && !cadence.Due(true, 100) && !cadence.Due(true, 100.249) && cadence.Due(true, 100.25), "S1 telemetry refresh bounded to four Hz across repeated GUI events");
        cadence.Invalidate(); check(cadence.Due(true, 100.26), "S1 reopening overlay refreshes once");
        var timing = new TimingWindow(); for (int i = 1; i <= 100; i++) timing.Add(i);
        check(timing.Average == 50.5 && timing.Summary().Contains("p95Ms=95.0000"), "S1 aggregate timing has correct mean and p95");
        for (int i = 0; i < 10000; i++) timing.Add(1); check(timing.Count == 8192, "S1 profiler storage is bounded");
        var profiler = new DevelopmentProfiler(); check(profiler.Begin() == 0, "S1 profiler off skips stopwatch reads");
        check(!ManualMarker.Format(DateTime.UtcNow, "a\nb", "MARK", "MainScene", "Connected", "CrouchIdle").Contains("\n"), "S1 manual marker prevents log injection");
        var pose = new PlayerTransform { PlayerId = 2, SceneEpoch = 4, X = 3, Y = 7, Z = 9, State = new PlayerReplicationState { Known = SampledFields.GroundFeet | SampledFields.Crouched, GroundFeet = -0.3f, Crouched = true, Vehicle = new VehicleOccupancyState { Seat = SeatRole.Driver, PresentationKey = 987 } } };
        var data = new byte[Protocol.StateBytes];
        check(Protocol.WriteState(pose, data) && Protocol.TryReadState(data, 0, data.Length, out var decoded) && decoded.State.Vehicle.IsInVehicle && decoded.State.Vehicle.PresentationKey == 987 && decoded.State.GroundFeet == -0.3f, "S1 driver occupancy and controller feet codec roundtrip");
        pose.State.Vehicle = default; check(Math.Abs(GroundingPresentation.Offset(-0.8f, pose.State)-0.5f) < 0.00001, "S1 crouch correction uses controller feet geometry");
        check(pose.X == 3 && pose.Y == 7 && pose.Z == 9, "S1 presentation calculation preserves authoritative NetworkRoot");
        pose.State.GroundFeet = -0.8f; pose.State.Crouched = false; check(GroundingPresentation.Offset(-0.8f, pose.State) == 0, "S1 crouch-to-stand restores standing offset");
        pose.State.Vehicle.Seat = SeatRole.Driver; check(GroundingPresentation.Offset(-0.8f, pose.State) == 0, "S1 seated rig does not apply standing/crouch grounding");
        pose.State.Vehicle = new VehicleOccupancyState { Seat=SeatRole.Passenger, PresentationKey=9 }; check(Protocol.ValidState(pose.State), "Protocol 7 functional passenger role supported");
        pose.State.Vehicle = new VehicleOccupancyState { PresentationKey = 9 }; check(!Protocol.ValidState(pose.State), "S1 exit occupancy cannot retain stale vehicle reference");
        pose.State.Vehicle = default; pose.State.GroundFeet = float.NaN; check(!Protocol.ValidState(pose.State), "S1 invalid feet correction rejected");
        var buffer = new TransformBuffer(); pose.State.GroundFeet = -0.8f; pose.Sequence = 1; buffer.Add(pose, 1);
        var crouch = pose; crouch.Sequence = 2; crouch.Y -= .5f; crouch.State.GroundFeet = -0.3f; buffer.Add(crouch, 1.1);
        check(buffer.TrySample(1.1, .05, out var midway) && Math.Abs(midway.State.GroundFeet + .55f) < .001, "S1 foot geometry interpolates on same root timeline during crouch");
        var seated = crouch; seated.Sequence = 3; seated.State.Vehicle = new VehicleOccupancyState { Seat = SeatRole.Driver, PresentationKey = 987 }; buffer.Add(seated, 1.2);
        check(buffer.Count == 1 && buffer.TrySample(1.2, .1, out var enter) && enter.State.Vehicle.IsInVehicle, "S1 vehicle entry discards stale locomotion timeline");
        var exit = seated; exit.Sequence = 4; exit.X += 2; exit.State.Vehicle = default; buffer.Add(exit, 1.3);
        check(buffer.Count == 1 && buffer.TrySample(1.3, .1, out var left) && !left.State.Vehicle.IsInVehicle && left.X == exit.X, "S1 vehicle exit restores current position without stale seated interpolation");
        Transport(check);
        Benchmark();
    }
    private static void Transport(Action<bool, string> check)
    {
        using var host = new NetworkManager("s1"); using var client = new NetworkManager("s1");
        var map = new NetworkSceneDescriptor("MadisonSt"); host.SetLocalScene(true, map.SceneKey); host.Host(0 + FreePort(), "Host"); host.PublishStableScene(map);
        // Client begins in a different safe world and receives host identity after handshake.
        client.SetLocalScene(true, "IntroHouse|MainScene"); string announced = "";
        int inventoryRebinds = 0; client.Inventories.StateReceived += _ => inventoryRebinds++;
        host.Inventories.Authority.Configure(new[] { new ItemDefinitionRule(0, .1f, 0, true) });
        host.Inventories.CreateRemoteSeed = owner => new InventoryState(owner, 1, 10, new[] { new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0) });
        client.HostSceneRequested += target => { announced = target.Map; client.MarkSceneLoaded(target.SceneKey); };
        host.World.TestEntitiesEnabled = client.World.TestEntitiesEnabled = true;
        host.World.PrepareRegistry = () => { if (host.World.Registry.Count == 0) host.World.Registry.Spawn(new TestEntityAdapter { Value = 9 }, new EntityOwnership(OwnershipKind.Session)); };
        client.Join("127.0.0.1", host.Port, "Client");
        void Pump(Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done() && watch.Elapsed.TotalSeconds < 8) { host.Poll(); client.Poll(); Thread.Sleep(5); } if (!done()) throw new Exception("S1 UDP timeout " + host.LastError + " / " + client.LastError + " " + host.Scenes.State + "/" + client.Scenes.State); }
        Pump(() => host.World.State == WorldSyncState.WorldReady && client.World.State == WorldSyncState.WorldReady);
        Pump(() => client.Inventories.LocalState != null);
        string personalHash = client.Inventories.LocalState.Hash; int initialRebinds = inventoryRebinds;
        check(announced == "MadisonSt" && host.Scenes.BarrierOpen && client.Scenes.BarrierOpen, "S1 real UDP join discovers host world before baseline");
        ulong session = host.Session.SessionId, snapshot = client.World.SnapshotId; uint oldEpoch = host.World.Registry.SceneEpoch;
        var next = new NetworkSceneDescriptor("Warehouse"); bool quiesced = false; host.SceneQuiescing += () => quiesced = true;
        var rules = new[] { new ItemDefinitionRule(0, .1f, 0, true) }; host.Inventories.Authority.Configure(rules);
        var owner = host.Inventories.LocalIdentity; var seed = new InventoryState(owner, 1, 10, new InventoryItemState[0]); host.Inventories.Authority.Initialize(seed);
        var loot = host.World.Registry.Spawn(new ManagedLootAdapter(new LootState { Item = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0) }), new EntityOwnership(OwnershipKind.Session));
        var request = new InventoryRequest { SessionId = session, SceneEpoch = oldEpoch, RequestId = 1, ActorId = 1, Action = InventoryAction.Pickup, Entity = loot, ExpectedInventoryRevision = 1, ExpectedEntityRevision = host.World.Registry.Lookup(loot).State.Revision };
        var reserved = host.Inventories.HostBegin(request, _ => InventoryResultCode.Reserved);
        check(reserved.Code == InventoryResultCode.Reserved && host.Inventories.Authority.PendingCount == 1, "S1 pending native reservation established before scene change");
        host.BeginSceneTransition(next);
        check(host.Inventories.Authority.PendingCount == 0 && host.Inventories.Authority.Get(owner).Hash == seed.Hash, "S1 transition cancels reservation and preserves authoritative personal inventory");
        check(quiesced && host.PeerConnected && client.PeerConnected && host.Session.SessionId == session && !host.ScenesCompatible, "S1 quiesce preserves real UDP session and rejects requests");
        check(!host.SendTransform(1,2,3,4) && !client.SendInteraction(default, InteractionKind.Open), "S1 in-transition gameplay sends blocked");
        host.MarkSceneLoaded(next.SceneKey);
        Pump(() => host.World.State == WorldSyncState.WorldReady && client.World.State == WorldSyncState.WorldReady);
        Pump(() => inventoryRebinds > initialRebinds);
        check(client.Inventories.LocalState.Hash == personalHash && client.Inventories.OwnerSnapshotWorldRevision == host.World.Registry.WorldRevision, "S1 unchanged inventory revision reapplies to recreated native bindings in new epoch");
        check(host.Session.SessionId == session && client.Session.SessionId == session && host.PeerConnected && client.PeerConnected, "S1 transport connection survives scene transition without rejoin");
        check(host.World.Registry.SceneEpoch == oldEpoch+1 && client.World.Registry.SceneEpoch == oldEpoch+1 && client.World.SnapshotId > snapshot, "S1 one authoritative epoch and fresh Milestone C baseline");
        check(host.World.Registry.DevelopmentSummary() == client.World.Registry.DevelopmentSummary(), "S1 post-load canonical world hashes match");
        int rejected = client.World.Rejected;
        client.World.Handle(new WorldMessage { Type = WorldMessageType.WorldReady, SessionId = session, SceneEpoch = oldEpoch, SnapshotId = snapshot }, client.Now);
        check(client.World.Rejected > rejected && client.World.State == WorldSyncState.WorldReady, "S1 old epoch world packet cannot affect new world");
        host.BeginSceneTransition(map); host.FailSceneTransition("cancelled");
        Pump(() => client.Scenes.State == SceneTransitionState.Failed);
        check(host.PeerConnected && client.PeerConnected && !host.ScenesCompatible && !client.ScenesCompatible, "S1 transition failure retains transport and closes both barriers");
        host.BeginSceneTransition(next); host.MarkSceneLoaded(next.SceneKey);
        Pump(() => host.World.State == WorldSyncState.WorldReady && client.World.State == WorldSyncState.WorldReady);
        check(host.Session.SessionId == session && client.Session.SessionId == session, "S1 newer supported transition recovers from failure without rejoin");
    }
    private static void Benchmark()
    {
        var registry = new NetworkEntityRegistry(true, 1); var authority = new InventoryAuthority(registry);
        authority.Configure(new[] { new ItemDefinitionRule(0, .1f, 0, true) });
        for (int i = 0; i < 1500; i++) registry.Spawn(new ManagedLootAdapter(new LootState { Item = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0) }), new EntityOwnership(OwnershipKind.Session));
        var items = new InventoryItemState[128]; for (int i = 0; i < items.Length; i++) items[i] = new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), 0);
        var inventory = new InventoryState(new PersistentCoopPlayerId(Guid.NewGuid()), 1, 100, items); authority.Initialize(inventory);
        Action[] actions = { () => { _ = registry.Capture(); }, () => { _ = authority.InvariantViolations(); }, () => { _ = inventory.Hash; }, () => { _ = registry.StaticCount; _ = registry.DynamicCount; _ = registry.TombstoneCount; _ = inventory.ItemCount; } };
        string[] names = { "LegacyCapture", "LegacyOwnershipAudit", "LegacyInventoryHash", "CurrentFastCounters" };
        for (int k = 0; k < actions.Length; k++) {
            for (int i = 0; i < 10; i++) actions[k]();
            long allocations = GC.GetAllocatedBytesForCurrentThread(); var time = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) actions[k](); time.Stop(); allocations = GC.GetAllocatedBytesForCurrentThread()-allocations;
            Console.WriteLine("S1_SYNTHETIC_PROFILE " + names[k] + " entities=1500 items=128 avgMs=" + (time.Elapsed.TotalMilliseconds/50).ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + " bytesPerRefresh=" + allocations/50 + " (net9; not Unity FPS evidence)");
        }
    }
    private static int FreePort() { using var socket = new System.Net.Sockets.UdpClient(0); return ((System.Net.IPEndPoint)socket.Client.LocalEndPoint).Port; }
}
