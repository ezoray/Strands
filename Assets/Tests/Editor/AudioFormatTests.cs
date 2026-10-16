using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Strands.Audio;

namespace Strands.Tests
{
    /// <summary>Reading the audio formats other than WAV.</summary>
    public class AudioFormatTests
    {
        const int Rate = 44100;

        /// <summary>
        /// An AIFF or AIFF-C file built by hand. A compression type makes it AIFF-C. The chunk given
        /// as filler goes between the format and the sound, or the format goes last.
        /// </summary>
        static byte[] Aiff(int channels, int bits, int sampleRate, byte[] sound, string compression = null,
            bool formatLast = false, int soundOffset = 0, byte[] filler = null)
        {
            var common = new List<byte>();
            common.AddRange(BigEndian(channels, 2));
            common.AddRange(BigEndian(sound.Length / (channels * ((bits + 7) / 8)), 4));
            common.AddRange(BigEndian(bits, 2));
            common.AddRange(Extended(sampleRate));
            if (compression != null)
            {
                common.AddRange(Encoding.ASCII.GetBytes(compression));
                common.AddRange(new byte[] { 0, 0 });   // an empty name, padded to an even length
            }

            var data = new List<byte>();
            data.AddRange(BigEndian(soundOffset, 4));
            data.AddRange(BigEndian(0, 4));
            data.AddRange(new byte[soundOffset]);
            data.AddRange(sound);

            var chunks = new List<byte>();
            if (!formatLast)
                chunks.AddRange(Chunk("COMM", common.ToArray()));
            if (filler != null)
                chunks.AddRange(Chunk("ANNO", filler));
            chunks.AddRange(Chunk("SSND", data.ToArray()));
            if (formatLast)
                chunks.AddRange(Chunk("COMM", common.ToArray()));

            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("FORM"));
            file.AddRange(BigEndian(4 + chunks.Count, 4));
            file.AddRange(Encoding.ASCII.GetBytes(compression != null ? "AIFC" : "AIFF"));
            file.AddRange(chunks);
            return file.ToArray();
        }

        static IEnumerable<byte> Chunk(string id, byte[] body)
        {
            var chunk = new List<byte>();
            chunk.AddRange(Encoding.ASCII.GetBytes(id));
            chunk.AddRange(BigEndian(body.Length, 4));
            chunk.AddRange(body);
            if (body.Length % 2 == 1)
                chunk.Add(0);
            return chunk;
        }

        static byte[] BigEndian(long value, int bytes)
        {
            var result = new byte[bytes];
            for (int i = 0; i < bytes; i++)
                result[i] = (byte)(value >> (8 * (bytes - 1 - i)));
            return result;
        }

        /// <summary>A whole number as the 80-bit float AIFF keeps its sample rate in.</summary>
        static byte[] Extended(int value)
        {
            int top = 31;
            while ((value >> top) == 0)
                top--;
            var result = new byte[10];
            BigEndian(16383 + top, 2).CopyTo(result, 0);
            BigEndian((long)value << (63 - top), 8).CopyTo(result, 2);
            return result;
        }

        [Test]
        public void Aiff_FixturesHoldTheSameSamplesAsTheirWav()
        {
            string[] paths = Fixtures.AudioPaths("aif", "aiff", "aifc").ToArray();
            Assert.That(paths, Is.Not.Empty);
            foreach (string path in paths)
            {
                WavData expected = WavReader.Read(Fixtures.ReferenceWav(path));
                WavData read = AiffReader.Read(path);
                string name = Path.GetFileName(path);
                Assert.That(read.SampleRate, Is.EqualTo(expected.SampleRate), name);
                Assert.That(read.SourceChannels, Is.EqualTo(expected.SourceChannels), name);
                Assert.That(read.SourceBitsPerSample, Is.EqualTo(expected.SourceBitsPerSample), name);
                Assert.That(read.Frames, Is.GreaterThan(0), name);
                CollectionAssert.AreEqual(expected.Samples, read.Samples, name);
            }
        }

