using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rewindable.Generator;

internal static class ModelBuilder
{
    public const string RollbackStateAttribute = "Rewindable.RollbackStateAttribute";
    private const string NoRollbackAttribute = "Rewindable.NoRollbackAttribute";
    private const string FixedPointAttribute = "Rewindable.FixedPointAttribute";
    private const string RollbackValueAttribute = "Rewindable.RollbackValueAttribute";

    public static GenerationResult Build(GeneratorAttributeSyntaxContext context, CancellationToken ct)
    {
        var symbol = (INamedTypeSymbol)context.TargetSymbol;
        var typeLocation = ((TypeDeclarationSyntax)context.TargetNode).Identifier.GetLocation();
        string display = symbol.ToDisplayString();

        var diagnostics = new List<DiagnosticInfo>();
        bool hasError = false;

        void Report(DiagnosticDescriptor descriptor, Location? location, params string[] args)
        {
            diagnostics.Add(new DiagnosticInfo(descriptor.Id, LocationInfo.From(location), new EquatableArray<string>(args)));
            if (descriptor.DefaultSeverity == DiagnosticSeverity.Error)
                hasError = true;
        }

        GenerationResult Fail() => new(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));

        // ---- type shape ----------------------------------------------------
        for (INamedTypeSymbol? t = symbol; t is not null; t = t.ContainingType)
        {
            if (!IsPartial(t, ct))
                Report(Diagnostics.NotPartial, typeLocation, t.ToDisplayString());
            if (t.IsGenericType)
                Report(Diagnostics.Unsupported, typeLocation, t.ToDisplayString(), "generic types");
        }

        if (symbol.IsRefLikeType)
            Report(Diagnostics.Unsupported, typeLocation, display, "ref structs");
        if (symbol.IsReadOnly)
            Report(Diagnostics.ReadOnlyMember, typeLocation, display);

        if (hasError)
            return Fail();

