using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Services;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskCloner.UI;

/// <summary>
/// Main window for the Disk Cloner application.
/// </summary>
public partial class MainWindow : Window
{
    private readonly DiskEnumerator _diskEnumerator;
    private readonly VssSnapshotService _vssService;
    private DiskClonerEngine? _clonerEngine;
    private ILogger? _logger;
    private List<DiskInfo> _availableDisks = new();
    private List<PartitionInfo> _allPartitions = new();
    private CloneOperation? _currentOperation;
    private bool _isCloning;

    // Concrete types for safer UI binding
    public class DiskDisplayItem
    {
        public int DiskNumber { get; set; }
        public string DisplayText { get; set; } = string.Empty;
    }

    public class PartitionDisplayItem
    {
        public int PartitionNumber { get; set; }
        public string DisplayText { get; set; } = string.Empty;
        public bool IsSelected { get; set; } = true;
    }

    public MainWindow()
    {
        InitializeComponent();
        InitializeLogger();
        _diskEnumerator = new DiskEnumerator(_logger!);
        _vssService = new VssSnapshotService(_logger);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void InitializeLogger()
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DiskCloner", "Logs");
        var logPath = Path.Combine(logDir, $"clone_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
        _logger = new FileLogger(logPath);

        LogFileTextBlock.Text = logPath;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Loading disk information...";
        await RefreshDisksAsync();
        MainTabControl.SelectedIndex = 0;
        StatusTextBlock.Text = "Ready";
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isCloning)
        {
            var result = MessageBox.Show(
                "A cloning operation is in progress. Are you sure you want to exit?",
                "Operation in Progress",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
        }

        _logger?.Dispose();
        _vssService?.Dispose();
    }

    private async Task RefreshDisksAsync()
    {
        try
        {
            var selectedSourceDiskNumber = SourceDiskComboBox.SelectedValue as int?;
            var selectedTargetDiskNumber = TargetDiskComboBox.SelectedValue as int?;

            _availableDisks = await _diskEnumerator.GetDisksAsync(forceRefresh: true);

            // Get all partitions from system disk
            var systemDisk = _availableDisks.FirstOrDefault(d => d.IsSystemDisk);
            if (systemDisk != null)
            {
                _allPartitions = systemDisk.Partitions;
            }

            // Update UI
            UpdateDiskComboBoxes();
            UpdatePartitionsList();

            // Restore selections
            if (selectedSourceDiskNumber.HasValue)
            {
                SourceDiskComboBox.SelectedValue = selectedSourceDiskNumber.Value;
            }
            else
            {
                // Auto-select system disk
                var sysDisk = _availableDisks.FirstOrDefault(d => d.IsSystemDisk);
                if (sysDisk != null)
                {
                    SourceDiskComboBox.SelectedValue = sysDisk.DiskNumber;
                }
            }

            if (selectedTargetDiskNumber.HasValue)
            {
                TargetDiskComboBox.SelectedValue = selectedTargetDiskNumber.Value;
            }

            UpdateSummary();
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to refresh disks", ex);
            MessageBox.Show($"Failed to load disk information: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateDiskComboBoxes()
    {
        SourceDiskComboBox.ItemsSource = null;
        TargetDiskComboBox.ItemsSource = null;

        var sourceItems = _availableDisks
            .Select(d => new DiskDisplayItem
            {
                DiskNumber = d.DiskNumber,
                DisplayText = $"Disk {d.DiskNumber}: {d.FriendlyName} ({d.SizeDisplay}){(d.IsSystemDisk ? " [SYSTEM]" : "")}"
            })
            .ToList();

        var targetItems = _availableDisks
            .Where(d => !d.IsSystemDisk && d.IsOnline && !d.IsReadOnly)
            .Select(d => new DiskDisplayItem
            {
                DiskNumber = d.DiskNumber,
                DisplayText = $"Disk {d.DiskNumber}: {d.FriendlyName} ({d.SizeDisplay}){(d.IsRemovable ? " [USB]" : "")}"
            })
            .ToList();

        SourceDiskComboBox.ItemsSource = sourceItems;
        TargetDiskComboBox.ItemsSource = targetItems;
    }

    private void UpdatePartitionsList()
    {
        var partitionItems = _allPartitions
            .Select(p => new PartitionDisplayItem
            {
                PartitionNumber = p.PartitionNumber,
                DisplayText = $"[{p.PartitionNumber}] {p.GetTypeName()}: {p.SizeDisplay}{(p.DriveLetter.HasValue ? $" ({p.DriveLetter.Value}:)" : "")}"
            })
            .ToList();

        PartitionsListBox.ItemsSource = partitionItems;
    }

    private void UpdateSummary()
    {
        if (SourceDiskComboBox.SelectedItem is DiskDisplayItem src)
        {
            SummarySourceTextBlock.Text = $"Source: {src.DisplayText}";
        }
        else
        {
            SummarySourceTextBlock.Text = "Source: Not selected";
        }

        if (TargetDiskComboBox.SelectedItem is DiskDisplayItem tgt)
        {
            SummaryTargetTextBlock.Text = $"Target: {tgt.DisplayText}";
        }
        else
        {
            SummaryTargetTextBlock.Text = "Target: Not selected";
        }
    }

    private async void RefreshDisksButton_Click(object sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Refreshing disk information...";
        await RefreshDisksAsync();
        StatusTextBlock.Text = "Ready";
    }

    private void SourceDiskComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSummary();
    }

    private void TargetDiskComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSummary();
    }

    private void ConfirmationTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        CloneButton.IsEnabled = ConfirmationTextBox.Text == "CLONE";
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSelection(out var sourceDisk, out var targetDisk))
            return;

        try
        {
            var operation = BuildCloneOperation(sourceDisk!, targetDisk!);
            _clonerEngine = new DiskClonerEngine(_logger!, _diskEnumerator, _vssService);
            var summary = _clonerEngine.GetOperationSummary(operation);
            PreviewTextBlock.Text = summary;

            _currentOperation = operation;
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to generate preview", ex);
            MessageBox.Show($"Failed to generate preview: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CloneButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateSelection(out var sourceDisk, out var targetDisk))
            return;

        // Final confirmation dialog
        var result = MessageBox.Show(
            "This is your LAST CHANCE to cancel.\n\n" +
            $"Source: Disk {sourceDisk!.DiskNumber} - {sourceDisk.FriendlyName}\n" +
            $"Target: Disk {targetDisk!.DiskNumber} - {targetDisk.FriendlyName}\n\n" +
            "All data on the target disk will be PERMANENTLY LOST.\n\n" +
            "Are you absolutely sure you want to continue?",
            "FINAL CONFIRMATION",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        // Clear confirmation
        ConfirmationTextBox.Text = "";
        CloneButton.IsEnabled = false;

        // Start cloning
        await StartCloningAsync(sourceDisk, targetDisk);
    }

    private async Task StartCloningAsync(DiskInfo sourceDisk, DiskInfo targetDisk)
    {
        _isCloning = true;
        _currentOperation = BuildCloneOperation(sourceDisk, targetDisk);
        _clonerEngine = new DiskClonerEngine(_logger!, _diskEnumerator, _vssService);

        // Subscribe to progress updates
        _clonerEngine.ProgressUpdate += OnProgressUpdate;

        // Show progress UI
        ProgressBorder.Visibility = Visibility.Visible;
        ResultsBorder.Visibility = Visibility.Collapsed;
        
        // Instead of disabling the whole TabControl (which kills scrollbars), 
        // we disable specific interactive elements.
        SetInteractiveElementsEnabled(false);
        
        // Scroll progress into view
        ProgressBorder.BringIntoView();

        CloneResult cloneResult;

        try
        {
            cloneResult = await _clonerEngine.CloneAsync(_currentOperation);
        }
        catch (Exception ex)
        {
            _logger?.Error("Cloning operation failed with exception", ex);
            cloneResult = new CloneResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Exception = ex
            };
        }
        finally
        {
            _isCloning = false;
            _clonerEngine.ProgressUpdate -= OnProgressUpdate;
            SetInteractiveElementsEnabled(true);
        }

        // Show results
        ShowResults(cloneResult);
    }

    private void OnProgressUpdate(CloneProgress progress)
    {
        // Use BeginInvoke to prevent blocking the cloning thread
        Dispatcher.BeginInvoke(() =>
        {
            StageTextBlock.Text = progress.StatusMessage;
            ProgressBar.Value = progress.PercentComplete;
            ProgressTextBlock.Text =
                $"{progress.BytesCopiedDisplay} / {progress.TotalBytesDisplay} " +
                $"({progress.PercentComplete:F1}%)";

            if (progress.ThroughputBytesPerSec > 0)
            {
                ThroughputTextBlock.Text = $"Speed: {progress.ThroughputDisplay}";
            }

            if (progress.EstimatedTimeRemaining.TotalSeconds > 0)
            {
                RemainingTimeTextBlock.Text = $"Estimated: {FormatTimeSpan(progress.EstimatedTimeRemaining)}";
            }

            if (progress.LastError != null)
            {
                _logger?.Error($"Progress error: {progress.LastError}");
            }
        });
    }

    private void SetInteractiveElementsEnabled(bool enabled)
    {
        SourceDiskComboBox.IsEnabled = enabled;
        TargetDiskComboBox.IsEnabled = enabled;
        PartitionsListBox.IsEnabled = enabled;
        UseVssCheckBox.IsEnabled = enabled;
        VerifyIntegrityCheckBox.IsEnabled = enabled;
        FullHashCheckBox.IsEnabled = enabled;
        AutoExpandCheckBox.IsEnabled = enabled;
        AllowSmallerTargetCheckBox.IsEnabled = enabled;
        BufferSizeComboBox.IsEnabled = enabled;
        PreviewButton.IsEnabled = enabled;
        ConfirmationTextBox.IsEnabled = enabled;
        CloneButton.IsEnabled = enabled ? (ConfirmationTextBox.Text.Trim().ToUpper() == "CLONE") : false;
        
        // Disable "Refresh Disks" buttons (they don't have names, so we'll find them)
        foreach (var child in FindVisualChildren<Button>(this))
        {
            if (child.Content?.ToString() == "Refresh Disks")
            {
                child.IsEnabled = enabled;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (T childOfChild in FindVisualChildren<T>(child)) yield return childOfChild;
        }
    }

    private void ShowResults(CloneResult result)
    {
        ProgressBorder.Visibility = Visibility.Collapsed;
        ResultsBorder.Visibility = Visibility.Visible;

        if (result.Success)
        {
            ResultsBorder.Background = System.Windows.Media.Brushes.LightGreen;
            ResultsTitleTextBlock.Text = "✓ Cloning Completed Successfully";
            ResultsTitleTextBlock.Foreground = System.Windows.Media.Brushes.DarkGreen;
            ViewLogsButton.Visibility = Visibility.Visible;
        }
        else
        {
            ResultsBorder.Background = System.Windows.Media.Brushes.LightPink;
            ResultsTitleTextBlock.Text = "✗ Cloning Failed";
            ResultsTitleTextBlock.Foreground = System.Windows.Media.Brushes.DarkRed;
            ViewLogsButton.Visibility = Visibility.Visible;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Duration: {result.Duration:hh\\:mm\\:ss}");
        sb.AppendLine($"Bytes Copied: {FormatBytes(result.BytesCopied)}");

        if (result.Duration.TotalSeconds > 0)
        {
            sb.AppendLine($"Average Speed: {FormatBytes((long)result.AverageThroughputBytesPerSec)}/s");
        }

        sb.AppendLine($"Integrity Verified: {(result.IntegrityVerified ? "Yes" : "No")}");
        sb.AppendLine($"Bootable: {(result.IsBootable ? "Yes" : "No")}");
        sb.AppendLine($"Boot Mode: {result.BootMode}");

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            sb.AppendLine();
            sb.AppendLine($"Error: {result.ErrorMessage}");
        }

        sb.AppendLine();
        sb.AppendLine("Next Steps:");
        foreach (var step in result.NextSteps)
        {
            sb.AppendLine($"• {step}");
        }

        ResultsTextBlock.Text = sb.ToString();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to cancel the cloning operation?",
            "Confirm Cancellation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _clonerEngine?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Cancelling...";
        }
    }

    private void ViewLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_logger != null && _logger is FileLogger fileLogger)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fileLogger.LogFilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open log file: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private bool ValidateSelection(out DiskInfo? sourceDisk, out DiskInfo? targetDisk)
    {
        sourceDisk = null;
        targetDisk = null;

        if (SourceDiskComboBox.SelectedValue == null)
        {
            MessageBox.Show("Please select a source disk.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (TargetDiskComboBox.SelectedValue == null)
        {
            MessageBox.Show("Please select a target disk.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var sourceDiskNumber = (int)SourceDiskComboBox.SelectedValue;
        var targetDiskNumber = (int)TargetDiskComboBox.SelectedValue;

        sourceDisk = _availableDisks.FirstOrDefault(d => d.DiskNumber == sourceDiskNumber);
        targetDisk = _availableDisks.FirstOrDefault(d => d.DiskNumber == targetDiskNumber);

        if (sourceDisk == null)
        {
            MessageBox.Show("Invalid source disk selection.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (targetDisk == null)
        {
            MessageBox.Show("Invalid target disk selection.", "Validation Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Safety check: source must be system disk
        if (!sourceDisk.IsSystemDisk)
        {
            var result = MessageBox.Show(
                "The selected source disk is not the system disk.\n\n" +
                "Are you sure you want to clone from this disk?",
                "Non-System Source Disk",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return false;
        }

        // Check for administrator privileges
        if (!IsAdministrator())
        {
            MessageBox.Show(
                "This application requires administrator privileges.\n\n" +
                "Please run as administrator and try again.",
                "Administrator Required",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
        // Safety check: target must not be system disk
        if (targetDisk.IsSystemDisk)
        {
            MessageBox.Show(
                "The target disk is the system disk. You cannot clone to the system disk.\n" +
                "Please select a different target disk.",
                "Invalid Target",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        // Size check
        if (targetDisk.SizeBytes < sourceDisk.SizeBytes && !AllowSmallerTargetCheckBox.IsChecked.GetValueOrDefault())
        {
            MessageBox.Show(
                $"The target disk ({targetDisk.SizeDisplay}) is smaller than the source disk ({sourceDisk.SizeDisplay}).\n\n" +
                "Enable 'Allow smaller target disk' in the Options tab if you want to proceed.",
                "Target Too Small",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private CloneOperation BuildCloneOperation(DiskInfo sourceDisk, DiskInfo targetDisk)
    {
        var operation = new CloneOperation
        {
            SourceDisk = sourceDisk,
            TargetDisk = targetDisk,
            UseVss = UseVssCheckBox.IsChecked.GetValueOrDefault(),
            VerifyIntegrity = VerifyIntegrityCheckBox.IsChecked.GetValueOrDefault(),
            FullHashVerification = FullHashCheckBox.IsChecked.GetValueOrDefault(),
            AutoExpandWindowsPartition = AutoExpandCheckBox.IsChecked.GetValueOrDefault(),
            AllowSmallerTarget = AllowSmallerTargetCheckBox.IsChecked.GetValueOrDefault(),
            LogFilePath = LogFileTextBlock.Text
        };

        // Parse buffer size
        var bufferSizes = new[] { 16, 64, 128, 256 };
        var selectedIndex = BufferSizeComboBox.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < bufferSizes.Length)
        {
            operation.IoBufferSize = bufferSizes[selectedIndex] * 1024 * 1024;
        }

        // Get selected partitions
        if (PartitionsListBox.ItemsSource is IEnumerable<PartitionDisplayItem> partitions)
        {
            var allPartitions = sourceDisk.Partitions.ToList();

            foreach (var item in partitions)
            {
                if (item.IsSelected)
                {
                    var partition = allPartitions.FirstOrDefault(p => p.PartitionNumber == item.PartitionNumber);
                    if (partition != null)
                    {
                        operation.PartitionsToClone.Add(partition);
                    }
                }
            }
        }

        // Ensure boot-required partitions are included
        var efiPartition = sourceDisk.Partitions.FirstOrDefault(p => p.IsEfiPartition);
        if (efiPartition != null && !operation.PartitionsToClone.Contains(efiPartition))
        {
            operation.PartitionsToClone.Add(efiPartition);
        }

        var systemPartition = sourceDisk.Partitions.FirstOrDefault(p => p.IsSystemPartition);
        if (systemPartition != null && !operation.PartitionsToClone.Contains(systemPartition))
        {
            operation.PartitionsToClone.Add(systemPartition);
        }

        // Sort partitions by starting offset
        operation.PartitionsToClone = operation.PartitionsToClone.OrderBy(p => p.StartingOffset).ToList();

        return operation;
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
