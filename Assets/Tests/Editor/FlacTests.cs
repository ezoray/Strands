using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Strands.Audio;

namespace Strands.Tests
{
    /// <summary>
    /// The FLAC decoder, against files from another encoder (Fixtures/audio) and against streams
    /// built here bit by bit, which reach the parts of the format an encoder may never choose.
    /// Section numbers are those of RFC 9639.
    /// </summary>
    public class FlacTests
    {
        const int Rate = 44100;
        const int Independent = 1, LeftSide = 8, SideRight = 9, MidSide = 10;

        /// <summary>Writes numbers of any width, most significant bit first.</summary>
        internal sealed class Bits
        {
            readonly List<byte> bytes = new List<byte>();
            int current, filled;

            public void Write(long value, int count)
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    current = (current << 1) | (int)((value >> i) & 1);
                    if (++filled == 8)
                    {
                        bytes.Add((byte)current);
                        current = 0;
                        filled = 0;
                    }
                }
            }

            public void Unary(int zeros)
            {
                for (int i = 0; i < zeros; i++)
                    Write(0, 1);
                Write(1, 1);
            }

            public void Rice(int value, int parameter)
            {
                uint folded = (uint)((value << 1) ^ (value >> 31));
                Unary((int)(folded >> parameter));
                Write(folded & ((1u << parameter) - 1), parameter);
            }

            public void Align()
            {
                while (filled != 0)
                    Write(0, 1);
            }

