using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DiskCloner.Core.Services;

/// <summary>
/// Copies partitions from source to target disk using raw or NTFS bitmap-guided smart copy.
/// </summary>
public class PartitionCopier : IPartitionCopier
{
    private readonly ILogger _logger;
    private readonly VssSnapshotService _vssService;
    private readonly Action<CloneProgress> _reportProgress;
    private readonly CancellationToken _cancellationToken;

    public PartitionCopier(
        ILogger logger,
        VssSnapshotService vssService,
        Action<CloneProgress> reportProgress,
        CancellationToken cancellationToken = default)
    {
        _logger = logger;
        _vssService = vssService;
        _reportProgress = reportProgress;
        _cancellationToken = cancellationToken;
    }

    // ── Strategy selection ───────────────────────────────────────────────────

    /// <inheritdoc />
    public CopyStrategy GetCopyStrategy(CloneOperation operation, PartitionInfo partition)
    {
        if (operation.SourceReadMode == SourceReadMode.SnapshotRawStrict)
            return CopyStrategy.RawBlock;

        bool isNtfs = partition.FileSystemType.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        bool isShrunk = partition.TargetSizeBytes > 0 && partition.TargetSizeBytes < partition.SizeBytes;
        bool isSystemNtfsShrink = operation.AllowSmallerTarget && partition.IsSystemPartition && isNtfs && isShrunk;

        if (isSystemNtfsShrink)
            return CopyStrategy.FileSystemMigration;

        bool useSmartCopy = operation.SmartCopy && isNtfs && partition.DriveLetter.HasValue;
        return useSmartCopy ? CopyStrategy.SmartBlock : CopyStrategy.RawBlock;
    }

    /// <inheritdoc />
    public long CalculatePlannedTotalBytes(CloneOperation operation)
    {
        long total = 0;
        foreach (var partition in operation.PartitionsToClone)
        {
            var strategy = GetCopyStrategy(operation, partition);
            total += strategy switch
            {
                CopyStrategy.FileSystemMigration => GetEstimatedMigrationBytes(partition),
                CopyStrategy.RawBlock => GetRawCopyLengthBytes(partition),
                _ => partition.TargetSizeBytes
            };
        }
        return Math.Max(1, total);
    }

    public static long GetRawCopyLengthBytes(PartitionInfo partition)
    {
        if (partition.SizeBytes <= 0 || partition.TargetSizeBytes <= 0)
            return 0;
        return Math.Min(partition.SizeBytes, partition.TargetSizeBytes);
    }

