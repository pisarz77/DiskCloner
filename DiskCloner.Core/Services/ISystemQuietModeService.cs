using DiskCloner.Core.Models;

namespace DiskCloner.Core.Services;

/// <summary>
/// Manages "quiet mode" — pausing background processes (OneDrive, Windows Update, etc.)
/// before a disk clone and restoring them afterwards.
/// </summary>
public interface ISystemQuietModeService
{
    /// <summary>
    /// Pauses OneDrive and background Windows services that could interfere with cloning.
    /// Returns state object so <see cref="ExitAsync"/> can restore exactly what was changed.
    /// </summary>
    Task<QuietModeState> EnterAsync(CloneOperation operation, CloneResult result, CloneProgress progress);

    /// <summary>
    /// Restores services and OneDrive that were stopped by <see cref="EnterAsync"/>.
    /// </summary>
    Task ExitAsync(QuietModeState? state, CloneResult result);
}
