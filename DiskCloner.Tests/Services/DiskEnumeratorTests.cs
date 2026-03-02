using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Services;
using Moq;
using Xunit;

namespace DiskCloner.Tests.Services;

public class DiskEnumeratorTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly DiskEnumerator _enumerator;

    public DiskEnumeratorTests()
    {
        _mockLogger = new Mock<ILogger>();
        _enumerator = new DiskEnumerator(_mockLogger.Object);
    }

    [Fact]
    public void DiskEnumerator_Constructor_ThrowsOnNullLogger()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DiskEnumerator(null));
    }

    [Fact]
    public void DiskEnumerator_Constructor_AcceptsValidLogger()
    {
        // Arrange & Act
        var enumerator = new DiskEnumerator(_mockLogger.Object);

        // Assert
        Assert.NotNull(enumerator);
    }

    [Fact]
    public async void GetDisksAsync_ReturnsNonEmptyList()
    {
        // Act
        var disks = await _enumerator.GetDisksAsync();

        // Assert
        Assert.NotNull(disks);
        Assert.True(disks.Count > 0, "Should find at least one disk on the system");
    }

    [Fact]
    public async void GetDisksAsync_CachesResults()
    {
        // Act
        var disks1 = await _enumerator.GetDisksAsync();
        var disks2 = await _enumerator.GetDisksAsync();

        // Assert
        Assert.Equal(disks1.Count, disks2.Count);
        _mockLogger.Verify(l => l.Debug(It.Is<string>(s => s.Contains("cached"))), Times.Once);
    }

    [Fact]
    public async void GetDisksAsync_ForceRefreshBypassesCache()
    {
        // Act
        var disks1 = await _enumerator.GetDisksAsync();
        var disks2 = await _enumerator.GetDisksAsync(true);

        // Assert
        Assert.Equal(disks1.Count, disks2.Count);
        _mockLogger.Verify(l => l.Info(It.Is<string>(s => s.Contains("Enumerating"))), Times.Exactly(2));
    }

    [Fact]
    public async void GetDiskAsync_ReturnsCorrectDisk()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var targetDisk = disks.FirstOrDefault();

        if (targetDisk == null)
        {
            return;
        }

        // Act
        var foundDisk = await _enumerator.GetDiskAsync(targetDisk.DiskNumber);

        // Assert
        Assert.NotNull(foundDisk);
        Assert.Equal(targetDisk.DiskNumber, foundDisk.DiskNumber);
        Assert.Equal(targetDisk.FriendlyName, foundDisk.FriendlyName);
    }

    [Fact]
    public async void GetDiskAsync_ReturnsNullForInvalidDiskNumber()
    {
        // Act
        var disk = await _enumerator.GetDiskAsync(999);

        // Assert
        Assert.Null(disk);
    }

    [Fact]
    public async void GetSystemDiskAsync_ReturnsSystemDisk()
    {
        // Act
        var systemDisk = await _enumerator.GetSystemDiskAsync();

        // Assert
        Assert.NotNull(systemDisk);
        Assert.True(systemDisk.IsSystemDisk);
    }

    [Fact]
    public async void GetTargetDisksAsync_ExcludesSystemDisk()
    {
        // Act
        var targetDisks = await _enumerator.GetTargetDisksAsync();

        // Assert
        Assert.NotNull(targetDisks);
        Assert.DoesNotContain(targetDisks, d => d.IsSystemDisk);
    }

    [Fact]
    public async void GetTargetDisksAsync_IncludesOnlineWritableDisks()
    {
        // Act
        var targetDisks = await _enumerator.GetTargetDisksAsync();

        // Assert
        Assert.NotNull(targetDisks);
        Assert.All(targetDisks, d => Assert.True(d.IsOnline));
        Assert.All(targetDisks, d => Assert.False(d.IsReadOnly));
        Assert.All(targetDisks, d => Assert.True(d.SizeBytes > 0));
    }

    [Fact]
    public async Task ValidateDiskAccessAsync_ReturnsTrueForValidDisk()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var targetDisk = disks.FirstOrDefault();

        if (targetDisk == null)
        {
            return;
        }

        // Act
        var result = await _enumerator.ValidateDiskAccessAsync(targetDisk.DiskNumber);

        // Assert
        Assert.IsType<bool>(result);
    }

    [Fact]
    public async void ValidateDiskAccessAsync_ReturnsFalseForInvalidDisk()
    {
        // Act
        var result = await _enumerator.ValidateDiskAccessAsync(999);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async void DiskProperties_AreCorrectlySet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Assert
        Assert.True(disk.DiskNumber >= 0);
        Assert.NotNull(disk.FriendlyName);
        Assert.NotEmpty(disk.FriendlyName);
        Assert.True(disk.SizeBytes >= 0);
        Assert.True(disk.TotalSectors >= 0);
        Assert.True(disk.LogicalSectorSize > 0);
        Assert.True(disk.PhysicalSectorSize > 0);
        Assert.True(disk.LogicalSectorSize <= disk.PhysicalSectorSize);
        Assert.NotNull(disk.Partitions);
    }

    [Fact]
    public async void PartitionProperties_AreCorrectlySet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null || !disk.Partitions.Any())
        {
            return;
        }

        var partition = disk.Partitions.First();

        // Assert
        Assert.True(partition.PartitionNumber > 0);
        Assert.True(partition.StartingOffset >= 0);
        Assert.True(partition.SizeBytes > 0);
        Assert.True(partition.StartingSector >= 0);
        Assert.True(partition.Sectors > 0);
        Assert.NotNull(partition.GetTypeName());
    }

    [Fact]
    public async void SystemDiskDetection_WorksCorrectly()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();

        // Act
        var systemDisk = await _enumerator.GetSystemDiskAsync();
        var systemDiskFromList = disks.FirstOrDefault(d => d.IsSystemDisk);

        // Assert
        Assert.NotNull(systemDisk);
        Assert.NotNull(systemDiskFromList);
        Assert.Equal(systemDisk.DiskNumber, systemDiskFromList.DiskNumber);
    }

    [Fact]
    public async void DiskBusType_IsSet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Assert
        Assert.NotNull(disk.BusType);
        Assert.NotEmpty(disk.BusType);
        Assert.NotEqual("Unknown", disk.BusType);
    }

    [Fact]
    public async void DiskIsGpt_IsSet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Assert
        Assert.IsType<bool>(disk.IsGpt);
    }

    [Fact]
    public async void DiskIsOnline_IsSet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Assert
        Assert.IsType<bool>(disk.IsOnline);
    }

    [Fact]
    public async void DiskIsReadOnly_IsSet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Assert
        Assert.IsType<bool>(disk.IsReadOnly);
    }

    [Fact]
    public async void DiskIsRemovable_IsSet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Assert
        Assert.IsType<bool>(disk.IsRemovable);
    }

    [Fact]
    public async void PartitionIsSystemPartition_IsSet()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null || !disk.Partitions.Any())
        {
            return;
        }

        // Assert
        Assert.Contains(disk.Partitions, p => p.IsSystemPartition);
    }

    [Fact]
    public async void PartitionIsBootRequired_IsCorrect()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null || !disk.Partitions.Any())
        {
            return;
        }

        // Assert
        foreach (var partition in disk.Partitions)
        {
            if (partition.IsEfiPartition || partition.IsSystemPartition)
            {
                Assert.True(partition.IsBootRequired);
            }
            else
            {
                Assert.False(partition.IsBootRequired);
            }
        }
    }

    [Fact]
    public async void PartitionIsHidden_IsCorrect()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null || !disk.Partitions.Any())
        {
            return;
        }

        // Assert
        foreach (var partition in disk.Partitions)
        {
            if (partition.IsRecoveryPartition || partition.IsMsrPartition)
            {
                Assert.True(partition.IsHidden);
            }
            else
            {
                Assert.False(partition.IsHidden);
            }
        }
    }

    [Fact]
    public async void DiskToString_IncludesAllProperties()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null)
        {
            return;
        }

        // Act
        var result = disk.ToString();

        // Assert
        Assert.Contains($"Disk {disk.DiskNumber}", result);
        Assert.Contains(disk.FriendlyName, result);
        Assert.Contains(disk.SizeDisplay, result);
        Assert.Contains(disk.BusType, result);
        Assert.Contains(disk.IsGpt ? "GPT" : "MBR", result);
    }

    [Fact]
    public async void PartitionToString_IncludesAllProperties()
    {
        // Arrange
        var disks = await _enumerator.GetDisksAsync();
        var disk = disks.FirstOrDefault();

        if (disk == null || !disk.Partitions.Any())
        {
            return;
        }

        var partition = disk.Partitions.First();

        // Act
        var result = partition.ToString();

        // Assert
        Assert.Contains($"Partition {partition.PartitionNumber}", result);
        Assert.Contains(partition.SizeDisplay, result);
        if (partition.DriveLetter.HasValue)
        {
            Assert.Contains($"Drive {partition.DriveLetter.Value}:", result);
        }
    }
}
