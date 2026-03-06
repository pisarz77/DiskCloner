namespace DiskCloner.Core.Utilities;

/// <summary>
/// Shared byte formatting utility. Replaces the duplicated FormatBytes method
/// that existed in DiskInfo, PartitionInfo, CloneProgress, DiskClonerEngine, and MainWindow.
/// </summary>
public static class ByteFormatter
{
    private static readonly string[] Sizes = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>
    /// Formats a byte count as a human-readable string (e.g. "14.9 GB").
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "0 B";
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < Sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {Sizes[order]}";
    }
}
