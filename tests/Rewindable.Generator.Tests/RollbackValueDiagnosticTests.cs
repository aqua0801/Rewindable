namespace Rewindable.Generator.Tests;

public class RollbackValueDiagnosticTests
{
    private static GeneratorHarness.Result Run(string valueType) => GeneratorHarness.Run($$"""
        using Rewindable;
        namespace Game
        {
            {{valueType}}

            [RollbackState]
            public partial struct S { public V X; public int Hp; }
        }
        """);

    [Fact]
    public void ValidReadonlyWrapper_GeneratesAndCompiles()
    {
        var result = Run("""
            [RollbackValue(nameof(Raw), nameof(FromRaw))]
            public readonly struct V { private V(int r) => Raw = r; public int Raw { get; } public static V FromRaw(int r) => new V(r); }
            """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Contains("this.X = global::Game.V.FromRaw(reader.ReadInt32());", result.GeneratedSources[0]);
    }

    [Fact]
    public void InternalMembers_AreAccessibleWithinTheAssembly()
    {
        var result = Run("""
            [RollbackValue("Raw", "Make")]
            public readonly struct V { internal readonly short Raw; private V(short r) => Raw = r; internal static V Make(short r) => new V(r); }
            """);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("""[RollbackValue("Missing", "FromRaw")] public readonly struct V { public readonly int Raw; public static V FromRaw(int r) => default; }""")]
    [InlineData("""[RollbackValue("Raw", "Missing")] public readonly struct V { public readonly int Raw; }""")]
    [InlineData("""[RollbackValue("Raw", "FromRaw")] public readonly struct V { public readonly int Raw; public static V FromRaw(long r) => default; }""")]
    [InlineData("""[RollbackValue("Raw", "FromRaw")] public readonly struct V { private readonly int Raw; public static V FromRaw(int r) => default; }""")]
    [InlineData("""[RollbackValue("Raw", "FromRaw")] public readonly struct V { public readonly float Raw; public static V FromRaw(float r) => default; }""")]
    [InlineData("""[RollbackValue("Raw", "FromRaw")] public readonly struct V { public readonly int Raw; public V FromRaw(int r) => default; }""")]
    public void InvalidWrapper_RWD010(string valueType)
    {
        var result = Run(valueType);

        Assert.Contains("RWD010", result.Ids);
        Assert.Empty(result.GeneratedSources);
    }
}
