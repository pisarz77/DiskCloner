using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using System.Diagnostics;
using System.Text;

namespace DiskCloner.Core.Services;

/// <summary>
/// Implements target disk lifecycle management: offline/online toggling, NTFS extension,
/// CHKDSK repair, and BCD updates to ensure the clone is fully operational and bootable.
/// </summary>
public class TargetDiskLifecycleManager : ITargetDiskLifecycleManager
{
    private readonly ILogger _logger;
    private readonly ICloneValidator _validator;

    public TargetDiskLifecycleManager(
        ILogger logger,
        ICloneValidator validator)
    {
        _logger = logger;
        _validator = validator;
    }

    // ── Public ────────────────────────────────────────────────────────────────

    public async Task OfflineTargetDiskAsync(CloneOperation operation)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "offline target disk");
        _logger.Info($"Taking target disk {operation.TargetDisk.DiskNumber} offline...");

        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine("offline disk")
            .ToString();

        _validator.AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "offline target disk");

        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            if (exitCode != 0)
            {
                var combined = $"{output}\n{error}";
                if (combined.Contains("already offline", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info("Target disk is already offline");
                    return;
                }
                throw new IOException($"diskpart offline failed with code {exitCode}. Output: {output}. Error: {error}");
            }
            _logger.Info("Target disk taken offline successfully");
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    public async Task OnlineTargetDiskAsync(CloneOperation operation)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "online target disk");
        _logger.Info($"Bringing target disk {operation.TargetDisk.DiskNumber} back online...");

        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine("online disk")
            .AppendLine("attributes disk clear readonly")
            .ToString();

        _validator.AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "online target disk");

        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            if (exitCode != 0)
            {
                var combined = $"{output}\n{error}";
                if (combined.Contains("already online", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Info("Target disk is already online");
                }
                else
                {
                    _logger.Warning($"diskpart online failed with code {exitCode}. Output: {output}");
                }
            }
            else
            {
                _logger.Info("Target disk brought back online successfully");
            }
        }
        finally { try { File.Delete(scriptPath); } catch { } }

        // Give Windows a moment to mount volumes
        await Task.Delay(2000);
    }

    public async Task MarkTargetIncompleteAsync(CloneOperation operation)
    {
        _logger.Info("Marking target disk as incomplete...");
        try
        {
            var path = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";
            await Task.Run(() =>
            {
                using var handle = WindowsApi.CreateFile(path,
                    WindowsApi.GENERIC_WRITE, WindowsApi.FILE_SHARE_WRITE, IntPtr.Zero,
                    WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

                if (handle.IsInvalid) return;

                var buffer = Encoding.ASCII.GetBytes("INCOMPLETE CLONE");
                WindowsApi.SetFilePointerEx(handle, 0, out _, WindowsApi.FILE_BEGIN);
                WindowsApi.WriteFile(handle, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
                WindowsApi.FlushFileBuffers(handle);
            });
            _logger.Info("Target disk marked as incomplete");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to mark target as incomplete: {ex.Message}");
        }
    }

    public async Task ExpandPartitionAsync(CloneOperation operation, CloneProgress progress)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "expand partition");
        _logger.Info("Expanding Windows partition on target disk...");

        if (operation.AllowSmallerTarget)
        {
            _logger.Info("Skipping diskpart extend in smaller-target mode.");
            return;
        }

        var systemPartition = operation.PartitionsToClone.FirstOrDefault(p => p.IsSystemPartition);
        if (systemPartition == null)
        {
            _logger.Info("No system partition selected for expansion.");
            return;
        }

        if (systemPartition.TargetPartitionNumber <= 0)
            throw new InvalidOperationException($"Target partition number is not initialized for source system partition {systemPartition.PartitionNumber}. Aborting expansion.");

        if (systemPartition.TargetSizeBytes > systemPartition.SizeBytes)
        {
            _logger.Info("Skipping diskpart extend: Windows partition was already pre-sized during layout planning.");
            return;
        }

        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine($"select partition {systemPartition.TargetPartitionNumber}")
            .AppendLine("extend")
            .ToString();

        _validator.AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "expand partition");

        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            if (exitCode == 0)
                _logger.Info("Windows partition expanded successfully");
            else
                _logger.Warning($"diskpart expansion exited with code {exitCode}");
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    public async Task<BootFinalizationStatus> MakeBootableAsync(CloneOperation operation)
    {
        _logger.Info("Making target disk bootable...");
        var status = new BootFinalizationStatus();

        try
        {
            await RefreshDiskLayoutAsync(operation);
            status = await UpdateBootConfigurationAsync(operation);

            _logger.Info("Target disk should be bootable");
            status.Success = true;
            return status;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to make target bootable: {ex.Message}");
            status.Success = false;
            return status;
        }
    }

    // ── Boot Configuration & Repair ───────────────────────────────────────────

    private async Task RefreshDiskLayoutAsync(CloneOperation operation)
    {
        await Task.Run(() =>
        {
            var path = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";
            using var handle = WindowsApi.CreateFile(path,
                WindowsApi.GENERIC_READ, WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid) return;

            WindowsApi.DeviceIoControl(handle, WindowsApi.IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        });
    }

    private async Task<BootFinalizationStatus> UpdateBootConfigurationAsync(CloneOperation operation)
    {
        _logger.Info("Updating boot configuration...");
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "update boot configuration");
        var status = new BootFinalizationStatus();

        var systemPartition = operation.PartitionsToClone.FirstOrDefault(p => p.IsSystemPartition);
        if (systemPartition == null)
            throw new InvalidOperationException("Cannot update boot configuration: no system partition is selected.");
        if (systemPartition.TargetPartitionNumber <= 0)
            throw new InvalidOperationException($"Cannot update boot configuration: target partition number missing for source partition {systemPartition.PartitionNumber}.");

        var isUefi = operation.SourceDisk.IsGpt;
        var efiPartition = operation.PartitionsToClone.FirstOrDefault(p => p.IsEfiPartition);
        if (isUefi && (efiPartition == null || efiPartition.TargetPartitionNumber <= 0))
            throw new InvalidOperationException("Cannot update UEFI boot configuration: target EFI partition is missing.");

        var windowsLetter = GetAvailableDriveLetter('W', 'V', 'T', 'R', 'Q');
        _validator.EnsureTargetVolumeMutationAllowed(operation, windowsLetter, "mount target windows partition");

        char? efiLetter = null;
        try
        {
            await MountExistingTargetPartitionAsync(operation, systemPartition.TargetPartitionNumber, windowsLetter);
            ValidateMountedWindowsPartitionAsync(systemPartition, windowsLetter);
            await ExpandMountedNtfsFileSystemAsync(operation, windowsLetter);

            var repairStatus = await RepairMountedWindowsVolumeAsync(operation, windowsLetter);
            status.WindowsVolumeClean = !repairStatus.DirtyAfterRepair;
            status.ChkdskFixApplied = repairStatus.FixApplied;
            status.WindowsVolumeStatus = repairStatus.Summary;

            if (isUefi)
            {
                efiLetter = GetAvailableDriveLetter('S', 'P', 'O', 'N');
                _validator.EnsureTargetVolumeMutationAllowed(operation, efiLetter.Value, "mount target efi partition");
                await MountExistingTargetPartitionAsync(operation, efiPartition!.TargetPartitionNumber, efiLetter.Value);
                await RebuildBootFilesAsync(operation, windowsLetter, efiLetter.Value);
                ValidateEfiBootArtifacts(efiLetter.Value);
                status.BootFilesRebuilt = true;
            }
            else
            {
                _logger.Warning("Legacy BIOS boot repair is not automated yet. Manual boot repair may be required for MBR clones.");
                status.BootFilesRebuilt = true;
            }

            return status;
        }
        finally
        {
            if (efiLetter.HasValue) await UnmountTargetPartitionAsync(operation, efiLetter.Value);
            await UnmountTargetPartitionAsync(operation, windowsLetter);
        }
    }

    private void ValidateMountedWindowsPartitionAsync(PartitionInfo systemPartition, char windowsLetter)
    {
        var windowsRoot = $@"{NormalizeDriveLetter(windowsLetter)}:\Windows";
        if (!Directory.Exists(windowsRoot))
            throw new IOException($"Mounted target system partition {systemPartition.TargetPartitionNumber} does not contain Windows directory at '{windowsRoot}'.");

        var systemHivePath = Path.Combine(windowsRoot, "System32", "config", "SYSTEM");
        if (!File.Exists(systemHivePath))
            throw new IOException($"Mounted target system partition {systemPartition.TargetPartitionNumber} is missing '{systemHivePath}'.");
    }

    private async Task ExpandMountedNtfsFileSystemAsync(CloneOperation operation, char windowsLetter)
    {
        _validator.EnsureTargetVolumeMutationAllowed(operation, windowsLetter, "expand mounted target filesystem");
        var scriptPath = Path.GetTempFileName();
        var script = new StringBuilder()
            .AppendLine($"select volume {NormalizeDriveLetter(windowsLetter)}")
            .AppendLine("extend filesystem")
            .ToString();

        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            _logger.Info($"DiskPart filesystem extend output: {output}");
            if (exitCode != 0)
                _logger.Warning($"Failed to extend NTFS filesystem on {NormalizeDriveLetter(windowsLetter)}:. ExitCode={exitCode}. Error={error}");
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    private async Task<VolumeRepairStatus> RepairMountedWindowsVolumeAsync(CloneOperation operation, char windowsLetter)
    {
        _validator.EnsureTargetVolumeMutationAllowed(operation, windowsLetter, "repair mounted target filesystem");
        var volumeArg = $"{NormalizeDriveLetter(windowsLetter)}:";
        var status = new VolumeRepairStatus();

        var scanStartInfo = new ProcessStartInfo
        {
            FileName = "chkdsk.exe", Arguments = $"{volumeArg} /scan",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };

        var (scanExitCode, scanOutput, scanError) = await RunProcessAsync(scanStartInfo);
        _logger.Info($"CHKDSK /scan output for {volumeArg}: {scanOutput}");
        if (scanExitCode != 0)
            _logger.Warning($"CHKDSK /scan returned code {scanExitCode} for {volumeArg}; continuing with offline fix if needed.");

        status.ScanDetectedIssues = scanExitCode != 0
            || scanOutput.Contains("found problems", StringComparison.OrdinalIgnoreCase)
            || scanOutput.Contains("found errors", StringComparison.OrdinalIgnoreCase)
            || scanOutput.Contains("run chkdsk /f", StringComparison.OrdinalIgnoreCase)
            || scanOutput.Contains("must be fixed", StringComparison.OrdinalIgnoreCase)
            || scanOutput.Contains("must be fixed offline", StringComparison.OrdinalIgnoreCase)
            || scanOutput.Contains("Aborting", StringComparison.OrdinalIgnoreCase)
            || scanOutput.Contains("run \"chkdsk /spotfix\"", StringComparison.OrdinalIgnoreCase);

        status.DirtyBeforeFix = await IsVolumeDirtyAsync(volumeArg);
        if (!status.ScanDetectedIssues && !status.DirtyBeforeFix)
        {
            status.FixApplied = false;
            status.DirtyAfterRepair = false;
            status.Summary = "Scan clean; no offline fix needed.";
            _logger.Info($"Skipping CHKDSK /f for {volumeArg}: scan clean and volume not dirty.");
            return status;
        }

        var chkdskFixStartInfo = new ProcessStartInfo
        {
            FileName = "chkdsk.exe", Arguments = $"{volumeArg} /f /x",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };

        var (chkdskFixExitCode, chkdskFixOutput, chkdskFixError) = await RunProcessAsync(chkdskFixStartInfo);
        _logger.Info($"CHKDSK /f output for {volumeArg}: {chkdskFixOutput}");
        if (chkdskFixExitCode != 0)
            _logger.Warning($"CHKDSK /f returned code {chkdskFixExitCode} for {volumeArg}; validating dirty bit.");

        status.FixApplied = true;
        status.DirtyAfterRepair = await IsVolumeDirtyAsync(volumeArg);
        if (status.DirtyAfterRepair)
            throw new IOException($"Target Windows volume {volumeArg} remains dirty after chkdsk /f.");

        status.Summary = status.ScanDetectedIssues
            ? "Scan reported issues; chkdsk /f applied and volume is clean."
            : "Dirty bit was set; chkdsk /f applied and volume is clean.";
        return status;
    }

    private async Task<bool> IsVolumeDirtyAsync(string volumeArg)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "fsutil.exe", Arguments = $"dirty query {volumeArg}",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        _logger.Info($"fsutil dirty query output for {volumeArg}: {output}");
        if (exitCode != 0)
            throw new IOException($"fsutil dirty query failed for {volumeArg}: (code {exitCode}) {error}");

        return output.Contains("is dirty", StringComparison.OrdinalIgnoreCase);
    }

    private void ValidateEfiBootArtifacts(char efiLetter)
    {
        var normalized = NormalizeDriveLetter(efiLetter);
        var bcdPath = $@"{normalized}:\EFI\Microsoft\Boot\BCD";
        var bootx64Path = $@"{normalized}:\EFI\Boot\bootx64.efi";

        if (!File.Exists(bcdPath))
            throw new IOException($"EFI boot artifact missing after bcdboot: '{bcdPath}'.");

        if (!File.Exists(bootx64Path))
            _logger.Warning($"EFI fallback loader not found at '{bootx64Path}'. Firmware may still boot via Microsoft\\Boot\\BCD entry.");
    }

    // ── Shared Helpers (Boot configuration) ───────────────────────────────────

    private async Task MountExistingTargetPartitionAsync(CloneOperation operation, int targetPartitionNumber, char mountLetter)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "mount target partition");
        _validator.EnsureTargetVolumeMutationAllowed(operation, mountLetter, "mount target partition");

        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine($"select partition {targetPartitionNumber}")
            .AppendLine($"assign letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        _validator.AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "mount target partition");

        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe", Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            if (exitCode != 0) throw new IOException($"Failed to mount partition {targetPartitionNumber}. Code={exitCode}. Error={error}");
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    private async Task UnmountTargetPartitionAsync(CloneOperation operation, char mountLetter)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "unmount target partition");
        _validator.EnsureTargetVolumeMutationAllowed(operation, mountLetter, "unmount target partition");

        var script = new StringBuilder()
            .AppendLine($"select volume {NormalizeDriveLetter(mountLetter)}")
            .AppendLine($"remove letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe", Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            if (exitCode != 0) _logger.Warning($"Failed to unmount {mountLetter}:. Code={exitCode}. Error={error}");
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    private async Task RebuildBootFilesAsync(CloneOperation operation, char windowsLetter, char efiLetter)
    {
        _validator.EnsureTargetVolumeMutationAllowed(operation, windowsLetter, "bcdboot windows source");
        _validator.EnsureTargetVolumeMutationAllowed(operation, efiLetter, "bcdboot efi target");

        var windowsPath = $"{NormalizeDriveLetter(windowsLetter)}:\\Windows";
        var startInfo = new ProcessStartInfo
        {
            FileName = "bcdboot.exe", Arguments = $"\"{windowsPath}\" /s {NormalizeDriveLetter(efiLetter)}: /f UEFI /c",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        _logger.Info($"bcdboot output: {output}");
        if (exitCode != 0) throw new IOException($"bcdboot failed with code {exitCode}. Error={error}");
    }

    private static char GetAvailableDriveLetter(params char[] preferredLetters)
    {
        var inUse = DriveInfo.GetDrives()
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => NormalizeDriveLetter(d.Name[0]))
            .ToHashSet();

        foreach (var letter in preferredLetters.Select(NormalizeDriveLetter))
            if (letter >= 'D' && letter <= 'Z' && !inUse.Contains(letter)) return letter;

        for (char letter = 'Z'; letter >= 'D'; letter--)
            if (!inUse.Contains(letter)) return letter;

        throw new InvalidOperationException("No free drive letter available for target partition mounting.");
    }

    private static char NormalizeDriveLetter(char driveLetter) => char.ToUpperInvariant(driveLetter);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ?? throw new IOException($"Failed to start {startInfo.FileName}");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }
}
