using System;
using System.Threading;
using System.Threading.Tasks;

namespace Strands.Audio
{
    /// <summary>
    /// A track as its low, middle and high sound, each as the lowest and highest value of short
    /// blocks, so that a waveform can be drawn with the three in colours of their own, the way a
    /// DJ player draws one: the kick and the bass, the body of the music, and the hats on top.
    /// It is a picture and nothing else. Nothing that is exported or measured comes from here.
    /// </summary>
    public sealed class BandPeaks
    {
        /// <summary>Below this is the low band.</summary>
        public const double LowBelowHz = 200;
        /// <summary>Above this is the high band. Between the two is the middle.</summary>
        public const double HighAboveHz = 2000;
        /// <summary>The finest a band can be drawn: a third of a millisecond at 44.1 kHz.</summary>
        public const int FineFrames = 16;

        // A second table of longer blocks, for when a pixel covers a great many of the short ones.
        const int FinePerCoarse = 16;
        // The track is filtered in stretches, side by side. Each starts its filters this far early
        // so that they have settled by the time their output counts, which makes a stretch come
        // out the same wherever the stretches are cut.
        const int StretchFrames = 1 << 16;
        const int SettleFrames = 4096;

        public readonly IPeaks Low, Mid, High;

        public int Frames { get; private set; }

        public BandPeaks(short[] interleavedStereo, int sampleRate, CancellationToken cancel = default(CancellationToken))
        {
            int frames = interleavedStereo.Length / 2;
            Frames = frames;
            int blocks = (frames + FineFrames - 1) / FineFrames;
            var low = new Band(frames, blocks);
            var mid = new Band(frames, blocks);
            var high = new Band(frames, blocks);
            Low = low;
            Mid = mid;
            High = high;

            Section lowPass = Section.LowPass(LowBelowHz, sampleRate), lowCut = Section.HighPass(LowBelowHz, sampleRate);
            Section highPass = Section.HighPass(HighAboveHz, sampleRate), highCut = Section.LowPass(HighAboveHz, sampleRate);
            int stretches = (frames + StretchFrames - 1) / StretchFrames;
            Parallel.For(0, stretches, new ParallelOptions { CancellationToken = cancel }, stretch =>
            {
                // Each band goes through its filter twice, which makes the slopes steep enough
                // that a kick stays out of the middle and a hat out of everything but the top.
                Section low1 = lowPass, low2 = lowPass, high1 = highPass, high2 = highPass;
                Section mid1 = highCut, mid2 = highCut, mid3 = lowCut, mid4 = lowCut;
                int start = stretch * StretchFrames, end = Math.Min(frames, start + StretchFrames);
                for (int frame = Math.Max(0, start - SettleFrames); frame < start; frame++)
                {
                    double value = (interleavedStereo[2 * frame] + interleavedStereo[2 * frame + 1]) * 0.5;
                    low2.Next(low1.Next(value));
                    high2.Next(high1.Next(value));
                    mid4.Next(mid3.Next(mid2.Next(mid1.Next(value))));
                }
                for (int block = start / FineFrames; block * FineFrames < end; block++)
                {
                    double lowMin = double.MaxValue, lowMax = double.MinValue, midMin = double.MaxValue, midMax = double.MinValue;
                    double highMin = double.MaxValue, highMax = double.MinValue;
                    int blockEnd = Math.Min(end, (block + 1) * FineFrames);
                    for (int frame = block * FineFrames; frame < blockEnd; frame++)
                    {
                        double value = (interleavedStereo[2 * frame] + interleavedStereo[2 * frame + 1]) * 0.5;
                        double l = low2.Next(low1.Next(value));
                        double h = high2.Next(high1.Next(value));
                        double m = mid4.Next(mid3.Next(mid2.Next(mid1.Next(value))));
                        if (l < lowMin) lowMin = l;
                        if (l > lowMax) lowMax = l;
                        if (m < midMin) midMin = m;
                        if (m > midMax) midMax = m;
                        if (h < highMin) highMin = h;
                        if (h > highMax) highMax = h;
                    }
                    low.Set(block, lowMin, lowMax);
                    mid.Set(block, midMin, midMax);
                    high.Set(block, highMin, highMax);
                }
            });
            low.Finish();
            mid.Finish();
            high.Finish();
        }

