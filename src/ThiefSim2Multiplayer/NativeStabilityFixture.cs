using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Game;

namespace ThiefSim2Multiplayer
{
    public sealed partial class Plugin
    {
        private static bool stabilityFixtureActive;
        private static object stabilityFixtureInput;
        private readonly PlayerLocator stabilityFixtureLocator=new PlayerLocator();
        private static bool NeutralFixtureAxis(object __instance,ref float __result) { if(!stabilityFixtureActive || seatFixtureInput!=null || !ReferenceEquals(__instance,stabilityFixtureInput)) return true; __result=0; return false; }
        private static bool NeutralFixtureButton(object __instance,ref bool __result) { if(!stabilityFixtureActive || seatFixtureInput!=null || !ReferenceEquals(__instance,stabilityFixtureInput)) return true; __result=false; return false; }
        private static bool NeutralFixturePlayer(Component __instance) => !stabilityFixtureActive || GameAccess.Bool(__instance,"is_in_vehicle") || !ReferenceEquals(GameAccess.Get(__instance,"rewiredPlayer"),stabilityFixtureInput);
        private void RefreshNeutralFixtureInput()
        {
            if(!stabilityFixtureActive) return;
            var actor=stabilityFixtureLocator.Find(); stabilityFixtureInput=actor ? GameAccess.Get(actor,"rewiredPlayer") : null;
        }
        public string DevelopmentStabilityCycle(string directory,bool missionDriveOnly=false)
        {
            if(settings==null || !settings.DevelopmentControls.Value || !Directory.Exists(directory) || stabilityFixtureActive) throw new InvalidOperationException("Owned stability fixture unavailable.");
            StartCoroutine(GuardedRoutine.Run(NativeStabilityFixture(directory,missionDriveOnly),e=>Trace("[TS2MP] NATIVE STABILITY failed after cleanup: "+e)));
            return "Bounded native rear-seat/mission/continuity sequence started; managed peers and neutral physical input, not human acceptance.";
        }
        public string DevelopmentVehicleVariantCycle(string directory)
        {
            if(settings==null || !settings.DevelopmentControls.Value || !Directory.Exists(directory) || stabilityFixtureActive) throw new InvalidOperationException("Owned variant fixture unavailable.");
            StartCoroutine(GuardedRoutine.Run(NativeStabilityFixture(directory,true,1),e=>Trace("[TS2MP] NATIVE VARIANT failed after cleanup: "+e)));
            return "Native second-vehicle initialization fixture started through authoritative descriptor; saved choice unchanged.";
        }
        private IEnumerator NativeStabilityFixture(string directory,bool missionDriveOnly,byte vehicleVariant=0)
        {
            if(network.Session.State!=Core.SessionState.Offline) throw new InvalidOperationException("Stability fixture requires offline backed-up gameplay.");
            var actor=new PlayerLocator().Find(); if(!actor || GameAccess.Bool(actor,"is_in_vehicle")) throw new InvalidOperationException("Original on-foot actor required.");
            var patch=new Harmony(Id+".stability-neutral");
            try {
                stabilityFixtureActive=true; RefreshNeutralFixtureInput();
                var input=stabilityFixtureInput.GetType();
                foreach(string name in new[] {"GetAxis","GetAxisRaw"}) patch.Patch(input.GetMethod(name,new[] {typeof(string)}),prefix:new HarmonyMethod(typeof(Plugin),nameof(NeutralFixtureAxis)));
                foreach(string name in new[] {"GetButton","GetButtonDown","GetButtonUp"}) patch.Patch(input.GetMethod(name,new[] {typeof(string)}),prefix:new HarmonyMethod(typeof(Plugin),nameof(NeutralFixtureButton)));
                patch.Patch(AccessTools.Method(actor.GetType(),"Update"),prefix:new HarmonyMethod(typeof(Plugin),nameof(NeutralFixturePlayer)));
                if(GameAccess.Bool(GameAccess.Get(actor,"invScript"),"isEnabled") || (GameAccess.Get(actor,"PauseMenuObj") as GameObject)?.activeInHierarchy==true) GameAccess.Call(actor,"DisableInventory");
                actor.GetType().GetMethod("DisableTownSelection").Invoke(actor,new object[] {false}); yield return new WaitForSecondsRealtime(.6f);
                if(missionDriveOnly) {
                    yield return NativeMissionPreparationFixture(vehicleVariant);
                    if(vehicleVariant==1) {
                        VehicleGeometryAudit(); yield return vehicles.NativePassengerFixture(camera=>CaptureNativePassengerPreview(camera,directory,"van-passenger-native-preview.png"));
                        yield return NativeSeatInteractionFixture();
                    }
                    yield return vehicles.NativeDrivingFixture();
                    Trace("[TS2MP] NATIVE VEHICLE VARIANT COMPLETE variant="+vehicleVariant);
                    Trace("[TS2MP] NATIVE MISSION DRIVE COMPLETE; native EVP with managed driver/passenger transport, real two-machine gate deferred"); yield break;
                }
                VehicleGeometryAudit();
                yield return vehicles.NativePassengerFixture(camera=>CaptureNativePassengerPreview(camera,directory,"passenger-native-preview.png"));
                network.FrameProfile.Reset();
                Trace("[TS2MP] NATIVE STABILITY phase=initial-world-lifecycle"); yield return NativeSeatInteractionFixture(); Trace(network.FrameProfile.Summary());
                Trace("[TS2MP] NATIVE STABILITY phase=mission-preparation"); yield return NativeMissionPreparationFixture();
                Trace("[TS2MP] NATIVE STABILITY phase=mission-lifecycle"); BaselineCostAudit(); yield return NativeSeatInteractionFixture(); Trace(network.FrameProfile.Summary());
                Trace("[TS2MP] NATIVE STABILITY phase=mission-sleep-return-client-exit-reconnect"); yield return NativeContinuityFixture(); Trace(network.FrameProfile.Summary());
                Trace("[TS2MP] NATIVE STABILITY phase=native-EVP-driving"); yield return vehicles.NativeDrivingFixture();
                Trace("[TS2MP] NATIVE STABILITY COMPLETE; one native process, managed peers, controlled boundaries; real two-machine acceptance deferred");
            } finally {stabilityFixtureActive=false; stabilityFixtureInput=null; patch.UnpatchSelf();}
        }
        private void CaptureNativePassengerPreview(Camera camera,string directory,string filename)
        {
            var target=new RenderTexture(640,360,24); var image=new Texture2D(640,360,TextureFormat.RGB24,false); var original=camera.targetTexture; var active=RenderTexture.active;
            try {camera.targetTexture=target; camera.Render(); RenderTexture.active=target; image.ReadPixels(new Rect(0,0,640,360),0,0); image.Apply(); File.WriteAllBytes(Path.Combine(directory,filename),image.EncodeToPNG());}
            finally {camera.targetTexture=original; RenderTexture.active=active; target.Release(); Destroy(target); Destroy(image);}
        }
        private void BaselineCostAudit()
        {
            var r=network.World.Registry;
            Trace("[TS2MP] BASELINE DETAIL validationMs="+r.BaselineValidationMilliseconds.ToString("F4",System.Globalization.CultureInfo.InvariantCulture));
            for(int i=0;i<r.BaselineAdapterCounts.Length;i++) if(r.BaselineAdapterCounts[i]>0)
                Trace("[TS2MP] BASELINE DETAIL type="+(Core.World.EntityTypeId)i+" count="+r.BaselineAdapterCounts[i]+" applyMs="+r.BaselineAdapterMilliseconds[i].ToString("F4",System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
