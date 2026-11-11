// M4.3.2b shared car storage (CarBox) on the ORIGINAL storage UI. Harmony (narrow, verified IL):
//   PlayerScript.OpenStorage(int)     : the only way the original storage UI opens (CarBox branch of PlayerScript.Update, and
//                                       the mod's passenger-seat bridge). Type 0 = car storage (gated), 1 = hideout locker (untouched).
//   Inventory.MoveItemToStorage()     : the only caller of ItemID.Move_To_Storage / Move_Out_Of_Storage (one unit of one type).
// Rules: while both players are connected, the car storage UI opens only for the holder of the host's exclusive lease
// ("carbox"); a refused or pending open simply does not open (no text). The client first receives the host's real contents
// ("carboxItems") and writes them into its own ItemID.item_amount_storage, so the original UI shows the host-authoritative
// storage. Host moves are the original code inside the host's own lease session. M4.3.3: a client move becomes a host
// transaction ("storageTxn", Player2Sync): the client runs the original local checks, sends one unit of one type, and only
// after the host committed it (Player-2 record + journal + host storage, written durably) the original
// MoveItemToStorage / ItemID.Move_To_Storage(0) / Move_Out_Of_Storage(0) runs locally. Nothing moves locally before the
// host's acknowledgment; a lost acknowledgment is resolved from the host record (AppliedTxn) after reconnecting.
// The hideout locker (type 1) is host world storage that the client does not have: client locker moves are refused.
using System;
using System.Collections.Generic;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class CarBoxSync : IDisposable
    {
        public static CarBoxSync Instance;
        public static bool Ready { get { var i = Instance; return i != null && i._ok; } }

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host; readonly byte _me;
        Harmony _h; bool _ok;
        Func<object, object> _psItems, _psInv; Func<object, int> _itemId, _itemStorage, _invType; Action<object, int> _setItemStorage, _openStorage;
        Func<object, bool> _invStorage, _invShop;
        // M4.3.3 client transfers (Inventory.MoveItemToStorage checks, verified IL)
        Func<object, object> _invButton, _btnItem; Func<object, bool> _btnInStorage, _itemStory, _itemHeavy; Func<object, int> _itemAmount, _itemMax, _invMaxStorage;
        Func<object, float> _itemWeight, _invStoCap, _psWeight, _psMaxWeight; Action<object, int> _moveTo, _moveOut; Action<object> _invMove;
        sealed class Txn { public uint Id, Txn_; public object Button, Item; public bool Withdraw; public double At; public bool Lost; }
        Txn _txn; bool _resumeMove; long _txnSent, _txnOk, _txnRefused;

        // session state of THIS instance
        uint _held;                 // lease session this instance holds (0 none)
        bool _sawOpen; double _heldAt, _nextRenew;
        uint _pendingId; double _pendingAt; Vector3 _pendingPos; Transform _pendingParent; bool _resume;
        long _opens, _refused, _blockedMoves, _hostMoves, _releases, _snapshots;
        double _lastBlockLog = -100, _nextStats, _lastNotReadyLog = -100; long _notReady;

        static double Now { get { return Time.realtimeSinceStartup; } }

        public CarBoxSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host; _me = world.LocalSlot;
            try
            {
                var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory"); var item = AccessTools.TypeByName("ItemID");
                var open = ps != null ? AccessTools.Method(ps, "OpenStorage", new[] { typeof(int) }) : null;
                var move = inv != null ? AccessTools.Method(inv, "MoveItemToStorage", Type.EmptyTypes) : null;
                _psItems = Fast.Getter<object>(ps, "item_IDs2"); _psInv = Fast.Getter<object>(ps, "invScript");
                _itemId = Fast.Getter<int>(item, "item_ID"); _itemStorage = Fast.Getter<int>(item, "item_amount_storage"); _setItemStorage = Fast.Setter<int>(item, "item_amount_storage");
                _invType = Fast.Getter<int>(inv, "storage_Type"); _invStorage = Fast.Getter<bool>(inv, "isStorage"); _invShop = Fast.Getter<bool>(inv, "isShop");
                _openStorage = Fast.Call<int>(ps, "OpenStorage");
                var btn = AccessTools.TypeByName("Item_Button");
                _invButton = Fast.Getter<object>(inv, "current_item_button"); _btnItem = Fast.Getter<object>(btn, "itemScript"); _btnInStorage = Fast.Getter<bool>(btn, "is_in_storage");
                _itemStory = Fast.Getter<bool>(item, "isStoryItem"); _itemHeavy = Fast.Getter<bool>(item, "is_heavy"); _itemAmount = Fast.Getter<int>(item, "item_amount"); _itemMax = Fast.Getter<int>(item, "max_amount");
                _itemWeight = Fast.Getter<float>(item, "item_weight"); _invMaxStorage = Fast.Getter<int>(inv, "max_storage_capacity"); _invStoCap = Fast.Getter<float>(inv, "current_storage_capacity");
                _psWeight = Fast.Getter<float>(ps, "currentWeight"); _psMaxWeight = Fast.Getter<float>(ps, "maxWeight");
                _moveTo = Fast.Call<int>(item, "Move_To_Storage"); _moveOut = Fast.Call<int>(item, "Move_Out_Of_Storage"); _invMove = Fast.Call0(inv, "MoveItemToStorage");
                if (open == null || move == null || _psItems == null || _psInv == null || _itemId == null || _itemStorage == null || _setItemStorage == null || _invType == null || _invStorage == null || _openStorage == null)
                { _log.Warn("CARBOX game API incomplete: shared car storage disabled (the original storage stays local)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.carbox");
                _h.Patch(open, new HarmonyMethod(AccessTools.Method(typeof(CarBoxSync), "OpenPrefix")));
                _h.Patch(move, new HarmonyMethod(AccessTools.Method(typeof(CarBoxSync), "MovePrefix")), new HarmonyMethod(AccessTools.Method(typeof(CarBoxSync), "MovePostfix")));
                _ok = true;
                Instance = this;
                world.ProposalResultHook2 = OnResult;
                if (_host && world.Host != null) world.Host.CarBoxSnapshot = Snapshot;
                _log.Info("CARBOX shared car storage ready (" + (_host ? "host: real storage, lease owner" : "client: host contents, moves as host transactions") + ")");
            }
            catch (Exception ex) { _log.Error("CARBOX init failed: shared car storage disabled", ex); _ok = false; }
        }

        bool Live { get { return _node.IsConnected && _world.Synced; } }

        // ---- PlayerScript.OpenStorage(int) prefix ----
        static bool OpenPrefix(object __instance, int __0)
        {
            var i = Instance;
            if (i == null || __0 != 0) return true;                 // hideout locker and other storages: original
            try { return i.AllowOpen(__instance); }
            catch (Exception ex) { i._log.Error("CARBOX open hook failed (storage not opened)", ex); return false; }   // fail closed
        }

        bool AllowOpen(object ps)
        {
            if (!Live) return _host;                                // host alone: original; client without host: nothing to show
            if (_resume) { _resume = false; return true; }          // our own continuation after the host's grant
            double now = Now;
            if (_host)
            {
                string why;
                var code = _world.Host.CarBoxRequest(_me, CarBoxOp.Open, 0, _world.Clock, out why);
                _world.FlushOutbox();
                if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged)
                {
                    _held = _world.Host.CarBoxState.Session; _sawOpen = false; _heldAt = now; _opens++;
                    _log.Info("CARBOX host opened the car storage (session " + _held + ")");
                    return true;
                }
                _refused++;
                _log.Info("CARBOX host open refused: " + code + (why != null ? " (" + why + ")" : "") + " - nothing opens");
                return false;
            }
            if (_held != 0 || _pendingId != 0) return false;        // one request at a time
            var p2 = Player2Sync.Instance;
            if (p2 == null || !p2.Ready || !p2.Restored)
            {
                // the original UI simply does not open; one log line per 10 s with a counter instead of one per key press
                _notReady++;
                if (now - _lastNotReadyLog >= 10.0) { _lastNotReadyLog = now; _log.Info("CARBOX client open ignored: Player-2 inventory not ready (state " + (p2 != null ? p2.SyncState : "-") + ", refusals " + _notReady + ")"); }
                return false;
            }
            var psc = ps as Component;
            // position in the parent's space: a seated passenger is parented to its seat and moves with the car
            _pendingParent = psc != null ? psc.transform.parent : null;
            _pendingPos = psc != null ? (_pendingParent != null ? psc.transform.localPosition : psc.transform.position) : Vector3.zero;
            _pendingAt = now;
            _pendingId = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.CarBoxReq, Value = CarBoxRequest.Encode(CarBoxOp.Open, 0) });
            _log.Info("CARBOX open requested #" + _pendingId);
            return false;                                           // opens after the host's grant
        }

        bool OnResult(uint id, ProposalCode code)
        {
            if (_txn != null && id == _txn.Id) { TxnResult(code); return true; }
            if (id != _pendingId || _pendingId == 0) return false;
            _pendingId = 0;
            if (code != ProposalCode.Accepted && code != ProposalCode.Unchanged) { _refused++; _log.Info("CARBOX open #" + id + " -> " + code + ": nothing opens"); return true; }
            // the registry deltas (contents, then lease) precede this result
            CarBoxLeaseState st; CarBoxItems items;
            var le = _world.Registry.Get(StateScope.Session, 0, WorldKeys.CarBox);
            var ie = _world.Registry.Get(StateScope.Session, 0, WorldKeys.CarBoxItems);
            if (le == null || !CarBoxLeaseState.TryParse(le.Value, out st) || st.Owner != _me) { _log.Warn("CARBOX grant #" + id + " without a matching lease in the registry: not opened"); return true; }
            _held = st.Session; _heldAt = Now; _sawOpen = false;
            if (ie == null || !CarBoxItems.TryParse(ie.Value, out items)) { _log.Warn("CARBOX grant without host contents: not opened"); Release("no host contents"); return true; }
            var gm = _world.GameManagerObject; var ps = gm != null ? GameAccessPs(gm) : null; var psc = ps as Component;
            if (ps == null || Time.timeScale <= 0f || Now - _pendingAt > 3.0 || (psc != null && (psc.transform.parent != _pendingParent || Vector3.Distance(_pendingParent != null ? psc.transform.localPosition : psc.transform.position, _pendingPos) > 1.5f)) || UiOpen(ps))
            { _log.Info("CARBOX grant arrived but the player moved on / a UI is open: released"); Release("stale grant"); return true; }
            int applied = ApplyContents(ps, items);
            _snapshots++;
            _log.Info("CARBOX opened the car storage with the host's contents (session " + _held + ", " + items.Counts.Count + " item types, " + applied + " counters set)");
            _resume = true; _opens++;
            try { _openStorage(ps, 0); }
            catch (Exception ex) { _log.Error("CARBOX OpenStorage after the grant failed", ex); }
            finally { if (_resume) { _resume = false; _log.Warn("CARBOX continuation not consumed (storage did not open)"); Release("open failed"); } }
            return true;
        }

        // ---- Inventory.MoveItemToStorage prefix/postfix ----
        static bool MovePrefix(object __instance)
        {
            var i = Instance;
            if (i == null) return true;
            try { return i.AllowMove(__instance); }
            catch (Exception ex) { i._log.Error("CARBOX move hook failed (move blocked)", ex); return i._host; }
        }

        static void MovePostfix(object __instance)
        {
            var i = Instance;
            if (i == null || !i._host) return;
            try { if (i._invType(__instance) == 0) i._hostMoves++; } catch { }
        }

        bool AllowMove(object inv)
        {
            int type = _invType(inv);
            if (_host)
            {
                if (type != 0 || !Live) return true;                // locker / solo: original
                var st = _world.Host.CarBoxState;                   // host moves only inside its own CURRENT lease session
                return _held != 0 && st.Owner == _me && st.Session == _held;
            }
            if (_resumeMove) { _resumeMove = false; return true; }  // the confirmed transfer, executed by the original code
            if (type != 0)
            {
                // hideout locker = host world storage, not present in the client (copy cleared): a move would put a
                // Player-2 item into a local copy that is gone at the next start (item lost)
                _blockedMoves++; BlockLog("CARBOX client locker move refused (the hideout locker is host storage, not shared yet)"); return false;
            }
            if (_held == 0 || _txn != null || !Live) return false;  // only inside the own session, one transfer at a time
            return BeginTransfer(inv);
        }

        void BlockLog(string s) { if (Now - _lastBlockLog > 2.0) { _lastBlockLog = Now; _log.Info(s); } }

        // The original local checks of Inventory.MoveItemToStorage (car storage), then the host transaction. Nothing moves
        // locally now; the original move runs when the host has committed the transfer.
        bool BeginTransfer(object inv)
        {
            var p2 = Player2Sync.Instance;
            if (p2 == null || !p2.Restored) return false;
            var btn = _invButton(inv); var item = btn != null ? _btnItem(btn) : null;
            if (btn == null || item == null || _itemStory(item)) return false;
            bool withdraw = _btnInStorage(btn);
            var gm = _world.GameManagerObject; var ps = gm != null ? GameAccessPs(gm) : null;
            if (ps == null) return false;
            bool ok = withdraw ? StorageRules.WithdrawFits(_psWeight(ps), _itemWeight(item), _psMaxWeight(ps), _itemAmount(item), _itemMax(item))
                               : StorageRules.DepositFits(_invStoCap(inv), _itemWeight(item), _invMaxStorage(inv));
            if (!ok) return false;                                  // the original would not move either
            if (!p2.PrepareTransfer()) return false;                // newest Player-2 state first (same ordered channel); a report still out -> nothing moves
            uint txn = p2.AppliedTxn + 1;
            var req = new StorageTxnRequest { Session = _held, Txn = txn, ItemId = _itemId(item), Withdraw = withdraw };
            _txn = new Txn { Txn_ = txn, Button = btn, Item = item, Withdraw = withdraw, At = Now };
            _txn.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.StorageTxn, Value = req.Encode() });
            _txnSent++;
            _log.Info("CARBOX transfer #" + txn + " item " + req.ItemId + (withdraw ? " CarBox -> Player 2" : " Player 2 -> CarBox") + " requested (proposal " + _txn.Id + ")");
            return false;
        }

        void TxnResult(ProposalCode code)
        {
            var t = _txn; _txn = null;
            if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged) { ApplyTransfer(t, "acknowledged (" + code + ")"); return; }
            _txnRefused++;
            _log.Info("CARBOX transfer #" + t.Txn_ + " -> " + code + ": nothing moved");
        }

        // The committed transfer on the client: the original MoveItemToStorage when the same item is still selected in the
        // open storage UI (original move + UI refresh + sound), else the original unit move ItemID.Move_To_Storage(0) /
        // Move_Out_Of_Storage(0) on the item's counters (UI closed / selection changed).
        void ApplyTransfer(Txn t, string why)
        {
            var p2 = Player2Sync.Instance;
            var gm = _world.GameManagerObject; var ps = gm != null ? GameAccessPs(gm) : null; var inv = ps != null ? _psInv(ps) : null;
            int before = _itemAmount(t.Item);
            bool viaUi = inv != null && _invStorage(inv) && _invType(inv) == 0 && ReferenceEquals(_invButton(inv), t.Button) && _btnInStorage(t.Button) == t.Withdraw;
            if (viaUi)
            {
                _resumeMove = true;
                try { _invMove(inv); } catch (Exception ex) { _log.Error("CARBOX original move after the acknowledgment failed", ex); }
                finally { _resumeMove = false; }
            }
            if (_itemAmount(t.Item) == before)                      // not moved by the UI path (closed / changed / its checks changed)
            {
                try { if (t.Withdraw) _moveOut(t.Item, 0); else _moveTo(t.Item, 0); } catch (Exception ex) { _log.Error("CARBOX unit move after the acknowledgment failed", ex); }
            }
            if (p2 != null) p2.AppliedTxn = t.Txn_;                 // the record delta (same channel) already carries this transfer
            _txnOk++;
            _log.Info("CARBOX transfer #" + t.Txn_ + " " + why + ": applied locally" + (viaUi ? " by the original storage UI" : " to the item counters") + ", Player 2 now " + _itemAmount(t.Item));
        }

        public bool TransferPending { get { return _txn != null && !_txn.Lost; } }
        public bool HasLostTransfer { get { return _txn != null && _txn.Lost; } }
        public bool SessionOpen { get { return _held != 0; } }

        // After a reconnect the acknowledgment may be lost: the host record tells whether the transfer was committed.
        public bool ResolvePending(P2Inventory rec)
        {
            var t = _txn;
            if (t == null) return false;
            _txn = null;
            if (rec.AppliedTxn == t.Txn_) { ApplyTransfer(t, "committed by the host (acknowledgment lost)"); return true; }
            _log.Info("CARBOX transfer #" + t.Txn_ + " was not committed by the host (record at " + rec.AppliedTxn + "): nothing moved");
            return false;
        }

        // ---- per frame: watch the own session ----
        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            if (now >= _nextStats) { _nextStats = now + 30; if (_opens + _refused + _blockedMoves > 0) _log.Info(Stats);
            if (_txn != null && !_txn.Lost && now - _txn.At > 15.0) { _txn.Lost = true; _log.Warn("CARBOX transfer #" + _txn.Txn_ + " unanswered for 15 s: resolved from the host record when it changes"); } }
            if (_pendingId != 0 && now - _pendingAt > 6.0) { _log.Info("CARBOX open request #" + _pendingId + " unanswered: dropped"); _pendingId = 0; }
            if (_txn != null && !Live) _txn.Lost = true;            // acknowledgment may be lost: resolved from the host record later
            if (_held == 0) return;
            if (!Live) { _log.Info("CARBOX session " + _held + " ended (connection lost)"); _held = 0; if (Player2Sync.Instance != null) Player2Sync.Instance.ClearStorageMirror(); return; }
            var gm = _world.GameManagerObject; var ps = gm != null ? GameAccessPs(gm) : null;
            var inv = ps != null ? _psInv(ps) : null;
            bool open = inv != null && _invStorage(inv) && _invType(inv) == 0;
            if (open) _sawOpen = true;
            if ((_sawOpen && !open) || (!_sawOpen && now - _heldAt > 1.5)) { Release(_sawOpen ? "storage UI closed" : "storage UI never opened"); return; }
            if (!_host && now >= _nextRenew)
            {
                _nextRenew = now + CarBoxLease.RenewSeconds;
                _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.CarBoxReq, Value = CarBoxRequest.Encode(CarBoxOp.Renew, _held) });
            }
        }

        void Release(string why)
        {
            if (_held == 0) return;
            uint s = _held; _held = 0; _releases++;
            if (_host) { string w; _world.Host.CarBoxRequest(_me, CarBoxOp.Close, s, _world.Clock, out w); _world.FlushOutbox(); }
            else if (Live) _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.CarBoxReq, Value = CarBoxRequest.Encode(CarBoxOp.Close, s) });
            _log.Info("CARBOX session " + s + " released (" + why + ")");
            if (!_host && Player2Sync.Instance != null) Player2Sync.Instance.ClearStorageMirror();   // the host's contents are not kept in the client
        }

        // ---- contents ----
        // Host: every item type with a car-storage count. Two ItemID objects with one item_ID and stock would be ambiguous ->
        // no snapshot (the client's open is refused) instead of guessing.
        byte[] Snapshot()
        {
            var gm = _world.GameManagerObject; var ps = gm != null ? GameAccessPs(gm) : null;
            var arr = ps != null ? _psItems(ps) as Array : null;
            if (arr == null) return null;
            var items = new CarBoxItems { Rev = (uint)(_snapshots + 1) };
            var seen = new HashSet<int>();
            foreach (var o in arr)
            {
                if (o == null) continue;
                int id = _itemId(o), c = _itemStorage(o);
                if (c <= 0) continue;
                if (!seen.Add(id)) { _log.Error("CARBOX item_ID " + id + " appears twice with stock: contents not shared"); return null; }
                items.Counts.Add(new KeyValuePair<int, int>(id, c));
            }
            byte[] data;
            if (!items.TryEncode(out data)) { _log.Error("CARBOX contents do not fit one snapshot (" + items.Counts.Count + " types): not shared"); return null; }
            _snapshots++;
            return data;
        }

        // Client: the original UI reads ItemID.item_amount_storage (Inventory.LoadInventory -> ItemID.Load_Item); every type is
        // set to the host's count (0 when absent). Only this instance's sandbox objects change; nothing is sent back.
        int ApplyContents(object ps, CarBoxItems items)
        {
            var arr = _psItems(ps) as Array;
            if (arr == null) return 0;
            var want = new Dictionary<int, int>();
            foreach (var kv in items.Counts) want[kv.Key] = kv.Value;
            int n = 0;
            foreach (var o in arr)
            {
                if (o == null) continue;
                int id = _itemId(o), c;
                if (!want.TryGetValue(id, out c)) c = 0; else want.Remove(id);   // one object per type gets the count
                if (_itemStorage(o) != c) { _setItemStorage(o, c); n++; }
            }
            if (want.Count > 0) _log.Warn("CARBOX " + want.Count + " host item type(s) unknown on this instance: not shown");
            return n;
        }

        bool UiOpen(object ps)
        {
            var inv = _psInv(ps);
            return inv != null && (_invStorage(inv) || (_invShop != null && _invShop(inv)));
        }

        static Func<object, object> _gmPs;
        static object GameAccessPs(object gm)
        {
            if (_gmPs == null) _gmPs = Fast.Getter<object>(gm.GetType(), "PS");
            return _gmPs != null ? _gmPs(gm) : null;
        }

        public string Stats { get { return "CARBOX_STATS held=" + _held + " opens=" + _opens + " refused=" + _refused + " releases=" + _releases + " hostMoves=" + _hostMoves + " clientTransfers sent/ok/refused=" + _txnSent + "/" + _txnOk + "/" + _txnRefused + " clientLockerBlocked=" + _blockedMoves + " snapshots=" + _snapshots + (_host && _world.Host != null ? " lease=" + _world.Host.CarBoxState + " grants/busy/stale/expired=" + _world.Host.CarBox.Grants + "/" + _world.Host.CarBox.Busy + "/" + _world.Host.CarBox.Stale + "/" + _world.Host.CarBox.Expired : ""); } }

        public void Dispose()
        {
            if (_held != 0) { try { Release("shutdown"); } catch { } }
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_world.ProposalResultHook2 == (Func<uint, ProposalCode, bool>)OnResult) _world.ProposalResultHook2 = null;
            if (_host && _world.Host != null) _world.Host.CarBoxSnapshot = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
