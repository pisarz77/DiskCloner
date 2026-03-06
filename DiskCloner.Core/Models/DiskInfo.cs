using DiskCloner.Core.Utilities;
using System.Text;

namespace DiskCloner.Core.Models;

/// <summary>
/// Represents a physical disk in the system.
/// </summary>
public class DiskInfo
{
    /// <summary>
    /// The physical disk number (e.g., 0 for \\.\PhysicalDrive0).
    /// </summary>
    public int DiskNumber { get; set; }

    /// <summary>
    /// The friendly name/model of the disk.
    /// </summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// The unique disk signature or GUID.
    /// </summary>
    public string DiskId { get; set; } = string.Empty;

    /// <summary>
    /// The total size of the disk in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// The number of sectors on the disk.
    /// </summary>
    public long TotalSectors { get; set; }

    /// <summary>
    /// The logical (reported) sector size in bytes.
    /// </summary>
    public int LogicalSectorSize { get; set; }

    /// <summary>
    /// The physical sector size in bytes.
    /// </summary>
    public int PhysicalSectorSize { get; set; }

    /// <summary>
    /// Whether this is a GPT disk (true) or MBR disk (false).
    /// </summary>
    public bool IsGpt { get; set; }

    /// <summary>
    /// Whether this disk is the current system disk (boot disk).
    /// </summary>
    public bool IsSystemDisk { get; set; }

    /// <summary>
    /// Whether this disk is online and available.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// Whether this disk is read-only.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Whether this disk is a removable USB drive.
    /// </summary>
    public bool IsRemovable { get; set; }

    /// <summary>
    /// The bus type (e.g., USB, SATA, NVMe).
    /// </summary>
    public string BusType { get; set; } = "Unknown";

    /// <summary>
    /// Partitions on this disk.
    /// </summary>
    public List<PartitionInfo> Partitions { get; set; } = new();

    /// <summary>
    /// Formatted size string for display.
    /// </summary>
    public string SizeDisplay => ByteFormatter.Format(SizeBytes);

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Disk {DiskNumber}: {FriendlyName}");
        sb.AppendLine($"  Size: {SizeDisplay} ({TotalSectors:N0} sectors)");
        sb.AppendLine($"  Sector Size: Logical={LogicalSectorSize}, Physical={PhysicalSectorSize}");
        sb.AppendLine($"  Type: {(IsGpt ? "GPT" : "MBR")}");
        sb.AppendLine($"  Bus: {BusType}");
        sb.AppendLine($"  Status: {(IsOnline ? "Online" : "Offline")}{(IsSystemDisk ? " [SYSTEM]" : "")}{(IsRemovable ? " [Removable]" : "")}");
        sb.AppendLine($"  Partitions: {Partitions.Count}");
        return sb.ToString();
    }

}
