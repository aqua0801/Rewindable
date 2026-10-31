using Microsoft.CodeAnalysis.CSharp;

namespace Rewindable.Generator.Tests;

public class FixedArrayDiagnosticTests
{
    private static GeneratorHarness.Result Run(string body, string extra = "", LanguageVersion version = LanguageVersion.Latest) =>
        GeneratorHarness.Run($$"""
            using Rewindable;
            namespace Game
            {
                public enum Mode : byte { A, B }
                [RollbackState] public partial struct Item { public int Id; }
                {{extra}}
                [RollbackState]
                public partial struct S
                {
                    {{body}}
                }
            }
            """, version);

    [Fact]
    public void ArraysOfEverySupportedElement_CompileUnderCSharp9()
    {
        var result = Run(
            "public FixedArray4<int> A; public FixedArray2<Mode> B; public FixedArray8<Item> C; public FixedArray16<bool> D;",
            version: LanguageVersion.CSharp9);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        string s = result.GeneratedSources.Single(g => g.Contains("partial struct S"));
        Assert.Contains("RollbackSnapshotSize = 4 * 4 + 2 * 1 + 8 * global::Game.Item.RollbackSnapshotSize + 16 * 1;", s);
        Assert.Contains("for (int i = 0; i < 8; i++)", s);
    }

    [Fact]
    public void FloatElements_RWD001() =>
        Assert.Contains("RWD001", Run("public FixedArray4<float> A;").Ids);

    [Fact]
    public void UnmarkedStructElements_RWD003() =>
        Assert.Contains("RWD003", Run("public FixedArray2<Plain> A;", "public struct Plain { public int X; }").Ids);

    [Fact]
    public void ArraysOfArrays_RWD009() =>
        Assert.Contains("RWD009", Run("public FixedArray2<FixedArray2<int>> A;").Ids);

    [Fact]
    public void CustomArrayType_IsSupported()
    {
        var result = Run("public Triple<int> A;", """
            [RollbackArray(3)]
            public struct Triple<T> where T : struct
            {
                private T _a, _b, _c;
                public T this[int i]
                {
                    get { return i == 0 ? _a : i == 1 ? _b : _c; }
                    set { if (i == 0) _a = value; else if (i == 1) _b = value; else _c = value; }
                }
            }
            """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("RollbackSnapshotSize = 3 * 4;", result.GeneratedSources.Single(g => g.Contains("partial struct S")));
    }

    [Theory]
    [InlineData("[RollbackArray(3)] public struct Bad<T> where T : struct { public T this[int i] { get { return default; } } }")]
    [InlineData("[RollbackArray(3)] public struct Bad<T> where T : struct { private T this[int i] { get { return default; } set { } } }")]
    [InlineData("[RollbackArray(0)] public struct Bad<T> where T : struct { public T this[int i] { get { return default; } set { } } }")]
    public void InvalidArrayType_RWD011(string arrayType)
    {
        var result = Run("public Bad<int> A;", arrayType);

        Assert.Contains("RWD011", result.Ids);
        Assert.DoesNotContain(result.GeneratedSources, g => g.Contains("partial struct S"));
    }
}
