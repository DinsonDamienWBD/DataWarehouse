// <copyright file="BtrfsFilesystemPlugin.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.FilesystemCore.Plugins;

/// <summary>
/// Filesystem plugin for Linux Btrfs (B-tree Filesystem).
/// </summary>
/// <remarks>
/// Btrfs capabilities include:
/// <list type="bullet">
/// <item>Hard links and symbolic links</item>
/// <item>Extended attributes (xattr)</item>
/// <item>POSIX ACLs</item>
/// <item>Transparent compression (zlib, lzo, zstd)</item>
/// <item>Block-level deduplication</item>
/// <item>Subvolumes and snapshots</item>
/// <item>Copy-on-write</item>
/// <item>Data and metadata checksums (CRC32C by default)</item>
/// <item>Online filesystem check and scrub</item>
/// <item>Quotas (qgroups)</item>
/// <item>Reflinks (fast file cloning)</item>
/// <item>RAID 0, 1, 5, 6, 10 support</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class BtrfsFilesystemPlugin : FilesystemPluginBase
{
    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.filesystem.btrfs";

    /// <inheritdoc/>
    public override string Name => "Btrfs Filesystem";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string FilesystemType => "btrfs";

    /// <inheritdoc/>
    public override int DetectionPriority => 250; // Higher than ext4 when Btrfs is detected

    /// <inheritdoc/>
    public override FilesystemCapabilities Capabilities => FilesystemCapabilities.BtrfsCommon;

    /// <inheritdoc/>
    public override async Task<bool> CanHandleAsync(string path, CancellationToken ct = default)
    {
        if (!IsLinux)
            return false;

        try
        {
            var fsType = await GetFilesystemTypeAsync(path, ct);
            return string.Equals(fsType, "btrfs", StringComparison.OrdinalIgnoreCase);
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
            FilesystemType = "btrfs",
            DevicePath = normalizedPath,
            CollectedAt = DateTime.UtcNow,
            MaxFileNameLength = 255,
            MaxPathLength = 4096
        };

        try
        {
            // Use btrfs fi usage for accurate space info
            var btrfsOutput = await RunCommandAsync("btrfs", $"filesystem usage -b \"{normalizedPath}\"", ct);

            // Parse total size
            var sizeMatch = TotalSizeRegex().Match(btrfsOutput);
            if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out var total))
                stats.TotalSizeBytes = total;

            // Parse used space
            var usedMatch = UsedSizeRegex().Match(btrfsOutput);
            if (usedMatch.Success && long.TryParse(usedMatch.Groups[1].Value, out var used))
                stats.UsedBytes = used;

            // Parse free space
            var freeMatch = FreeSizeRegex().Match(btrfsOutput);
            if (freeMatch.Success && long.TryParse(freeMatch.Groups[1].Value, out var free))
                stats.AvailableBytes = free;

            // If btrfs command fails, fallback to df
            if (stats.TotalSizeBytes == 0)
            {
                var dfOutput = await RunCommandAsync("df", $"-B1 \"{normalizedPath}\"", ct);
                var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length >= 2)
                {
                    var parts = WhitespaceRegex().Split(lines[1]);
                    if (parts.Length >= 5)
                    {
                        stats.TotalSizeBytes = long.TryParse(parts[1], out total) ? total : 0;
                        stats.UsedBytes = long.TryParse(parts[2], out used) ? used : 0;
                        stats.AvailableBytes = long.TryParse(parts[3], out free) ? free : 0;
                    }
                }
            }

            // Btrfs doesn't use traditional inodes
            stats.TotalInodes = 0;
            stats.FreeInodes = 0;
            stats.UsedInodes = 0;

            // Get filesystem label
            var labelOutput = await RunCommandAsync("btrfs", $"filesystem label \"{normalizedPath}\"", ct);
            if (!string.IsNullOrWhiteSpace(labelOutput))
                stats.VolumeLabel = labelOutput.Trim();

            // Get filesystem UUID
            var uuidOutput = await RunCommandAsync("btrfs", $"filesystem show \"{normalizedPath}\"", ct);
            var uuidMatch = UuidRegex().Match(uuidOutput);
            if (uuidMatch.Success)
            {
                stats.VolumeSerialNumber = uuidMatch.Groups[1].Value;
                if (Guid.TryParse(stats.VolumeSerialNumber, out var fsId))
                    stats.FilesystemId = fsId;
            }

            // Check if mounted read-only
            var mounts = await RunCommandAsync("findmnt", $"-n -o OPTIONS \"{normalizedPath}\"", ct);
            stats.IsReadOnly = mounts.Contains("ro,") || mounts.StartsWith("ro ");

            // Default Btrfs sector size
            stats.BlockSize = 4096;

            // Add Btrfs-specific extended stats
            stats.ExtendedStats["compression"] = await GetCompressionInfoAsync(normalizedPath, ct);
            stats.ExtendedStats["generation"] = await GetGenerationAsync(normalizedPath, ct);
        }
        catch (Exception ex)
        {
            stats.ExtendedStats["error"] = ex.Message;

            // Fallback to basic df
            try
            {
                var dfOutput = await RunCommandAsync("df", $"-B1 \"{normalizedPath}\"", ct);
                var lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length >= 2)
                {
                    var parts = WhitespaceRegex().Split(lines[1]);
                    if (parts.Length >= 5)
                    {
                        stats.TotalSizeBytes = long.TryParse(parts[1], out var total) ? total : 0;
                        stats.UsedBytes = long.TryParse(parts[2], out var used) ? used : 0;
                        stats.AvailableBytes = long.TryParse(parts[3], out var free) ? free : 0;
                    }
                }
            }
            catch
            {
                // Give up on stats
            }
        }

        return stats;
    }

    /// <inheritdoc/>
    public override async Task<FilesystemCapabilities> DetectCapabilitiesAsync(string path, CancellationToken ct = default)
    {
        if (!IsLinux)
            return FilesystemCapabilities.None;

        var fsType = await GetFilesystemTypeAsync(path, ct);
        if (!string.Equals(fsType, "btrfs", StringComparison.OrdinalIgnoreCase))
            return FilesystemCapabilities.None;

        var capabilities = FilesystemCapabilities.None;

        // Btrfs always supports these core features
        capabilities |= FilesystemCapabilities.HardLinks;
        capabilities |= FilesystemCapabilities.SymbolicLinks;
        capabilities |= FilesystemCapabilities.ExtendedAttributes;
        capabilities |= FilesystemCapabilities.ACLs;
        capabilities |= FilesystemCapabilities.Compression;
        capabilities |= FilesystemCapabilities.Deduplication;
        capabilities |= FilesystemCapabilities.Snapshots;
        capabilities |= FilesystemCapabilities.Quotas;
        capabilities |= FilesystemCapabilities.CopyOnWrite;
        capabilities |= FilesystemCapabilities.Checksums;
        capabilities |= FilesystemCapabilities.SparseFiles;
        capabilities |= FilesystemCapabilities.CaseSensitive;
        capabilities |= FilesystemCapabilities.Unicode;
        capabilities |= FilesystemCapabilities.LargeFiles;
        capabilities |= FilesystemCapabilities.Reflinks;
        capabilities |= FilesystemCapabilities.ChangeNotifications; // inotify
        capabilities |= FilesystemCapabilities.AdvisoryLocking;
        capabilities |= FilesystemCapabilities.AtomicRename;
        capabilities |= FilesystemCapabilities.MemoryMappedFiles;
        capabilities |= FilesystemCapabilities.AsyncIO;

        // Btrfs doesn't support direct I/O with compression
        var hasCompression = await HasCompressionEnabledAsync(path, ct);
        if (!hasCompression)
            capabilities |= FilesystemCapabilities.DirectIO;

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
                    Remediation = "Free up disk space, run btrfs balance, or add more devices"
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

            // Check device stats for errors
            var devStats = await RunCommandAsync("btrfs", $"device stats \"{path}\"", ct);
            var hasErrors = devStats.Split('\n')
                .Where(l => !l.Contains(" 0"))
                .Where(l => l.Contains("_errs") || l.Contains("_errors"))
                .Any();

            if (hasErrors)
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "DEVICE_ERRORS",
                    Message = "Device I/O errors detected",
                    Remediation = "Run 'btrfs scrub start' to verify data integrity"
                });
            }

            // Check for read-only remount
            var mounts = await RunCommandAsync("findmnt", $"-n -o OPTIONS \"{path}\"", ct);
            if (mounts.Contains("ro,") || mounts.StartsWith("ro "))
            {
                health.Status = FilesystemHealthStatus.Critical;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Critical,
                    Code = "READONLY_REMOUNT",
                    Message = "Filesystem has been remounted read-only",
                    Remediation = "Check dmesg for errors, run 'btrfs check' on unmounted filesystem"
                });
            }

            // Check scrub status
            var scrubOutput = await RunCommandAsync("btrfs", $"scrub status \"{path}\"", ct);
            if (scrubOutput.Contains("has errors"))
            {
                health.Status = FilesystemHealthStatus.Warning;
                health.Issues.Add(new FilesystemIssue
                {
                    Severity = FilesystemIssueSeverity.Warning,
                    Code = "SCRUB_ERRORS",
                    Message = "Previous scrub found errors",
                    Remediation = "Review scrub status and consider running 'btrfs check'"
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
        if (!IsLinux)
            throw new PlatformNotSupportedException("Btrfs plugin only works on Linux.");

        var handle = new BtrfsFilesystemHandle(path, options, Capabilities);
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

    private static async Task<bool> HasCompressionEnabledAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("findmnt", $"-n -o OPTIONS \"{path}\"", ct);
            return output.Contains("compress");
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> GetCompressionInfoAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("findmnt", $"-n -o OPTIONS \"{path}\"", ct);
            var compMatch = CompressionRegex().Match(output);
            if (compMatch.Success)
                return compMatch.Groups[1].Value;
            return output.Contains("compress") ? "enabled" : "disabled";
        }
        catch
        {
            return "unknown";
        }
    }

    private static async Task<string> GetGenerationAsync(string path, CancellationToken ct)
    {
        try
        {
            var output = await RunCommandAsync("btrfs", $"subvolume show \"{path}\"", ct);
            var genMatch = GenerationRegex().Match(output);
            return genMatch.Success ? genMatch.Groups[1].Value : "unknown";
        }
        catch
        {
            return "unknown";
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

    [GeneratedRegex(@"Device size:\s+(\d+)")]
    private static partial Regex TotalSizeRegex();

    [GeneratedRegex(@"Used:\s+(\d+)")]
    private static partial Regex UsedSizeRegex();

    [GeneratedRegex(@"Free \(estimated\):\s+(\d+)")]
    private static partial Regex FreeSizeRegex();

    [GeneratedRegex(@"uuid:\s+([a-f0-9-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"compress=([^,\s]+)")]
    private static partial Regex CompressionRegex();

    [GeneratedRegex(@"Generation:\s+(\d+)")]
    private static partial Regex GenerationRegex();

    #endregion
}

/// <summary>
/// Filesystem handle implementation for Btrfs.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class BtrfsFilesystemHandle : IFilesystemHandle
{
    private readonly MountOptions _options;
    private volatile bool _isValid = true;

    public BtrfsFilesystemHandle(string mountPath, MountOptions options, FilesystemCapabilities capabilities)
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

        // On Btrfs, try to use reflink for COW copy
        // Fallback to regular copy if cp --reflink fails
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cp",
                    Arguments = $"--reflink=auto {(overwrite ? "-f" : "-n")} \"{sourceFullPath}\" \"{destFullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                // Fallback to regular copy
                await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
            }
        }
        catch
        {
            // Fallback to regular copy
            await Task.Run(() => File.Copy(sourceFullPath, destFullPath, overwrite), ct);
        }
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
        return Task.FromResult(new Dictionary<string, byte[]>());
    }

    public Task SetExtendedAttributeAsync(string path, string name, byte[] value, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<bool> RemoveExtendedAttributeAsync(string path, string name, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    #endregion

    #region Locking

    public Task<IFileLock> LockFileAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new BtrfsFileLock(fullPath, isExclusive: true));
    }

    public Task<IFileLock> LockFileSharedAsync(string path, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        var fullPath = GetFullPath(path);
        return Task.FromResult<IFileLock>(new BtrfsFileLock(fullPath, isExclusive: false));
    }

    #endregion

    #region Links

    public async Task CreateHardLinkAsync(string sourcePath, string linkPath, CancellationToken ct = default)
    {
        ThrowIfInvalid();
        ThrowIfReadOnly();

        var sourceFullPath = GetFullPath(sourcePath);
        var linkFullPath = GetFullPath(linkPath);

        // Use ln command on Linux
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
            FilesystemType = "btrfs",
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
            throw new ObjectDisposedException(nameof(BtrfsFilesystemHandle));
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
/// Btrfs file lock implementation using flock.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class BtrfsFileLock : IFileLock
{
    private FileStream? _stream;

    public BtrfsFileLock(string path, bool isExclusive)
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
