using System;
using System.Collections.Generic;

namespace Strands.Sp16
{
    /// <summary>Scalar type codes of the payload grammar (format spec section 2).</summary>
    public static class PrjType
    {
        public const byte Int32 = 1;
        public const byte True = 2;
        public const byte False = 3;
        public const byte Float64 = 4;
        public const byte String = 5;
        public const byte Array = 7;
        public const byte Blob = 8;
    }

    /// <summary>
    /// One named value. <see cref="Raw"/> is the type byte followed by the data, exactly as stored,
    /// so a value nobody touches is written back unchanged. Null <see cref="Raw"/> is the null value.
    /// </summary>
    public sealed class PrjScalar
    {
        public string Name;
        public byte[] Raw;

        public PrjScalar(string name, byte[] raw)
        {
            Name = name;
            Raw = raw;
        }

        public bool IsNull { get { return Raw == null; } }
        public byte Type { get { return Raw[0]; } }

        public int AsInt32()
        {
            Expect(PrjType.Int32, 5);
            return Raw[1] | (Raw[2] << 8) | (Raw[3] << 16) | (Raw[4] << 24);
        }

        public bool AsBool()
        {
            if (Raw != null && Raw.Length == 1 && (Raw[0] == PrjType.True || Raw[0] == PrjType.False))
                return Raw[0] == PrjType.True;
            throw new InvalidOperationException("Scalar '" + Name + "' is not a bool.");
        }

        public double AsFloat64()
        {
            Expect(PrjType.Float64, 9);
            return BitConverter.Int64BitsToDouble(ReadInt64(Raw, 1));
        }

        public string AsString()
        {
            if (Raw == null || Raw[0] != PrjType.String || Raw[Raw.Length - 1] != 0)
                throw new InvalidOperationException("Scalar '" + Name + "' is not a string.");
            return Latin1.GetString(Raw, 1, Raw.Length - 2);
        }

        /// <summary>The bytes after the type byte (for blobs, the zlib stream).</summary>
        public byte[] Data()
        {
            var data = new byte[Raw.Length - 1];
            Buffer.BlockCopy(Raw, 1, data, 0, data.Length);
            return data;
        }

        public void SetInt32(int value)
        {
            Raw = new[] { PrjType.Int32, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
        }

        public void SetBool(bool value)
        {
            Raw = new[] { value ? PrjType.True : PrjType.False };
        }

        public void SetFloat64(double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            var raw = new byte[9];
            raw[0] = PrjType.Float64;
            for (int i = 0; i < 8; i++)
                raw[1 + i] = (byte)(bits >> (8 * i));
            Raw = raw;
        }

        public void SetString(string value)
        {
            var raw = new byte[value.Length + 2];
            raw[0] = PrjType.String;
            Latin1.GetBytes(value, raw, 1);
            Raw = raw;
        }

        public void SetBlob(byte[] zlibStream)
        {
            var raw = new byte[zlibStream.Length + 1];
            raw[0] = PrjType.Blob;
            Buffer.BlockCopy(zlibStream, 0, raw, 1, zlibStream.Length);
            Raw = raw;
        }

        /// <summary>An array of bools, as used by TrackMutes and TrackSolos.</summary>
        public void SetBoolArray(bool[] values)
        {
            var raw = new List<byte> { PrjType.Array };
            PrjTree.AppendVi(raw, values.Length);
            foreach (bool value in values)
            {
                PrjTree.AppendVi(raw, 1);
                raw.Add(value ? PrjType.True : PrjType.False);
            }
            Raw = raw.ToArray();
        }

        /// <summary>An array of int32, as used by SliceData.grids. An empty array gives no items.</summary>
        public int[] AsInt32Array()
        {
            if (Raw == null || Raw[0] != PrjType.Array)
                throw new InvalidOperationException("Scalar '" + Name + "' is not an array.");
            int offset = 1;
            int count = PrjTree.ReadVi(Raw, ref offset);
            var values = new int[count];
            for (int i = 0; i < count; i++)
            {
                int length = PrjTree.ReadVi(Raw, ref offset);
                if (length != 5 || offset + 5 > Raw.Length || Raw[offset] != PrjType.Int32)
                    throw new InvalidOperationException("Item " + i + " of array '" + Name + "' is not an int32.");
                values[i] = Raw[offset + 1] | (Raw[offset + 2] << 8) | (Raw[offset + 3] << 16) | (Raw[offset + 4] << 24);
                offset += 5;
            }
            if (offset != Raw.Length)
                throw new InvalidOperationException("Array '" + Name + "' has bytes after its last item.");
            return values;
        }

        public void SetInt32Array(int[] values)
        {
            var raw = new List<byte> { PrjType.Array };
            PrjTree.AppendVi(raw, values.Length);
            foreach (int value in values)
            {
                PrjTree.AppendVi(raw, 5);
                raw.Add(PrjType.Int32);
                for (int i = 0; i < 4; i++)
                    raw.Add((byte)(value >> (8 * i)));
            }
            Raw = raw.ToArray();
        }

        void Expect(byte type, int length)
        {
            if (Raw == null || Raw[0] != type || Raw.Length != length)
                throw new InvalidOperationException("Scalar '" + Name + "' does not have type " + type + ".");
        }

        static long ReadInt64(byte[] data, int offset)
        {
            long value = 0;
            for (int i = 7; i >= 0; i--)
                value = (value << 8) | data[offset + i];
            return value;
        }
    }

    /// <summary>A struct of the payload tree: scalars and children, both in file order.</summary>
    public sealed class PrjNode
    {
        public string Name;
        public readonly List<PrjScalar> Scalars = new List<PrjScalar>();
        public readonly List<PrjNode> Children = new List<PrjNode>();

        public PrjNode(string name)
        {
            Name = name;
        }

        public PrjScalar FindScalar(string name)
        {
            foreach (PrjScalar scalar in Scalars)
                if (scalar.Name == name)
                    return scalar;
            return null;
        }

        /// <summary>The named scalar. Throws when the template does not carry it, so no field is ever invented.</summary>
        public PrjScalar Scalar(string name)
        {
            PrjScalar scalar = FindScalar(name);
            if (scalar == null)
                throw new KeyNotFoundException("'" + Name + "' has no scalar '" + name + "'.");
            return scalar;
        }

        public PrjNode FindChild(string name)
        {
            foreach (PrjNode child in Children)
                if (child.Name == name)
                    return child;
            return null;
        }

        public PrjNode Child(string name)
        {
            PrjNode child = FindChild(name);
            if (child == null)
                throw new KeyNotFoundException("'" + Name + "' has no child '" + name + "'.");
            return child;
        }

        public PrjScalar AddScalar(string name)
        {
            var scalar = new PrjScalar(name, null);
            Scalars.Add(scalar);
            return scalar;
        }
    }

    /// <summary>Latin-1 maps bytes to the first 256 code points one to one, so no encoding provider is needed.</summary>
    static class Latin1
    {
        public static string GetString(byte[] data, int offset, int count)
        {
            var chars = new char[count];
            for (int i = 0; i < count; i++)
                chars[i] = (char)data[offset + i];
            return new string(chars);
        }

        public static void GetBytes(string value, byte[] target, int offset)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == 0 || c > 255)
                    throw new ArgumentException("'" + value + "' cannot be stored as a Latin-1 string.");
                target[offset + i] = (byte)c;
            }
        }
    }
}
