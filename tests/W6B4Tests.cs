// W6B.4 offline tests: shared original story phone calls (stable identity, one committed answer, finish, no replay).
// Real Shared code: StoryCallState / StoryCallRules + WorldHost.AnswerStoryCall / HandleProposal. The two PCs' phones are
// modelled after the decompiled 1.27c IL (StorySequence.Update ring / answer / decline branches, StoryManager.InitiateStory
// call_sequence nulling). Unity glue is source-scanned. In-game: TESTING.md section 58.
using System;
using System.Collections.Generic;
using System.IO;
using TS2MP.Shared;

static class W6B4Tests
{
    static int _passed, _failed;
    const int S15 = 19, S16 = 20;

    static int Main()
    {
        Run("01 identity: stage key + scene path hash, codec v2 (phase), old v1 records refused, StorySequences index gone", Identity);
        Run("02 P1 answers: committed once, P2 ringing -> original answer branch, P2 busy -> bypass branch at its ring, both dialogues once", P1Answers);
        Run("03 P2 answers: proposal committed, P1 behaves identically (ringing / pending)", P2Answers);
        Run("04 both answer at the same time: one record, the second Unchanged, no mirror on either PC, one conversation each", Simultaneous);
        Run("05 the call completes once: first finish report commits, later / repeated / stale reports Unchanged or Stale", CompletesOnce);
        Run("06 late join / reconnect / reload: a finished call is never replayed (ring skipped or put down); busy-during-call PC still hears it", NoReplay);
        Run("07 two stages reusing one original call object stay distinct (story15 vs story16, same sequence path)", ReusedObject);
        Run("08 non-story phone: payphone / bypass chain / free mode untouched, decline stays personal", NonStory);
        Run("09 Unity glue: PhoneCall1 / Beginn hooks, identity from the ring record, finish on having_phone_convo, decline branch order", CallGlue);
        Run("10 minimap root cause reproduced: renderer-only hiding leaves the box marker at the house / car (ghost + duplicate next to the cargo copy)", BlipRootCause);
        Run("11 mission box lifecycle on both PCs: house -> P1 / P2 carry -> Ranger cargo -> driving -> hideout -> table -> unpack -> contents -> collected: one marker at the right place", BlipLifecycle);
        Run("12 repeated / reordered presentation events, reconnect re-reconcile, scene reload: no duplicate, no stale marker", BlipIdempotent);
        Run("13 Unity glue: original bl_MiniMapItem / Diamond_Marker toggled on hide / show, Target retargeted to the avatar, event-only", BlipGlue);
        Run("14 regressions + versions: protocol 41 / mod 0.31.0, heavy presentation unchanged, other wire records unchanged", Versions);
        Console.WriteLine(_failed == 0 ? "W6B4 TESTS PASSED (" + _passed + ")" : "W6B4 TESTS FAILED: " + _failed + " of " + (_passed + _failed));
        return _failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action t)
    {
        try { t(); _passed++; Console.WriteLine("PASS  " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL  " + name + " :: " + ex.Message); }
    }
    static void Expect(bool c, string m) { if (!c) throw new Exception(m); }

    // ================================================================ model
    sealed class StoryHost : IStoryHost
    {
        public int Index = S15; public string Key = "story15"; public bool Free;
        public bool TryCurrent(out int i, out string k, out bool f) { i = Index; k = Key; f = Free; return true; }
        public bool Advance(int e) { return false; }
    }
    static uint _pid = 1;
    static ProposalCode Propose(WorldHost wh, byte slot, StateEntry e, uint pid = 0)
    { uint rev; int sr; float sh; return wh.HandleProposal("c" + slot, slot, pid != 0 ? pid : _pid++, e, 1, out rev, out sr, out sh); }
    static StoryCallState Committed(WorldHost wh) { var e = wh.Reg.Get(StateScope.Session, 0, WorldKeys.StoryCall); StoryCallState s; return e != null && StoryCallState.TryParse(e.Value, out s) ? s : null; }

    const string Path15 = "MANAGERS/STORY MANAGER/STORY CALLS/Call_story15";
    static readonly uint H15 = StoryCallRules.PathHash(Path15);

    // One PC's original phone (StorySequence.Update / PhoneCall1 / Beginn, PlayerScript.having_phone_convo) + the mod state.
    sealed class Phone
    {
        public byte Slot; public string Key = "story15"; public uint Seq = H15;
        public bool Pending, Ringing, Convo; public int Dialogues, Rings, Declines;
        public readonly HashSet<string> Played = new HashSet<string>(), SawLive = new HashSet<string>();
        public StoryCallState Seen;
        string Id { get { return Key + "|" + Seq.ToString("X8"); } }
        public void See(StoryCallState c) { Seen = c; if (c != null && c.Phase == StoryCallState.Answered) SawLive.Add(c.Id); }
        // PhoneCall1 prefix (CallRingPre)
        public void TryRing()
        {
            if (!Pending) return; Pending = false;
            var m = StoryCallRules.Decide(Seen, Slot, Key, Seq, false, true, false, Played.Contains(Id), SawLive.Contains(Id));
            if (m == CallMirror.AnswerWhenRung) { Dialogue(); return; }
            if (m == CallMirror.Silence) return;
            Ringing = true; Rings++;
        }
        // CallTick for a ringing sequence
        public void Tick()
        {
            if (!Ringing) return;
            var m = StoryCallRules.Decide(Seen, Slot, Key, Seq, true, false, Convo, Played.Contains(Id), SawLive.Contains(Id));
            if (m == CallMirror.AnswerRinging) { Ringing = false; Dialogue(); }
            else if (m == CallMirror.Silence) { Ringing = false; Declines++; }
        }
        public StoryCallState Answer() { Ringing = false; Dialogue(); return new StoryCallState { Key = Key, Stage = S15, Seq = Seq, By = Slot }; }
        void Dialogue() { Convo = true; Dialogues++; Played.Add(Id); }
        public StoryCallState End() { Convo = false; return new StoryCallState { Key = Key, Stage = S15, Seq = Seq, By = Slot, Phase = StoryCallState.Finished }; }
    }

    sealed class Pair
    {
        public readonly WorldHost Wh = new WorldHost(77); public readonly StoryHost Sh = new StoryHost();
        public readonly Phone P1 = new Phone { Slot = 1 }, P2 = new Phone { Slot = 2 };
        public Pair() { Wh.StoryHost = Sh; }
        public ProposalCode Send(byte slot, StoryCallState s, uint pid = 0)
        {
            ProposalCode c; string why;
            if (slot == 1) c = Wh.AnswerStoryCall(1, s, out why);
            else c = Propose(Wh, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StoryCallAnswer, Value = s.ToBytes() }, pid);
            Sync(); return c;
        }
        public void Sync() { var c = Committed(Wh); P1.See(c); P2.See(c); P1.Tick(); P2.Tick(); }
    }

    // ================================================================ tests
    static void Identity()
    {
        Expect(H15 == StoryCallRules.PathHash(Path15) && H15 != StoryCallRules.PathHash(Path15 + "x") && StoryCallRules.PathHash("") == 2166136261u, "FNV-1a, deterministic, path sensitive");
        var s = new StoryCallState { Key = "story15", Stage = S15, Seq = 0xDEADBEEF, By = 2, Phase = StoryCallState.Finished };
        StoryCallState b; Expect(StoryCallState.TryParse(s.ToBytes(), out b) && b.SameCall(s) && b.Seq == 0xDEADBEEF && b.Stage == S15 && b.Phase == StoryCallState.Finished && b.By == 2, "round trip");
        Expect(s.ToBytes().Length == 9 + 7 && StoryCallState.Version == 2, "9 B + key");
        var v1 = new byte[] { 1, 12, 2, 7, (byte)'s', (byte)'t', (byte)'o', (byte)'r', (byte)'y', (byte)'1', (byte)'5' };
        Expect(!StoryCallState.TryParse(v1, out b), "0.29.0 (v1) record refused");
        var bad = s.ToBytes(); bad[3] = 3; Expect(!StoryCallState.TryParse(bad, out b), "bad phase refused");
        bad = s.ToBytes(); bad[8] = 99; Expect(!StoryCallState.TryParse(bad, out b), "bad key length refused");
        Expect(!new StoryCallState { Key = "story15", Seq = 1 }.SameCall(new StoryCallState { Key = "story16", Seq = 1 }) && !new StoryCallState { Key = "story15", Seq = 1 }.SameCall(new StoryCallState { Key = "story15", Seq = 2 }), "key and sequence both part of the identity");
        var ss = Src("Plugin", "StorySync.cs");
        Expect(!ss.Contains("StorySequences") || ss.IndexOf("\"StorySequences\"", StringComparison.Ordinal) < 0, "GameManager.StorySequences no longer read (it never held the story calls)");
        Expect(!ss.Contains("not in GameManager.StorySequences"), "the 0.29.0 'not shared' path is gone");
    }

    static void P1Answers()
    {
        var p = new Pair(); p.P1.Pending = p.P2.Pending = true; p.P1.TryRing(); p.P2.TryRing();
        Expect(p.P1.Ringing && p.P2.Ringing && p.P1.Rings == 1 && p.P2.Rings == 1, "the call rings on both PCs (original PhoneCall1)");
        Expect(p.Send(1, p.P1.Answer()) == ProposalCode.Accepted && Committed(p.Wh).By == 1 && p.Wh.CallAnswers == 1, "host answer committed once");
        Expect(!p.P2.Ringing && p.P2.Dialogues == 1 && p.P1.Dialogues == 1, "P2 stops ringing with the original answer branch, same dialogue on both");
        p.Sync(); p.Sync(); Expect(p.P2.Dialogues == 1 && p.P1.Dialogues == 1, "repeated state events: nothing runs twice");
        // P2 busy (in a car): its ring is still pending when the answer arrives
        var q = new Pair(); q.P1.Pending = q.P2.Pending = true; q.P1.TryRing();
        Expect(q.Send(1, q.P1.Answer()) == ProposalCode.Accepted && q.P2.Dialogues == 0, "P2 pending: nothing yet");
        q.P2.TryRing(); Expect(!q.P2.Ringing && q.P2.Dialogues == 1 && q.P2.Rings == 0, "P2 free again -> original bypass branch: dialogue, no ring");
    }

    static void P2Answers()
    {
        var p = new Pair(); p.P1.Pending = p.P2.Pending = true; p.P1.TryRing(); p.P2.TryRing();
        uint pid = _pid++;
        Expect(p.Send(2, p.P2.Answer(), pid) == ProposalCode.Accepted && Committed(p.Wh).By == 2, "P2 answer committed by the host");
        Expect(!p.P1.Ringing && p.P1.Dialogues == 1 && p.P2.Dialogues == 1, "P1 stops ringing (original answer branch), one dialogue each");
        Expect(p.Send(2, new StoryCallState { Key = "story15", Stage = S15, Seq = H15, By = 2 }, pid) == ProposalCode.Accepted && p.Wh.CallAnswers == 1, "retransmission cached, counted once");
        var q = new Pair(); q.P1.Pending = q.P2.Pending = true; q.P2.TryRing();
        q.Send(2, q.P2.Answer()); q.P1.TryRing();
        Expect(q.P1.Dialogues == 1 && q.P1.Rings == 0, "P1 pending -> bypass branch at its ring");
    }

    static void Simultaneous()
    {
        var p = new Pair(); p.P1.Pending = p.P2.Pending = true; p.P1.TryRing(); p.P2.TryRing();
        var a1 = p.P1.Answer(); var a2 = p.P2.Answer();   // both pressed accept before any state arrived
        Expect(p.Send(1, a1) == ProposalCode.Accepted, "first answer committed");
        Expect(p.Send(2, a2) == ProposalCode.Unchanged, "second answer: Unchanged (already answered by P1)");
        Expect(p.P1.Dialogues == 1 && p.P2.Dialogues == 1 && p.Wh.CallAnswers == 1 && Committed(p.Wh).By == 1, "one record, one conversation per PC, no mirror on top");
    }

    static void CompletesOnce()
    {
        var p = new Pair(); p.P1.Pending = p.P2.Pending = true; p.P1.TryRing(); p.P2.TryRing();
        p.Send(2, p.P2.Answer());
        Expect(p.Send(1, p.P1.End()) == ProposalCode.Accepted && Committed(p.Wh).Phase == StoryCallState.Finished && Committed(p.Wh).By == 2, "first finish committed, answerer kept (P2)");
        Expect(p.Send(2, p.P2.End()) == ProposalCode.Unchanged && p.Wh.CallFinishes == 1 && p.Wh.CallAnswers == 1, "second finish Unchanged; one answer, one finish");
        Expect(p.Send(2, new StoryCallState { Key = "story15", Stage = S15, Seq = H15, By = 2 }) == ProposalCode.Unchanged, "late answer after the finish: Unchanged");
        p.Sh.Key = "story16"; p.Sh.Index = S16;
        var w = new Pair(); w.Send(1, new StoryCallState { Key = "story15", Stage = S15, Seq = H15, By = 1 }); w.Sh.Key = "story16";
        Expect(w.Send(2, new StoryCallState { Key = "story15", Stage = S15, Seq = H15, By = 2, Phase = StoryCallState.Finished }) == ProposalCode.Stale, "finish after the stage moved on: Stale (record untouched)");
        Expect(Propose(w.Wh, 2, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StoryCallAnswer, Value = new StoryCallState { Key = "story16", Seq = 5, By = 1, Phase = StoryCallState.Finished }.ToBytes() }) == ProposalCode.Invalid, "P2 cannot report for P1");
    }

