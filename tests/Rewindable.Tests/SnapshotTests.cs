namespace Rewindable.Tests;

public class SnapshotTests
{
    [Fact]
    public void SnapshotSize_IsSumOfFieldSizes()
    {
        // Fixed 4 + Fixed 4 + int 4 + ushort enum 2 + byte 1 + bool 1 (DebugCounter excluded)
        Assert.Equal(16, Fighter.RollbackSnapshotSize);
        // 4 + 16 + 16 + 8 + 2 + 1 + 2 + 4 + 8
        Assert.Equal(61, MatchState.RollbackSnapshotSize);
        Assert.Equal(61, default(MatchState).SnapshotSize);
    }

    [Fact]
    public void Layout_IsLittleEndian_InDeclarationOrder()
    {
        var fighter = new Fighter
        {
            X = Fixed.FromRaw(0x01020304),
            Y = Fixed.FromRaw(-1),
            Hp = 0x0A0B0C0D,
            Action = ActionId.Attack,
            Frame = 0x7F,
            Grounded = true,
        };

        byte[] bytes = RollbackSnapshot.Capture(in fighter);

        Assert.Equal(new byte[]
        {
            0x04, 0x03, 0x02, 0x01,
            0xFF, 0xFF, 0xFF, 0xFF,
            0x0D, 0x0C, 0x0B, 0x0A,
            0x02, 0x00,
            0x7F,
            0x01,
        }, bytes);
    }

    [Fact]
    public void RoundTrip_RestoresEveryRollbackField()
    {
        var original = MatchState.Sample();

        var restored = RollbackSnapshot.Restore<MatchState>(RollbackSnapshot.Capture(in original));

        Assert.Equal(original, restored);
        Assert.Empty(RollbackSnapshot.Diff(in original, in restored));
    }

    [Fact]
    public void Restore_OverwritesGarbage_ButLeavesNoRollbackFieldsAlone()
    {
        var original = MatchState.Sample();
        byte[] snapshot = RollbackSnapshot.Capture(in original);

        var target = new MatchState { Tick = 999, P1 = new Fighter { Hp = -1, DebugCounter = 42 } };
        RollbackSnapshot.Restore(snapshot, ref target);

        Assert.Equal(original.Tick, target.Tick);
        Assert.Equal(original.P1.Hp, target.P1.Hp);
        Assert.Equal(42, target.P1.DebugCounter);
    }

    [Fact]
    public void Checksum_IgnoresNoRollbackFields()
    {
        var a = MatchState.Sample();
        var b = a;
        b.P1.DebugCounter = 12345;

        Assert.Equal(RollbackSnapshot.Checksum(in a), RollbackSnapshot.Checksum(in b));
    }

    [Fact]
    public void Checksum_ChangesWhenAnyRollbackFieldChanges()
    {
        var baseline = MatchState.Sample();
        ulong baseHash = RollbackSnapshot.Checksum(in baseline);

        MatchState[] variants =
        {
            baseline with { Tick = baseline.Tick + 1 },
            baseline with { P2 = baseline.P2 with { X = Fixed.FromRaw(baseline.P2.X.Raw + 1) } },
            baseline with { P1 = baseline.P1 with { Grounded = false } },
            baseline with { P1 = baseline.P1 with { Action = ActionId.Idle } },
            baseline with { LastKey = 'L' },
            baseline with { LongValue = baseline.LongValue + 1 },
        };

        foreach (var variant in variants)
            Assert.NotEqual(baseHash, RollbackSnapshot.Checksum(in variant));
    }

    [Fact]
    public void Checksum_IsStableGolden()
    {
        // FNV-1a over the little-endian layout: must never change between versions/platforms.
        var fighter = new Fighter { X = Fixed.FromInt(1), Hp = 100, Action = ActionId.Walk, Frame = 3, Grounded = true };
        byte[] bytes = RollbackSnapshot.Capture(in fighter);

        Assert.Equal(RollbackChecksum.Fnv1a64(bytes), RollbackSnapshot.Checksum(in fighter));
        Assert.Equal(0xCBF29CE484222325UL, RollbackChecksum.Fnv1a64(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Capture_IntoTooSmallBuffer_Throws()
    {
        var state = MatchState.Sample();
        Assert.Throws<ArgumentException>(() => RollbackSnapshot.Capture(in state, new byte[10]));
    }
}
