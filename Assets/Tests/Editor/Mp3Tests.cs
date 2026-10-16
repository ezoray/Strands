using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Strands.Audio;

namespace Strands.Tests
{
    /// <summary>
    /// The MP3 reader. Nothing on the development machine can encode MP3, so the streams here are
    /// built by hand: Layer III frames that are silent, or that hold one low note in their first
    /// half. They show where each frame lands in time, not how good the decoding sounds; that was
    /// compared with another decoder on real files (see docs/spec-notes.md).
    /// </summary>
    public class Mp3Tests
    {
        const int PerFrame = 1152;
        static readonly int[] Kbps = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        static readonly int[] Rates = { 44100, 48000, 32000 };

        /// <summary>
        /// One MPEG-1 Layer III frame. With sound, the first granule holds a single spectral line
        /// (the lowest, at +1) written with the plain four-bit count1 table, five bits in all; the
        /// right channel of a stereo frame gets the same line at -1. Those bits sit at the start of
        /// the frame's own data, or, with borrow, the given number of bytes back in the frame before.
        /// </summary>
        static byte[] Frame(bool sound = true, bool mono = true, int bitRateIndex = 1, bool padded = false, int rateIndex = 0, int borrow = 0, byte lastByte = 0)
        {
            var bits = new FlacTests.Bits();
            bits.Write(0xFFFB, 16);                 // sync, MPEG-1, Layer III, no CRC
            bits.Write(bitRateIndex, 4);
            bits.Write(rateIndex, 2);
            bits.Write(padded ? 1 : 0, 1);
            bits.Write(0, 1);
            bits.Write(mono ? 3 : 0, 2);            // mono, or plain stereo
            bits.Write(0, 6);

            bits.Write(borrow, 9);                  // main_data_begin
            bits.Write(0, mono ? 5 : 3);            // private bits
            bits.Write(0, mono ? 4 : 8);            // scale factor sharing
            for (int granule = 0; granule < 2; granule++)
                for (int channel = 0; channel < (mono ? 1 : 2); channel++)
                {
                    bool on = sound && granule == 0;
                    bits.Write(on ? 5 : 0, 12);     // bits of main data
                    bits.Write(0, 9);               // no big values
                    bits.Write(on ? 210 : 0, 8);    // global gain; 210 is a factor of one
                    bits.Write(0, 4);               // no scale factors
                    bits.Write(0, 1);               // long blocks
                    bits.Write(0, 15 + 4 + 3);      // Huffman tables and regions, unused
                    bits.Write(0, 2);
                    bits.Write(1, 1);               // count1 table B: four plain bits, inverted
                }
            byte[] head = bits.ToArray();

            var frame = new byte[144000 * Kbps[bitRateIndex] / Rates[rateIndex] + (padded ? 1 : 0)];
            head.CopyTo(frame, 0);
            if (sound && borrow == 0)
            {
                // 0111 is the quadruple (1, 0, 0, 0); the bit after it is the sign.
                frame[head.Length] = mono ? (byte)0x70 : (byte)0x73;
                if (!mono)
                    frame[head.Length + 1] = 0xC0;
            }
            if (lastByte != 0)
                frame[frame.Length - 1] = lastByte;
            return frame;
        }

        /// <summary>
        /// A mono frame whose first granule is three short blocks holding one spectral line at +1,
        /// with a gain for each of the three windows (each step divides by four). The lines before
        /// it are pairs of zeros in Huffman table 0, which takes no bits.
        /// </summary>
        static byte[] ShortFrame(int line, int[] gains)
        {
            var bits = new FlacTests.Bits();
            bits.Write(0xFFFB, 16);
            bits.Write(9, 4);                       // 128 kbit/s
            bits.Write(0, 4);
            bits.Write(3, 2);                       // mono
            bits.Write(0, 6);
            bits.Write(0, 9 + 5 + 4);               // main_data_begin, private bits, scale factor sharing

            bits.Write(5, 12);                      // bits of main data
            bits.Write(line / 4 * 2, 9);            // big values, up to the quadruple that holds the line
            bits.Write(202, 8);                     // global gain: a quarter
            bits.Write(0, 4);
            bits.Write(1, 1);                       // window switching
            bits.Write(2, 2);                       // short blocks
            bits.Write(0, 1 + 10);                  // not mixed; table 0 for both regions
            foreach (int gain in gains)
                bits.Write(gain, 3);
            bits.Write(0, 2);
            bits.Write(1, 1);                       // count1 table B
            bits.Write(0, 59);                      // second granule: nothing
            byte[] head = bits.ToArray();

            var frame = new byte[144000 * 128 / 44100];
            head.CopyTo(frame, 0);
            // The quadruple with a 1 at the line's place, inverted, then a sign bit of 0.
            frame[head.Length] = (byte)((~(8 >> (line % 4)) & 15) << 4);
            return frame;
        }

