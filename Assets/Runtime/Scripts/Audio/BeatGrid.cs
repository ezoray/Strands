using System;
using System.Collections.Generic;
using System.Threading;

namespace Strands.Audio
{
    /// <summary>A constant-tempo beat grid: a tempo and the position of the first downbeat.</summary>
    public sealed class BeatGrid
    {
        public double Bpm;
        /// <summary>Time of the first beat found in the audio, in seconds.</summary>
        public double FirstBeatSeconds;
        /// <summary>Which of the first four beats (0 to 3) is taken as beat 1 of a bar.</summary>
        public int DownbeatIndex;

        // Diagnostics, for reporting how much to trust the result.
        public double CoarseBpm;
        public int BeatsInAudio;
        public int BeatsMatched;
        public double ResidualMs;
        public double OnsetAlignmentMs;
        /// <summary>Tempo from the onset envelope alone, before the attacks in the samples were used.</summary>
        public double FluxBpm;
        /// <summary>Tempo fitted to the attacks, before a round tempo was preferred.</summary>
        public double FittedBpm;
        /// <summary>How many beats were in groups whose hits could be placed against the grid.</summary>
        public int TransientBeats;
        /// <summary>How far those groups of eight beats sit from the grid lines (RMS).</summary>
        public double TransientSpreadMs;
        public double[] DownbeatScores = new double[4];
        public double DownbeatConfidence;
        public bool BpmWasGiven;
        /// <summary>
        /// Time of the first beat that starts a steady run of matched beats, which is where the drums
        /// (or whatever carries the beat) come in. Negative when no such run was found.
        /// </summary>
        public double FirstSteadyBeatSeconds = -1;
        /// <summary>True when the downbeat was taken from where the drums come in after an intro.</summary>
        public bool DownbeatFromDrums;

        public double BeatSeconds { get { return 60.0 / Bpm; } }
        public double FirstDownbeatSeconds { get { return FirstBeatSeconds + DownbeatIndex * BeatSeconds; } }

        /// <summary>Time of a beat counted from the first downbeat.</summary>
        public double TimeOfBeat(int beatFromDownbeat)
        {
            return FirstDownbeatSeconds + beatFromDownbeat * BeatSeconds;
        }

        // A sound is heard as starting on a beat when it starts a little after the line, or a touch before it.
        public const double OnTheBeatAfter = 0.25, OnTheBeatBefore = 0.1;

        /// <summary>
        /// Which beat of its bar a moment falls on, 1 to 4, and whether it is on that beat or
        /// somewhere between it and the next. Up to a quarter of a beat after a line counts as on
        /// that line's beat, and so does up to a tenth of a beat before it.
        /// </summary>
        public int BeatOfBarAt(double seconds, out bool onTheBeat)
        {
            double beats = (seconds - FirstDownbeatSeconds) / BeatSeconds;
            double inBar = beats - BeatsPerBar * Math.Floor(beats / BeatsPerBar);
            int beat = (int)Math.Floor(inBar);
            double past = inBar - beat;
            if (past >= 1 - OnTheBeatBefore)
            {
                beat = (beat + 1) % BeatsPerBar;
                past = 0;
            }
            onTheBeat = past <= OnTheBeatAfter;
            return beat % BeatsPerBar + 1;
        }

        public BeatGrid Clone()
        {
            var copy = (BeatGrid)MemberwiseClone();
            copy.DownbeatScores = (double[])DownbeatScores.Clone();
            return copy;
        }

        /// <summary>
        /// Brings the first beat back inside the first beat period of the audio without moving any
        /// beat or bar line, so the first downbeat is always the first bar line at or after time 0.
        /// </summary>
        public void Normalize()
        {
            while (FirstBeatSeconds < 0)
            {
                FirstBeatSeconds += BeatSeconds;
                DownbeatIndex = (DownbeatIndex + BeatsPerBar - 1) % BeatsPerBar;
            }
            while (FirstBeatSeconds >= BeatSeconds)
            {
                FirstBeatSeconds -= BeatSeconds;
                DownbeatIndex = (DownbeatIndex + 1) % BeatsPerBar;
            }
        }

        /// <summary>Moves every beat later by this long (earlier when negative).</summary>
        public void Shift(double seconds)
        {
            FirstBeatSeconds += seconds;
            Normalize();
        }

