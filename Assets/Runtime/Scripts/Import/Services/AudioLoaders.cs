using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Strands.Audio;

namespace Strands.Import
{
    /// <summary>Picks the loader for a file from its extension, and brings what it reads to 44.1 kHz.</summary>
    public static class AudioLoaders
    {
        static readonly IAudioLoader[] loaders = { new WavAudioLoader(), new AiffAudioLoader(), new FlacAudioLoader(), new Mp3AudioLoader(), new Mp4AudioLoader() };

        public static string[] Extensions
        {
            get
            {
                var all = new List<string>();
                foreach (IAudioLoader loader in loaders)
                    all.AddRange(loader.Extensions);
                return all.ToArray();
            }
        }

        /// <summary>
        /// What the file dialog offers: the audio formats and stem files (<see cref="StemFile"/>),
        /// which hold several stems and so are not loaded through here.
        /// </summary>
        public static string[] ExtensionsWithStemFiles
        {
            get
            {
                var all = new List<string>(Extensions);
                foreach (string extension in StemFile.Extensions)
                    if (!all.Contains(extension))
                        all.Add(extension);
                return all.ToArray();
            }
        }

        public static WavData Load(string path, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            foreach (IAudioLoader loader in loaders)
                if (Array.IndexOf(loader.Extensions, extension) >= 0)
                    return Resampler.ToTargetRate(loader.Load(path, progress, cancel), null, cancel);
            throw new NotSupportedException("Files of type ." + extension + " cannot be loaded yet. Use " + string.Join(", ", Extensions) + ".");
        }
    }
}
