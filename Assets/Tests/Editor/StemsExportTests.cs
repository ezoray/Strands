using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Strands.Audio;
using Strands.Export;
using Strands.Sp16;

namespace Strands.Tests
{
    /// <summary>
    /// Several stems to one package: every chunk has a pad for each layer, its pattern starts them
    /// together, and each layer plays from its own output. Tests of what goes into the project
    /// carry the section of the format spec they check.
    /// </summary>
    public class StemsExportTests
    {
        const int Rate = 44100;

        static readonly string[] Pairs = { "AssignTo12", "AssignTo34", "AssignTo56", "AssignTo78" };

        /// <summary>A steady tone, with the right channel at half the level of the left so the two can be told apart.</summary>
        static WavData Tone(int frames, double hertz, int level)
        {
            var samples = new short[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                double value = level * Math.Sin(2 * Math.PI * hertz * i / Rate);
                samples[2 * i] = (short)Math.Round(value);
                samples[2 * i + 1] = (short)Math.Round(value / 2);
            }
            return new WavData { SampleRate = Rate, Samples = samples, SourceChannels = 2, SourceBitsPerSample = 16 };
        }

        /// <summary>
        /// The kick pattern at 128 BPM from 0.25 s, and three tones, on the four outputs in order.
        /// The tones are kept above the bass: the same three at 110, 220 and 330 Hz fill the band the
        /// analyser finds the kick in, and it then puts the beat of the stems added together on the
        /// off-beat tick. The drums alone are still read correctly, which is what choosing the grid
        /// from the best-fitting stem is for.
        /// </summary>
        static List<Stem> FourStems(double seconds)
        {
            WavData drums = AudioAndExportTests.Synthesize(128, 0.25, 0, seconds);
            var stems = new List<Stem> { new Stem { Name = "Drums", Audio = drums, Output = StemsToSp16.DefaultOutput(0) } };
            string[] names = { "Bass", "Other", "Vocals" };
            for (int i = 0; i < names.Length; i++)
                stems.Add(new Stem { Name = names[i], Audio = Tone(drums.Frames, 440 * (i + 1), 2000 + 1500 * i), Output = StemsToSp16.DefaultOutput(i + 1) });
            return stems;
        }

        /// <summary>Every stem on the master output, each in a group of its own, which is how the app has four strips of one file.</summary>
        static List<Stem> OnMaster(List<Stem> stems)
        {
            for (int i = 0; i < stems.Count; i++)
            {
                stems[i].Output = Sp16Output.Master;
                stems[i].Group = i;
            }
            return stems;
        }

        static BeatGrid Grid128()
        {
            return new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.25, DownbeatIndex = 0 };
        }

        static List<ExportLayer> Layers(int count)
        {
            var layers = new List<ExportLayer>();
            for (int i = 0; i < count; i++)
                layers.Add(new ExportLayer { Label = "S" + (i + 1), Output = StemsToSp16.DefaultOutput(i) });
            return layers;
        }

        static WavData Silence(double seconds)
        {
            return new WavData { SampleRate = Rate, Samples = new short[(int)(seconds * Rate) * 2], SourceChannels = 2, SourceBitsPerSample = 16 };
        }