        /// <summary>Moves the bar lines this many beats later (earlier when negative).</summary>
        public void ShiftDownbeat(int beats)
        {
            DownbeatIndex = (((DownbeatIndex + beats) % BeatsPerBar) + BeatsPerBar) % BeatsPerBar;
        }

        /// <summary>
        /// Changes the tempo exactly, with no reference to the audio. The beat nearest
        /// <paramref name="pivotSeconds"/> stays where it is and keeps its place in the bar.
        /// </summary>
        public void SetTempo(double bpm, double pivotSeconds)
        {
            if (!(bpm > 0))
                throw new ArgumentOutOfRangeException("bpm");
            double beatsToPivot = Math.Round((pivotSeconds - FirstBeatSeconds) / BeatSeconds);
            double pivot = FirstBeatSeconds + beatsToPivot * BeatSeconds;
            // Shift the bar phase to the pivot first, so that it survives whatever Normalize does next.
            int pivotInBar = (int)((((long)beatsToPivot - DownbeatIndex) % BeatsPerBar + BeatsPerBar) % BeatsPerBar);
            Bpm = bpm;
            FirstBeatSeconds = pivot;
            DownbeatIndex = (BeatsPerBar - pivotInBar) % BeatsPerBar;
            Normalize();
        }

        const int BeatsPerBar = 4;
    }

    public sealed class BeatGridOptions
    {
        // One octave by default, so a tempo and its half or double cannot both be candidates.
        public double MinBpm = 88;
        public double MaxBpm = 176;
        /// <summary>Use this tempo instead of estimating one (it is still refined against the audio).</summary>
        public double? Bpm;
        /// <summary>Moves the downbeat this many beats later than the automatic guess.</summary>
        public int DownbeatShift;
        /// <summary>When set, the automatic downbeat guess is ignored and this beat (0 to 3) is used.</summary>
        public int? DownbeatIndex;
        /// <summary>Take a round tempo (120 rather than 119.993) when it fits the audio as well as the fitted one.</summary>
        public bool PreferRoundTempo = true;
        /// <summary>When the beat starts part way into the audio, take that beat as the first of a bar.</summary>
        public bool DrumsEnterOnTheOne = true;
    }

    /// <summary>
    /// Finds the beat grid of a constant-tempo track: spectral-flux onsets, a tempo estimate from their
    /// autocorrelation, then a straight-line fit of beat number against onset time.
    /// </summary>
    public static class BeatGridAnalyzer
    {
        const int FftSize = 1024;
        const int Hop = 256;
        const int Bands = 8;
        // Bins up to about 260 Hz, where kick drums and bass notes start.
        const int LowBins = 6;
        const int PhaseBins = 48;
        const int BeatsPerBar = 4;
        // A transient shows the largest frame-to-frame increase when it sits on the rising flank of the
        // window, three quarters of the way through it.
        const double FluxPositionInWindow = 0.75;
        // A round tempo is taken when the attacks sit about as tightly around its grid as around the
        // fitted one. Music made on a computer is nearly always at a round tempo, and a fit that is
        // off by a few thousandths of a BPM drifts by milliseconds over a track.
        const double RoundTempoSlack = 1.1;
        const double RoundTempoSlackSeconds = 0.00015;
        const double RoundTempoMaxDriftSeconds = 0.001;
        static readonly int[] GroupSizes = { 8, 4 };
        static readonly int[] MinPlacedGroups = { 6, 4 };
        const double MaxGroupSpreadSeconds = 0.004;
        const double CutOnBeatSeconds = 0.030;

