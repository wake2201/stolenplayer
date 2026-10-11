using System;
using UnityEngine;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class DoorbellNetworkAdapter : UnityNetworkEntityAdapter, IInteractionAdapter
    {
        internal readonly Component Bell;
        private readonly AudioSource source;
        private ulong activations;
        private bool initialized;
        internal DoorbellNetworkAdapter(Component bell) : base(bell) { Bell=bell; source=bell.GetComponent<AudioSource>(); }
        public override EntityTypeId EntityType => EntityTypeId.Doorbell;
        internal void Activated() { if(activations==ulong.MaxValue) throw new InvalidOperationException("Doorbell counter exhausted."); activations++; }
        public override byte[] CaptureState() { var b=new byte[9]; b[0]=1; for(int i=0;i<8;i++) b[i+1]=(byte)(activations>>(8*i)); return b; }
        public override bool ValidateState(byte[] bytes) => bytes!=null && bytes.Length==9 && bytes[0]==1;
        public override void ApplyState(byte[] bytes)
        {
            if(!ValidateState(bytes)) throw new ArgumentException("Invalid doorbell state.");
            ulong value=0; for(int i=0;i<8;i++) value|=(ulong)bytes[i+1]<<(8*i);
            if(initialized && !NetworkApplyScope.Silent && value>activations && source && source.clip) source.PlayOneShot(source.clip);
            activations=value; initialized=true;
        }
        public bool Supports(InteractionKind kind) => kind==InteractionKind.Ring;
        public bool Permits(InteractionKind kind) => IsAlive && Bell.gameObject.activeInHierarchy && ((Behaviour)Bell).enabled && Bell.tag=="Doorbell" && source && source.clip && Time.timeScale>0;
        public void Execute(InteractionKind kind) { using(new NetworkApplyScope()) GameAccess.Call(Bell,"Ring"); Activated(); }
    }
}
