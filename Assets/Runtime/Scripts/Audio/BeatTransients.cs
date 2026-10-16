using System;
using System.Collections.Generic;

namespace Strands.Audio
{
    /// <summary>
    /// Looks at the samples around every grid line to see where the beat really is. A single hit is
    /// an unreliable guide (its loudest rise may be anywhere in the body of the drum, and other
    /// sounds land near it), so hits are averaged: the average loudness curve of many beats has one
    /// clear climb, and a group of beats is placed by sliding its own curve along the average of
    /// the whole song until they match.
    /// </summary>
    internal sealed class BeatProfiles
    {
        const int Step = 16;            // samples between points of a curve (0.36 ms)
        const int Box = 128;            // samples each loudness value is averaged over (2.9 ms)
        const int RiseSpan = 4;         // points either side that a rise is measured across
        const double ReachSeconds = 0.060;
        const double MaxLagSeconds = 0.025;
        const double MinClimb = 0.2;
        const double TopReachSeconds = 0.030;
        const double SteadyReachSeconds = 0.030;
        const double HardLevel = 0.4;
        const int SteadyWindow = 8, SteadyHits = 4;
        const int SteadyLongWindow = 16, SteadyLongHits = 8;
        const double OnGridSeconds = 0.006;
        const int SteadyFromStartBeats = 8;
        /// <summary>The beat is put where the average hit has made between this much and this much of its climb.</summary>
        const double EdgeFrom = 0.1, EdgeTo = 0.5, EdgeStep = 0.05;
        /// <summary>A group of beats whose curve matches the song's less well than this is not placed.</summary>
        public const double MinMatch = 0.5;
        /// <summary>What <see cref="FirstSteadyBeat"/> returns when it finds nothing.</summary>
        public const long NoBeat = long.MinValue;

        readonly int sampleRate;
        readonly int points;
        readonly long firstBeat;
        // Per beat, or null when the beat is too close to an end of the audio.
        readonly List<float[]> loudness = new List<float[]>();
        readonly List<float[]> rises = new List<float[]>();
        bool[] hard;

        BeatProfiles(int sampleRate, long firstBeat)
        {
            this.sampleRate = sampleRate;
            this.firstBeat = firstBeat;
            points = (int)(2 * ReachSeconds * sampleRate / Step);
        }

        /// <summary>Measures the beats from <paramref name="fromBeat"/> up to <paramref name="toBeat"/>, counted from <paramref name="firstBeatSeconds"/>.</summary>
        public static BeatProfiles Measure(float[] mono, int sampleRate, double firstBeatSeconds, double beatSeconds, long fromBeat, long toBeat)
        {
            var profiles = new BeatProfiles(sampleRate, fromBeat);
            int points = profiles.points;
            int values = points + 2 * RiseSpan;
            int length = values * Step + Box;
            var sums = new double[length + 1];
            var level = new float[values];

            for (long beat = fromBeat; beat < toBeat; beat++)
            {
                int centre = (int)Math.Round((firstBeatSeconds + beat * beatSeconds) * sampleRate);
                int start = centre - (int)(ReachSeconds * sampleRate) - RiseSpan * Step;
                if (centre < 0 || centre >= mono.Length)
                {
                    profiles.loudness.Add(null);
                    profiles.rises.Add(null);
                    continue;
                }
                // A beat right at the start or end of the audio is measured too, with silence standing
                // in for what lies outside. A file cut on the beat has its first hit there.
                for (int i = 0; i < length; i++)
                {
                    int at = start + i;
                    float value = at >= 0 && at < mono.Length ? mono[at] : 0;
                    sums[i + 1] = sums[i] + value * value;
                }
                for (int j = 0; j < values; j++)
                    level[j] = (float)Math.Sqrt((sums[j * Step + Box] - sums[j * Step]) / Box);

                var loud = new float[points];
                var rise = new float[points];
                for (int j = 0; j < points; j++)
                {
                    loud[j] = level[j + RiseSpan];
                    rise[j] = Math.Max(0, level[j + 2 * RiseSpan] - level[j]);
                }
                profiles.loudness.Add(loud);
                profiles.rises.Add(rise);
            }
            profiles.FindHardBeats();
            return profiles;
        }

