using System;
using System.IO;
using System.Threading;
using SharpJaad.AAC;

namespace Strands.Audio
{
    /// <summary>
    /// Decodes a track of AAC audio from an MP4 file, with our copy of SharpJaad (see
    /// Assets/SharpJaad). It is our own code from the file to the samples, so it works the same on
    /// every system and gives the same samples every time it is run.
    /// The packets are found by <see cref="Mp4"/> and handed to the decoder one at a time. Each
    /// decodes to 1024 frames, and the file's edit list says which of all those frames are the
    /// sound: an encoder puts 2112 frames of its own in front, and fills out the last packet.
    /// </summary>
    public static class AacReader
    {
        const int Mpeg4Audio = 0x40;

        /// <summary>True when a track is AAC of a kind the decoder takes. Apple Lossless, for one, is not.</summary>
        public static bool CanDecode(Mp4AudioTrack track)
        {
            if (track.Coding != "mp4a" || track.ObjectType != Mpeg4Audio || track.DecoderSettings == null)
                return false;
            try
            {
                DecoderConfig settings = DecoderConfig.ParseMP4DecoderSpecificInfo(track.DecoderSettings);
                return settings != null && Decoder.CanDecode(settings.GetProfile());
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads the whole track as 16-bit stereo at its own sample rate, from its first real frame
        /// to its last. A packet that cannot be decoded stops the reading: the file is damaged, and
        /// a stem with a hole in it is not one to build a song from.
        /// </summary>
        public static WavData Read(Stream stream, Mp4AudioTrack track, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            if (!CanDecode(track))
                throw new NotSupportedException("The track is not AAC audio that can be decoded here (it is marked " + (string.IsNullOrEmpty(track.Coding) ? "nothing" : track.Coding) + ").");
            var decoder = new Decoder(track.DecoderSettings);
            var buffer = new SampleBuffer();
            buffer.SetBigEndian(false);

            int packets = track.PacketCount, channels = 0, rate = 0, done = 0;
            short[] samples = null;
            for (int packet = 0; packet < packets; packet++)
            {
                cancel.ThrowIfCancellationRequested();
                byte[] data;
                try
                {
                    decoder.DecodeFrame(track.ReadPacket(stream, packet), buffer);
                    data = buffer.Data;
                }
                catch (AACException e)
                {
                    throw new InvalidDataException(Damaged(packet, packets) + " " + e.Message);
                }
                // The decoder hands back nothing when a packet ends before its audio does.
                if (data == null || data.Length == 0 || buffer.Channels < 1 || buffer.BitsPerSample != 16)
                    throw new InvalidDataException(Damaged(packet, packets));
                if (samples == null)
                {
                    channels = buffer.Channels;
                    rate = buffer.SampleRate;
                    if (channels > 2)
                        throw new NotSupportedException("The audio has " + channels + " channels. Only mono and stereo are supported.");
                    samples = new short[2 * Math.Max(1024, (long)packets * (data.Length / (2 * channels)))];
                }
                else if (buffer.Channels != channels || buffer.SampleRate != rate)
                {
                    throw new InvalidDataException(Damaged(packet, packets) + " It changes its sample rate or its channels part of the way through.");
                }

                int frames = data.Length / (2 * channels);
                if (2L * (done + frames) > samples.Length)
                    Array.Resize(ref samples, checked(Math.Max(2 * samples.Length, 2 * (done + frames))));
                for (int frame = 0; frame < frames; frame++)
                {
                    // Two bytes to a sample, the low one first. A mono track goes to both sides.
                    int at = 2 * channels * frame;
                    short left = (short)(data[at] | data[at + 1] << 8);
                    samples[2 * (done + frame)] = left;
                    samples[2 * (done + frame) + 1] = channels == 2 ? (short)(data[at + 2] | data[at + 3] << 8) : left;
                }
                done += frames;
                if (progress != null)
                    progress.Report((float)(packet + 1) / packets);
            }
            if (done == 0)
                throw new InvalidDataException("The track holds no audio.");

            long first, length;
            track.Sound(rate, out first, out length);
            first = Math.Min(first, done);
            length = length < 0 ? done - first : Math.Min(length, done - first);
            var sound = new short[2 * length];
            Array.Copy(samples, 2 * first, sound, 0, sound.Length);
            return new WavData
            {
                SampleRate = rate,
                Samples = sound,
                SourceChannels = channels,
                SourceBitsPerSample = 16
            };
        }

        static string Damaged(int packet, int packets)
        {
            return "The AAC audio is damaged: packet " + (packet + 1) + " of " + packets + " could not be decoded.";
        }
    }
}
