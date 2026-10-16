using System;
using System.Globalization;

namespace Strands.Sp16
{
    /// <summary>
    /// Where a pad plays from (format spec section 7). In 8ch mode that is one of the four stereo
    /// pairs, or one of the eight outputs on its own. The pairs come first, in the order the unit
    /// lists them. Generated projects on the pairs and on the single outputs have both played
    /// on the SP-16. What the unit does with the second side of a stereo sample on a single
    /// output is not known, so a sample for one is written as a mono file.
    /// The master output is where every pad of a scene goes while the scene is in the mode the
    /// unit starts in and the pad has not been sent anywhere else, which is how the template
    /// has all of them. It comes last, so that the others keep their places.
    /// </summary>
    public enum Sp16Output
    {
        Out12,
        Out34,
        Out56,
        Out78,
        Out1,
        Out2,
        Out3,
        Out4,
        Out5,
        Out6,
        Out7,
        Out8,
        Master
    }

    /// <summary>
    /// How a sample follows the unit's tempo, as the unit's Time Stretch setting names them: Off,
    /// M.TMP (the length follows the tempo and the pitch stays) and RESMPL (the sample is played
    /// faster or slower, pitch and all).
    /// </summary>
    public enum Sp16Stretch
    {
        Off,
        MasterTempo,
        Resample
    }

    /// <summary>
    /// Typed view over a project tree. It only patches fields the template already carries
    /// (format spec section 11): a project is never built from scratch, and everything this class
    /// does not touch is written back exactly as it was read.
    /// </summary>
    public sealed class Sp16Project
    {
        public const int Scenes = 16;
        public const int PadsPerScene = 16;
        public const int PatternsPerScene = 16;
        public const int OutputPairs = 4;
        public const int MaxSlices = 16;
        public const int MaxPatternLength = 64;
        public const int StepsPerBar = 16;
        public const double MaxSampleSeconds = 64.0;
        /// <summary>
        /// The slowest tempo the unit plays. A package of exactly half the test song's tempo, 57 BPM in
        /// the project and in every section, showed 57 in the arranger and played at 60 (Confirmed,
        /// SP-16, 2026-10-15, U13). The DJS-1000 has the same range (Reported, user), though its
        /// tempo slider can take it down to 0 BPM.
        /// </summary>
        public const double MinTempoBpm = 60;
        /// <summary>
        /// The fastest tempo the unit takes, on the SP-16 and the DJS-1000 (the user, 2026-10-15). On
        /// the DJS-1000 a project of the user's own at 260 BPM played at 220 (Confirmed, DJS-1000);
        /// its tempo slider then took it up to nearly 260, with the loops in time. On the SP-16 a
        /// package at 228 BPM (U17Double) played with the stutter expected at 220 (Confirmed, SP-16,
        /// 2026-10-16).
        /// </summary>
        public const double MaxTempoBpm = 220;
        /// <summary>
        /// Total sample memory, counted as bytes of 16-bit audio in the files. Measured on the SP-16
        /// with packages of 60 s stereo samples: 268,000,000 bytes load and 269,000,000 do not
        /// (Confirmed, SP-16). That it is exactly 256 x 2^20 is taken from that, not measured.
        /// Above it the unit says it is out of memory and leaves a sample out.
        /// </summary>
        public const long SampleMemoryBytes = 256L * 1024 * 1024;
        /// <summary>
        /// A megabyte as the unit counts it, so that its sample memory reads as the 256 MB its
        /// owners know it by. A file the Finder calls 13.4 MB is 12.8 of these.
        /// </summary>
        public const int BytesPerMegabyte = 1024 * 1024;
        public const int StereoBytesPerFrame = 4;
        /// <summary>
        /// A mono sample is counted at the size of its file, half that of a stereo one. Confirmed
        /// on the SP-16: 268,000,000 bytes of 60 s mono samples load, as the same of stereo do.
        /// </summary>
        public const int MonoBytesPerFrame = 2;

