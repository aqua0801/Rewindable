#nullable enable
namespace Rewindable
{
    /// <summary>
    /// Implemented by the generator for every [RollbackState] struct. Do not implement by hand.
    /// </summary>
    public interface IRollbackState<T> where T : struct, IRollbackState<T>
    {
        /// <summary>Bytes one snapshot occupies. Constant per type.</summary>
        int SnapshotSize { get; }

        /// <summary>Writes all rollback fields in declaration order (little-endian).</summary>
        void WriteTo(ref SnapshotWriter writer);

        /// <summary>Overwrites all rollback fields from a snapshot.</summary>
        void ReadFrom(ref SnapshotReader reader);

        /// <summary>Reports every differing leaf field to <paramref name="sink"/>.</summary>
        void DiffAgainst(in T other, string path, IDiffSink sink);

        /// <summary>
        /// Describes every leaf field (path, kind, display type) in snapshot order. Lets a snapshot be read without
        /// the type, e.g. by the dump tools. Use <see cref="SnapshotLayout.Of{T}"/> rather than calling it directly.
        /// </summary>
        void DescribeLayout(SnapshotLayoutBuilder builder, string path);
    }
}
