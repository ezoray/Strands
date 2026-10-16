using System;
using System.Threading;
using Strands.Audio;

namespace Strands.Import
{
    public sealed class FlacAudioLoader : IAudioLoader
    {
        public string[] Extensions { get { return new[] { "flac" }; } }

        public WavData Load(string path, IProgress<float> progress, CancellationToken cancel)
        {
            return FlacReader.Read(path);
        }
    }
}
