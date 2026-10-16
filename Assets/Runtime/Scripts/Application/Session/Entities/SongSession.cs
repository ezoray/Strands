using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Strands.Audio;
using Strands.Export;
using Strands.Sp16;

namespace Strands.Applications.Session
{
    /// <summary>
    /// One loaded song and everything the user has decided about it: the grid, where the package
    /// starts, and the name. Every change lays the chunks out again, which is cheap.
    /// The song is one file, or stems: then <see cref="Wav"/> is the stems added together, which is
    /// what is played when no one stem is chosen, and the package is made from the stems.
    /// </summary>
    public sealed class SongSession
    {
        public const int MaxNameLength = 24;

        /// <summary>What to call the song on screen: the file's name, or how many stems there are.</summary>
        public string DisplayName { get; private set; }
        public WavData Wav { get; private set; }
        /// <summary>The stems, or null for a song in one file.</summary>
        public StemSet Stems { get; private set; }
        /// <summary>The stem the grid was found from, or null when it came from the whole song.</summary>
        public string GridFrom { get; private set; }
        /// <summary>
        /// The audio the grid is fitted to and measured against: the stem it came from, so that a
        /// grid fresh from the analysis reads as on the beat. For one file, the file.
        /// </summary>
        public short[] TimingSamples { get; private set; }
        /// <summary>The <see cref="StripLayout.FilesVersion"/> of the files this was made from.</summary>
        public int FilesVersion;
        /// <summary>The low, middle and high sound of a song in one file, which its waveform is coloured by. Null for stems.</summary>
        public BandPeaks Bands { get; private set; }
        public BeatGrid Grid { get; private set; }
        public WavExportOptions Options { get; private set; }
        public string ProjectName { get; private set; }
        /// <summary>Total of the fine nudges made by hand, for display.</summary>
        public double NudgeMs { get; private set; }
        /// <summary>The layout for the current settings, or null when they cannot be exported.</summary>
        public WavExportResult Plan { get; private set; }
        /// <summary>Why there is no plan.</summary>
        public string PlanError { get; private set; }
        /// <summary>
        /// For each chunk of the plan, how far its hits are from the grid in milliseconds (positive
        /// when the audio is late), or NaN where there is no clear beat to measure.
        /// </summary>
        public double[] PatternOffsetsMs { get; private set; }
        /// <summary>
        /// The beat line the package starts on, counted from the first bar line, with everything
        /// before it left out; null when it starts with the file. On the start of a pattern there
        /// is no lead-in; anywhere else, what lies between it and the next pattern is the lead-in.
        /// </summary>
        public int? StartBeat { get; private set; }
        /// <summary>
        /// Which of <see cref="StartBars"/> the package starts on, counted from 1, or 0 when it
        /// starts with the file or on a beat that does not start a pattern.
        /// </summary>
        public int StartChoice { get; private set; }
        /// <summary>
        /// The bars the package can start on with its beginning left out, each the start of a
        /// pattern, so that taking the beginning off a song never cuts a pattern short.
        /// </summary>
        public IList<int> StartBars { get; private set; } = new List<int>();
        /// <summary>The first beat line the package can start on that leaves something out: the one after the beat the song begins on.</summary>
        public int FirstLaterBeat { get; private set; }
        /// <summary>Whether the whole song, with nothing left out, begins with a lead-in and not on the start of its first pattern.</summary>
        public bool StartsWithLeadIn { get; private set; }

        /// <summary>
        /// Which pattern of the whole song the package starts in, counted from 0, the first being
        /// the lead-in when there is one. A start on a beat inside a pattern is in that pattern.
        /// </summary>
        public int StartPattern
        {
            get
            {
                int pattern = 0;
                if (StartBeat.HasValue)
                    foreach (int bar in StartBars)
                        if (4 * bar <= StartBeat.Value)
                            pattern++;
                return pattern;
            }
        }
        /// <summary>
        /// The strip each stem is on, in the order of the stems, which says what colour each is
        /// drawn in. Null for a song in one file.
        /// </summary>
        public IList<int> StemStrips { get; private set; }

