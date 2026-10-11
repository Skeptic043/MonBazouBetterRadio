using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

internal static class HookSurfaceTests
{
    private static object shuffleValidator;
    private static MethodInfo shuffleRewrite;
    public static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
    private static int Run(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("Expected plugin DLL, managed folder, BepInEx DLL and Harmony DLL, optionally dependency DLLs.");
        List<string> directories = new List<string> { args[1], Path.GetDirectoryName(args[2]), Path.GetDirectoryName(args[3]) };
        for (int i = 4; i < args.Length; i++) if (!args[i].StartsWith("--")) directories.Add(Path.GetDirectoryName(args[i]));
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request)
        {
            string name = new AssemblyName(request.Name).Name + ".dll";
            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };
        // Separate method keeps Harmony type resolution after the resolver is installed.
        return Validate(args);
    }
    private static int Validate(string[] args)
    {
        Assembly game = Assembly.LoadFrom(Path.Combine(args[1], "Assembly-CSharp.dll"));
        Assembly plugin = Assembly.LoadFrom(args[0]);
        ShufflePreferenceTests.Run(plugin, Assembly.LoadFrom(args[2]));
        HookCleanupTests.Run(plugin);
        PlaybackRefreshTests.Run(plugin, args.Contains("--patch-smoke"));
        CdSyncHookTests.Run(plugin, args.Contains("--patch-smoke"));
        Type startup = plugin.GetType("RadioEnhancements.StartupLoadingHooks", true);
        object validatedStartup = Activator.CreateInstance(startup, true);
        Console.WriteLine("Validated: startup loading coroutine and native loading-screen fields: " + startup.GetField("LoadingSave", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validatedStartup));
        Type hooks = plugin.GetType("RadioEnhancements.GameHooks", true);
        object validated = Activator.CreateInstance(hooks, true);
        foreach (string field in new[] { "BuildLibrary", "LoadMusic", "UpdateScreen", "SelectPlayback" })
        {
            MethodInfo method = (MethodInfo)hooks.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validated);
            Console.WriteLine("Validated: " + method.DeclaringType.FullName + "." + method);
        }
        Type shuffle = plugin.GetType("RadioEnhancements.ShuffleHooks", true);
        object shuffled = Activator.CreateInstance(shuffle, true);
        Type loadingUi = plugin.GetType("RadioEnhancements.LoadingUiHooks", true);
        object validatedUi = Activator.CreateInstance(loadingUi, true);
        Console.WriteLine("Validated: loading UI coroutine and spinner/count fields: " + loadingUi.GetField("LoadCd", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validatedUi));
        Type guard = plugin.GetType("RadioEnhancements.LoadingPlaybackGuard", true);
        object validatedGuard = Activator.CreateInstance(guard, true);
        Console.WriteLine("Validated: loading playback guard fields/properties/Tick/selection: " + guard.GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validatedGuard));
        foreach (string field in new[] { "Tick", "Preset", "Hover", "Next", "Previous" })
        {
            MethodInfo method = (MethodInfo)shuffle.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shuffled);
            Console.WriteLine("Validated: " + method.DeclaringType.FullName + "." + method);
        }
        MethodInfo tick = (MethodInfo)shuffle.GetField("Tick", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shuffled);
        List<CodeInstruction> original = PatchProcessor.GetOriginalInstructions(tick);
        MethodInfo rewrite = shuffle.GetMethod("RewriteTick", BindingFlags.Instance | BindingFlags.NonPublic);
        List<CodeInstruction> patched = ((IEnumerable<CodeInstruction>)rewrite.Invoke(shuffled, new object[] { original })).ToList();
        int originalRandom = original.Count(IsRandom), patchedRandom = patched.Count(IsRandom);
        if (originalRandom != 8 || patchedRandom != 7 || patched.Count != original.Count + 1) throw new Exception("Tick rewrite was not confined to the one CD call.");
        int inserted = patched.FindIndex(delegate(CodeInstruction instruction) { MethodInfo method = instruction.operand as MethodInfo; return method != null && method.Name == "ChooseAutomatic"; });
        if (inserted < 0) throw new Exception("Automatic selection bridge missing.");
        int oldSite = inserted - 1;
        for (int i = 0; i < original.Count; i++)
        {
            if (i == oldSite) continue;
            CodeInstruction before = original[i], after = patched[i < oldSite ? i : i+1];
            if (before.opcode != after.opcode || !Object.Equals(before.operand, after.operand)
                || !before.labels.SequenceEqual(after.labels) || !before.blocks.SequenceEqual(after.blocks)) throw new Exception("Unrelated IL changed.");
        }
        // A changed site must fail closed rather than patch a broader random-song method.
        List<CodeInstruction> altered = new List<CodeInstruction>(original); altered[oldSite] = new CodeInstruction(System.Reflection.Emit.OpCodes.Nop);
        bool rejected = false;
        try { rewrite.Invoke(shuffled, new object[] { altered }); } catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        if (!rejected) throw new Exception("Changed CD IL was not rejected.");
        altered = new List<CodeInstruction>(original); altered.AddRange(original);
        rejected = false;
        try { rewrite.Invoke(shuffled, new object[] { altered }); } catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        if (!rejected) throw new Exception("Ambiguous duplicate CD IL was not rejected.");
        if (args.Contains("--patch-smoke"))
        {
        shuffleValidator = shuffled; shuffleRewrite = rewrite;
        Type pluginType = plugin.GetType("RadioEnhancements.Plugin", true);
        Harmony smoke = new Harmony("radioenhancements.standalone.hooktests");
        try
        {
            MethodInfo build = (MethodInfo)hooks.GetField("BuildLibrary", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validated);
            smoke.Patch(build, prefix: Handler(pluginType, "BeforeBuild"), postfix: Handler(pluginType, "AfterBuildCompleted"), finalizer: Handler(pluginType, "AfterBuild"));
            smoke.Patch((MethodInfo)hooks.GetField("LoadMusic", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validated), postfix: Handler(pluginType, "AfterLoad"));
            smoke.Patch((MethodInfo)hooks.GetField("UpdateScreen", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validated), postfix: Handler(pluginType, "AfterScreen"));
            smoke.Patch((MethodInfo)hooks.GetField("SelectPlayback", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validated), prefix: Handler(pluginType, "BeforeLoadingSelection"), postfix: Handler(pluginType, "AfterLoadingSelection"));
            smoke.Patch((MethodInfo)hooks.GetField("SelectPlayback", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validated), postfix: Handler(pluginType, "AfterSelection"));
            smoke.Patch(tick, prefix: Handler(pluginType, "BeforeLoadingTick"), transpiler: new HarmonyMethod(typeof(HookSurfaceTests), "TestTranspiler"));
            smoke.Patch((MethodInfo)shuffle.GetField("Preset", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shuffled), postfix: Handler(pluginType, "AfterPreset"));
            smoke.Patch((MethodInfo)shuffle.GetField("Hover", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shuffled), postfix: Handler(pluginType, "AfterHover"));
            smoke.Patch((MethodInfo)shuffle.GetField("Next", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shuffled), prefix: Handler(pluginType, "BeforeNext"));
            smoke.Patch((MethodInfo)shuffle.GetField("Previous", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shuffled), prefix: Handler(pluginType, "BeforePrevious"));
            smoke.Patch((MethodInfo)loadingUi.GetField("LoadCd", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validatedUi), postfix: Handler(pluginType, "AfterLoadCd"));
            Console.WriteLine("PASS: standalone Harmony patch generation for all hook signatures and CD transpiler; no game method invoked.");
        }
        finally { smoke.UnpatchSelf(); }
        }
        Console.WriteLine("PASS: exact installed Tick IL; 7 FM/bar random calls preserved, 1 CD call replaced, all other instructions/labels/blocks unchanged, changed site rejected.");
        Console.WriteLine("Game assembly: " + game.FullName);
        Console.WriteLine("PASS: standalone hook/IL checks only; no Unity objects, game-method execution or gameplay acceptance.");
        return 0;
    }
    private static bool IsRandom(CodeInstruction instruction) { MethodInfo method = instruction.operand as MethodInfo; return method != null && method.DeclaringType.Name == "SOUND_Manager" && method.Name == "GetNewRandomSong"; }
    private static HarmonyMethod Handler(Type type, string name) { return new HarmonyMethod(type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }
    private static IEnumerable<CodeInstruction> TestTranspiler(IEnumerable<CodeInstruction> instructions) { return (IEnumerable<CodeInstruction>)shuffleRewrite.Invoke(shuffleValidator, new object[] { instructions }); }
}
