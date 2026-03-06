using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using DiskCloner.Core.Utilities;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskCloner.Core.Services;

/// <summary>
/// Encapsulates all diskpart.exe interactions and partition layout management for the target disk.
/// </summary>
public class DiskpartService : IDiskpartService
{
    private const long OneMiB = 1024 * 1024;

    private static readonly Regex DiskPartPartitionLineRegex = new(
        @"^\s*\*?\s*Partition\s+(?<number>\d+)\s+(?<type>.+?)\s+(?<sizeValue>\d+[\d.,]*)\s+(?<sizeUnit>KB|MB|GB|TB|B|Bytes)?\s+(?<offsetValue>\d+[\d.,]*)\s+(?<offsetUnit>KB|MB|GB|TB|B|Bytes)?(\s+.*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;

    public DiskpartService(ILogger logger, CancellationToken cancellationToken = default)
    {
        _logger = logger;
        _cancellationToken = cancellationToken;
    }

    // ── Target disk clearing ──────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task ClearTargetDiskAsync(CloneOperation operation)
    {
        _logger.Info("Clearing target disk...");
        var path = $@"\\.\PhysicalDrive{operation.TargetDisk.DiskNumber}";

        await Task.Run(() =>
        {
            using var handle = WindowsApi.CreateFile(
                path, WindowsApi.GENERIC_WRITE, WindowsApi.FILE_SHARE_WRITE,
                IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
                throw new IOException($"Failed to open target disk: {WindowsApi.GetLastErrorMessage()}");

            var bufferSize = 1024 * 1024;
            var buffer = new byte[bufferSize];

            uint bytesWritten;
            bool result = WindowsApi.WriteFile(handle, buffer, (uint)bufferSize, out bytesWritten, IntPtr.Zero);

            if (!result)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"Failed to clear target disk: {WindowsApi.GetErrorMessage((uint)error)} (Error {error})");
            }

            WindowsApi.FlushFileBuffers(handle);
            _logger.Info($"Cleared {bytesWritten} bytes from target disk");
        });
    }

    // ── Partition table creation ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task CreatePartitionTableAsync(CloneOperation operation)
    {
        _logger.Info("Creating partition table on target...");
        await CreatePartitionsViaDiskpartAsync(operation);
    }

