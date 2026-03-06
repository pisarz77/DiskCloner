namespace DiskCloner.Core.Models;

/// <summary>
/// Describes how to read data from the source partition (raw offset or VSS snapshot path).
/// </summary>
internal sealed class SourceReadDescriptor
{
    public string SourcePath { get; set; } = string.Empty;
    public long BaseOffset { get; set; }
    public bool IsSnapshotBacked { get; set; }
}
