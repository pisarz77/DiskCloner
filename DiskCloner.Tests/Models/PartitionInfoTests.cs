using System;
using DiskCloner.Core.Models;
using Xunit;

namespace DiskCloner.Tests.Models;

public class PartitionInfoTests
{
    [Fact]
    public void PartitionInfo_DefaultConstructor_InitializesProperties()
    {
        // Arrange & Act
        var partition = new PartitionInfo();

        // Assert
        Assert.Equal(0, partition.PartitionNumber);
        Assert.Equal(0, partition.StartingOffset);
        Assert.Equal(0, partition.SizeBytes);
        Assert.Null(partition.PartitionTypeGuid);
        Assert.Null(partition.UniqueId);
        Assert.Null(partition.MbrPartitionType);
        Assert.False(partition.IsActive);
        Assert.Equal(0, partition.GptAttributes);
        Assert.Equal(string.Empty, partition.PartitionName);
        Assert.Null(partition.DriveLetter);
        Assert.Null(partition.VolumeGuid);
        Assert.Equal(string.Empty, partition.FileSystemType);
        Assert.Equal(string.Empty, partition.VolumeLabel);
        Assert.False(partition.IsSystemPartition);
        Assert.False(partition.IsEfiPartition);
        Assert.False(partition.IsMsrPartition);
        Assert.False(partition.IsRecoveryPartition);
        Assert.False(partition.IsBootRequired);
        Assert.False(partition.IsHidden);
    }

    [Fact]
    public void PartitionInfo_SetProperties_CorrectlyStoresValues()
    {
        // Arrange
        var partition = new PartitionInfo
        {
            PartitionNumber = 1,
            StartingOffset = 1048576,
            SizeBytes = 1000000000,
            PartitionTypeGuid = Guid.Parse("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"),
            UniqueId = Guid.NewGuid(),
            MbrPartitionType = 0x07,
            IsActive = true,
            GptAttributes = 0x8000000000000001,
            PartitionName = "EFI System",
            DriveLetter = 'C',
            VolumeGuid = "{12345678-1234-1234-1234-123456789012}",
            FileSystemType = "NTFS",
            VolumeLabel = "Windows",
            IsSystemPartition = true,
            IsEfiPartition = true,
            IsMsrPartition = false,
            IsRecoveryPartition = false
        };

        // Assert
        Assert.Equal(1, partition.PartitionNumber);
        Assert.Equal(1048576, partition.StartingOffset);
        Assert.Equal(1000000000, partition.SizeBytes);
        Assert.NotNull(partition.PartitionTypeGuid);
        Assert.NotNull(partition.UniqueId);
        Assert.Equal(0x07, partition.MbrPartitionType);
        Assert.True(partition.IsActive);
        Assert.Equal(0x8000000000000001, partition.GptAttributes);
        Assert.Equal("EFI System", partition.PartitionName);
        Assert.Equal('C', partition.DriveLetter);
        Assert.Equal("{12345678-1234-1234-1234-123456789012}", partition.VolumeGuid);
        Assert.Equal("NTFS", partition.FileSystemType);
        Assert.Equal("Windows", partition.VolumeLabel);
        Assert.True(partition.IsSystemPartition);
        Assert.True(partition.IsEfiPartition);
        Assert.False(partition.IsMsrPartition);
        Assert.False(partition.IsRecoveryPartition);
    }

    [Fact]
    public void IsBootRequired_ReturnsTrueForEfiOrSystem()
    {
        // Arrange
        var efiPartition = new PartitionInfo { IsEfiPartition = true };
        var systemPartition = new PartitionInfo { IsSystemPartition = true };
        var dataPartition = new PartitionInfo { IsEfiPartition = false, IsSystemPartition = false };

        // Assert
        Assert.True(efiPartition.IsBootRequired);
        Assert.True(systemPartition.IsBootRequired);
        Assert.False(dataPartition.IsBootRequired);
    }

    [Fact]
    public void IsHidden_ReturnsTrueForRecoveryOrMsr()
    {
        // Arrange
        var recoveryPartition = new PartitionInfo { IsRecoveryPartition = true };
        var msrPartition = new PartitionInfo { IsMsrPartition = true };
        var efiPartition = new PartitionInfo { IsEfiPartition = true };
        var systemPartition = new PartitionInfo { IsSystemPartition = true };

        // Assert
        Assert.True(recoveryPartition.IsHidden);
        Assert.True(msrPartition.IsHidden);
        Assert.False(efiPartition.IsHidden);
        Assert.False(systemPartition.IsHidden);
    }

