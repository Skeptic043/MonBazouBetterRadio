// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.IO;
using System.Threading;

namespace RadioEnhancements
{
    // One Ogg page header. Body bytes stay in the reader buffer only until the next page read.
    internal struct OggPage
    {
        internal long Offset;
        internal int Size, Flags, Segments, BodyLength;
        internal long Granule;
        internal uint Serial, Sequence;

        internal bool Continued { get { return (Flags & OggReader.FlagContinued) != 0; } }
        internal bool Beginning { get { return (Flags & OggReader.FlagBeginning) != 0; } }
        internal bool End { get { return (Flags & OggReader.FlagEnd) != 0; } }
    }

    // One reassembled packet of the selected logical stream. Data is the reader's own buffer and is
    // valid until the next NextPacket call.
    internal struct OggPacket
    {
        internal byte[] Data;
        internal int Length;
        // Full packet length. Larger than Length when the packet exceeded the store limit.
        internal long TotalLength;
        // Page granule when this is the last packet completed on its page, otherwise -1.
        internal long Granule;
        internal bool LastOnPage;
        // Pages of this stream were lost or damaged before this packet.
        internal bool Hole;
        // Offset, end and sequence number of the page on which the packet completed.
        internal long PageOffset, PageEnd;
        internal uint PageSequence;
    }

    // Ogg container reader written from RFC 3533. It knows nothing about the codec inside, so it can
    // serve Vorbis, Opus or FLAC mappings alike. It verifies every page CRC, resynchronises past damaged
    // or foreign bytes, follows one logical stream by serial number, reassembles its packets across pages,
    // reports lost pages through sequence numbers and locates pages by granule position for seeking.
    // Worker threads only. The owner disposes the stream.
    internal sealed class OggReader
    {
        internal const int HeaderBytes = 27;
        internal const int MaxPageBytes = HeaderBytes + 255 + 255 * 255;
        internal const int FlagContinued = 1, FlagBeginning = 2, FlagEnd = 4;
        // Bisection falls back to a linear page walk below this many bytes.
        private const int LinearWindow = 65536;
        private const int ReadChunk = 65536;

        private readonly Stream input;
        private readonly CancellationToken token;
        private readonly long streamEnd;

        // Window over the stream. buffer[0, length) mirrors the stream from bufferOffset.
        private readonly byte[] buffer = new byte[2 * MaxPageBytes];
        private long bufferOffset;
        private int length, pos;

        // Last page returned by ReadPage and the index of its header in buffer.
        internal OggPage Page;
        private int pageStart;

        // Packet assembly for the selected logical stream.
        private uint serial;
        private long limit;
        private byte[] packet = new byte[8192];
        private int packetLength;
        private long packetTotal;
        private bool inPacket, discarding, endSeen, holePending, resetPending;
        private long lastSequence = -1;
        private int segment, segments, lacing, body, lastComplete;
        private long pageGranule, pageOffset, pageEnd;
        private uint pageSequence;

        internal long CrcFailures;

        internal OggReader(Stream input, CancellationToken token)
        {
            this.input = input;
            this.token = token;
            streamEnd = input.Length;
            limit = streamEnd;
        }

        // Pages at or after limit belong to a later chain link and are never read as packets.
        internal void Select(uint streamSerial, long streamLimit)
        {
            serial = streamSerial;
            limit = Math.Min(streamLimit, streamEnd);
            ResetPackets();
        }

        // Seeks to a page start whose predecessor had sequence number previousSequence, so a lost first page
        // is still reported as a hole.
        internal void SeekTo(long offset, uint previousSequence)
        {
            SeekTo(offset);
            lastSequence = previousSequence;
        }

        internal void SeekTo(long offset)
        {
            if (offset < 0) offset = 0;
            if (offset >= bufferOffset && offset <= bufferOffset + length) pos = (int)(offset - bufferOffset);
            else
            {
                bufferOffset = offset;
                length = pos = 0;
            }
            ResetPackets();
        }

