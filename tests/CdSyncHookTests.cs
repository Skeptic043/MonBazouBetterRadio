using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

internal static class CdSyncHookTests
{
    private static object validator;
    private static MethodInfo rewrite;
    private static int checks;
    internal static void Run(Assembly plugin, bool smoke)
    {
        Type type = plugin.GetType("RadioEnhancements.CdSyncHooks", true);
        validator = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { null, null, null, null }, null);
        rewrite = type.GetMethod("RewriteTickMode", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (string name in new[] { "Tick", "SelectPlayback", "PauseGame", "ResumeGame", "RadioStop" })
            Check(type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validator) is MethodInfo, "Validated current native surface " + name);
        MethodInfo tick = (MethodInfo)type.GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validator);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(tick), patched = Rewrite(original);
        int site = patched.FindIndex(delegate(CodeInstruction instruction) { MethodInfo method = instruction.operand as MethodInfo; return method != null && method.Name == "CdTickMode"; });
        Check(site > 0 && patched.Count == original.Count, "Single same-stack CD mode replacement");
        Check(((MethodInfo)original[site].operand).Name == "get_GetFMCD", "Caller original instructions not mutated");
        for (int i = 0; i < original.Count; i++)
        {
            if (i != site) Check(original[i].opcode == patched[i].opcode && Object.Equals(original[i].operand, patched[i].operand), "Unrelated IL unchanged " + i);
            Check(original[i].labels.SequenceEqual(patched[i].labels) && original[i].blocks.SequenceEqual(patched[i].blocks), "Labels/EH unchanged " + i);
        }
        Check(patched.Count(IsRandom) == 8, "All eight native random sites retained by sync gate");
        Reject(Alter(original, site, new CodeInstruction(OpCodes.Nop)), "missing mode getter");
        Reject(Alter(original, site + 1, new CodeInstruction(OpCodes.Ldc_R4, 0f)), "changed CD mode constant");
        Reject(Alter(original, site + 2, new CodeInstruction(OpCodes.Beq, original[site + 2].operand)), "inverted gate");
        Reject(Alter(original, site + 4, new CodeInstruction(OpCodes.Nop)), "changed CD library gate");
        List<CodeInstruction> duplicate = Clone(original); duplicate.AddRange(Clone(original)); Reject(duplicate, "duplicate CD gates");
        duplicate = Clone(original); duplicate.Add(new CodeInstruction(original[site])); Reject(duplicate, "unexpected extra mode getter");
        Type shuffle = plugin.GetType("RadioEnhancements.ShuffleHooks", true);
        object shuffleValidator = Activator.CreateInstance(shuffle, true);
        MethodInfo shuffleRewrite = shuffle.GetMethod("RewriteTick", BindingFlags.Instance | BindingFlags.NonPublic);
        List<CodeInstruction> both = Rewrite((IEnumerable<CodeInstruction>)shuffleRewrite.Invoke(shuffleValidator, new object[] { original }));
        Check(both.Count(IsRandom) == 7 && both.Count == original.Count + 1, "Composes after existing narrow shuffle rewrite");
        both = ((IEnumerable<CodeInstruction>)shuffleRewrite.Invoke(shuffleValidator, new object[] { patched })).ToList();
        Check(both.Count(IsRandom) == 7 && both.Count == original.Count + 1, "Composes before existing narrow shuffle rewrite");
        if (smoke)
        {
            Harmony harmony = new Harmony("radioenhancements.standalone.cdsynctests");
            try { harmony.Patch(tick, transpiler: new HarmonyMethod(typeof(CdSyncHookTests), "Transpiler")); }
            finally { harmony.UnpatchSelf(); }
            Check(true, "Standalone actual Harmony code generation");
        }
        Console.WriteLine("PASS: " + checks + " shared CD installed-assembly checks, exact Tick gate and native pause surfaces. No game method invoked.");
    }
    private static bool IsRandom(CodeInstruction instruction) { MethodInfo method = instruction.operand as MethodInfo; return method != null && method.Name == "GetNewRandomSong"; }
    private static List<CodeInstruction> Clone(IEnumerable<CodeInstruction> source) { return source.Select(delegate(CodeInstruction instruction) { return new CodeInstruction(instruction); }).ToList(); }
    private static List<CodeInstruction> Alter(IEnumerable<CodeInstruction> source, int index, CodeInstruction change) { List<CodeInstruction> altered = Clone(source); altered[index] = change; return altered; }
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source) { return ((IEnumerable<CodeInstruction>)rewrite.Invoke(validator, new object[] { source })).ToList(); }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source) { return Rewrite(source); }
    private static void Reject(IEnumerable<CodeInstruction> source, string message)
    {
        bool rejected = false;
        try { Rewrite(source); } catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        Check(rejected, "Rejected " + message);
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception("Shared CD: " + message); }
}
