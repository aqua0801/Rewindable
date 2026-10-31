using System;
using System.Buffers.Binary;

namespace Rewindable
{
    /// <summary>Little-endian reader mirroring <see cref="SnapshotWriter"/>.</summary>
    public ref struct SnapshotReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _position;

        public SnapshotReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        /// <summary>Bytes consumed so far.</summary>
        public int Position => _position;

        public bool ReadBool() => _buffer[_position++] != 0;

        public byte ReadByte() => _buffer[_position++];

        public sbyte ReadSByte() => unchecked((sbyte)_buffer[_position++]);

        public char ReadChar() => (char)ReadUInt16();

        public short ReadInt16()
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position));
            _position += 2;
            return value;
        }

        public ushort ReadUInt16()
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position));
            _position += 2;
            return value;
        }

        public int ReadInt32()
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position));
            _position += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position));
            _position += 4;
            return value;
        }

        public long ReadInt64()
        {
            long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position));
            _position += 8;
            return value;
        }

        public ulong ReadUInt64()
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position));
            _position += 8;
            return value;
        }
    }
}
