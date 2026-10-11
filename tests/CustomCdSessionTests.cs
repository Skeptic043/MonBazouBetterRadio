using System;
using System.Collections.Generic;
using RadioEnhancements;
using UnityEngine;

// Executable resource-lifecycle harness. Unity scheduling remains a live gameplay check.
internal static class CustomCdSessionTests
{
    internal sealed class Manager { public List<AudioClip> CDPlayer = new List<AudioClip>(); }
    internal sealed class Member { public AudioSource[] Speakers; }
    private static int count;
    private static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    private static CustomCdSession Session(Manager owner, Func<bool> paused)
    {
        return new CustomCdSession(owner, typeof(Manager).GetField("CDPlayer"), typeof(Member).GetField("Speakers"),
            delegate { }, delegate { }, delegate { }, paused, delegate { }, delegate { });
    }
    private static void Main()
    {
        var owner = new Manager(); var member = new Member { Speakers = new[] { new AudioSource(), new AudioSource() } };
        member.Speakers[0].pitch = 0.9f; member.Speakers[0].dopplerLevel = 2f;
        var entry = new CustomMusicEntry("song"); var track = new CustomMusicTrack(entry);
        var session = Session(owner, delegate { return false; });
        session.Publish(new CustomMusicCatalog(new[] { entry, new CustomMusicEntry("other") }));
        Check(owner.CDPlayer.Count == 2, "Native count mirrors catalog.");
        Check(ReferenceEquals(owner.CDPlayer[0], owner.CDPlayer[1]), "One inert token is reused.");
        session.Schedule(member, track, 0, 24000, 12.5);
        Check(track.Stream.Created == 2, "Every speaker has an independent reader.");
        Check(!ReferenceEquals(member.Speakers[0].clip, member.Speakers[1].clip), "Every speaker has an independent clip.");
        foreach (AudioSource source in member.Speakers)
        {
            Check(!ReferenceEquals(source.clip, owner.CDPlayer[0]), "Native count token is never attached to audio.");
            Check(source.Start == 12.5 && source.timeSamples == 24000, "All speakers have one DSP start and position.");
            Check(source.pitch == 1 && source.dopplerLevel == 0, "Sync takes pitch and doppler ownership.");
        }
        session.Transport.Register(member, 0);
        object logical; string title;
        Check(session.TryTitle(member, new[] { member.Speakers[0].clip, member.Speakers[1].clip }, out logical, out title)
            && ReferenceEquals(logical, entry) && title == "song", "Title resolves actual stream identity.");
        AudioClip foreign = new AudioClip(); member.Speakers[1].clip = foreign;
        Check(!session.TryTitle(member, null, out logical, out title), "A replaced source invalidates the title.");
        var eligible = session.Eligible(new object[] { member });
        Check(eligible.Count == 0, "A foreign source prevents automatic ownership fights.");
        Check(ReferenceEquals(member.Speakers[1].clip, foreign) && !foreign.Destroyed, "Foreign audio survives relinquishment.");
        Check(member.Speakers[0].clip == null && member.Speakers[0].pitch == 0.9f && member.Speakers[0].dopplerLevel == 2f,
            "Owned clip is detached and original source settings restored.");
        session.Reclaim(member); session.Schedule(member, track, 0, 0, 15);
        AudioClip ours = member.Speakers[0].clip;
        session.Stop(member);
        Check(ours.Destroyed && member.Speakers[0].clip == null, "Stopping disposes every owned clip after detachment.");
        session.Dispose();
        Check(owner.CDPlayer.Count == 0, "Disabling clears owned count tokens.");
        Check(AudioClip.UnsafeDestroy == 0, "No clip is destroyed while a source references it.");

        owner = new Manager(); member = new Member { Speakers = new[] { new AudioSource() } };
        session = Session(owner, delegate { return true; });
        session.Publish(new CustomMusicCatalog(new[] { entry }));
        session.Schedule(member, track, 0, 0, 4);
        Check(member.Speakers[0].Schedules == 0, "Paused transport never schedules audio.");
        var replaced = new List<AudioClip> { foreign }; owner.CDPlayer = replaced;
        session.Dispose();
        Check(ReferenceEquals(owner.CDPlayer, replaced) && replaced.Count == 1, "Foreign CD list survives cleanup.");
        Check(AudioClip.UnsafeDestroy == 0, "Paused cleanup also detaches clips before disposal.");
        Console.WriteLine("Custom CD output lifecycle checks passed: " + count);
    }
}

namespace UnityEngine
{
    public class Object
    {
        public static void Destroy(AudioClip clip)
        {
            foreach (AudioSource source in AudioSource.All) if (ReferenceEquals(source.clip, clip)) AudioClip.UnsafeDestroy++;
            clip.Destroyed = true;
        }
    }
    public sealed class AudioClip : Object
    {
        internal bool Destroyed; internal static int UnsafeDestroy;
        public static AudioClip Create(string name, int frames, int channels, int rate, bool stream) { return new AudioClip(); }
    }
    public sealed class AudioSource
    {
        internal static readonly List<AudioSource> All = new List<AudioSource>();
        public AudioClip clip; public float pitch = 1, dopplerLevel; public int timeSamples;
        internal double Start; internal int Schedules;
        public AudioSource() { All.Add(this); }
        public void Stop() { }
        public void PlayScheduled(double start) { Start = start; Schedules++; }
    }
}
namespace RadioEnhancements
{
    internal interface ICustomMusicSink
    {
        void Stop(object member); void Schedule(object member, CustomMusicTrack track, int index, int sample, double dspStart);
        void State(object member, int index, double seconds);
    }
    internal sealed class CustomMusicEntry { internal readonly string Title; internal CustomMusicEntry(string title) { Title = title; } }
    internal sealed class CustomMusicCatalog
    {
        internal static readonly CustomMusicCatalog Empty = new CustomMusicCatalog(new CustomMusicEntry[0]);
        internal readonly CustomMusicEntry[] Entries;
        internal CustomMusicCatalog(CustomMusicEntry[] entries) { Entries = entries; }
    }
    internal sealed class CustomMusicTrack
    {
        internal readonly CustomMusicEntry Entry; internal readonly StreamedAudio Stream = new StreamedAudio();
        internal int Samples { get { return 480000; } } internal int Frequency { get { return 48000; } }
        internal CustomMusicTrack(CustomMusicEntry entry) { Entry = entry; }
    }
    internal sealed class StreamedAudio
    {
        internal int Created; internal object CreateReader() { Created++; return new object(); }
        internal AudioClip CreateClip(object reader) { return new AudioClip(); }
    }
    internal sealed class CustomMusicTransport
    {
        private readonly ICustomMusicSink sink; private object member;
        internal int Index; internal CustomMusicTransport(ICustomMusicSink sink, int seed, Action<string> report) { this.sink = sink; }
        internal void Register(object value, int index) { member = value; Index = index; }
        internal bool Owns(object value) { return ReferenceEquals(member, value); }
        internal void Forget(object value) { if (Owns(value)) member = null; }
        internal void Dispose() { if (member != null) sink.Stop(member); }
    }
}
