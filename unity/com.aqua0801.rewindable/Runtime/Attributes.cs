#nullable enable
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
    /// Lets an immutable (<c>readonly struct</c>) value wrapper be used as a rollback field without
    /// changing its design. The snapshot stores the raw member; restore calls the static
    /// factory method. <c>factory(value.raw)</c> must reproduce <c>value</c>.
    /// Combine with <see cref="FixedPointAttribute"/> to get decimal diff output.
    /// </summary>
    /// <example><c>[RollbackValue(nameof(RawValue), nameof(FromRaw)), FixedPoint(16)] public readonly struct Fixed { ... }</c></example>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class RollbackValueAttribute : Attribute
    {
        /// <param name="rawMember">Instance field or property of an integer/bool/char type holding the whole value.</param>
        /// <param name="factoryMethod">Static method taking that raw type and returning the wrapper.</param>
        public RollbackValueAttribute(string rawMember, string factoryMethod)
        {
            RawMember = rawMember;
            FactoryMethod = factoryMethod;
        }

        public string RawMember { get; }

        public string FactoryMethod { get; }
    }

    /// <summary>
    /// Marks a generic struct <c>Name&lt;T&gt;</c> as a fixed-capacity array of the given number of elements, usable
    /// as a [RollbackState] field. The type needs a public <c>T this[int]</c> indexer with get and set; the generator
    /// visits elements 0..length-1 through it. <see cref="FixedArray4{T}"/> and its siblings are ready-made ones.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class RollbackArrayAttribute : Attribute
    {
        /// <param name="length">Number of elements, at least 1.</param>
        public RollbackArrayAttribute(int length)
        {
            Length = length;
        }

        public int Length { get; }
    }

    /// <summary>
    /// Declares a [RollbackState] (or [RollbackValue]) struct as a fixed-point number wrapping exactly one
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
