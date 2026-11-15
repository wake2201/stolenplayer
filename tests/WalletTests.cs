// W4.1B offline tests: ONE host-owned shared money balance + shared world money / unique valuables.
// Integrated model: a host (WorldHost + P2Book + the host's PlayerScript.moneyAm + FBPP save memory/disk + p2inv file) and a
// client (client StateRegistry + P2ClientSync + ClientWallet mirror + its own moneyAm/backpack) exchange REAL protocol
// messages (StateMessages codecs, proposal gate, registry snapshot/deltas) over an in-memory link. The host steps mirror
// Player2Sync (HostReport / ApplyWallet / BindWallet / HostMoneyTake / OnMoneyKeyWritten / SavePostfix) and WorldLootSync
// (HostDecide); the client steps mirror WalletSync (Observe / Mirror / Buy postfix) and Player2Sync (Apply / Capture).
// Unity, Harmony and the original game methods run only in game (manual QA, docs/TESTING.md §33).
using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib.Utils;
using TS2MP.Shared;

static class WalletTests
{
    static int _passed, _failed;
    const int Ns = 99;                      // MadisonSt
    const int Lockpick = 12, Vase = 57;     // item types
    const string Mark = WalletRules.MarkKey;

    static int Main()
    {
        Run("01 Example A: Player 1 spends 2000 of 10000 -> both see 8000", P1Spends);
        Run("02 Example C: Player 2 buys an item for 1000 -> deducted exactly once (duplicate report Unchanged), item in P2's record", P2Spends);
        Run("03 Player 1 earns (world money on the host) -> Player 2's mirror shows it", P1Earns);
        Run("04 Example B: Player 2 collects $500 world money -> +500 once for both, object taken, Player 1 cannot collect it", P2CollectsMoney);
        Run("05 Player 2 earns by selling its own item -> shared balance + sale, item gone (one snapshot)", P2Sells);
        Run("06 simultaneous purchases: host wins, client purchase rolled back (item AND money), never overspent", SimultaneousSpend);
        Run("07 simultaneous pickup of the same money object: P1 pending -> P2 Busy, then Stale; credited once", SimultaneousMoneyPickup);
        Run("08 duplicate pickup requests (same txn) -> Unchanged, money credited once", DuplicatePickup);
        Run("09 duplicate acknowledgments / retransmitted reports never apply twice; older report refused", DuplicateAcks);
        Run("10 interrupted pickups: P1 claim released (no money); P2 coroutine not run -> credited once, run -> counted once", Interrupted);
        Run("11 valuables: ordinary respawn, unique (destroy_if_was_picked) never again, key excluded", Valuables);
        Run("12 reconnection: lost report kept pending and committed once; lost result resolved from the record", Reconnect);
        Run("13 checkpoint rollback: P1's unsaved spend rolls back, P2's durable purchase and money stay; idempotent", CheckpointRollback);
        Run("14 host save + restart: p2inv v5 journal replayed above the watermark, compacted after the save", HostRestart);
        Run("15 migration: P2's old personal balance is never added or restored; shared balance = host money", Migration);
        Run("16 insufficient funds: purchase refused, record restored, nothing deducted", InsufficientFunds);
        Run("17 host scene loading: delta journaled and applied at bind; purchase uses the last bound balance", HostLoading);
        Run("18 penalties keep original semantics (may go below 0); host save clamps; client clamp/rent/hidden item reverted", Penalties);
        Run("19 unsupported: missions/jobs/pickpocket excluded, host/world purchases refused, special / story / heavy loot excluded", Unsupported);
        Run("20 codecs: wallet entry, record v3 (+v2 read), p2inv v5 (+v4 read), corrupt wallet journal rejected, fuzz", Codecs);
        Run("21 WorldHost: 'wallet' is host-committed only (client proposal NotAuthorized), registry entry readable", HostOnlyKey);
        Run("23 refused report right before a money pickup: the restored record covers it, add_money not counted twice", RefusedThenPickup);
        Run("22 performance guard: no scene scans / UI text in the wallet code, bounded journal", SourceScan);
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

    // ------------------------------------------------------------------------------------------------ link
    sealed class Link
    {
        public Queue<byte[]> ToHost = new Queue<byte[]>(), ToClient = new Queue<byte[]>();
        public bool Up = true, DuplicateResults;
        public void SendToClient(NetDataWriter w) { if (Up) ToClient.Enqueue(w.CopyData()); }
        public void SendToHost(NetDataWriter w) { if (Up) ToHost.Enqueue(w.CopyData()); }
        public void Cut() { Up = false; ToHost.Clear(); ToClient.Clear(); }
    }

    sealed class Store : IKeyStore
    {
        public Dictionary<string, int> D = new Dictionary<string, int>();
        public int Get(string k) { int v; return D.TryGetValue(k, out v) ? v : 0; }
        public void Set(string k, int v) { D[k] = v; }
    }

    // ------------------------------------------------------------------------------------------------ host
    sealed class Host
    {
        public WorldHost Wh = new WorldHost(0x7A11E7);
        public P2Book Book; public byte[] File; public bool FailWrites;
        public int Money;                                   // PlayerScript.moneyAm on the host = the shared balance
        public bool Bound = true; long _unapplied; int _lastBal; uint _pub;
        public Store Mem = new Store(); public Dictionary<string, int> Disk;
        public LootCatalog Cat = new LootCatalog(Ns); public int Days = 10;
        public Link L; readonly NetDataWriter _w = new NetDataWriter();
        public List<string> Log = new List<string>();

        public Host(int money, P2Inventory rec = null)
        {
            Wh.P2ReportHook = Report; Wh.LootTxnHook = DecideLoot;
            Wh.SetPlayerRegion(1, Ns, 0); Wh.SetPlayerRegion(2, Ns, 0);
            Book = new P2Book(rec ?? P2Inventory.Initial(false), 0) { HistoryId = 5 }; Book.Rec.Id = 5; if (Book.Rec.Rev == 0) Book.Rec.Rev = 1;
            Cat.Add(0x5001, "15$_z$udqeo", 0, LootFlags.Money | LootFlags.NoXp, null, 0, 500);
            Cat.Add(0x5002, "20$_q$yreky", 0, LootFlags.Money, null, 0, 20);
            Cat.Add(0x6001, "vase_102_2", Vase, LootFlags.DestroyIfPicked, null, 0);
            Cat.Add(0x6002, "Lamp_Kitchen", Vase, LootFlags.None, null, 0);
            Cat.Add(0x6003, "Key_102_Porch", 62, LootFlags.DestroyIfPicked, null, 0);
            Cat.Seal(null); Cat.Digest = 0xD16E57;
            Money = money; Mem.Set("Money", money);
            File = Book.Serialize(0); GameSave(); Bind();
        }

        public static ItemFacts Facts(int item) { return item == 62 ? new ItemFacts { Known = true, Key = true, Value = 50, AddXp = 5 } : new ItemFacts { Known = true, Value = 150f, AddXp = 20f }; }
        bool Write() { if (FailWrites) return false; File = Book.Serialize(0); return true; }

        // Player2Sync.HostBalance
        bool Balance(out long b) { b = Bound ? Money : (long)_lastBal + _unapplied; return true; }

        // Player2Sync.HostReport
        ProposalCode Report(P2Inventory rep)
        {
            var before = Book.Mark(); long bal; bool known = Balance(out bal);
            int delta; uint seq; string why;
            var code = Book.AcceptReport(rep, bal, known, out delta, out seq, out why);
            if (code == ProposalCode.Accepted)
            {
                if (!Write()) { Book.Restore(before); return ProposalCode.Busy; }
                if (delta != 0) ApplyWallet(seq, delta);
                Wh.CommitP2(Book.Rec);
            }
            Log.Add("report " + rep + " -> " + code + (why != null ? " (" + why + ")" : ""));
            return code;
        }

        // Player2Sync.ApplyWallet / PublishWallet
        void ApplyWallet(uint seq, int delta)
        {
            if (Bound) { Money = WalletRules.Apply(Money, delta); Book.WalletApplied = seq; }
            else _unapplied += delta;
            Publish();
        }
        public void Publish()
        {
            if (!Bound) return;
            _lastBal = Money; _unapplied = 0;
            Wh.CommitWallet(new WalletState { RecId = Book.Rec.Id, Balance = Money, P2Wallet = Book.Rec.Wallet, Seq = ++_pub });
        }

        // SaveGame.SaveGameNewSystem: damages, clamp (IL_036b-0374), SPrefs "Money" (SetInt prefix -> watermark), FBPP disk write
        public void GameSave(int damages = 0)
        {
            Money -= damages; if (Money < 0) Money = 0;
            Mem.Set("Money", Money);
            if (Bound) Mem.Set(Mark, (int)Book.WalletApplied);           // Player2Sync.OnMoneyKeyWritten
            int saving = Mem.Get(Mark);                                  // SavePrefix
            Disk = new Dictionary<string, int>(Mem.D);                   // FBPP.Save
            Book.WalletSaved((uint)saving); File = Book.Serialize(0);    // SavePostfix + queued write
            Publish();
        }

        // Quit without saving (or a checkpoint load) -> SaveGame.LoadGame (moneyAm = "Money") -> loadedMap -> HostBind/BindWallet
        public void Reload(bool restartProcess)
        {
            Bound = false;
            Mem = new Store { D = new Dictionary<string, int>(Disk) };
            Money = Mem.Get("Money");
            if (restartProcess) { P2Book b; string err; Expect(P2Book.TryDeserialize(File, 0, out b, out err), "p2inv file loads after restart: " + err); Book = b; }
            Bind();
        }
        public void Unbind() { Bound = false; }   // scene unloading / next scene not loaded yet
        public void Bind()
        {
            uint mark = (uint)Mem.Get(Mark);
            if (mark > Book.WalletSeq) Book.WalletSeq = mark;
            uint max; var l = Book.ReplayWallet(mark, out max);
            foreach (var e in l) Money = WalletRules.Apply(Money, e.Delta);
            Book.WalletApplied = max; Bound = true;
            Wh.CommitP2(Book.Rec); Publish(); Flush();
        }

        // ---- Player 1 (host) original actions
        public void P1Spend(int price) { Money -= price; Publish(); Flush(); }
        public bool P1Claim(uint hash, int token) { string why; return Wh.Loot.Claim(Ns, hash, token, out why); }
        public void P1OriginalCompletes(uint hash, string name, int addMoney)
        {
            Money += addMoney;                                                       // <PickingItem> IL_034d
            Mem.Set(LootKeys.Day(name), Days + 5); Mem.Set(LootKeys.Picked(name), 1); // SetDayRespawn (money 5) + Item_<name>
            Wh.Loot.Commit(Ns, hash, Wh.Reg, Wh.Outbox); Publish(); Flush();
        }

        // ---- WorldLootSync.HostDecide + Player2Sync.HostMoneyTake / HostLootTake
        ProposalCode DecideLoot(byte slot, LootTxnRequest t, bool inRegion, out string why)
        {
            LootEntry e = null; var facts = new ItemFacts(); bool persist = false;
            if (Cat.TryGet(t.Hash, out e))
            {
                facts = (e.Flags & LootFlags.Money) != 0 ? new ItemFacts() : Facts(e.ItemId);
                string ig; var c0 = LootRules.Classify(e, facts, out ig);
                persist = LootRules.PersistentlyTaken(c0, Mem.Get(LootKeys.Day(e.Name)), Mem.Get(LootKeys.Picked(e.Name)), Days);
            }
            LootClass cls;
            var code = LootDecision.DecideP2(t.Txn, Book.Rec.AppliedTxn, inRegion, Cat, t.Digest, t.Hash, t.ItemId, facts, Wh.Loot.StateOf(t.Ns, t.Hash), Wh.Loot.HasRoom(t.Ns, t.Hash), Book.Rec.CountOf(t.ItemId), persist, out cls, out why);
            if (code != ProposalCode.Accepted)
            {
                if (persist && code == ProposalCode.Stale && Wh.Loot.StateOf(t.Ns, t.Hash) == LootState.Available) Wh.Loot.Commit(t.Ns, t.Hash, Wh.Reg, Wh.Outbox);
                return code;
            }
            bool money = cls == LootClass.Money;
            var take = new LootTake { Ns = t.Ns, Hash = t.Hash, ItemId = e.ItemId, Day = Days + LootRules.RespawnDays(facts.Value, money), ItemFlag = true, Name = e.Name };
            var mark = Book.Mark(); uint wseq = 0;
            code = money ? Book.MoneyTxn(t.Txn, e.Money, out wseq) : Book.ItemTxn(t.Txn, e.ItemId, +1);
            if (code != ProposalCode.Accepted) return code;
            uint seq = Book.NextHostSeq();
            if (!Book.AddWorldTake(seq, take) || !Write()) { Book.Restore(mark); why = "durable write failed"; return ProposalCode.Busy; }
            LootKeys.Apply(Mem, take); Mem.Set("TS2MP_HostTxn", (int)seq);
            if (money) ApplyWallet(wseq, e.Money);
            Wh.CommitP2(Book.Rec);
            return ProposalCode.Accepted;
        }

        public void Connected() { Flush(); _w.Reset(); StateMessages.WriteSnapshot(_w, Wh.Reg); L.SendToClient(_w); }
        public void Flush() { foreach (var e in Wh.Outbox) { _w.Reset(); StateMessages.WriteDelta(_w, Wh.Reg.Epoch, e); if (L != null) L.SendToClient(_w); } Wh.Outbox.Clear(); }
        public void Pump(double now)
        {
            while (L.ToHost.Count > 0)
            {
                var r = new NetDataReader(L.ToHost.Dequeue());
                var type = (MsgType)r.GetByte();
                if (type == MsgType.StateProposal)
                {
                    uint id; StateEntry p; Expect(StateMessages.TryReadProposal(r, out id, out p), "proposal codec");
                    uint rev; int sr; float sh;
                    var code = Wh.HandleProposal("client", 2, id, p, now, out rev, out sr, out sh);
                    Flush();
                    _w.Reset(); StateMessages.WriteResult(_w, id, code, rev); L.SendToClient(_w);
                    if (L.DuplicateResults) L.SendToClient(_w);
                }
                else if (type == MsgType.StateResyncRequest) { Flush(); _w.Reset(); StateMessages.WriteSnapshot(_w, Wh.Reg); L.SendToClient(_w); }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------ client
    sealed class Client
    {
        public StateRegistry Reg = StateRegistry.CreateClient();
        public P2ClientSync Sync = new P2ClientSync();
        public ClientWallet W = new ClientWallet();
        public int Money;                                   // the client's PlayerScript.moneyAm (sandbox)
        public Dictionary<int, int> Live = new Dictionary<int, int>();
        public P2Inventory Restored; public bool Connected = true, Scene = true;
        public Link L; uint _nextId = 1; double _now; readonly NetDataWriter _w = new NetDataWriter();
        public uint LootPending; public ProposalCode LootResult; public List<string> Log = new List<string>();
        // WorldLootSync await after an accepted pickup (TxnPending blocks P2ClientSync.Tick until the coroutine finished)
        public bool Await; public uint AwaitTxn; public int AwaitMoney, AwaitItem; public bool AwaitCovered; int _reqMoney, _reqItem; uint _reqTxn;

        public Client(int sandboxMoney)
        {
            Money = sandboxMoney;
            Sync.Capture = Capture;
            Sync.Apply = rec =>
            {
                if (!Scene) return false;
                Live.Clear(); foreach (var kv in rec.Items) Live[kv.Key] = kv.Value;
                Restored = rec;
                W.Restore(rec.Id, rec.Wallet, rec.Spend, Money);   // WalletSync.ClientRestore
                Mirror();
                // WorldLootSync.OnRecordRestored: the record already contains the awaited money pickup
                if (Await && AwaitMoney > 0 && !AwaitCovered && rec.AppliedTxn >= AwaitTxn) { W.ArmExclusion(AwaitMoney, _now, 3.0); AwaitCovered = true; }
                return true;
            };
            Sync.Send = s => { byte[] d; if (!Connected || !s.TryEncode(out d)) return 0; return Propose(WorldKeys.P2Report, 2, d); };
            Sync.Persist = (p, prev) => { };
            Sync.RequestResync = () => { _w.Reset(); StateMessages.WriteResync(_w, Reg.Epoch, Reg.HeadRev); L.SendToHost(_w); };
            Sync.Info = m => Log.Add(m); Sync.Warn = m => Log.Add("WARN " + m); Sync.Error = m => Log.Add("ERROR " + m);
        }

        // Player2Sync.Capture (+ WalletSync.ClientCapture)
        P2Inventory Capture()
        {
            if (!Scene) return null;
            Observe();
            var s = new P2Inventory();
            foreach (var kv in Live) if (kv.Value > 0) s.Items[kv.Key] = kv.Value;
            s.Money = Restored != null ? Restored.Money : 0; s.Bank = Restored != null ? Restored.Bank : 0;
            s.Wallet = W.Active ? W.Local : (Restored != null ? Restored.Wallet : 0);
            s.Spend = W.Active ? W.Spend : (Restored != null ? Restored.Spend : 0);
            return s;
        }

        public void Observe() { Money = W.Observe(Money, _now); }
        public bool State(out WalletState s) { s = default(WalletState); var e = Reg.HasSnapshot ? Reg.Get(StateScope.Session, 0, WorldKeys.Wallet) : null; return e != null && WalletState.TryParse(e.Value, out s); }
        public void Mirror() { WalletState s; int d; if (State(out s) && W.Target(s, out d) && Money != d) { Money = d; W.Wrote(d); } }

        public uint Propose(string key, int arg, byte[] v)
        {
            uint id = _nextId++;
            _w.Reset(); StateMessages.WriteProposal(_w, id, new StateEntry { Scope = StateScope.Session, ScopeArg = arg, Key = key, Value = v }); L.SendToHost(_w);
            return id;
        }
        public P2Inventory Record() { if (!Connected || !Reg.HasSnapshot) return null; var e = Reg.Get(StateScope.Session, 2, WorldKeys.P2Inv); P2Inventory r; return e != null && P2Inventory.TryParse(e.Value, out r) ? r : null; }

        // WalletSync.Update + Player2Sync.ClientFrame
        public void Frame(double now) { _now = now; Pump(); if (Scene && W.Active) { Observe(); Mirror(); } Sync.Tick(now, Connected && Reg.HasSnapshot, Record(), false, Scene, LootPending != 0 || Await); }

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
                    if (id == LootPending)
                    {
                        LootPending = 0; LootResult = code;
                        if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged) { Await = true; AwaitTxn = _reqTxn; AwaitMoney = _reqMoney; AwaitItem = _reqItem; AwaitCovered = false; }
                    }
                }
            }
        }

        // ---- original game actions on the client
        // Shop_Manager.Buy_Item: item +1, moneyAm -= price (no balance check inside); WalletSync.BuyPostfix: Observe, Purchase, ReportNow
        public void Buy(int item, int price)
        {
            int c; Live.TryGetValue(item, out c); Live[item] = c + 1; Money -= price;
            Observe(); W.Purchase(price); Sync.PrepareTransfer(_now, Record());
        }
        public void Sell(int item, int price) { Live[item] = Live[item] - 1; Money += price; }   // Inventory.Sell_Item_NewSystem
        public void Penalty(int cost) { Money -= cost; }                                       // e.g. CarController.OnCollisionEnter
        // WorldLootSync.ClientPick (PrepareTransfer first, then lootTxn)
        public bool RequestLoot(uint hash, int itemId, int money)
        {
            if (!Sync.PrepareTransfer(_now, Record())) return false;
            var req = new LootTxnRequest { Txn = Sync.Applied + 1, Ns = Ns, Hash = hash, ItemId = itemId, Digest = 0xD16E57 };
            _reqTxn = req.Txn; _reqMoney = money; _reqItem = itemId;
            LootPending = Propose(WorldKeys.LootTxn, 0, req.Encode()); LootResult = (ProposalCode)255;
            return true;
        }
        public bool RequestLoot(uint hash, int itemId) { return RequestLoot(hash, itemId, 0); }

        // the resumed original coroutine finished (ran = its final step: add_money / Item_Picked + Destroy), or could not run
        // (WorldLootSync.FinishMoney credits once unless the restored record already covered it)
        public void CompleteLoot(bool ran)
        {
            if (!Await) return;
            if (AwaitMoney > 0) { if (ran || !AwaitCovered) { Money += AwaitMoney; Observe(); } }
            else { int c; Live.TryGetValue(AwaitItem, out c); Live[AwaitItem] = c + 1; }
            if (Sync.Applied < AwaitTxn) Sync.Applied = AwaitTxn;
            Await = false;
        }
    }