        // ---- fields ----------------------------------------------------------
        var candidates = symbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst)
            .Select(f => (Field: f, Location: FieldLocation(f)))
            .OrderBy(x => x.Location?.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.Location?.SourceSpan.Start ?? 0)
            .ToList();

        var fields = new List<FieldModel>();
        var declaringParts = new HashSet<SyntaxNode>();

        foreach (var (field, location) in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (HasAttribute(field, NoRollbackAttribute))
                continue;

            if (field.AssociatedSymbol is IPropertySymbol property)
            {
                Report(Diagnostics.AutoProperty, location, property.Name);
                continue;
            }

            if (field.AssociatedSymbol is IEventSymbol evt)
            {
                Report(Diagnostics.ReferenceTypeField, location, evt.Name, field.Type.ToDisplayString());
                continue;
            }

            if (field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct).FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } part)
                declaringParts.Add(part);

            if (field.IsReadOnly)
            {
                Report(Diagnostics.ReadOnlyMember, location, field.Name);
                continue;
            }

            if (field.IsFixedSizeBuffer)
            {
                Report(Diagnostics.Unsupported, location, field.Name, "fixed-size buffers");
                continue;
            }

            FieldModel? model = Classify(field, location, symbol, context.SemanticModel.Compilation, Report);
            if (model is not null)
                fields.Add(model);
        }

        if (declaringParts.Count > 1)
            Report(Diagnostics.FieldsInMultiplePartials, typeLocation, display);

        // ---- [FixedPoint] ----------------------------------------------------
        int fixedPointBits = -1;
        AttributeData? fixedPoint = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == FixedPointAttribute);
        if (fixedPoint is not null)
        {
            fixedPointBits = fixedPoint.ConstructorArguments.Length == 1 && fixedPoint.ConstructorArguments[0].Value is int bits ? bits : -1;
            bool validShape = fields.Count == 1
                && fields[0].Kind == FieldKind.Primitive
                && fields[0].Suffix is "Int16" or "Int32" or "Int64";
            if (!validShape || fixedPointBits < 0 || fixedPointBits > 62)
                Report(Diagnostics.FixedPointShape, typeLocation, display);
        }

        if (hasError)
            return Fail();

        // ---- model -----------------------------------------------------------
        var containing = new List<ContainingTypeModel>();
        for (INamedTypeSymbol? t = symbol.ContainingType; t is not null; t = t.ContainingType)
            containing.Insert(0, new ContainingTypeModel(TypeKeyword(t), Escape(t.Name)));

        var model2 = new TypeModel(
            Namespace: symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
            Keyword: TypeKeyword(symbol),
            Name: Escape(symbol.Name),
            FullName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            DisplayName: display,
            HintName: HintName(symbol),
            ContainingTypes: new EquatableArray<ContainingTypeModel>(containing.ToArray()),
            Fields: new EquatableArray<FieldModel>(fields.ToArray()),
            FixedPointBits: fixedPointBits);

        return new GenerationResult(model2, new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));
    }

    private static FieldModel? Classify(
        IFieldSymbol field,
        Location? location,
        INamedTypeSymbol owner,
        Compilation compilation,
        Action<DiagnosticDescriptor, Location?, string[]> report)
    {
        ITypeSymbol type = field.Type;
        string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (type.TypeKind == TypeKind.Struct
            && type is INamedTypeSymbol named
            && FindAttribute(named.OriginalDefinition, RollbackValueAttribute) is { } valueAttribute)
        {
            return ClassifyValue(field, location, named, valueAttribute, owner, compilation, report);
        }

        if (TryPrimitive(type.SpecialType, out var primitive))
            return new FieldModel(field.Name, FieldKind.Primitive, primitive.Suffix, primitive.Keyword, typeName, primitive.Size,
                DisplayType: primitive.Keyword);

        switch (type.SpecialType)
        {
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                report(Diagnostics.FloatingPointField, location, new[] { field.Name, type.ToDisplayString() });
                return null;
            case SpecialType.System_IntPtr:
            case SpecialType.System_UIntPtr:
                report(Diagnostics.Unsupported, location, new[] { field.Name, "pointer-sized integers (nint/nuint)" });
                return null;
        }

        if (type.TypeKind == TypeKind.Enum
            && type is INamedTypeSymbol { EnumUnderlyingType: { } underlying } enumType
            && TryPrimitive(underlying.SpecialType, out var enumPrimitive))
        {
            IFieldSymbol[] members = enumType.GetMembers().OfType<IFieldSymbol>().Where(m => m.IsStatic && m.HasConstantValue).ToArray();
            long[] values = members.Select(m => m.ConstantValue is ulong u
                ? unchecked((long)u)
                : Convert.ToInt64(m.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return new FieldModel(field.Name, FieldKind.Enum, enumPrimitive.Suffix, enumPrimitive.Keyword, typeName, enumPrimitive.Size,
                DisplayType: type.ToDisplayString(),
                EnumValues: new EquatableArray<long>(values),
                EnumNames: new EquatableArray<string>(members.Select(m => m.Name).ToArray()));
        }

        if (type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer)
        {
            report(Diagnostics.Unsupported, location, new[] { field.Name, "pointers" });
            return null;
        }

        if (type.IsReferenceType || type.TypeKind == TypeKind.TypeParameter)
        {
            report(Diagnostics.ReferenceTypeField, location, new[] { field.Name, type.ToDisplayString() });
            return null;
        }

        if (type.TypeKind == TypeKind.Struct)
        {
            if (HasAttribute(type.OriginalDefinition, RollbackStateAttribute))
                return new FieldModel(field.Name, FieldKind.Nested, string.Empty, string.Empty, typeName, 0);

            report(Diagnostics.UnmarkedStructField, location, new[] { field.Name, type.ToDisplayString() });
            return null;
        }

        report(Diagnostics.Unsupported, location, new[] { field.Name, type.TypeKind + " fields" });
        return null;
    }

    private static FieldModel? ClassifyValue(
        IFieldSymbol field,
        Location? location,
        INamedTypeSymbol valueType,
        AttributeData attribute,
        INamedTypeSymbol owner,
        Compilation compilation,
        Action<DiagnosticDescriptor, Location?, string[]> report)
    {
        string display = valueType.ToDisplayString();

        FieldModel? Bad(string reason)
        {
            report(Diagnostics.InvalidRollbackValue, location, new[] { display, field.Name, reason });
            return null;
        }

        if (attribute.ConstructorArguments.Length != 2
            || attribute.ConstructorArguments[0].Value is not string rawName
            || attribute.ConstructorArguments[1].Value is not string factoryName)
        {
            return Bad("expected (rawMember, factoryMethod)");
        }

        ITypeSymbol? rawType = null;
        ISymbol? rawAccessor = null;
        foreach (ISymbol member in valueType.GetMembers(rawName))
        {
            if (member is IFieldSymbol { IsStatic: false } rawField)
            {
                rawType = rawField.Type;
                rawAccessor = rawField;
                break;
            }
            if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, GetMethod: { } getter } rawProperty)
            {
                rawType = rawProperty.Type;
                rawAccessor = getter;
                break;
            }
        }

        if (rawType is null || rawAccessor is null)
            return Bad("no instance field or readable property named '" + rawName + "'");
        if (!compilation.IsSymbolAccessibleWithin(rawAccessor, owner))
            return Bad("'" + rawName + "' is not accessible from '" + owner.ToDisplayString() + "'");
        if (!TryPrimitive(rawType.SpecialType, out var primitive))
            return Bad("'" + rawName + "' must be an integer, bool or char, not '" + rawType.ToDisplayString() + "'");

        IMethodSymbol? factory = valueType.GetMembers(factoryName)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.IsStatic
                && !m.IsGenericMethod
                && m.Parameters.Length == 1
                && m.Parameters[0].RefKind == RefKind.None
                && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, rawType)
                && SymbolEqualityComparer.Default.Equals(m.ReturnType, valueType));

        if (factory is null)
            return Bad("no static method '" + factoryName + "(" + primitive.Keyword + ")' returning '" + valueType.Name + "'");
        if (!compilation.IsSymbolAccessibleWithin(factory, owner))
            return Bad("'" + factoryName + "' is not accessible from '" + owner.ToDisplayString() + "'");

        int fixedPointBits = -1;
        if (FindAttribute(valueType.OriginalDefinition, FixedPointAttribute) is { } fixedPoint
            && fixedPoint.ConstructorArguments.Length == 1
            && fixedPoint.ConstructorArguments[0].Value is int bits
            && bits is >= 0 and <= 62
            && primitive.Suffix is "Int16" or "Int32" or "Int64")
        {
            fixedPointBits = bits;
        }

        return new FieldModel(
            field.Name,
            FieldKind.Value,
            primitive.Suffix,
            primitive.Keyword,
            valueType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            primitive.Size,
            Escape(rawName),
            Escape(factoryName),
            fixedPointBits,
            DisplayType: valueType.ToDisplayString());
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullName);

    private static bool TryPrimitive(SpecialType specialType, out (string Suffix, string Keyword, int Size) primitive)
    {
        primitive = specialType switch
        {
            SpecialType.System_Boolean => ("Bool", "bool", 1),
            SpecialType.System_Char => ("Char", "char", 2),
            SpecialType.System_SByte => ("SByte", "sbyte", 1),
            SpecialType.System_Byte => ("Byte", "byte", 1),
            SpecialType.System_Int16 => ("Int16", "short", 2),
            SpecialType.System_UInt16 => ("UInt16", "ushort", 2),
            SpecialType.System_Int32 => ("Int32", "int", 4),
            SpecialType.System_UInt32 => ("UInt32", "uint", 4),
            SpecialType.System_Int64 => ("Int64", "long", 8),
            SpecialType.System_UInt64 => ("UInt64", "ulong", 8),
            _ => (string.Empty, string.Empty, 0),
        };
        return primitive.Size != 0;
    }

    private static Location? FieldLocation(IFieldSymbol field)
    {
        ISymbol source = field.AssociatedSymbol ?? field;
        return source.Locations.FirstOrDefault(l => l.IsInSource);
    }

    private static bool HasAttribute(ISymbol symbol, string fullName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    private static bool IsPartial(INamedTypeSymbol type, CancellationToken ct) =>
        type.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax(ct) is TypeDeclarationSyntax declaration
            && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

    private static string TypeKeyword(INamedTypeSymbol type)
    {
        if (type.IsRecord)
            return type.TypeKind == TypeKind.Struct ? "record struct" : "record";
        return type.TypeKind switch
        {
            TypeKind.Struct => "struct",
            TypeKind.Interface => "interface",
            _ => "class",
        };
    }

    internal static string Escape(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;

    private static string HintName(INamedTypeSymbol symbol)
    {
        var chars = symbol.ToDisplayString().Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '_').ToArray();
        return new string(chars) + ".Rollback.g.cs";
    }
}
