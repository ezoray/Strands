using System;
using System.IO;
using System.Threading;
using Strands.Audio;

namespace Strands.Import
{
    public sealed class Mp3AudioLoader : IAudioLoader
    {
        public string[] Extensions { get { return new[] { "mp3" }; } }

        public WavData Load(string path, IProgress<float> progress, CancellationToken cancel)
        {
            return Mp3Reader.Read(File.ReadAllBytes(path), progress, cancel);
        }
    }
}
