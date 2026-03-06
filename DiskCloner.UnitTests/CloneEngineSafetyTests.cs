using System.Reflection;
using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Services;
using Xunit;

namespace DiskCloner.UnitTests;

public class CloneEngineSafetyTests
{
    [Fact]
    public void ApplyTargetPartitionOffsets_AssignsTargetPartitionNumbers()
    {
        var service = new DiskpartService(new NoopLogger());
        var operation = CreateOperationForMapping();

        var targetPartitions = new List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>
        {
            (1, "Reserved", 15L * 1024 * 1024, 17L * 1024),
            (2, "System", 100L * 1024 * 1024, 16L * 1024 * 1024),
            (3, "Primary", 236L * 1024 * 1024 * 1024, 116L * 1024 * 1024),
            (4, "Recovery", 533L * 1024 * 1024, 236L * 1024 * 1024 * 1024),
            (5, "Recovery", 768L * 1024 * 1024, 237L * 1024 * 1024 * 1024)
        };

        InvokePrivate(
            service,
            "ApplyTargetPartitionOffsets",
            new object[] { operation, targetPartitions });

        var efi = operation.PartitionsToClone.Single(p => p.IsEfiPartition);
        var sys = operation.PartitionsToClone.Single(p => p.IsSystemPartition);
        var recs = operation.PartitionsToClone.Where(p => p.IsRecoveryPartition).OrderBy(p => p.StartingOffset).ToList();

        Assert.Equal(2, efi.TargetPartitionNumber);
        Assert.Equal(3, sys.TargetPartitionNumber);
        Assert.Equal(4, recs[0].TargetPartitionNumber);
        Assert.Equal(5, recs[1].TargetPartitionNumber);
    }

    [Fact]
    public void ApplyTargetPartitionOffsets_AllowsEqualRoundedOffsets_FromDiskpartText()
    {
        var service = new DiskpartService(new NoopLogger());
        var operation = CreateOperationForMapping();

        // DiskPart text output may round both recovery offsets to the same GB value.
        var targetPartitions = new List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>
        {
            (1, "Reserved", 15L * 1024 * 1024, 17L * 1024),
            (2, "System", 100L * 1024 * 1024, 16L * 1024 * 1024),
            (3, "Primary", 236L * 1024 * 1024 * 1024, 116L * 1024 * 1024),
            (4, "Recovery", 533L * 1024 * 1024, 237L * 1024 * 1024 * 1024),
            (5, "Recovery", 768L * 1024 * 1024, 237L * 1024 * 1024 * 1024)
        };

        InvokePrivate(
            service,
            "ApplyTargetPartitionOffsets",
            new object[] { operation, targetPartitions });

        var recs = operation.PartitionsToClone.Where(p => p.IsRecoveryPartition).OrderBy(p => p.StartingOffset).ToList();
        Assert.Equal(4, recs[0].TargetPartitionNumber);
        Assert.Equal(5, recs[1].TargetPartitionNumber);
    }

    [Fact]
    public void EnsureTargetDiskMutationAllowed_RejectsSourceDisk()
    {
        var logger = new NoopLogger();
        var validator = new CloneValidator(logger, new DiskEnumerator(logger), new VssSnapshotService(logger));
        var operation = CreateOperationForMapping();

        Assert.Throws<InvalidOperationException>(() =>
            validator.EnsureTargetDiskMutationAllowed(operation, 0, "test-op"));
    }

    [Fact]
    public void GetCopyStrategy_UsesFileSystemMigrationForShrunkNtfsSystem()
    {
        var copier = new PartitionCopier(new NoopLogger(), new VssSnapshotService(new NoopLogger()), null, default);
        var operation = CreateOperationForMapping();
        operation.AllowSmallerTarget = true;

        var system = operation.PartitionsToClone.Single(p => p.IsSystemPartition);
        system.FileSystemType = "NTFS";
        system.SizeBytes = 1000;
        system.TargetSizeBytes = 500;
        system.DriveLetter = 'C';

        var strategy = copier.GetCopyStrategy(operation, system);
        Assert.Equal("FileSystemMigration", strategy.ToString());
    }

