using System;
using System.Collections.Generic;

namespace Rewindable
{
    /// <summary>FNV-1a 64-bit hash. Stable across platforms and runtimes.</summary>
    public static class RollbackChecksum
    {
        public const ulong OffsetBasis = 14695981039346656037UL;
        public const ulong Prime = 1099511628211UL;

        public static ulong Fnv1a64(ReadOnlySpan<byte> data)
        {
            ulong hash = OffsetBasis;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash = unchecked(hash * Prime);
            }
            return hash;
        }
    }

    /// <summary>Convenience entry points over generated <see cref="IRollbackState{T}"/> members.</summary>
    public static class RollbackSnapshot
    {
        private const int StackAllocLimit = 1024;

        /// <summary>Captures a snapshot into a new array.</summary>
        public static byte[] Capture<T>(in T state) where T : struct, IRollbackState<T>
        {
            byte[] bytes = new byte[state.SnapshotSize];
            Capture(in state, bytes);
            return bytes;
        }

        /// <summary>Captures a snapshot into <paramref name="destination"/>; returns bytes written.</summary>
        public static int Capture<T>(in T state, Span<byte> destination) where T : struct, IRollbackState<T>
        {
            int size = state.SnapshotSize;
            if (destination.Length < size)
                throw new ArgumentException("Destination is " + destination.Length + " bytes, snapshot needs " + size + ".", nameof(destination));

            var writer = new SnapshotWriter(destination.Slice(0, size));
            state.WriteTo(ref writer);
            return size;
        }

        /// <summary>Rebuilds a state from a snapshot.</summary>
        public static T Restore<T>(ReadOnlySpan<byte> snapshot) where T : struct, IRollbackState<T>
        {
            T state = default;
            Restore(snapshot, ref state);
            return state;
        }

        /// <summary>Overwrites <paramref name="state"/> from a snapshot. [NoRollback] fields are left untouched.</summary>
        public static void Restore<T>(ReadOnlySpan<byte> snapshot, ref T state) where T : struct, IRollbackState<T>
        {
            int size = state.SnapshotSize;
            if (snapshot.Length < size)
                throw new ArgumentException("Snapshot is " + snapshot.Length + " bytes, type needs " + size + ".", nameof(snapshot));

            var reader = new SnapshotReader(snapshot.Slice(0, size));
            state.ReadFrom(ref reader);
        }

        /// <summary>Checksum of the snapshot bytes. Fields marked [NoRollback] do not contribute.</summary>
        public static ulong Checksum<T>(in T state) where T : struct, IRollbackState<T>
        {
            int size = state.SnapshotSize;
            Span<byte> buffer = size <= StackAllocLimit ? stackalloc byte[size] : new byte[size];
            Capture(in state, buffer);
            return RollbackChecksum.Fnv1a64(buffer);
        }

        /// <summary>Returns every differing leaf field between two states.</summary>
        public static List<FieldDiff> Diff<T>(in T left, in T right) where T : struct, IRollbackState<T>
        {
            var collector = new DiffCollector();
            left.DiffAgainst(in right, string.Empty, collector);
            return collector.Diffs;
        }
    }
}
