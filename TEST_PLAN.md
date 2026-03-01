# Disk Cloner - Test Plan

## Overview

This document outlines the test cases for the Disk Cloner application. Tests cover happy paths, edge cases, error conditions, and recovery scenarios.

## Test Environment

- **OS**: Windows 10/11 (multiple builds)
- **Firmware**: UEFI (primary), BIOS (fallback)
- **Source**: Various system disk sizes and configurations
- **Target**: Various USB drives and internal disks
- **Privileges**: Administrator required

## Test Categories

1. **Functional Tests** - Core cloning functionality
2. **Safety Tests** - Protection against data loss
3. **Edge Cases** - Unusual configurations
4. **Error Handling** - Failure scenarios
5. **Performance Tests** - Speed and resource usage
6. **Compatibility Tests** - Different hardware/configurations

## Test Cases

### TC001: GPT/UEFI Standard Clone (Happy Path)

**Objective:** Verify successful clone of a standard GPT/UEFI system disk.

**Setup:**
- Source: 500GB SSD with Windows 11, GPT partitioning
- Partitions: EFI (300MB), MSR (16MB), Recovery (500MB), C: (Windows)
- Target: 1TB USB drive (USB 3.0)

**Steps:**
1. Launch application with admin privileges
2. Verify auto-selection of system disk as source
3. Select USB drive as target
4. Verify all partitions are selected
5. Use default options (VSS enabled, sampling verification)
6. Generate and review preview
7. Type "CLONE" to confirm
8. Monitor progress to completion
9. Verify no errors in logs

**Expected Results:**
- Clone completes successfully
- Integrity verification passes
- Target is marked as bootable
- All partitions present on target
- C: expanded to use target space

**Cleanup:** Reboot from cloned disk, verify Windows boots successfully

---

### TC002: MBR/BIOS Legacy Clone

**Objective:** Verify clone of an MBR/BIOS system disk.

**Setup:**
- Source: Legacy system with Windows 10, MBR partitioning
- Partitions: System Reserved (100MB), C: (Windows)
- Target: 500GB USB drive

**Steps:**
1. Launch application
2. Verify system disk detection
3. Select target
4. Verify partition detection (System Reserved shown correctly)
5. Run clone with default options

**Expected Results:**
- Clone completes successfully
- MBR partition table created on target
- Boot flag set on system partition
- System boots from target

**Cleanup:** Boot test from cloned disk

---

### TC003: BitLocker Enabled (Suspended)

**Objective:** Verify clone of BitLocker-encrypted system drive.

**Setup:**
- Source: Windows 11 with BitLocker on C:
- BitLocker suspended before test
- Target: 1TB USB drive

**Steps:**
1. Suspend BitLocker: `manage-bde.exe -protectors -disable C:`
2. Launch application
3. Verify BitLocker warning is displayed
4. Proceed with clone
5. Monitor for any encryption-related errors

**Expected Results:**
- Warning displayed about BitLocker
- Clone completes successfully
- Clone is NOT encrypted (since suspended)
- Bootable clone created

**Cleanup:** Re-enable BitLocker on source if desired

---

### TC004: BitLocker Enabled (Active)

**Objective:** Verify behavior with active BitLocker.

**Setup:**
- Source: Windows 11 with active BitLocker on C:
- Target: 1TB USB drive

**Steps:**
1. Ensure BitLocker is active
2. Launch application
3. Note warning displayed
4. Proceed with clone

**Expected Results:**
- Clear warning about BitLocker
- Clone may complete (raw copy of encrypted data)
- Clone is encrypted (same as source)
- Boot from clone may require recovery key

**Note:** This test may fail to produce bootable result; documented limitation.

---

### TC005: Target Larger than Source

**Objective:** Verify automatic partition expansion.

**Setup:**
- Source: 256GB SSD with 200GB used
- Target: 1TB USB drive
- Option: Auto-expand enabled

**Steps:**
1. Configure clone with auto-expand option enabled
2. Execute clone
3. Check final partition sizes on target

**Expected Results:**
- Clone completes successfully
- EFI, MSR, Recovery partitions same size as source
- C: partition on target expanded to use remaining space (~745GB)
- Expanded space is usable in Windows

**Cleanup:** Boot from cloned disk, verify disk size in Disk Management

---

### TC006: Target Same Size as Source

**Objective:** Verify clone to equal-sized target.

