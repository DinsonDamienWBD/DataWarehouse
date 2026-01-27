// <copyright file="IFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FilesystemCore;

/// <summary>
/// Main plugin interface for filesystem support.
/// </summary>
/// <remarks>
/// Filesystem plugins provide abstracted access to different filesystem types
/// (NTFS, ext4, APFS, Btrfs, etc.) with intelligent feature detection.
/// Implementations should be thread-safe and support concurrent operations.
/// </remarks>
public interface IFilesystemPlugin : IPlugin
{
    /// <summary>
    /// Gets the capabilities supported by this filesystem plugin.
    /// </summary>
    /// <remarks>
    /// These are the capabilities the plugin can detect or utilize.
    /// Actual capabilities depend on the specific filesystem being accessed.
    /// </remarks>
    FilesystemCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the filesystem type name (e.g., "NTFS", "ext4", "APFS", "Btrfs").
    /// </summary>
    string FilesystemType { get; }

    /// <summary>
    /// Gets the priority for this plugin when auto-detecting filesystems.
    /// Higher values indicate higher priority.
    /// </summary>
    int DetectionPriority { get; }

    /// <summary>
    /// Gets whether this plugin can handle the specified path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if this plugin can handle the path, false otherwise.</returns>
    Task<bool> CanHandleAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Mounts a filesystem at the specified path and returns a handle for operations.
    /// </summary>
    /// <param name="path">The path to mount (drive letter, mount point, or device path).</param>
    /// <param name="options">Mount options for configuring the filesystem handle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A filesystem handle for performing operations.</returns>
    /// <exception cref="IOException">Thrown if the filesystem cannot be mounted.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown if access is denied.</exception>
    Task<IFilesystemHandle> MountAsync(string path, MountOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Gets statistics for the filesystem at the specified path without mounting.
    /// </summary>
    /// <param name="path">The path to get statistics for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filesystem statistics.</returns>
    Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Checks if a specific feature is supported on the filesystem at the given path.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="feature">The feature to check for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the feature is supported, false otherwise.</returns>
    Task<bool> SupportsFeatureAsync(string path, FilesystemFeature feature, CancellationToken ct = default);

    /// <summary>
    /// Detects all capabilities available on the filesystem at the given path.
    /// </summary>
    /// <param name="path">The path to detect capabilities for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detected filesystem capabilities.</returns>
    Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Performs a health check on the filesystem.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health status of the filesystem.</returns>
    Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Gets detailed information about the filesystem at the given path.
    /// </summary>
    /// <param name="path">The path to get information for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detailed filesystem information.</returns>
    Task<FilesystemInfo> GetFilesystemInfoAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Detailed information about a filesystem.
/// </summary>
public sealed class FilesystemInfo
{
    /// <summary>
    /// Gets or sets the filesystem type (e.g., "NTFS", "ext4", "APFS").
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filesystem version or format version.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the device path or identifier.
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// Gets or sets the mount point.
    /// </summary>
    public string? MountPoint { get; set; }

    /// <summary>
    /// Gets or sets the volume label.
    /// </summary>
    public string? VolumeLabel { get; set; }

    /// <summary>
    /// Gets or sets the volume serial number or UUID.
    /// </summary>
    public string? VolumeId { get; set; }

    /// <summary>
    /// Gets or sets the detected capabilities.
    /// </summary>
    public FilesystemCapabilities Capabilities { get; set; }

    /// <summary>
    /// Gets or sets the filesystem statistics.
    /// </summary>
    public FilesystemStats? Stats { get; set; }

    /// <summary>
    /// Gets or sets whether the filesystem is read-only.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Gets or sets whether this is a network filesystem.
    /// </summary>
    public bool IsNetwork { get; set; }

    /// <summary>
    /// Gets or sets whether this is removable media.
    /// </summary>
    public bool IsRemovable { get; set; }

    /// <summary>
    /// Gets or sets the underlying operating system.
    /// </summary>
    public string? OperatingSystem { get; set; }

    /// <summary>
    /// Gets or sets filesystem-specific extended information.
    /// </summary>
    public Dictionary<string, object> ExtendedInfo { get; set; } = new();
}
