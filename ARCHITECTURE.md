# Disk Cloner - Architecture and Design Document

## 1. Overview

This document describes the architecture and design of the Disk Cloner application, a Windows system disk cloning tool that operates while the system is running.

### 1.1 Primary Goals

1. Clone the currently running system disk to a USB-connected target drive
2. Use VSS snapshots for consistent source data
3. Preserve entire disk layout (GPT/UEFI with MBR fallback)
4. Automatically expand Windows partition on larger targets
5. Provide robust safety checks and user confirmation flow
6. Verify data integrity during/after cloning

### 1.2 Technology Stack

- **Language**: C# .NET 8
- **UI Framework**: WPF (Windows Presentation Foundation)
- **Disk APIs**: Windows API via P/Invoke (CreateFile, DeviceIoControl)
- **Management APIs**: WMI (Windows Management Instrumentation)
- **Snapshots**: Volume Shadow Copy Service (VSS)
- **Partition Management**: diskpart.exe scripting

## 2. Architecture

### 2.1 High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         DiskCloner.UI                          │
│                    (WPF Application Layer)                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐         │
│  │ MainWindow   │  │ ViewModels   │  │  Progress    │         │
│  │   (XAML)     │  │              │  │  Reporting   │         │
│  └──────────────┘  └──────────────┘  └──────────────┘         │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                       DiskCloner.Core                            │
│                      (Business Logic Layer)                     │
│  ┌─────────────────────────────────────────────────────────────┐ │
│  │                    DiskClonerEngine                         │ │
│  │  - Clone orchestration                                     │ │
│  │  - Progress coordination                                    │ │
│  │  - Safety validation                                       │ │
│  └─────────────────────────────────────────────────────────────┘ │
│  ┌─────────────────┐  ┌─────────────────┐  ┌──────────────────┐   │
│  │ DiskEnumerator │  │ VssSnapshotSvc  │  │   DataVerifier  │   │
│  │ - Disk discovery│  │ - VSS creation │  │ - Hash checking │   │
│  │ - Partition info│  │ - Cleanup      │  │                 │   │
│  └─────────────────┘  └─────────────────┘  └──────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Windows OS Layer                             │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────────┐   │
│  │ WMI APIs │  │ VSS APIs │  │ Raw Disk │  │  diskpart.exe │   │
│  │          │  │          │  │   I/O    │  │              │   │
│  └──────────┘  └──────────┘  └──────────┘  └──────────────┘   │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      Hardware Layer                             │
│                    System Disk ↔ USB Target                     │
└─────────────────────────────────────────────────────────────────┘
```

### 2.2 Component Responsibilities

#### DiskCloner.UI
- User interface and interaction
- Disk/partition selection
- Configuration options
- Progress visualization
- Error display

#### DiskCloner.Core
- Business logic and orchestration
- Disk and partition operations
- Safety validation
- Progress reporting

#### DiskEnumerator
- Disk discovery via WMI
- Partition enumeration
- System disk identification
- Physical to logical mapping

#### VssSnapshotService
- VSS snapshot creation
- Snapshot cleanup
- BitLocker detection
- Volume GUID resolution

#### DiskClonerEngine
- Main cloning orchestration
- Partition table replication
- Data copy coordination
- Integrity verification
- Partition expansion
- Bootability verification

## 3. System Disk Identification

### 3.1 Mapping C: to Physical Disk

```
Boot Drive (C:) → Volume GUID → Partition → Physical Disk Number
```

**Implementation Steps:**

1. **Query Win32_LogicalDisk** for C: drive
   - Gets logical disk properties

2. **Query Win32_DiskPartition** containing C:
   - Gets partition properties and DiskIndex

3. **Get Physical Disk Number** from DiskIndex
   - This is the physical disk number (e.g., 0 for \\.\PhysicalDrive0)

4. **Validate** by querying Win32_OperatingSystem.BootDevice

**Code Flow:**
```
Win32_LogicalDisk (DeviceID = 'C:')
  ↓
Win32_DiskPartition (DiskIndex = 0)
  ↓
