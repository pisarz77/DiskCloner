namespace DiskCloner.Core.Models;

/// <summary>
/// Captures the state of Windows services and OneDrive before entering quiet mode,
/// so they can be restored on exit.
/// </summary>
internal sealed class QuietModeState
{
    public List<string> StoppedServices { get; } = new();
    public bool OneDriveStopped { get; set; }
    public string? OneDriveExecutablePath { get; set; }
}
