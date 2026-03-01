using System;
using System.Management;

try
{
    Console.WriteLine("Querying Win32_DiskDrive...");
    using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
    int count = 0;
    foreach (ManagementObject disk in searcher.Get())
    {
        count++;
        Console.WriteLine($"Found: {disk["Model"]} - {disk["Size"]} bytes");
    }
    Console.WriteLine($"Total disks found via WMI: {count}");
    
    if (count == 0)
    {
        Console.WriteLine("Warning: No disks found via Win32_DiskDrive.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error querying WMI: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
