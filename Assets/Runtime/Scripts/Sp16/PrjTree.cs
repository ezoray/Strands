using System;
using System.Collections.Generic;
using System.IO;

namespace Strands.Sp16
{
    /// <summary>
    /// Reader and writer for the tagged tree (format spec section 2):
    ///   struct = name\0 vi(nScalars) scalar* vi(nChildren) struct*
    ///   scalar = name\0 00 | name\0 vi(len) type data
    /// </summary>
    public static class PrjTree
    {
        public static PrjNode Parse(byte[] payload)
        {
            int offset = 0;
            PrjNode root = ReadStruct(payload, ref offset);
            if (offset != payload.Length)
                throw new InvalidDataException((payload.Length - offset) + " trailing bytes after the root struct.");
            return root;
        }

        public static byte[] Emit(PrjNode root)
        {
            var output = new List<byte>(1 << 20);
            WriteStruct(output, root);
            return output.ToArray();
        }

        static PrjNode ReadStruct(byte[] p, ref int offset)
        {
            var node = new PrjNode(ReadName(p, ref offset));
            int scalarCount = ReadVi(p, ref offset);
            for (int i = 0; i < scalarCount; i++)
            {
                string name = ReadName(p, ref offset);
                int length = ReadVi(p, ref offset);
                byte[] raw = null;
                if (length > 0)
                {
                    if (offset + length > p.Length)
                        throw new InvalidDataException("Scalar '" + name + "' runs past the end of the payload.");
                    raw = new byte[length];
                    Buffer.BlockCopy(p, offset, raw, 0, length);
                    offset += length;
                }
                node.Scalars.Add(new PrjScalar(name, raw));
            }
            int childCount = ReadVi(p, ref offset);
            for (int i = 0; i < childCount; i++)
                node.Children.Add(ReadStruct(p, ref offset));
            return node;
        }

        static void WriteStruct(List<byte> output, PrjNode node)
        {
            AppendName(output, node.Name);
            AppendVi(output, node.Scalars.Count);
            foreach (PrjScalar scalar in node.Scalars)
            {
                AppendName(output, scalar.Name);
                if (scalar.Raw == null)
                {
                    output.Add(0);
                }
                else
                {
                    AppendVi(output, scalar.Raw.Length);
                    output.AddRange(scalar.Raw);
                }
            }
            AppendVi(output, node.Children.Count);
            foreach (PrjNode child in node.Children)
                WriteStruct(output, child);
        }

        /// <summary>Variable-width number: one byte k, then k bytes little-endian. k = 0 is the value 0.</summary>
        internal static int ReadVi(byte[] p, ref int offset)
        {
            int k = p[offset++];
            if (k > 4)
                throw new InvalidDataException("Variable-width number with " + k + " bytes at offset " + (offset - 1) + ".");
            int value = 0;
            for (int i = 0; i < k; i++)
                value |= p[offset++] << (8 * i);
            if (value < 0)
                throw new InvalidDataException("Variable-width number overflow at offset " + offset + ".");
            return value;
        }

        /// <summary>Writes the minimal k, which reproduces every sample file byte for byte.</summary>
        internal static void AppendVi(List<byte> output, int value)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException("value");
            int k = 0;
            for (int v = value; v != 0; v >>= 8)
                k++;
            output.Add((byte)k);
            for (int i = 0; i < k; i++)
                output.Add((byte)(value >> (8 * i)));
        }

        static string ReadName(byte[] p, ref int offset)
        {
            int end = Array.IndexOf(p, (byte)0, offset);
            if (end < 0)
                throw new InvalidDataException("Unterminated name at offset " + offset + ".");
            string name = Latin1.GetString(p, offset, end - offset);
            offset = end + 1;
            return name;
        }

        static void AppendName(List<byte> output, string name)
        {
            var bytes = new byte[name.Length];
            Latin1.GetBytes(name, bytes, 0);
            output.AddRange(bytes);
            output.Add(0);
        }
    }
}
