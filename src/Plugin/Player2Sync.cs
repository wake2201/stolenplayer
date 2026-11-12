// M4.3.3 durable Player-2 inventory (design: src/Shared/Player2Inventory.cs). Host: owns the record per save profile
// (p2inv-<profile>.dat next to the fleet file), executes CarBox transfers of Player 2 against its real storage counters,
// keeps a transfer watermark + save history id inside the host save and replays journal entries above the watermark after
// a load; a client report is written durably BEFORE it is acknowledged (0.7.1). Client (lifecycle: src/Shared/Player2Client.cs):
// restores the record into its sandbox (SPrefs + live ItemID/PlayerScript fields) in EVERY loaded scene, acknowledges it,
// reports its Player-2 state when it changed (2 s check, before every transfer, at every scene exit), keeps unconfirmed exit
// state in a small pending file until the host committed it, never shows the host's car storage / locker copies.
// W3.4 storage durability (design + IL: src/Shared/Player2Inventory.cs "W3.4 durability model"): host-only postfixes on
// ItemID.Move_To_Storage(int) / Move_Out_Of_Storage(int) feed the unsaved Player-1 deposit ledger; SaveGame.SaveGameNewSystem
// prefix + finalizer mark the original save whose FBPP write clears it. Player-2 withdrawals that need such a deposit journal
// it first (HostItem -1 + HostCar/HostLocker +1) in the same durable write; keys always hold the recoverable counts.
// W4.1 world loot (design: src/Shared/WorldLoot.cs): HostLootTake commits a Player-2 world pickup (record +1 + durable world
// take, one atomic p2inv write) and writes the original keys; HostBind replays world takes above the host watermark. The
// LootProvenance ledger attributes every durable Player-1 loss (P2 picked up P1's drop / withdrew P1's unsaved deposit):
// when Player 1's recoverable count cannot cover it, the unsaved world pickup it came from is journaled in the same write.
// Harmony (narrow, verified IL): FBPP.Save / FBPP.SaveToMainProfile (host: journal compaction, history confirmation),
// BlackBay_Object.SellIt (prefix guard: a sale may only consume items the seller really owns), client only:
// SaveGame.LoseGame (empty body; called by GameManager.LoadMenu IL_0011, Inventory.QuitGame IL_001f, SaveGame.OnApplicationQuit
// IL_000b = every exit to the main menu / quit, while the scene still exists) and SaveGame.SaveGameNewSystem(bool,float)
// (every Select<Map> travel, PlayerScript.Quicksave, EndingFade) - prefix only captures Player 2's state (exit sync).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class Player2Sync : IDisposable
    {
        public static Player2Sync Instance;
        public const string WatermarkKey = "TS2MP_StorageTxn";   // last Player-2 transfer contained in the Storage_Item_ counts (host SPrefs)
        public const string HistoryKey = "TS2MP_P2History";      // id of the Player-2 record that belongs to this save history (host SPrefs)
        public const string HostMarkKey = "TS2MP_HostTxn";       // W3: last host-journal sequence contained in the host save (Item_/Locker_Item_ counts)

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host; readonly string _dataDir;
        Harmony _h; bool _ok;
        // accessors (compiled once)
        Func<object, object> _gmPs, _psItems, _psInv, _bbHolder; Func<object, bool> _gmLoaded;
        Func<object, int> _id, _amt, _sto, _lock, _maxAmt, _money, _baton, _crowbar, _maxStorage, _maxLocker; Func<object, float> _w, _curW, _maxW;
        Func<object, bool> _heavy, _story, _invStorageOpen;
        Action<object, int> _setAmt, _setSto, _setLock, _setMoney, _setBaton, _setCrowbar; Action<object, float> _setCurW, _setStoCap, _setLockCap;
        Func<string, int> _get; Action<string, int> _set;
        Func<object, object> _bbAssigned; Func<object, int> _bbRequired; Type _bbItemT;

        // ---- host ----
        P2Book _book; int _profile = int.MinValue; FleetStore _store; FleetWriter _writer; object _bookGm; long _savingTxn = -1, _savingHost = -1; bool _dirtyFile; double _nextWrite;
        long _syncWrites; double _syncWriteMaxMs;
        bool _clientSynced; long _hostSyncs;
        // W3.4 storage durability (host): unsaved Player-1 deposits + "an original game save is running" (Save_Item_Amount
        // wrote the live counts into the keys; the following FBPP write puts them on disk)
        readonly HostDepositLedger _ledger = new HostDepositLedger(); bool _ledgerOn; int _gameSaveDepth; bool _gameSaveDiskWrite;
        long _prereqs, _refusedUnsaved, _ledgerClears;
        // W4.1 (host): world takes + provenance of Player-1 units that Player 2 durably receives
        readonly LootProvenance _prov = new LootProvenance(); long _lootTakes, _lootReplayed, _provTakes, _provUnattributed;
        sealed class Keys : IKeyStore { public Player2Sync S; public int Get(string k) { return S._get(k); } public void Set(string k, int v) { S._set(k, v); var m = WorldKeyMirror.Instance; if (m != null) m.HostKeyChanged(k); } }
        Keys _keys;
        // ---- client ----
        P2ClientSync _sync; object _sceneGm; string _pendingPath; readonly Dictionary<int, int> _unknown = new Dictionary<int, int>();
        long _bbBlocked, _pendingWrites; double _pendingWriteMaxMs, _captureMaxMs;

        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool Ready { get { return _ok; } }
        // Client: true only when this scene's Player-2 inventory was restored from the host record AND acknowledged.
        public bool Restored { get { return _host || (_sync != null && _sync.Ready); } }
        public string SyncState { get { return _host ? "host" : _sync != null ? _sync.State.ToString() : "-"; } }
        public uint AppliedTxn { get { return _sync != null ? _sync.Applied : 0; } set { if (_sync != null) _sync.Applied = value; } }

        public Player2Sync(SessionNode node, InstanceLog log, WorldSync world, string dataDir)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host; _dataDir = dataDir;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var it = AccessTools.TypeByName("ItemID");
                var inv = AccessTools.TypeByName("Inventory"); var sp = AccessTools.TypeByName("SPrefs");
                _gmPs = Fast.Getter<object>(gm, "PS"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap");
                _psItems = Fast.Getter<object>(ps, "item_IDs"); _psInv = Fast.Getter<object>(ps, "invScript");
                _money = Fast.Getter<int>(ps, "moneyAm"); _setMoney = Fast.Setter<int>(ps, "moneyAm");
                _baton = Fast.Getter<int>(ps, "batonLife"); _setBaton = Fast.Setter<int>(ps, "batonLife");
                _crowbar = Fast.Getter<int>(ps, "crowbarLife"); _setCrowbar = Fast.Setter<int>(ps, "crowbarLife");
                _curW = Fast.Getter<float>(ps, "currentWeight"); _setCurW = Fast.Setter<float>(ps, "currentWeight"); _maxW = Fast.Getter<float>(ps, "maxWeight");
                _id = Fast.Getter<int>(it, "item_ID"); _amt = Fast.Getter<int>(it, "item_amount"); _setAmt = Fast.Setter<int>(it, "item_amount");
                _sto = Fast.Getter<int>(it, "item_amount_storage"); _setSto = Fast.Setter<int>(it, "item_amount_storage");
                _lock = Fast.Getter<int>(it, "item_amount_locker"); _setLock = Fast.Setter<int>(it, "item_amount_locker");
                _w = Fast.Getter<float>(it, "item_weight"); _heavy = Fast.Getter<bool>(it, "is_heavy"); _maxAmt = Fast.Getter<int>(it, "max_amount"); _story = Fast.Getter<bool>(it, "isStoryItem");
                _maxStorage = Fast.Getter<int>(inv, "max_storage_capacity"); _setStoCap = Fast.Setter<float>(inv, "current_storage_capacity"); _setLockCap = Fast.Setter<float>(inv, "current_locker_capacity");
                _maxLocker = Fast.Getter<int>(inv, "max_locker_capacity");
                _invStorageOpen = Fast.Getter<bool>(inv, "isStorage");
                var gi = AccessTools.Method(sp, "GetInt", new[] { typeof(string) }); var si = AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) });
                if (gi != null) _get = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                if (si != null) _set = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), si);
                var bbo = AccessTools.TypeByName("BlackBay_Object"); _bbItemT = AccessTools.TypeByName("BlackBay_Item");
                _bbHolder = Fast.Getter<object>(bbo, "holder_of_items"); _bbAssigned = Fast.Getter<object>(_bbItemT, "assigned_itemID"); _bbRequired = Fast.Getter<int>(_bbItemT, "required_amount");
                bool core = _gmPs != null && _gmLoaded != null && _psItems != null && _money != null && _setMoney != null && _baton != null && _crowbar != null && _setBaton != null && _setCrowbar != null
                    && _id != null && _amt != null && _setAmt != null && _sto != null && _setSto != null && _lock != null && _setLock != null && _w != null && _heavy != null
                    && _curW != null && _setCurW != null && _get != null && _set != null && _maxStorage != null && _maxLocker != null && _maxAmt != null;
                if (!core) { _log.Warn("P2INV game API incomplete: Player-2 inventory persistence and CarBox transfers disabled"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.p2inv");
                if (_host)
                {
                    var fbpp = AccessTools.TypeByName("FBPP");
                    var save = fbpp != null ? AccessTools.Method(fbpp, "Save", Type.EmptyTypes) : null;
                    var main = fbpp != null ? AccessTools.Method(fbpp, "SaveToMainProfile", Type.EmptyTypes) : null;
                    if (save == null) { _log.Warn("P2INV FBPP.Save not found: Player-2 persistence disabled (no crash-safe save hook)"); return; }
                    var pre = new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "SavePrefix")); var post = new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "SavePostfix"));
                    _h.Patch(save, pre, post);
                    if (main != null) _h.Patch(main, pre, post);
                    // W3.4: Player-1 deposits not yet in the disk save (ItemID.Move_To_Storage / Move_Out_Of_Storage, only
                    // called by Inventory.MoveItemToStorage; the mod's Player-2 transfers never call them on the host) and
                    // the original save boundary (SaveGame.SaveGameNewSystem = the only Save_Item_Amount caller).
                    var mvIn = AccessTools.Method(it, "Move_To_Storage", new[] { typeof(int) }); var mvOut = AccessTools.Method(it, "Move_Out_Of_Storage", new[] { typeof(int) });
                    var sg = AccessTools.TypeByName("SaveGame"); var gameSave = sg != null ? AccessTools.Method(sg, "SaveGameNewSystem", new[] { typeof(bool), typeof(float) }) : null;
                    if (mvIn == null || mvOut == null || gameSave == null) _log.Warn("P2INV storage durability hooks missing (Move_To_Storage/Move_Out_Of_Storage/SaveGameNewSystem): Player-2 withdrawals of units not in the host save are refused");
                    else
                    {
                        _h.Patch(mvIn, null, new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "DepositPostfix")));
                        _h.Patch(mvOut, null, new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "WithdrawPostfix")));
                        _h.Patch(gameSave, prefix: new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "GameSavePrefix")), finalizer: new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "GameSaveFinalizer")));
                        _ledgerOn = true;
                    }
                }
                else
                {
                    var sg = AccessTools.TypeByName("SaveGame");
                    var lose = sg != null ? AccessTools.Method(sg, "LoseGame", Type.EmptyTypes) : null;
                    var newSave = sg != null ? AccessTools.Method(sg, "SaveGameNewSystem", new[] { typeof(bool), typeof(float) }) : null;
                    if (lose == null || newSave == null) { _log.Warn("P2INV SaveGame.LoseGame / SaveGameNewSystem not found: Player-2 persistence disabled (no exit synchronization)"); return; }
                    var exit = new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "ExitPrefix"));
                    _h.Patch(lose, exit); _h.Patch(newSave, exit);
                    InitClientSync();
                }
                var sell = bbo != null ? AccessTools.Method(bbo, "SellIt", Type.EmptyTypes) : null;
                if (sell != null && _bbHolder != null && _bbAssigned != null && _bbRequired != null) _h.Patch(sell, new HarmonyMethod(AccessTools.Method(typeof(Player2Sync), "SellPrefix")));
                else _log.Warn("P2INV BlackBay_Object.SellIt accessors missing: Blackbay sale guard inactive");
                _ok = true; Instance = this;
                if (_host && world.Host != null) { world.Host.P2ReportHook = HostReport; world.Host.StorageTxnHook = HostTransfer; }
                if (!_host) world.ProposalResultHook3 = OnResult;
                _node.RemoteDisconnected += OnDisconnected;
                _log.Info("P2INV ready (" + (_host ? "host: owns Player 2's record per save profile" : "client: Player-2 inventory restored from the host, changes reported") + ")");
            }
            catch (Exception ex) { _log.Error("P2INV init failed (Player-2 persistence disabled)", ex); _ok = false; }
        }

        bool Live { get { return _node.IsConnected && _world.Synced; } }
        object Ps() { var gm = _world.GameManagerObject; return gm != null ? _gmPs(gm) : null; }
        IList Items(object ps) { return ps != null ? _psItems(ps) as IList : null; }

        double _nextStats;
        public void Update()
        {
            if (!_ok) return;
            if (Now >= _nextStats) { _nextStats = Now + 30; _log.Info(Stats); }
            var gm = _world.GameManagerObject;
            bool loaded = false;
            if (gm != null) try { loaded = _gmLoaded(gm); } catch { }
            if (_host) { if (loaded) HostFrame(gm); }
            else ClientFrame(gm, loaded);
        }

        void OnDisconnected(string reason)
        {
            if (_host && _clientSynced) { _clientSynced = false; _log.Info("P2INV client disconnected (" + reason + "): Player-2 record kept (" + (_book != null ? _book.Rec.ToString() : "no book") + ")"); }
        }

        // ======================================================================== host
        void HostFrame(object gm)
        {
            if (!ReferenceEquals(gm, _bookGm)) { _bookGm = gm; HostBind(); }
            if (_book != null && _dirtyFile && Now >= _nextWrite) { _dirtyFile = false; _nextWrite = Now + 1.0; QueueWrite(); }
        }

        // New GameManager (save loaded, travel reload): (re)load the profile's book, then bring the in-memory storage counts
        // up to the journal: exactly the transfers above the watermark stored with those counts are applied (idempotent).
        void HostBind()
        {
            int profile = PlayerPrefs.GetInt("Profile");
            uint mark = (uint)Math.Max(0, _get(WatermarkKey));
            if (_book == null || profile != _profile)
            {
                if (_writer != null) _writer.Flush(2000);
                _profile = profile;
                _store = new FleetStore(Path.Combine(_dataDir, "p2inv-" + profile + ".dat"));
                _writer = new FleetWriter(_store);
                _book = LoadBook(profile, mark);
            }
            else if (!P2Book.SameHistory(_get(HistoryKey), mark, _book))
            {
                _book = LoadBook(profile, mark);                             // another save of this profile was loaded in this process
            }
            // W3.4: the live counts of this scene come from the keys (ItemID.Start): unsaved Player-1 deposits of a previous scene
            // are gone with the reload (travel saved them first) -> nothing left to attribute
            if (_ledger.Entries > 0) { _ledger.Clear(); _ledgerClears++; }
            if (_keys == null) _keys = new Keys { S = this };
            // W3: host-side effects of cross-player item transfers; W3.4: replayed together with the Player-2 transfers in
            // journal (= commit) order, so a prerequisite Player-1 deposit is applied before the withdrawal that depends on it
            uint hmark = (uint)Math.Max(0, _get(HostMarkKey));
            if (hmark > _book.HostSeq) _book.HostSeq = hmark;                // sequence stays above anything a save already contains
            uint maxTxn, maxHost;
            var list = _book.ReplayAll(mark, hmark, out maxTxn, out maxHost);
            if (list.Count > 0)
            {
                ApplyStorageDeltas(list, "replay of " + list.Count + " journal entr(ies) in commit order not yet contained in the loaded save (watermark " + mark + ", host watermark " + hmark + ")");
                if (maxTxn > mark) _set(WatermarkKey, (int)maxTxn);
                if (maxHost > hmark) _set(HostMarkKey, (int)maxHost);
                _book.Replayed += list.Count;
            }
            // W4.1: durable world takes not in the loaded save (ItemDay_ never moved backwards, Item_<name> = 1)
            uint maxWorld;
            var wl = _book.ReplayWorld(hmark, out maxWorld);
            if (wl.Count > 0)
            {
                foreach (var w in wl) LootKeys.Apply(_keys, w.Take);
                if (maxWorld > (uint)Math.Max(0, _get(HostMarkKey))) _set(HostMarkKey, (int)maxWorld);
                _lootReplayed += wl.Count;
                _log.Info("P2INV replay of " + wl.Count + " world take(s) not contained in the loaded save (host watermark " + hmark + "): " + wl[0].Take + (wl.Count > 1 ? " ..." : ""));
            }
            _prov.Reset();                                                   // the loaded save + replays = the recoverable state
            _world.Host.CommitP2(_book.Rec); _world.FlushOutbox();
            _log.Info("P2INV host book profile " + profile + " watermark " + mark + " host watermark " + hmark + ": " + _book.Rec + ", journal " + _book.Journal.Count);
        }

        P2Book LoadBook(int profile, uint mark)
        {
            bool free = _get("FreeMode") == 1;
            P2Book b = null; string err = "missing";
            foreach (var path in new[] { _store.Path, _store.Path + ".bak" })
            {
                if (!File.Exists(path)) continue;
                try { if (P2Book.TryDeserialize(File.ReadAllBytes(path), profile, out b, out err)) break; } catch (Exception ex) { err = ex.Message; }
                b = null;
            }
            int hist = _get(HistoryKey);
            if (b != null && !P2Book.SameHistory(hist, mark, b))
            {
                if (_writer != null) _writer.Flush(2000);
                string q = null; try { q = _store.Quarantine("stale"); } catch { }
                _log.Warn("P2INV record of profile " + profile + " belongs to another save history (save id " + hist + "/watermark " + mark + ", record id " + b.HistoryId + (b.HistoryConfirmed ? " confirmed" : "") + "/watermark " + b.LastSavedTxn + ": new game / older save): kept as " + q + ", Player 2 starts fresh");
                b = null;
            }
            if (b == null)
            {
                if (err != "missing") _log.Warn("P2INV record of profile " + profile + " unusable (" + err + "): Player 2 starts fresh");
                b = new P2Book(P2Inventory.Initial(free), mark);
                b.HistoryId = NewHistoryId(); b.Rec.Id = b.HistoryId;
                b.Rec.AppliedTxn = mark;                                     // transfer ids continue above any id already in this save
                b.Rec.Rev = 1;
                _log.Info("P2INV new Player-2 inventory for profile " + profile + " (original new-game state: empty backpack, money " + b.Rec.Money + ", tools 100) - never a copy of Player 1");
                _dirtyFile = true;
            }
            if (_get(HistoryKey) != b.HistoryId) _set(HistoryKey, b.HistoryId);   // reaches the disk with the next original save
            return b;
        }

        static int NewHistoryId() { var r = new System.Random(); int v; do { v = r.Next(1, int.MaxValue); } while (v == 0); return v; }

        // FBPP disk write: the watermark in memory is written together with the storage counts; afterwards every journal
        // entry up to it is contained in the save on disk and can be dropped from the p2inv file.
        static void SavePrefix()
        {
            var i = Instance; if (i == null || !i._host || i._book == null) return;
            try { i._savingTxn = Math.Max(0, i._get(WatermarkKey)); i._savingHost = Math.Max(0, i._get(HostMarkKey)); } catch { i._savingTxn = -1; i._savingHost = -1; }
        }

        static void SavePostfix()
        {
            var i = Instance; if (i == null || !i._host) return;
            // W3.4: only a disk write inside the original game save carries the live storage/backpack counts (Save_Item_Amount
            // ran just before): every Player-1 deposit is now on disk. Postfix = the write returned without an exception.
            if (i._gameSaveDepth > 0) { i._gameSaveDiskWrite = true; if (i._ledger.Entries > 0) { i._ledger.Clear(); i._ledgerClears++; } i._prov.Reset(); }
            if (i._book == null || i._savingTxn < 0) return;
            try
            {
                i._book.SaveCompleted((uint)i._savingTxn, (uint)Math.Max(0, i._savingHost)); i._savingTxn = -1; i._savingHost = -1;
                if (!i._book.HistoryConfirmed && i._get(HistoryKey) == i._book.HistoryId) i._book.HistoryConfirmed = true;
                i.QueueWrite();
            }
            catch (Exception ex) { i._log.Error("P2INV save completion failed", ex); }
        }

        // W3.4 ledger hooks (host). __0 = storage type (0 car, 1 locker), verified IL ItemID.Move_To_Storage(int) IL_0000.
        static void DepositPostfix(object __instance, int __0)
        {
            var i = Instance; if (i == null || !i._host || !StorageKind.Valid(__0)) return;
            try { i._ledger.Deposit((byte)__0, i._id(__instance)); } catch { }
        }
        static void WithdrawPostfix(object __instance, int __0)
        {
            var i = Instance; if (i == null || !i._host || !StorageKind.Valid(__0)) return;
            try { i._ledger.Withdraw((byte)__0, i._id(__instance)); } catch { }
        }
        static void GameSavePrefix() { var i = Instance; if (i != null && i._host) { i._gameSaveDepth++; i._gameSaveDiskWrite = false; } }
        static Exception GameSaveFinalizer(Exception __exception)
        {
            var i = Instance;
            if (i != null && i._host && i._gameSaveDepth > 0)
            {
                i._gameSaveDepth--;
                if (__exception != null) i._log.Warn("P2INV original save threw (" + __exception.GetType().Name + ")" + (i._gameSaveDiskWrite ? "" : " before its disk write: unsaved Player-1 deposits stay tracked"));
            }
            return __exception;                                            // never swallow the original's exception
        }

        void QueueWrite() { if (_book != null && _writer != null) _writer.Queue(_book.Serialize(_profile)); }

        bool WriteNow()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (_writer != null) _writer.Flush(2000);                 // never overtake a queued older payload
                _store.WriteAtomic(_book.Serialize(_profile));
                _syncWrites++; double ms = sw.Elapsed.TotalMilliseconds; if (ms > _syncWriteMaxMs) _syncWriteMaxMs = ms;
                return true;
            }
            catch (Exception ex) { _log.Error("P2INV durable write failed", ex); return false; }
        }

        // A client report is committed only after the record reached the p2inv file (atomic write); a failed write undoes it
        // and answers Busy (the client retries, bounded). The first committed/unchanged answer of a connection = the client's
        // restore acknowledgment (logged once).
        ProposalCode HostReport(P2Inventory rep)
        {
            if (_book == null) return ProposalCode.Busy;
            var before = _book.Rec;
            var code = _book.AcceptReport(rep);
            if (code == ProposalCode.Accepted)
            {
                if (!WriteNow()) { _book.Rec = before; _log.Warn("P2INV report not committed (durable write failed): Busy"); return ProposalCode.Busy; }
                _dirtyFile = false;
                _world.Host.CommitP2(_book.Rec);
                _log.Info("P2INV report committed: " + _book.Rec);
            }
            if (code == ProposalCode.Stale) _log.Info("P2INV report refused as stale: report " + rep + ", host " + _book.Rec);
            if ((code == ProposalCode.Accepted || code == ProposalCode.Unchanged) && !_clientSynced)
            { _clientSynced = true; _hostSyncs++; _log.Info("P2INV client synchronized: restore acknowledged, record " + _book.Rec); }
            return code;
        }

        // One client storage transfer (car storage or hideout locker): validated against the host's real counters (stock,
        // original capacity, backpack max_amount), applied to the record + journal + host storage, written durably BEFORE the
        // result is sent. A failed write undoes everything (no acknowledgment).
        ProposalCode HostTransfer(StorageTxnRequest t)
        {
            if (_book == null) return ProposalCode.Busy;
            var ps = Ps(); var items = Items(ps); var inv = ps != null ? _psInv(ps) : null;
            if (items == null || inv == null) return ProposalCode.Busy;
            bool locker = t.Kind == StorageKind.Locker;
            object item = null; float stoWeight = 0f;
            foreach (var o in items)
            {
                if (o == null) continue;
                if (!_heavy(o)) stoWeight += _w(o) * (locker ? _lock(o) : _sto(o));
                if (item == null && _id(o) == t.ItemId) item = o;
            }
            if (item == null || (_story != null && _story(item))) return ProposalCode.Invalid;
            int count = locker ? _lock(item) : _sto(item);
            bool depositFits = StorageRules.DepositFits(stoWeight, _w(item), locker ? _maxLocker(inv) : _maxStorage(inv));
            int mx = _maxAmt(item);
            bool withdrawFits = mx <= 0 || _book.Rec.CountOf(t.ItemId) + 1 <= mx;
            // W3.4: keys hold the RECOVERABLE counts (disk + journaled deltas); live fields hold the game state
            string stoKey = StorageKind.KeyPrefix(t.Kind) + t.ItemId, itemKey = "Item_" + t.ItemId;
            int stoKeyBefore = _get(stoKey), itemKeyBefore = _get(itemKey), prevMark = _get(WatermarkKey), prevHost = _get(HostMarkKey);
            int deposits = _ledgerOn ? _ledger.Count(t.Kind, t.ItemId) : 0;
            var mark = _book.Mark();
            StoragePlan plan;
            var code = _book.HostTransfer(t.Txn, t.Kind, t.ItemId, t.Withdraw, count, stoKeyBefore, itemKeyBefore, deposits, depositFits, withdrawFits, out plan);
            // W4.1: the prerequisite is a durable Player-1 loss: attribute it (saved unit first, else the unsaved world pickup)
            var attr = Attribution.SavedUnit; LootTake take = default(LootTake); uint takeSeq = 0;
            if (code == ProposalCode.Accepted && plan.Prerequisite)
            {
                attr = _prov.P1Lost(t.ItemId, itemKeyBefore, out take);
                if (attr == Attribution.WorldTake) { takeSeq = _book.NextHostSeq(); if (!_book.AddWorldTake(takeSeq, take)) { _prov.UndoLost(t.ItemId, attr, take); attr = Attribution.Unattributed; takeSeq = 0; } }
            }
            if (code != ProposalCode.Accepted)
            {
                if (plan.Why != null) _refusedUnsaved++;
                if (code != ProposalCode.Unchanged) _log.Info("P2INV transfer #" + t.Txn + " (" + StorageKind.Name(t.Kind) + ") item " + t.ItemId + (t.Withdraw ? " withdraw" : " deposit") + " -> " + code + (plan.Why != null ? ": " + plan.Why + " (live " + count + ", recoverable " + stoKeyBefore + ", tracked Player-1 deposits " + deposits + ")" : ""));
                return code;
            }
            int delta = plan.StorageDelta;
            SetLive(item, t.Kind, count + delta);
            _set(stoKey, plan.NewStorageKey);
            _set(WatermarkKey, (int)t.Txn);
            if (plan.Prerequisite)
            {
                _ledger.Consume(t.Kind, t.ItemId);
                _set(itemKey, plan.NewItemKey);                                 // the prerequisite effect only (never unrelated live changes)
                _set(HostMarkKey, (int)(takeSeq != 0 ? takeSeq : plan.PrereqSeq));   // W4.1: the world take's keys are in memory since Player 1's original pickup
            }
            if (!WriteNow())
            {
                SetLive(item, t.Kind, count);
                _set(stoKey, stoKeyBefore); _set(WatermarkKey, prevMark);
                if (plan.Prerequisite) { _ledger.Restore(t.Kind, t.ItemId); _set(itemKey, itemKeyBefore); _set(HostMarkKey, prevHost); _prov.UndoLost(t.ItemId, attr, take); }
                _book.Restore(mark);
                _log.Warn("P2INV transfer #" + t.Txn + " not committed (durable write failed): Busy, nothing changed");
                return ProposalCode.Busy;
            }
            if (plan.Prerequisite) _prereqs++;
            NoteAttribution(attr, take, t.ItemId, "withdrawal #" + t.Txn + " of an unsaved Player-1 deposit");
            _world.Host.CommitP2(_book.Rec);
            _log.Info("P2INV transfer #" + t.Txn + " item " + t.ItemId + (t.Withdraw ? " " + StorageKind.Name(t.Kind) + " -> Player 2" : " Player 2 -> " + StorageKind.Name(t.Kind)) + ": storage " + count + " -> " + (count + delta)
                + " (recoverable " + stoKeyBefore + " -> " + plan.NewStorageKey + ")" + (plan.Prerequisite ? ", unsaved Player-1 deposit journaled first (host seq " + plan.PrereqSeq + ": Player 1 Item_ " + itemKeyBefore + " -> " + plan.NewItemKey + ")" : "")
                + ", P2 now " + _book.Rec.CountOf(t.ItemId) + " (journal " + _book.Journal.Count + ")");
            return ProposalCode.Accepted;
        }

        void SetLive(object item, byte kind, int value) { if (kind == StorageKind.Locker) _setLock(item, value); else _setSto(item, value); }

        // Journal replay: storage (car / locker) and host backpack / host locker counts, key + live field.
        void ApplyStorageDeltas(List<JournalEntry> list, string why)
        {
            var ps = Ps(); var items = Items(ps);
            var byId = new Dictionary<int, object>();
            if (items != null) foreach (var o in items) if (o != null && !byId.ContainsKey(_id(o))) byId[_id(o)] = o;
            foreach (var e in list)
            {
                string key = JournalKind.KeyPrefix(e.Kind) + e.ItemId;
                int v = Math.Max(0, _get(key) + e.StorageDelta);
                _set(key, v);
                object o;
                if (!byId.TryGetValue(e.ItemId, out o)) continue;
                if (JournalKind.IsLockerKey(e.Kind)) _setLock(o, v);
                else if (e.Kind == JournalKind.HostItem) { _setAmt(o, v); if (!_heavy(o) && ps != null) _setCurW(ps, Math.Max(0f, _curW(ps) + _w(o) * e.StorageDelta)); }
                else _setSto(o, v);
            }
            _log.Info("P2INV " + why);
        }

        // ======================================================================== W3 dropped items (host)
        // Player 2 side of an item transaction: drop (record -1) / pickup (record +1), written durably before the
        // acknowledgment. A pickup of an item Player 1 dropped also journals Player 1 backpack loss (host journal entry with
        // the drop's host sequence), so a host reload of a save that predates the drop cannot give the item back to Player 1
        // while Player 2 keeps it.
        public ProposalCode HostP2ItemTxn(ItemOp op, uint txn, int itemId, byte origin, uint originTxn)
        {
            if (_book == null) return ProposalCode.Busy;
            var mark = _book.Mark();
            var code = _book.ItemTxn(txn, itemId, op == ItemOp.Drop ? -1 : +1);
            if (code != ProposalCode.Accepted) { _log.Info("P2INV item txn #" + txn + " " + op + " item " + itemId + " -> " + code); return code; }
            var attr = Attribution.SavedUnit; LootTake take = default(LootTake); uint takeSeq = 0; int prevHost = _get(HostMarkKey);
            bool p1Loss = op == ItemOp.Pickup && origin == 1 && originTxn != 0 && originTxn > _book.LastSavedHost;   // a save after the drop already lacks the unit
            if (op == ItemOp.Pickup && origin == 1 && originTxn != 0) _book.AddHostEntry(originTxn, JournalKind.HostItem, itemId, -1);
            if (p1Loss)
            {
                // W4.1: Player 2 now durably owns a unit of Player 1: attribute it (Player 1's recoverable base was captured
                // before HostDropMark changed the key)
                attr = _prov.P1Lost(itemId, _get("Item_" + itemId), out take);
                if (attr == Attribution.WorldTake) { takeSeq = _book.NextHostSeq(); if (!_book.AddWorldTake(takeSeq, take)) { _prov.UndoLost(itemId, attr, take); attr = Attribution.Unattributed; takeSeq = 0; } }
            }
            if (!WriteNow())
            {
                _book.Restore(mark);
                if (p1Loss) _prov.UndoLost(itemId, attr, take);
                _log.Warn("P2INV item txn #" + txn + " not committed (durable write failed): Busy");
                return ProposalCode.Busy;
            }
            if (takeSeq != 0 && takeSeq > (uint)Math.Max(0, prevHost)) _set(HostMarkKey, (int)takeSeq);
            if (p1Loss) NoteAttribution(attr, take, itemId, "pickup #" + txn + " of a Player-1 drop");
            _dirtyFile = false;
            _world.Host.CommitP2(_book.Rec);
            _log.Info("P2INV item txn #" + txn + " " + op + " item " + itemId + " committed: P2 now " + _book.Rec.CountOf(itemId) + (op == ItemOp.Pickup && origin == 1 ? " (dropped by Player 1, host journal seq " + originTxn + ")" : ""));
            return ProposalCode.Accepted;
        }

        // Host sequence for a Player-1 drop: the watermark and the current backpack count go into the save memory now, so any
        // later FBPP write says "this save already lacks the dropped item".
        public uint HostDropMark(object itemObj, int itemId)
        {
            if (_book == null) return 0;
            uint seq = _book.NextHostSeq();
            _set(HostMarkKey, (int)seq);
            _prov.EnsureBase(itemId, _get("Item_" + itemId));               // W4.1: Player 1's recoverable base before the key changes
            if (itemObj != null) _set("Item_" + itemId, _amt(itemObj));
            _dirtyFile = true;                                             // HostSeq persisted with the next background write
            return seq;
        }

        // Player 1 received an item Player 2 dropped (counted already by the original Item_Picked), or a Player-2 item was
        // converted into the host locker: journal + watermark + key, durably.
        public bool HostJournal(byte kind, int itemId, object itemObj, string why)
        {
            if (_book == null) return false;
            uint seq = _book.NextHostSeq();
            if (!_book.AddHostEntry(seq, kind, itemId, +1)) { _log.Warn("P2INV host journal full: " + why + " not journaled"); return false; }
            if (kind == JournalKind.HostItem) _prov.P1Gained(itemId, _get("Item_" + itemId));   // W4.1: before the key changes
            _set(HostMarkKey, (int)seq);
            // W3.4: the locker key stays the recoverable count (key + 1), never the live count (which may contain unsaved
            // Player-1 deposits); the backpack key keeps the W3 behaviour
            if (kind == JournalKind.HostLocker) _set(JournalKind.KeyPrefix(kind) + itemId, Math.Max(0, _get(JournalKind.KeyPrefix(kind) + itemId)) + 1);
            else if (itemObj != null) _set(JournalKind.KeyPrefix(kind) + itemId, _amt(itemObj));
            bool ok = WriteNow();
            _log.Info("P2INV host journal seq " + seq + " " + why + (ok ? "" : " (durable write FAILED - kept in memory, retried by the background writer)"));
            if (!ok) _dirtyFile = true;
            return ok;
        }

        // ======================================================================== W4.1 world loot (host)
        void NoteAttribution(Attribution a, LootTake take, int itemId, string what)
        {
            if (a == Attribution.WorldTake) { _provTakes++; _log.Info("P2INV " + what + ": the unit was Player 1's unsaved world pickup " + take + " -> world take journaled with it"); }
            else if (a == Attribution.Unattributed) { _provUnattributed++; _log.Warn("P2INV " + what + ": item " + itemId + " is an unsaved Player-1 acquisition that is not a tracked world pickup (shop / pickpocket / untracked): an unsaved host quit returns its origin (documented limitation)"); }
        }

        // Player 1's original pickup reached SetDayRespawn (unsaved world acquisition, original rollback semantics).
        public void NoteP1Pick(LootTake t) { if (_host && _ok) _prov.P1Picked(t); }

        // A validated Player-2 world pickup: record +1 (exactly-once txn) + world take, ONE atomic p2inv write, then the original
        // keys (ItemDay_<name>, Item_<name>) and the host watermark in the save memory. Failure: nothing changed (Busy).
        public ProposalCode HostLootTake(uint txn, LootTake take, out string why)
        {
            why = null;
            if (_book == null || _keys == null) { why = "no Player-2 book yet"; return ProposalCode.Busy; }
            if (_book.WorldFull) { why = "world journal full (host never saved)"; return ProposalCode.Busy; }
            var mark = _book.Mark();
            var code = _book.ItemTxn(txn, take.ItemId, +1);
            if (code != ProposalCode.Accepted) { why = "record " + code; return code; }
            uint seq = _book.NextHostSeq();
            if (!_book.AddWorldTake(seq, take)) { _book.Restore(mark); why = "world take refused"; return ProposalCode.Busy; }
            if (!WriteNow()) { _book.Restore(mark); why = "durable write failed"; _log.Warn("P2INV world pickup #" + txn + " not committed (durable write failed): Busy, nothing changed"); return ProposalCode.Busy; }
            LootKeys.Apply(_keys, take);
            _set(HostMarkKey, (int)seq);
            _dirtyFile = false; _lootTakes++;
            _world.Host.CommitP2(_book.Rec);
            _log.Info("P2INV world pickup #" + txn + " committed: " + take + " -> Player 2 now " + _book.Rec.CountOf(take.ItemId) + " (host seq " + seq + ", world journal " + _book.World.Count + ")");
            return ProposalCode.Accepted;
        }

        public int P2Count(int itemId) { return _book != null ? _book.Rec.CountOf(itemId) : 0; }
        public uint P2AppliedTxn { get { return _book != null ? _book.Rec.AppliedTxn : 0; } }
        public bool HostBookReady { get { return _host && _book != null && _keys != null; } }

        public byte LeaseOwner { get { return _host && _world.Host != null ? _world.Host.CarBoxState.Owner : (byte)0; } }
        public byte LockerLeaseOwner { get { return _host && _world.Host != null ? _world.Host.Locker.State.Owner : (byte)0; } }

        // ======================================================================== client
        void InitClientSync()
        {
            _pendingPath = Path.Combine(_dataDir, "p2pending-client.dat");
            _sync = new P2ClientSync
            {
                Capture = Capture, Apply = Apply, Send = SendReport, Persist = PersistPending, RequestResync = () => _world.RequestResync("Player-2 record missing"),
                Info = m => _log.Info(m), Warn = m => _log.Warn(m), Error = m => _log.Error(m)
            };
            foreach (var path in new[] { _pendingPath, _pendingPath + ".bak" })
            {
                if (!File.Exists(path)) continue;
                P2Inventory p, prev;
                try { if (P2PendingCodec.TryDecode(File.ReadAllBytes(path), out p, out prev)) { _sync.LoadPending(p, prev); break; } } catch (Exception ex) { _log.Warn("P2INV pending file unreadable (" + ex.Message + ")"); }
                _log.Warn("P2INV pending file " + path + " unusable: ignored");
            }
        }

        void ClientFrame(object gm, bool loaded)
        {
            object scene = loaded ? gm : null;
            if (!ReferenceEquals(scene, _sceneGm)) { _sceneGm = scene; _sync.SceneLeft(); }   // new GameManager / main menu: restore again
            bool live = Live;
            P2Inventory rec = null; bool invalid = false;
            if (live) TryRecord(out rec, out invalid);
            var cb = CarBoxSync.Instance; var wi = WorldItemSync.Instance; var wl = WorldLootSync.Instance;
            bool pending = (cb != null && cb.TransferPending) || (wi != null && wi.TxnPending) || (wl != null && wl.TxnPending);
            if (rec != null && !pending && cb != null && cb.HasLostTransfer) cb.ResolvePending(rec);   // committed -> applied now; not committed -> dropped
            if (rec != null && !pending && wi != null && wi.HasLostTxn) wi.ResolvePending(rec);         // W3: same for a drop / pickup
            if (rec != null && !pending && wl != null && wl.HasLostTxn) wl.ResolvePending(rec);         // W4.1: same for a world pickup
            bool localReady = loaded && LocalReady();
            _sync.Tick(Now, live, rec, invalid, localReady, pending);
        }

        bool LocalReady() { var items = Items(Ps()); return items != null && items.Count > 0; }

        // Parsed once per registry revision (no per-frame key composition / parsing / allocation). The cached record is
        // never mutated (P2ClientSync only copies its header).
        uint _recEpoch, _recHead = uint.MaxValue; bool _recSnap; P2Inventory _recCache; bool _recInvalid;
        bool TryRecord(out P2Inventory rec, out bool invalid)
        {
            var reg = _world.Registry;
            if (reg.Epoch != _recEpoch || reg.HeadRev != _recHead || reg.HasSnapshot != _recSnap)
            {
                _recEpoch = reg.Epoch; _recHead = reg.HeadRev; _recSnap = reg.HasSnapshot; _recCache = null; _recInvalid = false;
                var e = reg.Get(StateScope.Session, 2, WorldKeys.P2Inv);
                if (e != null && !P2Inventory.TryParse(e.Value, out _recCache)) { _recCache = null; _recInvalid = true; }
            }
            rec = _recCache; invalid = _recInvalid;
            return rec != null;
        }

        P2Inventory CurrentRecord() { P2Inventory r; bool inv; return Live && TryRecord(out r, out inv) ? r : null; }

        // Writes the record into SPrefs (later scene loads read it through ItemID.Start / SaveGame.LoadGame) and into the
        // live objects; the host's car storage and locker copies of the sandbox are cleared (world storages owned by the host).
        bool Apply(P2Inventory rec)
        {
            var ps = Ps(); var items = Items(ps);
            if (ps == null || items == null || items.Count == 0) return false;
            var known = new HashSet<int>();
            float weight = 0f;
            foreach (var o in items)
            {
                if (o == null) continue;
                int id = _id(o);
                int c = known.Add(id) ? rec.CountOf(id) : 0;
                _setAmt(o, c); _set("Item_" + id, c);
                _setSto(o, 0); _set("Storage_Item_" + id, 0);
                _setLock(o, 0); _set("Locker_Item_" + id, 0);
                if (!_heavy(o)) weight += _w(o) * c;
            }
            _unknown.Clear();
            foreach (var kv in rec.Items) if (!known.Contains(kv.Key)) _unknown[kv.Key] = kv.Value;
            _setMoney(ps, rec.Money); _set("Money", rec.Money);
            _set("BankBalance", rec.Bank);
            _setBaton(ps, rec.BatonLife); _set("BatonLife", rec.BatonLife);
            _setCrowbar(ps, rec.CrowbarLife); _set("CrowbarLife", rec.CrowbarLife);
            _setCurW(ps, weight);
            var inv = _psInv(ps); if (inv != null) { if (_setStoCap != null) _setStoCap(inv, 0f); if (_setLockCap != null) _setLockCap(inv, 0f); }
            _log.Info("P2INV Player-2 inventory restored: " + rec + (_unknown.Count > 0 ? ", " + _unknown.Count + " item type(s) unknown here kept in the record" : "") + "; host car storage / locker copies cleared");
            return true;
        }

        P2Inventory Capture()
        {
            var ps = Ps(); var items = Items(ps);
            if (ps == null || items == null || items.Count == 0) return null;
            var sw = Stopwatch.StartNew();
            var s = new P2Inventory();
            foreach (var o in items) { if (o == null) continue; int c = _amt(o); if (c > 0 && !s.Items.ContainsKey(_id(o))) s.Items[_id(o)] = c; }
            foreach (var kv in _unknown) if (!s.Items.ContainsKey(kv.Key)) s.Items[kv.Key] = kv.Value;
            s.Money = _money(ps); s.Bank = _get("BankBalance"); s.BatonLife = _baton(ps); s.CrowbarLife = _crowbar(ps);
            double ms = sw.Elapsed.TotalMilliseconds; if (ms > _captureMaxMs) _captureMaxMs = ms;
            return s;
        }

        uint SendReport(P2Inventory s)
        {
            byte[] d;
            if (!_node.IsConnected || !s.TryEncode(out d)) return 0;
            return _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 2, Key = WorldKeys.P2Report, Value = d });
        }

        // Only at scene exit / shutdown with unconfirmed state (a few hundred bytes, atomic replace); deleted when committed.
        void PersistPending(P2Inventory pending, P2Inventory prev)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (pending == null) { foreach (var path in new[] { _pendingPath, _pendingPath + ".bak", _pendingPath + ".tmp" }) if (File.Exists(path)) File.Delete(path); return; }
                new FleetStore(_pendingPath).WriteAtomic(P2PendingCodec.Encode(pending, prev));
                _pendingWrites++; double ms = sw.Elapsed.TotalMilliseconds; if (ms > _pendingWriteMaxMs) _pendingWriteMaxMs = ms;
            }
            catch (Exception ex) { _log.Error("P2INV pending file write failed (unconfirmed state kept in memory only)", ex); }
        }

        bool OnResult(uint id, ProposalCode code) { return _sync != null && _sync.OnResult(id, code, CurrentRecord()); }

        static void ExitPrefix()
        {
            var i = Instance;
            if (i == null || i._host || i._sync == null) return;
            try { i._sync.CaptureExit(Now, i.Live, i.CurrentRecord()); }
            catch (Exception ex) { i._log.Error("P2INV exit capture failed", ex); }
        }

        // Before a CarBox transfer: newest Player-2 state first (same ordered channel); false = the transfer waits.
        public bool PrepareTransfer() { return !_host && _sync != null && _sync.PrepareTransfer(Now, CurrentRecord()); }

        // The host's storage values written into the client for an open session are cleared when it ends.
        public void ClearStorageMirror(byte kind)
        {
            if (_host || !_ok) return;
            var ps = Ps(); var items = Items(ps);
            if (items == null) return;
            bool locker = kind == StorageKind.Locker;
            foreach (var o in items)
            {
                if (o == null) continue;
                if (locker) { if (_lock(o) != 0) _setLock(o, 0); } else if (_sto(o) != 0) _setSto(o, 0);
                _set(StorageKind.KeyPrefix(kind) + _id(o), 0);
            }
            var inv = _psInv(ps);
            if (inv != null) { if (locker) { if (_setLockCap != null) _setLockCap(inv, 0f); } else if (_setStoCap != null) _setStoCap(inv, 0f); }
        }

        // W3: a dropped-item transaction of Player 2 is in flight (shares the Player-2 transaction id sequence with storage transfers).
        public bool ItemTxnPending { get { var w = WorldItemSync.Instance; var l = WorldLootSync.Instance; return (w != null && w.TxnPending) || (l != null && l.TxnPending); } }

        // ---- BlackBay_Object.SellIt guard (both roles): the original pays first, then removes each required item from the
        // backpack, else the locker, else the car storage (ItemID.Remove_Item_Blackbay). Client: only its own backpack is real
        // (locker / car storage are host-owned and not present in the client) -> every item must be in the backpack. Host:
        // a sale that would take car-storage items while Player 2 has the CarBox open is refused (the storage is in use).
        static bool SellPrefix(object __instance)
        {
            var i = Instance;
            if (i == null) return true;
            try { return i.AllowSale(__instance); }
            catch (Exception ex) { i._log.Error("P2INV Blackbay guard failed (sale refused)", ex); return false; }
        }

        bool AllowSale(object bb)
        {
            var holder = _bbHolder(bb) as GameObject;
            if (holder == null) return true;
            var cbOpen = CarBoxSync.Instance != null && CarBoxSync.Instance.SessionOpen;
            foreach (var c in holder.GetComponentsInChildren(_bbItemT, true))
            {
                var it = _bbAssigned(c); int need = _bbRequired(c);
                if (it == null || need <= 0) continue;
                if (_host)
                {
                    if (BlackbayRules.HostSaleBlocked(LeaseOwner, _amt(it), _lock(it), _sto(it), need)) { _bbBlocked++; _log.Info("P2INV Blackbay sale refused: it would take CarBox items while Player 2 uses the CarBox"); return false; }
                    if (BlackbayRules.HostLockerSaleBlocked(LockerLeaseOwner, _amt(it), _lock(it), need)) { _bbBlocked++; _log.Info("P2INV Blackbay sale refused: it would take locker items while Player 2 uses the hideout locker"); return false; }
                }
                else if (!BlackbayRules.ClientSaleAllowed(_amt(it), need, cbOpen))
                {
                    _bbBlocked++; _log.Info("P2INV Blackbay sale refused on the client: item " + _id(it) + " needs " + need + " in Player 2's own backpack (has " + _amt(it) + ")" + (cbOpen ? ", CarBox open" : "")); return false;
                }
            }
            return true;
        }

        public string Stats
        {
            get
            {
                return "P2INV_STATS " + (_host ? (_book != null ? "profile " + _profile + " " + _book.Rec + " journal " + _book.Journal.Count + " savedWatermark " + _book.LastSavedTxn
                    + " reports " + _book.Reports + " stale " + _book.ReportsStale + " transfers " + _book.Transfers + " refused " + _book.TransfersRefused + " replayed " + _book.Replayed
                    + " syncWrites " + _syncWrites + " max " + _syncWriteMaxMs.ToString("0.0") + "ms bgWrites " + (_writer != null ? _writer.Writes + "/" + _writer.Failures : "-") : "no book")
                    + " clientSynced " + _clientSynced + " syncs " + _hostSyncs
                    + " durability ledger " + (_ledgerOn ? _ledger.Entries + " type(s)" : "OFF") + " prerequisites " + _prereqs + " refusedUnsaved " + _refusedUnsaved + " ledgerClears " + _ledgerClears
                    + " worldTakes " + _lootTakes + " worldReplayed " + _lootReplayed + " worldJournal " + (_book != null ? _book.World.Count : 0) + " provenance tracked " + _prov.Tracked + " attributed " + _provTakes + " unattributed " + _provUnattributed
                    : (_sync != null ? _sync.Stats : "no lifecycle") + " pendingWrites " + _pendingWrites + " max " + _pendingWriteMaxMs.ToString("0.0") + "ms captureMax " + _captureMaxMs.ToString("0.00") + "ms") + " blackbayBlocked " + _bbBlocked;
            }
        }

        public void Dispose()
        {
            try { if (!_host && _sync != null) _sync.CaptureExit(Now, Live, CurrentRecord()); } catch { }   // no-op if SaveGame.OnApplicationQuit already captured
            _node.RemoteDisconnected -= OnDisconnected;
            if (_host && _book != null && _writer != null) { try { QueueWrite(); _writer.Flush(3000); } catch { } }
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_host && _world.Host != null) { _world.Host.P2ReportHook = null; _world.Host.StorageTxnHook = null; }
            if (!_host && _world.ProposalResultHook3 == (Func<uint, ProposalCode, bool>)OnResult) _world.ProposalResultHook3 = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
