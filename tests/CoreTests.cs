using System;
using System.Collections;
using System.Collections.Generic;
using RadioEnhancements;

internal static class CoreTests
{
    private static int assertions;
    private static void Check(bool condition, string reason)
    {
        ++assertions;
        if (!condition) throw new Exception("FAILED: " + reason);
    }
    private static void Title(MusicLibraryTracker tracker, object owner, object clip, string expected)
    {
        string actual;
        bool found = tracker.TryGetTitle(owner, clip, out actual);
        Check(expected == null ? !found : found && actual == expected, "mapped title: " + expected);
    }
    private static IEnumerator Append(IList list, object clip, object yieldToken)
    {
        yield return yieldToken;
        list.Add(clip);
    }
    private static IEnumerator Wrap(MusicLibraryTracker tracker, object owner, IList list, object clip, string path)
    {
        return tracker.Observe(owner, path, Append(list, clip, "wait"), delegate { return list; }, null);
    }
    private static void Complete(IEnumerator iterator)
    {
        while (iterator.MoveNext()) { }
    }
    private sealed class Probe : IEnumerator, IDisposable
    {
        internal Func<bool> Step;
        internal object Value;
        internal bool Disposed;
        internal readonly Exception ResetError = new NotSupportedException("native reset");
        public bool MoveNext() { return Step(); }
        public object Current { get { return Value; } }
        public void Reset() { throw ResetError; }
        public void Dispose() { Disposed = true; }
    }

    private static void CompletionAndGeneration()
    {
        MusicLibraryTracker tracker = new MusicLibraryTracker();
        object owner = new object(), first = new object(), second = new object();
        IList list = new ArrayList();
        tracker.Reset(owner);
        IEnumerator a = Wrap(tracker, owner, list, first, @"C:\music\First.mp3");
        IEnumerator b = Wrap(tracker, owner, list, second, @"C:\music\Second.ogg");
        Check(a.MoveNext() && b.MoveNext(), "both native loads yield");
        Check((string)a.Current == "wait", "native yield object forwarded");
        Check(!b.MoveNext() && !a.MoveNext(), "native completion forwarded");
        Check(ReferenceEquals(list[0], second), "completion order differs from request order");
        Title(tracker, owner, first, "First"); Title(tracker, owner, second, "Second");
        object stale = new object();
        IEnumerator old = Wrap(tracker, owner, list, stale, "stale.wav");
        old.MoveNext();
        tracker.Reset(owner); list.Clear();
        object fresh = new object();
        Complete(Wrap(tracker, owner, list, fresh, "fresh.mp3"));
        old.MoveNext();
        Title(tracker, owner, first, null); Title(tracker, owner, stale, null); Title(tracker, owner, fresh, "fresh");
        object anotherOwner = new object();
        Title(tracker, anotherOwner, fresh, null);
        tracker.Reset(anotherOwner);
        Title(tracker, owner, fresh, "fresh");
        // Simulate a full 95-song library completing in reverse order.
        tracker.Reset(owner); list.Clear();
        List<IEnumerator> pending = new List<IEnumerator>();
        object[] clips = new object[95];
        for (int i = 0; i < clips.Length; ++i)
        {
            clips[i] = new object();
            pending.Add(Wrap(tracker, owner, list, clips[i], "song" + i + ".mp3"));
            pending[i].MoveNext();
        }
        for (int i = pending.Count - 1; i >= 0; --i) pending[i].MoveNext();
        for (int i = 0; i < clips.Length; ++i) Title(tracker, owner, clips[i], "song" + i);
    }

