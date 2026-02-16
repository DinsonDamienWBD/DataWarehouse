// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Detects when the application is running from removable/portable media (USB drives)
/// and discovers running DataWarehouse live instances on the local network.
/// </summary>
public static class PortableMediaDetector
{
    /// <summary>
    /// Determines if the current executable is running from removable media (USB drive).
    /// </summary>
    /// <returns>True if running from a removable drive, false otherwise.</returns>
    public static bool IsRunningFromRemovableMedia()
    {
        try
        {
            var exePath = AppContext.BaseDirectory;

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Removable &&
                    drive.IsReady &&
                    exePath.StartsWith(drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Fail safe -- if we can't determine drive type, assume not removable
        }

        return false;
    }

    /// <summary>
    /// Scans local ports for a running DataWarehouse live instance.
    /// </summary>
    /// <param name="portsToCheck">Ports to check (defaults to 8080, 8081, 9090).</param>
    /// <returns>URL of the first found live instance, or null if none found.</returns>
    public static string? FindLocalLiveInstance(int[]? portsToCheck = null)
    {
        portsToCheck ??= [8080, 8081, 9090];

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };

        foreach (var port in portsToCheck)
        {
            try
            {
                var url = $"http://localhost:{port}/api/v1/info";
                var response = client.GetAsync(url).GetAwaiter().GetResult();

                if (response.IsSuccessStatusCode)
                {
                    return $"http://localhost:{port}";
                }
            }
            catch (Exception)
            {
                // Port not responding or no instance -- continue scanning
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the appropriate data path based on whether running from portable media.
    /// When running from USB, data is stored alongside the executable.
    /// Otherwise, data uses the standard application data directory.
    /// </summary>
    /// <returns>The data path appropriate for the current execution context.</returns>
    public static string GetPortableDataPath()
    {
        if (IsRunningFromRemovableMedia())
        {
            return Path.Combine(AppContext.BaseDirectory, "data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DataWarehouse",
            "data");
    }

    /// <summary>
    /// Gets a temporary data path for ephemeral live instances.
    /// Data stored here is expected to be discarded on shutdown.
    /// </summary>
    /// <returns>A temporary directory path for live mode data.</returns>
    public static string GetPortableTempPath()
    {
        return Path.Combine(Path.GetTempPath(), "DataWarehouse-Live");
    }

    /// <summary>
    /// Gets all removable drives that are ready and accessible.
    /// </summary>
    /// <returns>List of removable drive information.</returns>
    public static IReadOnlyList<RemovableDriveInfo> GetRemovableDrives()
    {
        var drives = new List<RemovableDriveInfo>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    drives.Add(new RemovableDriveInfo
                    {
                        Name = drive.Name,
                        VolumeLabel = drive.VolumeLabel,
                        TotalSize = drive.TotalSize,
                        AvailableSpace = drive.AvailableFreeSpace,
                        HasDataWarehouse = Directory.Exists(
                            Path.Combine(drive.RootDirectory.FullName, "DataWarehouse")) ||
                            File.Exists(Path.Combine(drive.RootDirectory.FullName, "DataWarehouse.Launcher.exe"))
                    });
                }
            }
        }
        catch (Exception)
        {
            // Silently handle permission errors
        }

        return drives;
    }
}

/// <summary>
/// Information about a removable drive detected on the system.
/// </summary>
public sealed record RemovableDriveInfo
{
    /// <summary>Drive name (e.g., "E:\").</summary>
    public required string Name { get; init; }

    /// <summary>Volume label.</summary>
    public string? VolumeLabel { get; init; }

    /// <summary>Total size in bytes.</summary>
    public long TotalSize { get; init; }

    /// <summary>Available free space in bytes.</summary>
    public long AvailableSpace { get; init; }

    /// <summary>Whether a DataWarehouse installation was detected on this drive.</summary>
    public bool HasDataWarehouse { get; init; }
}