    sealed class World
    {
        public Host H; public Client C; public Link L = new Link(); public double T = 1;
        public World(int hostMoney, P2Inventory rec = null, int sandboxMoney = -1)
        {
            H = new Host(hostMoney, rec) { L = L };
            C = new Client(sandboxMoney >= 0 ? sandboxMoney : hostMoney) { L = L };
            H.Connected(); Step(30);
            Expect(C.Sync.Ready && C.W.Active, "client restored + acknowledged (" + C.Sync.State + ")");
        }
        public void Step(int n = 1) { for (int i = 0; i < n; i++) { C.Frame(T); H.Pump(T); H.Flush(); C.Pump(); T += 0.25; } }
        public void Settle() { Step(40); }
        public int Display { get { return C.Money; } }
    }

    static int Count(Dictionary<int, int> d, int item) { int c; return d.TryGetValue(item, out c) ? c : 0; }

    // ------------------------------------------------------------------------------------------------ cases
    static void P1Spends()
    {
        var w = new World(10000);
        Expect(w.Display == 10000, "client shows the shared balance");
        w.H.P1Spend(2000); w.Settle();
        Expect(w.H.Money == 8000 && w.Display == 8000, "both 8000 (host " + w.H.Money + ", client " + w.Display + ")");
        Expect(w.H.Book.Rec.Wallet == 0 && w.H.Book.WalletLog.Count == 0, "a host-side change is not a Player-2 delta");
    }

