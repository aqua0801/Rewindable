using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Rewindable.Generator;

internal static class Diagnostics
{
    private const string Category = "Rewindable";

    public static readonly DiagnosticDescriptor FloatingPointField = new(
        "RWD001", "Non-integer numeric field in rollback state",
        "Field '{0}' has type '{1}', which is not allowed in rollback state; use a fixed-point type, or mark it [NoRollback]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReferenceTypeField = new(
        "RWD002", "Reference-type field in rollback state",
        "Field '{0}' has reference type '{1}'; rollback state must be plain values (use a fixed-capacity struct, or mark it [NoRollback])",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnmarkedStructField = new(
        "RWD003", "Struct field is not rollback state",
        "Field '{0}' has struct type '{1}', which is not marked [RollbackState]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor FieldsInMultiplePartials = new(
        "RWD004", "Rollback fields split across partial declarations",
        "'{0}' declares rollback fields in more than one partial declaration; snapshot order follows file path then position, so renaming a file changes the snapshot layout",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AutoProperty = new(
        "RWD005", "Auto-property in rollback state",
        "Auto-property '{0}' is not supported in rollback state; use a field, or mark the backing field [field: NoRollback]",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReadOnlyMember = new(
        "RWD006", "Readonly rollback state cannot be restored",
        "'{0}' is readonly, so a snapshot cannot be restored into it",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor FixedPointShape = new(
        "RWD007", "Invalid [FixedPoint] struct",
        "[FixedPoint] on '{0}' requires exactly one short/int/long rollback field and 0..62 fractional bits",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotPartial = new(
        "RWD008", "Rollback state must be partial",
        "'{0}' must be declared partial (including all containing types) so members can be generated",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Unsupported = new(
        "RWD009", "Unsupported rollback state shape",
        "'{0}': {1} are not supported yet",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidRollbackValue = new(
        "RWD010", "Invalid [RollbackValue] type",
        "[RollbackValue] on '{0}' (used by field '{1}'): {2}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly ImmutableDictionary<string, DiagnosticDescriptor> ById = new[]
    {
        FloatingPointField, ReferenceTypeField, UnmarkedStructField, FieldsInMultiplePartials,
        AutoProperty, ReadOnlyMember, FixedPointShape, NotPartial, Unsupported, InvalidRollbackValue,
    }.ToImmutableDictionary(d => d.Id);
}
