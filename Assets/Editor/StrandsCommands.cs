using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Strands.Audio;
using Strands.Export;
using Strands.Sp16;
using UnityEditor;
using UnityEngine;

namespace Strands.Editor
{
    /// <summary>
    /// Entry points for the Strands tools: menu items for use in the Editor, and plain static
    /// methods that return a report so they can be called from the command line bridge.
    /// </summary>
    public static class StrandsCommands
    {
        const string TemplatePath = "Assets/Templates/arr0.prj.bytes";
        const string OutputFolder = "out";

        [MenuItem("Strands/Export WAV to SP-16 Package...")]
        static void ExportMenu()
        {
            string wav = EditorUtility.OpenFilePanel("Choose a WAV, AIFF, FLAC or MP3 file", "", "wav,aif,aiff,aifc,flac,mp3");
            if (string.IsNullOrEmpty(wav))
                return;
            string name = WavToSp16.SanitizeName(Path.GetFileNameWithoutExtension(wav), 24);
            string package = EditorUtility.SaveFilePanel("Save SP-16 package", Path.GetFullPath(OutputFolder), name, "tpkg");
            if (string.IsNullOrEmpty(package))
                return;
            try
            {
                var options = new WavExportOptions { ProjectName = Path.GetFileNameWithoutExtension(package) };
                WavExportResult result = WavToSp16.Export(wav, LoadTemplate(), package, options);
                Debug.Log("Strands export finished.\n" + result.Describe());
                EditorUtility.RevealInFinder(package);
            }
            catch (Exception e)
            {
                Debug.LogError("Strands export failed: " + e.Message);
                EditorUtility.DisplayDialog("Strands", "Export failed.\n\n" + e.Message, "OK");
            }
        }

        [MenuItem("Strands/Inspect SP-16 Project or Package...")]
        static void InspectMenu()
        {
            string file = EditorUtility.OpenFilePanel("Choose a .prj or .tpkg file", Path.GetFullPath(OutputFolder), "prj,tpkg");
            if (!string.IsNullOrEmpty(file))
                Debug.Log(Inspect(file));
        }

        /// <summary>Analyses a WAV and reports the beat grid without writing a package.</summary>
        public static string Analyze(string wavPath, double bpm = 0)
        {
            WavData wav = AudioFile.Read(wavPath);
            var options = new WavExportOptions();
            if (bpm > 0)
                options.Grid.Bpm = bpm;
            return WavToSp16.Plan(wav, WavToSp16.SanitizeName(Path.GetFileNameWithoutExtension(wavPath), 24), options).Describe();
        }

