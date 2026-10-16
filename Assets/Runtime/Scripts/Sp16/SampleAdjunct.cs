namespace Strands.Sp16
{
    /// <summary>
    /// The name.wav.dat file that accompanies each sample (format spec section 8):
    /// a bare tree, not wrapped in the .prj container.
    /// </summary>
    public sealed class SampleAdjunct
    {
        /// <summary>The loader writes 2.0 and the SP-16 accepted it. What the field means is unverified.</summary>
        public double Bar = 2.0;
        public double Bpm = 0.0;
        /// <summary>Frame count of the sample.</summary>
        public int SampleNum;

        public static SampleAdjunct ForSample(int frames)
        {
            return new SampleAdjunct { SampleNum = frames };
        }

        /// <summary>
        /// For a sample cut from a song: its tempo, which the unit needs for Time Stretch and
        /// Resmpl to run at the right speed (Reported, user, 2026-10-12: with 0 they do not), and
        /// its length in bars. That <c>bar</c> is a count of bars is Unverified: the loader page
        /// has put a count of beats there.
        /// </summary>
        public static SampleAdjunct ForSample(int frames, double bars, double bpm)
        {
            return new SampleAdjunct { SampleNum = frames, Bar = bars, Bpm = bpm };
        }

        public static SampleAdjunct Read(byte[] file)
        {
            PrjNode root = PrjTree.Parse(file);
            return new SampleAdjunct
            {
                Bar = root.Scalar("bar").AsFloat64(),
                Bpm = root.Scalar("bpm").AsFloat64(),
                SampleNum = root.Scalar("sampleNum").AsInt32()
            };
        }

        public byte[] Write()
        {
            var root = new PrjNode("SampleAdjunctData");
            root.AddScalar("bar").SetFloat64(Bar);
            root.AddScalar("bpm").SetFloat64(Bpm);
            root.AddScalar("sampleNum").SetInt32(SampleNum);
            return PrjTree.Emit(root);
        }
    }
}
