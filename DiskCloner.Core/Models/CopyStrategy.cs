namespace DiskCloner.Core.Models;

/// <summary>
/// Determines how a partition will be copied from source to target.
/// </summary>
public enum CopyStrategy
{
    /// <summary>Sector-by-sector raw block copy.</summary>
    RawBlock,

    /// <summary>NTFS allocation bitmap-guided copy (skips free clusters).</summary>
    SmartBlock,

    /// <summary>File system migration via Robocopy (used for shrunk NTFS partitions).</summary>
    FileSystemMigration
}