            public byte[] ToArray()
            {
                Assert.That(filled, Is.EqualTo(0), "not on a byte boundary");
                return bytes.ToArray();
            }
        }

        /// <summary>How the residual of a predicted subframe is to be written (section 9.2.7).</summary>
        sealed class Residual
        {
            public int Parameter = 6;
            public int PartitionOrder;
            public bool FiveBitParameters;
            /// <summary>Above zero, partitions are written as plain numbers of this width, not Rice coded.</summary>
            public int EscapeWidth;
        }

        // ---- building a stream ----

        static byte[] Stream(int channels, int bits, long totalSamples, byte[] md5, params byte[][] frames)
        {
            return Stream(channels, bits, Rate, totalSamples, md5, null, frames);
        }

        static byte[] Stream(int channels, int bits, int rate, long totalSamples, byte[] md5, byte[] extraBlock, params byte[][] frames)
        {
            var info = new Bits();
            info.Write(16, 16);             // smallest and largest block
            info.Write(65535, 16);
            info.Write(0, 24);              // smallest and largest frame, unknown
            info.Write(0, 24);
            info.Write(rate, 20);
            info.Write(channels - 1, 3);
            info.Write(bits - 1, 5);
            info.Write(totalSamples, 36);

            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("fLaC"));
            file.Add(0);                    // stream info, with more blocks to follow
            file.AddRange(new byte[] { 0, 0, 34 });
            file.AddRange(info.ToArray());
            file.AddRange(md5 ?? new byte[16]);
            if (extraBlock != null)
            {
                file.Add(4);                // a comment block, which the decoder has no use for
                file.AddRange(new byte[] { 0, (byte)(extraBlock.Length >> 8), (byte)extraBlock.Length });
                file.AddRange(extraBlock);
            }
            file.Add(0x80 | 1);             // padding, the last block
            file.AddRange(new byte[] { 0, 0, 2, 0, 0 });
            foreach (byte[] frame in frames)
                file.AddRange(frame);
            return file.ToArray();
        }

        /// <summary>One frame (section 9.1): the header with its CRC, the subframes, and the CRC of it all.</summary>
        static byte[] Frame(int number, int blockSize, int channelCode, int bits, Action<Bits> subframes, int blockCode = 7)
        {
            var frame = new Bits();
            frame.Write(0x7FFC, 15);        // sync and the reserved bit
            frame.Write(0, 1);              // blocks of a fixed size
            frame.Write(blockCode, 4);
            frame.Write(0, 4);              // sample rate as in the stream info
            frame.Write(channelCode == Independent ? 0 : channelCode == 2 ? 1 : channelCode, 4);
            frame.Write(bits == 16 ? 4 : bits == 24 ? 6 : 0, 3);
            frame.Write(0, 1);
            if (number < 128)
                frame.Write(number, 8);
            else
            {
                // Two bytes, in the manner of UTF-8.
                frame.Write(0xC0 | (number >> 6), 8);
                frame.Write(0x80 | (number & 0x3F), 8);
            }
            if (blockCode == 6)
                frame.Write(blockSize - 1, 8);
            else if (blockCode == 7)
                frame.Write(blockSize - 1, 16);
            frame.Write(Crc(frame.ToArray(), 0x07, 8), 8);
            subframes(frame);
            frame.Align();
            frame.Write(Crc(frame.ToArray(), 0x8005, 16), 16);
            return frame.ToArray();
        }

        /// <summary>A CRC worked out one bit at a time, so that it shares nothing with the decoder's tables.</summary>
        static int Crc(byte[] data, int polynomial, int width)
        {
            int crc = 0, top = 1 << (width - 1), mask = (1 << width) - 1;
            foreach (byte value in data)
                for (int bit = 7; bit >= 0; bit--)
                {
                    bool carry = ((crc & top) != 0) ^ (((value >> bit) & 1) != 0);
                    crc = (crc << 1) & mask;
                    if (carry)
                        crc ^= polynomial;
                }
            return crc;
        }

        static void SubframeHeader(Bits to, int type, int wasted)
        {
            to.Write(0, 1);
            to.Write(type, 6);
            to.Write(wasted > 0 ? 1 : 0, 1);
            if (wasted > 0)
                to.Unary(wasted - 1);
        }

        static void Constant(Bits to, int value, int bits)
        {
            SubframeHeader(to, 0, 0);
            to.Write(value, bits);
        }

        static void Verbatim(Bits to, int[] samples, int bits, int wasted = 0)
        {
            SubframeHeader(to, 1, wasted);
            foreach (int sample in samples)
                to.Write(sample >> wasted, bits - wasted);
        }

        static void Fixed(Bits to, int[] samples, int bits, int order, Residual residual = null, int wasted = 0)
        {
            int[] s = samples.Select(v => v >> wasted).ToArray();
            SubframeHeader(to, 8 + order, wasted);
            for (int i = 0; i < order; i++)
                to.Write(s[i], bits - wasted);
            var differences = new int[s.Length];
            for (int i = order; i < s.Length; i++)
            {
                long prediction = order == 0 ? 0
                    : order == 1 ? s[i - 1]
                    : order == 2 ? 2L * s[i - 1] - s[i - 2]
                    : order == 3 ? 3L * s[i - 1] - 3L * s[i - 2] + s[i - 3]
                    : 4L * s[i - 1] - 6L * s[i - 2] + 4L * s[i - 3] - s[i - 4];
                differences[i] = (int)(s[i] - prediction);
            }
            WriteResidual(to, differences, order, residual ?? new Residual());
        }

        static void Lpc(Bits to, int[] samples, int bits, int[] coefficients, int precision, int shift, Residual residual = null)
        {
            int order = coefficients.Length;
            SubframeHeader(to, 31 + order, 0);
            for (int i = 0; i < order; i++)
                to.Write(samples[i], bits);
            to.Write(precision - 1, 4);
            to.Write(shift, 5);
            foreach (int coefficient in coefficients)
                to.Write(coefficient, precision);
            var differences = new int[samples.Length];
            for (int i = order; i < samples.Length; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++)
                    sum += (long)coefficients[j] * samples[i - 1 - j];
                differences[i] = (int)(samples[i] - (sum >> shift));
            }
            WriteResidual(to, differences, order, residual ?? new Residual());
        }

        static void WriteResidual(Bits to, int[] differences, int order, Residual how)
        {
            int parameterBits = how.FiveBitParameters ? 5 : 4;
            to.Write(how.FiveBitParameters ? 1 : 0, 2);
            to.Write(how.PartitionOrder, 4);
            int partitions = 1 << how.PartitionOrder, perPartition = differences.Length >> how.PartitionOrder;
            int index = order;
            for (int partition = 0; partition < partitions; partition++)
            {
                int count = perPartition - (partition == 0 ? order : 0);
                if (how.EscapeWidth > 0)
                {
                    to.Write((1 << parameterBits) - 1, parameterBits);
                    to.Write(how.EscapeWidth, 5);
                    for (int i = 0; i < count; i++)
                        to.Write(differences[index++], how.EscapeWidth);
                }
                else
                {
                    // Each partition may have its own parameter; vary it so that a decoder cannot reuse the first.
                    int parameter = how.Parameter + partition % 2;
                    to.Write(parameter, parameterBits);
                    for (int i = 0; i < count; i++)
                        to.Rice(differences[index++], parameter);
                }
            }
        }

        // ---- test signals and expectations ----

        /// <summary>A tone with noise on it, different for each seed, that uses most of the range of the given width.</summary>
        static int[] Signal(int count, int bits, int seed)
        {
            var random = new Random(seed);
            double full = (1 << (bits - 1)) - 1;
            var samples = new int[count];
            for (int i = 0; i < count; i++)
                samples[i] = (int)Math.Round(full * (0.7 * Math.Sin(i * (0.05 + 0.01 * seed)) + 0.25 * (random.NextDouble() * 2 - 1)));
            return samples;
        }

        static byte[] Md5(int bits, params int[][] channels)
        {
            var raw = new List<byte>();
            for (int i = 0; i < channels[0].Length; i++)
                foreach (int[] channel in channels)
                    for (int b = 0; b < bits / 8; b++)
                        raw.Add((byte)(channel[i] >> (8 * b)));
            using (MD5 md5 = MD5.Create())
                return md5.ComputeHash(raw.ToArray());
        }

        /// <summary>What the reader should give for these channels: 16-bit, interleaved, mono on both sides.</summary>
        static short[] Expected(int bits, int[] left, int[] right)
        {
            var samples = new short[left.Length * 2];
            for (int i = 0; i < left.Length; i++)
            {
                samples[2 * i] = To16(left[i], bits);
                samples[2 * i + 1] = To16(right[i], bits);
            }
            return samples;
        }

        static short To16(int value, int bits)
        {
            return bits == 16 ? (short)value : (short)Math.Min(short.MaxValue, (value + 128) >> 8);
        }

        /// <summary>A mono 16-bit stream of one frame, checked against its samples and its MD5.</summary>
        static void AssertMonoRoundTrip(int[] samples, Action<Bits> subframe, int bits = 16)
        {
            byte[] file = Stream(1, bits, samples.Length, Md5(bits, samples), Frame(0, samples.Length, Independent, bits, subframe));
            WavData read = FlacReader.Read(file);
            Assert.That(read.SampleRate, Is.EqualTo(Rate));
            Assert.That(read.SourceChannels, Is.EqualTo(1));
            Assert.That(read.SourceBitsPerSample, Is.EqualTo(bits));
            CollectionAssert.AreEqual(Expected(bits, samples, samples), read.Samples);
        }

        // ---- files from another encoder ----

        [Test]
        public void Flac_FixturesHoldTheSameSamplesAsTheirWav()
        {
            string[] paths = Fixtures.AudioPaths("flac").ToArray();
            Assert.That(paths, Is.Not.Empty);
            foreach (string path in paths)
            {
                WavData expected = WavReader.Read(Fixtures.ReferenceWav(path));
                WavData read = FlacReader.Read(path);
                string name = Path.GetFileName(path);
                Assert.That(read.SampleRate, Is.EqualTo(expected.SampleRate), name);
                Assert.That(read.SourceChannels, Is.EqualTo(expected.SourceChannels), name);
                Assert.That(read.SourceBitsPerSample, Is.EqualTo(expected.SourceBitsPerSample), name);
                Assert.That(read.Frames, Is.GreaterThan(0), name);
                CollectionAssert.AreEqual(expected.Samples, read.Samples, name);
            }
        }

        [Test]
        public void Flac_FixturesCarryAnMd5SoTheirDecodingIsChecked()
        {
            // Without one, the test above would compare the audio but the decoder's own check would do nothing.
            foreach (string path in Fixtures.AudioPaths("flac"))
                Assert.That(File.ReadAllBytes(path).Skip(8 + 18).Take(16).Any(b => b != 0), Path.GetFileName(path));
        }

        // ---- subframes ----

        [Test]
        public void Flac_S9_2_ConstantSubframeRepeatsOneValue()
        {
            int[] samples = Enumerable.Repeat(-12345, 300).ToArray();
            AssertMonoRoundTrip(samples, to => Constant(to, -12345, 16));
        }

        [Test]
        public void Flac_S9_2_VerbatimSubframeHoldsPlainSamples()
        {
            int[] samples = Signal(300, 16, 1);
            samples[0] = short.MinValue;
            samples[1] = short.MaxValue;
            AssertMonoRoundTrip(samples, to => Verbatim(to, samples, 16));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void Flac_S9_2_FixedPredictorOfEachOrder(int order)
        {
            int[] samples = Signal(1024, 16, 2 + order);
            AssertMonoRoundTrip(samples, to => Fixed(to, samples, 16, order, new Residual { Parameter = 12 }));
        }

        [Test]
        public void Flac_S9_2_LinearPredictorUsesItsCoefficientsNewestSampleFirst()
        {
            int[] samples = Signal(1024, 16, 9);
            // Unequal coefficients, one of them negative, so that their order and sign both matter.
            AssertMonoRoundTrip(samples, to => Lpc(to, samples, 16, new[] { 1843, -922, 131 }, 12, 10, new Residual { Parameter = 11 }));
            AssertMonoRoundTrip(samples, to => Lpc(to, samples, 16, new[] { 16383 }, 15, 14, new Residual { Parameter = 11 }));
        }

        [Test]
        public void Flac_S9_2_WastedBitsArePutBack()
        {
            int[] samples = Signal(256, 16, 3).Select(v => v & ~7).ToArray();
            AssertMonoRoundTrip(samples, to => Verbatim(to, samples, 16, wasted: 3));
            AssertMonoRoundTrip(samples, to => Fixed(to, samples, 16, 2, new Residual { Parameter = 9 }, wasted: 3));
            AssertMonoRoundTrip(samples, to => Verbatim(to, samples, 16, wasted: 1));
        }

        // ---- residual ----

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(5)]
        public void Flac_S9_2_7_ResidualPartitionsEachHaveTheirOwnParameter(int partitionOrder)
        {
            int[] samples = Signal(1024, 16, 4);
            AssertMonoRoundTrip(samples, to => Fixed(to, samples, 16, 2, new Residual { Parameter = 10, PartitionOrder = partitionOrder }));
        }

        [Test]
        public void Flac_S9_2_7_FiveBitRiceParameters()
        {
            int[] samples = Signal(512, 16, 5);
            // A parameter of 15 is the escape code when parameters have four bits, and a plain one when they have five.
            AssertMonoRoundTrip(samples, to => Fixed(to, samples, 16, 1, new Residual { Parameter = 15, FiveBitParameters = true }));
            AssertMonoRoundTrip(samples, to => Fixed(to, samples, 16, 1, new Residual { Parameter = 16, FiveBitParameters = true, PartitionOrder = 2 }));
        }

        [Test]
        public void Flac_S9_2_7_EscapedPartitionsHoldPlainNumbers()
        {
            int[] samples = Signal(512, 16, 6);
            AssertMonoRoundTrip(samples, to => Fixed(to, samples, 16, 1, new Residual { EscapeWidth = 18 }));
            AssertMonoRoundTrip(samples, to => Fixed(to, samples, 16, 1, new Residual { EscapeWidth = 18, FiveBitParameters = true, PartitionOrder = 3 }));
        }

        [Test]
        public void Flac_S9_2_7_ZeroRiceParameterAndLongRuns()
        {
            // A slow ramp predicted exactly leaves a residual of zeros; steps leave values written in unary alone.
            int[] ramp = Enumerable.Range(0, 400).Select(i => 3 * i - 500).ToArray();
            AssertMonoRoundTrip(ramp, to => Fixed(to, ramp, 16, 2, new Residual { Parameter = 0 }));
            int[] steps = Enumerable.Range(0, 400).Select(i => (i / 50) % 2 == 0 ? 90 : -75).ToArray();
            AssertMonoRoundTrip(steps, to => Fixed(to, steps, 16, 1, new Residual { Parameter = 0 }));
        }

        // ---- channels ----

        [TestCase(Independent)]
        [TestCase(LeftSide)]
        [TestCase(SideRight)]
        [TestCase(MidSide)]
        public void Flac_S9_1_3_StereoIsRebuiltFromEachChannelLayout(int layout)
        {
            foreach (int bits in new[] { 16, 24 })
            {
                int[] left = Signal(500, bits, 7), right = Signal(500, bits, 8);
                // The extremes in opposite directions give the largest difference, which needs the extra bit.
                left[0] = -(1 << (bits - 1));
                right[0] = (1 << (bits - 1)) - 1;
                left[1] = (1 << (bits - 1)) - 1;
                right[1] = -(1 << (bits - 1));
                int[] side = left.Zip(right, (l, r) => l - r).ToArray();
                int[] mid = left.Zip(right, (l, r) => (l + r) >> 1).ToArray();

                byte[] frame = Frame(0, left.Length, layout == Independent ? 2 : layout, bits, to =>
                {
                    if (layout == Independent) { Verbatim(to, left, bits); Verbatim(to, right, bits); }
                    else if (layout == LeftSide) { Verbatim(to, left, bits); Verbatim(to, side, bits + 1); }
                    else if (layout == SideRight) { Verbatim(to, side, bits + 1); Verbatim(to, right, bits); }
                    else { Verbatim(to, mid, bits); Verbatim(to, side, bits + 1); }
                });
                WavData read = FlacReader.Read(Stream(2, bits, left.Length, Md5(bits, left, right), frame));
                Assert.That(read.SourceChannels, Is.EqualTo(2));
                CollectionAssert.AreEqual(Expected(bits, left, right), read.Samples, bits + "-bit");
            }
        }

        [Test]
        public void Flac_24BitIsRoundedTo16BitAndMonoIsDoubled()
        {
            int[] samples = { 0x012380, 0x7FFFFF, -129, -0x800000 };
            samples = samples.Concat(Signal(60, 24, 10)).ToArray();
            byte[] file = Stream(1, 24, samples.Length, Md5(24, samples), Frame(0, samples.Length, Independent, 24, to => Verbatim(to, samples, 24)));
            WavData read = FlacReader.Read(file);
            Assert.That(read.Samples.Take(8), Is.EqualTo(new short[] { 0x0124, 0x0124, short.MaxValue, short.MaxValue, -1, -1, short.MinValue, short.MinValue }));
            CollectionAssert.AreEqual(Expected(24, samples, samples), read.Samples);
        }

        // ---- frames and the stream ----

        [Test]
        public void Flac_S9_1_FramesFollowOneAnotherWithEachWayOfStatingTheBlockSize()
        {
            // Codes 1 to 5 and 8 to 15 name a size; 6 and 7 are followed by the size itself.
            var sizes = new Dictionary<int, int> { { 1, 192 }, { 2, 576 }, { 3, 1152 }, { 6, 200 }, { 7, 300 }, { 8, 256 }, { 9, 512 }, { 12, 4096 } };
            var all = new List<int>();
            var frames = new List<byte[]>();
            // More than 127 frames would need a longer frame number, so add small ones up to 130.
            int number = 0;
            foreach (KeyValuePair<int, int> size in sizes)
            {
                int[] samples = Signal(size.Value, 16, 20 + size.Key);
                all.AddRange(samples);
                frames.Add(Frame(number++, size.Value, Independent, 16, to => Fixed(to, samples, 16, 2, new Residual { Parameter = 11 }), size.Key));
            }
            while (number < 130)
            {
                int[] samples = Signal(16, 16, number);
                all.AddRange(samples);
                frames.Add(Frame(number++, 16, Independent, 16, to => Verbatim(to, samples, 16), 6));
            }

            int[] whole = all.ToArray();
            WavData read = FlacReader.Read(Stream(1, 16, whole.Length, Md5(16, whole), frames.ToArray()));
            CollectionAssert.AreEqual(Expected(16, whole, whole), read.Samples);
        }

        [Test]
        public void Flac_S8_OtherMetadataAndATagInFrontAreSkipped()
        {
            int[] samples = Signal(128, 16, 11);
            byte[] frame = Frame(0, samples.Length, Independent, 16, to => Verbatim(to, samples, 16));
            byte[] flac = Stream(1, 16, Rate, samples.Length, Md5(16, samples), Encoding.ASCII.GetBytes("a comment of odd length"), frame);
            CollectionAssert.AreEqual(Expected(16, samples, samples), FlacReader.Read(flac).Samples);

            // An ID3v2 tag: ten bytes of header, its size in four 7-bit bytes, then the body.
            var tagged = new List<byte>(Encoding.ASCII.GetBytes("ID3"));
            tagged.AddRange(new byte[] { 4, 0, 0, 0, 0, 1, 2 });
            tagged.AddRange(new byte[130]);
            tagged.AddRange(flac);
            Assert.That(FlacReader.IsFlac(tagged.ToArray()));
            CollectionAssert.AreEqual(Expected(16, samples, samples), FlacReader.Read(tagged.ToArray()).Samples);
            CollectionAssert.AreEqual(Expected(16, samples, samples), AudioFile.Read(tagged.ToArray()).Samples);
        }

        [Test]
        public void Flac_S8_2_UnknownLengthIsReadToTheEndOfTheFrames()
        {
            int[] first = Signal(256, 16, 12), second = Signal(100, 16, 13);
            int[] whole = first.Concat(second).ToArray();
            var file = new List<byte>(Stream(1, 16, 0, Md5(16, whole),
                Frame(0, 256, Independent, 16, to => Verbatim(to, first, 16)),
                Frame(1, 100, Independent, 16, to => Verbatim(to, second, 16))));
            // A tag at the end of the file, as some players add, is not a frame.
            file.AddRange(Encoding.ASCII.GetBytes("TAG"));
            file.AddRange(new byte[125]);
            CollectionAssert.AreEqual(Expected(16, whole, whole), FlacReader.Read(file.ToArray()).Samples);
        }

        [Test]
        public void Flac_SampleRateComesFromTheStreamInfo()
        {
            int[] samples = Signal(64, 16, 14);
            byte[] frame = Frame(0, 64, Independent, 16, to => Verbatim(to, samples, 16));
            foreach (int rate in new[] { 44100, 48000, 96000, 22050 })
                Assert.That(FlacReader.Read(Stream(1, 16, rate, 64, null, null, frame)).SampleRate, Is.EqualTo(rate));
        }

        // ---- damage ----

        [Test]
        public void Flac_S8_2_AudioThatDoesNotMatchItsMd5IsRefused()
        {
            int[] samples = Signal(128, 16, 15);
            byte[] frame = Frame(0, 128, Independent, 16, to => Verbatim(to, samples, 16));
            byte[] wrong = Md5(16, samples);
            wrong[5] ^= 0x10;
            Assert.That(() => FlacReader.Read(Stream(1, 16, 128, wrong, frame)),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("checksum"));
            // With no MD5 stored there is nothing to compare, and the file is read.
            Assert.That(FlacReader.Read(Stream(1, 16, 128, null, frame)).Frames, Is.EqualTo(128));
        }

        [Test]
        public void Flac_S9_3_AChangedByteAnywhereInAFrameIsNoticed()
        {
            int[] samples = Signal(128, 16, 16);
            byte[] frame = Frame(0, 128, Independent, 16, to => Verbatim(to, samples, 16));
            byte[] good = Stream(1, 16, 128, null, frame);
            int frameStart = good.Length - frame.Length;
            Assert.That(FlacReader.Read(good).Frames, Is.EqualTo(128));

            // No MD5 here, so only the frame's own CRCs can catch it: one in the header, one in the audio.
            foreach (int at in new[] { frameStart + 3, frameStart + 40, good.Length - 1 })
            {
                byte[] bad = (byte[])good.Clone();
                bad[at] ^= 0x04;
                Assert.That(() => FlacReader.Read(bad), Throws.TypeOf<InvalidDataException>(), "byte " + (at - frameStart) + " of the frame");
            }
        }

        [Test]
        public void Flac_AFileCutShortIsRefused()
        {
            int[] samples = Signal(256, 16, 17);
            byte[] frame = Frame(0, 128, Independent, 16, to => Verbatim(to, samples.Take(128).ToArray(), 16));
            byte[] second = Frame(1, 128, Independent, 16, to => Verbatim(to, samples.Skip(128).ToArray(), 16));
            byte[] whole = Stream(1, 16, 256, null, frame, second);
            Assert.That(FlacReader.Read(whole).Frames, Is.EqualTo(256));

            // Cut inside the second frame, and cut exactly between the two.
            Assert.That(() => FlacReader.Read(whole.Take(whole.Length - 50).ToArray()),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("cut short"));
            Assert.That(() => FlacReader.Read(whole.Take(whole.Length - second.Length).ToArray()),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("128 of 256"));
        }

        [Test]
        public void Flac_FilesThatCannotBeUsedAreRefusedWithAReason()
        {
            Assert.That(() => FlacReader.Read(Stream(1, 8, 16, null)),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("8-bit"));
            Assert.That(() => FlacReader.Read(Stream(1, 20, 16, null)),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("20-bit"));
            Assert.That(() => FlacReader.Read(Stream(6, 16, 16, null)),
                Throws.TypeOf<NotSupportedException>().With.Message.Contains("6 channels"));
            Assert.That(() => FlacReader.Read(WavWriter.Write(new short[4], Rate)), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => FlacReader.Read(Encoding.ASCII.GetBytes("fLaC")), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => AudioFile.Read(Encoding.ASCII.GetBytes("ID3 but nothing after it")), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void Flac_Crc_MatchesThePublishedCheckValues()
        {
            // CRC-8 with polynomial 0x07 and CRC-16 with 0x8005 (the "BUYPASS" form) of the text 123456789.
            byte[] text = Encoding.ASCII.GetBytes("123456789");
            Assert.That(Crc(text, 0x07, 8), Is.EqualTo(0xF4));
            Assert.That(Crc(text, 0x8005, 16), Is.EqualTo(0xFEE8));
        }
    }
}
