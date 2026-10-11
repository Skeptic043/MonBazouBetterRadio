// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.IO;

namespace RadioEnhancements
{
    // Vorbis I codec layer written from the Xiph.Org Vorbis I specification, with stb_vorbis 1.22 (public
    // domain) consulted for structure and codeword assignment. It parses the identification and setup
    // headers and turns audio packets into per-channel PCM: floor 1, residue types 0, 1 and 2, channel
    // coupling, an O(N log N) inverse MDCT through an N/4 point complex FFT, both window sizes with long
    // and short transitions, and overlap-add. It knows nothing about Ogg pages or stream positions.
    // Floor 0, used only by encoders from before 2004, is rejected. Worker threads only.
    internal sealed class VorbisCodec
    {
        // Allocation limits against hostile setup headers, far above any encoder's codebooks. Run coded
        // lengths let a few bytes of header request millions of entries.
        internal const int MaxVqValues = 1 << 22, MaxEntries = 1 << 20;
        internal const long SetupBudget = 1L << 23;

        internal readonly int Channels, BlockSize0, BlockSize1;
        internal readonly long SampleRate;
        internal readonly VorbisStatistics Statistics;

        private VorbisCodebook[] books;
        private VorbisFloor1[] floors;
        private VorbisResidue[] residues;
        private VorbisMapping[] mappings;
        private VorbisMode[] modes;
        private int modeBits;
        private VorbisMdct mdct0, mdct1;
        private float[] slope0, slope1;

        private readonly VorbisBitReader reader = new VorbisBitReader();
        private float[][] spectrum, tail, vectors;
        private float[] time, interleave;
        private int[][] floorY;
        private bool[][] floorStep;
        private bool[] floorUsed, noResidue, doNotDecode;
        private int previousSize;
        private bool havePrevious;

        // Parses the identification header packet.
        internal VorbisCodec(byte[] p, int length, VorbisStatistics statistics)
        {
            Statistics = statistics;
            if (length < 30 || !IsHeader(p, length, 1)) throw Bad("Vorbis identification header is invalid");
            if (p[7] != 0 || p[8] != 0 || p[9] != 0 || p[10] != 0) throw Bad("Vorbis version is not supported");
            Channels = p[11];
            SampleRate = (long)((uint)p[12] | ((uint)p[13] << 8) | ((uint)p[14] << 16) | ((uint)p[15] << 24));
            BlockSize0 = 1 << (p[28] & 15);
            BlockSize1 = 1 << (p[28] >> 4);
            if (Channels == 0) throw Bad("Vorbis channel count is invalid");
            if (SampleRate == 0 || SampleRate > int.MaxValue) throw Bad("Vorbis sample rate is invalid");
            if (BlockSize0 < 64 || BlockSize1 > 8192 || BlockSize0 > BlockSize1) throw Bad("Vorbis block sizes are invalid");
            if ((p[29] & 1) == 0) throw Bad("Vorbis identification header framing bit is missing");
        }

        internal static bool IsHeader(byte[] p, int length, int type)
        {
            return length >= 7 && p[0] == type && p[1] == 'v' && p[2] == 'o' && p[3] == 'r' && p[4] == 'b' && p[5] == 'i' && p[6] == 's';
        }

        internal static InvalidDataException Bad(string message) { return new InvalidDataException(message); }

        internal int BlockSize(int flag) { return flag == 0 ? BlockSize0 : BlockSize1; }

        internal void ParseSetup(byte[] p, int length)
        {
            if (!IsHeader(p, length, 5)) throw Bad("Vorbis setup header is invalid");
            var r = reader;
            r.Reset(p, 7, length - 7);
            int count = (int)r.Read(8) + 1;
            books = new VorbisCodebook[count];
            long budget = SetupBudget;
            for (int i = 0; i < count; i++)
            {
                books[i] = VorbisCodebook.Parse(r, ref budget);
                Statistics.LookupTypes[books[i].LookupType]++;
            }
            count = (int)r.Read(6) + 1;
            for (int i = 0; i < count; i++)
                if (r.Read(16) != 0) throw Bad("Vorbis time domain transform is invalid");
            count = (int)r.Read(6) + 1;
            floors = new VorbisFloor1[count];
            for (int i = 0; i < count; i++)
            {
                uint type = r.Read(16);
                if (type == 0) throw Bad("Vorbis floor 0 is not supported");
                if (type != 1) throw Bad("Vorbis floor type is invalid");
                floors[i] = VorbisFloor1.Parse(r, books);
            }
            count = (int)r.Read(6) + 1;
            residues = new VorbisResidue[count];
            for (int i = 0; i < count; i++)
            {
                uint type = r.Read(16);
                if (type > 2) throw Bad("Vorbis residue type is invalid");
                residues[i] = VorbisResidue.Parse(r, (int)type, books, Channels, BlockSize1 / 2);
            }
            count = (int)r.Read(6) + 1;
            mappings = new VorbisMapping[count];
            for (int i = 0; i < count; i++)
            {
                if (r.Read(16) != 0) throw Bad("Vorbis mapping type is invalid");
                mappings[i] = VorbisMapping.Parse(r, Channels, floors.Length, residues.Length);
                Statistics.MaxCouplingSteps = Math.Max(Statistics.MaxCouplingSteps, mappings[i].Magnitude.Length);
                Statistics.MaxSubmaps = Math.Max(Statistics.MaxSubmaps, mappings[i].Submaps);
            }
            count = (int)r.Read(6) + 1;
            modes = new VorbisMode[count];
            for (int i = 0; i < count; i++)
            {
                var m = new VorbisMode();
                m.BlockFlag = (int)r.Read(1);
                if (r.Read(16) != 0 || r.Read(16) != 0) throw Bad("Vorbis mode window or transform type is invalid");
                m.Mapping = (int)r.Read(8);
                if (m.Mapping >= mappings.Length) throw Bad("Vorbis mode mapping is invalid");
                modes[i] = m;
            }
            if (r.Read(1) != 1 || r.Eop) throw Bad(r.Eop ? "Vorbis setup header is truncated" : "Vorbis setup header framing bit is missing");
            modeBits = ILog(modes.Length - 1);
            Allocate();
        }