        /// <summary>Reports, for each 4-bar pattern, how far the attacks in the audio are from the grid.</summary>
        public static string Drift(string wavPath, double bpm = 0, bool preferRound = true)
        {
            WavData wav = AudioFile.Read(wavPath);
            var options = new BeatGridOptions { PreferRoundTempo = preferRound };
            if (bpm > 0)
                options.Bpm = bpm;
            BeatGrid grid = BeatGridAnalyzer.Analyze(wav.Samples, wav.SampleRate, options);
            int patterns = (int)((wav.Seconds - grid.FirstDownbeatSeconds) / (16 * grid.BeatSeconds));
            double[] drift = new BeatDriftMeter(wav.Samples, wav.SampleRate).Measure(grid, 0, 16, patterns);
            var text = new StringBuilder();
            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1:0.0} s, {2:0.0000} BPM (envelope {3:0.0000}, attacks {4:0.0000}), {5} beats placed, spread {6:0.00} ms, first downbeat {7:0.0000} s (beat {8} of 4{9}), drums enter {10:0.000} s",
                Path.GetFileName(wavPath), wav.Seconds, grid.Bpm, grid.FluxBpm, grid.FittedBpm, grid.TransientBeats, grid.TransientSpreadMs, grid.FirstDownbeatSeconds, grid.DownbeatIndex + 1, grid.DownbeatFromDrums ? ", from the drums" : "", grid.FirstSteadyBeatSeconds));
            text.Append("Pattern offsets (ms):");
            foreach (double value in drift)
                text.Append(double.IsNaN(value) ? "    --" : value.ToString(" +0.0; -0.0", CultureInfo.InvariantCulture).PadLeft(6));
            text.AppendLine();
            return text.ToString();
        }

        /// <summary>
        /// Exports a WAV to out/&lt;name&gt;.tpkg and returns the report.
        /// </summary>
        /// <param name="bpm">A tempo to start from, or 0 to estimate it.</param>
        /// <param name="downbeat">Which of the first four beats starts the bar (1 to 4), or 0 to guess.</param>
        /// <param name="clickTrack">Also writes out/&lt;name&gt;_clicks.wav, the audio with a click on every beat.</param>
        public static string Export(string wavPath, string name = null, double bpm = 0, int downbeat = 0,
            double crossfadeMs = 5.0, int barsPerChunk = 4, bool clickTrack = false)
        {
            var options = new WavExportOptions { ProjectName = name, CrossfadeMs = crossfadeMs, BarsPerChunk = barsPerChunk };
            if (bpm > 0)
                options.Grid.Bpm = bpm;
            if (downbeat > 0)
                options.Grid.DownbeatIndex = downbeat - 1;

            string projectName = WavToSp16.SanitizeName(string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(wavPath) : name, 24);
            string package = Path.Combine(Path.GetFullPath(OutputFolder), projectName + ".tpkg");
            WavExportResult result = WavToSp16.Export(wavPath, LoadTemplate(), package, options);
            string report = result.Describe();

            if (clickTrack)
            {
                string clicks = Path.Combine(Path.GetFullPath(OutputFolder), projectName + "_clicks.wav");
                File.WriteAllBytes(clicks, BeatGridPreview.WriteClickTrack(AudioFile.Read(wavPath), result.Grid));
                report += "Click track    " + clicks + "\n";
            }
            return report;
        }

        /// <summary>
        /// Exports the stems of one song to out/&lt;name&gt;.tpkg and returns the report. The song is laid
        /// out as the app lays out a full mix: from the start of the file, silence in front left out,
        /// a pattern starting where the drums come in, and a slice on every beat. The grid is the
        /// one that fits best of those found from each stem and from all of them added together.
        /// </summary>
        /// <param name="stemPaths">One file per stem. Outputs are given out in this order: 1-2, 3-4, 5-6, 7-8, then round again.</param>
        /// <param name="bpm">A tempo to start from, or 0 to estimate it.</param>
        /// <param name="downbeat">Which of the first four beats starts the bar (1 to 4), or 0 to guess.</param>
        /// <param name="keepApart">Give stems that share an output a pad each, and not one sample between them. Stems on master need it to stay apart.</param>
        /// <param name="clickTrack">Also writes out/&lt;name&gt;_clicks.wav, the stems added together with a click on every beat.</param>
        /// <param name="outputs">
        /// An output for each stem in place of the usual order, as the unit names them and with commas
        /// between: "1-2,3,4,5-6" puts the second and third stems on outputs 3 and 4 by themselves,
        /// as mono samples. "master" is the master output, which every stem has to be on if any is.
        /// </param>
        public static string ExportStems(string[] stemPaths, string name = null, double bpm = 0, int downbeat = 0,
            double crossfadeMs = 5.0, bool keepApart = false, bool clickTrack = false, string outputs = null, bool chokeGroups = false, string stretch = null,
            bool loop = false, bool arrangement = true, bool tempoInArrangement = true, double loopFadeOutMs = 3, double loopFadeInMs = 1)
        {
            StemSet stems;
            GridChoice choice;
            var extras = new WavExportOptions { Stretch = ParseStretch(stretch), Arrangement = arrangement, TempoInArrangement = tempoInArrangement,
                LoopFadeOutMs = loopFadeOutMs, LoopFadeInMs = loopFadeInMs };
            // On the command line choke and loop are for every stem or for none.
            for (int layer = 0; layer < Sp16Project.PadsPerScene; layer++)
            {
                if (chokeGroups)
                    extras.Pads.SetChokeOfLayer(layer, true);
                if (loop)
                    extras.Pads.SetLoopOfLayer(layer, true);
            }
            WavExportResult plan = PlanStems(stemPaths, name, bpm, downbeat, crossfadeMs, keepApart, outputs, extras, out stems, out choice);
            string package = Path.Combine(Path.GetFullPath(OutputFolder), plan.ProjectName + ".tpkg");
            stems.WritePackage(LoadTemplate(), package, plan);
            string report = choice.Describe() + plan.Describe();

            if (clickTrack)
            {
                string clicks = Path.Combine(Path.GetFullPath(OutputFolder), plan.ProjectName + "_clicks.wav");
                File.WriteAllBytes(clicks, BeatGridPreview.WriteClickTrack(stems.Mix, choice.Grid));
                report += "Click track    " + clicks + "\n";
            }
            return report;
        }

        /// <summary>Reports the grid each stem gives, the one taken, and the layout, without writing a package.</summary>
        public static string AnalyzeStems(string[] stemPaths, double bpm = 0)
        {
            StemSet stems;
            GridChoice choice;
            WavExportResult plan = PlanStems(stemPaths, null, bpm, 0, 5.0, false, null, new WavExportOptions(), out stems, out choice);
            return choice.Describe() + plan.Describe();
        }

        /// <summary>The unit's own names for its Time Stretch settings: off, m.tmp and resmpl. Nothing is off.</summary>
        static Sp16Stretch ParseStretch(string stretch)
        {
            switch ((stretch ?? "off").Trim().ToLowerInvariant())
            {
                case "": case "off": return Sp16Stretch.Off;
                case "m.tmp": case "mt": return Sp16Stretch.MasterTempo;
                case "resmpl": case "resample": return Sp16Stretch.Resample;
                default: throw new ArgumentException("Time stretch is off, m.tmp or resmpl, not \"" + stretch + "\".");
            }
        }

        static WavExportResult PlanStems(string[] stemPaths, string name, double bpm, int downbeat, double crossfadeMs, bool keepApart, string outputs, WavExportOptions extras,
            out StemSet stems, out GridChoice choice)
        {
            List<Stem> read = StemsToSp16.Read(stemPaths);
            if (!string.IsNullOrEmpty(outputs))
            {
                string[] wanted = outputs.Split(',');
                if (wanted.Length != read.Count)
                    throw new ArgumentException("There are " + read.Count + " stems and " + wanted.Length + " outputs were given.");
                for (int i = 0; i < wanted.Length; i++)
                    read[i].Output = ParseOutput(wanted[i].Trim());
            }
            // Stems kept apart on master are a strip each, as the app has them, which is what gives each its colour on the unit.
            if (keepApart)
                for (int i = 0; i < read.Count; i++)
                    if (read[i].Output == Sp16Output.Master)
                        read[i].Group = i % Sp16Project.OutputPairs;
            stems = StemsToSp16.Prepare(read, !keepApart);

            // The same choices the app makes for a song (SongSession).
            var options = new WavExportOptions { CrossfadeMs = crossfadeMs, KeepLeadIn = true, TrimLeadingSilence = true, RefuseSilence = true, SliceOnBeats = true, Stretch = extras.Stretch,
                Arrangement = extras.Arrangement, TempoInArrangement = extras.TempoInArrangement,
                LoopFadeOutMs = extras.LoopFadeOutMs, LoopFadeInMs = extras.LoopFadeInMs };
            options.Pads.CopyFrom(extras.Pads);
            if (bpm > 0)
                options.Grid.Bpm = bpm;
            if (downbeat > 0)
                options.Grid.DownbeatIndex = downbeat - 1;
            choice = StemsToSp16.ChooseGrid(stems, options.Grid);
            if (choice.Grid.FirstSteadyBeatSeconds >= 0)
                options.PatternAnchorSeconds = choice.Grid.FirstSteadyBeatSeconds;

            string folderName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(stemPaths[0])));
            string projectName = WavToSp16.SanitizeName(string.IsNullOrEmpty(name) ? folderName : name, 24);
            WavExportResult plan = stems.Plan(projectName, options, choice.Grid);
            plan.AddWarnings(choice.Warnings);
            return plan;
        }

        /// <summary>An output from its name on the unit: 1-2, 3-4, 5-6 or 7-8 for a pair, 1 to 8 for one by itself, or master.</summary>
        static Sp16Output ParseOutput(string name)
        {
            foreach (Sp16Output output in Enum.GetValues(typeof(Sp16Output)))
                if (Sp16Project.OutputName(output) == name)
                    return output;
            throw new ArgumentException("'" + name + "' is not an output. Use 1-2, 3-4, 5-6 or 7-8 for a pair, 1 to 8 for one by itself, or master.");
        }

        /// <summary>Describes a .prj or .tpkg: samples, patterns, routing and the arranger list.</summary>
        public static string Inspect(string path)
        {
            byte[] file = File.ReadAllBytes(path);
            var text = new StringBuilder();
            text.AppendLine(Path.GetFileName(path));
            if (PrjFile.HasMagic(file))
            {
                DescribeProject(text, file);
                return text.ToString();
            }

            List<StoredZipReader.Entry> entries = StoredZipReader.Read(file);
            text.AppendLine(entries.Count + " entries");
            foreach (StoredZipReader.Entry entry in entries)
                text.AppendLine(string.Format(CultureInfo.InvariantCulture, "  {0,-28} {1,10} bytes  {2}", entry.Name, entry.Data.Length, entry.Method == 0 ? "stored" : "method " + entry.Method));
            foreach (StoredZipReader.Entry entry in entries)
                if (entry.Name.EndsWith(".prj", StringComparison.OrdinalIgnoreCase))
                    DescribeProject(text, entry.Data);
            return text.ToString();
        }

        static void DescribeProject(StringBuilder text, byte[] prj)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            Sp16Project project = Sp16Project.FromPrj(prj);
            text.AppendLine(string.Format(inv, "Project path   {0}", project.ProjectFilePath));
            text.AppendLine(string.Format(inv, "Project tempo  {0:0.000} BPM, arranger {1}", project.ProjectBpm, project.PlayArrangement ? "on" : "off"));

            for (int scene = 0; scene < Sp16Project.Scenes; scene++)
            {
                PrjNode sceneNode = project.Scene(scene);
                PrjNode routing = sceneNode.Child("RoutingData");
                var lines = new List<string>();
                for (int pad = 0; pad < Sp16Project.PadsPerScene; pad++)
                {
                    PrjNode track = project.Pad(scene, pad);
                    PrjNode playback = track.Child("PlaybackData");
                    string url = playback.Scalar("audioSourceUrl").AsString();
                    if (url.Length == 0)
                        continue;
                    // AssignToNull is the master output, where a pad goes unless it is sent elsewhere.
                    PrjNode padRouting = routing.FindChild("TrackRoutingData" + pad.ToString(inv));
                    string output = padRouting == null ? "AssignToNull" : padRouting.Scalar("assignableOutCh").AsString();
                    lines.Add(string.Format(inv, "    pad {0,2}  {1}  {2} frames  choke {3}  stretch {4}{5}", pad + 1, url,
                        playback.Scalar("lengthSample").AsInt32(), track.Scalar("chokeGroup").AsInt32(), playback.Scalar("stretchMode").AsString(),
                        output == "AssignToNull" ? "" : "  out " + output));
                }
                for (int pattern = 0; pattern < Sp16Project.PatternsPerScene; pattern++)
                {
                    PrjNode node = project.FindPattern(scene, pattern);
                    if (node == null)
                        continue;
                    var triggers = new StringBuilder();
                    foreach (KeyValuePair<int, int> trigger in StepsBlob.FromScalar(node.Scalar("Steps")).GetTriggers())
                        triggers.Append(string.Format(inv, " pad {0} step {1};", trigger.Key + 1, trigger.Value + 1));
                    lines.Add(string.Format(inv, "    pattern {0,2}  length {1} steps  triggers:{2}", pattern + 1, node.Scalar("Length").AsInt32(), triggers));
                }
                string mode = routing.Scalar("assignMode").AsString();
                if (lines.Count == 0 && mode == "Master")
                    continue;
                text.AppendLine(string.Format(inv, "  Scene {0} '{1}'  routing {2}", scene + 1, sceneNode.Scalar("name").AsString(), mode));
                foreach (string line in lines)
                    text.AppendLine(line);
            }

            Arrangement arrangement = project.GetArrangement();
            text.AppendLine("Arranger       " + arrangement.Length + " sections");
            for (int i = 0; i < arrangement.Length; i++)
            {
                Section section = arrangement[i];
                text.AppendLine(string.Format(inv, "  {0,3}  scene {1,2} pattern {2,2}  x{3}  {4}", i + 1, section.Scene + 1, section.Pattern + 1, section.Repeat,
                    section.Bpm == 0 ? "no tempo override" : section.Bpm.ToString("0.000", inv) + " BPM"));
            }
        }

        static byte[] LoadTemplate()
        {
            return File.ReadAllBytes(TemplatePath);
        }
    }
}
