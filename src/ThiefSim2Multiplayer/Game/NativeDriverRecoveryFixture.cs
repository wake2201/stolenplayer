using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class SharedVehicleController
    {
        internal IEnumerator NativeDriverRecoveryFixture()
        {
            if(Online || fixture) throw new InvalidOperationException("Recovery fixture requires offline backed-up gameplay.");
            var p=locator.Find(); if(!p || GameAccess.Bool(p,"is_in_vehicle")) throw new InvalidOperationException("Original on-foot actor required.");
            var car=UnityEngine.Object.FindObjectsOfType(Type.GetType("CarController, Assembly-CSharp",true)).OfType<Component>().First(c=>c.gameObject.activeInHierarchy && GameAccess.Bool(c,"is_player_car"));
            var authored=GameAccess.Get(car,"sit_transform") as Transform;
            var origin=p.transform.position; var rotation=p.transform.rotation;
            var camera=GameAccess.Get(p,"MainCam") as Camera; var inventory=GameAccess.Get(p,"invScript");
            var cameraIds=p.GetComponentsInChildren<Camera>(true).Select(c=>c.GetInstanceID()).ToArray();
            var listenerIds=p.GetComponentsInChildren<AudioListener>(true).Select(c=>c.GetInstanceID()).ToArray();
            var input=car.GetComponent(Type.GetType("EVP.VehicleStandardInput, Assembly-CSharp",true)) as Behaviour; bool inputOn=input.enabled;
            var body=car.GetComponent<Rigidbody>(); bool kinematic=body.isKinematic;
            var recovery=new LocalDriverState(); Transform temporary=null; int checks=0;
            void Check(bool ok,string label) { if(!ok) throw new InvalidOperationException("Driver recovery FAIL: "+label); checks++; log("[TS2MP] NATIVE DRIVER RECOVERY PASS: "+label); }
            fixture=true;
            try {
                foreach(bool destroySeat in new[] {false,true}) {
                    temporary=new GameObject("TS2MP_RecoverySeatFixture").transform; temporary.SetParent(car.transform,false); temporary.SetPositionAndRotation(authored.position,authored.rotation);
                    GameAccess.Set(car,"sit_transform",temporary);
                    var a=new VehicleNetworkAdapter(car); input.enabled=false; body.isKinematic=true;
                    recovery.Capture(p,a); p.transform.position=temporary.position;
                    GameAccess.Call(car,"EnterVehicle"); car.GetType().GetMethod("RunEngine").Invoke(car,new object[] {true});
                    double end=network.Now+3;
                    while(!p.transform.IsChildOf(car.transform) && !GameAccess.Bool(p,"current_vehicle_running")) { if(network.Now>end) throw new InvalidOperationException("Native engine/seat attachment timeout."); yield return null; }
                    recovery.Tick(network.Now,log); yield return new WaitForSecondsRealtime(.3f);
                    p.GetType().GetMethod("DisableTownSelection").Invoke(p,new object[] {true}); recovery.Tick(network.Now,log);
                    body.isKinematic=true;
                    Check(GameAccess.Bool(p,"is_in_vehicle") && !p.GetComponent<CharacterController>().enabled,"native driver enters before recovery case "+destroySeat);
                    Check(!p.transform.IsChildOf(car.transform),"original driver lifetime independent of vehicle case "+destroySeat);
                    if(destroySeat) {
                        UnityEngine.Object.Destroy(temporary.gameObject); yield return null;
                        Check(recovery.Tick(network.Now,log),"destroyed verified seat detected and original actor recovered");
                    } else {
                        // Controlled stalled-exit state; no speculative obstruction geometry.
                        recovery.BeginExit(network.Now,origin); recovery.Tick(network.Now+3.1,log);
                        Check(!recovery.Active,"bounded stalled native exit restores snapshot");
                    }
                    Check(p && !GameAccess.Bool(p,"is_in_vehicle") && p.GetComponent<CharacterController>().enabled,"original first-person controller survives case "+destroySeat);
                    Check(GameAccess.Get(p,"MainCam") as Camera==camera && camera.enabled && GameAccess.Get(p,"invScript")==inventory,"original inventory and enabled camera survive case "+destroySeat);
                    Check(cameraIds.SequenceEqual(p.GetComponentsInChildren<Camera>(true).Select(c=>c.GetInstanceID())) && listenerIds.SequenceEqual(p.GetComponentsInChildren<AudioListener>(true).Select(c=>c.GetInstanceID())),"all original camera/listener identities survive case "+destroySeat);
                    var alarm=GameAccess.Get(car,"alarm_script") as Component;
                    Check(!alarm || alarm.GetComponents<Rigidbody>().Length==1 && alarm.GetComponent<Rigidbody>().isKinematic,"cancelled native exit restores exactly one alarm body case "+destroySeat);
                    GameAccess.Set(car,"sit_transform",authored); if(temporary) UnityEngine.Object.Destroy(temporary.gameObject); temporary=null;
                    yield return null;
                }
                log("[TS2MP] NATIVE DRIVER RECOVERY COMPLETE checks="+checks+"; controlled native seat loss and timeout; destroyed camera/whole-car reinitialization deferred");
            } finally {
                recovery.Restore(origin,log); GameAccess.Set(car,"sit_transform",authored); if(temporary) UnityEngine.Object.Destroy(temporary.gameObject);
                fixture=false; input.enabled=inputOn; body.isKinematic=kinematic;
                if(p) p.transform.SetPositionAndRotation(origin,rotation);
            }
        }
    }
}
