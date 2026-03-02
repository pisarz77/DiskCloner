using System.Text;

namespace DiskCloner.Core.Models;

/// <summary>
/// Represents a partition on a disk.
/// </summary>
public class PartitionInfo
{
    /// <summary>
    /// The partition number (1-based index on the disk).
    /// </summary>
    public int PartitionNumber { get; set; }

    /// <summary>
    /// The starting offset in bytes from the beginning of the disk.
    /// </summary>
    public long StartingOffset { get; set; }

    /// <summary>
    /// The size of the partition in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// The size it will have on the target disk (may be smaller than SizeBytes for auto-shrink).
    /// </summary>
    public long TargetSizeBytes { get; set; }

    /// <summary>
    /// The starting offset on the target disk after partition table creation.
    /// This may differ from StartingOffset when cloning to a smaller disk.
    /// </summary>
    public long TargetStartingOffset { get; set; }

    /// <summary>
    /// The target partition number after partition table creation.
    /// </summary>
    public int TargetPartitionNumber { get; set; }

    /// <summary>
    /// For GPT: The partition type GUID.
    /// </summary>
    public Guid? PartitionTypeGuid { get; set; }

    /// <summary>
    /// For GPT: The unique partition ID.
    /// </summary>
    public Guid? UniqueId { get; set; }

    /// <summary>
    /// For MBR: The partition type indicator byte.
    /// </summary>
    public byte? MbrPartitionType { get; set; }

    /// <summary>
    /// Whether this partition is active/bootable (MBR only).
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// For GPT: Partition attributes flags.
    /// </summary>
    public ulong GptAttributes { get; set; }

    /// <summary>
    /// For GPT: Partition name (up to 36 UTF-16 characters).
    /// </summary>
    public string PartitionName { get; set; } = string.Empty;

    /// <summary>
    /// The assigned drive letter (if mounted).
    /// </summary>
    public char? DriveLetter { get; set; }

    /// <summary>
    /// The volume GUID (if formatted and mounted).
    /// </summary>
    public string? VolumeGuid { get; set; }

    /// <summary>
    /// File system type (NTFS, FAT32, etc.).
    /// </summary>
    public string FileSystemType { get; set; } = string.Empty;

    /// <summary>
    /// Volume label (if present).
    /// </summary>
    public string VolumeLabel { get; set; } = string.Empty;

    /// <summary>
    /// Whether this partition is the Windows system/boot partition (C:).
    /// </summary>
    public bool IsSystemPartition { get; set; }

    /// <summary>
    /// Whether this is the EFI System Partition (ESP).
    /// </summary>
    public bool IsEfiPartition { get; set; }

    /// <summary>
    /// Whether this is the Microsoft Reserved Partition (MSR).
    /// </summary>
    public bool IsMsrPartition { get; set; }

    /// <summary>
    /// Whether this is a recovery partition.
    /// </summary>
    public bool IsRecoveryPartition { get; set; }

    /// <summary>
    /// Whether this is required for boot (EFI or system partition).
    /// </summary>
    public bool IsBootRequired => IsEfiPartition || IsSystemPartition;

    /// <summary>
    /// Whether this is protected/hidden.
    /// </summary>
    public bool IsHidden => IsRecoveryPartition || IsMsrPartition;

    /// <summary>
    /// Formatted size string for display.
    /// </summary>
    public string SizeDisplay => FormatBytes(SizeBytes);

    /// <summary>
    /// Starting sector (calculated from offset and typical sector size).
    /// </summary>
    public long StartingSector => StartingOffset / 512; // Approximate

    /// <summary>
    /// Number of sectors in the partition.
    /// </summary>
    public long Sectors => SizeBytes / 512; // Approximate

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"Partition {PartitionNumber}: ");
        if (DriveLetter.HasValue)
            sb.Append($"Drive {DriveLetter.Value}: ");
        sb.Append($"{SizeDisplay}");
        if (!string.IsNullOrEmpty(FileSystemType))
            sb.Append($" ({FileSystemType})");
        if (IsEfiPartition)
            sb.Append(" [EFI]");
        if (IsSystemPartition)
            sb.Append(" [SYSTEM]");
        if (IsMsrPartition)
            sb.Append(" [MSR]");
        if (IsRecoveryPartition)
            sb.Append(" [RECOVERY]");
        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
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
    /// Gets a descriptive type name for this partition.
    /// </summary>
    public string GetTypeName()
    {
        if (IsEfiPartition) return "EFI System Partition";
        if (IsSystemPartition) return "Windows/System";
        if (IsMsrPartition) return "Microsoft Reserved";
        if (IsRecoveryPartition) return "Recovery";
        if (PartitionTypeGuid.HasValue)
        {
            var guid = PartitionTypeGuid.Value;
            if (guid == Guid.Parse("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"))
                return "EFI System Partition";
            if (guid == Guid.Parse("E3C9E316-0B5C-4DB8-817D-F92DF00215AE"))
                return "Microsoft Reserved";
            if (guid == Guid.Parse("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC"))
                return "Windows Recovery";
        }
        return "Data";
    }
}
