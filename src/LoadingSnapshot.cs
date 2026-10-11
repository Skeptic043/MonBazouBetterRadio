using System;
using System.Collections.Generic;
using System.IO;

namespace RadioEnhancements
{
    // This is a change detector, never a source of track indices.
    internal sealed class LoadingSnapshot
    {
        internal sealed class FileStamp
        {
            internal readonly string Path;
            internal readonly string Format;
            internal readonly long Length;
            internal readonly long ModifiedUtc;
            internal FileStamp(string path, string format, long length, long modifiedUtc)
            { Path = path; Format = format; Length = length; ModifiedUtc = modifiedUtc; }
            internal string Key { get { return Format + "\0" + Path; } }
        }

        internal readonly FileStamp[] Files;
        internal LoadingSnapshot(IEnumerable<FileStamp> files)
        {
            var sorted = new List<FileStamp>(files);
            sorted.Sort(delegate(FileStamp x, FileStamp y) { return StringComparer.Ordinal.Compare(x.Key, y.Key); });
            Files = sorted.ToArray();
        }

        internal static LoadingSnapshot Capture(string directory)
        {
            var files = new List<FileStamp>();
            string[] patterns = { "*.wav*", "*.mp3*", "*.ogg*" };
            string[] formats = { "WAV", "MPEG", "OGGVORBIS" };
            for (int group = 0; group < patterns.Length; ++group)
            {
                // Preserve the game's overlapping globs: a path can represent two attempts.
                foreach (FileInfo file in new DirectoryInfo(directory).GetFiles(patterns[group]))
                    files.Add(new FileStamp(file.FullName, formats[group], file.Length, file.LastWriteTimeUtc.Ticks));
            }
            return new LoadingSnapshot(files);
        }

        internal bool SameAs(LoadingSnapshot other)
        {
            if (other == null || Files.Length != other.Files.Length) return false;
            for (int i = 0; i < Files.Length; ++i)
            {
                FileStamp a = Files[i], b = other.Files[i];
                if (!String.Equals(a.Key, b.Key, StringComparison.Ordinal)
                    || a.Length != b.Length || a.ModifiedUtc != b.ModifiedUtc) return false;
            }
            return true;
        }
    }
}
