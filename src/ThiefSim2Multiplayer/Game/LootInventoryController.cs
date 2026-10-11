using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class LootInventoryController : IDisposable
    {
        private static LootInventoryController current;
        internal static bool NativePickupPending => current != null && current.localPending.Values.Any(t => t.Request.Action == InventoryAction.Pickup);
        internal static bool StoreNativeDrop(Component pickup,Func<bool> move)
        {
            var c=current; LootNetworkAdapter adapter;
            if(c==null || !c.adapters.TryGetValue(pickup,out adapter)) return false;
            var entity=c.FindEntity(adapter); if(!entity.HasValue) return false;
            if(!c.network.Inventories.Authority.StoreNativeDrop(entity.Value,move)) c.log("[TS2MP] Native cargo transfer deferred for reserved/unavailable cooperative item "+entity.Value);
            return true; // A bound canonical item must never fall through to a duplicate native grant.
        }
        private readonly NetworkManager network;
        private readonly Action<string> log;
        private readonly PlayerInventoryAdapter native = new PlayerInventoryAdapter();
        private readonly Type pickupType = Type.GetType("Pickupable, Assembly-CSharp", true);
        private readonly Harmony harmony = new Harmony(Plugin.Id + ".loot-inventory");
        private readonly Dictionary<Component, LootNetworkAdapter> adapters = new Dictionary<Component, LootNetworkAdapter>();
        private readonly Dictionary<ulong, NativeTransaction> localPending = new Dictionary<ulong, NativeTransaction>();
        private readonly List<EntityRef> registeredStatic = new List<EntityRef>();
        private readonly Dictionary<LootNetworkAdapter, EntityRef> bindings = new Dictionary<LootNetworkAdapter, EntityRef>();
        private readonly ulong originSession = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0) | 1UL;
        private uint epoch;
        private bool hostRole;
        private bool manifestPrepared;
        private double nextObservation;
        private InventoryState deferred;
        private InventoryState initialSeed;
        private InventoryState lastAuthoritative;
        private int reconciliationFailures;
        private double nextReconcile;
        private ulong equipmentRequest;
        private ulong requiredInventoryRevision, requiredWorldRevision;
        private bool executing;
        internal int LootCount { get; private set; }
        internal int NativePendingCount => localPending.Count;
        internal int Excluded { get; private set; }
        private bool Active => network.Session.State != SessionState.Offline;
        private bool Client => Active && !network.Session.IsHost;
        private bool Cooperative => Active && (!network.Session.IsHost || network.PeerConnected);
        private sealed class NativeTransaction
        {
            internal InventoryRequest Request;
            internal ulong Transaction;
            internal Component Pickup;
            internal InventoryItemState Item;
            internal int Before;
            internal int MoneyBefore, Cash;
            internal float WeightBefore;
            internal bool Removed, Started, CompletionSent;
            internal double Expires;
            internal int Layer;
            internal Collider[] Colliders;
            internal bool[] ColliderEnabled;
            internal bool HadRigid, Gravity, Kinematic;
            internal HashSet<Component> BeforeDrop;
        }
        internal LootInventoryController(NetworkManager manager, Action<string> logger)
        {
            network = manager; log = logger; current = this;
            network.World.PrepareRegistry += Prepare;
            network.World.AdapterFactory = Factory;
            network.Inventories.ValidateTarget = ValidateRemote;
            network.Inventories.CreateDrop = CreateRemoteDrop;
            network.Inventories.CreateRemoteSeed = owner => initialSeed == null ? null : new InventoryState(owner, 1, initialSeed.Capacity, initialSeed.Items.Where(i => i.Definition == 0 || i.Definition == 3).GroupBy(i => i.Definition).Select(g => new InventoryItemState(new NetworkItemInstanceId(Guid.NewGuid()), g.Key)));
            network.Inventories.ResultReceived += Result;
            network.Inventories.StateReceived += ReceivedState;
            network.RemoteRemoved += Disconnected;
            Patch(pickupType, "PickUp", nameof(PickupIntent));
            Patch(Type.GetType("Inventory, Assembly-CSharp", true), "DropItemNewSystem", nameof(DropIntent), nameof(DropReturned));
            Patch(Type.GetType("PlayerScript, Assembly-CSharp", true), "SelectCrowbar", nameof(EquipIntent));
            var iterator = pickupType.GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<PickingItem>", StringComparison.Ordinal));
            harmony.Patch(AccessTools.Method(iterator, "MoveNext"), transpiler: new HarmonyMethod(typeof(LootInventoryController), nameof(DeferDestruction)));
        }
        private void Patch(Type type, string name, string prefix, string postfix = null)
        { harmony.Patch(AccessTools.Method(type, name), new HarmonyMethod(typeof(LootInventoryController), prefix), postfix == null ? null : new HarmonyMethod(typeof(LootInventoryController), postfix)); }
        internal void Prepare()
        {
            using var phase=network.FrameProfile.Measure(Core.Diagnostics.RuntimePhase.InventoryRebinding);
            if (network.Scenes.Active && !network.Scenes.LocalLoaded) return;
            if (!Active || network.World.TestEntitiesEnabled || !native.Refresh()) return;
            var registry = network.World.Registry;
            // An empty supported manifest is a successful initialization too.
            if (manifestPrepared && epoch == registry.SceneEpoch && hostRole == registry.IsHost && registeredStatic.All(e => registry.Lookup(e) != null)) return;
            adapters.Clear(); registeredStatic.Clear(); bindings.Clear(); LootCount = Excluded = 0; epoch = registry.SceneEpoch; hostRole = registry.IsHost;
            network.Inventories.Authority.Configure(native.Rules);
            if (registry.IsHost && network.Inventories.Authority.Get(network.Inventories.LocalIdentity) == null)
            { initialSeed = native.CaptureSeed(network.Inventories.LocalIdentity); network.Inventories.Authority.Initialize(initialSeed); }
            else if (registry.IsHost) deferred = network.Inventories.Authority.Get(network.Inventories.LocalIdentity);
            var candidates = new List<KeyValuePair<StaticEntityDescriptor, Component>>();
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s); if (!scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
                foreach (var root in scene.GetRootGameObjects()) foreach (Component pickup in root.GetComponentsInChildren(pickupType, true))
                {
                    if (!Supported(pickup) || GameAccess.Bool(pickup, "wasDropped")) { Excluded++; continue; }
                    candidates.Add(new KeyValuePair<StaticEntityDescriptor, Component>(UnityNetworkEntityAdapter.Describe(pickup, network.BuildFingerprintForDiagnostics, "pickup-v1", Array.IndexOf(pickup.gameObject.GetComponents(pickupType), pickup)), pickup));
                }
            }
            foreach (var pair in candidates.OrderBy(p => p.Key.Canonical, StringComparer.Ordinal))
            {
                var entity = new EntityRef(pair.Key.DeriveId(), 1);
                var item = new InventoryItemState(NetworkItemInstanceId.FromEntity(originSession, epoch, entity), (ushort)(int)GameAccess.Get(pair.Value, "item_ID"));
                var adapter = Bind(pair.Value, item, false);
                registry.RegisterStatic(pair.Key, adapter, new EntityOwnership(OwnershipKind.System)); registeredStatic.Add(entity); bindings[adapter] = entity; LootCount++;
                if (LootCount <= 16) log("[TS2MP] Loot binding entity=" + entity + " definition=" + item.Definition + " instance=" + item.Instance + " path=" + pair.Key.DiagnosticPath + " state=" + Convert.ToBase64String(adapter.CaptureState()));
            }
            log("[TS2MP] Loot manifest static=" + LootCount + " excluded=" + Excluded + " collisions=" + registry.CollisionCount);
            manifestPrepared=true;
        }
        private bool Supported(Component pickup)
        {
            if (!pickup || !((Behaviour)pickup).enabled || pickup.gameObject.layer == 23) return false;
            // Native runtime spawners produce different root clones on separate loads.
            // Until their spawn origin is verified, they have no authored static identity.
            if (pickup.transform.parent == null && pickup.name.EndsWith("(Clone)", StringComparison.Ordinal)) return false;
            int id = (int)GameAccess.Get(pickup, "item_ID"); if (id < 0 || id >= InventoryLimits.MaxDefinitions) return false;
            bool money = GameAccess.Bool(pickup,"is_money");
            if (!native.Definitions.ContainsKey((ushort)id) && !money) return false;
            // Inactive authored cash initializes itemScript only after native OnEnable/Start.
            // Its serialized identity/value must already bind for later house activation.
            if (money && (int)GameAccess.Get(pickup,"add_money") <= 0) return false;
            // Scene-resident drop prototypes and inventory previews are assets, not loose loot.
            if (!money && GameAccess.Get(native.Definitions[(ushort)id], "drop_object") as GameObject == pickup.gameObject) return false;
            foreach (string flag in new[] { "is_heavy_item", "isCarried", "advance_story_if_picked", "advance_story_if_placed_car", "lock_parcel_after_pickup", "isContractItem", "never_destroy" }) if (GameAccess.Bool(pickup, flag)) return false;
            foreach (var node in pickup.GetComponentsInParent<Transform>(true)) foreach (Component component in node.GetComponents<Component>())
                if (node.name == "DROPABLE ITEMS" || (component && new[] { "Canvas", "LootBox", "ParcelContainer", "VanHeavyItems", "Mission_Item", "CarController" }.Contains(component.GetType().Name))) return false;
            return true;
        }
        private LootNetworkAdapter Bind(Component pickup, InventoryItemState item, bool dynamic)
        {
            var p = pickup.transform.position; var q = pickup.transform.rotation;
            var adapter = new LootNetworkAdapter(pickup, new LootState { Item = item, MoneyValue = GameAccess.Bool(pickup,"is_money") ? (int)GameAccess.Get(pickup,"add_money") : 0, Available = pickup.gameObject.activeInHierarchy, X = p.x, Y = p.y, Z = p.z, Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w }, dynamic, network.Session.IsHost);
            adapters[pickup] = adapter; return adapter;
        }
        private EntityRef? FindEntity(LootNetworkAdapter adapter)
        {
            EntityRef cached;
            if (bindings.TryGetValue(adapter, out cached) && ReferenceEquals(network.World.Registry.GetBinding(cached), adapter)) return cached;
            bindings.Remove(adapter); var found = network.World.Registry.FindBinding(adapter);
            if (found.HasValue) bindings[adapter] = found.Value; return found;
        }
        private INetworkEntityAdapter Factory(EntityTypeId type)
        {
            if (type != EntityTypeId.Pickup) return null;
            LootNetworkAdapter adapter = null;
            adapter = new LootNetworkAdapter(null, null, true, false, state => { var pickup = Spawn(state); adapters[pickup] = adapter; return pickup; });
            return adapter;
        }
        private InventoryResultCode ValidateRemote(InventoryRequest request)
        {
            if (!native.Ready || !network.Telemetry.HasPose || network.Telemetry.ReceiveAge(network.Now) > 1) return InventoryResultCode.InvalidState;
            if(request.Action>=InventoryAction.Buy) return InventoryResultCode.Reserved;
            var pose = network.Telemetry.Latest; var root = new Vector3(pose.X, pose.Y, pose.Z);
            if (request.Action == InventoryAction.Drop) return (new Vector3(request.X, request.Y, request.Z) - root).sqrMagnitude <= 6.25f ? InventoryResultCode.Reserved : InventoryResultCode.OutOfRange;
            if (request.Action != InventoryAction.Pickup) return InventoryResultCode.Reserved;
            var adapter = network.World.Registry.GetBinding(request.Entity) as LootNetworkAdapter;
            Component pickup = adapter?.Pickup;
            if (!pickup || !Supported(pickup) || !pickup.gameObject.activeInHierarchy || GameAccess.Bool(pickup, "cant_pick_reason") || GameAccess.Bool(pickup, "picking")) return InventoryResultCode.EntityUnavailable;
            var controller = native.Player.GetComponent<CharacterController>(); var motor = native.Player.GetComponent(Type.GetType("PlayerMotorBehavior, Assembly-CSharp", true));
            float height = (float)GameAccess.Get(motor, pose.State.Crouched ? "crouchHeight" : "defaultCrouchHeight2");
            var eye = root + Vector3.up * (controller.center.y + height * .5f - .05f);
            return Reachable(pickup, eye, 2.75f) ? InventoryResultCode.Reserved : InventoryResultCode.OutOfRange;
        }
        private bool Reachable(Component pickup, Vector3 eye, float distance)
        {
            int mask = ((LayerMask)GameAccess.Get(native.Player, "mask")).value;
            foreach (var collider in pickup.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                foreach (var point in new[] { collider.bounds.ClosestPoint(eye), collider.bounds.center })
                {
                    var direction = point - eye; if (direction.sqrMagnitude < .0001f) continue;
                    RaycastHit target, first; var ray = new Ray(eye, direction.normalized);
                    if (collider.Raycast(ray, out target, distance) && Physics.Raycast(ray, out first, target.distance + .05f, mask) && first.collider.GetComponentInParent(pickupType) == pickup) return true;
                }
            }
            return false;
        }
        private bool StartPickup(Component pickup)
        {
            if (!network.TransactionsAllowed) return false;
            Prepare(); if (!Supported(pickup) || !pickup.gameObject.activeInHierarchy || GameAccess.Bool(pickup, "picking") || GameAccess.Bool(pickup, "cant_pick_reason") || localPending.Count >= InventoryLimits.MaxPending) return false;
            LootNetworkAdapter adapter; if (!adapters.TryGetValue(pickup, out adapter)) return false;
            var entity = FindEntity(adapter); if (!entity.HasValue || localPending.Values.Any(t => t.Request.Action == InventoryAction.Pickup && t.Request.Entity.Equals(entity.Value))) return false;
            if (adapter.State.MoneyValue == 0 ? !native.Eligible(adapter.State.Item.Definition) : !SharedVehicleController.Eligible(native.Player) || GameAccess.Bool(native.Player,"is_in_vehicle")) return false;
            var camera = GameAccess.Get(native.Player, "cam") as Transform;
            if (!camera || !Reachable(pickup, camera.position, 2.25f)) return false;
            var request = network.Inventories.Request(InventoryAction.Pickup, entity.Value); if (request == null) return false;
            var tx = Track(request, adapter.State.Item, pickup);
            if (network.Session.IsHost)
            {
                var result = network.Inventories.HostBegin(request, _ => InventoryResultCode.Reserved);
                if (result.Code != InventoryResultCode.Reserved) { localPending.Remove(request.RequestId); return false; }
                tx.Transaction = result.TransactionId; tx.Started = true; return true;
            }
            if (!network.Inventories.SendRequest(request)) localPending.Remove(request.RequestId);
            return false;
        }
        private NativeTransaction Track(InventoryRequest request, InventoryItemState item, Component pickup)
        {
            var t = new NativeTransaction { Request = request, Pickup = pickup, Item = item, Before = native.Definitions.ContainsKey(item.Definition) ? native.Amount(item.Definition) : 0, MoneyBefore = (int)GameAccess.Get(native.Player,"moneyAm"), Cash = pickup && GameAccess.Bool(pickup,"is_money") ? (int)GameAccess.Get(pickup,"add_money") : 0, WeightBefore = native.Weight, Expires = network.Now + InventoryLimits.ReservationSeconds + 1 };
            if (pickup)
            {
                t.Layer = pickup.gameObject.layer; t.Colliders = pickup.GetComponentsInChildren<Collider>(); t.ColliderEnabled = t.Colliders.Select(c => c.enabled).ToArray();
                var rigid = pickup.GetComponent<Rigidbody>(); t.HadRigid = rigid; if (rigid) { t.Gravity = rigid.useGravity; t.Kinematic = rigid.isKinematic; }
            }
            else t.BeforeDrop = new HashSet<Component>(UnityEngine.Object.FindObjectsOfType(pickupType).Cast<Component>());
            localPending.Add(request.RequestId, t); return t;
        }
        private bool StartDrop()
        {
            if (!network.TransactionsAllowed) return false;
            Prepare(); var selected = native.SelectedDefinition; var inventory = network.Inventories.LocalState;
            if (!selected.HasValue || !native.Eligible(selected.Value, true) || inventory == null || localPending.Count != 0) return false;
            var item = inventory.Items.FirstOrDefault(i => i.Definition == selected.Value); if (item == null) return false;
            var position = native.DropPosition;
            if ((position - native.Player.transform.position).sqrMagnitude > 6.25f) return false;
            var request = network.Inventories.Request(InventoryAction.Drop, instance: item.Instance.Value, x: position.x, y: position.y, z: position.z); if (request == null) return false;
            var t = Track(request, item, null);
            if (network.Session.IsHost)
            {
                var result = network.Inventories.HostBegin(request, _ => InventoryResultCode.Reserved);
                if (result.Code != InventoryResultCode.Reserved) { localPending.Remove(request.RequestId); return false; }
                t.Transaction = result.TransactionId; t.Started = true; return true;
            }
            if (!network.Inventories.SendRequest(request)) localPending.Remove(request.RequestId); return false;
        }
        private void FinishNativeDrop()
        {
            var t = localPending.Values.SingleOrDefault(p => p.Started && p.Request.Action == InventoryAction.Drop && !p.CompletionSent); if (t == null) return;
            var candidates = UnityEngine.Object.FindObjectsOfType(pickupType).Cast<Component>().Where(p => !t.BeforeDrop.Contains(p) && GameAccess.Bool(p, "wasDropped") && (int)GameAccess.Get(p, "item_ID") == t.Item.Definition && (p.transform.position - native.DropPosition).sqrMagnitude < 1).ToArray();
            bool success = candidates.Length == 1 && native.Amount(t.Item.Definition) == t.Before - 1;
            t.Pickup = candidates.Length == 1 ? candidates[0] : null; t.CompletionSent = true;
            if (Client && t.Pickup) t.Pickup.gameObject.SetActive(false);
            network.Inventories.Complete(t.Request, t.Transaction, success, _ => success ? Bind(t.Pickup, t.Item, true) : null);
        }
        private void Result(InventoryTransactionResult result)
        {
            if (result.RequestId == equipmentRequest && result.Code != InventoryResultCode.Reserved) equipmentRequest = 0;
            NativeTransaction t; if (!localPending.TryGetValue(result.RequestId, out t)) return;
            if (result.Code == InventoryResultCode.Reserved)
            {
                if (t.Started) return;
                t.Transaction = result.TransactionId;
                bool valid = network.Now < t.Expires && native.Ready && (t.Cash > 0 ? SharedVehicleController.Eligible(native.Player) && !GameAccess.Bool(native.Player,"is_in_vehicle") : native.Eligible(t.Item.Definition, t.Request.Action == InventoryAction.Drop));
                if (t.Request.Action == InventoryAction.Pickup) valid &= t.Pickup && !GameAccess.Bool(t.Pickup, "picking");
                else valid &= native.SelectedDefinition == t.Item.Definition;
                if (!valid) { t.CompletionSent = true; network.Inventories.Complete(t.Request, t.Transaction, false); return; }
                t.Started = true; executing = true;
                try
                {
                    if (t.Request.Action == InventoryAction.Pickup) GameAccess.Call(t.Pickup, "PickUp");
                    else { GameAccess.Call(native.Inventory, "DropItemNewSystem"); FinishNativeDrop(); }
                }
                catch (Exception error) { log("[TS2MP] Native inventory execution failed: " + error.GetType().Name); network.Inventories.Complete(t.Request, t.Transaction, false); }
                finally { executing = false; }
                return;
            }
            if (result.Code == InventoryResultCode.Success)
            {
                requiredInventoryRevision = Math.Max(requiredInventoryRevision, result.InventoryRevision);
                requiredWorldRevision = Math.Max(requiredWorldRevision, result.WorldRevision);
                if (t.Request.Action == InventoryAction.Drop && Client && t.Pickup) UnityEngine.Object.Destroy(t.Pickup.gameObject);
            }
            else { Restore(t); deferred = network.Inventories.LocalState; }
            localPending.Remove(result.RequestId);
        }
        private void Restore(NativeTransaction t)
        {
            if (t.Cash > 0 && native.Ready && network.Inventories.SharedEconomy != null) GameAccess.Set(native.Player,"moneyAm",network.Inventories.SharedEconomy.Money);
            if (!t.Pickup) return;
            if (t.Request.Action == InventoryAction.Drop) { UnityEngine.Object.Destroy(t.Pickup.gameObject); return; }
            ((MonoBehaviour)t.Pickup).StopAllCoroutines(); GameAccess.Set(t.Pickup, "picking", false); t.Pickup.gameObject.layer = t.Layer; t.Pickup.gameObject.SetActive(true);
            for (int i = 0; i < t.Colliders.Length; i++) if (t.Colliders[i]) t.Colliders[i].enabled = t.ColliderEnabled[i];
            if (t.HadRigid && !t.Pickup.GetComponent<Rigidbody>()) { var rigid = t.Pickup.gameObject.AddComponent<Rigidbody>(); rigid.useGravity = t.Gravity; rigid.isKinematic = t.Kinematic; }
        }
        private void ReceivedState(InventoryState state) { lastAuthoritative = deferred = state; reconciliationFailures = 0; }
        internal void Observe()
        {
            if (network.Scenes.Active && !network.Scenes.LocalLoaded) return;
            if (!Active || !native.Refresh()) return;
            if (network.Session.IsHost) Prepare();
            if(!Cooperative) { deferred=null; ObserveSoloWorld(); return; }
            foreach (var t in localPending.Values.ToArray())
            {
                if (t.Started && !t.CompletionSent && t.Request.Action == InventoryAction.Pickup && t.Removed && (t.Cash > 0 ? (long)(int)GameAccess.Get(native.Player,"moneyAm") >= (long)t.MoneyBefore+t.Cash : native.Amount(t.Item.Definition) >= t.Before + 1 && native.Weight >= t.WeightBefore + (float)GameAccess.Get(native.Definitions[t.Item.Definition], "item_weight") - .001f))
                { t.CompletionSent = true; network.Inventories.Complete(t.Request, t.Transaction, true); }
                else if (network.Now >= t.Expires) { Restore(t); localPending.Remove(t.Request.RequestId); if (t.Transaction != 0) network.Inventories.Complete(t.Request, t.Transaction, false); deferred = network.Inventories.LocalState; }
            }
            if (!network.Inventories.EconomyLocalPending && localPending.Count == 0 && deferred != null && reconciliationFailures < 3 && network.Now >= nextReconcile && (!Client || network.World.Registry.WorldRevision >= network.Inventories.OwnerSnapshotWorldRevision))
            {
                try { native.Apply(deferred, log); deferred = null; reconciliationFailures = 0; }
                catch (Exception error) { reconciliationFailures++; nextReconcile = network.Now + .5; log("[TS2MP] Inventory reconcile failure=" + reconciliationFailures + " revision=" + deferred.Revision + " error=" + error.GetType().Name); }
            }
            if (network.Now < nextObservation) return; nextObservation = network.Now + .1;
            foreach (var pair in adapters.ToArray())
            {
                var adapter = pair.Value; var entity = FindEntity(adapter);
                if (!pair.Key || !entity.HasValue) { adapters.Remove(pair.Key); bindings.Remove(adapter); continue; }
                if (localPending.Values.Any(t => t.Pickup == pair.Key) || network.Inventories.Authority.Reserved(entity.Value)) continue;
                if (network.Session.IsHost && !StableHash.Equal(network.World.Registry.Lookup(entity.Value).State.Payload, adapter.CaptureState())) network.World.Registry.CommitState(entity.Value);
            }
            ObserveEquipmentAndRemoval();
        }
        private void ObserveSoloWorld()
        {
            if(network.Now<nextObservation) return; nextObservation=network.Now+.1;
            foreach(var pair in adapters.ToArray()) {
                EntityRef entity;
                if(!bindings.TryGetValue(pair.Value,out entity)) continue;
                var entry=network.World.Registry.Lookup(entity);
                if(entry==null || entry.State.Tombstone) continue;
                // Native pickup/destruction still runs unchanged while Hosting alone.
                // Retire its old world record before the next joining peer's baseline.
                if(!pair.Key) {network.World.Registry.Despawn(entity); adapters.Remove(pair.Key); bindings.Remove(pair.Value);}
                else if(!StableHash.Equal(entry.State.Payload,pair.Value.CaptureState())) network.World.Registry.CommitState(entity);
            }
        }
        private void ObserveEquipmentAndRemoval()
        {
            var state = network.Inventories.LocalState; if (network.Inventories.EconomyLocalPending || state == null || deferred != null || localPending.Count != 0 || equipmentRequest != 0 || state.Revision < requiredInventoryRevision || network.World.Registry.WorldRevision < requiredWorldRevision) return;
            foreach (var item in state.Items)
                if (native.Amount(item.Definition) < state.Count(item.Definition)) { SendSimple(InventoryAction.NativeRemoval, item.Instance.Value); return; }
            // Reconcile unsolicited positive changes instead of granting canonical ownership.
            if (native.Definitions.Any(p => native.Amount(p.Key) > state.Count(p.Key))) { deferred = state; return; }
            native.ReconcileFlashlightOwnership();
            short definition; if (!EquipmentSlotMap.TryGetItem((int)GameAccess.Get(native.Player, "currentItem"), out definition)) return;
            var equipped = definition < 0 ? null : state.Items.FirstOrDefault(i => i.Definition == definition);
            if (definition >= 0 && equipped == null) { using (new NetworkApplyScope()) GameAccess.Call(native.Player, "HideWeapons"); return; }
            if (state.Equipped.HasValue == (equipped != null) && (!state.Equipped.HasValue || state.Equipped.Value.Equals(equipped.Instance))) return;
            SendSimple(equipped == null ? InventoryAction.Unequip : InventoryAction.Equip, equipped == null ? Guid.Empty : equipped.Instance.Value);
        }
        private void SendSimple(InventoryAction action, Guid instance)
        {
            var request = network.Inventories.Request(action, instance: instance); if (request == null) return;
            equipmentRequest = request.RequestId;
            if (network.Session.IsHost) { network.Inventories.HostBegin(request, _ => InventoryResultCode.Reserved); equipmentRequest = 0; }
            else if (!network.Inventories.SendRequest(request)) equipmentRequest = 0;
        }
        private Component Spawn(LootState state)
        {
            if (!native.Refresh() || !native.Definitions.ContainsKey(state.Item.Definition)) throw new InvalidOperationException("Unsupported dropped definition.");
            var prefab = GameAccess.Get(native.Definitions[state.Item.Definition], "drop_object") as GameObject;
            if (!prefab) throw new InvalidOperationException("Native drop prefab missing.");
            var go = UnityEngine.Object.Instantiate(prefab, new Vector3(state.X, state.Y, state.Z), new Quaternion(state.Qx, state.Qy, state.Qz, state.Qw));
            var pickup = go.GetComponent(pickupType); if (!pickup) { UnityEngine.Object.Destroy(go); throw new InvalidOperationException("Prefab has no Pickupable."); }
            GameAccess.Set(pickup, "item_ID", (int)state.Item.Definition); GameAccess.Set(pickup, "wasDropped", true); GameAccess.Set(pickup, "destroy_if_was_picked", false); GameAccess.Set(pickup, "add_any_XP", false); go.name = "dropped"; go.SetActive(true);
            if (!go.GetComponent<Rigidbody>()) go.AddComponent<Rigidbody>();
            var rigid = go.GetComponent<Rigidbody>(); rigid.isKinematic = Client; rigid.useGravity = !Client;
            var adapter = Bind(pickup, state.Item, true); adapter.State = state;
            return pickup;
        }
        private ILootAdapter CreateRemoteDrop(InventoryTransaction t)
        { var state = new LootState { Item = t.Item, X = t.Request.X, Y = t.Request.Y, Z = t.Request.Z }; var pickup = Spawn(state); return adapters[pickup]; }
        private void Disconnected()
        {
            bool interrupted = localPending.Count != 0;
            foreach (var t in localPending.Values.ToArray()) Restore(t); localPending.Clear(); equipmentRequest = 0;
            requiredInventoryRevision = requiredWorldRevision = 0;
            if (interrupted && lastAuthoritative != null && native.Refresh())
            { try { native.Apply(lastAuthoritative, log); } catch (Exception error) { log("[TS2MP] Disconnect inventory reconcile failed: " + error.GetType().Name); } }
            if (network.Session.IsHost) deferred = network.Inventories.Authority.Get(network.Inventories.LocalIdentity);
        }
        internal void SceneInvalidating() { Disconnected(); adapters.Clear(); registeredStatic.Clear(); bindings.Clear(); native.Reset(); epoch = 0; deferred = null; manifestPrepared=false; }
        internal string Development(string action)
        {
            if (!native.Refresh()) throw new InvalidOperationException("Genuine native inventory unavailable.");
            if (action == "audit")
            {
                var state = network.Inventories.LocalState;
                var remote = network.Inventories.RemoteIdentity.HasValue ? network.Inventories.Authority.Get(network.Inventories.RemoteIdentity.Value) : null;
                log("[TS2MP] LOOT AUDIT: role=" + network.Session.IsHost + " world=" + network.World.Registry.DevelopmentSummary() + " loot=" + LootCount + " excluded=" + Excluded + " pending=" + localPending.Count + " canonicalPending=" + network.Inventories.Authority.PendingCount + " ownerRevision=" + state?.Revision + " ownerCount=" + state?.Items.Length + " ownerHash=" + state?.Hash + " remoteRevision=" + remote?.Revision + " remoteCount=" + remote?.Items.Length + " remoteHash=" + remote?.Hash + " nativeWeight=" + native.Weight + " nativeCrowbar=" + native.Amount(0) + " crowbarLife=" + GameAccess.Get(native.Player, "crowbarLife") + " invariant=" + network.Inventories.Authority.InvariantViolations());
                log("[TS2MP] AUX EQUIPMENT AUDIT: flashlight=" + native.Amount(3) + " torch=" + GameAccess.Bool(native.Player, "torch") + " light=" + ((GameAccess.Get(native.Player, "LightObj") as GameObject)?.activeSelf));
                return "";
            }
            if (action == "catalog") return string.Join("\n", native.Rules.Select(r => r.Id + "\t" + r.Weight.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" + r.MaxAmount + "\t" + r.Droppable));
            if (action == "drop") { if (!native.SelectedDefinition.HasValue || Time.timeScale <= 0) throw new InvalidOperationException("Original UI selection and unpaused game required."); GameAccess.Call(native.Inventory, "DropItemNewSystem"); return ""; }
            Prepare(); var camera = GameAccess.Get(native.Player, "cam") as Transform;
            var target = adapters.Where(p => p.Key && p.Key.gameObject.activeInHierarchy && Reachable(p.Key, camera.position, 2.25f) && FindEntity(p.Value).HasValue).OrderBy(p => (p.Key.transform.position - camera.position).sqrMagnitude).FirstOrDefault();
            if (!target.Key) throw new InvalidOperationException("No supported real loot in original range and line of sight.");
            var entity = FindEntity(target.Value).Value;
            log("[TS2MP] DEVELOPMENT LOOT TARGET: entity=" + entity + " definition=" + target.Value.State.Item.Definition + " instance=" + target.Value.State.Item.Instance + " position=" + target.Key.transform.position.ToString("F4"));
            if (action == "pickup") { if (Time.timeScale <= 0) throw new InvalidOperationException("Unpaused game required."); GameAccess.Call(target.Key, "PickUp"); }
            return ((byte)entity.Id.Scope) + ":" + entity.Id.Value.ToString("X16");
        }
        private static bool PickupIntent(Component __instance) => current == null || !current.Cooperative || NetworkApplyScope.Active || current.executing || current.StartPickup(__instance);
        private static bool DropIntent(out NativeTransaction __state)
        {
            __state=null; var c=current;
            if(c!=null && c.Active && !c.Cooperative && c.native.Refresh()) {
                var id=c.native.SelectedDefinition; var item=id.HasValue ? c.network.Inventories.LocalState?.Items.FirstOrDefault(i=>i.Definition==id.Value) : null;
                if(item!=null) __state=new NativeTransaction {Item=item,Before=c.native.Amount(item.Definition),BeforeDrop=new HashSet<Component>(UnityEngine.Object.FindObjectsOfType(c.pickupType).Cast<Component>())};
            }
            return c==null || !c.Cooperative || NetworkApplyScope.Active || c.executing || c.StartDrop();
        }
        private static void DropReturned(NativeTransaction __state)
        {
            var c=current; if(c==null) return;
            if(c.Cooperative) {c.FinishNativeDrop(); return;}
            if(__state==null || c.native.Amount(__state.Item.Definition)!=__state.Before-1) return;
            var drops=UnityEngine.Object.FindObjectsOfType(c.pickupType).Cast<Component>().Where(p=>p && !__state.BeforeDrop.Contains(p) && c.Supported(p) && (int)GameAccess.Get(p,"item_ID")==__state.Item.Definition).ToArray();
            var inventory=c.network.Inventories.LocalState; var economy=c.network.Inventories.SharedEconomy;
            if(drops.Length!=1 || inventory==null || economy==null || c.network.Inventories.Authority.PendingCount!=0) return;
            c.network.Inventories.ObserveSoloHost(new InventoryState(inventory.Owner,inventory.Revision+1,inventory.Capacity,inventory.Items.Where(i=>!i.Instance.Equals(__state.Item.Instance)),inventory.Equipped.HasValue && inventory.Equipped.Value.Equals(__state.Item.Instance) ? null : inventory.Equipped),new EconomyState(economy.Revision+1,economy.Money,economy.StorageCapacity,economy.LockerCapacity,economy.Items(0),economy.Items(1)));
            var adapter=c.Bind(drops[0],__state.Item,true); c.bindings[adapter]=c.network.World.Registry.Spawn(adapter,new EntityOwnership(OwnershipKind.System));
        }
        private static bool EquipIntent() => current == null || !current.Cooperative || NetworkApplyScope.Active || (current.network.Inventories.LocalState != null && current.network.Inventories.LocalState.Count(0) > 0);
        private static IEnumerable<CodeInstruction> DeferDestruction(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0; var destroy = AccessTools.Method(typeof(UnityEngine.Object), "Destroy", new[] { typeof(UnityEngine.Object) });
            foreach (var instruction in instructions) { if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, destroy)) { instruction.operand = AccessTools.Method(typeof(LootInventoryController), nameof(DeferredDestroy)); count++; } yield return instruction; }
            if (count != 3) throw new InvalidOperationException("Native pickup destruction contract changed: " + count);
        }
        private static void DeferredDestroy(UnityEngine.Object obj)
        {
            var go = obj as GameObject;
            if (current != null && go)
            {
                var pickup = go.GetComponent(current.pickupType);
                var t = current.localPending.Values.FirstOrDefault(p => p.Started && p.Request.Action == InventoryAction.Pickup && p.Pickup == pickup && pickup);
                if (t != null) { t.Removed = true; go.SetActive(false); return; }
            }
            UnityEngine.Object.Destroy(obj);
        }
        public void Dispose()
        {
            Disconnected(); harmony.UnpatchSelf(); if (current == this) current = null;
            network.World.PrepareRegistry -= Prepare; network.World.AdapterFactory = null;
            network.Inventories.ResultReceived -= Result; network.Inventories.StateReceived -= ReceivedState; network.RemoteRemoved -= Disconnected;
            network.Inventories.ValidateTarget = null; network.Inventories.CreateDrop = null; network.Inventories.CreateRemoteSeed = null; adapters.Clear(); registeredStatic.Clear(); bindings.Clear(); native.Reset();
        }
    }
}