    static void P2Spends()
    {
        var w = new World(10000);
        w.C.Buy(Lockpick, 1000); w.Settle();
        Expect(w.H.Money == 9000 && w.Display == 9000, "deducted once: host " + w.H.Money + ", client " + w.Display);
        Expect(w.H.Book.Rec.CountOf(Lockpick) == 1 && w.H.Book.Rec.Wallet == -1000 && w.H.Book.Rec.Spend == 1000, "item in P2's record, counters " + w.H.Book.Rec);
        Expect(w.H.Book.WalletLog.Count == 1 && w.H.Book.WalletLog[0].Delta == -1000, "one journaled delta");
        // a retransmitted copy of the same report (lost acknowledgment) -> Unchanged, no second deduction
        byte[] d; var rep = w.H.Book.Rec.Clone(); rep.TryEncode(out d);
        w.C.Propose(WorldKeys.P2Report, 2, d); w.Settle();
        Expect(w.H.Money == 9000 && w.H.Book.WalletLog.Count == 1, "duplicate report never deducts twice");
        w.C.Buy(Lockpick, 1000); w.Settle();
        Expect(w.H.Money == 8000 && w.Display == 8000 && w.H.Book.Rec.CountOf(Lockpick) == 2, "second purchase");
    }

    static void P1Earns()
    {
        var w = new World(3000);
        Expect(w.H.P1Claim(0x5001, 1), "P1 claim");
        w.H.P1OriginalCompletes(0x5001, "15$_z$udqeo", 500); w.Settle();
        Expect(w.H.Money == 3500 && w.Display == 3500, "P2 sees P1's pickup: " + w.Display);
        Expect(w.H.Wh.Loot.StateOf(Ns, 0x5001) == LootState.Taken, "taken for both");
    }

