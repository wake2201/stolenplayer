using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class AvatarRig
    {
        internal readonly Transform Hips, Head, RightHand, LeftHand, LeftFoot, RightFoot;
        private readonly Transform[] motionRoots;
        private readonly Vector3[] positions;
        private readonly Quaternion[] rotations;
        private readonly Vector3 hipsPosition;
        internal AvatarRig(Transform source, Animation animation, Dictionary<Transform, Transform> map)
        {
            foreach (var pair in map)
            {
                switch (pair.Key.name)
                {
                    case "mixamorig:Hips": Hips = pair.Value; break;
                    case "mixamorig:Head": Head = pair.Value; break;
                    case "mixamorig:RightHand": RightHand = pair.Value; break;
                    case "mixamorig:LeftHand": LeftHand = pair.Value; break;
                    case "mixamorig:LeftFoot": LeftFoot = pair.Value; break;
                    case "mixamorig:RightFoot": RightFoot = pair.Value; break;
                }
            }
            if (!Hips || !Head || !RightHand || !LeftHand || !LeftFoot || !RightFoot) throw new InvalidOperationException("Required Mixamorig bones missing.");
            var roots = new List<Transform>();
            for (Transform t = Hips.parent; t && t != source.parent; t = t.parent) roots.Add(t);
            motionRoots = roots.ToArray(); positions = new Vector3[roots.Count]; rotations = new Quaternion[roots.Count];
            for (int i = 0; i < roots.Count; i++) { positions[i] = roots[i].localPosition; rotations[i] = roots[i].localRotation; }
            hipsPosition = Hips.localPosition;
        }
        // Legacy clips may key ancestor transforms and hips translation. Presentation never integrates root motion.
        internal void RemoveWorldMotion()
        {
            for (int i = 0; i < motionRoots.Length; i++) { motionRoots[i].localPosition = positions[i]; motionRoots[i].localRotation = rotations[i]; }
            Vector3 p = Hips.localPosition; p.x = hipsPosition.x; p.z = hipsPosition.z; Hips.localPosition = p;
        }
    }
}