    private async Task CreatePartitionsViaDiskpartAsync(CloneOperation operation)
    {
        var scriptPath = Path.GetTempFileName();
        var scriptContent = new StringBuilder();
        var orderedSourcePartitions = operation.PartitionsToClone
            .OrderBy(p => p.StartingOffset)
            .ToList();

        scriptContent.AppendLine($"select disk {operation.TargetDisk.DiskNumber}");
        scriptContent.AppendLine("online disk noerr");
        scriptContent.AppendLine("attributes disk clear readonly noerr");
        scriptContent.AppendLine("clean");
        scriptContent.AppendLine(operation.SourceDisk.IsGpt ? "convert gpt noerr" : "convert mbr noerr");

        foreach (var partition in orderedSourcePartitions)
        {
            var sizeMB = GetDiskPartSizeMegabytes(partition.TargetSizeBytes);

            if (partition.IsEfiPartition)
                scriptContent.AppendLine($"create partition efi size={sizeMB}");
            else if (partition.IsMsrPartition)
                scriptContent.AppendLine($"create partition msr size={sizeMB}");
            else if (partition.IsRecoveryPartition)
            {
                scriptContent.AppendLine($"create partition primary size={sizeMB}");
                scriptContent.AppendLine("set id=de94bba4-06d1-4d40-a16a-bfd50179d6ac override");
                scriptContent.AppendLine("gpt attributes=0x8000000000000001");
            }
            else
                scriptContent.AppendLine($"create partition primary size={sizeMB}");
        }

        scriptContent.AppendLine("list partition");

        await File.WriteAllTextAsync(scriptPath, scriptContent.ToString(), _cancellationToken);
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new IOException("Failed to start diskpart.exe");

            var output = await process.StandardOutput.ReadToEndAsync(_cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(_cancellationToken);
            await process.WaitForExitAsync(_cancellationToken);

            _logger.Info($"DiskPart output: {output}");

            if (process.ExitCode != 0)
            {
                _logger.Error($"diskpart failed with code {process.ExitCode}. Error: {error}");
                _logger.Error($"DiskPart script was:\n{scriptContent}");
                throw new IOException($"Failed to create partitions: DiskPart error {process.ExitCode}. See logs for details.");
            }

            var targetPartitions = await QueryTargetPartitionLayoutAsync(operation);
            if (targetPartitions.Count == 0)
            {
                _logger.Warning("Primary target layout query returned no rows. Falling back to diskpart output parsing.");
                targetPartitions = ParsePartitionTableFromDiskPartOutput(output);
            }
            if (targetPartitions.Count == 0)
                throw new InvalidOperationException("Could not read target partition layout after diskpart creation.");

            ApplyTargetPartitionOffsets(operation, targetPartitions);
            _logger.Info("Partitions created successfully");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    // ── Layout queries ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task ApplyTargetPartitionOffsetsAsync(CloneOperation operation)
    {
        var targetPartitions = await QueryTargetPartitionLayoutAsync(operation);
        if (targetPartitions.Count == 0)
            throw new InvalidOperationException("Could not read target partition layout.");

        ApplyTargetPartitionOffsets(operation, targetPartitions);
    }

    private async Task<List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>>
        QueryTargetPartitionLayoutAsync(CloneOperation operation)
    {
        var fromPowerShell = await QueryTargetPartitionLayoutViaPowerShellAsync(operation);
        if (fromPowerShell.Count > 0)
            return fromPowerShell;

        _logger.Warning("PowerShell Get-Partition layout query returned no rows; falling back to WMI.");
        return await QueryTargetPartitionLayoutViaWmiAsync(operation);
    }

    private async Task<List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>>
        QueryTargetPartitionLayoutViaPowerShellAsync(CloneOperation operation)
    {
        var command =
            $"$ErrorActionPreference = 'Stop'; " +
            $"Get-Partition -DiskNumber {operation.TargetDisk.DiskNumber} | " +
            "Sort-Object Offset | " +
            "Select-Object PartitionNumber,Type,Offset,Size | " +
            "ConvertTo-Json -Compress";

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                return new();

            var stdout = await process.StandardOutput.ReadToEndAsync(_cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(_cancellationToken);
            await process.WaitForExitAsync(_cancellationToken);

            if (process.ExitCode != 0)
            {
                _logger.Warning($"Get-Partition query failed with code {process.ExitCode}. StdErr: {stderr}");
                return new();
            }

            var parsed = ParseTargetPartitionLayoutJson(stdout);
            if (parsed.Count == 0)
                _logger.Warning($"Get-Partition query returned no parseable rows. Raw output: {stdout}");

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Get-Partition layout query failed: {ex.Message}");
            return new();
        }
    }

    private async Task<List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>>
        QueryTargetPartitionLayoutViaWmiAsync(CloneOperation operation)
    {
        return await Task.Run(() =>
        {
            var partitions = new List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>();
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Index, Type, Size, StartingOffset FROM Win32_DiskPartition WHERE DiskIndex = {operation.TargetDisk.DiskNumber}");
            using var results = searcher.Get();

            foreach (ManagementObject partitionObj in results)
            {
                using (partitionObj)
                {
                    try
                    {
                        var partitionIndex = Convert.ToInt32(partitionObj["Index"], CultureInfo.InvariantCulture);
                        var sizeBytes = Convert.ToInt64(partitionObj["Size"], CultureInfo.InvariantCulture);
                        var offsetBytes = Convert.ToInt64(partitionObj["StartingOffset"], CultureInfo.InvariantCulture);

                        if (partitionIndex < 0 || sizeBytes <= 0 || offsetBytes < 0)
                            continue;

                        var typeName = NormalizeDiskPartType(partitionObj["Type"]?.ToString() ?? string.Empty);
                        partitions.Add((partitionIndex + 1, typeName, sizeBytes, offsetBytes));
                    }
                    catch { continue; }
                }
            }

            return partitions.OrderBy(p => p.StartingOffsetBytes).ToList();
        }, _cancellationToken);
    }

    // ── Partition offset mapping ──────────────────────────────────────────────

    private void ApplyTargetPartitionOffsets(
        CloneOperation operation,
        List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)> targetPartitions)
    {
        var sourcePartitions = operation.PartitionsToClone.OrderBy(p => p.StartingOffset).ToList();

        if (targetPartitions.Count < sourcePartitions.Count)
            throw new InvalidOperationException(
                $"Partition mapping failed: expected at least {sourcePartitions.Count} target partitions, " +
                $"but target layout query returned {targetPartitions.Count}. Aborting to avoid incorrect writes.");

        if (targetPartitions.Count > sourcePartitions.Count)
            _logger.Warning(
                $"Target layout query returned {targetPartitions.Count} partitions but only {sourcePartitions.Count} are scheduled. " +
                "Matching source partitions by expected type and order.");

        int targetSearchStart = 0;
        long lastAssignedOffset = -1;

        for (int i = 0; i < sourcePartitions.Count; i++)
        {
            var sourcePartition = sourcePartitions[i];
            var expectedType = GetExpectedDiskPartType(sourcePartition);
            var mappedTargetIndex = -1;

            for (int targetIndex = targetSearchStart; targetIndex < targetPartitions.Count; targetIndex++)
            {
                if (targetPartitions[targetIndex].TypeName == expectedType)
                {
                    mappedTargetIndex = targetIndex;
                    break;
                }
            }

            if (mappedTargetIndex == -1)
            {
                mappedTargetIndex = targetSearchStart;
                _logger.Warning(
                    $"Could not find target partition type '{expectedType}' for source partition [{sourcePartition.PartitionNumber}]. " +
                    $"Falling back to next target partition {targetPartitions[mappedTargetIndex].PartitionNumber} ({targetPartitions[mappedTargetIndex].TypeName}).");
            }

            var targetPartition = targetPartitions[mappedTargetIndex];

            if (targetPartition.StartingOffsetBytes <= 0)
                throw new InvalidOperationException($"Parsed target partition {targetPartition.PartitionNumber} has invalid starting offset {targetPartition.StartingOffsetBytes}.");

            if (lastAssignedOffset >= 0 && targetPartition.StartingOffsetBytes < lastAssignedOffset)
                throw new InvalidOperationException($"Parsed target partition offsets are not strictly increasing (partition {targetPartition.PartitionNumber} offset {targetPartition.StartingOffsetBytes}). Aborting.");

            if (!operation.AllowSmallerTarget && targetPartition.SizeBytes < sourcePartition.SizeBytes)
                throw new InvalidOperationException($"Target partition {targetPartition.PartitionNumber} is smaller ({targetPartition.SizeBytes} bytes) than source partition {sourcePartition.PartitionNumber} ({sourcePartition.SizeBytes} bytes). Aborting.");

            sourcePartition.TargetStartingOffset = targetPartition.StartingOffsetBytes;
            sourcePartition.TargetPartitionNumber = targetPartition.PartitionNumber;
            lastAssignedOffset = targetPartition.StartingOffsetBytes;
            targetSearchStart = mappedTargetIndex + 1;

            _logger.Info(
                $"Partition mapping: source [{sourcePartition.PartitionNumber}] offset {sourcePartition.StartingOffset} -> " +
                $"target partition {targetPartition.PartitionNumber} ({targetPartition.TypeName}) offset {targetPartition.StartingOffsetBytes}");
        }
    }

    /// <inheritdoc />
    public long GetRequiredTargetStartingOffset(PartitionInfo partition)
    {
        if (partition.TargetStartingOffset <= 0)
            throw new InvalidOperationException(
                $"Target partition offset is not initialized for source partition {partition.PartitionNumber}. " +
                "Aborting to avoid writing data to the wrong location.");

        return partition.TargetStartingOffset;
    }

    // ── Parsing helpers ───────────────────────────────────────────────────────

    public static long GetDiskPartSizeMegabytes(long sizeBytes)
    {
        if (sizeBytes <= 0)
            throw new InvalidOperationException($"Invalid partition size for diskpart: {sizeBytes} bytes.");

        return Math.Max(1L, (sizeBytes + OneMiB - 1) / OneMiB);
    }

    public static List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>
        ParseTargetPartitionLayoutJson(string json)
    {
        var result = new List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        using var doc = JsonDocument.Parse(json.Trim());

        static void TryAdd(JsonElement element, List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)> dest)
        {
            if (!TryReadInt32Json(element, "PartitionNumber", out var partitionNumber)) return;
            if (!TryReadInt64Json(element, "Size", out var sizeBytes)) return;
            if (!TryReadInt64Json(element, "Offset", out var offsetBytes)) return;

            string typeText = element.TryGetProperty("Type", out var typeProp) ? typeProp.ToString() : string.Empty;
            dest.Add((partitionNumber, NormalizeDiskPartType(typeText), sizeBytes, offsetBytes));
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var element in doc.RootElement.EnumerateArray())
                TryAdd(element, result);
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            TryAdd(doc.RootElement, result);

        return result.OrderBy(p => p.StartingOffsetBytes).ToList();
    }

