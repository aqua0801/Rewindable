using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Rewindable.Generator;

internal enum FieldKind
{
    Primitive,
    Enum,
    Nested,
    /// <summary>Immutable wrapper declared with [RollbackValue].</summary>
    Value,
}

/// <param name="Name">Field name as written (no '@').</param>
/// <param name="Suffix">Writer/reader method suffix, e.g. "Int32".</param>
/// <param name="Keyword">C# keyword of the primitive / enum underlying / raw type, e.g. "int".</param>
/// <param name="TypeName">Fully qualified field type.</param>
/// <param name="Size">Byte size for primitives, enums and values; 0 for nested.</param>
/// <param name="RawMember">[RollbackValue] raw member name (escaped), else empty.</param>
/// <param name="Factory">[RollbackValue] factory method name (escaped), else empty.</param>
/// <param name="FixedPointBits">Fractional bits for a [FixedPoint] value, else -1.</param>
/// <param name="DisplayType">Type name shown in layouts (keyword for primitives, e.g. "Game.ActionId" otherwise).</param>
/// <param name="EnumValues">Enum member values (as long), parallel to <paramref name="EnumNames"/>.</param>
/// <param name="EnumNames">Enum member names.</param>
internal sealed record FieldModel(
    string Name,
    FieldKind Kind,
    string Suffix,
    string Keyword,
    string TypeName,
    int Size,
    string RawMember = "",
    string Factory = "",
    int FixedPointBits = -1,
    string DisplayType = "",
    EquatableArray<long> EnumValues = default,
    EquatableArray<string> EnumNames = default);

internal sealed record ContainingTypeModel(string Keyword, string Name);

internal sealed record TypeModel(
    string? Namespace,
    string Keyword,
    string Name,
    string FullName,
    string DisplayName,
    string HintName,
    EquatableArray<ContainingTypeModel> ContainingTypes,
    EquatableArray<FieldModel> Fields,
    int FixedPointBits);

internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(Location? location)
    {
        if (location is null || !location.IsInSource)
            return null;
        return new LocationInfo(location.SourceTree?.FilePath ?? string.Empty, location.SourceSpan, location.GetLineSpan().Span);
    }
}

internal sealed record DiagnosticInfo(string Id, LocationInfo? Location, EquatableArray<string> Args)
{
    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(Diagnostics.ById[Id], Location?.ToLocation(), Args.Cast<object>().ToArray());
}

internal sealed record GenerationResult(TypeModel? Model, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>Immutable array with value equality, so incremental pipeline caching works.</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    public int Count => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other) =>
        (_items ?? Array.Empty<T>()).SequenceEqual(other._items ?? Array.Empty<T>());

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_items is null)
            return 0;
        unchecked
        {
            int hash = 17;
            foreach (T item in _items)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
