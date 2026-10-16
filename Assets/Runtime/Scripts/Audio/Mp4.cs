using System;
using System.Collections.Generic;
using System.IO;

namespace Strands.Audio
{
    /// <summary>
    /// One audio track of an MP4 file: how it is coded, where each of its packets lies in the file,
    /// and which stretch of what they decode to is the sound itself.
    /// </summary>
    public sealed class Mp4AudioTrack
    {
        /// <summary>The four letters the file gives the coding: mp4a for AAC and its relations, alac for Apple Lossless.</summary>
        public string Coding;
        /// <summary>
        /// What a decoder is to be set up with, from the track's esds box, or null when it has none.
        /// For AAC this is the AudioSpecificConfig.
        /// </summary>
        public byte[] DecoderSettings;
        /// <summary>The kind of audio the esds box says the track holds: 0x40 for MPEG-4 audio, which AAC is. 0 when there is no esds box.</summary>
        public int ObjectType;

        // Units of the track's own clock, and of the file's, in a second.
        internal long TimeScale, MovieTimeScale;
        // The first edit that plays anything: where it starts, on the track's clock, and how long it
        // lasts, on the file's. An encoder puts frames of its own in front of the sound and fills out
        // the last packet behind it, and the edit is how the file says where the sound really is.
        internal bool HasEdit;
        internal long EditStart, EditLength;
        // True when the length came from a note among the file's tags and is on the track's clock.
        internal bool EditLengthOnTrackClock;
        internal long[] Offsets;
        internal int[] Sizes;

        public int PacketCount { get { return Sizes.Length; } }

        /// <summary>The bytes of one packet, counted from 0.</summary>
        public byte[] ReadPacket(Stream stream, int index)
        {
            var packet = new byte[Sizes[index]];
            stream.Position = Offsets[index];
            Mp4.Fill(stream, packet, 0, packet.Length);
            return packet;
        }

        /// <summary>
        /// Which of the decoded frames are the sound, at the rate they were decoded at: the first
        /// of them and how many. Without an edit that is everything from the first frame on, and
        /// the count is -1.
        /// </summary>
        public void Sound(int sampleRate, out long firstFrame, out long frames)
        {
            firstFrame = 0;
            frames = -1;
            if (!HasEdit || TimeScale <= 0 || MovieTimeScale <= 0)
                return;
            firstFrame = (EditStart * sampleRate + TimeScale / 2) / TimeScale;
            long clock = EditLengthOnTrackClock ? TimeScale : MovieTimeScale;
            frames = (EditLength * sampleRate + clock / 2) / clock;
        }
    }

    /// <summary>
    /// Reading an MP4 file's own lists: its boxes, and from them the audio tracks and their
    /// packets. A box is a 4-byte length that counts its own header, 4 letters, then the contents.
    /// Only the moov box, which holds the lists, is ever read whole; the audio is read a packet
    /// at a time from where the lists say it is.
    /// </summary>
    public static class Mp4
    {
        // The boxes that list a file's tracks are small; a figure far beyond that is not a size.
        const long MaxIndexBytes = 256L * 1024 * 1024;
        const string Damaged = "The MP4 file is damaged: ";

        internal struct Box
        {
            public string Type;
            /// <summary>Where the contents begin and end.</summary>
            public int Start, End;
        }

        /// <summary>The audio tracks of a file, in the order the file has them.</summary>
        public static List<Mp4AudioTrack> AudioTracks(Stream stream)
        {
            byte[] index = ReadIndex(stream);
            long fileLength = stream.Length;
            long movieTimeScale = 0;
            var tracks = new List<Mp4AudioTrack>();
            foreach (Box box in Children(index, 0, index.Length))
            {
                if (box.Type == "mvhd")
                    movieTimeScale = TimeScale(index, box);
                else if (box.Type == "trak" && IsAudio(index, box))
                    tracks.Add(ReadTrack(index, box, fileLength));
            }
            foreach (Mp4AudioTrack track in tracks)
                track.MovieTimeScale = movieTimeScale;
            // The note is about the file as a whole, so it is only taken for a file with one sound in it.
            if (tracks.Count == 1 && !tracks[0].HasEdit)
                ReadEncoderNote(index, tracks[0]);
            return tracks;
        }

        /// <summary>A track says what it holds in mdia/hdlr: four letters after a version and a reserved word.</summary>
        internal static bool IsAudio(byte[] index, Box track)
        {
            Box media, handler;
            return Find(index, track, "mdia", out media) && Find(index, media, "hdlr", out handler)
                && handler.End - handler.Start >= 12 && Tag(index, handler.Start + 8) == "soun";
        }

