#nullable enable
using System.Collections.Generic;
using System.Globalization;

namespace Rewindable
{
    /// <summary>Receives field-level differences from generated <c>DiffAgainst</c>.</summary>
    public interface IDiffSink
    {
        void Report(string path, string left, string right);
    }

    /// <summary>One differing leaf field, e.g. <c>P2.VelX: 3.25 != 3.2500153</c>.</summary>
    public readonly struct FieldDiff
    {
        public FieldDiff(string path, string left, string right)
        {
            Path = path;
            Left = left;
            Right = right;
        }

        public string Path { get; }
        public string Left { get; }
        public string Right { get; }

        public override string ToString() => Path + ": " + Left + " != " + Right;
    }

    /// <summary>Collects diffs into a list.</summary>
    public sealed class DiffCollector : IDiffSink
    {
        public List<FieldDiff> Diffs { get; } = new List<FieldDiff>();

        public void Report(string path, string left, string right) => Diffs.Add(new FieldDiff(path, left, right));
    }

    /// <summary>Formatting helpers used by generated diff code. Culture-invariant.</summary>
    public static class RollbackFormat
    {
        public const string RootPath = "<root>";

        public static string Join(string path, string field) => string.IsNullOrEmpty(path) ? field : path + "." + field;

        public static string Path(string path) => string.IsNullOrEmpty(path) ? RootPath : path;

        public static string Of(bool value) => value ? "true" : "false";
        public static string Of(char value) => "'" + value + "' (U+" + ((int)value).ToString("X4", CultureInfo.InvariantCulture) + ")";
        public static string Of(sbyte value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(byte value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(short value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(ushort value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(int value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(uint value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(long value) => value.ToString(CultureInfo.InvariantCulture);
        public static string Of(ulong value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Shows a fixed-point raw value as decimal plus raw, e.g. <c>1.5 (raw 98304)</c>.
        /// Uses double for display only; never feed this back into simulation.
        /// </summary>
        public static string FixedPoint(long raw, int fractionalBits)
        {
            double value = raw / (double)(1L << fractionalBits);
            return value.ToString("0.#########", CultureInfo.InvariantCulture)
                + " (raw " + raw.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