        [Test]
        public void Aiff_16BitStereoIsBigEndian()
        {
            byte[] file = Aiff(2, 16, Rate, new byte[] { 0x01, 0x23, 0xFF, 0xFE, 0x80, 0x00, 0x7F, 0xFF });
            WavData read = AiffReader.Read(file);
            Assert.That(read.SampleRate, Is.EqualTo(Rate));
            Assert.That(read.SourceChannels, Is.EqualTo(2));
            Assert.That(read.SourceBitsPerSample, Is.EqualTo(16));
            Assert.That(read.Samples, Is.EqualTo(new short[] { 0x0123, -2, short.MinValue, short.MaxValue }));
        }

        [Test]
        public void Aiff_24BitIsRoundedTo16BitAndMonoIsDoubled()
        {
            // 0x012380 rounds up to 0x0124; the largest value must not wrap round when rounded.
            byte[] file = Aiff(1, 24, Rate, new byte[] { 0x01, 0x23, 0x80, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F });
            WavData read = AiffReader.Read(file);
            Assert.That(read.SourceBitsPerSample, Is.EqualTo(24));
            Assert.That(read.Samples, Is.EqualTo(new short[] { 0x0124, 0x0124, short.MaxValue, short.MaxValue, -1, -1 }));
        }

        [Test]
        public void Aiff_SowtIsLittleEndian()
        {
            WavData sixteen = AiffReader.Read(Aiff(1, 16, Rate, new byte[] { 0x23, 0x01, 0xFE, 0xFF }, "sowt"));
            Assert.That(sixteen.Samples, Is.EqualTo(new short[] { 0x0123, 0x0123, -2, -2 }));

            WavData twentyFour = AiffReader.Read(Aiff(1, 24, Rate, new byte[] { 0x80, 0x23, 0x01 }, "sowt"));
            Assert.That(twentyFour.Samples, Is.EqualTo(new short[] { 0x0124, 0x0124 }));
        }

        [TestCase("NONE")]
        [TestCase("twos")]
        [TestCase("in24")]
        public void Aiff_UncompressedAiffCIsBigEndian(string compression)
        {
            WavData read = AiffReader.Read(Aiff(1, 16, Rate, new byte[] { 0x01, 0x23 }, compression));
            Assert.That(read.Samples, Is.EqualTo(new short[] { 0x0123, 0x0123 }));
        }

        [Test]
        public void Aiff_ChunksAreFoundInAnyOrderAndOddOnesArePadded()
        {
            byte[] sound = { 0x01, 0x23, 0x04, 0x56 };
            var expected = new short[] { 0x0123, 0x0123, 0x0456, 0x0456 };

            // A chunk of odd length before the sound is followed by one byte of padding.
            Assert.That(AiffReader.Read(Aiff(1, 16, Rate, sound, filler: new byte[] { 1, 2, 3 })).Samples, Is.EqualTo(expected));
            Assert.That(AiffReader.Read(Aiff(1, 16, Rate, sound, formatLast: true)).Samples, Is.EqualTo(expected));
            // The sound chunk can hold unused bytes in front of the first sample.
            Assert.That(AiffReader.Read(Aiff(1, 16, Rate, sound, soundOffset: 6)).Samples, Is.EqualTo(expected));
        }

        [Test]
        public void Aiff_ReadsNoFurtherThanTheFileOrTheFrameCount()
        {
            byte[] file = Aiff(1, 16, Rate, new byte[] { 0x01, 0x23, 0x04, 0x56, 0x07, 0x89 });
            // Cut off in the middle of the last sample: two whole frames are left.
            byte[] cut = file.Take(file.Length - 1).ToArray();
            Assert.That(AiffReader.Read(cut).Samples, Is.EqualTo(new short[] { 0x0123, 0x0123, 0x0456, 0x0456 }));

            // The format chunk says two frames (offset 22 is the low byte of the count); the third is ignored.
            file[22 + 3] = 2;
            Assert.That(AiffReader.Read(file).Frames, Is.EqualTo(2));
        }

