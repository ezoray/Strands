using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Strands.Audio;
using Strands.Export;
using Strands.Sp16;

namespace Strands.Tests
{
    public class AudioAndExportTests
    {
        const int Rate = 44100;

        /// <summary>
        /// A kick-like thump on every beat, a brighter tick on the off-beats, and a tone that enters and
        /// leaves on bar lines, so tempo, phase and bar start are all known.
        /// </summary>
        internal static WavData Synthesize(double bpm, double firstBeatSeconds, int downbeatIndex, double seconds)
        {
            int frames = (int)(seconds * Rate);
            var mix = new double[frames];
            double beat = 60.0 / bpm;
            var random = new Random(7);

            for (int k = 0; ; k++)
            {
                double time = firstBeatSeconds + k * beat;
                int start = (int)Math.Round(time * Rate);
                if (start >= frames)
                    break;
                for (int i = 0; i < 6000 && start + i < frames; i++)
                {
                    double t = (double)i / Rate;
                    mix[start + i] += 0.6 * Math.Exp(-t * 30) * Math.Sin(2 * Math.PI * (55 + 80 * Math.Exp(-t * 60)) * t);
                }
                int off = (int)Math.Round((time + beat / 2) * Rate);
                for (int i = 0; i < 1500 && off + i < frames; i++)
                    mix[off + i] += 0.15 * Math.Exp(-i / 300.0) * (random.NextDouble() * 2 - 1);

                // A sustained part that comes in for two bars and drops out for two, always on a bar line.
                bool barStart = ((k - downbeatIndex) % 4 + 4) % 4 == 0;
                int bar = (k - downbeatIndex) / 4;
                if (barStart && k >= downbeatIndex && (bar / 2) % 2 == 1)
                {
                    int length = (int)Math.Round(4 * beat * Rate);
                    for (int i = 0; i < length && start + i < frames; i++)
                        mix[start + i] += 0.12 * Math.Sin(2 * Math.PI * 330 * (start + i) / Rate);
                }
            }

            var samples = new short[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                short value = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, mix[i] * 24000));
                samples[2 * i] = value;
                samples[2 * i + 1] = value;
            }
            return new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
        }

        [Test]
        public void Wav_WriteThenReadGivesTheSameSamples()
        {
            WavData wav = Synthesize(120, 0.1, 0, 2);
            byte[] file = WavWriter.Write(wav.Samples, Rate);
            Assert.That(file.Length, Is.EqualTo(44 + wav.Samples.Length * 2));
            WavData read = WavReader.Read(file);
            Assert.That(read.SampleRate, Is.EqualTo(Rate));
            CollectionAssert.AreEqual(wav.Samples, read.Samples);
        }

        [Test]
        public void Wav_HeaderMatchesTheConfirmedPackagesSamples()
        {
            byte[] fixture = Fixtures.ReadEntry("rev1.tpkg", "rev1_A1.wav");
            WavData read = WavReader.Read(fixture);
            Assert.That(read.Frames, Is.EqualTo(17640));
            CollectionAssert.AreEqual(fixture, WavWriter.Write(read.Samples, read.SampleRate));
        }

        [Test]
        public void Wav_AMonoFileHasOneChannelInItsHeaderAndReadsBackOnBothSides()
        {
            var samples = new short[] { 100, -200, 300, -400, 500 };
            byte[] file = WavWriter.WriteMono(samples, Rate);
            Assert.That(file.Length, Is.EqualTo(44 + samples.Length * 2));
            Assert.That(BitConverter.ToUInt16(file, 22), Is.EqualTo(1), "channels");
            Assert.That(BitConverter.ToInt32(file, 28), Is.EqualTo(Rate * 2), "bytes a second");
            Assert.That(BitConverter.ToUInt16(file, 32), Is.EqualTo(2), "bytes a frame");
            Assert.That(BitConverter.ToInt32(file, 40), Is.EqualTo(samples.Length * 2), "bytes of audio");

            WavData read = WavReader.Read(file);
            Assert.That(read.SourceChannels, Is.EqualTo(1));
            Assert.That(read.Frames, Is.EqualTo(samples.Length));
            for (int i = 0; i < samples.Length; i++)
            {
                Assert.That(read.Samples[2 * i], Is.EqualTo(samples[i]));
                Assert.That(read.Samples[2 * i + 1], Is.EqualTo(samples[i]));
            }
        }

        [Test]
        public void Wav_24BitIsRoundedTo16BitAndMonoIsDoubled()
        {
            // One mono 24-bit frame holding 0x012380, which rounds up to 0x0124.
            var file = new List<byte>();
            file.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            file.AddRange(BitConverter.GetBytes(36 + 3));
            file.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            file.AddRange(BitConverter.GetBytes(16));
            file.AddRange(BitConverter.GetBytes((short)1));
            file.AddRange(BitConverter.GetBytes((short)1));
            file.AddRange(BitConverter.GetBytes(Rate));
            file.AddRange(BitConverter.GetBytes(Rate * 3));
            file.AddRange(BitConverter.GetBytes((short)3));
            file.AddRange(BitConverter.GetBytes((short)24));
            file.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            file.AddRange(BitConverter.GetBytes(3));
            file.AddRange(new byte[] { 0x80, 0x23, 0x01 });

            WavData read = WavReader.Read(file.ToArray());
            Assert.That(read.Samples, Is.EqualTo(new short[] { 0x0124, 0x0124 }));
        }

        [TestCase(128.0, 0.25, 0)]
        [TestCase(133.28, 0.111, 2)]
        [TestCase(96.5, 0.40, 1)]
        [TestCase(174.0, 0.05, 3)]
        public void BeatGrid_FindsTempoPhaseAndDownbeatOfASyntheticTrack(double bpm, double firstBeat, int downbeat)
        {
            WavData wav = Synthesize(bpm, firstBeat, downbeat, 45);
            // These tracks are silent up to a first kick that is not beat 1 of its bar. The analyser
            // would take such a kick for beat 1, so that rule is off here: this is about the guess.
            BeatGrid grid = BeatGridAnalyzer.Analyze(wav.Samples, Rate, new BeatGridOptions { DrumsEnterOnTheOne = false });

            Assert.That(grid.Bpm, Is.EqualTo(bpm).Within(0.02), "tempo");
            // Compare phase within one beat, so the test does not depend on which beat is called first.
            double beat = 60.0 / bpm;
            double error = (grid.FirstBeatSeconds - firstBeat) % beat;
            if (error > beat / 2) error -= beat;
            if (error < -beat / 2) error += beat;
            Assert.That(Math.Abs(error), Is.LessThan(0.003), "first beat is off by " + error * 1000 + " ms");

            double expectedDownbeat = firstBeat + downbeat * beat;
            double downbeatError = (grid.FirstDownbeatSeconds - expectedDownbeat) % (4 * beat);
            if (downbeatError > 2 * beat) downbeatError -= 4 * beat;
            if (downbeatError < -2 * beat) downbeatError += 4 * beat;
            Assert.That(Math.Abs(downbeatError), Is.LessThan(0.003), "downbeat is off by " + downbeatError / beat + " beats");
        }

        [Test]
        public void CutChunk_CrossfadedChunksAddBackUpToTheSource()
        {
            WavData wav = Synthesize(128, 0.2, 0, 4);
            const int split = 60000, fade = 220;
            short[] first = WavToSp16.CutChunk(wav.Samples, 0, split + fade, 0, fade);
            short[] second = WavToSp16.CutChunk(wav.Samples, split, wav.Frames - split, fade, 0);

            for (int frame = 0; frame < wav.Frames; frame++)
            {
                int sum = 0;
                if (frame < split + fade)
                    sum += first[frame * 2];
                if (frame >= split)
                    sum += second[(frame - split) * 2];
                Assert.That(Math.Abs(sum - wav.Samples[frame * 2]), Is.LessThanOrEqualTo(1), "frame " + frame);
            }
        }

        [Test]
        public void Export_WritesAPackageTheLibraryReadsBack()
        {
            WavData wav = Synthesize(128, 0.25, 0, 40);
            WavExportResult result;
            byte[] package;
            using (var output = new MemoryStream())
            {
                result = WavToSp16.Export(wav, Fixtures.Read("arr0.prj"), output, "Test Song", new WavExportOptions());
                package = output.ToArray();
            }
            Assert.That(result.ProjectName, Is.EqualTo("Test_Song"));

            // 40 s at 128 BPM from 0.25 s is 21.2 bars: five full 4-bar chunks and a 2-bar remainder.
            Assert.That(result.Chunks.Count, Is.EqualTo(6));
            Assert.That(result.Chunks[5].Bars, Is.EqualTo(2));

            List<StoredZipReader.Entry> entries = StoredZipReader.Read(package);
            Assert.That(entries.Count, Is.EqualTo(1 + 2 * result.Chunks.Count));
            Assert.That(entries[0].Name, Is.EqualTo("Test_Song.prj"));

            Sp16Project project = Sp16Project.FromPrj(entries[0].Data);
            Assert.That(project.PlayArrangement, Is.True);
            Assert.That(project.ProjectBpm, Is.EqualTo(result.Grid.Bpm));
            Assert.That(project.ProjectFilePath, Is.EqualTo("/mnt/emmc/PIONEER DJ SAMPLER/Projects/Test_Song.prj"));

            Arrangement arrangement = project.GetArrangement();
            Assert.That(arrangement.Length, Is.EqualTo(result.Chunks.Count));
            for (int i = 0; i < result.Chunks.Count; i++)
            {
                ChunkInfo chunk = result.Chunks[i];
                Section section = arrangement[i];
                Assert.That(section.Scene, Is.EqualTo(chunk.Scene));
                Assert.That(section.Pattern, Is.EqualTo(chunk.Pattern));
                Assert.That(section.Repeat, Is.EqualTo(1));
                Assert.That(section.Bpm, Is.EqualTo((float)result.Grid.Bpm));

                PrjNode pattern = project.FindPattern(chunk.Scene, chunk.Pattern);
                Assert.That(pattern.Scalar("Length").AsInt32(), Is.EqualTo(chunk.Bars * 16));
                List<KeyValuePair<int, int>> triggers = StepsBlob.FromScalar(pattern.Scalar("Steps")).GetTriggers();
                Assert.That(triggers.Count, Is.EqualTo(1));
                Assert.That(triggers[0].Key, Is.EqualTo(chunk.Pad));
                Assert.That(triggers[0].Value, Is.EqualTo(0));

                PrjNode playback = project.Pad(chunk.Scene, chunk.Pad).Child("PlaybackData");
                Assert.That(playback.Scalar("audioSourceUrl").AsString(), Is.EqualTo("$Int0/%Tmp/" + chunk.FileName));
                Assert.That(playback.Scalar("lengthSample").AsInt32(), Is.EqualTo(chunk.SampleFrames));
                Assert.That(project.Pad(chunk.Scene, chunk.Pad).Scalar("chokeGroup").AsInt32(), Is.EqualTo(-1));

                StoredZipReader.Entry wavEntry = entries[1 + 2 * i], datEntry = entries[2 + 2 * i];
                Assert.That(wavEntry.Name, Is.EqualTo(chunk.FileName));
                Assert.That(datEntry.Name, Is.EqualTo(chunk.FileName + ".dat"));
                Assert.That(WavReader.Read(wavEntry.Data).Frames, Is.EqualTo(chunk.SampleFrames));
                Assert.That(SampleAdjunct.Read(datEntry.Data).SampleNum, Is.EqualTo(chunk.SampleFrames));
                Assert.That((double)chunk.SampleFrames / Rate, Is.LessThanOrEqualTo(Sp16Project.MaxSampleSeconds));
            }

            // Chunks meet end to end, and laid back over each other they rebuild the source.
            var rebuilt = new int[wav.Frames];
            for (int i = 0; i < result.Chunks.Count; i++)
            {
                ChunkInfo chunk = result.Chunks[i];
                if (i > 0)
                    Assert.That(chunk.StartFrame, Is.EqualTo(result.Chunks[i - 1].EndFrame));
                short[] samples = WavReader.Read(entries[1 + 2 * i].Data).Samples;
                for (int frame = 0; frame < chunk.SampleFrames; frame++)
                    rebuilt[chunk.StartFrame + frame] += samples[frame * 2];
            }
            Assert.That(result.Chunks[result.Chunks.Count - 1].EndFrame, Is.EqualTo(wav.Frames));
            for (int frame = result.Chunks[0].StartFrame; frame < wav.Frames; frame++)
                if (Math.Abs(rebuilt[frame] - wav.Samples[frame * 2]) > 1)
                    Assert.Fail("Rebuilt audio differs from the source at frame " + frame);
        }

        [Test]
        public void Export_UsesASecondSceneAfterSixteenChunks()
        {
            // 1-bar chunks at 174 BPM: 40 s holds 28 bars, so the last twelve land in scene 2.
            WavData wav = Synthesize(174, 0.05, 0, 40);
            WavExportResult result;
            byte[] package;
            using (var output = new MemoryStream())
            {
                result = WavToSp16.Export(wav, Fixtures.Read("arr0.prj"), output, "two", new WavExportOptions { BarsPerChunk = 1 });
                package = output.ToArray();
            }
            Assert.That(result.Chunks.Count, Is.GreaterThan(16));
            Assert.That(result.Chunks[16].Scene, Is.EqualTo(1));
            Assert.That(result.Chunks[16].Pad, Is.EqualTo(12));

            PrjNode root = PrjFile.Read(StoredZipReader.Read(package)[0].Data);
            PrjNode scene = root.Child("SceneData0");
            // Sixteen pads, sixteen patterns in ascending order, then the three fixed children.
            Assert.That(scene.Children.Count, Is.EqualTo(16 + 16 + 3));
            for (int pattern = 0; pattern < 16; pattern++)
                Assert.That(scene.Children[16 + pattern].Name, Is.EqualTo("PatternData" + pattern));
            Assert.That(scene.Children[32].Name, Is.EqualTo("SendFx"));
            Assert.That(root.Child("SceneData1").Scalar("name").AsString(), Is.EqualTo("Part2"));
        }

        [Test]
        public void Export_RejectsOtherSampleRates()
        {
            WavData wav = Synthesize(128, 0.25, 0, 10);
            wav.SampleRate = 48000;
            Assert.Throws<NotSupportedException>(() => WavToSp16.Plan(wav, "x", new WavExportOptions()));
        }

        static List<double> BarLines(BeatGrid grid, double upTo)
        {
            var lines = new List<double>();
            for (int beat = 0; grid.TimeOfBeat(beat) < upTo; beat += 4)
                lines.Add(grid.TimeOfBeat(beat));
            return lines;
        }

        [Test]
        public void BeatGrid_NormalizeKeepsEveryBarLine()
        {
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0.1, DownbeatIndex = 2 };
            List<double> before = BarLines(grid, 20);

            grid.FirstBeatSeconds -= 3 * grid.BeatSeconds;
            grid.DownbeatIndex = 1;   // 2 + 3 beats, modulo the bar
            grid.Normalize();

            Assert.That(grid.FirstBeatSeconds, Is.EqualTo(0.1).Within(1e-9));
            Assert.That(grid.FirstDownbeatSeconds, Is.GreaterThanOrEqualTo(0));
            Assert.That(BarLines(grid, 20), Is.EqualTo(before).Within(1e-9));
        }

        [Test]
        public void BeatGrid_ShiftMovesEveryBarLineEvenAcrossTheStart()
        {
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0.004, DownbeatIndex = 0 };
            grid.Shift(-0.010);

            // The bar line that was at 0.004 s is now before the audio, so the next one comes first.
            Assert.That(grid.FirstDownbeatSeconds, Is.EqualTo(0.004 - 0.010 + 2.0).Within(1e-9));
            grid.Shift(0.010);
            Assert.That(grid.FirstDownbeatSeconds, Is.EqualTo(0.004).Within(1e-9));
        }

        [Test]
        public void BeatGrid_SetTempoPivotsOnTheNearestBeatAndKeepsItsPlaceInTheBar()
        {
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0.2, DownbeatIndex = 1 };
            double pivot = grid.TimeOfBeat(8);   // a bar line, 8 beats after the first downbeat

            grid.SetTempo(125, pivot + 0.01);

            Assert.That(grid.Bpm, Is.EqualTo(125));
            bool found = false;
            foreach (double line in BarLines(grid, pivot + 1))
                found |= Math.Abs(line - pivot) < 1e-9;
            Assert.That(found, "the pivot is still a bar line");
            Assert.That(grid.FirstBeatSeconds, Is.InRange(0.0, grid.BeatSeconds));
        }

        [Test]
        public void BeatGrid_ReportsWhereTheBeatComesIn()
        {
            // Eight seconds of a plain tone, then the usual kick pattern.
            WavData drums = Synthesize(128, 0.25, 0, 40);
            int lead = 8 * Rate;
            var samples = new short[drums.Samples.Length + lead * 2];
            for (int i = 0; i < lead; i++)
                samples[2 * i] = samples[2 * i + 1] = (short)(3000 * Math.Sin(2 * Math.PI * 220 * i / Rate));
            Array.Copy(drums.Samples, 0, samples, lead * 2, drums.Samples.Length);

            BeatGrid grid = BeatGridAnalyzer.Analyze(samples, Rate);

            // The first kick is at 8.25 s. It is found to the millisecond, the quiet tone before it
            // does not disturb the tempo, and it is taken as the first beat of a bar.
            Assert.That(grid.Bpm, Is.EqualTo(128).Within(1e-9));
            Assert.That(grid.FirstSteadyBeatSeconds, Is.EqualTo(8.25).Within(0.003));
            Assert.That(grid.DownbeatFromDrums);
            double barsBefore = (grid.FirstSteadyBeatSeconds - grid.FirstDownbeatSeconds) / (4 * grid.BeatSeconds);
            Assert.That(barsBefore, Is.EqualTo(Math.Round(barsBefore)).Within(1e-6));
        }

        [Test]
        public void BeatGrid_TakesAFileCutOnAHitAsStartingOnBeatOne()
        {
            // The same track twice: cut 10 ms before a kick that is beat 3 of its bar, and cut 300 ms
            // before the next kick, so that the file starts with the off-beat tick between the two.
            WavData wav = Synthesize(120, 0.5, 2, 30);
            var onTheHit = new short[wav.Samples.Length - 2 * (int)(0.49 * Rate)];
            Array.Copy(wav.Samples, wav.Samples.Length - onTheHit.Length, onTheHit, 0, onTheHit.Length);
            var early = new short[wav.Samples.Length - 2 * (int)(0.70 * Rate)];
            Array.Copy(wav.Samples, wav.Samples.Length - early.Length, early, 0, early.Length);

            BeatGrid cut = BeatGridAnalyzer.Analyze(onTheHit, Rate);
            Assert.That(cut.FirstSteadyBeatSeconds, Is.EqualTo(0.01).Within(0.003));
            Assert.That(cut.DownbeatFromDrums);
            Assert.That(cut.FirstDownbeatSeconds, Is.EqualTo(0.01).Within(0.003));

            Assert.That(BeatGridAnalyzer.Analyze(early, Rate).DownbeatFromDrums, Is.False);
        }

        [TestCase(0.05)]
        [TestCase(0.3)]
        [TestCase(1.2)]
        public void BeatGrid_SilenceInFrontOfAFileCutOnAHitDoesNotHideThat(double silentSeconds)
        {
            // Found on a separated song: 44 ms of nothing, then the first kick. The kick is
            // beat 3 of its bar in the track this is cut from, so the downbeat guess has to be overruled.
            WavData wav = Synthesize(120, 0.5, 2, 30);
            int cut = (int)(0.49 * Rate), silent = (int)(silentSeconds * Rate);
            var samples = new short[wav.Samples.Length - 2 * cut + 2 * silent];
            Array.Copy(wav.Samples, 2 * cut, samples, 2 * silent, wav.Samples.Length - 2 * cut);
            double kick = silentSeconds + 0.01;

            BeatGrid grid = BeatGridAnalyzer.Analyze(samples, Rate);
            Assert.That(grid.FirstSteadyBeatSeconds, Is.EqualTo(kick).Within(0.003));
            Assert.That(grid.DownbeatFromDrums);
            Assert.That(grid.FirstDownbeatSeconds, Is.EqualTo(kick).Within(0.003));

            // With the app's options the first pattern starts on the kick: no pattern of its own for the silence or for a part bar.
            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true, SliceOnBeats = true, PatternAnchorSeconds = grid.FirstSteadyBeatSeconds };
            WavExportResult plan = WavToSp16.Plan(new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 }, "x", options, grid);
            Assert.That(plan.Chunks[0].IsLeadIn, Is.False);
            Assert.That(plan.Chunks[0].Bars, Is.EqualTo(4));
            Assert.That((double)plan.Chunks[0].StartFrame / Rate, Is.EqualTo(kick).Within(0.003));
        }

        [Test]
        public void BeatGrid_IsNotFooledByALoudSampleInTheIntro()
        {
            // Loud hits at an unrelated pace for five seconds, a gap, then the kick pattern from 8.25 s.
            WavData drums = Synthesize(128, 0.25, 0, 40);
            int lead = 8 * Rate;
            var samples = new short[drums.Samples.Length + lead * 2];
            for (double time = 0.31; time < 5; time += 0.3713)
            {
                int start = (int)(time * Rate);
                for (int i = 0; i < 4000; i++)
                    samples[2 * (start + i)] = samples[2 * (start + i) + 1] = (short)(20000 * Math.Exp(-i / 700.0) * Math.Sin(2 * Math.PI * 90 * i / Rate));
            }
            Array.Copy(drums.Samples, 0, samples, lead * 2, drums.Samples.Length);

            BeatGrid grid = BeatGridAnalyzer.Analyze(samples, Rate);

            Assert.That(grid.Bpm, Is.EqualTo(128).Within(1e-9));
            Assert.That(grid.FirstSteadyBeatSeconds, Is.EqualTo(8.25).Within(0.003));
        }

        [Test]
        public void Plan_StartsAChunkAtTheAnchorAndCountsBackFromIt()
        {
            WavData wav = Synthesize(120, 0.1, 0, 40);
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0.1, DownbeatIndex = 0 };
            const double bar = 2.0;
            // Six bars in: with 4-bar chunks, that leaves two bars and the 0.1 s in front of them.
            var options = new WavExportOptions { KeepLeadIn = true, PatternAnchorSeconds = 0.1 + 6 * bar + 0.004 };

            WavExportResult plan = WavToSp16.Plan(wav, "x", options, grid);

            Assert.That(plan.Chunks[0].IsLeadIn);
            Assert.That(plan.Chunks[0].EndFrame, Is.EqualTo((int)Math.Round((0.1 + 2 * bar) * Rate)));
            // 4.1 s at half a second a beat is 8.2 beats, so 9 with silence in front: 36 steps.
            Assert.That(plan.Chunks[0].PatternLength, Is.EqualTo(36));
            Assert.That(plan.Chunks[0].Bars, Is.EqualTo(3));
            Assert.That(plan.Chunks[1].StartBar, Is.EqualTo(2));
            Assert.That(plan.Chunks[1].PatternLength, Is.EqualTo(64));
            Assert.That(plan.Chunks[2].StartBar, Is.EqualTo(6));
            Assert.That(plan.Chunks[2].StartFrame, Is.EqualTo((int)Math.Round((0.1 + 6 * bar) * Rate)));

            // From a chosen bar, the first chunk is cut short so the next still starts at the anchor.
            options.KeepLeadIn = false;
            options.StartBar = 1;
            WavExportResult fromBarTwo = WavToSp16.Plan(wav, "x", options, grid);
            Assert.That(fromBarTwo.Chunks[0].StartBar, Is.EqualTo(1));
            Assert.That(fromBarTwo.Chunks[0].Bars, Is.EqualTo(1));
            Assert.That(fromBarTwo.Chunks[0].PatternLength, Is.EqualTo(16));
            Assert.That(fromBarTwo.Chunks[1].StartBar, Is.EqualTo(2));
            Assert.That(fromBarTwo.Chunks[2].StartBar, Is.EqualTo(6));
        }

        [Test]
        public void Plan_FromAGridStartsAtTheChosenBar()
        {
            WavData wav = Synthesize(128, 0.25, 0, 40);
            var grid = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.25, DownbeatIndex = 0 };
            double bar = 4 * 60.0 / 128;

            WavExportResult fromStart = WavToSp16.Plan(wav, "x", new WavExportOptions(), grid);
            WavExportResult later = WavToSp16.Plan(wav, "x", new WavExportOptions { StartBar = 3 }, grid);

            Assert.That(fromStart.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(0.25 * Rate)));
            Assert.That(later.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round((0.25 + 3 * bar) * Rate)));
            Assert.That(later.Chunks[0].Pad, Is.EqualTo(12));
            Assert.That(later.Grid, Is.SameAs(grid));
            Assert.Throws<InvalidOperationException>(() => WavToSp16.Plan(wav, "x", new WavExportOptions { StartBar = 500 }, grid));
        }

        [Test]
        public void WritePackage_RemovesTheFileWhenCancelled()
        {
            WavData wav = Synthesize(128, 0.25, 0, 20);
            WavExportResult plan = WavToSp16.Plan(wav, "x", new WavExportOptions());
            string path = Path.Combine(Path.GetTempPath(), "strands_cancel_test.tpkg");
            using (var cancel = new System.Threading.CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.Throws<OperationCanceledException>(() => WavToSp16.WritePackage(wav, Fixtures.Read("arr0.prj"), path, plan, null, cancel.Token));
            }
            Assert.That(File.Exists(path), Is.False);

            float last = 0;
            WavToSp16.WritePackage(wav, Fixtures.Read("arr0.prj"), path, plan, new ImmediateProgress(value => last = value));
            Assert.That(last, Is.EqualTo(1f));
            Assert.That(plan.PackagePath, Is.EqualTo(path));
            File.Delete(path);
        }

        [Test]
        public void S4_SlicesOnBeats_AFourBarChunkGetsSixteen()
        {
            WavData wav = Synthesize(128, 0.25, 0, 20);
            var grid = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.25, DownbeatIndex = 0 };
            var options = new WavExportOptions { KeepLeadIn = true, SliceOnBeats = true };
            WavExportResult plan = WavToSp16.Plan(wav, "slices", options, grid);

            Assert.That(plan.Chunks[0].IsLeadIn, Is.True);
            Assert.That(plan.Chunks[0].SliceFrames, Is.Null);
            ChunkInfo chunk = plan.Chunks[1];
            Assert.That(chunk.Slices, Is.EqualTo(16));
            Assert.That(chunk.SliceFrames[0], Is.EqualTo(chunk.StartFrame));
            Assert.That(chunk.SliceFrames[16], Is.EqualTo(chunk.StartFrame + chunk.SampleFrames));
            for (int k = 1; k < 16; k++)
                Assert.That(chunk.SliceFrames[k], Is.EqualTo((int)Math.Round(grid.TimeOfBeat(chunk.StartBar * 4 + k) * wav.SampleRate)));

            // A point placed by hand takes the place of the beat line nearest to it, and of no other.
            int moved = chunk.SliceFrames[3] + 2000;
            options.ManualSliceFrames.Add(moved);
            ChunkInfo again = WavToSp16.Plan(wav, "slices", options, grid).Chunks[1];
            for (int k = 1; k < 16; k++)
            {
                Assert.That(again.SliceFrames[k], Is.EqualTo(k == 3 ? moved : chunk.SliceFrames[k]));
                Assert.That(again.SliceIsManual[k], Is.EqualTo(k == 3));
            }

            // In the package the boundaries are in frames of the sample, and the count is stored with them.
            string path = Path.Combine(Path.GetTempPath(), "strands-slices-test.tpkg");
            WavExportResult written = WavToSp16.Plan(wav, "slices", options, grid);
            WavToSp16.WritePackage(wav, Fixtures.Read("arr0.prj"), path, written);
            try
            {
                byte[] prj = null;
                foreach (StoredZipReader.Entry entry in StoredZipReader.Read(File.ReadAllBytes(path)))
                    if (entry.Name.EndsWith(".prj"))
                        prj = entry.Data;
                Sp16Project project = Sp16Project.FromPrj(prj);
                int[] stored = project.GetSliceBoundaries(again.Scene, again.Pad);
                Assert.That(stored.Length, Is.EqualTo(17));
                Assert.That(stored[0], Is.EqualTo(0));
                Assert.That(stored[3], Is.EqualTo(moved - again.StartFrame));
                Assert.That(stored[16], Is.EqualTo(again.SampleFrames));
                Assert.That(project.Pad(again.Scene, again.Pad).Child("SliceData").Scalar("nGrids").AsInt32(), Is.EqualTo(16));
                // A lead-in shorter than a beat and a half has no beat line to slice on, and its pad is left as the template has it.
                Assert.That(project.Pad(0, 0).Child("SliceData").Scalar("grids").AsInt32Array(), Is.Empty);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void S4_SlicesOnBeats_TheLeadInIsSlicedFromItsPartBeat()
        {
            // The first beat is at 0.25 s and the first pattern is made to start two bars later, at 4 s,
            // so the lead-in holds a part beat and then eight whole ones.
            WavData wav = Synthesize(128, 0.25, 0, 20);
            var grid = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.25, DownbeatIndex = 0 };
            var options = new WavExportOptions { KeepLeadIn = true, SliceOnBeats = true, PatternAnchorSeconds = 4.0 };
            ChunkInfo lead = WavToSp16.Plan(wav, "x", options, grid).Chunks[0];

            Assert.That(lead.IsLeadIn);
            Assert.That(lead.EndFrame, Is.EqualTo(4 * Rate));
            Assert.That(lead.LeadingSilenceFrames, Is.GreaterThan(0));
            Assert.That(lead.Slices, Is.EqualTo(9));
            int[] stored = lead.SliceBoundariesInSample();
            Assert.That(stored[0], Is.EqualTo(0));
            for (int k = 1; k <= 8; k++)
                Assert.That(stored[k], Is.EqualTo(lead.LeadingSilenceFrames + (int)Math.Round((0.25 + (k - 1) * 60.0 / 128) * Rate)));
            Assert.That(stored[9], Is.EqualTo(lead.SampleFrames));
        }

        [Test]
        public void S6_ATempoOutsideTheUnitsRangeIsWarnedOfApart()
        {
            // The unit plays 60 BPM at the slowest: a song at 57 plays at 60 (Confirmed, SP-16).
            WavData wav = Synthesize(114, 0.25, 0, 20);
            var options = new WavExportOptions();
            WavExportResult half = WavToSp16.Plan(wav, "x", options, new BeatGrid { Bpm = 57, FirstBeatSeconds = 0.25, DownbeatIndex = 0 });
            Assert.That(half.TempoWarning, Is.EqualTo("Under 60 BPM minimum, will play out of time"));
            // The app shows it beside the tempo, so it is not on the warnings line.
            Assert.That(half.Warnings, Has.None.Contains("minimum"));
            Assert.That(half.Describe(), Does.Contain("Warning: Under 60 BPM minimum"));
            WavExportResult sixty = WavToSp16.Plan(wav, "x", options, new BeatGrid { Bpm = 60, FirstBeatSeconds = 0.25, DownbeatIndex = 0 });
            Assert.That(sixty.TempoWarning, Is.Null);
            // And 220 at the fastest (Confirmed, DJS-1000).
            WavExportResult twice = WavToSp16.Plan(wav, "x", options, new BeatGrid { Bpm = 228, FirstBeatSeconds = 0.25, DownbeatIndex = 0 });
            Assert.That(twice.TempoWarning, Is.EqualTo("Above 220 BPM maximum, will play out of time"));
            WavExportResult top = WavToSp16.Plan(wav, "x", options, new BeatGrid { Bpm = 220, FirstBeatSeconds = 0.25, DownbeatIndex = 0 });
            Assert.That(top.TempoWarning, Is.Null);
            Assert.That(WavToSp16.TempoWarningFor(56.5), Does.StartWith("Under 60"));
        }

        [Test]
        public void LeadingSilence_IsLeftOutAndTheLeadInStartsAtTheFirstSound()
        {
            // Ten seconds of nothing, then the song: its first beat is at 10.25 s.
            WavData song = Synthesize(128, 0.25, 0, 20);
            int silentFrames = 10 * Rate;
            var samples = new short[(silentFrames + song.Frames) * 2];
            Array.Copy(song.Samples, 0, samples, silentFrames * 2, song.Samples.Length);
            WavData wav = WavReader.Read(WavWriter.Write(samples, Rate));
            // Bar lines every 1.875 s; with four bars to a chunk the boundaries are at 0.875, 8.375 and 15.875 s.
            var grid = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.875, DownbeatIndex = 0 };
            int firstSound = WavToSp16.FirstSoundFrame(wav);
            Assert.That(firstSound, Is.InRange((int)(10.25 * Rate), (int)(10.26 * Rate)));

            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true };
            WavExportResult plan = WavToSp16.Plan(wav, "x", options, grid);
            ChunkInfo lead = plan.Chunks[0];
            Assert.That(lead.IsLeadIn);
            Assert.That(lead.StartFrame, Is.EqualTo(firstSound));
            // The boundaries in the silence are skipped; the lead-in runs to the first one after the sound starts.
            Assert.That(lead.EndFrame, Is.EqualTo((int)Math.Round(15.875 * Rate)));
            Assert.That(plan.Chunks[1].StartFrame, Is.EqualTo(lead.EndFrame));
            Assert.That(lead.SampleFrames, Is.EqualTo(lead.LeadingSilenceFrames + lead.EndFrame - lead.StartFrame + plan.CrossfadeFrames));
            Assert.That(plan.Warnings, Has.Some.Contains("of silence"));

            // Without the option the silence is kept, as chunks of its own.
            options.TrimLeadingSilence = false;
            Assert.That(WavToSp16.Plan(wav, "x", options, grid).Chunks.Count, Is.GreaterThan(plan.Chunks.Count));

            // Hiss in front of the music is silence too, and when the first sound is a hit on a
            // pattern boundary there is no lead-in at all: the first pattern starts on the hit.
            var random = new Random(3);
            for (int i = 0; i < silentFrames * 2; i++)
                samples[i] = (short)random.Next(-20, 21);
            WavData hissy = WavReader.Read(WavWriter.Write(samples, Rate));
            var onTheHit = new BeatGrid { Bpm = 128, FirstBeatSeconds = 10.25 - 5 * 1.875, DownbeatIndex = 0 };
            options.TrimLeadingSilence = true;
            options.PatternAnchorSeconds = 10.25;
            WavExportResult direct = WavToSp16.Plan(hissy, "x", options, onTheHit);
            Assert.That(direct.Chunks[0].IsLeadIn, Is.False);
            Assert.That(direct.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(10.25 * Rate)));
        }

        [Test]
        public void S5_LeadInGetsItsOwnShortPatternEndingOnTheFirstBarLine()
        {
            WavData wav = Synthesize(128, 0.25, 0, 20);
            var grid = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.25, DownbeatIndex = 0 };
            double stepFrames = 60.0 / 128 / 4 * Rate;
            int leadFrames = (int)Math.Round(0.25 * Rate);

            WavExportResult plan = WavToSp16.Plan(wav, "x", new WavExportOptions { KeepLeadIn = true }, grid);
            ChunkInfo lead = plan.Chunks[0];

            Assert.That(lead.IsLeadIn);
            Assert.That(lead.StartFrame, Is.EqualTo(0));
            Assert.That(lead.EndFrame, Is.EqualTo(leadFrames));
            // 0.25 s is a little over half a beat at 128 BPM, so one beat with silence in front:
            // a lead-in starts on a beat line.
            Assert.That(lead.PatternLength, Is.EqualTo(4));
            Assert.That(lead.LeadingSilenceFrames + leadFrames, Is.EqualTo(4 * stepFrames).Within(1));
            // Its pattern starts on the beat line before the file does, and every other pattern where its audio does.
            Assert.That(lead.PatternStartFrame, Is.EqualTo((int)Math.Round((0.25 - 60.0 / 128) * Rate)).Within(1));
            Assert.That(plan.Chunks[1].PatternStartFrame, Is.EqualTo(plan.Chunks[1].StartFrame));
            Assert.That(plan.Chunks[1].IsLeadIn, Is.False);
            Assert.That(plan.Chunks[1].StartFrame, Is.EqualTo(leadFrames));
            Assert.That(plan.Chunks[1].Pad, Is.EqualTo(13));
            Assert.That(plan.Chunks[1].PatternLength, Is.EqualTo(64));

            // Less than 30 ms in front of the first bar line is only the front of the first hit:
            // it gets no pattern of its own, and the first pattern starts on the bar line.
            var close = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.012, DownbeatIndex = 0 };
            WavExportResult sliver = WavToSp16.Plan(wav, "x", new WavExportOptions { KeepLeadIn = true }, close);
            Assert.That(sliver.Chunks[0].IsLeadIn, Is.False);
            Assert.That(sliver.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(0.012 * Rate)));
            Assert.That(sliver.Chunks[0].PatternLength, Is.EqualTo(64));
            var further = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.040, DownbeatIndex = 0 };
            Assert.That(WavToSp16.Plan(wav, "x", new WavExportOptions { KeepLeadIn = true }, further).Chunks[0].IsLeadIn);

            // Starting at a later bar leaves the lead-in out whatever the option says.
            WavExportResult later = WavToSp16.Plan(wav, "x", new WavExportOptions { KeepLeadIn = true, StartBar = 1 }, grid);
            Assert.That(later.Chunks[0].IsLeadIn, Is.False);

            WavExportResult hardCuts = WavToSp16.Plan(wav, "lead", new WavExportOptions { KeepLeadIn = true, CrossfadeMs = 0 }, grid);
            string path = Path.Combine(Path.GetTempPath(), "strands_leadin_test.tpkg");
            WavToSp16.WritePackage(wav, Fixtures.Read("arr0.prj"), path, hardCuts);
            List<StoredZipReader.Entry> entries = StoredZipReader.Read(File.ReadAllBytes(path));
            File.Delete(path);

            Sp16Project project = Sp16Project.FromPrj(entries[0].Data);
            Assert.That(project.FindPattern(0, 0).Scalar("Length").AsInt32(), Is.EqualTo(4));
            Assert.That(project.FindPattern(0, 1).Scalar("Length").AsInt32(), Is.EqualTo(64));
            Assert.That(project.GetArrangement()[0].Pattern, Is.EqualTo(0));
            Assert.That(project.GetArrangement()[1].Pattern, Is.EqualTo(1));

            // The first sample is silence and then the start of the file, sample for sample,
            // and the second carries straight on from it.
            WavData first = WavReader.Read(entries[1].Data);
            WavData second = WavReader.Read(entries[3].Data);
            int silence = hardCuts.Chunks[0].LeadingSilenceFrames;
            Assert.That(first.Frames, Is.EqualTo(silence + leadFrames));
            for (int i = 0; i < silence; i += 53)
                Assert.That(first.Samples[2 * i], Is.EqualTo(0), "silent frame " + i);
            for (int i = 0; i < leadFrames; i += 53)
                Assert.That(first.Samples[2 * (silence + i)], Is.EqualTo(wav.Samples[2 * i]), "frame " + i);
            Assert.That(second.Samples[0], Is.EqualTo(wav.Samples[2 * leadFrames]));
        }

        [TestCase(128.0)]
        [TestCase(133.28)]
        [TestCase(96.5)]
        public void BeatGrid_TakesARoundTempoWhenTheAudioFitsIt(double bpm)
        {
            WavData wav = Synthesize(bpm, 0.3, 0, 60);
            BeatGrid grid = BeatGridAnalyzer.Analyze(wav.Samples, Rate);
            Assert.That(grid.Bpm, Is.EqualTo(bpm).Within(1e-9));
        }

        [Test]
        public void BeatGrid_KeepsATempoThatIsNotRound()
        {
            // 127.937 is 21 ms away from 128 by the end of a minute, which the audio plainly shows,
            // and 5 ms away from 127.94.
            WavData wav = Synthesize(127.937, 0.3, 0, 60);
            BeatGrid grid = BeatGridAnalyzer.Analyze(wav.Samples, Rate);
            Assert.That(grid.Bpm, Is.EqualTo(127.937).Within(0.004));
            Assert.That(grid.Bpm, Is.Not.EqualTo(128));
        }

        [Test]
        public void Plan_ASongThatOpensOnItsFirstBeatStartsItsFirstPatternThere()
        {
            // Silence, then sound that begins 10 ms after a bar line: the line is at the foot of the
            // first hit, and the sound counts as begun a moment later. That bar is the song's first,
            // not silence to be skipped, which once put the first pattern four bars in.
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0.2, DownbeatIndex = 0 };
            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true };
            int line = (int)Math.Round(0.2 * Rate);
            WavData Song(double afterTheLine)
            {
                var samples = new short[2 * 40 * Rate];
                for (int frame = line + (int)(afterTheLine * Rate); frame < samples.Length / 2; frame++)
                    samples[2 * frame] = samples[2 * frame + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * frame / Rate));
                return new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
            }

            WavExportResult onTheLine = WavToSp16.Plan(Song(0.010), "x", options, grid);
            Assert.That(onTheLine.Chunks[0].IsLeadIn, Is.False);
            Assert.That(onTheLine.Chunks[0].StartFrame, Is.EqualTo(line));
            Assert.That(onTheLine.Chunks[0].StartBar, Is.EqualTo(0));
            // What is left out is the silence in front of that line, and nothing of the bar.
            Assert.That(onTheLine.Warnings, Has.Some.Contains("Dropping first 0.20 seconds of silence"));

            // Sound that begins well into the bar is still a lead-in up to the next pattern, as it was.
            WavExportResult later = WavToSp16.Plan(Song(0.5), "x", options, grid);
            Assert.That(later.Chunks[0].IsLeadIn, Is.True);
            // To a frame or two: the tone's very first sample can be a nought.
            Assert.That(later.Chunks[0].StartFrame, Is.EqualTo(line + (int)(0.5 * Rate)).Within(2));
            Assert.That(later.Chunks[1].StartBar, Is.EqualTo(4));
            Assert.That(later.Warnings, Has.Some.Contains("Dropping first 0.70 seconds of silence"));
        }

        [Test]
        public void LaterStartBars_AreThePatternsOfTheWholePlanAndStartingOnOneLeavesTheRestAsItWas()
        {
            // A song of 40 s with silence in front, at 120 BPM: a bar is 2 s and a pattern 8 s.
            WavData Song(double firstSound)
            {
                var samples = new short[2 * 40 * Rate];
                for (int frame = (int)(firstSound * Rate); frame < samples.Length / 2; frame++)
                    samples[2 * frame] = samples[2 * frame + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * frame / Rate));
                return new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
            }
            // The first sound and the first beat, each way round; patterns counted from the first bar line and from one further in.
            double[][] cases = { new[] { 0.7, 0.2, -1 }, new[] { 0.21, 0.2, -1 }, new[] { 0.0, 1.3, -1 }, new[] { 3.0, 0.5, 12.5 }, new[] { 0.0, 0.0, 6.0 }, new[] { 9.0, 0.1, -1 } };
            // Each with patterns counted as usual, and with one told to start two bars off the count.
            foreach (bool restart in new[] { false, true })
            foreach (double[] one in cases)
            {
                string what = "first sound " + one[0] + ", first beat " + one[1] + ", anchor " + one[2] + (restart ? ", a pattern told to start at 22 s" : "");
                WavData wav = Song(one[0]);
                var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = one[1], DownbeatIndex = 0 };
                var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true };
                if (one[2] >= 0)
                    options.PatternAnchorSeconds = one[2];
                if (restart)
                    options.PatternStartSeconds.Add(22.0);
                WavExportResult whole = WavToSp16.Plan(wav, "x", options, grid);
                bool startsWithLeadIn;
                List<int> bars = WavToSp16.LaterStartBars(wav, options, grid, out startsWithLeadIn);
                Assert.That(startsWithLeadIn, Is.EqualTo(whole.Chunks[0].IsLeadIn), what);
                Assert.That(WavToSp16.LaterStartBars(wav, options, grid), Is.EqualTo(bars), what);

                // Every pattern of the whole plan but the first thing in it.
                var expected = new List<int>();
                for (int i = 1; i < whole.Chunks.Count; i++)
                    expected.Add(whole.Chunks[i].StartBar);
                Assert.That(bars, Is.EqualTo(expected), what);

                for (int skipped = 0; skipped < bars.Count; skipped++)
                {
                    options.KeepLeadIn = false;
                    options.StartBar = bars[skipped];
                    WavExportResult later = WavToSp16.Plan(wav, "x", options, grid);
                    Assert.That(later.Chunks.Count, Is.EqualTo(whole.Chunks.Count - 1 - skipped), what);
                    Assert.That(later.Chunks[0].IsLeadIn, Is.False, what);
                    for (int i = 0; i < later.Chunks.Count; i++)
                    {
                        ChunkInfo was = whole.Chunks[i + 1 + skipped], now = later.Chunks[i];
                        Assert.That(new[] { now.StartFrame, now.EndFrame, now.StartBar, now.Bars, now.PatternLength },
                            Is.EqualTo(new[] { was.StartFrame, was.EndFrame, was.StartBar, was.Bars, was.PatternLength }), what + ", from bar " + bars[skipped]);
                    }
                }
                options.KeepLeadIn = true;
                options.StartBar = 0;
            }
        }

        [Test]
        public void Plan_APatternToldToStartCutsTheOneBeforeItShortAndTheRestAreCountedFromIt()
        {
            // 80 s of sound from the first sample at 120 BPM: a bar is 2 s, and 40 bars from bar line 0.
            var samples = new short[2 * 80 * Rate];
            for (int frame = 0; frame < samples.Length / 2; frame++)
                samples[2 * frame] = samples[2 * frame + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * frame / Rate));
            var wav = new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0, DownbeatIndex = 0 };
            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true };

            List<int> Starts(WavExportResult plan) { var bars = new List<int>(); foreach (ChunkInfo chunk in plan.Chunks) bars.Add(chunk.StartBar); return bars; }
            List<int> Lengths(WavExportResult plan) { var bars = new List<int>(); foreach (ChunkInfo chunk in plan.Chunks) bars.Add(chunk.Bars); return bars; }

            Assert.That(Starts(WavToSp16.Plan(wav, "x", options, grid)), Is.EqualTo(new[] { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36 }));

            // A pattern told to start at bar 10, given a little off its line: the third pattern is two
            // bars, and every one after it starts two bars earlier than it did.
            options.PatternStartSeconds.Add(20.3);
            WavExportResult one = WavToSp16.Plan(wav, "x", options, grid);
            Assert.That(Starts(one), Is.EqualTo(new[] { 0, 4, 8, 10, 14, 18, 22, 26, 30, 34, 38 }));
            Assert.That(Lengths(one), Is.EqualTo(new[] { 4, 4, 2, 4, 4, 4, 4, 4, 4, 4, 2 }));
            Assert.That(one.Chunks[2].PatternLength, Is.EqualTo(2 * Sp16Project.StepsPerBar));
            Assert.That(one.Chunks[3].StartFrame, Is.EqualTo(20 * Rate));
            // The samples still meet end to start.
            for (int i = 0; i + 1 < one.Chunks.Count; i++)
                Assert.That(one.Chunks[i].EndFrame, Is.EqualTo(one.Chunks[i + 1].StartFrame));

            // A second, one bar on: a pattern of one bar between them.
            options.PatternStartSeconds.Add(22.0);
            WavExportResult two = WavToSp16.Plan(wav, "x", options, grid);
            Assert.That(Starts(two), Is.EqualTo(new[] { 0, 4, 8, 10, 11, 15, 19, 23, 27, 31, 35, 39 }));
            Assert.That(two.Chunks[3].PatternLength, Is.EqualTo(Sp16Project.StepsPerBar));

            // Starting later on one of the patterns gives the same patterns from there.
            options.KeepLeadIn = false;
            options.StartBar = 11;
            Assert.That(Starts(WavToSp16.Plan(wav, "x", options, grid)), Is.EqualTo(new[] { 11, 15, 19, 23, 27, 31, 35, 39 }));
        }

        [Test]
        public void Plan_StartBeatDropsBeatsFromTheFrontAndLeavesTheRestAsItWas()
        {
            // 40 s at 120 BPM with bar lines at 0.2 s, 2.2 s and so on, and the first sound on beat 2
            // of the first bar: a lead-in of fifteen beats up to the first pattern, at bar 5.
            var samples = new short[2 * 40 * Rate];
            for (int frame = (int)Math.Round(0.7 * Rate); frame < samples.Length / 2; frame++)
                samples[2 * frame] = samples[2 * frame + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * frame / Rate));
            var wav = new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0.2, DownbeatIndex = 0 };
            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true, SliceOnBeats = true };
            WavExportResult whole = WavToSp16.Plan(wav, "x", options, grid);
            Assert.That(whole.Chunks[0].IsLeadIn, Is.True);
            Assert.That(whole.Chunks[0].PatternLength, Is.EqualTo(60));
            Assert.That(whole.Chunks[1].StartBar, Is.EqualTo(4));
            int[] Shape(ChunkInfo chunk)
            {
                return new[] { chunk.StartFrame, chunk.EndFrame, chunk.StartBar, chunk.Bars, chunk.PatternLength, chunk.Slices };
            }

            // The song begins on beat 2, so the first beat that can be dropped to is beat 3.
            Assert.That(WavToSp16.FirstLaterStartBeat(wav, options, grid), Is.EqualTo(2));
            options.StartBeat = 2;
            WavExportResult oneLess = WavToSp16.Plan(wav, "x", options, grid);
            Assert.That(oneLess.Chunks.Count, Is.EqualTo(whole.Chunks.Count));
            Assert.That(oneLess.Chunks[0].IsLeadIn, Is.True);
            Assert.That(oneLess.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(1.2 * Rate)));
            Assert.That(oneLess.Chunks[0].PatternLength, Is.EqualTo(56));
            // It starts on a step already, so nothing is put in front of it.
            Assert.That(oneLess.Chunks[0].LeadingSilenceFrames, Is.LessThanOrEqualTo(1));
            Assert.That(oneLess.Chunks[0].Slices, Is.EqualTo(14));
            for (int i = 1; i < whole.Chunks.Count; i++)
                Assert.That(Shape(oneLess.Chunks[i]), Is.EqualTo(Shape(whole.Chunks[i])), "pattern " + (i + 1));
            Assert.That(oneLess.Warnings[0], Is.EqualTo("Dropping first 1.20 seconds, before starting beat."));
            Assert.That(oneLess.Warnings, Has.None.Contains("Padding"));

            // On the start of a pattern there is no lead-in, and the plan is the one that starts on that bar.
            options.StartBeat = 16;
            WavExportResult onThePattern = WavToSp16.Plan(wav, "x", options, grid);
            WavExportResult fromTheBar = WavToSp16.Plan(wav, "x", new WavExportOptions { StartBar = 4, SliceOnBeats = true }, grid);
            Assert.That(onThePattern.Chunks[0].IsLeadIn, Is.False);
            Assert.That(onThePattern.Chunks.Count, Is.EqualTo(fromTheBar.Chunks.Count));
            Assert.That(onThePattern.CrossfadeFrames, Is.EqualTo(fromTheBar.CrossfadeFrames));
            for (int i = 0; i < fromTheBar.Chunks.Count; i++)
            {
                Assert.That(Shape(onThePattern.Chunks[i]), Is.EqualTo(Shape(fromTheBar.Chunks[i])), "pattern " + (i + 1));
                Assert.That(onThePattern.Chunks[i].SampleFrames, Is.EqualTo(fromTheBar.Chunks[i].SampleFrames));
                Assert.That(onThePattern.Chunks[i].FileNames, Is.EqualTo(fromTheBar.Chunks[i].FileNames));
            }
            Assert.That(onThePattern.Warnings[0], Is.EqualTo("Dropping first 8.20 seconds, before bar 5."));
            Assert.That(fromTheBar.Warnings[0], Is.EqualTo("Dropping first 8.20 seconds, before bar 5."));

            // A beat into a pattern, what is left of that pattern is the lead-in to the next.
            options.StartBeat = 17;
            WavExportResult intoThePattern = WavToSp16.Plan(wav, "x", options, grid);
            Assert.That(intoThePattern.Chunks[0].IsLeadIn, Is.True);
            Assert.That(intoThePattern.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(8.7 * Rate)));
            Assert.That(intoThePattern.Chunks[0].PatternLength, Is.EqualTo(60));
            Assert.That(intoThePattern.Chunks[1].StartBar, Is.EqualTo(8));

            // A beat in the silence in front drops nothing, and neither does one with the lead-in not kept.
            options.StartBeat = 0;
            WavExportResult nothingDropped = WavToSp16.Plan(wav, "x", options, grid);
            for (int i = 0; i < whole.Chunks.Count; i++)
                Assert.That(Shape(nothingDropped.Chunks[i]), Is.EqualTo(Shape(whole.Chunks[i])));
            Assert.That(nothingDropped.Warnings, Is.EqualTo(whole.Warnings));

            // Past the last pattern there is nothing to play.
            options.StartBeat = 4 * 40;
            Assert.That(() => WavToSp16.Plan(wav, "x", options, grid), Throws.TypeOf<InvalidOperationException>().With.Message.Contains("starting beat"));

            // At a tempo whose beats do not fall on whole frames the two ends of the lead-in are
            // rounded apart by a frame at some beats. It is still a whole number of beats long,
            // with nothing put in front: a frame once made it a step longer, with a step of silence.
            var awkward = new BeatGrid { Bpm = 114, FirstBeatSeconds = 0.7508, DownbeatIndex = 0 };
            for (int beat = 2; beat < 40; beat++)
            {
                options.StartBeat = beat;
                WavExportResult plan = WavToSp16.Plan(wav, "x", options, awkward);
                Assert.That(plan.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(awkward.TimeOfBeat(beat) * Rate)), "beat " + beat);
                Assert.That(plan.Warnings, Has.None.Contains("Padding"), "beat " + beat);
                if (beat % 16 == 0)
                {
                    Assert.That(plan.Chunks[0].IsLeadIn, Is.False, "beat " + beat);
                    continue;
                }
                Assert.That(plan.Chunks[0].PatternLength, Is.EqualTo(4 * (16 - beat % 16)), "beat " + beat);
                Assert.That(plan.Chunks[0].LeadingSilenceFrames, Is.LessThanOrEqualTo(1), "beat " + beat);
            }
        }

        [Test]
        public void FirstLaterStartBeat_IsTheBeatAfterTheOneTheSongBeginsOn()
        {
            WavData Song(double firstSound)
            {
                var samples = new short[2 * 20 * Rate];
                for (int frame = (int)Math.Round(firstSound * Rate); frame < samples.Length / 2; frame++)
                    samples[2 * frame] = samples[2 * frame + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * frame / Rate + 1));
                return new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
            }
            // The first sound, the first bar line, and the beat expected, counted from that line. Beats are half a second.
            double[][] cases =
            {
                new[] { 0.7, 0.2, 2 },      // on beat 2
                new[] { 0.21, 0.2, 1 },     // on the bar line
                new[] { 0.80, 0.2, 2 },     // a fifth of a beat after beat 2: its lead-in starts on beat 2
                new[] { 0.66, 0.2, 1 },     // just before beat 2: its lead-in starts on beat 1, with silence up to the sound
                new[] { 0.95, 0.2, 2 },     // half-way between beats 2 and 3
                new[] { 0.0, 1.3, -2 }      // the file starts in the bar before the first bar line
            };
            foreach (double[] one in cases)
            {
                string what = "first sound " + one[0] + ", first bar line " + one[1];
                WavData wav = Song(one[0]);
                var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = one[1], DownbeatIndex = 0 };
                var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true };
                int first = WavToSp16.FirstLaterStartBeat(wav, options, grid);
                Assert.That(first, Is.EqualTo((int)one[2]), what);

                // Starting there leaves something out, and the lead-in is a beat shorter for it.
                WavExportResult whole = WavToSp16.Plan(wav, "x", options, grid);
                options.StartBeat = first;
                WavExportResult later = WavToSp16.Plan(wav, "x", options, grid);
                int beatFrame = (int)Math.Round(grid.TimeOfBeat(first) * Rate);
                Assert.That(later.Chunks[0].StartFrame, Is.EqualTo(beatFrame), what);
                int began = whole.Chunks[0].IsLeadIn ? whole.Chunks[0].StartFrame : (int)(one[0] * Rate);
                Assert.That(beatFrame, Is.GreaterThan(began), what);
                if (whole.Chunks[0].IsLeadIn)
                    Assert.That(later.Chunks[0].PatternLength, Is.EqualTo(whole.Chunks[0].PatternLength - 4), what);
                // A lead-in is whole beats long, which is what lets it be named by the beat it starts on.
                Assert.That(whole.Chunks[0].PatternLength % 4, Is.EqualTo(0), what);
            }
        }

        [Test]
        public void Plan_SaysWhatIsDroppedFirstAndWhereTheSilenceItAddsGoes()
        {
            // Sound from 0.3 s, a tenth of a second after a beat line: the lead-in is taken back to
            // that line with silence. With the sound 0.3 s after the line, it is taken back as far.
            WavData Song(double firstSound)
            {
                var samples = new short[2 * 20 * Rate];
                for (int frame = (int)(firstSound * Rate); frame < samples.Length / 2; frame++)
                    samples[2 * frame] = samples[2 * frame + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * frame / Rate + 1));
                return new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
            }
            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true };
            // Bar lines at 1.2 s and every 2 s after; beat lines at 0.2 s and 0.7 s before that.
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 1.2, DownbeatIndex = 0 };
            WavExportResult toBeat = WavToSp16.Plan(Song(0.3), "x", options, grid);
            Assert.That(toBeat.Warnings[0], Is.EqualTo("Dropping first 0.30 seconds of silence."));
            Assert.That(toBeat.Warnings[1], Is.EqualTo("Padding 100 ms of silence to starting beat."));
            WavExportResult further = WavToSp16.Plan(Song(0.5), "x", options, grid);
            Assert.That(further.Warnings[0], Is.EqualTo("Dropping first 0.50 seconds of silence."));
            Assert.That(further.Warnings[1], Is.EqualTo("Padding 300 ms of silence to starting beat."));
            Assert.That(further.Chunks[0].PatternLength, Is.EqualTo(8));
        }

        [Test]
        public void Plan_AShortRunOfFaintSoundBeforeABeatLineIsNotWhereTheSongStarts()
        {
            // 120 BPM with bar lines at 1.2 s, 3.2 s and so on, so beat lines at 0.2 and 0.7 s in front.
            // Loud sound from one moment, with something quieter before it from another.
            WavData Song(double quietFrom, int quietLevel, double loudFrom)
            {
                var samples = new short[2 * 20 * Rate];
                for (int frame = (int)Math.Round(quietFrom * Rate); frame < samples.Length / 2; frame++)
                {
                    int level = frame >= (int)Math.Round(loudFrom * Rate) ? 8000 : quietLevel;
                    samples[2 * frame] = samples[2 * frame + 1] = (short)(level * Math.Sin(2 * Math.PI * 220 * frame / Rate + 1));
                }
                return new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
            }
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 1.2, DownbeatIndex = 0 };
            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true };
            int beatLine = (int)Math.Round(0.7 * Rate), barLine = (int)Math.Round(1.2 * Rate);

            // 60 ms at 35 dB under the rest, straight in front of a hit on a beat line: the song
            // starts on that line. Taken as its start, the 60 ms cost a beat of silence in front.
            WavData faint = Song(0.64, 150, 0.7);
            Assert.That(WavToSp16.FirstSoundFrame(faint), Is.EqualTo((int)Math.Round(0.64 * Rate)).Within(2));
            Assert.That(WavToSp16.FirstSoundOnGrid(faint, grid), Is.EqualTo(beatLine));
            WavExportResult plan = WavToSp16.Plan(faint, "x", options, grid);
            Assert.That(plan.Chunks[0].IsLeadIn, Is.True);
            Assert.That(plan.Chunks[0].StartFrame, Is.EqualTo(beatLine));
            Assert.That(plan.Chunks[0].PatternLength, Is.EqualTo(4));
            Assert.That(plan.Chunks[0].LeadingSilenceFrames, Is.EqualTo(0));
            Assert.That(plan.Warnings[0], Is.EqualTo("Dropping first 0.70 seconds of silence."));
            Assert.That(plan.Warnings, Has.None.Contains("Padding"));
            // The beat after the one it starts on is the bar line.
            Assert.That(WavToSp16.FirstLaterStartBeat(faint, options, grid), Is.EqualTo(0));

            // In front of the bar line that starts the first pattern, there is no lead-in at all.
            WavData onTheOne = Song(1.14, 150, 1.2);
            WavExportResult direct = WavToSp16.Plan(onTheOne, "x", options, grid);
            Assert.That(direct.Chunks[0].IsLeadIn, Is.False);
            Assert.That(direct.Chunks[0].StartFrame, Is.EqualTo(barLine));
            Assert.That(direct.Chunks[0].StartBar, Is.EqualTo(0));
            bool startsWithLeadIn;
            WavToSp16.LaterStartBars(onTheOne, options, grid, out startsWithLeadIn);
            Assert.That(startsWithLeadIn, Is.False);

            // Sound that is not faint is the start of the song, however short: it is kept, with silence back to its beat.
            WavExportResult loud = WavToSp16.Plan(Song(0.64, 8000, 0.7), "x", options, grid);
            Assert.That(loud.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(0.64 * Rate)).Within(2));
            Assert.That(loud.Chunks[0].PatternLength, Is.EqualTo(8));
            Assert.That(loud.Warnings, Has.Some.Contains("Padding 440 ms"));

            // And so is faint sound that lasts a quarter of a beat or more, which could be a quiet note.
            WavExportResult longer = WavToSp16.Plan(Song(0.5, 150, 0.7), "x", options, grid);
            Assert.That(longer.Chunks[0].StartFrame, Is.EqualTo((int)Math.Round(0.5 * Rate)).Within(2));
            Assert.That(longer.Chunks[0].PatternLength, Is.EqualTo(8));

            // With silence in front kept, the file's start is the song's, as it was.
            WavExportResult kept = WavToSp16.Plan(faint, "x", new WavExportOptions { KeepLeadIn = true }, grid);
            Assert.That(kept.Chunks[0].StartFrame, Is.EqualTo(0));
        }

        [Test]
        public void BeatGrid_AFileWithOnlyFaintSoundBeforeItsFirstHitIsCutOnTheBeat()
        {
            // Kicks from 0.3 s at 120 BPM, with the part that marks the bar coming in on the second
            // of them. A file cut on its first hit starts on beat 1, whatever that part suggests.
            WavData With(double level)
            {
                WavData wav = Synthesize(120, 0.3, 1, 40);
                int peak = 1;
                foreach (short sample in wav.Samples)
                    peak = Math.Max(peak, Math.Abs((int)sample));
                // Something in front of the first kick for 60 ms, as a stem from a separator has.
                var random = new Random(3);
                for (int frame = (int)(0.24 * Rate); frame < (int)(0.29 * Rate); frame++)
                    wav.Samples[2 * frame] = wav.Samples[2 * frame + 1] = (short)(level * peak * (random.NextDouble() * 2 - 1));
                return wav;
            }
            WavData faint = With(0.01);
            BeatGrid grid = BeatGridAnalyzer.Analyze(faint.Samples, Rate);
            Assert.That(grid.FirstSteadyBeatSeconds, Is.EqualTo(0.3).Within(0.003));
            Assert.That(grid.DownbeatFromDrums, Is.True);
            Assert.That(grid.FirstDownbeatSeconds, Is.EqualTo(0.3).Within(0.003));
            // So the first pattern starts on the first kick, with no lead-in.
            var options = new WavExportOptions { KeepLeadIn = true, TrimLeadingSilence = true, PatternAnchorSeconds = grid.FirstSteadyBeatSeconds };
            WavExportResult plan = WavToSp16.Plan(faint, "x", options, grid);
            Assert.That(plan.Chunks[0].IsLeadIn, Is.False);
            Assert.That((double)plan.Chunks[0].StartFrame / Rate, Is.EqualTo(0.3).Within(0.003));

            // Sound at a quarter of full level before the kick is part of the song, and the file is not cut on the beat.
            WavData loud = With(0.25);
            Assert.That(BeatGridAnalyzer.Analyze(loud.Samples, Rate).DownbeatFromDrums, Is.False);
        }

        [Test]
        public void S8_SampleFilesCarryTheSongsTempoAndTheirLengthInBars()
        {
            // 20 s at 128 BPM with the first bar line at 0.25 s: a lead-in of a beat, two patterns
            // of four bars and what is left, 4.75 s, which is two and a half bars to the step.
            WavData wav = Synthesize(128, 0.25, 0, 20);
            var grid = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.25, DownbeatIndex = 0 };
            WavExportResult plan = WavToSp16.Plan(wav, "Tempo", new WavExportOptions { KeepLeadIn = true }, grid);
            string path = Path.Combine(Path.GetTempPath(), "strands_tempo_test.tpkg");
            List<StoredZipReader.Entry> entries;
            try
            {
                WavToSp16.WritePackage(wav, Fixtures.Read("arr0.prj"), path, plan);
                entries = StoredZipReader.Read(File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
            Assert.That(plan.Chunks.Count, Is.EqualTo(4));
            var bars = new List<double>();
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                SampleAdjunct adjunct = SampleAdjunct.Read(entries[2 + 2 * i].Data);
                // With no tempo the unit's Time Stretch and Resmpl run at the wrong speed (Reported, user).
                Assert.That(adjunct.Bpm, Is.EqualTo(plan.Grid.Bpm));
                Assert.That(adjunct.SampleNum, Is.EqualTo(plan.Chunks[i].SampleFrames));
                // The length the sample has in the song, to the step, without the tail it fades out over.
                double seconds = (double)(plan.Chunks[i].EndFrame - plan.Chunks[i].StartFrame + plan.Chunks[i].LeadingSilenceFrames) / Rate;
                Assert.That(adjunct.Bar, Is.EqualTo(seconds * plan.Grid.Bpm / 240).Within(1.0 / 32));
                Assert.That(adjunct.Bar * 16, Is.EqualTo(Math.Round(adjunct.Bar * 16)).Within(1e-9));
                bars.Add(adjunct.Bar);
            }
            Assert.That(plan.Chunks[0].IsLeadIn, Is.True);
            Assert.That(bars[0], Is.EqualTo(0.25));
            Assert.That(bars[1], Is.EqualTo(4));
            Assert.That(bars[2], Is.EqualTo(4));
            // The loader's values still come out of the plain form, which the probes use.
            Assert.That(SampleAdjunct.ForSample(100).Bar, Is.EqualTo(2.0));
            Assert.That(SampleAdjunct.ForSample(100).Bpm, Is.EqualTo(0.0));
        }

        [Test]
        public void BlockLevels_FindTheLoudestSampleOfAnyStretchAndSayWhenItIsSilence()
        {
            // Ten seconds, quiet with one loud second in the middle, and a stretch of nothing.
            var samples = new short[2 * 10 * Rate];
            var random = new Random(7);
            for (int frame = 0; frame < samples.Length / 2; frame++)
            {
                double seconds = (double)frame / Rate;
                int level = seconds >= 4 && seconds < 5 ? 20000 : seconds >= 7 && seconds < 9 ? 0 : seconds < 2 ? 12 : 900;
                samples[2 * frame] = (short)random.Next(-level, level + 1);
                samples[2 * frame + 1] = (short)random.Next(-level, level + 1);
            }
            var levels = new BlockLevels(samples);
            for (int trial = 0; trial < 200; trial++)
            {
                int from = random.Next(0, samples.Length / 2), to = random.Next(from, Math.Min(samples.Length / 2, from + 3 * Rate) + 1);
                int loudest = 0;
                for (int i = 2 * from; i < 2 * to; i++)
                    loudest = Math.Max(loudest, Math.Abs((int)samples[i]));
                Assert.That(levels.LoudestBetween(from, to), Is.EqualTo(loudest), from + " to " + to);
            }
            Assert.That(levels.Peak, Is.GreaterThan(19000));
            // Nothing at all is silence, and so is what is 54 dB or more under the peak, as at the front of a file.
            Assert.That(levels.IsSilent(7 * Rate, 9 * Rate), Is.True);
            Assert.That(levels.IsSilent(0, 2 * Rate), Is.True);
            Assert.That(levels.IsSilent(0, 2 * Rate + 1000), Is.False);
            Assert.That(levels.IsSilent(5 * Rate, 7 * Rate), Is.False);
            Assert.That(levels.IsSilent(8 * Rate, 9 * Rate + 2), Is.False);
            Assert.That(levels.LoudestBetween(-5, 0), Is.EqualTo(0));
            Assert.That(levels.LoudestBetween(3, 3), Is.EqualTo(0));
        }

        [Test]
        public void BeatGrid_NamesTheBeatOfTheBarAMomentFallsOn()
        {
            // 120 BPM with the first beat at 0.5 s and the bar on the third beat of the file: bar lines at 1.5, 3.5 and so on.
            var grid = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0.5, DownbeatIndex = 2 };
            bool on;
            // One beat before a bar line is beat 4, which is where a lead-in of one beat starts.
            Assert.That(grid.BeatOfBarAt(1.0, out on), Is.EqualTo(4));
            Assert.That(on, Is.True);
            Assert.That(grid.BeatOfBarAt(1.5, out on), Is.EqualTo(1));
            Assert.That(on, Is.True);
            Assert.That(grid.BeatOfBarAt(3.0, out on), Is.EqualTo(4));
            // The start of the file is three beats before the bar line: beat 2.
            Assert.That(grid.BeatOfBarAt(0.0, out on), Is.EqualTo(2));
            Assert.That(on, Is.True);
            // A little late or a touch early is still on the beat.
            Assert.That(grid.BeatOfBarAt(1.1, out on), Is.EqualTo(4));
            Assert.That(on, Is.True);
            Assert.That(grid.BeatOfBarAt(0.96, out on), Is.EqualTo(4));
            Assert.That(on, Is.True);
            Assert.That(grid.BeatOfBarAt(1.47, out on), Is.EqualTo(1));
            Assert.That(on, Is.True);
            // Half-way between beats 3 and 4 is on neither, and is named by the beat it follows.
            Assert.That(grid.BeatOfBarAt(0.75, out on), Is.EqualTo(3));
            Assert.That(on, Is.False);
            Assert.That(grid.BeatOfBarAt(1.25, out on), Is.EqualTo(4));
            Assert.That(on, Is.False);
        }

        [Test]
        public void DriftMeter_SettlesALinePutNearTheHitsAndLeavesOneFurtherOutAlone()
        {
            WavData wav = Synthesize(128, 0.3, 0, 60);
            BeatGrid analysed = BeatGridAnalyzer.Analyze(wav.Samples, Rate);
            var meter = new BeatDriftMeter(wav.Samples, Rate);

            // A line put down by hand is a few milliseconds off, early or late: it is brought on to the hits.
            foreach (double ms in new[] { -12.0, -4.0, 3.0, 9.0, 15.0 })
            {
                BeatGrid grid = analysed.Clone();
                grid.Shift(ms / 1000);
                double moved = meter.Settle(grid, 20);
                Assert.That(1000 * moved, Is.EqualTo(-ms).Within(0.5), ms + " ms out");
                Assert.That(grid.FirstBeatSeconds, Is.EqualTo(analysed.FirstBeatSeconds).Within(0.0005));
                Assert.That(meter.OverallOffsetMs(grid), Is.EqualTo(0).Within(0.5));
            }

            // On the hits already, nothing moves.
            BeatGrid onTheHits = analysed.Clone();
            Assert.That(1000 * meter.Settle(onTheHits, 20), Is.EqualTo(0).Within(0.3));

            // Further out than the reach it is not for this to say where the line belongs: it stays where it was put.
            BeatGrid far = analysed.Clone();
            far.Shift(0.060);
            Assert.That(meter.Settle(far, 20), Is.EqualTo(0));
            Assert.That(far.FirstBeatSeconds, Is.EqualTo(analysed.FirstBeatSeconds + 0.060).Within(1e-9));

            // Sound with no beat in it gives nothing to settle on.
            var tone = new short[2 * 10 * Rate];
            for (int i = 0; i < tone.Length / 2; i++)
                tone[2 * i] = tone[2 * i + 1] = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / Rate));
            BeatGrid untouched = analysed.Clone();
            Assert.That(new BeatDriftMeter(tone, Rate).Settle(untouched, 20), Is.EqualTo(0));
            Assert.That(untouched.FirstBeatSeconds, Is.EqualTo(analysed.FirstBeatSeconds));
        }

        [Test]
        public void DriftMeter_ReadsZeroOnAnAnalysedGridAndFollowsANudge()
        {
            WavData wav = Synthesize(128, 0.3, 0, 60);
            BeatGrid grid = BeatGridAnalyzer.Analyze(wav.Samples, Rate);
            var meter = new BeatDriftMeter(wav.Samples, Rate);

            double[] onGrid = meter.Measure(grid, 0, 16, 6);
            foreach (double offset in onGrid)
                Assert.That(offset, Is.EqualTo(0).Within(0.5));

            // Moving the grid 5 ms later leaves the audio 5 ms early.
            grid.Shift(0.005);
            foreach (double offset in meter.Measure(grid, 0, 16, 6))
                Assert.That(offset, Is.EqualTo(-5).Within(0.5));

            // A tempo that is a touch slow puts later patterns progressively early.
            grid.Shift(-0.005);
            grid.SetTempo(127.95, grid.FirstDownbeatSeconds);
            double[] drifting = meter.Measure(grid, 0, 16, 6);
            Assert.That(drifting[5], Is.LessThan(drifting[0] - 5));
        }

        sealed class ImmediateProgress : IProgress<float>
        {
            readonly Action<float> report;
            public ImmediateProgress(Action<float> report) { this.report = report; }
            public void Report(float value) { report(value); }
        }

        [Test]
        public void WaveformPeaks_MatchTheSamplesAtAnyZoom()
        {
            var samples = new short[2 * 2000];
            samples[2 * 300] = samples[2 * 300 + 1] = 16384;       // +0.5 in block 1
            samples[2 * 1500] = samples[2 * 1500 + 1] = -8192;     // -0.25 in block 5
            var peaks = new WaveformPeaks(samples);
            float min, max;

            peaks.Range(0, 2000, out min, out max);
            Assert.That(new[] { min, max }, Is.EqualTo(new[] { -0.25f, 0.5f }));
            peaks.Range(290, 310, out min, out max);
            Assert.That(new[] { min, max }, Is.EqualTo(new[] { 0f, 0.5f }));
            peaks.Range(301, 1500, out min, out max);
            Assert.That(new[] { min, max }, Is.EqualTo(new[] { 0f, 0f }));
            peaks.Range(301, 1501, out min, out max);
            Assert.That(new[] { min, max }, Is.EqualTo(new[] { -0.25f, 0f }));
            peaks.Range(-50, -10, out min, out max);
            Assert.That(new[] { min, max }, Is.EqualTo(new[] { 0f, 0f }));
            peaks.Range(5000, 6000, out min, out max);
            Assert.That(new[] { min, max }, Is.EqualTo(new[] { 0f, 0f }));
        }
    }
}