    private static void FailureAndAmbiguity()
    {
        MusicLibraryTracker tracker = new MusicLibraryTracker();
        object owner = new object(), clip = new object();
        IList list = new ArrayList();
        Probe native = new Probe();
        Exception nativeError = new InvalidOperationException("native failure");
        native.Step = delegate { list.Add(clip); throw nativeError; };
        IEnumerator wrapped = tracker.Observe(owner, "wrong.mp3", native, delegate { return list; }, null);
        try { wrapped.MoveNext(); Check(false, "must propagate native error"); }
        catch (Exception error) { Check(ReferenceEquals(error, nativeError), "same native exception"); }
        Title(tracker, owner, clip, null);
        object token = new object(); native.Value = token;
        Check(ReferenceEquals(wrapped.Current, token), "same Current identity");
        try { wrapped.Reset(); Check(false, "reset must propagate"); }
        catch (Exception error) { Check(ReferenceEquals(error, native.ResetError), "same reset exception"); }
        ((IDisposable)wrapped).Dispose(); Check(native.Disposed, "native disposal forwarded");
        // Broken bookkeeping must still call the native iterator, even if diagnostics also fail.
        int steps = 0;
        native.Step = delegate { ++steps; return false; };
        wrapped = tracker.Observe(owner, "safe.mp3", native, delegate { throw new Exception("bookkeeping"); }, delegate { throw new Exception("logger"); });
        Check(!wrapped.MoveNext() && steps == 1, "observer exception preserves native execution");
        Check(!wrapped.MoveNext() && steps == 2, "failed observer remains disabled");
        int reads = 0, warnings = 0;
        native.Step = delegate { ++steps; list.Add(new object()); return true; };
        wrapped = tracker.Observe(owner, "after.mp3", native, delegate
        {
            if (++reads == 2) throw new Exception("after-step bookkeeping");
            return list;
        }, delegate { ++warnings; });
        Check(wrapped.MoveNext() && steps == 3, "after-step failure preserves native yield result");
        Check(wrapped.MoveNext() && steps == 4 && reads == 2 && warnings == 1, "after-step failure disables only observation");

        list.Clear(); object other = new object();
        native.Step = delegate { list.Add(clip); list.Add(other); return false; };
        Complete(tracker.Observe(owner, "ambiguous.mp3", native, delegate { return list; }, null));
        Title(tracker, owner, clip, null); Title(tracker, owner, other, null);
        list.Clear(); list.Add(other);
        native.Step = delegate { list[0] = new object(); list.Add(clip); return false; };
        Complete(tracker.Observe(owner, "mutated.mp3", native, delegate { return list; }, null));
        Title(tracker, owner, clip, null);
        // Replacing the list during a step is not an append to the observed list.
        native.Step = delegate { list = new ArrayList { new object(), clip, new object() }; return false; };
        Complete(tracker.Observe(owner, "replaced.mp3", native, delegate { return list; }, null));
        Title(tracker, owner, clip, null);
        tracker.Reset(owner); list.Clear();
        Complete(Wrap(tracker, owner, list, clip, "one.mp3"));
        Complete(Wrap(tracker, owner, list, clip, "two.mp3"));
        Complete(Wrap(tracker, owner, list, clip, "one.mp3"));
        Title(tracker, owner, clip, null);
        tracker.Reset(owner); list.Clear();
        native.Step = delegate { return false; };
        Complete(tracker.Observe(owner, "failed.mp3", native, delegate { return list; }, null));
        Title(tracker, owner, clip, null);
    }

