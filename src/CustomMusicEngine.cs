using System;

namespace RadioEnhancements
{
    internal sealed class CustomMusicTrack
    {
        internal readonly CustomMusicEntry Entry;
        internal readonly StreamedAudio Stream;
        internal int Samples { get { return Stream.Frames; } }
        internal int Frequency { get { return Stream.SampleRate; } }
        internal CustomMusicTrack(CustomMusicEntry entry, double seconds)
        { Entry = entry; Stream = StreamedAudio.Start(entry.Path, seconds); }
    }

    // Session owns only the selected song and one prospective next song. AudioClip objects
    // belong to the main-thread output adapter; workers own only decoder and PCM buffers.
    internal sealed class CustomMusicEngine : IDisposable
    {
        internal CustomMusicTrack Current { get; private set; }
        internal CustomMusicTrack Prefetched { get; private set; }
        internal CustomMusicTrack Prepare(CustomMusicEntry entry, double seconds)
        {
            if (Current != null && ReferenceEquals(Current.Entry, entry)) return Current;
            if (Current != null) Current.Stream.Dispose();
            if (Prefetched != null && ReferenceEquals(Prefetched.Entry, entry))
            { Current = Prefetched; Prefetched = null; }
            else Current = new CustomMusicTrack(entry, seconds);
            return Current;
        }
        internal void Prefetch(CustomMusicEntry entry)
        {
            if (entry == null || (Current != null && ReferenceEquals(Current.Entry, entry)))
            { CancelPrefetch(); return; }
            if (Prefetched != null && ReferenceEquals(Prefetched.Entry, entry)) return;
            CancelPrefetch(); Prefetched = new CustomMusicTrack(entry, 0);
        }
        private void CancelPrefetch()
        { if (Prefetched == null) return; Prefetched.Stream.Dispose(); Prefetched = null; }
        public void Dispose()
        { if (Current != null) Current.Stream.Dispose(); Current = null; CancelPrefetch(); }
    }
}