    static void NoReplay()
    {
        // P2 reconnects after the call: the host snapshot already says finished; P2's sandbox rings again (no <key>_Call yet)
        var p = new Pair(); p.P1.Pending = true; p.P1.TryRing(); p.Send(1, p.P1.Answer()); p.Send(1, p.P1.End());
        var late = new Phone { Slot = 2 }; late.See(Committed(p.Wh)); late.Pending = true; late.TryRing();
        Expect(late.Rings == 0 && late.Dialogues == 0, "late join / reconnect: ring skipped, no dialogue replay");
        var ringing = new Phone { Slot = 2, Ringing = true }; ringing.See(Committed(p.Wh)); ringing.Tick();
        Expect(!ringing.Ringing && ringing.Declines == 1 && ringing.Dialogues == 0, "already ringing when the finished state arrives: original decline branch, no dialogue");
        // reload of the PC that already played it (call_only_once false / sandbox key lost): never replayed
        p.P1.Pending = true; p.P1.TryRing(); Expect(p.P1.Rings == 1 && p.P1.Dialogues == 1, "P1 reload: ring skipped (played here)");
        // P2 was connected but busy for the whole conversation: it saw the answer live -> still hears the dialogue once
        var q = new Pair(); q.P1.Pending = q.P2.Pending = true; q.P1.TryRing(); q.Send(1, q.P1.Answer()); q.Send(1, q.P1.End());
        q.P2.TryRing(); Expect(q.P2.Dialogues == 1 && q.P2.Rings == 0, "busy during the call: dialogue once (bypass) when free");
        q.P2.End(); q.P2.Pending = true; q.P2.TryRing(); Expect(q.P2.Dialogues == 1 && q.P2.Rings == 0, "then never again");
    }

