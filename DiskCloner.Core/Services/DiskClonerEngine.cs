using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskCloner.Core.Services;

/// <summary>
/// Main engine for cloning disks. Handles validation, snapshot creation,
/// data copying, verification, and partition expansion.
/// </summary>
public class DiskClonerEngine
{
    // More tolerant regex to handle locale decimal separators, optional asterisk, extra trailing columns
    private static readonly Regex DiskPartPartitionLineRegex = new(
        @"^\s*\*?\s*Partition\s+(?<number>\d+)\s+(?<type>.+?)\s+(?<sizeValue>\d+[\d.,]*)\s+(?<sizeUnit>KB|MB|GB|TB|B|Bytes)?\s+(?<offsetValue>\d+[\d.,]*)\s+(?<offsetUnit>KB|MB|GB|TB|B|Bytes)?(\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger _logger;
    private readonly DiskEnumerator _diskEnumerator;
    private readonly VssSnapshotService _vssService;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private enum CopyStrategy
    {
        RawBlock,
        SmartBlock,
        FileSystemMigration
    }

    public event Action<CloneProgress>? ProgressUpdate;

    public DiskClonerEngine(ILogger logger, DiskEnumerator diskEnumerator, VssSnapshotService vssService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diskEnumerator = diskEnumerator ?? throw new ArgumentNullException(nameof(diskEnumerator));
        _vssService = vssService ?? throw new ArgumentNullException(nameof(vssService));
    }

    /// <summary>
    /// Executes the cloning operation.
    /// </summary>
    public async Task<CloneResult> CloneAsync(CloneOperation operation)
    {
        var progress = new CloneProgress
        {
            Stage = CloneStage.Validating,
            StatusMessage = "Validating configuration..."
        };

        ReportProgress(progress);

        var startTime = DateTime.UtcNow;
        var result = new CloneResult
        {
            Success = false,
            IsBootable = false
        };
        var targetDiskWasPrepared = false; // Track if we took the disk offline

        try
        {
            // Step 1: Validate the configuration
            await ValidateOperationAsync(operation, progress);

            // Step 2: Create VSS snapshots
            if (operation.UseVss)
            {
                progress.Stage = CloneStage.CreatingSnapshots;
                progress.StatusMessage = "Creating VSS snapshots for consistent data...";
                ReportProgress(progress);

                var volumesToSnapshot = operation.PartitionsToClone
                    .Where(p => p.DriveLetter.HasValue)
                    .Select(p => $@"{p.DriveLetter.Value}:\")
                    .Distinct()
                    .ToList();

                await _vssService.CreateSnapshotsForVolumesAsync(volumesToSnapshot);
            }

            // Step 3: Prepare target disk
            progress.Stage = CloneStage.PreparingTarget;
            progress.StatusMessage = "Preparing target disk...";
            ReportProgress(progress);

            await PrepareTargetDiskAsync(operation, progress);
            targetDiskWasPrepared = true; // Disk is now offline, must be brought back online

            // Step 4: Copy partition data
            progress.Stage = CloneStage.CopyingData;
            progress.StatusMessage = "Copying partition data...";
            ReportProgress(progress);

            progress.TotalPartitions = operation.PartitionsToClone.Count;
            var totalBytesToCopy = operation.PartitionsToClone.Sum(p => p.TargetSizeBytes);
            progress.TotalBytes = totalBytesToCopy;
            var bytesCopied = 0L;
            var migratedPartitionNumbers = new HashSet<int>();

            foreach (var partition in operation.PartitionsToClone)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Operation was cancelled by user");
                }

                progress.CurrentPartition = operation.PartitionsToClone.IndexOf(partition);
                progress.CurrentPartitionName = partition.GetTypeName();
                var strategy = GetCopyStrategy(operation, partition);

                if (strategy == CopyStrategy.FileSystemMigration)
                {
                    progress.StatusMessage = $"Queueing file-system migration for {partition.GetTypeName()} ({partition.SizeDisplay})...";
                    ReportProgress(progress);
                    migratedPartitionNumbers.Add(partition.PartitionNumber);
                    _logger.Info($"Partition {partition.PartitionNumber} will use file-system migration after raw block copies.");
                    continue;
                }

                if (strategy == CopyStrategy.SmartBlock)
                {
                    progress.StatusMessage = $"Smart-copying {partition.GetTypeName()} ({partition.SizeDisplay}) - reading NTFS bitmap...";
                    ReportProgress(progress);
                    bytesCopied = await CopyPartitionSmartAsync(operation, partition, progress, bytesCopied);
                }
                else
                {
                    progress.StatusMessage = $"Copying {partition.GetTypeName()} ({partition.SizeDisplay})...";
                    ReportProgress(progress);
                    bytesCopied = await CopyPartitionAsync(operation, partition, progress, bytesCopied);
                }

                progress.BytesCopied = bytesCopied;
                progress.PercentComplete = (bytesCopied * 100.0) / totalBytesToCopy;
                ReportProgress(progress);
            }

            if (migratedPartitionNumbers.Count > 0)
            {
                progress.StatusMessage = "Bringing target online for NTFS file-system migration...";
                ReportProgress(progress);
                await OnlineTargetDiskAsync(operation);

                foreach (var partition in operation.PartitionsToClone.Where(p => migratedPartitionNumbers.Contains(p.PartitionNumber)))
                {
                    progress.StatusMessage = $"Migrating {partition.GetTypeName()} using file copy...";
                    ReportProgress(progress);
                    await MigratePartitionFileSystemAsync(operation, partition, result);
                    bytesCopied += partition.TargetSizeBytes;
                    progress.BytesCopied = Math.Min(bytesCopied, totalBytesToCopy);
                    progress.PercentComplete = (progress.BytesCopied * 100.0) / totalBytesToCopy;
                    ReportProgress(progress);
                }
            }

            result.BytesCopied = bytesCopied;

            // Step 5: Verify data integrity
            if (operation.VerifyIntegrity)
            {
                progress.Stage = CloneStage.Verifying;
                progress.StatusMessage = "Verifying data integrity...";
                ReportProgress(progress);

                result.IntegrityVerified = await VerifyIntegrityAsync(operation, progress, migratedPartitionNumbers);
                if (!result.IntegrityVerified && operation.StrictVerificationFailureStopsClone)
                {
                    throw new InvalidOperationException("Integrity verification failed; clone aborted due to strict verification setting.");
                }
            }
            else
            {
                result.IntegrityVerified = true; // Not verified but not failed
            }

            // Step 6: Expand partitions
            if (operation.AutoExpandWindowsPartition || operation.AllowSmallerTarget)
            {
                progress.Stage = CloneStage.ExpandingPartitions;
                progress.StatusMessage = operation.AllowSmallerTarget ? "Fixing NTFS metadata..." : "Expanding Windows partition...";
                ReportProgress(progress);

                await ExpandPartitionAsync(operation, progress);
            }

            // Step 7: Finalize and mark as bootable
            result.IsBootable = await MakeBootableAsync(operation);

            // Calculate final statistics
            result.Duration = DateTime.UtcNow - startTime;
            if (result.Duration.TotalSeconds > 0)
            {
                result.AverageThroughputBytesPerSec = result.BytesCopied / result.Duration.TotalSeconds;
            }

            // Build next steps
            BuildNextSteps(result, operation);

            progress.Stage = CloneStage.Completed;
            progress.PercentComplete = 100;
            progress.StatusMessage = "Clone completed successfully!";
            ReportProgress(progress);

            result.Success = true;
            _logger.Info($"Cloning completed successfully in {result.Duration.TotalHours:0}h {result.Duration.Minutes}m {result.Duration.Seconds}s");

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Operation was cancelled by user");

            progress.Stage = CloneStage.Cancelled;
            progress.IsCancelled = true;
            progress.StatusMessage = "Operation was cancelled. Target disk will be brought back online.";
            ReportProgress(progress);

            result.ErrorMessage = "Operation was cancelled";
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error("Cloning operation failed", ex);

            progress.Stage = CloneStage.Failed;
            progress.LastError = ex.Message;
            progress.StatusMessage = $"Operation failed: {ex.Message}";
            ReportProgress(progress);

            result.ErrorMessage = ex.Message;
            result.Exception = ex;
            return result;
        }
        finally
        {
            // Always bring the disk back online if we took it offline
            if (targetDiskWasPrepared)
            {
                try
                {
                    await OnlineTargetDiskAsync(operation);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to bring target disk back online: {ex.Message}");
                }
            }

            // Cleanup VSS
            progress.Stage = CloneStage.Cleanup;
            progress.StatusMessage = "Cleaning up...";
            ReportProgress(progress);

            try
            {
                await _vssService.DeleteSnapshotsAsync();
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error during cleanup: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Validates the cloning operation configuration.
    /// </summary>
    private async Task ValidateOperationAsync(CloneOperation operation, CloneProgress progress)
    {
        _logger.Info("Validating cloning operation...");

        // Check source disk
        if (operation.SourceDisk == null)
            throw new InvalidOperationException("Source disk not specified");

        if (!operation.SourceDisk.IsSystemDisk)
            throw new InvalidOperationException("Source disk must be the system disk");

        // Check target disk
        if (operation.TargetDisk == null)
            throw new InvalidOperationException("Target disk not specified");

        if (operation.TargetDisk.DiskNumber == operation.SourceDisk.DiskNumber)
            throw new InvalidOperationException("Source and target cannot be the same disk");

        if (operation.TargetDisk.IsSystemDisk)
            throw new InvalidOperationException("Target disk cannot be the system disk");

        if (operation.TargetDisk.IsReadOnly)
            throw new InvalidOperationException("Target disk is read-only");

        // Check partitions to clone
        if (operation.PartitionsToClone.Count == 0)
            throw new InvalidOperationException("No partitions selected for cloning");

        // Calculate target layout (sizes for partitions on destination)
        CalculateTargetLayout(operation);

        // Verify boot partitions are included
        var hasEfi = operation.PartitionsToClone.Any(p => p.IsEfiPartition);
        var hasSystem = operation.PartitionsToClone.Any(p => p.IsSystemPartition);

        if (!hasEfi && operation.SourceDisk.IsGpt)
            throw new InvalidOperationException("EFI partition must be included for GPT disks");

        if (!hasSystem)
            throw new InvalidOperationException("System partition must be included");

        // Check for BitLocker
        foreach (var partition in operation.PartitionsToClone.Where(p => p.DriveLetter.HasValue))
        {
            var isEncrypted = await _vssService.IsVolumeBitLockerEncrypted(partition.DriveLetter.Value);
            if (isEncrypted)
            {
                _logger.Warning($"Partition {partition.DriveLetter}: is BitLocker encrypted");
                progress.StatusMessage = $"Warning: Drive {partition.DriveLetter}: has BitLocker enabled";
                ReportProgress(progress);

                // In a real implementation, you would either:
                // 1. Ask user to suspend BitLocker
                // 2. Use a special raw copy method that handles encrypted data
                // For now, we'll proceed with a warning
            }
        }

        // Verify disk access
        var sourceAccessible = await _diskEnumerator.ValidateDiskAccessAsync(operation.SourceDisk.DiskNumber);
        if (!sourceAccessible)
            throw new InvalidOperationException($"Cannot access source disk {operation.SourceDisk.DiskNumber}");

        var targetAccessible = await _diskEnumerator.ValidateDiskAccessAsync(operation.TargetDisk.DiskNumber);
        if (!targetAccessible)
            throw new InvalidOperationException($"Cannot access target disk {operation.TargetDisk.DiskNumber}");

        _logger.Info("Validation passed");
    }

    /// <summary>
    /// Prepares the target disk by clearing it and creating the partition table.
    /// </summary>
    private async Task PrepareTargetDiskAsync(CloneOperation operation, CloneProgress progress)
    {
        _logger.Info($"Preparing target disk {operation.TargetDisk.DiskNumber}");

        // Step 1: Clear the target disk
        try
        {
            await ClearTargetDiskAsync(operation);
        }
        catch (IOException ex) when (ex.Message.Contains("Error 5", StringComparison.OrdinalIgnoreCase) ||
                                      ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            // This pre-clear is optional; the following diskpart script performs "clean" anyway.
            // Some USB bridges deny direct raw writes while still allowing diskpart clean.
            _logger.Warning($"Pre-clear skipped due to access denied: {ex.Message}. Continuing with diskpart clean.");
        }

        // Step 2: Create partition table
        await CreatePartitionTableAsync(operation);

        // Step 3: Refresh disk layout to make partitions visible
        await RefreshDiskLayoutAsync(operation);

        progress.StatusMessage = "Target disk prepared and partitioned";
        _logger.Info("Target disk prepared and partitioned");
    }

    /// <summary>
    /// Calculates how partitions will fit on the target disk.
    /// </summary>
    public void CalculateTargetLayout(CloneOperation operation)
    {
        // Calculate total required space (including 1MB alignment gaps and overhead)
        long totalSpaceRequired = 1024 * 1024; // Start with 1MB for MBR/GPT overhead
        foreach (var p in operation.PartitionsToClone)
        {
            totalSpaceRequired += p.SizeBytes;
            totalSpaceRequired += 1024 * 1024; // Add 1MB alignment buffer per partition
        }

        if (operation.TargetDisk.SizeBytes < totalSpaceRequired)
        {
            if (operation.AllowSmallerTarget)
            {
                // Magic Auto-Shrink Path
                long reservedSpace = 64L * 1024 * 1024; // GPT headers/safety
                foreach (var p in operation.PartitionsToClone)
                {
                    if (!p.IsSystemPartition)
                    {
                        reservedSpace += p.SizeBytes;
                        reservedSpace += 2 * 1024 * 1024; // 2MB Alignment buffer per partition
                    }
                }

                long availableForSystem = operation.TargetDisk.SizeBytes - reservedSpace - (256L * 1024 * 1024); // 256MB safety margin
                if (availableForSystem < (5L * 1024 * 1024 * 1024)) // Min 5GB
                {
                    throw new InvalidOperationException($"Target disk is too small even with shrinking. Only {FormatBytes(availableForSystem)} available for Windows.");
                }

                // Align to 1MB boundary
                availableForSystem = (availableForSystem / (1024 * 1024)) * (1024 * 1024);

                foreach (var p in operation.PartitionsToClone)
                {
                    if (p.IsSystemPartition)
                    {
                        p.TargetSizeBytes = availableForSystem;
                    }
                    else 
                    {
                        p.TargetSizeBytes = (p.SizeBytes / (1024 * 1024)) * (1024 * 1024);
                    }
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Target disk is too small. Required: {FormatBytes(totalSpaceRequired)}. " +
                    "Enable 'Allow smaller target' to auto-shrink.");
            }
        }
        else
        {
            // Normal case: All partitions fit as is
            foreach (var p in operation.PartitionsToClone)
            {
                p.TargetSizeBytes = p.SizeBytes;
            }
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Clears the target disk by writing zeros to the beginning.
    /// </summary>
    private async Task ClearTargetDiskAsync(CloneOperation operation)
    {
        _logger.Info("Clearing target disk...");

        var path = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";

        await Task.Run(() =>
        {
            using var handle = WindowsApi.CreateFile(
                path,
                WindowsApi.GENERIC_WRITE,
                WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"Failed to open target disk: {WindowsApi.GetLastErrorMessage()}");

            // Write zeros to the first 1MB to clear any existing partition table
            var bufferSize = 1024 * 1024;
            var buffer = new byte[bufferSize];

            uint bytesWritten;
            bool result = WindowsApi.WriteFile(
                handle,
                buffer,
                (uint)bufferSize,
                out bytesWritten,
                IntPtr.Zero);

            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to clear target disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            WindowsApi.FlushFileBuffers(handle);
            _logger.Info($"Cleared {bytesWritten} bytes from target disk");
        });
    }

    /// <summary>
    /// Creates the partition table on the target disk.
    /// </summary>
    private async Task CreatePartitionTableAsync(CloneOperation operation)
    {
        _logger.Info("Creating partition table on target...");

        // For simplicity, we'll write the partition entries from the source
        // In a full implementation, this would involve:
        // 1. Reading the GPT/MBR partition table from source
        // 2. Adjusting for target disk size if needed
        // 3. Writing to target

        // For this MVP, we'll use diskpart to create partitions
        await CreatePartitionsViaDiskpartAsync(operation);
    }

    /// <summary>
    /// Creates partitions on the target using diskpart.
    /// </summary>
    private async Task CreatePartitionsViaDiskpartAsync(CloneOperation operation)
    {
        EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "create partition table");
        var scriptPath = Path.GetTempFileName();
        var scriptContent = new StringBuilder();
        var orderedSourcePartitions = operation.PartitionsToClone
            .OrderBy(p => p.StartingOffset)
            .ToList();

        scriptContent.AppendLine($"select disk {operation.TargetDisk.DiskNumber}");
        scriptContent.AppendLine("clean");
        scriptContent.AppendLine("online disk noerr");
        scriptContent.AppendLine("attributes disk clear readonly noerr");

        if (operation.SourceDisk.IsGpt)
        {
            scriptContent.AppendLine("convert gpt noerr");
        }
        else
        {
            scriptContent.AppendLine("convert mbr noerr");
        }

        // Create partitions matching the source
        foreach (var partition in orderedSourcePartitions)
        {
            var sizeMB = partition.TargetSizeBytes / (1024 * 1024);
            
            if (partition.IsEfiPartition)
            {
                scriptContent.AppendLine($"create partition efi size={sizeMB}");
            }
            else if (partition.IsMsrPartition)
            {
                scriptContent.AppendLine($"create partition msr size={sizeMB}");
            }
            else if (partition.IsRecoveryPartition)
            {
                scriptContent.AppendLine($"create partition primary size={sizeMB}");
                scriptContent.AppendLine("set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac override");
                scriptContent.AppendLine("gpt attributes=0x8000000000000001"); // Required + No Drive Letter
            }
            else
            {
                scriptContent.AppendLine($"create partition primary size={sizeMB}");
            }
        }

        // List partitions to verify
        scriptContent.AppendLine("list partition");

        // Take disk offline so Windows releases all volume mounts
        // This is CRITICAL: without this, WriteFile to the PhysicalDrive will fail
        // because Windows blocks writes to disks with mounted volumes.
        scriptContent.AppendLine($"select disk {operation.TargetDisk.DiskNumber}");
        scriptContent.AppendLine("offline disk");

        AssertDiskpartScriptTargetsOnlyTargetDisk(operation, scriptContent.ToString(), "create partition table");
        await File.WriteAllTextAsync(scriptPath, scriptContent.ToString());

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(_cancellationTokenSource.Token);

                _logger.Info($"DiskPart output: {output}");

                if (process.ExitCode != 0)
                {
                    _logger.Error($"diskpart failed with code {process.ExitCode}. Error: {error}");
                    _logger.Error($"DiskPart script was:\n{scriptContent}");
                    throw new IOException($"Failed to create partitions: DiskPart error {process.ExitCode}. See logs for details.");
                }
                else
                {
                    ApplyTargetPartitionOffsets(operation, output);
                    _logger.Info("Partitions created successfully");
                }
            }
            else
            {
                throw new IOException("Failed to start diskpart.exe");
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private void ApplyTargetPartitionOffsets(CloneOperation operation, string diskPartOutput)
    {
        var sourcePartitions = operation.PartitionsToClone
            .OrderBy(p => p.StartingOffset)
            .ToList();
        var targetPartitions = ParsePartitionTableFromDiskPartOutput(diskPartOutput);

        if (targetPartitions.Count < sourcePartitions.Count)
        {
            throw new InvalidOperationException(
                $"Partition mapping failed: expected at least {sourcePartitions.Count} target partitions, " +
                $"but diskpart listed {targetPartitions.Count}. Aborting to avoid incorrect writes.");
        }

        if (targetPartitions.Count > sourcePartitions.Count)
        {
            _logger.Warning(
                $"diskpart listed {targetPartitions.Count} partitions but only {sourcePartitions.Count} are scheduled for cloning. " +
                "Matching source partitions by expected type and order.");
        }

        int targetSearchStart = 0;
        long lastAssignedOffset = -1;
        for (int i = 0; i < sourcePartitions.Count; i++)
        {
            var sourcePartition = sourcePartitions[i];
            var expectedType = GetExpectedDiskPartType(sourcePartition);
            var mappedTargetIndex = -1;

            for (int targetIndex = targetSearchStart; targetIndex < targetPartitions.Count; targetIndex++)
            {
                if (targetPartitions[targetIndex].TypeName == expectedType)
                {
                    mappedTargetIndex = targetIndex;
                    break;
                }
            }

            if (mappedTargetIndex == -1)
            {
                mappedTargetIndex = targetSearchStart;
                _logger.Warning(
                    $"Could not find target partition type '{expectedType}' for source partition [{sourcePartition.PartitionNumber}]. " +
                    $"Falling back to next target partition {targetPartitions[mappedTargetIndex].PartitionNumber} ({targetPartitions[mappedTargetIndex].TypeName}).");
            }

            var targetPartition = targetPartitions[mappedTargetIndex];
            // Sanity checks before assigning offsets to avoid accidental overwrites
            if (targetPartition.StartingOffsetBytes <= 0)
            {
                throw new InvalidOperationException($"Parsed target partition {targetPartition.PartitionNumber} has invalid starting offset {targetPartition.StartingOffsetBytes}.");
            }
            if (lastAssignedOffset >= 0 && targetPartition.StartingOffsetBytes <= lastAssignedOffset)
            {
                throw new InvalidOperationException($"Parsed target partition offsets are not strictly increasing (partition {targetPartition.PartitionNumber} offset {targetPartition.StartingOffsetBytes}). Aborting.");
            }
            if (!operation.AllowSmallerTarget && targetPartition.SizeBytes < sourcePartition.SizeBytes)
            {
                throw new InvalidOperationException($"Target partition {targetPartition.PartitionNumber} is smaller ({targetPartition.SizeBytes} bytes) than source partition {sourcePartition.PartitionNumber} ({sourcePartition.SizeBytes} bytes). Aborting to avoid data truncation.");
            }

            sourcePartition.TargetStartingOffset = targetPartition.StartingOffsetBytes;
            sourcePartition.TargetPartitionNumber = targetPartition.PartitionNumber;
            lastAssignedOffset = targetPartition.StartingOffsetBytes;
            targetSearchStart = mappedTargetIndex + 1;

            _logger.Info(
                $"Partition mapping: source [{sourcePartition.PartitionNumber}] offset {sourcePartition.StartingOffset} -> " +
                $"target partition {targetPartition.PartitionNumber} ({targetPartition.TypeName}) offset {targetPartition.StartingOffsetBytes}");
        }
    }

    private static List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)> ParsePartitionTableFromDiskPartOutput(string output)
    {
        var result = new List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var match = DiskPartPartitionLineRegex.Match(line);
            if (!match.Success)
            {
                // Try a very permissive whitespace-split fallback: Partition <num> <type> <size> <unit> <offset> <unit>
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 6 && tokens[0].Equals("Partition", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                    {
                        // Reconstruct type as the token(s) between number and last 4 tokens
                        var tail = tokens.Skip(Math.Max(0, tokens.Length - 4)).ToArray();
                        var typeTokens = tokens.Skip(2).Take(tokens.Length - 6).ToArray();
                        var typeText = typeTokens.Length > 0 ? string.Join(' ', typeTokens) : tokens[2];
                        var sizeText = tail[0];
                        var sizeUnit = tail[1];
                        var offsetText = tail[2];
                        var offsetUnit = tail[3];

                        if (TryParseSizeToBytes(sizeText, sizeUnit, out var sBytes) && TryParseSizeToBytes(offsetText, offsetUnit, out var oBytes))
                        {
                            result.Add((num, NormalizeDiskPartType(typeText), sBytes, oBytes));
                            continue;
                        }
                    }
                }

                // Could not parse line - skip it
                continue;
            }

            try
            {
                var partitionNumber = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
                var typeName = NormalizeDiskPartType(match.Groups["type"].Value);
                var sizeValue = match.Groups["sizeValue"].Value;
                var sizeUnit = match.Groups["sizeUnit"].Value;
                var offsetValue = match.Groups["offsetValue"].Value;
                var offsetUnit = match.Groups["offsetUnit"].Value;

                if (!TryParseSizeToBytes(sizeValue, sizeUnit, out var sizeBytes))
                    continue;
                if (!TryParseSizeToBytes(offsetValue, offsetUnit, out var offsetBytes))
                    continue;

                result.Add((partitionNumber, typeName, sizeBytes, offsetBytes));
            }
            catch
            {
                // Defensive: skip malformed lines rather than throw
                continue;
            }
        }

        return result.OrderBy(p => p.StartingOffsetBytes).ToList();
    }

    private static string GetExpectedDiskPartType(PartitionInfo partition)
    {
        if (partition.IsMsrPartition)
            return "Reserved";
        if (partition.IsEfiPartition)
            return "System";
        if (partition.IsRecoveryPartition)
            return "Recovery";

        return "Primary";
    }

    private static string NormalizeDiskPartType(string typeText)
    {
        var normalized = typeText.Trim().ToLowerInvariant();
        if (normalized.Contains("reserved"))
            return "Reserved";
        if (normalized.Contains("system"))
            return "System";
        if (normalized.Contains("recovery"))
            return "Recovery";
        if (normalized.Contains("primary"))
            return "Primary";

        return typeText.Trim();
    }

    private static long ParseSizeToBytes(string valueText, string unitText)
    {
        if (TryParseSizeToBytes(valueText, unitText, out var bytes))
            return bytes;

        throw new FormatException($"Could not parse size '{valueText} {unitText}' from diskpart output.");
    }

    private static bool TryParseSizeToBytes(string valueText, string unitText, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(valueText))
            return false;

        var normalizedValue = valueText.Replace(',', '.');
        if (!double.TryParse(normalizedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        var unit = (unitText ?? string.Empty).Trim();
        double multiplier = unit.ToUpperInvariant() switch
        {
            "B" => 1d,
            "BYTES" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "TB" => 1024d * 1024d * 1024d * 1024d,
            "" => 1d,
            _ => 0d
        };

        if (multiplier <= 0d)
            return false;

        try
        {
            bytes = checked((long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private long GetRequiredTargetStartingOffset(PartitionInfo partition)
    {
        if (partition.TargetStartingOffset <= 0)
        {
            throw new InvalidOperationException(
                $"Target partition offset is not initialized for source partition {partition.PartitionNumber}. " +
                "Aborting to avoid writing data to the wrong location.");
        }

        return partition.TargetStartingOffset;
    }

    private async Task<long> FallbackToRawCopyOrThrowAsync(
        CloneOperation operation,
        PartitionInfo partition,
        CloneProgress progress,
        long totalBytesAlreadyCopied,
        string reason)
    {
        var shrinkingThisPartition = operation.AllowSmallerTarget && partition.TargetSizeBytes < partition.SizeBytes;
        if (shrinkingThisPartition)
        {
            throw new InvalidOperationException(
                $"{reason}. Smart copy is required when shrinking partitions, but NTFS bitmap could not be read. " +
                "Retry with Smart Copy enabled and the source volume healthy, or clone to an equal/larger target.");
        }

        _logger.Warning($"{reason}, falling back to raw copy");
        return await CopyPartitionAsync(operation, partition, progress, totalBytesAlreadyCopied);
    }

    /// <summary>
    /// Copies a single partition from source to target using native aligned buffers.
    /// Physical disk handles on Windows implicitly use unbuffered I/O, which requires
    /// sector-aligned buffer memory, offsets, and byte counts.
    /// </summary>
    private async Task<long> CopyPartitionAsync(CloneOperation operation, PartitionInfo partition, CloneProgress progress, long totalBytesAlreadyCopied)
    {
        _logger.Info($"Copying partition {partition.PartitionNumber} ({partition.SizeDisplay}) to target ({FormatBytes(partition.TargetSizeBytes)})");

        var sourcePath = $@"\\.\PhysicalDrive{operation.SourceDisk.DiskNumber}";
        var targetPath = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";
        var targetPartitionOffset = GetRequiredTargetStartingOffset(partition);

        var partitionBytesCopied = 0L;
        var totalBytesInPartition = partition.TargetSizeBytes;
        const int sectorSize = 512;

        // Align buffer size to sector boundary
        var bufferSize = operation.IoBufferSize;
        bufferSize = ((bufferSize + sectorSize - 1) / sectorSize) * sectorSize;

        await Task.Run(() =>
        {
            using var sourceHandle = WindowsApi.CreateFile(
                sourcePath,
                WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (sourceHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open source disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            // Open target with READ+WRITE (both needed for raw physical disk I/O)
            // The disk should be OFFLINE at this point (set by diskpart), so no mounted volumes block us.
            using var targetHandle = WindowsApi.CreateFile(
                targetPath,
                WindowsApi.GENERIC_READ_WRITE,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (targetHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open target disk for writing: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            _logger.Info($"Target disk handle opened successfully for partition {partition.PartitionNumber}");

            // Allocate native memory for sector-aligned I/O buffer using NativeBuffer wrapper.
            // Physical disk handles require sector-aligned buffers, offsets, and byte counts.
            using var nativeBuffer = new NativeBuffer(bufferSize);

            try
            {
                var relativeOffset = 0L;
                var lastProgressUpdate = DateTime.UtcNow;
                var bytesSinceLastUpdate = 0L;

                while (partitionBytesCopied < totalBytesInPartition)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        _logger.Warning("Copy operation cancelled");
                        break;
                    }

                    // Calculate bytes to read, rounded UP to sector boundary
                    var bytesRemaining = totalBytesInPartition - partitionBytesCopied;
                    var bytesToRead = (int)Math.Min(bufferSize, bytesRemaining);
                    // Round up to sector size for physical disk I/O
                    bytesToRead = ((bytesToRead + sectorSize - 1) / sectorSize) * sectorSize;

                    long absoluteSourceOffset = partition.StartingOffset + relativeOffset;
                    long absoluteTargetOffset = targetPartitionOffset + relativeOffset;

                    // Seek to source position
                    if (!WindowsApi.SetFilePointerEx(sourceHandle, absoluteSourceOffset, out _, WindowsApi.FILE_BEGIN))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new IOException($"Failed to seek source at offset {absoluteSourceOffset}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                    }

                    // Read from source using native buffer
                    uint bytesRead;
                    if (!WindowsApi.ReadFile(sourceHandle, nativeBuffer.Pointer, (uint)bytesToRead, out bytesRead, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new IOException($"Failed to read from source at offset {absoluteSourceOffset}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                    }

                    if (bytesRead == 0)
                    {
                        _logger.Warning($"Read 0 bytes at offset {absoluteSourceOffset}, stopping partition copy");
                        break;
                    }

                    // Ensure write size is sector-aligned
                    uint bytesToWrite = ((bytesRead + (uint)sectorSize - 1) / (uint)sectorSize) * (uint)sectorSize;

                    // If the read returned a non-sector-aligned amount, zero-pad the remainder
                    // in the native buffer so we don't write stale memory to the target disk.
                    if (bytesToWrite > bytesRead)
                    {
                        var pad = (int)(bytesToWrite - bytesRead);
                        try
                        {
                            nativeBuffer.Zero((int)bytesRead, pad);
                        }
                        catch (OverflowException)
                        {
                            throw new IOException($"Buffer padding overflow while preparing write of {bytesToWrite} bytes (read {bytesRead}).");
                        }
                    }

                    // Seek to target position
                    if (!WindowsApi.SetFilePointerEx(targetHandle, absoluteTargetOffset, out _, WindowsApi.FILE_BEGIN))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new IOException($"Failed to seek target at offset {absoluteTargetOffset}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                    }

                    // Write to target using native buffer
                    uint bytesWritten;
                    if (!WindowsApi.WriteFile(targetHandle, nativeBuffer.Pointer, bytesToWrite, out bytesWritten, IntPtr.Zero))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new IOException($"Failed to write to target at offset {absoluteTargetOffset}, size {bytesToWrite}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                    }

                    partitionBytesCopied += bytesRead;
                    relativeOffset += bytesRead;
                    bytesSinceLastUpdate += bytesRead;

                    // Update progress periodically
                    if (DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromMilliseconds(250))
                    {
                        var now = DateTime.UtcNow;
                        var elapsedSec = (now - lastProgressUpdate).TotalSeconds;
                        var currentTotalCopied = totalBytesAlreadyCopied + partitionBytesCopied;
                        progress.BytesCopied = currentTotalCopied;
                        progress.ThroughputBytesPerSec = elapsedSec > 0
                            ? bytesSinceLastUpdate / elapsedSec
                            : 0;
                        progress.PercentComplete = (currentTotalCopied * 100.0) / progress.TotalBytes;

                        var remainingBytes = progress.TotalBytes - progress.BytesCopied;
                        if (progress.ThroughputBytesPerSec > 0)
                        {
                            progress.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / progress.ThroughputBytesPerSec);
                        }

                        ReportProgress(progress);
                        lastProgressUpdate = now;
                        bytesSinceLastUpdate = 0;
                    }
                }

                // Flush target buffers
                WindowsApi.FlushFileBuffers(targetHandle);
            }
            finally
            {
                // NativeBuffer is disposed via 'using' pattern.
            }

            _logger.Info($"Copied {partitionBytesCopied:N0} bytes for partition {partition.PartitionNumber}");
        });

        return totalBytesAlreadyCopied + partitionBytesCopied;
    }


    /// <summary>
    /// Copies a partition using the NTFS allocation bitmap to skip free clusters.
    /// This is faster than raw copy and makes auto-shrink reliable by ensuring
    /// only actually-used sectors are copied and written to the target.
    /// </summary>
    private async Task<long> CopyPartitionSmartAsync(CloneOperation operation, PartitionInfo partition, CloneProgress progress, long totalBytesAlreadyCopied)
    {
        _logger.Info($"Smart-copying partition {partition.PartitionNumber} ({partition.SizeDisplay}) using NTFS bitmap");

        var sourcePath = $@"\\.\PhysicalDrive{operation.SourceDisk.DiskNumber}";
        var targetPath = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";
        var sourcePartitionOffset = partition.StartingOffset;
        var targetPartitionOffset = GetRequiredTargetStartingOffset(partition);
        
        // FSCTL_GET_VOLUME_BITMAP must be routed to the live volume handle, not the VSS snapshot.
        // VSS snapshots return ERROR_INVALID_FUNCTION (1) for this IOCTL.
        var bitmapVolumePath = $@"\\.\{partition.DriveLetter!.Value}:";
        
        // For the actual data reads, we still use the physical disk (or we could use the VSS snapshot if we mapped cluster offsets to the snapshot, but physical disk + VSS is complex. We will read from the physical disk, which might have slight tearing, but for smart copy that's usually acceptable if VSS flushes first).
        
        var partitionBytesCopied = 0L;
        const int sectorSize = 512;

        var bufferSize = operation.IoBufferSize;
        bufferSize = ((bufferSize + sectorSize - 1) / sectorSize) * sectorSize;

        return await Task.Run(async () =>
        {
            // --- Step 1: Read the NTFS bitmap from the volume ---
            _logger.Info($"Reading NTFS bitmap from live volume {bitmapVolumePath}");

            // Open the live volume (not the VSS snapshot) for reading the bitmap
            using var volumeHandle = WindowsApi.CreateFile(
                bitmapVolumePath,
                WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (volumeHandle.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                return await FallbackToRawCopyOrThrowAsync(
                    operation,
                    partition,
                    progress,
                    totalBytesAlreadyCopied,
                    $"Cannot open volume {bitmapVolumePath} for bitmap read ({err})");
            }

            // Read NTFS boot sector to get bytes per cluster
            var bootSector = new byte[512];
            uint bootRead;
            if (!WindowsApi.ReadFile(volumeHandle, bootSector, 512, out bootRead, IntPtr.Zero) || bootRead < 512)
            {
                return await FallbackToRawCopyOrThrowAsync(
                    operation,
                    partition,
                    progress,
                    totalBytesAlreadyCopied,
                    "Failed to read NTFS boot sector");
            }

            // NTFS boot sector offsets: bytes per sector at 0x0B, sectors per cluster at 0x0D
            int bytesPerSector = BitConverter.ToUInt16(bootSector, 0x0B);
            int sectorsPerCluster = bootSector[0x0D];
            long bytesPerCluster = (long)bytesPerSector * sectorsPerCluster;

            if (bytesPerCluster <= 0 || bytesPerCluster > 64 * 1024 * 1024)
            {
                return await FallbackToRawCopyOrThrowAsync(
                    operation,
                    partition,
                    progress,
                    totalBytesAlreadyCopied,
                    $"Unexpected NTFS cluster size {bytesPerCluster}");
            }

            _logger.Info($"NTFS cluster size: {bytesPerCluster} bytes ({bytesPerCluster / 1024} KB)");

            // Read the full volume bitmap via FSCTL_GET_VOLUME_BITMAP
            // The API returns it in chunks; we request from LCN 0 and keep going until done
            long totalClusters = 0;
            byte[]? bitmap = null;
            long startingLcn = 0;

            while (true)
            {
                // Input: STARTING_LCN_INPUT_BUFFER (8 bytes = starting LCN as int64)
                var inputBuffer = BitConverter.GetBytes(startingLcn);
                // Output: VOLUME_BITMAP_BUFFER: StartingLcn (8) + BitmapSize (8) + bitmap bytes
                var outputBuffer = new byte[65536 + 16]; // 16 bytes header + 64KB bitmap data

                uint bytesReturned;
                bool ok = WindowsApi.DeviceIoControl(
                    volumeHandle,
                    WindowsApi.FSCTL_GET_VOLUME_BITMAP,
                    inputBuffer, inputBuffer.Length,
                    outputBuffer, outputBuffer.Length,
                    out bytesReturned,
                    IntPtr.Zero);

                int lastErr = Marshal.GetLastWin32Error();
                if (!ok && lastErr == 87)
                {
                    // Some storage/filter drivers reject managed byte[] marshalling for METHOD_NEITHER FSCTLs.
                    // Retry using unmanaged buffers.
                    ok = TryGetVolumeBitmapUnmanaged(
                        volumeHandle,
                        startingLcn,
                        outputBuffer,
                        out bytesReturned,
                        out lastErr);
                }

                if (!ok && lastErr != 234) // 234 = ERROR_MORE_DATA
                {
                    return await FallbackToRawCopyOrThrowAsync(
                        operation,
                        partition,
                        progress,
                        totalBytesAlreadyCopied,
                        $"FSCTL_GET_VOLUME_BITMAP failed ({lastErr}) on {bitmapVolumePath} (startLCN={startingLcn}, outBuf={outputBuffer.Length})");
                }

                if (bytesReturned < 16)
                {
                    return await FallbackToRawCopyOrThrowAsync(
                        operation,
                        partition,
                        progress,
                        totalBytesAlreadyCopied,
                        $"FSCTL_GET_VOLUME_BITMAP returned too little data ({bytesReturned} bytes)");
                }

                // Parse header: StartingLcn (8 bytes) + BitmapSize in clusters (8 bytes).
                // IMPORTANT: BitmapSize is the total volume cluster count, not this chunk size.
                long chunkStartLcn = BitConverter.ToInt64(outputBuffer, 0);
                totalClusters = BitConverter.ToInt64(outputBuffer, 8);
                if (totalClusters <= 0)
                {
                    return await FallbackToRawCopyOrThrowAsync(
                        operation,
                        partition,
                        progress,
                        totalBytesAlreadyCopied,
                        $"FSCTL_GET_VOLUME_BITMAP returned invalid cluster count ({totalClusters})");
                }

                if (bitmap == null)
                {
                    var totalBitmapBytesLong = (totalClusters + 7) / 8;
                    if (totalBitmapBytesLong <= 0 || totalBitmapBytesLong > int.MaxValue)
                    {
                        return await FallbackToRawCopyOrThrowAsync(
                            operation,
                            partition,
                            progress,
                            totalBytesAlreadyCopied,
                            $"Volume bitmap size is unsupported ({totalBitmapBytesLong} bytes)");
                    }

                    bitmap = new byte[(int)totalBitmapBytesLong];
                }

                int payloadBytes = (int)bytesReturned - 16;
                if (payloadBytes > 0)
                {
                    long destByteOffsetLong = chunkStartLcn / 8;
                    if (destByteOffsetLong < 0 || destByteOffsetLong >= bitmap.Length)
                    {
                        return await FallbackToRawCopyOrThrowAsync(
                            operation,
                            partition,
                            progress,
                            totalBytesAlreadyCopied,
                            $"Bitmap chunk offset out of range (chunkStartLCN={chunkStartLcn}, destByte={destByteOffsetLong})");
                    }

                    int destByteOffset = (int)destByteOffsetLong;
                    int toCopy = Math.Min(payloadBytes, bitmap.Length - destByteOffset);
                    Array.Copy(outputBuffer, 16, bitmap, destByteOffset, toCopy);

                    long chunkClustersReturned = (long)payloadBytes * 8;
                    startingLcn = chunkStartLcn + chunkClustersReturned;
                }

                if (ok)
                {
                    break; // Full bitmap retrieved
                }

                // ERROR_MORE_DATA: continue from next LCN covered by this chunk.
                if (startingLcn >= totalClusters)
                {
                    break;
                }
            }

            if (bitmap == null)
            {
                return await FallbackToRawCopyOrThrowAsync(
                    operation,
                    partition,
                    progress,
                    totalBytesAlreadyCopied,
                    "FSCTL_GET_VOLUME_BITMAP returned no bitmap data");
            }

            // Find the last allocated cluster to determine minimum required space
            long lastUsedCluster = 0;
            for (long lcn = totalClusters - 1; lcn >= 0; lcn--)
            {
                int byteIdx = (int)(lcn / 8);
                int bitIdx = (int)(lcn % 8);
                if (byteIdx < bitmap.Length && (bitmap[byteIdx] & (1 << bitIdx)) != 0)
                {
                    lastUsedCluster = lcn;
                    break;
                }
            }

            long lastUsedByteOffset = (lastUsedCluster + 1) * bytesPerCluster;
            _logger.Info($"Last used cluster: {lastUsedCluster}, last used byte offset in partition: {lastUsedByteOffset:N0} ({lastUsedByteOffset / (1024 * 1024 * 1024.0):F1} GB)");

            // --- Pre-flight check for auto-shrink ---
            if (lastUsedByteOffset > partition.TargetSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Cannot fit data onto target partition: used data extends to {lastUsedByteOffset / (1024 * 1024 * 1024.0):F1} GB " +
                    $"but target partition is only {partition.TargetSizeBytes / (1024 * 1024 * 1024.0):F1} GB. " +
                    $"Free up space on the source partition and try again.");
            }

            _logger.Info($"Smart copy: will copy ~{lastUsedByteOffset / (1024 * 1024.0):F0} MB of used data out of {partition.SizeBytes / (1024 * 1024.0):F0} MB partition");

            // Process only up to the last allocated cluster. This keeps smart-copy
            // bounded to the same byte budget used for progress/ETA calculations.
            long maxClustersByUsage = lastUsedCluster + 1;
            long maxClustersByTarget = (partition.TargetSizeBytes + bytesPerCluster - 1) / bytesPerCluster;
            long maxClustersToProcess = Math.Min(totalClusters, Math.Min(maxClustersByUsage, maxClustersByTarget));

            // Count allocated clusters within the processing boundary so progress reflects
            // actual bytes read/written (not scanned/skipped free space).
            long allocatedClusterCount = 0;
            for (long lcn = 0; lcn < maxClustersToProcess; lcn++)
            {
                int byteIdx = (int)(lcn / 8);
                int bitIdx = (int)(lcn % 8);
                if (byteIdx < bitmap.Length && (bitmap[byteIdx] & (1 << bitIdx)) != 0)
                {
                    allocatedClusterCount++;
                }
            }

            long allocatedBytesToCopy = allocatedClusterCount * bytesPerCluster;
            progress.TotalBytes = totalBytesAlreadyCopied + allocatedBytesToCopy;

            // --- Step 2: Open physical disk handles and copy cluster by cluster ---
            using var sourceHandle = WindowsApi.CreateFile(
                sourcePath,
                WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (sourceHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open source disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            using var targetHandle = WindowsApi.CreateFile(
                targetPath,
                WindowsApi.GENERIC_READ_WRITE,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (targetHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open target disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            // Allocate native aligned buffer (cluster-aligned, at least one cluster)
            int clusterRoundedBuffer = (int)Math.Max(bufferSize, bytesPerCluster);
            clusterRoundedBuffer = (int)(((long)clusterRoundedBuffer + bytesPerCluster - 1) / bytesPerCluster * bytesPerCluster);
            // Also align to sector size
            clusterRoundedBuffer = ((clusterRoundedBuffer + sectorSize - 1) / sectorSize) * sectorSize;

            using var nativeBuffer = new NativeBuffer(clusterRoundedBuffer);
            try
            {
                var lastProgressUpdate = DateTime.UtcNow;
                var bytesSinceLastUpdate = 0L;
                long lcn = 0;

                while (lcn < maxClustersToProcess)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        _logger.Warning("Smart copy cancelled");
                        break;
                    }

                    // Find next run of same state (allocated or free)
                    int byteIdx = (int)(lcn / 8);
                    int bitIdx = (int)(lcn % 8);
                    bool isAllocated = byteIdx < bitmap.Length && (bitmap[byteIdx] & (1 << bitIdx)) != 0;

                    // Count consecutive clusters with same state
                    long runStart = lcn;
                    while (lcn < maxClustersToProcess)
                    {
                        int bi = (int)(lcn / 8);
                        int bj = (int)(lcn % 8);
                        bool allocated = bi < bitmap.Length && (bitmap[bi] & (1 << bj)) != 0;
                        if (allocated != isAllocated) break;
                        lcn++;

                        // Cap run at buffer size
                        long runBytes = (lcn - runStart) * bytesPerCluster;
                        if (runBytes >= clusterRoundedBuffer) break;
                    }

                    long runSourceByteOffset = sourcePartitionOffset + runStart * bytesPerCluster;
                    long runTargetByteOffset = targetPartitionOffset + runStart * bytesPerCluster;
                    long runByteLength = (lcn - runStart) * bytesPerCluster;

                    // Stop at target boundary
                    if (runTargetByteOffset >= targetPartitionOffset + partition.TargetSizeBytes)
                        break;
                    if (runTargetByteOffset + runByteLength > targetPartitionOffset + partition.TargetSizeBytes)
                        runByteLength = (targetPartitionOffset + partition.TargetSizeBytes) - runTargetByteOffset;

                    // Align to sector
                    uint toProcess = (uint)(((runByteLength + sectorSize - 1) / sectorSize) * sectorSize);

                    // Seek target
                    if (!WindowsApi.SetFilePointerEx(targetHandle, runTargetByteOffset, out _, WindowsApi.FILE_BEGIN))
                    {
                        var error = Marshal.GetLastWin32Error();
                        throw new IOException($"Failed to seek target at {runTargetByteOffset}: {WindowsApi.GetErrorMessage((uint)error)}");
                    }

                    if (isAllocated)
                    {
                        // Seek source and read
                        if (!WindowsApi.SetFilePointerEx(sourceHandle, runSourceByteOffset, out _, WindowsApi.FILE_BEGIN))
                        {
                            var error = Marshal.GetLastWin32Error();
                            throw new IOException($"Failed to seek source at {runSourceByteOffset}: {WindowsApi.GetErrorMessage((uint)error)}");
                        }

                        uint bytesRead;
                        if (!WindowsApi.ReadFile(sourceHandle, nativeBuffer.Pointer, toProcess, out bytesRead, IntPtr.Zero))
                        {
                            var error = Marshal.GetLastWin32Error();
                            throw new IOException($"Failed to read source at {runSourceByteOffset}: {WindowsApi.GetErrorMessage((uint)error)}");
                        }

                        uint bytesWritten;
                        if (!WindowsApi.WriteFile(targetHandle, nativeBuffer.Pointer, bytesRead, out bytesWritten, IntPtr.Zero))
                        {
                            var error = Marshal.GetLastWin32Error();
                            throw new IOException($"Failed to write target at {runTargetByteOffset}: {WindowsApi.GetErrorMessage((uint)error)}");
                        }

                        partitionBytesCopied += bytesRead;
                        bytesSinceLastUpdate += bytesRead;
                    }
                    else
                    {
                        // Skip free-cluster runs entirely in smart-copy mode.
                        // We keep advancing through the bitmap, but we don't write them.
                    }

                    // Progress update
                    if (DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromMilliseconds(250))
                    {
                        var now = DateTime.UtcNow;
                        var elapsedSec = (now - lastProgressUpdate).TotalSeconds;
                        var currentTotalCopied = totalBytesAlreadyCopied + partitionBytesCopied;
                        progress.BytesCopied = currentTotalCopied;
                        progress.ThroughputBytesPerSec = elapsedSec > 0
                            ? bytesSinceLastUpdate / elapsedSec
                            : 0;
                        progress.PercentComplete = Math.Min(100.0, (currentTotalCopied * 100.0) / progress.TotalBytes);
                        var remainingBytes = progress.TotalBytes - progress.BytesCopied;
                        if (progress.ThroughputBytesPerSec > 0)
                            progress.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / progress.ThroughputBytesPerSec);
                        ReportProgress(progress);
                        lastProgressUpdate = now;
                        bytesSinceLastUpdate = 0;
                    }
                }

                WindowsApi.FlushFileBuffers(targetHandle);
                _logger.Info($"Smart copy complete: {partitionBytesCopied:N0} bytes processed for partition {partition.PartitionNumber}");
            }
            finally
            {
                // NativeBuffer freed by using/dispose
            }

            return totalBytesAlreadyCopied + partitionBytesCopied;
        });
    }

    private static bool TryGetVolumeBitmapUnmanaged(
        Microsoft.Win32.SafeHandles.SafeFileHandle volumeHandle,
        long startingLcn,
        byte[] outputBuffer,
        out uint bytesReturned,
        out int lastError)
    {
        bytesReturned = 0;

        using (var inBuf = new NativeBuffer(sizeof(long)))
        using (var outBuf = new NativeBuffer(outputBuffer.Length))
        {
            Marshal.WriteInt64(inBuf.Pointer, startingLcn);

            var ok = WindowsApi.DeviceIoControl(
                volumeHandle,
                WindowsApi.FSCTL_GET_VOLUME_BITMAP,
                inBuf.Pointer,
                sizeof(long),
                outBuf.Pointer,
                outputBuffer.Length,
                out bytesReturned,
                IntPtr.Zero);

            lastError = Marshal.GetLastWin32Error();

            if (ok || lastError == 234)
            {
                int toCopy = (int)Math.Min(bytesReturned, (uint)outputBuffer.Length);
                if (toCopy > 0)
                {
                    Marshal.Copy(outBuf.Pointer, outputBuffer, 0, toCopy);
                }
            }

            return ok;
        }
    }


    private CopyStrategy GetCopyStrategy(CloneOperation operation, PartitionInfo partition)
    {
        bool isNtfs = partition.FileSystemType.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        bool isShrunk = partition.TargetSizeBytes > 0 && partition.TargetSizeBytes < partition.SizeBytes;
        bool isSystemNtfsShrink = operation.AllowSmallerTarget && partition.IsSystemPartition && isNtfs && isShrunk;

        if (isSystemNtfsShrink)
        {
            return CopyStrategy.FileSystemMigration;
        }

        bool useSmartCopy = operation.SmartCopy && isNtfs && partition.DriveLetter.HasValue;
        return useSmartCopy ? CopyStrategy.SmartBlock : CopyStrategy.RawBlock;
    }

    private static char? GetSourceSystemDriveLetter(CloneOperation operation)
    {
        return operation.PartitionsToClone.FirstOrDefault(p => p.IsSystemPartition)?.DriveLetter;
    }

    private static char NormalizeDriveLetter(char driveLetter)
    {
        return char.ToUpperInvariant(driveLetter);
    }

    private static string EnsureTrailingBackslash(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;
        return path.EndsWith(@"\", StringComparison.Ordinal) ? path : path + @"\";
    }

    private void EnsureTargetDiskMutationAllowed(CloneOperation operation, int diskNumber, string operationName)
    {
        if (diskNumber != operation.TargetDisk.DiskNumber)
        {
            throw new InvalidOperationException(
                $"Unsafe disk mutation blocked for '{operationName}': disk {diskNumber} is not target disk {operation.TargetDisk.DiskNumber}.");
        }

        if (diskNumber == operation.SourceDisk.DiskNumber)
        {
            throw new InvalidOperationException(
                $"Unsafe disk mutation blocked for '{operationName}': attempted to mutate source disk {operation.SourceDisk.DiskNumber}.");
        }
    }

    private void EnsureTargetVolumeMutationAllowed(CloneOperation operation, char driveLetter, string operationName)
    {
        var normalized = NormalizeDriveLetter(driveLetter);
        var sourceDrive = GetSourceSystemDriveLetter(operation);
        if (sourceDrive.HasValue && normalized == NormalizeDriveLetter(sourceDrive.Value))
        {
            throw new InvalidOperationException(
                $"Unsafe volume mutation blocked for '{operationName}': drive {normalized}: is the source system volume.");
        }
    }

    private void AssertDiskpartScriptTargetsOnlyTargetDisk(CloneOperation operation, string scriptContent, string operationName)
    {
        if (string.IsNullOrWhiteSpace(scriptContent))
            throw new InvalidOperationException($"DiskPart script is empty for '{operationName}'.");

        var diskMatches = Regex.Matches(scriptContent, @"(?im)^\s*select\s+disk\s+(\d+)\s*$");
        if (diskMatches.Count == 0)
            throw new InvalidOperationException($"DiskPart script for '{operationName}' does not select a disk.");

        foreach (Match m in diskMatches)
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedDisk))
                continue;
            EnsureTargetDiskMutationAllowed(operation, selectedDisk, operationName);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(System.Diagnostics.ProcessStartInfo startInfo)
    {
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new IOException($"Failed to start process: {startInfo.FileName}");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static char GetAvailableDriveLetter(params char[] preferredLetters)
    {
        var inUse = DriveInfo.GetDrives()
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => NormalizeDriveLetter(d.Name[0]))
            .ToHashSet();

        foreach (var letter in preferredLetters.Select(NormalizeDriveLetter))
        {
            if (letter >= 'D' && letter <= 'Z' && !inUse.Contains(letter))
                return letter;
        }

        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!inUse.Contains(letter))
                return letter;
        }

        throw new InvalidOperationException("No free drive letter available for target partition mounting.");
    }

    private async Task<string> MigratePartitionFileSystemAsync(CloneOperation operation, PartitionInfo partition, CloneResult result)
    {
        if (!partition.DriveLetter.HasValue)
            throw new InvalidOperationException($"Cannot migrate partition {partition.PartitionNumber}: source drive letter is missing.");
        if (partition.TargetPartitionNumber <= 0)
            throw new InvalidOperationException($"Cannot migrate partition {partition.PartitionNumber}: target partition number is missing.");

        EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "filesystem migration");

        var sourceRoot = GetMigrationSourceRoot(operation, partition, result);
        var targetLetter = GetAvailableDriveLetter('W', 'V', 'T', 'R', 'Q');
        EnsureTargetVolumeMutationAllowed(operation, targetLetter, "filesystem migration target mount");

        await FormatAndMountTargetPartitionAsync(operation, partition.TargetPartitionNumber, targetLetter, "Windows");

        char? efiLetter = null;
        try
        {
            await CopyWithRoboCopyAsync(sourceRoot, $"{targetLetter}:\\");
            await ValidateTargetVolumeAsync(targetLetter, "NTFS", operation, partition);

            var efi = operation.PartitionsToClone.FirstOrDefault(p => p.IsEfiPartition);
            if (efi != null && efi.TargetPartitionNumber > 0)
            {
                efiLetter = GetAvailableDriveLetter('S', 'P', 'O', 'N');
                EnsureTargetVolumeMutationAllowed(operation, efiLetter.Value, "EFI mount");
                await MountExistingTargetPartitionAsync(operation, efi.TargetPartitionNumber, efiLetter.Value);
                await RebuildBootFilesAsync(operation, targetLetter, efiLetter.Value);
            }
        }
        finally
        {
            if (efiLetter.HasValue)
            {
                await UnmountTargetPartitionAsync(operation, efiLetter.Value);
            }
            await UnmountTargetPartitionAsync(operation, targetLetter);
        }

        return $"{targetLetter}:\\";
    }

    private string GetMigrationSourceRoot(CloneOperation operation, PartitionInfo partition, CloneResult result)
    {
        var sourceRoot = $@"{partition.DriveLetter.Value}:\";
        if (!operation.UseSnapshotForFileMigration)
            return sourceRoot;

        var snapshot = _vssService.GetSnapshotVolumePath(sourceRoot);
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            var warning = $"Snapshot path unavailable for {sourceRoot}; using live source volume for file migration.";
            _logger.Warning(warning);
            result.Warnings.Add(warning);
            return sourceRoot;
        }

        return EnsureTrailingBackslash(snapshot);
    }

    private async Task FormatAndMountTargetPartitionAsync(CloneOperation operation, int targetPartitionNumber, char mountLetter, string label)
    {
        EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "format target partition");
        EnsureTargetVolumeMutationAllowed(operation, mountLetter, "format target partition");

        var scriptPath = Path.GetTempFileName();
        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine($"select partition {targetPartitionNumber}")
            .AppendLine($"format fs=ntfs quick label=\"{label}\"")
            .AppendLine($"assign letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "format and mount target partition");
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            _logger.Info($"DiskPart format+mount output: {output}");
            if (exitCode != 0)
            {
                throw new IOException($"Failed to format/mount target partition {targetPartitionNumber}. ExitCode={exitCode}. Error={error}");
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private async Task MountExistingTargetPartitionAsync(CloneOperation operation, int targetPartitionNumber, char mountLetter)
    {
        EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "mount target partition");
        EnsureTargetVolumeMutationAllowed(operation, mountLetter, "mount target partition");

        var scriptPath = Path.GetTempFileName();
        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine($"select partition {targetPartitionNumber}")
            .AppendLine($"assign letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "mount target partition");
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            _logger.Info($"DiskPart mount output: {output}");
            if (exitCode != 0)
            {
                throw new IOException($"Failed to mount target partition {targetPartitionNumber}. ExitCode={exitCode}. Error={error}");
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private async Task UnmountTargetPartitionAsync(CloneOperation operation, char mountLetter)
    {
        EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "unmount target partition");
        EnsureTargetVolumeMutationAllowed(operation, mountLetter, "unmount target partition");

        var scriptPath = Path.GetTempFileName();
        var script = new StringBuilder()
            .AppendLine($"select volume {NormalizeDriveLetter(mountLetter)}")
            .AppendLine($"remove letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            if (exitCode != 0)
            {
                _logger.Warning($"Failed to unmount {mountLetter}:. ExitCode={exitCode}. Output={output}. Error={error}");
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private async Task CopyWithRoboCopyAsync(string sourceRoot, string targetRoot)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "robocopy.exe",
            Arguments = $"\"{EnsureTrailingBackslash(sourceRoot)}\" \"{EnsureTrailingBackslash(targetRoot)}\" /MIR /COPYALL /DCOPY:DAT /R:2 /W:2 /XJ /SL /XF pagefile.sys hiberfil.sys swapfile.sys",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        _logger.Info($"Robocopy output: {output}");
        // Robocopy uses bitmask style exit codes; 0-7 are success categories.
        if (exitCode > 7)
        {
            throw new IOException($"Robocopy migration failed with code {exitCode}. Error={error}");
        }
    }

    private async Task ValidateTargetVolumeAsync(char targetLetter, string expectedFileSystem, CloneOperation operation, PartitionInfo partition)
    {
        EnsureTargetVolumeMutationAllowed(operation, targetLetter, "validate target volume");

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "chkdsk.exe",
            Arguments = $"{NormalizeDriveLetter(targetLetter)}: /scan",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        _logger.Info($"CHKDSK output for {targetLetter}: {output}");
        if (exitCode != 0)
        {
            throw new IOException($"chkdsk /scan failed for {targetLetter}: (code {exitCode}) {error}");
        }

        var volume = GetVolumeByDriveLetter(targetLetter);
        var driveFormat = volume?.DriveFormat;
        if (volume == null || !string.Equals(driveFormat, expectedFileSystem, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Target partition {partition.TargetPartitionNumber} validation failed. Expected {expectedFileSystem}, got '{driveFormat ?? "<null>"}'.");
        }
    }

    private async Task RebuildBootFilesAsync(CloneOperation operation, char windowsLetter, char efiLetter)
    {
        EnsureTargetVolumeMutationAllowed(operation, windowsLetter, "bcdboot windows source");
        EnsureTargetVolumeMutationAllowed(operation, efiLetter, "bcdboot efi target");

        var windowsPath = $"{NormalizeDriveLetter(windowsLetter)}:\\Windows";
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "bcdboot.exe",
            Arguments = $"\"{windowsPath}\" /s {NormalizeDriveLetter(efiLetter)}: /f UEFI",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        _logger.Info($"bcdboot output: {output}");
        if (exitCode != 0)
        {
            throw new IOException($"bcdboot failed with code {exitCode}. Error={error}");
        }
    }

    private static DriveInfo? GetVolumeByDriveLetter(char driveLetter)
    {
        var normalized = NormalizeDriveLetter(driveLetter);
        return DriveInfo.GetDrives()
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Name) && NormalizeDriveLetter(d.Name[0]) == normalized);
    }

    private async Task<bool> VerifyIntegrityAsync(CloneOperation operation, CloneProgress progress, IReadOnlyCollection<int>? excludedPartitionNumbers = null)
    {
        _logger.Info("Verifying data integrity...");

        if (operation.FullHashVerification)
        {
            return await FullHashVerificationAsync(operation, progress, excludedPartitionNumbers);
        }
        else
        {
            return await SampleHashVerificationAsync(operation, progress, excludedPartitionNumbers);
        }
    }

    /// <summary>
    /// Performs full hash verification of copied data.
    /// </summary>
    private async Task<bool> FullHashVerificationAsync(CloneOperation operation, CloneProgress progress, IReadOnlyCollection<int>? excludedPartitionNumbers = null)
    {
        _logger.Info("Performing full hash verification...");

        var bufferSize = 1024 * 1024; // 1MB buffers
        var totalChecked = 0L;

        foreach (var partition in operation.PartitionsToClone)
        {
            if (excludedPartitionNumbers != null && excludedPartitionNumbers.Contains(partition.PartitionNumber))
            {
                _logger.Warning($"Skipping hash verification for migrated partition {partition.PartitionNumber} (file-system migration mode).");
                continue;
            }

            progress.CurrentPartitionName = $"Verifying {partition.GetTypeName()}";
            ReportProgress(progress);

            var sourceOffset = partition.StartingOffset;
            var targetOffset = GetRequiredTargetStartingOffset(partition);

            // Read and hash source (use independent SHA instances for source and target)
            using (var srcSha = SHA256.Create())
            {
                var sourceHash = await ComputeHashAsync(
                    operation.SourceDisk.DiskNumber,
                    sourceOffset,
                    partition.TargetSizeBytes,
                    bufferSize,
                    srcSha);

                // Read and hash target with a separate SHA instance
                using (var tgtSha = SHA256.Create())
                {
                    var targetHash = await ComputeHashAsync(
                        operation.TargetDisk.DiskNumber,
                        targetOffset,
                        partition.TargetSizeBytes,
                        bufferSize,
                        tgtSha);

                    if (!sourceHash.SequenceEqual(targetHash))
                    {
                        _logger.Error($"Hash mismatch for partition {partition.PartitionNumber}");
                        return false;
                    }
                }
            }

            totalChecked += partition.TargetSizeBytes;
            progress.BytesCopied = totalChecked;
            ReportProgress(progress);
        }

        _logger.Info("Full hash verification passed");
        return true;
    }

    /// <summary>
    /// Performs sampling-based hash verification.
    /// </summary>
    private async Task<bool> SampleHashVerificationAsync(CloneOperation operation, CloneProgress progress, IReadOnlyCollection<int>? excludedPartitionNumbers = null)
    {
        _logger.Info("Performing sampling hash verification...");

        const int sampleCount = 100; // Number of samples per partition
        var bufferSize = 1024 * 1024; // 1MB sample size

        foreach (var partition in operation.PartitionsToClone)
        {
            if (excludedPartitionNumbers != null && excludedPartitionNumbers.Contains(partition.PartitionNumber))
            {
                _logger.Warning($"Skipping sampling hash verification for migrated partition {partition.PartitionNumber} (file-system migration mode).");
                continue;
            }

            progress.CurrentPartitionName = $"Verifying {partition.GetTypeName()} (sampling)";
            ReportProgress(progress);
            var sourcePartitionOffset = partition.StartingOffset;
            var targetPartitionOffset = GetRequiredTargetStartingOffset(partition);

            var samplesChecked = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                // Calculate sample position
                var sourceSampleOffset = sourcePartitionOffset +
                    (partition.TargetSizeBytes * i / sampleCount);
                var targetSampleOffset = targetPartitionOffset +
                    (partition.TargetSizeBytes * i / sampleCount);

                // Ensure we don't go past the end
                sourceSampleOffset = Math.Min(sourceSampleOffset,
                    sourcePartitionOffset + partition.TargetSizeBytes - bufferSize);
                targetSampleOffset = Math.Min(targetSampleOffset,
                    targetPartitionOffset + partition.TargetSizeBytes - bufferSize);

                // Calculate actual sample size (might be smaller near the end)
                var sampleSize = Math.Min(bufferSize,
                    sourcePartitionOffset + partition.TargetSizeBytes - sourceSampleOffset);

                // Use separate hash instances for source and target samples
                using (var srcSha = SHA256.Create())
                using (var tgtSha = SHA256.Create())
                {
                    var sourceHash = await ComputeHashAsync(
                        operation.SourceDisk.DiskNumber,
                        sourceSampleOffset,
                        sampleSize,
                        (int)sampleSize,
                        srcSha);

                    var targetHash = await ComputeHashAsync(
                        operation.TargetDisk.DiskNumber,
                        targetSampleOffset,
                        sampleSize,
                        (int)sampleSize,
                        tgtSha);

                    if (!sourceHash.SequenceEqual(targetHash))
                    {
                        _logger.Error($"Hash mismatch for partition {partition.PartitionNumber} at sample {i}");
                        return false;
                    }
                }

                samplesChecked++;
                progress.PercentComplete = (samplesChecked * 100.0) / sampleCount;
                ReportProgress(progress);
            }
        }

        _logger.Info("Sampling hash verification passed");
        return true;
    }

    /// <summary>
    /// Computes a hash for a disk region.
    /// </summary>
    private async Task<byte[]> ComputeHashAsync(
        int diskNumber,
        long offset,
        long length,
        int bufferSize,
        HashAlgorithm hashAlgorithm)
    {
        var path = $@"\\.\PhysicalDrive{diskNumber}";
        var buffer = new byte[bufferSize];
        var bytesRemaining = length;

        await Task.Run(() =>
        {
            using var handle = WindowsApi.CreateFile(
                path,
                WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"Failed to open disk {diskNumber}: {WindowsApi.GetLastErrorMessage()}");

            while (bytesRemaining > 0)
            {
                var bytesToRead = (int)Math.Min(bufferSize, bytesRemaining);

                if (!WindowsApi.SetFilePointerEx(handle, offset, out _, WindowsApi.FILE_BEGIN))
                    throw new IOException($"Failed to seek: {WindowsApi.GetLastErrorMessage()}");

                uint bytesRead;
                if (!WindowsApi.ReadFile(handle, buffer, (uint)bytesToRead, out bytesRead, IntPtr.Zero))
                    throw new IOException($"Failed to read: {WindowsApi.GetLastErrorMessage()}");

                hashAlgorithm.TransformBlock(buffer, 0, (int)bytesRead, null, 0);

                bytesRemaining -= bytesRead;
                offset += bytesRead;
            }

            hashAlgorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        });

        return hashAlgorithm.Hash ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Expands the Windows partition on the target disk.
    /// </summary>
    private async Task ExpandPartitionAsync(CloneOperation operation, CloneProgress progress)
    {
        EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "expand partition");
        _logger.Info("Expanding Windows partition on target disk...");

        var scriptPath = Path.GetTempFileName();
        var scriptContent = new StringBuilder();

        scriptContent.AppendLine($"select disk {operation.TargetDisk.DiskNumber}");

        // Find the Windows partition (usually the last partition in our clone list)
        var systemPartition = operation.PartitionsToClone.FirstOrDefault(p => p.IsSystemPartition);
        if (systemPartition != null)
        {
            if (systemPartition.TargetPartitionNumber <= 0)
            {
                throw new InvalidOperationException(
                    $"Target partition number is not initialized for source system partition {systemPartition.PartitionNumber}. Aborting expansion.");
            }

            scriptContent.AppendLine($"select partition {systemPartition.TargetPartitionNumber}");
            scriptContent.AppendLine("extend");
        }

        AssertDiskpartScriptTargetsOnlyTargetDisk(operation, scriptContent.ToString(), "expand partition");
        await File.WriteAllTextAsync(scriptPath, scriptContent.ToString());

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    _logger.Info("Windows partition expanded successfully");
                }
                else
                {
                    _logger.Warning($"diskpart expansion exited with code {process.ExitCode}");
                }
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>
    /// Makes the target disk bootable by fixing boot configuration.
    /// </summary>
    private async Task<bool> MakeBootableAsync(CloneOperation operation)
    {
        _logger.Info("Making target disk bootable...");

        try
        {
            // Step 1: Refresh disk layout
            await RefreshDiskLayoutAsync(operation);

            // Step 2: Fix BCD on the clone
            // The BCD store is already copied from the source, but we may need to
            // update device paths. For most cases, simply copying the EFI partition
            // is sufficient.

            // Step 3: Use bcdboot to ensure proper boot files are present
            await UpdateBootConfigurationAsync(operation);

            _logger.Info("Target disk should be bootable");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to make target bootable: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Refreshes the disk layout to ensure partitions are recognized.
    /// </summary>
    private async Task RefreshDiskLayoutAsync(CloneOperation operation)
    {
        await Task.Run(() =>
        {
            var path = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";

            using var handle = WindowsApi.CreateFile(
                path,
                WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return;

            // Update disk properties
            uint bytesReturned;
            WindowsApi.DeviceIoControl(
                handle,
                WindowsApi.IOCTL_DISK_UPDATE_PROPERTIES,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                out bytesReturned,
                IntPtr.Zero);
        });
    }

    /// <summary>
    /// Brings the target disk back online after raw copy is complete.
    /// Must be called after CopyPartitionAsync finishes so that verification,
    /// expansion, and boot configuration can access the disk normally.
    /// </summary>
    private async Task OnlineTargetDiskAsync(CloneOperation operation)
    {
        EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "online target disk");
        _logger.Info($"Bringing target disk {operation.TargetDisk.DiskNumber} back online...");

        var scriptPath = Path.GetTempFileName();
        var scriptContent = new StringBuilder();
        scriptContent.AppendLine($"select disk {operation.TargetDisk.DiskNumber}");
        scriptContent.AppendLine("online disk");
        scriptContent.AppendLine("attributes disk clear readonly");

        AssertDiskpartScriptTargetsOnlyTargetDisk(operation, scriptContent.ToString(), "online target disk");
        await File.WriteAllTextAsync(scriptPath, scriptContent.ToString());

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    _logger.Warning($"diskpart online failed with code {process.ExitCode}. Output: {output}");
                }
                else
                {
                    _logger.Info("Target disk brought back online successfully");
                }
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }

        // Give Windows a moment to mount volumes
        await Task.Delay(2000);
    }

    /// <summary>
    /// Updates the boot configuration on the target disk.
    /// </summary>
    private async Task UpdateBootConfigurationAsync(CloneOperation operation)
    {
        _logger.Info("Updating boot configuration...");

        // Find the Windows partition on target
        // In a real implementation, you would need to discover which partition on
        // the target corresponds to C: and mount it temporarily

        // For this implementation, we'll use bcdboot to recreate the boot files
        // This requires mounting the target disk partitions, which is complex
        // We'll skip this step in the MVP but document it

        _logger.Info("Boot configuration update skipped in MVP (manual step may be required)");
    }

    /// <summary>
    /// Marks the target disk as incomplete after a cancelled operation.
    /// </summary>
    private async Task MarkTargetIncompleteAsync(CloneOperation operation)
    {
        _logger.Info("Marking target disk as incomplete...");

        // Write a marker file or modify the partition table to indicate incomplete state
        // This prevents booting from an incomplete clone

        try
        {
            var path = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";

            await Task.Run(() =>
            {
                using var handle = WindowsApi.CreateFile(
                    path,
                    WindowsApi.GENERIC_WRITE,
                    WindowsApi.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    WindowsApi.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                    return;

                // Write an incomplete marker to the first sector
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

    /// <summary>
    /// Builds the list of next steps for the user.
    /// </summary>
    private void BuildNextSteps(CloneResult result, CloneOperation operation)
    {
        result.BootMode = operation.SourceDisk.IsGpt ? "UEFI" : "BIOS";

        if (result.Success)
        {
            result.NextSteps.Add("Safely shutdown your computer.");
            result.NextSteps.Add("Disconnect the source disk.");
            result.NextSteps.Add($"Connect the target disk to the same SATA port as the source was on.");
            result.NextSteps.Add("Power on the computer.");
            result.NextSteps.Add("The system should boot from the cloned disk.");
            result.NextSteps.Add("Verify everything works correctly.");
            result.NextSteps.Add("You may now repurpose the old source disk as a backup.");

            if (result.BootMode == "UEFI")
            {
                result.NextSteps.Insert(4, "Enter BIOS/UEFI settings and verify the new disk is in the boot order.");
            }
        }
        else if (result.TargetMarkedIncomplete)
        {
            result.NextSteps.Add("The target disk has been marked as incomplete and will not boot.");
            result.NextSteps.Add("You can safely reformat the target disk.");
            result.NextSteps.Add("Run the cloning operation again with a stable connection.");
        }
    }

    /// <summary>
    /// Cancels the ongoing cloning operation.
    /// </summary>
    public void Cancel()
    {
        _logger.Info("Cancel requested by user");
        _cancellationTokenSource.Cancel();
    }

    /// <summary>
    /// Reports progress to subscribers.
    /// </summary>
    private void ReportProgress(CloneProgress progress)
    {
        ProgressUpdate?.Invoke(progress);
    }

    /// <summary>
    /// Gets a summary of the cloning operation for confirmation.
    /// </summary>
    public string GetOperationSummary(CloneOperation operation)
    {
        // Ensure layout is calculated before generating summary
        try { CalculateTargetLayout(operation); } catch { }

        var sb = new StringBuilder();
        sb.AppendLine("=== Cloning Operation Summary ===");
        sb.AppendLine();
        sb.AppendLine("Source Disk:");
        sb.AppendLine($"  ID: {operation.SourceDisk.DiskNumber}");
        sb.AppendLine($"  Model: {operation.SourceDisk.FriendlyName}");
        sb.AppendLine($"  Size: {operation.SourceDisk.SizeDisplay}");
        sb.AppendLine($"  Type: {(operation.SourceDisk.IsGpt ? "GPT/UEFI" : "MBR/BIOS")}");
        sb.AppendLine();
        sb.AppendLine("Target Disk:");
        sb.AppendLine($"  ID: {operation.TargetDisk.DiskNumber}");
        sb.AppendLine($"  Model: {operation.TargetDisk.FriendlyName}");
        sb.AppendLine($"  Size: {operation.TargetDisk.SizeDisplay}");
        sb.AppendLine($"  Type: {(operation.TargetDisk.IsGpt ? "GPT/UEFI" : "MBR/BIOS")}");
        sb.AppendLine();
        sb.AppendLine("Partitions to Clone:");
        foreach (var partition in operation.PartitionsToClone)
        {
            var role = partition.GetTypeName();
            var label = string.IsNullOrEmpty(partition.VolumeLabel) ? "" : $" [{partition.VolumeLabel}]";
            sb.AppendLine($"  [{partition.PartitionNumber}] {role}{label} - {partition.SizeDisplay}");
        }
        sb.AppendLine();
        sb.AppendLine("Planned Target Layout:");
        foreach (var partition in operation.PartitionsToClone)
        {
            var role = partition.GetTypeName();
            var targetSize = FormatBytes(partition.TargetSizeBytes);
            var shrinkInfo = (partition.TargetSizeBytes < partition.SizeBytes) 
                ? $" [SHRUNK from {partition.SizeDisplay}]" 
                : "";
            sb.AppendLine($"  [{partition.PartitionNumber}] {role} - {targetSize}{shrinkInfo}");
        }
        sb.AppendLine();

        sb.AppendLine("Options:");
        sb.AppendLine($"  Use VSS: {operation.UseVss}");
        sb.AppendLine($"  Verify: {operation.VerifyIntegrity} ({(operation.FullHashVerification ? "Full" : "Sampling")})");
        sb.AppendLine($"  Strict Verify Fail: {operation.StrictVerificationFailureStopsClone}");
        sb.AppendLine($"  Expand C:: {operation.AutoExpandWindowsPartition}");
        sb.AppendLine($"  Allow Smaller Target: {operation.AllowSmallerTarget}");
        sb.AppendLine($"  Snapshot For Migration: {operation.UseSnapshotForFileMigration}");
        sb.AppendLine();

        var estimatedTime = EstimateOperationTime(operation);
        sb.AppendLine($"Estimated Time: {estimatedTime}");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Estimates the time required for the cloning operation.
    /// </summary>
    private TimeSpan EstimateOperationTime(CloneOperation operation)
    {
        // Estimate based on typical USB 3.0 write speeds of ~100 MB/s
        // plus some overhead for verification
        const long typicalWriteSpeed = 100 * 1024 * 1024; // 100 MB/s

        var totalBytes = operation.PartitionsToClone.Sum(p => p.SizeBytes);
        var baseSeconds = totalBytes / typicalWriteSpeed;

        // Add 20% overhead
        baseSeconds = (long)(baseSeconds * 1.2);

        // Add verification time if enabled
        if (operation.VerifyIntegrity)
        {
            if (operation.FullHashVerification)
                baseSeconds = (long)(baseSeconds * 2.5); // Full verification takes ~2.5x
            else
                baseSeconds = (long)(baseSeconds * 1.2); // Sampling adds ~20%
        }

        return TimeSpan.FromSeconds(baseSeconds);
    }
}
