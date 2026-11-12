// M5 W4.1 shared pre-placed world loot on the ORIGINAL pickup (design + verified IL: src/Shared/WorldLoot.cs).
// Entry point: the existing W3 Harmony prefix on Pickupable.PickUp (WorldItemSync.PickPrefix) asks AllowPick for every object
// that is not a W3 dropped item. Supported = census object of a supported region (Madison Street) whose flags and item type
// are in the Phase B subset; everything else keeps the original local behaviour (logged once per object type).
//   Player 1 (host): a claim is taken at PickUp and stays PENDING while the original coroutine runs; the postfix on
//     Pickupable.SetDayRespawn (= the coroutine's irreversible step for that very instance) commits it as taken. A pending
//     claim whose object disappears (scene unloaded) or is deactivated before that step is released - nothing irreversible
//     happened. No timeout ever commits anything.
//   Player 2 (client): PickUp is refused, a "lootTxn" proposal is sent; the host validates against its own census catalog,
//     commits record +1 + durable world take, then the client resumes the ORIGINAL coroutine (presentation, sound, XP, its
//     own Item_Picked). The transaction stays pending until the original counted the item (or, if the object vanished in the
//     same scene, the original Item_Picked is called directly); a scene change in between leaves the count to the record
//     restore (never counted twice).
//   Observers (both roles): objects of the local region listed as taken in the registry are removed like the original's own
//     final Destroy (marker + object), never through the pickup coroutine (no item, no XP, no keys).
// Harmony: Pickupable.SetDayRespawn postfix (host claims + Player-1 provenance). No per-frame scans: the registry is read
// only when its head revision changes; pending claims are checked 2x per second; no scene-wide object search.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class WorldLootSync : IDisposable
    {
        public static WorldLootSync Instance;

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host;
        Harmony _h; bool _ok;
        Func<object, object> _gmPs, _psItems, _psInv, _pickItem; Func<object, int> _invDays;
        Func<object, int> _itemId, _itemAmount, _itemMax, _pickId; Func<object, float> _itemValue, _itemAddXp;
        Func<object, bool> _itemHeavy, _itemCar, _itemDrone, _itemStory, _itemMission, _itemTool, _itemSpecial, _pickPicking, _pickHeavy, _pickMoney, _pickDropped;
        Action<object> _pickUp, _destroyMarker; MethodInfo _itemPicked; Func<string, int> _get;

        // host: Player-1 claims pending until the original reached SetDayRespawn (key = Pickupable instance id)
        sealed class Claim { public int Ns; public uint Hash; public Component Pick; public string Name; public double At, InactiveSince; }
        readonly Dictionary<int, Claim> _claims = new Dictionary<int, Claim>();
        readonly List<int> _scratch = new List<int>();

        // client: one Player-2 world pickup at a time (shares the Player-2 transaction id with storage / dropped items)
        sealed class Txn { public uint Id, Txn_; public int Ns; public uint Hash; public Component Pick; public object Item, Gm; public string Name; public double At; public bool Lost; }
        Txn _txn;
        object _awaitItem; int _awaitAmount; uint _awaitTxn; Component _awaitPick; string _awaitName; double _awaitInactive;
        Component _resume;

        // observer: taken objects of the local region
        readonly HashSet<uint> _taken = new HashSet<uint>();
        int _shownNs = Regions.None; uint _seenHead = uint.MaxValue, _seenEpoch; object _seenGm; bool _dirty = true;
        readonly HashSet<int> _notSharedLogged = new HashSet<int>();

        long _p1Claims, _p1Commits, _p1Released, _p2Requests, _p2Accepted, _p2Refused, _hostDecided, _hostRefused, _removed, _directCounts, _notShared, _lost;
        double _pickMaxMs, _decideMaxMs, _observeMaxMs; double _nextStats, _nextClaimCheck;
        static double Now { get { return Time.realtimeSinceStartup; } }

        public bool TxnPending { get { return (_txn != null && !_txn.Lost) || _awaitItem != null; } }
        public bool HasLostTxn { get { return _txn != null && _txn.Lost; } }
        bool Live { get { return _node.IsConnected && _world.Synced; } }

        public WorldLootSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory");
                var it = AccessTools.TypeByName("ItemID"); var pk = AccessTools.TypeByName("Pickupable"); var sp = AccessTools.TypeByName("SPrefs");
                _gmPs = Fast.Getter<object>(gm, "PS"); _psItems = Fast.Getter<object>(ps, "item_IDs2"); _psInv = Fast.Getter<object>(ps, "invScript"); _invDays = Fast.Getter<int>(inv, "daysInGame");
                _itemId = Fast.Getter<int>(it, "item_ID"); _itemAmount = Fast.Getter<int>(it, "item_amount"); _itemMax = Fast.Getter<int>(it, "max_amount");
                _itemValue = Fast.Getter<float>(it, "item_value"); _itemAddXp = Fast.Getter<float>(it, "add_XP");
                _itemHeavy = Fast.Getter<bool>(it, "is_heavy"); _itemCar = Fast.Getter<bool>(it, "isCar"); _itemDrone = Fast.Getter<bool>(it, "isDrone"); _itemStory = Fast.Getter<bool>(it, "isStoryItem");
                _itemMission = Fast.Getter<bool>(it, "isMissionItem"); _itemTool = Fast.Getter<bool>(it, "isTool"); _itemSpecial = Fast.Getter<bool>(it, "is_special");
                _pickItem = Fast.Getter<object>(pk, "itemScript"); _pickId = Fast.Getter<int>(pk, "item_ID"); _pickPicking = Fast.Getter<bool>(pk, "picking");
                _pickHeavy = Fast.Getter<bool>(pk, "is_heavy_item"); _pickMoney = Fast.Getter<bool>(pk, "is_money"); _pickDropped = Fast.Getter<bool>(pk, "wasDropped");
                _pickUp = Fast.Call0(pk, "PickUp"); _destroyMarker = Fast.Call0(pk, "DestroyMarker");
                _itemPicked = AccessTools.Method(it, "Item_Picked", new[] { typeof(string), typeof(float), typeof(bool), typeof(bool) });
                var gi = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
                if (gi != null) _get = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                var respawn = pk != null ? AccessTools.Method(pk, "SetDayRespawn", Type.EmptyTypes) : null;
                if (_gmPs == null || _psItems == null || _psInv == null || _invDays == null || _itemId == null || _itemAmount == null || _itemMax == null || _itemValue == null || _itemAddXp == null
                    || _itemHeavy == null || _itemCar == null || _itemDrone == null || _itemStory == null || _itemMission == null || _itemTool == null || _itemSpecial == null
                    || _pickItem == null || _pickId == null || _pickPicking == null || _pickHeavy == null || _pickMoney == null || _pickDropped == null || _pickUp == null || _destroyMarker == null
                    || _itemPicked == null || _get == null || respawn == null)
                { _log.Warn("WLOOT game API incomplete: shared world loot disabled (original local pickups)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.wloot");
                _h.Patch(respawn, null, new HarmonyMethod(AccessTools.Method(typeof(WorldLootSync), "RespawnPostfix")));
                _ok = true; Instance = this;
                if (_host && world.Host != null) world.Host.LootTxnHook = HostDecide;
                if (!_host) world.ProposalResultHook5 = OnResult;
                _log.Info("WLOOT shared world loot ready (" + (_host ? "host: authoritative" : "client: pickups as host transactions") + "), supported regions: MadisonSt");
            }
            catch (Exception ex) { _log.Error("WLOOT init failed: shared world loot disabled", ex); _ok = false; }
        }

        object Ps() { var gm = _world.GameManagerObject; return gm != null ? _gmPs(gm) : null; }

        readonly Dictionary<int, object> _itemById = new Dictionary<int, object>(); object _itemsGm;
        object ItemOf(int id)
        {
            var gm = _world.GameManagerObject;
            if (!ReferenceEquals(gm, _itemsGm) || _itemById.Count == 0)
            {
                _itemsGm = gm; _itemById.Clear();
                var arr = gm != null ? _psItems(_gmPs(gm)) as Array : null;
                if (arr != null) foreach (var o in arr) if (o != null && !_itemById.ContainsKey(_itemId(o))) _itemById[_itemId(o)] = o;
            }
            object x; return _itemById.TryGetValue(id, out x) ? x : null;
        }

        ItemFacts Facts(int id)
        {
            var o = ItemOf(id);
            if (o == null) return new ItemFacts();
            return new ItemFacts
            {
                Known = true, Heavy = _itemHeavy(o), Car = _itemCar(o), Drone = _itemDrone(o), Story = _itemStory(o), Mission = _itemMission(o), Tool = _itemTool(o), Special = _itemSpecial(o),
                Value = _itemValue(o), AddXp = _itemAddXp(o), MaxAmount = _itemMax(o)
            };
        }

        LootCatalog LiveCatalog(int ns)
        {
            var c = WorldCensus.Instance != null ? WorldCensus.Instance.Loot(ns) : null;
            return c != null && c.Live ? c : null;
        }

        // ======================================================================== pickup decision (from WorldItemSync.PickPrefix)
        // true = the original pickup runs now; false = refused (nothing happens, like any refused original interaction).
        public bool AllowPick(Component pick)
        {
            if (!_ok || pick == null) return true;
            if (ReferenceEquals(_resume, pick)) { _resume = null; return true; }   // our confirmed Player-2 pickup
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                int ns = _world.LocalRegion;
                if (!LootRules.SupportedNamespace(ns)) return true;
                var cat = LiveCatalog(ns); LootEntry e;
                if (cat == null || !cat.TryByInstance(pick.GetInstanceID(), out e)) return true;   // not a verified scene object
                string excl = e.Flags != LootFlags.None ? LootRules.Exclusion(e.Flags) : LootRules.Exclusion(Facts(e.ItemId));
                if (excl != null) { NotShared(e, excl); return true; }
                return _host ? HostPick(ns, e, pick) : ClientPick(ns, cat, e, pick);
            }
            finally { double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _pickMaxMs) _pickMaxMs = ms; }
        }

        void NotShared(LootEntry e, string why)
        {
            _notShared++;
            if (_notSharedLogged.Count < 64 && _notSharedLogged.Add(e.ItemId)) _log.Info("WLOOT '" + e.Name + "' (item " + e.ItemId + ") not shared: " + why + " - original local pickup");
        }

        bool HostPick(int ns, LootEntry e, Component pick)
        {
            var book = _world.Host != null ? _world.Host.Loot : null;
            if (book == null) return true;
            int token = pick.GetInstanceID();
            if (_claims.ContainsKey(token)) return false;                                 // original already running for it
            if (book.StateOf(ns, e.Hash) == LootState.Taken) { Remove(pick, e.Name, "already taken by Player 2"); return false; }
            string why;
            if (!book.Claim(ns, e.Hash, token, out why)) { _log.Info("WLOOT Player-1 pickup of '" + e.Name + "' refused: " + why); return false; }
            _claims[token] = new Claim { Ns = ns, Hash = e.Hash, Pick = pick, Name = e.Name, At = Now };
            _p1Claims++;
            return true;
        }

        bool ClientPick(int ns, LootCatalog cat, LootEntry e, Component pick)
        {
            if (!Live) return false;                                   // disconnected: a local pick could never be reconciled
            if (_taken.Contains(e.Hash) && _shownNs == ns) { Remove(pick, e.Name, "taken"); return false; }
            if (_txn != null || _awaitItem != null) return false;
            var p2 = Player2Sync.Instance; var cb = CarBoxSync.Instance; var wi = WorldItemSync.Instance;
            if (p2 == null || !p2.Restored || (cb != null && cb.TransferPending) || (wi != null && wi.TxnPending)) return false;
            if (!p2.PrepareTransfer()) return false;
            uint txn = p2.AppliedTxn + 1;
            var req = new LootTxnRequest { Txn = txn, Ns = ns, Hash = e.Hash, ItemId = e.ItemId, Digest = cat.Digest };
            _txn = new Txn { Txn_ = txn, Ns = ns, Hash = e.Hash, Pick = pick, Item = ItemOf(e.ItemId), Gm = _world.GameManagerObject, Name = e.Name, At = Now };
            _txn.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.LootTxn, Value = req.Encode() });
            _p2Requests++;
            _log.Info("WLOOT pickup #" + txn + " of '" + e.Name + "' (" + IdentityNamespace.Name(ns) + ":" + e.Hash.ToString("X8") + ", item " + e.ItemId + ") requested (proposal " + _txn.Id + ")");
            return false;
        }

        // ======================================================================== host
        static void RespawnPostfix(object __instance)
        {
            var i = Instance; var c = __instance as Component;
            if (i == null || c == null || !i._host) return;
            try { i.OnRespawn(c); } catch (Exception ex) { i._log.Error("WLOOT respawn hook failed", ex); }
        }

        // The original coroutine reached its irreversible step for this instance (Player 1 on the host).
        void OnRespawn(Component pick)
        {
            Claim c; int token = pick.GetInstanceID();
            if (_claims.TryGetValue(token, out c))
            {
                _claims.Remove(token);
                var code = _world.Host.Loot.Commit(c.Ns, c.Hash, _world.Host.Reg, _world.Host.Outbox); _world.FlushOutbox();
                _p1Commits++;
                _log.Info("WLOOT Player 1 took '" + c.Name + "' (" + IdentityNamespace.Name(c.Ns) + ":" + c.Hash.ToString("X8") + ") -> " + code + " after " + ((Now - c.At) * 1000).ToString("0") + " ms");
            }
            // provenance: every Player-1 world pickup that wrote a respawn key (supported or not) is an unsaved acquisition
            if (_pickHeavy(pick) || _pickMoney(pick) || _pickDropped(pick)) return;   // heavy: VanHeavyItems path; money: no unit
            string name = pick.transform.name;
            if (name == "dropped" || name.Length > LootRules.MaxNameChars || LootRules.IsNumeric(name)) return;
            var ps = Ps(); var inv = ps != null ? _psInv(ps) : null;
            int today = inv != null ? _invDays(inv) : int.MaxValue, day = _get(LootKeys.Day(name));
            if (day <= today) return;                                  // the original wrote no key (no XP): original respawn on reload
            var it = _pickItem(pick); int id = it != null ? _itemId(it) : _pickId(pick);
            int ns = IdentityNamespace.ForScene(pick.gameObject.scene.name); uint hash = 0;
            var cat = ns != IdentityNamespace.None && WorldCensus.Instance != null ? WorldCensus.Instance.Loot(ns) : null; LootEntry e;
            if (cat != null && cat.TryByInstance(token, out e)) hash = e.Hash;
            var p2 = Player2Sync.Instance;
            if (p2 != null) p2.NoteP1Pick(new LootTake { Ns = ns, Hash = hash, ItemId = id, Day = day, ItemFlag = true, Name = name });
        }

        // WorldHost.LootTransaction hook: validation against the host's own catalog, then the durable Player-2 side.
        ProposalCode HostDecide(byte slot, LootTxnRequest t, bool inRegion, out string why)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                why = null;
                var p2 = Player2Sync.Instance;
                if (p2 == null || !p2.HostBookReady) { why = "host Player-2 book not ready"; return ProposalCode.Busy; }
                var ps = Ps(); var inv = ps != null ? _psInv(ps) : null;
                // host scene loading: item facts / day count unavailable -> retry later (never a permanent refusal)
                if (inv == null && t.Txn > p2.P2AppliedTxn) { why = "host scene loading"; return ProposalCode.Busy; }
                var cat = WorldCensus.Instance != null ? WorldCensus.Instance.Loot(t.Ns) : null;
                LootEntry e = null; var facts = new ItemFacts();
                if (cat != null && cat.TryGet(t.Hash, out e)) facts = Facts(e.ItemId);
                var book = _world.Host.Loot;
                var code = LootDecision.DecideP2(t.Txn, p2.P2AppliedTxn, inRegion, cat, t.Digest, t.Hash, t.ItemId, facts, book.StateOf(t.Ns, t.Hash), book.HasRoom(t.Ns, t.Hash), p2.P2Count(t.ItemId), out why);
                if (code != ProposalCode.Accepted) { if (code != ProposalCode.Unchanged) { _hostRefused++; _log.Info("WLOOT Player-2 pickup #" + t.Txn + " " + IdentityNamespace.Name(t.Ns) + ":" + t.Hash.ToString("X8") + " -> " + code + " (" + why + ")"); } return code; }
                if (inv == null) { why = "host scene loading"; return ProposalCode.Busy; }
                var take = new LootTake { Ns = t.Ns, Hash = t.Hash, ItemId = e.ItemId, Day = _invDays(inv) + LootRules.RespawnDays(facts.Value, false), ItemFlag = true, Name = e.Name };
                code = p2.HostLootTake(t.Txn, take, out why);
                if (code == ProposalCode.Accepted)
                {
                    _hostDecided++;
                    var obj = e.Target as Component;
                    if (obj != null) Remove(obj, e.Name, "taken by Player 2");   // a Player-1 claim on it would have made the decision Busy
                    _dirty = true;
                }
                else _hostRefused++;
                return code;
            }
            finally { double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _decideMaxMs) _decideMaxMs = ms; }
        }

        // 2x per second: a pending claim whose object is gone (scene unloaded) or deactivated (coroutine stopped) before the
        // irreversible step is released; nothing was granted.
        void CheckClaims(double now)
        {
            if (_claims.Count == 0 || _world.Host == null) return;
            _scratch.Clear();
            foreach (var kv in _claims)
            {
                var c = kv.Value; string why = null;
                if (c.Pick == null) why = "object gone before the original's final step (scene unloaded)";
                else if (!c.Pick.gameObject.activeInHierarchy) { if (c.InactiveSince == 0) c.InactiveSince = now; else if (now - c.InactiveSince > 2.0) why = "object deactivated during the pickup (coroutine stopped)"; }
                else c.InactiveSince = 0;
                if (why != null) { _scratch.Add(kv.Key); _world.Host.Loot.Release(c.Ns, c.Hash, kv.Key); _p1Released++; _log.Info("WLOOT Player-1 claim on '" + c.Name + "' released: " + why + " - available again"); }
            }
            foreach (var k in _scratch) _claims.Remove(k);
        }

        // ======================================================================== client results
        bool OnResult(uint id, ProposalCode code)
        {
            if (_txn == null || id != _txn.Id) return false;
            var t = _txn; _txn = null;
            if (code == ProposalCode.Accepted || code == ProposalCode.Unchanged) { _p2Accepted++; Apply(t, "acknowledged (" + code + ")"); }
            else { _p2Refused++; _log.Info("WLOOT pickup #" + t.Txn_ + " of '" + t.Name + "' -> " + code + ": nothing changed"); _dirty = true; }
            return true;
        }

        void Apply(Txn t, string why)
        {
            var p2 = Player2Sync.Instance;
            if (!ReferenceEquals(t.Gm, _world.GameManagerObject))
            {
                // the scene changed meanwhile: the record restore of the new scene carries the item (Resync if it ran earlier)
                _log.Info("WLOOT pickup #" + t.Txn_ + " of '" + t.Name + "' " + why + " after a scene change: counted by the record restore");
                return;
            }
            _awaitItem = t.Item; _awaitAmount = t.Item != null ? _itemAmount(t.Item) + 1 : 0; _awaitTxn = t.Txn_; _awaitPick = t.Pick; _awaitName = t.Name; _awaitInactive = 0;
            bool started = false;
            if (t.Pick != null && t.Pick.gameObject.activeInHierarchy)
            {
                _resume = t.Pick;
                try { _pickUp(t.Pick); started = true; } catch (Exception ex) { _log.Error("WLOOT original pickup after the acknowledgment failed", ex); }
                finally { _resume = null; }
            }
            if (!started) Finish("object gone - counted directly");
            _log.Info("WLOOT pickup #" + t.Txn_ + " of '" + t.Name + "' " + why + (started ? ": original pickup running" : ""));
        }

        void Finish(string why)
        {
            var item = _awaitItem; uint txn = _awaitTxn; _awaitItem = null; _awaitPick = null;
            if (item == null) return;
            if (_itemAmount(item) < _awaitAmount)
            {
                try { _itemPicked.Invoke(item, new object[] { _awaitName, 0f, true, false }); _directCounts++; } catch (Exception ex) { _log.Error("WLOOT direct pickup count failed", ex); }
                why += " (original Item_Picked called directly)";
            }
            var p2 = Player2Sync.Instance; if (p2 != null && p2.AppliedTxn < txn) p2.AppliedTxn = txn;
            _log.Info("WLOOT pickup #" + txn + " applied locally: item " + _itemId(item) + " now " + _itemAmount(item) + " - " + why);
        }

        // After a reconnect the acknowledgment may be lost: the host record decides.
        public void ResolvePending(P2Inventory rec)
        {
            var t = _txn; if (t == null) return;
            _txn = null;
            if (rec.AppliedTxn == t.Txn_) { Apply(t, "committed by the host (acknowledgment lost)"); return; }
            _log.Info("WLOOT pickup #" + t.Txn_ + " was not committed by the host (record at " + rec.AppliedTxn + "): nothing changed");
            _dirty = true;
        }

        // ======================================================================== observer + housekeeping (both roles)
        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            if (now >= _nextStats) { _nextStats = now + 30; if (_p1Claims + _p2Requests + _hostDecided + _removed + _hostRefused > 0) _log.Info(Stats); }
            if (_txn != null && !_txn.Lost && (!Live || now - _txn.At > 15.0)) { _txn.Lost = true; _lost++; _log.Warn("WLOOT pickup #" + _txn.Txn_ + " unanswered: resolved from the host record"); }
            if (_awaitItem != null)
            {
                if (_itemAmount(_awaitItem) >= _awaitAmount) Finish("original pickup counted");
                else if (_awaitPick == null) Finish("original pickup interrupted (object gone)");
                else if (!_awaitPick.gameObject.activeInHierarchy)
                {
                    if (_awaitInactive == 0) _awaitInactive = now;
                    else if (now - _awaitInactive > 2.0) { var go = _awaitPick.gameObject; Finish("object deactivated during the pickup"); UnityEngine.Object.Destroy(go); }
                }
            }
            if (_host && now >= _nextClaimCheck) { _nextClaimCheck = now + 0.5; CheckClaims(now); }

            var gm = _world.GameManagerObject;
            if (!ReferenceEquals(gm, _seenGm)) { _seenGm = gm; _dirty = true; _itemById.Clear(); _itemsGm = null; }
            int ns = _world.LocalRegion;
            if (!LootRules.SupportedNamespace(ns)) { if (_shownNs != Regions.None) { _shownNs = Regions.None; _taken.Clear(); } return; }
            if (!_host && !Live) return;
            var reg = _world.Registry;
            if (!_dirty && ns == _shownNs && reg.HeadRev == _seenHead && reg.Epoch == _seenEpoch) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch; _dirty = false; _shownNs = ns;
            _taken.Clear();
            for (int c = 0; c < LootBook.Chunks; c++)
            {
                var e = reg.Get(StateScope.Region, ns, LootBook.Keys[c]);
                if (e != null && !LootBook.TryParse(e.Value, c, _taken)) _log.Warn("WLOOT registry chunk " + c + " of " + Regions.Name(ns) + " unreadable");
            }
            var cat = LiveCatalog(ns);
            if (cat == null) return;                                   // census of this scene not available yet: retried on the next change
            foreach (var h in _taken)
            {
                LootEntry le; if (!cat.TryGet(h, out le)) continue;
                var obj = le.Target as Component;
                if (obj == null) continue;                             // already destroyed (original Start by the ItemDay_ key, or removed)
                if (OwnPickupRunning(obj, h)) continue;
                Remove(obj, le.Name, "taken");
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _observeMaxMs) _observeMaxMs = ms;
        }

        // This instance's own original pickup of that object is in progress: its coroutine removes it.
        bool OwnPickupRunning(Component obj, uint hash)
        {
            if (_pickPicking(obj)) return true;
            if (_host) return _claims.ContainsKey(obj.GetInstanceID());
            return (_txn != null && _txn.Hash == hash && ReferenceEquals(_txn.Pick, obj)) || (_awaitItem != null && ReferenceEquals(_awaitPick, obj));
        }

        // Observer removal = the original's own end of a pickup (marker + object), without any of its effects.
        void Remove(Component obj, string name, string why)
        {
            if (obj == null) return;
            try { _destroyMarker(obj); } catch { }
            UnityEngine.Object.Destroy(obj.gameObject);
            _removed++;
            if (_removed <= 32 || _removed % 50 == 0) _log.Info("WLOOT removed '" + name + "' from this world (" + why + ") [removed " + _removed + "]");
        }

        public string Stats
        {
            get
            {
                var b = _host && _world.Host != null ? _world.Host.Loot : null;
                return "WLOOT_STATS " + (_host ? "p1Claims " + _p1Claims + " p1Commits " + _p1Commits + " p1Released " + _p1Released + " pendingNow " + _claims.Count + " p2Decided " + _hostDecided + " p2Refused " + _hostRefused
                        + (b != null ? " bookCommits " + b.Commits + " bookCleared " + b.Cleared + " full " + b.Full : "")
                    : "requests " + _p2Requests + " accepted " + _p2Accepted + " refused " + _p2Refused + " lost " + _lost + " directCounts " + _directCounts)
                    + " removed " + _removed + " notShared " + _notShared + " takenHere " + _taken.Count + " pickMax " + _pickMaxMs.ToString("0.00") + "ms decideMax " + _decideMaxMs.ToString("0.00") + "ms observeMax " + _observeMaxMs.ToString("0.00") + "ms";
            }
        }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_host && _world.Host != null && _world.Host.LootTxnHook == (WorldHost.LootTxnFn)HostDecide) _world.Host.LootTxnHook = null;
            if (!_host && _world.ProposalResultHook5 == (Func<uint, ProposalCode, bool>)OnResult) _world.ProposalResultHook5 = null;
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
