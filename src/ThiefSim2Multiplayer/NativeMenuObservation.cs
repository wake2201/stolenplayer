using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;
using ThiefSim2Multiplayer.Game;

namespace ThiefSim2Multiplayer
{
    public sealed partial class Plugin
    {
        private IEnumerator NativeMenuObservation()
        {
            if(network.Session.State!=SessionState.Offline) throw new InvalidOperationException("Backed-up offline native setup required.");
            var actor=new PlayerLocator().Find();if(!actor || GameAccess.Bool(actor,"is_in_vehicle")) throw new InvalidOperationException("Native on-foot actor required.");
            var inventory=GameAccess.Get(actor,"invScript"); var camera=GameAccess.Get(actor,"MainCam") as Camera;
            if(GameAccess.Bool(inventory,"isEnabled") || Time.timeScale<=0) throw new InvalidOperationException("Close native UI before observation.");
            NetworkManager peer=null;bool opened=false;
            string CameraState()=>"camera="+camera.GetInstanceID()+" enabled="+camera.enabled+" active="+camera.gameObject.activeInHierarchy+" clear="+camera.clearFlags+" mask="+camera.cullingMask+" clip="+camera.nearClipPlane+" target="+(camera.targetTexture ? camera.targetTexture.GetInstanceID() : 0);
            var before=CameraState();int changed=0;
            try {
                peer=new NetworkManager(network.BuildFingerprintForDiagnostics,Trace);
                peer.World.PrepareBaseline=s=>MirrorFixture(peer.World.Registry,s.Entities);peer.World.AdapterFactory=type=>new FixtureRecord(type,new byte[0]);
                peer.HostSceneRequested+=s=>peer.MarkSceneLoaded(s.SceneKey);
                if(!network.Host(FixturePort(),"MenuObservationHost")) throw new InvalidOperationException("Host unavailable.");
                yield return null;yield return null;peer.Join("127.0.0.1",network.Port,"ManagedMenuPeer");
                double deadline=network.Now+25;
                while(!network.GameplayReady || !peer.GameplayReady) {peer.Poll();if(network.Now>deadline) throw new InvalidOperationException("Menu observation readiness timeout.");yield return null;}
                Trace("[TS2MP] MENU OBSERVATION before "+before);network.FrameProfile.Reset();
                foreach(string tab in new[] {"SelectInv","SelectGame"}) {
                    GameAccess.Call(actor,"EnableInventory");opened=true;GameAccess.Call(inventory,tab);
                    for(int frame=0;frame<12;frame++) {
                        peer.Poll();yield return new WaitForEndOfFrame();
                        var state=CameraState();if(state!=before) {changed++;Trace("[TS2MP] MENU OBSERVATION tab="+tab+" frame="+frame+" "+state);}
                    }
                    GameAccess.Call(actor,"DisableInventory");opened=false;yield return new WaitForSecondsRealtime(.3f);
                }
                int bells=0,clips=0;
                foreach(Component bell in UnityEngine.Object.FindObjectsOfType(Type.GetType("Doorbell, Assembly-CSharp",true))) {bells++;var source=bell.GetComponent<AudioSource>();if(source && source.clip) clips++;}
                var definitions=new PlayerInventoryAdapter();definitions.Refresh();
                Trace("[TS2MP] MENU OBSERVATION COMPLETE cameraChanges="+changed+" restored="+(CameraState()==before)+" nativeBellSources="+bells+" clips="+clips+" personalKeyDefinitions="+definitions.Definitions.Values.Count(d=>GameAccess.Bool(d,"isKey"))+"; rendered black-flash and two-human acceptance remain unproven");
                Trace(network.FrameProfile.Summary());
            } finally {if(opened && actor) GameAccess.Call(actor,"DisableInventory");peer?.Dispose();network.Disconnect("Bounded menu observation complete.");}
        }
    }
}
