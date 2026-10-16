using System;
using System.IO;
using System.Security.Cryptography;

namespace Strands.Audio
{
    /// <summary>
    /// A FLAC decoder written from the format's specification, RFC 9639. Every frame is checked
    /// against its two CRCs, and the whole of the audio against the MD5 the encoder stored.
    /// </summary>
    public static class FlacReader
    {
        const int ChannelsLeftSide = 8, ChannelsSideRight = 9, ChannelsMidSide = 10;

        static readonly byte[] crc8Table = BuildCrc8();
        static readonly ushort[] crc16Table = BuildCrc16();

        public static WavData Read(string path)
        {
            return Read(File.ReadAllBytes(path));
        }

        /// <summary>True when the file is FLAC, with or without an ID3 tag in front of it.</summary>
        public static bool IsFlac(byte[] file)
        {
            int start = SkipId3(file);
            return start + 4 <= file.Length && file[start] == 'f' && file[start + 1] == 'L' && file[start + 2] == 'a' && file[start + 3] == 'C';
        }

        /// <summary>
        /// Reads 16 or 24-bit FLAC, mono or stereo. 24-bit is rounded to 16-bit without dither and
        /// mono is copied to both channels, as for WAV.
        /// </summary>
        public static WavData Read(byte[] file)
        {
            if (!IsFlac(file))
                throw new InvalidDataException("Not a FLAC file.");
            int offset = SkipId3(file) + 4;

            int sampleRate = 0, channels = 0, bits = 0;
            long totalFrames = 0;
            var storedMd5 = new byte[16];
            bool last = false;
            while (!last)
            {
                if (offset + 4 > file.Length)
                    throw new InvalidDataException("FLAC file ends inside its header.");
                last = (file[offset] & 0x80) != 0;
                int type = file[offset] & 0x7F;
                int length = (file[offset + 1] << 16) | (file[offset + 2] << 8) | file[offset + 3];
                offset += 4;
                if (offset + length > file.Length)
                    throw new InvalidDataException("FLAC file ends inside its header.");
                if (type == 0 && length >= 34)
                {
                    // Stream info: after the block and frame sizes come 20 bits of sample rate, 3 of
                    // channels - 1, 5 of bits - 1, 36 of length in samples, then the MD5.
                    int at = offset + 10;
                    sampleRate = (file[at] << 12) | (file[at + 1] << 4) | (file[at + 2] >> 4);
                    channels = ((file[at + 2] >> 1) & 7) + 1;
                    bits = (((file[at + 2] & 1) << 4) | (file[at + 3] >> 4)) + 1;
                    totalFrames = ((long)(file[at + 3] & 0x0F) << 32) | ((long)file[at + 4] << 24) | ((long)file[at + 5] << 16) | ((long)file[at + 6] << 8) | file[at + 7];
                    Buffer.BlockCopy(file, offset + 18, storedMd5, 0, 16);
                }
                offset += length;
            }

            if (channels == 0)
                throw new InvalidDataException("FLAC file has no stream information.");
            if (channels != 1 && channels != 2)
                throw new NotSupportedException("Only mono and stereo FLAC files are supported (this one has " + channels + " channels).");
            if (bits != 16 && bits != 24)
                throw new NotSupportedException("Only 16-bit and 24-bit FLAC files are supported (this one is " + bits + "-bit).");
            if (totalFrames > int.MaxValue / 2)
                throw new NotSupportedException("This FLAC file is too long to load.");

            try
            {
                return Decode(file, offset, sampleRate, channels, bits, totalFrames, storedMd5);
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("FLAC file is cut short: it ends in the middle of a frame.");
            }
        }

