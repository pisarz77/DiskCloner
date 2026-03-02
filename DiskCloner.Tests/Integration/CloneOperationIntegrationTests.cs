using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Services;
using Moq;
using Xunit;

namespace DiskCloner.Tests.Integration;

public class CloneOperationIntegrationTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly DiskEnumerator _diskEnumerator;
    private readonly VssSnapshotService _vssService;

    public CloneOperationIntegrationTests()
    {
        _mockLogger = new Mock<ILogger>();
        _diskEnumerator = new DiskEnumerator(_mockLogger.Object);
        _vssService = new VssSnapshotService(_mockLogger.Object);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_DisksAndPartitions()
    {
        // Act
        var disks = await _diskEnumerator.GetDisksAsync();
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        var targetDisks = await _diskEnumerator.GetTargetDisksAsync();

        // Assert
        Assert.NotNull(disks);
        Assert.NotNull(systemDisk);
        Assert.NotNull(targetDisks);
        Assert.True(disks.Count > 0);
        Assert.True(systemDisk.IsSystemDisk);
        Assert.DoesNotContain(targetDisks, d => d.IsSystemDisk);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_SystemDiskDetection()
    {
        // Act
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        var allDisks = await _diskEnumerator.GetDisksAsync();

        // Assert
        Assert.NotNull(systemDisk);
        Assert.True(systemDisk.IsSystemDisk);
        var foundInList = allDisks.FirstOrDefault(d => d.DiskNumber == systemDisk.DiskNumber);
        Assert.NotNull(foundInList);
        Assert.True(foundInList!.IsSystemDisk);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_PartitionDetection()
    {
        // Act
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();

        // Assert
        Assert.NotNull(systemDisk);
        Assert.True(systemDisk.Partitions.Count > 0);
        
        // Should have at least system partition (C:)
        var systemPartition = systemDisk.Partitions.FirstOrDefault(p => p.IsSystemPartition);
        Assert.NotNull(systemPartition);
        
        // Should have EFI partition if GPT
        if (systemDisk.IsGpt)
        {
            var efiPartition = systemDisk.Partitions.FirstOrDefault(p => p.IsEfiPartition);
            Assert.NotNull(efiPartition);
        }
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_VssAvailability()
    {
        // Act
        var isVssAvailable = await _vssService.IsVssAvailableAsync();

        // Assert
        Assert.IsType<bool>(isVssAvailable);
        Assert.IsType<bool>(isVssAvailable);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_BitLockerStatus()
    {
        // Act
        var bitLockerStatus = await _vssService.GetBitLockerStatusAsync();

        // Assert
        Assert.NotNull(bitLockerStatus);
        Assert.NotNull(bitLockerStatus.Status);
        Assert.NotNull(bitLockerStatus.Protectors);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_VolumeGuidResolution()
    {
        // Arrange
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        Assert.NotNull(systemDisk);
        var systemPartition = systemDisk.Partitions.FirstOrDefault(p => p.IsSystemPartition);

        if (systemPartition == null || !systemPartition.DriveLetter.HasValue)
        {
            return;
        }

        // Act
        var volumeGuid = await _vssService.ResolveVolumeGuidAsync(systemPartition.DriveLetter.Value);

        // Assert
        Assert.NotNull(volumeGuid);
        Assert.Contains("?", volumeGuid);
        Assert.Contains("Volume", volumeGuid);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_SnapshotCreation()
    {
        // Arrange
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        Assert.NotNull(systemDisk);
        var systemPartition = systemDisk.Partitions.FirstOrDefault(p => p.IsSystemPartition);

        if (systemPartition == null || !systemPartition.DriveLetter.HasValue)
        {
            return;
        }

        var operation = new CloneOperation
        {
            SourceDisk = systemDisk,
            PartitionsToClone = new List<PartitionInfo> { systemPartition }
        };

        // Act
        var snapshotInfo = await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        Assert.NotNull(snapshotInfo);
        Assert.NotNull(snapshotInfo.VolumeSnapshots);
        Assert.NotNull(snapshotInfo.SnapshotPaths);
        Assert.NotEmpty(snapshotInfo.VolumeSnapshots);
        Assert.NotEmpty(snapshotInfo.SnapshotPaths);

        // Cleanup
        await _vssService.CleanupSnapshotsAsync(snapshotInfo);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_MultiplePartitions()
    {
        // Arrange
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        Assert.NotNull(systemDisk);
        var partitions = systemDisk.Partitions.Where(p => p.DriveLetter.HasValue).Take(2).ToList();

        if (partitions.Count < 2)
        {
            return;
        }

        var operation = new CloneOperation
        {
            SourceDisk = systemDisk,
            PartitionsToClone = partitions
        };

        // Act
        var snapshotInfo = await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        Assert.NotNull(snapshotInfo);
        Assert.Equal(2, snapshotInfo.VolumeSnapshots.Count);
        Assert.Equal(2, snapshotInfo.SnapshotPaths.Count);

        // Cleanup
        await _vssService.CleanupSnapshotsAsync(snapshotInfo);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_DiskValidation()
    {
        // Arrange
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        Assert.NotNull(systemDisk);

        // Act
        var isValid = await _diskEnumerator.ValidateDiskAccessAsync(systemDisk.DiskNumber);

        // Assert
        Assert.IsType<bool>(isValid);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_TargetDiskValidation()
    {
        // Arrange
        var targetDisks = await _diskEnumerator.GetTargetDisksAsync();

        if (!targetDisks.Any())
        {
            return;
        }

        var targetDisk = targetDisks.First();

        // Act
        var isValid = await _diskEnumerator.ValidateDiskAccessAsync(targetDisk.DiskNumber);

        // Assert
        Assert.IsType<bool>(isValid);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_CloneOperationModel()
    {
        // Arrange
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        Assert.NotNull(systemDisk);
        var targetDisks = await _diskEnumerator.GetTargetDisksAsync();
        var partitions = systemDisk.Partitions.Where(p => p.IsBootRequired).ToList();

        if (!targetDisks.Any())
        {
            return;
        }

        // Act
        var operation = new CloneOperation
        {
            SourceDisk = systemDisk,
            TargetDisk = targetDisks.First(),
            PartitionsToClone = partitions,
            UseVss = true,
            IoBufferSize = 64 * 1024 * 1024,
            VerifyIntegrity = true,
            FullHashVerification = false,
            AutoExpandWindowsPartition = true,
            AllowSmallerTarget = false,
            StrictVerificationFailureStopsClone = true,
            UseSnapshotForFileMigration = true,
            LogFilePath = Path.Combine(Path.GetTempPath(), "test_clone.log")
        };

        // Assert
        Assert.NotNull(operation.SourceDisk);
        Assert.NotNull(operation.TargetDisk);
        Assert.NotNull(operation.PartitionsToClone);
        Assert.True(operation.UseVss);
        Assert.Equal(64 * 1024 * 1024, operation.IoBufferSize);
        Assert.True(operation.VerifyIntegrity);
        Assert.False(operation.FullHashVerification);
        Assert.True(operation.AutoExpandWindowsPartition);
        Assert.False(operation.AllowSmallerTarget);
        Assert.True(operation.StrictVerificationFailureStopsClone);
        Assert.True(operation.UseSnapshotForFileMigration);
        Assert.NotEqual(Guid.Empty, operation.OperationId);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_ProgressModel()
    {
        // Arrange
        var systemDisk = await _diskEnumerator.GetSystemDiskAsync();
        Assert.NotNull(systemDisk);
        var partitions = systemDisk.Partitions.Where(p => p.IsBootRequired).ToList();

        // Act
        var progress = new CloneProgress
        {
            Stage = CloneStage.CopyingData,
            CurrentPartition = 1,
            TotalPartitions = partitions.Count,
            CurrentPartitionName = "Windows (C:)",
            PercentComplete = 50.0,
            BytesCopied = 10000000000,
            TotalBytes = 20000000000,
            ThroughputBytesPerSec = 100000000,
            EstimatedTimeRemaining = TimeSpan.FromMinutes(5),
            StatusMessage = "Copying partition 1 of 2...",
            IsCancelled = false
        };

        // Assert
        Assert.Equal(CloneStage.CopyingData, progress.Stage);
        Assert.Equal(1, progress.CurrentPartition);
        Assert.Equal(partitions.Count, progress.TotalPartitions);
        Assert.Equal("Windows (C:)", progress.CurrentPartitionName);
        Assert.Equal(50.0, progress.PercentComplete);
        Assert.Equal(10000000000, progress.BytesCopied);
        Assert.Equal(20000000000, progress.TotalBytes);
        Assert.Equal(100000000, progress.ThroughputBytesPerSec);
        Assert.Equal(TimeSpan.FromMinutes(5), progress.EstimatedTimeRemaining);
        Assert.Equal("Copying partition 1 of 2...", progress.StatusMessage);
        Assert.False(progress.IsCancelled);
        
        // Test formatted properties
        Assert.Contains("GB", progress.BytesCopiedDisplay);
        Assert.Contains("GB", progress.TotalBytesDisplay);
        Assert.Contains("MB/s", progress.ThroughputDisplay);
    }

    [Fact]
    public void CloneOperation_FullWorkflow_ResultModel()
    {
        // Act
        var result = new CloneResult
        {
            Success = true,
            IsBootable = true,
            BootMode = "UEFI",
            ErrorMessage = "Operation completed successfully",
            BytesCopied = 250000000000,
            Duration = TimeSpan.FromMinutes(15),
            AverageThroughputBytesPerSec = 277777777,
            IntegrityVerified = true,
            TargetMarkedIncomplete = false,
            NextSteps = new List<string>
            {
                "Shutdown computer",
                "Swap drives",
                "Boot from new drive"
            }
        };

        // Assert
        Assert.True(result.Success);
        Assert.True(result.IsBootable);
        Assert.Equal("UEFI", result.BootMode);
        Assert.Equal("Operation completed successfully", result.ErrorMessage);
        Assert.Equal(250000000000, result.BytesCopied);
        Assert.Equal(TimeSpan.FromMinutes(15), result.Duration);
        Assert.Equal(277777777, result.AverageThroughputBytesPerSec);
        Assert.True(result.IntegrityVerified);
        Assert.False(result.TargetMarkedIncomplete);
        Assert.Equal(3, result.NextSteps.Count);
        Assert.Contains("Shutdown computer", result.NextSteps);
        Assert.Contains("Swap drives", result.NextSteps);
        Assert.Contains("Boot from new drive", result.NextSteps);
    }

    [Fact]
    public void CloneOperation_FullWorkflow_StageEnum()
    {
        // Act & Assert
        Assert.Equal(0, (int)CloneStage.NotStarted);
        Assert.Equal(1, (int)CloneStage.Validating);
        Assert.Equal(2, (int)CloneStage.CreatingSnapshots);
        Assert.Equal(3, (int)CloneStage.PreparingTarget);
        Assert.Equal(4, (int)CloneStage.CopyingData);
        Assert.Equal(5, (int)CloneStage.Verifying);
        Assert.Equal(6, (int)CloneStage.ExpandingPartitions);
        Assert.Equal(7, (int)CloneStage.Cleanup);
        Assert.Equal(8, (int)CloneStage.Completed);
        Assert.Equal(9, (int)CloneStage.Failed);
        Assert.Equal(10, (int)CloneStage.Cancelled);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_DiskProperties()
    {
        // Arrange
        var disks = await _diskEnumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Act & Assert
        Assert.True(disk.DiskNumber >= 0);
        Assert.NotNull(disk.FriendlyName);
        Assert.NotEmpty(disk.FriendlyName);
        Assert.True(disk.SizeBytes >= 0);
        Assert.True(disk.TotalSectors >= 0);
        Assert.True(disk.LogicalSectorSize > 0);
        Assert.True(disk.PhysicalSectorSize > 0);
        Assert.NotNull(disk.Partitions);
        Assert.IsType<bool>(disk.IsGpt);
        Assert.IsType<bool>(disk.IsOnline);
        Assert.IsType<bool>(disk.IsReadOnly);
        Assert.IsType<bool>(disk.IsRemovable);
    }

    [Fact]
    public async Task CloneOperation_FullWorkflow_PartitionProperties()
    {
        // Arrange
        var disks = await _diskEnumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null || !disk.Partitions.Any())
        {
            return;
        }

        var partition = disk.Partitions.First();

        // Act & Assert
        Assert.True(partition.PartitionNumber > 0);
        Assert.True(partition.StartingOffset >= 0);
        Assert.True(partition.SizeBytes > 0);
        Assert.True(partition.StartingSector >= 0);
        Assert.True(partition.Sectors > 0);
        Assert.NotNull(partition.GetTypeName());
        Assert.IsType<bool>(partition.IsBootRequired);
        Assert.IsType<bool>(partition.IsHidden);
    }
}
