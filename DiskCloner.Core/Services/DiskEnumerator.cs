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
    
    private string? _systemDriveLetter;
    private string? _systemPartitionDeviceID;
    private long _systemPartitionOffset = -1;
    private int _systemDiskNumber = -1;
    private bool _isInitialized = false;

    public DiskEnumerator(ILogger? logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets all physical disks in the system.
    /// </summary>
    public async Task<List<DiskInfo>> GetDisksAsync(bool forceRefresh = false)
    {
        if (!_isInitialized)
        {
            await InitializeSystemInfoAsync();
        }

        if (!forceRefresh && DateTime.UtcNow - _lastCacheTime < _cacheDuration)
        {
            _logger.Debug("Returning cached disk information");
            return new List<DiskInfo>(_cachedDisks);
        }

        _logger.Info("Enumerating physical disks...");

        try
        {
            var disks = await QueryDisksViaWmiAsync();

            foreach (var disk in disks)
            {
                disk.IsSystemDisk = (disk.DiskNumber == _systemDiskNumber);
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
                    using (diskObj)
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
                    using (partitionObj)
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

                            string wmiType = SafeGetProperty(partitionObj, "Type")?.ToString() ?? "";

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
                            DeterminePartitionType(partition, wmiType, SafeGetProperty(partitionObj, "DeviceID")?.ToString());

                            partitions.Add(partition);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"Failed to process partition: {ex.Message}");
                        }
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

    private async Task InitializeSystemInfoAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                // 1. Get SystemDrive from Win32_OperatingSystem
                using var osSearcher = new ManagementObjectSearcher("SELECT SystemDrive FROM Win32_OperatingSystem");
                foreach (ManagementObject os in osSearcher.Get())
                {
                    _systemDriveLetter = os["SystemDrive"]?.ToString()?.Trim(':');
                    break;
                }

                if (string.IsNullOrEmpty(_systemDriveLetter)) _systemDriveLetter = "C";

                // 2. Trace back to hardware via P/Invoke (Most reliable: Offset-based)
                try
                {
                    var drivePath = $@"\\.\{_systemDriveLetter}:";
                    using var hVolume = WindowsApi.CreateFile(
                        drivePath,
                        WindowsApi.GENERIC_READ,
                        WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        WindowsApi.OPEN_EXISTING,
                        0,
                        IntPtr.Zero);

                    if (!hVolume.IsInvalid)
                    {
                        var size = Marshal.SizeOf<WindowsApi.VOLUME_DISK_EXTENTS>();
                        using var buffer = new NativeBuffer(size);

                        uint bytesReturned;
                        if (WindowsApi.DeviceIoControl(
                            hVolume,
                            WindowsApi.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                            IntPtr.Zero, 0,
                            buffer.Pointer, size,
                            out bytesReturned,
                            IntPtr.Zero))
                        {
                            var extents = Marshal.PtrToStructure<WindowsApi.VOLUME_DISK_EXTENTS>(buffer.Pointer);
                            if (extents.NumberOfDiskExtents > 0)
                            {
                                _systemPartitionOffset = extents.Extents[0].StartingOffset;
                                _systemDiskNumber = (int)extents.Extents[0].DiskNumber;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"P/Invoke system trace failed: {ex.Message}");
                }

                // 3. Fallback: Trace back via WMI associations
                if (_systemPartitionOffset == -1)
                {
                    using var ldSearcher = new ManagementObjectSearcher($"SELECT * FROM Win32_LogicalDisk WHERE DeviceID = '{_systemDriveLetter}:'");
                    foreach (ManagementObject ld in ldSearcher.Get())
                    {
                        using (ld)
                        {
                            foreach (ManagementObject partition in ld.GetRelated("Win32_DiskPartition"))
                            {
                                using (partition)
                                {
                                    _systemPartitionDeviceID = SafeGetProperty(partition, "DeviceID")?.ToString();
                                    _systemPartitionOffset = GetInt64OrDefault(SafeGetProperty(partition, "StartingOffset"), -1);
                                    _systemDiskNumber = GetInt32OrDefault(SafeGetProperty(partition, "DiskIndex"), -1);
                                    break;
                                }
                            }
                        }
                    }
                }

                // Fallback for disk number if tracing failed
                if (_systemDiskNumber == -1) _systemDiskNumber = 0;
                
                _isInitialized = true;
                _logger.Info($"System detected: Drive {_systemDriveLetter}: at Offset {_systemPartitionOffset} (Disk {_systemDiskNumber})");
            });
        }
        catch (Exception ex)
        {
            _logger.Warning($"System info initialization failed: {ex.Message}");
            _systemDiskNumber = 0;
            _isInitialized = true;
        }
    }

    /// <summary>
    /// Gets logical disk information for a partition.
    /// </summary>
    private List<dynamic> GetLogicalDisksForPartition(int diskNumber, int partitionIndex)
    {
        var logicalDisks = new List<dynamic>();

        try
        {
            // Use Win32_DiskPartition.DeviceID to get related logical disks
            string deviceId = $"Disk #{diskNumber}, Partition #{partitionIndex}";
            using var partition = new ManagementObject($"Win32_DiskPartition.DeviceID='{deviceId}'");
            
            foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
            {
                using (logical)
                {
                    var driveLetterStr = SafeGetProperty(logical, "DeviceID")?.ToString()?.Trim(':') ?? "";
                    if (driveLetterStr.Length == 1)
                    {
                        var volumeInfo = GetVolumeInfo(driveLetterStr[0]);
                        logicalDisks.Add(volumeInfo);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to query logical disks via related: {ex.Message}. Falling back to list scan.");
            // Fallback to the slower scan if the specific object doesn't exist (e.g. invalid DeviceID)
            return GetLogicalDisksForPartitionFallback(diskNumber, partitionIndex);
        }

        return logicalDisks;
    }

    private List<dynamic> GetLogicalDisksForPartitionFallback(int diskNumber, int partitionIndex)
    {
        var logicalDisks = new List<dynamic>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_LogicalDiskToPartition");
            using var results = searcher.Get();

            foreach (ManagementObject link in results)
            {
                using (link)
                {
                    var antecedent = SafeGetProperty(link, "Antecedent")?.ToString() ?? "";
                    var dependent = SafeGetProperty(link, "Dependent")?.ToString() ?? "";

                    if (antecedent.Contains($"Disk #{diskNumber}") &&
                        antecedent.Contains($"Partition #{partitionIndex}"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(dependent, @"Win32_LogicalDisk\.DeviceID\s*=\s*""([A-Z]):""");
                        if (match.Success)
                        {
                            var driveLetter = match.Groups[1].Value[0];
                            logicalDisks.Add(GetVolumeInfo(driveLetter));
                        }
                    }
                }
            }
        }
        catch { }
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
                using (disk)
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
            // Prefer DeviceIoControl for an authoritative partition style when possible
            var indexObj = diskObj["Index"];
            if (indexObj != null && int.TryParse(indexObj.ToString(), out var diskIndex))
            {
                if (TryGetPartitionStyle(diskIndex, out var isGpt))
                {
                    return isGpt;
                }
            }

            // Fallback: inspect signature (MBR usually has a non-zero signature)
            var signature = diskObj["Signature"]?.ToString();
            if (!string.IsNullOrEmpty(signature))
            {
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

    private bool TryGetPartitionStyle(int diskNumber, out bool isGpt)
    {
        isGpt = false;
        try
        {
            var path = $"\\.\\PhysicalDrive{diskNumber}";
            using var handle = WindowsApi.CreateFile(path, WindowsApi.GENERIC_READ, WindowsApi.FILE_SHARE_READ | WindowsApi.FILE_SHARE_WRITE, IntPtr.Zero, WindowsApi.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return false;

            const int outSize = 4096;
            using var outBuffer = new NativeBuffer(outSize);

            if (WindowsApi.DeviceIoControl(handle, WindowsApi.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, IntPtr.Zero, 0, outBuffer.Pointer, outSize, out uint bytesReturned, IntPtr.Zero))
            {
                // DRIVE_LAYOUT_INFORMATION_EX.PartitionStyle is the first int
                var partitionStyle = Marshal.ReadInt32(outBuffer.Pointer);
                // 0 = MBR, 1 = GPT
                isGpt = partitionStyle == 1;
                return true;
            }
        }
        catch
        {
            // best-effort only
        }

        return false;
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
    private void DeterminePartitionType(PartitionInfo partition, string wmiType, string? deviceId)
    {
        // 1. Check if this is exactly the Windows system partition (traced via Offset or DeviceID)
        if ((_systemPartitionOffset != -1 && partition.StartingOffset == _systemPartitionOffset) ||
            (!string.IsNullOrEmpty(_systemPartitionDeviceID) && deviceId == _systemPartitionDeviceID))
        {
            partition.IsSystemPartition = true;
        }

        // 2. Explicit WMI Type check (GPT-specific types from Win32_DiskPartition.Type)
        if (wmiType.Contains("System", StringComparison.OrdinalIgnoreCase) || 
            wmiType.Contains("EFI", StringComparison.OrdinalIgnoreCase))
        {
            partition.IsEfiPartition = true;
        }
        else if (wmiType.Contains("Reserved", StringComparison.OrdinalIgnoreCase) || 
                 wmiType.Contains("MSR", StringComparison.OrdinalIgnoreCase))
        {
            partition.IsMsrPartition = true;
        }
        else if (wmiType.Contains("Recovery", StringComparison.OrdinalIgnoreCase))
        {
            partition.IsRecoveryPartition = true;
        }
        
        // 3. Fallback check for drive letter (typically C:)
        if (!partition.IsSystemPartition && partition.DriveLetter.HasValue)
        {
            var driveLetter = char.ToUpperInvariant(partition.DriveLetter.Value);
            if (driveLetter == 'C' ||
                (!string.IsNullOrWhiteSpace(_systemDriveLetter) &&
                 string.Equals(driveLetter.ToString(), _systemDriveLetter, StringComparison.OrdinalIgnoreCase)))
            {
                partition.IsSystemPartition = true;
            }
        }

        // 3. Fallback heuristics if WMI type is generic ("GPT: Basic Data" or "Unknown")
        if (!partition.IsEfiPartition && !partition.IsMsrPartition && !partition.IsRecoveryPartition && !partition.IsSystemPartition)
        {
            // Likely EFI (around 100MB, often FAT32 or RAW, near start of disk)
            if (partition.SizeBytes >= 90 * 1024 * 1024 &&
                partition.SizeBytes <= 600 * 1024 * 1024 &&
                (partition.DriveLetter == null) &&
                partition.StartingOffset < 1024 * 1024 * 1024) // Within first 1GB
            {
                // If index is 0 or 1, very likely EFI
                partition.IsEfiPartition = true;
            }
            // Likely MSR (16MB or 128MB, no filesystem, no drive letter)
            else if ((partition.SizeBytes == 16 * 1024 * 1024 || partition.SizeBytes == 128 * 1024 * 1024) &&
                     string.IsNullOrEmpty(partition.FileSystemType) &&
                     partition.DriveLetter == null)
            {
                partition.IsMsrPartition = true;
            }
            // Likely Recovery (400MB-2GB, no drive letter)
            else if (partition.SizeBytes >= 400 * 1024 * 1024 && 
                     partition.SizeBytes <= 2 * 1024L * 1024 * 1024 &&
                     partition.DriveLetter == null)
            {
                partition.IsRecoveryPartition = true;
            }
        }
    }

    /// <summary>
    /// Gets the system disk number using WMI.
    /// </summary>
    [Obsolete("Use _systemDiskNumber initialized by InitializeSystemInfoAsync")]
    private async Task<int> GetSystemDiskNumberAsync()
    {
        if (!_isInitialized) await InitializeSystemInfoAsync();
        return _systemDiskNumber;
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
                using var buffer = new NativeBuffer(size);

                uint bytesReturned;
                bool result = WindowsApi.DeviceIoControl(
                    handle,
                    WindowsApi.IOCTL_DISK_GET_DRIVE_GEOMETRY,
                    IntPtr.Zero,
                    0,
                    buffer.Pointer,
                    size,
                    out bytesReturned,
                    IntPtr.Zero);

                if (!result)
                {
                    var error = WindowsApi.GetLastError();
                    _logger.Error($"Failed to get disk geometry: {WindowsApi.GetErrorMessage(error)}");
                    return false;
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