    static void P2CollectsMoney()
    {
        var w = new World(8000);
        Expect(w.C.RequestLoot(0x5001, 0, 500), "P2 pickup requested");
        w.Settle();
        Expect(w.C.LootResult == ProposalCode.Accepted, "host accepted: " + w.C.LootResult);
        Expect(w.H.Money == 8500, "host shared balance +500 exactly once: " + w.H.Money);
        // client: the original coroutine adds add_money (resumed after the acknowledgment); the mirror counts it
        w.C.CompleteLoot(true); w.Settle();
        Expect(w.Display == 8500 && w.H.Money == 8500, "both 8500: client " + w.Display + ", host " + w.H.Money);
        Expect(w.C.W.Local == w.H.Book.Rec.Wallet && w.H.Book.Rec.Wallet == 500, "client counter equals the host-credited record counter (no second credit by the next report)");
        Expect(w.H.Wh.Loot.StateOf(Ns, 0x5001) == LootState.Taken && w.H.Mem.Get("ItemDay_15$_z$udqeo") == w.H.Days + 5 && w.H.Mem.Get("Item_15$_z$udqeo") == 1, "object taken + original keys (5 days, Item_<name>)");
        Expect(!w.H.P1Claim(0x5001, 7), "Player 1 cannot collect it again");
        Expect(w.H.Book.World.Count == 1 && w.H.Book.WalletLog.Count == 1, "world take + wallet entry in the same durable book");
    }

    static void P2Sells()
    {
        var rec = P2Inventory.Initial(false); rec.Set(Vase, 1);
        var w = new World(1000, rec);
        Expect(Count(w.C.Live, Vase) == 1, "P2 owns a vase");
        w.C.Sell(Vase, 300); w.Settle();
        Expect(w.H.Money == 1300 && w.Display == 1300 && w.H.Book.Rec.CountOf(Vase) == 0 && w.H.Book.Rec.Spend == 0, "sale: +300 shared, item gone, no purchase counter");
    }

    static void SimultaneousSpend()
    {
        var w = new World(1000);
        w.C.Buy(Lockpick, 800);                    // report in flight (mirror still showed 1000)
        w.H.P1Spend(800);                          // host purchase lands first (host is the authority)
        w.Settle();
        Expect(w.H.Money == 200 && w.H.Money >= 0, "host balance 200, never negative: " + w.H.Money);
        Expect(w.H.Book.Rec.CountOf(Lockpick) == 0 && w.H.Book.Rec.Spend == 0 && w.H.Book.WalletRefused == 1, "client purchase refused (" + w.H.Book.Rec + ")");
        Expect(Count(w.C.Live, Lockpick) == 0 && w.Display == 200, "client restored: item gone, money 200 (client " + w.Display + ")");
        Expect(w.C.Sync.Ready, "client synchronized again: " + w.C.Sync.State);
        w.C.Buy(Lockpick, 150); w.Settle();
        Expect(w.H.Money == 50 && w.Display == 50 && w.H.Book.Rec.CountOf(Lockpick) == 1, "an affordable purchase afterwards works");
    }

    static void SimultaneousMoneyPickup()
    {
        var w = new World(1000);
        Expect(w.H.P1Claim(0x5001, 3), "P1 pickup running (pending)");
        w.C.RequestLoot(0x5001, 0, 500); w.Settle();
        Expect(w.C.LootResult == ProposalCode.Busy && w.H.Money == 1000, "P2 Busy while P1's pickup runs");
        w.H.P1OriginalCompletes(0x5001, "15$_z$udqeo", 500); w.Settle();
        w.C.RequestLoot(0x5001, 0, 500); w.Settle();
        Expect(w.C.LootResult == ProposalCode.Stale, "then Stale (already taken): " + w.C.LootResult);
        Expect(w.H.Money == 1500 && w.Display == 1500 && w.H.Book.Rec.Wallet == 0, "credited exactly once (to the shared balance)");
        // other order: P2 first, P1 refused
        Expect(w.C.RequestLoot(0x5002, 0, 20), "P2 requests the 20$"); w.Settle();
        Expect(w.C.LootResult == ProposalCode.Accepted && !w.H.P1Claim(0x5002, 9), "P2 first -> P1 refused");
        w.C.CompleteLoot(true); w.Settle();
        Expect(w.H.Money == 1520 && w.Display == 1520, "20 once");
    }

