// M5 W3B shared dropped items on the ORIGINAL drop / pickup code (design + verified IL: src/Shared/WorldItems.cs).
// Harmony (narrow, verified IL):
//   Inventory.DropItemNewSystem()                  prefix/postfix: a backpack drop becomes a host transaction; the original
//                                                  method runs exactly once (host: at once; client: after the host committed the
//                                                  Player-2 record -1) and the object it instantiates is bound to the registry id.
//   Pickupable.PickUp()                            prefix: a registered dropped item is claimed from the host first (one winner);
//                                                  the original pickup coroutine runs only for the winner.
//   ItemID.Item_Picked(string,float,bool,bool)     postfix (host): Player 1 received an item Player 2 dropped -> host journal.
//   (W4.1: the same PickUp prefix hands every non-dropped object to WorldLootSync.AllowPick - pre-placed world loot.)
//   GameManager.TryToMoveDroppedItemsToStorage()   prefix: the original dropped-item -> hideout-locker conversion (SaveGameNewSystem
//                                                  in Hillville) runs on the HOST locker for registered items (client: host request).
// Presentation: every instance shows the registry items of its own region with the item's original drop_object prefab
// (Pickupable configured like Inventory.DropItemNewSystem does, rigidbody kinematic). The dropper's own original object keeps
// its original physics; its rest pose is reported once it settles (and again only if it is moved), never streamed.
// W3.2: every original drop happens inside the pause menu (timeScale 0), so the registry pose of a new item is the original
// dropPos in the air. Such a "falling" item is shown on the surface below that point (SettleRules, one raycast per event) until
// the dropper's original physics reports the rest pose ("settled", registry bit). W3.1 showed it in the air, solid -> floating
// obstacle whenever the dropper stayed in the menu or a settle report was skipped.
// Not shared (original local behaviour, logged): heavy items, stolen cars, drones, story / mission items, items without a
// drop_object - they use special original mechanics.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TS2MP.Shared;
using UnityEngine;

namespace TS2MP.Plugin
{
    internal sealed class WorldItemSync : IDisposable
    {
        public static WorldItemSync Instance;

        readonly SessionNode _node; readonly InstanceLog _log; readonly WorldSync _world; readonly bool _host; readonly byte _me;
        Harmony _h; bool _ok;
        Type _pickT, _itemT;
        Func<object, object> _gmPs, _psItems, _invItem, _itemDrop, _pickItem; Func<object, Transform> _invDropPos; Func<object, bool> _gmLoaded;
        Func<object, int> _itemId, _itemAmount, _itemMax, _itemX10, _itemLocker, _pickId; Func<object, bool> _itemHeavy, _itemCar, _itemDrone, _itemStory, _itemMission, _pickDropped, _pickHeavy, _pickPicking;
        Action<object, int> _setPickId; Action<object, bool> _setPickDropped, _setPickX10, _setPickXp, _setPickDestroy;
        Action<object> _dropItem, _invDrop, _pickUp, _moveToLockerOnDrop; MethodInfo _itemPicked;

        // Own = this instance runs the ORIGINAL object's physics (the dropper); otherwise a presentation of the registry item.
        // Reported: the first rest pose was sent. Settled/SrcPos/SrcRot: the registry state last presented (observer).
        sealed class Bound
        {
            public uint Id; public int ItemId; public GameObject Go; public Component Pick; public Rigidbody Rb; public bool Own, Claiming, Reported, Settled, Diag;
            public Vector3 LastPos, DropPos, SrcPos; public Quaternion LastRot, SrcRot; public double NextCheck, LastTick, UnpausedFor, BornAt;
        }
        readonly Dictionary<uint, Bound> _bound = new Dictionary<uint, Bound>();
        readonly Dictionary<int, uint> _byObject = new Dictionary<int, uint>();   // GameObject instance id -> world id
        readonly Dictionary<int, object> _itemById = new Dictionary<int, object>();
        object _itemsGm, _sceneGm; int _shownRegion = Regions.None; uint _shownRev; bool _forceReconcile;

        // client transaction (one Player-2 transaction at a time, shares AppliedTxn with storage transfers)
        sealed class Txn { public uint Id, Txn_; public ItemOp Op; public object Item; public object Inv; public uint WorldId; public Component Pick; public double At; public bool Lost; public Vector3 Pos; public Quaternion Rot; }
        Txn _txn;
        // accepted client pickup waiting for the original coroutine to add the item
        object _awaitItem; int _awaitAmount; uint _awaitTxn; double _awaitUntil;
        // host / client original drop capture
        bool _resumeDrop; Component _resumePick; readonly DropCapture _capture = new DropCapture();
        // host: Player 1 picking up a Player-2 item -> journal when the original Item_Picked has counted it
        int _p1ExpectItem = int.MinValue; double _p1ExpectUntil;
        long _drops, _pickups, _refused, _spawned, _removed, _settles, _converted, _notShared, _lost, _projected, _projectMiss, _settleApplied;
        readonly DiagBudget _diag = new DiagBudget(8);   // W3.2: detailed WITEM_DIAG lines for the first 8 items of the session
        double _costMaxMs; double _nextStats;

        static double Now { get { return Time.realtimeSinceStartup; } }
        public bool TxnPending { get { return (_txn != null && !_txn.Lost) || _awaitItem != null; } }
        public bool HasLostTxn { get { return _txn != null && _txn.Lost; } }