        /// <summary>One second-order filter, with the state it carries from each sample to the next.</summary>
        struct Section
        {
            double b0, b1, b2, a1, a2, z1, z2;

            public static Section LowPass(double hertz, int sampleRate)
            {
                return Make(hertz, sampleRate, false);
            }

            public static Section HighPass(double hertz, int sampleRate)
            {
                return Make(hertz, sampleRate, true);
            }

            // A Butterworth section. Two of the same in a row make a Linkwitz-Riley filter.
            static Section Make(double hertz, int sampleRate, bool high)
            {
                double w = 2 * Math.PI * hertz / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / Math.Sqrt(2);
                double a0 = 1 + alpha, edge = high ? (1 + cos) / 2 : (1 - cos) / 2;
                return new Section
                {
                    b0 = edge / a0,
                    b1 = (high ? -2 : 2) * edge / a0,
                    b2 = edge / a0,
                    a1 = -2 * cos / a0,
                    a2 = (1 - alpha) / a0
                };
            }

            public double Next(double value)
            {
                double result = b0 * value + z1;
                z1 = b1 * value - a1 * result + z2;
                z2 = b2 * value - a2 * result;
                return result;
            }
        }

        sealed class Band : IPeaks
        {
            readonly int frames;
            readonly short[] fineMin, fineMax;
            readonly short[] coarseMin, coarseMax;

            public int Frames { get { return frames; } }

            public Band(int frames, int blocks)
            {
                this.frames = frames;
                fineMin = new short[blocks];
                fineMax = new short[blocks];
                int coarse = (blocks + FinePerCoarse - 1) / FinePerCoarse;
                coarseMin = new short[coarse];
                coarseMax = new short[coarse];
            }

            // A filter can ring a little over full scale, which the table has no room for.
            public void Set(int block, double min, double max)
            {
                fineMin[block] = (short)Math.Max(short.MinValue, Math.Floor(min));
                fineMax[block] = (short)Math.Min(short.MaxValue, Math.Ceiling(max));
            }

            public void Finish()
            {
                for (int coarse = 0; coarse < coarseMin.Length; coarse++)
                {
                    short low = short.MaxValue, high = short.MinValue;
                    int end = Math.Min(fineMin.Length, (coarse + 1) * FinePerCoarse);
                    for (int block = coarse * FinePerCoarse; block < end; block++)
                    {
                        if (fineMin[block] < low) low = fineMin[block];
                        if (fineMax[block] > high) high = fineMax[block];
                    }
                    coarseMin[coarse] = low;
                    coarseMax[coarse] = high;
                }
            }

            /// <summary>As <see cref="WaveformPeaks.Range"/>, to the nearest block of <see cref="FineFrames"/> outwards.</summary>
            public void Range(double fromFrame, double toFrame, out float min, out float max)
            {
                int from = (int)Math.Max(0, Math.Floor(fromFrame));
                int to = (int)Math.Min(frames, Math.Max(Math.Ceiling(toFrame), from + 1));
                min = 0;
                max = 0;
                if (from >= to)
                    return;
                int block = from / FineFrames, last = (to - 1) / FineFrames;
                short low = short.MaxValue, high = short.MinValue;
                while (block <= last)
                {
                    if (block % FinePerCoarse == 0 && block + FinePerCoarse - 1 <= last)
                    {
                        int coarse = block / FinePerCoarse;
                        if (coarseMin[coarse] < low) low = coarseMin[coarse];
                        if (coarseMax[coarse] > high) high = coarseMax[coarse];
                        block += FinePerCoarse;
                    }
                    else
                    {
                        if (fineMin[block] < low) low = fineMin[block];
                        if (fineMax[block] > high) high = fineMax[block];
                        block++;
                    }
                }
                min = low / 32768f;
                max = high / 32768f;
            }
        }
    }
}