        /// <summary>
        /// Marks the beats with a hard hit on or near them. Hard is judged against the hardest tenth
        /// of the song, so the soft movement of an intro without drums does not count. Only these
        /// beats shape the average hit, and only groups with enough of them are placed.
        /// </summary>
        void FindHardBeats()
        {
            var strengths = new double[rises.Count];
            var sorted = new List<double>(rises.Count);
            for (int i = 0; i < rises.Count; i++)
            {
                strengths[i] = Strength(i);
                sorted.Add(strengths[i]);
            }
            hard = new bool[rises.Count];
            if (sorted.Count == 0)
                return;
            sorted.Sort();
            double level = HardLevel * sorted[sorted.Count * 9 / 10];
            for (int i = 0; i < hard.Length; i++)
                hard[i] = level > 0 && strengths[i] >= level;
        }

        int HardBeats(long fromBeat, long toBeat)
        {
            int count = 0;
            for (long beat = Math.Max(fromBeat, firstBeat); beat < toBeat && beat - firstBeat < hard.Length; beat++)
                if (hard[beat - firstBeat])
                    count++;
            return count;
        }

        /// <summary>Time of point <paramref name="index"/> of a curve relative to its grid line.</summary>
        double SecondsAt(double index)
        {
            return (index * Step + Box / 2.0) / sampleRate - ReachSeconds;
        }

        double[] Mean(List<float[]> curves, long fromBeat, long toBeat, bool hardOnly, out int count)
        {
            var mean = new double[points];
            count = 0;
            for (long beat = Math.Max(fromBeat, firstBeat); beat < toBeat && beat - firstBeat < curves.Count; beat++)
            {
                float[] curve = curves[(int)(beat - firstBeat)];
                if (curve == null || (hardOnly && !hard[beat - firstBeat]))
                    continue;
                for (int j = 0; j < points; j++)
                    mean[j] += curve[j];
                count++;
            }
            if (count > 0)
                for (int j = 0; j < points; j++)
                    mean[j] /= count;
            return mean;
        }

        /// <summary>
        /// The average hit of every measured beat, as a rise curve and a loudness curve: the shapes
        /// that groups of beats are matched to.
        /// </summary>
        public double[][] Template(out int beats)
        {
            return new[]
            {
                Mean(rises, firstBeat, firstBeat + rises.Count, true, out beats),
                Mean(loudness, firstBeat, firstBeat + loudness.Count, true, out beats)
            };
        }

        /// <summary>How hard the hit on a beat is: the biggest rise in loudness close to its grid line.</summary>
        double Strength(int beatIndex)
        {
            float[] rise = rises[beatIndex];
            if (rise == null)
                return 0;
            int from = Math.Max(0, (int)((ReachSeconds - SteadyReachSeconds) * sampleRate / Step));
            int to = Math.Min(points, (int)((ReachSeconds + SteadyReachSeconds) * sampleRate / Step));
            float strongest = 0;
            for (int j = from; j < to; j++)
                strongest = Math.Max(strongest, rise[j]);
            return strongest;
        }

        /// <summary>
        /// The beat where the drums come in: the first beat with a hard hit sitting on its grid line
        /// that is followed by a sustained run of the same. A sample or a voice in an intro can be as
        /// loud as the drums, but its hits do not keep landing on the lines, so it is passed over.
        /// Returns <see cref="NoBeat"/> when there is no such beat.
        /// </summary>
        public long FirstSteadyBeat()
        {
            int beats;
            double[][] template = Template(out beats);
            if (beats == 0)
                return NoBeat;
            var onGrid = new bool[hard.Length];
            for (int i = 0; i < hard.Length; i++)
            {
                if (!hard[i])
                    continue;
                double match;
                double lag = Lag(template, firstBeat + i, firstBeat + i + 1, 1, 1, out match);
                onGrid[i] = !double.IsNaN(lag) && match >= MinMatch && Math.Abs(lag) <= OnGridSeconds;
            }
            for (int i = 0; i + SteadyLongWindow <= onGrid.Length; i++)
            {
                if (!onGrid[i])
                    continue;
                int soon = 0, sustained = 0;
                for (int j = i; j < i + SteadyLongWindow; j++)
                {
                    if (!onGrid[j])
                        continue;
                    sustained++;
                    if (j < i + SteadyWindow)
                        soon++;
                }
                if (soon >= SteadyHits && sustained >= SteadyLongHits)
                {
                    // When the run starts within the first couple of bars, the drums were there from
                    // the start: an earlier hard hit that was a little off its line is part of them.
                    if (i < SteadyFromStartBeats)
                        for (int j = 0; j < i; j++)
                            if (hard[j])
                                return firstBeat + j;
                    return firstBeat + i;
                }
            }
            // Loose or busy drumming may never put enough single hits squarely on the lines. Then
            // the first hard beat with more hard beats after it is the best there is.
            for (int i = 0; i + SteadyWindow <= hard.Length; i++)
                if (hard[i] && HardBeats(firstBeat + i, firstBeat + i + SteadyWindow) >= SteadyHits)
                    return firstBeat + i;
            return NoBeat;
        }

