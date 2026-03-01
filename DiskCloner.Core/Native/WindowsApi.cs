using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskCloner.Core.Native;

/// <summary>
/// Windows API declarations and constants for disk operations.
/// </summary>
internal static class WindowsApi
{
    #region Constants

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint GENERIC_READ_WRITE = GENERIC_READ | GENERIC_WRITE;

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public const uint OPEN_EXISTING = 3;

    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const uint FILE_DEVICE_DISK = 0x00000007;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_PARTITION_INFO = 0x00040048;
    public const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
    public const uint IOCTL_DISK_SET_PARTITION_INFO = 0x00040040;
    public const uint IOCTL_DISK_SET_PARTITION_INFO_EX = 0x00070040;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT = 0x0004004C;
    public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;
    public const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x00070070;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00050050;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x0002D108;
    public const uint IOCTL_DISK_IS_WRITABLE = 0x00000024;
    public const uint IOCTL_DISK_VERIFY = 0x00000014;
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009003F;
    public const uint FSCTL_EXTEND_VOLUME = 0x000900F0;

    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_LOCK_VIOLATION = 33;
    public const uint ERROR_SHARING_VIOLATION = 32;
    public const uint ERROR_IO_PENDING = 997;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_INVALID_PARAMETER = 87;
    public const uint ERROR_NOT_SUPPORTED = 50;
    public const uint ERROR_BAD_COMMAND = 22;
    public const uint ERROR_INVALID_DRIVE = 15;
    public const uint ERROR_GEN_FAILURE = 31;

    public const int VERBATIM_DISK_SIGNATURE_SIZE = 4;
    public const int MAX_PATH = 260;

    #endregion

    #region Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY
    {
        public long Cylinders;
        public uint MediaTyp;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_GEOMETRY_EX
    {
        public DISK_GEOMETRY Geometry;
        public long DiskSize;
        public byte Data; // Placeholder for variable length data
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PARTITION_INFORMATION
    {
        public long StartingOffset;
        public long PartitionLength;
        public uint HiddenSectors;
        public uint PartitionNumber;
        public byte PartitionType;
        public bool BootIndicator;
        public bool RecognizedPartition;
        public bool RewritePartition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PARTITION_INFORMATION_EX
    {
        public long StartingOffset;
        public long PartitionLength;
        public uint PartitionNumber;
        public uint RewritePartition;
        public byte PartitionStyle;
        public PARTITION_STYLE_UNION PartitionStyleUnion;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct PARTITION_STYLE_UNION
    {
        [FieldOffset(0)]
        public PARTITION_INFORMATION_MBR Mbr;

        [FieldOffset(0)]
        public PARTITION_INFORMATION_GPT Gpt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PARTITION_INFORMATION_MBR
    {
        public byte PartitionType;
        public bool BootIndicator;
        public bool RecognizedPartition;
        public uint HiddenSectors;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PARTITION_INFORMATION_GPT
    {
        public Guid PartitionType;
        public Guid PartitionId;
        public ulong Attributes;
        public ushort NameBuffer; // UTF-16, actually 36 chars
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVE_LAYOUT_INFORMATION
    {
        public int PartitionCount;
        public uint Signature;
        public PARTITION_INFORMATION Partitions; // Variable length array
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVE_LAYOUT_INFORMATION_EX
    {
        public int PartitionStyle;
        public int PartitionCount;
        public DRIVE_LAYOUT_INFORMATION_EX_UNION Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct DRIVE_LAYOUT_INFORMATION_EX_UNION
    {
        [FieldOffset(0)]
        public DRIVE_LAYOUT_INFORMATION_MBR Mbr;

        [FieldOffset(0)]
        public DRIVE_LAYOUT_INFORMATION_GPT Gpt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVE_LAYOUT_INFORMATION_MBR
    {
        public uint Signature;
        // PARTITION_INFORMATION_EX PartitionEntry[1]; // Variable length array
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVE_LAYOUT_INFORMATION_GPT
    {
        public Guid DiskId;
        public long StartingUsableOffset;
        public long UsableLength;
        public uint MaxPartitionCount;
        // PARTITION_INFORMATION_EX PartitionEntry[1]; // Variable length array
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_NUMBER
    {
        public int DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public uint Internal;
        public uint InternalHigh;
        public int OffsetLow;
        public int OffsetHigh;
        public IntPtr Event;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VOLUME_BITMAP_BUFFER
    {
        public long StartingLcn;
        public long BitmapSize;
        public byte Buffer; // Variable length bitmap
    }

    #endregion

    #region API Functions

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        SafeFileHandle hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    public const uint FILE_BEGIN = 0;
    public const uint FILE_CURRENT = 1;
    public const uint FILE_END = 2;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetLastError();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern uint FormatMessage(
        uint dwFlags,
        IntPtr lpSource,
        uint dwMessageId,
        uint dwLanguageId,
        StringBuilder lpBuffer,
        uint nSize,
        IntPtr Arguments);

    public const uint FORMAT_MESSAGE_FROM_SYSTEM = 0x00001000;
    public const uint FORMAT_MESSAGE_IGNORE_INSERTS = 0x00000200;

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the error message for the last Windows error.
    /// </summary>
    public static string GetLastErrorMessage()
    {
        uint error = GetLastError();
        return GetErrorMessage(error);
    }

    /// <summary>
    /// Gets the error message for a specific error code.
    /// </summary>
    public static string GetErrorMessage(uint errorCode)
    {
        StringBuilder sb = new(256);
        FormatMessage(
            FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            IntPtr.Zero,
            errorCode,
            0,
            sb,
            (uint)sb.Capacity,
            IntPtr.Zero);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Converts a Windows error code to a Win32Exception.
    /// </summary>
    public static Win32Exception GetLastException()
    {
        return new Win32Exception((int)GetLastError());
    }

    /// <summary>
    /// Gets the control code for DeviceIoControl.
    /// </summary>
    public static uint CTL_CODE(uint deviceType, uint function, uint method, uint access)
    {
        return (deviceType << 16) | (access << 14) | (function << 2) | method;
    }

    #endregion

    #region GPT Partition Type GUIDs

    public static readonly Guid EFI_SYSTEM_PART_GUID = Guid.Parse("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid MS_RESERVED_PART_GUID = Guid.Parse("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public static readonly Guid BASIC_DATA_GUID = Guid.Parse("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid MS_RECOVERY_GUID = Guid.Parse("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");

    public static readonly Guid ATTRIBUTES_GPT_BASIC = Guid.Parse("0000000000000000");
    public static readonly ulong GPT_ATTRIBUTE_PLATFORM_REQUIRED = 0x0000000000000001;
    public static readonly ulong GPT_ATTRIBUTE_HIDDEN = 0x4000000000000000;

    #endregion
}
