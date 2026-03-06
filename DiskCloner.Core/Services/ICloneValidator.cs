using DiskCloner.Core.Models;

namespace DiskCloner.Core.Services;

/// <summary>
/// Validates a <see cref="CloneOperation"/> before execution begins and provides
/// thread-safe mutation guards for target disk and target volume access.
/// </summary>
public interface ICloneValidator
{
    /// <summary>
    /// Validates the clone operation configuration ends calls CalculateTargetLayout.
    /// Throws <see cref="InvalidOperationException"/> on any validation failure.
    /// </summary>
    Task ValidateAsync(CloneOperation operation, CloneProgress progress);

    /// <summary>
    /// Asserts that <paramref name="diskNumber"/> is the target disk (not source).
    /// Throws if the disk is the source or does not match the target.
    /// </summary>
    void EnsureTargetDiskMutationAllowed(CloneOperation operation, int diskNumber, string operationName);

    /// <summary>
    /// Asserts that <paramref name="driveLetter"/> is not the source system volume.
    /// </summary>
    void EnsureTargetVolumeMutationAllowed(CloneOperation operation, char driveLetter, string operationName);

    /// <summary>
    /// Verifies that every "select disk N" command in a diskpart script targets only the target disk.
    /// </summary>
    void AssertDiskpartScriptTargetsOnlyTargetDisk(CloneOperation operation, string scriptContent, string operationName);
}
