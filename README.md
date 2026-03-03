# Disk Cloner

A Windows 10/11 system disk cloning application that clones the currently running system disk (including EFI/MSR/Recovery partitions and the main Windows partition C:) to a new target drive connected via USB.

## Features

- **Live Cloning**: Clone your system disk while Windows is running using Volume Shadow Copy Service (VSS)
- **Complete System Clone**: Copies EFI, MSR, Recovery, and Windows partitions
- **Automatic Partition Expansion**: Expands C: partition when target is larger
- **Data Integrity Verification**: Optional full or sampling-based hash verification
- **Safety First**: Multiple confirmation steps, source/target validation, and incomplete clone protection
- **Detailed Progress**: Real-time progress with throughput and time estimates
- **Comprehensive Logging**: Detailed logs for troubleshooting

## Requirements

- Windows 10 or Windows 11
- Administrator privileges (required for disk operations)
- .NET 8.0 Runtime
- USB 3.0+ connection recommended for optimal performance

## Building

```bash
dotnet build DiskCloner.sln
```

## Running

```bash
dotnet run --project DiskCloner.UI/DiskCloner.UI.csproj
```

Or build and run the executable:

```bash
dotnet build --configuration Release
# Run: DiskCloner.UI\bin\Release\net8.0-windows\DiskCloner.UI.exe
```

## Usage

1. **Run as Administrator**: The app will prompt for UAC elevation if needed
2. **Select Source Disk**: The system disk is auto-selected by default
3. **Select Target Disk**: Choose the USB drive to clone to
4. **Review Partitions**: Ensure all required partitions are selected
5. **Configure Options**:
   - Enable VSS for consistent snapshots (recommended)
   - Keep full verification enabled for maximum reliability
   - Enable automatic partition expansion
   - "Allow smaller target disk" is OFF by default (enable only when needed)
6. **Preview**: Review the operation summary
7. **Confirm**: Type `CLONE` exactly to enable and start the cloning process
8. **Wait**: The cloning will proceed with progress updates
9. **Complete**: Follow the next steps to test the cloned disk

## Safety Features

- **Source/Target Validation**: Cannot clone to the system disk
- **Multiple Confirmations**: Review + preview + type "CLONE" confirmation
- **Size Check**: Warns if target is smaller than source
- **Incomplete Clone Protection**: Marks target as incomplete if cancelled
- **Detailed Warnings**: Clear messages about destructive nature of operation

## Architecture

### Core Components

- **DiskCloner.Core**: Core library containing:
  - `DiskEnumerator`: Disk and partition discovery using WMI
  - `DiskClonerEngine`: Main cloning logic
  - `VssSnapshotService`: Volume Shadow Copy integration
  - Windows API interop for raw disk access

- **DiskCloner.UI**: WPF application with:
  - Tab-based wizard interface
  - Real-time progress updates
  - Disk/partition selection
  - Configuration options

### Technical Details

1. **System Disk Identification**: Maps drive letter C: to physical disk via WMI
2. **VSS Snapshots**: Creates shadow copies for consistent data
3. **Raw Disk I/O**: Uses `CreateFile` on `\\.\PhysicalDriveN` with `DeviceIoControl`
4. **Partition Management**: Creates partitions via diskpart, expands via diskpart
5. **Integrity Verification**: SHA-256 hashing (full or sampling)
6. **Bootability**: Copies EFI partition contents, BCD preserved by clone

## Limitations

1. **BitLocker**: Detection is implemented but cloning may not work with active BitLocker encryption. Suspend BitLocker before cloning.
2. **Smaller Targets**: Cloning to smaller disks is experimental and may fail.
3. **Hot Plug Issues**: If USB disconnects during clone, operation will fail and target will be marked incomplete.
4. **UEFI vs BIOS**: Tested primarily with UEFI/GPT. MBR/BIOS support is basic.
5. **Boot Configuration**: Boot files are rebuilt on target EFI; if hardware-specific issues remain, manual repair may still be required.

## Logs

Logs are saved next to the launched executable: `clone_YYYYMMDD_HHmmss.log`

## Troubleshooting

### "Cannot access target disk"
- Ensure no other applications are using the target drive
- Disconnect and reconnect the USB drive
- Try a different USB port

### "Hash mismatch during verification"
- USB connection may be unstable
- Try reducing I/O buffer size
- Disable verification if this occurs repeatedly

### "Clone not bootable"
- Enter BIOS/UEFI and verify the new disk is in boot order
- Try running Windows Startup Repair from the new disk
- Check that the EFI partition was copied correctly

## Legal

This software is provided as-is for legitimate system cloning purposes. The authors are not responsible for any data loss or damage that may occur from using this software. Always ensure you have proper backups before performing disk operations.

## License

MIT License - See LICENSE file for details
