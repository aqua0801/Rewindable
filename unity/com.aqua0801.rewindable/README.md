# Rewindable (Unity package)

Compile-time snapshot / restore / checksum / field-level diff for deterministic rollback state, desync dumps and a
dump diff window. Full documentation: the repository README.

## Install

`Packages/manifest.json`:

```json
"com.aqua0801.rewindable": "https://github.com/aqua0801/Rewindable.git?path=/unity/com.aqua0801.rewindable#v0.6.0"
```

Then add `Rewindable` to the `references` of every asmdef that declares `[RollbackState]` types or uses them.
The source generator (`Runtime/Analyzers`) runs for the `Rewindable` assembly and every assembly that references it.

## Contents

- `Runtime/` — attributes, `FixedArray2/4/8/16<T>`, snapshot writer/reader, `SnapshotRing`, `DesyncRecorder`,
  `DesyncDump`, `DumpComparison`, `ReplayRecorder` and `Replay` (C# 9, no engine references).
- `Runtime/Analyzers/Rewindable.Generator.dll` — the source generator (label `RoslynAnalyzer`).
- `Editor/` — **Window > Rewindable > Desync Dump Diff**: compare two machines' `.rwdump` files.
