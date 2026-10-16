using System;
using NUnit.Framework;
using Strands.Audio;

namespace Strands.Tests
{
    /// <summary>The low, middle and high sound of a track, which the waveform of a full song is coloured by.</summary>
    public class BandPeaksTests
    {
        const int Rate = 44100;
        const double Level = 0.5;

        static short[] Tone(double hertz, double seconds)
        {
            var samples = new short[(int)(seconds * Rate) * 2];
            for (int frame = 0; frame < samples.Length / 2; frame++)
                samples[2 * frame] = samples[2 * frame + 1] = (short)Math.Round(Level * 32767 * Math.Sin(2 * Math.PI * hertz * frame / Rate));
            return samples;
        }

        // The loudest a band gets once its filter has settled.
        static double Peak(IPeaks band)
        {
            float min, max;
            band.Range(Rate / 10, band.Frames, out min, out max);
            return Math.Max(-min, max);
        }

        [TestCase(60, 0)]
        [TestCase(632, 1)]
        [TestCase(8000, 2)]
        public void BandPeaks_PutAToneInTheBandItBelongsTo(double hertz, int band)
        {
            var peaks = new BandPeaks(Tone(hertz, 1), Rate);
            IPeaks[] bands = { peaks.Low, peaks.Mid, peaks.High };
            for (int i = 0; i < bands.Length; i++)
            {
                if (i == band)
                    Assert.That(Peak(bands[i]), Is.EqualTo(Level).Within(0.05 * Level), "its own band");
                else
                    Assert.That(Peak(bands[i]), Is.LessThan(0.03 * Level), "band " + i);
            }
        }

        [Test]
        public void BandPeaks_ShowNoJoinWhereTheWorkWasShared()
        {
            // The track is filtered in stretches of 65,536 frames side by side. A steady tone has to
            // come out steady across the places where one stretch hands over to the next.
            var peaks = new BandPeaks(Tone(100, 5), Rate);
            // What two passes of the low filter leave of a tone at half the frequency it turns at.
            double steady = Level / (1 + Math.Pow(100 / BandPeaks.LowBelowHz, 4));
            float min, max;
            for (int join = 65536; join < peaks.Frames; join += 65536)
            {
                for (int block = join - 2048; block < join + 2048; block += 512)
                {
                    peaks.Low.Range(block, block + 512, out min, out max);
                    Assert.That(max, Is.EqualTo(steady).Within(0.002), "at frame " + block);
                    Assert.That(min, Is.EqualTo(-steady).Within(0.002), "at frame " + block);
                }
            }
        }

        [Test]
        public void BandPeaks_AWideRangeIsTheMostOfItsNarrowOnes()
        {
            // Noise, so that every block has a different figure and a wrong one would show.
            var random = new Random(7);
            var samples = new short[2 * 30000];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)random.Next(-12000, 12000);
            IPeaks band = new BandPeaks(samples, Rate).Mid;
            Assert.That(band.Frames, Is.EqualTo(30000));

            int[][] ranges = { new[] { 0, 30000 }, new[] { 5, 6 }, new[] { 250, 5000 }, new[] { 4096, 8192 }, new[] { 4095, 8193 }, new[] { 29990, 40000 }, new[] { 16, 272 } };
            foreach (int[] range in ranges)
            {
                float min, max, low = float.MaxValue, high = float.MinValue;
                band.Range(range[0], range[1], out min, out max);
                // Block by block, which never reaches the table of longer blocks.
                for (int block = range[0] / BandPeaks.FineFrames; block * BandPeaks.FineFrames < Math.Min(range[1], 30000); block++)
                {
                    float blockMin, blockMax;
                    band.Range(block * BandPeaks.FineFrames, (block + 1) * BandPeaks.FineFrames, out blockMin, out blockMax);
                    low = Math.Min(low, blockMin);
                    high = Math.Max(high, blockMax);
                }
                Assert.That(new[] { min, max }, Is.EqualTo(new[] { low, high }), range[0] + " to " + range[1]);
                Assert.That(max, Is.GreaterThan(0.01f));
            }
        }

        [Test]
        public void BandPeaks_AreNothingWhereThereIsNoSound()
        {
            var peaks = new BandPeaks(new short[2 * 5000], Rate);
            float min, max;
            foreach (IPeaks band in new[] { peaks.Low, peaks.Mid, peaks.High })
            {
                band.Range(0, 5000, out min, out max);
                Assert.That(new[] { min, max }, Is.EqualTo(new[] { 0f, 0f }));
                band.Range(-50, -10, out min, out max);
                Assert.That(new[] { min, max }, Is.EqualTo(new[] { 0f, 0f }));
                band.Range(6000, 7000, out min, out max);
                Assert.That(new[] { min, max }, Is.EqualTo(new[] { 0f, 0f }));
            }
            Assert.That(new BandPeaks(new short[0], Rate).Frames, Is.EqualTo(0));
        }
    }
}
