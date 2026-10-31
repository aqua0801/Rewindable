# Changelog

## [0.3.0]

- Unity package (git URL install), runtime sources shared with the .NET build.
- Desync dumps: `DesyncRecorder`, self-describing `.rwdump`, `DumpComparison` (Step / Inputs / ChecksumOnly / BeforeWindow).
- **Window > Rewindable > Desync Dump Diff**.
- `rewindable` CLI (`diff`, `show`) in the repository's `tools/`.

## [0.2.0]

- `[RollbackValue]` for immutable value wrappers (RWD010).

## [0.1.0]

- `[RollbackState]` source generator: snapshot, restore, size, field diff; diagnostics RWD001–RWD009.
