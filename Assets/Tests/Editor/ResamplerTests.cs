using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Strands.Audio;

namespace Strands.Tests
{
    /// <summary>
    /// Bringing audio at another sample rate to 44.1 kHz. A sine can be made exactly at any rate,
    /// so the tests make one at the file's rate, convert it, and hold the result against the same
    /// sine made at 44.1 kHz: its level and its place in time are both in that one comparison.
    /// </summary>
    public class ResamplerTests
    {
        const int Target = 44100;
        const double Level = 16000;
        const double Phase = 0.3;
        // The first and last frames of a result are made partly from the silence either side of the file.
        const int Margin = 500;
        // One step for the rounding of what went in and one for the rounding of what came out.
        const double TwoSteps = 2;

        static double Exact(double hertz, int frame, int rate, double level = Level, double phase = Phase)
        {
            return level * Math.Sin(2 * Math.PI * hertz * frame / rate + phase);
        }

        /// <summary>A sine on the left and the same upside down on the right.</summary>
        static WavData Sine(int rate, double seconds, double hertz, double level = Level, double phase = Phase)
        {
            int frames = (int)Math.Round(seconds * rate);
            var samples = new short[2 * frames];
            for (int i = 0; i < frames; i++)
            {
                short value = (short)Math.Floor(Exact(hertz, i, rate, level, phase) + 0.5);
                samples[2 * i] = value;
                samples[2 * i + 1] = (short)-value;
            }
            return Audio(rate, samples);
        }

