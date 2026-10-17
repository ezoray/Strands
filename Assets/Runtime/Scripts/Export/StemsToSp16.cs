using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Strands.Audio;
using Strands.Sp16;

namespace Strands.Export
{
    /// <summary>One stem as it was read, and the output it is to play from.</summary>
    public sealed class Stem
    {
        /// <summary>The file's name without its extension.</summary>
        public string Name;
        public WavData Audio;
        public Sp16Output Output;
        /// <summary>
        /// Stems are only added together with others of their own group. On the screen it is the
        /// strip a stem is on, which keeps the strips apart when all of them are on the master
        /// output and so share it. Left at 0, every stem on one output is in one group.
        /// </summary>
        public int Group;
        /// <summary>Make this stem a layer of its own even when others share its output.</summary>
        public bool KeepApart;
    }

    /// <summary>
    /// A song's stems made ready for export: the stems at one common length, the layers each chunk
    /// will hold, and all of it added together.
    /// </summary>
    public sealed class StemSet
    {
        /// <summary>A short name for each stem, from its file name.</summary>
        public readonly List<string> StemLabels = new List<string>();
        /// <summary>The audio of each stem, in the order of <see cref="StemLabels"/>.</summary>
        public readonly List<short[]> StemSamples = new List<short[]>();
        public readonly List<ExportLayer> Layers = new List<ExportLayer>();
        /// <summary>The audio of each layer, in the order of <see cref="Layers"/>.</summary>
        public readonly List<short[]> LayerSamples = new List<short[]>();
        /// <summary>The stems each layer is made of, as indices into <see cref="StemSamples"/>, in the order of <see cref="Layers"/>.</summary>
        public readonly List<int[]> LayerStems = new List<int[]>();
        /// <summary>Every stem added together.</summary>
        public WavData Mix;
        public readonly List<string> Warnings = new List<string>();

        /// <summary>Lays the chunks out on a grid, one pad per layer in each. See <see cref="WavToSp16.Plan(WavData, string, WavExportOptions, BeatGrid, IList{ExportLayer})"/>.</summary>
        public WavExportResult Plan(string projectName, WavExportOptions options, BeatGrid grid)
        {
            WavExportResult plan = WavToSp16.Plan(Mix, projectName, options, grid, Layers, LayerSamples);
            plan.AddWarnings(Warnings);
            return plan;
        }

        public void WritePackage(byte[] templatePrj, string packagePath, WavExportResult plan,
            IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            WavToSp16.WritePackage(LayerSamples, templatePrj, packagePath, plan, progress, cancel);
        }
    }

    /// <summary>A grid found from one stem, or from all the stems added together.</summary>
    public sealed class GridCandidate
    {
        /// <summary>The stem's label, or null for the stems added together.</summary>
        public string Stem;
        /// <summary>The grid, or null when none could be found.</summary>
        public BeatGrid Grid;
        /// <summary>Why there is no grid.</summary>
        public string Error;
        /// <summary>
        /// How much the level of the whole song rises across this grid's lines
        /// (<see cref="BeatDriftMeter.AttackOnLines"/> on the stems added together).
        /// </summary>
        public double Attack;

        public string Name { get { return Stem ?? "all stems"; } }
    }

    /// <summary>The grids the stems gave, and the one that fits the song best.</summary>
    public sealed class GridChoice
    {
        public readonly List<GridCandidate> Candidates = new List<GridCandidate>();
        public GridCandidate Chosen;
        public readonly List<string> Warnings = new List<string>();

        public BeatGrid Grid { get { return Chosen.Grid; } }