    static void ReusedObject()
    {
        var p = new Pair(); p.P1.Pending = p.P2.Pending = true; p.P1.TryRing(); p.P2.TryRing();
        p.Send(1, p.P1.Answer()); p.Send(1, p.P1.End()); p.P2.End();
        // story16 rings the SAME StorySequence object (same path hash)
        p.Sh.Key = "story16"; p.Sh.Index = S16; p.P1.Key = p.P2.Key = "story16"; p.P1.Pending = p.P2.Pending = true; p.P1.TryRing(); p.P2.TryRing();
        Expect(p.P1.Ringing && p.P2.Ringing && p.P1.Rings == 2 && p.P2.Rings == 2, "story16 call rings normally on both (story15 record does not match)");
        Expect(p.Send(2, new StoryCallState { Key = "story16", Stage = S16, Seq = H15, By = 2 }) == ProposalCode.Accepted && p.Wh.CallAnswers == 2, "story16 answer is a new call (Accepted, not Unchanged)");
        p.P2.Ringing = false; p.Sync(); Expect(!p.P1.Ringing && p.P1.Dialogues == 2, "P1 mirrors the story16 answer");
    }

    static void NonStory()
    {
        var wh = new WorldHost(5); var sh = new StoryHost { Free = true }; wh.StoryHost = sh; string why;
        Expect(wh.AnswerStoryCall(1, new StoryCallState { Key = "story15", Seq = H15, By = 1 }, out why) == ProposalCode.Unchanged && Committed(wh) == null, "free mode: nothing committed");
        Expect(StoryCallRules.Decide(null, 2, "story15", H15, true, false, false, false, false) == CallMirror.None, "no shared call: vanilla ring / decline");
        var ss = Src("Plugin", "StorySync.cs");
        int b = ss.IndexOf("static void CallBeginnPre(object __instance)", StringComparison.Ordinal); string body = ss.Substring(b, ss.IndexOf("static bool CallRingPre", b, StringComparison.Ordinal) - b);
        Expect(body.Contains("bool answer = (ps != null && _psIncoming(ps)) || (vib != null && vib.isPlaying);") && body.Contains("if (answer) i.OnLocalAnswer(__instance);"), "Beginn without a ringing phone (payphone, bypassCall, nextSequence chain) is not an answer");
        Expect(ss.Contains("outside a story stage (free mode / loading): personal, not shared"), "no story stage: personal");
        Expect(!ss.Contains("cancel_phone"), "decline (cancel_phone -> ForceStop) is never hooked or shared");
    }

