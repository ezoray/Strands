using System;
using System.Threading;
using Strands.Audio;

namespace Strands.Import
{
    /// <summary>A song in an MP4 by itself, such as an .m4a. A stem file has the same endings and is not loaded through here.</summary>
    public sealed class Mp4AudioLoader : IAudioLoader
    {
        public string[] Extensions { get { return new[] { "m4a", "mp4" }; } }

        public WavData Load(string path, IProgress<float> progress, CancellationToken cancel)
        {
            return Mp4Song.Read(path, progress, cancel);
        }
    }
}
