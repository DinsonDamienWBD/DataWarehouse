// <copyright file="ExfatFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Runtime.Versioning;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for exFAT (Extended File Allocation Table).
/// </summary>
/// <remarks>
/// exFAT is designed for flash storage and offers improvements over FAT32:
/// <list type="bullet">
/// <item>Large file support (theoretical maximum: 16 EB)</item>
/// <item>Large volume support (up to 128 PB)</item>
/// <item>No journaling (by design, for flash wear leveling)</item>
/// <item>Optimized cluster sizes for flash storage</item>
/// <item>Transaction-safe FAT updates</item>
/// <item>Free space bitmap for faster allocation</item>
/// <item>OEM configurable file system parameters</item>
/// <item>Unicode file names (up to 255 characters)</item>
/// </list>
/// exFAT is commonly used for SD cards, USB drives, and cross-platform storage.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class ExfatFilesystemPlugin : FilesystemPluginBase
{
    /// <summary>
    /// Theoretical maximum file size for exFAT (16 EB).
    /// </summary>
    public const long TheoreticalMaxFileSize = long.MaxValue;

    /// <summary>
    /// Default cluster size for exFAT (128 KB is common for flash).
    /// </summary>
    public const int DefaultClusterSize = 128 * 1024;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.exfat";

    /// <inheritdoc/>
    public override string Name => "exFAT Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "exFAT";

    /// <inheritdoc/>
    public override int DetectionPriority => 60; // Slightly higher than FAT32

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities =>
        FilesystemCapabilities.Unicode |
        FilesystemCapabilities.LargeFiles |
        FilesystemCapabilities.AtomicRename |
        FilesystemCapabilities.MemoryMappedFiles;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var fsType = await GetFilesystemTypeAsync(path, ct);
            return IsExfatType(fsType);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default)
    {
        var stats = new FilesystemStats
        {
            FilesystemType = "exFAT",
            DevicePath = NormalizePath(path),
            CollectedAt = DateTime.UtcNow,
            MaxFileNameLength = 255,
            MaxPathLength = 32767
        };

        if (IsWindows)
        {
            await GetWindowsStatsAsync(path, stats);
        }
        else
        {
            await GetUnixStatsAsync(path, stats, ct);
        }

        // exFAT-specific info
        stats.ExtendedStats["noJournaling"] = true;
        stats.ExtendedStats["flashOptimized"] = true;
        stats.ExtendedStats["supportsLargeFiles"] = true;

        // exFAT doesn't use traditional inodes
        stats.TotalInodes = 0;
        stats.FreeInodes = 0;
        stats.UsedInodes = 0;

        return stats;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        var fsType = await GetFilesystemTypeAsync(path, ct);
        if (!IsExfatType(fsType))
            return FilesystemCapabilities.None;

        var capabilities = FilesystemCapabilities.None;

        // exFAT always supports these
        capabilities |= FilesystemCapabilities.Unicode;
        capabilities |= FilesystemCapabilities.LargeFiles; // Key difference from FAT32
        capabilities |= FilesystemCapabilities.AtomicRename;
        capabilities |= FilesystemCapabilities.MemoryMappedFiles;

        // exFAT does NOT support (by design):
        // - HardLinks
        // - SymbolicLinks
        // - ExtendedAttributes
        // - ACLs
        // - Encryption
        // - Compression
        // - Deduplication
        // - Snapshots
        // - Quotas
        // - Journaling (deliberately omitted for flash)
        // - CopyOnWrite
        // - Checksums
        // - SparseFiles
        // - AlternateDataStreams
        // - CaseSensitive
        // - Reflinks

        return capabilities;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default)
    {
        var health = new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            var stats = await GetStatsAsync(path, ct);

            // Check disk space
            if (stats.UsagePercent > 95)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "DISK_SPACE_CRITICAL",
                    Message = $"Disk space critically low: {stats.UsagePercent:F1}% used",
                    Remediation = "Free up disk space"
                });
            }
            else if (stats.UsagePercent > 90)
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "DISK_SPACE_LOW",
                    Message = $"Disk space low: {stats.UsagePercent:F1}% used"
                });
            }

            // Warn about lack of journaling
            health.Issues.Add(new FilesystemIssue
            {
                Severity = FilesystemIssueSeverity.Info,
                Code = "NO_JOURNALING",
                Message = "exFAT does not use journaling - always safely eject before removal",
                Remediation = "Use 'Eject' or 'Safely Remove Hardware' before disconnecting"
            });

            // Check cluster size optimization
            if (stats.BlockSize > 0)
            {
                stats.ExtendedStats["clusterSize"] = stats.BlockSize;
                if (stats.BlockSize < 32768)
                {
                    health.Issues.Add(new FilesystemIssue
                    {
                        Severity = FilesystemIssueSeverity.Info,
                        Code = "SMALL_CLUSTER_SIZE",
                        Message = $"Cluster size ({stats.BlockSize} bytes) may not be optimal for flash storage",
                        Remediation = "Consider reformatting with larger cluster size for better flash performance"
                    });
                }
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

        return health;
    }

    /// <inheritdoc/>
    protected override Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct)
    {
        var handle = new ExfatFilesystemHandle(path, options, Capabilities);
        return Task.FromResult<IFilesystemHandle>(handle);
    }

    #region Helper Methods

    private static bool IsExfatType(string fsType)
    {
        return string.Equals(fsType, "exFAT", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fsType, "exfat", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> GetFilesystemTypeAsync(string path, CancellationToken ct)
    {
        if (IsWindows)
        {
            return GetWindowsFilesystemType(path);
        }
        else
        {
            return await GetUnixFilesystemTypeAsync(path, ct);
        }
    }

    private static string GetWindowsFilesystemType(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return string.Empty;

            var driveInfo = new DriveInfo(root);
            return driveInfo.DriveFormat;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> GetUnixFilesystemTypeAsync(string path, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "df",
                Arguments = $"-T \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return string.Empty;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                var parts = lines[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    return parts[1];
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Task GetWindowsStatsAsync(string path, FilesystemStats stats)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return Task.CompletedTask;

            var driveInfo = new DriveInfo(root);

            stats.TotalSizeBytes = driveInfo.TotalSize;
            stats.AvailableBytes = driveInfo.AvailableFreeSpace;
            stats.UsedBytes = driveInfo.TotalSize - driveInfo.TotalFreeSpace;
            stats.VolumeLabel = driveInfo.VolumeLabel;
            stats.DevicePath = driveInfo.Name;
            stats.IsReadOnly = !driveInfo.IsReady;
            stats.IsRemovable = driveInfo.DriveType == DriveType.Removable;
            stats.IsNetwork = driveInfo.DriveType == DriveType.Network;

            // exFAT typically uses larger cluster sizes
            stats.BlockSize = DefaultClusterSize;
        }
        catch
        {
            // Ignore errors
        }

        return Task.CompletedTask;
    }

    private static async Task GetUnixStatsAsync(string path, FilesystemStats stats, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "df",
                Arguments = $"-B1 \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 2)
            {
                var parts = lines[1].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    stats.TotalSizeBytes = long.TryParse(parts[1], out var total) ? total : 0;
                    stats.UsedBytes = long.TryParse(parts[2], out var used) ? used : 0;
                    stats.AvailableBytes = long.TryParse(parts[3], out var avail) ? avail : 0;
                }
            }

            stats.BlockSize = DefaultClusterSize;
        }
        catch
        {
            // Ignore errors
        }
    }

    #endregion
}

