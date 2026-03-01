using System;
using DiskCloner.Core.Models;
using Xunit;

namespace DiskCloner.Tests.Models;

public class CloneOperationTests
{
    [Fact]
    public void CloneOperation_DefaultConstructor_InitializesProperties()
    {
        // Arrange & Act
        var operation = new CloneOperation();

        // Assert
        Assert.NotNull(operation.PartitionsToClone);
        Assert.Empty(operation.PartitionsToClone);
        Assert.True(operation.UseVss);
        Assert.Equal(64 * 1024 * 1024, operation.IoBufferSize); // 64MB default
        Assert.True(operation.VerifyIntegrity);
        Assert.False(operation.FullHashVerification);
        Assert.True(operation.AutoExpandWindowsPartition);
        Assert.False(operation.AllowSmallerTarget);
        Assert.Equal(string.Empty, operation.LogFilePath);
        Assert.NotEqual(Guid.Empty, operation.OperationId);
    }

    [Fact]
    public void CloneOperation_SetProperties_CorrectlyStoresValues()
    {
        // Arrange
        var sourceDisk = new DiskInfo { DiskNumber = 0 };
        var targetDisk = new DiskInfo { DiskNumber = 1 };
        var partitions = new List<PartitionInfo>
        {
            new PartitionInfo { PartitionNumber = 1, SizeBytes = 1000000000 },
            new PartitionInfo { PartitionNumber = 2, SizeBytes = 500000000 }
        };

        // Act
        var operation = new CloneOperation
        {
            SourceDisk = sourceDisk,
            TargetDisk = targetDisk,
            PartitionsToClone = partitions,
            UseVss = false,
            IoBufferSize = 32 * 1024 * 1024, // 32MB
            VerifyIntegrity = false,
            FullHashVerification = true,
            AutoExpandWindowsPartition = false,
            AllowSmallerTarget = true,
            LogFilePath = @"C:\logs\clone.log"
        };

        // Assert
        Assert.Equal(sourceDisk, operation.SourceDisk);
        Assert.Equal(targetDisk, operation.TargetDisk);
        Assert.Equal(partitions, operation.PartitionsToClone);
        Assert.False(operation.UseVss);
        Assert.Equal(32 * 1024 * 1024, operation.IoBufferSize);
        Assert.False(operation.VerifyIntegrity);
        Assert.True(operation.FullHashVerification);
        Assert.False(operation.AutoExpandWindowsPartition);
        Assert.True(operation.AllowSmallerTarget);
        Assert.Equal(@"C:\logs\clone.log", operation.LogFilePath);
    }

    [Fact]
    public void CloneOperation_OperationId_IsUnique()
    {
        // Arrange & Act
        var operation1 = new CloneOperation();
        var operation2 = new CloneOperation();

        // Assert
        Assert.NotEqual(operation1.OperationId, operation2.OperationId);
    }

    [Fact]
    public void CloneOperation_PartitionsToClone_CollectionIsModifiable()
    {
        // Arrange
        var operation = new CloneOperation();
        var partition = new PartitionInfo { PartitionNumber = 1, SizeBytes = 100000000 };

        // Act
        operation.PartitionsToClone.Add(partition);

        // Assert
        Assert.Single(operation.PartitionsToClone);
        Assert.Equal(partition, operation.PartitionsToClone[0]);
    }
}

public class CloneProgressTests
{
    [Fact]
    public void CloneProgress_DefaultConstructor_InitializesProperties()
    {
        // Arrange & Act
        var progress = new CloneProgress();

        // Assert
        Assert.Equal(CloneStage.NotStarted, progress.Stage);
        Assert.Equal(-1, progress.CurrentPartition);
        Assert.Equal(0, progress.TotalPartitions);
        Assert.Equal(string.Empty, progress.CurrentPartitionName);
        Assert.Equal(0, progress.PercentComplete);
        Assert.Equal(0, progress.BytesCopied);
        Assert.Equal(0, progress.TotalBytes);
        Assert.Equal(0, progress.ThroughputBytesPerSec);
        Assert.Equal(TimeSpan.Zero, progress.EstimatedTimeRemaining);
        Assert.Equal(string.Empty, progress.StatusMessage);
        Assert.False(progress.IsCancelled);
        Assert.Null(progress.LastError);
    }

