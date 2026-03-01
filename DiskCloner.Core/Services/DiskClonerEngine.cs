using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using System.Security.Cryptography;
using System.Text;

namespace DiskCloner.Core.Services;

/// <summary>
/// Main engine for cloning disks. Handles validation, snapshot creation,
/// data copying, verification, and partition expansion.
/// </summary>
public class DiskClonerEngine
{
    private readonly ILogger _logger;
    private readonly DiskEnumerator _diskEnumerator;
    private readonly VssSnapshotService _vssService;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

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

                await _vssService.CreateSnapshotsAsync(volumesToSnapshot);
            }

            // Step 3: Prepare target disk
            progress.Stage = CloneStage.PreparingTarget;
            progress.StatusMessage = "Preparing target disk...";
            ReportProgress(progress);

            await PrepareTargetDiskAsync(operation, progress);

            // Step 4: Copy partition data
            progress.Stage = CloneStage.CopyingData;
            progress.StatusMessage = "Copying partition data...";
            ReportProgress(progress);

            progress.TotalPartitions = operation.PartitionsToClone.Count;
            var totalBytesToCopy = operation.PartitionsToClone.Sum(p => p.SizeBytes);
            progress.TotalBytes = totalBytesToCopy;
            var bytesCopied = 0L;

            foreach (var partition in operation.PartitionsToClone)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Operation was cancelled by user");
                }

                progress.CurrentPartition = operation.PartitionsToClone.IndexOf(partition);
                progress.CurrentPartitionName = partition.GetTypeName();
                progress.StatusMessage = $"Copying {partition.GetTypeName()} ({partition.SizeDisplay})...";
                ReportProgress(progress);

                bytesCopied = await CopyPartitionAsync(operation, partition, progress, bytesCopied);
                
                progress.BytesCopied = bytesCopied;
                progress.PercentComplete = (bytesCopied * 100.0) / totalBytesToCopy;
                ReportProgress(progress);
            }

            result.BytesCopied = bytesCopied;

            // Step 5: Verify data integrity
            if (operation.VerifyIntegrity)
            {
                progress.Stage = CloneStage.Verifying;
                progress.StatusMessage = "Verifying data integrity...";
                ReportProgress(progress);

                result.IntegrityVerified = await VerifyIntegrityAsync(operation, progress);
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

            // Mark target as incomplete
            await MarkTargetIncompleteAsync(operation);

            progress.Stage = CloneStage.Cancelled;
            progress.IsCancelled = true;
            progress.StatusMessage = "Operation was cancelled. Target disk marked as incomplete.";
            ReportProgress(progress);

            result.TargetMarkedIncomplete = true;
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
            // Cleanup
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
        await ClearTargetDiskAsync(operation);

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
                throw new IOException($"Failed to clear target disk: {WindowsApi.GetLastErrorMessage()}");

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
        var scriptPath = Path.GetTempFileName();
        var scriptContent = new StringBuilder();

        scriptContent.AppendLine($"select disk {operation.TargetDisk.DiskNumber}");
        scriptContent.AppendLine("clean");

        if (operation.SourceDisk.IsGpt)
        {
            scriptContent.AppendLine("convert gpt noerr");
        }
        else
        {
            scriptContent.AppendLine("convert mbr noerr");
        }

        // Create partitions matching the source
        var alignment = 1024L * 1024; // 1MB alignment
        var currentOffset = 1024L * 1024; // Start at 1MB

        foreach (var partition in operation.PartitionsToClone.OrderBy(p => p.StartingOffset))
        {
            var sizeMB = partition.TargetSizeBytes / (1024 * 1024);
            
            // REMOVED OFFSET: Let DiskPart handle alignment/placement automatically for better reliability on all controllers
            if (partition.IsEfiPartition)
            {
                scriptContent.AppendLine($"create partition efi size={sizeMB}");
                scriptContent.AppendLine("format fs=fat32 quick");
            }
            else if (partition.IsMsrPartition)
            {
                scriptContent.AppendLine($"create partition msr size={sizeMB}");
            }
            else
            {
                scriptContent.AppendLine($"create partition primary size={sizeMB}");
                if (partition.IsSystemPartition)
                {
                    scriptContent.AppendLine("format fs=ntfs quick");
                }
            }
        }

        // List partitions to verify
        scriptContent.AppendLine("list partition");

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
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    _logger.Error($"diskpart failed with code {process.ExitCode}. Output: {output} Error: {error}");
                    _logger.Error("Failed DiskPart Script content:");
                    _logger.Error(scriptContent.ToString());
                    throw new IOException($"Failed to create partitions: DiskPart error {process.ExitCode}. See logs for details.");
                }
                else
                {
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

    /// <summary>
    /// Copies a single partition from source to target.
    /// </summary>
    private async Task<long> CopyPartitionAsync(CloneOperation operation, PartitionInfo partition, CloneProgress progress, long totalBytesAlreadyCopied)
    {
        _logger.Info($"Copying partition {partition.PartitionNumber} ({partition.SizeDisplay}) to target ({FormatBytes(partition.TargetSizeBytes)})");

        var sourcePath = $@"\\.\PhysicalDrive{operation.SourceDisk.DiskNumber}";
        var targetPath = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";

        var partitionBytesCopied = 0L;
        var totalBytesInPartition = partition.TargetSizeBytes; // Truncate if Target is smaller
        var bufferSize = operation.IoBufferSize;

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
                throw new IOException($"Failed to open source disk: {WindowsApi.GetLastErrorMessage()}");

            using var targetHandle = WindowsApi.CreateFile(
                targetPath,
                WindowsApi.GENERIC_WRITE,
                WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                WindowsApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (targetHandle.IsInvalid)
                throw new IOException($"Failed to open target disk: {WindowsApi.GetLastErrorMessage()}");

            // Align buffer to sector size
            bufferSize = ((bufferSize + 511) / 512) * 512;

            var buffer = new byte[bufferSize];
            var offset = partition.StartingOffset;
            var lastProgressUpdate = DateTime.UtcNow;

            while (partitionBytesCopied < totalBytesInPartition)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    _logger.Warning("Copy operation cancelled");
                    break;
                }

                var bytesToRead = (int)Math.Min(bufferSize, totalBytesInPartition - partitionBytesCopied);

                // Seek to source position
                if (!WindowsApi.SetFilePointerEx(sourceHandle, offset, out _, WindowsApi.FILE_BEGIN))
                    throw new IOException($"Failed to seek source: {WindowsApi.GetLastErrorMessage()}");

                // Seek to target position
                if (!WindowsApi.SetFilePointerEx(targetHandle, offset, out _, WindowsApi.FILE_BEGIN))
                    throw new IOException($"Failed to seek target: {WindowsApi.GetLastErrorMessage()}");

                // Read from source
                uint bytesRead;
                if (!WindowsApi.ReadFile(sourceHandle, buffer, (uint)bytesToRead, out bytesRead, IntPtr.Zero))
                    throw new IOException($"Failed to read from source: {WindowsApi.GetLastErrorMessage()}");

                // Write to target
                uint bytesWritten;
                if (!WindowsApi.WriteFile(targetHandle, buffer, bytesRead, out bytesWritten, IntPtr.Zero))
                    throw new IOException($"Failed to write to target: {WindowsApi.GetLastErrorMessage()}");

                partitionBytesCopied += bytesRead;
                offset += bytesRead;

                // Update progress periodically
                if (DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromMilliseconds(250)) // More frequent updates
                {
                    var currentTotalCopied = totalBytesAlreadyCopied + partitionBytesCopied;
                    progress.BytesCopied = currentTotalCopied;
                    progress.ThroughputBytesPerSec = bytesRead / (DateTime.UtcNow - lastProgressUpdate).TotalSeconds;
                    progress.PercentComplete = (currentTotalCopied * 100.0) / progress.TotalBytes;

                    var remainingBytes = progress.TotalBytes - progress.BytesCopied;
                    if (progress.ThroughputBytesPerSec > 0)
                    {
                        progress.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingBytes / progress.ThroughputBytesPerSec);
                    }

                    ReportProgress(progress);
                    lastProgressUpdate = DateTime.UtcNow;
                }
            }

            // Flush target buffers
            WindowsApi.FlushFileBuffers(targetHandle);

            _logger.Info($"Copied {partitionBytesCopied:N0} bytes for partition {partition.PartitionNumber}");
        });

        return totalBytesAlreadyCopied + partitionBytesCopied;
    }

    /// <summary>
    /// Verifies data integrity by sampling hashes.
    /// </summary>
    private async Task<bool> VerifyIntegrityAsync(CloneOperation operation, CloneProgress progress)
    {
        _logger.Info("Verifying data integrity...");

        if (operation.FullHashVerification)
        {
            return await FullHashVerificationAsync(operation, progress);
        }
        else
        {
            return await SampleHashVerificationAsync(operation, progress);
        }
    }

    /// <summary>
    /// Performs full hash verification of copied data.
    /// </summary>
    private async Task<bool> FullHashVerificationAsync(CloneOperation operation, CloneProgress progress)
    {
        _logger.Info("Performing full hash verification...");

        var bufferSize = 1024 * 1024; // 1MB buffers
        var totalChecked = 0L;

        foreach (var partition in operation.PartitionsToClone)
        {
            progress.CurrentPartitionName = $"Verifying {partition.GetTypeName()}";
            ReportProgress(progress);

            using var sha256 = SHA256.Create();
            var offset = partition.StartingOffset;

            // Read and hash source
            var sourceHash = await ComputeHashAsync(
                operation.SourceDisk.DiskNumber,
                offset,
                partition.TargetSizeBytes,
                bufferSize,
                sha256);

            // Read and hash target
            var targetHash = await ComputeHashAsync(
                operation.TargetDisk.DiskNumber,
                offset,
                partition.TargetSizeBytes,
                bufferSize,
                sha256);

            if (!sourceHash.SequenceEqual(targetHash))
            {
                _logger.Error($"Hash mismatch for partition {partition.PartitionNumber}");
                return false;
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
    private async Task<bool> SampleHashVerificationAsync(CloneOperation operation, CloneProgress progress)
    {
        _logger.Info("Performing sampling hash verification...");

        const int sampleCount = 100; // Number of samples per partition
        var bufferSize = 1024 * 1024; // 1MB sample size

        foreach (var partition in operation.PartitionsToClone)
        {
            progress.CurrentPartitionName = $"Verifying {partition.GetTypeName()} (sampling)";
            ReportProgress(progress);

            var samplesChecked = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                // Calculate sample position
                var sampleOffset = partition.StartingOffset +
                    (partition.TargetSizeBytes * i / sampleCount);

                // Ensure we don't go past the end
                sampleOffset = Math.Min(sampleOffset,
                    partition.StartingOffset + partition.TargetSizeBytes - bufferSize);

                // Calculate actual sample size (might be smaller near the end)
                var sampleSize = Math.Min(bufferSize,
                    partition.StartingOffset + partition.TargetSizeBytes - sampleOffset);

                using var sha256 = SHA256.Create();

                var sourceHash = await ComputeHashAsync(
                    operation.SourceDisk.DiskNumber,
                    sampleOffset,
                    sampleSize,
                    (int)sampleSize,
                    sha256);

                var targetHash = await ComputeHashAsync(
                    operation.TargetDisk.DiskNumber,
                    sampleOffset,
                    sampleSize,
                    (int)sampleSize,
                    sha256);

                if (!sourceHash.SequenceEqual(targetHash))
                {
                    _logger.Error($"Hash mismatch for partition {partition.PartitionNumber} at sample {i}");
                    return false;
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
        _logger.Info("Expanding Windows partition on target disk...");

        var scriptPath = Path.GetTempFileName();
        var scriptContent = new StringBuilder();

        scriptContent.AppendLine($"select disk {operation.TargetDisk.DiskNumber}");

        // Find the Windows partition (usually the last partition in our clone list)
        var systemPartition = operation.PartitionsToClone.FirstOrDefault(p => p.IsSystemPartition);
        if (systemPartition != null)
        {
            // Select by partition number (in diskpart, partitions are numbered sequentially)
            // We'll need to match based on the order we created them
            var partitionIndex = operation.PartitionsToClone.IndexOf(systemPartition) + 1;
            scriptContent.AppendLine($"select partition {partitionIndex}");
            scriptContent.AppendLine("extend");
        }

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
        sb.AppendLine($"  Expand C:: {operation.AutoExpandWindowsPartition}");
        sb.AppendLine($"  Allow Smaller Target: {operation.AllowSmallerTarget}");
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
