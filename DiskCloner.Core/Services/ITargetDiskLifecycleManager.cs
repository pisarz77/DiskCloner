using DiskCloner.Core.Models;

namespace DiskCloner.Core.Services;

/// <summary>
/// Manages the lifecycle of the target disk directly before and after partition copying.
/// This includes taking the disk offline/online, expanding partitions, repairing filesystems,
/// and making the target disk bootable.
/// </summary>
public interface ITargetDiskLifecycleManager
{
    Task OfflineTargetDiskAsync(CloneOperation operation);
    Task OnlineTargetDiskAsync(CloneOperation operation);
    Task MarkTargetIncompleteAsync(CloneOperation operation);
    Task ExpandPartitionAsync(CloneOperation operation, CloneProgress progress);
    Task<BootFinalizationStatus> MakeBootableAsync(CloneOperation operation);
}
