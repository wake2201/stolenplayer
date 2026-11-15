// M5 W4.1 shared pre-placed world loot on the ORIGINAL pickup (design + verified IL: src/Shared/WorldLoot.cs).
// Entry point: the existing W3 Harmony prefix on Pickupable.PickUp (WorldItemSync.PickPrefix) asks AllowPick for every object
// that is not a W3 dropped item. Supported = census object of a supported region (Madison Street) whose flags and item type
// are in the shared subset (v0.16.1: shared-name families included).
// v0.16.1 root cause of the two-PC duplicate: every object outside that subset (excluded classes such as 'Pot'/'Wine1' with
//   LootFlags.SharedName, unsupported regions, MainScene objects, heavy items) ran the ORIGINAL local pickup on BOTH PCs, so
//   each player could collect it once. Now: Player 1 keeps the original pickup (excluded census objects get a non-blocking
//   shadow claim so Player 2's copy disappears when the original commits); Player 2 is refused for everything no host
//   transaction covers (LootRules.ClientPolicy; heavy: Pickupable.PickUpHeavy prefix) - nothing happens, logged once per name.
//   Only Player 2's own unshared local drops (not in the census) are picked back locally.
// W4.1B: classes via LootRules.Classify - ordinary valuables, unique valuables (destroy_if_was_picked; host refuses a take its
//   save already removed and lists such objects as taken at region load) and world money (is_money: the host credits the
//   SHARED balance + record counter in the same durable write as the world take; Player 2's coroutine add_money is counted by
//   the wallet mirror into the same counter). World money that no host transaction can cover is refused for Player 2.
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
        Func<object, bool> _itemHeavy, _itemCar, _itemDrone, _itemStory, _itemMission, _itemTool, _itemSpecial, _itemKey, _pickPicking, _pickHeavy, _pickMoney, _pickDropped;
        Action<object> _pickUp, _destroyMarker; MethodInfo _itemPicked; Func<string, int> _get;

        // host: Player-1 claims pending until the original reached SetDayRespawn (key = Pickupable instance id)
        sealed class Claim { public int Ns; public uint Hash; public Component Pick; public string Name; public double At, InactiveSince; public bool Shadow, Family; public LootClass Cls; public uint Group; public int Outcome; }
        readonly Dictionary<int, Claim> _claims = new Dictionary<int, Claim>();
        readonly List<int> _scratch = new List<int>();

        // client: one Player-2 world pickup at a time (shares the Player-2 transaction id with storage / dropped items)
        sealed class Txn { public uint Id, Txn_; public int Ns; public uint Hash; public Component Pick; public object Item, Gm; public string Name; public double At; public bool Lost; public LootClass Cls; public int Money; }
        Txn _txn;
        object _awaitItem; int _awaitAmount; uint _awaitTxn; Component _awaitPick; string _awaitName; double _awaitInactive;
        // W4.1B money pickup: the original coroutine adds add_money; the wallet mirror counts it (await counter target)
        bool _awaitMoney, _awaitCovered; int _awaitMoneyAmount;
        bool Awaiting { get { return _awaitItem != null || _awaitMoney; } }
        Component _resume;

        // observer: taken objects of the local region
        readonly HashSet<uint> _taken = new HashSet<uint>();
        int _shownNs = Regions.None; uint _seenHead = uint.MaxValue, _seenEpoch; object _seenGm; bool _dirty = true;
        readonly HashSet<int> _notSharedLogged = new HashSet<int>();

        long _p1Claims, _p1Commits, _p1Released, _p2Requests, _p2Accepted, _p2Refused, _hostDecided, _hostRefused, _removed, _directCounts, _notShared, _lost;
        double _pickMaxMs, _decideMaxMs, _observeMaxMs; double _nextStats, _nextClaimCheck;
        static double Now { get { return Time.realtimeSinceStartup; } }

        public bool TxnPending { get { return (_txn != null && !_txn.Lost) || Awaiting; } }
        public bool HasLostTxn { get { return _txn != null && _txn.Lost; } }
        bool Live { get { return _node.IsConnected && _world.Synced; } }

        public WorldLootSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory");
                var it = AccessTools.TypeByName("ItemID"); var pk = AccessTools.TypeByName("Pickupable"); var sp = AccessTools.TypeByName("SPrefs");
                _gmMission = Fast.Getter<object>(gm, "mission_strings_to_set"); _gmParcels = Fast.Getter<object>(gm, "parcel_strings_to_set");
                _gmPs = Fast.Getter<object>(gm, "PS"); _psItems = Fast.Getter<object>(ps, "item_IDs2"); _psInv = Fast.Getter<object>(ps, "invScript"); _invDays = Fast.Getter<int>(inv, "daysInGame");
                _itemId = Fast.Getter<int>(it, "item_ID"); _itemAmount = Fast.Getter<int>(it, "item_amount"); _itemMax = Fast.Getter<int>(it, "max_amount");
                _itemValue = Fast.Getter<float>(it, "item_value"); _itemAddXp = Fast.Getter<float>(it, "add_XP");
                _itemHeavy = Fast.Getter<bool>(it, "is_heavy"); _itemCar = Fast.Getter<bool>(it, "isCar"); _itemDrone = Fast.Getter<bool>(it, "isDrone"); _itemStory = Fast.Getter<bool>(it, "isStoryItem");
                _itemMission = Fast.Getter<bool>(it, "isMissionItem"); _itemTool = Fast.Getter<bool>(it, "isTool"); _itemSpecial = Fast.Getter<bool>(it, "is_special"); _itemKey = Fast.Getter<bool>(it, "isKey");
                _pickItem = Fast.Getter<object>(pk, "itemScript"); _pickId = Fast.Getter<int>(pk, "item_ID"); _pickPicking = Fast.Getter<bool>(pk, "picking");
                _pickHeavy = Fast.Getter<bool>(pk, "is_heavy_item"); _pickMoney = Fast.Getter<bool>(pk, "is_money"); _pickDropped = Fast.Getter<bool>(pk, "wasDropped");
                _pickUp = Fast.Call0(pk, "PickUp"); _destroyMarker = Fast.Call0(pk, "DestroyMarker");
                _itemPicked = AccessTools.Method(it, "Item_Picked", new[] { typeof(string), typeof(float), typeof(bool), typeof(bool) });
                var gi = sp != null ? AccessTools.Method(sp, "GetInt", new[] { typeof(string) }) : null;
                if (gi != null) _get = (Func<string, int>)Delegate.CreateDelegate(typeof(Func<string, int>), gi);
                var respawn = pk != null ? AccessTools.Method(pk, "SetDayRespawn", Type.EmptyTypes) : null;
                if (_gmPs == null || _psItems == null || _psInv == null || _invDays == null || _itemId == null || _itemAmount == null || _itemMax == null || _itemValue == null || _itemAddXp == null
                    || _itemHeavy == null || _itemCar == null || _itemDrone == null || _itemStory == null || _itemMission == null || _itemTool == null || _itemSpecial == null || _itemKey == null
                    || _pickItem == null || _pickId == null || _pickPicking == null || _pickHeavy == null || _pickMoney == null || _pickDropped == null || _pickUp == null || _destroyMarker == null
                    || _itemPicked == null || _get == null || respawn == null)
                { _log.Warn("WLOOT game API incomplete: shared world loot disabled (original local pickups)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.wloot");
                _h.Patch(respawn, null, new HarmonyMethod(AccessTools.Method(typeof(WorldLootSync), "RespawnPostfix")));
                // H1: both roles - the H1 lease decides (HeavySync); without it the v0.16.1 client refusal stays
                var heavy = AccessTools.Method(pk, "PickUpHeavy", new[] { typeof(float) });
                if (heavy != null) _h.Patch(heavy, new HarmonyMethod(AccessTools.Method(typeof(WorldLootSync), "HeavyPrefix")));
                else _log.Warn("WLOOT Pickupable.PickUpHeavy(float) not found: Player-2 heavy pickups are not guarded");
                // W4.2D: both instances seed the original RandomFileCabinet roll identically (cabinet identity + campaign day)
                var cab = AccessTools.TypeByName("RandomFileCabinet"); var cabStart = cab != null ? AccessTools.Method(cab, "Start", Type.EmptyTypes) : null;
                var rnd = AccessTools.TypeByName("UnityEngine.Random");
                _rndState = rnd != null ? AccessTools.Property(rnd, "state") : null; _rndInit = rnd != null ? AccessTools.Method(rnd, "InitState", new[] { typeof(int) }) : null;
                if (cabStart != null && _rndState != null && _rndInit != null)
                    _h.Patch(cabStart, new HarmonyMethod(AccessTools.Method(typeof(WorldLootSync), "CabinetPrefix")), new HarmonyMethod(AccessTools.Method(typeof(WorldLootSync), "CabinetPostfix")));
                else _log.Warn("WLOOT RandomFileCabinet.Start / UnityEngine.Random.state not found: cabinet rolls stay unseeded (the host still allows one outcome per slot)");
                _ok = true; Instance = this;
                if (_host && world.Host != null) { world.Host.LootTxnHook = HostDecide; HostLootCatalogs.Start(_log); }
                if (!_host) world.ProposalResultHook5 = OnResult;
                _log.Info("WLOOT shared world loot ready (" + (_host ? "host: authoritative, validated against its own scene-file catalogs" : "client: pickups as host transactions") + "), regions: every original scene + MainScene");
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
                Known = true, Heavy = _itemHeavy(o), Car = _itemCar(o), Drone = _itemDrone(o), Story = _itemStory(o), Mission = _itemMission(o), Tool = _itemTool(o), Special = _itemSpecial(o), Key = _itemKey(o),
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
                int ns = IdentityNamespace.ForScene(pick.gameObject.scene.name);   // W4.2D: the object's own scene (region scene or MainScene)
                bool supported = LootRules.SupportedNamespace(ns) && (ns == IdentityNamespace.MainScene || ns == _world.LocalRegion);
                var cat = supported ? LiveCatalog(ns) : null; LootEntry e = null;
                bool inCat = cat != null && cat.TryByInstance(pick.GetInstanceID(), out e);
                string excl = null; var cls = inCat ? LootRules.Classify(e, ItemFactsFor(e), out excl) : LootClass.Excluded;
                if (_host)
                {
                    // Player 1 keeps the original pickup for everything; shared classes are claimed (host authority), excluded
                    // census objects get a non-blocking shadow claim so Player 2's copy disappears once the original commits.
                    if (!inCat) return true;
                    if (cls == LootClass.Excluded) { ShadowClaim(ns, e, pick, excl); return true; }
                    return HostPick(ns, e, cls, pick);
                }
                // v0.16.1 Player 2: only a host transaction may grant a world reward; everything else is refused (no duplicate).
                bool dropped = _pickDropped(pick) || pick.transform.name == "dropped";
                switch (LootRules.ClientPolicy(supported, inCat, cls, dropped))
                {
                    case P2PickPolicy.LocalOwnDrop: _p2OwnDrops++; return true;
                    case P2PickPolicy.Refuse:
                        string why = !supported ? "region " + Regions.Name(ns) + " not shared yet" : !inCat ? "not a verified scene object of " + Regions.Name(ns) + " (MainScene / runtime object)" : "excluded: " + excl;
                        return RefuseP2(inCat ? e.Name : pick.transform.name, _pickMoney(pick), inCat ? e.Hash : 0u, why);
                }
                return ClientPick(ns, cat, e, cls, pick);
            }
            finally { double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _pickMaxMs) _pickMaxMs = ms; }
        }

        // v0.16.1: a Player-2 pickup no host transaction can cover. Refused: the object stays, nothing is granted (logged once
        // per object name; money keeps its W4.1B counter).
        bool RefuseP2(string name, bool money, uint hash, string why)
        {
            if (money) { _moneyRefused++; } else _p2RefusedLocal++;
            if (_notSharedLogged.Count < 128 && _notSharedLogged.Add(((money ? "money:" : "item:") + name).GetHashCode()))
                _log.Info("WLOOT Player-2 pickup of " + (money ? "world money " : "") + "'" + name + "'" + (hash != 0 ? " (" + hash.ToString("X8") + ")" : "") + " refused: " + why + " - no shared transaction possible, a local pickup would duplicate the reward");
            return false;
        }

        // Heavy items (Pickupable.PickUpHeavy(float), PlayerScript.Update interact branch - its only caller, called right after
        // the switch sound with no PlayerScript state set before it). H1: the host lease decides (HeavySync.AllowPickUpHeavy:
        // census heavy objects of the current region; everything else stays refused for Player 2). Without HeavySync (API
        // incomplete / init failed) the v0.16.1 rule stays: Player 2 is refused - a local carry would duplicate the object.
        static bool HeavyPrefix(object __instance, float dist)
        {
            var i = Instance; var c = __instance as Component;
            if (i == null || c == null) return true;
            try
            {
                var hs = HeavySync.Instance;
                if (hs != null) return hs.AllowPickUpHeavy(c, dist);
                return i._host || i.RefuseP2Heavy(c);
            }
            catch (Exception ex) { i._log.Error("WLOOT heavy pickup hook failed (pickup refused)", ex); return false; }
        }
        bool RefuseP2Heavy(Component pick)
        {
            _heavyRefused++;
            string name = pick.transform.name;
            if (_notSharedLogged.Count < 128 && _notSharedLogged.Add(("heavy:" + name).GetHashCode())) _log.Info("WLOOT Player-2 heavy pickup of '" + name + "' refused: heavy-object sync unavailable (lease transaction family missing) - a local carry would duplicate the object");
            return false;
        }

        // Money objects have no meaningful item type (item 0): their class does not depend on item facts.
        ItemFacts ItemFactsFor(LootEntry e) { return (e.Flags & LootFlags.Money) != 0 ? new ItemFacts() : Facts(e.ItemId); }

        // W4.1B: a client pickup of world money that no host transaction can cover would add to the shared balance a second
        // time (both players see their own copy): refused, nothing happens (logged once per object name).
        bool RefuseMoney(string name, string why) { return RefuseP2(name, true, 0u, why); }
        long _moneyRefused, _moneyTakes, _uniqueTakes, _persistRefused, _uniqueSeeded, _p2RefusedLocal, _p2OwnDrops, _heavyRefused, _sharedNameTakes, _shadowClaims, _shadowCommits, _liveOnlyTakes, _cabinetTakes, _parcelEffects, _seededRegions;
        Func<object, object> _gmMission, _gmParcels; PropertyInfo _rndState; MethodInfo _rndInit;

        // v0.16.1 (host): Player 1's original pickup of an excluded census object runs unchanged (never refused). If the book
        // has room, a claim is recorded anyway: when the original reaches SetDayRespawn for this instance it is committed and
        // Player 2's copy is removed. No SetDayRespawn (e.g. no XP) = the claim is released when the object is gone.
        void ShadowClaim(int ns, LootEntry e, Component pick, string why)
        {
            _notShared++;
            if (_notSharedLogged.Count < 128 && _notSharedLogged.Add(("p1:" + e.Name).GetHashCode())) _log.Info("WLOOT Player-1 pickup of '" + e.Name + "' (" + e.Hash.ToString("X8") + ", item " + e.ItemId + ") not shared (" + why + "): original local pickup; Player 2 cannot pick it");
            var book = _world.Host != null ? _world.Host.Loot : null;
            int token = pick.GetInstanceID(); string ignore;
            if (book == null || _claims.ContainsKey(token) || book.StateOf(ns, e.Hash) != LootState.Available || !book.Claim(ns, e.Hash, token, out ignore)) return;
            _claims[token] = new Claim { Ns = ns, Hash = e.Hash, Pick = pick, Name = e.Name, At = Now, Shadow = true };
            _shadowClaims++;
        }

        // Host catalog entry (W4.2D): the trusted scene-file metadata (cabinet slot, parcel pref) of a census object.
        static LootEntry HostEntry(int ns, uint hash) { var c = HostLootCatalogs.Get(ns); LootEntry e; return c != null && c.TryGet(hash, out e) ? e : null; }

        bool HostPick(int ns, LootEntry e, LootClass cls, Component pick)
        {
            var book = _world.Host != null ? _world.Host.Loot : null;
            if (book == null) return true;
            int token = pick.GetInstanceID();
            if (_claims.ContainsKey(token)) return false;                                 // original already running for it
            if (book.StateOf(ns, e.Hash) == LootState.Taken) { Remove(pick, e.Name, "already taken by Player 2"); return false; }
            var he = HostEntry(ns, e.Hash); uint group = he != null ? he.Group : 0u; int outcome = he != null ? he.Outcome : 0;
            if (!book.Cabinets.Allowed(ns, group, outcome))
            {
                book.Cabinets.Refused++;
                _log.Info("WLOOT Player-1 pickup of '" + e.Name + "' (" + e.Hash.ToString("X8") + ") refused: this cabinet slot already gave another outcome in this occupancy (one roll per slot)");
                return false;
            }
            string why;
            if (!book.Claim(ns, e.Hash, token, out why)) { _log.Info("WLOOT Player-1 pickup of '" + e.Name + "' (" + e.Hash.ToString("X8") + ") refused: " + why); return false; }
            _claims[token] = new Claim { Ns = ns, Hash = e.Hash, Pick = pick, Name = e.Name, At = Now, Cls = cls, Family = (e.Flags & LootFlags.SharedName) != 0, Group = group, Outcome = outcome };
            _p1Claims++;
            return true;
        }

        bool ClientPick(int ns, LootCatalog cat, LootEntry e, LootClass cls, Component pick)
        {
            if (!Live) return false;                                   // disconnected: a local pick could never be reconciled
            if (IsTaken(ns, e.Hash)) { Remove(pick, e.Name, "taken"); return false; }
            if (_txn != null || Awaiting) return false;
            var p2 = Player2Sync.Instance; var cb = CarBoxSync.Instance; var wi = WorldItemSync.Instance;
            if (p2 == null || !p2.Restored || (cb != null && cb.TransferPending) || (wi != null && wi.TxnPending)) return false;
            bool money = cls == LootClass.Money;
            var wal = WalletSync.Instance;
            if (money && (wal == null || !wal.ClientActive)) return RefuseMoney(e.Name, "shared money mirror not active in this scene");
            if (!p2.PrepareTransfer()) return false;
            uint txn = p2.AppliedTxn + 1;
            var req = new LootTxnRequest { Txn = txn, Ns = ns, Hash = e.Hash, ItemId = e.ItemId, Digest = cat.Digest };
            _txn = new Txn { Txn_ = txn, Ns = ns, Hash = e.Hash, Pick = pick, Item = money ? null : ItemOf(e.ItemId), Gm = _world.GameManagerObject, Name = e.Name, At = Now, Cls = cls, Money = money ? e.Money : 0 };
            _txn.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.LootTxn, Value = req.Encode() });
            _p2Requests++;
            _log.Info("WLOOT pickup #" + txn + " of '" + e.Name + "' (" + IdentityNamespace.Name(ns) + ":" + e.Hash.ToString("X8") + ", " + cls + (money ? " $" + e.Money : ", item " + e.ItemId) + ") requested (proposal " + _txn.Id + ")");
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
                _world.Host.Loot.Cabinets.Take(c.Ns, c.Group, c.Outcome);
                if (c.Shadow) _shadowCommits++; else _p1Commits++;
                _log.Info("WLOOT Player 1 took '" + c.Name + "' (" + IdentityNamespace.Name(c.Ns) + ":" + c.Hash.ToString("X8") + ", " + (c.Shadow ? "not shared, original local pickup" : c.Cls.ToString()) + (c.Family ? ", shared-name family" : "") + (c.Group != 0 ? ", cabinet outcome " + c.Outcome : "")
                    + ") -> " + code + " after " + ((Now - c.At) * 1000).ToString("0") + " ms: the original granted it once on this PC; listed as taken (Player 2's copy is removed)");
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

        // WorldHost.LootTransaction hook. W4.2D: validated against the host's OWN scene-file catalog of that namespace (no need
        // to have loaded the region), only after the region's book was seeded for the current occupancy; then the durable
        // Player-2 side (record / wallet + world take in one write), the original key effects and the original side effects
        // the host owns (parcel lists).
        ProposalCode HostDecide(byte slot, LootTxnRequest t, bool inRegion, out string why)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                why = null;
                var p2 = Player2Sync.Instance;
                if (p2 == null || !p2.HostBookReady) { why = "host Player-2 book not ready"; return ProposalCode.Busy; }
                var ps = Ps(); var inv = ps != null ? _psInv(ps) : null;
                var book = _world.Host.Loot;
                bool fresh = t.Txn > p2.P2AppliedTxn;   // retransmissions are answered by DecideP2 before anything else
                // host scene loading: item facts / day count unavailable -> retry later (never a permanent refusal)
                if (inv == null && fresh) { why = "host scene loading"; return ProposalCode.Busy; }
                var cat = HostLootCatalogs.Get(t.Ns);
                if (fresh && cat == null && HostLootCatalogs.Pending(t.Ns)) { why = "host loot catalog of " + IdentityNamespace.Name(t.Ns) + " still building"; return ProposalCode.Busy; }
                if (fresh && cat != null && !book.IsSeeded(t.Ns)) { why = "loot state of " + IdentityNamespace.Name(t.Ns) + " not initialized for this occupancy yet"; return ProposalCode.Busy; }
                LootEntry e = null; var facts = new ItemFacts(); bool persist = false;
                if (cat != null && cat.TryGet(t.Hash, out e))
                {
                    facts = ItemFactsFor(e);
                    string ignore; var c0 = LootRules.Classify(e, facts, out ignore);
                    if (c0 != LootClass.Excluded && inv != null)
                        persist = LootRules.TakenByKeys(c0, (e.Flags & LootFlags.SharedName) != 0, _get(LootKeys.Day(e.Name)), _get(LootKeys.Picked(e.Name)), _invDays(inv));
                }
                LootClass cls;
                var code = LootDecision.DecideP2(t.Txn, p2.P2AppliedTxn, inRegion, cat, t.Digest, t.Hash, t.ItemId, facts, book.StateOf(t.Ns, t.Hash), book.HasRoom(t.Ns, t.Hash), p2.P2Count(t.ItemId), persist, out cls, out why);
                if (code == ProposalCode.Accepted && !book.Cabinets.Allowed(t.Ns, e.Group, e.Outcome))
                {
                    book.Cabinets.Refused++; persist = true; code = ProposalCode.Stale;
                    why = "this cabinet slot already gave another outcome in this occupancy (one roll per slot)";
                }
                if (code != ProposalCode.Accepted)
                {
                    if (code != ProposalCode.Unchanged) { _hostRefused++; _log.Info("WLOOT Player-2 pickup #" + t.Txn + " " + IdentityNamespace.Name(t.Ns) + ":" + t.Hash.ToString("X8") + (e != null ? " '" + e.Name + "'" : "") + " -> " + code + " (" + why + ")"); }
                    if (persist && code == ProposalCode.Stale && book.StateOf(t.Ns, t.Hash) == LootState.Available)
                    { _persistRefused++; book.Commit(t.Ns, t.Hash, _world.Host.Reg, _world.Host.Outbox); _world.FlushOutbox(); _dirty = true; }   // listed as taken: Player 2's copy disappears
                    return code;
                }
                if (inv == null) { why = "host scene loading"; return ProposalCode.Busy; }
                bool money = cls == LootClass.Money;
                var take = new LootTake { Ns = t.Ns, Hash = t.Hash, ItemId = e.ItemId, Day = LootRules.TakeDay(cls, _invDays(inv), facts.Value), ItemFlag = true, Name = e.Name };
                code = money ? p2.HostMoneyTake(t.Txn, take, e.Money, out why) : p2.HostLootTake(t.Txn, take, out why);
                if (code == ProposalCode.Accepted)
                {
                    _hostDecided++; if (money) _moneyTakes++; else if (cls == LootClass.UniqueValuable) _uniqueTakes++; else if (cls == LootClass.LiveOnly) _liveOnlyTakes++;
                    bool family = (e.Flags & LootFlags.SharedName) != 0; if (family) _sharedNameTakes++;
                    book.Cabinets.Take(t.Ns, e.Group, e.Outcome); if (e.Group != 0) _cabinetTakes++;
                    string side = HostSideEffects(e);
                    var live = WorldCensus.Instance != null ? WorldCensus.Instance.Loot(t.Ns) : null; LootEntry le = null;
                    var obj = live != null && live.TryGet(t.Hash, out le) ? le.Target as Component : null;
                    _log.Info("WLOOT Player-2 pickup #" + t.Txn + " of '" + e.Name + "' (" + IdentityNamespace.Name(t.Ns) + ":" + t.Hash.ToString("X8") + ", " + cls + (family ? ", shared-name family" : "") + (e.Group != 0 ? ", cabinet outcome " + e.Outcome : "") + ") ACCEPTED by the host: "
                        + (money ? "$" + e.Money + " credited once to the shared balance" : "item " + e.ItemId + " +1 in Player 2's record") + ", world take durable, ItemDay " + (take.Day > 0 ? take.Day.ToString() : "none") + side
                        + "; host copy " + (obj != null ? "removed" : "not loaded here"));
                    if (obj != null) Remove(obj, e.Name, "taken by Player 2");   // a Player-1 claim on it would have made the decision Busy
                    _dirty = true;
                }
                else _hostRefused++;
                return code;
            }
            finally { double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _decideMaxMs) _decideMaxMs = ms; }
        }

        // Original side effects of the pickup that belong to the host campaign (verified <PickingItem>d__72 final step):
        // lock_parcel_after_pickup -> GameManager.mission_strings_to_set.Add(parcel_pref) + parcel_strings_to_set.Remove(parcel_pref).
        // Contract items only show "contract_item_found" there (nothing to apply). Executed once, for the accepted take only.
        string HostSideEffects(LootEntry e)
        {
            if ((e.Flags & LootFlags.Parcel) == 0 || string.IsNullOrEmpty(e.Pref)) return "";
            var gm = _world.GameManagerObject;
            var mission = gm != null && _gmMission != null ? _gmMission(gm) as System.Collections.IList : null;
            var parcels = gm != null && _gmParcels != null ? _gmParcels(gm) as System.Collections.IList : null;
            if (mission == null || parcels == null) { _log.Warn("WLOOT parcel '" + e.Pref + "': host GameManager mission lists unavailable - parcel lock NOT applied"); return ", parcel lock NOT applied"; }
            mission.Add(e.Pref); parcels.Remove(e.Pref); _parcelEffects++;
            return ", parcel '" + e.Pref + "' locked in the host campaign (original mission_strings_to_set / parcel_strings_to_set)";
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
            _awaitMoney = false;
            if (t.Cls == LootClass.Money)
            {
                // the host already credited the shared balance and the record counter; the original coroutine's add_money is
                // counted by the mirror into the same counter value (await it; credit directly if the coroutine cannot run)
                var wal = WalletSync.Instance; int local;
                if (wal == null || !wal.TryClientCounter(out local)) { _log.Info("WLOOT money pickup #" + t.Txn_ + " " + why + ": mirror inactive - the record restore carries it"); if (p2 != null && p2.AppliedTxn < t.Txn_) p2.AppliedTxn = t.Txn_; return; }
                _awaitItem = null; _awaitMoney = true; _awaitCovered = false; _awaitMoneyAmount = t.Money;
            }
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
            if (_awaitMoney) { FinishMoney(why, true); return; }   // never started / interrupted: the original did not add it
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

        // W4.1B money: the coroutine's add_money was counted by the mirror (object destroyed in the same coroutine step) or, when
        // it could not run, the amount is added to the live field once (counted like the original's own addition). If the
        // record was restored in between and already contains this pickup (_awaitCovered), the restore carried the amount:
        // the coroutine's addition is reverted by the armed exclusion and nothing is credited here. Exactly once in every case.
        void FinishMoney(string why, bool credit)
        {
            uint txn = _awaitTxn; _awaitMoney = false; _awaitPick = null;
            var wal = WalletSync.Instance;
            if (_awaitCovered) { credit = false; why += " (already contained in the restored record)"; if (wal != null) wal.DisarmClientExclusion(); }
            _awaitCovered = false;
            if (credit && wal != null && wal.ClientCredit(_awaitMoneyAmount)) { _directCounts++; why += " (amount added directly)"; }
            var p2 = Player2Sync.Instance; if (p2 != null && p2.AppliedTxn < txn) p2.AppliedTxn = txn;
            _log.Info("WLOOT money pickup #" + txn + " applied locally: $" + _awaitMoneyAmount + " - " + why);
        }

        // W4.1B: the client restored a host record while a money pickup was still awaiting its coroutine (e.g. the report sent
        // just before the pickup was refused -> resync). If that record already contains the pickup, the restored counter
        // includes the amount, so the coroutine's own add_money must not be counted again (one-shot exclusion, 3 s).
        public void OnRecordRestored(P2Inventory rec)
        {
            if (!_awaitMoney || _awaitCovered || rec == null || rec.AppliedTxn < _awaitTxn) return;
            var wal = WalletSync.Instance;
            if (wal == null) return;
            wal.ArmClientExclusion(_awaitMoneyAmount, 3.0);
            _awaitCovered = true;
            _log.Info("WLOOT money pickup #" + _awaitTxn + ": the restored record already contains it - the original's add_money is not counted again");
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
            if (Awaiting)
            {
                int wl; var wal = WalletSync.Instance;
                if (_awaitMoney && (wal == null || !wal.TryClientCounter(out wl))) { _awaitMoney = false; _awaitPick = null; var p2m = Player2Sync.Instance; if (p2m != null && p2m.AppliedTxn < _awaitTxn) p2m.AppliedTxn = _awaitTxn; _log.Info("WLOOT money pickup #" + _awaitTxn + ": scene left before the original finished - the record restore carries it"); }
                // money: the original adds add_money and destroys the object in ONE coroutine step (IL_034d .. IL_046f), so
                // "object destroyed" = counted by the mirror; a counter target would be wrong if Player 2 spends meanwhile
                else if (_awaitMoney && _awaitPick == null) FinishMoney("original pickup finished", false);
                else if (!_awaitMoney && _itemAmount(_awaitItem) >= _awaitAmount) Finish("original pickup counted");
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
            if (_host) SeedOccupied(now);
            if (!_host && !Live) return;
            // W4.2D: this instance shows two namespaces - its region scene and MainScene (present in every region)
            int region = _world.LocalRegion;
            var reg = _world.Registry;
            if (!_dirty && region == _shownNs && reg.HeadRev == _seenHead && reg.Epoch == _seenEpoch) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch; _dirty = false; _shownNs = region;
            _taken.Clear(); _takenMain.Clear();
            if (region != Regions.None && LootRules.SupportedNamespace(region)) Observe(region, _taken, reg);
            if (region != Regions.None) Observe(IdentityNamespace.MainScene, _takenMain, reg);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _observeMaxMs) _observeMaxMs = ms;
        }

        readonly HashSet<uint> _takenMain = new HashSet<uint>();
        bool IsTaken(int ns, uint hash) { return ns == IdentityNamespace.MainScene ? _takenMain.Contains(hash) : ns == _shownNs && _taken.Contains(hash); }

        void Observe(int ns, HashSet<uint> into, StateRegistry reg)
        {
            for (int c = 0; c < LootBook.Chunks; c++)
            {
                var e = reg.Get(StateScope.Region, ns, LootBook.Keys[c]);
                if (e != null && !LootBook.TryParse(e.Value, c, into)) _log.Warn("WLOOT registry chunk " + c + " of " + IdentityNamespace.Name(ns) + " unreadable");
            }
            var cat = LiveCatalog(ns);
            if (cat == null) return;                                   // census of this scene not available yet: retried on the next change
            foreach (var h in into)
            {
                LootEntry le; if (!cat.TryGet(h, out le)) continue;
                var obj = le.Target as Component;
                if (obj == null) continue;                             // already destroyed (original Start by the ItemDay_ key, or removed)
                if (OwnPickupRunning(obj, h)) continue;
                Remove(obj, le.Name, "taken");
            }
        }

        // W4.2D host: a namespace's book is seeded once per OCCUPANCY (a player entered that region; MainScene: re-seeded after
        // every region change, i.e. every original MainScene reload), from the host's own scene-file catalog and the host's keys
        // with the original reload rule (<Start>d__68 destroys while daysInGame < ItemDay_<name>; destroy_if_was_picked with
        // Item_<name> = 1). This replaces the former "host scene loaded" seeding: the host may never load the region at all.
        // WorldHost clears a region's book (and its seeded mark) when the region empties, and MainScene's at every region change.
        double _nextSeed;
        void SeedOccupied(double now)
        {
            if (now < _nextSeed || _world.Host == null) return;
            _nextSeed = now + 0.5;
            var book = _world.Host.Loot;
            int p1 = _world.LocalRegion, p2 = _world.Host.RegionOf(2);
            if (p1 != Regions.None || p2 != Regions.None) SeedRegion(IdentityNamespace.MainScene, book);
            if (p1 != Regions.None) SeedRegion(p1, book);
            if (p2 != Regions.None && p2 != p1) SeedRegion(p2, book);
        }

        void SeedRegion(int ns, LootBook book)
        {
            if (book.IsSeeded(ns) || !LootRules.SupportedNamespace(ns)) return;
            var cat = HostLootCatalogs.Get(ns);
            if (cat == null) { if (!HostLootCatalogs.Pending(ns)) book.MarkSeeded(ns); return; }   // no catalog (no scene / unusable): nothing to seed, takes are refused
            var ps = Ps(); var inv = ps != null ? _psInv(ps) : null;
            if (inv == null) return;                                   // host scene loading: day count needed, retried
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int today = _invDays(inv), n = 0, unique = 0, family = 0;
            CampaignData keys; string err; Func<string, int> get = _get;
            if (FbppApi.ReadModel(out keys, out err)) get = k => { int v; return keys.TryInt(k, out v) ? v : 0; };   // one model read, O(1) lookups
            foreach (var e in cat.Entries)
            {
                if (book.StateOf(ns, e.Hash) != LootState.Available || !book.HasRoom(ns, e.Hash)) continue;
                string ignore; var cls = LootRules.Classify(e, ItemFactsFor(e), out ignore);
                if (!LootRules.PersistentlyTaken(cls, get(LootKeys.Day(e.Name)), get(LootKeys.Picked(e.Name)), today)) continue;
                if (book.Commit(ns, e.Hash, _world.Host.Reg, _world.Host.Outbox) != ProposalCode.Accepted) continue;
                n++; if (cls == LootClass.UniqueValuable) unique++; if ((e.Flags & LootFlags.SharedName) != 0) family++;
            }
            book.MarkSeeded(ns); _seededRegions++;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (n > 0) { _world.FlushOutbox(); _uniqueSeeded += n; }
            _dirty = true;
            _log.Info("WLOOT " + IdentityNamespace.Name(ns) + " occupancy seeded from the host catalog: " + n + " of " + cat.Count + " object(s) already removed by the host save listed as taken (unique " + unique + ", shared-name family members " + family + ", day " + today + ", " + ms.ToString("0.0") + " ms" + (err != null ? ", key fallback: " + err : "") + ")");
        }

        // W4.2D RandomFileCabinet: the ORIGINAL Start rolls its slots with UnityEngine.Random; both instances run it with the same
        // seed (cabinet census identity + campaign day), then the global Random state is restored. No outcome is replaced.
        static object _savedRandom; static bool _seededCabinet;
        static void CabinetPrefix(object __instance)
        {
            var i = Instance; var c = __instance as Component; _seededCabinet = false;
            if (i == null || c == null || WorldCensus.Instance == null) return;
            try
            {
                uint hash; if (!WorldCensus.Instance.TryCabinetId(c, out hash)) return;
                var ps = i.Ps(); var inv = ps != null ? i._psInv(ps) : null; if (inv == null) return;
                int day = i._invDays(inv);
                _savedRandom = i._rndState.GetValue(null, null);
                i._rndInit.Invoke(null, new object[] { CabinetBook.Seed(hash, day) });
                _seededCabinet = true; i._cabinetSeeds++;
            }
            catch (Exception ex) { i._log.Error("WLOOT cabinet seed failed (original random roll)", ex); }
        }
        static void CabinetPostfix()
        {
            var i = Instance;
            if (!_seededCabinet || i == null) return;
            _seededCabinet = false;
            try { i._rndState.SetValue(null, _savedRandom, null); } catch (Exception ex) { i._log.Error("WLOOT Random state restore failed", ex); }
        }
        long _cabinetSeeds;

        // This instance's own original pickup of that object is in progress: its coroutine removes it.
        bool OwnPickupRunning(Component obj, uint hash)
        {
            if (_pickPicking(obj)) return true;
            if (_host) return _claims.ContainsKey(obj.GetInstanceID());
            return (_txn != null && _txn.Hash == hash && ReferenceEquals(_txn.Pick, obj)) || (Awaiting && ReferenceEquals(_awaitPick, obj));
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
                    + " removed " + _removed + " notShared " + _notShared + " moneyTakes " + _moneyTakes + " uniqueTakes " + _uniqueTakes + " familyTakes " + _sharedNameTakes + " moneyRefused " + _moneyRefused + " persistRefused " + _persistRefused + " seeded " + _uniqueSeeded + " liveOnlyTakes " + _liveOnlyTakes + " cabinetTakes " + _cabinetTakes + " cabinetSeeds " + _cabinetSeeds + " parcelEffects " + _parcelEffects + " seededRegions " + _seededRegions + (_host && _world.Host != null ? " cabinetRefused " + _world.Host.Loot.Cabinets.Refused + " | " + HostLootCatalogs.Summary : "")
                    + (_host ? " shadowClaims " + _shadowClaims + " shadowCommits " + _shadowCommits : " p2Refused(local) " + _p2RefusedLocal + " heavyRefused " + _heavyRefused + " ownDrops " + _p2OwnDrops) + " takenHere " + _taken.Count + " pickMax " + _pickMaxMs.ToString("0.00") + "ms decideMax " + _decideMaxMs.ToString("0.00") + "ms observeMax " + _observeMaxMs.ToString("0.00") + "ms";
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
