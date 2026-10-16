using System.Collections.Generic;

namespace Strands.Export
{
    /// <summary>
    /// What each pad of a package is set to on the unit beyond its sample: whether it is in its
    /// layer's choke group, and whether it loops. A setting is made for a layer, which is every
    /// pad of a stem, or for one pad by itself. Whichever was made last holds: setting a layer
    /// takes away what was set on its pads one at a time, and a pad set afterwards keeps its own
    /// until the layer is set again.
    /// A pad is named by its chunk and its layer. Its setting stays with that place when the song
    /// is laid out again, so a pad set by itself is worth looking at again after the start of the
    /// song has been moved.
    /// </summary>
    public sealed class PadSettings
    {
        sealed class Setting
        {
            readonly Dictionary<int, bool> layers = new Dictionary<int, bool>();
            readonly Dictionary<long, bool> pads = new Dictionary<long, bool>();

            static long Key(int chunk, int layer) { return ((long)chunk << 8) | (uint)layer; }

            public bool Of(int chunk, int layer)
            {
                bool on;
                if (pads.TryGetValue(Key(chunk, layer), out on))
                    return on;
                return layers.TryGetValue(layer, out on) && on;
            }

            public bool OfLayer(int layer)
            {
                bool on;
                return layers.TryGetValue(layer, out on) && on;
            }

            public void SetPad(int chunk, int layer, bool on)
            {
                pads[Key(chunk, layer)] = on;
            }

            public void SetLayer(int layer, bool on)
            {
                layers[layer] = on;
                var gone = new List<long>();
                foreach (long key in pads.Keys)
                    if ((int)(key & 0xff) == layer)
                        gone.Add(key);
                foreach (long key in gone)
                    pads.Remove(key);
            }

            public void CopyFrom(Setting other)
            {
                layers.Clear();
                pads.Clear();
                foreach (KeyValuePair<int, bool> entry in other.layers)
                    layers[entry.Key] = entry.Value;
                foreach (KeyValuePair<long, bool> entry in other.pads)
                    pads[entry.Key] = entry.Value;
            }

            public bool Any
            {
                get
                {
                    foreach (bool on in pads.Values)
                        if (on)
                            return true;
                    foreach (bool on in layers.Values)
                        if (on)
                            return true;
                    return false;
                }
            }
        }

        readonly Setting choke = new Setting();
        readonly Setting loop = new Setting();

        /// <summary>
        /// Whether a pad is in its layer's choke group: the first layer's pads in group 1, the
        /// second's in group 2. It goes by layer and not by output because the pads of a chunk
        /// start on the same step, and two in one group would cut each other off.
        /// </summary>
        public bool Choke(int chunk, int layer) { return choke.Of(chunk, layer); }

        /// <summary>
        /// Whether a pad's sample goes round for as long as it sounds. It is set apart from the
        /// choke, since a loop left to run under other pads can be what is wanted; what a package
        /// with an arrangement does about it is <see cref="WavExportResult.ChokeOf"/>.
        /// </summary>
        public bool Loop(int chunk, int layer) { return loop.Of(chunk, layer); }

        /// <summary>What a layer was last set to as a whole, whatever its pads have been set to since one at a time.</summary>
        public bool ChokeOfLayer(int layer) { return choke.OfLayer(layer); }

        /// <summary>What a layer was last set to as a whole, whatever its pads have been set to since one at a time.</summary>
        public bool LoopOfLayer(int layer) { return loop.OfLayer(layer); }

        /// <summary>Takes every setting of another, in place of its own.</summary>
        public void CopyFrom(PadSettings other)
        {
            choke.CopyFrom(other.choke);
            loop.CopyFrom(other.loop);
        }

        /// <summary>Whether any pad at all is set to loop.</summary>
        public bool AnyLoop { get { return loop.Any; } }

        public void SetChoke(int chunk, int layer, bool on)
        {
            choke.SetPad(chunk, layer, on);
        }

        public void SetLoop(int chunk, int layer, bool on)
        {
            loop.SetPad(chunk, layer, on);
        }

        /// <summary>Sets every pad of a layer, whatever its pads were set to one at a time.</summary>
        public void SetChokeOfLayer(int layer, bool on)
        {
            choke.SetLayer(layer, on);
        }

        /// <summary>Sets every pad of a layer, whatever its pads were set to one at a time.</summary>
        public void SetLoopOfLayer(int layer, bool on)
        {
            loop.SetLayer(layer, on);
        }
    }
}