    [Fact]
    public void GetOperationSummary_ShowsSourceToTargetSizes_WithExpandedSystemPartition()
    {
        var logger = new NoopLogger();
        var vssService = new VssSnapshotService(logger);
        var diskEnumerator = new DiskEnumerator(logger);
        var cts = new CancellationTokenSource();
        var validator = new CloneValidator(logger, diskEnumerator, vssService);
        
        var orchestrator = new CloneOrchestrator(
            logger, validator, new DiskpartService(logger, cts.Token), 
            new PartitionCopier(logger, vssService, null, cts.Token),
            new FileSystemMigrator(logger, vssService, validator, null, cts.Token),
            new SystemQuietModeService(logger, null),
            new IntegrityVerifier(logger, PartitionCopier.GetRawCopyLengthBytes, PartitionCopier.CalculateSafeEta, null, cts.Token),
            new TargetDiskLifecycleManager(logger, validator),
            diskEnumerator, vssService, cts);
        var operation = new CloneOperation
        {
            SourceDisk = new DiskInfo
            {
                DiskNumber = 0,
                IsGpt = true,
                SizeBytes = 476L * 1024 * 1024 * 1024
            },
            TargetDisk = new DiskInfo
            {
                DiskNumber = 1,
                IsGpt = true,
                SizeBytes = 238L * 1024 * 1024 * 1024
            },
            AutoExpandWindowsPartition = true,
            AllowSmallerTarget = false,
            PartitionsToClone = new List<PartitionInfo>
            {
                new() { PartitionNumber = 1, StartingOffset = 1_048_576, SizeBytes = 100L * 1024 * 1024, IsEfiPartition = true, FileSystemType = "FAT32" },
                new() { PartitionNumber = 2, StartingOffset = 122_683_392, SizeBytes = 103L * 1024 * 1024 * 1024, IsSystemPartition = true, DriveLetter = 'C', FileSystemType = "NTFS" },
                new() { PartitionNumber = 3, StartingOffset = 510_744_592_384, SizeBytes = 533L * 1024 * 1024, IsRecoveryPartition = true },
                new() { PartitionNumber = 4, StartingOffset = 511_303_483_392, SizeBytes = 768L * 1024 * 1024, IsRecoveryPartition = true }
            }
        };

        var summary = orchestrator.GetOperationSummary(operation);
        var systemPartition = operation.PartitionsToClone.Single(p => p.IsSystemPartition);

        Assert.True(systemPartition.TargetSizeBytes > systemPartition.SizeBytes);
        Assert.Contains("Partition Plan (source -> target):", summary);
        Assert.Contains("[2] Windows/System:", summary);
        Assert.Contains("[EXPANDED]", summary);
    }

    private static object? InvokePrivate(object instance, string methodName, object[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().Name, methodName);
        return method.Invoke(instance, args);
    }

    private static CloneOperation CreateOperationForMapping()
    {
        return new CloneOperation
        {
            SourceDisk = new DiskInfo { DiskNumber = 0, IsGpt = true },
            TargetDisk = new DiskInfo { DiskNumber = 1, IsGpt = true },
            AllowSmallerTarget = true,
            PartitionsToClone = new List<PartitionInfo>
            {
                new() { PartitionNumber = 1, StartingOffset = 1048576, SizeBytes = 100 * 1024 * 1024, TargetSizeBytes = 100 * 1024 * 1024, IsEfiPartition = true, FileSystemType = "FAT32" },
                new() { PartitionNumber = 2, StartingOffset = 122683392, SizeBytes = 475L * 1024 * 1024 * 1024, TargetSizeBytes = 236L * 1024 * 1024 * 1024, IsSystemPartition = true, DriveLetter = 'C', FileSystemType = "NTFS" },
                new() { PartitionNumber = 3, StartingOffset = 510744592384, SizeBytes = 533 * 1024 * 1024, TargetSizeBytes = 533 * 1024 * 1024, IsRecoveryPartition = true },
                new() { PartitionNumber = 4, StartingOffset = 511303483392, SizeBytes = 768 * 1024 * 1024, TargetSizeBytes = 768 * 1024 * 1024, IsRecoveryPartition = true }
            }
        };
    }



    private sealed class NoopLogger : ILogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
        public IReadOnlyList<LogEntry> GetLogEntries() => Array.Empty<LogEntry>();
        public void Clear() { }
        public void Dispose() { }
    }
}