        const string UnitProjectFolder = "/mnt/emmc/PIONEER DJ SAMPLER/Projects/";
        const string PackageSampleFolder = "$Int0/%Tmp/";

        public readonly PrjNode Root;

        Sp16Project(PrjNode root)
        {
            if (root.Name != "ProjectData")
                throw new ArgumentException("Root struct is '" + root.Name + "', expected 'ProjectData'.");
            Root = root;
        }

        public static Sp16Project FromPrj(byte[] prjFile)
        {
            return new Sp16Project(PrjFile.Read(prjFile));
        }

        public byte[] ToPrj()
        {
            return PrjFile.Write(Root);
        }

        public double ProjectBpm
        {
            get { return Root.Scalar("projectBpm").AsFloat64(); }
            set { Root.Scalar("projectBpm").SetFloat64(value); }
        }

        public bool PlayArrangement
        {
            get { return Root.Scalar("bPlayArrangement").AsBool(); }
            set { Root.Scalar("bPlayArrangement").SetBool(value); }
        }

        public int SceneIndex
        {
            get { return Root.Scalar("sceneIndex").AsInt32(); }
            set { Root.Scalar("sceneIndex").SetInt32(value); }
        }

        public int PatternIndex
        {
            get { return Root.Scalar("patternIndex").AsInt32(); }
            set { Root.Scalar("patternIndex").SetInt32(value); }
        }

        public string ProjectFilePath
        {
            get { return Root.Scalar("projectFilePath").AsString(); }
            set { Root.Scalar("projectFilePath").SetString(value); }
        }

        /// <summary>Sets the path the way the SP-16 writes it for a project of this name.</summary>
        public void SetProjectName(string name)
        {
            ProjectFilePath = UnitProjectFolder + name + ".prj";
        }

        public PrjNode Scene(int scene)
        {
            if (scene < 0 || scene >= Scenes)
                throw new ArgumentOutOfRangeException("scene");
            return Root.Child("SceneData" + scene.ToString(CultureInfo.InvariantCulture));
        }

        public PrjNode Pad(int scene, int pad)
        {
            if (pad < 0 || pad >= PadsPerScene)
                throw new ArgumentOutOfRangeException("pad");
            return Scene(scene).Child("TrackData" + pad.ToString(CultureInfo.InvariantCulture));
        }

        public void SetSceneName(int scene, string name)
        {
            Scene(scene).Scalar("name").SetString(name);
        }

        /// <summary>
        /// Points a pad at a sample inside the package and makes it an independent one-shot:
        /// no loop, no time-stretch, choke group Off.
        /// </summary>
        public void SetPadSample(int scene, int pad, string wavFileName, int lengthFrames)
        {
            PrjNode track = Pad(scene, pad);
            // -1 is Off. The loader's 0 means group 1, where only one pad sounds at a time.
            track.Scalar("chokeGroup").SetInt32(-1);

            PrjNode playback = track.Child("PlaybackData");
            playback.Scalar("startSample").SetInt32(0);
            playback.Scalar("loopStartSample").SetInt32(0);
            playback.Scalar("lengthSample").SetInt32(lengthFrames);
            playback.Scalar("bLoop").SetBool(false);
            playback.Scalar("stretchMode").SetString("Off");
            playback.Scalar("audioSourceUrl").SetString(PackageSampleFolder + wavFileName);
        }

        /// <summary>
        /// Whether a pad's level follows how hard it is struck, which the unit keeps in its amp
        /// envelope (<c>bVelocityEffective</c>; Confirmed, file: <c>velocity.prj</c>, saved on the
        /// SP-16 with it on for tracks 1 and 2 of scene 1). Every template track has it off.
        /// </summary>
        public void SetPadVelocity(int scene, int pad, bool on)
        {
            Pad(scene, pad).Child("AmpEnvelopeData").Scalar("bVelocityEffective").SetBool(on);
        }

