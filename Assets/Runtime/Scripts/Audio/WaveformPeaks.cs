using System;

namespace Strands.Audio
{
    /// <summary>Something a waveform can be drawn from: the lowest and highest value of any stretch of a track.</summary>
    public interface IPeaks
    {
        int Frames { get; }

        /// <summary>
        /// Lowest and highest value, in the range -1 to 1, of the frames from <paramref name="fromFrame"/>
        /// up to but not including <paramref name="toFrame"/>. Both are 0 outside the audio.
        /// </summary>
        void Range(double fromFrame, double toFrame, out float min, out float max);
    }

    /// <summary>
    /// The lowest and highest sample of each short block of a track (left and right mixed), so a
    /// waveform can be drawn at any zoom without reading every sample.
    /// </summary>
    public sealed class WaveformPeaks : IPeaks
    {
        public const int BlockFrames = 256;

        readonly short[] samples;
        readonly float[] blockMin;
        readonly float[] blockMax;

        public int Frames { get { return samples.Length / 2; } }

        public WaveformPeaks(short[] interleavedStereo)
        {
            samples = interleavedStereo;
            int blocks = (Frames + BlockFrames - 1) / BlockFrames;
            blockMin = new float[blocks];
            blockMax = new float[blocks];
            for (int block = 0; block < blocks; block++)
                Scan(block * BlockFrames, Math.Min(Frames, (block + 1) * BlockFrames), out blockMin[block], out blockMax[block]);
        }

        public void Range(double fromFrame, double toFrame, out float min, out float max)
        {
            int from = (int)Math.Max(0, Math.Floor(fromFrame));
            int to = (int)Math.Min(Frames, Math.Max(Math.Ceiling(toFrame), from + 1));
            min = 0;
            max = 0;
            if (from >= to)
                return;

            int firstBlock = (from + BlockFrames - 1) / BlockFrames;
            int lastBlock = to / BlockFrames;
            if (lastBlock <= firstBlock)
            {
                Scan(from, to, out min, out max);
                return;
            }

            // Whole blocks from the table, and the ragged ends from the samples.
            min = float.MaxValue;
            max = float.MinValue;
            for (int block = firstBlock; block < lastBlock; block++)
            {
                min = Math.Min(min, blockMin[block]);
                max = Math.Max(max, blockMax[block]);
            }
            float edgeMin, edgeMax;
            if (from < firstBlock * BlockFrames)
            {
                Scan(from, firstBlock * BlockFrames, out edgeMin, out edgeMax);
                min = Math.Min(min, edgeMin);
                max = Math.Max(max, edgeMax);
            }
            if (to > lastBlock * BlockFrames)
            {
                Scan(lastBlock * BlockFrames, to, out edgeMin, out edgeMax);
                min = Math.Min(min, edgeMin);
                max = Math.Max(max, edgeMax);
            }
        }

        void Scan(int from, int to, out float min, out float max)
        {
            int low = int.MaxValue, high = int.MinValue;
            for (int frame = from; frame < to; frame++)
            {
                int value = samples[2 * frame] + samples[2 * frame + 1];
                if (value < low) low = value;
                if (value > high) high = value;
            }
            min = low / 65536f;
            max = high / 65536f;
        }
    }
}
