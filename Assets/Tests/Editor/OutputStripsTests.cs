using System.Collections.Generic;
using NUnit.Framework;
using Strands.Audio;
using Strands.Export;
using Strands.Sp16;

namespace Strands.Tests
{
    /// <summary>The four output strips of the Source screen: where files go, what they cost and the stems they make.</summary>
    public class OutputStripsTests
    {
        const int Rate = 44100;

        static StripFile File(string name, double seconds = 10, short level = 1000)
        {
            var samples = new short[(int)(seconds * Rate) * 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = level;
            return new StripFile
            {
                Path = "/music/" + name + ".wav",
                Name = name,
                Audio = new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 }
            };
        }

        // Strips set to sum the files that share one, which a strip does only when it is told to.
        static StripLayout Summing()
        {
            var layout = new StripLayout();
            foreach (OutputStrip strip in layout.Strips)
                strip.KeepApart = false;
            return layout;
        }

        [Test]
        public void Strips_WhatWouldHelpNamesOnlyWhatApplies()
        {
            // Two files kept apart on a stereo strip: both summing and mono would help.
            var layout = new StripLayout();
            layout.Add(0, 0, File("Drums"));
            layout.Add(0, 0, File("Kick"));
            layout.Add(1, 0, File("Bass"));
            Assert.That(WavToSp16.WhatWouldHelp(layout.CanSum, layout.CanUseMono), Is.EqualTo("Sum stems, use mono, or remove tracks."));
            // Summed, and every strip split into mono: only removing is left.
            layout.Strips[0].KeepApart = false;
            layout.Strips[0].SetSplit(true);
            layout.Strips[1].SetSplit(true);
            Assert.That(WavToSp16.WhatWouldHelp(layout.CanSum, layout.CanUseMono), Is.EqualTo("Remove tracks."));
            // On master there is no mono, and a split strip is one place again, so its files can be summed.
            layout.Strips[0].KeepApart = true;
            layout.AllOnMaster = true;
            Assert.That(WavToSp16.WhatWouldHelp(layout.CanSum, layout.CanUseMono), Is.EqualTo("Sum stems or remove tracks."));
            Assert.That(WavToSp16.WhatWouldHelp(false, true), Is.EqualTo("Use mono or remove tracks."));
        }

        static List<string> Names(List<StripFile> files)
        {
            return files.ConvertAll(file => file.Name);
        }

        [Test]
        public void Spread_PutsTheUsualFourStemsOnTheirStripsWhateverOrderTheyComeIn()
        {
            var layout = new StripLayout();
            layout.Spread(new[] { File("Song - bass"), File("Song - drums"), File("Song - other"), File("Song - vocals") });
            Assert.That(Names(layout.Strips[0].Slots[0]), Is.EqualTo(new[] { "Song - drums" }));
            Assert.That(Names(layout.Strips[1].Slots[0]), Is.EqualTo(new[] { "Song - bass" }));
            Assert.That(Names(layout.Strips[2].Slots[0]), Is.EqualTo(new[] { "Song - other" }));
            Assert.That(Names(layout.Strips[3].Slots[0]), Is.EqualTo(new[] { "Song - vocals" }));

            // The name of the song does not count: only what the files do not share.
            var bassline = new StripLayout();
            bassline.Spread(new[] { File("Bassline A"), File("Bassline B") });
            Assert.That(Names(bassline.Strips[0].Slots[0]), Is.EqualTo(new[] { "Bassline A" }));
            Assert.That(Names(bassline.Strips[1].Slots[0]), Is.EqualTo(new[] { "Bassline B" }));
        }

        [Test]
        public void Spread_SharesOtherFilesOutAndComesRoundAgain()
        {
            var layout = new StripLayout();
            layout.Spread(new[] { File("a"), File("b"), File("c"), File("d"), File("e") });
            Assert.That(Names(layout.Strips[0].Slots[0]), Is.EqualTo(new[] { "a", "e" }));
            Assert.That(Names(layout.Strips[3].Slots[0]), Is.EqualTo(new[] { "d" }));

            // Named files are placed first, and the rest fill the strips left empty.
            var some = new StripLayout();
            some.Spread(new[] { File("guitar"), File("vocals"), File("kick drum"), File("bass drum") });
            Assert.That(Names(some.Strips[0].Slots[0]), Is.EqualTo(new[] { "kick drum", "bass drum" }));
            Assert.That(Names(some.Strips[1].Slots[0]), Is.EqualTo(new[] { "guitar" }));
            Assert.That(Names(some.Strips[3].Slots[0]), Is.EqualTo(new[] { "vocals" }));
        }

