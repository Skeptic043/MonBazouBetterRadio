using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace RadioEnhancements
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.skeptic043.monbazou.radioenhancements";
        public const string PluginName = "Better Radio";
        public const string PluginVersion = "1.0.0";
        private static Plugin active;
        private readonly MusicLibraryTracker tracker = new MusicLibraryTracker();
        private GameHooks hooks;
        private ShuffleHooks shuffle;
        private Harmony harmony, shuffleHarmony;
        private LoadingCoordinator loading;
        private LoadingUiHooks loadingUi;
        private Harmony loadingUiHarmony;
        private ConfigEntry<int> concurrentLoads;
        private LoadingPlaybackGuard loadingGuard;
        private Harmony loadingGuardHarmony;
        private PlaybackRefreshHooks playbackRefresh;
        private Harmony playbackRefreshHarmony;
        private StartupLoadingHooks startupLoading;
        private Harmony startupHarmony;
        private CdSyncHooks cdSync;
        private Harmony cdSyncHarmony;
        private bool cdSyncReady;
        private bool customMusicReady;
        private CustomMusicLibrary customMusic;
        private ConfigEntry<bool> useCustomLoader;
        private ConfigEntry<string> musicFolders;
        private ShufflePreference shufflePreference;
        private bool formatStatusLogged;
        private ConfigEntry<bool> preloadOnGameLoad;
        private ConfigEntry<bool> reuseLibrary, logTimings;
        private int remainingWarnings = 8;
        private void Awake()
        {
            try
            {
                hooks = new GameHooks();
                useCustomLoader = Config.Bind("Music", "UseCustomLoader", true, "Index music folders and stream songs as needed. Disable to use the game's original loader. Restart the game after changing this setting.");
                musicFolders = Config.Bind("Music", "MusicFolders", "", "Optional external music folder, for example D:\\Music. Separate multiple folders with |. The native CDPlayer folder is always included. All folders are scanned recursively with UseCustomLoader enabled. Supports MP3, OGG, PCM WAV, and AAC/M4A/MP4 audio on supported Windows systems. Leave blank for native-folder-only playback.");
                shufflePreference = new ShufflePreference(Config);
                reuseLibrary = Config.Bind("Loading", "ReuseUnchangedLibrary", true, "Reuse a completely loaded library when its files and loaded clip identities are unchanged.");
                logTimings = Config.Bind("Loading", "LogTimings", false, "Log aggregate LOAD timings and cache results without music filenames.");
                preloadOnGameLoad = Config.Bind("Loading", "PreloadOnGameLoad", true, "Load custom music during the game's save/new-game loading screen before handing over control.");
                concurrentLoads = Config.Bind("Loading", "ConcurrentLoads", 1, new ConfigDescription("Maximum active native audio requests. 1 spreads request starts over frames; 0 restores native launch timing for comparison.", new AcceptableValueRange<int>(0, 16)));
                loading = new LoadingCoordinator(delegate(string message) { if (logTimings.Value) Logger.LogInfo(message); }, delegate(object value) { AudioClip clip = value as AudioClip; return clip != null && clip.length > 0; }, delegate { return Time.frameCount; });
                harmony = new Harmony(PluginGuid);
                harmony.Patch(hooks.BuildLibrary, prefix: Patch("BeforeBuild"), postfix: Patch("AfterBuildCompleted"), finalizer: Patch("AfterBuild"));
                harmony.Patch(hooks.LoadMusic, postfix: Patch("AfterLoad"));
                harmony.Patch(hooks.UpdateScreen, postfix: Patch("AfterScreen"));
                harmony.Patch(hooks.SelectPlayback, postfix: Patch("AfterSelection"));
                active = this;
                Logger.LogInfo(PluginName + " " + PluginVersion + ": filename scrolling and LOAD observation ready.");
            }
            catch (Exception error)
            {
                active = null;
                try { if (harmony != null) harmony.UnpatchSelf(); } catch (Exception cleanup) { Warn(cleanup); }
                Logger.LogError("Display hooks disabled: " + error);
                return;
            }
            try
            {
                loadingUi = new LoadingUiHooks();
                loadingUiHarmony = new Harmony(PluginGuid + ".loadingui");
                loadingUiHarmony.Patch(loadingUi.LoadCd, postfix: Patch("AfterLoadCd"));
            }
            catch (Exception error)
            {
                try { if (loadingUiHarmony != null) loadingUiHarmony.UnpatchSelf(); } catch (Exception cleanup) { Warn(cleanup); }
                loadingUi = null;
                Logger.LogWarning("Loading UI hooks unavailable; native launch timing retained: " + error);
            }
            try
            {
                loadingGuard = new LoadingPlaybackGuard();
                loadingGuardHarmony = new Harmony(PluginGuid + ".loadingguard");
                loadingGuardHarmony.Patch(loadingGuard.Tick, prefix: Patch("BeforeLoadingTick"));
                loadingGuardHarmony.Patch(loadingGuard.SelectPlayback, prefix: Patch("BeforeLoadingSelection"), postfix: Patch("AfterLoadingSelection"));
            }
            catch (Exception error)
            {
                try { if (loadingGuardHarmony != null) loadingGuardHarmony.UnpatchSelf(); } catch (Exception cleanup) { Warn(cleanup); }
                loadingGuard = null;
                Logger.LogWarning("Loading playback guard unavailable; native launch timing retained: " + error);
            }
            try
            {
                startupLoading = new StartupLoadingHooks();
                startupHarmony = new Harmony(PluginGuid + ".startup");
                startupHarmony.Patch(startupLoading.LoadingSave, postfix: Patch("AfterLoadingSave"));
            }
            catch (Exception error)
            {
                try { if (startupHarmony != null) startupHarmony.UnpatchSelf(); } catch (Exception cleanup) { Warn(cleanup); }
                startupLoading = null;
                Logger.LogWarning("Automatic music preload unavailable; manual LOAD remains available: " + error);
            }
            try
            {
                playbackRefresh = new PlaybackRefreshHooks();
                PlaybackRefreshHooks.SetDiagnostics(delegate(string message) { if (logTimings.Value) Logger.LogInfo(message); });
                playbackRefreshHarmony = new Harmony(PluginGuid + ".playbackrefresh");
                playbackRefreshHarmony.Patch(playbackRefresh.UpdateRadio, transpiler: Patch("RewriteStationRefresh"));
                PlaybackRefreshHooks.SetEnabled(true);
                Logger.LogInfo("FM station refreshes isolated from CD playback.");
            }
            catch (Exception error)
            {
                PlaybackRefreshHooks.SetEnabled(false);
                try { if (playbackRefreshHarmony != null) playbackRefreshHarmony.UnpatchSelf(); } catch (Exception cleanup) { Warn(cleanup); }
                playbackRefresh = null;
                PlaybackRefreshHooks.SetDiagnostics(null);
                Logger.LogWarning("CD playback refresh fix unavailable: " + error);
            }
            try
            {
                shuffle = new ShuffleHooks();
                shuffle.PreferredShuffle = delegate { return shufflePreference.Value; };
                shuffleHarmony = new Harmony(PluginGuid + ".shuffle");
                // Patch Tick first: the transpiler must prove the exact CD site before controls are enabled.
                shuffleHarmony.Patch(shuffle.Tick, transpiler: Patch("RewriteTick"));
                shuffleHarmony.Patch(shuffle.Preset, postfix: Patch("AfterPreset"));
                shuffleHarmony.Patch(shuffle.Hover, postfix: Patch("AfterHover"));
                shuffleHarmony.Patch(shuffle.Next, prefix: Patch("BeforeNext"));
                shuffleHarmony.Patch(shuffle.Previous, prefix: Patch("BeforePrevious"));
                Logger.LogInfo("CD shuffle ready: preset 6 toggles shuffle. Remembered setting: " + (shufflePreference.Value ? "ON" : "OFF") + ".");
            }
            catch (Exception error)
            {
                try { if (shuffleHarmony != null) shuffleHarmony.UnpatchSelf(); } catch (Exception cleanup) { Warn(cleanup); }
                shuffle = null;
                Logger.LogWarning("Shuffle disabled independently; vanilla playback retained: " + error);
            }
            try
            {
                if (shuffle == null || loadingGuard == null) throw new InvalidOperationException("Shared CD requires the validated control and loading hooks.");
                cdSync = new CdSyncHooks(delegate(object owner, object sound)
                {
                    if (!cdSync.IsCustom(owner)) loadingGuard.AfterSelection(owner, sound, loading);
                    hooks.RefreshAfterSelection(owner, sound, true);
                }, delegate(object owner) { var status = loading.GetStatus(owner); return status.Exists && !status.IsComplete; }, Warn,
                    delegate(string message) { if (logTimings.Value) Logger.LogInfo(message); });
                cdSync.PreferredShuffle = delegate { return shufflePreference.Value; };
                cdSyncHarmony = new Harmony(PluginGuid + ".cdsync");
                cdSyncHarmony.Patch(cdSync.Tick, transpiler: Patch("RewriteCdTick"));
                cdSyncHarmony.Patch(cdSync.SelectPlayback, prefix: new HarmonyMethod(typeof(Plugin), "BeforeCdSelection") { priority = Priority.First });
                cdSyncHarmony.Patch(cdSync.RadioStop, postfix: Patch("AfterRadioStop"));
                cdSyncHarmony.Patch(cdSync.PauseGame, prefix: Patch("BeforeCdPause"), finalizer: Patch("AfterCdPause"));
                cdSyncHarmony.Patch(cdSync.ResumeGame, postfix: Patch("AfterCdResume"));
                cdSyncReady = true;
                cdSync.IsActive = isActiveAndEnabled;
                Logger.LogInfo("Shared CD playback ready: all active CD radios share track position, next/previous and shuffle.");
            }
            catch (Exception error)
            {
                if (cdSync != null) cdSync.IsActive = false;
                try { if (cdSyncHarmony != null) cdSyncHarmony.UnpatchSelf(); } catch (Exception cleanup) { Warn(cleanup); }
                Logger.LogWarning("Shared CD playback unavailable; independent playback retained: " + error);
            }
            try
            {
                if (useCustomLoader.Value && cdSyncReady && loadingUi != null && startupLoading != null)
                {
                    customMusic = new CustomMusicLibrary();
                    cdSync.EnableCustom(customMusic);
                    customMusicReady = true;
                    hooks.CustomTitle = delegate(object owner, object sound, AudioClip[] clips)
                    {
                        object identity; string title;
                        return cdSync.TryResolveTitle(owner, sound, clips, out identity, out title) ? Tuple.Create(identity, title) : null;
                    };
                    loadingUi.CustomOwner = delegate { return hooks.ExistingManager; };
                    loadingUi.CustomBusy = delegate(object owner) { return cdSync.CustomBusy(owner); };
                    loadingUi.CustomCount = delegate(object owner) { return cdSync.GetCount(owner); };
                    Logger.LogInfo("Custom music ready: recursive folder index and background streaming. Native eager audio loading bypassed.");
                }
            }
            catch (Exception error)
            {
                customMusicReady = false;
                Logger.LogWarning("Custom music loader unavailable: " + error);
            }
        }
        private void Update()
        {
            if (!ReferenceEquals(active, this) || hooks == null) return;
            if (!formatStatusLogged && MediaFoundationSupport.Detected)
            {
                formatStatusLogged = true;
                Logger.LogInfo(MediaFoundationSupport.Summary);
            }
            if (startupLoading != null) startupLoading.Update(Warn);
            try { if (cdSync != null) cdSync.UpdateAll(); } catch (Exception error) { Warn(error); }
            hooks.UpdateDisplays(tracker, Warn);
            if (loadingUi != null) loadingUi.Update(loading, hooks, Warn);
        }
        private void OnDisable()
        {
            if (cdSync != null) cdSync.IsActive = false;
            try { if (cdSync != null) cdSync.Clear(); } catch (Exception error) { Warn(error); }
            try { if (startupLoading != null) startupLoading.Clear(); } catch (Exception error) { Warn(error); }
        }
        private void OnEnable()
        {
            try { if (cdSyncReady && cdSync != null) cdSync.Activate(); } catch (Exception error) { Warn(error); }
        }
        private void OnDestroy()
        {
            if (cdSync != null) cdSync.IsActive = false;
            try { if (cdSync != null) cdSync.Clear(); } catch (Exception error) { Warn(error); }
            try { if (customMusic != null) customMusic.Dispose(); } catch (Exception error) { Warn(error); }
            PlaybackRefreshHooks.SetEnabled(false);
            try { if (startupLoading != null) startupLoading.Clear(); } catch (Exception error) { Warn(error); }
            try { if (hooks != null) hooks.RestoreDisplays(); } catch (Exception error) { Warn(error); }
            try { if (loadingUi != null) loadingUi.Clear(); } catch (Exception error) { Warn(error); }
            try { if (shuffleHarmony != null) shuffleHarmony.UnpatchSelf(); } catch (Exception error) { Warn(error); }
            try { if (cdSyncHarmony != null) cdSyncHarmony.UnpatchSelf(); } catch (Exception error) { Warn(error); }
            try { if (harmony != null) harmony.UnpatchSelf(); } catch (Exception error) { Warn(error); }
            try { if (loadingUiHarmony != null) loadingUiHarmony.UnpatchSelf(); } catch (Exception error) { Warn(error); }
            try { if (loadingGuardHarmony != null) loadingGuardHarmony.UnpatchSelf(); } catch (Exception error) { Warn(error); }
            try { if (startupHarmony != null) startupHarmony.UnpatchSelf(); } catch (Exception error) { Warn(error); }
            try { if (playbackRefreshHarmony != null) playbackRefreshHarmony.UnpatchSelf(); } catch (Exception error) { Warn(error); }
            if (ReferenceEquals(active, this)) active = null;
            PlaybackRefreshHooks.SetDiagnostics(null);
            if (loading != null) loading.Clear();
        }
        private static HarmonyMethod Patch(string name) { return new HarmonyMethod(typeof(Plugin), name) { priority = Priority.Last }; }
        private static bool BeforeBuild(object __instance, bool __runOriginal, out bool __state)
        {
            __state = false;
            Plugin plugin = active; if (plugin == null || !__runOriginal) return true;
            if (plugin.customMusicReady && plugin.cdSync.IsActive)
            {
                try
                {
                    plugin.tracker.Reset(__instance);
                    plugin.hooks.InvalidateDisplays(__instance, plugin.Warn);
                    if (plugin.shuffle != null) plugin.shuffle.ResetLibrary();
                    plugin.cdSync.BeginCustomScan(__instance, plugin.MusicRoots());
                }
                catch (Exception error) { plugin.Warn(error); }
                return false;
            }
            try
            {
                if (plugin.loading != null) plugin.loading.MaxConcurrentLoads = plugin.loadingUi == null || plugin.loadingGuard == null ? 0 : plugin.concurrentLoads.Value;
                if (plugin.loading != null && !plugin.loading.BeforeBuild(__instance, plugin.hooks.Library(__instance), Application.persistentDataPath + "/CDPlayer", plugin.reuseLibrary.Value)) return false;
                __state = true;
            }
            catch (Exception error) { plugin.Warn(error); }
            try
            {
                plugin.tracker.Reset(__instance);
                plugin.hooks.InvalidateDisplays(__instance, plugin.Warn);
                if (plugin.shuffle != null) plugin.shuffle.ResetLibrary();
                if (plugin.cdSync != null) plugin.cdSync.ResetLibrary(__instance);
            }
            catch (Exception error) { plugin.Warn(error); }
            return true;
        }
        private static void AfterBuildCompleted(object __instance, bool __state, bool __runOriginal)
        {
            Plugin plugin = active;
            if (plugin != null && plugin.loading != null && __state)
                try { plugin.loading.AfterBuild(__instance, __runOriginal ? null : new InvalidOperationException("Native LOAD skipped by another patch.")); }
                catch (Exception error) { plugin.Warn(error); }
        }
        private static Exception AfterBuild(object __instance, bool __state, Exception __exception)
        {
            Plugin plugin = active;
            if (plugin != null && plugin.loading != null && __state && __exception != null)
                try { plugin.loading.AfterBuild(__instance, __exception); }
                catch (Exception error) { plugin.Warn(error); }
            return __exception;
        }
        private static void AfterLoad(object __instance, string __0, AudioType __1, ref IEnumerator __result)
        {
            Plugin plugin = active; if (plugin == null || __result == null) return;
            object owner = __instance;
            try { __result = plugin.tracker.Observe(owner, __0, __result, delegate { return plugin.hooks.Library(owner); }, plugin.Warn); }
            catch (Exception error) { plugin.Warn(error); }
            try { if (plugin.loading != null) __result = plugin.loading.Observe(owner, __0, __1.ToString(), __result, delegate { return plugin.hooks.Library(owner); }); }
            catch (Exception error) { plugin.Warn(error); }
        }
        private static void AfterLoadCd(object __instance, ref IEnumerator __result, bool __runOriginal)
        {
            Plugin plugin = active;
            if (plugin == null || plugin.loadingUi == null || !__runOriginal || __result == null) return;
            try { __result = plugin.loadingUi.Observe(__instance, __result, plugin.loading, plugin.hooks, plugin.Warn); }
            catch (Exception error) { plugin.Warn(error); }
        }
        private static void AfterLoadingSave(object __instance, ref IEnumerator __result, bool __runOriginal)
        {
            Plugin plugin = active;
            if (plugin != null && plugin.customMusicReady && plugin.cdSync.IsActive && __runOriginal && __result != null)
            {
                try { __result = plugin.startupLoading.ObserveIndex(__instance, __result, delegate(object owner) { plugin.cdSync.BeginCustomScan(owner, plugin.MusicRoots()); }, plugin.Warn); }
                catch (Exception error) { plugin.Warn(error); }
                return;
            }
            if (plugin == null || !plugin.isActiveAndEnabled || plugin.startupLoading == null || !plugin.preloadOnGameLoad.Value
                || plugin.loadingGuard == null || plugin.loadingUi == null || !__runOriginal || __result == null) return;
            try { __result = plugin.startupLoading.Observe(__instance, __result, plugin.loading, plugin.hooks, plugin.Warn); }
            catch (Exception error) { plugin.Warn(error); }
        }
        private static IEnumerable<CodeInstruction> RewriteStationRefresh(IEnumerable<CodeInstruction> instructions)
        {
            if (active == null || active.playbackRefresh == null) throw new InvalidOperationException("Playback refresh validator missing.");
            return active.playbackRefresh.RewriteStationRefresh(instructions);
        }
        private static void BeforeLoadingTick(object __instance, bool __runOriginal)
        {
            Plugin plugin = active;
            if (plugin == null || plugin.loadingGuard == null || !__runOriginal) return;
            if (plugin.cdSync != null && plugin.cdSync.IsCustom(__instance)) return;
            try { plugin.loadingGuard.BeforeTick(__instance, plugin.loading, plugin.Warn, delegate(object sound) { return plugin.cdSync != null && plugin.cdSync.IsManaged(__instance, sound); }); } catch (Exception error) { plugin.Warn(error); }
        }
        private static void BeforeLoadingSelection(object __instance, object __0, bool __runOriginal)
        {
            Plugin plugin = active;
            if (plugin == null || plugin.loadingGuard == null || !__runOriginal) return;
            try { plugin.loadingGuard.BeforeSelection(__instance, __0, plugin.loading); } catch (Exception error) { plugin.Warn(error); }
        }
        private static void AfterLoadingSelection(object __instance, object __0, bool __runOriginal)
        {
            Plugin plugin = active;
            if (plugin == null || plugin.loadingGuard == null || !__runOriginal) return;
            try { plugin.loadingGuard.AfterSelection(__instance, __0, plugin.loading); } catch (Exception error) { plugin.Warn(error); }
        }
        private static void AfterScreen(object __instance, bool __runOriginal)
        {
            Plugin plugin = active; if (plugin == null || !__runOriginal) return;
            try { plugin.hooks.Display(__instance, plugin.tracker); } catch (Exception error) { plugin.Warn(error); }
        }
        private static void AfterSelection(object __instance, object __0, bool __runOriginal)
        {
            Plugin plugin = active; if (plugin == null || !__runOriginal) return;
            try { plugin.hooks.RefreshAfterSelection(__instance, __0); } catch (Exception error) { plugin.Warn(error); }
        }
        private static IEnumerable<CodeInstruction> RewriteTick(IEnumerable<CodeInstruction> instructions)
        {
            if (active == null || active.shuffle == null) throw new InvalidOperationException("Shuffle hook validator missing.");
            return active.shuffle.RewriteTick(instructions);
        }
        private static int ChooseAutomatic(object manager, int current, int size, object sound)
        {
            Plugin plugin = active;
            if (plugin == null || plugin.shuffle == null) return ShuffleHooks.NativeRandom(manager, current, size);
            try { return plugin.shuffle.AutoNext(manager, current, size, sound); }
            catch (Exception error) { plugin.Warn(error); return ShuffleHooks.NativeRandom(manager, current, size); }
        }
        private static void AfterPreset(object __instance, int __0, bool __runOriginal)
        {
            Plugin plugin = active; if (plugin == null || plugin.shuffle == null || !__runOriginal) return;
            try
            {
                bool enabled;
                if ((plugin.cdSync != null && plugin.cdSync.Toggle(__instance, __0, out enabled)) || plugin.shuffle.Toggle(__instance, __0, out enabled))
                {
                    // Apply the live choice even if the config cannot be written this time.
                    try { plugin.shufflePreference.Remember(enabled); } catch (Exception error) { plugin.Warn(error); }
                    if (plugin.cdSync != null) plugin.cdSync.SetShuffle(enabled);
                    plugin.hooks.ShowShuffle(__instance, enabled);
                }
            }
            catch (Exception error) { plugin.Warn(error); }
        }
        private static void AfterHover(object __instance, ref string __result, bool __runOriginal)
        {
            Plugin plugin = active; if (plugin == null || plugin.shuffle == null || !__runOriginal) return;
            try
            {
                string managed;
                if (plugin.cdSync != null && plugin.cdSync.TryHoverText(__instance, out managed)) { __result = managed; return; }
                __result = plugin.shuffle.HoverText(__instance, __result);
            }
            catch (Exception error) { plugin.Warn(error); }
        }
        private static bool BeforeNext(object __instance, object __0, bool __runOriginal) { return Seek(__instance, __0, false, __runOriginal); }
        private static bool BeforePrevious(object __instance, object __0, bool __runOriginal) { return Seek(__instance, __0, true, __runOriginal); }
        private static bool Seek(object manager, object sound, bool previous, bool runOriginal)
        {
            Plugin plugin = active; if (plugin == null || plugin.shuffle == null || !runOriginal) return true;
            if (plugin.cdSync != null)
            {
                bool custom = plugin.cdSync.OwnsCustomCd(manager, sound);
                try { if (plugin.cdSync.Seek(manager, sound, previous) || custom) return false; }
                catch (Exception error) { plugin.Warn(error); if (custom || plugin.cdSync.Mutated) return false; }
            }
            bool changed = false;
            try { return plugin.shuffle.Seek(manager, sound, previous, out changed); }
            catch (Exception error) { plugin.Warn(error); if (changed) throw; return true; }
        }
        private static IEnumerable<CodeInstruction> RewriteCdTick(IEnumerable<CodeInstruction> instructions)
        {
            if (active == null || active.cdSync == null) throw new InvalidOperationException("Shared CD validator missing.");
            return active.cdSync.RewriteTickMode(instructions);
        }
        private static float CdTickMode(object sound)
        {
            Plugin plugin = active;
            bool custom = plugin != null && plugin.cdSync != null && plugin.cdSync.CustomTickMode(sound);
            if (custom) return 0f;
            try { if (plugin != null && plugin.cdSync != null) return plugin.cdSync.TickMode(sound); }
            catch (Exception error) { plugin.Warn(error); }
            return CdSyncHooks.NativeMode(sound);
        }
        private static bool BeforeCdSelection(object __instance, object __0, bool __runOriginal)
        {
            Plugin plugin = active; if (plugin == null || plugin.cdSync == null || !__runOriginal) return true;
            bool custom = plugin.cdSync.OwnsCustomCd(__instance, __0);
            try { return !(plugin.cdSync.Select(__instance, __0) || custom); }
            catch (Exception error) { plugin.Warn(error); return !(custom || plugin.cdSync.Mutated); }
        }
        private static void AfterRadioStop()
        {
            Plugin plugin = active; if (plugin == null || plugin.cdSync == null) return;
            try { plugin.cdSync.UpdateAll(); } catch (Exception error) { plugin.Warn(error); }
        }
        private static void BeforeCdPause(object __instance, bool __runOriginal)
        {
            Plugin plugin = active; if (plugin == null || plugin.cdSync == null || !__runOriginal) return;
            try { plugin.cdSync.BeforePause(__instance); } catch (Exception error) { plugin.Warn(error); }
        }
        private static Exception AfterCdPause(object __instance, Exception __exception)
        {
            AfterCdResume(__instance); // Recover if native pause was skipped or failed before setting its paused flag.
            return __exception;
        }
        private static void AfterCdResume(object __instance)
        {
            Plugin plugin = active; if (plugin == null || plugin.cdSync == null) return;
            try { plugin.cdSync.AfterResume(__instance); } catch (Exception error) { plugin.Warn(error); }
        }
        private string[] MusicRoots()
        {
            return MusicRoots(Application.persistentDataPath + "/CDPlayer", musicFolders.Value);
        }
        internal static string[] MusicRoots(string nativeRoot, string configured)
        {
            List<string> roots = new List<string> { nativeRoot };
            foreach (string value in (configured ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = value.Trim();
                if (trimmed.Length != 0) roots.Add(trimmed);
            }
            return roots.ToArray();
        }
        private void Warn(Exception error)
        {
            if (remainingWarnings <= 0) return;
            --remainingWarnings;
            Logger.LogWarning("Better Radio operation skipped: " + error.GetType().Name + ": " + error.Message
                + (remainingWarnings == 0 ? " (Further warnings suppressed.)" : ""));
        }
    }
}