        public static BeatGrid Analyze(short[] interleavedStereo, int sampleRate, BeatGridOptions options = null, CancellationToken cancel = default(CancellationToken))
        {
            options = options ?? new BeatGridOptions();
            float[] mono = ToMono(interleavedStereo);
            if (mono.Length < sampleRate * 4)
                throw new ArgumentException("At least 4 seconds of audio are needed to find a beat grid.");

            float[][] bandEnergy;
            float[] lowEnvelope;
            float[] envelope = OnsetEnvelope(mono, sampleRate, out lowEnvelope, out bandEnergy, cancel);
            double framesPerSecond = (double)sampleRate / Hop;

            var grid = new BeatGrid();
            double coarsePeriod;
            if (options.Bpm.HasValue)
            {
                grid.BpmWasGiven = true;
                coarsePeriod = framesPerSecond * 60.0 / options.Bpm.Value;
            }
            else
            {
                coarsePeriod = CoarsePeriod(envelope, framesPerSecond, options.MinBpm, options.MaxBpm);
            }
            grid.CoarseBpm = framesPerSecond * 60.0 / coarsePeriod;

            // Fold the envelope at candidate periods; the right one piles every beat into the same phase.
            double period = SearchPeriod(envelope, coarsePeriod * 0.97, coarsePeriod * 1.03, coarsePeriod * 0.0001);
            period = SearchPeriod(envelope, period * 0.9997, period * 1.0003, period * 0.00001);
            double phase = BeatPhase(envelope, lowEnvelope, period);

            List<int> matched = FitLine(envelope, ref period, ref phase, out grid.ResidualMs, framesPerSecond);
            grid.BeatsMatched = matched.Count;

            double beatSeconds = period / framesPerSecond;
            double firstBeat = (phase * Hop + FluxPositionInWindow * FftSize) / sampleRate;

            grid.FluxBpm = 60.0 / beatSeconds;
            double beforeAlignment = firstBeat;
            RefineOnTransients(mono, sampleRate, options, grid, ref firstBeat, ref beatSeconds);
            grid.OnsetAlignmentMs = 1000.0 * (firstBeat - beforeAlignment);

            // Keep the first beat inside the first beat period of the audio.
            long steadyBeat = grid.FirstSteadyBeatSeconds < 0 ? -1 : (long)Math.Round((grid.FirstSteadyBeatSeconds - firstBeat) / beatSeconds);
            long wrapped = 0;
            while (firstBeat < 0)
            {
                firstBeat += beatSeconds;
                wrapped++;
            }
            while (firstBeat >= beatSeconds)
            {
                firstBeat -= beatSeconds;
                wrapped--;
            }
            steadyBeat -= wrapped;

            grid.Bpm = 60.0 / beatSeconds;
            grid.FirstBeatSeconds = firstBeat;
            double duration = (double)mono.Length / sampleRate;
            grid.BeatsInAudio = (int)Math.Floor((duration - firstBeat) / beatSeconds) + 1;

            int guess = GuessDownbeat(bandEnergy, sampleRate, grid, out grid.DownbeatScores, out grid.DownbeatConfidence);
            // When the drums come in after an intro without them, they nearly always come in on
            // the first beat of a bar, which is a better clue than anything in the guess above.
            // A file that starts right on a hit was cut on the beat, and a loop is cut on beat 1.
            // Silence in front does not count: a file is often exported with a few hundredths of a
            // second of nothing before its first sample of music. Nor does a short run of faint
            // sound straight in front of the hit, which a stem from a separator often has.
            int firstSoundFrame = LeadingSilence.FirstSoundFrame(interleavedStereo);
            double firstSound = (double)firstSoundFrame / sampleRate;
            double beforeTheHit = grid.FirstSteadyBeatSeconds - firstSound;
            bool cutOnTheBeat = grid.FirstSteadyBeatSeconds >= 0 && (beforeTheHit < CutOnBeatSeconds
                // The levels of the whole file are only worked out when the hit is near enough for them to matter.
                || (beforeTheHit < beatSeconds && LeadingSilence.IsFaintRunUpTo(new BlockLevels(interleavedStereo), firstSoundFrame, grid.FirstSteadyBeatSeconds, beatSeconds, sampleRate)));
            if (options.DrumsEnterOnTheOne && grid.FirstSteadyBeatSeconds >= 0 && (steadyBeat >= BeatsPerBar || cutOnTheBeat))
            {
                guess = (int)(steadyBeat % BeatsPerBar);
                grid.DownbeatConfidence = 1;
                grid.DownbeatFromDrums = true;
            }
            int downbeat = (options.DownbeatIndex ?? guess) + options.DownbeatShift;
            grid.DownbeatIndex = ((downbeat % BeatsPerBar) + BeatsPerBar) % BeatsPerBar;
            return grid;
        }

