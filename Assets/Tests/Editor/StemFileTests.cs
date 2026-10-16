using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Strands.Audio;
using Strands.Export;
using Strands.Sp16;

namespace Strands.Tests
{
    /// <summary>
    /// Stem files: MP4s that hold the full mix and then each stem as an audio track, with the
    /// stems' names in a box of their own. The list is read by our own code and is tested on
    /// boxes built here. Stems coded as AAC are decoded by our own code too, on every system, and
    /// on a Mac that is held against what macOS decodes. Stems coded as Apple Lossless are left
    /// to macOS, so the tests of those only run on a Mac.
    /// </summary>
    public class StemFileTests
    {
        /// <summary>The stems of the files in Fixtures/audio, named for what each is of the reference WAV.</summary>
        static readonly string[] FixtureStems = { "As it is", "Sides exchanged", "Backwards", "Backwards and exchanged" };

        // ---- MP4 boxes built by hand ----

        static byte[] Box(string type, params byte[][] contents)
        {
            var box = new List<byte>();
            int length = 8 + contents.Sum(part => part.Length);
            box.AddRange(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length });
            box.AddRange(Encoding.ASCII.GetBytes(type));
            foreach (byte[] part in contents)
                box.AddRange(part);
            return box.ToArray();
        }

        /// <summary>A track that says what it holds: soun for audio, vide for pictures.</summary>
        static byte[] Track(string holds)
        {
            byte[] handler = Box("hdlr", new byte[8], Encoding.ASCII.GetBytes(holds), new byte[12], Encoding.ASCII.GetBytes("Handler\0"));
            return Box("trak", Box("tkhd", new byte[84]), Box("mdia", Box("mdhd", new byte[24]), handler, Box("minf", new byte[16])));
        }

        static byte[] Tracks(params string[] hold)
        {
            return hold.SelectMany(Track).ToArray();
        }

        static byte[] AudioTracks(int count)
        {
            return Tracks(Enumerable.Repeat("soun", count).ToArray());
        }

        /// <summary>The list of stems as Traktor writes it, with settings in front that are not used.</summary>
        static string List(params string[] names)
        {
            var stems = names.Select(name => "{\"color\":\"#FD6C38\",\"name\":\"" + name + "\"}");
            return "{\"mastering_dsp\":{\"compressor\":{\"attack\":0.003000000026077032,\"enabled\":false,\"ratio\":3},\"limiter\":{\"ceiling\":0,\"enabled\":false}},"
                + "\"offset\":0,\"stems\":[" + string.Join(",", stems) + "],\"version\":2}";
        }

        static byte[] Index(byte[] tracks, string list)
        {
            byte[] user = list == null ? new byte[0] : Box("udta", Box("stem", Encoding.UTF8.GetBytes(list)));
            return Box("moov", Box("mvhd", new byte[100]), tracks, user);
        }

        static readonly byte[] Start = Box("ftyp", Encoding.ASCII.GetBytes("mp42"), new byte[4], Encoding.ASCII.GetBytes("isommp41mp42"));
        static readonly byte[] Sound = Box("mdat", new byte[5000]);

        static byte[] Join(params byte[][] parts)
        {
            return parts.SelectMany(part => part).ToArray();
        }

        static StemFileInfo Info(params byte[][] boxes)
        {
            return StemFile.ReadInfo(new MemoryStream(Join(boxes)));
        }

        // ---- the list of stems ----

        [Test]
        public void ReadInfo_CountsTheAudioTracksAndNamesTheStemsInOrder()
        {
            StemFileInfo info = Info(Start, Sound, Index(AudioTracks(5), List("Drums", "Bass", "Other", "Vocals")));
            Assert.That(info.AudioTracks, Is.EqualTo(5));
            Assert.That(info.StemNames, Is.EqualTo(new[] { "Drums", "Bass", "Other", "Vocals" }));
        }

