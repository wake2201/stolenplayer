// H3 heavy loot sale integrity on the ORIGINAL heavy shop (host model: src/Shared/HeavyItems.cs HeavyBook.Sell / Unsell).
// Verified original (ilspycmd 1.27c, read-only):
//  - Inventory.OpenShop(junkyard false): heavy_in_shop / heavyAmountInShop of every Pickupable.is_heavy_item listed by the
//    SensorToolkit.TriggerSensor tagged "HeavyShopTrigger" (ItemID.Load_Item then lists them as shop items).
//  - Inventory.Sell_Item_NewSystem(bool chainSell), gate (!sellingItems || chainSell): value from current_item_script,
//    PS.moneyAm increased by the value, AddMoneyMade, ..., then FindHeavyInShopAndDestroy(): if current_item_button.itemScript.heavy_in_shop,
//    the FIRST DetectedObjects entry whose Pickupable.itemScript == current_item_button.itemScript gets the XP / Item_<name> /
//    stolenBigItemsNr (add_any_XP only) and Object.Destroy. Callers: the shop's Sell button and the hold-to-sell timer
//    (Inventory.Update -> SellButton.onClick.Invoke). CloseShop -> ClearInventory destroys every item button.
//  - BaseVolumeSensor.DetectedObjects rebuilds its list on every read and drops only null / inactive objects (a hidden H1
//    copy stays listed, a destroyed one disappears at the end of the frame).
// Root cause of the 0.22.1 duplication: the sale destroyed only the seller's local copy; the record stayed Placed, the other
// PC kept the object, and the seller's own HeavySync -> HeavyCargoSync.SyntheticObject re-created a cargo object from the
// record (log: "Release ... (object destroyed) -> Accepted", CARGO "spawned" growing, +298 per sale on both PCs).
// Multiplayer: a Sell_Item_NewSystem prefix (both roles) resolves the object the original is about to destroy, with the
// original's own selection rule, and asks the host to consume its record:
//  - host: decided in place (first committed wins) -> the original runs once (shared balance, original destroy, XP, story);
//    refused -> nothing happens (the button does nothing, log only).
//  - client: the original is refused, a heavyTxn Sell proposal; on Accepted the ORIGINAL Sell_Item_NewSystem runs exactly
//    once if the shop still has that object selected (money through the existing Player-2 wallet report, sent at once);
//    otherwise Unsell returns the object (no money was paid). While the sale is pending the object keeps its presentation
//    (Reconcile hold), so the original still finds it.
//  - an untracked heavy object (MainScene / runtime / ambiguous census id) stays the host's local original; Player 2's sale
//    of one is refused (its copy would be a local duplicate). A listed heavy item whose object is gone is refused in a session.
// Cost: one prefix per sale click (field reads, one FindGameObjectWithTag, one sensor list walk); per frame only a null
// check of the pending request.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class HeavySync
    {
        Func<object, object> _invButton, _btnItem, _pickItem; Func<object, bool> _invShop, _invSelling, _itemInShop; Func<object, int> _psMoney;
        Action<object, bool> _sellOriginal; Type _sensorT; PropertyInfo _sensorDetected;
        bool _saleOk, _resumeSale;

        sealed class SaleReq { public uint Id; public int Ns; public uint Hash; public ushort Seen; public bool Cargo; public object Inv; public UnityEngine.Object Button; public UnityEngine.Object Item; public GameObject Go; public double At; public string Name; }
        SaleReq _sale;
        readonly Dictionary<uint, SaleReq> _saleLate = new Dictionary<uint, SaleReq>();
        long _salesHost, _salesRequested, _salesGranted, _salesRun, _salesUndone, _salesRefused, _salesUntracked;
        public const double SaleTimeout = 15.0;

        void InitSale()
        {
            var inv = AccessTools.TypeByName("Inventory"); var btn = AccessTools.TypeByName("Item_Button"); var item = AccessTools.TypeByName("ItemID"); var ps = AccessTools.TypeByName("PlayerScript");
            _sensorT = AccessTools.TypeByName("SensorToolkit.TriggerSensor");
            _sensorDetected = _sensorT != null ? AccessTools.Property(_sensorT, "DetectedObjects") : null;
            _invButton = Fast.Getter<object>(inv, "current_item_button"); _invShop = Fast.Getter<bool>(inv, "isShop"); _invSelling = Fast.Getter<bool>(inv, "sellingItems");
            _btnItem = Fast.Getter<object>(btn, "itemScript"); _itemInShop = Fast.Getter<bool>(item, "heavy_in_shop"); _pickItem = Fast.Getter<object>(_pickT, "itemScript");
            _psMoney = Fast.Getter<int>(ps, "moneyAm"); _sellOriginal = Fast.Call<bool>(inv, "Sell_Item_NewSystem");
            var sell = inv != null ? AccessTools.Method(inv, "Sell_Item_NewSystem", new[] { typeof(bool) }) : null;
            if (sell == null || _sensorDetected == null || _invButton == null || _invShop == null || _invSelling == null || _btnItem == null || _itemInShop == null || _pickItem == null || _psMoney == null || _sellOriginal == null)
            {
                _log.Warn("HEAVY heavy shop API incomplete: heavy sales are not gated (a heavy object could be sold on both PCs)");
                return;
            }
            _h.Patch(sell, new HarmonyMethod(AccessTools.Method(typeof(HeavySync), "SalePrefix")));
            _saleOk = true;
        }

        static bool SalePrefix(object __instance, bool chainSell)
        {
            var hs = Instance;
            if (hs == null || !hs._ok || !hs._saleOk || __instance == null) return true;
            return hs.AllowSale(__instance, chainSell);
        }

        // The object FindHeavyInShopAndDestroy would destroy for this shop item (original selection rule), or null.
        GameObject ShopTarget(object item)
        {
            var g = GameObject.FindGameObjectWithTag("HeavyShopTrigger");
            var sensor = g != null ? g.GetComponent(_sensorT) : null;
            var list = sensor != null ? _sensorDetected.GetValue(sensor, null) as List<GameObject> : null;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var go = list[i]; var pick = go != null ? go.GetComponent(_pickT) : null;
                if (pick != null && ReferenceEquals(_pickItem(pick), item)) return go;
            }
            return null;
        }

        // true = the original sale runs now. Ordinary (non-heavy) sales and the original's own no-op gate pass untouched.
        bool AllowSale(object inv, bool chainSell)
        {
            if (_resumeSale) return true;                                       // our granted Player-2 sale
            string name = "?";
            try
            {
                if (_invSelling(inv) && !chainSell) return true;                // the original does nothing
                var btn = _invButton(inv) as UnityEngine.Object; if (btn == null) return true;
                var item = _btnItem(btn) as UnityEngine.Object; if (item == null || !_itemInShop(item)) return true;   // not a heavy shop item
                var go = ShopTarget(item);
                if (go == null)
                {
                    _salesRefused++;
                    LogOnce("gone:" + item.name, "HEAVY sale of heavy item '" + item.name + "' refused: its object is no longer at the heavy shop (sold or moved on the other PC) - nothing happens, no money");
                    return false;
                }
                name = go.name;
                var pick = go.GetComponent(_pickT);
                int ns; var e = Resolve(pick, out ns); uint hash = e != null ? e.Hash : 0u; bool cargo = false;
                if (e == null && HeavyCargoSync.Instance != null && HeavyCargoSync.Instance.TryCargoObject(pick, out hash)) { cargo = true; ns = _world.LocalRegion; }
                if (e != null) name = e.Name;
                if (e == null && !cargo)
                {
                    if (_host) { _salesUntracked++; LogOnce("untracked:" + name, "HEAVY Player-1 sale of '" + name + "': not a shared heavy object (MainScene / runtime / ambiguous) - the original local sale runs"); return true; }
                    _salesRefused++;
                    LogOnce("untracked:" + name, "HEAVY Player-2 sale of '" + name + "' refused: not a shared heavy object here (MainScene / runtime / ambiguous) - its copy would be a duplicate");
                    return false;
                }
                if (ns == Regions.None || ns == IdentityNamespace.MainScene) { _salesRefused++; LogOnce("nsale:" + name, "HEAVY sale of '" + name + "' refused: no region"); return false; }
                HeavyRecord r; bool has = TryRecord(ns, hash, out r);
                ushort seen = has ? r.Lease : (ushort)0;
                var t = new HeavyTxnRequest { Op = HeavyOp.Sell, Ns = ns, Hash = hash, Lease = seen, Rot = new Quat16 { W = 32767 }, Cargo = cargo };
                if (_host)
                {
                    if (_world.Host == null) return false;
                    string why; ushort l;
                    var code = _world.Host.HeavyTransaction(1, t, _world.Clock, out l, out why);
                    _world.FlushOutbox();
                    if (code != ProposalCode.Accepted) { _salesRefused++; _log.Info("HEAVY Player-1 sale of '" + name + "' (" + t + ") refused: " + code + (why != null ? " (" + why + ")" : "") + " - nothing happens, no money"); return false; }
                    _sims.Remove(hash); _dirty = true; _salesHost++;   // no "object destroyed" release: the record is consumed
                    _log.Info("HEAVY Player 1 sells '" + name + "' (" + t + "): host record consumed once, the original sale pays and destroys the object");
                    return true;
                }
                if (!Live) { _salesRefused++; LogOnce("offsale:" + name, "HEAVY Player-2 sale of '" + name + "' refused: not connected"); return false; }
                if (_sale != null) return false;                                // one sale at a time (hold-to-sell continues after the grant)
                if (has && (r.State == HeavyState.Removed || r.State == HeavyState.Carried)) { _salesRefused++; _log.Info("HEAVY Player-2 sale of '" + name + "' refused locally: " + r + " - nothing happens"); return false; }
                var cat = cargo ? null : LiveCatalog(ns);
                t.Digest = cat != null ? cat.Digest : 0u;
                _sale = new SaleReq { Ns = ns, Hash = hash, Seen = seen, Cargo = cargo, Inv = inv, Button = btn, Item = item, Go = go, At = Now, Name = name };
                _sale.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HeavyTxn, Value = t.Encode() });
                _salesRequested++;
                _log.Info("HEAVY Player-2 sale of '" + name + "' (" + t + ") requested from the host (proposal " + _sale.Id + "): the original sale waits for the grant");
                return false;
            }
            catch (Exception ex) { _salesRefused++; _log.Error("HEAVY sale gate failed for '" + name + "': sale refused (nothing happens)", ex); return false; }
        }

        bool OnSaleResultHook(uint id, ProposalCode code)
        {
            if (_sale != null && id == _sale.Id) { var q = _sale; _sale = null; _dirty = true; OnSaleResult(q, code); return true; }
            SaleReq late;
            if (!_saleLate.TryGetValue(id, out late)) return false;
            _saleLate.Remove(id);
            if (code == ProposalCode.Accepted) { _salesUndone++; SendTxn(HeavyOp.Unsell, late.Ns, late.Hash, late.Seen, default(Vector3), Quaternion.identity, 0u, late.Cargo, "late grant of an abandoned sale"); }
            return true;
        }

        void OnSaleResult(SaleReq q, ProposalCode code)
        {
            if (code != ProposalCode.Accepted) { _salesRefused++; _log.Info("HEAVY Player-2 sale of '" + q.Name + "' -> " + code + ": nothing happens, no money (sold / taken on the other PC, or stale)"); return; }
            _salesGranted++;
            string block = SaleBlock(q);
            if (block != null)
            {
                _salesUndone++;
                SendTxn(HeavyOp.Unsell, q.Ns, q.Hash, q.Seen, default(Vector3), Quaternion.identity, 0u, q.Cargo, "granted sale no longer possible: " + block);
                _log.Info("HEAVY Player-2 sale of '" + q.Name + "' granted but " + block + ": returned to the host (Unsell), no money");
                return;
            }
            var ps = Ps(); int before = ps != null ? _psMoney(ps) : 0; bool failed = false;
            _sims.Remove(q.Hash);
            _resumeSale = true;
            try { _sellOriginal(q.Inv, true); }
            catch (Exception ex) { failed = true; _log.Error("HEAVY original Sell_Item_NewSystem after the grant failed", ex); }
            finally { _resumeSale = false; }
            int paid = ps != null ? _psMoney(ps) - before : 0;
            if (failed && paid <= 0)
            {
                _salesUndone++;
                SendTxn(HeavyOp.Unsell, q.Ns, q.Hash, q.Seen, default(Vector3), Quaternion.identity, 0u, q.Cargo, "original sale failed before paying");
                return;
            }
            _salesRun++;
            if (WalletSync.Instance != null) WalletSync.Instance.CountNow("heavy sale");
            _log.Info("HEAVY Player 2 sold '" + q.Name + "' (" + ((Now - q.At) * 1000).ToString("0") + " ms after the click): original sale ran once, +" + paid + " reported to the shared balance now" + (paid <= 0 ? " (WARNING: no money change seen)" : ""));
        }

        string SaleBlock(SaleReq q)
        {
            if (q.Ns != _world.LocalRegion) return "region left";
            var invObj = q.Inv as UnityEngine.Object; if (invObj == null) return "no inventory";
            if (!_invShop(q.Inv)) return "shop closed";
            var btn = _invButton(q.Inv) as UnityEngine.Object;
            if (btn == null || !ReferenceEquals(btn, q.Button)) return "another item selected";
            var bc = btn as Component; if (bc == null || !bc.gameObject.activeInHierarchy) return "item no longer listed";
            if (q.Item == null || !_itemInShop(q.Item)) return "item no longer at the shop";
            if (q.Go == null) return "object gone";
            if (!ReferenceEquals(ShopTarget(q.Item), q.Go)) return "another object is next at the shop";
            return null;
        }

        void SaleFrame(double now)
        {
            if (_sale == null || (Live && now - _sale.At <= SaleTimeout)) return;
            _salesRefused++;
            _log.Warn("HEAVY Player-2 sale of '" + _sale.Name + "' unanswered" + (Live ? " after " + SaleTimeout + " s" : " (disconnected)") + ": abandoned, no money (a late grant is returned with Unsell)");
            if (_saleLate.Count < 16) _saleLate[_sale.Id] = _sale;
            _sale = null; _dirty = true;
        }

        // The pending sale's object keeps its presentation until the original sale ran (or the request ended).
        bool SaleHold(uint hash) { return _sale != null && _sale.Hash == hash; }

        string SaleStats
        {
            get
            {
                return " | sales host " + _salesHost + " req " + _salesRequested + " granted " + _salesGranted + " ran " + _salesRun + " undone " + _salesUndone + " refused " + _salesRefused + " untracked " + _salesUntracked;
            }
        }
    }
}