        /// <summary>The unit's choke groups, 1 to 8 on its screen and 0 to 7 in a file (Confirmed, hardware). -1 is Off.</summary>
        public const int ChokeGroups = 8;

        /// <summary>
        /// Puts a pad in a choke group, 0 to 7 for the unit's groups 1 to 8, or takes it out of them
        /// with -1. A pad started in a group cuts off whatever else of the group is sounding.
        /// </summary>
        public void SetPadChokeGroup(int scene, int pad, int group)
        {
            if (group < -1 || group >= ChokeGroups)
                throw new ArgumentOutOfRangeException("group");
            Pad(scene, pad).Scalar("chokeGroup").SetInt32(group);
        }

        /// <summary>
        /// The word a file has for a way of following the tempo, from a project saved on the unit
        /// with a track set to each (<c>stretch-modes.prj</c>; Confirmed, file).
        /// </summary>
        public static string StretchValue(Sp16Stretch stretch)
        {
            switch (stretch)
            {
                case Sp16Stretch.Off: return "Off";
                case Sp16Stretch.MasterTempo: return "MT";
                case Sp16Stretch.Resample: return "Resample";
                default: throw new ArgumentOutOfRangeException("stretch");
            }
        }

        /// <summary>
        /// Sets how a pad's sample follows the unit's tempo. Anything but Off is worked out from
        /// the tempo in the sample's <c>.dat</c>, which has to be the tempo the audio is at.
        /// </summary>
        public void SetPadStretch(int scene, int pad, Sp16Stretch stretch)
        {
            Pad(scene, pad).Child("PlaybackData").Scalar("stretchMode").SetString(StretchValue(stretch));
        }

        /// <summary>
        /// Makes a pad's sample go round from its start for as long as the pad sounds, or play
        /// once. Every unit-saved project to hand has it off, so that the unit loops a sample a
        /// file says to loop is Unverified.
        /// </summary>
        public void SetPadLoop(int scene, int pad, bool loop)
        {
            PrjNode playback = Pad(scene, pad).Child("PlaybackData");
            playback.Scalar("loopStartSample").SetInt32(0);
            playback.Scalar("bLoop").SetBool(loop);
        }

        /// <summary>Pads to a row of the unit's four by four grid.</summary>
        public const int PadsPerRow = 4;

        /// <summary>
        /// The pad at a place on the unit's grid counted as a page is read: place 0 is the top left
        /// pad, 3 the top right, 15 the bottom right. The unit numbers its tracks from the bottom
        /// row up, track 1 at the bottom left and 13 at the top left (the user's word, from the
        /// unit, 2026-10-12), so place 0 is pad 12 here. It is its own inverse: given a pad it
        /// gives the pad's place.
        /// </summary>
        public static int PadInReadingOrder(int place)
        {
            if (place < 0 || place >= PadsPerScene)
                throw new ArgumentOutOfRangeException("place");
            int rows = PadsPerScene / PadsPerRow;
            return (rows - 1 - place / PadsPerRow) * PadsPerRow + place % PadsPerRow;
        }

        /// <summary>How many track colours the unit has: every unit-saved project gives track N of a scene colour N - 1 (Confirmed, file).</summary>
        public const int TrackColours = 16;

        /// <summary>
        /// Gives a pad one of the unit's track colours, 0 to 15, as the unit numbers them from 1 on
        /// its screen. A unit-saved project has each track on its own colour; that the unit shows
        /// several tracks of a scene in one colour when a file says so is Unverified.
        /// </summary>
        public void SetPadColour(int scene, int pad, int colour)
        {
            if (colour < 0 || colour >= TrackColours)
                throw new ArgumentOutOfRangeException("colour");
            Pad(scene, pad).Scalar("colourIndex").SetInt32(colour);
        }

