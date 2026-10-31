namespace Rewindable.Tests;

[RollbackState, FixedPoint(16)]
public partial struct Fixed
{
    public int Raw;

    public static Fixed FromInt(int value) => new() { Raw = value << 16 };
    public static Fixed FromRaw(int raw) => new() { Raw = raw };
}

public enum ActionId : ushort
{
    Idle,
    Walk,
    Attack,
}

[RollbackState]
public partial struct Fighter
{
    public Fixed X;
    public Fixed Y;
    public int Hp;
    public ActionId Action;
    public byte Frame;
    public bool Grounded;

    [NoRollback] public int DebugCounter;
}

[RollbackState]
public partial struct MatchState
{
    public int Tick;
    public Fighter P1;
    public Fighter P2;
    public ulong RngState;
    public char LastKey;
    public sbyte SByteValue;
    public short ShortValue;
    public uint UIntValue;
    public long LongValue;

    public static MatchState Sample() => new()
    {
        Tick = 120,
        P1 = new Fighter { X = Fixed.FromInt(-3), Y = Fixed.FromInt(0), Hp = 1000, Action = ActionId.Walk, Frame = 4, Grounded = true },
        P2 = new Fighter { X = Fixed.FromInt(3), Y = Fixed.FromRaw(98304), Hp = 850, Action = ActionId.Attack, Frame = 11, Grounded = false },
        RngState = 0xDEADBEEFCAFEBABEUL,
        LastKey = 'K',
        SByteValue = -5,
        ShortValue = -1234,
        UIntValue = 4_000_000_000u,
        LongValue = long.MinValue + 7,
    };
}
