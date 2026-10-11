using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using RadioEnhancements;

internal static class LoadingPlaybackGuardTests
{
    private static int checks;
    private static void Check(bool condition, string reason) { ++checks; if (!condition) throw new Exception(reason); }
    private static void Case(bool observed, float mode, int count, int current, bool generation, bool expected, int selected)
    {
        int assignments = 0, selections = 0, newIndex = -999; float newTime = -1;
        bool result = LoadingPlaybackGuard.Synchronize(observed, mode, count, current, generation,
            delegate(int value, float seconds) { ++assignments; newIndex = value; newTime = seconds; },
            delegate { ++selections; });
        Check(result == expected, "synchronization decision");
        Check(assignments == (expected ? 1 : 0) && selections == assignments, "assignment and native selection happen together once");
        if (expected) Check(newIndex == selected && newTime == 0f, "selected index and time match new library");
    }
    internal static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2)
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs request)
                {
                    string name = new AssemblyName(request.Name).Name + ".dll";
                    foreach (string directory in new[] { args[0], Path.GetDirectoryName(args[1]) })
                    {
                        string candidate = Path.Combine(directory, name);
                        if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
                    }
                    return null;
                };
            return Run();
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        Case(true, 1f, 0, 50, true, false, 0);
        Case(true, 1f, 1, -1, false, true, 0);
        Case(true, 1f, 1, 1, false, true, 0);
        Case(true, 1f, 2, 50, false, true, 0);
        Case(true, 1f, 2, 2, false, true, 0);
        Case(true, 1f, 2, 1, false, false, 0);
        Case(true, 1f, 2, 1, true, true, 1);
        Case(true, 1f, 1, 0, true, true, 0);
        Case(true, 1f, 1, 0, false, false, 0);
        Case(true, 0f, 2, 50, true, false, 0);
        Case(false, 1f, 2, 50, true, false, 0);
        int assigned = -1;
        Check(LoadingPlaybackGuard.Synchronize(true, 1f, 1, 50, false, delegate(int value, float seconds) { assigned = value; }, null)
            && assigned == 0, "selection prefix clamps without recursively selecting");
        float oldTime = 500f;
        Check(LoadingPlaybackGuard.Synchronize(true, 1f, 1, 0, true,
            delegate(int value, float seconds) { assigned = value; oldTime = seconds; }, null)
            && assigned == 0 && oldTime == 0f, "first selection of new generation resets valid index old time without recursive selection");
        Console.WriteLine("Loading playback guard tests passed: " + checks + " assertions.");
        return 0;
    }
}
