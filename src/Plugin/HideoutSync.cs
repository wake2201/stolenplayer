// W1: shared hideout ownership on the ORIGINAL purchase paths (design + verified IL: src/Shared/Hideout.cs).
//   Host: the original Inventory.BuyGarage / HouseRenovator.BuyAddition / BuyRepairs / SetWallpaperBedroom run unchanged
//     (prefix refuses only an item the shared state already owns); the postfix re-derives and commits the shared state
//     "hideout". A client purchase ("hbuy") is decided by WorldHost.BuyHideout and applied here on the host's own original
//     state: moneyAm -= price (shared balance) + the key exactly as the original timing writes it -
//       garage   -> GM.mission_strings_to_set "garage" + GM.boughtGarage (Garage.BoughtSeq; saved as 2 at the next checkpoint)
//       addition / repairs -> GM.parcel_strings_to_set (saved as 1 at the next checkpoint, together with the money; the
//         client's original wrote it at once and saved at once through SelectHideout - the host is not reloading, so the
//         queue keeps money and ownership in one checkpoint) + HomeAddition.bought for the original window
//       wallpaper -> SPrefs.SetInt(pref, value) at once (exactly the original; its rollback behaviour is the original's)
//     The host never needs the hideout scene: HouseRenovator lives in MainScene (GameManager.houseRenovations).
//   Client: the original button -> proposal; accepted -> the ORIGINAL method runs once for its own presentation (buy
//     animation, camera lock, sound, message, achievement, window, SelectHideout reload) with the price credited right
//     before it, so its own deduction nets to zero (the shared balance comes from the wallet mirror). Its key writes stay in
//     the client sandbox (presentation only); the committed state is the authority and is mirrored into the sandbox keys.
//   Presentation (both roles), event-driven on the committed state: the current hideout scene remembers what it was built
//     with; a newly owned garage -> the BoughtSeq scene swap (garageDisable off, garageEnable on, boughtGarage,
//     RefreshLightObjects; no animation / camera lock / money / achievement on the observer); changed wallpapers ->
//     CheckRenovations.CheckAll (the original's own live update); a new addition / repairs -> the original rebuild
//     GM.SelectHideout() for a player who is IN the hideout (deferred while in a car, carrying, caught or in the travel
//     menu; once per scene). A player elsewhere is never interrupted: the sandbox keys make the next original hideout load
//     build the upgraded scene. Joining clients get the state in the campaign snapshot (OverlaySnapshot).
// Cost: per frame two reference compares + the registry head revision; scene objects are looked up only on a state change
// (FindObjectOfType / FindObjectsOfType, the original's own lookups). Network: 3 B per purchase request, 8 B state per change.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    // W1: original money writers whose client call is a host transaction of a mod subsystem (WalletSync leaves them alone).
    internal static class HostTxnHooks
    {
        static readonly HashSet<string> _claimed = new HashSet<string>(StringComparer.Ordinal);
        public static void Claim(string typeDotMethod) { lock (_claimed) _claimed.Add(typeDotMethod); }
        public static void Release(string typeDotMethod) { lock (_claimed) _claimed.Remove(typeDotMethod); }
        public static bool Claims(string typeDotMethod) { lock (_claimed) return _claimed.Contains(typeDotMethod); }
    }

    internal sealed class HideoutSync : IDisposable, IHideoutHost
    {
        public static HideoutSync Instance;
        static readonly string[] Hooked = { "Inventory.BuyGarage", "HouseRenovator.BuyAddition", "HouseRenovator.BuyRepairs", "HouseRenovator.SetWallpaperBedroom" };

        sealed class Pending { public uint Id; public HideoutBuyRequest Req; public object Instance; public MethodInfo Method; public int Price; public double At; }

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        Func<object, object> _gmPs, _gmReno, _psInv; Func<object, List<string>> _gmMission, _gmParcel; Func<object, bool> _gmBought, _gmLoaded, _psInVehicle, _psCaught, _psCarrying, _psOpened;
        Action<object, bool> _setGmBought; Func<object, int> _money; Action<object, int> _setMoney;
        Func<object, Array> _renoAdditions; Func<object, int> _renoAddNr, _renoWallSetting; Action<object, int> _setRenoArray;
        Func<object, string> _addPref; Func<object, int> _addCost; Action<object, bool> _setAddBought;
        Func<object, object> _garageDisable, _garageEnable, _gmDayNight;
        MethodInfo _selectHideout, _setWallSetting, _refreshLights, _checkAll;
        Type _garageT, _checkT;
        readonly Func<object, Array>[] _wallArrays = new Func<object, Array>[5];
        Func<string, int> _prefInt; Action<string, int> _prefSet;

        Pending _pend; bool _resume, _ran;
        object _builtGm; HideoutState _built; bool _hasBuilt;
        object _reloadGm; double _nextReloadTry; bool _retry; uint _frameHead = uint.MaxValue;
        uint _seenHead = uint.MaxValue, _seenEpoch; int _seenRegion = int.MinValue;
        HideoutState _committed; bool _hasCommitted;
        long _requests, _accepted, _refused, _hostBuys, _hostRefused, _applied, _swaps, _wallUpdates, _reloads, _reloadDeferred, _sandboxWrites;
        double _nextStats;
        const double Timeout = 10;
        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool Ready { get { return _ok; } }

        public HideoutSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory");
                var reno = AccessTools.TypeByName("HouseRenovator"); var add = AccessTools.TypeByName("HomeAddition"); var sp = AccessTools.TypeByName("SPrefs");
                _garageT = AccessTools.TypeByName("Garage"); _checkT = AccessTools.TypeByName("CheckRenovations"); var dn = AccessTools.TypeByName("DayNightTime");
                _gmPs = Fast.Getter<object>(gm, "PS"); _gmReno = Fast.Getter<object>(gm, "houseRenovations"); _psInv = Fast.Getter<object>(ps, "invScript");
                _gmMission = Fast.Getter<List<string>>(gm, "mission_strings_to_set"); _gmParcel = Fast.Getter<List<string>>(gm, "parcel_strings_to_set");
                _gmBought = Fast.Getter<bool>(gm, "boughtGarage"); _setGmBought = Fast.Setter<bool>(gm, "boughtGarage"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap");
                _gmDayNight = Fast.Getter<object>(gm, "dayNightScript");
                _money = Fast.Getter<int>(ps, "moneyAm"); _setMoney = Fast.Setter<int>(ps, "moneyAm");
                _psInVehicle = Fast.Getter<bool>(ps, "is_in_vehicle"); _psCaught = Fast.Getter<bool>(ps, "caughtUp"); _psCarrying = Fast.Getter<bool>(ps, "isCarrying"); _psOpened = Fast.Getter<bool>(ps, "openedTownSelection");
                _renoAdditions = Fast.Getter<Array>(reno, "additions"); _renoAddNr = Fast.Getter<int>(reno, "currentAdditionNr"); _renoWallSetting = Fast.Getter<int>(reno, "currentWallpaperSetting");
                _setRenoArray = Fast.Setter<int>(reno, "arrayWallpaper");
                string[] wallFields = { "wallpapersBedroom", "floorsBedroom", "wallsLivingRoom", "floorsLivingRoom", "storageFloor" };
                for (int i = 0; i < 5; i++) _wallArrays[i] = Fast.Getter<Array>(reno, wallFields[i]);
                _addPref = Fast.Getter<string>(add, "additionPref"); _addCost = Fast.Getter<int>(add, "additionCost"); _setAddBought = Fast.Setter<bool>(add, "bought");
                _garageDisable = Fast.Getter<object>(_garageT, "garageDisable"); _garageEnable = Fast.Getter<object>(_garageT, "garageEnable");
                _selectHideout = AccessTools.Method(gm, "SelectHideout", Type.EmptyTypes);
                _setWallSetting = AccessTools.Method(reno, "SetWallpaperSetting", new[] { typeof(int) });
                _refreshLights = AccessTools.Method(dn, "RefreshLightObjects", Type.EmptyTypes);
                _checkAll = AccessTools.Method(_checkT, "CheckAll", Type.EmptyTypes);
                var gi = AccessTools.Method(sp, "GetInt", new[] { typeof(string) }); var si = AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) });
                if (gi != null) _prefInt = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                if (si != null) _prefSet = (Action<string, int>)Delegate.CreateDelegate(typeof(Action<string, int>), si);
                var methods = new Dictionary<string, MethodInfo>
                {
                    { Hooked[0], AccessTools.Method(inv, "BuyGarage", Type.EmptyTypes) }, { Hooked[1], AccessTools.Method(reno, "BuyAddition", Type.EmptyTypes) },
                    { Hooked[2], AccessTools.Method(reno, "BuyRepairs", Type.EmptyTypes) }, { Hooked[3], AccessTools.Method(reno, "SetWallpaperBedroom", Type.EmptyTypes) },
                };
                var missing = new List<string>();
                foreach (var kv in methods) if (kv.Value == null) missing.Add(kv.Key);
                if (_gmPs == null || _gmReno == null || _gmMission == null || _gmParcel == null || _gmBought == null || _setGmBought == null || _money == null || _setMoney == null
                    || _renoAdditions == null || _addPref == null || _addCost == null || _setAddBought == null || _garageDisable == null || _garageEnable == null || _selectHideout == null
                    || _checkAll == null || _prefInt == null || _prefSet == null || _setWallSetting == null || _setRenoArray == null || _psCarrying == null || missing.Count > 0)
                { _log.Warn("HIDEOUT game API incomplete (" + string.Join(", ", missing.ToArray()) + "): shared hideout purchases disabled (Player 2's purchases stay refused)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.hideout");
                var pre = new HarmonyMethod(AccessTools.Method(typeof(HideoutSync), "BuyPrefix")); var post = new HarmonyMethod(AccessTools.Method(typeof(HideoutSync), "BuyPostfix"));
                foreach (var kv in methods) { _h.Patch(kv.Value, pre, post); HostTxnHooks.Claim(kv.Key); }
                if (_host && world.Host != null) world.Host.HideoutHost = this;
                node.RemoteDisconnected += OnDisconnected;
                world.ProposalResultHook9 = OnProposalResult;
                _ok = true; Instance = this;
                _log.Info("HIDEOUT shared hideout ready (" + (_host ? "host: original purchases + Player-2 transactions on the host campaign" : "client: purchases are host transactions") + ", 4 original purchase methods hooked)");
            }
            catch (Exception ex) { _log.Error("HIDEOUT init failed: shared hideout purchases disabled", ex); _ok = false; if (_h != null) { _h.UnpatchSelf(); _h = null; } foreach (var k in Hooked) HostTxnHooks.Release(k); }
        }

        object Gm() { var gm = _world.GameManagerObject; var u = gm as UnityEngine.Object; return u != null && u ? gm : null; }
        object Ps(object gm) { var ps = gm != null ? _gmPs(gm) : null; var u = ps as UnityEngine.Object; return u != null && u ? ps : null; }
        object Reno(object gm) { var r = gm != null ? _gmReno(gm) : null; var u = r as UnityEngine.Object; return u != null && u ? r : null; }
        int Pref(string k) { try { return _prefInt(k); } catch { return 0; } }
        static bool Has(List<string> l, string k) { if (l == null) return false; for (int i = 0; i < l.Count; i++) if (l[i] == k) return true; return false; }

        object Addition(object reno, int i) { var a = reno != null ? _renoAdditions(reno) : null; return a != null && i >= 0 && i < a.Length ? a.GetValue(i) : null; }

        // ======================================================================== state
        // What this instance's keys say (the current scene was built from these in Start).
        HideoutState FromPrefs()
        {
            var s = new HideoutState { Garage = Pref(HideoutShop.GarageKey) >= 1, Repairs = Pref(HideoutShop.RepairsKey) == 1 };
            for (int i = 0; i < HideoutShop.AdditionPrefs.Length; i++) if (Pref(HideoutShop.AdditionPrefs[i]) == 1) s.Additions |= (byte)(1 << i);
            for (int i = 0; i < 5; i++) s.SetWall(i, (byte)Mathf.Clamp(Pref(HideoutShop.WallPrefs[i]), 0, 255));
            return s;
        }

        // IHideoutHost: the host's campaign = its keys + purchases queued for the next checkpoint.
        public bool TryState(out HideoutState s)
        {
            s = default(HideoutState);
            var gm = Gm(); if (gm == null) return false;
            s = FromPrefs();
            var mission = _gmMission(gm); var parcel = _gmParcel(gm);
            if (Has(mission, HideoutShop.GarageKey) || _gmBought(gm)) s.Garage = true;
            if (Has(parcel, HideoutShop.RepairsKey)) s.Repairs = true;
            for (int i = 0; i < HideoutShop.AdditionPrefs.Length; i++) if (Has(parcel, HideoutShop.AdditionPrefs[i])) s.Additions |= (byte)(1 << i);
            return true;
        }

        public bool TryBalance(out int balance)
        {
            balance = 0;
            var w = WalletSync.Instance;
            if (w != null && w.TryHostBalance(out balance)) return true;
            var gm = Gm(); var ps = Ps(gm);
            if (ps == null || !_gmLoaded(gm)) return false;
            balance = _money(ps); return true;
        }

        public int AdditionCost(int index)
        {
            var add = Addition(Reno(Gm()), index);
            if (add == null || _addPref(add) != HideoutShop.AdditionPrefs[index]) return 0;   // the verified table must match the live one
            return _addCost(add);
        }

        public int WallpaperCount(int setting)
        {
            var reno = Reno(Gm()); if (reno == null || setting < 0 || setting >= 5 || _wallArrays[setting] == null) return 0;
            var a = _wallArrays[setting](reno); return a != null ? Math.Min(a.Length, 256) : 0;
        }

        public bool Apply(HideoutBuyRequest r, int price)
        {
            var gm = Gm(); var ps = Ps(gm); if (ps == null) return false;
            int before = _money(ps);
            _setMoney(ps, before - price);   // the original's field and amount
            switch (r.Item)
            {
                case HideoutItem.Garage:
                    var m = _gmMission(gm); if (m != null && !Has(m, HideoutShop.GarageKey)) m.Add(HideoutShop.GarageKey);   // Garage.BoughtSeq
                    _setGmBought(gm, true);
                    break;
                case HideoutItem.Repairs:
                case HideoutItem.Addition:
                    string key = r.Item == HideoutItem.Repairs ? HideoutShop.RepairsKey : HideoutShop.AdditionPrefs[r.Index];
                    var p = _gmParcel(gm); if (p != null && !Has(p, key)) p.Add(key);
                    if (r.Item == HideoutItem.Addition) { var add = Addition(Reno(gm), r.Index); if (add != null) _setAddBought(add, true); }
                    break;
                case HideoutItem.Wallpaper:
                    _prefSet(HideoutShop.WallPrefs[r.Index], r.Value);   // HouseRenovator.SetWallpaperBedroom writes it at once
                    break;
            }
            _applied++;
            _log.Info("HIDEOUT Player-2 purchase " + r + " applied on the host: shared balance " + before + " -> " + (before - price)
                + (r.Item == HideoutItem.Wallpaper ? ", key written (original timing)" : r.Item == HideoutItem.Garage ? ", garage queued for the next host checkpoint (saved as 2)" : ", key queued for the next host checkpoint (saved as 1 with the money)"));
            return true;
        }

        // CampaignBootstrap: a joining client builds its scenes from this snapshot -> include purchases still queued on the host.
        internal void OverlaySnapshot(CampaignData model)
        {
            HideoutState s;
            if (!_ok || !_host || model == null || !TryState(out s)) return;
            int g; if (s.Garage && (!model.TryInt(HideoutShop.GarageKey, out g) || g < 1)) model.Ints[HideoutShop.GarageKey] = 2;
            if (s.Repairs) model.Ints[HideoutShop.RepairsKey] = 1;
            for (int i = 0; i < HideoutShop.AdditionPrefs.Length; i++) if (s.Has(i)) model.Ints[HideoutShop.AdditionPrefs[i]] = 1;
            for (int i = 0; i < 5; i++) model.Ints[HideoutShop.WallPrefs[i]] = s.Wall(i);
        }

        // ======================================================================== Harmony: the four original purchases
        static bool BuyPrefix(object __instance, MethodBase __originalMethod)
        {
            var h = Instance; if (h == null || !h._ok) return true;
            try { return h.OnBuy(__instance, __originalMethod); }
            catch (Exception ex) { h._log.Error("HIDEOUT purchase hook failed (nothing charged)", ex); return h._host; }   // client fails closed
        }

        static void BuyPostfix(object __instance, MethodBase __originalMethod)
        {
            var h = Instance; if (h == null || !h._ran) return;
            h._ran = false;
            try { if (h._host) { h._hostBuys++; if (h._world.Host != null && h._world.Host.PublishHideout()) h._world.FlushOutbox(); h._log.Info("HIDEOUT host purchase " + __originalMethod.Name + " (original) -> shared state " + h.Committed()); } }
            catch (Exception ex) { h._log.Error("HIDEOUT purchase postfix failed", ex); }
        }

        string Committed() { HideoutState s; return TryState(out s) ? s.ToString() : "?"; }

        bool OnBuy(object inst, MethodBase m)
        {
            if (_resume) { _ran = true; return true; }   // accepted: the original runs once for its presentation
            var gm = Gm(); var ps = Ps(gm);
            HideoutBuyRequest r; int price;
            if (!Build(inst, m, gm, out r, out price)) { _log.Info("HIDEOUT " + m.Name + " not understood (nothing charged)"); return false; }
            HideoutState s; bool known = _host ? TryState(out s) : TryCommitted(out s);
            if (known && Owned(s, r)) { if (_host) _hostRefused++; _log.Info("HIDEOUT " + r + " refused: already owned in the shared hideout (nothing charged)"); return false; }
            if (_host)
            {
                _ran = true;
                if (r.Item == HideoutItem.Addition || r.Item == HideoutItem.Repairs) _reloadGm = gm;   // the original reloads the hideout itself
                return true;
            }
            // client: below the price the original only shows its own "too poor" page (no money change) -> keep it
            if (ps != null && r.Item != HideoutItem.Garage && _money(ps) < price) return true;
            Request(r, price, inst, m as MethodInfo);
            return false;
        }

        static bool Owned(HideoutState s, HideoutBuyRequest r)
        {
            switch (r.Item)
            {
                case HideoutItem.Garage: return s.Garage;
                case HideoutItem.Repairs: return s.Repairs;
                case HideoutItem.Addition: return s.Has(r.Index);
                default: return s.Wall(r.Index) == r.Value;
            }
        }

        bool Build(object inst, MethodBase m, object gm, out HideoutBuyRequest r, out int price)
        {
            r = default(HideoutBuyRequest); price = 0;
            switch (m.Name)
            {
                case "BuyGarage": r = new HideoutBuyRequest { Item = HideoutItem.Garage }; price = HideoutShop.GaragePrice; return true;
                case "BuyRepairs": r = new HideoutBuyRequest { Item = HideoutItem.Repairs }; price = HideoutShop.RepairsPrice; return true;
                case "BuyAddition":
                    int n = _renoAddNr(inst); var add = Addition(inst, n);
                    if (add == null || n >= HideoutShop.AdditionPrefs.Length || _addPref(add) != HideoutShop.AdditionPrefs[n]) return false;
                    r = new HideoutBuyRequest { Item = HideoutItem.Addition, Index = (byte)n }; price = _addCost(add); return price > 0;
                case "SetWallpaperBedroom":
                    int setting = _renoWallSetting(inst);
                    var f = AccessTools.Field(inst.GetType(), "arrayWallpaper"); int v = f != null ? (int)f.GetValue(inst) : -1;
                    if (setting < 0 || setting >= 5 || v < 0 || v > 255) return false;
                    r = new HideoutBuyRequest { Item = HideoutItem.Wallpaper, Index = (byte)setting, Value = (byte)v }; price = HideoutShop.WallpaperPrice; return true;
            }
            return false;
        }

        void Request(HideoutBuyRequest r, int price, object inst, MethodInfo m)
        {
            if (_pend != null) { _log.Info("HIDEOUT purchase click ignored: request #" + _pend.Id + " still waiting for the host"); return; }
            if (!_node.IsConnected || !_world.Synced) { _log.Info("HIDEOUT purchase click ignored: not connected (nothing charged)"); return; }
            uint id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.HideoutBuy, Value = r.ToBytes() });
            _pend = new Pending { Id = id, Req = r, Instance = inst, Method = m, Price = price, At = Now };
            _requests++;
            _log.Info("HIDEOUT purchase " + r + " requested #" + id + " (" + price + " from the shared balance, decided by the host)");
        }

        internal bool OnProposalResult(uint id, ProposalCode code)
        {
            if (_host || _pend == null || id != _pend.Id) return false;
            var p = _pend; _pend = null;
            if (code != ProposalCode.Accepted) { _refused++; _log.Info("HIDEOUT purchase #" + id + " " + p.Req + " refused by the host: " + code + " (nothing charged)"); return true; }
            _accepted++;
            var gm = Gm(); var ps = Ps(gm); var u = p.Instance as UnityEngine.Object;
            if (ps == null || u == null || !u || _world.LocalRegion != HideoutShop.Region) { _log.Info("HIDEOUT purchase #" + id + " accepted; purchase window gone, original presentation skipped (the shared state applies it)"); return true; }
            _log.Info("HIDEOUT purchase #" + id + " " + p.Req + " accepted: the host charged the shared balance; original presentation runs here");
            if (p.Req.Item == HideoutItem.Wallpaper && _renoWallSetting(p.Instance) != p.Req.Index) _setWallSetting.Invoke(p.Instance, new object[] { (int)p.Req.Index });
            if (p.Req.Item == HideoutItem.Wallpaper) _setRenoArray(p.Instance, p.Req.Value);
            int before = _money(ps);
            ApplySandbox();                                     // keys first: the original's own reload / CheckAll reads them
            _resume = true; _reloadGm = gm;                     // BuyAddition / BuyRepairs reload by themselves
            _setMoney(ps, before + p.Price);                    // the original's own deduction nets to zero
            try { p.Method.Invoke(p.Instance, null); }
            catch (Exception ex) { _log.Warn("HIDEOUT original purchase presentation failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            finally { _resume = false; _ran = false; if (_money(ps) != before) _setMoney(ps, before); }
            return true;
        }

        bool TryCommitted(out HideoutState s)
        {
            ReadCommitted();
            s = _committed; return _hasCommitted;
        }

        void ReadCommitted()
        {
            var reg = _world.Registry;
            if (reg.HeadRev == _seenHead && reg.Epoch == _seenEpoch) return;
            _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch;
            var e = reg.Get(StateScope.Session, 0, WorldKeys.Hideout);
            HideoutState s;
            if (e != null && HideoutState.TryParse(e.Value, out s)) { _committed = s; _hasCommitted = true; }
        }

        // Client: the sandbox keys follow the committed state exactly (presentation input of every original hideout Start).
        void ApplySandbox()
        {
            if (_host || !_hasCommitted) return;
            var s = _committed; int n = 0;
            int g = Pref(HideoutShop.GarageKey);
            if (s.Garage && g < 1) { _prefSet(HideoutShop.GarageKey, 2); n++; } else if (!s.Garage && g >= 1) { _prefSet(HideoutShop.GarageKey, 0); n++; }
            n += SetIf(HideoutShop.RepairsKey, s.Repairs ? 1 : 0);
            for (int i = 0; i < HideoutShop.AdditionPrefs.Length; i++) n += SetIf(HideoutShop.AdditionPrefs[i], s.Has(i) ? 1 : 0);
            for (int i = 0; i < 5; i++) n += SetIf(HideoutShop.WallPrefs[i], s.Wall(i));
            if (n > 0) { _sandboxWrites += n; _log.Info("HIDEOUT sandbox keys follow the host hideout (" + n + " key(s)): " + s); }
        }

        int SetIf(string k, int v) { if (Pref(k) == v) return 0; _prefSet(k, v); return 1; }

        // ======================================================================== per frame
        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            if (now >= _nextStats) { _nextStats = now + 30; if (_requests + _hostBuys + _applied + _swaps + _reloads > 0) _log.Info(Stats); }
            if (_pend != null && now - _pend.At > Timeout) { _log.Warn("HIDEOUT purchase request #" + _pend.Id + " timed out (the host decides alone; the shared state shows the result)"); _pend = null; }
            var gm = Gm();
            if (gm == null) return;
            int region = _world.LocalRegion;
            if (!ReferenceEquals(gm, _builtGm))
            {
                if (!_gmLoaded(gm)) return;   // the original Start methods have not read the keys yet
                _builtGm = gm; _built = FromPrefs(); _hasBuilt = true; _seenRegion = int.MinValue;
                if (_host && _world.Host != null && _world.Host.PublishHideout()) _world.FlushOutbox();
            }
            uint head = _world.Registry.HeadRev;
            bool changed = head != _frameHead || region != _seenRegion;
            if (!changed && (!_retry || now < _nextReloadTry)) return;
            _frameHead = head; _seenRegion = region; _retry = false;
            ReadCommitted();
            if (!_hasCommitted) return;
            if (changed) ApplySandbox();
            if (region != HideoutShop.Region || !_hasBuilt || _pend != null) return;   // elsewhere: the next original hideout load builds it
            Present(gm, now);
        }

        void Present(object gm, double now)
        {
            var s = _committed;
            if (s.Garage && !_built.Garage)
            {
                var g = UnityEngine.Object.FindObjectOfType(_garageT);
                if (g != null)
                {
                    var off = _garageDisable(g) as GameObject; var on = _garageEnable(g) as GameObject;
                    if (off != null) off.SetActive(false);
                    if (on != null) on.SetActive(true);
                    _setGmBought(gm, true);
                    var dn = _gmDayNight != null ? _gmDayNight(gm) as UnityEngine.Object : null;
                    if (dn != null && _refreshLights != null) try { _refreshLights.Invoke(dn, null); } catch { }
                    _swaps++;
                    _log.Info("HIDEOUT garage bought by the other player: original garage scene swap applied here (Garage.BoughtSeq objects, lights)");
                }
                _built.Garage = true;
            }
            if (s.WallsDiffer(_built))
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType(_checkT)) try { _checkAll.Invoke(c, null); } catch { }
                for (int i = 0; i < 5; i++) _built.SetWall(i, s.Wall(i));
                _wallUpdates++;
                _log.Info("HIDEOUT wallpapers / floors updated live (CheckRenovations.CheckAll): " + s);
            }
            if (s.NeedsRebuild(_built) && !ReferenceEquals(_reloadGm, gm))
            {
                if (now < _nextReloadTry) return;
                var ps = Ps(gm); string wait = null;
                if (ps == null || !_gmLoaded(gm)) wait = "scene loading";
                else if (_psInVehicle(ps)) wait = "in a car";
                else if (_psCarrying(ps)) wait = "carrying an object";
                else if (_psCaught(ps)) wait = "caught";
                else if (_psOpened != null && _psOpened(ps)) wait = "travel menu open";
                if (wait != null) { _nextReloadTry = now + 1; _retry = true; if (_reloadDeferred++ % 30 == 0) _log.Info("HIDEOUT rebuild for the other player's renovation waits (" + wait + ")"); return; }
                _reloadGm = gm; _reloads++;
                _log.Info("HIDEOUT renovation by the other player (" + s + "): original hideout rebuild GameManager.SelectHideout (built with " + _built + ")");
                try { _selectHideout.Invoke(gm, null); }
                catch (Exception ex) { _log.Error("HIDEOUT original hideout rebuild failed", ex); }
            }
        }

        void OnDisconnected(string reason) { if (_pend != null) { _log.Info("HIDEOUT purchase request #" + _pend.Id + " dropped: disconnected"); _pend = null; } }

        public string Stats
        {
            get
            {
                return "HIDEOUT_STATS " + (_host ? "host" : "client") + " req " + _requests + " ok " + _accepted + " refused " + _refused + " hostBuys " + _hostBuys + " hostRefused " + _hostRefused
                    + " applied " + _applied + " swaps " + _swaps + " wallUpdates " + _wallUpdates + " rebuilds " + _reloads + " deferred " + _reloadDeferred + " sandboxWrites " + _sandboxWrites
                    + (_hasCommitted ? " | " + _committed : "");
            }
        }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_ok) { _node.RemoteDisconnected -= OnDisconnected; _world.ProposalResultHook9 = null; }
            if (_world.Host != null && ReferenceEquals(_world.Host.HideoutHost, this)) _world.Host.HideoutHost = null;
            foreach (var k in Hooked) HostTxnHooks.Release(k);
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
