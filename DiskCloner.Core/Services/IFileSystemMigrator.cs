using DiskCloner.Core.Models;

namespace DiskCloner.Core.Services;

/// <summary>
/// Migrates an NTFS file system from source to target using Robocopy when raw copy
/// cannot be used (e.g., when the target partition is smaller than the source).
/// </summary>
public interface IFileSystemMigrator
{
    /// <summary>
    /// Copies files from the source partition via Robocopy into the formatted target partition,
    /// optionally rebuilds boot files. Returns total bytes migrated.
    /// </summary>
    Task<long> MigrateAsync(
        CloneOperation operation,
        PartitionInfo partition,
        CloneResult result,
        CloneProgress progress,
        long totalBytesAlreadyCopied,
        long migrationPlannedBytes);
}