        static WavData Audio(int rate, short[] samples)
        {
            return new WavData { SampleRate = rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
        }

        /// <summary>The furthest the left side of a result is from the sine it should be, in steps of 16 bits.</summary>
        static double WorstError(WavData result, double hertz)
        {
            double worst = 0;
            for (int i = Margin; i < result.Frames - Margin; i++)
                worst = Math.Max(worst, Math.Abs(result.Samples[2 * i] - Exact(hertz, i, Target)));
            return worst;
        }

        /// <summary>How much of one frequency is in the left side of a result, by fitting a sine to it.</summary>
        static double AmountOf(WavData result, double hertz)
        {
            double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
            for (int i = Margin; i < result.Frames - Margin; i++)
            {
                double angle = 2 * Math.PI * hertz * i / Target;
                double s = Math.Sin(angle), c = Math.Cos(angle), y = result.Samples[2 * i];
                ss += s * s;
                cc += c * c;
                sc += s * c;
                ys += y * s;
                yc += y * c;
            }
            double all = ss * cc - sc * sc;
            double p = (ys * cc - yc * sc) / all, q = (yc * ss - ys * sc) / all;
            return Math.Sqrt(p * p + q * q);
        }

        static double Decibels(double ratio)
        {
            return 20 * Math.Log10(Math.Max(ratio, 1e-12));
        }

        [Test]
        public void AudioAt441IsReturnedAsItIs()
        {
            WavData wav = Sine(Target, 0.1, 1000);
            Assert.That(Resampler.ToTargetRate(wav), Is.SameAs(wav));
            Assert.That(wav.SourceSampleRate, Is.EqualTo(0));
        }

        [TestCase(50)]
        [TestCase(1000)]
        [TestCase(10000)]
        [TestCase(19000)]
        [TestCase(20000)]
        public void From48kHz_ASineKeepsItsLevelAndItsPlaceUpTo20kHz(double hertz)
        {
            WavData source = Sine(48000, 1, hertz);
            WavData result = Resampler.ToTargetRate(source);
            Assert.That(result, Is.Not.SameAs(source));
            Assert.That(result.SampleRate, Is.EqualTo(Target));
            Assert.That(result.SourceSampleRate, Is.EqualTo(48000));
            Assert.That(result.SourceChannels, Is.EqualTo(2));
            Assert.That(result.SourceBitsPerSample, Is.EqualTo(16));
            // A second is a second: 48,000 frames become 44,100.
            Assert.That(result.Frames, Is.EqualTo(Target));
            // A frame out of place would be 2,000 steps off at 1 kHz, and a hundredth of a dB in level 18.
            Assert.That(WorstError(result, hertz), Is.LessThanOrEqualTo(TwoSteps));
            // The right side went in upside down and comes out so.
            for (int i = Margin; i < result.Frames - Margin; i += 7)
                Assert.That(result.Samples[2 * i + 1], Is.EqualTo(-result.Samples[2 * i]).Within(1));
        }

        [TestCase(22500)]
        [TestCase(23000)]
        [TestCase(23900)]
        public void From48kHz_WhatIsAbove22kHzDoesNotFoldBack(double hertz)
        {
            // 44.1 kHz cannot hold these. Left alone, each would come out at 44,100 less its frequency, as a tone that was never there.
            WavData result = Resampler.ToTargetRate(Sine(48000, 1, hertz));
            Assert.That(Decibels(AmountOf(result, Target - hertz) / Level), Is.LessThan(-100));

            // What is left altogether is the rounding of the samples, about 92 dB under the tone that went in.
            double energy = 0;
            for (int i = Margin; i < result.Frames - Margin; i++)
                energy += (double)result.Samples[2 * i] * result.Samples[2 * i];
            double left = Math.Sqrt(2 * energy / (result.Frames - 2 * Margin));
            Assert.That(Decibels(left / Level), Is.LessThan(-88));
        }

        [TestCase(8000)]
        [TestCase(11025)]
        [TestCase(12000)]
        [TestCase(16000)]
        [TestCase(22050)]
        [TestCase(24000)]
        [TestCase(32000)]
        [TestCase(88200)]
        [TestCase(96000)]
        [TestCase(176400)]
        [TestCase(192000)]
        public void EveryUsualRateIsConverted(int rate)
        {
            Assert.That(Resampler.CanConvert(rate));
            // A tone in the middle, and one near the top of what the lower of the two rates can hold.
            foreach (double hertz in new[] { 1000.0, 0.8 * Math.Min(rate, Target) / 2 })
            {
                WavData source = Sine(rate, 1, hertz);
                WavData result = Resampler.ToTargetRate(source);
                Assert.That(result.SampleRate, Is.EqualTo(Target));
                Assert.That(result.SourceSampleRate, Is.EqualTo(rate));
                Assert.That(result.Frames, Is.EqualTo(Target));
                Assert.That(WorstError(result, hertz), Is.LessThanOrEqualTo(TwoSteps), hertz + " Hz");
            }
        }

        [TestCase(48000, 1)]
        [TestCase(48000, 479)]
        [TestCase(48000, 480)]
        [TestCase(48000, 481)]
        [TestCase(96000, 12345)]
        [TestCase(32000, 12345)]
        [TestCase(22050, 3)]
        public void TheResultIsAsLongInTimeAsTheSource(int rate, int frames)
        {
            // Rounded up to a whole frame, so that nothing at the end of the file is left out.
            int expected = (int)Math.Ceiling((double)frames * Target / rate);
            Assert.That(Resampler.ToTargetRate(Audio(rate, new short[2 * frames])).Frames, Is.EqualTo(expected));
        }

        [TestCase(48000)]
        [TestCase(96000)]
        [TestCase(32000)]
        [TestCase(22050)]
        public void AClickStaysWhereItWas(int rate)
        {
            // A fifth of a second in at any of these rates is a whole frame, and frame 8,820 at 44.1 kHz.
            var samples = new short[2 * rate];
            samples[2 * (rate / 5)] = 30000;
            samples[2 * (rate / 5) + 1] = -30000;
            WavData result = Resampler.ToTargetRate(Audio(rate, samples));
            int loudest = 0;
            for (int i = 0; i < result.Frames; i++)
                if (Math.Abs(result.Samples[2 * i]) > Math.Abs(result.Samples[2 * loudest]))
                    loudest = i;
            Assert.That(loudest, Is.EqualTo(Target / 5));
            // From a higher rate the click is spread over more than one frame, so it is lower; it is still plainly there.
            Assert.That(result.Samples[2 * loudest], Is.GreaterThan(10000));
            Assert.That(result.Samples[2 * loudest + 1], Is.EqualTo(-result.Samples[2 * loudest]).Within(1));
            // Well away from it there is nothing.
            Assert.That(result.Samples[2 * (loudest - 2000)], Is.EqualTo(0));
            Assert.That(result.Samples[2 * (loudest + 2000)], Is.EqualTo(0));
        }

        [TestCase(48000)]
        [TestCase(32000)]
        public void ASteadyLevelComesThroughExactly(int rate)
        {
            var samples = new short[2 * rate];
            for (int i = 0; i < rate; i++)
            {
                samples[2 * i] = 12345;
                samples[2 * i + 1] = -321;
            }
            WavData result = Resampler.ToTargetRate(Audio(rate, samples));
            for (int i = Margin; i < result.Frames - Margin; i++)
            {
                if (result.Samples[2 * i] != 12345 || result.Samples[2 * i + 1] != -321)
                    Assert.Fail("Frame " + i + " is " + result.Samples[2 * i] + " and " + result.Samples[2 * i + 1]);
            }
        }

        [Test]
        public void TheTwoSidesAreKeptApart()
        {
            WavData source = Sine(48000, 0.5, 3000);
            for (int i = 0; i < source.Frames; i++)
                source.Samples[2 * i + 1] = 0;
            WavData result = Resampler.ToTargetRate(source);
            Assert.That(WorstError(result, 3000), Is.LessThanOrEqualTo(TwoSteps));
            for (int i = 0; i < result.Frames; i++)
                if (result.Samples[2 * i + 1] != 0)
                    Assert.Fail("The silent side has " + result.Samples[2 * i + 1] + " at frame " + i);
        }

        [Test]
        public void WhatGoesOverFullScaleIsHeldThere()
        {
            // At a quarter of the rate and an eighth of a turn round, every sample of this sine is 0.92 of
            // full scale, but the wave between them reaches 1.3. At 44.1 kHz the samples fall
            // elsewhere on it, and those over full scale must stop there and not wrap round.
            const double level = 1.3 * short.MaxValue;
            WavData source = Sine(48000, 0.5, 12000, level, Math.PI / 4);
            foreach (short sample in source.Samples)
                Assert.That(Math.Abs((int)sample), Is.InRange(30000, 30200));

            WavData result = Resampler.ToTargetRate(source);
            int held = 0;
            for (int i = Margin; i < result.Frames - Margin; i++)
            {
                double exact = Exact(12000, i, Target, level, Math.PI / 4);
                int sample = result.Samples[2 * i];
                if (exact > short.MaxValue + TwoSteps || exact < short.MinValue - TwoSteps)
                {
                    held++;
                    if (sample != (exact > 0 ? short.MaxValue : short.MinValue))
                        Assert.Fail("Frame " + i + " should be held at full scale and is " + sample);
                }
                else if (Math.Abs(sample - Math.Max(short.MinValue, Math.Min(short.MaxValue, exact))) > TwoSteps)
                {
                    Assert.Fail("Frame " + i + " is " + sample + " and should be " + exact);
                }
            }
            Assert.That(held, Is.GreaterThan(1000));
        }

        [Test]
        public void RatesThatCannotBeConvertedAreRefusedWithAReason()
        {
            foreach (int rate in new[] { 47999, 44056, 7000, 384000, 0, -48000 })
            {
                Assert.That(Resampler.CanConvert(rate), Is.False, rate.ToString());
                Assert.That(() => Resampler.ToTargetRate(Audio(rate, new short[200])),
                    Throws.TypeOf<NotSupportedException>().With.Message.Contains(rate.ToString()).And.Message.Contains("44.1 kHz"));
            }
            Assert.That(Resampler.CanConvert(Target));
            Assert.That(Resampler.CanConvert(48000));
        }

        sealed class Recorder : IProgress<float>
        {
            public readonly List<float> Values = new List<float>();
            public void Report(float value) { lock (Values) Values.Add(value); }
        }

        [Test]
        public void ReportsProgressAndStopsWhenCancelled()
        {
            var recorder = new Recorder();
            Resampler.ToTargetRate(Sine(48000, 2, 440), recorder);
            Assert.That(recorder.Values, Is.Not.Empty);
            Assert.That(recorder.Values, Has.All.InRange(0f, 1f));
            Assert.That(recorder.Values, Has.Some.EqualTo(1f));

            var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.That(() => Resampler.ToTargetRate(Sine(48000, 2, 440), null, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
        }

        [Test]
        public void AnEmptyFileStaysEmpty()
        {
            WavData result = Resampler.ToTargetRate(Audio(48000, new short[0]));
            Assert.That(result.Frames, Is.EqualTo(0));
            Assert.That(result.SampleRate, Is.EqualTo(Target));
        }
    }
}
