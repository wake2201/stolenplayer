// M4.3.3 hotfix (0.7.1): client-side lifecycle of Player 2's inventory (pure logic, offline/integration-tested).
// The plugin (Player2Sync) feeds it the host record from the registry, the local readiness and the network results, and
// supplies four operations: Capture (read Player 2's live game state), Apply (write a host record into the sandbox game
// state), Send (one "p2report" proposal) and Persist (small client-side pending file, only at scene exit / shutdown).
//
// States: Offline -> WaitingRecord (no valid host record yet) -> WaitingLocal (no loaded scene / player inventory) ->
// Acknowledging (record applied in this scene, acknowledgment report sent) -> Ready. Failed = a visible refusal (logged).
// Rules:
//  - The local game state is restored from the host record ONCE PER SCENE (every new GameManager: first join, travel,
//    return to the main menu and continue). The original game reloads the player from the sandbox prefs at every scene load,
//    which do not contain later changes (Shop_Manager.Buy_Item only writes fields) - restoring once per process (0.7.0)
//    would have reported the older prefs state back to the host and lost purchases.
//  - Before a scene is restored, unconfirmed local changes of the previous scene ("pending", captured at the exit hooks
//    SaveGame.LoseGame / SaveGameNewSystem) are committed first - but only if the host record is still exactly the one they
//    are based on (or the one their in-flight predecessor would produce). Otherwise the newer host state wins (logged).
//  - Ready: every 2 s (and before each CarBox transfer, at each exit hook) the live state is compared with the host record;
//    a difference is sent as ONE report based on that record; never more than one report in flight.
//  - Results: Accepted/Unchanged = committed (the host wrote it durably before answering). Stale = the host record is newer
//    -> if it already equals the report it counts as committed, else the host record is restored. Busy = bounded retries.
//  - Missing result: retry after Timeout s, at most MaxAttempts, then Failed (until the next scene / reconnect).
//  - Missing record: bounded resync requests (MaxResyncAsks per connection).
using System;

namespace TS2MP.Shared
{
    public enum P2SyncState : byte { Offline, WaitingRecord, WaitingLocal, Acknowledging, Ready, Failed }

    public sealed class P2ClientSync
    {
        public const double CheckInterval = 2.0, Timeout = 5.0, RetryDelay = 2.0, ResyncInterval = 8.0;
        public const int MaxAttempts = 5, MaxResyncAsks = 3;

        public Func<P2Inventory> Capture;                 // null when the local player inventory is not available
        public Func<P2Inventory, bool> Apply;             // false when it could not be applied (scene not ready)
        public Func<P2Inventory, uint> Send;              // proposal id, 0 = not sent
        public Action<P2Inventory, P2Inventory> Persist;  // (pending, predecessor); (null, null) = delete
        public Action RequestResync;
        public Action<string> Info, Warn, Error;

        public P2SyncState State { get; private set; }
        public uint Applied;                              // CarBox transfers contained in the local state
        public P2Inventory Pending { get; private set; }  // unconfirmed local state; header = record it is based on
        public P2Inventory PendingPrev { get; private set; }  // in-flight predecessor of Pending (its commit = base Rev + 1)
        public bool InFlight { get { return _flightId != 0; } }
        public bool Ready { get { return State == P2SyncState.Ready; } }
        public bool SceneRestored { get { return _base != null; } }

        P2Inventory _base;                                // host record header the live scene state derives from (null = not restored)
        P2Inventory _flight; uint _flightId; double _flightAt, _now; int _attempts; double _retryAt, _nextCheck, _waitSince, _nextAsk;
        int _asks; bool _ack, _pendingOnDisk, _recWarned, _failedLogged;
        public long Reports, Acks, Restores, Resyncs, Committed, StaleResults, Retries, Superseded, PendingRecovered, ExitCaptures, Failures;

        public P2ClientSync() { State = P2SyncState.Offline; }

        // Loaded at client start (pending file of an earlier process); validated against the host record later.
        public void LoadPending(P2Inventory pending, P2Inventory prev)
        {
            Pending = pending; PendingPrev = pending != null ? prev : null; _pendingOnDisk = pending != null;
            if (pending != null) Note("pending Player-2 state of an earlier session loaded (" + pending + "), committed after the host record is known");
        }

