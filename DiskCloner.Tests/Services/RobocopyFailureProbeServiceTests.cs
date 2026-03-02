using DiskCloner.Core.Services;
using Xunit;

namespace DiskCloner.Tests.Services;

public class RobocopyFailureProbeServiceTests
{
    [Fact]
    public void TryExtractProblematicFilePath_ParsesRobocopyErrorLine()
    {
        var line = "[2026-03-02 22:31:15.123] [ Warning] Robocopy stdout: 2026/03/02 23:31:15 ERROR 5 (0x00000005) Copying File Z:\\Windows\\WinSxS\\test.dll";

        var ok = RobocopyFailureDiagnostics.TryExtractProblematicFilePath(line, out var path);

        Assert.True(ok);
        Assert.Equal(@"Z:\Windows\WinSxS\test.dll", path);
    }

    [Fact]
    public void ExtractProblematicFilePaths_DeduplicatesAndHonorsLimit()
    {
        var lines = new[]
        {
            "2026/03/02 23:31:15 ERROR 5 (0x00000005) Copying File Z:\\Windows\\A.dll",
            "2026/03/02 23:31:16 ERROR 5 (0x00000005) Copying File Z:\\Windows\\A.dll",
            "2026/03/02 23:31:17 ERROR 32 (0x00000020) Accessing Source File Z:\\Windows\\B.dll",
            "2026/03/02 23:31:18 ERROR 3 (0x00000003) Accessing Source Directory Z:\\Windows"
        };

        var paths = RobocopyFailureDiagnostics.ExtractProblematicFilePaths(lines, maxFiles: 1);

        Assert.Single(paths);
        Assert.Equal(@"Z:\Windows\A.dll", paths[0]);
    }

    [Fact]
    public void NormalizePathForProbe_RewritesMissingDriveToFallback()
    {
        var available = new HashSet<char> { 'C', 'D' };

        var normalized = RobocopyFailureDiagnostics.NormalizePathForProbe(
            @"Z:\Windows\System32\kernel32.dll",
            fallbackDriveLetter: 'C',
            availableDriveLetters: available);

        Assert.Equal(@"C:\Windows\System32\kernel32.dll", normalized);
    }

    [Fact]
    public void NormalizePathForProbe_LeavesExistingDriveUnchanged()
    {
        var available = new HashSet<char> { 'C', 'D' };

        var normalized = RobocopyFailureDiagnostics.NormalizePathForProbe(
            @"C:\Windows\System32\kernel32.dll",
            fallbackDriveLetter: 'D',
            availableDriveLetters: available);

        Assert.Equal(@"C:\Windows\System32\kernel32.dll", normalized);
    }
}
