// V1.2: shared Shadowhawk purchase / unlock and the Auto Shop travel-only proxy (partial of VehicleSync).
// Verified original (IL 1.27c, Tutorial.BuyVan / OpenBuyVan / DestroyIfPref, VehiclesManager.InitiateMainCar; scene scan level5):
//   The purchase sign "BUY VAN OBJ" exists only in the Auto Shop and only while garage >= 1 and VanBought == 0. BuyVan queues
//   VehiclesManager.vanBoughtPref in GameManager.parcel_strings_to_set (set to 1 by the next SaveGameNewSystem), charges
//   moneyAm -= 25000, shows "bought_van", plays BuySound, sets achievement BUY_MUSTANG and switches the sign off. The car is
//   not activated: InitiateMainCar shows it at secondCarHideoutPos on a later hideout load.
// Root causes fixed here:
//   1. Player 2: WalletSync refused Tutorial.BuyVan on the client (MoneyPolicy.Refuse) -> the button did nothing.
//      Now: client prefix -> host transaction "vbuy" (WorldHost.BuyVehicle validates car, region, garage, balance; charges
//      the host's moneyAm = shared balance and queues the original unlock key ONCE), accepted -> the original BuyVan runs on
//      the client for its presentation (TurnOff, message, sound, achievement, sign) and its own money / key changes are
//      undone in the same call (the shared balance arrives through the wallet mirror).
//   2. Player 1: EnsureHostFleet registered Unlocked only once per profile (from the pref) -> the fleet record stayed locked
//      after a purchase, PresentationRules hid the car (Hidden) even after InitiateMainCar activated it. Now the host derives
//      the unlock from its own unlock state (pref or queued parcel string) after its purchase and at every scene bind
//      (a checkpoint reload that drops the unsaved purchase also drops the unlock, together with the money).
//   A newly owned car that the original did not place (no InitiateMainCar activation on this instance) is placed at the
//   original second-car spot (secondCarHideoutPos, else SpawnHideoutCar) away from the other cars and the local player.
// Auto Shop proxy: PresentationRules.AutoShopProxyCar picks one owned car elsewhere while no fleet car is in the Auto Shop;
//   it is presented as AwayProxy (frozen original object, storage/cargo off, E -> original travel menu, on-foot travel) at
//   the spot where that car was last presented here, else at the original Auto Shop car spawn GameManager.SpawnGarageCar.
// Cost: event-driven (proposal / registry change / scene bind); the sign lookup (GameObject.Find) runs at most once per
//   second, only in the Auto Shop, only until found (<= 30 tries per scene). No per-frame allocation.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed partial class VehicleSync : IVehicleShopHost
    {
        internal static VehicleSync Current;   // H2 cargo: no cargo on a car that is only a local travel proxy here

        // The car is presented here only as a frozen travel access point (AwayProxy), not as the car itself.
        internal bool IsTravelProxy(ushort id) { foreach (var lc in _local) if (lc.Id == id) return lc.Active && lc.IsProxy && !lc.Observed; return false; }

        // V1.4.1 medkit: the fleet id of an original CarController (Medkit.myCar) and whether it is only a travel proxy here.
        internal bool CarOf(object car, out ushort id, out bool proxy)
        {
            id = VehicleIds.None; proxy = false;
            foreach (var lc in _local) if (ReferenceEquals(lc.Car, car)) { id = lc.Id; proxy = lc.IsProxy && !lc.Observed; return true; }
            return false;
        }
        // host-committed fleet flag EngineOn (both PCs; the holder reports it)
        internal bool EngineOn(ushort id) { VehicleRecord r; return Rec(id, out r) && (r.Flags & VehicleFlags.EngineOn) != 0; }

        sealed class BuyReq { public uint ProposalId; public UnityEngine.Object Tutorial; public double At; }

        Func<object, List<string>> _gmParcel, _gmMission;
        Func<object, int> _psMoney; Action<object, int> _setPsMoney;
        Func<object, UnityEngine.Object> _gmGarageCar;
        MethodInfo _buyVanMi;
        BuyReq _buy; bool _buyResume, _buyRan;
        object _signGm; double _signNext; int _signTries;
        ushort _shopSeen;   // car last presented (real / observed) in the Auto Shop during this stay
        long _buyRequests, _buyAccepted, _buyRefused, _hostBuys, _hostBuyRefused, _unlockChanges, _shopProxies, _placedNew;
        const double BuyTimeout = 10;

        void ResolveShopApi()
        {
            var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var tut = AccessTools.TypeByName("Tutorial");
            _gmParcel = Fast.Getter<List<string>>(gm, "parcel_strings_to_set");
            _gmMission = Fast.Getter<List<string>>(gm, "mission_strings_to_set");
            _psMoney = Fast.Getter<int>(ps, "moneyAm"); _setPsMoney = Fast.Setter<int>(ps, "moneyAm");
            _gmGarageCar = Fast.Getter<UnityEngine.Object>(gm, "SpawnGarageCar");
            _buyVanMi = tut != null ? AccessTools.Method(tut, "BuyVan", Type.EmptyTypes) : null;
            if (_gmParcel == null || _psMoney == null || _setPsMoney == null || _buyVanMi == null) _log.Warn("VEHICLE purchase API incomplete (parcel list / money / Tutorial.BuyVan): shared Shadowhawk purchase unavailable");
        }

        bool ShopApi { get { return _gmParcel != null && _psMoney != null && _setPsMoney != null && _buyVanMi != null; } }

        static bool ListHas(List<string> l, string k) { if (l == null || k == null) return false; for (int i = 0; i < l.Count; i++) if (l[i] == k) return true; return false; }

        string VanPref(object gm)
        {
            var vm = gm != null ? _gmVM(gm) : null;
            var p = vm != null && _vanPref != null ? _vanPref(vm) : null;
            return string.IsNullOrEmpty(p) ? "VanBought" : p;
        }

        bool VanOwned() { VehicleRecord r; return Rec(VehicleShop.Vehicle, out r) && r.Unlocked; }

        // ======================================================================== host: unlock state -> fleet record
        // Unlocked = the host save already says so (VanBought == 1) or the original purchase is queued for the next checkpoint.
        void SyncHostUnlock(string why)
        {
            if (!_host || !_fleetReady || _world.Host == null) return;
            var gm = _world.GameManagerObject; if (gm == null) return;
            string pref = VanPref(gm);
            bool queued = _gmParcel != null && ListHas(_gmParcel(gm), pref);
            bool unlocked = Pref(pref) >= 1 || queued;
            if (!_world.Host.SetUnlocked(VehicleShop.Vehicle, unlocked)) return;
            _unlockChanges++; _fleetDirty = true; _fleetUrgent = true; _reconcile = true;
            _world.FlushOutbox();
            _log.Info("VEHICLE " + CarName(VehicleShop.Vehicle) + (unlocked ? " unlocked" : " locked again") + " in the shared fleet (" + why + ": " + pref + "=" + Pref(pref) + (queued ? ", purchase queued for the next checkpoint" : "") + ")");
        }

        // IVehicleShopHost (WorldHost.BuyVehicle on a client request)
        public bool TryBalance(out int balance)
        {
            balance = 0;
            var w = WalletSync.Instance;
            if (w != null && w.TryHostBalance(out balance)) return true;
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null;
            if (ps == null || _psMoney == null || !_gmLoaded(gm)) return false;
            balance = _psMoney(ps); return true;
        }

        public bool GarageOwned
        {
            get { var gm = _world.GameManagerObject; return Pref("garage") >= 1 || (gm != null && _gmMission != null && ListHas(_gmMission(gm), "garage")); }
        }

        public bool Charge(ushort vehicle, int price)
        {
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null;
            if (ps == null || !ShopApi) return false;
            var parcel = _gmParcel(gm); if (parcel == null) return false;
            string pref = VanPref(gm);
            int before = _psMoney(ps);
            _setPsMoney(ps, before - price);                 // the same field and amount as Tutorial.BuyVan IL (moneyAm -= 25000)
            if (!ListHas(parcel, pref)) parcel.Add(pref);   // the same unlock key, written by the host's next SaveGameNewSystem
            _log.Info("VEHICLE purchase of " + CarName(vehicle) + " by Player 2 charged on the host: shared balance " + before + " -> " + (before - price) + ", " + pref + " queued for the next host checkpoint");
            HideBuySign(gm, true);
            return true;
        }

        // ======================================================================== TravelHooks: Tutorial.BuyVan
        public bool AllowBuyVan(object tutorial)
        {
            if (_host)
            {
                if (_world.Host != null && _fleetReady)
                {
                    VehicleRecord r;
                    if (_world.Host.Fleet.TryGet(VehicleShop.Vehicle, out r) && r.Unlocked)
                    {
                        _hostBuyRefused++;
                        _log.Info("VEHICLE purchase of " + CarName(VehicleShop.Vehicle) + " by the host refused: already owned in the shared fleet (no charge)");
                        return false;
                    }
                }
                _buyRan = true; return true;
            }
            if (_buyResume) { _buyRan = true; return true; }   // accepted by the host: the original presentation runs once
            RequestBuy(tutorial as UnityEngine.Object);
            return false;
        }

        public void AfterBuyVan(object tutorial)
        {
            if (!_buyRan) return;   // postfixes also run when the prefix skipped the original
            _buyRan = false;
            if (!_host) return;     // client: BuyResult undoes the local money / key change (also when the original throws)
            _hostBuys++;
            SyncHostUnlock("host purchase Tutorial.BuyVan");
        }

        void RequestBuy(UnityEngine.Object tutorial)
        {
            if (_buy != null) { _log.Info("VEHICLE purchase click ignored: request #" + _buy.ProposalId + " still waiting for the host"); return; }
            if (!ShopApi || !_node.IsConnected || !_world.Synced) { _log.Info("VEHICLE purchase click ignored: not connected / purchase API missing (nothing charged)"); return; }
            if (VanOwned()) { _log.Info("VEHICLE purchase click ignored: " + CarName(VehicleShop.Vehicle) + " is already owned in the shared fleet (nothing charged)"); return; }
            var e = new StateEntry { Scope = StateScope.Session, ScopeArg = VehicleShop.Vehicle, Key = WorldKeys.VehicleBuy, Value = new byte[] { 1 } };
            uint id = _world.SendProposal(e);
            _buy = new BuyReq { ProposalId = id, Tutorial = tutorial, At = Now };
            _buyRequests++;
            _log.Info("VEHICLE purchase of " + CarName(VehicleShop.Vehicle) + " requested #" + id + " (" + VehicleShop.Price + " from the shared balance, decided by the host)");
        }

        bool BuyResult(uint id, ProposalCode code)
        {
            if (_host || _buy == null || id != _buy.ProposalId) return false;
            var req = _buy; _buy = null;
            if (code != ProposalCode.Accepted)
            {
                _buyRefused++;
                _log.Info("VEHICLE purchase #" + id + " refused by the host: " + code + " (nothing charged, the original window stays as it is)");
                return true;
            }
            _buyAccepted++;
            _log.Info("VEHICLE purchase #" + id + " accepted: the host charged the shared balance and unlocked " + CarName(VehicleShop.Vehicle));
            var gm = _world.GameManagerObject; var ps = gm != null ? _gmPS(gm) : null;
            if (req.Tutorial == null || ps == null || _world.LocalRegion != VehicleShop.ShopRegion) { _log.Info("VEHICLE purchase #" + id + ": purchase window gone, original presentation skipped"); return true; }
            var parcel = _gmParcel(gm); string pref = VanPref(gm);
            bool had = ListHas(parcel, pref); int money = _psMoney(ps);
            _buyResume = true;
            try { _buyVanMi.Invoke(req.Tutorial, null); }
            catch (Exception ex) { _log.Warn("VEHICLE original purchase presentation failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); }
            finally
            {
                _buyResume = false; _buyRan = false;
                // the shared balance is the host's: never a local money change (the mirror shows the host value), never a key
                // in the client sandbox (the host save is the only purchase record)
                if (_psMoney(ps) != money) _setPsMoney(ps, money);
                if (!had && parcel != null) parcel.Remove(pref);
            }
            return true;
        }

        void BuyFrame(double now)
        {
            if (_buy != null && now - _buy.At > BuyTimeout) { _log.Warn("VEHICLE purchase request #" + _buy.ProposalId + " timed out (the host decides alone; the shared fleet shows the result)"); _buy = null; }
        }

        void DropBuy(string why) { if (_buy == null) return; _log.Info("VEHICLE purchase request #" + _buy.ProposalId + " dropped: " + why); _buy = null; }

        // DestroyIfPref only runs in Start: once the car is owned in the shared fleet, the Auto Shop sign of THIS instance is
        // switched off like the original after a purchase (the other player's sandbox / an unsaved host key would keep it).
        void SignFrame(object gm, int region, double now)
        {
            if (region != VehicleShop.ShopRegion || gm == null || ReferenceEquals(_signGm, gm) || now < _signNext || _buy != null) return;   // own purchase pending: the original switches it off
            if (!VanOwned()) { _signNext = now + 1; return; }
            HideBuySign(gm, false);
        }

        void HideBuySign(object gm, bool once)
        {
            if (_world.LocalRegion != VehicleShop.ShopRegion || ReferenceEquals(_signGm, gm)) return;
            var go = GameObject.Find("BUY VAN OBJ");
            if (go != null) { go.SetActive(false); _signGm = gm; _log.Info("VEHICLE Auto Shop purchase sign switched off (" + CarName(VehicleShop.Vehicle) + " owned in the shared fleet)"); return; }
            if (once) return;
            _signNext = Now + 1;
            if (++_signTries >= 30) _signGm = gm;   // the original DestroyIfPref already removed it
        }

        // ======================================================================== presentation helpers
        // Hideout: a car the original did not activate on this instance (bought during the session, or not bought in the
        // client's sandbox) has no valid scene pose -> the original second-car spot, else the hideout car spawn; the first
        // spot clear of the other cars and of the local player wins.
        void PlaceNewHideoutCar(object gm, LocalCar lc)
        {
            var vm = gm != null ? _gmVM(gm) : null;
            var a = vm != null && _vmSecondPos != null ? _vmSecondPos(vm) as Transform : null;
            var b = gm != null && _gmHideoutCar != null ? _gmHideoutCar(gm) as Transform : null;
            var ps = gm != null ? _gmPS(gm) as Component : null;
            Transform pick = null;
            foreach (var sp in new[] { a, b })
            {
                if (sp == null || Overlaps(lc, sp.position)) continue;
                if (ps != null && Flat(ps.transform.position, sp.position) < 3.5f) { if (pick == null) pick = sp; continue; }
                pick = sp; break;
            }
            if (pick == null) pick = a ?? b;
            if (pick == null) return;
            lc.T.SetPositionAndRotation(pick.position, pick.rotation);
            if (lc.Rb != null) { lc.Rb.velocity = Vector3.zero; lc.Rb.angularVelocity = Vector3.zero; }
            lc.Placed = true; _placedNew++;
            _log.Info("VEHICLE " + CarName(lc.Id) + " placed at the original hideout spot " + pick.name + " (not activated by the original on this instance)");
        }

        // Auto Shop proxy spot: where the car was last presented during this stay, else the original car spawn.
        void PlaceShopProxy(object gm, LocalCar lc)
        {
            var spawn = gm != null && _gmGarageCar != null ? _gmGarageCar(gm) as Transform : null;
            bool keep = lc.Id == _shopSeen && (spawn == null || Flat(lc.T.position, spawn.position) <= 60f);
            if (!keep && spawn != null) lc.T.SetPositionAndRotation(spawn.position, spawn.rotation);
            if (lc.Rb != null && !lc.Rb.isKinematic) { lc.Rb.velocity = Vector3.zero; lc.Rb.angularVelocity = Vector3.zero; }
            _shopProxies++;
            _log.Info("VEHICLE " + CarName(lc.Id) + " Auto Shop travel proxy " + (keep ? "at its last spot here" : "at the original Auto Shop car spawn" + (spawn != null ? " " + spawn.name : " (missing: kept in place)")) + ": E opens the travel menu, the car stays where it is");
        }

        string ShopStats { get { return " buy req " + _buyRequests + " ok " + _buyAccepted + " refused " + _buyRefused + " hostBuys " + _hostBuys + " hostRefused " + _hostBuyRefused + " unlockChanges " + _unlockChanges + " shopProxies " + _shopProxies + " placedNew " + _placedNew + " hideoutLayout passes/moves " + _layoutPasses + "/" + _layoutMoves; } }
    }
}
