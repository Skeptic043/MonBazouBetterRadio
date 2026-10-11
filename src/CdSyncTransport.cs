using System;
using System.Collections.Generic;

namespace RadioEnhancements
{
    internal sealed class CdSyncTrack
    {
        internal readonly object Clip;
        internal readonly int Samples, Frequency;
        internal CdSyncTrack(object clip, int samples, int frequency) { Clip = clip; Samples = samples; Frequency = frequency; }
    }

    internal interface ICdSyncSink
    {
        void Schedule(object member, CdSyncTrack track, int index, int sample, double dspStart);
        void State(object member, int index, double seconds);
    }

    // One clock and one shuffle history. Member ordering and individual radio clocks never elect a leader.
    internal sealed class CdSyncTransport
    {
        private readonly ICdSyncSink sink;
        private readonly HashSet<object> members = new HashSet<object>();
        private readonly ShuffleSequence sequence;
        private CdSyncTrack track;
        private int index, originSample;
        private double originDsp;
        private bool paused;
        internal CdSyncTransport(ICdSyncSink sink, int seed) { this.sink = sink; sequence = new ShuffleSequence(seed); }
        internal bool Active { get { return track != null; } }
        internal bool Shuffle { get { return sequence.Enabled; } }
        internal int Index { get { return index; } }
        internal double Seconds(double dsp) { return track == null ? 0 : Position(dsp) / track.Frequency; }
        internal bool Owns(object member) { return track != null && members.Contains(member); }
        internal void Forget(object member) { members.Remove(member); }
        internal void Toggle() { sequence.Enabled = !sequence.Enabled; sequence.Reset(); }
        internal void SetShuffle(bool value) { if (sequence.Enabled != value) { sequence.Enabled = value; sequence.Reset(); } }
        internal void Clear() { track = null; members.Clear(); sequence.Reset(); }
        internal void Pause(double dsp)
        {
            if (paused) return;
            if (track != null) originSample = Math.Min(track.Samples - 1, (int)Position(dsp));
            originDsp = dsp; paused = true;
        }
        internal void Resume(IList<object> current, double dsp, double lead)
        {
            if (!paused) return;
            paused = false;
            if (track != null && current.Count > 0) Start(track, index, originSample, current, dsp + lead);
        }

        internal void Update(IList<CdSyncTrack> library, IList<object> current, double dsp, double lead, int initialIndex, int initialSample)
        {
            if (current.Count == 0 || library.Count == 0) { Clear(); return; }
            members.RemoveWhere(delegate(object value) { return !current.Contains(value); });
            if (track == null || index < 0 || index >= library.Count || !ReferenceEquals(track.Clip, library[index].Clip))
            {
                index = Math.Max(0, Math.Min(library.Count - 1, initialIndex));
                Start(library[index], index, Math.Max(0, Math.Min(library[index].Samples - 1, initialSample)), current, dsp + lead);
            }
            else if (Position(dsp) >= track.Samples)
            {
                int next = sequence.Next(index, library.Count);
                Start(library[next], next, 0, current, dsp + lead);
            }
            else
            {
                // Joining within the final scheduling window waits for the shared next track.
                // Clamping the requested sample would make that radio audibly behind the others.
                double start = Math.Max(dsp + lead, originDsp);
                double requested = Position(start);
                if (Math.Round(requested) < track.Samples)
                {
                    foreach (object member in current)
                    {
                        if (members.Contains(member)) continue;
                        sink.Schedule(member, track, index, (int)Math.Round(requested), start);
                        members.Add(member);
                    }
                }
            }
            double seconds = Seconds(dsp);
            foreach (object member in current) sink.State(member, index, seconds);
        }

        internal void Seek(IList<CdSyncTrack> library, IList<object> current, double dsp, double lead, bool previous)
        {
            if (!Active || library.Count == 0 || current.Count == 0) return;
            int selected = previous ? sequence.Previous(index, library.Count, (float)Seconds(dsp)) : sequence.Next(index, library.Count);
            Start(library[selected], selected, 0, current, dsp + lead);
        }
        private double Position(double dsp) { return originSample + (paused ? 0 : Math.Max(0, dsp - originDsp) * track.Frequency); }
        private void Start(CdSyncTrack selected, int selectedIndex, int sample, IList<object> current, double start)
        {
            track = selected; index = selectedIndex; originSample = sample; originDsp = start;
            members.Clear();
            foreach (object member in current)
            {
                sink.Schedule(member, track, index, sample, start);
                members.Add(member);
                sink.State(member, index, sample / (double)track.Frequency);
            }
        }
    }
}