        // A new GameManager / the main menu: the live objects of the old scene are gone; the next scene is restored again.
        public void SceneLeft()
        {
            _attempts = 0; _failedLogged = false; _retryAt = 0;
            if (_base == null && State != P2SyncState.Failed) return;
            _base = null; _ack = false;
            if (State == P2SyncState.Ready || State == P2SyncState.Acknowledging || State == P2SyncState.Failed) Set(P2SyncState.WaitingLocal, "scene left");
        }

        // rec: the parsed host record (null = none); recInvalid: an entry exists but is unreadable.
        public void Tick(double now, bool live, P2Inventory rec, bool recInvalid, bool localReady, bool transferPending)
        {
            _now = now;
            if (!live)
            {
                if (State != P2SyncState.Offline)
                {
                    if (_flightId != 0) { if (Pending == null) Pending = _flight; _flightId = 0; }   // result lost with the connection: kept as pending
                    _attempts = 0; _asks = 0; _recWarned = false; _ack = false;
                    Set(P2SyncState.Offline, "connection lost");
                }
                return;
            }
            if (rec == null)
            {
                if (State != P2SyncState.WaitingRecord) { Set(P2SyncState.WaitingRecord, recInvalid ? "host record unreadable" : "no host record yet"); _waitSince = now; _nextAsk = now + ResyncInterval; }
                if (recInvalid && !_recWarned) { _recWarned = true; Err("host Player-2 record present but unreadable: inventory NOT restored, CarBox blocked"); }
                if (localReady && _asks < MaxResyncAsks && now >= _nextAsk && RequestResync != null)
                {
                    _asks++; _nextAsk = now + ResyncInterval; Resyncs++;
                    Note("no host Player-2 record " + (now - _waitSince).ToString("0") + " s after the scene became ready: resync request " + _asks + "/" + MaxResyncAsks);
                    RequestResync();
                }
                return;
            }
            if (State == P2SyncState.Offline || State == P2SyncState.WaitingRecord) Set(P2SyncState.WaitingLocal, "host record " + rec);
            if (_flightId != 0)
            {
                if (now - _flightAt < Timeout) return;
                _flightId = 0; Retries++;
                Note("report unanswered for " + Timeout + " s (attempt " + _attempts + "/" + MaxAttempts + ")");
                if (!CanRetry()) return;
            }
            if (State == P2SyncState.Failed) return;
            if (now < _retryAt) return;

            if (_base == null)
            {
                // previous scene / process left unconfirmed changes: commit them first, never over a newer host record
                if (Pending != null && !ResolvePending(now, rec)) return;
                if (!localReady) { if (State != P2SyncState.WaitingLocal) Set(P2SyncState.WaitingLocal, "scene not ready"); return; }
                if (Apply == null || !Apply(rec)) return;
                _base = rec; Applied = rec.AppliedTxn; Restores++; _ack = false; _attempts = 0;
                Set(P2SyncState.Acknowledging, "record " + rec + " applied to the scene");
                SendCurrent(now, rec, true);
                return;
            }
            if (transferPending) return;
            if (rec.Id != _base.Id) { Resync("host record replaced (id " + _base.Id + " -> " + rec.Id + ": new game / other save)"); return; }
            if (rec.AppliedTxn != Applied) { Resync("host record has transfer " + rec.AppliedTxn + ", this client " + Applied); return; }
            _base = rec;                                   // only this client and its own transfers change the record
            if (State != P2SyncState.Ready && State != P2SyncState.Acknowledging) { _ack = false; Set(P2SyncState.Acknowledging, "reconnected in the same scene, record " + rec); }
            if (State == P2SyncState.Acknowledging) { if (!_ack) SendCurrent(now, rec, true); return; }
            if (now < _nextCheck) return;
            _nextCheck = now + CheckInterval;
            if (localReady) SendCurrent(now, rec, false);
        }