    static void DuplicatePickup()
    {
        var w = new World(0);
        w.C.RequestLoot(0x5001, 0, 500); w.Settle(); w.C.CompleteLoot(true); w.Settle();
        var req = new LootTxnRequest { Txn = w.H.Book.Rec.AppliedTxn, Ns = Ns, Hash = 0x5001, ItemId = 0, Digest = 0xD16E57 };
        uint id = w.C.Propose(WorldKeys.LootTxn, 0, req.Encode()); w.C.LootPending = id; w.Settle(); w.C.Await = false;
        Expect(w.C.LootResult == ProposalCode.Unchanged && w.H.Money == 500 && w.H.Book.WalletLog.Count == 1 && w.Display == 500, "same txn again: Unchanged, money once (" + w.H.Money + ")");
        string why; Expect(w.H.Book.MoneyTxn(req.Txn, 500, out id) == ProposalCode.Unchanged, "book-level retransmission Unchanged");
        Expect(w.H.Book.MoneyTxn(req.Txn + 5, 500, out id) == ProposalCode.Stale, "out of order Stale");
        Expect(LootDecision.DecideP2(req.Txn, req.Txn, true, w.H.Cat, 0xD16E57, 0x5001, 0, new ItemFacts(), LootState.Taken, true, 0, out why) == ProposalCode.Unchanged, "decision answers a retransmission first");
    }

    static void DuplicateAcks()
    {
        var w = new World(5000);
        w.L.DuplicateResults = true;                // every result arrives twice
        w.C.Buy(Lockpick, 700); w.Settle();
        w.C.Sell(Lockpick, 100); w.Settle();
        Expect(w.H.Money == 4400 && w.Display == 4400 && w.H.Book.WalletLog.Count == 2, "duplicated results change nothing: host " + w.H.Money + ", client " + w.Display);
        w.L.DuplicateResults = false;
        var old = w.H.Book.Rec.Clone(); old.Rev -= 1; old.Wallet -= 999;
        int d; uint s; string why;
        Expect(w.H.Book.AcceptReport(old, w.H.Money, true, out d, out s, out why) == ProposalCode.Stale && d == 0, "an older report never overwrites the newer state");
        // failed durable write: Busy, book restored, money untouched; the retry commits it once
        w.H.FailWrites = true; w.C.Penalty(40); w.Step(10);
        Expect(w.H.Money == 4400 && w.H.Book.WalletLog.Count == 2 && w.H.Book.Rec.Wallet == -600, "failed write changes nothing: " + w.H.Money);
        w.H.FailWrites = false; w.Settle();
        Expect(w.H.Money == 4360 && w.Display == 4360 && w.H.Book.WalletLog.Count == 3, "retry after the failure applied once: " + w.H.Money);
    }

    static void Interrupted()
    {
        // P1: claim released before the irreversible step -> no money, object available again
        var w = new World(1000);
        Expect(w.H.P1Claim(0x5001, 4), "claim");
        Expect(w.H.Wh.Loot.Release(Ns, 0x5001, 4) && w.H.Money == 1000 && w.H.Wh.Loot.StateOf(Ns, 0x5001) == LootState.Available, "released, nothing granted");
        // P2: accepted, but the original coroutine could not run (object gone) -> WorldLootSync.FinishMoney credits once
        w.C.RequestLoot(0x5001, 0, 500); w.Settle();
        Expect(w.C.LootResult == ProposalCode.Accepted, "accepted");
        int before = w.C.Money; w.C.CompleteLoot(false); w.Settle();
        Expect(w.H.Money == 1500 && w.Display == 1500 && w.C.W.Local == w.H.Book.Rec.Wallet, "direct credit counted once (" + before + " -> " + w.Display + ")");
        // the counter equality is what prevents a second credit: one extra local credit WOULD be reported as +500 more
        var probe = w.H.Book.Rec.Clone(); probe.Wallet += 500; int dd; uint ss; string why;
        var b2 = new P2Book(w.H.Book.Rec.Clone(), 0);
        Expect(b2.AcceptReport(probe, 1500, true, out dd, out ss, out why) == ProposalCode.Accepted && dd == 500, "(guard) a double count would be visible as a delta - the model above produced none");
    }

    static void Valuables()
    {
        var w = new World(1000);
        LootEntry vase, lamp, key; w.H.Cat.TryGet(0x6001, out vase); w.H.Cat.TryGet(0x6002, out lamp); w.H.Cat.TryGet(0x6003, out key);
        string why;
        Expect(LootRules.Classify(vase, Host.Facts(Vase), out why) == LootClass.UniqueValuable, "destroy_if_was_picked = unique valuable");
        Expect(LootRules.Classify(lamp, Host.Facts(Vase), out why) == LootClass.Valuable, "ordinary valuable");
        Expect(LootRules.Classify(key, Host.Facts(62), out why) == LootClass.Excluded && why.Contains("key"), "a unique key (lock state) stays excluded: " + why);
        w.C.RequestLoot(0x6001, Vase); w.Settle();
        Expect(w.C.LootResult == ProposalCode.Accepted && w.H.Book.Rec.CountOf(Vase) == 1 && w.H.Mem.Get("Item_vase_102_2") == 1, "P2 takes the unique vase: record +1, Item_<name> = 1");
        Live(w, Vase, 1);
        w.C.RequestLoot(0x6002, Vase); w.Settle(); Live(w, Vase, 2);
        Expect(Count(w.C.Live, Vase) == 2, "client backpack 2 (no duplicate from the record restore)");
        // everyone leaves the region: live taken list cleared (original reload semantics)
        w.H.Wh.SetPlayerRegion(1, 0, w.T); w.H.Wh.SetPlayerRegion(2, 0, w.T); w.H.Wh.SetPlayerRegion(1, Ns, w.T); w.H.Wh.SetPlayerRegion(2, Ns, w.T); w.Settle();
        Expect(w.H.Wh.Loot.StateOf(Ns, 0x6001) == LootState.Available, "live list cleared");
        // days pass beyond both respawn days: the ordinary lamp respawns, the unique vase never does
        w.H.Days += 30;
        Expect(!LootRules.PersistentlyTaken(LootClass.Valuable, w.H.Mem.Get("ItemDay_Lamp_Kitchen"), w.H.Mem.Get("Item_Lamp_Kitchen"), w.H.Days), "ordinary valuable respawns after its day");
        Expect(LootRules.PersistentlyTaken(LootClass.UniqueValuable, w.H.Mem.Get("ItemDay_vase_102_2"), w.H.Mem.Get("Item_vase_102_2"), w.H.Days), "unique valuable stays gone (Item_<name> = 1)");
        w.C.RequestLoot(0x6001, Vase); w.Settle();
        Expect(w.C.LootResult == ProposalCode.Stale && w.H.Book.Rec.CountOf(Vase) == 2, "a second take of the unique vase is refused (no duplicate)");
        Expect(w.H.Wh.Loot.StateOf(Ns, 0x6001) == LootState.Taken, "and listed as taken so P2's copy is removed");
        Expect(LootRules.PersistentlyTaken(LootClass.Valuable, w.H.Days + 2, 0, w.H.Days), "ordinary valuable before its respawn day is still taken");
    }
    static void Live(World w, int item, int n) { w.C.CompleteLoot(true); w.Settle(); Expect(w.H.Book.Rec.CountOf(item) == n, "record " + n + " (" + w.H.Book.Rec + ")"); }