        [Test]
        public void NameOf_IsTheUsualStemOfEachStrip()
        {
            var names = new List<string>();
            for (int strip = 0; strip < 4; strip++)
                names.Add(StripLayout.NameOf(strip));
            Assert.That(names, Is.EqualTo(new[] { "Drums", "Bass", "Other", "Vocals" }));
        }

        [Test]
        public void MoveTo_PutsAFileOnAnotherStripAndLeavesTheGridGood()
        {
            var layout = new StripLayout();
            StripFile drums = File("Drums"), bass = File("Bass"), other = File("Other");
            layout.Add(0, 0, drums);
            layout.Add(1, 0, bass);
            layout.Add(2, 0, other);
            int version = layout.FilesVersion;

            // It joins whatever is on the strip it goes to.
            Assert.That(layout.MoveTo(drums, 1), Is.True);
            Assert.That(layout.Strips[0].FileCount, Is.EqualTo(0));
            Assert.That(Names(layout.Strips[1].Slots[0]), Is.EqualTo(new[] { "Bass", "Drums" }));
            Assert.That(layout.StripOf(drums), Is.EqualTo(1));
            // The files are the same files, so a grid found from them is still theirs.
            Assert.That(layout.FilesVersion, Is.EqualTo(version));
            Assert.That(layout.FileCount, Is.EqualTo(3));

            // Where it is already, no strip at all, and a file that is not loaded: nothing moves.
            Assert.That(layout.MoveTo(drums, 1), Is.False);
            Assert.That(layout.MoveTo(drums, 4), Is.False);
            Assert.That(layout.MoveTo(drums, -1), Is.False);
            Assert.That(layout.MoveTo(File("Stranger"), 0), Is.False);
            Assert.That(layout.StripOf(File("Stranger")), Is.EqualTo(-1));

            // On a split strip it goes to the output with fewer files, or to the one asked for.
            layout.Strips[2].SetSplit(true);
            Assert.That(layout.MoveTo(drums, 2), Is.True);
            Assert.That(Names(layout.Strips[2].Slots[0]), Is.EqualTo(new[] { "Other" }));
            Assert.That(Names(layout.Strips[2].Slots[1]), Is.EqualTo(new[] { "Drums" }));
            Assert.That(layout.MoveTo(bass, 2, 1), Is.True);
            Assert.That(Names(layout.Strips[2].Slots[1]), Is.EqualTo(new[] { "Drums", "Bass" }));
            // And from one output of a strip to the other.
            Assert.That(layout.MoveTo(bass, 2, 0), Is.True);
            Assert.That(Names(layout.Strips[2].Slots[0]), Is.EqualTo(new[] { "Other", "Bass" }));
            Assert.That(Names(layout.Strips[2].Slots[1]), Is.EqualTo(new[] { "Drums" }));
            Assert.That(layout.FilesVersion, Is.EqualTo(version));
        }

