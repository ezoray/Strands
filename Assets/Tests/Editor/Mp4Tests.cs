using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Strands.Audio;

namespace Strands.Tests
{
    /// <summary>
    /// Finding the audio of an MP4 file from the file's own lists: which tracks are sound, how each
    /// is coded, where every packet lies, and which stretch of what they decode to is the sound.
    /// The files are built here box by box, so that the lists can be laid out in ways one encoder
    /// would never choose. Real files are in the tests of stem files.
    /// </summary>
    public class Mp4Tests
    {
        static readonly byte[] Settings = { 0x12, 0x10 };

        static byte[] Box(string type, params byte[][] contents)
        {
            var box = new List<byte>();
            box.AddRange(Number(8 + contents.Sum(part => part.Length), 4));
            box.AddRange(Encoding.ASCII.GetBytes(type));
            foreach (byte[] part in contents)
                box.AddRange(part);
            return box.ToArray();
        }

        static byte[] Number(long value, int bytes)
        {
            var number = new byte[bytes];
            for (int i = 0; i < bytes; i++)
                number[bytes - 1 - i] = (byte)(value >> (8 * i));
            return number;
        }

        static byte[] Join(params byte[][] parts)
        {
            return parts.SelectMany(part => part).ToArray();
        }

        static byte[] Version(int version)
        {
            return new byte[] { (byte)version, 0, 0, 0 };
        }

        /// <summary>A descriptor of an esds box: a tag, a length, and what it holds. Apple writes the length in four bytes.</summary>
        static byte[] Descriptor(int tag, bool longLength, params byte[][] contents)
        {
            byte[] inside = Join(contents);
            byte[] length = longLength ? new byte[] { 0x80, 0x80, 0x80, (byte)inside.Length } : new[] { (byte)inside.Length };
            return Join(new[] { (byte)tag }, length, inside);
        }

        static byte[] Esds(byte[] settings, bool longLengths = true, int objectType = 0x40)
        {
            byte[] decoder = Descriptor(4, longLengths, new byte[] { (byte)objectType, 0x15 }, new byte[3], Number(256000, 4), Number(256000, 4), Descriptor(5, longLengths, settings));
            return Box("esds", Version(0), Descriptor(3, longLengths, Number(1, 2), new byte[1], decoder, Descriptor(6, longLengths, new byte[] { 2 })));
        }

        /// <summary>The description of a track's sound: its four letters, the fields every sound has, and the boxes inside it.</summary>
        static byte[] Description(string coding, params byte[][] inside)
        {
            byte[] entry = Box(coding, Join(new byte[6], Number(1, 2), new byte[8], Number(2, 2), Number(16, 2), new byte[4], Number(44100L << 16, 4)), Join(inside));
            return Box("stsd", Version(0), Number(1, 4), entry);
        }

        static byte[] Sizes(params int[] sizes)
        {
            return Box("stsz", Version(0), Number(0, 4), Number(sizes.Length, 4), Join(sizes.Select(size => Number(size, 4)).ToArray()));
        }

        /// <summary>Runs of chunks that hold the same number of packets: the chunk each run starts at, counted from 1, and that number.</summary>
        static byte[] Runs(params int[] firstAndEach)
        {
            var runs = new List<byte[]>();
            for (int i = 0; i < firstAndEach.Length; i += 2)
                runs.Add(Join(Number(firstAndEach[i], 4), Number(firstAndEach[i + 1], 4), Number(1, 4)));
            return Box("stsc", Version(0), Number(runs.Count, 4), Join(runs.ToArray()));
        }

        static byte[] Offsets(bool wide, params long[] offsets)
        {
            return Box(wide ? "co64" : "stco", Version(0), Number(offsets.Length, 4), Join(offsets.Select(offset => Number(offset, wide ? 8 : 4)).ToArray()));
        }

