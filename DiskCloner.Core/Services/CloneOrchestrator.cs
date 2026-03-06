using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Utilities;
using System.Text;

namespace DiskCloner.Core.Services;

/// <summary>
/// Orchestrates the high-level cloning process by delegating to specialized services.
/// </summary>
public class CloneOrchestrator
{
    private const long OneMiB = 1024L * 1024L;

    private readonly ILogger _logger;
    private readonly ICloneValidator _validator;
    private readonly IDiskpartService _diskpartService;
    private readonly IPartitionCopier _copier;
    private readonly IFileSystemMigrator _migrator;
    private readonly ISystemQuietModeService _quietMode;
    private readonly IIntegrityVerifier _verifier;
    private readonly ITargetDiskLifecycleManager _lifecycle;
    private readonly DiskEnumerator _diskEnumerator;
    private readonly VssSnapshotService _vssService;

    // The token source used for cancellation
    private readonly CancellationTokenSource _cancellationTokenSource;

    public event Action<CloneProgress>? ProgressUpdate;

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
        VssSnapshotService vssService,
        CancellationTokenSource cancellationTokenSource)
    {
        _logger = logger;
        _validator = validator;
        _diskpartService = diskpartService;
        _copier = copier;
        _migrator = migrator;
        _quietMode = quietMode;
        _verifier = verifier;
        _lifecycle = lifecycle;
        _diskEnumerator = diskEnumerator;
        _vssService = vssService;
        _cancellationTokenSource = cancellationTokenSource;
    }

    public void Cancel()
    {
        _logger.Info("Cancel requested by user");
        _cancellationTokenSource.Cancel();
    }

    public void ReportProgress(CloneProgress progress)
    {
        ProgressUpdate?.Invoke(progress);
    }

    public async Task<CloneResult> CloneAsync(CloneOperation operation)
    {
        var progress = new CloneProgress { Stage = CloneStage.Validating, StatusMessage = "Validating configuration..." };
        ReportProgress(progress);

        var startTime = DateTime.UtcNow;
        var result = new CloneResult { Success = false, IsBootable = false };
        var targetDiskWasPrepared = false;
        var partitionMappingCompleted = false;
        BootFinalizationStatus? bootStatus = null;
        QuietModeState? quietModeState = null;

        try
        {
            await _validator.ValidateAsync(operation, progress, ReportProgress);

            if (operation.EnableQuietMode)
                quietModeState = await _quietMode.EnterAsync(operation, result, progress);

            if (operation.UseVss)
            {
                progress.Stage = CloneStage.CreatingSnapshots;
                progress.StatusMessage = "Creating VSS snapshots for consistent data...";
                ReportProgress(progress);

                var volumesToSnapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var partition in operation.PartitionsToClone)
                {
                    if (partition.DriveLetter.HasValue)
                        volumesToSnapshot.Add($@"{char.ToUpperInvariant(partition.DriveLetter.Value)}:\");
                }
                await _vssService.CreateSnapshotsForVolumesAsync(volumesToSnapshot.ToList());
            }

            progress.Stage = CloneStage.PreparingTarget;
            progress.StatusMessage = "Preparing target disk...";
            ReportProgress(progress);

            try { await _diskpartService.ClearTargetDiskAsync(operation); }
            catch (IOException ex) when (ex.Message.Contains("Error 5") || ex.Message.Contains("Access is denied"))
            {
                _logger.Warning($"Pre-clear skipped due to access denied: {ex.Message}. Continuing with diskpart clean.");
            }
            await _diskpartService.CreatePartitionTableAsync(operation);
            await _diskpartService.ApplyTargetPartitionOffsetsAsync(operation);
            targetDiskWasPrepared = true;
            partitionMappingCompleted = true;

            progress.Stage = CloneStage.CopyingData;
            progress.StatusMessage = "Copying partition data...";
            ReportProgress(progress);

            progress.TotalPartitions = operation.PartitionsToClone.Count;
            var totalBytesToCopy = _copier.CalculatePlannedTotalBytes(operation);
            progress.TotalBytes = totalBytesToCopy;
            var bytesCopied = 0L;
            var migratedPartitionNumbers = new HashSet<int>();
            var targetDiskIsOffline = false;
            var partitionCopyFailures = new List<string>();
            Exception? firstPartitionCopyException = null;

            for (int i = 0; i < operation.PartitionsToClone.Count; i++)
            {
                var partition = operation.PartitionsToClone[i];
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    throw new OperationCanceledException("Operation was cancelled by user");

                progress.CurrentPartition = i;
                progress.CurrentPartitionName = partition.GetTypeName();
                var strategy = _copier.GetCopyStrategy(operation, partition);

                try
                {
                    if (strategy == CopyStrategy.FileSystemMigration)
                    {
                        progress.StatusMessage = $"Migrating {partition.GetTypeName()} ({partition.SizeDisplay}) using file copy...";
                        ReportProgress(progress);
                        if (targetDiskIsOffline)
                        {
                            progress.StatusMessage = "Bringing target disk online for file-system migration...";
                            ReportProgress(progress);
                            await _lifecycle.OnlineTargetDiskAsync(operation);
                            targetDiskIsOffline = false;
                        }

                        var migrationPlannedBytes = PartitionCopier.GetEstimatedMigrationBytes(partition);
                        var migratedBytes = await _migrator.MigrateAsync(operation, partition, result, progress, bytesCopied, migrationPlannedBytes);
                        migratedPartitionNumbers.Add(partition.PartitionNumber);
                        
                        if (migratedBytes > migrationPlannedBytes)
                        {
                            var delta = migratedBytes - migrationPlannedBytes;
                            totalBytesToCopy += delta;
                            progress.TotalBytes = totalBytesToCopy;
                            _logger.Info($"Migration for partition {partition.PartitionNumber} exceeded estimate by {delta:N0} bytes; adjusted total planned bytes.");
                        }

                        bytesCopied += migratedBytes;
                        progress.BytesCopied = Math.Min(bytesCopied, totalBytesToCopy);
                        progress.PercentComplete = (progress.BytesCopied * 100.0) / totalBytesToCopy;
                        progress.ThroughputBytesPerSec = 0;
                        progress.EstimatedTimeRemaining = TimeSpan.Zero;
                        ReportProgress(progress);

                        var remainingNeedsRawDiskCopy = operation.PartitionsToClone
                            .Skip(i + 1)
                            .Any(next => _copier.GetCopyStrategy(operation, next) != CopyStrategy.FileSystemMigration);

                        if (remainingNeedsRawDiskCopy)
                        {
                            await _lifecycle.OfflineTargetDiskAsync(operation);
                            targetDiskIsOffline = true;
                        }
                    }
                    else
                    {
                        if (!targetDiskIsOffline)
                        {
                            await _lifecycle.OfflineTargetDiskAsync(operation);
                            targetDiskIsOffline = true;
                        }

                        if (strategy == CopyStrategy.SmartBlock)
                        {
                            progress.StatusMessage = $"Smart-copying {partition.GetTypeName()} ({partition.SizeDisplay}) - reading NTFS bitmap...";
                            ReportProgress(progress);
                            bytesCopied = await _copier.CopySmartAsync(operation, partition, progress, bytesCopied);
                        }
                        else
                        {
                            progress.StatusMessage = $"Copying {partition.GetTypeName()} ({partition.SizeDisplay})...";
                            ReportProgress(progress);
                            bytesCopied = await _copier.CopyRawAsync(operation, partition, progress, bytesCopied);
                        }

                        progress.BytesCopied = bytesCopied;
                        progress.PercentComplete = (bytesCopied * 100.0) / totalBytesToCopy;
                        ReportProgress(progress);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    firstPartitionCopyException ??= ex;
                    var failureMessage = $"Partition {partition.PartitionNumber} ({partition.GetTypeName()}) failed during {strategy}: {ex.Message}";
                    partitionCopyFailures.Add(failureMessage);
                    _logger.Error(failureMessage, ex);

                    progress.LastError = failureMessage;
                    progress.StatusMessage = $"{failureMessage} Continuing with remaining partitions...";
                    ReportProgress(progress);
                }

                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    throw new OperationCanceledException("Operation was cancelled by user");
            }

            result.BytesCopied = bytesCopied;
            if (partitionCopyFailures.Count > 0)
                throw new InvalidOperationException("One or more partitions failed during copy: " + string.Join(" | ", partitionCopyFailures), firstPartitionCopyException);

            if (operation.VerifyIntegrity)
            {
                progress.Stage = CloneStage.Verifying;
                progress.StatusMessage = "Verifying integrity...";
                progress.BytesCopied = 0;
                progress.PercentComplete = 0;
                progress.ThroughputBytesPerSec = 0;
                progress.EstimatedTimeRemaining = TimeSpan.Zero;
                ReportProgress(progress);

                var excludedFromVerification = _verifier.BuildExclusions(operation, migratedPartitionNumbers, result);
                result.IntegrityVerified = await _verifier.VerifyAsync(operation, progress, excludedFromVerification);
                if (!result.IntegrityVerified && operation.StrictVerificationFailureStopsClone)
                    throw new InvalidOperationException("Integrity verification failed; clone aborted due to strict verification setting.");
            }
            else
            {
                result.IntegrityVerified = true;
            }

            if (operation.AutoExpandWindowsPartition || operation.AllowSmallerTarget)
            {
                if (targetDiskIsOffline)
                {
                    await _lifecycle.OnlineTargetDiskAsync(operation);
                    targetDiskIsOffline = false;
                }

                progress.Stage = CloneStage.ExpandingPartitions;
                progress.StatusMessage = operation.AllowSmallerTarget ? "Fixing NTFS metadata..." : "Expanding Windows partition...";
                ReportProgress(progress);

                await _lifecycle.ExpandPartitionAsync(operation, progress);
            }

            if (targetDiskIsOffline)
            {
                await _lifecycle.OnlineTargetDiskAsync(operation);
                targetDiskIsOffline = false;
            }

            bootStatus = await _lifecycle.MakeBootableAsync(operation);
            result.IsBootable = bootStatus.Success;
            if (!result.IsBootable)
                throw new InvalidOperationException("Boot finalization failed. Clone data was copied, but target boot preparation did not complete.");

            result.Duration = DateTime.UtcNow - startTime;
            if (result.Duration.TotalSeconds > 0)
                result.AverageThroughputBytesPerSec = result.BytesCopied / result.Duration.TotalSeconds;

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
            result.BootMode = operation.SourceDisk.IsGpt ? "UEFI" : "BIOS";
            if (ex.Message.StartsWith("Boot finalization failed", StringComparison.OrdinalIgnoreCase))
            {
                result.NextSteps.Clear();
                result.NextSteps.Add("Re-run clone to retry automated boot finalization.");
                result.NextSteps.Add("If the issue persists, mount target Windows+EFI and run bcdboot manually.");
                result.NextSteps.Add("Run chkdsk /f on target Windows volume before first boot.");
            }
            return result;
        }
        finally
        {
            if (targetDiskWasPrepared)
            {
                try { await _lifecycle.OnlineTargetDiskAsync(operation); }
                catch (Exception ex) { _logger.Warning($"Failed to bring target disk back online: {ex.Message}"); }
            }

            progress.Stage = CloneStage.Cleanup;
            progress.StatusMessage = "Cleaning up...";
            ReportProgress(progress);

            try { await _vssService.DeleteSnapshotsAsync(); }
            catch (Exception ex) { _logger.Warning($"Error during cleanup: {ex.Message}"); }

            try { await _quietMode.ExitAsync(quietModeState, result); }
            catch (Exception ex) { _logger.Warning($"Failed to restore quiet mode state: {ex.Message}"); }

            AppendHealthChecksSummary(result, partitionMappingCompleted, bootStatus, _vssService.LastCleanupStatus, _vssService.LastCleanupMessage);
        }
    }

    public string GetOperationSummary(CloneOperation operation)
    {
        _validator.CalculateTargetLayout(operation);

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
        sb.AppendLine("Partition Plan (source -> target):");
        foreach (var partition in operation.PartitionsToClone)
        {
            var role = partition.GetTypeName();
            var label = string.IsNullOrEmpty(partition.VolumeLabel) ? "" : $" [{partition.VolumeLabel}]";
            var sourceSize = ByteFormatter.Format(partition.SizeBytes);
            var targetSize = ByteFormatter.Format(partition.TargetSizeBytes);
            var resizeInfo = partition.TargetSizeBytes < partition.SizeBytes ? " [SHRUNK]" : partition.TargetSizeBytes > partition.SizeBytes ? " [EXPANDED]" : "";
            sb.AppendLine($"  [{partition.PartitionNumber}] {role}{label}: {sourceSize} -> {targetSize}{resizeInfo}");
        }
        sb.AppendLine();
        var plannedFootprint = OneMiB;
        plannedFootprint += operation.PartitionsToClone.Sum(p => p.TargetSizeBytes + OneMiB);
        sb.AppendLine($"Planned Target Footprint: {ByteFormatter.Format(plannedFootprint)} / {operation.TargetDisk.SizeDisplay}");
        sb.AppendLine();
        sb.AppendLine("Options:");
        sb.AppendLine($"  Use VSS: {operation.UseVss}");
        sb.AppendLine($"  Source Read Mode: {operation.SourceReadMode}");
        sb.AppendLine($"  Quiet Mode: {operation.EnableQuietMode}");
        sb.AppendLine($"  Verify: {operation.VerifyIntegrity}");
        sb.AppendLine($"  Strict Verify Fail: {operation.StrictVerificationFailureStopsClone}");
        sb.AppendLine($"  Expand C:: {operation.AutoExpandWindowsPartition}");
        sb.AppendLine($"  Allow Smaller Target: {operation.AllowSmallerTarget}");
        sb.AppendLine($"  Snapshot For Migration: {operation.UseSnapshotForFileMigration}");
        sb.AppendLine();
        sb.AppendLine($"Estimated Time: {EstimateOperationTime(operation)}");
        sb.AppendLine();

        return sb.ToString();
    }

    private TimeSpan EstimateOperationTime(CloneOperation operation)
    {
        const long typicalWriteSpeed = 100 * 1024 * 1024; // 100 MB/s
        var totalBytes = operation.PartitionsToClone.Sum(p => p.SizeBytes);
        var baseSeconds = totalBytes / typicalWriteSpeed;
        baseSeconds = (long)(baseSeconds * 1.2);
        if (operation.VerifyIntegrity) baseSeconds = (long)(baseSeconds * 1.2);
        return TimeSpan.FromSeconds(baseSeconds);
    }

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
                result.NextSteps.Insert(4, "Enter BIOS/UEFI settings and verify the new disk is in the boot order.");
        }
        else if (result.TargetMarkedIncomplete)
        {
            result.NextSteps.Add("The target disk has been marked as incomplete and will not boot.");
            result.NextSteps.Add("You can safely reformat the target disk.");
            result.NextSteps.Add("Run the cloning operation again with a stable connection.");
        }
    }

    private void AppendHealthChecksSummary(CloneResult result, bool partitionMappingCompleted, BootFinalizationStatus? bootStatus, string vssCleanupStatus, string vssCleanupMessage)
    {
        result.HealthChecks.Clear();
        var partitionMappingLine = $"Partition mapping: {(partitionMappingCompleted ? "OK" : "Not completed")}";
        result.HealthChecks.Add(partitionMappingLine);
        _logger.Info($"Health check: {partitionMappingLine}");
        var bootFilesLine = $"Boot files: {(bootStatus?.BootFilesRebuilt == true ? "OK" : "Not rebuilt")}";
        result.HealthChecks.Add(bootFilesLine);
        _logger.Info($"Health check: {bootFilesLine}");
        var volumeCleanLine = $"Windows volume clean: {bootStatus?.WindowsVolumeStatus ?? "Not checked"}";
        result.HealthChecks.Add(volumeCleanLine);
        _logger.Info($"Health check: {volumeCleanLine}");
        var normalizedCleanupStatus = string.IsNullOrWhiteSpace(vssCleanupStatus) ? "Unknown" : vssCleanupStatus;
        var vssLine = $"VSS cleanup: {normalizedCleanupStatus}";
        if (!string.IsNullOrWhiteSpace(vssCleanupMessage)) vssLine += $" ({vssCleanupMessage})";
        result.HealthChecks.Add(vssLine);
        _logger.Info($"Health check: {vssLine}");
    }
}
