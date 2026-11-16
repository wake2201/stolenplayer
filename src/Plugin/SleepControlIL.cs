// S1: IL rewrite of PassTime.Update for the shared sleep control (Harmony only, no Unity / plugin state, so the offline test
// runs it on the real game IL: tests/RedirectTest.cs). Verified IL 1.27c (ilspycmd): Update has exactly one
//   call bool UnityEngine.Input::GetKeyDown(KeyCode)      (Esc -> StopIt / QuitIt)
// and two
//   call(virt) instance void PassTime::StopIt()           (1st: survival energy >= 125, before the key read;
//                                                          2nd: the Esc branch after it - left unchanged, unreachable for
//                                                          the observer once the key read is gated)
// -> call keyGate(KeyCode) / call ruleGate(object) for the survival StopIt. Stack shapes are identical (KeyCode in, bool
// out / PassTime in, nothing out). Applied only when the key read occurs exactly once with exactly one StopIt before it;
// otherwise the instructions are returned unchanged (caller logs, the StopIt / QuitIt button prefixes still apply).
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace TS2MP.Plugin
{
    internal static class SleepControlIL
    {
        public static List<CodeInstruction> Rewrite(List<CodeInstruction> code, MethodInfo keyGate, MethodInfo ruleGate, out int esc, out int rule)
        {
            esc = 0; rule = 0;
            int ki = -1, si = -1;
            for (int i = 0; i < code.Count; i++)
            {
                var m = code[i].operand as MethodInfo;
                if (m == null || !(code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) || m.DeclaringType == null) continue;
                var ps = m.GetParameters();
                if (m.Name == "GetKeyDown" && m.DeclaringType.FullName == "UnityEngine.Input" && ps.Length == 1 && ps[0].ParameterType.Name == "KeyCode") { esc++; ki = i; }
                else if (m.Name == "StopIt" && m.DeclaringType.Name == "PassTime" && ps.Length == 0 && !m.IsStatic && esc == 0) { rule++; si = i; }   // only before the key read
            }
            if (esc != 1 || rule != 1) return code;
            code[ki] = new CodeInstruction(OpCodes.Call, keyGate) { labels = code[ki].labels, blocks = code[ki].blocks };
            code[si] = new CodeInstruction(OpCodes.Call, ruleGate) { labels = code[si].labels, blocks = code[si].blocks };
            return code;
        }
    }
}