    internal static List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>
        ParsePartitionTableFromDiskPartOutput(string output)
    {
        var result = new List<(int PartitionNumber, string TypeName, long SizeBytes, long StartingOffsetBytes)>();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        foreach (var rawLine in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var match = DiskPartPartitionLineRegex.Match(line);
            if (!match.Success)
            {
                var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 6 && tokens[0].Equals("Partition", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                {
                    var tail = tokens.Skip(Math.Max(0, tokens.Length - 4)).ToArray();
                    var typeTokens = tokens.Skip(2).Take(tokens.Length - 6).ToArray();
                    var typeText = typeTokens.Length > 0 ? string.Join(' ', typeTokens) : tokens[2];

                    if (TryParseSizeToBytes(tail[0], tail[1], out var sBytes) && TryParseSizeToBytes(tail[2], tail[3], out var oBytes))
                        result.Add((num, NormalizeDiskPartType(typeText), sBytes, oBytes));
                }
                continue;
            }

            try
            {
                var partitionNumber = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
                var typeName = NormalizeDiskPartType(match.Groups["type"].Value);

                if (!TryParseSizeToBytes(match.Groups["sizeValue"].Value, match.Groups["sizeUnit"].Value, out var sizeBytes)) continue;
                if (!TryParseSizeToBytes(match.Groups["offsetValue"].Value, match.Groups["offsetUnit"].Value, out var offsetBytes)) continue;

                result.Add((partitionNumber, typeName, sizeBytes, offsetBytes));
            }
            catch { }
        }

        return result.OrderBy(p => p.StartingOffsetBytes).ToList();
    }

    private static string GetExpectedDiskPartType(PartitionInfo partition)
    {
        if (partition.IsMsrPartition) return "Reserved";
        if (partition.IsEfiPartition) return "System";
        if (partition.IsRecoveryPartition) return "Recovery";
        return "Primary";
    }

    public static string NormalizeDiskPartType(string typeText)
    {
        var normalized = typeText.Trim().ToLowerInvariant();
        if (normalized.Contains("reserved")) return "Reserved";
        if (normalized.Contains("system")) return "System";
        if (normalized.Contains("recovery")) return "Recovery";
        if (normalized.Contains("basic") || normalized.Contains("primary")) return "Primary";
        return typeText.Trim();
    }

    public static bool TryParseSizeToBytes(string valueText, string unitText, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(valueText)) return false;

        if (!double.TryParse(valueText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        double multiplier = (unitText ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "B" or "BYTES" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            "TB" => 1024d * 1024d * 1024d * 1024d,
            "" => 1d,
            _ => 0d
        };

        if (multiplier <= 0d) return false;

        try
        {
            bytes = checked((long)Math.Round(value * multiplier, MidpointRounding.AwayFromZero));
            return true;
        }
        catch { return false; }
    }

    private static bool TryReadInt32Json(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetInt32(out value);
        if (property.ValueKind == JsonValueKind.String) return int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        return false;
    }

    private static bool TryReadInt64Json(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetInt64(out value);
        if (property.ValueKind == JsonValueKind.String) return long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        return false;
    }
}
