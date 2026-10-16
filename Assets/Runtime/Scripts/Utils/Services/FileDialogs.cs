using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SFB;
using Strands.Import;
using UnityEngine;

namespace Strands.Utils
{
    /// <summary>The operating system's open and save dialogs. Both return null when cancelled.</summary>
    public static class FileDialogs
    {
        const string LastAudioFolderKey = "Strands.LastAudioFolder";
        const string LastPackageFolderKey = "Strands.LastPackageFolder";
        // The click that closes a dialog can land on the window underneath, and when that is on
        // the button the dialog came from it opens again, for as long as the user goes on pressing
        // Cancel in the same place. Nothing opens a dialog this soon after one has closed.
        const double QuietSeconds = 0.5;

        // Timed on a clock that only ever goes forward. This is kept from one run of play mode to
        // the next in the Editor, where Unity's own clock starts again each time: timed on that,
        // a dialog closed late in one run shut every dialog out of the next.
        static long closedAt = long.MinValue;

        static bool JustClosed
        {
            get { return closedAt != long.MinValue && (Stopwatch.GetTimestamp() - closedAt) < QuietSeconds * Stopwatch.Frequency; }
        }

        static void Closed()
        {
            closedAt = Stopwatch.GetTimestamp();
        }

        /// <summary>One or more audio files, or null when cancelled.</summary>
        public static string[] OpenAudioFiles()
        {
            if (JustClosed)
                return null;
            var filters = new[] { new ExtensionFilter("Audio", AudioLoaders.ExtensionsWithStemFiles) };
            string[] chosen = StandaloneFileBrowser.OpenFilePanel("Choose audio files", PlayerPrefs.GetString(LastAudioFolderKey, ""), filters, true);
            Closed();
            var paths = new List<string>();
            if (chosen != null)
                foreach (string path in chosen)
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(FromDialog(path));
            if (paths.Count == 0)
                return null;
            PlayerPrefs.SetString(LastAudioFolderKey, Path.GetDirectoryName(paths[0]));
            return paths.ToArray();
        }

        /// <summary>
        /// Every audio file in a folder the user chooses, in the order of their names, for a song
        /// whose stems are kept together in a folder of their own. Null when cancelled; none when
        /// the folder has no audio in it, with the folder's name in <paramref name="folder"/>.
        /// </summary>
        public static string[] OpenAudioFolder(out string folder)
        {
            folder = null;
            if (JustClosed)
                return null;
            string[] chosen = StandaloneFileBrowser.OpenFolderPanel("Choose folder of stems", PlayerPrefs.GetString(LastAudioFolderKey, ""), false);
            Closed();
            if (chosen == null || chosen.Length == 0 || string.IsNullOrEmpty(chosen[0]))
                return null;
            folder = FromDialog(chosen[0]);
            if (!Directory.Exists(folder))
                return null;
            PlayerPrefs.SetString(LastAudioFolderKey, folder);
            return AudioFilesIn(folder);
        }

        /// <summary>Every audio file in a folder, not the folders inside it, in the order of their names.</summary>
        public static string[] AudioFilesIn(string folder)
        {
            var paths = new List<string>();
            foreach (string path in Directory.GetFiles(folder))
            {
                string name = Path.GetFileName(path);
                // Files whose names start with a dot are the system's own, not the user's audio.
                if (!name.StartsWith(".") && Array.IndexOf(AudioLoaders.ExtensionsWithStemFiles, Path.GetExtension(name).TrimStart('.').ToLowerInvariant()) >= 0)
                    paths.Add(path);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths.ToArray();
        }

        public static string SavePackage(string defaultName)
        {
            if (JustClosed)
                return null;
            var filters = new[] { new ExtensionFilter("SP-16 package", "tpkg") };
            string chosen = StandaloneFileBrowser.SaveFilePanel("Save SP-16 package", PlayerPrefs.GetString(LastPackageFolderKey, ""), defaultName, filters);
            Closed();
            if (string.IsNullOrEmpty(chosen))
                return null;
            string path = FromDialog(chosen);
            if (!path.EndsWith(".tpkg", System.StringComparison.OrdinalIgnoreCase))
                path += ".tpkg";
            PlayerPrefs.SetString(LastPackageFolderKey, Path.GetDirectoryName(path));
            return path;
        }

        // The macOS dialog can hand back a file URL instead of a plain path.
        static string FromDialog(string path)
        {
            const string prefix = "file://";
            return path.StartsWith(prefix) ? System.Uri.UnescapeDataString(path.Substring(prefix.Length)) : path;
        }
    }
}
