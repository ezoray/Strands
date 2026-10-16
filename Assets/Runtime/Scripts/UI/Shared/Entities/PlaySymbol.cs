using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>
    /// The sign on a pad that plays: a triangle while the pad is quiet and a square while it
    /// sounds, when a press on it stops the pad. Each is a black shape with an edge of the quiet text's grey and nothing behind it, so
    /// that it is not taken for one of the pad's buttons. They are drawn, because neither typeface has them.
    /// </summary>
    public class PlaySymbol : VisualElement
    {
        bool playing;

        public PlaySymbol()
        {
            AddToClassList("pad__play");
            generateVisualContent += Draw;
        }

        public bool Playing
        {
            get { return playing; }
            set
            {
                if (value == playing)
                    return;
                playing = value;
                MarkDirtyRepaint();
            }
        }

        void Draw(MeshGenerationContext context)
        {
            Rect area = contentRect;
            if (float.IsNaN(area.width) || area.width < 8 || area.height < 8)
                return;
            Vector2 centre = area.center;
            const float half = 5.5f;
            Painter2D painter = context.painter2D;
            painter.fillColor = Color.black;
            painter.strokeColor = Theme.QuietText;
            painter.lineWidth = 1.5f;
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();
            if (playing)
            {
                painter.MoveTo(new Vector2(centre.x - half, centre.y - half));
                painter.LineTo(new Vector2(centre.x + half, centre.y - half));
                painter.LineTo(new Vector2(centre.x + half, centre.y + half));
                painter.LineTo(new Vector2(centre.x - half, centre.y + half));
            }
            else
            {
                // A little right of centre, where a triangle looks centred.
                painter.MoveTo(new Vector2(centre.x - half + 1.5f, centre.y - half - 0.5f));
                painter.LineTo(new Vector2(centre.x + half + 1, centre.y));
                painter.LineTo(new Vector2(centre.x - half + 1.5f, centre.y + half + 0.5f));
            }
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
        }
    }
}