        /// <summary>The frame LAME and ffmpeg put first: no sound, the frame count, and the encoder's delay and padding.</summary>
        static byte[] InfoFrame(string encoder, int delay, int padding, int frames, bool mono = true)
        {
            byte[] frame = Frame(sound: false, mono: mono, bitRateIndex: 9);
            var tag = new List<byte>(Encoding.ASCII.GetBytes("Info"));
            tag.AddRange(new byte[] { 0, 0, 0, 0x0F });
            tag.AddRange(new byte[] { (byte)(frames >> 24), (byte)(frames >> 16), (byte)(frames >> 8), (byte)frames });
            tag.AddRange(new byte[4 + 100 + 4]);    // byte count, seek table, quality
            if (encoder != null)
            {
                var lame = new byte[24];
                Encoding.ASCII.GetBytes(encoder).CopyTo(lame, 0);
                lame[21] = (byte)(delay >> 4);
                lame[22] = (byte)((delay << 4) | (padding >> 8));
                lame[23] = (byte)padding;
                tag.AddRange(lame);
            }
            tag.ToArray().CopyTo(frame, 4 + (mono ? 17 : 32));
            return frame;
        }

        static byte[] Join(params byte[][] parts)
        {
            return parts.SelectMany(part => part).ToArray();
        }

        static byte[] Frames(int count, Func<int, byte[]> frame)
        {
            return Join(Enumerable.Range(0, count).Select(frame).ToArray());
        }

        /// <summary>Average size of the left channel over a stretch of the output.</summary>
        static double Level(WavData wav, int from, int count, int channel = 0)
        {
            double sum = 0;
            for (int i = from; i < from + count; i++)
                sum += Math.Abs((int)wav.Samples[2 * i + channel]);
            return sum / count;
        }

        /// <summary>
        /// Level in the part of the output where the note of the frame with this number is, when
        /// the output starts the given number of samples after the first decoded sample.
        /// </summary>
        static double LevelOfFrame(WavData wav, int frame, int skipped = Mp3Reader.DecoderDelayFrames)
        {
            // Measured: the note of a frame runs from about 500 samples before this stretch to 300 after it.
            return Level(wav, frame * PerFrame + Mp3Reader.DecoderDelayFrames - skipped + 50, 600);
        }

        const double Loud = 2000, Quiet = 2;

        [Test]
        public void Mp3_EveryFrameIsDecodedAndTheDecoderDelayIsLeftOut()
        {
            WavData wav = Mp3Reader.Read(Frames(12, i => Frame()));
            Assert.That(wav.SampleRate, Is.EqualTo(44100));
            Assert.That(wav.SourceChannels, Is.EqualTo(1));
            Assert.That(wav.Frames, Is.EqualTo(12 * PerFrame - Mp3Reader.DecoderDelayFrames));
            for (int frame = 0; frame < 11; frame++)
                Assert.That(LevelOfFrame(wav, frame), Is.GreaterThan(Loud), "frame " + frame);
        }

        [Test]
        public void Mp3_ASilentFrameKeepsItsPlaceInTime()
        {
            WavData wav = Mp3Reader.Read(Frames(12, i => Frame(sound: i != 5 && i != 6)));
            Assert.That(wav.Frames, Is.EqualTo(12 * PerFrame - Mp3Reader.DecoderDelayFrames));
            for (int frame = 0; frame < 11; frame++)
                if (frame == 5 || frame == 6)
                    Assert.That(LevelOfFrame(wav, frame), Is.LessThan(Quiet), "frame " + frame);
                else
                    Assert.That(LevelOfFrame(wav, frame), Is.GreaterThan(Loud), "frame " + frame);
        }

