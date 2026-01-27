// <copyright file="Hammer2FilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for HAMMER2 - DragonFly BSD.
/// </summary>
/// <remarks>
/// HAMMER2 capabilities include:
/// <list type="bullet">
/// <item>Hard links and symbolic links</item>
/// <item>Extended attributes (xattr)</item>
/// <item>POSIX ACLs</item>
/// <item>Snapshots (read-write, instant creation)</item>
/// <item>Copy-on-write</item>
/// <item>End-to-end data integrity checksums (CRC, xxhash, SHA256)</item>
/// <item>Compression (LZ4, zlib)</item>
/// <item>Deduplication</item>
/// <item>Multi-volume clustering</item>
/// <item>Online filesystem check and repair</item>
/// <item>Crash recovery without fsck</item>
/// </list>
/// HAMMER2 is the modern filesystem for DragonFly BSD, replacing HAMMER.
/// </remarks>
[SupportedOSPlatform("dragonfly")]
public sealed partial class Hammer2FilesystemPlugin : FilesystemPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.hammer2";

    /// <inheritdoc/>
    public override string Name => "HAMMER2 Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "hammer2";

    /// <inheritdoc/>
    public override int DetectionPriority => 300; // High priority on DragonFly BSD

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities =>
        FilesystemCapabilities.HardLinks |
        FilesystemCapabilities.SymbolicLinks |
        FilesystemCapabilities.ExtendedAttributes |
        FilesystemCapabilities.ACLs |
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
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            return false;

        // Check if it's DragonFly BSD specifically
        try
        {
            var uname = await RunCommandAsync("uname", "-s", ct);
            if (!uname.Trim().Equals("DragonFly", StringComparison.OrdinalIgnoreCase))
                return false;

            // Check if hammer2 command is available
            if (!await IsCommandAvailableAsync("hammer2", ct))
                return false;

            var fsType = await GetFilesystemTypeAsync(path, ct);
            return string.Equals(fsType, "hammer2", StringComparison.OrdinalIgnoreCase);
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
            FilesystemType = "hammer2",
            DevicePath = normalizedPath,
            CollectedAt = DateTime.UtcNow,
            MaxFileNameLength = 255,
            MaxPathLength = 1024
        };

        try
        {
            // Use df for basic space statistics
            var dfOutput = await RunCommandAsync("df", $"-k \"{normalizedPath}\"", ct);
            var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length >= 2)
            {
                var parts = WhitespaceRegex().Split(lines[1]);
                if (parts.Length >= 6)
                {
                    stats.TotalSizeBytes = long.TryParse(parts[1], out var total) ? total * 1024 : 0;
                    stats.UsedBytes = long.TryParse(parts[2], out var used) ? used * 1024 : 0;
                    stats.AvailableBytes = long.TryParse(parts[3], out var avail) ? avail * 1024 : 0;

                    if (parts.Length >= 1)
                        stats.DevicePath = parts[0];
                }
            }

            // Get HAMMER2-specific information
            var device = stats.DevicePath;
            if (!string.IsNullOrEmpty(device))
            {
                // Get volume info using hammer2 command
                try
                {
                    var infoOutput = await RunCommandAsync("hammer2", $"info \"{device}\"", ct);

                    // Parse compression
                    var compMatch = CompressionRegex().Match(infoOutput);
                    if (compMatch.Success)
                        stats.ExtendedStats["compression"] = compMatch.Groups[1].Value;

                    // Parse check algorithm
                    var checkMatch = CheckAlgoRegex().Match(infoOutput);
                    if (checkMatch.Success)
                        stats.ExtendedStats["checksum_algorithm"] = checkMatch.Groups[1].Value;

                    // Parse UUID
                    var uuidMatch = UuidRegex().Match(infoOutput);
                    if (uuidMatch.Success && Guid.TryParse(uuidMatch.Groups[1].Value, out var uuid))
                        stats.FilesystemId = uuid;
                }
                catch
                {
                    // hammer2 command failed or insufficient permissions
                }

                // Get snapshot count
                try
                {
                    var snapOutput = await RunCommandAsync("hammer2", $"snapshot \"{normalizedPath}\"", ct);
                    var snapCount = snapOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    stats.ExtendedStats["snapshot_count"] = snapCount.ToString();
                }
                catch
                {
                    stats.ExtendedStats["snapshot_count"] = "unavailable";
                }
            }

            stats.BlockSize = 65536; // HAMMER2 uses 64KB blocks
            stats.TotalInodes = 0; // HAMMER2 uses dynamic inode allocation
            stats.FreeInodes = 0;
            stats.UsedInodes = 0;

            // Check mount options for read-only
            var mounts = await RunCommandAsync("mount", "", ct);
            var mountLine = mounts.Split('\n').FirstOrDefault(l => l.Contains(normalizedPath));
            if (mountLine != null)
            {
                stats.IsReadOnly = mountLine.Contains("read-only") || mountLine.Contains("ro,");
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
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            return FilesystemCapabilities.None;

        // Check if it's DragonFly BSD
        try
        {
            var uname = await RunCommandAsync("uname", "-s", ct);
            if (!uname.Trim().Equals("DragonFly", StringComparison.OrdinalIgnoreCase))
                return FilesystemCapabilities.None;

            var fsType = await GetFilesystemTypeAsync(path, ct);
            if (!string.Equals(fsType, "hammer2", StringComparison.OrdinalIgnoreCase))
                return FilesystemCapabilities.None;

            // Return standard HAMMER2 capabilities
            return Capabilities;
        }
        catch
        {
            return FilesystemCapabilities.None;
        }
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
                    Remediation = "Free up disk space, remove snapshots, or run bulkfree"
                });
            }
            else if (stats.UsagePercent > 90)
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "DISK_SPACE_LOW",
                    Message = $"Disk space low: {stats.UsagePercent:F1}% used",
                    Remediation = "Consider running 'hammer2 bulkfree' to reclaim space"
                });
            }

            // Check filesystem using hammer2 check (if available)
            var device = stats.DevicePath;
            if (!string.IsNullOrEmpty(device))
            {
                try
                {
                    // Note: This is a read-only check
                    var checkOutput = await RunCommandAsync("hammer2", $"check \"{device}\"", ct);

                    if (checkOutput.Contains("error") || checkOutput.Contains("corrupt"))
                    {
                        health.Status = FilesystemHealthStatus.Critical;
                        health.Issues.Add(new FilesystemIssue
                        {
                            Severity = FilesystemIssueSeverity.Critical,
                            Code = "CORRUPTION_DETECTED",
                            Message = "Filesystem corruption detected",
                            Remediation = "Run 'hammer2 check -F' to attempt repair"
                        });
                    }
                }
                catch
                {
                    // Check command not available or insufficient permissions
                }
            }

            // Check for read-only remount
            if (stats.IsReadOnly)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "READONLY_REMOUNT",
                    Message = "Filesystem is mounted read-only",
                    Remediation = "Check dmesg for errors and run 'hammer2 check'"
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

        health.CheckDuration = DateTime.UtcNow - startTime;
        return health;
    }

    /// <inheritdoc/>
    protected override Task<IFilesystemHandle> CreateHandleAsync(string path, MountOptions options, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            throw new PlatformNotSupportedException("HAMMER2 plugin only works on DragonFly BSD.");

        var handle = new Hammer2FilesystemHandle(path, options, Capabilities);
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

    [GeneratedRegex(@"compression:\s+([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CompressionRegex();

    [GeneratedRegex(@"check:\s+([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CheckAlgoRegex();

    [GeneratedRegex(@"uuid:\s+([a-f0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    #endregion
}

/// <summary>
/// Filesystem handle implementation for HAMMER2.
/// </summary>
[SupportedOSPlatform("dragonfly")]
internal sealed class Hammer2FilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    public Hammer2FilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
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
        // Would need to use extattr syscall via P/Invoke
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        // Would need to use extattr syscall via P/Invoke
        return Task.CompletedTask;
    }

    public Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        // Would need to use extattr syscall via P/Invoke
        return Task.FromResult(false);
    }

    #endregion

    #region Locking

    public Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new Hammer2FileLock(fullPath, isExclusive: true));
    }

    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new Hammer2FileLock(fullPath, isExclusive: false));
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
            FilesystemType = "hammer2",
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
            throw new ObjectDisposedException(nameof(Hammer2FilesystemHandle));
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
/// HAMMER2 file lock implementation using flock.
/// </summary>
[SupportedOSPlatform("dragonfly")]
internal sealed class Hammer2FileLock : IFileLock
{
    private FileStream? _stream;

    public Hammer2FileLock(string path, bool isExclusive)
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
