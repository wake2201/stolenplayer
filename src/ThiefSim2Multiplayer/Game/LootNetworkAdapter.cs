using System;
using UnityEngine;
using ThiefSim2Multiplayer.Core.Inventory;
using ThiefSim2Multiplayer.Core.World;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class LootNetworkAdapter : ILootAdapter, IDespawnPresentation
    {
        internal Component Pickup { get; private set; }
        internal LootState State;
        private readonly bool dynamic, host;
        private readonly Func<LootState, Component> spawn;
        private Rigidbody suppressedBody;
        private bool originalKinematic, originalGravity;
        private byte[] captured;
        private Vector3 capturedPosition;
        private Quaternion capturedRotation;
        private bool capturedAvailable;
        internal LootNetworkAdapter(Component pickup, LootState state, bool isDynamic, bool isHost, Func<LootState, Component> spawner = null)
        { Pickup = pickup; State = state; dynamic = isDynamic; host = isHost; spawn = spawner; }
        public EntityTypeId EntityType => EntityTypeId.Pickup;
        public bool IsAlive => Pickup || (dynamic && spawn != null);
        public byte[] CaptureState()
        {
            if (Pickup && host)
            { var p = Pickup.transform.position; var q = Pickup.transform.rotation; State.X = p.x; State.Y = p.y; State.Z = p.z; State.Qx = q.x; State.Qy = q.y; State.Qz = q.z; State.Qw = q.w; State.Available = Pickup.gameObject.activeInHierarchy; }
            var position=new Vector3(State.X,State.Y,State.Z); var rotation=new Quaternion(State.Qx,State.Qy,State.Qz,State.Qw);
            if(captured==null || !position.Equals(capturedPosition) || !rotation.Equals(capturedRotation) || capturedAvailable!=State.Available) {
                captured=State.Encode(); capturedPosition=position; capturedRotation=rotation; capturedAvailable=State.Available;
            }
            return captured;
        }
        public bool ValidateState(byte[] payload) { LootState state; return LootState.TryDecode(payload, out state); }
        public void ApplyState(byte[] payload)
        {
            LootState state; if (!LootState.TryDecode(payload, out state)) throw new ArgumentException("Invalid loot payload.");
            if (!dynamic && State.Item.Definition != state.Item.Definition) throw new ArgumentException("Authored loot definition mismatch.");
            State = state;
            captured=null;
            if (!Pickup && dynamic) Pickup = spawn(state);
            if (!Pickup) throw new InvalidOperationException("Loot binding missing.");
            if ((int)GameAccess.Get(Pickup, "item_ID") != state.Item.Definition) throw new ArgumentException("Native loot definition mismatch.");
            var position = new Vector3(state.X, state.Y, state.Z); var rotation = new Quaternion(state.Qx, state.Qy, state.Qz, state.Qw);
            // Equal authored states need no native transform/activation callback.
            // Exact component equality avoids treating a small canonical move as equal.
            if (!Pickup.transform.position.Equals(position) || !Pickup.transform.rotation.Equals(rotation)) Pickup.transform.SetPositionAndRotation(position, rotation);
            if (Pickup.gameObject.activeSelf != state.Available) Pickup.gameObject.SetActive(state.Available);
            var rigid = Pickup.GetComponent<Rigidbody>(); if (rigid && !host) {
                if(suppressedBody!=rigid) { RestorePhysics(); suppressedBody=rigid; originalKinematic=rigid.isKinematic; originalGravity=rigid.useGravity; }
                if (!rigid.isKinematic) rigid.isKinematic = true; if (rigid.useGravity) rigid.useGravity = false;
            }
        }
        public void ApplyDespawn()
        { if (Pickup) { Pickup.gameObject.SetActive(false); if (dynamic) UnityEngine.Object.Destroy(Pickup.gameObject); } }
        public void Release()
        { RestorePhysics(); if (dynamic && !host && Pickup) UnityEngine.Object.Destroy(Pickup.gameObject); Pickup = null; }
        private void RestorePhysics()
        { if(suppressedBody) { suppressedBody.isKinematic=originalKinematic; suppressedBody.useGravity=originalGravity; } suppressedBody=null; }
    }
}
