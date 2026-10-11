// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;

namespace RadioEnhancements
{
    // Decodes one local file to interleaved float PCM for the stream worker. Implementations never touch Unity objects.
    internal interface IPcmDecoder : IDisposable
    {
        // Output channels after any downmix. Always 1 or 2.
        int Channels { get; }
        int SampleRate { get; }
        // Total sample frames, known before the first Read.
        long Frames { get; }
        // Frame that the next Read returns first.
        long Position { get; }
        // Exact seek. The next Read starts at frame, including any decoder preroll needed to get there.
        void Seek(long frame);
        // Reads up to count interleaved samples, always whole frames. Returns 0 only at the end.
        // Every decoder follows one short-read contract. Returning 0 before Frames is a clean early
        // end, and the reported remainder plays as silence. Damaged data may throw from Read or Seek
        // instead. The stream engine handles both: a throw before any audio fails the track, and a
        // later one ends the track where the damage starts.
        int Read(float[] buffer, int offset, int count);
    }
}
