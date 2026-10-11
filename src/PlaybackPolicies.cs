using System;
using System.Collections.Generic;
using System.Globalization;

namespace RadioEnhancements
{
    internal sealed class TitleMarquee
    {
        private readonly string[] frames;
        internal TitleMarquee(string title, float width, Func<string, float> measure)
        {
            int[] starts = StringInfo.ParseCombiningCharacters(title);
            List<string> windows = new List<string>();
            if (measure(title) <= width || starts.Length == 0) windows.Add(title);
            else
            {
                for (int first = 0; first < starts.Length; first++)
                {
                    // Grow using complete Unicode text elements; never cut surrogate pairs/combining sequences.
                    int end = first + 1;
                    while (end < starts.Length && measure(title.Substring(starts[first], (end + 1 == starts.Length ? title.Length : starts[end + 1]) - starts[first])) <= width) end++;
                    string window = title.Substring(starts[first], (end == starts.Length ? title.Length : starts[end]) - starts[first]);
                    windows.Add(measure(window) <= width ? window : (measure("…") <= width ? "…" : ""));
                    if (end == starts.Length) break;
                }
            }
            frames = windows.ToArray();
        }
        internal string Frame(float elapsed)
        {
            if (frames.Length == 1) return frames[0];
            const float pause = 1.4f, step = 0.22f;
            float cycle = pause * 2 + (frames.Length - 1) * step;
            float position = Math.Max(0, elapsed) % cycle;
            int index = position <= pause ? 0 : Math.Min(frames.Length - 1, (int)((position - pause) / step) + 1);
            return frames[index];
        }
    }

    internal sealed class ShuffleSequence
    {
        private readonly Random random;
        private readonly List<int> bag = new List<int>();
        private readonly List<int> history = new List<int>();
        private int cursor, count = -1;
        internal bool Enabled;
        internal int Generation = 0;
        internal ShuffleSequence(int seed) { random = new Random(seed); }
        internal void Reset() { bag.Clear(); history.Clear(); cursor = 0; count = -1; }
        private void Sync(int current, int size)
        {
            if (count != size || history.Count == 0 || history[cursor] != current)
            {
                Reset(); count = size; history.Add(current);
                for (int i = 0; i < size; i++) if (i != current) bag.Add(i);
            }
        }
        internal int Next(int current, int size)
        {
            if (size <= 1) return 0;
            if (!Enabled) return (current + 1) % size;
            Sync(current, size);
            if (cursor + 1 < history.Count) return history[++cursor];
            if (bag.Count == 0)
            {
                // A new cycle includes every track, including the current one later in the cycle.
                for (int i = 0; i < size; i++) bag.Add(i);
            }
            int pick;
            do { pick = random.Next(bag.Count); } while (bag.Count > 1 && bag[pick] == current);
            int selected = bag[pick]; bag.RemoveAt(pick);
            history.Add(selected); cursor++;
            if (history.Count > 256) { history.RemoveAt(0); cursor--; }
            return selected;
        }
        internal int Previous(int current, int size, float seconds)
        {
            if (size <= 1) return 0;
            if (seconds >= 2f) return current;
            if (!Enabled) return (current + size - 1) % size;
            Sync(current, size);
            return cursor > 0 ? history[--cursor] : current;
        }
    }
}
