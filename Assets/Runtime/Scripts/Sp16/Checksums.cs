namespace Strands.Sp16
{
    public static class Adler32
    {
        const uint Mod = 65521;

        public static uint Compute(byte[] data)
        {
            return Compute(data, 0, data.Length);
        }

        public static uint Compute(byte[] data, int offset, int count)
        {
            uint a = 1, b = 0;
            int end = offset + count;
            while (offset < end)
            {
                // 5552 is the largest run that cannot overflow a uint before the modulo.
                int run = System.Math.Min(5552, end - offset);
                for (int i = 0; i < run; i++)
                {
                    a += data[offset++];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            return (b << 16) | a;
        }
    }

    public static class Crc32
    {
        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(byte[] data)
        {
            return Compute(data, 0, data.Length);
        }

        public static uint Compute(byte[] data, int offset, int count)
        {
            uint c = 0xFFFFFFFFu;
            int end = offset + count;
            for (int i = offset; i < end; i++)
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
