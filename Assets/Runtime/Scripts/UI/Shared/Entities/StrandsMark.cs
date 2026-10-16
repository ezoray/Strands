using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>
    /// The mark: an S drawn as four strands side by side, one in the colour of each output pair.
    /// It is two bowls, each three-eighths short of a full circle. A strand on the outside of the
    /// upper bowl is on the inside of the lower one, which is what keeps the four parallel.
    /// </summary>
    public class StrandsMark : VisualElement
    {
        // On a square 100 units a side: the centres of the two bowls, the radius between the two
        // middle strands, and how far apart and how thick the strands are.
        const float UpperCentre = 30, LowerCentre = 70, MiddleRadius = 20, Pitch = 4, Thickness = 3.4f;

        public StrandsMark()
        {
            AddToClassList("mark");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext context)
        {
            float size = Mathf.Min(contentRect.width, contentRect.height);
            if (float.IsNaN(size) || size < 4)
                return;
            float unit = size / 100f;
            var origin = new Vector2((contentRect.width - size) / 2, (contentRect.height - size) / 2);
            Painter2D painter = context.painter2D;
            painter.lineWidth = Thickness * unit;
            painter.lineCap = LineCap.Butt;
            for (int strand = 0; strand < 4; strand++)
            {
                float upper = MiddleRadius + (1.5f - strand) * Pitch;
                float lower = 2 * MiddleRadius - upper;
                painter.strokeColor = Theme.Strip(strand);
                painter.BeginPath();
                // From the upper right, over the top and down the left to the middle of the S.
                painter.Arc(origin + new Vector2(50, UpperCentre) * unit, upper * unit, Angle.Degrees(315), Angle.Degrees(90), ArcDirection.CounterClockwise);
                // On round the right of the lower bowl, to end at the lower left.
                painter.Arc(origin + new Vector2(50, LowerCentre) * unit, lower * unit, Angle.Degrees(270), Angle.Degrees(495), ArcDirection.Clockwise);
                painter.Stroke();
            }
        }
    }
}