Physical Drive 0
```

### 3.2 Why This Approach?

- WMI provides reliable mapping without requiring raw disk access
- Works regardless of partition scheme (GPT/MBR)
- Handles dynamic disk situations better than direct enumeration
- Provides additional metadata (model, size, bus type)

### 3.3 Partition Map Construction

1. Query `Win32_DiskPartition` filtered by DiskIndex
2. For each partition, query associated `Win32_LogicalDisk`
3. Extract: partition number, starting offset, size, type
4. Determine partition role:
   - **EFI**: ~100-300MB, FAT32, no drive letter, starts at 1MB
   - **MSR**: 16MB or 128MB, no filesystem, no drive letter
   - **System**: Has C: drive letter, NTFS
   - **Recovery**: Specific sizes (450MB, 500MB, 1GB), no letter

## 4. Volume Shadow Copy Service (VSS) Integration

### 4.1 VSS Purpose

VSS creates a point-in-time snapshot of volumes, ensuring file system consistency during the clone. This is critical for:
- Database files
- Open files
- Registry hives
- Active system files

### 4.2 What is Read from Snapshot vs Raw Disk

**Read from VSS Snapshot:**
- Volume-level data via shadow copy device path
- Consistent point-in-time view
- Used for partition data copying

**Read from Raw Disk:**
- Partition table (MBR/GPT)
- Disk geometry
- Boot sector (first sectors)
- Areas outside volume boundaries

### 4.3 VSS Implementation Strategy

**Method 1: COM Interface (Preferred in production)**
```
IVssBackupComponents
  → CreateSnapshotSet()
  → AddToSnapshotSet()
  → DoSnapshotSet()
  → GetSnapshotDevicePath()
```

**Method 2: vshadow.exe (Used in MVP)**
```
vshadow.exe -p C:
  → Returns shadow copy device path
  → \\?\Volume{GUID}\ShadowCopy{ID}
```

### 4.4 Volume Access During Clone

| Region | Access Method | Consistency |
|--------|---------------|-------------|
| Partition Table | Raw Disk | N/A (static) |
| EFI Partition | VSS Snapshot | VSS-consistent |
| System Partition | VSS Snapshot | VSS-consistent |
| Recovery Partition | VSS Snapshot | VSS-consistent |

### 4.5 Locking/Coordination

```
1. Create VSS snapshot for each volume
2. Hold snapshot handles during copy
3. Read from snapshot device paths
4. Release snapshots after verification complete
5. If cancelled, ensure cleanup before exit
```

**No explicit volume locking is required** because:
- VSS snapshots are read-only
- Source is only read, never written
- Target is written to after preparation

## 5. GPT vs MBR Handling

### 5.1 Detection

```csharp
// Query Win32_DiskDrive.PartitionStyle
// Returns: "GPT" or "MBR"
```

### 5.2 Partition Table Copying Strategy

**GPT (GUID Partition Table):**
```
1. Read protective MBR (LBA 0)
2. Read Primary GPT Header (LBA 1)
3. Read Partition Entries (LBA 2-33)
4. Write to target (same positions)
5. Adjust disk GUID if needed
```

**MBR (Master Boot Record):**
```
1. Read MBR (LBA 0)
2. Read extended partition tables if present
3. Write to target
4. Update disk signature if needed
```

### 5.3 Simplified Approach (MVP)

Instead of raw GPT/MBR binary copying, the MVP uses diskpart:

```
diskpart.exe /s script.txt
  - select disk X
  - clean
  - convert gpt|mbr
  - create partition primary size=... offset=...
