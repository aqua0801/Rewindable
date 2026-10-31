using System;

namespace Rewindable
{
    /// <summary>
    /// Fixed-capacity ring of snapshots indexed by frame number. All memory is allocated
    /// up front; <see cref="Save"/> and <see cref="TryLoad"/> do not allocate.
    /// </summary>
    public sealed class SnapshotRing<T> where T : struct, IRollbackState<T>
    {
        private const int EmptyFrame = int.MinValue;

        private readonly byte[] _data;
        private readonly int[] _frames;
        private readonly ulong[] _checksums;
        private readonly int _stride;

        public SnapshotRing(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

            Capacity = capacity;
            _stride = default(T).SnapshotSize;
            _data = new byte[checked(capacity * _stride)];
            _frames = new int[capacity];
            _checksums = new ulong[capacity];
            Clear();
        }

        /// <summary>Number of frames kept.</summary>
        public int Capacity { get; }

        /// <summary>Bytes per snapshot.</summary>
        public int Stride => _stride;

        /// <summary>Stores <paramref name="state"/> for <paramref name="frame"/>, overwriting the oldest slot. Returns its checksum.</summary>
        public ulong Save(int frame, in T state)
        {
            if (frame == EmptyFrame)
                throw new ArgumentOutOfRangeException(nameof(frame), "int.MinValue is reserved.");

            int slot = Slot(frame);
            Span<byte> span = _data.AsSpan(slot * _stride, _stride);
            var writer = new SnapshotWriter(span);
            state.WriteTo(ref writer);

            ulong checksum = RollbackChecksum.Fnv1a64(span);
            _frames[slot] = frame;
            _checksums[slot] = checksum;
            return checksum;
        }

        /// <summary>Overwrites <paramref name="state"/> with the snapshot of <paramref name="frame"/> if still held.</summary>
        public bool TryLoad(int frame, ref T state)
        {
            int slot = Slot(frame);
            if (_frames[slot] != frame)
                return false;

            var reader = new SnapshotReader(_data.AsSpan(slot * _stride, _stride));
            state.ReadFrom(ref reader);
            return true;
        }

        /// <summary>Checksum recorded when <paramref name="frame"/> was saved.</summary>
        public bool TryGetChecksum(int frame, out ulong checksum)
        {
            int slot = Slot(frame);
            if (_frames[slot] != frame)
            {
                checksum = 0;
                return false;
            }

            checksum = _checksums[slot];
            return true;
        }

        /// <summary>Raw snapshot bytes of <paramref name="frame"/> (for desync dumps), or empty if not held.</summary>
        public ReadOnlySpan<byte> GetSnapshot(int frame)
        {
            int slot = Slot(frame);
            return _frames[slot] == frame ? _data.AsSpan(slot * _stride, _stride) : ReadOnlySpan<byte>.Empty;
        }

        public bool Contains(int frame) => _frames[Slot(frame)] == frame;

        public void Clear()
        {
            for (int i = 0; i < _frames.Length; i++)
                _frames[i] = EmptyFrame;
        }

        private int Slot(int frame)
        {
            int m = frame % Capacity;
            return m < 0 ? m + Capacity : m;
        }
    }
}
