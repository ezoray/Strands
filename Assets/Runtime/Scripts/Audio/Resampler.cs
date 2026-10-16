using System;
using System.Threading;
using System.Threading.Tasks;

namespace Strands.Audio
{
    /// <summary>
    /// Brings audio at another sample rate to the 44.1 kHz the SP-16 and the DJS-1000 play, so that
    /// a file at 48 kHz can be used like any other. This is a conversion and not a stretch: the
    /// sound lasts as long as it did and at the same pitch, and a frame of the result is the same
    /// moment as it was in the file, so stems stay lined up and a beat stays where it was.
    ///
    /// Every frame of the result is a weighted sum of the frames around its moment in the file: a
    /// low-pass filter (a sinc under a Kaiser window) that passes everything up to
    /// <see cref="PassbandShare"/> of the lower rate's half and nothing from that half upwards,
    /// which is what keeps sound above 22.05 kHz from folding back into what is heard. There is a
    /// set of weights for each place a frame of the result can fall between two frames of the file.
    /// </summary>
    public static class Resampler
    {
        /// <summary>The rate everything is brought to.</summary>
        public const int TargetRate = 44100;
        public const int MinRate = 8000, MaxRate = 192000;

        /// <summary>The filter is level up to this share of half the lower rate: 20 kHz between 48 and 44.1 kHz.</summary>
        const double PassbandShare = 0.907;
        /// <summary>
        /// How far down, in dB, anything from half the lower rate upwards is pushed. One step of 16
        /// bits is 96 dB under full scale, so what is left of it is smaller than the rounding.
        /// </summary>
        const double StopbandDb = 110;
        /// <summary>
        /// The most places between two frames that weights are kept for. Every rate that is a whole
        /// number of hundreds of Hz needs no more, which takes in all the usual ones; a rate that
        /// would need more (47,999 Hz, say) is refused.
        /// </summary>
        const int MaxPhases = 441;
        const int BlockFrames = 16384;

        /// <summary>True for 44.1 kHz itself and for every rate that can be converted to it.</summary>
        public static bool CanConvert(int rate)
        {
            return rate == TargetRate || (rate >= MinRate && rate <= MaxRate && TargetRate / Gcd(TargetRate, rate) <= MaxPhases);
        }

        /// <summary>
        /// The audio at 44.1 kHz. Audio that is already there is returned as it is, the same object;
        /// anything else is a new one, as long in time, that remembers the rate it came from.
        /// A sum that goes over full scale, which a peak between two samples of a loud file can, is
        /// held at full scale.
        /// </summary>
        public static WavData ToTargetRate(WavData wav, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            if (wav.SampleRate == TargetRate)
                return wav;
            if (!CanConvert(wav.SampleRate))
                throw new NotSupportedException(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "The audio is at {0} Hz, which cannot be converted to 44.1 kHz. The usual rates from 8 to 192 kHz can.", wav.SampleRate));

            int divisor = Gcd(TargetRate, wav.SampleRate);
            // A frame of the result is `down` of these steps after the one before, and a frame of the file is `up` of them.
            int up = TargetRate / divisor, down = wav.SampleRate / divisor;
            int taps;
            double[] weights = Weights(wav.SampleRate, up, out taps);
            int half = taps / 2;

            short[] source = wav.Samples;
            int sourceFrames = wav.Frames;
            int frames = (int)(((long)sourceFrames * up + down - 1) / down);
            var result = new short[2 * frames];
            int blocks = (frames + BlockFrames - 1) / BlockFrames, done = 0;

            Parallel.For(0, blocks, new ParallelOptions { CancellationToken = cancel }, block =>
            {
                int from = block * BlockFrames, to = Math.Min(frames, from + BlockFrames);
                long steps = (long)from * down;
                int whole = (int)(steps / up), phase = (int)(steps % up);
                for (int frame = from; frame < to; frame++)
                {
                    // The moment of this frame is `whole` frames of the file and `phase` parts of the next.
                    int first = whole - half + 1;
                    int row = phase * taps;
                    int tapFrom = first < 0 ? -first : 0, tapTo = Math.Min(taps, sourceFrames - first);
                    double left = 0, right = 0;
                    int at = 2 * (first + tapFrom);
                    for (int tap = tapFrom; tap < tapTo; tap++)
                    {
                        double weight = weights[row + tap];
                        left += weight * source[at];
                        right += weight * source[at + 1];
                        at += 2;
                    }
                    result[2 * frame] = ToSample(left);
                    result[2 * frame + 1] = ToSample(right);

                    phase += down;
                    whole += phase / up;
                    phase %= up;
                }
                if (progress != null)
                    progress.Report((float)Interlocked.Increment(ref done) / blocks);
            });

            return new WavData
            {
                SampleRate = TargetRate,
                Samples = result,
                SourceChannels = wav.SourceChannels,
                SourceBitsPerSample = wav.SourceBitsPerSample,
                SourceSampleRate = wav.SampleRate
            };
        }

        /// <summary>
        /// The weights for a rate: `up` rows of `taps`, a row for each place between two frames of the
        /// file. Row p, weight j, is for the frame of the file that is half - 1 - j + p / up frames
        /// before the moment wanted.
        /// </summary>
        static double[] Weights(int rate, int up, out int taps)
        {
            // Everything is in cycles per frame of the file. Whichever rate is lower sets what can be kept.
            double edge = Math.Min(rate, TargetRate) / 2.0 / rate;
            double width = edge * (1 - PassbandShare);
            double cutoff = edge - width / 2;
            // Kaiser's own figures for how long a filter has to be, and how its window is shaped, for a given depth.
            taps = (int)Math.Ceiling((StopbandDb - 7.95) / (2.285 * 2 * Math.PI * width)) + 1;
            taps += taps & 1;
            int half = taps / 2;
            double shape = 0.1102 * (StopbandDb - 8.7);
            double whole = BesselI0(shape);

            var weights = new double[up * taps];
            for (int phase = 0; phase < up; phase++)
            {
                double sum = 0;
                for (int tap = 0; tap < taps; tap++)
                {
                    double away = (double)phase / up + half - 1 - tap;
                    double across = away / half;
                    double window = BesselI0(shape * Math.Sqrt(Math.Max(0, 1 - across * across))) / whole;
                    double angle = 2 * Math.PI * cutoff * away;
                    double weight = (Math.Abs(angle) < 1e-12 ? 1 : Math.Sin(angle) / angle) * window;
                    weights[phase * taps + tap] = weight;
                    sum += weight;
                }
                // Each row adds up to exactly 1, so a steady level comes through as it went in wherever a frame falls.
                for (int tap = 0; tap < taps; tap++)
                    weights[phase * taps + tap] /= sum;
            }
            return weights;
        }

        static short ToSample(double value)
        {
            double rounded = Math.Floor(value + 0.5);
            return rounded > short.MaxValue ? short.MaxValue : rounded < short.MinValue ? short.MinValue : (short)rounded;
        }

        /// <summary>The modified Bessel function of the first kind, order 0, by its series.</summary>
        static double BesselI0(double x)
        {
            double sum = 1, term = 1, quarter = x * x / 4;
            for (int k = 1; k < 200; k++)
            {
                term *= quarter / ((double)k * k);
                sum += term;
                if (term < sum * 1e-17)
                    break;
            }
            return sum;
        }

        static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                int rest = a % b;
                a = b;
                b = rest;
            }
            return a;
        }
    }
}
