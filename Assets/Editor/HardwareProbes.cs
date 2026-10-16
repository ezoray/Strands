using System;
using System.Globalization;
using System.IO;
using System.Text;
using Strands.Audio;
using Strands.Sp16;
using UnityEditor;
using UnityEngine;

namespace Strands.Editor
{
    /// <summary>
    /// Small generated packages that put a question to the SP-16 which no file can answer. They hold
    /// generated tones only, and each report says what to listen for. Nothing in the app depends on them.
    /// </summary>
    public static class HardwareProbes
    {
        const string TemplatePath = "Assets/Templates/arr0.prj.bytes";
        const string OutputFolder = "out";
        const int Rate = 44100;
        // 0.4 s, the length of the tones in rev1.tpkg, which the SP-16 loaded and played.
        const int ToneFrames = 17640;
        const float Bpm = 120f;
        const int Level = 8000;
        const double LowHertz = 220, HighHertz = 880, MonoHertz = 440;

        [MenuItem("Strands/Hardware probes/Single outputs")]
        static void SingleOutputsMenu()
        {
            Debug.Log(SingleOutputs());
        }

        /// <summary>
        /// Writes out/SingleOutA.tpkg and out/SingleOutB.tpkg and returns what each is for.
        /// A asks whether a generated project on the eight single outputs plays where it should,
        /// and what a stereo sample does on one output. B asks whether a mono WAV loads from a
        /// package. They are apart so that a refusal of the mono files cannot hide A's answer.
        /// </summary>
        public static string SingleOutputs()
        {
            byte[] stereo = WavWriter.Write(Tone(LowHertz, HighHertz), Rate);
            byte[] mono = WavWriter.WriteMono(Tone(MonoHertz), Rate);
            var report = new StringBuilder();

            // A: eight stereo samples, low tone on the left and high on the right, one on each output.
            var outputsA = new Sp16Output[8];
            var filesA = new byte[8][];
            for (int pad = 0; pad < 8; pad++)
            {
                outputsA[pad] = Sp16Output.Out1 + pad;
                filesA[pad] = stereo;
            }
            string a = WritePackage("SingleOutA", "Singles", outputsA, filesA, 32);
            report.AppendLine(a);
            report.AppendLine("  Eight tones half a second apart over two bars, played four times at 120 BPM.");
            report.AppendLine("  Pads 1 to 8 are on outputs 1 to 8, so with the outputs in pairs on four mixer channels the tone should");
            report.AppendLine("  step through channel 1 left, channel 1 right, channel 2 left, and so on to channel 4 right.");
            report.AppendLine("  Every sample is stereo: 220 Hz on its left side and 880 Hz, two octaves up, on its right.");
            report.AppendLine("  Which do you hear on each output: the low tone only, the high tone only, or both together?");
            report.AppendLine();

            // B: mono files on two single outputs and on a pair, with a stereo file on a pair to compare.
            Sp16Output[] outputsB = { Sp16Output.Out1, Sp16Output.Out2, Sp16Output.Out34, Sp16Output.Out56 };
            byte[][] filesB = { mono, mono, mono, stereo };
            string b = WritePackage("SingleOutB", "MonoWav", outputsB, filesB, 16);
            report.AppendLine(b);
            report.AppendLine("  Four tones half a second apart over one bar, played four times at 120 BPM.");
            report.AppendLine("  1: a mono file (440 Hz) on output 1, expected on channel 1 left.");
            report.AppendLine("  2: the same mono file on output 2, expected on channel 1 right.");
            report.AppendLine("  3: the same mono file on outputs 3-4, expected in the middle of channel 2.");
            report.AppendLine("  4: the stereo file on outputs 5-6, expected on channel 3 with the low tone left and the high tone right.");
            report.AppendLine("  If the package does not load, or the first three are silent, the unit does not take mono files this way.");
            return report.ToString();
        }

        const int MemorySampleSeconds = 60;
        const int MemoryStepsApart = 4;
        const int NoiseLevel = 16;

        [MenuItem("Strands/Hardware probes/Memory, 259 MB stereo")]
        static void MemoryMenu()
        {
            Debug.Log(Memory(259, false));
        }