        private void ResetPackets()
        {
            segment = segments = 0;
            packetLength = 0;
            packetTotal = 0;
            inPacket = discarding = endSeen = holePending = resetPending = false;
            lastSequence = -1;
        }

        // Copies up to count bytes from the start of the last page body. Valid until the next read.
        internal int CopyPageBody(byte[] target, int count)
        {
            int n = Math.Min(count, Page.BodyLength);
            Buffer.BlockCopy(buffer, pageStart + HeaderBytes + Page.Segments, target, 0, n);
            return n;
        }

        // Reads the next page with a valid CRC that starts before pageLimit. Anything else is skipped
        // byte by byte, which resynchronises after damage. False at the end of the data.
        internal bool ReadPage(long pageLimit)
        {
            while (true)
            {
                if (bufferOffset + pos >= pageLimit) return false;
                if (Ensure(HeaderBytes) < HeaderBytes) return false;
                byte[] b = buffer;
                if (b[pos] != (byte)'O' || b[pos + 1] != (byte)'g' || b[pos + 2] != (byte)'g' || b[pos + 3] != (byte)'S')
                {
                    token.ThrowIfCancellationRequested();
                    int p = pos + 1, end = length - 3;
                    while (p < end && !(b[p] == (byte)'O' && b[p + 1] == (byte)'g' && b[p + 2] == (byte)'g' && b[p + 3] == (byte)'S')) p++;
                    pos = p;
                    continue;
                }
                if (b[pos + 4] != 0)
                {
                    Reject();
                    continue;
                }
                int count = b[pos + 26];
                if (Ensure(HeaderBytes + count) < HeaderBytes + count)
                {
                    Reject();
                    continue;
                }
                int bodyLength = 0;
                for (int i = 0; i < count; i++) bodyLength += b[pos + HeaderBytes + i];
                int size = HeaderBytes + count + bodyLength;
                if (Ensure(size) < size)
                {
                    Reject();
                    continue;
                }
                if (Crc(b, pos, size) != ReadUInt32(b, pos + 22))
                {
                    CrcFailures++;
                    Reject();
                    continue;
                }
                Page.Offset = bufferOffset + pos;
                Page.Size = size;
                Page.Flags = b[pos + 5];
                Page.Granule = (long)((ulong)ReadUInt32(b, pos + 6) | ((ulong)ReadUInt32(b, pos + 10) << 32));
                Page.Serial = ReadUInt32(b, pos + 14);
                Page.Sequence = ReadUInt32(b, pos + 18);
                Page.Segments = count;
                Page.BodyLength = bodyLength;
                pageStart = pos;
                pos += size;
                return true;
            }
        }

        private void Reject() { pos++; }

        private static uint ReadUInt32(byte[] b, int at)
        {
            return (uint)b[at] | ((uint)b[at + 1] << 8) | ((uint)b[at + 2] << 16) | ((uint)b[at + 3] << 24);
        }

        // Makes n bytes available at pos when the stream has them and returns how many are.
        private int Ensure(int n)
        {
            if (length - pos >= n) return n;
            if (pos > 0)
            {
                Buffer.BlockCopy(buffer, pos, buffer, 0, length - pos);
                bufferOffset += pos;
                length -= pos;
                pos = 0;
            }
            while (length < n)
            {
                long at = bufferOffset + length;
                if (at >= streamEnd) break;
                if (input.Position != at) input.Position = at;
                int want = (int)Math.Min(Math.Min(buffer.Length - length, Math.Max(ReadChunk, n - length)), streamEnd - at);
                int got = input.Read(buffer, length, want);
                if (got <= 0) break;
                length += got;
            }
            return Math.Min(n, length - pos);
        }