    static void Reconnect()
    {
        var w = new World(6000);
        // report lost with the connection: stays pending, committed exactly once after the reconnect
        w.C.Penalty(250); w.C.Observe();
        w.L.Cut(); w.C.Connected = false; w.Step(4);
        w.L.Up = true; w.C.Connected = true; w.C.Reg = StateRegistry.CreateClient(); w.H.Connected(); w.Settle();
        Expect(w.H.Money == 5750 && w.Display == 5750 && w.H.Book.WalletLog.Count == 1, "lost report committed once after reconnect: " + w.H.Money);
        // result lost: the host applied it, the client never heard -> the same cumulative counter is Unchanged, never applied twice
        w.C.Penalty(100); w.Step(12);                 // report sent and committed
        w.L.Cut(); w.C.Connected = false; w.Step(4);
        w.L.Up = true; w.C.Connected = true; w.C.Reg = StateRegistry.CreateClient(); w.H.Connected(); w.Settle();
        Expect(w.H.Money == 5650 && w.Display == 5650 && w.H.Book.WalletLog.Count == 2, "no double application after a lost result: " + w.H.Money);
    }

    static void CheckpointRollback()
    {
        var w = new World(10000);                   // saved at 10000
        w.H.P1Spend(2000);                          // Player 1, unsaved
        w.C.Buy(Lockpick, 1000); w.Settle();        // Player 2, durable (record + journal)
        Expect(w.H.Money == 7000 && w.Display == 7000, "live 7000");
        w.H.Reload(false); w.Settle();              // checkpoint: load the save from disk without saving
        Expect(w.H.Money == 9000 && w.Display == 9000, "P1's spend rolled back, P2's purchase stays paid: " + w.H.Money);
        Expect(w.H.Book.Rec.CountOf(Lockpick) == 1 && Count(w.C.Live, Lockpick) == 1, "P2 keeps the item");
        w.H.Reload(false); w.Settle();
        Expect(w.H.Money == 9000, "a second reload replays nothing twice: " + w.H.Money);
        w.H.GameSave(); Expect(w.H.Book.WalletLog.Count == 0 && w.H.Disk[Mark] == 1, "normal save: watermark 1, journal compacted");
        w.H.Reload(false); Expect(w.H.Money == 9000, "after the save: no replay");
    }

    static void HostRestart()
    {
        var w = new World(4000);
        w.C.Buy(Lockpick, 500); w.Settle();
        w.C.Penalty(70); w.Settle();
        Expect(w.H.Money == 3430 && w.H.Book.WalletLog.Count == 2, "two deltas journaled");
        Expect(w.H.File[8] == 5, "p2inv file v5");
        w.H.Reload(true); w.Settle();               // host process restarts, loads the save (4000) + the p2inv file
        Expect(w.H.Money == 3430 && w.Display == 3430, "replayed from the file after a restart: " + w.H.Money);
        w.H.GameSave(); w.H.Reload(true); w.Settle();
        Expect(w.H.Money == 3430 && w.H.Book.WalletLog.Count == 0 && w.H.Book.LastSavedWallet == 2, "saved, compacted, restart: same balance");
    }

    static void Migration()
    {
        var old = P2Inventory.Initial(false); old.Money = 77777; old.Bank = 1234; old.Rev = 3;
        // encode as a v2 record (0.14.x) and read it back: Wallet = Spend = 0
        var w2 = new NetDataWriter();
        w2.Put((byte)2); w2.Put(5); w2.Put(old.Rev); w2.Put(0u); w2.Put(old.Money); w2.Put(old.Bank); w2.Put(100); w2.Put(100); w2.Put((ushort)0);
        P2Inventory v2; Expect(P2Inventory.TryParse(w2.CopyData(), out v2) && v2.Wallet == 0 && v2.Spend == 0 && v2.Money == 77777, "v2 record readable, counters 0");
        var w = new World(10000, v2, 77777);        // client sandbox still had the old personal value
        Expect(w.Display == 10000, "client shows the shared balance, not 77777 / 87777: " + w.Display);
        Expect(w.H.Money == 10000 && w.H.Book.Rec.Money == 77777 && w.H.Book.Rec.Bank == 1234, "host balance unchanged, legacy fields untouched (dormant)");
        w.Settle();
        Expect(w.H.Money == 10000 && w.H.Book.WalletLog.Count == 0, "the acknowledgment report adds nothing");
        w.C.Penalty(10); w.Settle();
        Expect(w.H.Money == 9990 && w.H.Book.Rec.Money == 77777, "later changes go to the shared balance only");
    }

    static void InsufficientFunds()
    {
        var w = new World(300);
        w.C.Buy(Lockpick, 500); w.Settle();          // stale UI / mirror race: original deducts without a check
        Expect(w.H.Money == 300 && w.H.Book.Rec.CountOf(Lockpick) == 0 && w.H.Book.Rec.Spend == 0, "refused on the host: " + w.H.Money);
        Expect(w.Display == 300 && Count(w.C.Live, Lockpick) == 0 && w.C.Sync.Ready, "client restored: no item, no deduction");
        long b = 300; int d; string why; var rec = w.H.Book.Rec; var rep = rec.Clone(); rep.Wallet -= 300; rep.Spend += 300;
        Expect(WalletRules.DecideReport(rec, rep, b, true, out d, out why) == ProposalCode.Accepted, "exactly the balance is affordable");
        rep.Wallet -= 1; rep.Spend += 1;
        Expect(WalletRules.DecideReport(rec, rep, b, true, out d, out why) == ProposalCode.Stale && why.Contains("insufficient"), "one more is not");
        rep.Spend = rec.Spend - 1;
        Expect(WalletRules.DecideReport(rec, rep, b, true, out d, out why) == ProposalCode.Invalid, "purchase counter going backwards is invalid");
        rep = rec.Clone(); rep.Wallet += WalletRules.MaxDelta + 1;
        Expect(WalletRules.DecideReport(rec, rep, b, true, out d, out why) == ProposalCode.Invalid, "absurd delta invalid");
    }

    static void HostLoading()
    {
        var w = new World(2000);
        w.H.GameSave();                              // travel: SaveGameNewSystem first
        w.H.Unbind();                                // next scene loading
        w.C.Penalty(300); w.Settle();                // report arrives while unbound
        Expect(w.H.Money == 2000 && w.H.Book.WalletLog.Count == 1 && w.H.Book.WalletApplied == 0, "journaled only (not applied to the unloaded scene)");
        w.C.Buy(Lockpick, 1500); w.Settle();         // purchase while unbound: last bound balance 2000 - 300 covers it
        Expect(w.H.Book.Rec.CountOf(Lockpick) == 1, "purchase accepted against the last bound balance + journaled deltas");
        w.C.Buy(Lockpick, 400); w.Settle();          // 2000 - 300 - 1500 - 400 < 0 -> refused
        Expect(w.H.Book.Rec.CountOf(Lockpick) == 1, "overspend refused while unbound too");
        w.H.Reload(false); w.Settle();               // LoadGame (2000) + bind replays both deltas
        Expect(w.H.Money == 200 && w.Display == 200, "applied at bind: " + w.H.Money);
    }

