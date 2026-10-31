using Rewindable.Cli;

namespace Rewindable.Tests;

public class ReplayTests
{
    // A toy deterministic simulation: inputs are two bytes (damage dealt by P2 and P1).
    private static MatchState Step(in MatchState s, ReadOnlySpan<byte> inputs)
    {
        MatchState n = s;
        n.Tick++;
        n.P1.Hp -= inputs[0];
        n.P2.Hp -= inputs[1];
        n.P1.X = Fixed.FromRaw(n.P1.X.Raw + (inputs[0] - inputs[1]) * 100);
        n.RngState = unchecked(n.RngState * 6364136223846793005UL + 1442695040888963407UL);
        return n;
    }

    private static byte[] InputsAt(int frame) => new byte[] { (byte)(frame % 7), (byte)(frame % 5) };

    private static ulong Hash(in MatchState s) => RollbackSnapshot.Checksum(in s);

    private static MatchState Start()
    {
        var s = MatchState.Sample();
        s.Tick = 0;
        return s;
    }

    internal static Replay Record(int frames, int interval = 60)
    {
        MatchState s = Start();
        var recorder = new ReplayRecorder<MatchState>(in s, startFrame: 0, Hash(in s), inputBytesPerFrame: 2, interval, expectedFrames: 16);
        recorder.Metadata["rules"] = "toy";
        recorder.Metadata["p1"] = "Ryu";
        for (int f = 0; f < frames; f++)
        {
            byte[] inputs = InputsAt(f);
            s = Step(in s, inputs);
            recorder.Record(inputs, in s, Hash(in s));
        }
        return recorder.Build();
    }

    [Fact]
    public void Recorder_KeepsKeyframesAtTheIntervalAndTheEnd()
    {
        Replay replay = Record(250);

        Assert.Equal(250, replay.FrameCount);
        Assert.Equal(250, replay.EndFrame);
        Assert.Equal(new[] { 0, 60, 120, 180, 240, 250 }, replay.Checkpoints.Select(c => c.Frame));
        Assert.Equal(InputsAt(37), replay.GetInputs(37).ToArray());
    }

    [Fact]
    public void Verify_ReproducesEveryKeyframe()
    {
        ReplayResult result = Record(300).Verify<MatchState>(Step, Hash);

        Assert.True(result.Succeeded, result.ToReport());
        Assert.Equal(5, result.CheckpointsVerified);
        Assert.Equal(300, result.FramesRun);
    }

    [Fact]
    public void Verify_NamesTheFields_AndTheWindow_WhenTheSimulationChanged()
    {
        ReplayStepper<MatchState> changed = (in MatchState s, ReadOnlySpan<byte> inputs) =>
        {
            MatchState n = Step(in s, inputs);
            if (s.Tick == 130) n.P2.Hp -= 1;   // a "new build" deals one more damage on frame 131
            return n;
        };

        ReplayResult result = Record(300).Verify(changed, Hash);

        Assert.Equal(ReplayStatus.Diverged, result.Status);
        Assert.Equal(180, result.FailedFrame);
        Assert.Equal(120, result.LastMatchingFrame);
        Assert.Equal(2, result.CheckpointsVerified);
        Assert.Equal("P2.Hp", Assert.Single(result.Differences).Path);
        Assert.Contains("between frames 120 and 180", result.ToReport());
    }

    [Fact]
    public void Verify_ReportsAChecksumThatReadsOutsideTheState()
    {
        ReplayResult result = Record(120).Verify<MatchState>(Step, (in MatchState s) => Hash(in s) ^ 1);

        Assert.Equal(ReplayStatus.ChecksumMismatch, result.Status);
        Assert.Equal(60, result.FailedFrame);
    }

    private static Replay RecordFighter()
    {
        var f = new Fighter { Hp = 100 };
        var recorder = new ReplayRecorder<Fighter>(in f, 0, 0, inputBytesPerFrame: 1, checkpointInterval: 10);
        for (int i = 0; i < 30; i++)
        {
            f.Hp -= 1;
            recorder.Record(new byte[] { 1 }, in f, 0);
        }
        return recorder.Build();
    }