        /// <summary>
        /// The slice boundaries of a pad in frames: one more than the number of slices, running from
        /// 0 to the sample length. A pad whose slices were never edited stores none, and the unit
        /// then divides the sample into <c>nGrids</c> equal slices; those are returned here.
        /// </summary>
        public int[] GetSliceBoundaries(int scene, int pad)
        {
            PrjNode track = Pad(scene, pad);
            PrjNode slice = track.Child("SliceData");
            int[] stored = slice.Scalar("grids").AsInt32Array();
            if (stored.Length > 0)
                return stored;
            return EqualSliceBoundaries(track.Child("PlaybackData").Scalar("lengthSample").AsInt32(), slice.Scalar("nGrids").AsInt32());
        }

        /// <summary>
        /// Equal slices as the unit lays them out: boundary k is k times the length over the count,
        /// rounded to the nearest frame. Taken from one 81415-frame sample at 16 slices.
        /// </summary>
        public static int[] EqualSliceBoundaries(int lengthFrames, int slices)
        {
            if (slices < 1)
                throw new ArgumentOutOfRangeException("slices");
            var boundaries = new int[slices + 1];
            for (int i = 0; i <= slices; i++)
                boundaries[i] = (int)Math.Round((double)lengthFrames * i / slices, MidpointRounding.AwayFromZero);
            return boundaries;
        }

        /// <summary>
        /// Stores slice boundaries on a pad. They must start at 0, end at the pad's sample length and rise.
        /// </summary>
        public void SetSliceBoundaries(int scene, int pad, int[] boundaries)
        {
            PrjNode track = Pad(scene, pad);
            int length = track.Child("PlaybackData").Scalar("lengthSample").AsInt32();
            int slices = boundaries.Length - 1;
            // 8 and 16 are the only counts seen in unit-saved files, and 16 is the number of pads.
            // Whether the unit accepts any other count from a file is unconfirmed.
            if (slices < 1 || slices > MaxSlices)
                throw new ArgumentOutOfRangeException("boundaries", "A pad takes 1 to " + MaxSlices + " slices.");
            if (boundaries[0] != 0 || boundaries[slices] != length)
                throw new ArgumentException("Slice boundaries must run from 0 to the sample length (" + length + ").", "boundaries");
            for (int i = 1; i <= slices; i++)
                if (boundaries[i] <= boundaries[i - 1])
                    throw new ArgumentException("Slice boundaries must rise.", "boundaries");

            PrjNode slice = track.Child("SliceData");
            slice.Scalar("grids").SetInt32Array(boundaries);
            slice.Scalar("nGrids").SetInt32(slices);
        }

        public PrjNode FindPattern(int scene, int pattern)
        {
            return Scene(scene).FindChild(PatternName(pattern));
        }

        public void RemovePatterns(int scene)
        {
            Scene(scene).Children.RemoveAll(child => PatternNumber(child.Name) >= 0);
        }

        /// <summary>
        /// Stores a pattern as PatternData&lt;N&gt;, replacing any existing one. Patterns go after the 16 pads
        /// in ascending N and before SendFx, with scalars in the order the unit writes them.
        /// </summary>
        /// <param name="length">Total steps: 16 * (bars - 1) + beats in the last bar, at most 64.</param>
        public void SetPattern(int scene, int pattern, StepsBlob steps, int length)
        {
            if (pattern < 0 || pattern >= PatternsPerScene)
                throw new ArgumentOutOfRangeException("pattern");
            if (length < 1 || length > MaxPatternLength)
                throw new ArgumentOutOfRangeException("length");

            var node = new PrjNode(PatternName(pattern));
            node.AddScalar("Steps").SetBlob(steps.ToZlib());
            node.AddScalar("Length").SetInt32(length);
            node.AddScalar("TrackMutes").SetBoolArray(new bool[PadsPerScene]);
            node.AddScalar("TrackSolos").SetBoolArray(new bool[PadsPerScene]);

            PrjNode sceneNode = Scene(scene);
            sceneNode.Children.RemoveAll(child => child.Name == node.Name);
            int index = 0;
            while (index < sceneNode.Children.Count && ComesBeforePattern(sceneNode.Children[index].Name, pattern))
                index++;
            sceneNode.Children.Insert(index, node);
        }