        // How near the hits a pattern start put by hand has to be for the lines to be settled on
        // to them. Within this the measurement has always pointed the right way; see BeatDriftMeter.Settle.
        const double SettleReachMs = 20;
        // A playhead this near a line is on it: placing a pattern there chooses the line and moves nothing.
        const double OnALineSeconds = 0.0005;

        // The moment the package was told to start at, when its beginning is left out. It is a
        // place in the music: the pattern nearest it is found again whenever the grid moves, or
        // the beat line nearest it when it was put on a beat and not on a pattern.
        double? startAtSeconds;
        bool startOnAPattern;
        BeatDriftMeter meter;
        WaveformPeaks[] stemPeaks;
        // The stems of a strip added together, for strips that hold several, by the strip's number.
        readonly Dictionary<int, short[]> stripSamples = new Dictionary<int, short[]>();
        readonly Dictionary<int, WaveformPeaks> stripPeaks = new Dictionary<int, WaveformPeaks>();
        // The grid and where the patterns are counted from, as the analysis left them.
        BeatGrid analysedGrid;
        double? analysedAnchor;
        int? analysedDownbeat;

        public event Action Changed;

        /// <param name="displayName">What to call the song on screen.</param>
        /// <param name="songName">A name for the project.</param>
        /// <param name="bands">The song's low, middle and high sound, which the Source screen has made already.</param>
        public SongSession(string displayName, string songName, WavData wav, BandPeaks bands)
        {
            DisplayName = displayName;
            Wav = wav;
            Bands = bands;
            TimingSamples = wav.Samples;
            meter = new BeatDriftMeter(wav.Samples, wav.SampleRate);
            // Play the whole file unless the user chooses a later beat or bar.
            Options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true, SliceOnBeats = true };
            ProjectName = WavToSp16.SanitizeName(songName, MaxNameLength);
        }

