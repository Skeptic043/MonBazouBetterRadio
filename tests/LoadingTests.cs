using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using RadioEnhancements;

internal static class LoadingTests
{
    private static int checks;
    private static void Check(bool value, string reason) { ++checks; if (!value) throw new Exception("FAILED: " + reason); }
    private static LoadingSnapshot Snap(params string[] paths)
    {
        var stamps = new List<LoadingSnapshot.FileStamp>();
        foreach (string path in paths) stamps.Add(new LoadingSnapshot.FileStamp(path, "MPEG", 100, 200));
        return new LoadingSnapshot(stamps);
    }
    private static IEnumerator Append(IList list, object clip)
    { yield return "native request"; list.Add(clip); }
    private static IEnumerator Empty() { yield return null; }
    private static void Drain(IEnumerator value) { while (value.MoveNext()) { } }
    private sealed class Probe : IEnumerator, IDisposable
    {
        internal int Calls, Disposals;
        internal Func<bool> Step;
        internal bool ThrowDispose;
        public bool MoveNext() { ++Calls; return Step == null || Step(); }
        public object Current { get { return "probe"; } }
        public void Reset() { throw new NotSupportedException(); }
        public void Dispose() { ++Disposals; if (ThrowDispose) throw new InvalidOperationException("dispose fixture"); }
    }
    private sealed class Fixture
    {
        internal object Owner = new object();
        internal IList List = new ArrayList();
        internal LoadingSnapshot Snapshot;
        internal readonly List<string> Logs = new List<string>();
        internal readonly LoadingCoordinator Loader;
        internal Fixture(params string[] paths)
        {
            Snapshot = Snap(paths);
            Loader = new LoadingCoordinator(Logs.Add, delegate(object clip) { return clip != null; }, delegate { return Snapshot; });
        }
        internal bool Begin(bool cache) { return Loader.BeforeBuild(Owner, List, "fixture", cache); }
        internal IEnumerator Start(string path, object clip)
        { return Loader.Observe(Owner, path, "MPEG", Append(List, clip), delegate { return List; }); }
        internal void Close() { Loader.AfterBuild(Owner, null); }
        internal void Successful(params string[] paths)
        {
            Check(Begin(true), "first build runs");
            List.Clear();
            foreach (string path in paths) Drain(Start(path, new object()));
            Close();
        }
    }

    private static void SuccessAndIdentity()
    {
        var f = new Fixture("a", "b");
        Check(f.Begin(true), "initial load runs");
        IEnumerator a = f.Start("a", new object()), b = f.Start("b", new object());
        Check(a.MoveNext() && (string)a.Current == "native request", "preserve native yield");
        Check(b.MoveNext(), "second concurrent request");
        Check(!b.MoveNext() && !a.MoveNext(), "reverse completion works");
        Check(f.Logs.Count == 0, "no complete report before enrollment closes");
        f.Close();
        long generation = f.Loader.GetStatus(f.Owner).Generation;
        object first = f.List[0], second = f.List[1];
        Check(!f.Begin(true), "unchanged successful repeat skips");
        Check(f.Loader.GetStatus(f.Owner).Generation == generation, "cache hit preserves generation");
        Check(ReferenceEquals(first, f.List[0]) && ReferenceEquals(second, f.List[1]), "cache leaves list unchanged");
        Check(f.Logs[0].Contains("succeeded=2") && f.Logs[0].Contains("cacheEligible=True"), "aggregate completion telemetry");
        Check(f.Logs[1].Contains("nativeStarted=0"), "cache telemetry");
        Check(f.Begin(false), "disabled cache loads normally");
        Check(f.Loader.GetStatus(f.Owner).Generation > generation, "actual rebuild creates a new generation");
        f.Loader.Clear();

        f = new Fixture("a"); f.Successful("a");
        f.List = new ArrayList(f.List);
        Check(f.Begin(true), "equal contents different list invalidates");
        f = new Fixture("a", "b"); f.Successful("a", "b");
        object temp = f.List[0]; f.List[0] = f.List[1]; f.List[1] = temp;
        Check(f.Begin(true), "clip reordering invalidates");
        f = new Fixture("a"); f.Successful("a"); f.List[0] = new object();
        Check(f.Begin(true), "clip replacement invalidates");
        f = new Fixture("a"); f.Successful("a"); f.Owner = new object();
        Check(f.Begin(true), "new owner cannot reuse old library");
        f = new Fixture("a"); f.Successful("a"); f.List[0] = null;
        Check(f.Begin(true), "dead clip invalidates");
    }