        static byte[] MediaHeader(int timeScale, bool wide = false)
        {
            return wide
                ? Box("mdhd", Version(1), new byte[16], Number(timeScale, 4), new byte[12])
                : Box("mdhd", Version(0), new byte[8], Number(timeScale, 4), new byte[8]);
        }

        static byte[] Track(string holds, byte[] mediaHeader, byte[] edits, params byte[][] table)
        {
            byte[] handler = Box("hdlr", new byte[8], Encoding.ASCII.GetBytes(holds), new byte[12], Encoding.ASCII.GetBytes("Handler\0"));
            return Box("trak", Box("tkhd", new byte[84]), edits ?? new byte[0], Box("mdia", mediaHeader, handler, Box("minf", Box("smhd", new byte[8]), Box("stbl", table))));
        }

        /// <summary>One edit after another: how long each lasts on the file's clock and where it starts on the track's.</summary>
        static byte[] Edits(bool wide, params long[] lengthAndStart)
        {
            var entries = new List<byte[]>();
            for (int i = 0; i < lengthAndStart.Length; i += 2)
                entries.Add(Join(Number(lengthAndStart[i], wide ? 8 : 4), Number(lengthAndStart[i + 1], wide ? 8 : 4), Number(0x10000, 4)));
            return Box("edts", Box("elst", Version(wide ? 1 : 0), Number(entries.Count, 4), Join(entries.ToArray())));
        }

        static readonly byte[] Start = Box("ftyp", Encoding.ASCII.GetBytes("mp42"), new byte[4], Encoding.ASCII.GetBytes("isommp42"));

        /// <summary>A whole file: the audio first, as Traktor has it, and then the lists. The audio begins 8 bytes after <see cref="Start"/>.</summary>
        static MemoryStream File(byte[] audio, int movieTimeScale, params byte[][] tracks)
        {
            return new MemoryStream(Join(Start, Box("mdat", audio), Box("moov", Box("mvhd", Version(0), new byte[8], Number(movieTimeScale, 4), new byte[84]), Join(tracks))));
        }

        static readonly int AudioStart = Start.Length + 8;

        /// <summary>A packet that can be told from every other: its number, as many times as it is long.</summary>
        static byte[] Packet(int number, int length)
        {
            return Enumerable.Repeat((byte)(number + 1), length).ToArray();
        }

        [Test]
        public void AudioTracks_FindsEveryPacketFromTheFilesOwnLists()
        {
            // Eight packets in four chunks, with another track's audio between the chunks. The first two
            // chunks hold three packets each and the last two hold one.
            int[] sizes = { 3, 5, 2, 7, 1, 4, 6, 2 };
            var audio = new List<byte>();
            var chunkOffsets = new List<long>();
            int packet = 0;
            foreach (int count in new[] { 3, 3, 1, 1 })
            {
                audio.AddRange(new byte[11]);
                chunkOffsets.Add(AudioStart + audio.Count);
                for (int i = 0; i < count; i++, packet++)
                    audio.AddRange(Packet(packet, sizes[packet]));
            }
            MemoryStream file = File(audio.ToArray(), 44100,
                Track("soun", MediaHeader(44100), null, Description("mp4a", Esds(Settings)), Sizes(sizes), Runs(1, 3, 3, 1), Offsets(false, chunkOffsets.ToArray())));

            List<Mp4AudioTrack> tracks = Mp4.AudioTracks(file);
            Assert.That(tracks.Count, Is.EqualTo(1));
            Mp4AudioTrack track = tracks[0];
            Assert.That(track.Coding, Is.EqualTo("mp4a"));
            Assert.That(track.ObjectType, Is.EqualTo(0x40));
            Assert.That(track.DecoderSettings, Is.EqualTo(Settings));
            Assert.That(track.PacketCount, Is.EqualTo(8));
            // Out of order on purpose: each packet is found on its own.
            foreach (int i in new[] { 7, 0, 3, 2, 6, 1, 5, 4 })
                Assert.That(track.ReadPacket(file, i), Is.EqualTo(Packet(i, sizes[i])), "packet " + i);
        }