**Setup:**
- Source: 512GB SSD (300GB used)
- Target: 512GB USB drive

**Steps:**
1. Run clone with default options
2. Monitor completion
3. Verify partition sizes

**Expected Results:**
- Clone completes successfully
- All partitions same size as source
- No expansion attempted
- Bootable clone created

**Cleanup:** Boot test

---

### TC007: Target Smaller than Source (Rejected)

**Objective:** Verify rejection of smaller target by default.

**Setup:**
- Source: 1TB SSD
- Target: 500GB USB drive

**Steps:**
1. Try to select smaller target
2. Attempt to generate preview

**Expected Results:**
- Warning/error displayed: "Target too small"
- Preview generation blocked
- User must explicitly enable "Allow smaller target" to proceed

---

### TC008: Target Smaller than Source (Allowed)

**Objective:** Verify clone to smaller target with sufficient free space.

**Setup:**
- Source: 500GB SSD (200GB used)
- Target: 300GB USB drive
- Option: "Allow smaller target" enabled

**Steps:**
1. Enable "Allow smaller target" option
2. Verify warning about risk
3. Execute clone

**Expected Results:**
- Clone completes successfully
- All partitions fit within target size
- C: partition not expanded (no space)
- Bootable clone created

**Expected Failure Case:**
- If source has 450GB used, clone should fail
- Error message about insufficient space

---

### TC009: USB Disconnect Mid-Copy

**Objective:** Verify handling of USB disconnection during clone.

**Setup:**
- Source: Standard Windows installation
- Target: USB drive

**Steps:**
1. Start clone operation
2. Wait for copy to begin (after preparation phase)
3. Physically disconnect USB drive
4. Wait for error detection

**Expected Results:**
- Operation fails with IO error
- Error message displayed to user
- Target marked as incomplete
- VSS snapshots cleaned up
- Clear message about failed operation
- No system corruption

**Cleanup:** Reconnect USB, verify it can be reformatted

---

### TC010: Cancellation During Copy

**Objective:** Verify graceful cancellation during active copy.

**Setup:**
- Source: Large system disk (>200GB)
- Target: USB drive

**Steps:**
1. Start clone operation
2. Wait for copy to begin (after VSS creation)
3. Click "Cancel" button
4. Confirm cancellation dialog
5. Wait for cleanup to complete

**Expected Results:**
- Operation marked as cancelled
- Progress stops at current position
- Target disk marked as incomplete
- "INCOMPLETE CLONE" marker written to first sector
- VSS snapshots deleted
- Handles closed properly
- User informed of cancellation
- Can re-format target drive

**Cleanup:** Verify target is not bootable, reformat and retry

---

### TC011: Cancellation During Preparation

**Objective:** Verify cancellation before data copy begins.

**Setup:**
- Source: Standard system disk
- Target: USB drive

**Steps:**
1. Start clone operation
2. Immediately click Cancel (during VSS creation or preparation)
3. Wait for cleanup

**Expected Results:**
- Operation cancelled quickly
- No data copied to target
- VSS snapshots (if any) deleted
- Target untouched (or minimal writes only)
- Clean shutdown

---

### TC012: Recovery Partition Present

**Objective:** Verify clone includes OEM recovery partition.

**Setup:**
- Source: OEM system with recovery partition
- Partitions: EFI, MSR, Recovery (10GB), C:
- Target: Equal or larger USB drive

**Steps:**
1. Verify recovery partition is detected
2. Verify it's selected for cloning
3. Execute clone

**Expected Results:**
- Recovery partition copied to target
- Same size and attributes
- Partition type/ID preserved
- Recovery functionality preserved (may vary by OEM)

**Note:** OEM recovery partitions often have special requirements; basic copying is MVP behavior.

---

### TC013: Recovery Partition Absent

**Objective:** Verify clone without recovery partition.

**Setup:**
- Source: Clean Windows install without recovery partition
- Partitions: EFI, MSR, C: only
- Target: USB drive

**Steps:**
1. Verify only required partitions detected
2. Execute clone

**Expected Results:**
- Clone completes successfully
- No recovery partition on target
- System boots normally
- No errors about missing recovery partition

---

### TC014: VSS Failure (Service Not Running)

**Objective:** Verify fallback when VSS is unavailable.

**Setup:**
- Stop VSS service: `net stop vss`
- Source: Standard system disk
- Target: USB drive

**Steps:**
1. Stop VSS service
2. Launch application
3. Attempt clone with VSS enabled

