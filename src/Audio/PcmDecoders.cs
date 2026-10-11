using System;
using System.IO;
using System.Threading;

namespace RadioEnhancements
{
    internal static class PcmDecoders
    {
        private static bool IsMediaFoundation(string extension)
        {
            return String.Equals(extension, ".aac", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool Supports(string path)
        {
            string extension = Path.GetExtension(path);
            if (IsMediaFoundation(extension)) return MediaFoundationSupport.Enabled;
            return String.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase);
        }
        internal static IPcmDecoder Open(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path);
            IPcmDecoder decoder;
            if (IsMediaFoundation(extension))
            {
                if (!MediaFoundationSupport.Enabled) throw new IOException("AAC, M4A and MP4 playback is disabled. " + MediaFoundationSupport.Reason);
                decoder = MediaFoundationDecoder.Open(path, token);
            }
            else if (String.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)) decoder = Mp3PcmDecoder.Open(path, token);
            else if (String.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)) decoder = WavPcmDecoder.Open(path, token);
            else if (String.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase))
            {
                Stream input = new AudioFiles.CancellableFileStream(path, token);
                try { decoder = new VorbisDecoder(input, token); }
                catch { input.Dispose(); throw; }
            }
            else throw new IOException("Unsupported audio format");
            if (decoder.Channels < 1 || decoder.Channels > 2 || decoder.Frames <= 0)
            {
                decoder.Dispose(); throw new IOException("Decoder reported an invalid audio layout");
            }
            return decoder;
        }
    }
}
