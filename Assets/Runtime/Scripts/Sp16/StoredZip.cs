using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Strands.Sp16
{
    /// <summary>
    /// Writes the .tpkg container (format spec section 9): a zip whose entries are stored, not compressed,
    /// in a flat layout, dated 1980-01-01. Written by hand so the header fields match the package that
    /// was confirmed on the SP-16 byte for byte, and so entries can be streamed one at a time.
    /// </summary>
    public sealed class StoredZipWriter : IDisposable
    {
        // 1980-01-01 00:00:00 in DOS date and time.
        const ushort DosDate = 0x0021;
        const ushort DosTime = 0;
        // Version 2.0, made on Unix, with rw-r--r-- in the external attributes.
        const ushort VersionNeeded = 20;
        const ushort VersionMadeBy = 0x0314;
        const uint ExternalAttributes = 0x01A40000;

        struct Entry
        {
            public byte[] Name;
            public uint Crc;
            public uint Size;
            public uint Offset;
        }

        readonly Stream output;
        readonly List<Entry> entries = new List<Entry>();
        long position;
        bool finished;

        public StoredZipWriter(Stream output)
        {
            this.output = output;
        }

        public void Add(string name, byte[] data)
        {
            if (finished)
                throw new InvalidOperationException("The package is already finished.");
            if (position + data.Length > uint.MaxValue - (1 << 20))
                throw new InvalidOperationException("The package would exceed 4 GB.");

            var entry = new Entry
            {
                Name = Encoding.ASCII.GetBytes(name),
                Crc = Crc32.Compute(data),
                Size = (uint)data.Length,
                Offset = (uint)position
            };
            entries.Add(entry);

            var header = new List<byte>(30 + entry.Name.Length);
            AppendUInt32(header, 0x04034B50);
            AppendCommonFields(header, entry);
            AppendUInt16(header, (ushort)entry.Name.Length);
            AppendUInt16(header, 0);
            header.AddRange(entry.Name);
            Write(header);
            output.Write(data, 0, data.Length);
            position += data.Length;
        }

        /// <summary>Writes the central directory. Called by Dispose if not called before.</summary>
        public void Finish()
        {
            if (finished)
                return;
            finished = true;

            long directoryOffset = position;
            foreach (Entry entry in entries)
            {
                var record = new List<byte>(46 + entry.Name.Length);
                AppendUInt32(record, 0x02014B50);
                AppendUInt16(record, VersionMadeBy);
                AppendCommonFields(record, entry);
                AppendUInt16(record, (ushort)entry.Name.Length);
                AppendUInt16(record, 0);                  // extra length
                AppendUInt16(record, 0);                  // comment length
                AppendUInt16(record, 0);                  // disk number
                AppendUInt16(record, 0);                  // internal attributes
                AppendUInt32(record, ExternalAttributes);
                AppendUInt32(record, entry.Offset);
                record.AddRange(entry.Name);
                Write(record);
            }

            var end = new List<byte>(22);
            AppendUInt32(end, 0x06054B50);
            AppendUInt16(end, 0);
            AppendUInt16(end, 0);
            AppendUInt16(end, (ushort)entries.Count);
            AppendUInt16(end, (ushort)entries.Count);
            AppendUInt32(end, (uint)(position - directoryOffset));
            AppendUInt32(end, (uint)directoryOffset);
            AppendUInt16(end, 0);
            Write(end);
            output.Flush();
        }

        public void Dispose()
        {
            Finish();
        }

        // Version needed, flags, method (0 = stored), time, date, CRC, compressed size, size.
        static void AppendCommonFields(List<byte> target, Entry entry)
        {
            AppendUInt16(target, VersionNeeded);
            AppendUInt16(target, 0);
            AppendUInt16(target, 0);
            AppendUInt16(target, DosTime);
            AppendUInt16(target, DosDate);
            AppendUInt32(target, entry.Crc);
            AppendUInt32(target, entry.Size);
            AppendUInt32(target, entry.Size);
        }

        void Write(List<byte> bytes)
        {
            output.Write(bytes.ToArray(), 0, bytes.Count);
            position += bytes.Count;
        }

        static void AppendUInt16(List<byte> target, ushort value)
        {
            target.Add((byte)value);
            target.Add((byte)(value >> 8));
        }

        static void AppendUInt32(List<byte> target, uint value)
        {
            target.Add((byte)value);
            target.Add((byte)(value >> 8));
            target.Add((byte)(value >> 16));
            target.Add((byte)(value >> 24));
        }
    }

    /// <summary>Reads a .tpkg (or any small zip with stored or deflated entries) into memory.</summary>
    public static class StoredZipReader
    {
        public sealed class Entry
        {
            public string Name;
            public int Method;
            public byte[] Data;
        }

        public static List<Entry> Read(byte[] file)
        {
            int end = -1;
            for (int i = file.Length - 22; i >= 0; i--)
            {
                if (ReadUInt32(file, i) == 0x06054B50)
                {
                    end = i;
                    break;
                }
            }
            if (end < 0)
                throw new InvalidDataException("Not a zip file (no end of central directory).");

            int count = ReadUInt16(file, end + 10);
            int offset = (int)ReadUInt32(file, end + 16);
            var entries = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                if (ReadUInt32(file, offset) != 0x02014B50)
                    throw new InvalidDataException("Bad central directory record.");
                int method = ReadUInt16(file, offset + 10);
                int compressedSize = (int)ReadUInt32(file, offset + 20);
                int size = (int)ReadUInt32(file, offset + 24);
                int nameLength = ReadUInt16(file, offset + 28);
                int extraLength = ReadUInt16(file, offset + 30);
                int commentLength = ReadUInt16(file, offset + 32);
                int localOffset = (int)ReadUInt32(file, offset + 42);
                string name = Encoding.UTF8.GetString(file, offset + 46, nameLength);
                offset += 46 + nameLength + extraLength + commentLength;

                if (ReadUInt32(file, localOffset) != 0x04034B50)
                    throw new InvalidDataException("Bad local header for '" + name + "'.");
                int dataOffset = localOffset + 30 + ReadUInt16(file, localOffset + 26) + ReadUInt16(file, localOffset + 28);

                byte[] data;
                if (method == 0)
                {
                    data = new byte[size];
                    Buffer.BlockCopy(file, dataOffset, data, 0, size);
                }
                else if (method == 8)
                {
                    data = Zlib.InflateRaw(file, dataOffset, compressedSize);
                }
                else
                {
                    throw new InvalidDataException("Entry '" + name + "' uses unsupported compression method " + method + ".");
                }
                entries.Add(new Entry { Name = name, Method = method, Data = data });
            }
            return entries;
        }

        static int ReadUInt16(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8);
        }

        static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }
}