```

This approach:
- Is more reliable than raw binary manipulation
- Handles alignment automatically
- Works across Windows versions
- Simplifies error handling

### 5.4 Partition Type Preservation

**GPT:**
- EFI: `C12A7328-F81F-11D2-BA4B-00A0C93EC93B`
- MSR: `E3C9E316-0B5C-4DB8-817D-F92DF00215AE`
- Basic Data: `EBD0A0A2-B9E5-4433-87C0-68B6B72699C7`
- Recovery: `DE94BBA4-06D1-4D40-A16A-BFD50179D6AC`

**MBR:**
- EFI: 0xEF
- NTFS: 0x07
- Recovery: 0x27

## 6. Bootability Implementation

### 6.1 What Makes a Disk Bootable?

**UEFI/GPT:**
1. Valid GPT partition table
2. EFI System Partition (ESP) with:
   - FAT32 filesystem
   - `/EFI/Boot/bootx64.efi` or similar
   - `/EFI/Microsoft/Boot/bootmgfw.efi`
3. BCD (Boot Configuration Data) in EFI partition
4. System partition (Windows)

**BIOS/MBR:**
1. Valid MBR partition table
2. Active/bootable flag on system partition
3. Boot sector on system partition
4. BOOTMGR at root of system partition

### 6.2 Bootability Preservation Strategy

**EFI Partition Copy:**
- Copy entire EFI partition byte-for-byte
- This preserves:
  - Boot manager (`bootmgfw.efi`)
  - BCD store
  - EFI boot entries
  - All other EFI binaries

**No BCD Modification Required:**
- BCD uses device paths like `partition=\Device\HarddiskVolumeX`
- Windows assigns volume numbers dynamically
- The cloned disk will get new volume numbers
- Windows boot manager resolves paths at runtime

**Fallback - bcdboot.exe:**
If boot fails, run:
```
bcdboot C:\Windows /s S: /f UEFI
```
Where S: is the EFI partition mount point.

### 6.3 Bootability Validation

```
1. Check EFI partition exists and is FAT32
2. Verify bootmgr.efi exists in EFI partition
3. Verify BCD store exists in EFI\Microsoft\Boot\
4. Attempt to parse BCD (complex, often skipped in MVP)
5. Result: Mark as "Expected to be bootable"
```

### 6.4 Known Limitations

- **BCD Paths**: May point to wrong volumes if Windows doesn't reassign correctly
- **Secure Boot**: Copies Secure Boot entries but may not validate on new hardware
- **BitLocker**: If encrypted, cloning raw data may not produce bootable result

## 7. Partition Expansion

### 7.1 When to Expand

```
TargetSize > SourceSize AND UserEnabledExpand
```

### 7.2 Expansion Strategy

**Using diskpart:**
```
select disk X
select partition Y
extend
```

This:
- Uses all unallocated space after the partition
- Works for both NTFS and FAT32
- Preserves partition type and attributes
- Automatically aligns

### 7.3 Partition Selection

Expand the Windows system partition:
```csharp
var systemPartition = partitions.FirstOrDefault(p => p.IsSystemPartition);
if (systemPartition != null)
{
    SelectPartition(systemPartition);
    Extend();
}
```

### 7.4 Safety Considerations

- Only expand if target has sufficient unallocated space
- Do NOT shrink (not supported in MVP)
- Do NOT modify EFI/MSR partitions
- Wait until after verification to expand

## 8. Data Copy Implementation

### 8.1 Block Copy Algorithm

```
foreach partition in partitions:
    offset = partition.StartingOffset
    remaining = partition.SizeBytes

    while remaining > 0:
        bytesToRead = min(bufferSize, remaining)
        read(source, offset, buffer, bytesToRead)
        write(target, offset, buffer, bytesToRead)
        offset += bytesToRead
        remaining -= bytesToRead
        reportProgress(bytesToRead)

    flush(target)
```

### 8.2 Buffer Size Considerations

| Buffer Size | Pros | Cons |
|-------------|------|------|
| 16 MB | Low memory, good for small disks | More system calls, slower |
| 64 MB (default) | Balanced performance | Moderate memory usage |
| 128 MB | Good for large disks | Higher memory |
| 256 MB | Maximum throughput | May cause memory pressure |

### 8.3 Sector Alignment

```
// Align buffer to physical sector size
bufferSize = ((bufferSize + sectorSize - 1) / sectorSize) * sectorSize

// Default alignment: 1MB (typical for modern disks)
partitionAlignment = 1 * 1024 * 1024
```

### 8.4 Error Handling

```
try:
    read from source
    write to target
catch IOException:
    if isCancelRequested:
        mark target incomplete
        throw
    else:
        retry (up to 3 times)
        if still failed:
            mark target incomplete
            throw
```

### 8.5 Progress Calculation

```
overallProgress = totalBytesCopied / totalBytesToCopy

partitionProgress = partitionBytesCopied / partitionBytesToCopy

throughput = bytesCopied / timeElapsed

