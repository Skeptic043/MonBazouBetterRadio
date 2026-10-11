using System;
using System.Collections.Generic;

namespace RadioEnhancements
{
    internal interface ICustomMusicSink
    {
        void Stop(object member);
        void Schedule(object member, CustomMusicTrack track, int index, int sample, double dspStart);
        void State(object member, int index, double seconds);
    }

    // Reservation does not advance playback history. It lets prefetch decode precisely the
    // song the next button or automatic transition will consume.
    internal sealed class CustomMusicOrder
    {
        private readonly Random random;
        private readonly List<int> bag = new List<int>(), history = new List<int>();
        private int size = -1, cursor, reserved = -1;
        internal bool Enabled;
        internal CustomMusicOrder(int seed) { random = new Random(seed); }
        internal void Reset() { size = -1; reserved = -1; cursor = 0; bag.Clear(); history.Clear(); }
        private void Sync(int current, int count)
        {
            if (size == count && history.Count > 0 && history[cursor] == current) return;
            Reset(); size = count; history.Add(current);
            for (int i = 0; i < count; i++) if (i != current) bag.Add(i);
        }
        internal int Peek(int current, int count, HashSet<int> failed)
        {
            if (count <= failed.Count) return -1;
            if (!Enabled)
            { for (int step = 1; step <= count; step++) { int next = (current + step) % count; if (!failed.Contains(next)) return next; } return -1; }
            Sync(current, count);
            for (int i = cursor + 1; i < history.Count; i++) if (!failed.Contains(history[i])) return history[i];
            if (reserved >= 0 && !failed.Contains(reserved)) return reserved;
            bag.RemoveAll(delegate(int candidate) { return failed.Contains(candidate); });
            if (bag.Count == 0) for (int i = 0; i < count; i++) if (!failed.Contains(i)) bag.Add(i);
            int pick;
            do { pick = random.Next(bag.Count); } while (bag.Count > 1 && bag[pick] == current);
            return reserved = bag[pick];
        }
        internal int Next(int current, int count, HashSet<int> failed)
        {
            int next = Peek(current, count, failed);
            if (next < 0 || !Enabled) return next;
            for (int i = cursor + 1; i < history.Count; i++) if (history[i] == next) { cursor = i; return next; }
            bag.Remove(next); reserved = -1; history.Add(next); cursor++;
            if (history.Count > 256) { history.RemoveAt(0); cursor--; }
            return next;
        }
        internal int Previous(int current, int count, double seconds, HashSet<int> failed)
        {
            reserved = -1;
            if (seconds >= 2 && !failed.Contains(current)) return current;
            if (!Enabled)
            { for (int step = 1; step <= count; step++) { int next = (current + count - step) % count; if (!failed.Contains(next)) return next; } return -1; }
            Sync(current, count);
            while (cursor > 0) { int next = history[--cursor]; if (!failed.Contains(next)) return next; }
            return !failed.Contains(current) ? current : Next(current, count, failed);
        }
    }

