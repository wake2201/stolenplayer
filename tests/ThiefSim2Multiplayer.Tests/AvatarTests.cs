using System;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Diagnostics;

internal static class AvatarTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (byte id in new byte[] { 0, 1 })
        {
            var request = new Message { Type = MessageType.HandshakeRequest, ProtocolVersion = Protocol.Version, ModVersion = Protocol.ModVersion, PlayerName = "AvatarTest", AvatarId = id };
            check(Protocol.TryDecode(Protocol.Encode(request), out var decoded) && decoded.AvatarId == id, "AvatarId " + id + " handshake roundtrip");
        }
        check(Protocol.Version == 13 && NetworkManager.ValidateHandshake(new Message { ProtocolVersion = 1 }, Protocol.Version, "", false).Contains("protocol") && NetworkManager.ValidateHandshake(new Message { ProtocolVersion = 12 }, Protocol.Version, "", false).Contains("protocol"), "v1 and previous vehicle protocol peers explicitly rejected by current protocol");
        check(NetworkManager.ValidateHandshake(new Message { ProtocolVersion = 1 }, 1, "", false).Contains("protocol"), "runtime cannot silently negotiate obsolete v1 semantics");
        var old = new Message { Type = MessageType.HandshakeRequest, ProtocolVersion = 1, ModVersion = "0.2.1", PlayerName = "Old" };
        check(Protocol.TryDecode(Protocol.Encode(old), out var legacy) && legacy.ProtocolVersion == 1, "v1 handshake layout retained for explicit rejection");
        check(!AvatarCatalog.IsSupported(2) && AvatarCatalog.IsSupported(0) && AvatarCatalog.IsSupported(1), "avatar fallback selection rejects undefined identities");
        var pose = new PlayerTransform { PlayerId = 2, SceneEpoch = 1, State = new PlayerReplicationState { Known = SampledFields.Velocity | SampledFields.Equipment | SampledFields.LookPitch, VelocityX = 3, VelocityY = -2, VelocityZ = 4, EquippedItemId = 0, LookPitch = -40, Locomotion = LocomotionState.Run } };
        var bytes = new byte[Protocol.StateBytes + 10];
        check(Protocol.WriteState(pose, bytes, 5) && Protocol.TryReadState(bytes, 5, Protocol.StateBytes, out var read) && read.State.PlanarSpeed == 5 && read.State.VerticalVelocity == -2 && read.State.EquippedItemId == 0 && read.State.LookPitch == -40, "semantic state pooled encode/decode preserves velocity, pitch and equipment");
        check(Protocol.TryDecode(Protocol.Encode(new Message { Type = MessageType.PlayerState, Transform = pose }), out var message) && message.Transform.State.Locomotion == LocomotionState.Run, "control decoder agrees with semantic fast codec");
        for (int i = 0; i < 1000; i++) { Protocol.WriteState(pose, bytes, 5); Protocol.TryReadState(bytes, 5, Protocol.StateBytes, out _); }
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) { Protocol.WriteState(pose, bytes, 5); Protocol.TryReadState(bytes, 5, Protocol.StateBytes, out _); }
        check(GC.GetAllocatedBytesForCurrentThread() == allocationStart, "10,000 reusable semantic state codec pairs allocate zero managed bytes");
        var timeline = new TransformBuffer(); var later = pose; later.Sequence = 2; later.State.Locomotion = LocomotionState.Idle; later.State.EquippedItemId = -1;
        timeline.Add(pose, 1); timeline.Add(later, 1.1);
        check(timeline.TrySample(1.2, 0, out var endpoint) && endpoint.State.Locomotion == LocomotionState.Idle && endpoint.State.EquippedItemId == -1, "delayed semantic endpoint advances equipment and gait together");
        var invalid = pose.State; invalid.Locomotion = (LocomotionState)255; check(!Protocol.ValidState(invalid), "invalid locomotion enum rejected");
        invalid = pose.State; invalid.LookPitch = 81; check(!Protocol.ValidState(invalid), "unbounded look pitch rejected");
        invalid = pose.State; invalid.Known = SampledFields.None; check(!Protocol.ValidState(invalid), "unknown fields cannot carry invented velocity/pitch");
        invalid = pose.State; invalid.EquippedItemId = 452; check(!Protocol.ValidState(invalid), "equipment definition bounds enforced");
        check(!Protocol.TryReadState(bytes, 5, 51, out _) && !Protocol.TryReadState(bytes, int.MaxValue, 52, out _), "semantic packet truncation and invalid slice rejected");
        var selector = new LocomotionSelector(0.1f, 0.2f, 6, 5);
        var s = new PlayerReplicationState { Known = SampledFields.Velocity | SampledFields.Grounded, Grounded = true };
        check(selector.Select(s) == LocomotionState.Idle, "semantic idle selection");
        s.VelocityZ = 3; check(selector.Select(s) == LocomotionState.Walk, "Idle to Walk");
        s.VelocityZ = 7; check(selector.Select(s) == LocomotionState.Run, "Walk to Run");
        s.VelocityZ = 5.5f; check(selector.Select(s) == LocomotionState.Run, "run hysteresis holds between thresholds");
        s.VelocityZ = 4; check(selector.Select(s) == LocomotionState.Walk, "Run to Walk");
        s.VelocityZ = 0.15f; check(selector.Select(s) == LocomotionState.Walk, "idle hysteresis holds moving state");
        s.VelocityZ = 0.05f; check(selector.Select(s) == LocomotionState.Idle, "Walk to Idle");
        for (int i = 0; i < 100; i++) { s.VelocityZ = 3; selector.Select(s); s.VelocityZ = 7; selector.Select(s); s.VelocityZ = 0; if (selector.Select(s) != LocomotionState.Idle) throw new Exception("cycle drift"); }
        check(true, "100 locomotion cycles return Idle");
        var motor = new MotorLocomotionSelector();
        check(motor.Select(s, true, 10) == LocomotionState.Walk && motor.Select(s, false, 10.05) == LocomotionState.Walk && motor.Select(s, false, 10.2) == LocomotionState.Idle, "verified motor movement uses stop hysteresis without velocity guesses");
        s.Known |= SampledFields.Sprinting; s.Sprinting = true;
        check(motor.Select(s, true, 11) == LocomotionState.Run, "verified sprint semantic is independent of exhausted movement speed");
        motor.Reset(); check(motor.Select(s, false, 12) == LocomotionState.Idle, "reconnect resets production locomotion hysteresis");
        s.Sprinting = false;
        s.Known |= SampledFields.Crouched; s.Crouched = true; check(selector.Select(s) == LocomotionState.CrouchIdle, "crouch idle semantic supported");
        s.VelocityZ = 3; check(selector.Select(s) == LocomotionState.CrouchMove, "crouch movement semantic supported");
        s.Grounded = false; check(selector.Select(s) == LocomotionState.Airborne, "airborne overrides grounded gait");
        s.VelocityX = 3; s.VelocityZ = 0; check(Math.Abs(s.ForwardVelocity(90) - 3) < 0.001 && Math.Abs(s.RightVelocity(0) - 3) < 0.001, "velocity retains forward and strafe direction relative to BodyYaw");
        var equipment = new EquipmentPresentationState(); s = new PlayerReplicationState { Known = SampledFields.Equipment, EquippedItemId = 0 };
        check(equipment.Change(s) && equipment.Item == 0 && !equipment.Change(s), "None to Crowbar and repeated state creates one visual");
        s.EquippedItemId = -1; check(equipment.Change(s) && equipment.Item == -1, "Crowbar to None destroys presentation");
        s.EquippedItemId = 0; equipment.Change(s); equipment.Reset(); check(equipment.Item == -1 && equipment.Change(s), "disconnect/reconnect resets equipped presentation");
        s.Known = SampledFields.None; check(equipment.Change(s) && equipment.Item == -1, "unknown equipment removes stale visual");
        var rng = new Random(44);
        for (int i = 0; i < 10000; i++) { var b = new byte[rng.Next(0, 100)]; rng.NextBytes(b); Protocol.TryReadState(b, rng.Next(-10, 110), rng.Next(-10, 110), out _); }
        check(true, "10,000 malformed semantic packets cannot throw");
        check(EquipmentSlotMap.TryGetItem(0, out var slotItem) && slotItem == -1, "hand slot maps to verified none");
        check(EquipmentSlotMap.TryGetItem(99, out slotItem) && slotItem == -1, "phone slot carries no replicated functional tool");
        check(EquipmentSlotMap.TryGetItem(1, out slotItem) && slotItem == 0, "crowbar slot differs from crowbar item definition");
        check(!EquipmentSlotMap.TryGetItem(2, out slotItem) && slotItem == -1, "unproven tool slot remains unknown");
        check(!EquipmentSlotMap.TryGetItem(-1, out slotItem), "negative slot cannot become an item definition");
        check(!EquipmentSlotMap.TryGetItem(451, out slotItem), "valid ItemID is not assumed to be a tool slot");
        check(MilestoneBInput.TryParse("6 1 -0.5 10 -10 true jump 1234", 1234, out var input) && input.Seconds == 6 && input.Forward == 1 && input.Right == -0.5f && input.LookX == 10 && input.LookY == -10 && input.Run && input.Button == "jump", "development fixture parses bounded input without Unity");
        check(!MilestoneBInput.TryParse("6 1 0 0 0 false none 1234", 1235, out input), "development fixture rejects a different process");
        check(!MilestoneBInput.TryParse("0 1 0 0 0 false none 1234", 1234, out input) && !MilestoneBInput.TryParse("61 1 0 0 0 false none 1234", 1234, out input), "development fixture rejects unbounded lifetime");
        check(!MilestoneBInput.TryParse("6 -1.01 0 0 0 false none 1234", 1234, out input) && !MilestoneBInput.TryParse("6 0 2 0 0 false none 1234", 1234, out input), "development fixture rejects movement outside normalized bounds");
        check(!MilestoneBInput.TryParse("6 0 0 101 0 false none 1234", 1234, out input) && !MilestoneBInput.TryParse("6 0 0 0 -101 false none 1234", 1234, out input), "development fixture bounds both look axes");
        check(!MilestoneBInput.TryParse("NaN 0 0 0 0 false none 1234", 1234, out input) && !MilestoneBInput.TryParse("6 0 Infinity 0 0 false none 1234", 1234, out input), "development fixture rejects nonfinite input");
        check(!MilestoneBInput.TryParse("6 0 0 0 0 maybe none 1234", 1234, out input), "development fixture requires an explicit boolean");
        check(!MilestoneBInput.TryParse("6 0 0 0 0 false arbitrary_action 1234", 1234, out input), "development fixture rejects unapproved buttons");
        check(!MilestoneBInput.TryParse(null, 1234, out input) && !MilestoneBInput.TryParse("6 0 0 0 0 false none", 1234, out input), "development fixture rejects missing request fields");
        check(!MilestoneBInput.TryParse(new string(' ', 257), 1234, out input) && !MilestoneBInput.TryParse("6 0 0 0 0 false none 0", 0, out input), "development fixture enforces text bound and live process identity");
    }
}
