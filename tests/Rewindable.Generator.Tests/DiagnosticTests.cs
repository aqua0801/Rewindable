namespace Rewindable.Generator.Tests;

public class DiagnosticTests
{
    private static GeneratorHarness.Result Run(string body, string extra = "") => GeneratorHarness.Run($$"""
        using Rewindable;
        namespace Game
        {
            {{extra}}
            [RollbackState]
            public partial struct S
            {
                {{body}}
            }
        }
        """);

    [Fact]
    public void ValidState_NoDiagnostics_AndCompiles()
    {
        var result = Run("public int Hp; public bool Grounded; public byte Frame;");

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Single(result.GeneratedSources);
    }

    [Theory]
    [InlineData("float")]
    [InlineData("double")]
    [InlineData("decimal")]
    public void FloatingPointField_RWD001(string type)
    {
        var result = Run($"public {type} X;");
        Assert.Contains("RWD001", result.Ids);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void FloatingPointField_WithNoRollback_IsAllowed()
    {
        var result = Run("public int Hp; [NoRollback] public float RenderLerp;");
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Theory]
    [InlineData("public string Name;")]
    [InlineData("public int[] Buffer;")]
    [InlineData("public object Boxed;")]
    public void ReferenceTypeField_RWD002(string body) =>
        Assert.Contains("RWD002", Run(body).Ids);

    [Fact]
    public void UnmarkedStructField_RWD003() =>
        Assert.Contains("RWD003", Run("public Plain P;", "public struct Plain { public int A; }").Ids);

    [Fact]
    public void FieldsInTwoPartials_RWD004_IsWarningAndStillGenerates()
    {
        var result = GeneratorHarness.Run("""
            using Rewindable;
            [RollbackState] public partial struct S { public int A; }
            public partial struct S { public int B; }
            """);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal("RWD004", diagnostic.Id);
        Assert.Single(result.GeneratedSources);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void AutoProperty_RWD005() =>
        Assert.Contains("RWD005", Run("public int Hp { get; set; }").Ids);

    [Fact]
    public void AutoProperty_WithFieldTargetedNoRollback_IsAllowed()
    {
        var result = Run("public int Hp; [field: NoRollback] public int Debug { get; set; }");
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
    }

    [Fact]
    public void ReadonlyField_RWD006() =>
        Assert.Contains("RWD006", Run("public readonly int Hp;").Ids);

    [Fact]
    public void ReadonlyStruct_RWD006()
    {
        var result = GeneratorHarness.Run("using Rewindable; [RollbackState] public readonly partial struct S { public readonly int A; }");
        Assert.Contains("RWD006", result.Ids);
    }

    [Theory]
    [InlineData("public int A; public int B;")]
    [InlineData("public byte A;")]
    public void FixedPoint_WrongShape_RWD007(string body)
    {
        var result = GeneratorHarness.Run($"using Rewindable; [RollbackState, FixedPoint(16)] public partial struct Fx {{ {body} }}");
        Assert.Contains("RWD007", result.Ids);
    }

    [Fact]
    public void NotPartial_RWD008() =>
        Assert.Contains("RWD008", GeneratorHarness.Run("using Rewindable; [RollbackState] public struct S { public int A; }").Ids);

    [Fact]
    public void NonPartialContainingType_RWD008() =>
        Assert.Contains("RWD008", GeneratorHarness.Run("using Rewindable; public class Outer { [RollbackState] public partial struct S { public int A; } }").Ids);

    [Fact]
    public void Generic_RWD009() =>
        Assert.Contains("RWD009", GeneratorHarness.Run("using Rewindable; [RollbackState] public partial struct S<T> where T : struct { public T A; }").Ids);

    [Fact]
    public void StaticAndConstFields_AreIgnored()
    {
        var result = Run("public int Hp; public static float Gravity; public const double Pi = 3.14;");
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Contains("RollbackSnapshotSize = 4;", result.GeneratedSources[0]);
    }
}
