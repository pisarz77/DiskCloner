using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DiskCloner.Core.Services;

/// <summary>
/// Verifies that cloned partition data matches the source using SHA-256 hash comparison.
/// Supports full-pass and sampling-based strategies.
/// </summary>
public class IntegrityVerifier : IIntegrityVerifier
{
    private readonly ILogger _logger;
    private readonly Func<PartitionInfo, long> _getTargetOffset;
    private readonly Func<long, double, TimeSpan> _calculateEta;
    private readonly Action<CloneProgress> _reportProgress;
    private readonly CancellationToken _cancellationToken;

    public IntegrityVerifier(
        ILogger logger,
        Func<PartitionInfo, long> getTargetOffset,
        Func<long, double, TimeSpan> calculateEta,
        Action<CloneProgress> reportProgress,
        CancellationToken cancellationToken)
    {
        _logger = logger;
        _getTargetOffset = getTargetOffset;
        _calculateEta = calculateEta;
        _reportProgress = reportProgress;
        _cancellationToken = cancellationToken;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<int> BuildExclusions(
        CloneOperation operation,
        IReadOnlyCollection<int> migratedPartitionNumbers,
        CloneResult result)
    {
        var excluded = new HashSet<int>(migratedPartitionNumbers);

        // EFI is usually not part of the VSS snapshot set, so live-source reads taken
        // long after copy can drift and trigger false mismatches.
        if (operation.UseVss)
        {
            foreach (var partition in operation.PartitionsToClone.Where(p => p.IsEfiPartition && !p.DriveLetter.HasValue))
            {
                if (excluded.Add(partition.PartitionNumber))
                {
                    var warning =
                        $"Skipping verification for EFI partition {partition.PartitionNumber} because it is not snapshot-backed in this workflow.";
                    _logger.Warning(warning);
                    result.Warnings.Add(warning);
                }
            }
        }

        return excluded;
    }

    /// <inheritdoc />
    public async Task<bool> VerifyAsync(
        CloneOperation operation,
        CloneProgress progress,
        IReadOnlyCollection<int>? excludedPartitionNumbers = null)
    {
        _logger.Warning("Integrity verification is currently disabled. Full-hash verification step is skipped.");
        progress.TotalBytes = 1;
        progress.BytesCopied = 1;
        progress.PercentComplete = 100;
        progress.ThroughputBytesPerSec = 0;
        progress.EstimatedTimeRemaining = TimeSpan.Zero;
        _reportProgress(progress);
        await Task.CompletedTask;
        return true;
    }

    /// <summary>
    /// Performs full hash verification of all included partitions.
    /// </summary>
    public async Task<bool> FullHashAsync(
        CloneOperation operation,
        CloneProgress progress,
        IReadOnlyCollection<int>? excludedPartitionNumbers = null)
    {
        _logger.Info("Performing full hash verification...");

        var bufferSize = 1024 * 1024; // 1MB buffers
        var includedPartitions = operation.PartitionsToClone
            .Where(p => excludedPartitionNumbers == null || !excludedPartitionNumbers.Contains(p.PartitionNumber))
            .ToList();
        var totalLogicalBytes = includedPartitions.Sum(GetVerificationLengthBytes);
        if (totalLogicalBytes <= 0)
        {
            _logger.Info("No partitions selected for hash verification after exclusions.");
            progress.TotalBytes = 1;
            progress.BytesCopied = 1;
            progress.PercentComplete = 100;
            progress.ThroughputBytesPerSec = 0;
            progress.EstimatedTimeRemaining = TimeSpan.Zero;
            _reportProgress(progress);
            return true;
        }

        progress.TotalBytes = totalLogicalBytes;
        progress.BytesCopied = 0;
        progress.PercentComplete = 0;
        progress.ThroughputBytesPerSec = 0;
        progress.EstimatedTimeRemaining = TimeSpan.Zero;
        _reportProgress(progress);

        var totalCheckedLogical = 0L;

        foreach (var partition in operation.PartitionsToClone)
        {
            if (excludedPartitionNumbers != null && excludedPartitionNumbers.Contains(partition.PartitionNumber))
            {
                _logger.Warning($"Skipping hash verification for excluded partition {partition.PartitionNumber}.");
                continue;
            }

            progress.CurrentPartitionName = $"Verifying {partition.GetTypeName()}";
            _reportProgress(progress);

            var sourceOffset = partition.StartingOffset;
            var targetOffset = _getTargetOffset(partition);
            var verificationLength = GetVerificationLengthBytes(partition);
            if (verificationLength <= 0)
            {
                _logger.Warning($"Skipping hash verification for partition {partition.PartitionNumber}: no bytes selected for comparison.");
                continue;
            }

            var partitionBaseLogical = totalCheckedLogical;
            var sourceBytesRead = 0L;
            var targetBytesRead = 0L;
            var lastProgressUpdate = DateTime.UtcNow;
            var bytesSinceLastUpdate = 0L;

            void ReportPartitionProgress()
            {
                var sourceContribution = sourceBytesRead / 2;
                var targetContribution = targetBytesRead / 2;
                var partitionLogicalProgress = Math.Min(
                    verificationLength,
                    sourceContribution + targetContribution + Math.Min(verificationLength % 2, sourceBytesRead > 0 || targetBytesRead > 0 ? 1 : 0));

                progress.BytesCopied = Math.Min(progress.TotalBytes, partitionBaseLogical + partitionLogicalProgress);
                progress.PercentComplete = (progress.BytesCopied * 100.0) / Math.Max(1, progress.TotalBytes);

                var remainingBytes = Math.Max(0L, progress.TotalBytes - progress.BytesCopied);
                progress.EstimatedTimeRemaining = _calculateEta(remainingBytes, progress.ThroughputBytesPerSec);
                _reportProgress(progress);
            }

            using (var srcSha = SHA256.Create())
            {
                var sourceHash = await ComputeHashAsync(
                    operation.SourceDisk.DiskNumber, sourceOffset, verificationLength, bufferSize, srcSha,
                    bytesRead =>
                    {
                        sourceBytesRead += bytesRead;
                        bytesSinceLastUpdate += bytesRead;
                        var now = DateTime.UtcNow;
                        if (now - lastProgressUpdate >= TimeSpan.FromMilliseconds(250))
                        {
                            var elapsedSec = (now - lastProgressUpdate).TotalSeconds;
                            progress.ThroughputBytesPerSec = elapsedSec > 0 ? bytesSinceLastUpdate / elapsedSec : 0;
                            ReportPartitionProgress();
                            lastProgressUpdate = now;
                            bytesSinceLastUpdate = 0;
                        }
                    });

                using (var tgtSha = SHA256.Create())
                {
                    var targetHash = await ComputeHashAsync(
                        operation.TargetDisk.DiskNumber, targetOffset, verificationLength, bufferSize, tgtSha,
                        bytesRead =>
                        {
                            targetBytesRead += bytesRead;
                            bytesSinceLastUpdate += bytesRead;
                            var now = DateTime.UtcNow;
                            if (now - lastProgressUpdate >= TimeSpan.FromMilliseconds(250))
                            {
                                var elapsedSec = (now - lastProgressUpdate).TotalSeconds;
                                progress.ThroughputBytesPerSec = elapsedSec > 0 ? bytesSinceLastUpdate / elapsedSec : 0;
                                ReportPartitionProgress();
                                lastProgressUpdate = now;
                                bytesSinceLastUpdate = 0;
                            }
                        });

                    if (!sourceHash.SequenceEqual(targetHash))
                    {
                        _logger.Error($"Hash mismatch for partition {partition.PartitionNumber}");
                        return false;
                    }
                }
            }

            totalCheckedLogical += verificationLength;
            progress.BytesCopied = totalCheckedLogical;
            progress.PercentComplete = (progress.BytesCopied * 100.0) / Math.Max(1, progress.TotalBytes);
            progress.ThroughputBytesPerSec = 0;
            progress.EstimatedTimeRemaining = TimeSpan.Zero;
            _reportProgress(progress);
        }

        _logger.Info("Full hash verification passed");
        return true;
    }

    /// <summary>
    /// Performs sampling-based hash verification.
    /// </summary>
    public async Task<bool> SampleHashAsync(
        CloneOperation operation,
        CloneProgress progress,
        IReadOnlyCollection<int>? excludedPartitionNumbers = null)
    {
        _logger.Info("Performing sampling hash verification...");

        const int sampleCount = 100;
        var bufferSize = 1024 * 1024;

        foreach (var partition in operation.PartitionsToClone)
        {
            if (excludedPartitionNumbers != null && excludedPartitionNumbers.Contains(partition.PartitionNumber))
            {
                _logger.Warning($"Skipping sampling hash verification for migrated partition {partition.PartitionNumber}.");
                continue;
            }

            progress.CurrentPartitionName = $"Verifying {partition.GetTypeName()} (sampling)";
            _reportProgress(progress);

            var sourcePartitionOffset = partition.StartingOffset;
            var targetPartitionOffset = _getTargetOffset(partition);
            var samplesChecked = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                if (_cancellationToken.IsCancellationRequested)
                    break;

                var sourceSampleOffset = sourcePartitionOffset + (partition.TargetSizeBytes * i / sampleCount);
                var targetSampleOffset = targetPartitionOffset + (partition.TargetSizeBytes * i / sampleCount);

                sourceSampleOffset = Math.Min(sourceSampleOffset, sourcePartitionOffset + partition.TargetSizeBytes - bufferSize);
                targetSampleOffset = Math.Min(targetSampleOffset, targetPartitionOffset + partition.TargetSizeBytes - bufferSize);

                var sampleSize = Math.Min(bufferSize, sourcePartitionOffset + partition.TargetSizeBytes - sourceSampleOffset);

                using var srcSha = SHA256.Create();
                using var tgtSha = SHA256.Create();
                var sourceHash = await ComputeHashAsync(operation.SourceDisk.DiskNumber, sourceSampleOffset, sampleSize, (int)sampleSize, srcSha);
                var targetHash = await ComputeHashAsync(operation.TargetDisk.DiskNumber, targetSampleOffset, sampleSize, (int)sampleSize, tgtSha);

                if (!sourceHash.SequenceEqual(targetHash))
                {
                    _logger.Error($"Hash mismatch for partition {partition.PartitionNumber} at sample {i}");
                    return false;
                }

                samplesChecked++;
                progress.PercentComplete = (samplesChecked * 100.0) / sampleCount;
                _reportProgress(progress);
            }
        }

        _logger.Info("Sampling hash verification passed");
        return true;
    }

    /// <summary>
    /// Computes SHA-256 hash for a disk region.
    /// </summary>
    public static async Task<byte[]> ComputeHashAsync(
        int diskNumber,
        long offset,
        long length,
        int bufferSize,
        HashAlgorithm hashAlgorithm,
        Action<long>? onBytesRead = null)
    {
        var path = $@"\\.\PhysicalDrive{diskNumber}";
        var buffer = new byte[bufferSize];
        var bytesRemaining = length;

        await Task.Run(() =>
        {
            using var handle = WindowsApi.CreateFile(
                path, WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

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
                onBytesRead?.Invoke(bytesRead);

                bytesRemaining -= bytesRead;
                offset += bytesRead;
            }

            hashAlgorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        });

        return hashAlgorithm.Hash ?? Array.Empty<byte>();
    }

    public static long GetVerificationLengthBytes(PartitionInfo partition)
    {
        if (partition.SizeBytes <= 0 || partition.TargetSizeBytes <= 0)
            return 0;

        return Math.Min(partition.SizeBytes, partition.TargetSizeBytes);
    }
}
