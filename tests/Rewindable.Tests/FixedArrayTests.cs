namespace Rewindable.Tests;

[RollbackState]
public partial struct Squad
{
    public int Tick;
    public FixedArray4<int> Scores;
    public FixedArray2<ActionId> Actions;
    public FixedArray4<Fixed> Positions;          // [RollbackState, FixedPoint] elements
    public FixedArray2<ImmutableFixed> Speeds;    // [RollbackValue] elements
    public FixedArray2<Fighter> Members;          // nested elements with a [NoRollback] field

    public static Squad Sample()
    {
        var s = new Squad { Tick = 7 };
        for (int i = 0; i < 4; i++)
        {
            s.Scores[i] = (i + 1) * 10;
            s.Positions[i] = Fixed.FromInt(i);
        }
        s.Actions[0] = ActionId.Walk;
        s.Actions[1] = ActionId.Attack;
        s.Speeds[0] = ImmutableFixed.FromRaw(98304);
        s.Speeds[1] = ImmutableFixed.FromInt(-2);
        s.Members[0] = new Fighter { Hp = 100, Action = ActionId.Idle, Grounded = true };
        s.Members[1] = new Fighter { Hp = 80, Action = ActionId.Attack, Frame = 3 };
        return s;
    }
}

public class FixedArrayTypeTests
{
    [Fact]
    public void Indexer_ReadsAndWritesEveryElement()
    {
        var a = new FixedArray4<int>();
        for (int i = 0; i < a.Length; i++) a[i] = i * i;

        Assert.Equal(new[] { 0, 1, 4, 9 }, Enumerable.Range(0, 4).Select(i => a[i]));
        Assert.Equal("[0, 1, 4, 9]", a.ToString());
    }

    [Fact]
    public void At_ModifiesInPlace()
    {
        var a = new FixedArray2<Fighter>();
        FixedArray2<Fighter>.At(ref a, 1).Hp = 42;

        Assert.Equal(42, a[1].Hp);
        Assert.Equal(0, a[0].Hp);
    }

    [Fact]
    public void OutOfRange_Throws()
    {
        var a = new FixedArray8<byte>();
        Assert.Throws<IndexOutOfRangeException>(() => a[8]);
        Assert.Throws<IndexOutOfRangeException>(() => a[-1] = 1);
        Assert.Throws<IndexOutOfRangeException>(() => FixedArray8<byte>.At(ref a, 99));
    }

    [Fact]
    public void Equality_IsElementWise()
    {
        var a = new FixedArray16<int>();
        var b = new FixedArray16<int>();
        Assert.True(a == b);

        b[15] = 1;
        Assert.True(a != b);
        Assert.False(a.Equals((object)b));
    }

    [Fact]
    public void IsCopiedByValue()
    {
        var a = new FixedArray4<int>();
        var b = a;
        b[0] = 5;

        Assert.Equal(0, a[0]);
    }
}

public class FixedArrayStateTests
{
    [Fact]
    public void SnapshotSize_MultipliesElementSize()
    {
        // 4 + 4*4 + 2*2 + 4*4 + 2*4 + 2*Fighter(16)
        Assert.Equal(80, Squad.RollbackSnapshotSize);
    }

    [Fact]
    public void Elements_AreWrittenInIndexOrder()
    {
        var s = Squad.Sample();
        byte[] bytes = RollbackSnapshot.Capture(in s);

        Assert.Equal(10, BitConverter.ToInt32(bytes, 4));
        Assert.Equal(20, BitConverter.ToInt32(bytes, 8));
        Assert.Equal(40, BitConverter.ToInt32(bytes, 16));
    }

    [Fact]
    public void RoundTrip_RestoresEveryElement()
    {
        var original = Squad.Sample();

        var restored = RollbackSnapshot.Restore<Squad>(RollbackSnapshot.Capture(in original));

        Assert.Empty(RollbackSnapshot.Diff(in original, in restored));
        Assert.Equal(original.Scores, restored.Scores);
        Assert.Equal(original.Speeds[0], restored.Speeds[0]);
        Assert.Equal(original.Members[1].Frame, restored.Members[1].Frame);
    }

    [Fact]
    public void Restore_KeepsNoRollbackFieldsOfElements()
    {
        var original = Squad.Sample();
        byte[] snapshot = RollbackSnapshot.Capture(in original);

        var target = new Squad();
        FixedArray2<Fighter>.At(ref target.Members, 1).DebugCounter = 99;
        RollbackSnapshot.Restore(snapshot, ref target);

        Assert.Equal(80, target.Members[1].Hp);
        Assert.Equal(99, target.Members[1].DebugCounter);
    }

    [Fact]
    public void Diff_NamesTheElement()
    {
        var a = Squad.Sample();
        var b = a;
        b.Scores[2] = 5;
        b.Actions[1] = ActionId.Idle;
        b.Positions[3] = Fixed.FromRaw(98304);
        b.Speeds[1] = ImmutableFixed.FromInt(3);
        FixedArray2<Fighter>.At(ref b.Members, 1).Hp = 1;

        var diffs = RollbackSnapshot.Diff(in a, in b).Select(d => d.ToString()).ToArray();

        Assert.Equal(new[]
        {
            "Scores[2]: 30 != 5",
            "Actions[1]: Attack != Idle",
            "Positions[3]: 3 (raw 196608) != 1.5 (raw 98304)",
            "Speeds[1]: -2 (raw -131072) != 3 (raw 196608)",
            "Members[1].Hp: 80 != 1",
        }, diffs);
    }

    [Fact]
    public void Layout_ListsElements_AndMatchesTypedDiff()
    {
        SnapshotLayout layout = SnapshotLayout.Of<Squad>();

        Assert.Equal(Squad.RollbackSnapshotSize, layout.Size);
        Assert.Contains("Scores[3]", layout.Fields.Select(f => f.Path));
        Assert.Contains("Members[1].Grounded", layout.Fields.Select(f => f.Path));
        Assert.Equal(new[] { "Idle", "Walk", "Attack" }, layout.Fields.Single(f => f.Path == "Actions[0]").EnumNames);

        var a = Squad.Sample();
        var b = a;
        b.Scores[0] = -1;
        FixedArray2<Fighter>.At(ref b.Members, 0).Action = ActionId.Walk;
        var typed = RollbackSnapshot.Diff(in a, in b).Select(d => d.ToString());
        var untyped = layout.Compare(RollbackSnapshot.Capture(in a), RollbackSnapshot.Capture(in b)).Select(d => d.ToString());
        Assert.Equal(typed, untyped);
    }

    [Fact]
    public void Checksum_SeesEveryElement()
    {
        var baseline = Squad.Sample();
        ulong hash = RollbackSnapshot.Checksum(in baseline);

        for (int i = 0; i < 4; i++)
        {
            var s = baseline;
            s.Scores[i]++;
            Assert.NotEqual(hash, RollbackSnapshot.Checksum(in s));
        }
    }
}