    private static void FailuresAndMutations()
    {
        var f = new Fixture("a", "b"); f.Begin(true); Drain(f.Start("a", new object())); f.Close();
        Check(f.Begin(true), "missing expected attempt prevents cache");
        f = new Fixture("a"); f.Begin(true);
        Drain(f.Loader.Observe(f.Owner, "a", "MPEG", Empty(), delegate { return f.List; })); f.Close();
        Check(f.Logs[0].Contains("failed=1"), "zero-append failure counted");
        Check(f.Begin(true), "corrupt or missing song retry allowed");
        f = new Fixture("a"); f.Begin(true); Drain(f.Start("wrong", new object())); f.Close();
        Check(f.Begin(true), "source mismatch prevents cache");
        f = new Fixture("a"); f.Begin(true); Drain(f.Start("a", new object())); Drain(f.Start("a", new object())); f.Close();
        Check(f.Begin(true), "duplicate attempt prevents cache");
        f = new Fixture("a"); f.Begin(true); Drain(f.Start("a", new object()));
        f.Snapshot = Snap("a", "added"); f.Close();
        Check(f.Begin(true), "directory mutation midload prevents cache");
        f = new Fixture("a"); f.Successful("a"); f.Snapshot = Snap("renamed");
        Check(f.Begin(true), "renamed source invalidates");
        f = new Fixture("a"); f.Successful("a");
        f.Snapshot = new LoadingSnapshot(new[] { new LoadingSnapshot.FileStamp("a", "MPEG", 101, 200) });
        Check(f.Begin(true), "length change invalidates");
        f = new Fixture("a"); f.Successful("a");
        f.Snapshot = new LoadingSnapshot(new[] { new LoadingSnapshot.FileStamp("a", "MPEG", 100, 201) });
        Check(f.Begin(true), "timestamp change invalidates");
        f = new Fixture("a"); f.Begin(true); Drain(f.Start("a", new object()));
        f.Loader.AfterBuild(f.Owner, new InvalidOperationException());
        Check(f.Begin(true), "native build failure prevents cache");
        f = new Fixture("a"); f.Begin(true);
        var multi = new Probe { Step = delegate { f.List.Add(new object()); f.List.Add(new object()); return false; } };
        Drain(f.Loader.Observe(f.Owner, "a", "MPEG", multi, delegate { return f.List; })); f.Close();
        Check(f.Begin(true), "ambiguous multiple append prevents cache");
        f = new Fixture("a"); f.Begin(true); IEnumerator waiting = f.Start("a", new object()); waiting.MoveNext();
        f.List.Add(new object()); waiting.MoveNext(); f.Close();
        Check(f.Begin(true), "foreign append during request invalidates cache");
    }

    private static void Lifecycle()
    {
        var f = new Fixture("a"); f.Begin(true);
        var old = new Probe { ThrowDispose = true };
        IEnumerator wrapper = f.Loader.Observe(f.Owner, "a", "MPEG", old, delegate { return f.List; });
        wrapper.MoveNext(); f.Close();
        Check(f.Begin(true), "pending batch never skips");
        Check(old.Disposals == 1, "new build disposes pending request");
        Check(!wrapper.MoveNext() && old.Calls == 1, "dispose exception cannot revive stale native enumerator");
        ((IDisposable)wrapper).Dispose();
        Check(old.Disposals == 1, "disposal idempotent");
        f.List.Clear(); Drain(f.Start("a", new object())); f.Close();
        Check(!f.Begin(true), "canceled prior batch cannot contaminate completed new batch");
        f.Loader.Clear(); Check(f.Begin(true), "shutdown invalidates cache");

        f = new Fixture("a"); f.Begin(true);
        var boom = new InvalidOperationException("native fixture");
        var native = new Probe { Step = delegate { throw boom; } };
        wrapper = f.Loader.Observe(f.Owner, "a", "MPEG", native, delegate { return f.List; }); f.Close();
        Exception observed = null; try { wrapper.MoveNext(); } catch (Exception ex) { observed = ex; }
        Check(ReferenceEquals(boom, observed), "native exception preserved");
        Check(!wrapper.MoveNext() && native.Calls == 1, "native exception completes once");
        Check(f.Begin(true), "exception prevents reuse");

        f = new Fixture("a"); f.Begin(true); wrapper = f.Start("a", new object()); f.Close();
        ((IDisposable)wrapper).Dispose();
        Check(f.Begin(true), "disposed unstarted attempt remains retryable");
        f = new Fixture("a"); f.Successful("a"); Drain(f.Start("external", new object()));
        Check(f.Begin(true), "late external load invalidates cache");
        f = new Fixture(); Check(f.Begin(true), "empty first build runs"); f.Close();
        Check(!f.Begin(true), "completed unchanged empty library is reusable");
        f = new Fixture("a"); f.Snapshot = null; f.Begin(true); Drain(f.Start("a", new object())); f.Close();
        Check(f.Begin(true), "unavailable snapshot prevents cache");
    }

