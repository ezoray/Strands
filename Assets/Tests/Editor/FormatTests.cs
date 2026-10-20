using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Strands.Sp16;

namespace Strands.Tests
{
    /// <summary>
    /// The format library against the unit-saved fixtures. Test names carry the section of
    /// docs/SP-16_DJS-1000_project_format_spec.md they check.
    /// </summary>
    public class FormatTests
    {
        static IEnumerable<string> Projects() { return Fixtures.ProjectNames(); }
        static IEnumerable<string> Packages() { return Fixtures.PackageNames(); }

        [Test]
        public void Fixtures_ArePresent()
        {
            var names = new List<string>(Fixtures.ProjectNames());
            Assert.That(names, Has.Member("arr0.prj"));
            Assert.That(names, Has.Member("rev1.tpkg/rev1.prj"));
            Assert.That(names.Count, Is.GreaterThanOrEqualTo(24));
        }

        [TestCaseSource(nameof(Projects))]
        public void S1_S2_Project_ParsesFullyAndReEmitsIdenticalPayload(string name)
        {
            byte[] payload = PrjFile.ReadPayload(Fixtures.ReadProjectBytes(name));
            PrjNode root = PrjTree.Parse(payload);
            Assert.That(root.Name, Is.EqualTo("ProjectData"));
            CollectionAssert.AreEqual(payload, PrjTree.Emit(root));
        }

        [TestCaseSource(nameof(Projects))]
        public void S1_Container_WrittenFileReadsBackToSamePayload(string name)
        {
            byte[] payload = PrjFile.ReadPayload(Fixtures.ReadProjectBytes(name));
            byte[] written = PrjFile.WritePayload(payload);
            CollectionAssert.AreEqual(payload, PrjFile.ReadPayload(written));
        }

        [Test]
        public void S1_Container_RejectsCorruptChecksum()
        {
            byte[] file = Fixtures.Read("arr0.prj");
            file[file.Length - 1] ^= 0xFF;
            Assert.Throws<InvalidDataException>(() => PrjFile.ReadPayload(file));
        }

        [TestCaseSource(nameof(Projects))]
        public void S5_S6_Blobs_InflateAndRecompressToSameContents(string name)
        {
            PrjNode root = PrjFile.Read(Fixtures.ReadProjectBytes(name));
            int blobs = 0;
            Visit(root, scalar =>
            {
                if (scalar.IsNull || scalar.Type != PrjType.Blob)
                    return;
                blobs++;
                byte[] inflated = Zlib.Decompress(scalar.Raw, 1, scalar.Raw.Length - 1);
                CollectionAssert.AreEqual(inflated, Zlib.Decompress(Zlib.Compress(inflated)));
            });
            Assert.That(blobs, Is.GreaterThan(0));
        }

        [Test]
        public void S2_VariableWidthNumbers_UseTheMinimalWidth()
        {
            Assert.That(Vi(0), Is.EqualTo(new byte[] { 0 }));
            Assert.That(Vi(1), Is.EqualTo(new byte[] { 1, 1 }));
            Assert.That(Vi(255), Is.EqualTo(new byte[] { 1, 255 }));
            Assert.That(Vi(256), Is.EqualTo(new byte[] { 2, 0, 1 }));
            // The Steps length from the spec: 691 is stored as 02 b3 02.
            Assert.That(Vi(691), Is.EqualTo(new byte[] { 2, 0xB3, 0x02 }));
        }

        [Test]
        public void S2_Scalars_TypedValuesRoundTrip()
        {
            var scalar = new PrjScalar("x", null);
            scalar.SetInt32(-127);
            Assert.That(scalar.AsInt32(), Is.EqualTo(-127));
            scalar.SetFloat64(133.28);
            Assert.That(scalar.AsFloat64(), Is.EqualTo(133.28));
            scalar.SetString("$Int0/%Tmp/a.wav");
            Assert.That(scalar.AsString(), Is.EqualTo("$Int0/%Tmp/a.wav"));
            scalar.SetString("");
            Assert.That(scalar.Raw, Is.EqualTo(new byte[] { 5, 0 }));
            scalar.SetBool(true);
            Assert.That(scalar.AsBool(), Is.True);
        }