    static void CallGlue()
    {
        var ss = Src("Plugin", "StorySync.cs");
        Expect(ss.Contains("AccessTools.Method(seq, \"PhoneCall1\", Type.EmptyTypes)") && ss.Contains("AccessTools.Method(seq, \"Beginn\", Type.EmptyTypes)"), "hooks on the original PhoneCall1 / Beginn");
        Expect(ss.Contains("Seq = StoryCallRules.PathHash(PathOf(((Component)__instance).transform))") && ss.Contains("i._rings[uo] = id;"), "identity recorded at ring time (key at ring, scene path hash)");
        Expect(ss.Contains("if (uo != null && _rings.TryGetValue(uo, out r))"), "answer uses the ring record");
        int m = ss.IndexOf("void Mirror(object seq, bool ringing)", StringComparison.Ordinal); string mirror = ss.Substring(m, ss.IndexOf("void SilenceRinging", m, StringComparison.Ordinal) - m);
        int a = mirror.IndexOf("_setCalling(seq, false);", StringComparison.Ordinal), b = mirror.IndexOf("pick.Play();", StringComparison.Ordinal), c = mirror.IndexOf("_seqBeginn(seq);", StringComparison.Ordinal), d = mirror.IndexOf("vib.Stop();", StringComparison.Ordinal), e = mirror.IndexOf("_psStopCall(ps);", StringComparison.Ordinal);
        Expect(a > 0 && b > a && c > b && d > c && e > d, "mirror = StorySequence.Update answer branch order");
        int s = ss.IndexOf("void SilenceRinging(object seq)", StringComparison.Ordinal); string sil = ss.Substring(s, ss.IndexOf("void CallTick()", s, StringComparison.Ordinal) - s);
        int f1 = sil.IndexOf("_seqForceStop(seq); _setCalling(seq, false);", StringComparison.Ordinal), f2 = sil.IndexOf("_psStopCall(ps);", StringComparison.Ordinal), f3 = sil.IndexOf("_gmCheckStory(gm);", StringComparison.Ordinal);
        Expect(f1 > 0 && f2 > f1 && f3 > f2, "silence = original decline branch order (IL_0306..IL_0324)");
        Expect(ss.Contains("if (ps == null || _psConvo(ps)) return;") && ss.Contains("Phase = StoryCallState.Finished }, \"finished\")"), "finish reported when having_phone_convo drops");
        Expect(ss.Contains("if (reg.HeadRev != _callHead || reg.Epoch != _callEpoch)") && ss.Contains("_nextCallCheck = now + 0.25;"), "event-driven parse, 4 Hz ring check");
        Expect(ss.Contains("if (s.Phase == StoryCallState.Answered) _sawLive.Add(s.Id);"), "live answers remembered (busy PC still gets the dialogue)");
    }