        /// <summary>
        /// The onset envelope places beats to within a few milliseconds, and its idea of where an onset
        /// is shifts with the sound, which is enough to tilt the tempo slightly over a whole track.
        /// This corrects tempo and position against the attacks in the samples themselves, then looks
        /// for a round tempo that fits those attacks just as well.
        /// </summary>
        static void RefineOnTransients(float[] mono, int sampleRate, BeatGridOptions options, BeatGrid grid, ref double firstBeat, ref double beatSeconds)
        {
            double startBeat = firstBeat, startSeconds = beatSeconds;
            var index = new List<double>();
            var lags = new List<double>();
            double rms = 0;
            bool fitted = false;
            int group = 0;

            // Groups of eight beats give the steadiest answer. A song with only a short stretch of
            // drums has too few of them, so it gets a second try with groups of four.
            for (int attempt = 0; attempt < GroupSizes.Length && !fitted; attempt++)
            {
                group = GroupSizes[attempt];
                firstBeat = startBeat;
                beatSeconds = startSeconds;
                for (int pass = 0; pass < 4; pass++)
                {
                    long lastBeat = (long)((double)mono.Length / sampleRate / beatSeconds) + 2;
                    BeatProfiles profiles = BeatProfiles.Measure(mono, sampleRate, firstBeat, beatSeconds, 0, lastBeat);
                    int beats;
                    double[][] template = profiles.Template(out beats);
                    fitted = false;
                    if (beats < 2 * group)
                        break;

                    index.Clear();
                    lags.Clear();
                    for (long from = 0; from < lastBeat; from += group)
                    {
                        double match;
                        double lag = profiles.LagSeconds(template, from, from + group, out match);
                        if (double.IsNaN(lag) || match < BeatProfiles.MinMatch)
                            continue;
                        index.Add(from);
                        lags.Add(lag);
                    }
                    double slope = 0, intercept = 0;
                    // A tempo from a handful of groups, or from groups that do not agree, is worth
                    // less than the one the envelope gave.
                    fitted = index.Count >= MinPlacedGroups[attempt]
                        && BeatProfiles.FitLine(index, lags, out slope, out intercept, out rms)
                        && rms <= MaxGroupSpreadSeconds;
                    if (!fitted)
                        break;
                    // The last pass only measures what is left, so the figures describe the final grid.
                    if (pass == 3)
                        break;
                    // A group's lag belongs to its middle beat.
                    firstBeat += intercept - slope * (group - 1) / 2.0;
                    beatSeconds += slope;
                    double edge = profiles.EdgeSeconds();
                    if (!double.IsNaN(edge))
                        firstBeat += edge;
                }
            }
            if (!fitted)
            {
                firstBeat = startBeat;
                beatSeconds = startSeconds;
            }
            grid.FittedBpm = 60.0 / beatSeconds;
            if (fitted)
            {
                grid.TransientBeats = index.Count * group;
                grid.TransientSpreadMs = 1000.0 * rms;
                if (options.PreferRoundTempo)
                    TakeRoundTempo(grid, index, lags, rms, group, ref firstBeat, ref beatSeconds);
            }

            // Last, put the lines on the front of the average hit at the tempo just settled on. The
            // drift meter uses the same definition, so a freshly analysed grid reads as on the beat.
            // Moving the lines moves the window the hits are looked at through, which can change
            // the answer a little, so this is repeated until it settles.
            long end = (long)((double)mono.Length / sampleRate / beatSeconds) + 2;
            // From the first beat inside the audio, which may be before the one counted as beat 0.
            long begin = -(long)Math.Floor(firstBeat / beatSeconds);
            BeatProfiles final = BeatProfiles.Measure(mono, sampleRate, firstBeat, beatSeconds, begin, end);
            for (int round = 0; round < 6; round++)
            {
                double front = final.EdgeSeconds();
                if (double.IsNaN(front) || Math.Abs(front) < 0.0002)
                    break;
                firstBeat += front;
                begin = -(long)Math.Floor(firstBeat / beatSeconds);
                final = BeatProfiles.Measure(mono, sampleRate, firstBeat, beatSeconds, begin, end);
            }
            long steady = final.FirstSteadyBeat();
            if (steady != BeatProfiles.NoBeat)
                grid.FirstSteadyBeatSeconds = Math.Max(0, firstBeat + steady * beatSeconds);
        }

