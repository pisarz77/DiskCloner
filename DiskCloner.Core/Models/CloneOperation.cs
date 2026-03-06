using DiskCloner.Core.Utilities;

namespace DiskCloner.Core.Models;

/// <summary>
/// Represents a disk cloning operation configuration and state.
/// </summary>
public class CloneOperation
{
    /// <summary>
    /// The source disk to clone from.
    /// </summary>
    public DiskInfo SourceDisk { get; set; } = null!;

    /// <summary>
    /// The target disk to clone to.
    /// </summary>
    public DiskInfo TargetDisk { get; set; } = null!;

    /// <summary>
    /// Partitions to clone from source.
    /// </summary>
    public List<PartitionInfo> PartitionsToClone { get; set; } = new();

    /// <summary>
    /// Whether to use Volume Shadow Copy Service for consistent snapshots.
    /// </summary>
    public bool UseVss { get; set; } = true;

    /// <summary>
    /// Source read mode used during partition data copy.
    /// </summary>
    public SourceReadMode SourceReadMode { get; set; } = SourceReadMode.LivePreferred;

    /// <summary>
    /// Buffer size for disk I/O operations in bytes.
    /// </summary>
    public int IoBufferSize { get; set; } = 64 * 1024 * 1024; // 64MB default

    /// <summary>
    /// Whether to verify copied data integrity using hashes.
    /// </summary>
    public bool VerifyIntegrity { get; set; } = true;

    /// <summary>
    /// Whether to automatically expand the Windows partition after cloning.
    /// </summary>
    public bool AutoExpandWindowsPartition { get; set; } = true;

    /// <summary>
    /// Whether to allow cloning to a smaller target disk.
    /// </summary>
    public bool AllowSmallerTarget { get; set; } = false;

    /// <summary>
    /// Whether to use smart (bitmap-guided) copy that skips unallocated sectors.
    /// Faster than raw copy and required for reliable auto-shrink to smaller targets.
    /// Only applies to NTFS partitions; other partition types always use raw copy.
    /// </summary>
    public bool SmartCopy { get; set; } = false;

    /// <summary>
    /// Whether to pause selected background writers on the source system
    /// before snapshot/copy to improve live-clone consistency.
    /// </summary>
    public bool EnableQuietMode { get; set; } = true;

    /// <summary>
    /// Whether verification mismatch should fail the clone operation.
    /// </summary>
    public bool StrictVerificationFailureStopsClone { get; set; } = true;

    /// <summary>
    /// Whether file-system migration for smaller target should read from VSS snapshots.
    /// </summary>
    public bool UseSnapshotForFileMigration { get; set; } = true;

    /// <summary>
    /// Path to the log file.
    /// </summary>
    public string LogFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Operation ID for tracking.
    /// </summary>
    public Guid OperationId { get; set; } = Guid.NewGuid();
}

public enum SourceReadMode
{
    LivePreferred = 0,
    SnapshotRawStrict = 1
}

/// <summary>
/// Progress information for a cloning operation.
/// </summary>
public class CloneProgress
{
    /// <summary>
    /// The current operation stage.
    /// </summary>
    public CloneStage Stage { get; set; }

    /// <summary>
    /// Current partition being cloned (0-indexed, or -1 for partition table).
    /// </summary>
    public int CurrentPartition { get; set; } = -1;

    /// <summary>
    /// Total number of partitions to clone.
    /// </summary>
    public int TotalPartitions { get; set; }

    /// <summary>
    /// Current partition name being processed.
    /// </summary>
    public string CurrentPartitionName { get; set; } = string.Empty;

    /// <summary>
    /// Overall progress percentage (0-100).
    /// </summary>
    public double PercentComplete { get; set; }

    /// <summary>
    /// Bytes copied so far.
    /// </summary>
    public long BytesCopied { get; set; }

    /// <summary>
    /// Total bytes to copy.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// Current throughput in bytes per second.
    /// </summary>
    public double ThroughputBytesPerSec { get; set; }

    /// <summary>
    /// Estimated remaining time in seconds.
    /// </summary>
    public TimeSpan EstimatedTimeRemaining { get; set; }

    /// <summary>
    /// Current status message.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// Whether the operation has been cancelled.
    /// </summary>
    public bool IsCancelled { get; set; }

    /// <summary>
    /// Last error message, if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Formatted bytes copied.
    /// </summary>
    public string BytesCopiedDisplay => ByteFormatter.Format(BytesCopied);

    /// <summary>
    /// Formatted total bytes.
    /// </summary>
    public string TotalBytesDisplay => ByteFormatter.Format(TotalBytes);

    /// <summary>
    /// Formatted throughput.
    /// </summary>
    public string ThroughputDisplay => ByteFormatter.Format((long)ThroughputBytesPerSec) + "/s";
}

/// <summary>
/// Stages of the cloning process.
/// </summary>
public enum CloneStage
{
    /// <summary>
    /// Operation not started.
    /// </summary>
    NotStarted,

    /// <summary>
    /// Validating source and target disks.
    /// </summary>
    Validating,

    /// <summary>
    /// Creating VSS snapshots.
    /// </summary>
    CreatingSnapshots,

    /// <summary>
    /// Preparing target disk (cleaning, writing partition table).
    /// </summary>
    PreparingTarget,

    /// <summary>
    /// Copying partition data.
    /// </summary>
    CopyingData,

    /// <summary>
    /// Verifying data integrity.
    /// </summary>
    Verifying,

    /// <summary>
    /// Expanding partitions on target.
    /// </summary>
    ExpandingPartitions,

    /// <summary>
    /// Cleaning up (removing snapshots, closing handles).
    /// </summary>
    Cleanup,

    /// <summary>
    /// Operation completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Operation failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Operation was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Result of a cloning operation.
/// </summary>
public class CloneResult
{
    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Whether the cloned disk should be bootable.
    /// </summary>
    public bool IsBootable { get; set; }

    /// <summary>
    /// Boot mode detected (UEFI or BIOS).
    /// </summary>
    public string BootMode { get; set; } = string.Empty;

    /// <summary>
    /// Error message if operation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Exception that caused the failure.
    /// </summary>
    public Exception? Exception { get; set; }

    /// <summary>
    /// Total bytes copied.
    /// </summary>
    public long BytesCopied { get; set; }

    /// <summary>
    /// Time taken for the operation.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Average throughput in bytes per second.
    /// </summary>
    public double AverageThroughputBytesPerSec { get; set; }

    /// <summary>
    /// Whether integrity verification passed.
    /// </summary>
    public bool IntegrityVerified { get; set; }

    /// <summary>
    /// Whether the target was marked as incomplete after cancellation.
    /// </summary>
    public bool TargetMarkedIncomplete { get; set; }

    /// <summary>
    /// Recommended next steps for the user.
    /// </summary>
    public List<string> NextSteps { get; set; } = new();

    /// <summary>
    /// Non-fatal warnings encountered during the operation.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// End-of-run health checks summary.
    /// </summary>
    public List<string> HealthChecks { get; set; } = new();
}
