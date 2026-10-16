using System;
using System.IO;
using System.Threading;
using NLayer;

namespace Strands.Audio
{
    /// <summary>
    /// Reads MP3 through the NLayer decoder (Assets/NLayer), and removes the samples an MP3 has in
    /// front of and behind the music, so that the first sample is the first sample of what was encoded.
    /// </summary>
    public static class Mp3Reader
    {
        /// <summary>
        /// Every Layer III decoder gives its output this many samples late. Reported, not confirmed
        /// here against an original: it is the figure LAME documents (528 + 1), and with it NLayer's
        /// output lines up with Apple's decoder to the sample on five files (2026-10-10). No MP3 has
        /// yet been compared with the WAV it was made from.
        /// </summary>
        public const int DecoderDelayFrames = 529;

        /// <summary>What the first frame of the file says about the samples to leave out.</summary>
        public struct Trim
        {
            /// <summary>Where the audio frames begin: after any ID3 tag and after the information frame.</summary>
            public int AudioOffset;
            public bool LayerThree;
            /// <summary>True when the encoder recorded how much it added; the two counts below are then meaningful.</summary>
            public bool Known;
            public int EncoderDelayFrames;
            public int EncoderPaddingFrames;
        }

        public static WavData Read(string path)
        {
            return Read(File.ReadAllBytes(path));
        }

        /// <summary>True when the file starts as an MP3 does: with an ID3 tag or with the sync bits of a frame.</summary>
        public static bool IsMp3(byte[] file)
        {
            if (file.Length >= 3 && file[0] == 'I' && file[1] == 'D' && file[2] == '3')
                return true;
            return file.Length >= 2 && file[0] == 0xFF && (file[1] & 0xE0) == 0xE0;
        }

        /// <summary>Decodes to 16-bit, without dither. Mono is copied to both channels.</summary>
        public static WavData Read(byte[] file, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            Trim trim = ReadTrim(file);

            // The frames are found here and handed to NLayer's decoder one at a time, so that every
            // frame takes up its place in time whatever the decoder makes of it. NLayer's own file
            // reader trims by rules of its own and leaves out a frame it cannot decode, which moves
            // everything after it.
            var frames = new System.Collections.Generic.List<int>();
            int header = 0;
            for (int at = trim.AudioOffset; at + 4 <= file.Length;)
            {
                int length = FrameLength(file, at);
                bool fits = length > 0 && at + length <= file.Length && (frames.Count == 0 || SameStream(file, at, header));
                // After anything that is not a frame, a header only counts when another follows it.
                bool follows = frames.Count > 0 && at == frames[frames.Count - 1] + FrameLength(file, frames[frames.Count - 1]);
                if (fits && !follows && at + length + 4 <= file.Length && FrameLength(file, at + length) == 0)
                    fits = false;
                if (!fits)
                {
                    at++;
                    continue;
                }
                if (frames.Count == 0)
                    header = at;
                frames.Add(at);
                at += length;
            }
            if (frames.Count == 0)
                throw new InvalidDataException("MP3 file has no audio that can be decoded.");

            bool versionOne = ((file[header + 1] >> 3) & 3) == 3;
            int channels = (file[header + 3] >> 6) == 3 ? 1 : 2;
            int sampleRate = SampleRate(file, header);
            int perFrame = versionOne ? 1152 : 576;
            long raw = (long)frames.Count * perFrame;

            // The music starts after the decoder's delay and the encoder's, and ends before the padding.
            long skip = DecoderDelayFrames + (trim.Known ? trim.EncoderDelayFrames : 0);
            long valid = trim.Known ? raw - trim.EncoderDelayFrames - trim.EncoderPaddingFrames : raw - skip;
            valid = Math.Max(0, Math.Min(valid, raw - skip));
            if (valid > int.MaxValue / 4)
                throw new NotSupportedException("This MP3 file is too long to load.");

            var samples = new short[valid * 2];
            var decoder = new MpegFrameDecoder();
            var buffer = new float[2304];
            int decodedFrames = 0;
            for (int index = 0; index < frames.Count; index++)
            {
                cancel.ThrowIfCancellationRequested();
                int got;
                try
                {
                    got = decoder.DecodeFrame(new Frame(file, frames[index], FrameLength(file, frames[index])), buffer, 0) / channels;
                }
                catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException || e is IndexOutOfRangeException || e is ArgumentException)
                {
                    got = 0;
                }
                // A frame that cannot be decoded is left as silence. The first frame of a file cut
                // out of a longer one is like this: it borrows bytes from a frame that is not there.
                if (got == perFrame)
                {
                    decodedFrames++;
                    long first = (long)index * perFrame - skip;
                    for (int i = 0; i < perFrame; i++)
                    {
                        long to = first + i;
                        if (to < 0 || to >= valid)
                            continue;
                        short left = ToInt16(buffer[i * channels]);
                        samples[to * 2] = left;
                        samples[to * 2 + 1] = channels == 2 ? ToInt16(buffer[i * channels + 1]) : left;
                    }
                }
                if (progress != null && (index & 63) == 0)
                    progress.Report((float)index / frames.Count);
            }
            if (decodedFrames == 0)
                throw new InvalidDataException("MP3 file has no audio that can be decoded.");

