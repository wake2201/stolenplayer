using System;
using System.Linq;
using System.IO;
using System.Globalization;
using UnityEngine;
using ThiefSim2Multiplayer.Game;

namespace ThiefSim2Multiplayer
{
    public sealed partial class Plugin
    {
        private void VehicleStorageAudit()
        {
            var p=new PlayerLocator().Find(); if(!p) throw new InvalidOperationException("Initialized native player required.");
            var inv=GameAccess.Get(p,"invScript"); var vm=GameAccess.Get(GameAccess.Get(p,"GM"),"VM");
            Trace("[TS2MP] STORAGE NATIVE capacity="+GameAccess.Get(inv,"max_storage_capacity")+" used="+GameAccess.Get(inv,"current_storage_capacity")+" locker="+GameAccess.Get(inv,"max_locker_capacity")+" motor="+((Behaviour)GameAccess.Get(p,"PMB")).enabled+" look="+((Behaviour)GameAccess.Get(p,"PCB")).enabled+" canInteract="+GameAccess.Bool(p,"canInteract"));
            foreach(string slot in new[] {"firstCar","van"}) {
                var car=GameAccess.Get(vm,slot) as Component; var box=car ? GameAccess.Get(car,"car_box_script") as Component : null;
                if(!car || !box) {Trace("[TS2MP] STORAGE NATIVE slot="+slot+" unavailable"); continue;}
                var door=GameAccess.Get(car,"car_door_R") as Component; var driver=GameAccess.Get(car,"sit_transform") as Transform;
                var collider=box.GetComponent<Collider>();
                Trace("[TS2MP] STORAGE NATIVE slot="+slot+" model="+car.name+" active="+car.gameObject.activeInHierarchy+" type="+GameAccess.Get(box,"storage_type")+" tag="+box.tag+" boxLocal="+car.transform.InverseTransformPoint(box.transform.position).ToString("F4")+" collider="+(collider ? collider.GetType().Name : "missing")+" seated="+GameAccess.Bool(car,"is_sitting_inside")+" engine="+GameAccess.Bool(car,"engine_running")+" rightDoor="+(door && GameAccess.Bool(door,"isOpen"))+" driverToBox="+(driver && collider ? Vector3.Distance(driver.position,collider.bounds.center).ToString("F4") : "unknown"));
            }
        }
        private string VehicleGeometryAudit()
        {
            var p=new PlayerLocator().Find(); var gm=p ? GameAccess.Get(p,"GM") : null; var vm=gm!=null ? GameAccess.Get(gm,"VM") : null;
            if(vm==null) throw new InvalidOperationException("Initialized native vehicle manager required.");
            foreach(var car in new[] {GameAccess.Get(vm,"firstCar") as Component,GameAccess.Get(vm,"van") as Component}.Where(c=>c)) {
                Trace("[TS2MP] GEOMETRY vehicle="+car.name+" id="+car.GetInstanceID()+" scale="+car.transform.lossyScale.ToString("F4"));
                foreach(string key in new[] {"sit_transform","associateSitPos","leave_car_transform","tpp_character","car_box_script","main_camera","enter_car_object"}) {
                    var value=GameAccess.Get(car,key); var t=value is Component c ? c.transform : value is GameObject g ? g.transform : null;
                    if(t) Trace("[TS2MP] GEOMETRY field="+key+" name="+t.name+" id="+t.GetInstanceID()+" localToVehicle="+car.transform.InverseTransformPoint(t.position).ToString("F4")+" active="+t.gameObject.activeSelf);
                }
                foreach(var f in car.GetComponentsInChildren<MeshFilter>(true).OrderBy(f=>f.GetComponentsInParent<Transform>(true).Any(t=>t.name.IndexOf("seat",StringComparison.OrdinalIgnoreCase)>=0) ? 0 : 1).Take(96)) {
                    if(!f.sharedMesh) continue; var b=f.sharedMesh.bounds;
                    Trace("[TS2MP] GEOMETRY mesh="+f.name+" source="+f.sharedMesh.name+" vertices="+f.sharedMesh.vertexCount+" centre="+car.transform.InverseTransformPoint(f.transform.TransformPoint(b.center)).ToString("F4")+" min="+car.transform.InverseTransformPoint(f.transform.TransformPoint(b.min)).ToString("F4")+" max="+car.transform.InverseTransformPoint(f.transform.TransformPoint(b.max)).ToString("F4"));
                }
                foreach(var t in car.GetComponentsInChildren<Transform>(true).Where(t=>t.name.IndexOf("seat",StringComparison.OrdinalIgnoreCase)>=0 || t.name.IndexOf("sit",StringComparison.OrdinalIgnoreCase)>=0 || t.name.IndexOf("interior",StringComparison.OrdinalIgnoreCase)>=0).Take(32)) Trace("[TS2MP] GEOMETRY node="+t.name+" localToVehicle="+car.transform.InverseTransformPoint(t.position).ToString("F4"));
                var source=GameAccess.Get(car,"tpp_character") as GameObject; var seat=GameAccess.Get(car,"sit_transform") as Transform;
                if(source && seat) {
                    using(var visual=new PlayerAvatarFactory().CreateDriver(source.transform,null,seat)) {
                        visual.Root.transform.SetPositionAndRotation(source.transform.position,source.transform.rotation);
                        Trace("[TS2MP] GEOMETRY driving_front hips="+car.transform.InverseTransformPoint(visual.Rig.Hips.position).ToString("F4")+" head="+car.transform.InverseTransformPoint(visual.Rig.Head.position).ToString("F4")+" source="+car.transform.InverseTransformPoint(source.transform.position).ToString("F4"));
                    }
                }
            }
            return "Bounded native vehicle geometry recorded; no native object changed.";
        }
        public string DevelopmentVehicleGeometry(string directory)
        {
            if(settings==null || !settings.DevelopmentControls.Value || !Directory.Exists(directory)) throw new InvalidOperationException("Geometry output unavailable.");
            VehicleGeometryAudit();
            var p=new PlayerLocator().Find(); var vm=GameAccess.Get(GameAccess.Get(p,"GM"),"VM");
            foreach(var car in new[] {GameAccess.Get(vm,"firstCar") as Component,GameAccess.Get(vm,"van") as Component}.Where(c=>c)) {
                foreach(var f in car.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.sharedMesh && (f.sharedMesh.name=="Car04_Interior_LOD0" || f.sharedMesh.name=="Car05_Interior_LOD0" || f.GetComponentsInParent<Transform>(true).Any(t=>t.name.IndexOf("seat",StringComparison.OrdinalIgnoreCase)>=0))).Take(32)) {
                    if(!f.sharedMesh.isReadable || f.sharedMesh.vertexCount>20000) throw new InvalidOperationException("Verified interior mesh not readable/bounded.");
                    using(var output=new StreamWriter(Path.Combine(directory,f.sharedMesh.name+"-"+f.name+"-local.csv"),false)) {
                        output.WriteLine("x,y,z");
                        foreach(var vertex in f.sharedMesh.vertices) { var v=car.transform.InverseTransformPoint(f.transform.TransformPoint(vertex)); output.WriteLine(string.Format(CultureInfo.InvariantCulture,"{0:R},{1:R},{2:R}",v.x,v.y,v.z)); }
                    }
                    using(var output=new StreamWriter(Path.Combine(directory,f.sharedMesh.name+"-"+f.name+"-triangles.csv"),false)) {
                        output.WriteLine("a,b,c"); var triangles=f.sharedMesh.triangles;
                        for(int i=0;i<triangles.Length;i+=3) output.WriteLine(triangles[i]+","+triangles[i+1]+","+triangles[i+2]);
                    }
                }
                var camera=GameAccess.Get(p,"MainCam") as Camera;
                if(camera && camera.enabled) {
                    var position=camera.transform.position; var rotation=camera.transform.rotation; var original=camera.targetTexture; var active=RenderTexture.active; float near=camera.nearClipPlane;
                    var target=new RenderTexture(960,540,24); var texture=new Texture2D(960,540,TextureFormat.RGB24,false);
                    try {
                        camera.targetTexture=target; camera.nearClipPlane=.03f;
                        // Diagnostic candidates only, never used as gameplay anchors.
                        for(int view=0;view<3;view++) {
                            var candidate=car.name=="PLAYER CAR" ? new Vector3(.50f,.95f,-.45f) : new Vector3(.40f,.35f,-1.05f);
                            var direction=view==0 ? Vector3.forward : view==1 ? Vector3.down+Vector3.back*.5f : Vector3.right;
                            camera.transform.SetPositionAndRotation(car.transform.TransformPoint(candidate),Quaternion.LookRotation(car.transform.TransformDirection(direction),car.transform.up));
                            camera.Render(); RenderTexture.active=target; texture.ReadPixels(new Rect(0,0,960,540),0,0); texture.Apply(); File.WriteAllBytes(Path.Combine(directory,"cabin-"+car.name+"-"+view+".png"),texture.EncodeToPNG());
                        }
                    } finally { camera.transform.SetPositionAndRotation(position,rotation); camera.targetTexture=original; camera.nearClipPlane=near; RenderTexture.active=active; target.Release(); Destroy(target); Destroy(texture); }
                }
            }
            return "Bounded native interior geometry exported locally for inspection; never tracked.";
        }
    }
}