    private static void TitlesAndDisplay()
    {
        Check(TrackDisplayController.TitleFromPath(@"C:\private\The Strokes - Reptilia.mp3") == "The Strokes - Reptilia", "filename only");
        Check(TrackDisplayController.TitleFromPath("/music/日本語 – café.OGG") == "日本語 – café", "Unicode survives");
        Check(TrackDisplayController.TitleFromPath("song.mix.wav") == "song.mix", "only last extension");
        Check(TrackDisplayController.TitleFromPath("no extension") == "no extension", "extension optional");
        Check(TrackDisplayController.TitleFromPath(".mp3") == null, "empty stem fallback");
        Check(TrackDisplayController.TitleFromPath(null) == null, "missing path fallback");
        Check(TrackDisplayController.TitleFromPath("</noparse><b>.mp3").IndexOf('<') < 0, "no TMP markup injection");
        Check(TrackDisplayController.TitleFromPath("a\nb.mp3") == "a b", "control characters cleaned");
        Check(TrackDisplayController.TitleFromPath(new string('x', 300) + ".mp3").Length == 300, "long title retained");
        MusicLibraryTracker tracker = new MusicLibraryTracker();
        object owner = new object(), a = new object(), b = new object();
        IList list = new ArrayList();
        Complete(Wrap(tracker, owner, list, a, "a.mp3"));
        Complete(Wrap(tracker, owner, list, b, "b.ogg"));
        object duplicateOne = new object(), duplicateTwo = new object();
        Complete(Wrap(tracker, owner, list, duplicateOne, @"C:\first\same.mp3"));
        Complete(Wrap(tracker, owner, list, duplicateTwo, @"C:\second\same.mp3"));
        Title(tracker, owner, duplicateOne, "same"); Title(tracker, owner, duplicateTwo, "same");
        string title;
        Check(TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, "CD TRACK 01", new[] { a, a }, out title) && title == "a", "matching speakers display title");
        Check(TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 1, "CD TRACK 02", new[] { b }, out title) && title == "b", "next track title");
        Check(TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, "CD TRACK 01", new[] { a }, out title) && title == "a", "previous or random track title");
        foreach (string text in new[] { "VOLUME 50", "- - -", "NO DISC", "custom station", "CD TRACK 02" })
            Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, text, new[] { a }, out title), "preserve nonmatching display " + text);
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, false, 1, 0, "CD TRACK 01", new[] { a }, out title), "power off");
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 0, 0, "CD TRACK 01", new[] { a }, out title), "FM mode");
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, -1, "CD TRACK 00", new[] { a }, out title), "negative index");
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 4, "CD TRACK 05", new[] { a }, out title), "index equal count");
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, "CD TRACK 01", new[] { b }, out title), "wrong clip");
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, "CD TRACK 01", new[] { a, b }, out title), "conflicting speakers");
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, "CD TRACK 01", new object[] { a, null }, out title), "missing speaker clip");
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, "CD TRACK 01", new object[0], out title), "no speakers");
        tracker.Reset(owner);
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, "CD TRACK 01", new[] { a }, out title), "generation invalidation");
    }

    private static void OwnershipSequences()
    {
        MusicLibraryTracker tracker = new MusicLibraryTracker();
        DisplayOwnership ownership = new DisplayOwnership();
        object owner = new object(), furniture = new object(), a = new object(), b = new object();
        IList list = new ArrayList();
        Complete(Wrap(tracker, owner, list, a, "A.mp3"));
        string nativeLabel = "CD TRACK 01", text;
        Check(TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, nativeLabel, new[] { a }, out text), "initial A display");
        ownership.Record(furniture, owner, text, nativeLabel);
        bool powered = false;
        // Rebuilding while powered off must remove the old filename before any re-power.
        tracker.Reset(owner);
        Check(ownership.TargetsFor(owner).Length == 1, "rebuild locates powered-off owned radio");
        Check(ownership.TargetsFor(new object()).Length == 0, "other manager cannot invalidate radio");
        string fallback;
        Check(ownership.TryGetFallback(furniture, text, out fallback) && fallback == nativeLabel, "restore exact owned A to vanilla label");
        text = fallback;
        ownership.Record(furniture, owner, text, fallback);
        list.Clear();
        Complete(Wrap(tracker, owner, list, b, "B.mp3"));
        Check(text == nativeLabel, "rebuild never leaves filename A");
        powered = true;
        bool owned = ownership.TryGetFallback(furniture, text, out fallback);
        Check(DisplayOwnership.CanRefresh(powered, true, text, nativeLabel, owned), "re-power permits native refresh");
        ownership.Forget(furniture); // Native UpdateScreen supersedes the prior lease.
        Check(TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, nativeLabel, new[] { b }, out text) && text == "B", "re-power resolves actual newly assigned B");
        ownership.Record(furniture, owner, text, nativeLabel);
        // A subsequent selected clip without a mapping must receive native text, not retain B.
        list[0] = new object();
        Check(ownership.TryGetFallback(furniture, text, out fallback), "old B is still owned before selection");
        Check(DisplayOwnership.CanRefresh(true, true, text, nativeLabel, true), "selection allows owned text refresh");
        ownership.Forget(furniture);
        string unknownTitle;
        Check(!TrackDisplayController.TryResolve(tracker, owner, list, true, 1, 0, nativeLabel, new[] { list[0] }, out unknownTitle), "unmapped selected clip keeps native fallback");
        text = nativeLabel;
        Check(!ownership.TryGetFallback(furniture, text, out fallback), "unmapped native text owns no filename");
        foreach (string replacement in new[] { "VOLUME 50", "- - -", "another mod title", "100.9" })
        {
            ownership.Record(furniture, owner, "B", nativeLabel);
            Check(!ownership.TryGetFallback(furniture, replacement, out fallback), "another writer immediately loses lease: " + replacement);
            Check(!DisplayOwnership.CanRefresh(true, true, replacement, nativeLabel, false), "selection preserves another writer: " + replacement);
            Check(!ownership.TryGetFallback(furniture, "B", out fallback), "old text reappearing cannot revive lost lease");
        }
        Check(!DisplayOwnership.CanRefresh(false, true, "B", nativeLabel, true), "power-off never invokes native refresh");
        Check(!DisplayOwnership.CanRefresh(true, false, "B", nativeLabel, true), "FM never invokes native refresh");
        Check(DisplayOwnership.CanRefresh(true, true, nativeLabel, nativeLabel, false), "current native label may refresh without prior ownership");
        foreach (string label in new[] { "CD TRACK 01", "CD TRACK 123", "NO DISC" })
            Check(DisplayOwnership.IsNativeCdLabel(label), "shared selection recognizes earlier native label: " + label);
        foreach (string label in new[] { "CD TRACK 00", "CD TRACK 1", "CD TRACK 0001", "CD TRACK 01 custom", "VOLUME 50", "- - -", "foreign title", "100.9", null })
            Check(!DisplayOwnership.IsNativeCdLabel(label), "shared selection preserves non-native text: " + label);
    }

    public static int Main()
    {
        CompletionAndGeneration(); FailureAndAmbiguity(); TitlesAndDisplay(); OwnershipSequences(); PlaybackPolicyTests.Run();
        Console.WriteLine("PASS: " + assertions + " automated core assertions (no Unity playback).");
        return 0;
    }
}