**Expected Results:**
- Application detects VSS unavailable
- Falls back to direct disk access (with warning)
- Clone may complete but consistency not guaranteed
- Log entry about VSS failure
- User informed of limitation

**Cleanup:** Restart VSS service: `net start vss`

---

### TC015: VSS Verification Disabled

**Objective:** Verify clone without integrity verification.

**Setup:**
- Source: Standard system disk
- Target: USB drive
- Option: Verification disabled

**Steps:**
1. Disable verification in options
2. Execute clone
3. Monitor time to complete

**Expected Results:**
- Clone completes faster than with verification
- No hash verification performed
- Success reported if copy completes
- Log notes verification disabled

---

### TC016: Full Hash Verification

**Objective:** Verify full hash verification of cloned data.

**Setup:**
- Source: Small system disk (<100GB for speed)
- Target: USB drive
- Option: Full hash verification enabled

**Steps:**
1. Enable full hash verification
2. Execute clone
3. Note total time
4. Compare with sampling verification time

**Expected Results:**
- Clone completes successfully
- Full verification takes significantly longer (~2-3x)
- Each partition hash matches
- Integrity verified flag set to true
- Detailed hash verification in logs

---

### TC017: Multiple Clones to Same Target

**Objective:** Verify overwriting existing clone.

**Setup:**
- Source: System disk
- Target: USB drive with previous clone

**Steps:**
1. Run first clone, verify success
2. Run second clone to same target
3. Use "clean" operation (default)

**Expected Results:**
- Second clone completes successfully
- Previous clone completely overwritten
- No corruption from previous data
- Target is bootable after second clone

---

### TC018: Source Equals Target (Rejected)

**Objective:** Verify protection against selecting source as target.

**Setup:**
- Source: System disk (Disk 0)

**Steps:**
1. Try to select Disk 0 as target
2. Verify UI behavior

**Expected Results:**
- System disk not shown in target list
- Cannot select source as target
- Validation error if somehow selected
- Clear message: "Cannot clone to system disk"

---

### TC019: Target Contains Running OS (Rejected)

**Objective:** Verify detection of target with Windows installed.

**Setup:**
- Source: Disk 0 (System)
- Target: Disk 1 with Windows installation

**Steps:**
1. Enumerate disks
2. Try to select Disk 1 as target
3. Generate preview

**Expected Results:**
- Application detects Windows on target
- Warning displayed: "Target contains an operating system"
- Blocks or warns heavily before proceeding
- Not recommended to proceed

**Expected Failure:** If user proceeds, operation may fail or produce non-functional clone.

---

### TC020: Insufficient Disk Space (Intermediate Check)

**Objective:** Verify space check before clone starts.

**Setup:**
- Source: 400GB SSD, 380GB used
- Target: 200GB USB drive
- Option: "Allow smaller target" disabled

**Steps:**
1. Try to configure clone
2. Attempt preview

**Expected Results:**
- Error before operation starts
- Message: "Target disk too small"
- Blocks operation

---

### TC021: Verification Failure Simulation

**Objective:** Verify behavior when verification fails.

**Setup:**
- Use modified code or tool to corrupt a block on target
- Source: Small system disk
- Target: USB drive

**Steps:**
1. Execute clone
2. After copy, corrupt a block on target (simulated)
3. Run verification

**Expected Results:**
- Verification fails
- Error reported to user
- Integrity verified flag false
- Suggestion to re-run clone
- Target may still be usable (depends on corruption location)

**Note:** This is primarily for testing verification logic.

---

### TC022: Low Memory Condition

**Objective:** Verify behavior with limited available memory.

**Setup:**
- Source: Large disk (>500GB)
- Target: USB drive
- System: Simulate low memory or run memory-intensive applications

**Steps:**
1. Start memory-intensive processes
2. Run clone with large buffer size (256MB)
3. Monitor for out-of-memory errors

**Expected Results:**
- Clone may fail gracefully
- OutOfMemoryException caught
- Clear error message
- Suggestion to reduce buffer size
- Target marked incomplete on failure

---

### TC023: Very Large Disk Clone (>1TB)

**Objective:** Verify clone of large system disk.

**Setup:**
- Source: 2TB SSD with Windows
- Target: 2TB USB drive (or larger)

**Steps:**
1. Run clone
2. Monitor for overflow issues
3. Verify progress updates correctly
4. Check completion