        // Returns the next packet of the selected stream. Bytes beyond storeLimit are consumed but not kept.
        // False at the end of the logical stream, the chain link limit or the data.
        internal bool NextPacket(int storeLimit, out OggPacket result)
        {
            result = new OggPacket();
            if (resetPending)
            {
                packetLength = 0;
                packetTotal = 0;
                resetPending = false;
            }
            while (true)
            {
                if (segment < segments)
                {
                    int start = body, run = 0;
                    bool complete = false;
                    while (segment < segments)
                    {
                        int value = buffer[lacing + segment];
                        segment++;
                        run += value;
                        if (value < 255)
                        {
                            complete = true;
                            break;
                        }
                    }
                    body += run;
                    if (discarding)
                    {
                        // The start of this packet was never seen, after a seek or a lost page.
                        if (complete) discarding = false;
                        continue;
                    }
                    Append(start, run, storeLimit);
                    if (!complete)
                    {
                        inPacket = true;
                        continue;
                    }
                    inPacket = false;
                    bool last = segment - 1 == lastComplete;
                    result.Data = packet;
                    result.Length = packetLength;
                    result.TotalLength = packetTotal;
                    result.LastOnPage = last;
                    result.Granule = last ? pageGranule : -1;
                    result.Hole = holePending;
                    result.PageOffset = pageOffset;
                    result.PageEnd = pageEnd;
                    result.PageSequence = pageSequence;
                    holePending = false;
                    resetPending = true;
                    return true;
                }
                if (!LoadNextPage())
                {
                    inPacket = discarding = false;
                    packetLength = 0;
                    packetTotal = 0;
                    return false;
                }
            }
        }

        private bool LoadNextPage()
        {
            if (endSeen) return false;
            do
            {
                if (!ReadPage(limit)) return false;
            } while (Page.Serial != serial);
            token.ThrowIfCancellationRequested();
            if (lastSequence >= 0 && Page.Sequence != (uint)(lastSequence + 1))
            {
                holePending = true;
                DropPartial();
                discarding = false;
            }
            lastSequence = Page.Sequence;
            if (Page.Continued)
            {
                if (!inPacket) discarding = true;
            }
            else
            {
                // A packet left open by the previous page can never complete.
                if (inPacket)
                {
                    DropPartial();
                    holePending = true;
                }
                discarding = false;
            }
            if (Page.End) endSeen = true;
            segment = 0;
            segments = Page.Segments;
            lacing = pageStart + HeaderBytes;
            body = lacing + segments;
            lastComplete = -1;
            for (int i = segments - 1; i >= 0; i--)
            {
                if (buffer[lacing + i] < 255)
                {
                    lastComplete = i;
                    break;
                }
            }
            pageGranule = Page.Granule;
            pageOffset = Page.Offset;
            pageEnd = Page.Offset + Page.Size;
            pageSequence = Page.Sequence;
            return true;
        }

        private void DropPartial()
        {
            inPacket = false;
            packetLength = 0;
            packetTotal = 0;
        }

        private void Append(int start, int run, int storeLimit)
        {
            packetTotal += run;
            int keep = (int)Math.Min(run, Math.Max(0L, (long)storeLimit - packetLength));
            if (keep <= 0) return;
            if (packetLength + keep > packet.Length)
            {
                long size = packet.Length;
                while (size < packetLength + keep) size *= 2;
                Array.Resize(ref packet, (int)Math.Min(size, Math.Max(storeLimit, packetLength + keep)));
            }
            Buffer.BlockCopy(buffer, start, packet, packetLength, keep);
            packetLength += keep;
        }

        // First valid page starting in [from, pageLimit) that matches the serial filter (negative for any)
        // and, when needGranule, carries a granule position. Every page search leaves the packet state reset.
        internal bool FindPage(long from, long pageLimit, long serialFilter, bool needGranule, out OggPage found)
        {
            SeekTo(from);
            while (ReadPage(pageLimit))
            {
                if (Matches(serialFilter, needGranule))
                {
                    found = Page;
                    ResetPackets();
                    return true;
                }
            }
            found = new OggPage();
            ResetPackets();
            return false;
        }

