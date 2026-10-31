# Rewindable

確定性 rollback 遊戲狀態的編譯期產生器：標一個 attribute，自動得到 **Snapshot / Restore / Checksum / 逐欄位 Diff**，並在編譯期擋掉會造成 desync 的欄位型別。

## 為什麼

手寫 rollback 時，每加一個狀態欄位都要同步改四個地方（存、讀、hash、diff），漏一處就是只在連線對戰才出現的 desync。Rewindable 讓這四件事都從同一份 struct 定義產生。

## 用法

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
    [NoRollback] public int DebugCounter;   // 不進 snapshot / checksum / diff
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
var ring = new SnapshotRing<MatchState>(capacity: 16);   // 預先配置，Save/Load 零 GC
ulong checksum = ring.Save(frame, in state);             // 存檔並回傳 FNV-1a 64 checksum
ring.TryLoad(rollbackFrame, ref state);                  // rollback

foreach (var d in RollbackSnapshot.Diff(in mine, in theirs))
    Console.WriteLine(d);                                // P2.X: 3 (raw 196608) != 3.000015259 (raw 196609)
```

## 規則（編譯期診斷）

| ID | 等級 | 內容 |
|---|---|---|
| RWD001 | Error | `float` / `double` / `decimal` 欄位 |
| RWD002 | Error | 參考型別欄位（`string`、陣列、class、event） |
| RWD003 | Error | 巢狀 struct 沒標 `[RollbackState]` |
| RWD004 | Warning | 欄位分散在多個 partial 宣告（順序依檔名＋位置） |
| RWD005 | Error | auto-property（改用欄位，或 `[field: NoRollback]`） |
| RWD006 | Error | `readonly` 欄位或 `readonly struct` |
| RWD007 | Error | `[FixedPoint]` 必須剛好一個 short/int/long 欄位 |
| RWD008 | Error | struct 或其外層型別不是 `partial` |
| RWD009 | Error | 尚未支援：泛型、ref struct、fixed buffer、指標、nint |
| RWD010 | Error | `[RollbackValue]` 的 raw 成員或 factory 不存在、型別不符或無法存取 |

`[NoRollback]` 的欄位不受上述限制。`static` / `const` 欄位一律忽略。

## 不可變的值型別：`[RollbackValue]`

已經設計成 `readonly struct` 的型別（例如定點數、打包好的輸入）不用為了 rollback 改成可變的，只要指定 raw 成員和還原用的 factory：

```csharp
[RollbackValue(nameof(RawValue), nameof(FromRaw)), FixedPoint(16)]
public readonly struct Fixed
{
    public int RawValue { get; }
    public static Fixed FromRaw(int raw) => ...;
}
```

Snapshot 只存 raw，還原時呼叫 `FromRaw(raw)`，所以必須滿足 `FromRaw(x.RawValue) == x`。加上 `[FixedPoint]` 後，diff 會顯示小數。

## Snapshot 格式

- 依宣告順序、little-endian，跨平台位元組一致。
- `bool` 1 byte、`char` 2 bytes、enum 依底層型別。
- 每個型別有 `public const int RollbackSnapshotSize`，巢狀型別在編譯期加總。

## Unity

- Runtime（`src/Rewindable`）以 C# 9 / netstandard2.1 撰寫，可直接放進 Unity。
- Generator target netstandard2.0、Roslyn 4.3.1（Unity 2022.3+）。放進 Unity 時，將 `Rewindable.Generator.dll` 加上 `RoslynAnalyzer` label，並取消所有平台勾選。
- 產生的程式碼已用 C# 9 語言版本測試可編譯。

## 專案結構

```
src/Rewindable               runtime（attribute、SnapshotWriter/Reader、SnapshotRing、Diff）
src/Rewindable.Generator     incremental source generator（ForAttributeWithMetadataName）
tests/Rewindable.Tests             以 analyzer 方式使用 generator 的行為測試
tests/Rewindable.Generator.Tests   診斷規則與 C# 9 編譯相容性測試
```

## Roadmap

- [ ] 固定容量陣列欄位（`FixedArray8<T>` 之類）
- [ ] `DesyncRecorder`：每幀 checksum，對不上時 dump 前後幀
- [ ] Dump 比對 CLI / Unity EditorWindow
- [ ] Light / full 兩層 hash（`[HashTier]`）
- [ ] UPM 套件發佈