        static void TakeRoundTempo(BeatGrid grid, List<double> index, List<double> lags, double rms, int group, ref double firstBeat, ref double beatSeconds)
        {
            for (int decimals = 0; decimals <= 2; decimals++)
            {
                double candidate = Math.Round(grid.FittedBpm, decimals);
                double change = 60.0 / candidate - beatSeconds;
                if (change == 0)
                    return;
                double mean = 0;
                for (int i = 0; i < index.Count; i++)
                    mean += lags[i] - change * index[i];
                mean /= index.Count;
                double sumSquares = 0;
                for (int i = 0; i < index.Count; i++)
                {
                    double r = lags[i] - change * index[i] - mean;
                    sumSquares += r * r;
                }
                double candidateRms = Math.Sqrt(sumSquares / index.Count);
                // Over a short stretch of beats two close tempos cannot be told apart at all: when the
                // round one ends up within a millisecond of the fitted one, it is as good.
                double beatsSpanned = index[index.Count - 1] - index[0] + group;
                bool indistinguishable = Math.Abs(change) * beatsSpanned <= RoundTempoMaxDriftSeconds;
                if (indistinguishable || candidateRms <= RoundTempoSlack * rms + RoundTempoSlackSeconds)
                {
                    beatSeconds = 60.0 / candidate;
                    firstBeat += mean - change * (group - 1) / 2.0;
                    grid.TransientSpreadMs = 1000.0 * candidateRms;
                    return;
                }
            }
        }

        internal static float[] ToMono(short[] interleavedStereo)
        {
            var mono = new float[interleavedStereo.Length / 2];
            for (int i = 0; i < mono.Length; i++)
                mono[i] = (interleavedStereo[2 * i] + interleavedStereo[2 * i + 1]) / 65536f;
            return mono;
        }

        /// <summary>
        /// Spectral flux of the log-magnitude spectrum, with the local mean removed so that only
        /// onsets stand out. Also returns the same thing for the bass alone, and the energy in eight
        /// octave-wide bands for each frame.
        /// </summary>
        static float[] OnsetEnvelope(float[] mono, int sampleRate, out float[] lowEnvelope, out float[][] bandEnergy, CancellationToken cancel)
        {
            int frames = (mono.Length - FftSize) / Hop + 1;
            int bins = FftSize / 2 + 1;
            var fft = new Fft(FftSize);
            var window = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
                window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FftSize));

            var re = new float[FftSize];
            var im = new float[FftSize];
            var previous = new float[bins];
            var flux = new float[frames];
            var lowFlux = new float[frames];
            bandEnergy = new float[Bands][];
            for (int band = 0; band < Bands; band++)
                bandEnergy[band] = new float[frames];

            for (int frame = 0; frame < frames; frame++)
            {
                if ((frame & 1023) == 0)
                    cancel.ThrowIfCancellationRequested();
                int start = frame * Hop;
                for (int i = 0; i < FftSize; i++)
                {
                    re[i] = mono[start + i] * window[i];
                    im[i] = 0;
                }
                fft.Transform(re, im);

                float sum = 0, lowSum = 0;
                for (int bin = 1; bin < bins; bin++)
                {
                    float power = re[bin] * re[bin] + im[bin] * im[bin];
                    float level = (float)Math.Log(1.0 + Math.Sqrt(power));
                    float rise = level - previous[bin];
                    if (rise > 0)
                    {
                        sum += rise;
                        if (bin <= LowBins)
                            lowSum += rise;
                    }
                    previous[bin] = level;
                    bandEnergy[BandOfBin(bin)][frame] += power;
                }
                flux[frame] = frame == 0 ? 0 : sum;
                lowFlux[frame] = frame == 0 ? 0 : lowSum;
            }