        private void Allocate()
        {
            int half = BlockSize1 / 2;
            spectrum = new float[Channels][];
            tail = new float[Channels][];
            floorY = new int[Channels][];
            floorStep = new bool[Channels][];
            for (int c = 0; c < Channels; c++)
            {
                spectrum[c] = new float[half];
                tail[c] = new float[half];
                floorY[c] = new int[VorbisFloor1.MaxValues];
                floorStep[c] = new bool[VorbisFloor1.MaxValues];
            }
            vectors = new float[Channels][];
            floorUsed = new bool[Channels];
            noResidue = new bool[Channels];
            doNotDecode = new bool[Channels];
            time = new float[BlockSize1];
            interleave = new float[half * Channels];
            mdct0 = new VorbisMdct(BlockSize0);
            mdct1 = BlockSize1 == BlockSize0 ? mdct0 : new VorbisMdct(BlockSize1);
            slope0 = Slope(BlockSize0 / 2);
            slope1 = Slope(BlockSize1 / 2);
        }

        // Rising half of the Vorbis power-complementary window over n samples.
        internal static float[] Slope(int n)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
            {
                double s = Math.Sin((i + 0.5) / n * Math.PI / 2);
                w[i] = (float)Math.Sin(Math.PI / 2 * s * s);
            }
            return w;
        }

        internal static int ILog(long x)
        {
            int n = 0;
            while (x > 0) { n++; x >>= 1; }
            return n;
        }

        // Block flag of an audio packet from its first bits without decoding: 0 short, 1 long, -1 when the
        // packet is not a decodable audio packet and must be skipped.
        internal int PeekBlockFlag(byte[] p, int length)
        {
            if (length <= 0 || (p[0] & 1) != 0) return -1;
            ulong bits = 0;
            int n = Math.Min(length, 5);
            for (int i = 0; i < n; i++) bits |= (ulong)p[i] << (8 * i);
            if (1 + modeBits > 8 * length) return -1;
            int mode = (int)((bits >> 1) & ((1UL << modeBits) - 1));
            if (mode >= modes.Length) return -1;
            int flag = modes[mode].BlockFlag;
            if (flag == 1 && 3 + modeBits > 8 * length) return -1;
            return flag;
        }

        // Forgets the previous block, so the next packet only primes the overlap.
        internal void ResetOverlap() { havePrevious = false; }

        // Decodes one audio packet. Writes the finished samples of each source channel to output[c] from
        // index 0 and returns their count, 0 for a priming packet, or -1 when the packet is skipped.
        internal int Synthesize(byte[] p, int length, float[][] output)
        {
            var r = reader;
            if (length <= 0) { Statistics.SkippedPackets++; return -1; }
            r.Reset(p, 0, length);
            if (r.Read(1) != 0) { Statistics.SkippedPackets++; return -1; }
            int modeNumber = (int)r.Read(modeBits);
            if (r.Eop || modeNumber >= modes.Length) { Statistics.SkippedPackets++; return -1; }
            var mode = modes[modeNumber];
            int flag = mode.BlockFlag;
            int n = flag == 0 ? BlockSize0 : BlockSize1, n2 = n / 2;
            bool previousLong = false, nextLong = false;
            if (flag == 1)
            {
                previousLong = r.Read(1) != 0;
                nextLong = r.Read(1) != 0;
                if (r.Eop) { Statistics.SkippedPackets++; return -1; }
                if (!previousLong || !nextLong) Statistics.TransitionBlocks++;
            }
            else Statistics.ShortBlocks++;
            Statistics.Packets++;
            var map = mappings[mode.Mapping];
            int channels = Channels;

            for (int c = 0; c < channels; c++)
            {
                var floor = floors[map.SubmapFloor[map.Mux[c]]];
                bool used = floor.Decode(r, books, floorY[c], floorStep[c]);
                floorUsed[c] = used;
                noResidue[c] = !used;
                if (!used) Statistics.UnusedFloors++;
                Array.Clear(spectrum[c], 0, n2);
            }
            int steps = map.Magnitude.Length;
            for (int s = 0; s < steps; s++)
            {
                int m = map.Magnitude[s], a = map.Angle[s];
                if (!noResidue[m] || !noResidue[a]) noResidue[m] = noResidue[a] = false;
            }
            for (int s = 0; s < map.Submaps; s++)
            {
                int count = 0;
                for (int c = 0; c < channels; c++)
                {
                    if (map.Mux[c] != s) continue;
                    vectors[count] = spectrum[c];
                    doNotDecode[count] = noResidue[c];
                    count++;
                }
                var residue = residues[map.SubmapResidue[s]];
                Statistics.ResidueTypesUsed[residue.Type]++;
                residue.Decode(r, books, vectors, doNotDecode, count, n2, interleave);
            }
            for (int s = steps - 1; s >= 0; s--)
            {
                float[] mag = spectrum[map.Magnitude[s]], ang = spectrum[map.Angle[s]];
                for (int i = 0; i < n2; i++)
                {
                    float m = mag[i], a = ang[i];
                    if (m > 0)
                    {
                        if (a > 0) ang[i] = m - a;
                        else { ang[i] = m; mag[i] = m + a; }
                    }
                    else
                    {
                        if (a > 0) ang[i] = m + a;
                        else { ang[i] = m; mag[i] = m - a; }
                    }
                }
            }

            int produced = havePrevious ? previousSize / 4 + n / 4 : 0;
            var transform = flag == 0 ? mdct0 : mdct1;
            float[] t = time;
            for (int c = 0; c < channels; c++)
            {
                if (floorUsed[c])
                {
                    floors[map.SubmapFloor[map.Mux[c]]].Render(floorY[c], floorStep[c], spectrum[c], n2);
                    transform.Inverse(spectrum[c], t);
                    ApplyWindow(t, n, flag, previousLong, nextLong);
                }
                else Array.Clear(t, 0, n);
                if (havePrevious) Overlap(tail[c], t, output[c], previousSize, n, produced);
                Array.Copy(t, n2, tail[c], 0, n2);
            }
            previousSize = n;
            havePrevious = true;
            return produced;
        }

