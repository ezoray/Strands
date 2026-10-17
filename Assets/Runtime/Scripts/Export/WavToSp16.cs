using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Strands.Audio;
using Strands.Sp16;

namespace Strands.Export
{
    public sealed class WavExportOptions
    {
        /// <summary>Name of the project and package. Taken from the WAV file name when empty.</summary>
        public string ProjectName;
        /// <summary>Bars per sample, 1 to 4. A pattern holds at most 4 bars, so each chunk is one pattern.</summary>
        public int BarsPerChunk = 4;
        /// <summary>
        /// Each chunk runs on past its end by this long, fading out while the next one fades in, so a
        /// trigger that lands a little early or late gives a short blend rather than a gap or a click.
        /// How tightly the SP-16 joins consecutive samples is untested; 0 gives plain hard cuts.
        /// </summary>
        public double CrossfadeMs = 5.0;
        /// <summary>
        /// Bars to skip before the first chunk, counted from the first bar line in the audio. 0 starts
        /// on that bar line and leaves out only the part of a bar in front of it.
        /// </summary>
        public int StartBar;
        /// <summary>
        /// Keep the audio in front of the first bar line as a short first sample of its own, so the
        /// package plays from the very start of the file. Only applies when <see cref="StartBar"/> is 0.
        /// </summary>
        public bool KeepLeadIn;
        /// <summary>
        /// Leave out complete silence at the start of the file: the lead-in then begins at the first
        /// sound, and chunk boundaries that fall in the silence are skipped. Only applies with
        /// <see cref="KeepLeadIn"/>.
        /// </summary>
        public bool TrimLeadingSilence;
        /// <summary>
        /// A beat line to start the package on, counted from the first bar line (so one in front
        /// of it is negative), with everything before it left out: beats dropped from the front of
        /// the lead-in, or of the song. What lies between that beat and the next start of a pattern
        /// is then the lead-in; on the start of a pattern there is none. It is not used when the
        /// beat is at or before where the lead-in would begin anyway. Only applies with
        /// <see cref="KeepLeadIn"/> and <see cref="StartBar"/> 0.
        /// </summary>
        public int? StartBeat;
        /// <summary>
        /// A time in the song where a chunk has to start, such as where the drums come in. Chunks are
        /// counted off in both directions from the bar line nearest to it, so the chunk before it may
        /// be shorter than the rest. When null, chunks are counted from the first bar line.
        /// </summary>
        public double? PatternAnchorSeconds;
        /// <summary>
        /// Moments in the song where a pattern has to start, each taken to the bar line nearest to
        /// it: how a pattern is made shorter than the rest. Patterns are counted in
        /// <see cref="BarsPerChunk"/> from each of them, so the pattern before one ends there, and
        /// those after it start again from it. They are the user's, set a bar at a time on the
        /// Analysis screen, for songs whose phrases are not all a whole number of patterns long (the
        /// user, 2026-10-14, on three songs whose build-ups moved two bars off the patterns). A
        /// pattern of one, two or three bars in the middle of a song is Unverified on the unit.
        /// </summary>
        public readonly List<double> PatternStartSeconds = new List<double>();
        /// <summary>
        /// Put a slice point on every beat of each chunk, so that a 4-bar chunk has 16 slices to play
        /// from the pads in the unit's slice mode. A shorter chunk has fewer, and so does the lead-in,
        /// whose first slice is the part beat it starts with. The SP-16 loads and plays these
        /// (Confirmed, SP-16, by the user on 2026-10-10).
        /// </summary>
        public bool SliceOnBeats;
        /// <summary>
        /// Which pads are in their layer's choke group and which loop. In the arrangement a pad
        /// in a group is cut off by the next one of its layer, where it would have faded out under
        /// it; that joined cleanly on the SP-16 with the crossfade and with none (the user,
        /// 2026-10-12). Choke is not used with more layers than the unit has groups. With any pad
        /// looping the song is cut with no crossfade, so that a turn of a loop is exactly its
        /// pattern's length. That the unit loops a sample a file says to loop is Unverified.
        /// </summary>
        public readonly PadSettings Pads = new PadSettings();
        /// <summary>
        /// A short fade at the end and at the start of every sample that loops, so that where it
        /// goes round the level passes through nothing: without it a sound held across the bar
        /// line, such as a voice, clicks at the turn (the user, SP-16, 2026-10-12, on the vocals of
        /// a package with every pad looped). The cost is a dip of that length where the pattern
        /// hands over to the next in the arrangement, and a softer front on a hit that starts the
        /// sample, which is why the fade in is the shorter. 0 for none. With 3 ms out and 1 ms in
        /// the clicks were gone and the package was "fine" on the SP-16 (the user, 2026-10-12,
        /// Loop112f.tpkg). A sample that does not loop is never touched by it.
        /// </summary>
        public double LoopFadeOutMs = 3, LoopFadeInMs = 1;
        /// <summary>
        /// How every sample follows the unit's tempo. Off plays the audio as it is, which is the
        /// only way the export never changes the sound. With Resample the song speeds up and slows
        /// down with the unit, as when the tempo comes over Pro DJ Link; it leans on the tempo
        /// written into each sample's .dat. What either does to the joins between patterns is
        /// Unverified.
        /// </summary>
        public Sp16Stretch Stretch = Sp16Stretch.Off;
        /// <summary>
        /// Write the song's tempo into every section of the arrangement. The unit then plays the
        /// arrangement at that tempo whatever it was set to, and goes back to it when the tempo is
        /// changed (the user, SP-16, 2026-10-12, on a package with every sample on RESMPL). Without
        /// it a section has no tempo of its own (0 in the file: Confirmed, file) and the song
        /// plays at the unit's tempo, which starts as the project's and can then follow Pro DJ
        /// Link; with <see cref="Stretch"/> Off the patterns would then no longer meet.
        /// </summary>
        public bool TempoInArrangement = true;
        /// <summary>
        /// Have the unit open the project with its arranger on, so that it plays the song. The
        /// arrangement is written either way, and can be switched on at the unit; with it off the
        /// package opens as pads and patterns to play by hand.
        /// </summary>
        public bool Arrangement = true;
        /// <summary>
        /// Slice points placed by hand, in frames of the source. Each one takes the place of the beat
        /// line nearest to it, when that is less than half a beat away; otherwise it is not used.
        /// The app has no control for these at present.
        /// </summary>
        public readonly List<int> ManualSliceFrames = new List<int>();
        public BeatGridOptions Grid = new BeatGridOptions();
    }

    /// <summary>
    /// One of the samples a chunk is made of when there are several: a stem, or stems mixed
    /// together, and the output it plays from. Every chunk has one pad for each layer, and its
    /// pattern triggers them all on step 1.
    /// </summary>
    public sealed class ExportLayer
    {
        /// <summary>A short name for the sample files, such as Drums.</summary>
        public string Label;
        public Sp16Output Output;
        /// <summary>
        /// The group its stems are in (<see cref="Stem.Group"/>): on the screen, the strip they are
        /// on. It says which colour the layer is shown in, which its output cannot when every
        /// layer is on master.
        /// </summary>
        public int Group;

        /// <summary>
        /// A layer on one output of its own is written as mono files, its two sides added and
        /// halved, so that nothing is left to what the unit does with a stereo sample there.
        /// </summary>
        public bool IsMono { get { return Sp16Project.IsSingleOutput(Output); } }
    }

    public sealed class ChunkInfo
    {
        public int Index;
        public int Scene;
        /// <summary>
        /// Where the chunk is on the unit's grid of pads, counted as a page is read from the top
        /// left. With layers it is the place of the first; the others follow, one place per layer.
        /// </summary>
        public int Place;
        /// <summary>The chunk's pad. With layers it is the first layer's; see <see cref="PadOf"/> for the others.</summary>
        public int Pad { get { return PadOf(0); } }
        /// <summary>
        /// The pad of one of the chunk's layers. Chunks fill a scene from the top row down, left to
        /// right, so the first pattern of a scene is on the top row and, with four layers, each
        /// layer has a column.
        /// </summary>
        public int PadOf(int layer) { return Sp16Project.PadInReadingOrder(Place + layer); }
        public int Pattern;
        /// <summary>Where the chunk starts in the source, in frames.</summary>
        public int StartFrame;
        /// <summary>Where the next chunk starts (or the audio ends).</summary>
        public int EndFrame;
        /// <summary>Frames in the sample file, including the crossfade tail.</summary>
        public int SampleFrames;
        public int Bars;
        public int PatternLength;
        /// <summary>
        /// Silent frames written in front of the audio. Only the lead-in sample has any: it is padded
        /// so that it lasts a whole number of steps and ends exactly on the first bar line.
        /// </summary>
        public int LeadingSilenceFrames;
        /// <summary>
        /// Where the chunk's pattern starts, in frames of the source: for the lead-in, the beat
        /// line its silence takes it back to, which is before the start of the file when the
        /// file starts with sound. For every other chunk it is <see cref="StartFrame"/>.
        /// </summary>
        public int PatternStartFrame { get { return StartFrame - LeadingSilenceFrames; } }
        /// <summary>True for the sample that holds the audio in front of the first bar line.</summary>
        public bool IsLeadIn;
        /// <summary>The bar this chunk starts on, counted from the first bar line in the audio; -1 for the lead-in.</summary>
        public int StartBar = -1;
        /// <summary>
        /// The chunk's sample files: one, or one per layer. A layer that is silent for the whole
        /// of the chunk has no file, and null here: its pad is left empty and is not triggered.
        /// </summary>
        public string[] FileNames;

