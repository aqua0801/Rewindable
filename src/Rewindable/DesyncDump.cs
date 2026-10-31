using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Rewindable
{
    /// <summary>One recorded frame: the state's snapshot, the game's own checksum of it, and opaque input bytes.</summary>
    public sealed class DumpFrame
    {
        public DumpFrame(int frame, ulong checksum, byte[] snapshot, byte[] inputs)
        {
            Frame = frame;
            Checksum = checksum;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        }

        public int Frame { get; }

        /// <summary>The checksum the game compares between peers (whatever algorithm it uses).</summary>
        public ulong Checksum { get; }

        public byte[] Snapshot { get; }

        /// <summary>By convention the inputs that produced this frame's state (see <see cref="DesyncRecorder{T}"/>).</summary>
        public byte[] Inputs { get; }
    }

    /// <summary>
    /// A self-describing window of recorded frames from one machine (<c>.rwdump</c>). Two dumps of the same match
    /// from different machines are compared with <see cref="DumpComparison"/> or the <c>rewindable</c> CLI; the
    /// layout travels with the file, so no game assembly is needed to read it.
    /// </summary>
    public sealed class DesyncDump
    {
        public const string FileExtension = ".rwdump";
        public const int FormatVersion = 1;
        private static readonly byte[] Magic = { (byte)'R', (byte)'W', (byte)'D', (byte)'U', (byte)'M', (byte)'P', 0, 0 };

        private readonly DumpFrame[] _frames;

        public DesyncDump(string label, int markFrame, long createdUnixMs, SnapshotLayout layout, int inputBytesPerFrame,
            IReadOnlyList<DumpFrame> frames)
        {
            Label = label ?? string.Empty;
            MarkFrame = markFrame;
            CreatedUnixMs = createdUnixMs;
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            InputBytesPerFrame = inputBytesPerFrame;
            _frames = new DumpFrame[frames.Count];
            for (int i = 0; i < frames.Count; i++)
            {
                DumpFrame f = frames[i];
                if (f.Snapshot.Length != layout.Size)
                    throw new ArgumentException("Frame " + f.Frame + " snapshot is " + f.Snapshot.Length + " bytes, layout is " + layout.Size + ".");
                if (f.Inputs.Length != inputBytesPerFrame)
                    throw new ArgumentException("Frame " + f.Frame + " inputs are " + f.Inputs.Length + " bytes, expected " + inputBytesPerFrame + ".");
                if (i > 0 && f.Frame <= _frames[i - 1].Frame)
                    throw new ArgumentException("Frames must be strictly ascending.");
                _frames[i] = f;
            }
        }

        /// <summary>Free text identifying the machine / side, e.g. "P2 (client)".</summary>
        public string Label { get; }

        /// <summary>The frame the game flagged (e.g. the check frame whose hashes disagreed), or -1.</summary>
        public int MarkFrame { get; }

        public long CreatedUnixMs { get; }
        public SnapshotLayout Layout { get; }
        public int InputBytesPerFrame { get; }
        public IReadOnlyList<DumpFrame> Frames => _frames;

        public bool TryGetFrame(int frame, out DumpFrame result)
        {
            int lo = 0, hi = _frames.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int f = _frames[mid].Frame;
                if (f == frame)
                {
                    result = _frames[mid];
                    return true;
                }
                if (f < frame) lo = mid + 1;
                else hi = mid - 1;
            }
            result = null!;
            return false;
        }

        public void WriteTo(Stream stream)
        {
            using (var w = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(Label);
                w.Write(MarkFrame);
                w.Write(CreatedUnixMs);
                Layout.WriteTo(w);
                w.Write(InputBytesPerFrame);
                w.Write(_frames.Length);
                foreach (DumpFrame f in _frames)
                {
                    w.Write(f.Frame);
                    w.Write(f.Checksum);
                    w.Write(f.Snapshot);
                    w.Write(f.Inputs);
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
            using (var fs = File.Create(path))
                WriteTo(fs);
        }

        public static DesyncDump Load(string path)
        {
            using (var fs = File.OpenRead(path))
                return ReadFrom(fs);
        }

        public static DesyncDump ReadFrom(Stream stream)
        {
            using (var r = new BinaryReader(stream, new UTF8Encoding(false), leaveOpen: true))
            {
                byte[] magic = r.ReadBytes(Magic.Length);
                if (!magic.AsSpan().SequenceEqual(Magic))
                    throw new InvalidDataException("Not a Rewindable dump (bad magic).");
                int version = r.ReadInt32();
                if (version != FormatVersion)
                    throw new InvalidDataException("Unsupported dump version " + version + " (this build reads " + FormatVersion + ").");

                string label = r.ReadString();
                int mark = r.ReadInt32();
                long created = r.ReadInt64();
                SnapshotLayout layout = SnapshotLayout.ReadFrom(r);
                int inputBytes = r.ReadInt32();
                int count = r.ReadInt32();
                if (inputBytes < 0 || count < 0)
                    throw new InvalidDataException("Corrupt dump header.");

                var frames = new DumpFrame[count];
                for (int i = 0; i < count; i++)
                {
                    int frame = r.ReadInt32();
                    ulong checksum = r.ReadUInt64();
                    byte[] snapshot = ReadExactly(r, layout.Size);
                    byte[] inputs = ReadExactly(r, inputBytes);
                    frames[i] = new DumpFrame(frame, checksum, snapshot, inputs);
                }
                return new DesyncDump(label, mark, created, layout, inputBytes, frames);
            }
        }

        private static byte[] ReadExactly(BinaryReader r, int count)
        {
            byte[] bytes = r.ReadBytes(count);
            if (bytes.Length != count)
                throw new EndOfStreamException("Dump is truncated.");
            return bytes;
        }
    }

    /// <summary>
    /// Keeps the last <see cref="Capacity"/> recorded frames in preallocated buffers (no allocation per frame) and
    /// turns them into a <see cref="DesyncDump"/> when a desync is detected.
    /// Record every final (confirmed) frame, together with the inputs that produced it.
    /// </summary>
    public sealed class DesyncRecorder<T> where T : struct, IRollbackState<T>
    {
        private readonly int[] _frames;
        private readonly ulong[] _checksums;
        private readonly byte[] _snapshots;
        private readonly byte[] _inputs;
        private readonly int _stride;
        private int _next;

        public DesyncRecorder(int capacity, int inputBytesPerFrame)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be positive.");
            if (inputBytesPerFrame < 0) throw new ArgumentOutOfRangeException(nameof(inputBytesPerFrame), inputBytesPerFrame, "Must not be negative.");

            Capacity = capacity;
            InputBytesPerFrame = inputBytesPerFrame;
            Layout = SnapshotLayout.Of<T>();
            _stride = Layout.Size;
            _frames = new int[capacity];
            _checksums = new ulong[capacity];
            _snapshots = new byte[checked(capacity * _stride)];
            _inputs = new byte[checked(capacity * inputBytesPerFrame)];
        }

        public int Capacity { get; }
        public int InputBytesPerFrame { get; }
        public SnapshotLayout Layout { get; }

        /// <summary>Frames currently held (at most Capacity).</summary>
        public int Count { get; private set; }

        /// <summary>Last recorded frame, or -1 when empty.</summary>
        public int NewestFrame => Count == 0 ? -1 : _frames[(_next - 1 + Capacity) % Capacity];

        /// <summary>Oldest frame still held, or -1 when empty.</summary>
        public int OldestFrame => Count == 0 ? -1 : _frames[(_next - Count + Capacity) % Capacity];

        /// <summary>
        /// Records a frame. Frames must be recorded in strictly ascending order; <paramref name="inputs"/> must be
        /// <see cref="InputBytesPerFrame"/> bytes (by convention: the inputs that produced this state).
        /// </summary>
        public void Record(int frame, in T state, ulong checksum, ReadOnlySpan<byte> inputs)
        {
            if (Count > 0 && frame <= NewestFrame)
                throw new ArgumentOutOfRangeException(nameof(frame), frame, "Frames must be ascending (newest is " + NewestFrame + ").");
            if (inputs.Length != InputBytesPerFrame)
                throw new ArgumentException("Expected " + InputBytesPerFrame + " input bytes, got " + inputs.Length + ".", nameof(inputs));

            int slot = _next;
            var writer = new SnapshotWriter(_snapshots.AsSpan(slot * _stride, _stride));
            state.WriteTo(ref writer);
            inputs.CopyTo(_inputs.AsSpan(slot * InputBytesPerFrame, InputBytesPerFrame));
            _frames[slot] = frame;
            _checksums[slot] = checksum;

            _next = (_next + 1) % Capacity;
            if (Count < Capacity) Count++;
        }

        public void Clear()
        {
            Count = 0;
            _next = 0;
        }

        /// <summary>Copies the held frames, oldest first, into a dump. Allocates; call it once, on desync.</summary>
        public DesyncDump CreateDump(string label, int markFrame)
        {
            var frames = new DumpFrame[Count];
            int first = (_next - Count + Capacity) % Capacity;
            for (int i = 0; i < Count; i++)
            {
                int slot = (first + i) % Capacity;
                frames[i] = new DumpFrame(
                    _frames[slot],
                    _checksums[slot],
                    _snapshots.AsSpan(slot * _stride, _stride).ToArray(),
                    _inputs.AsSpan(slot * InputBytesPerFrame, InputBytesPerFrame).ToArray());
            }
            return new DesyncDump(label, markFrame, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Layout, InputBytesPerFrame, frames);
        }
    }
}
