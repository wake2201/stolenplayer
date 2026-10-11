using System;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class PlayerAvatarController : IDisposable
    {
        internal readonly AvatarVisual Visual;
        internal readonly AvatarEquipmentController Equipment;
        private readonly Action<string> log;
        private Core.LocomotionState? previous;
        internal PlayerAvatarController(AvatarVisual visual, AvatarDefinition definition, GameObject crowbar, Action<string> log)
        { Visual = visual; Equipment = new AvatarEquipmentController(definition, visual.Rig, crowbar, log); this.log = log; }
        internal void Apply(PlayerTransform pose)
        {
            Visual.Animation.SetState(pose.State.Locomotion);
            Visual.Rig.RemoveWorldMotion();
            // Local camera pitch sampling is supported; remote bone axes require a separate visual acceptance.
            Equipment.Apply(pose.State);
            if (previous != pose.State.Locomotion) { previous = pose.State.Locomotion; log("[TS2MP] Avatar locomotion=" + previous + "; clip=" + Visual.Animation.Current); }
        }
        public void Dispose() { Equipment.Dispose(); Visual.Dispose(); }
    }
}
