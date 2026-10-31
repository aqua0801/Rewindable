using Rewindable.Cli;

namespace Rewindable.Tests;

public class LayoutTests
{
    [Fact]
    public void Layout_ListsEveryLeaf_InSnapshotOrder()
    {
        SnapshotLayout layout = SnapshotLayout.Of<MatchState>();

        Assert.Equal(MatchState.RollbackSnapshotSize, layout.Size);
        Assert.Equal(new[]
        {
            "Tick",
            "P1.X", "P1.Y", "P1.Hp", "P1.Action", "P1.Frame", "P1.Grounded",
            "P2.X", "P2.Y", "P2.Hp", "P2.Action", "P2.Frame", "P2.Grounded",
            "RngState", "LastKey", "SByteValue", "ShortValue", "UIntValue", "LongValue",
        }, layout.Fields.Select(f => f.Path));
        Assert.Equal(new[] { 0, 4, 8, 12, 16, 18, 19 }, layout.Fields.Take(7).Select(f => f.Offset));
    }

    [Fact]
    public void Layout_CarriesEnumNames_AndFixedPoint()
    {
        SnapshotLayout layout = SnapshotLayout.Of<MatchState>();
        LayoutField action = layout.Fields.Single(f => f.Path == "P1.Action");
        LayoutField x = layout.Fields.Single(f => f.Path == "P1.X");

        Assert.Equal(LeafKind.UInt16, action.Kind);
        Assert.Equal("Rewindable.Tests.ActionId", action.TypeName);
        Assert.Equal(new[] { "Idle", "Walk", "Attack" }, action.EnumNames);
        Assert.Equal(16, x.FixedPointBits);
        Assert.Equal("Rewindable.Tests.Fixed", x.TypeName);
    }

    [Fact]
    public void UntypedCompare_MatchesTypedDiff()
    {
        var a = MatchState.Sample();
        var b = a;
        b.Tick++;
        b.P2.X = Fixed.FromRaw(b.P2.X.Raw + 1);
        b.P1.Action = ActionId.Idle;
        b.LastKey = 'P';
        b.LongValue = -1;

        var typed = RollbackSnapshot.Diff(in a, in b).Select(d => d.ToString());
        var untyped = SnapshotLayout.Of<MatchState>()
            .Compare(RollbackSnapshot.Capture(in a), RollbackSnapshot.Capture(in b))
            .Select(d => d.ToString());

        Assert.Equal(typed, untyped);
    }

    [Fact]
    public void RollbackValueLeaves_AreDescribed()
    {
        SnapshotLayout layout = SnapshotLayout.Of<Body>();
        var body = new Body { Y = ImmutableFixed.FromRaw(98304), Previous = PackedInput.FromRawValue(0x123) };
        byte[] snapshot = RollbackSnapshot.Capture(in body);

        Assert.Equal("1.5 (raw 98304)", layout.Fields.Single(f => f.Path == "Y").Format(snapshot));
        Assert.Equal("291", layout.Fields.Single(f => f.Path == "Previous").Format(snapshot));
    }

    [Fact]
    public void UnknownEnumValue_IsShownAsNumber()
    {
        var a = MatchState.Sample();
        a.P1.Action = (ActionId)42;
        byte[] snapshot = RollbackSnapshot.Capture(in a);

        string text = SnapshotLayout.Of<MatchState>().Fields.Single(f => f.Path == "P1.Action").Format(snapshot);

        Assert.Equal("42 (no Rewindable.Tests.ActionId member)", text);
    }
}

public class DesyncRecorderTests
{
    internal static byte[] Inputs(int frame, int salt = 0) => new byte[] { (byte)frame, (byte)(frame >> 8), (byte)salt, 0 };

    internal static MatchState StateAt(int frame)
    {
        var s = MatchState.Sample();
        s.Tick = frame;
        s.P1.X = Fixed.FromInt(frame);
        return s;
    }

    [Fact]
    public void Recorder_KeepsTheNewestFrames()
    {
        var recorder = new DesyncRecorder<MatchState>(capacity: 4, inputBytesPerFrame: 4);
        for (int f = 0; f < 10; f++)
        {
            var s = StateAt(f);
            recorder.Record(f, in s, checksum: (ulong)f * 7, Inputs(f));
        }

        DesyncDump dump = recorder.CreateDump("P1", markFrame: 9);

        Assert.Equal(6, recorder.OldestFrame);
        Assert.Equal(9, recorder.NewestFrame);
        Assert.Equal(new[] { 6, 7, 8, 9 }, dump.Frames.Select(f => f.Frame));
        Assert.Equal(56UL, dump.Frames[2].Checksum);
        Assert.Equal(Inputs(8), dump.Frames[2].Inputs);
        Assert.Equal(StateAt(8), RollbackSnapshot.Restore<MatchState>(dump.Frames[2].Snapshot));
    }