    static void Penalties()
    {
        var w = new World(100);
        w.C.Penalty(250); w.Settle();                // repair cost: original semantics, no balance check
        Expect(w.H.Money == -150 && w.Display == -150, "penalty below zero applied like the original: " + w.H.Money);
        w.H.GameSave();
        Expect(w.H.Money == 0, "host save clamps like the original (IL_0374)");
        w.Settle(); Expect(w.Display == 0, "client mirrors the clamp: " + w.Display);
        // client-side reverts (WalletSync hooks), pure helpers
        Expect(WalletPolicy.ClientAfterSave(-50, 30, 0) == -80, "client save: damage kept, clamp undone");
        Expect(WalletPolicy.ClientAfterSave(500, 30, 470) == 470, "no clamp: unchanged");
        Expect(WalletPolicy.RentCharged(0, 168) && !WalletPolicy.RentCharged(5, 168) && WalletPolicy.RentAmount(false) == 150 && WalletPolicy.RentAmount(true) == 600, "rent branch detection + amounts (IL_0112/012b)");
        var cw = new ClientWallet(); cw.Restore(5, 0, 0, 1000);
        cw.ArmExclusion(100, 1, 2);
        Expect(cw.Observe(1100, 1.1) == 1000 && cw.Local == 0 && cw.Reverted == 100, "hidden item +100 reverted, not counted");
        Expect(cw.Observe(1050, 1.2) == 1050 && cw.Local == 50, "later change counted normally");
        cw.ArmExclusion(100, 2, 2);
        Expect(cw.Observe(1150, 10) == 1150 && cw.Local == 150, "an expired exclusion never swallows a real change");
        cw.Deactivate(); Expect(cw.Observe(99999, 11) == 99999 && cw.Local == 150, "inactive (scene load) never counts");
    }

    static void Unsupported()
    {
        int refuse = 0, exclude = 0, purchase = 0;
        var methods = new HashSet<string>();
        foreach (var m in WalletPolicy.Writers)
        {
            Expect(!string.IsNullOrEmpty(m.Evidence) && !string.IsNullOrEmpty(m.Reason), m.Type + "." + m.Method + " documented");
            methods.Add(m.Type + "." + m.Method);
            if (m.Policy == MoneyPolicy.Refuse) refuse++; else if (m.Policy == MoneyPolicy.Exclude) exclude++; else if (m.Policy == MoneyPolicy.Purchase) purchase++;
        }
        Expect(methods.Count == 40 + 1, "the 40 verified writer methods + HiddenItem.PickIt are classified (" + methods.Count + ")");
        foreach (var m in new[] { "Mission_Item.MissionComplete1", "JobOffer.ClaimRewards", "ContractUIObj.ClaimReward", "Payphone.Success", "Pickpocket_Manager.Success" })
            Expect(Policy(m) == MoneyPolicy.Exclude, m + " excluded (not shared yet)");
        foreach (var m in new[] { "Darkbank.DepositMoney", "Darkbank.WithdrawMoney", "StockManager.BuyShares", "Inventory.BuyGarage", "Whiplist.UpgradeEngine", "DroneUpgradePanel.BuyUpgrade", "HouseRenovator.BuyAddition", "Tutorial.BuyVan" })
            Expect(Policy(m) == MoneyPolicy.Refuse, m + " refused on the client");
        Expect(Policy("Shop_Manager.Buy_Item") == MoneyPolicy.Purchase && purchase == 1, "only the shop purchase is a checked purchase");
        // loot classes that stay unsupported
        string why;
        var flags = new[] { LootFlags.StoryPick, LootFlags.StoryName, LootFlags.Contract, LootFlags.Parcel, LootFlags.Heavy, LootFlags.RandomCabinet, LootFlags.DeleteIfPref, LootFlags.SharedName, LootFlags.NeverDestroy, LootFlags.NoDays, LootFlags.CantPick };
        foreach (var f in flags)
        {
            Expect(LootRules.Classify(new LootEntry { Name = "x", Flags = f }, new ItemFacts { Known = true, Value = 100, AddXp = 5 }, out why) == LootClass.Excluded, f + " item excluded");
            Expect(LootRules.Classify(new LootEntry { Name = "m", Flags = f | LootFlags.Money, Money = 50 }, new ItemFacts(), out why) == LootClass.Excluded, f + " money excluded");
            Expect(LootRules.Classify(new LootEntry { Name = "u", Flags = f | LootFlags.DestroyIfPicked }, new ItemFacts { Known = true, Value = 100, AddXp = 5 }, out why) == LootClass.Excluded, f + " unique excluded");
        }
        foreach (var fx in new[] { new ItemFacts { Known = true, Drone = true, Value = 1, AddXp = 5 }, new ItemFacts { Known = true, Special = true, Value = 1, AddXp = 5 }, new ItemFacts { Known = true, Mission = true, Value = 1, AddXp = 5 }, new ItemFacts { Known = true, Story = true, Value = 1, AddXp = 5 } })
            Expect(LootRules.Classify(new LootEntry { Name = "s", Flags = LootFlags.DestroyIfPicked }, fx, out why) == LootClass.Excluded, "special/story/mission/drone unique excluded: " + why);
        Expect(LootRules.Classify(new LootEntry { Name = "m", Flags = LootFlags.Money, Money = 0 }, new ItemFacts(), out why) == LootClass.Excluded, "money without amount excluded");
        Expect(LootRules.Classify(new LootEntry { Name = "m", Flags = LootFlags.Money | LootFlags.NoXp, Money = 15 }, new ItemFacts(), out why) == LootClass.Money, "money ignores NoXp (respawn key written for money)");
        var hill = new LootCatalog(97); hill.Add(1, "15$_a", 0, LootFlags.Money, null, 0, 15); hill.Seal(null); hill.Digest = 1; LootClass cls;
        Expect(LootDecision.DecideP2(1, 0, true, hill, 1, 1, 0, new ItemFacts(), LootState.Available, true, 0, false, out cls, out why) == ProposalCode.NotAuthorized, "money in an unsupported region: refused (client also refuses the pickup)");
    }
    static MoneyPolicy Policy(string tm) { foreach (var m in WalletPolicy.Writers) if (m.Type + "." + m.Method == tm && m.Policy != MoneyPolicy.Count) return m.Policy; foreach (var m in WalletPolicy.Writers) if (m.Type + "." + m.Method == tm) return m.Policy; throw new Exception("not in table: " + tm); }

