// M4.3.3 hotfix (0.7.1) integration tests: Player 2's inventory lifecycle through REAL message sequencing.
// Host side = WorldHost + P2Book + a simulated p2inv file (the host half of Player2Sync); client side = client StateRegistry +
// P2ClientSync + a simulated original game (sandbox prefs, live fields, scene loads that re-read the prefs like ItemID.Start /
// SaveGame.LoadGame, Shop_Manager.Buy_Item writing only fields). Every message passes through the real StateMessages codecs
// over a reliable-ordered link that can be cut. These tests found the 0.7.0 defect (empty-backpack record rejected) and
// the follow-up rollback on menu return; they still do not prove Unity scene timing - that remains manual QA.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class Player2LifecycleTests
{
    static int _passed, _failed;
    const int Flashlight = 31, Lockpick = 12, Watch = 501;
    const int Madison = 99, Hideout = 0;
    static readonly ushort Ranger = VehicleIds.FromCarNr(0), GT = VehicleIds.FromCarNr(1);

    static int Main()
    {
        Run("01 empty-backpack record (the 0.7.0 defect): v2 and v1 encodings parse; truncated still refused", EmptyRecord);
        Run("02 client connects before the host loaded its save; first record created after the handshake", ConnectBeforeHostLoad);
        Run("03 client scene loads after the handshake: restore waits for the local inventory", SceneAfterHandshake);
        Run("04-06 snapshot delivered when both are ready, applied, acknowledged; host and client synchronized", RestoreAndAck);
        Run("07 missing initial record: bounded resync requests recover it; none -> at most 3 asks", MissingRecordRecovery);
        Run("08 duplicate snapshot / duplicate record never resets newer state", DuplicateSnapshot);
        Run("09-10 purchase after sync: host receives it and persists it before acknowledging", PurchasePersisted);
        Run("11-15 purchase then main menu at once (inside the 2 s window), rejoin without host restart", MenuRejoin);
        Run("12b main menu while disconnected: pending file, client restart, recovered exactly once", PendingAcrossRestart);
        Run("12c exit while a report is in flight with a newer state (predecessor chain)", ExitDuringFlight);
        Run("16 restart of both instances preserves committed data (sandbox reseeded with Player 1's copy)", RestartBoth);
        Run("17-19 CarBox only after readiness; deposit / withdraw as host transactions; exclusive lease", CarBoxAfterReady);
        Run("20 Blackbay guard rules (client backpack only, never with CarBox open; host storage during client lease)", Blackbay);
        Run("21 old pending report / replayed report cannot overwrite newer transfers or reports", OldReportsRefused);
        Run("22 real new game on the host: new record id, client restores it, old pending discarded", NewGameReset);
        Run("23a host durable write failure: Busy, bounded retries, visible failure, recovery next scene", WriteFailure);
        Run("23b travel keeps the state (exit capture + restore in the next scene)", Travel);
        Run("23c cost: report bytes, messages per purchase, no traffic while unchanged", Cost);
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

    // ------------------------------------------------------------------ harness
    sealed class Link
    {
        public readonly Queue<byte[]> ToClient = new Queue<byte[]>(), ToHost = new Queue<byte[]>();
        public bool Up = true;
        public int DropNextToClient;        // drop the next n host->client messages (simulates a lost message for recovery tests)
        public long Msgs, Bytes;
        public void SendToClient(NetDataWriter w) { if (!Up) return; if (DropNextToClient > 0) { DropNextToClient--; return; } ToClient.Enqueue(w.CopyData()); Msgs++; Bytes += w.Length; }
        public void SendToHost(NetDataWriter w) { if (!Up) return; ToHost.Enqueue(w.CopyData()); Msgs++; Bytes += w.Length; }
        public void Cut() { Up = false; ToClient.Clear(); ToHost.Clear(); }
    }

    sealed class HostSim
    {
        public WorldHost Wh = new WorldHost(0xE889158C) { FallbackHours = 12f };
        public P2Book Book; public byte[] Disk; public bool FailWrites; public bool Synced; public long Writes;
        public Dictionary<int, int> Storage = new Dictionary<int, int>();
        public Link L; readonly NetDataWriter _w = new NetDataWriter();
        public List<string> Log = new List<string>();

        public HostSim()
        {
            Wh.P2ReportHook = Report; Wh.StorageTxnHook = Txn;
            Wh.CarBoxSnapshot = () => { var it = new CarBoxItems { Rev = 1 }; byte[] d; it.TryEncode(out d); return d; };
            Wh.SetPlayerRegion(1, Madison, 0); Wh.SetPlayerRegion(2, Madison, 0);
            Wh.Fleet.Register(Ranger, 0, true, Wh.Outbox); Wh.Fleet.Register(GT, 1, true, Wh.Outbox);
            Wh.Fleet.Place(Ranger, Madison, Wh.Outbox); Wh.Fleet.Place(GT, Hideout, Wh.Outbox);
            Wh.Outbox.Clear();
        }

        // Player2Sync.HostBind/LoadBook: the profile's file, else a new record (original new game: empty backpack, 500).
        public void LoadSave(int historyId)
        {
            P2Book b = null; string err;
            if (Disk != null && P2Book.TryDeserialize(Disk, 0, out b, out err) && b.HistoryId != historyId) b = null;
            if (b == null) { b = new P2Book(P2Inventory.Initial(false), 0) { HistoryId = historyId }; b.Rec.Id = historyId; b.Rec.Rev = 1; Disk = b.Serialize(0); }
            Book = b;
            Wh.CommitP2(Book.Rec); Flush();
        }

        ProposalCode Report(P2Inventory rep)
        {
            if (Book == null) return ProposalCode.Busy;
            var before = Book.Rec;
            var code = Book.AcceptReport(rep);
            if (code == ProposalCode.Accepted)
            {
                if (FailWrites) { Book.Rec = before; return ProposalCode.Busy; }
                Disk = Book.Serialize(0); Writes++;
                Wh.CommitP2(Book.Rec);
            }
            if ((code == ProposalCode.Accepted || code == ProposalCode.Unchanged) && !Synced) Synced = true;
            Log.Add("report " + rep + " -> " + code);
            return code;
        }

        ProposalCode Txn(StorageTxnRequest t)
        {
            int c; Storage.TryGetValue(t.ItemId, out c);
            int d; var code = Book.Transfer(t.Txn, t.ItemId, t.Withdraw, c, true, out d);
            if (code != ProposalCode.Accepted) return code;
            Storage[t.ItemId] = c + d; Disk = Book.Serialize(0); Writes++;
            Wh.CommitP2(Book.Rec);
            return code;
        }

        public void Connected() { Synced = false; Flush(); Wh.Outbox.Clear(); _w.Reset(); StateMessages.WriteSnapshot(_w, Wh.Reg); L.SendToClient(_w); }

        public void Flush() { foreach (var e in Wh.Outbox) { _w.Reset(); StateMessages.WriteDelta(_w, Wh.Reg.Epoch, e); if (L != null) L.SendToClient(_w); } Wh.Outbox.Clear(); }

        public void Pump(string clientInstance, double now)
        {
            while (L.ToHost.Count > 0)
            {
                var r = new NetDataReader(L.ToHost.Dequeue());
                var type = (MsgType)r.GetByte();
                if (type == MsgType.StateProposal)
                {
                    uint id; StateEntry p; Expect(StateMessages.TryReadProposal(r, out id, out p), "proposal codec");
                    uint rev; int sr; float sh;
                    var code = Wh.HandleProposal(clientInstance, 2, id, p, now, out rev, out sr, out sh);
                    Flush();                                     // deltas precede the result (WorldSync.HostProposal)
                    _w.Reset(); StateMessages.WriteResult(_w, id, code, rev); L.SendToClient(_w);
                }
                else if (type == MsgType.StateResyncRequest) { Flush(); _w.Reset(); StateMessages.WriteSnapshot(_w, Wh.Reg); L.SendToClient(_w); }
            }
        }
    }

    sealed class ClientSim
    {
        public StateRegistry Reg = StateRegistry.CreateClient();
        public P2ClientSync Sync = new P2ClientSync();
        public string Instance; uint _nextId = 1; public Link L; readonly NetDataWriter _w = new NetDataWriter();
        // simulated original game (sandbox)
        public Dictionary<int, int> Prefs = new Dictionary<int, int>(); public int PrefMoney;
        public Dictionary<int, int> Live = new Dictionary<int, int>(); public int Money; public bool Scene;
        public byte[] PendingFile; public long PendingWrites;
        public uint CarBoxPending, TxnPending; public uint Held; public long CarBoxRefused;
        public List<string> Log = new List<string>();
        public bool Connected;

        public ClientSim(string instance, byte[] pendingFile)
        {
            Instance = instance; PendingFile = pendingFile;
            Sync.Capture = () => { if (!Scene) return null; var s = new P2Inventory { Money = Money }; foreach (var kv in Live) if (kv.Value > 0) s.Items[kv.Key] = kv.Value; return s; };
            Sync.Apply = rec => { if (!Scene) return false; Live.Clear(); Prefs.Clear(); foreach (var kv in rec.Items) { Live[kv.Key] = kv.Value; Prefs[kv.Key] = kv.Value; } Money = rec.Money; PrefMoney = rec.Money; return true; };
            Sync.Send = s => { byte[] d; if (!Connected || !s.TryEncode(out d)) return 0; return Propose(WorldKeys.P2Report, 2, d); };
            Sync.Persist = (p, prev) => { PendingWrites++; PendingFile = p == null ? null : P2PendingCodec.Encode(p, prev); };
            Sync.RequestResync = () => { _w.Reset(); StateMessages.WriteResync(_w, Reg.Epoch, Reg.HeadRev); L.SendToHost(_w); };
            Sync.Info = m => Log.Add(m); Sync.Warn = m => Log.Add("WARN " + m); Sync.Error = m => Log.Add("ERROR " + m);
            if (pendingFile != null) { P2Inventory p, prev; Expect(P2PendingCodec.TryDecode(pendingFile, out p, out prev), "pending file codec"); Sync.LoadPending(p, prev); }
        }

        public uint Propose(string key, int arg, byte[] v)
        {
            uint id = _nextId++;
            _w.Reset(); StateMessages.WriteProposal(_w, id, new StateEntry { Scope = StateScope.Session, ScopeArg = arg, Key = key, Value = v }); L.SendToHost(_w);
            return id;
        }

        public P2Inventory Record(out bool invalid)
        {
            invalid = false;
            if (!Connected || !Reg.HasSnapshot) return null;
            var e = Reg.Get(StateScope.Session, 2, WorldKeys.P2Inv);
            if (e == null) return null;
            P2Inventory r; if (P2Inventory.TryParse(e.Value, out r)) return r;
            invalid = true; return null;
        }
        public P2Inventory Record() { bool i; return Record(out i); }

        public void Tick(double now) { bool inv; var rec = Record(out inv); Sync.Tick(now, Connected && Reg.HasSnapshot, rec, inv, Scene, TxnPending != 0); }

        public List<KeyValuePair<uint, ProposalCode>> OtherResults = new List<KeyValuePair<uint, ProposalCode>>();
        public void Pump()
        {
            while (L.ToClient.Count > 0)
            {
                var r = new NetDataReader(L.ToClient.Dequeue());
                var type = (MsgType)r.GetByte();
                if (type == MsgType.StateSnapshot) { uint ep, head; List<StateEntry> es; Expect(StateMessages.TryReadSnapshot(r, out ep, out head, out es), "snapshot codec"); Reg.LoadSnapshot(ep, head, es); }
                else if (type == MsgType.StateDelta)
                {
                    uint ep; StateEntry e; Expect(StateMessages.TryReadDelta(r, out ep, out e), "delta codec");
                    var res = Reg.ApplyDelta(ep, e);
                    if (res == DeltaResult.Gap || res == DeltaResult.WrongEpoch) { _w.Reset(); StateMessages.WriteResync(_w, Reg.Epoch, Reg.HeadRev); L.SendToHost(_w); }
                }
                else if (type == MsgType.ProposalResult)
                {
                    uint id, rev; ProposalCode code; Expect(StateMessages.TryReadResult(r, out id, out code, out rev), "result codec");
                    if (Sync.OnResult(id, code, Record())) continue;
                    if (id == CarBoxPending) { CarBoxPending = 0; if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged) { var le = Reg.Get(StateScope.Session, 0, WorldKeys.CarBox); CarBoxLeaseState st; if (le != null && CarBoxLeaseState.TryParse(le.Value, out st) && st.Owner == 2) Held = st.Session; } else CarBoxRefused++; continue; }
                    if (id == TxnPending) { TxnPending = 0; OtherResults.Add(new KeyValuePair<uint, ProposalCode>(id, code)); continue; }
                    OtherResults.Add(new KeyValuePair<uint, ProposalCode>(id, code));
                }
            }
        }

        // original game: a scene load re-reads the sandbox prefs (ItemID.Start, SaveGame.LoadGame); a new GameManager = SceneLeft
        public void LoadScene() { Sync.SceneLeft(); Scene = true; Live = new Dictionary<int, int>(Prefs); Money = PrefMoney; }
        // GameManager.LoadMenu -> SaveGame.LoseGame (prefix = exit capture) -> MainMenuNew; nothing written to the prefs
        public void LeaveToMenu(double now) { Sync.CaptureExit(now, Connected && Reg.HasSnapshot, Record()); Scene = false; Sync.SceneLeft(); }
        // Select<Map>: SaveGameNewSystem (prefix = exit capture, then writes the prefs) -> MainScene reload
        public void Travel(double now) { Sync.CaptureExit(now, Connected && Reg.HasSnapshot, Record()); Prefs = new Dictionary<int, int>(Live); PrefMoney = Money; Scene = false; Sync.SceneLeft(); LoadScene(); }
        // Shop_Manager.Buy_Item: item_amount++ and moneyAm -= price on the live fields only (IL), no prefs write
        public void Buy(int item, int price) { int c; Live.TryGetValue(item, out c); Live[item] = c + 1; Money -= price; }

        public bool CarBoxReady { get { return Sync.Ready; } }
        public bool TryOpenCarBox() { if (!CarBoxReady) { CarBoxRefused++; return false; } CarBoxPending = Propose(WorldKeys.CarBoxReq, 0, CarBoxRequest.Encode(CarBoxOp.Open, 0)); return true; }
        public bool TryTransfer(int item, bool withdraw)
        {
            if (Held == 0 || !Sync.PrepareTransfer(_now, Record())) return false;
            var t = new StorageTxnRequest { Session = Held, Txn = Sync.Applied + 1, ItemId = item, Withdraw = withdraw };
            _txnItem = item; _txnWithdraw = withdraw; _txnNo = t.Txn;
            TxnPending = Propose(WorldKeys.StorageTxn, 0, t.Encode());
            return true;
        }
        public int _txnItem; public bool _txnWithdraw; public uint _txnNo; public double _now;
        public void ApplyAckedTransfer() { int c; Live.TryGetValue(_txnItem, out c); Live[_txnItem] = c + (_txnWithdraw ? 1 : -1); Sync.Applied = _txnNo; }
    }

    sealed class World
    {
        public HostSim H = new HostSim(); public ClientSim C; public Link L = new Link(); public double Now;
        public bool HostPaused;            // host does not process (a report stays in flight)
        public World(ClientSim c = null) { H.L = L; Attach(c ?? new ClientSim("cli-1", null)); }
        public void Attach(ClientSim c) { C = c; C.L = L; }
        public void Connect() { L.Up = true; C.Connected = true; C.Reg.Reset(); H.Connected(); }
        public void Disconnect() { L.Cut(); C.Connected = false; H.Synced = false; }
        public void Step(int n = 1) { for (int i = 0; i < n; i++) { Now += 0.1; C._now = Now; C.Pump(); C.Tick(Now); if (!HostPaused) H.Pump(C.Instance, Now); C.Pump(); } }
        public void Until(Func<bool> cond, double maxSeconds, string what) { double end = Now + maxSeconds; while (!cond()) { if (Now > end) throw new Exception("timeout waiting for " + what + " | client: " + C.Sync.Stats + " | log: " + string.Join(" / ", C.Log.ToArray())); Step(); } }
        public P2Book DiskBook() { P2Book b = null; string e; Expect(H.Disk != null && P2Book.TryDeserialize(H.Disk, 0, out b, out e), "host file readable"); return b; }
    }

    static World Joined()
    {
        var w = new World();
        w.H.LoadSave(4711);
        w.Connect();
        w.C.Prefs[Lockpick] = 9; w.C.PrefMoney = 99999;          // SaveGuard.SeedSandbox: the sandbox starts as a copy of Player 1's save
        w.C.LoadScene();
        w.Until(() => w.C.Sync.Ready, 3, "ready");
        return w;
    }

    // ------------------------------------------------------------------ tests
    static void EmptyRecord()
    {
        var r = P2Inventory.Initial(false); r.Id = 77; r.Rev = 1;
        byte[] d; Expect(r.TryEncode(out d), "encode");
        Expect(d.Length == P2Inventory.HeaderV2, "empty v2 record = header only (" + d.Length + " B)");
        P2Inventory back; Expect(P2Inventory.TryParse(d, out back) && back.SameContent(r) && back.Id == 77 && back.Rev == 1, "empty v2 record parses (0.7.0 rejected every empty-backpack record)");
        var w = new NetDataWriter(); w.Put((byte)1); w.Put(5u); w.Put(0u); w.Put(200); w.Put(0); w.Put(100); w.Put(100); w.Put((ushort)0);
        Expect(w.Length == P2Inventory.HeaderV1, "v1 empty record is 27 bytes");
        Expect(P2Inventory.TryParse(w.CopyData(), out back) && back.Money == 200 && back.Rev == 5 && back.Id == 0, "v1 empty record (0.7.0 p2inv file) parses");
        var cut = new byte[d.Length - 1]; Array.Copy(d, cut, cut.Length);
        Expect(!P2Inventory.TryParse(cut, out back), "truncated refused");
        var book = new P2Book(r, 0) { HistoryId = 77 }; P2Book bb; string err;
        Expect(P2Book.TryDeserialize(book.Serialize(0), 0, out bb, out err) && bb.Rec.Id == 77, "host file with an empty-backpack record survives a restart (" + err + ")");
    }

    static void ConnectBeforeHostLoad()
    {
        var w = new World();
        w.Connect();                                              // host still in the main menu: no record
        w.C.LoadScene();
        w.Step(10);
        Expect(w.C.Sync.State == P2SyncState.WaitingRecord && !w.C.CarBoxReady, "client waits for the record (CarBox blocked)");
        w.H.LoadSave(4711);                                       // record created after the handshake, delivered as a delta
        w.Until(() => w.C.Sync.Ready, 3, "ready after the host loaded");
        Expect(w.C.Money == 500 && w.C.Live.Count == 0, "original new-game state applied, Player 1's sandbox copy replaced (money " + w.C.Money + ", items " + w.C.Live.Count + ")");
        Expect(w.H.Synced, "host saw the acknowledgment");
    }

    static void SceneAfterHandshake()
    {
        var w = new World(); w.H.LoadSave(4711); w.Connect();
        w.Step(10);
        Expect(w.C.Sync.State == P2SyncState.WaitingLocal, "record known, no scene yet: " + w.C.Sync.State);
        w.C.LoadScene();
        w.Until(() => w.C.Sync.Ready, 3, "ready after the scene loaded");
    }

    static void RestoreAndAck()
    {
        var w = Joined();
        Expect(w.C.Sync.Restores == 1 && w.C.Sync.Acks == 1, "one restore, one acknowledgment");
        Expect(w.H.Synced && w.C.Sync.Ready, "both synchronized");
        Expect(w.C.Prefs.Count == 0 && w.C.PrefMoney == 500, "restore written into the sandbox prefs too");
        w.Step(50);
        Expect(w.C.Sync.Reports == 1, "no further reports while nothing changes (" + w.C.Sync.Reports + ")");
    }

    static void MissingRecordRecovery()
    {
        var w = new World(); w.Connect();
        w.C.LoadScene();
        w.L.DropNextToClient = 1;                                 // the delta that carries the new record is lost
        w.H.LoadSave(4711);
        w.Step(20);
        Expect(w.C.Sync.State == P2SyncState.WaitingRecord, "record missing on the client");
        w.Until(() => w.C.Sync.Ready, 20, "recovered by a resync request");
        Expect(w.C.Sync.Resyncs >= 1 && w.C.Sync.Resyncs <= P2ClientSync.MaxResyncAsks, "bounded resync requests: " + w.C.Sync.Resyncs);
        var w2 = new World(); w2.Connect(); w2.C.LoadScene();
        w2.Step(600);                                             // 60 s, host never loads a save
        Expect(w2.C.Sync.Resyncs == P2ClientSync.MaxResyncAsks, "at most " + P2ClientSync.MaxResyncAsks + " asks without a record: " + w2.C.Sync.Resyncs);
    }

    static void DuplicateSnapshot()
    {
        var w = Joined();
        w.C.Buy(Flashlight, 35); w.Step(30);
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 1, "purchase committed");
        w.H.Connected();                                          // the host sends the snapshot again (resync) with the same record
        w.C.Live[Watch] = 1;                                      // newer local change not yet reported
        w.Step(1);
        Expect(w.C.Live[Watch] == 1 && w.C.Live[Flashlight] == 1 && w.C.Sync.Restores == 1, "no re-restore in the same scene, local state intact");
        w.Step(30);
        Expect(w.H.Book.Rec.CountOf(Watch) == 1 && w.H.Book.Rec.CountOf(Flashlight) == 1, "newer change committed afterwards");
        w.H.Wh.CommitP2(w.H.Book.Rec); w.H.Flush(); w.Step(5);    // committing the same record again: no new revision
        Expect(w.C.Sync.Restores == 1, "same record again: nothing reset");
    }

    static void PurchasePersisted()
    {
        var w = Joined();
        long writes = w.H.Writes;
        w.C.Buy(Flashlight, 35);
        w.Until(() => w.H.Book.Rec.CountOf(Flashlight) == 1, 3, "report within the 2 s check");
        var disk = w.DiskBook();
        Expect(disk.Rec.CountOf(Flashlight) == 1 && disk.Rec.Money == 465, "host file has the purchase before the acknowledgment (money " + disk.Rec.Money + ")");
        Expect(w.H.Writes == writes + 1, "exactly one durable write for one purchase");
        w.Step(5);
        Expect(w.C.Sync.Pending == null && w.C.PendingFile == null, "no client pending file for an ordinary confirmed report");
    }

    static void MenuRejoin()
    {
        var w = Joined();
        w.Step(25);                                               // let a periodic check pass
        w.C.Buy(Flashlight, 35);
        w.Step(1);                                                // well inside the 2 s window
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 0, "not reported yet (periodic check not due)");
        w.C.LeaveToMenu(w.Now);                                   // GameManager.LoadMenu -> SaveGame.LoseGame
        Expect(w.C.PendingFile != null, "unconfirmed exit state persisted on the client");
        w.Step(3);
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 1 && w.H.Book.Rec.Money == 465, "final synchronization committed by the host");
        Expect(w.C.PendingFile == null, "pending file deleted after the commit");
        Expect(!w.C.Prefs.ContainsKey(Flashlight), "the sandbox prefs do NOT contain the flashlight (the original game never wrote it)");
        w.C.LoadScene();                                          // Continue: the scene re-reads the prefs (0.7.0 would have reported this back)
        w.Until(() => w.C.Sync.Ready, 3, "ready after rejoin");
        Expect(w.C.Live[Flashlight] == 1 && w.C.Money == 465, "flashlight present and money correct after rejoin");
        w.Step(30);
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 1 && w.H.Book.Rec.Money == 465, "no rollback reported to the host");
        Expect(w.H.Book.Rec.CountOf(Lockpick) == 0, "Player 1's sandbox items never reached Player 2's record");
    }

    static void PendingAcrossRestart()
    {
        var w = Joined();
        w.Step(25);                                               // periodic check not due during the purchase
        w.C.Buy(Flashlight, 35); w.Step(1);
        w.Disconnect();                                           // connection lost just before leaving
        w.C.LeaveToMenu(w.Now);
        Expect(w.C.PendingFile != null && w.H.Book.Rec.CountOf(Flashlight) == 0, "pending kept on the client, host unchanged");
        var file = w.C.PendingFile;
        var c2 = new ClientSim("cli-2", file);                    // client process restarted
        c2.Prefs[Lockpick] = 9; c2.PrefMoney = 99999;             // sandbox reseeded from Player 1's save
        w.Attach(c2); w.Connect(); c2.LoadScene();
        w.Until(() => c2.Sync.Ready, 5, "ready after restart");
        Expect(c2.Sync.PendingRecovered == 1 && c2.Live[Flashlight] == 1 && c2.Money == 465 && !c2.Live.ContainsKey(Lockpick), "pending committed first, then restored");
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 1 && c2.PendingFile == null, "host committed once; pending file gone");
        var c3 = new ClientSim("cli-3", file);                    // the same old pending file again (e.g. copy left behind)
        w.Disconnect(); w.Attach(c3); w.Connect(); c3.LoadScene();
        w.Until(() => c3.Sync.Ready, 5, "ready");
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 1 && w.H.Book.Rec.Money == 465, "replayed pending never applied twice (" + w.H.Book.Rec + ")");
    }

    static void ExitDuringFlight()
    {
        var w = Joined();
        w.HostPaused = true;
        w.C.Buy(Flashlight, 35);
        w.Until(() => w.C.Sync.InFlight, 3, "report in flight");
        w.C.Buy(Watch, 100);                                      // newer change while the first report is out
        w.C.LeaveToMenu(w.Now);
        Expect(w.C.Sync.PendingPrev != null, "pending chained to its in-flight predecessor");
        w.HostPaused = false;
        w.Step(5);
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 1 && w.H.Book.Rec.CountOf(Watch) == 1 && w.H.Book.Rec.Money == 365, "both committed in order: " + w.H.Book.Rec);
        // the same, but the connection drops before the predecessor's result arrives
        var v = Joined();
        v.HostPaused = true;
        v.C.Buy(Flashlight, 35);
        v.Until(() => v.C.Sync.InFlight, 3, "report in flight");
        v.H.Pump(v.C.Instance, v.Now);                            // host commits the first report; its result is lost
        v.C.Buy(Watch, 100); v.C.LeaveToMenu(v.Now); v.Disconnect();
        v.HostPaused = false;
        var c2 = new ClientSim("cli-9", v.C.PendingFile); v.Attach(c2); v.Connect(); c2.LoadScene();
        v.Until(() => c2.Sync.Ready, 5, "ready");
        Expect(v.H.Book.Rec.CountOf(Flashlight) == 1 && v.H.Book.Rec.CountOf(Watch) == 1 && v.H.Book.Rec.Money == 365, "newer state committed after its predecessor: " + v.H.Book.Rec);
    }

    static void RestartBoth()
    {
        var w = Joined();
        w.C.Buy(Flashlight, 35); w.C.LeaveToMenu(w.Now); w.Step(3);
        var disk = w.H.Disk;
        var w2 = new World();                                     // both processes restarted
        w2.H.Disk = disk; w2.H.LoadSave(4711);
        w2.Connect(); w2.C.Prefs[Lockpick] = 9; w2.C.PrefMoney = 99999; w2.C.LoadScene();
        w2.Until(() => w2.C.Sync.Ready, 3, "ready after restart of both");
        Expect(w2.C.Live[Flashlight] == 1 && w2.C.Money == 465 && !w2.C.Live.ContainsKey(Lockpick), "committed data restored, not Player 1's copy");
    }

    static void CarBoxAfterReady()
    {
        var w = new World(); w.H.LoadSave(4711); w.Connect();
        w.C.LoadScene();
        Expect(!w.C.TryOpenCarBox(), "CarBox refused before readiness (guard kept)");
        w.Until(() => w.C.Sync.Ready, 3, "ready");
        w.C.Buy(Watch, 50); w.Step(25);
        Expect(w.C.TryOpenCarBox(), "open request sent after readiness");
        w.Step(2);
        Expect(w.C.Held != 0 && w.H.Wh.CarBoxState.Owner == 2, "client holds the exclusive lease");
        string why;
        Expect(w.H.Wh.CarBoxRequest(1, CarBoxOp.Open, 0, w.Now, out why) == ProposalCode.Busy, "host refused while the client holds it");
        Expect(w.C.TryTransfer(Watch, false), "deposit");
        w.Until(() => w.C.TxnPending == 0, 2, "deposit result");
        Expect(w.C.OtherResults[w.C.OtherResults.Count - 1].Value == ProposalCode.Accepted, "deposit accepted");
        w.C.ApplyAckedTransfer();
        Expect(w.H.Storage[Watch] == 1 && w.H.Book.Rec.CountOf(Watch) == 0 && w.C.Live[Watch] == 0, "item only in the CarBox");
        Expect(w.C.TryTransfer(Watch, true), "withdraw");
        w.Until(() => w.C.TxnPending == 0, 2, "withdraw result"); w.C.ApplyAckedTransfer();
        Expect(w.H.Storage[Watch] == 0 && w.H.Book.Rec.CountOf(Watch) == 1 && w.C.Live[Watch] == 1, "item back in the backpack only");
        w.Step(30);
        Expect(w.C.Sync.Ready && w.C.Sync.Restores == 1 && w.H.Book.Rec.CountOf(Watch) == 1, "no resync / report conflict after transfers");
        w.C.Propose(WorldKeys.CarBoxReq, 0, CarBoxRequest.Encode(CarBoxOp.Close, w.C.Held)); w.Step(2);
        Expect(w.H.Wh.CarBoxState.Owner == 0, "lock released");
        Expect(w.H.Wh.CarBoxRequest(1, CarBoxOp.Open, 0, w.Now, out why) == ProposalCode.Accepted, "host can open afterwards");
    }

    static void Blackbay()
    {
        Expect(BlackbayRules.ClientSaleAllowed(2, 2, false) && !BlackbayRules.ClientSaleAllowed(1, 2, false), "client: only items in its own backpack");
        Expect(!BlackbayRules.ClientSaleAllowed(5, 1, true), "client: never while the CarBox is open");
        Expect(BlackbayRules.HostSaleBlocked(2, 0, 0, 3, 1), "host: storage-only sale refused while Player 2 holds the CarBox");
        Expect(!BlackbayRules.HostSaleBlocked(2, 1, 0, 3, 1) && !BlackbayRules.HostSaleBlocked(2, 0, 1, 3, 1), "host: backpack / locker serve first (original order)");
        Expect(!BlackbayRules.HostSaleBlocked(0, 0, 0, 3, 1) && !BlackbayRules.HostSaleBlocked(1, 0, 0, 3, 1), "host: allowed when the CarBox is free or the host's own");
    }

    static void OldReportsRefused()
    {
        var w = Joined();
        var old = w.H.Book.Rec.Clone(); old.Set(Watch, 5); old.Money = 1;   // a report based on the first revision
        w.C.Buy(Flashlight, 35); w.Step(30);
        Expect(w.H.Book.AcceptReport(old) == ProposalCode.Stale && w.H.Book.Rec.CountOf(Flashlight) == 1, "old-revision report never overwrites a newer report");
        // transfer committed after a pending state was captured
        w.C.Buy(Watch, 50); w.Step(25); w.C.TryOpenCarBox(); w.Step(2);
        var pend = w.C.Record().Clone(); pend.Set(Watch, 7);                 // stale pending based on txn 0
        w.C.TryTransfer(Watch, false); w.Until(() => w.C.TxnPending == 0, 2, "deposit"); w.C.ApplyAckedTransfer();
        Expect(w.H.Book.AcceptReport(pend) == ProposalCode.Stale, "pending based on an older transfer state refused");
        Expect(w.H.Storage[Watch] == 1 && w.H.Book.Rec.CountOf(Watch) == 0, "committed transfer intact");
        var c2 = new ClientSim("cli-5", P2PendingCodec.Encode(pend, null));  // the same as a recovered pending file
        w.Disconnect(); w.Attach(c2); w.Connect(); c2.LoadScene();
        w.Until(() => c2.Sync.Ready, 5, "ready");
        Expect(c2.Sync.Superseded == 1 && w.H.Book.Rec.CountOf(Watch) == 0 && w.H.Storage[Watch] == 1 && !c2.Live.ContainsKey(Watch), "recovered stale pending discarded, no duplicate");
    }

    static void NewGameReset()
    {
        var w = Joined();
        w.C.Buy(Flashlight, 35); w.Step(30);
        w.C.Buy(Watch, 10); w.C.LeaveToMenu(w.Now); var oldPending = w.C.PendingFile; w.Disconnect();
        w.H.Disk = null; w.H.LoadSave(999);                         // host: new game (new save history, new record id)
        var c2 = new ClientSim("cli-6", oldPending); w.Attach(c2); w.Connect(); c2.LoadScene();
        w.Until(() => c2.Sync.Ready, 5, "ready on the new record");
        Expect(c2.Live.Count == 0 && c2.Money == 500 && w.H.Book.Rec.Id == 999 && w.H.Book.Rec.Items.Count == 0, "new game: original new-game Player 2");
        Expect(c2.Sync.Superseded == 1, "pending of the old history discarded");
        // same scene: host switches history while the client stays connected
        var v = Joined(); v.C.Buy(Flashlight, 35); v.Step(30);
        v.H.Disk = null; v.H.LoadSave(1234); v.Step(5);
        v.Until(() => v.C.Sync.Ready && v.C.Sync.Restores == 2, 5, "restored the replaced record");
        Expect(!v.C.Live.ContainsKey(Flashlight) && v.C.Money == 500, "client follows the host's new game");
    }

    static void WriteFailure()
    {
        var w = Joined();
        w.H.FailWrites = true;
        w.C.Buy(Flashlight, 35);
        w.Step(150);
        Expect(w.C.Sync.State == P2SyncState.Failed && w.H.Book.Rec.CountOf(Flashlight) == 0, "visible failure, nothing committed without the durable write");
        Expect(w.C.Sync.Reports <= 1 + P2ClientSync.MaxAttempts, "bounded attempts: " + w.C.Sync.Reports);
        Expect(!w.C.CarBoxReady, "CarBox blocked while failed");
        bool err = false; foreach (var l in w.C.Log) if (l.Contains("P2INV_FAIL")) err = true;
        Expect(err, "P2INV_FAIL logged");
        w.H.FailWrites = false;
        w.C.Travel(w.Now);                                          // next scene: capture + retry
        w.Until(() => w.C.Sync.Ready, 5, "recovered in the next scene");
        Expect(w.H.Book.Rec.CountOf(Flashlight) == 1 && w.C.Live[Flashlight] == 1, "purchase committed after recovery");
    }

    static void Travel()
    {
        var w = Joined();
        w.C.Buy(Flashlight, 35);
        w.C.Travel(w.Now);
        w.Until(() => w.C.Sync.Ready && w.C.Sync.Restores == 2, 3, "ready in the new scene");
        Expect(w.C.Live[Flashlight] == 1 && w.C.Money == 465 && w.H.Book.Rec.CountOf(Flashlight) == 1, "travel keeps and commits the purchase");
    }

    static void Cost()
    {
        var w = Joined();
        w.Step(10);
        long m0 = w.L.Msgs, b0 = w.L.Bytes;
        w.Step(300);                                                // 30 s unchanged
        Expect(w.L.Msgs == m0, "no traffic while unchanged (" + (w.L.Msgs - m0) + " msgs)");
        w.C.Buy(Flashlight, 35); w.Step(30);
        long msgs = w.L.Msgs - m0, bytes = w.L.Bytes - b0;
        Expect(msgs == 3, "one purchase = report + record delta + result (" + msgs + ")");
        Console.WriteLine("      one purchase: " + msgs + " messages, " + bytes + " bytes on the reliable channel; empty record " + P2Inventory.HeaderV2 + " B");
        var sw = System.Diagnostics.Stopwatch.StartNew(); long g = GC.CollectionCount(0);
        var sync = new P2ClientSync(); var rec = P2Inventory.Initial(false); rec.Id = 1;
        for (int i = 0; i < 100000; i++) sync.Tick(i * 0.001, true, rec, false, false, false);   // per-frame tick, scene not ready
        Console.WriteLine("      offline (.NET Framework, not Unity): P2ClientSync.Tick " + (sw.Elapsed.TotalMilliseconds * 1000 / 100000).ToString("0.000") + " us/frame, gen0 GCs " + (GC.CollectionCount(0) - g));
    }
}
