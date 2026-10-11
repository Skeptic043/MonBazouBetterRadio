using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace RadioEnhancements
{
    // Owner and clip comparisons deliberately use managed reference identity.
    internal sealed class MusicLibraryTracker
    {
        private readonly ConditionalWeakTable<object, Generation> owners = new ConditionalWeakTable<object, Generation>();

        internal void Reset(object owner)
        {
            owners.Remove(owner);
            owners.Add(owner, new Generation());
        }

        internal IEnumerator Observe(object owner, string path, IEnumerator original, Func<IList> library, Action<Exception> report)
        {
            Generation generation = owners.GetValue(owner, delegate { return new Generation(); });
            return new ObservedEnumerator(original, delegate
            {
                return new Snapshot(library());
            }, delegate(Snapshot before)
            {
                Generation current;
                if (!owners.TryGetValue(owner, out current) || !ReferenceEquals(current, generation)) return;
                IList after = library();
                if (!before.IsSingleAppend(after)) return;
                generation.Record(after[after.Count - 1], TrackDisplayController.TitleFromPath(path));
            }, report);
        }

        internal bool TryGetTitle(object owner, object clip, out string title)
        {
            title = null;
            Generation generation;
            return owner != null && clip != null && owners.TryGetValue(owner, out generation)
                && generation.Titles.TryGetValue(clip, out title) && !String.IsNullOrEmpty(title);
        }

        private sealed class Generation
        {
            internal readonly Dictionary<object, string> Titles = new Dictionary<object, string>(ReferenceComparer.Instance);
            internal void Record(object clip, string title)
            {
                if (clip == null) return;
                string previous;
                // Null marks a permanently ambiguous clip for this generation.
                if (Titles.TryGetValue(clip, out previous) && !String.Equals(previous, title, StringComparison.Ordinal))
                    Titles[clip] = null;
                else if (!Titles.ContainsKey(clip)) Titles.Add(clip, title);
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }

        private sealed class Snapshot
        {
            private readonly IList list;
            private readonly object[] items;
            internal Snapshot(IList list)
            {
                if (list == null) throw new InvalidOperationException("Music library unavailable.");
                this.list = list;
                items = new object[list.Count];
                list.CopyTo(items, 0);
            }
            internal bool IsSingleAppend(IList after)
            {
                if (!ReferenceEquals(list, after) || after.Count != items.Length + 1) return false;
                for (int index = 0; index < items.Length; ++index)
                    if (!ReferenceEquals(items[index], after[index])) return false;
                return true;
            }
        }

        private sealed class ObservedEnumerator : IEnumerator, IDisposable
        {
            private readonly IEnumerator original;
            private readonly Func<Snapshot> before;
            private readonly Action<Snapshot> after;
            private readonly Action<Exception> report;
            private bool observing = true;
            internal ObservedEnumerator(IEnumerator original, Func<Snapshot> before, Action<Snapshot> after, Action<Exception> report)
            {
                this.original = original; this.before = before; this.after = after; this.report = report;
            }
            public object Current { get { return original.Current; } }
            public bool MoveNext()
            {
                Snapshot snapshot = null;
                if (observing)
                {
                    try { snapshot = before(); }
                    catch (Exception error) { Disable(error); }
                }
                // Native exceptions propagate unchanged. Do not infer a mapping on a failed step.
                bool more = original.MoveNext();
                if (observing)
                {
                    try { after(snapshot); }
                    catch (Exception error) { Disable(error); }
                }
                return more;
            }
            public void Reset() { original.Reset(); }
            public void Dispose()
            {
                IDisposable disposable = original as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
            private void Disable(Exception error)
            {
                observing = false;
                try { if (report != null) report(error); } catch { }
            }
        }
    }
}