        [TestCase(44100)]
        [TestCase(48000)]
        [TestCase(22050)]
        [TestCase(96000)]
        [TestCase(88200)]
        public void Aiff_SampleRateIsReadFromItsExtendedFloat(int rate)
        {
            Assert.That(AiffReader.Read(Aiff(1, 16, rate, new byte[2])).SampleRate, Is.EqualTo(rate));
        }

        [Test]
        public void Aiff_SampleRateBytesMatchTheWellKnownEncoding()
        {
            // 44100 as every AIFF writer stores it, so the helper above is not checking itself.
            Assert.That(Extended(44100), Is.EqualTo(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0 }));
        }

        [Test]
        public void Aiff_FilesThatCannotBeUsedAreRefusedWithAReason()
        {
            Assert.That(() => AiffReader.Read(Aiff(2, 16, Rate, new byte[8], "ima4")),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("ima4"));
            Assert.That(() => AiffReader.Read(Aiff(1, 32, Rate, new byte[4], "fl32")),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("fl32"));
            Assert.That(() => AiffReader.Read(Aiff(1, 8, Rate, new byte[4])),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("8-bit"));
            Assert.That(() => AiffReader.Read(Aiff(6, 16, Rate, new byte[12])),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("6 channels"));
            Assert.That(() => AiffReader.Read(Encoding.ASCII.GetBytes("FORM\0\0\0\u0004AIFF")),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(() => AiffReader.Read(WavWriter.Write(new short[4], Rate)),
                Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void AudioFile_TellsTheFormatFromTheContent()
        {
            var samples = new short[] { 0x0123, 0x0123, -2, -2 };
            Assert.That(AudioFile.Read(WavWriter.Write(samples, Rate)).Samples, Is.EqualTo(samples));
            Assert.That(AudioFile.Read(Aiff(1, 16, Rate, new byte[] { 0x01, 0x23, 0xFF, 0xFE })).Samples, Is.EqualTo(samples));
            Assert.That(() => AudioFile.Read(new byte[64]), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void AudioFile_ReadsEveryAudioFixture()
        {
            // A fixture at another rate comes back at 44.1 kHz; for the others that is no change.
            foreach (string path in Fixtures.AudioPaths("wav", "aif", "aiff", "aifc", "flac"))
                CollectionAssert.AreEqual(Resampler.ToTargetRate(WavReader.Read(Fixtures.ReferenceWav(path))).Samples, AudioFile.Read(path).Samples, Path.GetFileName(path));
        }

        [Test]
        public void AudioFile_BringsAFileAtAnotherRateTo441()
        {
            // The readers give a file at its own rate; it is AudioFile that converts.
            byte[] file = Fixtures.Read("audio/tone48.wav");
            WavData asItIs = WavReader.Read(file);
            Assert.That(asItIs.SampleRate, Is.EqualTo(48000));
            Assert.That(asItIs.SourceSampleRate, Is.EqualTo(0));

            WavData read = AudioFile.Read(file);
            Assert.That(read.SampleRate, Is.EqualTo(Rate));
            Assert.That(read.SourceSampleRate, Is.EqualTo(48000));
            // A fifth of a second at either rate.
            Assert.That(asItIs.Frames, Is.EqualTo(9600));
            Assert.That(read.Frames, Is.EqualTo(8820));

            // A file already at 44.1 kHz is not marked as converted.
            Assert.That(AudioFile.Read(Fixtures.Read("audio/tone16.wav")).SourceSampleRate, Is.EqualTo(0));
            // A rate that cannot be converted is refused, and the message says which.
            Assert.That(() => AudioFile.Read(WavWriter.Write(new short[400], 47999)),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("47999"));
        }
    }
}
