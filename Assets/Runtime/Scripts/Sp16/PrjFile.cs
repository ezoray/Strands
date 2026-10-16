using System;
using System.IO;

namespace Strands.Sp16
{
    /// <summary>
    /// The .prj container (format spec section 1): a 10-byte magic, a raw DEFLATE stream,
    /// and the big-endian Adler-32 of the inflated payload.
    /// </summary>
    public static class PrjFile
    {
        static readonly byte[] Magic = { 0x76, 0x6C, 0x74, 0x72, 0x67, 0x7A, 0x69, 0x70, 0x78, 0x01 };

        public static bool HasMagic(byte[] file)
        {
            if (file.Length < Magic.Length + 4)
                return false;
            for (int i = 0; i < Magic.Length; i++)
                if (file[i] != Magic[i])
                    return false;
            return true;
        }

        /// <summary>Inflates the payload and checks its Adler-32.</summary>
        public static byte[] ReadPayload(byte[] file)
        {
            if (!HasMagic(file))
                throw new InvalidDataException("Not an SP-16 project file (bad magic).");
            byte[] payload = Zlib.InflateRaw(file, Magic.Length, file.Length - Magic.Length - 4);
            if (Adler32.Compute(payload) != Zlib.ReadUInt32BigEndian(file, file.Length - 4))
                throw new InvalidDataException("Project payload Adler-32 mismatch.");
            return payload;
        }

        public static byte[] WritePayload(byte[] payload)
        {
            byte[] deflated = Zlib.DeflateRaw(payload);
            var file = new byte[Magic.Length + deflated.Length + 4];
            Buffer.BlockCopy(Magic, 0, file, 0, Magic.Length);
            Buffer.BlockCopy(deflated, 0, file, Magic.Length, deflated.Length);
            Zlib.WriteUInt32BigEndian(file, file.Length - 4, Adler32.Compute(payload));
            return file;
        }

        public static PrjNode Read(byte[] file)
        {
            return PrjTree.Parse(ReadPayload(file));
        }

        public static byte[] Write(PrjNode root)
        {
            return WritePayload(PrjTree.Emit(root));
        }
    }
}
