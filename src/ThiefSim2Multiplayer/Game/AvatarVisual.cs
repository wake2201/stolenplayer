using System;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class AvatarVisual : IDisposable
    {
        internal GameObject Root;
        internal AvatarRig Rig;
        internal AvatarAnimationController Animation;
        internal SkinnedMeshRenderer[] Skins;
        internal float FeetOffset;
        internal static void Validate(GameObject root)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (!c || !(c is Transform || c is SkinnedMeshRenderer || c is Animation || c is MeshFilter || c is MeshRenderer))
                    throw new InvalidOperationException("Unsafe presentation component: " + (c ? c.GetType().FullName : "missing script"));
                if (c.gameObject.tag != "Untagged" || c.gameObject.layer != 0) throw new InvalidOperationException("Unexpected presentation tag/layer: " + c.name);
            }
        }
        public void Dispose()
        {
            Animation?.Dispose(); Animation = null;
            if (Root) { Root.SetActive(false); Root.transform.SetParent(null, false); UnityEngine.Object.Destroy(Root); Root = null; }
        }
    }
}