        // Exit hooks (SaveGame.LoseGame = main menu / quit / application quit; SaveGameNewSystem = travel / quicksave):
        // the live state is captured while the scene still exists; sent now when possible, persisted until confirmed.
        // Allowed whenever this scene was restored (also offline: the state is kept for the next connection).
        public void CaptureExit(double now, bool live, P2Inventory rec)
        {
            _now = now;
            if (_base == null) return;
            var s = Capture != null ? Capture() : null;
            if (s == null) return;
            ExitCaptures++;
            if (_flightId != 0)
            {
                if (s.SameContent(_flight)) { Pending = _flight; PendingPrev = null; }
                else { Header(s, _flight); Pending = s; PendingPrev = _flight; }   // sent after the in-flight result
                Save();
                return;
            }
            if (Pending != null)
            {
                // unsent or lost-in-flight pending state (offline / Busy): the newer state follows it; its commit = base Rev + 1
                if (!s.SameContent(Pending)) { Header(s, Pending); PendingPrev = Pending; Pending = s; }
                Save();
                return;
            }
            var reference = live && rec != null && rec.Id == _base.Id && rec.AppliedTxn == Applied ? rec : _base;
            if (s.SameContent(reference)) return;
            Header(s, reference); Pending = s; PendingPrev = null;
            Save();
            if (live && (State == P2SyncState.Ready || State == P2SyncState.Acknowledging)) Fly(now, s);
        }

        // Before a CarBox transfer: the newest local state goes first (same ordered channel); refused while a report is out.
        public bool PrepareTransfer(double now, P2Inventory rec)
        {
            if (State != P2SyncState.Ready || _flightId != 0 || _base == null || rec == null || rec.Id != _base.Id || rec.AppliedTxn != Applied) return false;
            SendCurrent(now, rec, false);
            return true;
        }

        public bool OnResult(uint id, ProposalCode code, P2Inventory rec)
        {
            if (id == 0 || id != _flightId) return false;
            var f = _flight; _flightId = 0;
            bool ok = code == ProposalCode.Accepted || code == ProposalCode.Unchanged;
            if (!ok && code == ProposalCode.Stale && rec != null && rec.Id == f.Id && rec.AppliedTxn == f.AppliedTxn && rec.SameContent(f)) ok = true;   // already committed
            if (ok)
            {
                Committed++; _attempts = 0; _retryAt = 0;
                if (_base != null && State == P2SyncState.Acknowledging) { _ack = true; Acks++; Set(P2SyncState.Ready, "restore acknowledged by the host (" + code + ", record " + (rec != null ? rec.ToString() : f.ToString()) + ")"); }
                if (Pending != null && !ReferenceEquals(Pending, f) && ReferenceEquals(PendingPrev, f) && rec != null)
                {
                    Header(Pending, rec); PendingPrev = null; Save();        // newer exit state: now based on the committed predecessor
                    Fly(_now, Pending);
                }
                else if (Pending != null && (ReferenceEquals(Pending, f) || Pending.SameContent(f))) DropPending(null);
                return true;
            }
            if (code == ProposalCode.Stale)
            {
                StaleResults++;
                Warn2("report refused as stale (host record " + (rec != null ? rec.ToString() : "-") + ", report based on rev " + f.Rev + "): the newer host record is restored");
                if (Pending != null) { Superseded++; DropPending(null); }
                Resync("stale report");
                return true;
            }
            if (code == ProposalCode.Busy) { Note("report -> Busy (host not ready / durable write failed): retry"); CanRetry(); return true; }
            Fail("report -> " + code);
            return true;
        }

        bool ResolvePending(double now, P2Inventory rec)
        {
            var p = Pending;
            if (rec.Id == p.Id && rec.Rev == p.Rev && rec.AppliedTxn == p.AppliedTxn)
            {
                if (rec.SameContent(p)) { DropPending(null); return true; }
                PendingRecovered++; Note("committing pending Player-2 state of the previous scene/session (" + p + ")");
                Fly(now, p); return false;
            }
            if (PendingPrev != null && rec.Id == p.Id && rec.Rev == p.Rev + 1 && rec.AppliedTxn == p.AppliedTxn && rec.SameContent(PendingPrev))
            {
                Header(p, rec); PendingPrev = null; Save();
                PendingRecovered++; Note("predecessor report was committed; committing the newer pending state (" + p + ")");
                Fly(now, p); return false;
            }
            if (rec.Id == p.Id && rec.AppliedTxn == p.AppliedTxn && rec.SameContent(p)) { DropPending(null); return true; }   // committed, acknowledgment lost
            Superseded++;
            Warn2("pending Player-2 state (" + p + ") does not fit the host record (" + rec + "): newer host state kept, pending discarded");
            DropPending(null);
            return true;
        }

