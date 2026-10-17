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
    /// The whole song in one strip under the large waveform, in the colours the large waveform
    /// gives it: the scenes on two grounds by turns, a numbered block for each pattern, and a
    /// frame round the part the large waveform is showing. A click or a drag anywhere goes there.
    /// </summary>
    public class SongStripView : VisualElement
    {
        const float NamesHeight = 14;
        const float BlocksHeight = 14;
        const float WaveTop = NamesHeight + BlocksHeight + 2;
        const float Text = 10;
        // The numbers are in the mono face, so one is centred in its block by the width of its characters.
        const float Character = Text * Faces.MonoAdvance;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly VisualElement playhead;
        readonly Faces faces;

        WavePicture picture;
        int sampleRate = 44100;
        WavExportResult plan;
        int selected = -1;
        double viewStart, viewEnd;
        double playheadSeconds;
        bool pointerDown;

        /// <summary>The user pointed at this time in the song.</summary>
        public event Action<double> Clicked;
        /// <summary>
        /// The user pressed a pattern's block, or the name of a scene, which stands for its first
        /// pattern (the user, 2026-10-17: "when we click on the pattern number above the long
        /// waveform, have it put the position at the start of that pattern").
        /// </summary>
        public event Action<int> PatternChosen;

        double DurationSeconds { get { return picture == null ? 0 : (double)picture.Frames / sampleRate; } }

        /// <summary>
        /// Where the strip starts: the start of the file, or before it when the lead-in's pattern
        /// starts there, on its beat line, with silence up to the file's first sound.
        /// </summary>
        double StartSeconds
        {
            get { return plan == null || plan.Chunks.Count == 0 ? 0 : Math.Min(0, (double)plan.Chunks[0].PatternStartFrame / sampleRate); }
        }

        public SongStripView()
        {
            AddToClassList("songstrip");
            faces = new Faces(this, MarkDirtyRepaint);
            playhead = new VisualElement();
            playhead.AddToClassList("songstrip__playhead");
            playhead.pickingMode = PickingMode.Ignore;
            Add(playhead);

            generateVisualContent += Draw;
            RegisterCallback<GeometryChangedEvent>(evt => PlacePlayhead());
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
        }

        public void SetSong(WavePicture song, int songSampleRate)
        {
            picture = song;
            sampleRate = songSampleRate;
            MarkDirtyRepaint();
        }

        public void SetLayout(WavExportResult layout)
        {
            plan = layout;
            MarkDirtyRepaint();
        }

        public int Selected
        {
            get { return selected; }
            set { selected = value; MarkDirtyRepaint(); }
        }

        /// <summary>The stretch the large waveform is showing, which is framed here.</summary>
        public void SetView(double startSeconds, double endSeconds)
        {
            viewStart = startSeconds;
            viewEnd = endSeconds;
            MarkDirtyRepaint();
        }

        public double PlayheadSeconds
        {
            get { return playheadSeconds; }
            set { playheadSeconds = value; PlacePlayhead(); }
        }

        float XOf(double seconds)
        {
            double start = StartSeconds, length = DurationSeconds - start;
            return length <= 0 ? 0 : (float)((seconds - start) / length * contentRect.width);
        }

        void PlacePlayhead()
        {
            bool visible = picture != null && !float.IsNaN(contentRect.width) && contentRect.width > 2;
            playhead.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible)
                playhead.style.left = Mathf.Clamp(XOf(playheadSeconds), 0, contentRect.width - 1);
        }

        void OnPointerDown(PointerDownEvent evt)
        {
            if (picture == null || evt.button != 0)
                return;
            // Above the waveform: the row of scene names and the row of pattern blocks.
            if (evt.localPosition.y < NamesHeight + BlocksHeight && PatternChosen != null)
            {
                int chunk = ChunkAt(evt.localPosition.x);
                if (chunk >= 0)
                {
                    if (evt.localPosition.y < NamesHeight)
                        while (chunk > 0 && plan.Chunks[chunk - 1].Scene == plan.Chunks[chunk].Scene)
                            chunk--;
                    PatternChosen(chunk);
                    return;
                }
            }
            pointerDown = true;
            this.CapturePointer(evt.pointerId);
            Point(evt.localPosition.x);
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (pointerDown)
                Point(evt.localPosition.x);
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (!pointerDown)
                return;
            pointerDown = false;
            this.ReleasePointer(evt.pointerId);
        }

        /// <summary>The pattern whose block is drawn under this point across the strip, or -1.</summary>
        int ChunkAt(float x)
        {
            if (plan == null)
                return -1;
            for (int i = 0; i < plan.Chunks.Count; i++)
            {
                ChunkInfo chunk = plan.Chunks[i];
                if (x >= Mathf.Round(XOf((double)chunk.PatternStartFrame / sampleRate)) && x < Mathf.Round(XOf((double)chunk.EndFrame / sampleRate)))
                    return i;
            }
            return -1;
        }

        void Point(float x)
        {
            float width = contentRect.width;
            if (Clicked == null || float.IsNaN(width) || width < 2)
                return;
            double start = StartSeconds;
            Clicked(start + Mathf.Clamp01(x / width) * (DurationSeconds - start));
        }

        void Draw(MeshGenerationContext context)
        {
            float width = contentRect.width, height = contentRect.height;
            if (picture == null || float.IsNaN(width) || width < 2 || height < WaveTop + 6)
                return;
            Painter2D painter = context.painter2D;
            int count = plan == null ? 0 : plan.Chunks.Count;
            int selectedScene = selected >= 0 && selected < count ? plan.Chunks[selected].Scene : -1;

            // The part of the song the large waveform is showing is framed, last of all.
            float left = Mathf.Clamp(XOf(viewStart), 0, width - 2), right = Mathf.Clamp(XOf(viewEnd), left + 2, width);

            // Each scene has a ground of its own under the waveform, by turns.
            for (int i = 0; i < count; i++)
            {
                if (plan.Chunks[i].Pattern != 0)
                    continue;
                int lastOfScene = i;
                while (lastOfScene + 1 < count && plan.Chunks[lastOfScene + 1].Scene == plan.Chunks[i].Scene)
                    lastOfScene++;
                float from = Mathf.Round(XOf((double)plan.Chunks[i].PatternStartFrame / sampleRate)), to = Mathf.Round(XOf((double)plan.Chunks[lastOfScene].EndFrame / sampleRate));
                FillRect(painter, from, WaveTop - 2, to - from, height - WaveTop + 2, plan.Chunks[i].Scene % 2 == 0 ? Theme.SceneGroundA : Theme.SceneGroundB);
            }

            // What is not in the package, at either end, is drawn faintly; the rest in its colours.
            float packageStart = count == 0 ? width : XOf((double)plan.Chunks[0].PatternStartFrame / sampleRate);
            float packageEnd = count == 0 ? width : XOf((double)plan.Chunks[count - 1].EndFrame / sampleRate);
            int columns = (int)width;
            int start = Mathf.Clamp(Mathf.RoundToInt(packageStart), 0, columns), end = Mathf.Clamp(Mathf.RoundToInt(packageEnd), start, columns);
            float middle = (WaveTop + height) / 2, half = (height - WaveTop) / 2 - 1;
            double firstFrame = StartSeconds * sampleRate, framesPerPixel = (picture.Frames - firstFrame) / width;
            // There is no audio before the file starts: only the silence the lead-in's pattern begins with.
            int fileStart = Mathf.Clamp(Mathf.CeilToInt(XOf(0)), 0, columns);
            picture.DrawPlain(context, fileStart, start, firstFrame, framesPerPixel, middle, half, Theme.WaveOutside);
            picture.DrawPlain(context, end, columns, firstFrame, framesPerPixel, middle, half, Theme.WaveOutside);
            picture.Draw(context, Math.Max(start, fileStart), end, firstFrame, framesPerPixel, middle, half);

            for (int i = 0; i < count; i++)
            {
                ChunkInfo chunk = plan.Chunks[i];
                float x0 = XOf((double)chunk.PatternStartFrame / sampleRate), x1 = XOf((double)chunk.EndFrame / sampleRate);
                bool even = chunk.Scene % 2 == 0, inSelectedScene = chunk.Scene == selectedScene;

                // Blocks stand on whole pixels, as the line where a scene begins does.
                float blockLeft = Mathf.Round(x0), span = Mathf.Round(x1) - blockLeft;
                bool isSelected = i == selected;
                FillRect(painter, blockLeft + 1, NamesHeight, Mathf.Max(1, span - 1), BlocksHeight, isSelected ? Theme.Paper : even ? Theme.BlockA : Theme.BlockB);
                string label = chunk.IsLeadIn ? (span > 46 ? "lead-in" : "") : (chunk.Pattern + 1).ToString(Inv);
                if (label.Length > 0 && span > label.Length * Character + 4)
                    context.DrawText(label, new Vector2(x0 + (span - label.Length * Character) / 2, NamesHeight + 1), Faces.Size(this, Text), isSelected ? Theme.Ink : inSelectedScene ? Theme.Text : Theme.QuietText, faces.Mono);

                if (chunk.Pattern == 0)
                {
                    int lastOfScene = i;
                    while (lastOfScene + 1 < count && plan.Chunks[lastOfScene + 1].Scene == chunk.Scene)
                        lastOfScene++;
                    float room = XOf((double)plan.Chunks[lastOfScene].EndFrame / sampleRate) - x0;
                    string number = (chunk.Scene + 1).ToString(Inv);
                    string name = room > 56 ? "Scene " + number : room > 14 ? number : "";
                    if (name.Length > 0)
                        context.DrawText(name, new Vector2(x0 + 3, 0), Faces.Size(this, Text + 1), inSelectedScene ? Color.white : Theme.QuietText, inSelectedScene ? faces.Strong : faces.Text);
                }
            }

            // A line where each scene begins, over the waveform.
            painter.strokeColor = Theme.SceneEdge;
            painter.lineWidth = 1;
            painter.BeginPath();
            for (int i = 1; i < count; i++)
            {
                if (plan.Chunks[i].Pattern != 0)
                    continue;
                float x = Mathf.Round(XOf((double)plan.Chunks[i].StartFrame / sampleRate)) + 0.5f;
                painter.MoveTo(new Vector2(x, WaveTop - 2));
                painter.LineTo(new Vector2(x, height));
            }
            painter.Stroke();

            painter.strokeColor = Theme.Text;
            painter.lineWidth = 1;
            painter.BeginPath();
            painter.MoveTo(new Vector2(left + 0.5f, NamesHeight - 0.5f));
            painter.LineTo(new Vector2(right - 0.5f, NamesHeight - 0.5f));
            painter.LineTo(new Vector2(right - 0.5f, height - 0.5f));
            painter.LineTo(new Vector2(left + 0.5f, height - 0.5f));
            painter.ClosePath();
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
    }
}