    // ================================================================ minimap model
    // Where an original marker is drawn on one PC. Box = the census scene box (its STORY MARKER child), Copy = the cargo /
    // synthetic drop_object copy (its own original marker), Package = the unpacked contents (own original marker).
    // A marker is visible when its object exists and its mode is Original (at the object) or FollowCarrier (at the carrier).
    const uint BoxHash = 0x104B0090;
    sealed class Pc
    {
        public byte Me; public bool AvatarShown = true;
        public string Where(bool hasRec, HeavyRecord r, string objectPlace, bool legacy)
        {
            var show = HeavyRules.Present(hasRec, r, Me, hasRec && r.Actor == Me && r.State != HeavyState.Carried);
            if (legacy) return show == HeavyRules.Show.Hidden ? "hidden@" + objectPlace : Place(show, r, objectPlace);   // 0.29.0: icon stays where the object was hidden
            var m = MarkerRules.For(show, hasRec, r, Me, AvatarShown);
            if (m == MarkerMode.Off) return null;
            if (m == MarkerMode.FollowCarrier) return "P" + r.Actor;
            return Place(show, r, objectPlace);
        }
        string Place(HeavyRules.Show show, HeavyRecord r, string objectPlace)
        {
            if (show == HeavyRules.Show.Own && r.State == HeavyState.Carried) return "P" + Me;
            if (show == HeavyRules.Show.AtPose || (show == HeavyRules.Show.Own && r.HasPose)) return "pose" + r.Pos.X;
            return objectPlace;
        }
    }
    static HeavyRecord Rec(HeavyState s, byte actor, bool pose = false, float x = 0, bool cargo = false)
    { return new HeavyRecord { Hash = BoxHash, State = s, Actor = actor, HasPose = pose, Pos = new Vec3(x, 0, 0), Cargo = cargo }; }

