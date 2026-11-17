// S1 offline tests: shared sleep control. Pure rules (SleepControl / SleepShowFollower in src/Shared/Player2Inventory.cs),
// the host's sleep coordination (WorldHost: sleepStart / sleep / UpdateSleepShows in src/Shared/WorldTime.cs) and source
// scans of the Unity glue (SleepPresenter.cs, SleepControlIL.cs, WorldSync.cs). The real PassTime.Update IL rewrite is
// checked by tests/RedirectTest.cs. In-game behaviour (original clock / fast-forward UI, Esc refused for the observer, both
// presentations ending) is manual two-PC QA (docs/TESTING.md section 45).
using System;
using System.IO;
using TS2MP.Shared;

static class S1Tests
{
    static int _passed, _failed;
    const int Hillville = 97, Madison = 99;
    const byte Host = 1, Client = 2;
    static uint _pid = 500;

    static int Main()
    {
        Run("01 host sleeps: client in the same region presents it; client's own sleep refused; host end ends the event", HostSleeper);
        Run("02 client sleeps: host presents it (accepted start); host's own sleep refused there; completion ends the event", ClientSleeper);
        Run("03 cancellation ownership: observer Esc / StopIt buttons / QuitIt / PassStart refused, mod calls + survival rule kept", Ownership);
        Run("04 normal completion: watched -> no second advance; not watched -> from + advance once; one event per sleep", Completion);
        Run("05 sleeper cancels early: short cancel reported and released without a commit; longer cancel stops the observer at the sleeper's end", Cancel);
        Run("06 different regions: no presentation, no refusal, independent sleeps and clocks", DifferentRegions);
        Run("07 simultaneous requests: host sleeping -> client Busy; race in one region keeps the first committed event, no flapping", Simultaneous);
        Run("08 temporarily busy observer: retries every 0.5 s while the sleep runs, logs a reason once, one presentation per event", BusyRetry);
        Run("09 observer reaches the target first: holds until the sleeper's end, bounded", Hold);
        Run("10 disconnect / reconnect: host drops the client's sleep and its event; client re-presents a still running sleep", Reconnect);
        Run("11 glue: control gate, own-sleep refusal, retries, disconnect stop, short cancel report, no mod UI", Glue);
        Run("12 regressions: clock rules, sleepStart validation, wire unchanged by S1 (protocol 34 since H3), version 0.23.0", Regressions);
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
    static void Near(float a, float b, float eps, string m) { if (Math.Abs(TimeMath.SignedDiff(a, b)) > eps) throw new Exception(m + ": " + a + " vs " + b); }

    // ------------------------------------------------------------------------------------------------ host model helpers
    static WorldHost Session(int hostRegion, int clientRegion)
    {
        var wh = new WorldHost(7) { FallbackHours = 12f };
        wh.SetPlayerRegion(Host, hostRegion, 0);
        Expect(Propose(wh, new StateEntry { Scope = StateScope.Player, ScopeArg = Client, Key = WorldKeys.Region, Value = WorldKeys.Int(clientRegion) }, 0) == ProposalCode.Accepted, "client region");
        wh.Outbox.Clear();
        return wh;
    }
    static ProposalCode Propose(WorldHost wh, StateEntry p, double t) { int sr; float sh; return Propose(wh, p, t, out sr, out sh); }
    static ProposalCode Propose(WorldHost wh, StateEntry p, double t, out int sleepRegion, out float sleepHours)
    {
        uint rev;
        return wh.HandleProposal("cli", Client, ++_pid, p, t, out rev, out sleepRegion, out sleepHours);
    }
    static StateEntry StartP(int region, float from, int target) { return new StateEntry { Scope = StateScope.Region, ScopeArg = region, Key = WorldKeys.SleepStart, Value = new SleepStart { From = from, TargetHour = (byte)target }.ToBytes() }; }
    static StateEntry SleepP(int region, float from, float adv) { return new StateEntry { Scope = StateScope.Region, ScopeArg = region, Key = WorldKeys.Sleep, Value = new SleepRequest { From = from, Advance = adv }.ToBytes() }; }
    static bool Show(WorldHost wh, int region, out SleepShowState s)
    {
        s = default(SleepShowState);
        var e = wh.Reg.Get(StateScope.Region, region, WorldKeys.SleepShow);
        return e != null && SleepShowState.TryParse(e.Value, out s);
    }
    // what each instance would decide from the committed registry (WorldSync.OwnSleepBlocked / ShouldPresent)
    static bool Blocked(WorldHost wh, byte me, int myRegion)
    {
        SleepShowState s; bool has = Show(wh, myRegion, out s);
        bool remoteHere = me == Host && wh.Active != null && wh.Active.Region == myRegion;
        return SleepControl.OwnSleepBlocked(has, s, myRegion, me, myRegion, remoteHere);
    }
    static bool Presents(WorldHost wh, byte me, int myRegion) { SleepShowState s; return Show(wh, myRegion, out s) && SleepShowState.ShouldPresent(s, myRegion, me, myRegion); }

    // ------------------------------------------------------------------------------------------------ tests
    static void HostSleeper()
    {
        var wh = Session(Madison, Madison);
        Expect(!Blocked(wh, Client, Madison) && !Blocked(wh, Host, Madison), "nobody sleeps: nothing refused");
        wh.HostSleeping = true; wh.UpdateSleepShows(Madison, 6);
        SleepShowState s; Expect(Show(wh, Madison, out s) && s.Active && s.Initiator == Host && s.TargetHour == 6, "host sleep event committed");
        Expect(Presents(wh, Client, Madison), "client in the same region presents the host's sleep (original PassTime UI)");
        Expect(!Presents(wh, Host, Madison), "the sleeper never presents its own sleep");
        Expect(Blocked(wh, Client, Madison), "client's own PassStart refused while the host sleeps there");
        Expect(!Blocked(wh, Host, Madison), "the sleeper's own controls untouched");
        // the client asking anyway (race) -> Busy, nothing accepted
        Expect(Propose(wh, StartP(Madison, wh.Clocks.Model(Madison, 1), 9), 1) == ProposalCode.Busy, "client sleepStart while the host sleeps there -> Busy");
        Expect(wh.Active == null, "no second sleep");
        wh.HostSleeping = false; wh.UpdateSleepShows(Madison, -1);
        Expect(Show(wh, Madison, out s) && !s.Active && s.Initiator == Host, "host end (completed or Esc) -> event inactive -> client presentation stops");
        Expect(!Blocked(wh, Client, Madison), "client may sleep again");
    }

    static void ClientSleeper()
    {
        var wh = Session(Madison, Madison);
        float from = wh.Clocks.Model(Madison, 1);
        Expect(Propose(wh, StartP(Madison, from, 8), 1) == ProposalCode.Accepted, "client sleepStart accepted");
        Expect(wh.Active != null && wh.Active.Slot == Client && wh.Active.TargetHour == 8, "active client sleep");
        Expect(Blocked(wh, Host, Madison), "host's own PassStart refused immediately (accepted start, before the event commit)");
        wh.UpdateSleepShows(Madison, -1);
        SleepShowState s; Expect(Show(wh, Madison, out s) && s.Active && s.Initiator == Client && s.TargetHour == 8, "client sleep event");
        Expect(!Presents(wh, Client, Madison), "the sleeper does not present its own sleep");
        Expect(!Blocked(wh, Client, Madison), "client (sleeper) keeps its original controls");
        wh.SetWatching(true);   // host presented it: host PassTime drives the region clock
        int sr; float sh;
        Expect(Propose(wh, SleepP(Madison, from, 3f), 5, out sr, out sh) == ProposalCode.Accepted && sr == Madison, "completion accepted");
        Expect(wh.Active == null && wh.SleepWatched, "watched completion releases the transaction");
        wh.UpdateSleepShows(Madison, -1);
        Expect(Show(wh, Madison, out s) && !s.Active, "event ends with the sleeper's completion");
        Expect(!Blocked(wh, Host, Madison), "host may sleep again");
    }

    static void Ownership()
    {
        foreach (var own in new[] { false, true })
            Expect(!SleepControl.ObserverMay(SleepCall.EscKey, own, false) && !SleepControl.ObserverMay(SleepCall.QuitIt, own, false), "observer Esc / QuitIt never pass (cancel / close)");
        Expect(!SleepControl.ObserverMay(SleepCall.StopIt, false, false), "observer StopIt from a UI button refused");
        Expect(SleepControl.ObserverMay(SleepCall.StopIt, true, false), "the mod's own StopIt (sleeper ended) passes");
        Expect(SleepControl.ObserverMay(SleepCall.StopIt, false, true), "the game's own survival rule passes (documented limitation)");
        Expect(!SleepControl.ObserverMay(SleepCall.PassStart, false, false) && SleepControl.ObserverMay(SleepCall.PassStart, true, false), "PassStart only from the mod while presenting");
        // the gate applies only while presenting: the sleeper's prefixes return true before any rule (glue scan)
        var sp = Src("Plugin", "SleepPresenter.cs");
        Expect(Body(sp, "static bool StopItPrefix()").Contains("if (!Presenting || SleepControl.ObserverMay(SleepCall.StopIt, _own, _ruleStop)) return true;"), "StopIt gate only while presenting");
        Expect(Body(sp, "static bool QuitItPrefix()").Contains("if (!Presenting ||"), "QuitIt gate only while presenting");
        Expect(Body(sp, "public static bool ObserverKeyDown(KeyCode k)").Contains("if (!Presenting) return down;"), "Esc unchanged for the sleeper / without presentation");
        Expect(Body(sp, "public static void RuleStopIt(object pt)").Contains("_ruleStop = true;"), "survival StopIt marked as the game's rule");
    }

    static void Completion()
    {
        // not watched (host elsewhere): the region clock gets from + advance exactly once
        var wh = Session(Hillville, Madison);
        float from = wh.Clocks.Model(Madison, 1);
        Expect(Propose(wh, StartP(Madison, from, (int)TimeMath.Wrap24(from + 4)), 1) == ProposalCode.Accepted, "start");
        wh.UpdateSleepShows(Hillville, -1);
        int sr; float sh;
        Expect(Propose(wh, SleepP(Madison, from, 4f), 3, out sr, out sh) == ProposalCode.Accepted && sr == Madison && !wh.SleepWatched, "unwatched completion");
        Near(wh.Clocks.Model(Madison, 3), TimeMath.Wrap24(from + 4f), 1e-3f, "region clock = from + advance");
        Expect(Propose(wh, SleepP(Madison, from, 4f), 4, out sr, out sh) == ProposalCode.Invalid, "a repeated completion never advances twice");
        Near(wh.Clocks.Model(Madison, 4), TimeMath.Wrap24(from + 4f), 2e-2f, "still once");
        // watched: host clock authoritative, no commit of the client's advance
        var w2 = Session(Madison, Madison);
        float f2 = w2.Clocks.Model(Madison, 1);
        Expect(Propose(w2, StartP(Madison, f2, (int)TimeMath.Wrap24(f2 + 4)), 1) == ProposalCode.Accepted, "start 2");
        w2.SetWatching(true);
        float before = w2.Clocks.Model(Madison, 3);
        Expect(Propose(w2, SleepP(Madison, f2, 4f), 3, out sr, out sh) == ProposalCode.Accepted && w2.SleepWatched && sr == Madison, "watched completion");
        Near(w2.Clocks.Model(Madison, 3), before, 1e-3f, "watched: no second advance");
        Near(sh, TimeMath.Wrap24(f2 + 4f), 1e-3f, "sleeper's end handed to the host presentation (StopAtHours)");
    }

    static void Cancel()
    {
        Expect(SleepControl.ReportEnd(true, 0f) && SleepControl.ReportEnd(true, 0.01f) && !SleepControl.ReportEnd(false, 2f) && !SleepControl.ReportEnd(true, float.NaN), "client reports every started end");
        SleepRequest rq;
        Expect(SleepRequest.TryParse(new SleepRequest { From = 3f, Advance = SleepControl.WireAdvance(0f) }.ToBytes(), out rq), "zero advance travels as a valid payload");
        Expect(SleepControl.WireAdvance(2.5f) == 2.5f, "normal advance unchanged");
        // short cancel, not watched -> released, nothing committed
        var wh = Session(Hillville, Madison);
        float from = wh.Clocks.Model(Madison, 1);
        Expect(Propose(wh, StartP(Madison, from, 9), 1) == ProposalCode.Accepted, "start");
        wh.UpdateSleepShows(Hillville, -1);
        uint rev = wh.Reg.HeadRev;
        int sr; float sh;
        Expect(Propose(wh, SleepP(Madison, from, SleepControl.WireAdvance(0.01f)), 1.5, out sr, out sh) == ProposalCode.Accepted, "short cancel accepted");
        Expect(wh.Active == null && sr == Regions.None && float.IsNaN(sh), "transaction released, no catch-up requested");
        Expect(wh.Reg.Get(StateScope.Region, Madison, WorldKeys.Clock).Rev <= rev, "short cancel commits no clock");
        wh.UpdateSleepShows(Hillville, -1);
        SleepShowState s; Expect(Show(wh, Madison, out s) && !s.Active, "event ends with the cancel (any observer stops)");
        // short cancel, watched -> sleeper's end handed to the host presentation (stops it at once)
        var w2 = Session(Madison, Madison);
        float f2 = w2.Clocks.Model(Madison, 1);
        Expect(Propose(w2, StartP(Madison, f2, 9), 1) == ProposalCode.Accepted, "start 2"); w2.SetWatching(true);
        Expect(Propose(w2, SleepP(Madison, f2, SleepControl.WireAdvance(0f)), 1.2, out sr, out sh) == ProposalCode.Accepted && sr == Madison && w2.SleepWatched, "watched short cancel ends the host presentation");
        // longer cancel, watched: the observer stops when it reaches the sleeper's end (forward only, Frame / ForwardDiff)
        float end = TimeMath.Wrap24(f2 + 2f);
        Expect(TimeMath.ForwardDiff(end, TimeMath.Wrap24(f2 + 1f)) > 0.02f, "observer behind: keeps fast-forwarding to the sleeper's end");
        float d = TimeMath.ForwardDiff(end, TimeMath.Wrap24(f2 + 2.5f));
        Expect(d > 12f, "observer ahead: stops immediately (no backwards jump in the presentation)");
    }

    static void DifferentRegions()
    {
        var wh = Session(Madison, Hillville);
        wh.HostSleeping = true; wh.UpdateSleepShows(Madison, 7);
        Expect(!Presents(wh, Client, Hillville), "client in another region sees nothing");
        Expect(!Blocked(wh, Client, Hillville), "client may sleep in its own region meanwhile");
        float from = wh.Clocks.Model(Hillville, 1);
        float madison = wh.Clocks.Model(Madison, 1);
        Expect(Propose(wh, StartP(Hillville, from, 10), 1) == ProposalCode.Accepted, "independent client sleep accepted");
        wh.UpdateSleepShows(Madison, 7);
        SleepShowState a, b;
        Expect(Show(wh, Madison, out a) && a.Active && a.Initiator == Host && Show(wh, Hillville, out b) && b.Active && b.Initiator == Client && a.EventId != b.EventId, "two independent events");
        Expect(!Blocked(wh, Host, Madison), "host not refused by a sleep in another region");
        int sr; float sh;
        Expect(Propose(wh, SleepP(Hillville, from, 2f), 2, out sr, out sh) == ProposalCode.Accepted && !wh.SleepWatched, "unwatched (host elsewhere)");
        Near(wh.Clocks.Model(Hillville, 2), TimeMath.Wrap24(from + 2f), 1e-3f, "Hillville advanced");
        Near(wh.Clocks.Model(Madison, 2), madison, 0.05f, "Madison clock not touched by the client's sleep");
    }

    static void Simultaneous()
    {
        // client first, then the host's PassStart in the same frame window (race): the client's event stays, no flapping
        var wh = Session(Madison, Madison);
        float from = wh.Clocks.Model(Madison, 1);
        Expect(Propose(wh, StartP(Madison, from, 8), 1) == ProposalCode.Accepted, "client first");
        wh.UpdateSleepShows(Madison, -1);
        SleepShowState s0; Show(wh, Madison, out s0);
        wh.HostSleeping = true;   // would have been refused by OwnSleepBlocked; modelled as a lost race
        uint rev = wh.Reg.HeadRev;
        for (int i = 0; i < 10; i++) wh.UpdateSleepShows(Madison, 9);
        SleepShowState s1; Show(wh, Madison, out s1);
        Expect(wh.Reg.HeadRev == rev && s1.EventId == s0.EventId && s1.Initiator == Client, "first committed event kept, nothing committed per frame");
        wh.HostSleeping = false; wh.UpdateSleepShows(Madison, -1);
        Expect(wh.Reg.HeadRev == rev, "still nothing");
        // host first -> client Busy (exact tie decided by the host)
        var w2 = Session(Madison, Madison);
        w2.HostSleeping = true; w2.UpdateSleepShows(Madison, 6);
        Expect(Propose(w2, StartP(Madison, w2.Clocks.Model(Madison, 1), 9), 1) == ProposalCode.Busy && w2.Active == null, "client Busy");
        // a second client start while one runs -> Busy
        var w3 = Session(Madison, Madison);
        float f3 = w3.Clocks.Model(Madison, 1);
        Expect(Propose(w3, StartP(Madison, f3, 8), 1) == ProposalCode.Accepted && Propose(w3, StartP(Madison, f3, 9), 1.1) == ProposalCode.Busy, "one sleep at a time");
    }

    static void BusyRetry()
    {
        var f = new SleepShowFollower();
        var st = new SleepShowState { EventId = 11, Initiator = Host, TargetHour = 6, Active = true };
        Expect(f.Due(st, Madison, Client, Madison, 10), "due at once");
        Expect(f.Refused("game paused / UI open", 10), "first reason logged");
        Expect(!f.Due(st, Madison, Client, Madison, 10.2), "no retry before 0.5 s");
        Expect(f.Due(st, Madison, Client, Madison, 10.5), "retry after 0.5 s");
        Expect(!f.Refused("game paused / UI open", 10.5), "same reason not logged again");
        Expect(f.Refused("driving a moving car", 11), "new reason logged (moving car never interrupted)");
        Expect(f.Due(st, Madison, Client, Madison, 11.5), "still retrying while it runs");
        f.Started(11);
        Expect(!f.Due(st, Madison, Client, Madison, 20), "one presentation per event (no restart after it ended)");
        st.Active = false;
        Expect(!f.Due(st, Madison, Client, Madison, 21), "ended event: nothing");
        var n = new SleepShowState { EventId = 12, Initiator = Host, TargetHour = 9, Active = true };
        Expect(f.Due(n, Madison, Client, Madison, 22), "next sleep presented");
        var g = new SleepShowFollower();
        Expect(!g.Due(n, Madison, Host, Madison, 0), "the sleeper never presents its own sleep");
        Expect(!g.Due(n, Madison, Client, Hillville, 0), "other region never");
    }

    static void Hold()
    {
        Expect(SleepControl.AtTarget(false, 0) == ObserverAtTarget.Hold, "target first: hold");
        Expect(SleepControl.AtTarget(false, SleepControl.HoldMaxSeconds - 0.1) == ObserverAtTarget.Hold, "still holding");
        Expect(SleepControl.AtTarget(true, 0.1) == ObserverAtTarget.End, "sleeper finished: end");
        Expect(SleepControl.AtTarget(false, SleepControl.HoldMaxSeconds + 0.1) == ObserverAtTarget.End, "bounded (lost end)");
        var fr = Body(Src("Plugin", "SleepPresenter.cs"), "public bool Frame(float localHours)");
        Expect(fr.Contains("SleepControl.AtTarget(_sleeperDone") && fr.Contains("HoldUI()"), "Frame holds at the target");
        Expect(Body(Src("Plugin", "SleepPresenter.cs"), "public void StopAtHours(float hours)").Contains("_sleeperDone = true"), "host: sleeper's completion releases the hold");
    }

    static void Reconnect()
    {
        var wh = Session(Madison, Madison);
        float from = wh.Clocks.Model(Madison, 1);
        Expect(Propose(wh, StartP(Madison, from, 8), 1) == ProposalCode.Accepted, "start");
        wh.UpdateSleepShows(Madison, -1);
        wh.PlayerDisconnected(Client, 2);
        Expect(wh.Active == null, "sleeper disconnect drops the transaction (host presentation stops: Active == null, no StopAt)");
        wh.UpdateSleepShows(Madison, -1);
        SleepShowState s; Expect(Show(wh, Madison, out s) && !s.Active, "event ended");
        Expect(!Blocked(wh, Host, Madison), "host free again");
        // client observer: a reconnect forgets the presented event and re-presents a still running one from the snapshot
        var f = new SleepShowFollower();
        var st = new SleepShowState { EventId = 3, Initiator = Host, TargetHour = 6, Active = true };
        Expect(f.Due(st, Madison, Client, Madison, 0), "due"); f.Started(3);
        Expect(!f.Due(st, Madison, Client, Madison, 1), "presented");
        f.Reset();
        Expect(f.Due(st, Madison, Client, Madison, 2) && f.Shown == 0, "after reconnect the running host sleep is presented again");
        var ws = Src("Plugin", "WorldSync.cs");
        var dis = Body(ws, "void OnRemoteDisconnected(string reason)");
        Expect(dis.Contains("_presenter.Stop(\"disconnected\")") && dis.Contains("_follow.Reset()"), "client stops the presentation on disconnect and forgets the event");
    }

    static void Glue()
    {
        var sp = Src("Plugin", "SleepPresenter.cs");
        var il = Src("Plugin", "SleepControlIL.cs");
        var ws = Src("Plugin", "WorldSync.cs");
        Expect(sp.Contains("_ctl.Patch(update, transpiler:") && sp.Contains("\"StopItPrefix\"") && sp.Contains("\"QuitItPrefix\"") && sp.Contains("\"PassStartPrefix\""), "PassTime Update / StopIt / QuitIt / PassStart patched");
        Expect(il.Contains("\"GetKeyDown\"") && il.Contains("\"StopIt\"") && il.Contains("if (esc != 1 || rule != 1) return code;"), "IL rewrite only on the exact verified pattern");
        var ps = Body(sp, "static bool PassStartPrefix(object __instance)");
        Expect(ps.Contains("if (_own || SleepHooks.Observer) return true;") && ps.Contains("\"you_cant_sleep\"") && ps.Contains("OwnSleepBlocked"), "own sleep refused with the game's own warning only");
        var ts = Body(sp, "public bool TryStart(object gm, int targetHour, uint eventId, out string why)");
        Expect(ts.Contains("_own = true;") && ts.Contains("finally { _own = false; }") && ts.Contains("driving a moving car"), "mod's own PassTime calls marked; moving car never interrupted");
        Expect(Body(sp, "public void Stop(string why)").Contains("_own = true;"), "mod's StopIt marked");
        Expect(Body(sp, "public void Dispose()").Contains("_ctl.UnpatchSelf()"), "gate removed on shutdown");
        Expect(!sp.Contains("OnGUI") && !sp.Contains("GUI.Label") && !il.Contains("OnGUI"), "no mod UI");
        Expect(ws.Contains("SleepPresenter.OwnSleepBlocked = OwnSleepBlocked;") && Body(ws, "void SleepShowFrame()").Contains("_wh.UpdateSleepShows(_region, SleepHooks.TargetHour)"), "WorldSync wiring");
        var host = Body(ws, "void HostFrame(");
        Expect(host.Contains("HostShowRetry(gm);"), "host retries the presentation of a running client sleep");
        Expect(Body(ws, "bool HostTryPresent(").Contains("StopLapse(\"handed over to the sleep presentation\", false)"), "time-lapse hands over without resetting the day length");
        Expect(Body(ws, "void HostShowRetry(").Contains("_wh.SetWatching(true)"), "late presentation makes the host clock authoritative (no second advance)");
        var end = Body(ws, "void OnSleepEnded(bool completed)");
        Expect(end.Contains("SleepControl.ReportEnd(true, req.Advance)") && end.Contains("SleepControl.WireAdvance(req.Advance)") && !end.Contains("req.Advance < SleepTransaction.MinAdvance) return;"), "client reports a short cancel");
        var cs = Body(ws, "void ClientShowFrame(UnityEngine.Object gm)");
        Expect(cs.Contains("_follow.Due(") && cs.Contains("_follow.Refused(why, Now)") && cs.Contains("_presenter.Stop(\"the sleeper finished or stopped\")"), "client: retries, ends with the sleeper");
        Expect(Body(ws, "void OnLocalRegionChanged(int region, double now)").Contains("_presenter.Stop(\"region changed\")"), "travel / scene change stops the presentation");
        Expect(Body(sp, "public bool Frame(float localHours)").Contains("PassTime / player destroyed (scene change)"), "scene change detected");
    }

    static void Regressions()
    {
        var wh = Session(Madison, Madison);
        Expect(Propose(wh, StartP(Madison, TimeMath.Wrap24(wh.Clocks.Model(Madison, 1) + 5f), 8), 1) == ProposalCode.Invalid, "sleepStart far from the region model still Invalid");
        Expect(Propose(wh, StartP(Hillville, 12f, 8), 1) == ProposalCode.NotAuthorized, "sleepStart for a region the client is not in still refused");
        int sr; float sh;
        Expect(Propose(wh, SleepP(Madison, 12f, 2f), 1, out sr, out sh) == ProposalCode.Invalid, "completion without an accepted start never advances");
        Expect(SleepTransaction.MinAdvance == 0.05f && TimeMath.SleepCycleMinutes == 0.2f, "clock constants unchanged");
        Expect(WorldKeys.SleepShow == "sleepShow" && WorldKeys.SleepStart == "sleepStart" && WorldKeys.Sleep == "sleep", "keys unchanged (no new networking)");
        Expect(new SleepShowState { EventId = 1, Initiator = 1, TargetHour = 1, Active = true }.ToBytes().Length == 7 && SleepStart.WireSize == 5, "wire sizes unchanged");
        Expect(ProtocolInfo.ProtocolVersion == 37 && ProtocolInfo.ModVersion == "0.26.4", "protocol 34 / mod 0.23.0 (H3 wire change; S1 itself changed no wire values)");
    }

    // ------------------------------------------------------------------------------------------------ source helpers
    static string Src(params string[] p)
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var f = Path.Combine(dir, "src", Path.Combine(p));
            if (File.Exists(f)) return File.ReadAllText(f);
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new Exception("source not found: " + string.Join("/", p));
    }
    static string Body(string src, string signature)
    {
        int a = src.IndexOf(signature, StringComparison.Ordinal); if (a < 0) throw new Exception("missing " + signature);
        int open = src.IndexOf('{', a), depth = 0;
        for (int i = open; i < src.Length; i++) { if (src[i] == '{') depth++; else if (src[i] == '}' && --depth == 0) return src.Substring(a, i - a + 1); }
        throw new Exception("unbalanced " + signature);
    }
}
