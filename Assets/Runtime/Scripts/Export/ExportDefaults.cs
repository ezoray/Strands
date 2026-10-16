using System;
using System.Collections.Generic;
using System.Text;
using Strands.Sp16;

namespace Strands.Export
{
    /// <summary>
    /// The Export page's choices as they were last left, to start the next song with (the user,
    /// 2026-10-16: the options should be saved like the click and the outputs, so that forgetting
    /// to set choke does not mean doing it again by hand). Time stretch and the two arrangement
    /// switches are kept as they are. Choke, loop and velocity are kept by strip, since a strip
    /// keeps its part from one song to the next (drums on the first, and so on), and a song in
    /// one file has a place of its own. Settings made on one pad are not kept: they belong to
    /// that song's patterns.
    /// </summary>
    public sealed class ExportDefaults
    {
        /// <summary>The four strips, then a song in one file.</summary>
        public const int Places = 5;
        public const int SongPlace = 4;

        public Sp16Stretch Stretch = Sp16Stretch.Off;
        public bool Arrangement = true;
        public bool TempoInArrangement = true;
        readonly bool[] choke = new bool[Places];
        readonly bool[] loop = new bool[Places];
        readonly bool[] velocity = new bool[Places];

        public bool Choke(int place) { return choke[place]; }
        public bool Loop(int place) { return loop[place]; }
        public bool Velocity(int place) { return velocity[place]; }

        public void Set(int place, bool chokeOn, bool loopOn, bool velocityOn)
        {
            choke[place] = chokeOn;
            loop[place] = loopOn;
            velocity[place] = velocityOn;
        }

        // Where a layer's settings are kept: its strip, or the song's place when there are no layers.
        static int PlaceOf(IList<ExportLayer> layers, int layer)
        {
            if (layers == null || layers.Count == 0)
                return SongPlace;
            return Math.Max(0, Math.Min(SongPlace - 1, layers[layer].Group));
        }

        /// <summary>Starts a song's options from these: every layer takes its strip's settings.</summary>
        public void ApplyTo(WavExportOptions options, IList<ExportLayer> layers)
        {
            options.Stretch = Stretch;
            options.Arrangement = Arrangement;
            options.TempoInArrangement = TempoInArrangement;
            int count = layers == null || layers.Count == 0 ? 1 : layers.Count;
            for (int layer = 0; layer < count; layer++)
            {
                int place = PlaceOf(layers, layer);
                options.Pads.SetChokeOfLayer(layer, choke[place]);
                options.Pads.SetLoopOfLayer(layer, loop[place]);
                options.Pads.SetVelocityOfLayer(layer, velocity[place]);
            }
        }

        /// <summary>
        /// Keeps a song's choices. A strip takes what its first layer was set to as a whole; the
        /// strips this song does not use keep what they had.
        /// </summary>
        public void TakeFrom(WavExportOptions options, IList<ExportLayer> layers)
        {
            Stretch = options.Stretch;
            Arrangement = options.Arrangement;
            TempoInArrangement = options.TempoInArrangement;
            int count = layers == null || layers.Count == 0 ? 1 : layers.Count;
            var taken = new bool[Places];
            for (int layer = 0; layer < count; layer++)
            {
                int place = PlaceOf(layers, layer);
                if (taken[place])
                    continue;
                taken[place] = true;
                Set(place, options.Pads.ChokeOfLayer(layer), options.Pads.LoopOfLayer(layer), options.Pads.VelocityOfLayer(layer));
            }
        }

        /// <summary>As one line of text, for keeping between runs of the app.</summary>
        public string ToText()
        {
            var text = new StringBuilder();
            text.Append(Sp16Project.StretchValue(Stretch)).Append(';').Append(Arrangement ? '1' : '0').Append(TempoInArrangement ? '1' : '0');
            for (int place = 0; place < Places; place++)
                text.Append(';').Append(choke[place] ? '1' : '0').Append(loop[place] ? '1' : '0').Append(velocity[place] ? '1' : '0');
            return text.ToString();
        }

        /// <summary>Reads what <see cref="ToText"/> wrote. Anything it cannot read gives the defaults.</summary>
        public static ExportDefaults FromText(string text)
        {
            var defaults = new ExportDefaults();
            if (string.IsNullOrEmpty(text))
                return defaults;
            string[] parts = text.Split(';');
            if (parts.Length != 2 + Places || parts[1].Length != 2)
                return defaults;
            Sp16Stretch stretch;
            if (!TryStretch(parts[0], out stretch))
                return defaults;
            for (int place = 0; place < Places; place++)
                if (parts[2 + place].Length != 3)
                    return defaults;
            defaults.Stretch = stretch;
            defaults.Arrangement = parts[1][0] == '1';
            defaults.TempoInArrangement = parts[1][1] == '1';
            for (int place = 0; place < Places; place++)
            {
                string flags = parts[2 + place];
                defaults.Set(place, flags[0] == '1', flags[1] == '1', flags[2] == '1');
            }
            return defaults;
        }

        static bool TryStretch(string value, out Sp16Stretch stretch)
        {
            foreach (Sp16Stretch each in (Sp16Stretch[])Enum.GetValues(typeof(Sp16Stretch)))
                if (Sp16Project.StretchValue(each) == value)
                {
                    stretch = each;
                    return true;
                }
            stretch = Sp16Stretch.Off;
            return false;
        }
    }
}