        [TestCaseSource("Projects")]
        public void S4_SliceGrids_AreBoundariesFromZeroToTheSampleLength(string name)
        {
            var project = Sp16Project.FromPrj(Fixtures.ReadProjectBytes(name));
            for (int scene = 0; scene < Sp16Project.Scenes; scene++)
            {
                for (int pad = 0; pad < Sp16Project.PadsPerScene; pad++)
                {
                    PrjNode track = project.Pad(scene, pad);
                    PrjScalar grids = track.Child("SliceData").Scalar("grids");
                    int[] stored = grids.AsInt32Array();
                    if (stored.Length == 0)
                        continue;

                    string where = "scene " + scene + " pad " + pad;
                    Assert.That(stored.Length, Is.EqualTo(track.Child("SliceData").Scalar("nGrids").AsInt32() + 1), where);
                    Assert.That(stored[0], Is.EqualTo(0), where);
                    Assert.That(stored[stored.Length - 1], Is.EqualTo(track.Child("PlaybackData").Scalar("lengthSample").AsInt32()), where);
                    Assert.That(stored, Is.Ordered.Ascending, where);

                    var rewritten = new PrjScalar("grids", null);
                    rewritten.SetInt32Array(stored);
                    Assert.That(rewritten.Raw, Is.EqualTo(grids.Raw), where);
                }
            }
        }

        [Test]
        public void S4_SliceGrids_UneditedSlicesAreEqualAndStoreNothing()
        {
            var project = Sp16Project.FromPrj(Fixtures.Read("slice-count-8.prj"));
            PrjNode slice = project.Pad(0, 0).Child("SliceData");
            Assert.That(slice.Scalar("nGrids").AsInt32(), Is.EqualTo(8));
            Assert.That(slice.Scalar("grids").Raw, Is.EqualTo(new byte[] { PrjType.Array, 0 }));
            Assert.That(project.GetSliceBoundaries(0, 0), Is.EqualTo(Sp16Project.EqualSliceBoundaries(81415, 8)));
        }

        [Test]
        public void S4_SliceGrids_AMovedPointChangesOnlyItsOwnBoundary()
        {
            int[] equal = Sp16Project.EqualSliceBoundaries(81415, 16);

            // The start of slice 2 was moved to 9300 on the unit.
            int[] one = Sp16Project.FromPrj(Fixtures.Read("slice-point-moved.prj")).GetSliceBoundaries(0, 0);
            int[] expected = (int[])equal.Clone();
            expected[1] = 9300;
            Assert.That(one, Is.EqualTo(expected));

            // Then the end of slice 2, which is the start of slice 3, was moved to 12985.
            int[] two = Sp16Project.FromPrj(Fixtures.Read("slice-two-points-moved.prj")).GetSliceBoundaries(0, 0);
            expected[2] = 12985;
            Assert.That(two, Is.EqualTo(expected));
        }

