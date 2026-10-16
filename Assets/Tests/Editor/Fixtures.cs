using System;
using System.Collections.Generic;
using System.IO;
using Strands.Sp16;

namespace Strands.Tests
{
    /// <summary>Access to the unit-saved files in the Fixtures folder at the project root.</summary>
    static class Fixtures
    {
        // The Editor runs with the project root as its working directory.
        public static readonly string Folder = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures");

        public static byte[] Read(string name)
        {
            return File.ReadAllBytes(Path.Combine(Folder, name));
        }

        public static PrjNode ReadProject(string name)
        {
            return PrjFile.Read(Read(name));
        }

        /// <summary>Every .prj in the folder, and the .prj inside every .tpkg, found by listing the folder.</summary>
        public static IEnumerable<string> ProjectNames()
        {
            foreach (string path in SortedFiles("*.prj"))
                yield return Path.GetFileName(path);
            foreach (string path in SortedFiles("*.tpkg"))
                foreach (StoredZipReader.Entry entry in StoredZipReader.Read(File.ReadAllBytes(path)))
                    if (entry.Name.EndsWith(".prj", StringComparison.OrdinalIgnoreCase))
                        yield return Path.GetFileName(path) + "/" + entry.Name;
        }

        public static IEnumerable<string> PackageNames()
        {
            foreach (string path in SortedFiles("*.tpkg"))
                yield return Path.GetFileName(path);
        }

        /// <summary>Bytes of a project named as ProjectNames returns it.</summary>
        public static byte[] ReadProjectBytes(string name)
        {
            int slash = name.IndexOf('/');
            if (slash < 0)
                return Read(name);
            return ReadEntry(name.Substring(0, slash), name.Substring(slash + 1));
        }

        public static byte[] ReadEntry(string package, string entryName)
        {
            foreach (StoredZipReader.Entry entry in StoredZipReader.Read(Read(package)))
                if (entry.Name == entryName)
                    return entry.Data;
            throw new FileNotFoundException(entryName + " is not in " + package);
        }

        /// <summary>
        /// Generated audio in Fixtures/audio with one of the given extensions (no dot), found by
        /// listing the folder. Each file holds the same sound as the WAV named by ReferenceWav.
        /// </summary>
        public static IEnumerable<string> AudioPaths(params string[] extensions)
        {
            string[] files = Directory.GetFiles(Path.Combine(Folder, "audio"));
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string path in files)
                if (Array.IndexOf(extensions, Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) >= 0)
                    yield return path;
        }

        /// <summary>tone24.in24.aifc is checked against tone24.wav: the name up to the first dot.</summary>
        public static string ReferenceWav(string audioPath)
        {
            string name = Path.GetFileName(audioPath);
            return Path.Combine(Path.GetDirectoryName(audioPath), name.Substring(0, name.IndexOf('.')) + ".wav");
        }

        static string[] SortedFiles(string pattern)
        {
            string[] files = Directory.GetFiles(Folder, pattern);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
    }
}
