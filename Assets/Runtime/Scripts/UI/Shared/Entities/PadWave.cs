using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>
    /// The picture of one sample on its pad: the stretch of a stem, or of the song, that the pad
    /// plays. It is drawn against the loudest point of the whole stem, so a quiet stretch looks quiet.
    /// A lead-in's sample starts with silence up to its first sound, and a lead-out's ends with
    /// silence to its last beat, each drawn as a flat line, so that the line that moves across
    /// while it plays is seen to move through the silence too.
    /// </summary>
    public class PadWave : VisualElement
    {
        readonly WavePicture picture;
        readonly int firstFrame, frames, silenceFrames, trailingFrames;
        float progress = -1;

        /// <summary>How far through its stretch the pad has played, from 0 to 1, drawn as a line; below 0 for none.</summary>
        public float Progress
        {
            get { return progress; }
            set
            {
                if (Mathf.Approximately(value, progress))
                    return;
                progress = value;
                MarkDirtyRepaint();
            }
        }

        /// <param name="firstFrame">Where the sound of the sample starts in the song.</param>
        /// <param name="frames">How long the sound runs.</param>
        /// <param name="silenceFrames">The silence the sample holds in front of its sound.</param>
        /// <param name="trailingFrames">The silence the sample holds after its sound.</param>
        public PadWave(WavePicture picture, int firstFrame, int frames, int silenceFrames = 0, int trailingFrames = 0)
        {
            this.picture = picture;
            this.firstFrame = firstFrame;
            this.frames = frames;
            this.silenceFrames = System.Math.Max(0, silenceFrames);
            this.trailingFrames = System.Math.Max(0, trailingFrames);
            AddToClassList("pad__wave");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext context)
        {
            float width = contentRect.width, height = contentRect.height;
            if (picture == null || frames <= 0 || float.IsNaN(width) || width < 2 || height < 4)
                return;
            double framesPerPixel = (silenceFrames + frames + trailingFrames) / (double)width;
            int soundFrom = Mathf.Min((int)width, Mathf.RoundToInt((float)(silenceFrames / framesPerPixel)));
            int soundTo = Mathf.Max(soundFrom, (int)width - Mathf.RoundToInt((float)(trailingFrames / framesPerPixel)));
            Painter2D painter = context.painter2D;
            painter.strokeColor = Theme.QuietText;
            painter.lineWidth = 1;
            if (soundFrom > 0)
            {
                painter.BeginPath();
                painter.MoveTo(new Vector2(0, height / 2));
                painter.LineTo(new Vector2(soundFrom, height / 2));
                painter.Stroke();
            }
            if (soundTo < width)
            {
                painter.BeginPath();
                painter.MoveTo(new Vector2(soundTo, height / 2));
                painter.LineTo(new Vector2(width, height / 2));
                painter.Stroke();
            }
            // The picture counts its columns from the frame at the left edge, which is the silence's start.
            picture.Draw(context, soundFrom, soundTo, firstFrame - soundFrom * framesPerPixel, framesPerPixel, height / 2, height / 2 - 1);
            if (progress < 0)
                return;
            float x = Mathf.Round(Mathf.Clamp01(progress) * (width - 2)) + 1;
            painter.strokeColor = Theme.Paper;
            painter.lineWidth = 2;
            painter.BeginPath();
            painter.MoveTo(new Vector2(x, 0));
            painter.LineTo(new Vector2(x, height));
            painter.Stroke();
        }
    }
}
