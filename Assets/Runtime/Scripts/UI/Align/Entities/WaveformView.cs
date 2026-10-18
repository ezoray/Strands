using System;
using System.Globalization;
using Strands.Audio;
using Strands.Export;
using Strands.UI.Shared;
using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Align
{
    /// <summary>
    /// The large view of the song: the waveform with beat and bar lines over it, and above it a
    /// lane showing which scene and pattern each stretch of audio becomes. It normally shows one
    /// scene, but nothing stops it: scroll the wheel to zoom, drag to move along, click to place
    /// the playhead. The whole song is in the strip underneath (<see cref="SongStripView"/>).
    /// </summary>
    public class WaveformView : VisualElement
    {
        const float SceneRowHeight = 20;
        const float PatternRowHeight = 22;
        const float LaneHeight = SceneRowHeight + 2 + PatternRowHeight + 2;
        const float RulerHeight = 16;
        const float DragThreshold = 4;
        // Dragging the grid is fine work, so it starts sooner than a drag of the view does.
        const float GridDragThreshold = 1.5f;
        // A hand letting go of a button or a trackpad moves the pointer a little. When the pointer
        // had come to rest and then moves this little, this soon before the release, it was the
        // letting go and not the user, and the grid is put back where it had come to rest.
        const double RestSeconds = 0.15, SlipSeconds = 0.08;
        const float SlipPixels = 4;
        const float LaneText = 11;
        const float FlagWidth = 10;
        const float RulerTick = 9;
        // Figures are in the mono face, so one placed from its right-hand end is as wide as its characters.
        const float LaneCharacter = LaneText * Faces.MonoAdvance;
        const double MinSecondsPerPixel = 1.0 / 4000;
        // Pixels left at the right of a fitted stretch for its closing line (a scene's line is 2 wide).
        const float FitEndRoom = 2;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly VisualElement playhead;
        readonly Button previousScene, nextScene;
        readonly Faces faces;

        WavePicture picture;
        int sampleRate = 44100;
        BeatGrid grid;
        WavExportResult plan;
        int selected = -1;
        double[] offsetsMs;
        double offBeatMs = 3;
        double viewStart;
        double secondsPerPixel = 0.05;
        double playheadSeconds;
        double flagSeconds = double.NaN;
        string leadInNote;
        // The stretch the view was last asked to fit, which is a scene. While the view still fits
        // it, it goes on fitting it when the window is resized.
        double fitStart, fitEnd;
        bool fitPending = true;
        bool fitted;

        bool pointerDown;
        bool dragging;
        Vector2 downPosition;
        double downViewStart;
        float lastDragX;
        // For telling a slip on release from a move: when the pointer last moved, and where it was
        // and when it set off again after its last rest.
        double lastMoveTime, setOffTime;
        float restX;

        /// <summary>
        /// The user clicked the waveform at this time in the song. The flag is true when Shift was
        /// held, which asks for the exact spot and not the nearest beat.
        /// </summary>
        public event Action<double, bool> Clicked;
        /// <summary>The user scrolled or zoomed the view by hand.</summary>
        public event Action ViewMovedByUser;
        /// <summary>The view shows a different stretch of the song, however that came about.</summary>
        public event Action ViewChanged;
        /// <summary>The user dragged the grid this many seconds later (earlier when negative).</summary>
        public event Action<double> GridDragged;
        /// <summary>The user asked for the scene before (-1) or after (1) the one in view.</summary>
        public event Action<int> SceneStepRequested;

        /// <summary>When set, dragging slides the grid under the audio instead of moving the view.</summary>
        public bool DragMovesGrid { get; set; }

        public double DurationSeconds { get { return picture == null ? 0 : (double)picture.Frames / sampleRate; } }

        /// <summary>
        /// The earliest moment the view can show: the start of the file, or before it when the
        /// lead-in's pattern starts there, on the beat line its silence takes it back to. The
        /// pattern is shown from that line, so that it is seen to start on the beat it is said to.
        /// </summary>
        public double StartSeconds
        {
            get { return plan == null || plan.Chunks.Count == 0 ? 0 : Math.Min(0, (double)plan.Chunks[0].PatternStartFrame / sampleRate); }
        }
        public double ViewStartSeconds { get { return viewStart; } }
        public double ViewEndSeconds { get { return viewStart + Width * secondsPerPixel; } }
        /// <summary>True while the view is the stretch it was last asked to fit, so that following the song can go scene by scene.</summary>
        public bool IsFitted { get { return fitted; } }

        float Width { get { float w = contentRect.width; return float.IsNaN(w) || w < 1 ? 1 : w; } }

        public WaveformView()
        {
            AddToClassList("waveform");
            faces = new Faces(this, MarkDirtyRepaint);
            playhead = new VisualElement();
            playhead.AddToClassList("waveform__playhead");
            playhead.pickingMode = PickingMode.Ignore;
            Add(playhead);

            var scenes = new VisualElement();
            scenes.AddToClassList("waveform__scenes");
            scenes.pickingMode = PickingMode.Ignore;
            previousScene = SceneButton(-1);
            nextScene = SceneButton(1);
            scenes.Add(previousScene);
            scenes.Add(nextScene);
            Add(scenes);

            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        Button SceneButton(int direction)
        {
            var button = new Button(() =>
            {
                if (SceneStepRequested != null)
                    SceneStepRequested(direction);
            });
            button.focusable = false;
            button.AddToClassList("waveform__scene");
            button.style.display = DisplayStyle.None;
            // A press on one of these is not a click on the waveform under it.
            button.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            return button;
        }

        /// <summary>A new song. The view starts on the whole of it until it is asked to fit a scene.</summary>
        public void SetSong(WavePicture song, int songSampleRate)
        {
            picture = song;
            sampleRate = songSampleRate;
            Fit(0, DurationSeconds);
        }

        /// <summary>
        /// Which audio is drawn: the whole song in its colours, or one stem by itself in the colour
        /// of its output and drawn up to its own loudest point. The view stays where it is.
        /// </summary>
        public void SetWave(WavePicture shown)
        {
            picture = shown;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// The grid and the chunks to draw. <paramref name="layout"/> may be null, and so may
        /// <paramref name="patternOffsetsMs"/>, the distance of each chunk's hits from the grid.
        /// </summary>
        public void SetLayout(BeatGrid beatGrid, WavExportResult layout, double[] patternOffsetsMs, double offBeatThresholdMs)
        {
            grid = beatGrid;
            plan = layout;
            offsetsMs = patternOffsetsMs;
            offBeatMs = offBeatThresholdMs;
            MarkDirtyRepaint();
        }

        /// <summary>The words on the two buttons that step to the scene before and after, or null for no such scene.</summary>
        public void SetSceneSteps(string previous, string next)
        {
            previousScene.text = previous ?? "";
            previousScene.style.display = previous == null ? DisplayStyle.None : DisplayStyle.Flex;
            nextScene.text = next ?? "";
            nextScene.style.display = next == null ? DisplayStyle.None : DisplayStyle.Flex;
        }

        public int Selected
        {
            get { return selected; }
            set { selected = value; MarkDirtyRepaint(); }
        }

        /// <summary>Where patterns are counted from, which is marked with a flag on the pattern that starts there. NaN for none.</summary>
        public double FlagSeconds
        {
            get { return flagSeconds; }
            set { flagSeconds = value; MarkDirtyRepaint(); }
        }

        /// <summary>A few words after the lead-in's name where there is room, such as the beat it starts on.</summary>
        public string LeadInNote
        {
            get { return leadInNote; }
            set { leadInNote = value; MarkDirtyRepaint(); }
        }

        public double PlayheadSeconds
        {
            get { return playheadSeconds; }
            set { playheadSeconds = value; PlacePlayhead(); }
        }

        /// <summary>Shows exactly this stretch of the song, which is how a scene is brought up.</summary>
        public void Fit(double startSeconds, double endSeconds)
        {
            fitStart = startSeconds;
            fitEnd = endSeconds;
            if (float.IsNaN(contentRect.width) || contentRect.width < 2 || picture == null)
            {
                fitPending = true;
                return;
            }
            fitPending = false;
            // The stretch ends a scene line's width short of the right-hand edge, so that the line closing it,
            // which is the next scene's first, is drawn as the one opening it is.
            secondsPerPixel = Math.Max(MinSecondsPerPixel, Math.Max(0.05, fitEnd - fitStart) / Math.Max(1, Width - FitEndRoom));
            viewStart = fitStart;
            Moved();
            fitted = true;
        }

        /// <summary>Zooms by <paramref name="factor"/> (above 1 zooms in), keeping the time under x in place.</summary>
        public void ZoomBy(double factor, float x)
        {
            double anchor = viewStart + x * secondsPerPixel;
            double widest = Math.Max(MinSecondsPerPixel, (DurationSeconds - StartSeconds) / Width);
            secondsPerPixel = Math.Max(MinSecondsPerPixel, Math.Min(widest, secondsPerPixel / factor));
            viewStart = anchor - x * secondsPerPixel;
            Moved();
        }

        public void ZoomAtCentre(double factor)
        {
            ZoomBy(factor, Width / 2);
        }

        /// <summary>Brings a stretch of the song into view, zooming out if it does not fit.</summary>
        public void ShowRange(double startSeconds, double endSeconds)
        {
            double length = endSeconds - startSeconds;
            if (length > 0.8 * Width * secondsPerPixel)
                secondsPerPixel = length / (0.8 * Width);
            double visible = Width * secondsPerPixel;
            if (startSeconds < viewStart || endSeconds > viewStart + visible)
                viewStart = startSeconds - (visible - length) / 2;
            Moved();
        }

        public void CentreOn(double seconds)
        {
            viewStart = seconds - Width * secondsPerPixel / 2;
            Moved();
        }

        void Moved()
        {
            double visible = Width * secondsPerPixel, earliest = StartSeconds;
            // The view may run on past the song by the room a fitted stretch leaves, so that the line that
            // ends the song shows when its last scene is fitted.
            double latest = Math.Max(earliest, DurationSeconds + FitEndRoom * secondsPerPixel - visible);
            viewStart = Math.Max(earliest, Math.Min(latest, viewStart));
            fitted = false;
            PlacePlayhead();
            MarkDirtyRepaint();
            if (ViewChanged != null)
                ViewChanged();
        }

        void PlacePlayhead()
        {
            float x = XOf(playheadSeconds);
            bool visible = picture != null && x >= 0 && x <= Width;
            playhead.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible)
                playhead.style.left = x;
        }

        float XOf(double seconds)
        {
            return (float)((seconds - viewStart) / secondsPerPixel);
        }

        double TimeAt(float x)
        {
            return viewStart + x * secondsPerPixel;
        }

        void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (fitPending || fitted)
                Fit(fitStart, fitEnd);
            else
                Moved();
        }

        void OnWheel(WheelEvent evt)
        {
            if (picture == null)
                return;
            if (Mathf.Abs(evt.delta.x) > Mathf.Abs(evt.delta.y))
            {
                viewStart += evt.delta.x * 20 * secondsPerPixel;
                Moved();
            }
            else
            {
                ZoomBy(Math.Pow(1.15, -evt.delta.y), evt.localMousePosition.x);
            }
            if (ViewMovedByUser != null)
                ViewMovedByUser();
            evt.StopPropagation();
        }

        void OnPointerDown(PointerDownEvent evt)
        {
            if (picture == null || evt.button != 0)
                return;
            pointerDown = true;
            dragging = false;
            downPosition = evt.localPosition;
            downViewStart = viewStart;
            lastDragX = downPosition.x;
            this.CapturePointer(evt.pointerId);
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (!pointerDown)
                return;
            float moved = evt.localPosition.x - downPosition.x;
            if (!dragging && Mathf.Abs(moved) > (DragMovesGrid ? GridDragThreshold : DragThreshold))
            {
                dragging = true;
                if (DragMovesGrid)
                {
                    // The lines set off from where they are, with no jump to catch the pointer up.
                    lastDragX = evt.localPosition.x;
                    restX = lastDragX;
                    lastMoveTime = setOffTime = Time.realtimeSinceStartupAsDouble;
                    // The view stays exactly where it is from here on, so that only the lines move.
                    // Left on its scene it would be fitted again as the scene moved with the grid.
                    fitted = false;
                }
            }
            if (!dragging)
                return;
            if (DragMovesGrid)
            {
                // The lines follow the pointer, so a line can be picked up and put on a hit.
                double now = Time.realtimeSinceStartupAsDouble;
                if (now - lastMoveTime >= RestSeconds)
                {
                    restX = lastDragX;
                    setOffTime = now;
                }
                lastMoveTime = now;
                double seconds = (evt.localPosition.x - lastDragX) * secondsPerPixel;
                lastDragX = evt.localPosition.x;
                if (seconds != 0 && GridDragged != null)
                    GridDragged(seconds);
            }
            else
            {
                viewStart = downViewStart - moved * secondsPerPixel;
                Moved();
                if (ViewMovedByUser != null)
                    ViewMovedByUser();
            }
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (!pointerDown)
                return;
            pointerDown = false;
            this.ReleasePointer(evt.pointerId);
            if (dragging && DragMovesGrid && GridDragged != null)
            {
                bool slipped = Time.realtimeSinceStartupAsDouble - setOffTime <= SlipSeconds && Mathf.Abs(lastDragX - restX) <= SlipPixels;
                if (slipped && lastDragX != restX)
                    GridDragged((restX - lastDragX) * secondsPerPixel);
            }
            if (!dragging && Clicked != null)
                Clicked(Math.Max(StartSeconds, Math.Min(DurationSeconds, TimeAt(evt.localPosition.x))), evt.shiftKey);
            dragging = false;
        }

        void Draw(MeshGenerationContext context)
        {
            float width = contentRect.width, height = contentRect.height;
            if (float.IsNaN(width) || width < 2 || height < LaneHeight + RulerHeight + 10)
                return;
            Painter2D painter = context.painter2D;
            float top = LaneHeight, bottom = height - RulerHeight;

            FillRect(painter, 0, top, width, bottom - top, Theme.Well);
            if (picture == null)
                return;

            double exportStart = 0, exportEnd = DurationSeconds;
            if (plan != null && plan.Chunks.Count > 0)
            {
                exportStart = (double)plan.Chunks[0].PatternStartFrame / sampleRate;
                exportEnd = (double)plan.Chunks[plan.Chunks.Count - 1].EndFrame / sampleRate;
            }
            // Audio that will not be in the package is drawn darker.
            float startX = Mathf.Clamp(XOf(exportStart), 0, width), endX = Mathf.Clamp(XOf(exportEnd), 0, width);
            if (startX > 0)
                FillRect(painter, 0, top, startX, bottom - top, Theme.OutsideShade);
            if (endX < width)
                FillRect(painter, endX, top, width - endX, bottom - top, Theme.OutsideShade);

            if (plan != null && selected >= 0 && selected < plan.Chunks.Count)
            {
                ChunkInfo chunk = plan.Chunks[selected];
                float x0 = Mathf.Clamp(XOf((double)chunk.PatternStartFrame / sampleRate), 0, width);
                float x1 = Mathf.Clamp(XOf((double)chunk.EndFrame / sampleRate), 0, width);
                if (x1 > x0)
                    FillRect(painter, x0, top, x1 - x0, bottom - top, Theme.SelectedWell);
            }

            DrawGrid(painter, width, top, bottom);
            DrawWave(context, width, top, bottom, startX, endX);
            DrawLane(painter, context, width);
            // After the lane, so that each boundary is one line from its block's edge down to the foot of the waveform.
            DrawChunkLines(painter, width, bottom);
            DrawBarNumbers(painter, context, width, bottom);
        }

        void DrawWave(MeshGenerationContext context, float width, float top, float bottom, float exportStartX, float exportEndX)
        {
            float middle = (top + bottom) / 2, half = (bottom - top) / 2 - 3;
            double framesPerPixel = secondsPerPixel * sampleRate;
            double firstFrame = viewStart * sampleRate;
            // The part outside the package has one colour of its own, at either end.
            int columns = (int)width;
            int start = Mathf.Clamp(Mathf.CeilToInt(exportStartX), 0, columns), end = Mathf.Clamp(Mathf.CeilToInt(exportEndX), start, columns);
            // There is no audio before the file starts. A lead-in whose pattern starts there has
            // silence up to the file's first sound, which is drawn as the line silence is.
            int fileStart = Mathf.Clamp(Mathf.CeilToInt(XOf(0)), 0, columns);
            if (fileStart > start)
                FillRect(context.painter2D, start, middle - 0.5f, fileStart - start, 1, Theme.QuietText);
            picture.DrawPlain(context, fileStart, start, firstFrame, framesPerPixel, middle, half, Theme.WaveOutside);
            picture.DrawPlain(context, end, columns, firstFrame, framesPerPixel, middle, half, Theme.WaveOutside);
            picture.Draw(context, Math.Max(start, fileStart), end, firstFrame, framesPerPixel, middle, half);
        }

        void DrawGrid(Painter2D painter, float width, float top, float bottom)
        {
            if (grid == null)
                return;
            double beat = grid.BeatSeconds;
            float pixelsPerBeat = (float)(beat / secondsPerPixel);
            bool beats = pixelsPerBeat >= 7, bars = pixelsPerBeat * 4 >= 5;
            if (!bars)
                return;

            long first = (long)Math.Floor((viewStart - grid.FirstDownbeatSeconds) / beat);
            long last = (long)Math.Floor((Math.Min(ViewEndSeconds, DurationSeconds) - grid.FirstDownbeatSeconds) / beat);
            for (int pass = 0; pass < 2; pass++)
            {
                bool barPass = pass == 1;
                if (!barPass && !beats)
                    continue;
                painter.strokeColor = barPass ? Theme.BarLine : Theme.BeatLine;
                painter.lineWidth = 1;
                painter.BeginPath();
                for (long index = first; index <= last; index++)
                {
                    bool isBar = ((index % 4) + 4) % 4 == 0;
                    if (isBar != barPass)
                        continue;
                    float x = Mathf.Round(XOf(grid.FirstDownbeatSeconds + index * beat)) + 0.5f;
                    if (x < 0 || x > width)
                        continue;
                    painter.MoveTo(new Vector2(x, top));
                    painter.LineTo(new Vector2(x, bottom));
                }
                painter.Stroke();
            }
        }

        void DrawChunkLines(Painter2D painter, float width, float bottom)
        {
            if (plan == null)
                return;
            for (int i = 0; i <= plan.Chunks.Count; i++)
            {
                bool end = i == plan.Chunks.Count;
                int frame = end ? plan.Chunks[i - 1].EndFrame : plan.Chunks[i].PatternStartFrame;
                float edge = Edge(XOf((double)frame / sampleRate));
                if (edge < -2 || edge > width + 1)
                    continue;
                // A scene begins with a heavier line than a pattern does, and its line starts at the
                // top, through the scene's own row. A pattern's starts at the row of patterns. Either
                // is the edge of the blocks it runs between.
                bool sceneStart = end || plan.Chunks[i].Pattern == 0;
                float thick = sceneStart ? 2 : 1, top = sceneStart ? 0 : SceneRowHeight + 2;
                painter.strokeColor = Theme.Paper;
                painter.lineWidth = thick;
                painter.BeginPath();
                painter.MoveTo(new Vector2(edge + thick / 2, top));
                painter.LineTo(new Vector2(edge + thick / 2, bottom));
                painter.Stroke();
            }
        }

        void DrawLane(Painter2D painter, MeshGenerationContext context, float width)
        {
            if (plan == null)
                return;
            int count = plan.Chunks.Count;
            int selectedScene = selected >= 0 && selected < count ? plan.Chunks[selected].Scene : -1;
            long flagFrame = double.IsNaN(flagSeconds) ? long.MinValue : (long)Math.Round(flagSeconds * sampleRate);
            for (int i = 0; i < count; i++)
            {
                ChunkInfo chunk = plan.Chunks[i];
                // A block starts where its pattern does, which for the lead-in is the beat line in front of its first sound.
                float x0 = XOf((double)chunk.PatternStartFrame / sampleRate), x1 = XOf((double)chunk.EndFrame / sampleRate);
                bool flagged = Math.Abs(chunk.StartFrame - flagFrame) <= 1;

                if (chunk.Pattern == 0)
                {
                    // The scene runs to the start of the next scene, or to the end of the last chunk.
                    int lastOfScene = i;
                    while (lastOfScene + 1 < count && plan.Chunks[lastOfScene + 1].Scene == chunk.Scene)
                        lastOfScene++;
                    float sceneEnd = Edge(XOf((double)plan.Chunks[lastOfScene].EndFrame / sampleRate));
                    if (sceneEnd > 0 && x0 < width)
                    {
                        float left = Mathf.Max(Edge(x0), -2), right = Mathf.Min(sceneEnd, width + 2);
                        FillRect(painter, left, 0, right - left, SceneRowHeight, Theme.Raised);
                        float room = right - Mathf.Max(left, 0);
                        if (room > 60)
                        {
                            string name = "Scene " + (chunk.Scene + 1).ToString(Inv);
                            if (room > 300)
                                name += "  ·  " + Bars(FirstBar(plan.Chunks[i]), LastBar(plan.Chunks[lastOfScene]));
                            bool inView = chunk.Scene == selectedScene;
                            context.DrawText(name, new Vector2(Mathf.Max(left, 0) + 8, 3), Faces.Size(this, LaneText), inView ? Color.white : Theme.SoftText, inView ? faces.Strong : faces.Text);
                        }
                    }
                }

                // A pattern that starts at the right-hand edge is the next scene's first, and has nothing in view.
                if (x1 < 0 || x0 >= width)
                    continue;
                // A block runs from its own boundary line to the next one, on whole pixels, which is
                // where the lines are drawn: they are its two edges.
                float y = SceneRowHeight + 2, blockLeft = Edge(x0), span = Edge(x1) - Edge(x0);
                bool isSelected = i == selected;
                FillRect(painter, blockLeft, y, span, PatternRowHeight, isSelected ? Theme.Selected : Theme.Raised);
                if (isSelected && span > 6)
                    StrokeRect(painter, blockLeft, y, span + 1, PatternRowHeight, Theme.Paper);
                bool hasOffset = offsetsMs != null && i < offsetsMs.Length && !double.IsNaN(offsetsMs[i]);
                bool offBeat = hasOffset && Math.Abs(offsetsMs[i]) >= offBeatMs;
                // The flag stands at the start of the pattern that patterns are counted from.
                bool showFlag = flagged && x0 >= -FlagWidth && x0 < width && span > FlagWidth + 4;
                if (showFlag)
                    DrawFlag(painter, blockLeft + 2, y);
                // What is said on a block has to fit the part of it that is in view. A pattern says
                // how far its hits are from the lines only when they are off them: on the lines there
                // is nothing to tell, and a row of noughts was only something to puzzle over.
                float inViewLeft = Mathf.Max(x0, 0), inViewRight = Mathf.Min(x1, width);
                string offset = offBeat && inViewRight - inViewLeft > 170 ? Math.Abs(offsetsMs[i]).ToString("0", Inv) + " ms " + (offsetsMs[i] > 0 ? "late" : "early") : null;
                float offsetLeft = offset == null ? inViewRight : inViewRight - 8 - offset.Length * LaneCharacter;
                // The name takes the room that is left: the longest way of saying it that ends short
                // of the offset, so that the two never lie over each other.
                float labelLeft = inViewLeft + 8 + (showFlag ? FlagWidth + 2 : 0);
                float labelRoom = offsetLeft - (offset == null ? 8 : 12) - labelLeft;
                // The lead-in is a pattern like the others, the first, and says so where there is room.
                string number = (chunk.Pattern + 1).ToString(Inv);
                string[] names = chunk.IsLeadIn
                    ? new[] { string.IsNullOrEmpty(leadInNote) ? null : "Lead-in Pattern " + number + "  ·  " + leadInNote, "Lead-in Pattern " + number, "Lead-in", "P" + number }
                    : chunk.IsLeadOut
                    ? new[] { "Lead-out Pattern " + number + "  ·  " + Bars(FirstBar(chunk), LastBar(chunk)), "Lead-out Pattern " + number, "Lead-out", "P" + number }
                    : new[] { "Pattern " + number + "  ·  " + Bars(FirstBar(chunk), LastBar(chunk)), "Pattern " + number, "P" + number };
                foreach (string name in names)
                {
                    // Counted as the widest the letters can be, so a name that is let through always fits.
                    if (name == null || name.Length * LaneCharacter > labelRoom)
                        continue;
                    context.DrawText(name, new Vector2(labelLeft, y + 4), Faces.Size(this, LaneText), isSelected ? Color.white : Theme.SoftText, faces.Text);
                    break;
                }
                if (offset != null)
                    context.DrawText(offset, new Vector2(offsetLeft, y + 4), Faces.Size(this, LaneText), Theme.Note, faces.Mono);
                else if (offBeat && span > 6)
                {
                    FillRect(painter, blockLeft + 2, y + PatternRowHeight - 3, span - 3, 2, Theme.Note);
                }
            }
        }

        // Where a boundary at x is drawn: on a whole pixel, the same for a line and for the blocks either side of it.
        static float Edge(float x)
        {
            return Mathf.Round(x);
        }

        // A small flag on a pole, at the left-hand edge of its pattern.
        static void DrawFlag(Painter2D painter, float x, float y)
        {
            painter.fillColor = Theme.Paper;
            painter.BeginPath();
            painter.MoveTo(new Vector2(x + 3, y + 4));
            painter.LineTo(new Vector2(x + 3 + FlagWidth - 2, y + 8));
            painter.LineTo(new Vector2(x + 3, y + 12));
            painter.ClosePath();
            painter.Fill();
            painter.strokeColor = Theme.Paper;
            painter.lineWidth = 1;
            painter.BeginPath();
            painter.MoveTo(new Vector2(x + 2.5f, y + 3));
            painter.LineTo(new Vector2(x + 2.5f, y + PatternRowHeight - 3));
            painter.Stroke();
        }

        // Bars are counted on screen from the first bar line in the file.
        static int FirstBar(ChunkInfo chunk)
        {
            return chunk.StartBar + 1;
        }

        static int LastBar(ChunkInfo chunk)
        {
            return chunk.StartBar + chunk.Bars;
        }

        static string Bars(int first, int last)
        {
            // A lead-in comes before bar 1, so a scene that starts with one is counted from its first whole bar.
            first = Math.Max(1, first);
            return first >= last ? "bar " + last.ToString(Inv) : string.Format(Inv, "bars {0} to {1}", first, last);
        }

        /// <summary>
        /// The bars, numbered under the waveform. Each number stands at a tick that carries its
        /// bar line down into the ruler, so that it is plain which line it belongs to. The lane
        /// above says they are bars.
        /// </summary>
        void DrawBarNumbers(Painter2D painter, MeshGenerationContext context, float width, float bottom)
        {
            if (grid == null)
                return;
            double bar = 4 * grid.BeatSeconds;
            float pixelsPerBar = (float)(bar / secondsPerPixel);
            int step = 1;
            while (step * pixelsPerBar < 44)
                step *= 2;
            long first = (long)Math.Floor((viewStart - grid.FirstDownbeatSeconds) / bar / step) * step;
            painter.strokeColor = Theme.BarLine;
            painter.lineWidth = 1;
            painter.BeginPath();
            for (long index = Math.Max(0, first); ; index += step)
            {
                double seconds = grid.FirstDownbeatSeconds + index * bar;
                float x = XOf(seconds);
                if (x > width - 10 || seconds >= DurationSeconds)
                    break;
                if (x < 0)
                    continue;
                float line = Mathf.Round(x) + 0.5f;
                painter.MoveTo(new Vector2(line, bottom));
                painter.LineTo(new Vector2(line, bottom + RulerTick));
                context.DrawText((index + 1).ToString(Inv), new Vector2(line + 4, bottom + 1), Faces.Size(this, LaneText), Theme.QuietText, faces.Mono);
            }
            painter.Stroke();
        }

        static void FillRect(Painter2D painter, float x, float y, float width, float height, Color color)
        {
            if (width <= 0 || height <= 0)
                return;
            painter.fillColor = color;
            painter.BeginPath();
            painter.MoveTo(new Vector2(x, y));
            painter.LineTo(new Vector2(x + width, y));
            painter.LineTo(new Vector2(x + width, y + height));
            painter.LineTo(new Vector2(x, y + height));
            painter.ClosePath();
            painter.Fill();
        }

        static void StrokeRect(Painter2D painter, float x, float y, float width, float height, Color color)
        {
            if (width <= 0 || height <= 0)
                return;
            painter.strokeColor = color;
            painter.lineWidth = 1;
            painter.BeginPath();
            painter.MoveTo(new Vector2(x + 0.5f, y + 0.5f));
            painter.LineTo(new Vector2(x + width - 0.5f, y + 0.5f));
            painter.LineTo(new Vector2(x + width - 0.5f, y + height - 0.5f));
            painter.LineTo(new Vector2(x + 0.5f, y + height - 0.5f));
            painter.ClosePath();
            painter.Stroke();
        }
    }
}
