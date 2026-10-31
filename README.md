# Rewindable

A C# source generator for deterministic rollback game state. Mark a struct with one attribute and get
**snapshot / restore / checksum / field-level diff** generated at compile time, while field types that would cause
a desync (floats, references, ...) are rejected by the compiler.

Works in plain .NET and in Unity (C# 9, netstandard2.1, Unity 2022.3+).

## Why

With hand-written rollback, every new state field has to be added in four places: save, load, hash and diff.
Miss one and you get a desync that only shows up in online matches. Rewindable generates all four from the struct
definition itself, so they cannot drift apart.

## Install

**.NET** (runtime + generator in one package; *not published yet*):

```
dotnet add package Rewindable
```

**Unity** (`Packages/manifest.json`):

```json
"com.aqua0801.rewindable": "https://github.com/aqua0801/Rewindable.git?path=/unity/com.aqua0801.rewindable#v0.3.0"
```

Then add `Rewindable` to the `references` of every asmdef that declares or uses `[RollbackState]` types. See
[Unity](#unity) for details.

## Quick start

```csharp
using Rewindable;

[RollbackState, FixedPoint(16)]
public partial struct Fixed { public int Raw; }

public enum ActionId : ushort { Idle, Walk, Attack }

[RollbackState]
public partial struct Fighter
{
    public Fixed X, Y;
    public int Hp;
    public ActionId Action;
    public byte Frame;
    [NoRollback] public int DebugCounter;   // excluded from snapshot, checksum and diff
}

[RollbackState]
public partial struct MatchState
{
    public int Tick;
    public Fighter P1, P2;
    public ulong Rng;
}
```

```csharp
var ring = new SnapshotRing<MatchState>(capacity: 16);   // preallocated: Save/TryLoad never allocate
ulong checksum = ring.Save(frame, in state);             // stores the frame, returns its FNV-1a 64 checksum
ring.TryLoad(rollbackFrame, ref state);                  // roll back

foreach (FieldDiff d in RollbackSnapshot.Diff(in mine, in theirs))
    Console.WriteLine(d);                                // P2.X: 3 (raw 196608) != 3.000015259 (raw 196609)
```

Every `[RollbackState]` struct gets `WriteTo`, `ReadFrom`, `DiffAgainst`, `DescribeLayout`, `SnapshotSize` and a
`const int RollbackSnapshotSize`. `RollbackSnapshot` offers `Capture`, `Restore`, `Checksum` and `Diff` on top.

## Diagnostics

Fields are checked at compile time:

| ID | Severity | Rule |
|---|---|---|
| RWD001 | Error | `float` / `double` / `decimal` field |
| RWD002 | Error | Reference-type field (`string`, arrays, classes, events) |
| RWD003 | Error | Struct field whose type is not `[RollbackState]` |
| RWD004 | Warning | Fields spread over several partial declarations (order then follows file path, then position) |
| RWD005 | Error | Auto-property (use a field, or `[field: NoRollback]`) |
| RWD006 | Error | `readonly` field or `readonly struct` |
| RWD007 | Error | `[FixedPoint]` needs exactly one `short` / `int` / `long` field |
| RWD008 | Error | The struct or one of its containing types is not `partial` |
| RWD009 | Error | Not supported yet: generic types, ref structs, fixed buffers, pointers, `nint` |
| RWD010 | Error | `[RollbackValue]` raw member or factory missing, of the wrong type, or not accessible |

Fields marked `[NoRollback]` are exempt. `static` and `const` fields are always ignored.

## Immutable value types: `[RollbackValue]`

Types already designed as `readonly struct` (fixed-point numbers, packed inputs, ...) do not have to become mutable.
Name the member that holds the raw value and the factory that rebuilds it:

```csharp
[RollbackValue(nameof(RawValue), nameof(FromRaw)), FixedPoint(16)]
public readonly struct Fixed
{
    public int RawValue { get; }
    public static Fixed FromRaw(int raw) => ...;
}
```

The snapshot stores only the raw value and restore calls `FromRaw(raw)`, so `FromRaw(x.RawValue)` must equal `x`.
With `[FixedPoint]`, diffs show the value as a decimal.

## Snapshot format

- Fields in declaration order, little-endian: identical bytes on every platform.
- `bool` is 1 byte, `char` 2 bytes, enums use their underlying type.
- Each type exposes `public const int RollbackSnapshotSize`; nested types are summed at compile time.

## Diagnosing online desyncs

Peers usually exchange only hashes, so a mismatch tells you *that* a frame went wrong, not *what*.
`DesyncRecorder<T>` keeps the last N confirmed frames (snapshot, the game's own checksum, the inputs that produced
the frame) in preallocated buffers, without allocating per frame. When a desync is detected, write them out as a
`.rwdump` file:

```csharp
var recorder = new DesyncRecorder<GameState>(capacity: 240, inputBytesPerFrame: 4);
recorder.Record(frame, in state, state.Hash(), inputsThatProducedThisFrame);   // every confirmed frame

// when the hashes disagree
recorder.CreateDump("P2", desyncFrame).Save("p2.rwdump");
```

A dump carries its own layout (every field's path, type, enum names and fixed-point format), so comparing two dumps
needs neither the game nor its assemblies:

```
> rewindable diff p1.rwdump p2.rwdump --follow 5
common frames: 5..124 (120)
first divergent state: frame 101
  Player1.Meter: 0 | 1
=> frame 100 matches and the inputs into frame 101 match: the step is not deterministic here ...
frame 102: 1 field(s) differ
```

`DumpComparison` classifies the divergence (`DivergenceCause`):

| Cause | Meaning |
|---|---|
| `Step` | Same previous state and same inputs, different result: the simulation step is not deterministic. |
| `Inputs` | Same previous state, different inputs into the divergent frame: an input / netcode problem. |
| `ChecksumOnly` | Every rollback field matches but the checksums differ: the hash reads data outside the snapshot. |
| `BeforeWindow` | Already different at the oldest common frame: the cause is older than the recorded window. |

`rewindable show x.rwdump --frame 101` prints every field of one frame. The CLI lives in `tools/Rewindable.Cli`
and packs as a .NET tool (`dotnet pack`, command name `rewindable`). Exit codes: 0 no divergence, 1 diverged, 2 error.

## Unity

The package lives in `unity/com.aqua0801.rewindable` and is installed by git URL (see [Install](#install)).

- Add `Rewindable` to the `references` of every asmdef that declares or uses `[RollbackState]` types. The source
  generator (`Runtime/Analyzers/Rewindable.Generator.dll`, label `RoslynAnalyzer`) runs for the `Rewindable`
  assembly and every assembly that references it.
- **Window > Rewindable > Desync Dump Diff** compares two `.rwdump` files (or the two newest ones in
  `persistentDataPath/desync`): the report, then every field of any common frame side by side, differences in red.

## Development

```
unity/com.aqua0801.rewindable   Unity package: Runtime (the only copy of the runtime sources), Analyzers, Editor window
src/Rewindable                  .NET project that compiles the package's Runtime folder (NuGet, tests)
src/Rewindable.Generator        Incremental source generator (Roslyn 4.3.1, ForAttributeWithMetadataName)
tools/Rewindable.Cli            `rewindable` CLI (diff / show .rwdump)
tests/Rewindable.Tests              Behaviour tests (generator consumed as an analyzer) + package checks
tests/Rewindable.Generator.Tests    Diagnostics and C# 9 compatibility of the generated code
scripts/unity_meta.py           Creates missing Unity .meta files
```

- The runtime is written in C# 9 for Unity; `src/Rewindable` compiles the package's `Runtime` folder rather than a
  copy of it.
- `dotnet build -c Release` copies the generator into `Runtime/Analyzers/`. Commit that DLL.
- After adding files to the package, run `python scripts/unity_meta.py`: git-installed packages are immutable, so
  every file needs a `.meta`. GUIDs are derived from the path, so re-running never changes them. `PackageTests`
  fails if one is missing.

### Releasing

1. Bump `<Version>` in `Directory.Build.props` and `version` in `unity/com.aqua0801.rewindable/package.json`
   (a test checks they match), and add a `CHANGELOG.md` entry.
2. `dotnet build -c Release`, then commit, including the refreshed analyzer DLL.
3. Tag `vX.Y.Z`, push the tag, and update `#vX.Y.Z` in the consuming projects' `manifest.json`.

## Roadmap

- [x] `[RollbackState]` generator with compile-time diagnostics
- [x] `[RollbackValue]` for immutable value types
- [x] `DesyncRecorder` and `.rwdump` dumps
- [x] `rewindable` CLI
- [x] Unity package (git URL) and Desync Dump Diff window
- [ ] Fixed-capacity array fields (`FixedArray8<T>` and similar)
- [ ] Light / full hash tiers (`[HashTier]`)
- [ ] NuGet release
