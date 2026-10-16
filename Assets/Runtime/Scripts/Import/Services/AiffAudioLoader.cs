using System;
using System.Threading;
using Strands.Audio;

namespace Strands.Import
{
    public sealed class AiffAudioLoader : IAudioLoader
    {
        public string[] Extensions { get { return new[] { "aif", "aiff", "aifc" }; } }

        public WavData Load(string path, IProgress<float> progress, CancellationToken cancel)
        {
            return AiffReader.Read(path);
        }
    }
}
