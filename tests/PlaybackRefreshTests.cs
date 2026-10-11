using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

// Compiled beside HookSurfaceTests. Uses the loaded candidate and exact installed game IL without Unity objects.
internal static class PlaybackRefreshTests
{
    private static object validator;
    private static MethodInfo rewrite;
    private static int checks;

    internal static void Run(Assembly plugin, bool patchSmoke)
    {
        Type type = plugin.GetType("RadioEnhancements.PlaybackRefreshHooks", true);
        validator = Activator.CreateInstance(type, true);
        rewrite = type.GetMethod("RewriteStationRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo target = (MethodInfo)type.GetField("UpdateRadio", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validator);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(target);
        List<CodeInstruction> patched = Rewrite(original);
        int inserted = patched.FindIndex(delegate(CodeInstruction item) { MethodInfo m = item.operand as MethodInfo; return m != null && m.Name == "ShouldSkipStationRefresh"; }) - 2;
        Check(inserted >= 4 && patched.Count == original.Count + 4, "exact four-instruction guard");
        Check(patched[inserted].opcode == original[inserted+1].opcode && Object.Equals(patched[inserted].operand, original[inserted+1].operand), "same radio local");
        MethodInfo getter = (MethodInfo)patched[inserted+1].operand;
        Check(getter.DeclaringType.Name == "SOUND_Radio" && getter.Name == "get_GetFMCD", "native CD mode getter");
        Check(patched[inserted+3].opcode == OpCodes.Brtrue && Object.Equals(patched[inserted+3].operand, original[inserted-1].operand), "reuse existing loop continuation");
        for (int i = 0; i < original.Count; i++)
        {
            CodeInstruction before = original[i], after = patched[i < inserted ? i : i+4];
            Check(before.opcode == after.opcode && Object.Equals(before.operand, after.operand)
                && before.labels.SequenceEqual(after.labels) && before.blocks.SequenceEqual(after.blocks), "unrelated instruction/labels/EH preserved " + i);
        }
        Check(original.Count(IsSelection) == 1 && patched.Count(IsSelection) == 1, "original native selection call retained");
        List<CodeInstruction> changed = Clone(original); changed[inserted+2] = new CodeInstruction(OpCodes.Nop); Reject(changed, "missing selection");
        changed = Clone(original); changed[inserted-3] = new CodeInstruction(OpCodes.Nop); Reject(changed, "changed station getter");
        changed = Clone(original); changed[inserted+1] = new CodeInstruction(OpCodes.Ldloc_0); Reject(changed, "different radio local");
        changed = Clone(original); changed[inserted-1] = new CodeInstruction(OpCodes.Beq, original[inserted-1].operand); Reject(changed, "inverted station branch");
        ILGenerator labels = new DynamicMethod("labels", typeof(void), Type.EmptyTypes).GetILGenerator();
        Label absent;
        do { absent = labels.DefineLabel(); } while (original[inserted+3].labels.Contains(absent));
        changed = Clone(original); changed[inserted-1].operand = absent; Reject(changed, "wrong branch destination");
        changed = Clone(original); changed.AddRange(Clone(original)); Reject(changed, "ambiguous duplicate");
        changed = Clone(original); changed.Add(new CodeInstruction(original[inserted+2])); Reject(changed, "unmatched additional selection");
        for (int i = inserted; i <= inserted+2; i++)
        {
            changed = Clone(original); changed[i].labels.Add(default(Label)); Reject(changed, "incoming label at " + i);
            changed = Clone(original); changed[i].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock)); Reject(changed, "exception boundary at " + i);
        }

        MethodInfo decision = type.GetMethod("ShouldSkipStationRefresh", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo configure = type.GetMethod("SetDiagnostics", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo enable = type.GetMethod("SetEnabled", BindingFlags.Static | BindingFlags.NonPublic);
        List<string> messages = new List<string>();
        configure.Invoke(null, new object[] { new Action<string>(messages.Add) });
        Check(!(bool)decision.Invoke(null, new object[] { 1f }), "disabled by default");
        Check(messages.Count == 0, "disabled guard emits no diagnostic");
        enable.Invoke(null, new object[] { true });
        foreach (float mode in new[] { 0f, -1f, 2f, Single.NaN, Single.PositiveInfinity }) Check(!(bool)decision.Invoke(null, new object[] { mode }), "non-CD mode preserved");
        for (int i = 0; i < 20; i++) Check((bool)decision.Invoke(null, new object[] { 1f }), "CD refresh suppressed");
        Check(messages.Count == 3 && messages[0].Contains("count 1)") && messages[1].Contains("count 10)") && messages[2].Contains("count 20)"), "bounded diagnostic counts");
        configure.Invoke(null, new object[] { new Action<string>(delegate { throw new Exception("diagnostic failure"); }) });
        Check((bool)decision.Invoke(null, new object[] { 1f }), "diagnostic exceptions isolated");
        enable.Invoke(null, new object[] { false });
        Check(!(bool)decision.Invoke(null, new object[] { 1f }), "rollback makes guard transparent");
        enable.Invoke(null, new object[] { true });
        Check((bool)decision.Invoke(null, new object[] { 1f }), "guard can be reenabled");
        enable.Invoke(null, new object[] { false });
        configure.Invoke(null, new object[] { null });

        if (patchSmoke)
        {
            Harmony smoke = new Harmony("radioenhancements.standalone.playbackrefreshtests");
            try { smoke.Patch(target, transpiler: new HarmonyMethod(typeof(PlaybackRefreshTests), "Transpiler")); }
            finally { smoke.UnpatchSelf(); }
            Check(true, "actual Harmony patch generation");
        }
        Console.WriteLine("PASS: " + checks + " playback refresh checks; installed UpdateRadio guard only, native selection and all other IL/EH preserved, incompatible sites rejected. No game method invoked.");
    }

    private static bool IsSelection(CodeInstruction code) { MethodInfo m = code.operand as MethodInfo; return m != null && m.DeclaringType.Name == "SOUND_Manager" && m.Name == "PlayStationOnThisRadio"; }
    private static List<CodeInstruction> Clone(List<CodeInstruction> source) { return source.Select(delegate(CodeInstruction item) { return new CodeInstruction(item); }).ToList(); }
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source) { return ((IEnumerable<CodeInstruction>)rewrite.Invoke(validator, new object[] { source })).ToList(); }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source) { return Rewrite(source); }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Playback refresh: " + message); }
    private static void Reject(List<CodeInstruction> source, string message)
    {
        bool rejected = false;
        try { Rewrite(source); } catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        Check(rejected, "reject " + message);
    }
}
