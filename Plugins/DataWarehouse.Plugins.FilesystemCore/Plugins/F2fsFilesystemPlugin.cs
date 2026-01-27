// <copyright file="F2fsFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for F2FS (Flash-Friendly File System) - Linux.
/// </summary>
/// <remarks>
/// F2FS capabilities include:
/// <list type="bullet">
/// <item>Hard links and symbolic links</item>
/// <item>Extended attributes (xattr)</item>
/// <item>POSIX ACLs</item>
/// <item>Native encryption (fscrypt)</item>
/// <item>Transparent compression (LZO, LZ4, zstd)</item>
/// <item>Inline data for small files</item>
/// <item>Flash-optimized log-structured design</item>
/// <item>TRIM/discard support</item>
/// <item>Atomic write operations</item>
/// <item>Quotas (user and project)</item>
/// </list>
/// F2FS is specifically designed for flash storage devices (SSDs, eMMC, SD cards).
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class F2fsFilesystemPlugin : FilesystemPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.f2fs";

    /// <inheritdoc/>
    public override string Name => "F2FS Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "f2fs";

    /// <inheritdoc/>
    public override int DetectionPriority => 220; // Higher priority for flash devices

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities =>
        FilesystemCapabilities.HardLinks |
        FilesystemCapabilities.SymbolicLinks |
        FilesystemCapabilities.ExtendedAttributes |
        FilesystemCapabilities.ACLs |
        FilesystemCapabilities.Encryption |
        FilesystemCapabilities.Compression |
        FilesystemCapabilities.Quotas |
        FilesystemCapabilities.SparseFiles |
        FilesystemCapabilities.CaseSensitive |
        FilesystemCapabilities.Unicode |
        FilesystemCapabilities.LargeFiles |
        FilesystemCapabilities.ChangeNotifications |
        FilesystemCapabilities.AdvisoryLocking |
        FilesystemCapabilities.AtomicRename |
        FilesystemCapabilities.MemoryMappedFiles |
        FilesystemCapabilities.AsyncIO |
        FilesystemCapabilities.DirectIO;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        if (!IsLinux)
            return false;

        try
        {
            var fsType = await GetFilesystemTypeAsync(path, ct);
            return string.Equals(fsType, "f2fs", StringComparison.OrdinalIgnoreCase);
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
            FilesystemType = "f2fs",
            DevicePath = normalizedPath,
            CollectedAt = DateTime.UtcNow,
            MaxFileNameLength = 255,
            MaxPathLength = 4096
        };

        try
        {
            // Use df for basic space statistics
            var dfOutput = await RunCommandAsync("df", $"-B1 \"{normalizedPath}\"", ct);
            var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(lines[1]);
                if (parts.Length >= 5)
                {
                    stats.TotalSizeBytes = long.TryParse(parts[1], out var total) ? total : 0;
                    stats.UsedBytes = long.TryParse(parts[2], out var used) ? used : 0;
                    stats.AvailableBytes = long.TryParse(parts[3], out var avail) ? avail : 0;

                    if (parts.Length >= 1)
                        stats.DevicePath = parts[0];
                }
            }

            // Get inode information
            var dfInodeOutput = await RunCommandAsync("df", $"-i \"{normalizedPath}\"", ct);
            var inodeLines = dfInodeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (inodeLines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(inodeLines[1]);
                if (parts.Length >= 5)
                {
                    stats.TotalInodes = long.TryParse(parts[1], out var total) ? total : 0;
                    stats.UsedInodes = long.TryParse(parts[2], out var used) ? used : 0;
                    stats.FreeInodes = long.TryParse(parts[3], out var free) ? free : 0;
                }
            }

            // Check for F2FS-specific features using /sys/fs/f2fs
            var device = stats.DevicePath?.Replace("/dev/", string.Empty);
            if (!string.IsNullOrEmpty(device))
            {
                // Check for inline data
                var inlineDataPath = $"/sys/fs/f2fs/{device}/inline_xattr_size";
                if (File.Exists(inlineDataPath))
                {
                    var inlineData = await File.ReadAllTextAsync(inlineDataPath, ct);
                    stats.ExtendedStats["inline_data"] = inlineData.Trim();
                }

                // Check compression
                var mountOutput = await RunCommandAsync("findmnt", $"-n -o OPTIONS \"{normalizedPath}\"", ct);
                if (mountOutput.Contains("compress"))
                {
                    var compressionMatch = CompressionRegex().Match(mountOutput);
                    stats.ExtendedStats["compression"] = compressionMatch.Success
                        ? compressionMatch.Groups[1].Value
                        : "enabled";
                }
                else
                {
                    stats.ExtendedStats["compression"] = "disabled";
                }

                // Check encryption
                if (mountOutput.Contains("encrypt") || mountOutput.Contains("test_dummy_encryption"))
                {
                    stats.ExtendedStats["encryption"] = "enabled";
                }
                else
                {
                    stats.ExtendedStats["encryption"] = "disabled";
                }
            }

            stats.BlockSize = 4096; // F2FS uses 4KB blocks
            stats.IsReadOnly = false;

            // Check mount options for read-only
            var mounts = await RunCommandAsync("mount", "", ct);
            var mountLine = mounts.Split('\n').FirstOrDefault(l => l.Contains(normalizedPath));
            if (mountLine != null)
            {
                stats.IsReadOnly = mountLine.Contains("ro,") || mountLine.Contains("(ro");
            }
        }
        catch (Exception ex)
        {
            stats.ExtendedStats["error"] = ex.Message;
        }

        return stats;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        if (!IsLinux)
            return FilesystemCapabilities.None;

        var fsType = await GetFilesystemTypeAsync(path, ct);
        if (!string.Equals(fsType, "f2fs", StringComparison.OrdinalIgnoreCase))
            return FilesystemCapabilities.None;

        // Return standard F2FS capabilities
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
                    Remediation = "Free up disk space or expand the filesystem"
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

            // Check inode usage
            if (stats.InodeUsagePercent > 95)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "INODE_CRITICAL",
                    Message = $"Inode usage critically high: {stats.InodeUsagePercent:F1}% used",
                    Remediation = "Remove files or recreate filesystem with more inodes"
                });
            }
            else if (stats.InodeUsagePercent > 90)
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "INODE_LOW",
                    Message = $"Inode usage high: {stats.InodeUsagePercent:F1}% used"
                });
            }

            // Check for read-only remount
            if (stats.IsReadOnly)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "READONLY_REMOUNT",
                    Message = "Filesystem has been remounted read-only",
                    Remediation = "Run fsck.f2fs to check and repair the filesystem"
                });
            }

            // Check for F2FS-specific issues via dmesg (requires permissions)
            try
            {
                var dmesgOutput = await RunCommandAsync("dmesg", "-T", ct);
                if (dmesgOutput.Contains("F2FS-fs") && (dmesgOutput.Contains("error") || dmesgOutput.Contains("corrupt")))
                {
                    health.Status = FilesystemHealthStatus.Warning;
                    health.Issues.Add(new FilesystemIssue
                    {
                        Severity = FilesystemIssueSeverity.Warning,
                        Code = "KERNEL_ERRORS",
                        Message = "F2FS errors detected in kernel log",
                        Remediation = "Check dmesg for details and run fsck.f2fs"
                    });
                }
            }
            catch
            {
                // dmesg access denied or not available
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
        if (!IsLinux)
            throw new PlatformNotSupportedException("F2FS plugin only works on Linux.");

        var handle = new F2fsFilesystemHandle(path, options, Capabilities);
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

    [GeneratedRegex(@"compress_algorithm=([^,\s]+)")]
    private static partial Regex CompressionRegex();

    #endregion
}

/// <summary>
/// Filesystem handle implementation for F2FS.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class F2fsFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    public F2fsFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
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
        return Task.FromResult<IFileLock>(new F2fsFileLock(fullPath, isExclusive: true));
    }

    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new F2fsFileLock(fullPath, isExclusive: false));
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
            FilesystemType = "f2fs",
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
            throw new ObjectDisposedException(nameof(F2fsFilesystemHandle));
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
/// F2FS file lock implementation using flock.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class F2fsFileLock : IFileLock
{
    private FileStream? _stream;

    public F2fsFileLock(string path, bool isExclusive)
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
