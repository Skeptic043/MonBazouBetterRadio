using System;
using System.Collections;

namespace RadioEnhancements
{
    internal sealed class StartupTimeScaleOwnership
    {
        private readonly Func<float> read;
        private readonly Action<float> write;
        private float previous;
        private bool owns;
        internal StartupTimeScaleOwnership(Func<float> read, Action<float> write) { this.read = read; this.write = write; }
        internal void Acquire() { if (owns) return; previous = read(); if (previous != 0f) { write(0f); owns = true; } }
        internal void Release() { if (!owns) return; owns = false; if (read() == 0f) write(previous); }
    }

    // The native first yield establishes the loading screen and paused listener. Gate only its next step.
    internal sealed class StartupLoadingSequence : IEnumerator, IDisposable
    {
        private readonly IEnumerator original;
        private readonly Func<bool> alive, pending;
        private readonly Action begin, cancel, progress, finish;
        private readonly Func<int> completed;
        private readonly Func<double> clock;
        private readonly Action<Exception> report;
        private readonly double timeout;
        private bool first = true, entered, gated, released, disposed;
        private int lastCompleted;
        private double lastProgress;
        internal bool GateReleased { get { return released || disposed; } }

        internal StartupLoadingSequence(IEnumerator original, Func<bool> alive, Action begin, Func<bool> pending,
            Action cancel, Action progress, Action finish, Func<int> completed, Func<double> clock,
            double timeoutSeconds, Action<Exception> report)
        {
            this.original = original; this.alive = alive; this.begin = begin; this.pending = pending;
            this.cancel = cancel; this.progress = progress; this.finish = finish; this.completed = completed;
            this.clock = clock; timeout = timeoutSeconds; this.report = report;
        }
        public object Current { get { return gated ? null : original.Current; } }
        public bool MoveNext()
        {
            if (disposed) return false;
            if (!alive()) { Dispose(); return false; }
            if (first)
            {
                first = false;
                return AdvanceNative();
            }
            if (!released)
            {
                try
                {
                    if (!entered)
                    {
                        entered = true; begin();
                        lastCompleted = completed(); lastProgress = clock();
                    }
                    if (pending())
                    {
                        int nowCompleted = completed();
                        double now = clock();
                        if (nowCompleted != lastCompleted) { lastCompleted = nowCompleted; lastProgress = now; }
                        if (now - lastProgress < timeout)
                        {
                            progress(); gated = true; return true;
                        }
                        Report(new TimeoutException("Music startup preload made no progress for " + timeout + " seconds; leaving the startup music wait."));
                        cancel();
                    }
                    Release();
                }
                catch (Exception error)
                {
                    Report(error);
                    try { cancel(); }
                    catch (Exception cancellation) { Report(cancellation); }
                    Release();
                }
            }
            return AdvanceNative();
        }
        private bool AdvanceNative()
        {
            try
            {
                bool more = original.MoveNext();
                if (!more) Dispose();
                return more;
            }
            catch
            {
                try { Dispose(); } catch (Exception cleanup) { Report(cleanup); }
                throw;
            }
        }
        private void Release()
        {
            if (released) return;
            released = true; gated = false;
            try { finish(); } catch (Exception error) { Report(error); }
        }
        // Plugin shutdown removes our wait, but an alive scene's native save loader must still finish.
        internal void ReleaseGate()
        {
            if (disposed || released) return;
            if (entered) { try { cancel(); } catch (Exception error) { Report(error); } }
            Release();
        }
        private void Report(Exception error) { try { if (report != null) report(error); } catch { } }
        public void Reset() { throw new NotSupportedException(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (entered && !released) { try { cancel(); } catch (Exception error) { Report(error); } }
            Release();
            IDisposable native = original as IDisposable;
            if (native != null) native.Dispose();
        }
    }
}