    [Fact]
    public void Verify_RefusesAReplayOfAnotherStateLayout()
    {
        ReplayResult result = RecordFighter().Verify<FighterV2>((in FighterV2 s, ReadOnlySpan<byte> i) => s);

        Assert.Equal(ReplayStatus.Incompatible, result.Status);
        Assert.Contains("~ Action: enum members differ", result.Problem);
    }

    [Fact]
    public void Verify_AcceptsARenamedStateType()
    {
        ReplayResult result = RecordFighter().Verify<FighterRenamed>((in FighterRenamed s, ReadOnlySpan<byte> i) =>
        {
            FighterRenamed n = s;
            n.Hp -= i[0];
            return n;
        });

        Assert.True(result.Succeeded, result.ToReport());
    }

    [Fact]
    public void File_RoundTrips()
    {
        Replay original = Record(130);

        Replay copy = Replay.ReadFrom(new MemoryStream(original.ToArray()));

        Assert.Equal("Ryu", copy.Metadata["p1"]);
        Assert.Equal(original.Layout.Fingerprint, copy.Layout.Fingerprint);
        Assert.Equal(original.Checkpoints.Select(c => c.Frame), copy.Checkpoints.Select(c => c.Frame));
        Assert.Equal(original.GetInputs(129).ToArray(), copy.GetInputs(129).ToArray());
        Assert.True(copy.Verify<MatchState>(Step, Hash).Succeeded);
    }

    [Fact]
    public void StateAt_SeeksFromTheNearestKeyframe()
    {
        Replay replay = Record(300);
        MatchState expected = Start();
        for (int f = 0; f < 137; f++)
            expected = Step(in expected, InputsAt(f));

        Assert.Equal(expected, replay.StateAt<MatchState>(137, Step));
        Assert.Equal(Start(), replay.StateAt<MatchState>(0, Step));
        Assert.True(replay.TryGetCheckpoint(180, out ReplayCheckpoint k));
        Assert.Equal(RollbackSnapshot.Restore<MatchState>(k.Snapshot), replay.StateAt<MatchState>(180, Step));
    }

    [Fact]
    public void Inputs_OutsideTheRecording_Throw()
    {
        Replay replay = Record(10);
        Assert.Throws<ArgumentOutOfRangeException>(() => replay.GetInputs(10).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => replay.StateAt<MatchState>(11, Step));
    }

    [Fact]
    public void Record_DoesNotAllocateBetweenKeyframes()
    {
        MatchState s = Start();
        var recorder = new ReplayRecorder<MatchState>(in s, 0, 0, 2, checkpointInterval: 1000, expectedFrames: 2000);
        byte[] inputs = InputsAt(1);
        recorder.Record(inputs, in s, 0);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 500; f++)
            recorder.Record(inputs, in s, 0);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Garbage_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Replay.ReadFrom(new MemoryStream(new byte[64])));
    }

    [Fact]
    public void Cli_ShowsAReplay_AndItsKeyframes()
    {
        string path = Path.Combine(Directory.CreateTempSubdirectory("rewindable-replay-").FullName, "m" + Replay.FileExtension);
        Record(130).Save(path);

        var summary = new StringWriter();
        Assert.Equal(CliApp.Ok, CliApp.Run(new[] { "show", path }, summary));
        Assert.Contains("keyframes: 0, 60, 120, 130", summary.ToString());
        Assert.Contains("p1 = Ryu", summary.ToString());

        var keyframe = new StringWriter();
        Assert.Equal(CliApp.Ok, CliApp.Run(new[] { "show", path, "--frame", "60" }, keyframe));
        Assert.Contains("P2.Hp", keyframe.ToString());

        Assert.Equal(CliApp.Error, CliApp.Run(new[] { "show", path, "--frame", "61" }, new StringWriter()));
    }
}
