using DiskCloner.Core.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskCloner.Core.Services;

public sealed class RobocopyProbeFileResult
{
    public string LoggedPath { get; set; } = string.Empty;
    public string EffectiveSourcePath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Details { get; set; } = string.Empty;
}

public sealed class RobocopyProbeResult
{
    public string LogFilePath { get; set; } = string.Empty;
    public string DestinationRoot { get; set; } = string.Empty;
    public string SummaryFilePath { get; set; } = string.Empty;
    public int DiscoveredFailurePaths { get; set; }
    public int FilesTested { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public List<RobocopyProbeFileResult> Files { get; set; } = new();
}

public static class RobocopyFailureDiagnostics
{
    private static readonly Regex FailurePathRegex = new(
        @"ERROR\s+\d+\s+\(0x[0-9A-Fa-f]+\)\s+(?:Copying|Accessing)\s+(?:Source\s+)?File\s+(?<path>[A-Za-z]:\\.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ExtractProblematicFilePaths(IEnumerable<string> logLines, int maxFiles = 1000)
    {
        if (logLines == null)
            throw new ArgumentNullException(nameof(logLines));

        if (maxFiles <= 0)
            return Array.Empty<string>();

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extracted = new List<string>();

        foreach (var line in logLines)
        {
            if (!TryExtractProblematicFilePath(line, out var path))
                continue;

            if (!unique.Add(path))
                continue;

            extracted.Add(path);
            if (extracted.Count >= maxFiles)
                break;
        }

        return extracted;
    }

    public static bool TryExtractProblematicFilePath(string? logLine, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(logLine))
            return false;

        var match = FailurePathRegex.Match(logLine);
        if (!match.Success)
            return false;

        var candidate = match.Groups["path"].Value.Trim();
        if (candidate.Length < 4 || candidate[1] != ':' || candidate[2] != '\\')
            return false;

        path = candidate;
        return true;
    }

    public static string NormalizePathForProbe(
        string sourcePath,
        char fallbackDriveLetter,
        IReadOnlyCollection<char> availableDriveLetters)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return sourcePath;

        if (sourcePath.Length < 3 || sourcePath[1] != ':' || sourcePath[2] != '\\')
            return sourcePath;

        var sourceDrive = char.ToUpperInvariant(sourcePath[0]);
        if (availableDriveLetters.Contains(sourceDrive))
            return sourcePath;

        var fallbackDrive = char.ToUpperInvariant(fallbackDriveLetter);
        if (!char.IsLetter(fallbackDrive))
            fallbackDrive = 'C';

        return fallbackDrive + sourcePath.Substring(1);
    }
}

public sealed class RobocopyFailureProbeService
{
    private readonly ILogger _logger;

    public RobocopyFailureProbeService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RobocopyProbeResult> ProbeFromLogAsync(
        string logFilePath,
        string destinationRoot,
        int maxFiles,
        char fallbackSourceDriveLetter,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
            throw new ArgumentNullException(nameof(logFilePath));
        if (!File.Exists(logFilePath))
            throw new FileNotFoundException("Log file not found.", logFilePath);
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentNullException(nameof(destinationRoot));
        if (maxFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFiles), "Max files must be greater than zero.");

