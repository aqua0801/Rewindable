using System;

namespace Rewindable
{
    /// <summary>
    /// Marks a <c>partial struct</c> as rollback state. The generator emits
    /// <see cref="IRollbackState{T}"/> members: snapshot write / read, size and field-level diff.
    /// Every instance field must be an integer, bool, char, enum, or another [RollbackState] struct.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class RollbackStateAttribute : Attribute
    {
    }

    /// <summary>
    /// Excludes a field from snapshots, checksums and diffs (debug counters, render-only caches...).
    /// For auto-properties use <c>[field: NoRollback]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class NoRollbackAttribute : Attribute
    {
    }

    /// <summary>
    /// Declares a [RollbackState] struct as a fixed-point number wrapping exactly one
    /// <c>short</c>/<c>int</c>/<c>long</c> raw field. Only affects diff output, which shows
    /// the decimal value (e.g. <c>1.5 (raw 98304)</c>) instead of a nested raw field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class FixedPointAttribute : Attribute
    {
        /// <param name="fractionalBits">Number of fractional bits, e.g. 16 for Q16.16.</param>
        public FixedPointAttribute(int fractionalBits)
        {
            FractionalBits = fractionalBits;
        }

        /// <summary>Number of fractional bits.</summary>
        public int FractionalBits { get; }
    }
}