        /// <param name="songName">A name for the project: the stem file the stems are from, what their file names share, or the folder they are in.</param>
        /// <param name="strips">The strip each stem is on, in the order of the stems.</param>
        public SongSession(string songName, StemSet stems, IList<int> strips)
        {
            Stems = stems;
            StemStrips = strips;
            DisplayName = stems.StemLabels.Count + " stems";
            Wav = stems.Mix;
            TimingSamples = Wav.Samples;
            meter = new BeatDriftMeter(Wav.Samples, Wav.SampleRate);
            Options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true, SliceOnBeats = true };
            ProjectName = WavToSp16.SanitizeName(songName, MaxNameLength);
            // Every stem is in the picture of the whole song, so they are made here, away from the screen,
            // and so is the level a set of strips is played at.
            MakeStemPeaks();
            mixGain = MeasureMixGain();
        }

        /// <summary>Takes the grid chosen from the stems, and measures against the stem it came from.</summary>
        public void ApplyGridChoice(GridChoice choice)
        {
            GridFrom = choice.Chosen.Stem;
            Stems.Warnings.AddRange(choice.Warnings);
            UseTimingStem();
            ApplyAnalysis(choice.Grid, false);
        }

        /// <summary>
        /// Takes the same files laid out on other outputs, or on master. The grid and everything
        /// decided on the Align screen stay, since the song is the same.
        /// </summary>
        public void Reroute(StemSet stems, IList<int> strips)
        {
            Stems = stems;
            StemStrips = strips;
            stemPeaks = null;
            stripSamples.Clear();
            stripPeaks.Clear();
            mixGain = null;
            Wav = stems.Mix;
            UseTimingStem();
            Replan();
        }

        /// <summary>The waveform of one stem by itself.</summary>
        public WaveformPeaks PeaksOfStem(int stem)
        {
            if (stemPeaks == null)
                MakeStemPeaks();
            return stemPeaks[stem];
        }

        /// <summary>The stems that are on one strip, in their order.</summary>
        public List<int> StemsOnStrip(int strip)
        {
            var stems = new List<int>();
            for (int stem = 0; stem < StemStrips.Count; stem++)
                if (StemStrips[stem] == strip)
                    stems.Add(stem);
            return stems;
        }

        /// <summary>
        /// What a strip is called on screen: the label of the first file on it, cut short when it
        /// is long, and how many more it holds.
        /// </summary>
        public string NameOfStrip(int strip)
        {
            List<int> stems = StemsOnStrip(strip);
            return StripLayout.NameOf(strip, stems.Count == 0 ? null : Stems.StemLabels[stems[0]], stems.Count);
        }

        /// <summary>
        /// What a strip sounds like by itself: its one stem, or its stems added together, held at
        /// full scale where they go over it, as they are when they are mixed into one sample.
        /// </summary>
        public short[] SamplesOfStrip(int strip)
        {
            List<int> stems = StemsOnStrip(strip);
            if (stems.Count == 1)
                return Stems.StemSamples[stems[0]];
            short[] sum;
            if (stripSamples.TryGetValue(strip, out sum))
                return sum;
            sum = new short[Wav.Samples.Length];
            for (int i = 0; i < sum.Length; i++)
            {
                int value = 0;
                foreach (int stem in stems)
                    value += Stems.StemSamples[stem][i];
                sum[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
            }
            stripSamples[strip] = sum;
            return sum;
        }

        double? mixGain;

        /// <summary>
        /// How far the whole song was turned down to fit in 16 bits, which it is when its stems
        /// added up go over full scale (`StemsToSp16.Prepare`): 1 when they do not.
        /// </summary>
        double MixGain
        {
            get
            {
                if (!mixGain.HasValue)
                    mixGain = MeasureMixGain();
                return mixGain.Value;
            }
        }

        double MeasureMixGain()
        {
            // Over arrays and by index: a foreach over a list here made an object for every
            // sample, which held the build up for 5 to 10 seconds at the first switch of a strip.
            short[][] parts = Stems.StemSamples.ToArray();
            var peaks = new int[64];
            int length = parts[0].Length;
            Parallel.For(0, 64, block =>
            {
                int from = (int)((long)length * block / 64), to = (int)((long)length * (block + 1) / 64), peak = 0;
                for (int i = from; i < to; i++)
                {
                    int value = 0;
                    for (int part = 0; part < parts.Length; part++)
                        value += parts[part][i];
                    if (value < 0)
                        value = -value;
                    if (value > peak)
                        peak = value;
                }
                peaks[block] = peak;
            });
            int most = 0;
            foreach (int peak in peaks)
                most = Math.Max(most, peak);
            return most > short.MaxValue ? (double)short.MaxValue / most : 1.0;
        }

        /// <summary>The waveform of a strip by itself.</summary>
        public WaveformPeaks PeaksOfStrip(int strip)
        {
            List<int> stems = StemsOnStrip(strip);
            if (stems.Count == 1)
                return PeaksOfStem(stems[0]);
            WaveformPeaks peaks;
            if (!stripPeaks.TryGetValue(strip, out peaks))
                stripPeaks[strip] = peaks = new WaveformPeaks(SamplesOfStrip(strip));
            return peaks;
        }

        void MakeStemPeaks()
        {
            var made = new WaveformPeaks[Stems.StemSamples.Count];
            Parallel.For(0, made.Length, stem => made[stem] = new WaveformPeaks(Stems.StemSamples[stem]));
            stemPeaks = made;
        }

        void UseTimingStem()
        {
            int stem = GridFrom == null ? -1 : Stems.StemLabels.IndexOf(GridFrom);
            TimingSamples = stem < 0 ? Wav.Samples : Stems.StemSamples[stem];
            meter = new BeatDriftMeter(TimingSamples, Wav.SampleRate);
        }

        /// <summary>Writes the package for a plan this session made.</summary>
        public void WritePackage(byte[] templatePrj, string packagePath, WavExportResult plan, IProgress<float> progress, CancellationToken cancel)
        {
            if (Stems != null)
                Stems.WritePackage(templatePrj, packagePath, plan, progress, cancel);
            else
                WavToSp16.WritePackage(Wav, templatePrj, packagePath, plan, progress, cancel);
        }

        /// <summary>One pad's sample as the package for a plan will hold it, for playing it on the Export page.</summary>
        public short[] SampleOfPad(WavExportResult plan, int chunk, int layer)
        {
            return WavToSp16.SampleOf(Stems != null ? (IList<short[]>)Stems.LayerSamples : new[] { Wav.Samples }, plan, chunk, layer);
        }

        public bool HasGrid { get { return Grid != null; } }

        /// <summary>
        /// The point that stays put when the tempo is changed: where a pattern has been told to start
        /// (by default where the drums come in), or failing that the bar the package starts on.
        /// </summary>
        public double StartSeconds { get { return Options.PatternAnchorSeconds ?? Grid.TimeOfBeat(StartChoice > 0 ? StartBeat.Value : 0); } }

        /// <summary>Takes a grid from the analyser. Later analyses start from the current tempo.</summary>
        public void ApplyAnalysis(BeatGrid analysed, bool keepBarLines)
        {
            if (keepBarLines && Grid != null)
            {
                // Put the bar lines on whichever beat is nearest to where the user had them.
                double wanted = StartSeconds;
                int best = 0;
                double bestDistance = double.MaxValue;
                for (int shift = 0; shift < 4; shift++)
                {
                    double bar = 4 * analysed.BeatSeconds;
                    double offset = (wanted - (analysed.FirstBeatSeconds + shift * analysed.BeatSeconds)) % bar;
                    double distance = Math.Min(Math.Abs(offset), bar - Math.Abs(offset));
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = shift;
                    }
                }
                analysed.DownbeatIndex = best;
            }
            else
            {
                // Start a pattern where the drums come in, so an intro of any length is counted back from there.
                Options.PatternAnchorSeconds = analysed.FirstSteadyBeatSeconds >= 0 ? analysed.FirstSteadyBeatSeconds : (double?)null;
                // Kept so that everything done by hand afterwards can be undone in one go. A copy, because the grid is changed in place.
                analysedGrid = analysed.Clone();
                analysedAnchor = Options.PatternAnchorSeconds;
                analysedDownbeat = Options.Grid.DownbeatIndex;
            }
            Grid = analysed;
            NudgeMs = 0;
            Replan();
        }

        /// <summary>
        /// Puts the grid, the place the patterns are counted from, the start and the crossfade back
        /// as they were when the song was first analysed. The name is left as it is.
        /// </summary>
        /// <summary>
        /// Whether a pattern can be made a bar shorter (-1) or longer (+1). A pattern is one to
        /// four bars. The lead-in's length is set by where the song starts, and the last pattern
        /// runs to the end of the song, so neither has one of its own. A pattern is made longer
        /// only back towards the length it was cut down from.
        /// </summary>
        public bool CanStepPatternLength(int chunk, int direction)
        {
            WavExportResult plan = Plan;
            if (plan == null || chunk < 0 || chunk + 1 >= plan.Chunks.Count || plan.Chunks[chunk].IsLeadIn || direction == 0)
                return false;
            int bars = plan.Chunks[chunk + 1].StartBar - plan.Chunks[chunk].StartBar;
            if (direction < 0)
                return bars > 1;
            return bars < Options.BarsPerChunk && PatternStartAt(plan.Chunks[chunk + 1].StartBar) >= 0;
        }

        /// <summary>
        /// Makes a pattern a bar shorter or longer, with every bar line where it was: the patterns
        /// after it all start a bar earlier or later (the user, 2026-10-14: "each pattern is manually
        /// adjustable from 1 to 4 bars"). It is kept as the bar the next pattern starts on, a
        /// moment in the music, so it stays with the music when the grid is moved.
        /// </summary>
        public void StepPatternLength(int chunk, int direction)
        {
            if (!CanStepPatternLength(chunk, direction))
                return;
            direction = Math.Sign(direction);
            WavExportResult plan = Plan;
            int start = plan.Chunks[chunk].StartBar, end = plan.Chunks[chunk + 1].StartBar;
            double barSeconds = 4 * Grid.BeatSeconds;
            int at = PatternStartAt(end);
            if (at >= 0)
                Options.PatternStartSeconds.RemoveAt(at);
            // Everything after it moves with it, the places other patterns were told to start included.
            for (int i = 0; i < Options.PatternStartSeconds.Count; i++)
                if (BarNearest(Options.PatternStartSeconds[i]) > end)
                    Options.PatternStartSeconds[i] += direction * barSeconds;
            // Back to a whole pattern, the count carries on by itself.
            if (end + direction - start != Options.BarsPerChunk)
                Options.PatternStartSeconds.Add(Grid.TimeOfBeat(4 * (end + direction)));
            Replan();
        }

        int BarNearest(double seconds)
        {
            return (int)Math.Round((seconds - Grid.FirstDownbeatSeconds) / (4 * Grid.BeatSeconds));
        }

        // Which of the places patterns were told to start is on this bar, or -1.
        int PatternStartAt(int bar)
        {
            for (int i = 0; i < Options.PatternStartSeconds.Count; i++)
                if (BarNearest(Options.PatternStartSeconds[i]) == bar)
                    return i;
            return -1;
        }

        public void ResetToAnalysis()
        {
            if (analysedGrid == null)
                return;
            Grid = analysedGrid.Clone();
            Options.PatternAnchorSeconds = analysedAnchor;
            Options.Grid.DownbeatIndex = analysedDownbeat;
            startAtSeconds = null;
            Options.PatternStartSeconds.Clear();
            Options.CrossfadeMs = new WavExportOptions().CrossfadeMs;
            NudgeMs = 0;
            Replan();
        }

        /// <summary>
        /// Makes <paramref name="seconds"/> the first beat of a bar and the start of a pattern:
        /// everything else is counted off from there in both directions. The lane marks it with a flag.
        /// When that moment is not on a line already, the lines are moved so that one is exactly
        /// there, by half a beat at most, and then settled on to the hits when there are hits
        /// within reach. This is the one tool for a grid that is out: by a few milliseconds, on the
        /// off-beat, or with its bars on the wrong beat. On a line already, nothing moves but the flag.
        /// </summary>
        public void SetPatternStartAt(double seconds)
        {
            long beat = (long)Math.Round((seconds - Grid.FirstDownbeatSeconds) / Grid.BeatSeconds);
            double away = seconds - (Grid.FirstDownbeatSeconds + beat * Grid.BeatSeconds);
            if (Math.Abs(away) > OnALineSeconds)
            {
                Grid.Shift(away);
                seconds += meter.Settle(Grid, SettleReachMs);
                // The lines are somewhere new, so the fine tuning is counted from here.
                NudgeMs = 0;
                // Moving the lines can change which of them the grid counts as its first.
                beat = (long)Math.Round((seconds - Grid.FirstDownbeatSeconds) / Grid.BeatSeconds);
            }
            Grid.ShiftDownbeat((int)(beat % 4));
            Options.Grid.DownbeatIndex = Grid.DownbeatIndex;
            Options.PatternAnchorSeconds = seconds;
            Replan();
        }

        /// <summary>
        /// Where patterns are counted from: the bar line nearest the place a pattern was told to
        /// start, or the first bar line of all when none was.
        /// </summary>
        public double FlagSeconds { get { return Grid.TimeOfBeat(FlagBar * 4); } }

        int FlagBar
        {
            get
            {
                if (!Options.PatternAnchorSeconds.HasValue)
                    return 0;
                long bar = (long)Math.Round((Options.PatternAnchorSeconds.Value - Grid.FirstDownbeatSeconds) / (4 * Grid.BeatSeconds));
                // A place before the first bar line counts the same patterns off as the first line that falls in step with it.
                return (int)(bar >= 0 ? bar : ((bar % 4) + 4) % 4);
            }
        }

        /// <summary>
        /// Starts the package with the file (0), or on one of <see cref="StartBars"/>, counted
        /// from 1, leaving out what comes before it: an unwanted beginning, a pattern at a time.
        /// </summary>
        public void SetStartChoice(int choice)
        {
            choice = Math.Max(0, Math.Min(StartBars.Count, choice));
            startAtSeconds = choice == 0 ? (double?)null : Grid.TimeOfBeat(4 * StartBars[choice - 1]);
            startOnAPattern = true;
            Replan();
        }

        /// <summary>
        /// Where a step of one pattern later (1) or earlier (-1) takes the start: one of
        /// <see cref="StartBars"/> counted from 1, 0 for the start of the file, or -1 when there
        /// is nowhere to go. From a beat inside a pattern, later is the next pattern and earlier
        /// the start of the pattern it is in.
        /// </summary>
        public int StartChoiceAfter(int direction)
        {
            if (!StartBeat.HasValue)
                return direction > 0 && StartBars.Count > 0 ? 1 : -1;
            if (direction > 0)
            {
                for (int i = 0; i < StartBars.Count; i++)
                    if (4 * StartBars[i] > StartBeat.Value)
                        return i + 1;
                return -1;
            }
            for (int i = StartBars.Count - 1; i >= 0; i--)
                if (4 * StartBars[i] < StartBeat.Value)
                    return i + 1;
            return 0;
        }

        /// <summary>
        /// Whether the start can be moved a beat later (1) or earlier (-1): later for as long as a
        /// bar of the song is left to play, earlier until nothing is left out.
        /// </summary>
        public bool CanStepStartBeat(int direction)
        {
            if (direction < 0)
                return StartBeat.HasValue;
            int next = StartBeat.HasValue ? StartBeat.Value + 1 : FirstLaterBeat;
            return Grid.TimeOfBeat(next + 4) <= Wav.Seconds;
        }

        /// <summary>
        /// Starts the package a beat later (1), dropping a beat from the front of the lead-in or of
        /// the song, or a beat earlier (-1), putting one back. The first step later goes to the
        /// beat after the one the song begins on; stepping back past it starts with the file again.
        /// </summary>
        public void StepStartBeat(int direction)
        {
            if (!CanStepStartBeat(direction))
                return;
            int beat = !StartBeat.HasValue ? FirstLaterBeat : StartBeat.Value + (direction > 0 ? 1 : -1);
            startAtSeconds = beat < FirstLaterBeat ? (double?)null : Grid.TimeOfBeat(beat);
            startOnAPattern = false;
            Replan();
        }

        public void SetTempo(double bpm)
        {
            Grid.SetTempo(bpm, StartSeconds);
            Replan();
        }

        /// <summary>
        /// Moves the bar lines later by this many beats, and earlier when negative: a whole beat, a
        /// half or a quarter. A whole beat leaves every line where it is and changes which of them
        /// starts the bar. A part of a beat moves all the lines on to another part of the beat,
        /// which is what a grid that has landed on the off-beat needs.
        /// </summary>
        public void ShiftBeats(double beats)
        {
            double seconds = beats * Grid.BeatSeconds;
            Grid.Shift(seconds);
            MoveAnchor(seconds);
            // From here on the bar is where the user put it, not a guess to warn about.
            Options.Grid.DownbeatIndex = Grid.DownbeatIndex;
            Replan();
        }

        public void Nudge(double milliseconds)
        {
            Grid.Shift(milliseconds / 1000.0);
            MoveAnchor(milliseconds / 1000.0);
            NudgeMs += milliseconds;
            Replan();
        }

        /// <summary>Puts one pad in its stem's choke group, or takes it out.</summary>
        public void SetChokeOfPad(int chunk, int layer, bool on)
        {
            Options.Pads.SetChoke(chunk, layer, on);
            Replan();
        }

        /// <summary>Sets one pad to loop, or to play once.</summary>
        public void SetLoopOfPad(int chunk, int layer, bool on)
        {
            Options.Pads.SetLoop(chunk, layer, on);
            Replan();
        }

        /// <summary>Sets the choke of every pad of some layers, over whatever their pads were set to one at a time.</summary>
        public void SetChokeOfLayers(IEnumerable<int> layers, bool on)
        {
            foreach (int layer in layers)
                Options.Pads.SetChokeOfLayer(layer, on);
            Replan();
        }

        /// <summary>Sets the loop of every pad of some layers, over whatever their pads were set to one at a time.</summary>
        public void SetLoopOfLayers(IEnumerable<int> layers, bool on)
        {
            foreach (int layer in layers)
                Options.Pads.SetLoopOfLayer(layer, on);
            Replan();
        }

        /// <summary>Sets the velocity of every pad of some layers, over whatever their pads were set to one at a time.</summary>
        public void SetVelocityOfLayers(IEnumerable<int> layers, bool on)
        {
            foreach (int layer in layers)
                Options.Pads.SetVelocityOfLayer(layer, on);
            Replan();
        }

        /// <summary>
        /// How every sample follows the unit's tempo. A song that follows it cannot have a tempo
        /// of its own in the arrangement, which the unit goes back to, and one that does not
        /// follow needs it there or its patterns no longer meet: so the arrangement's tempo is
        /// set with it, and can be changed afterwards.
        /// </summary>
        public void SetStretch(Sp16Stretch stretch)
        {
            Options.Stretch = stretch;
            Options.TempoInArrangement = stretch == Sp16Stretch.Off;
            Replan();
        }

        /// <summary>Whether the unit opens the project with its arranger on. The song is in the arrangement either way.</summary>
        public void SetArrangerOn(bool on)
        {
            Options.Arrangement = on;
            Replan();
        }

        /// <summary>Whether the sections of the arrangement carry the song's tempo. Without it the song plays at the project's tempo, which the unit can change.</summary>
        public void SetTempoInArrangement(bool added)
        {
            Options.TempoInArrangement = added;
            Replan();
        }

        /// <summary>
        /// What some strips sound like together: every strip gives the song as it is, one gives
        /// that strip, and none gives silence. Each is turned down by as much as the whole song is
        /// when its stems added up go over full scale, so that switching a strip off or on leaves
        /// the others at the level they were (the user, 2026-10-14: "the vocal seems to change
        /// when I turn bass and other on and off"; before, the song was turned down and any other
        /// set of strips was not).
        /// </summary>
        public short[] SamplesOfStrips(IList<int> strips)
        {
            var distinct = new List<int>();
            foreach (int strip in StemStrips)
                if (!distinct.Contains(strip))
                    distinct.Add(strip);
            bool all = true;
            foreach (int strip in distinct)
                all &= strips.Contains(strip);
            if (all)
                return Wav.Samples;
            double gain = MixGain;
            var stems = new List<int>();
            foreach (int strip in strips)
                stems.AddRange(StemsOnStrip(strip));
            if (stems.Count == 1 && gain == 1.0)
                return Stems.StemSamples[stems[0]];
            // Kept by which strips are in it, with a number no single strip has.
            int key = 1000;
            foreach (int strip in strips)
                key += 1 << strip;
            short[] sum;
            if (stripSamples.TryGetValue(key, out sum))
                return sum;
            sum = new short[Wav.Samples.Length];
            var parts = new short[stems.Count][];
            for (int stem = 0; stem < parts.Length; stem++)
                parts[stem] = Stems.StemSamples[stems[stem]];
            if (parts.Length > 0)
                Parallel.For(0, 64, block =>
                {
                    int from = (int)((long)sum.Length * block / 64), to = (int)((long)sum.Length * (block + 1) / 64);
                    for (int i = from; i < to; i++)
                    {
                        int value = 0;
                        for (int part = 0; part < parts.Length; part++)
                            value += parts[part][i];
                        double scaled = gain == 1.0 ? value : Math.Round(value * gain);
                        sum[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, scaled));
                    }
                });
            stripSamples[key] = sum;
            return sum;
        }

        public void SetProjectName(string name)
        {
            ProjectName = WavToSp16.SanitizeName(name, MaxNameLength);
            Replan();
        }

        void Replan()
        {
            try
            {
                // The patterns and the beats go with the grid, so the one the package starts on is
                // found again each time: the one nearest the moment that was chosen.
                bool leadIn;
                StartBars = WavToSp16.LaterStartBars(Wav, Options, Grid, out leadIn);
                StartsWithLeadIn = leadIn;
                FirstLaterBeat = WavToSp16.FirstLaterStartBeat(Wav, Options, Grid);
                StartChoice = 0;
                StartBeat = null;
                if (startAtSeconds.HasValue && startOnAPattern && StartBars.Count > 0)
                {
                    double nearest = double.MaxValue;
                    for (int i = 0; i < StartBars.Count; i++)
                    {
                        double away = Math.Abs(Grid.TimeOfBeat(4 * StartBars[i]) - startAtSeconds.Value);
                        if (away < nearest)
                        {
                            nearest = away;
                            StartChoice = i + 1;
                        }
                    }
                    StartBeat = 4 * StartBars[StartChoice - 1];
                }
                else if (startAtSeconds.HasValue && !startOnAPattern)
                {
                    int beat = (int)Math.Round((startAtSeconds.Value - Grid.FirstDownbeatSeconds) / Grid.BeatSeconds);
                    // A beat the song has not reached yet leaves nothing out.
                    if (beat >= FirstLaterBeat)
                    {
                        StartBeat = beat;
                        StartChoice = beat % 4 == 0 ? StartBars.IndexOf(beat / 4) + 1 : 0;
                    }
                }
                Options.StartBeat = StartBeat;
                Plan = Stems != null ? Stems.Plan(ProjectName, Options, Grid) : WavToSp16.Plan(Wav, ProjectName, Options, Grid);
                PlanError = null;
                MeasureOffsets();
            }
            catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException || e is ArgumentException)
            {
                Plan = null;
                PlanError = e.Message;
                PatternOffsetsMs = null;
            }
            if (Changed != null)
                Changed();
        }

        // The anchor is a place in the music, so it goes with the grid when the grid is moved,
        // and so does the place the package starts.
        void MoveAnchor(double seconds)
        {
            if (Options.PatternAnchorSeconds.HasValue)
                Options.PatternAnchorSeconds += seconds;
            if (startAtSeconds.HasValue)
                startAtSeconds += seconds;
        }

        void MeasureOffsets()
        {
            int leadIn = Plan.Chunks.Count > 0 && Plan.Chunks[0].IsLeadIn ? 1 : 0;
            var starts = new long[Plan.Chunks.Count - leadIn];
            var lengths = new int[starts.Length];
            for (int i = 0; i < starts.Length; i++)
            {
                ChunkInfo chunk = Plan.Chunks[i + leadIn];
                starts[i] = chunk.StartBar * 4L;
                lengths[i] = chunk.Bars * 4;
            }
            double[] measured = meter.Measure(Grid, starts, lengths);
            PatternOffsetsMs = new double[Plan.Chunks.Count];
            if (leadIn == 1)
                PatternOffsetsMs[0] = double.NaN;
            Array.Copy(measured, 0, PatternOffsetsMs, leadIn, measured.Length);
        }
    }
}
