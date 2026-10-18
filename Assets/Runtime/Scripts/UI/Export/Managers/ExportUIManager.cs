using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Strands.Applications.Core;
using Strands.Applications.Session;
using Strands.Export;
using Strands.Playback;
using Strands.Sp16;
using Strands.UI.Shared;
using Strands.UI.State;
using Strands.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Strands.UI.Export
{
    /// <summary>
    /// The Export page: a scene's sixteen pads as the unit will show them, top row first, and what
    /// the package is set to. A pad carries its own choke and loop; the rows beside the pads set
    /// every pad of a stem, or of the whole song, over whatever its pads were set to one at a
    /// time. The package is written from here, and what there is to say about it is on the page.
    /// </summary>
    public class ExportUIManager : MonoBehaviour
    {
        const string HiddenClass = "hidden";
        const string OnClass = "on";
        const string BadClass = "warnings--bad";
        const string Keys = "Space stop  ·  ← → scene";
        const string QuietClass = "pads__stop--quiet";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] OffOn = { "Off", "On" };

        [SerializeField] UIStateManager uiState;
        [SerializeField] ApplicationManager app;
        [SerializeField] JobManager jobs;

        VisualElement screen, padGrid, stemRows, stretchHost, arrangementHost, arrangementTempoHost, packageNotes;
        Label padsCount, sceneName, packageFacts, packageWritten;
        Button exportButton, scenePrevious, sceneNext, showFolder, stopPads;
        // Plays the pads. It has to be on the listener's object to be heard, which is where the audition is.
        PadPlayer pads;

        // A pad of the scene that can be played: what to light while it sounds.
        sealed class PadView
        {
            public int Key, Chunk, Layer;
            public VisualElement Face;
            public PadWave Wave;
            public PlaySymbol Symbol;
            public Color Edge;
            public bool Lit;
        }

        readonly List<PadView> padViews = new List<PadView>();
        SongSession session;
        // The picture each layer's pads are cut from, made when first wanted.
        readonly Dictionary<int, WavePicture> pictures = new Dictionary<int, WavePicture>();
        int scene;
        // Where the package was last written, for as long as the page still shows what is in it.
        string writtenPath;
        // The frame the page was opened in. The press of the button or of Cmd+E that opened it is
        // also seen here, in that frame, and must not go on to write the package.
        int openedFrame = -1;

        void Start()
        {
            VisualElement root = uiState.Root;
            screen = root.Q("exportScreen");
            padGrid = root.Q("padGrid");
            stemRows = root.Q("stemRows");
            stretchHost = root.Q("stretchHost");
            arrangementHost = root.Q("arrangementHost");
            arrangementTempoHost = root.Q("arrangementTempoHost");
            padsCount = root.Q<Label>("padsCount");
            sceneName = root.Q<Label>("sceneName");
            packageFacts = root.Q<Label>("packageFacts");
            packageNotes = root.Q("packageNotes");
            packageNotes.RegisterCallback<GeometryChangedEvent>(_ => FitNotes());
            packageWritten = root.Q<Label>("packageWritten");
            exportButton = root.Q<Button>("export");
            scenePrevious = root.Q<Button>("scenePrevious");
            sceneNext = root.Q<Button>("sceneNext");
            showFolder = root.Q<Button>("showFolder");

            stopPads = root.Q<Button>("stopPads");
            pads = FindAnyObjectByType<AuditionPlayer>().gameObject.AddComponent<PadPlayer>();

            exportButton.clicked += Export;
            stopPads.clicked += pads.StopAll;
            scenePrevious.clicked += () => StepScene(-1);
            sceneNext.clicked += () => StepScene(1);
            showFolder.clicked += () => Application.OpenURL(new Uri(Path.GetDirectoryName(writtenPath)).AbsoluteUri);
            uiState.StateChanged += OnStateChanged;
            uiState.StepRequested += OnStepRequested;
        }

        void OnDestroy()
        {
            uiState.StateChanged -= OnStateChanged;
            uiState.StepRequested -= OnStepRequested;
            Unbind();
        }

        void OnStateChanged(UIStates state)
        {
            if (state != UIStates.Export)
            {
                Unbind();
                return;
            }
            Unbind();
            session = app.Session;
            session.Changed += OnSessionChanged;
            pictures.Clear();
            scene = 0;
            writtenPath = null;
            openedFrame = Time.frameCount;
            uiState.SetShortcuts(Keys);
            Refresh();
        }

        // The first two steps at the top go back to the files and to the analysis.
        void OnStepRequested(UIStates state)
        {
            if (uiState.State == UIStates.Export && state != UIStates.Export && !jobs.Busy)
                uiState.Show(state);
        }

        void Unbind()
        {
            if (session == null)
                return;
            pads.StopAll();
            session.Changed -= OnSessionChanged;
            session = null;
            pictures.Clear();
        }

        // Anything changed after a package was written is not in it, so the page stops saying where it is.
        void OnSessionChanged()
        {
            // What the page is left set to is where the next song starts.
            app.RememberExport(session);
            writtenPath = null;
            Refresh();
            // A change of a pad's settings leaves what is sounding sounding, with what the pad now
            // holds: a pad set to loop goes round, one taken off its loop ends at its end.
            WavExportResult plan = session.Plan;
            foreach (int key in pads.PlayingKeys())
            {
                int chunk = key / Sp16Project.PadsPerScene, layer = key % Sp16Project.PadsPerScene;
                if (plan == null || chunk >= plan.Chunks.Count || layer >= LayersOf(plan))
                {
                    pads.Stop(key);
                    continue;
                }
                bool mono = plan.Layers.Count > 0 && plan.Layers[layer].IsMono;
                pads.Change(key, session.SampleOfPad(plan, chunk, layer), mono ? 1 : 2,
                    plan.LoopOf(chunk, layer), plan.ChokeOf(chunk, layer) ? layer + 1 : 0);
            }
        }

        void Update()
        {
            if (session == null || uiState.State != UIStates.Export || Time.frameCount == openedFrame)
                return;
            bool idle = !jobs.Busy;
            ShowPlaying();
            screen.SetEnabled(idle);
            exportButton.SetEnabled(idle && session.Plan != null);
            Keyboard keys = Keyboard.current;
            if (!idle || keys == null || Controls.IsTyping(uiState.Root))
                return;
            bool command = keys.leftCommandKey.isPressed || keys.rightCommandKey.isPressed || keys.leftCtrlKey.isPressed || keys.rightCtrlKey.isPressed;
            if (command)
            {
                if (keys.eKey.wasPressedThisFrame)
                    Export();
                return;
            }
            if (keys.spaceKey.wasPressedThisFrame)
                pads.StopAll();
            if (keys.leftArrowKey.wasPressedThisFrame)
                StepScene(-1);
            if (keys.rightArrowKey.wasPressedThisFrame)
                StepScene(1);
        }

        int LastScene
        {
            get
            {
                WavExportResult plan = session.Plan;
                return plan == null || plan.Chunks.Count == 0 ? 0 : plan.Chunks[plan.Chunks.Count - 1].Scene;
            }
        }

        void StepScene(int direction)
        {
            int to = Mathf.Clamp(scene + direction, 0, LastScene);
            if (to == scene)
                return;
            scene = to;
            pads.StopAll();
            Refresh();
        }

        /// <summary>Draws the page again from the session. Called after every change to it.</summary>
        void Refresh()
        {
            WavExportResult plan = session.Plan;
            scene = Mathf.Clamp(scene, 0, LastScene);
            sceneName.text = string.Format(Inv, "Scene {0} of {1}", scene + 1, LastScene + 1);
            scenePrevious.SetEnabled(scene > 0);
            sceneNext.SetEnabled(scene < LastScene);
            ShowPads(plan);
            ShowRows(plan);
            ShowChoices();
            ShowPackage(plan);
            Controls.KeepKeysOffButtons(screen);
        }

        static int LayersOf(WavExportResult plan)
        {
            return plan.SamplesPerChunk;
        }

        // A layer's strip, which gives it its colour and its name; a song in one file has neither.
        static int StripOf(WavExportResult plan, int layer)
        {
            return plan.Layers.Count == 0 ? -1 : plan.Layers[layer].Group;
        }

        /// <summary>
        /// What a pad is called: its strip, as everywhere on the Analysis page, or the track's own
        /// label when its strip holds several tracks, which the strip's name would not tell apart.
        /// </summary>
        string NameOf(WavExportResult plan, ChunkInfo chunk, int layer)
        {
            // A song's pad is called what its sample is called on the unit.
            if (plan.Layers.Count == 0)
                return Path.GetFileNameWithoutExtension(chunk.FileName);
            int sharing = 0;
            foreach (ExportLayer other in plan.Layers)
                if (other.Group == plan.Layers[layer].Group)
                    sharing++;
            return sharing > 1 ? StripLayout.NameOf(plan.Layers[layer].Group, plan.Layers[layer].Label, 1) : session.NameOfStrip(plan.Layers[layer].Group);
        }

        /// <summary>
        /// The waveform a layer's samples are cut from, in its strip's colour: a stem, the tracks
        /// of a strip or of one output of a split strip summed, or the song by its low, middle and
        /// high sound. Null where a layer cannot be matched to audio the session holds.
        /// </summary>
        WavePicture PictureOf(WavExportResult plan, int layer)
        {
            WavePicture picture;
            if (pictures.TryGetValue(layer, out picture))
                return picture;
            if (session.Stems == null)
            {
                picture = session.Bands == null ? null : WavePicture.OfBands(session.Bands);
            }
            else if (layer < session.Stems.LayerStems.Count)
            {
                picture = new WavePicture(session.PeaksOfLayer(layer), Theme.Strip(plan.Layers[layer].Group));
            }
            pictures[layer] = picture;
            return picture;
        }

        static string PatternName(ChunkInfo chunk)
        {
            return chunk.IsLeadIn ? "Lead-in" : chunk.IsLeadOut ? "Lead-out" : "Pattern " + (chunk.Pattern + 1).ToString(Inv);
        }

        /// <summary>
        /// The scene's pads, laid out as they are on the unit: its tracks are numbered from the
        /// bottom row up, and the patterns fill the scene from the top row down.
        /// </summary>
        void ShowPads(WavExportResult plan)
        {
            padGrid.Clear();
            padViews.Clear();
            // Which chunk and layer each pad of the scene holds, or nothing.
            var chunkOf = new ChunkInfo[Sp16Project.PadsPerScene];
            var layerOf = new int[Sp16Project.PadsPerScene];
            int samples = 0;
            if (plan != null)
                foreach (ChunkInfo chunk in plan.Chunks)
                {
                    if (chunk.Scene != scene)
                        continue;
                    for (int layer = 0; layer < chunk.FileNames.Length; layer++)
                    {
                        if (chunk.FileNames[layer] == null)
                            continue;
                        chunkOf[chunk.PadOf(layer)] = chunk;
                        layerOf[chunk.PadOf(layer)] = layer;
                        samples++;
                    }
                }
            padsCount.text = samples == 1 ? "1 sample" : samples.ToString(Inv) + " samples";

            for (int place = 0; place < Sp16Project.PadsPerScene; place++)
            {
                int pad = Sp16Project.PadInReadingOrder(place);
                padGrid.Add(chunkOf[pad] == null ? EmptyPad(pad) : Pad(plan, chunkOf[pad], layerOf[pad], pad));
            }
        }

        static VisualElement PadShell(int pad, string name, out VisualElement face)
        {
            var cell = new VisualElement();
            cell.AddToClassList("pad");
            face = new VisualElement();
            face.AddToClassList("pad__face");
            cell.Add(face);
            var top = new VisualElement();
            top.AddToClassList("pad__top");
            var title = new Label(name);
            title.AddToClassList("pad__name");
            top.Add(title);
            var track = new Label((pad + 1).ToString("00", Inv));
            track.AddToClassList("pad__track");
            top.Add(track);
            face.Add(top);
            return cell;
        }

        static VisualElement EmptyPad(int pad)
        {
            VisualElement face;
            VisualElement cell = PadShell(pad, "Empty", out face);
            cell.AddToClassList("pad--empty");
            return cell;
        }

        VisualElement Pad(WavExportResult plan, ChunkInfo chunk, int layer, int pad)
        {
            VisualElement face;
            VisualElement cell = PadShell(pad, NameOf(plan, chunk, layer), out face);
            int strip = StripOf(plan, layer);
            // A ground and an edge of the strip's colour, each a colour of its own: a faint wash
            // over a dark ground comes out grey in this project.
            Color colour = strip < 0 ? Theme.WaveAll : Theme.Strip(strip);
            face.style.backgroundColor = Color.Lerp(Theme.Well, colour, strip < 0 ? 0.1f : 0.22f);
            Color edge = Color.Lerp(Theme.Well, colour, strip < 0 ? 0.4f : 0.85f);
            face.style.borderTopColor = edge;
            face.style.borderBottomColor = edge;
            face.style.borderLeftColor = edge;
            face.style.borderRightColor = edge;

            var pattern = new Label(PatternName(chunk));
            pattern.AddToClassList("pad__pattern");
            face.Add(pattern);
            var wave = new PadWave(PictureOf(plan, layer), chunk.StartFrame, chunk.EndFrame - chunk.StartFrame, chunk.LeadingSilenceFrames, chunk.TrailingSilenceFrames);
            face.Add(wave);

            var keys = new VisualElement();
            keys.AddToClassList("pad__keys");
            int index = chunk.Index;
            SongSession song = session;
            if (plan.CanChoke)
            {
                bool choked = plan.ChokeOf(index, layer);
                keys.Add(PadKey("Choke " + (layer + 1).ToString(Inv), choked, () => song.SetChokeOfPad(index, layer, !choked)));
                // Nothing loops with the arrangement on; what the pad is set to shows greyed, and holds again with it off.
                bool looped = plan.Pads.Loop(index, layer);
                Button loop = PadKey("Loop", looped, () => song.SetLoopOfPad(index, layer, !looped));
                loop.SetEnabled(!plan.Arrangement);
                keys.Add(loop);
            }
            var spacer = new VisualElement();
            spacer.AddToClassList("spacer");
            keys.Add(spacer);
            var symbol = new PlaySymbol();
            keys.Add(symbol);
            face.Add(keys);

            var view = new PadView { Key = KeyOf(index, layer), Chunk = index, Layer = layer, Face = face, Wave = wave, Symbol = symbol, Edge = edge };
            padViews.Add(view);
            // A press anywhere on the pad but its two buttons plays it from its start. On the
            // square a sounding pad shows, or with Shift, it stops it.
            face.RegisterCallback<PointerDownEvent>(pressed =>
            {
                if (pressed.button != 0 || jobs.Busy)
                    return;
                for (var at = pressed.target as VisualElement; at != null && at != face; at = at.parent)
                    if (at is Button)
                        return;
                bool onSymbol = false;
                for (var at = pressed.target as VisualElement; at != null && at != face; at = at.parent)
                    onSymbol |= at == view.Symbol;
                if (pressed.shiftKey || (onSymbol && view.Lit))
                    pads.Stop(view.Key);
                else
                    PlayPad(view);
            });
            return cell;
        }

        static int KeyOf(int chunk, int layer) { return chunk * Sp16Project.PadsPerScene + layer; }

        /// <summary>
        /// Plays a pad as the unit will: the sample the package holds, from its start, going round
        /// if the pad loops, and cutting off whatever else of its choke group is sounding. The
        /// unit's time stretch is not heard here: a sample plays at the song's own tempo.
        /// </summary>
        void PlayPad(PadView view)
        {
            WavExportResult plan = session.Plan;
            if (plan == null || view.Chunk >= plan.Chunks.Count)
                return;
            bool mono = plan.Layers.Count > 0 && plan.Layers[view.Layer].IsMono;
            pads.Play(view.Key, session.SampleOfPad(plan, view.Chunk, view.Layer), mono ? 1 : 2, plan.SampleRate,
                plan.LoopOf(view.Chunk, view.Layer), plan.ChokeOf(view.Chunk, view.Layer) ? view.Layer + 1 : 0);
        }

        /// <summary>Lights the pads that are sounding, moves the line across each one's waveform, and shows Stop while any sounds.</summary>
        void ShowPlaying()
        {
            WavExportResult plan = session.Plan;
            stopPads.EnableInClassList(QuietClass, !pads.AnyPlaying);
            foreach (PadView view in padViews)
            {
                double at = plan == null || view.Chunk >= plan.Chunks.Count ? -1 : pads.PositionOf(view.Key);
                bool lit = at >= 0;
                if (lit)
                {
                    // Through the whole sample, the silence a lead-in starts with included, as the pad draws it.
                    ChunkInfo chunk = plan.Chunks[view.Chunk];
                    view.Wave.Progress = (float)(at / Math.Max(1, chunk.EndFrame - chunk.StartFrame + chunk.LeadingSilenceFrames + chunk.TrailingSilenceFrames));
                }
                if (lit == view.Lit)
                    continue;
                view.Lit = lit;
                if (!lit)
                    view.Wave.Progress = -1;
                view.Symbol.Playing = lit;
                Color edge = lit ? Theme.Paper : view.Edge;
                view.Face.style.borderTopColor = edge;
                view.Face.style.borderBottomColor = edge;
                view.Face.style.borderLeftColor = edge;
                view.Face.style.borderRightColor = edge;
            }
        }

        static Button PadKey(string text, bool on, Action pressed)
        {
            var key = new Button(pressed) { text = text, focusable = false };
            key.AddToClassList("pad__key");
            key.EnableInClassList(OnClass, on);
            return key;
        }

        /// <summary>
        /// A row for each strip that holds tracks, and one for them all. A switch shows what its
        /// row was last set to, and pressing it sets every pad the row covers. The row for all
        /// shows neither side when the strips' rows differ.
        /// </summary>
        void ShowRows(WavExportResult plan)
        {
            stemRows.Clear();
            if (plan == null)
                return;
            // Velocity is for every pad; choke and loop only up to a group for each track.
            bool chokeAndLoop = plan.CanChoke;
            if (!chokeAndLoop)
            {
                var none = new Label(string.Format(Inv, "Choke and loop need {0} tracks per pattern or fewer.", Sp16Project.ChokeGroups));
                none.AddToClassList("hint");
                stemRows.Add(none);
            }

            var head = new VisualElement();
            head.AddToClassList("stemrow");
            head.AddToClassList("stemrow--head");
            // A song in one file has the one row, which needs no name: its switches line up with
            // the choices under them (the user, 2026-10-16).
            bool named = plan.Layers.Count > 0;
            if (named)
                head.Add(RowName(null, null));
            if (chokeAndLoop)
            {
                head.Add(RowCaption("Choke group"));
                head.Add(RowCaption("Loop"));
            }
            head.Add(RowCaption("Velocity"));
            stemRows.Add(head);

            var all = new List<int>();
            for (int layer = 0; layer < LayersOf(plan); layer++)
                all.Add(layer);
            if (plan.Layers.Count > 0)
            {
                var strips = new List<int>();
                foreach (ExportLayer layer in plan.Layers)
                    if (!strips.Contains(layer.Group))
                        strips.Add(layer.Group);
                foreach (int strip in strips)
                {
                    var layers = new List<int>();
                    for (int layer = 0; layer < plan.Layers.Count; layer++)
                        if (plan.Layers[layer].Group == strip)
                            layers.Add(layer);
                    // A strip with one track names its group; one with several has a group for each.
                    string[] choke = { "Off", layers.Count == 1 ? (layers[0] + 1).ToString(Inv) : "On" };
                    stemRows.Add(Row(plan, session.NameOfStrip(strip), Theme.Strip(strip), layers, choke, false, chokeAndLoop));
                }
            }
            VisualElement last = Row(plan, named ? "All" : null, null, all, OffOn, named, chokeAndLoop);
            // The last row has no space under it, so the gap to Time stretch is the panel's own.
            last.AddToClassList("stemrow--last");
            stemRows.Add(last);
        }

        static VisualElement RowName(string name, Color? colour)
        {
            var cell = new VisualElement();
            cell.AddToClassList("stemrow__name");
            if (colour.HasValue)
            {
                var dot = new VisualElement();
                dot.AddToClassList("dot");
                dot.style.backgroundColor = colour.Value;
                cell.Add(dot);
            }
            if (name != null)
                cell.Add(new Label(name));
            return cell;
        }

        static VisualElement RowCaption(string text)
        {
            var cell = new VisualElement();
            cell.AddToClassList("stemrow__cell");
            var caption = new Label(text);
            caption.AddToClassList("caption");
            cell.Add(caption);
            return cell;
        }

        VisualElement Row(WavExportResult plan, string name, Color? colour, List<int> layers, string[] chokeLabels, bool forAll, bool chokeAndLoop)
        {
            var row = new VisualElement();
            row.AddToClassList("stemrow");
            if (forAll)
                row.AddToClassList("stemrow--all");
            if (name != null)
                row.Add(RowName(name, colour));
            SongSession song = session;

            if (chokeAndLoop)
            {
                var choke = new VisualElement();
                choke.AddToClassList("stemrow__cell");
                choke.Add(Controls.Segmented(chokeLabels, StateOf(layers, plan.Pads.ChokeOfLayer), chosen => song.SetChokeOfLayers(layers, chosen == 1)));
                row.Add(choke);

                var loop = new VisualElement();
                loop.AddToClassList("stemrow__cell");
                VisualElement loopSwitch = Controls.Segmented(OffOn, StateOf(layers, plan.Pads.LoopOfLayer), chosen => song.SetLoopOfLayers(layers, chosen == 1));
                // Greyed with the arrangement on, when nothing loops (WavExportResult.LoopOf).
                loopSwitch.SetEnabled(!plan.Arrangement);
                loop.Add(loopSwitch);
                row.Add(loop);
            }

            var velocity = new VisualElement();
            velocity.AddToClassList("stemrow__cell");
            velocity.Add(Controls.Segmented(OffOn, StateOf(layers, plan.Pads.VelocityOfLayer), chosen => song.SetVelocityOfLayers(layers, chosen == 1)));
            row.Add(velocity);
            return row;
        }

        // What the layers were last set to from a row: 0 off, 1 on, -1 when they differ. A pad set by
        // itself does not show here; pressing a row sets every pad it covers, such pads among them.
        static int StateOf(List<int> layers, Func<int, bool> setting)
        {
            int on = 0, off = 0;
            foreach (int layer in layers)
            {
                if (setting(layer))
                    on++;
                else
                    off++;
            }
            return on > 0 && off > 0 ? -1 : on > 0 ? 1 : 0;
        }

        void ShowChoices()
        {
            WavExportOptions options = session.Options;
            SongSession song = session;
            stretchHost.Clear();
            stretchHost.Add(Controls.Segmented(new[] { "Off", "M.TMP", "RESMPL" }, (int)options.Stretch, chosen => song.SetStretch((Sp16Stretch)chosen)));
            // The song is always in the arrangement. This is whether the unit opens with its arranger on.
            arrangementHost.Clear();
            arrangementHost.Add(Controls.Segmented(OffOn, options.Arrangement ? 1 : 0, chosen => song.SetArrangerOn(chosen == 1)));
            // Not added, the song plays at the project's tempo, which the unit can change.
            arrangementTempoHost.Clear();
            arrangementTempoHost.Add(Controls.Segmented(OffOn, options.TempoInArrangement ? 1 : 0, chosen => song.SetTempoInArrangement(chosen == 1)));
        }

        /// <summary>What used to come up in a box once a package was written, and is true before it is: what the package holds.</summary>
        void ShowPackage(WavExportResult plan)
        {
            showFolder.EnableInClassList(HiddenClass, writtenPath == null);
            packageWritten.text = writtenPath == null ? "" : "Written to " + writtenPath;
            // Empty, it would still keep a line's room from the notes.
            packageWritten.EnableInClassList(HiddenClass, writtenPath == null);
            if (plan == null)
            {
                packageFacts.text = "";
                ShowNotes(session.PlanError == null ? new string[0] : new[] { session.PlanError }, true);
                return;
            }
            int scenes = LastScene + 1;
            string facts = string.Format(Inv, "{0} patterns in {1} scene{2}  ·  {3:0.000} BPM  ·  {4:0.0} MB of samples",
                plan.Chunks.Count, scenes, scenes == 1 ? "" : "s", plan.Grid.Bpm, Sp16Project.Megabytes(plan.SampleBytes));
            if (plan.Layers.Count > 0)
            {
                // Layers are all on master or none of them is, so it is said once for them all.
                bool onMaster = plan.Layers[0].Output == Sp16Output.Master;
                var tracks = new List<string>();
                foreach (ExportLayer layer in plan.Layers)
                    tracks.Add(onMaster ? layer.Label : layer.Label + " on " + Sp16Project.OutputName(layer.Output));
                facts += "\nEach pattern plays " + string.Join(", ", tracks.ToArray()) + (onMaster ? ", from master output" : "");
            }
            packageFacts.text = facts;
            var notes = new List<string>(plan.Warnings);
            if (plan.TempoWarning != null)
                notes.Add(plan.TempoWarning + ".");
            ShowNotes(notes, false);
        }

        /// <summary>
        /// The notes under the package's facts, each starting on a line of its own and wrapping
        /// as it needs (the user, 2026-10-17: a run of "too loud" warnings as one block was hard
        /// to read and went off the foot of the screen).
        /// </summary>
        void ShowNotes(IList<string> notes, bool bad)
        {
            packageNotes.Clear();
            foreach (string note in notes)
            {
                var line = new Label(note) { userData = note };
                line.AddToClassList("export__note");
                line.EnableInClassList(BadClass, bad);
                packageNotes.Add(line);
            }
            // Fitted once the new lines are laid out: before then they have no font size to be
            // measured by, and the notes' room, when it comes out the size it was, says nothing.
            if (packageNotes.childCount > 0)
                packageNotes[0].RegisterCallback<GeometryChangedEvent>(FitNotesOnce);
        }

        void FitNotesOnce(GeometryChangedEvent laidOut)
        {
            ((VisualElement)laidOut.target).UnregisterCallback<GeometryChangedEvent>(FitNotesOnce);
            FitNotes();
        }

        /// <summary>
        /// Shows as much of the notes as fits above the panel's foot. Where the text would run past
        /// it, it is cut after the last word that fits and ends in an ellipsis, and the notes after
        /// it are not shown. The room the notes have is set by the panel and not by the notes, so
        /// cutting them never changes it.
        /// </summary>
        void FitNotes()
        {
            float room = packageNotes.contentRect.height, width = packageNotes.contentRect.width;
            int count = packageNotes.childCount;
            if (float.IsNaN(room) || float.IsNaN(width) || width <= 0 || count == 0)
                return;
            var tops = new float[count];
            float top = 0;
            int cutAt = -1;
            for (int i = 0; i < count; i++)
            {
                var line = (Label)packageNotes[i];
                line.text = (string)line.userData;
                line.style.display = DisplayStyle.Flex;
                tops[i] = top;
                if (cutAt >= 0)
                    continue;
                top += HeightOf(line, line.text, width);
                if (top > room + 0.5f)
                    cutAt = i;
            }
            if (cutAt < 0)
                return;
            // The note that runs past the foot, or the one before when not a word of it fits there.
            int last = cutAt;
            while (last >= 0 && !CutToFit((Label)packageNotes[last], room - tops[last], width))
                last--;
            for (int i = last + 1; i < count; i++)
                packageNotes[i].style.display = DisplayStyle.None;
        }

        static float HeightOf(Label line, string text, float width)
        {
            return line.MeasureTextSize(text, width, VisualElement.MeasureMode.Exactly, 0, VisualElement.MeasureMode.Undefined).y;
        }

        /// <summary>Cuts a note to as many of its words as fit in the height given, with an ellipsis after them; false when not one fits.</summary>
        static bool CutToFit(Label line, float height, float width)
        {
            string[] words = ((string)line.userData).Split(' ');
            int fits = 0, from = 1, to = words.Length;
            while (from <= to)
            {
                int middle = (from + to) / 2;
                if (HeightOf(line, Words(words, middle), width) <= height + 0.5f)
                {
                    fits = middle;
                    from = middle + 1;
                }
                else
                    to = middle - 1;
            }
            if (fits == 0)
                return false;
            line.text = Words(words, fits);
            return true;
        }

        static string Words(string[] words, int count)
        {
            return string.Join(" ", words, 0, count).TrimEnd('.', ',', ' ') + "…";
        }

        void Export()
        {
            if (uiState.State != UIStates.Export || session == null || session.Plan == null || jobs.Busy || Time.frameCount == openedFrame)
                return;
            pads.StopAll();
            string path = FileDialogs.SavePackage(session.ProjectName);
            if (path != null)
                ExportTo(path);
        }

        /// <summary>Writes the package for the current plan to <paramref name="path"/>.</summary>
        public void ExportTo(string path)
        {
            SongSession song = session;
            WavExportResult plan = song.Plan;
            if (plan == null)
                return;
            byte[] template = app.ProjectTemplate;
            var sources = new List<string>();
            foreach (StripFile file in app.Strips.Files)
                if (!sources.Contains(file.Path))
                    sources.Add(file.Path);
            jobs.Run("Writing " + Path.GetFileName(path),
                (cancel, progress) =>
                {
                    song.WritePackage(template, path, plan, progress, cancel);
                    return plan;
                },
                written =>
                {
                    jobs.SetIdleMessage("Wrote " + Path.GetFileName(written.PackagePath));
                    Debug.Log("Wrote " + written.PackagePath + " from " + string.Join(", ", sources.ToArray()));
                    if (session != song)
                        return;
                    writtenPath = written.PackagePath;
                    ShowPackage(song.Plan);
                },
                e =>
                {
                    jobs.SetIdleMessage("Export failed");
                    ShowNotes(new[] { "Package could not be written. " + e.Message }, true);
                });
        }
    }
}