        // Last matching page that starts in [floor, end). Searches backward in growing windows.
        internal bool FindLastPage(long end, long floor, long serialFilter, bool needGranule, out OggPage found)
        {
            found = new OggPage();
            end = Math.Min(end, streamEnd);
            long window = LinearWindow;
            bool any = false;
            while (end > floor && !any)
            {
                token.ThrowIfCancellationRequested();
                long from = Math.Max(floor, end - window);
                SeekTo(from);
                while (ReadPage(end))
                {
                    if (Matches(serialFilter, needGranule))
                    {
                        found = Page;
                        any = true;
                    }
                }
                end = from;
                if (window < 4 * MaxPageBytes) window *= 2;
            }
            ResetPackets();
            return any;
        }

        private bool Matches(long serialFilter, bool needGranule)
        {
            return (serialFilter < 0 || Page.Serial == (uint)serialFilter) && (!needGranule || Page.Granule >= 0);
        }

        // Offset of the first page after start whose serial is outside the first link's group, which
        // is where a chained file's next link begins. The stream end when the file is not chained.
        internal long FindLinkEnd(long start, uint[] group)
        {
            OggPage last;
            if (!FindLastPage(streamEnd, start, -1, false, out last) || InGroup(group, last.Serial)) return streamEnd;
            long boundary = last.Offset, lo = start, hi = last.Offset;
            while (hi - lo > LinearWindow)
            {
                token.ThrowIfCancellationRequested();
                long mid = lo + (hi - lo) / 2;
                OggPage p;
                if (!FindPage(mid, hi, -1, false, out p)) hi = mid;
                else if (InGroup(group, p.Serial)) lo = p.Offset + p.Size;
                else boundary = hi = p.Offset;
            }
            SeekTo(lo);
            while (ReadPage(hi))
            {
                if (!InGroup(group, Page.Serial))
                {
                    boundary = Page.Offset;
                    break;
                }
            }
            ResetPackets();
            return boundary;
        }

        private static bool InGroup(uint[] group, uint value)
        {
            foreach (uint s in group) if (s == value) return true;
            return false;
        }

        // Last page of the stream that carries a granule below target and starts in [from, to).
        // Granule positions are assumed to rise monotonically, as RFC 3533 requires.
        internal bool FindGranuleBefore(uint streamSerial, long target, long from, long to, out OggPage found)
        {
            found = new OggPage();
            bool any = false;
            long lo = from, hi = to;
            while (hi - lo > LinearWindow)
            {
                token.ThrowIfCancellationRequested();
                long mid = lo + (hi - lo) / 2;
                OggPage p;
                if (FindPage(mid, hi, streamSerial, true, out p) && p.Granule < target)
                {
                    found = p;
                    any = true;
                    lo = p.Offset + p.Size;
                }
                else hi = mid;
            }
            SeekTo(lo);
            while (ReadPage(hi))
            {
                if (!Matches(streamSerial, true)) continue;
                if (Page.Granule >= target) break;
                found = Page;
                any = true;
            }
            ResetPackets();
            return any;
        }

        // CRC-32 with polynomial 0x04C11DB7, no reflection and a zero initial value.
        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int j = 0; j < 8; j++) r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : r << 1;
                table[i] = r;
            }
            return table;
        }

        // CRC of a page held at offset with its checksum field taken as zero.
        internal static uint Crc(byte[] b, int offset, int size)
        {
            uint crc = 0;
            int end = offset + size;
            for (int i = offset; i < offset + 22; i++) crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b[i]];
            for (int i = 0; i < 4; i++) crc = (crc << 8) ^ CrcTable[crc >> 24];
            for (int i = offset + 26; i < end; i++) crc = (crc << 8) ^ CrcTable[(crc >> 24) ^ b[i]];
            return crc;
        }
    }
}
