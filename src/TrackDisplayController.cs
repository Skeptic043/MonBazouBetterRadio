using System;
using System.Collections;
using System.Text;

namespace RadioEnhancements
{
    internal static class TrackDisplayController
    {
        internal static string TitleFromPath(string path)
        {
            if (String.IsNullOrEmpty(path)) return null;
            int start = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1;
            string name = path.Substring(start);
            int extension = name.LastIndexOf('.');
            if (extension >= 0) name = name.Substring(0, extension);
            StringBuilder text = new StringBuilder(name.Length);
            foreach (char character in name)
            {
                // Never let a filename inject TMP tags. Angle brackets are invalid in Windows filenames.
                if (character == '<') text.Append('\uFF1C');
                else if (character == '>') text.Append('\uFF1E');
                else text.Append(Char.IsControl(character) ? ' ' : character);
            }
            string title = text.ToString().Trim();
            return title.Length == 0 ? null : title;
        }

        internal static bool TryResolve(MusicLibraryTracker tracker, object owner, IList library,
            bool powered, float mode, int index, string currentText, object[] speakerClips, out string title)
        {
            title = null;
            if (!powered || mode != 1f || library == null || index < 0 || index >= library.Count
                || !String.Equals(currentText, "CD TRACK " + (index + 1).ToString("00"), StringComparison.Ordinal)
                || speakerClips == null || speakerClips.Length == 0) return false;
            object clip = library[index];
            if (clip == null) return false;
            foreach (object speakerClip in speakerClips)
                if (!ReferenceEquals(clip, speakerClip)) return false;
            return tracker.TryGetTitle(owner, clip, out title);
        }
    }
}