            return new WavData
            {
                SampleRate = sampleRate,
                Samples = samples,
                SourceChannels = channels,
                // An MP3 has no word length of its own; this is the one it is decoded to.
                SourceBitsPerSample = 16
            };
        }

        /// <summary>
        /// Finds the first frame and, when it is an information frame (the "Xing" or "Info" frame that
        /// LAME and ffmpeg write), reads the encoder's delay and padding from it.
        /// </summary>
        public static Trim ReadTrim(byte[] file)
        {
            int start = 0;
            // ID3v2 tags, each with its size in four 7-bit bytes.
            while (start + 10 <= file.Length && file[start] == 'I' && file[start + 1] == 'D' && file[start + 2] == '3')
                start += 10 + (((file[start + 6] & 0x7F) << 21) | ((file[start + 7] & 0x7F) << 14) | ((file[start + 8] & 0x7F) << 7) | (file[start + 9] & 0x7F))
                    + ((file[start + 5] & 0x10) != 0 ? 10 : 0);
            start = Math.Min(start, file.Length);
            var trim = new Trim { AudioOffset = start };

            // The first frame is the first place that looks like a header and is followed by another.
            int frame = -1, length = 0;
            for (int at = start; at + 4 <= file.Length && at < start + 65536; at++)
            {
                length = FrameLength(file, at);
                if (length > 0 && (at + length + 4 > file.Length || FrameLength(file, at + length) > 0))
                {
                    frame = at;
                    break;
                }
            }
            if (frame < 0)
                return trim;
            trim.LayerThree = true;

            bool versionOne = ((file[frame + 1] >> 3) & 3) == 3, mono = (file[frame + 3] >> 6) == 3;
            int tag = frame + 4 + (versionOne ? (mono ? 17 : 32) : (mono ? 9 : 17));
            if (tag + 8 > file.Length || !(Has(file, tag, "Xing") || Has(file, tag, "Info")))
                return trim;
            // An information frame holds no sound, so the audio starts after it.
            trim.AudioOffset = Math.Min(file.Length, frame + length);

            int flags = file[tag + 7];
            int lame = tag + 8 + ((flags & 1) != 0 ? 4 : 0) + ((flags & 2) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 100 : 0) + ((flags & 8) != 0 ? 4 : 0);
            if (lame + 24 > file.Length || lame + 24 > frame + length)
                return trim;
            // Nine bytes naming the encoder (LAME, Lavc, Lavf ...), then figures in the layout LAME
            // set: twelve bits of delay and twelve of padding in bytes 21 to 23.
            int delay = (file[lame + 21] << 4) | (file[lame + 22] >> 4);
            int padding = ((file[lame + 22] & 0x0F) << 8) | file[lame + 23];
            // An encoder that does not fill this in leaves zeros, which say nothing.
            if (file[lame] != 0 && (delay != 0 || padding != 0))
            {
                trim.Known = true;
                trim.EncoderDelayFrames = delay;
                trim.EncoderPaddingFrames = padding;
            }
            return trim;
        }

        static readonly int[] bitRatesVersionOne = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        static readonly int[] bitRatesVersionTwo = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
        static readonly int[] sampleRatesVersionOne = { 44100, 48000, 32000 };