    // ── Raw block copy ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<long> CopyRawAsync(CloneOperation operation, PartitionInfo partition, CloneProgress progress, long totalBytesAlreadyCopied)
    {
        var sourceRead = await ResolveSourceReadDescriptorAsync(operation, partition);
        _logger.Info(
            $"Copying partition {partition.PartitionNumber} ({partition.SizeDisplay}) — " +
            $"mode: {(sourceRead.IsSnapshotBacked ? "SnapshotRawStrict" : "LiveRaw")}.");

        var sourcePath = sourceRead.SourcePath;
        var targetPath = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";
        var targetPartitionOffset = GetRequiredTargetStartingOffset(partition);

        var partitionBytesCopied = 0L;
        var totalBytesInPartition = GetRawCopyLengthBytes(partition);
        const int sectorSize = 512;

        var bufferSize = operation.IoBufferSize;
        bufferSize = ((bufferSize + sectorSize - 1) / sectorSize) * sectorSize;

        await Task.Run(() =>
        {
            using var sourceHandle = WindowsApi.CreateFile(
                sourcePath, WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (sourceHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open source disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            using var targetHandle = WindowsApi.CreateFile(
                targetPath, WindowsApi.GENERIC_READ_WRITE,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (targetHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open target disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            _logger.Info($"Target disk handle opened for partition {partition.PartitionNumber}");

            using var nativeBuffer = new NativeBuffer(bufferSize);

            var relativeOffset = 0L;
            var lastProgressUpdate = DateTime.UtcNow;
            var bytesSinceLastUpdate = 0L;

            while (partitionBytesCopied < totalBytesInPartition)
            {
                if (_cancellationToken.IsCancellationRequested)
                {
                    _logger.Warning("Copy operation cancelled");
                    throw new OperationCanceledException("Operation was cancelled by user");
                }

                var bytesRemaining = totalBytesInPartition - partitionBytesCopied;
                var bytesToRead = (int)Math.Min(bufferSize, bytesRemaining);
                bytesToRead = ((bytesToRead + sectorSize - 1) / sectorSize) * sectorSize;

                long absoluteSourceOffset = sourceRead.BaseOffset + relativeOffset;
                long absoluteTargetOffset = targetPartitionOffset + relativeOffset;

                if (!WindowsApi.SetFilePointerEx(sourceHandle, absoluteSourceOffset, out _, WindowsApi.FILE_BEGIN))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to seek source at {absoluteSourceOffset}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                }

                uint bytesRead;
                if (!WindowsApi.ReadFile(sourceHandle, nativeBuffer.Pointer, (uint)bytesToRead, out bytesRead, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to read from source at {absoluteSourceOffset}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                }

                if (bytesRead == 0)
                {
                    _logger.Warning($"Read 0 bytes at offset {absoluteSourceOffset}, stopping partition copy");
                    break;
                }

                uint bytesToWrite = ((bytesRead + (uint)sectorSize - 1) / (uint)sectorSize) * (uint)sectorSize;
                if (bytesToWrite > bytesRead)
                {
                    var pad = (int)(bytesToWrite - bytesRead);
                    try { nativeBuffer.Zero((int)bytesRead, pad); }
                    catch (OverflowException)
                    {
                        throw new IOException($"Buffer padding overflow preparing write of {bytesToWrite} bytes (read {bytesRead}).");
                    }
                }

                if (!WindowsApi.SetFilePointerEx(targetHandle, absoluteTargetOffset, out _, WindowsApi.FILE_BEGIN))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to seek target at {absoluteTargetOffset}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                }

                uint bytesWritten;
                if (!WindowsApi.WriteFile(targetHandle, nativeBuffer.Pointer, bytesToWrite, out bytesWritten, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to write to target at {absoluteTargetOffset}: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
                }

                partitionBytesCopied += bytesRead;
                relativeOffset += bytesRead;
                bytesSinceLastUpdate += bytesRead;

                if (DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromMilliseconds(250))
                {
                    var now = DateTime.UtcNow;
                    var elapsedSec = (now - lastProgressUpdate).TotalSeconds;
                    var currentTotalCopied = totalBytesAlreadyCopied + partitionBytesCopied;
                    progress.BytesCopied = currentTotalCopied;
                    progress.ThroughputBytesPerSec = elapsedSec > 0 ? bytesSinceLastUpdate / elapsedSec : 0;
                    progress.PercentComplete = (currentTotalCopied * 100.0) / progress.TotalBytes;
                    progress.EstimatedTimeRemaining = CalculateSafeEta(
                        Math.Max(0L, progress.TotalBytes - progress.BytesCopied),
                        progress.ThroughputBytesPerSec);
                    _reportProgress(progress);
                    lastProgressUpdate = now;
                    bytesSinceLastUpdate = 0;
                }
            }

            WindowsApi.FlushFileBuffers(targetHandle);
            _logger.Info($"Copied {partitionBytesCopied:N0} bytes for partition {partition.PartitionNumber}");
        }, _cancellationToken);

        return totalBytesAlreadyCopied + partitionBytesCopied;
    }

    // ── Smart copy (NTFS bitmap) ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<long> CopySmartAsync(CloneOperation operation, PartitionInfo partition, CloneProgress progress, long totalBytesAlreadyCopied)
    {
        _logger.Info($"Smart-copying partition {partition.PartitionNumber} ({partition.SizeDisplay}) using NTFS bitmap");

        var sourcePath = $@"\\.\PhysicalDrive{operation.SourceDisk.DiskNumber}";
        var targetPath = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";
        var sourcePartitionOffset = partition.StartingOffset;
        var targetPartitionOffset = GetRequiredTargetStartingOffset(partition);
        var bitmapVolumePath = $@"\\.\{partition.DriveLetter!.Value}:";

        var partitionBytesCopied = 0L;
        const int sectorSize = 512;
        var bufferSize = operation.IoBufferSize;
        bufferSize = ((bufferSize + sectorSize - 1) / sectorSize) * sectorSize;

        return await Task.Run(async () =>
        {
            // Step 1: Read NTFS bitmap
            _logger.Info($"Reading NTFS bitmap from {bitmapVolumePath}");

            using var volumeHandle = WindowsApi.CreateFile(
                bitmapVolumePath, WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (volumeHandle.IsInvalid)
            {
                return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                    $"Cannot open volume {bitmapVolumePath} for bitmap read ({Marshal.GetLastWin32Error()})");
            }

            var bootSector = new byte[512];
            uint bootRead;
            if (!WindowsApi.ReadFile(volumeHandle, bootSector, 512, out bootRead, IntPtr.Zero) || bootRead < 512)
            {
                return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                    "Failed to read NTFS boot sector");
            }

            int bytesPerSector = BitConverter.ToUInt16(bootSector, 0x0B);
            int sectorsPerCluster = bootSector[0x0D];
            long bytesPerCluster = (long)bytesPerSector * sectorsPerCluster;

            if (bytesPerCluster <= 0 || bytesPerCluster > 64 * 1024 * 1024)
            {
                return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                    $"Unexpected NTFS cluster size {bytesPerCluster}");
            }

            _logger.Info($"NTFS cluster size: {bytesPerCluster} bytes");

            // Fetch full volume bitmap in chunks
            long totalClusters = -1;
            byte[]? bitmap = null;
            long startingLcn = 0;

            while (true)
            {
                var inputBuffer = BitConverter.GetBytes(startingLcn);
                var outputBuffer = new byte[65536 + 16];
                uint bytesReturned;

                bool ok = WindowsApi.DeviceIoControl(
                    volumeHandle, WindowsApi.FSCTL_GET_VOLUME_BITMAP,
                    inputBuffer, inputBuffer.Length,
                    outputBuffer, outputBuffer.Length,
                    out bytesReturned, IntPtr.Zero);

                int lastErr = Marshal.GetLastWin32Error();
                if (!ok && lastErr == 87)
                    ok = TryGetVolumeBitmapUnmanaged(volumeHandle, startingLcn, outputBuffer, out bytesReturned, out lastErr);

                if (!ok && lastErr != 234)
                {
                    return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                        $"FSCTL_GET_VOLUME_BITMAP failed ({lastErr}) on {bitmapVolumePath} (startLCN={startingLcn})");
                }

                if (bytesReturned < 16)
                {
                    return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                        $"FSCTL_GET_VOLUME_BITMAP returned too little data ({bytesReturned} bytes)");
                }

                long chunkStartLcn = BitConverter.ToInt64(outputBuffer, 0);
                long chunkBitmapSize = BitConverter.ToInt64(outputBuffer, 8);
                if (chunkBitmapSize <= 0)
                {
                    return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                        $"FSCTL_GET_VOLUME_BITMAP returned invalid bitmap size ({chunkBitmapSize})");
                }

                long chunkEndCluster;
                try { chunkEndCluster = checked(chunkStartLcn + chunkBitmapSize); }
                catch (OverflowException)
                {
                    return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                        $"Bitmap chunk overflow (start={chunkStartLcn}, size={chunkBitmapSize})");
                }

                if (totalClusters < chunkEndCluster) totalClusters = chunkEndCluster;

                if (bitmap == null)
                {
                    var totalBitmapBytesLong = (totalClusters + 7) / 8;
                    if (totalBitmapBytesLong <= 0 || totalBitmapBytesLong > int.MaxValue)
                    {
                        return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                            $"Volume bitmap size is unsupported ({totalBitmapBytesLong} bytes)");
                    }
                    bitmap = new byte[(int)totalBitmapBytesLong];
                }

                int payloadBytes = (int)bytesReturned - 16;
                if (payloadBytes > 0)
                {
                    long destBitOffset = chunkStartLcn;
                    if (destBitOffset < 0 || destBitOffset >= totalClusters)
                    {
                        return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                            $"Bitmap chunk offset out of range (chunkStartLCN={chunkStartLcn})");
                    }

                    if ((destBitOffset & 7) == 0)
                    {
                        int destByteOffset = (int)(destBitOffset / 8);
                        int toCopy = Math.Min(payloadBytes, bitmap.Length - destByteOffset);
                        Array.Copy(outputBuffer, 16, bitmap, destByteOffset, toCopy);
                    }
                    else
                    {
                        long bitsAvailable = (long)payloadBytes * 8;
                        long bitsUntilEnd = totalClusters - destBitOffset;
                        long bitsToCopy = Math.Min(bitsAvailable, bitsUntilEnd);
                        for (long bit = 0; bit < bitsToCopy; bit++)
                        {
                            int srcByte = 16 + (int)(bit / 8), srcBit = (int)(bit % 8);
                            if ((outputBuffer[srcByte] & (1 << srcBit)) == 0) continue;
                            long destBit = destBitOffset + bit;
                            bitmap[(int)(destBit / 8)] |= (byte)(1 << (int)(destBit % 8));
                        }
                    }

                    long chunkClustersReturned = (long)payloadBytes * 8;
                    startingLcn = chunkStartLcn + chunkClustersReturned;
                }