        Directory.CreateDirectory(destinationRoot);
        var availableDriveLetters = DriveInfo.GetDrives()
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && d.Name.Length >= 2 && d.Name[1] == ':')
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        var discoveredPaths = RobocopyFailureDiagnostics.ExtractProblematicFilePaths(File.ReadLines(logFilePath), maxFiles);
        var result = new RobocopyProbeResult
        {
            LogFilePath = logFilePath,
            DestinationRoot = destinationRoot,
            DiscoveredFailurePaths = discoveredPaths.Count
        };

        _logger.Info($"Starting robocopy failure probe from '{logFilePath}'. Paths discovered: {discoveredPaths.Count}.");

        foreach (var loggedPath in discoveredPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var effectivePath = RobocopyFailureDiagnostics.NormalizePathForProbe(
                loggedPath,
                fallbackSourceDriveLetter,
                availableDriveLetters);

            var item = new RobocopyProbeFileResult
            {
                LoggedPath = loggedPath,
                EffectiveSourcePath = effectivePath
            };

            if (!File.Exists(effectivePath))
            {
                item.Success = false;
                item.ExitCode = -1;
                item.Details = "Source file does not exist at probe path.";
                result.Files.Add(item);
                _logger.Warning($"Probe skip (missing source): {effectivePath}");
                continue;
            }

            var sourceDirectory = Path.GetDirectoryName(effectivePath);
            var fileName = Path.GetFileName(effectivePath);
            if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(fileName))
            {
                item.Success = false;
                item.ExitCode = -1;
                item.Details = "Invalid source path for robocopy.";
                result.Files.Add(item);
                _logger.Warning($"Probe skip (invalid source path): {effectivePath}");
                continue;
            }

            var targetDirectory = BuildProbeTargetDirectory(destinationRoot, effectivePath, fallbackSourceDriveLetter);
            Directory.CreateDirectory(targetDirectory);

            var (exitCode, stdout, stderr) = await RunRobocopySingleFileAsync(
                sourceDirectory,
                targetDirectory,
                fileName,
                cancellationToken);

            item.ExitCode = exitCode;
            item.Success = exitCode <= 7;
            item.Details = BuildDetails(stdout, stderr);
            result.Files.Add(item);

            if (item.Success)
            {
                _logger.Info($"Probe success (code {exitCode}): {effectivePath}");
            }
            else
            {
                _logger.Warning($"Probe failed (code {exitCode}): {effectivePath}");
            }
        }

        result.FilesTested = result.Files.Count;
        result.SuccessCount = result.Files.Count(f => f.Success);
        result.FailureCount = result.Files.Count(f => !f.Success);

        var summaryPath = Path.Combine(destinationRoot, "robocopy_probe_summary.txt");
        await File.WriteAllTextAsync(summaryPath, BuildSummary(result), cancellationToken);
        result.SummaryFilePath = summaryPath;

        _logger.Info(
            $"Robocopy failure probe finished. Tested={result.FilesTested}, Success={result.SuccessCount}, " +
            $"Failed={result.FailureCount}, Summary={summaryPath}");

        return result;
    }

    private static string BuildProbeTargetDirectory(string destinationRoot, string sourcePath, char fallbackSourceDriveLetter)
    {
        var normalizedRoot = Path.GetPathRoot(sourcePath);
        var rootLength = string.IsNullOrEmpty(normalizedRoot) ? 0 : normalizedRoot.Length;
        var relativePath = rootLength > 0 && sourcePath.Length > rootLength
            ? sourcePath.Substring(rootLength)
            : Path.GetFileName(sourcePath);
        var relativeDirectory = Path.GetDirectoryName(relativePath) ?? string.Empty;

        var rootDrive = fallbackSourceDriveLetter;
        if (!string.IsNullOrWhiteSpace(normalizedRoot) && normalizedRoot.Length >= 2 && normalizedRoot[1] == ':')
        {
            rootDrive = normalizedRoot[0];
        }

        return Path.Combine(destinationRoot, char.ToUpperInvariant(rootDrive).ToString(), relativeDirectory);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunRobocopySingleFileAsync(
        string sourceDirectory,
        string targetDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "robocopy.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(EnsureTrailingBackslash(sourceDirectory));
        startInfo.ArgumentList.Add(EnsureTrailingBackslash(targetDirectory));
        startInfo.ArgumentList.Add(fileName);
        startInfo.ArgumentList.Add("/R:0");
        startInfo.ArgumentList.Add("/W:0");
        startInfo.ArgumentList.Add("/ZB");
        startInfo.ArgumentList.Add("/COPY:DATSO");
        startInfo.ArgumentList.Add("/DCOPY:DAT");
        startInfo.ArgumentList.Add("/XJ");
        startInfo.ArgumentList.Add("/NFL");
        startInfo.ArgumentList.Add("/NDL");
        startInfo.ArgumentList.Add("/NP");
        startInfo.ArgumentList.Add("/NJH");
        startInfo.ArgumentList.Add("/NJS");

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Failed to start robocopy.exe for probe.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private static string BuildDetails(string stdout, string stderr)
    {
        static string Tail(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();
            if (lines.Length <= 3)
                return string.Join(" | ", lines);

            return string.Join(" | ", lines.Skip(lines.Length - 3));
        }

        var stdoutTail = Tail(stdout);
        var stderrTail = Tail(stderr);

        if (string.IsNullOrWhiteSpace(stdoutTail) && string.IsNullOrWhiteSpace(stderrTail))
            return "No output.";

        if (string.IsNullOrWhiteSpace(stderrTail))
            return $"stdout: {stdoutTail}";

        if (string.IsNullOrWhiteSpace(stdoutTail))
            return $"stderr: {stderrTail}";

        return $"stdout: {stdoutTail}; stderr: {stderrTail}";
    }

    private static string BuildSummary(RobocopyProbeResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Log: {result.LogFilePath}");
        sb.AppendLine($"Destination: {result.DestinationRoot}");
        sb.AppendLine($"Discovered paths: {result.DiscoveredFailurePaths}");
        sb.AppendLine($"Tested: {result.FilesTested}");
        sb.AppendLine($"Success: {result.SuccessCount}");
        sb.AppendLine($"Failed: {result.FailureCount}");
        sb.AppendLine();

        foreach (var file in result.Files)
        {
            var status = file.Success ? "OK" : "FAIL";
            sb.AppendLine($"{status} | code={file.ExitCode} | logged={file.LoggedPath} | source={file.EffectiveSourcePath}");
            if (!string.IsNullOrWhiteSpace(file.Details))
            {
                sb.AppendLine($"  {file.Details}");
            }
        }

        return sb.ToString();
    }

    private static string EnsureTrailingBackslash(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        return path.EndsWith(@"\", StringComparison.Ordinal) ? path : path + @"\";
    }
}