        public string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            text.AppendLine("Grid from      " + Chosen.Name + ", whose lines have the most attack on them in the song as a whole");
            foreach (GridCandidate candidate in Candidates)
            {
                if (candidate.Grid == null)
                {
                    text.AppendLine(string.Format(inv, "  {0,-12} no grid: {1}", candidate.Name, candidate.Error));
                    continue;
                }
                BeatGrid grid = candidate.Grid;
                text.AppendLine(string.Format(inv, "  {0,-12} {1,8:0.0000} BPM  first downbeat {2,7:0.0000} s  {3,3} beats placed, spread {4:0.00} ms  attack {5:0.0000}{6}",
                    candidate.Name, grid.Bpm, grid.FirstDownbeatSeconds, grid.TransientBeats, grid.TransientSpreadMs, candidate.Attack,
                    candidate == Chosen ? "  <- taken" : ""));
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// Turns the stems of one song into an SP-16 package: the same chunks as a full mix, with each
    /// chunk holding one sample per output, all started together by the chunk's pattern.
    /// Pads triggered on the same step, with choke Off, all sound on the SP-16 (Confirmed, SP-16,
    /// 2026-10-10, with four stems over three scenes).
    /// </summary>
    public static class StemsToSp16
    {
        // An analysis holds a copy of its audio as it works, about 10 MB a minute.
        const int MaxAnalysesAtOnce = 6;

        /// <summary>Outputs are handed out in the order the stems come, 1-2 to 7-8 and then round again.</summary>
        public static Sp16Output DefaultOutput(int stemIndex)
        {
            return (Sp16Output)(stemIndex % Sp16Project.OutputPairs);
        }

        /// <summary>
        /// Reads one file per stem and gives each its default output. A stem file
        /// (<see cref="StemFile"/>) gives every stem it holds, in its own order. An MP4 that is
        /// not a stem file is one stem like any other file.
        /// </summary>
        public static List<Stem> Read(IList<string> paths)
        {
            var stems = new List<Stem>();
            foreach (string path in paths)
            {
                if (StemFile.HasStemExtension(path) && StemFile.IsStemFile(path))
                {
                    foreach (StemFileTrack track in StemFile.Read(path))
                        stems.Add(new Stem { Name = track.Name, Audio = track.Audio, Output = DefaultOutput(stems.Count) });
                    continue;
                }
                stems.Add(new Stem { Name = Path.GetFileNameWithoutExtension(path), Audio = AudioFile.Read(path), Output = DefaultOutput(stems.Count) });
            }
            return stems;
        }

        /// <summary>
        /// Makes the layers from the stems. Stems are taken to start together; shorter ones are
        /// filled out with silence at the end.
        /// </summary>
        /// <param name="mixSharedOutputs">
        /// Add stems that share an output, and are of one group, into one sample, which saves pads
        /// and sample memory. Otherwise every stem is a layer of its own, in the order given.
        /// </param>
        /// <summary>The rate the stems were converted from when they all were and from the same one, otherwise 0.</summary>
        static int SharedSourceRate(IList<Stem> stems)
        {
            foreach (Stem stem in stems)
                if (stem.Audio.SourceSampleRate != stems[0].Audio.SourceSampleRate)
                    return 0;
            return stems[0].Audio.SourceSampleRate;
        }

        public static StemSet Prepare(IList<Stem> stems, bool mixSharedOutputs = true)
        {
            if (stems == null || stems.Count == 0)
                throw new ArgumentException("At least one stem is needed.", "stems");
            int frames = 0, shortest = int.MaxValue;
            foreach (Stem stem in stems)
            {
                WavToSp16.CheckSampleRate(stem.Audio);
                frames = Math.Max(frames, stem.Audio.Frames);
                shortest = Math.Min(shortest, stem.Audio.Frames);
            }

            var set = new StemSet();
            int rate = stems[0].Audio.SampleRate;
            if (shortest != frames)
                set.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "Stems are not the same length ({0:0.000} to {1:0.000} s). Shorter ones padded with silence.",
                    (double)shortest / rate, (double)frames / rate));

            var names = new string[stems.Count];
            var audio = new short[stems.Count][];
            for (int i = 0; i < stems.Count; i++)
            {
                names[i] = stems[i].Name;
                audio[i] = ToLength(stems[i].Audio.Samples, frames);
            }
            string[] labels = Labels(names);
            set.StemLabels.AddRange(labels);
            set.StemSamples.AddRange(audio);

            // One layer for each output in use, and on it for each group, in the order the stems
            // first come to it, so that the pads of a chunk follow the order the stems were given
            // in. A stem kept apart is a layer of its own, where it stands.
            var taken = new bool[stems.Count];
            for (int s = 0; s < stems.Count; s++)
            {
                Sp16Output output = stems[s].Output;
                int group = stems[s].Group;
                if (!mixSharedOutputs || stems[s].KeepApart)
                {
                    set.Layers.Add(new ExportLayer { Label = labels[s], Output = output, Group = group });
                    set.LayerSamples.Add(audio[s]);
                    set.LayerStems.Add(new[] { s });
                    continue;
                }
                if (taken[s])
                    continue;
                var sharing = new List<int>();
                for (int i = 0; i < stems.Count; i++)
                {
                    if (stems[i].Output != output || stems[i].Group != group || stems[i].KeepApart)
                        continue;
                    sharing.Add(i);
                    taken[i] = true;
                }
                if (sharing.Count == 1)
                {
                    set.Layers.Add(new ExportLayer { Label = labels[s], Output = output, Group = group });
                    set.LayerSamples.Add(audio[s]);
                    set.LayerStems.Add(new[] { s });
                    continue;
                }
                var parts = new List<short[]>();
                var mixed = new List<string>();
                foreach (int i in sharing)
                {
                    parts.Add(audio[i]);
                    mixed.Add(labels[i]);
                }
                int clipped;
                // A mix is named after its output. On master that says nothing, so there it is numbered by its group.
                bool onMaster = output == Sp16Output.Master;
                string mixLabel = "Mix" + (onMaster ? (group + 1).ToString(CultureInfo.InvariantCulture) : Sp16Project.OutputName(output).Replace("-", ""));
                set.Layers.Add(new ExportLayer { Label = mixLabel, Output = output, Group = group });
                set.LayerSamples.Add(Add(parts, out clipped));
                set.LayerStems.Add(sharing.ToArray());
                if (clipped > 0)
                    set.Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0} summed are too loud together and will distort. Use Separate tracks, or turn the files down.",
                        string.Join(" and ", mixed.ToArray())));
            }
            if (set.Layers.Count > Sp16Project.PadsPerScene)
                throw new ArgumentException("A scene has " + Sp16Project.PadsPerScene + " pads, so no more than that many stems can be kept apart.", "stems");

            set.Mix = new WavData
            {
                SampleRate = rate,
                Samples = audio.Length == 1 ? audio[0] : AddToFit(audio),
                SourceChannels = 2,
                SourceBitsPerSample = stems[0].Audio.SourceBitsPerSample,
                SourceSampleRate = SharedSourceRate(stems)
            };
            return set;
        }

        /// <summary>
        /// Files added together into one song, as the stems are for finding the beat: shorter ones
        /// filled out with silence, and the whole turned down when the sum would not fit in 16 bits.
        /// One file is given back as it is.
        /// </summary>
        public static WavData MixOf(IList<WavData> files)
        {
            if (files.Count == 1)
                return files[0];
            int frames = 0;
            foreach (WavData file in files)
                frames = Math.Max(frames, file.Frames);
            var audio = new short[files.Count][];
            for (int i = 0; i < audio.Length; i++)
            {
                WavToSp16.CheckSampleRate(files[i]);
                audio[i] = ToLength(files[i].Samples, frames);
            }
            return new WavData { SampleRate = files[0].SampleRate, Samples = AddToFit(audio), SourceChannels = 2, SourceBitsPerSample = files[0].SourceBitsPerSample };
        }

        /// <summary>
        /// Finds a grid from every stem on its own and from all of them added together, and takes
        /// the one whose lines have the most attack on them in the song as a whole.
        /// The stems together are not always the best guide: a steady sound in the bass can hide the
        /// kick from the analyser, and a part without drums can pull the grid on to something
        /// else, where the drums alone are read correctly. A stem without a beat gives a grid too,
        /// of a kind, and can agree with itself at the wrong tempo; measuring every grid against
        /// the whole song is what sets those aside.
        /// </summary>
        public static GridChoice ChooseGrid(StemSet stems, BeatGridOptions options = null,
            IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            var choice = new GridChoice();
            var audio = new List<short[]>();
            for (int i = 0; i < stems.StemSamples.Count; i++)
            {
                choice.Candidates.Add(new GridCandidate { Stem = stems.StemLabels[i] });
                audio.Add(stems.StemSamples[i]);
            }
            // One stem added together is that stem again.
            if (audio.Count != 1)
            {
                choice.Candidates.Add(new GridCandidate());
                audio.Add(stems.Mix.Samples);
            }

            // Each analysis is on its own and takes seconds, so they run side by side, with a core
            // left over for whatever started this.
            int rate = stems.Mix.SampleRate;
            int done = 0;
            var progressLock = new object();
            var side = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(MaxAnalysesAtOnce, Environment.ProcessorCount - 1)) };
            Parallel.For(0, audio.Count, side, i =>
            {
                GridCandidate candidate = choice.Candidates[i];
                try
                {
                    candidate.Grid = BeatGridAnalyzer.Analyze(audio[i], rate, options, cancel);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    candidate.Error = e.Message;
                }
                // One at a time, so that the figure reported never goes backwards.
                lock (progressLock)
                {
                    done++;
                    if (progress != null)
                        progress.Report((float)done / audio.Count);
                }
            });
            cancel.ThrowIfCancellationRequested();

            var meter = new BeatDriftMeter(stems.Mix.Samples, rate);
            foreach (GridCandidate candidate in choice.Candidates)
            {
                if (candidate.Grid == null)
                    continue;
                candidate.Attack = meter.AttackOnLines(candidate.Grid);
                if (choice.Chosen == null || candidate.Attack > choice.Chosen.Attack)
                    choice.Chosen = candidate;
            }
            if (choice.Chosen == null)
                throw new ArgumentException(choice.Candidates[choice.Candidates.Count - 1].Error);

            if (choice.Chosen.Grid.TransientBeats == 0)
                choice.Warnings.Add("No clear beat found. Tempo and grid are estimates: check by ear.");
            return choice;
        }

        /// <summary>
        /// A short name for each stem, from the part of its file name that the others do not share:
        /// "Song (Drums)" and "Song (Bass)" give Drums and Bass. What is shared is only dropped up
        /// to a space or other mark, never from inside a word.
        /// </summary>
        public static string[] Labels(IList<string> names)
        {
            int prefix = 0, suffix = 0;
            if (names.Count > 1)
            {
                int shortest = int.MaxValue;
                foreach (string name in names)
                    shortest = Math.Min(shortest, name.Length);
                while (prefix < shortest && AllShare(names, prefix, false))
                    prefix++;
                while (prefix > 0 && char.IsLetterOrDigit(names[0][prefix - 1]))
                    prefix--;
                while (suffix < shortest - prefix && AllShare(names, suffix, true))
                    suffix++;
                while (suffix > 0 && char.IsLetterOrDigit(names[0][names[0].Length - suffix]))
                    suffix--;
            }

            var labels = new string[names.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                string own = names[i].Substring(prefix, names[i].Length - prefix - suffix);
                int start = 0, end = own.Length;
                while (start < end && !char.IsLetterOrDigit(own[start]))
                    start++;
                while (end > start && !char.IsLetterOrDigit(own[end - 1]))
                    end--;
                labels[i] = end > start ? Balanced(own.Substring(start, end - start)) : "Stem" + (i + 1).ToString(CultureInfo.InvariantCulture);
            }
            return labels;
        }

        /// <summary>
        /// A label with its brackets made to match. Taking away what the names share can take one
        /// of a pair: "Drums (L)" and "Drums (R)" share their last bracket, which left "Drums (L".
        /// </summary>
        static string Balanced(string label)
        {
            const string opening = "([{", closing = ")]}";
            for (int kind = 0; kind < opening.Length; kind++)
            {
                int open = 0, unopened = 0;
                foreach (char c in label)
                {
                    if (c == opening[kind])
                        open++;
                    else if (c == closing[kind] && open > 0)
                        open--;
                    else if (c == closing[kind])
                        unopened++;
                }
                label = new string(opening[kind], unopened) + label + new string(closing[kind], open);
            }
            return label;
        }

        /// <summary>
        /// A name for the song, from what the stems' file names all start with: "Song (Drums)" and
        /// "Song (Bass)" give Song. Empty when they share nothing.
        /// </summary>
        public static string SongName(IList<string> names)
        {
            if (names.Count == 0)
                return "";
            int prefix = names.Count == 1 ? names[0].Length : 0;
            if (names.Count > 1)
            {
                int shortest = int.MaxValue;
                foreach (string name in names)
                    shortest = Math.Min(shortest, name.Length);
                while (prefix < shortest && AllShare(names, prefix, false))
                    prefix++;
                // As for the labels: a name is not cut inside a word.
                while (prefix > 0 && prefix < names[0].Length && char.IsLetterOrDigit(names[0][prefix - 1]))
                    prefix--;
            }
            int end = prefix;
            while (end > 0 && !char.IsLetterOrDigit(names[0][end - 1]) && names[0][end - 1] != ')')
                end--;
            return names[0].Substring(0, end).Trim();
        }

        /// <summary>Whether every name has the same character this far from its start, or from its end.</summary>
        static bool AllShare(IList<string> names, int offset, bool fromEnd)
        {
            char shared = fromEnd ? names[0][names[0].Length - 1 - offset] : names[0][offset];
            foreach (string name in names)
                if ((fromEnd ? name[name.Length - 1 - offset] : name[offset]) != shared)
                    return false;
            return true;
        }

        static short[] ToLength(short[] samples, int frames)
        {
            if (samples.Length == frames * 2)
                return samples;
            var filled = new short[frames * 2];
            Array.Copy(samples, filled, samples.Length);
            return filled;
        }

        // The sums below run over every sample of every stem, so they are written over arrays and
        // by index, in blocks side by side: a foreach over an IList in the loop made an object for
        // every sample, and took 20 s for eight stems of 76 s. Each block writes only its own
        // samples, so the result is the same however the blocks are run.
        const int SumBlocks = 64;

        /// <summary>Adds audio together. A sum that does not fit in 16 bits is held at the limit and counted.</summary>
        public static short[] Add(IList<short[]> parts, out int clipped)
        {
            short[][] arrays = ToArray(parts);
            var sum = new short[arrays[0].Length];
            var clippedIn = new int[SumBlocks];
            Parallel.For(0, SumBlocks, block =>
            {
                int from, to, count = 0;
                BlockOf(block, sum.Length, out from, out to);
                for (int i = from; i < to; i++)
                {
                    int value = 0;
                    for (int part = 0; part < arrays.Length; part++)
                        value += arrays[part][i];
                    if (value > short.MaxValue || value < short.MinValue)
                    {
                        count++;
                        value = value > 0 ? short.MaxValue : short.MinValue;
                    }
                    sum[i] = (short)value;
                }
                clippedIn[block] = count;
            });
            clipped = 0;
            foreach (int count in clippedIn)
                clipped += count;
            return sum;
        }

        /// <summary>
        /// Whether audio added together goes past what 16 bits hold anywhere, which is where
        /// <see cref="Add"/> holds it at the limit and it distorts. Parts shorter than the longest
        /// count as silence past their end, as they are filled out when the stems are prepared.
        /// </summary>
        public static bool Clips(IList<short[]> parts)
        {
            short[][] arrays = ToArray(parts);
            int length = 0;
            foreach (short[] part in arrays)
                length = Math.Max(length, part.Length);
            var clips = new bool[SumBlocks];
            Parallel.For(0, SumBlocks, block =>
            {
                int from, to;
                BlockOf(block, length, out from, out to);
                for (int i = from; i < to; i++)
                {
                    int value = 0;
                    for (int part = 0; part < arrays.Length; part++)
                        if (i < arrays[part].Length)
                            value += arrays[part][i];
                    if (value > short.MaxValue || value < short.MinValue)
                    {
                        clips[block] = true;
                        return;
                    }
                }
            });
            return Array.IndexOf(clips, true) >= 0;
        }

        /// <summary>The loudest point of audio added together, as a magnitude that may be over 16 bits.</summary>
        static int PeakOfSum(IList<short[]> parts)
        {
            short[][] arrays = ToArray(parts);
            int length = arrays[0].Length;
            var peaks = new int[SumBlocks];
            Parallel.For(0, SumBlocks, block =>
            {
                int from, to, peak = 0;
                BlockOf(block, length, out from, out to);
                for (int i = from; i < to; i++)
                {
                    int value = 0;
                    for (int part = 0; part < arrays.Length; part++)
                        value += arrays[part][i];
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
            return most;
        }

        /// <summary>
        /// Adds audio together for finding the beat. When the sum would not fit in 16 bits the
        /// whole of it is turned down, so that no hit is flattened; this audio is never exported.
        /// </summary>
        static short[] AddToFit(IList<short[]> parts)
        {
            short[][] arrays = ToArray(parts);
            int peak = PeakOfSum(arrays);
            double gain = peak > short.MaxValue ? (double)short.MaxValue / peak : 1.0;
            var sum = new short[arrays[0].Length];
            Parallel.For(0, SumBlocks, block =>
            {
                int from, to;
                BlockOf(block, sum.Length, out from, out to);
                for (int i = from; i < to; i++)
                {
                    int value = 0;
                    for (int part = 0; part < arrays.Length; part++)
                        value += arrays[part][i];
                    sum[i] = gain == 1.0 ? (short)value : (short)Math.Round(value * gain);
                }
            });
            return sum;
        }

        static short[][] ToArray(IList<short[]> parts)
        {
            var arrays = parts as short[][];
            if (arrays != null)
                return arrays;
            arrays = new short[parts.Count][];
            parts.CopyTo(arrays, 0);
            return arrays;
        }

        /// <summary>The samples one block of a sum covers. Blocks start on a frame, so a frame is never split.</summary>
        static void BlockOf(int block, int length, out int from, out int to)
        {
            int frames = length / 2;
            from = (int)((long)frames * block / SumBlocks) * 2;
            to = block == SumBlocks - 1 ? length : (int)((long)frames * (block + 1) / SumBlocks) * 2;
        }
    }
}
