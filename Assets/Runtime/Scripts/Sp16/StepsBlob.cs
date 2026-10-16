using System;
using System.Collections.Generic;
using System.IO;

namespace Strands.Sp16
{
    /// <summary>
    /// A pattern's step data (format spec section 5): 1,024 records of 152 bytes,
    /// record index = pad * 64 + step, pad and step counted from 0.
    /// </summary>
    public sealed class StepsBlob
    {
        public const int Pads = 16;
        public const int StepsPerPad = 64;
        public const int RecordSize = 152;
        public const int RecordCount = Pads * StepsPerPad;
        public const int ByteLength = RecordCount * RecordSize;

        const int UnusedPad = -127;
        const int UnusedStep = -126;

        static readonly byte[] DefaultRecord = BuildDefaultRecord();

        readonly byte[] data;

        StepsBlob(byte[] data)
        {
            this.data = data;
        }

        /// <summary>All 1,024 records unused, as the unit writes them.</summary>
        public static StepsBlob CreateEmpty()
        {
            var data = new byte[ByteLength];
            for (int i = 0; i < RecordCount; i++)
                Buffer.BlockCopy(DefaultRecord, 0, data, i * RecordSize, RecordSize);
            return new StepsBlob(data);
        }

        public static StepsBlob FromInflated(byte[] inflated)
        {
            if (inflated.Length != ByteLength)
                throw new InvalidDataException("Steps blob is " + inflated.Length + " bytes, expected " + ByteLength + ".");
            return new StepsBlob((byte[])inflated.Clone());
        }

        public static StepsBlob FromScalar(PrjScalar scalar)
        {
            return FromInflated(Zlib.Decompress(scalar.Raw, 1, scalar.Raw.Length - 1));
        }

        public byte[] ToInflated()
        {
            return (byte[])data.Clone();
        }

        public byte[] ToZlib()
        {
            return Zlib.Compress(data);
        }

        /// <summary>Places a trigger by writing pad and step into the record; every other byte keeps its default.</summary>
        public void SetTrigger(int pad, int step)
        {
            int offset = RecordOffset(pad, step);
            WriteInt32(offset, pad);
            WriteInt32(offset + 4, step);
        }

        public void ClearTrigger(int pad, int step)
        {
            Buffer.BlockCopy(DefaultRecord, 0, data, RecordOffset(pad, step), RecordSize);
        }

        public bool HasTrigger(int pad, int step)
        {
            return ReadInt32(RecordOffset(pad, step)) != UnusedPad;
        }

        /// <summary>Pad and step of every used record, in record order.</summary>
        public List<KeyValuePair<int, int>> GetTriggers()
        {
            var triggers = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < RecordCount; i++)
                if (ReadInt32(i * RecordSize) != UnusedPad)
                    triggers.Add(new KeyValuePair<int, int>(i / StepsPerPad, i % StepsPerPad));
            return triggers;
        }

        static int RecordOffset(int pad, int step)
        {
            if (pad < 0 || pad >= Pads)
                throw new ArgumentOutOfRangeException("pad");
            if (step < 0 || step >= StepsPerPad)
                throw new ArgumentOutOfRangeException("step");
            return (pad * StepsPerPad + step) * RecordSize;
        }

        /// <summary>
        /// pad -127, step -126, constant 15, then 35 parameter entries (id, 00, 80, 3f):
        /// ids 0 to 22, then 5 to 10 twice. The meaning of the entries other than id 13 is unverified.
        /// </summary>
        static byte[] BuildDefaultRecord()
        {
            var record = new byte[RecordSize];
            WriteInt32(record, 0, UnusedPad);
            WriteInt32(record, 4, UnusedStep);
            WriteInt32(record, 8, 15);
            int offset = 12;
            for (int id = 0; id <= 22; id++)
                offset = WriteEntry(record, offset, id);
            for (int pass = 0; pass < 2; pass++)
                for (int id = 5; id <= 10; id++)
                    offset = WriteEntry(record, offset, id);
            return record;
        }

        static int WriteEntry(byte[] record, int offset, int id)
        {
            record[offset] = (byte)id;
            record[offset + 1] = 0x00;
            record[offset + 2] = 0x80;
            record[offset + 3] = 0x3F;
            return offset + 4;
        }

        int ReadInt32(int offset)
        {
            return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
        }

        void WriteInt32(int offset, int value)
        {
            WriteInt32(data, offset, value);
        }

        static void WriteInt32(byte[] target, int offset, int value)
        {
            target[offset] = (byte)value;
            target[offset + 1] = (byte)(value >> 8);
            target[offset + 2] = (byte)(value >> 16);
            target[offset + 3] = (byte)(value >> 24);
        }
    }
}
