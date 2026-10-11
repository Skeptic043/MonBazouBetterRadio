using System;
using System.Collections;
using System.Collections.Generic;
using RadioEnhancements;

internal static class StartupLoadingTests
{
    private static int checks;
    private static void Check(bool value, string reason) { ++checks; if (!value) throw new Exception("FAILED: " + reason); }
    private sealed class Probe : IEnumerator, IDisposable
    {
        internal int Calls, Disposals;
        internal bool ThrowMove, ThrowDispose;
        internal object Yield = new object();
        public object Current { get { return Yield; } }
        public bool MoveNext() { ++Calls; if (ThrowMove) throw new InvalidOperationException("native move"); return Calls < 4; }
        public void Dispose() { ++Disposals; if (ThrowDispose) throw new InvalidOperationException("native dispose"); }
        public void Reset() { throw new NotSupportedException(); }
    }
    private sealed class Fixture
    {
        internal Probe Native = new Probe();
        internal bool Alive = true, Pending = true, ThrowBegin, ThrowPending, ThrowProgress, ThrowFinish, ThrowCancel;
        internal int Begins, Cancels, Progresses, Finishes, Completed;
        internal double Clock;
        internal float Scale = 0.5f;
        internal readonly List<Exception> Errors = new List<Exception>();
        internal StartupLoadingSequence Sequence;
        internal Fixture()
        {
            var pause = new StartupTimeScaleOwnership(delegate { return Scale; }, delegate(float value) { Scale = value; });
            Sequence = new StartupLoadingSequence(Native, delegate { return Alive; },
                delegate { ++Begins; pause.Acquire(); if (ThrowBegin) throw new Exception("begin"); },
                delegate { if (ThrowPending) throw new Exception("pending"); return Pending; },
                delegate { ++Cancels; Pending = false; if (ThrowCancel) throw new Exception("cancel"); },
                delegate { ++Progresses; if (ThrowProgress) throw new Exception("progress"); },
                delegate { ++Finishes; pause.Release(); if (ThrowFinish) throw new Exception("finish"); },
                delegate { return Completed; }, delegate { return Clock; }, 180, Errors.Add);
        }
        internal void StartGate()
        {
            Check(Sequence.MoveNext(), "native setup yields");
            Check(ReferenceEquals(Sequence.Current, Native.Yield), "native first yield preserved");
            Check(Begins == 0 && Scale == 0.5f, "gate not entered before first yield resumes");
            Check(Sequence.MoveNext() && Sequence.Current == null, "gate waits by frame");
            Check(Begins == 1 && Native.Calls == 1 && Scale == 0f, "native save work paused behind gate");
        }
    }
    private static void Ordering()
    {
        var f = new Fixture(); f.StartGate();
        Check(f.Sequence.MoveNext() && f.Native.Calls == 1 && f.Begins == 1, "pending waits without repeated begin");
        f.Pending = false;
        Check(f.Sequence.MoveNext() && f.Native.Calls == 2 && f.Scale == 0.5f, "complete restores scale before native resumes");
        Check(ReferenceEquals(f.Sequence.Current, f.Native.Yield), "later native yields preserved");
        Check(f.Sequence.MoveNext() && !f.Sequence.MoveNext(), "native completes normally");
        Check(f.Native.Disposals == 1 && !f.Sequence.MoveNext(), "completed native disposed once");
        var skip = new Fixture { Pending = false };
        Check(skip.Sequence.MoveNext() && skip.Sequence.MoveNext() && skip.Native.Calls == 2, "missing manager or disabled begin with no pending does not gate");
        Check(skip.Scale == 0.5f, "no pending restores immediately");
    }
    private static void TimeoutAndProgress()
    {
        var f = new Fixture(); f.StartGate();
        f.Clock = 179; Check(f.Sequence.MoveNext(), "wait below timeout");
        f.Completed = 1; f.Clock = 180; Check(f.Sequence.MoveNext(), "progress resets idle timeout");
        f.Clock = 359; Check(f.Sequence.MoveNext() && f.Cancels == 0, "totalduration above timeout allowed with progress");
        f.Clock = 360; Check(f.Sequence.MoveNext() && f.Cancels == 1 && f.Native.Calls == 2, "no progress cancels before native resumes");
        Check(f.Errors.Count == 1 && f.Errors[0] is TimeoutException && f.Scale == 0.5f, "timeout reported and scale restored");
    }
    private static void Failures()
    {
        foreach (string fault in new[] { "begin", "pending", "progress", "finish" })
        {
            var f = new Fixture(); f.Sequence.MoveNext();
            f.ThrowBegin = fault == "begin"; f.ThrowPending = fault == "pending";
            f.ThrowProgress = fault == "progress"; f.ThrowFinish = fault == "finish";
            if (fault == "finish") f.Pending = false;
            Check(f.Sequence.MoveNext() && f.Native.Calls == 2, fault + " mod error releases native");
            Check(f.Errors.Count == 1 && f.Scale == 0.5f, fault + " reported and scale restored");
        }
        var native = new Fixture(); native.Sequence.MoveNext(); native.Pending = false; native.Native.ThrowMove = true; native.Native.ThrowDispose = true;
        Exception caught = null; try { native.Sequence.MoveNext(); } catch (Exception error) { caught = error; }
        Check(caught != null && caught.Message == "native move", "native exception preserved even when disposal fails");
        Check(native.Native.Disposals == 1 && native.Scale == 0.5f, "native exception cleanup once");
        var cancel = new Fixture(); cancel.StartGate(); cancel.ThrowCancel = true; cancel.Clock = 180;
        Check(cancel.Sequence.MoveNext() && cancel.Native.Calls == 2 && cancel.Scale == 0.5f, "cancellation disposal error cannot strand native save startup");
    }
    private static void Cancellation()
    {
        var disposed = new Fixture(); disposed.StartGate(); disposed.Sequence.Dispose(); disposed.Sequence.Dispose();
        Check(disposed.Cancels == 1 && disposed.Native.Disposals == 1 && disposed.Scale == 0.5f, "disposal cancels and restores once");
        Check(!disposed.Sequence.MoveNext() && disposed.Native.Calls == 1, "disposed original never resumed");
        var dead = new Fixture(); dead.StartGate(); dead.Alive = false;
        Check(!dead.Sequence.MoveNext() && dead.Native.Calls == 1 && dead.Cancels == 1 && dead.Scale == 0.5f, "dead owner cancels without native resume");
        var external = new Fixture(); external.StartGate(); external.Scale = 2f; external.Pending = false; external.Sequence.MoveNext();
        Check(external.Scale == 2f, "another writer's time scale preserved");
        var unload = new Fixture(); unload.StartGate(); unload.Sequence.ReleaseGate(); unload.Sequence.ReleaseGate();
        Check(unload.Cancels == 1 && unload.Native.Disposals == 0 && unload.Scale == 0.5f, "plugin unload releases gate without disposing native");
        Check(unload.Sequence.MoveNext() && unload.Native.Calls == 2, "native save startup continues after plugin unload");
        var earlyUnload = new Fixture(); earlyUnload.Sequence.ReleaseGate(); earlyUnload.Sequence.MoveNext(); earlyUnload.Sequence.MoveNext();
        Check(earlyUnload.Begins == 0 && earlyUnload.Native.Calls == 2, "plugin unload before gate leaves native sequence untouched");
        float scale = 0f; var pause = new StartupTimeScaleOwnership(delegate { return scale; }, delegate(float value) { scale = value; });
        pause.Acquire(); pause.Release(); Check(scale == 0f, "preexisting zero scale remains zero");
        scale = 0.25f; pause.Acquire(); pause.Acquire(); pause.Release(); pause.Release();
        Check(scale == 0.25f, "repeat acquire release preserves initial scale");
    }
    public static int Main()
    {
        try { Ordering(); TimeoutAndProgress(); Failures(); Cancellation(); Console.WriteLine("Startup loading tests passed: " + checks + " checks."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
