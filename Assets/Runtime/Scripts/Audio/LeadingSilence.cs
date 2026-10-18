using System;

namespace Strands.Audio
{
    /// <summary>Where the sound in a piece of audio begins.</summary>
    public static class LeadingSilence
    {
        // What counts as silence at the start of a file: anything this far below the loudest sample
        // in it (1/500 is 54 dB down), and never less than about -66 dB of full scale. A recording
        // often has hiss in front of the music, which is not worth a pad.
        const int SilenceBelowPeak = 500;
        const int SilenceFloor = 16;
        // What counts as faint: sound, but 30 dB or more under the loudest sample. A run of it too
        // short to be music, straight in front of a hit, is the hiss or the bleed a stem has
        // before its first note, and not where the song starts.
        const int FaintBelowPeak = 32;
        // The foot of a hit is a few milliseconds in front of its beat line, and is not faint: in
        // the song this was found on, the kick is 20 dB under its peak 7 ms before its line.
        const double HitFootSeconds = 0.010;
        // A faint run this long, as a part of a beat, could be a quiet note, and is kept.
        const double FaintRunBeats = 0.25;
        // A stretch that is faint throughout and whose level on average is this far below the
        // loudest sample (1/316 is 50 dB down) is as good as silent: a separator leaves clicks and
        // bleed of that kind in a stem where its instrument is not playing. A quiet part that is
        // really played, a shaker under a loud kick, is faint too, but it goes on, and its average
        // is some 10 dB above this.
        const int AsGoodAsSilentBelowPeak = 316;
        // A track whose loudest sample is this low (-40 dBFS) is silent from end to end: a
        // separator's vocal stem of a song with no vocals peaked at -44 dBFS and averaged -79.
        // Measured against its own peak it looked like music throughout, was drawn as a full
        // waveform and was exported (the user, 2026-10-18: "we shouldn't boost silence or near
        // silence, and we definately shouldn't be including it in export").
        const int SilentTrackPeak = 327;

        /// <summary>Whether a track whose loudest sample is <paramref name="peak"/> is silent from end to end.</summary>
        public static bool IsSilentThroughout(int peak)
        {
            return peak <= SilentTrackPeak;
        }

        /// <summary>The level, as a part of full scale, at or under which a whole track is silent: -40 dBFS.</summary>
        public static float SilentTrackLevel { get { return SilentTrackPeak / 32768f; } }

        /// <summary>The level at or under which a sample is silence, in audio whose loudest sample is <paramref name="peak"/>.</summary>
        public static int SilenceLevel(int peak)
        {
            return Math.Max(SilenceFloor, peak / SilenceBelowPeak);
        }

        /// <summary>The level at or under which a sample is faint, in audio whose loudest sample is <paramref name="peak"/>.</summary>
        public static int FaintLevel(int peak)
        {
            return Math.Max(SilenceFloor, peak / FaintBelowPeak);
        }

        /// <summary>The average level at or under which a faint stretch is as good as silent, in audio whose loudest sample is <paramref name="peak"/>.</summary>
        public static double AsGoodAsSilentLevel(int peak)
        {
            return (double)peak / AsGoodAsSilentBelowPeak;
        }

        /// <summary>
        /// Whether the song as good as starts on a moment later than its first sound: when what
        /// sounds between the two is faint and lasts less than a quarter of a beat. A stem made by
        /// a separator often has a little of something 50 ms in front of its first kick, at 40 dB
        /// under it. Taken for the start of the song, that put a beat of silence in front of the
        /// kick and the bar line on the second kick, in a song that plainly opens on its first.
        /// </summary>
        public static bool IsFaintRunUpTo(BlockLevels levels, int firstSoundFrame, double momentSeconds, double beatSeconds, int sampleRate)
        {
            int moment = (int)Math.Round(momentSeconds * sampleRate);
            if (moment <= firstSoundFrame || moment - firstSoundFrame >= FaintRunBeats * beatSeconds * sampleRate)
                return false;
            return levels.LoudestBetween(firstSoundFrame, moment - (int)(HitFootSeconds * sampleRate)) <= FaintLevel(levels.Peak);
        }

        /// <summary>The first frame that is not silent, or the frame count when there is none.</summary>
        public static int FirstSoundFrame(short[] interleavedStereo)
        {
            int peak = 0;
            foreach (short sample in interleavedStereo)
                peak = Math.Max(peak, Math.Abs((int)sample));
            int level = SilenceLevel(peak);
            for (int i = 0; i < interleavedStereo.Length; i++)
                if (interleavedStereo[i] > level || interleavedStereo[i] < -level)
                    return i / 2;
            return interleavedStereo.Length / 2;
        }
    }

