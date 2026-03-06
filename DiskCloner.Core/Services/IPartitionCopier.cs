using DiskCloner.Core.Models;

namespace DiskCloner.Core.Services;

/// <summary>
/// Copies partitions from source to target disk using raw or NTFS bitmap-guided smart copy.
/// </summary>
public interface IPartitionCopier
{
    /// <summary>
    /// Copies a single partition using the raw block strategy (sector-by-sector).
    /// Returns total bytes copied including the <paramref name="totalBytesAlreadyCopied"/> offset.
    /// </summary>
    Task<long> CopyRawAsync(CloneOperation operation, PartitionInfo partition, CloneProgress progress, long totalBytesAlreadyCopied);

    /// <summary>
    /// Copies only the allocated NTFS clusters using the volume bitmap (smart copy).
    /// Falls back to raw copy if the bitmap cannot be read and the partition is not shrunk.
    /// </summary>
    Task<long> CopySmartAsync(CloneOperation operation, PartitionInfo partition, CloneProgress progress, long totalBytesAlreadyCopied);

    /// <summary>
    /// Chooses between RawBlock, SmartBlock, or FileSystemMigration based on operation settings
    /// and partition characteristics.
    /// </summary>
    CopyStrategy GetCopyStrategy(CloneOperation operation, PartitionInfo partition);

    /// <summary>
    /// Returns the number of bytes that will be read/written during a raw copy
    /// (minimum of source and target sizes).
    /// </summary>
    static long GetRawCopyLengthBytes(PartitionInfo partition)
        => PartitionCopier.GetRawCopyLengthBytes(partition);

    /// <summary>
    /// Returns the total planned bytes across all partitions for progress/ETA calculations.
    /// </summary>
    long CalculatePlannedTotalBytes(CloneOperation operation);
}
