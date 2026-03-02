using System;
using DiskCloner.Core.Models;
using Xunit;

namespace DiskCloner.Tests.Models;

public class DiskInfoTests
{
    [Fact]
    public void DiskInfo_DefaultConstructor_InitializesProperties()
    {
        // Arrange & Act
        var disk = new DiskInfo();

        // Assert
        Assert.Equal(0, disk.DiskNumber);
        Assert.Equal(string.Empty, disk.FriendlyName);
        Assert.Equal(string.Empty, disk.DiskId);
        Assert.Equal(0, disk.SizeBytes);
        Assert.Equal(0, disk.TotalSectors);
        Assert.Equal(0, disk.LogicalSectorSize);
        Assert.Equal(0, disk.PhysicalSectorSize);
        Assert.False(disk.IsGpt);
        Assert.False(disk.IsSystemDisk);
        Assert.False(disk.IsOnline);
        Assert.False(disk.IsReadOnly);
        Assert.False(disk.IsRemovable);
        Assert.Equal("Unknown", disk.BusType);
        Assert.NotNull(disk.Partitions);
        Assert.Empty(disk.Partitions);
    }

    [Fact]
    public void DiskInfo_SetProperties_CorrectlyStoresValues()
    {
        // Arrange
        var disk = new DiskInfo
        {
            DiskNumber = 1,
            FriendlyName = "Samsung SSD 860 EVO 1TB",
            DiskId = "6002638-1234567890",
            SizeBytes = 1000204886016,
            TotalSectors = 1953525167,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            IsGpt = true,
            IsSystemDisk = true,
            IsOnline = true,
            IsReadOnly = false,
            IsRemovable = false,
            BusType = "SATA"
        };

        // Assert
        Assert.Equal(1, disk.DiskNumber);
        Assert.Equal("Samsung SSD 860 EVO 1TB", disk.FriendlyName);
        Assert.Equal("6002638-1234567890", disk.DiskId);
        Assert.Equal(1000204886016, disk.SizeBytes);
        Assert.Equal(1953525167, disk.TotalSectors);
        Assert.Equal(512, disk.LogicalSectorSize);
        Assert.Equal(4096, disk.PhysicalSectorSize);
        Assert.True(disk.IsGpt);
        Assert.True(disk.IsSystemDisk);
        Assert.True(disk.IsOnline);
        Assert.False(disk.IsReadOnly);
        Assert.False(disk.IsRemovable);
        Assert.Equal("SATA", disk.BusType);
    }

    [Fact]
    public void SizeDisplay_FormatsBytesCorrectly()
    {
        // Arrange
        var disk = new DiskInfo();

        // Act & Assert
        disk.SizeBytes = 512;
        Assert.Equal("512 B", disk.SizeDisplay);

        disk.SizeBytes = 1024;
        Assert.Equal("1 KB", disk.SizeDisplay);

        disk.SizeBytes = 1048576;
        Assert.Equal("1 MB", disk.SizeDisplay);

        disk.SizeBytes = 1073741824;
        Assert.Equal("1 GB", disk.SizeDisplay);

        disk.SizeBytes = 1099511627776;
        Assert.Equal("1 TB", disk.SizeDisplay);

        disk.SizeBytes = 1125899906842624;
        Assert.Equal("1 PB", disk.SizeDisplay);

        disk.SizeBytes = 1500000000;
        Assert.Equal("1.4 GB", disk.SizeDisplay);
    }

    [Fact]
    public void ToString_IncludesAllProperties()
    {
        // Arrange
        var disk = new DiskInfo
        {
            DiskNumber = 0,
            FriendlyName = "Windows Boot Drive",
            DiskId = "1234567890",
            SizeBytes = 500107862016,
            TotalSectors = 976773167,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 512,
            IsGpt = true,
            IsSystemDisk = true,
            IsOnline = true,
            IsReadOnly = false,
            IsRemovable = false,
            BusType = "NVMe"
        };

        // Act
        var result = disk.ToString();

        // Assert
        Assert.Contains("Disk 0: Windows Boot Drive", result);
        Assert.Contains("Size: 465.76 GB (976,773,167 sectors)", result);
        Assert.Contains("Sector Size: Logical=512, Physical=512", result);
        Assert.Contains("Type: GPT", result);
        Assert.Contains("Bus: NVMe", result);
        Assert.Contains("Status: Online [SYSTEM]", result);
        Assert.Contains("Partitions: 0", result);
    }

    [Fact]
    public void ToString_IncludesRemovableFlag()
    {
        // Arrange
        var disk = new DiskInfo
        {
            DiskNumber = 1,
            FriendlyName = "USB Flash Drive",
            IsRemovable = true,
            IsOnline = true
        };

        // Act
        var result = disk.ToString();

        // Assert
        Assert.Contains("[Removable]", result);
    }

    [Fact]
    public void ToString_IncludesOfflineStatus()
    {
        // Arrange
        var disk = new DiskInfo
        {
            DiskNumber = 2,
            FriendlyName = "External HDD",
            IsOnline = false
        };

        // Act
        var result = disk.ToString();

        // Assert
        Assert.Contains("Status: Offline", result);
    }

    [Fact]
    public void Partitions_CollectionIsModifiable()
    {
        // Arrange
        var disk = new DiskInfo();
        var partition = new PartitionInfo { PartitionNumber = 1, SizeBytes = 100000000 };

        // Act
        disk.Partitions.Add(partition);

        // Assert
        Assert.Single(disk.Partitions);
        Assert.Equal(partition, disk.Partitions[0]);
    }
}
