namespace Rewindable.Tests;

/// <summary>Same shape as a typical immutable Q16.16 type: private ctor, get-only raw, clamping factory.</summary>
[RollbackValue(nameof(RawValue), nameof(FromRaw)), FixedPoint(16)]
public readonly struct ImmutableFixed : IEquatable<ImmutableFixed>
{
    private ImmutableFixed(int raw) => RawValue = raw;

    public int RawValue { get; }

    public static ImmutableFixed FromRaw(int raw) => new(raw < -int.MaxValue ? -int.MaxValue : raw);
    public static ImmutableFixed FromInt(int value) => new(value << 16);

    public bool Equals(ImmutableFixed other) => RawValue == other.RawValue;
    public override bool Equals(object? obj) => obj is ImmutableFixed other && Equals(other);
    public override int GetHashCode() => RawValue;
}

/// <summary>Readonly-field wrapper with a sanitizing factory, like a packed input frame.</summary>
[RollbackValue(nameof(Value), nameof(FromRawValue))]
public readonly struct PackedInput
{
    private const ushort ValidBits = 0x07FF;

    public readonly ushort Value;

    private PackedInput(ushort value) => Value = (ushort)(value & ValidBits);

    public static PackedInput FromRawValue(ushort value) => new(value);
}

[RollbackState]
public partial struct Body
{
    public ImmutableFixed X;
    public ImmutableFixed Y;
    public PackedInput Previous;
    public int Hp;
}

public class RollbackValueTests
{
    private static Body Sample() => new()
    {
        X = ImmutableFixed.FromInt(-3),
        Y = ImmutableFixed.FromRaw(98304),
        Previous = PackedInput.FromRawValue(0x0123),
        Hp = 900,
    };

    [Fact]
    public void Size_UsesRawMemberSize()
    {
        // 4 + 4 + 2 + 4
        Assert.Equal(14, Body.RollbackSnapshotSize);
    }

    [Fact]
    public void RoundTrip_GoesThroughFactory()
    {
        var original = Sample();

        var restored = RollbackSnapshot.Restore<Body>(RollbackSnapshot.Capture(in original));

        Assert.Equal(original.X, restored.X);
        Assert.Equal(original.Y, restored.Y);
        Assert.Equal(original.Previous.Value, restored.Previous.Value);
        Assert.Equal(original.Hp, restored.Hp);
    }

    [Fact]
    public void Diff_FormatsFixedPointValues_AndPlainRawValues()
    {
        var a = Sample();
        var b = a;
        b.Y = ImmutableFixed.FromRaw(0);
        b.Previous = PackedInput.FromRawValue(0x0001);

        var diffs = RollbackSnapshot.Diff(in a, in b).Select(d => d.ToString()).ToArray();

        Assert.Equal(new[]
        {
            "Y: 1.5 (raw 98304) != 0 (raw 0)",
            "Previous: 291 != 1",
        }, diffs);
    }

    [Fact]
    public void Restore_SanitizesThroughFactory()
    {
        var state = Sample();
        byte[] bytes = RollbackSnapshot.Capture(in state);
        bytes[9] = 0xFF; // high byte of Previous.Value: bits outside ValidBits get masked by the factory

        var restored = RollbackSnapshot.Restore<Body>(bytes);

        Assert.Equal(0x0723, restored.Previous.Value);
    }
}
