// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;

namespace RadioEnhancements
{
    // Speaker positions in WAVE channel mask bit order, so a mask bit index is its speaker.
    internal enum Speaker
    {
        FrontLeft, FrontRight, FrontCentre, LowFrequency, BackLeft, BackRight, FrontLeftOfCentre, FrontRightOfCentre,
        BackCentre, SideLeft, SideRight, TopCentre, TopFrontLeft, TopFrontCentre, TopFrontRight, TopBackLeft,
        TopBackCentre, TopBackRight, Unknown
    }

    // The one stereo fold-down every decoder uses. Formats only differ in how they name their channels.
    // Front left and right, including left and right of centre, enter at full weight on their side. The
    // front centre and every other side or back speaker enter at -3 dB. Back centre and the other centre
    // or unknown positions give half to each side. LFE is dropped. Both output rows are then divided by one
    // common factor, the larger row's absolute weight sum, so neither row can clip and an asymmetric
    // layout keeps its left and right balance. A layout whose louder row sums below one is not raised.
    internal static class ChannelDownmix
    {
        private const double Minus3 = 0.70710678118654752;

        internal static void Build(Speaker[] layout, out float[] left, out float[] right)
        {
            if (layout == null) throw new ArgumentNullException("layout");
            var l = new double[layout.Length];
            var r = new double[layout.Length];
            for (int c = 0; c < layout.Length; c++)
            {
                switch (layout[c])
                {
                    case Speaker.FrontLeft: case Speaker.FrontLeftOfCentre: l[c] = 1; break;
                    case Speaker.FrontRight: case Speaker.FrontRightOfCentre: r[c] = 1; break;
                    case Speaker.FrontCentre: l[c] = r[c] = Minus3; break;
                    case Speaker.LowFrequency: break;
                    case Speaker.BackLeft: case Speaker.SideLeft: case Speaker.TopFrontLeft: case Speaker.TopBackLeft: l[c] = Minus3; break;
                    case Speaker.BackRight: case Speaker.SideRight: case Speaker.TopFrontRight: case Speaker.TopBackRight: r[c] = Minus3; break;
                    default: l[c] = r[c] = 0.5; break;
                }
            }
            // Never below one, so a silent layout divides by one rather than zero.
            double divisor = Math.Max(1, Math.Max(AbsoluteSum(l), AbsoluteSum(r)));
            left = Scale(l, divisor);
            right = Scale(r, divisor);
        }

        private static double AbsoluteSum(double[] weights)
        {
            double sum = 0;
            foreach (double w in weights) sum += Math.Abs(w);
            return sum;
        }

        private static float[] Scale(double[] weights, double divisor)
        {
            var result = new float[weights.Length];
            for (int i = 0; i < weights.Length; i++) result[i] = (float)(weights[i] / divisor);
            return result;
        }

        // Folds interleaved source frames into interleaved stereo with the weights from Build.
        internal static void Mix(float[] source, int sourceChannels, float[] left, float[] right, int frames, float[] output, int outputAt)
        {
            for (int f = 0, at = 0, o = outputAt; f < frames; f++, at += sourceChannels, o += 2)
            {
                float l = 0, r = 0;
                for (int c = 0; c < sourceChannels; c++)
                {
                    float value = source[at + c];
                    l += value * left[c];
                    r += value * right[c];
                }
                output[o] = l;
                output[o + 1] = r;
            }
        }

        // WAVE and Media Foundation. Channels take the mask's set bits in order. Without a mask the common
        // layouts apply, and channels beyond the mask are unknown positions.
        internal static Speaker[] FromWaveMask(int channels, int mask)
        {
            if (mask == 0)
                mask = channels == 3 ? 0x7 : channels == 4 ? 0x33 : channels == 5 ? 0x37 :
                    channels == 6 ? 0x3F : channels == 7 ? 0x70F : channels == 8 ? 0x63F : 0;
            const int KnownBits = (int)Speaker.Unknown;
            var layout = new Speaker[channels];
            int bit = 0;
            for (int c = 0; c < channels; c++)
            {
                while (bit < KnownBits && (mask & (1 << bit)) == 0) bit++;
                layout[c] = bit < KnownBits ? (Speaker)bit : Speaker.Unknown;
                bit++;
            }
            return layout;
        }

        // RFC 9639 channel orders.
        //   3: L R C   4: FL FR BL BR   5: FL FR FC BL BR   6: FL FR FC LFE BL BR
        //   7: FL FR FC LFE BC SL SR    8: FL FR FC LFE BL BR SL SR
        internal static Speaker[] Flac(int channels)
        {
            switch (channels)
            {
                case 3: return new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCentre };
                case 4: return new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight };
                case 5: return new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCentre, Speaker.BackLeft, Speaker.BackRight };
                case 6:
                    return new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCentre, Speaker.LowFrequency, Speaker.BackLeft,
                        Speaker.BackRight };
                case 7:
                    return new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCentre, Speaker.LowFrequency, Speaker.BackCentre,
                        Speaker.SideLeft, Speaker.SideRight };
                default:
                    return new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCentre, Speaker.LowFrequency, Speaker.BackLeft,
                        Speaker.BackRight, Speaker.SideLeft, Speaker.SideRight };
            }
        }

        // Vorbis I channel orders.
        //   3: L C R   4: FL FR RL RR   5: FL C FR RL RR   6: FL C FR RL RR LFE
        //   7: FL C FR SL SR RC LFE    8: FL C FR SL SR RL RR LFE
        // Above eight channels the order is application defined. Even channels count as front left and odd
        // ones as front right.
        internal static Speaker[] Vorbis(int channels)
        {
            switch (channels)
            {
                case 3: return new[] { Speaker.FrontLeft, Speaker.FrontCentre, Speaker.FrontRight };
                case 4: return new[] { Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight };
                case 5: return new[] { Speaker.FrontLeft, Speaker.FrontCentre, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight };
                case 6:
                    return new[] { Speaker.FrontLeft, Speaker.FrontCentre, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight,
                        Speaker.LowFrequency };
                case 7:
                    return new[] { Speaker.FrontLeft, Speaker.FrontCentre, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight,
                        Speaker.BackCentre, Speaker.LowFrequency };
                case 8:
                    return new[] { Speaker.FrontLeft, Speaker.FrontCentre, Speaker.FrontRight, Speaker.SideLeft, Speaker.SideRight,
                        Speaker.BackLeft, Speaker.BackRight, Speaker.LowFrequency };
            }
            var layout = new Speaker[channels];
            for (int c = 0; c < channels; c++) layout[c] = (c & 1) == 0 ? Speaker.FrontLeft : Speaker.FrontRight;
            return layout;
        }
    }
}