        static List<StoredZipReader.Entry> Write(StemSet stems, WavExportResult plan)
        {
            string path = Path.Combine(Path.GetTempPath(), "strands_stems_test.tpkg");
            stems.WritePackage(Fixtures.Read("arr0.prj"), path, plan);
            try
            {
                return StoredZipReader.Read(File.ReadAllBytes(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void Plan_WithLayers_AChunkTakesAPadForEachAndASceneHoldsFewerChunks()
        {
            // 40 s at 128 BPM from 0.25 s is 21.2 bars: five full 4-bar chunks and a 2-bar remainder.
            WavData wav = Silence(40);
            WavExportResult single = WavToSp16.Plan(wav, "Song", new WavExportOptions(), Grid128());
            WavExportResult four = WavToSp16.Plan(wav, "Song", new WavExportOptions(), Grid128(), Layers(4));

            Assert.That(four.Chunks.Count, Is.EqualTo(6));
            for (int i = 0; i < four.Chunks.Count; i++)
            {
                // The audio is cut exactly where a full mix is cut.
                Assert.That(four.Chunks[i].StartFrame, Is.EqualTo(single.Chunks[i].StartFrame));
                Assert.That(four.Chunks[i].SampleFrames, Is.EqualTo(single.Chunks[i].SampleFrames));
                Assert.That(four.Chunks[i].PatternLength, Is.EqualTo(single.Chunks[i].PatternLength));
                Assert.That(four.Chunks[i].Scene, Is.EqualTo(i / 4));
                Assert.That(four.Chunks[i].Pad, Is.EqualTo((3 - i % 4) * 4), "the first pattern of a scene is on the top row");
                Assert.That(four.Chunks[i].Pattern, Is.EqualTo(i % 4));
                Assert.That(four.Chunks[i].FileNames.Length, Is.EqualTo(4));
            }
            Assert.That(four.SampleBytes, Is.EqualTo(4 * single.SampleBytes));
            Assert.That(single.Chunks[5].Scene, Is.EqualTo(0));
            Assert.That(single.Chunks[5].Pad, Is.EqualTo(9));
            Assert.That(single.Chunks[5].FileNames, Is.EqualTo(new[] { "Song_06.wav" }));

            // Three layers leave the sixteenth pad over: five chunks to a scene.
            WavExportResult three = WavToSp16.Plan(wav, "Song", new WavExportOptions(), Grid128(), Layers(3));
            Assert.That(three.Chunks[4].Scene, Is.EqualTo(0));
            Assert.That(three.Chunks[4].Pad, Is.EqualTo(0));
            Assert.That(three.Chunks[5].Scene, Is.EqualTo(1));
            Assert.That(three.Chunks[5].Pad, Is.EqualTo(12));

            // Sixteen fill a scene with one chunk.
            WavExportResult sixteen = WavToSp16.Plan(wav, "Song", new WavExportOptions(), Grid128(), Layers(16));
            Assert.That(sixteen.Chunks[1].Scene, Is.EqualTo(1));
            Assert.That(sixteen.Chunks[1].Pad, Is.EqualTo(12));
            Assert.That(sixteen.Chunks[1].Pattern, Is.EqualTo(0));
            Assert.Throws<ArgumentException>(() => WavToSp16.Plan(wav, "Song", new WavExportOptions(), Grid128(), Layers(17)));
        }

        [Test]
        public void Plan_WithLayers_TooLargeNamesOnlyWhatWouldHelp()
        {
            // 480 s in 4-bar chunks at 60 BPM is 30 chunks, which eight layers have room for (32),
            // but eight of them need more memory than there is, even in mono.
            WavData wav = Silence(480);
            var slow = new BeatGrid { Bpm = 60, FirstBeatSeconds = 0, DownbeatIndex = 0 };
            Func<Func<int, ExportLayer>, string> refusal = make =>
            {
                var layers = new List<ExportLayer>();
                for (int i = 0; i < 8; i++)
                    layers.Add(make(i));
                return Assert.Throws<PlanRefusedException>(() => WavToSp16.Plan(wav, "x", new WavExportOptions(), slow, layers)).Message;
            };

            // Each on a stereo output of its own: nothing to sum, but mono would help.
            Assert.That(refusal(i => new ExportLayer { Label = "S" + i, Output = (Sp16Output)(i % 4), Group = i }),
                Does.EndWith("too large to fit. Use mono or remove tracks."));
            // Already mono, each on its own output.
            Assert.That(refusal(i => new ExportLayer { Label = "S" + i, Output = Sp16Output.Out1 + i, Group = i }),
                Does.EndWith("too large to fit. Remove tracks."));
            // On master, where there is no mono, one strip each.
            Assert.That(refusal(i => new ExportLayer { Label = "S" + i, Output = Sp16Output.Master, Group = i }),
                Does.EndWith("too large to fit. Remove tracks."));
            // Two to a strip on master, kept apart: those could be summed.
            Assert.That(refusal(i => new ExportLayer { Label = "S" + i, Output = Sp16Output.Master, Group = i / 2 }),
                Does.EndWith("too large to fit. Sum stems or remove tracks."));
        }

        [Test]
        public void Plan_WithLayers_RefusesASongThatNeedsMoreScenesOrMemoryThanThereIs()
        {
            WavData wav = Silence(100);

            // 1-bar chunks at 174 BPM: 100 s is 73 of them. A full mix has room for 256, four layers for 64.
            var fast = new BeatGrid { Bpm = 174, FirstBeatSeconds = 0, DownbeatIndex = 0 };
            var oneBar = new WavExportOptions { BarsPerChunk = 1 };
            Assert.That(WavToSp16.Plan(wav, "x", oneBar, fast).Chunks.Count, Is.EqualTo(73));
            var tooMany = Assert.Throws<PlanRefusedException>(() => WavToSp16.Plan(wav, "x", oneBar, fast, Layers(4)));
            Assert.That(tooMany.Message, Is.EqualTo("Too large to fit with 4 tracks. Fits with 3 or fewer. Remove tracks."));
            Assert.That(tooMany.PatternsNeeded, Is.EqualTo(73));
            Assert.That(tooMany.ForPatterns, Is.True);
            WavExportResult three = WavToSp16.Plan(wav, "x", oneBar, fast, Layers(3));
            Assert.That(three.Chunks.Count, Is.EqualTo(73));
            Assert.That(three.PatternsNeeded, Is.EqualTo(73));

            // 4-bar chunks at 120 BPM: 13 of them, one to a scene with sixteen layers, but 269 MB of samples.
            var slow = new BeatGrid { Bpm = 120, FirstBeatSeconds = 0, DownbeatIndex = 0 };
            WavExportResult eight = WavToSp16.Plan(wav, "x", new WavExportOptions(), slow, Layers(8));
            Assert.That(eight.SampleBytes, Is.EqualTo(8L * wav.Samples.Length * 2).Within(8L * 13 * 4 * 300));
            // Each layer's share is counted the same way, so the shares add up to the whole.
            long shares = 0;
            for (int layer = 0; layer < eight.Layers.Count; layer++)
                shares += eight.BytesOfLayer(layer);
            Assert.That(shares, Is.EqualTo(eight.SampleBytes));
            Assert.That(eight.BytesOfLayer(0), Is.EqualTo(eight.SampleBytes / 8));
            var tooBig = Assert.Throws<PlanRefusedException>(() => WavToSp16.Plan(wav, "x", new WavExportOptions(), slow, Layers(16)));
            Assert.That(tooBig.ForPatterns, Is.False);
            Assert.That(tooBig.PatternsNeeded, Is.EqualTo(13));
            // The message counts as the unit does: 256 MB of memory, not 268.
            Assert.That(tooBig.Message, Does.StartWith("Calculated size of ").And.EndWith("MB is too large to fit. Sum stems, use mono, or remove tracks."));
        }

        [Test]
        public void Plan_WithLayers_SampleNamesAreShortAndNoTwoAreTheSame()
        {
            var layers = new List<ExportLayer>
            {
                new ExportLayer { Label = "Drums" },
                new ExportLayer { Label = "drums" },        // the same name to a file system that ignores case
                new ExportLayer { Label = "Lead Vocals" },
                new ExportLayer { Label = "" },
                new ExportLayer { Label = null },
                new ExportLayer { Label = "Lead Vox" }      // the same first six characters as Lead Vocals
            };
            WavExportResult plan = WavToSp16.Plan(Silence(40), "Recording_2000-01-01", new WavExportOptions(), Grid128(), layers);

            Assert.That(plan.Chunks[0].FileNames, Is.EqualTo(new[]
            {
                "Drums_Recor_001.wav", "drums2_Recor_001.wav", "Lead_V_Recor_001.wav", "L_Recor_001.wav", "L2_Recor_001.wav", "Lead_2_Recor_001.wav"
            }));
            Assert.That(plan.Chunks[5].FileNames[0], Is.EqualTo("Drums_Recor_006.wav"));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChunkInfo chunk in plan.Chunks)
                foreach (string name in chunk.FileNames)
                {
                    Assert.That(seen.Add(name), name + " is used twice");
                    // No longer than a full mix's names: twelve characters of the project, an underscore and three digits.
                    Assert.That(name.Length, Is.LessThanOrEqualTo(16 + ".wav".Length));
                }

            Assert.That(WavToSp16.Plan(Silence(40), "Recording_2000-01-01", new WavExportOptions(), Grid128()).Chunks[0].FileName, Is.EqualTo("Recording_20_01.wav"));
        }

        [Test]
        public void Plan_FullMix_SamplesAreNumberedInTwoFiguresUpTo99()
        {
            // At 128 BPM a 4-bar chunk is 7.5 s: 99 chunks, and then 100.
            WavExportResult few = WavToSp16.Plan(Silence(99 * 7.5), "Song", new WavExportOptions(), Grid128());
            Assert.That(few.Chunks.Count, Is.EqualTo(99));
            Assert.That(few.Chunks[0].FileName, Is.EqualTo("Song_01.wav"));
            Assert.That(few.Chunks[98].FileName, Is.EqualTo("Song_99.wav"));

            WavExportResult many = WavToSp16.Plan(Silence(100 * 7.5), "Song", new WavExportOptions(), Grid128());
            Assert.That(many.Chunks.Count, Is.EqualTo(100));
            Assert.That(many.Chunks[0].FileName, Is.EqualTo("Song_001.wav"));
            Assert.That(many.Chunks[99].FileName, Is.EqualTo("Song_100.wav"));
        }

        [Test]
        public void S7_Stems_UsedScenesAre8ChWithEachLayerOnItsOutputAndTheRestAreLeftAlone()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(40));
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(plan.Chunks.Count, Is.EqualTo(6));
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
            PrjNode template = Fixtures.ReadProject("arr0.prj");
            PrjNode unit = Fixtures.ReadProject("route1.prj").Child("SceneData0").Child("RoutingData");

            // Six chunks of four pads are a full scene and half of the next.
            for (int scene = 0; scene < 2; scene++)
            {
                PrjNode routing = project.Scene(scene).Child("RoutingData");
                // The scene's own settings are the ones the unit saved when it was put in 8ch mode.
                Assert.That(routing.Scalars.Count, Is.EqualTo(unit.Scalars.Count));
                for (int i = 0; i < unit.Scalars.Count; i++)
                {
                    Assert.That(routing.Scalars[i].Name, Is.EqualTo(unit.Scalars[i].Name));
                    Assert.That(routing.Scalars[i].Raw, Is.EqualTo(unit.Scalars[i].Raw), unit.Scalars[i].Name);
                }
                Assert.That(routing.Scalar("assignMode").AsString(), Is.EqualTo("4Assign"));
                Assert.That(routing.Scalar("headphoneOutCh").AsString(), Is.EqualTo("Cue12"));
                // All sixteen pads carry an output, in use or not, column by column.
                for (int pad = 0; pad < 16; pad++)
                    Assert.That(routing.Child("TrackRoutingData" + pad).Scalar("assignableOutCh").AsString(), Is.EqualTo(Pairs[pad % 4]), "scene " + scene + " pad " + pad);
                // The first chunk's four pads are the unit's own file, where pads 1 to 4 were sent to the four pairs.
                for (int pad = 0; pad < 4; pad++)
                    FormatTests.AssertSameTree(unit.Child("TrackRoutingData" + pad), routing.Child("TrackRoutingData" + pad), "TrackRoutingData" + pad);

                var differences = new List<string>();
                FormatTests.CollectDifferences(template.Child("SceneData" + scene).Child("RoutingData"), routing, "", differences);
                foreach (string difference in differences)
                    Assert.That(difference, Is.EqualTo(".assignMode").Or.EqualTo(".headphoneOutCh").Or.EndsWith(".assignableOutCh"));
            }
            for (int scene = 2; scene < 16; scene++)
                FormatTests.AssertSameTree(template.Child("SceneData" + scene), project.Scene(scene), "SceneData" + scene);
        }

        [Test]
        public void S7_Stems_OnMasterLeaveTheRoutingOfEverySceneAsTheTemplateHasIt()
        {
            StemSet master = StemsToSp16.Prepare(OnMaster(FourStems(40)));
            // Four tracks of their own, though they share the one output.
            Assert.That(master.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Drums", "Bass", "Other", "Vocals" }));
            Assert.That(master.Layers.ConvertAll(layer => layer.Output), Is.All.EqualTo(Sp16Output.Master));
            Assert.That(master.Layers.ConvertAll(layer => layer.Group), Is.EqualTo(new[] { 0, 1, 2, 3 }));
            Assert.That(master.Layers.ConvertAll(layer => layer.IsMono), Is.All.False);

            WavExportResult plan = master.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(plan.Chunks.Count, Is.EqualTo(6));
            Assert.That(plan.Describe(), Does.Contain("Drums on master, Bass on master, Other on master, Vocals on master"));
            List<StoredZipReader.Entry> written = Write(master, plan);
            Sp16Project project = Sp16Project.FromPrj(written[0].Data);

            // No scene's routing is touched, in use or not: every pad is where the unit's own empty project has it.
            PrjNode template = Fixtures.ReadProject("arr0.prj");
            for (int scene = 0; scene < 16; scene++)
                FormatTests.AssertSameTree(template.Child("SceneData" + scene).Child("RoutingData"), project.Scene(scene).Child("RoutingData"), "SceneData" + scene + "/RoutingData");
            Assert.That(project.Scene(0).Child("RoutingData").Scalar("assignMode").AsString(), Is.EqualTo("Master"));
            Assert.That(project.Scene(0).Child("RoutingData").Child("TrackRoutingData3").Scalar("assignableOutCh").AsString(), Is.EqualTo("AssignToNull"));

            // Each pattern still starts its four pads together.
            foreach (ChunkInfo chunk in plan.Chunks)
            {
                List<KeyValuePair<int, int>> triggers = StepsBlob.FromScalar(project.FindPattern(chunk.Scene, chunk.Pattern).Scalar("Steps")).GetTriggers();
                Assert.That(triggers.Count, Is.EqualTo(4));
                for (int layer = 0; layer < 4; layer++)
                {
                    Assert.That(triggers[layer].Key, Is.EqualTo(chunk.PadOf(layer)));
                    Assert.That(triggers[layer].Value, Is.EqualTo(0));
                }
            }

            // Everything else is the package the same stems make on the four pairs: the same
            // samples under the same names, and a project that differs in its routing and nowhere else.
            StemSet pairs = StemsToSp16.Prepare(FourStems(40));
            List<StoredZipReader.Entry> onPairs = Write(pairs, pairs.Plan("Stems", new WavExportOptions(), Grid128()));
            Assert.That(written.Count, Is.EqualTo(onPairs.Count));
            for (int i = 1; i < written.Count; i++)
            {
                Assert.That(written[i].Name, Is.EqualTo(onPairs[i].Name));
                Assert.That(written[i].Data, Is.EqualTo(onPairs[i].Data), written[i].Name);
            }
            var differences = new List<string>();
            FormatTests.CollectDifferences(Sp16Project.FromPrj(onPairs[0].Data).Root, project.Root, "", differences);
            // Two scenes in use, each with its mode, its cue and the sixteen pads.
            Assert.That(differences.Count, Is.EqualTo(2 * 18));
            foreach (string difference in differences)
                Assert.That(difference, Does.Contain("/RoutingData"), difference);
        }

        [Test]
        public void S7_Stems_OnMasterAndOnOtherOutputsAreRefusedInOnePackage()
        {
            // The mode the unit starts in could send some pads to master and others to 3-4, 5-6 or
            // 7-8, but nothing generated has tried it on the unit.
            List<ExportLayer> layers = Layers(3);
            layers[1].Output = Sp16Output.Master;
            var refused = Assert.Throws<ArgumentException>(() => WavToSp16.Plan(Silence(40), "Song", new WavExportOptions(), Grid128(), layers));
            Assert.That(refused.Message, Does.Contain("master"));

            foreach (ExportLayer layer in layers)
                layer.Output = Sp16Output.Master;
            Assert.That(WavToSp16.Plan(Silence(40), "Song", new WavExportOptions(), Grid128(), layers).Layers.Count, Is.EqualTo(3));
        }

        [Test]
        public void Prepare_StemsOnMasterAreMixedGroupByGroupAndNotAllTogether()
        {
            WavData tone = Tone(Rate, 440, 3000);
            var source = new List<Stem>
            {
                new Stem { Name = "Kick", Audio = Tone(Rate, 60, 4000), Output = Sp16Output.Master, Group = 0 },
                new Stem { Name = "Bass", Audio = tone, Output = Sp16Output.Master, Group = 1 },
                new Stem { Name = "Snare", Audio = Tone(Rate, 200, 2000), Output = Sp16Output.Master, Group = 0 },
                new Stem { Name = "Pad", Audio = tone, Output = Sp16Output.Master, Group = 3 },
                new Stem { Name = "Lead", Audio = tone, Output = Sp16Output.Master, Group = 3 }
            };
            StemSet set = StemsToSp16.Prepare(source);
            // A mix on master is numbered by its group, since its output says nothing.
            Assert.That(set.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Mix1", "Bass", "Mix4" }));
            Assert.That(set.Layers.ConvertAll(layer => layer.Group), Is.EqualTo(new[] { 0, 1, 3 }));
            Assert.That(set.Layers.ConvertAll(layer => layer.Output), Is.All.EqualTo(Sp16Output.Master));
            for (int k = 0; k < 2000; k++)
                Assert.That(set.LayerSamples[0][k], Is.EqualTo(source[0].Audio.Samples[k] + source[2].Audio.Samples[k]));
            Assert.That(set.LayerSamples[1], Is.SameAs(source[1].Audio.Samples));

            // Kept apart, the stems of a group are tracks of their own where they stand.
            source[3].KeepApart = source[4].KeepApart = true;
            Assert.That(StemsToSp16.Prepare(source).Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Mix1", "Bass", "Pad", "Lead" }));

            // Left in one group, which is how the command line has them, stems on master are one
            // sample between them, as stems on any one output are.
            foreach (Stem stem in source)
            {
                stem.Group = 0;
                stem.KeepApart = false;
            }
            StemSet together = StemsToSp16.Prepare(source);
            Assert.That(together.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Mix1" }));
            Assert.That(StemsToSp16.Prepare(source, false).Layers.Count, Is.EqualTo(5));

            // A group only keeps stems apart that would otherwise be mixed: on different outputs they never are.
            var pairs = new List<Stem>
            {
                new Stem { Name = "A", Audio = tone, Output = Sp16Output.Out12, Group = 0 },
                new Stem { Name = "B", Audio = tone, Output = Sp16Output.Out12, Group = 1 },
                new Stem { Name = "C", Audio = tone, Output = Sp16Output.Out34, Group = 1 },
                new Stem { Name = "D", Audio = tone, Output = Sp16Output.Out34, Group = 1 }
            };
            Assert.That(StemsToSp16.Prepare(pairs).Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "A", "B", "Mix34" }));
        }

        [Test]
        public void S7_Stems_SingleOutputsAreWrittenAsTheUnitSavedThem()
        {
            // Eight stems kept apart, one on each output: two chunks to a scene.
            var source = new List<Stem>();
            WavData tone = Tone(20 * Rate, 440, 3000);
            for (int i = 0; i < 8; i++)
                source.Add(new Stem { Name = "S" + (i + 1), Audio = tone, Output = Sp16Output.Out1 + i });
            StemSet stems = StemsToSp16.Prepare(source, false);
            WavExportResult plan = stems.Plan("Singles", new WavExportOptions(), Grid128());
            Assert.That(plan.Chunks[2].Scene, Is.EqualTo(1));
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);

            PrjNode unit = Fixtures.ReadProject("route3-mono-8ch.prj").Child("SceneData0").Child("RoutingData");
            PrjNode routing = project.Scene(0).Child("RoutingData");
            for (int i = 0; i < unit.Scalars.Count; i++)
                Assert.That(routing.Scalars[i].Raw, Is.EqualTo(unit.Scalars[i].Raw), unit.Scalars[i].Name);
            for (int pad = 0; pad < 16; pad++)
            {
                // A chunk's eight pads are two rows, read from the top left, and have the values the
                // unit gave its own tracks 1 to 8; the second chunk's repeat them.
                int place = Sp16Project.PadInReadingOrder(pad);
                Assert.That(routing.Child("TrackRoutingData" + pad).Scalar("assignableOutCh").AsString(), Is.EqualTo("AssignTo" + (place % 8 + 1)));
                Assert.That(routing.Child("TrackRoutingData" + pad).Scalar("assignableOutCh").Raw, Is.EqualTo(unit.Child("TrackRoutingData" + place % 8).Scalar("assignableOutCh").Raw));
            }
            Assert.That(plan.Describe(), Does.Contain("S1 on 1 (mono), S2 on 2 (mono)"));

            // Stems sharing one output are still added into one sample when that is asked for.
            source[1].Output = Sp16Output.Out1;
            StemSet mixed = StemsToSp16.Prepare(source);
            Assert.That(mixed.Layers.Count, Is.EqualTo(7));
            Assert.That(mixed.Layers[0].Label, Is.EqualTo("Mix1"));
            Assert.That(mixed.Layers[0].Output, Is.EqualTo(Sp16Output.Out1));

            // The layers come in the order the stems were given, whatever their outputs: here a
            // single output between two pairs, and a pair that is used out of turn.
            var ordered = new List<Stem>
            {
                new Stem { Name = "Drums", Audio = tone, Output = Sp16Output.Out56 },
                new Stem { Name = "Bass", Audio = tone, Output = Sp16Output.Out3 },
                new Stem { Name = "Other", Audio = tone, Output = Sp16Output.Out12 },
                new Stem { Name = "Perc", Audio = tone, Output = Sp16Output.Out56 }
            };
            StemSet inOrder = StemsToSp16.Prepare(ordered);
            Assert.That(inOrder.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Mix56", "Bass", "Other" }));
            Assert.That(inOrder.Layers.ConvertAll(layer => layer.IsMono), Is.EqualTo(new[] { false, true, false }));
        }

        [Test]
        public void S9_Stems_ALayerOnASingleOutputIsWrittenAsMonoFiles()
        {
            // Drums stay on a pair. The second stem goes to output 3 by itself; its right side is at
            // half the level of its left, so its mono sample must be three quarters of the left.
            List<Stem> source = FourStems(20);
            source.RemoveRange(2, 2);
            source[1].Output = Sp16Output.Out3;
            StemSet stems = StemsToSp16.Prepare(source);
            Assert.That(stems.Layers[1].IsMono);
            Assert.That(stems.Layers[0].IsMono, Is.False);
            var options = new WavExportOptions { KeepLeadIn = true, SliceOnBeats = true, PatternAnchorSeconds = 4.0 };
            WavExportResult plan = stems.Plan("Mono", options, Grid128());
            Assert.That(plan.Chunks[0].IsLeadIn);
            Assert.That(plan.Chunks[0].LeadingSilenceFrames, Is.GreaterThan(0));
            Assert.That(plan.Describe(), Does.Contain("Bass on 3 (mono)").And.Contain("Drums on 1-2,"));

            // A stereo sample is counted at four bytes a frame and a mono one at two.
            long frames = 0;
            foreach (ChunkInfo chunk in plan.Chunks)
                frames += chunk.SampleFrames;
            Assert.That(plan.SampleBytes, Is.EqualTo(frames * (4 + 2)));

            List<StoredZipReader.Entry> entries = Write(stems, plan);
            Sp16Project project = Sp16Project.FromPrj(entries[0].Data);
            PrjNode routing = project.Scene(0).Child("RoutingData");
            Assert.That(routing.Child("TrackRoutingData0").Scalar("assignableOutCh").AsString(), Is.EqualTo("AssignTo12"));
            Assert.That(routing.Child("TrackRoutingData1").Scalar("assignableOutCh").AsString(), Is.EqualTo("AssignTo3"));

            short[] stem = source[1].Audio.Samples;
            var rebuilt = new double[stem.Length / 2];
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                ChunkInfo chunk = plan.Chunks[i];
                byte[] drums = entries[1 + 2 * (i * 2)].Data, bass = entries[1 + 2 * (i * 2 + 1)].Data;
                Assert.That(BitConverter.ToUInt16(drums, 22), Is.EqualTo(2), "channels of the drums");
                Assert.That(BitConverter.ToUInt16(bass, 22), Is.EqualTo(1), "channels of the bass");
                Assert.That(bass.Length, Is.EqualTo(44 + 2 * chunk.SampleFrames));

                // Length, slices and the adjunct count frames, the same for mono as for stereo.
                WavData sample = WavReader.Read(bass);
                Assert.That(sample.Frames, Is.EqualTo(chunk.SampleFrames));
                Assert.That(SampleAdjunct.Read(entries[2 + 2 * (i * 2 + 1)].Data).SampleNum, Is.EqualTo(chunk.SampleFrames));
                Assert.That(project.Pad(chunk.Scene, chunk.PadOf(1)).Child("PlaybackData").Scalar("lengthSample").AsInt32(), Is.EqualTo(chunk.SampleFrames));
                Assert.That(project.GetSliceBoundaries(chunk.Scene, chunk.PadOf(1)), Is.EqualTo(project.GetSliceBoundaries(chunk.Scene, chunk.Pad)));

                for (int frame = chunk.LeadingSilenceFrames; frame < chunk.SampleFrames - chunk.TrailingSilenceFrames; frame++)
                    rebuilt[chunk.StartFrame + frame - chunk.LeadingSilenceFrames] += sample.Samples[2 * frame];
                for (int frame = chunk.SampleFrames - chunk.TrailingSilenceFrames; frame < chunk.SampleFrames; frame++)
                    Assert.That(sample.Samples[2 * frame], Is.EqualTo(0));
                if (chunk.IsLeadIn)
                    for (int frame = 0; frame < chunk.LeadingSilenceFrames; frame += 29)
                        Assert.That(sample.Samples[2 * frame], Is.EqualTo(0));
            }
            // Laid back over each other the mono chunks are the two sides of the stem, added and halved.
            for (int frame = 0; frame < rebuilt.Length; frame++)
                if (Math.Abs(rebuilt[frame] - (stem[2 * frame] + stem[2 * frame + 1]) / 2.0) > 1)
                    Assert.Fail("The mono samples differ from the stem at frame " + frame);

            // Audio that is the same on both sides comes through exactly.
            short[] same = { 100, 100, -7, -7, 32767, 32767, -32768, -32768 };
            Assert.That(WavToSp16.CutChunkMono(same, 0, 4, 0, 0), Is.EqualTo(new short[] { 100, -7, 32767, -32768 }));
        }

        [Test]
        public void S7_FullMix_EverySceneStaysOnTheMasterOutput()
        {
            WavData wav = AudioAndExportTests.Synthesize(128, 0.25, 0, 20);
            byte[] package;
            using (var output = new MemoryStream())
            {
                WavToSp16.Export(wav, Fixtures.Read("arr0.prj"), output, "Mix", new WavExportOptions());
                package = output.ToArray();
            }
            Sp16Project project = Sp16Project.FromPrj(StoredZipReader.Read(package)[0].Data);
            PrjNode template = Fixtures.ReadProject("arr0.prj");
            for (int scene = 0; scene < 16; scene++)
            {
                Assert.That(project.Scene(scene).Child("RoutingData").Scalar("assignMode").AsString(), Is.EqualTo("Master"));
                FormatTests.AssertSameTree(template.Child("SceneData" + scene).Child("RoutingData"), project.Scene(scene).Child("RoutingData"), "RoutingData");
            }
        }

        [Test]
        public void S4_PadsFillASceneFromTheTopRowDown()
        {
            // The unit numbers its tracks from the bottom row up, so the top row is pads 13 to 16.
            Assert.That(Sp16Project.PadInReadingOrder(0), Is.EqualTo(12));
            Assert.That(Sp16Project.PadInReadingOrder(3), Is.EqualTo(15));
            Assert.That(Sp16Project.PadInReadingOrder(4), Is.EqualTo(8));
            Assert.That(Sp16Project.PadInReadingOrder(15), Is.EqualTo(3));
            for (int place = 0; place < 16; place++)
                Assert.That(Sp16Project.PadInReadingOrder(Sp16Project.PadInReadingOrder(place)), Is.EqualTo(place));

            // Four stems: a row to a pattern, the first on top, and a column to a stem with its output.
            List<Stem> source = FourStems(40);
            source[1].Output = Sp16Output.Out3;
            StemSet stems = StemsToSp16.Prepare(source);
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
            for (int layer = 0; layer < 4; layer++)
            {
                Assert.That(plan.Chunks[0].PadOf(layer), Is.EqualTo(12 + layer));
                Assert.That(plan.Chunks[1].PadOf(layer), Is.EqualTo(8 + layer));
                Assert.That(plan.Chunks[3].PadOf(layer), Is.EqualTo(layer));
                for (int row = 0; row < 4; row++)
                    Assert.That(project.Scene(0).Child("RoutingData").Child("TrackRoutingData" + (row * 4 + layer)).Scalar("assignableOutCh").AsString(),
                        Is.EqualTo(Sp16Project.OutputValue(plan.Layers[layer].Output)));
            }
        }

        [Test]
        public void S4_Stems_APadIsInItsLayersChokeGroupAndLoopsWhenSetTo()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(40));
            // Loops are only written with the arrangement off.
            var options = new WavExportOptions { Arrangement = false };
            for (int layer = 0; layer < 4; layer++)
                options.Pads.SetChokeOfLayer(layer, true);
            // One pad taken out of its group by itself, and one set to loop.
            options.Pads.SetChoke(1, 3, false);
            options.Pads.SetLoop(2, 0, true);
            WavExportResult plan = stems.Plan("Stems", options, Grid128());
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
            // Two pads of a pattern in one group would cut each other off, so the group goes by layer.
            foreach (ChunkInfo chunk in plan.Chunks)
                for (int layer = 0; layer < 4; layer++)
                {
                    PrjNode track = project.Pad(chunk.Scene, chunk.PadOf(layer));
                    Assert.That(track.Scalar("chokeGroup").AsInt32(), Is.EqualTo(chunk.Index == 1 && layer == 3 ? -1 : layer));
                    Assert.That(track.Child("PlaybackData").Scalar("bLoop").AsBool(), Is.EqualTo(chunk.Index == 2 && layer == 0));
                }
            // A loop is exactly its pattern's length: the song is cut with no crossfade.
            Assert.That(plan.CrossfadeFrames, Is.EqualTo(0));
            Assert.That(stems.Plan("Stems", new WavExportOptions(), Grid128()).CrossfadeFrames, Is.GreaterThan(0));

            // With the arrangement on nothing loops, whatever the pads are set to, and the song keeps
            // its crossfade: the last sample is shorter than its pattern, and a loop played its start
            // again before the arranger stopped (the user, 2026-10-16). The settings are kept.
            options.Arrangement = true;
            WavExportResult arranged = stems.Plan("Stems", options, Grid128());
            Assert.That(arranged.LoopOf(2, 0), Is.False);
            Assert.That(arranged.AnyLoop, Is.False);
            Assert.That(arranged.Pads.Loop(2, 0), Is.True);
            Assert.That(arranged.CrossfadeFrames, Is.GreaterThan(0));
            Sp16Project arrangedProject = Sp16Project.FromPrj(Write(stems, arranged)[0].Data);
            foreach (ChunkInfo chunk in arranged.Chunks)
                for (int layer = 0; layer < 4; layer++)
                    Assert.That(arrangedProject.Pad(chunk.Scene, chunk.PadOf(layer)).Child("PlaybackData").Scalar("bLoop").AsBool(), Is.False);

            // Whichever was set last holds: the layer over its pads, then a pad over its layer.
            var pads = new PadSettings();
            pads.SetChoke(5, 1, true);
            Assert.That(pads.Choke(5, 1));
            Assert.That(pads.Choke(4, 1), Is.False);
            pads.SetChokeOfLayer(1, false);
            Assert.That(pads.Choke(5, 1), Is.False);
            pads.SetChokeOfLayer(1, true);
            pads.SetChoke(5, 1, false);
            Assert.That(pads.Choke(4, 1));
            Assert.That(pads.Choke(5, 1), Is.False);
            Assert.That(pads.Choke(4, 0), Is.False);
            // Loop and choke are set apart: neither brings the other with it.
            pads.SetLoop(7, 2, true);
            Assert.That(pads.Choke(7, 2), Is.False);
            pads.SetChoke(7, 2, true);
            pads.SetChoke(7, 2, false);
            Assert.That(pads.Loop(7, 2));
            pads.SetLoopOfLayer(2, true);
            Assert.That(pads.Loop(0, 2) && !pads.Choke(0, 2));
            Assert.That(pads.AnyLoop);

            // A pad that loops is choked only when it is set to be, arrangement or not.
            var loose = new WavExportOptions();
            loose.Pads.SetLoop(1, 2, true);
            Assert.That(stems.Plan("Stems", loose, Grid128()).ChokeOf(1, 2), Is.False);
            loose.Arrangement = false;
            WavExportResult byHand = stems.Plan("Stems", loose, Grid128());
            Assert.That(byHand.ChokeOf(1, 2), Is.False);
            Assert.That(Sp16Project.FromPrj(Write(stems, byHand)[0].Data).Pad(0, byHand.Chunks[1].PadOf(2)).Scalar("chokeGroup").AsInt32(), Is.EqualTo(-1));

            // The unit has eight groups: more layers than that are left with choke Off.
            var nineOptions = new WavExportOptions();
            nineOptions.Pads.SetChokeOfLayer(0, true);
            WavExportResult nine = WavToSp16.Plan(stems.Mix, "Song", nineOptions, Grid128(), Layers(9));
            Assert.That(nine.ChokeOf(0, 0), Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => project.SetPadChokeGroup(0, 0, 8));
        }

