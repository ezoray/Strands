using System.IO;

namespace Strands.Audio
{
    /// <summary>
    /// Reads any audio file the app supports. The format is told from the first bytes of the file,
    /// not from its name. Whatever rate the file is at, the audio comes back at 44.1 kHz
    /// (<see cref="Resampler"/>); the readers themselves give a file at its own rate.
    /// </summary>
    public static class AudioFile
    {
        public static WavData Read(string path)
        {
            // An MP4 is read from the file itself: what macOS decodes of one it will only take that way.
            var start = new byte[8];
            using (FileStream stream = File.OpenRead(path))
                if (stream.Read(start, 0, start.Length) == start.Length && Mp4Song.IsMp4(start))
                    return Resampler.ToTargetRate(Mp4Song.Read(path));
            return Read(File.ReadAllBytes(path));
        }

        public static WavData Read(byte[] file)
        {
            return Resampler.ToTargetRate(ReadAtItsOwnRate(file));
        }

        static WavData ReadAtItsOwnRate(byte[] file)
        {
            if (StartsWith(file, "RIFF"))
                return WavReader.Read(file);
            if (StartsWith(file, "FORM"))
                return AiffReader.Read(file);
            if (FlacReader.IsFlac(file))
                return FlacReader.Read(file);
            if (Mp4Song.IsMp4(file))
                return Mp4Song.Read(new MemoryStream(file));
            // An MP3 has no marker of its own, so it is tried last.
            if (Mp3Reader.IsMp3(file))
                return Mp3Reader.Read(file);
            throw new InvalidDataException("Not a WAV, AIFF, FLAC, MP3 or MP4 file.");
        }

        static bool StartsWith(byte[] file, string tag)
        {
            if (file.Length < tag.Length)
                return false;
            for (int i = 0; i < tag.Length; i++)
                if (file[i] != tag[i])
                    return false;
            return true;
        }
    }
}
