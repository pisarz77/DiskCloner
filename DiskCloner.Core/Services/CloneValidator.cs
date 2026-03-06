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
    private readonly Action<CloneProgress> _reportProgress;

    // Injected layout calculator so CloneOperation target sizes are set before validation checks
    private readonly Action<CloneOperation> _calculateTargetLayout;

    public CloneValidator(
        ILogger logger,
        DiskEnumerator diskEnumerator,
        VssSnapshotService vssService,
        Action<CloneProgress> reportProgress,
        Action<CloneOperation> calculateTargetLayout)
    {
        _logger = logger;
        _diskEnumerator = diskEnumerator;
        _vssService = vssService;
        _reportProgress = reportProgress;
        _calculateTargetLayout = calculateTargetLayout;
    }

    /// <inheritdoc />
    public async Task ValidateAsync(CloneOperation operation, CloneProgress progress)
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
        _calculateTargetLayout(operation);

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
                _reportProgress(progress);
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
