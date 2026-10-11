using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RadioEnhancements
{
    // Wraps native loading; never creates, renames, unloads or destroys an AudioClip.
    internal sealed class LoadingCoordinator
    {
        private readonly ConditionalWeakTable<object, Batch> batches = new ConditionalWeakTable<object, Batch>();
        private readonly List<WeakReference<Batch>> knownBatches = new List<WeakReference<Batch>>();
        private readonly Action<string> report;
        private readonly Func<object, bool> usableClip;
        private readonly Func<string, LoadingSnapshot> capture;
        private readonly Func<int> renderedFrame;
        private WeakReference<object> latestOwner;
        private int lastStartFrame;
        private bool hasStartFrame;
        private long nextGeneration;
        internal int MaxConcurrentLoads { get; set; }
        internal object CurrentOwner { get { object owner; return latestOwner != null && latestOwner.TryGetTarget(out owner) ? owner : null; } }

        internal LoadingCoordinator(Action<string> report, Func<object, bool> usableClip)
            : this(report, usableClip, LoadingSnapshot.Capture, null) { }

        internal LoadingCoordinator(Action<string> report, Func<object, bool> usableClip, Func<int> renderedFrame)
            : this(report, usableClip, LoadingSnapshot.Capture, renderedFrame) { }

        internal LoadingCoordinator(Action<string> report, Func<object, bool> usableClip, Func<string, LoadingSnapshot> capture)
            : this(report, usableClip, capture, null) { }

        internal LoadingCoordinator(Action<string> report, Func<object, bool> usableClip, Func<string, LoadingSnapshot> capture, Func<int> renderedFrame)
        { this.report = report; this.usableClip = usableClip; this.capture = capture; this.renderedFrame = renderedFrame; }

        // Call before invalidating the display, shuffle state or filename mappings.
        internal bool BeforeBuild(object owner, IList library, string directory, bool reuseUnchanged)
        {
            latestOwner = new WeakReference<object>(owner);
            var scan = Stopwatch.StartNew();
            LoadingSnapshot snapshot = SafeCapture(directory);
            double scanMs = scan.Elapsed.TotalMilliseconds;
            Batch previous;
            if (batches.TryGetValue(owner, out previous))
            {
                if (reuseUnchanged && previous.CacheReady && previous.Before != null
                    && previous.Before.SameAs(snapshot) && ExactLibrary(previous, library))
                {
                    Log("Music LOAD reused unchanged library: cached=" + library.Count
                        + ", scanMs=" + Milliseconds(scanMs) + ", nativeStarted=0.");
                    return false;
                }
                Cancel(previous);
            }
            var batch = new Batch(library, directory, snapshot);
            batch.Generation = ++nextGeneration;
            batch.MaxConcurrent = renderedFrame == null ? 0 : Math.Max(0, Math.Min(16, MaxConcurrentLoads));
            batch.ScanMs = scanMs;
            batches.Remove(owner);
            batches.Add(owner, batch);
            knownBatches.RemoveAll(delegate(WeakReference<Batch> item) { Batch target; return !item.TryGetTarget(out target); });
            knownBatches.Add(new WeakReference<Batch>(batch));
            return true;
        }

        // Call once after native Build returned or threw, only if BeforeBuild returned true.
        internal void AfterBuild(object owner, Exception buildError)
        {
            Batch batch;
            if (!batches.TryGetValue(owner, out batch) || batch.Cancelled) return;
            batch.BuildMs = batch.Clock.Elapsed.TotalMilliseconds;
            batch.EnrollmentClosed = true;
            if (buildError != null) batch.Valid = false;
            TryComplete(batch);
        }

        // Place this OUTSIDE the existing filename observer, so only native steps are measured.
        internal IEnumerator Observe(object owner, string path, string audioType, IEnumerator original, Func<IList> library)
        {
            Batch batch;
            if (original == null || !batches.TryGetValue(owner, out batch)) return original;
            if (batch.EnrollmentClosed) { batch.Valid = false; batch.CacheReady = false; }
            string key = audioType + "\0" + path;
            if (!batch.Attempts.Add(key)) batch.Valid = false;
            ++batch.Enrolled;
            var wrapped = new Observed(this, batch, original, library);
            batch.Pending.Add(wrapped);
            return wrapped;
        }

        internal LoadingStatus GetStatus(object owner)
        {
            Batch batch;
            if (owner == null || !batches.TryGetValue(owner, out batch) || batch.Cancelled) return new LoadingStatus();
            return new LoadingStatus { Exists = true, Pending = batch.Pending.Count, Enrolled = batch.Enrolled,
                Generation = batch.Generation,
                Started = batch.Started, Completed = batch.Succeeded + batch.Failed,
                Succeeded = batch.Succeeded, Failed = batch.Failed,
                IsComplete = batch.EnrollmentClosed && batch.Pending.Count == 0 };
        }

        internal void Cancel(object owner, long generation)
        {
            Batch batch;
            if (owner != null && batches.TryGetValue(owner, out batch) && batch.Generation == generation) Cancel(batch);
        }

        internal void Clear()
        {
            foreach (WeakReference<Batch> item in knownBatches) { Batch batch; if (item.TryGetTarget(out batch)) Cancel(batch); }
            knownBatches.Clear();
            latestOwner = null;
        }

        private LoadingSnapshot SafeCapture(string directory)
        {
            try { return capture(directory); }
            catch (Exception error) { Log("Music LOAD change check unavailable: " + error.GetType().Name + ". Native loading retained."); return null; }
        }

        private void Cancel(Batch batch)
        {
            batch.Cancelled = true;
            batch.CacheReady = false;
            batch.Valid = false;
            var pending = new List<Observed>(batch.Pending);
            foreach (Observed item in pending)
            {
                try { item.Dispose(); }
                catch (Exception error) { Log("Music LOAD cancellation disposal failed: " + error.GetType().Name + "."); }
            }
        }

        private bool ExactLibrary(Batch batch, IList list)
        {
            if (!ReferenceEquals(batch.Library, list) || list == null || list.Count != batch.Clips.Count) return false;
            for (int i = 0; i < list.Count; ++i)
                if (!ReferenceEquals(list[i], batch.Clips[i]) || !usableClip(list[i])) return false;
            return true;
        }

        private void TryComplete(Batch batch)
        {
            if (batch.Cancelled || !batch.EnrollmentClosed || batch.Pending.Count != 0 || batch.Reported) return;
            batch.Reported = true;
            double elapsedMs = batch.Clock.Elapsed.TotalMilliseconds;
            var scan = Stopwatch.StartNew();
            LoadingSnapshot after = SafeCapture(batch.Directory);
            batch.ScanMs += scan.Elapsed.TotalMilliseconds;
            bool expected = batch.Before != null && batch.Before.Files.Length == batch.Enrolled;
            if (expected)
                foreach (LoadingSnapshot.FileStamp file in batch.Before.Files)
                    if (!batch.Attempts.Contains(file.Key)) { expected = false; break; }
            batch.CacheReady = batch.Valid && expected && batch.Failed == 0
                && batch.Succeeded == batch.Enrolled && batch.Before.SameAs(after) && ExactLibrary(batch, batch.Library);
            Log("Music LOAD complete: enrolled=" + batch.Enrolled + ", started=" + batch.Started + ", succeeded=" + batch.Succeeded
                + ", failed=" + batch.Failed + ", cacheEligible=" + batch.CacheReady
                + ", scanMs=" + Milliseconds(batch.ScanMs) + ", buildMs=" + Milliseconds(batch.BuildMs)
                + ", elapsedMs=" + Milliseconds(elapsedMs) + ", observedMoveNextTotalMs=" + Milliseconds(batch.StepTotalMs)
                + ", observedMoveNextWorstMs=" + Milliseconds(batch.StepWorstMs)
                + ", firstStepTotalMs=" + Milliseconds(batch.FirstTotalMs) + ", firstStepWorstMs=" + Milliseconds(batch.FirstWorstMs)
                + ", laterStepTotalMs=" + Milliseconds(batch.LaterTotalMs) + ", laterStepWorstMs=" + Milliseconds(batch.LaterWorstMs)
                + ". MoveNext includes filename observation; elapsed includes engine waiting and frame scheduling.");
        }

        private static string Milliseconds(double value) { return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture); }
        private void Log(string message) { try { if (report != null) report(message); } catch { } }

        private sealed class Batch
        {
            internal readonly IList Library;
            internal readonly string Directory;
            internal readonly LoadingSnapshot Before;
            internal readonly Stopwatch Clock = Stopwatch.StartNew();
            internal readonly HashSet<string> Attempts = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<object> Clips = new List<object>();
            internal readonly HashSet<Observed> Pending = new HashSet<Observed>();
            internal bool Valid = true, EnrollmentClosed, Cancelled, CacheReady, Reported;
            internal int Enrolled, Started, Succeeded, Failed, Active, MaxConcurrent;
            internal long Generation;
            internal double ScanMs, BuildMs, StepTotalMs, StepWorstMs, FirstTotalMs, FirstWorstMs, LaterTotalMs, LaterWorstMs;
            internal Batch(IList library, string directory, LoadingSnapshot before)
            { Library = library; Directory = directory; Before = before; }
        }

        private sealed class Observed : IEnumerator, IDisposable
        {
            private readonly LoadingCoordinator coordinator;
            private readonly Batch batch;
            private readonly IEnumerator original;
            private readonly Func<IList> library;
            private bool done, disposed, appended, started, waiting, ownsSlot;
            internal Observed(LoadingCoordinator coordinator, Batch batch, IEnumerator original, Func<IList> library)
            { this.coordinator = coordinator; this.batch = batch; this.original = original; this.library = library; }
            public object Current { get { return waiting || !started ? null : original.Current; } }
            public bool MoveNext()
            {
                if (done || batch.Cancelled) return false;
                bool firstStep = !started;
                if (!started)
                {
                    if (batch.MaxConcurrent > 0)
                    {
                        int frame = coordinator.renderedFrame();
                        if (batch.Active >= batch.MaxConcurrent || (coordinator.hasStartFrame && coordinator.lastStartFrame == frame))
                        { waiting = true; return true; }
                        coordinator.lastStartFrame = frame;
                        coordinator.hasStartFrame = true;
                    }
                    started = true;
                    ownsSlot = true;
                    ++batch.Started;
                    ++batch.Active;
                }
                waiting = false;
                IList before = null;
                int count = 0;
                try { before = library(); count = before.Count; if (!coordinator.ExactLibrary(batch, before)) batch.Valid = false; }
                catch { batch.Valid = false; }
                bool more;
                try
                {
                    var watch = Stopwatch.StartNew();
                    try { more = original.MoveNext(); }
                    finally
                    {
                        double elapsed = watch.Elapsed.TotalMilliseconds;
                        batch.StepTotalMs += elapsed;
                        batch.StepWorstMs = Math.Max(batch.StepWorstMs, elapsed);
                        if (firstStep) { batch.FirstTotalMs += elapsed; batch.FirstWorstMs = Math.Max(batch.FirstWorstMs, elapsed); }
                        else { batch.LaterTotalMs += elapsed; batch.LaterWorstMs = Math.Max(batch.LaterWorstMs, elapsed); }
                    }
                }
                catch { Finish(false); throw; }
                try
                {
                    IList after = library();
                    if (ReferenceEquals(before, after) && after != null && after.Count == count + 1)
                    {
                        bool exact = !appended && count == batch.Clips.Count;
                        for (int i = 0; exact && i < count; ++i) exact = ReferenceEquals(after[i], batch.Clips[i]);
                        object clip = after[after.Count - 1];
                        exact = exact && coordinator.usableClip(clip);
                        if (exact) { batch.Clips.Add(clip); appended = true; }
                        else batch.Valid = false;
                    }
                    else if (!coordinator.ExactLibrary(batch, after)) batch.Valid = false;
                }
                catch { batch.Valid = false; }
                if (!more) Finish(appended);
                return more;
            }
            public void Reset() { throw new NotSupportedException(); }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { var disposable = original as IDisposable; if (disposable != null) disposable.Dispose(); }
                finally { if (!done) Finish(false); }
            }
            private void Finish(bool success)
            {
                if (done) return;
                done = true;
                if (ownsSlot) { --batch.Active; ownsSlot = false; }
                batch.Pending.Remove(this);
                if (success) ++batch.Succeeded;
                else { ++batch.Failed; batch.Valid = false; }
                try { coordinator.TryComplete(batch); }
                catch (Exception error) { batch.Valid = false; batch.CacheReady = false; coordinator.Log("Music LOAD completion observation failed: " + error.GetType().Name + "."); }
            }
        }

        internal struct LoadingStatus
        {
            internal bool Exists, IsComplete;
            internal long Generation;
            internal int Pending, Enrolled, Started, Completed, Succeeded, Failed;
        }

    }
}
