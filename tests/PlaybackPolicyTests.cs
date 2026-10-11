using System;
using System.Collections.Generic;
using System.Globalization;
using RadioEnhancements;

internal static class PlaybackPolicyTests
{
    private static int count;
    private static void Check(bool value, string reason) { count++; if (!value) throw new Exception(reason); }
    internal static int Run()
    {
        foreach (int size in new[] { 1, 2, 7, 95 })
        {
            for (int seed = 0; seed < 12; seed++)
            {
                ShuffleSequence policy = new ShuffleSequence(seed); int current = 0;
                Check(policy.Next(size-1, size) == 0, "Sequential wraps");
                policy.Enabled = true;
                HashSet<int> heard = new HashSet<int>(); heard.Add(current);
                for (int i = 1; i < size; i++) { int next = policy.Next(current, size); Check(next != current, "Initial no repeat"); Check(heard.Add(next), "Initial bag unique"); current = next; }
                for (int cycle = 0; cycle < 4; cycle++)
                {
                    heard.Clear();
                    for (int i = 0; i < size; i++) { int next = policy.Next(current, size); Check(size == 1 || next != current, "No immediate repeats"); Check(heard.Add(next), "Full cycle unique"); current = next; }
                }
            }
        }
        ShuffleSequence history = new ShuffleSequence(2); history.Enabled = true;
        int first = history.Next(0, 5), second = history.Next(first, 5);
        Check(history.Previous(second, 5, 2) == second, "Previous at two seconds restarts");
        Check(history.Previous(second, 5, 1.99f) == first, "Previous traverses history");
        Check(history.Next(first, 5) == second, "Next after Previous traverses forward history");
        history.Reset(); Check(history.Enabled, "Bag reset preserves enabled preference");
        Check(history.Previous(3, 5, 0) == 3, "Empty history restarts safely");
        Check(history.Next(0, 0) == 0, "Empty library safe policy fallback");
        Check(history.Next(0, 1) == 0, "One track safe");
        history.Enabled = false; Check(history.Previous(0, 5, 0) == 4, "Sequential Previous wraps");
        Func<string,float> width = delegate(string value) { return StringInfo.ParseCombiningCharacters(value).Length; };
        TitleMarquee shortTitle = new TitleMarquee("Song", 4, width);
        Check(shortTitle.Frame(0) == "Song" && shortTitle.Frame(100) == "Song", "Short title always full");
        TitleMarquee longTitle = new TitleMarquee("abcdef", 3, width);
        Check(longTitle.Frame(0) == "abc" && longTitle.Frame(1.3f) == "abc", "Initial pause");
        Check(longTitle.Frame(1.41f) == "bcd", "Scroll begins");
        Check(longTitle.Frame(2.1f) == "def" && longTitle.Frame(3.1f) == "def", "End pause");
        Check(longTitle.Frame(3.47f) == "abc", "Cycle restarts");
        TitleMarquee unicode = new TitleMarquee("a\u0301\U0001F3B5bc", 2, width);
        Check(unicode.Frame(0) == "a\u0301\U0001F3B5", "Combining marks and surrogate pair kept whole");
        Check(unicode.Frame(1.41f) == "\U0001F3B5b", "Unicode scroll boundary");
        TitleMarquee tooWide = new TitleMarquee("x", 0.5f, width);
        Check(tooWide.Frame(0) == "", "A single oversized glyph cannot spill");
        Console.WriteLine("PASS: " + count + " scrolling/shuffle assertions (no Unity playback).");
        return count;
    }
}