        /// <summary>
        /// Where the average hit starts, relative to the grid lines (positive when the audio is late):
        /// the point where the average loudness curve has made part of its steepest climb.
        /// NaN when the curve has no climb to speak of.
        /// </summary>
        public double EdgeSeconds()
        {
            int count;
            double[] mean = Mean(loudness, firstBeat, firstBeat + loudness.Count, true, out count);
            if (count == 0)
                return double.NaN;
            // From the quietest point before the loudest one, the first place the curve has made
            // part of the climb between them. Taking the first such place, not the steepest stretch,
            // keeps the answer on the front of the hit when the body of the drum rises faster later.
            // The loudest point is looked for close to the line; something louder further off
            // belongs to another sound.
            int last = Math.Min(points, (int)((ReachSeconds + TopReachSeconds) * sampleRate / Step));
            int top = 0;
            for (int j = 1; j < last; j++)
                if (mean[j] > mean[top])
                    top = j;
            int low = 0;
            for (int j = 1; j < top; j++)
                if (mean[j] < mean[low])
                    low = j;
            double climb = mean[top] - mean[low];
            // A climb that is a small part of the level it sits on is a ripple, not a hit.
            if (top == 0 || climb < MinClimb * mean[top])
                return double.NaN;
            // One level would jump about when the front of the hit has a shoulder near that level,
            // so the crossings of a spread of levels are averaged.
            double total = 0;
            int levels = 0;
            for (double part = EdgeFrom; part <= EdgeTo + 1e-9; part += EdgeStep)
            {
                double level = mean[low] + part * climb;
                for (int j = low + 1; j <= top; j++)
                {
                    if (mean[j] < level)
                        continue;
                    double within = mean[j] > mean[j - 1] ? (level - mean[j - 1]) / (mean[j] - mean[j - 1]) : 0;
                    total += SecondsAt(j - 1 + within);
                    levels++;
                    break;
                }
            }
            return levels == 0 ? double.NaN : total / levels;
        }

        /// <summary>
        /// How much later than the song's average hit the beats from <paramref name="fromBeat"/> up to
        /// <paramref name="toBeat"/> are, found by sliding their average rise curve along the template.
        /// <paramref name="match"/> is how alike the two curves are at that shift (1 is identical);
        /// below <see cref="MinMatch"/> the answer means nothing.
        /// </summary>
        public double LagSeconds(double[][] template, long fromBeat, long toBeat, out double match)
        {
            // A group needs enough hard hits to have a beat to place; a syncopated drum part does
            // not hit every beat, so not all of them are asked for.
            return Lag(template, fromBeat, toBeat, (int)Math.Max(2, (toBeat - fromBeat) / 2), (int)Math.Max(2, (toBeat - fromBeat) * 3 / 8), out match);
        }

        double Lag(double[][] template, long fromBeat, long toBeat, int minBeats, int minHardBeats, out double match)
        {
            match = 0;
            int count;
            double[][] curves = { Mean(rises, fromBeat, toBeat, false, out count), Mean(loudness, fromBeat, toBeat, false, out count) };
            if (count < minBeats || HardBeats(fromBeat, toBeat) < minHardBeats)
                return double.NaN;

            // The rise curve gives a sharp answer, but the body of a drum rings, so it matches nearly
            // as well one cycle of that ring early or late. The loudness curve is a plain step with
            // no such repeats. Adding the two keeps the sharpness and loses the ambiguity.
            int maxLag = (int)(MaxLagSeconds * sampleRate / Step);
            var scores = new double[2 * maxLag + 1];
            for (int kind = 0; kind < 2; kind++)
            {
                double[] curve = curves[kind], reference = template[kind];
                double curveMean = 0, referenceMean = 0;
                for (int j = 0; j < points; j++)
                {
                    curveMean += curve[j];
                    referenceMean += reference[j];
                }
                curveMean /= points;
                referenceMean /= points;
                double curveSize = 0, referenceSize = 0;
                for (int j = 0; j < points; j++)
                {
                    curveSize += (curve[j] - curveMean) * (curve[j] - curveMean);
                    referenceSize += (reference[j] - referenceMean) * (reference[j] - referenceMean);
                }
                double size = Math.Sqrt(curveSize * referenceSize);
                if (size <= 0)
                    return double.NaN;
                for (int lag = -maxLag; lag <= maxLag; lag++)
                {
                    double sum = 0;
                    for (int j = Math.Max(0, -lag); j < Math.Min(points, points - lag); j++)
                        sum += (curve[j + lag] - curveMean) * (reference[j] - referenceMean);
                    scores[lag + maxLag] += 0.5 * sum / size;
                }
            }

            int bestIndex = 0;
            for (int i = 1; i < scores.Length; i++)
                if (scores[i] > scores[bestIndex])
                    bestIndex = i;
            match = scores[bestIndex];
            double fraction = 0;
            if (bestIndex > 0 && bestIndex < scores.Length - 1)
            {
                double curvature = scores[bestIndex - 1] - 2 * scores[bestIndex] + scores[bestIndex + 1];
                if (curvature < 0)
                    fraction = 0.5 * (scores[bestIndex - 1] - scores[bestIndex + 1]) / curvature;
            }
            return (bestIndex - maxLag + fraction) * Step / sampleRate;
        }