        private void ApplyWindow(float[] t, int n, int flag, bool previousLong, bool nextLong)
        {
            int n2 = n / 2, quarter0 = BlockSize0 / 4;
            int leftStart, leftEnd, rightStart, rightEnd;
            float[] left, right;
            if (flag == 1 && !previousLong) { leftStart = n / 4 - quarter0; leftEnd = n / 4 + quarter0; left = slope0; }
            else { leftStart = 0; leftEnd = n2; left = flag == 1 ? slope1 : slope0; }
            if (flag == 1 && !nextLong) { rightStart = 3 * n / 4 - quarter0; rightEnd = 3 * n / 4 + quarter0; right = slope0; }
            else { rightStart = n2; rightEnd = n; right = flag == 1 ? slope1 : slope0; }
            for (int i = 0; i < leftStart; i++) t[i] = 0;
            for (int i = leftStart, k = 0; i < leftEnd; i++, k++) t[i] *= left[k];
            for (int i = rightStart, k = rightEnd - rightStart - 1; i < rightEnd; i++, k--) t[i] *= right[k];
            for (int i = rightEnd; i < n; i++) t[i] = 0;
        }

        // Adds the previous block's right half to the current block's left half. The returned span runs from
        // the centre of the previous window to the centre of the current one.
        private static void Overlap(float[] previous, float[] current, float[] output, int previousN, int n, int produced)
        {
            int previousHalf = previousN / 2, n2 = n / 2;
            int shift = n / 4 - previousN / 4;
            if (shift == 0)
            {
                for (int k = 0; k < produced; k++) output[k] = previous[k] + current[k];
                return;
            }
            for (int k = 0; k < produced; k++)
            {
                float s = k < previousHalf ? previous[k] : 0f;
                int i = k + shift;
                if (i >= 0 && i < n2) s += current[i];
                output[k] = s;
            }
        }
    }

    internal sealed class VorbisMode
    {
        internal int BlockFlag, Mapping;
    }

    // Decode counters for diagnostics and tests. Plain increments, not thread safe.
    internal sealed class VorbisStatistics
    {
        internal long Packets, ShortBlocks, TransitionBlocks, SkippedPackets, UnusedFloors, Holes;
        internal int MaxCouplingSteps, MaxSubmaps;
        internal readonly long[] LookupTypes = new long[3];
        internal readonly long[] ResidueTypesUsed = new long[3];
    }

    // LSB-first bit reader over one packet. Reading past the end sets Eop and returns zeros, which is
    // the end-of-packet condition of the specification.
    internal sealed class VorbisBitReader
    {
        private byte[] data;
        private int pos, end;
        private ulong acc;
        private int accBits;
        private long bitsLeft;
        internal bool Eop;

        internal long BitsLeft { get { return bitsLeft; } }

        internal void Reset(byte[] packet, int offset, int count)
        {
            data = packet;
            pos = offset;
            end = offset + Math.Max(0, count);
            acc = 0;
            accBits = 0;
            bitsLeft = 8L * Math.Max(0, count);
            Eop = false;
        }

        private void Refill()
        {
            while (accBits <= 56)
            {
                if (pos < end) acc |= (ulong)data[pos++] << accBits;
                accBits += 8;
            }
        }

        // Reads n bits, 0 to 32.
        internal uint Read(int n)
        {
            if (accBits < n) Refill();
            uint v = (uint)(acc & ((1UL << n) - 1));
            acc >>= n;
            accBits -= n;
            bitsLeft -= n;
            if (bitsLeft < 0)
            {
                Eop = true;
                bitsLeft = 0;
                return 0;
            }
            return v;
        }

        // Decodes one Huffman codeword. Returns -1 and sets Eop at the end of the packet or on an
        // unassigned codeword.
        internal int Decode(VorbisCodebook book)
        {
            if (accBits < 32) Refill();
            int e = book.Fast[(int)(acc & book.FastMask)];
            int len;
            if (e >= 0)
            {
                len = e & 63;
                e >>= 6;
            }
            else
            {
                e = book.DecodeSlow((uint)acc, out len);
                if (e < 0)
                {
                    Eop = true;
                    bitsLeft = 0;
                    return -1;
                }
            }
            acc >>= len;
            accBits -= len;
            bitsLeft -= len;
            if (bitsLeft < 0)
            {
                Eop = true;
                bitsLeft = 0;
                return -1;
            }
            return e;
        }
    }

    internal sealed class VorbisCodebook
    {
        private const int FastLimit = 10;

        internal int Dimensions, Entries, LookupType;
        internal int[] Fast;
        internal ulong FastMask;
        internal float[] Values;
        private uint[] slowCodes;
        private int[] slowEntries;
        private byte[] slowLengths;

        internal static VorbisCodebook Parse(VorbisBitReader r, ref long budget)
        {
            if (r.Read(24) != 0x564342) throw VorbisCodec.Bad("Vorbis codebook sync pattern is invalid");
            var b = new VorbisCodebook();
            b.Dimensions = (int)r.Read(16);
            b.Entries = (int)r.Read(24);
            if (r.Eop) throw VorbisCodec.Bad("Vorbis setup header is truncated");
            if (b.Dimensions == 0 || b.Entries == 0) throw VorbisCodec.Bad("Vorbis codebook is empty");
            int entries = b.Entries;
            budget -= entries;
            if (entries > VorbisCodec.MaxEntries || budget < 0) throw VorbisCodec.Bad("Vorbis codebook is too large");
            // Unordered lengths cost at least one bit per entry, checked against the packet. Run coded lengths
            // are bounded by the entry limit above.
            var lengths = new byte[entries];
            if (r.Read(1) == 0)
            {
                bool sparse = r.Read(1) != 0;
                if ((sparse ? 1L : 5L) * entries > r.BitsLeft) throw VorbisCodec.Bad("Vorbis setup header is truncated");
                for (int i = 0; i < entries; i++)
                {
                    if (sparse && r.Read(1) == 0) continue;
                    lengths[i] = (byte)(r.Read(5) + 1);
                }
            }
            else
            {
                int current = 0, len = (int)r.Read(5) + 1;
                while (current < entries)
                {
                    int number = (int)r.Read(VorbisCodec.ILog(entries - current));
                    if (r.Eop) throw VorbisCodec.Bad("Vorbis setup header is truncated");
                    if (number > entries - current || (number > 0 && len > 32)) throw VorbisCodec.Bad("Vorbis codebook lengths are invalid");
                    for (int i = 0; i < number; i++) lengths[current + i] = (byte)len;
                    current += number;
                    len++;
                }
            }
            b.LookupType = (int)r.Read(4);
            if (b.LookupType > 2) throw VorbisCodec.Bad("Vorbis codebook lookup type is invalid");
            if (b.LookupType != 0)
            {
                float minimum = Unpack(r.Read(32)), delta = Unpack(r.Read(32));
                int valueBits = (int)r.Read(4) + 1;
                bool sequence = r.Read(1) != 0;
                long lookupValues = b.LookupType == 1 ? Lookup1Values(entries, b.Dimensions) : (long)entries * b.Dimensions;
                if (lookupValues * valueBits > r.BitsLeft) throw VorbisCodec.Bad("Vorbis setup header is truncated");
                budget -= (long)entries * b.Dimensions;
                if ((long)entries * b.Dimensions > VorbisCodec.MaxVqValues || budget < 0) throw VorbisCodec.Bad("Vorbis codebook is too large");
                var multiplicands = new int[lookupValues];
                for (int i = 0; i < lookupValues; i++) multiplicands[i] = (int)r.Read(valueBits);
                b.Values = BuildValues(b.LookupType, entries, b.Dimensions, multiplicands, (int)lookupValues, minimum, delta, sequence);
            }
            if (r.Eop) throw VorbisCodec.Bad("Vorbis setup header is truncated");
            b.BuildDecoder(lengths);
            return b;
        }

        // float32_unpack from the specification.
        internal static float Unpack(uint x)
        {
            double mantissa = x & 0x1FFFFF;
            if ((x & 0x80000000u) != 0) mantissa = -mantissa;
            int exponent = (int)((x >> 21) & 0x3FF);
            return (float)(mantissa * Math.Pow(2, exponent - 788));
        }

        // Largest r with r to the power dimensions not above entries.
        internal static long Lookup1Values(int entries, int dimensions)
        {
            long r = (long)Math.Floor(Math.Exp(Math.Log(entries) / dimensions));
            if (r < 1) r = 1;
            while (Power(r + 1, dimensions, entries) <= entries) r++;
            while (r > 1 && Power(r, dimensions, entries) > entries) r--;
            return r;
        }

        private static long Power(long b, int e, long cap)
        {
            long v = 1;
            for (int i = 0; i < e; i++)
            {
                v *= b;
                if (v > cap) return cap + 1;
            }
            return v;
        }

        private static float[] BuildValues(int type, int entries, int dims, int[] mult, int lookupValues, float minimum, float delta, bool sequence)
        {
            var values = new float[entries * dims];
            for (int e = 0; e < entries; e++)
            {
                float last = 0;
                long divisor = 1;
                for (int k = 0; k < dims; k++)
                {
                    int index = type == 1 ? (int)((e / divisor) % lookupValues) : e * dims + k;
                    float v = (float)(mult[index] * delta);
                    v = (float)(v + minimum);
                    v = (float)(v + last);
                    if (sequence) last = v;
                    values[e * dims + k] = v;
                    if (type == 1 && divisor <= entries) divisor *= lookupValues;
                }
            }
            return values;
        }

        // Assigns codewords in entry order, each taking the lowest free leaf of its length.
        internal static bool AssignCodewords(byte[] lengths, uint[] codes)
        {
            var available = new uint[33];
            int n = lengths.Length, k = 0;
            while (k < n && lengths[k] == 0) k++;
            if (k == n) return true;
            codes[k] = 0;
            for (int i = 1; i <= lengths[k]; i++) available[i] = 1u << (32 - i);
            for (int i = k + 1; i < n; i++)
            {
                int z = lengths[i];
                if (z == 0) continue;
                while (z > 0 && available[z] == 0) z--;
                if (z == 0) return false;
                uint res = available[z];
                available[z] = 0;
                codes[i] = res;
                for (int y = lengths[i]; y > z; y--) available[y] = res + (1u << (32 - y));
            }
            return true;
        }

        internal static uint Reverse(uint v)
        {
            v = ((v >> 1) & 0x55555555u) | ((v & 0x55555555u) << 1);
            v = ((v >> 2) & 0x33333333u) | ((v & 0x33333333u) << 2);
            v = ((v >> 4) & 0x0F0F0F0Fu) | ((v & 0x0F0F0F0Fu) << 4);
            v = ((v >> 8) & 0x00FF00FFu) | ((v & 0x00FF00FFu) << 8);
            return (v >> 16) | (v << 16);
        }

        private void BuildDecoder(byte[] lengths)
        {
            int used = 0, maxLength = 0, single = -1;
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] == 0) continue;
                used++;
                single = i;
                if (lengths[i] > maxLength) maxLength = lengths[i];
            }
            int fastBits = Math.Min(maxLength, FastLimit);
            Fast = new int[1 << fastBits];
            FastMask = (1UL << fastBits) - 1;
            for (int i = 0; i < Fast.Length; i++) Fast[i] = -1;
            if (used == 1)
            {
                // A lone codeword decodes whatever its bits hold, as libvorbis does.
                for (int i = 0; i < Fast.Length; i++) Fast[i] = (single << 6) | lengths[single];
                slowCodes = new uint[0];
                return;
            }
            var codes = new uint[lengths.Length];
            if (!AssignCodewords(lengths, codes)) throw VorbisCodec.Bad("Vorbis codebook is overspecified");
            int slow = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                int len = lengths[i];
                if (len == 0) continue;
                if (len > fastBits)
                {
                    slow++;
                    continue;
                }
                int code = (int)Reverse(codes[i]);
                for (int fill = code; fill < Fast.Length; fill += 1 << len) Fast[fill] = (i << 6) | len;
            }
            slowCodes = new uint[slow];
            slowEntries = new int[slow];
            slowLengths = new byte[slow];
            slow = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] <= fastBits) continue;
                slowCodes[slow] = codes[i];
                slowEntries[slow] = i;
                slowLengths[slow] = lengths[i];
                slow++;
            }
            Array.Sort(slowCodes, slowEntries);
            // Lengths follow their codes. Re-derive them from the entries after sorting.
            for (int i = 0; i < slow; i++) slowLengths[i] = lengths[slowEntries[i]];
        }

        // Long codewords by binary search over their MSB-first forms.
        internal int DecodeSlow(uint bits, out int len)
        {
            len = 0;
            if (slowCodes.Length == 0) return -1;
            uint code = Reverse(bits);
            int lo = 0, hi = slowCodes.Length;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (slowCodes[mid] <= code) lo = mid;
                else hi = mid;
            }
            if (slowCodes[lo] > code) return -1;
            int l = slowLengths[lo];
            uint mask = l >= 32 ? 0xFFFFFFFFu : ~(0xFFFFFFFFu >> l);
            if (((code ^ slowCodes[lo]) & mask) != 0) return -1;
            len = l;
            return slowEntries[lo];
        }
    }

    internal sealed class VorbisFloor1
    {
        internal const int MaxValues = 65;
        private static readonly int[] Ranges = { 256, 128, 86, 64 };
        // floor1_inverse_dB_table. The specification's values form an exact geometric series from
        // 1.0649863e-07 to 1.0, which this reproduces to float precision.
        internal static readonly float[] InverseDb = BuildInverseDb();

        private int partitions, multiplier, values, rangeBits, range;
        private int[] partitionClass, classDimensions, classSubclasses, classMaster;
        private int[][] subclassBooks;
        private int[] x, sorted, low, high;

        private static float[] BuildInverseDb()
        {
            var table = new float[256];
            double first = Math.Log(1.0649863e-07);
            for (int i = 0; i < 256; i++) table[i] = (float)Math.Exp(first * (255 - i) / 255.0);
            return table;
        }

        internal static VorbisFloor1 Parse(VorbisBitReader r, VorbisCodebook[] books)
        {
            var f = new VorbisFloor1();
            f.partitions = (int)r.Read(5);
            f.partitionClass = new int[f.partitions];
            int maxClass = -1;
            for (int i = 0; i < f.partitions; i++)
            {
                f.partitionClass[i] = (int)r.Read(4);
                maxClass = Math.Max(maxClass, f.partitionClass[i]);
            }
            int classes = maxClass + 1;
            f.classDimensions = new int[classes];
            f.classSubclasses = new int[classes];
            f.classMaster = new int[classes];
            f.subclassBooks = new int[classes][];
            for (int c = 0; c < classes; c++)
            {
                f.classDimensions[c] = (int)r.Read(3) + 1;
                f.classSubclasses[c] = (int)r.Read(2);
                if (f.classSubclasses[c] != 0)
                {
                    f.classMaster[c] = (int)r.Read(8);
                    if (f.classMaster[c] >= books.Length) throw VorbisCodec.Bad("Vorbis floor codebook is invalid");
                }
                int count = 1 << f.classSubclasses[c];
                f.subclassBooks[c] = new int[count];
                for (int j = 0; j < count; j++)
                {
                    int book = (int)r.Read(8) - 1;
                    if (book >= books.Length) throw VorbisCodec.Bad("Vorbis floor codebook is invalid");
                    f.subclassBooks[c][j] = book;
                }
            }
            f.multiplier = (int)r.Read(2) + 1;
            f.range = Ranges[f.multiplier - 1];
            f.rangeBits = VorbisCodec.ILog(f.range - 1);
            int bits = (int)r.Read(4);
            var list = new int[MaxValues];
            list[0] = 0;
            list[1] = 1 << bits;
            int values = 2;
            for (int i = 0; i < f.partitions; i++)
            {
                int dims = f.classDimensions[f.partitionClass[i]];
                if (values + dims > MaxValues) throw VorbisCodec.Bad("Vorbis floor 1 has too many points");
                for (int j = 0; j < dims; j++) list[values++] = (int)r.Read(bits);
            }
            if (r.Eop) throw VorbisCodec.Bad("Vorbis setup header is truncated");
            f.values = values;
            f.x = new int[values];
            Array.Copy(list, f.x, values);
            f.sorted = new int[values];
            for (int i = 0; i < values; i++) f.sorted[i] = i;
            Array.Sort((int[])f.x.Clone(), f.sorted);
            for (int i = 1; i < values; i++)
                if (f.x[f.sorted[i]] == f.x[f.sorted[i - 1]]) throw VorbisCodec.Bad("Vorbis floor 1 has duplicate points");
            f.low = new int[values];
            f.high = new int[values];
            for (int i = 2; i < values; i++)
            {
                int lo = 0, hi = 1, lx = -1, hx = int.MaxValue;
                for (int j = 0; j < i; j++)
                {
                    int v = f.x[j];
                    if (v < f.x[i] && v > lx) { lx = v; lo = j; }
                    if (v > f.x[i] && v < hx) { hx = v; hi = j; }
                }
                f.low[i] = lo;
                f.high[i] = hi;
            }
            return f;
        }

        // Reads the floor and applies amplitude synthesis. False when the channel is unused this packet.
        internal bool Decode(VorbisBitReader r, VorbisCodebook[] books, int[] y, bool[] step)
        {
            if (r.Read(1) == 0) return false;
            y[0] = (int)r.Read(rangeBits);
            y[1] = (int)r.Read(rangeBits);
            int offset = 2;
            for (int i = 0; i < partitions; i++)
            {
                int cls = partitionClass[i];
                int dims = classDimensions[cls], cbits = classSubclasses[cls], csub = (1 << cbits) - 1, cval = 0;
                if (cbits > 0) cval = r.Decode(books[classMaster[cls]]);
                int[] sub = subclassBooks[cls];
                for (int j = 0; j < dims; j++)
                {
                    int book = sub[cval & csub];
                    cval >>= cbits;
                    y[offset + j] = book >= 0 ? r.Decode(books[book]) : 0;
                }
                offset += dims;
            }
            if (r.Eop) return false;

            // Amplitude value synthesis, in place. The step flags mark points that bound line segments.
            step[0] = step[1] = true;
            for (int i = 2; i < values; i++)
            {
                int lo = low[i], hi = high[i];
                int predicted = RenderPoint(x[lo], y[lo], x[hi], y[hi], x[i]);
                int val = y[i];
                int highroom = range - predicted, lowroom = predicted;
                int room = highroom < lowroom ? highroom * 2 : lowroom * 2;
                if (val != 0)
                {
                    step[lo] = step[hi] = step[i] = true;
                    if (val >= room) y[i] = highroom > lowroom ? val - lowroom + predicted : predicted - val + highroom - 1;
                    else y[i] = (val & 1) != 0 ? predicted - ((val + 1) >> 1) : predicted + (val >> 1);
                }
                else
                {
                    step[i] = false;
                    y[i] = predicted;
                }
            }
            return true;
        }

        private static int RenderPoint(int x0, int y0, int x1, int y1, int px)
        {
            int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy);
            int off = ady * (px - x0) / adx;
            return dy < 0 ? y0 - off : y0 + off;
        }

        // Curve synthesis multiplied straight into the spectrum.
        internal void Render(int[] y, bool[] step, float[] v, int n)
        {
            int lx = 0, ly = y[sorted[0]] * multiplier, hx = 0, hy = ly;
            for (int k = 1; k < values; k++)
            {
                int i = sorted[k];
                if (!step[i]) continue;
                hy = y[i] * multiplier;
                hx = x[i];
                RenderLine(lx, ly, hx, hy, v, n);
                lx = hx;
                ly = hy;
            }
            if (hx < n) RenderLine(hx, hy, n, hy, v, n);
        }

        private static void RenderLine(int x0, int y0, int x1, int y1, float[] v, int n)
        {
            if (x0 >= n) return;
            float[] db = InverseDb;
            int dy = y1 - y0, adx = x1 - x0, ady = Math.Abs(dy);
            int b = dy / adx, sy = dy < 0 ? b - 1 : b + 1, y = y0, err = 0;
            ady -= Math.Abs(b) * adx;
            int end = x1 < n ? x1 : n;
            v[x0] *= db[y < 0 ? 0 : y > 255 ? 255 : y];
            for (int px = x0 + 1; px < end; px++)
            {
                err += ady;
                if (err >= adx)
                {
                    err -= adx;
                    y += sy;
                }
                else y += b;
                v[px] *= db[y < 0 ? 0 : y > 255 ? 255 : y];
            }
        }
    }

    internal sealed class VorbisResidue
    {
        internal int Type;
        private int begin, end, partitionSize, classifications, classBook;
        private int[][] bookTable;
        private int[][] classes;

        internal static VorbisResidue Parse(VorbisBitReader r, int type, VorbisCodebook[] books, int channels, int maxHalf)
        {
            var res = new VorbisResidue();
            res.Type = type;
            res.begin = (int)r.Read(24);
            res.end = (int)r.Read(24);
            res.partitionSize = (int)r.Read(24) + 1;
            res.classifications = (int)r.Read(6) + 1;
            res.classBook = (int)r.Read(8);
            if (res.classBook >= books.Length) throw VorbisCodec.Bad("Vorbis residue classbook is invalid");
            var cascade = new int[res.classifications];
            for (int i = 0; i < res.classifications; i++)
            {
                int lowBits = (int)r.Read(3);
                int highBits = r.Read(1) != 0 ? (int)r.Read(5) : 0;
                cascade[i] = highBits * 8 + lowBits;
            }
            res.bookTable = new int[res.classifications][];
            for (int i = 0; i < res.classifications; i++)
            {
                res.bookTable[i] = new int[8];
                for (int j = 0; j < 8; j++)
                {
                    int book = -1;
                    if ((cascade[i] & (1 << j)) != 0)
                    {
                        book = (int)r.Read(8);
                        if (book >= books.Length) throw VorbisCodec.Bad("Vorbis residue codebook is invalid");
                        if (books[book].Values == null) throw VorbisCodec.Bad("Vorbis residue codebook has no vector lookup");
                    }
                    res.bookTable[i][j] = book;
                }
            }
            if (r.Eop) throw VorbisCodec.Bad("Vorbis setup header is truncated");
            int size = type == 2 ? maxHalf * channels : maxHalf;
            int span = Math.Max(0, Math.Min(res.end, size) - Math.Min(res.begin, size));
            int perWord = books[res.classBook].Dimensions;
            int vectorCount = type == 2 ? 1 : channels;
            res.classes = new int[vectorCount][];
            for (int i = 0; i < vectorCount; i++) res.classes[i] = new int[span / res.partitionSize + perWord];
            return res;
        }

        // Decodes residue vectors for the channels of one submap. Vectors must start zeroed.
        internal void Decode(VorbisBitReader r, VorbisCodebook[] books, float[][] vectors, bool[] doNotDecode, int count, int n2, float[] interleave)
        {
            if (Type != 2)
            {
                DecodeVectors(r, books, vectors, doNotDecode, count, n2, Type);
                return;
            }
            bool any = false;
            for (int j = 0; j < count; j++) if (!doNotDecode[j]) any = true;
            if (!any || count == 0) return;
            int size = n2 * count;
            Array.Clear(interleave, 0, size);
            var single = singleVector;
            single[0] = interleave;
            DecodeVectors(r, books, single, singleDecode, 1, size, 1);
            if (count == 2)
            {
                float[] a = vectors[0], b = vectors[1];
                for (int i = 0, k = 0; i < n2; i++, k += 2)
                {
                    a[i] = interleave[k];
                    b[i] = interleave[k + 1];
                }
            }
            else
            {
                for (int j = 0; j < count; j++)
                {
                    float[] v = vectors[j];
                    for (int i = 0, k = j; i < n2; i++, k += count) v[i] = interleave[k];
                }
            }
        }

        private readonly float[][] singleVector = new float[1][];
        private readonly bool[] singleDecode = new bool[1];

        private void DecodeVectors(VorbisBitReader r, VorbisCodebook[] books, float[][] v, bool[] doNotDecode, int count, int size, int format)
        {
            int from = Math.Min(begin, size), to = Math.Min(end, size);
            int toRead = to - from;
            if (toRead <= 0) return;
            int psize = partitionSize, partitions = toRead / psize;
            var classBookRef = books[classBook];
            int perWord = classBookRef.Dimensions, cls = classifications;
            for (int pass = 0; pass < 8; pass++)
            {
                int p = 0;
                while (p < partitions)
                {
                    if (pass == 0)
                    {
                        for (int j = 0; j < count; j++)
                        {
                            if (doNotDecode[j]) continue;
                            int temp = r.Decode(classBookRef);
                            if (temp < 0) return;
                            int[] c = classes[j];
                            for (int i = perWord - 1; i >= 0; i--)
                            {
                                c[p + i] = temp % cls;
                                temp /= cls;
                            }
                        }
                    }
                    for (int i = 0; i < perWord && p < partitions; i++, p++)
                    {
                        for (int j = 0; j < count; j++)
                        {
                            if (doNotDecode[j]) continue;
                            int book = bookTable[classes[j][p]][pass];
                            if (book < 0) continue;
                            int offset = from + p * psize;
                            bool ok = format == 0 ? Format0(r, books[book], v[j], offset, psize) : Format1(r, books[book], v[j], offset, psize);
                            if (!ok) return;
                        }
                    }
                }
            }
        }

        private static bool Format0(VorbisBitReader r, VorbisCodebook book, float[] v, int offset, int n)
        {
            int dims = book.Dimensions, step = n / dims;
            float[] vq = book.Values;
            for (int i = 0; i < step; i++)
            {
                int e = r.Decode(book);
                if (e < 0) return false;
                int b = e * dims;
                for (int j = 0; j < dims; j++) v[offset + i + j * step] += vq[b + j];
            }
            return true;
        }

        private static bool Format1(VorbisBitReader r, VorbisCodebook book, float[] v, int offset, int n)
        {
            int dims = book.Dimensions;
            float[] vq = book.Values;
            int i = 0;
            if (dims == 2)
            {
                while (i + 1 < n)
                {
                    int e = r.Decode(book);
                    if (e < 0) return false;
                    int b = e * 2;
                    v[offset + i] += vq[b];
                    v[offset + i + 1] += vq[b + 1];
                    i += 2;
                }
            }
            while (i < n)
            {
                int e = r.Decode(book);
                if (e < 0) return false;
                int b = e * dims;
                for (int j = 0; j < dims && i < n; j++, i++) v[offset + i] += vq[b + j];
            }
            return true;
        }
    }

    internal sealed class VorbisMapping
    {
        internal int Submaps;
        internal int[] Magnitude, Angle, Mux, SubmapFloor, SubmapResidue;

        internal static VorbisMapping Parse(VorbisBitReader r, int channels, int floorCount, int residueCount)
        {
            var m = new VorbisMapping();
            m.Submaps = r.Read(1) != 0 ? (int)r.Read(4) + 1 : 1;
            int steps = r.Read(1) != 0 ? (int)r.Read(8) + 1 : 0;
            m.Magnitude = new int[steps];
            m.Angle = new int[steps];
            int bits = VorbisCodec.ILog(channels - 1);
            for (int i = 0; i < steps; i++)
            {
                m.Magnitude[i] = (int)r.Read(bits);
                m.Angle[i] = (int)r.Read(bits);
                if (m.Magnitude[i] == m.Angle[i] || m.Magnitude[i] >= channels || m.Angle[i] >= channels)
                    throw VorbisCodec.Bad("Vorbis channel coupling is invalid");
            }
            if (r.Read(2) != 0) throw VorbisCodec.Bad("Vorbis mapping reserved bits are set");
            m.Mux = new int[channels];
            if (m.Submaps > 1)
            {
                for (int c = 0; c < channels; c++)
                {
                    m.Mux[c] = (int)r.Read(4);
                    if (m.Mux[c] >= m.Submaps) throw VorbisCodec.Bad("Vorbis mapping submap is invalid");
                }
            }
            m.SubmapFloor = new int[m.Submaps];
            m.SubmapResidue = new int[m.Submaps];
            for (int s = 0; s < m.Submaps; s++)
            {
                r.Read(8);
                m.SubmapFloor[s] = (int)r.Read(8);
                m.SubmapResidue[s] = (int)r.Read(8);
                if (m.SubmapFloor[s] >= floorCount || m.SubmapResidue[s] >= residueCount)
                    throw VorbisCodec.Bad("Vorbis mapping submap is invalid");
            }
            if (r.Eop) throw VorbisCodec.Bad("Vorbis setup header is truncated");
            return m;
        }
    }

    // Inverse MDCT of the Vorbis definition, y[n] = sum over k of X[k] cos(2 pi / N (n + 1/2 + N/4)(k + 1/2)),
    // computed as a DCT-IV of N/2 points through an N/4 point radix-2 complex FFT with pre and post twiddles.
    internal sealed class VorbisMdct
    {
        private readonly int half, quarter;
        private readonly float[] twiddleCos, twiddleSin, fftCos, fftSin, re, im, work;
        private readonly int[] bitReverse;

        internal VorbisMdct(int size)
        {
            half = size / 2;
            quarter = size / 4;
            twiddleCos = new float[quarter];
            twiddleSin = new float[quarter];
            for (int q = 0; q < quarter; q++)
            {
                double theta = Math.PI * (q + 0.125) / half;
                twiddleCos[q] = (float)Math.Cos(theta);
                twiddleSin[q] = (float)Math.Sin(theta);
            }
            fftCos = new float[Math.Max(1, quarter / 2)];
            fftSin = new float[fftCos.Length];
            for (int k = 0; k < quarter / 2; k++)
            {
                double theta = 2 * Math.PI * k / quarter;
                fftCos[k] = (float)Math.Cos(theta);
                fftSin[k] = (float)Math.Sin(theta);
            }
            bitReverse = new int[quarter];
            int bits = VorbisCodec.ILog(quarter - 1);
            for (int i = 0; i < quarter; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
                bitReverse[i] = r;
            }
            re = new float[quarter];
            im = new float[quarter];
            work = new float[half];
        }

        // x holds N/2 coefficients, y receives N samples.
        internal void Inverse(float[] x, float[] y)
        {
            int m = half, l = quarter;
            float[] a = re, b = im, tc = twiddleCos, ts = twiddleSin;
            int[] rev = bitReverse;
            for (int q = 0; q < l; q++)
            {
                float xr = x[2 * q], xi = x[m - 1 - 2 * q], c = tc[q], s = ts[q];
                int t = rev[q];
                a[t] = xr * c + xi * s;
                b[t] = xi * c - xr * s;
            }
            float[] fc = fftCos, fs = fftSin;
            for (int size = 2; size <= l; size <<= 1)
            {
                int h = size >> 1, stride = l / size;
                for (int k = 0; k < h; k++)
                {
                    float wr = fc[k * stride], wi = -fs[k * stride];
                    for (int start = k; start < l; start += size)
                    {
                        int j = start + h;
                        float tr = a[j] * wr - b[j] * wi, ti = a[j] * wi + b[j] * wr;
                        a[j] = a[start] - tr;
                        b[j] = b[start] - ti;
                        a[start] += tr;
                        b[start] += ti;
                    }
                }
            }
            // Post twiddle gives u[2p] = Re Z and u[M-1-2p] = -Im Z. The output then follows from the DCT-IV
            // symmetries: y[j] = u[j + M/2] below M/2, -u[3M/2 - 1 - j] below 3M/2 and -u[j - 3M/2] above.
            float[] u = work;
            for (int p = 0; p < l; p++)
            {
                float c = tc[p], s = ts[p];
                u[2 * p] = a[p] * c + b[p] * s;
                u[m - 1 - 2 * p] = a[p] * s - b[p] * c;
            }
            int hq = m / 2, three = 3 * hq;
            for (int j = 0; j < hq; j++) y[j] = u[j + hq];
            for (int j = hq; j < three; j++) y[j] = -u[three - 1 - j];
            for (int j = three; j < 2 * m; j++) y[j] = -u[j - three];
        }
    }
}