    [Fact]
    public void Recorder_RejectsOutOfOrderFrames_AndWrongInputSize()
    {
        var recorder = new DesyncRecorder<MatchState>(4, 4);
        var s = StateAt(5);
        recorder.Record(5, in s, 0, Inputs(5));

        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.Record(5, in s, 0, Inputs(5)));
        Assert.Throws<ArgumentException>(() => recorder.Record(6, in s, 0, new byte[3]));
    }

    [Fact]
    public void Record_DoesNotAllocate()
    {
        var recorder = new DesyncRecorder<MatchState>(16, 4);
        var s = StateAt(0);
        byte[] inputs = Inputs(0);
        recorder.Record(0, in s, 0, inputs);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 1; f <= 1000; f++)
            recorder.Record(f, in s, 0, inputs);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Dump_RoundTripsThroughBytes()
    {
        var recorder = new DesyncRecorder<MatchState>(8, 4);
        for (int f = 0; f < 5; f++)
        {
            var s = StateAt(f);
            recorder.Record(f, in s, (ulong)f, Inputs(f));
        }
        DesyncDump original = recorder.CreateDump("P2 (client)", 3);

        DesyncDump copy = DesyncDump.ReadFrom(new MemoryStream(original.ToArray()));

        Assert.Equal("P2 (client)", copy.Label);
        Assert.Equal(3, copy.MarkFrame);
        Assert.Equal(original.CreatedUnixMs, copy.CreatedUnixMs);
        Assert.True(copy.Layout.IsCompatibleWith(original.Layout, out _));
        Assert.Equal(new[] { "Idle", "Walk", "Attack" }, copy.Layout.Fields.Single(f => f.Path == "P2.Action").EnumNames);
        Assert.Equal(original.Frames.Select(f => f.Snapshot), copy.Frames.Select(f => f.Snapshot));
        Assert.Equal(original.Frames.Select(f => f.Inputs), copy.Frames.Select(f => f.Inputs));
    }

    [Fact]
    public void Dump_RejectsGarbage()
    {
        Assert.Throws<InvalidDataException>(() => DesyncDump.ReadFrom(new MemoryStream(new byte[64])));
    }
}

public class DumpComparisonTests
{
    /// <summary>Two machines record frames 0..19; <paramref name="tweak"/> changes the right side per frame.</summary>
    internal static (DesyncDump Left, DesyncDump Right) Pair(
        Func<int, MatchState, MatchState>? tweakState = null,
        Func<int, int>? inputSalt = null,
        Func<int, ulong, ulong>? tweakChecksum = null,
        int leftFrom = 0)
    {
        var left = new DesyncRecorder<MatchState>(64, 4);
        var right = new DesyncRecorder<MatchState>(64, 4);
        for (int f = 0; f < 20; f++)
        {
            var s = DesyncRecorderTests.StateAt(f);
            ulong hash = RollbackSnapshot.Checksum(in s);
            if (f >= leftFrom)
                left.Record(f, in s, hash, DesyncRecorderTests.Inputs(f));

            var r = tweakState?.Invoke(f, s) ?? s;
            right.Record(f, in r, tweakChecksum?.Invoke(f, RollbackSnapshot.Checksum(in r)) ?? RollbackSnapshot.Checksum(in r),
                DesyncRecorderTests.Inputs(f, inputSalt?.Invoke(f) ?? 0));
        }
        return (left.CreateDump("P1", 18), right.CreateDump("P2", 18));
    }

    private static MatchState BumpHpFrom(int frame, int f, MatchState s)
    {
        if (f >= frame) s.P2.Hp += 1;
        return s;
    }

    [Fact]
    public void IdenticalDumps_HaveNoDivergence()
    {
        var (l, r) = Pair();
        var c = DumpComparison.Compare(l, r);

        Assert.Equal(DivergenceCause.None, c.Cause);
        Assert.Equal(20, c.CommonFrameCount);
    }

