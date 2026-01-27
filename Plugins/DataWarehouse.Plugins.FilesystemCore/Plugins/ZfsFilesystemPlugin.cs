// <copyright file="ZfsFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for ZFS (Zettabyte File System).
/// </summary>
/// <remarks>
/// ZFS capabilities include:
/// <list type="bullet">
/// <item>Hard links and symbolic links</item>
/// <item>Extended attributes (xattr)</item>
/// <item>POSIX ACLs</item>
/// <item>Transparent compression (lz4, gzip, zstd)</item>
/// <item>Block-level deduplication</item>
/// <item>Snapshots and clones</item>
/// <item>Copy-on-write</item>
/// <item>End-to-end data integrity checksums (Fletcher, SHA256)</item>
/// <item>Native encryption (AES-GCM, AES-CCM)</item>
/// <item>Quotas and reservations</item>
/// <item>Software RAID (mirrors, RAID-Z, RAID-Z2, RAID-Z3)</item>
/// <item>Self-healing with scrub</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
[SupportedOSPlatform("openbsd")]
public sealed partial class ZfsFilesystemPlugin : FilesystemPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.zfs";

    /// <inheritdoc/>
    public override string Name => "ZFS Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "zfs";

    /// <inheritdoc/>
    public override int DetectionPriority => 300; // Higher than Btrfs and ext4

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities =>
        FilesystemCapabilities.HardLinks |
        FilesystemCapabilities.SymbolicLinks |
        FilesystemCapabilities.ExtendedAttributes |
        FilesystemCapabilities.ACLs |
        FilesystemCapabilities.Encryption |
        FilesystemCapabilities.Compression |
        FilesystemCapabilities.Deduplication |
        FilesystemCapabilities.Snapshots |
        FilesystemCapabilities.Quotas |
        FilesystemCapabilities.CopyOnWrite |
        FilesystemCapabilities.Checksums |
        FilesystemCapabilities.SparseFiles |
        FilesystemCapabilities.CaseSensitive |
        FilesystemCapabilities.Unicode |
        FilesystemCapabilities.LargeFiles |
        FilesystemCapabilities.ChangeNotifications |
        FilesystemCapabilities.AdvisoryLocking |
        FilesystemCapabilities.AtomicRename |
        FilesystemCapabilities.MemoryMappedFiles |
        FilesystemCapabilities.AsyncIO;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        if (!IsLinux && !RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            return false;

        try
        {
            // Check if zfs command is available
            if (!await IsCommandAvailableAsync("zfs", ct))
                return false;

            var fsType = await GetFilesystemTypeAsync(path, ct);
            return string.Equals(fsType, "zfs", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public override async Task<FilesystemStats> GetStatsAsync(string path, CancellationToken ct = default)
    {
        var normalizedPath = NormalizePath(path);

        var stats = new FilesystemStats
        {
            FilesystemType = "zfs",
            DevicePath = normalizedPath,
            CollectedAt = DateTime.UtcNow,
            MaxFileNameLength = 255,
            MaxPathLength = 4096
        };

        try
        {
            // Get dataset name for this path
            var dataset = await GetDatasetForPathAsync(normalizedPath, ct);
            if (string.IsNullOrEmpty(dataset))
            {
                // Fallback to df
                await FillStatsFromDfAsync(stats, normalizedPath, ct);
                return stats;
            }

            // Get space information
            var output = await RunCommandAsync("zfs", $"list -H -p -o used,avail,refer,logicalused \"{dataset}\"", ct);
            var parts = WhitespaceRegex().Split(output.Trim());

            if (parts.Length >= 4)
            {
                stats.UsedBytes = long.TryParse(parts[0], out var used) ? used : 0;
                stats.AvailableBytes = long.TryParse(parts[1], out var avail) ? avail : 0;
                stats.TotalSizeBytes = stats.UsedBytes + stats.AvailableBytes;
            }

            // Get dataset properties
            var propsOutput = await RunCommandAsync("zfs", $"get -H -p -o value compression,dedup,encryption,quota \"{dataset}\"", ct);
            var propLines = propsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (propLines.Length >= 1)
                stats.ExtendedStats["compression"] = propLines[0].Trim();
            if (propLines.Length >= 2)
                stats.ExtendedStats["deduplication"] = propLines[1].Trim();
            if (propLines.Length >= 3)
                stats.ExtendedStats["encryption"] = propLines[2].Trim();
            if (propLines.Length >= 4)
                stats.ExtendedStats["quota"] = propLines[3].Trim();

            // Get pool name and properties
            var poolName = dataset.Split('/')[0];
            var poolOutput = await RunCommandAsync("zpool", $"get -H -p -o value health,guid \"{poolName}\"", ct);
            var poolLines = poolOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (poolLines.Length >= 1)
                stats.ExtendedStats["pool_health"] = poolLines[0].Trim();
            if (poolLines.Length >= 2 && ulong.TryParse(poolLines[1].Trim(), out var guid))
                stats.FilesystemId = new Guid((int)(guid >> 32), (short)(guid >> 16), (short)guid, 0, 0, 0, 0, 0, 0, 0, 0);

            stats.VolumeLabel = dataset;
            stats.BlockSize = 131072; // Default ZFS recordsize (128KB)

            // ZFS doesn't use traditional inodes
            stats.TotalInodes = 0;
            stats.FreeInodes = 0;
            stats.UsedInodes = 0;

            // Check if mounted read-only
            var mountOutput = await RunCommandAsync("zfs", $"get -H -p -o value readonly \"{dataset}\"", ct);
            stats.IsReadOnly = mountOutput.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            stats.ExtendedStats["error"] = ex.Message;
            await FillStatsFromDfAsync(stats, normalizedPath, ct);
        }

        return stats;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        if (!IsLinux && !RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            return FilesystemCapabilities.None;

        if (!await IsCommandAvailableAsync("zfs", ct))
            return FilesystemCapabilities.None;

        var fsType = await GetFilesystemTypeAsync(path, ct);
        if (!string.Equals(fsType, "zfs", StringComparison.OrdinalIgnoreCase))
            return FilesystemCapabilities.None;

        // Return all standard ZFS capabilities
        return Capabilities;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemHealth> CheckHealthAsync(string path, CancellationToken ct = default)
    {
        var health = new FilesystemHealth
        {
            Status = FilesystemHealthStatus.Healthy,
            CheckedAt = DateTime.UtcNow
        };

        var startTime = DateTime.UtcNow;

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
                    Remediation = "Free up disk space, destroy old snapshots, or add more storage to the pool"
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

            // Get dataset for health checks
            var dataset = await GetDatasetForPathAsync(path, ct);
            if (!string.IsNullOrEmpty(dataset))
            {
                var poolName = dataset.Split('/')[0];

                // Check pool health
                var poolHealth = await RunCommandAsync("zpool", $"get -H -o value health \"{poolName}\"", ct);
                var healthStatus = poolHealth.Trim();

                if (!healthStatus.Equals("ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    var severity = healthStatus.Equals("DEGRADED", StringComparison.OrdinalIgnoreCase)
                        ? FilesystemIssueSeverity.Warning
                        : FilesystemIssueSeverity.Critical;

                    health.Status = severity == FilesystemIssueSeverity.Critical
                        ? FilesystemHealthStatus.Critical
                        : FilesystemHealthStatus.Warning;

                    health.Issues.Add(new FilesystemIssue
                    {
                        Severity = severity,
                        Code = "POOL_UNHEALTHY",
                        Message = $"ZFS pool '{poolName}' status: {healthStatus}",
                        Remediation = "Run 'zpool status' to diagnose issues and repair/replace failed devices"
                    });
                }

                // Check for pool errors
                var statusOutput = await RunCommandAsync("zpool", $"status \"{poolName}\"", ct);
                if (statusOutput.Contains("errors:") && !statusOutput.Contains("errors: No known data errors"))
                {
                    health.Status = FilesystemHealthStatus.Critical;
                    health.Issues.Add(new FilesystemIssue
                    {
                        Severity = FilesystemIssueSeverity.Critical,
                        Code = "DATA_ERRORS",
                        Message = "Data errors detected in ZFS pool",
                        Remediation = "Run 'zpool scrub' to repair errors and check disk health"
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

        health.CheckDuration = DateTime.UtcNow - startTime;
        return health;
    }

    /// <inheritdoc/>
    protected override Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct)
    {
        if (!IsLinux && !RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            throw new PlatformNotSupportedException("ZFS plugin only works on Linux and BSD systems.");

        var handle = new ZfsFilesystemHandle(path, options, Capabilities);
        return Task.FromResult<IFilesystemHandle>(handle);
    }

    #region Helper Methods

    private static async Task<string> GetFilesystemTypeAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("df", $"-T \"{path}\"", ct);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(lines[1]);
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

    private static async Task<string> GetDatasetForPathAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("df", $"\"{path}\"", ct);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && parts[0].Contains('/'))
                    return parts[0];
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task FillStatsFromDfAsync(FilesystemStats stats, string path, CancellationToken ct)
    {
        try
        {
            var dfOutput = await RunCommandAsync("df", $"-B1 \"{path}\"", ct);
            var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(lines[1]);
                if (parts.Length >= 5)
                {
                    stats.TotalSizeBytes = long.TryParse(parts[1], out var total) ? total : 0;
                    stats.UsedBytes = long.TryParse(parts[2], out var used) ? used : 0;
                    stats.AvailableBytes = long.TryParse(parts[3], out var avail) ? avail : 0;
                }
            }
        }
        catch
        {
            // Leave stats at defaults
        }
    }

    private static async Task<bool> IsCommandAvailableAsync(string command, CancellationToken ct)
    {
        try
        {
            var which = await RunCommandAsync("which", command, ct);
            return !string.IsNullOrWhiteSpace(which);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunCommandAsync(string command, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return output;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    #endregion
}

/// <summary>
/// Filesystem handle implementation for ZFS.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
[SupportedOSPlatform("openbsd")]
internal sealed class ZfsFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    public ZfsFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
    {
        MountPath = mountPath;
        Capabilities = capabilities;
        _options = options;
        IsReadOnly = options.ReadOnly;
    }

    public string MountPath { get; }
    public FilesystemCapabilities Capabilities { get; }
    public bool IsValid => _isValid;
    public bool IsReadOnly { get; }

    #region File Operations

    public Task<Stream> CreateFileAsync(string path, FileCreationOptions? options = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var mode = options?.Overwrite == true ? FileMode.Create : FileMode.CreateNew;
        var stream = new FileStream(fullPath, mode, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    public Task<Stream> ReadFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    public Task<Stream> WriteFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

    public Task<Stream> AppendFileAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.None,
            _options.BufferSize, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(stream);
    }

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

    public Task<bool> FileExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    public async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var destFullPath = GetFullPath(destinationPath);

        await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
    }

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

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult(Directory.Exists(fullPath));
    }

    #endregion

    #region Attributes

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

    public Task SetAttributesAsync(string path, FilesystemAttributes attributes, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var fullPath = GetFullPath(path);

        File.SetCreationTimeUtc(fullPath, attributes.CreatedAtUtc);
        File.SetLastWriteTimeUtc(fullPath, attributes.ModifiedAtUtc);
        File.SetLastAccessTimeUtc(fullPath, attributes.AccessedAtUtc);

        return Task.CompletedTask;
    }

    public Task<Dictionary<string, byte[]>> GetExtendedAttributesAsync(string path, CancellationToken ct = default)
    {
        // Would need to use getxattr syscall via P/Invoke
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        // Would need to use setxattr syscall via P/Invoke
        return Task.CompletedTask;
    }

    public Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        // Would need to use removexattr syscall via P/Invoke
        return Task.FromResult(false);
    }

    #endregion

    #region Locking

    public Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new ZfsFileLock(fullPath, isExclusive: true));
    }

    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new ZfsFileLock(fullPath, isExclusive: false));
    }

    #endregion

    #region Links

    public async Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var linkFullPath = GetFullPath(linkPath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ln",
                Arguments = $"\"{sourceFullPath}\" \"{linkFullPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new IOException($"Failed to create hard link: {error}");
        }
    }

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

    public Task FlushAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        return Task.CompletedTask;
    }

    public Task<FilesystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        ThrowIfInvalid();
        return Task.FromResult(new FilesystemStats
        {
            FilesystemType = "zfs",
            CollectedAt = DateTime.UtcNow
        });
    }

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
            throw new ObjectDisposedException(nameof(ZfsFilesystemHandle));
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new UnauthorizedAccessException("Filesystem is mounted read-only.");
    }

    public ValueTask DisposeAsync()
    {
        _isValid = false;
        return ValueTask.CompletedTask;
    }

    #endregion
}

/// <summary>
/// ZFS file lock implementation using flock.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("freebsd")]
[SupportedOSPlatform("openbsd")]
internal sealed class ZfsFileLock : IFileLock
{
    private FileStream? _stream;

    public ZfsFileLock(string path, bool isExclusive)
    {
        Path = path;
        IsExclusive = isExclusive;

        var access = isExclusive ? FileAccess.ReadWrite : FileAccess.Read;
        var share = isExclusive ? FileShare.None : FileShare.Read;

        _stream = new FileStream(path, FileMode.Open, access, share);
    }

    public string Path { get; }
    public bool IsExclusive { get; }
    public bool IsHeld => _stream != null;

    public Task ReleaseAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}