        [Test]
        public void Mp3_AFrameThatCannotBeDecodedBecomesSilenceOfTheSameLength()
        {
            // Frame 4 says its data starts 300 bytes back, where the frames before have nothing like that much.
            WavData wav = Mp3Reader.Read(Frames(12, i => Frame(borrow: i == 0 ? 0 : i == 4 ? 300 : 0)));
            Assert.That(wav.Frames, Is.EqualTo(12 * PerFrame - Mp3Reader.DecoderDelayFrames));
            for (int frame = 5; frame < 11; frame++)
                Assert.That(LevelOfFrame(wav, frame), Is.GreaterThan(Loud), "frame " + frame);
        }

        [Test]
        public void Mp3_NLayer_AFrameIsNotLostWhenTheOneBeforeEndsOnTheLastByteOfTheReservoir()
        {
            // The change made to Assets/NLayer/Decoder/BitReservoir.cs. The reservoir is a ring of
            // 8192 bytes. Mono frames at 32 kbit/s add 83 bytes each, or 84 with padding, so 40 of
            // one and 58 of the other fill it exactly. The next frame takes its data from the last
            // byte of the frame before; the original code dropped such a frame.
            const int wrapAfter = 98;
            byte[] file = Frames(104, i => Frame(
                padded: i >= 40 && i < wrapAfter,
                sound: true,
                borrow: i == wrapAfter ? 1 : 0,
                lastByte: i == wrapAfter - 1 ? (byte)0x70 : (byte)0));
            WavData wav = Mp3Reader.Read(file);
            Assert.That(wav.Frames, Is.EqualTo(104 * PerFrame - Mp3Reader.DecoderDelayFrames));
            for (int frame = wrapAfter - 3; frame < wrapAfter + 4; frame++)
                Assert.That(LevelOfFrame(wav, frame), Is.GreaterThan(Loud), "frame " + frame);
        }

        [Test]
        public void Mp3_NLayer_TheTopBandOfAShortBlockUsesTheGainOfItsOwnWindow()
        {
            // The change made to Assets/NLayer/Decoder/LayerIIIDecoder.cs. In short blocks the top
            // band is lines 408 to 575, a third to each window, so line 470 belongs to the second.
            // The original code gave the whole band the first window's gain.
            Func<int[], double> peak = gains =>
            {
                byte[] silence = Frames(3, i => Frame(sound: false, bitRateIndex: 9));
                WavData wav = Mp3Reader.Read(Join(silence, ShortFrame(470, gains), silence));
                return wav.Samples.Max(sample => Math.Abs((int)sample));
            };
            double plain = peak(new[] { 0, 0, 0 });
            Assert.That(plain, Is.GreaterThan(800).And.LessThan(30000));
            Assert.That(peak(new[] { 0, 2, 0 }), Is.EqualTo(plain / 16).Within(plain / 16 * 0.1), "its own window's gain");
            Assert.That(peak(new[] { 2, 0, 3 }), Is.EqualTo(plain).Within(plain * 0.01), "the other windows' gains");
        }

        [TestCase("LAME3.100")]
        [TestCase("Lavc58.35")]
        [TestCase("Lavf57.83")]
        public void Mp3_EncoderDelayAndPaddingComeFromTheInfoFrame(string encoder)
        {
            byte[] info = InfoFrame(encoder, 576, 1000, 12);
            byte[] file = Join(info, Frames(12, i => Frame(sound: i == 6)));
            Mp3Reader.Trim trim = Mp3Reader.ReadTrim(file);
            Assert.That(trim.Known);
            Assert.That(trim.EncoderDelayFrames, Is.EqualTo(576));
            Assert.That(trim.EncoderPaddingFrames, Is.EqualTo(1000));
            Assert.That(trim.AudioOffset, Is.EqualTo(info.Length));

            // The information frame is not sound, and the delay and padding come off the twelve that are.
            WavData wav = Mp3Reader.Read(file);
            Assert.That(wav.Frames, Is.EqualTo(12 * PerFrame - 576 - 1000));
            int skipped = Mp3Reader.DecoderDelayFrames + 576;
            Assert.That(LevelOfFrame(wav, 6, skipped), Is.GreaterThan(Loud));
            Assert.That(LevelOfFrame(wav, 5, skipped), Is.LessThan(Quiet));
            Assert.That(LevelOfFrame(wav, 7, skipped), Is.LessThan(Quiet));
        }

