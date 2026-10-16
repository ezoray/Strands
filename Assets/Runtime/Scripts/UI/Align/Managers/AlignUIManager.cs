using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Strands.Applications.Core;
using Strands.Applications.Session;
using Strands.Audio;
using Strands.Export;
using Strands.Playback;
using Strands.Sp16;
using Strands.UI.Shared;
using Strands.UI.State;
using Strands.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace Strands.UI.Align
{
    /// <summary>
    /// The second screen, which the app calls Analysis: check the grid against the audio, correct
    /// it, and export. Every control changes the session, and the screen redraws from the session's new plan.
    /// </summary>
    public class AlignUIManager : MonoBehaviour
    {
        const string HiddenClass = "hidden";
        const double MinBpm = 40, MaxBpm = 300;
        const double TapTimeoutSeconds = 2.0;
        // A tap this close to the pace of the ones before it is averaged in; one further off starts again.
        const double TapTolerance = 0.12;
        const double OffBeatMs = 3.0;
        const string BadClass = "warnings--bad";
        // How many patterns the large waveform shows at a time. A song in one file has sixteen to a
        // scene, which is too many to see anything of; four are a scene of four stems.
        const int PatternsInView = 4;
        // What a song in one file is called where the stems of a song would be named.
        const string SongName = "Song";
        const string Keys = "Space play  ·  ← → pattern  ·  ↑ ↓ scene  ·  [ ] length  ·  L loop  ·  C click  ·  , . nudge  ·  T tap tempo  ·  F fit scene  ·  1–4 stems  ·  5 all";

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        [SerializeField] UIStateManager uiState;
        [SerializeField] ApplicationManager app;
        [SerializeField] JobManager jobs;
        [SerializeField] AuditionPlayer player;

        VisualElement controls, showHost, gridFromDot;
        WaveformView waveform;
        SongStripView strip;
        Label positionLabel, warningsLabel, nudgeLabel, tapLabel, tempoWarningLabel;
        Label gridFromCaption, gridFromLabel, patternsCount;
        Label leadInLabel, leadInLength, startAtLabel;
        Label selectedName, lengthCaption, selectedBars, selectedLength, selectedTime, selectedHits, selectedSamples;
        Button lengthShorter, lengthLonger;
        TextField tempoField;
        Button playButton, clickButton, loopButton, dragGridButton, exportButton, startEarlier, startLater, beatEarlier, beatLater;

        SongSession session;
        int selected;
        // The tempo the patterns were last laid out at, and the moment in the middle of the
        // selected pattern then, so that a new tempo can keep the selection on the same music.
        double laidOutBpm = double.NaN, selectedMiddleSeconds = double.NaN;
        // Set while a pattern is made shorter or longer, which keeps it selected wherever the playhead is.
        bool keepSelection;
        // Whether the last change was to the tempo, after which the view stays where it is.
        bool tempoChanged;
        // The strips that hold stems, in order: what Show offers. A strip is one button however
        // many files are on it, so the row is never longer than the four strips.
        readonly List<int> shownStrips = new List<int>();
        // Which of them are shown and played, counted along shownStrips.
        readonly List<bool> stripOn = new List<bool>();
        // The patterns in view, and whether the view is a whole scene, which Fit scene asks for.
        int shownFirst, shownLast;
        bool wholeScene;
        bool clickOn = true, loopOn, dragGridOn;
        // False after the user scrolls away during playback, so the view stays where they put it
        // until the playhead comes back into it or they ask to be taken somewhere.
        bool followPlayhead = true;
        double lastTap = double.NegativeInfinity;
        // Presses of T, at the time each was made, waiting for the next frame to be counted.
        readonly List<double> tapsPressed = new List<double>();
        double tapInterval;
        int tapIntervals;

        void Start()
        {
            VisualElement root = uiState.Root;
            controls = root.Q("alignControls");
            showHost = root.Q("showHost");
            gridFromDot = root.Q("gridFromDot");

            waveform = new WaveformView();
            root.Q("waveformHost").Add(waveform);
            waveform.Clicked += OnWaveformClicked;
            waveform.ViewMovedByUser += () => followPlayhead = false;
            waveform.GridDragged += seconds => session.Nudge(1000.0 * seconds);
            waveform.SceneStepRequested += StepScene;
            strip = new SongStripView();
            root.Q("stripHost").Add(strip);
            strip.Clicked += GoTo;
            waveform.ViewChanged += () => strip.SetView(waveform.ViewStartSeconds, waveform.ViewEndSeconds);

            positionLabel = root.Q<Label>("position");
            warningsLabel = root.Q<Label>("warnings");
            tempoWarningLabel = root.Q<Label>("tempoWarning");
            nudgeLabel = root.Q<Label>("nudge");
            tapLabel = root.Q<Label>("tapCount");
            gridFromCaption = root.Q<Label>("gridFromCaption");
            gridFromLabel = root.Q<Label>("gridFrom");
            patternsCount = root.Q<Label>("patternsCount");
            leadInLabel = root.Q<Label>("leadIn");
            leadInLength = root.Q<Label>("leadInLength");
            selectedName = root.Q<Label>("selectedName");
            selectedBars = root.Q<Label>("selectedBars");
            selectedLength = root.Q<Label>("selectedLength");
            lengthCaption = root.Q<Label>("lengthCaption");
            lengthShorter = root.Q<Button>("lengthShorter");
            lengthLonger = root.Q<Button>("lengthLonger");
            lengthShorter.clicked += () => StepLength(-1);
            lengthLonger.clicked += () => StepLength(1);
            selectedTime = root.Q<Label>("selectedTime");
            selectedHits = root.Q<Label>("selectedHits");
            selectedSamples = root.Q<Label>("selectedSamples");
            startAtLabel = root.Q<Label>("startAt");

            tempoField = root.Q<TextField>("tempo");
            tempoField.isDelayed = true;
            // Once a figure is typed the keyboard goes back to the shortcuts.
            tempoField.RegisterValueChangedCallback(evt =>
            {
                OnTempoTyped(evt.newValue);
                tempoField.Blur();
            });

            clickButton = root.Q<Button>("click");
            loopButton = root.Q<Button>("loop");
            dragGridButton = root.Q<Button>("dragGrid");
            exportButton = root.Q<Button>("export");
            startEarlier = root.Q<Button>("startEarlier");
            startLater = root.Q<Button>("startLater");
            beatEarlier = root.Q<Button>("beatEarlier");
            beatLater = root.Q<Button>("beatLater");
            playButton = root.Q<Button>("play");
            playButton.clicked += TogglePlay;
            clickButton.clicked += ToggleClick;
            loopButton.clicked += ToggleLoop;
            dragGridButton.clicked += () =>
            {
                dragGridOn = !dragGridOn;
                waveform.DragMovesGrid = dragGridOn;
                Controls.SetOn(dragGridButton, dragGridOn);
            };
            exportButton.clicked += GoToExport;
            root.Q<Button>("patternStartHere").clicked += StartPatternAtPlayhead;
            startEarlier.clicked += () => StepStart(-1);
            startLater.clicked += () => StepStart(1);
            beatEarlier.clicked += () => StepStartBeat(-1);
            beatLater.clicked += () => StepStartBeat(1);
            root.Q<Button>("reset").clicked += () =>
            {
                session.ResetToAnalysis();
                ForgetTaps();
            };

            root.Q<Button>("zoomOut").clicked += () => waveform.ZoomAtCentre(1 / 1.6);
            root.Q<Button>("zoomIn").clicked += () => waveform.ZoomAtCentre(1.6);
            root.Q<Button>("zoomFit").clicked += FitScene;

            root.Q<Button>("tempoHalf").clicked += () => SetTempo(session.Grid.Bpm / 2);
            root.Q<Button>("tempoDouble").clicked += () => SetTempo(session.Grid.Bpm * 2);
            root.Q<Button>("refit").clicked += Refit;
            root.Q<Button>("tap").clicked += () => Tap(InputState.currentTime);
            root.Q<Button>("barEarlier1").clicked += () => session.ShiftBeats(-1);
            root.Q<Button>("barEarlierHalf").clicked += () => session.ShiftBeats(-0.5);
            root.Q<Button>("barEarlierQuarter").clicked += () => session.ShiftBeats(-0.25);
            root.Q<Button>("barLater1").clicked += () => session.ShiftBeats(1);
            root.Q<Button>("barLaterHalf").clicked += () => session.ShiftBeats(0.5);
            root.Q<Button>("barLaterQuarter").clicked += () => session.ShiftBeats(0.25);
            root.Q<Button>("nudgeBack10").clicked += () => session.Nudge(-10);
            root.Q<Button>("nudgeBack1").clicked += () => session.Nudge(-1);
            root.Q<Button>("nudgeOn1").clicked += () => session.Nudge(1);
            root.Q<Button>("nudgeOn10").clicked += () => session.Nudge(10);


            clickOn = app.ClickOn;
            Controls.SetOn(clickButton, clickOn);
            uiState.StateChanged += OnStateChanged;
            uiState.StepRequested += OnStepRequested;
            app.ProjectNameChanged += OnNameChanged;
            InputSystem.onEvent += OnInputEvent;
        }

        void OnDestroy()
        {
            uiState.StateChanged -= OnStateChanged;
            uiState.StepRequested -= OnStepRequested;
            app.ProjectNameChanged -= OnNameChanged;
            InputSystem.onEvent -= OnInputEvent;
            Unbind();
        }

        void OnStateChanged(UIStates state)
        {
            // The Export page is the same song, laid out as it is here: the session stays, so that
            // coming back finds the view and the selection as they were left.
            if (state == UIStates.Align && session != app.Session)
                Bind(app.Session);
            else if (state == UIStates.Align)
                uiState.SetShortcuts(Keys);
            else if (state == UIStates.Export && player != null)
                player.Pause();
            else if (state == UIStates.Source)
                Unbind();
        }

        // The first step at the top goes back to the files. The grid stays with the session.
        void OnStepRequested(UIStates state)
        {
            if (uiState.State != UIStates.Align || jobs.Busy)
                return;
            if (state == UIStates.Source)
                uiState.Show(UIStates.Source);
            else if (state == UIStates.Export)
                GoToExport();
        }

        // The main button and the third step: on to the page where the package is set up and written.
        void GoToExport()
        {
            if (uiState.State == UIStates.Align && session != null && session.Plan != null && !jobs.Busy)
                uiState.Show(UIStates.Export);
        }

        void OnNameChanged()
        {
            if (session != null && session.ProjectName != app.ProjectName)
                session.SetProjectName(app.ProjectName);
        }

        void Bind(SongSession song)
        {
            Unbind();
            session = song;
            if (session.ProjectName != app.ProjectName && !string.IsNullOrEmpty(app.ProjectName))
                session.SetProjectName(app.ProjectName);
            session.Changed += Refresh;
            player.Load(session.Wav);
            player.ClickEnabled = clickOn;
            shownStrips.Clear();
            // On master too: the strips are still there, and each can be heard by itself.
            if (session.Stems != null)
                foreach (int stemStrip in session.StemStrips)
                    if (!shownStrips.Contains(stemStrip))
                        shownStrips.Add(stemStrip);
            shownStrips.Sort();
            // Start on the whole song, which is what Play is expected to give.
            stripOn.Clear();
            foreach (int shownStrip in shownStrips)
                stripOn.Add(true);
            waveform.SetSong(PictureOfSong(), session.Wav.SampleRate);
            // The strip is always the whole song, whatever the large waveform is showing.
            strip.SetSong(PictureOfSong(), session.Wav.SampleRate);
            selected = 0;
            laidOutBpm = selectedMiddleSeconds = double.NaN;
            wholeScene = false;
            ForgetTaps();
            uiState.SetShortcuts(Keys);

            ShowStrips();
            Refresh();
            if (session.Plan != null)
            {
                ShowAround(selected);
                // Play starts where the package does: on the first pattern, and at the beat line
                // its silence starts on when it is a lead-in, not at the start of the file.
                player.PositionSeconds = (double)session.Plan.Chunks[0].PatternStartFrame / session.Plan.SampleRate;
            }
        }

        void Unbind()
        {
            if (session == null)
                return;
            session.Changed -= Refresh;
            session = null;
            uiState.SetExportAvailable(false);
            if (player != null)
                player.Unload();
        }

        void Update()
        {
            // Presses of T are counted this frame or not at all: a press on another screen, or
            // while typing, is not a tap.
            List<double> taps = tapsPressed.Count == 0 ? null : new List<double>(tapsPressed);
            tapsPressed.Clear();
            if (session == null)
                return;
            bool idle = !jobs.Busy;
            controls.SetEnabled(idle);
            if (uiState.State != UIStates.Align)
                return;
            exportButton.SetEnabled(idle && session.Plan != null);
            if (idle)
                HandleKeys(taps);
            if (session == null)
                return;

            double position = player.PositionSeconds;
            bool playing = player.IsPlaying;
            playButton.text = playing ? "Pause" : "Play";
            positionLabel.text = DescribePosition(position);
            waveform.PlayheadSeconds = position;
            strip.PlayheadSeconds = position;

            if (!playing)
                return;
            int playingChunk = ChunkAt(position);
            // Turn the page when the playhead runs off the right of the view, unless the user has
            // scrolled off to look at something else. A view that is on its patterns goes to the next ones.
            bool inView = position <= waveform.ViewEndSeconds && position >= waveform.ViewStartSeconds;
            if (inView)
                followPlayhead = true;
            else if (followPlayhead && waveform.IsFitted && playingChunk >= 0)
                ShowAround(playingChunk);
            else if (followPlayhead)
                waveform.CentreOn(position + 0.4 * (waveform.ViewEndSeconds - waveform.ViewStartSeconds));
            if (playingChunk >= 0 && playingChunk != selected && !loopOn)
                Select(playingChunk, false);
        }

        void HandleKeys(List<double> taps)
        {
            Keyboard keys = Keyboard.current;
            if (keys == null || Controls.IsTyping(uiState.Root))
                return;
            bool command = keys.leftCommandKey.isPressed || keys.rightCommandKey.isPressed || keys.leftCtrlKey.isPressed || keys.rightCtrlKey.isPressed;
            if (command)
            {
                if (keys.eKey.wasPressedThisFrame)
                    GoToExport();
                return;
            }
            bool shift = keys.shiftKey.isPressed;
            // 1 to 4 switch the strips on and off, with Shift one by itself, and 5 is all of them.
            Key[] digits = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4 };
            for (int i = 0; i < digits.Length; i++)
                if (keys[digits[i]].wasPressedThisFrame)
                {
                    if (shift)
                        SoloStrip(i);
                    else
                        ToggleStrip(i);
                }
            if (keys.digit5Key.wasPressedThisFrame)
                AllStripsOn();
            if (keys.spaceKey.wasPressedThisFrame)
                TogglePlay();
            if (keys.leftArrowKey.wasPressedThisFrame)
                Select(selected - 1, true);
            if (keys.rightArrowKey.wasPressedThisFrame)
                Select(selected + 1, true);
            if (keys.upArrowKey.wasPressedThisFrame)
                StepScene(-1);
            if (keys.downArrowKey.wasPressedThisFrame)
                StepScene(1);
            if (keys.leftBracketKey.wasPressedThisFrame)
                StepLength(-1);
            if (keys.rightBracketKey.wasPressedThisFrame)
                StepLength(1);
            if (keys.lKey.wasPressedThisFrame)
                ToggleLoop();
            if (keys.cKey.wasPressedThisFrame)
                ToggleClick();
            if (keys.commaKey.wasPressedThisFrame)
                session.Nudge(shift ? -10 : -1);
            if (keys.periodKey.wasPressedThisFrame)
                session.Nudge(shift ? 10 : 1);
            if (taps != null)
                Tap(taps);
            if (keys.fKey.wasPressedThisFrame)
                FitScene();
            if (keys.minusKey.wasPressedThisFrame)
                waveform.ZoomAtCentre(1 / 1.6);
            if (keys.equalsKey.wasPressedThisFrame)
                waveform.ZoomAtCentre(1.6);
        }

        /// <summary>Redraws everything from the session. Called after every change to it.</summary>
        void Refresh()
        {
            BeatGrid grid = session.Grid;
            WavExportResult plan = session.Plan;
            player.SetGrid(grid);
            if (plan != null)
                selected = Mathf.Clamp(FollowSelection(grid), 0, plan.Chunks.Count - 1);
            waveform.SetLayout(grid, plan, session.PatternOffsetsMs, OffBeatMs);
            // The silence a lead-in starts with can be played, from the beat line its pattern starts on.
            player.EarliestSeconds = waveform.StartSeconds;
            waveform.FlagSeconds = session.FlagSeconds;
            waveform.Selected = plan == null ? -1 : selected;
            strip.SetLayout(plan);
            strip.Selected = plan == null ? -1 : selected;
            // The grid, the tempo or the start moved the patterns, and the loop with them; the
            // playhead stays where it is in the music (the user, 2026-10-14).
            ApplyLoop(false);

            tempoField.SetValueWithoutNotify(grid.Bpm.ToString("0.000", Inv));
            nudgeLabel.text = session.NudgeMs.ToString("+0.#;-0.#;0", Inv) + " ms";
            ShowGridSource();
            ShowStart();
            ShowLeadIn(plan);
            ShowMemory(plan);
            uiState.SetExportAvailable(plan != null);
            // Beside the tempo, whether or not the song can be laid out at it.
            tempoWarningLabel.text = session.Grid != null ? WavToSp16.TempoWarningFor(session.Grid.Bpm) ?? "" : "";

            if (plan == null)
            {
                patternsCount.text = "";
                ShowSelected(null);
                ShowWarnings(session.PlanError, true);
                waveform.SetSceneSteps(null, null);
                jobs.SetIdleMessage("These settings cannot be exported");
                return;
            }

            int scenes = plan.Chunks[plan.Chunks.Count - 1].Scene + 1;
            int steps = 0;
            foreach (ChunkInfo chunk in plan.Chunks)
                steps += chunk.PatternLength;
            int toAScene = Sp16Project.PadsPerScene / Math.Max(1, plan.Layers.Count);
            patternsCount.text = string.Format(Inv, "{0} in {1} of {2} scenes  ·  {3} of {4} bars", plan.Chunks.Count, scenes, Sp16Project.Scenes,
                (steps + Sp16Project.StepsPerBar - 1) / Sp16Project.StepsPerBar, Sp16Project.Scenes * toAScene * MostBarsInAPattern);
            ShowWarnings(string.Join("  ", plan.Warnings), false);
            ShowSelection();
            // The view stays on the same music while the lines move over it: fitting it again to the
            // patterns at every move of a bar line or change of length made it jump about (the user,
            // 2026-10-16). Only a selection that has left the view altogether brings it back to its
            // patterns, and not at a new tempo, after which the view stays where it is (2026-10-14).
            if (waveform.IsFitted && !tempoChanged && !SelectionInView(plan))
                ShowAround(selected);
            jobs.SetIdleMessage(string.Format(Inv, "Ready  ·  {0:0.000} BPM", grid.Bpm));
        }

        /// <summary>Which stem the grid was found from, with that stem's colour.</summary>
        void ShowGridSource()
        {
            bool stems = session.Stems != null;
            int from = stems && session.GridFrom != null ? session.Stems.StemLabels.IndexOf(session.GridFrom) : -1;
            gridFromCaption.EnableInClassList(HiddenClass, !stems);
            gridFromLabel.EnableInClassList(HiddenClass, !stems);
            gridFromDot.EnableInClassList(HiddenClass, from < 0);
            // Named by the file it came from, cut short as a strip's name is.
            if (stems)
                gridFromLabel.text = from < 0 ? "all stems" : StripLayout.NameOf(session.StemStrips[from], session.GridFrom, 1);
            if (from >= 0)
                gridFromDot.style.backgroundColor = Theme.Strip(session.StemStrips[from]);
        }

        /// <summary>
        /// Says where the song starts in the terms a listener counts in: the bar and the beat of
        /// it, and when what starts there is a lead-in, how long it runs before the pattern after
        /// it. It is the hard part of lining a song up, and a count of "4, then 1" is how it is
        /// checked by ear. The arrows either side start the song a beat later or earlier, which
        /// drops beats from the front of the lead-in, or of the song, and puts them back.
        /// </summary>
        void ShowLeadIn(WavExportResult plan)
        {
            string note = null, where = "–", length = "";
            if (plan != null)
            {
                ChunkInfo first = plan.Chunks[0];
                // A lead-in's pattern ends on the bar line of the pattern after it, so it starts
                // its own length before that: on a step, which is where the unit starts it.
                int step = first.IsLeadIn ? Sp16Project.StepsPerBar * plan.Chunks[1].StartBar - first.PatternLength : Sp16Project.StepsPerBar * first.StartBar;
                int inBar = ((step % Sp16Project.StepsPerBar) + Sp16Project.StepsPerBar) % Sp16Project.StepsPerBar;
                string beat = (inBar / 4 + 1).ToString(Inv) + Quarters[inBar % 4];
                where = (step >= 0 ? "Bar " + (step / Sp16Project.StepsPerBar + 1).ToString(Inv) : "Before bar 1") + ", beat " + beat;
                length = first.IsLeadIn ? Steps(first.PatternLength) + " long" : "no lead-in";
                if (first.IsLeadIn)
                    note = "starts on beat " + beat;
            }
            leadInLabel.text = where;
            leadInLength.text = length;
            beatEarlier.SetEnabled(plan != null && session.CanStepStartBeat(-1));
            beatLater.SetEnabled(plan != null && session.CanStepStartBeat(1));
            waveform.LeadInNote = note;
        }

        // The parts of a beat a step can fall on, as they are written after the beat's number.
        /// <summary>A pattern on the unit is 64 steps at most, which is four bars.</summary>
        const int MostBarsInAPattern = 4;

        /// <summary>
        /// What the selected pattern is, at the right of the Patterns panel: its name as the lane
        /// has it, its bars, how long it is, where it lies in the file, how far its hits are from
        /// the lines, and what its samples cost.
        /// </summary>
        void ShowSelected(WavExportResult plan)
        {
            if (plan == null)
            {
                selectedName.text = "–";
                selectedBars.text = selectedLength.text = selectedTime.text = selectedHits.text = selectedSamples.text = "";
                ShowLengthSteps(false);
                return;
            }
            ChunkInfo chunk = plan.Chunks[selected];
            string pattern = chunk.IsLeadIn ? "Lead-in" : "Pattern " + (chunk.Pattern + 1).ToString(Inv);
            selectedName.text = "Scene " + (chunk.Scene + 1).ToString(Inv) + " " + pattern;

            int steps = Sp16Project.StepsPerBar;
            if (chunk.IsLeadIn)
                selectedBars.text = "before bar " + (plan.Chunks[1].StartBar + 1).ToString(Inv);
            else
            {
                int last = chunk.StartBar + (chunk.PatternLength + steps - 1) / steps;
                selectedBars.text = last == chunk.StartBar + 1 ? (chunk.StartBar + 1).ToString(Inv)
                    : (chunk.StartBar + 1).ToString(Inv) + " to " + last.ToString(Inv);
            }
            selectedLength.text = Steps(chunk.PatternLength);
            ShowLengthSteps(!chunk.IsLeadIn);
            selectedTime.text = Clock((double)chunk.PatternStartFrame / plan.SampleRate) + " to " + Clock((double)chunk.EndFrame / plan.SampleRate);

            double[] offsets = session.PatternOffsetsMs;
            double off = offsets != null && selected < offsets.Length ? offsets[selected] : double.NaN;
            selectedHits.text = double.IsNaN(off) ? "not measured"
                : Math.Abs(off) < OffBeatMs ? "on the lines"
                : Math.Abs(off).ToString("0", Inv) + (off > 0 ? " ms late" : " ms early");

            int layers = Math.Max(1, plan.Layers.Count);
            long bytes = 0;
            for (int layer = 0; layer < layers; layer++)
                if (chunk.FileNames == null || chunk.FileNames[layer] != null)
                    bytes += (long)chunk.SampleFrames * (plan.Layers.Count > 0 && plan.Layers[layer].IsMono ? Sp16Project.MonoBytesPerFrame : Sp16Project.StereoBytesPerFrame);
            string count = chunk.SampleCount == layers ? layers.ToString(Inv) : chunk.SampleCount.ToString(Inv) + " of " + layers.ToString(Inv);
            selectedSamples.text = count + "  ·  " + Sp16Project.Megabytes(bytes).ToString("0.0", Inv) + " MB";
        }

        /// <summary>A moment in the file as minutes and seconds; before the file's start it is negative.</summary>
        static string Clock(double seconds)
        {
            double size = Math.Abs(seconds);
            int minutes = (int)(size / 60);
            return (seconds < -0.005 ? "-" : "") + minutes.ToString(Inv) + ":" + (size - 60 * minutes).ToString("00.00", Inv);
        }

        static readonly string[] Quarters = { "", "¼", "½", "¾" };

        /// <summary>A length in steps, sixteen to the bar, as bars and beats: 54 is 3 bars and 1½ beats.</summary>
        static string Steps(int steps)
        {
            string[] quarters = Quarters;
            int bars = steps / Sp16Project.StepsPerBar, left = steps % Sp16Project.StepsPerBar, beats = left / 4;
            string text = bars == 0 ? "" : bars.ToString(Inv) + (bars == 1 ? " bar" : " bars");
            if (left == 0)
                return text;
            string count = (beats == 0 ? "" : beats.ToString(Inv)) + quarters[left % 4];
            return text + (bars == 0 ? "" : " and ") + count + (left <= 4 ? " beat" : " beats");
        }

        /// <summary>
        /// Which pattern the song starts at, by the name it has on the lane with nothing left out:
        /// the lead-in, or a later pattern, which is how an unwanted beginning is taken off a
        /// pattern at a time. Only patterns are named here: a start moved by beats is still in
        /// its pattern, and the place it has reached is beside Adjust lead-in start.
        /// </summary>
        void ShowStart()
        {
            int pattern = session.StartPattern;
            int toAScene = Sp16Project.PadsPerScene / Math.Max(1, session.Stems == null ? 1 : session.Stems.Layers.Count);
            string name = "Pattern " + (pattern % toAScene + 1).ToString(Inv);
            startAtLabel.text = pattern == 0 && session.StartsWithLeadIn ? "Lead-in"
                : pattern < toAScene ? name
                : "Scene " + (pattern / toAScene + 1).ToString(Inv) + " " + name;
            startEarlier.SetEnabled(session.StartChoiceAfter(-1) >= 0);
            startLater.SetEnabled(session.StartChoiceAfter(1) >= 0);
        }

        /// <summary>
        /// The arrows either side of the selected pattern's length. The lead-in has none: its
        /// length is where the song starts, which Adjust lead-in start sets. When neither arrow
        /// can be pressed, as on the last pattern, the caption and the length are greyed with them.
        /// </summary>
        void ShowLengthSteps(bool shown)
        {
            lengthShorter.EnableInClassList(HiddenClass, !shown);
            lengthLonger.EnableInClassList(HiddenClass, !shown);
            selectedLength.EnableInClassList("value--free", !shown);
            bool shorter = shown && session.CanStepPatternLength(selected, -1), longer = shown && session.CanStepPatternLength(selected, 1);
            lengthShorter.SetEnabled(shorter);
            lengthLonger.SetEnabled(longer);
            // The last pattern runs to the end of the song: its length is shown greyed, as its arrows are.
            bool fixedLength = shown && !shorter && !longer;
            lengthCaption.EnableInClassList("length--fixed", fixedLength);
            selectedLength.EnableInClassList("length--fixed", fixedLength);
        }

        /// <summary>
        /// Makes the selected pattern a bar shorter or longer; every pattern after it starts a bar
        /// earlier or later, and the grid does not move. For songs whose phrases are not all a
        /// whole number of patterns long. The selection stays on the pattern, and the playhead where it is.
        /// </summary>
        void StepLength(int direction)
        {
            if (session == null || session.Plan == null || !session.CanStepPatternLength(selected, direction))
                return;
            keepSelection = true;
            session.StepPatternLength(selected, direction);
            keepSelection = false;
        }

        /// <summary>Starts the song a pattern later or earlier, and goes to where it now starts.</summary>
        void StepStart(int direction)
        {
            int choice = session.StartChoiceAfter(direction);
            if (choice < 0)
                return;
            session.SetStartChoice(choice);
            ShowStartOfSong();
        }

        /// <summary>
        /// Starts the song a beat later or earlier. The view is left as it is, zoomed in or not, so
        /// that the start is seen to move a beat along; going to the start of the song, as a step
        /// of a whole pattern does, zoomed a close view out at every press. The playhead stays
        /// where it is unless it is now in what is left out.
        /// </summary>
        void StepStartBeat(int direction)
        {
            if (!session.CanStepStartBeat(direction))
                return;
            session.StepStartBeat(direction);
            WavExportResult plan = session.Plan;
            if (plan == null)
                return;
            Select(0, false, false);
            KeepPlayheadInSong();
        }

        // A playhead in what is now left out goes to where the song starts; anywhere else it stays
        // where it is in the music (the user, 2026-10-14).
        void KeepPlayheadInSong()
        {
            WavExportResult plan = session.Plan;
            if (plan == null)
                return;
            double start = (double)plan.Chunks[0].PatternStartFrame / plan.SampleRate;
            if (player.PositionSeconds < start)
                player.PositionSeconds = start;
        }

        void ShowStartOfSong()
        {
            if (session.Plan == null)
                return;
            wholeScene = false;
            Select(0, true, false);
            ShowAround(0);
            KeepPlayheadInSong();
        }

        /// <summary>The memory the samples take, exactly, with each output's share in its colour.</summary>
        void ShowMemory(WavExportResult plan)
        {
            var shares = new List<KeyValuePair<Color, long>>();
            if (plan == null)
            {
                // Nothing can be counted without a plan, so the Source screen's estimate stands in.
                foreach (OutputStrip output in app.Strips.Strips)
                    shares.Add(new KeyValuePair<Color, long>(app.Strips.IsFullSong ? Theme.WaveAll : Theme.Strip(output.Pair), app.Strips.EstimatedBytesOn(output)));
            }
            else if (plan.Layers.Count == 0)
            {
                shares.Add(new KeyValuePair<Color, long>(Theme.WaveAll, plan.SampleBytes));
            }
            else
            {
                for (int layer = 0; layer < plan.Layers.Count; layer++)
                    shares.Add(new KeyValuePair<Color, long>(Theme.Strip(plan.Layers[layer].Group), plan.BytesOfLayer(layer)));
            }
            uiState.ShowMemory(shares);
        }

        /// <summary>
        /// What there is to say about the song as it is laid out, on the one line kept for it:
        /// notes, or in red the reason it cannot be exported.
        /// </summary>
        void ShowWarnings(string text, bool bad)
        {
            warningsLabel.text = text ?? "";
            warningsLabel.EnableInClassList(BadClass, bad);
        }

        void ShowSelection()
        {
            WavExportResult plan = session.Plan;
            if (plan == null || selected < 0 || selected >= plan.Chunks.Count)
                return;
            int scene = plan.Chunks[selected].Scene, last = plan.Chunks[plan.Chunks.Count - 1].Scene;
            ShowSelected(plan);
            waveform.SetSceneSteps(scene > 0 ? "‹ Scene " + scene.ToString(Inv) : null, scene < last ? "Scene " + (scene + 2).ToString(Inv) + " ›" : null);
        }

        /// <summary>
        /// The strips that are shown in the large waveform and played, each switched on and off by
        /// itself: a button for each strip that holds stems, called what its strip is (Drums,
        /// Bass, Other, Vocals) whatever its files are called and however many there are. It
        /// took the place of a choice of all or one, which could not give two together. A song
        /// in one file has nothing to switch, and the row names it.
        /// </summary>
        void BuildShow()
        {
            showHost.Clear();
            if (shownStrips.Count == 0)
            {
                showHost.Add(Controls.Segmented(new[] { SongName }, 0, chosen => { }));
                return;
            }
            var labels = new List<string>();
            var dots = new List<Color?>();
            foreach (int shownStrip in shownStrips)
            {
                labels.Add(session.NameOfStrip(shownStrip));
                dots.Add(Theme.Strip(shownStrip));
            }
            showHost.Add(Controls.Toggles(labels, stripOn, ToggleStrip, dots));
        }

        /// <summary>Switches one of the strips that hold stems on or off, counted along them.</summary>
        void ToggleStrip(int choice)
        {
            if (choice < 0 || choice >= shownStrips.Count)
                return;
            stripOn[choice] = !stripOn[choice];
            ShowStrips();
        }

        /// <summary>One strip by itself, or back to every strip when it is by itself already.</summary>
        void SoloStrip(int choice)
        {
            if (choice < 0 || choice >= shownStrips.Count)
                return;
            bool alone = stripOn[choice];
            for (int i = 0; i < stripOn.Count; i++)
                alone &= i == choice || !stripOn[i];
            for (int i = 0; i < stripOn.Count; i++)
                stripOn[i] = alone || i == choice;
            ShowStrips();
        }

        void AllStripsOn()
        {
            for (int i = 0; i < stripOn.Count; i++)
                stripOn[i] = true;
            ShowStrips();
        }

        /// <summary>Draws and plays the strips that are on. What is shown is what is heard.</summary>
        void ShowStrips()
        {
            waveform.SetWave(PictureOfShown());
            if (session.Stems != null)
            {
                var on = new List<int>();
                for (int i = 0; i < shownStrips.Count; i++)
                    if (stripOn[i])
                        on.Add(shownStrips[i]);
                player.Switch(session.SamplesOfStrips(on));
            }
            BuildShow();
        }

        /// <summary>
        /// The whole song: every stem in the colour of its strip, laid over each other, or one
        /// file coloured by its low, middle and high sound.
        /// </summary>
        WavePicture PictureOfSong()
        {
            if (session.Stems == null)
                return WavePicture.OfBands(session.Bands);
            var layers = new List<IPeaks>();
            var colours = new List<Color>();
            for (int i = 0; i < session.Stems.StemLabels.Count; i++)
            {
                layers.Add(session.PeaksOfStem(i));
                colours.Add(Theme.Strip(session.StemStrips[i]));
            }
            return new WavePicture(layers, colours);
        }

        /// <summary>
        /// The stems of the strips that are on, each in its strip's colour. With every strip off
        /// the song is still drawn, in the colour of what is not played, so that the grid has
        /// something to be seen against.
        /// </summary>
        WavePicture PictureOfShown()
        {
            if (session.Stems == null)
                return PictureOfSong();
            var layers = new List<IPeaks>();
            var colours = new List<Color>();
            for (int i = 0; i < session.Stems.StemLabels.Count; i++)
            {
                int at = shownStrips.IndexOf(session.StemStrips[i]);
                if (at < 0 || !stripOn[at])
                    continue;
                layers.Add(session.PeaksOfStem(i));
                colours.Add(Theme.Strip(session.StemStrips[i]));
            }
            // At the size of the whole song, whichever stems are on.
            float gain = PictureOfSong().Gain;
            if (layers.Count > 0)
                return new WavePicture(layers, colours, gain);
            for (int i = 0; i < session.Stems.StemLabels.Count; i++)
            {
                layers.Add(session.PeaksOfStem(i));
                colours.Add(Theme.WaveOutside);
            }
            return new WavePicture(layers, colours, gain);
        }

        string DescribePosition(double seconds)
        {
            BeatGrid grid = session.Grid;
            // Before the file starts, in the silence a lead-in begins with, the time counts up to nought.
            var time = TimeSpan.FromSeconds(Math.Abs(seconds));
            string clock = string.Format(Inv, "{0}{1}:{2:00.00}", seconds < -0.004 ? "-" : "", (int)time.TotalMinutes, Math.Abs(seconds) - 60 * (int)time.TotalMinutes);
            double beats = (seconds - grid.FirstDownbeatSeconds) / grid.BeatSeconds;
            if (beats < 0)
                return "lead-in  ·  " + clock;
            int beat = (int)Math.Floor(beats + 1e-6);
            return string.Format(Inv, "bar {0}.{1}  ·  {2}", beat / 4 + 1, beat % 4 + 1, clock);
        }

        int ChunkAt(double seconds)
        {
            WavExportResult plan = session.Plan;
            if (plan == null)
                return -1;
            // Rounded the way the chunk boundaries are, so a time exactly on a boundary belongs to
            // the chunk that starts there and not, by a fraction of a frame, to the one before.
            int frame = (int)Math.Round(seconds * plan.SampleRate);
            for (int i = 0; i < plan.Chunks.Count; i++)
                if (frame >= plan.Chunks[i].PatternStartFrame && frame < plan.Chunks[i].EndFrame)
                    return i;
            return -1;
        }

        /// <summary>
        /// Selects a pattern. With movePlayhead, which is how a click on it or a key to it selects
        /// it, the playhead goes to its start, or into it when it loops; without, it stays.
        /// </summary>
        void Select(int index, bool bringIntoView, bool movePlayhead = true)
        {
            WavExportResult plan = session.Plan;
            if (plan == null)
                return;
            selected = Mathf.Clamp(index, 0, plan.Chunks.Count - 1);
            waveform.Selected = selected;
            strip.Selected = selected;
            ShowSelection();
            ApplyLoop(movePlayhead);
            if (!bringIntoView)
                return;
            followPlayhead = true;
            ChunkInfo chunk = plan.Chunks[selected];
            double start = (double)chunk.StartFrame / plan.SampleRate, end = (double)chunk.EndFrame / plan.SampleRate;
            // A view that is on its patterns goes to the ones this pattern is among; one the user has zoomed keeps its zoom.
            if (!waveform.IsFitted)
                waveform.ShowRange((double)chunk.PatternStartFrame / plan.SampleRate, end);
            else if (selected < shownFirst || selected > shownLast)
                ShowAround(selected);
            // From where the pattern starts, which for a lead-in is the beat line in front of its sound.
            if (!loopOn && movePlayhead)
                player.PositionSeconds = (double)chunk.PatternStartFrame / plan.SampleRate;
        }

        /// <summary>
        /// Puts a pattern and its neighbours in the large waveform: the four it is among, counted
        /// off from the start of its scene, or the whole of its scene after Fit scene. With four
        /// stems the two are the same. A song in one file has sixteen patterns to a scene, each too
        /// small to work on when all sixteen are in view.
        /// </summary>
        void ShowAround(int chunk)
        {
            WavExportResult plan = session.Plan;
            if (plan == null)
                return;
            chunk = Mathf.Clamp(chunk, 0, plan.Chunks.Count - 1);
            int scene = plan.Chunks[chunk].Scene, first = chunk, last = chunk;
            while (first > 0 && plan.Chunks[first - 1].Scene == scene)
                first--;
            while (last + 1 < plan.Chunks.Count && plan.Chunks[last + 1].Scene == scene)
                last++;
            if (!wholeScene)
            {
                first += (chunk - first) / PatternsInView * PatternsInView;
                last = Math.Min(last, first + PatternsInView - 1);
            }
            shownFirst = first;
            shownLast = last;
            // From where the first of them starts as a pattern, which for a lead-in is the beat line in front of its sound.
            waveform.Fit((double)plan.Chunks[first].PatternStartFrame / plan.SampleRate, (double)plan.Chunks[last].EndFrame / plan.SampleRate);
        }

        /// <summary>Whether any of the selected pattern is in the large waveform.</summary>
        bool SelectionInView(WavExportResult plan)
        {
            ChunkInfo chunk = plan.Chunks[selected];
            double start = (double)chunk.PatternStartFrame / plan.SampleRate, end = (double)chunk.EndFrame / plan.SampleRate;
            return end > waveform.ViewStartSeconds && start < waveform.ViewEndSeconds;
        }

        void FitScene()
        {
            if (session.Plan == null)
                return;
            wholeScene = true;
            ShowAround(selected);
        }

        void StepScene(int direction)
        {
            WavExportResult plan = session.Plan;
            if (plan == null)
                return;
            int scene = plan.Chunks[selected].Scene;
            // Going back from the middle of a scene lands on its first pattern, like a track-skip button.
            int target = direction < 0 && plan.Chunks[selected].Pattern > 0 ? scene : scene + direction;
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                if (plan.Chunks[i].Scene == target)
                {
                    Select(i, true);
                    ShowAround(i);
                    return;
                }
            }
        }

        /// <summary>A click or a drag on the strip of the whole song: go to that place, and show the patterns round it close up.</summary>
        void GoTo(double seconds)
        {
            WavExportResult plan = session.Plan;
            if (plan == null)
                return;
            followPlayhead = true;
            int chunk = ChunkAt(seconds);
            if (chunk < 0)
                chunk = seconds * plan.SampleRate < plan.Chunks[0].PatternStartFrame ? 0 : plan.Chunks.Count - 1;
            Select(chunk, false);
            wholeScene = false;
            ShowAround(chunk);
            player.PositionSeconds = StartOfBeat(seconds);
        }

        /// <summary>
        /// Which pattern is selected once the patterns are laid out again. The playhead stays where
        /// it is in the music, so when the patterns move from under it (a bar line moved, a new
        /// tempo, a nudge) the selection, and the loop with it, goes to the pattern it is now in,
        /// however little it has moved past the pattern's edge (the user, 2026-10-14). A new tempo with the playhead in no
        /// pattern goes to the one that holds the middle of what was selected. A pattern made
        /// shorter or longer stays selected, so that [ and ] go on working on it.
        /// </summary>
        int FollowSelection(BeatGrid grid)
        {
            int follow = selected;
            bool newTempo = !double.IsNaN(laidOutBpm) && grid.Bpm != laidOutBpm;
            tempoChanged = newTempo;
            laidOutBpm = grid.Bpm;
            if (keepSelection)
            {
                keepSelection = false;
                return follow;
            }
            int atPlayhead = ChunkAt(player.PositionSeconds);
            if (atPlayhead >= 0)
                return atPlayhead;
            if (newTempo && atPlayhead < 0 && !double.IsNaN(selectedMiddleSeconds))
            {
                int chunk = ChunkAt(selectedMiddleSeconds);
                if (chunk >= 0)
                    follow = chunk;
            }
            return follow;
        }

        /// <summary>Loops the selected pattern when Loop pattern is on. With jumpIn the playhead goes into it.</summary>
        void ApplyLoop(bool jumpIn = true)
        {
            WavExportResult plan = session.Plan;
            if (plan != null && selected >= 0 && selected < plan.Chunks.Count)
                selectedMiddleSeconds = 0.5 * (plan.Chunks[selected].PatternStartFrame + plan.Chunks[selected].EndFrame) / plan.SampleRate;
            if (loopOn && plan != null && selected >= 0 && selected < plan.Chunks.Count)
            {
                ChunkInfo chunk = plan.Chunks[selected];
                player.SetLoop((double)chunk.PatternStartFrame / plan.SampleRate, (double)chunk.EndFrame / plan.SampleRate, jumpIn);
            }
            else
            {
                player.ClearLoop();
            }
        }

        // The beginning of the beat a time falls in, so that playback from there begins in time.
        double StartOfBeat(double seconds)
        {
            BeatGrid grid = session.Grid;
            double beat = Math.Floor((seconds - grid.FirstDownbeatSeconds) / grid.BeatSeconds + 1e-9);
            return Math.Max(waveform.StartSeconds, Math.Min(session.Wav.Seconds, grid.FirstDownbeatSeconds + beat * grid.BeatSeconds));
        }

        void OnWaveformClicked(double seconds, bool exact)
        {
            // While it plays, a click carries on from the beginning of the beat that was clicked in,
            // so that it stays in time. Stopped, or with Shift, the playhead goes exactly where it
            // was put, which is what starting a pattern at the playhead needs.
            if (!exact && player.IsPlaying)
                seconds = StartOfBeat(seconds);
            followPlayhead = true;
            int chunk = ChunkAt(seconds);
            if (chunk >= 0)
                Select(chunk, false);
            // With the loop on, Select has just moved the loop; a click outside it is put back inside by the player.
            player.PositionSeconds = seconds;
        }

        /// <summary>
        /// Starts a pattern at the playhead. When the playhead is not on a line the lines move to it
        /// and settle on to the hits, so the playhead is then put on the line, where the pattern starts.
        /// </summary>
        void StartPatternAtPlayhead()
        {
            session.SetPatternStartAt(player.PositionSeconds);
            double start = session.Options.PatternAnchorSeconds.Value;
            followPlayhead = true;
            int chunk = ChunkAt(start);
            if (chunk >= 0)
                Select(chunk, false);
            player.PositionSeconds = start;
        }

        void TogglePlay()
        {
            if (player.IsPlaying)
            {
                player.Pause();
                return;
            }
            followPlayhead = true;
            player.Play();
        }

        void ToggleClick()
        {
            clickOn = !clickOn;
            app.ClickOn = clickOn;
            player.ClickEnabled = clickOn;
            Controls.SetOn(clickButton, clickOn);
        }

        void ToggleLoop()
        {
            loopOn = !loopOn;
            Controls.SetOn(loopButton, loopOn);
            ApplyLoop();
        }

        void OnTempoTyped(string text)
        {
            double bpm;
            if (TryParse(text, out bpm))
                SetTempo(bpm);
            else
                Refresh();
        }

        void SetTempo(double bpm)
        {
            if (bpm < MinBpm || bpm > MaxBpm)
            {
                Refresh();
                return;
            }
            session.SetTempo(bpm);
        }

        static bool TryParse(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, Inv, out value);
        }

        /// <summary>
        /// Every press of T, with the time the keyboard gave it. Read once a frame, two presses
        /// that fell in one frame were counted as one, so fast tapping read as half the tempo or
        /// less (the user, 2026-10-16: "if T is pressed super fast... it's actually very low").
        /// </summary>
        void OnInputEvent(InputEventPtr inputEvent, InputDevice device)
        {
            var keyboard = device as Keyboard;
            if (keyboard == null || (!inputEvent.IsA<StateEvent>() && !inputEvent.IsA<DeltaStateEvent>()))
                return;
            // The event comes before the keyboard's state is changed by it, so a press is a key
            // that is down in the event and up until now.
            float down;
            if (keyboard.tKey.ReadValueFromEvent(inputEvent, out down) && down > 0.5f && !keyboard.tKey.isPressed)
                tapsPressed.Add(inputEvent.time);
        }

        void ForgetTaps()
        {
            lastTap = double.NegativeInfinity;
            tapIntervals = 0;
            tapLabel.text = "";
        }

        /// <summary>The presses of one frame, counted in turn; the tempo is set once, from the last.</summary>
        void Tap(List<double> times)
        {
            for (int i = 0; i < times.Count; i++)
                Count(times[i], i == times.Count - 1);
        }

        void Tap(double time)
        {
            Count(time, true);
        }

        /// <summary>
        /// Each tap sets the tempo from the time since the tap before. Taps that keep the same pace
        /// are averaged, which steadies the figure; a tap well off that pace starts a new count
        /// from itself, so one bad tap is forgotten on the next good one. Times are the Input
        /// System's, for the key and the button alike.
        /// </summary>
        void Count(double now, bool setTempo)
        {
            double interval = now - lastTap;
            lastTap = now;
            if (interval > TapTimeoutSeconds)
            {
                tapIntervals = 0;
                tapLabel.text = "tap again";
                return;
            }
            if (tapIntervals > 0 && Math.Abs(interval - tapInterval) <= TapTolerance * tapInterval)
            {
                tapInterval = (tapInterval * tapIntervals + interval) / (tapIntervals + 1);
                tapIntervals++;
            }
            else
            {
                tapInterval = interval;
                tapIntervals = 1;
            }
            // Taps faster or slower than the tempo box takes set its limit (the user, 2026-10-16):
            // refused, the count went up and the tempo stayed, as if tapping did nothing.
            double bpm = Math.Max(MinBpm, Math.Min(MaxBpm, Math.Round(60.0 / tapInterval, 1)));
            tapLabel.text = tapIntervals == 1 ? "from last 2 taps" : "average of " + (tapIntervals + 1).ToString(Inv) + " taps";
            // A tapped tempo is rough: one decimal is all it can honestly claim. Refit tightens it.
            if (setTempo)
                SetTempo(bpm);
        }

        /// <summary>Runs the analysis again, starting from the tempo now showing.</summary>
        void Refit()
        {
            SongSession song = session;
            var options = new BeatGridOptions { Bpm = song.Grid.Bpm };
            player.Pause();
            jobs.Run("Fitting grid to audio at about " + song.Grid.Bpm.ToString("0.0", Inv) + " BPM",
                (cancel, progress) => BeatGridAnalyzer.Analyze(song.TimingSamples, song.Wav.SampleRate, options, cancel),
                grid =>
                {
                    song.ApplyAnalysis(grid, true);
                    // The analysis knows nothing of the tempo box's limits: from 40 it could land
                    // on 39.x. Held to them as a tap is (the user, 2026-10-16).
                    double held = Math.Max(MinBpm, Math.Min(MaxBpm, song.Grid.Bpm));
                    if (held != song.Grid.Bpm)
                        song.SetTempo(held);
                },
                e => ShowWarnings("Grid could not be fitted. " + e.Message, true));
        }
    }
}
