using System.Globalization;
using System.Text;

namespace Rewindable.Cli;

/// <summary>
/// <c>rewindable diff a.rwdump b.rwdump [--follow N]</c> and <c>rewindable show a.rwdump [--frame N]</c>.
/// Exit codes: 0 = no divergence / ok, 1 = diverged, 2 = usage or file error.
/// </summary>
internal static class CliApp
{
    public const int Ok = 0;
    public const int Diverged = 1;
    public const int Error = 2;

    public static int Run(string[] args, TextWriter output)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
                return Usage(output, Ok);

            return args[0] switch
            {
                "diff" => Diff(args, output),
                "show" => Show(args, output),
                _ => Usage(output, Error, "unknown command '" + args[0] + "'"),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            output.WriteLine("error: " + e.Message);
            return Error;
        }
    }

    private static int Diff(string[] args, TextWriter output)
    {
        var positional = new List<string>();
        int follow = 0;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--follow" && i + 1 < args.Length)
                follow = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else
                positional.Add(args[i]);
        }
        if (positional.Count != 2)
            return Usage(output, Error, "diff needs two dump files");

        DesyncDump left = DesyncDump.Load(positional[0]);
        DesyncDump right = DesyncDump.Load(positional[1]);
        DumpComparison comparison = DumpComparison.Compare(left, right);
        output.Write(comparison.ToReport());

        if (follow > 0 && comparison.FirstStateDivergence >= 0)
            FollowDivergence(left, right, comparison, follow, output);

        return comparison.Cause switch
        {
            DivergenceCause.None => Ok,
            DivergenceCause.Incomparable => Error,
            _ => Diverged,
        };
    }

    /// <summary>How the divergence spreads over the next frames: which fields join in.</summary>
    private static void FollowDivergence(DesyncDump left, DesyncDump right, DumpComparison comparison, int count, TextWriter output)
    {
        int from = comparison.FirstStateDivergence;
        var seen = new HashSet<string>(comparison.StateDiffs.Select(d => d.Path));
        int shown = 0;
        foreach (DumpFrame l in left.Frames)
        {
            if (l.Frame <= from || !right.TryGetFrame(l.Frame, out DumpFrame r))
                continue;
            if (shown++ >= count)
                break;

            List<FieldDiff> diffs = left.Layout.Compare(l.Snapshot, r.Snapshot);
            var fresh = diffs.Where(d => seen.Add(d.Path)).Select(d => d.Path).ToList();
            output.Write("frame " + l.Frame + ": " + diffs.Count + " field(s) differ");
            output.WriteLine(fresh.Count > 0 ? ", new: " + string.Join(", ", fresh) : "");
        }
    }

    private static int Show(string[] args, TextWriter output)
    {
        string? path = null;
        int? frame = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--frame" && i + 1 < args.Length)
                frame = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (path == null)
                path = args[i];
            else
                return Usage(output, Error, "unexpected argument '" + args[i] + "'");
        }
        if (path == null)
            return Usage(output, Error, "show needs a dump or replay file");

        if (IsReplay(path))
            return ShowReplay(Replay.Load(path), frame, output);

        DesyncDump dump = DesyncDump.Load(path);
        output.WriteLine("label   : " + (dump.Label.Length == 0 ? "(none)" : dump.Label));
        output.WriteLine("type    : " + dump.Layout.TypeName + " (" + dump.Layout.Fields.Count + " fields, " + dump.Layout.Size + " bytes)");
        output.WriteLine("layout  : " + dump.Layout.FingerprintText);
        output.WriteLine("created : " + DateTimeOffset.FromUnixTimeMilliseconds(dump.CreatedUnixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        output.WriteLine("flagged : " + (dump.MarkFrame >= 0 ? dump.MarkFrame.ToString(CultureInfo.InvariantCulture) : "-"));
        output.WriteLine("frames  : " + (dump.Frames.Count == 0 ? "none" : dump.Frames[0].Frame + ".." + dump.Frames[^1].Frame + " (" + dump.Frames.Count + ")"));

        if (frame is null)
        {
            output.WriteLine();
            output.WriteLine("frame      checksum          inputs");
            foreach (DumpFrame f in dump.Frames)
                output.WriteLine(f.Frame.ToString(CultureInfo.InvariantCulture).PadLeft(8) + "   " + f.Checksum.ToString("x16", CultureInfo.InvariantCulture) + "  " + Hex(f.Inputs));
            return Ok;
        }

        if (!dump.TryGetFrame(frame.Value, out DumpFrame selected))
        {
            output.WriteLine("error: frame " + frame.Value + " is not in the dump");
            return Error;
        }

        output.WriteLine();
        output.WriteLine("frame " + selected.Frame + "  checksum " + selected.Checksum.ToString("x16", CultureInfo.InvariantCulture) + "  inputs " + Hex(selected.Inputs));
        int width = dump.Layout.Fields.Max(f => f.Path.Length);
        foreach (LayoutField f in dump.Layout.Fields)
            output.WriteLine("  " + f.Path.PadRight(width) + "  " + f.Format(selected.Snapshot));
        return Ok;
    }

    private static bool IsReplay(string path)
    {
        byte[] head = new byte[8];
        using (FileStream fs = File.OpenRead(path))
        {
            int read = fs.Read(head, 0, head.Length);
            return read == head.Length && Encoding.ASCII.GetString(head) == "RWREPLAY";
        }
    }

    private static int ShowReplay(Replay replay, int? frame, TextWriter output)
    {
        output.WriteLine("replay  : " + replay.Layout.TypeName + " (" + replay.Layout.Fields.Count + " fields, " + replay.Layout.Size + " bytes)");
        output.WriteLine("layout  : " + replay.Layout.FingerprintText);
        output.WriteLine("frames  : " + replay.StartFrame + ".." + replay.EndFrame + " (" + replay.FrameCount + " recorded, "
            + replay.InputBytesPerFrame + " input bytes each)");
        output.WriteLine("keyframes: " + string.Join(", ", replay.Checkpoints.Select(c => c.Frame.ToString(CultureInfo.InvariantCulture))));
        foreach (KeyValuePair<string, string> pair in replay.Metadata.OrderBy(p => p.Key, StringComparer.Ordinal))
            output.WriteLine("  " + pair.Key + " = " + pair.Value);

        if (frame is null)
            return Ok;

        if (!replay.TryGetCheckpoint(frame.Value, out ReplayCheckpoint keyframe))
        {
            output.WriteLine("error: frame " + frame.Value + " is not a keyframe (the CLI cannot simulate; pick one listed above)");
            return Error;
        }

        output.WriteLine();
        output.WriteLine("keyframe " + keyframe.Frame + "  checksum " + keyframe.Checksum.ToString("x16", CultureInfo.InvariantCulture));
        int width = replay.Layout.Fields.Max(f => f.Path.Length);
        foreach (LayoutField f in replay.Layout.Fields)
            output.WriteLine("  " + f.Path.PadRight(width) + "  " + f.Format(keyframe.Snapshot));
        return Ok;
    }

    private static string Hex(byte[] bytes)
    {
        if (bytes.Length == 0) return "-";
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static int Usage(TextWriter output, int code, string? problem = null)
    {
        if (problem != null) output.WriteLine("error: " + problem);
        output.WriteLine("usage:");
        output.WriteLine("  rewindable diff <left.rwdump> <right.rwdump> [--follow N]   compare two machines' dumps");
        output.WriteLine("  rewindable show <dump.rwdump> [--frame N]                  list frames, or every field of one frame");
        output.WriteLine("  rewindable show <match.rwreplay> [--frame N]               replay summary, or every field of a keyframe");
        output.WriteLine("exit codes: 0 ok / no divergence, 1 diverged, 2 error");
        return code;
    }
}
