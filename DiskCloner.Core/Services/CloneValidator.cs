using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskCloner.Core.Services;

/// <summary>
/// Validates clone operations and enforces target disk mutation guards.
/// </summary>
public class CloneValidator : ICloneValidator
{
    private readonly ILogger _logger;
    private readonly DiskEnumerator _diskEnumerator;
    private readonly VssSnapshotService _vssService;
    public CloneValidator(
        ILogger logger,
        DiskEnumerator diskEnumerator,
        VssSnapshotService vssService)
    {
        _logger = logger;
        _diskEnumerator = diskEnumerator;
        _vssService = vssService;
    }

    /// <inheritdoc />
    public async Task ValidateAsync(CloneOperation operation, CloneProgress progress, Action<CloneProgress> reportProgress)
    {
        _logger.Info("Validating cloning operation...");

        if (operation.SourceDisk == null)
            throw new InvalidOperationException("Source disk not specified");

        if (!operation.SourceDisk.IsSystemDisk)
            throw new InvalidOperationException("Source disk must be the system disk");

        if (operation.TargetDisk == null)
            throw new InvalidOperationException("Target disk not specified");

        if (operation.TargetDisk.DiskNumber == operation.SourceDisk.DiskNumber)
            throw new InvalidOperationException("Source and target cannot be the same disk");

        if (operation.TargetDisk.IsSystemDisk)
            throw new InvalidOperationException("Target disk cannot be the system disk");

        if (operation.TargetDisk.IsReadOnly)
            throw new InvalidOperationException("Target disk is read-only");

        if (operation.PartitionsToClone.Count == 0)
            throw new InvalidOperationException("No partitions selected for cloning");

        // Calculate target layout (partition sizes for destination)
        CalculateTargetLayout(operation);

        var hasEfi = operation.PartitionsToClone.Any(p => p.IsEfiPartition);
        var hasSystem = operation.PartitionsToClone.Any(p => p.IsSystemPartition);

        if (!hasEfi && operation.SourceDisk.IsGpt)
            throw new InvalidOperationException("EFI partition must be included for GPT disks");

        if (!hasSystem)
            throw new InvalidOperationException("System partition must be included");

        if (operation.SourceReadMode == SourceReadMode.SnapshotRawStrict)
        {
            if (!operation.UseVss)
                throw new InvalidOperationException("Source read mode 'VSS snapshot (raw, strict)' requires VSS to be enabled.");

            if (operation.AllowSmallerTarget)
                throw new InvalidOperationException("Source read mode 'VSS snapshot (raw, strict)' is not compatible with 'Allow smaller target disk'.");
        }

        // BitLocker warnings
        foreach (var partition in operation.PartitionsToClone)
        {
            if (!partition.DriveLetter.HasValue)
                continue;

            var driveLetter = partition.DriveLetter.Value;
            var isEncrypted = await _vssService.IsVolumeBitLockerEncrypted(driveLetter);
            if (isEncrypted)
            {
                _logger.Warning($"Partition {driveLetter}: is BitLocker encrypted");
                progress.StatusMessage = $"Warning: Drive {driveLetter}: has BitLocker enabled";
                reportProgress(progress);
            }
        }

        // Disk access checks
        var sourceAccessible = await _diskEnumerator.ValidateDiskAccessAsync(operation.SourceDisk.DiskNumber);
        if (!sourceAccessible)
            throw new InvalidOperationException($"Cannot access source disk {operation.SourceDisk.DiskNumber}");

        var targetAccessible = await _diskEnumerator.ValidateDiskAccessAsync(operation.TargetDisk.DiskNumber);
        if (!targetAccessible)
            throw new InvalidOperationException($"Cannot access target disk {operation.TargetDisk.DiskNumber}");

        _logger.Info("Validation passed");
    }

