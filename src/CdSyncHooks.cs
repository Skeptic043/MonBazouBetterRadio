using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace RadioEnhancements
{
    internal sealed class CdSyncHooks
    {
        internal readonly MethodInfo Tick, SelectPlayback, PauseGame, ResumeGame, RadioStop;
        private readonly FieldInfo playing, library, speakers, furnitureMode, furnitureSound, presetIndex, presetRadio, pausedSources;
        private readonly PropertyInfo mode, index, time, furniture, power, pausedProperty;
        private static MethodInfo nativeMode;
        private readonly ConditionalWeakTable<object, Session> sessions = new ConditionalWeakTable<object, Session>();
        private readonly List<WeakReference> owners = new List<WeakReference>();
        // Decoder workers need deterministic disposal even if a weak manager key would be collected.
        private readonly List<Session> retainedCustom = new List<Session>();
        private readonly Action<object, object> refreshed;
        private readonly Func<object, bool> pending;
        private readonly Action<Exception> report;
        private readonly Action<string> diagnostics;
        private bool paused;
        private CustomMusicLibrary customLibrary;
        private WeakReference pausedGameplay;
        internal bool IsActive;
        internal Func<bool> PreferredShuffle;
        internal bool Mutated { get; private set; }

        internal CdSyncHooks(Action<object, object> refreshed, Func<object, bool> pending, Action<Exception> report = null, Action<string> diagnostics = null)
        {
            this.refreshed = refreshed; this.pending = pending; this.report = report; this.diagnostics = diagnostics;
            Type manager = GameHooks.RequiredType("SOUND_Manager"), sound = GameHooks.RequiredType("SOUND_Radio");
            Type radio = GameHooks.RequiredType("Furniture_Radio"), button = GameHooks.RequiredType("Furniture_Radio_ButtonPreset");
            Type gameplay = GameHooks.RequiredType("Gameplay");
            Tick = GameHooks.Method(manager, "Tick", typeof(void), typeof(int));
            SelectPlayback = GameHooks.Method(manager, "PlayStationOnThisRadio", typeof(void), sound);
            RadioStop = GameHooks.Method(manager, "RadioStop", typeof(void), sound);
            PauseGame = GameHooks.Method(gameplay, "PauseGame", typeof(void));
            ResumeGame = GameHooks.Method(gameplay, "ResumeGame", typeof(void));
            pausedProperty = GameHooks.Property(gameplay, "Get_isGamePaused", typeof(bool));
            pausedSources = GameHooks.Field(gameplay, "EveryAudioSourcePaused", typeof(List<AudioSource>), false);
            playing = GameHooks.Field(manager, "RadioPlaying", typeof(List<>).MakeGenericType(sound), false);
            library = GameHooks.Field(manager, "CDPlayer", typeof(List<AudioClip>), false);
            speakers = GameHooks.Field(sound, "Speakers", typeof(AudioSource[]), false);
            mode = GameHooks.Property(sound, "GetFMCD", typeof(float)); nativeMode = mode.GetGetMethod();
            index = GameHooks.Property(sound, "GetCurrentCDSong", typeof(int));
            time = GameHooks.Property(sound, "GetCurrentCDTime", typeof(float));
            if (index.GetSetMethod() == null || time.GetSetMethod() == null) throw new MissingMethodException("Shared CD state setters changed.");
            furniture = GameHooks.Property(sound, "Get_Radio", radio);
            power = GameHooks.Property(radio, "Get_IsOn", typeof(bool));
            furnitureMode = GameHooks.Field(radio, "FMCD", typeof(int), false);
            furnitureSound = GameHooks.Field(radio, "SOUND_Radio", sound, false);
            presetIndex = GameHooks.Field(button, "PresetIndex", typeof(int), false);
            presetRadio = GameHooks.Field(button, "Furniture_Radio", radio, false);
        }

        private sealed class Session : ICdSyncSink
        {
            internal readonly WeakReference Owner;
            internal readonly CdSyncTransport Transport;
            internal CustomCdSession Custom;
            internal readonly HashSet<object> Waiting = new HashSet<object>();
            internal readonly List<CdSyncTrack> Tracks = new List<CdSyncTrack>();
            internal readonly HashSet<object> Blocked = new HashSet<object>();
            internal readonly Dictionary<AudioSource, SourceClaim> Sources = new Dictionary<AudioSource, SourceClaim>();
            internal readonly HashSet<object> Notifications = new HashSet<object>();
            private readonly CdSyncHooks hooks;
            internal Session(CdSyncHooks hooks, object owner)
            {
                this.hooks = hooks; Owner = new WeakReference(owner);
                Transport = new CdSyncTransport(this, Guid.NewGuid().GetHashCode());
                Transport.SetShuffle(hooks.PreferredShuffle != null && hooks.PreferredShuffle());
            }
            public void Schedule(object member, CdSyncTrack track, int selected, int sample, double start)
            {
                AudioSource[] sources = (AudioSource[])hooks.speakers.GetValue(member);
                hooks.Mutated = true;
                // All source preparation precedes PlayScheduled; the same absolute DSP instant is used for every speaker.
                foreach (AudioSource source in sources)
                {
                    if (source == null) continue;
                    SourceClaim claim;
                    if (!Sources.TryGetValue(source, out claim))
                    {
                        claim = new SourceClaim { Member = member, Lease = new CdSyncSourceLease(source.pitch, source.dopplerLevel) };
                        Sources.Add(source, claim);
                    }
                    claim.Lease.Clip = track.Clip;
                    source.Stop(); source.clip = (AudioClip)track.Clip;
                    source.pitch = 1f; source.dopplerLevel = 0f;
                    source.timeSamples = sample;
                    if (hooks.paused)
                    {
                        object gameplay = hooks.pausedGameplay == null ? null : hooks.pausedGameplay.Target;
                        if (GameHooks.Alive(gameplay))
                        {
                            IList nativePaused = (IList)hooks.pausedSources.GetValue(gameplay);
                            if (nativePaused != null && nativePaused.Contains(source)) nativePaused.Remove(source);
                        }
                    }
                }
                if (!hooks.paused)
                    foreach (AudioSource source in sources) if (source != null) source.PlayScheduled(start);
                State(member, selected, sample / (double)track.Frequency);
                Waiting.Remove(member);
                Notifications.Add(member);
            }
            public void State(object member, int selected, double seconds)
            {
                hooks.index.SetValue(member, selected, null);
                hooks.time.SetValue(member, (float)seconds, null);
            }
        }
        private sealed class SourceClaim { internal object Member; internal CdSyncSourceLease Lease; }
        private void Notify(Session session)
        {
            object owner = session.Owner.Target;
            foreach (object sound in session.Notifications)
            {
                try { if (GameHooks.Alive(owner) && refreshed != null) refreshed(owner, sound); }
                catch (Exception error) { if (report != null) report(error); }
            }
            session.Notifications.Clear();
        }

        private void Release(Session session, object member)
        {
            List<AudioSource> remove = new List<AudioSource>();
            foreach (KeyValuePair<AudioSource, SourceClaim> pair in session.Sources)
            {
                if (member != null && !ReferenceEquals(pair.Value.Member, member)) continue;
                AudioSource source = pair.Key;
                if (source != null)
                {
                    source.pitch = pair.Value.Lease.RestorePitch(source.pitch);
                    source.dopplerLevel = pair.Value.Lease.RestoreDoppler(source.dopplerLevel);
                }
                remove.Add(source);
            }
            foreach (AudioSource source in remove) session.Sources.Remove(source);
        }

        private Session Get(object owner)
        {
            Session session;
            if (sessions.TryGetValue(owner, out session)) return session;
            session = new Session(this, owner); sessions.Add(owner, session); owners.Add(new WeakReference(owner));
            if (paused) session.Transport.Pause(AudioSettings.dspTime);
            return session;
        }
        internal void EnableCustom(CustomMusicLibrary value) { customLibrary = value; }
        internal void SetShuffle(bool value)
        {
            foreach (WeakReference weak in owners)
            {
                object owner = weak.Target; Session session;
                if (owner == null || !sessions.TryGetValue(owner, out session)) continue;
                session.Transport.SetShuffle(value);
                if (session.Custom != null) session.Custom.Transport.SetShuffle(value);
            }
        }
        internal bool IsCustom(object owner)
        {
            Session session;
            return IsActive && GameHooks.Alive(owner) && sessions.TryGetValue(owner, out session) && session.Custom != null;
        }
        internal bool OwnsCustomCd(object owner, object sound)
        {
            Session session;
            return owner != null && sound != null && sessions.TryGetValue(owner, out session) && session.Custom != null
                && GameHooks.Alive(sound) && (float)mode.GetValue(sound, null) == 1f;
        }
        internal bool CustomTickMode(object sound)
        {
            if (sound == null || !GameHooks.Alive(sound) || (float)mode.GetValue(sound, null) != 1f) return false;
            foreach (WeakReference weak in owners)
            {
                object owner = weak.Target; Session session;
                if (owner == null || !sessions.TryGetValue(owner, out session) || session.Custom == null) continue;
                IList registered = (IList)playing.GetValue(owner);
                if (registered != null && registered.Contains(sound)) return true;
            }
            return false;
        }
        internal bool CustomBusy(object owner)
        {
            Session session;
            return IsCustom(owner) && sessions.TryGetValue(owner, out session)
                && customLibrary.Scanning;
        }
        internal int GetCount(object owner)
        {
            Session session;
            return IsCustom(owner) && sessions.TryGetValue(owner, out session) ? session.Custom.Catalog.Entries.Length : 0;
        }
        internal void BeginCustomScan(object owner, string[] roots)
        {
            Mutated = false;
            if (!IsActive || customLibrary == null || !GameHooks.Alive(owner)) throw new InvalidOperationException("Custom CD loader requires active validated hooks.");
            Session session = Get(owner);
            if (session.Custom == null)
            {
                foreach (object sound in Members(owner)) Stop(sound);
                Release(session, null); session.Transport.Clear(); session.Waiting.Clear(); session.Blocked.Clear();
                session.Custom = new CustomCdSession(owner, library, speakers, session.State,
                    delegate(object sound) { session.Notifications.Add(sound); }, delegate { Mutated = true; },
                    delegate { return paused; }, ExcludeNativeResume,
                    delegate(string message) { if (diagnostics != null) diagnostics(message); });
                retainedCustom.Add(session);
                session.Custom.Transport.SetShuffle(PreferredShuffle != null && PreferredShuffle());
                session.Custom.Publish(customLibrary.Current);
                if (paused) session.Custom.Transport.Pause(AudioSettings.dspTime);
            }
            customLibrary.ScanAsync(roots);
        }
        private void ExcludeNativeResume(AudioSource source)
        {
            object gameplay = pausedGameplay == null ? null : pausedGameplay.Target;
            if (!GameHooks.Alive(gameplay)) return;
            IList remembered = (IList)pausedSources.GetValue(gameplay);
            if (remembered != null && remembered.Contains(source)) remembered.Remove(source);
        }
        internal bool TryResolveTitle(object owner, object sound, AudioClip[] sourceClips, out object logicalClip, out string title)
        {
            logicalClip = null; title = null; Session session;
            return IsCustom(owner) && Cd(sound) && sessions.TryGetValue(owner, out session)
                && session.Custom.TryTitle(sound, sourceClips, out logicalClip, out title);
        }
        private bool Cd(object sound)
        {
            if (!GameHooks.Alive(sound) || (float)mode.GetValue(sound, null) != 1f) return false;
            object radio = furniture.GetValue(sound, null);
            return GameHooks.Alive(radio) && (bool)power.GetValue(radio, null) && (int)furnitureMode.GetValue(radio) == 1;
        }
        private List<object> Members(object owner)
        {
            List<object> result = new List<object>();
            IList radios = (IList)playing.GetValue(owner);
            if (radios != null) foreach (object sound in radios) if (Cd(sound)) result.Add(sound);
            return result;
        }
        private List<CdSyncTrack> Tracks(object owner, Session session)
        {
            IList clips = (IList)library.GetValue(owner);
            bool same = clips != null && clips.Count == session.Tracks.Count;
            if (same) for (int i = 0; i < clips.Count; i++) if (!ReferenceEquals(clips[i], session.Tracks[i].Clip)) { same = false; break; }
            if (same) return session.Tracks;
            session.Tracks.Clear();
            if (clips == null) return session.Tracks;
            foreach (object value in clips)
            {
                AudioClip clip = value as AudioClip;
                // Never collapse indexes when an externally changed list contains an invalid clip.
                if (clip == null || clip.samples <= 0 || clip.frequency <= 0) { session.Tracks.Clear(); return session.Tracks; }
                session.Tracks.Add(new CdSyncTrack(clip, clip.samples, clip.frequency));
            }
            return session.Tracks;
        }
        private static double Lead()
        {
            int samples, buffers; AudioSettings.GetDSPBufferSize(out samples, out buffers);
            int rate = AudioSettings.outputSampleRate;
            return Math.Max(0.1, rate > 0 ? samples * (double)Math.Max(2, buffers) / rate : 0.1);
        }
        private void Stop(object sound)
        {
            if (!Cd(sound)) return;
            AudioSource[] sources = (AudioSource[])speakers.GetValue(sound);
            Mutated = true;
            foreach (AudioSource source in sources) if (source != null) source.Stop();
        }
        private void Update(object owner, Session session)
        {
            List<object> members = Members(owner);
            if (session.Custom != null)
            {
                IList<object> eligible = session.Custom.Eligible(members);
                int first = eligible.Count == 0 ? 0 : (int)index.GetValue(eligible[0], null);
                double seconds = eligible.Count == 0 ? 0 : (float)time.GetValue(eligible[0], null);
                session.Custom.Transport.Update(session.Custom.Catalog, eligible, AudioSettings.dspTime, Lead(), first, seconds);
                Notify(session); return;
            }
            // Relinquish a source changed by another mod instead of continuously fighting it.
            foreach (KeyValuePair<AudioSource, SourceClaim> pair in session.Sources)
                if (pair.Key != null && members.Contains(pair.Value.Member)
                    && !pair.Value.Lease.Owns(pair.Key.clip, pair.Key.pitch, pair.Key.dopplerLevel)) session.Blocked.Add(pair.Value.Member);
            List<object> release = new List<object>();
            foreach (KeyValuePair<AudioSource, SourceClaim> pair in session.Sources)
                if (!members.Contains(pair.Value.Member) || session.Blocked.Contains(pair.Value.Member)) release.Add(pair.Value.Member);
            foreach (object sound in release) Release(session, sound);
            session.Blocked.RemoveWhere(delegate(object sound) { return !members.Contains(sound); });
            members.RemoveAll(delegate(object sound) { return session.Blocked.Contains(sound); });
            session.Waiting.RemoveWhere(delegate(object sound) { return !members.Contains(sound); });
            List<CdSyncTrack> tracks = pending != null && pending(owner) ? new List<CdSyncTrack>() : Tracks(owner, session);
            if (tracks.Count == 0)
            {
                foreach (object sound in members)
                {
                    if (!session.Waiting.Contains(sound)) { Stop(sound); session.Waiting.Add(sound); }
                    session.State(sound, 0, 0);
                }
                session.Transport.Clear();
                return;
            }
            foreach (object sound in members)
                if (!session.Transport.Owns(sound) && !session.Waiting.Contains(sound)) { Stop(sound); session.Waiting.Add(sound); }
            int initialIndex = members.Count == 0 ? 0 : (int)index.GetValue(members[0], null);
            initialIndex = Math.Max(0, Math.Min(tracks.Count - 1, initialIndex));
            float initialTime = members.Count == 0 ? 0f : (float)time.GetValue(members[0], null);
            int sample = (int)Math.Max(0, Math.Min(tracks[initialIndex].Samples - 1, initialTime * (double)tracks[initialIndex].Frequency));
            session.Transport.Update(tracks, members, AudioSettings.dspTime, Lead(), initialIndex, sample);
            Notify(session);
        }
        internal bool Select(object owner, object sound)
        {
            Mutated = false;
            if (!IsActive || !GameHooks.Alive(owner)) return false;
            // Even an inactive CD source must never reach native playback with count-only tokens.
            if (IsCustom(owner) && GameHooks.Alive(sound) && (float)mode.GetValue(sound, null) == 1f
                && (!Cd(sound) || !Members(owner).Contains(sound))) return true;
            if (!Cd(sound)) { Session previous; if (sessions.TryGetValue(owner, out previous)) { if (previous.Custom != null) { previous.Custom.Stop(sound); previous.Custom.Transport.Forget(sound); } Release(previous, sound); Update(owner, previous); } return false; }
            if (!Members(owner).Contains(sound)) return false;
            Session session = Get(owner);
            if (session.Custom != null) { Stop(sound); session.Custom.Reclaim(sound); Update(owner, session); return true; }
            if (session.Blocked.Remove(sound)) { Release(session, sound); session.Transport.Forget(sound); }
            Update(owner, session); return true;
        }
        internal bool Seek(object owner, object sound, bool previous)
        {
            Mutated = false;
            if (IsCustom(owner) && GameHooks.Alive(sound) && (float)mode.GetValue(sound, null) == 1f
                && (!Cd(sound) || !Members(owner).Contains(sound))) return true;
            if (!IsActive || !GameHooks.Alive(owner) || !Cd(sound) || !Members(owner).Contains(sound)) return false;
            Session session = Get(owner);
            if (session.Custom != null)
            {
                session.Custom.Reclaim(sound); Update(owner, session);
                session.Custom.Transport.Seek(session.Custom.Catalog, session.Custom.Eligible(Members(owner)), AudioSettings.dspTime, Lead(), previous);
                Notify(session); return true;
            }
            if (session.Blocked.Remove(sound)) { Release(session, sound); session.Transport.Forget(sound); }
            Update(owner, session);
            List<object> members = Members(owner); members.RemoveAll(delegate(object member) { return session.Blocked.Contains(member); });
            if (pending == null || !pending(owner)) session.Transport.Seek(Tracks(owner, session), members, AudioSettings.dspTime, Lead(), previous);
            Notify(session);
            return true;
        }
        private Session ForSound(object sound)
        {
            foreach (WeakReference weak in owners)
            {
                object owner = weak.Target; Session session;
                if (GameHooks.Alive(owner) && sessions.TryGetValue(owner, out session) && (session.Custom != null || !session.Blocked.Contains(sound)) && Members(owner).Contains(sound)) return session;
            }
            return null;
        }
        internal bool Handles(object sound) { return IsActive && Cd(sound) && ForSound(sound) != null; }
        internal bool IsManaged(object owner, object sound) { Session session; return IsActive && GameHooks.Alive(owner) && Cd(sound) && sessions.TryGetValue(owner, out session) && (session.Custom != null || !session.Blocked.Contains(sound)) && Members(owner).Contains(sound); }
        internal float TickMode(object sound) { return Handles(sound) ? 0f : NativeMode(sound); }
        internal static float NativeMode(object sound) { return (float)nativeMode.Invoke(sound, null); }
        internal bool Toggle(object radio, int preset, out bool enabled)
        {
            Mutated = false; enabled = false;
            if (!IsActive || preset != 6 || !GameHooks.Alive(radio)) return false;
            object sound = furnitureSound.GetValue(radio);
            if (!Cd(sound)) return false;
            Session session = ForSound(sound); if (session == null) return false;
            if (session.Custom != null) { session.Custom.Transport.Toggle(); enabled = session.Custom.Transport.Shuffle; return true; }
            session.Transport.Toggle(); enabled = session.Transport.Shuffle; return true;
        }
        internal bool TryHoverText(object button, out string text)
        {
            text = null;
            if (!IsActive || (int)presetIndex.GetValue(button) != 6) return false;
            object radio = presetRadio.GetValue(button);
            if (!GameHooks.Alive(radio)) return false;
            object sound = furnitureSound.GetValue(radio);
            if (!Cd(sound)) return false;
            bool enabled = PreferredShuffle != null && PreferredShuffle();
            text = " 6 - Shuffle " + (enabled ? "ON" : "OFF");
            return true;
        }
        internal void UpdateAll()
        {
            Mutated = false; if (!IsActive) return;
            CustomMusicCatalog published = null;
            bool newCatalog = customLibrary != null && customLibrary.TryPublish(out published);
            if (customLibrary == null) published = null;
            if (newCatalog && diagnostics != null)
            {
                diagnostics("Custom music index ready: " + published.Entries.Length + " files. Audio is prepared only for the shared song and its next track.");
                foreach (string warning in published.Warnings) diagnostics("Custom music scan: " + warning);
            }
            object gameplay = pausedGameplay == null ? null : pausedGameplay.Target;
            // Exiting a paused save can destroy Gameplay without calling ResumeGame.
            if (paused && (!GameHooks.Alive(gameplay) || !(bool)pausedProperty.GetValue(gameplay, null))) ResumeSessions();
            for (int i = owners.Count - 1; i >= 0; i--)
            {
                object owner = owners[i].Target; Session session;
                if (!GameHooks.Alive(owner) || !sessions.TryGetValue(owner, out session))
                {
                    if (owner != null && sessions.TryGetValue(owner, out session)) { if (session.Custom != null) { session.Custom.Dispose(); retainedCustom.Remove(session); } Release(session, null); sessions.Remove(owner); }
                    owners.RemoveAt(i); continue;
                }
                if (newCatalog && session.Custom != null) session.Custom.Publish(published);
                Update(owner, session);
            }
        }
        internal void ResetLibrary(object owner)
        {
            Mutated = false; if (!IsActive || !GameHooks.Alive(owner)) return;
            Session session = Get(owner);
            if (session.Custom != null) return;
            foreach (object sound in Members(owner)) { Stop(sound); session.State(sound, 0, 0); }
            Release(session, null); session.Waiting.Clear(); session.Transport.Clear(); session.Tracks.Clear(); session.Blocked.Clear();
        }
        internal void BeforePause(object gameplay)
        {
            Mutated = false; if (!IsActive) return;
            pausedGameplay = new WeakReference(gameplay);
            if (paused) return;
            paused = true;
            foreach (WeakReference weak in owners)
            {
                object owner = weak.Target; Session session;
                if (!GameHooks.Alive(owner) || !sessions.TryGetValue(owner, out session)) continue;
                Update(owner, session);
                if (session.Custom != null)
                {
                    session.Custom.Transport.Pause(AudioSettings.dspTime); session.Custom.PauseSources(); continue;
                }
                session.Transport.Pause(AudioSettings.dspTime);
                foreach (object sound in Members(owner)) if (!session.Blocked.Contains(sound)) Stop(sound);
            }
        }
        internal void AfterResume(object gameplay)
        {
            Mutated = false;
            if (!IsActive || !paused || !GameHooks.Alive(gameplay) || (bool)pausedProperty.GetValue(gameplay, null)) return;
            ResumeSessions();
        }
        private void ResumeSessions()
        {
            paused = false;
            foreach (WeakReference weak in owners)
            {
                object owner = weak.Target; Session session;
                if (!GameHooks.Alive(owner) || !sessions.TryGetValue(owner, out session)) continue;
                List<object> members = Members(owner); members.RemoveAll(delegate(object sound) { return session.Blocked.Contains(sound); });
                if (session.Custom != null) { session.Custom.Transport.Resume(); Update(owner, session); continue; }
                session.Transport.Resume(members, AudioSettings.dspTime, Lead());
                Notify(session);
            }
        }
        internal void Clear()
        {
            object gameplay = pausedGameplay == null ? null : pausedGameplay.Target;
            IList nativePaused = paused && GameHooks.Alive(gameplay) && (bool)pausedProperty.GetValue(gameplay, null)
                ? (IList)pausedSources.GetValue(gameplay) : null;
            foreach (WeakReference weak in owners)
            {
                object owner = weak.Target; Session session;
                if (owner != null)
                {
                    if (sessions.TryGetValue(owner, out session))
                    {
                        if (session.Custom != null) { session.Custom.Dispose(); session.Custom = null; }
                        // Native ResumeGame uses Play, so transfer the frozen position and ownership of resume.
                        if (nativePaused != null && session.Transport.Active)
                        {
                            foreach (KeyValuePair<AudioSource, SourceClaim> pair in session.Sources)
                            {
                                AudioSource source = pair.Key;
                                if (source == null || !Cd(pair.Value.Member) || !pair.Value.Lease.Owns(source.clip, source.pitch, source.dopplerLevel)) continue;
                                source.timeSamples = Math.Max(0, Math.Min(source.clip.samples - 1, (int)(session.Transport.Seconds(AudioSettings.dspTime) * source.clip.frequency)));
                                if (!nativePaused.Contains(source)) nativePaused.Add(source);
                            }
                        }
                        Release(session, null);
                    }
                    sessions.Remove(owner);
                }
            }
            owners.Clear();
            retainedCustom.Clear();
        }
        internal void Activate()
        {
            object gameplay = pausedGameplay == null ? null : pausedGameplay.Target;
            paused = GameHooks.Alive(gameplay) && (bool)pausedProperty.GetValue(gameplay, null);
            IsActive = true;
        }
        internal IEnumerable<CodeInstruction> RewriteTickMode(IEnumerable<CodeInstruction> source)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(source);
            int found = -1, count = 0;
            int getters = 0;
            foreach (CodeInstruction instruction in code) if (instruction.Calls(mode.GetGetMethod())) getters++;
            MethodInfo countGetter = typeof(List<AudioClip>).GetProperty("Count").GetGetMethod();
            for (int i = 1; i + 5 < code.Count; i++)
            {
                if (!code[i].Calls(mode.GetGetMethod()) || code[i + 1].opcode != OpCodes.Ldc_R4 || !(code[i + 1].operand is float) || (float)code[i + 1].operand != 1f
                    || (code[i + 2].opcode != OpCodes.Bne_Un && code[i + 2].opcode != OpCodes.Bne_Un_S)
                    || code[i + 3].opcode != OpCodes.Ldarg_0 || !code[i + 4].LoadsField(library) || !code[i + 5].Calls(countGetter)) continue;
                found = i; count++;
            }
            if (count != 1 || getters != 1) throw new InvalidOperationException("Expected one native CD mode/count gate in Tick; found " + count + " gates and " + getters + " getters. Shared CD disabled.");
            CodeInstruction replacement = new CodeInstruction(code[found]);
            replacement.opcode = OpCodes.Call;
            replacement.operand = typeof(Plugin).GetMethod("CdTickMode", BindingFlags.NonPublic | BindingFlags.Static);
            if (replacement.operand == null) throw new MissingMethodException("Shared CD bridge missing.");
            code[found] = replacement;
            return code;
        }
    }
}