    // markers of one PC: box (scene object), copy (cargo / synthetic), package
    static List<string> Markers(Pc pc, bool boxRec, HeavyRecord box, bool copyInCar, bool copyRec, HeavyRecord copy, bool package, bool legacy = false)
    {
        var l = new List<string>();
        var b = pc.Where(boxRec, box, "house", legacy); if (b != null) l.Add(b);
        if (copyInCar) l.Add("car");
        else if (copyRec) { var c = pc.Where(true, copy, "car-last", legacy); if (c != null) l.Add(c); }
        if (package) l.Add("table");
        return l;
    }
    static string J(List<string> l) { return l.Count == 0 ? "none" : string.Join(",", l.ToArray()); }

    static void BlipRootCause()
    {
        var p2 = new Pc { Me = 2 };
        var loaded = Rec(HeavyState.Removed, 0);
        var legacy = Markers(p2, true, loaded, true, false, default(HeavyRecord), false, true);
        Expect(legacy.Count == 2 && legacy.Contains("car") && legacy.Contains("hidden@house"), "0.29.0: cargo copy marker + the hidden box's marker left behind (" + J(legacy) + ")");
        var carried = Markers(p2, true, Rec(HeavyState.Carried, 1, true, 5), false, false, default(HeavyRecord), false, true);
        Expect(J(carried) == "hidden@house", "0.29.0: P1 carries the box away, P2's marker stays at the house");
        var fixd = Markers(p2, true, loaded, true, false, default(HeavyRecord), false);
        Expect(J(fixd) == "car", "W6B.4: only the cargo copy's marker (" + J(fixd) + ")");
    }

