using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Services;

namespace DiskCloner.Tests;

public class Program
{
    private static readonly List<string> _testResults = new();
    private static int _passedTests = 0;
    private static int _failedTests = 0;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Disk Cloner Test Suite");
        Console.WriteLine("======================");
        Console.WriteLine();

        try
        {
            await RunAllTests();
            PrintSummary();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Test runner failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static async Task RunAllTests()
    {
        await TestDiskInfo();
        await TestPartitionInfo();
        await TestCloneOperation();
        await TestDiskEnumerator();
        await TestVssSnapshotService();
        await TestFileLogger();
        await TestIntegration();
    }

    private static async Task TestDiskInfo()
    {
        Console.WriteLine("Testing DiskInfo...");

        // Test 1: Default constructor
        var disk = new DiskInfo();
        Assert(disk.DiskNumber == 0, "Default constructor sets DiskNumber to 0");
        Assert(disk.FriendlyName == string.Empty, "Default constructor sets FriendlyName to empty string");
        Assert(disk.Partitions != null, "Default constructor initializes Partitions collection");
        Assert(disk.Partitions.Count == 0, "Default constructor creates empty Partitions collection");

        // Test 2: Property setting
        disk.DiskNumber = 1;
        disk.FriendlyName = "Test Disk";
        disk.SizeBytes = 1000000000;
        disk.IsGpt = true;
        disk.IsSystemDisk = true;

        Assert(disk.DiskNumber == 1, "DiskNumber property works");
        Assert(disk.FriendlyName == "Test Disk", "FriendlyName property works");
        Assert(disk.SizeBytes == 1000000000, "SizeBytes property works");
        Assert(disk.IsGpt == true, "IsGpt property works");
        Assert(disk.IsSystemDisk == true, "IsSystemDisk property works");

        // Test 3: ToString method
        var toString = disk.ToString();
        Assert(toString.Contains("Disk 1"), "ToString includes disk number");
        Assert(toString.Contains("Test Disk"), "ToString includes friendly name");

        Console.WriteLine("✓ DiskInfo tests passed");
    }

    private static async Task TestPartitionInfo()
    {
        Console.WriteLine("Testing PartitionInfo...");

        // Test 1: Default constructor
        var partition = new PartitionInfo();
        Assert(partition.PartitionNumber == 0, "Default constructor sets PartitionNumber to 0");
        Assert(partition.StartingOffset == 0, "Default constructor sets StartingOffset to 0");
        Assert(partition.SizeBytes == 0, "Default constructor sets SizeBytes to 0");
        Assert(partition.PartitionName == string.Empty, "Default constructor sets PartitionName to empty string");

        // Test 2: Property setting
        partition.PartitionNumber = 1;
        partition.StartingOffset = 1048576;
        partition.SizeBytes = 1000000000;
        partition.DriveLetter = 'C';
        partition.IsSystemPartition = true;
        partition.IsEfiPartition = false;

        Assert(partition.PartitionNumber == 1, "PartitionNumber property works");
        Assert(partition.StartingOffset == 1048576, "StartingOffset property works");
        Assert(partition.SizeBytes == 1000000000, "SizeBytes property works");
        Assert(partition.DriveLetter == 'C', "DriveLetter property works");
        Assert(partition.IsSystemPartition == true, "IsSystemPartition property works");
        Assert(partition.IsEfiPartition == false, "IsEfiPartition property works");

        // Test 3: Calculated properties
        Assert(partition.StartingSector == 2048, "StartingSector calculation works");
        Assert(partition.Sectors == 1953125, "Sectors calculation works");

        // Test 4: IsBootRequired property
        Assert(partition.IsBootRequired == true, "IsBootRequired returns true for system partition");

        // Test 5: ToString method
        var toString = partition.ToString();
        Assert(toString.Contains("Partition 1"), "ToString includes partition number");
        Assert(toString.Contains("Drive C:"), "ToString includes drive letter");

        Console.WriteLine("✓ PartitionInfo tests passed");
    }

    private static async Task TestCloneOperation()
    {
        Console.WriteLine("Testing CloneOperation...");

        // Test 1: Default constructor
        var operation = new CloneOperation();
        Assert(operation.PartitionsToClone != null, "Default constructor initializes PartitionsToClone");
        Assert(operation.PartitionsToClone.Count == 0, "Default constructor creates empty PartitionsToClone");
        Assert(operation.UseVss == true, "Default constructor sets UseVss to true");
        Assert(operation.IoBufferSize == 64 * 1024 * 1024, "Default constructor sets IoBufferSize to 64MB");
        Assert(operation.VerifyIntegrity == true, "Default constructor sets VerifyIntegrity to true");
        Assert(operation.OperationId != Guid.Empty, "Default constructor generates OperationId");

        // Test 2: Property setting
        var sourceDisk = new DiskInfo { DiskNumber = 0 };
        var targetDisk = new DiskInfo { DiskNumber = 1 };
        var partitions = new List<PartitionInfo>
        {
            new PartitionInfo { PartitionNumber = 1, SizeBytes = 1000000000 }
        };

        operation.SourceDisk = sourceDisk;
        operation.TargetDisk = targetDisk;
        operation.PartitionsToClone = partitions;
        operation.UseVss = false;
        operation.IoBufferSize = 32 * 1024 * 1024;
        operation.VerifyIntegrity = false;

        Assert(operation.SourceDisk == sourceDisk, "SourceDisk property works");
        Assert(operation.TargetDisk == targetDisk, "TargetDisk property works");
        Assert(operation.PartitionsToClone == partitions, "PartitionsToClone property works");
        Assert(operation.UseVss == false, "UseVss property works");
        Assert(operation.IoBufferSize == 32 * 1024 * 1024, "IoBufferSize property works");
        Assert(operation.VerifyIntegrity == false, "VerifyIntegrity property works");

        Console.WriteLine("✓ CloneOperation tests passed");
    }

    private static async Task TestDiskEnumerator()
    {
        Console.WriteLine("Testing DiskEnumerator...");

        var logger = new TestLogger();
        var enumerator = new DiskEnumerator(logger);

        try
        {
            // Test 1: Get disks
            var disks = await enumerator.GetDisksAsync();
            Assert(disks != null, "GetDisksAsync returns non-null result");
            Assert(disks.Count > 0, "GetDisksAsync returns at least one disk");

            // Test 2: Get system disk
            var systemDisk = await enumerator.GetSystemDiskAsync();
            Assert(systemDisk != null, "GetSystemDiskAsync returns non-null result");
            Assert(systemDisk.IsSystemDisk, "GetSystemDiskAsync returns system disk");

            // Test 3: Get target disks
            var targetDisks = await enumerator.GetTargetDisksAsync();
            Assert(targetDisks != null, "GetTargetDisksAsync returns non-null result");
            Assert(targetDisks.Count >= 0, "GetTargetDisksAsync returns valid count");

            // Test 4: Disk properties
            var disk = disks[0];
            Assert(disk.DiskNumber >= 0, "Disk number is valid");
            Assert(!string.IsNullOrEmpty(disk.FriendlyName), "Disk friendly name is not empty");
            Assert(disk.SizeBytes >= 0, "Disk size is valid");
            Assert(disk.Partitions != null, "Disk partitions collection is not null");

            // Test 5: Partition properties
            if (disk.Partitions.Count > 0)
            {
                var partition = disk.Partitions[0];
                Assert(partition.PartitionNumber > 0, "Partition number is valid");
                Assert(partition.SizeBytes > 0, "Partition size is valid");
                Assert(partition.GetTypeName() != null, "Partition type name is not null");
            }

            Console.WriteLine("✓ DiskEnumerator tests passed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ DiskEnumerator tests failed: {ex.Message}");
            _failedTests++;
        }
    }

    private static async Task TestVssSnapshotService()
    {
        Console.WriteLine("Testing VssSnapshotService...");

        var logger = new TestLogger();
        var vssService = new VssSnapshotService(logger);

        try
        {
            // Test 1: Check VSS availability
            var isVssAvailable = await vssService.IsVssAvailableAsync();
            Assert(isVssAvailable is bool, "IsVssAvailableAsync returns boolean");

            // Test 2: Get BitLocker status
            var bitLockerStatus = await vssService.GetBitLockerStatusAsync();
            Assert(bitLockerStatus != null, "GetBitLockerStatusAsync returns non-null result");
            Assert(bitLockerStatus.Status != null, "BitLocker status is not null");
            Assert(bitLockerStatus.Protectors != null, "BitLocker protectors is not null");

            Console.WriteLine("✓ VssSnapshotService tests passed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ VssSnapshotService tests failed: {ex.Message}");
            _failedTests++;
        }
    }

    private static async Task TestFileLogger()
    {
        Console.WriteLine("Testing FileLogger...");

        var tempFile = Path.GetTempFileName();
        File.Delete(tempFile); // Delete the file, we just want the path

        try
        {
            var logger = new FileLogger(tempFile);

            // Test 1: Info logging
            logger.Info("Test info message");
            var content = File.ReadAllText(tempFile);
            Assert(content.Contains("INFO"), "Info message contains INFO level");
            Assert(content.Contains("Test info message"), "Info message contains the message");

            // Test 2: Warning logging
            logger.Warning("Test warning message");
            content = File.ReadAllText(tempFile);
            Assert(content.Contains("WARNING"), "Warning message contains WARNING level");

            // Test 3: Error logging
            logger.Error("Test error message");
            content = File.ReadAllText(tempFile);
            Assert(content.Contains("ERROR"), "Error message contains ERROR level");

            // Test 4: Error with exception
            var exception = new Exception("Test exception");
            logger.Error("Test error with exception", exception);
            content = File.ReadAllText(tempFile);
            Assert(content.Contains("Test exception"), "Error with exception contains exception message");

            // Test 5: Debug logging
            logger.Debug("Test debug message");
            content = File.ReadAllText(tempFile);
            Assert(content.Contains("DEBUG"), "Debug message contains DEBUG level");

            Console.WriteLine("✓ FileLogger tests passed");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    private static async Task TestIntegration()
    {
        Console.WriteLine("Testing Integration...");

        var logger = new TestLogger();
        var enumerator = new DiskEnumerator(logger);
        var vssService = new VssSnapshotService(logger);

        try
        {
            // Test 1: Full workflow - get system disk and its partitions
            var systemDisk = await enumerator.GetSystemDiskAsync();
            Assert(systemDisk != null, "System disk is found");
            Assert(systemDisk.IsSystemDisk, "System disk is marked as system");
            Assert(systemDisk.Partitions.Count > 0, "System disk has partitions");

            // Test 2: Check for system partition
            var systemPartition = systemDisk.Partitions.FirstOrDefault(p => p.IsSystemPartition);
            Assert(systemPartition != null, "System partition is found");

            // Test 3: Check VSS availability
            var isVssAvailable = await vssService.IsVssAvailableAsync();
            Assert(isVssAvailable, "VSS is available");

            // Test 4: Check BitLocker status
            var bitLockerStatus = await vssService.GetBitLockerStatusAsync();
            Assert(bitLockerStatus != null, "BitLocker status is retrieved");

            Console.WriteLine("✓ Integration tests passed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ Integration tests failed: {ex.Message}");
            _failedTests++;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            _passedTests++;
            _testResults.Add($"✓ {message}");
        }
        else
        {
            _failedTests++;
            _testResults.Add($"✗ {message}");
            Console.WriteLine($"  FAILED: {message}");
        }
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine("Test Results");
        Console.WriteLine("============");
        Console.WriteLine();

        foreach (var result in _testResults)
        {
            Console.WriteLine(result);
        }

        Console.WriteLine();
        Console.WriteLine($"Total tests: {_passedTests + _failedTests}");
        Console.WriteLine($"Passed: {_passedTests}");
        Console.WriteLine($"Failed: {_failedTests}");

        if (_failedTests == 0)
        {
            Console.WriteLine();
            Console.WriteLine("🎉 All tests passed!");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"❌ {_failedTests} test(s) failed.");
        }
    }
}

public class TestLogger : ILogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");
    public void Warning(string message) => Console.WriteLine($"[WARNING] {message}");
    public void Error(string message) => Console.WriteLine($"[ERROR] {message}");
    public void Error(string message, Exception ex) => Console.WriteLine($"[ERROR] {message}: {ex.Message}");
    public void Debug(string message) => Console.WriteLine($"[DEBUG] {message}");
    public IReadOnlyList<LogEntry> GetLogEntries() => Array.Empty<LogEntry>();
    public void Clear() { }
    public void Dispose() { }
}