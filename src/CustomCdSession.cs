using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RadioEnhancements
{
    // Native CDPlayer is a count adapter only. Actual identity, decode and audio belong here.
    internal sealed class CustomCdSession : ICustomMusicSink, IDisposable
    {
        private sealed class Claim
        {
            internal object Member;
            internal CustomMusicTrack Track;
            internal AudioClip Clip;
            internal CdSyncSourceLease Lease;
        }
        private readonly object owner;
        private readonly FieldInfo library, speakers;
        private readonly Action<object, int, double> state;
        private readonly Action<object> changed;
        private readonly Action mutate;
        private readonly Func<bool> paused;
        private readonly Action<AudioSource> excludeNativeResume;
        private readonly Dictionary<AudioSource, Claim> sources = new Dictionary<AudioSource, Claim>();
        private readonly HashSet<object> blocked = new HashSet<object>();
        private List<AudioClip> tokens;
        private AudioClip token;
        internal readonly CustomMusicTransport Transport;
        internal CustomMusicCatalog Catalog = CustomMusicCatalog.Empty;
        internal CustomCdSession(object owner, FieldInfo library, FieldInfo speakers,
            Action<object, int, double> state, Action<object> changed, Action mutate,
            Func<bool> paused, Action<AudioSource> excludeNativeResume, Action<string> report)
        {
            this.owner = owner; this.library = library; this.speakers = speakers;
            this.state = state; this.changed = changed; this.mutate = mutate;
            this.paused = paused; this.excludeNativeResume = excludeNativeResume;
            Transport = new CustomMusicTransport(this, Guid.NewGuid().GetHashCode(), report);
        }
        internal void Publish(CustomMusicCatalog catalog)
        {
            if (ReferenceEquals(Catalog, catalog) && tokens != null) return;
            // Update first cancels any old decoder through the transport's catalog generation gate.
            Catalog = catalog;
            mutate();
            if (tokens != null && ReferenceEquals(library.GetValue(owner), tokens)) tokens.Clear();
            if (token == null) token = AudioClip.Create("Better Radio catalog count", 1, 1, 44100, false);
            tokens = new List<AudioClip>(catalog.Entries.Length);
            for (int i = 0; i < catalog.Entries.Length; i++) tokens.Add(token);
            library.SetValue(owner, tokens);
        }
        internal IList<object> Eligible(IList<object> members)
        {
            List<object> result = new List<object>(members);
            foreach (KeyValuePair<AudioSource, Claim> pair in sources)
                if (pair.Key != null && members.Contains(pair.Value.Member)
                    && !pair.Value.Lease.Owns(pair.Key.clip, pair.Key.pitch, pair.Key.dopplerLevel)) blocked.Add(pair.Value.Member);
            blocked.RemoveWhere(delegate(object member) { return !members.Contains(member); });
            List<object> release = new List<object>();
            foreach (Claim claim in sources.Values)
                if (!members.Contains(claim.Member) || blocked.Contains(claim.Member)) release.Add(claim.Member);
            foreach (object member in release) Stop(member);
            result.RemoveAll(delegate(object member) { return blocked.Contains(member); });
            return result;
        }
        internal void Reclaim(object member) { blocked.Remove(member); Transport.Forget(member); }
        public void Stop(object member)
        {
            List<AudioSource> remove = new List<AudioSource>();
            foreach (KeyValuePair<AudioSource, Claim> pair in sources)
            {
                if (member != null && !ReferenceEquals(member, pair.Value.Member)) continue;
                AudioSource source = pair.Key; Claim claim = pair.Value;
                mutate();
                // Detach every source referencing our clip before destroying that clip.
                if (source != null)
                {
                    if (ReferenceEquals(source.clip, claim.Clip)) { source.Stop(); source.clip = null; }
                    source.pitch = claim.Lease.RestorePitch(source.pitch);
                    source.dopplerLevel = claim.Lease.RestoreDoppler(source.dopplerLevel);
                }
                if (claim.Clip != null) UnityEngine.Object.Destroy(claim.Clip);
                remove.Add(source);
            }
            foreach (AudioSource source in remove) sources.Remove(source);
        }
        public void Schedule(object member, CustomMusicTrack track, int index, int sample, double dspStart)
        {
            AudioSource[] outputs = (AudioSource[])speakers.GetValue(member);
            // A source gets its own reader because Unity invokes each PCM callback independently.
            foreach (AudioSource source in outputs)
            {
                if (source == null) continue;
                Claim claim;
                if (sources.TryGetValue(source, out claim) && !ReferenceEquals(claim.Track, track))
                {
                    Stop(member); claim = null;
                }
                if (claim == null)
                {
                    claim = new Claim { Member = member, Track = track,
                        Lease = new CdSyncSourceLease(source.pitch, source.dopplerLevel) };
                    claim.Clip = track.Stream.CreateClip(track.Stream.CreateReader());
                    claim.Lease.Clip = claim.Clip;
                    sources.Add(source, claim);
                }
                mutate();
                source.Stop(); source.clip = claim.Clip; source.pitch = 1f; source.dopplerLevel = 0f;
                source.timeSamples = Math.Max(0, Math.Min(track.Samples - 1, sample));
                if (paused()) excludeNativeResume(source);
            }
            if (!paused()) foreach (AudioSource source in outputs) if (source != null) source.PlayScheduled(dspStart);
            state(member, index, sample / (double)track.Frequency); changed(member);
        }
        public void State(object member, int index, double seconds) { state(member, index, seconds); }
        internal void PauseSources()
        {
            foreach (KeyValuePair<AudioSource, Claim> pair in sources)
                if (pair.Key != null && ReferenceEquals(pair.Key.clip, pair.Value.Clip)) { mutate(); pair.Key.Stop(); }
        }
        internal bool TryTitle(object member, AudioClip[] clips, out object logicalTrack, out string title)
        {
            logicalTrack = null; title = null;
            if (!Transport.Owns(member) || blocked.Contains(member)) return false;
            int index = Transport.Index;
            if (index < 0 || index >= Catalog.Entries.Length) return false;
            AudioSource[] outputs = (AudioSource[])speakers.GetValue(member);
            bool found = false;
            foreach (AudioSource source in outputs)
            {
                if (source == null) continue;
                Claim claim;
                if (!sources.TryGetValue(source, out claim) || !ReferenceEquals(claim.Member, member)
                    || !ReferenceEquals(claim.Track.Entry, Catalog.Entries[index])
                    || !claim.Lease.Owns(source.clip, source.pitch, source.dopplerLevel)) return false;
                bool provided = clips == null;
                if (clips != null) foreach (AudioClip clip in clips) if (ReferenceEquals(clip, claim.Clip)) { provided = true; break; }
                if (!provided) return false;
                found = true;
            }
            if (!found) return false;
            logicalTrack = Catalog.Entries[index]; title = Catalog.Entries[index].Title; return true;
        }
        public void Dispose()
        {
            // Transport calls Stop before retiring its stream workers.
            Transport.Dispose(); Stop(null);
            if (tokens != null && ReferenceEquals(library.GetValue(owner), tokens)) { mutate(); tokens.Clear(); }
            if (token != null) { UnityEngine.Object.Destroy(token); token = null; }
            tokens = null;
        }
    }
}
