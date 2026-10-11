using System;
using System.Collections.Generic;
using UnityEngine;
using ThiefSim2Multiplayer.Core;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class AvatarAnimationController : IDisposable
    {
        internal readonly Animation Animation;
        private readonly List<AnimationClip> clips = new List<AnimationClip>();
        private readonly bool driver;
        internal string Current { get; private set; }
        internal AvatarAnimationController(Animation original, GameObject target, bool driver = false)
        {
            Animation = target.AddComponent<Animation>();
            this.driver = driver;
            Animation.playAutomatically = false; Animation.wrapMode = WrapMode.Loop; Animation.cullingType = AnimationCullingType.AlwaysAnimate;
            try
            {
                Add(original, driver ? "driving_front" : "BreatheIdle", "Idle");
                if (!driver) { Add(original, "Walk", "Walk"); Add(original, "Running", "Run"); }
                // SampleAnimation also works on the deliberately inactive construction tree.
                Animation.GetClip("Idle").SampleAnimation(target, 0); Current = "Idle";
            }
            catch { Dispose(); throw; }
        }
        private void Add(Animation original, string source, string semantic)
        {
            var clip = original.GetClip(source);
            if (!clip || !clip.legacy) throw new InvalidOperationException("Compatible legacy clip unavailable: " + source);
            var copy = UnityEngine.Object.Instantiate(clip); clips.Add(copy);
            copy.name = "TS2MP_" + semantic; copy.events = new AnimationEvent[0]; copy.wrapMode = WrapMode.Loop;
            Animation.AddClip(copy, semantic); Animation[semantic].wrapMode = WrapMode.Loop;
        }
        internal void SetState(LocomotionState state)
        {
            string next = driver ? "Idle" : state == LocomotionState.Run ? "Run" : state == LocomotionState.Walk || state == LocomotionState.CrouchMove ? "Walk" : "Idle";
            if (next == Current && Animation.IsPlaying(next)) return;
            Animation.CrossFade(next, 0.15f); Current = next;
        }
        public void Dispose() { if (Animation) Animation.Stop(); foreach (var clip in clips) if (clip) UnityEngine.Object.Destroy(clip); clips.Clear(); }
    }
}
