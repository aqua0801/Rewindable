namespace Rewindable.Tests;

public class SnapshotRingTests
{
    private static MatchState AtTick(int tick)
    {
        var state = MatchState.Sample();
        state.Tick = tick;
        state.P1.X = Fixed.FromInt(tick);
        return state;
    }

    [Fact]
    public void SaveThenLoad_ReturnsSameState()
    {
        var ring = new SnapshotRing<MatchState>(8);
        var state = AtTick(5);
        ulong checksum = ring.Save(5, in state);

        var loaded = default(MatchState);
        Assert.True(ring.TryLoad(5, ref loaded));
        Assert.Equal(state, loaded);
        Assert.True(ring.TryGetChecksum(5, out ulong stored));
        Assert.Equal(checksum, stored);
        Assert.Equal(RollbackSnapshot.Checksum(in state), checksum);
    }

    [Fact]
    public void OldFrames_AreOverwritten()
    {
        var ring = new SnapshotRing<MatchState>(4);
        for (int tick = 0; tick < 10; tick++)
        {
            var state = AtTick(tick);
            ring.Save(tick, in state);
        }

        var loaded = default(MatchState);
        Assert.False(ring.TryLoad(5, ref loaded));
        Assert.False(ring.Contains(5));
        for (int tick = 6; tick < 10; tick++)
        {
            Assert.True(ring.TryLoad(tick, ref loaded));
            Assert.Equal(tick, loaded.Tick);
        }
    }

    [Fact]
    public void NegativeFrames_AreSupported()
    {
        var ring = new SnapshotRing<MatchState>(3);
        var state = AtTick(-7);
        ring.Save(-7, in state);

        var loaded = default(MatchState);
        Assert.True(ring.TryLoad(-7, ref loaded));
        Assert.Equal(-7, loaded.Tick);
    }

    [Fact]
    public void EmptyRing_HasNothing()
    {
        var ring = new SnapshotRing<MatchState>(4);
        Assert.False(ring.Contains(0));
        Assert.True(ring.GetSnapshot(0).IsEmpty);
        Assert.Equal(MatchState.RollbackSnapshotSize, ring.Stride);
    }

    [Fact]
    public void SaveAndLoad_DoNotAllocate()
    {
        var ring = new SnapshotRing<MatchState>(8);
        var state = AtTick(1);
        var loaded = default(MatchState);
        ring.Save(1, in state);
        ring.TryLoad(1, ref loaded); // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            ring.Save(i, in state);
            ring.TryLoad(i, ref loaded);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }
}
