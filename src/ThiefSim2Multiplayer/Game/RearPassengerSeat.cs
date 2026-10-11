using System;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    // Local geometry contracts from the two verified native interior meshes.
    // A reference frame only: no collider, camera, controller or native storage mutation.
    internal static class RearPassengerSeat
    {
        internal const string AnchorName="TS2MP_RearCenterPassengerSeat";
        internal const string EyeName="TS2MP_RearPassengerEyes";
        internal static Transform Resolve(Component car)
        {
            if(!car) return null;
            var existing=car.transform.Find(AnchorName); if(existing) return existing;
            var driver=GameAccess.Get(car,"sit_transform") as Transform;
            if(!driver || !driver.IsChildOf(car.transform)) return null;
            bool first=VehiclePresentation.SemanticSlot(car)=="VehiclesManager.firstCar";
            string meshName=first ? "Car04_Interior_LOD0" : "Car05_Interior_LOD0";
            bool verified=false;
            foreach(var mesh in car.GetComponentsInChildren<MeshFilter>(true)) if(mesh.sharedMesh && mesh.sharedMesh.name==meshName) { verified=true; break; }
            if(!verified) return null;
            var native=car.transform.InverseTransformPoint(driver.position);
            // Local mesh vertical intersections at the centered hips position:
            // Car04 cushion Y=.15361 / roof Y=1.48279 at X=0,Z=-.497.
            // Car05 cushion Y=-.06259 / roof Y=.65199 at X=-.014,Z=-.85.
            // Transfer native front cushion clearance (Y=.191 / -.235). Offset
            // the actor reference by the measured driving_front hips X offsets
            // (-.0071 / -.0524); center the body, not just the camera.
            var position=first ? new Vector3(.0071f,native.y+.15361f-.191f,-.491f) : new Vector3(.0384f,native.y-.06259f+.235f,-.9546f);
            var seat=new GameObject(AnchorName).transform; seat.SetParent(car.transform,false); seat.localPosition=position;
            seat.localRotation=Quaternion.Inverse(car.transform.rotation)*driver.rotation;
            var eyes=new GameObject(EyeName).transform; eyes.SetParent(seat,false);
            // Preserve the previous head-to-eye clearance at the center cushion.
            eyes.position=car.transform.TransformPoint(first ? new Vector3(position.x,1.04761f,position.z) : new Vector3(position.x,.59941f,position.z));
            return seat;
        }
        internal static Transform Eyes(Transform seat) => seat ? seat.Find(EyeName) : null;
    }
}
