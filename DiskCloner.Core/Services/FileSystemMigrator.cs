using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskCloner.Core.Services;

/// <summary>
/// Migrates an NTFS partition using Robocopy when raw copy cannot be used (shrunk target).
/// Depends on ICloneValidator for mutation guards and SystemQuietModeService's RunProcessAsync
/// for diskpart/chkdsk/bcdboot invocations.
/// </summary>
public class FileSystemMigrator : IFileSystemMigrator
{
    private readonly ILogger _logger;
    private readonly VssSnapshotService _vssService;
    private readonly ICloneValidator _validator;
    private readonly Action<CloneProgress> _reportProgress;
    private readonly CancellationToken _cancellationToken;

    private static readonly Regex RobocopyTimestampedErrorRegex = new(
        @"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2} ERROR",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FileSystemMigrator(
        ILogger logger,
        VssSnapshotService vssService,
        ICloneValidator validator,
        Action<CloneProgress> reportProgress,
        CancellationToken cancellationToken = default)
    {
        _logger = logger;
        _vssService = vssService;
        _validator = validator;
        _reportProgress = reportProgress;
        _cancellationToken = cancellationToken;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<long> MigrateAsync(
        CloneOperation operation,
        PartitionInfo partition,
        CloneResult result,
        CloneProgress progress,
        long totalBytesAlreadyCopied,
        long migrationPlannedBytes)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        if (!partition.DriveLetter.HasValue)
            throw new InvalidOperationException($"Cannot migrate partition {partition.PartitionNumber}: source drive letter is missing.");
        if (partition.TargetPartitionNumber <= 0)
            throw new InvalidOperationException($"Cannot migrate partition {partition.PartitionNumber}: target partition number is missing.");

        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "filesystem migration");

        var sourceRoot = await GetMigrationSourceRootAsync(operation, partition, result);
        var targetLetter = GetAvailableDriveLetter('W', 'V', 'T', 'R', 'Q');
        _validator.EnsureTargetVolumeMutationAllowed(operation, targetLetter, "filesystem migration target mount");

        await FormatAndMountTargetPartitionAsync(operation, partition.TargetPartitionNumber, targetLetter, "Windows");

        char? efiLetter = null;
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var migratedBytes = await CopyWithRobocopyAsync(
                sourceRoot,
                $"{targetLetter}:\\",
                progress,
                totalBytesAlreadyCopied,
                migrationPlannedBytes,
                targetLetter);

            await ValidateTargetVolumeAsync(targetLetter, "NTFS", operation, partition);

            var efi = operation.PartitionsToClone.FirstOrDefault(p => p.IsEfiPartition);
            if (efi != null && efi.TargetPartitionNumber > 0)
            {
                efiLetter = GetAvailableDriveLetter('S', 'P', 'O', 'N');
                _validator.EnsureTargetVolumeMutationAllowed(operation, efiLetter.Value, "EFI mount");
                await MountExistingTargetPartitionAsync(operation, efi.TargetPartitionNumber, efiLetter.Value);
                await RebuildBootFilesAsync(operation, targetLetter, efiLetter.Value);
            }

            return migratedBytes;
        }
        finally
        {
            if (efiLetter.HasValue)
                await UnmountTargetPartitionAsync(operation, efiLetter.Value);
            await UnmountTargetPartitionAsync(operation, targetLetter);
        }
    }

    // ── Migration helpers ─────────────────────────────────────────────────────

    private async Task<string> GetMigrationSourceRootAsync(CloneOperation operation, PartitionInfo partition, CloneResult result)
    {
        if (!partition.DriveLetter.HasValue)
            throw new InvalidOperationException($"Cannot migrate partition {partition.PartitionNumber}: source drive letter is missing.");

        var sourceRoot = $@"{partition.DriveLetter.Value}:\";
        if (!operation.UseSnapshotForFileMigration)
            return sourceRoot;

        var snapshot = _vssService.GetSnapshotVolumePath(sourceRoot);
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            var warning = $"Snapshot path unavailable for {sourceRoot}; using live source for file migration.";
            _logger.Warning(warning);
            result.Warnings.Add(warning);
            return sourceRoot;
        }

        if (snapshot.StartsWith(@"\\?\GLOBALROOT\", StringComparison.OrdinalIgnoreCase))
        {
            var exposed = await _vssService.ExposeSnapshotVolumeAsync(sourceRoot);
            if (!string.IsNullOrWhiteSpace(exposed))
            {
                _logger.Info($"Using exposed snapshot mount '{exposed}' for migration source {sourceRoot}");
                return EnsureTrailingBackslash(exposed);
            }

            var warning = $"Snapshot path '{snapshot}' could not be exposed; using live source {sourceRoot} for migration.";
            _logger.Warning(warning);
            result.Warnings.Add(warning);
            return sourceRoot;
        }

        return EnsureTrailingBackslash(snapshot);
    }

    private async Task FormatAndMountTargetPartitionAsync(
        CloneOperation operation, int targetPartitionNumber, char mountLetter, string label)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "format target partition");
        _validator.EnsureTargetVolumeMutationAllowed(operation, mountLetter, "format target partition");

        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine($"select partition {targetPartitionNumber}")
            .AppendLine($"format fs=ntfs quick label=\"{NormalizeDriveLetter(mountLetter)}\"")
            .AppendLine($"assign letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        _validator.AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "format and mount target partition");
        await RunDiskpartScriptAsync(script, "format+mount", $"format/mount target partition {targetPartitionNumber}");
    }

    private async Task MountExistingTargetPartitionAsync(
        CloneOperation operation, int targetPartitionNumber, char mountLetter)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "mount target partition");
        _validator.EnsureTargetVolumeMutationAllowed(operation, mountLetter, "mount target partition");

        var script = new StringBuilder()
            .AppendLine($"select disk {operation.TargetDisk.DiskNumber}")
            .AppendLine($"select partition {targetPartitionNumber}")
            .AppendLine($"assign letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        _validator.AssertDiskpartScriptTargetsOnlyTargetDisk(operation, script, "mount target partition");
        await RunDiskpartScriptAsync(script, "mount", $"mount target partition {targetPartitionNumber}");
    }

    private async Task UnmountTargetPartitionAsync(CloneOperation operation, char mountLetter)
    {
        _validator.EnsureTargetDiskMutationAllowed(operation, operation.TargetDisk.DiskNumber, "unmount target partition");
        _validator.EnsureTargetVolumeMutationAllowed(operation, mountLetter, "unmount target partition");

        var script = new StringBuilder()
            .AppendLine($"select volume {NormalizeDriveLetter(mountLetter)}")
            .AppendLine($"remove letter={NormalizeDriveLetter(mountLetter)}")
            .ToString();

        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            if (exitCode != 0)
                _logger.Warning($"Failed to unmount {mountLetter}:. Code={exitCode}. Output={output}. Error={error}");
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    private async Task RunDiskpartScriptAsync(string script, string logTag, string errorContext)
    {
        var scriptPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(scriptPath, script);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };

            var (exitCode, output, error) = await RunProcessAsync(startInfo);
            _logger.Info($"DiskPart {logTag} output: {output}");
            if (exitCode != 0)
                throw new IOException($"Failed to {errorContext}. ExitCode={exitCode}. Error={error}");
        }
        finally { try { File.Delete(scriptPath); } catch { } }
    }

    // ── Robocopy ──────────────────────────────────────────────────────────────

    private async Task<long> CopyWithRobocopyAsync(
        string sourceRoot, string targetRoot,
        CloneProgress progress, long totalBytesAlreadyCopied,
        long migrationPlannedBytes, char targetLetter)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(ToRobocopyPath(EnsureTrailingBackslash(sourceRoot)));
        startInfo.ArgumentList.Add(ToRobocopyPath(EnsureTrailingBackslash(targetRoot)));
        startInfo.ArgumentList.Add("/MIR");
        startInfo.ArgumentList.Add("/COPY:DATSO");
        startInfo.ArgumentList.Add("/DCOPY:DAT");
        startInfo.ArgumentList.Add("/ZB");
        startInfo.ArgumentList.Add("/R:0");
        startInfo.ArgumentList.Add("/W:0");
        startInfo.ArgumentList.Add("/XJ");
        startInfo.ArgumentList.Add("/SL");
        startInfo.ArgumentList.Add("/XF");
        startInfo.ArgumentList.Add("pagefile.sys");
        startInfo.ArgumentList.Add("hiberfil.sys");
        startInfo.ArgumentList.Add("swapfile.sys");

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Failed to start robocopy.exe");

        var outputLock = new object();
        var outputTail = new Queue<string>();
        var errorTail = new Queue<string>();
        const int maxCapturedTailLines = 500;
        long outputLineCount = 0, stderrLineCount = 0;
        long benignErrorZeroLineCount = 0, suspiciousStdoutErrorLineCount = 0;
        long lastOutputTicksUtc = DateTime.UtcNow.Ticks;

        static void EnqueueTail(Queue<string> queue, string line)
        {
            queue.Enqueue(line);
            if (queue.Count > maxCapturedTailLines)
                queue.Dequeue();
        }

        Task PumpStreamAsync(StreamReader reader, Queue<string> tail, bool isError)
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    lock (outputLock) { EnqueueTail(tail, line); }
                    Interlocked.Exchange(ref lastOutputTicksUtc, DateTime.UtcNow.Ticks);

                    if (isError)
                    {
                        var idx = Interlocked.Increment(ref stderrLineCount);
                        if (idx <= 50 || idx % 200 == 0)
                            _logger.Warning($"Robocopy stderr: {line}");
                        continue;
                    }

                    Interlocked.Increment(ref outputLineCount);
                    if (IsBenignRobocopyErrorZeroLine(line)) { Interlocked.Increment(ref benignErrorZeroLineCount); continue; }
                    if (IsSuspiciousRobocopyStdoutLine(line))
                    {
                        var idx = Interlocked.Increment(ref suspiciousStdoutErrorLineCount);
                        if (idx <= 20 || idx % 100 == 0)
                            _logger.Warning($"Robocopy stdout: {line}");
                    }
                }
            });
        }

        var outputTask = PumpStreamAsync(process.StandardOutput, outputTail, false);
        var errorTask = PumpStreamAsync(process.StandardError, errorTail, true);

        var lastSampleAt = DateTime.UtcNow;
        var baselineUsed = GetVolumeUsedBytes(targetLetter);
        var lastObservedMigratedBytes = 0L;
        var effectivePlannedBytes = Math.Max(1L, migrationPlannedBytes);
        double? smoothedThroughput = null;
        const double alpha = 0.20;
        var lastGrowthAt = DateTime.UtcNow;
        var lastNoGrowthWarningAt = DateTime.MinValue;

        while (!process.HasExited)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new OperationCanceledException("Operation was cancelled by user");
            }

            var now = DateTime.UtcNow;
            var usedNow = GetVolumeUsedBytes(targetLetter);
            var observedMigratedBytes = Math.Max(0L, usedNow - baselineUsed);
            if (observedMigratedBytes > effectivePlannedBytes) effectivePlannedBytes = observedMigratedBytes;

            var projectedTotalBytes = totalBytesAlreadyCopied + observedMigratedBytes;
            if (projectedTotalBytes > progress.TotalBytes) progress.TotalBytes = projectedTotalBytes;

            var deltaBytes = Math.Max(0L, observedMigratedBytes - lastObservedMigratedBytes);
            var deltaSeconds = Math.Max(0.001d, (now - lastSampleAt).TotalSeconds);
            var instantThroughput = deltaBytes / deltaSeconds;
            smoothedThroughput = smoothedThroughput.HasValue
                ? (alpha * instantThroughput) + ((1.0 - alpha) * smoothedThroughput.Value)
                : instantThroughput;

            if (observedMigratedBytes > lastObservedMigratedBytes) lastGrowthAt = now;

            var noGrowthDuration = now - lastGrowthAt;
            var lastOutputAt = new DateTime(Interlocked.Read(ref lastOutputTicksUtc), DateTimeKind.Utc);
            var noOutputDuration = now - lastOutputAt;

            if (noGrowthDuration >= TimeSpan.FromSeconds(45) &&
                now - lastNoGrowthWarningAt >= TimeSpan.FromSeconds(30))
            {
                _logger.Warning(
                    $"Robocopy migration has no target byte growth for {noGrowthDuration.TotalSeconds:0}s " +
                    $"(no output for {noOutputDuration.TotalSeconds:0}s).");
                lastNoGrowthWarningAt = now;
            }

            progress.BytesCopied = Math.Min(progress.TotalBytes, totalBytesAlreadyCopied + observedMigratedBytes);
            progress.ThroughputBytesPerSec = smoothedThroughput!.Value;
            progress.PercentComplete = Math.Min(99.9, (progress.BytesCopied * 100.0) / Math.Max(1, progress.TotalBytes));
            progress.EstimatedTimeRemaining = PartitionCopier.CalculateSafeEta(
                Math.Max(0L, progress.TotalBytes - progress.BytesCopied),
                progress.ThroughputBytesPerSec);
            progress.StatusMessage = noGrowthDuration >= TimeSpan.FromSeconds(20)
                ? "Migrating Windows partition (robocopy in progress, processing protected files/metadata)..."
                : "Migrating Windows partition (robocopy in progress)...";
            _reportProgress(progress);

            lastObservedMigratedBytes = observedMigratedBytes;
            lastSampleAt = now;

            await Task.Delay(1000, _cancellationToken);
        }

        await process.WaitForExitAsync();
        await Task.WhenAll(outputTask, errorTask);
        string outputStr, errorStr;
        lock (outputLock)
        {
            outputStr = string.Join(Environment.NewLine, outputTail);
            errorStr = string.Join(Environment.NewLine, errorTail);
        }
        var exitCode = process.ExitCode;
        _logger.Info(
            $"Robocopy finished. ExitCode={exitCode}, StdOut={outputLineCount}, " +
            $"StdErr={stderrLineCount}, BenignError0={benignErrorZeroLineCount}, " +
            $"Suspicious={suspiciousStdoutErrorLineCount}");
        _logger.Info($"Robocopy stdout tail: {outputStr}");
        if (!string.IsNullOrWhiteSpace(errorStr))
            _logger.Warning($"Robocopy stderr tail: {errorStr}");

        if (exitCode > 7)
            throw new IOException($"Robocopy migration failed with code {exitCode}. Error={errorStr}");

        var finalObserved = Math.Max(lastObservedMigratedBytes, Math.Max(0L, GetVolumeUsedBytes(targetLetter) - baselineUsed));
        return finalObserved;
    }

    // ── Post-migration validation & boot rebuild ──────────────────────────────

    private async Task ValidateTargetVolumeAsync(char targetLetter, string expectedFileSystem,
        CloneOperation operation, PartitionInfo partition)
    {
        _validator.EnsureTargetVolumeMutationAllowed(operation, targetLetter, "validate target volume");

        var startInfo = new ProcessStartInfo
        {
            FileName = "chkdsk.exe",
            Arguments = $"{NormalizeDriveLetter(targetLetter)}: /scan",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        _logger.Info($"CHKDSK output for {targetLetter}: {output}");
        if (exitCode != 0)
            throw new IOException($"chkdsk /scan failed for {targetLetter}: (code {exitCode}) {error}");

        var volume = GetVolumeByDriveLetter(targetLetter);
        var driveFormat = volume?.DriveFormat;
        if (volume == null || !string.Equals(driveFormat, expectedFileSystem, StringComparison.OrdinalIgnoreCase))
            throw new IOException(
                $"Target partition {partition.TargetPartitionNumber} validation failed. Expected {expectedFileSystem}, got '{driveFormat ?? "<null>"}'.");
    }

    private async Task RebuildBootFilesAsync(CloneOperation operation, char windowsLetter, char efiLetter)
    {
        _validator.EnsureTargetVolumeMutationAllowed(operation, windowsLetter, "bcdboot windows source");
        _validator.EnsureTargetVolumeMutationAllowed(operation, efiLetter, "bcdboot efi target");

        var windowsPath = $"{NormalizeDriveLetter(windowsLetter)}:\\Windows";
        var startInfo = new ProcessStartInfo
        {
            FileName = "bcdboot.exe",
            Arguments = $"\"{windowsPath}\" /s {NormalizeDriveLetter(efiLetter)}: /f UEFI /c",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        _logger.Info($"bcdboot output: {output}");
        if (exitCode != 0)
            throw new IOException($"bcdboot failed with code {exitCode}. Error={error}");
    }

    // ── Static helpers ────────────────────────────────────────────────────────

    private static char GetAvailableDriveLetter(params char[] preferredLetters)
    {
        var inUse = DriveInfo.GetDrives()
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => NormalizeDriveLetter(d.Name[0]))
            .ToHashSet();

        foreach (var letter in preferredLetters.Select(NormalizeDriveLetter))
            if (letter >= 'D' && letter <= 'Z' && !inUse.Contains(letter))
                return letter;

        for (char letter = 'Z'; letter >= 'D'; letter--)
            if (!inUse.Contains(letter))
                return letter;

        throw new InvalidOperationException("No free drive letter available for target partition mounting.");
    }

    private static DriveInfo? GetVolumeByDriveLetter(char driveLetter)
    {
        var normalized = NormalizeDriveLetter(driveLetter);
        return DriveInfo.GetDrives()
            .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Name) && NormalizeDriveLetter(d.Name[0]) == normalized);
    }

    private static long GetVolumeUsedBytes(char driveLetter)
    {
        try
        {
            var volume = GetVolumeByDriveLetter(driveLetter);
            if (volume == null || !volume.IsReady) return 0;
            return Math.Max(0L, volume.TotalSize - volume.AvailableFreeSpace);
        }
        catch { return 0; }
    }

    private static char NormalizeDriveLetter(char driveLetter) => char.ToUpperInvariant(driveLetter);

    private static string EnsureTrailingBackslash(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return path.EndsWith(@"\", StringComparison.Ordinal) ? path : path + @"\";
    }

    private static string ToRobocopyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return path.Trim();
    }

    private static bool IsBenignRobocopyErrorZeroLine(string line) =>
        !string.IsNullOrWhiteSpace(line) &&
        line.Contains("ERROR 0 (0x00000000) Copying File", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuspiciousRobocopyStdoutLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)) return true;
        if (RobocopyTimestampedErrorRegex.IsMatch(trimmed)) return true;
        return trimmed.Contains("ERROR 3 (0x00000003)", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("ERROR 5 (0x00000005)", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("ERROR 32 (0x00000020)", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("ERROR 123 (0x0000007B)", StringComparison.OrdinalIgnoreCase);
    }

    // Small static run-process helper (same pattern as SystemQuietModeService)
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ?? throw new IOException($"Failed to start {startInfo.FileName}");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }
}
