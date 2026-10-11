using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ThiefSim2Multiplayer.Game
{
    internal sealed partial class SharedVehicleController
    {
        private static bool RemoteHostUpdate(Component car)
        {
            var c=Current;
            return c!=null && c.Cooperative && c.cars.TryGetValue(car,out var a) &&
                a.State.Driver!=0 && a.State.Driver!=c.network.Session.LocalPlayerId &&
                !GameAccess.Bool(car,"is_sitting_inside");
        }
        private static bool VehicleUpdateButton(bool value,Component car) => value && !RemoteHostUpdate(car);
        private static float VehicleUpdateAxis(float value,Component car)
        {
            if(!RemoteHostUpdate(car)) return value;
            return (float)GameAccess.Get(Current.cars[car].Drive,"steerInput");
        }
        private static bool ReplicaFuel(Component car) => Current!=null && Current.Cooperative && Current.cars.TryGetValue(car,out var a) && !a.Simulates;
        // Keep the original Update, including fuel, gauges, repair/audio/lootbox and
        // timers. Only an authenticated remote Host driver excludes local buttons
        // and isDrifting writes on the Host passenger; steering is visual-only here.
        private static IEnumerable<CodeInstruction> NativeVehicleUpdateReads(IEnumerable<CodeInstruction> instructions,ILGenerator generator)
        {
            int buttons=0,axes=0,driftWrites=0,fuelWrites=0;
            foreach(var instruction in instructions) {
                if(instruction.opcode==OpCodes.Stfld && instruction.operand is FieldInfo fuel && fuel.Name=="gasTankLeft") {
                    fuelWrites++;
                    var original=generator.DefineLabel(); var end=generator.DefineLabel();
                    var actor=new CodeInstruction(OpCodes.Ldarg_0); actor.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    yield return actor; yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SharedVehicleController),nameof(ReplicaFuel)));
                    yield return new CodeInstruction(OpCodes.Brfalse,original);
                    yield return new CodeInstruction(OpCodes.Pop); yield return new CodeInstruction(OpCodes.Pop); yield return new CodeInstruction(OpCodes.Br,end);
                    instruction.labels.Add(original); yield return instruction;
                    var finish=new CodeInstruction(OpCodes.Nop); finish.labels.Add(end); yield return finish; continue;
                }
                if(instruction.opcode==OpCodes.Stfld && instruction.operand is FieldInfo field && field.DeclaringType.Name=="PlayerScript" && field.Name=="isDrifting") {
                    driftWrites++;
                    var original=generator.DefineLabel(); var end=generator.DefineLabel();
                    var actor=new CodeInstruction(OpCodes.Ldarg_0); actor.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    yield return actor;
                    yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SharedVehicleController),nameof(RemoteHostUpdate)));
                    yield return new CodeInstruction(OpCodes.Brfalse,original);
                    yield return new CodeInstruction(OpCodes.Pop); yield return new CodeInstruction(OpCodes.Pop);
                    yield return new CodeInstruction(OpCodes.Br,end);
                    instruction.labels.Add(original); yield return instruction;
                    var finish=new CodeInstruction(OpCodes.Nop); finish.labels.Add(end); yield return finish;
                    continue;
                }
                yield return instruction;
                if(instruction.opcode!=OpCodes.Callvirt || !(instruction.operand is MethodInfo method) || method.DeclaringType.FullName!="Rewired.Player") continue;
                string filter;
                if(method.Name=="GetButtonDown" || method.Name=="GetButton") { buttons++; filter=nameof(VehicleUpdateButton); }
                else if(method.Name=="GetAxis") { axes++; filter=nameof(VehicleUpdateAxis); }
                else continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SharedVehicleController),filter));
            }
            if(buttons!=6 || axes!=4 || driftWrites!=2 || fuelWrites!=1) throw new InvalidOperationException("Native vehicle Update contract changed: buttons="+buttons+" axes="+axes+" actorWrites="+driftWrites+" fuelWrites="+fuelWrites);
        }
    }
}
