using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio.Cast
{
    /// <summary>
    /// Minimal Cast binary writer (https://github.com/dtzxporter/cast).
    /// Self-contained so we don't depend on Cast.NET (.NET 8+ only).
    /// </summary>
    internal sealed class CastNode
    {
        public uint Identifier { get; }
        public ulong Hash { get; private set; }
        public Dictionary<string, CastProperty> Properties { get; } = new Dictionary<string, CastProperty>();
        public List<CastNode> Children { get; } = new List<CastNode>();

        private static ulong _nextHash = 0x534E495752545250UL;

        public CastNode(uint identifier)
        {
            Identifier = identifier;
            Hash = _nextHash++;
        }

        public CastNode AddChild(uint identifier)
        {
            var child = new CastNode(identifier);
            Children.Add(child);
            return child;
        }

        public CastProperty SetString(string name, string value)
        {
            var prop = new CastProperty(name, CastPropertyType.String) { StringValue = value ?? "" };
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetFloat(string name, float value)
        {
            var prop = new CastProperty(name, CastPropertyType.Float);
            prop.FloatValues.Add(value);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetByte(string name, byte value)
        {
            var prop = new CastProperty(name, CastPropertyType.Byte);
            prop.ByteValues.Add(value);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetInt32(string name, int value)
        {
            var prop = new CastProperty(name, CastPropertyType.Int32);
            prop.IntValues.Add(unchecked((uint)value));
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetInt64(string name, ulong value)
        {
            var prop = new CastProperty(name, CastPropertyType.Int64);
            prop.LongValues.Add(value);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetVec3(string name, float x, float y, float z)
        {
            var prop = new CastProperty(name, CastPropertyType.Vector3);
            prop.FloatValues.Add(x);
            prop.FloatValues.Add(y);
            prop.FloatValues.Add(z);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetVec4(string name, float x, float y, float z, float w)
        {
            var prop = new CastProperty(name, CastPropertyType.Vector4);
            prop.FloatValues.Add(x);
            prop.FloatValues.Add(y);
            prop.FloatValues.Add(z);
            prop.FloatValues.Add(w);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetFloatArray(string name, List<float> values)
        {
            var prop = new CastProperty(name, CastPropertyType.Float);
            prop.FloatValues.AddRange(values);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetInt32Array(string name, List<uint> values)
        {
            var prop = new CastProperty(name, CastPropertyType.Int32);
            prop.IntValues.AddRange(values);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetByteArray(string name, List<byte> values)
        {
            var prop = new CastProperty(name, CastPropertyType.Byte);
            prop.ByteValues.AddRange(values);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetVec3Array(string name, List<float> xyzFlat)
        {
            var prop = new CastProperty(name, CastPropertyType.Vector3);
            prop.FloatValues.AddRange(xyzFlat);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetVec4Array(string name, List<float> xyzwFlat)
        {
            var prop = new CastProperty(name, CastPropertyType.Vector4);
            prop.FloatValues.AddRange(xyzwFlat);
            Properties[name] = prop;
            return prop;
        }

        public CastProperty SetVec2Array(string name, List<float> xyFlat)
        {
            var prop = new CastProperty(name, CastPropertyType.Vector2);
            prop.FloatValues.AddRange(xyFlat);
            Properties[name] = prop;
            return prop;
        }

        public int ByteLength()
        {
            var result = 0x18;
            foreach (var prop in Properties.Values)
                result += prop.ByteLength();
            foreach (var child in Children)
                result += child.ByteLength();
            return result;
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Identifier);
            w.Write(ByteLength());
            w.Write(Hash);
            w.Write(Properties.Count);
            w.Write(Children.Count);
            foreach (var prop in Properties.Values)
                prop.Write(w);
            foreach (var child in Children)
                child.Write(w);
        }
    }

    internal enum CastPropertyType
    {
        Byte,
        Int16,
        Int32,
        Int64,
        Float,
        Double,
        String,
        Vector2,
        Vector3,
        Vector4
    }

    internal sealed class CastProperty
    {
        public string Name { get; }
        public CastPropertyType Type { get; }
        public string StringValue { get; set; }
        public List<byte> ByteValues { get; } = new List<byte>();
        public List<uint> IntValues { get; } = new List<uint>();
        public List<ulong> LongValues { get; } = new List<ulong>();
        public List<float> FloatValues { get; } = new List<float>();

        public CastProperty(string name, CastPropertyType type)
        {
            Name = name;
            Type = type;
        }

        private string TypeId
        {
            get
            {
                // Match official python cast.py identifiers (2v/3v/4v), not the README mnemonic.
                switch (Type)
                {
                    case CastPropertyType.Byte: return "b";
                    case CastPropertyType.Int16: return "h";
                    case CastPropertyType.Int32: return "i";
                    case CastPropertyType.Int64: return "l";
                    case CastPropertyType.Float: return "f";
                    case CastPropertyType.Double: return "d";
                    case CastPropertyType.String: return "s";
                    case CastPropertyType.Vector2: return "2v";
                    case CastPropertyType.Vector3: return "3v";
                    case CastPropertyType.Vector4: return "4v";
                    default: throw new ArgumentOutOfRangeException();
                }
            }
        }

        private int ElementSize
        {
            get
            {
                switch (Type)
                {
                    case CastPropertyType.Byte: return 1;
                    case CastPropertyType.Int16: return 2;
                    case CastPropertyType.Int32: return 4;
                    case CastPropertyType.Int64: return 8;
                    case CastPropertyType.Float: return 4;
                    case CastPropertyType.Double: return 8;
                    case CastPropertyType.String: return 0;
                    case CastPropertyType.Vector2: return 8;
                    case CastPropertyType.Vector3: return 12;
                    case CastPropertyType.Vector4: return 16;
                    default: throw new ArgumentOutOfRangeException();
                }
            }
        }

        private int Components
        {
            get
            {
                switch (Type)
                {
                    case CastPropertyType.Vector2: return 2;
                    case CastPropertyType.Vector3: return 3;
                    case CastPropertyType.Vector4: return 4;
                    default: return 1;
                }
            }
        }

        private int ArrayLength
        {
            get
            {
                if (Type == CastPropertyType.String)
                    return 1;
                if (Type == CastPropertyType.Byte)
                    return ByteValues.Count;
                if (Type == CastPropertyType.Int32)
                    return IntValues.Count;
                if (Type == CastPropertyType.Int64)
                    return LongValues.Count;
                return FloatValues.Count / Components;
            }
        }

        public int ByteLength()
        {
            var nameBytes = Encoding.UTF8.GetByteCount(Name);
            var result = 0x8 + nameBytes;
            if (Type == CastPropertyType.String)
                result += Encoding.UTF8.GetByteCount(StringValue ?? "") + 1;
            else
                result += ElementSize * ArrayLength;
            return result;
        }

        public void Write(BinaryWriter w)
        {
            var typeBytes = Encoding.ASCII.GetBytes(TypeId);
            if (typeBytes.Length == 1)
            {
                w.Write(typeBytes[0]);
                w.Write((byte)0);
            }
            else
            {
                w.Write(typeBytes[0]);
                w.Write(typeBytes[1]);
            }

            var nameBytes = Encoding.UTF8.GetBytes(Name);
            w.Write((ushort)nameBytes.Length);
            w.Write(ArrayLength);
            w.Write(nameBytes);

            if (Type == CastPropertyType.String)
            {
                var s = Encoding.UTF8.GetBytes(StringValue ?? "");
                w.Write(s);
                w.Write((byte)0);
                return;
            }

            switch (Type)
            {
                case CastPropertyType.Byte:
                    foreach (var v in ByteValues) w.Write(v);
                    break;
                case CastPropertyType.Int32:
                    foreach (var v in IntValues) w.Write(v);
                    break;
                case CastPropertyType.Int64:
                    foreach (var v in LongValues) w.Write(v);
                    break;
                case CastPropertyType.Float:
                case CastPropertyType.Vector2:
                case CastPropertyType.Vector3:
                case CastPropertyType.Vector4:
                    foreach (var v in FloatValues) w.Write(v);
                    break;
                default:
                    throw new NotSupportedException($"Cast property type {Type} not implemented for write");
            }
        }
    }

    internal static class CastIds
    {
        public const uint Root = 0x746F6F72;
        public const uint Model = 0x6C646F6D;
        public const uint Mesh = 0x6873656D;
        public const uint Skeleton = 0x6C656B73;
        public const uint Bone = 0x656E6F62;
        public const uint Animation = 0x6D696E61;
        public const uint Curve = 0x76727563;
        public const uint NotificationTrack = 0x6669746E;
        public const uint Material = 0x6C74616D;
        public const uint File = 0x656C6966;
        public const uint Metadata = 0x6174656D;
    }

    internal static class CastFile
    {
        public static void Save(string path, CastNode root)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(0x74736163u); // 'cast'
                w.Write(1u);          // version
                w.Write(1u);          // root count
                w.Write(0u);          // flags
                root.Write(w);
            }
        }
    }
}
