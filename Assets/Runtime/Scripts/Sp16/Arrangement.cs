using System;
using System.IO;

namespace Strands.Sp16
{
    /// <summary>One arranger section (format spec section 6). Scene and pattern are 0-based.</summary>
    public struct Section
    {
        public int Scene;
        public int Pattern;
        /// <summary>Stored as the count itself, 1 to 32.</summary>
        public int Repeat;
        /// <summary>Byte 3, 0 in every file so far. Preserved, never interpreted.</summary>
        public byte Reserved;
        /// <summary>Tempo override; 0 means none. The override is sticky on the unit, so write one into every section.</summary>
        public float Bpm;

        public Section(int scene, int pattern, int repeat, float bpm)
        {
            Scene = scene;
            Pattern = pattern;
            Repeat = repeat;
            Reserved = 0;
            Bpm = bpm;
        }

        public static readonly Section Default = new Section(0, 0, 1, 0f);
    }

    /// <summary>
    /// ArrangementData0: the number of used sections and the Sections blob,
    /// 999 records of 8 bytes played in order from record 0.
    /// </summary>
    public sealed class Arrangement
    {
        public const int Capacity = 999;
        public const int RecordSize = 8;
        public const int ByteLength = Capacity * RecordSize;
        public const int MaxRepeat = 32;

        readonly byte[] data;

        public int Length { get; private set; }

        Arrangement(byte[] data, int length)
        {
            this.data = data;
            Length = length;
        }

        public static Arrangement CreateEmpty()
        {
            var arrangement = new Arrangement(new byte[ByteLength], 0);
            for (int i = 0; i < Capacity; i++)
                arrangement.Write(i, Section.Default);
            return arrangement;
        }

        public static Arrangement FromNode(PrjNode arrangementData)
        {
            PrjScalar sections = arrangementData.Scalar("Sections");
            byte[] inflated = Zlib.Decompress(sections.Raw, 1, sections.Raw.Length - 1);
            if (inflated.Length != ByteLength)
                throw new InvalidDataException("Sections blob is " + inflated.Length + " bytes, expected " + ByteLength + ".");
            return new Arrangement(inflated, arrangementData.Scalar("length").AsInt32());
        }

        public void WriteTo(PrjNode arrangementData)
        {
            arrangementData.Scalar("length").SetInt32(Length);
            arrangementData.Scalar("Sections").SetBlob(Zlib.Compress(data));
        }

        public byte[] ToInflated()
        {
            return (byte[])data.Clone();
        }

        public Section this[int index]
        {
            get { return Read(CheckIndex(index)); }
            set { Write(CheckIndex(index), value); }
        }

        public void Add(Section section)
        {
            Insert(Length, section);
        }

        /// <summary>Shifts the following records one slot later, as the unit does.</summary>
        public void Insert(int index, Section section)
        {
            if (index < 0 || index > Length)
                throw new ArgumentOutOfRangeException("index");
            if (Length >= Capacity)
                throw new InvalidOperationException("The arrangement already holds " + Capacity + " sections.");
            Buffer.BlockCopy(data, index * RecordSize, data, (index + 1) * RecordSize, (Length - index) * RecordSize);
            Length++;
            Write(index, section);
        }

        /// <summary>Shifts the following records earlier and resets the vacated last slot to the default.</summary>
        public void Delete(int index)
        {
            CheckIndex(index);
            Buffer.BlockCopy(data, (index + 1) * RecordSize, data, index * RecordSize, (Length - index - 1) * RecordSize);
            Length--;
            Write(Length, Section.Default);
        }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++)
                Write(i, Section.Default);
            Length = 0;
        }

        int CheckIndex(int index)
        {
            if (index < 0 || index >= Length)
                throw new ArgumentOutOfRangeException("index");
            return index;
        }

        Section Read(int index)
        {
            int o = index * RecordSize;
            return new Section
            {
                Scene = data[o],
                Pattern = data[o + 1],
                Repeat = data[o + 2],
                Reserved = data[o + 3],
                Bpm = BitConverter.ToSingle(data, o + 4)
            };
        }

        void Write(int index, Section section)
        {
            if (section.Scene < 0 || section.Scene > 15)
                throw new ArgumentOutOfRangeException("section", "Scene must be 0 to 15.");
            if (section.Pattern < 0 || section.Pattern > 15)
                throw new ArgumentOutOfRangeException("section", "Pattern must be 0 to 15.");
            if (section.Repeat < 1 || section.Repeat > MaxRepeat)
                throw new ArgumentOutOfRangeException("section", "Repeat must be 1 to " + MaxRepeat + ".");
            int o = index * RecordSize;
            data[o] = (byte)section.Scene;
            data[o + 1] = (byte)section.Pattern;
            data[o + 2] = (byte)section.Repeat;
            data[o + 3] = section.Reserved;
            byte[] bpm = BitConverter.GetBytes(section.Bpm);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bpm);
            Buffer.BlockCopy(bpm, 0, data, o + 4, 4);
        }
    }
}