        public WorldItemSync(SessionNode node, InstanceLog log, WorldSync world)
        {
            _node = node; _log = log; _world = world; _host = node.Role == Role.Host; _me = world.LocalSlot;
            try
            {
                var gm = AccessTools.TypeByName("GameManager"); var ps = AccessTools.TypeByName("PlayerScript"); var inv = AccessTools.TypeByName("Inventory");
                _itemT = AccessTools.TypeByName("ItemID"); _pickT = AccessTools.TypeByName("Pickupable");
                _gmPs = Fast.Getter<object>(gm, "PS"); _gmLoaded = Fast.Getter<bool>(gm, "loadedMap"); _psItems = Fast.Getter<object>(ps, "item_IDs2");
                _invItem = Fast.Getter<object>(inv, "current_item_script"); _pickItem = Fast.Getter<object>(_pickT, "itemScript");
                var dp = AccessTools.Field(inv, "dropPos"); if (dp != null) { var g = Fast.Getter<object>(inv, "dropPos"); _invDropPos = o => g(o) as Transform; }
                _itemDrop = Fast.Getter<object>(_itemT, "drop_object"); _itemId = Fast.Getter<int>(_itemT, "item_ID"); _itemAmount = Fast.Getter<int>(_itemT, "item_amount"); _itemMax = Fast.Getter<int>(_itemT, "max_amount");
                _itemX10 = Fast.Getter<int>(_itemT, "x10xp_amount_of"); _itemLocker = Fast.Getter<int>(_itemT, "item_amount_locker");
                _itemHeavy = Fast.Getter<bool>(_itemT, "is_heavy"); _itemCar = Fast.Getter<bool>(_itemT, "isCar"); _itemDrone = Fast.Getter<bool>(_itemT, "isDrone"); _itemStory = Fast.Getter<bool>(_itemT, "isStoryItem"); _itemMission = Fast.Getter<bool>(_itemT, "isMissionItem");
                _pickId = Fast.Getter<int>(_pickT, "item_ID"); _setPickId = Fast.Setter<int>(_pickT, "item_ID"); _pickDropped = Fast.Getter<bool>(_pickT, "wasDropped"); _setPickDropped = Fast.Setter<bool>(_pickT, "wasDropped");
                _setPickX10 = Fast.Setter<bool>(_pickT, "x10XP"); _setPickXp = Fast.Setter<bool>(_pickT, "add_any_XP"); _setPickDestroy = Fast.Setter<bool>(_pickT, "destroy_if_was_picked");
                _pickHeavy = Fast.Getter<bool>(_pickT, "is_heavy_item"); _pickPicking = Fast.Getter<bool>(_pickT, "picking");
                _dropItem = Fast.Call0(_itemT, "Drop_Item"); _invDrop = Fast.Call0(inv, "DropItemNewSystem"); _pickUp = Fast.Call0(_pickT, "PickUp"); _moveToLockerOnDrop = Fast.Call0(_itemT, "MoveToStorageOnDrop");
                _itemPicked = AccessTools.Method(_itemT, "Item_Picked", new[] { typeof(string), typeof(float), typeof(bool), typeof(bool) });
                var drop = AccessTools.Method(inv, "DropItemNewSystem", Type.EmptyTypes);
                var pick = AccessTools.Method(_pickT, "PickUp", Type.EmptyTypes);
                var conv = AccessTools.Method(gm, "TryToMoveDroppedItemsToStorage", Type.EmptyTypes);
                if (drop == null || pick == null || conv == null || _itemPicked == null || _gmPs == null || _psItems == null || _invItem == null || _invDropPos == null || _itemDrop == null || _itemId == null
                    || _pickId == null || _setPickId == null || _setPickDropped == null || _setPickX10 == null || _setPickXp == null || _setPickDestroy == null || _dropItem == null || _invDrop == null || _pickUp == null || _moveToLockerOnDrop == null
                    || _itemHeavy == null || _itemCar == null || _itemDrone == null || _itemStory == null || _itemMission == null || _itemMax == null || _itemX10 == null || _itemLocker == null || _pickDropped == null || _pickHeavy == null || _pickItem == null)
                { _log.Warn("WITEM game API incomplete: shared dropped items disabled (original local drops)"); return; }
                _h = new Harmony("com.ts2mp.multiplayer.witems");
                // W3.1: the dropped object is captured from the original Instantiate call itself (transpiler, exactly one
                // call site). W3 searched the "Pickupable" tag, which the original only sets in Pickupable.<Start> after
                // WaitForSecondsRealtime(0.05) -> never found. Without the capture point shared drops stay off (original drop).
                _dropSites = 0;
                _h.Patch(drop, new HarmonyMethod(AccessTools.Method(typeof(WorldItemSync), "DropPrefix")), new HarmonyMethod(AccessTools.Method(typeof(WorldItemSync), "DropPostfix")),
                         new HarmonyMethod(AccessTools.Method(typeof(WorldItemSync), "DropTranspiler")));
                if (_dropSites != 1)
                {
                    _h.Unpatch(drop, HarmonyPatchType.All, _h.Id);
                    _log.Warn("WITEM Inventory.DropItemNewSystem has " + _dropSites + " Instantiate<GameObject> call sites (expected 1): shared drops disabled, original local drops");
                    _dropsOn = false;
                }
                _h.Patch(pick, new HarmonyMethod(AccessTools.Method(typeof(WorldItemSync), "PickPrefix")));
                _h.Patch(conv, new HarmonyMethod(AccessTools.Method(typeof(WorldItemSync), "ConvertPrefix")));
                if (_host) _h.Patch(_itemPicked, null, new HarmonyMethod(AccessTools.Method(typeof(WorldItemSync), "PickedPostfix")));
                _ok = true; Instance = this;
                if (_host && world.Host != null) world.Host.ItemTxnHook = HostHook;
                world.ProposalResultHook4 = OnResult;
                _log.Info("WITEM shared dropped items ready (" + (_host ? "host: registry owner" : "client: drops/pickups as host transactions") + ")");
            }
            catch (Exception ex) { _log.Error("WITEM init failed: shared dropped items disabled", ex); _ok = false; }
        }

        bool Live { get { return _node.IsConnected && _world.Synced; } }
        object Ps() { var gm = _world.GameManagerObject; return gm != null ? _gmPs(gm) : null; }

        object ItemOf(int id)
        {
            var gm = _world.GameManagerObject;
            if (!ReferenceEquals(gm, _itemsGm) || _itemById.Count == 0)
            {
                _itemsGm = gm; _itemById.Clear();
                var arr = gm != null ? _psItems(_gmPs(gm)) as Array : null;
                if (arr != null) foreach (var o in arr) if (o != null && !_itemById.ContainsKey(_itemId(o))) _itemById[_itemId(o)] = o;
            }
            object it; return _itemById.TryGetValue(id, out it) ? it : null;
        }

