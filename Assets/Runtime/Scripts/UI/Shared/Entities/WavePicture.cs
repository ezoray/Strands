using System;
using System.Collections.Generic;
using Strands.Audio;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Strands.UI.Shared
{
    /// <summary>
    /// What a waveform is drawn from, and the drawing of it. It is one track in one colour, or
    /// several laid over each other, each in a colour of its own: the stems of a song in the
    /// colours of their outputs, or one song as its low, middle and high sound, which is how a
    /// DJ player colours a waveform.
    /// It is drawn as a DJ player draws one, as smooth outlines that are the same above and below.
    /// Of several, the widest is at the back in every column and the narrowest in front, so that
    /// none is hidden by a louder one, and where they lie over each other their colours run together.
    /// All of it is drawn up by one figure, so that the loudest layer nearly fills the height and
    /// the others keep their size beside it.
    /// </summary>
    public sealed class WavePicture
    {
        const float Headroom = 0.92f;
        // An outline rises at once and falls away slowly: each column keeps this much of the one
        // before it, or what is left after a column's worth of HeldFrames when that is more. So
        // the front of a hit stays on its own column, a bass note is one shape and not a row of
        // spikes, and zoomed right in the cycles of a low note still join up.
        const float HeldEachColumn = 0.75f;
        const double HeldFrames = 530;
        // The outline is followed from this far to the left of what is drawn, so that it does not
        // depend on where the drawing starts.
        const int SettleColumns = 200;
        // How much of a layer's own colour shows where it lies over the ones behind it.
        const float OwnColour = 0.7f;
        // A mesh holds 65,536 corners at most, and every block of colour has four.
        const int MostBlocksAtOnce = 16000;

        readonly IPeaks[] layers;
        readonly Color[] colours;
        readonly float gain = 1;

        // How far each layer reaches from the middle in each column, and the layers of each column
        // from the widest to the narrowest. Kept from one drawing to the next.
        float[] reach = new float[0];
        int[] order = new int[0];
        readonly float[] held;

        public int Frames { get { return layers[0].Frames; } }

        public WavePicture(IPeaks peaks, Color colour) : this(new[] { peaks }, new[] { colour })
        {
        }

        /// <summary>How much the picture is drawn up by, so that its loudest layer nearly fills the height.</summary>
        public float Gain { get { return gain; } }

        /// <summary>
        /// Some of the layers of a larger picture, drawn at that picture's size and not their
        /// own: a stem switched off leaves the others as they were, where drawn to their own
        /// loudest point they would grow to fill the room it left.
        /// </summary>
        public WavePicture(IList<IPeaks> layers, IList<Color> layerColours, float gain) : this(layers, layerColours)
        {
            this.gain = gain;
        }

        public WavePicture(IList<IPeaks> layers, IList<Color> layerColours)
        {
            this.layers = new IPeaks[layers.Count];
            colours = new Color[layers.Count];
            held = new float[layers.Count];
            float loudest = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                this.layers[i] = layers[i];
                colours[i] = layerColours[i];
                float min, max;
                layers[i].Range(0, layers[i].Frames, out min, out max);
                loudest = Mathf.Max(loudest, Mathf.Max(-min, max));
            }
            // A track that never rises above -40 dBFS is silent, and is drawn as a flat line: not
            // drawn up, and not as the specks its few loudest samples would make.
            gain = loudest > LeadingSilence.SilentTrackLevel ? Headroom / loudest : 0;
        }

        /// <summary>One song by itself, coloured by its sound: the kick and the bass, the body of the music, and the hats.</summary>
        public static WavePicture OfBands(BandPeaks bands)
        {
            return new WavePicture(new[] { bands.Low, bands.Mid, bands.High }, new[] { Theme.BandLow, Theme.BandMid, Theme.BandHigh });
        }

        /// <summary>
        /// Draws the columns of pixels from <paramref name="fromX"/> up to <paramref name="toX"/>.
        /// Column x is the frames from <paramref name="firstFrame"/> + x * <paramref name="framesPerPixel"/>;
        /// silence is at <paramref name="middle"/> and full level <paramref name="half"/> above and below it.
        /// </summary>
        public void Draw(MeshGenerationContext context, int fromX, int toX, double firstFrame, double framesPerPixel, float middle, float half)
        {
            int columns = toX - fromX;
            if (columns <= 0)
                return;
            Measure(fromX, columns, firstFrame, framesPerPixel, half);
            int count = layers.Length, blocksPerColumn = 2 * count - 1;
            int columnsAtOnce = Mathf.Max(1, MostBlocksAtOnce / blocksPerColumn);
            for (int first = 0; first < columns; first += columnsAtOnce)
            {
                int some = Mathf.Min(columnsAtOnce, columns - first), blocks = some * blocksPerColumn;
                NativeSlice<Vertex> corners;
                NativeSlice<ushort> indices;
                context.AllocateTempMesh(4 * blocks, 6 * blocks, out corners, out indices);
                int block = 0;
                for (int column = first; column < first + some; column++)
                {
                    int at = column * count;
                    float x = fromX + column;
                    // Silence is a line one pixel thick.
                    float outer = Mathf.Max(0.5f, reach[at + order[at]]);
                    Color colour = colours[order[at]];
                    for (int depth = 0; depth < count; depth++)
                    {
                        // From the edge of this layer in to the edge of the next one, above the
                        // middle and below it; the narrowest is one block across the middle.
                        if (depth > 0)
                            colour = Color.Lerp(colour, colours[order[at + depth]], OwnColour);
                        if (depth == count - 1)
                        {
                            Block(corners, indices, block++, x, middle - outer, middle + outer, colour);
                            break;
                        }
                        float inner = Mathf.Min(outer, reach[at + order[at + depth + 1]]);
                        Block(corners, indices, block++, x, middle - outer, middle - inner, colour);
                        Block(corners, indices, block++, x, middle + inner, middle + outer, colour);
                        outer = inner;
                    }
                }
                context.DrawMesh(corners, indices);
            }
        }

        /// <summary>As <see cref="Draw"/>, but the outline of all the layers in one colour.</summary>
        public void DrawPlain(MeshGenerationContext context, int fromX, int toX, double firstFrame, double framesPerPixel, float middle, float half, Color colour)
        {
            int columns = toX - fromX, count = layers.Length;
            if (columns <= 0)
                return;
            Measure(fromX, columns, firstFrame, framesPerPixel, half);
            Painter2D painter = context.painter2D;
            painter.strokeColor = colour;
            painter.lineWidth = 1;
            painter.BeginPath();
            for (int column = 0; column < columns; column++)
            {
                // Silence is a line one pixel thick.
                float widest = Mathf.Max(0.5f, reach[column * count + order[column * count]]);
                painter.MoveTo(new Vector2(fromX + column + 0.5f, middle - widest));
                painter.LineTo(new Vector2(fromX + column + 0.5f, middle + widest));
            }
            painter.Stroke();
        }

        static void Block(NativeSlice<Vertex> corners, NativeSlice<ushort> indices, int block, float x, float top, float bottom, Color colour)
        {
            int corner = 4 * block, index = 6 * block;
            Color32 tint = colour;
            corners[corner] = new Vertex { position = new Vector3(x, bottom, Vertex.nearZ), tint = tint };
            corners[corner + 1] = new Vertex { position = new Vector3(x, top, Vertex.nearZ), tint = tint };
            corners[corner + 2] = new Vertex { position = new Vector3(x + 1, top, Vertex.nearZ), tint = tint };
            corners[corner + 3] = new Vertex { position = new Vector3(x + 1, bottom, Vertex.nearZ), tint = tint };
            indices[index] = (ushort)corner;
            indices[index + 1] = (ushort)(corner + 1);
            indices[index + 2] = (ushort)(corner + 2);
            indices[index + 3] = (ushort)(corner + 2);
            indices[index + 4] = (ushort)(corner + 3);
            indices[index + 5] = (ushort)corner;
        }

        void Measure(int fromX, int columns, double firstFrame, double framesPerPixel, float half)
        {
            int count = layers.Length;
            if (reach.Length < columns * count)
            {
                reach = new float[columns * count];
                order = new int[columns * count];
            }
            float keep = Mathf.Max(HeldEachColumn, (float)Math.Exp(-framesPerPixel / HeldFrames));
            Array.Clear(held, 0, count);
            for (int column = -SettleColumns; column < columns; column++)
            {
                double from = firstFrame + (fromX + column) * framesPerPixel;
                if (from + framesPerPixel <= 0)
                    continue;
                int at = column * count;
                for (int layer = 0; layer < count; layer++)
                {
                    float min, max;
                    layers[layer].Range(from, from + framesPerPixel, out min, out max);
                    float now = Mathf.Min(1, Mathf.Max(-min, max) * gain) * half;
                    held[layer] = Mathf.Max(now, held[layer] * keep);
                    if (column < 0)
                        continue;
                    reach[at + layer] = held[layer];
                    // Put in its place among the ones before it, widest first.
                    int place = layer;
                    while (place > 0 && reach[at + order[at + place - 1]] < held[layer])
                    {
                        order[at + place] = order[at + place - 1];
                        place--;
                    }
                    order[at + place] = layer;
                }
            }
        }
    }
}
