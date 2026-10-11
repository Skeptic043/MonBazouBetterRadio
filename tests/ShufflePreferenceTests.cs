using System;
using System.IO;
using System.Reflection;

internal static class ShufflePreferenceTests
{
    internal static void Run(Assembly plugin, Assembly bepinex)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BetterRadio-shuffle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "preference.cfg");
            Type configType = bepinex.GetType("BepInEx.Configuration.ConfigFile", true);
            Type preferenceType = plugin.GetType("RadioEnhancements.ShufflePreference", true);
            object config = Activator.CreateInstance(configType, new object[] { path, false });
            configType.GetProperty("SaveOnConfigSet").SetValue(config, false, null);
            object preference = Create(preferenceType, config);
            Check(!Value(preferenceType, preference), "Fresh preference is sequential");
            Remember(preferenceType, preference, true);
            config = Activator.CreateInstance(configType, new object[] { path, false });
            preference = Create(preferenceType, config);
            Check(Value(preferenceType, preference), "Shuffle survives actual config save and new configuration instance");
            Remember(preferenceType, preference, false);
            preference = Create(preferenceType, Activator.CreateInstance(configType, new object[] { path, false }));
            Check(!Value(preferenceType, preference), "Sequential choice also survives reload");
            Console.WriteLine("PASS: 3 shuffle preference persistence checks using real BepInEx ConfigFile.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static object Create(Type type, object config)
    { return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { config }, null); }
    private static bool Value(Type type, object instance)
    { return (bool)type.GetProperty("Value", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance, null); }
    private static void Remember(Type type, object instance, bool value)
    { type.GetMethod("Remember", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, new object[] { value }); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
