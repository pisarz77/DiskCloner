using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiskCloner.Core.Logging;
using Xunit;

namespace DiskCloner.Tests.Logging;

public class FileLoggerTests : IDisposable
{
    private readonly string _testLogDirectory;
    private readonly string _testLogFilePath;
    private readonly FileLogger _logger;

    public FileLoggerTests()
    {
        _testLogDirectory = Path.Combine(Path.GetTempPath(), "DiskClonerTests", "Logs", Guid.NewGuid().ToString("N"));
        _testLogFilePath = Path.Combine(_testLogDirectory, "test.log");
        
        // Ensure directory exists
        Directory.CreateDirectory(_testLogDirectory);
        
        _logger = new FileLogger(_testLogFilePath);
    }

    public void Dispose()
    {
        try
        {
            _logger.Dispose();
            if (File.Exists(_testLogFilePath))
            {
                File.Delete(_testLogFilePath);
            }
            if (Directory.Exists(_testLogDirectory))
            {
                Directory.Delete(_testLogDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void FileLogger_Constructor_ThrowsOnNullPath()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new FileLogger(null));
    }

    [Fact]
    public void FileLogger_Constructor_ThrowsOnEmptyPath()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new FileLogger(string.Empty));
    }

    [Fact]
    public void FileLogger_Constructor_ThrowsOnWhitespacePath()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new FileLogger("   "));
    }

    [Fact]
    public void FileLogger_Constructor_AcceptsValidPath()
    {
        // Arrange & Act
        using var logger = new FileLogger(_testLogFilePath);

        // Assert
        Assert.NotNull(logger);
    }

    [Fact]
    public void FileLogger_Constructor_CreatesLogFile()
    {
        // Arrange & Act
        using var logger = new FileLogger(_testLogFilePath);

        // Assert
        Assert.True(File.Exists(_testLogFilePath));
    }

    [Fact]
    public void FileLogger_Constructor_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var newLogDir = Path.Combine(Path.GetTempPath(), "DiskClonerTests", "NewLogs", Guid.NewGuid().ToString("N"));
        var newLogPath = Path.Combine(newLogDir, "new.log");

        // Act
        using (var logger = new FileLogger(newLogPath))
        {
            // Assert
            Assert.True(Directory.Exists(newLogDir));
            Assert.True(File.Exists(newLogPath));
        }

        // Cleanup
        Directory.Delete(newLogDir, true);
    }

    [Fact]
    public void Info_WritesToLogFile()
    {
        // Arrange
        var message = "Test info message";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
        Assert.Contains("Info", logContent);
    }

    [Fact]
    public void Warning_WritesToLogFile()
    {
        // Arrange
        var message = "Test warning message";

        // Act
        _logger.Warning(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
        Assert.Contains("Warning", logContent);
    }

    [Fact]
    public void Error_WritesToLogFile()
    {
        // Arrange
        var message = "Test error message";

        // Act
        _logger.Error(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
        Assert.Contains("Error", logContent);
    }

    [Fact]
    public void Error_WithException_WritesToLogFile()
    {
        // Arrange
        var message = "Test error with exception";
        var exception = new Exception("Test exception");

        // Act
        _logger.Error(message, exception);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
        Assert.Contains("Error", logContent);
        Assert.Contains("Test exception", logContent);
    }

    [Fact]
    public void Debug_WritesToLogFile()
    {
        // Arrange
        var message = "Test debug message";

        // Act
        _logger.Debug(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
        Assert.Contains("Debug", logContent);
    }

    [Fact]
    public void LogEntry_ContainsTimestamp()
    {
        // Arrange
        var message = "Test message";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), logContent);
    }

    [Fact]
    public void LogEntry_ContainsLogLevel()
    {
        // Arrange
        var message = "Test message";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains("Info]", logContent);
    }

    [Fact]
    public void LogEntry_ContainsMessage()
    {
        // Arrange
        var message = "This is a test message with special characters: !@#$%^&*()";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
    }

    [Fact]
    public void LogEntry_ContainsNewline()
    {
        // Arrange
        var message = "Test message";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.EndsWith(Environment.NewLine, logContent);
    }

    [Fact]
    public void MultipleLogEntries_AreAppended()
    {
        // Arrange
        var message1 = "First message";
        var message2 = "Second message";
        var message3 = "Third message";

        // Act
        _logger.Info(message1);
        _logger.Warning(message2);
        _logger.Error(message3);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message1, logContent);
        Assert.Contains(message2, logContent);
        Assert.Contains(message3, logContent);
        Assert.Contains("Info", logContent);
        Assert.Contains("Warning", logContent);
        Assert.Contains("Error", logContent);
    }

    [Fact]
    public void Exception_StackTraceIsIncluded()
    {
        // Arrange
        var message = "Error with stack trace";
        var exception = new InvalidOperationException("Test exception", new ArgumentException("Inner exception"));

        // Act
        _logger.Error(message, exception);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
        Assert.Contains("Test exception", logContent);
        Assert.Contains("Stack Trace:", logContent);
    }

    [Fact]
    public void SpecialCharacters_AreHandledCorrectly()
    {
        // Arrange
        var message = "Message with special chars: \n\t\r\"'<>{}[]|\\/";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(message, logContent);
    }

    [Fact]
    public void LongMessage_IsHandled()
    {
        // Arrange
        var longMessage = new string('A', 10000);

        // Act
        _logger.Info(longMessage);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(longMessage, logContent);
    }

    [Fact]
    public void EmptyMessage_IsHandled()
    {
        // Arrange
        var message = string.Empty;

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains("Info]", logContent);
        Assert.Contains(":", logContent);
    }

    [Fact]
    public void NullMessage_IsHandled()
    {
        // Arrange
        string? message = null;

        // Act
        _logger.Info(message!);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains("Info]", logContent);
        Assert.Contains(":", logContent);
    }

    [Fact]
    public void LogFile_IsCreatedWithCorrectEncoding()
    {
        // Arrange
        var unicodeMessage = "Unicode test: ąćęłńóśźż";

        // Act
        _logger.Info(unicodeMessage);

        // Assert
        var logContent = ReadLogContent();
        Assert.Contains(unicodeMessage, logContent);
    }

    [Fact]
    public void LogFile_HandlesConcurrentAccess()
    {
        // Arrange
        var tasks = new Task[10];
        
        for (int i = 0; i < 10; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() =>
            {
                _logger.Info($"Concurrent message {index}");
            });
        }

        // Act
        Task.WaitAll(tasks);

        // Assert
        var logContent = ReadLogContent();
        for (int i = 0; i < 10; i++)
        {
            Assert.Contains($"Concurrent message {i}", logContent);
        }
    }

    [Fact]
    public void LogFile_Size_GrowsWithContent()
    {
        // Arrange
        var initialSize = new FileInfo(_testLogFilePath).Length;

        // Act
        _logger.Info("Test message");
        var finalSize = new FileInfo(_testLogFilePath).Length;

        // Assert
        Assert.True(finalSize > initialSize);
    }

    [Fact]
    public void LogFile_CanBeReadAfterWriting()
    {
        // Arrange
        var message = "Test message for reading";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        Assert.NotNull(logContent);
        Assert.NotEmpty(logContent);
    }

    [Fact]
    public void LogFile_Format_IsConsistent()
    {
        // Arrange
        var message = "Test message";

        // Act
        _logger.Info(message);

        // Assert
        var logContent = ReadLogContent();
        var lines = logContent.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        var logLine = lines.FirstOrDefault(l => l.Contains(message)) ?? string.Empty;
        
        // Should contain: timestamp, level, message
        Assert.Contains("Info]", logLine);
        Assert.Contains(":", logLine);
        Assert.Contains(message, logLine);
    }

    private string ReadLogContent()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var stream = new FileStream(_testLogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (i < 9)
            {
                Thread.Sleep(25);
            }
        }

        return string.Empty;
    }
}

