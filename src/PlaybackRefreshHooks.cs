using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace RadioEnhancements
{
    // FM track changes refresh radios by their remembered station, even while those radios play a CD.
    // Guard only that refresh call. Power, seeking, CD completion and loading restoration keep the native selection path.
    internal sealed class PlaybackRefreshHooks
    {
        internal readonly MethodInfo UpdateRadio;
        private readonly MethodInfo select, station, mode;
        private static Action<string> diagnostics;
        private static long suppressed;
        private static bool enabled;

        internal PlaybackRefreshHooks()
        {
            Type manager = GameHooks.RequiredType("SOUND_Manager"), sound = GameHooks.RequiredType("SOUND_Radio");
            UpdateRadio = GameHooks.Method(manager, "UpdateRadio", typeof(void), typeof(float));
            select = GameHooks.Method(manager, "PlayStationOnThisRadio", typeof(void), sound);
            station = GameHooks.Property(sound, "GetStationPlaying", typeof(float)).GetGetMethod();
            mode = GameHooks.Property(sound, "GetFMCD", typeof(float)).GetGetMethod();
        }

        internal static void SetDiagnostics(Action<string> report) { diagnostics = report; suppressed = 0; }
        internal static void SetEnabled(bool value) { enabled = value; }

        internal static bool ShouldSkipStationRefresh(float currentMode)
        {
            if (!enabled || currentMode != 1f) return false;
            suppressed++;
            Action<string> report = diagnostics;
            if (report != null && (suppressed == 1 || suppressed % 10 == 0))
            {
                try { report("Prevented FM-triggered CD playback restart (session count " + suppressed + ")."); }
                catch { /* Diagnostics must never affect playback. */ }
            }
            return true;
        }

        internal IEnumerable<CodeInstruction> RewriteStationRefresh(IEnumerable<CodeInstruction> source)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(source);
            int found = -1, matches = 0, selections = 0;
            for (int i = 0; i < code.Count; i++) if (code[i].Calls(select)) selections++;
            for (int i = 4; i + 3 < code.Count; i++)
            {
                // if (radio.GetStationPlaying == station) this.PlayStationOnThisRadio(radio);
                if (!IsLocalLoad(code[i-4]) || !code[i-3].Calls(station) || code[i-2].opcode != OpCodes.Ldarg_1
                    || (code[i-1].opcode != OpCodes.Bne_Un && code[i-1].opcode != OpCodes.Bne_Un_S)
                    || code[i].opcode != OpCodes.Ldarg_0 || !SameLocal(code[i-4], code[i+1]) || !code[i+2].Calls(select)) continue;
                if (!(code[i-1].operand is Label) || !code[i+3].labels.Contains((Label)code[i-1].operand)) continue;
                // Reject entry points or EH boundaries that could bypass the guard or change stack semantics.
                bool boundary = false;
                for (int j = i; j <= i+2; j++) if (code[j].labels.Count != 0 || code[j].blocks.Count != 0) boundary = true;
                if (boundary) continue;
                found = i; matches++;
            }
            if (matches != 1 || selections != 1)
                throw new InvalidOperationException("Expected one station-filtered radio refresh call; found " + matches + " matches and " + selections + " selections. Playback refresh fix disabled.");

            // The branch rejoins the existing loop without calling Stop/PlayDelayed on CD speakers.
            // Existing instructions, including their labels and exception blocks, remain untouched.
            code.InsertRange(found, new[] {
                new CodeInstruction(code[found+1].opcode, code[found+1].operand),
                new CodeInstruction(OpCodes.Callvirt, mode),
                new CodeInstruction(OpCodes.Call, typeof(PlaybackRefreshHooks).GetMethod("ShouldSkipStationRefresh", BindingFlags.Static | BindingFlags.NonPublic)),
                new CodeInstruction(OpCodes.Brtrue, code[found-1].operand)
            });
            return code;
        }

        private static bool SameLocal(CodeInstruction first, CodeInstruction second)
        { return IsLocalLoad(second) && first.opcode == second.opcode && Object.Equals(first.operand, second.operand); }
        private static bool IsLocalLoad(CodeInstruction code)
        {
            return code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S || code.opcode == OpCodes.Ldloc_0
                || code.opcode == OpCodes.Ldloc_1 || code.opcode == OpCodes.Ldloc_2 || code.opcode == OpCodes.Ldloc_3;
        }
    }
}