        static WavData Decode(byte[] file, int offset, int sampleRate, int channels, int bits, long totalFrames, byte[] storedMd5)
        {
            // A length of zero in the header means the encoder did not know it; read to the end.
            bool lengthKnown = totalFrames > 0;
            var samples = new short[(lengthKnown ? (int)totalFrames : sampleRate) * 2];
            int decoded = 0;
            int bytesPerSample = bits / 8;
            var block = new[] { new int[0], new int[0] };
            var raw = new byte[0];
            var reader = new BitReader(file, offset);

            using (MD5 md5 = MD5.Create())
            {
                while (!lengthKnown || decoded < totalFrames)
                {
                    if (!AtFrame(file, reader.BytePosition))
                    {
                        if (lengthKnown)
                            throw new InvalidDataException("FLAC file is cut short: it holds " + decoded + " of " + totalFrames + " samples.");
                        break;
                    }
                    int blockSize = ReadFrame(reader, file, channels, bits, block);
                    int[] left = block[0], right = block[channels - 1];

                    // The MD5 is taken over the samples as a little-endian file would hold them.
                    if (raw.Length < blockSize * channels * bytesPerSample)
                        raw = new byte[blockSize * channels * bytesPerSample];
                    int length = 0;
                    for (int i = 0; i < blockSize; i++)
                        for (int channel = 0; channel < channels; channel++)
                        {
                            int value = block[channel][i];
                            for (int b = 0; b < bytesPerSample; b++)
                                raw[length++] = (byte)(value >> (8 * b));
                        }
                    md5.TransformBlock(raw, 0, length, null, 0);

                    int keep = lengthKnown ? (int)Math.Min(blockSize, totalFrames - decoded) : blockSize;
                    if ((decoded + keep) * 2 > samples.Length)
                        Array.Resize(ref samples, Math.Max(samples.Length * 2, (decoded + keep) * 2));
                    for (int i = 0; i < keep; i++)
                    {
                        samples[(decoded + i) * 2] = bits == 16 ? (short)left[i] : Pcm.From24Bit(left[i]);
                        samples[(decoded + i) * 2 + 1] = bits == 16 ? (short)right[i] : Pcm.From24Bit(right[i]);
                    }
                    decoded += keep;
                }

                md5.TransformFinalBlock(raw, 0, 0);
                // An MD5 of all zeros means the encoder did not store one.
                if (Array.Exists(storedMd5, b => b != 0) && !Same(md5.Hash, storedMd5))
                    throw new InvalidDataException("FLAC file is damaged: its audio does not match the checksum stored in the file.");
            }

            if (decoded * 2 != samples.Length)
                Array.Resize(ref samples, decoded * 2);
            return new WavData
            {
                SampleRate = sampleRate,
                Samples = samples,
                SourceChannels = channels,
                SourceBitsPerSample = bits
            };
        }

        /// <summary>Every frame starts on a byte with fourteen 1 bits, a 0 and a 0.</summary>
        static bool AtFrame(byte[] file, int position)
        {
            return position + 2 <= file.Length && file[position] == 0xFF && (file[position + 1] & 0xFE) == 0xF8;
        }

        /// <summary>Decodes one frame into block (one array per channel) and returns its length in samples.</summary>
        static int ReadFrame(BitReader reader, byte[] file, int channels, int bits, int[][] block)
        {
            int frameStart = reader.BytePosition;
            reader.Read(15);                                // sync and a reserved bit, checked by AtFrame
            reader.Read(1);                                 // fixed or variable block size; not needed to decode
            int blockCode = (int)reader.Read(4);
            int rateCode = (int)reader.Read(4);
            int channelCode = (int)reader.Read(4);
            int sizeCode = (int)reader.Read(3);
            if (reader.Read(1) != 0)
                throw Damaged();

            // The frame or sample number, stored the way UTF-8 stores a character. Only its length matters here.
            uint first = reader.Read(8);
            int leadingOnes = 0;
            while ((first & (0x80 >> leadingOnes)) != 0)
                if (++leadingOnes > 7)
                    throw Damaged();
            if (leadingOnes == 1)
                throw Damaged();
            for (int i = 1; i < leadingOnes; i++)
                if ((reader.Read(8) & 0xC0) != 0x80)
                    throw Damaged();

            int blockSize;
            if (blockCode == 0)
                throw Damaged();
            else if (blockCode == 1)
                blockSize = 192;
            else if (blockCode <= 5)
                blockSize = 576 << (blockCode - 2);
            else if (blockCode == 6)
                blockSize = (int)reader.Read(8) + 1;
            else if (blockCode == 7)
                blockSize = (int)reader.Read(16) + 1;
            else
                blockSize = 256 << (blockCode - 8);

            if (rateCode == 12)
                reader.Read(8);
            else if (rateCode == 13 || rateCode == 14)
                reader.Read(16);
            else if (rateCode == 15)
                throw Damaged();

            uint headerCrc = reader.Read(8);
            if (Crc8(file, frameStart, reader.BytePosition - 1 - frameStart) != headerCrc)
                throw Damaged();

            // The header says a size only when it is one of the common ones; it must agree with the stream.
            int frameBits = sizeCode == 0 ? bits : sizeCode == 1 ? 8 : sizeCode == 2 ? 12 : sizeCode == 4 ? 16 : sizeCode == 5 ? 20 : sizeCode == 6 ? 24 : sizeCode == 7 ? 32 : 0;
            int frameChannels = channelCode < 8 ? channelCode + 1 : channelCode <= ChannelsMidSide ? 2 : 0;
            if (frameBits != bits || frameChannels != channels)
                throw new InvalidDataException("FLAC file changes its format part way through, which is not supported.");

            for (int channel = 0; channel < channels; channel++)
            {
                if (block[channel].Length < blockSize)
                    block[channel] = new int[blockSize];
                // The difference between two channels needs one more bit than either.
                bool side = (channelCode == ChannelsLeftSide && channel == 1) || (channelCode == ChannelsSideRight && channel == 0) || (channelCode == ChannelsMidSide && channel == 1);
                ReadSubframe(reader, block[channel], blockSize, bits + (side ? 1 : 0));
            }

            int[] a = block[0], b = block[1];
            if (channelCode == ChannelsLeftSide)
                for (int i = 0; i < blockSize; i++)
                    b[i] = a[i] - b[i];
            else if (channelCode == ChannelsSideRight)
                for (int i = 0; i < blockSize; i++)
                    a[i] += b[i];
            else if (channelCode == ChannelsMidSide)
                for (int i = 0; i < blockSize; i++)
                {
                    // The sum of the channels was halved; its lost bit is the lowest bit of the difference.
                    int side = b[i];
                    int mid = (a[i] << 1) | (side & 1);
                    a[i] = (mid + side) >> 1;
                    b[i] = (mid - side) >> 1;
                }

            reader.AlignToByte();
            uint frameCrc = reader.Read(16);
            if (Crc16(file, frameStart, reader.BytePosition - 2 - frameStart) != frameCrc)
                throw Damaged();
            return blockSize;
        }