        [Test]
        public void AudioTracks_ReadsPacketsOfOneSizeAndOffsetsOfEightBytes()
        {
            // Every packet 4 bytes long, which the list then says once, two to a chunk, in a file long enough to need wide offsets.
            var audio = new List<byte>();
            var chunkOffsets = new List<long>();
            for (int chunk = 0; chunk < 3; chunk++)
            {
                audio.AddRange(new byte[5]);
                chunkOffsets.Add(AudioStart + audio.Count);
                audio.AddRange(Packet(2 * chunk, 4));
                audio.AddRange(Packet(2 * chunk + 1, 4));
            }
            byte[] oneSize = Box("stsz", Version(0), Number(4, 4), Number(6, 4));
            MemoryStream file = File(audio.ToArray(), 600,
                Track("soun", MediaHeader(48000, wide: true), null, Description("mp4a", Esds(Settings, longLengths: false)), oneSize, Runs(1, 2), Offsets(true, chunkOffsets.ToArray())));

            Mp4AudioTrack track = Mp4.AudioTracks(file)[0];
            Assert.That(track.DecoderSettings, Is.EqualTo(Settings));
            Assert.That(track.PacketCount, Is.EqualTo(6));
            for (int i = 0; i < 6; i++)
                Assert.That(track.ReadPacket(file, i), Is.EqualTo(Packet(i, 4)), "packet " + i);
        }

        [Test]
        public void AudioTracks_TheEditSaysWhichFramesAreTheSound()
        {
            byte[] audio = Packet(0, 4);
            byte[][] table = { Description("mp4a", Esds(Settings)), Sizes(4), Runs(1, 1), Offsets(false, AudioStart) };
            long first, frames;

            // Two seconds on a file clock of 1000 to the second, from 2,112 on a track clock of 44,100.
            Mp4.AudioTracks(File(audio, 1000, Track("soun", MediaHeader(44100), Edits(false, 2000, 2112), table)))[0].Sound(44100, out first, out frames);
            Assert.That(first, Is.EqualTo(2112));
            Assert.That(frames, Is.EqualTo(88200));

            // The same edit when the audio decodes at another rate than the track's clock counts in.
            Mp4.AudioTracks(File(audio, 1000, Track("soun", MediaHeader(44100), Edits(false, 2000, 2112), table)))[0].Sound(48000, out first, out frames);
            Assert.That(first, Is.EqualTo(2299));
            Assert.That(frames, Is.EqualTo(96000));

            // Numbers of eight bytes, and a stretch of nothing in front, which starts at -1 and is not the edit that plays.
            Mp4.AudioTracks(File(audio, 44100, Track("soun", MediaHeader(44100, wide: true), Edits(true, 500, -1, 8820, 1024), table)))[0].Sound(44100, out first, out frames);
            Assert.That(first, Is.EqualTo(1024));
            Assert.That(frames, Is.EqualTo(8820));

            // No edit: everything that is decoded, however much that is.
            Mp4.AudioTracks(File(audio, 44100, Track("soun", MediaHeader(44100), null, table)))[0].Sound(44100, out first, out frames);
            Assert.That(first, Is.EqualTo(0));
            Assert.That(frames, Is.EqualTo(-1));
        }

