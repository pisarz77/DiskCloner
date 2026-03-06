using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskCloner.Core.Services;

/// <summary>
/// Manages quiet mode: pauses OneDrive and background Windows services
/// before cloning, and restores them on exit.
/// </summary>
public class SystemQuietModeService : ISystemQuietModeService
{
    private readonly ILogger _logger;
    private readonly Action<CloneProgress> _reportProgress;

    private static readonly string[] QuietModeServiceNames =
    {
        "UsoSvc",    // Update Orchestrator
        "wuauserv",  // Windows Update
        "BITS",      // Background transfers
        "WSearch"    // Windows Search indexing
    };

    public SystemQuietModeService(ILogger logger, Action<CloneProgress> reportProgress)
    {
        _logger = logger;
        _reportProgress = reportProgress;
    }

    /// <inheritdoc />
    public async Task<QuietModeState> EnterAsync(CloneOperation operation, CloneResult result, CloneProgress progress)
    {
        _logger.Info("Entering source quiet mode (best-effort).");
        progress.StatusMessage = "Preparing quiet mode (pausing background writers)...";
        _reportProgress(progress);

        var state = new QuietModeState
        {
            OneDriveExecutablePath = ResolveOneDriveExecutablePath()
        };

        // Stop OneDrive process (if present) to reduce user-profile churn.
        var oneDriveStopInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "taskkill.exe",
            Arguments = "/IM OneDrive.exe /F",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var (oneDriveExit, oneDriveOut, oneDriveErr) = await RunProcessAsync(oneDriveStopInfo);
        var oneDriveCombined = $"{oneDriveOut}\n{oneDriveErr}";
        if (oneDriveExit == 0)
        {
            state.OneDriveStopped = true;
            _logger.Info("Quiet mode: OneDrive process stopped.");
        }
        else if (oneDriveCombined.Contains("not found", StringComparison.OrdinalIgnoreCase)
                 || oneDriveCombined.Contains("no running instance", StringComparison.OrdinalIgnoreCase)
                 || oneDriveCombined.Contains("not running", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info("Quiet mode: OneDrive process is not running.");
        }
        else
        {
            var warning = $"Quiet mode: failed to stop OneDrive process (code {oneDriveExit}).";
            _logger.Warning($"{warning} Output: {oneDriveCombined}");
            result.Warnings.Add(warning);
        }

        foreach (var serviceName in QuietModeServiceNames)
        {
            var stateCode = await QueryServiceStateCodeAsync(serviceName);
            if (!stateCode.HasValue)
            {
                _logger.Info($"Quiet mode: service '{serviceName}' not available on this system.");
                continue;
            }

            if (stateCode.Value != 4) // RUNNING
            {
                _logger.Info($"Quiet mode: service '{serviceName}' already not running.");
                continue;
            }

            if (await StopServiceBestEffortAsync(serviceName))
            {
                state.StoppedServices.Add(serviceName);
                _logger.Info($"Quiet mode: service '{serviceName}' paused.");
            }
            else
            {
                var warning = $"Quiet mode: could not pause service '{serviceName}'.";
                _logger.Warning(warning);
                result.Warnings.Add(warning);
            }
        }

        return state;
    }

    /// <inheritdoc />
    public async Task ExitAsync(QuietModeState? state, CloneResult result)
    {
        if (state == null)
            return;

        _logger.Info("Restoring source quiet mode state...");

        foreach (var serviceName in Enumerable.Reverse(state.StoppedServices))
        {
            if (!await StartServiceBestEffortAsync(serviceName))
            {
                var warning = $"Quiet mode restore: failed to restart service '{serviceName}'.";
                _logger.Warning(warning);
                result.Warnings.Add(warning);
            }
            else
            {
                _logger.Info($"Quiet mode restore: service '{serviceName}' resumed.");
            }
        }

        if (state.OneDriveStopped && !string.IsNullOrWhiteSpace(state.OneDriveExecutablePath) && File.Exists(state.OneDriveExecutablePath))
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = state.OneDriveExecutablePath,
                    UseShellExecute = true
                };

                System.Diagnostics.Process.Start(startInfo);
                _logger.Info("Quiet mode restore: OneDrive restarted.");
            }
            catch (Exception ex)
            {
                var warning = $"Quiet mode restore: failed to restart OneDrive ({ex.Message}).";
                _logger.Warning(warning);
                result.Warnings.Add(warning);
            }
        }
    }

    private static string? ResolveOneDriveExecutablePath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            var candidate = Path.Combine(local, "Microsoft", "OneDrive", "OneDrive.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            var candidate = Path.Combine(programFiles, "Microsoft OneDrive", "OneDrive.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private async Task<int?> QueryServiceStateCodeAsync(string serviceName)
    {
        var queryInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query \"{serviceName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(queryInfo);
        var combined = $"{output}\n{error}";

        if (exitCode != 0 && combined.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return null;

        var match = Regex.Match(combined, @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return code;

        return exitCode == 0 ? 0 : (int?)null;
    }

    private async Task<bool> StopServiceBestEffortAsync(string serviceName)
    {
        var stopInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"stop \"{serviceName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(stopInfo);
        var combined = $"{output}\n{error}";

        if (exitCode != 0 &&
            !combined.Contains("not started", StringComparison.OrdinalIgnoreCase) &&
            !combined.Contains("already stopped", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning($"Service stop failed for '{serviceName}' (code {exitCode}): {combined}");
            return false;
        }

        return await WaitForServiceStateAsync(serviceName, desiredStateCode: 1, TimeSpan.FromSeconds(12)); // STOPPED
    }

    private async Task<bool> StartServiceBestEffortAsync(string serviceName)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"start \"{serviceName}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var (exitCode, output, error) = await RunProcessAsync(startInfo);
        var combined = $"{output}\n{error}";
        if (exitCode != 0 && !combined.Contains("already running", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning($"Service start failed for '{serviceName}' (code {exitCode}): {combined}");
            return false;
        }

        return await WaitForServiceStateAsync(serviceName, desiredStateCode: 4, TimeSpan.FromSeconds(12)); // RUNNING
    }

    private async Task<bool> WaitForServiceStateAsync(string serviceName, int desiredStateCode, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var stateCode = await QueryServiceStateCodeAsync(serviceName);
            if (stateCode == desiredStateCode)
                return true;

            await Task.Delay(500);
        }

        return false;
    }

    internal static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        System.Diagnostics.ProcessStartInfo startInfo)
    {
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new IOException($"Failed to start process: {startInfo.FileName}");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask, await errorTask);
    }
}