        static Mp4AudioTrack ReadTrack(byte[] index, Box trak, long fileLength)
        {
            var track = new Mp4AudioTrack { Coding = "", Offsets = new long[0], Sizes = new int[0] };
            Box media, header, information, table;
            if (!Find(index, trak, "mdia", out media))
                return track;
            if (Find(index, media, "mdhd", out header))
                track.TimeScale = TimeScale(index, header);
            ReadEdit(index, trak, track);
            if (!Find(index, media, "minf", out information) || !Find(index, information, "stbl", out table))
                return track;

            Box description, sizes, runs, offsets;
            if (Find(index, table, "stsd", out description))
                ReadDescription(index, description, track);
            bool wide = false;
            if (!Find(index, table, "stsz", out sizes) || !Find(index, table, "stsc", out runs)
                || !(Find(index, table, "stco", out offsets) || (wide = Find(index, table, "co64", out offsets))))
                return track;
            ReadPackets(index, sizes, runs, offsets, wide, track, fileLength);
            return track;
        }

        /// <summary>The clock of a movie or media header: after the version, two dates of 4 bytes each, or of 8 in version 1.</summary>
        static long TimeScale(byte[] index, Box header)
        {
            Need(header, 4);
            int at = header.Start + (index[header.Start] == 1 ? 20 : 12);
            Need(header, at - header.Start + 4);
            return Number(index, at, 4);
        }

