// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RadioEnhancements
{
    // Streaming Ogg Vorbis decoder. OggReader supplies packets of the first Vorbis logical stream and
    // VorbisCodec synthesises them. This layer owns stream positions: it applies the Vorbis beginning and
    // end of stream trimming from granule positions, seeks exactly by bisecting pages and priming with the
    // packet before the target, bridges lost pages with silence, and folds more than two channels to
    // stereo. It buffers at most one packet of output. Worker threads only.
    //
    // Positions. Raw positions are granule positions. The first audio packet only primes the overlap and the
    // output of the next packet starts at startGranule, which is the first audio page granule minus the
    // samples its packets produce. A negative startGranule is discarded from the beginning. Output frame f is
    // raw position baseGranule + f, and Frames ends at the granule of the last page of the stream, which
    // trims the end. When the first granule page is also the last, the specification trims the end instead.
    internal sealed class VorbisDecoder : IPcmDecoder
    {
        internal const int PacketLimit = 8 << 20;
        private const int MaxQueue = 512;
        private const int MaxStepBack = 64;

        private readonly Stream input;
        private readonly bool leaveOpen;
        private readonly CancellationToken token;
        private readonly OggReader ogg;
        private readonly VorbisCodec codec;
        private readonly int outputChannels, sampleRate;
        private readonly uint serial;
        private readonly long firstAudioOffset, firstGranule, finalPageOffset, finalGranule, linkEnd;
        private readonly long startGranule, baseGranule, frames;
        private readonly uint setupSequence;
        private readonly float[] mixLeft, mixRight;
        private readonly float[][] pcm;
        private readonly string[] groupCodecs;

        // Current output block in raw positions, and the start of the span Read can serve without relocating.
        private long outRaw, validFrom, rawNext;
        private int outLen;
        private bool ended;
        private long position;
        private bool disposed;

        // Packets read ahead while their positions are fixed from a page granule.
        private sealed class Queued
        {
            internal byte[] Data = new byte[4096];
            internal int Length, Flag;
            internal long End;
        }
        private Queued[] queue = new Queued[0];
        private int queueCount, queueIndex;
        // A packet that follows lost pages, kept for DecodeNext after a read-ahead stopped at it.
        private readonly Queued held = new Queued();
        private bool heldValid;
        private long heldGranule, heldPageOffset;

        internal readonly VorbisStatistics Statistics = new VorbisStatistics();

        public int Channels { get { return outputChannels; } }
        public int SampleRate { get { return sampleRate; } }
        public long Frames { get { return frames; } }
        public long Position { get { return position; } }
        internal int SourceChannels { get { return codec.Channels; } }
        internal long StartGranule { get { return startGranule; } }
        internal uint StreamSerial { get { return serial; } }
        internal long LinkEnd { get { return linkEnd; } }
        internal int BlockSize1 { get { return codec.BlockSize1; } }
        // Codec names of the beginning-of-stream group, in file order.
        internal string[] GroupCodecs { get { return groupCodecs; } }
        internal VorbisCodec Codec { get { return codec; } }

        public VorbisDecoder(Stream input, CancellationToken token, bool leaveOpen = false)
        {
            if (input == null) throw new ArgumentNullException("input");
            this.input = input;
            this.leaveOpen = leaveOpen;
            this.token = token;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!input.CanRead || !input.CanSeek) throw new IOException("Ogg input must be a seekable stream");
                input.Position = 0;
                AudioFiles.SkipId3v2(input);
                long start = input.Position;
                var magic = new byte[4];
                if (!AudioFiles.ReadFully(input, magic, 4) || magic[0] != 'O' || magic[1] != 'g' || magic[2] != 'g' || magic[3] != 'S')
                    throw new InvalidDataException("File is not an Ogg stream");
                ogg = new OggReader(input, token);

                // The beginning-of-stream pages come first. Each names its codec in its first packet.
                ogg.SeekTo(start);
                if (!ogg.ReadPage(long.MaxValue) || ogg.Page.Offset != start || !ogg.Page.Beginning)
                    throw new InvalidDataException("Ogg stream start is damaged");
                var serials = new List<uint>();
                var codecs = new List<string>();
                var head = new byte[16];
                long vorbisSerial = -1;
                do
                {
                    token.ThrowIfCancellationRequested();
                    if (serials.Contains(ogg.Page.Serial)) throw new InvalidDataException("Ogg stream start is damaged");
                    int n = ogg.CopyPageBody(head, head.Length);
                    string name = IdentifyCodec(head, n);
                    serials.Add(ogg.Page.Serial);
                    codecs.Add(name);
                    if (name == "Vorbis" && vorbisSerial < 0) vorbisSerial = ogg.Page.Serial;
                } while (ogg.ReadPage(long.MaxValue) && ogg.Page.Beginning);
                groupCodecs = codecs.ToArray();
                if (vorbisSerial < 0) throw new InvalidDataException(UnsupportedMessage(codecs));
                serial = (uint)vorbisSerial;

                // Headers: identification, comment and setup, in that order, with no lost pages.
                ogg.Select(serial, long.MaxValue);
                ogg.SeekTo(start);
                OggPacket p;
                if (!ogg.NextPacket(PacketLimit, out p) || p.Hole) throw new InvalidDataException("Vorbis headers are incomplete");
                codec = new VorbisCodec(p.Data, p.Length, Statistics);
                if (!ogg.NextPacket(7, out p) || p.Hole) throw new InvalidDataException("Vorbis headers are incomplete");
                if (!VorbisCodec.IsHeader(p.Data, p.Length, 3)) throw new InvalidDataException("Vorbis comment header is invalid");
                if (!ogg.NextPacket(PacketLimit, out p) || p.Hole) throw new InvalidDataException("Vorbis headers are incomplete");
                if (p.Length != p.TotalLength) throw new InvalidDataException("Vorbis setup header is too large");
                codec.ParseSetup(p.Data, p.Length);
                if (!p.LastOnPage) throw new InvalidDataException("Vorbis setup header does not end its page");
                firstAudioOffset = p.PageEnd;
                setupSequence = p.PageSequence;
                sampleRate = (int)codec.SampleRate;
                outputChannels = codec.Channels == 1 ? 1 : 2;
                if (codec.Channels > 2) ChannelDownmix.Build(ChannelDownmix.Vorbis(codec.Channels), out mixLeft, out mixRight);
                pcm = new float[codec.Channels][];
                for (int c = 0; c < codec.Channels; c++) pcm[c] = new float[codec.BlockSize1 / 2];

                // A chained file ends the first link where a page of another group appears.
                linkEnd = ogg.FindLinkEnd(start, serials.ToArray());
                ogg.Select(serial, linkEnd);

                // Samples produced by the packets up to the first granule page give the start offset.
                ogg.SeekTo(firstAudioOffset, setupSequence);
                long produced = 0;
                int previous = -1;
                long firstGranulePage = -1;
                bool lost = false;
                while (ogg.NextPacket(PacketLimit, out p))
                {
                    lost |= p.Hole;
                    int flag = p.Length == p.TotalLength ? codec.PeekBlockFlag(p.Data, p.Length) : -1;
                    if (flag >= 0)
                    {
                        int size = codec.BlockSize(flag);
                        if (previous >= 0) produced += previous / 4 + size / 4;
                        previous = size;
                    }
                    if (p.Granule >= 0)
                    {
                        firstGranule = p.Granule;
                        firstGranulePage = p.PageOffset;
                        break;
                    }
                }
                OggPage last;
                if (firstGranulePage < 0 || !ogg.FindLastPage(linkEnd, firstAudioOffset, serial, true, out last))
                    throw new InvalidDataException("Ogg Vorbis stream has no audio");
                finalGranule = last.Granule;
                finalPageOffset = last.Offset;
                // A final granule below the granule page before it, or beyond what the bytes between can carry, is
                // damage. Every packet costs at least one lacing byte and adds at most half a long block.
                OggPage before;
                if (last.Offset != firstGranulePage && ogg.FindLastPage(last.Offset, firstAudioOffset, serial, true, out before))
                {
                    long most = before.Granule + (last.Offset + last.Size - before.Offset - before.Size) * (codec.BlockSize1 / 2);
                    if (finalGranule < before.Granule)
                    {
                        finalGranule = before.Granule;
                        finalPageOffset = before.Offset;
                    }
                    else if (finalGranule > most) finalGranule = most;
                }
                // Lost pages before the first granule hide how many samples came first. Streams then start at
                // zero, as libvorbis writes them, and the decode re-anchors after the gap.
                if (lost) startGranule = 0;
                else if (firstGranulePage == finalPageOffset) startGranule = Math.Max(0, firstGranule - produced);
                else startGranule = firstGranule - produced;
                baseGranule = Math.Max(0, startGranule);
                frames = finalGranule - baseGranule;
                if (frames <= 0) throw new InvalidDataException("Ogg Vorbis stream has no audio");
                RestartFromBeginning();
            }
            catch
            {
                if (!leaveOpen) input.Dispose();
                throw;
            }
        }

        public void Seek(long frame)
        {
            if (disposed) throw new ObjectDisposedException("VorbisDecoder");
            if (frame < 0) throw new ArgumentOutOfRangeException("frame");
            if (frame > frames) frame = frames;
            position = frame;
            if (frame == frames) return;
            long want = baseGranule + frame;
            if (want >= validFrom && want < outRaw + outLen) return;
            // Short forward jumps decode through the intervening packets.
            if (!ended && want >= rawNext && want - rawNext < 4L * codec.BlockSize1) return;
            Locate(want);
        }

        public int Read(float[] output, int offset, int count)
        {
            if (disposed) throw new ObjectDisposedException("VorbisDecoder");
            if (output == null) throw new ArgumentNullException("output");
            if (offset < 0 || count < 0 || count > output.Length - offset) throw new ArgumentOutOfRangeException("count");
            int wanted = count / outputChannels, written = 0;
            while (written < wanted && position < frames)
            {
                long want = baseGranule + position;
                int room = (int)Math.Min(wanted - written, frames - position);
                if (want >= outRaw && want < outRaw + outLen)
                {
                    int index = (int)(want - outRaw);
                    int n = Math.Min(room, outLen - index);
                    Convert(index, n, output, offset + written * outputChannels);
                    written += n;
                    position += n;
                    continue;
                }
                if (want >= validFrom && want < outRaw)
                {
                    // Lost pages left a gap before the current block.
                    int n = (int)Math.Min(room, outRaw - want);
                    Array.Clear(output, offset + written * outputChannels, n * outputChannels);
                    written += n;
                    position += n;
                    continue;
                }
                if (want < validFrom)
                {
                    Locate(want);
                    continue;
                }
                if (ended || !DecodeNext())
                {
                    // Missing packets at the end are silence, so the stream still has exactly Frames frames.
                    Array.Clear(output, offset + written * outputChannels, room * outputChannels);
                    written += room;
                    position += room;
                }
            }
            return written * outputChannels;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!leaveOpen) input.Dispose();
        }

        private void RestartFromBeginning()
        {
            ogg.SeekTo(firstAudioOffset, setupSequence);
            codec.ResetOverlap();
            queueCount = queueIndex = 0;
            heldValid = false;
            ended = false;
            rawNext = startGranule;
            outRaw = validFrom = rawNext;
            outLen = 0;
        }

        // Decodes packets until one produces output. False at the end of the logical stream.
        private bool DecodeNext()
        {
            long expected = rawNext;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                byte[] data;
                int length;
                long granule = -1, pageOffset = -1;
                if (queueIndex < queueCount)
                {
                    var q = queue[queueIndex++];
                    data = q.Data;
                    length = q.Length;
                }
                else
                {
                    bool hole;
                    if (heldValid)
                    {
                        heldValid = false;
                        data = held.Data;
                        length = held.Length;
                        granule = heldGranule;
                        pageOffset = heldPageOffset;
                        hole = true;
                    }
                    else
                    {
                        OggPacket p;
                        if (!ogg.NextPacket(PacketLimit, out p))
                        {
                            ended = true;
                            return false;
                        }
                        data = p.Data;
                        length = p.Length == p.TotalLength ? p.Length : 0;
                        granule = p.Granule;
                        pageOffset = p.PageOffset;
                        hole = p.Hole;
                    }
                    if (hole)
                    {
                        Recover(data, length, granule, pageOffset);
                        continue;
                    }
                }
                int produced = codec.Synthesize(data, length, pcm);
                bool anchor = granule >= 0 && pageOffset != finalPageOffset;
                if (produced <= 0)
                {
                    // A skipped packet's granule marks the end of the packet before it.
                    if (anchor) rawNext = granule;
                    continue;
                }
                outRaw = rawNext;
                outLen = produced;
                rawNext += produced;
                if (anchor && rawNext != granule)
                {
                    // A page granule that disagrees with the decoded length wins, as in libvorbis.
                    outRaw += granule - rawNext;
                    rawNext = granule;
                }
                validFrom = Math.Min(expected, outRaw);
                return true;
            }
        }

        // Pages were lost before this packet. Collect packets until a granule fixes their positions,
        // prime with the first, and let Read fill the gap with silence.
        private void Recover(byte[] data, int length, long granule, long pageOffset)
        {
            Statistics.Holes++;
            codec.ResetOverlap();
            queueCount = queueIndex = 0;
            int flag = codec.PeekBlockFlag(data, length);
            if (flag >= 0) Enqueue(data, length, flag);
            if (granule >= 0 && queueCount > 0) FixPositions(granule);
            else if (CollectAnchor(long.MinValue, true) != 0)
            {
                queueCount = queueIndex = 0;
                ended = true;
                return;
            }
            StartFromQueue(long.MinValue);
        }

        // Reads packets into the queue until a page granule fixes their positions, then reads on until the
        // queue covers target. 0 when anchored, 1 when only the final page could anchor and that is not
        // allowed, 2 at the end of the stream.
        private int CollectAnchor(long target, bool allowFinal)
        {
            OggPacket p;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!ogg.NextPacket(PacketLimit, out p)) return 2;
                if (p.Hole) queueCount = 0;
                // Pages that withhold granules cannot grow the queue without bound. The oldest packets go,
                // and the span they covered plays as silence once a granule fixes the rest.
                if (queueCount == MaxQueue) DropOldest(MaxQueue / 2);
                int flag = p.Length == p.TotalLength ? codec.PeekBlockFlag(p.Data, p.Length) : -1;
                if (flag >= 0) Enqueue(p.Data, p.Length, flag);
                if (p.Granule < 0 || queueCount == 0) continue;
                if (!allowFinal && p.PageOffset == finalPageOffset) return 1;
                FixPositions(p.Granule);
                break;
            }
            while (queue[queueCount - 1].End <= target && queueCount < MaxQueue)
            {
                token.ThrowIfCancellationRequested();
                if (!ogg.NextPacket(PacketLimit, out p)) break;
                if (p.Hole)
                {
                    Hold(p);
                    break;
                }
                int flag = p.Length == p.TotalLength ? codec.PeekBlockFlag(p.Data, p.Length) : -1;
                if (flag < 0) continue;
                long previousEnd = queue[queueCount - 1].End;
                int previousSize = codec.BlockSize(queue[queueCount - 1].Flag);
                Enqueue(p.Data, p.Length, flag);
                queue[queueCount - 1].End = previousEnd + previousSize / 4 + codec.BlockSize(flag) / 4;
            }
            return 0;
        }

        // The last queued packet ends at granule. Earlier ends follow from the block sizes.
        private void FixPositions(long granule)
        {
            queue[queueCount - 1].End = granule;
            for (int i = queueCount - 1; i > 0; i--)
                queue[i - 1].End = queue[i].End - codec.BlockSize(queue[i - 1].Flag) / 4 - codec.BlockSize(queue[i].Flag) / 4;
        }

        // Keeps the dropped entries past the end of the queue for reuse.
        private void DropOldest(int count)
        {
            var dropped = new Queued[count];
            Array.Copy(queue, dropped, count);
            Array.Copy(queue, count, queue, 0, queueCount - count);
            Array.Copy(dropped, 0, queue, queueCount - count, count);
            queueCount -= count;
        }

        private void Enqueue(byte[] data, int length, int flag)
        {
            if (queueCount == queue.Length)
            {
                Array.Resize(ref queue, Math.Max(16, queue.Length * 2));
                for (int i = queueCount; i < queue.Length; i++) queue[i] = new Queued();
            }
            var q = queue[queueCount++];
            if (q.Data.Length < length) q.Data = new byte[Math.Max(length, q.Data.Length * 2)];
            Buffer.BlockCopy(data, 0, q.Data, 0, length);
            q.Length = length;
            q.Flag = flag;
        }

        private void Hold(OggPacket p)
        {
            int length = p.Length == p.TotalLength ? p.Length : 0;
            if (held.Data.Length < length) held.Data = new byte[Math.Max(length, held.Data.Length * 2)];
            Buffer.BlockCopy(p.Data, 0, held.Data, 0, length);
            held.Length = length;
            heldGranule = p.Granule;
            heldPageOffset = p.PageOffset;
            heldValid = true;
        }

        // Primes with the queued packet just before the one whose output holds target. Earlier packets are
        // dropped undecoded, since Vorbis output depends only on the previous packet.
        private void StartFromQueue(long target)
        {
            int j = 0;
            while (j + 1 < queueCount && queue[j + 1].End <= target) j++;
            codec.ResetOverlap();
            codec.Synthesize(queue[j].Data, queue[j].Length, pcm);
            rawNext = queue[j].End;
            queueIndex = j + 1;
            ended = false;
            outRaw = validFrom = rawNext;
            outLen = 0;
        }

        // Positions the decoder so the next output block holds raw position want.
        private void Locate(long want)
        {
            queueCount = queueIndex = 0;
            heldValid = false;
            ended = false;
            outLen = 0;
            OggPage page;
            if (want <= firstGranule || !ogg.FindGranuleBefore(serial, want, firstAudioOffset, linkEnd, out page))
            {
                RestartFromBeginning();
                return;
            }
            long offset = page.Offset;
            for (int attempt = 0; attempt < MaxStepBack; attempt++)
            {
                ogg.SeekTo(offset);
                int status = CollectAnchor(want, false);
                if (status == 0 && queue[0].End <= want)
                {
                    StartFromQueue(want);
                    return;
                }
                queueCount = 0;
                heldValid = false;
                // The anchor page's packets started too late. Step back one page and try again.
                if (offset <= firstAudioOffset || !ogg.FindLastPage(offset, firstAudioOffset, serial, false, out page)) break;
                offset = page.Offset;
            }
            RestartFromBeginning();
        }

        private void Convert(int index, int count, float[] output, int offset)
        {
            if (outputChannels == 1)
            {
                float[] a = pcm[0];
                Array.Copy(a, index, output, offset, count);
            }
            else if (mixLeft == null)
            {
                float[] a = pcm[0], b = pcm[1];
                for (int i = 0, o = offset; i < count; i++, o += 2)
                {
                    output[o] = a[index + i];
                    output[o + 1] = b[index + i];
                }
            }
            else
            {
                int source = pcm.Length;
                for (int i = 0, o = offset; i < count; i++, o += 2)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < source; c++)
                    {
                        float v = pcm[c][index + i];
                        l += mixLeft[c] * v;
                        r += mixRight[c] * v;
                    }
                    output[o] = l;
                    output[o + 1] = r;
                }
            }
        }

        internal static string IdentifyCodec(byte[] b, int n)
        {
            if (Starts(b, n, "\u0001vorbis")) return "Vorbis";
            if (Starts(b, n, "OpusHead")) return "Opus";
            if (Starts(b, n, "\u007FFLAC") || Starts(b, n, "fLaC")) return "FLAC";
            if (Starts(b, n, "Speex   ")) return "Speex";
            if (Starts(b, n, "\u0080theora")) return "Theora";
            if (Starts(b, n, "fishead\0")) return "Skeleton";
            if (Starts(b, n, "\u0080kate")) return "Kate";
            if (Starts(b, n, "CELT    ")) return "CELT";
            if (Starts(b, n, "PCM     ")) return "PCM";
            if (Starts(b, n, "BBCD")) return "Dirac";
            if (Starts(b, n, "OVP80")) return "VP8";
            return "unknown";
        }

        private static bool Starts(byte[] b, int n, string text)
        {
            if (n < text.Length) return false;
            for (int i = 0; i < text.Length; i++) if (b[i] != text[i]) return false;
            return true;
        }

        private static string UnsupportedMessage(List<string> codecs)
        {
            foreach (string name in codecs)
            {
                switch (name)
                {
                    case "Opus": case "FLAC": case "Speex": case "CELT": case "PCM": return "Ogg " + name + " audio is not supported";
                    case "Theora": case "Dirac": case "VP8": return "Ogg " + name + " video without Vorbis audio is not supported";
                }
            }
            return "Ogg stream contains no Vorbis audio";
        }
    }
}