        /// <summary>
        /// Writes out/Mem&lt;megabytes&gt;.tpkg (with an m after the number for mono): samples whose audio
        /// adds up to that many million bytes, to find where the SP-16's sample memory ends. Each
        /// sample is 60 s, under the reported 64 s limit, with the last one cut to fit. A sample is a
        /// short tone followed by noise too quiet to matter, different in every file so that no two
        /// can be taken for the same one. Pads fill in order over as many scenes as it takes, and
        /// each scene has one pattern that triggers its pads a beat apart, the tones rising a
        /// semitone a pad, so that a sample which did not load is a gap in the scale.
        /// </summary>
        public static string Memory(int megabytes, bool mono)
        {
            int bytesPerFrame = mono ? 2 : Sp16Project.StereoBytesPerFrame;
            long totalFrames = megabytes * 1000000L / bytesPerFrame;
            int fullFrames = MemorySampleSeconds * Rate;
            int count = (int)((totalFrames + fullFrames - 1) / fullFrames);
            int lastFrames = (int)(totalFrames - (long)(count - 1) * fullFrames);
            // A last sample too short to hear is added to the one before it, which stays under 64 s.
            if (count > 1 && lastFrames < Rate)
            {
                count--;
                lastFrames += fullFrames;
            }
            if (megabytes < 1 || count > Sp16Project.Scenes * Sp16Project.PadsPerScene)
                throw new ArgumentOutOfRangeException("megabytes");

            string name = "Mem" + megabytes.ToString(CultureInfo.InvariantCulture) + (mono ? "m" : "");
            int scenes = (count + Sp16Project.PadsPerScene - 1) / Sp16Project.PadsPerScene;

            Sp16Project project = Sp16Project.FromPrj(File.ReadAllBytes(TemplatePath));
            project.SetProjectName(name);
            project.ProjectBpm = Bpm;
            project.PlayArrangement = true;
            project.SceneIndex = 0;
            project.PatternIndex = 0;

            var padOutputs = new string[Sp16Project.PadsPerScene];
            for (int pad = 0; pad < padOutputs.Length; pad++)
                padOutputs[pad] = Sp16Project.OutputValue(Sp16Output.Out12);

            var names = new string[count];
            var lengths = new int[count];
            Arrangement arrangement = Arrangement.CreateEmpty();
            for (int scene = 0; scene < scenes; scene++)
            {
                project.RemovePatterns(scene);
                project.SetSceneName(scene, "Mem" + (scene + 1).ToString(CultureInfo.InvariantCulture));
                project.SetRouting8Ch(scene, padOutputs);

                StepsBlob steps = StepsBlob.CreateEmpty();
                for (int pad = 0; pad < Sp16Project.PadsPerScene; pad++)
                {
                    int index = scene * Sp16Project.PadsPerScene + pad;
                    if (index >= count)
                        break;
                    names[index] = name + "_" + (index + 1).ToString("000", CultureInfo.InvariantCulture) + ".wav";
                    lengths[index] = index == count - 1 ? lastFrames : fullFrames;
                    project.SetPadSample(scene, pad, names[index], lengths[index]);
                    steps.SetTrigger(pad, MemoryStepsApart * pad);
                }
                project.SetPattern(scene, 0, steps, Sp16Project.MaxPatternLength);
                arrangement.Add(new Section(scene, 0, 1, Bpm));
            }
            project.SetArrangement(arrangement);

            string path = Path.Combine(Path.GetFullPath(OutputFolder), name + ".tpkg");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            long audioBytes = 0;
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new StoredZipWriter(output))
            {
                zip.Add(name + ".prj", project.ToPrj());
                for (int index = 0; index < count; index++)
                {
                    short[] samples = MemorySample(index, lengths[index], mono);
                    zip.Add(names[index], mono ? WavWriter.WriteMono(samples, Rate) : WavWriter.Write(samples, Rate));
                    zip.Add(names[index] + ".dat", SampleAdjunct.ForSample(lengths[index]).Write());
                    audioBytes += (long)lengths[index] * bytesPerFrame;
                }
            }

            CultureInfo inv = CultureInfo.InvariantCulture;
            int lastScenePads = count - (scenes - 1) * Sp16Project.PadsPerScene;
            var report = new StringBuilder();
            report.AppendLine(path);
            report.AppendLine(string.Format(inv, "  {0:N0} bytes of {1} audio in {2} samples over {3} scene{4}; the package is {5:N0} bytes.",
                audioBytes, mono ? "mono" : "stereo", count, scenes, scenes == 1 ? "" : "s", new FileInfo(path).Length));
            report.AppendLine(string.Format(inv, "  Every sample is {0} s except the last, pad {1} of scene {2}, which is {3:0.0} s.",
                MemorySampleSeconds, lastScenePads, scenes, lastFrames / (double)Rate));
            report.AppendLine("  First: does it load, and what does the unit show while it loads and afterwards?");
            report.AppendLine("  Then play the arrangement. Each scene plays its pads once, half a second apart, as a rising scale:");
            report.AppendLine(string.Format(inv, "  16 tones in a full scene, {0} in the last. A tone missing from the scale is a sample that did not load.", lastScenePads));
            report.AppendLine("  The pads can also be played by hand, scene by scene.");
            return report.ToString();
        }

