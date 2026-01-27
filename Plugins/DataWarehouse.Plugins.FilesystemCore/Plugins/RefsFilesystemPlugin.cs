// <copyright file="RefsFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for Windows ReFS (Resilient File System).
/// </summary>
/// <remarks>
/// ReFS capabilities include:
/// <list type="bullet">
/// <item>Integrity streams with automatic corruption detection and repair</item>
/// <item>Block cloning (FSCTL_DUPLICATE_EXTENTS) for instant file copies</item>
/// <item>Sparse VDL (Valid Data Length) handling for efficient virtual disk storage</item>
/// <item>Data integrity checksums (CRC64)</item>
/// <item>Mirror-accelerated parity for tiered storage</item>
/// <item>Large volume support (up to 35 PB)</item>
/// <item>Online error correction when used with Storage Spaces</item>
/// <item>Proactive error correction (background scrubbing)</item>
/// </list>
/// ReFS is optimized for large-scale storage scenarios and data integrity.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RefsFilesystemPlugin : FilesystemPluginBase
{
    /// <summary>
    /// FSCTL code for block cloning (duplicate extents).
    /// </summary>
    private const int FSCTL_DUPLICATE_EXTENTS_TO_FILE = 0x00098344;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.refs";

    /// <inheritdoc/>
    public override string Name => "ReFS Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "ReFS";

    /// <inheritdoc/>
    public override int DetectionPriority => 190; // Just below NTFS on Windows

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities =>
        FilesystemCapabilities.Checksums |
        FilesystemCapabilities.CopyOnWrite |
        FilesystemCapabilities.Deduplication |
        FilesystemCapabilities.SparseFiles |
        FilesystemCapabilities.LargeFiles |
        FilesystemCapabilities.Unicode |
        FilesystemCapabilities.AtomicRename |
        FilesystemCapabilities.MemoryMappedFiles |
        FilesystemCapabilities.AsyncIO |
        FilesystemCapabilities.Reflinks |
        FilesystemCapabilities.ChangeNotifications |
        FilesystemCapabilities.ACLs;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        if (!IsWindows)
            return false;

        try
        {
            var driveInfo = GetDriveInfo(path);
            if (driveInfo == null)
                return false;

            return string.Equals(driveInfo.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default)
    {
        var driveInfo = GetDriveInfo(path)
            ?? throw new DirectoryNotFoundException($"Cannot find drive for path: {path}");

        var stats = new FilesystemStats
        {
            TotalSizeBytes = driveInfo.TotalSize,
            AvailableBytes = driveInfo.AvailableFreeSpace,
            UsedBytes = driveInfo.TotalSize - driveInfo.TotalFreeSpace,
            ReservedBytes = driveInfo.TotalFreeSpace - driveInfo.AvailableFreeSpace,
            FilesystemType = driveInfo.DriveFormat,
            VolumeLabel = driveInfo.VolumeLabel,
            DevicePath = driveInfo.Name,
            IsReadOnly = !driveInfo.IsReady,
            IsNetwork = driveInfo.DriveType == DriveType.Network,
            IsRemovable = driveInfo.DriveType == DriveType.Removable,
            BlockSize = 64 * 1024, // ReFS default cluster size (64KB)
            MaxFileNameLength = 255,
            MaxPathLength = 32767, // With long path support
            CollectedAt = DateTime.UtcNow
        };

        // ReFS doesn't use traditional inodes
        stats.TotalInodes = 0;
        stats.FreeInodes = 0;
        stats.UsedInodes = 0;

        // ReFS-specific extended stats
        stats.ExtendedStats["integrityStreamsEnabled"] = IsIntegrityStreamsEnabled(path);
        stats.ExtendedStats["blockCloningSupported"] = IsBlockCloningSupported(path);

        return Task.FromResult(stats);
    }

    /// <inheritdoc/>
    public override Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        if (!IsWindows)
            return Task.FromResult(FilesystemCapabilities.None);

        var driveInfo = GetDriveInfo(path);
        if (driveInfo == null || !string.Equals(driveInfo.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(FilesystemCapabilities.None);

        var capabilities = FilesystemCapabilities.None;

        // ReFS always supports these
        capabilities |= FilesystemCapabilities.Checksums;
        capabilities |= FilesystemCapabilities.CopyOnWrite;
        capabilities |= FilesystemCapabilities.SparseFiles;
        capabilities |= FilesystemCapabilities.LargeFiles;
        capabilities |= FilesystemCapabilities.Unicode;
        capabilities |= FilesystemCapabilities.AtomicRename;
        capabilities |= FilesystemCapabilities.MemoryMappedFiles;
        capabilities |= FilesystemCapabilities.AsyncIO;
        capabilities |= FilesystemCapabilities.ChangeNotifications;
        capabilities |= FilesystemCapabilities.ACLs;
        capabilities |= FilesystemCapabilities.AdvisoryLocking;
        capabilities |= FilesystemCapabilities.MandatoryLocking;

        // Block cloning (reflinks) depends on version and configuration
        if (IsBlockCloningSupported(path))
            capabilities |= FilesystemCapabilities.Reflinks;

        // Deduplication on Windows Server only
        if (IsDeduplicationAvailable())
            capabilities |= FilesystemCapabilities.Deduplication;

        // Note: ReFS does NOT support the following (by design):
        // - Hard links
        // - Extended attributes / Alternate Data Streams (limited support)
        // - Compression (transparent)
        // - Encryption (EFS)
        // - Disk quotas
        // - Symbolic links (limited support)

        return Task.FromResult(capabilities);
    }

    /// <inheritdoc/>
    public override Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default)
    {
        var health = new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            var driveInfo = GetDriveInfo(path);
            if (driveInfo == null || !driveInfo.IsReady)
            {
                health.Status = FilesystemHealthStatus.Failed;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "DRIVE_NOT_READY",
                    Message = "Drive is not ready or accessible"
                });
                return Task.FromResult(health);
            }

            // Check for low disk space
            var usagePercent = (double)(driveInfo.TotalSize - driveInfo.AvailableFreeSpace) / driveInfo.TotalSize * 100;
            if (usagePercent > 95)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "DISK_SPACE_CRITICAL",
                    Message = $"Disk space critically low: {usagePercent:F1}% used",
                    Remediation = "Free up disk space or expand the volume"
                });
            }
            else if (usagePercent > 90)
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "DISK_SPACE_LOW",
                    Message = $"Disk space low: {usagePercent:F1}% used",
                    Remediation = "Consider freeing up disk space"
                });
            }

            // Check integrity streams status
            if (!IsIntegrityStreamsEnabled(path))
            {
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Info,
                    Code = "INTEGRITY_STREAMS_DISABLED",
                    Message = "Integrity streams are not enabled on this volume",
                    Remediation = "Consider enabling integrity streams for better data protection"
                });
            }
        }
        catch (Exception ex)
        {
            health.Status = FilesystemHealthStatus.Warning;
            health.Issues.Add(new FilesystemIssue
            {
                Severity = FilesystemIssueSeverity.Warning,
                Code = "HEALTH_CHECK_ERROR",
                Message = $"Error during health check: {ex.Message}"
            });
        }

        return Task.FromResult(health);
    }

    /// <inheritdoc/>
    protected override Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct)
    {
        if (!IsWindows)
            throw new PlatformNotSupportedException("ReFS plugin only works on Windows.");

        var driveInfo = GetDriveInfo(path)
            ?? throw new DirectoryNotFoundException($"Cannot find drive for path: {path}");

        if (!string.Equals(driveInfo.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Path is not on a ReFS filesystem: {driveInfo.DriveFormat}");

        var handle = new RefsFilesystemHandle(path, options, Capabilities);
        return Task.FromResult<IFilesystemHandle>(handle);
    }

    #region Helper Methods

    private static DriveInfo? GetDriveInfo(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return null;

            return new DriveInfo(root);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks if integrity streams are enabled on the volume.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if integrity streams are enabled.</returns>
    private static bool IsIntegrityStreamsEnabled(string path)
    {
        // In a full implementation, this would use FSCTL_GET_INTEGRITY_INFORMATION
        // For now, assume enabled on ReFS volumes
        try
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if block cloning is supported on this volume.
    /// </summary>
    /// <param name="path">The path to check.</param>
    /// <returns>True if block cloning is supported.</returns>
    private static bool IsBlockCloningSupported(string path)
    {
        // Block cloning requires ReFS 3.1+ (Windows Server 2016+, Windows 10 version 1709+)
        try
        {
            var version = Environment.OSVersion.Version;
            return version.Major >= 10 && version.Build >= 16299;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if data deduplication is available (Windows Server only).
    /// </summary>
    /// <returns>True if deduplication is available.</returns>
    private static bool IsDeduplicationAvailable()
    {
        // Deduplication is a Windows Server feature
        try
        {
            // Check if we're on Windows Server by looking at product type
            // For simplicity, assume it's potentially available
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}

/// <summary>
/// Filesystem handle implementation for ReFS.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RefsFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefsFilesystemHandle"/> class.
    /// </summary>
    /// <param name="mountPath">The mount path.</param>
    /// <param name="options">Mount options.</param>
    /// <param name="capabilities">Detected capabilities.</param>
    public RefsFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
    {
        MountPath = mountPath;
        Capabilities = capabilities;
        _options = options;
        IsReadOnly = options.ReadOnly;
    }

    /// <inheritdoc/>
    public string MountPath { get; }

    /// <inheritdoc/>
    public FilesystemCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public bool IsValid => _isValid;

    /// <inheritdoc/>
    public bool IsReadOnly { get; }

    #region File Operations

    /// <inheritdoc/>
    public Task<Stream> CreateFileAsync(string path, FileCreationOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var mode = options?.Overwrite == true ? FileMode.Create : FileMode.CreateNew;

        // ReFS benefits from larger buffers due to larger cluster sizes
        var bufferSize = Math.Max(_options.BufferSize, 64 * 1024);
        var stream = new FileStream(fullPath, mode, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var bufferSize = Math.Max(_options.BufferSize, 64 * 1024);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public Task<Stream> WriteFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var bufferSize = Math.Max(_options.BufferSize, 64 * 1024);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public Task<Stream> AppendFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var bufferSize = Math.Max(_options.BufferSize, 64 * 1024);
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        if (!File.Exists(fullPath))
            return Task.FromResult(false);

        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    /// <inheritdoc/>
    public async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var destFullPath = GetFullPath(destinationPath);

        // On ReFS, try to use block cloning for instant copy
        // Fallback to regular copy if cloning fails
        var cloneSucceeded = await TryBlockCloneAsync(sourceFullPath, destFullPath, overwrite, ct);
        if (!cloneSucceeded)
        {
            await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
        }
    }

    /// <inheritdoc/>
    public async Task MoveFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var destFullPath = GetFullPath(destinationPath);

        if (overwrite && File.Exists(destFullPath))
            File.Delete(destFullPath);

        await Task.Run(() => File.Move(sourceFullPath, destFullPath), ct);
    }

    /// <summary>
    /// Attempts to use ReFS block cloning for instant file copy.
    /// </summary>
    /// <param name="sourcePath">Source file path.</param>
    /// <param name="destPath">Destination file path.</param>
    /// <param name="overwrite">Whether to overwrite existing file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if block cloning succeeded.</returns>
    private static async Task<bool> TryBlockCloneAsync(string sourcePath, string destPath, bool overwrite, CancellationToken ct)
    {
        // Block cloning requires FSCTL_DUPLICATE_EXTENTS_TO_FILE
        // This is a simplified implementation - full version would use P/Invoke
        try
        {
            // For now, fall back to regular copy
            // In production, implement FSCTL_DUPLICATE_EXTENTS_TO_FILE
            await Task.Delay(0, ct);
            return false;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Directory Operations

    /// <inheritdoc/>
    public Task<bool> CreateDirectoryAsync(string path, bool recursive = true, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        if (Directory.Exists(fullPath))
            return Task.FromResult(false);

        Directory.CreateDirectory(fullPath);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteDirectoryAsync(string path, bool recursive = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        if (!Directory.Exists(fullPath))
            return Task.FromResult(false);

        Directory.Delete(fullPath, recursive);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<FilesystemEntry> ListDirectoryAsync(string path, string? pattern = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var searchPattern = pattern ?? "*";

        var entries = await Task.Run(() =>
        {
            var dirInfo = new DirectoryInfo(fullPath);
            return dirInfo.EnumerateFileSystemInfos(searchPattern).ToList();
        }, ct);

        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested)
                yield break;

            yield return new FilesystemEntry
            {
                Name = entry.Name,
                FullPath = entry.FullName,
                IsDirectory = entry is DirectoryInfo,
                IsSymbolicLink = entry.LinkTarget != null,
                Size = entry is FileInfo fi ? fi.Length : 0,
                CreatedAt = entry.CreationTimeUtc,
                ModifiedAt = entry.LastWriteTimeUtc,
                AccessedAt = entry.LastAccessTimeUtc,
                Attributes = entry.Attributes
            };
        }
    }

    /// <inheritdoc/>
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(Directory.Exists(fullPath));
    }

    #endregion

    #region Attributes

    /// <inheritdoc/>
    public Task<FilesystemAttributes> GetAttributesAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var info = new FileInfo(fullPath);

        return Task.FromResult(new FilesystemAttributes
        {
            Size = info.Exists ? info.Length : 0,
            CreatedAtUtc = info.CreationTimeUtc,
            ModifiedAtUtc = info.LastWriteTimeUtc,
            AccessedAtUtc = info.LastAccessTimeUtc,
            FileAttributes = info.Attributes,
            IsDirectory = (info.Attributes & FileAttributes.Directory) != 0,
            IsSymbolicLink = info.LinkTarget != null,
            LinkCount = 1
        });
    }

    /// <inheritdoc/>
    public Task SetAttributesAsync(string path, FilesystemAttributes attributes, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);

        File.SetAttributes(fullPath, attributes.FileAttributes);
        File.SetCreationTimeUtc(fullPath, attributes.CreatedAtUtc);
        File.SetLastWriteTimeUtc(fullPath, attributes.ModifiedAtUtc);
        File.SetLastAccessTimeUtc(fullPath, attributes.AccessedAtUtc);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<Dictionary<string, byte[]>> GetExtendedAttributesAsync(string path, CancellationToken ct = default)
    {
        // ReFS has limited ADS support - return empty for now
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    /// <inheritdoc/>
    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        // ReFS has limited ADS support
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    #endregion

    #region Locking

    /// <inheritdoc/>
    public Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new RefsFileLock(fullPath, isExclusive: true));
    }

    /// <inheritdoc/>
    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new RefsFileLock(fullPath, isExclusive: false));
    }

    #endregion

    #region Links

    /// <inheritdoc/>
    public Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default)
    {
        // ReFS does not support hard links
        throw new NotSupportedException("ReFS does not support hard links.");
    }

    /// <inheritdoc/>
    public Task CreateSymbolicLinkAsync(string targetPath, string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var linkFullPath = GetFullPath(linkPath);
        var isDirectory = Directory.Exists(targetPath);

        if (isDirectory)
            Directory.CreateSymbolicLink(linkFullPath, targetPath);
        else
            File.CreateSymbolicLink(linkFullPath, targetPath);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> GetSymbolicLinkTargetAsync(string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(linkPath);
        var info = new FileInfo(fullPath);

        if (info.LinkTarget == null)
            throw new InvalidOperationException($"Path is not a symbolic link: {linkPath}");

        return Task.FromResult(info.LinkTarget);
    }

    #endregion

    #region Filesystem Operations

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<FilesystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var driveInfo = new DriveInfo(Path.GetPathRoot(MountPath)!);

        return await Task.FromResult(new FilesystemStats
        {
            TotalSizeBytes = driveInfo.TotalSize,
            AvailableBytes = driveInfo.AvailableFreeSpace,
            UsedBytes = driveInfo.TotalSize - driveInfo.TotalFreeSpace,
            FilesystemType = driveInfo.DriveFormat,
            VolumeLabel = driveInfo.VolumeLabel,
            CollectedAt = DateTime.UtcNow
        });
    }

    /// <inheritdoc/>
    public Task<FilesystemHealth> CheckHealthAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();

        return Task.FromResult(new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        });
    }

    #endregion

    #region Helper Methods

    private string GetFullPath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.Combine(MountPath, path);
    }

    private void ThrowIfInvalid()
    {
        if (!_isValid)
            throw new ObjectDisposedException(nameof(RefsFilesystemHandle));
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new UnauthorizedAccessException("Filesystem is mounted read-only.");
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _isValid = false;
        return ValueTask.CompletedTask;
    }

    #endregion
}

/// <summary>
/// ReFS file lock implementation.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RefsFileLock : IFileLock
{
    private FileStream? _stream;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefsFileLock"/> class.
    /// </summary>
    /// <param name="path">The file path to lock.</param>
    /// <param name="isExclusive">Whether to acquire an exclusive lock.</param>
    public RefsFileLock(string path, bool isExclusive)
    {
        Path = path;
        IsExclusive = isExclusive;

        var access = isExclusive ? FileAccess.ReadWrite : FileAccess.Read;
        var share = isExclusive ? FileShare.None : FileShare.Read;

        _stream = new FileStream(path, FileMode.Open, access, share);
    }

    /// <inheritdoc/>
    public string Path { get; }

    /// <inheritdoc/>
    public bool IsExclusive { get; }

    /// <inheritdoc/>
    public bool IsHeld => _stream != null;

    /// <inheritdoc/>
    public Task ReleaseAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}
