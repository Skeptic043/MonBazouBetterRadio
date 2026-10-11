// Adapted from skeptic043/Radio, revision 1aa3626c53ae2fb785d1bda7ef711382923a015e, src/Audio/MediaFoundationDecoder.cs.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RadioEnhancements
{
    // M4A (AAC or Apple Lossless) and ADTS AAC through a synchronous IMFSourceReader. Worker
    // threads only. Each call joins the multithreaded apartment. Frames is the container's upper
    // bound. A selected audio stream ending earlier returns a short read, so video tails do not
    // extend music playback with silence.
    internal sealed class MediaFoundationDecoder : IPcmDecoder
    {
        private const double TicksPerSecond = 10000000.0;
        // AAC frames overlap, so a seek starts several whole frames early and discards up to the target.
        private const int SeekPrerollFrames = 8192;
        private const int MaximumSourceChannels = 32;
        private readonly CancellationToken token;
        // MP4 sample tables give exact timestamps. ADTS timestamps after a seek are bitrate estimates.
        private readonly bool exactTimestamps;
        private readonly int sourceChannels;
        private readonly float[] toLeft, toRight;
        private IntPtr reader;
        private bool started, disposed;
        private float[] raw = new float[0], pending = new float[0];
        private long pendingStart;
        private int pendingCount;
        private long decodedEnd;
        private bool synced, endOfStream;

        public int Channels { get; private set; }
        public int SampleRate { get; private set; }
        public long Frames { get; private set; }
        public long Position { get; private set; }

        internal static IPcmDecoder Open(string path, CancellationToken token) { return new MediaFoundationDecoder(path, token); }

        private MediaFoundationDecoder(string path, CancellationToken token)
        {
            this.token = token;
            token.ThrowIfCancellationRequested();
            bool adts = String.Equals(Path.GetExtension(path), ".aac", StringComparison.OrdinalIgnoreCase);
            exactTimestamps = !adts;
            int adtsRate = 0;
            long adtsFrames = adts ? AdtsInfo.CountFrames(path, token, out adtsRate) : 0;
            using (new MediaFoundation.Apartment())
            {
                try
                {
                    MediaFoundation.Startup();
                    started = true;
                    int result = MediaFoundation.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader);
                    if (result < 0) throw OpenError(result);
                    Guid codec = Configure();
                    IntPtr current;
                    Check(MediaFoundation.Method<MediaFoundation.GetCurrentMediaTypeMethod>(reader, MediaFoundation.SlotGetCurrentMediaType)(
                        reader, MediaFoundation.FirstAudioStream, out current), "read the decoded audio format");
                    int mask;
                    try
                    {
                        sourceChannels = MediaFoundation.GetUInt32(current, MediaFoundation.Channels, 0);
                        SampleRate = MediaFoundation.GetUInt32(current, MediaFoundation.SamplesPerSecond, 0);
                        mask = MediaFoundation.GetUInt32(current, MediaFoundation.ChannelMask, 0);
                    }
                    finally { MediaFoundation.Release(ref current); }
                    if (sourceChannels < 1 || sourceChannels > MaximumSourceChannels) throw new IOException("The audio channel count is unsupported");
                    AudioFiles.CheckPlayableRate(SampleRate);
                    Channels = Math.Min(2, sourceChannels);
                    if (sourceChannels > 2) ChannelDownmix.Build(ChannelDownmix.FromWaveMask(sourceChannels, mask), out toLeft, out toRight);
                    // HE-AAC decodes at twice the ADTS core rate, with twice the frames per packet.
                    if (adts) Frames = adtsFrames * SampleRate / Math.Max(1, adtsRate);
                    else Frames = ReadDurationFrames();
                    if (Frames <= 0) throw new IOException(codec == MediaFoundation.Aac ? "The AAC file contains no audio" : "The M4A file contains no audio");
                }
                catch { Dispose(); throw; }
            }
        }

        // Selects the first audio stream, requests float output and returns the source codec.
        private Guid Configure()
        {
            var select = MediaFoundation.Method<MediaFoundation.SetStreamSelectionMethod>(reader, MediaFoundation.SlotSetStreamSelection);
            Check(select(reader, MediaFoundation.AllStreams, 0), "disable unused streams");
            if (select(reader, MediaFoundation.FirstAudioStream, 1) < 0) throw new IOException("The file has no audio track");
            if (StreamFlag(MediaFoundation.Protected)) throw new IOException("The file is copy-protected");
            IntPtr native;
            Guid codec = Guid.Empty;
            Check(MediaFoundation.Method<MediaFoundation.GetNativeMediaTypeMethod>(reader, MediaFoundation.SlotGetNativeMediaType)(
                reader, MediaFoundation.FirstAudioStream, 0, out native), "read the audio format");
            try { MediaFoundation.GetGuid(native, MediaFoundation.Subtype, out codec); }
            finally { MediaFoundation.Release(ref native); }
            bool alac = codec == MediaFoundation.Alac || codec == MediaFoundation.AlacFourCc;
            if (codec != MediaFoundation.Aac && !alac)
                throw new IOException("The audio is copy-protected or not AAC or Apple Lossless");
            IntPtr type;
            Check(MediaFoundation.MFCreateMediaType(out type), "create the output format");
            try
            {
                var set = MediaFoundation.Method<MediaFoundation.SetGuidMethod>(type, MediaFoundation.SlotSetGuid);
                Guid major = MediaFoundation.MajorType, audio = MediaFoundation.Audio, subtype = MediaFoundation.Subtype, sampleFormat = MediaFoundation.Float;
                Check(set(type, ref major, ref audio), "set the output format");
                Check(set(type, ref subtype, ref sampleFormat), "set the output format");
                int result = MediaFoundation.Method<MediaFoundation.SetCurrentMediaTypeMethod>(reader, MediaFoundation.SlotSetCurrentMediaType)(
                    reader, MediaFoundation.FirstAudioStream, IntPtr.Zero, type);
                if (result < 0)
                    throw new IOException((alac ? "Apple Lossless" : "AAC") + " decoding is unavailable on this system (error 0x" + result.ToString("X8") + ")");
            }
            finally { MediaFoundation.Release(ref type); }
            return alac ? MediaFoundation.Alac : codec;
        }

        private bool StreamFlag(Guid key)
        {
            IntPtr variant = MediaFoundation.NewVariant();
            try
            {
                if (MediaFoundation.Method<MediaFoundation.GetPresentationAttributeMethod>(reader, MediaFoundation.SlotGetPresentationAttribute)(
                    reader, MediaFoundation.FirstAudioStream, ref key, variant) < 0) return false;
                bool set = Marshal.ReadInt16(variant) == 19 && Marshal.ReadInt32(variant, 8) != 0; // VT_UI4
                MediaFoundation.PropVariantClear(variant);
                return set;
            }
            finally { Marshal.FreeHGlobal(variant); }
        }

        private long ReadDurationFrames()
        {
            IntPtr variant = MediaFoundation.NewVariant();
            try
            {
                Guid key = MediaFoundation.Duration;
                Check(MediaFoundation.Method<MediaFoundation.GetPresentationAttributeMethod>(reader, MediaFoundation.SlotGetPresentationAttribute)(
                    reader, MediaFoundation.MediaSource, ref key, variant), "read the track duration");
                long ticks = Marshal.ReadInt16(variant) == 21 ? Marshal.ReadInt64(variant, 8) : 0; // VT_UI8
                MediaFoundation.PropVariantClear(variant);
                return (long)Math.Round(ticks * (double)SampleRate / TicksPerSecond);
            }
            finally { Marshal.FreeHGlobal(variant); }
        }

        public void Seek(long frame)
        {
            if (disposed) throw new ObjectDisposedException("MediaFoundationDecoder");
            token.ThrowIfCancellationRequested();
            frame = Math.Max(0, Math.Min(Frames, frame));
            Position = frame;
            if (frame == Frames) return;
            if (pendingCount > 0 && frame >= pendingStart && frame < pendingStart + pendingCount) return;
            // Short forward jumps decode through the gap.
            if (synced && !endOfStream && frame >= decodedEnd && frame - decodedEnd < SampleRate * 2L) return;
            using (new MediaFoundation.Apartment())
            {
                if (!exactTimestamps)
                {
                    // Only the stream start has a known frame index in ADTS, so earlier targets restart there.
                    if (synced && !endOfStream && frame >= decodedEnd) return;
                    SetReaderPosition(0);
                    synced = true;
                    decodedEnd = 0;
                    return;
                }
                long preroll = SeekPrerollFrames;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    SetReaderPosition(attempt == 3 ? 0 : Math.Max(0, frame - preroll));
                    Fetch();
                    if (pendingCount == 0 || pendingStart <= frame) return;
                    preroll *= 8;
                }
                throw new IOException("The decoder could not seek to the requested sample");
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (disposed) throw new ObjectDisposedException("MediaFoundationDecoder");
            int wanted = (int)Math.Min(count / Channels, Frames - Position);
            if (wanted <= 0) return 0;
            int written = 0;
            using (new MediaFoundation.Apartment())
            {
                while (written < wanted)
                {
                    token.ThrowIfCancellationRequested();
                    long pendingEnd = pendingStart + pendingCount;
                    int take;
                    if (pendingCount > 0 && Position >= pendingStart && Position < pendingEnd)
                    {
                        take = (int)Math.Min(wanted - written, pendingEnd - Position);
                        Array.Copy(pending, (int)(Position - pendingStart) * Channels, buffer, offset + written * Channels, take * Channels);
                    }
                    else if (pendingCount > 0 && Position < pendingStart)
                    {
                        // A gap before the next decoded sample is silence.
                        take = (int)Math.Min(wanted - written, pendingStart - Position);
                        Array.Clear(buffer, offset + written * Channels, take * Channels);
                    }
                    else if (endOfStream)
                    {
                        break;
                    }
                    else
                    {
                        Fetch();
                        continue;
                    }
                    written += take;
                    Position += take;
                }
            }
            return written * Channels;
        }

        // Replaces pending with the next decoded sample, converted to the output layout.
        private void Fetch()
        {
            token.ThrowIfCancellationRequested();
            int actual, flags;
            long time;
            IntPtr sample;
            int result = MediaFoundation.Method<MediaFoundation.ReadSampleMethod>(reader, MediaFoundation.SlotReadSample)(
                reader, MediaFoundation.FirstAudioStream, 0, out actual, out flags, out time, out sample);
            try
            {
                if (result < 0 || (flags & MediaFoundation.ReaderError) != 0)
                    throw new IOException("The audio data is damaged or truncated" + (result < 0 ? " (error 0x" + result.ToString("X8") + ")" : ""));
                if ((flags & MediaFoundation.ReaderTypeChanged) != 0) CheckFormatUnchanged();
                if ((flags & MediaFoundation.ReaderEndOfStream) != 0) endOfStream = true;
                pendingCount = 0;
                if (sample == IntPtr.Zero) return;
                int frames = Convert(sample);
                long start = synced || !exactTimestamps ? decodedEnd : (long)Math.Round(time * (double)SampleRate / TicksPerSecond);
                synced = true;
                pendingStart = start;
                pendingCount = frames;
                decodedEnd = start + frames;
            }
            finally { MediaFoundation.Release(ref sample); }
        }

        private int Convert(IntPtr sample)
        {
            IntPtr buffer;
            Check(MediaFoundation.Method<MediaFoundation.GetPointerMethod>(sample, MediaFoundation.SlotConvertToContiguousBuffer)(sample, out buffer),
                "read decoded audio");
            try
            {
                IntPtr data;
                int maximum, bytes;
                Check(MediaFoundation.Method<MediaFoundation.LockMethod>(buffer, MediaFoundation.SlotLock)(buffer, out data, out maximum, out bytes),
                    "read decoded audio");
                int frames = bytes / (4 * sourceChannels);
                try
                {
                    if (raw.Length < frames * sourceChannels) raw = new float[frames * sourceChannels];
                    Marshal.Copy(data, raw, 0, frames * sourceChannels);
                }
                finally { MediaFoundation.Method<MediaFoundation.UnlockMethod>(buffer, MediaFoundation.SlotUnlock)(buffer); }
                if (pending.Length < frames * Channels) pending = new float[frames * Channels];
                if (toLeft == null) Array.Copy(raw, pending, frames * Channels);
                else ChannelDownmix.Mix(raw, sourceChannels, toLeft, toRight, frames, pending, 0);
                return frames;
            }
            finally { MediaFoundation.Release(ref buffer); }
        }

        private void CheckFormatUnchanged()
        {
            IntPtr current;
            Check(MediaFoundation.Method<MediaFoundation.GetCurrentMediaTypeMethod>(reader, MediaFoundation.SlotGetCurrentMediaType)(
                reader, MediaFoundation.FirstAudioStream, out current), "read the decoded audio format");
            try
            {
                if (MediaFoundation.GetUInt32(current, MediaFoundation.Channels, 0) != sourceChannels ||
                    MediaFoundation.GetUInt32(current, MediaFoundation.SamplesPerSecond, 0) != SampleRate)
                    throw new IOException("The audio format changed during decoding");
            }
            finally { MediaFoundation.Release(ref current); }
        }

        private void SetReaderPosition(long frame)
        {
            IntPtr variant = MediaFoundation.NewVariant();
            try
            {
                Marshal.WriteInt16(variant, 20); // VT_I8, in 100 nanosecond units
                Marshal.WriteInt64(variant, 8, (long)(frame * TicksPerSecond / SampleRate));
                Guid format = Guid.Empty;
                Check(MediaFoundation.Method<MediaFoundation.SetCurrentPositionMethod>(reader, MediaFoundation.SlotSetCurrentPosition)(
                    reader, ref format, variant), "seek");
            }
            finally { Marshal.FreeHGlobal(variant); }
            pendingCount = 0;
            synced = false;
            endOfStream = false;
        }

        private static Exception OpenError(int result)
        {
            const string Missing = "Music file is missing or inaccessible";
            switch ((uint)result)
            {
                case 0x80070002: return new FileNotFoundException(Missing);
                case 0x80070003: return new DirectoryNotFoundException(Missing);
                case 0x80070005: return new UnauthorizedAccessException(Missing);
                case 0x80070020: return new IOException(Missing);
                case 0xC00D36C4: return new IOException("The file is not a readable AAC, M4A or MP4 stream");
                case 0xC00D36E5: return new IOException("The file is damaged or truncated");
                default: return new IOException("Media Foundation could not open the file (error 0x" + result.ToString("X8") + ")");
            }
        }

        private static void Check(int result, string action)
        {
            if (result < 0) throw new IOException("Media Foundation could not " + action + " (error 0x" + result.ToString("X8") + ")");
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            using (new MediaFoundation.Apartment())
            {
                try { MediaFoundation.Release(ref reader); }
                finally
                {
                    if (started) MediaFoundation.MFShutdown();
                    started = false;
                }
            }
        }
    }

    // Exact ADTS length from frame headers. Media Foundation estimates it from the bitrate.
    internal static class AdtsInfo
    {
        private static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        // Returns decoded frames at the ADTS core rate. Leading ID3v2 tags and trailing tags are skipped.
        internal static long CountFrames(string path, CancellationToken token, out int rate)
        {
            rate = 0;
            using (var input = new AudioFiles.CancellableFileStream(path, token))
            {
                try { AudioFiles.SkipId3v2(input); }
                catch (InvalidDataException) { throw new IOException("The AAC file has a damaged ID3 tag"); }
                var block = new byte[65536];
                long blockStart = input.Position, at = blockStart, frames = 0, length = input.Length;
                int filled = 0;
                while (length - at >= 7)
                {
                    if (at + 7 > blockStart + filled)
                    {
                        token.ThrowIfCancellationRequested();
                        input.Position = at;
                        blockStart = at;
                        filled = 0;
                        int got;
                        while (filled < block.Length && (got = input.Read(block, filled, block.Length - filled)) > 0) filled += got;
                        if (filled < 7) break;
                    }
                    int i = (int)(at - blockStart);
                    if (block[i] != 0xFF || (block[i + 1] & 0xF6) != 0xF0) break;
                    int index = (block[i + 2] >> 2) & 0xF;
                    int size = (block[i + 3] & 3) << 11 | block[i + 4] << 3 | block[i + 5] >> 5;
                    if (index >= Rates.Length || size < 7) break;
                    if (rate == 0) rate = Rates[index];
                    else if (rate != Rates[index]) throw new IOException("The AAC file changes sample rate");
                    if (size > length - at) break; // A truncated final frame does not decode.
                    frames += ((block[i + 6] & 3) + 1) * 1024L;
                    at += size;
                }
                if (frames == 0) throw new IOException("The file is not an ADTS AAC stream");
                return frames;
            }
        }
    }
}