        [Test]
        public void ReadInfo_OnlyAudioTracksAreCounted()
        {
            // Pictures in front and text behind, as a file with a cover or a video would have.
            StemFileInfo info = Info(Start, Sound, Index(Tracks("vide", "soun", "soun", "soun", "text"), List("Drums", "Bass", "Other", "Vocals")));
            Assert.That(info.AudioTracks, Is.EqualTo(3));
            Assert.That(info.StemNames, Is.EqualTo(new[] { "Drums", "Bass" }));
        }

        [Test]
        public void ReadInfo_StemsTheListDoesNotNameAreNumbered()
        {
            string some = "{\"stems\":[{\"name\":\"Kick\"},{\"color\":\"#ffffff\"},{\"name\":\"   \"}]}";
            Assert.That(Info(Start, Sound, Index(AudioTracks(5), some)).StemNames, Is.EqualTo(new[] { "Kick", "Stem 2", "Stem 3", "Stem 4" }));
            // A list with nothing in it, and one that is not a list of stems at all.
            Assert.That(Info(Start, Sound, Index(AudioTracks(3), "{}")).StemNames, Is.EqualTo(new[] { "Stem 1", "Stem 2" }));
            Assert.That(Info(Start, Sound, Index(AudioTracks(3), "{\"stems\":7}")).StemNames, Is.EqualTo(new[] { "Stem 1", "Stem 2" }));
            Assert.That(Info(Start, Sound, Index(AudioTracks(3), "[1,2]")).StemNames, Is.EqualTo(new[] { "Stem 1", "Stem 2" }));
            // More names than there are tracks for.
            Assert.That(Info(Start, Sound, Index(AudioTracks(3), List("A", "B", "C", "D"))).StemNames, Is.EqualTo(new[] { "A", "B" }));
        }

        [Test]
        public void ReadInfo_ReadsNamesHoweverTheListIsWritten()
        {
            // Spread over lines, with every kind of value in the parts that are skipped, escapes
            // in the names, and the zero bytes Traktor leaves after the list.
            string list = "{ \"version\" : 2, \"offset\": -1.5e2,\r\n\t\"mastering_dsp\": {\"compressor\": {\"enabled\": true, \"x\": null, \"y\": [1, [2, {}], []]}},\n"
                + "\t\"stems\": [ {\"name\": \"Dr\\u00fcms \\\"A\\\"\"}, {\"name\": \"B\\\\ass\\/1\"}, {\"name\":\" Other \"},{\"name\":\"Vöcals\"} ] }\0\0";
            Assert.That(Info(Start, Sound, Index(AudioTracks(5), list)).StemNames, Is.EqualTo(new[] { "Drüms \"A\"", "B\\ass/1", "Other", "Vöcals" }));
        }

        [Test]
        public void ReadInfo_FindsTheListOfTracksWhereverItIs()
        {
            byte[] index = Index(AudioTracks(5), List("Drums", "Bass", "Other", "Vocals"));
            string[] expected = { "Drums", "Bass", "Other", "Vocals" };
            // In front of the audio, as a file made ready for streaming has it.
            Assert.That(Info(Start, index, Sound).StemNames, Is.EqualTo(expected));
            Assert.That(Info(Start, Box("free", new byte[33]), Sound, Box("free"), index, Box("free", new byte[7])).StemNames, Is.EqualTo(expected));

            // Audio too long for four bytes gives its length in eight more.
            byte[] longSound = Join(new byte[] { 0, 0, 0, 1 }, Encoding.ASCII.GetBytes("mdat"), new byte[] { 0, 0, 0, 0, 0, 0, 0x13, 0x98 }, new byte[5000]);
            Assert.That(Info(Start, longSound, index).StemNames, Is.EqualTo(expected));

            // A length of 0 means the box runs to the end of the file.
            byte[] toTheEnd = (byte[])index.Clone();
            Array.Clear(toTheEnd, 0, 4);
            Assert.That(Info(Start, Sound, toTheEnd).StemNames, Is.EqualTo(expected));
        }

