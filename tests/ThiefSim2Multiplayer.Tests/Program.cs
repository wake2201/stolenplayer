using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using NetworkManager = ThiefSim2Multiplayer.Core.Networking.NetworkManager;
using ThiefSim2Multiplayer.TransportHarness;

internal static class Program
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name); passed++;
    }
    private static Message Request(ushort version = Protocol.Version) => new Message { Type = MessageType.HandshakeRequest,
        ProtocolVersion = version, ModVersion = Protocol.ModVersion, PlayerName = "Test", BuildFingerprint = "build" };
    private static PlayerTransform Pose(uint sequence = 1, float yaw = 10) => new PlayerTransform {
        PlayerId = 2, SceneEpoch = 3, Sequence = sequence, Tick = 12345, X = -219.75f, Y = 0.55f, Z = 17.25f, Yaw = yaw };
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length==1 && args[0]=="health-068") {Health068Tests.Run(Check);Console.WriteLine(passed+" health 0.6.8 checks passed.");return 0;}
            if(args.Length==1 && args[0]=="gameplay-067") {Gameplay067Tests.Run(Check);Console.WriteLine(passed+" gameplay 0.6.7 checks passed.");return 0;}
            if(args.Length==1 && args[0]=="economy") { InventoryTests.Economy(Check); Console.WriteLine(passed+" economy checks passed."); return 0; }
            if(args.Length==1 && args[0]=="economy-transport") { InventoryTests.EconomyTransport(Check); Console.WriteLine(passed+" economy transport checks passed."); return 0; }
            if(args.Length==1 && args[0]=="travel") { CooperativeTravelTests.Run(Check); Console.WriteLine(passed+" cooperative travel checks passed."); return 0; }
            if(args.Length==1 && args[0]=="native-boolean") { NativeBooleanMemberTests.Run(Check); Console.WriteLine(passed+" native Boolean member checks passed."); return 0; }
            if (args.Length == 1 && args[0] == "vehicles") { VehicleTests.Run(Check); CooperativeTravelTests.Run(Check); NativeBooleanMemberTests.Run(Check); Console.WriteLine(passed + " vehicle checks passed."); return 0; }
            TestProtocol(); AvatarTests.Run(Check); TestStates(); TestInterpolation(); TestTelemetry(); TestTransport(); TestWireValidation(); TestNetworkConditions(); WorldTests.Run(Check); InteractionTests.Run(Check); InventoryTests.Run(Check); StabilizationTests.Run(Check); FrameStabilityTests.Run(Check); VehicleTests.Run(Check); CooperativeTravelTests.Run(Check); NativeBooleanMemberTests.Run(Check); Gameplay067Tests.Run(Check); Health068Tests.Run(Check);
            Console.WriteLine("AUTOMATED VERIFIED: " + passed + " checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void TestProtocol()
    {
        Message decoded;
        Check(Protocol.TryDecode(Protocol.Encode(Request()), out decoded) && decoded.PlayerName == "Test" && decoded.BuildFingerprint == "build" && decoded.ProtocolVersion == Protocol.Version, "handshake binary roundtrip");
        var accepted = new Message { Type = MessageType.HandshakeAccepted, ProtocolVersion = Protocol.Version, ModVersion = Protocol.ModVersion,
            PlayerName = "Host", BuildFingerprint = "build", LocalPlayerId = 2, RemotePlayerId = 1, SessionId = 777 };
        Check(Protocol.TryDecode(Protocol.Encode(accepted), out decoded) && decoded.LocalPlayerId == 2 && decoded.RemotePlayerId == 1 && decoded.SessionId == 777, "accepted handshake IDs/session roundtrip");
        var transform = new Message { Type = MessageType.PlayerTransform, Transform = Pose() };
        byte[] encoded = Protocol.Encode(transform);
        Check(encoded.Length == 30 && Protocol.TryDecode(encoded, out decoded) && decoded.Transform.X == -219.75f && decoded.Transform.Yaw == 10 && decoded.Transform.Tick == 12345 && decoded.Transform.Sequence == 1, "30-byte transform roundtrip");
        Check(!Protocol.TryDecode(new byte[] { 255 }, out decoded), "invalid message type");
        Check(!Protocol.TryDecode(encoded.Concat(new byte[] { 0 }).ToArray(), out decoded), "trailing data rejected");
        foreach (Message message in new[] { Request(), accepted, transform,
            new Message { Type = MessageType.HandshakeRejected, Reason = "Rejected" },
            new Message { Type = MessageType.PlayerLeft, Reason = "Left" },
            new Message { Type = MessageType.SceneState, Ready = true, SceneEpoch = 1, SceneKey = "MainScene|IntroHouse" } })
        {
            encoded = Protocol.Encode(message);
            for (int i = 0; i < encoded.Length; i++)
                if (Protocol.TryDecode(encoded.Take(i).ToArray(), out decoded)) throw new Exception("Truncation accepted");
            Check(Protocol.TryDecode(encoded, out decoded), "every truncation rejected / roundtrip " + message.Type);
        }
        var invalid = Pose(); invalid.X = float.NaN;
        Check(!Protocol.ValidPose(invalid), "NaN transform rejected"); invalid = Pose(); invalid.Y = float.PositiveInfinity;
        Check(!Protocol.ValidPose(invalid), "infinite transform rejected"); invalid = Pose(); invalid.PlayerId = 3;
        Check(!Protocol.ValidPose(invalid), "invalid player ID rejected"); invalid = Pose(); invalid.X = 100001;
        Check(!Protocol.ValidPose(invalid), "out-of-range position rejected"); invalid = Pose(); invalid.SceneEpoch = 0;
        Check(!Protocol.ValidPose(invalid), "zero scene epoch rejected");
        Check(!Protocol.TryDecode(new byte[513], out decoded), "oversize packet rejected");
        Check(NetworkManager.ValidateHandshake(Request(2), 1, "build", false).Contains("protocol"), "protocol mismatch rejection");
        Check(NetworkManager.ValidateHandshake(Request(), Protocol.Version, "other", false).Contains("game build"), "game build mismatch rejection");
        Check(NetworkManager.ValidateHandshake(Request(), Protocol.Version, "build", true).Contains("full"), "capacity/duplicate handshake rejection");
        var versionMismatch = Request(); versionMismatch.ModVersion = "0.2.1";
        Check(NetworkManager.ValidateHandshake(versionMismatch, Protocol.Version, "build", false).Contains("mod"), "mod version mismatch rejection");
        var rng = new Random(1337);
        for (int i = 0; i < 10000; i++) { var bytes = new byte[rng.Next(0, 600)]; rng.NextBytes(bytes); Protocol.TryDecode(bytes, out decoded); }
        Check(true, "10,000 malformed random packets cannot throw");
        var storage = new byte[50]; var originalPose = Pose();
        Check(Protocol.WriteTransform(originalPose, storage, 7) && storage.Skip(7).Take(30).SequenceEqual(Protocol.Encode(transform)), "reusable transform encoder preserves protocol v1 bytes");
        Check(Protocol.TryReadTransform(storage, 7, 30, out var direct) && direct.X == originalPose.X && direct.Tick == originalPose.Tick, "direct transform decoder handles transport buffer offset");
        Check(!Protocol.TryReadTransform(storage, -1, 30, out _) && !Protocol.TryReadTransform(storage, int.MaxValue, 30, out _) && !Protocol.TryReadTransform(storage, 7, 31, out _), "direct transform decoder rejects invalid slice bounds");
        var requestBytes = Protocol.Encode(Request()); var padded = new byte[requestBytes.Length + 12]; Array.Copy(requestBytes, 0, padded, 5, requestBytes.Length);
        Check(Protocol.TryDecode(padded, 5, requestBytes.Length, out decoded) && decoded.PlayerName == "Test", "bounded control decoder handles pooled buffer offset");
        Check(!Protocol.TryDecode(padded, 5, requestBytes.Length + 1, out decoded), "pooled control slice rejects trailing bytes");
        var floatCorruption = Protocol.Encode(transform); Array.Copy(BitConverter.GetBytes(float.NaN), 0, floatCorruption, 14, 4);
        Check(!Protocol.TryReadTransform(floatCorruption, 0, 30, out _), "direct decoder rejects wire NaN");
        for (int i = 0; i < 1000; i++) { Protocol.WriteTransform(originalPose, storage, 7); Protocol.TryReadTransform(storage, 7, 30, out direct); }
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { Protocol.WriteTransform(originalPose, storage, 7); Protocol.TryReadTransform(storage, 7, 30, out direct); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Check(allocated == 0, "10,000 reusable transform encode/decode pairs allocate zero managed bytes");
        for (int i = 0; i < 10000; i++) { var bytes = new byte[rng.Next(0, 600)]; rng.NextBytes(bytes); Protocol.TryReadTransform(bytes, rng.Next(-10, 650), rng.Next(-10, 650), out _); }
        Check(true, "10,000 malformed direct transform slices cannot throw");
    }
    private static void TestStates()
    {
        var host = new MultiplayerSession(); Check(host.BeginHost() && host.State == SessionState.StartingHost, "Offline -> StartingHost");
        host.HostStarted(); Check(host.State == SessionState.Hosting && host.LocalPlayerId == 1, "StartingHost -> Hosting ID 1");
        Check(!host.BeginHost() && !host.BeginConnect(), "active host prevents duplicate host/join"); host.Accept(1);
        Check(host.State == SessionState.Connected && host.RemotePlayerId == 2, "host Connected assigns client ID 2");
        host.PeerLost(); Check(host.State == SessionState.Hosting && host.RemotePlayerId == 0 && host.SessionId == 0, "host peer loss clears membership and returns Hosting");
        host.BeginDisconnect(); Check(host.State == SessionState.Disconnecting, "explicit Disconnecting state"); host.Stop();
        Check(host.State == SessionState.Offline && host.LocalPlayerId == 0, "host stop clears IDs");
        var client = new MultiplayerSession(); Check(client.BeginConnect() && !client.BeginHost(), "Offline -> Connecting prevents mode change"); client.Accept(9);
        Check(client.LocalPlayerId == 2 && client.RemotePlayerId == 1, "client Connected ID assignment"); client.PeerLost();
        Check(client.State == SessionState.Offline && client.RemotePlayerId == 0, "client peer loss returns Offline");
        bool rejected = false; try { client.Accept(1); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "invalid acceptance transition rejected");
    }
    private static void TestInterpolation()
    {
        Check(Math.Abs(TransformBuffer.InterpolateYaw(350,10,0.5f)) < 0.001, "yaw interpolation crosses 360 via shortest arc");
        Check(Math.Abs(TransformBuffer.InterpolateYaw(10,350,0.5f)) < 0.001, "reverse yaw interpolation");
        var buffer = new TransformBuffer(); var first = Pose(1,350); first.X = 0; var second = Pose(2,10); second.X = 10;
        buffer.Add(first, 1); buffer.Add(second, 1.1); PlayerTransform result;
        Check(buffer.TrySample(1.15,0.1,out result) && Math.Abs(result.X-5) < 0.001 && Math.Abs(result.Yaw) < 0.001, "position/yaw interpolation at delayed midpoint");
        Check(!buffer.Add(first,1.2), "stale/duplicate sequence ignored");
        Check(!buffer.TrySample(2.2,0.1,out result), "stale transform hidden after one second");
        Check(Protocol.IsNewer(0,uint.MaxValue) && !Protocol.IsNewer(uint.MaxValue,0), "uint sequence wrap ordering");
        for (uint i = 3; i < 80; i++) buffer.Add(Pose(i), 1+i*0.01);
        Check(buffer.Count == 32, "jitter buffer is bounded"); buffer.Clear();
        Check(buffer.Count == 0 && !buffer.TrySample(2,0.1,out result) && buffer.Add(Pose(1),2), "disconnect cleanup resets buffer and sequence");
        Check(buffer.TrySample(2,0.5,out result) && result.X == Pose().X, "first snapshot snaps immediately despite interpolation delay");
        var jumped = Pose(2); jumped.X += 100;
        buffer.Add(jumped, 2.05);
        Check(buffer.Count == 1 && buffer.SnapCount == 1 && buffer.TrySample(2.05,0.1,out result) && result.X == jumped.X, "large discontinuity clears interpolation and snaps immediately");
        Check(!buffer.Add(Pose(1),2.06), "old pre-teleport snapshot cannot rewind snapped state");
        buffer.Clear(); buffer.Add(Pose(10),3); buffer.Add(Pose(11),3.05);
        Check(!buffer.Add(Pose(10),3.1) && !buffer.Add(Pose(9),3.15) && buffer.LastSequence == 11, "N N+1 N N-1 preserves newest sequence");
        buffer.Clear(); buffer.Add(Pose(uint.MaxValue),4);
        Check(buffer.Add(Pose(0),4.05) && !buffer.Add(Pose(uint.MaxValue),4.1), "snapshot buffer sequence wrap rejects the old side");
        Check(!buffer.Add(Pose(1),double.NaN) && !buffer.TrySample(double.NaN,0.1,out result), "invalid local timeline cannot produce NaN render state");
    }
    private static void TestTelemetry()
    {
        var metrics = new SnapshotTelemetry(); metrics.Record(Pose(1),1); metrics.Record(Pose(2),1.04); metrics.Record(Pose(4),1.10);
        var stats = metrics.Statistics;
        Check(stats.Samples == 2 && Math.Abs(stats.MeanMilliseconds-50) < 0.001 && Math.Abs(stats.JitterMilliseconds-10) < 0.001, "arrival telemetry computes mean and population jitter");
        Check(Math.Abs(stats.MinimumMilliseconds-40) < 0.001 && Math.Abs(stats.MaximumMilliseconds-60) < 0.001 && metrics.SequenceGaps == 1, "arrival telemetry records range and sequence gaps");
        Check(!metrics.Record(Pose(3),1.15) && metrics.Latest.Sequence == 4 && metrics.Accepted == 3, "telemetry refuses stale snapshots");
        for (uint i = 5; i < 300; i++) metrics.Record(Pose(i),1.1+i*0.05);
        Check(metrics.Statistics.Samples == 128, "arrival telemetry remains bounded at 128 intervals");
    }
    private static int FreePort()
    {
        using (var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback,0))) return ((IPEndPoint)socket.Client.LocalEndPoint).Port;
    }
    private static void Pump(Func<bool> done, params NetworkManager[] managers)
    {
        var timer = Stopwatch.StartNew();
        // A dropped transport-close datagram is resolved by the configured 5 s
        // peer timeout. Allow that documented fallback, not just normal delivery.
        while (!done() && timer.Elapsed.TotalSeconds < 6.5) { foreach(var manager in managers) manager.Poll(); Thread.Sleep(5); }
        foreach(var manager in managers) manager.Poll();
        if (!done()) throw new Exception("Transport timeout: " + string.Join(" | ", managers.Select(m => m.Session.State + ": " + m.LastError)));
    }
    private static void TestTransport()
    {
        using var host = new NetworkManager("build"); using var client = new NetworkManager("build");
        host.LocalAvatarId = 1; client.LocalAvatarId = 0;
        int removed = 0; host.RemoteRemoved += () => removed++;
        Check(host.Host(FreePort(),"Host"), "real UDP host binds");
        var rawListener = new LiteNetLib.EventBasedNetListener();
        var raw = new LiteNetLib.NetManager(rawListener) { IPv6Enabled = false };
        bool rawRejected = false;
        rawListener.PeerDisconnectedEvent += (_, info) => rawRejected = info.Reason == LiteNetLib.DisconnectReason.ConnectionRejected;
        try
        {
            raw.Start(); raw.Connect("127.0.0.1",host.Port,new LiteNetLib.Utils.NetDataWriter());
            var wait = Stopwatch.StartNew();
            while (!rawRejected && wait.Elapsed.TotalSeconds < 4) { raw.PollEvents(); host.Poll(); Thread.Sleep(5); }
            if (!rawRejected || host.Session.State != SessionState.Hosting) Console.WriteLine("RAW HANDSHAKE DIAGNOSTIC: rejected=" + rawRejected + " state=" + host.Session.State + " error=" + host.LastError);
            Check(rawRejected && host.Session.State == SessionState.Hosting, "empty wire handshake rejected without shutting down host");
        }
        finally { raw.Stop(); }
        Check(!host.Host(host.Port,"Host") && !host.Join("127.0.0.1",host.Port,"Client"), "transport invalid mode actions blocked");
        host.SetLocalScene(true,"MainScene|IntroHouse"); client.SetLocalScene(true,"MainScene|IntroHouse");
        Check(client.Join("127.0.0.1",host.Port,"Client"), "real UDP client starts");
        Pump(() => host.ScenesCompatible && client.ScenesCompatible, host,client);
        Check(host.Session.LocalPlayerId == 1 && client.Session.LocalPlayerId == 2 && host.Session.SessionId == client.Session.SessionId, "wire handshake and host-assigned membership");
        Check(host.RemoteAvatarId == 0 && client.RemoteAvatarId == 1, "actual UDP handshakes retain both peers' distinct AvatarIds");
        Check(host.RemoteModVersion == Protocol.ModVersion && client.HandshakeState == "Accepted" && host.PeerConnected, "acceptance telemetry reports peer and remote mod");
        Exception crossThread = null; var thread = new Thread(() => { try { host.Poll(); } catch (Exception error) { crossThread = error; } }); thread.Start(); thread.Join();
        Check(crossThread is InvalidOperationException && host.Session.State == SessionState.Connected, "wrong-thread polling is rejected before any callback or Unity mutation");
        using (var third = new NetworkManager("build"))
        {
            third.Join("127.0.0.1",host.Port,"Third"); Pump(() => third.Session.State == SessionState.Offline,host,client,third);
            Check(third.LastError.Contains("full") && host.Session.State == SessionState.Connected, "third peer rejected without disturbing existing session");
        }
        Check(host.SendTransform(1,2,3,359) && client.SendTransform(4,5,6,1), "both directions send sequenced transforms");
        Pump(() => host.RemoteTransforms.Count > 0 && client.RemoteTransforms.Count > 0,host,client);
        PlayerTransform pose; host.RemoteTransforms.TrySample(host.Now,0,out pose);
        Check(pose.X == 4 && pose.PlayerId == 2, "host receives client pose"); client.RemoteTransforms.TrySample(client.Now,0,out pose);
        Check(pose.X == 1 && pose.PlayerId == 1, "client receives host pose");
        Check(host.Telemetry.Accepted == 1 && client.Telemetry.Sent == 1 && host.PacketsReceived > 0 && client.PacketsSent > 0, "live transport counters and accepted snapshot telemetry");
        client.SetLocalScene(false,""); Pump(() => !host.ScenesCompatible,host,client);
        Check(!host.SendTransform(1,2,3,0) && host.RemoteTransforms.Count == 0, "loading readiness clears buffer and stops pose traffic");
        client.SetLocalScene(true,"DifferentScene"); Pump(() => client.Session.State == SessionState.Connected,host,client);
        // Allow reliable scene-state delivery to settle without relying on frame timing.
        for(int i=0;i<30;i++) { host.Poll(); client.Poll(); Thread.Sleep(5); }
        Check(!host.ScenesCompatible && !client.ScenesCompatible, "mismatched scenes hide proxies");
        client.SetLocalScene(true,"MainScene|IntroHouse"); Pump(() => host.ScenesCompatible && client.ScenesCompatible,host,client);
        Check(client.SendTransform(7,8,9,90), "readiness recovery resumes traffic");
        Pump(() => host.RemoteTransforms.Count > 0,host,client);
        client.Disconnect(); Pump(() => host.Session.State == SessionState.Hosting,host,client);
        Check(client.Session.State == SessionState.Offline && host.Session.RemotePlayerId == 0 && host.RemoteTransforms.Count == 0 && removed > 0, "wire disconnect destroys independent peer/buffer state");
        using (var incompatible = new NetworkManager("build",version:1))
        {
            incompatible.Join("127.0.0.1",host.Port,"WrongVersion"); Pump(() => incompatible.Session.State == SessionState.Offline,host,incompatible);
            Check(incompatible.LastError.Contains("protocol"), "wire incompatible protocol rejected cleanly");
        }
        using (var wrongBuild = new NetworkManager("other"))
        {
            wrongBuild.Join("127.0.0.1",host.Port,"WrongBuild"); Pump(() => wrongBuild.Session.State == SessionState.Offline,host,wrongBuild);
            Check(wrongBuild.LastError.Contains("game build"), "wire game build mismatch rejected cleanly");
        }
        ulong previousSession = 0;
        for (int cycle = 0; cycle < 3; cycle++)
        {
            client.Join("127.0.0.1",host.Port,"Client"); Pump(() => host.ScenesCompatible && client.ScenesCompatible,host,client);
            Check(client.Session.LocalPlayerId == 2 && host.Session.RemotePlayerId == 2 && client.Session.SessionId != previousSession && host.RemoteTransforms.Count == 0, "reconnect cycle " + (cycle+1) + " assigns fresh membership without stale buffer");
            previousSession = client.Session.SessionId;
            if (cycle < 2) { client.Disconnect(); Pump(() => host.Session.State == SessionState.Hosting,host,client); }
        }
        host.Disconnect("Host exited."); Pump(() => client.Session.State == SessionState.Offline,host,client);
        Check(client.Session.RemotePlayerId == 0 && client.RemoteTransforms.Count == 0, "host exit clears client membership and transforms");
        host.Host(FreePort(),"Host"); client.Join("127.0.0.1",host.Port,"Client"); client.Disconnect();
        Check(client.Session.State == SessionState.Offline && !client.PeerConnected && client.Session.LocalPlayerId == 0, "disconnect during handshake immediately clears client identity/transport");
    }
    private static void TestWireValidation()
    {
        using var client = new NetworkManager("build"); var listener = new LiteNetLib.EventBasedNetListener();
        var rawHost = new LiteNetLib.NetManager(listener) { IPv6Enabled = false, ChannelsCount = 2 }; LiteNetLib.NetPeer sender = null;
        listener.ConnectionRequestEvent += request => request.Accept(); listener.PeerConnectedEvent += connected => sender = connected;
        try
        {
            rawHost.Start(FreePort()); client.SetLocalScene(true,"MainScene"); client.Join("127.0.0.1",rawHost.LocalPort,"Client");
            var timer = Stopwatch.StartNew();
            while (sender == null && timer.Elapsed.TotalSeconds < 4) { rawHost.PollEvents(); client.Poll(); Thread.Sleep(5); }
            if (sender == null) throw new Exception("Raw host failed");
            var pose = Pose(1); pose.PlayerId = 1; pose.SceneEpoch = 1;
            void SendPose(PlayerTransform value, byte channel = 1) => sender.Send(Protocol.Encode(new Message { Type = MessageType.PlayerState, Transform = value }),channel,LiteNetLib.DeliveryMethod.Sequenced);
            void Settle() { for(int i=0;i<20;i++) { rawHost.PollEvents(); client.Poll(); Thread.Sleep(5); } }
            SendPose(pose); Settle(); Check(client.RemoteTransforms.Count == 0, "wire transform before application handshake is ignored");
            sender.Send(Protocol.Encode(new Message { Type = MessageType.HandshakeAccepted, ProtocolVersion = Protocol.Version, ModVersion = Protocol.ModVersion, PlayerName = "RawHost", BuildFingerprint = "build", LocalPlayerId = 2, RemotePlayerId = 1, SessionId = 123 }),0,LiteNetLib.DeliveryMethod.ReliableOrdered);
            sender.Send(Protocol.Encode(new Message { Type = MessageType.SceneState, Ready = true, SceneEpoch = 1, SceneKey = "MainScene" }),0,LiteNetLib.DeliveryMethod.ReliableOrdered); Settle();
            SendPose(pose); Settle();
            Check(client.RemoteTransforms.LastSequence == 1, "valid wire pose accepted after readiness");
            pose.Sequence = 2; SendPose(pose); Settle(); pose.Sequence = 1; SendPose(pose); Settle(); pose.Sequence = 0; SendPose(pose); Settle();
            Check(client.RemoteTransforms.LastSequence == 2 && client.Telemetry.Rejected >= 2, "wire N N+1 N N-1 cannot rewind state");
            pose.Sequence = 3; pose.PlayerId = 2; SendPose(pose); Settle(); pose.PlayerId = 1; pose.SceneEpoch = 2; SendPose(pose); Settle();
            Check(client.RemoteTransforms.LastSequence == 2, "wrong wire identity and scene epoch cannot enter render buffer");
            pose.SceneEpoch = 1; SendPose(pose,0); Settle();
            Check(client.RemoteTransforms.LastSequence == 2, "transform on control channel is ignored");
        }
        finally { rawHost.Stop(); }
    }
    private static void TestNetworkConditions()
    {
        foreach (var simulation in new[] { (LatencyMilliseconds:50,JitterMilliseconds:20,LossPercent:5), (LatencyMilliseconds:100,JitterMilliseconds:40,LossPercent:10) })
        {
            using var host = new NetworkManager("build"); using var client = new NetworkManager("build");
            host.SetLocalScene(true,"MainScene"); client.SetLocalScene(true,"MainScene"); host.Host(FreePort(),"Host");
            using var link = new UdpConditionLink(0,host.Port,simulation.LatencyMilliseconds,simulation.JitterMilliseconds,simulation.LossPercent);
            client.Join("127.0.0.1",link.Port,"Client");
            Pump(() => host.ScenesCompatible && client.ScenesCompatible,host,client);
            int callbackThread = -1; host.RemoteRemoved += () => callbackThread = Thread.CurrentThread.ManagedThreadId;
            var clock = Stopwatch.StartNew(); double next = 0; bool regressed = false; float lastX = float.MinValue; int rendered = 0;
            while (clock.Elapsed.TotalSeconds < 3)
            {
                host.Poll(); client.Poll(); double time = clock.Elapsed.TotalSeconds;
                if (time >= next) { next = time+0.05; host.SendTransform((float)time,0,0,(float)time); client.SendTransform((float)time,0,0,(float)time); }
                if (client.RemoteTransforms.TrySample(client.Now,0.1,out var render))
                { if (render.X < lastX-0.0001f) regressed = true; lastX = render.X; rendered++; }
                Thread.Sleep(5);
            }
            Check(host.Telemetry.Accepted > 20 && client.Telemetry.Accepted > 20 && host.Session.State == SessionState.Connected, "reliable handshake and bidirectional snapshots under " + simulation.LatencyMilliseconds + " ms latency / " + simulation.JitterMilliseconds + " ms jitter / " + simulation.LossPercent + "% loss");
            Check(link.Dropped > 0 && link.QueueOverflow == 0 && link.Error.Length == 0 && !regressed && rendered > 20, "real datagram loss occurs and delayed interpolation never rewinds under " + simulation.LossPercent + "% loss");
            var stats = client.Telemetry.Statistics;
            Console.WriteLine("CONDITION: latency=" + simulation.LatencyMilliseconds + " jitter=" + simulation.JitterMilliseconds + " loss=" + simulation.LossPercent + " arrivals=" + client.Telemetry.Accepted + " mean=" + stats.MeanMilliseconds.ToString("F1") + " min=" + stats.MinimumMilliseconds.ToString("F1") + " max=" + stats.MaximumMilliseconds.ToString("F1") + " stddev=" + stats.JitterMilliseconds.ToString("F1") + " gaps=" + client.Telemetry.SequenceGaps);
            client.Disconnect(); Pump(() => host.Session.State == SessionState.Hosting,host,client);
            Check(callbackThread == Thread.CurrentThread.ManagedThreadId, "lossy disconnect cleanup callback runs on polling thread");
        }
    }
}
