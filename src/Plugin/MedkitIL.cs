// V1.4.1: IL rewrite of the original medkit use sequence in PlayerScript.Update (Harmony only, no Unity / plugin state, so the
// offline test runs it on the real game IL: tests/RedirectTest.cs). Pattern (verified IL 1.27c, IL_72f0..IL_7328):
//   ldarg.0; call PlayerScript::Heal(); ldloca.s <hit>; ...healSound.Play()...; ...Object::Destroy(...);
//   ldstr "Medkit"; ldc.i4.1; call SPrefs::SetInt(string, int32)
// -> ldarg.0; ldloca.s <hit>; call <hook>(object, ref RaycastHit). Applied only when the pattern occurs exactly once and no
// branch jumps into it; otherwise the instructions are returned unchanged and sites != 1 (caller disables the feature).
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace TS2MP.Plugin
{
    internal static class MedkitIL
    {
        public static List<CodeInstruction> Rewrite(List<CodeInstruction> code, MethodInfo hook, out int sites)
        {
            int found = -1, end = -1, count = 0;
            for (int i = 1; i + 1 < code.Count; i++)
            {
                var mi = code[i].operand as MethodInfo;
                if (!(code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt) || mi == null || mi.Name != "Heal" || mi.GetParameters().Length != 0 || mi.DeclaringType == null || mi.DeclaringType.Name != "PlayerScript") continue;
                if (code[i - 1].opcode != OpCodes.Ldarg_0 || !(code[i + 1].opcode == OpCodes.Ldloca_S || code[i + 1].opcode == OpCodes.Ldloca)) continue;
                int j = -1;
                for (int k = i + 1; k < code.Count && k <= i + 20; k++)
                {
                    var m2 = code[k].operand as MethodInfo;
                    if (code[k].opcode == OpCodes.Call && m2 != null && m2.Name == "SetInt" && m2.DeclaringType != null && m2.DeclaringType.Name == "SPrefs"
                        && code[k - 2].opcode == OpCodes.Ldstr && (code[k - 2].operand as string) == "Medkit" && code[k - 1].opcode == OpCodes.Ldc_I4_1) { j = k; break; }
                }
                if (j < 0) continue;
                bool labelled = false;
                for (int k = i; k <= j; k++) if (code[k].labels.Count > 0) labelled = true;   // a jump into the sequence -> unsafe
                if (labelled) continue;
                count++; found = i - 1; end = j;
            }
            sites = count;
            if (count != 1 || hook == null) return code;
            var first = code[found];
            var hit = code[found + 2];   // ldloca(.s) <hit>
            var repl = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label>(first.labels), blocks = new List<ExceptionBlock>(first.blocks) },
                new CodeInstruction(hit.opcode, hit.operand),
                new CodeInstruction(OpCodes.Call, hook)
            };
            code.RemoveRange(found, end - found + 1);
            code.InsertRange(found, repl);
            return code;
        }
    }
}