                if (ok) break;
                if (startingLcn >= totalClusters) break;
            }

            if (bitmap == null)
                return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                    "FSCTL_GET_VOLUME_BITMAP returned no bitmap data");

            if (totalClusters <= 0)
                return await FallbackToRawCopyOrThrowAsync(operation, partition, progress, totalBytesAlreadyCopied,
                    $"Invalid total cluster count ({totalClusters})");

            // Find last used cluster
            long lastUsedCluster = 0;
            for (long lcn = totalClusters - 1; lcn >= 0; lcn--)
            {
                if ((bitmap[(int)(lcn / 8)] & (1 << (int)(lcn % 8))) != 0)
                {
                    lastUsedCluster = lcn;
                    break;
                }
            }

            long lastUsedByteOffset = (lastUsedCluster + 1) * bytesPerCluster;
            _logger.Info($"Last used cluster: {lastUsedCluster}, last used byte offset: {lastUsedByteOffset:N0}");

            if (lastUsedByteOffset > partition.TargetSizeBytes)
                throw new InvalidOperationException(
                    $"Cannot fit data onto target partition: data extends to {lastUsedByteOffset / (1024 * 1024 * 1024.0):F1} GB " +
                    $"but target is only {partition.TargetSizeBytes / (1024 * 1024 * 1024.0):F1} GB.");

            long maxClustersByUsage = lastUsedCluster + 1;
            long maxClustersByTarget = (partition.TargetSizeBytes + bytesPerCluster - 1) / bytesPerCluster;
            long maxClustersToProcess = Math.Min(totalClusters, Math.Min(maxClustersByUsage, maxClustersByTarget));

            // Count allocated clusters for accurate progress
            long allocatedClusterCount = 0;
            for (long lcn = 0; lcn < maxClustersToProcess; lcn++)
                if ((bitmap[(int)(lcn / 8)] & (1 << (int)(lcn % 8))) != 0)
                    allocatedClusterCount++;

            long allocatedBytesToCopy = allocatedClusterCount * bytesPerCluster;
            progress.TotalBytes = totalBytesAlreadyCopied + allocatedBytesToCopy;

            // Step 2: Open disk handles and copy cluster by cluster
            using var sourceHandle = WindowsApi.CreateFile(
                sourcePath, WindowsApi.GENERIC_READ,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (sourceHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open source disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            using var targetHandle = WindowsApi.CreateFile(
                targetPath, WindowsApi.GENERIC_READ_WRITE,
                WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (targetHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to open target disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            int clusterRoundedBuffer = (int)Math.Max(bufferSize, bytesPerCluster);
            clusterRoundedBuffer = (int)(((long)clusterRoundedBuffer + bytesPerCluster - 1) / bytesPerCluster * bytesPerCluster);
            clusterRoundedBuffer = ((clusterRoundedBuffer + sectorSize - 1) / sectorSize) * sectorSize;

            using var nativeBuffer = new NativeBuffer(clusterRoundedBuffer);

            var lastProgressUpdate = DateTime.UtcNow;
            var bytesSinceLastUpdate = 0L;
            long clusterLcn = 0;

            while (clusterLcn < maxClustersToProcess)
            {
                if (_cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Operation was cancelled by user");

                bool isAllocated = (bitmap[(int)(clusterLcn / 8)] & (1 << (int)(clusterLcn % 8))) != 0;

                long runStart = clusterLcn;
                while (clusterLcn < maxClustersToProcess)
                {
                    bool a = (bitmap[(int)(clusterLcn / 8)] & (1 << (int)(clusterLcn % 8))) != 0;
                    if (a != isAllocated) break;
                    clusterLcn++;
                    if ((clusterLcn - runStart) * bytesPerCluster >= clusterRoundedBuffer) break;
                }

                long runSourceByteOffset = sourcePartitionOffset + runStart * bytesPerCluster;
                long runTargetByteOffset = targetPartitionOffset + runStart * bytesPerCluster;
                long runByteLength = (clusterLcn - runStart) * bytesPerCluster;

                if (runTargetByteOffset >= targetPartitionOffset + partition.TargetSizeBytes) break;
                if (runTargetByteOffset + runByteLength > targetPartitionOffset + partition.TargetSizeBytes)
                    runByteLength = (targetPartitionOffset + partition.TargetSizeBytes) - runTargetByteOffset;

                uint toProcess = (uint)(((runByteLength + sectorSize - 1) / sectorSize) * sectorSize);

                if (!WindowsApi.SetFilePointerEx(targetHandle, runTargetByteOffset, out _, WindowsApi.FILE_BEGIN))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException($"Failed to seek target at {runTargetByteOffset}: {WindowsApi.GetErrorMessage((uint)error)}");
                }

                if (isAllocated)
                {
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

                if (DateTime.UtcNow - lastProgressUpdate > TimeSpan.FromMilliseconds(250))
                {
                    var now = DateTime.UtcNow;
                    var elapsedSec = (now - lastProgressUpdate).TotalSeconds;
                    var currentTotalCopied = totalBytesAlreadyCopied + partitionBytesCopied;
                    progress.BytesCopied = currentTotalCopied;
                    progress.ThroughputBytesPerSec = elapsedSec > 0 ? bytesSinceLastUpdate / elapsedSec : 0;
                    progress.PercentComplete = Math.Min(100.0, (currentTotalCopied * 100.0) / progress.TotalBytes);
                    progress.EstimatedTimeRemaining = CalculateSafeEta(
                        Math.Max(0L, progress.TotalBytes - progress.BytesCopied),
                        progress.ThroughputBytesPerSec);
                    _reportProgress(progress);
                    lastProgressUpdate = now;
                    bytesSinceLastUpdate = 0;
                }
            }

            WindowsApi.FlushFileBuffers(targetHandle);
            _logger.Info($"Smart copy complete: {partitionBytesCopied:N0} bytes for partition {partition.PartitionNumber}");

            return totalBytesAlreadyCopied + partitionBytesCopied;
        }, _cancellationToken);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<long> FallbackToRawCopyOrThrowAsync(
        CloneOperation operation, PartitionInfo partition,
        CloneProgress progress, long totalBytesAlreadyCopied, string reason)
    {
        bool shrinkingThisPartition = operation.AllowSmallerTarget && partition.TargetSizeBytes < partition.SizeBytes;
        if (shrinkingThisPartition)
            throw new InvalidOperationException(
                $"{reason}. Smart copy required when shrinking partitions but NTFS bitmap could not be read. " +
                "Retry with Smart Copy enabled and the source volume healthy, or clone to equal/larger target.");

        _logger.Warning($"{reason}, falling back to raw copy");
        return await CopyRawAsync(operation, partition, progress, totalBytesAlreadyCopied);
    }

    private async Task<SourceReadDescriptor> ResolveSourceReadDescriptorAsync(
        CloneOperation operation, PartitionInfo partition)
    {
        if (operation.SourceReadMode != SourceReadMode.SnapshotRawStrict)
        {
            return new SourceReadDescriptor
            {
                SourcePath = $@"\\.\PhysicalDrive{operation.SourceDisk.DiskNumber}",
                BaseOffset = partition.StartingOffset,
                IsSnapshotBacked = false
            };
        }

        if (partition.IsSystemPartition && partition.DriveLetter.HasValue)
        {
            var sourceRoot = $"{char.ToUpperInvariant(partition.DriveLetter.Value)}:\\";
            var snapshotPath = _vssService.GetSnapshotVolumePath(sourceRoot);
            if (string.IsNullOrWhiteSpace(snapshotPath))
                throw new InvalidOperationException($"Snapshot strict mode requested, but no snapshot path for {sourceRoot}.");

            var exposedPath = await _vssService.ExposeSnapshotVolumeAsync(sourceRoot);
            if (string.IsNullOrWhiteSpace(exposedPath))
                throw new InvalidOperationException($"Snapshot strict mode: snapshot for {sourceRoot} could not be exposed for raw reads.");

            return new SourceReadDescriptor
            {
                SourcePath = $@"\\.\{char.ToUpperInvariant(exposedPath[0])}:",
                BaseOffset = 0,
                IsSnapshotBacked = true
            };
        }

        return new SourceReadDescriptor
        {
            SourcePath = $@"\\.\PhysicalDrive{operation.SourceDisk.DiskNumber}",
            BaseOffset = partition.StartingOffset,
            IsSnapshotBacked = false
        };
    }

    public static long GetEstimatedMigrationBytes(PartitionInfo partition)
    {
        if (!partition.DriveLetter.HasValue)
            return partition.TargetSizeBytes;

        try
        {
            var drive = new DriveInfo($"{char.ToUpperInvariant(partition.DriveLetter.Value)}:\\");
            if (!drive.IsReady) return partition.TargetSizeBytes;
            long used = Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
            return used <= 0 ? partition.TargetSizeBytes : Math.Min(partition.TargetSizeBytes, used);
        }
        catch { return partition.TargetSizeBytes; }
    }

    private static long GetRequiredTargetStartingOffset(PartitionInfo partition)
    {
        if (partition.TargetStartingOffset <= 0)
            throw new InvalidOperationException(
                $"Target partition offset is not initialized for source partition {partition.PartitionNumber}.");
        return partition.TargetStartingOffset;
    }

    private static bool TryGetVolumeBitmapUnmanaged(
        SafeFileHandle volumeHandle, long startingLcn,
        byte[] outputBuffer, out uint bytesReturned, out int lastError)
    {
        bytesReturned = 0;
        using var inBuf = new NativeBuffer(sizeof(long));
        using var outBuf = new NativeBuffer(outputBuffer.Length);

        Marshal.WriteInt64(inBuf.Pointer, startingLcn);

        var ok = WindowsApi.DeviceIoControl(
            volumeHandle, WindowsApi.FSCTL_GET_VOLUME_BITMAP,
            inBuf.Pointer, sizeof(long),
            outBuf.Pointer, outputBuffer.Length,
            out bytesReturned, IntPtr.Zero);

        lastError = Marshal.GetLastWin32Error();
        if (ok || lastError == 234)
        {
            int toCopy = (int)Math.Min(bytesReturned, (uint)outputBuffer.Length);
            if (toCopy > 0) Marshal.Copy(outBuf.Pointer, outputBuffer, 0, toCopy);
        }

        return ok;
    }

    public static TimeSpan CalculateSafeEta(long remainingBytes, double throughputBytesPerSec)
    {
        if (remainingBytes <= 0) return TimeSpan.Zero;
        if (throughputBytesPerSec <= 0 || double.IsNaN(throughputBytesPerSec) || double.IsInfinity(throughputBytesPerSec))
            return TimeSpan.Zero;

        var seconds = remainingBytes / throughputBytesPerSec;
        if (seconds <= 0 || double.IsNaN(seconds)) return TimeSpan.Zero;

        var maxSeconds = TimeSpan.MaxValue.TotalSeconds - 1;
        if (double.IsInfinity(seconds) || seconds >= maxSeconds) return TimeSpan.MaxValue;

        return TimeSpan.FromSeconds(seconds);
    }
}
