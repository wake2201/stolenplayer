// W4.1B shared money on the ORIGINAL money field (design + verified IL: src/Shared/Wallet.cs). Host-side money application,
// journal and replay live with the Player-2 record in Player2Sync; this class holds the hooks and the client mirror.
//   Host: Harmony prefix on SPrefs.SetInt(string,int): when the original writes "Money" (SaveGame.SaveGameNewSystem IL_0462,
//     value = moneyAm), Player2Sync writes the wallet watermark into the same save memory.
//   Client: PlayerScript.moneyAm mirrors the host's shared balance (registry "wallet"); every other change of the field is one
//     of Player 2's own original money changes and is counted into the record's Wallet counter (reported with the backpack).
//     Policy per original money writer (WalletPolicy table, all methods verified by a Cecil scan):
//       Purchase (Shop_Manager.Buy_Item): prefix/postfix measure the price -> Spend counter, report immediately.
//       Exclude: prefix/postfix revert the method's own money change (mission/job/contract/payphone rewards, pickpocket,
//         PickUpMoney); SaveGame.Update reverts only the rent branch; SaveGameNewSystem reverts only the negative clamp;
//         HiddenItem.PickIt arms a one-shot exclusion for the +100 that PlayerScript.Update adds right after it.
//       Refuse: prefix skips the original (host / world-owned purchases): nothing happens, like a refused interaction.
// Per frame (client): one int read + compare, registry parse only when its head revision changes. No scene search, no LINQ.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class WalletSync : IDisposable
    {
        public static WalletSync Instance;

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        Func<object, object> _gmPs, _psGm, _sgPs; Func<object, int> _money, _rentTimer, _carDamages; Action<object, int> _setMoney; Func<object, bool> _survival;

        readonly ClientWallet _cw = new ClientWallet();
        WalletState _state; bool _hasState; uint _seenHead = uint.MaxValue, _seenEpoch; bool _seenSnap;
        readonly HashSet<string> _logged = new HashSet<string>();
        long _refused, _excluded, _excludedAmount, _rentReverted, _clampReverted, _parseErrors;
        double _nextStats, _mirrorMaxMs;
        static double Now { get { return Time.realtimeSinceStartup; } }

        public bool IsClient { get { return _ok && !_host; } }
        public bool ClientActive { get { return IsClient && _cw.Active; } }

        public WalletSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var sg = AccessTools.TypeByName("SaveGame");
                _gmPs = Fast.Getter<object>(gm, "PS"); _psGm = Fast.Getter<object>(ps, "GM"); _sgPs = Fast.Getter<object>(sg, "PS");
                _money = Fast.Getter<int>(ps, "moneyAm"); _setMoney = Fast.Setter<int>(ps, "moneyAm");
                _rentTimer = Fast.Getter<int>(sg, "timeTillRentPayment"); _carDamages = Fast.Getter<int>(gm, "carDamagesValue"); _survival = Fast.Getter<bool>(gm, "isSurvivalMode");
                if (_gmPs == null || _psGm == null || _sgPs == null || _money == null || _setMoney == null || _rentTimer == null || _carDamages == null || _survival == null)
                { _log.Warn("WALLET game API incomplete: shared money disabled"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.wallet");
                if (_host)
                {
                    var sp = AccessTools.TypeByName("SPrefs");
                    var setInt = sp != null ? AccessTools.Method(sp, "SetInt", new[] { typeof(string), typeof(int) }) : null;
                    if (setInt == null) { _log.Warn("WALLET SPrefs.SetInt not found: shared money disabled (no save watermark)"); return; }
                    _h.Patch(setInt, new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "SetIntPrefix")));
                }
                else if (!InstallClientPolicy(sg)) return;
                _ok = true; Instance = this;
                _log.Info("WALLET shared money ready (" + (_host ? "host: PlayerScript.moneyAm is the shared balance" : "client: money mirrors the host balance, own changes reported") + ")");
            }
            catch (Exception ex) { _log.Error("WALLET init failed: shared money disabled", ex); _ok = false; }
        }

        // Every Refuse / Exclude / Purchase writer must be patchable; a missing one would leave a money path unaccounted -> off.
        bool InstallClientPolicy(Type sg)
        {
            int n = 0; var missing = new List<string>();
            foreach (var w in WalletPolicy.Writers)
            {
                if (w.Policy == MoneyPolicy.Count || w.Policy == MoneyPolicy.Load) continue;
                if (w.Policy == MoneyPolicy.HostTxn && HostTxnHooks.Claims(w.Type + "." + w.Method)) { n++; continue; }   // V1.2 vbuy / W1 hbuy own it
                var t = AccessTools.TypeByName(w.Type);
                var m = t != null ? AccessTools.Method(t, w.Method) : null;
                if (m == null) { missing.Add(w.Type + "." + w.Method); continue; }
                if (w.Type == "SaveGame" && w.Method == "Update") _h.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "RentPrefix")), new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "RentPostfix")));
                else if (w.Type == "SaveGame" && w.Method == "SaveGameNewSystem")
                    _h.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "SavePrefix")), new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "SavePostfix")) { priority = Priority.First });
                else if (w.Type == "HiddenItem") _h.Patch(m, null, new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "HiddenPostfix")));
                else if (w.Policy == MoneyPolicy.Purchase) _h.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "BuyPrefix")), new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "BuyPostfix")));
                else if (w.Policy == MoneyPolicy.Exclude) _h.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "ExcludePrefix")), new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "ExcludePostfix")));
                else _h.Patch(m, new HarmonyMethod(AccessTools.Method(typeof(WalletSync), "RefusePrefix")));
                n++;
            }
            if (missing.Count > 0) { _log.Warn("WALLET money writer(s) not found (" + string.Join(", ", missing.ToArray()) + "): shared money disabled on the client"); _h.UnpatchSelf(); return false; }
            _log.Info("WALLET client policy installed on " + n + " original money writer(s)");
            return true;
        }

        object Ps() { var gm = _world.GameManagerObject; var ps = gm != null ? _gmPs(gm) : null; var u = ps as UnityEngine.Object; return u != null && u ? ps : null; }

        // ======================================================================== host
        static void SetIntPrefix(string __0)
        {
            if (!string.Equals(__0, "Money", StringComparison.Ordinal)) return;
            var p2 = Player2Sync.Instance;
            if (p2 != null) try { p2.OnMoneyKeyWritten(); } catch (Exception ex) { if (Instance != null) Instance._log.Error("WALLET watermark write failed", ex); }
        }

        internal bool TryHostBalance(out int balance) { balance = 0; var p2 = Player2Sync.Instance; return _host && _ok && p2 != null && p2.TryHostBalance(out balance); }

        // ======================================================================== client mirror
        internal void ClientRestore(object ps, P2Inventory rec)
        {
            if (!IsClient || ps == null || rec == null) return;
            _cw.Restore(rec.Id, rec.Wallet, rec.Spend, _money(ps));
            ReadState();
            Mirror(ps, "restore");
            _log.Info("WALLET mirror active: record " + rec.Id + " counter " + rec.Wallet + " spend " + rec.Spend + (_hasState ? ", " + _state : ", waiting for the host balance") + " -> money " + _money(ps)
                + (rec.Money != 0 || rec.Bank != 0 ? " (legacy personal money " + rec.Money + " / bank " + rec.Bank + " kept untouched, not added)" : ""));
        }

        internal void ClientSceneLeft() { if (_cw.Active) _cw.Deactivate(); }

        internal bool TryClientCounter(out int local) { local = _cw.Local; return ClientActive; }
        internal void ArmClientExclusion(int amount, double seconds) { if (ClientActive) _cw.ArmExclusion(amount, Now, seconds); }
        internal void DisarmClientExclusion() { _cw.Disarm(); }

        // W4.1B loot: a host-credited world money pickup whose original coroutine could not add the amount (object gone /
        // coroutine stopped): the amount is added to the live field once and counted like the original's own addition.
        internal bool ClientCredit(int amount)
        {
            if (!ClientActive || amount <= 0) return false;
            var ps = Ps(); if (ps == null) return false;
            Observe(ps);
            _setMoney(ps, WalletRules.Apply(_money(ps), amount));
            Observe(ps);
            return true;
        }

        internal void ClientCapture(object ps, ref int wallet, ref int spend)
        {
            if (!ClientActive || ps == null) return;
            Observe(ps);
            wallet = _cw.Local; spend = _cw.Spend;
        }

        void Observe(object ps)
        {
            int now = _money(ps);
            int target = _cw.Observe(now, Now);
            if (target != now) { _setMoney(ps, target); _excluded++; _excludedAmount += now - target; Once("armed", "WALLET local credit " + (now - target) + " not kept (world object not shared)"); }
        }

        void ReadState()
        {
            var reg = _world.Registry;
            if (reg.HeadRev == _seenHead && reg.Epoch == _seenEpoch && reg.HasSnapshot == _seenSnap) return;
            _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch; _seenSnap = reg.HasSnapshot;
            var e = reg.Get(StateScope.Session, 0, WorldKeys.Wallet);
            WalletState s;
            if (e != null && WalletState.TryParse(e.Value, out s)) { _state = s; _hasState = true; }
            else if (e != null) { _parseErrors++; Once("parse", "WALLET host balance entry unreadable"); }
        }

        void Mirror(object ps, string why)
        {
            int display;
            if (!_hasState || !_cw.Target(_state, out display)) return;
            int cur = _money(ps);
            if (cur == display) return;
            _setMoney(ps, display); _cw.Wrote(display);
            if (_cw.MirrorWrites <= 20 || _cw.MirrorWrites % 50 == 0) _log.Info("WALLET money " + cur + " -> " + display + " (" + why + ", " + _state + ", own counter " + _cw.Local + ")");
        }

        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            if (now >= _nextStats) { _nextStats = now + 30; if (_host ? false : _cw.Counted + _cw.MirrorWrites + _refused + _excluded > 0) _log.Info(Stats); }
            if (_host || !_cw.Active) return;
            var ps = Ps(); if (ps == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Observe(ps);
            ReadState();
            Mirror(ps, "host balance");
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _mirrorMaxMs) _mirrorMaxMs = ms;
        }

        void Once(string key, string msg) { if (_logged.Count < 128 && _logged.Add(key)) _log.Info(msg); }

        // ======================================================================== client policy hooks
        static bool ClientOn(out WalletSync w) { w = Instance; return w != null && w.IsClient; }

        static bool RefusePrefix(MethodBase __originalMethod)
        {
            WalletSync w; if (!ClientOn(out w)) return true;
            w._refused++;
            string name = __originalMethod != null ? __originalMethod.DeclaringType.Name + "." + __originalMethod.Name : "?";
            w.Once("refuse:" + name, "WALLET " + name + " refused on the client: it buys host / world-owned state Player 2 cannot own (nothing happens, shared money unchanged)");
            return false;
        }

        static void ExcludePrefix(out int __state) { __state = int.MinValue; WalletSync w; if (!ClientOn(out w)) return; var ps = w.Ps(); if (ps != null) __state = w._money(ps); }
        static void ExcludePostfix(int __state, MethodBase __originalMethod)
        {
            WalletSync w; if (__state == int.MinValue || !ClientOn(out w)) return;
            var ps = w.Ps(); if (ps == null) return;
            int now = w._money(ps); if (now == __state) return;
            w._setMoney(ps, __state); w._excluded++; w._excludedAmount += now - __state;
            string name = __originalMethod != null ? __originalMethod.DeclaringType.Name + "." + __originalMethod.Name : "?";
            w._log.Info("WALLET " + name + " money change " + (now - __state) + " not credited to the shared balance (system not shared yet) - reverted on the client");
        }

        static void HiddenPostfix() { WalletSync w; if (ClientOn(out w)) { w._cw.ArmExclusion(WalletPolicy.HiddenItemReward, Now, 2.0); } }

        static void RentPrefix(object __instance, out int __state) { __state = int.MaxValue; WalletSync w; if (ClientOn(out w) && __instance != null) try { __state = w._rentTimer(__instance); } catch { } }
        static void RentPostfix(object __instance, int __state)
        {
            WalletSync w; if (__state == int.MaxValue || !ClientOn(out w) || __instance == null) return;
            try
            {
                if (!WalletPolicy.RentCharged(__state, w._rentTimer(__instance))) return;
                var ps = w._sgPs(__instance); var gm = ps != null ? w._psGm(ps) : null; if (ps == null || gm == null) return;
                int rent = WalletPolicy.RentAmount(w._survival(gm));
                w._setMoney(ps, w._money(ps) + rent); w._rentReverted++;
                w._log.Info("WALLET rent " + rent + " charged by the client's original timer reverted: the host pays the household rent once from the shared balance");
            }
            catch (Exception ex) { w._log.Error("WALLET rent hook failed", ex); }
        }

        static void SavePrefix(object __instance, out long __state)
        {
            __state = long.MinValue; WalletSync w; if (!ClientOn(out w) || __instance == null) return;
            try { var ps = w._sgPs(__instance); var gm = ps != null ? w._psGm(ps) : null; if (ps != null && gm != null) __state = ((long)w._money(ps) << 32) | (uint)w._carDamages(gm); } catch { }
        }
        static void SavePostfix(object __instance, long __state)
        {
            WalletSync w; if (__state == long.MinValue || !ClientOn(out w) || __instance == null) return;
            try
            {
                var ps = w._sgPs(__instance); if (ps == null) return;
                int before = (int)(__state >> 32), damages = (int)(uint)__state, after = w._money(ps);
                int target = WalletPolicy.ClientAfterSave(before, damages, after);
                if (target == after) return;
                w._setMoney(ps, target); w._clampReverted++;
                w._log.Info("WALLET negative-money clamp of the client's save reverted (" + after + " -> " + target + "): the host's own saves apply it to the shared balance");
            }
            catch (Exception ex) { w._log.Error("WALLET save hook failed", ex); }
        }

        static void BuyPrefix(out int __state) { __state = int.MinValue; WalletSync w; if (!ClientOn(out w) || !w._cw.Active) return; var ps = w.Ps(); if (ps != null) __state = w._money(ps); }
        static void BuyPostfix(int __state)
        {
            WalletSync w; if (__state == int.MinValue || !ClientOn(out w) || !w._cw.Active) return;
            var ps = w.Ps(); if (ps == null) return;
            int price = __state - w._money(ps);
            if (price <= 0) return;
            w.Observe(ps);                 // the money change first, then the purchase counter: a report never carries one without the other
            w._cw.Purchase(price);
            w._log.Info("WALLET Player-2 purchase " + price + " (counter " + w._cw.Local + ", spend " + w._cw.Spend + "): reported now, the host checks the shared balance");
            var p2 = Player2Sync.Instance; if (p2 != null) p2.ReportNow();
        }

        public string Stats
        {
            get
            {
                return "WALLET_STATS " + (_host ? "host" : "client active " + _cw.Active + " counter " + _cw.Local + " spend " + _cw.Spend + " counted " + _cw.Counted + " purchases " + _cw.Purchases
                    + " mirrorWrites " + _cw.MirrorWrites + (_hasState ? " " + _state : " no host balance") + " refused " + _refused + " excluded " + _excluded + " (" + _excludedAmount + ") rentReverted " + _rentReverted
                    + " clampReverted " + _clampReverted + " armedReverted " + _cw.Reverted + " parseErrors " + _parseErrors + " mirrorMax " + _mirrorMaxMs.ToString("0.000") + "ms");
            }
        }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