    internal sealed class CustomMusicTransport : IDisposable
    {
        private readonly ICustomMusicSink sink;
        private readonly Action<string> report;
        private readonly CustomMusicOrder order;
        private readonly CustomMusicEngine engine = new CustomMusicEngine();
        private readonly HashSet<object> members = new HashSet<object>();
        private readonly HashSet<int> failed = new HashSet<int>();
        private CustomMusicCatalog catalog = CustomMusicCatalog.Empty;
        private CustomMusicTrack track;
        private CustomMusicEntry remembered;
        private int index;
        private double originSample, originDsp, desiredSeconds;
        private bool paused, active, selected, disposed;
        private int missingReported;
        internal bool Active { get { return active; } }
        internal bool Pending { get { return selected && !active; } }
        internal bool Shuffle { get { return order.Enabled; } }
        internal int Index { get { return index; } }
        internal bool Owns(object member) { return members.Contains(member); }
        internal CustomMusicTransport(ICustomMusicSink sink, int seed, Action<string> report)
        { this.sink = sink; this.report = report; order = new CustomMusicOrder(seed); }
        internal double Seconds(double dsp) { return active && track != null ? Position(dsp) / track.Frequency : desiredSeconds; }
        private double Position(double dsp) { return originSample + (paused ? 0 : Math.Max(0, dsp - originDsp) * track.Frequency); }
        internal void Toggle() { order.Enabled = !order.Enabled; order.Reset(); }
        internal void SetShuffle(bool value) { if (order.Enabled != value) { order.Enabled = value; order.Reset(); } }
        internal void Forget(object member) { members.Remove(member); }
        private void StopAll()
        { foreach (object member in members) sink.Stop(member); members.Clear(); }
        private void Select(int selectedIndex, double seconds)
        {
            StopAll(); active = false; index = selectedIndex; selected = selectedIndex >= 0;
            desiredSeconds = Double.IsNaN(seconds) || Double.IsInfinity(seconds) ? 0 : Math.Max(0, seconds);
            track = selected ? engine.Prepare(catalog.Entries[index], desiredSeconds) : null;
            if (selected) remembered = catalog.Entries[index];
            if (!selected) engine.Dispose(); missingReported = 0;
        }
        internal void Pause(double dsp)
        {
            if (paused) return;
            desiredSeconds = Seconds(dsp);
            if (active && track != null) originSample = Math.Min(track.Samples - 1, Position(dsp));
            originDsp = dsp; paused = true; StopAll(); active = false;
        }
        internal void Resume() { paused = false; }
        internal void Seek(CustomMusicCatalog nextCatalog, IList<object> current, double dsp, double lead, bool previous)
        {
            UpdateCatalog(nextCatalog, index, Seconds(dsp));
            if (catalog.Entries.Length == 0 || current.Count == 0) return;
            int next = previous ? order.Previous(index, catalog.Entries.Length, Seconds(dsp), failed) : order.Next(index, catalog.Entries.Length, failed);
            Select(next, 0); Update(catalog, current, dsp, lead, index, 0);
        }
        private void UpdateCatalog(CustomMusicCatalog next, int initialIndex, double initialSeconds)
        {
            if (ReferenceEquals(catalog, next)) return;
            string previousPath = remembered == null ? null : remembered.Path;
            StopAll(); engine.Dispose(); track = null; active = false; selected = false; failed.Clear(); order.Reset();
            catalog = next ?? CustomMusicCatalog.Empty;
            index = Math.Max(0, Math.Min(catalog.Entries.Length - 1, initialIndex)); desiredSeconds = initialSeconds;
            if (previousPath != null)
            {
                int found = Array.FindIndex(catalog.Entries, delegate(CustomMusicEntry entry) { return StringComparer.OrdinalIgnoreCase.Equals(previousPath, entry.Path); });
                if (found >= 0) index = found;
                else { index = 0; desiredSeconds = 0; remembered = null; }
            }
        }
        internal void Update(CustomMusicCatalog nextCatalog, IList<object> current, double dsp, double lead, int initialIndex, double initialSeconds)
        {
            if (disposed) return;
            UpdateCatalog(nextCatalog, initialIndex, remembered == null ? initialSeconds : Seconds(dsp));
            var departed = new List<object>();
            foreach (object member in members) if (!current.Contains(member)) departed.Add(member);
            foreach (object member in departed) { sink.Stop(member); members.Remove(member); }
            if (current.Count == 0 || catalog.Entries.Length == 0)
            {
                desiredSeconds = Seconds(dsp); StopAll(); engine.Dispose(); track = null; active = false; selected = false; return;
            }
            if (!selected) Select(index, desiredSeconds);
            if (track == null) return;
            if (track.Stream.Fault != null)
            {
                Fail(index, track.Stream.Fault.Message); Select(order.Next(index, catalog.Entries.Length, failed), 0); return;
            }
            CustomMusicTrack prefetch = engine.Prefetched;
            if (prefetch != null && prefetch.Stream.Fault != null)
            {
                int at = Array.IndexOf(catalog.Entries, prefetch.Entry);
                if (at >= 0) Fail(at, prefetch.Stream.Fault.Message);
            }
            if (!track.Stream.IsOpen) return;
            if (!active)
            {
                int sample = (int)Math.Min(track.Samples - 1, Math.Floor(desiredSeconds * track.Frequency));
                track.Stream.Request(sample);
                if (paused || !track.Stream.Ready(sample)) return;
                originSample = sample; originDsp = dsp + lead; active = true;
            }
            double position = Position(dsp);
            int end = Math.Min(track.Stream.EndFrame, track.Stream.DamageFrame);
            if (position >= end)
            {
                if (track.Stream.Damage != null) Fail(index, track.Stream.Damage.Message);
                Select(order.Next(index, catalog.Entries.Length, failed), 0); return;
            }
            track.Stream.Request((int)position);
            double start = Math.Max(originDsp, dsp + lead);
            int joinSample = (int)Math.Round(Position(start));
            if (!paused && joinSample < end && track.Stream.Ready(joinSample))
                foreach (object member in current)
                {
                    if (members.Contains(member)) continue;
                    sink.Schedule(member, track, index, joinSample, start); members.Add(member);
                }
            foreach (object member in current) sink.State(member, index, position / track.Frequency);
            int nextIndex = order.Peek(index, catalog.Entries.Length, failed);
            engine.Prefetch(nextIndex < 0 ? null : catalog.Entries[nextIndex]);
            int missing = track.Stream.MissingFrames;
            if (missing > missingReported && report != null)
            { report("Music callback missing PCM frames (may include discarded preroll): " + (missing - missingReported) + " in " + track.Entry.Title); missingReported = missing; }
        }
        private void Fail(int at, string reason)
        { if (failed.Add(at) && report != null) report("Skipping music file " + catalog.Entries[at].Path + ": " + reason); }
        public void Dispose()
        { if (disposed) return; disposed = true; StopAll(); engine.Dispose(); track = null; active = selected = false; }
    }
}