        static void ReadSubframe(BitReader reader, int[] output, int blockSize, int bits)
        {
            if (reader.Read(1) != 0)
                throw Damaged();
            int type = (int)reader.Read(6);
            // Low bits that are zero in every sample of the block are left out and put back afterwards.
            int wasted = reader.Read(1) == 1 ? reader.ReadUnary() + 1 : 0;
            bits -= wasted;
            if (bits <= 0)
                throw Damaged();

            if (type == 0)
            {
                int value = reader.ReadSigned(bits);
                for (int i = 0; i < blockSize; i++)
                    output[i] = value;
            }
            else if (type == 1)
            {
                for (int i = 0; i < blockSize; i++)
                    output[i] = reader.ReadSigned(bits);
            }
            else if (type >= 8 && type <= 12)
            {
                int order = type - 8;
                if (order > blockSize)
                    throw Damaged();
                for (int i = 0; i < order; i++)
                    output[i] = reader.ReadSigned(bits);
                ReadResidual(reader, output, order, blockSize);
                RestoreFixed(output, order, blockSize);
            }
            else if (type >= 32)
            {
                int order = type - 31;
                if (order > blockSize)
                    throw Damaged();
                for (int i = 0; i < order; i++)
                    output[i] = reader.ReadSigned(bits);
                int precision = (int)reader.Read(4) + 1;
                int shift = reader.ReadSigned(5);
                if (precision == 16 || shift < 0)
                    throw Damaged();
                var coefficients = new int[order];
                for (int i = 0; i < order; i++)
                    coefficients[i] = reader.ReadSigned(precision);
                ReadResidual(reader, output, order, blockSize);
                for (int i = order; i < blockSize; i++)
                {
                    long sum = 0;
                    for (int j = 0; j < order; j++)
                        sum += (long)coefficients[j] * output[i - 1 - j];
                    output[i] += (int)(sum >> shift);
                }
            }
            else
                throw Damaged();

            if (wasted > 0)
                for (int i = 0; i < blockSize; i++)
                    output[i] <<= wasted;
        }

        /// <summary>
        /// The differences between the prediction and the audio, Rice coded in partitions that each
        /// have their own parameter. They are written to output from index order onwards.
        /// </summary>
        static void ReadResidual(BitReader reader, int[] output, int order, int blockSize)
        {
            int method = (int)reader.Read(2);
            if (method > 1)
                throw Damaged();
            int parameterBits = method == 0 ? 4 : 5;
            int escape = method == 0 ? 15 : 31;
            int partitionOrder = (int)reader.Read(4);
            int partitions = 1 << partitionOrder;
            int perPartition = blockSize >> partitionOrder;
            if ((blockSize & (partitions - 1)) != 0 || perPartition < order)
                throw Damaged();

            int index = order;
            for (int partition = 0; partition < partitions; partition++)
            {
                int count = perPartition - (partition == 0 ? order : 0);
                int parameter = (int)reader.Read(parameterBits);
                if (parameter == escape)
                {
                    // Not Rice coded: plain numbers of a stated width.
                    int width = (int)reader.Read(5);
                    for (int i = 0; i < count; i++)
                        output[index++] = reader.ReadSigned(width);
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        uint folded = ((uint)reader.ReadUnary() << parameter) | reader.Read(parameter);
                        output[index++] = (int)(folded >> 1) ^ -(int)(folded & 1);
                    }
                }
            }
        }

