// Adapted from skeptic043/Sailwind Radio, revision 5f234d9bd4544721516cc47865b723c6ca2e8d38.
// Copyright (c) 2026 skeptic043. MIT license; see licenses/SailwindRadio.txt.
using System;
using System.IO;
using System.Threading;

namespace RadioEnhancements
{
    // Worker-side limits and file access shared by the decoders and the stream engine. Nothing here
    // touches Unity objects or the playback class.
    internal static class AudioFiles
    {
        // Inherited decoder/PCM bound from Sailwind Radio. Supported rates in Mon Bazou's
        // Unity 2021.2 player still require live playback acceptance.
        internal const int MaximumPlayableSampleRate = 192000;

        internal static void CheckPlayableRate(int rate)
        {
            if (rate < 8000 || rate > MaximumPlayableSampleRate)
                throw new IOException("Sample rate " + rate + " Hz is outside the playable range of 8000 to " +
                    MaximumPlayableSampleRate + " Hz");
        }

        // Skips any ID3v2 tags at the stream position and leaves the stream at the first byte after them.
        internal static void SkipId3v2(Stream stream)
        {
            var tag = new byte[10];
            while (true)
            {
                long start = stream.Position;
                if (!ReadFully(stream, tag, 10) || tag[0] != 'I' || tag[1] != 'D' || tag[2] != '3' || tag[3] == 0xFF || tag[4] == 0xFF ||
                    ((tag[6] | tag[7] | tag[8] | tag[9]) & 0x80) != 0)
                {
                    stream.Position = start;
                    return;
                }
                long size = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
                if ((tag[5] & 0x10) != 0) size += 10;
                if (size > stream.Length - stream.Position) throw new InvalidDataException("ID3v2 tag is truncated");
                stream.Position += size;
            }
        }

        // False when the stream ends before count bytes.
        internal static bool ReadFully(Stream stream, byte[] data, int count)
        {
            int done = 0;
            while (done < count)
            {
                int got = stream.Read(data, done, count - done);
                if (got <= 0) return false;
                done += got;
            }
            return true;
        }

        internal sealed class CancellableFileStream : Stream
        {
            private readonly FileStream input;
            private readonly CancellationToken token;
            internal CancellableFileStream(string path, CancellationToken token)
            {
                this.token = token;
                input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return input.Length; } }
            public override long Position { get { return input.Position; } set { token.ThrowIfCancellationRequested(); input.Position = value; } }
            public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return input.Read(buffer, offset, count); }
            public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return input.Seek(offset, origin); }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        }
    }
}
