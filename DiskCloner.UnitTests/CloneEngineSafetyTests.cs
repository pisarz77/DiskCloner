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
        var engine = CreateEngine();
        var operation = CreateOperationForMapping();

        var diskpartOutput = @"
  Partition ###  Type              Size     Offset
  -------------  ----------------  -------  -------
  Partition 1    Reserved            15 MB    17 KB
  Partition 2    System             100 MB    16 MB
  Partition 3    Primary            236 GB   116 MB
  Partition 4    Recovery           533 MB   236 GB
* Partition 5    Recovery           768 MB   237 GB
";

        InvokePrivate(
            engine,
            "ApplyTargetPartitionOffsets",
            new object[] { operation, diskpartOutput });

        var efi = operation.PartitionsToClone.Single(p => p.IsEfiPartition);
        var sys = operation.PartitionsToClone.Single(p => p.IsSystemPartition);
        var recs = operation.PartitionsToClone.Where(p => p.IsRecoveryPartition).OrderBy(p => p.StartingOffset).ToList();

        Assert.Equal(2, efi.TargetPartitionNumber);
        Assert.Equal(3, sys.TargetPartitionNumber);
        Assert.Equal(4, recs[0].TargetPartitionNumber);
        Assert.Equal(5, recs[1].TargetPartitionNumber);
    }

    [Fact]
    public void EnsureTargetDiskMutationAllowed_RejectsSourceDisk()
    {
        var engine = CreateEngine();
        var operation = CreateOperationForMapping();

        var ex = Assert.Throws<TargetInvocationException>(() =>
            InvokePrivate(engine, "EnsureTargetDiskMutationAllowed", new object[] { operation, 0, "test-op" }));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void GetCopyStrategy_UsesFileSystemMigrationForShrunkNtfsSystem()
    {
        var engine = CreateEngine();
        var operation = CreateOperationForMapping();
        operation.AllowSmallerTarget = true;

        var system = operation.PartitionsToClone.Single(p => p.IsSystemPartition);
        system.FileSystemType = "NTFS";
        system.SizeBytes = 1000;
        system.TargetSizeBytes = 500;
        system.DriveLetter = 'C';

        var strategy = InvokePrivate(engine, "GetCopyStrategy", new object[] { operation, system });
        Assert.Equal("FileSystemMigration", strategy?.ToString());
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

    private static DiskClonerEngine CreateEngine()
    {
        var logger = new NoopLogger();
        return new DiskClonerEngine(logger, new DiskEnumerator(logger), new VssSnapshotService(logger));
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