            int half = (int)(0.4 * sampleRate / Hop);
            lowEnvelope = AboveLocalMean(lowFlux, half);
            return AboveLocalMean(flux, half);
        }

        /// <summary>Subtracts the mean of the surrounding frames and keeps what rises above it.</summary>
        static float[] AboveLocalMean(float[] values, int half)
        {
            int frames = values.Length;
            var prefix = new double[frames + 1];
            for (int i = 0; i < frames; i++)
                prefix[i + 1] = prefix[i] + values[i];
            var result = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                int from = Math.Max(0, i - half), to = Math.Min(frames, i + half + 1);
                float mean = (float)((prefix[to] - prefix[from]) / (to - from));
                result[i] = Math.Max(0, values[i] - mean);
            }
            return result;
        }

        // Bins 1, 2-3, 4-7, 8-15, 16-31, 32-63, 64-127, 128 and up.
        static int BandOfBin(int bin)
        {
            int band = 0;
            while (bin > 1 && band < Bands - 1)
            {
                bin >>= 1;
                band++;
            }
            return band;
        }

        /// <summary>
        /// Beat period in envelope frames. Each candidate is scored by the autocorrelation at one,
        /// two, three and four times its period, which favours the beat over its subdivisions.
        /// </summary>
        static double CoarsePeriod(float[] envelope, double framesPerSecond, double minBpm, double maxBpm)
        {
            double minPeriod = framesPerSecond * 60.0 / maxBpm;
            double maxPeriod = framesPerSecond * 60.0 / minBpm;
            int maxLag = Math.Min(envelope.Length - 1, (int)Math.Ceiling(maxPeriod * 4) + 2);
            var acf = new double[maxLag + 1];
            for (int lag = 1; lag <= maxLag; lag++)
            {
                double sum = 0;
                int count = envelope.Length - lag;
                for (int i = 0; i < count; i++)
                    sum += envelope[i] * envelope[i + lag];
                acf[lag] = sum / count;
            }

            double bestPeriod = minPeriod, bestScore = double.MinValue;
            for (double period = minPeriod; period < maxPeriod; period += 0.125)
            {
                double score = 0;
                for (int multiple = 1; multiple <= 4; multiple++)
                    score += Interpolate(acf, period * multiple);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPeriod = period;
                }
            }
            return bestPeriod;
        }

        static double Interpolate(double[] values, double position)
        {
            int index = (int)position;
            if (index + 1 >= values.Length)
                return 0;
            double fraction = position - index;
            return values[index] * (1 - fraction) + values[index + 1] * fraction;
        }

        /// <summary>The period in the range that gathers the most envelope into one phase.</summary>
        static double SearchPeriod(float[] envelope, double from, double to, double step)
        {
            var histogram = new double[PhaseBins];
            double bestPeriod = from, bestScore = double.MinValue;
            for (double period = from; period <= to; period += step)
            {
                Fold(envelope, period, histogram);
                for (int bin = 0; bin < PhaseBins; bin++)
                {
                    double score = histogram[bin] + 0.5 * (histogram[(bin + PhaseBins - 1) % PhaseBins] + histogram[(bin + 1) % PhaseBins]);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPeriod = period;
                    }
                }
            }
            return bestPeriod;
        }

        /// <summary>
        /// Position of the first beat, in frames. Off-beat hi-hats and claps can out-shout the kick across
        /// the whole spectrum, so the bass onsets are counted twice over when choosing where the beat falls.
        /// </summary>
        static double BeatPhase(float[] envelope, float[] lowEnvelope, double period)
        {
            var full = new double[PhaseBins];
            var low = new double[PhaseBins];
            double fullTotal = Fold(envelope, period, full);
            double lowTotal = Fold(lowEnvelope, period, low);
            var combined = new double[PhaseBins];
            for (int bin = 0; bin < PhaseBins; bin++)
                combined[bin] = (fullTotal > 0 ? full[bin] / fullTotal : 0) + 2 * (lowTotal > 0 ? low[bin] / lowTotal : 0);

            int best = 0;
            double bestScore = double.MinValue;
            for (int bin = 0; bin < PhaseBins; bin++)
            {
                double score = combined[bin] + 0.5 * (combined[(bin + PhaseBins - 1) % PhaseBins] + combined[(bin + 1) % PhaseBins]);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = bin;
                }
            }
            double before = combined[(best + PhaseBins - 1) % PhaseBins], after = combined[(best + 1) % PhaseBins];
            double total = before + combined[best] + after;
            double centroid = total > 0 ? (after - before) / total : 0;
            double phase = (best + 0.5 + centroid) / PhaseBins * period;
            return phase - period * Math.Floor(phase / period);
        }

        /// <summary>Adds each frame of the envelope to the bin for its position within the period.</summary>
        static double Fold(float[] envelope, double period, double[] histogram)
        {
            Array.Clear(histogram, 0, PhaseBins);
            double scale = PhaseBins / period, total = 0;
            for (int i = 0; i < envelope.Length; i++)
            {
                float value = envelope[i];
                if (value <= 0)
                    continue;
                double position = i * scale;
                int bin = (int)(position - PhaseBins * Math.Floor(position / PhaseBins));
                histogram[bin < PhaseBins ? bin : PhaseBins - 1] += value;
                total += value;
            }
            return total;
        }

        /// <summary>
        /// Picks the onset nearest each predicted beat and fits beat number against onset position by
        /// least squares, dropping outliers. Returns the beat numbers that were used.
        /// </summary>
        static List<int> FitLine(float[] envelope, ref double period, ref double phase, out double residualMs, double framesPerSecond)
        {
            int reach = Math.Max(2, (int)Math.Round(0.035 * framesPerSecond));
            var beats = new List<int>();
            residualMs = 0;

            for (int pass = 0; pass < 3; pass++)
            {
                var candidateBeats = new List<int>();
                var candidatePositions = new List<double>();
                var candidateHeights = new List<float>();
                for (int beat = 0; ; beat++)
                {
                    double predicted = phase + beat * period;
                    int centre = (int)Math.Round(predicted);
                    if (centre + reach + 1 >= envelope.Length)
                        break;
                    if (centre - reach - 1 < 0)
                        continue;

                    int peak = centre - reach;
                    for (int i = centre - reach; i <= centre + reach; i++)
                        if (envelope[i] > envelope[peak])
                            peak = i;
                    // A maximum on the edge of the search range belongs to some other onset.
                    if (peak == centre - reach || peak == centre + reach || envelope[peak] <= 0)
                        continue;

                    float left = envelope[peak - 1], middle = envelope[peak], right = envelope[peak + 1];
                    float curvature = left - 2 * middle + right;
                    double offset = curvature < 0 ? 0.5 * (left - right) / curvature : 0;
                    candidateBeats.Add(beat);
                    candidatePositions.Add(peak + offset);
                    candidateHeights.Add(middle);
                }
                if (candidateBeats.Count < 8)
                    break;

                // Ignore onsets much weaker than a typical beat.
                var sorted = new List<float>(candidateHeights);
                sorted.Sort();
                float floor = 0.3f * sorted[sorted.Count / 2];
                var x = new List<double>();
                var y = new List<double>();
                for (int i = 0; i < candidateBeats.Count; i++)
                {
                    if (candidateHeights[i] < floor)
                        continue;
                    x.Add(candidateBeats[i]);
                    y.Add(candidatePositions[i]);
                }
                if (x.Count < 8)
                    break;

                double slope = period, intercept = phase, rms = 0;
                for (int round = 0; round < 4; round++)
                {
                    LeastSquares(x, y, out slope, out intercept);
                    double sumSquares = 0;
                    for (int i = 0; i < x.Count; i++)
                    {
                        double r = y[i] - (intercept + slope * x[i]);
                        sumSquares += r * r;
                    }
                    rms = Math.Sqrt(sumSquares / x.Count);
                    double limit = Math.Max(0.75, 2.5 * rms);
                    for (int i = x.Count - 1; i >= 0 && x.Count > 8; i--)
                    {
                        if (Math.Abs(y[i] - (intercept + slope * x[i])) > limit)
                        {
                            x.RemoveAt(i);
                            y.RemoveAt(i);
                        }
                    }
                }

                period = slope;
                phase = intercept;
                residualMs = 1000.0 * rms / framesPerSecond;
                beats.Clear();
                foreach (double beat in x)
                    beats.Add((int)beat);
            }
            return beats;
        }

        static void LeastSquares(List<double> x, List<double> y, out double slope, out double intercept)
        {
            double meanX = 0, meanY = 0;
            for (int i = 0; i < x.Count; i++)
            {
                meanX += x[i];
                meanY += y[i];
            }
            meanX /= x.Count;
            meanY /= x.Count;
            double sxx = 0, sxy = 0;
            for (int i = 0; i < x.Count; i++)
            {
                sxx += (x[i] - meanX) * (x[i] - meanX);
                sxy += (x[i] - meanX) * (y[i] - meanY);
            }
            slope = sxy / sxx;
            intercept = meanY - slope * meanX;
        }

        /// <summary>
        /// Guesses which beat starts the bar. Arrangement changes (a part entering or leaving) tend to
        /// land on beat 1, so for each beat this measures how much its spectrum differs from the same
        /// beat one bar earlier, and credits the beat where that difference first jumps.
        /// </summary>
        static int GuessDownbeat(float[][] bandEnergy, int sampleRate, BeatGrid grid, out double[] scores, out double confidence)
        {
            scores = new double[BeatsPerBar];
            confidence = 0;
            int frames = bandEnergy[0].Length;
            int beats = grid.BeatsInAudio - 1;
            if (beats < 3 * BeatsPerBar)
                return 0;

            var level = new double[beats][];
            for (int beat = 0; beat < beats; beat++)
            {
                double from = grid.FirstBeatSeconds + beat * grid.BeatSeconds;
                int first = Math.Max(0, (int)Math.Ceiling((from * sampleRate - FftSize / 2) / Hop));
                int last = Math.Min(frames, (int)Math.Ceiling(((from + grid.BeatSeconds) * sampleRate - FftSize / 2) / Hop));
                level[beat] = new double[Bands];
                for (int band = 0; band < Bands; band++)
                {
                    double sum = 0;
                    for (int frame = first; frame < last; frame++)
                        sum += bandEnergy[band][frame];
                    level[beat][band] = Math.Log(1e-6 + sum / Math.Max(1, last - first));
                }
            }

            var change = new double[beats];
            for (int beat = BeatsPerBar; beat < beats; beat++)
                for (int band = 0; band < Bands; band++)
                    change[beat] += Math.Abs(level[beat][band] - level[beat - BeatsPerBar][band]);

            var counts = new int[BeatsPerBar];
            for (int beat = BeatsPerBar + 1; beat < beats; beat++)
            {
                scores[beat % BeatsPerBar] += Math.Max(0, change[beat] - change[beat - 1]);
                counts[beat % BeatsPerBar]++;
            }

            int bestIndex = 0;
            for (int i = 0; i < BeatsPerBar; i++)
            {
                if (counts[i] > 0)
                    scores[i] /= counts[i];
                if (scores[i] > scores[bestIndex])
                    bestIndex = i;
            }
            double second = 0;
            for (int i = 0; i < BeatsPerBar; i++)
                if (i != bestIndex && scores[i] > second)
                    second = scores[i];
            confidence = scores[bestIndex] > 0 ? (scores[bestIndex] - second) / scores[bestIndex] : 0;
            return bestIndex;
        }
    }

    /// <summary>In-place radix-2 FFT for one fixed power-of-two size.</summary>
    sealed class Fft
    {
        readonly int size;
        readonly int[] reversed;
        readonly float[] cos;
        readonly float[] sin;

        public Fft(int size)
        {
            this.size = size;
            int bits = 0;
            while ((1 << bits) < size)
                bits++;
            if ((1 << bits) != size)
                throw new ArgumentException("FFT size must be a power of two.");

            reversed = new int[size];
            for (int i = 0; i < size; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++)
                    if ((i & (1 << b)) != 0)
                        r |= 1 << (bits - 1 - b);
                reversed[i] = r;
            }
            cos = new float[size / 2];
            sin = new float[size / 2];
            for (int i = 0; i < size / 2; i++)
            {
                cos[i] = (float)Math.Cos(2 * Math.PI * i / size);
                sin[i] = (float)Math.Sin(2 * Math.PI * i / size);
            }
        }

        public void Transform(float[] re, float[] im)
        {
            for (int i = 0; i < size; i++)
            {
                int j = reversed[i];
                if (j > i)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int length = 2; length <= size; length <<= 1)
            {
                int half = length / 2, stride = size / length;
                for (int start = 0; start < size; start += length)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float wr = cos[k * stride], wi = -sin[k * stride];
                        int a = start + k, b = a + half;
                        float xr = re[b] * wr - im[b] * wi;
                        float xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr;
                        im[b] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;
                    }
                }
            }
        }
    }
}
