using System;

namespace RadioEnhancements
{
    internal sealed class CdSyncSourceLease
    {
        internal object Clip = null;
        private readonly float originalPitch, originalDoppler;
        internal CdSyncSourceLease(float pitch, float doppler) { originalPitch = pitch; originalDoppler = doppler; }
        internal bool Owns(object clip, float pitch, float doppler) { return ReferenceEquals(clip, Clip) && pitch == 1f && doppler == 0f; }
        internal float RestorePitch(float current) { return current == 1f ? originalPitch : current; }
        internal float RestoreDoppler(float current) { return current == 0f ? originalDoppler : current; }
    }
}
