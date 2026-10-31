using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Rewindable.Generator.Tests;

internal static class GeneratorHarness
{
    private static readonly MetadataReference[] References = BuildReferences();

    public sealed record Result(
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompilationErrors,
        ImmutableArray<string> GeneratedSources)
    {
        public IEnumerable<string> Ids => GeneratorDiagnostics.Select(d => d.Id);
    }

    public static Result Run(string source, LanguageVersion languageVersion = LanguageVersion.Latest)
    {
        var parseOptions = new CSharpParseOptions(languageVersion);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "Test.cs");
        var compilation = CSharpCompilation.Create(
            "GeneratorTest",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new RollbackStateGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var generated = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).ToImmutableArray();
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return new Result(generatorDiagnostics, errors, generated);
    }

    private static MetadataReference[] BuildReferences()
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return tpa
            .Where(p =>
            {
                string name = Path.GetFileName(p);
                return name.StartsWith("System.", StringComparison.Ordinal)
                    || name is "System.dll" or "netstandard.dll" or "mscorlib.dll";
            })
            .Append(typeof(RollbackStateAttribute).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }
}
