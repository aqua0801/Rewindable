# Changelog

## [0.5.0]

- `SnapshotLayout.Fingerprint`: a frozen 64-bit hash of the layout's shape (paths, encodings, fixed-point bits, enum
  members; not type names). Shown by `rewindable show`, the comparison report and the Desync Dump Diff window.
- `SnapshotLayout.DescribeDifferences` lists added (`+`), removed (`-`) and changed (`~`) fields between two layouts.
- `IsCompatibleWith` now compares fingerprints, so dumps of a renamed or moved type stay comparable; incomparable
  dumps explain which fields differ.

## [0.4.0]

- Fixed-capacity arrays: `FixedArray2/4/8/16<T>` and `[RollbackArray(n)]` for custom ones. Elements may be integers,
  enums, `[RollbackValue]` or `[RollbackState]` types; diffs and layouts name them `Field[i]` (RWD011).
- MIT license.

## [0.3.0]

- Unity package (git URL install), runtime sources shared with the .NET build.
- Desync dumps: `DesyncRecorder`, self-describing `.rwdump`, `DumpComparison` (Step / Inputs / ChecksumOnly / BeforeWindow).
- **Window > Rewindable > Desync Dump Diff**.
- `rewindable` CLI (`diff`, `show`) in the repository's `tools/`.

## [0.2.0]

- `[RollbackValue]` for immutable value wrappers (RWD010).

## [0.1.0]

- `[RollbackState]` source generator: snapshot, restore, size, field diff; diagnostics RWD001–RWD009.