    [Fact]
    public void SameInputs_DifferentState_IsStep()
    {
        var (l, r) = Pair(tweakState: (f, s) => BumpHpFrom(12, f, s));
        var c = DumpComparison.Compare(l, r);

        Assert.Equal(DivergenceCause.Step, c.Cause);
        Assert.Equal(12, c.FirstStateDivergence);
        Assert.Equal("P2.Hp: 850 != 851", Assert.Single(c.StateDiffs).ToString());
        Assert.Equal(-1, c.FirstInputDivergence);
        Assert.Contains("the step is not deterministic", c.ToReport());
    }

    [Fact]
    public void DifferentInputsIntoTheDivergentFrame_IsInputs()
    {
        var (l, r) = Pair(tweakState: (f, s) => BumpHpFrom(12, f, s), inputSalt: f => f == 12 ? 1 : 0);
        var c = DumpComparison.Compare(l, r);

        Assert.Equal(DivergenceCause.Inputs, c.Cause);
        Assert.Equal(12, c.FirstInputDivergence);
    }

    [Fact]
    public void ChecksumDiffersButStateMatches_IsChecksumOnly()
    {
        var (l, r) = Pair(tweakChecksum: (f, h) => f >= 15 ? h ^ 1 : h);
        var c = DumpComparison.Compare(l, r);

        Assert.Equal(DivergenceCause.ChecksumOnly, c.Cause);
        Assert.Equal(15, c.FirstChecksumDivergence);
    }

    [Fact]
    public void DivergentFromTheFirstCommonFrame_IsBeforeWindow()
    {
        var (l, r) = Pair(tweakState: (f, s) => BumpHpFrom(0, f, s), leftFrom: 5);
        var c = DumpComparison.Compare(l, r);

        Assert.Equal(DivergenceCause.BeforeWindow, c.Cause);
        Assert.Equal(5, c.FirstCommonFrame);
    }

    [Fact]
    public void DifferentTypes_AreIncomparable()
    {
        var bodyRecorder = new DesyncRecorder<Body>(4, 4);
        var body = new Body();
        bodyRecorder.Record(0, in body, 0, new byte[4]);
        var (l, _) = Pair();

        var c = DumpComparison.Compare(l, bodyRecorder.CreateDump("other", -1));

        Assert.Equal(DivergenceCause.Incomparable, c.Cause);
        Assert.Contains("layouts differ", c.Problem);
    }
}

public class CliTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rewindable-cli-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (string Left, string Right) Write(DesyncDump l, DesyncDump r)
    {
        string a = Path.Combine(_dir, "a" + DesyncDump.FileExtension), b = Path.Combine(_dir, "b" + DesyncDump.FileExtension);
        l.Save(a);
        r.Save(b);
        return (a, b);
    }

    private static (int Code, string Text) Run(params string[] args)
    {
        var output = new StringWriter();
        int code = CliApp.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void Diff_ReportsFieldAndCause_ExitsOne()
    {
        var (l, r) = DumpComparisonTests.Pair(tweakState: (f, s) => { if (f >= 12) s.P2.Hp += 1; return s; });
        var (a, b) = Write(l, r);

        var (code, text) = Run("diff", a, b, "--follow", "3");

        Assert.Equal(CliApp.Diverged, code);
        Assert.Contains("first divergent state: frame 12", text);
        Assert.Contains("P2.Hp: 850 | 851", text);
        Assert.Contains("frame 13: 1 field(s) differ", text);
    }

    [Fact]
    public void Diff_OfIdenticalDumps_ExitsZero()
    {
        var (l, r) = DumpComparisonTests.Pair();
        var (a, b) = Write(l, r);

        Assert.Equal(CliApp.Ok, Run("diff", a, b).Code);
    }

    [Fact]
    public void Show_PrintsEveryFieldOfAFrame()
    {
        var (l, _) = DumpComparisonTests.Pair();
        var (a, _) = Write(l, l);

        var (code, text) = Run("show", a, "--frame", "7");

        Assert.Equal(CliApp.Ok, code);
        Assert.Contains("P1.X", text);
        Assert.Contains("7 (raw 458752)", text);
        Assert.Contains("P2.Action", text);
        Assert.Contains("Attack", text);
    }

    [Fact]
    public void BadInput_ExitsTwo()
    {
        string junk = Path.Combine(_dir, "junk.rwdump");
        File.WriteAllBytes(junk, new byte[16]);

        Assert.Equal(CliApp.Error, Run("show", junk).Code);
        Assert.Equal(CliApp.Error, Run("diff", junk).Code);
        Assert.Equal(CliApp.Error, Run("bogus").Code);
    }
}
