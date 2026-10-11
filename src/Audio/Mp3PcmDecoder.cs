// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.IO;
using System.Threading;

namespace RadioEnhancements
{
    // Keeps every pinned NLayer 1.16 quirk behind exact seeks, so shared engines never see them.
    internal sealed class Mp3PcmDecoder : IPcmDecoder
    {
        private const int SeekPrerollSeconds = 5;
        private const int DiscardFrames = 4096;
        // NLayer indexes every frame when it opens a file, so its time and memory grow with the file.
        private const long MaximumFileBytes = 1L << 30;
        private readonly string path;
        private readonly CancellationToken token;
        private Stream input;
        private NLayer.MpegFile decoder;
        private float[] discard;
        private bool used;

        public int Channels { get; private set; }
        public int SampleRate { get; private set; }
        public long Frames { get; private set; }
        public long Position { get; private set; }

        internal static IPcmDecoder Open(string path, CancellationToken token) { return new Mp3PcmDecoder(path, token); }

        private Mp3PcmDecoder(string path, CancellationToken token)
        {
            this.path = path;
            this.token = token;
            token.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > MaximumFileBytes) throw new IOException("Audio file is larger than 1 GB");
            try
            {
                // Own the stream outside MpegFile so a malformed-header constructor failure
                // still closes the file. Read/seek cancellation covers header scanning too.
                input = new AudioFiles.CancellableFileStream(path, token);
                decoder = new NLayer.MpegFile(input);
                Channels = decoder.Channels;
                SampleRate = decoder.SampleRate;
                if (Channels < 1 || Channels > 2 || SampleRate < 8000 || SampleRate > 96000)
                    throw new IOException("Unsupported MP3 channel count or sample rate");
                // NLayer indexes every frame to report its length. Zero means unknown, and the
                // stream engine needs the length before its first read.
                long bytes = decoder.Length;
                Frames = bytes <= 0 ? 0 : bytes / (Channels * sizeof(float));
                if (Frames <= 0) throw new IOException("MP3 length could not be determined");
            }
            catch { Dispose(); throw; }
        }

        public void Seek(long frame)
        {
            frame = Math.Max(0, Frames > 0 ? Math.Min(Frames, frame) : frame);
            if (frame == Position) return;
            // Reopen on a discontinuity. NLayer can retain a stale read
            // buffer across a backward seek on unindexed MP3s.
            if (used) Reopen();
            used = true;
            // MP3 bit reservoirs and decoder state need a short preroll.
            // Starting from zero also avoids NLayer's short-file seek bug.
            long anchor = Math.Max(0, frame - (long)SeekPrerollSeconds * SampleRate);
            if (anchor > 0)
            {
                if (!decoder.CanSeek) throw new IOException("MP3 cannot seek");
                decoder.Position = anchor * Channels * sizeof(float);
            }
            long actual = decoder.Position / (Channels * sizeof(float));
            if (anchor > 0)
            {
                // Pinned NLayer 1.16 reports the preceding frame's PCM
                // position after an indexed seek, although its next read
                // starts one MPEG Layer III frame later.
                actual += SampleRate >= 32000 ? 1152 : 576;
            }
            if (actual > frame) throw new IOException("MP3 decoder seek passed the requested sample");
            if (discard == null) discard = new float[DiscardFrames * Channels];
            while (actual < frame)
            {
                token.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(frame - actual, DiscardFrames) * Channels;
                int got = decoder.ReadSamples(discard, 0, wanted);
                if (got <= 0 || got % Channels != 0) throw new IOException("MP3 decoder could not finish seek");
                actual += got / Channels;
            }
            Position = frame;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            used = true;
            count -= count % Channels;
            if (count <= 0) return 0;
            int got = decoder.ReadSamples(buffer, offset, count);
            if (got <= 0) return 0;
            if (got % Channels != 0) throw new IOException("MP3 decoder returned incomplete sample frames");
            Position += got / Channels;
            return got;
        }

        private void Reopen()
        {
            Stream oldInput = input;
            NLayer.MpegFile oldDecoder = decoder;
            decoder = null;
            input = null;
            try { oldDecoder.Dispose(); }
            finally { oldInput.Dispose(); }
            input = new AudioFiles.CancellableFileStream(path, token);
            decoder = new NLayer.MpegFile(input);
            Position = 0;
        }

        public void Dispose()
        {
            try { if (decoder != null) decoder.Dispose(); }
            finally
            {
                decoder = null;
                if (input != null) input.Dispose();
                input = null;
            }
        }
    }
}
