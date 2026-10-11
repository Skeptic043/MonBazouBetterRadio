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
            CheckWeakIdentityStore(plugin);
            CheckMusicRoots(plugin);
            Console.WriteLine("PASS: shuffle persistence, weak reference-identity state, reset, and music-root normalization checks.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static object Create(Type type, object config)
    { return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { config }, null); }
    private static bool Value(Type type, object instance)
    { return (bool)type.GetProperty("Value", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance, null); }
    private static void Remember(Type type, object instance, bool value)
    { type.GetMethod("Remember", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, new object[] { value }); }
    private static void CheckWeakIdentityStore(Assembly plugin)
    {
        Type open = plugin.GetType("RadioEnhancements.WeakIdentityStateStore`1", true);
        Type type = open.MakeGenericType(typeof(object));
        object store = Activator.CreateInstance(type, true);
        MethodInfo get = type.GetMethod("GetOrAdd", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo find = type.GetMethod("TryGetValue", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo clear = type.GetMethod("Clear", BindingFlags.Instance | BindingFlags.NonPublic);
        EqualKey first = new EqualKey(), second = new EqualKey();
        object firstState = new object(), secondState = new object();
        object actual = get.Invoke(store, new object[] { first, new Func<object>(delegate { return firstState; }) });
        Check(Object.ReferenceEquals(firstState, actual), "First reference creates its state");
        actual = get.Invoke(store, new object[] { first, new Func<object>(delegate { throw new Exception("Existing reference recreated"); }) });
        Check(Object.ReferenceEquals(firstState, actual), "Same reference reuses state");
        actual = get.Invoke(store, new object[] { second, new Func<object>(delegate { return secondState; }) });
        Check(Object.ReferenceEquals(secondState, actual), "Equal-looking distinct reference receives independent state");
        object[] lookup = { first, null };
        Check((bool)find.Invoke(store, lookup) && Object.ReferenceEquals(firstState, lookup[1]), "Reference lookup returns existing state");
        clear.Invoke(store, null);
        lookup = new object[] { first, null };
        Check(!(bool)find.Invoke(store, lookup), "Library reset discards fallback state");
    }
    private static void CheckMusicRoots(Assembly plugin)
    {
        MethodInfo roots = plugin.GetType("RadioEnhancements.Plugin", true).GetMethod("MusicRoots", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(string) }, null);
        string[] result = (string[])roots.Invoke(null, new object[] { "native", "  D:\\Music  | | D:\\Test Music " });
        Check(result.Length == 3 && result[0] == "native" && result[1] == "D:\\Music" && result[2] == "D:\\Test Music", "Native root retained and configured roots trimmed");
    }
    private sealed class EqualKey
    {
        public override bool Equals(object obj) { return obj is EqualKey; }
        public override int GetHashCode() { return 1; }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
