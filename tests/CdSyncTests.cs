using System;
using System.Collections.Generic;
using RadioEnhancements;

internal static class CdSyncTests
{
    private sealed class Scheduled
    {
        internal CdSyncTrack Track;
        internal int Index, Sample;
        internal double Start;
        internal double At(double dsp) { return Sample + Math.Max(0, dsp - Start) * Track.Frequency; }
    }
    private sealed class Sink : ICdSyncSink
    {
        internal readonly Dictionary<object, Scheduled> Playing = new Dictionary<object, Scheduled>();
        internal readonly Dictionary<object, int> Calls = new Dictionary<object, int>();
        internal readonly Dictionary<object, double> Times = new Dictionary<object, double>();
        public void Schedule(object member, CdSyncTrack track, int index, int sample, double start)
        {
            Playing[member] = new Scheduled { Track = track, Index = index, Sample = sample, Start = start };
            if (!Calls.ContainsKey(member)) Calls[member] = 0;
            Calls[member]++;
        }
        public void State(object member, int index, double seconds) { Times[member] = seconds; }
    }
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Near(double actual, double expected, double tolerance, string message) { Check(Math.Abs(actual - expected) <= tolerance, message + ": " + actual + " != " + expected); }
    private static List<CdSyncTrack> Library(int count, int seconds)
    {
        List<CdSyncTrack> tracks = new List<CdSyncTrack>();
        for (int i = 0; i < count; i++) tracks.Add(new CdSyncTrack(new object(), seconds * (i % 2 == 0 ? 44100 : 48000), i % 2 == 0 ? 44100 : 48000));
        return tracks;
    }
    private static int Main()
    {
        try
        {
            JoinAndControls(); PauseAndResume(); BoundariesAndLifecycle(); ShuffleAndReload(); Ownership(); EarlyJoinAndRejoin(); PausedLibraryReplacement();
            Console.WriteLine("PASS: " + checks + " shared CD transport checks."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void JoinAndControls()
    {
        Sink sink = new Sink(); CdSyncTransport transport = new CdSyncTransport(sink, 12);
        List<CdSyncTrack> library = Library(5, 180); object a = new object(), b = new object(), c = new object();
        List<object> members = new List<object> { a };
        transport.Update(library, members, 20, 0.1, 2, 22050);
        Check(transport.Index == 2 && sink.Playing[a].Sample == 22050, "First radio retains valid native track/position");
        members.Add(b); transport.Update(library, members, 31.01321, 0.1, 0, 0);
        Check(sink.Calls[a] == 1 && sink.Calls[b] == 1, "Join never restarts existing source");
        Near(sink.Playing[a].At(32), sink.Playing[b].At(32), 0.500001, "Join compensated to same future DSP sample");
        members.Add(c); transport.Update(library, members, 39.05551, 0.1, 4, 0);
        Near(sink.Playing[a].At(40), sink.Playing[c].At(40), 0.500001, "Third radio sync independent of own old song");
        for (int i = 0; i < 500; i++) transport.Update(library, members, 40 + i / 60.0, 0.1, 4, 0);
        Check(sink.Calls[a] == 1 && sink.Calls[b] == 1 && sink.Calls[c] == 1, "Steady update never reschedules playback");
        transport.Seek(library, members, 49, 0.1, false);
        Check(transport.Index == 3, "Shared sequential next");
        Check(sink.Playing[a].Start == sink.Playing[b].Start && sink.Playing[b].Start == sink.Playing[c].Start, "All speakers receive same scheduled seek time");
        Near(sink.Playing[a].At(50), sink.Playing[b].At(50), 0, "Changed sample frequency remains synchronized");
        transport.Seek(library, members, 50, 0.1, true); Check(transport.Index == 2, "Previous within two seconds selects preceding track");
        transport.Seek(library, members, 56, 0.1, true); Check(transport.Index == 2, "Previous after two seconds restarts shared current track");
        members.Remove(a); transport.Update(library, members, 57, 0.1, 0, 0);
        Check(!transport.Owns(a) && transport.Owns(b), "Power off detaches only one radio");
        Check(sink.Calls[b] == 4 && sink.Calls[c] == 4, "Removing original radio does not restart other members");
    }
    private static void PauseAndResume()
    {
        Sink sink = new Sink(); CdSyncTransport transport = new CdSyncTransport(sink, 19);
        List<CdSyncTrack> library = Library(3, 60); object a = new object(), b = new object(); List<object> members = new List<object> { a, b };
        transport.Update(library, members, 0, 0.1, 0, 0);
        transport.Pause(10.1); Near(transport.Seconds(10.1), 10, 0.00003, "Freeze actual sample at pause");
        transport.Update(library, members, 500, 0.1, 0, 0);
        Near(transport.Seconds(500), 10, 0.00003, "DSP advancing during game pause does not advance CD");
        Check(transport.Index == 0 && sink.Calls[a] == 1, "Long pause cannot auto advance");
        transport.Pause(700); transport.Resume(members, 800, 0.1);
        Check(sink.Calls[a] == 2 && sink.Calls[b] == 2, "Resume reschedules both sources once");
        Check(sink.Playing[a].Start == 800.1 && sink.Playing[a].Start == sink.Playing[b].Start, "Resume shares future DSP timestamp");
        Near(transport.Seconds(801.1), 11, 0.00003, "Resumed timeline excludes entire pause");
        transport.Resume(members, 802, 0.1); Check(sink.Calls[a] == 2, "Repeated resume does not restart");
        transport.Clear(); transport.Pause(900); transport.Update(library, members, 910, 0.1, 1, 0);
        Near(transport.Seconds(920), 0, 0, "New timeline created while paused remains frozen");
        transport.Resume(members, 930, 0.1); Near(transport.Seconds(931.1), 1, 0.00003, "Paused library start resumes correctly");
    }
    private static void BoundariesAndLifecycle()
    {
        Sink sink = new Sink(); CdSyncTransport transport = new CdSyncTransport(sink, 71);
        List<CdSyncTrack> library = Library(2, 2); object a = new object(), b = new object(); List<object> members = new List<object> { a };
        transport.Update(library, members, 1, 0.1, 0, 0);
        members.Add(b); transport.Update(library, members, 3.05, 0.1, 0, 0);
        Check(!sink.Playing.ContainsKey(b), "Near-end joining source waits rather than playing clamped stale position");
        transport.Update(library, members, 3.11, 0.1, 0, 0);
        Check(transport.Index == 1 && sink.Playing[b].Sample == 0, "Waiting member joins shared automatic next");
        Check(sink.Playing[a].Start == sink.Playing[b].Start, "Auto next same DSP time");
        members.Clear(); transport.Update(library, members, 4, 0.1, 0, 0);
        Check(!transport.Active, "Last radio off clears session playback");
        members.Add(b); transport.Update(library, members, 5, 0.1, 0, 1234);
        Check(transport.Index == 0 && sink.Playing[b].Sample == 1234, "Fresh first member supplies own valid initial state");
        library[0] = new CdSyncTrack(new object(), 88200, 44100);
        transport.Update(library, members, 6, 0.1, 0, 0);
        Check(ReferenceEquals(library[0], sink.Playing[b].Track), "Same-count replacement cannot keep stale clip timeline");
        library.Clear(); transport.Update(library, members, 7, 0.1, 0, 0); Check(!transport.Active, "Empty library clears transport");
        List<CdSyncTrack> single = Library(1, 1); transport.Update(single, members, 8, 0.1, 100, 0);
        transport.Update(single, members, 9.2, 0.1, 0, 0); Check(transport.Index == 0 && sink.Playing[b].Sample == 0, "Single song automatically repeats together");
    }
    private static void ShuffleAndReload()
    {
        Sink sink = new Sink(); CdSyncTransport transport = new CdSyncTransport(sink, 721);
        transport.SetShuffle(true); Check(transport.Shuffle, "New native session restores remembered shuffle");
        transport.SetShuffle(false); Check(!transport.Shuffle, "New native session restores sequential preference");
        List<CdSyncTrack> library = Library(9, 120); List<object> members = new List<object> { new object(), new object() };
        transport.Update(library, members, 0, 0.1, 0, 0); transport.Toggle();
        HashSet<int> seen = new HashSet<int> { 0 }; int previous = 0;
        for (int i = 1; i < 9; i++)
        {
            transport.Seek(library, members, i, 0.1, false);
            Check(seen.Add(transport.Index), "One shared shuffle cycle has no repeats");
            Check(sink.Playing[members[0]].Index == sink.Playing[members[1]].Index, "Shared shuffle changes every member");
            if (i == 7) previous = transport.Index;
        }
        transport.Seek(library, members, 8.5, 0.1, true); Check(transport.Index == previous, "Previous shares shuffle history");
        transport.Clear(); Check(transport.Shuffle, "Library reset retains session shuffle setting");
        transport.Update(library, members, 10, 0.1, 2, 0);
        transport.Seek(library, members, 11, 0.1, false); Check(transport.Index != 2, "Reset shuffle history initializes from new first clip");
    }
    private static void Ownership()
    {
        object clip = new object(); CdSyncSourceLease lease = new CdSyncSourceLease(0.9f, 0.7f); lease.Clip = clip;
        Check(lease.Owns(clip, 1f, 0f), "Exact source settings identify owned playback");
        Check(!lease.Owns(new object(), 1f, 0f), "Foreign clip replacement relinquishes ownership");
        Check(!lease.Owns(clip, 0.8f, 0f), "Foreign pitch replacement relinquishes ownership");
        Check(!lease.Owns(clip, 1f, 0.2f), "Foreign Doppler replacement relinquishes ownership");
        Near(lease.RestorePitch(1f), 0.9f, 0, "Detach restores original pitch");
        Near(lease.RestoreDoppler(0f), 0.7f, 0, "Detach restores original Doppler");
        Near(lease.RestorePitch(0.8f), 0.8f, 0, "Detach preserves externally changed pitch");
        Near(lease.RestoreDoppler(0.2f), 0.2f, 0, "Detach preserves externally changed Doppler");
    }
    private static void EarlyJoinAndRejoin()
    {
        Sink sink = new Sink(); CdSyncTransport transport = new CdSyncTransport(sink, 818);
        List<CdSyncTrack> library = Library(3, 60); object a = new object(), b = new object(); List<object> members = new List<object> { a };
        transport.Update(library, members, 0, 0.1, 0, 0);
        members.Add(b); transport.Update(library, members, 0.015, 0.1, 0, 0);
        Check(sink.Calls[a] == 1, "Join before scheduled first start does not restart first radio");
        Near(sink.Playing[a].At(1), sink.Playing[b].At(1), 0.500001, "Early join uses same sample timeline");
        Check(sink.Playing[b].Start > sink.Playing[a].Start, "Joining radio waits until its safe future DSP instant");
        transport.Forget(b); int aCalls = sink.Calls[a];
        transport.Update(library, members, 12.037, 0.1, 2, 0);
        Check(sink.Calls[a] == aCalls && sink.Calls[b] == 2, "Reclaiming forgotten member does not restart other radios");
        Check(transport.Index == 0, "Rejoining member cannot replace shared track with its local old index");
        Near(sink.Playing[a].At(13), sink.Playing[b].At(13), 0.500001, "Reclaimed member rejoins exact shared timeline");
    }
    private static void PausedLibraryReplacement()
    {
        Sink sink = new Sink(); CdSyncTransport transport = new CdSyncTransport(sink, 919);
        List<CdSyncTrack> library = Library(3, 60); object a = new object(), b = new object(); List<object> members = new List<object> { a };
        transport.Update(library, members, 0, 0.1, 0, 0); transport.Pause(10);
        transport.Clear(); library = Library(2, 120);
        transport.Update(library, members, 40, 0.1, 1, 0);
        members.Add(b); transport.Update(library, members, 80, 0.1, 0, 0);
        Near(transport.Seconds(100), 0, 0, "Rebuilt library cannot advance while game remains paused");
        Check(transport.Index == 1 && ReferenceEquals(sink.Playing[b].Track, library[1]), "Second paused member uses rebuilt shared clip");
        transport.Resume(members, 200, 0.1);
        Check(sink.Playing[a].Start == sink.Playing[b].Start && sink.Playing[a].Start == 200.1, "Paused rebuild and join resume at one scheduled instant");
        Near(sink.Playing[a].At(201), sink.Playing[b].At(201), 0, "Paused rebuilt members remain sample-aligned");
        Near(transport.Seconds(201.1), 1, 0.00003, "New shared timeline resumes after reset");
    }
}