    /// <inheritdoc />
    public void CalculateTargetLayout(CloneOperation operation)
    {
        const long OneMiB = 1024L * 1024L;
        long totalSpaceRequired = OneMiB;
        foreach (var p in operation.PartitionsToClone)
        {
            totalSpaceRequired += p.SizeBytes;
            totalSpaceRequired += OneMiB;
        }

        var targetIsSmallerThanRequired = operation.TargetDisk.SizeBytes < totalSpaceRequired;
        if (targetIsSmallerThanRequired && !operation.AllowSmallerTarget)
        {
            throw new InvalidOperationException($"Target disk is too small. Required: {DiskCloner.Core.Utilities.ByteFormatter.Format(totalSpaceRequired)}. Enable 'Allow smaller target' to auto-shrink.");
        }

        foreach (var partition in operation.PartitionsToClone)
        {
            partition.TargetSizeBytes = partition.SizeBytes;
        }

        var systemPartition = operation.PartitionsToClone.OrderBy(p => p.StartingOffset).FirstOrDefault(p => p.IsSystemPartition);
        if (systemPartition == null) return;

        if (targetIsSmallerThanRequired)
        {
            foreach (var partition in operation.PartitionsToClone)
            {
                if (!partition.IsSystemPartition)
                    partition.TargetSizeBytes = Math.Max(OneMiB, (partition.SizeBytes / OneMiB) * OneMiB);
            }

            var maxSystemBytes = CalculateMaximumSystemPartitionBytes(operation, systemPartition);
            if (maxSystemBytes < (5L * 1024 * 1024 * 1024))
                throw new InvalidOperationException($"Target disk is too small even with shrinking. Only {DiskCloner.Core.Utilities.ByteFormatter.Format(maxSystemBytes)} available for Windows.");

            systemPartition.TargetSizeBytes = maxSystemBytes;
            _logger.Info($"Layout planning: shrinking Windows partition to {DiskCloner.Core.Utilities.ByteFormatter.Format(maxSystemBytes)} to fit target.");
            return;
        }

        if (operation.AutoExpandWindowsPartition)
        {
            var maxSystemBytes = CalculateMaximumSystemPartitionBytes(operation, systemPartition);
            if (maxSystemBytes > systemPartition.SizeBytes)
            {
                systemPartition.TargetSizeBytes = maxSystemBytes;
                _logger.Info($"Layout planning: pre-expanding Windows partition to {DiskCloner.Core.Utilities.ByteFormatter.Format(maxSystemBytes)} while reserving space for remaining partitions.");
            }
        }
    }

    private long CalculateMaximumSystemPartitionBytes(CloneOperation operation, PartitionInfo systemPartition)
    {
        const long OneMiB = 1024L * 1024L;
        var totalDiskMb = operation.TargetDisk.SizeBytes / OneMiB;
        if (totalDiskMb <= 0) return 0;

        long nonSystemMb = 0;
        foreach (var partition in operation.PartitionsToClone.Where(p => !ReferenceEquals(p, systemPartition)))
        {
            nonSystemMb += Math.Max(1, (partition.TargetSizeBytes + OneMiB - 1) / OneMiB);
        }

        var baseOverheadMb = operation.SourceDisk.IsGpt ? 8L : 1L;
        var implicitMsrMb = operation.SourceDisk.IsGpt && !operation.PartitionsToClone.Any(p => p.IsMsrPartition) ? 16L : 0L;
        var alignmentSlackMb = operation.PartitionsToClone.Count + 2L;

        var systemMb = totalDiskMb - nonSystemMb - baseOverheadMb - implicitMsrMb - alignmentSlackMb;
        if (systemMb <= 0) return 0;

        return systemMb * OneMiB;
    }

    /// <inheritdoc />
    public void EnsureTargetDiskMutationAllowed(CloneOperation operation, int diskNumber, string operationName)
    {
        if (diskNumber != operation.TargetDisk.DiskNumber)
            throw new InvalidOperationException(
                $"Unsafe disk mutation blocked for '{operationName}': disk {diskNumber} is not target disk {operation.TargetDisk.DiskNumber}.");

        if (diskNumber == operation.SourceDisk.DiskNumber)
            throw new InvalidOperationException(
                $"Unsafe disk mutation blocked for '{operationName}': attempted to mutate source disk {operation.SourceDisk.DiskNumber}.");
    }

    /// <inheritdoc />
    public void EnsureTargetVolumeMutationAllowed(CloneOperation operation, char driveLetter, string operationName)
    {
        var normalized = char.ToUpperInvariant(driveLetter);
        var sourceDrive = operation.PartitionsToClone.FirstOrDefault(p => p.IsSystemPartition)?.DriveLetter;

        if (sourceDrive.HasValue && normalized == char.ToUpperInvariant(sourceDrive.Value))
            throw new InvalidOperationException(
                $"Unsafe volume mutation blocked for '{operationName}': drive {normalized}: is the source system volume.");
    }

    /// <inheritdoc />
    public void AssertDiskpartScriptTargetsOnlyTargetDisk(CloneOperation operation, string scriptContent, string operationName)
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
}
