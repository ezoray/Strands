using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Strands.Audio
{
    /// <summary>What a stem file says it holds.</summary>
    public sealed class StemFileInfo
    {
        /// <summary>How many audio tracks there are: the full mix first, then one for each stem.</summary>
        public int AudioTracks;
        /// <summary>A name for each stem in the order of their tracks, from the file's own list, or "Stem 3" where it gives none.</summary>
        public readonly List<string> StemNames = new List<string>();
    }

    /// <summary>One stem read from a stem file.</summary>
    public sealed class StemFileTrack
    {
        public string Name;
        public WavData Audio;
    }

    /// <summary>
    /// Reads a stem file: Native Instruments' Stems format, which Traktor writes for a track it has
    /// split (<c>.stem.mp4</c>). It is an MP4 file whose first audio track is the full mix and whose
    /// other audio tracks are the stems, with a list of the stems' names in a box of its own
    /// (<c>moov/udta/stem</c>, as JSON). The file's lists are read by <see cref="Mp4"/>.
    /// The audio is AAC, which is what Traktor writes and is decoded here on every system
    /// (<see cref="AacReader"/>), or Apple Lossless, which is left to macOS
    /// (<see cref="AudioToolboxReader"/>) and so can only be read on a Mac.
    /// The file also says how Traktor is to compress and limit the stems once they are added
    /// together again. That is not used: the stems are taken as they are.
    /// </summary>
    public static class StemFile
    {
        /// <summary>The extensions a stem file can have, lower case and without the dot. The full ending is .stem.mp4.</summary>
        public static readonly string[] Extensions = { "mp4", "m4a" };

        const int MaxJsonDepth = 32;

        /// <summary>Whether stems coded as Apple Lossless can be decoded on this system. Stems coded as AAC can be on every one.</summary>
        public static bool CanReadLossless { get { return AudioToolboxReader.Available; } }

        public static bool HasStemExtension(string path)
        {
            return Array.IndexOf(Extensions, Path.GetExtension(path).TrimStart('.').ToLowerInvariant()) >= 0;
        }

        /// <summary>
        /// Whether an MP4 is a stem file: it has a list of stems and the tracks to go with it. One
        /// that has not is a song by itself, which <see cref="Mp4Song"/> reads.
        /// </summary>
        public static bool IsStemFile(string path)
        {
            try
            {
                ReadInfo(path);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        /// <summary>The name of the song a stem file holds: its file name without .stem.mp4.</summary>
        public static string SongName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            const string marker = ".stem";
            return name.EndsWith(marker, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - marker.Length) : name;
        }

        public static StemFileInfo ReadInfo(string path)
        {
            using (FileStream stream = File.OpenRead(path))
                return ReadInfo(stream);
        }

        /// <summary>Finds how many audio tracks the file has and what its stems are called, without decoding anything.</summary>
        public static StemFileInfo ReadInfo(Stream stream)
        {
            byte[] index = Mp4.ReadIndex(stream);
            var info = new StemFileInfo();
            string list = null;
            foreach (Mp4.Box box in Mp4.Children(index, 0, index.Length))
            {
                if (box.Type == "trak")
                {
                    if (Mp4.IsAudio(index, box))
                        info.AudioTracks++;
                }
                else if (box.Type == "udta")
                {
                    Mp4.Box stems;
                    if (Mp4.Find(index, box, "stem", out stems))
                        list = Encoding.UTF8.GetString(index, stems.Start, stems.End - stems.Start).Trim('\0', ' ', '\t', '\r', '\n');
                }
            }
            if (list == null)
                throw new InvalidDataException("This MP4 is not a stem file: it has no list of stems.");
            if (info.AudioTracks < 2)
                throw new InvalidDataException("This MP4 is not a stem file: it holds " + info.AudioTracks.ToString(CultureInfo.InvariantCulture)
                    + " audio track" + (info.AudioTracks == 1 ? "" : "s") + ", where a stem file holds the full mix and then each stem.");

            var named = new List<object>();
            var root = Json.Parse(list) as Dictionary<string, object>;
            object found;
            if (root != null && root.TryGetValue("stems", out found) && found is List<object>)
                named = (List<object>)found;
            for (int i = 0; i < info.AudioTracks - 1; i++)
            {
                string name = null;
                var entry = i < named.Count ? named[i] as Dictionary<string, object> : null;
                if (entry != null && entry.TryGetValue("name", out found))
                    name = found as string;
                info.StemNames.Add(string.IsNullOrWhiteSpace(name) ? "Stem " + (i + 1).ToString(CultureInfo.InvariantCulture) : name.Trim());
            }
            return info;
        }

        /// <summary>
        /// Reads every stem, leaving out the full mix that comes first, and brings each to 44.1 kHz
        /// when the file is at another rate. Stems coded as AAC are decoded side by side, each from
        /// a stream of its own. Stems coded as Apple Lossless need a Mac: see <see cref="CanReadLossless"/>.
        /// </summary>
        public static List<StemFileTrack> Read(string path, IProgress<float> progress = null, CancellationToken cancel = default(CancellationToken))
        {
            StemFileInfo info = ReadInfo(path);
            List<Mp4AudioTrack> tracks;
            using (FileStream stream = File.OpenRead(path))
                tracks = Mp4.AudioTracks(stream);
            int count = info.StemNames.Count;
            var audio = new WavData[count];
            var shares = progress == null ? null : new Shares(progress, count);

            // Track 0 is the full mix, so stem i is track i + 1.
            try
            {
                Parallel.For(0, count, new ParallelOptions { CancellationToken = cancel }, i =>
                {
                    if (!AacReader.CanDecode(tracks[i + 1]))
                        return;
                    using (FileStream own = File.OpenRead(path))
                        audio[i] = AacReader.Read(own, tracks[i + 1], shares == null ? null : shares.Part(i), cancel);
                });
            }
            catch (AggregateException e)
            {
                // What went wrong with a stem is the error, not that it went wrong beside the others.
                ExceptionDispatchInfo.Capture(e.InnerExceptions[0]).Throw();
            }
            for (int i = 0; i < count; i++)
            {
                if (audio[i] != null)
                    continue;
                if (!CanReadLossless)
                    throw new NotSupportedException("The stems of this file are coded as " + Coding(tracks[i + 1]) + ", which can only be read on a Mac for now: that is left to macOS to decode. Stems coded as AAC can be read here.");
                audio[i] = AudioToolboxReader.ReadTrack(path, i + 1, shares == null ? null : shares.Part(i), cancel);
            }

            var stems = new List<StemFileTrack>();
            for (int i = 0; i < count; i++)
                stems.Add(new StemFileTrack { Name = info.StemNames[i], Audio = Resampler.ToTargetRate(audio[i], null, cancel) });
            return stems;
        }

        static string Coding(Mp4AudioTrack track)
        {
            return track.Coding == "alac" ? "Apple Lossless" : string.IsNullOrEmpty(track.Coding) ? "something this app does not know" : "\"" + track.Coding + "\"";
        }

        /// <summary>
        /// Progress through several stems that may be read at the same time, as progress through
        /// them all. What is reported never goes back, whichever stem reports last.
        /// </summary>
        sealed class Shares
        {
            readonly IProgress<float> whole;
            readonly float[] parts;
            readonly object gate = new object();

            public Shares(IProgress<float> whole, int count)
            {
                this.whole = whole;
                parts = new float[count];
            }

            public IProgress<float> Part(int index)
            {
                return new Share(this, index);
            }

            void Set(int index, float value)
            {
                lock (gate)
                {
                    parts[index] = Math.Max(parts[index], Math.Max(0f, Math.Min(1f, value)));
                    float sum = 0;
                    foreach (float part in parts)
                        sum += part;
                    whole.Report(sum / parts.Length);
                }
            }

            sealed class Share : IProgress<float>
            {
                readonly Shares owner;
                readonly int index;

                public Share(Shares owner, int index)
                {
                    this.owner = owner;
                    this.index = index;
                }

                public void Report(float value)
                {
                    owner.Set(index, value);
                }
            }
        }

        /// <summary>
        /// Just enough of a JSON reader for the list of stems: objects as dictionaries, arrays as
        /// lists, strings, numbers as doubles, true, false and null.
        /// </summary>
        sealed class Json
        {
            readonly string text;
            int at;

            Json(string text)
            {
                this.text = text;
            }

            public static object Parse(string text)
            {
                var reader = new Json(text);
                object value = reader.Value(0);
                reader.SkipSpace();
                if (reader.at != text.Length)
                    throw Bad();
                return value;
            }

            static InvalidDataException Bad()
            {
                return new InvalidDataException("The stem file's list of stems cannot be read.");
            }

            void SkipSpace()
            {
                while (at < text.Length && (text[at] == ' ' || text[at] == '\t' || text[at] == '\r' || text[at] == '\n'))
                    at++;
            }

            char Next()
            {
                SkipSpace();
                if (at >= text.Length)
                    throw Bad();
                return text[at];
            }

            bool Take(string word)
            {
                if (string.CompareOrdinal(text, at, word, 0, word.Length) != 0)
                    return false;
                at += word.Length;
                return true;
            }

            object Value(int depth)
            {
                if (depth > MaxJsonDepth)
                    throw Bad();
                char c = Next();
                if (c == '{')
                {
                    at++;
                    var members = new Dictionary<string, object>();
                    if (Next() == '}')
                    {
                        at++;
                        return members;
                    }
                    while (true)
                    {
                        if (Next() != '"')
                            throw Bad();
                        string key = String();
                        if (Next() != ':')
                            throw Bad();
                        at++;
                        members[key] = Value(depth + 1);
                        char after = Next();
                        at++;
                        if (after == '}')
                            return members;
                        if (after != ',')
                            throw Bad();
                    }
                }
                if (c == '[')
                {
                    at++;
                    var items = new List<object>();
                    if (Next() == ']')
                    {
                        at++;
                        return items;
                    }
                    while (true)
                    {
                        items.Add(Value(depth + 1));
                        char after = Next();
                        at++;
                        if (after == ']')
                            return items;
                        if (after != ',')
                            throw Bad();
                    }
                }
                if (c == '"')
                    return String();
                if (Take("true"))
                    return true;
                if (Take("false"))
                    return false;
                if (Take("null"))
                    return null;

                int start = at;
                while (at < text.Length && "+-0123456789.eE".IndexOf(text[at]) >= 0)
                    at++;
                double number;
                if (at == start || !double.TryParse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw Bad();
                return number;
            }

            string String()
            {
                var value = new StringBuilder();
                at++;
                while (true)
                {
                    if (at >= text.Length)
                        throw Bad();
                    char c = text[at++];
                    if (c == '"')
                        return value.ToString();
                    if (c != '\\')
                    {
                        value.Append(c);
                        continue;
                    }
                    if (at >= text.Length)
                        throw Bad();
                    char escaped = text[at++];
                    switch (escaped)
                    {
                        case 'n': value.Append('\n'); break;
                        case 't': value.Append('\t'); break;
                        case 'r': value.Append('\r'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'u':
                            int code;
                            if (at + 4 > text.Length || !int.TryParse(text.Substring(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                                throw Bad();
                            value.Append((char)code);
                            at += 4;
                            break;
                        default: value.Append(escaped); break;
                    }
                }
            }
        }
    }
}
