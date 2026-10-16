using System;
using System.Threading;
using Strands.Audio;

namespace Strands.Import
{
    /// <summary>
    /// Reads one kind of audio file into the form the rest of the app works on. Analysis and export
    /// only ever see <see cref="WavData"/>, so a new format is a new loader and nothing else.
    /// </summary>
    public interface IAudioLoader
    {
        /// <summary>File extensions this loader reads, lower case and without the dot.</summary>
        string[] Extensions { get; }

        /// <summary>Runs on a background thread. Progress is 0 to 1; loaders that are quick need not report any.</summary>
        WavData Load(string path, IProgress<float> progress, CancellationToken cancel);
    }
}
