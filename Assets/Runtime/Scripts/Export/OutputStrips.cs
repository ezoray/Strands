using System;
using System.Collections.Generic;
using Strands.Audio;
using Strands.Sp16;

namespace Strands.Export
{
    /// <summary>One audio file as it was loaded.</summary>
    public sealed class StripFile
    {
        public string Path;
        /// <summary>The file's name without its extension, or the stem's name when it came out of a stem file.</summary>
        public string Name;
        /// <summary>The song this is a stem of, when it came out of a stem file: that file's name. Null for a file of its own.</summary>
        public string Song;
        public WavData Audio;
    }

    /// <summary>
    /// One pair of the SP-16's outputs and the files that play from it. The pair can be split into
    /// its two outputs, each then taking files of its own, which are written as mono samples.
    /// </summary>
    public sealed class OutputStrip
    {
        /// <summary>0 for outputs 1-2, up to 3 for 7-8.</summary>
        public readonly int Pair;
        /// <summary>
        /// The files on the pair, or on its first output when it is split, and the files on its
        /// second output, which has some only when it is split.
        /// </summary>
        public readonly List<StripFile>[] Slots = { new List<StripFile>(), new List<StripFile>() };
        /// <summary>
        /// Files that share an output are tracks of their own instead of one sample. It is how a
        /// strip starts: every file is a track unless its strip is told to sum them.
        /// </summary>
        public bool KeepApart = true;

        public bool Split { get; private set; }

        public OutputStrip(int pair)
        {
            Pair = pair;
        }

        public int FileCount { get { return Slots[0].Count + Slots[1].Count; } }

        public Sp16Output OutputOf(int slot)
        {
            return Split ? (Sp16Output)((int)Sp16Output.Out1 + 2 * Pair + slot) : (Sp16Output)Pair;
        }

        /// <summary>Splitting leaves every file on the first output; joining brings the second output's files back to the pair.</summary>
        public void SetSplit(bool split)
        {
            if (!split)
            {
                Slots[0].AddRange(Slots[1]);
                Slots[1].Clear();
            }
            Split = split;
        }
    }

    /// <summary>
    /// What the user has put on the four output pairs. One file by itself is a full song, played
    /// from the master output whichever strip it is on; two or more are stems.
    /// The strips can also be told to play everything from the master output
    /// (<see cref="AllOnMaster"/>), and are then four places for tracks and not four pairs.
    /// </summary>
    public sealed class StripLayout
    {
        // Where a file goes by its name when several are added at once, so that the usual four
        // stems land in the same order whatever order the dialog hands them over in. Drum comes
        // first so that a bass drum goes with the drums.
        static readonly string[] NameHints = { "drum", "bass", "other", "vocal", "vox" };
        static readonly int[] HintStrips = { 0, 1, 2, 3, 3 };
        static readonly string[] StripNames = { "Drums", "Bass", "Other", "Vocals" };

        /// <summary>
        /// What a strip is called wherever the strips are named and not the files on them: the
        /// usual four stems, in their order. The files' own names can be any length, and a strip
        /// can hold several.
        /// </summary>
        public static string NameOf(int strip)
        {
            return StripNames[(strip % StripNames.Length + StripNames.Length) % StripNames.Length];
        }

        public readonly OutputStrip[] Strips = new OutputStrip[Sp16Project.OutputPairs];

        /// <summary>Goes up whenever a file is added or taken away, which is when a grid found earlier stops being good.</summary>
        public int FilesVersion { get; private set; }

        /// <summary>
        /// Everything plays from the master output, for a unit that has only that connected. Each
        /// strip's files are still a track of their own, or tracks when they are kept apart, but
        /// none goes to a pair or to an output by itself, so no strip is split and every sample is
        /// stereo. It lies over what the strips are set to and changes none of it: a strip that
        /// was split is split again when this is turned off.
        /// </summary>
        public bool AllOnMaster;

        public StripLayout()
        {
            for (int i = 0; i < Strips.Length; i++)
                Strips[i] = new OutputStrip(i);
        }

        public int FileCount
        {
            get
            {
                int count = 0;
                foreach (OutputStrip strip in Strips)
                    count += strip.FileCount;
                return count;
            }
        }

        public bool IsFullSong { get { return FileCount == 1; } }

        /// <summary>The most letters of a file's own name that a strip is called by, so that four strips' names always fit side by side.</summary>
        public const int MaxStripNameLength = 10;

