using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using System.Management;
using System.Runtime.InteropServices;

namespace DiskCloner.Core.Services;

/// <summary>
/// Service for enumerating disks and partitions.
/// </summary>
public class DiskEnumerator
{
    private readonly ILogger _logger;
    private readonly List<DiskInfo> _cachedDisks = new();
    private DateTime _lastCacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(5);

    public DiskEnumerator(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets all physical disks in the system.
    /// </summary>
    public async Task<List<DiskInfo>> GetDisksAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && DateTime.UtcNow - _lastCacheTime < _cacheDuration)
        {
            _logger.Debug("Returning cached disk information");
            return new List<DiskInfo>(_cachedDisks);
        }

        _logger.Info("Enumerating physical disks...");

        try
        {
            var disks = await QueryDisksViaWmiAsync();
            var systemDiskNumber = await GetSystemDiskNumberAsync();

            foreach (var disk in disks)
            {
                disk.IsSystemDisk = (disk.DiskNumber == systemDiskNumber);
                _logger.Info($"Found Disk {disk.DiskNumber}: {disk.FriendlyName} ({disk.SizeDisplay}) {(disk.IsSystemDisk ? "[SYSTEM]" : "")}");
            }

            _cachedDisks.Clear();
            _cachedDisks.AddRange(disks);
            _lastCacheTime = DateTime.UtcNow;

            return disks;
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to enumerate disks", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets a specific disk by number.
    /// </summary>
    public async Task<DiskInfo?> GetDiskAsync(int diskNumber)
    {
        var disks = await GetDisksAsync();
        return disks.FirstOrDefault(d => d.DiskNumber == diskNumber);
    }

    /// <summary>
    /// Gets the system disk (boot disk).
    /// </summary>
    public async Task<DiskInfo?> GetSystemDiskAsync()
    {
        var disks = await GetDisksAsync();
        return disks.FirstOrDefault(d => d.IsSystemDisk);
    }

    /// <summary>
    /// Gets disks that are suitable as cloning targets (not system, removable or writable).
    /// </summary>
    public async Task<List<DiskInfo>> GetTargetDisksAsync()
    {
        var disks = await GetDisksAsync();
        return disks.Where(d =>
            !d.IsSystemDisk &&
            d.IsOnline &&
            !d.IsReadOnly &&
            d.SizeBytes > 0
        ).ToList();
    }

    /// <summary>
    /// Queries disks using WMI.
    /// </summary>
    private async Task<List<DiskInfo>> QueryDisksViaWmiAsync()
    {
        var disks = new List<DiskInfo>();

        try
        {
            await Task.Run(async () =>
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                using var results = searcher.Get();

                foreach (ManagementObject diskObj in results)
                {
                    try
                    {
                        // Safely get basic properties first
                        int diskNumber = GetInt32OrDefault(SafeGetProperty(diskObj, "Index"));
                        string model = SafeGetProperty(diskObj, "Model")?.ToString() ?? "Unknown Disk";
                        long size = GetInt64OrDefault(SafeGetProperty(diskObj, "Size"));

                        var disk = new DiskInfo
                        {
                            DiskNumber = diskNumber,
                            FriendlyName = model,
                            SizeBytes = size,
                            IsOnline = true
                        };

                        // Optional properties with individual protection
                        string status = SafeGetProperty(diskObj, "Status")?.ToString() ?? "";
                        disk.IsOnline = string.IsNullOrEmpty(status) || status.Equals("OK", StringComparison.OrdinalIgnoreCase);

                        string mediaType = SafeGetProperty(diskObj, "MediaType")?.ToString() ?? "";
                        string interfaceType = SafeGetProperty(diskObj, "InterfaceType")?.ToString() ?? "";
                        
                        disk.IsRemovable = mediaType.Contains("removable", StringComparison.OrdinalIgnoreCase) ||
                                         interfaceType.Contains("usb", StringComparison.OrdinalIgnoreCase);

                        disk.BusType = DetermineBusType(interfaceType, mediaType);

                        disk.PhysicalSectorSize = GetInt32OrDefault(SafeGetProperty(diskObj, "BytesPerSector"), 512);
                        disk.LogicalSectorSize = disk.PhysicalSectorSize;

                        disk.IsGpt = DeterminePartitionStyle(diskObj);
                        disk.TotalSectors = disk.SizeBytes > 0 ? disk.SizeBytes / disk.PhysicalSectorSize : 0;

                        // Partitions can be queried separately
                        disk.Partitions = await QueryPartitionsForDisk(disk.DiskNumber);

                        disks.Add(disk);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to process specific disk entry: {ex.Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error("WMI query for disks failed", ex);
            throw;
        }

        return disks;
    }

    private static object? SafeGetProperty(ManagementObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName];
        }
        catch
        {
            return null;
        }
    }

    private string DetermineBusType(string interfaceType, string mediaType)
    {
        if (interfaceType.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("USB", StringComparison.OrdinalIgnoreCase))
            return "USB";

        if (interfaceType.Contains("SCSI", StringComparison.OrdinalIgnoreCase))
            return "SCSI/SATA";

        if (interfaceType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return "NVMe";

        if (interfaceType.Contains("IDE", StringComparison.OrdinalIgnoreCase) ||
            interfaceType.Contains("ATA", StringComparison.OrdinalIgnoreCase))
            return "IDE/ATA";

        return string.IsNullOrEmpty(interfaceType) ? "UNKNOWN" : interfaceType.ToUpperInvariant();
    }

    private static bool GetBoolOrDefault(object? value, bool defaultValue = false)
    {
        if (value == null || value == DBNull.Value)
            return defaultValue;

        if (value is bool boolean)
            return boolean;

        if (value is string text)
        {
            if (bool.TryParse(text, out var parsed))
                return parsed;

            if (text.Equals("ok", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("online", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.Equals("offline", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return defaultValue;
        }

        try
        {
            return Convert.ToBoolean(value);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static int GetInt32OrDefault(object? value, int defaultValue = 0)
    {
        if (value == null || value == DBNull.Value)
            return defaultValue;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static long GetInt64OrDefault(object? value, long defaultValue = 0)
    {
        if (value == null || value == DBNull.Value)
            return defaultValue;

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Queries partitions for a specific disk.
    /// </summary>
    private async Task<List<PartitionInfo>> QueryPartitionsForDisk(int diskNumber)
    {
        var partitions = new List<PartitionInfo>();

        try
        {
            await Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskNumber}");
                using var results = searcher.Get();

                foreach (ManagementObject partitionObj in results)
                {
                    try
                    {
                        var partition = new PartitionInfo
                        {
                            PartitionNumber = GetInt32OrDefault(SafeGetProperty(partitionObj, "Index")) + 1,
                            StartingOffset = GetInt64OrDefault(SafeGetProperty(partitionObj, "StartingOffset")),
                            SizeBytes = GetInt64OrDefault(SafeGetProperty(partitionObj, "Size")),
                            IsActive = GetBoolOrDefault(SafeGetProperty(partitionObj, "Bootable")) ||
                                       GetBoolOrDefault(SafeGetProperty(partitionObj, "PrimaryPartition"))
                        };

                        // Get logical disks for this partition
                        var logicalDisks = GetLogicalDisksForPartition(diskNumber, partition.PartitionNumber - 1);
                        if (logicalDisks.Any())
                        {
                            var logical = logicalDisks.First();
                            partition.DriveLetter = logical.VolumeId != null && logical.VolumeId.Length > 0 ? logical.VolumeId[0] : (char?)null;
                            partition.FileSystemType = logical.FileSystem ?? "RAW";
                            partition.VolumeLabel = logical.VolumeName ?? "";
                        }

                        // Determine partition type/role
                        DeterminePartitionType(partition, diskNumber);

                        partitions.Add(partition);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to process partition: {ex.Message}");
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error($"WMI query for partitions on disk {diskNumber} failed", ex);
        }

        return partitions.OrderBy(p => p.StartingOffset).ToList();
    }

    /// <summary>
    /// Gets logical disk information for a partition.
    /// </summary>
    private List<dynamic> GetLogicalDisksForPartition(int diskNumber, int partitionIndex)
    {
        var logicalDisks = new List<dynamic>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_LogicalDiskToPartition");
            using var results = searcher.Get();

            foreach (ManagementObject link in results)
            {
                try
                {
                    var antecedent = SafeGetProperty(link, "Antecedent")?.ToString() ?? "";
                    var dependent = SafeGetProperty(link, "Dependent")?.ToString() ?? "";

                    if (antecedent.Contains($"Disk #{diskNumber}") &&
                        antecedent.Contains($"Partition #{partitionIndex}"))
                    {
                        // Extract drive letter
                        var match = System.Text.RegularExpressions.Regex.Match(
                            dependent, @"Win32_LogicalDisk\s*=\s*""([A-Z]):""");
                        if (match.Success)
                        {
                            var driveLetter = match.Groups[1].Value[0];
                            var volumeInfo = GetVolumeInfo(driveLetter);
                            logicalDisks.Add(volumeInfo);
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to query logical disks: {ex.Message}");
        }

        return logicalDisks;
    }

    /// <summary>
    /// Gets volume information for a drive letter.
    /// </summary>
    private dynamic GetVolumeInfo(char driveLetter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_LogicalDisk WHERE DeviceID = '{driveLetter}:'");
            using var results = searcher.Get();

            foreach (ManagementObject disk in results)
            {
                return new
                {
                    VolumeId = new[] { driveLetter },
                    FileSystem = SafeGetProperty(disk, "FileSystem")?.ToString(),
                    VolumeName = SafeGetProperty(disk, "VolumeName")?.ToString(),
                    VolumeSerialNumber = SafeGetProperty(disk, "VolumeSerialNumber")?.ToString(),
                    Size = GetInt64OrDefault(SafeGetProperty(disk, "Size")),
                    FreeSpace = GetInt64OrDefault(SafeGetProperty(disk, "FreeSpace"))
                };
            }
        }
        catch { }

        return new { VolumeId = Array.Empty<char>(), FileSystem = "", VolumeName = "" };
    }

    /// <summary>
    /// Determines if a disk uses GPT or MBR partition style.
    /// </summary>
    private bool DeterminePartitionStyle(ManagementObject diskObj)
    {
        try 
        {
            // Check for Partitions property (count of partitions)
            var partitionCount = GetInt32OrDefault(diskObj["Partitions"]);
            
            // On Win32_DiskDrive, there isn't a direct "PartitionStyle" property.
            // However, we can infer it or just default to GPT for large modern disks.
            // A more accurate way is to check the signature.
            var signature = diskObj["Signature"]?.ToString();
            if (!string.IsNullOrEmpty(signature))
            {
                // MBR signatures are usually simple integers (often < 10 digits as string)
                // GPT doesn't use the Signature field in Win32_DiskDrive in the same way.
                if (long.TryParse(signature, out var sigLong) && sigLong != 0)
                {
                    return false; // Likely MBR
                }
            }
        }
        catch { }

        // Default to GPT for modern Windows/NVMe
        return true;
    }

    /// <summary>
    /// Determines the bus type of the disk.
    /// </summary>
    private string DetermineBusType(ManagementObject diskObj)
    {
        var interfaceType = diskObj["InterfaceType"]?.ToString() ?? "";
        var mediaType = diskObj["MediaType"]?.ToString() ?? "";

        if (interfaceType.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("USB", StringComparison.OrdinalIgnoreCase))
            return "USB";

        if (interfaceType.Contains("SCSI", StringComparison.OrdinalIgnoreCase))
            return "SCSI/SATA";

        if (interfaceType.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return "NVMe";

        if (interfaceType.Contains("IDE", StringComparison.OrdinalIgnoreCase) ||
            interfaceType.Contains("ATA", StringComparison.OrdinalIgnoreCase))
            return "IDE/ATA";

        if (interfaceType.Contains("RAID", StringComparison.OrdinalIgnoreCase))
            return "RAID";

        return interfaceType.ToUpperInvariant();
    }

    /// <summary>
    /// Determines the type/role of a partition (EFI, System, MSR, Recovery, etc.).
    /// </summary>
    private void DeterminePartitionType(PartitionInfo partition, int diskNumber)
    {
        // Check if this is the Windows system partition (typically C:)
        if (partition.DriveLetter == 'C')
        {
            partition.IsSystemPartition = true;
        }

        // Check for EFI partition (usually 100-300MB, FAT32, no drive letter, starts at 1MB)
        if (partition.SizeBytes >= 100 * 1024 * 1024 &&
            partition.SizeBytes <= 600 * 1024 * 1024 &&
            partition.FileSystemType.Equals("FAT32", StringComparison.OrdinalIgnoreCase) &&
            (partition.DriveLetter == null || partition.DriveLetter < 'A' || partition.DriveLetter > 'Z') &&
            partition.StartingOffset >= 1024 * 1024 &&
            partition.StartingOffset < 10 * 1024 * 1024)
        {
            partition.IsEfiPartition = true;
        }

        // Check for MSR partition (usually 16MB or 128MB, no filesystem, no drive letter)
        if ((partition.SizeBytes == 16 * 1024 * 1024 ||
             partition.SizeBytes == 128 * 1024 * 1024) &&
            string.IsNullOrEmpty(partition.FileSystemType) &&
            partition.DriveLetter == null)
        {
            partition.IsMsrPartition = true;
        }

        // Check for recovery partition (common sizes: 450MB, 500MB, 1GB, etc.)
        if ((partition.SizeBytes == 450 * 1024 * 1024 ||
             partition.SizeBytes == 500 * 1024 * 1024 ||
             partition.SizeBytes == 1024 * 1024 * 1024) &&
            !partition.IsEfiPartition &&
            !partition.IsSystemPartition &&
            !partition.IsMsrPartition)
        {
            partition.IsRecoveryPartition = true;
        }
    }

    /// <summary>
    /// Gets the system disk number using WMI.
    /// </summary>
    private async Task<int> GetSystemDiskNumberAsync()
    {
        try
        {
            return await Task.Run(() =>
            {
                // Method 1: Use Win32_DiskDrive directly by checking for the boot partition
                // This is often more reliable than complex joins.
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_DiskDrive WHERE Index = 0");
                    using var results = searcher.Get();
                    
                    foreach (ManagementObject disk in results)
                    {
                        var capabilities = SafeGetProperty(disk, "Capabilities") as ushort[];
                        if (capabilities != null && capabilities.Contains((ushort)4)) // 4 = Supports Boot
                        {
                            return 0; 
                        }
                    }
                }
                catch { }

                // Method 2: Query operating system for SystemDrive
                try
                {
                    using var osSearcher = new ManagementObjectSearcher("SELECT SystemDrive FROM Win32_OperatingSystem");
                    foreach (ManagementObject os in osSearcher.Get())
                    {
                        var systemDrive = os["SystemDrive"]?.ToString() ?? "C:";
                        // Finding which disk owns "C:" is non-trivial in WMI without a lot of queries.
                        // We'll trust the first disk found with partitions as a fallback if disk 0 fails.
                    }
                }
                catch { }

                return 0; // Default to disk 0 as it's almost always the system disk
            });
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to get system disk number: {ex.Message}, defaulting to 0");
            return 0;
        }
    }

    /// <summary>
    /// Validates that a disk exists and is accessible.
    /// </summary>
    public async Task<bool> ValidateDiskAccessAsync(int diskNumber)
    {
        _logger.Info($"Validating access to disk {diskNumber}");

        try
        {
            var path = $@"\\.\PhysicalDrive{diskNumber}";
            var result = await Task.Run<bool>(() =>
            {
                using var handle = WindowsApi.CreateFile(
                    path,
                    WindowsApi.GENERIC_READ,
                    WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    WindowsApi.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    var error = WindowsApi.GetLastError();
                    _logger.Error($"Failed to open disk {diskNumber}: {WindowsApi.GetErrorMessage(error)}");
                    return false;
                }

                // Try to get geometry
                var size = Marshal.SizeOf<WindowsApi.DISK_GEOMETRY>();
                var buffer = Marshal.AllocHGlobal(size);

                try
                {
                    uint bytesReturned;
                    bool result = WindowsApi.DeviceIoControl(
                        handle,
                        WindowsApi.IOCTL_DISK_GET_DRIVE_GEOMETRY,
                        IntPtr.Zero,
                        0,
                        buffer,
                        size,
                        out bytesReturned,
                        IntPtr.Zero);

                    if (!result)
                    {
                        var error = WindowsApi.GetLastError();
                        _logger.Error($"Failed to get disk geometry: {WindowsApi.GetErrorMessage(error)}");
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                return true;
            });

            if (result)
                _logger.Info($"Disk {diskNumber} is accessible");

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error($"Exception validating disk {diskNumber}", ex);
            return false;
        }
    }
}
