using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class WorldInteractionController : IDisposable
    {
        private static WorldInteractionController current;
        internal static WorldInteractionController Current => current;
        private readonly NetworkManager network;
        private readonly PlayerLocator playerLocator = new PlayerLocator();
        private readonly Action<string> log;
        private readonly Dictionary<Component, IInteractionAdapter> adapters = new Dictionary<Component, IInteractionAdapter>();
        private readonly Dictionary<IInteractionAdapter, EntityRef> references = new Dictionary<IInteractionAdapter, EntityRef>();
        private readonly Dictionary<EntityRef, double> pendingRequests = new Dictionary<EntityRef, double>();
        private readonly Harmony harmony = new Harmony(Plugin.Id + ".world-interactions");
        private readonly Type doorType = Type.GetType("Door, Assembly-CSharp", true);
        private bool prepared;
        private uint epoch;
        private bool hostRole;
        private double nextObservation;
        private EntityRef? developmentTarget;
        private Component slowIntent;
        private bool Client => network.Session.State != SessionState.Offline && !network.Session.IsHost;
        internal WorldInteractionController(NetworkManager manager, Action<string> logger)
        {
            network = manager; log = logger; current = this;
            network.World.PrepareRegistry = Prepare;
            network.ValidateInteractionTarget = ValidateTarget;
            network.InteractionCompleted += RequestCompleted;
            foreach (string name in new[] { "Use", "UseFully", "SlowFailed", "TimeAdd", "ResetLock", "OpenDestroyed", "NPCUse", "OpenDoorNPC", "CloseDoorNPC", "Unlock_Key" })
                Patch(doorType, name, nameof(DoorIntent), nameof(DoorChanged));
            harmony.Patch(AccessTools.Method(doorType,"Update"),prefix:new HarmonyMethod(typeof(WorldInteractionController),nameof(DoorUpdate)),postfix:new HarmonyMethod(typeof(WorldInteractionController),nameof(DoorUpdated)),finalizer:new HarmonyMethod(typeof(WorldInteractionController),nameof(DoorUpdateFinished)));
            // Replace only the verified bool write which begins local slow interaction; preserve PlayerScript.Update.
            harmony.Patch(AccessTools.Method(Type.GetType("PlayerScript, Assembly-CSharp", true), "Update"), transpiler: new HarmonyMethod(typeof(WorldInteractionController), nameof(IntentWrite)));
            foreach (string name in new[] { "StartLockpicking", "DoorUnlock", "DoorUnlockKey", "RelockDoor", "BreakPadlock" })
                Patch(Type.GetType("Lockpick, Assembly-CSharp", true), name, nameof(LimitedAction), nameof(LockChanged));
            foreach (string name in new[] { "BeginCut", "BrokeWindow", "UnlockWindow", "UnlockWindowNoAlarm" })
                Patch(Type.GetType("LockedWindow, Assembly-CSharp", true), name, nameof(LimitedAction), name == "BrokeWindow" || name == "UnlockWindow" ? nameof(WindowOutcome) : null);
            Patch(Type.GetType("PlayerScript, Assembly-CSharp", true), "BreakinCrowbar", nameof(BreachIntent));
            var crowbarIterator=Type.GetType("PlayerScript, Assembly-CSharp",true).GetNestedTypes(BindingFlags.NonPublic).Single(t=>t.Name.StartsWith("<CrowbarBreakin>",StringComparison.Ordinal));
            harmony.Patch(AccessTools.Method(crowbarIterator,"MoveNext"),transpiler:new HarmonyMethod(typeof(WorldInteractionController),nameof(BreachWorldFlags)));
            harmony.Patch(AccessTools.Method(Type.GetType("PlayerScript, Assembly-CSharp",true),"CrowbarBreakin"),postfix:new HarmonyMethod(typeof(WorldInteractionController),nameof(BreachCoroutine)));
            foreach(var contract in new[] {Tuple.Create("PlayerScript","AlertNPCs_DistanceBased"),Tuple.Create("AI","HeardSound"),Tuple.Create("Police_Manager","Update_Last_Location")})
                harmony.Patch(AccessTools.Method(Type.GetType(contract.Item1+", Assembly-CSharp",true),contract.Item2),transpiler:new HarmonyMethod(typeof(WorldInteractionController),nameof(NativeSoundPositionReads)));
            Patch(Type.GetType("Doorbell, Assembly-CSharp",true),"Ring",nameof(BellIntent),nameof(BellActivated));
        }
        private void Patch(Type type, string method, string prefix, string postfix = null)
        {
            var original = AccessTools.Method(type, method); if (original == null) throw new MissingMethodException(type.FullName, method);
            harmony.Patch(original, new HarmonyMethod(typeof(WorldInteractionController), prefix), postfix == null ? null : new HarmonyMethod(typeof(WorldInteractionController), postfix));
        }
        internal void Prepare()
        {
            using var phase=network.FrameProfile.Measure(Core.Diagnostics.RuntimePhase.RegistryRebuild);
            if (network.Scenes.Active && !network.Scenes.LocalLoaded) return;
            var registry = network.World.Registry;
            if (prepared && epoch == registry.SceneEpoch && hostRole == registry.IsHost && (registry.Count > 0 || adapters.Count == 0)) return;
            if (network.World.TestEntitiesEnabled) return; // The C synthetic acceptance mode remains isolated.
            CancelActions(); dirty.Clear(); adapters.Clear(); references.Clear(); pendingRequests.Clear(); slowIntent = null; network.Interactions.Doors = network.Interactions.Windows = network.Interactions.Unresolved = 0;
            var candidates = new List<KeyValuePair<StaticEntityDescriptor, Component>>();
            var vehicleDoorOwners = new Dictionary<Component,VehicleNetworkAdapter>();
            var linkedLocks = new Dictionary<Component, List<Component>>();
            foreach(var record in registry.Capture(EntityTypeId.Vehicle)) {
                var vehicle=registry.GetBinding(record.Entity) as VehicleNetworkAdapter;
                if(vehicle==null || !vehicle.IsAlive) continue;
                foreach(var component in vehicle.NativeDoors) {
                    if(!component) continue;
                    vehicleDoorOwners[component]=vehicle;
                }
            }
            var lockType = Type.GetType("Lockpick, Assembly-CSharp", true);
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s); if (!scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var lp in root.GetComponentsInChildren(lockType, true))
                    {
                        var leaves = GameAccess.Get(lp, "unlockDoor") as Array;
                        if (leaves == null) continue;
                        foreach (var leaf in leaves)
                        {
                            var door = leaf as Component; if (!door) continue;
                            if (!linkedLocks.TryGetValue(door, out var links)) linkedLocks.Add(door, links = new List<Component>());
                            if (!links.Contains(lp)) links.Add(lp);
                        }
                    }
                    foreach (var component in root.GetComponentsInChildren(doorType, true))
                    {
                        if (VehicleDescendant(component)) { if(!vehicleDoorOwners.ContainsKey(component)) network.Interactions.Unresolved++; continue; }
                        if (!GameAccess.Bool(component, "rotateOpen") && !GameAccess.Bool(component, "useVectorRotation") && !GameAccess.Bool(component, "moveOpen")) { network.Interactions.Unresolved++; continue; }
                        int ordinal = Array.IndexOf(component.gameObject.GetComponents(doorType), component);
                        candidates.Add(new KeyValuePair<StaticEntityDescriptor, Component>(UnityNetworkEntityAdapter.Describe(component, network.BuildFingerprintForDiagnostics, GameAccess.Bool(component, "isWindow") ? "window-v1" : "door-v1", ordinal), component));
                    }
                }
            }
            foreach(var pair in vehicleDoorOwners) {
                Component component=pair.Key; VehicleNetworkAdapter vehicle=pair.Value;
                if(!GameAccess.Bool(component,"rotateOpen") && !GameAccess.Bool(component,"useVectorRotation") && !GameAccess.Bool(component,"moveOpen")) { network.Interactions.Unresolved++; log("[TS2MP] Unsupported native vehicle door motion skipped: "+vehicle.Car.name+"/"+component.name); continue; }
                int ordinal=Array.IndexOf(component.gameObject.GetComponents(doorType),component);
                try {
                    string path=UnityNetworkEntityAdapter.RelativeHierarchyPath(vehicle.Car.transform,component.transform);
                    string slot=VehiclePresentation.SemanticSlot(vehicle.Car);
                    string identity=VehicleDoorPolicy.StableIdentity(slot,path,ordinal);
                    candidates.Add(new KeyValuePair<StaticEntityDescriptor,Component>(UnityNetworkEntityAdapter.Describe(component,network.BuildFingerprintForDiagnostics,GameAccess.Bool(component,"isWindow") ? "vehicle-window-v1" : "vehicle-door-v1",ordinal,identity),component));
                } catch(Exception error) { network.Interactions.Unresolved++; log("[TS2MP] Vehicle door identity skipped for "+vehicle.Car.name+"/"+component.name+": "+error.Message); }
            }
            foreach (var pair in candidates.OrderBy(p => p.Key.Canonical, StringComparer.Ordinal))
            {
                Component component = pair.Value;
                // Unknown tags fail visibly; never register an action whose capability cannot be represented.
                try { GameAccess.Tag(component); } catch (ArgumentException error) { network.Interactions.Unresolved++; log("[TS2MP] Unresolved world entity " + pair.Key.DiagnosticPath + ": " + error.Message); continue; }
                linkedLocks.TryGetValue(component, out var locks);
                vehicleDoorOwners.TryGetValue(component,out var vehicleOwner);
                bool rearPassengerDoor=vehicleOwner!=null && vehicleOwner.IsRearPassengerDoor(component);
                IInteractionAdapter adapter = GameAccess.Bool(component, "isWindow") ? (IInteractionAdapter)new WindowNetworkAdapter(component) : new DoorNetworkAdapter(component, locks?.ToArray(),vehicleOwner,rearPassengerDoor);
                if(adapter is DoorNetworkAdapter d) BindActions(d);
                if(adapter is WindowNetworkAdapter w) BindActions(w.Actions);
                var entity = registry.RegisterStatic(pair.Key, adapter, new EntityOwnership(OwnershipKind.System)); adapters.Add(component, adapter);
                references.Add(adapter, entity);
                if (adapter.EntityType == EntityTypeId.Door) network.Interactions.Doors++; else network.Interactions.Windows++;
                if (references.Count <= 16) log("[TS2MP] World binding entity=" + entity + " type=" + adapter.EntityType + " path=" + pair.Key.DiagnosticPath + " state=" + BitConverter.ToString(adapter.CaptureState()));
            }
            // noDoor windows lack a durable Door boundary; report explicitly, rather than inventing one.
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Type.GetType("LockedWindow, Assembly-CSharp", true))) if (!((Component)obj).GetComponent(doorType)) network.Interactions.Unresolved++;
            PrepareBells();
            prepared = true; epoch = registry.SceneEpoch; hostRole = registry.IsHost;
            log("[TS2MP] World manifest doors=" + network.Interactions.Doors + " windows=" + network.Interactions.Windows + " unresolved=" + network.Interactions.Unresolved + " collisions=" + registry.CollisionCount);
        }
        internal void Observe(bool force = false)
        {
            ExpireActions();
            if (!Client) slowIntent = null;
            if (!prepared) return;
            if (network.Scenes.Active && !network.Scenes.LocalLoaded) return;
            if (!force && network.Now < nextObservation) return;
            nextObservation = network.Now + 0.1;
            if(Client) { CheckLocalGesture(); return; }
            if(force) dirty.Clear();
            var observed = force ? (IEnumerable<Component>)adapters.Keys : dirty.Drain(32);
            foreach (var component in observed)
            {
                if(!adapters.TryGetValue(component,out var adapter)) continue;
                var entity = references[adapter]; var entry = network.World.Registry.Lookup(entity);
                if (entry == null || entry.State.Tombstone) continue;
                if (!adapter.IsAlive)
                {
                    if (network.World.Registry.IsHost) network.World.Registry.Despawn(entity);
                    else if (Client && network.World.State == WorldSyncState.WorldReady) network.World.Resnapshot();
                    continue;
                }
                if (network.World.Registry.GetBinding(entity) != adapter) continue;
                var record = entry.State;
                if (!StableHash.Equal(record.Payload, adapter.CaptureState()))
                {
                    if (network.World.Registry.IsHost)
                    {
                        network.World.Registry.CommitState(entity);
                        // Continuous native motion already has bounded reliable deltas; no per-sample log/second serialization.
                    }
                    else if (Client && network.World.State == WorldSyncState.WorldReady)
                    {
                        try { using (new NetworkApplyScope(true)) adapter.ApplyState(record.Payload); }
                        catch { network.World.Resnapshot(); }
                    }
                }
            }
        }
        private static bool VehicleDescendant(Component component)
        {
            if (GameAccess.Bool(component, "isCarDoor")) return true;
            // Actual vehicle leaves can lack isCarDoor; exclude fixed vehicle roots too.
            foreach (var parent in component.GetComponentsInParent<Transform>(true))
            {
                // Verified unused MainScene vehicle template has no controller marker.
                // Authored ancestry remains stable when furniture Start removes rigidbodies.
                if (parent.name == "Real Car 4" && parent.parent && parent.parent.name == "IMPORTANT OBJECTS" && parent.gameObject.scene.name == "MainScene") return true;
                foreach (var script in parent.GetComponents<Component>())
                {
                    if (!script) continue;
                    switch (script.GetType().FullName)
                    {
                        case "CarController": case "Car": case "AICar": case "CMS_Car": case "CopCar":
                        case "CarDisassembly": case "EVP.VehicleController": return true;
                    }
                }
            }
            return false;
        }
        private static bool Reachable(Collider collider, Vector3 eye, float distance, int mask)
        {
            if (!collider.enabled || !collider.gameObject.activeInHierarchy || (collider.bounds.ClosestPoint(eye) - eye).sqrMagnitude > distance * distance) return false;
            // Bounds select candidate rays only. Collider.Raycast verifies actual geometry,
            // including non-convex mesh colliders unsupported by Collider.ClosestPoint.
            foreach (var point in new[] { collider.bounds.ClosestPoint(eye), collider.bounds.center })
            {
                Vector3 direction = point - eye; if (direction.sqrMagnitude < .0001f) continue;
                RaycastHit targetHit, firstHit; var ray = new Ray(eye, direction.normalized);
                if (collider.Raycast(ray, out targetHit, distance) && Physics.Raycast(ray, out firstHit, targetHit.distance + .05f, mask, QueryTriggerInteraction.UseGlobal) && firstHit.collider == collider) return true;
            }
            return false;
        }
        private bool TryRemoteInteractionEye(PlayerTransform pose,out Vector3 eye)
        {
            eye=default(Vector3);
            var player=playerLocator.Find(); var controller=player ? player.GetComponent<CharacterController>() : null;
            var motor=player ? player.GetComponent(Type.GetType("PlayerMotorBehavior, Assembly-CSharp",true)) : null;
            if(!player || !controller || !motor) return false;
            float height=(float)GameAccess.Get(motor,pose.State.Crouched ? "crouchHeight" : "defaultCrouchHeight2");
            eye=new Vector3(pose.X,pose.Y+controller.center.y+height*.5f-.05f,pose.Z);
            return true;
        }
        private static bool WithinNativeVehicleDoorReach(Component door,Vector3 eye)
        {
            return door && door.GetComponents<Collider>().Any(c=>c && c.enabled && c.gameObject.activeInHierarchy &&
                VehicleDoorPolicy.WithinNativeInteractionReach((c.bounds.ClosestPoint(eye)-eye).sqrMagnitude));
        }
        internal bool TryGetRearPassengerDoor(Component door, VehicleNetworkAdapter vehicle, out EntityRef entity)
        {
            entity=default(EntityRef);
            if(!door || vehicle==null || !adapters.TryGetValue(door,out var binding) || !(binding is DoorNetworkAdapter adapter) ||
                !VehicleDoorPolicy.MatchesVehicleTarget(vehicle.Entity,adapter.VehicleOwner?.Entity ?? default(EntityRef),adapter.RearPassengerEntryDoor) ||
                !references.TryGetValue(adapter,out entity) || network.World.Registry.GetBinding(entity)!=adapter) return false;
            return true;
        }
        internal bool ValidateRearPassengerEntry(byte actor, EntityRef vehicleRef, EntityRef doorRef)
        {
            var vehicle=network.World.Registry.GetBinding(vehicleRef) as VehicleNetworkAdapter;
            var door=network.World.Registry.GetBinding(doorRef) as DoorNetworkAdapter;
            if(vehicle==null || !vehicle.Operational || !vehicle.PassengerSeat || door==null || !door.IsAlive ||
                !VehicleDoorPolicy.MatchesVehicleTarget(vehicleRef,door.VehicleOwner?.Entity ?? default(EntityRef),door.RearPassengerEntryDoor) ||
                !door.Door.gameObject.activeInHierarchy || door.Door.tag!="Door" || !GameAccess.Bool(door.Door,"isOpen") ||
                GameAccess.Bool(door.Door,"opening") || GameAccess.Bool(door.Door,"openingSlowly") || GameAccess.Bool(door.Door,"load_slow") ||
                !door.Presentation.Permits(InteractionKind.Close)) return false;
            if(actor==network.Session.LocalPlayerId) {
                var local=playerLocator.Find();
                return SharedVehicleController.Current?.NativeRearDoorHit(local,door.Door)==true;
            }
            if(actor!=network.Session.RemotePlayerId || !network.Telemetry.HasPose || network.Telemetry.ReceiveAge(network.Now)>.75) return false;
            var pose=network.Telemetry.Latest;
            if(pose.State.Vehicle.IsInVehicle || pose.State.SeatEligibility!=SeatEligibility.Eligible) return false;
            return TryRemoteInteractionEye(pose,out var eye) && WithinNativeVehicleDoorReach(door.Door,eye);
        }
        internal string DevelopmentProbe(string action)
        {
            Prepare();
            var ps = playerLocator.Find();
            var cam = ps ? GameAccess.Get(ps, "cam") as Transform : null;
            if (!cam) throw new InvalidOperationException("Genuine camera unavailable.");
            foreach (var pair in adapters.OrderBy(p => (p.Key.transform.position - cam.position).sqrMagnitude).Take(12))
                log("[TS2MP] WORLD PROBE: entity=" + network.World.Registry.FindBinding(pair.Value) + " position=" + pair.Key.transform.position.ToString("F3") + " active=" + pair.Key.gameObject.activeInHierarchy + " permitted=" + pair.Value.Permits(InteractionKind.Open) + " state=" + BitConverter.ToString(pair.Value.CaptureState()));
            if (action == "probe")
            {
                developmentTarget = null;
                foreach (var pair in adapters.OrderBy(p => (p.Key.transform.position - cam.position).sqrMagnitude))
                {
                    if (!pair.Value.Permits(InteractionKind.Open)) continue;
                    foreach (var collider in pair.Key.GetComponents<Collider>())
                    {
                        if (!collider.enabled) continue;
                          if (Reachable(collider, cam.position, 2.25f, ((LayerMask)GameAccess.Get(ps, "mask")).value))
                        { developmentTarget = network.World.Registry.FindBinding(pair.Value); break; }
                    }
                    if (developmentTarget.HasValue) break;
                }
                if (!developmentTarget.HasValue) throw new InvalidOperationException("No usable real door/window within original 2.25m and line of sight.");
                log("[TS2MP] DEVELOPMENT WORLD TARGET: " + developmentTarget.Value);
                return developmentTarget.Value.Id.Value.ToString("X16");
            }
            if (action == "audit")
            {
                Observe(true); log("[TS2MP] WORLD AUDIT: " + network.World.Registry.DevelopmentSummary());
                if (developmentTarget.HasValue)
                {
                    var selected = network.World.Registry.GetBinding(developmentTarget.Value);
                    var targetDoor = selected is DoorNetworkAdapter ? ((DoorNetworkAdapter)selected).Door : selected is WindowNetworkAdapter ? ((WindowNetworkAdapter)selected).Door : null;
                    if (targetDoor)
                    {
                        var source = GameAccess.Get(targetDoor, "DoorSound") as AudioSource;
                        log("[TS2MP] DEVELOPMENT PRESENTATION: entity=" + developmentTarget + " open=" + GameAccess.Bool(targetDoor, "isOpen") + " opening=" + GameAccess.Bool(targetDoor, "opening") + " usable=" + GameAccess.Bool(targetDoor, "canUse") + " rotation=" + targetDoor.transform.localEulerAngles.ToString("F3") + " localPosition=" + targetDoor.transform.localPosition.ToString("F3") + " audioPlaying=" + (source && source.isPlaying));
                    }
                }
                return "";
            }
            if (!network.Session.IsHost || !developmentTarget.HasValue || Time.timeScale <= 0) throw new InvalidOperationException("Unpaused host and selected target required.");
            var adapter = network.World.Registry.GetBinding(developmentTarget.Value) as IInteractionAdapter;
            var kind = action == "open" ? InteractionKind.Open : InteractionKind.Close;
            if (adapter == null || !adapter.Permits(kind)) throw new InvalidOperationException("Target no longer permitted.");
            // Revalidate against actual host pose before each bounded fixture action.
            var component = adapter is DoorNetworkAdapter ? ((DoorNetworkAdapter)adapter).Door : ((WindowNetworkAdapter)adapter).Door;
            if (!component.GetComponents<Collider>().Any(c => Reachable(c, cam.position, 2.25f, ((LayerMask)GameAccess.Get(ps, "mask")).value))) throw new InvalidOperationException("Fixture target out of original reach or line of sight.");
            adapter.Execute(kind); Observe(true);
            log("[TS2MP] DEVELOPMENT ORIGINAL INTERACTION: entity=" + developmentTarget.Value + " kind=" + kind + " revision=" + network.World.Registry.WorldRevision);
            return developmentTarget.Value.Id.Value.ToString("X16");
        }
        private InteractionResult ValidateTarget(byte actor, EntityRef entity)
        {
            if (actor != network.Session.RemotePlayerId || !network.Telemetry.HasPose || network.Telemetry.ReceiveAge(network.Now) > 1) return InteractionResult.WrongActor;
            var binding = network.World.Registry.GetBinding(entity);
            Component door = binding is DoorNetworkAdapter ? ((DoorNetworkAdapter)binding).Door : binding is WindowNetworkAdapter ? ((WindowNetworkAdapter)binding).Door : binding is DoorbellNetworkAdapter bell ? bell.Bell : null;
            if (!door || !door.gameObject.activeInHierarchy) return InteractionResult.InvalidTarget;
            if(binding is DoorNetworkAdapter vehicleDoor && vehicleDoor.VehicleOwner!=null) {
                var vehicle=vehicleDoor.VehicleOwner; var vehiclePose=network.Telemetry.Latest;
                if(!TryRemoteInteractionEye(vehiclePose,out var vehicleEye)) return InteractionResult.InvalidTarget;
                bool nativeDriverClose=vehicle.IsNativeFrontEntryDoor(door) &&
                    (vehicle.State.PendingDriver==actor || vehicle.State.Driver==actor) && GameAccess.Bool(door,"isOpen") &&
                    (door.tag=="Door" || door.tag=="Untagged") && !GameAccess.Bool(door,"brokeHinge") && !GameAccess.Bool(door,"npcUsing");
                if(nativeDriverClose)
                    return WithinNativeVehicleDoorReach(door,vehicleEye) ? InteractionResult.Accepted : InteractionResult.OutOfRange;
                if(vehiclePose.State.Vehicle.IsInVehicle || vehiclePose.State.SeatEligibility!=SeatEligibility.Eligible) return InteractionResult.InvalidTarget;
                // The Client already sent this stable native door identity after its
                // original PlayerScript ray selected it. Host validates authored
                // binding, eligibility and native reach without inventing a new ray
                // through a differently interpolated vehicle replica.
                return WithinNativeVehicleDoorReach(door,vehicleEye) ? InteractionResult.Accepted : InteractionResult.OutOfRange;
            }
            var pose = network.Telemetry.Latest;
            // The genuine root is the capsule centre (centre=0 in observed gameplay), not feet.
            // Use the original controller's eye region with the received crouch stance; no client-supplied eye point.
            var ps = playerLocator.Find(); var controller = ps ? ps.GetComponent<CharacterController>() : null;
            if (!controller) return InteractionResult.InvalidTarget;
            var motor = ps.GetComponent(Type.GetType("PlayerMotorBehavior, Assembly-CSharp", true));
            if (!ps || !motor) return InteractionResult.InvalidTarget;
            float height = (float)GameAccess.Get(motor, pose.State.Crouched ? "crouchHeight" : "defaultCrouchHeight2");
            float eyeOffset = controller.center.y + height * 0.5f - 0.05f;
            int mask = ((LayerMask)GameAccess.Get(ps, "mask")).value;
            Vector3 eye = new Vector3(pose.X, pose.Y + eyeOffset, pose.Z);
            Collider[] colliders = door.GetComponents<Collider>();
            if(binding is DoorNetworkAdapter d && door.tag=="Lock1") colliders = d.Locks.Where(l=>l && l.tag=="Lock1").SelectMany(l=>l.GetComponents<Collider>()).Concat(colliders).ToArray();
            foreach (var collider in colliders)
            {
                if (!collider.enabled) continue;
                if (Reachable(collider, eye, 2.75f, mask)) return InteractionResult.Accepted;
            }
            return colliders.Any(c => c.enabled && InteractionAuthority.InRange((c.bounds.ClosestPoint(eye) - eye).sqrMagnitude, 2.25f, 0.5f)) ? InteractionResult.InvalidTarget : InteractionResult.OutOfRange;
        }
        private void Request(Component door)
        {
            IInteractionAdapter adapter; if (!adapters.TryGetValue(door, out adapter)) { network.Interactions.Rejected++; return; }
            var entity = network.World.Registry.FindBinding(adapter); if (!entity.HasValue) return;
            double expires; if (pendingRequests.TryGetValue(entity.Value, out expires) && network.Now < expires) return;
            var state = network.World.Registry.Lookup(entity.Value).State;
            bool open;
            if (state.Type == EntityTypeId.Door) { DoorState decoded; if (!DoorState.TryDecode(state.Payload, out decoded)) return; open = decoded.Open; }
            else { WindowState decoded; if (!WindowState.TryDecode(state.Payload, out decoded)) return; open = decoded.Open; }
            if (network.SendInteraction(entity.Value, open ? InteractionKind.Close : InteractionKind.Open)) pendingRequests[entity.Value] = network.Now + 2;
        }
        private void RequestCompleted(ulong request, InteractionResult result, ulong revision) { pendingRequests.Clear(); ActionCompleted(request,result); }
        internal void SceneInvalidating() { CancelActions(); prepared = false; adapters.Clear(); references.Clear(); pendingRequests.Clear(); localDriverCloseIntents.Clear(); localDriverCloseRequests.Clear(); slowIntent = null; dirty.Clear(); }
        private static bool DoorIntent(Component __instance, MethodBase __originalMethod)
        {
            if (current == null || NetworkApplyScope.Active || !current.adapters.ContainsKey(__instance)) return true;
            if (!current.network.TransactionsAllowed) return false;
            if(!current.Client) return !current.RemoteOwns(__instance);
            if(__originalMethod.Name=="Use" && current.adapters.TryGetValue(__instance,out var adapter) && adapter is DoorNetworkAdapter vehicleDoor &&
                vehicleDoor.VehicleOwner!=null && GameAccess.Bool(__instance,"isOpen") && SharedVehicleController.Current?.IsLocalDriverAutoClose(vehicleDoor.VehicleOwner,__instance)==true) {
                current.localDriverCloseIntents.Add(__instance); return true;
            }
            if (__originalMethod.Name == "SlowFailed")
            {
                // PlayerScript calls this again when clearing assignDoor after release.
                // Consume the original gesture before sending, including rejected intents.
                if (ReferenceEquals(current.slowIntent, __instance)) { current.SendAction(__instance,InteractionKind.SlowRelease); current.slowIntent = null; }
            }
            else if (__originalMethod.Name == "Use" || __originalMethod.Name == "TimeAdd") current.Request(__instance);
            return false;
        }
        private static bool LimitedAction(Component __instance, MethodBase __originalMethod)
        {
            if(current==null || NetworkApplyScope.Active) return true;
            if(!current.Client) return current.network.TransactionsAllowed && !current.RemoteOwns(current.DoorFor(__instance));
            return current.ClientLockAction(__instance,__originalMethod.Name);
        }
        private static void WindowOutcome(Component __instance, MethodBase __originalMethod)
        {
            if (current == null || current.Client || NetworkApplyScope.Active || GameAccess.Bool(__instance, "isTutorial")) return;
            IInteractionAdapter adapter; var door = __instance.GetComponent(current.doorType);
            if (door && current.adapters.TryGetValue(door, out adapter) && adapter is WindowNetworkAdapter)
                ((WindowNetworkAdapter)adapter).Outcome = __originalMethod.Name == "BrokeWindow" ? WindowGlass.Broken : WindowGlass.Cut;
            if(door) current.dirty.Add(door);
        }
        private static IEnumerable<CodeInstruction> IntentWrite(IEnumerable<CodeInstruction> input)
        {
            int replacements = 0; var field = AccessTools.Field(Type.GetType("Door, Assembly-CSharp", true), "load_slow");
            foreach (var instruction in input)
            {
                if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, field))
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(WorldInteractionController), nameof(SetSlowIntent)); replacements++; }
                yield return instruction;
            }
            if (replacements != 3) throw new InvalidOperationException("PlayerScript slow-intent contract changed: " + replacements);
        }
        private static void SetSlowIntent(Component door, bool value)
        {
            if (current == null || !current.Client || NetworkApplyScope.Active || !current.adapters.ContainsKey(door)) { GameAccess.Set(door, "load_slow", value); return; }
            // Keep original hold/release UI and actor handling. Release calls SlowFailed -> bounded request.
            if (value) { current.slowIntent = door; current.SendAction(door,InteractionKind.SlowBegin); }
            GameAccess.Set(door, "load_slow", false);
        }
        public void Dispose()
        {
            CancelActions();
            harmony.UnpatchSelf(); if (current == this) current = null;
            network.InteractionCompleted -= RequestCompleted;
            network.World.PrepareRegistry = null; network.ValidateInteractionTarget = null; adapters.Clear(); references.Clear(); pendingRequests.Clear();
        }
    }
}