        [Test]
        public void S4_SliceGrids_WrittenBoundariesMatchTheUnitsBytes()
        {
            var unit = Sp16Project.FromPrj(Fixtures.Read("slice-point-moved.prj"));
            var made = Sp16Project.FromPrj(Fixtures.Read("slice-count-8.prj"));
            made.SetSliceBoundaries(0, 0, unit.GetSliceBoundaries(0, 0));

            PrjNode expected = unit.Pad(0, 0).Child("SliceData");
            PrjNode actual = made.Pad(0, 0).Child("SliceData");
            Assert.That(actual.Scalar("grids").Raw, Is.EqualTo(expected.Scalar("grids").Raw));
            Assert.That(actual.Scalar("nGrids").AsInt32(), Is.EqualTo(16));

            Assert.Throws<ArgumentException>(() => made.SetSliceBoundaries(0, 0, new[] { 0, 500, 400, 81415 }));
            Assert.Throws<ArgumentException>(() => made.SetSliceBoundaries(0, 0, new[] { 0, 500, 81414 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => made.SetSliceBoundaries(0, 0, Sp16Project.EqualSliceBoundaries(81415, 17)));
        }

        [Test]
        public void S6_EmptyArrangement_Is999DefaultRecords()
        {
            Arrangement arrangement = Arrangement.FromNode(Fixtures.ReadProject("arr0.prj").Child("ArrangementData0"));
            Assert.That(arrangement.Length, Is.EqualTo(0));
            CollectionAssert.AreEqual(arrangement.ToInflated(), Arrangement.CreateEmpty().ToInflated());
            Assert.That(arrangement.ToInflated().Length, Is.EqualTo(7992));
        }

        [TestCase("arr2-repeat2.prj", "00 00 02 00 00 00 00 00")]
        [TestCase("arr3-repeat-big.prj", "00 00 20 00 00 00 00 00")]
        [TestCase("arr4-scene2.prj", "01 00 20 00 00 00 00 00")]
        [TestCase("arr5-pattern2.prj", "01 01 20 00 00 00 00 00")]
        [TestCase("arr6-bpm-whole.prj", "01 01 20 00 00 00 05 43")]
        [TestCase("arr6-bpm-decimal.prj", "01 01 20 00 CD 4C 05 43")]
        public void S6_SectionRecord_StoresSceneRepeatPatternAndTempo(string fixture, string expectedHex)
        {
            Arrangement arrangement = Arrangement.FromNode(Fixtures.ReadProject(fixture).Child("ArrangementData0"));
            Assert.That(arrangement.Length, Is.EqualTo(1));
            byte[] record = new byte[8];
            Buffer.BlockCopy(arrangement.ToInflated(), 0, record, 0, 8);
            Assert.That(BitConverter.ToString(record).Replace('-', ' '), Is.EqualTo(expectedHex));

            // Writing the decoded section back must give the same bytes.
            Arrangement rebuilt = Arrangement.CreateEmpty();
            rebuilt.Add(arrangement[0]);
            CollectionAssert.AreEqual(arrangement.ToInflated(), rebuilt.ToInflated());
        }

        [Test]
        public void S6_Tempo_IsFloat32()
        {
            Assert.That(Arrangement.FromNode(Fixtures.ReadProject("arr6-bpm-whole.prj").Child("ArrangementData0"))[0].Bpm, Is.EqualTo(133f));
            Assert.That(Arrangement.FromNode(Fixtures.ReadProject("arr6-bpm-decimal.prj").Child("ArrangementData0"))[0].Bpm, Is.EqualTo(133.3f));
        }

        [Test]
        public void S6_Editing_InsertDeleteAndClearMatchTheUnit()
        {
            Arrangement aba = LoadArrangement("arr10-aba.prj");
            Assert.That(aba.Length, Is.EqualTo(3));

            // arr11 is arr10 with a new section inserted second.
            aba.Insert(1, Section.Default);
            CollectionAssert.AreEqual(LoadArrangement("arr11-insert.prj").ToInflated(), aba.ToInflated());
            Assert.That(aba.Length, Is.EqualTo(4));

            // arr12 is arr11 with that section deleted again.
            aba.Delete(1);
            CollectionAssert.AreEqual(LoadArrangement("arr12-delete.prj").ToInflated(), aba.ToInflated());
            Assert.That(aba.Length, Is.EqualTo(3));

            aba.Clear();
            CollectionAssert.AreEqual(LoadArrangement("arr13-clear.prj").ToInflated(), aba.ToInflated());
            Assert.That(aba.Length, Is.EqualTo(0));
        }

        [Test]
        public void S6_Arrangement_TwoSectionsAreStoredInPlayOrder()
        {
            Arrangement two = LoadArrangement("arr9-two-sections.prj");
            Assert.That(two.Length, Is.EqualTo(2));
            Assert.That(two[0].Scene, Is.EqualTo(0));
            Assert.That(two[0].Pattern, Is.EqualTo(1));
            Assert.That(two[1].Scene, Is.EqualTo(1));
            Assert.That(two[1].Pattern, Is.EqualTo(0));
        }

        [Test]
        public void S5_StepsBlob_Pad5Step9IsRecord264AndTheRestIsDefault()
        {
            PrjNode pattern = Fixtures.ReadProject("arr7-pattern2-step.prj").Child("SceneData0").Child("PatternData1");
            StepsBlob fromUnit = StepsBlob.FromScalar(pattern.Scalar("Steps"));
            Assert.That(fromUnit.ToInflated().Length, Is.EqualTo(155648));

            List<KeyValuePair<int, int>> triggers = fromUnit.GetTriggers();
            Assert.That(triggers.Count, Is.EqualTo(1));
            Assert.That(triggers[0].Key * 64 + triggers[0].Value, Is.EqualTo(264));

            // Built from the spec's description, with the one trigger placed, it equals the unit's blob.
            StepsBlob built = StepsBlob.CreateEmpty();
            built.SetTrigger(4, 8);
            CollectionAssert.AreEqual(fromUnit.ToInflated(), built.ToInflated());

            built.ClearTrigger(4, 8);
            CollectionAssert.AreEqual(StepsBlob.CreateEmpty().ToInflated(), built.ToInflated());
        }

        static StepsBlob StepsOf(string fixture, int pattern)
        {
            return StepsBlob.FromScalar(Fixtures.ReadProject(fixture).Child("SceneData0").Child("PatternData" + pattern).Scalar("Steps"));
        }

        // steps-1.prj was saved on the DJS-1000 with one setting on each beat of pattern 1, track 1,
        // over a 4-bar sample sliced into 16 slices of 22050 frames. Beat n is step 4n - 3.
        [Test]
        public void S5_StepSettings_ReadAsTheUnitShowedThem()
        {
            StepsBlob steps = StepsOf("steps-1.prj", 0);
            Func<int, StepParameter, int?> at = (beat, p) => steps.GetParameter(0, (beat - 1) * 4, p);

            // Beat 2 plays slice 3: the slice's boundaries, not its number.
            Assert.That(at(2, StepParameter.Start), Is.EqualTo(44100));
            Assert.That(at(2, StepParameter.LoopStart), Is.EqualTo(44100));
            Assert.That(at(2, StepParameter.Length), Is.EqualTo(22050));
            Assert.That(at(3, StepParameter.Start), Is.EqualTo(220500));

            Assert.That(at(4, StepParameter.Attack), Is.EqualTo(StepsBlob.KnobValue(5)));
            Assert.That(at(5, StepParameter.Hold), Is.EqualTo(StepsBlob.KnobValue(99)));
            Assert.That(at(6, StepParameter.Release), Is.EqualTo(StepsBlob.KnobValue(15)));
            Assert.That(at(7, StepParameter.RetriggerCount), Is.EqualTo(2));
            Assert.That(at(8, StepParameter.RetriggerCount), Is.EqualTo(4));
            Assert.That(at(8, StepParameter.RetriggerSpeed), Is.EqualTo(4)); // 1/16
            Assert.That(at(9, StepParameter.Offset), Is.EqualTo(21));
            Assert.That(at(11, StepParameter.Pitch), Is.EqualTo(700));
            Assert.That(at(12, StepParameter.Pitch), Is.EqualTo(-500));
            Assert.That(at(16, StepParameter.Velocity), Is.EqualTo(91));
            Assert.That(steps.GetParameter(0, 63, StepParameter.LfoSpeed), Is.EqualTo(StepsBlob.KnobValue(13)));

            // START set by its knob leaves loop start and length to the track.
            Assert.That(at(13, StepParameter.Start), Is.EqualTo(15180));
            Assert.That(at(13, StepParameter.Length), Is.Null);
            Assert.That(at(14, StepParameter.Length), Is.EqualTo(130198));
            Assert.That(at(14, StepParameter.Start), Is.Null);

            Assert.That(steps.GetTriggerKind(0, 0), Is.EqualTo(StepTrigger.Full));
            Assert.That(steps.GetTriggerKind(0, 36), Is.EqualTo(StepTrigger.Half));
            Assert.That(steps.GetTriggerKind(0, 40), Is.EqualTo(StepTrigger.Param));
        }

        [Test]
        public void S5_StepSettings_WrittenByTheLibraryMatchTheUnitsBytes()
        {
            StepsBlob unit = StepsOf("steps-1.prj", 0);
            // Beat 15 holds a setting of the insert effect, which the library does not write.
            unit.ClearTrigger(0, 56);

            StepsBlob built = StepsBlob.CreateEmpty();
            Action<int, StepTrigger> trigger = (step, kind) => built.SetTrigger(0, step, kind);
            int[] slice = { 0, 0, 44100, 220500, 220500, 220500, 220500, 0, 0, 0, 0, 0, 0 };
            for (int beat = 1; beat <= 12; beat++)
            {
                int step = (beat - 1) * 4;
                StepTrigger kind = beat == 10 ? StepTrigger.Half : beat == 11 ? StepTrigger.Param : StepTrigger.Full;
                trigger(step, kind);
                built.SetPlayRange(0, step, slice[beat], 22050);
            }
            built.SetParameter(0, 12, StepParameter.Attack, StepsBlob.KnobValue(5));
            built.SetParameter(0, 16, StepParameter.Hold, StepsBlob.KnobValue(99));
            built.SetParameter(0, 20, StepParameter.Release, StepsBlob.KnobValue(15));
            built.SetParameter(0, 24, StepParameter.RetriggerCount, 2);
            built.SetParameter(0, 28, StepParameter.RetriggerCount, 4);
            built.SetParameter(0, 28, StepParameter.RetriggerSpeed, 4);
            built.SetParameter(0, 32, StepParameter.Offset, 21);
            built.SetParameter(0, 40, StepParameter.Pitch, 700);
            built.SetParameter(0, 44, StepParameter.Pitch, -500);
            trigger(48, StepTrigger.Full);
            built.SetParameter(0, 48, StepParameter.Start, 15180);
            trigger(52, StepTrigger.Full);
            built.SetParameter(0, 52, StepParameter.Length, 130198);
            trigger(60, StepTrigger.Full);
            built.SetParameter(0, 60, StepParameter.Velocity, 91);
            trigger(63, StepTrigger.Full);
            built.SetParameter(0, 63, StepParameter.LfoSpeed, StepsBlob.KnobValue(13));

            CollectionAssert.AreEqual(unit.ToInflated(), built.ToInflated());

            built.ClearParameter(0, 63, StepParameter.LfoSpeed);
            Assert.That(built.GetParameter(0, 63, StepParameter.LfoSpeed), Is.Null);
        }

        [Test]
        public void S5_StepSettings_AStepKeepsItsStartWhenTheSliceIsMoved()
        {
            // steps-2.prj is steps-1 with slice 3's start moved from 44100 to 50182 after beat 2 was set to it.
            Sp16Project moved = Sp16Project.FromPrj(Fixtures.Read("steps-2.prj"));
            Assert.That(moved.GetSliceBoundaries(0, 0)[2], Is.EqualTo(50182));
            Assert.That(StepsOf("steps-2.prj", 0).GetParameter(0, 4, StepParameter.Start), Is.EqualTo(44100));
        }

        [Test]
        public void S5_Pattern_LengthIsTotalStepsAndScalarsAreInUnitOrder()
        {
            PrjNode pattern = Fixtures.ReadProject("arr8-length.prj").Child("SceneData0").Child("PatternData1");
            // 2 bars with 5 beats in the second: 16 * (2 - 1) + 5.
            Assert.That(pattern.Scalar("Length").AsInt32(), Is.EqualTo(21));
            Assert.That(ScalarNames(pattern), Is.EqualTo(new[] { "Steps", "Length", "TrackMutes", "TrackSolos" }));
        }

        [Test]
        public void S3_Scene_PatternSitsAfterThePadsAndBeforeSendFx()
        {
            PrjNode scene = Fixtures.ReadProject("arr7-pattern2-step.prj").Child("SceneData0");
            Assert.That(scene.Children[15].Name, Is.EqualTo("TrackData15"));
            Assert.That(scene.Children[16].Name, Is.EqualTo("PatternData1"));
            Assert.That(scene.Children[17].Name, Is.EqualTo("SendFx"));
        }

        [Test]
        public void S5_SetPattern_ReproducesTheUnitsPatternStruct()
        {
            byte[] expected = PrjFile.ReadPayload(Fixtures.Read("arr7-pattern2-step.prj"));

            // Take the unit's own file, remove its pattern, and put the same pattern back through the library.
            Sp16Project project = Sp16Project.FromPrj(Fixtures.Read("arr7-pattern2-step.prj"));
            project.RemovePatterns(0);
            Assert.That(project.FindPattern(0, 1), Is.Null);
            StepsBlob steps = StepsBlob.CreateEmpty();
            steps.SetTrigger(4, 8);
            project.SetPattern(0, 1, steps, 16);

            AssertSameTree(PrjTree.Parse(expected), project.Root, "");
        }

        [Test]
        public void S7_Routing_8ChStringsMatchTheUnit()
        {
            PrjNode expected = Fixtures.ReadProject("route1.prj").Child("SceneData0").Child("RoutingData");

            // route1 differs from the default only in scene 1's routing, so start from the default scene 2.
            Sp16Project project = Sp16Project.FromPrj(Fixtures.Read("route1.prj"));
            var outputs = new string[16];
            for (int pad = 0; pad < 16; pad++)
                outputs[pad] = "AssignTo12";
            outputs[1] = "AssignTo34";
            outputs[2] = "AssignTo56";
            outputs[3] = "AssignTo78";
            project.SetRouting8Ch(1, outputs);

            AssertSameTree(expected, project.Scene(1).Child("RoutingData"), "RoutingData");
        }

        [Test]
        public void S7_Routing_IsPerScene()
        {
            PrjNode root = Fixtures.ReadProject("route1.prj");
            Assert.That(root.Child("SceneData0").Child("RoutingData").Scalar("assignMode").AsString(), Is.EqualTo("4Assign"));
            Assert.That(root.Child("SceneData0").Child("RoutingData").Scalar("headphoneOutCh").AsString(), Is.EqualTo("Cue12"));
            for (int scene = 1; scene < 16; scene++)
                Assert.That(root.Child("SceneData" + scene).Child("RoutingData").Scalar("assignMode").AsString(), Is.EqualTo("Master"));

            PrjNode mono = Fixtures.ReadProject("route2.prj").Child("SceneData0").Child("RoutingData");
            Assert.That(mono.Child("TrackRoutingData2").Scalar("assignableOutCh").AsString(), Is.EqualTo("AssignTo5"));
            Assert.That(mono.Child("TrackRoutingData3").Scalar("assignableOutCh").AsString(), Is.EqualTo("AssignTo6"));
        }

        [Test]
        public void S7_Routing_SingleOutputsIn8ChModeReproduceTheUnitsFile()
        {
            // Saved on the SP-16 as mono-tracks: scene 1 in 8ch mode with tracks 1 to 8 sent to outputs
            // 1 to 8 one at a time. The unit put the other eight on the first pair by itself.
            PrjNode expected = Fixtures.ReadProject("route3-mono-8ch.prj");
            PrjNode routing = expected.Child("SceneData0").Child("RoutingData");
            Assert.That(routing.Scalar("assignMode").AsString(), Is.EqualTo("4Assign"));
            Assert.That(routing.Scalar("headphoneOutCh").AsString(), Is.EqualTo("Cue12"));
            Sp16Output[] singles =
            {
                Sp16Output.Out1, Sp16Output.Out2, Sp16Output.Out3, Sp16Output.Out4,
                Sp16Output.Out5, Sp16Output.Out6, Sp16Output.Out7, Sp16Output.Out8
            };
            var outputs = new string[16];
            for (int pad = 0; pad < 16; pad++)
            {
                outputs[pad] = Sp16Project.OutputValue(pad < 8 ? singles[pad] : Sp16Output.Out12);
                Assert.That(routing.Child("TrackRoutingData" + pad).Scalar("assignableOutCh").AsString(), Is.EqualTo(pad < 8 ? "AssignTo" + (pad + 1) : "AssignTo12"));
                Assert.That(pad < 8 ? Sp16Project.IsSingleOutput(singles[pad]) : !Sp16Project.IsSingleOutput(Sp16Output.Out12));
            }

            // The whole file is the empty project with that routing and its own name, and nothing else.
            Sp16Project project = Sp16Project.FromPrj(Fixtures.Read("arr0.prj"));
            project.SetProjectName("mono-tracks");
            project.SetRouting8Ch(0, outputs);
            AssertSameTree(expected, project.Root, "");
            AssertSameTree(expected, PrjFile.Read(project.ToPrj()), "");
        }

        [Test]
        public void S7_Routing_OutputNamesAndValuesCoverThePairsAndTheSingleOutputs()
        {
            Assert.That(Sp16Project.OutputValue(Sp16Output.Out12), Is.EqualTo("AssignTo12"));
            Assert.That(Sp16Project.OutputValue(Sp16Output.Out78), Is.EqualTo("AssignTo78"));
            Assert.That(Sp16Project.OutputName(Sp16Output.Out34), Is.EqualTo("3-4"));
            Assert.That(Sp16Project.OutputName(Sp16Output.Out6), Is.EqualTo("6"));
            // The pairs keep the first four places: stems are given them in order by default.
            Assert.That((int)Sp16Output.Out78, Is.EqualTo(Sp16Project.OutputPairs - 1));
            var values = new HashSet<string>();
            foreach (Sp16Output output in Enum.GetValues(typeof(Sp16Output)))
                Assert.That(values.Add(Sp16Project.OutputValue(output)), output + " shares its value with another output");
            // Twelve in 8ch mode, and the master output.
            Assert.That(values.Count, Is.EqualTo(13));
        }

        [Test]
        public void S7_Routing_TheEmptyProjectHasEveryPadOnTheMasterOutput()
        {
            // The project every export is patched from: all sixteen scenes in the mode the unit
            // starts in, with no pad sent anywhere. It is what an export on master leaves alone.
            PrjNode root = Fixtures.ReadProject("arr0.prj");
            for (int scene = 0; scene < 16; scene++)
            {
                PrjNode routing = root.Child("SceneData" + scene).Child("RoutingData");
                Assert.That(routing.Scalar("assignMode").AsString(), Is.EqualTo("Master"));
                Assert.That(routing.Scalar("headphoneOutCh").AsString(), Is.EqualTo("CueMaster"));
                for (int pad = 0; pad < 16; pad++)
                    Assert.That(routing.Child("TrackRoutingData" + pad).Scalar("assignableOutCh").AsString(), Is.EqualTo(Sp16Project.OutputValue(Sp16Output.Master)));
            }
            Assert.That(Sp16Project.OutputValue(Sp16Output.Master), Is.EqualTo("AssignToNull"));
            Assert.That(Sp16Project.OutputName(Sp16Output.Master), Is.EqualTo("master"));
            Assert.That(Sp16Project.IsSingleOutput(Sp16Output.Master), Is.False);
            // It is last, so the pairs and the single outputs are where they always were.
            Assert.That((int)Sp16Output.Master, Is.EqualTo(12));
        }

        [Test]
        public void S8_Adjunct_WritesTheSameBytesAsTheFixture()
        {
            byte[] expected = Fixtures.ReadEntry("rev1.tpkg", "rev1_A1.wav.dat");
            SampleAdjunct adjunct = SampleAdjunct.Read(expected);
            Assert.That(adjunct.Bar, Is.EqualTo(2.0));
            Assert.That(adjunct.Bpm, Is.EqualTo(0.0));
            Assert.That(adjunct.SampleNum, Is.EqualTo(17640));
            CollectionAssert.AreEqual(expected, SampleAdjunct.ForSample(17640).Write());
        }

        [TestCaseSource(nameof(Packages))]
        public void S9_Package_IsFlatAndPairsEverySampleWithADat(string package)
        {
            List<StoredZipReader.Entry> entries = StoredZipReader.Read(Fixtures.Read(package));
            var names = new List<string>();
            foreach (StoredZipReader.Entry entry in entries)
            {
                Assert.That(entry.Name, Does.Not.Contain("/"));
                Assert.That(entry.Method, Is.EqualTo(0), entry.Name + " should be stored");
                names.Add(entry.Name);
            }
            Assert.That(names[0], Does.EndWith(".prj"));
            foreach (string name in names)
                if (name.EndsWith(".wav"))
                    Assert.That(names, Has.Member(name + ".dat"));
        }

        [Test]
        public void S9_PackageWriter_ReproducesTheConfirmedPackageByteForByte()
        {
            byte[] expected = Fixtures.Read("rev1.tpkg");
            using (var output = new MemoryStream())
            {
                using (var zip = new StoredZipWriter(output))
                    foreach (StoredZipReader.Entry entry in StoredZipReader.Read(expected))
                        zip.Add(entry.Name, entry.Data);
                CollectionAssert.AreEqual(expected, output.ToArray());
            }
        }

        [Test]
        public void S11_Patching_ChangesOnlyTheIntendedFields()
        {
            PrjNode original = Fixtures.ReadProject("arr0.prj");
            Sp16Project project = Sp16Project.FromPrj(Fixtures.Read("arr0.prj"));
            project.SetPadSample(2, 5, "a.wav", 1234);

            var differences = new List<string>();
            CollectDifferences(original, project.Root, "", differences);
            Assert.That(differences, Is.EquivalentTo(new[]
            {
                "/SceneData2/TrackData5/PlaybackData.lengthSample",
                "/SceneData2/TrackData5/PlaybackData.audioSourceUrl"
            }));
        }

        [Test]
        public void S11_Generation_ReproducesTheConfirmedProjectFromTheEmptyTemplate()
        {
            PrjNode expected = PrjFile.Read(Fixtures.ReadEntry("rev1.tpkg", "rev1.prj"));

            Sp16Project project = Sp16Project.FromPrj(Fixtures.Read("arr0.prj"));
            project.PlayArrangement = true;
            project.SetProjectName("rev1");
            var outputs = new string[16];
            for (int pad = 0; pad < 16; pad++)
                outputs[pad] = "AssignTo12";
            outputs[1] = "AssignTo34";
            outputs[2] = "AssignTo56";
            outputs[3] = "AssignTo78";

            string[] sceneNames = { "SceneA", "SceneB" };
            int[][] triggerSteps = { new[] { 0, 4, 8, 12 }, new[] { 12, 8, 4, 0 } };
            for (int scene = 0; scene < 2; scene++)
            {
                project.SetSceneName(scene, sceneNames[scene]);
                StepsBlob steps = StepsBlob.CreateEmpty();
                for (int pad = 0; pad < 4; pad++)
                {
                    project.SetPadSample(scene, pad, "rev1_" + "AB"[scene] + (pad + 1) + ".wav", 17640);
                    steps.SetTrigger(pad, triggerSteps[scene][pad]);
                }
                project.SetPattern(scene, scene, steps, 16);
                project.SetRouting8Ch(scene, outputs);
            }

            Arrangement arrangement = Arrangement.CreateEmpty();
            arrangement.Add(new Section(0, 0, 2, 0f));
            arrangement.Add(new Section(1, 1, 1, 0f));
            arrangement.Add(new Section(0, 0, 1, 140f));
            project.SetArrangement(arrangement);

            // rev1 wrote the pattern scalars in a different order from the unit, and its blobs were
            // compressed by another zlib, so patterns and blobs are compared by content.
            AssertSameTree(expected, PrjFile.Read(project.ToPrj()), "");
        }

        static Arrangement LoadArrangement(string fixture)
        {
            return Arrangement.FromNode(Fixtures.ReadProject(fixture).Child("ArrangementData0"));
        }

        static byte[] Vi(int value)
        {
            // A struct with no name, `value` null scalars and no children starts 00, then the count.
            var node = new PrjNode("");
            for (int i = 0; i < value; i++)
                node.Scalars.Add(new PrjScalar("", null));
            byte[] emitted = PrjTree.Emit(node);
            int width = emitted[1];
            var vi = new byte[1 + width];
            Buffer.BlockCopy(emitted, 1, vi, 0, vi.Length);
            return vi;
        }

        static string[] ScalarNames(PrjNode node)
        {
            var names = new string[node.Scalars.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = node.Scalars[i].Name;
            return names;
        }

        static void Visit(PrjNode node, Action<PrjScalar> action)
        {
            foreach (PrjScalar scalar in node.Scalars)
                action(scalar);
            foreach (PrjNode child in node.Children)
                Visit(child, action);
        }

        /// <summary>
        /// Two trees are the same when they have the same structs in the same order with the same scalars.
        /// Blobs are compared inflated. Scalar order is checked everywhere except inside PatternData.
        /// </summary>
        internal static void AssertSameTree(PrjNode expected, PrjNode actual, string path)
        {
            var differences = new List<string>();
            CollectDifferences(expected, actual, path, differences);
            Assert.That(differences, Is.Empty);
        }

        internal static void CollectDifferences(PrjNode expected, PrjNode actual, string path, List<string> differences)
        {
            bool anyOrder = expected.Name.StartsWith("PatternData", StringComparison.Ordinal);
            if (expected.Scalars.Count != actual.Scalars.Count)
                differences.Add(path + " scalar count");
            for (int i = 0; i < expected.Scalars.Count; i++)
            {
                PrjScalar e = expected.Scalars[i];
                PrjScalar a = anyOrder ? actual.FindScalar(e.Name) : (i < actual.Scalars.Count ? actual.Scalars[i] : null);
                if (a == null || a.Name != e.Name)
                {
                    differences.Add(path + "." + e.Name + " (missing or out of order)");
                    continue;
                }
                if (!SameValue(e, a))
                    differences.Add(path + "." + e.Name);
            }

            if (expected.Children.Count != actual.Children.Count)
            {
                differences.Add(path + " child count");
                return;
            }
            for (int i = 0; i < expected.Children.Count; i++)
            {
                if (expected.Children[i].Name != actual.Children[i].Name)
                {
                    differences.Add(path + "/" + expected.Children[i].Name + " (found " + actual.Children[i].Name + ")");
                    continue;
                }
                CollectDifferences(expected.Children[i], actual.Children[i], path + "/" + expected.Children[i].Name, differences);
            }
        }

        static bool SameValue(PrjScalar expected, PrjScalar actual)
        {
            if (expected.IsNull || actual.IsNull)
                return expected.IsNull && actual.IsNull;
            if (expected.Type == PrjType.Blob && actual.Type == PrjType.Blob)
                return SameBytes(Zlib.Decompress(expected.Raw, 1, expected.Raw.Length - 1), Zlib.Decompress(actual.Raw, 1, actual.Raw.Length - 1));
            return SameBytes(expected.Raw, actual.Raw);
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }
    }
}