        /// <summary>
        /// A 0.4 s tone, a semitone higher on each pad of a scene, then quiet noise seeded by the
        /// sample's number. Stereo samples carry the tone on both sides and different noise on each.
        /// </summary>
        static short[] MemorySample(int index, int frames, bool mono)
        {
            int channels = mono ? 1 : 2;
            var samples = new short[frames * channels];
            uint state = 2463534242u + (uint)index * 2654435761u;
            for (int i = 0; i < samples.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                samples[i] = (short)((int)(state >> 27) - NoiseLevel);
            }
            double hertz = LowHertz * Math.Pow(2, (index % Sp16Project.PadsPerScene) / 12.0);
            for (int frame = 0; frame < ToneFrames && frame < frames; frame++)
            {
                short tone = Sample(hertz, frame);
                for (int channel = 0; channel < channels; channel++)
                    samples[frame * channels + channel] += tone;
            }
            return samples;
        }

        /// <summary>
        /// One scene in 8ch mode with a sample on each of the first pads, each pad on its output, and
        /// one pattern that triggers them four steps apart. The arranger plays the pattern four times.
        /// </summary>
        static string WritePackage(string name, string sceneName, Sp16Output[] outputs, byte[][] wavFiles, int patternLength)
        {
            Sp16Project project = Sp16Project.FromPrj(File.ReadAllBytes(TemplatePath));
            project.SetProjectName(name);
            project.ProjectBpm = Bpm;
            project.PlayArrangement = true;
            project.SceneIndex = 0;
            project.PatternIndex = 0;
            project.RemovePatterns(0);
            project.SetSceneName(0, sceneName);

            // In 8ch mode the unit gives a pad it has nothing for the first pair, so all 16 are written.
            var padOutputs = new string[Sp16Project.PadsPerScene];
            for (int pad = 0; pad < padOutputs.Length; pad++)
                padOutputs[pad] = Sp16Project.OutputValue(pad < outputs.Length ? outputs[pad] : Sp16Output.Out12);
            project.SetRouting8Ch(0, padOutputs);

            StepsBlob steps = StepsBlob.CreateEmpty();
            var names = new string[outputs.Length];
            for (int pad = 0; pad < outputs.Length; pad++)
            {
                names[pad] = name + "_" + (pad + 1).ToString(CultureInfo.InvariantCulture) + ".wav";
                project.SetPadSample(0, pad, names[pad], ToneFrames);
                steps.SetTrigger(pad, 4 * pad);
            }
            project.SetPattern(0, 0, steps, patternLength);

            // An explicit tempo, because an override on the unit is sticky.
            Arrangement arrangement = Arrangement.CreateEmpty();
            arrangement.Add(new Section(0, 0, 4, Bpm));
            project.SetArrangement(arrangement);

            string path = Path.Combine(Path.GetFullPath(OutputFolder), name + ".tpkg");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new StoredZipWriter(output))
            {
                zip.Add(name + ".prj", project.ToPrj());
                for (int pad = 0; pad < outputs.Length; pad++)
                {
                    zip.Add(names[pad], wavFiles[pad]);
                    zip.Add(names[pad] + ".dat", SampleAdjunct.ForSample(ToneFrames).Write());
                }
            }
            return path;
        }

        /// <summary>A stereo tone, one pitch on each side, with 5 ms fades so that it does not click.</summary>
        static short[] Tone(double leftHertz, double rightHertz)
        {
            var samples = new short[ToneFrames * 2];
            for (int i = 0; i < ToneFrames; i++)
            {
                samples[2 * i] = Sample(leftHertz, i);
                samples[2 * i + 1] = Sample(rightHertz, i);
            }
            return samples;
        }

        static short[] Tone(double hertz)
        {
            var samples = new short[ToneFrames];
            for (int i = 0; i < ToneFrames; i++)
                samples[i] = Sample(hertz, i);
            return samples;
        }

        static short Sample(double hertz, int frame)
        {
            int fade = Rate / 200;
            double gain = Math.Min(1.0, Math.Min((frame + 0.5) / fade, (ToneFrames - frame - 0.5) / fade));
            return (short)Math.Round(Level * gain * Math.Sin(2 * Math.PI * hertz * frame / Rate));
        }
    }
}