        /// <summary>Adds back the fixed predictors: the last value, a straight line through the last two, and so on.</summary>
        static void RestoreFixed(int[] s, int order, int blockSize)
        {
            for (int i = order; i < blockSize; i++)
            {
                long prediction = 0;
                if (order == 1)
                    prediction = s[i - 1];
                else if (order == 2)
                    prediction = 2L * s[i - 1] - s[i - 2];
                else if (order == 3)
                    prediction = 3L * s[i - 1] - 3L * s[i - 2] + s[i - 3];
                else if (order == 4)
                    prediction = 4L * s[i - 1] - 6L * s[i - 2] + 4L * s[i - 3] - s[i - 4];
                s[i] += (int)prediction;
            }
        }

        /// <summary>Some taggers put an ID3v2 tag in front of the FLAC marker.</summary>
        static int SkipId3(byte[] file)
        {
            if (file.Length < 10 || file[0] != 'I' || file[1] != 'D' || file[2] != '3')
                return 0;
            int size = ((file[6] & 0x7F) << 21) | ((file[7] & 0x7F) << 14) | ((file[8] & 0x7F) << 7) | (file[9] & 0x7F);
            return 10 + size + ((file[5] & 0x10) != 0 ? 10 : 0);
        }

        static InvalidDataException Damaged()
        {
            return new InvalidDataException("FLAC file is damaged: a frame fails its check.");
        }

        static bool Same(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i])
                    return false;
            return true;
        }

        static byte[] BuildCrc8()
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 0x80) != 0 ? (value << 1) ^ 0x07 : value << 1;
                table[i] = (byte)value;
            }
            return table;
        }

        static ushort[] BuildCrc16()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int value = i << 8;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 0x8000) != 0 ? (value << 1) ^ 0x8005 : value << 1;
                table[i] = (ushort)value;
            }
            return table;
        }

        static uint Crc8(byte[] file, int offset, int count)
        {
            byte crc = 0;
            for (int i = 0; i < count; i++)
                crc = crc8Table[crc ^ file[offset + i]];
            return crc;
        }

        static uint Crc16(byte[] file, int offset, int count)
        {
            ushort crc = 0;
            for (int i = 0; i < count; i++)
                crc = (ushort)((crc << 8) ^ crc16Table[(crc >> 8) ^ file[offset + i]]);
            return crc;
        }

        /// <summary>Reads numbers of any width from a byte array, most significant bit first.</summary>
        sealed class BitReader
        {
            readonly byte[] data;
            int position;
            // The low `available` bits of buffer are the bits read from data and not yet used.
            ulong buffer;
            int available;

            public BitReader(byte[] data, int position)
            {
                this.data = data;
                this.position = position;
            }

            /// <summary>The byte the next bit comes from. Meaningful on a byte boundary.</summary>
            public int BytePosition { get { return position - available / 8; } }

            public uint Read(int count)
            {
                if (count == 0)
                    return 0;
                while (available < count)
                    Fill();
                available -= count;
                return (uint)((buffer >> available) & ((1UL << count) - 1));
            }

            public int ReadSigned(int count)
            {
                if (count == 0)
                    return 0;
                return (int)(Read(count) << (32 - count)) >> (32 - count);
            }

            /// <summary>Counts the 0 bits before the next 1 bit, and takes the 1 as well.</summary>
            public int ReadUnary()
            {
                int zeros = 0;
                while (true)
                {
                    if (available == 0)
                        Fill();
                    ulong unread = buffer & ((1UL << available) - 1);
                    if (unread == 0)
                    {
                        zeros += available;
                        available = 0;
                        continue;
                    }
                    int top = available - 1;
                    while (((unread >> top) & 1) == 0)
                        top--;
                    zeros += available - 1 - top;
                    available = top;
                    return zeros;
                }
            }

            public void AlignToByte()
            {
                available -= available % 8;
            }

            void Fill()
            {
                if (position >= data.Length)
                    throw new EndOfStreamException();
                buffer = (buffer << 8) | data[position++];
                available += 8;
            }
        }
    }
}
