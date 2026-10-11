using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class WorldInteractionController
    {
        private readonly DirtyDoors dirty=new DirtyDoors();
        private sealed class DirtyDoors
        {
            private readonly HashSet<Component> members=new HashSet<Component>();
            private readonly Queue<Component> pending=new Queue<Component>();
            internal void Add(Component door) {if(members.Add(door)) pending.Enqueue(door);}
            internal void Clear() {members.Clear();pending.Clear();}
            internal IEnumerable<Component> Drain(int limit) {while(limit-->0 && pending.Count>0) {var door=pending.Dequeue();members.Remove(door);yield return door;}}
        }
        private readonly Dictionary<Component,double> remoteSlow=new Dictionary<Component,double>();
        private readonly Dictionary<Component,double> remoteBreach=new Dictionary<Component,double>();
        private readonly Dictionary<Component,string> nativeTags=new Dictionary<Component,string>();
        private readonly Dictionary<Component,DoorbellNetworkAdapter> bells=new Dictionary<Component,DoorbellNetworkAdapter>();
        private readonly Dictionary<ulong,InteractionKind> actions=new Dictionary<ulong,InteractionKind>();
        private readonly HashSet<Component> localDriverCloseIntents=new HashSet<Component>();
        private readonly Dictionary<ulong,DoorNetworkAdapter> localDriverCloseRequests=new Dictionary<ulong,DoorNetworkAdapter>();
        private GameObject breachObject;
        private RaycastHit breachHit;
        private Component breachDoor;
        private bool nativeBreach;
        private IEnumerator localBreachCoroutine;
        private Component localBreachActor;
        private readonly List<Tuple<object,string,object>> localBreachFields=new List<Tuple<object,string,object>>();
        private GameObject localBreachHand;
        private bool localBreachHandActive;
        private Transform soundActor;
        private Vector3 soundOrigin;
        private Component[] possessionTriggers=Array.Empty<Component>();
        private double slowExpires, breachExpires;

        private void PrepareBells()
        {
            possessionTriggers=UnityEngine.Object.FindObjectsOfType(Type.GetType("OnPossesion, Assembly-CSharp",true)).Cast<Component>().ToArray();
            bells.Clear(); var type=Type.GetType("Doorbell, Assembly-CSharp",true);
            for(int s=0;s<UnityEngine.SceneManagement.SceneManager.sceneCount;s++) {
                var scene=UnityEngine.SceneManagement.SceneManager.GetSceneAt(s); if(!scene.isLoaded || string.IsNullOrEmpty(scene.path)) continue;
                foreach(var root in scene.GetRootGameObjects()) foreach(var bell in root.GetComponentsInChildren(type,true)) {
                    var adapter=new DoorbellNetworkAdapter(bell);
                    var descriptor=UnityNetworkEntityAdapter.Describe(bell,network.BuildFingerprintForDiagnostics,"doorbell-v1",Array.IndexOf(bell.GetComponents(type),bell));
                    var entity=network.World.Registry.RegisterStatic(descriptor,adapter,new EntityOwnership(OwnershipKind.System));
                    bells.Add(bell,adapter); references.Add(adapter,entity);
                }
            }
        }
        private void BindActions(DoorNetworkAdapter adapter)
        {
            adapter.Continuation=(actor,kind)=>actor==network.Session.RemotePlayerId &&
                (kind==InteractionKind.SlowRelease && remoteSlow.ContainsKey(adapter.Door) || kind==InteractionKind.BreachComplete && remoteBreach.ContainsKey(adapter.Door));
            adapter.RemotePermits=(actor,kind)=>PermitsAction(adapter,actor,kind);
            adapter.RemoteExecute=(actor,kind)=>ExecuteAction(adapter,actor,kind);
        }
        private bool RemoteOwns(Component door) => door && (remoteSlow.ContainsKey(door) || remoteBreach.ContainsKey(door));
        private void ExpireActions()
        {
            if(remoteBreach.Count>0) foreach(var pair in remoteBreach.ToArray()) if(!pair.Key || network.Now-pair.Value>5) remoteBreach.Remove(pair.Key);
        }
        private bool PermitsAction(DoorNetworkAdapter a,byte actor,InteractionKind kind)
        {
            if(actor!=network.Session.RemotePlayerId || !a.IsAlive || !a.Door.gameObject.activeInHierarchy || Time.timeScale<=0) return false;
            if(kind==InteractionKind.Close && NativeDriverEntryClosePermitted(a,actor)) return true;
            var ps=playerLocator.Find(); if(!ps || !network.Inventories.RemoteIdentity.HasValue) return false;
            var inventory=network.Inventories.Authority.Get(network.Inventories.RemoteIdentity.Value); if(inventory==null) return false;
            if(kind==InteractionKind.SlowRelease) return remoteSlow.ContainsKey(a.Door);
            if(kind==InteractionKind.BreachComplete) return remoteBreach.TryGetValue(a.Door,out var began) && network.Now-began>=.75 && network.Now-began<5 && a.Door.tag=="Lock1";
            if(RemoteOwns(a.Door) || GameAccess.Bool(a.Door,"npcUsing")) return false;
            if(kind==InteractionKind.BreachBegin) {
                if(a.Door.tag!="Lock1" || GameAccess.Bool(a.Door,"only_with_key") || GameAccess.Bool(a.Door,"brokeHinge") || inventory.Count(0)==0 || network.Telemetry.Latest.State.EquippedItemId!=0) return false;
                var lp=Lock(a); return lp && !GameAccess.Bool(lp,"is_tutorial") && !GameAccess.Bool(lp,"isPadlock") && ((int)GameAccess.Get(lp,"LockpickDifficulty")<2 || GameAccess.Bool(ps,"crowbarLevel3"));
            }
            if(kind==InteractionKind.UnlockKey) return a.Door.tag=="Lock1" && GameAccess.Bool(a.Door,"requires_key") && inventory.Count((ushort)(int)GameAccess.Get(a.Door,"key_item_ID"))>0 && Lock(a);
            return a.Presentation.Permits(kind) && (kind!=InteractionKind.SlowBegin || GameAccess.Bool(ps,"canUseDoorsSlowly"));
        }
        private static bool NativeDriverEntryClosePermitted(DoorNetworkAdapter a,byte actor)
        {
            var owner=a?.VehicleOwner;
            return owner!=null && owner.IsNativeFrontEntryDoor(a.Door) &&
                (owner.State.Driver==actor || owner.State.PendingDriver==actor) && a.Door &&
                (a.Door.tag=="Door" || a.Door.tag=="Untagged") && GameAccess.Bool(a.Door,"isOpen") &&
                !GameAccess.Bool(a.Door,"brokeHinge") && !GameAccess.Bool(a.Door,"npcUsing") && ((Behaviour)a.Door).enabled;
        }
        private static Component Lock(DoorNetworkAdapter a) => a.Locks.FirstOrDefault(l=>l && l.tag=="Lock1");
        private void ExecuteAction(DoorNetworkAdapter a,byte actor,InteractionKind kind)
        {
            using(new NetworkApplyScope()) {
                if(kind==InteractionKind.SlowBegin) { remoteSlow[a.Door]=network.Now; GameAccess.Set(a.Door,"load_slow",true); }
                else if(kind==InteractionKind.SlowRelease) { using(new NativeDoorScope(a.Door)) {GameAccess.Set(a.Door,"load_slow",false); GameAccess.Call(a.Door,"SlowFailed");} }
                else if(kind==InteractionKind.BreachBegin) remoteBreach[a.Door]=network.Now;
                else if(kind==InteractionKind.BreachComplete) {
                    var lp=Lock(a); if(!lp) throw new InvalidOperationException("Native lock lost.");
                    // This is the original coroutine's world completion, after the owner's native animation/tool cost.
                    GameAccess.Call(lp,"DoorUnlock"); foreach(Component door in (Array)GameAccess.Get(lp,"unlockDoor")) { GameAccess.Call(door,"OpenDestroyed"); dirty.Add(door); }
                    ApplyBreachEffects(a);
                    remoteBreach.Remove(a.Door);
                }
                else if(kind==InteractionKind.UnlockKey) GameAccess.Call(Lock(a),"DoorUnlockKey");
                else if(kind==InteractionKind.Close && NativeDriverEntryClosePermitted(a,actor)) a.Presentation.ExecuteNativeEntryClose();
                else a.Execute(kind);
            }
            dirty.Add(a.Door);
        }
        private Component DoorFor(Component lp)
        {
            if(!lp) return null;
            if(adapters.ContainsKey(lp)) return lp;
            if(lp.GetType().Name=="Lockpick") foreach(Component door in (Array)GameAccess.Get(lp,"unlockDoor")) if(door && adapters.ContainsKey(door)) return door;
            return null;
        }
        private bool SendAction(Component component,InteractionKind kind)
        {
            IInteractionAdapter adapter;
            if(!adapters.TryGetValue(component,out adapter) && !bells.TryGetValue(component,out var bell)) return false;
            if(adapter==null) adapter=bells[component];
            if(!references.TryGetValue(adapter,out var entity) || !network.SendInteraction(entity,kind)) return false;
            actions[network.LastInteractionRequestId]=kind;
            if(kind==InteractionKind.SlowBegin) slowExpires=network.Now+5;
            return true;
        }
        private void ActionCompleted(ulong id,InteractionResult result)
        {
            if(!actions.TryGetValue(id,out var kind)) { localDriverCloseRequests.Remove(id); return; } actions.Remove(id);
            if(localDriverCloseRequests.TryGetValue(id,out var localDoor)) {
                localDriverCloseRequests.Remove(id);
                if(result!=InteractionResult.Accepted) {
                    localDoor.Presentation.ClearCloseAudioSuppression();
                    var canonicalEntity=references.TryGetValue(localDoor,out var localEntity) ? localEntity : default(EntityRef);
                    var canonical=canonicalEntity.Id.IsValid ? network.World.Registry.Lookup(canonicalEntity) : null;
                    if(canonical!=null && !canonical.State.Tombstone) try { using(new NetworkApplyScope(true)) localDoor.ApplyState(canonical.State.Payload); } catch { network.World.Resnapshot(); }
                }
            }
            if(kind==InteractionKind.BreachBegin) {
                if(result==InteractionResult.Accepted && breachObject && breachDoor) {
                    var ps=playerLocator.Find(); if(!ps) return;
                    CaptureLocalBreach(ps);
                    try {nativeBreach=true; ps.GetType().GetMethod("BreakinCrowbar").Invoke(ps,new object[] {breachObject,breachHit});}
                    catch {CancelLocalBreach();throw;}
                    finally {nativeBreach=false;}
                } else {breachObject=null; breachDoor=null;}
            }
            if(kind==InteractionKind.SlowBegin && result!=InteractionResult.Accepted) ReleaseLocalSlow();
        }
        private void ReleaseLocalSlow()
        {
            var ps=playerLocator.Find();
            if(ps && slowIntent && GameAccess.Get(ps,"assignDoor") as Component==slowIntent) {
                GameAccess.Set(ps,"opening_slowly",false); GameAccess.Set(ps,"assignDoor",null);
                GameAccess.Set(GameAccess.Get(ps,"PMB"),"canMove",true); GameAccess.Set(GameAccess.Get(ps,"PCB"),"allowRotation",true);
                GameAccess.Set(GameAccess.Get(ps,"PCB"),"mouseSensitivity",GameAccess.Get(ps,"mouseSensitivityOriginal"));
            }
            slowIntent=null;
        }
        private void CheckLocalGesture()
        {
            if(slowIntent && (network.Now>slowExpires || !network.TransactionsAllowed)) ReleaseLocalSlow();
            var ps=playerLocator.Find();
            if(breachDoor && network.Now>breachExpires) {CancelLocalBreach();breachDoor=null;breachObject=null;}
            else if(breachDoor && ps && !GameAccess.Bool(ps,"breakingInCrowbar") && actions.Count==0) {ClearLocalBreach();breachDoor=null;breachObject=null;}
        }
        private void CancelActions() {
            ReleaseLocalSlow();CancelLocalBreach();
            foreach(var pair in remoteSlow) if(pair.Key && GameAccess.Get(pair.Key,"PS") is Component ps && ps) {
                using(new NetworkApplyScope()) using(new NativeDoorScope(pair.Key)) {GameAccess.Set(pair.Key,"load_slow",false);GameAccess.Call(pair.Key,"SlowFailed");}
            }
            remoteSlow.Clear(); remoteBreach.Clear(); nativeTags.Clear(); bells.Clear(); possessionTriggers=Array.Empty<Component>(); actions.Clear(); localDriverCloseIntents.Clear(); localDriverCloseRequests.Clear(); breachObject=null; breachDoor=null;
        }
        private void CaptureLocalBreach(Component ps)
        {
            localBreachActor=ps;
            void Capture(object owner,params string[] fields) {foreach(var field in fields) localBreachFields.Add(Tuple.Create(owner,field,GameAccess.Get(owner,field)));}
            Capture(ps,"breakingInCrowbar","isLockpicking","hideHands","canInteract");
            Capture(GameAccess.Get(ps,"PMB"),"canMove","canRun");Capture(GameAccess.Get(ps,"PCB"),"allowRotation");
            localBreachHand=GameAccess.Get(ps,"breakin_hand") as GameObject;localBreachHandActive=localBreachHand && localBreachHand.activeSelf;
        }
        private static void BreachCoroutine(IEnumerator __result) {var c=current;if(c?.nativeBreach==true) c.localBreachCoroutine=__result;}
        private void CancelLocalBreach()
        {
            if(localBreachActor) {
                if(localBreachCoroutine!=null) ((MonoBehaviour)localBreachActor).StopCoroutine(localBreachCoroutine);
                foreach(var field in localBreachFields) {if(field.Item1 is UnityEngine.Object obj && !obj) continue;GameAccess.Set(field.Item1,field.Item2,field.Item3);}
                if(localBreachHand) localBreachHand.SetActive(localBreachHandActive);
            }
            ClearLocalBreach();
        }
        private void ClearLocalBreach() {localBreachActor=null;localBreachHand=null;localBreachCoroutine=null;localBreachFields.Clear();}
        // Only this native coroutine's authority branch is suppressed on its owning Client.
        // Its tool cost, hands, sounds, dust and control restoration remain native.
        private static IEnumerable<CodeInstruction> BreachWorldFlags(IEnumerable<CodeInstruction> instructions)
        {
            int matches=0;
            foreach(var code in instructions) {
                yield return code;
                if(code.opcode==OpCodes.Ldfld && code.operand is FieldInfo field && (field.Name=="isOnPossesion" || field.Name=="isInPossesionRange")) {
                    matches++; yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(WorldInteractionController),nameof(LocalBreachWorldAllowed)));
                }
            }
            if(matches!=2) throw new InvalidOperationException("Native crowbar world boundary changed.");
        }
        private static bool LocalBreachWorldAllowed(bool native) => native && !(current?.Client==true && current.breachDoor);
        private static IEnumerable<CodeInstruction> NativeSoundPositionReads(IEnumerable<CodeInstruction> instructions)
        {
            var getter=AccessTools.PropertyGetter(typeof(Transform),"position");int count=0;
            foreach(var code in instructions) {
                if(code.Calls(getter)) {code.opcode=OpCodes.Call;code.operand=AccessTools.Method(typeof(WorldInteractionController),nameof(NativeSoundPosition));count++;}
                yield return code;
            }
            if(count==0) throw new InvalidOperationException("Native sound position boundary changed.");
        }
        private static Vector3 NativeSoundPosition(Transform transform) => current!=null && current.soundActor && transform==current.soundActor ? current.soundOrigin : transform.position;
        private void ApplyBreachEffects(DoorNetworkAdapter door)
        {
            var ps=playerLocator.Find(); if(!ps || !network.Telemetry.HasPose) return;
            var pose=network.Telemetry.Latest; var origin=new Vector3(pose.X,pose.Y,pose.Z);
            Component inside=null,range=null;
            foreach(var trigger in possessionTriggers) {
                if(!trigger || !trigger.gameObject.activeInHierarchy) continue;
                var collider=trigger.GetComponent<Collider>();
                if(!collider || !collider.enabled || (collider.ClosestPoint(origin)-origin).sqrMagnitude>.01f) continue;
                if(GameAccess.Bool(trigger,"isInfo")) range=trigger; else inside=trigger;
            }
            // No residence means the original coroutine would perform no possession effects.
            if(!inside && !range) return;
            var residence=GameAccess.Get(inside ? inside : range,"ResidenceScript");
            var on=GameAccess.Get(ps,"isOnPossesion"); var near=GameAccess.Get(ps,"isInPossesionRange");
            var originalTrigger=GameAccess.Get(ps,"currentPossesion"); var originalResidence=GameAccess.Get(ps,"currentPossesionScript");
            var npcFlags=new List<Tuple<object,object>>();
            // Borrow only the native sound callback's possession context. The original
            // actor/controller/camera transform never moves; native position reads are
            // redirected to the authenticated origin only within these synchronous calls.
            try {
                soundActor=ps.transform;soundOrigin=origin;
                GameAccess.Set(ps,"isOnPossesion",inside!=null); GameAccess.Set(ps,"isInPossesionRange",range!=null);
                GameAccess.Set(ps,"currentPossesion",inside); GameAccess.Set(ps,"currentPossesionScript",residence);
                if(inside) foreach(var npc in (IEnumerable)GameAccess.Get(inside,"ais")) {npcFlags.Add(Tuple.Create(npc,GameAccess.Get(npc,"isOnPossesion")));GameAccess.Set(npc,"isOnPossesion",true);}
                ps.GetType().GetMethod("AlertNPCs_DistanceBased").Invoke(ps,new object[] {1f});
                if(residence!=null && GameAccess.Bool(residence,"hasSoundSecurity")) {
                    var ais=GameAccess.Get(residence,"AIs") as Array;
                    if(ais!=null && ais.Length>0) {
                        GameAccess.Call(GameAccess.Get(ps,"PoliceManager"),"Update_Last_Location");
                        ps.GetType().GetMethod("StartPoliceChase").Invoke(ps,new object[] {1,ais.GetValue(0)});
                        var warning=GameAccess.Get(ps,"warningSpawner");
                        warning.GetType().InvokeMember("SpawnWarning",BindingFlags.Instance|BindingFlags.Public|BindingFlags.InvokeMethod|BindingFlags.OptionalParamBinding,null,warning,new object[] {"sound_detected"});
                    }
                }
            } finally {
                soundActor=null;foreach(var flag in npcFlags) GameAccess.Set(flag.Item1,"isOnPossesion",flag.Item2);
                GameAccess.Set(ps,"isOnPossesion",on); GameAccess.Set(ps,"isInPossesionRange",near);
                GameAccess.Set(ps,"currentPossesion",originalTrigger); GameAccess.Set(ps,"currentPossesionScript",originalResidence);
            }
        }
        private bool ClientLockAction(Component lp,string method)
        {
            var door=DoorFor(lp);
            if(!network.TransactionsAllowed || !door) return false;
            if(method=="DoorUnlock" && breachDoor==door) { SendAction(door,InteractionKind.BreachComplete); return false; }
            if(method=="DoorUnlockKey") SendAction(door,InteractionKind.UnlockKey);
            return false;
        }
        private static bool BreachIntent(Component __instance,GameObject __0,RaycastHit __1)
        {
            var c=current; if(c==null || NetworkApplyScope.Active || c.nativeBreach) return true;
            if(!c.Client) return c.network.TransactionsAllowed;
            if(!c.network.TransactionsAllowed || !__0 || c.breachDoor) return false;
            var lp=__0.GetComponent(Type.GetType("Lockpick, Assembly-CSharp",true)); var door=c.DoorFor(lp);
            if(!door || !GameAccess.Bool(__instance,"currentCrowbar") || (int)GameAccess.Get(__instance,"crowbarLife")<=0) return false;
            c.breachObject=__0; c.breachHit=__1; c.breachDoor=door; c.breachExpires=c.network.Now+5;
            if(!c.SendAction(door,InteractionKind.BreachBegin)) {c.breachObject=null; c.breachDoor=null;}
            return false;
        }
        private static bool BellIntent(Component __instance)
        {
            var c=current; if(c==null || NetworkApplyScope.Active || !c.bells.ContainsKey(__instance)) return true;
            if(!c.network.TransactionsAllowed) return false;
            if(!c.Client) return true;
            c.SendAction(__instance,InteractionKind.Ring); return false;
        }
        private static void BellActivated(Component __instance)
        {
            var c=current; if(c==null || c.Client || NetworkApplyScope.Active || !c.bells.TryGetValue(__instance,out var bell)) return;
            bell.Activated(); c.network.World.Registry.CommitState(c.references[bell]);
        }
        private static void DoorChanged(Component __instance)
        {
            var c=current; if(c==null || !c.adapters.ContainsKey(__instance)) return;
            if(!c.Client) { c.dirty.Add(__instance); return; }
            if(c.localDriverCloseIntents.Remove(__instance) && !GameAccess.Bool(__instance,"isOpen") && c.adapters.TryGetValue(__instance,out var binding) && binding is DoorNetworkAdapter door) {
                door.Presentation.SuppressNextCloseAudio();
                if(c.SendAction(__instance,InteractionKind.Close)) c.localDriverCloseRequests[c.network.LastInteractionRequestId]=door;
                else door.Presentation.ClearCloseAudioSuppression();
            }
        }
        private static void LockChanged(Component __instance)
        {
            var c=current; if(c==null || c.Client) return;
            foreach(Component door in (Array)GameAccess.Get(__instance,"unlockDoor")) if(door && c.adapters.ContainsKey(door)) c.dirty.Add(door);
        }
        private static bool DoorUpdate(Component __instance,out NativeDoorScope __state)
        {
            __state=null; var c=current; if(c==null || !c.adapters.TryGetValue(__instance,out var binding)) return true;
            var presentation=binding is DoorNetworkAdapter d ? d.Presentation : binding is WindowNetworkAdapter w ? w.Presentation : null;
            if(presentation==null) return true;
            if(c.Client) {
                presentation.Tick();
                if(c.slowIntent==__instance) {
                    var ps=c.playerLocator.Find(); var image=ps ? GameAccess.Get(ps,"SlowOpen") : null;
                    if(image!=null) image.GetType().GetProperty("fillAmount").SetValue(image,presentation.Motion.Progress+.2f,null);
                }
                return false;
            }
            if(GameAccess.Bool(__instance,"opening") || GameAccess.Bool(__instance,"load_slow")) c.dirty.Add(__instance);
            if(c.remoteSlow.ContainsKey(__instance)) __state=new NativeDoorScope(__instance);
            return true;
        }
        private static void DoorUpdated(Component __instance,NativeDoorScope __state)
        {
            __state?.Dispose(); var c=current; if(c==null || c.Client) return;
            if(c.adapters.ContainsKey(__instance)) {
                var tag=__instance.tag;
                if(!c.nativeTags.TryGetValue(__instance,out var previous) || tag!=previous) {c.nativeTags[__instance]=tag;c.dirty.Add(__instance);}
            }
            if(c.remoteSlow.TryGetValue(__instance,out var began) && (!GameAccess.Bool(__instance,"opening") && !GameAccess.Bool(__instance,"load_slow") || c.network.Now-began>5)) c.remoteSlow.Remove(__instance);
        }
        private static Exception DoorUpdateFinished(Exception __exception,NativeDoorScope __state) {__state?.Dispose();return __exception;}
        // Native remote-door timers may write their cached local PS/UI. Restore those writes synchronously.
        private sealed class NativeDoorScope : IDisposable
        {
            private readonly object motor,look,image,move,sensitivity,fill;
            private bool disposed;
            internal NativeDoorScope(Component door) { var ps=GameAccess.Get(door,"PS"); motor=GameAccess.Get(ps,"PMB");look=GameAccess.Get(ps,"PCB");image=GameAccess.Get(ps,"SlowOpen");move=GameAccess.Get(motor,"canMove");sensitivity=GameAccess.Get(look,"mouseSensitivity");fill=image.GetType().GetProperty("fillAmount").GetValue(image,null); }
            public void Dispose() { if(disposed) return;disposed=true;GameAccess.Set(motor,"canMove",move);GameAccess.Set(look,"mouseSensitivity",sensitivity);image.GetType().GetProperty("fillAmount").SetValue(image,fill,null); }
        }
    }
}
