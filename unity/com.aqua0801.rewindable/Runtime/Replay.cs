#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Rewindable
{
    /// <summary>Advances a state by one frame with that frame's recorded input bytes (the game's step function).</summary>
    public delegate T ReplayStepper<T>(in T state, ReadOnlySpan<byte> inputs);

    /// <summary>The game's own checksum of a state (e.g. its Hash()), to verify it as well as the snapshot.</summary>
    public delegate ulong ReplayChecksum<T>(in T state);

    /// <summary>A keyframe: the full state at <see cref="Frame"/> and the checksum the game reported for it.</summary>
    public sealed class ReplayCheckpoint
    {
        public ReplayCheckpoint(int frame, ulong checksum, byte[] snapshot)
        {
            Frame = frame;
            Checksum = checksum;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public int Frame { get; }
        public ulong Checksum { get; }
        public byte[] Snapshot { get; }
    }

    /// <summary>
    /// A recorded match (<c>.rwreplay</c>): the start state, the input bytes of every frame, keyframes (full state +
    /// checksum) at regular intervals, free-form metadata and the state's layout. Re-simulating it with the game's step
    /// function reproduces the match; <see cref="Verify{T}"/> checks every keyframe on the way.
    /// Frame <c>f</c>'s inputs turn the state at <c>f</c> into the state at <c>f + 1</c>.
    /// </summary>
    public sealed class Replay
    {
        public const string FileExtension = ".rwreplay";
        public const int FormatVersion = 1;
        internal static readonly byte[] Magic = { (byte)'R', (byte)'W', (byte)'R', (byte)'E', (byte)'P', (byte)'L', (byte)'A', (byte)'Y' };

        private readonly byte[] _inputs;
        private readonly ReplayCheckpoint[] _checkpoints;
        private readonly Dictionary<string, string> _metadata;

        public Replay(
            SnapshotLayout layout,
            int startFrame,
            int inputBytesPerFrame,
            byte[] inputs,
            IReadOnlyList<ReplayCheckpoint> checkpoints,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            if (inputBytesPerFrame <= 0)
                throw new ArgumentOutOfRangeException(nameof(inputBytesPerFrame), inputBytesPerFrame, "Must be positive.");
            if (inputs.Length % inputBytesPerFrame != 0)
                throw new ArgumentException("Input bytes are not a whole number of frames.", nameof(inputs));

            StartFrame = startFrame;
            InputBytesPerFrame = inputBytesPerFrame;
            _inputs = inputs;
            FrameCount = inputs.Length / inputBytesPerFrame;

            _checkpoints = new ReplayCheckpoint[checkpoints.Count];
            for (int i = 0; i < checkpoints.Count; i++)
            {
                ReplayCheckpoint c = checkpoints[i];
                if (c.Snapshot.Length != layout.Size)
                    throw new ArgumentException("Checkpoint " + c.Frame + " is " + c.Snapshot.Length + " bytes, layout is " + layout.Size + ".");
                if (c.Frame < startFrame || c.Frame > EndFrame)
                    throw new ArgumentException("Checkpoint " + c.Frame + " is outside " + startFrame + ".." + EndFrame + ".");
                if (i > 0 && c.Frame <= _checkpoints[i - 1].Frame)
                    throw new ArgumentException("Checkpoints must be strictly ascending.");
                _checkpoints[i] = c;
            }
            if (_checkpoints.Length == 0 || _checkpoints[0].Frame != startFrame)
                throw new ArgumentException("The first checkpoint must be the start state (frame " + startFrame + ").");

            _metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (metadata != null)
                foreach (KeyValuePair<string, string> pair in metadata)
                    _metadata[pair.Key] = pair.Value;
        }

        /// <summary>Layout of the recorded state; its fingerprint tells whether a build can replay this file.</summary>
        public SnapshotLayout Layout { get; }

        public int StartFrame { get; }

        /// <summary>Number of recorded frames of input.</summary>
        public int FrameCount { get; }

        /// <summary>The last frame the replay reaches (<see cref="StartFrame"/> + <see cref="FrameCount"/>).</summary>
        public int EndFrame => StartFrame + FrameCount;

        public int InputBytesPerFrame { get; }

        /// <summary>Keyframes, the first being the start state.</summary>
        public IReadOnlyList<ReplayCheckpoint> Checkpoints => _checkpoints;

        /// <summary>Free-form key/value pairs (rules, characters, players, build, date...).</summary>
        public IReadOnlyDictionary<string, string> Metadata => _metadata;

        /// <summary>The start state's snapshot.</summary>
        public byte[] StartSnapshot => _checkpoints[0].Snapshot;

        /// <summary>Inputs of frame <paramref name="frame"/> (they produce frame + 1).</summary>
        public ReadOnlySpan<byte> GetInputs(int frame)
        {
            if (frame < StartFrame || frame >= EndFrame)
                throw new ArgumentOutOfRangeException(nameof(frame), frame, "Recorded frames are " + StartFrame + ".." + (EndFrame - 1) + ".");
            return new ReadOnlySpan<byte>(_inputs, (frame - StartFrame) * InputBytesPerFrame, InputBytesPerFrame);
        }

        public bool TryGetCheckpoint(int frame, out ReplayCheckpoint checkpoint)
        {
            int lo = 0, hi = _checkpoints.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int f = _checkpoints[mid].Frame;
                if (f == frame)
                {
                    checkpoint = _checkpoints[mid];
                    return true;
                }
                if (f < frame) lo = mid + 1;
                else hi = mid - 1;
            }
            checkpoint = null!;
            return false;
        }

        /// <summary>
        /// The state at <paramref name="frame"/>: restores the nearest keyframe at or before it and steps forward.
        /// For seeking / scrubbing a replay.
        /// </summary>
        public T StateAt<T>(int frame, ReplayStepper<T> step) where T : struct, IRollbackState<T>
        {
            RequireCompatible<T>();
            if (frame < StartFrame || frame > EndFrame)
                throw new ArgumentOutOfRangeException(nameof(frame), frame, "The replay covers " + StartFrame + ".." + EndFrame + ".");

            ReplayCheckpoint start = _checkpoints[0];
            foreach (ReplayCheckpoint c in _checkpoints)
            {
                if (c.Frame > frame) break;
                start = c;
            }

            T state = RollbackSnapshot.Restore<T>(start.Snapshot);
            for (int f = start.Frame; f < frame; f++)
                state = step(in state, GetInputs(f));
            return state;
        }

        /// <summary>
        /// Re-simulates the whole replay from the start state with <paramref name="step"/> and compares every keyframe:
        /// the snapshot always, the game's checksum too when <paramref name="checksum"/> is given. Stops at the first
        /// keyframe that differs and reports its fields.
        /// </summary>
        public ReplayResult Verify<T>(ReplayStepper<T> step, ReplayChecksum<T>? checksum = null) where T : struct, IRollbackState<T>
        {
            SnapshotLayout current = SnapshotLayout.Of<T>();
            if (!Layout.IsCompatibleWith(current, out string problem))
                return ReplayResult.Incompatible(this, "recorded with another state layout (" + problem + ")");

            T state = RollbackSnapshot.Restore<T>(StartSnapshot);
            byte[] actual = new byte[current.Size];
            int next = 1;
            int lastGood = StartFrame;
            for (int f = StartFrame; f < EndFrame; f++)
            {
                state = step(in state, GetInputs(f));
                if (next >= _checkpoints.Length || _checkpoints[next].Frame != f + 1)
                    continue;

                ReplayCheckpoint expected = _checkpoints[next++];
                RollbackSnapshot.Capture(in state, actual);
                if (!actual.AsSpan().SequenceEqual(expected.Snapshot))
                    return ReplayResult.Diverged(this, expected.Frame, lastGood, Layout.Compare(expected.Snapshot, actual), f + 1 - StartFrame);
                if (checksum != null && checksum(in state) != expected.Checksum)
                    return ReplayResult.ChecksumMismatch(this, expected.Frame, lastGood, expected.Checksum, checksum(in state), f + 1 - StartFrame);
                lastGood = expected.Frame;
            }
            return ReplayResult.Matched(this, next - 1);
        }

        private void RequireCompatible<T>() where T : struct, IRollbackState<T>
        {
            if (!Layout.IsCompatibleWith(SnapshotLayout.Of<T>(), out string problem))
                throw new InvalidOperationException("This replay was recorded with another state layout (" + problem + ").");
        }

        // ------------------------------------------------------------------ file format

        public void WriteTo(Stream stream)
        {
            using (var w = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(_metadata.Count);
                foreach (KeyValuePair<string, string> pair in _metadata)
                {
                    w.Write(pair.Key);
                    w.Write(pair.Value);
                }
                Layout.WriteTo(w);
                w.Write(StartFrame);
                w.Write(InputBytesPerFrame);
                w.Write(FrameCount);
                w.Write(_inputs);
                w.Write(_checkpoints.Length);
                foreach (ReplayCheckpoint c in _checkpoints)
                {
                    w.Write(c.Frame);
                    w.Write(c.Checksum);
                    w.Write(c.Snapshot);
                }
            }
        }

        public byte[] ToArray()
        {
            using (var ms = new MemoryStream())
            {
                WriteTo(ms);
                return ms.ToArray();
            }
        }

        public void Save(string path)
        {
            using (FileStream fs = File.Create(path))
                WriteTo(fs);
        }

        public static Replay Load(string path)
        {
            using (FileStream fs = File.OpenRead(path))
                return ReadFrom(fs);
        }

        public static Replay ReadFrom(Stream stream)
        {
            using (var r = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                byte[] magic = r.ReadBytes(Magic.Length);
                if (!magic.AsSpan().SequenceEqual(Magic))
                    throw new InvalidDataException("Not a Rewindable replay (bad magic).");
                int version = r.ReadInt32();
                if (version != FormatVersion)
                    throw new InvalidDataException("Unsupported replay version " + version + " (this build reads " + FormatVersion + ").");

                int metadataCount = r.ReadInt32();
                if (metadataCount < 0 || metadataCount > 100_000)
                    throw new InvalidDataException("Corrupt replay metadata.");
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < metadataCount; i++)
                {
                    string key = r.ReadString();
                    metadata[key] = r.ReadString();
                }

                SnapshotLayout layout = SnapshotLayout.ReadFrom(r);
                int startFrame = r.ReadInt32();
                int inputBytes = r.ReadInt32();
                int frameCount = r.ReadInt32();
                if (inputBytes <= 0 || frameCount < 0 || (long)inputBytes * frameCount > int.MaxValue)
                    throw new InvalidDataException("Corrupt replay header.");
                byte[] inputs = ReadExactly(r, inputBytes * frameCount);

                int checkpointCount = r.ReadInt32();
                if (checkpointCount <= 0 || checkpointCount > frameCount + 1)
                    throw new InvalidDataException("Corrupt replay checkpoints.");
                var checkpoints = new ReplayCheckpoint[checkpointCount];
                for (int i = 0; i < checkpointCount; i++)
                {
                    int frame = r.ReadInt32();
                    ulong sum = r.ReadUInt64();
                    checkpoints[i] = new ReplayCheckpoint(frame, sum, ReadExactly(r, layout.Size));
                }
                return new Replay(layout, startFrame, inputBytes, inputs, checkpoints, metadata);
            }
        }

        private static byte[] ReadExactly(BinaryReader r, int count)
        {
            byte[] bytes = r.ReadBytes(count);
            if (bytes.Length != count)
                throw new EndOfStreamException("Replay is truncated.");
            return bytes;
        }
    }

    /// <summary>Outcome of <see cref="Replay.Verify{T}"/>.</summary>
    public enum ReplayStatus
    {
        /// <summary>Every keyframe was reproduced.</summary>
        Matched,

        /// <summary>A keyframe's state differs: the simulation no longer reproduces the recording.</summary>
        Diverged,

        /// <summary>The state matches but the game's checksum differs: the checksum reads data outside the state.</summary>
        ChecksumMismatch,

        /// <summary>The replay was recorded with a different state layout and cannot be re-simulated.</summary>
        Incompatible,
    }

    public sealed class ReplayResult
    {
        private ReplayResult(Replay replay, ReplayStatus status)
        {
            Replay = replay;
            Status = status;
        }

        public Replay Replay { get; }
        public ReplayStatus Status { get; }

        /// <summary>Keyframes reproduced (the start state excluded).</summary>
        public int CheckpointsVerified { get; private set; }

        /// <summary>Frames simulated before stopping.</summary>
        public int FramesRun { get; private set; }

        /// <summary>The keyframe that failed, or -1.</summary>
        public int FailedFrame { get; private set; } = -1;

        /// <summary>The last keyframe that matched before <see cref="FailedFrame"/> (the divergence lies after it).</summary>
        public int LastMatchingFrame { get; private set; } = -1;

        /// <summary>Recorded vs re-simulated fields at <see cref="FailedFrame"/> (Diverged only).</summary>
        public IReadOnlyList<FieldDiff> Differences { get; private set; } = Array.Empty<FieldDiff>();

        public ulong ExpectedChecksum { get; private set; }
        public ulong ActualChecksum { get; private set; }

        /// <summary>Why the replay could not be run (Incompatible only).</summary>
        public string Problem { get; private set; } = string.Empty;

        public bool Succeeded => Status == ReplayStatus.Matched;

        internal static ReplayResult Matched(Replay replay, int checkpoints) =>
            new ReplayResult(replay, ReplayStatus.Matched) { CheckpointsVerified = checkpoints, FramesRun = replay.FrameCount };

        internal static ReplayResult Diverged(Replay replay, int frame, int lastGood, IReadOnlyList<FieldDiff> diffs, int framesRun) =>
            new ReplayResult(replay, ReplayStatus.Diverged)
            {
                FailedFrame = frame, LastMatchingFrame = lastGood, Differences = diffs, FramesRun = framesRun,
                CheckpointsVerified = Count(replay, lastGood),
            };

        internal static ReplayResult ChecksumMismatch(Replay replay, int frame, int lastGood, ulong expected, ulong actual, int framesRun) =>
            new ReplayResult(replay, ReplayStatus.ChecksumMismatch)
            {
                FailedFrame = frame, LastMatchingFrame = lastGood, ExpectedChecksum = expected, ActualChecksum = actual,
                FramesRun = framesRun, CheckpointsVerified = Count(replay, lastGood),
            };

        internal static ReplayResult Incompatible(Replay replay, string problem) =>
            new ReplayResult(replay, ReplayStatus.Incompatible) { Problem = problem };

        private static int Count(Replay replay, int upToFrame)
        {
            int n = 0;
            foreach (ReplayCheckpoint c in replay.Checkpoints)
                if (c.Frame > replay.StartFrame && c.Frame <= upToFrame) n++;
            return n;
        }

        public string ToReport()
        {
            var sb = new StringBuilder();
            switch (Status)
            {
                case ReplayStatus.Matched:
                    sb.Append("replay reproduced: ").Append(FramesRun).Append(" frames, ").Append(CheckpointsVerified).Append(" keyframes verified.");
                    break;
                case ReplayStatus.Diverged:
                    sb.Append("replay diverged at keyframe ").Append(FailedFrame).Append(" (last match: ").Append(LastMatchingFrame).AppendLine("):");
                    foreach (FieldDiff d in Differences)
                        sb.Append("  ").Append(d.Path).Append(": recorded ").Append(d.Left).Append(", now ").Append(d.Right).AppendLine();
                    sb.Append("=> the simulation changed, or is not deterministic, between frames ")
                      .Append(LastMatchingFrame).Append(" and ").Append(FailedFrame).Append('.');
                    break;
                case ReplayStatus.ChecksumMismatch:
                    sb.Append("state matches at keyframe ").Append(FailedFrame).Append(" but the checksum differs (recorded ")
                      .Append(ExpectedChecksum.ToString("x16", CultureInfo.InvariantCulture)).Append(", now ")
                      .Append(ActualChecksum.ToString("x16", CultureInfo.InvariantCulture))
                      .Append("): the checksum reads data outside the snapshot, or its algorithm changed.");
                    break;
                default:
                    sb.Append("cannot replay: ").Append(Problem);
                    break;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Records a match frame by frame. Between keyframes recording does not allocate (input storage grows by
    /// doubling from <c>expectedFrames</c>); every <c>checkpointInterval</c> frames the full state is kept.
    /// </summary>
    public sealed class ReplayRecorder<T> where T : struct, IRollbackState<T>
    {
        private readonly List<ReplayCheckpoint> _checkpoints = new List<ReplayCheckpoint>();
        private byte[] _inputs;
        private int _inputLength;

        // The newest recorded frame when it is not a keyframe, so Build() can end on a keyframe.
        private (int Frame, ulong Checksum, T State) _last = (-1, 0, default);

        /// <param name="start">The state before the first recorded frame.</param>
        /// <param name="startFrame">Frame number of <paramref name="start"/>.</param>
        /// <param name="startChecksum">The game's checksum of <paramref name="start"/> (0 if unused).</param>
        /// <param name="inputBytesPerFrame">Size of each frame's input bytes.</param>
        /// <param name="checkpointInterval">Keyframe every this many frames (and always at the end).</param>
        /// <param name="expectedFrames">Initial input capacity, in frames.</param>
        public ReplayRecorder(in T start, int startFrame, ulong startChecksum, int inputBytesPerFrame,
            int checkpointInterval = 60, int expectedFrames = 60 * 60 * 3)
        {
            if (inputBytesPerFrame <= 0)
                throw new ArgumentOutOfRangeException(nameof(inputBytesPerFrame), inputBytesPerFrame, "Must be positive.");
            if (checkpointInterval <= 0)
                throw new ArgumentOutOfRangeException(nameof(checkpointInterval), checkpointInterval, "Must be positive.");

            StartFrame = startFrame;
            InputBytesPerFrame = inputBytesPerFrame;
            CheckpointInterval = checkpointInterval;
            _inputs = new byte[checked(Math.Max(expectedFrames, 1) * inputBytesPerFrame)];
            _checkpoints.Add(new ReplayCheckpoint(startFrame, startChecksum, RollbackSnapshot.Capture(in start)));
        }

        public int StartFrame { get; }
        public int InputBytesPerFrame { get; }
        public int CheckpointInterval { get; }

        /// <summary>Key/value pairs saved with the replay (rules, characters, players, build...).</summary>
        public Dictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        public int FrameCount => _inputLength / InputBytesPerFrame;

        /// <summary>The frame the next <see cref="Record"/> produces.</summary>
        public int NextFrame => StartFrame + FrameCount + 1;

        /// <summary>
        /// Records one frame: the inputs that were applied and the state (and the game's checksum) they produced.
        /// Call it for final (confirmed) frames only, in order.
        /// </summary>
        public void Record(ReadOnlySpan<byte> inputs, in T resultingState, ulong resultingChecksum)
        {
            if (inputs.Length != InputBytesPerFrame)
                throw new ArgumentException("Expected " + InputBytesPerFrame + " input bytes, got " + inputs.Length + ".", nameof(inputs));

            if (_inputLength + InputBytesPerFrame > _inputs.Length)
                Array.Resize(ref _inputs, checked(_inputs.Length * 2));
            inputs.CopyTo(_inputs.AsSpan(_inputLength));
            _inputLength += InputBytesPerFrame;

            int frame = StartFrame + FrameCount;
            if ((frame - StartFrame) % CheckpointInterval == 0)
                _checkpoints.Add(new ReplayCheckpoint(frame, resultingChecksum, RollbackSnapshot.Capture(in resultingState)));
            else
                _last = (frame, resultingChecksum, resultingState);
        }

        /// <summary>The replay so far, with a keyframe at its last frame. Allocates; recording can continue.</summary>
        public Replay Build()
        {
            var checkpoints = new List<ReplayCheckpoint>(_checkpoints);
            int end = StartFrame + FrameCount;
            if (checkpoints[checkpoints.Count - 1].Frame != end && _last.Frame == end)
                checkpoints.Add(new ReplayCheckpoint(end, _last.Checksum, RollbackSnapshot.Capture(in _last.State)));

            byte[] inputs = _inputs.AsSpan(0, _inputLength).ToArray();
            return new Replay(SnapshotLayout.Of<T>(), StartFrame, InputBytesPerFrame, inputs, checkpoints, Metadata);
        }
    }
}