    [Fact]
    public void CloneProgress_SetProperties_CorrectlyStoresValues()
    {
        // Arrange
        var progress = new CloneProgress
        {
            Stage = CloneStage.CopyingData,
            CurrentPartition = 1,
            TotalPartitions = 4,
            CurrentPartitionName = "Windows (C:)",
            PercentComplete = 25.5,
            BytesCopied = 5000000000,
            TotalBytes = 20000000000,
            ThroughputBytesPerSec = 100000000,
            EstimatedTimeRemaining = TimeSpan.FromMinutes(5),
            StatusMessage = "Copying partition 2 of 4...",
            IsCancelled = false,
            LastError = "Test error message"
        };

        // Assert
        Assert.Equal(CloneStage.CopyingData, progress.Stage);
        Assert.Equal(1, progress.CurrentPartition);
        Assert.Equal(4, progress.TotalPartitions);
        Assert.Equal("Windows (C:)", progress.CurrentPartitionName);
        Assert.Equal(25.5, progress.PercentComplete);
        Assert.Equal(5000000000, progress.BytesCopied);
        Assert.Equal(20000000000, progress.TotalBytes);
        Assert.Equal(100000000, progress.ThroughputBytesPerSec);
        Assert.Equal(TimeSpan.FromMinutes(5), progress.EstimatedTimeRemaining);
        Assert.Equal("Copying partition 2 of 4...", progress.StatusMessage);
        Assert.False(progress.IsCancelled);
        Assert.Equal("Test error message", progress.LastError);
    }

    [Fact]
    public void CloneProgress_FormattedProperties()
    {
        // Arrange
        var progress = new CloneProgress
        {
            BytesCopied = 1500000000,
            TotalBytes = 5000000000,
            ThroughputBytesPerSec = 125000000
        };

        // Act & Assert
        Assert.Equal("1.39 GB", progress.BytesCopiedDisplay);
        Assert.Equal("4.65 GB", progress.TotalBytesDisplay);
        Assert.Equal("116.41 MB/s", progress.ThroughputDisplay);
    }

    [Fact]
    public void CloneProgress_IsCancelledFlag()
    {
        // Arrange
        var progress = new CloneProgress();

        // Act
        progress.IsCancelled = true;

        // Assert
        Assert.True(progress.IsCancelled);
    }

    [Fact]
    public void CloneProgress_LastErrorHandling()
    {
        // Arrange
        var progress = new CloneProgress();

        // Act
        progress.LastError = "Disk access error";

        // Assert
        Assert.Equal("Disk access error", progress.LastError);
    }
}

public class CloneResultTests
{
    [Fact]
    public void CloneResult_DefaultConstructor_InitializesProperties()
    {
        // Arrange & Act
        var result = new CloneResult();

        // Assert
        Assert.False(result.Success);
        Assert.False(result.IsBootable);
        Assert.Equal(string.Empty, result.BootMode);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.Exception);
        Assert.Equal(0, result.BytesCopied);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Equal(0, result.AverageThroughputBytesPerSec);
        Assert.False(result.IntegrityVerified);
        Assert.False(result.TargetMarkedIncomplete);
        Assert.NotNull(result.NextSteps);
        Assert.Empty(result.NextSteps);
    }

    [Fact]
    public void CloneResult_SetProperties_CorrectlyStoresValues()
    {
        // Arrange
        var exception = new Exception("Test exception");

        // Act
        var result = new CloneResult
        {
            Success = true,
            IsBootable = true,
            BootMode = "UEFI",
            ErrorMessage = "Operation completed successfully",
            Exception = exception,
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
        Assert.Equal(exception, result.Exception);
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
    public void CloneResult_NextSteps_CollectionIsModifiable()
    {
        // Arrange
        var result = new CloneResult();
        var step = "Test step";

        // Act
        result.NextSteps.Add(step);

        // Assert
        Assert.Single(result.NextSteps);
        Assert.Equal(step, result.NextSteps[0]);
    }

    [Fact]
    public void CloneResult_ExceptionHandling()
    {
        // Arrange
        var exception = new IOException("Disk not found");

        // Act
        var result = new CloneResult
        {
            Success = false,
            Exception = exception,
            ErrorMessage = "Failed to access target disk"
        };

        // Assert
        Assert.False(result.Success);
        Assert.Equal(exception, result.Exception);
        Assert.Equal("Failed to access target disk", result.ErrorMessage);
    }
}

public class CloneStageTests
{
    [Fact]
    public void CloneStage_Values_AreSequential()
    {
        // Arrange & Act & Assert
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
    public void CloneStage_EnumNames_AreCorrect()
    {
        // Arrange
        var expectedNames = new[]
        {
            "NotStarted", "Validating", "CreatingSnapshots", "PreparingTarget",
            "CopyingData", "Verifying", "ExpandingPartitions", "Cleanup",
            "Completed", "Failed", "Cancelled"
        };

        // Act
        var actualNames = Enum.GetNames<CloneStage>();

        // Assert
        Assert.Equal(expectedNames, actualNames);
    }
}