        void SendCurrent(double now, P2Inventory rec, bool force)
        {
            var s = Capture != null ? Capture() : null;
            if (s == null) return;
            if (!force && s.SameContent(rec)) { if (Pending != null) DropPending(null); return; }
            Header(s, rec);
            if (Pending != null) { Pending = s; PendingPrev = null; if (_pendingOnDisk) Save(); }
            Fly(now, s);
        }

        void Fly(double now, P2Inventory s)
        {
            uint id = Send != null ? Send(s) : 0;
            if (id == 0) { Fail("report could not be encoded/sent (" + s.Items.Count + " item types)"); return; }
            _flight = s; _flightId = id; _flightAt = now; _attempts++; Reports++;
        }

        bool CanRetry()
        {
            if (_attempts >= MaxAttempts) { Fail("no committed report after " + _attempts + " attempts"); return false; }
            _retryAt = _now + RetryDelay;   // the next tick after the delay resends from the current state
            return true;
        }

        void Resync(string why)
        {
            _base = null; _ack = false;
            Set(P2SyncState.WaitingLocal, "resync: " + why);
        }

        void Fail(string why)
        {
            Failures++;
            if (State != P2SyncState.Failed || !_failedLogged) Err("P2INV_FAIL " + why + " - Player-2 inventory not synchronized, CarBox blocked until the next scene/reconnect");
            _failedLogged = true;
            Set(P2SyncState.Failed, why);
        }

        void DropPending(string why)
        {
            if (why != null) Note(why);
            Pending = null; PendingPrev = null;
            if (_pendingOnDisk) { _pendingOnDisk = false; if (Persist != null) Persist(null, null); }
        }

        void Save() { _pendingOnDisk = true; if (Persist != null) Persist(Pending, PendingPrev); }

        static void Header(P2Inventory s, P2Inventory from) { s.Id = from.Id; s.Rev = from.Rev; s.AppliedTxn = from.AppliedTxn; }

        void Set(P2SyncState st, string why) { if (State == st) return; var old = State; State = st; Note("state " + old + " -> " + st + " (" + why + ")"); }
        void Note(string m) { if (Info != null) Info("P2INV " + m); }
        void Warn2(string m) { if (Warn != null) Warn("P2INV " + m); }
        void Err(string m) { if (Error != null) Error("P2INV " + m); }

        public string Stats
        {
            get
            {
                return "state " + State + " restoredScene " + (_base != null) + " applied " + Applied + (Pending != null ? " pending " + Pending : "") + (InFlight ? " inFlight" : "")
                    + " reports " + Reports + " committed " + Committed + " acks " + Acks + " restores " + Restores + " stale " + StaleResults + " retries " + Retries
                    + " superseded " + Superseded + " recovered " + PendingRecovered + " exitCaptures " + ExitCaptures + " resyncAsks " + Resyncs + " failures " + Failures;
            }
        }
    }

    // Client pending file (one record + optional in-flight predecessor), FNV-checked; written atomically only at scene exit.
    public static class P2PendingCodec
    {
        public static byte[] Encode(P2Inventory pending, P2Inventory prev)
        {
            var w = new LiteNetLib.Utils.NetDataWriter();
            w.Put((uint)0x31505032); pending.Write(w); w.Put(prev != null); if (prev != null) prev.Write(w);
            var body = w.CopyData();
            var o = new LiteNetLib.Utils.NetDataWriter(); o.Put(body.Length); o.Put(body); o.Put(Fnv(body));
            return o.CopyData();
        }

        public static bool TryDecode(byte[] data, out P2Inventory pending, out P2Inventory prev)
        {
            pending = null; prev = null;
            try
            {
                var r = new LiteNetLib.Utils.NetDataReader(data);
                int len = r.GetInt();
                if (len <= 0 || len > data.Length - 8) return false;
                var body = new byte[len]; r.GetBytes(body, len);
                if (r.GetUInt() != Fnv(body)) return false;
                var b = new LiteNetLib.Utils.NetDataReader(body);
                if (b.GetUInt() != 0x31505032 || !P2Inventory.TryRead(b, out pending)) { pending = null; return false; }
                if (b.GetBool() && !P2Inventory.TryRead(b, out prev)) { pending = null; prev = null; return false; }
                return b.AvailableBytes == 0;
            }
            catch { pending = null; prev = null; return false; }
        }

        static uint Fnv(byte[] d) { uint h = 2166136261; foreach (var x in d) { h ^= x; h *= 16777619; } return h; }
    }
}