        /// <summary>
        /// What a strip is called from the files on it: the first one's label, cut short when it
        /// is long, and how many more there are ("Drums", "Loop one…", "Kick +2"). With no label it
        /// is the strip's usual name.
        /// </summary>
        public static string NameOf(int strip, string firstLabel, int files)
        {
            string name = string.IsNullOrWhiteSpace(firstLabel) ? NameOf(strip) : firstLabel.Trim();
            if (name.Length > MaxStripNameLength)
                name = name.Substring(0, MaxStripNameLength - 1).TrimEnd() + "…";
            return files > 1 ? name + " +" + (files - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
        }

        /// <summary>How many places on a strip hold files of their own: its two outputs when it is split, otherwise one.</summary>
        public int PlacesOn(OutputStrip strip)
        {
            return strip.Split && !AllOnMaster ? 2 : 1;
        }

        /// <summary>
        /// The files in one place of a strip. A strip that is one place has every one of its files
        /// there, whichever output of a split they were put on.
        /// </summary>
        public List<StripFile> FilesIn(OutputStrip strip, int place)
        {
            if (PlacesOn(strip) == 2)
                return strip.Slots[place];
            var files = new List<StripFile>(strip.Slots[0]);
            files.AddRange(strip.Slots[1]);
            return files;
        }

        public int CountIn(OutputStrip strip, int place)
        {
            return PlacesOn(strip) == 2 ? strip.Slots[place].Count : strip.FileCount;
        }

        /// <summary>Where the files in one place of a strip play from.</summary>
        public Sp16Output OutputOf(OutputStrip strip, int place)
        {
            return AllOnMaster ? Sp16Output.Master : strip.OutputOf(place);
        }

        /// <summary>
        /// Whether a strip's files are written as mono samples, which those on the two outputs of
        /// a split strip are. One file by itself is a full song and is not.
        /// </summary>
        public bool IsMono(OutputStrip strip)
        {
            return PlacesOn(strip) == 2 && !IsFullSong;
        }

        /// <summary>
        /// Whether summing would bring the tracks down: some place of a strip holds files kept apart.
        /// </summary>
        public bool CanSum
        {
            get
            {
                foreach (OutputStrip strip in Strips)
                    for (int place = 0; place < PlacesOn(strip); place++)
                        if (strip.KeepApart && FilesIn(strip, place).Count > 1)
                            return true;
                return false;
            }
        }

        /// <summary>
        /// Whether splitting a strip into mono would save memory: some strip with files is still
        /// a stereo pair. There is no mono on master.
        /// </summary>
        public bool CanUseMono
        {
            get
            {
                if (AllOnMaster || IsFullSong)
                    return false;
                foreach (OutputStrip strip in Strips)
                    if (strip.FileCount > 0 && PlacesOn(strip) == 1)
                        return true;
                return false;
            }
        }

        /// <summary>Every file, strip by strip.</summary>
        public List<StripFile> Files
        {
            get
            {
                var files = new List<StripFile>();
                foreach (OutputStrip strip in Strips)
                    foreach (List<StripFile> slot in strip.Slots)
                        files.AddRange(slot);
                return files;
            }
        }

        public void Add(int strip, int slot, StripFile file)
        {
            Strips[strip].Slots[Strips[strip].Split ? slot : 0].Add(file);
            FilesVersion++;
        }

        public void Remove(StripFile file)
        {
            foreach (OutputStrip strip in Strips)
                foreach (List<StripFile> slot in strip.Slots)
                    if (slot.Remove(file))
                        FilesVersion++;
        }

        /// <summary>
        /// Takes every file away and puts the strips back as they start: joined, with files that
        /// share one kept as tracks of their own. Whether everything is on master is left as it is: that is about what
        /// is connected to the unit, not about the song.
        /// </summary>
        public void Clear()
        {
            foreach (OutputStrip strip in Strips)
            {
                foreach (List<StripFile> slot in strip.Slots)
                    slot.Clear();
                strip.SetSplit(false);
                strip.KeepApart = true;
            }
            FilesVersion++;
        }

        /// <summary>Moves a file to the other output of its split strip.</summary>
        public void MoveAcross(StripFile file)
        {
            foreach (OutputStrip strip in Strips)
            {
                if (!strip.Split)
                    continue;
                for (int slot = 0; slot < 2; slot++)
                {
                    if (!strip.Slots[slot].Remove(file))
                        continue;
                    strip.Slots[1 - slot].Add(file);
                    return;
                }
            }
        }

        /// <summary>The strip a file is on, 0 to 3, or -1 when it is on none.</summary>
        public int StripOf(StripFile file)
        {
            foreach (OutputStrip strip in Strips)
                if (strip.Slots[0].Contains(file) || strip.Slots[1].Contains(file))
                    return strip.Pair;
            return -1;
        }

        /// <summary>
        /// Moves a file to another strip, where it joins whatever is there: on a split strip, the
        /// output given, or the one with fewer files when none is. The files are the same files,
        /// so a grid found from them stays good, as it does when a strip is split.
        /// </summary>
        /// <returns>False when nothing moved: the file is on no strip, or there already.</returns>
        public bool MoveTo(StripFile file, int strip, int slot = -1)
        {
            int from = StripOf(file);
            if (from < 0 || strip < 0 || strip >= Strips.Length)
                return false;
            OutputStrip target = Strips[strip];
            int place = !target.Split ? 0 : slot >= 0 ? Math.Min(slot, 1) : target.Slots[1].Count < target.Slots[0].Count ? 1 : 0;
            if (target.Slots[place].Contains(file))
                return false;
            foreach (List<StripFile> files in Strips[from].Slots)
                files.Remove(file);
            target.Slots[place].Add(file);
            return true;
        }

        /// <summary>
        /// Shares files out over the strips. A file whose name says drums, bass, other or vocals
        /// goes to the first, second, third or fourth strip; the rest go one at a time to whichever
        /// strip holds the fewest, so four files fill the four pairs and a fifth starts round again.
        /// </summary>
        public void Spread(IList<StripFile> files)
        {
            var names = new string[files.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = files[i].Name;
            // Judged on the part of the name the files do not share, so a song called Bassline does not send every stem to the bass.
            string[] labels = StemsToSp16.Labels(names);
            var unplaced = new List<StripFile>();
            for (int i = 0; i < files.Count; i++)
            {
                int strip = HintedStrip(labels[i]);
                if (strip < 0)
                    unplaced.Add(files[i]);
                else
                    AddToEmptierSlot(strip, files[i]);
            }
            foreach (StripFile file in unplaced)
            {
                int emptiest = 0;
                for (int i = 1; i < Strips.Length; i++)
                    if (Strips[i].FileCount < Strips[emptiest].FileCount)
                        emptiest = i;
                AddToEmptierSlot(emptiest, file);
            }
        }

        /// <summary>
        /// Puts the stems of a stem file on the strips in the order the file has them, the first
        /// on 1-2, whatever they are called. A fifth would come round to 1-2 again.
        /// </summary>
        public void AddInOrder(IList<StripFile> files)
        {
            for (int i = 0; i < files.Count; i++)
                AddToEmptierSlot(i % Strips.Length, files[i]);
        }

        static int HintedStrip(string label)
        {
            string lower = label.ToLowerInvariant();
            for (int i = 0; i < NameHints.Length; i++)
                if (lower.Contains(NameHints[i]))
                    return HintStrips[i];
            return -1;
        }

        void AddToEmptierSlot(int strip, StripFile file)
        {
            OutputStrip target = Strips[strip];
            Add(strip, target.Split && target.Slots[1].Count < target.Slots[0].Count ? 1 : 0, file);
        }

        /// <summary>
        /// The files as stems, strip by strip, each with the output of the place it is in and its
        /// strip as its group, so that only files on one strip are ever mixed.
        /// </summary>
        public List<Stem> Stems()
        {
            var stems = new List<Stem>();
            foreach (OutputStrip strip in Strips)
                for (int place = 0; place < PlacesOn(strip); place++)
                    foreach (StripFile file in FilesIn(strip, place))
                        stems.Add(new Stem { Name = file.Name, Audio = file.Audio, Output = OutputOf(strip, place), Group = strip.Pair, KeepApart = strip.KeepApart });
            return stems;
        }

        /// <summary>How many pads each pattern takes: one for a full song, otherwise one for every sample the stems make.</summary>
        public int Tracks
        {
            get
            {
                if (FileCount <= 1)
                    return FileCount;
                int tracks = 0;
                foreach (OutputStrip strip in Strips)
                    for (int place = 0; place < PlacesOn(strip); place++)
                        tracks += strip.KeepApart ? CountIn(strip, place) : Math.Min(1, CountIn(strip, place));
                return tracks;
            }
        }

        /// <summary>The length of the longest file, which is the length of the song.</summary>
        public int Frames
        {
            get
            {
                int frames = 0;
                foreach (StripFile file in Files)
                    frames = Math.Max(frames, file.Audio.Frames);
                return frames;
            }
        }

        /// <summary>
        /// The sample memory the song will take, near enough: the export adds a crossfade to each
        /// sample and may leave out silence at the start, which only a plan can count.
        /// </summary>
        public long EstimatedBytes
        {
            get
            {
                long bytes = 0;
                foreach (OutputStrip strip in Strips)
                    bytes += EstimatedBytesOn(strip);
                return bytes;
            }
        }

        /// <summary>
        /// The share of <see cref="EstimatedBytes"/> that one strip's files take. Every sample is as
        /// long as the song, a file on one output of a split strip costs half of one on a pair or
        /// on master, and files mixed together cost what one does.
        /// </summary>
        public long EstimatedBytesOn(OutputStrip strip)
        {
            long frames = Frames;
            if (FileCount <= 1)
                return strip.FileCount * frames * Sp16Project.StereoBytesPerFrame;
            long bytesPerFrame = 0;
            for (int place = 0; place < PlacesOn(strip); place++)
            {
                int samples = strip.KeepApart ? CountIn(strip, place) : Math.Min(1, CountIn(strip, place));
                bytesPerFrame += samples * (IsMono(strip) ? Sp16Project.MonoBytesPerFrame : Sp16Project.StereoBytesPerFrame);
            }
            return frames * bytesPerFrame;
        }

        /// <summary>How many 4-bar patterns a scene holds with this many tracks to a pattern, or 0 when a scene cannot hold one.</summary>
        public int PatternsPerScene
        {
            get
            {
                int tracks = Tracks;
                return tracks == 0 || tracks > Sp16Project.PadsPerScene ? 0 : Sp16Project.PadsPerScene / tracks;
            }
        }
    }
}