**Expected Results:**
- Clone completes successfully
- No integer overflow in byte calculations
- Progress percentage accurate
- All data copied
- Bootable clone

---

### TC024: EFI Partition Missing (Error)

**Objective:** Verify error when EFI partition not included.

**Setup:**
- Source: GPT system disk with EFI partition
- Target: USB drive

**Steps:**
1. Deselect EFI partition in UI
2. Attempt to generate preview or start clone

**Expected Results:**
- Validation error: "EFI partition must be included"
- Clone blocked
- Clear explanation of why EFI is required

---

### TC025: System Partition Missing (Error)

**Objective:** Verify error when system partition not included.

**Setup:**
- Source: System disk with C: partition
- Target: USB drive

**Steps:**
1. Deselect system (C:) partition
2. Attempt to start clone

**Expected Results:**
- Validation error: "System partition must be included"
- Clone blocked

---

### TC026: Progress Reporting Accuracy

**Objective:** Verify progress updates are accurate.

**Setup:**
- Source: Disk with known size
- Target: USB drive

**Steps:**
1. Start clone
2. Record progress at various points:
   - After 25% completion
   - After 50% completion
   - After 75% completion
3. Compare with actual bytes copied (from logs)
4. Verify throughput calculations

**Expected Results:**
- Progress percentage matches actual bytes/total
- Throughput is reasonable (USB 3.0: ~50-150 MB/s)
- Estimated time updates appropriately
- No jumps or stalls in progress

---

### TC027: Multiple Concurrent Snapshots

**Objective:** Verify VSS handles multiple volume snapshots.

**Setup:**
- Source: System disk with multiple volumes (C:, D:)
- Target: USB drive

**Steps:**
1. Configure to clone both C: and D: partitions
2. Enable VSS
3. Execute clone

**Expected Results:**
- Both volumes snapshotted successfully
- Consistent data from both volumes
- Clone completes without snapshot errors

---

### TC028: Log File Creation and Content

**Objective:** Verify proper logging.

**Setup:**
- Source: Standard system disk
- Target: USB drive

**Steps:**
1. Execute clone
2. Locate log file in Documents\DiskCloner\Logs\
3. Review log content

**Expected Results:**
- Log file created with timestamp
- Contains all operation steps
- Contains disk/partition information
- Contains error details (if any)
- Contains progress information
- File is readable and complete

---

### TC029: Application Without Admin Rights

**Objective:** Verify behavior without admin privileges.

**Setup:**
- User account without admin rights

**Steps:**
1. Launch application (should prompt for elevation)
2. If elevated, test with non-admin process

**Expected Results:**
- App requires UAC elevation
- Without elevation, fails gracefully
- Clear message: "Administrator privileges required"
- App exits or blocks operation

---

### TC030: Clean Reinstall Recovery Test

**Objective:** Verify clone can be used after clean reinstall.

**Setup:**
- Clone system disk to USB
- Boot from USB
- Run clean Windows install on original disk
- Clone back from USB

**Steps:**
1. Clone original to USB
2. Boot from USB
3. Clean install Windows on original SSD
4. Boot to Windows on SSD
5. Clone from USB back to SSD

**Expected Results:**
- Both operations complete successfully
- Windows boots after restore
- Data preserved

---

## Test Execution Guidelines

### Pre-Test Checklist

- [ ] System backup created
- [ ] Test environment isolated from production data
- [ ] Administrator privileges confirmed
- [ ] USB drives of various sizes available
- [ ] Log file location known
- [ ] Time estimates recorded

### During Test

- Take screenshots of errors
- Record exact error messages
- Note timing for performance tests
- Save log files for each test
- Document any unexpected behavior

### Post-Test

- Review log files
- Document results
- Clean up test disks
- Update test cases as needed

## Bug Report Template

```
Title: [Brief description]

Test Case: TC###
Environment:
- OS Version:
- Firmware: UEFI/BIOS
- Source Disk:
- Target Disk:

Steps to Reproduce:
1.
2.
3.

Expected Result:
[What should happen]

Actual Result:
[What actually happened]

Error Message:
[Exact error text]

Logs Attached: [Yes/No]

Severity: [Critical/Major/Minor]
```

## Success Criteria

The application is considered test-ready when:
- All critical test cases (TC001-TC010) pass
- Safety mechanisms function correctly
- No data loss in failure scenarios
- Performance meets acceptable benchmarks
- Logging provides sufficient troubleshooting information
