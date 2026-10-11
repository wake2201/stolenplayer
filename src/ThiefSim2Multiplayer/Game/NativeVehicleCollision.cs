using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class SharedVehicleController
    {
        private static bool impactReplay;
        private sealed class ImpactObservation { internal VehicleImpact Packet; }
        private static readonly Collider[] impactCandidates=new Collider[64];
        private ulong ImpactIdentity(Collider collider)
        {
            var siblings=collider.GetComponents<Collider>(); int ordinal=Array.IndexOf(siblings,collider);
            var vehicle=collider.GetComponentInParent(Type.GetType("CarController, Assembly-CSharp",true)) as Component;
            var path=new Stack<string>();
            for(var node=collider.transform;node;node=node.parent) {
                if(vehicle && node==vehicle.transform) {try {path.Push(VehiclePresentation.SemanticSlot(vehicle)); break;} catch(ArgumentException) {vehicle=null;}}
                // Native driver reparenting changes unrelated ROOT sibling indices.
                // Contact-local uniqueness and collider position are checked on Host.
                path.Push(Uri.EscapeDataString(node.name)+(node.parent ? "["+node.GetSiblingIndex().ToString(System.Globalization.CultureInfo.InvariantCulture)+"]" : ""));
            }
            return UnityNetworkEntityAdapter.Describe(collider,network.BuildFingerprintForDiagnostics,"native-vehicle-contact-v1",ordinal,"contact:"+string.Join("/",path.ToArray())).DeriveId().Value;
        }
        private static void CollisionObserved(Component __instance,Collision __0,out ImpactObservation __state)
        {
            var c=Current; __state=null;
            if(c==null || !c.Cooperative || c.network.Session.IsHost || impactReplay || !c.cars.TryGetValue(__instance,out var a) || a.State.Driver!=c.network.Session.LocalPlayerId) return;
            // Observe eligibility BEFORE vanilla consumes its 200-ms impact gate.
            bool eligible=GameAccess.Bool(__instance,"is_sitting_inside") && (__0.transform.CompareTag("PoliceCarCrash") && GameAccess.Bool(__instance,"engine_running") || __0.relativeVelocity.magnitude>=7 && GameAccess.Bool(__instance,"canImpact"));
            if(!eligible || __0.contactCount==0) return;
            try {
                var point=__0.GetContact(0).point; var velocity=__0.relativeVelocity;
                // Capture before native NPC/ragdoll callbacks can reparent the target.
                __state=new ImpactObservation {Packet=new VehicleImpact {Vehicle=a.Entity,Target=c.ImpactIdentity(__0.collider),X=point.x,Y=point.y,Z=point.z,Vx=velocity.x,Vy=velocity.y,Vz=velocity.z}};
            } catch(ArgumentException error) {c.log("[TS2MP] Native collision target unavailable: "+error.Message);}
        }
        private static void CollisionCompleted(Component __instance,ImpactObservation __state)
        {
            var c=Current; if(__state==null || c==null || !c.cars.TryGetValue(__instance,out var a) || !a.Operational) return;
            c.network.SendVehicleMotion(a.Motion()); c.network.SendVehicleImpact(__state.Packet);
        }
        private bool ReplayImpact(VehicleImpact q)
        {
            var a=network.World.Registry.GetBinding(q.Vehicle) as VehicleNetworkAdapter;
            if(a==null || !a.Operational || a.Simulates) return false;
            var point=new Vector3(q.X,q.Y,q.Z); var velocity=new Vector3(q.Vx,q.Vy,q.Vz);
            float speed=Math.Max(new Vector3(a.State.Vx,a.State.Vy,a.State.Vz).magnitude,network.Vehicles.RecentImpactSpeed(q.Vehicle));
            float reach=6+speed*.35f;
            if((point-new Vector3(a.State.X,a.State.Y,a.State.Z)).sqrMagnitude>reach*reach) return false;
            Collider target=null; bool ambiguous=false;
            int count=Physics.OverlapSphereNonAlloc(point,1.5f,impactCandidates,~0,QueryTriggerInteraction.Ignore);
            try {
                if(count==impactCandidates.Length) return false;
                for(int i=0;i<count;i++) {
                    var candidate=impactCandidates[i]; if(!candidate || !candidate.enabled || !candidate.gameObject.activeInHierarchy || candidate.transform.IsChildOf(a.Car.transform)) continue;
                    try {if(ImpactIdentity(candidate)==q.Target) {if(target) ambiguous=true;else target=candidate;}} catch(ArgumentException) { }
                }
            } finally {Array.Clear(impactCandidates,0,impactCandidates.Length);}
            if(!target || ambiguous || velocity.magnitude>30+speed+(target.attachedRigidbody ? target.attachedRigidbody.velocity.magnitude : 0)) return false;
            var nativeTarget=target.attachedRigidbody ? target.attachedRigidbody.transform : target.transform;
            if(!nativeTarget.CompareTag("PoliceCarCrash") && (velocity.magnitude<7 || !GameAccess.Bool(a.Car,"canImpact"))) return false;
            bool seated=GameAccess.Bool(a.Car,"is_sitting_inside");
            try {
                // Run the VERIFIED original consequence method, including its own
                // cooldown, repair price, shared money debit and native NPC callbacks.
                // Collision/ContactPoint field contracts were inspected in the installed
                // Unity PhysicsModule. No forces, contacts or solver step are fabricated.
                var collision=new Collision(); object contact=new ContactPoint();
                GameAccess.Set(contact,"m_Point",point);
                GameAccess.Set(collision,"m_RelativeVelocity",velocity); GameAccess.Set(collision,"m_Body",target.attachedRigidbody);
                GameAccess.Set(collision,"m_Collider",target); GameAccess.Set(collision,"m_ContactCount",1); GameAccess.Set(collision,"m_ReusedContacts",new[] {(ContactPoint)contact});
                impactReplay=true;
                GameAccess.Set(a.Car,"is_sitting_inside",true);
                AccessTools.Method(a.Car.GetType(),"OnCollisionEnter").Invoke(a.Car,new object[] {collision});
                return true;
            } catch(Exception error) {log("[TS2MP] Native collision consequence unavailable: "+error.GetType().Name); return false;}
            finally {GameAccess.Set(a.Car,"is_sitting_inside",seated); impactReplay=false;}
        }
        // Police collision tazing already ran on the genuine Client driver. Its
        // Host world replay must not taze the Host's passenger/on-foot player.
        private static bool NativeImpactTaze() => !impactReplay;
    }
}