        /// <summary>The chunk's first sample file.</summary>
        public string FileName
        {
            get
            {
                foreach (string name in FileNames)
                    if (name != null)
                        return name;
                return null;
            }
        }

        /// <summary>How many sample files the chunk has: its layers, less those that are silent in it.</summary>
        public int SampleCount
        {
            get
            {
                int count = 0;
                foreach (string name in FileNames)
                    if (name != null)
                        count++;
                return count;
            }
        }
        /// <summary>
        /// Slice boundaries in frames of the source, from the start of the sample to its end: one
        /// more than the number of slices. The lead-in's first one is before its
        /// <see cref="StartFrame"/> by the silence in front of it. Null when the chunk is not sliced.
        /// </summary>
        public int[] SliceFrames;
        /// <summary>For each boundary, whether it was placed by hand and not taken from the beat grid.</summary>
        public bool[] SliceIsManual;

        public int Slices { get { return SliceFrames == null ? 0 : SliceFrames.Length - 1; } }

        /// <summary>The boundaries as the project stores them: frames of the sample, from 0 to its length.</summary>
        public int[] SliceBoundariesInSample()
        {
            var boundaries = new int[SliceFrames.Length];
            for (int i = 0; i < boundaries.Length; i++)
                boundaries[i] = SliceFrames[i] - StartFrame + LeadingSilenceFrames;
            return boundaries;
        }
    }

    /// <summary>
    /// A song that does not fit on the unit: more patterns than there is room for with this many
    /// tracks, or more sample memory than there is.
    /// </summary>
    public sealed class PlanRefusedException : InvalidOperationException
    {
        /// <summary>How many patterns the song is cut into, the lead-in among them.</summary>
        public readonly int PatternsNeeded;
        /// <summary>Whether it was refused for its patterns, and not for its memory.</summary>
        public readonly bool ForPatterns;

        public PlanRefusedException(string message, int patternsNeeded, bool forPatterns) : base(message)
        {
            PatternsNeeded = patternsNeeded;
            ForPatterns = forPatterns;
        }
    }

    public sealed class WavExportResult
    {
        public string ProjectName;
        public string PackagePath;
        public BeatGrid Grid;
        public int SampleRate;
        public int SourceFrames;
        public int SourceBitsPerSample;
        /// <summary>The rate the source was at when it was converted to 44.1 kHz; 0 when it was not.</summary>
        public int SourceSampleRate;
        public int CrossfadeFrames;
        public readonly List<ChunkInfo> Chunks = new List<ChunkInfo>();
        /// <summary>The layers every chunk is made of. Empty for a full mix, which is one sample per chunk.</summary>
        public readonly List<ExportLayer> Layers = new List<ExportLayer>();
        public readonly List<string> Warnings = new List<string>();
        /// <summary>
        /// That the tempo is outside the units' range, or null. Kept apart from <see cref="Warnings"/>: the app
        /// shows it beside the tempo (the user, 2026-10-15).
        /// </summary>
        public string TempoWarning;

        /// <summary>Adds warnings found outside the plan, such as the stems' and the grid's.</summary>
        public void AddWarnings(IEnumerable<string> more)
        {
            Warnings.AddRange(more);
        }

        /// <summary>How many pads a chunk takes.</summary>
        public int SamplesPerChunk { get { return Math.Max(1, Layers.Count); } }
        /// <summary>
        /// How many patterns the song is cut into, the lead-in among them, before patterns of
        /// silence at its end are dropped: what is counted against the room the unit has.
        /// </summary>
        public int PatternsNeeded;
        /// <summary>How every sample follows the unit's tempo (<see cref="WavExportOptions.Stretch"/>).</summary>
        public Sp16Stretch Stretch;
        /// <summary>See <see cref="WavExportOptions.TempoInArrangement"/>.</summary>
        public bool TempoInArrangement = true;
        /// <summary>See <see cref="WavExportOptions.Arrangement"/>.</summary>
        public bool Arrangement = true;
        /// <summary>The choke and loop of every pad (<see cref="WavExportOptions.Pads"/>).</summary>
        public PadSettings Pads = new PadSettings();
        /// <summary>The fades at the two ends of a sample that loops (<see cref="WavExportOptions.LoopFadeOutMs"/>), in frames.</summary>
        public int LoopFadeOutFrames, LoopFadeInFrames;
        /// <summary>Whether the plan has few enough layers for each to have one of the unit's choke groups.</summary>
        public bool CanChoke { get { return SamplesPerChunk <= Sp16Project.ChokeGroups; } }
        /// <summary>Whether a pad of the plan is written in its layer's choke group.</summary>
        public bool ChokeOf(int chunk, int layer) { return CanChoke && Pads.Choke(chunk, layer); }
        /// <summary>
        /// Whether a pad of the plan loops. Never with the arrangement on: the song's last sample is
        /// shorter than its pattern, so a loop played its start again before the arranger stopped
        /// (heard on the SP-16), and a project for playing loops by hand is not one for playing from
        /// the arranger (the user, 2026-10-16: "looping and arrangement playback should be mutually
        /// exclusive"). What the pads are set to is kept, and holds again with the arrangement off.
        /// </summary>
        public bool LoopOf(int chunk, int layer) { return !Arrangement && Pads.Loop(chunk, layer); }
        /// <summary>Whether any pad of the plan loops.</summary>
        public bool AnyLoop { get { return !Arrangement && Pads.AnyLoop; } }

        public long SampleBytes
        {
            get
            {
                if (Layers.Count == 0)
                    return BytesOfLayer(0);
                long bytes = 0;
                for (int layer = 0; layer < Layers.Count; layer++)
                    bytes += BytesOfLayer(layer);
                return bytes;
            }
        }

        /// <summary>The share of <see cref="SampleBytes"/> that one layer's samples take: those it has, which are not the silent ones.</summary>
        public long BytesOfLayer(int layer)
        {
            long bytesPerFrame = Layers.Count > 0 && Layers[layer].IsMono ? Sp16Project.MonoBytesPerFrame : Sp16Project.StereoBytesPerFrame;
            long bytes = 0;
            foreach (ChunkInfo chunk in Chunks)
                if (chunk.FileNames == null || chunk.FileNames[layer] != null)
                    bytes += chunk.SampleFrames * bytesPerFrame;
            return bytes;
        }

        /// <summary>How many samples were left out for being silent from end to end.</summary>
        public int SilentSamples
        {
            get
            {
                int silent = 0;
                foreach (ChunkInfo chunk in Chunks)
                    silent += chunk.FileNames.Length - chunk.SampleCount;
                return silent;
            }
        }