        [Test]
        public void S4_TimeStretchIsWrittenInTheWordsTheUnitSaved()
        {
            // Saved on the unit with track 1 set to RESMPL and track 2 to M.TMP; the rest are Off.
            Sp16Project unit = Sp16Project.FromPrj(Fixtures.Read("stretch-modes.prj"));
            string Saved(int pad) { return unit.Pad(0, pad).Child("PlaybackData").Scalar("stretchMode").AsString(); }
            Assert.That(Saved(0), Is.EqualTo(Sp16Project.StretchValue(Sp16Stretch.Resample)));
            Assert.That(Saved(1), Is.EqualTo(Sp16Project.StretchValue(Sp16Stretch.MasterTempo)));
            Assert.That(Saved(2), Is.EqualTo(Sp16Project.StretchValue(Sp16Stretch.Off)));

            StemSet stems = StemsToSp16.Prepare(FourStems(40));
            foreach (Sp16Stretch stretch in new[] { Sp16Stretch.Off, Sp16Stretch.MasterTempo, Sp16Stretch.Resample })
            {
                WavExportResult plan = stems.Plan("Stems", new WavExportOptions { Stretch = stretch }, Grid128());
                Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
                foreach (ChunkInfo chunk in plan.Chunks)
                    for (int layer = 0; layer < 4; layer++)
                    {
                        PrjScalar written = project.Pad(chunk.Scene, chunk.PadOf(layer)).Child("PlaybackData").Scalar("stretchMode");
                        Assert.That(written.Raw, Is.EqualTo(unit.Pad(0, stretch == Sp16Stretch.Resample ? 0 : stretch == Sp16Stretch.MasterTempo ? 1 : 2).Child("PlaybackData").Scalar("stretchMode").Raw));
                    }
            }
        }

