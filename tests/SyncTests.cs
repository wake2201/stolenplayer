// Offline tests for M2 movement sync: codec, validation, ordering, interpolation math, visibility lifecycle.
// No Unity, no network. Exit code 0 = all passed.
using System;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class SyncTests
{
    static int _passed, _failed;

    static int Main()
    {
        Run("snapshot round-trip, 29-byte wire size", CodecRoundTrip);
        Run("yaw quantization error < 0.01 deg, wraps negatives", YawQuantization);
        Run("truncated snapshot rejected", Truncated);
        Run("SceneInfo round-trip", SceneInfoRoundTrip);
        Run("scene hash deterministic and distinct", SceneHash);
        Run("validation: wrong slot / NaN / out of range / stale", Validation);
        Run("sequence wrap-around treated as newer", SeqWrap);
        Run("buffer drops stale and duplicate snapshots", BufferStale);
        Run("interpolation midpoint position and speed", InterpMidpoint);
        Run("yaw interpolation takes shortest path (350 -> 10)", YawShortest);
        Run("holds newest state when data runs out (no unbounded extrapolation)", HoldNewest);
        Run("bounded extrapolation never exceeds MaxExtrapolation", BoundedExtrapolation);
        Run("teleport (> TeleportDistance) snaps instead of sliding", TeleportSnap);
        Run("long gap snaps instead of sliding", GapSnap);
        Run("sender restart (clock backwards) resets buffer", SenderRestart);
        Run("render position independent of frame rate", FrameRateIndependent);
        Run("jittered arrival still interpolates monotonically", Jitter);
        Run("visibility decision matrix", Visibility);
        Console.WriteLine();
        Console.WriteLine("RESULT: " + _passed + " passed, " + _failed + " failed");
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }

    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }
    static void Near(float a, float b, float eps, string m) { if (Math.Abs(a - b) > eps) throw new Exception(m + " (" + a + " vs " + b + ")"); }

    static PlayerSnapshot Snap(uint seq, uint ms, float x, float yaw = 0f, MoveFlags f = MoveFlags.Present | MoveFlags.Grounded)
    {
        return new PlayerSnapshot { Slot = 2, Seq = seq, TimeMs = ms, SceneHash = 123, Pos = new Vec3(x, 1f, -x), Yaw = yaw, Flags = f };
    }

    static void CodecRoundTrip()
    {
        var s = new PlayerSnapshot { Slot = 1, Seq = 4000000000, TimeMs = 123456, SceneHash = -77, Pos = new Vec3(-1234.5f, 56.25f, 0.125f), Yaw = 271.5f, Flags = MoveFlags.Present | MoveFlags.Crouching | MoveFlags.Moving };
        var w = new NetDataWriter();
        PlayerStateCodec.Write(w, s);
        Expect(w.Length == PlayerStateCodec.WireSize, "size " + w.Length);
        var r = new NetDataReader(w.Data, 0, w.Length);
        Expect(r.GetByte() == (byte)MsgType.PlayerState, "type");
        PlayerSnapshot d;
        Expect(PlayerStateCodec.TryRead(r, out d), "read");
        Expect(d.Slot == 1 && d.Seq == s.Seq && d.TimeMs == s.TimeMs && d.SceneHash == -77 && d.Flags == s.Flags, "fields");
        Expect(d.Pos.X == s.Pos.X && d.Pos.Y == s.Pos.Y && d.Pos.Z == s.Pos.Z, "pos");
        Near(d.Yaw, 271.5f, 0.01f, "yaw");
    }

    static void YawQuantization()
    {
        for (float y = -720; y <= 720; y += 7.3f)
        {
            float back = PlayerStateCodec.DequantizeYaw(PlayerStateCodec.QuantizeYaw(y));
            float expected = ((y % 360f) + 360f) % 360f;
            float diff = Math.Abs(back - expected); diff = Math.Min(diff, 360f - diff);
            Expect(diff < 0.01f, "yaw " + y + " -> " + back);
        }
    }

    static void Truncated()
    {
        var w = new NetDataWriter();
        PlayerStateCodec.Write(w, Snap(1, 1, 0));
        var r = new NetDataReader(w.Data, 0, w.Length - 3);
        r.GetByte();
        PlayerSnapshot d;
        Expect(!PlayerStateCodec.TryRead(r, out d), "accepted truncated");
    }

    static void SceneInfoRoundTrip()
    {
        var w = new NetDataWriter();
        new SceneInfo { Slot = 2, SceneHash = SceneKeys.Hash("MainScene+MadisonSt"), SceneKey = "MainScene+MadisonSt", Present = true }.Write(w);
        var r = new NetDataReader(w.Data, 0, w.Length);
        Expect(r.GetByte() == (byte)MsgType.SceneInfo, "type");
        SceneInfo d;
        Expect(SceneInfo.TryRead(r, out d) && d.Slot == 2 && d.SceneKey == "MainScene+MadisonSt" && d.Present && d.SceneHash == SceneKeys.Hash(d.SceneKey), "fields");
    }

    static void SceneHash()
    {
        Expect(SceneKeys.Hash("MainScene") == SceneKeys.Hash("MainScene"), "deterministic");
        Expect(SceneKeys.Hash("MainScene") != SceneKeys.Hash("MainScene+Hillville"), "distinct");
        Expect(SceneKeys.Hash("") == unchecked((int)2166136261), "FNV offset basis");
    }

    static void Validation()
    {
        Expect(SnapshotValidation.Check(Snap(5, 0, 0), 2, true, 4) == SnapshotVerdict.Accepted, "accept");
        var s = Snap(5, 0, 0); s.Slot = 1;
        Expect(SnapshotValidation.Check(s, 2, false, 0) == SnapshotVerdict.WrongSlot, "slot");
        s = Snap(5, 0, 0); s.Pos.Y = float.NaN;
        Expect(SnapshotValidation.Check(s, 2, false, 0) == SnapshotVerdict.Invalid, "nan");
        s = Snap(5, 0, 0); s.Pos.X = 1e6f;
        Expect(SnapshotValidation.Check(s, 2, false, 0) == SnapshotVerdict.Invalid, "range");
        Expect(SnapshotValidation.Check(Snap(4, 0, 0), 2, true, 4) == SnapshotVerdict.Stale, "duplicate");
        Expect(SnapshotValidation.Check(Snap(3, 0, 0), 2, true, 4) == SnapshotVerdict.Stale, "older");
    }

    static void SeqWrap()
    {
        Expect(SnapshotValidation.IsNewer(2, uint.MaxValue - 1), "wrapped newer");
        Expect(!SnapshotValidation.IsNewer(uint.MaxValue - 1, 2), "wrapped older");
    }

    static void BufferStale()
    {
        var b = new SnapshotBuffer();
        Expect(b.Add(Snap(10, 1000, 0), 1.0), "first");
        Expect(!b.Add(Snap(10, 1000, 0), 1.0), "dup accepted");
        Expect(!b.Add(Snap(9, 950, 0), 1.0), "older accepted");
        Expect(b.Add(Snap(11, 1050, 1), 1.05), "newer");
        Expect(b.Count == 2 && b.StaleDropped == 2, "count " + b.Count + " stale " + b.StaleDropped);
    }

    // Snapshots every 50 ms moving +1 m per snapshot (20 m/s), delivered with constant 30 ms latency.
    static SnapshotBuffer Feed(int count, double latency = 0.03, float step = 1f)
    {
        var b = new SnapshotBuffer { InterpolationDelay = 0.1 };
        for (int i = 0; i < count; i++) b.Add(Snap((uint)(i + 1), (uint)(i * 50), i * step, i * 10f), i * 0.05 + latency);
        return b;
    }

    static void InterpMidpoint()
    {
        var b = Feed(10);
        // local time 0.405 -> render time = 0.405 - 0.03 - 0.1 = 0.275 s -> between snapshot 5 (0.25) and 6 (0.30), t = 0.5
        RemoteSample s;
        Expect(b.TrySample(0.405, out s), "sample");
        Near(s.Pos.X, 5.5f, 0.001f, "x");
        Near(s.Pos.Z, -5.5f, 0.001f, "z");
        Near(s.Yaw, 55f, 0.01f, "yaw");
        Near(s.HorizontalSpeed, (float)(Math.Sqrt(2) * 20), 0.01f, "speed");
        Expect(!s.Snapped, "snapped");
    }

    static void YawShortest()
    {
        Near(SnapshotBuffer.LerpAngle(350f, 10f, 0.5f), 0f, 0.001f, "350->10");
        Near(SnapshotBuffer.LerpAngle(10f, 350f, 0.25f), 5f, 0.001f, "10->350");
        Near(SnapshotBuffer.LerpAngle(90f, 270f, 0.5f) % 180f, 0f, 0.001f, "opposite");
    }

    static void HoldNewest()
    {
        var b = Feed(5); // newest at sender t=0.20, x=4
        RemoteSample s;
        Expect(b.TrySample(5.0, out s), "sample");
        Near(s.Pos.X, 4f, 0.0001f, "held x");
        Near(s.HorizontalSpeed, 0f, 0.0001f, "speed must drop to 0 when data is late");
    }

    static void BoundedExtrapolation()
    {
        var b = Feed(5);
        b.MaxExtrapolation = 0.05; // at most one more snapshot interval -> x <= 5
        RemoteSample s;
        Expect(b.TrySample(10.0, out s), "sample");
        Expect(s.Pos.X <= 5.0001f && s.Pos.X >= 4f, "x " + s.Pos.X);
    }

    static void TeleportSnap()
    {
        var b = new SnapshotBuffer { InterpolationDelay = 0.1, TeleportDistance = 8f };
        b.Add(Snap(1, 0, 0), 0.0);
        b.Add(Snap(2, 50, 100), 0.05); // 100 m jump
        b.Add(Snap(3, 100, 100.5f), 0.10);
        RemoteSample s;
        Expect(b.TrySample(0.125, out s), "sample"); // render t = 0.025, inside the jump interval
        Expect(s.Snapped && s.Pos.X == 100f, "expected snap to 100, got " + s.Pos.X + " snapped=" + s.Snapped);
    }

    static void GapSnap()
    {
        var b = new SnapshotBuffer { InterpolationDelay = 0.1, MaxGap = 1.0 };
        b.Add(Snap(1, 0, 0), 0.0);
        b.Add(Snap(2, 3000, 2), 3.0); // 3 s gap
        b.Add(Snap(3, 3050, 2.1f), 3.05);
        RemoteSample s;
        Expect(b.TrySample(1.6, out s), "sample"); // render t = 1.5, inside the gap
        Expect(s.Snapped && s.Pos.X == 2f, "expected snap, got " + s.Pos.X);
    }

    static void SenderRestart()
    {
        var b = Feed(5);
        Expect(b.Add(Snap(6, 10, 50f), 1.0), "restart snapshot (newer seq, older clock) accepted");
        Expect(b.Count == 1, "buffer reset, count=" + b.Count);
        RemoteSample s;
        Expect(b.TrySample(1.0, out s) && s.Pos.X == 50f, "single sample");
    }

    static void FrameRateIndependent()
    {
        // Same wall-clock instant sampled from a 30 fps and a 144 fps loop must give the same position.
        var b30 = Feed(20); var b144 = Feed(20);
        RemoteSample a = new RemoteSample(), c = new RemoteSample();
        for (double t = 0; t < 0.6; t += 1.0 / 30) b30.TrySample(t, out a);   // sampling must not mutate state
        for (double t = 0; t < 0.6; t += 1.0 / 144) b144.TrySample(t, out c);
        b30.TrySample(0.6, out a);
        b144.TrySample(0.6, out c);
        Near(a.Pos.X, c.Pos.X, 1e-5f, "x");
    }

    static void Jitter()
    {
        var rnd = new Random(42);
        var b = new SnapshotBuffer { InterpolationDelay = 0.12 };
        double arrival = 0;
        for (int i = 0; i < 60; i++)
        {
            arrival = Math.Max(arrival, i * 0.05 + 0.02 + rnd.NextDouble() * 0.06); // 20-80 ms latency, in order
            b.Add(Snap((uint)(i + 1), (uint)(i * 50), i * 0.2f), arrival);
        }
        float last = -1; RemoteSample s;
        for (double t = 0.3; t < 3.0; t += 1.0 / 60)
        {
            Expect(b.TrySample(t, out s), "sample");
            Expect(s.Pos.X >= last - 1e-4f, "moved backwards at t=" + t);
            last = s.Pos.X;
        }
    }

    static void Visibility()
    {
        var P = MoveFlags.Present;
        Expect(RemoteVisibility.Decide(false, true, true, 1, 1, P, true) == RemoteView.NotConnected, "nc");
        Expect(RemoteVisibility.Decide(true, false, true, 1, 1, P, true) == RemoteView.NoLocalPlayer, "nlp");
        Expect(RemoteVisibility.Decide(true, true, false, 1, 1, P, true) == RemoteView.RemoteNotPresent, "rnp");
        Expect(RemoteVisibility.Decide(true, true, true, 1, 1, MoveFlags.None, true) == RemoteView.RemoteNotPresent, "flag not present");
        Expect(RemoteVisibility.Decide(true, true, true, 1, 2, P, true) == RemoteView.OtherScene, "scene");
        Expect(RemoteVisibility.Decide(true, true, true, 1, 1, P | MoveFlags.InVehicle, true) == RemoteView.InVehicle, "veh");
        Expect(RemoteVisibility.Decide(true, true, true, 1, 1, P | MoveFlags.Hidden, true) == RemoteView.Hiding, "hide");
        Expect(RemoteVisibility.Decide(true, true, true, 1, 1, P, false) == RemoteView.WaitingForData, "wait");
        Expect(RemoteVisibility.Decide(true, true, true, 1, 1, P | MoveFlags.Running, true) == RemoteView.Visible, "visible");
    }
}
