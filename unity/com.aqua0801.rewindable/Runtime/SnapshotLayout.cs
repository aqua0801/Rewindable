#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Rewindable
{
    /// <summary>Primitive encoding of one snapshot leaf (little-endian).</summary>
    public enum LeafKind : byte
    {
        Bool = 1,
        Char = 2,
        SByte = 3,
        Byte = 4,
        Int16 = 5,
        UInt16 = 6,
        Int32 = 7,
        UInt32 = 8,
        Int64 = 9,
        UInt64 = 10,
    }

    /// <summary>One leaf field of a snapshot: where it is and how to show it.</summary>
    public sealed class LayoutField
    {
        internal LayoutField(string path, LeafKind kind, int offset, string typeName, int fixedPointBits,
            long[]? enumValues, string[]? enumNames)
        {
            Path = path;
            Kind = kind;
            Offset = offset;
            Size = SizeOf(kind);
            TypeName = typeName;
            FixedPointBits = fixedPointBits;
            EnumValues = enumValues;
            EnumNames = enumNames;
        }

        public string Path { get; }
        public LeafKind Kind { get; }
        public int Offset { get; }
        public int Size { get; }

        /// <summary>Display type, e.g. <c>int</c>, <c>Game.ActionId</c>, <c>Game.Fixed</c>.</summary>
        public string TypeName { get; }

        /// <summary>Fractional bits for fixed-point leaves, else -1.</summary>
        public int FixedPointBits { get; }

        public long[]? EnumValues { get; }
        public string[]? EnumNames { get; }

        public bool IsEnum => EnumValues != null;

        public static int SizeOf(LeafKind kind)
        {
            switch (kind)
            {
                case LeafKind.Bool:
                case LeafKind.SByte:
                case LeafKind.Byte:
                    return 1;
                case LeafKind.Char:
                case LeafKind.Int16:
                case LeafKind.UInt16:
                    return 2;
                case LeafKind.Int32:
                case LeafKind.UInt32:
                    return 4;
                case LeafKind.Int64:
                case LeafKind.UInt64:
                    return 8;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown leaf kind.");
            }
        }

        /// <summary>The leaf's bytes as a signed value (unsigned 64-bit values are reinterpreted).</summary>
        public long ReadSigned(ReadOnlySpan<byte> snapshot)
        {
            ReadOnlySpan<byte> b = snapshot.Slice(Offset, Size);
            switch (Kind)
            {
                case LeafKind.Bool:
                case LeafKind.Byte: return b[0];
                case LeafKind.SByte: return unchecked((sbyte)b[0]);
                case LeafKind.Char:
                case LeafKind.UInt16: return BinaryPrimitives.ReadUInt16LittleEndian(b);
                case LeafKind.Int16: return BinaryPrimitives.ReadInt16LittleEndian(b);
                case LeafKind.Int32: return BinaryPrimitives.ReadInt32LittleEndian(b);
                case LeafKind.UInt32: return BinaryPrimitives.ReadUInt32LittleEndian(b);
                case LeafKind.Int64: return BinaryPrimitives.ReadInt64LittleEndian(b);
                default: return unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(b));
            }
        }

        /// <summary>Human-readable value, matching the typed diff where possible.</summary>
        public string Format(ReadOnlySpan<byte> snapshot)
        {
            long value = ReadSigned(snapshot);
            switch (Kind)
            {
                case LeafKind.Bool:
                    return value != 0 ? "true" : "false";
                case LeafKind.Char:
                    return RollbackFormat.Of((char)value);
                case LeafKind.UInt64:
                    if (!IsEnum) return unchecked((ulong)value).ToString(CultureInfo.InvariantCulture);
                    break;
            }

            if (FixedPointBits >= 0)
                return RollbackFormat.FixedPoint(value, FixedPointBits);

            if (EnumValues != null && EnumNames != null)
            {
                for (int i = 0; i < EnumValues.Length; i++)
                    if (EnumValues[i] == value)
                        return EnumNames[i];
                return value.ToString(CultureInfo.InvariantCulture) + " (no " + TypeName + " member)";
            }

            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal bool SameShape(LayoutField other) =>
            Path == other.Path && Kind == other.Kind && Offset == other.Offset;
    }

    /// <summary>Collects leaves in snapshot order. Used by generated <c>DescribeLayout</c>.</summary>
    public sealed class SnapshotLayoutBuilder
    {
        private readonly List<LayoutField> _fields = new List<LayoutField>();
        private int _offset;

        public void Add(string path, LeafKind kind, string typeName, int fixedPointBits)
        {
            Append(new LayoutField(RollbackFormat.Path(path), kind, _offset, typeName, fixedPointBits, null, null));
        }

        public void AddEnum(string path, LeafKind kind, string typeName, long[] values, string[] names)
        {
            if (values.Length != names.Length)
                throw new ArgumentException("Enum values and names must have the same length.");
            Append(new LayoutField(RollbackFormat.Path(path), kind, _offset, typeName, -1, values, names));
        }

        public SnapshotLayout Build(string typeName) => new SnapshotLayout(typeName, _fields.ToArray());

        private void Append(LayoutField field)
        {
            _fields.Add(field);
            _offset += field.Size;
        }
    }

    /// <summary>
    /// Self-description of a snapshot: every leaf with its offset, type and formatting. Lets snapshots be compared
    /// and printed without the original type (dump files, CLI).
    /// </summary>
    public sealed class SnapshotLayout
    {
        private readonly LayoutField[] _fields;

        internal SnapshotLayout(string typeName, LayoutField[] fields)
        {
            TypeName = typeName;
            _fields = fields;
            int size = 0;
            foreach (LayoutField f in fields)
                size += f.Size;
            Size = size;
        }

        public string TypeName { get; }

        /// <summary>Total snapshot bytes.</summary>
        public int Size { get; }

        public IReadOnlyList<LayoutField> Fields => _fields;

        /// <summary>The layout of <typeparamref name="T"/>, built once and cached.</summary>
        public static SnapshotLayout Of<T>() where T : struct, IRollbackState<T> => Cache<T>.Value;

        /// <summary>Reports every leaf whose bytes differ.</summary>
        public void Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, IDiffSink sink)
        {
            CheckLength(left, nameof(left));
            CheckLength(right, nameof(right));
            foreach (LayoutField f in _fields)
            {
                if (!left.Slice(f.Offset, f.Size).SequenceEqual(right.Slice(f.Offset, f.Size)))
                    sink.Report(f.Path, f.Format(left), f.Format(right));
            }
        }

        public List<FieldDiff> Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            var collector = new DiffCollector();
            Compare(left, right, collector);
            return collector.Diffs;
        }

        /// <summary>Same type name and same leaves at the same offsets.</summary>
        public bool IsCompatibleWith(SnapshotLayout other, out string problem)
        {
            if (TypeName != other.TypeName)
            {
                problem = "type " + TypeName + " vs " + other.TypeName;
                return false;
            }
            if (_fields.Length != other._fields.Length)
            {
                problem = _fields.Length + " fields vs " + other._fields.Length;
                return false;
            }
            for (int i = 0; i < _fields.Length; i++)
            {
                if (!_fields[i].SameShape(other._fields[i]))
                {
                    problem = "field #" + i + ": " + _fields[i].Path + " vs " + other._fields[i].Path;
                    return false;
                }
            }
            problem = string.Empty;
            return true;
        }

        internal void WriteTo(BinaryWriter w)
        {
            w.Write(TypeName);
            w.Write(_fields.Length);
            foreach (LayoutField f in _fields)
            {
                w.Write(f.Path);
                w.Write((byte)f.Kind);
                w.Write(f.TypeName);
                w.Write(f.FixedPointBits);
                int enumCount = f.EnumValues?.Length ?? -1;
                w.Write(enumCount);
                for (int i = 0; i < enumCount; i++)
                {
                    w.Write(f.EnumValues![i]);
                    w.Write(f.EnumNames![i]);
                }
            }
        }

        internal static SnapshotLayout ReadFrom(BinaryReader r)
        {
            string typeName = r.ReadString();
            int count = r.ReadInt32();
            if (count < 0 || count > 1_000_000)
                throw new InvalidDataException("Bad field count " + count + ".");

            var builder = new SnapshotLayoutBuilder();
            for (int i = 0; i < count; i++)
            {
                string path = r.ReadString();
                var kind = (LeafKind)r.ReadByte();
                LayoutField.SizeOf(kind); // validates
                string fieldType = r.ReadString();
                int bits = r.ReadInt32();
                int enumCount = r.ReadInt32();
                if (enumCount < 0)
                {
                    builder.Add(path, kind, fieldType, bits);
                    continue;
                }
                var values = new long[enumCount];
                var names = new string[enumCount];
                for (int e = 0; e < enumCount; e++)
                {
                    values[e] = r.ReadInt64();
                    names[e] = r.ReadString();
                }
                builder.AddEnum(path, kind, fieldType, values, names);
            }
            return builder.Build(typeName);
        }

        private void CheckLength(ReadOnlySpan<byte> snapshot, string name)
        {
            if (snapshot.Length < Size)
                throw new ArgumentException("Snapshot is " + snapshot.Length + " bytes, layout needs " + Size + ".", name);
        }

        private static class Cache<T> where T : struct, IRollbackState<T>
        {
            public static readonly SnapshotLayout Value = Build();

            private static SnapshotLayout Build()
            {
                var builder = new SnapshotLayoutBuilder();
                T state = default;
                state.DescribeLayout(builder, string.Empty);
                SnapshotLayout layout = builder.Build(typeof(T).FullName ?? typeof(T).Name);
                if (layout.Size != state.SnapshotSize)
                    throw new InvalidOperationException("Layout of " + typeof(T) + " covers " + layout.Size
                        + " bytes but its snapshot is " + state.SnapshotSize + "; regenerate the type.");
                return layout;
            }
        }
    }
}