    static void BlipLifecycle()
    {
        foreach (byte carrier in new byte[] { 1, 2 })
        {
            var a = new Pc { Me = 1 }; var b = new Pc { Me = 2 }; byte other = (byte)(3 - carrier); string tag = " (carrier P" + carrier + ")";
            foreach (var pc in new[] { a, b })
            {
                Func<bool, HeavyRecord, bool, bool, HeavyRecord, bool, string> M = (br, r, inCar, cr, c, pk) => J(Markers(pc, br, r, inCar, cr, c, pk));
                string me = " on P" + pc.Me + tag;
                Expect(M(false, default(HeavyRecord), false, false, default(HeavyRecord), false) == "house", "house pickup target" + me);
                Expect(M(true, Rec(HeavyState.Carried, carrier), false, false, default(HeavyRecord), false) == "P" + carrier, "carrying: marker on the carrier" + me);
                Expect(M(true, Rec(HeavyState.Placed, carrier, true, 7), false, false, default(HeavyRecord), false) == "pose7", "dropped: at the committed pose" + me);
                Expect(M(true, Rec(HeavyState.Carried, other), false, false, default(HeavyRecord), false) == "P" + other, "the other player takes over" + me);
                Expect(M(true, Rec(HeavyState.Removed, 0), true, false, default(HeavyRecord), false) == "car", "in the Ranger cargo: one marker, on the car (moves with it)" + me);
                // hideout: the cargo copy is picked up (synthetic record), carried, placed on the table
                var syn = new HeavyRecord { Hash = 0xC0000001, State = HeavyState.Carried, Actor = carrier, Cargo = true };
                Expect(M(true, Rec(HeavyState.Removed, 0), false, true, syn, false) == "P" + carrier, "unloaded at the hideout: on the carrier" + me);
                var table = new HeavyRecord { Hash = 0xC0000001, State = HeavyState.Placed, Actor = carrier, HasPose = true, Pos = new Vec3(9, 0, 0), Cargo = true };
                Expect(M(true, Rec(HeavyState.Removed, 0), false, true, table, false) == "pose9", "on the table" + me);
                var opened = new HeavyRecord { Hash = 0xC0000001, State = HeavyState.Removed, Cargo = true };
                Expect(M(true, Rec(HeavyState.Removed, 0), false, true, opened, true) == "table", "unpacked: the contents replace the box marker" + me);
                Expect(M(true, Rec(HeavyState.Removed, 0), false, true, opened, false) == "none", "contents collected / delivered: no marker left" + me);
                // copy outside its car without a record (HeavySync.Restore synthetic -> Hide): no marker at the car's last place
                Expect(M(true, Rec(HeavyState.Removed, 0), false, false, default(HeavyRecord), false) == "none", "no ghost at the car" + me);
            }
        }
        var hidden = new Pc { Me = 2, AvatarShown = false };
        Expect(J(Markers(hidden, true, Rec(HeavyState.Carried, 1), false, false, default(HeavyRecord), false)) == "none", "carrier not shown in this scene: off (never a stale marker)");
    }

    static void BlipIdempotent()
    {
        var pc = new Pc { Me = 2 };
        var seq = new[] { Rec(HeavyState.Carried, 1), Rec(HeavyState.Carried, 1), Rec(HeavyState.Removed, 0), Rec(HeavyState.Removed, 0) };
        foreach (var r in seq) { var l = Markers(pc, true, r, r.State == HeavyState.Removed, false, default(HeavyRecord), false); Expect(l.Count == 1, "one marker for " + r); }
        // the two registry entries of one host load step arrive in either order (cargo list first / heavy record first)
        Expect(J(Markers(pc, true, Rec(HeavyState.Removed, 0), false, false, default(HeavyRecord), false)) == "none", "record first, cargo list not yet: none (never two)");
        Expect(J(Markers(pc, true, Rec(HeavyState.Carried, 1), true, false, default(HeavyRecord), false)) == "P1,car", "cargo list first: copy + carrier for one frame (bounded by the same host step)");
        // reconnect / scene reload: the presentation is recomputed from the same registry -> same result
        for (int i = 0; i < 3; i++) Expect(J(Markers(pc, true, Rec(HeavyState.Removed, 0), true, false, default(HeavyRecord), false)) == "car", "re-reconcile " + i);
        Expect(MarkerRules.For(HeavyRules.Show.Hidden, false, default(HeavyRecord), 2, true) == MarkerMode.Off && MarkerRules.For(HeavyRules.Show.ScenePose, false, default(HeavyRecord), 2, true) == MarkerMode.Original, "no record: scene marker as vanilla");
        var mm = Src("Plugin", "MissionMarkers.cs");
        Expect(mm.Contains("if (st.Off) return;") && mm.Contains("if (st == null || !st.Active) return;") && mm.Contains("if (follow == st.Following && st.Following != null) return;"), "hide / follow / show are idempotent (no repeated enable -> no extra original icon)");
        Expect(mm.Contains("if (b != null && b.enabled) { st.Items.Add(b); st.Targets.Add(_getTarget(b)); }"), "only markers enabled by the original are managed and restored");
    }