        /// <summary>
        /// A straight line through values that may contain a few far from the rest. Points further than
        /// three typical distances from the line are dropped and the line fitted again.
        /// Returns false with fewer than three points.
        /// </summary>
        public static bool FitLine(List<double> x, List<double> y, out double slope, out double intercept, out double rms)
        {
            slope = intercept = rms = 0;
            if (x.Count < 3)
                return false;
            for (int round = 0; round < 6; round++)
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
                slope = sxx > 0 ? sxy / sxx : 0;
                intercept = meanY - slope * meanX;

                var distances = new List<double>(x.Count);
                double sumSquares = 0;
                for (int i = 0; i < x.Count; i++)
                {
                    double r = y[i] - (intercept + slope * x[i]);
                    sumSquares += r * r;
                    distances.Add(Math.Abs(r));
                }
                rms = Math.Sqrt(sumSquares / x.Count);
                distances.Sort();
                double limit = Math.Max(0.001, 3 * 1.4826 * distances[distances.Count / 2]);
                int before = x.Count;
                for (int i = x.Count - 1; i >= 0 && x.Count > 3; i--)
                {
                    if (Math.Abs(y[i] - (intercept + slope * x[i])) > limit)
                    {
                        x.RemoveAt(i);
                        y.RemoveAt(i);
                    }
                }
                if (x.Count == before)
                    break;
            }
            return true;
        }
    }

    /// <summary>
    /// Reports how far each stretch of a song is from a grid: for every group of beats, the distance
    /// from the grid lines to where the hits in the audio start.
    /// </summary>
    public sealed class BeatDriftMeter
    {
        // How long either side of a line the level is taken over for AttackOnLines. It is kept
        // short: with 8 ms a grid whose lines sat 6 ms after the kicks scored nearly as well as
        // the grid on them, and with 16 ms there was little between the two.
        const double AttackSpanSeconds = 0.004;

        readonly float[] mono;
        readonly int sampleRate;

        public BeatDriftMeter(short[] interleavedStereo, int sampleRate)
        {
            mono = BeatGridAnalyzer.ToMono(interleavedStereo);
            this.sampleRate = sampleRate;
        }

        /// <summary>
        /// The offset in milliseconds of each of <paramref name="segments"/> consecutive groups of
        /// <paramref name="beatsPerSegment"/> beats, the first starting <paramref name="startBeat"/> beats
        /// after the first downbeat. Positive means the audio is later than the grid. NaN where a group
        /// has no clear beat to measure, as in a breakdown.
        /// </summary>
        public double[] Measure(BeatGrid grid, int startBeat, int beatsPerSegment, int segments)
        {
            var starts = new long[segments];
            var lengths = new int[segments];
            for (int segment = 0; segment < segments; segment++)
            {
                starts[segment] = startBeat + (long)beatsPerSegment * segment;
                lengths[segment] = beatsPerSegment;
            }
            return Measure(grid, starts, lengths);
        }

        /// <summary>The same for groups of any length: group i starts <paramref name="startBeats"/>[i] beats after the first downbeat and holds <paramref name="beatCounts"/>[i] beats.</summary>
        public double[] Measure(BeatGrid grid, long[] startBeats, int[] beatCounts)
        {
            int segments = startBeats.Length;
            var result = new double[segments];
            // Every beat in the song goes into the average, so the offsets do not depend on where the package starts.
            long first = -(long)Math.Floor(grid.FirstDownbeatSeconds / grid.BeatSeconds);
            long last = (long)(((double)mono.Length / sampleRate - grid.FirstDownbeatSeconds) / grid.BeatSeconds) + 1;
            BeatProfiles profiles = BeatProfiles.Measure(mono, sampleRate, grid.FirstDownbeatSeconds, grid.BeatSeconds, first, last);
            int beats;
            double[][] template = profiles.Template(out beats);
            double edge = beats == 0 ? double.NaN : profiles.EdgeSeconds();
            for (int segment = 0; segment < segments; segment++)
            {
                long from = startBeats[segment];
                double match = 0;
                double lag = double.IsNaN(edge) ? double.NaN : profiles.LagSeconds(template, from, from + beatCounts[segment], out match);
                result[segment] = double.IsNaN(lag) || match < BeatProfiles.MinMatch ? double.NaN : 1000.0 * (edge + lag);
            }
            return result;
        }

        /// <summary>
        /// How far the song's hits as a whole are from the grid lines, in milliseconds (positive when
        /// the audio is late). Shifting the grid by this much puts the lines on the hits. NaN when the
        /// song has no clear beat.
        /// </summary>
        public double OverallOffsetMs(BeatGrid grid)
        {
            long first = -(long)Math.Floor(grid.FirstDownbeatSeconds / grid.BeatSeconds);
            long last = (long)(((double)mono.Length / sampleRate - grid.FirstDownbeatSeconds) / grid.BeatSeconds) + 1;
            return 1000.0 * BeatProfiles.Measure(mono, sampleRate, grid.FirstDownbeatSeconds, grid.BeatSeconds, first, last).EdgeSeconds();
        }

        /// <summary>
        /// Moves a grid that is nearly on the hits the rest of the way, which is what a line put
        /// down by hand needs: it is rarely on the front of the hit to the millisecond. The grid
        /// is moved by what <see cref="OverallOffsetMs"/> measures, when that is no more than
        /// <paramref name="reachMs"/> and when the hits are then nearer the lines than they were;
        /// and again, up to three times. A grid further out than that, or audio with no clear
        /// beat, is left exactly where it is: further out the measurement cannot be trusted, and
        /// it was seen to point the wrong way. Returns how far the lines were moved, in seconds.
        /// </summary>
        public double Settle(BeatGrid grid, double reachMs)
        {
            double moved = 0;
            double offset = OverallOffsetMs(grid);
            for (int pass = 0; pass < 3; pass++)
            {
                if (double.IsNaN(offset) || Math.Abs(offset) > reachMs || Math.Abs(offset) < 0.05)
                    break;
                BeatGrid tried = grid.Clone();
                tried.Shift(offset / 1000.0);
                double after = OverallOffsetMs(tried);
                if (double.IsNaN(after) || Math.Abs(after) >= Math.Abs(offset))
                    break;
                grid.Shift(offset / 1000.0);
                moved += offset / 1000.0;
                offset = after;
            }
            return moved;
        }

        /// <summary>
        /// How much louder the audio gets across the grid's lines: the rise in level from the few
        /// milliseconds before a beat line to the few after it, averaged over every line in the
        /// audio. A grid with its lines on the front of the hits scores high. One that is a few
        /// milliseconds out, at a slightly different tempo, or on another part of the beat scores a
        /// fraction of that. The figure tells grids of the same audio apart; it means nothing
        /// between one song and another.
        /// </summary>
        public double AttackOnLines(BeatGrid grid)
        {
            int span = (int)Math.Round(AttackSpanSeconds * sampleRate);
            double total = 0;
            int lines = 0;
            for (long beat = -(long)Math.Floor(grid.FirstBeatSeconds / grid.BeatSeconds); ; beat++)
            {
                int line = (int)Math.Round((grid.FirstBeatSeconds + beat * grid.BeatSeconds) * sampleRate);
                if (line >= mono.Length)
                    break;
                total += Math.Max(0, Level(line, line + span) - Level(line - span, line));
                lines++;
            }
            return lines == 0 ? 0 : total / lines;
        }

        /// <summary>The level (RMS) of a stretch of the audio, taking what lies outside it as silence.</summary>
        double Level(int from, int to)
        {
            double sum = 0;
            for (int i = Math.Max(0, from); i < Math.Min(mono.Length, to); i++)
                sum += mono[i] * mono[i];
            return Math.Sqrt(sum / (to - from));
        }
    }
}
