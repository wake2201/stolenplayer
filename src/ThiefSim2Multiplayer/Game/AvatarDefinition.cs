using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class AvatarDefinition
    {
        internal readonly byte Id;
        internal readonly string SourceKey;
        internal readonly string SourceGroup;
        internal readonly int SkinCount;
        // Male calibration is runtime accepted. Female equipment stays hidden until
        // that rig and its own socket offsets pass visual acceptance.
        internal bool CrowbarCalibrated { get { return Id == 0; } }
        internal readonly Vector3 CrowbarPosition = new Vector3(0.025f, -0.035f, 0.015f);
        internal readonly Vector3 CrowbarRotation = new Vector3(90, 0, 0);
        internal readonly Vector3 CrowbarScale = Vector3.one;
        private AvatarDefinition(byte id, string sourceKey, string group, int skins) { Id = id; SourceKey = sourceKey; SourceGroup = group; SkinCount = skins; }
        internal static AvatarDefinition Find(byte id)
        {
            if (id == 0) return new AvatarDefinition(0, "NPC_Man105", "105", 7);
            if (id == 1) return new AvatarDefinition(1, "NPC_Woman112", "112", 8);
            return null;
        }
    }
}