        [Test]
        public void ReadInfo_RefusesWhatIsNotAStemFile()
        {
            byte[] index = Index(AudioTracks(5), List("Drums", "Bass", "Other", "Vocals"));
            Assert.That(() => StemFile.ReadInfo(new MemoryStream(Fixtures.Read("audio/tone16.wav"))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Not an MP4"));
            Assert.That(() => Info(new byte[0]), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Not an MP4"));
            Assert.That(() => Info(new byte[5]), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Not an MP4"));

            // An MP4 that holds a song and nothing else.
            Assert.That(() => Info(Start, Sound, Index(AudioTracks(1), null)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no list of stems"));
            Assert.That(() => Info(Start, Sound, Index(AudioTracks(5), null)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no list of stems"));
            Assert.That(() => Info(Start, Sound, Index(AudioTracks(1), List("Drums"))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("1 audio track,"));
            Assert.That(() => Info(Start, Sound, Index(Tracks("vide"), List("Drums"))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("0 audio tracks,"));

            // Cut short: before the list of tracks, and in the middle of it.
            Assert.That(() => Info(Start, Sound), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no list of its tracks"));
            byte[] whole = Join(Start, Sound, index);
            Assert.That(() => StemFile.ReadInfo(new MemoryStream(whole, 0, whole.Length - 40)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("cut short"));

            // A box inside the list that says it is longer than what holds it.
            byte[] overrun = (byte[])index.Clone();
            overrun[8 + 2] = 0x7F;
            Assert.That(() => Info(Start, Sound, overrun), Throws.TypeOf<InvalidDataException>().With.Message.Contains("damaged"));

            // A list that is not JSON, or stops half way, or is nested without end.
            foreach (string broken in new[] { "stems", "{\"stems\":[{\"name\":\"Drums\"", "{\"stems\":[{\"name\":\"Dru", "{\"stems\" [] }", "{} {}", new string('[', 5000) })
                Assert.That(() => Info(Start, Sound, Index(AudioTracks(5), broken)), Throws.TypeOf<InvalidDataException>().With.Message.Contains("cannot be read"), broken.Substring(0, Math.Min(30, broken.Length)));
        }

        [Test]
        public void AStemFileIsKnownByItsNameAndNamesItsSong()
        {
            Assert.That(StemFile.HasStemExtension("/music/Artist - Title.stem.mp4"), Is.True);
            Assert.That(StemFile.HasStemExtension("song.STEM.M4A"), Is.True);
            Assert.That(StemFile.HasStemExtension("song.mp3"), Is.False);
            Assert.That(StemFile.HasStemExtension("mp4"), Is.False);
            Assert.That(StemFile.SongName("/music/Artist - Title.stem.mp4"), Is.EqualTo("Artist - Title"));
            Assert.That(StemFile.SongName("QX1E.STEM.m4a"), Is.EqualTo("QX1E"));
            Assert.That(StemFile.SongName("plain.mp4"), Is.EqualTo("plain"));
        }

        // ---- files written as Traktor writes them, with tracks encoded by macOS ----

        static string[] FixturePaths()
        {
            // A stem file says so in its name. An MP4 without that is a song by itself.
            string[] paths = Fixtures.AudioPaths("mp4", "m4a").Where(path => Path.GetFileName(path).Contains(".stem.")).ToArray();
            Assert.That(paths, Is.Not.Empty);
            return paths;
        }

        static void NeedsAMac()
        {
            if (!StemFile.CanReadLossless)
                Assert.Ignore("This needs macOS's own decoder, so it only runs on a Mac.");
        }

        /// <summary>Apple Lossless gives back what went in; AAC only something close to it.</summary>
        static bool IsLossless(string path)
        {
            return Path.GetFileName(path).Contains(".alac.");
        }

        /// <summary>The fixtures that can be read on this system: those coded as AAC anywhere, the lossless ones on a Mac.</summary>
        static string[] ReadableFixturePaths()
        {
            string[] paths = FixturePaths().Where(path => !IsLossless(path) || StemFile.CanReadLossless).ToArray();
            Assert.That(paths, Is.Not.Empty);
            return paths;
        }

        static WavData AtTargetRate(short[] samples, WavData like)
        {
            return Resampler.ToTargetRate(new WavData { SampleRate = like.SampleRate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = like.SourceBitsPerSample });
        }

        /// <summary>The reference sound the way stem <paramref name="stem"/> of a fixture holds it.</summary>
        static short[] Turned(short[] source, int stem)
        {
            int frames = source.Length / 2;
            var turned = new short[source.Length];
            for (int i = 0; i < frames; i++)
            {
                int from = stem >= 2 ? frames - 1 - i : i;
                turned[2 * i] = source[2 * from + stem % 2];
                turned[2 * i + 1] = source[2 * from + 1 - stem % 2];
            }
            return turned;
        }

        /// <summary>How far apart two sounds are, as a share of the level of the second.</summary>
        static double Distance(short[] first, short[] second)
        {
            Assert.That(first.Length, Is.EqualTo(second.Length));
            double difference = 0, level = 0;
            for (int i = 0; i < first.Length; i++)
            {
                difference += Math.Abs(first[i] - second[i]);
                level += Math.Abs(second[i]);
            }
            return difference / level;
        }

        static short[] Later(short[] samples, int frames)
        {
            var moved = new short[samples.Length];
            if (frames >= 0)
                Array.Copy(samples, 0, moved, 2 * frames, samples.Length - 2 * frames);
            else
                Array.Copy(samples, -2 * frames, moved, 0, samples.Length + 2 * frames);
            return moved;
        }

        static string[] SongPaths()
        {
            string[] paths = Fixtures.AudioPaths("mp4", "m4a").Where(path => !Path.GetFileName(path).Contains(".stem.")).ToArray();
            Assert.That(paths, Is.Not.Empty);
            return paths;
        }

        [Test]
        public void Mp4Song_ReadsAnM4aAsTheSoundItWasMadeFrom()
        {
            // Written by Apple's encoder, which notes its own frames in front of the sound among the
            // file's tags and writes no edit list. Read rightly, each file is the length of its WAV.
            int read = 0;
            foreach (string path in SongPaths())
            {
                string name = Path.GetFileName(path);
                Assert.That(StemFile.IsStemFile(path), Is.False, name);
                if (IsLossless(path) && !StemFile.CanReadLossless)
                    continue;
                read++;
                WavData reference = WavReader.Read(Fixtures.ReferenceWav(path));
                WavData song = AudioFile.Read(path);
                Assert.That(song.SampleRate, Is.EqualTo(Resampler.TargetRate), name);
                Assert.That(song.Frames, Is.EqualTo(reference.Frames), name);
                if (IsLossless(path))
                {
                    CollectionAssert.AreEqual(reference.Samples, song.Samples, name);
                    continue;
                }
                // The same audio comes from the bytes alone, which is how AAC is read on every system.
                CollectionAssert.AreEqual(song.Samples, AudioFile.Read(File.ReadAllBytes(path)).Samples, name);
                Assert.That(Distance(song.Samples, reference.Samples), Is.LessThan(0.15), name);
                // A frame early or late would be further off than the coding leaves it.
                Assert.That(Distance(Later(song.Samples, 1), reference.Samples), Is.GreaterThan(0.25), name);
                Assert.That(Distance(Later(song.Samples, -1), reference.Samples), Is.GreaterThan(0.25), name);

                // Among stems it is one stem like any other file, named after itself.
                List<Stem> stems = StemsToSp16.Read(new[] { path });
                Assert.That(stems.Count, Is.EqualTo(1), name);
                Assert.That(stems[0].Name, Is.EqualTo(Path.GetFileNameWithoutExtension(path)), name);
                CollectionAssert.AreEqual(song.Samples, stems[0].Audio.Samples, name);
            }
            Assert.That(read, Is.GreaterThan(0));
            foreach (string path in FixturePaths())
                Assert.That(StemFile.IsStemFile(path), Is.True, Path.GetFileName(path));
        }

        [Test]
        public void Fixtures_ListFiveTracksAndTheFourStems()
        {
            foreach (string path in FixturePaths())
            {
                StemFileInfo info = StemFile.ReadInfo(path);
                Assert.That(info.AudioTracks, Is.EqualTo(5), Path.GetFileName(path));
                Assert.That(info.StemNames, Is.EqualTo(FixtureStems), Path.GetFileName(path));
            }
        }

        [Test]
        public void Read_FixturesGiveEachStemFromItsOwnTrackOnItsFirstSample()
        {
            foreach (string path in ReadableFixturePaths())
            {
                string name = Path.GetFileName(path);
                WavData reference = WavReader.Read(Fixtures.ReferenceWav(path));
                bool lossless = IsLossless(path);
                bool converted = reference.SampleRate != Resampler.TargetRate;
                // A file at another rate comes back at 44.1 kHz, each stem converted as it was in the file.
                var held = new List<WavData>();
                for (int i = 0; i < FixtureStems.Length; i++)
                    held.Add(AtTargetRate(Turned(reference.Samples, i), reference));

                List<StemFileTrack> stems = StemFile.Read(path);
                Assert.That(stems.ConvertAll(stem => stem.Name), Is.EqualTo(FixtureStems), name);
                for (int i = 0; i < stems.Count; i++)
                {
                    WavData read = stems[i].Audio;
                    string which = name + ", " + stems[i].Name;
                    Assert.That(read.SampleRate, Is.EqualTo(Resampler.TargetRate), which);
                    Assert.That(read.SourceSampleRate, Is.EqualTo(converted ? reference.SampleRate : 0), which);
                    Assert.That(read.SourceChannels, Is.EqualTo(2), which);
                    Assert.That(read.SourceBitsPerSample, Is.EqualTo(lossless ? reference.SourceBitsPerSample : 16), which);
                    // The encoder's priming in front and its padding behind are both left out.
                    Assert.That(read.Frames, Is.EqualTo(held[i].Frames), which);
                    short[] expected = held[i].Samples;
                    if (lossless)
                    {
                        // 24 bits come down to 16 by the rule the WAV reader uses.
                        CollectionAssert.AreEqual(expected, read.Samples, which);
                        continue;
                    }
                    for (int other = 0; other < stems.Count; other++)
                    {
                        double distance = Distance(read.Samples, held[other].Samples);
                        // The coding itself leaves a stem about 0.035 from what went in. A decoder that does
                        // nothing with TNS, as this one did when it was downloaded, leaves it 0.09 away.
                        if (other == i)
                            Assert.That(distance, Is.LessThan(0.05), which);
                        else
                            Assert.That(distance, Is.GreaterThan(1.0), which + " against what stem " + (other + 1) + " should hold");
                    }
                    // A frame early or late would be far further off than the coding leaves it.
                    Assert.That(Distance(Later(read.Samples, 1), expected), Is.GreaterThan(0.2), which);
                    Assert.That(Distance(Later(read.Samples, -1), expected), Is.GreaterThan(0.2), which);
                }
            }
        }

        [Test]
        public void AacReader_IsWithinOneStepOfWhatMacOsDecodes()
        {
            NeedsAMac();
            // Two decoders written apart agree to the rounding of the last bit, at every sample of
            // every track. With the decoder as it was downloaded, which did nothing with TNS, the
            // fixtures were up to 28,000 steps apart.
            int files = 0;
            foreach (string path in FixturePaths().Where(path => !IsLossless(path)))
            {
                files++;
                using (FileStream stream = File.OpenRead(path))
                {
                    List<Mp4AudioTrack> tracks = Mp4.AudioTracks(stream);
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        string which = Path.GetFileName(path) + ", track " + (i + 1);
                        WavData ours = AacReader.Read(stream, tracks[i]);
                        WavData mac = AudioToolboxReader.ReadTrack(path, i);
                        Assert.That(ours.SampleRate, Is.EqualTo(mac.SampleRate), which);
                        Assert.That(ours.Samples.Length, Is.EqualTo(mac.Samples.Length), which);
                        int worst = 0, same = 0;
                        for (int k = 0; k < ours.Samples.Length; k++)
                        {
                            int apart = Math.Abs(ours.Samples[k] - mac.Samples[k]);
                            worst = Math.Max(worst, apart);
                            if (apart == 0)
                                same++;
                        }
                        Assert.That(worst, Is.LessThanOrEqualTo(1), which);
                        Assert.That(same, Is.GreaterThan(ours.Samples.Length * 9 / 10), which);
                    }
                }
            }
            Assert.That(files, Is.GreaterThanOrEqualTo(2));
        }

        [Test]
        public void Read_AacStemsAreTheSameSamplesEveryTime()
        {
            foreach (string path in FixturePaths().Where(path => !IsLossless(path)))
            {
                List<StemFileTrack> first = StemFile.Read(path), second = StemFile.Read(path);
                for (int i = 0; i < first.Count; i++)
                    CollectionAssert.AreEqual(first[i].Audio.Samples, second[i].Audio.Samples, Path.GetFileName(path) + ", " + first[i].Name);
            }
        }

        [Test]
        public void AacReader_TakesAacTracksAndNoOthers()
        {
            foreach (string path in FixturePaths())
                using (FileStream stream = File.OpenRead(path))
                {
                    List<Mp4AudioTrack> tracks = Mp4.AudioTracks(stream);
                    Assert.That(tracks.Count, Is.EqualTo(5), Path.GetFileName(path));
                    foreach (Mp4AudioTrack track in tracks)
                    {
                        Assert.That(track.Coding, Is.EqualTo(IsLossless(path) ? "alac" : "mp4a"), Path.GetFileName(path));
                        Assert.That(AacReader.CanDecode(track), Is.EqualTo(!IsLossless(path)), Path.GetFileName(path));
                        if (IsLossless(path))
                            Assert.That(() => AacReader.Read(stream, track), Throws.TypeOf<NotSupportedException>().With.Message.Contains("alac"));
                    }
                }
        }

        [Test]
        public void AacReader_ReadsATrackFromItsFirstRealFrameToItsLast()
        {
            // The encoder puts 2,112 frames of its own in front and fills out the last packet. The file's
            // edit list says so, and what is read is the sound alone: the full mix here is silence
            // as long as the reference.
            string path = FixturePaths().First(candidate => !IsLossless(candidate));
            WavData reference = WavReader.Read(Fixtures.ReferenceWav(path));
            using (FileStream stream = File.OpenRead(path))
            {
                List<Mp4AudioTrack> tracks = Mp4.AudioTracks(stream);
                long first, frames;
                tracks[0].Sound(reference.SampleRate, out first, out frames);
                Assert.That(first, Is.EqualTo(2112));
                Assert.That(frames, Is.EqualTo(reference.Frames));
                Assert.That(tracks[0].PacketCount * 1024, Is.GreaterThan(first + frames));

                WavData mix = AacReader.Read(stream, tracks[0]);
                Assert.That(mix.SampleRate, Is.EqualTo(reference.SampleRate));
                Assert.That(mix.Frames, Is.EqualTo(reference.Frames));
                Assert.That(mix.SourceChannels, Is.EqualTo(2));
                Assert.That(mix.Samples.All(sample => sample == 0));

                var cancel = new CancellationTokenSource();
                cancel.Cancel();
                Assert.That(() => AacReader.Read(stream, tracks[1], null, cancel.Token), Throws.InstanceOf<OperationCanceledException>());
            }
        }

        [Test]
        public void Fixtures_OneOfThemIsAt48kHz()
        {
            // Without it nothing above would show that a stem file at another rate is converted.
            Assert.That(FixturePaths().Count(path => WavReader.Read(Fixtures.ReferenceWav(path)).SampleRate == 48000), Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Read_LeavesOutTheFullMixThatComesFirst()
        {
            NeedsAMac();
            // In the fixtures the first track is silence, and no stem is.
            foreach (string path in FixturePaths())
            {
                Assert.That(AudioToolboxReader.TrackCount(path), Is.EqualTo(5), Path.GetFileName(path));
                Assert.That(AudioToolboxReader.ReadTrack(path, 0).Samples.All(sample => sample == 0), Path.GetFileName(path));
                foreach (StemFileTrack stem in StemFile.Read(path))
                    Assert.That(stem.Audio.Samples.Any(sample => Math.Abs(sample) > 1000), Path.GetFileName(path) + ", " + stem.Name);
            }
        }

        sealed class Recorded : IProgress<float>
        {
            public readonly List<float> Values = new List<float>();
            public void Report(float value) { Values.Add(value); }
        }

        [Test]
        public void Read_ReportsProgressThroughTheStemsAndStopsWhenCancelled()
        {
            // AAC stems are read side by side and the lossless ones one after another; either way
            // what is reported only ever goes up, and ends at the end.
            foreach (string path in ReadableFixturePaths())
            {
                var progress = new Recorded();
                StemFile.Read(path, progress);
                Assert.That(progress.Values, Is.Not.Empty, Path.GetFileName(path));
                Assert.That(progress.Values, Is.Ordered, Path.GetFileName(path));
                Assert.That(progress.Values[0], Is.GreaterThan(0f), Path.GetFileName(path));
                Assert.That(progress.Values[progress.Values.Count - 1], Is.EqualTo(1f).Within(1e-6), Path.GetFileName(path));

                var cancel = new CancellationTokenSource();
                cancel.Cancel();
                Assert.That(() => StemFile.Read(path, null, cancel.Token), Throws.InstanceOf<OperationCanceledException>(), Path.GetFileName(path));
            }
        }

        [Test]
        public void ReadTrack_RefusesATrackOrAFileThatIsNotThere()
        {
            NeedsAMac();
            string path = FixturePaths()[0];
            Assert.That(() => AudioToolboxReader.ReadTrack(path, 5), Throws.TypeOf<InvalidDataException>().With.Message.Contains("5 audio tracks"));
            Assert.That(() => AudioToolboxReader.ReadTrack(path, -1), Throws.TypeOf<InvalidDataException>());
            Assert.That(() => AudioToolboxReader.ReadTrack(Path.Combine(Fixtures.Folder, "audio", "nothing.stem.mp4"), 0), Throws.TypeOf<FileNotFoundException>());
            Assert.That(() => StemFile.Read(Path.Combine(Fixtures.Folder, "audio", "nothing.stem.mp4")), Throws.TypeOf<FileNotFoundException>());
            // Not audio at all.
            Assert.That(() => AudioToolboxReader.ReadTrack(Path.Combine(Fixtures.Folder, "arr0.prj"), 0), Throws.TypeOf<InvalidDataException>().With.Message.Contains("macOS could not open"));
        }

        [Test]
        public void ReadTrack_RoundsWiderAudioAsTheOtherReadersDo()
        {
            NeedsAMac();
            // macOS reads the other lossless files too, each as a file with one track. Whatever it
            // is given, 16 bits come through as they are and 24 are rounded here, by the rule
            // every format shares.
            foreach (string path in Fixtures.AudioPaths("wav", "aif", "aiff", "aifc", "flac"))
            {
                WavData expected = WavReader.Read(Fixtures.ReferenceWav(path));
                WavData read = AudioToolboxReader.ReadTrack(path, 0);
                Assert.That(read.SourceBitsPerSample, Is.EqualTo(expected.SourceBitsPerSample), Path.GetFileName(path));
                CollectionAssert.AreEqual(expected.Samples, read.Samples, Path.GetFileName(path));
            }
        }

        [Test]
        public void StemsRead_TakesEveryStemOfAStemFileOnTheOutputsInOrder()
        {
            string path = ReadableFixturePaths()[0];
            List<Stem> stems = StemsToSp16.Read(new[] { path, Fixtures.ReferenceWav(path) });
            Assert.That(stems.ConvertAll(stem => stem.Name), Is.EqualTo(FixtureStems.Concat(new[] { Path.GetFileNameWithoutExtension(Fixtures.ReferenceWav(path)) })));
            Assert.That(stems.ConvertAll(stem => stem.Output), Is.EqualTo(new[] { Sp16Output.Out12, Sp16Output.Out34, Sp16Output.Out56, Sp16Output.Out78, Sp16Output.Out12 }));
            // The file after it is still read as itself.
            CollectionAssert.AreEqual(WavReader.Read(Fixtures.ReferenceWav(path)).Samples, stems[4].Audio.Samples);
        }
    }
}
