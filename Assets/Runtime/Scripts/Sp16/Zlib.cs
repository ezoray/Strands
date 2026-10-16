using System;
using System.IO;
using System.IO.Compression;

namespace Strands.Sp16
{
    /// <summary>
    /// Raw DEFLATE plus the zlib wrapper (2-byte header, big-endian Adler-32 trailer).
    /// The wrapper is written by hand because ZLibStream is not in .NET Standard 2.1.
    /// </summary>
    public static class Zlib
    {
        public static byte[] InflateRaw(byte[] data, int offset, int count)
        {
            using (var input = new MemoryStream(data, offset, count, false))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        public static byte[] DeflateRaw(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                    deflate.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        public static byte[] Decompress(byte[] zlibStream)
        {
            return Decompress(zlibStream, 0, zlibStream.Length);
        }

        public static byte[] Decompress(byte[] data, int offset, int count)
        {
            if (count < 6)
                throw new InvalidDataException("zlib stream is too short.");
            int cmf = data[offset], flg = data[offset + 1];
            if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0)
                throw new InvalidDataException("Not a zlib stream.");
            if ((flg & 0x20) != 0)
                throw new InvalidDataException("zlib preset dictionaries are not supported.");

            byte[] inflated = InflateRaw(data, offset + 2, count - 6);
            uint expected = ReadUInt32BigEndian(data, offset + count - 4);
            if (Adler32.Compute(inflated) != expected)
                throw new InvalidDataException("zlib Adler-32 mismatch.");
            return inflated;
        }

        public static byte[] Compress(byte[] data)
        {
            byte[] raw = DeflateRaw(data);
            var result = new byte[raw.Length + 6];
            // 78 9c is the header the unit writes (deflate, 32K window, default level).
            result[0] = 0x78;
            result[1] = 0x9C;
            Buffer.BlockCopy(raw, 0, result, 2, raw.Length);
            WriteUInt32BigEndian(result, result.Length - 4, Adler32.Compute(data));
            return result;
        }

        internal static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
                 | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        internal static void WriteUInt32BigEndian(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }
}