        [Test]
        public void AudioTracks_AreTheSoundTracksInTheFilesOrder()
        {
            byte[] audio = Join(Packet(0, 4), Packet(1, 6));
            MemoryStream file = File(audio, 44100,
                Track("vide", MediaHeader(30), null, Description("avc1"), Sizes(4), Runs(1, 1), Offsets(false, AudioStart)),
                Track("soun", MediaHeader(44100), null, Description("alac"), Sizes(4), Runs(1, 1), Offsets(false, AudioStart)),
                Track("soun", MediaHeader(44100), null, Description("mp4a", Esds(Settings)), Sizes(6), Runs(1, 1), Offsets(false, AudioStart + 4)));

            List<Mp4AudioTrack> tracks = Mp4.AudioTracks(file);
            Assert.That(tracks.Select(track => track.Coding), Is.EqualTo(new[] { "alac", "mp4a" }));
            // A coding with no esds box has no settings and no kind.
            Assert.That(tracks[0].DecoderSettings, Is.Null);
            Assert.That(tracks[0].ObjectType, Is.EqualTo(0));
            Assert.That(tracks[0].ReadPacket(file, 0), Is.EqualTo(Packet(0, 4)));
            Assert.That(tracks[1].ReadPacket(file, 0), Is.EqualTo(Packet(1, 6)));
            // Neither is audio the AAC decoder takes: one is not AAC, and the other's kind is checked next.
            Assert.That(AacReader.CanDecode(tracks[0]), Is.False);
        }

        [Test]
        public void AacReader_OnlyTakesMpeg4AudioWithSettingsItCanUse()
        {
            byte[] audio = Packet(0, 4);
            byte[][] rest = { Sizes(4), Runs(1, 1), Offsets(false, AudioStart) };
            Func<byte[], Mp4AudioTrack> track = description =>
                Mp4.AudioTracks(File(audio, 44100, Track("soun", MediaHeader(44100), null, new[] { description }.Concat(rest).ToArray())))[0];

            // AAC-LC, 44.1 kHz, stereo: what Traktor writes.
            Assert.That(AacReader.CanDecode(track(Description("mp4a", Esds(Settings)))), Is.True);
            // An mp4a track can hold MP3 too, which says so with another kind.
            Assert.That(AacReader.CanDecode(track(Description("mp4a", Esds(Settings, objectType: 0x6B)))), Is.False);
            // Settings that are not there, or are not settings.
            Assert.That(AacReader.CanDecode(track(Description("mp4a"))), Is.False);
            Assert.That(AacReader.CanDecode(track(Description("mp4a", Esds(new byte[0])))), Is.False);
        }

        [Test]
        public void AudioTracks_RefusesListsThatDoNotAddUp()
        {
            byte[] audio = Join(Packet(0, 4), Packet(1, 4));
            byte[] description = Description("mp4a", Esds(Settings));
            Func<byte[][], TestDelegate> read = table => () => Mp4.AudioTracks(File(audio, 44100, Track("soun", MediaHeader(44100), null, table)));

            // The second packet would lie past the end of the file: the file has been cut short.
            Assert.That(read(new[] { description, Sizes(4, 4000), Runs(1, 2), Offsets(false, AudioStart) }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("cut short"));
            // Chunks are counted from 1.
            Assert.That(read(new[] { description, Sizes(4, 4), Runs(0, 2), Offsets(false, AudioStart) }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("out of order"));
            // Two packets, and room said for one.
            Assert.That(read(new[] { description, Sizes(4, 4), Runs(1, 1), Offsets(false, AudioStart) }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("all of a track's audio"));
            // A count of packets with no sizes behind it.
            Assert.That(read(new[] { description, Box("stsz", Version(0), Number(0, 4), Number(500, 4)), Runs(1, 2), Offsets(false, AudioStart) }),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("packets"));
            // A track with no lists at all has no packets, which is not an error until something is asked of it.
            Assert.That(Mp4.AudioTracks(File(audio, 44100, Track("soun", MediaHeader(44100), null, description)))[0].PacketCount, Is.EqualTo(0));
        }

        [Test]
        public void AudioTracks_RefusesWhatIsNotAnMp4()
        {
            Assert.That(() => Mp4.AudioTracks(new MemoryStream(Fixtures.Read("audio/tone16.wav"))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("Not an MP4"));
            Assert.That(() => Mp4.AudioTracks(new MemoryStream(Join(Start, Box("mdat", new byte[40])))), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no list of its tracks"));
        }
    }
}
