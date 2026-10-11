using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static class HookCleanupTests
{
    private sealed class Target
    {
        internal int Calls;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Build() { Calls++; }
    }
    internal static void Run(Assembly plugin)
    {
        Type type = plugin.GetType("RadioEnhancements.Plugin", true);
        MethodInfo finalizer = type.GetMethod("AfterBuild", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (ParameterInfo parameter in finalizer.GetParameters())
            if (parameter.Name == "__runOriginal") throw new Exception("LOAD finalizer still requires the removed-prefix runOriginal local.");
        Harmony prefix = new Harmony("radioenhancements.tests.cleanup.prefix");
        Harmony tail = new Harmony("radioenhancements.tests.cleanup.tail");
        MethodInfo target = typeof(Target).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance);
        try
        {
            prefix.Patch(target, prefix: new HarmonyMethod(type.GetMethod("BeforeBuild", BindingFlags.NonPublic | BindingFlags.Static)));
            tail.Patch(target, postfix: new HarmonyMethod(type.GetMethod("AfterBuildCompleted", BindingFlags.NonPublic | BindingFlags.Static)), finalizer: new HarmonyMethod(finalizer));
            prefix.UnpatchSelf(); // Regenerate while the postfix/finalizer remain, reproducing cleanup order.
            tail.UnpatchSelf();
            Console.WriteLine("PASS: LOAD hooks generate and unpatch with the prefix removed first. Synthetic CLR target only; no patched method invoked.");
        }
        finally { prefix.UnpatchSelf(); tail.UnpatchSelf(); }
    }
}
