#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rewindable
{
    /// <summary>Most likely reason two machines diverged, as far as two dumps can tell.</summary>
    public enum DivergenceCause
    {
        /// <summary>Every common frame matches (snapshot, inputs and checksum).</summary>
        None,

        /// <summary>The dumps cannot be compared (different layouts, or no frame in common).</summary>
        Incomparable,

        /// <summary>States already differ at the oldest common frame: the cause is older than the recorded window.</summary>
        BeforeWindow,

        /// <summary>Same previous state, different inputs into the first divergent frame: an input / netcode problem.</summary>
        Inputs,

        /// <summary>Same previous state and same inputs, different result: the step is not deterministic (or reads outside the state).</summary>
        Step,

        /// <summary>
        /// Every rollback field matches but the game's checksums differ: the checksum reads something the snapshot does
        /// not hold (a [NoRollback] field, a static), or the two builds hash differently.
        /// </summary>
        ChecksumOnly,

        /// <summary>A gap in the recorded frames right before the divergence prevents telling inputs from step.</summary>
        Unknown,
    }

    /// <summary>Compares two dumps of the same match from different machines.</summary>
    public sealed class DumpComparison
    {
        private DumpComparison(DesyncDump left, DesyncDump right)
        {
            Left = left;
            Right = right;
        }

        public DesyncDump Left { get; }
        public DesyncDump Right { get; }

        public DivergenceCause Cause { get; private set; }

        /// <summary>Why the dumps could not be compared (Incomparable only).</summary>
        public string Problem { get; private set; } = string.Empty;

        public int FirstCommonFrame { get; private set; } = -1;
        public int LastCommonFrame { get; private set; } = -1;
        public int CommonFrameCount { get; private set; }

        /// <summary>First common frame whose snapshots differ, or -1.</summary>
        public int FirstStateDivergence { get; private set; } = -1;

        /// <summary>Field differences at <see cref="FirstStateDivergence"/>.</summary>
        public IReadOnlyList<FieldDiff> StateDiffs { get; private set; } = Array.Empty<FieldDiff>();

        /// <summary>First common frame whose recorded inputs differ, or -1.</summary>
        public int FirstInputDivergence { get; private set; } = -1;

        /// <summary>First common frame whose checksums differ, or -1.</summary>
        public int FirstChecksumDivergence { get; private set; } = -1;

        public static DumpComparison Compare(DesyncDump left, DesyncDump right)
        {
            var result = new DumpComparison(left, right);
            result.Run();
            return result;
        }

        private void Run()
        {
            if (!Left.Layout.IsCompatibleWith(Right.Layout, out string problem))
            {
                Incomparable("layouts differ (" + problem + "); were both dumps made by the same build?");
                return;
            }
            if (Left.InputBytesPerFrame != Right.InputBytesPerFrame)
            {
                Incomparable("input sizes differ (" + Left.InputBytesPerFrame + " vs " + Right.InputBytesPerFrame + " bytes)");
                return;
            }

            DumpFrame? previous = null;
            DumpFrame? previousRight = null;
            foreach (DumpFrame l in Left.Frames)
            {
                if (!Right.TryGetFrame(l.Frame, out DumpFrame r))
                    continue;

                if (CommonFrameCount == 0) FirstCommonFrame = l.Frame;
                LastCommonFrame = l.Frame;
                CommonFrameCount++;

                if (FirstInputDivergence < 0 && !l.Inputs.AsSpan().SequenceEqual(r.Inputs))
                    FirstInputDivergence = l.Frame;
                if (FirstChecksumDivergence < 0 && l.Checksum != r.Checksum)
                    FirstChecksumDivergence = l.Frame;

                if (FirstStateDivergence < 0 && !l.Snapshot.AsSpan().SequenceEqual(r.Snapshot))
                {
                    FirstStateDivergence = l.Frame;
                    StateDiffs = Left.Layout.Compare(l.Snapshot, r.Snapshot);
                    Cause = Classify(l, r, previous, previousRight);
                }

                previous = l;
                previousRight = r;
            }

            if (CommonFrameCount == 0)
            {
                Incomparable("no frame in common (left " + Range(Left) + ", right " + Range(Right) + ")");
                return;
            }

            if (FirstStateDivergence < 0)
                Cause = FirstChecksumDivergence >= 0 ? DivergenceCause.ChecksumOnly : DivergenceCause.None;
        }

        private DivergenceCause Classify(DumpFrame l, DumpFrame r, DumpFrame? previous, DumpFrame? previousRight)
        {
            if (previous == null || previousRight == null)
                return DivergenceCause.BeforeWindow;
            if (previous.Frame != l.Frame - 1)
                return DivergenceCause.Unknown;
            return l.Inputs.AsSpan().SequenceEqual(r.Inputs) ? DivergenceCause.Step : DivergenceCause.Inputs;
        }

        private void Incomparable(string problem)
        {
            Cause = DivergenceCause.Incomparable;
            Problem = problem;
        }

        private static string Range(DesyncDump d) =>
            d.Frames.Count == 0 ? "empty" : d.Frames[0].Frame + ".." + d.Frames[d.Frames.Count - 1].Frame;

        /// <summary>Multi-line, human-readable summary.</summary>
        public string ToReport()
        {
            var sb = new StringBuilder();
            sb.Append("left : ").Append(Describe(Left)).AppendLine();
            sb.Append("right: ").Append(Describe(Right)).AppendLine();

            if (Cause == DivergenceCause.Incomparable)
            {
                sb.Append("cannot compare: ").Append(Problem).AppendLine();
                return sb.ToString();
            }

            sb.Append("common frames: ").Append(FirstCommonFrame).Append("..").Append(LastCommonFrame)
              .Append(" (").Append(CommonFrameCount).Append(')').AppendLine();

            switch (Cause)
            {
                case DivergenceCause.None:
                    sb.AppendLine("no divergence in the common frames.");
                    break;
                case DivergenceCause.ChecksumOnly:
                    sb.Append("every rollback field matches, but checksums differ from frame ").Append(FirstChecksumDivergence).AppendLine(".");
                    sb.AppendLine("=> the checksum reads data outside the rollback fields ([NoRollback], statics), or the builds hash differently.");
                    break;
                default:
                    sb.Append("first divergent state: frame ").Append(FirstStateDivergence).AppendLine();
                    foreach (FieldDiff d in StateDiffs)
                        sb.Append("  ").Append(d.Path).Append(": ").Append(d.Left).Append(" | ").Append(d.Right).AppendLine();
                    sb.Append("=> ").AppendLine(Explain());
                    break;
            }

            if (FirstInputDivergence >= 0)
                sb.Append("first input difference: frame ").Append(FirstInputDivergence).AppendLine();
            return sb.ToString();
        }

        private string Explain()
        {
            int d = FirstStateDivergence;
            switch (Cause)
            {
                case DivergenceCause.Step:
                    return "frame " + (d - 1) + " matches and the inputs into frame " + d
                        + " match: the step is not deterministic here (it reads something outside the state and inputs).";
                case DivergenceCause.Inputs:
                    return "frame " + (d - 1) + " matches but the inputs into frame " + d + " differ: an input / netcode problem.";
                case DivergenceCause.BeforeWindow:
                    return "already different at the oldest common frame: the cause is older; record more frames.";
                default:
                    return "a gap in the recorded frames before frame " + d + " hides the cause.";
            }
        }

        private static string Describe(DesyncDump d) =>
            (d.Label.Length == 0 ? "(no label)" : d.Label)
            + ", frames " + Range(d)
            + (d.MarkFrame >= 0 ? ", flagged at " + d.MarkFrame.ToString(CultureInfo.InvariantCulture) : "")
            + ", " + d.Layout.TypeName;
    }
}
