using System;
using System.Collections.Generic;
using System.IO;

namespace Strands.Sp16
{
    /// <summary>
    /// What a trigger plays (Confirmed, file: <c>steps-1.prj</c>, saved on the DJS-1000). Full restarts
    /// the sample, its envelope and the step's settings; Half the envelope and settings without the
    /// sample; Param the settings alone. Stored as flags: Half is Full without the 4.
    /// </summary>
    public enum StepTrigger
    {
        Param = 8,
        Half = 11,
        Full = 15,
    }

    /// <summary>
    /// The settings a step can hold, by their entry's id, which is also its place among the
    /// record's first 23 entries (Confirmed, file: <c>steps-1.prj</c>, one setting on each step
    /// with the screen value noted). The others among the first 23 are not known.
    /// </summary>
    public enum StepParameter
    {
        /// <summary>Cents: +7 semitones is 700.</summary>
        Pitch = 0,
        /// <summary>Frames. Choosing a slice on a step sets Start, LoopStart and Length to its boundaries.</summary>
        Start = 1,
        LoopStart = 2,
        Length = 3,
        /// <summary>0 to 127 as on the screen.</summary>
        Velocity = 4,
        /// <summary>A knob: see <see cref="StepsBlob.KnobValue"/>.</summary>
        Attack = 5,
        Hold = 6,
        Release = 7,
        LfoSpeed = 8,
        /// <summary>In 1/48 of a step, -48 to 48 (Confirmed, render: 21 delayed a step by 54.7 ms at 120 BPM).</summary>
        Offset = 10,
        /// <summary>Triggers in all, the first included.</summary>
        RetriggerCount = 11,
        /// <summary>A place in the unit's list: 0 is 1/64 up to 7 for 1/4T (Confirmed, render); see <see cref="StepsBlob.RetriggerSpeedSteps"/>.</summary>
        RetriggerSpeed = 12,
        /// <summary>The step's volume, 0 to 127.</summary>
        Volume = 13,
    }

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

        /// <summary>Places a trigger of a given kind; the step's settings are left as they are.</summary>
        public void SetTrigger(int pad, int step, StepTrigger kind)
        {
            SetTrigger(pad, step);
            WriteInt32(RecordOffset(pad, step) + 8, (int)kind);
        }

        public StepTrigger GetTriggerKind(int pad, int step)
        {
            return (StepTrigger)ReadInt32(RecordOffset(pad, step) + 8);
        }

        public void ClearTrigger(int pad, int step)
        {
            Buffer.BlockCopy(DefaultRecord, 0, data, RecordOffset(pad, step), RecordSize);
        }

        const int EntriesOffset = 12;
        const int EntrySize = 4;
        const int ValueBits = 22;
        const int ValueMask = (1 << ValueBits) - 1;
        // An entry with nothing set holds this in its value bits: 00 80 3f after its id.
        const int UnsetField = 0x3F8000;

        /// <summary>
        /// The value a step holds for a setting, or null when the step leaves it to the track. An
        /// entry is its id in one byte and a 22-bit signed value in the next three (Confirmed, file:
        /// -5 semitones is stored 0c fe 3f, -500 cents).
        /// </summary>
        public int? GetParameter(int pad, int step, StepParameter parameter)
        {
            int offset = EntryOffset(pad, step, parameter);
            int field = data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16);
            if (field == UnsetField)
                return null;
            return (field << (32 - ValueBits)) >> (32 - ValueBits);
        }

        public void SetParameter(int pad, int step, StepParameter parameter, int value)
        {
            if (value < -(1 << (ValueBits - 1)) || value >= 1 << (ValueBits - 1))
                throw new ArgumentOutOfRangeException("value");
            int offset = EntryOffset(pad, step, parameter);
            int field = value & ValueMask;
            data[offset] = (byte)parameter;
            data[offset + 1] = (byte)field;
            data[offset + 2] = (byte)(field >> 8);
            data[offset + 3] = (byte)(field >> 16);
        }

        public void ClearParameter(int pad, int step, StepParameter parameter)
        {
            int offset = EntryOffset(pad, step, parameter);
            Buffer.BlockCopy(DefaultRecord, offset - RecordOffset(pad, step), data, offset, EntrySize);
        }

        /// <summary>
        /// Makes a step play a stretch of its pad's sample, as choosing a slice on the unit does:
        /// Start and LoopStart at the first frame, Length the frames to play. The step keeps it
        /// when the pad's slices are moved later (Confirmed, file: <c>steps-2.prj</c>).
        /// </summary>
        public void SetPlayRange(int pad, int step, int startFrame, int lengthFrames)
        {
            SetParameter(pad, step, StepParameter.Start, startFrame);
            SetParameter(pad, step, StepParameter.LoopStart, startFrame);
            SetParameter(pad, step, StepParameter.Length, lengthFrames);
        }

        /// <summary>
        /// A setting turned on a 0 to 127 knob is stored as that fraction of 2^21, rounded down
        /// (Confirmed, file: attack 5 is 82565, hold 99 is 1634787, release 15 is 247695).
        /// With the envelope's SYNC on, hold is stored on another scale, not yet mapped.
        /// </summary>
        public static int KnobValue(int knob)
        {
            if (knob < 0 || knob > 127)
                throw new ArgumentOutOfRangeException("knob");
            return (int)((long)knob * (1 << 21) / 127);
        }

        /// <summary>
        /// The gap between retriggers, in steps, for each place in the unit's list: 1/64, 1/32T,
        /// 1/32, 1/16T, 1/16, 1/8T, 1/8, 1/4T (Confirmed, render of P02Steps at 120 BPM), then
        /// 1/4 (Unverified: on the unit's list, not yet rendered).
        /// </summary>
        public static readonly double[] RetriggerSpeedSteps = { 0.25, 1.0 / 3, 0.5, 2.0 / 3, 1, 4.0 / 3, 2, 8.0 / 3, 4 };

        static int EntryOffset(int pad, int step, StepParameter parameter)
        {
            int id = (int)parameter;
            if (id < 0 || id > 22)
                throw new ArgumentOutOfRangeException("parameter");
            return RecordOffset(pad, step) + EntriesOffset + id * EntrySize;
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
        /// pad -127, step -126, trigger kind 15 (Full), then 35 parameter entries with nothing set
        /// (id, 00, 80, 3f): ids 0 to 22, then 5 to 10 twice. The last twelve are the six settings of
        /// each insert effect; a set one carries another id (41 for FX1's first). See
        /// <see cref="StepParameter"/> for the first 23.
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
