using Rewindable.Cli;

namespace Rewindable.Tests;

/// <summary>Same shape as <see cref="Fighter"/> under another name: snapshots are interchangeable.</summary>
[RollbackState]
public partial struct FighterRenamed
{
    public Fixed X;
    public Fixed Y;
    public int Hp;
    public ActionId Action;
    public byte Frame;
    public bool Grounded;
}

public enum ActionIdV2 : ushort
{
    Idle,
    Walk,
    Attack,
    Block,
}

/// <summary><see cref="Fighter"/> after a "new build" added an enum member.</summary>
[RollbackState]
public partial struct FighterV2
{
    public Fixed X;
    public Fixed Y;
    public int Hp;
    public ActionIdV2 Action;
    public byte Frame;
    public bool Grounded;
}

public class LayoutFingerprintTests
{
    private static SnapshotLayout Layout(params (string Path, LeafKind Kind, int Bits)[] fields)
    {
        var builder = new SnapshotLayoutBuilder();
        foreach (var (path, kind, bits) in fields)
            builder.Add(path, kind, kind.ToString(), bits);
        return builder.Build("Test");
    }

    [Fact]
    public void Fingerprint_IsFrozen()
    {
        // Stored in dumps and sent in handshakes: the algorithm must never change silently.
        Assert.Equal(0xC498FAA7112A8BC1UL, SnapshotLayout.Of<MatchState>().Fingerprint);
        Assert.Equal(16, SnapshotLayout.Of<MatchState>().FingerprintText.Length);
    }

    [Fact]
    public void SameShape_UnderAnotherName_HasTheSameFingerprint()
    {
        SnapshotLayout a = SnapshotLayout.Of<Fighter>();
        SnapshotLayout b = SnapshotLayout.Of<FighterRenamed>();

        Assert.NotEqual(a.TypeName, b.TypeName);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.True(a.IsCompatibleWith(b, out _));
        Assert.Empty(a.DescribeDifferences(b));
    }

    [Fact]
    public void DifferentShapes_HaveDifferentFingerprints()
    {
        var fingerprints = new[]
        {
            SnapshotLayout.Of<MatchState>().Fingerprint,
            SnapshotLayout.Of<Fighter>().Fingerprint,
            SnapshotLayout.Of<Squad>().Fingerprint,
            SnapshotLayout.Of<Body>().Fingerprint,
        };

        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());
    }

    [Fact]
    public void EnumMemberChange_ChangesTheFingerprint()
    {
        SnapshotLayout before = SnapshotLayout.Of<Fighter>();
        SnapshotLayout after = SnapshotLayout.Of<FighterV2>();

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal(new[] { "~ Action: enum members differ" }, before.DescribeDifferences(after));
    }

    [Fact]
    public void Differences_ListAddedRemovedAndChangedFields()
    {
        SnapshotLayout before = Layout(("Hp", LeafKind.Int32, -1), ("Meter", LeafKind.UInt16, -1), ("X", LeafKind.Int32, 16));
        SnapshotLayout after = Layout(("Hp", LeafKind.Int16, -1), ("X", LeafKind.Int32, 8), ("Guard", LeafKind.Byte, -1));

        Assert.Equal(new[]
        {
            "~ Hp: Int32 -> Int16",
            "- Meter",
            "~ X: fixed-point bits 16 -> 8",
            "+ Guard",
        }, before.DescribeDifferences(after));
        Assert.False(before.IsCompatibleWith(after, out string problem));
        Assert.Contains(before.FingerprintText + " vs " + after.FingerprintText, problem);
        Assert.Contains("- Meter", problem);
    }

    [Fact]
    public void ReorderedFields_AreReportedAsSuch()
    {
        SnapshotLayout before = Layout(("A", LeafKind.Int32, -1), ("B", LeafKind.Int32, -1));
        SnapshotLayout after = Layout(("B", LeafKind.Int32, -1), ("A", LeafKind.Int32, -1));

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Equal(new[] { "field order differs" }, before.DescribeDifferences(after));
    }

    [Fact]
    public void Fingerprint_SurvivesADumpRoundTrip()
    {
        var recorder = new DesyncRecorder<MatchState>(4, 0);
        var s = MatchState.Sample();
        recorder.Record(0, in s, 0, ReadOnlySpan<byte>.Empty);

        DesyncDump copy = DesyncDump.ReadFrom(new MemoryStream(recorder.CreateDump("P1", -1).ToArray()));

        Assert.Equal(SnapshotLayout.Of<MatchState>().Fingerprint, copy.Layout.Fingerprint);
    }

    private static DesyncDump DumpOf<T>(T state) where T : struct, IRollbackState<T>
    {
        var recorder = new DesyncRecorder<T>(4, 0);
        recorder.Record(0, in state, 0, ReadOnlySpan<byte>.Empty);
        return recorder.CreateDump(typeof(T).Name, -1);
    }

    [Fact]
    public void DumpsOfDifferentBuilds_AreIncomparable_AndSayWhy()
    {
        var c = DumpComparison.Compare(DumpOf(new Fighter()), DumpOf(new FighterV2()));

        Assert.Equal(DivergenceCause.Incomparable, c.Cause);
        Assert.Contains("~ Action: enum members differ", c.Problem);
    }

    [Fact]
    public void DumpsOfARenamedType_StayComparable()
    {
        var c = DumpComparison.Compare(DumpOf(new Fighter { Hp = 5 }), DumpOf(new FighterRenamed { Hp = 5 }));

        Assert.Equal(DivergenceCause.None, c.Cause);
    }

    [Fact]
    public void Cli_ShowsTheFingerprint()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("rewindable-fp-").FullName, "a.rwdump");
        DumpOf(MatchState.Sample()).Save(path);
        var output = new StringWriter();

        CliApp.Run(new[] { "show", path }, output);

        Assert.Contains("layout  : " + SnapshotLayout.Of<MatchState>().FingerprintText, output.ToString());
    }
}
