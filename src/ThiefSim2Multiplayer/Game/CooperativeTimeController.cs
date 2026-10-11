using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using ThiefSim2Multiplayer.Core;
using ThiefSim2Multiplayer.Core.Networking;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed class CooperativeTimeController : IDisposable
    {
        private static CooperativeTimeController current;
        private readonly NetworkManager network;
        private readonly Action<string> log;
        private readonly Harmony harmony=new Harmony(Plugin.Id+".cooperative-time");
        private Component gm, pass, player;
        private Behaviour frozenMotor;
        private bool motorEnabled, interact, drive, bypass;
        private bool clockSuppressed, nativeSleepStarted;
        private object enviro, gameTime, originalProgress;
        private object stoppedProgress;
        private double nextPublish;
        internal CooperativeTimeController(NetworkManager n,Action<string> logger)
        {
            network=n; log=logger; current=this; network.RequireTimeBaseline=true; network.ApplyWorldTime=Apply;
            network.SleepStarted+=StartNative; network.SleepEnded+=EndNative;
            network.RemoteRemoved+=RestoreClock;
            harmony.Patch(AccessTools.Method(Type.GetType("DayNightTime, Assembly-CSharp",true),"Update"),transpiler:new HarmonyMethod(typeof(CooperativeTimeController),nameof(HourEffects)));
        }
        // Preserve the native lighting portion while skipping the Client's independent hourly/day mutations.
        private static IEnumerable<CodeInstruction> HourEffects(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
        {
            var code=instructions.ToList();
            int first=code.FindIndex(i=>i.opcode==OpCodes.Stfld && (i.operand as FieldInfo)?.Name=="prevHour");
            int refresh=code.FindIndex(first+1,i=>i.Calls(AccessTools.Method(Type.GetType("DayNightTime, Assembly-CSharp",true),"RefreshDay")));
            if(first<0 || refresh<2 || code[refresh-1].opcode!=OpCodes.Ldarg_0) throw new InvalidOperationException("Native hourly-effect boundary changed.");
            Label skip=generator.DefineLabel(); code[refresh-1].labels.Add(skip);
            code.InsertRange(first+1,new[] {new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(CooperativeTimeController),nameof(ClientClock))),new CodeInstruction(OpCodes.Brtrue,skip)});
            return code;
        }
        private static bool ClientClock() => current!=null && current.network.Session.State!=SessionState.Offline && !current.network.Session.IsHost;
        internal bool Intent(Component source,MethodBase method,bool mission)
        {
            if(bypass) return true;
            string reason;
            if(!network.CanGlobalAction(mission,out reason)) { log("[TS2MP] SLEEP denied: "+reason); return false; }
            if(!network.PeerConnected) return true;
            if(method.Name!="PassStart") return true;
            if(!source || !(bool)AccessTools.Method(source.GetType(),"CheckIfCanSleepAnymore").Invoke(source,null)) return false;
            if(network.Inventories.Authority.PendingCount!=0) { log("[TS2MP] SLEEP denied: inventory transaction pending"); return false; }
            pass=source;
            if(!network.BeginSleep(mission)) return false;
            log("[TS2MP] SLEEP start barrier id="+network.Sleep.Id); return false;
        }
        private void StartNative()
        {
            if(!pass) { network.FailSceneTransition("Native sleep controller lost before start."); return; }
            try { bypass=true; nativeSleepStarted=true; GameAccess.Call(pass,"PassStart"); }
            finally { bypass=false; }
        }
        private void EndNative()
        {
            if(nativeSleepStarted && pass && GameAccess.Bool(pass,"passingTime")) GameAccess.Call(pass,"StopIt");
            if(nativeSleepStarted && pass && player && GameAccess.Bool(player,"passingTimeOn")) GameAccess.Call(pass,"QuitIt");
            nativeSleepStarted=false;
            Unfreeze();
        }
        internal void Bind(Component manager)
        {
            if(gm==manager) return;
            Release(); gm=manager;
            if(!gm) return;
            player=GameAccess.Get(gm,"PS") as Component; pass=GameAccess.Get(gm,"passTimeScript") as Component;
            enviro=GameAccess.Get(gm,"enviro"); gameTime=GameAccess.Get(enviro,"GameTime"); originalProgress=GameAccess.Get(gameTime,"ProgressTime");
            stoppedProgress=Enum.Parse(originalProgress.GetType(),"None");
            if(ClientClock()) { GameAccess.Set(gameTime,"ProgressTime",stoppedProgress); clockSuppressed=true; }
        }
        internal WorldTimeState Capture()
        {
            return new WorldTimeState {Hour=(float)AccessTools.Method(enviro.GetType(),"GetTimeOfDay").Invoke(enviro,null),Year=(int)GameAccess.Get(gameTime,"Years"),Day=(int)GameAccess.Get(gameTime,"Days"),Weekday=(int)GameAccess.Get(gm,"currentDayInt"),Week=(int)GameAccess.Get(gm,"currentWeek"),TotalHours=(int)GameAccess.Get(player,"totalHoursPassed"),RentHours=(int)GameAccess.Get(GameAccess.Get(player,"saveGameScript"),"timeTillRentPayment"),DaysPlayed=(int)GameAccess.Get(GameAccess.Get(player,"invScript"),"daysInGame")};
        }
        private bool Apply(WorldTimeState s)
        {
            if(!gm || !player || enviro==null || !GameAccess.Bool(gm,"loadedMap")) return false;
            if(s.SleepId!=0) Freeze();
            AccessTools.Method(enviro.GetType(),"SetInternalTimeOfDay").Invoke(enviro,new object[] {s.Hour});
            GameAccess.Set(gameTime,"Years",s.Year); GameAccess.Set(gameTime,"Days",s.Day); GameAccess.Call(enviro,"ResetHourEventTimer");
            GameAccess.Set(gm,"currentDayInt",s.Weekday); GameAccess.Set(gm,"currentWeek",s.Week); GameAccess.Set(player,"totalHoursPassed",s.TotalHours);
            GameAccess.Set(GameAccess.Get(player,"saveGameScript"),"timeTillRentPayment",s.RentHours); GameAccess.Set(GameAccess.Get(player,"invScript"),"daysInGame",s.DaysPlayed);
            GameAccess.Call(gm,"TimeStringFromPercent"); GameAccess.Call(GameAccess.Get(gm,"dayNightScript"),"RefreshDay"); return true;
        }
        internal void Tick()
        {
            if(!gm || network.Session.State==SessionState.Offline) { Unfreeze(); return; }
            if(ClientClock()) {
                GameAccess.Set(gameTime,"ProgressTime",stoppedProgress);
                clockSuppressed=true;
                if(network.Sleep.Frozen) Freeze(); else Unfreeze();
            }
            else if(network.PeerConnected) {
                if(network.Sleep.Phase==CooperativeSleepPhase.Running && pass && !GameAccess.Bool(pass,"passingTime")) { network.WakeSleep(Capture()); log("[TS2MP] SLEEP wake barrier id="+network.Sleep.Id); }
                if(network.Now>=nextPublish) { nextPublish=network.Now+(network.Sleep.Frozen ? .25 : 1); network.PublishTime(Capture()); }
            }
        }
        private void Unfreeze()
        {
            if(frozenMotor) { frozenMotor.enabled=motorEnabled; if(player) { GameAccess.Set(player,"canInteract",interact); GameAccess.Set(player,"can_drive_car",drive); } }
            frozenMotor=null;
        }
        private void Freeze() {
            if(frozenMotor || !player) return;
            frozenMotor=GameAccess.Get(player,"PMB") as Behaviour; motorEnabled=frozenMotor.enabled; interact=GameAccess.Bool(player,"canInteract"); drive=GameAccess.Bool(player,"can_drive_car");
            frozenMotor.enabled=false; GameAccess.Set(player,"canInteract",false); GameAccess.Set(player,"can_drive_car",false);
        }
        internal void OpenPassengerSleep(Component actor)
        {
            string reason; if(!pass || !network.CanGlobalAction(true,out reason)) return;
            var camera=GameAccess.Get(actor,"MainCam") as Camera;
            GameAccess.Set(actor,"cursorLock",false); Cursor.visible=true; Cursor.lockState=CursorLockMode.None;
            AccessTools.Method(pass.GetType(),"Begin").Invoke(pass,new object[] {camera.transform});
        }
        internal void Release() { RestoreClock(); nativeSleepStarted=false; gm=pass=player=null; enviro=gameTime=originalProgress=null; }
        private void RestoreClock() { Unfreeze(); if(clockSuppressed && gameTime!=null && originalProgress!=null) GameAccess.Set(gameTime,"ProgressTime",originalProgress); clockSuppressed=false; }
        public void Dispose() { EndNative(); Release(); network.ApplyWorldTime=null; network.SleepStarted-=StartNative; network.SleepEnded-=EndNative; network.RemoteRemoved-=RestoreClock; harmony.UnpatchSelf(); if(current==this) current=null; }
    }
}
