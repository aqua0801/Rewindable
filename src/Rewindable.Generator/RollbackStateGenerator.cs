using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Rewindable.Generator;

/// <summary>
/// Generates <c>IRollbackState&lt;T&gt;</c> implementations for structs marked
/// <c>[Rewindable.RollbackState]</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RollbackStateGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<GenerationResult> results = context.SyntaxProvider.ForAttributeWithMetadataName(
            ModelBuilder.RollbackStateAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, ct) => ModelBuilder.Build(ctx, ct));

        context.RegisterSourceOutput(results, static (spc, result) =>
        {
            foreach (DiagnosticInfo diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (result.Model is { } model)
                spc.AddSource(model.HintName, SourceText.From(Emitter.Emit(model), Encoding.UTF8));
        });
    }
}
