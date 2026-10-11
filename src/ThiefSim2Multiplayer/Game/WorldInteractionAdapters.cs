using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using ThiefSim2Multiplayer.Core.World;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    // Fixed, build-verified contracts only. No names or field paths come from the wire.
    internal static class GameAccess
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<Type,Dictionary<string,FieldInfo>> cached=new Dictionary<Type,Dictionary<string,FieldInfo>>();
        private static FieldInfo Field(object obj,string name) {
            var type=obj.GetType(); Dictionary<string,FieldInfo> fields; if(!cached.TryGetValue(type,out fields)) cached.Add(type,fields=new Dictionary<string,FieldInfo>());
            FieldInfo field; if(!fields.TryGetValue(name,out field)) { field=type.GetField(name,Fields); if(field==null) throw new MissingFieldException(type.FullName,name); fields.Add(name,field); } return field;
        }
        internal static object Get(object obj, string name) => Field(obj,name).GetValue(obj);
        internal static bool Bool(object obj, string name) => (bool)Get(obj, name);
        internal static void Set(object obj, string name, object value) => Field(obj,name).SetValue(obj, value);
        internal static void Call(object obj, string name) => obj.GetType().GetMethod(name, Fields, null, Type.EmptyTypes, null).Invoke(obj, null);
        internal static InteractionTag Tag(Component door)
        {
            switch (door.tag) { case "Door": return InteractionTag.Door; case "Lock1": return InteractionTag.Lock1; case "ElectricDoor": return InteractionTag.ElectricDoor; case "Untagged": return InteractionTag.Untagged; case "Window": return InteractionTag.Window; default: throw new ArgumentException("Unsupported interaction tag: " + door.tag); }
        }
        internal static string TagName(InteractionTag tag) => tag.ToString();
    }
    internal sealed class DoorPresentation
    {
        internal readonly Component Door;
        private bool received, previousOpen, previousSlow, previousMoving;
        private Vector3 fromPosition, toPosition;
        private Quaternion fromRotation, toRotation;
        private float receivedAt;
        private bool suppressNextCloseAudio;
        private float suppressCloseAudioUntil;
        internal DoorMotionState Motion;
        internal DoorPresentation(Component door) { Door = door; }
        internal void SuppressNextCloseAudio() { suppressNextCloseAudio=true; suppressCloseAudioUntil=Time.realtimeSinceStartup+2f; }
        internal void ClearCloseAudioSuppression() { suppressNextCloseAudio=false; suppressCloseAudioUntil=0; }
        internal DoorMotionState CaptureMotion()
        {
            var p=Door.transform.localPosition; var q=Door.transform.localRotation;
            return new DoorMotionState {Opening=GameAccess.Bool(Door,"opening"),Slow=GameAccess.Bool(Door,"openingSlowly"),CanUse=GameAccess.Bool(Door,"canUse"),Loading=GameAccess.Bool(Door,"load_slow"),Progress=(float)GameAccess.Get(Door,"slowMoving"),X=p.x,Y=p.y,Z=p.z,Qx=q.x,Qy=q.y,Qz=q.z,Qw=q.w};
        }
        internal void ApplyMotion(DoorMotionState motion, bool open, bool enabled, bool broken)
        {
            var p=new Vector3(motion.X,motion.Y,motion.Z); var q=new Quaternion(motion.Qx,motion.Qy,motion.Qz,motion.Qw);
            bool baseline=!received || NetworkApplyScope.Silent;
            fromPosition=baseline ? p : Door.transform.localPosition; fromRotation=baseline ? q : Door.transform.localRotation;
            toPosition=p; toRotation=q; receivedAt=Time.realtimeSinceStartup; Motion=motion;
            GameAccess.Set(Door,"load_slow",false); GameAccess.Set(Door,"closeAfter",false);
            GameAccess.Set(Door,"isOpen",open); GameAccess.Set(Door,"opening",motion.Opening); GameAccess.Set(Door,"openingSlowly",motion.Slow); GameAccess.Set(Door,"canUse",motion.CanUse);
            GameAccess.Set(Door,"brokeHinge",broken); ((Behaviour)Door).enabled=enabled;
            var windowUse=GameAccess.Get(Door,"WindowUse") as GameObject; if(windowUse) windowUse.SetActive(open && !motion.Opening);
            var source=GameAccess.Get(Door,"DoorSound") as AudioSource;
            bool suppressCloseAudio=suppressNextCloseAudio && !open && motion.Opening && Time.realtimeSinceStartup<=suppressCloseAudioUntil;
            if(suppressCloseAudio || Time.realtimeSinceStartup>suppressCloseAudioUntil) suppressNextCloseAudio=false;
            if(!baseline && source) {
                if(suppressCloseAudio) { }
                else if(motion.Slow && !previousSlow) { source.Stop(); source.volume=.3f; source.pitch=UnityEngine.Random.Range(.9f,1.1f); source.clip=GameAccess.Get(Door,"DoorSqueak") as AudioClip; if(source.clip) source.Play(); }
                else if(!motion.Slow && motion.Opening && (!previousMoving || previousSlow || previousOpen!=open)) {
                    if(previousSlow) source.Stop(); var clip=GameAccess.Get(Door,open ? "DoorOpen" : "DoorClose") as AudioClip;
                    source.volume=(float)GameAccess.Get(Door,"maxVolume"); source.pitch=1; if(clip) source.PlayOneShot(clip);
                }
                else if(!motion.Slow && previousSlow) source.Stop();
            }
            previousOpen=open; previousSlow=motion.Slow; previousMoving=motion.Opening; received=true;
            if(baseline) Tick();
        }
        internal void Tick()
        {
            if(!received || !Door) return;
            float t=Mathf.Clamp01((Time.realtimeSinceStartup-receivedAt)/.1f);
            // Follow actual native samples; no receiver hinge simulation or invented opening duration.
            Door.transform.localPosition=Vector3.Lerp(fromPosition,toPosition,t); Door.transform.localRotation=Quaternion.Slerp(fromRotation,toRotation,t);
        }
        internal bool Permits(InteractionKind kind) => Door && Door.gameObject.activeInHierarchy && ((Behaviour)Door).enabled && Door.tag == "Door" && !GameAccess.Bool(Door, "requires_key") && !GameAccess.Bool(Door, "brokeHinge") && !GameAccess.Bool(Door, "opening") && !GameAccess.Bool(Door, "npcUsing") && GameAccess.Bool(Door, "canUse") && Time.timeScale > 0;
        internal void Execute(InteractionKind kind)
        {
            if (GameAccess.Bool(Door, "isOpen") == (kind == InteractionKind.Open)) return;
            // Use preserves game noise and audio. Restore its two local-actor control writes synchronously.
            object player = GameAccess.Get(Door, "PS"); object motor = GameAccess.Get(player, "PMB"), camera = GameAccess.Get(player, "PCB");
            object move = GameAccess.Get(motor, "canMove"), sensitivity = GameAccess.Get(camera, "mouseSensitivity");
            try { GameAccess.Set(Door, "useTim", 0f); GameAccess.Call(Door, "Use"); }
            finally { GameAccess.Set(motor, "canMove", move); GameAccess.Set(camera, "mouseSensitivity", sensitivity); }
        }
        internal void ExecuteNativeEntryClose()
        {
            if(!GameAccess.Bool(Door,"isOpen")) return;
            if(GameAccess.Bool(Door,"opening") || GameAccess.Bool(Door,"openingSlowly")) GameAccess.Call(Door,"Resett");
            GameAccess.Set(Door,"canUse",true);
            Execute(InteractionKind.Close);
        }
        internal void ApplyOpen(bool open, bool enabled, bool broken)
        {
            // UseFully has no PS/UI/noise callbacks; original Update owns all motion and Resett.
            // Reset stale slow/NPC timers before starting an authoritative presentation target.
            GameAccess.Set(Door, "load_slow", false); GameAccess.Set(Door, "closeAfter", false);
            bool changed = GameAccess.Bool(Door, "isOpen") != open;
            if (changed || GameAccess.Bool(Door, "openingSlowly"))
            {
                GameAccess.Call(Door, "Resett");
                GameAccess.Set(Door, "isOpen", !open);
                GameAccess.Call(Door, "UseFully");
                var source = GameAccess.Get(Door, "DoorSound") as AudioSource;
                var clip = GameAccess.Get(Door, open ? "DoorOpen" : "DoorClose") as AudioClip;
                if (changed && !NetworkApplyScope.Silent && source && clip) { source.volume = (float)GameAccess.Get(Door, "maxVolume"); source.pitch = 1f; source.PlayOneShot(clip); }
            }
            GameAccess.Set(Door, "brokeHinge", broken); ((Behaviour)Door).enabled = enabled;
        }
    }
    internal sealed class DoorNetworkAdapter : UnityNetworkEntityAdapter, IActorInteractionAdapter
    {
        internal readonly Component Door;
        internal readonly DoorPresentation Presentation;
        internal readonly VehicleNetworkAdapter VehicleOwner;
        internal readonly bool RearPassengerEntryDoor;
        private readonly Component[] locks;
        internal Component[] Locks => locks;
        internal Func<byte,InteractionKind,bool> RemotePermits;
        internal Action<byte,InteractionKind> RemoteExecute;
        internal Func<byte,InteractionKind,bool> Continuation;
        private readonly Dictionary<Component,bool> suppressedAlarms=new Dictionary<Component,bool>();
        internal DoorNetworkAdapter(Component door, Component[] linkedLocks, VehicleNetworkAdapter vehicleOwner=null, bool rearPassengerEntryDoor=false) : base(door) { Door = door; Presentation = new DoorPresentation(door); locks = linkedLocks ?? Array.Empty<Component>(); VehicleOwner=vehicleOwner; RearPassengerEntryDoor=rearPassengerEntryDoor; }
        public override EntityTypeId EntityType => EntityTypeId.Door;
        public override byte[] CaptureState()
        {
            AssertThread(); return new DoorState { Open = GameAccess.Bool(Door, "isOpen"), Enabled = ((Behaviour)Door).enabled, RequiresKey = GameAccess.Bool(Door, "requires_key"), RelockAvailable = GameAccess.Bool(Door, "was_locked"), BrokenHinge = GameAccess.Bool(Door, "brokeHinge"), Tag = GameAccess.Tag(Door), HasMotion=true, Motion=Presentation.CaptureMotion() }.Encode();
        }
        public override bool ValidateState(byte[] payload) { DoorState state; return DoorState.TryDecode(payload, out state); }
        public override void ApplyState(byte[] payload)
        {
            AssertThread(); DoorState state; if (!IsAlive || !DoorState.TryDecode(payload, out state)) throw new ArgumentException("Invalid door state/binding.");
            using (new NetworkApplyScope())
            {
                bool unlockedKey=GameAccess.Bool(Door,"requires_key") && !state.RequiresKey;
                Door.tag = GameAccess.TagName(state.Tag); GameAccess.Set(Door, "requires_key", state.RequiresKey); GameAccess.Set(Door, "was_locked", state.RelockAvailable);
                // Reproduce lock capability without replaying rewards, story, physics or alarms.
                // Scene preparation indexes the authored links once. A baseline must not
                // scan every native scene object again for every door in the manifest.
                foreach (var lp in locks)
                {
                    if (!lp || !lp.gameObject.activeInHierarchy) continue;
                    // A shared lock may cover several leaves: all canonical leaf states are applied in the baseline.
                    lp.tag = lp.gameObject==Door.gameObject ? GameAccess.TagName(state.Tag) : state.Tag == InteractionTag.Lock1 ? "Lock1" : "Untagged";
                    if(!suppressedAlarms.ContainsKey(lp)) suppressedAlarms.Add(lp,GameAccess.Bool(lp,"hasAlarm"));
                    GameAccess.Set(lp, "hasAlarm", false);
                    if (GameAccess.Bool(lp, "Disable_This_Collider")) { var collider = lp.GetComponent<BoxCollider>(); if (collider) collider.enabled = state.Tag == InteractionTag.Lock1; }
                }
                if(state.HasMotion) Presentation.ApplyMotion(state.Motion,state.Open,state.Enabled,state.BrokenHinge); else Presentation.ApplyOpen(state.Open, state.Enabled, state.BrokenHinge);
                if(unlockedKey && !NetworkApplyScope.Silent) {
                    var source=GameAccess.Get(Door,"DoorSound") as AudioSource;var clip=GameAccess.Get(Door,"unlockSound") as AudioClip;
                    if(source && clip) source.PlayOneShot(clip);
                }
            }
        }
        public bool Supports(InteractionKind kind) => kind >= InteractionKind.Open && kind <= InteractionKind.UnlockKey;
        public bool CanContinue(byte actor,InteractionKind kind) => Continuation?.Invoke(actor,kind) ?? false;
        public bool Permits(byte actor,InteractionKind kind) => RemotePermits?.Invoke(actor,kind) ?? Permits(kind);
        public void Execute(byte actor,InteractionKind kind) { if(RemoteExecute!=null) RemoteExecute(actor,kind); else Execute(kind); }
        public override void Release()
        {
            AssertThread(); foreach(var pair in suppressedAlarms) if(pair.Key) GameAccess.Set(pair.Key,"hasAlarm",pair.Value);
            suppressedAlarms.Clear(); base.Release();
        }
        public bool Permits(InteractionKind kind) { AssertThread(); return Presentation.Permits(kind); }
        public void Execute(InteractionKind kind) { AssertThread(); Presentation.Execute(kind); }
    }
    internal sealed class WindowNetworkAdapter : UnityNetworkEntityAdapter, IActorInteractionAdapter
    {
        internal readonly Component Door;
        private readonly DoorPresentation presentation;
        internal DoorPresentation Presentation => presentation;
        internal readonly DoorNetworkAdapter Actions;
        private readonly Component lockedWindow;
        private readonly GameObject pane, brokenVisual, enterables;
        private readonly bool instantiateGlass, unconvex;
        internal WindowGlass Outcome;
        private bool alarmSuppressed,originalAlarm;
        internal WindowNetworkAdapter(Component door) : base(door)
        {
            Door = door; presentation = new DoorPresentation(door);
            Actions = new DoorNetworkAdapter(door,null);
            lockedWindow = door.GetComponent(Type.GetType("LockedWindow, Assembly-CSharp", true));
            if (lockedWindow)
            {
                var windows = (GameObject[])GameAccess.Get(lockedWindow, "Windows");
                pane = windows.Length > 0 ? windows[0] : null; brokenVisual = windows.Length > 1 ? windows[1] : null;
                enterables = GameAccess.Get(lockedWindow, "enterablesEnable") as GameObject;
                instantiateGlass = GameAccess.Bool(lockedWindow, "instantiateGlass"); unconvex = GameAccess.Bool(lockedWindow, "unconvexCollider");
                if (GameAccess.Bool(lockedWindow, "brokeGlass")) Outcome = WindowGlass.Broken;
                else if (Door.tag == "Door" && !((Behaviour)lockedWindow).enabled) Outcome = WindowGlass.Cut;
            }
        }
        public override EntityTypeId EntityType => EntityTypeId.Window;
        public override byte[] CaptureState()
        {
            AssertThread(); return new WindowState { Open = GameAccess.Bool(Door, "isOpen"), Enabled = ((Behaviour)Door).enabled, Locked = lockedWindow && Outcome == WindowGlass.Intact, Glass = Outcome, PaneVisible = pane && pane.activeSelf, Tag = GameAccess.Tag(Door), HasMotion=true, Motion=presentation.CaptureMotion() }.Encode();
        }
        public override bool ValidateState(byte[] payload) { WindowState state; return WindowState.TryDecode(payload, out state); }
        public override void ApplyState(byte[] payload)
        {
            AssertThread(); WindowState state; if (!IsAlive || !WindowState.TryDecode(payload, out state)) throw new ArgumentException("Invalid window state/binding.");
            using (new NetworkApplyScope())
            {
                Outcome = state.Glass; Door.tag = GameAccess.TagName(state.Tag);
                if (lockedWindow) { ((Behaviour)lockedWindow).enabled = state.Locked; if(!alarmSuppressed) {originalAlarm=GameAccess.Bool(lockedWindow,"hasAlarm"); alarmSuppressed=true;} GameAccess.Set(lockedWindow, "hasAlarm", false); GameAccess.Set(lockedWindow, "brokeGlass", state.Glass == WindowGlass.Broken); }
                if (pane) pane.SetActive(state.PaneVisible);
                if (brokenVisual && !instantiateGlass) brokenVisual.SetActive(state.Glass == WindowGlass.Broken);
                if (enterables) enterables.SetActive(!state.Locked);
                if (unconvex && !state.Locked) { var collider = Door.GetComponent<MeshCollider>(); if (collider) collider.convex = false; }
                Door.gameObject.layer = state.Locked ? Door.gameObject.layer : 13;
                if(state.HasMotion) presentation.ApplyMotion(state.Motion,state.Open,state.Enabled,false); else presentation.ApplyOpen(state.Open, state.Enabled, false);
            }
        }
        public bool Supports(InteractionKind kind) => kind>=InteractionKind.Open && kind<=InteractionKind.SlowRelease;
        public bool CanContinue(byte actor,InteractionKind kind) => Actions.CanContinue(actor,kind);
        public bool Permits(byte actor,InteractionKind kind) => (kind==InteractionKind.SlowRelease || !lockedWindow || Outcome!=WindowGlass.Intact) && Actions.Permits(actor,kind);
        public void Execute(byte actor,InteractionKind kind) => Actions.Execute(actor,kind);
        public override void Release()
        {
            AssertThread(); if(alarmSuppressed && lockedWindow) GameAccess.Set(lockedWindow,"hasAlarm",originalAlarm);
            alarmSuppressed=false; base.Release();
        }
        public bool Permits(InteractionKind kind) { AssertThread(); return Outcome != WindowGlass.Intact || !lockedWindow ? presentation.Permits(kind) : false; }
        public void Execute(InteractionKind kind) { AssertThread(); presentation.Execute(kind); }
    }
}
