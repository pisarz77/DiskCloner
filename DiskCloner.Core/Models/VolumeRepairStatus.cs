namespace DiskCloner.Core.Models;

/// <summary>
/// Holds the outcome of a volume repair (chkdsk) operation.
/// </summary>
public sealed class VolumeRepairStatus
{
    public bool ScanDetectedIssues { get; set; }
    public bool DirtyBeforeFix { get; set; }
    public bool FixApplied { get; set; }
    public bool DirtyAfterRepair { get; set; }
    public string Summary { get; set; } = string.Empty;
}
