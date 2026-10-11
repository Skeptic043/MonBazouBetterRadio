using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;

namespace RadioEnhancements
{
    internal sealed class StartupLoadingHooks
    {
        internal readonly MethodInfo LoadingSave;
        private readonly Type managerType, screenType;
        private readonly FieldInfo blackScreen, loadingText;
        private readonly ConditionalWeakTable<object, object> observed = new ConditionalWeakTable<object, object>();
        private sealed class Entry { internal WeakReference Save; internal StartupLoadingSequence Sequence; }
        // Retain an active gate until cleanup, even if Unity abandons its coroutine without Dispose.
        private readonly List<Entry> sequences = new List<Entry>();
        internal StartupLoadingHooks()
        {
            Type saveType = GameHooks.RequiredType("SaveSystem");
            managerType = GameHooks.RequiredType("SOUND_Manager");
            screenType = GameHooks.RequiredType("UI_BlackScreenWaitForSaveSystem");
            LoadingSave = GameHooks.Method(saveType, "LoadingSave", typeof(IEnumerator));
            blackScreen = GameHooks.Field(screenType, "BlackScreen", typeof(CanvasGroup), false);
            loadingText = GameHooks.Field(screenType, "Text_LoadingInformations", typeof(TextMeshProUGUI), false);
        }
        internal IEnumerator Observe(object saveSystem, IEnumerator original, LoadingCoordinator loading, GameHooks hooks, Action<Exception> report)
        {
            if (original == null || !GameHooks.Alive(saveSystem)) return original;
            object prior;
            if (observed.TryGetValue(saveSystem, out prior)) return original;
            observed.Add(saveSystem, new object());
            WeakReference save = new WeakReference(saveSystem);
            object owner = null;
            TextMeshProUGUI label = null;
            string baseline = null, written = null;
            int displayed = -1;
            long generation = 0;
            bool ownsBatch = false;
            StartupTimeScaleOwnership timeScale = new StartupTimeScaleOwnership(delegate { return Time.timeScale; }, delegate(float value) { Time.timeScale = value; });
            Func<LoadingCoordinator.LoadingStatus> status = delegate { return loading.GetStatus(owner); };
            Action cancel = delegate
            {
                if (ownsBatch) loading.Cancel(owner, generation);
            };
            StartupLoadingSequence sequence = new StartupLoadingSequence(original,
                delegate { return Runnable(save.Target); },
                delegate
                {
                    // Fail open if a game update or another patch changes the native setup yield.
                    if (!(original.Current is WaitForEndOfFrame) || !AudioListener.pause) return;
                    MonoBehaviour saveComponent = save.Target as MonoBehaviour;
                    if (saveComponent == null) return;
                    MonoBehaviour screen = UnityEngine.Object.FindObjectOfType(screenType) as MonoBehaviour;
                    if (screen == null || !screen.enabled || screen.gameObject.scene != saveComponent.gameObject.scene) return;
                    CanvasGroup black = blackScreen.GetValue(screen) as CanvasGroup;
                    label = loadingText.GetValue(screen) as TextMeshProUGUI;
                    if (black == null || !black.gameObject.activeInHierarchy || label == null) { label = null; return; }
                    owner = saveComponent.GetComponent(managerType);
                    if (!GameHooks.Alive(owner)) { owner = null; label = null; return; }
                    baseline = label.text;
                    LoadingCoordinator.LoadingStatus existing = status();
                    if (existing.Exists && !existing.IsComplete && existing.Pending > 0) { generation = existing.Generation; timeScale.Acquire(); return; }
                    if (hooks.Library(owner).Count > 0) { owner = null; return; }
                    timeScale.Acquire();
                    try { hooks.BuildLibrary.Invoke(owner, null); }
                    finally
                    {
                        LoadingCoordinator.LoadingStatus started = status();
                        generation = started.Generation;
                        ownsBatch = started.Exists && (!existing.Exists || started.Generation != existing.Generation);
                    }
                },
                delegate
                {
                    LoadingCoordinator.LoadingStatus current = status();
                    return GameHooks.Alive(owner) && current.Exists && current.Generation == generation && !current.IsComplete && current.Pending > 0;
                }, cancel,
                delegate
                {
                    LoadingCoordinator.LoadingStatus current = status();
                    if (label == null || displayed == current.Completed) return;
                    // Only replace text we observed or wrote, preserving an intervening mod's UI update.
                    if (label.text != (written ?? baseline)) return;
                    written = baseline + "Loading music: " + current.Completed + " / " + current.Enrolled + "\n";
                    label.text = written; displayed = current.Completed;
                },
                delegate
                {
                    timeScale.Release();
                    if (label != null && written != null && label.text == written) label.text = baseline;
                },
                delegate { return status().Completed; }, delegate { return (double)Time.realtimeSinceStartup; }, 180, report);
            sequences.RemoveAll(delegate(Entry item) { return item.Sequence.GateReleased; });
            sequences.Add(new Entry { Save = save, Sequence = sequence });
            return sequence;
        }
        internal IEnumerator ObserveIndex(object saveSystem, IEnumerator original, Action<object> begin, Action<Exception> report)
        {
            if (original == null || !GameHooks.Alive(saveSystem)) return original;
            object prior; if (observed.TryGetValue(saveSystem, out prior)) return original;
            observed.Add(saveSystem, new object());
            WeakReference save = new WeakReference(saveSystem);
            StartupLoadingSequence sequence = new StartupLoadingSequence(original,
                delegate { return Runnable(save.Target); },
                delegate
                {
                    if (!(original.Current is WaitForEndOfFrame)) return;
                    MonoBehaviour component = save.Target as MonoBehaviour;
                    if (component == null) return;
                    object owner = component.GetComponent(managerType);
                    if (GameHooks.Alive(owner)) begin(owner);
                }, delegate { return false; }, delegate { }, delegate { }, delegate { },
                delegate { return 0; }, delegate { return (double)Time.realtimeSinceStartup; }, 180, report);
            sequences.Add(new Entry { Save = save, Sequence = sequence });
            return sequence;
        }
        internal void Update(Action<Exception> report)
        {
            for (int i = sequences.Count - 1; i >= 0; --i)
            {
                Entry entry = sequences[i];
                if (!Runnable(entry.Save.Target))
                {
                    try { entry.Sequence.Dispose(); } catch (Exception error) { report(error); }
                }
                if (entry.Sequence.GateReleased) sequences.RemoveAt(i);
            }
        }
        internal void Clear()
        {
            foreach (Entry item in sequences) item.Sequence.ReleaseGate();
            sequences.Clear();
        }
        private static bool Runnable(object value)
        {
            MonoBehaviour owner = value as MonoBehaviour;
            return owner != null && owner.gameObject.activeInHierarchy;
        }
    }
}
