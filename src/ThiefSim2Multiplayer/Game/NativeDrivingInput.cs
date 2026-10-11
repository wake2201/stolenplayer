using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ThiefSim2Multiplayer.Core.Networking;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class SharedVehicleController
    {
        private static bool nativeInputActive;
        private static DriverInput nativeInput;
        private static bool NetworkInputActive() => nativeInputActive;
        internal string NativeInputAudit()
        {
            if(Online || fixture) throw new InvalidOperationException("Owned offline audit required.");
            var player=locator.Find(); var car=player ? CurrentFixtureCar(player) : null;
            if(!car) throw new InvalidOperationException("Native vehicle unavailable.");
            var a=new VehicleNetworkAdapter(car);
            string[] keys={"steerInput","throttleInput","brakeInput","handbrakeInput"};
            var values=new object[keys.Length]; for(int i=0;i<keys.Length;i++) values[i]=GameAccess.Get(a.Drive,keys[i]);
            bool engine=GameAccess.Bool(car,"engine_running"),block=GameAccess.Bool(car,"blockDriving"),kinematic=a.Body.isKinematic;
            float drunk=(float)GameAccess.Get(player,"drunkLevel");
            var input=car.GetComponent(Type.GetType("EVP.VehicleStandardInput, Assembly-CSharp",true));
            bool continuous=GameAccess.Bool(input,"continuousForwardAndReverse");
            void Check(bool ok,string label) { if(!ok) throw new InvalidOperationException("Vanilla audit FAIL: "+label); log("[TS2MP] VANILLA AUDIT PASS: "+label); }
            try {
                a.Restore();
                Check(GameAccess.Bool(car,"engine_running")==engine && a.Body.isKinematic==kinematic && Equals(GameAccess.Get(a.Drive,"brakeInput"),values[2]),"read-only Host binding teardown preserves native state");
                if(Mathf.Abs(a.Speed)>.1f) throw new InvalidOperationException("Parked vehicle required.");
                GameAccess.Set(car,"engine_running",true); GameAccess.Set(car,"blockDriving",false);
                GameAccess.Set(player,"drunkLevel",0f); GameAccess.Set(input,"continuousForwardAndReverse",true);
                nativeInputActive=true;
                foreach(float forward in new[] {.09f,.11f,-.09f,-.11f}) {
                    nativeInput=new DriverInput {Forward=forward,Steer=.25f,Handbrake=.3f,EngineRunning=true};
                    a.ReadNativeInput();
                    float expected=Mathf.Abs(forward)<=.1f ? 0 : forward;
                    Check(Mathf.Abs((float)GameAccess.Get(a.Drive,"throttleInput")-expected)<.0001f && (float)GameAccess.Get(a.Drive,"brakeInput")==0 && (float)GameAccess.Get(a.Drive,"steerInput")==.25f && (float)GameAccess.Get(a.Drive,"handbrakeInput")==.3f,"real native input dead zone/axes forward="+forward);
                }
                return "Five focused native preservation regressions passed; no simulation or driving sequence.";
            } finally {
                nativeInputActive=false; nativeInput=null;
                GameAccess.Set(car,"engine_running",engine); GameAccess.Set(car,"blockDriving",block);
                GameAccess.Set(player,"drunkLevel",drunk); GameAccess.Set(input,"continuousForwardAndReverse",continuous);
                for(int i=0;i<keys.Length;i++) GameAccess.Set(a.Drive,keys[i],values[i]);
            }
        }
        private static float NetworkAxis(object player,string action)
        {
            if(nativeInput==null) return action=="handbrake_axis" ? 1 : 0;
            switch(action) {
                case "left_right_car": return nativeInput.Steer;
                case "drive_forward": return nativeInput.Forward;
                case "handbrake_axis": return nativeInput.Handbrake;
                default: throw new InvalidOperationException("Unverified native driving axis: "+action);
            }
        }
        // Both branches retain the original evaluation stack. Offline and local Host
        // calls execute the original field read/GetAxis instruction without reflection.
        private static IEnumerable<CodeInstruction> NativeDrivingReads(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
        {
            int axes=0,gates=0;
            foreach(var instruction in instructions) {
                bool axis=instruction.opcode==OpCodes.Callvirt && instruction.operand is MethodInfo method && method.Name=="GetAxis" && method.DeclaringType.FullName=="Rewired.Player";
                bool gate=instruction.opcode==OpCodes.Ldfld && instruction.operand is FieldInfo field && field.Name=="can_drive_car";
                if(!axis && !gate) { yield return instruction; continue; }
                var original=generator.DefineLabel(); var end=generator.DefineLabel();
                var check=new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SharedVehicleController),nameof(NetworkInputActive)));
                check.labels.AddRange(instruction.labels); instruction.labels.Clear();
                yield return check;
                yield return new CodeInstruction(OpCodes.Brfalse,original);
                if(axis) { axes++; yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SharedVehicleController),nameof(NetworkAxis))); }
                else { gates++; yield return new CodeInstruction(OpCodes.Pop); yield return new CodeInstruction(OpCodes.Ldc_I4_1); }
                yield return new CodeInstruction(OpCodes.Br,end);
                instruction.labels.Add(original); yield return instruction;
                var finish=new CodeInstruction(OpCodes.Nop); finish.labels.Add(end); yield return finish;
            }
            if(axes!=4 || gates!=1) throw new InvalidOperationException("Native driving input contract changed: axes="+axes+" gates="+gates);
        }
    }
}
