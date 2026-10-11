using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace RadioEnhancements
{
    // Native LoadCD still runs unchanged. Its final step hides the indicator before asynchronous
    // decoding finishes; the wrapper immediately restores it for our observed pending batch.
    internal sealed class LoadingUiHooks
    {
        internal readonly MethodInfo LoadCd;
        private readonly FieldInfo spinner, count;
        private readonly List<Entry> entries = new List<Entry>();
        internal Func<object> CustomOwner;
        internal Func<object, bool> CustomBusy;
        internal Func<object, int> CustomCount;
        private sealed class Entry { internal WeakReference Menu, Owner; internal bool OwnsSpinner; }
        internal LoadingUiHooks()
        {
            Type menu = GameHooks.RequiredType("EscapeMenu_GeneralV2");
            LoadCd = GameHooks.Method(menu, "LoadCD", typeof(IEnumerator));
            spinner = GameHooks.Field(menu, "CDPlayer_Loading", typeof(GameObject), false);
            count = GameHooks.Field(menu, "CDPlayer_SongLoaded", typeof(TextMeshProUGUI), false);
        }
        internal IEnumerator Observe(object menu, IEnumerator original, LoadingCoordinator loading, GameHooks hooks, Action<Exception> report)
        {
            entries.RemoveAll(delegate(Entry entry) { return !GameHooks.Alive(entry.Menu.Target) || ReferenceEquals(entry.Menu.Target, menu); });
            Entry observed = new Entry { Menu = new WeakReference(menu) };
            entries.Add(observed);
            return new Observed(original, delegate
            {
                object owner = CustomOwner == null ? loading.CurrentOwner : CustomOwner();
                if (!GameHooks.Alive(menu) || !GameHooks.Alive(owner)) return;
                observed.Owner = new WeakReference(owner);
                if (!entries.Contains(observed)) entries.Add(observed);
                Update(loading, hooks, report);
            }, report);
        }
        internal void Update(LoadingCoordinator loading, GameHooks hooks, Action<Exception> report)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i]; object menu = entry.Menu.Target, owner = entry.Owner == null ? null : entry.Owner.Target;
                if (!GameHooks.Alive(menu)) { entries.RemoveAt(i); continue; }
                if (entry.Owner == null) continue; // Native first yield precedes BuildPlayerMusicList.
                if (!GameHooks.Alive(owner)) { entries.RemoveAt(i); continue; }
                try
                {
                    MonoBehaviour component = (MonoBehaviour)menu;
                    if (!component.gameObject.activeInHierarchy) continue; // Never reopen the paused/hidden menu.
                    LoadingCoordinator.LoadingStatus status = loading.GetStatus(owner);
                    GameObject indicator = (GameObject)spinner.GetValue(menu);
                    TextMeshProUGUI label = (TextMeshProUGUI)count.GetValue(menu);
                    bool busy = CustomBusy == null ? status.Exists && !status.IsComplete && status.Pending > 0 : CustomBusy(owner);
                    if (indicator != null && (busy || entry.OwnsSpinner)) indicator.SetActive(busy);
                    entry.OwnsSpinner = busy;
                    if (label != null) label.text = (CustomCount == null ? hooks.Library(owner).Count : CustomCount(owner)).ToString();
                    if (!busy) entries.RemoveAt(i);
                }
                catch (Exception error) { entries.RemoveAt(i); report(error); }
            }
        }
        internal void Clear()
        {
            foreach (Entry entry in entries)
            {
                object menu = entry.Menu.Target;
                if (!entry.OwnsSpinner || !GameHooks.Alive(menu)) continue;
                GameObject indicator = (GameObject)spinner.GetValue(menu);
                if (indicator != null) indicator.SetActive(false);
            }
            entries.Clear();
        }
        private sealed class Observed : IEnumerator, IDisposable
        {
            private readonly IEnumerator original;
            private readonly Action finish;
            private readonly Action<Exception> report;
            private int steps;
            internal Observed(IEnumerator original, Action finish, Action<Exception> report) { this.original = original; this.finish = finish; this.report = report; }
            public object Current { get { return original.Current; } }
            public bool MoveNext()
            {
                bool more = original.MoveNext();
                // Verified native sequence: step 1 shows spinner/yields; step 2 builds/yields;
                // step 3 hides spinner/completes. Observe step 2 before menu deactivation can stop it.
                if (++steps >= 2) { try { finish(); } catch (Exception error) { report(error); } }
                return more;
            }
            public void Reset() { original.Reset(); }
            public void Dispose() { IDisposable disposable = original as IDisposable; if (disposable != null) disposable.Dispose(); }
        }
    }
}
