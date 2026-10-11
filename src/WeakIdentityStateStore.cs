using System;
using System.Collections.Generic;

namespace RadioEnhancements
{
    // Unity objects can override equality and hashing, so fallback state is keyed only by managed reference identity.
    internal sealed class WeakIdentityStateStore<TValue> where TValue : class
    {
        private sealed class Entry
        {
            internal readonly WeakReference Key;
            internal readonly TValue Value;
            internal Entry(object key, TValue value) { Key = new WeakReference(key); Value = value; }
        }

        private readonly List<Entry> entries = new List<Entry>();

        internal bool TryGetValue(object key, out TValue value)
        {
            if (key == null) { value = null; return false; }
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                object candidate = entries[i].Key.Target;
                if (!ReferenceEquals(candidate, key)) continue;
                value = entries[i].Value;
                return true;
            }
            value = null;
            return false;
        }

        internal TValue GetOrAdd(object key, Func<TValue> create)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (create == null) throw new ArgumentNullException("create");
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i].Key.Target == null) entries.RemoveAt(i);
            TValue value;
            if (TryGetValue(key, out value)) return value;
            value = create();
            if (value == null) throw new InvalidOperationException("State factory returned null.");
            entries.Add(new Entry(key, value));
            return value;
        }

        internal void Clear() { entries.Clear(); }
    }
}