        [Test]
        public void S6_TheArrangementCanLeaveOutItsTempoAndTheArrangerCanBeOff()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(40));

            // No tempo in a section is 0 in the file, and the song then plays at the project's.
            WavExportResult free = stems.Plan("Stems", new WavExportOptions { TempoInArrangement = false }, Grid128());
            Sp16Project project = Sp16Project.FromPrj(Write(stems, free)[0].Data);
            Arrangement arrangement = project.GetArrangement();
            Assert.That(arrangement.Length, Is.EqualTo(free.Chunks.Count));
            for (int i = 0; i < arrangement.Length; i++)
                Assert.That(arrangement[i].Bpm, Is.EqualTo(0f));
            Assert.That(project.ProjectBpm, Is.EqualTo(128.0));
            Assert.That(project.PlayArrangement);

            // Arranger off: the song is still in the arrangement, for the unit to switch on.
            var loose = new WavExportOptions { Arrangement = false };
            for (int layer = 0; layer < 4; layer++)
                loose.Pads.SetLoopOfLayer(layer, true);
            WavExportResult pads = stems.Plan("Stems", loose, Grid128());
            project = Sp16Project.FromPrj(Write(stems, pads)[0].Data);
            Assert.That(project.PlayArrangement, Is.False);
            Assert.That(project.GetArrangement().Length, Is.EqualTo(pads.Chunks.Count));
            Assert.That(project.ProjectBpm, Is.EqualTo(128.0));
            foreach (ChunkInfo chunk in pads.Chunks)
            {
                Assert.That(project.FindPattern(chunk.Scene, chunk.Pattern), Is.Not.Null);
                for (int layer = 0; layer < 4; layer++)
                    Assert.That(project.Pad(chunk.Scene, chunk.PadOf(layer)).Child("PlaybackData").Scalar("bLoop").AsBool());
            }
        }

        [Test]
        public void S4_Stems_ASampleThatLoopsFadesAtItsTwoEnds()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(40));
            // 3 ms out and 1 ms in unless told otherwise, which is what the unit was heard with.
            // Loops are only written with the arrangement off.
            var options = new WavExportOptions { Arrangement = false };
            options.Pads.SetLoopOfLayer(0, true);
            WavExportResult plan = stems.Plan("Stems", options, Grid128());
            List<StoredZipReader.Entry> entries = Write(stems, plan);
            var plain = new WavExportOptions { LoopFadeOutMs = 0, LoopFadeInMs = 0, Arrangement = false };
            plain.Pads.SetLoopOfLayer(0, true);
            List<StoredZipReader.Entry> unfaded = Write(stems, stems.Plan("Stems", plain, Grid128()));

            ChunkInfo chunk = plan.Chunks[1];
            WavData Sample(List<StoredZipReader.Entry> from, int layer)
            {
                foreach (StoredZipReader.Entry entry in from)
                    if (entry.Name == chunk.FileNames[layer])
                        return WavReader.Read(entry.Data);
                throw new FileNotFoundException(chunk.FileNames[layer]);
            }
            WavData looped = Sample(entries, 0), asCut = Sample(unfaded, 0);
            int frames = looped.Frames, fadeOut = plan.LoopFadeOutFrames, fadeIn = plan.LoopFadeInFrames;
            Assert.That(fadeOut, Is.EqualTo(132));
            Assert.That(fadeIn, Is.EqualTo(44));
            // Next to nothing at the two ends, where it goes round, and untouched between the fades.
            Assert.That(Math.Abs((int)looped.Samples[0]), Is.LessThanOrEqualTo(Math.Abs((int)asCut.Samples[0]) / fadeIn + 1));
            Assert.That(Math.Abs((int)looped.Samples[2 * (frames - 1)]), Is.LessThanOrEqualTo(Math.Abs((int)asCut.Samples[2 * (frames - 1)]) / fadeOut + 1));
            for (int frame = fadeIn; frame < frames - fadeOut; frame += 53)
                Assert.That(looped.Samples[2 * frame], Is.EqualTo(asCut.Samples[2 * frame]));
            // A pad that does not loop is cut as it always was.
            Assert.That(Sample(entries, 1).Samples, Is.EqualTo(Sample(unfaded, 1).Samples));
        }

        [Test]
        public void S5_Stems_APatternTriggersEveryPadOfItsChunkOnStepOne()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(40));
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);

            Arrangement arrangement = project.GetArrangement();
            Assert.That(arrangement.Length, Is.EqualTo(plan.Chunks.Count));
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                ChunkInfo chunk = plan.Chunks[i];
                Section section = arrangement[i];
                Assert.That(section.Scene, Is.EqualTo(chunk.Scene));
                Assert.That(section.Pattern, Is.EqualTo(chunk.Pattern));
                Assert.That(section.Repeat, Is.EqualTo(1));
                Assert.That(section.Bpm, Is.EqualTo(128f));

                PrjNode pattern = project.FindPattern(chunk.Scene, chunk.Pattern);
                Assert.That(pattern.Scalar("Length").AsInt32(), Is.EqualTo(chunk.PatternLength));
                List<KeyValuePair<int, int>> triggers = StepsBlob.FromScalar(pattern.Scalar("Steps")).GetTriggers();
                Assert.That(triggers.Count, Is.EqualTo(4));
                for (int layer = 0; layer < 4; layer++)
                {
                    Assert.That(triggers[layer].Key, Is.EqualTo(chunk.PadOf(layer)));
                    Assert.That(triggers[layer].Value, Is.EqualTo(0));

                    PrjNode track = project.Pad(chunk.Scene, chunk.PadOf(layer));
                    PrjNode playback = track.Child("PlaybackData");
                    Assert.That(playback.Scalar("audioSourceUrl").AsString(), Is.EqualTo("$Int0/%Tmp/" + chunk.FileNames[layer]));
                    Assert.That(playback.Scalar("lengthSample").AsInt32(), Is.EqualTo(chunk.SampleFrames));
                    Assert.That(playback.Scalar("stretchMode").AsString(), Is.EqualTo("Off"));
                    Assert.That(playback.Scalar("bLoop").AsBool(), Is.False);
                    // Choke Off, or only one of the four would sound.
                    Assert.That(track.Scalar("chokeGroup").AsInt32(), Is.EqualTo(-1));
                }
            }

            // The second scene holds two chunks: two patterns, the eight pads of the top two rows, and nothing on the rest.
            Assert.That(project.FindPattern(1, 1), Is.Not.Null);
            Assert.That(project.FindPattern(1, 2), Is.Null);
            Assert.That(project.Pad(1, 8).Child("PlaybackData").Scalar("audioSourceUrl").AsString(), Is.Not.Empty);
            Assert.That(project.Pad(1, 7).Child("PlaybackData").Scalar("audioSourceUrl").AsString(), Is.Empty);
        }

        /// <summary>Takes the sound out of a stem from one time to another.</summary>
        static void Hush(Stem stem, double fromSeconds, double toSeconds)
        {
            short[] samples = stem.Audio.Samples;
            for (int i = 2 * (int)(fromSeconds * Rate); i < Math.Min(samples.Length, 2 * (int)(toSeconds * Rate)); i++)
                samples[i] = 0;
        }

        [Test]
        public void S5_Stems_ALayerWithOnlyAFaintClickInAChunkHasNoSampleThere()
        {
            // What a separator leaves in a stem whose instrument is not playing: next to nothing,
            // with a click 32 dB under the stem's loudest sample. Found on separated stems of
            // a commercial song, whose drums and bass had samples of it in the second pattern.
            List<Stem> source = FourStems(40);
            short[] bass = source[1].Audio.Samples, vocals = source[3].Audio.Samples;
            int peak = 0, vocalPeak = 0;
            foreach (short sample in bass)
                peak = Math.Max(peak, Math.Abs((int)sample));
            foreach (short sample in vocals)
                vocalPeak = Math.Max(vocalPeak, Math.Abs((int)sample));
            Hush(source[1], 0, 16);
            for (int frame = 3 * Rate; frame < 3 * Rate + Rate / 100; frame++)
                bass[2 * frame] = bass[2 * frame + 1] = (short)(frame % 2 == 0 ? peak / 40 : -peak / 40);
            // A quiet part that goes on is music, however faint: the vocals from 31 s are as quiet as the click, and stay.
            for (int frame = 31 * Rate; frame < vocals.Length / 2; frame++)
                vocals[2 * frame] = vocals[2 * frame + 1] = (short)(frame % 2 == 0 ? vocalPeak / 40 : -vocalPeak / 40);
            StemSet stems = StemsToSp16.Prepare(source);
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(plan.Chunks.Count, Is.EqualTo(6));
            for (int i = 0; i < plan.Chunks.Count; i++)
                for (int layer = 0; layer < 4; layer++)
                    Assert.That(plan.Chunks[i].FileNames[layer] == null, Is.EqualTo(layer == 1 && i < 2), "chunk " + i + ", layer " + layer);
        }

        [Test]
        public void S5_Stems_ALayerThatNeverRisesAbove40dBUnderFullScaleHasNoSamples()
        {
            // A separator's vocal stem of a song with no vocals: residue peaking at -44 dBFS.
            // Against its own peak it looks like music; it is silent. A stem peaking at -38 dBFS
            // is quiet, and keeps its samples.
            List<Stem> source = FourStems(40);
            ScaleToPeak(source[3], 300);
            ScaleToPeak(source[2], 400);
            StemSet stems = StemsToSp16.Prepare(source);
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(plan.Chunks.Count, Is.EqualTo(6));
            for (int i = 0; i < plan.Chunks.Count; i++)
                for (int layer = 0; layer < 4; layer++)
                    Assert.That(plan.Chunks[i].FileNames[layer] == null, Is.EqualTo(layer == 3), "chunk " + i + ", layer " + layer);
        }

        [Test]
        public void S5_ASongThatNeverRisesAbove40dBUnderFullScaleIsRefused()
        {
            // A separator's empty vocal stem loaded by itself is a song with nothing in it.
            List<Stem> source = FourStems(40);
            ScaleToPeak(source[3], 300);
            var refused = Assert.Throws<InvalidOperationException>(() => WavToSp16.Plan(source[3].Audio, "Song", new WavExportOptions { RefuseSilence = true }, Grid128()));
            Assert.That(refused.Message, Is.EqualTo(WavToSp16.SilentMessage));
            // Stems that are all silent are refused too.
            foreach (Stem stem in source)
                ScaleToPeak(stem, 60);
            StemSet stems = StemsToSp16.Prepare(source);
            Assert.Throws<InvalidOperationException>(() => stems.Plan("Stems", new WavExportOptions { RefuseSilence = true }, Grid128()));
            // Without the option it is laid out as it is.
            Assert.That(stems.Plan("Stems", new WavExportOptions(), Grid128()).Chunks.Count, Is.GreaterThan(0));
        }

        static void ScaleToPeak(Stem stem, int peak)
        {
            short[] samples = stem.Audio.Samples;
            int own = 0;
            foreach (short sample in samples)
                own = Math.Max(own, Math.Abs((int)sample));
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Round((double)samples[i] * peak / own);
        }

        [Test]
        public void S5_Stems_PatternsOfSilenceAtTheEndAreDropped()
        {
            // Chunks start at 0.25 s and every 7.5 s after. Nothing sounds from 23 s on, so the
            // fifth and sixth chunks are silence in every stem; silence inside the song stays.
            List<Stem> source = FourStems(40);
            foreach (Stem stem in source)
            {
                Hush(stem, 8, 15);
                Hush(stem, 23, 40);
            }
            StemSet stems = StemsToSp16.Prepare(source);
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(plan.Chunks.Count, Is.EqualTo(4));
            List<StoredZipReader.Entry> entries = Write(stems, plan);
            Sp16Project project = Sp16Project.FromPrj(entries[0].Data);
            Assert.That(project.FindPattern(0, 3), Is.Not.Null);
            Assert.That(project.FindPattern(1, 0), Is.Null);

            // A song in one file loses its silent end too.
            WavExportResult mix = WavToSp16.Plan(stems.Mix, "Mix", new WavExportOptions(), Grid128());
            Assert.That(mix.Chunks.Count, Is.EqualTo(4));
        }

        [Test]
        public void S5_Stems_ALayerThatIsSilentForAChunkHasNoSampleThereAndNoTrigger()
        {
            // Chunks start at 0.25 s and every 7.5 s after. The bass comes in at 16 s, so it has
            // nothing in the first two; the vocals stop at 31 s, so they have nothing in the last.
            List<Stem> source = FourStems(40);
            Hush(source[1], 0, 16);
            Hush(source[3], 31, 40);
            StemSet stems = StemsToSp16.Prepare(source);
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(plan.Chunks.Count, Is.EqualTo(6));
            for (int i = 0; i < plan.Chunks.Count; i++)
                for (int layer = 0; layer < 4; layer++)
                    Assert.That(plan.Chunks[i].FileNames[layer] == null, Is.EqualTo((layer == 1 && i < 2) || (layer == 3 && i == 5)), "chunk " + i + ", layer " + layer);
            Assert.That(plan.SilentSamples, Is.EqualTo(3));
            Assert.That(plan.Chunks[0].SampleCount, Is.EqualTo(3));
            Assert.That(plan.Chunks[0].FileName, Is.EqualTo("Drums_Stems_001.wav"));
            // The names keep their numbers: the bass's first sample is its third chunk's.
            Assert.That(plan.Chunks[2].FileNames[1], Is.EqualTo("Bass_Stems_003.wav"));
            // Nothing is said about it: the memory figure shows it, and there is nothing to do about it.
            Assert.That(plan.Warnings, Has.None.Contains("silent"));

            // They cost no memory.
            long all = 0;
            foreach (ChunkInfo chunk in plan.Chunks)
                all += 4L * chunk.SampleFrames * Sp16Project.StereoBytesPerFrame;
            long saved = (plan.Chunks[0].SampleFrames + plan.Chunks[1].SampleFrames + plan.Chunks[5].SampleFrames) * (long)Sp16Project.StereoBytesPerFrame;
            Assert.That(plan.SampleBytes, Is.EqualTo(all - saved));
            Assert.That(plan.BytesOfLayer(0) - plan.BytesOfLayer(1), Is.EqualTo((plan.Chunks[0].SampleFrames + plan.Chunks[1].SampleFrames) * (long)Sp16Project.StereoBytesPerFrame));

            // Without the audio to look at, every layer has a sample in every chunk, as before.
            WavExportResult unseen = WavToSp16.Plan(stems.Mix, "Stems", new WavExportOptions(), Grid128(), stems.Layers);
            Assert.That(unseen.SilentSamples, Is.EqualTo(0));

            List<StoredZipReader.Entry> entries = Write(stems, plan);
            Assert.That(entries.Count, Is.EqualTo(1 + 2 * (4 * 6 - 3)));
            var names = entries.ConvertAll(entry => entry.Name);
            Assert.That(names, Has.None.EqualTo("Bass_Stems_001.wav"));
            Assert.That(names, Has.None.EqualTo("Bass_Stems_002.wav.dat"));
            Assert.That(names, Has.None.EqualTo("Vocals_Stems_006.wav"));
            Assert.That(names, Has.Some.EqualTo("Bass_Stems_003.wav"));

            Sp16Project project = Sp16Project.FromPrj(entries[0].Data);
            PrjNode template = Fixtures.ReadProject("arr0.prj");
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                ChunkInfo chunk = plan.Chunks[i];
                var triggered = new List<int>();
                foreach (KeyValuePair<int, int> trigger in StepsBlob.FromScalar(project.FindPattern(chunk.Scene, chunk.Pattern).Scalar("Steps")).GetTriggers())
                {
                    Assert.That(trigger.Value, Is.EqualTo(0));
                    triggered.Add(trigger.Key);
                }
                var expected = new List<int>();
                for (int layer = 0; layer < 4; layer++)
                {
                    PrjNode track = project.Pad(chunk.Scene, chunk.PadOf(layer));
                    if (chunk.FileNames[layer] != null)
                    {
                        expected.Add(chunk.PadOf(layer));
                        continue;
                    }
                    // The pad has no sample, and is as the empty project has it but for its stem's colour.
                    PrjNode empty = template.Child("SceneData" + chunk.Scene).Child("TrackData" + chunk.PadOf(layer));
                    Assert.That(track.Child("PlaybackData").Scalar("audioSourceUrl").AsString(), Is.EqualTo(""));
                    Assert.That(track.Scalar("colourIndex").AsInt32(), Is.EqualTo(4 * layer));
                    track.Scalar("colourIndex").SetInt32(empty.Scalar("colourIndex").AsInt32());
                    FormatTests.AssertSameTree(empty, track, "TrackData");
                }
                Assert.That(triggered, Is.EqualTo(expected), "chunk " + i);
            }

            // What is left of the bass still rebuilds it from where it comes in.
            short[] bass = source[1].Audio.Samples;
            var rebuilt = new int[bass.Length];
            int entry = 1;
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                for (int layer = 0; layer < 4; layer++)
                {
                    if (plan.Chunks[i].FileNames[layer] == null)
                        continue;
                    if (layer == 1)
                    {
                        WavData sample = WavReader.Read(entries[entry].Data);
                        int sound = sample.Samples.Length - 2 * plan.Chunks[i].TrailingSilenceFrames;
                        for (int s = 0; s < sound; s++)
                            rebuilt[2 * plan.Chunks[i].StartFrame + s] += sample.Samples[s];
                        for (int s = sound; s < sample.Samples.Length; s++)
                            Assert.That(sample.Samples[s], Is.EqualTo(0));
                    }
                    entry += 2;
                }
            }
            int apartBy = 0;
            for (int s = 0; s < bass.Length; s++)
                apartBy = Math.Max(apartBy, Math.Abs(rebuilt[s] - bass[s]));
            Assert.That(apartBy, Is.LessThanOrEqualTo(1));
        }

        [Test]
        public void S5_Stems_APatternWithNoSoundAtAllKeepsOneSampleToTrigger()
        {
            // Nothing in any stem for the first 16 s: the first two patterns would have no trigger,
            // which the unit has not been asked to play, so each keeps its first layer's sample.
            List<Stem> source = FourStems(40);
            foreach (Stem stem in source)
                Hush(stem, 0, 16);
            StemSet stems = StemsToSp16.Prepare(source);
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            for (int i = 0; i < 2; i++)
            {
                Assert.That(plan.Chunks[i].SampleCount, Is.EqualTo(1));
                Assert.That(plan.Chunks[i].FileNames[0], Is.Not.Null);
            }
            Assert.That(plan.Chunks[2].SampleCount, Is.EqualTo(4));
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
            List<KeyValuePair<int, int>> triggers = StepsBlob.FromScalar(project.FindPattern(0, 0).Scalar("Steps")).GetTriggers();
            Assert.That(triggers.Count, Is.EqualTo(1));
            Assert.That(triggers[0].Key, Is.EqualTo(plan.Chunks[0].Pad));
        }

        [Test]
        public void S4_VelocityIsTheAmpEnvelopesFlagAndIsSetByLayer()
        {
            // Saved on the SP-16 from a Strands package with velocity on for tracks 1 and 2 of scene 1 only.
            PrjNode saved = Fixtures.ReadProject("velocity.prj");
            for (int pad = 0; pad < Sp16Project.PadsPerScene; pad++)
                Assert.That(saved.Child("SceneData0").Child("TrackData" + pad).Child("AmpEnvelopeData").Scalar("bVelocityEffective").AsBool(),
                    Is.EqualTo(pad < 2), "track " + (pad + 1));

            // The bass is silent for the first two chunks: its empty pads take its velocity too.
            List<Stem> source = FourStems(40);
            Hush(source[1], 0, 16);
            StemSet stems = StemsToSp16.Prepare(source);
            var options = new WavExportOptions();
            options.Pads.SetVelocityOfLayer(1, true);
            options.Pads.SetVelocityOfLayer(2, true);
            options.Pads.SetVelocity(3, 2, false);
            WavExportResult plan = stems.Plan("Stems", options, Grid128());
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
            foreach (ChunkInfo chunk in plan.Chunks)
                for (int layer = 0; layer < 4; layer++)
                {
                    bool expected = (layer == 1) || (layer == 2 && chunk.Index != 3);
                    Assert.That(project.Pad(chunk.Scene, chunk.PadOf(layer)).Child("AmpEnvelopeData").Scalar("bVelocityEffective").AsBool(),
                        Is.EqualTo(expected), "chunk " + chunk.Index + ", layer " + layer);
                }
            Assert.That(options.Pads.VelocityOfLayer(2), Is.True);
            Assert.That(options.Pads.VelocityOfLayer(0), Is.False);
        }

        [Test]
        public void S4_Stems_AnEmptyPadTakesItsStemsChokeLoopAndStretch()
        {
            // The bass is silent for the first two chunks, so its pads there have no sample.
            List<Stem> source = FourStems(40);
            Hush(source[1], 0, 16);
            StemSet stems = StemsToSp16.Prepare(source);
            var options = new WavExportOptions { Stretch = Sp16Stretch.Resample, Arrangement = false };
            options.Pads.SetChokeOfLayer(1, true);
            options.Pads.SetLoopOfLayer(1, true);
            WavExportResult plan = stems.Plan("Stems", options, Grid128());
            Assert.That(plan.Chunks[0].FileNames[1], Is.Null);
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
            for (int i = 0; i < 2; i++)
            {
                PrjNode track = project.Pad(plan.Chunks[i].Scene, plan.Chunks[i].PadOf(1));
                Assert.That(track.Child("PlaybackData").Scalar("audioSourceUrl").AsString(), Is.EqualTo(""), "chunk " + i);
                Assert.That(track.Scalar("chokeGroup").AsInt32(), Is.EqualTo(1), "chunk " + i);
                Assert.That(track.Child("PlaybackData").Scalar("bLoop").AsBool(), Is.True, "chunk " + i);
                Assert.That(track.Child("PlaybackData").Scalar("stretchMode").AsString(), Is.EqualTo("Resample"), "chunk " + i);
                Assert.That(track.Scalar("colourIndex").AsInt32(), Is.EqualTo(4), "chunk " + i);
            }
        }

        [Test]
        public void S4_Stems_EveryPadOfAStemHasTheColourOfItsStrip()
        {
            // The unit gives track N of a scene colour N. Four stems on the four pairs take the
            // 1st, 5th, 9th and 13th, in every pattern, so that a stem is one colour throughout.
            int[] Colours(Sp16Project written, WavExportResult planned, int chunk)
            {
                var colours = new int[planned.Layers.Count];
                for (int layer = 0; layer < colours.Length; layer++)
                    colours[layer] = written.Pad(planned.Chunks[chunk].Scene, planned.Chunks[chunk].PadOf(layer)).Scalar("colourIndex").AsInt32();
                return colours;
            }
            StemSet stems = StemsToSp16.Prepare(FourStems(40));
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            Sp16Project project = Sp16Project.FromPrj(Write(stems, plan)[0].Data);
            for (int chunk = 0; chunk < plan.Chunks.Count; chunk++)
                Assert.That(Colours(project, plan, chunk), Is.EqualTo(new[] { 0, 4, 8, 12 }), "chunk " + chunk);

            // On master a stem's strip is its group.
            StemSet onMaster = StemsToSp16.Prepare(OnMaster(FourStems(40)));
            WavExportResult masterPlan = onMaster.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(Colours(Sp16Project.FromPrj(Write(onMaster, masterPlan)[0].Data), masterPlan, 1), Is.EqualTo(new[] { 0, 4, 8, 12 }));

            // Tracks kept apart on one strip take the colours after its first, and an output by itself is of its pair's strip.
            List<Stem> more = FourStems(40);
            more[1].Output = Sp16Output.Out12;
            more[2].Output = Sp16Output.Out4;
            more[3].Output = Sp16Output.Out78;
            StemSet apart = StemsToSp16.Prepare(more, false);
            WavExportResult apartPlan = apart.Plan("Stems", new WavExportOptions(), Grid128());
            Assert.That(Colours(Sp16Project.FromPrj(Write(apart, apartPlan)[0].Data), apartPlan, 0), Is.EqualTo(new[] { 0, 1, 4, 12 }));

            // A full mix is left as the unit has it: each track its own colour.
            byte[] package;
            using (var output = new MemoryStream())
            {
                WavToSp16.Export(FourStems(40)[0].Audio, Fixtures.Read("arr0.prj"), output, "Mix", new WavExportOptions());
                package = output.ToArray();
            }
            Sp16Project mix = Sp16Project.FromPrj(StoredZipReader.Read(package)[0].Data);
            for (int pad = 0; pad < 16; pad++)
                Assert.That(mix.Pad(0, pad).Scalar("colourIndex").AsInt32(), Is.EqualTo(pad));
        }

        [Test]
        public void S9_Stems_EveryLayerIsCutAtTheSameFramesAndItsSamplesRebuildTheStem()
        {
            List<Stem> source = FourStems(40);
            StemSet stems = StemsToSp16.Prepare(source);
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            List<StoredZipReader.Entry> entries = Write(stems, plan);

            Assert.That(entries.Count, Is.EqualTo(1 + 2 * 4 * plan.Chunks.Count));
            Assert.That(entries[0].Name, Is.EqualTo("Stems.prj"));
            Assert.That(plan.Chunks[0].FileNames, Is.EqualTo(new[] { "Drums_Stems_001.wav", "Bass_Stems_001.wav", "Other_Stems_001.wav", "Vocals_Stems_001.wav" }));

            for (int layer = 0; layer < 4; layer++)
            {
                short[] stem = source[layer].Audio.Samples;
                var rebuilt = new int[stem.Length];
                for (int i = 0; i < plan.Chunks.Count; i++)
                {
                    ChunkInfo chunk = plan.Chunks[i];
                    StoredZipReader.Entry wavEntry = entries[1 + 2 * (i * 4 + layer)], datEntry = entries[2 + 2 * (i * 4 + layer)];
                    Assert.That(wavEntry.Name, Is.EqualTo(chunk.FileNames[layer]));
                    Assert.That(datEntry.Name, Is.EqualTo(chunk.FileNames[layer] + ".dat"));
                    Assert.That(SampleAdjunct.Read(datEntry.Data).SampleNum, Is.EqualTo(chunk.SampleFrames));
                    WavData sample = WavReader.Read(wavEntry.Data);
                    Assert.That(sample.Frames, Is.EqualTo(chunk.SampleFrames));
                    // The lead-out's silence after its sound is not in the stem.
                    int sound = sample.Samples.Length - 2 * chunk.TrailingSilenceFrames;
                    for (int k = 0; k < sound; k++)
                        rebuilt[chunk.StartFrame * 2 + k] += sample.Samples[k];
                    for (int k = sound; k < sample.Samples.Length; k++)
                        Assert.That(sample.Samples[k], Is.EqualTo(0));
                }
                // Laid back over each other, with their crossfades, the chunks are the stem again, left and right.
                for (int k = plan.Chunks[0].StartFrame * 2; k < stem.Length; k++)
                    if (Math.Abs(rebuilt[k] - stem[k]) > 1)
                        Assert.Fail(source[layer].Name + " differs from its stem at sample " + k);
            }
        }

        [Test]
        public void S4_Stems_TheLeadInAndItsSlicesAreTheSameOnEveryLayer()
        {
            // The first pattern is made to start two bars in, at 4 s, so there is a lead-in of a part
            // beat and eight whole ones, with silence in front to bring it to a whole number of steps.
            List<Stem> source = FourStems(20);
            StemSet stems = StemsToSp16.Prepare(source);
            var options = new WavExportOptions { KeepLeadIn = true, SliceOnBeats = true, PatternAnchorSeconds = 4.0 };
            WavExportResult plan = stems.Plan("Stems", options, Grid128());
            ChunkInfo lead = plan.Chunks[0];
            Assert.That(lead.IsLeadIn);
            Assert.That(lead.Slices, Is.EqualTo(9));
            Assert.That(lead.LeadingSilenceFrames, Is.GreaterThan(0));
            Assert.That(plan.Chunks[1].Pad, Is.EqualTo(8));

            List<StoredZipReader.Entry> entries = Write(stems, plan);
            Sp16Project project = Sp16Project.FromPrj(entries[0].Data);
            Assert.That(project.FindPattern(0, 0).Scalar("Length").AsInt32(), Is.EqualTo(lead.PatternLength));
            Assert.That(StepsBlob.FromScalar(project.FindPattern(0, 0).Scalar("Steps")).GetTriggers().Count, Is.EqualTo(4));
            for (int layer = 0; layer < 4; layer++)
            {
                Assert.That(project.GetSliceBoundaries(0, lead.PadOf(layer)), Is.EqualTo(lead.SliceBoundariesInSample()));
                Assert.That(project.Pad(0, lead.PadOf(layer)).Child("SliceData").Scalar("nGrids").AsInt32(), Is.EqualTo(9));
                Assert.That(project.GetSliceBoundaries(0, plan.Chunks[1].PadOf(layer)), Is.EqualTo(plan.Chunks[1].SliceBoundariesInSample()));

                // Each sample is the silence and then the start of its own stem.
                WavData sample = WavReader.Read(entries[1 + 2 * layer].Data);
                Assert.That(sample.Frames, Is.EqualTo(lead.SampleFrames));
                for (int frame = 0; frame < lead.LeadingSilenceFrames; frame += 37)
                    Assert.That(sample.Samples[2 * frame], Is.EqualTo(0));
                for (int frame = 0; frame < 4 * Rate - 1000; frame += 97)
                    Assert.That(sample.Samples[2 * (lead.LeadingSilenceFrames + frame) + 1], Is.EqualTo(source[layer].Audio.Samples[2 * frame + 1]), source[layer].Name + " frame " + frame);
            }
        }

        [Test]
        public void WritePackage_RefusesAudioThePlanWasNotMadeFor()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(20));
            WavExportResult plan = stems.Plan("Stems", new WavExportOptions(), Grid128());
            string path = Path.Combine(Path.GetTempPath(), "strands_stems_refused.tpkg");
            byte[] template = Fixtures.Read("arr0.prj");

            var three = new List<short[]>(stems.LayerSamples);
            three.RemoveAt(3);
            Assert.Throws<ArgumentException>(() => WavToSp16.WritePackage(three, template, path, plan));
            var shortened = new List<short[]>(stems.LayerSamples);
            shortened[2] = new short[shortened[2].Length - 2];
            Assert.Throws<ArgumentException>(() => WavToSp16.WritePackage(shortened, template, path, plan));
            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void StemSet_StemsOnOneOutputAreAddedIntoOneSampleUnlessKeptApart()
        {
            // A fifth stem comes round to the first pair again.
            Assert.That(StemsToSp16.DefaultOutput(4), Is.EqualTo(Sp16Output.Out12));
            Assert.That(StemsToSp16.DefaultOutput(7), Is.EqualTo(Sp16Output.Out78));
            List<Stem> source = FourStems(10);
            int frames = source[0].Audio.Frames;
            source.Add(new Stem { Name = "Perc", Audio = Tone(frames, 550, 3000), Output = StemsToSp16.DefaultOutput(4) });

            StemSet mixed = StemsToSp16.Prepare(source);
            // The stems themselves are kept as they came, for finding the grid; only the layers are mixed.
            Assert.That(mixed.StemLabels, Is.EqualTo(new[] { "Drums", "Bass", "Other", "Vocals", "Perc" }));
            Assert.That(mixed.StemSamples[4], Is.SameAs(source[4].Audio.Samples));
            Assert.That(mixed.Layers.Count, Is.EqualTo(4));
            Assert.That(mixed.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Mix12", "Bass", "Other", "Vocals" }));
            Assert.That(mixed.Layers.ConvertAll(layer => layer.Output), Is.EqualTo(new[] { Sp16Output.Out12, Sp16Output.Out34, Sp16Output.Out56, Sp16Output.Out78 }));
            for (int k = 0; k < frames * 2; k += 7)
                Assert.That(mixed.LayerSamples[0][k], Is.EqualTo(source[0].Audio.Samples[k] + source[4].Audio.Samples[k]));
            Assert.That(mixed.LayerSamples[1], Is.SameAs(source[1].Audio.Samples));
            Assert.That(mixed.Warnings, Is.Empty);

            StemSet apart = StemsToSp16.Prepare(source, false);
            Assert.That(apart.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Drums", "Bass", "Other", "Vocals", "Perc" }));
            Assert.That(apart.Layers[4].Output, Is.EqualTo(Sp16Output.Out12));
            Assert.That(apart.LayerSamples[4], Is.SameAs(source[4].Audio.Samples));
            // Five pads to a chunk leave room for three chunks in a scene.
            WavExportResult plan = apart.Plan("x", new WavExportOptions { BarsPerChunk = 1 }, Grid128());
            Assert.That(plan.Chunks[3].Scene, Is.EqualTo(1));
            Assert.That(plan.Chunks[2].Pad, Is.EqualTo(6));

            // Two stems that together go over full level are held there, and it is reported.
            var loud = new List<Stem>
            {
                new Stem { Name = "A", Audio = Tone(Rate, 100, 30000), Output = Sp16Output.Out34 },
                new Stem { Name = "B", Audio = Tone(Rate, 100, 30000), Output = Sp16Output.Out34 }
            };
            StemSet clipped = StemsToSp16.Prepare(loud);
            Assert.That(clipped.Layers.Count, Is.EqualTo(1));
            Assert.That(clipped.Layers[0].Label, Is.EqualTo("Mix34"));
            short highest = 0, lowest = 0;
            foreach (short sample in clipped.LayerSamples[0])
            {
                highest = Math.Max(highest, sample);
                lowest = Math.Min(lowest, sample);
            }
            Assert.That(highest, Is.EqualTo(short.MaxValue));
            Assert.That(lowest, Is.EqualTo(short.MinValue));
            Assert.That(clipped.Warnings, Has.Some.EqualTo("A and B summed are too loud together and will distort. Use Separate tracks, or turn the files down."));
        }

        [Test]
        public void StemSet_ShorterStemsAreFilledOutWithSilence()
        {
            List<Stem> source = FourStems(10);
            int frames = source[0].Audio.Frames;
            source[2].Audio = Tone(frames - 5000, 220, 3000);

            StemSet stems = StemsToSp16.Prepare(source);
            Assert.That(stems.Mix.Frames, Is.EqualTo(frames));
            Assert.That(stems.LayerSamples[2].Length, Is.EqualTo(frames * 2));
            Assert.That(stems.LayerSamples[2][2 * (frames - 5001)], Is.EqualTo(source[2].Audio.Samples[2 * (frames - 5001)]));
            for (int frame = frames - 5000; frame < frames; frame++)
                Assert.That(stems.LayerSamples[2][2 * frame], Is.EqualTo(0));
            Assert.That(stems.Warnings, Has.Some.Contains("same length"));
            // The warning goes with the plan, where an export reports it.
            Assert.That(stems.Plan("x", new WavExportOptions(), Grid128()).Warnings, Has.Some.Contains("same length"));

            Assert.That(StemsToSp16.Prepare(FourStems(10)).Warnings, Is.Empty);
            source[1].Audio.SampleRate = 48000;
            Assert.Throws<NotSupportedException>(() => StemsToSp16.Prepare(source));
        }

        [Test]
        public void Prepare_StemsThatWereAtDifferentRatesAreOneLengthOnceConverted()
        {
            // Ten seconds at 44.1 kHz and the same ten seconds at 48 kHz, as two files of one song might be.
            List<Stem> source = FourStems(10);
            var other = new short[2 * 480000];
            for (int i = 0; i < 480000; i++)
                other[2 * i] = other[2 * i + 1] = (short)Math.Round(3000 * Math.Sin(2 * Math.PI * 330 * i / 48000.0));
            source[1].Audio = Resampler.ToTargetRate(new WavData { SampleRate = 48000, Samples = other, SourceChannels = 2, SourceBitsPerSample = 16 });

            StemSet stems = StemsToSp16.Prepare(source);
            Assert.That(stems.Warnings, Is.Empty);
            Assert.That(stems.Mix.Frames, Is.EqualTo(10 * Rate));
            // Only when every stem came from the same rate does the song say it was converted.
            Assert.That(stems.Mix.SourceSampleRate, Is.EqualTo(0));
            foreach (Stem stem in source)
                stem.Audio.SourceSampleRate = 48000;
            Assert.That(StemsToSp16.Prepare(source).Mix.SourceSampleRate, Is.EqualTo(48000));
            Assert.That(StemsToSp16.Prepare(source).Plan("x", new WavExportOptions(), Grid128()).Describe(), Does.Contain("converted from 48000 Hz"));
        }

        [Test]
        public void StemSet_TheMixIsTheStemsAddedTogetherAndTheBeatIsFoundFromIt()
        {
            List<Stem> source = FourStems(40);
            StemSet stems = StemsToSp16.Prepare(source);
            for (int k = 0; k < stems.Mix.Samples.Length; k += 11)
            {
                int sum = 0;
                foreach (Stem stem in source)
                    sum += stem.Audio.Samples[k];
                Assert.That(stems.Mix.Samples[k], Is.EqualTo(sum));
            }

            // The tones on top of the kick do not move the beat: the same tempo, and lines on the kicks.
            BeatGrid grid = BeatGridAnalyzer.Analyze(stems.Mix.Samples, Rate);
            Assert.That(grid.Bpm, Is.EqualTo(128).Within(0.02));
            Assert.That(Math.Abs(BeatError(grid, 0.25, 128)), Is.LessThan(0.003));

            // Three stems that add up to twice full level: the mix is turned down as a whole, not flattened.
            var loud = new List<Stem>();
            for (int i = 0; i < 3; i++)
                loud.Add(new Stem { Name = "T" + i, Audio = Tone(Rate, 100, 21000), Output = StemsToSp16.DefaultOutput(i) });
            short[] mix = StemsToSp16.Prepare(loud).Mix.Samples;
            for (int frame = 0; frame < Rate; frame += 3)
                Assert.That(mix[2 * frame], Is.EqualTo(3 * loud[0].Audio.Samples[2 * frame] * (32767.0 / 63000)).Within(1.0));
        }

        [Test]
        public void DriftMeter_AttackOnLinesIsHighOnTheHitsAndFallsAwayWithinMilliseconds()
        {
            WavData wav = AudioAndExportTests.Synthesize(128, 0.3, 0, 40);
            var meter = new BeatDriftMeter(wav.Samples, Rate);
            var onTheKicks = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.3, DownbeatIndex = 0 };
            double attack = meter.AttackOnLines(onTheKicks);
            Assert.That(attack, Is.GreaterThan(0.05));

            // Six milliseconds late the lines are inside the body of the kick, where nothing rises.
            var late = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.306, DownbeatIndex = 0 };
            Assert.That(meter.AttackOnLines(late), Is.LessThan(attack / 4));
            // Half a beat on they are on the off-beat tick, which is a quarter as loud.
            var offBeat = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.3 + 0.5 * 60 / 128, DownbeatIndex = 0 };
            Assert.That(meter.AttackOnLines(offBeat), Is.LessThan(attack / 2));
            // A tempo that is a little out leaves the lines on the kicks only near the start.
            var slow = new BeatGrid { Bpm = 127.9, FirstBeatSeconds = 0.3, DownbeatIndex = 0 };
            Assert.That(meter.AttackOnLines(slow), Is.LessThan(attack / 2));
            // Which beat is called the first, and which starts the bar, makes no difference.
            var later = new BeatGrid { Bpm = 128, FirstBeatSeconds = 0.3 + 3 * 60.0 / 128, DownbeatIndex = 2 };
            Assert.That(meter.AttackOnLines(later), Is.EqualTo(attack).Within(1e-9));
        }

        [Test]
        public void ChooseGrid_TakesTheDrumsWhenTheStemsTogetherHideTheKick()
        {
            // A kick with a tick on the off-beat, and three steady tones that between them fill the
            // bass. Added together they leave the analyser on the tick, half a beat out; the drums
            // alone are read correctly. (Found while writing these tests.)
            WavData drums = AudioAndExportTests.Synthesize(128, 0.25, 0, 40);
            var source = new List<Stem>
            {
                new Stem { Name = "Drums", Audio = drums, Output = Sp16Output.Out12 },
                new Stem { Name = "Bass", Audio = Tone(drums.Frames, 110, 2000), Output = Sp16Output.Out34 },
                new Stem { Name = "Other", Audio = Tone(drums.Frames, 220, 3500), Output = Sp16Output.Out56 },
                new Stem { Name = "Vocals", Audio = Tone(drums.Frames, 330, 5000), Output = Sp16Output.Out78 }
            };
            StemSet stems = StemsToSp16.Prepare(source);
            float progress = 0;
            bool wentBack = false;
            GridChoice choice = StemsToSp16.ChooseGrid(stems, null, new Progress(value =>
            {
                wentBack |= value < progress;
                progress = value;
            }));

            Assert.That(choice.Candidates.Count, Is.EqualTo(5));
            Assert.That(choice.Candidates.ConvertAll(candidate => candidate.Name), Is.EqualTo(new[] { "Drums", "Bass", "Other", "Vocals", "all stems" }));
            Assert.That(progress, Is.EqualTo(1f));
            Assert.That(wentBack, Is.False);
            Assert.That(choice.Chosen.Stem, Is.EqualTo("Drums"));
            Assert.That(choice.Grid, Is.SameAs(choice.Candidates[0].Grid));
            Assert.That(choice.Grid.Bpm, Is.EqualTo(128).Within(0.02));
            Assert.That(Math.Abs(BeatError(choice.Grid, 0.25, 128)), Is.LessThan(0.003));
            Assert.That(choice.Warnings, Is.Empty);
            // Every other grid has a fraction of the attack on its lines.
            foreach (GridCandidate candidate in choice.Candidates)
                if (candidate != choice.Chosen)
                    Assert.That(candidate.Attack, Is.LessThan(choice.Chosen.Attack / 2), candidate.Name);
            Assert.That(choice.Describe(), Does.Contain("Grid from      Drums").And.Contain("all stems"));
        }

        [Test]
        public void ChooseGrid_GivesTheSameGridAsTheStemsTogetherWhenThatOneIsRight()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(40));
            GridChoice choice = StemsToSp16.ChooseGrid(stems);
            BeatGrid together = choice.Candidates[4].Grid;
            Assert.That(choice.Candidates[4].Stem, Is.Null);
            Assert.That(Math.Abs(BeatError(together, 0.25, 128)), Is.LessThan(0.003));
            // Whichever is taken, the drums or everything together, it is the same grid to within a millisecond.
            Assert.That(choice.Grid.Bpm, Is.EqualTo(together.Bpm).Within(0.01));
            Assert.That(Math.Abs(BeatError(choice.Grid, together.FirstBeatSeconds, together.Bpm)), Is.LessThan(0.001));

            // One stem is its own candidate, and there is nothing to add it to.
            var one = new List<Stem> { new Stem { Name = "Loop", Audio = AudioAndExportTests.Synthesize(128, 0.25, 0, 20), Output = Sp16Output.Out12 } };
            GridChoice single = StemsToSp16.ChooseGrid(StemsToSp16.Prepare(one));
            Assert.That(single.Candidates.Count, Is.EqualTo(1));
            Assert.That(single.Chosen.Stem, Is.EqualTo("Loop"));
            Assert.That(single.Grid.Bpm, Is.EqualTo(128).Within(0.02));
        }

        [Test]
        public void ChooseGrid_StopsWhenCancelledAndSaysWhyWhenNoStemGivesAGrid()
        {
            StemSet stems = StemsToSp16.Prepare(FourStems(20));
            using (var cancel = new System.Threading.CancellationTokenSource())
            {
                cancel.Cancel();
                Assert.Throws<OperationCanceledException>(() => StemsToSp16.ChooseGrid(stems, null, null, cancel.Token));
            }

            // Three seconds is too little for any of them.
            var brief = new List<Stem>
            {
                new Stem { Name = "A", Audio = Tone(3 * Rate, 440, 3000), Output = Sp16Output.Out12 },
                new Stem { Name = "B", Audio = Tone(3 * Rate, 660, 3000), Output = Sp16Output.Out34 }
            };
            var refused = Assert.Throws<ArgumentException>(() => StemsToSp16.ChooseGrid(StemsToSp16.Prepare(brief)));
            Assert.That(refused.Message, Does.Contain("4 seconds"));
        }

        /// <summary>How far the grid's beat is from a known one, in seconds, whichever beat is called the first.</summary>
        static double BeatError(BeatGrid grid, double firstBeatSeconds, double bpm)
        {
            double beat = 60.0 / bpm;
            double error = (grid.FirstBeatSeconds - firstBeatSeconds) % beat;
            if (error > beat / 2) error -= beat;
            if (error < -beat / 2) error += beat;
            return error;
        }

        sealed class Progress : IProgress<float>
        {
            readonly Action<float> report;
            public Progress(Action<float> report) { this.report = report; }
            public void Report(float value) { report(value); }
        }

        [Test]
        public void StemSet_LabelsAreThePartOfEachNameTheOthersDoNotShare()
        {
            Assert.That(StemsToSp16.Labels(new[] { "drums", "bass", "other", "vocals" }), Is.EqualTo(new[] { "drums", "bass", "other", "vocals" }));
            Assert.That(StemsToSp16.Labels(new[] { "Song (Drums)", "Song (Bass)" }), Is.EqualTo(new[] { "Drums", "Bass" }));
            Assert.That(StemsToSp16.Labels(new[] { "01 Song - drums_stem", "01 Song - bass_stem" }), Is.EqualTo(new[] { "drums", "bass" }));
            // What is shared inside a word stays.
            Assert.That(StemsToSp16.Labels(new[] { "bass", "backing" }), Is.EqualTo(new[] { "bass", "backing" }));
            Assert.That(StemsToSp16.Labels(new[] { "Song_Drums", "Song_Drum Loop" }), Is.EqualTo(new[] { "Drums", "Drum Loop" }));
            Assert.That(StemsToSp16.Labels(new[] { " Song " }), Is.EqualTo(new[] { "Song" }));
            // A name with nothing of its own is numbered.
            Assert.That(StemsToSp16.Labels(new[] { "Song - ", "Song - Drums" }), Is.EqualTo(new[] { "Stem1", "Drums" }));
            // What the names share can be one of a pair of brackets, which is then put back.
            Assert.That(StemsToSp16.Labels(new[] { "Drums (L)", "Drums (R)", "Bass (L)" }), Is.EqualTo(new[] { "Drums (L)", "Drums (R)", "Bass (L)" }));
            Assert.That(StemsToSp16.Labels(new[] { "Song (Drums) 1", "Song (Drums) 2", "Song (Bass)" }), Is.EqualTo(new[] { "(Drums) 1", "(Drums) 2", "Bass" }));
            Assert.That(StemsToSp16.Labels(new[] { "Kick [dry", "Snare [wet]" }), Is.EqualTo(new[] { "Kick [dry]", "Snare [wet]" }));
        }

        [Test]
        public void SongName_IsWhatTheNamesAllStartWith()
        {
            Assert.That(StemsToSp16.SongName(new[] { "Song (Drums)", "Song (Bass)" }), Is.EqualTo("Song"));
            Assert.That(StemsToSp16.SongName(new[] { "01 My Song - drums_stem", "01 My Song - bass_stem" }), Is.EqualTo("01 My Song"));
            Assert.That(StemsToSp16.SongName(new[] { "Song_Drums", "Song_Drum Loop" }), Is.EqualTo("Song"));
            Assert.That(StemsToSp16.SongName(new[] { "drums", "bass" }), Is.EqualTo(""));
            // What is shared inside a word is not a name.
            Assert.That(StemsToSp16.SongName(new[] { "bass", "backing" }), Is.EqualTo(""));
            Assert.That(StemsToSp16.SongName(new[] { "Only One" }), Is.EqualTo("Only One"));
            Assert.That(StemsToSp16.SongName(new string[0]), Is.EqualTo(""));
        }

        [Test]
        public void Read_GivesEachFileItsNameAndTheNextOutput()
        {
            var paths = new List<string>(Fixtures.AudioPaths("wav", "flac", "aiff"));
            Assert.That(paths.Count, Is.GreaterThanOrEqualTo(5));
            List<Stem> stems = StemsToSp16.Read(paths);
            for (int i = 0; i < stems.Count; i++)
            {
                Assert.That(stems[i].Name, Is.EqualTo(Path.GetFileNameWithoutExtension(paths[i])));
                Assert.That(stems[i].Output, Is.EqualTo((Sp16Output)(i % 4)));
                Assert.That(stems[i].Audio.Frames, Is.GreaterThan(0));
            }
        }

        [Test]
        public void Stems_EachLayerKnowsWhichStemsItIsMadeOf()
        {
            WavData tone = Tone(Rate, 440, 3000);
            var source = new List<Stem>
            {
                new Stem { Name = "Kick", Audio = tone, Output = Sp16Output.Out12, Group = 0 },
                new Stem { Name = "Snare", Audio = tone, Output = Sp16Output.Out12, Group = 0 },
                new Stem { Name = "Bass", Audio = tone, Output = Sp16Output.Out34, Group = 1, KeepApart = true },
                new Stem { Name = "Sub", Audio = tone, Output = Sp16Output.Out34, Group = 1, KeepApart = true },
                new Stem { Name = "Hats", Audio = tone, Output = Sp16Output.Out12, Group = 0 }
            };
            StemSet set = StemsToSp16.Prepare(source);
            Assert.That(set.Layers.ConvertAll(layer => layer.Label), Is.EqualTo(new[] { "Mix12", "Bass", "Sub" }));
            Assert.That(set.LayerStems, Is.EqualTo(new[] { new[] { 0, 1, 4 }, new[] { 2 }, new[] { 3 } }));
            Assert.That(StemsToSp16.Prepare(source, false).LayerStems, Is.EqualTo(new[] { new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 } }));
        }

        [Test]
        public void Stems_SummingIsTheSameSampleForSampleAsAddingThemOneByOne()
        {
            // Sums are made in blocks side by side; every sample must come out as a plain sum,
            // held at the limit, would make it. Odd lengths so that no block ends neatly.
            var random = new Random(7);
            var parts = new short[3][];
            for (int p = 0; p < parts.Length; p++)
            {
                parts[p] = new short[2 * 10007];
                for (int i = 0; i < parts[p].Length; i++)
                    parts[p][i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
            }
            int clipped;
            short[] sum = StemsToSp16.Add(parts, out clipped);
            int expectedClipped = 0;
            for (int i = 0; i < sum.Length; i++)
            {
                int value = parts[0][i] + parts[1][i] + parts[2][i];
                if (value > short.MaxValue || value < short.MinValue)
                    expectedClipped++;
                Assert.That(sum[i], Is.EqualTo((short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value))), "sample " + i);
            }
            Assert.That(clipped, Is.EqualTo(expectedClipped));
            Assert.That(clipped, Is.GreaterThan(0));
        }

        [Test]
        public void Stems_ClipsAudiblyOnlyWhenClippingIsDenseEnoughToHear()
        {
            Func<int, short[]> silence = frames => new short[2 * frames];
            // Left side held over full scale for this many frames, from frame 1000.
            Func<int, short[]> burst = frames =>
            {
                short[] samples = silence(4000);
                for (int f = 1000; f < 1000 + frames; f++)
                    samples[2 * f] = 20000;
                return samples;
            };
            Assert.That(StemsToSp16.ClipsAudibly(new[] { burst(43), burst(43) }), Is.False);
            Assert.That(StemsToSp16.ClipsAudibly(new[] { burst(44), burst(44) }), Is.True);
            // Wherever a run falls against the windows it is found.
            for (int offset = 0; offset < StemsToSp16.ClipWindowFrames; offset += 37)
            {
                short[] a = silence(4000);
                for (int f = 500 + offset; f < 544 + offset; f++)
                    a[2 * f + 1] = -20000;
                Assert.That(StemsToSp16.ClipsAudibly(new[] { a, a }), Is.True, "offset " + offset);
            }
            // Short clips far apart, as the tips of hits: not heard.
            short[] hits = silence(44100);
            for (int f = 0; f < 44100; f += 2205)
                for (int k = 0; k < 4; k++)
                    hits[2 * (f + k)] = 20000;
            Assert.That(StemsToSp16.ClipsAudibly(new[] { hits, hits }), Is.False);
            // The lowest value 16 bits hold is not past them.
            short[] low = silence(1000);
            for (int i = 0; i < low.Length; i++)
                low[i] = -16384;
            Assert.That(StemsToSp16.ClipsAudibly(new[] { low, low }), Is.False);
            // A shorter part counts as silence past its end.
            Assert.That(StemsToSp16.ClipsAudibly(new[] { burst(100), silence(500) }), Is.False);

            // A steady tone a little too loud clips on every wave, and is heard.
            WavData tone = Tone(Rate, 440, 17000);
            short[] both = (short[])tone.Samples.Clone();
            for (int f = 0; f < Rate; f++)
                both[2 * f + 1] = both[2 * f];
            Assert.That(StemsToSp16.ClipsAudibly(new[] { both, both }), Is.True);
            var stems = new List<Stem>
            {
                new Stem { Name = "A", Audio = new WavData { SampleRate = Rate, Samples = both, SourceChannels = 2, SourceBitsPerSample = 16 }, Output = Sp16Output.Out12 },
                new Stem { Name = "B", Audio = new WavData { SampleRate = Rate, Samples = both, SourceChannels = 2, SourceBitsPerSample = 16 }, Output = Sp16Output.Out12 }
            };
            Assert.That(StemsToSp16.Prepare(stems).Warnings, Has.Some.Contains("too loud"));
            stems[1].Audio = new WavData { SampleRate = Rate, Samples = hits.Length == 2 * Rate ? hits : silence(Rate), SourceChannels = 2, SourceBitsPerSample = 16 };
            stems[0].Audio = stems[1].Audio;
            Assert.That(StemsToSp16.Prepare(stems).Warnings, Has.None.Contains("too loud"));
        }

        [Test]
        public void MostTracksFor_SharesOutTheScenesPadsAmongThePatterns()
        {
            // Sixteen scenes of sixteen pads: a pattern of N tracks leaves room for 16 / N patterns to a scene.
            Assert.That(WavToSp16.MostTracksFor(1), Is.EqualTo(16));
            Assert.That(WavToSp16.MostTracksFor(16), Is.EqualTo(16));
            Assert.That(WavToSp16.MostTracksFor(17), Is.EqualTo(8));
            Assert.That(WavToSp16.MostTracksFor(27), Is.EqualTo(8));
            Assert.That(WavToSp16.MostTracksFor(32), Is.EqualTo(8));
            Assert.That(WavToSp16.MostTracksFor(33), Is.EqualTo(5));
            Assert.That(WavToSp16.MostTracksFor(64), Is.EqualTo(4));
            Assert.That(WavToSp16.MostTracksFor(65), Is.EqualTo(3));
            Assert.That(WavToSp16.MostTracksFor(256), Is.EqualTo(1));
            Assert.That(WavToSp16.MostTracksFor(257), Is.EqualTo(0));
            Assert.That(WavToSp16.TooManyTracks(12, 27, true), Is.EqualTo("Too large to fit with 12 tracks. Fits with 8 or fewer. Sum stems or remove tracks."));
            Assert.That(WavToSp16.TooManyTracks(2, 300, false), Is.EqualTo("Too large to fit on SP-16."));
        }

        [Test]
        public void MixOf_AddsFilesOfAnyLengthAndTurnsDownASumThatWouldNotFit()
        {
            WavData tone = Tone(Rate, 440, 20000);
            Assert.That(StemsToSp16.MixOf(new[] { tone }), Is.SameAs(tone));

            WavData quiet = Tone(Rate / 2, 440, 1000);
            WavData sum = StemsToSp16.MixOf(new[] { quiet, Tone(Rate, 220, 1000) });
            Assert.That(sum.Frames, Is.EqualTo(Rate));
            Assert.That(sum.Samples[2 * 100], Is.EqualTo(quiet.Samples[2 * 100] + Tone(Rate, 220, 1000).Samples[2 * 100]));

            // Two at 20000 would reach 40000: the whole is turned down so that its loudest point fits.
            WavData loud = StemsToSp16.MixOf(new[] { tone, tone });
            int peak = 0;
            foreach (short sample in loud.Samples)
                peak = Math.Max(peak, Math.Abs((int)sample));
            Assert.That(peak, Is.InRange(32000, 32767));
        }
    }
}