        /// <summary>
        /// What iTunes and Apple's own encoder write where others write an edit list: a note among
        /// the file's tags, called iTunSMPB, of how many frames the encoder put in front of the
        /// sound and how many frames the sound has. It is text, in hexadecimal: a zero, the frames
        /// in front, the filling behind, and the length. Without it an .m4a from those encoders
        /// comes out 2,112 frames late and a little long.
        /// </summary>
        static void ReadEncoderNote(byte[] index, Mp4AudioTrack track)
        {
            int name = IndexOf(index, "iTunSMPB", 0, index.Length);
            if (name < 0)
                return;
            // The note's text is in a box of its own just after its name: a length, "data", eight bytes that say what kind of data, then the text.
            int data = IndexOf(index, "data", name, Math.Min(index.Length, name + 64));
            if (data < 4)
                return;
            long size = Number(index, data - 4, 4);
            int start = data + 12, end = (int)Math.Min(index.Length, data - 4 + size);
            if (end <= start)
                return;
            string[] fields = System.Text.Encoding.ASCII.GetString(index, start, end - start).Split(new[] { ' ', '\0' }, StringSplitOptions.RemoveEmptyEntries);
            long inFront, length;
            if (fields.Length < 4
                || !long.TryParse(fields[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out inFront)
                || !long.TryParse(fields[3], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out length)
                || length <= 0)
                return;
            track.HasEdit = true;
            track.EditStart = inFront;
            track.EditLength = length;
            track.EditLengthOnTrackClock = true;
        }

        static int IndexOf(byte[] data, string text, int from, int to)
        {
            for (int at = from; at + text.Length <= to; at++)
            {
                int same = 0;
                while (same < text.Length && data[at + same] == text[same])
                    same++;
                if (same == text.Length)
                    return at;
            }
            return -1;
        }

        static void ReadEdit(byte[] index, Box trak, Mp4AudioTrack track)
        {
            Box edits, list;
            if (!Find(index, trak, "edts", out edits) || !Find(index, edits, "elst", out list))
                return;
            Need(list, 8);
            bool wide = index[list.Start] == 1;
            long count = Number(index, list.Start + 4, 4);
            int entry = wide ? 20 : 12;
            for (int i = 0; i < count; i++)
            {
                int at = list.Start + 8 + i * entry;
                Need(list, at - list.Start + entry);
                long length = Number(index, at, wide ? 8 : 4);
                long start = wide ? Signed(index, at + 8, 8) : Signed(index, at + 4, 4);
                // A start of -1 is a stretch of nothing in front of the track; the edit after it is the one that plays.
                if (start < 0)
                    continue;
                track.HasEdit = true;
                track.EditStart = start;
                track.EditLength = length;
                return;
            }
        }

        /// <summary>
        /// The description of the track's sound: its coding, and inside it, for the MPEG-4 codings,
        /// an esds box with what the decoder needs.
        /// </summary>
        static void ReadDescription(byte[] index, Box description, Mp4AudioTrack track)
        {
            // A version, a count of entries, then the first entry: its length, its four letters,
            // 8 bytes that are not needed, and the fields every sound has.
            Need(description, 16);
            int entry = description.Start + 8;
            long length = Number(index, entry, 4);
            if (length < 16 || entry + length > description.End)
                throw new InvalidDataException(Damaged + "the description of a track's sound runs past its end.");
            track.Coding = Tag(index, entry + 4);
            int end = entry + (int)length;
            if (end - entry < 36)
                return;
            // QuickTime's later versions of those fields are longer.
            long version = Number(index, entry + 16, 2);
            int inside = entry + 36 + (version == 1 ? 16 : version == 2 ? 36 : 0);
            if (inside > end)
                return;
            foreach (Box box in Children(index, inside, end))
            {
                Box found = box;
                // QuickTime puts it one box further in.
                if (box.Type == "wave" && !Find(index, box, "esds", out found))
                    continue;
                if (found.Type == "esds")
                {
                    ReadElementaryStream(index, found, track);
                    return;
                }
            }
        }

        /// <summary>
        /// An esds box is descriptors inside descriptors, each a tag, a length and its contents: the
        /// stream (3) holds the decoder's configuration (4), which says what kind of audio it is and
        /// holds the settings themselves (5).
        /// </summary>
        static void ReadElementaryStream(byte[] index, Box box, Mp4AudioTrack track)
        {
            int at = box.Start + 4, end = box.End;
            int length;
            if (!Descriptor(index, ref at, end, 3, out length))
                return;
            end = Math.Min(end, at + length);
            if (at + 3 > end)
                return;
            int flags = index[at + 2];
            at += 3;
            if ((flags & 0x80) != 0)
                at += 2;
            if ((flags & 0x40) != 0 && at < end)
                at += 1 + index[at];
            if ((flags & 0x20) != 0)
                at += 2;
            if (!Descriptor(index, ref at, end, 4, out length) || at + 13 > end)
                return;
            int configurationEnd = Math.Min(end, at + length);
            track.ObjectType = index[at];
            at += 13;
            if (!Descriptor(index, ref at, configurationEnd, 5, out length) || at + length > configurationEnd)
                return;
            track.DecoderSettings = new byte[length];
            Array.Copy(index, at, track.DecoderSettings, 0, length);
        }

        /// <summary>Steps over a descriptor's tag and length. The length is 7 bits to a byte, with the top bit saying there is another.</summary>
        static bool Descriptor(byte[] index, ref int at, int end, int tag, out int length)
        {
            length = 0;
            if (at >= end || index[at] != tag)
                return false;
            at++;
            for (int i = 0; i < 4 && at < end; i++)
            {
                int part = index[at++];
                length = length << 7 | (part & 0x7F);
                if ((part & 0x80) == 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Where each packet is. The file gives the size of every packet (stsz), where each chunk of
        /// packets starts (stco, or co64 for a long file) and how many packets the chunks have, as
        /// runs of chunks that have the same number (stsc). The packets of a chunk follow one another.
        /// </summary>
        static void ReadPackets(byte[] index, Box sizes, Box runs, Box offsets, bool wide, Mp4AudioTrack track, long fileLength)
        {
            Need(sizes, 12);
            long fixedSize = Number(index, sizes.Start + 4, 4);
            long count = Number(index, sizes.Start + 8, 4);
            if (count > int.MaxValue / 16 || (fixedSize == 0 && sizes.End - sizes.Start - 12 < count * 4))
                throw new InvalidDataException(Damaged + "the list of a track's packets runs past its end.");
            track.Sizes = new int[count];
            track.Offsets = new long[count];
            for (int i = 0; i < count; i++)
            {
                long size = fixedSize != 0 ? fixedSize : Number(index, sizes.Start + 12 + 4 * i, 4);
                if (size > int.MaxValue / 2)
                    throw new InvalidDataException(Damaged + "a packet cannot be that long.");
                track.Sizes[i] = (int)size;
            }

            Need(offsets, 8);
            long chunks = Number(index, offsets.Start + 4, 4);
            int offsetBytes = wide ? 8 : 4;
            if (offsets.End - offsets.Start - 8 < chunks * offsetBytes)
                throw new InvalidDataException(Damaged + "the list of where a track's audio lies runs past its end.");
            Need(runs, 8);
            long runCount = Number(index, runs.Start + 4, 4);
            if (runs.End - runs.Start - 8 < runCount * 12)
                throw new InvalidDataException(Damaged + "the list of a track's chunks runs past its end.");

            int packet = 0;
            for (int run = 0; run < runCount && packet < count; run++)
            {
                int at = runs.Start + 8 + 12 * run;
                // Chunks are counted from 1. A run goes on to the chunk the next run starts at, and the last to the end.
                long first = Number(index, at, 4), each = Number(index, at + 4, 4);
                long next = run + 1 < runCount ? Number(index, at + 12, 4) : chunks + 1;
                if (first < 1 || next < first || next > chunks + 1)
                    throw new InvalidDataException(Damaged + "the list of a track's chunks is out of order.");
                for (long chunk = first; chunk < next && packet < count; chunk++)
                {
                    long position = Number(index, offsets.Start + 8 + (int)(chunk - 1) * offsetBytes, offsetBytes);
                    for (long i = 0; i < each && packet < count; i++, packet++)
                    {
                        if (position < 0 || position + track.Sizes[packet] > fileLength)
                            throw new InvalidDataException(Damaged + "a track's audio runs past the end of the file, so the file is cut short.");
                        track.Offsets[packet] = position;
                        position += track.Sizes[packet];
                    }
                }
            }
            if (packet < count)
                throw new InvalidDataException(Damaged + "it does not say where all of a track's audio lies.");
        }

        static void Need(Box box, int bytes)
        {
            if (box.End - box.Start < bytes)
                throw new InvalidDataException(Damaged + "a " + box.Type + " box is too short.");
        }

        /// <summary>The contents of the moov box, which lists the tracks. The audio itself, in mdat, is skipped over.</summary>
        internal static byte[] ReadIndex(Stream stream)
        {
            var header = new byte[16];
            long position = 0, length = stream.Length;
            bool first = true;
            while (position + 8 <= length)
            {
                stream.Position = position;
                Fill(stream, header, 0, 8);
                long size = Number(header, 0, 4);
                string type = Tag(header, 4);
                int headerBytes = 8;
                if (size == 1)
                {
                    // A box too long for four bytes gives its length in eight more.
                    if (position + 16 > length)
                        throw new InvalidDataException("The MP4 file is damaged or cut short.");
                    Fill(stream, header, 8, 8);
                    size = Number(header, 8, 8);
                    headerBytes = 16;
                }
                else if (size == 0)
                {
                    size = length - position;
                }
                if (first && type != "ftyp")
                    break;
                first = false;
                if (size < headerBytes || size > length - position)
                    throw new InvalidDataException("The MP4 file is damaged or cut short.");
                if (type == "moov")
                {
                    if (size - headerBytes > MaxIndexBytes)
                        throw new InvalidDataException("The MP4 file is damaged: the list of its tracks cannot be that long.");
                    var index = new byte[size - headerBytes];
                    Fill(stream, index, 0, index.Length);
                    return index;
                }
                position += size;
            }
            if (first)
                throw new InvalidDataException("Not an MP4 file.");
            throw new InvalidDataException("The MP4 file has no list of its tracks, so it is damaged or cut short.");
        }

        internal static IEnumerable<Box> Children(byte[] data, int start, int end)
        {
            int position = start;
            while (position + 8 <= end)
            {
                long size = Number(data, position, 4);
                int headerBytes = 8;
                if (size == 1 && position + 16 <= end)
                {
                    size = Number(data, position + 8, 8);
                    headerBytes = 16;
                }
                else if (size == 0)
                {
                    size = end - position;
                }
                if (size < headerBytes || size > end - position)
                    throw new InvalidDataException("The MP4 file is damaged: a box in its list of tracks runs past the end.");
                yield return new Box { Type = Tag(data, position + 4), Start = position + headerBytes, End = position + (int)size };
                position += (int)size;
            }
        }

        internal static bool Find(byte[] data, Box parent, string type, out Box found)
        {
            foreach (Box box in Children(data, parent.Start, parent.End))
            {
                if (box.Type != type)
                    continue;
                found = box;
                return true;
            }
            found = default(Box);
            return false;
        }

        internal static long Number(byte[] data, int at, int bytes)
        {
            long value = 0;
            for (int i = 0; i < bytes; i++)
                value = value << 8 | data[at + i];
            // Eight bytes with the top bit set would be negative: not a length any file has.
            return value < 0 ? long.MaxValue : value;
        }

        /// <summary>A number that can be negative, as the start of an edit can.</summary>
        static long Signed(byte[] data, int at, int bytes)
        {
            long value = 0;
            for (int i = 0; i < bytes; i++)
                value = value << 8 | data[at + i];
            return bytes == 4 ? (int)value : value;
        }

        internal static string Tag(byte[] data, int at)
        {
            var letters = new char[4];
            for (int i = 0; i < 4; i++)
                letters[i] = (char)data[at + i];
            return new string(letters);
        }

        internal static void Fill(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0)
                    throw new InvalidDataException("The MP4 file is damaged or cut short.");
                offset += read;
                count -= read;
            }
        }
    }
}
