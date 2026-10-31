#nullable enable
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Rewindable.Editor
{
    /// <summary>
    /// Window > Rewindable > Desync Dump Diff. Compares two machines' .rwdump files: the report (first divergent frame,
    /// probable cause) and every field of any common frame side by side.
    /// </summary>
    public sealed class DesyncDumpWindow : EditorWindow
    {
        private const string MenuPath = "Window/Rewindable/Desync Dump Diff";
        private static readonly Color DiffColor = new Color(1f, 0.55f, 0.5f);

        [SerializeField] private string _leftPath = string.Empty;
        [SerializeField] private string _rightPath = string.Empty;
        [SerializeField] private int _frame = -1;
        [SerializeField] private bool _onlyDifferences = true;
        [SerializeField] private string _filter = string.Empty;

        private DesyncDump? _left;
        private DesyncDump? _right;
        private DumpComparison? _comparison;
        private string _error = string.Empty;
        private Vector2 _scroll;

        /// <summary>Where games are expected to write dumps: persistentDataPath/desync.</summary>
        public static string DefaultFolder => Path.Combine(Application.persistentDataPath, "desync");

        [MenuItem(MenuPath)]
        public static void Open() => GetWindow<DesyncDumpWindow>("Desync Diff");

        /// <summary>Opens the window on two files and compares them.</summary>
        public static void Open(string leftPath, string rightPath)
        {
            DesyncDumpWindow window = GetWindow<DesyncDumpWindow>("Desync Diff");
            window._leftPath = leftPath;
            window._rightPath = rightPath;
            window.Compare();
        }

        private void OnEnable()
        {
            if (_leftPath.Length > 0 && _rightPath.Length > 0 && File.Exists(_leftPath) && File.Exists(_rightPath))
                Compare();
        }

        private void OnGUI()
        {
            PathRow("Left", ref _leftPath);
            PathRow("Right", ref _rightPath);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Newest two in desync folder"))
                    UseNewestTwo();
                if (GUILayout.Button("Open folder"))
                {
                    Directory.CreateDirectory(DefaultFolder);
                    EditorUtility.RevealInFinder(DefaultFolder + Path.DirectorySeparatorChar);
                }
                using (new EditorGUI.DisabledScope(_leftPath.Length == 0 || _rightPath.Length == 0))
                {
                    if (GUILayout.Button("Compare", GUILayout.Width(90f)))
                        Compare();
                }
            }

            if (_error.Length > 0)
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            if (_comparison == null || _left == null || _right == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_comparison.ToReport().TrimEnd(), Severity(_comparison.Cause));
            if (_comparison.Cause != DivergenceCause.Incomparable)
                DrawFrame(_left, _right, _comparison);
        }

        private void PathRow(string label, ref string path)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                path = EditorGUILayout.TextField(label, path);
                if (GUILayout.Button("...", GUILayout.Width(30f)))
                {
                    string start = File.Exists(path) ? Path.GetDirectoryName(path) ?? DefaultFolder : DefaultFolder;
                    string picked = EditorUtility.OpenFilePanel("Select a desync dump", start, DesyncDump.FileExtension.TrimStart('.'));
                    if (!string.IsNullOrEmpty(picked))
                        path = picked;
                }
            }
        }

        private void UseNewestTwo()
        {
            string[] newest = Directory.Exists(DefaultFolder)
                ? Directory.GetFiles(DefaultFolder, "*" + DesyncDump.FileExtension)
                    .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                    .Take(2)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();

            if (newest.Length < 2)
            {
                _error = "Need two " + DesyncDump.FileExtension + " files in " + DefaultFolder + " (copy the other machine's dump there).";
                return;
            }
            _leftPath = newest[0];
            _rightPath = newest[1];
            Compare();
        }

        private void Compare()
        {
            _error = string.Empty;
            _comparison = null;
            try
            {
                _left = DesyncDump.Load(_leftPath);
                _right = DesyncDump.Load(_rightPath);
                _comparison = DumpComparison.Compare(_left, _right);
                _frame = _comparison.FirstStateDivergence >= 0 ? _comparison.FirstStateDivergence : _comparison.LastCommonFrame;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                _left = _right = null;
                _error = e.Message;
            }
            Repaint();
        }

        private void DrawFrame(DesyncDump left, DesyncDump right, DumpComparison comparison)
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                _frame = EditorGUILayout.IntField("Frame", _frame);
                if (GUILayout.Button("<", GUILayout.Width(26f))) _frame = Neighbour(left, right, _frame, -1);
                if (GUILayout.Button(">", GUILayout.Width(26f))) _frame = Neighbour(left, right, _frame, +1);
                using (new EditorGUI.DisabledScope(comparison.FirstStateDivergence < 0))
                {
                    if (GUILayout.Button("First divergence", GUILayout.Width(120f)))
                        _frame = comparison.FirstStateDivergence;
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                _onlyDifferences = EditorGUILayout.ToggleLeft("Only differences", _onlyDifferences, GUILayout.Width(130f));
                _filter = EditorGUILayout.TextField("Filter", _filter);
            }

            if (!left.TryGetFrame(_frame, out DumpFrame l) || !right.TryGetFrame(_frame, out DumpFrame r))
            {
                EditorGUILayout.HelpBox("Frame " + _frame + " is not in both dumps (common: "
                    + comparison.FirstCommonFrame + ".." + comparison.LastCommonFrame + ").", MessageType.Info);
                return;
            }

            bool inputsDiffer = !l.Inputs.AsSpan().SequenceEqual(r.Inputs);
            EditorGUILayout.LabelField("checksum", l.Checksum.ToString("x16") + "  |  " + r.Checksum.ToString("x16"));
            EditorGUILayout.LabelField("inputs", Hex(l.Inputs) + "  |  " + Hex(r.Inputs) + (inputsDiffer ? "   (differ)" : ""));

            float half = Mathf.Max(80f, (position.width - 40f) * 0.3f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Field", EditorStyles.boldLabel);
                GUILayout.Label(Label(left), EditorStyles.boldLabel, GUILayout.Width(half));
                GUILayout.Label(Label(right), EditorStyles.boldLabel, GUILayout.Width(half));
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            Color previous = GUI.color;
            int shown = 0;
            foreach (LayoutField field in left.Layout.Fields)
            {
                if (_filter.Length > 0 && field.Path.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                bool differs = !l.Snapshot.AsSpan(field.Offset, field.Size).SequenceEqual(r.Snapshot.AsSpan(field.Offset, field.Size));
                if (_onlyDifferences && !differs)
                    continue;

                GUI.color = differs ? DiffColor : previous;
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(field.Path);
                    EditorGUILayout.SelectableLabel(field.Format(l.Snapshot), GUILayout.Width(half), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                    EditorGUILayout.SelectableLabel(field.Format(r.Snapshot), GUILayout.Width(half), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                }
                shown++;
            }
            GUI.color = previous;
            if (shown == 0)
                GUILayout.Label(_onlyDifferences ? "No differing field in this frame." : "No field matches the filter.", EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Next / previous frame present in both dumps.</summary>
        private static int Neighbour(DesyncDump left, DesyncDump right, int frame, int direction)
        {
            var frames = left.Frames;
            if (direction > 0)
            {
                for (int i = 0; i < frames.Count; i++)
                    if (frames[i].Frame > frame && right.TryGetFrame(frames[i].Frame, out _))
                        return frames[i].Frame;
            }
            else
            {
                for (int i = frames.Count - 1; i >= 0; i--)
                    if (frames[i].Frame < frame && right.TryGetFrame(frames[i].Frame, out _))
                        return frames[i].Frame;
            }
            return frame;
        }

        private static MessageType Severity(DivergenceCause cause)
        {
            switch (cause)
            {
                case DivergenceCause.None: return MessageType.Info;
                case DivergenceCause.Incomparable: return MessageType.Error;
                default: return MessageType.Warning;
            }
        }

        private static string Label(DesyncDump dump) => dump.Label.Length > 0 ? dump.Label : "(no label)";

        private static string Hex(byte[] bytes) =>
            bytes.Length == 0 ? "-" : BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }
}
