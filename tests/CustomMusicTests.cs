using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using RadioEnhancements;

internal static class CustomMusicTests
{
    private static int checks;
    private static string work;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    private static void Wait(Func<bool> ready, string message)
    { DateTime until = DateTime.UtcNow.AddSeconds(10); bool done; while (!(done = ready()) && DateTime.UtcNow < until) Thread.Sleep(5); Check(done, message); }
    private static int Main(string[] args)
    {
        work = Path.Combine(Path.GetTempPath(), "RadioEnhancements-custom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            ScanChecks(); OrderChecks(); DecoderChecks(args[0]); MediaFoundationChecks(args[0]); StreamChecks(); FailureChecks(); TransportChecks();
            Console.WriteLine("Custom music checks passed: " + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(work, true); }
    }
    private static void ScanChecks()
    {
        string tree = Path.Combine(work, "tree");
        string nested = Path.Combine(tree, "Artist", "Album", "Disk"); Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(tree, "same.mp3"), "bad music");
        File.WriteAllText(Path.Combine(nested, "same.MP3"), "also bad");
        File.WriteAllText(Path.Combine(nested, "Björk 音楽 #%.ogg"), "bad but indexed");
        File.WriteAllText(Path.Combine(tree, "ignored.mp3.backup"), "ignore");
        File.WriteAllText(Path.Combine(tree, "ignored.flac"), "ignore");
        var first = CustomMusicLibrary.Scan(new[] { tree, nested, tree.ToUpperInvariant(), Path.Combine(work, "missing") }, 1, CancellationToken.None);
        Check(first.Entries.Length == 3, "Recursive scanner includes supported extensions and deduplicates overlapping roots");
        Check(first.Entries[0].Path != first.Entries[1].Path, "Duplicate basenames preserve full path identities");
        Check(Array.Exists(first.Entries, delegate(CustomMusicEntry e) { return e.Title == "Björk 音楽 #%"; }), "Unicode and URI punctuation remain literal titles");
        string external = Path.Combine(work, "external", "Album"); Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "outside.wav"), "indexed");
        var combined = CustomMusicLibrary.Scan(new[] { tree, Path.GetDirectoryName(external) }, 2, CancellationToken.None);
        Check(combined.Entries.Length == 4 && Array.Exists(combined.Entries, delegate(CustomMusicEntry e) { return e.Title == "outside"; })
            && Array.Exists(combined.Entries, delegate(CustomMusicEntry e) { return e.Title == "same"; }),
            "Native and external roots form one recursively indexed library");
        var again = CustomMusicLibrary.Scan(new[] { tree }, 2, CancellationToken.None);
        for (int i = 0; i < first.Entries.Length; i++) Check(StringComparer.OrdinalIgnoreCase.Equals(first.Entries[i].Path, again.Entries[i].Path), "Deterministic catalog ordering");
        var token = new CancellationTokenSource(); token.Cancel(); bool cancelled = false;
        try { CustomMusicLibrary.Scan(new[] { tree }, 3, token.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Cancelled scanner publishes no partial generation"); token.Dispose();
        using (var library = new CustomMusicLibrary())
        {
            library.ScanAsync(new[] { tree }); library.ScanAsync(new[] { Path.Combine(work, "missing") });
            CustomMusicCatalog catalog = null;
            Wait(delegate { return library.TryPublish(out catalog); }, "Latest scan completes");
            Check(catalog.Generation != 1 && catalog.Entries.Length == 0 && !library.Scanning, "Old scan cannot replace newer empty catalog");
            library.ScanAsync(new[] { tree }); Wait(delegate { return library.TryPublish(out catalog); }, "Nonempty scan completes");
            CustomMusicCatalog same = catalog;
            library.ScanAsync(new[] { tree }); Wait(delegate { return library.TryPublish(out catalog); }, "Unchanged rescan completes");
            Check(ReferenceEquals(same, catalog), "Unchanged LOAD retains catalog identity and current playback");
            library.ScanAsync(new[] { tree }); library.Dispose(); Check(!library.Scanning, "Dispose cancels scan without main-thread wait");
        }
    }
    private static void OrderChecks()
    {
        for (int seed = 0; seed < 20; seed++)
        {
            var order = new CustomMusicOrder(seed); order.Enabled = true;
            var failed = new HashSet<int>(); var heard = new HashSet<int> { 0 }; int current = 0;
            for (int step = 0; step < 94; step++)
            {
                int reserved = order.Peek(current, 95, failed);
                Check(order.Peek(current, 95, failed) == reserved, "Prefetch reservation is stable");
                int next = order.Next(current, 95, failed);
                Check(next == reserved && heard.Add(next), "Shuffle consumes each reserved track once per round"); current = next;
            }
            int previous = order.Previous(current, 95, 0, failed);
            Check(previous != current && order.Next(previous, 95, failed) == current, "Previous and next follow audible history");
            Check(order.Previous(current, 95, 3, failed) == current, "Previous after two seconds restarts current");
        }
        var sequential = new CustomMusicOrder(1); var broken = new HashSet<int> { 1, 2 };
        Check(sequential.Peek(0, 4, broken) == 3 && sequential.Next(0, 4, broken) == 3, "Sequential prefetch skips known malformed songs");
        broken.Add(0); broken.Add(3); Check(sequential.Next(0, 4, broken) == -1, "All failed tracks terminate bounded selection");
    }
    private static string Wav(string name, int seconds)
    {
        string path = Path.Combine(work, name);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            const int rate = 8000, channels = 2; int bytes = seconds * rate * channels * 2;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)channels);
            writer.Write(rate); writer.Write(rate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            for (int i = 0; i < seconds * rate; i++) { writer.Write((short)(Math.Sin(i * 0.1) * 15000)); writer.Write((short)(Math.Cos(i * 0.1) * 12000)); }
        }
        return path;
    }
    private static void DecoderChecks(string fixtures)
    {
        foreach (string path in new[] { Wav("decode.wav", 5), Path.Combine(fixtures, "stereo-noise.mp3"), Path.Combine(fixtures, "sine-stereo.ogg") })
        using (IPcmDecoder decoder = PcmDecoders.Open(path, CancellationToken.None))
        {
            Check(decoder.Channels == 2 && decoder.Frames > 4096 && decoder.SampleRate >= 8000, "Real decoder exposes layout: " + path);
            var first = new float[4096 * decoder.Channels]; int got = decoder.Read(first, 0, first.Length);
            Check(got == first.Length, "Real decoder reads initial PCM");
            decoder.Seek(512); var seek = new float[1024 * decoder.Channels]; got = decoder.Read(seek, 0, seek.Length);
            Check(got == seek.Length && decoder.Position == 1536, "Exact backward seek preserves frame position");
            for (int i = 0; i < seek.Length; i++) Check(Math.Abs(seek[i] - first[512 * decoder.Channels + i]) < 0.00005f, "Exact-seek PCM matches sequential PCM: " + path);
            decoder.Seek(2048); Check(decoder.Position == 2048, "Forward seek lands at requested frame");
        }
        foreach (string extension in new[] { ".wav", ".mp3", ".ogg" })
        {
            string path = Path.Combine(work, "corrupt" + extension); File.WriteAllText(path, "not audio"); bool failed = false;
            try { using (var ignored = PcmDecoders.Open(path, CancellationToken.None)) { } } catch (Exception) { failed = true; }
            Check(failed, "Malformed decoder rejects unsupported contents");
        }
    }
    private static void MediaFoundationChecks(string fixtures)
    {
        string reason; bool probed = false;
        Check(!MediaFoundationSupport.Decide(delegate { return true; }, delegate { probed = true; return true; }, out reason)
            && !probed && reason == MediaFoundationSupport.WineReason, "Wine guard never probes Media Foundation");
        Check(!MediaFoundationSupport.Decide(delegate { throw new DllNotFoundException(); }, delegate { return true; }, out reason), "Missing platform disables optional formats");
        MediaFoundationSupport.Detect(delegate { return false; }, delegate { return false; });
        Check(!PcmDecoders.Supports("track.aac") && !PcmDecoders.Supports("track.M4A") && !PcmDecoders.Supports("video.mp4") && PcmDecoders.Supports("track.mp3"), "Unavailable MF preserves existing formats");
        MediaFoundationSupport.Detect();
        if (!MediaFoundationSupport.Enabled) { Console.WriteLine("SKIP real Media Foundation checks: " + MediaFoundationSupport.Reason); return; }
        foreach (string name in new[] { "sine-stereo.aac", "sine-stereo.m4a", "sine-alac.m4a", "video-audio.mp4", "video-two-audio.mp4" })
        {
            string path = Path.Combine(fixtures, name);
            Check(PcmDecoders.Supports(path), "Available format included: " + name);
            using (var cancel = new CancellationTokenSource())
            using (IPcmDecoder decoder = PcmDecoders.Open(path, cancel.Token))
            {
                Check(decoder.Channels == 2 && decoder.SampleRate == 44100 && decoder.Frames >= 5 * 44100, "MF exposes expected audio layout: " + name);
                var pcm = new float[3 * 44100 * 2];
                Check(decoder.Read(pcm, 0, pcm.Length) == pcm.Length, "MF reads three seconds: " + name);
                int crossings = 0;
                for (int frame = 44101; frame < 88200; frame++) if (pcm[(frame - 1) * 2] <= 0 && pcm[frame * 2] > 0) crossings++;
                int frequency = name == "video-two-audio.mp4" && crossings >= 879 && crossings <= 881 ? 880 : 330;
                Check(Math.Abs(crossings - frequency) <= 1, "One audio stream is selected without video or mixing: " + name + " crossings=" + crossings);
                // A single sine has very high autocorrelation at its period. Mixing both tracks fails this bound.
                int lag = (int)Math.Round(44100.0 / frequency); double energy = 0, difference = 0;
                for (int frame = 44100; frame < 88000; frame++) { double a = pcm[frame * 2], b = pcm[(frame + lag) * 2]; energy += a * a; difference += (a - b) * (a - b); }
                Check(energy > 1 && difference / energy < 0.002, "Output contains only the selected tone: " + name);
                decoder.Seek(22050); var sought = new float[4096];
                Check(decoder.Read(sought, 0, sought.Length) == sought.Length && decoder.Position == 22050 + 2048, "MF backward seek returns requested frame: " + name);
                float maximumError = 0;
                for (int i = 0; i < sought.Length; i++) maximumError = Math.Max(maximumError, Math.Abs(sought[i] - pcm[44100 + i]));
                Check(maximumError < 0.00005f, "MF seek PCM matches sequential PCM: " + name + " error " + maximumError);
                decoder.Seek(decoder.Frames - 100); Check(decoder.Read(sought, 0, sought.Length) == 200 && decoder.Read(sought, 0, sought.Length) == 0, "MF reaches bounded EOF: " + name);
                cancel.Cancel(); bool cancelled = false;
                try { decoder.Seek(0); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "MF observes cancellation: " + name);
            }
            var stream = StreamedAudio.Start(path, 1);
            Wait(delegate { return stream.Fault != null || stream.Ready(44100); }, "MF worker prepares initial seek");
            Check(stream.Fault == null, "MF worker opens valid file: " + name);
            stream.Dispose(); Wait(delegate { return stream.WorkerCompleted; }, "MF worker cancellation releases resources");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { Check(true, "Disposed MF workers release file handles: " + name); }
        }
        bool rejected = false;
        try { using (var ignored = PcmDecoders.Open(Path.Combine(fixtures, "video-only.mp4"), CancellationToken.None)) { } }
        catch (IOException) { rejected = true; }
        Check(rejected, "Video without audio fails cleanly");
        string longVideo = Path.Combine(fixtures, "video-long-tail.mp4");
        using (var stream = StreamedAudio.Start(longVideo, 0))
        {
            Wait(delegate { return stream.Fault != null || (stream.IsOpen && stream.EndFrame < stream.Frames); }, "Long video reports selected audio EOF");
            Check(stream.Fault == null && stream.Damage == null && stream.EndFrame >= 5 * 44100 && stream.EndFrame < 6 * 44100,
                "Selected audio cleanly ends near five seconds, not twelve-second video duration");
        }
        var videoEntries = new[] { new CustomMusicEntry(longVideo, 0, 0), new CustomMusicEntry(Path.Combine(fixtures, "sine-stereo.m4a"), 0, 0) };
        var videoCatalog = new CustomMusicCatalog(1, videoEntries, new string[0]); var videoSink = new Sink();
        var videoMembers = new List<object> { new object() };
        using (var transport = new CustomMusicTransport(videoSink, 2, null))
        {
            Wait(delegate { transport.Update(videoCatalog, videoMembers, 0, 0.1, 0, 0); return transport.Active; }, "MP4 audio begins in real transport");
            Wait(delegate { return videoSink.LastTrack.Stream.EndFrame < videoSink.LastTrack.Stream.Frames; }, "Transport learns selected audio end");
            transport.Update(videoCatalog, videoMembers, 6, 0.1, 0, 0);
            Check(transport.Index == 1, "Transport advances to next song before longer MP4 video ends");
        }
        Console.WriteLine("PASS: real Windows Media Foundation AAC, M4A/ALAC and MP4 first-audio-stream checks.");
    }
    private static void StreamChecks()
    {
        string path = Wav("stream.wav", 40);
        using (var stream = StreamedAudio.Start(path, 0))
        {
            Wait(delegate { return stream.Fault != null || stream.Ready(0); }, "Worker publishes prepared PCM");
            Check(stream.Fault == null && stream.BufferSamples <= 20 * 8000 * 2 + 8192, "PCM memory is bounded to window, independent of track duration");
            var left = stream.CreateReader(); var right = stream.CreateReader();
            var clip1 = stream.CreateClip(left); var clip2 = stream.CreateClip(right);
            Check(!ReferenceEquals(clip1, clip2), "Each physical output has its own callback cursor");
            clip1.SetPosition(500); clip2.SetPosition(500); var a = new float[512]; var b = new float[512]; clip1.Read(a); clip2.Read(b);
            for (int i = 0; i < a.Length; i++) Check(a[i] == b[i], "Shared PCM produces identical aligned output readers");
            Check(left.Cursor == 756 && right.Cursor == 756, "Readers advance independently");
            clip1.Read(a); Check(left.Cursor == 1012 && right.Cursor == 756, "One reader never consumes another's cursor");
            stream.Request(200000); Wait(delegate { return stream.Ready(200000); }, "Worker prepares distant seek without callback disk I/O");
            clip2.SetPosition(200000); clip2.Read(b); Check(stream.MissingFrames == 0, "Prepared reader has no missing PCM");
            stream.Dispose(); clip2.Read(b); foreach (float value in b) Check(value == 0, "Disposed callback returns silence without worker access");
        }
        var cancelled = StreamedAudio.Start(path, 0); cancelled.Dispose(); Wait(delegate { return cancelled.WorkerCompleted; }, "Stream cancellation ends worker asynchronously");
    }
    private sealed class Sink : ICustomMusicSink
    {
        internal int Schedules, Stops, LastIndex, LastSample; internal double LastDsp;
        internal CustomMusicTrack LastTrack; internal readonly Dictionary<object, double> Times = new Dictionary<object, double>();
        public void Stop(object member) { Stops++; }
        public void Schedule(object member, CustomMusicTrack track, int index, int sample, double dsp)
        { Schedules++; LastTrack = track; LastIndex = index; LastSample = sample; LastDsp = dsp; }
        public void State(object member, int index, double seconds) { Times[member] = seconds; }
    }
    private sealed class ScriptedDecoder : IPcmDecoder
    {
        internal int End = Int32.MaxValue, Damage = Int32.MaxValue; internal bool Closed;
        public int Channels { get { return 2; } } public int SampleRate { get { return 8000; } }
        public long Frames { get { return 160000; } } public long Position { get; private set; }
        public void Seek(long frame) { Position = frame; }
        public int Read(float[] buffer, int offset, int count)
        {
            if (Position >= Damage) throw new IOException("Scripted decode damage");
            int frames = (int)Math.Min(count / 2, Math.Max(0, End - Position));
            for (int i = 0; i < frames * 2; i++) buffer[offset + i] = 0.25f;
            Position += frames; return frames * 2;
        }
        public void Dispose() { Closed = true; }
    }
    private static void FailureChecks()
    {
        string path = Path.Combine(work, "scripted.wav"); File.WriteAllText(path, "scripted only");
        var early = new ScriptedDecoder { End = 4000 };
        using (var stream = StreamedAudio.Start(path, 0, delegate { return early; }))
        {
            Wait(delegate { return stream.Ready(0); }, "Clean early end publishes bounded silence after valid PCM");
            Check(stream.EndFrame == 4000 && stream.Damage == null && stream.Fault == null, "Early-end sample boundary is exposed to transport");
        }
        var damage = new ScriptedDecoder { Damage = 4096 };
        using (var stream = StreamedAudio.Start(path, 0, delegate { return damage; }))
        {
            Wait(delegate { return stream.Damage != null; }, "Midstream decoder damage is contained after prior audio");
            Check(stream.DamageFrame == 4096 && stream.EndFrame == 4096 && stream.Fault == null, "Damage ends at actual sample instead of reported duration");
        }
        var failed = new ScriptedDecoder { Damage = 0 };
        using (var stream = StreamedAudio.Start(path, 0, delegate { return failed; }))
        { Wait(delegate { return stream.Fault != null; }, "Failure before any PCM faults stream"); Check(stream.Damage == null, "Initial fault is distinguished from partial damage"); }
        var entered = new ManualResetEvent(false);
        var blocked = StreamedAudio.Start(path, 0, delegate(string ignored, CancellationToken token)
        { entered.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new ScriptedDecoder(); });
        Check(entered.WaitOne(5000), "Blocked open starts on background worker");
        blocked.Dispose(); Wait(delegate { return blocked.WorkerCompleted; }, "Dispose cancels blocked open without blocking caller"); entered.Dispose();
        var engine = new CustomMusicEngine();
        var entry1 = new CustomMusicEntry(Wav("engine1.wav", 5), 0, 0);
        var entry2 = new CustomMusicEntry(Wav("engine2.wav", 5), 0, 0);
        CustomMusicTrack current = engine.Prepare(entry1, 0); engine.Prefetch(entry2); CustomMusicTrack next = engine.Prefetched;
        Check(ReferenceEquals(current, engine.Prepare(entry1, 2)), "Current track preparation reuses one decoder worker");
        Check(ReferenceEquals(next, engine.Prepare(entry2, 0)) && engine.Prefetched == null, "Prefetch promotion consumes existing worker without duplicate open"); engine.Dispose();
    }
    private static void TransportChecks()
    {
        string path1 = Wav("transport1.wav", 40), path2 = Wav("transport2.wav", 40);
        var entries = new[] { new CustomMusicEntry(path1, 0, 0), new CustomMusicEntry(path2, 0, 0) };
        var catalog = new CustomMusicCatalog(1, entries, new string[0]); var sink = new Sink();
        object first = new object(), second = new object(); var members = new List<object> { first };
        using (var transport = new CustomMusicTransport(sink, 1, null))
        {
            transport.SetShuffle(true); Check(transport.Shuffle, "Custom session restores remembered shuffle");
            transport.SetShuffle(false); Check(!transport.Shuffle, "Custom session restores sequential preference too");
            transport.Update(catalog, members, 10, 0.1, 0, 1);
            Check(transport.Pending && sink.Schedules == 0, "Selecting returns while worker prepares current song");
            Wait(delegate { transport.Update(catalog, members, 10, 0.1, 0, 1); return transport.Active && sink.Schedules > 0; }, "Prepared track schedules on shared timeline");
            Check(sink.LastIndex == 0 && sink.LastSample == 8000 && sink.LastDsp == 10.1, "First output starts at saved sample and common future DSP instant");
            members.Add(second); transport.Update(catalog, members, 13, 0.1, 0, 1);
            Check(sink.Schedules == 2 && Math.Abs(sink.LastSample - 32000) <= 1 && sink.LastDsp == 13.1, "Joining output compensates future scheduled time");
            Check(transport.Owns(first) && transport.Owns(second) && Math.Abs(sink.Times[first] - sink.Times[second]) < 0.0001, "Every member follows one shared clock");
            transport.Update(catalog, members, 14, 0.1, 0, 1); Check(sink.Schedules == 2, "Stable playback never reschedules existing outputs");
            transport.Pause(14); Check(sink.Stops == 2, "Pause stops both owned sources before native pause enumeration");
            double saved = transport.Seconds(14); transport.Update(catalog, members, 100, 0.1, 0, 1);
            Check(sink.Schedules == 2 && transport.Seconds(100) == saved, "Pause freezes timeline while DSP advances");
            transport.Resume(); Wait(delegate { transport.Update(catalog, members, 100, 0.1, 0, 1); return sink.Schedules == 4; }, "Resume schedules all outputs together");
            Check(sink.LastSample == (int)Math.Floor(saved * 8000), "Resume restores exact captured sample");
            transport.Seek(catalog, members, 100, 0.1, false);
            Wait(delegate { transport.Update(catalog, members, 100, 0.1, 0, 1); return sink.LastIndex == 1 && transport.Active; }, "Shared next promotes prepared song");
            Check(sink.LastSample == 0 && sink.Schedules == 6, "Shared next updates both outputs");
            members.Remove(first); transport.Forget(first); transport.Update(catalog, members, 101, 0.1, 0, 0);
            Check(sink.Schedules == 6 && transport.Owns(second), "Removing first radio never elects or restarts a leader");
            members.Clear(); transport.Forget(second); transport.Update(catalog, members, 102, 0.1, 0, 0);
            double remembered = transport.Seconds(102); Check(!transport.Active && !transport.Pending, "Last radio off cancels current and prefetch workers");
            members.Add(second); Wait(delegate { transport.Update(catalog, members, 200, 0.1, 0, 0); return transport.Active && sink.Schedules == 7; }, "Restart opens remembered track lazily");
            Check(sink.LastIndex == 1 && sink.LastSample == (int)Math.Floor(remembered * 8000), "All-off duration does not advance remembered cursor");
            var inserted = new CustomMusicCatalog(2, new[] { new CustomMusicEntry(path1 + "-unrelated.wav", 0, 0), entries[0], entries[1] }, new string[0]);
            transport.Update(inserted, members, 201, 0.1, 1, 0);
            Wait(delegate { transport.Update(inserted, members, 201, 0.1, 1, 0); return transport.Active && sink.Schedules == 8; }, "Changed catalog reopens current full path");
            Check(sink.LastIndex == 2 && ReferenceEquals(sink.LastTrack.Entry, entries[1]), "Insertion before current track preserves identity rather than old numeric index");
            transport.Update(new CustomMusicCatalog(2, new CustomMusicEntry[0], new string[0]), members, 200, 0.1, 0, 0);
            Check(!transport.Active && sink.Stops >= 5, "Catalog replacement stops old source before disposing its worker");
        }
        var brokenPath = Path.Combine(work, "transport-bad.mp3"); File.WriteAllText(brokenPath, "not music");
        var broken = new CustomMusicCatalog(3, new[] { new CustomMusicEntry(brokenPath, 0, 0) }, new string[0]);
        sink = new Sink(); var reports = new List<string>();
        using (var transport = new CustomMusicTransport(sink, 1, reports.Add))
        {
            Wait(delegate { transport.Update(broken, members, 0, 0.1, 0, 0); return reports.Count > 0 && !transport.Pending; }, "All malformed catalog reports failure and exits bounded selection");
            Check(sink.Schedules == 0 && reports.Count == 1, "Corrupt-only catalog neither starts audio nor repeats errors");
            for (int i = 0; i < 20; i++) transport.Update(broken, members, i, 0.1, 0, 0);
            Check(reports.Count == 1 && !transport.Pending, "All failed catalog does not reopen or repeat failures on later updates");
        }
        sink = new Sink();
        using (var transport = new CustomMusicTransport(sink, 1, null))
        {
            transport.Pause(0); transport.Update(catalog, members, 0, 0.1, 0, 2);
            Thread.Sleep(30); transport.Update(catalog, members, 30, 0.1, 0, 2);
            Check(sink.Schedules == 0 && transport.Pending && transport.Seconds(30) == 2, "Pause while first track prepares never starts output or advances time");
            transport.Resume(); Wait(delegate { transport.Update(catalog, members, 30, 0.1, 0, 2); return sink.Schedules == 1; }, "Pending prepared track starts only after resume");
        }
    }
}
