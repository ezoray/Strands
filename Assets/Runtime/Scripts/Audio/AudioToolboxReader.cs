using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Strands.Audio
{
    /// <summary>
    /// Decodes one audio track of a file with macOS's own decoders (AudioToolbox). The app uses it
    /// for what it has no decoder of its own for: the Apple Lossless tracks of a stem file. AAC
    /// tracks, which it reads as well, are decoded by <see cref="AacReader"/> on every system,
    /// and the tests hold that against what this gives.
    /// This is what the afconvert tool is built on, and it gives the same samples: a track starts
    /// on its first real sample, with the encoder's priming left out.
    /// There is nothing to match it on Windows or Linux, so callers ask <see cref="Available"/> first.
    /// </summary>
    public static class AudioToolboxReader
    {
        const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        const sbyte ReadPermission = 1;
        const uint SignedInteger = 1 << 2, Packed = 1 << 3;
        const int ChunkFrames = 16384;

        static readonly uint UseAudioTrack = Code("uatk"), AudioTrackCount = Code("atct");
        static readonly uint FileDataFormat = Code("ffmt"), ClientDataFormat = Code("cfmt"), FileLengthFrames = Code("#frm");
        static readonly uint LinearPcm = Code("lpcm"), AppleLossless = Code("alac"), Flac = Code("flac");

        /// <summary>AudioStreamBasicDescription.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct StreamDescription
        {
            public double SampleRate;
            public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
        }

        /// <summary>AudioBuffer.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct AudioBuffer
        {
            public uint NumberChannels, DataByteSize;
            public IntPtr Data;
        }

        /// <summary>AudioBufferList with its one buffer.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct BufferList
        {
            public uint NumberBuffers;
            public AudioBuffer Buffer;
        }

        [DllImport(CoreFoundation)]
        static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] path, IntPtr pathLength, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

        [DllImport(CoreFoundation)]
        static extern void CFRelease(IntPtr reference);

        [DllImport(AudioToolbox)]
        static extern int AudioFileOpenURL(IntPtr url, sbyte permissions, uint fileTypeHint, out IntPtr audioFile);

        [DllImport(AudioToolbox)]
        static extern int AudioFileClose(IntPtr audioFile);

        [DllImport(AudioToolbox)]
        static extern int AudioFileGetProperty(IntPtr audioFile, uint property, ref uint dataSize, out uint value);

        [DllImport(AudioToolbox)]
        static extern int AudioFileSetProperty(IntPtr audioFile, uint property, uint dataSize, ref uint value);

        [DllImport(AudioToolbox)]
        static extern int ExtAudioFileWrapAudioFileID(IntPtr audioFile, [MarshalAs(UnmanagedType.I1)] bool forWriting, out IntPtr reader);

        [DllImport(AudioToolbox)]
        static extern int ExtAudioFileDispose(IntPtr reader);

        [DllImport(AudioToolbox, EntryPoint = "ExtAudioFileGetProperty")]
        static extern int ExtAudioFileGetFormat(IntPtr reader, uint property, ref uint dataSize, out StreamDescription value);

        [DllImport(AudioToolbox, EntryPoint = "ExtAudioFileGetProperty")]
        static extern int ExtAudioFileGetLength(IntPtr reader, uint property, ref uint dataSize, out long value);

        [DllImport(AudioToolbox, EntryPoint = "ExtAudioFileSetProperty")]
        static extern int ExtAudioFileSetFormat(IntPtr reader, uint property, uint dataSize, ref StreamDescription value);

        [DllImport(AudioToolbox)]
        static extern int ExtAudioFileRead(IntPtr reader, ref uint frames, ref BufferList data);

        public static bool Available { get { return RuntimeInformation.IsOSPlatform(OSPlatform.OSX); } }

        /// <summary>How many audio tracks macOS finds in the file.</summary>
        public static int TrackCount(string path)
        {
            IntPtr url = Url(path), file = IntPtr.Zero;
            try
            {
                Check(AudioFileOpenURL(url, ReadPermission, 0, out file), "open the file");
                return (int)Tracks(file);
            }
            finally
            {
                if (file != IntPtr.Zero)
                    AudioFileClose(file);
                CFRelease(url);
            }
        }

        /// <summary>
        /// Reads one audio track, counted from 0, as 16-bit stereo at the track's own sample rate.
        /// A lossless track of more than 16 bits is rounded as every other format is.
        /// </summary>
        public static WavData ReadTrack(string path, int track, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            IntPtr url = Url(path), file = IntPtr.Zero, reader = IntPtr.Zero;
            try
            {
                Check(AudioFileOpenURL(url, ReadPermission, 0, out file), "open the file");
                uint size, tracks = Tracks(file), index = (uint)track;
                if (track < 0 || track >= tracks)
                    throw new InvalidDataException("The file has " + tracks + " audio track" + (tracks == 1 ? "" : "s") + ", so there is no track " + (track + 1) + " to read.");
                if (track > 0)
                    Check(AudioFileSetProperty(file, UseAudioTrack, sizeof(uint), ref index), "find audio track " + (track + 1) + " of the file");
                Check(ExtAudioFileWrapAudioFileID(file, false, out reader), "read the file's audio");

                StreamDescription source;
                size = (uint)Marshal.SizeOf(typeof(StreamDescription));
                Check(ExtAudioFileGetFormat(reader, FileDataFormat, ref size, out source), "tell the format of the audio");
                int channels = (int)source.ChannelsPerFrame;
                if (channels < 1 || channels > 2)
                    throw new NotSupportedException("The audio has " + channels + " channels. Only mono and stereo are supported.");
                int bits = SourceBits(source);
                // More than 16 bits are asked for as 32, and rounded here by the rule every format shares.
                bool wide = bits > 16;
                int bytesPerFrame = channels * (wide ? 4 : 2);
                var wanted = new StreamDescription
                {
                    SampleRate = source.SampleRate,
                    FormatId = LinearPcm,
                    FormatFlags = SignedInteger | Packed,
                    BitsPerChannel = wide ? 32u : 16u,
                    ChannelsPerFrame = (uint)channels,
                    BytesPerFrame = (uint)bytesPerFrame,
                    FramesPerPacket = 1,
                    BytesPerPacket = (uint)bytesPerFrame
                };
                Check(ExtAudioFileSetFormat(reader, ClientDataFormat, (uint)Marshal.SizeOf(typeof(StreamDescription)), ref wanted), "decode the audio");

                // The length is only used to size the result and report progress; the reading goes on until nothing comes.
                long expected;
                size = sizeof(long);
                if (ExtAudioFileGetLength(reader, FileLengthFrames, ref size, out expected) != 0 || expected < 0 || expected > int.MaxValue / 2)
                    expected = 0;

                var samples = new short[2 * Math.Max(ChunkFrames, (int)expected)];
                int done = 0;
                var chunk = new byte[ChunkFrames * bytesPerFrame];
                GCHandle pinned = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                try
                {
                    while (true)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var list = new BufferList
                        {
                            NumberBuffers = 1,
                            Buffer = new AudioBuffer { NumberChannels = (uint)channels, DataByteSize = (uint)chunk.Length, Data = pinned.AddrOfPinnedObject() }
                        };
                        uint frames = ChunkFrames;
                        Check(ExtAudioFileRead(reader, ref frames, ref list), "decode the audio");
                        if (frames == 0)
                            break;
                        int got = (int)Math.Min(frames, (uint)ChunkFrames);
                        if (2L * (done + got) > samples.Length)
                            Array.Resize(ref samples, checked(Math.Max(2 * samples.Length, 2 * (done + got))));
                        if (!wide && channels == 2)
                            Buffer.BlockCopy(chunk, 0, samples, 4 * done, 4 * got);
                        else
                            for (int frame = 0; frame < got; frame++)
                                for (int side = 0; side < 2; side++)
                                {
                                    int at = frame * bytesPerFrame + Math.Min(side, channels - 1) * (wide ? 4 : 2);
                                    samples[2 * (done + frame) + side] = wide
                                        ? Pcm.From24Bit(BitConverter.ToInt32(chunk, at) >> 8)
                                        : BitConverter.ToInt16(chunk, at);
                                }
                        done += got;
                        if (progress != null && expected > 0)
                            progress.Report(Math.Min(1f, (float)done / expected));
                    }
                }
                finally
                {
                    pinned.Free();
                }
                if (done == 0)
                    throw new InvalidDataException("The track holds no audio.");
                Array.Resize(ref samples, 2 * done);
                return new WavData
                {
                    SampleRate = (int)Math.Round(source.SampleRate),
                    Samples = samples,
                    SourceChannels = channels,
                    SourceBitsPerSample = bits
                };
            }
            finally
            {
                if (reader != IntPtr.Zero)
                    ExtAudioFileDispose(reader);
                if (file != IntPtr.Zero)
                    AudioFileClose(file);
                CFRelease(url);
            }
        }

        /// <summary>Only the kinds of file that can hold several tracks can say how many they hold; any other holds one.</summary>
        static uint Tracks(IntPtr file)
        {
            uint size = sizeof(uint), count;
            return AudioFileGetProperty(file, AudioTrackCount, ref size, out count) == 0 ? count : 1;
        }

        /// <summary>
        /// The bit depth the audio was made from. The lossless formats keep it in their flags; a
        /// format such as AAC has none, and is decoded to 16 bits.
        /// </summary>
        static int SourceBits(StreamDescription source)
        {
            if (source.FormatId == LinearPcm)
                return (int)source.BitsPerChannel;
            if (source.FormatId != AppleLossless && source.FormatId != Flac)
                return 16;
            switch (source.FormatFlags)
            {
                case 2: return 20;
                case 3: return 24;
                case 4: return 32;
                default: return 16;
            }
        }

        static IntPtr Url(string path)
        {
            if (!Available)
                throw new NotSupportedException("This kind of file is decoded by macOS, so it can only be read on a Mac.");
            if (!File.Exists(path))
                throw new FileNotFoundException("There is no file at " + path + ".", path);
            byte[] name = Encoding.UTF8.GetBytes(path);
            IntPtr url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, name, (IntPtr)name.Length, false);
            if (url == IntPtr.Zero)
                throw new InvalidDataException("macOS could not make sense of the file's path.");
            return url;
        }

        static void Check(int status, string doing)
        {
            if (status != 0)
                throw new InvalidDataException("macOS could not " + doing + " (" + Describe(status) + ").");
        }

        /// <summary>Most of AudioToolbox's error codes are four letters, such as 'fmt?' for a format it cannot read.</summary>
        static string Describe(int status)
        {
            var letters = new char[4];
            for (int i = 0; i < 4; i++)
            {
                int letter = (status >> (24 - 8 * i)) & 0xFF;
                if (letter < 32 || letter > 126)
                    return "error " + status;
                letters[i] = (char)letter;
            }
            return "error '" + new string(letters) + "'";
        }

        static uint Code(string fourLetters)
        {
            return (uint)(fourLetters[0] << 24 | fourLetters[1] << 16 | fourLetters[2] << 8 | fourLetters[3]);
        }
    }
}
