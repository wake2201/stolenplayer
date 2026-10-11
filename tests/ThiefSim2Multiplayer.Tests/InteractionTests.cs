using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.TransportHarness;

internal static class InteractionTests
{
    private sealed class Adapter : IInteractionAdapter
    {
        public bool Open, Alive = true, Allowed = true, Fail, FailApply;
        public int Executions, Applies;
        public EntityTypeId Type = EntityTypeId.Door;
        public EntityTypeId EntityType => Type;
        public bool IsAlive => Alive;
        public bool Supports(InteractionKind kind) => kind == InteractionKind.Open || kind == InteractionKind.Close;
        public bool Permits(InteractionKind kind) => Allowed;
        public void Execute(InteractionKind kind) { Executions++; if (Fail) throw new InvalidOperationException(); Open = kind == InteractionKind.Open; }
        public byte[] CaptureState() => Type == EntityTypeId.Window ? new WindowState { Open = Open, Enabled = true }.Encode() : new DoorState { Open = Open, Enabled = true }.Encode();
        public bool ValidateState(byte[] payload) { DoorState d; WindowState w; return Type == EntityTypeId.Window ? WindowState.TryDecode(payload, out w) : DoorState.TryDecode(payload, out d); }
        public void ApplyState(byte[] payload)
        {
            if (FailApply) throw new InvalidOperationException("Synthetic application failure");
            using (new NetworkApplyScope()) { if (!NetworkApplyScope.Active) throw new Exception("Missing scope"); DoorState d; WindowState w; if (Type == EntityTypeId.Window) { WindowState.TryDecode(payload, out w); Open = w.Open; } else { DoorState.TryDecode(payload, out d); Open = d.Open; } Applies++; }
        }
        public void Release() { Alive = false; }
    }
    private static StaticEntityDescriptor Descriptor => new StaticEntityDescriptor("build", "scene", "World[0]/Door[0]", "door-v1", 0);
    private static InteractionRequest Request(EntityRef entity, ulong id = 1, ulong rev = 1) => new InteractionRequest { SessionId = 7, SceneEpoch = 1, ActorId = 2, Entity = entity, RequestId = id, Kind = InteractionKind.Open, ExpectedRevision = rev };
    internal static void Run(Action<bool, string> check)
    {
        DoorState door; WindowState window;
        var bytes = new DoorState { Open = true, Enabled = true, RequiresKey = true, RelockAvailable = true, BrokenHinge = true, Tag = InteractionTag.Lock1 }.Encode();
        check(DoorState.TryDecode(bytes, out door) && door.Open && door.RequiresKey && door.RelockAvailable && door.BrokenHinge && door.Tag == InteractionTag.Lock1, "D door semantic codec all verified flags");
        check(!DoorState.TryDecode(new byte[] { 1, 32, 0 }, out door) && !DoorState.TryDecode(new byte[] { 1, 0, 5 }, out door) && !DoorState.TryDecode(new byte[] { 2, 0, 0 }, out door), "D door codec rejects reserved bits tag schema");
        check(WindowState.TryDecode(new WindowState { Locked = true, PaneVisible = true, Tag = InteractionTag.Window }.Encode(), out window) && window.Tag == InteractionTag.Window && window.Locked, "D authored locked Window tag remains representable after initialization");
        check(WindowState.TryDecode(new WindowState { Open = true, Enabled = true, Glass = WindowGlass.Broken }.Encode(), out window) && window.Glass == WindowGlass.Broken && window.Open, "D independent window semantic codec");
        check(!WindowState.TryDecode(new byte[] { 1, 4, 1, 0 }, out window) && !WindowState.TryDecode(new byte[] { 1, 0, 3, 0 }, out window) && !WindowState.TryDecode(new byte[] { 1, 16, 0, 0 }, out window), "D impossible window locked/broken state and invalid enum rejected");
        check(WindowState.TryDecode(new WindowState { PaneVisible = true, Glass = WindowGlass.Cut }.Encode(), out window) && window.PaneVisible && !window.Locked, "D unlocked intact pane preserved for UnlockWindowNoAlarm");
        check(StaticEntityDescriptor.ParseCanonical(Descriptor.Canonical).DeriveId().Equals(Descriptor.DeriveId()), "D harness parses canonical real static descriptor without inventing identity");
        bool parseRejected = false; try { StaticEntityDescriptor.ParseCanonical(Descriptor.Canonical + "x"); } catch (ArgumentException) { parseRejected = true; }
        check(parseRejected, "D captured manifest rejects noncanonical descriptor");
        var host = new NetworkEntityRegistry(true, 1); var adapter = new Adapter(); var entity = host.RegisterStatic(Descriptor, adapter, new EntityOwnership(OwnershipKind.System));
        var pendingRegistry = new NetworkEntityRegistry(false, 1);
        pendingRegistry.RegisterStatic(Descriptor, new Adapter(), new EntityOwnership(OwnershipKind.System));
        check(pendingRegistry.DevelopmentSummary().Contains("PENDING BASELINE"), "D pre-baseline client diagnostic reports pending without inventing a canonical hash");
        var request = Request(entity); var encoded = InteractionProtocol.Encode(request); InteractionRequest decoded;
        check(encoded.Length == 44 && InteractionProtocol.TryDecode(encoded, 0, encoded.Length, out decoded) && decoded.Entity.Equals(entity) && decoded.ExpectedRevision == 1 && decoded.ActorId == 2, "D bounded 44-byte interaction request roundtrip");
        check(Enumerable.Range(0, encoded.Length).All(n => !InteractionProtocol.TryDecode(encoded, 0, n, out decoded)) && !InteractionProtocol.TryDecode(encoded.Concat(new byte[] { 0 }).ToArray(), 0, 45, out decoded), "D every request truncation and trailing bytes rejected");
        foreach (int offset in new[] { 0, 21, 22, 35 })
        { var bad = (byte[])encoded.Clone(); bad[offset] = 255; check(!InteractionProtocol.TryDecode(bad, 0, bad.Length, out decoded), "D malformed request field offset " + offset); }
        var zeroEpoch = (byte[])encoded.Clone(); Array.Clear(zeroEpoch, 9, 4); check(!InteractionProtocol.TryDecode(zeroEpoch, 0, 44, out decoded), "D zero wire epoch rejected");
        for (int n = 13; n < 21; n++) encoded[n] = 0;
        check(!InteractionProtocol.TryDecode(encoded, 0, encoded.Length, out decoded), "D zero request ID rejected");
        var resultBytes = InteractionProtocol.EncodeResult(7, 1, 1, InteractionResult.Accepted, 2);
        check(resultBytes.Length == 30 && InteractionProtocol.TryDecodeResult(resultBytes, 0, 30, out var session, out var epoch, out var id, out var result, out var revision) && revision == 2, "D bounded result codec roundtrip");
        var telemetry = new InteractionTelemetry(); var authority = new InteractionAuthority(telemetry);
        InteractionResult Handle(InteractionRequest r, bool ready = true, ulong token = 7, byte actor = 2, InteractionResult target = InteractionResult.Accepted) => authority.Handle(host, r, token, actor, ready, r.RequestId, (_, _) => target);
        check(Handle(request, ready: false) == InteractionResult.NotReady && adapter.Executions == 0, "D requests before WorldReady ignored");
        var wrong = Request(entity); wrong.SessionId = 8; check(Handle(wrong) == InteractionResult.WrongSession, "D wrong session rejected");
        wrong = Request(entity); wrong.SceneEpoch = 2; check(Handle(wrong) == InteractionResult.WrongEpoch, "D wrong epoch rejected");
        wrong = Request(entity); wrong.ActorId = 1; check(Handle(wrong) == InteractionResult.WrongActor, "D peer cannot spoof actor");
        check(Handle(request) == InteractionResult.Accepted && adapter.Open && adapter.Executions == 1 && host.WorldRevision == 2, "D client intent executes host canonical state once and commits revision");
        check(Handle(request) == InteractionResult.Duplicate && adapter.Executions == 1 && host.WorldRevision == 2, "D duplicate request idempotent");
        request.Kind = InteractionKind.Close; request.ExpectedRevision = 2;
        check(Handle(request) == InteractionResult.Duplicate && adapter.Open, "D changed action with duplicate ID cannot execute");
        check(Handle(Request(entity, 2, 1)) == InteractionResult.StaleRevision && adapter.Executions == 1, "D stale revision rejects double toggle after host ordering");
        check(Handle(Request(entity, 3, 99)) == InteractionResult.FutureRevision, "D future revision rejected");
        check(Handle(Request(new EntityRef(entity.Id, 2), 4, 2)) == InteractionResult.WrongGeneration, "D wrong generation rejected");
        check(Handle(Request(new EntityRef(new NetworkEntityId(EntityScope.Static, 999), 1), 5, 2)) == InteractionResult.UnknownEntity, "D unknown entity rejected");
        check(Handle(Request(entity, 6, 2), target: InteractionResult.OutOfRange) == InteractionResult.OutOfRange && Handle(Request(entity, 7, 2), target: InteractionResult.InvalidTarget) == InteractionResult.InvalidTarget, "D host range and line-of-sight abstraction rejects invalid targets");
        adapter.Allowed = false; check(Handle(Request(entity, 8, 2)) == InteractionResult.NotPermitted, "D game transition/lock capability rejects request"); adapter.Allowed = true;
        adapter.Type = EntityTypeId.Pickup; var pickup = host.Spawn(adapter, new EntityOwnership(OwnershipKind.System)); adapter.Type = EntityTypeId.Door;
        check(Handle(Request(pickup, 9, 3)) == InteractionResult.Unsupported, "D wrong entity type cannot invoke door action");
        adapter.Fail = true; check(Handle(Request(entity, 10, 2)) == InteractionResult.ExecutionFailed && Handle(Request(entity, 10, 2)) == InteractionResult.Duplicate, "D throwing execution consumed once"); adapter.Fail = false;
        check(InteractionAuthority.InRange(2.75f * 2.75f, 2.25f, .5f) && !InteractionAuthority.InRange(8, 2.25f, .5f) && !InteractionAuthority.InRange(float.NaN, 2.25f, .5f) && !InteractionAuthority.InRange(-1, 2.25f, .5f), "D range tolerance finite bounded original reach");
        for (ulong n = 11; n < 150; n++) Handle(Request(entity, n, 1));
        check(Handle(Request(entity, 1, 2)) == InteractionResult.Duplicate, "D old ID stays burned beyond bounded recent cache");
        using (new NetworkApplyScope()) { using (new NetworkApplyScope()) check(NetworkApplyScope.Active, "D nested network apply guard active"); check(NetworkApplyScope.Active, "D inner disposal retains guard"); }
        check(!NetworkApplyScope.Active, "D network apply scope unwinds recursion suppression");
        using (new NetworkApplyScope(true)) { using (new NetworkApplyScope()) check(NetworkApplyScope.Silent, "D baseline silence survives nested adapter scopes"); }
        check(!NetworkApplyScope.Silent, "D baseline silence unwinds safely");
        var rateAuthority = new InteractionAuthority(new InteractionTelemetry()); InteractionResult rateResult = InteractionResult.Accepted;
        for (ulong n = 1; n <= 21; n++) rateResult = rateAuthority.Handle(host, Request(entity, n, 2), 7, 2, true, .1, (_, _) => InteractionResult.Accepted);
        check(rateResult == InteractionResult.RateLimited, "D actor interaction burst bounded to 20 per second");
        var client = new NetworkEntityRegistry(false, 1); var replica = new Adapter(); client.RegisterStatic(Descriptor, replica, new EntityOwnership(OwnershipKind.System));
        // Fresh registry isolates the baseline/delta race from protocol rejection cases.
        host = new NetworkEntityRegistry(true, 1); adapter = new Adapter(); entity = host.RegisterStatic(Descriptor, adapter, new EntityOwnership(OwnershipKind.System));
        var baseline = host.Capture(); EntityChange delta = null; host.Changed += change => delta = change;
        adapter.Open = true; host.CommitState(entity);
        check(client.ApplyBaseline(baseline, 1, _ => null) && !replica.Open, "D baseline carries closed semantic door state");
        check(client.ApplyChange(1, delta, _ => null) == ApplyResult.Applied && replica.Open && client.WorldRevision == 2, "D concurrent post-baseline host local interaction wins");
        check(client.DevelopmentSummary() == host.DevelopmentSummary(), "D registry hash includes semantic state and revision");
        var hash = client.DevelopmentSummary(); adapter.Open = false; host.CommitState(entity);
        check(host.DevelopmentSummary() != hash && client.ApplyChange(1, delta, _ => null) == ApplyResult.Applied, "D door change alters canonical state hash");
        host.Despawn(entity); check(client.ApplyChange(1, delta, _ => null) == ApplyResult.Applied && !replica.IsAlive, "D despawn clears interaction binding");
        check(authority.Handle(host, Request(entity, 200, 4), 7, 2, true, 200, (_, _) => InteractionResult.Accepted) == InteractionResult.UnknownEntity, "D request against invalidated entity safe");
        var wh = new NetworkEntityRegistry(true, 1); var wc = new NetworkEntityRegistry(false, 1);
        var wa = new Adapter { Type = EntityTypeId.Window, Open = true }; var wr = new Adapter { Type = EntityTypeId.Window };
        var wd = new StaticEntityDescriptor("build", "scene", "World[0]/Window[1]", "window-v1", 0);
        wh.RegisterStatic(wd, wa, new EntityOwnership(OwnershipKind.System)); wc.RegisterStatic(wd, wr, new EntityOwnership(OwnershipKind.System));
        check(wc.ApplyBaseline(wh.Capture(), wh.WorldRevision, _ => null) && wr.Open && wh.DevelopmentSummary() == wc.DevelopmentSummary(), "D independent window baseline and state hash match");
        Recovery(check);
        Transport(check, false); Transport(check, true);
    }
    private static void Recovery(Action<bool, string> check)
    {
        var toHost = new Queue<WorldMessage>(); var toClient = new Queue<WorldMessage>();
        var host = new WorldReplication(m => toClient.Enqueue(m)); var client = new WorldReplication(m => toHost.Enqueue(m));
        host.ConfigureRole(true); var adapter = new Adapter(); var entity = host.Registry.RegisterStatic(Descriptor, adapter, new EntityOwnership(OwnershipKind.System));
        bool fail = false; Adapter replica = null;
        client.PrepareRegistry = () => { if (client.Registry.Count == 0) { replica = new Adapter { FailApply = fail }; client.Registry.RegisterStatic(Descriptor, replica, new EntityOwnership(OwnershipKind.System)); } };
        host.BeginConnection(true, 7, 0); client.BeginConnection(false, 7, 0);
        double now = 0;
        void Step()
        {
            now += .5; host.Tick(now, true);
            while (toClient.Count > 0) client.Handle(toClient.Dequeue(), now);
            client.Tick(now, true); while (toHost.Count > 0) host.Handle(toHost.Dequeue(), now);
        }
        for (int n = 0; n < 20 && client.State != WorldSyncState.WorldReady; n++) Step();
        check(client.State == WorldSyncState.WorldReady, "D recovery fixture begins with matching static baseline");
        var failedBinding = replica;
        fail = replica.FailApply = true; adapter.Open = true; host.Registry.CommitState(entity); Step();
        check(client.AdapterApplyFailures == 1 && !failedBinding.IsAlive && client.Registry.WorldRevision == 0 && client.State != WorldSyncState.WorldReady, "D failed delta releases old binding and gates fresh binding until resnapshot");
        for (int n = 0; n < 30 && client.State != WorldSyncState.Failed; n++) Step();
        check(client.State == WorldSyncState.Failed && client.AdapterApplyFailures <= 4 && client.Registry.Count == 0, "D persistent adapter failure ends visibly after bounded recovery attempts");
        client.BeginConnection(false, 8, 100);
        check(client.LastError.Length == 0 && client.State == WorldSyncState.Connected, "D fresh connection clears prior sync failure diagnostic");
    }
    private static void Transport(Action<bool, string> check, bool impaired)
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); int port = ((IPEndPoint)socket.Client.LocalEndPoint).Port; socket.Close();
        using var host = new NetworkManager("build"); using var client = new NetworkManager("build");
        host.Host(port, "Host"); var adapter = new Adapter(); EntityRef entity = host.World.Registry.RegisterStatic(Descriptor, adapter, new EntityOwnership(OwnershipKind.System));
        using var link = impaired ? new UdpConditionLink(0, port, 60, 180, 10, 20) : null;
        client.World.PrepareRegistry = () => { if (client.World.Registry.Count == 0) client.World.Registry.RegisterStatic(Descriptor, new Adapter(), new EntityOwnership(OwnershipKind.System)); };
        host.ValidateInteractionTarget = (_, _) => InteractionResult.Accepted;
        int joinPort = link == null ? port : link.Port;
        host.SetLocalScene(true, "scene"); client.SetLocalScene(true, "scene"); client.Join("127.0.0.1", joinPort, "Client");
        void Pump(Func<bool> complete)
        { var timer = Stopwatch.StartNew(); while (!complete() && timer.Elapsed.TotalSeconds < 8) { host.Poll(); client.Poll(); Thread.Sleep(5); } if (!complete()) throw new Exception("D transport timeout: " + host.LastError + " / " + client.World.LastError); }
        Pump(() => client.World.State == WorldSyncState.WorldReady && host.World.State == WorldSyncState.WorldReady);
        check(client.SendInteraction(entity, InteractionKind.Open), "D actual UDP request sent after baseline ready");
        Pump(() => adapter.Open && client.World.Registry.WorldRevision == host.World.Registry.WorldRevision && client.Interactions.Accepted == 1);
        check(((Adapter)client.World.Registry.GetBinding(entity)).Open && host.Interactions.Accepted == 1 && client.World.DeltasApplied == 1, "D actual UDP host executes and client applies authoritative delta");
        var duplicate = new InteractionRequest { SessionId = client.Session.SessionId, SceneEpoch = 1, RequestId = 1, ActorId = client.Session.LocalPlayerId, Entity = entity, Kind = InteractionKind.Open, ExpectedRevision = 1 };
        client.SendInteractionRequest(duplicate); Pump(() => host.Interactions.Duplicates == 1);
        check(adapter.Executions == 1 && host.World.Registry.WorldRevision == 2, "D duplicate over reliable UDP cannot execute twice");
        ulong oldSnapshot = client.World.SnapshotId;
        duplicate.RequestId = 2; client.SendInteractionRequest(duplicate);
        Pump(() => host.Interactions.Stale == 1 && client.World.SnapshotId > oldSnapshot && client.World.State == WorldSyncState.WorldReady);
        check(adapter.Executions == 1 && ((Adapter)client.World.Registry.GetBinding(entity)).Open, "D actual UDP stale request receives fresh current baseline");
        check(client.SendInteraction(entity, InteractionKind.Close), "D confirmed semantic close request after resync");
        Pump(() => !adapter.Open && client.World.Registry.WorldRevision == 3);
        check(client.SendInteraction(entity, InteractionKind.Open), "D simultaneous client request starts at current closed revision");
        adapter.Open = true; host.World.Registry.CommitState(entity);
        Pump(() => host.Interactions.Stale == 2 && client.World.Registry.WorldRevision == 4 && client.World.State == WorldSyncState.WorldReady);
        check(adapter.Executions == 2 && ((Adapter)client.World.Registry.GetBinding(entity)).Open, "D host-first race cannot double toggle or execute stale open");
        client.Disconnect(); Pump(() => host.Session.State == ThiefSim2Multiplayer.Core.SessionState.Hosting);
        check(adapter.Open && host.World.Registry.WorldRevision == 4, "D disconnect preserves canonical interaction result");
        // Condition links intentionally pin their first client endpoint. Reconnect creates a new UDP socket.
        using var reconnectLink = impaired ? new UdpConditionLink(0, port, 60, 180, 10, 20) : null;
        check(client.Join("127.0.0.1", reconnectLink == null ? joinPort : reconnectLink.Port, "Again"), "D reconnect starts with a fresh transport endpoint");
        Pump(() => client.World.State == WorldSyncState.WorldReady);
        check(((Adapter)client.World.Registry.GetBinding(entity)).Open && host.World.Registry.DevelopmentSummary() == client.World.Registry.DevelopmentSummary(), "D reconnect fresh baseline retains world and matches hash");
        using (new NetworkApplyScope()) check(!client.SendInteraction(entity, InteractionKind.Close), "D application cannot recursively send interaction request");
        host.InvalidateWorldScene(); check(host.World.Registry.Count == 0 && host.World.Registry.SceneEpoch == 2 && !adapter.IsAlive, "D reload epoch releases real interaction bindings");
        if (impaired) check(link.Dropped > 0 && link.Duplicated > 0 && link.QueueOverflow == 0 && link.Error.Length == 0, "D interaction path passes loss duplication and reordering-capable jitter without overflow");
    }
}
