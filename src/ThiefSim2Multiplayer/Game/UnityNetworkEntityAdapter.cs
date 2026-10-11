using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    // Future adapters implement explicit semantic codecs. No real game adapter is installed in C.
    internal abstract class UnityNetworkEntityAdapter : INetworkEntityAdapter
    {
        private UnityEngine.Object binding;
        private readonly int mainThread = Thread.CurrentThread.ManagedThreadId;
        protected UnityNetworkEntityAdapter(UnityEngine.Object localBinding) { if (!localBinding) throw new ArgumentException("Destroyed binding."); binding = localBinding; }
        protected UnityEngine.Object Binding { get { AssertThread(); return binding ? binding : null; } }
        public virtual bool IsAlive { get { AssertThread(); return binding; } }
        protected void AssertThread() { if (Thread.CurrentThread.ManagedThreadId != mainThread) throw new InvalidOperationException("Unity binding accessed off main thread."); }
        public abstract EntityTypeId EntityType { get; }
        public abstract byte[] CaptureState();
        public abstract bool ValidateState(byte[] payload);
        public abstract void ApplyState(byte[] payload);
        public virtual void Release() { AssertThread(); binding = null; }
        internal static StaticEntityDescriptor Describe(Component component, string buildFingerprint, string componentContract, int sameTypeOrdinal, string validatedSemanticKey = "")
        {
            if (!component || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded || string.IsNullOrEmpty(component.gameObject.scene.path)) throw new ArgumentException("Loaded authored scene/component required.");
            // Root-to-leaf indices, including root sibling index. Names are escaped diagnostic context; never name alone.
            var path = new Stack<string>();
            for (Transform node = component.transform; node; node = node.parent)
                path.Push(Uri.EscapeDataString(node.name) + "[" + node.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + "]");
            return new StaticEntityDescriptor(buildFingerprint, component.gameObject.scene.path, string.Join("/", path.ToArray()), componentContract, sameTypeOrdinal, validatedSemanticKey);
        }
        internal static string RelativeHierarchyPath(Transform root, Transform item)
        {
            if (!root || !item) throw new ArgumentException("Vehicle and native door transforms are required.");
            var path=new Stack<string>(); Transform node=item;
            while(node && node!=root) { path.Push(Uri.EscapeDataString(node.name)+"["+node.GetSiblingIndex().ToString(CultureInfo.InvariantCulture)+"]"); node=node.parent; }
            if(node!=root) throw new ArgumentException("Native door is outside its stable vehicle root.");
            return string.Join("/",path.ToArray());
        }
    }
}
