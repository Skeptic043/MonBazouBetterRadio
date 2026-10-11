using System;

namespace UnityEngine
{
    // Verifies reader isolation and callback contents only. This does not simulate Unity's
    // streaming pre-read, scheduling, native source cursors, or audible alignment.
    public sealed class AudioClip
    {
        public delegate void PCMReaderCallback(float[] data);
        public delegate void PCMSetPositionCallback(int position);
        public int samples, channels, frequency;
        public PCMReaderCallback Read;
        public PCMSetPositionCallback SetPosition;
        public static AudioClip Create(string name, int samples, int channels, int frequency, bool stream,
            PCMReaderCallback read, PCMSetPositionCallback setPosition)
        { return new AudioClip { samples = samples, channels = channels, frequency = frequency, Read = read, SetPosition = setPosition }; }
    }
}