        /// <summary>The length in bytes of the Layer III frame whose header is at this offset, or 0 when there is none.</summary>
        static int FrameLength(byte[] file, int at)
        {
            if (at + 4 > file.Length || file[at] != 0xFF || (file[at + 1] & 0xE0) != 0xE0)
                return 0;
            int version = (file[at + 1] >> 3) & 3;      // 3 is MPEG-1, 2 is MPEG-2, 0 is "2.5"
            int layer = (file[at + 1] >> 1) & 3;        // 1 is Layer III
            int bitRateIndex = file[at + 2] >> 4;
            int sampleRateIndex = (file[at + 2] >> 2) & 3;
            if (version == 1 || layer != 1 || bitRateIndex == 0 || bitRateIndex == 15 || sampleRateIndex == 3)
                return 0;
            int padding = (file[at + 2] >> 1) & 1;
            int sampleRate = sampleRatesVersionOne[sampleRateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            if (version == 3)
                return 144000 * bitRatesVersionOne[bitRateIndex] / sampleRate + padding;
            return 72000 * bitRatesVersionTwo[bitRateIndex] / sampleRate + padding;
        }

        static int SampleRate(byte[] file, int at)
        {
            int version = (file[at + 1] >> 3) & 3;
            return sampleRatesVersionOne[(file[at + 2] >> 2) & 3] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
        }

        /// <summary>True when two headers agree on what does not change within a file: version, layer, sample rate and mono or stereo.</summary>
        static bool SameStream(byte[] file, int at, int first)
        {
            return ((file[at + 1] ^ file[first + 1]) & 0x1E) == 0 && ((file[at + 2] ^ file[first + 2]) & 0x0C) == 0
                && ((file[at + 3] >> 6) == 3) == ((file[first + 3] >> 6) == 3);
        }

        static bool Has(byte[] file, int at, string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (file[at + i] != text[i])
                    return false;
            return true;
        }

        static short ToInt16(float value)
        {
            double scaled = Math.Round(value * 32768.0);
            return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, scaled));
        }

        /// <summary>One Layer III frame of the file, in the form NLayer's decoder asks for.</summary>
        sealed class Frame : IMpegFrame
        {
            readonly byte[] file;
            readonly int offset, length;
            int position, available;
            ulong bits;

            public Frame(byte[] file, int offset, int length)
            {
                this.file = file;
                this.offset = offset;
                this.length = length;
            }

            int VersionBits { get { return (file[offset + 1] >> 3) & 3; } }

            public MpegVersion Version { get { return VersionBits == 3 ? MpegVersion.Version1 : VersionBits == 2 ? MpegVersion.Version2 : MpegVersion.Version25; } }
            public MpegLayer Layer { get { return MpegLayer.LayerIII; } }
            public int FrameLength { get { return length; } }
            public int SampleCount { get { return VersionBits == 3 ? 1152 : 576; } }
            public int SampleRate { get { return Mp3Reader.SampleRate(file, offset); } }
            public int SampleRateIndex { get { return (file[offset + 2] >> 2) & 3; } }
            public int BitRateIndex { get { return file[offset + 2] >> 4; } }
            public int BitRate { get { return (VersionBits == 3 ? bitRatesVersionOne : bitRatesVersionTwo)[BitRateIndex] * 1000; } }
            public MpegChannelMode ChannelMode { get { return (MpegChannelMode)(file[offset + 3] >> 6); } }
            public int ChannelModeExtension { get { return (file[offset + 3] >> 4) & 3; } }
            public bool IsCopyrighted { get { return (file[offset + 3] & 8) != 0; } }
            /// <summary>When the protection bit is clear, a 16-bit CRC follows the header. It is stepped over, not checked.</summary>
            public bool HasCrc { get { return (file[offset + 1] & 1) == 0; } }
            public bool IsCorrupted { get { return false; } }

            public void Reset()
            {
                position = offset + 4 + (HasCrc ? 2 : 0);
                available = 0;
                bits = 0;
            }

            public int ReadBits(int bitCount)
            {
                while (available < bitCount)
                {
                    if (position >= offset + length)
                        throw new EndOfStreamException();
                    bits = (bits << 8) | file[position++];
                    available += 8;
                }
                available -= bitCount;
                return (int)((bits >> available) & ((1UL << bitCount) - 1));
            }
        }
    }
}
