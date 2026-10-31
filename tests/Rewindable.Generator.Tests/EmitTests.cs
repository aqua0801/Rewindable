using Microsoft.CodeAnalysis.CSharp;

namespace Rewindable.Generator.Tests;

public class EmitTests
{
    // Unity compiles user code with C# 9: generated code must not need anything newer.
    private const string UnityStyleSource = """
        using Rewindable;

        namespace Game.Sim
        {
            [RollbackState, FixedPoint(16)]
            public partial struct Fixed { public int Raw; }

            public enum ActionId : ushort { Idle, Walk, Attack }

            public partial class Outer
            {
                [RollbackState]
                public partial struct Fighter
                {
                    public Fixed X;
                    public int @base;
                    public ActionId Action;
                    public char LastKey;
                    public bool Grounded;
                    [NoRollback] public float RenderOnly;
                }
            }

            [RollbackState]
            public partial struct Match
            {
                public int Tick;
                public Outer.Fighter P1, P2;
                public ulong Rng;
            }
        }

        [RollbackState]
        public partial struct GlobalNamespaceState { public long Value; }
        """;

    [Fact]
    public void GeneratedCode_CompilesUnderCSharp9()
    {
        var result = GeneratorHarness.Run(UnityStyleSource, LanguageVersion.CSharp9);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Equal(4, result.GeneratedSources.Length);
    }

    [Fact]
    public void KeywordFieldNames_AreEscaped()
    {
        var result = GeneratorHarness.Run(UnityStyleSource);
        string fighter = result.GeneratedSources.Single(s => s.Contains("partial struct Fighter"));

        Assert.Contains("writer.WriteInt32(this.@base);", fighter);
        Assert.Contains("Join(path, \"base\")", fighter);
    }

    [Fact]
    public void SnapshotSize_SumsNestedConstants()
    {
        var result = GeneratorHarness.Run(UnityStyleSource);
        string match = result.GeneratedSources.Single(s => s.Contains("partial struct Match"));

        Assert.Contains(
            "RollbackSnapshotSize = 4 + global::Game.Sim.Outer.Fighter.RollbackSnapshotSize + global::Game.Sim.Outer.Fighter.RollbackSnapshotSize + 8;",
            match);
    }

    [Fact]
    public void Layout_EmitsEnumMembers_AndFixedPointBits()
    {
        var result = GeneratorHarness.Run(UnityStyleSource);
        string fighter = result.GeneratedSources.Single(s => s.Contains("partial struct Fighter"));
        string fixedPoint = result.GeneratedSources.Single(s => s.Contains("partial struct Fixed"));

        Assert.Contains(
            "builder.AddEnum(global::Rewindable.RollbackFormat.Join(path, \"Action\"), global::Rewindable.LeafKind.UInt16, \"Game.Sim.ActionId\", "
            + "new long[] { 0L, 1L, 2L }, new string[] { \"Idle\", \"Walk\", \"Attack\" });",
            fighter);
        Assert.Contains("builder.Add(path, global::Rewindable.LeafKind.Int32, \"Game.Sim.Fixed\", 16);", fixedPoint);
    }
}
