using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThiefSim2Multiplayer.Diagnostics
{
    public sealed partial class Plugin
    {
        private bool pickupTestRunning;
        private static object State(Component component,string name)
        { return component.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(component); }
        private IEnumerator TestDroppedPickup()
        {
            string permit=Path.Combine(outputDirectory,"pickup-authorization.txt");
            string expected=Process.GetCurrentProcess().Id.ToString();
            if(pickupTestRunning || !File.Exists(permit) || new FileInfo(permit).Length>64 ||
                DateTime.UtcNow-File.GetLastWriteTimeUtc(permit)>TimeSpan.FromMinutes(5) || File.ReadAllText(permit).Trim()!=expected)
            { Emit("pickup_test_blocked","reason","missing_fresh_backup_authorization_or_running"); yield break; }
            File.Delete(permit);
            Component player=null,pickup=null; int matches=0,visited=0;
            var queue=new Queue<Transform>();
            for(int i=0;i<SceneManager.sceneCount;i++)
                foreach(GameObject root in SceneManager.GetSceneAt(i).GetRootGameObjects()) queue.Enqueue(root.transform);
            // A deliberately narrow experiment: only ONE already dropped starter crowbar.
            // No authored loot, rewards, police activation, save method, or arbitrary game call.
            while(queue.Count>0 && visited++<50000)
            {
                Transform node=queue.Dequeue(); if(!node)continue;
                foreach(Component component in node.GetComponents<Component>())
                {
                    if(!component)continue;
                    if(component.GetType().Name=="PlayerScript")player=component;
                    if(component.GetType().Name=="Pickupable" && node.name=="dropped" && node.gameObject.activeInHierarchy &&
                        Convert.ToInt32(State(component,"item_ID"))==0 && Equals(State(component,"wasDropped"),true) &&
                        Equals(State(component,"is_heavy_item"),false) && Equals(State(component,"is_money"),false))
                    {pickup=component;matches++;}
                }
                for(int i=0;i<node.childCount && queue.Count+visited<50000;i++)queue.Enqueue(node.GetChild(i));
                if(visited%256==0)yield return null;
            }
            Component definition=pickup ? State(pickup,"itemScript") as Component : null;
            if(!player || !pickup || !definition || matches!=1 || Vector3.Distance(player.transform.position,pickup.transform.position)>5 ||
                !Equals(State(player,"canInteract"),true) || Equals(State(pickup,"picking"),true) ||
                Convert.ToSingle(State(player,"currentWeight"))+Convert.ToSingle(State(definition,"item_weight"))>Convert.ToSingle(State(player,"maxWeight")))
            { Emit("pickup_test_blocked","reason","unique_nearby_dropped_crowbar_or_capacity_not_valid"); yield break; }
            MethodInfo method=pickup.GetType().GetMethod("PickUp",Type.EmptyTypes);
            if(method==null){Emit("pickup_test_blocked","reason","method_missing");yield break;}
            pickupTestRunning=true;
            try
            {
                string path=PathOf(pickup.transform);
                Emit("pickup_test_begin","path",path,"instance_id",pickup.GetInstanceID().ToString(),"entry","original_PickUp_method_after_harness_validation_not_player_raycast");
                ReadStateFields(pickup); ObserveTransaction("test_before",player,definition);
                method.Invoke(pickup,null);
                yield return new WaitForSecondsRealtime(0.1f);
                Emit("pickup_test_sample","elapsed","0.1","world_exists",(bool)pickup ? "True":"False");
                ObserveTransaction("test_0.1",player,definition);
                yield return new WaitForSecondsRealtime(0.6f);
                Emit("pickup_test_end","path",path,"world_exists",(bool)pickup ? "True":"False");
                ObserveTransaction("test_0.7",player,definition);
            }
            finally {pickupTestRunning=false;}
        }
        private void ObserveTransaction(string phase,params Component[] components)
        {
            foreach(Component component in components)
                if(component)
                    foreach(string field in new[]{"item_ID","item_amount","currentWeight","maxWeight","currentXPPoints","moneyAm","extraSkillPoints","specialSkillXP","pickedItemsSinceSave","saveAfterLeaving"})
                    {
                        object value=State(component,field);
                        if(value!=null) Emit("transaction_field","phase",phase,"type",component.GetType().Name,"path",PathOf(component.transform),"field",field,"value",FieldValue(value));
                    }
        }
    }
}
