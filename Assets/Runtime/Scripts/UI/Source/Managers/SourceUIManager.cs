using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Strands.Applications.Core;
using Strands.Applications.Session;
using Strands.Audio;
using Strands.Export;
using Strands.Import;
using Strands.Sp16;
using Strands.UI.Shared;
using Strands.UI.State;
using Strands.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Strands.UI.Source
{
    /// <summary>
    /// The first screen: load the audio on to the four output strips and start the analysis.
    /// One file is a full song; several are stems, each playing from the outputs of its strip.
    /// </summary>
    public class SourceUIManager : MonoBehaviour
    {
        const string HiddenClass = "hidden";
        const string PrimaryClass = "primary";
        const string DraggingClass = "file--dragging";
        const string StripDropClass = "strip--drop";
        const string WellDropClass = "well--drop";
        // How far the pointer has to go with the button down before a card is being dragged and not clicked.
        const float DragStartPixels = 6;
        // The most files taken in one go, from a folder or from the dialog: as many as a pattern has
        // tracks. A folder can hold hundreds, and every one of them would be read into memory.
        const int MostFilesAtOnce = Sp16Project.PadsPerScene;
        // What the usual four stems are, shown on a strip until it holds a file. Files added together
        // go to the strip their name says (StripLayout.Spread). The first also says that one file
        // by itself is a whole song, which is what many people will load.
        static readonly string[] Waiting = { "Song / Drums", "Bass", "Other", "Vocals" };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [SerializeField] UIStateManager uiState;
        [SerializeField] ApplicationManager app;
        [SerializeField] JobManager jobs;

        VisualElement strips, outputsHost;
        Button addFiles, addFolder, clearFiles, analyse;
        Label info, error, summary;
        Label statTracks, statTracksCaption, statPatterns, statPatternsCaption, statBars, statNeeded, statLength;
        // The small waveform shown on each file, made while the file loads.
        readonly Dictionary<StripFile, WaveformPeaks> peaks = new Dictionary<StripFile, WaveformPeaks>();
        // The low, middle and high sound of a file, which colour its waveform when it is a full song
        // by itself. Made while a file loads when it is the only one asked for, otherwise when wanted.
        readonly Dictionary<StripFile, BandPeaks> bands = new Dictionary<StripFile, BandPeaks>();
        // What each file is called on its card when there are several: the part of its name the
        // others do not share, which is what tells them apart and what the Analysis screen calls them.
        readonly Dictionary<StripFile, string> titles = new Dictionary<StripFile, string>();
        // Whether a set of files summed goes past full scale, for each set asked about, so that a
        // strip is not added up again every time the screen is drawn.
        readonly List<KeyValuePair<StripFile[], bool>> sumsThatClip = new List<KeyValuePair<StripFile[], bool>>();
        int namedVersion = -1;
        // How many patterns the song is cut into, guessed from the files as soon as they are loaded,
        // so that it can be said before Analyse how many tracks it has room for. The guess runs in
        // the background, from the files summed, as Analyse would lay out a song in one file.
        sealed class Estimate
        {
            public int Version, Patterns;
        }
        int estimatedVersion = -1;
        int estimatedPatterns;
        CancellationTokenSource estimating;
        Estimate estimated;
        // What the main button reads when nothing is running, and whether what is running is the analysis.
        string analyseText = "Analyse";
        bool analysing;
        // Why the files as they are on the strips cannot make a package, found by the last analysis,
        // and those strips as they were then. The Analysis screen has nothing to show without a
        // package, so the app stays here, saying why, until the strips change (the user, 2026-10-16).
        string cannotExport;
        string cannotExportLayout;
        // The strips as they were when the analysis that is kept made its package. While they are
        // still so, the memory shown is the package's, which leaves out silent samples, and not
        // the estimate, which counts every sample as long as the song (the user, 2026-10-17: 266 MB
        // estimated, 254 analysed, and a warning on coming back to this screen).
        string analysedLayout;
        // The places a dragged card can be dropped: every strip, and each output of a split one.
        readonly List<DropPlace> places = new List<DropPlace>();
        // The card that has the pointer down on it, if any, which is being dragged once it has moved.
        Drag drag;

        struct DropPlace
        {
            public VisualElement Area;
            public int Strip;
            /// <summary>The output of a split strip, or -1 for the strip as a whole.</summary>
            public int Slot;
        }

        sealed class Drag
        {
            public StripFile File;
            public VisualElement Card;
            public Vector2 Down;
            public int Pointer;
            public int From;
            /// <summary>The file's name beside the pointer, there once the card is being dragged.</summary>
            public Label Ghost;
            /// <summary>Where the pointer was last, for scrolling a strip while it is held near an edge.</summary>
            public Vector2 Last;
            public IVisualElementScheduledItem Scrolling;
        }

        // How near the top or bottom of a strip's list a dragged card scrolls it, and how fast at the
        // edge itself, in the panel's units a tick (the user, 2026-10-17: a strip split to mono shows
        // its second output below the first, out of view, and could not be scrolled to while dragging).
        const float ScrollEdge = 48, MostScrollPerTick = 14;
        const long ScrollTickMs = 16;

        sealed class Loaded
        {
            public readonly List<StripFile> Files = new List<StripFile>();
            /// <summary>The stems of each stem file, which go on the strips in the file's own order.</summary>
            public readonly List<List<StripFile>> StemFiles = new List<List<StripFile>>();
            public readonly Dictionary<StripFile, WaveformPeaks> Peaks = new Dictionary<StripFile, WaveformPeaks>();
            public readonly Dictionary<StripFile, BandPeaks> Bands = new Dictionary<StripFile, BandPeaks>();
            public readonly List<string> Failed = new List<string>();
        }

        sealed class Analysed
        {
            public SongSession Session;
            public BeatGrid Grid;
            public GridChoice Choice;
        }

        /// <summary>Progress through one file of several, as progress through them all.</summary>
        sealed class PartProgress : IProgress<float>
        {
            readonly IProgress<float> whole;
            readonly int part, parts;

            public PartProgress(IProgress<float> whole, int part, int parts)
            {
                this.whole = whole;
                this.part = part;
                this.parts = parts;
            }

            public void Report(float value)
            {
                whole.Report((part + Mathf.Clamp01(value)) / parts);
            }
        }

        void Start()
        {
            VisualElement root = uiState.Root;
            strips = root.Q("strips");
            outputsHost = root.Q("outputsHost");
            addFiles = root.Q<Button>("addFiles");
            addFolder = root.Q<Button>("addFolder");
            clearFiles = root.Q<Button>("clearFiles");
            analyse = root.Q<Button>("analyse");
            info = root.Q<Label>("sourceInfo");
            error = root.Q<Label>("sourceError");
            summary = root.Q<Label>("sourceSummary");
            statTracks = root.Q<Label>("statTracks");
            statTracksCaption = root.Q<Label>("statTracksCaption");
            statPatterns = root.Q<Label>("statPatterns");
            statPatternsCaption = root.Q<Label>("statPatternsCaption");
            statBars = root.Q<Label>("statBars");
            statNeeded = root.Q<Label>("statNeeded");
            statLength = root.Q<Label>("statLength");

            addFiles.clicked += () => ChooseFiles(-1, 0);
            addFolder.clicked += ChooseFolder;
            clearFiles.clicked += () =>
            {
                app.Strips.Clear();
                peaks.Clear();
                bands.Clear();
                sumsThatClip.Clear();
                app.ForgetProjectName();
                cannotExport = cannotExportLayout = analysedLayout = null;
                // The analysed song holds every stem, their sum and their waveforms; with no files
                // left it has nothing to go back to.
                app.Session = null;
                ShowError(null);
                Rebuild();
                FreeMemory();
            };
            analyse.clicked += Analyse;
            uiState.StateChanged += OnStateChanged;
            uiState.StepRequested += OnStepRequested;
            Rebuild();
        }

        void OnDestroy()
        {
            if (estimating != null)
                estimating.Cancel();
            uiState.StateChanged -= OnStateChanged;
            uiState.StepRequested -= OnStepRequested;
        }

        void OnStateChanged(UIStates state)
        {
            if (state != UIStates.Source)
                return;
            // What the status bar said about the grid belongs to the other screen.
            if (!jobs.Busy)
                jobs.SetIdleMessage("Ready");
            Rebuild();
        }

        // The second step at the top goes back to the Align screen with the grid that is there.
        void OnStepRequested(UIStates state)
        {
            if (state == UIStates.Align && uiState.State == UIStates.Source && !jobs.Busy)
                Continue();
        }

        void Update()
        {
            Estimate done = Interlocked.Exchange(ref estimated, null);
            if (done != null && done.Version == estimatedVersion)
            {
                estimatedPatterns = done.Patterns;
                ShowFigures(app.Strips);
            }
            bool idle = !jobs.Busy;
            StripLayout layout = app.Strips;
            // The button that was pressed says so for as long as the analysis runs.
            if (idle)
                analysing = false;
            string says = analysing ? "Analysing…" : analyseText;
            if (analyse.text != says)
                analyse.text = says;
            addFiles.SetEnabled(idle);
            addFolder.SetEnabled(idle);
            clearFiles.SetEnabled(idle && layout.FileCount > 0);
            strips.SetEnabled(idle);
            outputsHost.SetEnabled(idle);
            analyse.SetEnabled(idle && layout.FileCount > 0 && layout.PatternsPerScene > 0 && !CannotExport() && !TooManyTracksForSong());

            if (uiState.State != UIStates.Source || !idle || Controls.IsTyping(uiState.Root))
                return;
            Keyboard keys = Keyboard.current;
            if (keys == null)
                return;
            bool command = keys.leftCommandKey.isPressed || keys.rightCommandKey.isPressed || keys.leftCtrlKey.isPressed || keys.rightCtrlKey.isPressed;
            if (command && keys.oKey.wasPressedThisFrame)
                ChooseFiles(-1, 0);
        }

        /// <summary>Asks for files and puts them on a strip, or shares them out over the strips when none is given.</summary>
        void ChooseFiles(int strip, int slot)
        {
            string[] paths = FileDialogs.OpenAudioFiles();
            if (paths != null)
                AddFilesTo(paths, strip, slot);
        }

        /// <summary>Asks for a folder and shares every audio file in it out over the strips, as Add files does with files.</summary>
        void ChooseFolder()
        {
            string folder;
            string[] paths = FileDialogs.OpenAudioFolder(out folder);
            if (paths == null)
                return;
            if (paths.Length == 0)
                ShowError("No audio files in " + Path.GetFileName(folder) + ".");
            else if (paths.Length > MostFilesAtOnce)
                ShowError(string.Format(Inv, "{0} holds {1} audio files. Up to {2} can be added at once: choose them with Add files.", Path.GetFileName(folder), paths.Length, MostFilesAtOnce));
            else
                AddFilesTo(paths, -1, 0);
        }

        /// <summary>
        /// Gives back the memory the files and the song held, now that nothing points to them (the
        /// user, 2026-10-17: Unity apps keep what they have used, so the app looks larger than it
        /// is). Collecting takes a moment, which a press of Clear can spare.
        /// </summary>
        static void FreeMemory()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        /// <summary>Replaces whatever is loaded with one file, a full song.</summary>
        public void LoadFile(string path)
        {
            app.Strips.Clear();
            peaks.Clear();
            bands.Clear();
            sumsThatClip.Clear();
            app.ForgetProjectName();
            AddFilesTo(new[] { path }, 0, 0);
        }

        /// <summary>Loads files and shares them out over the strips.</summary>
        public void AddFiles(string[] paths)
        {
            AddFilesTo(paths, -1, 0);
        }

        /// <summary>
        /// Loads files on to one strip: on its pair, or on its first or second output when it is
        /// split. A stem file is different: its stems go on the strips in order, one to each.
        /// </summary>
        public void AddFilesTo(string[] paths, int strip, int slot)
        {
            if (paths.Length > MostFilesAtOnce)
            {
                ShowError(string.Format(Inv, "{0} files chosen. Up to {1} can be added at once.", paths.Length, MostFilesAtOnce));
                return;
            }
            ShowError(null);
            string what = paths.Length == 1 ? Path.GetFileName(paths[0]) : paths.Length.ToString(Inv) + " files";
            jobs.Run("Loading " + what,
                (cancel, progress) =>
                {
                    var loaded = new Loaded();
                    for (int i = 0; i < paths.Length; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var part = new PartProgress(progress, i, paths.Length);
                        part.Report(0);
                        try
                        {
                            // An MP4 is a stem file when it lists its stems. One that does not is a song like any other file.
                            if (StemFile.HasStemExtension(paths[i]) && StemFile.IsStemFile(paths[i]))
                            {
                                var stems = new List<StripFile>();
                                foreach (StemFileTrack track in StemFile.Read(paths[i], part, cancel))
                                {
                                    WavToSp16.CheckSampleRate(track.Audio);
                                    var stem = new StripFile { Path = paths[i], Name = track.Name, Song = StemFile.SongName(paths[i]), Audio = track.Audio };
                                    loaded.Peaks[stem] = new WaveformPeaks(track.Audio.Samples);
                                    stems.Add(stem);
                                }
                                loaded.StemFiles.Add(stems);
                                continue;
                            }
                            WavData wav = AudioLoaders.Load(paths[i], part, cancel);
                            WavToSp16.CheckSampleRate(wav);
                            var file = new StripFile { Path = paths[i], Name = Path.GetFileNameWithoutExtension(paths[i]), Audio = wav };
                            loaded.Peaks[file] = new WaveformPeaks(wav.Samples);
                            if (paths.Length == 1)
                                loaded.Bands[file] = new BandPeaks(wav.Samples, wav.SampleRate, cancel);
                            loaded.Files.Add(file);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            loaded.Failed.Add(Path.GetFileName(paths[i]) + " could not be loaded. " + e.Message);
                        }
                    }
                    return loaded;
                },
                loaded =>
                {
                    foreach (KeyValuePair<StripFile, WaveformPeaks> made in loaded.Peaks)
                        peaks[made.Key] = made.Value;
                    foreach (KeyValuePair<StripFile, BandPeaks> made in loaded.Bands)
                        bands[made.Key] = made.Value;
                    foreach (List<StripFile> stems in loaded.StemFiles)
                        app.Strips.AddInOrder(stems);
                    if (strip < 0)
                        app.Strips.Spread(loaded.Files);
                    else
                        foreach (StripFile file in loaded.Files)
                            app.Strips.Add(strip, slot, file);
                    jobs.SetIdleMessage(loaded.Files.Count + loaded.StemFiles.Count == 0 ? "Ready" : "Loaded " + what + ConvertedNote(loaded));
                    ShowError(loaded.Failed.Count == 0 ? null : string.Join("\n", loaded.Failed.ToArray()));
                    foreach (string failed in loaded.Failed)
                        Debug.LogWarning(failed);
                    Rebuild();
                },
                e =>
                {
                    jobs.SetIdleMessage("Ready");
                    ShowError(what + " could not be loaded. " + e.Message);
                });
        }

        /// <summary>Finds the beat grid of what is loaded and goes on to the Align screen.</summary>
        public void Analyse()
        {
            StripLayout layout = app.Strips;
            if (layout.FileCount == 0 || layout.PatternsPerScene == 0 || CannotExport() || TooManyTracksForSong())
                return;
            // After the strips were changed to bring a package that did not fit within the unit,
            // the grid found from the same files still holds, so the stems are only laid out again.
            if (cannotExport != null && CanContinue())
            {
                Continue();
                return;
            }
            ShowError(null);
            int version = layout.FilesVersion;
            List<StripFile> files = layout.Files;
            string songName = string.IsNullOrEmpty(app.ProjectName) ? DefaultName(files) : app.ProjectName;

            Func<System.Threading.CancellationToken, IProgress<float>, Analysed> work;
            string status;
            if (layout.IsFullSong)
            {
                StripFile file = files[0];
                BandPeaks colours = BandsOf(file);
                status = "Finding beat grid of " + DisplayName(file);
                work = (cancel, progress) =>
                {
                    var session = new SongSession(DisplayName(file), songName, file.Audio, colours) { FilesVersion = version };
                    return new Analysed { Session = session, Grid = BeatGridAnalyzer.Analyze(file.Audio.Samples, file.Audio.SampleRate, session.Options.Grid, cancel) };
                };
            }
            else
            {
                List<Stem> stems = layout.Stems();
                status = "Finding beat grid from " + files.Count.ToString(Inv) + " stems";
                work = (cancel, progress) =>
                {
                    StemSet set = StemsToSp16.Prepare(stems);
                    var session = new SongSession(songName, set, StripsOf(stems)) { FilesVersion = version };
                    return new Analysed { Session = session, Choice = StemsToSp16.ChooseGrid(set, session.Options.Grid, progress, cancel) };
                };
            }

            analysing = true;
            jobs.Run(status, work,
                analysed =>
                {
                    if (analysed.Choice != null)
                        analysed.Session.ApplyGridChoice(analysed.Choice);
                    else
                        analysed.Session.ApplyAnalysis(analysed.Grid, false);
                    analysed.Session.ApplyExportDefaults(app.ExportDefaults);
                    app.Session = analysed.Session;
                    jobs.SetIdleMessage("Ready");
                    ShowAlignIfItFits(analysed.Session);
                },
                e =>
                {
                    jobs.SetIdleMessage("Ready");
                    ShowError("Beat grid could not be found. " + e.Message);
                });
        }

        /// <summary>
        /// Goes back to the Align screen with the grid as it was left, when the files are the ones
        /// it was found from. What may have changed is which outputs they are on.
        /// </summary>
        public void Continue()
        {
            SongSession session = app.Session;
            if (!CanContinue())
                return;
            ShowError(null);
            if (session.Stems == null)
            {
                ShowAlignIfItFits(session);
                return;
            }
            List<Stem> stems = app.Strips.Stems();
            jobs.Run("Laying out stems",
                (cancel, progress) => StemsToSp16.Prepare(stems),
                set =>
                {
                    session.Reroute(set, StripsOf(stems));
                    jobs.SetIdleMessage("Ready");
                    ShowAlignIfItFits(session);
                },
                e =>
                {
                    jobs.SetIdleMessage("Ready");
                    ShowError("Stems could not be laid out. " + e.Message);
                });
        }

        bool CanContinue()
        {
            SongSession session = app.Session;
            return session != null && session.HasGrid && session.FilesVersion == app.Strips.FilesVersion && app.Strips.FileCount > 0 && !CannotExport() && !TooManyTracksForSong();
        }

        /// <summary>
        /// Whether the strips as they are give the song more tracks than its patterns leave room
        /// for, as an analysis of these files found it: the patterns go with the grid and not with
        /// the outputs, so it holds however the files are laid out. Making tracks mono does not
        /// help: a mono track takes a pad as a stereo one does.
        /// </summary>
        bool TooManyTracksForSong()
        {
            bool exact;
            int needed = SongPatterns(out exact);
            return exact && app.Strips.Tracks > WavToSp16.MostTracksFor(needed);
        }

        /// <summary>
        /// How many patterns the song is cut into: as the last analysis of these files found it
        /// (exact), or else as guessed when they were loaded; 0 when neither is known yet.
        /// </summary>
        int SongPatterns(out bool exact)
        {
            SongSession session = app.Session;
            exact = session != null && session.HasGrid && session.FilesVersion == app.Strips.FilesVersion && session.PatternsNeeded > 0;
            if (exact)
                return session.PatternsNeeded;
            return estimatedVersion == app.Strips.FilesVersion ? estimatedPatterns : 0;
        }

        /// <summary>
        /// Starts a guess at the song's length in patterns when the files have changed since the
        /// last one: a beat grid found from them summed, laid out as Analyse would lay out a song
        /// in one file. It runs beside everything else and is dropped if the files change again.
        /// </summary>
        void EstimatePatterns(StripLayout layout)
        {
            if (layout.FilesVersion == estimatedVersion)
                return;
            if (estimating != null)
                estimating.Cancel();
            estimating = null;
            estimatedVersion = layout.FilesVersion;
            estimatedPatterns = 0;
            if (layout.FileCount == 0)
                return;
            int version = layout.FilesVersion;
            List<WavData> audio = layout.Files.ConvertAll(file => file.Audio);
            var cancel = new CancellationTokenSource();
            estimating = cancel;
            Task.Run(() =>
            {
                try
                {
                    WavData song = StemsToSp16.MixOf(audio);
                    var session = new SongSession("", "Estimate", song, null);
                    session.ApplyAnalysis(BeatGridAnalyzer.Analyze(song.Samples, song.SampleRate, session.Options.Grid, cancel.Token), false);
                    if (!cancel.IsCancellationRequested && session.PatternsNeeded > 0)
                        estimated = new Estimate { Version = version, Patterns = session.PatternsNeeded };
                }
                catch (Exception e)
                {
                    // A guess that cannot be made leaves the figure as unknown; Analyse says why.
                    if (!(e is OperationCanceledException))
                        Debug.Log("Length of song not estimated: " + e.Message);
                }
            });
        }

        /// <summary>
        /// Goes on to the Analysis screen when the files make a package. When they do not (too
        /// much memory, or more patterns than the unit holds), that screen would have no patterns
        /// to show or adjust, so the app stays here with the reason in red at the foot.
        /// </summary>
        void ShowAlignIfItFits(SongSession session)
        {
            if (session.Plan != null)
            {
                cannotExport = cannotExportLayout = null;
                analysedLayout = LayoutKey();
                uiState.Show(UIStates.Align);
                return;
            }
            cannotExport = session.PlanError;
            cannotExportLayout = LayoutKey();
            analysedLayout = null;
            jobs.SetIdleMessage("These files cannot be exported");
            Rebuild();
        }

        /// <summary>
        /// The package the kept analysis made, while the files and the strips are as they were for
        /// it; null once anything that bears on the package has changed.
        /// </summary>
        WavExportResult AnalysedPlan()
        {
            SongSession session = app.Session;
            return session != null && session.Plan != null && session.FilesVersion == app.Strips.FilesVersion
                && analysedLayout != null && analysedLayout == LayoutKey() ? session.Plan : null;
        }

        /// <summary>What a strip's files take of the unit's memory: the analysed package's samples when there is one, otherwise the estimate.</summary>
        static long BytesOn(StripLayout layout, OutputStrip strip, WavExportResult analysed)
        {
            if (analysed == null)
                return layout.EstimatedBytesOn(strip);
            if (analysed.Layers.Count == 0)
                return strip.FileCount > 0 ? analysed.SampleBytes : 0;
            long bytes = 0;
            for (int layer = 0; layer < analysed.Layers.Count; layer++)
                if (analysed.Layers[layer].Group == strip.Pair)
                    bytes += analysed.BytesOfLayer(layer);
            return bytes;
        }

        /// <summary>Whether the strips are as they were when the last analysis found they make no package.</summary>
        bool CannotExport()
        {
            return cannotExport != null && cannotExportLayout == LayoutKey();
        }

        /// <summary>
        /// The strips as they bear on the package: which files are on which output, split or not,
        /// summed or not, and all on master or not. Any change to one of them changes this.
        /// </summary>
        string LayoutKey()
        {
            StripLayout layout = app.Strips;
            var key = new System.Text.StringBuilder();
            key.Append(layout.AllOnMaster ? 'M' : 'P');
            foreach (OutputStrip strip in layout.Strips)
            {
                key.Append('|').Append(strip.Split ? 'S' : 'J').Append(strip.KeepApart ? 'A' : 'T');
                foreach (List<StripFile> slot in strip.Slots)
                {
                    key.Append('/');
                    foreach (StripFile file in slot)
                        key.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(file).ToString(Inv)).Append(',');
                }
            }
            return key.ToString();
        }

        /// <summary>The strip each stem is on, which is its group.</summary>
        static List<int> StripsOf(List<Stem> stems)
        {
            return stems.ConvertAll(stem => stem.Group);
        }

        /// <summary>
        /// A name for the project from the files: the one file's name, or for stems the stem file
        /// they all came out of, or what their names share, or failing that the folder they are in.
        /// </summary>
        static string DefaultName(List<StripFile> files)
        {
            if (files.Count == 0)
                return null;
            if (files.Count == 1)
                return files[0].Song == null ? Path.GetFileNameWithoutExtension(files[0].Path) : files[0].Song + " " + files[0].Name;
            if (FromOneStemFile(files))
                return files[0].Song;
            string shared = StemsToSp16.SongName(files.ConvertAll(file => file.Name));
            return shared.Length >= 2 ? shared : Path.GetFileName(Path.GetDirectoryName(files[0].Path));
        }

        static bool FromOneStemFile(List<StripFile> files)
        {
            return files[0].Song != null && files.TrueForAll(file => file.Path == files[0].Path);
        }

        /// <summary>What to call one file on screen: its own name, or the stem and the stem file it came out of.</summary>
        static string DisplayName(StripFile file)
        {
            return file.Song == null ? Path.GetFileName(file.Path) : file.Name + " of " + Path.GetFileName(file.Path);
        }

        BandPeaks BandsOf(StripFile file)
        {
            BandPeaks made;
            if (!bands.TryGetValue(file, out made))
                bands[file] = made = new BandPeaks(file.Audio.Samples, file.Audio.SampleRate);
            return made;
        }

        /// <summary>Draws the strips and the figures again from what is loaded. Called after every change.</summary>
        void Rebuild()
        {
            StripLayout layout = app.Strips;
            // A name from the files, each time the files change. It gives way to one the user has typed.
            if (layout.FilesVersion != namedVersion)
            {
                namedVersion = layout.FilesVersion;
                if (layout.FileCount > 0)
                    app.SuggestProjectName(DefaultName(layout.Files));
            }
            BuildOutputsChoice(layout);
            EstimatePatterns(layout);
            // The cards are made again, so one that was being dragged is no longer there to drop.
            EndDrag();
            places.Clear();
            titles.Clear();
            List<StripFile> all = layout.Files;
            if (all.Count > 1)
            {
                string[] labels = StemsToSp16.Labels(all.ConvertAll(file => file.Name));
                for (int i = 0; i < all.Count; i++)
                    titles[all[i]] = labels[i];
            }
            strips.Clear();
            foreach (OutputStrip strip in layout.Strips)
                strips.Add(BuildStrip(layout, strip));
            Controls.KeepKeysOffButtons(strips);
            ShowFigures(layout);
        }

        /// <summary>
        /// The choice of where the strips play from: the pair of outputs each stands for, or all of
        /// them from the master output, for a unit that has only that connected. Without it the
        /// only way to keep stems apart on master was to pile them on one strip as separate tracks.
        /// </summary>
        void BuildOutputsChoice(StripLayout layout)
        {
            outputsHost.Clear();
            outputsHost.Add(Controls.Segmented(new[] { "Master", "1 to 8" }, layout.AllOnMaster ? 0 : 1, chosen =>
            {
                app.SetAllOnMaster(chosen == 0);
                Rebuild();
            }));
        }

        VisualElement BuildStrip(StripLayout layout, OutputStrip strip)
        {
            var box = new VisualElement();
            box.AddToClassList("strip");
            box.EnableInClassList("strip--last", strip.Pair == layout.Strips.Length - 1);
            places.Add(new DropPlace { Area = box, Strip = strip.Pair, Slot = -1 });
            int firstOutput = 2 * strip.Pair + 1, secondOutput = firstOutput + 1;
            string pairName = Sp16Project.OutputName((Sp16Output)strip.Pair);
            string splitName = string.Format(Inv, "{0} and {1}", firstOutput, secondOutput);
            // A split strip is two places for files. With everything on master no strip is split, whatever it is set to.
            bool split = layout.PlacesOn(strip) == 2;

            var header = new VisualElement();
            header.AddToClassList("strip__header");
            var badge = new Label(layout.AllOnMaster ? "Master" : split ? splitName : pairName);
            badge.AddToClassList("badge");
            // One file by itself plays from the master output, so no pair's colour is its own.
            if (layout.IsFullSong)
                badge.AddToClassList("badge--idle");
            else
                badge.style.backgroundColor = Theme.Strip(strip.Pair);
            header.Add(badge);
            var state = new Label(DescribeState(layout, strip));
            state.AddToClassList("strip__state");
            header.Add(state);
            box.Add(header);

            // Both ways of taking the pair are shown, so a split strip says that it is split and how to join it.
            // On master there is no pair to take either way.
            if (!layout.AllOnMaster)
                box.Add(Controls.Segmented(new[] { pairName + " stereo", splitName + " mono" }, strip.Split ? 1 : 0, chosen =>
                {
                    strip.SetSplit(chosen == 1);
                    Rebuild();
                }));
            bool shared = layout.CountIn(strip, 0) > 1 || (split && layout.CountIn(strip, 1) > 1);
            if (shared)
            {
                box.Add(Controls.Segmented(new[] { "Separate tracks", "Summed tracks" }, strip.KeepApart ? 0 : 1, chosen =>
                {
                    strip.KeepApart = chosen == 0;
                    Rebuild();
                }));
                // Said here, as soon as the tracks are summed, and not only once they are analysed.
                if (!strip.KeepApart && SumClips(layout, strip))
                {
                    var note = new Label("Too loud summed, will distort");
                    note.AddToClassList("strip__note");
                    box.Add(note);
                }
            }

            var body = new ScrollView(ScrollViewMode.Vertical);
            body.AddToClassList("strip__body");
            for (int slot = 0; slot < layout.PlacesOn(strip); slot++)
            {
                List<StripFile> files = layout.FilesIn(strip, slot);
                int target = slot;
                VisualElement place = body;
                if (split)
                {
                    place = new VisualElement();
                    place.AddToClassList("well");
                    string sharing = files.Count == 0 ? "  ·  empty" : files.Count == 1 ? "" : strip.KeepApart ? "  ·  " + files.Count.ToString(Inv) + " tracks" : "  ·  " + files.Count.ToString(Inv) + " summed";
                    var caption = new Label("Output " + Sp16Project.OutputName(strip.OutputOf(slot)) + "  ·  mono" + sharing);
                    caption.AddToClassList("well__caption");
                    place.Add(caption);
                    body.Add(place);
                    places.Add(new DropPlace { Area = place, Strip = strip.Pair, Slot = slot });
                }
                foreach (StripFile file in files)
                    place.Add(BuildFile(layout, strip, slot, file));
                place.Add(split ? AddButton(strip, target) : AddArea(layout, strip, files));
            }
            box.Add(body);
            return box;
        }

        /// <summary>Whether the files of a strip clip for long enough to be heard when they are summed, on either output of a split strip.</summary>
        bool SumClips(StripLayout layout, OutputStrip strip)
        {
            for (int place = 0; place < layout.PlacesOn(strip); place++)
            {
                List<StripFile> files = layout.FilesIn(strip, place);
                if (files.Count > 1 && SumClips(files.ToArray()))
                    return true;
            }
            return false;
        }

        bool SumClips(StripFile[] files)
        {
            foreach (KeyValuePair<StripFile[], bool> known in sumsThatClip)
                if (SameFiles(known.Key, files))
                    return known.Value;
            var parts = new short[files.Length][];
            for (int i = 0; i < files.Length; i++)
                parts[i] = files[i].Audio.Samples;
            bool clips = StemsToSp16.ClipsAudibly(parts);
            sumsThatClip.Add(new KeyValuePair<StripFile[], bool>(files, clips));
            return clips;
        }

        static bool SameFiles(StripFile[] a, StripFile[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        /// <summary>The small button that adds a file to one output of a split strip.</summary>
        Button AddButton(OutputStrip strip, int slot)
        {
            var add = new Button(() => ChooseFiles(strip.Pair, slot)) { text = "+ Add file" };
            add.AddToClassList("add");
            return add;
        }

        /// <summary>
        /// The rest of a strip, all of which adds a file to it. With files on it, it says no more
        /// than that: a line on what the next file would become ("A second file is mixed with
        /// Drums") was there and only confused, and the strip's own switch says it once there are two.
        /// </summary>
        Button AddArea(StripLayout layout, OutputStrip strip, List<StripFile> files)
        {
            var add = new Button(() => ChooseFiles(strip.Pair, 0));
            add.AddToClassList("add");
            add.AddToClassList("add--fill");
            if (files.Count == 0)
            {
                var waiting = new Label(Waiting[strip.Pair]) { pickingMode = PickingMode.Ignore };
                waiting.AddToClassList("add__waiting");
                add.Add(waiting);
            }
            var title = new Label("+ Add file") { pickingMode = PickingMode.Ignore };
            title.AddToClassList("add__title");
            add.Add(title);
            // With nothing loaded the strips show the four usual stems, so the first one also says that
            // stems are not needed. The others get an empty line, which keeps the four names level.
            string says = files.Count > 0 || layout.FileCount > 0 ? null : strip.Pair == 0 ? "or full song" : " ";
            if (says != null)
            {
                var hint = new Label(says) { pickingMode = PickingMode.Ignore };
                hint.AddToClassList("add__hint");
                add.Add(hint);
            }
            return add;
        }

        /// <summary>A file's name on its card: what sets it apart from the others, or its whole name when it is the only one.</summary>
        string Title(StripFile file)
        {
            string title;
            return titles.TryGetValue(file, out title) ? title : file.Name;
        }

        /// <summary>The whole of what a file is, for when the pointer is over its card: its name in full and the folder it is in.</summary>
        static string FullName(StripFile file)
        {
            string from = Path.GetFileName(file.Path), folder = Path.GetFileName(Path.GetDirectoryName(file.Path));
            return (file.Song == null ? from : file.Name + ", stem of " + from) + "\nin " + folder;
        }

        VisualElement BuildFile(StripLayout layout, OutputStrip strip, int slot, StripFile file)
        {
            var card = new VisualElement();
            card.AddToClassList("file");
            // Before the hint, so that a card being dragged can keep the hint from coming back.
            MakeDraggable(card, file, strip.Pair);
            Controls.Hint(card, FullName(file));

            var row = new VisualElement();
            row.AddToClassList("file__row");
            var name = new Label(Title(file));
            name.AddToClassList("file__name");
            row.Add(name);
            // To the strip on either side, for when a file has landed on the wrong one. Dragging the card does the same.
            row.Add(MoveButton(layout, file, strip.Pair - 1, "←"));
            row.Add(MoveButton(layout, file, strip.Pair + 1, "→"));
            if (layout.PlacesOn(strip) == 2)
            {
                var move = new Button(() =>
                {
                    app.Strips.MoveAcross(file);
                    Rebuild();
                });
                move.text = "to " + Sp16Project.OutputName(strip.OutputOf(1 - slot));
                row.Add(move);
            }
            var remove = new Button(() =>
            {
                app.Strips.Remove(file);
                peaks.Remove(file);
                bands.Remove(file);
                if (app.Strips.FileCount == 0)
                    app.ForgetProjectName();
                Rebuild();
            });
            remove.text = "×";
            remove.AddToClassList("icon");
            row.Add(remove);
            card.Add(row);

            var details = new Label(Describe(layout, strip, slot, file));
            details.AddToClassList("file__details");
            card.Add(details);

            // One file by itself plays from the master output, so it is coloured by its sound and not by an output.
            WaveformPeaks wave;
            peaks.TryGetValue(file, out wave);
            card.Add(new MiniWave(layout.IsFullSong ? WavePicture.OfBands(BandsOf(file)) : wave == null ? null : new WavePicture(wave, Theme.Strip(strip.Pair))));
            return card;
        }

        /// <summary>A small button on a file's card that moves it to the next strip along, switched off where there is none.</summary>
        Button MoveButton(StripLayout layout, StripFile file, int to, string arrow)
        {
            var move = new Button(() =>
            {
                if (app.Strips.MoveTo(file, to))
                    Rebuild();
            });
            move.text = arrow;
            move.AddToClassList("icon");
            move.SetEnabled(to >= 0 && to < layout.Strips.Length);
            return move;
        }

        /// <summary>
        /// Lets a file's card be dragged to another strip, or to the other output of a split one.
        /// The card stays where it is, dimmed, with the file's name beside the pointer, and the
        /// strip it would land on is lit. A press that does not move is left alone.
        /// </summary>
        void MakeDraggable(VisualElement card, StripFile file, int from)
        {
            card.RegisterCallback<PointerDownEvent>(evt =>
            {
                // The buttons on a card are pressed, not dragged.
                for (VisualElement at = evt.target as VisualElement; at != null && at != card; at = at.parent)
                    if (at is Button)
                        return;
                if (evt.button != 0 || drag != null)
                    return;
                drag = new Drag { File = file, Card = card, Down = evt.position, Pointer = evt.pointerId, From = from };
                card.CapturePointer(evt.pointerId);
            });
            card.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (drag == null || drag.Card != card)
                    return;
                Vector2 pointer = evt.position;
                if (drag.Ghost == null && (pointer - drag.Down).magnitude < DragStartPixels)
                    return;
                DragTo(pointer);
                evt.StopImmediatePropagation();
            });
            card.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (drag == null || drag.Card != card)
                    return;
                bool dragged = drag.Ghost != null;
                StripFile moved = drag.File;
                int place = dragged ? PlaceAt(evt.position) : -1;
                EndDrag();
                if (place >= 0 && app.Strips.MoveTo(moved, places[place].Strip, places[place].Slot))
                    Rebuild();
            });
            // The pointer can be taken away without being let go, as when the window loses it.
            card.RegisterCallback<PointerCaptureOutEvent>(evt =>
            {
                if (drag != null && drag.Card == card)
                    EndDrag();
            });
        }

        /// <summary>Shows a dragged card's name at the pointer and lights the place it is over.</summary>
        void DragTo(Vector2 pointer)
        {
            VisualElement top = uiState.Root.Q(className: "root");
            if (drag.Ghost == null)
            {
                Controls.HideTip(drag.Card);
                drag.Card.AddToClassList(DraggingClass);
                drag.Ghost = new Label(Title(drag.File)) { pickingMode = PickingMode.Ignore };
                drag.Ghost.AddToClassList("drag-ghost");
                // One file by itself has no strip's colour.
                drag.Ghost.style.backgroundColor = app.Strips.IsFullSong ? Theme.WaveAll : Theme.Strip(drag.From);
                top.Add(drag.Ghost);
            }
            Vector2 at = top.WorldToLocal(pointer);
            drag.Ghost.style.left = at.x + 12;
            drag.Ghost.style.top = at.y + 8;
            drag.Last = pointer;
            if (drag.Scrolling == null)
                drag.Scrolling = drag.Card.schedule.Execute(ScrollWhileDragging).Every(ScrollTickMs);
            LightPlaceUnder(pointer);
        }

        void LightPlaceUnder(Vector2 pointer)
        {
            int over = PlaceAt(pointer);
            for (int i = 0; i < places.Count; i++)
                places[i].Area.EnableInClassList(places[i].Slot < 0 ? StripDropClass : WellDropClass, i == over && WouldMove(places[i]));
        }

        /// <summary>
        /// Scrolls the strip a dragged card is held over when the pointer is near the top or bottom
        /// of its list, or past them, faster the nearer the edge; then lights what is now under it.
        /// </summary>
        void ScrollWhileDragging()
        {
            if (drag == null || drag.Ghost == null)
                return;
            Vector2 pointer = drag.Last;
            foreach (ScrollView body in strips.Query<ScrollView>(className: "strip__body").ToList())
            {
                Rect view = body.worldBound, strip = body.parent.worldBound;
                if (pointer.x < view.xMin || pointer.x > view.xMax)
                    continue;
                float step = 0;
                if (pointer.y < view.yMin + ScrollEdge && pointer.y > strip.yMin - ScrollEdge)
                    step = -MostScrollPerTick * Mathf.Clamp01((view.yMin + ScrollEdge - pointer.y) / ScrollEdge);
                else if (pointer.y > view.yMax - ScrollEdge)
                    step = MostScrollPerTick * Mathf.Clamp01((pointer.y - (view.yMax - ScrollEdge)) / ScrollEdge);
                if (step == 0)
                    return;
                float most = Mathf.Max(0, body.contentContainer.layout.height - body.contentViewport.layout.height);
                Vector2 offset = body.scrollOffset;
                float y = Mathf.Clamp(offset.y + step, 0, most);
                if (Mathf.Approximately(y, offset.y))
                    return;
                body.scrollOffset = new Vector2(offset.x, y);
                LightPlaceUnder(pointer);
                return;
            }
        }

        /// <summary>Whether dropping the dragged card on a place would move it: not on the strip or the output it is on already.</summary>
        bool WouldMove(DropPlace place)
        {
            OutputStrip target = app.Strips.Strips[place.Strip];
            if (place.Slot >= 0)
                return !target.Slots[place.Slot].Contains(drag.File);
            return place.Strip != drag.From;
        }

        /// <summary>The place under the pointer: an output of a split strip before the strip it is part of. -1 when it is over none.</summary>
        int PlaceAt(Vector2 pointer)
        {
            int found = -1;
            for (int i = 0; i < places.Count; i++)
                if (places[i].Area.worldBound.Contains(pointer) && (found < 0 || places[i].Slot >= 0))
                    found = i;
            // On a split strip but on neither of its outputs, the file stays on the strip it is on.
            if (found >= 0 && places[found].Slot < 0 && places[found].Strip == drag.From)
                return -1;
            return found;
        }

        /// <summary>Puts everything back as it was before a card was picked up. The drop itself is the caller's to make.</summary>
        void EndDrag()
        {
            if (drag == null)
                return;
            Drag ended = drag;
            // Cleared first: letting go of the pointer comes back here.
            drag = null;
            if (ended.Scrolling != null)
                ended.Scrolling.Pause();
            if (ended.Card.HasPointerCapture(ended.Pointer))
                ended.Card.ReleasePointer(ended.Pointer);
            ended.Card.RemoveFromClassList(DraggingClass);
            if (ended.Ghost != null)
                ended.Ghost.RemoveFromHierarchy();
            foreach (DropPlace place in places)
                place.Area.RemoveFromClassList(place.Slot < 0 ? StripDropClass : WellDropClass);
        }

        string DescribeState(StripLayout layout, OutputStrip strip)
        {
            if (strip.FileCount == 0)
                return "";
            string cost = Megabytes(BytesOn(layout, strip, AnalysedPlan()));
            // With everything on master the badge has said where it plays from.
            return layout.IsFullSong ? (layout.AllOnMaster ? "full song  ·  " : "full song, on master  ·  ") + cost : cost;
        }

        /// <summary>A file's length, what it is, and what it costs when it is a sample of its own.</summary>
        static string Describe(StripLayout layout, OutputStrip strip, int slot, StripFile file)
        {
            WavData wav = file.Audio;
            string channels = wav.SourceChannels == 1 ? "mono" : "stereo";
            bool asMono = layout.IsMono(strip);
            // A file that was at another rate says so in place of its bit depth: there is not room for both.
            string kind = wav.SourceSampleRate != 0 ? "from " + RateText(wav.SourceSampleRate) + ", " + (asMono && wav.SourceChannels == 2 ? "as mono" : channels)
                : !asMono ? string.Format(Inv, "{0}-bit {1}", wav.SourceBitsPerSample, channels)
                : wav.SourceChannels == 1 ? "mono" : "stereo, as mono";
            bool ownSample = layout.IsFullSong || strip.KeepApart || layout.CountIn(strip, slot) == 1;
            // Every sample is as long as the song, so a short file costs what the longest does.
            long bytes = (long)layout.Frames * (asMono ? Sp16Project.MonoBytesPerFrame : Sp16Project.StereoBytesPerFrame);
            return Clock(wav.Seconds) + "  ·  " + kind + "  ·  " + (ownSample ? Megabytes(bytes) : "summed");
        }

        void ShowFigures(StripLayout layout)
        {
            WavExportResult analysed = AnalysedPlan();
            var shares = new List<KeyValuePair<Color, long>>();
            foreach (OutputStrip strip in layout.Strips)
                shares.Add(new KeyValuePair<Color, long>(layout.IsFullSong ? Theme.WaveAll : Theme.Strip(strip.Pair), BytesOn(layout, strip, analysed)));
            uiState.ShowMemory(shares);
            // This screen has the one key, for Add files, which is not listed.
            uiState.SetShortcuts("");

            int count = layout.FileCount, tracks = layout.Tracks, patterns = layout.PatternsPerScene;
            string length = count == 0 ? "" : Clock((double)layout.Frames / layout.Files[0].Audio.SampleRate);
            bool fits = count > 0 && patterns > 0;
            statTracks.text = count == 0 ? "–" : tracks.ToString(Inv);
            statTracksCaption.text = tracks == 1 ? "track per pattern" : "tracks per pattern";
            statPatterns.text = fits ? patterns.ToString(Inv) : "–";
            statPatternsCaption.text = patterns == 1 ? "pattern per scene" : "patterns per scene";
            statBars.text = fits ? (patterns * Sp16Project.Scenes * 4).ToString(Inv) : "–";
            statLength.text = count == 0 ? "–" : length;
            // The song's own length in bars, against the room: every pattern takes four bars of room,
            // the lead-in's and a short last one too. Guessed (~) until Analyse has counted it.
            bool exact;
            int songPatterns = SongPatterns(out exact);
            bool tooLong = fits && songPatterns > patterns * Sp16Project.Scenes;
            statNeeded.text = count == 0 ? "–" : songPatterns == 0 ? "…" : (exact ? "" : "~") + (4 * songPatterns).ToString(Inv);
            statNeeded.EnableInClassList("stat__value--bad", tooLong);

            bool bad = false;
            if (count == 0)
            {
                summary.text = "Add a song as a single file or as multiple stems.";
            }
            else if (patterns == 0)
            {
                bad = true;
                summary.text = string.Format(Inv, "{0} tracks, but a scene has {1} pads. ", tracks, Sp16Project.PadsPerScene) + WavToSp16.WhatWouldHelp(layout.CanSum, false);
            }
            else if (CannotExport())
            {
                bad = true;
                summary.text = cannotExport;
            }
            else if (TooManyTracksForSong())
            {
                bad = true;
                summary.text = WavToSp16.TooManyTracks(tracks, songPatterns, layout.CanSum);
            }
            else if (tooLong)
            {
                // Only a guess so far, so Analyse can still be tried: it will count the patterns.
                bad = true;
                int most = WavToSp16.MostTracksFor(songPatterns);
                summary.text = most == 0 ? "Likely too large to fit on SP-16."
                    : string.Format(Inv, "Likely too large to fit with {0} tracks. Fits with about {1} or fewer. ", tracks, most) + WavToSp16.WhatWouldHelp(layout.CanSum, false);
            }
            else if (analysed == null && layout.EstimatedBytes > Sp16Project.SampleMemoryBytes)
            {
                bad = true;
                summary.text = string.Format(Inv, "Needs about {0:0} MB of {1:0}. ",
                    Sp16Project.Megabytes(layout.EstimatedBytes), Sp16Project.Megabytes(Sp16Project.SampleMemoryBytes))
                    + WavToSp16.WhatWouldHelp(layout.CanSum, layout.CanUseMono);
            }
            else if (count == 1)
            {
                summary.text = "One file alone is full song, on master.";
            }
            else
            {
                summary.text = "WAV, AIFF, FLAC, MP3, M4A or stem files (.stem.mp4). Other rates converted to 44.1 kHz.";
            }
            summary.EnableInClassList("summary__message--bad", bad);

            bool canContinue = CanContinue();
            uiState.SetAlignAvailable(canContinue);
            analyseText = canContinue ? "Analyse again" : "Analyse";
            analyse.EnableInClassList(PrimaryClass, !canContinue);

            List<StripFile> files = layout.Files;
            info.text = count == 0 ? ""
                : count == 1 ? DisplayName(files[0]) + "  ·  " + length
                : count.ToString(Inv) + " stems" + (FromOneStemFile(files) ? " of " + Path.GetFileName(files[0].Path) : "") + "  ·  " + length;
        }

        static string Megabytes(long bytes)
        {
            return string.Format(Inv, "{0:0.0} MB", Sp16Project.Megabytes(bytes));
        }

        /// <summary>A sample rate as it is usually said: 48 kHz, 44.1 kHz.</summary>
        static string RateText(int hertz)
        {
            return (hertz / 1000.0).ToString("0.###", Inv) + " kHz";
        }

        /// <summary>What to add to "Loaded ..." when files were at another rate and have been converted.</summary>
        static string ConvertedNote(Loaded loaded)
        {
            var rates = new List<int>();
            var files = new List<StripFile>(loaded.Files);
            foreach (List<StripFile> stems in loaded.StemFiles)
                files.AddRange(stems);
            foreach (StripFile file in files)
                if (file.Audio.SourceSampleRate != 0 && !rates.Contains(file.Audio.SourceSampleRate))
                    rates.Add(file.Audio.SourceSampleRate);
            return rates.Count == 0 ? "" : rates.Count == 1 ? ", converted from " + RateText(rates[0]) + " to 44.1 kHz" : ", converted to 44.1 kHz";
        }

        static string Clock(double seconds)
        {
            int minutes = (int)(seconds / 60);
            return string.Format(Inv, "{0}:{1:00.0}", minutes, seconds - 60 * minutes);
        }

        void ShowError(string message)
        {
            error.text = message ?? "";
            error.EnableInClassList(HiddenClass, string.IsNullOrEmpty(message));
        }
    }
}