/// <summary>
/// Filesystem handle implementation for exFAT.
/// </summary>
internal sealed class ExfatFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExfatFilesystemHandle"/> class.
    /// </summary>
    /// <param name="mountPath">The mount path.</param>
    /// <param name="options">Mount options.</param>
    /// <param name="capabilities">Detected capabilities.</param>
    public ExfatFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
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

        // Use larger buffer for flash-optimized performance
        var bufferSize = Math.Max(_options.BufferSize, ExfatFilesystemPlugin.DefaultClusterSize);
        var stream = new FileStream(fullPath, mode, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    /// <inheritdoc/>
    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var bufferSize = Math.Max(_options.BufferSize, ExfatFilesystemPlugin.DefaultClusterSize);
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
        var bufferSize = Math.Max(_options.BufferSize, ExfatFilesystemPlugin.DefaultClusterSize);
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
        var bufferSize = Math.Max(_options.BufferSize, ExfatFilesystemPlugin.DefaultClusterSize);
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

        await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
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
                IsSymbolicLink = false,
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
            IsSymbolicLink = false,
            LinkCount = 1,
            UnixPermissions = 0x1FF // 0777 - exFAT has no permissions
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
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    /// <inheritdoc/>
    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
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
        return Task.FromResult<IFileLock>(new ExfatFileLock(fullPath, isExclusive: true));
    }

    /// <inheritdoc/>
    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new ExfatFileLock(fullPath, isExclusive: false));
    }

    #endregion

    #region Links

    /// <inheritdoc/>
    public Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default)
    {
        throw new NotSupportedException("exFAT does not support hard links.");
    }

    /// <inheritdoc/>
    public Task CreateSymbolicLinkAsync(string targetPath, string linkPath, CancellationToken ct = default)
    {
        throw new NotSupportedException("exFAT does not support symbolic links.");
    }

    /// <inheritdoc/>
    public Task<string> GetSymbolicLinkTargetAsync(string linkPath, CancellationToken ct = default)
    {
        throw new NotSupportedException("exFAT does not support symbolic links.");
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
    public Task<FilesystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        return Task.FromResult(new FilesystemStats
        {
            FilesystemType = "exFAT",
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
            throw new ObjectDisposedException(nameof(ExfatFilesystemHandle));
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
/// exFAT file lock implementation.
/// </summary>
internal sealed class ExfatFileLock : IFileLock
{
    private FileStream? _stream;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExfatFileLock"/> class.
    /// </summary>
    /// <param name="path">The file path to lock.</param>
    /// <param name="isExclusive">Whether to acquire an exclusive lock.</param>
    public ExfatFileLock(string path, bool isExclusive)
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
