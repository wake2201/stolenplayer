using System;
using System.Threading;
using System.Text.Json;
using ThiefSim2Multiplayer.TransportHarness;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

internal static class Program
{
    // Synthetic sender only: no game, Steam, saves, input, or proprietary dependencies.
    private static int Main(string[] args)
    {
        if (args.Length == 7 && args[0] == "link")
        {
            try
            {
                using var link = new UdpConditionLink(int.Parse(args[1]),int.Parse(args[2]),int.Parse(args[4]),int.Parse(args[5]),int.Parse(args[6]));
                Console.WriteLine("TEST LINK: port="+link.Port+" target="+args[2]+" latency="+args[4]+" jitterSpan="+args[5]+" loss="+args[6]);
                Thread.Sleep(TimeSpan.FromSeconds(double.Parse(args[3],System.Globalization.CultureInfo.InvariantCulture)));
                Console.WriteLine("LINK METRICS: "+JsonSerializer.Serialize(new { received=link.Received, forwarded=link.Forwarded, dropped=link.Dropped, overflow=link.QueueOverflow, error=link.Error }));
                return link.Error.Length == 0 && link.Forwarded > 0 && link.QueueOverflow == 0 ? 0 : 1;
            }
            catch (Exception failure) { Console.Error.WriteLine(failure); return 1; }
        }
        if (args.Length < 3 || (args[0] != "host" && args[0] != "client"))
        {
            Console.WriteLine("host PORT BUILD_HASH [SECONDS] [SCENE_KEY] [circle|follow|avatar|equipment|soak] [AVATAR_PHASE_SECONDS] [AVATAR_ID]");
            Console.WriteLine("client IP PORT BUILD_HASH [SECONDS] [SCENE_KEY] [circle|follow|avatar|equipment|soak] [AVATAR_PHASE_SECONDS] [AVATAR_ID]");
            Console.WriteLine("link LISTEN_PORT HOST_PORT SECONDS LATENCY_MS JITTER_SPAN_MS LOSS_PERCENT"); return 2;
        }
        try
        {
            bool host = args[0] == "host"; int offset = host ? 0 : 1;
            int port = int.Parse(args[1+offset]); string build = args[2+offset];
            double duration = args.Length > 3+offset ? double.Parse(args[3+offset],System.Globalization.CultureInfo.InvariantCulture) : 30;
            string scene = args.Length > 4+offset ? args[4+offset] : "IntroHouse|MainScene";
            string mode = args.Length > 5+offset ? args[5+offset] : "circle";
            double phaseSeconds = args.Length > 6+offset ? double.Parse(args[6+offset],System.Globalization.CultureInfo.InvariantCulture) : 4;
            if (!double.IsFinite(phaseSeconds) || phaseSeconds < 2 || phaseSeconds > 30) throw new ArgumentException("Avatar phase duration must be 2..30 seconds.");
            bool worldFixture = mode == "world";
            bool interactionFixture = mode == "interactions";
            bool inventoryFixture = mode == "inventory";
            if (interactionFixture && args.Length <= 9 + offset) throw new ArgumentException("interactions requires phase seconds, avatar ID, local manifest path and local target path; use tools/runtime/start-interaction-harness.ps1.");
            if (inventoryFixture && args.Length <= 10 + offset) throw new ArgumentException("inventory requires manifest, target and native catalog paths; use tools/runtime/start-inventory-harness.ps1.");
            bool stabilizationFixture = mode == "s1";
            bool avatarFixture = mode == "avatar" || mode == "equipment" || mode == "soak";
            if (mode != "circle" && mode != "follow" && !stabilizationFixture && !avatarFixture && !worldFixture && !interactionFixture && !inventoryFixture) throw new ArgumentException("Unknown synthetic pose mode.");
            using var network = new NetworkManager(build, message => Console.WriteLine(DateTime.UtcNow.ToString("O") + " [Harness" + (host ? "Host" : "Client") + "] " + message));
            WorldSyncFixture world = worldFixture ? new WorldSyncFixture(network.World) : null;
            InteractionHarness interactions = interactionFixture ? new InteractionHarness(network, host, args[8 + offset], args[9 + offset]) : null;
            InventoryHarness inventory = inventoryFixture ? new InventoryHarness(network, host, args[8 + offset], args[9 + offset], args[10 + offset]) : null;
            if (avatarFixture || interactionFixture || stabilizationFixture) network.Inventories.LocalIdentity = InventoryHarness.FixtureIdentity;
            if ((avatarFixture || stabilizationFixture) && !host) network.World.PrepareBaseline = baseline => InteractionHarness.BindPresentationBaseline(network, baseline);
            network.HostSceneRequested += target => { scene = target.SceneKey; network.MarkSceneLoaded(scene); Console.WriteLine("SYNTHETIC SCENE FOLLOW: " + scene + " epoch=" + network.Scenes.Epoch); };
            bool presentationWorldReady = false;
            if (avatarFixture && !host) network.World.WorldBecameReady += () => { presentationWorldReady = true; Console.WriteLine("AVATAR WORLD READY: " + network.World.Registry.DevelopmentSummary()); };
            if (host && world != null) world.InitializeHost();
            bool worldPassed = false;
            if (worldFixture) network.World.WorldBecameReady += () => {
                worldPassed = WorldSyncFixture.ValidateClient(network.World);
                Console.WriteLine("WORLD FIXTURE: " + (worldPassed ? "PASS" : "FAIL") + " revision=" + network.World.Registry.WorldRevision + " chunks=" + network.World.SnapshotChunks + " tombstones=" + network.World.Registry.TombstoneCount);
            };
            if (args.Length > 7 + offset) network.LocalAvatarId = byte.Parse(args[7 + offset]);
            network.SetLocalScene(true,scene);
            if (!(host ? network.Host(port,"HarnessHost") : network.Join(args[1],port,"HarnessClient"))) return 1;
            int received = 0, sent = 0; uint previous = 0; double nextSend = 0, nextReport = 0; bool connected = false;
            double fixtureStart = -1; PlayerTransform fixtureAnchor = default; int previousPhase = -1;
            LocomotionState? outgoingState = null; float outgoingMin = float.PositiveInfinity, outgoingMax = 0; int outgoingSamples = 0;
            uint previousRaw = 0;
            while (network.Now < duration)
            {
                network.Poll(); connected |= network.Session.State == SessionState.Connected;
                if ((avatarFixture || worldFixture || interactionFixture || inventoryFixture) && network.RemoteSceneReady && scene != network.RemoteSceneKey)
                {
                    scene = network.RemoteSceneKey; network.SetLocalScene(true, scene);
                    Console.WriteLine("AVATAR FIXTURE: adopted actual peer scene=" + scene);
                }
                if (!host && connected && network.Session.State == SessionState.Offline) break;
                if (network.ScenesCompatible && network.Now >= nextSend)
                {
                    nextSend = network.Now+0.05;
                    // Synthetic poses are suitable for transport checks; do not present as gameplay movement.
                    if (stabilizationFixture && network.Telemetry.HasPose)
                    {
                        var fixture = network.Telemetry.Latest;
                        if (!fixture.State.Vehicle.IsInVehicle) { double angle = fixture.Yaw * Math.PI / 180; fixture.X += (float)Math.Sin(angle)*3; fixture.Z += (float)Math.Cos(angle)*3; }
                        if (network.SendState(fixture)) sent++;
                    }
                    else if (avatarFixture)
                    {
                        if (network.Telemetry.HasPose)
                        {
                            if (fixtureStart < 0) { fixtureStart = network.Now; fixtureAnchor = network.Telemetry.Latest; }
                            double elapsed = network.Now - fixtureStart + (mode == "equipment" ? 6 * phaseSeconds : 0);
                            int phase = (int)(elapsed / phaseSeconds);
                            if (mode == "soak") phase %= 10;
                            if (phase >= (mode == "equipment" ? 8 : 10)) { network.Disconnect(mode == "equipment" ? "Disconnect while crowbar equipped." : "Avatar sequence complete."); break; }
                            var anchor = fixtureAnchor; double angle = anchor.Yaw * Math.PI / 180;
                            var fixture = new PlayerTransform { X = anchor.X + (float)Math.Sin(angle) * 4.5f, Y = anchor.Y, Z = anchor.Z + (float)Math.Cos(angle) * 4.5f, Yaw = (anchor.Yaw + 180) % 360 };
                            fixture.State = new PlayerReplicationState { Known = SampledFields.Velocity | SampledFields.Grounded | SampledFields.Equipment | SampledFields.Sprinting | SampledFields.LookPitch, Grounded = true, EquippedItemId = (phase == 6 || phase == 7) ? (short)0 : (short)-1, LookPitch = phase == 4 ? -60 : phase == 5 ? 60 : 0 };
                            fixture.State.Locomotion = phase == 2 ? LocomotionState.Run : phase == 1 || phase == 3 || phase == 7 ? LocomotionState.Walk : LocomotionState.Idle;
                            if (fixture.State.Locomotion != LocomotionState.Idle)
                            {
                                // Deterministic bounded lateral track, with derivative consistent with world velocity.
                                float radius = phase == 2 ? 1.2f : 0.6f; double t = elapsed % phaseSeconds;
                                float displacement = radius * (float)Math.Sin(t * 2 * Math.PI / phaseSeconds);
                                float velocity = radius * (float)(2 * Math.PI / phaseSeconds * Math.Cos(t * 2 * Math.PI / phaseSeconds));
                                fixture.X += (float)Math.Cos(angle) * displacement; fixture.Z -= (float)Math.Sin(angle) * displacement;
                                fixture.State.VelocityX = (float)Math.Cos(angle) * velocity; fixture.State.VelocityZ = -(float)Math.Sin(angle) * velocity;
                                fixture.State.Sprinting = phase == 2;
                            }
                            // Show both front and side angles; keep equipment visible from the anchor camera.
                            if (phase == 5 || phase >= 8) fixture.Yaw = (anchor.Yaw + 270) % 360;
                            if (phase == 9 && mode != "soak") fixture.X += 20;
                            if (phase != previousPhase) { previousPhase = phase; Console.WriteLine("AVATAR FIXTURE: phase=" + phase + " state=" + fixture.State.Locomotion + " item=" + fixture.State.EquippedItemId + " yaw=" + fixture.Yaw + " teleport=" + (phase == 9 && mode != "soak")); }
                            if (network.SendState(fixture)) sent++;
                        }
                    }
                    else if (mode == "follow" || interactionFixture || inventoryFixture)
                    {
                        if (network.Telemetry.HasPose)
                        {
                            var anchor = network.Telemetry.Latest; double angle = anchor.Yaw*Math.PI/180;
                            // Diagnostic fixture only: remain 3 m in front of the genuine sampled player.
                            bool shareRoot = interactionFixture || (inventoryFixture && !host);
                            if (network.SendTransform(anchor.X+(shareRoot ? 0 : (float)Math.Sin(angle)*3),anchor.Y,anchor.Z+(shareRoot ? 0 : (float)Math.Cos(angle)*3),anchor.Yaw)) sent++;
                        }
                    }
                    else if (network.SendTransform((float)Math.Sin(network.Now),0,(float)Math.Cos(network.Now),(float)(network.Now*30%360))) sent++;
                }
                if (interactions != null && !interactions.Completed) interactions.Tick(host);
                inventory?.Tick(host);
                if (network.RemoteTransforms.TrySample(network.Now,0.1,out var pose) && pose.Sequence != previous)
                {
                    previous = pose.Sequence; received++;
                    if (avatarFixture && network.Telemetry.Latest.Sequence != previousRaw)
                    {
                        var actual = network.Telemetry.Latest;
                        previousRaw = actual.Sequence;
                        Console.WriteLine("REAL PEER SAMPLE: " + JsonSerializer.Serialize(new { utc = DateTime.UtcNow, sequence = actual.Sequence, x = actual.X, y = actual.Y, z = actual.Z, yaw = actual.Yaw, pitch = actual.State.LookPitch, vx = actual.State.VelocityX, vy = actual.State.VelocityY, vz = actual.State.VelocityZ, planar = actual.State.PlanarSpeed, state = actual.State.Locomotion.ToString(), grounded = actual.State.Grounded, crouched = actual.State.Crouched, sprint = actual.State.Sprinting, item = actual.State.EquippedItemId, known = actual.State.Known.ToString() }));
                    }
                    var state = pose.State; outgoingMin = Math.Min(outgoingMin, state.PlanarSpeed); outgoingMax = Math.Max(outgoingMax, state.PlanarSpeed); outgoingSamples++;
                    if (outgoingState != state.Locomotion) { outgoingState = state.Locomotion; Console.WriteLine("REAL PEER TRANSITION: state=" + state.Locomotion + " planar=" + state.PlanarSpeed.ToString("F4") + " grounded=" + state.Grounded + " crouched=" + state.Crouched + " sprint=" + state.Sprinting + " item=" + state.EquippedItemId + " known=" + state.Known); }
                }
                if ((mode == "follow" || avatarFixture) && network.Now >= nextReport)
                {
                    nextReport = network.Now + 1;
                    var latest = network.Telemetry.Latest;
                    Console.WriteLine("SYNTHETIC FOLLOW: state="+network.Session.State+" received="+network.Telemetry.Accepted+" remote=("+latest.X.ToString("F2")+","+latest.Y.ToString("F2")+","+latest.Z.ToString("F2")+") yaw="+latest.Yaw.ToString("F1"));
                    if (outgoingSamples > 0) Console.WriteLine("REAL PEER SUMMARY: samples=" + outgoingSamples + " planarRange=" + outgoingMin.ToString("F4") + ".." + outgoingMax.ToString("F4") + " state=" + latest.State.Locomotion + " item=" + latest.State.EquippedItemId + " pitch=" + latest.State.LookPitch.ToString("F2"));
                    outgoingSamples = 0; outgoingMin = float.PositiveInfinity; outgoingMax = 0;
                }
                Thread.Sleep(5);
            }
            Console.WriteLine("TRANSPORT HARNESS: connected="+connected+" sent="+sent+" sampled="+received+" error="+network.LastError);
            var statistics = network.Telemetry.Statistics;
            Console.WriteLine("METRICS: " + JsonSerializer.Serialize(new { mode, role = host ? "host" : "client", mod = Protocol.ModVersion,
                connected, state = network.Session.State.ToString(), sent, received = network.Telemetry.Accepted, sampled = received,
                gaps = network.Telemetry.SequenceGaps, intervals = statistics.Samples, meanMs = statistics.MeanMilliseconds,
                minMs = statistics.MinimumMilliseconds, maxMs = statistics.MaximumMilliseconds, jitterMs = statistics.JitterMilliseconds,
                packetsSent = network.PacketsSent, packetsReceived = network.PacketsReceived }));
            return connected && sent > 0 && received > 0 && (!worldFixture || worldPassed) && (!interactionFixture || interactions.Passed) && (!inventoryFixture || inventory.Passed) && (!avatarFixture || host || presentationWorldReady) ? 0 : 1;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