    private static void SnapshotAndFormats()
    {
        Check(Snap("a", "b").SameAs(Snap("b", "a")), "snapshot ordering does not define track ordering");
        var f = new Fixture();
        f.Snapshot = new LoadingSnapshot(new[] {
            new LoadingSnapshot.FileStamp("shared.mp3.ogg", "MPEG", 100, 200),
            new LoadingSnapshot.FileStamp("shared.mp3.ogg", "OGGVORBIS", 100, 200) });
        f.Begin(true);
        Drain(f.Start("shared.mp3.ogg", new object()));
        Drain(f.Loader.Observe(f.Owner, "shared.mp3.ogg", "OGGVORBIS", Append(f.List, new object()), delegate { return f.List; }));
        f.Close(); Check(!f.Begin(true), "overlapping native globs account for each format attempt");

        string directory = Path.Combine(Path.GetTempPath(), "RadioEnhancements-loading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "fixture.mp3.ogg"), second = Path.Combine(directory, "fixture.wav.bak");
        try
        {
            File.WriteAllText(first, "fixture"); File.WriteAllText(second, "fixture");
            LoadingSnapshot snapshot = LoadingSnapshot.Capture(directory);
            Check(snapshot.Files.Length == 3, "real snapshot uses overlapping broad native patterns");
            Check(snapshot.SameAs(LoadingSnapshot.Capture(directory)), "stable real snapshot");
            File.AppendAllText(first, "changed");
            Check(!snapshot.SameAs(LoadingSnapshot.Capture(directory)), "real changed source detected");
        }
        finally { File.Delete(first); File.Delete(second); Directory.Delete(directory); }
    }

    private static void Pacing()
    {
        int frame = 5;
        var owner = new object();
        IList library = new ArrayList();
        var logs = new List<string>();
        var loader = new LoadingCoordinator(logs.Add, delegate(object clip) { return clip != null; },
            delegate { return Snap("a", "b", "c"); }, delegate { return frame; });
        loader.MaxConcurrentLoads = 1;
        loader.BeforeBuild(owner, library, "fixture", true);
        var a = new Probe(); var b = new Probe(); var c = new Probe();
        IEnumerator wa = loader.Observe(owner, "a", "MPEG", a, delegate { return library; });
        IEnumerator wb = loader.Observe(owner, "b", "MPEG", b, delegate { return library; });
        IEnumerator wc = loader.Observe(owner, "c", "MPEG", c, delegate { return library; });
        Check(wa.Current == null, "never-started Current does not access native state");
        Check(wa.MoveNext() && (string)wa.Current == "probe", "first allowed request preserves yield");
        Check(wb.MoveNext() && wb.Current == null && b.Calls == 0, "second request queues before touching native");
        loader.AfterBuild(owner, null);
        LoadingCoordinator.LoadingStatus status = loader.GetStatus(owner);
        Check(status.Pending == 3 && status.Started == 1 && status.Enrolled == 3 && !status.IsComplete, "queued status includes unstarted attempts");
        ++frame;
        Check(wb.MoveNext() && b.Calls == 0, "slot held while native request waits across frames");
        a.Step = delegate { library.Add(new object()); return false; };
        Check(!wa.MoveNext(), "completed request releases slot");
        Check(wb.MoveNext() && b.Calls == 1, "next native request starts after slot released");
        b.Step = delegate { library.Add(new object()); return false; };
        wb.MoveNext();
        Check(wc.MoveNext() && wc.Current == null && c.Calls == 0, "same-frame completion cannot start another request");
        ++frame;
        c.Step = delegate { library.Add(new object()); return false; };
        Check(!wc.MoveNext() && c.Calls == 1, "next rendered frame allows progress with no game-time clock");
        status = loader.GetStatus(owner);
        Check(status.IsComplete && status.Pending == 0 && status.Started == 3 && status.Succeeded == 3, "paced batch completion status");
        Check(logs[0].Contains("firstStepTotalMs=") && logs[0].Contains("laterStepTotalMs="), "first and later telemetry separated");
        Check(!loader.BeforeBuild(owner, library, "fixture", true), "paced successful library cacheable");

        loader.MaxConcurrentLoads = 2;
        loader.BeforeBuild(owner, library, "fixture", false); library.Clear();
        a = new Probe(); b = new Probe(); c = new Probe();
        wa = loader.Observe(owner, "a", "MPEG", a, delegate { return library; });
        wb = loader.Observe(owner, "b", "MPEG", b, delegate { return library; });
        wc = loader.Observe(owner, "c", "MPEG", c, delegate { return library; });
        Check(wa.MoveNext() && a.Calls == 0, "new batch respects already-used rendered frame");
        ++frame; wa.MoveNext(); wb.MoveNext();
        Check(a.Calls == 1 && b.Calls == 0, "concurrency two still admits only one start per frame");
        ++frame; wb.MoveNext(); ++frame; wc.MoveNext();
        Check(b.Calls == 1 && c.Calls == 0, "concurrency two bounds active native operations");
        ((IDisposable)wa).Dispose();
        wc.MoveNext();
        Check(c.Calls == 1, "disposal releases exactly one slot");
        loader.Clear();
        Check(!wb.MoveNext() && !wc.MoveNext(), "clear disables all active old wrappers");
        Check(loader.CurrentOwner == null && !loader.GetStatus(owner).Exists, "clear hides canceled status");

        loader.MaxConcurrentLoads = 1;
        loader.BeforeBuild(owner, library, "fixture", false); library.Clear();
        a = new Probe { Step = delegate { throw new InvalidOperationException(); } };
        b = new Probe();
        wa = loader.Observe(owner, "a", "MPEG", a, delegate { return library; });
        wb = loader.Observe(owner, "b", "MPEG", b, delegate { return library; });
        ++frame; try { wa.MoveNext(); } catch (InvalidOperationException) { }
        ++frame; wb.MoveNext();
        Check(b.Calls == 1, "first-step exception releases slot");
        loader.BeforeBuild(owner, library, "fixture", false);
        Check(!wb.MoveNext() && b.Calls == 1 && b.Disposals == 1, "replacement generation cancels pending native operation");
        loader.Clear();

        loader.MaxConcurrentLoads = 0;
        loader.BeforeBuild(owner, library, "fixture", false); library.Clear();
        a = new Probe(); b = new Probe();
        wa = loader.Observe(owner, "a", "MPEG", a, delegate { return library; });
        wb = loader.Observe(owner, "b", "MPEG", b, delegate { return library; });
        wa.MoveNext(); wb.MoveNext();
        Check(a.Calls == 1 && b.Calls == 1, "zero preserves immediate native starts even same frame");
        loader.Clear();

        loader.MaxConcurrentLoads = 1;
        loader.BeforeBuild(owner, library, "fixture", false); library.Clear();
        a = new Probe(); b = new Probe();
        wa = loader.Observe(owner, "a", "MPEG", a, delegate { return library; });
        wb = loader.Observe(owner, "b", "MPEG", b, delegate { return library; });
        ++frame; wa.MoveNext(); wb.MoveNext(); loader.Clear();
        Check(b.Calls == 0 && b.Disposals == 1 && !wb.MoveNext(), "canceling queued iterator never executes its native body");
    }

    private static void GenerationScopedCancellation()
    {
        var f = new Fixture("a");
        f.Begin(false);
        long oldGeneration = f.Loader.GetStatus(f.Owner).Generation;
        var oldNative = new Probe();
        IEnumerator old = f.Loader.Observe(f.Owner, "a", "MPEG", oldNative, delegate { return f.List; });
        old.MoveNext(); f.Close();
        f.Begin(false);
        long currentGeneration = f.Loader.GetStatus(f.Owner).Generation;
        var currentNative = new Probe();
        IEnumerator current = f.Loader.Observe(f.Owner, "a", "MPEG", currentNative, delegate { return f.List; });
        current.MoveNext(); f.Close();
        f.Loader.Cancel(f.Owner, oldGeneration);
        Check(current.MoveNext() && currentNative.Disposals == 0, "old startup generation cannot cancel newer rebuild");
        f.Loader.Cancel(new object(), currentGeneration);
        Check(current.MoveNext() && currentNative.Disposals == 0, "different owner cannot cancel current rebuild");
        f.Loader.Cancel(f.Owner, currentGeneration);
        Check(!current.MoveNext() && currentNative.Disposals == 1 && !f.Loader.GetStatus(f.Owner).Exists, "matching owner generation cancels pending work");
        f.Loader.Cancel(f.Owner, currentGeneration);
        Check(currentNative.Disposals == 1 && !old.MoveNext() && oldNative.Disposals == 1, "repeated cancellation is idempotent");
    }

    internal static int Main()
    {
        SuccessAndIdentity(); FailuresAndMutations(); Lifecycle(); SnapshotAndFormats(); Pacing(); GenerationScopedCancellation();
        Console.WriteLine("Loading tests passed: " + checks + " assertions.");
        return 0;
    }
}