    static void BlipGlue()
    {
        var mm = Src("Plugin", "MissionMarkers.cs"); var hs = Src("Plugin", "HeavySync.cs"); var rg = Src("Plugin", "RemoteGear.cs");
        Expect(mm.Contains("AccessTools.TypeByName(\"bl_MiniMapItem\")") && mm.Contains("AccessTools.TypeByName(\"Diamond_Marker\")") && mm.Contains("Fast.Setter<Transform>(_itemT, \"Target\")"), "original marker types / Target field");
        Expect(!mm.Contains("Instantiate(") && !mm.Contains("AddComponent") && !mm.Contains("SetActive("), "no new markers, no custom UI, StoryObject's SetActive untouched");
        Expect(hs.Contains("MissionMarkers.Hide(_log, o.Go, o.Markers, follow, why);") && hs.Contains("MissionMarkers.Show(o.Go, o.Markers, \"shown again\");"), "HeavySync.Hide / Unhide manage the markers");
        Expect(hs.Contains("if (show == HeavyRules.Show.Hidden) MissionMarkers.Hide(_log, o.Go, o.Markers, Carrier(show, r), MarkerWhy(r));"), "hidden -> hidden transitions (carried -> loaded) re-evaluated");
        Expect(hs.Contains("MarkerRules.For(show, true, r, Me, shown) == MarkerMode.FollowCarrier ? av : null"), "follow target from the shared rule");
        Expect(rg.Contains("CurrentAvatar = avatar != null ? avatar.transform : null;"), "remote avatar transform published by RemoteGear.Bind");
        Expect(mm.Contains("BLIP '") && mm.Contains("follow ") && mm.Contains("hidden (") && mm.Contains("restored ("), "event logs: hidden / follow / restored");
        Expect(!mm.Contains("void Update") && !mm.Contains("FindObjectsOfType"), "event-only, no scene scans");
    }

    static void Versions()
    {
        Expect(ProtocolInfo.ProtocolVersion == 41 && ProtocolInfo.ModVersion == "0.31.0", "protocol 41 / mod 0.30.0 (story call record v2: both PCs need 0.30.0)");
        Expect(WorldKeys.StoryCall == "storyCall" && WorldKeys.StoryCallAnswer == "storyCallAnswer" && WorldKeys.Heavy == "wheavy", "keys unchanged");
        Expect(HeavyRules.Present(true, Rec(HeavyState.Removed, 0), 1, false) == HeavyRules.Show.Hidden && HeavyRules.Present(true, Rec(HeavyState.Carried, 2), 1, false) == HeavyRules.Show.Hidden
            && HeavyRules.Present(true, Rec(HeavyState.Carried, 1), 1, false) == HeavyRules.Show.Own, "heavy presentation unchanged");
    }

    static string Root()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && root != null; i++) { if (Directory.Exists(Path.Combine(root, "src", "Plugin"))) return Path.Combine(root, "src"); root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)); }
        throw new Exception("src not found");
    }
    static string Src(string dir, string f) { return File.ReadAllText(Path.Combine(Root(), dir, f)).Replace("\r\n", "\n"); }
}
