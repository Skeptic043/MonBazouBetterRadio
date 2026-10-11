using System;
using System.Collections.Generic;

namespace RadioEnhancements
{
    // Weak references avoid retaining scene radios or managers across scene/save changes.
    internal sealed class DisplayOwnership
    {
        private sealed class Entry
        {
            internal WeakReference Target;
            internal WeakReference Owner;
            internal string Written;
            internal string NativeLabel;
        }
        private readonly List<Entry> entries = new List<Entry>();

        internal void Record(object target, object owner, string written, string nativeLabel)
        {
            Forget(target);
            entries.Add(new Entry { Target = new WeakReference(target), Owner = new WeakReference(owner), Written = written, NativeLabel = nativeLabel });
        }
        internal void Forget(object target)
        {
            for (int i = entries.Count - 1; i >= 0; --i)
            {
                object existing = entries[i].Target.Target;
                if (existing == null || entries[i].Owner.Target == null || ReferenceEquals(existing, target)) entries.RemoveAt(i);
            }
        }
        internal bool TryGetFallback(object target, string currentText, out string nativeLabel)
        {
            nativeLabel = null;
            for (int i = entries.Count - 1; i >= 0; --i)
            {
                Entry entry = entries[i];
                if (!ReferenceEquals(entry.Target.Target, target)) continue;
                if (!String.Equals(entry.Written, currentText, StringComparison.Ordinal))
                {
                    entries.RemoveAt(i); // Another writer owns this screen now.
                    return false;
                }
                nativeLabel = entry.NativeLabel;
                return true;
            }
            return false;
        }
        internal object[] TargetsFor(object owner)
        {
            Forget(null); // Prune expired references.
            List<object> targets = new List<object>();
            foreach (Entry entry in entries)
            {
                object target = entry.Target.Target;
                if (target != null && ReferenceEquals(entry.Owner.Target, owner)) targets.Add(target);
            }
            return targets.ToArray();
        }
        internal static bool CanRefresh(bool powered, bool cdMode, string currentText, string nativeLabel, bool owned)
        {
            return powered && cdMode && (owned || String.Equals(currentText, nativeLabel, StringComparison.Ordinal));
        }
        internal static bool IsNativeCdLabel(string text)
        {
            if (text == "NO DISC") return true;
            const string prefix = "CD TRACK ";
            if (text == null || !text.StartsWith(prefix, StringComparison.Ordinal) || text.Length < prefix.Length + 2) return false;
            for (int i = prefix.Length; i < text.Length; ++i) if (text[i] < '0' || text[i] > '9') return false;
            int value;
            string suffix = text.Substring(prefix.Length);
            return Int32.TryParse(suffix, out value) && value > 0 && value.ToString("00") == suffix;
        }
    }
}
