namespace DiskCloner.Core.Logging;

/// <summary>
/// Interface for logging operations.
/// </summary>
public interface ILogger : IDisposable
{
    /// <summary>
    /// Logs a debug message.
    /// </summary>
    void Debug(string message);

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    void Info(string message);

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    void Warning(string message);

    /// <summary>
    /// Logs an error message.
    /// </summary>
    void Error(string message);

    /// <summary>
    /// Logs an error message with exception details.
    /// </summary>
    void Error(string message, Exception ex);

    /// <summary>
    /// Gets all log entries.
    /// </summary>
    IReadOnlyList<LogEntry> GetLogEntries();

    /// <summary>
    /// Clears all log entries.
    /// </summary>
    void Clear();
}

/// <summary>
/// Represents a single log entry.
/// </summary>
public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
    public Exception? Exception { get; set; }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] ");
        sb.Append($"[{Level,8}] ");
        sb.Append(Message);
        if (Exception != null)
        {
            sb.AppendLine();
            sb.Append(Exception.ToString());
        }
        return sb.ToString();
    }
}

/// <summary>
/// Log levels.
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}
