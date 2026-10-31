namespace Rewindable.Tests;

public class DiffTests
{
    [Fact]
    public void IdenticalStates_HaveNoDiff()
    {
        var a = MatchState.Sample();
        var b = a;
        b.P1.DebugCounter = 99; // [NoRollback] never shows up

        Assert.Empty(RollbackSnapshot.Diff(in a, in b));
    }

    [Fact]
    public void NestedFixedPoint_ReportsPathAndDecimal()
    {
        var a = MatchState.Sample();
        var b = a;
        b.P2.X = Fixed.FromRaw(Fixed.FromInt(3).Raw + 1);

        var diff = Assert.Single(RollbackSnapshot.Diff(in a, in b));

        Assert.Equal("P2.X", diff.Path);
        Assert.Equal("3 (raw 196608)", diff.Left);
        Assert.Equal("3.000015259 (raw 196609)", diff.Right);
    }

    [Fact]
    public void Diff_ReportsEveryChangedLeaf_InDeclarationOrder()
    {
        var a = MatchState.Sample();
        var b = a;
        b.Tick++;
        b.P1.Action = ActionId.Idle;
        b.P1.Grounded = false;
        b.LastKey = 'P';

        var paths = RollbackSnapshot.Diff(in a, in b).Select(d => d.ToString()).ToArray();

        Assert.Equal(new[]
        {
            "Tick: 120 != 121",
            "P1.Action: Walk != Idle",
            "P1.Grounded: true != false",
            "LastKey: 'K' (U+004B) != 'P' (U+0050)",
        }, paths);
    }

    [Fact]
    public void FixedPointAtRoot_UsesRootPath()
    {
        var a = Fixed.FromRaw(98304);
        var b = Fixed.FromRaw(0);

        var diff = Assert.Single(RollbackSnapshot.Diff(in a, in b));

        Assert.Equal(RollbackFormat.RootPath, diff.Path);
        Assert.Equal("1.5 (raw 98304)", diff.Left);
    }
}
