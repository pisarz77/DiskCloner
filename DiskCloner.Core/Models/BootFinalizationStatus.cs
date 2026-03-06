namespace DiskCloner.Core.Models;

/// <summary>
/// Holds the outcome of boot finalization steps after cloning.
/// </summary>
internal sealed class BootFinalizationStatus
{
    public bool Success { get; set; }
    public bool BootFilesRebuilt { get; set; }
    public bool WindowsVolumeClean { get; set; }
    public bool ChkdskFixApplied { get; set; }
    public string WindowsVolumeStatus { get; set; } = "Not checked";
}
