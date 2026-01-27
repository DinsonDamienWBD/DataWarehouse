// <copyright file="FilesystemStats.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

namespace DataWarehouse.Plugins.FilesystemCore;

/// <summary>
/// Statistics and information about a mounted filesystem.
/// </summary>
/// <remarks>
/// This class provides detailed information about filesystem space usage,
/// inode allocation, and performance characteristics.
/// </remarks>
public sealed class FilesystemStats
{
    /// <summary>
    /// Gets or sets the total size of the filesystem in bytes.
    /// </summary>
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the available (free) space in bytes.
    /// </summary>
    public long AvailableBytes { get; set; }

    /// <summary>
    /// Gets or sets the used space in bytes.
    /// </summary>
    public long UsedBytes { get; set; }

    /// <summary>
    /// Gets or sets the reserved space (typically for root/admin) in bytes.
    /// </summary>
    public long ReservedBytes { get; set; }

    /// <summary>
    /// Gets the usage percentage (0-100).
    /// </summary>
    public double UsagePercent => TotalSizeBytes > 0
        ? (double)UsedBytes / TotalSizeBytes * 100.0
        : 0;

    /// <summary>
    /// Gets or sets the total number of inodes (file entries) available.
    /// Zero indicates inode count is not applicable (e.g., NTFS).
    /// </summary>
    public long TotalInodes { get; set; }

    /// <summary>
    /// Gets or sets the number of free inodes available.
    /// </summary>
    public long FreeInodes { get; set; }

    /// <summary>
    /// Gets or sets the number of used inodes.
    /// </summary>
    public long UsedInodes { get; set; }

    /// <summary>
    /// Gets the inode usage percentage (0-100).
    /// </summary>
    public double InodeUsagePercent => TotalInodes > 0
        ? (double)UsedInodes / TotalInodes * 100.0
        : 0;

    /// <summary>
    /// Gets or sets the filesystem block size in bytes.
    /// </summary>
    public int BlockSize { get; set; }

    /// <summary>
    /// Gets or sets the optimal I/O transfer size in bytes.
    /// </summary>
    public int OptimalTransferSize { get; set; }

    /// <summary>
    /// Gets or sets the maximum file name length in characters.
    /// </summary>
    public int MaxFileNameLength { get; set; }

    /// <summary>
    /// Gets or sets the maximum path length in characters.
    /// </summary>
    public int MaxPathLength { get; set; }

    /// <summary>
    /// Gets or sets the filesystem type name (e.g., "NTFS", "ext4", "APFS").
    /// </summary>
    public string FilesystemType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the volume label or name.
    /// </summary>
    public string? VolumeLabel { get; set; }

    /// <summary>
    /// Gets or sets the volume serial number.
    /// </summary>
    public string? VolumeSerialNumber { get; set; }

    /// <summary>
    /// Gets or sets the device path or mount point.
    /// </summary>
    public string? DevicePath { get; set; }

    /// <summary>
    /// Gets or sets whether the filesystem is mounted read-only.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Gets or sets whether the filesystem is a network filesystem.
    /// </summary>
    public bool IsNetwork { get; set; }

    /// <summary>
    /// Gets or sets whether the filesystem is removable media.
    /// </summary>
    public bool IsRemovable { get; set; }

    /// <summary>
    /// Gets or sets the filesystem UUID/GUID.
    /// </summary>
    public Guid? FilesystemId { get; set; }

    /// <summary>
    /// Gets or sets when the filesystem was last mounted.
    /// </summary>
    public DateTime? LastMounted { get; set; }

    /// <summary>
    /// Gets or sets when the filesystem was last checked for errors.
    /// </summary>
    public DateTime? LastChecked { get; set; }

    /// <summary>
    /// Gets or sets the number of times the filesystem has been mounted.
    /// </summary>
    public int MountCount { get; set; }

    /// <summary>
    /// Gets or sets whether the filesystem requires a consistency check.
    /// </summary>
    public bool NeedsCheck { get; set; }

    /// <summary>
    /// Gets or sets filesystem-specific extended statistics.
    /// </summary>
    public Dictionary<string, object> ExtendedStats { get; set; } = new();

    /// <summary>
    /// Gets or sets the timestamp when these statistics were collected.
    /// </summary>
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Returns a human-readable summary of the filesystem statistics.
    /// </summary>
    public override string ToString()
    {
        var usedFormatted = FormatBytes(UsedBytes);
        var totalFormatted = FormatBytes(TotalSizeBytes);
        var availableFormatted = FormatBytes(AvailableBytes);

        return $"{FilesystemType}: {usedFormatted} used / {totalFormatted} total ({UsagePercent:F1}%), {availableFormatted} available";
    }

    /// <summary>
    /// Formats a byte count as a human-readable string.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        var i = 0;
        double value = bytes;

        while (value >= 1024 && i < suffixes.Length - 1)
        {
            value /= 1024;
            i++;
        }

        return $"{value:F2} {suffixes[i]}";
    }
}

/// <summary>
/// Health status of a filesystem.
/// </summary>
public sealed class FilesystemHealth
{
    /// <summary>
    /// Gets or sets the overall health status.
    /// </summary>
    public FilesystemHealthStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the list of issues detected.
    /// </summary>
    public List<FilesystemIssue> Issues { get; set; } = new();

    /// <summary>
    /// Gets or sets when the health check was performed.
    /// </summary>
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the duration of the health check.
    /// </summary>
    public TimeSpan CheckDuration { get; set; }

    /// <summary>
    /// Gets whether the filesystem is healthy.
    /// </summary>
    public bool IsHealthy => Status == FilesystemHealthStatus.Healthy;
}

/// <summary>
/// Health status levels for filesystems.
/// </summary>
public enum FilesystemHealthStatus
{
    /// <summary>
    /// Filesystem is unknown or not checked.
    /// </summary>
    Unknown,

    /// <summary>
    /// Filesystem is healthy with no issues.
    /// </summary>
    Healthy,

    /// <summary>
    /// Filesystem has warnings but is operational.
    /// </summary>
    Warning,

    /// <summary>
    /// Filesystem has critical issues and may have data loss.
    /// </summary>
    Critical,

    /// <summary>
    /// Filesystem is failed and inaccessible.
    /// </summary>
    Failed
}

/// <summary>
/// Represents an issue detected on a filesystem.
/// </summary>
public sealed class FilesystemIssue
{
    /// <summary>
    /// Gets or sets the issue severity.
    /// </summary>
    public FilesystemIssueSeverity Severity { get; set; }

    /// <summary>
    /// Gets or sets the issue code.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the issue message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the affected path or object.
    /// </summary>
    public string? AffectedPath { get; set; }

    /// <summary>
    /// Gets or sets recommended remediation actions.
    /// </summary>
    public string? Remediation { get; set; }

    /// <summary>
    /// Gets or sets when the issue was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Severity levels for filesystem issues.
/// </summary>
public enum FilesystemIssueSeverity
{
    /// <summary>
    /// Informational message, no action required.
    /// </summary>
    Info,

    /// <summary>
    /// Warning that may require attention.
    /// </summary>
    Warning,

    /// <summary>
    /// Error that requires attention.
    /// </summary>
    Error,

    /// <summary>
    /// Critical issue that may cause data loss.
    /// </summary>
    Critical
}
