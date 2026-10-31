#nullable enable
using System;
using System.Buffers.Binary;

namespace Rewindable
{
    /// <summary>Little-endian, allocation-free writer used by generated code.</summary>
    public ref struct SnapshotWriter
    {
        private readonly Span<byte> _buffer;
        private int _position;

        public SnapshotWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        /// <summary>Bytes written so far.</summary>
        public int Position => _position;

        public void WriteBool(bool value) => _buffer[_position++] = value ? (byte)1 : (byte)0;

        public void WriteByte(byte value) => _buffer[_position++] = value;

        public void WriteSByte(sbyte value) => _buffer[_position++] = unchecked((byte)value);

        public void WriteChar(char value) => WriteUInt16(value);

        public void WriteInt16(short value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position), value);
            _position += 2;
        }

        public void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position), value);
            _position += 2;
        }

        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position), value);
            _position += 4;
        }

        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_position), value);
            _position += 4;
        }

        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position), value);
            _position += 8;
        }

        public void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_position), value);
            _position += 8;
        }
    }
}
