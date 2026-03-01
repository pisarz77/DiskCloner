using DiskCloner.Core.Logging;
using DiskCloner.Core.Models;
using DiskCloner.Core.Native;
using System.Management;

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
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_DiskDrive");

            await Task.Run(() =>
            {
                foreach (ManagementObject diskObj in searcher.Get())
                {
                    try
                    {
                        var disk = new DiskInfo
                        {
                            DiskNumber = Convert.ToInt32(diskObj["Index"]),
                            FriendlyName = diskObj["Model"]?.ToString() ?? "Unknown Disk",
                            SizeBytes = Convert.ToInt64(diskObj["Size"]),
                            IsOnline = Convert.ToBoolean(diskObj["Status"]) ||
                                       diskObj["Status"]?.ToString()?.ToLowerInvariant() == "ok",
                            IsReadOnly = Convert.ToBoolean(diskObj["ReadOnly"]) ||
                                         diskObj["MediaType"]?.ToString()?.ToLowerInvariant().Contains("read only") == true
                        };

                        // Determine if removable (usually USB)
                        var mediaType = diskObj["MediaType"]?.ToString() ?? "";
                        var interfaceType = diskObj["InterfaceType"]?.ToString() ?? "";
                        disk.IsRemovable = mediaType.ToLowerInvariant().Contains("removable") ||
                                           interfaceType.ToLowerInvariant().Contains("usb");

                        disk.BusType = DetermineBusType(diskObj);

                        // Get sector sizes
                        disk.PhysicalSectorSize = Convert.ToInt32(diskObj["BytesPerSector"]);
                        disk.LogicalSectorSize = disk.PhysicalSectorSize; // Assume same unless we query further

                        // Determine partition style (GPT vs MBR)
                        disk.IsGpt = DeterminePartitionStyle(diskObj);

                        // Get detailed geometry
                        disk.TotalSectors = disk.SizeBytes / disk.PhysicalSectorSize;

                        // Query partitions for this disk
                        disk.Partitions = QueryPartitionsForDisk(disk.DiskNumber).Result;

                        disks.Add(disk);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Failed to process disk entry: {ex.Message}");
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

    /// <summary>
    /// Queries partitions for a specific disk.
    /// </summary>
    private async Task<List<PartitionInfo>> QueryPartitionsForDisk(int diskNumber)
    {
        var partitions = new List<PartitionInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_DiskPartition WHERE DiskIndex = {diskNumber}");

            await Task.Run(() =>
            {
                foreach (ManagementObject partitionObj in searcher.Get())
                {
                    try
                    {
                        var partition = new PartitionInfo
                        {
                            PartitionNumber = Convert.ToInt32(partitionObj["Index"]) + 1,
                            StartingOffset = Convert.ToInt64(partitionObj["StartingOffset"]),
                            SizeBytes = Convert.ToInt64(partitionObj["Size"]),
                            IsActive = Convert.ToBoolean(partitionObj["Bootable"] ||
                                                       partitionObj["PrimaryPartition"])
                        };

                        // Get logical disks for this partition
                        var logicalDisks = GetLogicalDisksForPartition(diskNumber, partition.PartitionNumber - 1);
                        if (logicalDisks.Any())
                        {
                            var logical = logicalDisks.First();
                            partition.DriveLetter = logical.VolumeId?.FirstOrDefault();
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

            foreach (ManagementObject link in searcher.Get())
            {
                try
                {
                    var antecedent = link["Antecedent"]?.ToString() ?? "";
                    var dependent = link["Dependent"]?.ToString() ?? "";

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

            foreach (ManagementObject disk in searcher.Get())
            {
                return new
                {
                    VolumeId = new[] { driveLetter },
                    FileSystem = disk["FileSystem"]?.ToString(),
                    VolumeName = disk["VolumeName"]?.ToString(),
                    VolumeSerialNumber = disk["VolumeSerialNumber"]?.ToString(),
                    Size = Convert.ToInt64(disk["Size"]),
                    FreeSpace = Convert.ToInt64(disk["FreeSpace"])
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
        // Check for PartitionStyle property
        var partitionStyle = diskObj["PartitionStyle"]?.ToString();
        if (!string.IsNullOrEmpty(partitionStyle))
        {
            return partitionStyle.Equals("GPT", StringComparison.OrdinalIgnoreCase);
        }

        // Check Signature for MBR
        var signature = diskObj["Signature"]?.ToString();
        if (!string.IsNullOrEmpty(signature) && signature.Length <= 8)
        {
            // MBR disks have a 4-byte signature represented as hex
            return false;
        }

        // Default to GPT for modern Windows
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
                // Method 1: Query Win32_LogicalDisk for C: and get its partition
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_LogicalDisk WHERE DeviceID = 'C:'");

                foreach (ManagementObject disk in searcher.Get())
                {
                    try
                    {
                        // Get the partition info
                        using var partitionSearcher = new ManagementObjectSearcher(
                            $"SELECT * FROM Win32_DiskPartition WHERE DeviceID = '{disk["DeviceID"]}'");

                        foreach (ManagementObject partition in partitionSearcher.Get())
                        {
                            return Convert.ToInt32(partition["DiskIndex"]);
                        }
                    }
                    catch { }
                }

                // Method 2: Query operating system directly
                using var osSearcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_OperatingSystem");

                foreach (ManagementObject os in osSearcher.Get())
                {
                    var systemDrive = os["SystemDrive"]?.ToString() ?? "C:";
                    var bootDevice = os["BootDevice"]?.ToString() ?? "";

                    // Parse boot device to get disk number
                    var match = System.Text.RegularExpressions.Regex.Match(
                        bootDevice, @"Disk #(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int diskNum))
                    {
                        return diskNum;
                    }
                }

                // Default to disk 0
                _logger.Warning("Could not determine system disk, defaulting to disk 0");
                return 0;
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

            await Task.Run(() =>
            {
                using var handle = WindowsApi.CreateFile(
                    path,
                    WindowsApi.GENERIC_READ,
                    WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    WindowsApi.OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle == WindowsApi.INVALID_HANDLE_VALUE)
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
            });

            _logger.Info($"Disk {diskNumber} is accessible");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Exception validating disk {diskNumber}", ex);
            return false;
        }
    }
}
