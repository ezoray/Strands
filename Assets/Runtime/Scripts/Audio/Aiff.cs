using System;
using System.IO;

namespace Strands.Audio
{
    public static class AiffReader
    {
        public static WavData Read(string path)
        {
            return Read(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Reads 16 or 24-bit PCM, mono or stereo, from AIFF and from AIFF-C files that are not
        /// compressed. 24-bit is rounded to 16-bit without dither and mono is copied to both
        /// channels, as for WAV.
        /// </summary>
        public static WavData Read(byte[] file)
        {
            if (file.Length < 12 || Tag(file, 0) != "FORM" || (Tag(file, 8) != "AIFF" && Tag(file, 8) != "AIFC"))
                throw new InvalidDataException("Not an AIFF file.");
            bool aifc = Tag(file, 8) == "AIFC";

            int channels = 0, bits = 0;
            long declaredFrames = 0;
            double sampleRate = 0;
            // AIFF-C names how the samples are stored; plain AIFF is always big-endian PCM.
            string compression = "NONE";
            long dataOffset = -1, dataLength = 0;
            long offset = 12;
            // The chunks may come in any order, so the whole file is walked.
            while (offset + 8 <= file.Length)
            {
                string id = Tag(file, (int)offset);
                long size = UInt32(file, (int)offset + 4);
                long body = offset + 8;
                if (id == "COMM" && size >= 18 && body + 18 <= file.Length)
                {
                    channels = Int16(file, (int)body);
                    declaredFrames = UInt32(file, (int)body + 2);
                    bits = Int16(file, (int)body + 6);
                    sampleRate = Extended(file, (int)body + 8);
                    if (aifc && size >= 22 && body + 22 <= file.Length)
                        compression = Tag(file, (int)body + 18);
                }
                else if (id == "SSND" && size >= 8 && body + 8 <= file.Length)
                {
                    // The samples start after an offset that is nearly always zero.
                    long skip = UInt32(file, (int)body);
                    dataOffset = body + 8 + skip;
                    // Never read past the file, whatever the header says.
                    dataLength = Math.Min(size - 8 - skip, file.Length - dataOffset);
                }
                offset = body + size + (size & 1);
            }

            if (channels == 0)
                throw new InvalidDataException("AIFF file has no format chunk.");
            if (dataOffset < 0 || dataLength < 0)
                throw new InvalidDataException("AIFF file has no audio data.");
            bool littleEndian = compression == "sowt";
            if (!littleEndian && compression != "NONE" && compression != "twos" && compression != "in24")
                throw new NotSupportedException("Only uncompressed AIFF files are supported (this one is '" + compression + "').");
            if (channels != 1 && channels != 2)
                throw new NotSupportedException("Only mono and stereo AIFF files are supported (this one has " + channels + " channels).");
            if (bits != 16 && bits != 24)
                throw new NotSupportedException("Only 16-bit and 24-bit AIFF files are supported (this one is " + bits + "-bit).");

            int bytesPerSample = bits / 8;
            int blockAlign = channels * bytesPerSample;
            int frames = (int)Math.Min(declaredFrames, dataLength / blockAlign);
            var samples = new short[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                int position = (int)dataOffset + frame * blockAlign;
                short left = ReadSample(file, position, bytesPerSample, littleEndian);
                short right = channels == 2 ? ReadSample(file, position + bytesPerSample, bytesPerSample, littleEndian) : left;
                samples[frame * 2] = left;
                samples[frame * 2 + 1] = right;
            }

            return new WavData
            {
                SampleRate = (int)Math.Round(sampleRate),
                Samples = samples,
                SourceChannels = channels,
                SourceBitsPerSample = bits
            };
        }

        static short ReadSample(byte[] file, int offset, int bytesPerSample, bool littleEndian)
        {
            if (littleEndian)
                return WavReader.ReadSample(file, offset, bytesPerSample);
            if (bytesPerSample == 2)
                return (short)((file[offset] << 8) | file[offset + 1]);
            return Pcm.From24Bit(((sbyte)file[offset] << 16) | (file[offset + 1] << 8) | file[offset + 2]);
        }

        static int Int16(byte[] file, int offset)
        {
            return (short)((file[offset] << 8) | file[offset + 1]);
        }

        static long UInt32(byte[] file, int offset)
        {
            return ((long)file[offset] << 24) | ((long)file[offset + 1] << 16) | ((long)file[offset + 2] << 8) | file[offset + 3];
        }

        /// <summary>The 80-bit float the sample rate is stored in: sign and exponent, then a 64-bit mantissa.</summary>
        static double Extended(byte[] file, int offset)
        {
            int exponent = ((file[offset] & 0x7F) << 8) | file[offset + 1];
            ulong mantissa = 0;
            for (int i = 0; i < 8; i++)
                mantissa = (mantissa << 8) | file[offset + 2 + i];
            if (mantissa == 0)
                return 0;
            double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
            return (file[offset] & 0x80) != 0 ? -value : value;
        }

        static string Tag(byte[] file, int offset)
        {
            return new string(new[] { (char)file[offset], (char)file[offset + 1], (char)file[offset + 2], (char)file[offset + 3] });
        }
    }
}