        // Ordinary backpack items only; the rest has special original mechanics (stays local, unchanged behaviour).
        bool Shareable(object item)
        {
            return item != null && _itemDrop(item) as GameObject != null && !_itemHeavy(item) && !_itemCar(item) && !_itemDrone(item) && !_itemStory(item) && !_itemMission(item);
        }

        // ======================================================================== drop
        static bool DropPrefix(object __instance)
        {
            var i = Instance; if (i == null) return true;
            try { return i.AllowDrop(__instance); }
            catch (Exception ex) { i._capture.Take(out _unused); i._log.Error("WITEM drop hook failed (drop refused)", ex); return !i.Live; }
        }

        bool AllowDrop(object inv)
        {
            if (_resumeDrop) { _resumeDrop = false; _capture.Arm(_invItem(inv)); return true; }   // our confirmed client drop
            var item = _invItem(inv);
            int region = _world.LocalRegion;
            int count = 0;
            if (region != Regions.None)
            {
                if (_host) count = _world.Host.Items.CountIn(region);
                else { List<WorldItem> cur; var ce = _world.Registry.Get(StateScope.Region, region, WorldKeys.WorldItems); if (ce != null && WorldItemList.TryParse(ce.Value, out cur)) count = cur.Count; }
            }
            var p2 = Player2Sync.Instance; var cb = CarBoxSync.Instance;
            bool busy = _txn != null || _awaitItem != null || (cb != null && cb.TransferPending) || (WorldLootSync.Instance != null && WorldLootSync.Instance.TxnPending);
            string why;
            // W3.1: a drop that cannot become a shared item is refused BEFORE the original changes the inventory (nothing
            // happens, like any refused original interaction); never an untracked extra object
            var d = DropGate.Decide(Live, _host, Shareable(item), region, count, busy, _host || (p2 != null && p2.Restored), out why);
            switch (d)
            {
                case DropDecision.Original:
                    if (Live) { _notShared++; _log.Info("WITEM drop of item " + (item != null ? _itemId(item).ToString() : "?") + " not shared (" + why + ")"); }
                    return true;
                case DropDecision.Refuse: RefuseLog(why); return false;
                case DropDecision.HostOriginal: _capture.Arm(item); return true;   // original now; captured from its Instantiate, registered in the postfix
            }
            if (!p2.PrepareTransfer()) { RefuseLog("Player-2 report still in flight"); return false; }
            var drop = _itemDrop(item) as GameObject;
            var pos = DropPos(inv); var rot = drop.transform.rotation;
            uint txn = p2.AppliedTxn + 1;
            var t = new ItemTxnRequest { Op = ItemOp.Drop, Txn = txn, ItemId = _itemId(item), Region = region, Pos = new Vec3(pos.x, pos.y, pos.z), Rot = Quat16.From(rot.x, rot.y, rot.z, rot.w) };
            _txn = new Txn { Op = ItemOp.Drop, Txn_ = txn, Item = item, Inv = inv, At = Now, Pos = pos, Rot = rot };
            _txn.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ItemTxn, Value = t.Encode() });
            _log.Info("WITEM drop #" + txn + " item " + t.ItemId + " requested in " + Regions.Name(region) + " (proposal " + _txn.Id + ")");
            return false;
        }

        Vector3 DropPos(object inv) { var t = _invDropPos(inv); return t != null ? t.position : Vector3.zero; }

        double _lastRefuseLog = -100; long _refusedDrops;
        void RefuseLog(string why)
        {
            _refusedDrops++;
            if (Now - _lastRefuseLog >= 2.0) { _lastRefuseLog = Now; _log.Info("WITEM drop refused before the original ran (inventory unchanged): " + why + " [refused drops " + _refusedDrops + "]"); }
        }

        // ---- capture point: the result of the original Instantiate<GameObject>(drop_object, dropPos, rotation) ----
        static int _dropSites; static object _unused;
        static IEnumerable<CodeInstruction> DropTranspiler(IEnumerable<CodeInstruction> code)
        {
            var hook = AccessTools.Method(typeof(WorldItemSync), "OnDropInstantiated");
            foreach (var ci in code)
            {
                yield return ci;
                var mi = ci.operand as MethodInfo;
                if ((ci.opcode == System.Reflection.Emit.OpCodes.Call || ci.opcode == System.Reflection.Emit.OpCodes.Callvirt) && mi != null && mi.Name == "Instantiate" && mi.ReturnType == typeof(GameObject))
                {
                    _dropSites++;
                    if (_dropSites == 1) yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, hook);   // GameObject -> same GameObject
                }
            }
        }

        static GameObject OnDropInstantiated(GameObject go)
        {
            var i = Instance;
            if (i != null) i._capture.Seen(go);
            return go;
        }

        static void DropPostfix(object __instance)
        {
            var i = Instance; if (i == null || !i._capture.Armed) return;
            object item; var go = i._capture.Take(out item) as GameObject;
            try { i.CaptureDrop(item, go); }
            catch (Exception ex) { i._log.Error("WITEM drop capture failed", ex); }
        }

        // Postfix: the captured object now carries the fields the original set after Instantiate (item_ID, wasDropped, ...).
        void CaptureDrop(object item, GameObject go)
        {
            if (item == null) return;
            int id = _itemId(item);
            var found = go != null ? go.GetComponent(_pickT) : null;
            if (found == null || _pickId(found) != id || !_pickDropped(found))
            {
                _log.Error("WITEM original drop of item " + id + " produced no capturable object (" + (go == null ? "no Instantiate seen" : "no Pickupable / other item") + "): the object stays a LOCAL item of this instance (inventory already changed by the original, item not lost)");
                return;
            }
            if (_host)
            {
                int region = _world.LocalRegion; var p2 = Player2Sync.Instance;
                uint seq = p2 != null ? p2.HostDropMark(item, id) : 0;
                var rot = found.transform.rotation; var pos = found.transform.position;
                var t = new ItemTxnRequest { Op = ItemOp.Drop, Txn = seq, ItemId = id, Region = region, Pos = new Vec3(pos.x, pos.y, pos.z), Rot = Quat16.From(rot.x, rot.y, rot.z, rot.w) };
                string why; var code = _world.Host.ItemTransaction(_me, t, _world.Clock, out why); _world.FlushOutbox();
                if (code != ProposalCode.Accepted) { _log.Info("WITEM host drop of item " + id + " not registered: " + code + " (" + why + ") - stays a local object"); return; }
                uint wid = _world.Host.Items.LastApplied;
                Bind(wid, id, found, true);
                _drops++;
                _log.Info("WITEM host dropped item " + id + " as #" + wid + " in " + Regions.Name(region) + " (host seq " + seq + ")");
                DiagDrop(wid, id, 1, region, found.gameObject);
            }
            else if (_bindAfterDrop != 0) { uint wid = _bindAfterDrop; Bind(wid, id, found, true); _bindAfterDrop = 0; DiagDrop(wid, id, _me, _world.LocalRegion, found.gameObject); }
        }

        void DiagDrop(uint wid, int itemId, byte by, int region, GameObject go)
        {
            Bound b; if (!_bound.TryGetValue(wid, out b) || !_diag.Want(wid)) return;
            b.Diag = true;
            _log.Info("WITEM_DIAG #" + wid + " item " + itemId + " dropped by P" + by + " in " + Regions.Name(region) + " at " + V3(go.transform.position)
                + " ui=" + (Time.timeScale <= 0f ? "open (timeScale 0: original physics paused until the menu closes)" : "closed (timeScale " + Time.timeScale.ToString("0.##") + ")")
                + " rb=" + RbText(b.Rb) + " colliders=" + ColText(go) + " (owner: original object, original physics)");
        }

        static string V3(Vector3 v) { return "(" + v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00") + ")"; }
        static string RbText(Rigidbody rb) { return rb == null ? "none (removed by the original Pickupable at rest)" : (rb.isKinematic ? "kinematic" : "dynamic") + (rb.useGravity ? "+gravity" : "") + (rb.IsSleeping() ? " asleep" : " awake"); }
        static string ColText(GameObject go)
        {
            int n = 0, trig = 0, off = 0;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) { n++; if (c.isTrigger) trig++; if (!c.enabled) off++; }
            return n + " (" + trig + " trigger, " + off + " disabled)";
        }
        uint _bindAfterDrop; bool _dropsOn = true;

        // ======================================================================== pickup
        static bool PickPrefix(object __instance)
        {
            var i = Instance; if (i == null) return true;
            try { return i.AllowPick(__instance as Component); }
            catch (Exception ex) { i._log.Error("WITEM pickup hook failed (pickup refused)", ex); return false; }
        }

        bool AllowPick(Component pick)
        {
            if (pick == null) return true;
            if (ReferenceEquals(_resumePick, pick)) { _resumePick = null; return true; }   // our confirmed client pickup
            uint wid;
            if (!_byObject.TryGetValue(pick.gameObject.GetInstanceID(), out wid))
            {
                var loot = WorldLootSync.Instance;                                  // W4.1: pre-placed world loot
                return loot == null || loot.AllowPick(pick);
            }
            Bound b; _bound.TryGetValue(wid, out b);
            if (b == null || b.Claiming) return false;
            int region = _world.LocalRegion;
            if (_host)
            {
                WorldItem it; int r; bool known = _world.Host.Items.TryGet(wid, out it, out r);
                var t = new ItemTxnRequest { Op = ItemOp.Pickup, WorldId = wid, Region = region, ItemId = b.ItemId };
                string why; var code = _world.Host.ItemTransaction(_me, t, _world.Clock, out why); _world.FlushOutbox();
                if (code != ProposalCode.Accepted) { _refused++; _log.Info("WITEM host pickup of #" + wid + " refused: " + code + " (" + why + ")"); Unbind(wid, true); return false; }
                Unbind(wid, false);                                        // the original coroutine destroys the object
                if (known && it.Origin == 2) { _p1ExpectItem = it.ItemId; _p1ExpectUntil = Now + 6.0; }
                _pickups++;
                _log.Info("WITEM host picked up #" + wid + " item " + b.ItemId + (known ? " (dropped by P" + it.Origin + ")" : ""));
                return true;
            }
            if (!Live) return false;
            var p2 = Player2Sync.Instance; var cb = CarBoxSync.Instance;
            if (_txn != null || _awaitItem != null || (cb != null && cb.TransferPending) || (WorldLootSync.Instance != null && WorldLootSync.Instance.TxnPending) || p2 == null || !p2.Restored) return false;
            if (!p2.PrepareTransfer()) return false;
            uint txn = p2.AppliedTxn + 1;
            var req = new ItemTxnRequest { Op = ItemOp.Pickup, Txn = txn, WorldId = wid, ItemId = b.ItemId, Region = region };
            _txn = new Txn { Op = ItemOp.Pickup, Txn_ = txn, WorldId = wid, Pick = pick, Item = ItemOf(b.ItemId), At = Now };
            _txn.Id = _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ItemTxn, Value = req.Encode() });
            b.Claiming = true;
            _log.Info("WITEM pickup #" + txn + " of #" + wid + " item " + b.ItemId + " requested (proposal " + _txn.Id + ")");
            return false;
        }

        // Host: Player 1 picked up a Player-2 item; the original Item_Picked(name, xp, wasDropped=true, ...) counted it.
        static void PickedPostfix(object __instance, bool __2)
        {
            var i = Instance; if (i == null || i._p1ExpectItem == int.MinValue || !__2) return;
            try
            {
                if (Now > i._p1ExpectUntil) { i._p1ExpectItem = int.MinValue; return; }
                if (i._itemId(__instance) != i._p1ExpectItem) return;
                i._p1ExpectItem = int.MinValue;
                var p2 = Player2Sync.Instance;
                if (p2 != null) p2.HostJournal(JournalKind.HostItem, i._itemId(__instance), __instance, "Player 1 received item " + i._itemId(__instance) + " dropped by Player 2");
            }
            catch (Exception ex) { i._log.Error("WITEM pickup journal failed", ex); }
        }

        // ======================================================================== client results
        bool OnResult(uint id, ProposalCode code)
        {
            if (_txn == null || id != _txn.Id) return false;
            var t = _txn; _txn = null;
            bool ok = code == ProposalCode.Accepted || code == ProposalCode.Unchanged;
            if (!ok)
            {
                _refused++;
                _log.Info("WITEM " + t.Op + " #" + t.Txn_ + " -> " + code + ": nothing changed");
                if (t.Op == ItemOp.Pickup) { Bound b; if (_bound.TryGetValue(t.WorldId, out b)) b.Claiming = false; _forceReconcile = true; }
                return true;
            }
            Apply(t, "acknowledged (" + code + ")");
            return true;
        }

        void Apply(Txn t, string why)
        {
            var p2 = Player2Sync.Instance;
            if (t.Op == ItemOp.Drop)
            {
                uint wid = FindCommitted(t.Txn_);
                int before = _itemAmount(t.Item);
                var inv = t.Inv;
                bool viaUi = inv != null && ReferenceEquals(_invItem(inv), t.Item) && _itemAmount(t.Item) > 0;
                if (viaUi)
                {
                    _resumeDrop = true; _bindAfterDrop = wid;
                    try { _invDrop(inv); } catch (Exception ex) { _log.Error("WITEM original drop after the acknowledgment failed", ex); }
                    finally { _resumeDrop = false; _bindAfterDrop = 0; }
                }
                if (_itemAmount(t.Item) == before)
                {
                    try { _dropItem(t.Item); } catch (Exception ex) { _log.Error("WITEM unit drop after the acknowledgment failed", ex); }   // presented from the registry
                }
                if (p2 != null) p2.AppliedTxn = t.Txn_;
                _drops++;
                _log.Info("WITEM drop #" + t.Txn_ + " " + why + ": item " + _itemId(t.Item) + " now " + _itemAmount(t.Item) + (viaUi ? ", original drop" : ", counters only") + (wid != 0 ? ", world #" + wid : ""));
                _forceReconcile = true;
                return;
            }
            // pickup: the original coroutine adds the item at its end; the transaction stays pending until it did
            Bound bb; _bound.TryGetValue(t.WorldId, out bb);
            _awaitItem = t.Item; _awaitAmount = t.Item != null ? _itemAmount(t.Item) + 1 : 0; _awaitTxn = t.Txn_; _awaitUntil = Now + 4.0;
            bool started = false;
            if (bb != null && bb.Pick != null && bb.Go != null)
            {
                Unbind(t.WorldId, false);
                _resumePick = bb.Pick;
                try { _pickUp(bb.Pick); started = true; } catch (Exception ex) { _log.Error("WITEM original pickup after the acknowledgment failed", ex); }
                finally { _resumePick = null; }
            }
            if (!started) FinishPickup("object gone - counted directly");
            _pickups++;
            _log.Info("WITEM pickup #" + t.Txn_ + " of #" + t.WorldId + " " + why + (started ? ": original pickup running" : ""));
        }

        void FinishPickup(string why)
        {
            var item = _awaitItem; uint txn = _awaitTxn; _awaitItem = null;
            if (item == null) return;
            if (_itemAmount(item) < _awaitAmount)
            {
                try { _itemPicked.Invoke(item, new object[] { "dropped", 0f, true, false }); } catch (Exception ex) { _log.Error("WITEM direct pickup count failed", ex); }
                why += " (original Item_Picked called directly)";
            }
            var p2 = Player2Sync.Instance; if (p2 != null) p2.AppliedTxn = txn;
            _log.Info("WITEM pickup #" + txn + " applied locally: item " + _itemId(item) + " now " + _itemAmount(item) + " - " + why);
        }

        uint FindCommitted(uint txn)
        {
            List<WorldItem> l; var e = _world.Registry.Get(StateScope.Region, _world.LocalRegion, WorldKeys.WorldItems);
            if (e != null && WorldItemList.TryParse(e.Value, out l)) foreach (var i in l) if (i.Origin == _me && i.OriginTxn == txn) return i.Id;
            return 0;
        }

        // After a reconnect the acknowledgment may be lost: the host record decides (same rule as CarBox transfers).
        public void ResolvePending(P2Inventory rec)
        {
            var t = _txn; if (t == null) return;
            _txn = null;
            if (rec.AppliedTxn == t.Txn_) { Apply(t, "committed by the host (acknowledgment lost)"); return; }
            _log.Info("WITEM " + t.Op + " #" + t.Txn_ + " was not committed by the host (record at " + rec.AppliedTxn + "): nothing changed");
            if (t.Op == ItemOp.Pickup) { Bound b; if (_bound.TryGetValue(t.WorldId, out b)) b.Claiming = false; }
        }

        // ======================================================================== original conversion (Hillville save)
        static bool ConvertPrefix(object __instance)
        {
            var i = Instance; if (i == null) return true;
            try { return i.Convert(); }
            catch (Exception ex) { i._log.Error("WITEM conversion hook failed (original conversion skipped)", ex); return false; }
        }

        bool Convert()
        {
            if (!Live) return true;
            int region = _world.LocalRegion;
            if (_host)
            {
                // unregistered dropped objects (local-only special items): the original conversion, object by object
                int local = 0;
                foreach (var go in GameObject.FindGameObjectsWithTag("Pickupable"))
                {
                    if (go == null || _byObject.ContainsKey(go.GetInstanceID())) continue;
                    var p = go.GetComponent(_pickT);
                    if (p == null || !_pickDropped(p) || _pickHeavy(p)) continue;
                    var it = _pickItem(p);
                    if (it != null) { _moveToLockerOnDrop(it); local++; }
                }
                int n = HostConvert(region, "host save in " + Regions.Name(region));
                _world.FlushOutbox();
                _log.Info("WITEM original dropped-item conversion (" + Regions.Name(region) + "): " + n + " shared + " + local + " local item(s) moved into the hideout locker");
                return false;
            }
            // client: its locker is not real; the host converts the registry items of this region into the HOST locker
            var t = new ItemTxnRequest { Op = ItemOp.Convert, Region = region };
            _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ItemTxn, Value = t.Encode() });
            _log.Info("WITEM original dropped-item conversion in " + Regions.Name(region) + ": requested on the host locker");
            return false;
        }

        // Host: ItemID.MoveToStorageOnDrop (original: locker + 1 unless max_amount > 0) for every registry item of the region;
        // converted items leave the registry; a Player-2 item gets a host journal entry (durable, replayed after a reload).
        int HostConvert(int region, string why)
        {
            int n = 0;
            foreach (var it in _world.Host.Items.InRegion(region))
            {
                var item = ItemOf(it.ItemId);
                if (item == null) continue;
                int before = _itemLocker(item);
                _moveToLockerOnDrop(item);
                if (_itemLocker(item) == before) continue;               // max_amount > 0: stays on the ground (original)
                _world.Host.Items.Remove(it.Id, _world.Host.Reg, _world.Host.Outbox);
                Unbind(it.Id, true);
                if (it.Origin == 2) { var p2 = Player2Sync.Instance; if (p2 != null) p2.HostJournal(JournalKind.HostLocker, it.ItemId, item, "Player-2 item " + it.ItemId + " converted into the hideout locker (" + why + ")"); }
                n++; _converted++;
            }
            return n;
        }

        // ======================================================================== host hook (WorldHost.ItemTransaction)
        ProposalCode HostHook(byte slot, ItemTxnRequest t)
        {
            var p2 = Player2Sync.Instance;
            switch (t.Op)
            {
                case ItemOp.Drop:
                    if (slot == 1) return ProposalCode.Accepted;           // the host's original drop already happened
                    if (_world.GameManagerObject == null) return ProposalCode.Busy;   // host scene loading: client retries later
                    if (!Shareable(ItemOf(t.ItemId))) return ProposalCode.Invalid;
                    return p2 != null ? p2.HostP2ItemTxn(ItemOp.Drop, t.Txn, t.ItemId, 2, 0) : ProposalCode.NotAuthorized;
                case ItemOp.Pickup:
                {
                    WorldItem it; int r;
                    if (!_world.Host.Items.TryGet(t.WorldId, out it, out r)) return ProposalCode.Stale;
                    if (slot == 1) return ProposalCode.Accepted;
                    return p2 != null ? p2.HostP2ItemTxn(ItemOp.Pickup, t.Txn, it.ItemId, it.Origin, it.OriginTxn) : ProposalCode.NotAuthorized;
                }
                case ItemOp.Convert:
                    if (slot == 2) { int n = HostConvert(t.Region, "Player 2 saved in " + Regions.Name(t.Region)); _log.Info("WITEM conversion requested by Player 2 in " + Regions.Name(t.Region) + ": " + n + " item(s) moved into the host locker"); }
                    return ProposalCode.Accepted;
                default: return ProposalCode.Invalid;
            }
        }

        // ======================================================================== presentation (both roles)
        public void Update()
        {
            if (!_ok) return;
            double now = Now;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= _nextStats) { _nextStats = now + 30; if (_drops + _pickups + _spawned + _refused > 0) _log.Info(Stats); }
            if (_txn != null && !_txn.Lost && (!Live || now - _txn.At > 15.0)) { _txn.Lost = true; _lost++; _log.Warn("WITEM " + _txn.Op + " #" + _txn.Txn_ + " unanswered: resolved from the host record"); }
            if (_awaitItem != null && (_itemAmount(_awaitItem) >= _awaitAmount)) FinishPickup("original pickup finished");
            else if (_awaitItem != null && now > _awaitUntil) FinishPickup("original pickup did not finish in time");
            if (_host && _p1ExpectItem != int.MinValue && now > _p1ExpectUntil) { _log.Warn("WITEM Player-1 pickup of a Player-2 item not counted by the original in time: not journaled"); _p1ExpectItem = int.MinValue; }

            var gm = _world.GameManagerObject;
            bool loaded = false; if (gm != null) try { loaded = _gmLoaded(gm); } catch { }
            if (!ReferenceEquals(gm, _sceneGm)) { _sceneGm = gm; _itemById.Clear(); _itemsGm = null; DropAll(false); _forceReconcile = true; }   // new scene: objects destroyed with it
            int region = loaded ? _world.LocalRegion : Regions.None;
            if (!Live && !_host) { if (_bound.Count > 0) DropAll(true); _shownRegion = Regions.None; return; }
            // the registry is only looked at when something changed in it (head revision), the region or the scene changed
            var reg = _world.Registry;
            if (region != _shownRegion || reg.HeadRev != _seenHead || reg.Epoch != _seenEpoch || _forceReconcile)
            {
                _seenHead = reg.HeadRev; _seenEpoch = reg.Epoch;
                var e = region != Regions.None ? reg.Get(StateScope.Region, region, WorldKeys.WorldItems) : null;
                uint rev = e != null ? e.Rev : 0;
                if (region == _shownRegion && rev == _shownRev && !_forceReconcile) goto settle;
                if (region != _shownRegion) DropAll(true);
                _shownRegion = region; _shownRev = rev; _forceReconcile = false;
                List<WorldItem> l = null;
                if (e != null && !WorldItemList.TryParse(e.Value, out l)) _log.Warn("WITEM registry list of " + Regions.Name(region) + " unreadable");
                Reconcile(l ?? new List<WorldItem>());
            }
        settle:
            SettleFrame(now, region);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; if (ms > _costMaxMs) _costMaxMs = ms;
        }
        uint _seenHead = uint.MaxValue, _seenEpoch;

        void Reconcile(List<WorldItem> want)
        {
            var keep = new HashSet<uint>();
            foreach (var it in want)
            {
                keep.Add(it.Id);
                Bound b;
                if (_bound.TryGetValue(it.Id, out b))
                {
                    if (b.Go == null) { Unbind(it.Id, false); b = null; }
                    else
                    {
                        if (!b.Own && !b.Claiming) Present(b, it, false);
                        continue;
                    }
                }
                if (_txn != null && _txn.Op == ItemOp.Drop && it.Origin == _me && it.OriginTxn == _txn.Txn_) continue;   // our own drop: bound when the original runs
                if (_bindAfterDrop == it.Id) continue;
                Spawn(it);
            }
            var drop = new List<uint>();
            foreach (var kv in _bound) if (!keep.Contains(kv.Key) && !kv.Value.Claiming) drop.Add(kv.Key);
            foreach (var id in drop) Unbind(id, true);
        }

        // W3.2 observer presentation of one registry item (event-driven: only when its registry state changed).
        //  Settled -> exactly the rest pose the dropper's original physics produced.
        //  Falling -> the dropper's object is still at Inventory.dropPos (original pause menu open, no physics yet): shown on
        //             the surface straight below that point, never as a solid object hanging in the air; corrected to the exact
        //             rest pose when it arrives. The copy never simulates (kinematic; the original Pickupable.Update then
        //             removes that Rigidbody itself, exactly as it does on every resting item) -> no competing physics.
        void Present(Bound b, WorldItem it, bool spawn)
        {
            var p = new Vector3(it.Pos.X, it.Pos.Y, it.Pos.Z); var q = new Quaternion(it.Rot.Fx, it.Rot.Fy, it.Rot.Fz, it.Rot.Fw);
            if (!spawn && it.Settled == b.Settled && (p - b.SrcPos).sqrMagnitude < 1e-6f && Quaternion.Angle(q, b.SrcRot) < 0.5f) return;
            var t = b.Go.transform; var shownBefore = t.position; bool wasSettled = b.Settled;
            b.SrcPos = p; b.SrcRot = q; b.Settled = it.Settled;
            t.SetPositionAndRotation(p, q);
            string surface = null; float dy = 0f;
            if (SettleRules.ProjectToSurface(it.Settled)) { surface = ProjectDown(b.Go, out dy); if (surface != null) _projected++; else _projectMiss++; }
            else if (!spawn) _settleApplied++;
            if (b.Diag)
                _log.Info("WITEM_DIAG #" + b.Id + (spawn ? " shown" : " updated") + " (" + (it.Settled ? "settled" : "falling") + ", simulated by P" + it.Sim + ") reported " + V3(p)
                    + (it.Settled ? (spawn ? "" : " -> exact rest pose, moved " + Vector3.Distance(shownBefore, t.position).ToString("0.00") + " m" + (wasSettled ? "" : " from the surface estimate"))
                                  : surface != null ? " -> on '" + surface + "' " + dy.ToString("0.00") + " m" : " -> no surface below (reported pose kept)")
                    + " at " + V3(t.position) + " rb=" + RbText(b.Rb) + " colliders=" + ColText(b.Go) + " timeScale " + Time.timeScale.ToString("0.##"));
        }

        // One downward cast against the layers the item's own layer collides with (the original physics matrix), skipping the
        // item itself, triggers, players / avatars and moving bodies. Returns the surface name or null (pose unchanged).
        static readonly RaycastHit[] _hits = new RaycastHit[16];
        readonly int[] _maskByLayer = new int[32]; readonly bool[] _maskKnown = new bool[32];
        Type _psType;
        string ProjectDown(GameObject go, out float dy)
        {
            dy = 0f;
            Physics.SyncTransforms();                                  // collider bounds of the object just placed (event, not per frame)
            Bounds bb; if (!ColliderBounds(go, out bb)) return null;
            int layer = go.layer & 31;
            if (!_maskKnown[layer]) { int m = 0; for (int l = 0; l < 32; l++) if (!Physics.GetIgnoreLayerCollision(layer, l)) m |= 1 << l; _maskByLayer[layer] = m; _maskKnown[layer] = true; }
            if (_psType == null) _psType = AccessTools.TypeByName("PlayerScript");
            int n = Physics.RaycastNonAlloc(bb.center, Vector3.down, _hits, SettleRules.MaxDrop + bb.extents.y, _maskByLayer[layer], QueryTriggerInteraction.Ignore);
            int best = -1; float bestD = float.MaxValue; var self = go.transform;
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i].collider;
                if (c == null || _hits[i].distance >= bestD || c.transform.IsChildOf(self) || c is CharacterController) continue;
                var arb = c.attachedRigidbody; if (arb != null && !arb.isKinematic) continue;
                var root = c.transform.root;
                if (root.name.StartsWith("TS2MP_", StringComparison.Ordinal) || (_psType != null && c.GetComponentInParent(_psType) != null)) continue;
                best = i; bestD = _hits[i].distance;
            }
            if (best < 0) return null;
            if (!SettleRules.SurfaceOffset(bb.min.y, true, _hits[best].point.y, out dy)) return null;
            go.transform.position += new Vector3(0f, dy, 0f);
            return _hits[best].collider.name;
        }

        static bool ColliderBounds(GameObject go, out Bounds b)
        {
            bool any = false; b = new Bounds();
            foreach (var c in go.GetComponentsInChildren<Collider>(false))
            {
                if (!c.enabled || c.isTrigger) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (any) return true;
            foreach (var r in go.GetComponentsInChildren<Renderer>(false)) { if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            return any;
        }

        // The original drop_object prefab, configured exactly like Inventory.DropItemNewSystem does, kinematic (no second simulation).
        void Spawn(WorldItem it)
        {
            var item = ItemOf(it.ItemId);
            var prefab = item != null ? _itemDrop(item) as GameObject : null;
            if (prefab == null) { _log.Warn("WITEM item " + it.ItemId + " (#" + it.Id + ") unknown on this instance: not shown"); return; }
            var go = UnityEngine.Object.Instantiate(prefab, new Vector3(it.Pos.X, it.Pos.Y, it.Pos.Z), new Quaternion(it.Rot.Fx, it.Rot.Fy, it.Rot.Fz, it.Rot.Fw));
            var p = go.GetComponent(_pickT);
            if (p == null) { UnityEngine.Object.Destroy(go); _log.Warn("WITEM drop_object of item " + it.ItemId + " has no Pickupable: not shown"); return; }
            _setPickId(p, it.ItemId); _setPickDropped(p, true);
            if (_itemX10(item) > 0) _setPickX10(p, true); else { _setPickXp(p, false); go.name = "dropped"; }
            _setPickDestroy(p, false);
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.useGravity = false; }
            go.SetActive(true);
            Bind(it.Id, it.ItemId, p, false);
            _spawned++;
            Bound b;
            if (_bound.TryGetValue(it.Id, out b)) { b.Diag = _diag.Want(it.Id); Present(b, it, true); }
        }

        void Bind(uint id, int itemId, Component pick, bool own)
        {
            if (id == 0 || pick == null) return;
            Unbind(id, false);
            var go = pick.gameObject;
            double now = Now;
            var b = new Bound { Id = id, ItemId = itemId, Go = go, Pick = pick, Rb = go.GetComponent<Rigidbody>(), Own = own, LastPos = go.transform.position, LastRot = go.transform.rotation,
                                DropPos = go.transform.position, NextCheck = now + 0.2, LastTick = now, BornAt = now };
            _bound[id] = b; _byObject[go.GetInstanceID()] = id;
        }

        void Unbind(uint id, bool destroy)
        {
            Bound b;
            if (!_bound.TryGetValue(id, out b)) return;
            _bound.Remove(id);
            if (b.Go != null) { _byObject.Remove(b.Go.GetInstanceID()); if (destroy && !(_pickPicking != null && b.Pick != null && _pickPicking(b.Pick))) { UnityEngine.Object.Destroy(b.Go); _removed++; } }
            else { var stale = new List<int>(); foreach (var kv in _byObject) if (kv.Value == id) stale.Add(kv.Key); foreach (var k in stale) _byObject.Remove(k); }
        }

        void DropAll(bool destroy)
        {
            if (_bound.Count == 0) return;
            foreach (var id in new List<uint>(_bound.Keys)) Unbind(id, destroy);
            _byObject.Clear();
        }

        // The simulating (dropper's) instance reports the rest pose of its own ORIGINAL object (W3.2, SettleRules):
        //  - never while its original pause menu is open (timeScale 0: the object has not fallen, velocity 0 means nothing);
        //  - first report when the original considers it at rest (Pickupable.Update destroyed the Rigidbody after IsSleeping) or
        //    it is asleep / slow after >= 0.2 s of running physics; that report marks the registry item Settled;
        //  - later reports only after a real movement. Own unreported items are checked 5x/s, reported ones 1x/s.
        // Nothing is streamed while falling: observers show the surface estimate meanwhile.
        void SettleFrame(double now, int region)
        {
            if (region == Regions.None || _bound.Count == 0) return;
            bool paused = Time.timeScale <= 0f;
            foreach (var b in _bound.Values)
            {
                if (!b.Own || b.Claiming || b.Go == null) continue;
                if (!paused) b.UnpausedFor += now - b.LastTick;
                b.LastTick = now;
                if (now < b.NextCheck) continue;
                b.NextCheck = now + (b.Reported ? 1.0 : 0.2);
                var t = b.Go.transform;
                bool gone = b.Rb == null;                              // Unity null after the original Destroy(thisRigid)
                string why;
                if (!SettleRules.OwnerShouldReport(b.Reported, paused, b.UnpausedFor, gone, !gone && b.Rb.IsSleeping(), !gone && b.Rb.isKinematic,
                                                   gone ? 0f : b.Rb.velocity.sqrMagnitude, (t.position - b.LastPos).sqrMagnitude, Quaternion.Angle(t.rotation, b.LastRot), out why)) continue;
                bool first = !b.Reported;
                b.LastPos = t.position; b.LastRot = t.rotation; b.Reported = true;
                var q = t.rotation;
                var req = new ItemTxnRequest { Op = ItemOp.Settle, WorldId = b.Id, Region = region, Pos = new Vec3(t.position.x, t.position.y, t.position.z), Rot = Quat16.From(q.x, q.y, q.z, q.w) };
                string res = "sent";
                if (_host) { string w2; var code = _world.Host.ItemTransaction(_me, req, _world.Clock, out w2); _world.FlushOutbox(); res = code.ToString() + (w2 != null ? " (" + w2 + ")" : ""); }
                else if (Live) _world.SendProposal(new StateEntry { Scope = StateScope.Session, ScopeArg = 0, Key = WorldKeys.ItemTxn, Value = req.Encode() });
                else res = "not sent (offline)";
                _settles++;
                if (b.Diag)
                    _log.Info("WITEM_DIAG #" + b.Id + (first ? " settled" : " moved") + " at " + V3(t.position) + " (" + why + ", fell " + (b.DropPos.y - t.position.y).ToString("0.00") + " m, "
                        + (now - b.BornAt).ToString("0.0") + " s after the drop, " + b.UnpausedFor.ToString("0.0") + " s unpaused) rb=" + RbText(b.Rb) + " colliders=" + ColText(b.Go) + " -> settle " + res);
            }
        }

        public string Stats
        {
            get
            {
                var wh = _world.Host;
                return "WITEM_STATS sharedDrops=" + (_dropsOn ? "on" : "OFF") + " bound=" + _bound.Count + " refusedDrops=" + _refusedDrops + " region=" + Regions.Name(_shownRegion) + " drops=" + _drops + " pickups=" + _pickups + " refused=" + _refused + " spawned=" + _spawned + " removed=" + _removed
                    + " settles=" + _settles + " settleApplied=" + _settleApplied + " surfaceShown=" + _projected + " surfaceMiss=" + _projectMiss + " converted=" + _converted + " notShared=" + _notShared + " lost=" + _lost + " frameMax=" + _costMaxMs.ToString("0.00") + "ms"
                    + (_host && wh != null ? " registry total=" + wh.Items.Total + " drops/pickups/settles/converted/cleared/refused=" + wh.Items.Drops + "/" + wh.Items.Pickups + "/" + wh.Items.Settles + "/" + wh.Items.Converted + "/" + wh.Items.Cleared + "/" + wh.Items.Refused : "");
            }
        }

        public void Dispose()
        {
            if (ReferenceEquals(Instance, this)) Instance = null;
            if (_host && _world.Host != null) _world.Host.ItemTxnHook = null;
            if (_world.ProposalResultHook4 == (Func<uint, ProposalCode, bool>)OnResult) _world.ProposalResultHook4 = null;
            if (!_host) DropAll(true);
            if (_h != null) { _h.UnpatchSelf(); _h = null; }
        }
    }
}
