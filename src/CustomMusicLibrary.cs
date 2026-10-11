using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RadioEnhancements
{
    internal sealed class CustomMusicEntry
    {
        internal readonly string Path, Title;
        internal readonly long Size, Modified;
        internal CustomMusicEntry(string path, long size, long modified)
            { Path = path; Title = CleanTitle(System.IO.Path.GetFileNameWithoutExtension(path)); Size = size; Modified = modified; }
        private static string CleanTitle(string title)
        {
            // Titles are plain text. TMP tags and control characters must not become markup.
            var clean = new System.Text.StringBuilder(title.Length);
            foreach (char c in title) clean.Append(Char.IsControl(c) ? ' ' : c == '<' ? '‹' : c == '>' ? '›' : c);
            return clean.ToString().Trim();
        }
    }
    internal sealed class CustomMusicCatalog
    {
        internal static readonly CustomMusicCatalog Empty = new CustomMusicCatalog(0, new CustomMusicEntry[0], new string[0]);
        internal readonly int Generation;
        internal readonly CustomMusicEntry[] Entries;
        internal readonly string[] Warnings;
        internal CustomMusicCatalog(int generation, CustomMusicEntry[] entries, string[] warnings)
        { Generation = generation; Entries = entries; Warnings = warnings; }
    }

    // Enumeration and stat calls run exclusively on the scan worker. Publication happens on
    // the caller's main thread, and a cancelled generation never replaces the current catalog.
    internal sealed class CustomMusicLibrary : IDisposable
    {
        private CancellationTokenSource cancellation;
        private Task<CustomMusicCatalog> scan;
        private int generation;
        private bool disposed;
        internal CustomMusicCatalog Current { get; private set; }
        internal bool Scanning { get { return scan != null; } }
        internal CustomMusicLibrary() { Current = CustomMusicCatalog.Empty; }
        internal void ScanAsync(string[] roots)
        {
            if (disposed) throw new ObjectDisposedException("CustomMusicLibrary");
            CancelScan();
            string[] request = roots == null ? new string[0] : (string[])roots.Clone();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            int requestGeneration = ++generation;
            scan = Task.Factory.StartNew(delegate { return Scan(request, requestGeneration, token); }, token, TaskCreationOptions.None, TaskScheduler.Default);
        }
        internal bool TryPublish(out CustomMusicCatalog catalog)
        {
            catalog = Current;
            if (disposed || scan == null || !scan.IsCompleted) return false;
            Task<CustomMusicCatalog> done = scan; scan = null;
            cancellation.Dispose(); cancellation = null;
            if (done.IsCanceled) return false;
            if (done.IsFaulted)
            {
                Exception error = done.Exception.GetBaseException();
                Current = new CustomMusicCatalog(generation, new CustomMusicEntry[0], new[] { error.Message });
            }
            else if (!SameEntries(Current.Entries, done.Result.Entries)) Current = done.Result;
            catalog = Current; return true;
        }
        private static bool SameEntries(CustomMusicEntry[] first, CustomMusicEntry[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++)
                if (!StringComparer.OrdinalIgnoreCase.Equals(first[i].Path, second[i].Path) ||
                    first[i].Size != second[i].Size || first[i].Modified != second[i].Modified) return false;
            return true;
        }
        private void CancelScan()
        {
            if (cancellation == null) return;
            CancellationTokenSource old = cancellation;
            Task<CustomMusicCatalog> done = scan;
            cancellation = null; scan = null;
            old.Cancel();
            if (done == null) { old.Dispose(); return; }
            // Observe exceptions and dispose handles after the worker exits, without blocking Unity.
            done.ContinueWith(delegate(Task<CustomMusicCatalog> task) { if (task.IsFaulted) { var observed = task.Exception; } old.Dispose(); }, TaskScheduler.Default);
        }
        public void Dispose() { if (disposed) return; disposed = true; CancelScan(); }

        internal static CustomMusicCatalog Scan(string[] roots, int generation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            MediaFoundationSupport.EnsureDetected();
            const int maximumTracks = 100000, maximumDirectories = 100000, maximumDepth = 256;
            var files = new Dictionary<string, CustomMusicEntry>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            var stack = new Stack<KeyValuePair<string, int>>();
            foreach (string root in roots)
            {
                token.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(root)) continue;
                try { stack.Push(new KeyValuePair<string, int>(System.IO.Path.GetFullPath(root), 0)); }
                catch (Exception ex) { warnings.Add("Music root could not be read: " + ex.Message); }
            }
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                KeyValuePair<string, int> item = stack.Pop();
                if (!directories.Add(item.Key)) continue;
                if (directories.Count > maximumDirectories) { warnings.Add("Directory scan limit reached."); break; }
                try
                {
                    var directory = new DirectoryInfo(item.Key);
                    if (!directory.Exists) continue;
                    // Reparse points can loop, cross the selected tree, or revisit an ancestor.
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { warnings.Add("Skipped linked music folder: " + item.Key); continue; }
                    foreach (FileInfo file in directory.EnumerateFiles())
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (!PcmDecoders.Supports(file.Name) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            string path = file.FullName;
                            files[path] = new CustomMusicEntry(path, file.Length, file.LastWriteTimeUtc.Ticks);
                        }
                        catch (Exception ex) { warnings.Add("Music file could not be read: " + file.FullName + ": " + ex.Message); }
                        if (files.Count >= maximumTracks) break;
                    }
                    if (files.Count >= maximumTracks) { warnings.Add("Music track limit reached."); break; }
                    if (item.Value >= maximumDepth) { warnings.Add("Music folder depth limit reached: " + item.Key); continue; }
                    foreach (DirectoryInfo child in directory.EnumerateDirectories())
                        stack.Push(new KeyValuePair<string, int>(child.FullName, item.Value + 1));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { warnings.Add("Music folder could not be read: " + item.Key + ": " + ex.Message); }
            }
            var entries = new List<CustomMusicEntry>(files.Values);
            entries.Sort(delegate(CustomMusicEntry a, CustomMusicEntry b)
            {
                int result = StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
                return result == 0 ? StringComparer.Ordinal.Compare(a.Path, b.Path) : result;
            });
            return new CustomMusicCatalog(generation, entries.ToArray(), warnings.ToArray());
        }
    }
}
