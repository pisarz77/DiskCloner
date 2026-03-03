using System.Globalization;
using System.Management;
using System.Security.Cryptography;

internal sealed record PartitionDescriptor(int PartitionNumber, long StartingOffset, long SizeBytes, string TypeName);

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);

            if (options.ContainsKey("help") || options.ContainsKey("h") || args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            if (options.ContainsKey("list-disks"))
            {
                ListDisks();
                return 0;
            }

            if (options.TryGetValue("list-partitions", out var listDiskRaw))
            {
                if (!TryParseInt(listDiskRaw, out var diskToList))
                {
                    Console.Error.WriteLine("Invalid --list-partitions value. Expected disk number.");
                    return 2;
                }

                ListPartitions(diskToList);
                return 0;
            }

            if (!TryGetRequiredInt(options, "source-disk", out var sourceDisk))
                return UsageError("Missing required argument --source-disk <N>.");
            if (!TryGetRequiredInt(options, "target-disk", out var targetDisk))
                return UsageError("Missing required argument --target-disk <N>.");

            PartitionDescriptor? sourcePartition = null;
            PartitionDescriptor? targetPartition = null;

            long sourceOffset;
            long targetOffset;
            long length;

            if (TryGetInt(options, "source-partition", out var sourcePartitionNumber))
            {
                sourcePartition = GetPartitionDescriptor(sourceDisk, sourcePartitionNumber);
                sourceOffset = sourcePartition.StartingOffset;
            }
            else if (!TryGetRequiredLong(options, "source-offset", out sourceOffset))
            {
                return UsageError("Provide either --source-partition <N> or --source-offset <bytes>.");
            }

            if (TryGetInt(options, "target-partition", out var targetPartitionNumber))
            {
                targetPartition = GetPartitionDescriptor(targetDisk, targetPartitionNumber);
                targetOffset = targetPartition.StartingOffset;
            }
            else if (!TryGetRequiredLong(options, "target-offset", out targetOffset))
            {
                return UsageError("Provide either --target-partition <N> or --target-offset <bytes>.");
            }

            if (TryGetLong(options, "length", out var lengthOverride))
            {
                length = lengthOverride;
            }
            else if (sourcePartition != null && targetPartition != null)
            {
                length = Math.Min(sourcePartition.SizeBytes, targetPartition.SizeBytes);
            }
            else
            {
                return UsageError("Missing required argument --length <bytes> when partition sizes are not available.");
            }

            if (length <= 0)
                return UsageError("Verification length must be greater than 0.");

            var mode = GetValueOrDefault(options, "mode", "sample").Trim().ToLowerInvariant();
            var sampleCount = TryGetInt(options, "samples", out var samplesValue) ? Math.Max(1, samplesValue) : 100;
            var sampleSizeBytes = (TryGetInt(options, "sample-size-mb", out var sampleMb) ? Math.Max(1, sampleMb) : 1) * 1024 * 1024;
            var bufferSizeBytes = (TryGetInt(options, "buffer-size-mb", out var bufferMb) ? Math.Max(1, bufferMb) : 8) * 1024 * 1024;

            Console.WriteLine("=== DiskCloner.VerifyHelper ===");
            Console.WriteLine($"Source disk: {sourceDisk}");
            Console.WriteLine($"Target disk: {targetDisk}");
            if (sourcePartition != null)
                Console.WriteLine($"Source partition: {sourcePartition.PartitionNumber} ({sourcePartition.TypeName})");
            if (targetPartition != null)
                Console.WriteLine($"Target partition: {targetPartition.PartitionNumber} ({targetPartition.TypeName})");
            Console.WriteLine($"Source offset: {sourceOffset:N0}");
            Console.WriteLine($"Target offset: {targetOffset:N0}");
            Console.WriteLine($"Length: {length:N0} bytes ({FormatBytes(length)})");
            Console.WriteLine($"Mode: {mode}");
            Console.WriteLine();

            bool ok;
            if (mode == "full")
            {
                ok = VerifyFull(sourceDisk, sourceOffset, targetDisk, targetOffset, length, bufferSizeBytes);
            }
            else if (mode == "sample")
            {
                ok = VerifySample(sourceDisk, sourceOffset, targetDisk, targetOffset, length, sampleCount, sampleSizeBytes);
            }
            else
            {
                return UsageError("Unsupported --mode value. Use 'sample' or 'full'.");
            }

            Console.WriteLine();
            Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static bool VerifySample(
        int sourceDisk,
        long sourceOffset,
        int targetDisk,
        long targetOffset,
        long length,
        int samples,
        int sampleSizeBytes)
    {
        using var sourceStream = OpenDiskStream(sourceDisk);
        using var targetStream = OpenDiskStream(targetDisk);
        var sourceBuffer = new byte[sampleSizeBytes];
        var targetBuffer = new byte[sampleSizeBytes];

        Console.WriteLine($"Running sampling verification: {samples} samples, sample size {FormatBytes(sampleSizeBytes)}");

        for (int i = 0; i < samples; i++)
        {
            var sampleSourceOffset = sourceOffset + (length * i / samples);
            var sampleTargetOffset = targetOffset + (length * i / samples);

            var sampleSize = Math.Min(sampleSizeBytes, sourceOffset + length - sampleSourceOffset);
            if (sampleSize <= 0)
                break;

            var sourceHash = HashRegion(sourceStream, sampleSourceOffset, sampleSize, sourceBuffer);
            var targetHash = HashRegion(targetStream, sampleTargetOffset, sampleSize, targetBuffer);

            if (!sourceHash.SequenceEqual(targetHash))
            {
                Console.WriteLine($"Mismatch at sample {i + 1}/{samples}");
                Console.WriteLine($"Source sample offset: {sampleSourceOffset:N0}");
                Console.WriteLine($"Target sample offset: {sampleTargetOffset:N0}");
                Console.WriteLine($"Sample length: {sampleSize:N0}");
                Console.WriteLine($"Source SHA256: {Convert.ToHexString(sourceHash)}");
                Console.WriteLine($"Target SHA256: {Convert.ToHexString(targetHash)}");

                var firstDiff = FindFirstDifference(
                    sourceStream,
                    targetStream,
                    sampleSourceOffset,
                    sampleTargetOffset,
                    sampleSize);

                if (firstDiff.HasValue)
                {
                    Console.WriteLine($"First differing byte in sample at +{firstDiff.Value:N0}");
                    Console.WriteLine($"Absolute source offset: {(sampleSourceOffset + firstDiff.Value):N0}");
                    Console.WriteLine($"Absolute target offset: {(sampleTargetOffset + firstDiff.Value):N0}");
                }

                return false;
            }

            if ((i + 1) % 10 == 0 || i == samples - 1)
            {
                var percent = ((i + 1) * 100.0) / samples;
                Console.WriteLine($"Sample progress: {i + 1}/{samples} ({percent:F1}%)");
            }
        }

        return true;
    }

    private static bool VerifyFull(
        int sourceDisk,
        long sourceOffset,
        int targetDisk,
        long targetOffset,
        long length,
        int bufferSizeBytes)
    {
        using var sourceStream = OpenDiskStream(sourceDisk);
        using var targetStream = OpenDiskStream(targetDisk);
        using var sourceSha = SHA256.Create();
        using var targetSha = SHA256.Create();

        var sourceBuffer = new byte[bufferSizeBytes];
        var targetBuffer = new byte[bufferSizeBytes];
        long remaining = length;
        long processed = 0;
        long lastReportedPercent = -1;

        sourceStream.Seek(sourceOffset, SeekOrigin.Begin);
        targetStream.Seek(targetOffset, SeekOrigin.Begin);

        Console.WriteLine($"Running full verification with buffer size {FormatBytes(bufferSizeBytes)}");

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(bufferSizeBytes, remaining);
            var sourceRead = ReadExact(sourceStream, sourceBuffer, toRead);
            var targetRead = ReadExact(targetStream, targetBuffer, toRead);

            if (sourceRead != targetRead)
            {
                Console.WriteLine($"Read length mismatch at +{processed:N0}: source={sourceRead}, target={targetRead}");
                return false;
            }

            sourceSha.TransformBlock(sourceBuffer, 0, sourceRead, null, 0);
            targetSha.TransformBlock(targetBuffer, 0, targetRead, null, 0);

            var mismatchIndex = FindMismatchIndex(sourceBuffer, targetBuffer, sourceRead);
            if (mismatchIndex >= 0)
            {
                var absoluteDelta = processed + mismatchIndex;
                Console.WriteLine($"Byte mismatch at +{absoluteDelta:N0} in compared range.");
                Console.WriteLine($"Absolute source offset: {(sourceOffset + absoluteDelta):N0}");
                Console.WriteLine($"Absolute target offset: {(targetOffset + absoluteDelta):N0}");
                return false;
            }

            processed += sourceRead;
            remaining -= sourceRead;

            var percent = (long)(processed * 100 / length);
            if (percent != lastReportedPercent && percent % 5 == 0)
            {
                lastReportedPercent = percent;
                Console.WriteLine($"Full progress: {percent}% ({FormatBytes(processed)} / {FormatBytes(length)})");
            }
        }

        sourceSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        targetSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        var sourceHash = sourceSha.Hash ?? Array.Empty<byte>();
        var targetHash = targetSha.Hash ?? Array.Empty<byte>();
        Console.WriteLine($"Source SHA256: {Convert.ToHexString(sourceHash)}");
        Console.WriteLine($"Target SHA256: {Convert.ToHexString(targetHash)}");

        return sourceHash.SequenceEqual(targetHash);
    }

    private static byte[] HashRegion(FileStream stream, long offset, long length, byte[] buffer)
    {
        using var sha = SHA256.Create();
        stream.Seek(offset, SeekOrigin.Begin);
        long remaining = length;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = ReadExact(stream, buffer, toRead);
            sha.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash ?? Array.Empty<byte>();
    }

    private static long? FindFirstDifference(
        FileStream sourceStream,
        FileStream targetStream,
        long sourceOffset,
        long targetOffset,
        long length)
    {
        const int chunkSize = 64 * 1024;
        var sourceBuffer = new byte[chunkSize];
        var targetBuffer = new byte[chunkSize];
        long processed = 0;

        sourceStream.Seek(sourceOffset, SeekOrigin.Begin);
        targetStream.Seek(targetOffset, SeekOrigin.Begin);

        while (processed < length)
        {
            var toRead = (int)Math.Min(chunkSize, length - processed);
            var sourceRead = ReadExact(sourceStream, sourceBuffer, toRead);
            var targetRead = ReadExact(targetStream, targetBuffer, toRead);

            if (sourceRead != targetRead)
                return processed;

            var mismatchIndex = FindMismatchIndex(sourceBuffer, targetBuffer, sourceRead);
            if (mismatchIndex >= 0)
                return processed + mismatchIndex;

            processed += sourceRead;
        }

        return null;
    }

    private static int FindMismatchIndex(byte[] left, byte[] right, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (left[i] != right[i])
                return i;
        }

        return -1;
    }

    private static int ReadExact(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read <= 0)
                throw new IOException($"Unexpected end of stream while reading {count} bytes (got {total}).");
            total += read;
        }

        return total;
    }

    private static FileStream OpenDiskStream(int diskNumber)
    {
        var path = $@"\\.\PhysicalDrive{diskNumber}";
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.SequentialScan);
    }

    private static void ListDisks()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Index, Model, Size FROM Win32_DiskDrive");
        using var results = searcher.Get();

        Console.WriteLine("Disks:");
        foreach (ManagementObject disk in results)
        {
            using (disk)
            {
                var index = Convert.ToInt32(disk["Index"], CultureInfo.InvariantCulture);
                var model = disk["Model"]?.ToString() ?? "Unknown";
                var sizeBytes = Convert.ToInt64(disk["Size"] ?? 0, CultureInfo.InvariantCulture);
                Console.WriteLine($"  Disk {index}: {model} ({FormatBytes(sizeBytes)})");
            }
        }
    }

    private static void ListPartitions(int diskNumber)
    {
        var partitions = QueryPartitions(diskNumber)
            .OrderBy(p => p.PartitionNumber)
            .ToList();

        if (partitions.Count == 0)
        {
            Console.WriteLine($"No partitions found for disk {diskNumber}.");
            return;
        }

        Console.WriteLine($"Partitions on disk {diskNumber}:");
        Console.WriteLine("  #   Type        Size         Offset");
        foreach (var partition in partitions)
        {
            Console.WriteLine($"  {partition.PartitionNumber,-3} {TrimType(partition.TypeName),-10} {FormatBytes(partition.SizeBytes),-12} {partition.StartingOffset:N0}");
        }
    }

    private static PartitionDescriptor GetPartitionDescriptor(int diskNumber, int partitionNumber)
    {
        var partition = QueryPartitions(diskNumber)
            .FirstOrDefault(p => p.PartitionNumber == partitionNumber);

        if (partition == null)
        {
            throw new InvalidOperationException($"Partition {partitionNumber} was not found on disk {diskNumber}.");
        }

        return partition;
    }

    private static IEnumerable<PartitionDescriptor> QueryPartitions(int diskNumber)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT Index, StartingOffset, Size, Type FROM Win32_DiskPartition WHERE DiskIndex = {diskNumber}");
        using var results = searcher.Get();

        foreach (ManagementObject partition in results)
        {
            using (partition)
            {
                int index;
                long startingOffset;
                long sizeBytes;

                try
                {
                    index = Convert.ToInt32(partition["Index"], CultureInfo.InvariantCulture);
                    startingOffset = Convert.ToInt64(partition["StartingOffset"], CultureInfo.InvariantCulture);
                    sizeBytes = Convert.ToInt64(partition["Size"], CultureInfo.InvariantCulture);
                }
                catch
                {
                    continue;
                }

                var typeName = partition["Type"]?.ToString() ?? "Unknown";
                yield return new PartitionDescriptor(index + 1, startingOffset, sizeBytes, typeName);
            }
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            var token = arg[2..];
            var separator = token.IndexOf('=');
            if (separator >= 0)
            {
                var key = token[..separator];
                var value = token[(separator + 1)..];
                result[key] = value;
                continue;
            }

            var keyOnly = token;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[keyOnly] = args[i + 1];
                i++;
            }
            else
            {
                result[keyOnly] = "true";
            }
        }

        return result;
    }

    private static string GetValueOrDefault(Dictionary<string, string> options, string key, string fallback)
        => options.TryGetValue(key, out var value) ? value : fallback;

    private static bool TryGetRequiredInt(Dictionary<string, string> options, string key, out int value)
    {
        value = default;
        return options.TryGetValue(key, out var raw) && TryParseInt(raw, out value);
    }

    private static bool TryGetRequiredLong(Dictionary<string, string> options, string key, out long value)
    {
        value = default;
        return options.TryGetValue(key, out var raw) && TryParseLong(raw, out value);
    }

    private static bool TryGetInt(Dictionary<string, string> options, string key, out int value)
    {
        value = default;
        return options.TryGetValue(key, out var raw) && TryParseInt(raw, out value);
    }

    private static bool TryGetLong(Dictionary<string, string> options, string key, out long value)
    {
        value = default;
        return options.TryGetValue(key, out var raw) && TryParseLong(raw, out value);
    }

    private static bool TryParseInt(string raw, out int value)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParseLong(string raw, out long value)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int UsageError(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine();
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("DiskCloner.VerifyHelper");
        Console.WriteLine();
        Console.WriteLine("List mode:");
        Console.WriteLine("  --list-disks");
        Console.WriteLine("  --list-partitions <diskNumber>");
        Console.WriteLine();
        Console.WriteLine("Verify mode:");
        Console.WriteLine("  --source-disk <N> --target-disk <N>");
        Console.WriteLine("  [--source-partition <N> | --source-offset <bytes>]");
        Console.WriteLine("  [--target-partition <N> | --target-offset <bytes>]");
        Console.WriteLine("  [--length <bytes>]                     (optional if both partitions are specified)");
        Console.WriteLine("  [--mode sample|full]                   (default: sample)");
        Console.WriteLine("  [--samples <count>]                    (sample mode, default: 100)");
        Console.WriteLine("  [--sample-size-mb <MB>]                (sample mode, default: 1)");
        Console.WriteLine("  [--buffer-size-mb <MB>]                (full mode, default: 8)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  --list-partitions 0");
        Console.WriteLine("  --source-disk 0 --target-disk 1 --source-partition 2 --target-partition 3 --mode sample --samples 100");
        Console.WriteLine("  --source-disk 0 --target-disk 1 --source-offset 122683392 --target-offset 121634816 --length 110356327936 --mode full");
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double value = bytes;
        while (value >= 1024 && order < sizes.Length - 1)
        {
            value /= 1024;
            order++;
        }

        return $"{value:0.##} {sizes[order]}";
    }

    private static string TrimType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unknown";

        return value.Length <= 10 ? value : value[..10];
    }
}
