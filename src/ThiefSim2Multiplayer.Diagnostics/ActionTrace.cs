using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ThiefSim2Multiplayer.Diagnostics
{
    public sealed partial class Plugin
    {
        private static Plugin traceOwner;
        private Harmony actionTrace;
        private int traceEvents;
        private void EnableActionTrace()
        {
            if (actionTrace != null) return;
            Assembly game=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="Assembly-CSharp");
            if(game==null) return;
            traceOwner=this; traceEvents=0; actionTrace=new Harmony(Id+".action-trace");
            foreach(string target in new[] { "SaveGame:SaveGameNewSystem", "SaveGame:SaveGame1", "SaveGame:OnApplicationQuit",
                "GameManager:SaveAll", "GameManager:AddXP", "Door:Use", "Door:NPCUse", "WindowUse:UseWindow",
                "Pickupable:PickUp", "ItemID:Item_Picked", "ItemID:Drop_Item", "Inventory:DropItemNewSystem",
                "StoryManager:AdvanceStory", "StoryManager:SaveStory", "Mission_Item:Mission_Complete",
                "CarController:EnterVehicle", "CarController:ExitVehicle", "Police_Manager:Activate_Chase", "AI:HeardSoundAction",
                "PlayerScript:SelectCrowbar", "PlayerScript:SelectHand", "PlayerScript:CheckItem", "PlayerScript:Attack", "PlayerScript:CrowbarHit" })
            {
                string[] split=target.Split(':'); Type type=game.GetType(split[0],false); if(type==null)continue;
                foreach(MethodInfo method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly).Where(m=>m.Name==split[1] && !m.ContainsGenericParameters))
                    try
                    {
                        actionTrace.Patch(method,new HarmonyMethod(typeof(Plugin),nameof(ActionBefore)),new HarmonyMethod(typeof(Plugin),nameof(ActionAfter)));
                        Emit("trace_installed", "signature", method.ToString(), "type", type.FullName);
                    }
                    catch(Exception error) { Emit("trace_patch_error", "target", target, "error", error.GetType().Name); }
            }
            Assembly prefs=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="FileBasedPrefs");
            Type fbpp=prefs?.GetType("FBPP",false);
            if(fbpp!=null)
                foreach(MethodInfo method in fbpp.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static).Where(m=>new[]{"Save","SaveToMainProfile","SaveSaveFile","WriteToSaveFile"}.Contains(m.Name)))
                    try { actionTrace.Patch(method,new HarmonyMethod(typeof(Plugin),nameof(ActionBefore)),new HarmonyMethod(typeof(Plugin),nameof(ActionAfter))); Emit("trace_installed","type",fbpp.FullName,"signature",method.ToString()); }
                    catch(Exception error) { Emit("trace_patch_error", "target", method.Name, "error", error.GetType().Name); }
        }
        private static void ActionBefore(object __instance, MethodBase __originalMethod,object[] __args) { TraceAction("action_before",__instance,__originalMethod,__args); }
        private static void ActionAfter(object __instance, MethodBase __originalMethod,object[] __args) { TraceAction("action_after",__instance,__originalMethod,__args); }
        private static void TraceAction(string phase, object instance, MethodBase method,object[] args)
        {
            Plugin owner=traceOwner;
            if(!owner || owner.traceEvents>=1000)return;
            owner.traceEvents++;
            try
            {
                var component=instance as Component;
                owner.Emit(phase,"type",method.DeclaringType.FullName,"method",method.ToString(),"frame",Time.frameCount.ToString(),
                    "path",component ? PathOf(component.transform) : "static", "instance_id",component ? component.GetInstanceID().ToString() : "none",
                    "arguments",method.Name=="WriteToSaveFile" ? "payload_length="+(args.Length>0 && args[0] is string ? ((string)args[0]).Length.ToString() : "unknown") : string.Join("|",args.Take(8).Select(a=>owner.FieldValue(a)).ToArray()));
                if(component && method.Name=="Item_Picked")
                {
                    var inventory=State(component,"inventory_script") as Component;
                    owner.ObserveTransaction(phase,component,inventory ? State(inventory,"PS") as Component : null);
                }
                if(owner.traceEvents==1000) owner.Emit("trace_limit", "events", "1000");
            }
            catch { /* Observation must never change a gameplay result. */ }
        }
        private void DisableActionTrace()
        { if(actionTrace!=null){actionTrace.UnpatchSelf();actionTrace=null;Emit("trace_removed");} if(traceOwner==this)traceOwner=null; }
    }
}
