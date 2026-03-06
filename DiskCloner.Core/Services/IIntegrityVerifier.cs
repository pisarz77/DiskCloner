using DiskCloner.Core.Models;

namespace DiskCloner.Core.Services;

/// <summary>
/// Verifies that copied partition data matches the source via SHA-256 hash comparison.
/// </summary>
public interface IIntegrityVerifier
{
    /// <summary>
    /// Verifies integrity of all cloned partitions (skipping any in excludedPartitionNumbers).
    /// Returns true if all checked partitions match.
    /// </summary>
    Task<bool> VerifyAsync(
        CloneOperation operation,
        CloneProgress progress,
        IReadOnlyCollection<int>? excludedPartitionNumbers = null);

    /// <summary>
    /// Builds the set of partition numbers that should be skipped during verification
    /// (e.g. EFI when VSS is in use, or migrated file-system partitions).
    /// </summary>
    IReadOnlyCollection<int> BuildExclusions(
        CloneOperation operation,
        IReadOnlyCollection<int> migratedPartitionNumbers,
        CloneResult result);
}