        [Test]
        public void Mp3_AnInfoFrameWithoutEncoderFiguresOnlyTakesOffTheDecoderDelay()
        {
            foreach (byte[] info in new[] { InfoFrame(null, 0, 0, 12), InfoFrame("LAME3.100", 0, 0, 12), InfoFrame("\0\0\0\0", 576, 576, 12) })
            {
                byte[] file = Join(info, Frames(12, i => Frame(sound: i == 6)));
                Mp3Reader.Trim trim = Mp3Reader.ReadTrim(file);
                Assert.That(trim.Known, Is.False);
                Assert.That(trim.AudioOffset, Is.EqualTo(info.Length));
                WavData wav = Mp3Reader.Read(file);
                Assert.That(wav.Frames, Is.EqualTo(12 * PerFrame - Mp3Reader.DecoderDelayFrames));
                Assert.That(LevelOfFrame(wav, 6), Is.GreaterThan(Loud));
            }
        }

        [Test]
        public void Mp3_TagsAndOtherBytesAreSteppedOverWithoutTakingUpTime()
        {
            byte[] plain = Frames(10, i => Frame(sound: i % 3 == 0));
            short[] expected = Mp3Reader.Read(plain).Samples;

            // An ID3v2 tag whose body happens to hold the two bytes that start a frame.
            var id3 = new List<byte>(Encoding.ASCII.GetBytes("ID3"));
            id3.AddRange(new byte[] { 3, 0, 0, 0, 0, 2, 0 });
            id3.AddRange(Enumerable.Repeat(new byte[] { 0xFF, 0xFB, 0x10, 0xC0 }, 64).SelectMany(b => b));
            var id3v1 = new List<byte>(Encoding.ASCII.GetBytes("TAG"));
            id3v1.AddRange(Enumerable.Repeat((byte)0xFF, 125));

            byte[] tagged = Join(id3.ToArray(), plain, id3v1.ToArray());
            Assert.That(Mp3Reader.ReadTrim(tagged).AudioOffset, Is.EqualTo(id3.Count));
            CollectionAssert.AreEqual(expected, Mp3Reader.Read(tagged).Samples);

            // Bytes that are not a frame, between two frames.
            byte[] first = Frames(4, i => Frame(sound: i % 3 == 0)), rest = Frames(6, i => Frame(sound: (i + 4) % 3 == 0));
            CollectionAssert.AreEqual(expected, Mp3Reader.Read(Join(first, Encoding.ASCII.GetBytes("not a frame at all"), rest)).Samples);

            // A last frame that is cut off is left out.
            byte[] cut = Join(plain, Frame().Take(60).ToArray());
            CollectionAssert.AreEqual(expected, Mp3Reader.Read(cut).Samples);
        }

        [Test]
        public void Mp3_StereoIsKeptAndMonoIsDoubled()
        {
            WavData mono = Mp3Reader.Read(Frames(6, i => Frame()));
            Assert.That(mono.SourceChannels, Is.EqualTo(1));
            for (int i = 0; i < mono.Frames; i++)
                Assert.That(mono.Samples[2 * i + 1], Is.EqualTo(mono.Samples[2 * i]));

            // The stereo frames hold the same note the other way up in the right channel.
            WavData stereo = Mp3Reader.Read(Frames(6, i => Frame(mono: false, bitRateIndex: 5)));
            Assert.That(stereo.SourceChannels, Is.EqualTo(2));
            Assert.That(stereo.Frames, Is.EqualTo(6 * PerFrame - Mp3Reader.DecoderDelayFrames));
            Assert.That(LevelOfFrame(stereo, 2), Is.GreaterThan(Loud));
            for (int i = 0; i < stereo.Frames; i++)
                Assert.That(stereo.Samples[2 * i + 1], Is.EqualTo(-stereo.Samples[2 * i]).Within(1), "sample " + i);
        }

        [TestCase(0, 44100)]
        [TestCase(1, 48000)]
        [TestCase(2, 32000)]
        public void Mp3_SampleRateComesFromTheFrameHeader(int rateIndex, int rate)
        {
            WavData wav = Mp3Reader.Read(Frames(6, i => Frame(rateIndex: rateIndex, bitRateIndex: 5)));
            Assert.That(wav.SampleRate, Is.EqualTo(rate));
            Assert.That(wav.Frames, Is.EqualTo(6 * PerFrame - Mp3Reader.DecoderDelayFrames));
        }