        [Test]
        public void Stems_TakeTheOutputOfThePlaceTheyAreIn()
        {
            var layout = Summing();
            layout.Add(0, 0, File("Drums"));
            layout.Add(1, 0, File("Bass"));
            layout.Add(2, 0, File("Other"));
            layout.Add(2, 0, File("Guitar"));
            layout.Add(3, 0, File("Vocals"));
            layout.Strips[1].SetSplit(true);
            layout.Add(1, 1, File("Sub"));

            List<Stem> stems = layout.Stems();
            Assert.That(stems.ConvertAll(stem => stem.Name), Is.EqualTo(new[] { "Drums", "Bass", "Sub", "Other", "Guitar", "Vocals" }));
            Assert.That(stems.ConvertAll(stem => stem.Output), Is.EqualTo(new[]
                { Sp16Output.Out12, Sp16Output.Out3, Sp16Output.Out4, Sp16Output.Out56, Sp16Output.Out56, Sp16Output.Out78 }));
            Assert.That(layout.Tracks, Is.EqualTo(5));
            Assert.That(layout.PatternsPerScene, Is.EqualTo(3));
            // Three stereo samples and two mono ones, each 10 s long.
            Assert.That(layout.EstimatedBytes, Is.EqualTo(10L * Rate * (3 * 4 + 2 * 2)));

            StemSet set = StemsToSp16.Prepare(stems);
            Assert.That(set.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Drums", "Bass", "Sub", "Mix56", "Vocals" }));
            Assert.That(set.Layers.Count, Is.EqualTo(layout.Tracks));
            WavExportResult plan = set.Plan("x", new WavExportOptions(), new BeatGrid { Bpm = 120, FirstBeatSeconds = 0, DownbeatIndex = 0 });
            // The plan adds a crossfade to each sample but the last, and nothing else here.
            Assert.That(plan.SampleBytes, Is.EqualTo(layout.EstimatedBytes).Within(0.001 * layout.EstimatedBytes));
        }

        [Test]
        public void EstimatedBytesOn_SharesTheSongOutByStrip()
        {
            var layout = Summing();
            layout.Add(0, 0, File("Drums"));
            layout.Add(1, 0, File("Bass"));
            layout.Add(2, 0, File("Other"));
            layout.Add(2, 0, File("Guitar"));
            layout.Strips[1].SetSplit(true);
            layout.Add(1, 1, File("Sub"));

            long stereo = 10L * Rate * 4;
            Assert.That(layout.EstimatedBytesOn(layout.Strips[0]), Is.EqualTo(stereo));
            // Two files on the two outputs of a split strip are two mono samples: what one stereo sample costs.
            Assert.That(layout.EstimatedBytesOn(layout.Strips[1]), Is.EqualTo(stereo));
            // Two files mixed cost what one does; kept apart they cost two.
            Assert.That(layout.EstimatedBytesOn(layout.Strips[2]), Is.EqualTo(stereo));
            layout.Strips[2].KeepApart = true;
            Assert.That(layout.EstimatedBytesOn(layout.Strips[2]), Is.EqualTo(2 * stereo));
            Assert.That(layout.EstimatedBytesOn(layout.Strips[3]), Is.EqualTo(0));

            long total = 0;
            foreach (OutputStrip strip in layout.Strips)
                total += layout.EstimatedBytesOn(strip);
            Assert.That(total, Is.EqualTo(layout.EstimatedBytes));

            // A shorter file is filled out to the length of the song, so it costs as much as the longest.
            layout.Add(3, 0, File("Vocals", 4));
            Assert.That(layout.EstimatedBytesOn(layout.Strips[3]), Is.EqualTo(stereo));

            // One file by itself is a stereo sample on the master output, on a split strip or not.
            var song = new StripLayout();
            song.Strips[1].SetSplit(true);
            song.Add(1, 1, File("Song", 20));
            Assert.That(song.EstimatedBytesOn(song.Strips[1]), Is.EqualTo(20L * Rate * 4));
        }

        [Test]
        public void Megabytes_AreCountedAsTheUnitCountsThem()
        {
            // The sample memory reads as the 256 MB the SP-16 is known by, not as 268.
            Assert.That(Sp16Project.Megabytes(Sp16Project.SampleMemoryBytes), Is.EqualTo(256.0));
            // A minute of stereo at 44.1 kHz: 10,584,000 bytes.
            Assert.That(Sp16Project.Megabytes(60L * Rate * 4), Is.EqualTo(10.09).Within(0.005));
        }

        [Test]
        public void Stems_OnAStripKeptApartAreTracksOfTheirOwn()
        {
            var layout = Summing();
            layout.Add(0, 0, File("Drums"));
            layout.Add(2, 0, File("Other"));
            layout.Add(2, 0, File("Guitar"));
            layout.Add(0, 0, File("Perc"));
            layout.Strips[2].KeepApart = true;

            Assert.That(layout.Tracks, Is.EqualTo(3));
            Assert.That(layout.EstimatedBytes, Is.EqualTo(10L * Rate * 3 * 4));
            StemSet set = StemsToSp16.Prepare(layout.Stems());
            // The first pair is still one sample; only the strip kept apart is not.
            Assert.That(set.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Mix12", "Other", "Guitar" }));
            Assert.That(set.Layers.ConvertAll(layer => layer.Output), Is.EqualTo(new[] { Sp16Output.Out12, Sp16Output.Out56, Sp16Output.Out56 }));
            Assert.That(set.LayerSamples[1], Is.SameAs(layout.Strips[2].Slots[0][0].Audio.Samples));
        }

        [Test]
        public void AllOnMaster_EveryStripIsATrackOfItsOwnOnTheMasterOutput()
        {
            var layout = Summing();
            layout.Add(0, 0, File("Drums"));
            layout.Add(1, 0, File("Bass"));
            layout.Add(2, 0, File("Other"));
            layout.Add(2, 0, File("Guitar"));
            layout.Add(3, 0, File("Vocals"));
            layout.Strips[1].SetSplit(true);
            layout.Add(1, 1, File("Sub"));
            int version = layout.FilesVersion;
            long stereo = 10L * Rate * 4;
            // Three stereo samples and two mono ones.
            Assert.That(layout.EstimatedBytes, Is.EqualTo(4 * stereo));
            Assert.That(layout.Tracks, Is.EqualTo(5));

            layout.AllOnMaster = true;
            // Where the files play from is not what the song is, so a grid found before still stands.
            Assert.That(layout.FilesVersion, Is.EqualTo(version));
            List<Stem> stems = layout.Stems();
            Assert.That(stems.ConvertAll(stem => stem.Name), Is.EqualTo(new[] { "Drums", "Bass", "Sub", "Other", "Guitar", "Vocals" }));
            Assert.That(stems.ConvertAll(stem => stem.Output), Is.All.EqualTo(Sp16Output.Master));
            Assert.That(stems.ConvertAll(stem => stem.Group), Is.EqualTo(new[] { 0, 1, 1, 2, 2, 3 }));

            // No strip is split on master: the two outputs of the second strip are one place, and
            // what was on them is mixed as any two files on a strip are.
            OutputStrip second = layout.Strips[1];
            Assert.That(layout.PlacesOn(second), Is.EqualTo(1));
            Assert.That(Names(layout.FilesIn(second, 0)), Is.EqualTo(new[] { "Bass", "Sub" }));
            Assert.That(layout.CountIn(second, 0), Is.EqualTo(2));
            Assert.That(layout.OutputOf(second, 0), Is.EqualTo(Sp16Output.Master));
            Assert.That(layout.IsMono(second), Is.False);
            Assert.That(layout.Tracks, Is.EqualTo(4));
            Assert.That(layout.PatternsPerScene, Is.EqualTo(4));
            // Four stereo samples, one to a strip.
            Assert.That(layout.EstimatedBytes, Is.EqualTo(4 * stereo));
            foreach (OutputStrip strip in layout.Strips)
                Assert.That(layout.EstimatedBytesOn(strip), Is.EqualTo(stereo));

            StemSet set = StemsToSp16.Prepare(stems);
            Assert.That(set.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Drums", "Mix2", "Mix3", "Vocals" }));
            Assert.That(set.Layers.ConvertAll(layer => layer.Output), Is.All.EqualTo(Sp16Output.Master));
            Assert.That(set.Layers.ConvertAll(layer => layer.Group), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(set.Layers.ConvertAll(layer => layer.IsMono), Is.All.False);
            WavExportResult plan = set.Plan("x", new WavExportOptions(), new BeatGrid { Bpm = 120, FirstBeatSeconds = 0, DownbeatIndex = 0 });
            Assert.That(plan.SampleBytes, Is.EqualTo(layout.EstimatedBytes).Within(0.001 * layout.EstimatedBytes));

            // A strip kept apart is a track for each of its files, as it is on a pair.
            layout.Strips[2].KeepApart = true;
            Assert.That(layout.Tracks, Is.EqualTo(5));
            Assert.That(layout.EstimatedBytesOn(layout.Strips[2]), Is.EqualTo(2 * stereo));
            Assert.That(StemsToSp16.Prepare(layout.Stems()).Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Drums", "Mix2", "Other", "Guitar", "Vocals" }));

            // Taking everything away leaves it on: it is about what is connected to the unit, not about the song.
            var cleared = new StripLayout { AllOnMaster = true };
            cleared.Add(0, 0, File("Drums"));
            cleared.Clear();
            Assert.That(cleared.AllOnMaster, Is.True);

            // Turned off again, the strips are as they were set: the second is still split, with a file on each of its outputs.
            layout.Strips[2].KeepApart = false;
            layout.AllOnMaster = false;
            Assert.That(second.Split, Is.True);
            Assert.That(layout.PlacesOn(second), Is.EqualTo(2));
            Assert.That(Names(layout.FilesIn(second, 1)), Is.EqualTo(new[] { "Sub" }));
            Assert.That(layout.IsMono(second), Is.True);
            Assert.That(layout.Stems().ConvertAll(stem => stem.Output), Is.EqualTo(new[]
                { Sp16Output.Out12, Sp16Output.Out3, Sp16Output.Out4, Sp16Output.Out56, Sp16Output.Out56, Sp16Output.Out78 }));
            Assert.That(layout.EstimatedBytes, Is.EqualTo(4 * stereo));
            Assert.That(layout.Tracks, Is.EqualTo(5));
        }

        [Test]
        public void AllOnMaster_OneFileIsStillAFullSong()
        {
            var layout = new StripLayout { AllOnMaster = true };
            layout.Strips[2].SetSplit(true);
            layout.Add(2, 1, File("Song", 20));
            Assert.That(layout.IsFullSong, Is.True);
            Assert.That(layout.Tracks, Is.EqualTo(1));
            Assert.That(layout.PatternsPerScene, Is.EqualTo(16));
            Assert.That(layout.IsMono(layout.Strips[2]), Is.False);
            Assert.That(layout.EstimatedBytes, Is.EqualTo(20L * Rate * 4));
            Assert.That(Names(layout.FilesIn(layout.Strips[2], 0)), Is.EqualTo(new[] { "Song" }));
        }

        [Test]
        public void OneFileIsAFullSongWhereverItIs()
        {
            var layout = new StripLayout();
            Assert.That(layout.Tracks, Is.EqualTo(0));
            Assert.That(layout.PatternsPerScene, Is.EqualTo(0));
            Assert.That(layout.EstimatedBytes, Is.EqualTo(0));

            layout.Add(2, 0, File("Song", 20));
            layout.Strips[2].SetSplit(true);
            Assert.That(layout.IsFullSong, Is.True);
            Assert.That(layout.Tracks, Is.EqualTo(1));
            Assert.That(layout.PatternsPerScene, Is.EqualTo(16));
            // On the master output it is a stereo sample, split strip or not.
            Assert.That(layout.EstimatedBytes, Is.EqualTo(20L * Rate * 4));
        }

        [Test]
        public void SplittingAndJoiningKeepEveryFile()
        {
            var layout = new StripLayout();
            StripFile bass = File("Bass"), sub = File("Sub");
            layout.Add(1, 0, bass);
            // A strip that is not split has one place only.
            layout.Add(1, 1, sub);
            Assert.That(Names(layout.Strips[1].Slots[0]), Is.EqualTo(new[] { "Bass", "Sub" }));
            Assert.That(layout.Strips[1].OutputOf(0), Is.EqualTo(Sp16Output.Out34));

            layout.Strips[1].SetSplit(true);
            Assert.That(layout.Strips[1].OutputOf(0), Is.EqualTo(Sp16Output.Out3));
            Assert.That(layout.Strips[1].OutputOf(1), Is.EqualTo(Sp16Output.Out4));
            int version = layout.FilesVersion;
            layout.MoveAcross(sub);
            Assert.That(Names(layout.Strips[1].Slots[0]), Is.EqualTo(new[] { "Bass" }));
            Assert.That(Names(layout.Strips[1].Slots[1]), Is.EqualTo(new[] { "Sub" }));
            // Moving a file changes where it plays, not what the song is.
            Assert.That(layout.FilesVersion, Is.EqualTo(version));

            layout.Strips[1].SetSplit(false);
            Assert.That(Names(layout.Strips[1].Slots[0]), Is.EqualTo(new[] { "Bass", "Sub" }));
            Assert.That(layout.Strips[1].Slots[1], Is.Empty);

            layout.Remove(bass);
            Assert.That(layout.FileCount, Is.EqualTo(1));
            Assert.That(layout.FilesVersion, Is.GreaterThan(version));
        }

        [Test]
        public void Clear_PutsTheStripsBackAsTheyStart()
        {
            var layout = new StripLayout();
            layout.Add(1, 0, File("Bass"));
            layout.Add(2, 0, File("Other"));
            layout.Strips[1].SetSplit(true);
            layout.Strips[2].KeepApart = false;
            // Taking the files away one at a time leaves a strip as the user set it.
            layout.Remove(layout.Strips[1].Slots[0][0]);
            Assert.That(layout.Strips[1].Split, Is.True);

            int version = layout.FilesVersion;
            layout.Clear();
            Assert.That(layout.FileCount, Is.EqualTo(0));
            Assert.That(layout.Strips[1].Split, Is.False);
            Assert.That(layout.Strips[1].OutputOf(0), Is.EqualTo(Sp16Output.Out34));
            Assert.That(layout.Strips[2].KeepApart, Is.True);
            Assert.That(layout.FilesVersion, Is.GreaterThan(version));
        }

        [Test]
        public void AddInOrder_PutsAStemFilesStemsOnTheStripsAsTheFileHasThem()
        {
            // The order is the file's own, whatever the stems are called: here the vocals come first.
            var layout = new StripLayout();
            layout.AddInOrder(new[] { File("Vocals"), File("Other"), File("Bass"), File("Drums"), File("Extra") });
            Assert.That(Names(layout.Strips[0].Slots[0]), Is.EqualTo(new[] { "Vocals", "Extra" }));
            Assert.That(Names(layout.Strips[1].Slots[0]), Is.EqualTo(new[] { "Other" }));
            Assert.That(Names(layout.Strips[2].Slots[0]), Is.EqualTo(new[] { "Bass" }));
            Assert.That(Names(layout.Strips[3].Slots[0]), Is.EqualTo(new[] { "Drums" }));
            Assert.That(layout.Stems().ConvertAll(stem => stem.Output), Is.EqualTo(new[]
                { Sp16Output.Out12, Sp16Output.Out12, Sp16Output.Out34, Sp16Output.Out56, Sp16Output.Out78 }));
        }

        [Test]
        public void MoreTracksThanASceneHasPadsLeaveNoRoom()
        {
            var layout = new StripLayout();
            layout.Strips[0].KeepApart = true;
            for (int i = 0; i < 17; i++)
                layout.Add(0, 0, File("s" + i, 0.1));
            Assert.That(layout.Tracks, Is.EqualTo(17));
            Assert.That(layout.PatternsPerScene, Is.EqualTo(0));
            Assert.Throws<System.ArgumentException>(() => StemsToSp16.Prepare(layout.Stems()));
        }
        [Test]
        public void SeveralFilesAreStemsAndEachIsATrackUnlessItsStripSumsThem()
        {
            var layout = new StripLayout();
            layout.Add(1, 0, File("Loop one", 20));
            layout.Add(1, 0, File("Loop two", 20));
            // Two files are never a song, even on one strip; and a strip starts with its files kept apart.
            Assert.That(layout.IsFullSong, Is.False);
            Assert.That(layout.Strips[1].KeepApart, Is.True);
            Assert.That(layout.Tracks, Is.EqualTo(2));
            Assert.That(StemsToSp16.Prepare(layout.Stems()).Layers.Count, Is.EqualTo(2));

            layout.Strips[1].KeepApart = false;
            Assert.That(layout.IsFullSong, Is.False);
            Assert.That(layout.Tracks, Is.EqualTo(1));
            Assert.That(StemsToSp16.Prepare(layout.Stems()).Layers.Count, Is.EqualTo(1));

            layout.Clear();
            Assert.That(layout.Strips[1].KeepApart, Is.True);
        }

        [Test]
        public void NameOf_AStripIsCalledByItsFirstFileCutShort()
        {
            Assert.That(StripLayout.NameOf(0, "Drums", 1), Is.EqualTo("Drums"));
            Assert.That(StripLayout.NameOf(0, "Kick", 3), Is.EqualTo("Kick +2"));
            Assert.That(StripLayout.NameOf(2, "A very long loop name", 1), Is.EqualTo("A very lo…"));
            Assert.That(StripLayout.NameOf(2, "A very long loop name", 2), Is.EqualTo("A very lo… +1"));
            Assert.That(StripLayout.NameOf(2, "Exactly 10", 1), Is.EqualTo("Exactly 10"));
            Assert.That(StripLayout.NameOf(1, "", 1), Is.EqualTo("Bass"));
            Assert.That(StripLayout.NameOf(3, null, 2), Is.EqualTo("Vocals +1"));
        }
    }
}
