using System;
using System.IO;
using System.Threading.Tasks;
using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Services;
using Moq;
using Xunit;

namespace DiskCloner.Tests.Services;

public class VssSnapshotServiceTests
{
    private readonly Mock<ILogger> _mockLogger;
    private readonly VssSnapshotService _vssService;

    public VssSnapshotServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _vssService = new VssSnapshotService(_mockLogger.Object);
    }

    [Fact]
    public void VssSnapshotService_Constructor_ThrowsOnNullLogger()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new VssSnapshotService(null));
    }

    [Fact]
    public void VssSnapshotService_Constructor_AcceptsValidLogger()
    {
        // Arrange & Act
        var service = new VssSnapshotService(_mockLogger.Object);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public async Task CreateSnapshotsAsync_ThrowsOnNullOperation()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _vssService.CreateSnapshotsAsync((CloneOperation)null!));
    }

    [Fact]
    public async Task CreateSnapshotsAsync_ThrowsOnEmptyPartitions()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>()
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _vssService.CreateSnapshotsAsync(operation));
    }

    [Fact]
    public async Task CreateSnapshotsAsync_ThrowsOnNullPartitions()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = null!
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _vssService.CreateSnapshotsAsync(operation));
    }

    [Fact]
    public async Task CreateSnapshotsAsync_ReturnsValidSnapshotInfo()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                }
            }
        };

        // Act
        var result = await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.VolumeSnapshots);
        Assert.NotEmpty(result.VolumeSnapshots);
        Assert.NotNull(result.SnapshotPaths);
        Assert.NotEmpty(result.SnapshotPaths);
    }

    [Fact]
    public async Task CreateSnapshotsAsync_HandlesMultiplePartitions()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                },
                new PartitionInfo
                {
                    PartitionNumber = 2,
                    DriveLetter = 'D',
                    SizeBytes = 500000000
                }
            }
        };

        // Act
        var result = await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.VolumeSnapshots.Count);
        Assert.Equal(2, result.SnapshotPaths.Count);
    }

    [Fact]
    public async Task CreateSnapshotsAsync_HandlesPartitionsWithoutDriveLetters()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = null,
                    SizeBytes = 1000000000
                }
            }
        };

        // Act
        var result = await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.VolumeSnapshots);
        Assert.Empty(result.SnapshotPaths);
    }

    [Fact]
    public async Task CleanupSnapshotsAsync_ThrowsOnNullSnapshotInfo()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => _vssService.CleanupSnapshotsAsync((VssSnapshotService.SnapshotInfo)null!));
    }

    [Fact]
    public async Task CleanupSnapshotsAsync_CompletesSuccessfully()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                }
            }
        };

        // Create snapshots first
        var snapshotInfo = await _vssService.CreateSnapshotsAsync(operation);

        // Act
        await _vssService.CleanupSnapshotsAsync(snapshotInfo);

        // Assert
        // If we get here without exception, cleanup was successful
        Assert.NotNull(snapshotInfo);
    }

    [Fact]
    public async Task CleanupSnapshotsAsync_HandlesEmptySnapshotInfo()
    {
        // Arrange
        var snapshotInfo = new VssSnapshotService.SnapshotInfo
        {
            VolumeSnapshots = new List<string>(),
            SnapshotPaths = new List<string>()
        };

        // Act
        await _vssService.CleanupSnapshotsAsync(snapshotInfo);

        // Assert
        // If we get here without exception, cleanup was successful
        Assert.NotNull(snapshotInfo);
    }

    [Fact]
    public async Task IsVssAvailableAsync_ReturnsBoolean()
    {
        // Act
        var result = await _vssService.IsVssAvailableAsync();

        // Assert
        Assert.IsType<bool>(result);
    }

    [Fact]
    public async Task GetBitLockerStatusAsync_ReturnsStatus()
    {
        // Act
        var result = await _vssService.GetBitLockerStatusAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Status);
        Assert.NotNull(result.Protectors);
    }

    [Fact]
    public async Task GetBitLockerStatusAsync_StatusIsSet()
    {
        // Act
        var result = await _vssService.GetBitLockerStatusAsync();

        // Assert
        Assert.NotNull(result.Status);
        Assert.NotEmpty(result.Status);
    }

    [Fact]
    public async Task GetBitLockerStatusAsync_ProtectorsIsSet()
    {
        // Act
        var result = await _vssService.GetBitLockerStatusAsync();

        // Assert
        Assert.NotNull(result.Protectors);
        Assert.NotEmpty(result.Protectors);
    }

    [Fact]
    public async Task ResolveVolumeGuidAsync_ReturnsGuidForValidDrive()
    {
        // Arrange
        var driveLetter = 'C';

        // Act
        var result = await _vssService.ResolveVolumeGuidAsync(driveLetter);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("?", result);
        Assert.Contains("Volume", result);
    }

    [Fact]
    public async Task ResolveVolumeGuidAsync_ReturnsNullForInvalidDrive()
    {
        // Arrange
        var driveLetter = 'Z';

        // Act
        var result = await _vssService.ResolveVolumeGuidAsync(driveLetter);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveVolumeGuidAsync_HandlesNullDrive()
    {
        // Arrange
        char? driveLetter = null;

        // Act
        var result = await _vssService.ResolveVolumeGuidAsync(driveLetter);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateSnapshotsAsync_LogsInformation()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                }
            }
        };

        // Act
        await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        _mockLogger.Verify(l => l.Info(It.Is<string>(s => s.Contains("Creating VSS snapshots"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CleanupSnapshotsAsync_LogsInformation()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                }
            }
        };

        var snapshotInfo = await _vssService.CreateSnapshotsAsync(operation);

        // Act
        await _vssService.CleanupSnapshotsAsync(snapshotInfo);

        // Assert
        _mockLogger.Verify(l => l.Info(It.Is<string>(s => s.Contains("Cleaning up VSS snapshots"))), Times.Once);
    }

    [Fact]
    public async Task IsVssAvailableAsync_LogsInformation()
    {
        // Act
        await _vssService.IsVssAvailableAsync();

        // Assert
        _mockLogger.Verify(l => l.Info(It.Is<string>(s => s.Contains("Checking VSS availability"))), Times.Once);
    }

    [Fact]
    public async Task GetBitLockerStatusAsync_LogsInformation()
    {
        // Act
        await _vssService.GetBitLockerStatusAsync();

        // Assert
        _mockLogger.Verify(l => l.Info(It.Is<string>(s => s.Contains("Checking BitLocker status"))), Times.Once);
    }

    [Fact]
    public async Task ResolveVolumeGuidAsync_LogsInformation()
    {
        // Arrange
        var driveLetter = 'C';

        // Act
        await _vssService.ResolveVolumeGuidAsync(driveLetter);

        // Assert
        _mockLogger.Verify(l => l.Info(It.Is<string>(s => s.Contains("Resolving volume GUID"))), Times.Once);
    }

    [Fact]
    public async Task CreateSnapshotsAsync_HandlesExceptionGracefully()
    {
        // Arrange - This test would require mocking the actual vshadow.exe execution
        // For now, we test that the method signature is correct and handles basic validation
        
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                }
            }
        };

        // Act & Assert - This should not throw for valid input
        var result = await _vssService.CreateSnapshotsAsync(operation);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CleanupSnapshotsAsync_HandlesExceptionGracefully()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                }
            }
        };

        var snapshotInfo = await _vssService.CreateSnapshotsAsync(operation);

        // Act & Assert - This should not throw for valid input
        await _vssService.CleanupSnapshotsAsync(snapshotInfo);
    }

    [Fact]
    public async Task SnapshotInfo_PropertiesAreSet()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                }
            }
        };

        // Act
        var result = await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        Assert.NotNull(result.VolumeSnapshots);
        Assert.NotNull(result.SnapshotPaths);
        Assert.NotNull(result.SnapshotIds);
        Assert.NotNull(result.VolumeGuids);
    }

    [Fact]
    public async Task SnapshotInfo_CollectionCountsMatch()
    {
        // Arrange
        var operation = new CloneOperation
        {
            PartitionsToClone = new List<PartitionInfo>
            {
                new PartitionInfo
                {
                    PartitionNumber = 1,
                    DriveLetter = 'C',
                    SizeBytes = 1000000000
                },
                new PartitionInfo
                {
                    PartitionNumber = 2,
                    DriveLetter = 'D',
                    SizeBytes = 500000000
                }
            }
        };

        // Act
        var result = await _vssService.CreateSnapshotsAsync(operation);

        // Assert
        Assert.Equal(2, result.VolumeSnapshots.Count);
        Assert.Equal(2, result.SnapshotPaths.Count);
        Assert.Equal(2, result.SnapshotIds.Count);
        Assert.Equal(2, result.VolumeGuids.Count);
    }
}