remainingTime = remainingBytes / throughput
```

## 9. Integrity Verification

### 9.1 Sampling vs Full Hash

**Sampling (default):**
- Read 100 random samples per partition
- Compare hashes
- Fast (~20% overhead)
- Good for detecting USB corruption

**Full Hash:**
- Hash entire partition
- Compare source vs target
- Slow (~2.5x overhead)
- Complete verification

### 9.2 Hash Algorithm

SHA-256 for balance of speed and collision resistance:
```
hash = SHA256.ComputeHash(data)
```

### 9.3 Sampling Strategy

```
sampleCount = 100
sampleSize = 1 MB

for i in 0..sampleCount:
    sampleOffset = partitionOffset + (partitionSize * i / sampleCount)
    sourceHash = Hash(sourceDisk, sampleOffset, sampleSize)
    targetHash = Hash(targetDisk, sampleOffset, sampleSize)
    if sourceHash != targetHash:
        return FAILED

return PASSED
```

### 9.4 Sample Locations

```
Partition: [===========|=========|=========|...]
           ↑          ↑         ↑
        Start      Mid       End
```

Samples distributed evenly across the partition to catch:
- Beginning corruption
- Middle corruption
- End corruption

## 10. Safety and Anti-Brick Rules

### 10.1 Pre-Flight Checks

```
1. Source != Target (disk number comparison)
2. Source is System Disk (or user confirmed otherwise)
3. Target is NOT System Disk
4. Target is writable
5. Target is online
6. Target size >= Source size (unless user opted-in)
7. Boot partitions included in clone list
8. USB connection stable (optional)
```

### 10.2 Confirmation Flow

```
┌─────────────────────────────────────┐
│ Step 1: Select Source/Target        │
│  - Validation at selection time     │
└─────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────┐
│ Step 2: Options                      │
│  - User configures options          │
└─────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────┐
│ Step 3: Preview                     │
│  - Full operation summary           │
│  - Estimated time                   │
│  - User reviews before proceeding   │
└─────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────┐
│ Step 4: "Type CLONE" confirmation   │
│  - Explicit user intent confirmation│
│  - Final safety check              │
└─────────────────────────────────────┘
                │
                ▼
        Begin Cloning
```

### 10.3 Incomplete Clone Protection

If cancelled or failed:
```
1. Mark target as incomplete
   - Write "INCOMPLETE CLONE" to first sector
   - Overwrites partition table

2. User informed:
   - "Target disk marked as incomplete"
   - "Will not boot"
   - "Can be reformatted"
```

### 10.4 Rollback Strategy

**No rollback** is implemented because:
- Cloning is a one-way operation
- No backup of target before clone
- Incomplete marking prevents booting from partial clone

**Future Enhancement:** Could implement:
- Before clone: Store target's partition table
- On failure: Restore original partition table
- Allow recovery of data if clone fails early

## 11. Cancellation Behavior

### 11.1 Graceful Cancellation

```
1. User clicks Cancel
2. Set cancellation token
3. In-progress I/O completes
4. Mark target incomplete
5. Delete VSS snapshots
6. Close all handles
7. Report cancelled status
```

### 11.2 Cleanup After Cancellation

```
┌─────────────┐
│  Cancel     │
│  Requested  │
└──────┬──────┘
       │
       ▼
┌─────────────────┐
│ Mark Target    │
│ as Incomplete  │
└──────┬──────────┘
       │
       ▼
┌─────────────────┐
│ Delete VSS      │
│ Snapshots       │
└──────┬──────────┘
       │
       ▼
┌─────────────────┐
│ Close Handles   │
└──────┬──────────┘
       │
       ▼
┌─────────────────┐
│ Report Cancel   │
│ Status          │
└─────────────────┘
```

## 12. Logging

### 12.1 Log Levels

| Level | Usage |
|-------|-------|
| Debug | Detailed operation details |
| Info | Major operation steps |
| Warning | Non-fatal issues |
| Error | Failures and exceptions |

### 12.2 Log Content

```
[2024-03-01 12:34:56.789] [    INFO] Application started
[2024-03-01 12:34:57.123] [    INFO] Enumerating physical disks...
[2024-03-01 12:34:57.456] [    INFO] Found Disk 0: Samsung SSD 980 1TB (931.51 GB) [SYSTEM]
[2024-03-01 12:34:57.789] [    INFO] Found Disk 1: SanDisk Extreme 512GB (476.94 GB) [USB]
[2024-03-01 12:35:01.234] [    INFO] Creating VSS snapshots...
[2024-03-01 12:35:05.567] [    INFO] Copying EFI System Partition...
[2024-03-01 12:35:10.890] [   DEBUG] Copied 300.00 MB, Speed: 62.5 MB/s
...
```

### 12.3 Log Location

```
%USERPROFILE%\Documents\DiskCloner\Logs\clone_YYYYMMDD_HHmmss.log
```

## 13. 4K Sector and Sector Size Handling

### 13.1 Sector Size Detection

```csharp
DISK_GEOMETRY_EX geometry;
DeviceIoControl(device, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, ...)