        public string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.AppendLine("Project        " + ProjectName);
            if (PackagePath != null)
                text.AppendLine("Package        " + PackagePath);
            text.AppendLine(string.Format(inv, "Source         {0:0.000} s, {1} Hz{3}, {2}-bit", (double)SourceFrames / SampleRate, SampleRate, SourceBitsPerSample,
                SourceSampleRate == 0 ? "" : string.Format(inv, " (converted from {0} Hz)", SourceSampleRate)));
            text.AppendLine(string.Format(inv, "Tempo          {0:0.000} BPM{1}", Grid.Bpm, Grid.BpmWasGiven ? " (refined from the given tempo)" : string.Format(inv, " (first estimate {0:0.00})", Grid.CoarseBpm)));
            text.AppendLine(string.Format(inv, "First beat     {0:0.0000} s", Grid.FirstBeatSeconds));
            text.AppendLine(string.Format(inv, "First downbeat {0:0.0000} s (beat {1} of the first four; confidence {2:0.00})", Grid.FirstDownbeatSeconds, Grid.DownbeatIndex + 1, Grid.DownbeatConfidence));
            text.AppendLine(string.Format(inv, "Downbeat score {0:0.000}  {1:0.000}  {2:0.000}  {3:0.000}", Grid.DownbeatScores[0], Grid.DownbeatScores[1], Grid.DownbeatScores[2], Grid.DownbeatScores[3]));
            text.AppendLine(string.Format(inv, "Grid fit       {0} of {1} beats matched, spread {2:0.00} ms, transient alignment {3:+0.00;-0.00} ms", Grid.BeatsMatched, Grid.BeatsInAudio, Grid.ResidualMs, Grid.OnsetAlignmentMs));
            text.AppendLine(string.Format(inv, "Attacks        {0} beats placed, spread {1:0.00} ms; envelope tempo {2:0.0000}, fitted to attacks {3:0.0000}", Grid.TransientBeats, Grid.TransientSpreadMs, Grid.FluxBpm, Grid.FittedBpm));
            text.AppendLine(string.Format(inv, "Chunks         {0}, crossfade {1:0.0} ms", Chunks.Count, 1000.0 * CrossfadeFrames / SampleRate));
            if (Stretch != Sp16Stretch.Off)
                text.AppendLine("Time stretch   " + (Stretch == Sp16Stretch.Resample ? "RESMPL" : "M.TMP") + " on every sample");
            int choked = 0, looped = 0, padsInUse = 0;
            foreach (ChunkInfo chunk in Chunks)
                for (int layer = 0; layer < chunk.FileNames.Length; layer++)
                {
                    if (chunk.FileNames[layer] == null)
                        continue;
                    padsInUse++;
                    if (ChokeOf(chunk.Index, layer))
                        choked++;
                    if (LoopOf(chunk.Index, layer))
                        looped++;
                }
            if (choked > 0)
                text.AppendLine(string.Format(inv, "Choke groups   {0} of {1} pads, a group for each layer", choked, padsInUse));
            if (looped > 0)
                text.AppendLine(string.Format(inv, "Loop           {0} of {1} pads", looped, padsInUse));
            if (!Arrangement)
                text.AppendLine("Arrangement    written, arranger off" + (TempoInArrangement ? "" : ", no tempo in its sections"));
            else if (!TempoInArrangement)
                text.AppendLine("Arrangement    no tempo in its sections: plays at the unit's tempo");
            if (Layers.Count > 0)
            {
                var layers = new StringBuilder();
                foreach (ExportLayer layer in Layers)
                    layers.Append(layers.Length == 0 ? "" : ", ").Append(layer.Label).Append(" on ").Append(Sp16Project.OutputName(layer.Output)).Append(layer.IsMono ? " (mono)" : "");
                text.AppendLine(string.Format(inv, "Layers         {0}, a pad each in every chunk: {1}", Layers.Count, layers));
            }
            text.AppendLine(string.Format(inv, "Sample memory  {0:0.0} MB of {1:0} MB", Sp16Project.Megabytes(SampleBytes), Sp16Project.Megabytes(Sp16Project.SampleMemoryBytes)));
            int sliced = 0, placed = 0;
            foreach (ChunkInfo chunk in Chunks)
            {
                if (chunk.SliceFrames == null)
                    continue;
                sliced++;
                foreach (bool manual in chunk.SliceIsManual)
                    if (manual)
                        placed++;
            }
            if (sliced > 0)
                text.AppendLine(string.Format(inv, "Slices         one per beat on {0} chunk{1}, {2} placed by hand", sliced, sliced == 1 ? "" : "s", placed));
            foreach (ChunkInfo chunk in Chunks)
            {
                string pads = Layers.Count > 1
                    ? string.Format(inv, "pads {0,2}-{1,-2}", chunk.Pad + 1, chunk.PadOf(Layers.Count - 1) + 1)
                    : string.Format(inv, "pad {0,2}", chunk.Pad + 1);
                string files = chunk.SampleCount > 1
                    ? string.Format(inv, "{0} and {1} more", chunk.FileName, chunk.SampleCount - 1)
                    : chunk.FileName;
                if (chunk.SampleCount < chunk.FileNames.Length)
                    files += string.Format(inv, " ({0} silent, left out)", chunk.FileNames.Length - chunk.SampleCount);
                text.AppendLine(string.Format(inv, "  {0,3}  scene {1,2} {2} pattern {3,2}  {4} bar{5}  start {6,9:0.0000} s  length {7,8:0.0000} s  {8}",
                    chunk.Index + 1, chunk.Scene + 1, pads, chunk.Pattern + 1, chunk.Bars, chunk.Bars == 1 ? " " : "s",
                    (double)chunk.StartFrame / SampleRate, (double)chunk.SampleFrames / SampleRate, files));
            }
            foreach (string warning in Warnings)
                text.AppendLine("Warning: " + warning);
            if (TempoWarning != null)
                text.AppendLine("Warning: " + TempoWarning);
            return text.ToString();
        }
    }

    /// <summary>
    /// Turns one WAV into an SP-16 package that plays it from the arranger: the audio is cut on bar lines
    /// into chunks, one per pad, each triggered by its own pattern, with one arranger section per chunk.
    /// With layers (see <see cref="StemsToSp16"/>) a chunk is several samples cut at the same place,
    /// on a pad each.
    /// </summary>
    public static class WavToSp16
    {
        const int Sp16SampleRate = 44100;
        const int BeatsPerBar = 4;
        // Sample names with layers are a label, part of the project name and the chunk number,
        // and come to no more than a full mix's names, which the SP-16 has loaded.
        const int MaxLabelLength = 6;
        const int MaxPrefixLengthWithLabel = 5;
        // The lead-in pattern is not a whole number of bars: its Length is a count of steps, 1 to 64. The spec
        // has a unit-saved file with a part bar (Length 21, Confirmed, file), and the unit plays a
        // lead-in of 54 steps at that length in the arranger (Confirmed, SP-16, Stems112.tpkg).
        // The lead-in is a whole number of beats, 4 steps: silence is put in front of it back to
        // the beat line before its first sound, so that it starts on a beat and can be named by
        // one. It was a whole number of steps (1 here) until 2026-10-12, when a start on "beat 3½"
        // made no sense to the user. 16 would pad it to a whole bar.
        const int LeadInStepGranularity = 4;
        // A pattern in which every layer is silent keeps the first layer's sample, silent as it
        // is, so that it has a trigger. A pattern that is stored with a length and no trigger at
        // all has not been played on the unit (Unverified): only a pattern with no struct has,
        // and that one has no length of its own.
        const bool KeepOneSampleInASilentPattern = true;
        // The most tracks of one strip that get a colour of their own. The unit has 16 colours,
        // which is four for each of the four strips.
        const int ColoursToAStrip = Sp16Project.TrackColours / Sp16Project.OutputPairs;
        // A lead-in shorter than this is only the very front of the first hit, so the first chunk
        // starts on its bar line and there is no lead-in. It is the same whether the audio starts
        // with the file or after silence that was trimmed.
        const double MinLeadInSeconds = 0.030;
        // How far after a bar line the first sound may begin and still be that bar's first beat.
        // A line stands at the foot of its hit, and the sound only counts as begun a little later,
        // once it has risen to within 54 dB of the song's peak. With 2 ms here a song that opens
        // on a kick had its first bar taken for silence, and its first pattern started four bars in.
        const double FirstSoundToleranceSeconds = 0.030;
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<short[], object> firstSoundFrames =
            new System.Runtime.CompilerServices.ConditionalWeakTable<short[], object>();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<short[], BlockLevels> blockLevels =
            new System.Runtime.CompilerServices.ConditionalWeakTable<short[], BlockLevels>();

        public static WavExportResult Export(string wavPath, byte[] templatePrj, string packagePath, WavExportOptions options = null)
        {
            options = options ?? new WavExportOptions();
            WavData wav = AudioFile.Read(wavPath);
            string name = SanitizeName(string.IsNullOrEmpty(options.ProjectName) ? Path.GetFileNameWithoutExtension(wavPath) : options.ProjectName, 24);

            // Plan first, so nothing is written when the audio cannot be exported.
            WavExportResult result = Plan(wav, name, options);
            string folder = Path.GetDirectoryName(packagePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            WritePackage(wav, templatePrj, packagePath, result);
            return result;
        }

        /// <summary>
        /// Writes the package for a plan made earlier. When it is cancelled or fails part way, the
        /// unfinished file is removed.
        /// </summary>
        public static void WritePackage(WavData wav, byte[] templatePrj, string packagePath, WavExportResult plan,
            IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            WritePackage(new[] { wav.Samples }, templatePrj, packagePath, plan, progress, cancel);
        }

        /// <summary>Writes the package for a plan with layers.</summary>
        /// <param name="layerSamples">The audio of each layer, in the plan's order, all of the length the plan was made for.</param>
        public static void WritePackage(IList<short[]> layerSamples, byte[] templatePrj, string packagePath, WavExportResult plan,
            IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            string folder = Path.GetDirectoryName(packagePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            try
            {
                using (var output = new FileStream(packagePath, FileMode.Create, FileAccess.Write))
                    Write(layerSamples, templatePrj, output, plan, progress, cancel);
            }
            catch
            {
                File.Delete(packagePath);
                throw;
            }
            plan.PackagePath = packagePath;
        }

        public static WavExportResult Export(WavData wav, byte[] templatePrj, Stream output, string projectName, WavExportOptions options = null)
        {
            WavExportResult result = Plan(wav, SanitizeName(projectName, 24), options ?? new WavExportOptions());
            Write(new[] { wav.Samples }, templatePrj, output, result, null, CancellationToken.None);
            return result;
        }

        /// <summary>Analyses the audio and lays out the chunks without writing anything.</summary>
        public static WavExportResult Plan(WavData wav, string projectName, WavExportOptions options)
        {
            CheckSampleRate(wav);
            return Plan(wav, projectName, options, BeatGridAnalyzer.Analyze(wav.Samples, wav.SampleRate, options.Grid));
        }

        public static void CheckSampleRate(WavData wav)
        {
            if (wav.SampleRate != Sp16SampleRate)
                throw new NotSupportedException("SP-16 needs 44.1 kHz audio and this file is " + wav.SampleRate + " Hz. Convert it to 44.1 kHz first.");
        }

        /// <summary>
        /// Lays out the chunks on a grid that is already known, without analysing or writing anything.
        /// This is cheap, so it can be run again after every change to the grid.
        /// </summary>
        /// <param name="layers">
        /// The samples each chunk is made of, or null for a full mix. With layers,
        /// <paramref name="wav"/> is all of them added together: it gives the length and the first sound.
        /// </param>
        /// <param name="layerSamples">
        /// The audio of each layer, in the order of <paramref name="layers"/>. When it is given, a
        /// layer that is silent for the whole of a chunk gets no sample there, which saves the
        /// memory of it. Without it every layer has a sample in every chunk.
        /// </param>
        public static WavExportResult Plan(WavData wav, string projectName, WavExportOptions options, BeatGrid grid, IList<ExportLayer> layers = null, IList<short[]> layerSamples = null)
        {
            CheckSampleRate(wav);
            if (options.BarsPerChunk < 1 || options.BarsPerChunk > Sp16Project.MaxPatternLength / Sp16Project.StepsPerBar)
                throw new ArgumentOutOfRangeException("options", "BarsPerChunk must be 1 to 4.");
            if (layers != null && layers.Count > Sp16Project.PadsPerScene)
                throw new ArgumentException("A chunk takes a pad for each layer, and a scene has " + Sp16Project.PadsPerScene + " pads.", "layers");
            if (layers != null)
                CheckOutputs(layers);

            var result = new WavExportResult
            {
                ProjectName = projectName,
                SampleRate = wav.SampleRate,
                SourceFrames = wav.Frames,
                SourceBitsPerSample = wav.SourceBitsPerSample,
                SourceSampleRate = wav.SourceSampleRate,
                Grid = grid
            };
            if (layers != null)
                result.Layers.AddRange(layers);
            result.Pads = options.Pads;
            result.LoopFadeOutFrames = Math.Max(0, (int)Math.Round(options.LoopFadeOutMs / 1000.0 * wav.SampleRate));
            result.LoopFadeInFrames = Math.Max(0, (int)Math.Round(options.LoopFadeInMs / 1000.0 * wav.SampleRate));
            result.Stretch = options.Stretch;
            result.TempoInArrangement = options.TempoInArrangement;
            result.Arrangement = options.Arrangement;
            // A chunk's pads sit side by side in one scene, so a scene holds fewer chunks the more layers there are.
            int chunksPerScene = Sp16Project.PadsPerScene / result.SamplesPerChunk;
            int maxChunks = Sp16Project.Scenes * chunksPerScene;
            if (options.StartBar < 0)
                throw new ArgumentOutOfRangeException("options", "StartBar cannot be negative.");
            int rate = wav.SampleRate;
            int total = wav.Frames;
            double barFrames = BeatsPerBar * grid.BeatSeconds * rate;

            // Which bars start a chunk: those a whole number of chunks away from the anchor, or from
            // a place a pattern was told to start.
            var starts = new PatternStarts(options, grid);
            // From the start of the file the first chunk boundary is the first such bar, with whatever
            // is in front of it as the lead-in. From a chosen bar, the first chunk runs from that bar
            // to the next such bar, so it may be shorter than the rest.
            bool fromFileStart = options.KeepLeadIn && options.StartBar == 0;
            int firstBar = fromFileStart ? (starts.IsStart(0) ? 0 : starts.Next(0)) : options.StartBar;
            // Where the lead-in begins: the start of the file, or the first sound in it.
            int leadStart = fromFileStart && options.TrimLeadingSilence ? FirstSoundOnGrid(wav, grid) : 0;
            // Or a beat line further in, when the beats in front of it are dropped.
            bool fromBeat = false;
            if (fromFileStart && options.StartBeat.HasValue)
            {
                int beatFrame = (int)Math.Round(grid.TimeOfBeat(options.StartBeat.Value) * rate);
                if (beatFrame > leadStart)
                {
                    leadStart = beatFrame;
                    fromBeat = true;
                }
            }
            int leadTolerance = (int)(FirstSoundToleranceSeconds * rate);
            if (leadStart > 0)
                while ((int)Math.Round(grid.TimeOfBeat(firstBar * BeatsPerBar) * rate) < Math.Min(leadStart, total) - leadTolerance)
                    firstBar = starts.Next(firstBar);

            // Chunk boundaries, each rounded on its own from the grid so rounding never accumulates.
            var boundaries = new List<int>();
            var boundaryBars = new List<int>();
            for (int bar = firstBar; ; )
            {
                int frame = (int)Math.Round(grid.TimeOfBeat(bar * BeatsPerBar) * rate);
                if (frame >= total)
                    break;
                boundaries.Add(Math.Max(0, frame));
                boundaryBars.Add(bar);
                bar = starts.Next(bar);
            }
            // A sliver at the very end is not worth a pad; let the previous sample run to the end.
            if (boundaries.Count > 1 && total - boundaries[boundaries.Count - 1] < 0.25 * grid.BeatSeconds * rate)
                boundaries.RemoveAt(boundaries.Count - 1);
            if (boundaries.Count == 0)
                throw new InvalidOperationException(options.StartBar > 0 || fromBeat ? "No audio left after starting " + (fromBeat ? "beat." : "bar.") : "Audio too short to hold one bar.");
            boundaries.Add(total);
            leadStart = Math.Min(leadStart, boundaries[0]);
            int leadFrames = boundaries[0] - leadStart;
            bool leadIn = fromFileStart && leadFrames >= MinLeadInSeconds * rate;
            int patternsNeeded = boundaries.Count - 1 + (leadIn ? 1 : 0);
            if (patternsNeeded > maxChunks)
                throw new PlanRefusedException(TooLarge(result.Layers, patternsNeeded), patternsNeeded, true);
            result.PatternsNeeded = patternsNeeded;

            int shortest = leadIn ? leadFrames : int.MaxValue;
            for (int i = 0; i + 1 < boundaries.Count; i++)
                shortest = Math.Min(shortest, boundaries[i + 1] - boundaries[i]);
            int crossfade = Math.Max(0, Math.Min((int)Math.Round(options.CrossfadeMs / 1000.0 * rate), shortest / 4));
            // A sample that loops has to be exactly as long as its pattern, with no tail and no fade.
            // Nothing loops with the arrangement on (WavExportResult.LoopOf).
            if (options.Pads.AnyLoop && !options.Arrangement)
                crossfade = 0;
            result.CrossfadeFrames = crossfade;

            string[] labels = SampleLabels(result.Layers);
            int chunkCount = boundaries.Count - 1;
            if (leadIn)
            {
                // Pad the front with silence up to a whole number of steps, so that at the song's own
                // tempo the pattern ends, and the next sample starts, exactly on the first bar line.
                double unitFrames = barFrames / Sp16Project.StepsPerBar * LeadInStepGranularity;
                // From a beat line it is a whole number of beats already, but for a frame either way
                // where the two ends were rounded apart, which must not count as part of another
                // beat: a thousandth of one, a few frames, is let go.
                double unitsLong = leadFrames / unitFrames;
                int units = Math.Min(Sp16Project.MaxPatternLength / LeadInStepGranularity, Math.Max(1, (int)Math.Ceiling(unitsLong - 1e-3)));
                int silence = Math.Max(0, (int)Math.Round(units * unitFrames) - leadFrames);
                result.Chunks.Add(new ChunkInfo
                {
                    StartFrame = leadStart,
                    EndFrame = boundaries[0],
                    SampleFrames = silence + leadFrames + crossfade,
                    LeadingSilenceFrames = silence,
                    IsLeadIn = true,
                    Bars = (units * LeadInStepGranularity + Sp16Project.StepsPerBar - 1) / Sp16Project.StepsPerBar,
                    PatternLength = units * LeadInStepGranularity
                });
            }
            for (int i = 0; i < chunkCount; i++)
            {
                int start = boundaries[i], end = boundaries[i + 1];
                bool last = i == chunkCount - 1;
                int bars = Math.Max(1, Math.Min(options.BarsPerChunk, (int)Math.Ceiling((end - start) / barFrames - 0.02)));
                result.Chunks.Add(new ChunkInfo
                {
                    StartFrame = start,
                    EndFrame = end,
                    SampleFrames = (last ? end : Math.Min(end + crossfade, total)) - start,
                    StartBar = boundaryBars[i],
                    Bars = bars,
                    PatternLength = bars * Sp16Project.StepsPerBar
                });
            }
            for (int i = 0; i < result.Chunks.Count; i++)
            {
                ChunkInfo chunk = result.Chunks[i];
                chunk.Index = i;
                chunk.Scene = i / chunksPerScene;
                chunk.Place = i % chunksPerScene * result.SamplesPerChunk;
                chunk.Pattern = i % chunksPerScene;
                chunk.FileNames = SampleNames(projectName, labels, i, result.Chunks.Count);
                if ((double)chunk.SampleFrames / rate > Sp16Project.MaxSampleSeconds)
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "A {0}-bar sample at {1:0.00} BPM is longer than SP-16's {2:0} s limit. Use fewer bars per chunk.",
                        chunk.Bars, grid.Bpm, Sp16Project.MaxSampleSeconds));
            }

            if (options.SliceOnBeats)
                foreach (ChunkInfo chunk in result.Chunks)
                    LaySlices(chunk, grid, rate, options.ManualSliceFrames);
            if (layerSamples != null && result.Layers.Count > 0)
                LeaveOutSilence(result, layerSamples);
            else if (result.Layers.Count == 0)
                DropSilentEnd(result, new[] { blockLevels.GetValue(wav.Samples, samples => new BlockLevels(samples)) });

            if (result.SampleBytes > Sp16Project.SampleMemoryBytes)
                throw new PlanRefusedException(string.Format(CultureInfo.InvariantCulture,
                    "Calculated size of {0:0.0} MB is too large to fit.", Sp16Project.Megabytes(result.SampleBytes))
                    + WhatWouldHelp(result.Layers, true), result.PatternsNeeded, false);
            // What is left out at the front is said first, then what is put there.
            CultureInfo inv = CultureInfo.InvariantCulture;
            if (fromBeat)
                result.Warnings.Add(string.Format(inv, "Dropping first {0}, before {1}.", Span((double)leadStart / rate),
                    leadIn ? "starting beat" : "bar " + (boundaryBars[0] + 1).ToString(inv)));
            else if (leadStart > leadTolerance)
                result.Warnings.Add(string.Format(inv, "Dropping first {0} of silence.", Span((double)leadStart / rate)));
            if (boundaries[0] > 0 && !leadIn && leadStart == 0)
                result.Warnings.Add(string.Format(inv, "Dropping first {0}, before {1}.", Span((double)boundaries[0] / rate),
                    options.StartBar > 0 ? "bar " + (options.StartBar + 1).ToString(inv) : "first bar line"));
            // The silence takes the lead-in back to the beat line before its first sound, which is
            // the beat it then starts on, so that its pattern ends on the bar line.
            double paddedMs = leadIn ? 1000.0 * result.Chunks[0].LeadingSilenceFrames / rate : 0;
            if (paddedMs >= 0.5)
                result.Warnings.Add(string.Format(inv, "Padding {0:0} ms of silence to starting {1}.", paddedMs,
                    result.Chunks[0].PatternLength % (Sp16Project.StepsPerBar / BeatsPerBar) == 0 ? "beat" : "step"));
            if (result.SampleBytes > 0.85 * Sp16Project.SampleMemoryBytes)
                result.Warnings.Add("Over 85% of SP-16 sample memory. Load on unit from a fresh start.");
            if (grid.BeatsMatched < 16 || grid.BeatsMatched < grid.BeatsInAudio / 4)
                result.Warnings.Add("Few beats matched. Check tempo.");
            result.TempoWarning = TempoWarningFor(grid.Bpm);
            return result;
        }

        /// <summary>A length of time as a message gives it: in seconds, or in milliseconds when it is under a tenth of one.</summary>
        static string Span(double seconds)
        {
            return seconds >= 0.1 ? seconds.ToString("0.00", CultureInfo.InvariantCulture) + " seconds" : (1000 * seconds).ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        /// <summary>
        /// Which bars start a pattern: those a whole number of patterns on from the anchor, and from
        /// each place a pattern was told to start (<see cref="WavExportOptions.PatternStartSeconds"/>),
        /// which the counting starts again from. Asked of a bar, it gives the next bar after it that
        /// starts a pattern, so the plan and <see cref="LaterStartBars(WavData, WavExportOptions, BeatGrid)"/>
        /// walk the same bars.
        /// </summary>
        sealed class PatternStarts
        {
            readonly int phase, length;
            readonly List<int> restarts = new List<int>();

            public PatternStarts(WavExportOptions options, BeatGrid grid)
            {
                length = options.BarsPerChunk;
                double barSeconds = BeatsPerBar * grid.BeatSeconds;
                if (options.PatternAnchorSeconds.HasValue)
                {
                    long anchorBar = (long)Math.Round((options.PatternAnchorSeconds.Value - grid.FirstDownbeatSeconds) / barSeconds);
                    phase = (int)((anchorBar % length + length) % length);
                }
                foreach (double seconds in options.PatternStartSeconds)
                {
                    int bar = (int)Math.Round((seconds - grid.FirstDownbeatSeconds) / barSeconds);
                    if (!restarts.Contains(bar))
                        restarts.Add(bar);
                }
                restarts.Sort();
            }

            /// <summary>Whether a pattern starts on this bar.</summary>
            public bool IsStart(int bar)
            {
                return restarts.Contains(bar) || Next(bar - 1) == bar;
            }

            /// <summary>The first bar after this one that starts a pattern.</summary>
            public int Next(int bar)
            {
                // Counted from the last place told to start a pattern at or before this bar, or from the anchor.
                int from = int.MinValue;
                foreach (int restart in restarts)
                    if (restart <= bar)
                        from = restart;
                int next = from == int.MinValue
                    ? bar + (((phase - bar) % length + length) % length == 0 ? length : ((phase - bar) % length + length) % length)
                    : from + length * ((bar - from) / length + 1);
                // A place told to start a pattern comes first, cutting the one before it short.
                foreach (int restart in restarts)
                    if (restart > bar)
                        return Math.Min(next, restart);
                return next;
            }
        }

        /// <summary>
        /// The bars a package can start on when the beginning of the song is left out: every bar
        /// that starts a pattern, but for one the song begins on, which is the start of the file
        /// already. A plan told to start on one of them (<see cref="WavExportOptions.StartBar"/>,
        /// with no lead-in kept) is the whole plan without its first patterns, so leaving out the
        /// beginning never cuts a pattern short. They are found as <see cref="Plan(WavData, string, WavExportOptions, BeatGrid, IList{ExportLayer})"/>
        /// finds its boundaries, and a test holds the two together.
        /// </summary>
        public static List<int> LaterStartBars(WavData wav, WavExportOptions options, BeatGrid grid)
        {
            bool startsWithLeadIn;
            return LaterStartBars(wav, options, grid, out startsWithLeadIn);
        }

        /// <param name="startsWithLeadIn">Whether the whole song begins with a lead-in, and not on the start of its first pattern.</param>
        public static List<int> LaterStartBars(WavData wav, WavExportOptions options, BeatGrid grid, out bool startsWithLeadIn)
        {
            int rate = wav.SampleRate, total = wav.Frames;
            var starts = new PatternStarts(options, grid);
            int leadStart = options.TrimLeadingSilence ? FirstSoundOnGrid(wav, grid) : 0;
            int leadTolerance = (int)(FirstSoundToleranceSeconds * rate);
            int firstBar = starts.IsStart(0) ? 0 : starts.Next(0);
            if (leadStart > 0)
                while ((int)Math.Round(grid.TimeOfBeat(firstBar * BeatsPerBar) * rate) < Math.Min(leadStart, total) - leadTolerance)
                    firstBar = starts.Next(firstBar);

            var bars = new List<int>();
            int first = 0, last = 0;
            for (int bar = firstBar; ; bar = starts.Next(bar))
            {
                int frame = (int)Math.Round(grid.TimeOfBeat(bar * BeatsPerBar) * rate);
                if (frame >= total)
                    break;
                if (bars.Count == 0)
                    first = Math.Max(0, frame);
                last = frame;
                bars.Add(bar);
            }
            // The sliver at the very end that the plan gives to the pattern before it.
            if (bars.Count > 1 && total - last < 0.25 * grid.BeatSeconds * rate)
                bars.RemoveAt(bars.Count - 1);
            // With no lead-in in front of it, the first is where the package starts anyway.
            startsWithLeadIn = bars.Count > 0 && first - Math.Min(leadStart, first) >= MinLeadInSeconds * rate;
            if (bars.Count > 0 && !startsWithLeadIn)
                bars.RemoveAt(0);
            return bars;
        }

        /// <summary>
        /// The first beat line a package can start on that leaves something out
        /// (<see cref="WavExportOptions.StartBeat"/>): the first one after the song's first sound,
        /// counted from the first bar line. A lead-in starts on the beat line at or before its
        /// first sound, with silence put between the two, so this is the beat after the one the
        /// song starts on, and dropping a beat always moves the start on by one.
        /// </summary>
        public static int FirstLaterStartBeat(WavData wav, WavExportOptions options, BeatGrid grid)
        {
            double firstSound = options.TrimLeadingSilence ? (double)Math.Min(FirstSoundOnGrid(wav, grid), wav.Frames) / wav.SampleRate : 0;
            // A thousandth of a beat is allowed for a line's own frame having been rounded.
            return (int)Math.Floor((firstSound - grid.FirstDownbeatSeconds) / grid.BeatSeconds + 1e-3) + 1;
        }

        /// <summary>
        /// Where the song starts once silence in front is left out: at its first sound, or on the
        /// beat line after it when all there is before that line is a short run of faint sound
        /// (<see cref="LeadingSilence.IsFaintRunUpTo"/>). Such a run is not a lead-in: kept, it
        /// would be padded back to the beat before with silence, a beat of next to nothing in
        /// front of the first hit. It is left out with the silence.
        /// </summary>
        public static int FirstSoundOnGrid(WavData wav, BeatGrid grid)
        {
            int first = FirstSoundFrame(wav);
            if (first >= wav.Frames)
                return first;
            int beat = (int)Math.Ceiling(((double)first / wav.SampleRate - grid.FirstDownbeatSeconds) / grid.BeatSeconds);
            double line = grid.TimeOfBeat(beat);
            BlockLevels levels = blockLevels.GetValue(wav.Samples, samples => new BlockLevels(samples));
            bool faint = line * wav.SampleRate < wav.Frames && LeadingSilence.IsFaintRunUpTo(levels, first, line, grid.BeatSeconds, wav.SampleRate);
            return faint ? (int)Math.Round(line * wav.SampleRate) : first;
        }

        /// <summary>
        /// Takes away the samples of layers that are silent from one end of a chunk to the other:
        /// a stem that has not come in yet, or has stopped. Silence is what it is at the front of
        /// a file, 54 dB or more below the loudest sample of the layer, or what is as good as
        /// silent (<see cref="BlockLevels.IsAsGoodAsSilent"/>): the click or the bleed a separator
        /// leaves in a stem whose instrument is not playing (the user, 2026-10-13: "there seems to
        /// be near silence on the drums and bass but they're included"). The pad of such a sample
        /// is left empty and its pattern does not trigger it, which is what removing it from the
        /// pattern on the unit does. Patterns at the end of the song in which every layer is
        /// silent are dropped whole.
        /// </summary>
        static void LeaveOutSilence(WavExportResult plan, IList<short[]> layerSamples)
        {
            if (layerSamples.Count != plan.Layers.Count)
                throw new ArgumentException("The plan has " + plan.Layers.Count + " layers and audio for " + layerSamples.Count + " was given.", "layerSamples");
            var levels = new BlockLevels[layerSamples.Count];
            for (int layer = 0; layer < levels.Length; layer++)
                levels[layer] = blockLevels.GetValue(layerSamples[layer], samples => new BlockLevels(samples));
            DropSilentEnd(plan, levels);
            foreach (ChunkInfo chunk in plan.Chunks)
            {
                // Up to where the next chunk starts, and not the few milliseconds a sample runs on
                // under the next: there the next pattern's first hit is already rising, and a
                // stem that comes in on it would never count as silent before it.
                int sounding = 0;
                var silent = new bool[levels.Length];
                for (int layer = 0; layer < levels.Length; layer++)
                {
                    silent[layer] = levels[layer].IsAsGoodAsSilent(chunk.StartFrame, chunk.EndFrame);
                    if (!silent[layer])
                        sounding++;
                }
                for (int layer = 0; layer < levels.Length; layer++)
                    if (silent[layer] && (sounding > 0 || layer > 0 || !KeepOneSampleInASilentPattern))
                        chunk.FileNames[layer] = null;
            }
        }

        /// <summary>
        /// Drops the patterns at the end of the song that hold nothing but silence, in every layer:
        /// a file often runs on after its last sound, and a pattern of that is a scene's pads and
        /// the unit's time spent on nothing. Silence inside the song stays: it is part of the music.
        /// </summary>
        static void DropSilentEnd(WavExportResult plan, IList<BlockLevels> levels)
        {
            int keep = 0;
            for (int i = plan.Chunks.Count - 1; i >= 0 && keep == 0; i--)
            {
                ChunkInfo chunk = plan.Chunks[i];
                foreach (BlockLevels layer in levels)
                    if (!layer.IsAsGoodAsSilent(chunk.StartFrame, chunk.EndFrame))
                        keep = i + 1;
            }
            // Audio with no sound in it at all is laid out as it is: there is no end to find.
            if (keep > 0 && keep < plan.Chunks.Count)
                plan.Chunks.RemoveRange(keep, plan.Chunks.Count - keep);
        }

        /// <summary>
        /// The colour of each layer's track on the unit. The unit numbers its 16 colours as it
        /// numbers the tracks of a scene, and the four strips of the Source screen take the 1st,
        /// 5th, 9th and 13th (the user's choice, 2026-10-12: "Drums are correct, 01, Bass should be
        /// 05, Other is 09, Vocals are 13"), so that a stem is one colour in every pattern and not
        /// a different one on each of its pads. A second track of a strip takes the next colour
        /// along, up to the four each strip has room for.
        /// </summary>
        static int[] LayerColours(IList<ExportLayer> layers)
        {
            var colours = new int[layers.Count];
            var onStrip = new int[Sp16Project.OutputPairs];
            for (int layer = 0; layer < colours.Length; layer++)
            {
                int strip = StripOf(layers[layer]);
                colours[layer] = ColoursToAStrip * strip + Math.Min(onStrip[strip]++, ColoursToAStrip - 1);
            }
            return colours;
        }

        /// <summary>
        /// Which of the four strips a layer belongs to: the pair its output is, or is one of. On
        /// the master output, where there is no pair, it is the layer's group, which is the
        /// strip it was put on.
        /// </summary>
        static int StripOf(ExportLayer layer)
        {
            int strip = layer.Output == Sp16Output.Master ? layer.Group
                : layer.IsMono ? ((int)layer.Output - (int)Sp16Output.Out1) / 2
                : (int)layer.Output;
            return (strip % Sp16Project.OutputPairs + Sp16Project.OutputPairs) % Sp16Project.OutputPairs;
        }

        /// <summary>The first frame that is not silent, or the frame count when there is none.</summary>
        public static int FirstSoundFrame(WavData wav)
        {
            // Worked out once for each piece of audio: the plan is laid out again on every edit.
            return (int)firstSoundFrames.GetValue(wav.Samples, samples => LeadingSilence.FirstSoundFrame(samples));
        }

        /// <summary>
        /// One slice per beat. A beat line less than half a beat from either end of the sample is
        /// left out, so no slice is a sliver and a hand-placed point always has room. A chunk starts
        /// on a beat line; the lead-in does not, so its first slice is the part beat in front.
        /// </summary>
        static void LaySlices(ChunkInfo chunk, BeatGrid grid, int rate, List<int> manualFrames)
        {
            double beatFrames = grid.BeatSeconds * rate;
            int start = chunk.StartFrame - chunk.LeadingSilenceFrames;
            int end = start + chunk.SampleFrames;
            var frames = new List<int> { start };
            var manual = new List<bool> { false };
            int firstBeat = (int)Math.Ceiling(((start + 0.5 * beatFrames) / rate - grid.FirstDownbeatSeconds) / grid.BeatSeconds - 1e-6);
            for (int beat = firstBeat; frames.Count < Sp16Project.MaxSlices; beat++)
            {
                int line = (int)Math.Round(grid.TimeOfBeat(beat) * rate);
                if (line + 0.5 * beatFrames > end)
                    break;
                int frame = line;
                bool placed = false;
                double nearest = 0.5 * beatFrames;
                foreach (int candidate in manualFrames)
                {
                    double distance = Math.Abs(candidate - line);
                    if (distance < nearest)
                    {
                        nearest = distance;
                        frame = candidate;
                        placed = true;
                    }
                }
                frames.Add(frame);
                manual.Add(placed);
            }
            if (frames.Count < 2)
                return;
            frames.Add(end);
            manual.Add(false);
            chunk.SliceFrames = frames.ToArray();
            chunk.SliceIsManual = manual.ToArray();
        }

        // Said as a user can act on it: not how many patterns it needed, and not as the song's length,
        // since the cause with stems is the number of tracks (the user, 2026-10-16).
        static string TooLarge(IList<ExportLayer> layers, int patterns)
        {
            if (layers.Count == 0)
                return "Too large to fit on SP-16.";
            bool sum = false;
            for (int i = 0; i < layers.Count; i++)
                for (int j = i + 1; j < layers.Count; j++)
                    if (layers[j].Group == layers[i].Group && layers[j].Output == layers[i].Output)
                        sum = true;
            return TooManyTracks(layers.Count, patterns, sum);
        }

        /// <summary>
        /// The most tracks a pattern can have for a song of this many patterns to fit: a scene's
        /// pads are shared out among its patterns, so the more tracks, the fewer patterns. 0 when
        /// not even one track leaves room for them all.
        /// </summary>
        public static int MostTracksFor(int patterns)
        {
            int perScene = (Math.Max(1, patterns) + Sp16Project.Scenes - 1) / Sp16Project.Scenes;
            return perScene > Sp16Project.PadsPerScene ? 0 : Sp16Project.PadsPerScene / perScene;
        }

        /// <summary>
        /// Why a song of this many patterns does not fit with this many tracks, and how many it
        /// fits with (the user, 2026-10-17: making the stems mono brought the memory under the
        /// limit and the song was still refused, since a mono track takes a pad as a stereo one does).
        /// </summary>
        public static string TooManyTracks(int tracks, int patterns, bool canSum)
        {
            int most = MostTracksFor(patterns);
            if (most == 0)
                return "Too large to fit on SP-16.";
            return string.Format(CultureInfo.InvariantCulture, "Too large to fit with {0} tracks. Fits with {1} or fewer. ", tracks, most)
                + WhatWouldHelp(canSum, false);
        }

        /// <summary>
        /// What would bring the layers down, naming only what can be done with them as they are
        /// (the user, 2026-10-16): summing where two layers share a strip and an output, mono where
        /// a layer is on a stereo pair (on master there is no mono), and always removing tracks.
        /// Mono saves memory but not pads, so it is only offered for memory. Empty for a full mix.
        /// </summary>
        static string WhatWouldHelp(IList<ExportLayer> layers, bool forMemory)
        {
            if (layers.Count == 0)
                return "";
            bool sum = false, mono = false;
            for (int i = 0; i < layers.Count; i++)
            {
                if (forMemory && layers[i].Output != Sp16Output.Master && !layers[i].IsMono)
                    mono = true;
                for (int j = i + 1; j < layers.Count; j++)
                    if (layers[j].Group == layers[i].Group && layers[j].Output == layers[i].Output)
                        sum = true;
            }
            return " " + WhatWouldHelp(sum, mono);
        }

        /// <summary>
        /// "Sum stems, use mono, or remove tracks.", with only the first two that apply. Removing
        /// tracks always does. The Source screen's estimate says it the same way.
        /// </summary>
        public static string WhatWouldHelp(bool sum, bool mono)
        {
            var help = new List<string>();
            if (sum)
                help.Add("sum stems");
            if (mono)
                help.Add("use mono");
            help.Add("remove tracks");
            string text = help.Count == 1 ? help[0] : string.Join(", ", help.GetRange(0, help.Count - 1).ToArray()) + (help.Count > 2 ? "," : "") + " or " + help[help.Count - 1];
            return char.ToUpperInvariant(text[0]) + text.Substring(1) + ".";
        }

        /// <summary>
        /// The layers' labels as they go into file names: short, and no two the same, whatever the
        /// case, because every sample of a package sits in one folder.
        /// </summary>
        static string[] SampleLabels(List<ExportLayer> layers)
        {
            var labels = new string[layers.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                string wanted = string.IsNullOrEmpty(layers[i].Label) ? "L" : SanitizeName(layers[i].Label, MaxLabelLength);
                string label = wanted;
                // A label that is taken gives way to the same one with a number on the end.
                for (int number = 2; IndexOfLabel(labels, i, label) >= 0; number++)
                {
                    string digits = number.ToString(CultureInfo.InvariantCulture);
                    label = wanted.Substring(0, Math.Min(wanted.Length, MaxLabelLength - digits.Length)) + digits;
                }
                labels[i] = label;
            }
            return labels;
        }

        static int IndexOfLabel(string[] labels, int count, string label)
        {
            for (int i = 0; i < count; i++)
                if (string.Equals(labels[i], label, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // A song in one file numbers its samples in two figures while that is enough, which is
        // how they are read on the unit's screen and on the app's pads.
        const int SamplesNumberedInTwoFigures = 99;

        static string[] SampleNames(string projectName, string[] labels, int chunkIndex, int chunkCount)
        {
            if (labels.Length == 0)
                return new[] { Shorten(projectName, 12) + "_" + (chunkIndex + 1).ToString(chunkCount <= SamplesNumberedInTwoFigures ? "00" : "000", CultureInfo.InvariantCulture) + ".wav" };
            string number = (chunkIndex + 1).ToString("000", CultureInfo.InvariantCulture);
            // The label goes first, so that it is what shows where the unit has room for part of a name.
            var names = new string[labels.Length];
            for (int layer = 0; layer < names.Length; layer++)
                names[layer] = labels[layer] + "_" + Shorten(projectName, MaxPrefixLengthWithLabel) + "_" + number + ".wav";
            return names;
        }

        static string Shorten(string text, int maxLength)
        {
            return text.Length > maxLength ? text.Substring(0, maxLength) : text;
        }

        /// <summary>
        /// Layers are all on the master output or none of them is. The mode the unit starts in can
        /// send some pads to master and others to 3-4, 5-6 or 7-8 (format spec section 7), but no
        /// generated project has tried it, so it is refused and not guessed at.
        /// </summary>
        static void CheckOutputs(IList<ExportLayer> layers)
        {
            int onMaster = 0;
            foreach (ExportLayer layer in layers)
                if (layer.Output == Sp16Output.Master)
                    onMaster++;
            if (onMaster > 0 && onMaster < layers.Count)
                throw new ArgumentException("Stems on master output and stems on other outputs cannot go into one package. Put all on master, or none.", "layers");
        }

        /// <summary>
        /// Whether a plan leaves the routing of every scene as the template has it, which is on the
        /// master output: a full mix, or layers that are on master. For layers this is the one thing
        /// the SP-16 has not been asked yet. A pad on master has played there in every full mix,
        /// and several pads started together in every package of stems, but several pads started
        /// together on master have not (Unverified).
        /// </summary>
        static bool StaysOnMaster(WavExportResult plan)
        {
            return plan.Layers.Count == 0 || plan.Layers[0].Output == Sp16Output.Master;
        }

        /// <summary>
        /// The output of every pad in a scene of a plan with layers. A chunk's pads follow the
        /// layers, so the pattern repeats down the scene, the same in every scene. All 16 are
        /// written, in use or not: in 8ch mode the unit has no setting that leaves a pad unassigned,
        /// and gives one it has nothing for the first pair (format spec section 7).
        /// </summary>
        static string[] PadOutputs(WavExportResult plan)
        {
            int layers = plan.Layers.Count;
            int inChunks = Sp16Project.PadsPerScene / layers * layers;
            var outputs = new string[Sp16Project.PadsPerScene];
            for (int place = 0; place < outputs.Length; place++)
                outputs[Sp16Project.PadInReadingOrder(place)] = Sp16Project.OutputValue(place < inChunks ? plan.Layers[place % layers].Output : Sp16Output.Out12);
            return outputs;
        }

        static void Write(IList<short[]> layerSamples, byte[] templatePrj, Stream output, WavExportResult plan, IProgress<float> progress, CancellationToken cancel)
        {
            if (layerSamples.Count != plan.SamplesPerChunk)
                throw new ArgumentException("The plan has " + plan.SamplesPerChunk + " samples to a chunk and audio for " + layerSamples.Count + " was given.", "layerSamples");
            foreach (short[] samples in layerSamples)
                if (samples.Length != plan.SourceFrames * 2)
                    throw new ArgumentException("The audio is not the length the plan was made for.", "layerSamples");

            CheckOutputs(plan.Layers);
            Sp16Project project = Sp16Project.FromPrj(templatePrj);
            project.ProjectBpm = plan.Grid.Bpm;
            project.PlayArrangement = plan.Arrangement;
            project.SceneIndex = 0;
            project.PatternIndex = 0;
            project.SetProjectName(plan.ProjectName);

            // An explicit tempo goes into every section, because an override on the unit is sticky.
            // Left out (0, no override), the song plays at the unit's own tempo and can follow it.
            float sectionBpm = plan.TempoInArrangement ? (float)plan.Grid.Bpm : 0f;
            Arrangement arrangement = Arrangement.CreateEmpty();
            // A full mix stays on the master output, which is how the template has every scene, and so do layers on master.
            string[] padOutputs = StaysOnMaster(plan) ? null : PadOutputs(plan);
            // A full mix keeps the colours the unit gives its tracks. Stems are coloured by their strips.
            int[] layerColours = plan.Layers.Count == 0 ? null : LayerColours(plan.Layers);
            double framesToABar = BeatsPerBar * plan.Grid.BeatSeconds * plan.SampleRate;
            int namedScene = -1;
            foreach (ChunkInfo chunk in plan.Chunks)
            {
                if (chunk.Scene != namedScene)
                {
                    namedScene = chunk.Scene;
                    project.RemovePatterns(chunk.Scene);
                    project.SetSceneName(chunk.Scene, "Part" + (chunk.Scene + 1).ToString(CultureInfo.InvariantCulture));
                    if (padOutputs != null)
                        project.SetRouting8Ch(chunk.Scene, padOutputs);
                }

                // Every sample of the chunk starts on step 1, each from a pad of its own. A layer
                // that is silent here has no sample: its pad stays empty and has no trigger. It still
                // takes its stem's colour, choke group, loop, velocity and stretch, as the unit lets an empty
                // pad have, so a sample put on it there behaves as the stem's others do (the user,
                // 2026-10-16).
                StepsBlob steps = StepsBlob.CreateEmpty();
                for (int layer = 0; layer < chunk.FileNames.Length; layer++)
                {
                    int pad = chunk.PadOf(layer);
                    bool hasSample = chunk.FileNames[layer] != null;
                    // A sample sets its pad back to no choke, loop or stretch, so it goes first.
                    if (hasSample)
                        project.SetPadSample(chunk.Scene, pad, chunk.FileNames[layer], chunk.SampleFrames);
                    if (plan.ChokeOf(chunk.Index, layer))
                        project.SetPadChokeGroup(chunk.Scene, pad, layer);
                    if (plan.Stretch != Sp16Stretch.Off)
                        project.SetPadStretch(chunk.Scene, pad, plan.Stretch);
                    if (plan.LoopOf(chunk.Index, layer))
                        project.SetPadLoop(chunk.Scene, pad, true);
                    if (plan.Pads.Velocity(chunk.Index, layer))
                        project.SetPadVelocity(chunk.Scene, pad, true);
                    if (layerColours != null)
                        project.SetPadColour(chunk.Scene, pad, layerColours[layer]);
                    if (!hasSample)
                        continue;
                    if (chunk.SliceFrames != null)
                        project.SetSliceBoundaries(chunk.Scene, pad, chunk.SliceBoundariesInSample());
                    steps.SetTrigger(pad, 0);
                }
                project.SetPattern(chunk.Scene, chunk.Pattern, steps, chunk.PatternLength);
                arrangement.Add(new Section(chunk.Scene, chunk.Pattern, 1, sectionBpm));
            }
            project.SetArrangement(arrangement);

            using (var zip = new StoredZipWriter(output))
            {
                zip.Add(plan.ProjectName + ".prj", project.ToPrj());
                int written = 0, samplesInAll = 0;
                foreach (ChunkInfo chunk in plan.Chunks)
                    samplesInAll += chunk.SampleCount;
                for (int i = 0; i < plan.Chunks.Count; i++)
                {
                    ChunkInfo chunk = plan.Chunks[i];
                    // How long the sample is in bars, to the step, without the tail it plays on into the next.
                    double bars = Math.Round((chunk.EndFrame - chunk.StartFrame + chunk.LeadingSilenceFrames) / framesToABar * Sp16Project.StepsPerBar) / Sp16Project.StepsPerBar;
                    for (int layer = 0; layer < chunk.FileNames.Length; layer++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (chunk.FileNames[layer] == null)
                            continue;
                        bool mono = plan.Layers.Count > 0 && plan.Layers[layer].IsMono;
                        short[] samples = SampleOf(layerSamples, plan, i, layer);
                        zip.Add(chunk.FileNames[layer], mono ? WavWriter.WriteMono(samples, plan.SampleRate) : WavWriter.Write(samples, plan.SampleRate));
                        zip.Add(chunk.FileNames[layer] + ".dat", SampleAdjunct.ForSample(chunk.SampleFrames, bars, plan.Grid.Bpm).Write());
                        written++;
                        if (progress != null)
                            progress.Report((float)written / samplesInAll);
                    }
                }
            }
        }

        /// <summary>Frames that chunk <paramref name="index"/> plays on into the chunk after it.</summary>
        static int OverlapAfter(WavExportResult plan, int index)
        {
            ChunkInfo chunk = plan.Chunks[index];
            return chunk.StartFrame + chunk.SampleFrames - chunk.LeadingSilenceFrames - chunk.EndFrame;
        }

        /// <summary>
        /// One sample of a package as it is written: a layer's stretch of the audio with the fades
        /// of its joins, or of its loop, mono for a layer on a single output, and with the lead-in's
        /// silence in front. The package is written from this, and the Export page plays it.
        /// </summary>
        public static short[] SampleOf(IList<short[]> layerSamples, WavExportResult plan, int chunkIndex, int layer)
        {
            ChunkInfo chunk = plan.Chunks[chunkIndex];
            // A sample fades in under the end of the one before it. Where that one was left out for
            // being silent there is nothing to fade in under, and the sample starts at full level.
            bool follows = chunkIndex > 0 && (plan.Chunks[chunkIndex - 1].FileNames == null || plan.Chunks[chunkIndex - 1].FileNames[layer] != null);
            int fadeIn = follows ? OverlapAfter(plan, chunkIndex - 1) : 0;
            int fadeOut = chunkIndex + 1 < plan.Chunks.Count ? OverlapAfter(plan, chunkIndex) : 0;
            bool mono = plan.Layers.Count > 0 && plan.Layers[layer].IsMono;
            int channels = mono ? 1 : 2;
            int frames = chunk.SampleFrames - chunk.LeadingSilenceFrames;
            // A sample that loops is brought down to nothing at its two ends, where it goes round.
            bool loops = plan.LoopOf(chunk.Index, layer);
            int padFadeIn = loops ? Math.Min(frames / 4, Math.Max(fadeIn, plan.LoopFadeInFrames)) : fadeIn;
            int padFadeOut = loops ? Math.Min(frames / 4, Math.Max(fadeOut, plan.LoopFadeOutFrames)) : fadeOut;
            short[] samples = mono
                ? CutChunkMono(layerSamples[layer], chunk.StartFrame, frames, padFadeIn, padFadeOut)
                : CutChunk(layerSamples[layer], chunk.StartFrame, frames, padFadeIn, padFadeOut);
            if (chunk.LeadingSilenceFrames > 0)
            {
                var padded = new short[chunk.SampleFrames * channels];
                Array.Copy(samples, 0, padded, chunk.LeadingSilenceFrames * channels, samples.Length);
                samples = padded;
            }
            return samples;
        }

        /// <summary>
        /// Copies a chunk with a linear fade in over its first frames and a linear fade out over its
        /// last ones. The two fades of a join add up to exactly 1, so the join is transparent when
        /// the next sample starts on time.
        /// </summary>
        public static short[] CutChunk(short[] source, int startFrame, int frames, int fadeInFrames, int fadeOutFrames)
        {
            var chunk = new short[frames * 2];
            Buffer.BlockCopy(source, startFrame * 4, chunk, 0, frames * 4);
            for (int i = 0; i < fadeInFrames; i++)
                Scale(chunk, i, (i + 0.5) / fadeInFrames);
            for (int i = 0; i < fadeOutFrames; i++)
                Scale(chunk, frames - fadeOutFrames + i, 1.0 - (i + 0.5) / fadeOutFrames);
            return chunk;
        }

        /// <summary>
        /// The same cut for a sample that goes to one output: the two sides are added and halved,
        /// which leaves audio that is the same on both sides exactly as it was. The fades are
        /// those of <see cref="CutChunk"/>.
        /// </summary>
        public static short[] CutChunkMono(short[] source, int startFrame, int frames, int fadeInFrames, int fadeOutFrames)
        {
            var chunk = new short[frames];
            for (int i = 0; i < frames; i++)
            {
                double gain = 0.5;
                if (i < fadeInFrames)
                    gain *= (i + 0.5) / fadeInFrames;
                if (i >= frames - fadeOutFrames)
                    gain *= 1.0 - (i - (frames - fadeOutFrames) + 0.5) / fadeOutFrames;
                int at = (startFrame + i) * 2;
                chunk[i] = (short)Math.Round((source[at] + source[at + 1]) * gain);
            }
            return chunk;
        }

        static void Scale(short[] samples, int frame, double gain)
        {
            samples[frame * 2] = (short)Math.Round(samples[frame * 2] * gain);
            samples[frame * 2 + 1] = (short)Math.Round(samples[frame * 2 + 1] * gain);
        }

        /// <summary>
        /// What to say of a tempo outside the 60 to 220 BPM the SP-16 and DJS-1000 play, or null inside it.
        /// The user's wording (2026-10-15).
        /// </summary>
        public static string TempoWarningFor(double bpm)
        {
            if (bpm < Sp16Project.MinTempoBpm)
                return string.Format(CultureInfo.InvariantCulture, "Under {0:0} BPM minimum, will play out of time", Sp16Project.MinTempoBpm);
            if (bpm > Sp16Project.MaxTempoBpm)
                return string.Format(CultureInfo.InvariantCulture, "Above {0:0} BPM maximum, will play out of time", Sp16Project.MaxTempoBpm);
            return null;
        }

        /// <summary>Keeps letters, digits, '-' and '_'; anything else becomes '_'.</summary>
        public static string SanitizeName(string name, int maxLength)
        {
            var clean = new StringBuilder();
            foreach (char c in name ?? "")
            {
                bool keep = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                clean.Append(keep ? c : '_');
                if (clean.Length == maxLength)
                    break;
            }
            return clean.Length == 0 ? "Strands" : clean.ToString();
        }
    }

    /// <summary>Makes a copy of the audio with a click on every beat, for checking a grid by ear.</summary>
    public static class BeatGridPreview
    {
        public static byte[] WriteClickTrack(WavData wav, BeatGrid grid)
        {
            // The music is halved so the clicks stand out without clipping.
            var samples = new short[wav.Samples.Length];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)(wav.Samples[i] / 2);
            int clickFrames = (int)(0.012 * wav.SampleRate);
            for (int beat = -grid.DownbeatIndex; ; beat++)
            {
                int start = (int)Math.Round(grid.TimeOfBeat(beat) * wav.SampleRate);
                if (start >= wav.Frames)
                    break;
                if (start < 0)
                    continue;
                // Downbeats get a higher click than the other beats.
                bool downbeat = ((beat % 4) + 4) % 4 == 0;
                double frequency = downbeat ? 2000 : 1000;
                for (int i = 0; i < clickFrames && start + i < wav.Frames; i++)
                {
                    double envelope = 1.0 - (double)i / clickFrames;
                    int click = (int)(12000 * envelope * Math.Sin(2 * Math.PI * frequency * i / wav.SampleRate));
                    for (int channel = 0; channel < 2; channel++)
                    {
                        int index = (start + i) * 2 + channel;
                        samples[index] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, samples[index] + click));
                    }
                }
            }
            return WavWriter.Write(samples, wav.SampleRate);
        }
    }
}
