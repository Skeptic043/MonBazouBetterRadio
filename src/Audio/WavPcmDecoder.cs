// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RadioEnhancements
{
    // RIFF/WAVE integer and float PCM. Larger layouts downmix to stereo with fixed gains.
    internal sealed class WavPcmDecoder : IPcmDecoder
    {
        private const int FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;
        private const int ScratchFrames = 4096;
        private const int MaximumSourceChannels = 32;
        private static readonly byte[] SubformatTail = { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 };
        private readonly CancellationToken token;
        private Stream input;
        private readonly long dataStart;
        private readonly int sourceChannels, bytesPerSample, blockAlign;
        private readonly bool isFloat;
        // Per source channel gains into left and right. Null for mono and stereo passthrough.
        private readonly float[] toLeft, toRight;
        private byte[] scratch;
        private float[] samples;

        public int Channels { get; private set; }
        public int SampleRate { get; private set; }
        public long Frames { get; private set; }
        public long Position { get; private set; }

        internal static IPcmDecoder Open(string path, CancellationToken token) { return new WavPcmDecoder(path, token); }

        private WavPcmDecoder(string path, CancellationToken token)
        {
            this.token = token;
            token.ThrowIfCancellationRequested();
            try
            {
                input = new AudioFiles.CancellableFileStream(path, token);
                var header = new byte[12];
                if (!AudioFiles.ReadFully(input, header, 12)) throw new IOException("WAV header is truncated");
                string riff = Encoding.ASCII.GetString(header, 0, 4);
                if (riff == "RF64" || riff == "BW64") throw new IOException("RF64 WAV files are not supported");
                if (riff == "RIFX") throw new IOException("Big-endian WAV files are not supported");
                if (riff != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE") throw new IOException("The file is not a RIFF WAVE file");
                long fileLength = input.Length;
                byte[] format = null;
                long dataBytes = -1;
                var chunk = new byte[8];
                long at = 12;
                while (format == null || dataBytes < 0)
                {
                    input.Position = at;
                    if (!AudioFiles.ReadFully(input, chunk, 8))
                        throw new IOException(format == null ? "WAV has no format chunk" : "WAV has no data chunk");
                    string id = Encoding.ASCII.GetString(chunk, 0, 4);
                    long size = BitConverter.ToUInt32(chunk, 4);
                    at += 8;
                    if (id == "fmt ")
                    {
                        if (size < 16 || size > 1024) throw new IOException("WAV format chunk is invalid");
                        format = new byte[size];
                        if (!AudioFiles.ReadFully(input, format, (int)size)) throw new IOException("WAV format chunk is truncated");
                    }
                    else if (id == "data")
                    {
                        dataStart = at;
                        // Streaming writers can leave a placeholder size. Use what exists.
                        dataBytes = Math.Min(size, Math.Max(0, fileLength - at));
                    }
                    // Chunks are word aligned. An odd size is followed by one pad byte.
                    at += size + (size & 1);
                }
                int tag = BitConverter.ToUInt16(format, 0);
                sourceChannels = BitConverter.ToUInt16(format, 2);
                SampleRate = BitConverter.ToInt32(format, 4);
                blockAlign = BitConverter.ToUInt16(format, 12);
                int bits = BitConverter.ToUInt16(format, 14);
                int channelMask = 0;
                if (tag == FormatExtensible)
                {
                    if (format.Length < 40 || BitConverter.ToUInt16(format, 16) < 22) throw new IOException("WAV extensible format chunk is truncated");
                    channelMask = BitConverter.ToInt32(format, 20);
                    for (int i = 0; i < SubformatTail.Length; i++)
                        if (format[26 + i] != SubformatTail[i]) throw new IOException("WAV uses an unsupported extensible subformat");
                    tag = BitConverter.ToUInt16(format, 24);
                }
                if (tag != FormatPcm && tag != FormatFloat)
                    throw new IOException("WAV uses compressed format 0x" + tag.ToString("X4") + " which is not supported. Use PCM or float WAV");
                isFloat = tag == FormatFloat;
                if (isFloat ? bits != 32 : bits != 8 && bits != 16 && bits != 24 && bits != 32)
                    throw new IOException("WAV uses unsupported " + bits + "-bit " + (isFloat ? "float" : "integer") + " samples");
                bytesPerSample = bits / 8;
                if (sourceChannels < 1 || sourceChannels > MaximumSourceChannels) throw new IOException("WAV channel count is unsupported");
                if (blockAlign != sourceChannels * bytesPerSample) throw new IOException("WAV block alignment is inconsistent");
                AudioFiles.CheckPlayableRate(SampleRate);
                Frames = dataBytes / blockAlign;
                if (Frames <= 0) throw new IOException("WAV contains no audio frames");
                Channels = Math.Min(2, sourceChannels);
                if (sourceChannels > 2) ChannelDownmix.Build(ChannelDownmix.FromWaveMask(sourceChannels, channelMask), out toLeft, out toRight);
                input.Position = dataStart;
            }
            catch { Dispose(); throw; }
        }

        public void Seek(long frame)
        {
            frame = Math.Max(0, Math.Min(Frames, frame));
            input.Position = dataStart + frame * blockAlign;
            Position = frame;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int wanted = (int)Math.Min(count / Channels, Frames - Position);
            if (wanted <= 0) return 0;
            if (scratch == null) scratch = new byte[ScratchFrames * blockAlign];
            if (toLeft != null && samples == null) samples = new float[ScratchFrames * sourceChannels];
            int done = 0;
            while (done < wanted)
            {
                token.ThrowIfCancellationRequested();
                int take = Math.Min(wanted - done, ScratchFrames);
                if (!AudioFiles.ReadFully(input, scratch, take * blockAlign)) throw new IOException("WAV ended before the end of its data chunk");
                int at = offset + done * Channels;
                if (toLeft == null)
                    for (int i = 0; i < take * Channels; i++) buffer[at + i] = Sample(scratch, i * bytesPerSample);
                else
                {
                    for (int i = 0; i < take * sourceChannels; i++) samples[i] = Sample(scratch, i * bytesPerSample);
                    ChannelDownmix.Mix(samples, sourceChannels, toLeft, toRight, take, buffer, at);
                }
                done += take;
            }
            Position += done;
            return done * Channels;
        }

        private float Sample(byte[] bytes, int at)
        {
            switch (bytesPerSample)
            {
                case 1: return (bytes[at] - 128) / 128f;
                case 2: return (short)(bytes[at] | bytes[at + 1] << 8) / 32768f;
                case 3: return ((bytes[at] << 8 | bytes[at + 1] << 16 | bytes[at + 2] << 24) >> 8) / 8388608f;
                default:
                    if (isFloat) return BitConverter.ToSingle(bytes, at);
                    return BitConverter.ToInt32(bytes, at) / 2147483648f;
            }
        }

        public void Dispose()
        {
            if (input != null) input.Dispose();
            input = null;
        }
    }
}
