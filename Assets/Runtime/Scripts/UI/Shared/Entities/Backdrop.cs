using Unity.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>
    /// The ground behind both screens: the four colours of the strips side by side across the
    /// window, coral, saffron, mint and periwinkle, each only a little way out of the dark so that
    /// it is seen and nothing on it is harder to read. Where one runs into the next the join is
    /// an S down the window, as the four strands of the mark run side by side round its S.
    /// It is drawn as a mesh with a colour at each corner, which has no steps in it at any size.
    /// </summary>
    public class Backdrop : VisualElement
    {
        // Close enough together that the bends of the S and the blends across it show no corners.
        const int Columns = 96, Rows = 54;
        // How much of a strip's colour is in the ground, the rest being the dark it stands on.
        // It was 0.24 until the user asked for it about 30% more faded.
        const float Strength = 0.17f;
        // How far the S swings either side of where a join would stand if it were straight, as a part of the width.
        const float Swing = 0.1f;
        // The S is a little more than one turn of a wave, so that it begins at the top heading
        // left and ends at the foot heading left, as the letter's two ends do.
        const float Turns = 1.2f, TurnsBeforeTop = 0.1f;
        // How wide the blend from one colour to the next is, as a part of the width, either side of the join.
        const float Blend = 0.085f;

        public Backdrop()
        {
            AddToClassList("backdrop");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
        }

        void Draw(MeshGenerationContext context)
        {
            float width = contentRect.width, height = contentRect.height;
            if (float.IsNaN(width) || float.IsNaN(height) || width < 1 || height < 1)
                return;
            NativeSlice<Vertex> corners;
            NativeSlice<ushort> indices;
            context.AllocateTempMesh((Columns + 1) * (Rows + 1), 6 * Columns * Rows, out corners, out indices);
            for (int row = 0; row <= Rows; row++)
            {
                for (int column = 0; column <= Columns; column++)
                {
                    float across = (float)column / Columns, down = (float)row / Rows;
                    // Left in the upper half and right in the lower, which is the way an S goes.
                    float swing = -Swing * Mathf.Sin(2 * Mathf.PI * (Turns * down - TurnsBeforeTop));
                    Color32 tint = Color.Lerp(Theme.Ink, Shade(across - swing), Strength);
                    corners[row * (Columns + 1) + column] = new Vertex { position = new Vector3(across * width, down * height, Vertex.nearZ), tint = tint };
                }
            }
            int index = 0;
            for (int row = 0; row < Rows; row++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int top = row * (Columns + 1) + column, bottom = top + Columns + 1;
                    indices[index++] = (ushort)bottom;
                    indices[index++] = (ushort)top;
                    indices[index++] = (ushort)(top + 1);
                    indices[index++] = (ushort)(top + 1);
                    indices[index++] = (ushort)(bottom + 1);
                    indices[index++] = (ushort)bottom;
                }
            }
            context.DrawMesh(corners, indices);
        }

        // The colour a part of the way across: each strip's over a quarter of the width, with a
        // soft blend into the next where the two meet.
        static Color Shade(float along)
        {
            Color shade = Theme.Strip(0);
            for (int join = 1; join < 4; join++)
                shade = Color.Lerp(shade, Theme.Strip(join), Mathf.SmoothStep(0, 1, Mathf.InverseLerp(join / 4f - Blend, join / 4f + Blend, along)));
            return shade;
        }
    }
}
