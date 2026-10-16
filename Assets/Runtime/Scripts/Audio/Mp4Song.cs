using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Strands.Audio
{
    /// <summary>
    /// Reads a song that is in an MP4 file by itself: an .m4a, or the sound of an .mp4 that has a
    /// picture as well. It is the file's first audio track, from its first real frame to its last
    /// where the file's edit list says which those are. AAC is decoded here on every system
    /// (<see cref="AacReader"/>). Anything else the track may be, Apple Lossless among them, is
    /// left to macOS (<see cref="AudioToolboxReader"/>) and so can only be read on a Mac.
    /// A stem file is an MP4 too, and is told from a song by its list of stems
    /// (<see cref="StemFile.IsStemFile"/>).
    /// </summary>
    public static class Mp4Song
    {
        /// <summary>True for a file that starts as an MP4 does, with the box that says what kind of file it is.</summary>
        public static bool IsMp4(byte[] start)
        {
            return start != null && start.Length >= 8 && start[4] == 'f' && start[5] == 't' && start[6] == 'y' && start[7] == 'p';
        }

        /// <summary>Reads the song at the file's own sample rate, as 16-bit stereo.</summary>
        public static WavData Read(string path, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            Mp4AudioTrack track;
            using (FileStream stream = File.OpenRead(path))
            {
                track = FirstAudioTrack(stream);
                if (AacReader.CanDecode(track))
                {
                    try
                    {
                        return AacReader.Read(stream, track, progress, cancel);
                    }
                    catch (Exception e) when (AudioToolboxReader.Available && !(e is OperationCanceledException))
                    {
                        // Some AAC is beyond the decoder here, HE-AAC for one, which it takes for
                        // plain AAC and then cannot follow. On a Mac the system's decoder has it.
                    }
                }
            }
            if (!AudioToolboxReader.Available)
                throw new NotSupportedException("The sound in this file is coded as " + Coding(track) + ", which can only be read on a Mac for now: that is left to macOS to decode. AAC, which most .m4a files are, can be read here.");
            return AudioToolboxReader.ReadTrack(path, 0, progress, cancel);
        }

        /// <summary>The same from a stream, for AAC only: what macOS decodes it needs as a file.</summary>
        public static WavData Read(Stream stream, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            Mp4AudioTrack track = FirstAudioTrack(stream);
            if (!AacReader.CanDecode(track))
                throw new NotSupportedException("The sound in this MP4 is coded as " + Coding(track) + ", which has to be read from the file itself.");
            return AacReader.Read(stream, track, progress, cancel);
        }

        static Mp4AudioTrack FirstAudioTrack(Stream stream)
        {
            List<Mp4AudioTrack> tracks = Mp4.AudioTracks(stream);
            if (tracks.Count == 0)
                throw new InvalidDataException("This MP4 has no sound in it.");
            return tracks[0];
        }

        static string Coding(Mp4AudioTrack track)
        {
            return track.Coding == "alac" ? "Apple Lossless" : string.IsNullOrEmpty(track.Coding) ? "something this app does not know" : "\"" + track.Coding + "\"";
        }
    }
}
