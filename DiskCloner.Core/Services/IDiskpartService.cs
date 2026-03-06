using DiskCloner.Core.Models;

namespace DiskCloner.Core.Services;

/// <summary>
/// Encapsulates all interactions with diskpart.exe and target-disk partition management.
/// </summary>
public interface IDiskpartService
{
    /// <summary>
    /// Clears the first 1 MB of the target disk to erase any existing partition table.
    /// </summary>
    Task ClearTargetDiskAsync(CloneOperation operation);

    /// <summary>
    /// Creates a partition table on the target disk matching the source partition layout.
    /// </summary>
    Task CreatePartitionTableAsync(CloneOperation operation);

    /// <summary>
    /// Queries the partition layout of the target disk after diskpart creation,
    /// populates TargetStartingOffset and TargetPartitionNumber on each source partition.
    /// </summary>
    Task ApplyTargetPartitionOffsetsAsync(CloneOperation operation);

    /// <summary>
    /// Returns the starting byte offset of the target partition that corresponds to this source partition.
    /// Throws if the offset has not been set (i.e. ApplyTargetPartitionOffsetsAsync was not called).
    /// </summary>
    long GetRequiredTargetStartingOffset(PartitionInfo partition);

    /// <summary>
    /// Converts a partition size in bytes to the megabyte count accepted by diskpart "create partition … size=N".
    /// Rounds up to the nearest MB, minimum 1 MB.
    /// </summary>
    static long GetDiskPartSizeMegabytes(long sizeBytes) => DiskpartService.GetDiskPartSizeMegabytes(sizeBytes);

    /// <summary>
    /// Parses the JSON output of <c>Get-Partition | ConvertTo-Json</c> into a partition list.
    /// </summary>
    static List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)> ParseTargetPartitionLayoutJson(string json)
        => DiskpartService.ParseTargetPartitionLayoutJson(json);
}
