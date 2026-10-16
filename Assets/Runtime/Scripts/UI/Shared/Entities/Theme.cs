using UnityEngine;

namespace Strands.UI.Shared
{
    /// <summary>
    /// The colours of everything that is drawn in code: the waveforms, the song strip and the mark.
    /// The stylesheet carries the same values for the controls. They are in one place so that a
    /// second colour scheme is one more set of these.
    /// </summary>
    public static class Theme
    {
        public static readonly Color Ink = new Color32(0x0d, 0x10, 0x22, 0xff);
        public static readonly Color Surface = new Color32(0x15, 0x19, 0x33, 0xff);
        public static readonly Color Raised = new Color32(0x1f, 0x25, 0x47, 0xff);
        public static readonly Color Selected = new Color32(0x2c, 0x35, 0x66, 0xff);
        public static readonly Color Well = new Color32(0x09, 0x0b, 0x19, 0xff);
        public static readonly Color OutsideShade = new Color32(0x06, 0x07, 0x10, 0xff);
        // The ground under the selected pattern. It is a colour of its own and not a wash over
        // what is there: the project blends in linear light, where a faint light wash over a dark
        // ground comes out grey.
        public static readonly Color SelectedWell = new Color32(0x15, 0x1a, 0x36, 0xff);
        public static readonly Color Line = new Color32(0x26, 0x2d, 0x52, 0xff);
        // The lines of the grid have to be seen through a waveform and on the lighter ground of the
        // selected pattern. They were first drawn two shades darker and the user could hardly see them.
        public static readonly Color BeatLine = new Color32(0x3a, 0x44, 0x7a, 0xff);
        public static readonly Color BarLine = new Color32(0x76, 0x82, 0xc4, 0xff);
        public static readonly Color Text = new Color32(0xed, 0xef, 0xff, 0xff);
        public static readonly Color SoftText = new Color32(0xc3, 0xc8, 0xea, 0xff);
        public static readonly Color QuietText = new Color32(0x9a, 0xa1, 0xc9, 0xff);
        public static readonly Color Paper = new Color32(0xf4, 0xf1, 0xe8, 0xff);
        /// <summary>Something to take note of, such as a pattern whose hits are off the lines. Not one of the outputs' colours.</summary>
        public static readonly Color Note = new Color32(0xff, 0xd6, 0x99, 0xff);

        /// <summary>A full song where it has to be one colour, such as its share of the memory: no one output's colour.</summary>
        public static readonly Color WaveAll = new Color32(0xc9, 0xcf, 0xf5, 0xff);
        // A full song has no output's colour, so its waveform is coloured by its sound instead, as a
        // DJ player's is: the low sound, the middle and the high, lighter as they go up. They are
        // kept away from the four colours of the outputs so that they are not taken for them.
        public static readonly Color BandLow = new Color32(0x6a, 0x5c, 0xff, 0xff);
        public static readonly Color BandMid = new Color32(0xe8, 0x5f, 0xd0, 0xff);
        public static readonly Color BandHigh = new Color32(0xf4, 0xf1, 0xe8, 0xff);
        public static readonly Color WaveOutside = new Color32(0x3a, 0x42, 0x70, 0xff);

        // The song strip: scenes take two grounds in turn under the waveform, so that where one
        // ends and the next begins is plain, and so do their pattern blocks.
        public static readonly Color SceneGroundA = new Color32(0x09, 0x0b, 0x19, 0xff);
        public static readonly Color SceneGroundB = new Color32(0x23, 0x2a, 0x57, 0xff);
        public static readonly Color SceneEdge = new Color32(0x76, 0x82, 0xc4, 0xff);
        public static readonly Color BlockA = new Color32(0x3f, 0x49, 0x80, 0xff);
        public static readonly Color BlockB = new Color32(0x2a, 0x32, 0x5c, 0xff);

        // The four strips of the Source screen, which are the output pairs 1-2 to 7-8 unless
        // everything is on master. A file, its waveform and its share of the memory bar all take
        // the colour of the strip they are on; the mark is these four strands.
        static readonly Color[] Strips =
        {
            new Color32(0xff, 0x7d, 0x5c, 0xff),
            new Color32(0xff, 0xd0, 0x4a, 0xff),
            new Color32(0x3f, 0xdc, 0xb6, 0xff),
            new Color32(0x8a, 0xa9, 0xff, 0xff)
        };

        /// <summary>The colour of a strip, 0 to 3: of the pair 1-2 up to 7-8, or of the strip's tracks when everything is on master.</summary>
        public static Color Strip(int strip)
        {
            return Strips[((strip % Strips.Length) + Strips.Length) % Strips.Length];
        }
    }
}