        /// <summary>Sets a scene to 8ch mode. Mode and headphone cue change together on the unit.</summary>
        /// <param name="assignableOutCh">One value per pad, such as AssignTo12 or AssignTo34.</param>
        public void SetRouting8Ch(int scene, string[] assignableOutCh)
        {
            if (assignableOutCh.Length != PadsPerScene)
                throw new ArgumentException("One output assignment per pad is required.");
            PrjNode routing = Scene(scene).Child("RoutingData");
            routing.Scalar("assignMode").SetString("4Assign");
            routing.Scalar("headphoneOutCh").SetString("Cue12");
            for (int pad = 0; pad < PadsPerScene; pad++)
                routing.Child("TrackRoutingData" + pad.ToString(CultureInfo.InvariantCulture))
                    .Scalar("assignableOutCh").SetString(assignableOutCh[pad]);
        }

        /// <summary>
        /// What the unit stores in assignableOutCh for an output: AssignTo and the output's digits
        /// (Confirmed, file and SP-16, for all twelve). For the master output it is AssignToNull,
        /// which belongs to the mode the unit starts in and not to 8ch mode (Confirmed, file: it
        /// is what every pad of the empty project has). An export never writes that one: pads on
        /// master are left as the template has them.
        /// </summary>
        public static string OutputValue(Sp16Output output)
        {
            return output == Sp16Output.Master ? "AssignToNull" : "AssignTo" + OutputName(output).Replace("-", "");
        }

        /// <summary>A number of bytes in the megabytes every figure on screen is given in (<see cref="BytesPerMegabyte"/>).</summary>
        public static double Megabytes(long bytes)
        {
            return (double)bytes / BytesPerMegabyte;
        }

        /// <summary>An output as the unit names it on screen, such as 3-4 for a pair or 5 for one output; the master output is called master.</summary>
        public static string OutputName(Sp16Output output)
        {
            switch (output)
            {
                case Sp16Output.Out12: return "1-2";
                case Sp16Output.Out34: return "3-4";
                case Sp16Output.Out56: return "5-6";
                case Sp16Output.Out78: return "7-8";
                case Sp16Output.Out1: return "1";
                case Sp16Output.Out2: return "2";
                case Sp16Output.Out3: return "3";
                case Sp16Output.Out4: return "4";
                case Sp16Output.Out5: return "5";
                case Sp16Output.Out6: return "6";
                case Sp16Output.Out7: return "7";
                case Sp16Output.Out8: return "8";
                case Sp16Output.Master: return "master";
                default: throw new ArgumentOutOfRangeException("output");
            }
        }

        /// <summary>Whether an output is one of the eight on its own, and not a stereo pair.</summary>
        public static bool IsSingleOutput(Sp16Output output)
        {
            return output >= Sp16Output.Out1 && output <= Sp16Output.Out8;
        }

        public Arrangement GetArrangement()
        {
            return Arrangement.FromNode(Root.Child("ArrangementData0"));
        }

        public void SetArrangement(Arrangement arrangement)
        {
            arrangement.WriteTo(Root.Child("ArrangementData0"));
        }

        static string PatternName(int pattern)
        {
            return "PatternData" + pattern.ToString(CultureInfo.InvariantCulture);
        }

        static int PatternNumber(string childName)
        {
            const string prefix = "PatternData";
            int number;
            if (childName.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(childName.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                return number;
            return -1;
        }

        static bool ComesBeforePattern(string childName, int pattern)
        {
            if (childName.StartsWith("TrackData", StringComparison.Ordinal))
                return true;
            int number = PatternNumber(childName);
            return number >= 0 && number < pattern;
        }
    }
}
