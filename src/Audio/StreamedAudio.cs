// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace RadioEnhancements
{
    // One decoder feeds all outputs. Its own worker opens the file, so the Unity thread never
    // touches disk or decoding. Audio callbacks only copy published, immutable PCM chunks and
    // never touch the decoder, disk, or Unity objects.
    internal sealed class StreamedAudio : IDisposable
    {
        private const int ChunkFrames = 4096;
        private const int WindowSeconds = 20;
        private const int AheadSeconds = 10;
        // Two seconds of PCM are prepared before scheduling. Unity 2021.2's native streaming
        // pre-read still requires a live player check; missing-frame telemetry measures underruns.
        private const int ReadyMilliseconds = 2000;
        private const int NoFrame = Int32.MaxValue;
        private readonly string path;
        private readonly double initialSeconds;
        private readonly Func<string, CancellationToken, IPcmDecoder> opener;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        // Wakes the idle worker when the main thread moves its target or changes looping.
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Task worker;
        private IPcmDecoder decoder;
        private Chunk[] chunks;
        // Ring slot plus one for each track chunk, or zero. Readers still check the slot's Start,
        // because the worker may reuse it at any moment.
        private int[] slots;
        private long requestedFrame;
        private volatile bool loop;
        // Worker only. Frames from here on are silence because the decoder ended early or failed.
        private int dataEnd = NoFrame;
        // Worker only. A fill has completed, so a later decode failure ends playback instead of failing it.
        private bool produced;
        private bool reseek;
        private int channels, sampleRate, frames;
        private Exception fault, damage;
        private int damageFrame = NoFrame, loopDamageFrame = NoFrame;
        private volatile bool opened;
        private volatile bool disposed;
        private int missingFrames;
        // Layout fields are published by the worker before IsOpen becomes true.
        internal bool IsOpen { get { return opened; } }
        internal int Channels { get { return channels; } }
        internal int SampleRate { get { return sampleRate; } }
        internal int Frames { get { return frames; } }
        internal int BufferSamples { get { return opened ? chunks.Length * ChunkFrames * channels : 0; } }
        internal bool WorkerCompleted { get { return worker.IsCompleted; } }
        // The stream failed before producing audio, so the track cannot play.
        internal Exception Fault { get { return Volatile.Read(ref fault); } }
        // First frame lost to a decode failure at or ahead of the worker target after audio was produced,
        // or Int32.MaxValue. Frames before it play normally and frames from it on are silence.
        internal int DamageFrame { get { return Volatile.Read(ref damageFrame); } }
        internal int EndFrame { get { return Math.Min(Frames, Volatile.Read(ref dataEnd)); } }
        // The same for a failure in the looping look-ahead, which lies behind the playhead until it wraps.
        internal int LoopDamageFrame { get { return Volatile.Read(ref loopDamageFrame); } }
        internal Exception Damage { get { return Volatile.Read(ref damage); } }
        // Diagnostic count of frames Unity read before their chunk was decoded, so they played as silence unless
        // a later seek discarded them. The editor probe observes start underruns with it.
        internal int MissingFrames { get { return Volatile.Read(ref missingFrames); } }
        // Main thread. When set, the look-ahead continues from the start of the track past its end.
        internal bool Loop
        {
            get { return loop; }
            set
            {
                if (loop == value) return;
                loop = value;
                Wake();
            }
        }

        private sealed class Chunk
        {
            // -1 means the worker owns the slot, positive values are active readers.
            internal int State;
            internal int Start, Count;
            internal readonly float[] Samples;
            internal Chunk(int channels) { Samples = new float[ChunkFrames * channels]; }
        }

        internal sealed class Reader
        {
            private readonly StreamedAudio owner;
            private int cursor;
            private int generation;
            internal Reader(StreamedAudio owner) { this.owner = owner; }
            internal int Cursor { get { return Volatile.Read(ref cursor); } }
            internal int Generation { get { return Volatile.Read(ref generation); } }
            // Moves only this reader. The main thread alone decides what the worker decodes.
            internal void SetPosition(int position)
            {
                position = Math.Max(0, Math.Min(owner.Frames - 1, position));
                Interlocked.Increment(ref generation);
                Volatile.Write(ref cursor, position);
            }
            internal void Read(float[] data)
            {
                Array.Clear(data, 0, data.Length);
                if (owner.disposed) return;
                int observedGeneration = Volatile.Read(ref generation);
                int start = Volatile.Read(ref cursor);
                int frames = data.Length / owner.Channels;
                owner.Copy(start, data, frames);
                int advanced = start > owner.Frames - frames ? owner.Frames : start + frames;
                CommitRead(observedGeneration, start, advanced);
            }
            internal void CommitRead(int observedGeneration, int start, int advanced)
            {
                if (Volatile.Read(ref generation) == observedGeneration)
                    Interlocked.CompareExchange(ref cursor, advanced, start);
            }
        }

        // Returns at once. Open failures appear as Fault, and disposal cancels an unfinished open.
        internal static StreamedAudio Start(string path, double initialSeconds) { return new StreamedAudio(path, initialSeconds, PcmDecoders.Open); }
        // Per-stream decoder injection keeps fault/cancellation checks independent of global registration.
        internal static StreamedAudio Start(string path, double initialSeconds, Func<string, CancellationToken, IPcmDecoder> opener)
        { return new StreamedAudio(path, initialSeconds, opener); }

        private StreamedAudio(string path, double initialSeconds, Func<string, CancellationToken, IPcmDecoder> opener)
        {
            this.path = path;
            this.initialSeconds = initialSeconds;
            this.opener = opener;
            // A dedicated thread, because the loop lives as long as the track.
            worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal Reader CreateReader() { return new Reader(this); }
        internal AudioClip CreateClip(Reader reader)
        {
            if (disposed) throw new ObjectDisposedException("StreamedAudio");
            if (!opened) throw new InvalidOperationException("Streamed audio is not open yet");
            return AudioClip.Create("Better Radio streamed audio", Frames, Channels, SampleRate, true,
                reader.Read, reader.SetPosition);
        }

        // Main thread. The frame the worker keeps ten seconds ahead of.
        internal void Request(int frame)
        {
            if (!opened || disposed) return;
            long target = Math.Max(0, Math.Min(Frames - 1, frame));
            if (Interlocked.Exchange(ref requestedFrame, target) != target) Wake();
        }

        private void Wake()
        {
            if (disposed) return;
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
        }

        // True once the audio Unity pre-reads from frame is decoded, continuing from the start when looping.
        internal bool Ready(int frame)
        {
            if (disposed || !opened) return false;
            frame = Math.Max(0, Math.Min(Frames - 1, frame));
            long end = (long)frame + Math.Max(1L, (long)SampleRate * ReadyMilliseconds / 1000);
            if (!Decoded(frame, (int)Math.Min(Frames, end))) return false;
            return !loop || end <= Frames || Decoded(0, (int)Math.Min(frame, end - Frames));
        }

        private bool Decoded(int from, int to)
        {
            for (int at = from; at < to; at = ((at / ChunkFrames) + 1) * ChunkFrames)
            {
                int index = at / ChunkFrames, slot = Volatile.Read(ref slots[index]) - 1;
                if (slot < 0) return false;
                Chunk chunk = chunks[slot];
                if (Volatile.Read(ref chunk.State) < 0 || chunk.Start != index * ChunkFrames || chunk.Count <= 0) return false;
            }
            return true;
        }

        private void Copy(int frame, float[] destination, int frames)
        {
            if (frame < 0 || frame >= Frames) return;
            int at = frame, end = (int)Math.Min((long)Frames, (long)frame + frames);
            while (at < end)
            {
                int start = at / ChunkFrames * ChunkFrames;
                int take = Math.Min(end - at, start + ChunkFrames - at);
                int slot = Volatile.Read(ref slots[start / ChunkFrames]) - 1;
                bool copied = false;
                if (slot >= 0)
                {
                    Chunk chunk = chunks[slot];
                    int readers;
                    do { readers = Volatile.Read(ref chunk.State); }
                    while (readers >= 0 && Interlocked.CompareExchange(ref chunk.State, readers + 1, readers) != readers);
                    if (readers >= 0)
                    {
                        try
                        {
                            if (chunk.Start == start && at + take <= start + chunk.Count)
                            {
                                Array.Copy(chunk.Samples, (at - start) * Channels, destination, (at - frame) * Channels, take * Channels);
                                copied = true;
                            }
                        }
                        finally { Interlocked.Decrement(ref chunk.State); }
                    }
                }
                if (!copied) Interlocked.Add(ref missingFrames, take);
                at += take; // Missing chunks are silence, never a blocking read.
            }
        }

        private void Open(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("Music file is missing or inaccessible");
            decoder = opener(path, token);
            int layoutChannels = decoder.Channels, rate = decoder.SampleRate;
            long length = decoder.Frames;
            if (length <= 0) throw new IOException("The file contains no decodable audio");
            AudioFiles.CheckPlayableRate(rate);
            // Unity AudioClip.Create and AudioSource.timeSamples both use int frames.
            if (length > Int32.MaxValue - rate * 2L)
                throw new IOException("Track duration exceeds Unity's sample-position limit");
            // A track shorter than the window is held whole, so repeats and backward seeks never decode again.
            int trackChunks = (int)((length + ChunkFrames - 1) / ChunkFrames);
            int windowChunks = (WindowSeconds * rate + ChunkFrames - 1) / ChunkFrames;
            // A cancelled open allocates no window and is never published as open.
            token.ThrowIfCancellationRequested();
            var ring = new Chunk[Math.Max(1, Math.Min(trackChunks, windowChunks))];
            for (int i = 0; i < ring.Length; i++) ring[i] = new Chunk(layoutChannels);
            double seconds = initialSeconds;
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            channels = layoutChannels;
            sampleRate = rate;
            frames = (int)length;
            chunks = ring;
            slots = new int[trackChunks];
            requestedFrame = (long)Math.Min(length - 1, Math.Floor(seconds * rate));
            token.ThrowIfCancellationRequested();
            opened = true;
        }

        private void Run()
        {
            CancellationToken token = cancellation.Token;
            try
            {
                Open(token);
                var signals = new[] { wake, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    int target = (int)Interlocked.Read(ref requestedFrame);
                    int first, forwardEnd, wrapEnd;
                    Window(target, loop, out first, out forwardEnd, out wrapEnd);
                    // Fill the first chunk missing between the playhead and the look-ahead limit. Chunks
                    // already held are kept, so wraps and short seeks inside the window cost nothing.
                    int missing = -1;
                    for (int index = first; index < forwardEnd && missing < 0; index++) if (!Holds(index)) missing = index;
                    bool wrapped = missing < 0;
                    for (int index = 0; index < wrapEnd && missing < 0; index++) if (!Holds(index)) missing = index;
                    // Request, Loop and Dispose all signal, so an idle worker needs no timed poll.
                    if (missing < 0) { WaitHandle.WaitAny(signals); continue; }
                    Fill(missing, Victim(first, forwardEnd, wrapEnd), wrapped, token);
                }
            }
            catch (OperationCanceledException ex) { if (!token.IsCancellationRequested) Volatile.Write(ref fault, ex); }
            catch (Exception ex) { Volatile.Write(ref fault, ex); }
            finally { if (decoder != null) decoder.Dispose(); }
        }

        // Worker only. The chunks the next ten seconds need: forward from the target to the end of the
        // track, then from its start when looping. A short track never lists a chunk twice.
        private void Window(int target, bool looping, out int first, out int forwardEnd, out int wrapEnd)
        {
            long ahead = (long)target + (long)AheadSeconds * sampleRate;
            first = target / ChunkFrames;
            forwardEnd = (int)((Math.Min(ahead, frames) + ChunkFrames - 1) / ChunkFrames);
            wrapEnd = looping && ahead > frames ? (int)Math.Min(first, (ahead - frames + ChunkFrames - 1) / ChunkFrames) : 0;
        }

        // Worker only. It is the sole writer of Start, Count and the slot map.
        private bool Holds(int index)
        {
            int slot = slots[index] - 1;
            if (slot < 0) return false;
            Chunk chunk = chunks[slot];
            return chunk.Start == index * ChunkFrames && chunk.Count == Math.Min(ChunkFrames, frames - index * ChunkFrames);
        }

        // Worker only. An empty slot, otherwise the one whose chunk lies furthest behind the target
        // outside the window. The ring outgrows the window, and a track held whole always has an empty
        // slot for its missing chunk, so a slot is always found.
        private int Victim(int first, int forwardEnd, int wrapEnd)
        {
            int best = -1, bestDistance = -1, trackChunks = slots.Length;
            for (int slot = 0; slot < chunks.Length; slot++)
            {
                Chunk chunk = chunks[slot];
                if (chunk.Count == 0) return slot;
                int index = chunk.Start / ChunkFrames;
                if ((index >= first && index < forwardEnd) || index < wrapEnd) continue;
                int distance = (first - index + trackChunks) % trackChunks;
                if (distance > bestDistance) { best = slot; bestDistance = distance; }
            }
            if (best < 0) throw new InvalidOperationException("Streamed audio window has no free slot");
            return best;
        }

        private void Fill(int index, int slot, bool wrapped, CancellationToken token)
        {
            Chunk chunk = chunks[slot];
            int start = index * ChunkFrames;
            while (Interlocked.CompareExchange(ref chunk.State, -1, 0) != 0)
            {
                token.ThrowIfCancellationRequested();
                token.WaitHandle.WaitOne(1);
            }
            try
            {
                // Invalidate first, so an interrupted fill never exposes a mix of old and new PCM.
                int previous = chunk.Start / ChunkFrames;
                if (chunk.Count > 0 && slots[previous] == slot + 1) Volatile.Write(ref slots[previous], 0);
                chunk.Count = 0;
                chunk.Start = start;
                int count = Math.Min(ChunkFrames, frames - start);
                int read = Decode(chunk.Samples, start, count, wrapped, token);
                for (int i = 0; i < read; i++)
                    if (Single.IsNaN(chunk.Samples[i]) || Single.IsInfinity(chunk.Samples[i])) chunk.Samples[i] = 0f;
                    else chunk.Samples[i] = Math.Max(-1f, Math.Min(1f, chunk.Samples[i]));
                Array.Clear(chunk.Samples, read, count * channels - read);
                chunk.Count = count;
            }
            finally { Volatile.Write(ref chunk.State, 0); }
            // Published after the chunk, so a reader that finds the slot also finds its PCM.
            Volatile.Write(ref slots[index], slot + 1);
        }

        // Worker only. Decodes up to count frames at start and returns the samples written. Frames past a
        // clean early end or a decode failure stay silent. A failure before any audio faults the stream.
        private int Decode(float[] samples, int start, int count, bool wrapped, CancellationToken token)
        {
            int wanted = (int)Math.Min(count, Math.Max(0L, (long)dataEnd - start)) * channels;
            if (wanted == 0) return 0;
            int read = 0;
            try
            {
                if (reseek || decoder.Position != start)
                {
                    // Decoders own any preroll or reopen needed for an exact seek.
                    decoder.Seek(start);
                    if (decoder.Position != start) throw new IOException("Decoder seek missed the requested sample");
                    reseek = false;
                }
                while (read < wanted)
                {
                    token.ThrowIfCancellationRequested();
                    int got = decoder.Read(samples, read, wanted - read);
                    if (got <= 0) break;
                    read += got;
                }
                if (read % channels != 0) throw new IOException("Decoder returned an incomplete frame");
                if (read < wanted)
                {
                    if (start == 0 && read == 0) throw new IOException("The file contains no decodable audio");
                    // A clean early end. The reported remainder plays as silence.
                    dataEnd = start + read / channels;
                }
                produced = true;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && token.IsCancellationRequested) throw;
                if (!produced) throw;
                // Audio already played. It continues up to the damage, which the main thread treats as the end.
                read -= read % channels;
                int at = start + read / channels;
                dataEnd = Math.Min(dataEnd, at);
                reseek = true;
                if (damage == null) Volatile.Write(ref damage, ex);
                // Kept apart, so a failure behind the playhead never moves the end of the audio ahead of it.
                if (wrapped) { if (at < loopDamageFrame) Volatile.Write(ref loopDamageFrame, at); }
                else if (at < damageFrame) Volatile.Write(ref damageFrame, at);
            }
            return read;
        }

        public void Dispose()
        {
            if (disposed) return;
            cancellation.Cancel();
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
            disposed = true;
            // Never wait for the worker. It disposes its decoder, then its token source and signal are released.
            worker.ContinueWith(done =>
            {
                if (done.IsFaulted) { var observed = done.Exception; }
                cancellation.Dispose();
                wake.Dispose();
            }, TaskScheduler.Default);
        }
    }
}