        [Test]
        public void Mp3_ProgressIsReportedAndCancellingStopsTheDecoding()
        {
            byte[] file = Frames(200, i => Frame());
            var reported = new List<float>();
            Mp3Reader.Read(file, new Recorder(reported));
            Assert.That(reported, Is.Not.Empty);
            Assert.That(reported, Is.Ordered);
            Assert.That(reported.All(value => value >= 0 && value <= 1));

            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.That(() => Mp3Reader.Read(file, null, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
        }

        sealed class Recorder : IProgress<float>
        {
            readonly List<float> values;
            public Recorder(List<float> values) { this.values = values; }
            public void Report(float value) { values.Add(value); }
        }

        [Test]
        public void Mp3_FilesWithNoFramesAreRefused()
        {
            Assert.That(() => Mp3Reader.Read(new byte[2000]), Throws.TypeOf<InvalidDataException>());
            // One header on its own, with nothing after it that confirms it.
            Assert.That(() => Mp3Reader.Read(Join(new byte[] { 0xFF, 0xFB, 0x10, 0xC0 }, Encoding.ASCII.GetBytes(new string('x', 400)))),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(() => AudioFile.Read(Encoding.ASCII.GetBytes("ID3 but nothing after it at all")), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public void AudioFile_TakesAFileForMp3WhenItStartsWithATagOrAFrame()
        {
            byte[] plain = Frames(6, i => Frame());
            short[] expected = Mp3Reader.Read(plain).Samples;
            CollectionAssert.AreEqual(expected, AudioFile.Read(plain).Samples);
            byte[] tagged = Join(Encoding.ASCII.GetBytes("ID3"), new byte[] { 3, 0, 0, 0, 0, 0, 20 }, new byte[20], plain);
            CollectionAssert.AreEqual(expected, AudioFile.Read(tagged).Samples);
        }

        [Test]
        public void Mp3_FixturesLineUpWithTheirWav()
        {
            string[] paths = Fixtures.AudioPaths("mp3").ToArray();
            if (paths.Length == 0)
                Assert.Ignore("There is no MP3 in Fixtures/audio yet: nothing on the development machine can encode one. Until there is, the decoder delay is a reported figure.");
            foreach (string path in paths)
            {
                WavData expected = WavReader.Read(Fixtures.ReferenceWav(path));
                WavData read = Mp3Reader.Read(path);
                string name = Path.GetFileName(path);
                Assert.That(Mp3Reader.ReadTrim(File.ReadAllBytes(path)).Known, name + " should record its encoder delay");
                Assert.That(read.Frames, Is.EqualTo(expected.Frames), name);
                Assert.That(BestOffset(expected, read, 1500), Is.EqualTo(0), name + ": samples the MP3 is late by");
            }
        }

        /// <summary>How many samples later the second is than the first, by the closest match of their left channels.</summary>
        static int BestOffset(WavData first, WavData second, int range)
        {
            int count = Math.Min(first.Frames, second.Frames) - 2 * range;
            long best = long.MaxValue;
            int bestOffset = 0;
            for (int offset = -range; offset <= range; offset++)
            {
                long sum = 0;
                for (int i = range; i < range + count; i++)
                    sum += Math.Abs(first.Samples[2 * i] - second.Samples[2 * (i + offset)]);
                if (sum < best)
                {
                    best = sum;
                    bestOffset = offset;
                }
            }
            return bestOffset;
        }

        [Test]
        public void Mp3_BestOffsetFindsAKnownShift()
        {
            // The check used on fixtures, tried on a signal moved by a known amount.
            var random = new Random(3);
            var samples = new short[2 * 6000];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)random.Next(-20000, 20000);
            var moved = new short[samples.Length];
            Array.Copy(samples, 0, moved, 2 * 37, samples.Length - 2 * 37);
            var a = new WavData { SampleRate = 44100, Samples = samples };
            var b = new WavData { SampleRate = 44100, Samples = moved };
            Assert.That(BestOffset(a, b, 200), Is.EqualTo(37));
            Assert.That(BestOffset(b, a, 200), Is.EqualTo(-37));
            Assert.That(BestOffset(a, a, 200), Is.EqualTo(0));
        }
    }
}
