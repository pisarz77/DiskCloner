using System.Collections.Concurrent;
using System.Text;

namespace DiskCloner.Core.Logging;

/// <summary>
/// Logger that writes to a file and keeps entries in memory.
/// </summary>
public class FileLogger : ILogger
{
    private readonly string _logFilePath;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly object _fileLock = new();
    private readonly LogLevel _minLevel;
    private StreamWriter? _writer;
    private bool _disposed;

    public FileLogger(string? logFilePath, LogLevel minLevel = LogLevel.Debug)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
            throw new ArgumentNullException(nameof(logFilePath));

        _logFilePath = logFilePath!;
        _minLevel = minLevel;

        var directory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(
            logFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite);

        _writer = new StreamWriter(stream, Encoding.UTF8)
        {
            AutoFlush = true
        };

        Info($"Logger initialized. Log file: {logFilePath}");
    }

    public void Debug(string? message)
    {
        Log(LogLevel.Debug, message);
    }

    public void Info(string? message)
    {
        Log(LogLevel.Info, message);
    }

    public void Warning(string? message)
    {
        Log(LogLevel.Warning, message);
    }

    public void Error(string? message)
    {
        Log(LogLevel.Error, message);
    }

    public void Error(string? message, Exception ex)
    {
        Log(LogLevel.Error, message, ex);
    }

    private void Log(LogLevel level, string? message, Exception? exception = null)
    {
        if (level < _minLevel)
            return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message ?? string.Empty,
            Exception = exception
        };

        _entries.Enqueue(entry);
        WriteToFile(entry);
    }

    private void WriteToFile(LogEntry entry)
    {
        if (_disposed || _writer == null)
            return;

        try
        {
            lock (_fileLock)
            {
                _writer.WriteLine($"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level,8}] {entry.Message}");
                if (entry.Exception != null)
                {
                    _writer.WriteLine($"Exception: {entry.Exception.Message}");
                    _writer.WriteLine($"Stack Trace: {entry.Exception.StackTrace}");
                }
            }
        }
        catch (Exception ex)
        {
            // Silently fail if we can't write to the log file
            Console.Error.WriteLine($"Failed to write to log file: {ex.Message}");
        }
    }

    public IReadOnlyList<LogEntry> GetLogEntries()
    {
        return _entries.ToArray();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>
    /// Gets the log file path.
    /// </summary>
    public string LogFilePath => _logFilePath;
}
