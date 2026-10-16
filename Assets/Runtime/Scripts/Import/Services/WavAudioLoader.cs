using System;
using System.Threading;
using Strands.Audio;

namespace Strands.Import
{
    public sealed class WavAudioLoader : IAudioLoader
    {
        public string[] Extensions { get { return new[] { "wav" }; } }

        public WavData Load(string path, IProgress<float> progress, CancellationToken cancel)
        {
            return WavReader.Read(path);
        }
    }
}