    [Fact]
    public void SizeDisplay_FormatsBytesCorrectly()
    {
        // Arrange
        var partition = new PartitionInfo();

        // Act & Assert
        partition.SizeBytes = 512;
        Assert.Equal("512 B", partition.SizeDisplay);

        partition.SizeBytes = 1024;
        Assert.Equal("1 KB", partition.SizeDisplay);

        partition.SizeBytes = 1048576;
        Assert.Equal("1 MB", partition.SizeDisplay);

        partition.SizeBytes = 1073741824;
        Assert.Equal("1 GB", partition.SizeDisplay);

        partition.SizeBytes = 1500000000;
        Assert.Equal("1.39 GB", partition.SizeDisplay);
    }

    [Fact]
    public void StartingSector_CalculatesCorrectly()
    {
        // Arrange
        var partition = new PartitionInfo
        {
            StartingOffset = 1048576 // 2048 sectors * 512 bytes
        };

        // Assert
        Assert.Equal(2048, partition.StartingSector);
    }

    [Fact]
    public void Sectors_CalculatesCorrectly()
    {
        // Arrange
        var partition = new PartitionInfo
        {
            SizeBytes = 1048576 // 2048 sectors * 512 bytes
        };

        // Assert
        Assert.Equal(2048, partition.Sectors);
    }

    [Fact]
    public void ToString_IncludesAllProperties()
    {
        // Arrange
        var partition = new PartitionInfo
        {
            PartitionNumber = 1,
            DriveLetter = 'C',
            SizeBytes = 250000000000,
            FileSystemType = "NTFS",
            IsEfiPartition = false,
            IsSystemPartition = true,
            IsMsrPartition = false,
            IsRecoveryPartition = false
        };

        // Act
        var result = partition.ToString();

        // Assert
        Assert.Contains("Partition 1: Drive C: 232.83 GB (NTFS) [SYSTEM]", result);
    }

    [Fact]
    public void ToString_IncludesAllPartitionTypes()
    {
        // Arrange
        var partition = new PartitionInfo
        {
            PartitionNumber = 2,
            SizeBytes = 100000000,
            IsEfiPartition = true,
            IsSystemPartition = false,
            IsMsrPartition = true,
            IsRecoveryPartition = true
        };

        // Act
        var result = partition.ToString();

        // Assert
        Assert.Contains("Partition 2: 95.37 MB [EFI] [MSR] [RECOVERY]", result);
    }

    [Fact]
    public void ToString_ExcludesDriveLetterWhenNull()
    {
        // Arrange
        var partition = new PartitionInfo
        {
            PartitionNumber = 3,
            SizeBytes = 500000000,
            DriveLetter = null
        };

        // Act
        var result = partition.ToString();

        // Assert
        Assert.Contains("Partition 3: 476.84 MB", result);
        Assert.DoesNotContain("Drive", result);
    }

    [Fact]
    public void GetTypeName_ReturnsCorrectTypeNames()
    {
        // Arrange
        var efiPartition = new PartitionInfo { IsEfiPartition = true };
        var systemPartition = new PartitionInfo { IsSystemPartition = true };
        var msrPartition = new PartitionInfo { IsMsrPartition = true };
        var recoveryPartition = new PartitionInfo { IsRecoveryPartition = true };
        var dataPartition = new PartitionInfo { IsEfiPartition = false, IsSystemPartition = false, IsMsrPartition = false, IsRecoveryPartition = false };

        // Act & Assert
        Assert.Equal("EFI System Partition", efiPartition.GetTypeName());
        Assert.Equal("Windows/System", systemPartition.GetTypeName());
        Assert.Equal("Microsoft Reserved", msrPartition.GetTypeName());
        Assert.Equal("Recovery", recoveryPartition.GetTypeName());
        Assert.Equal("Data", dataPartition.GetTypeName());
    }

    [Fact]
    public void GetTypeName_HandlesGptGuids()
    {
        // Arrange
        var msrPartition = new PartitionInfo
        {
            PartitionTypeGuid = Guid.Parse("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")
        };
        var recoveryPartition = new PartitionInfo
        {
            PartitionTypeGuid = Guid.Parse("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")
        };

        // Act & Assert
        Assert.Equal("Microsoft Reserved", msrPartition.GetTypeName());
        Assert.Equal("Windows Recovery", recoveryPartition.GetTypeName());
    }

    [Fact]
    public void PartitionGuids_AreValid()
    {
        // Arrange
        var partition = new PartitionInfo
        {
            PartitionTypeGuid = Guid.Parse("C12A7328-F81F-11D2-BA4B-00A0C93EC93B"),
            UniqueId = Guid.NewGuid()
        };

        // Assert
        Assert.NotNull(partition.PartitionTypeGuid);
        Assert.NotEqual(Guid.Empty, partition.PartitionTypeGuid.Value);
        Assert.NotNull(partition.UniqueId);
        Assert.NotEqual(Guid.Empty, partition.UniqueId.Value);
    }
}