logicalSectorSize = geometry.Geometry.BytesPerSector;
physicalSectorSize = geometry.Data.BytesPerPhysicalSector; // In extended data
```

### 13.2 Alignment Rules

```
// Partition alignment: 1MB (2048 x 512-byte sectors)
partitionOffset = 1 * 1024 * 1024;  // 1MB

// Buffer alignment: match physical sector size
bufferSize = ((bufferSize + physicalSectorSize - 1) / physicalSectorSize) * physicalSectorSize;
```

### 13.3 Different Sector Size Scenarios

| Source | Target | Handling |
|--------|--------|----------|
| 512 | 512 | Direct copy |
| 512 | 4K | Convert via Windows (not in MVP) |
| 4K | 4K | Direct copy |
| 4K | 512 | Not supported (source smaller sectors) |

**MVP Limitation:** Assumes 512-byte logical sectors everywhere. 4K native disks work if they report 512-byte logical sectors (emulation mode).

## 14. BitLocker Handling

### 14.1 Detection

```
manage-bde.exe -status C:
  → Parse output for "Protection On"
```

### 14.2 BitLocker Challenges

1. **Raw Data Copy**: Copies encrypted blocks directly
   - Clone will be encrypted
   - May boot if TPM matches new hardware
   - Often fails on different hardware

2. **Key Protection**: TPM binds to hardware
   - Clone may not unlock automatically
   - Recovery key needed

### 14.3 Recommended Workflow

```
Option A: Before Clone
  1. Suspend BitLocker: manage-bde.exe -protectors -disable C:
  2. Clone (data decrypted on-the-fly)
  3. Re-enable on source (optional)
  4. Enable on clone (optional)

Option B: Clone Encrypted (not recommended)
  1. Clone raw encrypted data
  2. Provide recovery key for clone
  3. May require BitLocker recovery on boot
```

### 14.4 MVP Implementation

- Detection is implemented
- Warning shown to user
- User must manually suspend before cloning
- No automatic BitLocker handling

## 15. Assumptions and Limitations

### 15.1 Assumptions

1. Source disk is the system/boot disk
2. Target disk is connected via USB (can be other interfaces)
3. Windows 10/11 with UEFI firmware (primary target)
4. At least 4GB RAM available
5. .NET 8.0 runtime installed
6. Administrator privileges available

### 15.2 Limitations

1. **BitLocker**: Active BitLocker requires manual suspension
2. **Smaller Targets**: Experimental, may fail
3. **4K Sectors**: Only 512-byte logical sector emulation tested
4. **Dynamic Disks**: Not supported
5. **RAID Arrays**: Clones individual disk, not logical RAID
6. **Hot Plug**: USB disconnect causes failure and incomplete clone
7. **BCD Updates**: Skipped in MVP, may require manual fix
8. **Multiple Volumes**: Only partitions on system disk cloned
9. **Recovery Partition**: Copy is best-effort, may not work on all OEM systems
10. **Secure Boot**: Entries copied but not tested on all hardware

### 15.3 Future Improvements

1. **COM VSS Interface**: Use proper VSS COM instead of vshadow.exe
2. **Native Partition Creation**: Use Windows API instead of diskpart
3. **4K Sector Support**: Proper handling of 4K native disks
4. **BitLocker Integration**: Automatic suspend/resume
5. **BCD Repair**: Automatic BCD path correction
6. **Progress Estimation**: Better time estimation using actual throughput
7. **Compression**: Optional compression for faster clones
8. **Differential Cloning**: Only copy changed blocks (requires previous clone)
9. **Multi-Volume Support**: Clone multiple system volumes
10. **USB Reconnect Detection**: Pause on disconnect, resume on reconnect
