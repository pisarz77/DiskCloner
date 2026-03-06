# Execution Plan: DiskClonerEngine Breakup Refactor

## Context

- **Source:** [IMPLEMENTATION_PLAN2.md](file:///Users/pisarz/Documents/projekty/test-diskcloning/IMPLEMENTATION_PLAN2.md) — method-to-class mapping with exact line numbers
- **Target branch:** `vscode/fixing-after-code-review`
- **Current state:** `DiskClonerEngine.cs` = 3,978 lines / 92 methods
- **Goal:** 8 focused services + 1 thin `CloneOrchestrator`, all with interfaces

---

## Prerequisites

Before starting, run once:

```bash
cd /Users/pisarz/Documents/projekty/test-diskcloning
git status                                         # confirm clean working tree
dotnet build DiskCloner.sln                        # confirm baseline builds
dotnet test DiskCloner.UnitTests/DiskCloner.UnitTests.csproj  # confirm tests pass
```

> [!WARNING]
> `DiskCloner.UnitTests` is **not in `DiskCloner.sln`** yet. Run via project path until Step 4 fixes this.

---

## Phase 0 — Satellite Fixes (Steps 1–4)

**No behaviour changes. Zero functional risk.**

### Step 1 — Extract Inner Classes to `Models/`

Create 4 files, cut-paste classes from engine (L49–79), remove from engine, fix references.

```
DiskCloner.Core/Models/BootFinalizationStatus.cs   ← engine L49–56
DiskCloner.Core/Models/VolumeRepairStatus.cs        ← engine L58–65
DiskCloner.Core/Models/SourceReadDescriptor.cs      ← engine L67–72
DiskCloner.Core/Models/QuietModeState.cs            ← engine L74–79
```

Commit: `refactor: extract inner DTO classes to Models`

---

### Step 2 — Deduplicate `FormatBytes`

Create `DiskCloner.Core/Utilities/ByteFormatter.cs`:
```csharp
namespace DiskCloner.Core.Utilities;
public static class ByteFormatter
{
    public static string Format(long bytes)
    {
        // copy body from any of the 5 existing copies
    }
}
```

Replace in 5 files (search `private static string FormatBytes`):
- `DiskInfo.cs` → `ByteFormatter.Format(...)`
- `PartitionInfo.cs` → `ByteFormatter.Format(...)`
- `CloneOperation.cs` (CloneProgress) → `ByteFormatter.Format(...)`
- `DiskClonerEngine.cs` → `ByteFormatter.Format(...)`
- `MainWindow.xaml.cs` → `ByteFormatter.Format(...)`

Add `ByteFormatterTests.cs` to `DiskCloner.UnitTests`.

Commit: `refactor: deduplicate FormatBytes into ByteFormatter`

---

### Step 3 — Consolidate `DetermineBusType` in `DiskEnumerator`

`DiskEnumerator.cs` has two implementations of bus type detection:
- Primary: `DetermineBusType()` at L188
- Duplicate inline at L603

Add RAID branch to the primary method, then replace the L603 inline block with a call to `DetermineBusType(interfaceType, mediaType)`.

Commit: `refactor: consolidate DetermineBusType in DiskEnumerator`

---

### Step 4 — Fix Solution & Test References

1. Fix `DiskCloner.sln` — replace invalid GUID `9C5B2E6G-3F4D-5B8C-0G9E-2D3F4A5B6C7E` with a valid one
2. Add `DiskCloner.UnitTests` project to `.sln`
3. Move `DiskCloner.Core/Utilities/TestHelpers.cs` → `DiskCloner.UnitTests/TestHelpers.cs`
4. Delete `wmi_test.cs` from repo root

Verify:
```bash
dotnet test DiskCloner.sln --verbosity normal
```

Commit: `chore: fix solution references and test project registration`

---

## Phase 1 — Self-Contained Extractions (Steps 5–6)

**Low risk — these services have minimal coupling.**

### Step 5 — Extract `IntegrityVerifier`

New files:
```
DiskCloner.Core/Services/IIntegrityVerifier.cs
DiskCloner.Core/Services/IntegrityVerifier.cs
```

Methods to move from engine (see IMPLEMENTATION_PLAN2.md §6):
`VerifyIntegrityAsync`, `BuildVerificationExclusions`, `FullHashVerificationAsync`,
`SampleHashVerificationAsync`, `ComputeHashAsync`, `GetVerificationLengthBytes`

Engine change — replace body with:
```csharp
result.IntegrityVerified = await _verifier.VerifyAsync(operation, progress);
```

Commit: `refactor: extract IntegrityVerifier service`

---

### Step 6 — Extract `SystemQuietModeService`

New files:
```
DiskCloner.Core/Services/ISystemQuietModeService.cs
DiskCloner.Core/Services/SystemQuietModeService.cs
```

Methods to move (see IMPLEMENTATION_PLAN2.md §5):
`EnterQuietModeAsync`, `ExitQuietModeAsync`, `ResolveOneDriveExecutablePath`,
`QueryServiceStateCodeAsync`, `StopServiceBestEffortAsync`, `StartServiceBestEffortAsync`,
`WaitForServiceStateAsync`

Engine change — replace inline calls with:
```csharp
var quietState = await _quietMode.EnterAsync();
// ... clone ...
await _quietMode.ExitAsync(quietState);
```

Commit: `refactor: extract SystemQuietModeService`

---

## Phase 2 — Core Extractions (Steps 7–11)

**Medium risk — higher coupling, larger moves.**

### Step 7 — Extract `DiskpartService` (~650 lines)

New files:
```
DiskCloner.Core/Services/IDiskpartService.cs
DiskCloner.Core/Services/DiskpartService.cs
```

Move 20 methods (IMPLEMENTATION_PLAN2.md §2). Mark parsing methods `internal static`.

New tests — add `DiskpartParsingTests.cs`:
- `ParsePartitionTable_ValidGptOutput`
- `ParsePartitionTable_LocaleWithCommaDecimal`
- `NormalizeDiskPartType_PrimaryBecomesBasic`
- `TryParseSizeToBytes_GigabyteString`
- `TryParseSizeToBytes_TerabyteString`

Commit: `refactor: extract DiskpartService`

---

### Step 8 — Extract `CloneValidator` (~120 lines)

New files:
```
DiskCloner.Core/Services/ICloneValidator.cs
DiskCloner.Core/Services/CloneValidator.cs
```

Move: `ValidateOperationAsync`, `CalculateTargetLayout`, `CalculateMaximumSystemPartitionBytes`

New tests — add `CloneValidatorTests.cs`:
- `Validate_SameSourceAndTarget_Throws`
- `Validate_TargetTooSmall_Throws`
- `CalculateTargetLayout_ShrinksFitsLargerSource`

Commit: `refactor: extract CloneValidator`

---

### Step 9 — Extract `PartitionCopier` (~750 lines)

New files:
```
DiskCloner.Core/Services/IPartitionCopier.cs
DiskCloner.Core/Services/PartitionCopier.cs
```

Move 8 methods (IMPLEMENTATION_PLAN2.md §3). Constructor takes `ILogger`, `IDiskpartService`, `WindowsApi`.

Commit: `refactor: extract PartitionCopier`

---

### Step 10 — Extract `FileSystemMigrator` (~500 lines)

New files:
```
DiskCloner.Core/Services/IFileSystemMigrator.cs
DiskCloner.Core/Services/FileSystemMigrator.cs
```

Move 22 methods (IMPLEMENTATION_PLAN2.md §4). Constructor takes `ILogger`, `IDiskpartService`, `ISystemQuietModeService`.

New tests — add `RobocopyParsingTests.cs`:
- `IsBenignRobocopyErrorZeroLine_MatchesKnownPattern`
- `IsSuspiciousRobocopyStdoutLine_MatchesCopyFailed`
- `CalculateSafeEta_ZeroProgressReturnsNull`

Commit: `refactor: extract FileSystemMigrator`

---

### Step 11 — Extract `TargetDiskLifecycleManager` (~500 lines)

New files:
```
DiskCloner.Core/Services/ITargetDiskLifecycleManager.cs
DiskCloner.Core/Services/TargetDiskLifecycleManager.cs
```

Move 12 methods (IMPLEMENTATION_PLAN2.md §7). Constructor takes `ILogger`, `IDiskpartService`.

Commit: `refactor: extract TargetDiskLifecycleManager`

---

## Phase 3 — Orchestrator & Wiring (Step 12)

### Step 12 — Create `CloneOrchestrator`, Delete Engine, Wire UI

**12a.** Create `CloneOrchestrator.cs` — copy remaining 5 methods from engine, update constructor:

```csharp
public CloneOrchestrator(
    ILogger logger,
    ICloneValidator validator,
    IDiskpartService diskpartService,
    IPartitionCopier copier,
    IFileSystemMigrator migrator,
    ISystemQuietModeService quietMode,
    IIntegrityVerifier verifier,
    ITargetDiskLifecycleManager lifecycle,
    DiskEnumerator diskEnumerator,
    VssSnapshotService vssService)
```

**12b.** Delete `DiskClonerEngine.cs`

**12c.** Update `MainWindow.xaml.cs`:
```csharp
// Replace DiskClonerEngine instantiation with:
var diskpartSvc   = new DiskpartService(_logger);
var quietMode     = new SystemQuietModeService(_logger);
var validator     = new CloneValidator(_logger, diskpartSvc, _vssService);
var copier        = new PartitionCopier(_logger, diskpartSvc);
var migrator      = new FileSystemMigrator(_logger, diskpartSvc, quietMode);
var verifier      = new IntegrityVerifier(_logger, diskpartSvc);
var lifecycle     = new TargetDiskLifecycleManager(_logger, diskpartSvc);
_engine           = new CloneOrchestrator(_logger, validator, diskpartSvc, copier,
                       migrator, quietMode, verifier, lifecycle, _diskEnumerator, _vssService);
```

**12d.** Final verification:
```bash
dotnet build DiskCloner.sln --configuration Release
dotnet test DiskCloner.sln --verbosity normal
```

Commit: `refactor: introduce CloneOrchestrator, remove monolithic engine`

---

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Missing `using` after move | High | Low | Build check after each step |
| `internal` visibility breaks test access | Medium | Low | Add `[InternalsVisibleTo]` or make `public` |
| UI fails to instantiate new services | Medium | Medium | Step 12c wiring, manual smoke test |
| Diskpart service regression | Low | High | Existing parsing tests |
| Smart copy regression | Low | Critical | Manual USB clone test |

---

## Estimated Effort

| Phase | Steps | Estimated Time |
|---|---|---|
| Phase 0 — Satellite fixes | 1–4 | 1–2 hrs |
| Phase 1 — Easy extractions | 5–6 | 1–2 hrs |
| Phase 2 — Core extractions | 7–11 | 4–6 hrs |
| Phase 3 — Orchestrator | 12 | 1–2 hrs |
| **Total** | | **7–12 hrs** |

---

## Rollback

Every step is a separate commit. Revert any single step with:
```bash
git revert HEAD~1
```