    static void Codecs()
    {
        var s = new WalletState { RecId = 5, Balance = -42, P2Wallet = 1234567, Seq = 9 };
        WalletState b; Expect(WalletState.TryParse(s.ToBytes(), out b) && b.RecId == 5 && b.Balance == -42 && b.P2Wallet == 1234567 && b.Seq == 9 && s.ToBytes().Length == WalletState.WireSize, "wallet entry 16 B round trip");
        Expect(!WalletState.TryParse(new byte[15], out b) && !WalletState.TryParse(new byte[16], out b), "wrong size / record 0 refused");
        Expect(WalletRules.Display(new WalletState { RecId = 1, Balance = int.MaxValue, P2Wallet = -10 }, 10) == int.MaxValue, "display clamps to int");
        var r = P2Inventory.Initial(false); r.Id = 3; r.Wallet = -500; r.Spend = 700; r.Set(Lockpick, 2);
        byte[] d; Expect(r.TryEncode(out d) && d[0] == 3, "record v3");
        P2Inventory r2; Expect(P2Inventory.TryParse(d, out r2) && r2.SameContent(r) && r2.Wallet == -500 && r2.Spend == 700, "v3 round trip");
        var e = new P2Inventory(); e.TryEncode(out d); Expect(d.Length == P2Inventory.HeaderV3, "empty v3 record = header (" + d.Length + " B)");
        var full = new P2Inventory(); for (int i = 0; i < P2Inventory.MaxItems; i++) full.Set(i + 1, 5); full.TryEncode(out d);
        Expect(d.Length <= StateEntry.MaxValueBytes, "full record fits the registry (" + d.Length + " B)");
        // p2inv v5 with a wallet journal; v4 still readable
        var book = new P2Book(r, 0) { HistoryId = 3 }; int dd; uint seq; string why;
        var rep = r.Clone(); rep.Wallet -= 100; rep.Rev = r.Rev;
        Expect(book.AcceptReport(rep, 1000, true, out dd, out seq, out why) == ProposalCode.Accepted && seq == 1, "journaled");
        var f = book.Serialize(0); Expect(f[8] == 5, "v5");
        P2Book bk; string err; Expect(P2Book.TryDeserialize(f, 0, out bk, out err) && bk.WalletLog.Count == 1 && bk.WalletLog[0].Delta == -100 && bk.WalletSeq == 1 && bk.Rec.Wallet == -600, "v5 round trip: " + err);
        var v4 = V4Bytes(); Expect(P2Book.TryDeserialize(v4, 0, out bk, out err) && bk.WalletLog.Count == 0 && bk.Rec.Wallet == 0 && bk.Rec.Money == 640, "v4 file (0.14.x) loads, legacy money kept: " + err);
        // corrupt wallet entry (delta 0) must be rejected, not silently loaded
        var cw = new P2Book(r.Clone(), 0) { HistoryId = 3 }; cw.WalletSeq = 2; cw.WalletLog.Add(new WalletEntry { Seq = 1, Delta = 0 });
        Expect(!P2Book.TryDeserialize(cw.Serialize(0), 0, out bk, out err), "zero delta rejected");
        cw.WalletLog.Clear(); cw.WalletLog.Add(new WalletEntry { Seq = 3, Delta = 5 });
        Expect(!P2Book.TryDeserialize(cw.Serialize(0), 0, out bk, out err), "sequence above WalletSeq rejected");
        var rnd = new Random(7);
        for (int i = 0; i < 2000; i++) { var junk = new byte[rnd.Next(0, 64)]; rnd.NextBytes(junk); WalletState.TryParse(junk, out b); P2Inventory.TryParse(junk, out r2); P2Book.TryDeserialize(junk, 0, out bk, out err); }
    }

    // a p2inv v4 file as written by 0.14.x (record v2 inside, no wallet section)
    static byte[] V4Bytes()
    {
        var w = new NetDataWriter();
        w.Put((uint)0x32504D54); w.Put((byte)4); w.Put(0); w.Put(0u); w.Put(3); w.Put(false); w.Put(0u); w.Put(0u);
        w.Put((byte)2); w.Put(3); w.Put(1u); w.Put(0u); w.Put(640); w.Put(0); w.Put(100); w.Put(100); w.Put((ushort)0);
        w.Put(0); w.Put(0);
        var body = w.CopyData();
        uint h = 2166136261; foreach (var x in body) { h ^= x; h *= 16777619; }
        var o = new NetDataWriter(); o.Put(body.Length); o.Put(body); o.Put(h); return o.CopyData();
    }

    static void HostOnlyKey()
    {
        var host = new WorldHost(3); uint rev; int sr; float sh;
        var s = new WalletState { RecId = 1, Balance = 50, P2Wallet = 0, Seq = 1 };
        var code = host.HandleProposal("c", 2, 1, new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.Wallet, Value = s.ToBytes() }, 1, out rev, out sr, out sh);
        Expect(code == ProposalCode.NotAuthorized && host.Reg.Get(StateScope.Session, 0, WorldKeys.Wallet) == null, "a client can never write the shared balance");
        host.CommitWallet(s); WalletState b;
        var e = host.Reg.Get(StateScope.Session, 0, WorldKeys.Wallet);
        Expect(e != null && WalletState.TryParse(e.Value, out b) && b.Balance == 50 && host.Outbox.Count == 1, "host commit -> registry + one delta");
        host.Outbox.Clear(); host.CommitWallet(s); Expect(host.Outbox.Count == 0, "unchanged value -> no traffic");
    }

    static void RefusedThenPickup()
    {
        var w = new World(100);
        // a purchase the 2 s check has not reported yet (the shop path reports at once; this models any unreported spend)
        w.C.Live[Lockpick] = 1; w.C.Money -= 500; w.C.Observe(); w.C.W.Purchase(500);
        Expect(w.C.RequestLoot(0x5001, 0, 500), "pickup: report (with the unaffordable purchase) + lootTxn on the same channel");
        w.Step(4);                                  // results arrive, record restored; the coroutine (~0.48 s) is still running
        Expect(w.H.Money == 600 && w.H.Book.Rec.CountOf(Lockpick) == 0 && w.H.Book.Rec.Wallet == 500, "host: purchase refused, money pickup credited once (" + w.H.Book.Rec + ")");
        Expect(w.C.Await && w.C.AwaitCovered && w.C.W.Armed == 500, "client restored a record that already contains the pickup -> exclusion armed");
        w.C.CompleteLoot(true); w.C.W.Disarm(); w.Settle();
        Expect(w.H.Money == 600 && w.Display == 600 && w.C.W.Local == 500 && Count(w.C.Live, Lockpick) == 0, "coroutine's add_money not counted twice: host " + w.H.Money + ", client " + w.Display);
    }

    static void SourceScan()
    {
        string root = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && !Directory.Exists(Path.Combine(root, "src")); i++) root = Path.GetDirectoryName(root.TrimEnd('\\', '/'));
        var src = System.IO.File.ReadAllText(Path.Combine(root, "src", "Plugin", "WalletSync.cs"));
        foreach (var bad in new[] { "FindObjectOfType", "GameObject.Find", "OnGUI", "GUI.Label", "SpawnWarning", ".Select(", ".Where(" })
            Expect(!src.Contains(bad), "WalletSync has no " + bad);
        Expect(P2Book.MaxWallet <= 4096, "bounded wallet journal");
    }
}
