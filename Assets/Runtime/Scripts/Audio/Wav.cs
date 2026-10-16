using System;
using System.IO;

namespace Strands.Audio
{
    /// <summary>Audio held as interleaved 16-bit stereo, the form the SP-16 loads.</summary>
    public sealed class WavData
    {
        public int SampleRate;
        /// <summary>Left and right samples alternating; length is Frames * 2.</summary>
        public short[] Samples;
        public int SourceChannels;
        public int SourceBitsPerSample;
        /// <summary>The rate the file was at when its audio was converted to this one (<see cref="Resampler"/>); 0 when it was not.</summary>
        public int SourceSampleRate;

        public int Frames { get { return Samples.Length / 2; } }
        public double Seconds { get { return (double)Frames / SampleRate; } }
    }

    /// <summary>Brings wider samples down to the 16 bits that WavData holds, the same way for every format.</summary>
    static class Pcm
    {
        /// <summary>Rounds to the nearest 16-bit value, without dither.</summary>
        public static short From24Bit(int value)
        {
            int rounded = (value + 128) >> 8;
            return (short)Math.Min(short.MaxValue, rounded);
        }
    }

    public static class WavReader
    {
        const int FormatPcm = 1;
        const int FormatExtensible = 0xFFFE;

        public static WavData Read(string path)
        {
            return Read(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Reads 16 or 24-bit PCM, mono or stereo. 24-bit is rounded to 16-bit without dither
        /// and mono is copied to both channels.
        /// </summary>
        public static WavData Read(byte[] file)
        {
            if (file.Length < 12 || Tag(file, 0) != "RIFF" || Tag(file, 8) != "WAVE")
                throw new InvalidDataException("Not a WAV file.");

            int format = 0, channels = 0, sampleRate = 0, bits = 0, blockAlign = 0;
            int dataOffset = -1, dataLength = 0;
            int offset = 12;
            while (offset + 8 <= file.Length)
            {
                string id = Tag(file, offset);
                long size = BitConverter.ToUInt32(file, offset + 4);
                int body = offset + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(file, body);
                    channels = BitConverter.ToUInt16(file, body + 2);
                    sampleRate = BitConverter.ToInt32(file, body + 4);
                    blockAlign = BitConverter.ToUInt16(file, body + 12);
                    bits = BitConverter.ToUInt16(file, body + 14);
                    // The extensible form keeps the real format in the first two bytes of its sub-format GUID.
                    if (format == FormatExtensible && size >= 26)
                        format = BitConverter.ToUInt16(file, body + 24);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    // Some recorders leave a wrong size in the header; never read past the file.
                    dataLength = (int)Math.Min(size, file.Length - body);
                    break;
                }
                offset = body + (int)size + (int)(size & 1);
            }

            if (channels == 0)
                throw new InvalidDataException("WAV file has no format chunk.");
            if (dataOffset < 0)
                throw new InvalidDataException("WAV file has no audio data.");
            if (format != FormatPcm)
                throw new NotSupportedException("Only PCM WAV files are supported (this one is format " + format + ").");
            if (channels != 1 && channels != 2)
                throw new NotSupportedException("Only mono and stereo WAV files are supported (this one has " + channels + " channels).");
            if (bits != 16 && bits != 24)
                throw new NotSupportedException("Only 16-bit and 24-bit WAV files are supported (this one is " + bits + "-bit).");

            int bytesPerSample = bits / 8;
            int frames = dataLength / blockAlign;
            var samples = new short[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                int position = dataOffset + frame * blockAlign;
                short left = ReadSample(file, position, bytesPerSample);
                short right = channels == 2 ? ReadSample(file, position + bytesPerSample, bytesPerSample) : left;
                samples[frame * 2] = left;
                samples[frame * 2 + 1] = right;
            }

            return new WavData
            {
                SampleRate = sampleRate,
                Samples = samples,
                SourceChannels = channels,
                SourceBitsPerSample = bits
            };
        }

        internal static short ReadSample(byte[] file, int offset, int bytesPerSample)
        {
            if (bytesPerSample == 2)
                return (short)(file[offset] | (file[offset + 1] << 8));
            return Pcm.From24Bit(file[offset] | (file[offset + 1] << 8) | ((sbyte)file[offset + 2] << 16));
        }

        static string Tag(byte[] file, int offset)
        {
            return new string(new[] { (char)file[offset], (char)file[offset + 1], (char)file[offset + 2], (char)file[offset + 3] });
        }
    }

    public static class WavWriter
    {
        /// <summary>A 16-bit stereo PCM file with the plain 44-byte header.</summary>
        public static byte[] Write(short[] interleavedStereo, int sampleRate)
        {
            return Write(interleavedStereo, sampleRate, 2);
        }

        /// <summary>
        /// The same with one channel. The SP-16 loads a mono file from a package and plays it
        /// (Confirmed, SP-16, 2026-10-10).
        /// </summary>
        public static byte[] WriteMono(short[] samples, int sampleRate)
        {
            return Write(samples, sampleRate, 1);
        }

        static byte[] Write(short[] samples, int sampleRate, int channels)
        {
            int dataLength = samples.Length * 2;
            var file = new byte[44 + dataLength];
            PutTag(file, 0, "RIFF");
            PutInt32(file, 4, 36 + dataLength);
            PutTag(file, 8, "WAVE");
            PutTag(file, 12, "fmt ");
            PutInt32(file, 16, 16);
            PutInt16(file, 20, 1);                           // PCM
            PutInt16(file, 22, channels);
            PutInt32(file, 24, sampleRate);
            PutInt32(file, 28, sampleRate * 2 * channels);   // byte rate
            PutInt16(file, 32, 2 * channels);                // block align
            PutInt16(file, 34, 16);                          // bits per sample
            PutTag(file, 36, "data");
            PutInt32(file, 40, dataLength);
            Buffer.BlockCopy(samples, 0, file, 44, dataLength);
            if (!BitConverter.IsLittleEndian)
                for (int i = 44; i < file.Length; i += 2)
                    Array.Reverse(file, i, 2);
            return file;
        }

        static void PutTag(byte[] file, int offset, string tag)
        {
            for (int i = 0; i < 4; i++)
                file[offset + i] = (byte)tag[i];
        }

        static void PutInt16(byte[] file, int offset, int value)
        {
            file[offset] = (byte)value;
            file[offset + 1] = (byte)(value >> 8);
        }

        static void PutInt32(byte[] file, int offset, int value)
        {
            file[offset] = (byte)value;
            file[offset + 1] = (byte)(value >> 8);
            file[offset + 2] = (byte)(value >> 16);
            file[offset + 3] = (byte)(value >> 24);
        }
    }
}