    /// <summary>
    /// The loudest sample of each short block of a piece of audio, so that whether a stretch of it
    /// is silent can be told without reading every sample of it again: a song is laid out anew
    /// on every change to its grid. Silence is what it is at the start of a file
    /// (<see cref="LeadingSilence"/>): 54 dB or more below the loudest sample of the whole.
    /// </summary>
    public sealed class BlockLevels
    {
        const int BlockFrames = 256;

        readonly short[] samples;
        readonly int[] loudest;
        // The squares of each block's samples added up, for the average level of a stretch.
        readonly double[] energy;

        /// <summary>The loudest sample of all, as a size: 0 to 32768.</summary>
        public int Peak { get; private set; }

        public int Frames { get { return samples.Length / 2; } }

        /// <summary>Whether the whole of it is silent: its loudest sample is -40 dBFS or under.</summary>
        public bool IsSilentThroughout { get { return LeadingSilence.IsSilentThroughout(Peak); } }

        public BlockLevels(short[] interleavedStereo)
        {
            samples = interleavedStereo;
            loudest = new int[(Frames + BlockFrames - 1) / BlockFrames];
            energy = new double[loudest.Length];
            for (int block = 0; block < loudest.Length; block++)
            {
                loudest[block] = Scan(block * BlockFrames, Math.Min(Frames, (block + 1) * BlockFrames));
                energy[block] = Energy(block * BlockFrames, Math.Min(Frames, (block + 1) * BlockFrames));
                Peak = Math.Max(Peak, loudest[block]);
            }
        }

        /// <summary>The loudest sample from one frame up to another.</summary>
        public int LoudestBetween(int fromFrame, int toFrame)
        {
            fromFrame = Math.Max(0, fromFrame);
            toFrame = Math.Min(Frames, toFrame);
            if (toFrame <= fromFrame)
                return 0;
            int firstWhole = (fromFrame + BlockFrames - 1) / BlockFrames, afterWhole = toFrame / BlockFrames;
            if (afterWhole <= firstWhole)
                return Scan(fromFrame, toFrame);
            // The part blocks at either end are read; the whole ones between are looked up.
            int level = Math.Max(Scan(fromFrame, firstWhole * BlockFrames), Scan(afterWhole * BlockFrames, toFrame));
            for (int block = firstWhole; block < afterWhole; block++)
                level = Math.Max(level, loudest[block]);
            return level;
        }

        /// <summary>Whether there is nothing but silence from one frame up to another.</summary>
        public bool IsSilent(int fromFrame, int toFrame)
        {
            return IsSilentThroughout || LoudestBetween(fromFrame, toFrame) <= LeadingSilence.SilenceLevel(Peak);
        }

        /// <summary>The average level (root mean square) of the samples from one frame up to another.</summary>
        public double AverageBetween(int fromFrame, int toFrame)
        {
            fromFrame = Math.Max(0, fromFrame);
            toFrame = Math.Min(Frames, toFrame);
            if (toFrame <= fromFrame)
                return 0;
            int firstWhole = (fromFrame + BlockFrames - 1) / BlockFrames, afterWhole = toFrame / BlockFrames;
            double sum;
            if (afterWhole <= firstWhole)
                sum = Energy(fromFrame, toFrame);
            else
            {
                sum = Energy(fromFrame, firstWhole * BlockFrames) + Energy(afterWhole * BlockFrames, toFrame);
                for (int block = firstWhole; block < afterWhole; block++)
                    sum += energy[block];
            }
            return Math.Sqrt(sum / (2.0 * (toFrame - fromFrame)));
        }

        /// <summary>
        /// Whether a stretch is not worth a sample: silent, or faint from end to end (30 dB or
        /// more under the loudest sample) with an average level 50 dB or more under it. That is
        /// what a stem holds where its instrument is not playing and something of the others
        /// has come through.
        /// </summary>
        public bool IsAsGoodAsSilent(int fromFrame, int toFrame)
        {
            int level = LoudestBetween(fromFrame, toFrame);
            return IsSilentThroughout
                || level <= LeadingSilence.SilenceLevel(Peak)
                || (level <= LeadingSilence.FaintLevel(Peak) && AverageBetween(fromFrame, toFrame) <= LeadingSilence.AsGoodAsSilentLevel(Peak));
        }

        double Energy(int fromFrame, int toFrame)
        {
            double sum = 0;
            for (int i = 2 * fromFrame; i < 2 * toFrame; i++)
                sum += (double)samples[i] * samples[i];
            return sum;
        }

        int Scan(int fromFrame, int toFrame)
        {
            int level = 0;
            for (int i = 2 * fromFrame; i < 2 * toFrame; i++)
                level = Math.Max(level, Math.Abs((int)samples[i]));
            return level;
        }
    }
}
